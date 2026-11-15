using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Feeds.Venues;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using static TradingHub.Tests.VenueFixtures;

namespace TradingHub.Tests;

/// <summary>Order-flow, book and derivatives library studies against deterministic fixtures (no network).</summary>
public sealed class FlowLibraryTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "th-flow-library-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static TradeProvenance From(string source, DataQuality quality = DataQuality.Raw) => new(quality, SourceIdentity: source);

    /// <summary>An aggregate of Binance Futures, Bybit linear and Binance Spot with deterministic prints per minute.</summary>
    private static (InstrumentData Data, AggregateFeed Feed, IDisposable[] Venues) Aggregate(Action<InstrumentData, Func<long>> fill)
    {
        var binance = new FakeVenue("binance-futures", "Binance Futures", "Binance", spot: false);
        var bybit = new FakeVenue("bybit-linear", "Bybit", "Bybit", spot: false);
        var spot = new FakeVenue("binance-spot", "Binance Spot", "Binance", spot: true);
        var feed = new AggregateFeed([binance, bybit, spot]);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = AggregateFeed.FeedId, TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        var data = new InstrumentData(instrument, feed) { RecordLive = false };
        long id = 0;
        fill(data, () => ++id);
        return (data, feed, new IDisposable[] { binance, bybit, spot });
    }

    private static void Release((InstrumentData Data, AggregateFeed Feed, IDisposable[] Venues) fixture)
    {
        fixture.Data.Dispose();
        fixture.Feed.Dispose();
        foreach (var v in fixture.Venues) v.Dispose();
    }

    // ------------------------------------------------------------------ flow attribution and per-exchange column

    [Fact]
    public void Flow_bars_attribute_prints_by_market_keep_unknown_markets_unattributed_and_never_count_compact_packets_as_trades()
    {
        Assert.Equal(0, MarketFlowBar.Band(1000));
        Assert.Equal(2, MarketFlowBar.Band(80_000));
        Assert.Equal(7, MarketFlowBar.Band(9_000_000));
        var index = new Dictionary<string, int> { ["a"] = 0, ["b"] = 1 };
        var t = Start;
        var bar = MarketFlowBar.Build([
            new BarTrade(t, 100, 2, TradeSide.Buy, From("a")),
            new BarTrade(t, 100, 1, TradeSide.Sell, From("b")),
            new BarTrade(t, 100, 5, TradeSide.Buy, From("unknown-venue")),
            new BarTrade(t, 100, 7, TradeSide.Sell, From("a", DataQuality.Compact)),
            new BarTrade(t, 100, 9, TradeSide.Buy, From("a", DataQuality.Raw | DataQuality.InferredSide)),
            new BarTrade(t, 100, 3, TradeSide.None, From("b")),
        ], index, 2);
        Assert.True(bar.HasFlow);
        Assert.Equal((200m, 700m), (bar.BuyNotional[0], bar.SellNotional[0]));
        Assert.Equal(-100m, bar.Net(1));
        Assert.Equal(500m, bar.UnattributedBuy);
        Assert.Equal(3, bar.Trades);
        Assert.True(bar.HasCompact);
        Assert.Equal(200m + 500m, bar.SizeBuy[0]);

        var empty = MarketFlowBar.Build([], index, 2);
        Assert.False(empty.HasFlow);
        Assert.False(empty.HasIndividualPrints);
    }

    [Fact]
    public void Flow_by_exchange_ranks_exchanges_by_notional_with_spot_and_perpetual_merged_per_exchange_and_filtered()
    {
        var sources = new[]
        {
            new InstrumentSource("binance-futures", "Binance Futures", "Binance", false),
            new InstrumentSource("bybit-linear", "Bybit", "Bybit", false),
            new InstrumentSource("binance-spot", "Binance Spot", "Binance", true),
        };
        var index = sources.Select((s, i) => (s.Identity, i)).ToDictionary(x => x.Identity, x => x.i);
        var flow = MarketFlowBar.Build([
            new BarTrade(Start, 100, 10, TradeSide.Buy, From("bybit-linear")),
            new BarTrade(Start, 100, 4, TradeSide.Buy, From("binance-futures")),
            new BarTrade(Start, 100, 3, TradeSide.Sell, From("binance-spot")),
        ], index, 3);
        var ranked = FlowByExchange.Rank(sources, [flow], FlowByExchange.MarketFilter.Both);
        Assert.Equal(new[] { "Bybit", "Binance" }, ranked.Select(r => r.Name));
        Assert.Equal((400m, -300m), (ranked[1].NetPerpetual, ranked[1].NetSpot));
        var spotOnly = FlowByExchange.Rank(sources, [flow], FlowByExchange.MarketFilter.Spot);
        Assert.Equal("Binance", Assert.Single(spotOnly).Name);
    }

    [Fact]
    public void Flow_studies_render_on_an_aggregate_chart_and_lamps_count_only_measured_prints()
    {
        var fixture = Aggregate((data, next) =>
        {
            string[] markets = { "binance-futures", "bybit-linear", "binance-spot" };
            for (int i = 0; i < 600; i++)
            {
                var time = Start.AddSeconds(i * 6);
                string source = markets[i % 3];
                var side = i % 3 == 0 ? TradeSide.Buy : i % 3 == 1 ? TradeSide.Sell : i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell;
                data.Trades.Add(new Trade(next(), time, 80000 + i % 7, i % 20 == 0 ? 10 : 0.1m, side, From(source)));
            }
        });
        try
        {
            using var model = new ChartModel(fixture.Data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
            var flow = new FlowByExchange { Ranking = FlowByExchange.RankWindow.Visible };
            var stats = new BarStatsStrip { DeltaPercent = true, Trades = true, AverageTrade = true, DeltaRetail = true, DeltaWhales = true, CvdWhales = true, Imbalances = true, Stacked = true, Poc = true, Range = true, BookNet = true };
            foreach (var study in new Indicator[] { flow, stats, new SpotPerpetualDominance(), new PositioningNlNs(), new DecisionBoard(), new DeltaDeviationBands(), new VolumeConcentrationBubbles(), new FootprintImbalanceAlerts(),
                new PrintAbsorption { ThresholdNotional = 50_000 }, new TrappedTraders(), new HighlightsAndNotifications(), new LiquidityTracker(), new BookMirror(), new LiquidationLevels(),
                new LiquidityImbalanceOscillator(), new OrderBookPressure(), new LeverageLiquidationBands(), new EstimatedLiquidationMap(), new LiquidationBursts() })
                Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
            model.Update();
            model.Viewport.BarWidth = 30;
            using var surface = SKSurface.Create(new SKImageInfo(1400, 1000));
            new ChartRenderer().Render(surface.Canvas, model, 1400, 1000, new ChartRenderState());

            Assert.Equal(new[] { "Binance", "Bybit" }, flow.RankedExchanges);
            var (buy, sell, sizeBuy, sizeSell) = flow.LampTotals(Start.AddSeconds(599 * 6));
            Assert.True(buy[3] > 0 && sell[3] > 0);
            Assert.True(buy[0] <= buy[3]);
            Assert.Equal(buy[3] + sell[3], sizeBuy.Sum() + sizeSell.Sum());

            var rows = stats.BuildRows(0, model.Series.Count - 1);
            var tradeRow = rows.Single(r => r.Name == "trades");
            Assert.Equal(10m, tradeRow.Value(0));
            Assert.Null(rows.Single(r => r.Name == "book net").Value(0)); // no recorded book: a gap, never zero
        }
        finally { Release(fixture); }
    }

    [Fact]
    public void Bar_statistics_count_diagonal_imbalances_and_stacks_and_leave_unmeasured_delta_empty()
    {
        var levels = new List<PriceVolumeInfo>
        {
            new(100) { Bid = 1, Ask = 0 }, new(101) { Bid = 1, Ask = 4 }, new(102) { Bid = 1, Ask = 4 }, new(103) { Bid = 1, Ask = 4 }, new(104) { Bid = 1, Ask = 1 },
        };
        var (imbalances, stacks) = BarStatsStrip.CountImbalances(levels, 3, 0, 3);
        Assert.Equal(3, imbalances);
        Assert.Equal(1, stacks);

        var instrument = new Instrument { Symbol = "STATS", FeedId = "fake", TickSize = 1, VolumeStep = 1 };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        data.Trades.Add(new Trade(1, Start, 100, 5, TradeSide.Buy));
        data.Trades.Add(new Trade(2, Start.AddMinutes(1), 101, 5, TradeSide.None));
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
        var strip = new BarStatsStrip();
        Assert.Null(model.AddIndicator(strip, applyDefaultColors: false).Error);
        var rows = strip.BuildRows(0, 1);
        Assert.Equal(5m, rows.Single(r => r.Name == "delta").Value(0));
        Assert.Null(rows.Single(r => r.Name == "delta").Value(1));
        Assert.Null(rows.Single(r => r.Name == "cvd").Value(1));
    }

    // ------------------------------------------------------------------ positioning (NL/NS)

    [Fact]
    public void Positioning_classifies_open_interest_change_by_aggressor_and_by_price_and_leaves_missing_open_interest_as_gaps()
    {
        Assert.Equal((5m, true), PositioningNlNs.ClassifyByDelta(5, 2));
        Assert.Equal((-5m, true), PositioningNlNs.ClassifyByDelta(5, -2));
        Assert.Equal((-3m, false), PositioningNlNs.ClassifyByDelta(-3, -1));
        Assert.Null(PositioningNlNs.ClassifyByDelta(0, 1));
        Assert.Equal(PositioningNlNs.Regime.NewLongs, PositioningNlNs.ClassifyByPrice(1, 1));
        Assert.Equal(PositioningNlNs.Regime.NewShorts, PositioningNlNs.ClassifyByPrice(1, -1));
        Assert.Equal(PositioningNlNs.Regime.ShortsCovering, PositioningNlNs.ClassifyByPrice(-1, 1));
        Assert.Equal(PositioningNlNs.Regime.LongsClosing, PositioningNlNs.ClassifyByPrice(-1, -1));

        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "fake", TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        for (int i = 0; i < 4; i++) data.Trades.Add(new Trade(i + 1, Start.AddMinutes(i).AddSeconds(10), 100, 2, i == 2 ? TradeSide.Sell : TradeSide.Buy));
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
        var study = new PositioningNlNs();
        // Open interest observed mid-bar for bars 0..2 only (each venue contributes half).
        var samples = new[] { (Start.AddSeconds(-30), 50m), (Start.AddSeconds(30), 55m), (Start.AddSeconds(90), 52m), (Start.AddSeconds(150), 60m) };
        study.Derivatives.SetOpenInterest(DerivativesData.Binance, samples);
        study.Derivatives.SetOpenInterest(DerivativesData.Bybit, samples);
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
        var bars = Assert.IsType<OptionalValueDataSeries>(study.DataSeries[0]);
        var line = Assert.IsType<OptionalValueDataSeries>(study.DataSeries[1]);
        Assert.Equal(10m, bars[0]);   // OI +10 with net buying: new longs
        Assert.Equal(6m, bars[1]);    // OI −6 with net buying: shorts covering (signed by the aggressor, faint)
        Assert.Equal(-16m, bars[2]);  // OI +16 with net selling: new shorts
        Assert.Equal(0m, line[2]);
        Assert.True(bars.IsAvailable(3)); // OI flat across the forming bar: a real zero, not a gap
        Assert.Equal(0m, bars[3]);
        Assert.Null(study.Derivatives.OpenInterestAt(Start.AddMinutes(30))); // stale beyond ten minutes: unknown
    }

    // ------------------------------------------------------------------ book studies

    private static DepthUpdate Book(DateTime at, (long Ticks, decimal Size)[] bids, (long Ticks, decimal Size)[] asks) => new()
    {
        Snapshot = new DepthSnapshot(bids.Select(b => new BookLevel(b.Ticks, b.Size)).ToArray(), asks.Select(a => new BookLevel(a.Ticks, a.Size)).ToArray(), at),
        ProviderId = "fake", Venue = "venue-a", ReceivedAt = at, Sequence = 1, IsFullRefresh = true, Coverage = DepthCoverage.ReceivedChanges,
    };

    [Fact]
    public void Liquidity_tracker_weighs_by_distance_and_mirror_sums_bands()
    {
        // Ticks of 0.1: best bid 80000.0, a second level 10 bp lower (800 ticks) weighs one half with a 10 bp half-life.
        var book = Book(Start, [(800000, 12.5m), (799200, 12.5m)], [(800001, 12.5m)]).Snapshot;
        var (bid, ask, available) = LiquidityTracker.Measure(book, 0.1m, 10, 0, 0, false);
        Assert.True(available);
        Assert.InRange(bid, 1_499_000m, 1_501_000m);
        Assert.InRange(ask, 999_000m, 1_001_000m);
        Assert.Equal(0m, LiquidityTracker.Measure(book, 0.1m, 10, 2_000_000, 0, false).Bid);
        Assert.False(LiquidityTracker.Measure(new DepthSnapshot([new BookLevel(1, 1)], [], Start), 0.1m, 10, 0, 0, false).Available);

        var levels = new[] { (80000m, 1m), (79990m, 2m), (80010m, 0.5m) };
        Assert.Equal(3m, BookMirror.Sum(levels, 79980, 80000).Size);
    }

    [Fact]
    public void Book_imbalance_oscillator_and_pressure_use_recorded_snapshots_and_bars_without_a_book_are_gaps()
    {
        var book = Book(Start, [(1000, 6), (985, 4)], [(1001, 2), (1020, 8)]).Snapshot;
        Assert.Equal(50m, LiquidityImbalanceOscillator.Imbalance(book, 0, 1)); // within 1 %: bids 6 vs asks 2
        var pressure = OrderBookPressure.Pressure(book);
        Assert.Null(pressure[0]);
        Assert.Equal(0.5m, pressure[1]);
        Assert.Equal(-1m / 3, pressure[2]); // 1–2.5 %: bids 4 vs asks 8
        Assert.Equal((1980m, 2020m), LeverageLiquidationBands.Prices(2000, 100));

        var instrument = new Instrument { Symbol = "BOOK", FeedId = "fake", TickSize = 1, VolumeStep = 1 };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        for (int i = 0; i < 3; i++) data.Trades.Add(new Trade(i + 1, Start.AddMinutes(i), 1000, 1, TradeSide.Buy));
        data.DepthHistory.Start(_root, new(), Start.AddMinutes(3));
        var id = Guid.NewGuid();
        data.DepthHistory.Apply(new(Start, Start.AddMinutes(3), [new DepthSample(id, 1, Book(Start.AddSeconds(5), [(1000, 6)], [(1001, 2)]))], null));
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
        var oscillator = new LiquidityImbalanceOscillator { From = LiquidityImbalanceOscillator.DepthFrom.P0, To = LiquidityImbalanceOscillator.DepthTo.P1, Smooth = 1 };
        var tracker = new LiquidityTracker();
        Assert.Null(model.AddIndicator(oscillator, applyDefaultColors: false).Error);
        Assert.Null(model.AddIndicator(tracker, applyDefaultColors: false).Error);
        var series = Assert.IsType<OptionalValueDataSeries>(oscillator.DataSeries[0]);
        Assert.Equal(50m, series[0]);
        Assert.False(series.IsAvailable(1));
        Assert.False(Assert.IsType<OptionalValueDataSeries>(tracker.DataSeries[0]).IsAvailable(2));
    }

    // ------------------------------------------------------------------ third-party liquidation levels

    private const string LevelsJson = """{"v":2,"coin":"BTC","attribution":"Powered by HyperTracker","snapshot":{"asOf":1793840368498},"mark":80500.0,"positions":3,"step":10.0,"bins":[[8000,100,50,0,0,2,0,120,0],[8040,0,0,300,0,0,1,0,300],[8100,0,0,700,100,0,2,0,600]]}""";

    [Fact]
    public void Third_party_liquidation_levels_parse_the_published_format_and_drop_levels_price_traded_through()
    {
        var snapshot = LiquidationLevelsService.Parse(LevelsJson, Start)!;
        Assert.Equal(3, snapshot.Bins.Count);
        Assert.Equal((80000m, 150m), (snapshot.Bins[0].Price, snapshot.Bins[0].LongNotional));
        Assert.Equal("Powered by HyperTracker", snapshot.Attribution);
        var left = LiquidationLevelsService.Remaining(snapshot, 80005, 80450);
        Assert.DoesNotContain(left, b => b.Price == 80000);
        Assert.DoesNotContain(left, b => b.Price == 80400);
        Assert.Contains(left, b => b.Price == 81000 && b.ShortNotional == 800);
        Assert.Null(LiquidationLevelsService.Parse("""{"v":1}""", Start));
        Assert.True(LiquidationLevelsService.Supports("BTC", "BTCUSDT"));
        Assert.False(LiquidationLevelsService.Supports("ETH", "ETHUSDT"));
    }

    [Fact]
    public async Task Third_party_liquidation_levels_are_rate_limited_keep_the_last_snapshot_on_failure_and_are_off_by_default()
    {
        Assert.False(new TradingHub.App.Services.AppSettings().ThirdPartyLiquidationLevels);
        int calls = 0;
        bool fail = false;
        using var handler = new Http((request, _) =>
        {
            calls++;
            Assert.Equal(LiquidationLevelsService.DefaultEndpoint, request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            return fail ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) : Json(LevelsJson);
        });
        var now = Start;
        using var service = new LiquidationLevelsService(new HttpClient(handler), ownsHttp: true, utcNow: () => now);
        var first = await service.RefreshAsync(CancellationToken.None);
        Assert.NotNull(first.Snapshot);
        Assert.Equal(1, calls);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, calls); // within the minimum refresh interval
        now += LiquidationLevelsService.MinimumRefresh;
        fail = true;
        var failed = await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Same(first.Snapshot, failed.Snapshot);
        Assert.Contains("503", failed.Message);
    }

    // ------------------------------------------------------------------ stand-out heuristics and notifications

    [Fact]
    public void Absorption_trapped_traders_and_the_stand_out_rule()
    {
        var trades = new List<BarTrade>();
        // Thirty sells of 0.2 at 80000 within 6 ms: 480k absorbed by the bid; scattered buys elsewhere.
        var raw = new TradeProvenance(DataQuality.Raw);
        for (int i = 0; i < 30; i++) trades.Add(new BarTrade(Start.AddTicks(i * 2000), 80000m, 0.2m, TradeSide.Sell, raw));
        for (int i = 0; i < 10; i++) trades.Add(new BarTrade(Start.AddSeconds(1 + i), 80001m, 0.1m, TradeSide.Buy, raw));
        trades.Add(new BarTrade(Start, 80000m, 50m, TradeSide.Sell, new TradeProvenance(DataQuality.Compact))); // packets are not prints
        var marks = PrintAbsorption.Find(trades.OrderBy(t => t.Time).ToList(), 10, 250_000);
        var mark = Assert.Single(marks);
        Assert.False(mark.AggressorBuy);
        Assert.Equal(480_000m, mark.Notional);

        var levels = new List<PriceVolumeInfo>
        {
            new(100) { Bid = 5, Ask = 1 }, new(101) { Bid = 5, Ask = 1 }, new(102) { Bid = 4, Ask = 2 }, new(103) { Bid = 1, Ask = 9 }, new(104) { Bid = 1, Ask = 8 },
        };
        var (net, average, other) = TrappedTraders.Wick(levels, 103, 104, true);
        Assert.Equal(15m, net);
        Assert.True(net > other);
        Assert.InRange(average, 103m, 104m);

        var values = Enumerable.Range(0, 80).Select(i => i == 79 ? 100.0 : 10 + i % 3).ToArray();
        Assert.True(values[79] > StandOutRule.Threshold(i => values[i], 79, 72, 2));
        Assert.Null(StandOutRule.Threshold(i => values[i], 5, 72, 2));
        // Missing values are skipped, not counted as zero.
        Assert.Null(StandOutRule.Threshold(i => i % 2 == 0 ? null : values[i], 20, 20, 2));
    }

    [Fact]
    public void Live_alert_gate_rejects_history_and_future_events_and_rate_limits()
    {
        var now = Start;
        var gate = new LiveAlertGate { Clock = () => now };
        Assert.False(gate.Allow(Start.AddMinutes(-5)));
        Assert.False(gate.Allow(Start.AddMinutes(1)));
        Assert.True(gate.Allow(Start));
        Assert.False(gate.Allow(Start)); // one per second
        for (int i = 1; i <= 3; i++) { now = Start.AddSeconds(i * 1.5); Assert.True(gate.Allow(now)); }
        now = Start.AddSeconds(6);
        Assert.False(gate.Allow(now)); // four per ten seconds
        now = Start.AddSeconds(12);
        Assert.True(gate.Allow(now));
    }

    [Fact]
    public void Absorption_notifications_fire_once_for_live_marks_and_never_for_loaded_history()
    {
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "fake", TickSize = 0.1m, VolumeStep = 0.001m };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        long id = 0;
        for (int bar = 0; bar < 3; bar++)
            for (int i = 0; i < 30; i++)
                data.Trades.Add(new Trade(++id, Start.AddMinutes(bar).AddTicks(i * 2000), 80000m, 0.2m, TradeSide.Sell));
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
        var alerts = new List<AlertInfo>();
        model.AlertRaised += alerts.Add;
        var history = new PrintAbsorption { Notify = true, Clock = () => Start.AddDays(1) };
        Assert.Null(model.AddIndicator(history, applyDefaultColors: false).Error);
        Assert.Empty(alerts);
        var live = new PrintAbsorption { Notify = true, Clock = () => Start.AddMinutes(2).AddSeconds(1) };
        Assert.Null(model.AddIndicator(live, applyDefaultColors: false).Error);
        model.Update();
        model.Update();
        var alert = Assert.Single(alerts);
        Assert.Contains("80000", alert.Message);
    }

    // ------------------------------------------------------------------ order-flow library

    [Fact]
    public void Spot_versus_perpetual_dominance_scores_each_side_against_its_own_history_and_excludes_unattributed_prints()
    {
        var fixture = Aggregate((data, next) =>
        {
            for (int bar = 0; bar < 20; bar++)
            {
                var time = Start.AddMinutes(bar).AddSeconds(5);
                decimal spotSize = bar == 19 ? 30 : 1 + bar % 3, perpetualSize = 2 + bar % 2;
                data.Trades.Add(new Trade(next(), time, 100, spotSize, TradeSide.Buy, From("binance-spot")));
                data.Trades.Add(new Trade(next(), time, 100, perpetualSize, TradeSide.Sell, From("bybit-linear")));
                data.Trades.Add(new Trade(next(), time, 100, 50, TradeSide.Buy, From("other-venue")));
            }
        });
        try
        {
            using var model = new ChartModel(fixture.Data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
            var study = new SpotPerpetualDominance { Unit = SpotPerpetualDominance.DominanceUnit.Base };
            Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
            Assert.Equal((30m, -3m), study.Deltas(19)); // the 50-lot print of an unknown market is not on either side
            var spot = Assert.IsType<OptionalValueDataSeries>(study.DataSeries[1]);
            Assert.False(spot.IsAvailable(11)); // fewer than twelve preceding bars
            Assert.True(spot.IsAvailable(12));
            Assert.True(spot[19] > 10);
            Assert.Equal("", study.Status);
        }
        finally { Release(fixture); }
    }

    [Fact]
    public void Footprint_stacks_volume_tiers_deviation_bands_and_decision_board_without_data()
    {
        var rows = FootprintImbalanceAlerts.Rows([new(100) { Bid = 1, Ask = 0 }, new(101) { Bid = 1, Ask = 4 }, new(102) { Bid = 1, Ask = 4 }, new(103) { Bid = 1, Ask = 4 }], 1);
        Assert.Equal((3, 3), FootprintImbalanceAlerts.Longest(rows, true, FootprintImbalanceAlerts.ImbalanceMethod.Diagonal, 3, 0, true));
        Assert.Equal(0, FootprintImbalanceAlerts.Longest(rows, false, FootprintImbalanceAlerts.ImbalanceMethod.Diagonal, 3, 0, true).Length);
        Assert.Equal(3, FootprintImbalanceAlerts.Longest(rows, true, FootprintImbalanceAlerts.ImbalanceMethod.Horizontal, 3, 0, true).Length);

        var preceding = Enumerable.Range(0, 50).Select(i => 10m + i % 2).ToList();
        Assert.Equal(3, VolumeConcentrationBubbles.Tier(20, preceding, 1.5m, 2.5m, 3.5m));
        Assert.Equal(0, VolumeConcentrationBubbles.Tier(10.5m, preceding, 1.5m, 2.5m, 3.5m));
        Assert.Equal(1.0, DeltaDeviationBands.Deviation([1m, 3m]));
        Assert.Null(DeltaDeviationBands.Deviation([1m]));

        var instrument = new Instrument { Symbol = "BOARD", FeedId = "fake", TickSize = 1, VolumeStep = 1 };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        for (int i = 0; i < 80; i++) data.Trades.Add(new Trade(i + 1, Start.AddMinutes(i), 100 + i, 1 + i % 3, TradeSide.Buy));
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
        var board = new DecisionBoard();
        Assert.Null(model.AddIndicator(board, applyDefaultColors: false).Error);
        var result = board.Evaluate(79);
        Assert.Equal(1, result.Signs[0]); // rising closes above the EMA
        Assert.Equal(1, result.Signs[3]); // all measured flow is buying
        Assert.Null(result.Signs[4]);     // no open interest: does not count
        Assert.Null(result.Signs[5]);     // no observed liquidations: does not count
        Assert.Equal("—", result.Readings[4]);
    }

    // ------------------------------------------------------------------ derivatives estimates

    [Fact]
    public void Estimated_liquidation_map_spreads_new_open_interest_by_aggressor_share_and_removes_traversed_levels()
    {
        var bars = new List<EstimatedLiquidationMap.Entry>
        {
            new(101, 99, 100, 10, 10, 2),      // all buyers opened 2: long levels only
            new(101, 99, 100, 10, null, 5),    // unmeasured delta: skipped
            new(101, 99, 100, 10, 0, null),    // no open interest: skipped
            new(100.5m, 99.5m, 100, 10, -10, 4), // all sellers opened 4: short levels only
        };
        var map = EstimatedLiquidationMap.Estimate(bars, 100, 12, 240, 0.5m);
        Assert.Equal((2, 2), (map.Used, map.Skipped));
        decimal Above() => map.Rows.Select((v, k) => (v, k)).Where(x => map.Low + (x.k + 0.5m) * map.Step > 100).Sum(x => x.v);
        decimal Below() => map.Rows.Select((v, k) => (v, k)).Where(x => map.Low + (x.k + 0.5m) * map.Step < 100).Sum(x => x.v);
        Assert.True(Above() > 0);
        Assert.True(Below() > 0);
        // A later low at 90 liquidates every first-bar long level above it (5×: 80.5; 10×: 90.5 is traversed).
        bars.Add(new(100, 90, 95, 10, 0, 0));
        var traversed = EstimatedLiquidationMap.Estimate(bars, 95, 12, 240, 0.5m);
        Assert.True(traversed.Rows.Sum() < map.Rows.Sum());

        double?[] totals = Enumerable.Range(0, 30).Select(i => i == 29 ? 100.0 : i % 4 == 0 ? (double?)null : 10 + i % 3).ToArray();
        Assert.True(LiquidationBursts.BurstScore(i => totals[i], 29, 28) > 3);
        Assert.Null(LiquidationBursts.BurstScore(i => totals[i], 10, 10));
    }

    // ------------------------------------------------------------------ big trades extensions

    [Fact]
    public void Big_trades_fixed_threshold_can_be_notional_and_each_bar_can_keep_only_its_largest_orders()
    {
        var instrument = new Instrument { Symbol = "BIG", FeedId = "sim", TickSize = 1, VolumeStep = 1 };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        data.Trades.Add(new Trade(1, Start, 100, 5, TradeSide.Buy));               // 500 notional
        data.Trades.Add(new Trade(2, Start.AddSeconds(1), 1000, 2, TradeSide.Sell)); // 2000 notional
        data.Trades.Add(new Trade(3, Start.AddSeconds(2), 1000, 3, TradeSide.Buy));  // 3000 notional
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var byNotional = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, Unit = BigTrades.SizeUnit.Notional, MinVolume = 1500 };
        var byBase = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 3 };
        Assert.Null(model.AddIndicator(byNotional, applyDefaultColors: false).Error);
        Assert.Null(model.AddIndicator(byBase, applyDefaultColors: false).Error);
        model.Update();
        Assert.Equal(2, byNotional.BubbleCount); // the 5-lot at 100 is large in size but small in notional
        Assert.Equal(2, byBase.BubbleCount);     // sizes 5 and 3
        byNotional.MaxPerBar = 1;
        Assert.Equal(3m, Assert.Single(byNotional.VisibleBubbles(0, 0)).Volume);
    }

    // ------------------------------------------------------------------ fixtures

    private sealed class FakeVenue(string id, string name, string venue, bool spot) : IMarketDataFeed, ICryptoVenue
    {
        public string Id => id;
        public string DisplayName => name;
        public string Venue => venue;
        public bool IsSpot => spot;
        public bool SupportsHistory => false;
        public bool IsLive => false;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Instrument>>([new Instrument { Symbol = "BTCUSDT", FeedId = id, TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" }]);
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => new Subscription();
        public void Dispose() { }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
