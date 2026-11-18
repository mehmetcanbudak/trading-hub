using System.Globalization;
using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Tradovate send-turn order at the seams: an entry amendment whose order the user replay has not reported yet, and a
/// request that a Changed subscriber issues while a new order is published. Also an account whose first contract lookup
/// fails before it is ready.
/// </summary>
public sealed class TradovateReviewOrderPathTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const long UnreportedEntry = 424_242;
    private readonly NativeOrderTestFixture _orders = new();

    public void Dispose() => _orders.Dispose();

    private readonly record struct Arrival(string Route, string Action, string Type, int Quantity);

    /// <summary>A flat fake Tradovate account with an optional request interceptor, its session and one ES broker.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly List<Arrival> _arrivals = new();
        public FakeTradovate Fake { get; } = new() { TimestampPositions = true };
        public TradovateFeed Feed { get; }
        public TradovateTradingSession Session { get; }
        public InstrumentData Data { get; }
        public TradovateBroker Broker { get; private set; } = null!;

        /// <summary>Returns a reply to intercept the request, or null to let the fake venue answer it.</summary>
        public Func<FakeRequest, Arrival, Task<(int Status, string Body)?>>? Intercept { get; set; }

        public Rig()
        {
            var normalHttp = Fake.Server.OnHttp!;
            Fake.Server.OnHttpAsync = async (request, _) =>
            {
                if (request.Route.StartsWith("/v1/order/", StringComparison.Ordinal))
                {
                    var body = request.Body.Length > 0 ? JsonNode.Parse(request.Body) : null;
                    var arrival = new Arrival(request.Route, body?["action"]?.GetValue<string>() ?? "",
                        body?["orderType"]?.GetValue<string>() ?? "", body?["orderQty"]?.GetValue<int>() ?? 0);
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

        public Arrival[] Arrivals()
        {
            lock (_arrivals) return _arrivals.ToArray();
        }

        public Arrival[] Placed() => Arrivals().Where(arrival => arrival.Route == "/v1/order/placeorder").ToArray();

        /// <summary>Accepts a limit order without reporting it on the user channel, so an amendment of it is not sendable
        /// until the test reports it.</summary>
        public static Task<(int Status, string Body)?> AcceptLimitsUnreported(FakeRequest request, Arrival arrival) =>
            Task.FromResult<(int, string)?>(arrival is { Route: "/v1/order/placeorder", Type: "Limit" }
                ? (200, $"{{\"failureReason\":\"Success\",\"failureText\":\"\",\"orderId\":{UnreportedEntry}}}")
                : null);

        public async Task StartAsync(NativeOrderTestFixture orders)
        {
            Session.Start();
            await Until(() => Session.SnapshotReady, () => Session.StatusText);
            Broker = orders.Tradovate(Data, Session, Assert.Single(Session.Accounts), "Tradovate review rig");
            await Until(() => Broker.PositionReady && Broker.OrdersReady && Broker.Position.Volume == 0, () => Broker.LastError);
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

    /// <summary>A working limit buy from flat (an entry) that the venue accepted and the user replay has not reported.</summary>
    private static async Task<Order> PlaceUnreportedEntryAsync(Rig rig)
    {
        var entry = OrderActions.Manual(rig.Broker, OrderTypes.Limit, OrderDirections.Buy, 1, 4000m);
        await Until(() => entry.ExchangeOrderId == UnreportedEntry.ToString(CultureInfo.InvariantCulture), () => rig.Broker.LastError);
        await rig.Session.SendsIdle.WaitAsync(Deadline);
        Assert.DoesNotContain(rig.Session.OrdersOf(101, 555), state => state.Id == UnreportedEntry);
        return entry;
    }

    private static async Task ReportEntryAsync(Rig rig, Order entry)
    {
        await rig.Fake.PublishVersion(new JsonObject { ["id"] = 77, ["orderId"] = UnreportedEntry, ["orderQty"] = 1, ["orderType"] = "Limit", ["price"] = 4000m });
        await rig.Fake.PublishOrder(new JsonObject
        {
            ["id"] = UnreportedEntry, ["accountId"] = 101, ["contractId"] = 555, ["clOrdId"] = entry.ClientOrderId,
            ["action"] = "Buy", ["ordStatus"] = "Working", ["timestamp"] = DateTime.UtcNow.ToString("O"),
        });
    }

    /// <summary>
    /// An amendment of an entry whose order the replay has not reported yet keeps its send turn while it polls for it: a
    /// market buy issued after the amendment still waits for it once the amendment found its order missing, and reaches
    /// the venue only after the amendment.
    /// </summary>
    [Fact]
    public async Task Tradovate_entry_amendment_of_an_unreported_order_keeps_later_requests_behind_it()
    {
        using var rig = new Rig { Intercept = Rig.AcceptLimitsUnreported };
        await rig.StartAsync(_orders);
        var entry = await PlaceUnreportedEntryAsync(rig);

        // Task.Run captures this flag, so only the amendment's own task raises Changed with it set: its polls.
        var amendmentFlow = new AsyncLocal<bool>();
        int armed = 0, polls = 0, queuedAtPoll = -1;
        var polledAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Broker.Changed += () =>
        {
            // The second poll after the later buy was issued follows a whole look for the order that found it missing.
            if (!amendmentFlow.Value || Volatile.Read(ref armed) == 0 || Interlocked.Increment(ref polls) != 2) return;
            Volatile.Write(ref queuedAtPoll, rig.Broker.QueuedRequests);
            polledAgain.TrySetResult();
        };
        amendmentFlow.Value = true;
        OrderActions.Reprice(rig.Broker, entry, 4001m);
        amendmentFlow.Value = false;
        OrderActions.Manual(rig.Broker, OrderTypes.Market, OrderDirections.Buy, 1);
        Volatile.Write(ref armed, 1);

        await polledAgain.Task.WaitAsync(Deadline);
        Assert.True(Volatile.Read(ref queuedAtPoll) == 1,
            $"The later buy stopped waiting for the unsent entry amendment ({Volatile.Read(ref queuedAtPoll)} queued).");
        Assert.DoesNotContain(rig.Placed(), arrival => arrival.Type == "Market");

        await ReportEntryAsync(rig, entry);
        await Until(() => rig.Placed().Any(arrival => arrival.Type == "Market"), () => rig.Broker.LastError);
        var sent = rig.Arrivals();
        int amended = Array.FindIndex(sent, arrival => arrival.Route == "/v1/order/modifyorder");
        int later = Array.FindIndex(sent, arrival => arrival is { Route: "/v1/order/placeorder", Type: "Market" });
        Assert.True(amended >= 0 && amended < later, "The later buy reached the venue before the entry amendment: " +
            string.Join(", ", sent.Select(arrival => $"{arrival.Route} {arrival.Type}")));
        await Until(() => !rig.Broker.IsReplacementPending(entry) && entry.Price == 4001m, () => rig.Broker.LastError);
    }

    /// <summary>Close position cancels an entry whose amendment holds its turn for the replay: the cancel ends that wait,
    /// so the cancel is sent in order (not after its reducing bound) and nothing is amended.</summary>
    [Fact]
    public async Task Tradovate_close_position_ends_a_held_entry_amendment_without_waiting_for_its_bound()
    {
        using var rig = new Rig { Intercept = Rig.AcceptLimitsUnreported };
        await rig.StartAsync(_orders);
        var entry = await PlaceUnreportedEntryAsync(rig);
        OrderActions.Reprice(rig.Broker, entry, 4001m);
        OrderActions.Flatten(rig.Broker);

        await Until(() => entry.Canceled && rig.Arrivals().Any(arrival => arrival.Route == "/v1/order/cancelorder"), () => rig.Broker.LastError);
        await rig.Session.SendsIdle.WaitAsync(Deadline);
        Assert.DoesNotContain(rig.Arrivals(), arrival => arrival.Route == "/v1/order/modifyorder");
        Assert.False(rig.Broker.IsReplacementPending(entry));
        Assert.DoesNotContain(NativeSendTurns.UnsettledMessage, rig.Broker.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A reducing request behind an entry amendment that holds its turn (an Accounts-page liquidation) is not
    /// held by it indefinitely: it is sent after its bound and says the earlier outcome was unsettled.</summary>
    [Fact]
    public async Task Tradovate_liquidation_behind_a_held_entry_amendment_is_sent_after_its_bound()
    {
        using var rig = new Rig { Intercept = Rig.AcceptLimitsUnreported };
        rig.Session.SendTurnWait = TimeSpan.FromMilliseconds(200);
        await rig.StartAsync(_orders);
        var entry = await PlaceUnreportedEntryAsync(rig);
        OrderActions.Reprice(rig.Broker, entry, 4001m);

        Assert.Equal(NativeSendTurns.UnsettledMessage, await rig.Session.LiquidateAsync(101, 555, CancellationToken.None).WaitAsync(Deadline));
        Assert.Contains(rig.Arrivals(), arrival => arrival.Route == "/v1/order/liquidateposition");
        Assert.DoesNotContain(rig.Arrivals(), arrival => arrival.Route == "/v1/order/modifyorder");
        Assert.True(rig.Broker.IsReplacementPending(entry));
    }

    /// <summary>A Changed subscriber that issues an order when a new order is first published: the new order's request
    /// is sent first, and the subscriber's waits for its reply.</summary>
    [Fact]
    public async Task Tradovate_request_a_changed_subscriber_issues_on_a_new_order_is_sent_after_it()
    {
        using var rig = new Rig();
        var limitReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Intercept = async (_, arrival) =>
        {
            if (arrival is { Route: "/v1/order/placeorder", Type: "Limit" }) await limitReply.Task;
            return null;
        };
        await rig.StartAsync(_orders);
        int armed = 1;
        rig.Broker.Changed += () =>
        {
            if (rig.Broker.ActiveOrders.Any(order => order.Comment == "Published") && Interlocked.Exchange(ref armed, 0) == 1)
                OrderActions.Manual(rig.Broker, OrderTypes.Market, OrderDirections.Buy, 1, comment: "Reaction");
        };
        try
        {
            OrderActions.Manual(rig.Broker, OrderTypes.Limit, OrderDirections.Buy, 1, 4000m, "Published");
            Assert.Equal(0, Volatile.Read(ref armed)); // the subscriber reacted while the limit order was published
            await Until(() => rig.Placed().Any(arrival => arrival.Type == "Limit"), () => rig.Broker.LastError);
            Assert.Equal(new[] { "Limit" }, rig.Placed().Select(arrival => arrival.Type));
            Assert.Equal(1, rig.Broker.QueuedRequests); // the reaction waits for the limit order's reply
        }
        finally { limitReply.TrySetResult(); }

        await Until(() => rig.Placed().Length == 2, () => rig.Broker.LastError);
        Assert.Equal(new[] { "Limit", "Market" }, rig.Placed().Select(arrival => arrival.Type));
    }

    /// <summary>The account's first contract lookup fails (here before its session connected): the session's next change
    /// looks it up again, so the account becomes ready without any order, and the stale lookup error is cleared.</summary>
    [Fact]
    public async Task Tradovate_contract_lookup_that_failed_once_is_retried_without_an_order()
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        int lookups = 0;
        fake.Server.OnHttp = request => request.Route == "/v1/contract/find" && Interlocked.Increment(ref lookups) == 1
            ? (503, "Contract service unavailable")
            : normalHttp(request);
        using var data = new InstrumentData(new Instrument
        {
            Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD",
        }, new SimulatedFeed()) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        // Created before the session's replay names the contract, so the broker looks it up over REST.
        using var broker = _orders.Tradovate(data, session, new TradovateAccount(101, "DEMO101", true), "Tradovate contract retry");
        await Until(() => broker.LastError?.StartsWith("Could not determine Tradovate's active contract", StringComparison.Ordinal) == true,
            () => broker.LastError);
        Assert.False(broker.PositionReady);

        session.Start();
        await Until(() => broker.PositionReady && broker.OrdersReady, () => $"{broker.LastError}; lookups {Volatile.Read(ref lookups)}");
        Assert.DoesNotContain("Could not determine Tradovate's active contract", broker.LastError ?? "", StringComparison.Ordinal);
        lock (fake.Orders) Assert.Empty(fake.Orders);
    }

    private static async Task Until(Func<bool> condition, Func<string?> diagnostics)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "The Tradovate review fixture did not reach the expected state: " + (diagnostics() ?? "(no error)"));
    }
}
