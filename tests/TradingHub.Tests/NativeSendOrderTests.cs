using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// A chart gesture's account call only starts a native order: the request itself is sent later. Requests of one native
/// account reach the venue in the order their gestures were issued: a Close position issued after a market buy is not
/// sent while that buy's request is still unanswered.
/// </summary>
public sealed class NativeSendOrderTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private readonly NativeOrderTestFixture _orders = new();

    public void Dispose() => _orders.Dispose();

    [Fact]
    public async Task Tradovate_close_position_is_not_sent_before_an_earlier_buy_is_answered()
    {
        using var fake = new FakeTradovate { TimestampPositions = true };
        fake.SeedPosition(1);
        var normalHttp = fake.Server.OnHttp!;
        var buyReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdNextBuy = 0;
        var arrivals = new List<string>(); // order requests in arrival order: "<route> <action>"
        fake.Server.OnHttpAsync = async (request, _) =>
        {
            if (request.Route.StartsWith("/v1/order/", StringComparison.Ordinal))
            {
                string action = request.Route == "/v1/order/placeorder" ? JsonNode.Parse(request.Body)!["action"]!.GetValue<string>() : "";
                lock (arrivals) arrivals.Add((request.Route + " " + action).Trim());
                if (action == "Buy" && Interlocked.Exchange(ref holdNextBuy, 0) == 1) await buyReply.Task;
            }
            return normalHttp(request);
        };
        string[] Arrivals() { lock (arrivals) return arrivals.ToArray(); }
        using var data = new InstrumentData(new Instrument
        {
            Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD",
        }, new SimulatedFeed()) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady, () => session.StatusText);
        using var broker = _orders.Tradovate(data, session, Assert.Single(session.Accounts), "Tradovate send order");
        await Until(() => broker.PositionReady && broker.OrdersReady && broker.Position.Volume == 1, () => broker.LastError);

        Interlocked.Exchange(ref holdNextBuy, 1);
        var buy = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1);
        await Until(() => Arrivals().Contains("/v1/order/placeorder Buy"), () => broker.LastError);
        try
        {
            // Close position, issued after the buy: cancel all, then a reducing market sell sized from the position.
            OrderActions.Flatten(broker);
            await Until(() => Arrivals().Length > 1 || session.QueuedSends(101) > 0, () => broker.LastError);
            Assert.Equal(new[] { "/v1/order/placeorder Buy" }, Arrivals());
        }
        finally { buyReply.TrySetResult(); }

        await Until(() => Arrivals().Contains("/v1/order/placeorder Sell"), () => $"{broker.LastError}; {string.Join(", ", Arrivals())}");
        var sent = Arrivals().Where(route => route.StartsWith("/v1/order/placeorder", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "/v1/order/placeorder Buy", "/v1/order/placeorder Sell" }, sent);
        await Until(() => buy.ExchangeOrderId.Length > 0, () => broker.LastError);
    }

    [Fact]
    public async Task Binance_close_position_is_not_sent_before_an_earlier_buy_is_answered()
    {
        using var fake = new FakeBinance(futures: true);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
        using var broker = _orders.Binance(new InstrumentData(instrument, new SimulatedFeed()), fake.Settings, "Binance send order");
        await Until(() => broker.PositionReady && broker.OrdersReady, () => broker.LastError);
        var opening = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 0.01m);
        await Until(() => broker.Position.Volume == 0.01m && opening.ExchangeOrderId.Length > 0, () => broker.LastError);
        int callsBefore = fake.Calls.Count;

        var buyReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdNextPost = 1;
        fake.BeforePostReply = async _ =>
        {
            if (Interlocked.Exchange(ref holdNextPost, 0) == 1) await buyReply.Task;
        };
        string[] Sends() => fake.Calls.Skip(callsBefore).Where(call => !call.StartsWith("GET ", StringComparison.Ordinal)).ToArray();
        var buy = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 0.01m);
        await Until(() => Sends().Length == 1, () => broker.LastError);
        try
        {
            OrderActions.Flatten(broker);
            await Until(() => Sends().Length > 1 || broker.QueuedSends > 0, () => broker.LastError);
            Assert.Equal(new[] { "POST /fapi/v1/order" }, Sends());
        }
        finally { buyReply.TrySetResult(); }

        await Until(() => fake.Placed.Count(order => order.Side == "SELL") == 1, () => $"{broker.LastError}; {string.Join(", ", Sends())}");
        Assert.Equal("POST /fapi/v1/order", Sends()[0]);
        var placed = fake.Placed.Skip(1).ToArray();
        Assert.Equal(new[] { ("BUY", false), ("SELL", true) }, placed.Select(order => (order.Side, order.ReduceOnly)));
        await Until(() => buy.ExchangeOrderId.Length > 0, () => broker.LastError);
    }

    /// <summary>Close position issued right after a market buy (which fills in the venue as it is answered): the close
    /// is sized after that buy's outcome, so it closes the buy too and the account ends flat.</summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(0, 1)]
    public async Task Tradovate_close_position_after_a_queued_market_buy_closes_it_too(int start, int expectedSell)
    {
        using var fake = new FakeTradovate { TimestampPositions = true };
        if (start != 0) fake.SeedPosition(start);
        using var data = new InstrumentData(new Instrument
        {
            Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD",
        }, new SimulatedFeed()) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady, () => session.StatusText);
        using var broker = _orders.Tradovate(data, session, Assert.Single(session.Accounts), "Tradovate close sizing");
        await Until(() => broker.PositionReady && broker.OrdersReady && broker.Position.Volume == start, () => broker.LastError);

        var buy = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1);
        OrderActions.Flatten(broker);

        JsonObject[] Sells() { lock (fake.Orders) return fake.Orders.Where(order => order["action"]?.GetValue<string>() == "Sell").ToArray(); }
        await Until(() => Sells().Length == 1 && fake.NetPosition == 0 && broker.Position.Volume == 0 && buy.Filled == 1,
            () => $"{broker.LastError}; venue net {fake.NetPosition}, local {broker.Position.Volume}, sells {string.Join(", ", Sells().Select(s => s["orderQty"]))}");
        Assert.Equal(expectedSell, Sells()[0]["orderQty"]!.GetValue<int>());
        Assert.NotEqual(NativeSendTurns.UnsettledMessage, broker.LastError); // sized from a confirmed position
    }

    /// <summary>Position reports without a venue timestamp never prove they include the buy: past its wait the close is
    /// sent from the best known position, once, and says the earlier outcome was unsettled.</summary>
    [Fact]
    public async Task Tradovate_close_position_sent_unconfirmed_reports_the_unsettled_earlier_order()
    {
        using var fake = new FakeTradovate { TimestampPositions = false };
        fake.SeedPosition(1);
        using var data = new InstrumentData(new Instrument
        {
            Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD",
        }, new SimulatedFeed()) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady, () => session.StatusText);
        using var broker = _orders.Tradovate(data, session, Assert.Single(session.Accounts), "Tradovate unconfirmed close");
        broker.CloseConfirmationWait = TimeSpan.FromMilliseconds(300);
        await Until(() => broker.PositionReady && broker.OrdersReady && broker.Position.Volume == 1, () => broker.LastError);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1);
        OrderActions.Flatten(broker);

        JsonObject[] Sells() { lock (fake.Orders) return fake.Orders.Where(order => order["action"]?.GetValue<string>() == "Sell").ToArray(); }
        await Until(() => Sells().Length == 1 && broker.LastError?.Contains(NativeSendTurns.UnsettledMessage, StringComparison.Ordinal) == true,
            () => $"{broker.LastError}; sells {Sells().Length}");
        Assert.InRange(Sells()[0]["orderQty"]!.GetValue<int>(), 1, 2); // the best known position when it was sent
    }

    [Fact]
    public async Task Tradovate_refused_liquidation_is_reported_not_treated_as_sent()
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        fake.Server.OnHttp = request => request.Route == "/v1/order/liquidateposition"
            ? (200, "{\"failureReason\":\"TradingLocked\",\"failureText\":\"Trading is locked for this account.\"}")
            : normalHttp(request);
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady, () => session.StatusText);

        var refused = await Assert.ThrowsAsync<TradovateException>(() => session.LiquidateAsync(101, 555, CancellationToken.None).WaitAsync(Deadline));
        Assert.Equal("Tradovate did not liquidate the position: Trading is locked for this account.", refused.Message);
    }

    [Theory]
    [InlineData("0.01", "0.02")]
    [InlineData("0", "0.01")]
    public async Task Binance_close_position_after_a_queued_market_buy_closes_it_too(string startText, string expectedText)
    {
        decimal start = decimal.Parse(startText, System.Globalization.CultureInfo.InvariantCulture);
        decimal expectedSell = decimal.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture);
        using var fake = new FakeBinance(futures: true);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
        using var broker = _orders.Binance(new InstrumentData(instrument, new SimulatedFeed()), fake.Settings, "Binance close sizing");
        await Until(() => broker.PositionReady && broker.OrdersReady, () => broker.LastError);
        if (start != 0)
        {
            var opening = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, start);
            await Until(() => broker.Position.Volume == start && opening.ExchangeOrderId.Length > 0, () => broker.LastError);
        }

        var buy = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 0.01m);
        OrderActions.Flatten(broker);

        FakeBinanceOrder[] Sells() => fake.Placed.Where(order => order.Side == "SELL").ToArray();
        await Until(() => Sells().Length == 1 && broker.Position.Volume == 0 && buy.ExchangeOrderId.Length > 0,
            () => $"{broker.LastError}; local {broker.Position.Volume}, sells {string.Join(", ", Sells().Select(s => s.Qty))}");
        var sell = Sells()[0];
        Assert.Equal(expectedSell, sell.Qty);
        Assert.True(sell.ReduceOnly);
        Assert.NotEqual(NativeSendTurns.UnsettledMessage, broker.LastError); // sized from a confirmed position
    }

    /// <summary>Reverse issued right after a market buy flips the position that includes that buy: one opposite market
    /// order of twice the confirmed position, ending at minus that position.</summary>
    [Theory]
    [InlineData(1, 4, -2)]
    [InlineData(0, 2, -1)]
    public async Task Tradovate_reverse_after_a_queued_market_buy_flips_the_position_including_it(int start, int expectedSell, int expectedNet)
    {
        using var fake = new FakeTradovate { TimestampPositions = true };
        if (start != 0) fake.SeedPosition(start);
        using var data = new InstrumentData(new Instrument
        {
            Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD",
        }, new SimulatedFeed()) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady, () => session.StatusText);
        using var broker = _orders.Tradovate(data, session, Assert.Single(session.Accounts), "Tradovate reverse sizing");
        await Until(() => broker.PositionReady && broker.OrdersReady && broker.Position.Volume == start, () => broker.LastError);

        var buy = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1);
        OrderActions.Reverse(broker);

        JsonObject[] Sells() { lock (fake.Orders) return fake.Orders.Where(order => order["action"]?.GetValue<string>() == "Sell").ToArray(); }
        await Until(() => Sells().Length == 1 && fake.NetPosition == expectedNet && broker.Position.Volume == expectedNet && buy.Filled == 1,
            () => $"{broker.LastError}; venue net {fake.NetPosition}, local {broker.Position.Volume}, sells {string.Join(", ", Sells().Select(s => s["orderQty"]))}");
        Assert.Equal(expectedSell, Sells()[0]["orderQty"]!.GetValue<int>());
        Assert.NotEqual(NativeSendTurns.UnsettledMessage, broker.LastError); // sized from a confirmed position
    }

    [Theory]
    [InlineData("0.01", "0.04", "-0.02")]
    [InlineData("0", "0.02", "-0.01")]
    public async Task Binance_reverse_after_a_queued_market_buy_flips_the_position_including_it(string startText, string sellText, string netText)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        decimal start = decimal.Parse(startText, inv), expectedSell = decimal.Parse(sellText, inv), expectedNet = decimal.Parse(netText, inv);
        using var fake = new FakeBinance(futures: true);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
        using var broker = _orders.Binance(new InstrumentData(instrument, new SimulatedFeed()), fake.Settings, "Binance reverse sizing");
        await Until(() => broker.PositionReady && broker.OrdersReady, () => broker.LastError);
        if (start != 0)
        {
            var opening = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, start);
            await Until(() => broker.Position.Volume == start && opening.ExchangeOrderId.Length > 0, () => broker.LastError);
        }

        var buy = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 0.01m);
        OrderActions.Reverse(broker);

        FakeBinanceOrder[] Sells() => fake.Placed.Where(order => order.Side == "SELL").ToArray();
        await Until(() => Sells().Length == 1 && broker.Position.Volume == expectedNet && buy.ExchangeOrderId.Length > 0,
            () => $"{broker.LastError}; local {broker.Position.Volume}, sells {string.Join(", ", Sells().Select(s => s.Qty))}");
        var sell = Sells()[0];
        Assert.Equal(expectedSell, sell.Qty);
        Assert.False(sell.ReduceOnly);
        Assert.NotEqual(NativeSendTurns.UnsettledMessage, broker.LastError); // sized from a confirmed position
    }

    private static async Task Until(Func<bool> condition, Func<string?> diagnostics)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "The native send-order fixture did not reach the expected state: " + (diagnostics() ?? "(no error)"));
    }
}
