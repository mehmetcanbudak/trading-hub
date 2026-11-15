using System.Text.Json;
using SkiaSharp;
using TradingHub.App.Services;
using TradingHub.Charting.Dom;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

/// <summary>DOM book-reading features (defaults keep today's look) and the DOM trading switch gate.</summary>
public sealed class DomBookTests
{
    private static readonly DateTime Start = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static Instrument Inst() => new() { Symbol = "DOMFIX", FeedId = "fake", Exchange = "venue", TickSize = 0.1m, VolumeStep = 0.001m };

    // ------------------------------------------------------------------ settings

    [Fact]
    public void New_dom_settings_default_to_the_existing_look_and_old_saved_settings_gain_hidden_columns()
    {
        var d = new DomSettings();
        Assert.False(d.AutoStep);
        Assert.False(d.ShowStats);
        Assert.False(d.HoverMirror);
        Assert.False(d.AutoLargeSize);
        Assert.Equal(DomUnit.Quantity, d.Unit);
        Assert.Empty(d.Marks());
        Assert.Equal(new[] { DomColumn.BuyOrders, DomColumn.Bid, DomColumn.Price, DomColumn.Ask, DomColumn.SellOrders, DomColumn.Volume },
            d.Columns.Where(c => c.Visible).Select(c => c.Column));
        Assert.All(new[] { DomColumn.Size, DomColumn.Total, DomColumn.Changes, DomColumn.Recent },
            c => Assert.False(d.Columns.Single(x => x.Column == c).Visible));

        // a settings file from before these columns existed keeps its columns (order columns included) and gains hidden ones
        var old = new DomSettings { Columns = new() { new() { Column = DomColumn.BuyOrders }, new() { Column = DomColumn.Price }, new() { Column = DomColumn.SellOrders } } };
        old.Normalize();
        Assert.Equal(new[] { DomColumn.BuyOrders, DomColumn.Price, DomColumn.SellOrders }, old.Columns.Take(3).Select(c => c.Column));
        Assert.All(old.Columns.Take(3), c => Assert.True(c.Visible));
        Assert.All(new[] { DomColumn.Size, DomColumn.Total, DomColumn.Changes, DomColumn.Recent },
            c => Assert.False(old.Columns.Single(x => x.Column == c).Visible));

        Assert.Equal(new[] { 0.5, 1, 2.5, 5 }, new DomSettings { DistanceMarks = "5, 1;0.5 2.5% 1 -3 abc 150" }.Marks());
    }

    [Fact]
    public void Dom_trading_switch_defaults_on_persists_and_older_settings_files_keep_trading_enabled()
    {
        Assert.True(new AppSettings().DomTrading);
        var options = new JsonSerializerOptions { Converters = { new ColorJsonConverter() } };
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}", options)!.DomTrading);
        var off = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { DomTrading = false, TapeMinNotional = 100_000 }, options), options)!;
        Assert.False(off.DomTrading);
        Assert.Equal(100_000m, off.TapeMinNotional);
        var dom = JsonSerializer.Deserialize<DomSettings>("{\"AutoStep\":true,\"Unit\":1,\"DistanceMarks\":\"1\"}", options)!;
        Assert.True(dom.AutoStep);
        Assert.Equal(DomUnit.Notional, dom.Unit);
    }

    [Fact]
    public void Auto_tick_and_notional_amounts()
    {
        // ±0.5 % of 80,000 over 40 rows: 800 / 40 = 20 per row = 200 ticks of 0.1
        Assert.Equal(200, DomModel.AutoStepFor(80000, 0.1m, 40, 0.5));
        Assert.Equal(1, DomModel.AutoStepFor(0, 0.1m, 40, 0.5));
        // a shallow book (30 each side) bounds the span: 60 / 40 = 1.5 → 15 ticks → nice step 20
        Assert.Equal(20, DomModel.AutoStepFor(80000, 0.1m, 40, 0.5, 30));
        Assert.Equal("80K", DomRenderer.Amount(1, 80000, DomUnit.Notional, 3));
        Assert.Equal("1.6M", DomRenderer.Amount(20, 80000, DomUnit.Notional, 3));
        Assert.Equal("160K", DomRenderer.Amount(2, 1600, DomUnit.Notional, 0, pointValue: 50));
        Assert.Equal("-2K", DomRenderer.Amount(-1, 2000, DomUnit.Notional, 0));
        Assert.Equal("1.5", DomRenderer.Amount(1.5m, 80000, DomUnit.Quantity, 1));
    }

    // ------------------------------------------------------------------ rendering and recorded changes

    [Fact]
    public async Task Default_render_keeps_the_header_and_all_book_reading_features_render_from_live_depth()
    {
        using var feed = new BookFeed { Now = Start.AddMinutes(3) };
        using var data = new InstrumentData(Inst(), feed) { RecordLive = false };
        data.Start(Start);
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        var rnd = new Random(2);
        for (int i = 0; i < 2000; i++)
            data.Trades.Add(new Trade(i + 1, Start.AddMilliseconds(i * 90), 80000m + rnd.Next(-30, 30) * 0.1m, 0.01m * rnd.Next(1, 20), rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));
        long px = data.Instrument.ToTicks(80000m);
        var bids = Enumerable.Range(0, 300).Select(i => new BookLevel(px - 1 - i, 0.5m + i % 7)).ToArray();
        var asks = Enumerable.Range(0, 300).Select(i => new BookLevel(px + i, i == 40 ? 60m : 0.4m + i % 5)).ToArray();
        feed.Sink!.OnDepth(data.Instrument, new DepthUpdate { Snapshot = new DepthSnapshot(bids, asks, feed.Now), IsFullRefresh = true, ProviderId = "fake", Venue = "venue", ReceivedAt = feed.Now });
        data.Pump();
        Assert.True(data.Depth.HasBid);

        var plain = new DomRenderer();
        var defaultDom = new DomModel(data, new DomSettings());
        using (var surface = SKSurface.Create(new SKImageInfo(420, 700)))
            plain.Render(surface.Canvas, defaultDom, 420, 700, 300);
        Assert.Equal(1, defaultDom.Step);
        float defaultHeader = plain.HeaderHeight;
        Assert.InRange(defaultHeader, 10, 40);
        Assert.Null(defaultDom.RecentChanges); // hidden column: no recorded-depth query

        foreach (var unit in new[] { DomUnit.Quantity, DomUnit.Notional })
        {
            var settings = new DomSettings { Unit = unit, AutoStep = true, ShowStats = true, HoverMirror = true, AutoLargeSize = true, DistanceMarks = "0.5, 1" };
            settings.Normalize();
            foreach (var column in settings.Columns) column.Visible = true;
            var dom = new DomModel(data, settings);
            var renderer = new DomRenderer();
            using var surface = SKSurface.Create(new SKImageInfo(420, 700));
            renderer.Render(surface.Canvas, dom, 420, 700, 300);
            Assert.True(dom.Step > 1);                         // auto tick grouped rows
            Assert.True(renderer.HeaderHeight > defaultHeader); // stats bar on top
            Assert.Equal("Depth recording is off", dom.RecentChangesStatus);
        }
    }

    [Fact]
    public void Dom_book_changes_use_the_existing_depth_recording_window_and_match_trades()
    {
        var feed = new BookFeed { Now = Start.AddSeconds(60) };
        using var data = new InstrumentData(Inst(), feed);
        var id = Guid.NewGuid();
        DepthSample Book(int seconds, decimal bid, decimal ask) => new(id, seconds, new DepthUpdate
        {
            Snapshot = new DepthSnapshot([new BookLevel(1000, bid), new BookLevel(999, 1)], [new BookLevel(1001, ask), new BookLevel(1002, 1)], Start.AddSeconds(seconds)),
            ProviderId = "fake", Venue = "venue", ReceivedAt = Start.AddSeconds(seconds), IsFullRefresh = true, Coverage = DepthCoverage.ReceivedChanges,
        });
        data.Trades.Add(new Trade(1, Start.AddSeconds(30.5), 100.0m, 2, TradeSide.Sell));
        data.Trades.Add(new Trade(-5, Start.AddSeconds(30.5), 100.0m, 50, TradeSide.Sell)); // candle-derived history: never matched
        data.DepthHistory.Apply(new(Start, Start.AddMinutes(1), [Book(5, 9, 9), Book(30, 9, 9), Book(31, 5, 12), Book(32, 6, 12)], null));
        var dom = new DomModel(data, new DomSettings { ChangeWindowSeconds = 60 }) { Clock = () => feed.Now };
        var result = dom.RefreshChanges(feed.Now);
        Assert.True(result.IsAvailable);
        Assert.Equal(1, result.SkippedTransitions); // 5 s → 30 s is longer than the stale timeout
        Assert.Equal(2m, result.Levels[1000].BidFilled);
        Assert.Equal(2m, result.Levels[1000].BidPulled);
        Assert.Equal(1m, result.Levels[1000].BidAdded);
        Assert.Equal(3m, result.Levels[1001].AskAdded);
        Assert.Equal("Partial: gaps or stale intervals skipped", dom.RecentChangesStatus);

        // the window slides: an hour later nothing recorded remains inside it
        feed.Now = Start.AddHours(1);
        Assert.False(dom.RefreshChanges(feed.Now).IsAvailable);
        Assert.Equal("Depth recording is off", dom.RecentChangesStatus);
    }

    [Fact]
    public void Tape_notional_filter_and_recent_traded_window()
    {
        using var data = new InstrumentData(Inst(), new SimulatedFeed());
        data.Trades.Add(new Trade(1, Start, 80000m, 0.1m, TradeSide.Buy));   // 8K
        data.Trades.Add(new Trade(2, Start.AddSeconds(50), 80000m, 2m, TradeSide.Sell)); // 160K
        var dom = new DomModel(data, new DomSettings { ChangeWindowSeconds = 30 });
        var recent = dom.RecentTraded(Start.AddSeconds(60));
        var only = Assert.Single(recent);
        Assert.Equal((2m, 0m), only.Value);
        var tape = new TapeModel(data) { MinNotional = 100_000 };
        Assert.Equal(160_000m, tape.Notional(2m, 80000m));
        using var surface = SKSurface.Create(new SKImageInfo(300, 200));
        new TapeRenderer().Render(surface.Canvas, tape, 300, 200);
    }

    // ------------------------------------------------------------------ DOM trading gate

    private sealed class Rig
    {
        public readonly InstrumentData Data = new(Inst(), new SimulatedFeed());
        public readonly SimBroker Broker;
        public readonly DomModel Dom;
        public readonly DomRenderer Renderer = new();
        public readonly DomOrderGestures Gestures;
        public readonly List<string> Messages = new();

        public Rig()
        {
            Data.Trades.Add(new Trade(1, Start, 100m, 1, TradeSide.Buy));
            Broker = new SimBroker(Data, new Portfolio(), new TradingStatistics());
            Dom = new DomModel(Data) { Broker = Broker, OrderQuantity = 2 };
            Gestures = new DomOrderGestures(Renderer);
            Gestures.Message += Messages.Add;
            Layout();
        }

        public void Layout()
        {
            using var surface = SKSurface.Create(new SKImageInfo(400, 600));
            Renderer.Render(surface.Canvas, Dom, 400, 600);
        }

        /// <summary>A pixel inside the column at a price (price row found through the renderer's own hit test).</summary>
        public (float X, float Y) At(DomColumn column, decimal price)
        {
            for (float y = Renderer.HeaderHeight + 1; y < 600; y += Dom.RowHeight)
                for (float x = 1; x < 400; x += 4)
                    if (Renderer.HitTest(Dom, x, y) is { } hit && hit.Column == column && hit.Price == price) return (x, y);
            throw new InvalidOperationException($"{column} @ {price} is not on screen.");
        }
    }

    [Fact]
    public void Enabled_dom_routes_clicks_exactly_as_before_to_the_listener_or_directly_to_the_account()
    {
        // without a listener the DOM uses the existing direct path: identical to DomRenderer.Click
        var direct = new Rig();
        var (x, y) = direct.At(DomColumn.Bid, 99m);
        Assert.Equal(DomPressResult.Routed, direct.Gestures.Press(direct.Dom, x, y, rightButton: false, alt: false, clickCount: 1));
        var placed = Assert.Single(direct.Broker.ActiveOrders);
        var reference = new Rig();
        Assert.Equal(direct.Messages.Single(), reference.Renderer.Click(reference.Dom, x, y, false, false));
        var expected = Assert.Single(reference.Broker.ActiveOrders);
        Assert.Equal((expected.Type, expected.Direction, expected.Price, expected.QuantityToFill), (placed.Type, placed.Direction, placed.Price, placed.QuantityToFill));
        Assert.Equal((OrderTypes.Limit, OrderDirections.Buy, 99m, 2m), (placed.Type, placed.Direction, placed.Price, placed.QuantityToFill));

        // with a listener (the app's confirmed order path) the request goes there and nowhere else
        var routed = new Rig();
        var requests = new List<(DomColumn, decimal, bool)>();
        routed.Gestures.OrderRequested += (c, p, cancel) => requests.Add((c, p, cancel));
        var at = routed.At(DomColumn.Ask, 101m);
        routed.Gestures.Press(routed.Dom, at.X, at.Y, false, false, 1);
        routed.Gestures.Press(routed.Dom, at.X, at.Y, rightButton: true, alt: false, clickCount: 1);
        routed.Gestures.Press(routed.Dom, at.X, at.Y, rightButton: false, alt: true, clickCount: 1);
        Assert.Equal(new[] { (DomColumn.Ask, 101m, false), (DomColumn.Ask, 101m, true), (DomColumn.Ask, 101m, true) }, requests);
        Assert.Empty(routed.Broker.ActiveOrders);

        // double-click trading is unchanged: a single click does nothing, a double click routes
        routed.Dom.Settings.DoubleClickToTrade = true;
        Assert.Equal(DomPressResult.None, routed.Gestures.Press(routed.Dom, at.X, at.Y, false, false, 1));
        Assert.Equal(DomPressResult.Routed, routed.Gestures.Press(routed.Dom, at.X, at.Y, false, false, 2));
        Assert.Equal(4, requests.Count);
    }

    [Fact]
    public void Disabled_dom_sends_no_order_cancel_or_move_for_any_gesture()
    {
        var rig = new Rig();
        var resting = OrderActions.Manual(rig.Broker, OrderTypes.Limit, OrderDirections.Buy, 1, 99.5m);
        rig.Layout();
        int requests = 0, moves = 0;
        rig.Gestures.OrderRequested += (_, _, _) => requests++;
        rig.Gestures.OrderMoved += (_, _) => moves++;
        rig.Gestures.TradingEnabled = false;

        var bid = rig.At(DomColumn.Bid, 99m);
        var own = rig.At(DomColumn.BuyOrders, 99.5m);
        foreach (var (x, y) in new[] { bid, own, rig.At(DomColumn.Ask, 101m), rig.At(DomColumn.Bid, 101.2m) })
            foreach (var (right, alt, clicks) in new[] { (false, false, 1), (false, false, 2), (true, false, 1), (false, true, 1) })
                Assert.Equal(DomPressResult.Blocked, rig.Gestures.Press(rig.Dom, x, y, right, alt, clicks));
        Assert.False(rig.Gestures.IsDragging);
        Assert.False(rig.Gestures.ShowsMoveCursor(rig.Dom, own.X, own.Y));
        Assert.False(rig.Gestures.Release(rig.Dom, bid.Y));
        Assert.Equal(0, requests);
        Assert.Equal(0, moves);
        Assert.Same(resting, Assert.Single(rig.Broker.ActiveOrders));
        Assert.Equal(99.5m, resting.Price);
        Assert.All(rig.Messages, m => Assert.Equal(DomOrderGestures.TradingOffMessage, m));

        // without a listener the direct account path is blocked too
        var direct = new Rig();
        direct.Gestures.TradingEnabled = false;
        var at = direct.At(DomColumn.Bid, 99m);
        Assert.Equal(DomPressResult.Blocked, direct.Gestures.Press(direct.Dom, at.X, at.Y, false, false, 1));
        Assert.Empty(direct.Broker.ActiveOrders);
    }

    [Fact]
    public void Dragging_reprices_only_while_enabled_and_switching_off_mid_drag_leaves_the_order()
    {
        var rig = new Rig();
        var order = OrderActions.Manual(rig.Broker, OrderTypes.Limit, OrderDirections.Buy, 1, 99.5m);
        rig.Layout();
        var moves = new List<(Order, decimal)>();
        rig.Gestures.OrderMoved += (o, p) => moves.Add((o, p));
        var from = rig.At(DomColumn.BuyOrders, 99.5m);
        var to = rig.At(DomColumn.BuyOrders, 99.2m);

        Assert.Equal(DomPressResult.DragStarted, rig.Gestures.Press(rig.Dom, from.X, from.Y, false, false, 1));
        rig.Gestures.Move(rig.Dom, to.Y);
        Assert.True(rig.Gestures.Release(rig.Dom, to.Y));
        Assert.Equal((order, 99.2m), Assert.Single(moves));

        // switched off while dragging: release moves nothing
        Assert.Equal(DomPressResult.DragStarted, rig.Gestures.Press(rig.Dom, from.X, from.Y, false, false, 1));
        rig.Gestures.Move(rig.Dom, to.Y);
        rig.Gestures.TradingEnabled = false;
        Assert.True(rig.Gestures.Release(rig.Dom, to.Y));
        Assert.Single(moves);
        Assert.Null(rig.Dom.DragOrder);
        Assert.Equal(99.5m, order.Price);
    }

    // ------------------------------------------------------------------ fixtures

    private sealed class BookFeed : IMarketDataFeed
    {
        public string Id => "fake";
        public string DisplayName => "Fake DOM fixture";
        public bool SupportsHistory => false;
        public bool IsLive => false;
        public DateTime Now { get; set; } = Start;
        public IMarketDataSink? Sink { get; private set; }
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>([Inst()]);
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            Sink = sink;
            sink.OnStatus(instrument, FeedStatus.Connected, null);
            return new EndSubscription();
        }
        public void Dispose() { }
    }

    private sealed class EndSubscription : IDisposable
    {
        public void Dispose() { }
    }

    private static async Task Until(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            Assert.True(DateTime.UtcNow < until, "The fake DOM feed did not settle.");
            await Task.Delay(1);
        }
    }
}
