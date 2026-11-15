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

    /// <summary>A fake Tradovate account with an optional request interceptor, its session and one ES broker.</summary>
    private sealed class TradovateRig : IDisposable
    {
        public FakeTradovate Fake { get; } = new() { TimestampPositions = true };
        public TradovateFeed Feed { get; }
        public TradovateTradingSession Session { get; }
        public InstrumentData Data { get; }
        public TradovateBroker Broker { get; private set; } = null!;
        private readonly List<(string Route, string Action, int Quantity)> _arrivals = new();

        /// <summary>Returns a reply to intercept the request, or null to let the fake venue answer it.</summary>
        public Func<FakeRequest, (string Route, string Action, int Quantity), Task<(int Status, string Body)?>>? Intercept { get; set; }

        public TradovateRig(int start)
        {
            if (start != 0) Fake.SeedPosition(start);
            var normalHttp = Fake.Server.OnHttp!;
            Fake.Server.OnHttpAsync = async (request, _) =>
            {
                if (request.Route.StartsWith("/v1/order/", StringComparison.Ordinal))
                {
                    var body = request.Body.Length > 0 ? JsonNode.Parse(request.Body) : null;
                    var arrival = (request.Route, body?["action"]?.GetValue<string>() ?? "", body?["orderQty"]?.GetValue<int>() ?? 0);
                    lock (_arrivals) _arrivals.Add(arrival);
                    if (Intercept != null && await Intercept(request, arrival) is { } reply) return reply;
                }
                return normalHttp(request);
            };
            Feed = new TradovateFeed(() => Fake.Settings());
            Session = new TradovateTradingSession(Feed.Connection);
            Data = new InstrumentData(new Instrument
            {
                Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD",
            }, new SimulatedFeed()) { RecordLive = false };
        }

        public (string Route, string Action, int Quantity)[] Arrivals()
        {
            lock (_arrivals) return _arrivals.ToArray();
        }

        public (string Route, string Action, int Quantity)[] Placed(string action) =>
            Arrivals().Where(arrival => arrival.Route == "/v1/order/placeorder" && arrival.Action == action).ToArray();

        public async Task StartAsync(NativeOrderTestFixture orders, int start)
        {
            Session.Start();
            await Until(() => Session.SnapshotReady, () => Session.StatusText);
            Broker = orders.Tradovate(Data, Session, Assert.Single(Session.Accounts), "Tradovate rig");
            await Until(() => Broker.PositionReady && Broker.OrdersReady && Broker.Position.Volume == start, () => Broker.LastError);
        }

        public void Dispose()
        {
            Broker?.Dispose();
            Data.Dispose();
            Session.Dispose();
            Feed.Dispose();
            Fake.Dispose();
        }
    }

    /// <summary>Long 2 with a market sell of 1 still unanswered: an unconfirmed close never sends more than the position
    /// left if that sell executes.</summary>
    [Fact]
    public async Task Tradovate_unconfirmed_close_never_exceeds_the_position_left_by_an_unsettled_sell()
    {
        using var rig = new TradovateRig(2);
        var sellReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdSell = 1;
        rig.Intercept = async (_, arrival) =>
        {
            if (arrival.Action == "Sell" && Interlocked.Exchange(ref holdSell, 0) == 1) await sellReply.Task;
            return null;
        };
        rig.Session.SendTurnWait = TimeSpan.FromMilliseconds(100);
        await rig.StartAsync(_orders, 2);
        rig.Broker.CloseConfirmationWait = TimeSpan.FromMilliseconds(300);
        try
        {
            OrderActions.Manual(rig.Broker, OrderTypes.Market, OrderDirections.Sell, 1);
            await Until(() => rig.Placed("Sell").Length == 1, () => rig.Broker.LastError);
            OrderActions.Flatten(rig.Broker);
            await Until(() => rig.Placed("Sell").Length == 2, () => rig.Broker.LastError);
            Assert.Equal(1, rig.Placed("Sell")[1].Quantity); // at most 2 - 1, on the long side
            await Until(() => rig.Broker.LastError?.Contains(NativeSendTurns.UnsettledMessage, StringComparison.Ordinal) == true, () => rig.Broker.LastError);
        }
        finally { sellReply.TrySetResult(); }
    }

    /// <summary>A reverse waits for earlier replies without the reducing bound, so the earlier sell's outcome is made
    /// unknown (an unreadable 503 reply: the sell stays working): the reverse then refuses instead of guessing twice the
    /// position.</summary>
    [Fact]
    public async Task Tradovate_unconfirmed_reverse_is_refused_instead_of_guessing_twice_the_position()
    {
        using var rig = new TradovateRig(2);
        int lose = 1;
        rig.Intercept = (_, arrival) => Task.FromResult<(int, string)?>(
            arrival.Action == "Sell" && Interlocked.Exchange(ref lose, 0) == 1 ? (503, "{}") : null);
        await rig.StartAsync(_orders, 2);
        rig.Broker.CloseConfirmationWait = TimeSpan.FromMilliseconds(300);
        var sell = OrderActions.Manual(rig.Broker, OrderTypes.Market, OrderDirections.Sell, 1);
        OrderActions.Reverse(rig.Broker);
        await Until(() => rig.Broker.LastError?.Contains(NativeSendTurns.NotSentUnsettledMessage, StringComparison.Ordinal) == true,
            () => rig.Broker.LastError);
        Assert.Equal(OrderStates.Active, sell.State); // its outcome is still unknown
        Assert.Single(rig.Placed("Sell"));
        Assert.Empty(rig.Placed("Buy"));
    }

    /// <summary>A refused market sell in flight when Close position or Reverse is issued: the side and size come from the
    /// confirmed position in the order's send turn, not from the expected position at issue.</summary>
    [Theory]
    [InlineData(1, "close")]   // expected flat at issue: the close still closes the long 1
    [InlineData(2, "close")]   // expected short 1 at issue: the close sells the long 1 and reports the side change
    [InlineData(2, "reverse")] // expected short 1 at issue: the reverse is refused with the side change
    public async Task Tradovate_position_orders_choose_their_side_from_the_confirmed_position(int refusedSell, string operation)
    {
        using var rig = new TradovateRig(1);
        var refusal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int refuse = 1;
        rig.Intercept = async (_, arrival) =>
        {
            if (arrival.Action != "Sell" || Interlocked.Exchange(ref refuse, 0) != 1) return null;
            await refusal.Task;
            return (200, "{\"failureReason\":\"TradingLocked\",\"failureText\":\"Fixture refused the sell.\"}");
        };
        await rig.StartAsync(_orders, 1);
        try
        {
            OrderActions.Manual(rig.Broker, OrderTypes.Market, OrderDirections.Sell, refusedSell);
            if (operation == "close") OrderActions.Flatten(rig.Broker);
            else OrderActions.Reverse(rig.Broker);
        }
        finally { refusal.TrySetResult(); }

        if (operation == "close")
        {
            await Until(() => rig.Placed("Sell").Length == 2 && rig.Fake.NetPosition == 0 && rig.Broker.Position.Volume == 0,
                () => $"{rig.Broker.LastError}; net {rig.Fake.NetPosition}, sells {string.Join(", ", rig.Placed("Sell").Select(s => s.Quantity))}");
            Assert.Equal(1, rig.Placed("Sell")[1].Quantity);
            Assert.Empty(rig.Placed("Buy"));
            if (refusedSell == 2) Assert.Contains(NativeSendTurns.CloseSideChangedMessage, rig.Broker.LastError);
        }
        else
        {
            await Until(() => rig.Broker.LastError?.Contains(NativeSendTurns.ReverseSideChangedMessage, StringComparison.Ordinal) == true,
                () => rig.Broker.LastError);
            Assert.Single(rig.Placed("Sell"));
            Assert.Empty(rig.Placed("Buy"));
            Assert.Equal(1, rig.Fake.NetPosition);
        }
    }

    /// <summary>An amendment of an entry order (it adds exposure) waits for earlier replies without the reducing bound: a
    /// later bounded cancel has already given up waiting and been sent while the amendment still waits.</summary>
    [Fact]
    public async Task Tradovate_entry_amendment_waits_for_earlier_replies_without_the_reducing_bound()
    {
        using var rig = new TradovateRig(0);
        var buyReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdMarketBuy = 0, waitingAtMarker = -1;
        bool modifiedAtMarker = true;
        rig.Intercept = async (request, arrival) =>
        {
            if (arrival.Route == "/v1/order/placeorder" && arrival.Action == "Buy" &&
                JsonNode.Parse(request.Body)!["orderType"]!.GetValue<string>() == "Market" && Interlocked.Exchange(ref holdMarketBuy, 0) == 1)
                await buyReply.Task;
            if (arrival.Route == "/v1/order/cancelorder" && JsonNode.Parse(request.Body)!["orderId"]!.GetValue<long>() == 999_999)
            {
                modifiedAtMarker = rig.Arrivals().Any(a => a.Route == "/v1/order/modifyorder");
                waitingAtMarker = rig.Session.QueuedSends(101);
            }
            return null;
        };
        rig.Session.SendTurnWait = TimeSpan.FromMilliseconds(50);
        await rig.StartAsync(_orders, 0);
        var entry = OrderActions.Manual(rig.Broker, OrderTypes.Limit, OrderDirections.Buy, 1, 4000m);
        await Until(() => entry.ExchangeOrderId.Length > 0 && rig.Broker.ActiveOrders.Contains(entry), () => rig.Broker.LastError);
        try
        {
            Interlocked.Exchange(ref holdMarketBuy, 1);
            OrderActions.Manual(rig.Broker, OrderTypes.Market, OrderDirections.Buy, 1);
            await Until(() => rig.Placed("Buy").Length == 2, () => rig.Broker.LastError);
            OrderActions.Reprice(rig.Broker, entry, 4001m);
            // The marker cancel gives up its bounded wait behind the unanswered buy and is sent.
            Assert.Equal(NativeSendTurns.UnsettledMessage, await rig.Session.CancelAsync(101, 999_999, CancellationToken.None).WaitAsync(Deadline));
            Assert.False(modifiedAtMarker, "The entry amendment was sent while the earlier buy was unanswered.");
            Assert.Equal(1, waitingAtMarker);
        }
        finally { buyReply.TrySetResult(); }
        await Until(() => rig.Arrivals().Any(a => a.Route == "/v1/order/modifyorder"), () => rig.Broker.LastError);
    }

    private static async Task Until(Func<bool> condition, Func<string?> diagnostics)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "The native send-order fixture did not reach the expected state: " + (diagnostics() ?? "(no error)"));
    }
}
