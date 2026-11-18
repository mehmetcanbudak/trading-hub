using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Opt-in exchange-side protection through Tradovate's documented OCO strategy (POST /order/placeoco) against the owned
/// fake transport: wire shape, durable per-leg client identities, venue sibling cancellation, refusal and unknown-reply
/// fallbacks, and restart re-ownership. Fake-venue conformance only; a demo account must confirm the real behaviour.
/// </summary>
public sealed class TradovateNativeProtectionTests : IDisposable
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 50 };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-native-protection-" + Guid.NewGuid().ToString("N"));
    private readonly string _contextId = Guid.NewGuid().ToString("N");

    private OrderContext Context()
    {
        var context = new OrderContext(new AccountContextDescriptor("", "Tradovate", "futures", "unconfigured", "", ""));
        context.Configure(new AccountContextDescriptor(_contextId, "Tradovate", "futures", "owned-loopback-fixture", _contextId, "fixture-generation"),
            Path.Combine(_root, _contextId));
        return context;
    }

    private static BracketManager Brackets(ITradingAccount account) => new(account)
    {
        Template = new BracketTemplate
        {
            Enabled = true, StopTicks = 20, BreakevenAfterFirstTarget = false,
        },
    };

    private static async Task Until(Func<bool> condition, Func<string>? diagnostics = null)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > end) Assert.Fail("Condition not reached. " + diagnostics?.Invoke());
            await Task.Delay(20);
        }
    }

    private static int Count(FakeTradovate fake, string route)
    {
        lock (fake.Orders) return fake.Orders.Count(o => route == "placeoco" ? o["other"] != null : o["other"] == null && o["orderId"] == null);
    }

    private static async Task<(TradovateFeed Feed, TradovateTradingSession Session, TradovateAccount Account)> Connect(FakeTradovate fake)
    {
        var feed = new TradovateFeed(() => fake.Settings());
        var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.Accounts.Count == 1 && session.SnapshotReady);
        return (feed, session, Assert.Single(session.Accounts));
    }

    private static void UseTargetOnly(BracketManager brackets)
    {
        brackets.Template.Targets.Clear();
        brackets.Template.Targets.Add(new BracketTarget { Ticks = 40, Percent = 100 });
    }

    [Fact]
    public async Task Exchange_side_pair_is_one_placeoco_with_both_client_ids_and_the_venue_cancels_the_sibling()
    {
        using var fake = new FakeTradovate();
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        using var brackets = Brackets(broker);
        UseTargetOnly(brackets);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => brackets.Mode == ProtectionMode.ExchangeSide, () => $"mode {brackets.Mode}; error {broker.LastError}");

        var stop = brackets.StopLoss!;
        var target = Assert.Single(brackets.Targets);
        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder")); // only the entry
        JsonObject oco;
        lock (fake.Orders) oco = fake.Orders.Single(o => o["other"] != null);
        Assert.Equal("Sell", oco["action"]!.GetValue<string>());
        Assert.Equal("Stop", oco["orderType"]!.GetValue<string>());
        Assert.Equal(4995.25m, oco["stopPrice"]!.GetValue<decimal>());
        Assert.Equal(2, oco["orderQty"]!.GetValue<int>());
        Assert.Equal(stop.ClientOrderId, oco["clOrdId"]!.GetValue<string>());
        var other = oco["other"]!.AsObject();
        Assert.Equal("Sell", other["action"]!.GetValue<string>());
        Assert.Equal("Limit", other["orderType"]!.GetValue<string>());
        Assert.Equal(5010.25m, other["price"]!.GetValue<decimal>());
        Assert.Equal(target.ClientOrderId, other["clOrdId"]!.GetValue<string>());
        Assert.NotEqual(stop.ClientOrderId, target.ClientOrderId);
        Assert.Equal(broker.ProtectionGroupOf(stop), broker.ProtectionGroupOf(target));
        Assert.Equal(broker.OrderContext.ManualOwner.Id, stop.OwnerId);
        Assert.Equal(broker.OrderContext.ManualOwner.Id, target.OwnerId);

        await fake.FillOcoLeg(fake.OcoLegFor(target.ClientOrderId!));
        await Until(() => broker.Position.Volume == 0 && stop.Canceled && target.Status == OrderStatus.Filled);
        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder"));
        Assert.Equal(ProtectionMode.None, brackets.Mode);
    }

    [Fact]
    public async Task Switched_off_sends_the_same_pair_as_two_ordinary_orders()
    {
        using var fake = new FakeTradovate();
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context());
        using var brackets = Brackets(broker);
        UseTargetOnly(brackets);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => brackets.StopLoss?.ExchangeOrderId.Length > 0 && brackets.Targets.Count == 1 && brackets.Targets[0].ExchangeOrderId.Length > 0);

        Assert.Equal(NativeProtectionFeatures.None, broker.NativeProtection);
        Assert.Equal(0, fake.OcoRequests);
        Assert.Equal(3, Count(fake, "placeorder"));
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode);
        Assert.Null(brackets.ProtectionNote);
    }

    [Fact]
    public async Task A_refused_placeoco_falls_back_to_app_managed_orders_with_a_visible_reason()
    {
        using var fake = new FakeTradovate();
        var normal = fake.Server.OnHttp!;
        fake.Server.OnHttp = request => request.Route == "/v1/order/placeoco"
            ? Refuse(fake, request)
            : normal(request);
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        using var brackets = Brackets(broker);
        UseTargetOnly(brackets);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => Count(fake, "placeorder") == 3 && brackets.StopLoss?.ExchangeOrderId.Length > 0 &&
            brackets.Targets.Count == 1 && brackets.Targets[0].ExchangeOrderId.Length > 0, () => $"note {brackets.ProtectionNote}; error {broker.LastError}");

        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(NativeProtectionFeatures.None, broker.NativeProtection);
        Assert.Contains("Unsupported", broker.NativeProtectionFault);
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode);
        Assert.Contains("refused", brackets.ProtectionNote);
        Assert.Equal(2, brackets.StopLoss!.Unfilled);
        Assert.Equal(4995.25m, brackets.StopLoss!.TriggerPrice);
    }

    private static (int, string) Refuse(FakeTradovate fake, FakeRequest request)
    {
        lock (fake.Orders) fake.Orders.Add(JsonNode.Parse(request.Body)!.AsObject());
        Interlocked.Increment(ref fake.OcoRequests);
        return (200, "{\"failureReason\":\"Unsupported\",\"failureText\":\"\"}");
    }

    [Fact]
    public async Task An_unknown_placeoco_reply_is_never_resent_and_both_legs_bind_by_client_id()
    {
        using var fake = new FakeTradovate();
        var normal = fake.Server.OnHttp!;
        fake.Server.OnHttp = request =>
        {
            if (request.Route != "/v1/order/placeoco") return normal(request);
            normal(request); // the venue accepted it ...
            return (502, "Upstream response lost"); // ... but the reply was lost
        };
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        using var brackets = Brackets(broker);
        UseTargetOnly(brackets);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => brackets.Mode == ProtectionMode.ExchangeSide, () => $"mode {brackets.Mode}; error {broker.LastError}");

        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder"));
        Assert.Null(broker.NativeProtectionFault);
        Assert.True(broker.OrdersReady);
        Assert.DoesNotContain("acceptance is unknown", broker.LastError ?? "");
    }

    [Fact]
    public async Task A_nested_link_scope_is_a_no_op_and_never_throws()
    {
        using var fake = new FakeTradovate();
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        var owner = broker.OrderContext.ManualOwner;

        using var outer = broker.LinkProtection(owner);
        using var inner = broker.LinkProtection(owner);
        Assert.NotNull(outer);
        Assert.Null(inner); // a nested scope links nothing, so its legs are never marked as linked
    }

    [Fact]
    public async Task A_strategy_leg_submitted_inside_a_chart_scope_is_never_paired_with_a_chart_leg()
    {
        using var fake = new FakeTradovate();
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => broker.Position.Volume == 2);

        Order chartStop, strategyTarget;
        using (broker.LinkProtection(broker.OrderContext.ManualOwner))
        {
            chartStop = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 2, 4995.25m, "SL", reduceOnly: true);
            // A runner reacting on the same thread to this submission's account change.
            strategyTarget = OrderActions.Place(broker, new OrderOwner("strategy-fixture", Kind: OrderOwnerKind.Strategy),
                OrderTypes.Limit, OrderDirections.Sell, 2, 5010.25m, "TP", reduceOnly: true, protective: true);
        }
        await Until(() => chartStop.ExchangeOrderId.Length > 0 && strategyTarget.ExchangeOrderId.Length > 0);

        Assert.Equal(0, fake.OcoRequests);
        Assert.Equal(3, Count(fake, "placeorder"));
        Assert.Null(broker.ProtectionGroupOf(chartStop));
        Assert.Null(broker.ProtectionGroupOf(strategyTarget));
    }

    [Fact]
    public async Task Cancel_requests_are_kept_while_the_oco_partner_works_and_forgotten_one_sync_after_both_end()
    {
        using var fake = new FakeTradovate();
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => broker.Position.Volume == 2);
        Order stop, target;
        using (broker.LinkProtection(broker.OrderContext.ManualOwner))
        {
            stop = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 2, 4995.25m, "SL", reduceOnly: true);
            target = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 2, 5010.25m, "TP", reduceOnly: true);
        }
        await Until(() => broker.ProtectionGroupOf(stop) != null && broker.ProtectionGroupOf(target) != null);

        broker.Cancel(stop);
        await Until(() => stop.Canceled);
        broker.Cancel(target); // its own sync runs while the stop has ended and the partner still works
        await Until(() => target.Canceled);
        Assert.True(broker.CancelRequested(stop));
        Assert.True(broker.CancelRequested(target));

        await fake.PublishPosition(new JsonObject { ["id"] = 1, ["accountId"] = 101, ["contractId"] = 555, ["netPos"] = 2, ["netPrice"] = 5000.25m,
            ["bought"] = 2, ["boughtValue"] = 10000.5m, ["sold"] = 0, ["soldValue"] = 0 });
        await Until(() => !broker.CancelRequested(stop) && !broker.CancelRequested(target));
    }

    [Fact]
    public async Task The_cancel_request_cap_evicts_ended_orders_first_and_never_a_working_one()
    {
        using var fake = new FakeTradovate();
        var normal = fake.Server.OnHttp!;
        long held = 0;
        fake.Server.OnHttp = request =>
        {
            // The second cancellation is accepted but never reported: that order stays working.
            if (request.Route == "/v1/order/cancelorder" && JsonNode.Parse(request.Body)!["orderId"]!.GetValue<long>() == Interlocked.Read(ref held))
                return (200, $"{{\"failureReason\":\"Success\",\"commandId\":{held}}}");
            return normal(request);
        };
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true)
        {
            CancelRequestMemory = 1,
        };
        var first = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 4990);
        var second = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 4985);
        await Until(() => first.ExchangeOrderId.Length > 0 && second.ExchangeOrderId.Length > 0 &&
            broker.ActiveOrders.Contains(first) && broker.ActiveOrders.Contains(second));
        Interlocked.Exchange(ref held, long.Parse(second.ExchangeOrderId, System.Globalization.CultureInfo.InvariantCulture));

        broker.Cancel(second);
        broker.Cancel(first);
        await Until(() => first.Canceled);

        Assert.False(broker.CancelRequested(first)); // over the cap: the ended entry goes at once
        Assert.True(broker.CancelRequested(second)); // a working order's request is never evicted
        Assert.True(second.State == OrderStates.Active);
    }

    [Fact]
    public async Task A_position_is_never_proven_to_include_executions_without_dated_fill_reports()
    {
        using var fake = new FakeTradovate();
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        long id = long.Parse(order.ExchangeOrderId, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(broker.PositionReflects(order)); // nothing executed yet

        // Executed, but no fill entity has arrived yet.
        await fake.PublishOrder(new JsonObject { ["id"] = id, ["accountId"] = 101, ["contractId"] = 555, ["action"] = "Buy",
            ["ordStatus"] = "Filled", ["timestamp"] = "2026-11-15T12:00:00Z" });
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.False(broker.PositionReflects(order));

        // A fill without a timestamp, and a dated position report: still unprovable.
        await fake.PublishFill(new JsonObject { ["id"] = 9001, ["orderId"] = id, ["contractId"] = 555, ["action"] = "Buy",
            ["qty"] = 1, ["price"] = 4990m, ["active"] = true });
        await fake.PublishPosition(new JsonObject { ["id"] = 1, ["accountId"] = 101, ["contractId"] = 555, ["netPos"] = 1,
            ["netPrice"] = 4990m, ["bought"] = 1, ["boughtValue"] = 4990m, ["sold"] = 0, ["soldValue"] = 0, ["timestamp"] = "2026-11-15T12:00:05Z" });
        await Until(() => broker.Position.Volume == 1);
        Assert.False(broker.PositionReflects(order));

        // Once the fill carries its venue timestamp, the later position report proves it.
        await fake.PublishFill(new JsonObject { ["id"] = 9001, ["orderId"] = id, ["contractId"] = 555, ["action"] = "Buy",
            ["qty"] = 1, ["price"] = 4990m, ["active"] = true, ["timestamp"] = "2026-11-15T12:00:01Z" });
        await Until(() => broker.PositionReflects(order));
    }

    /// <summary>The first process: a market entry whose exchange-side pair the venue acknowledges, then the app stops
    /// with the position and both linked legs still working at the venue.</summary>
    private async Task<(string Stop, string Target)> ProtectAndStop(FakeTradovate fake)
    {
        (string Stop, string Target) protection;
        {
            var (feed, session, account) = await Connect(fake);
            using var _f = feed;
            using var _s = session;
            using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
            using var brackets = Brackets(broker);
            UseTargetOnly(brackets);
            OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
            await Until(() => brackets.Mode == ProtectionMode.ExchangeSide);
            protection = (brackets.StopLoss!.ClientOrderId!, brackets.Targets[0].ClientOrderId!);
        }
        fake.ReplayPositions.Add(JsonNode.Parse("{\"id\":1,\"accountId\":101,\"contractId\":555,\"netPos\":2,\"netPrice\":5000.25,\"bought\":2,\"boughtValue\":10000.5,\"sold\":0,\"soldValue\":0}"));
        return protection;
    }

    private static async Task AssertAdoptedWithoutSendingAgain(FakeTradovate fake, TradovateBroker restarted, BracketManager adopted, string stopClient, string targetClient)
    {
        var stop = adopted.StopLoss!;
        var target = Assert.Single(adopted.Targets);
        Assert.Equal(stopClient, stop.ClientOrderId);
        Assert.Equal(targetClient, target.ClientOrderId);
        Assert.Equal(restarted.OrderContext.ManualOwner.Id, stop.OwnerId);
        Assert.Equal(restarted.OrderContext.ManualOwner.Id, target.OwnerId);
        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder"));

        await fake.FillOcoLeg(fake.OcoLegFor(stopClient));
        await Until(() => restarted.Position.Volume == 0 && target.Canceled);
        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder"));
    }

    [Fact]
    public async Task A_restarted_app_re_owns_and_adopts_the_working_oco_without_sending_again()
    {
        using var fake = new FakeTradovate();
        var (stopClient, targetClient) = await ProtectAndStop(fake);

        var (feed2, session2, account2) = await Connect(fake);
        using var _f2 = feed2;
        using var _s2 = session2;
        using var restarted = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session2, account2, "DEMO101", Context(), () => true);
        using var adopted = Brackets(restarted);
        UseTargetOnly(adopted);
        await Until(() => adopted.Mode == ProtectionMode.ExchangeSide, () => $"mode {adopted.Mode}; error {restarted.LastError}; position {restarted.Position.Volume}");
        await AssertAdoptedWithoutSendingAgain(fake, restarted, adopted, stopClient, targetClient);
    }

    /// <summary>
    /// The interleaving the restart test hit under load: the restarted account finishes its replay and raises its last
    /// change before the protection manager exists. A quiet account raises no later change, so the manager must
    /// reconcile when it is created, not on the next account event.
    /// </summary>
    [Fact]
    public async Task A_protection_manager_created_after_the_restarted_account_settled_adopts_its_working_oco_at_once()
    {
        using var fake = new FakeTradovate();
        var (stopClient, targetClient) = await ProtectAndStop(fake);

        var (feed2, session2, account2) = await Connect(fake);
        using var _f2 = feed2;
        using var _s2 = session2;
        using var restarted = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session2, account2, "DEMO101", Context(), () => true);
        string manual = restarted.OrderContext.ManualOwner.Id;
        bool Settled() => restarted is { PositionReady: true, OrdersReady: true } && restarted.Position.Volume == 2 &&
            restarted.ActiveOrders.Count(o => o.OwnerId == manual && restarted.ProtectionGroupOf(o) != null) == 2;
        // Wait for the account change that reports the settled replay to finish: its handlers have all run.
        var settledChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() { if (Settled()) settledChange.TrySetResult(); }
        restarted.Changed += OnChanged;
        try
        {
            OnChanged();
            await settledChange.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { restarted.Changed -= OnChanged; }

        using var adopted = Brackets(restarted);
        UseTargetOnly(adopted);
        Assert.Equal(ProtectionMode.ExchangeSide, adopted.Mode);
        await AssertAdoptedWithoutSendingAgain(fake, restarted, adopted, stopClient, targetClient);
    }

    /// <summary>Another process holding a journal record exclusively, as backup or antivirus software can: reads of it fail
    /// until the returned handle is closed.</summary>
    private FileStream HoldClientIndex(string clientOrderId)
    {
        string name = OrderContext.Hash(clientOrderId) + ".json";
        string path = Directory.EnumerateFiles(Path.Combine(_root, _contextId), name, SearchOption.AllDirectories)
            .Single(file => Path.GetFileName(Path.GetDirectoryName(file)) == "client");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    /// <summary>
    /// A transient journal error while the restarted account first checks its linked legs (their client index records
    /// are held by another process): each leg is checked again on the next sync and re-owned then, and the adopted pair
    /// is never sent again.
    /// </summary>
    [Fact]
    public async Task A_restarted_app_re_owns_its_oco_after_a_transient_journal_error()
    {
        using var fake = new FakeTradovate();
        var (stopClient, targetClient) = await ProtectAndStop(fake);

        var (feed2, session2, account2) = await Connect(fake);
        using var _f2 = feed2;
        using var _s2 = session2;
        var context = Context();
        using var heldStop = HoldClientIndex(stopClient);
        using var heldTarget = HoldClientIndex(targetClient);
        using var restarted = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session2, account2, "DEMO101", context, () => true);
        string manual = restarted.OrderContext.ManualOwner.Id;
        int OwnedLegs() => restarted.ActiveOrders.Count(o => o.OwnerId == manual && restarted.ProtectionGroupOf(o) != null);
        await Until(() => restarted.LastError?.Contains("could not be re-owned after restart", StringComparison.Ordinal) == true,
            () => $"error {restarted.LastError}");
        Assert.Equal(0, OwnedLegs());

        heldStop.Dispose();
        heldTarget.Dispose();
        // Any later change syncs the account; this one repeats the position the venue holds.
        await fake.PublishPosition(new JsonObject { ["id"] = 1, ["accountId"] = 101, ["contractId"] = 555, ["netPos"] = 2, ["netPrice"] = 5000.25m,
            ["bought"] = 2, ["boughtValue"] = 10000.5m, ["sold"] = 0, ["soldValue"] = 0 });
        await Until(() => OwnedLegs() == 2, () => $"owned {OwnedLegs()}; error {restarted.LastError}");
        Assert.DoesNotContain("could not be re-owned", restarted.LastError ?? "", StringComparison.Ordinal);

        using var adopted = Brackets(restarted);
        UseTargetOnly(adopted);
        await Until(() => adopted.Mode == ProtectionMode.ExchangeSide, () => $"mode {adopted.Mode}; error {restarted.LastError}");
        await AssertAdoptedWithoutSendingAgain(fake, restarted, adopted, stopClient, targetClient);
    }

    public void Dispose()
    {
        // Execution projection outlives the adapters and may still write their journals; delete after it settles.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!AccountExecutionProjector.BackgroundWorkIdle && DateTime.UtcNow < deadline) Thread.Sleep(20);
        if (!AccountExecutionProjector.BackgroundWorkIdle)
            throw new InvalidOperationException("Execution projection did not settle before journal cleanup.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
