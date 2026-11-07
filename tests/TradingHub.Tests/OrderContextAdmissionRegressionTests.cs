using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class OrderContextAdmissionRegressionTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-admission-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = LiveStore.Root;

    public OrderContextAdmissionRegressionTests() => LiveStore.Root = _root;

    public void Dispose()
    {
        LiveStore.Root = _previousRoot;
        // Execution projection outlives its runner by design and may still be reading a ledger. Windows refuses
        // to delete an open file, so cleanup waits for that real work instead of racing it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!AccountExecutionProjector.BackgroundWorkIdle && DateTime.UtcNow < deadline) Thread.Sleep(20);
        if (!AccountExecutionProjector.BackgroundWorkIdle)
            throw new InvalidOperationException("Execution projection did not settle before ledger cleanup.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Quantity_cap_lowered_during_native_replay_rejects_queued_strategy_exposure_before_transport()
    {
        var replayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, OrderReplayRelease = replayRelease };
        var profile = new ConnectionProfile
        {
            Id = "quantity-cap-fixture", Kind = ConnectionKind.Rithmic, Name = "Quantity cap fixture",
            Enabled = true, AllowRealOrders = true, MaxOrderQuantity = 2,
            TradingNamespace = Guid.NewGuid().ToString("N"), TradingGeneration = Guid.NewGuid().ToString("N"),
            RithmicUri = server.Uri, RithmicSystem = "Rithmic 01", RithmicUser = "demo",
        };
        var services = SearchZoomFixture.Services();
        services.Connections.Profiles.Add(profile);
        InitializeField(services.Connections, "_tradingRoutes");
        services.Connections.EnsureTradingIdentity(profile);
        InitializeField(services, "_orderContexts");
        InitializeField(services, "_wireAccounts");
        InitializeField(services, "_brokerOrigins");
        SearchZoomFixture.Set(services, "_executionRoot", _root);
        string accountId = AppServices.RithmicAccountId(profile, "ACC1");
        var context = (OrderContext)typeof(AppServices).GetMethod("TradingContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(services, new object?[] { profile, accountId, "Rithmic", "futures", server.Uri, null, null })!;
        using var data = Data("ES");
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = profile.RithmicSystem, User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        using var broker = new RithmicBroker(data, session, "ACC1", "Quantity cap fixture",
            _ => Task.FromResult(("ESZ6", "CME")), context);
        var routes = (ConcurrentDictionary<(string Context, string Instrument), WeakReference<ITradingAccount>>)
            typeof(AppServices).GetField("_wireAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(services)!;
        routes[(context.Descriptor.Id, data.Instrument.Key)] = new(broker);
        using var runner = new LiveStrategyRunner(new ProtectedStrategy { StopLossTicks = 0, TakeProfitTicks = 0 },
            data, broker, ChartPeriod.M1)
        {
            AccountId = accountId,
            Guard = (ILiveOrderGuard)typeof(AppServices).GetProperty("OrderGuard", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(services)!,
        };
        session.Start();
        try
        {
            await server.OrderReplayRequested.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => broker.PositionReady);
            Assert.True(broker.IsReal);
            Assert.False(broker.OrdersReady);
            Assert.Equal(0m, broker.Position.Volume);
            runner.Start();
            var queued = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 2, 98, "Queued cap entry");
            Assert.Equal(StrategyOrderState.Active, queued.State);
            var queuedNative = Assert.Single(broker.ActiveOrders);
            Assert.Equal(2m, queuedNative.QuantityToFill);
            Assert.Equal(context.Descriptor.Id, queuedNative.ContextId);
            Assert.NotEqual(context.ManualOwner.Id, queuedNative.OwnerId);
            Assert.Empty(queuedNative.ExchangeOrderId);
            lock (server.Tags) Assert.Empty(server.Tags);

            profile.MaxOrderQuantity = 1;
            replayRelease.TrySetResult();
            await Until(() => queued.State == StrategyOrderState.Canceled || queuedNative.ExchangeOrderId.Length > 0,
                () => $"Queued cap entry did not settle: native={queuedNative.Status}; strategy={queued.State}; error={broker.LastError}");

            var control = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1, 97, "Whole-quantity cap control");
            var controlNative = Assert.Single(broker.ActiveOrders, order => !ReferenceEquals(order, queuedNative));
            await Until(() => controlNative.ExchangeOrderId.Length > 0);
            Assert.Equal(StrategyOrderState.Active, control.State);
            Assert.Equal(1m, controlNative.QuantityToFill);
            Assert.Equal(context.Descriptor.Id, controlNative.ContextId);
            lock (server.Tags)
            {
                Assert.Contains(controlNative.ClientOrderId, server.Tags);
                Assert.DoesNotContain(queuedNative.ClientOrderId, server.Tags);
                Assert.Single(server.Tags);
            }
            Assert.Equal(OrderStates.Failed, queuedNative.State);
            Assert.Equal(OrderStatus.Rejected, queuedNative.Status);
            Assert.Equal(StrategyOrderState.Canceled, queued.State);
            Assert.Empty(queuedNative.ExchangeOrderId);
        }
        finally { replayRelease.TrySetResult(); }
    }

    [Fact]
    public async Task Native_fill_before_position_keeps_required_protection_pending_until_the_actual_app_route_reconciles()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        var profile = new ConnectionProfile
        {
            Id = "owned-real-fixture", Kind = ConnectionKind.Rithmic, Name = "Owned REAL fixture",
            Enabled = true, AllowRealOrders = true, TradingNamespace = Guid.NewGuid().ToString("N"),
            TradingGeneration = Guid.NewGuid().ToString("N"), RithmicUri = server.Uri,
            RithmicSystem = "Rithmic 01", RithmicUser = "demo",
        };
        // Reuse the existing constructor-bypass fixture: no app startup, profile file, secrets, sleep,
        // market feed or global runner subscription. The permission closure below is NOT a copy.
        var services = SearchZoomFixture.Services();
        services.Connections.Profiles.Add(profile);
        InitializeField(services.Connections, "_tradingRoutes");
        services.Connections.EnsureTradingIdentity(profile);
        InitializeField(services, "_orderContexts");
        InitializeField(services, "_wireAccounts");
        InitializeField(services, "_brokerOrigins");
        SearchZoomFixture.Set(services, "_executionRoot", _root);
        string accountId = AppServices.RithmicAccountId(profile, "ACC1");
        var context = (OrderContext)typeof(AppServices).GetMethod("TradingContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(services, new object?[] { profile, accountId, "Rithmic", "futures", server.Uri, null, null })!;
        using var data = Data("ES");
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = profile.RithmicSystem, User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        using var broker = new RithmicBroker(data, session, "ACC1", "Owned REAL fixture",
            _ => Task.FromResult(("ESZ6", "CME")), context);
        var routes = (ConcurrentDictionary<(string Context, string Instrument), WeakReference<ITradingAccount>>)
            typeof(AppServices).GetField("_wireAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(services)!;
        routes[(context.Descriptor.Id, data.Instrument.Key)] = new(broker);
        using var runner = new LiveStrategyRunner(new ProtectedStrategy(), data, broker, ChartPeriod.M1)
        {
            AccountId = accountId,
        };
        session.Start();
        await Until(() => broker.PositionReady && broker.OrdersReady);
        Assert.True(broker.IsReal);
        Assert.Equal(0m, broker.Position.Volume);
        runner.Start();
        var entry = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1, 99, "Owned entry");
        await Until(() => broker.ActiveOrders.Any(o => o.LogicalOrderId != null && o.ExchangeOrderId.Length > 0));
        var nativeEntry = Assert.Single(broker.ActiveOrders);
        Assert.Equal(1m, nativeEntry.QuantityToFill);
        Assert.Equal(context.Descriptor.Id, nativeEntry.ContextId);
        Assert.NotEqual(context.ManualOwner.Id, nativeEntry.OwnerId);

        var permissionObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permissionField = typeof(OrderContext).GetField("_permission", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var actualPermission = (Func<OrderRequest, string?>)permissionField.GetValue(context)!;
        permissionField.SetValue(context, (Func<OrderRequest, string?>)(request =>
        {
            string? decision = actualPermission(request);
            if (request.ReduceOnly) permissionObserved.TrySetResult();
            return decision;
        }));
        // Revocation is intentional: the eventual stop/target must be reductions through the real app
        // permission closure, not freshly permitted REAL exposure or an unowned bypass.
        profile.AllowRealOrders = false;
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PositionUpdateRelease = positionRelease;
        Task fill = server.FillOrder(nativeEntry.ExchangeOrderId, 1, 99);
        try
        {
            await server.PositionUpdateRequested.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => runner.OwnPosition == 1 && entry.State == StrategyOrderState.Filled);
            Assert.True(broker.PositionReady && broker.OrdersReady);
            Assert.Equal(0m, broker.Position.Volume);
            Assert.Equal(1, server.Position);
            Assert.Equal(1m, runner.Ledger.Position);
            // A future implementation may defer the permission check itself until reconciliation.
            // Hold the PnL barrier for the existing native-fixture deadline, or until the actual
            // closure has evaluated a protection request. No ordering is inferred from a short sleep.
            await Task.WhenAny(permissionObserved.Task, Task.Delay(TimeSpan.FromSeconds(8)));
            lock (server.Tags) Assert.Single(server.Tags); // no reduction may cross a replay-ready flat snapshot
        }
        finally { positionRelease.TrySetResult(); }
        await fill.WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => broker.Position.Volume == 1 && broker.ActiveOrders.Count(o =>
            o.ReduceOnly && o.ExchangeOrderId.Length > 0 && o.OwnerId == nativeEntry.OwnerId) == 2);
        var stop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        var target = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Limit);
        foreach (var protection in new[] { stop, target })
        {
            Assert.Equal(1m, protection.Unfilled);
            Assert.True(protection.ReduceOnly);
            Assert.Equal(OrderDirections.Sell, protection.Direction);
            Assert.Equal(nativeEntry.OwnerId, protection.OwnerId);
            Assert.Equal(context.Descriptor.Id, protection.ContextId);
        }
        Assert.Equal(94m, stop.TriggerPrice);
        Assert.Equal(109m, target.Price);
        Assert.Equal(2, runner.ActiveOrders.Count(o => o.IsProtective));
        lock (server.Tags)
        {
            Assert.Equal(3, server.Tags.Count);
            Assert.Single(server.Tags, tag => tag == nativeEntry.ClientOrderId);
            Assert.Single(server.Tags, tag => tag == stop.ClientOrderId);
            Assert.Single(server.Tags, tag => tag == target.ClientOrderId);
        }
        var forbiddenOpening = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 90);
        var oversizedReduction = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 2, 94, reduceOnly: true);
        await Until(() => forbiddenOpening.Status == OrderStatus.Rejected && oversizedReduction.Status == OrderStatus.Rejected);
        lock (server.Tags) Assert.Equal(3, server.Tags.Count);
        await server.FillOrder(stop.ExchangeOrderId, 1, 94);
        await Until(() => broker.Position.Volume == 0 && runner.OwnPosition == 0 && !broker.ActiveOrders.Any());
        Assert.Equal(0, server.Position);
        Assert.True(target.Canceled);
        Assert.Equal(0m, runner.Ledger.Position);
    }

    [Fact]
    public async Task Restarted_owned_native_quantity_is_not_erased_by_a_replay_ready_stale_flat_position()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        using var data = Data("ES");
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic 01", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var context = DurableContext();
        using var broker = new RithmicBroker(data, session, "ACC1", "Owned restart fixture",
            _ => Task.FromResult(("ESZ6", "CME")), context);
        using var original = new LiveStrategyRunner(new ProtectedStrategy(), data, broker, ChartPeriod.M1);
        session.Start();
        await Until(() => broker.PositionReady && broker.OrdersReady);
        original.Start();
        var entry = original.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1, 99, "Owned restart entry");
        await Until(() => broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0));
        var nativeEntry = Assert.Single(broker.ActiveOrders);
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PositionUpdateRelease = positionRelease;
        Task fill = server.FillOrder(nativeEntry.ExchangeOrderId, 1, 99);
        using var successor = new LiveStrategyRunner(new ProtectedStrategy(), data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        try
        {
            await server.PositionUpdateRequested.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => original.OwnPosition == 1 && entry.State == StrategyOrderState.Filled);
            original.Stop(flatten: false, keepProtection: true);
            Assert.True(broker.PositionReady && broker.OrdersReady);
            Assert.Equal(0m, broker.Position.Volume);
            Assert.Equal(1, server.Position);
            successor.Start();
            Assert.Equal(original.Key, successor.Key);
            Assert.Equal(1m, successor.OwnPosition);
            Assert.Equal(1m, LiveStore.LoadLedger(successor.Key, 1m).Position);
            Assert.True(successor.Ledger.HasPendingPrices);
            Assert.Null(successor.Ledger.NetRealized);
            Assert.Empty(successor.Ledger.Fills);
            lock (server.Tags) Assert.Single(server.Tags);
        }
        finally { positionRelease.TrySetResult(); }
        await fill.WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => broker.Position.Volume == 1 &&
            successor.ActiveOrders.Count(order => order.IsProtective) == 2);
        Assert.Equal(1m, successor.OwnPosition);
        Assert.Equal(94m, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop).TriggerPrice);
        Assert.Equal(109m, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Limit).Price);
        Assert.Empty(successor.Ledger.Fills);
    }

    [Fact]
    public async Task Closing_owned_native_quantity_waits_for_position_reconciliation_without_pricing_an_absent_fill()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        using var data = Data("ES");
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic 01", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var context = DurableContext();
        using var broker = new RithmicBroker(data, session, "ACC1", "Owned close fixture",
            _ => Task.FromResult(("ESZ6", "CME")), context);
        using var runner = new LiveStrategyRunner(new ProtectedStrategy(), data, broker, ChartPeriod.M1);
        session.Start();
        await Until(() => broker.PositionReady && broker.OrdersReady);
        runner.Start();
        var entry = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1, 99, "Owned close entry");
        await Until(() => broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0));
        var nativeEntry = Assert.Single(broker.ActiveOrders);
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PositionUpdateRelease = positionRelease;
        Task fill = server.FillOrder(nativeEntry.ExchangeOrderId, 1, 99);
        try
        {
            await server.PositionUpdateRequested.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => runner.OwnPosition == 1 && entry.State == StrategyOrderState.Filled);
            runner.Stop(flatten: true);
            Assert.True(runner.IsClosing);
            Assert.Equal(1m, runner.OwnPosition);
            Assert.Equal(1m, LiveStore.LoadLedger(runner.Key, 1m).Position);
            Assert.Null(runner.Ledger.NetRealized);
            Assert.Empty(runner.Ledger.Fills);
            lock (server.Tags) Assert.Single(server.Tags);
        }
        finally { positionRelease.TrySetResult(); }
        await fill.WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => broker.Position.Volume == 0 && runner.OwnPosition == 0 && !runner.IsClosing);
        Assert.Equal(0, server.Position);
        Assert.Empty(runner.Ledger.Fills);
        lock (server.Tags) Assert.Equal(2, server.Tags.Count);
        // The read loop's last order notification may still be committing this ledger; join it before cleanup.
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Partial_native_close_rechecks_remaining_owned_quantity_after_its_confirmed_terminal_fill()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        using var data = Data("ES");
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic 01", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var context = DurableContext();
        using var broker = new RithmicBroker(data, session, "ACC1", "Owned partial close fixture",
            _ => Task.FromResult(("ESZ6", "CME")), context);
        using var runner = new LiveStrategyRunner(new ProtectedStrategy { StopLossTicks = 0, TakeProfitTicks = 0 },
            data, broker, ChartPeriod.M1);
        session.Start();
        await Until(() => broker.PositionReady && broker.OrdersReady);
        runner.Start();
        runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 2, 99, "Owned partial close entry");
        await Until(() => broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0));
        var nativeEntry = Assert.Single(broker.ActiveOrders);
        await server.FillOrder(nativeEntry.ExchangeOrderId, 1, 99);
        await Until(() => runner.OwnPosition == 1 && broker.Position.Volume == 1);
        await Until(() => nativeEntry.Canceled);
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PositionUpdateRelease = positionRelease;
        Task secondFill = server.FillCanceledOrder(nativeEntry.ExchangeOrderId, 1, 99);
        try
        {
            await Until(() => runner.OwnPosition == 2 && server.Position == 2 && broker.Position.Volume == 1,
                () => $"Before close: owned={runner.OwnPosition}; ledger={runner.Ledger.Position}; native={broker.Position.Volume}; server={server.Position}; filled={nativeEntry.Filled}; state={nativeEntry.State}; stop={runner.StopReason}; error={broker.LastError}");
            runner.Stop(flatten: true);
            await Until(() => runner.OwnPosition == 1 && server.Position == 1,
                () => $"After bounded close: owned={runner.OwnPosition}; native={broker.Position.Volume}; server={server.Position}; closing={runner.IsClosing}; error={broker.LastError}");
            Assert.True(runner.IsClosing);
            Assert.Empty(runner.Ledger.Fills);
        }
        finally { positionRelease.TrySetResult(); }
        await secondFill.WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => broker.Position.Volume == 0 && runner.OwnPosition == 0 && !runner.IsClosing,
            () => $"After native terminal reconciliation: owned={runner.OwnPosition}; native={broker.Position.Volume}; server={server.Position}; closing={runner.IsClosing}; error={broker.LastError}");
        Assert.Equal(0, server.Position);
        Assert.Equal(0m, LiveStore.LoadLedger(runner.Key, 1m).Position);
        Assert.Empty(runner.Ledger.Fills);
        lock (server.Tags) Assert.Equal(3, server.Tags.Count);
    }

    [Theory]
    [InlineData(OrderTypes.Market)]
    [InlineData(OrderTypes.Stop)]
    [InlineData(OrderTypes.Limit)]
    public void Another_instrument_cannot_erase_a_manual_owners_unused_bounded_emergency_identity(OrderTypes type)
    {
        var context = DurableContext();
        var owner = context.ManualOwner;
        var a = Opening(context, "rithmic:ES", 2);
        Assert.Null(context.StartDispatch(a, "ESZ6", "CME"));
        var b = Opening(context, "rithmic:NQ", 3);
        Assert.Equal(owner, b.Owner);
        Assert.Null(context.StartDispatch(b, "NQZ6", "CME"));
        var disk = new OrderContext(context.Descriptor, _root);
        AssertAdmitted(disk, a, "ESZ6", 2);
        AssertAdmitted(disk, b, "NQZ6", 3);
        BreakOwnedWrites();

        Assert.NotNull(context.Prepare(new OrderOwner("another-owner"), Reduction("rithmic:ES", type, 2)).Error);
        Assert.NotNull(context.Prepare(owner, Reduction("rithmic:ES", type, 3)).Error);
        Assert.NotNull(context.Prepare(owner, Reduction("rithmic:ES", type, 2) with { Direction = OrderDirections.Buy }).Error);
        Assert.NotNull(context.Prepare(owner, Reduction("rithmic:UNKNOWN", type, 1)).Error);
        Assert.NotNull(context.Prepare(owner, Reduction("rithmic:ES", OrderTypes.StopLimit, 1)).Error);

        var exitA = context.Prepare(owner, Reduction("rithmic:ES", type, 2));
        Assert.Null(exitA.Error);
        AssertReserved(disk, exitA, owner, "ESZ6", 2);
        Assert.NotNull(context.StartDispatch(exitA, "NQZ6", "CME"));
        Assert.NotNull(context.StartDispatch(exitA, "ESZ6", "NYMEX"));
        Assert.NotNull(context.StartDispatch(exitA, "ESZ6", "CME", actualQuantity: 3));
        Assert.Null(context.StartDispatch(exitA, "ESZ6", "CME"));
        Assert.NotNull(context.StartDispatch(exitA, "ESZ6", "CME"));
        Assert.NotNull(context.Prepare(owner, Reduction("rithmic:ES", type, 1)).Error);
        // Instrument B retains its separate slot, with neither a cross-route reuse nor an enlarged bound.
        var exitB = context.Prepare(owner, Reduction("rithmic:NQ", type, 3));
        Assert.Null(exitB.Error);
        AssertReserved(disk, exitB, owner, "NQZ6", 3);
        Assert.NotEqual(exitA.Id, exitB.Id);
        Assert.Null(context.StartDispatch(exitB, "NQZ6", "CME"));
        Assert.NotNull(context.Prepare(owner, Reduction("rithmic:NQ", type, 1)).Error);
        Assert.NotNull(Opening(context, "rithmic:ES", 1).Error);
        Assert.True(context.HasStorageFault);
    }

    [Fact]
    public async Task Two_resting_openings_require_combined_emergency_capacity_before_both_can_be_admitted()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic 01", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var context = DurableContext();
        using var data = Data("ES");
        using var broker = new RithmicBroker(data, session, "ACC1", "Owned REAL fixture",
            _ => Task.FromResult(("ESZ6", "CME")), context);
        session.Start();
        await Until(() => broker.PositionReady && broker.OrdersReady);
        Assert.True(broker.IsReal);
        var first = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 2, 99);
        await Until(() => first.ExchangeOrderId.Length > 0);
        Assert.Equal(0m, broker.Position.Volume);
        var second = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 2, 99);
        await Until(() => second.ExchangeOrderId.Length > 0);
        Assert.Equal(0m, broker.Position.Volume);
        Assert.Equal(context.ManualOwner.Id, first.OwnerId);
        Assert.Equal(context.ManualOwner.Id, second.OwnerId);
        var disk = new OrderContext(context.Descriptor, _root);
        var admittedFirst = Assert.IsType<OwnedOrderIntent>(disk.Find(first.IntentId));
        Assert.True(admittedFirst.DispatchUncertain);
        Assert.Equal(2m, admittedFirst.DispatchQuantity);
        var admittedSecond = Assert.IsType<OwnedOrderIntent>(disk.Find(second.IntentId));
        Assert.True(admittedSecond.DispatchUncertain);
        Assert.Equal(2m, admittedSecond.DispatchQuantity);
        await server.FillOrder(first.ExchangeOrderId, 2, 99);
        await server.FillOrder(second.ExchangeOrderId, 2, 99);
        const decimal filledQuantity = 4m;
        await Until(() => broker.Position.Volume == filledQuantity && !broker.ActiveOrders.Any());
        Assert.Equal(filledQuantity, first.Filled + second.Filled);
        BreakOwnedWrites();
        Assert.NotNull(context.Prepare(context.ManualOwner,
            Reduction(data.Instrument.Key, OrderTypes.Market, filledQuantity + 1)).Error);
        // Same public manual reduction used by Flatten, with no working siblings left to cancel.
        var flatten = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Sell,
            filledQuantity, comment: "Close", reduceOnly: true);
        // PnL and order acknowledgments use independent native streams; flat exposure is not a fill barrier.
        await Until(() => broker.Position.Volume == 0 && flatten.Filled == filledQuantity || flatten.Status == OrderStatus.Rejected);
        Assert.Equal(0m, broker.Position.Volume);
        Assert.Equal(0, server.Position);
        Assert.Equal(filledQuantity, flatten.Filled);
        Assert.True(flatten.ReduceOnly);
        Assert.Equal(context.ManualOwner.Id, flatten.OwnerId);
        var reserved = Assert.IsType<OwnedOrderIntent>(disk.Find(flatten.IntentId));
        Assert.True(reserved.Emergency);
        Assert.True(reserved.DispatchUncertain);
        Assert.Equal(filledQuantity, reserved.MaximumReduction);
        Assert.Equal("ESZ6", reserved.NativeInstrument);
        Assert.NotNull(context.Prepare(context.ManualOwner, Reduction(data.Instrument.Key, OrderTypes.Market, 1)).Error);
        Assert.NotNull(Opening(context, data.Instrument.Key, 1).Error);
        Assert.True(context.HasStorageFault);
        lock (server.Tags) Assert.Equal(3, server.Tags.Count);
    }

    [Fact]
    public async Task Terminal_late_quantity_after_handle_fold_preserves_combined_emergency_reduction_capacity()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic 01", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var context = DurableContext();
        using var data = Data("ES");
        using var broker = new RithmicBroker(data, session, "ACC1", "Owned REAL fixture",
            _ => Task.FromResult(("ESZ6", "CME")), context);
        session.Start();
        await Until(() => broker.PositionReady && broker.OrdersReady);
        var first = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 2, 99);
        await Until(() => first.ExchangeOrderId.Length > 0);
        broker.Cancel(first);
        await Until(() => first.Canceled && !broker.ActiveOrders.Any());
        Assert.Equal(0m, first.Filled);
        var second = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 2, 98);
        await Until(() => second.ExchangeOrderId.Length > 0);
        // Admitting B has now had the opportunity to fold terminal A out of active exposure.
        Assert.Equal(0m, broker.Position.Volume);
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PositionUpdateRelease = positionRelease;
        Task lateFill = server.FillCanceledOrder(first.ExchangeOrderId, 2, 99);
        try
        {
            await server.PositionUpdateRequested.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => first.Filled == 2);
            Assert.NotEqual(OrderStates.Active, first.State);
            Assert.Equal(0m, broker.Position.Volume);
            Assert.Equal(2, server.Position);
            OrderActions.Reprice(broker, second, 97);
            await Until(() => server.ModifyRequests == 1 && !broker.IsReplacementPending(second));
            Assert.Equal(97m, second.Price);
            Assert.Equal(2m, second.Unfilled);
            Assert.Same(second, Assert.Single(broker.ActiveOrders));
        }
        finally { positionRelease.TrySetResult(); }
        await lateFill.WaitAsync(TimeSpan.FromSeconds(8));
        server.PositionUpdateRelease = null;
        await Until(() => broker.Position.Volume == 2);
        await server.FillOrder(second.ExchangeOrderId, 2, 97);
        await Until(() => broker.Position.Volume == 4 && !broker.ActiveOrders.Any());
        Assert.Equal(4m, first.Filled + second.Filled);
        var disk = new OrderContext(context.Descriptor, _root);
        BreakOwnedWrites();
        Assert.NotNull(context.Prepare(context.ManualOwner,
            Reduction(data.Instrument.Key, OrderTypes.Market, 5)).Error);
        var flatten = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Sell, 4,
            comment: "Close", reduceOnly: true);
        await Until(() => (broker.Position.Volume == 0 && flatten.Filled == 4m) || flatten.Status == OrderStatus.Rejected);
        Assert.Equal(0m, broker.Position.Volume);
        Assert.Equal(0, server.Position);
        Assert.Equal(4m, flatten.Filled);
        Assert.True(flatten.ReduceOnly);
        Assert.Equal(context.ManualOwner.Id, flatten.OwnerId);
        Assert.Equal(context.Descriptor.Id, flatten.ContextId);
        var reserved = Assert.IsType<OwnedOrderIntent>(disk.Find(flatten.IntentId));
        Assert.True(reserved.Emergency && reserved.DispatchUncertain);
        Assert.Equal((OrderDirections.Sell, 4m, "ESZ6", "CME"),
            (reserved.Request.Direction, reserved.MaximumReduction, reserved.NativeInstrument, reserved.NativeExchange));
        Assert.NotNull(context.Prepare(context.ManualOwner,
            Reduction(data.Instrument.Key, OrderTypes.Market, 1)).Error);
        Assert.NotNull(Opening(context, data.Instrument.Key, 1).Error);
        Assert.True(context.HasStorageFault);
        lock (server.Tags) Assert.Equal(3, server.Tags.Count);
    }

    [Fact]
    public async Task Repricing_an_unowned_manual_opening_reserves_the_opposite_side_bounded_emergency_exit()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic 01", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var context = DurableContext();
        int openingPermission = 0;
        context.Configure(context.Descriptor, _root, request =>
            Volatile.Read(ref openingPermission) != 0 || request.ReduceOnly
                ? null : "Fixture opening permission denied.");
        using var data = Data("ES");
        using var broker = new RithmicBroker(data, session, "ACC1", "Owned REAL fixture",
            _ => Task.FromResult(("ESZ6", "CME")), context);
        session.Start();
        await Until(() => broker.PositionReady && broker.OrdersReady);
        var forbidden = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 90);
        await Until(() => forbidden.Status == OrderStatus.Rejected);
        lock (server.Tags) Assert.Empty(server.Tags);
        Volatile.Write(ref openingPermission, 1);
        await server.PushOrder("manual-opening", "external-manual-opening", buy: true, qty: 2, price: 99);
        // A list published before the rejection can still hold the forbidden order; wait for the pushed one.
        await Until(() => broker.ActiveOrders.Any(order => order.ExchangeOrderId == "manual-opening"));
        var opening = Assert.Single(broker.ActiveOrders);
        Assert.Null(opening.OwnerId);
        Assert.Null(opening.IntentId);
        Assert.Equal((OrderDirections.Buy, OrderTypes.Limit, 0m),
            (opening.Direction, opening.Type, broker.Position.Volume));
        var native = Assert.IsType<NativeOrderIdentity>(opening.NativeIdentity);
        OrderActions.Reprice(broker, opening, 98);
        await Until(() => server.ModifyRequests == 1 && !broker.IsReplacementPending(opening));
        Assert.Equal(98m, opening.Price);
        Assert.Same(opening, Assert.Single(broker.ActiveOrders));
        Assert.Equal(context.ManualOwner.Id, opening.OwnerId);
        Assert.Equal(context.Descriptor.Id, opening.ContextId);
        string logical = Assert.IsType<string>(opening.LogicalOrderId);
        string amendment = Assert.IsType<string>(opening.IntentId);
        Assert.Equal(native, opening.NativeIdentity);
        await server.FillOrder(opening.ExchangeOrderId, 2, 98);
        await Until(() => broker.Position.Volume == 2 && !broker.ActiveOrders.Any());
        Assert.Equal(2m, opening.Filled);
        Volatile.Write(ref openingPermission, 0);
        var disk = new OrderContext(context.Descriptor, _root);
        BreakOwnedWrites();
        Assert.NotNull(context.Prepare(new OrderOwner("unrelated-owner"),
            Reduction(data.Instrument.Key, OrderTypes.Market, 2)).Error);
        Assert.NotNull(context.Prepare(context.ManualOwner,
            Reduction(data.Instrument.Key, OrderTypes.Market, 3)).Error);
        var flatten = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Sell, 2,
            comment: "Close", reduceOnly: true);
        await Until(() => (broker.Position.Volume == 0 && flatten.Filled == 2m) || flatten.Status == OrderStatus.Rejected);
        Assert.Equal(0m, broker.Position.Volume);
        Assert.Equal(0, server.Position);
        Assert.Equal(2m, flatten.Filled);
        Assert.True(flatten.ReduceOnly);
        Assert.Equal(context.ManualOwner.Id, flatten.OwnerId);
        var reserved = Assert.IsType<OwnedOrderIntent>(disk.Find(flatten.IntentId));
        Assert.True(reserved.Emergency && reserved.DispatchUncertain);
        Assert.Equal((OrderDirections.Sell, 2m, "ESZ6", "CME"),
            (reserved.Request.Direction, reserved.MaximumReduction, reserved.NativeInstrument, reserved.NativeExchange));
        var adoption = Assert.IsType<OwnedOrderIntent>(disk.Find(logical));
        Assert.True(adoption.Adopted);
        Assert.False(adoption.Request.Protective);
        Assert.False(adoption.Request.ReduceOnly);
        Assert.Equal((OrderDirections.Buy, OrderTypes.Limit, 2m, 99m),
            (adoption.Request.Direction, adoption.Request.Type, adoption.Request.Quantity, adoption.Request.Price));
        var replacement = Assert.IsType<OwnedOrderIntent>(disk.Find(amendment));
        Assert.Equal((context.ManualOwner, logical, adoption.Id),
            (replacement.Owner, replacement.LogicalOrderId, replacement.PredecessorId));
        Assert.NotNull(context.Prepare(context.ManualOwner,
            Reduction(data.Instrument.Key, OrderTypes.Market, 1)).Error);
        Assert.NotNull(Opening(context, data.Instrument.Key, 1).Error);
        Assert.True(context.HasStorageFault);
        lock (server.Tags) Assert.Single(server.Tags);
    }

    private OrderContext DurableContext()
    {
        string id = Guid.NewGuid().ToString("N");
        return new OrderContext(new(id, "Rithmic", "futures", "owned-fixture", id, "generation"), _root);
    }

    private static PreparedOrderIntent Opening(OrderContext context, string instrument, decimal quantity) =>
        context.Prepare(context.ManualOwner, new(instrument, OrderDirections.Buy, OrderTypes.Limit,
            quantity, 90, MaximumOwnedQuantity: quantity));

    private static OrderRequest Reduction(string instrument, OrderTypes type, decimal quantity) =>
        new(instrument, OrderDirections.Sell, type, quantity, type == OrderTypes.Market ? 0 : 95,
            ReduceOnly: true, Protective: true);

    private static void AssertAdmitted(OrderContext disk, PreparedOrderIntent intent, string native, decimal quantity)
    {
        var saved = Assert.IsType<OwnedOrderIntent>(disk.Find(intent.Id));
        Assert.True(saved.DispatchUncertain);
        Assert.Equal(native, saved.NativeInstrument);
        Assert.Equal(quantity, saved.DispatchQuantity);
        Assert.Equal(intent.Owner, saved.Owner);
    }

    private static void AssertReserved(OrderContext disk, PreparedOrderIntent ticket, OrderOwner owner, string native, decimal bound)
    {
        var saved = Assert.IsType<OwnedOrderIntent>(disk.Find(ticket.Id));
        Assert.True(saved.Emergency);
        Assert.True(saved.DispatchUncertain); // cannot become a fresh identity after unknown delivery/restart
        Assert.True(saved.Request.ReduceOnly);
        Assert.Equal(owner, saved.Owner);
        Assert.Equal(ticket.Request.Instrument, saved.Request.Instrument);
        Assert.Equal(ticket.Request.Direction, saved.Request.Direction);
        Assert.Equal(ticket.Request.Type, saved.Request.Type);
        Assert.Equal(native, saved.NativeInstrument);
        Assert.Equal("CME", saved.NativeExchange);
        Assert.Equal(bound, saved.MaximumReduction);
    }

    [Fact]
    public async Task FlatOwnerResetAndAnotherOwnersNativeRithmicFillCompleteWithoutInvertingProviderAndLedgerGates()
    {
        using var server = new FakeRithmicServer { MarketPrice = 99 };
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var context = DurableContext();
        using var data = Data("ES");
        using var broker = new RithmicBroker(data, session, "ACC1", "Reset/native gate fixture",
            _ => Task.FromResult(("ESZ6", "CME")), context);
        var quantityEntered = new TaskCompletionSource<OwnedOrderQuantity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quantityRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeSyncLeft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? openingLogicalId = null;
        OrderOwner? openingOwner = null;
        void HoldActualNativeQuantity(OwnedOrderQuantity observation)
        {
            if (observation.Intent.Owner != openingOwner || observation.Intent.LogicalOrderId != openingLogicalId) return;
            quantityEntered.TrySetResult(observation);
            quantityRelease.Task.GetAwaiter().GetResult();
        }
        void ObserveNativeSyncCompletion()
        {
            if (quantityEntered.Task.IsCompleted) nativeSyncLeft.TrySetResult();
        }
        // Hold native quantity publication before either projector can apply it to an owned ledger.
        context.QuantityChanged += HoldActualNativeQuantity;
        broker.Changed += ObserveNativeSyncCompletion;
        var boundary = new ResetNativeGateAccount(broker, quantityEntered.Task, () => quantityRelease.TrySetResult());
        var filledStrategy = new ResetFenceFilledStrategy { StopLossTicks = 5 };
        using var filled = new LiveStrategyRunner(filledStrategy, data, broker, ChartPeriod.M1);
        using var flat = new LiveStrategyRunner(new ResetFenceFlatStrategy(), data, boundary, ChartPeriod.M1);
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        filled.Start();
        flat.Start();
        Assert.NotEqual(filled.Key, flat.Key);
        Guid filledGeneration = LiveStore.CaptureLedgerGeneration(_root, filled.Key);
        Guid flatGeneration = LiveStore.CaptureLedgerGeneration(_root, flat.Key);
        openingOwner = context.LedgerOwner(filled.Key, filledGeneration);
        var opening = OrderActions.Place(broker, openingOwner, OrderTypes.Limit, OrderDirections.Buy, 2m, 99m);
        await Until(() => opening.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(opening));
        openingLogicalId = Assert.IsType<string>(opening.LogicalOrderId);
        Assert.Equal(0m, flat.OwnPosition);
        Assert.Empty(flat.ActiveOrders);
        server.PositionUpdateRelease = positionRelease;
        Task? nativeFill = null;
        Task<bool>? reset = null;
        bool accepted = false;
        Exception? resetError = null;
        try
        {
            reset = Task.Run(() => boundary.RunReset(flat.ResetLedger,
                () => nativeFill = server.FillOrder(opening.ExchangeOrderId, 2, 99)));
            try { accepted = await reset.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { resetError = ex; }
        }
        finally
        {
            quantityRelease.TrySetResult();
            positionRelease.TrySetResult();
            server.PositionUpdateRelease = null;
            if (reset != null)
                try { await reset.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (Exception ex) { resetError ??= ex; }
            if (nativeFill != null) await nativeFill.WaitAsync(TimeSpan.FromSeconds(8));
            await boundary.JoinProviderQueriesAsync();
            await nativeSyncLeft.Task.WaitAsync(TimeSpan.FromSeconds(8));
            context.QuantityChanged -= HoldActualNativeQuantity;
            broker.Changed -= ObserveNativeSyncCompletion;
        }

        // No assertion depends on how many times the fixed reset queries its account. A query
        // outside the global ledger gate can complete; a query beneath it times out, unwinds
        // that gate, and is then joined above rather than leaving the suite deadlocked.
        Assert.False(boundary.ProviderQueryTimedOut,
            "Reset held the ledger gate while its genuine Rithmic getter and native quantity commit waited on one another.");
        Assert.Null(resetError);
        Assert.NotNull(reset);
        Assert.True(reset.IsCompletedSuccessfully);
        var observation = await quantityEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal((openingOwner, openingLogicalId, 2m),
            (observation.Intent.Owner, observation.Intent.LogicalOrderId, observation.CumulativeQuantity));
        await Until(() => broker.Position.Volume == 2m && filled.OwnPosition == 2m &&
            broker.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && order.ExchangeOrderId.Length > 0));
        Assert.Equal((OrderStates.Done, OrderStatus.Filled, 2m, 0m),
            (opening.State, opening.Status, opening.Filled, opening.Unfilled));
        Assert.Equal(2, server.Position);
        Assert.Equal(99m, broker.Position.AveragePrice);
        Assert.Equal(2m, filled.VirtualPosition);
        Assert.Equal(2m, filledStrategy.Position);
        foreach (var ledger in new[] { filled.Ledger, LiveStore.LoadLedger(_root, filled.Key, 1m) })
        {
            Assert.Equal(2m, ledger.Position);
            Assert.True(ledger.HasPendingPrices);
            Assert.Null(ledger.AuthoritativeNet);
            Assert.Empty(ledger.Fills); // Rithmic cumulative state is not a fee-complete execution-price claim
            Assert.False(ledger.OpenContextConflict);
        }
        Assert.Equal(filledGeneration, LiveStore.CaptureLedgerGeneration(_root, filled.Key));
        Guid actualFlatGeneration = LiveStore.CaptureLedgerGeneration(_root, flat.Key);
        if (accepted) Assert.NotEqual(flatGeneration, actualFlatGeneration);
        else Assert.Equal(flatGeneration, actualFlatGeneration);
        Assert.Equal(0m, flat.OwnPosition);
        Assert.Equal(0m, LiveStore.LoadLedger(_root, flat.Key, 1m).Position);
        Assert.Empty(flat.ActiveOrders);
        var stop = Assert.Single(broker.ActiveOrders);
        Assert.Equal((OrderDirections.Sell, OrderTypes.Stop, 2m, 94m, openingOwner.Id),
            (stop.Direction, stop.Type, stop.Unfilled, stop.TriggerPrice, stop.OwnerId));
        var protection = Assert.IsType<OwnedOrderIntent>(context.Find(stop.IntentId));
        Assert.Equal(openingOwner, protection.Owner);
        Assert.True(protection.Request.ReduceOnly);
        Assert.True(protection.Request.Protective);
        Assert.Equal(2m, protection.Request.MaximumOwnedQuantity);
        Assert.Equal(new[] { stop.ExchangeOrderId }, filled.ProtectionOrderIds);
        var disk = new OrderContext(context.Descriptor, _root);
        Assert.Equal(2m, Assert.IsType<OwnedOrderIntent>(disk.Find(openingLogicalId)).QuantityProjected);
        Assert.Empty(disk.PendingReceipts());
        lock (server.Tags)
        {
            Assert.Equal(2, server.Tags.Count);
            Assert.Single(server.Tags, tag => tag == opening.ClientOrderId);
            Assert.Single(server.Tags, tag => tag == stop.ClientOrderId);
        }
    }

    // This boundary changes only scheduling. Every order, account value, source, notification,
    // quantity and durable write still comes from the real Rithmic adapter/context/projector.
    private sealed class ResetNativeGateAccount(RithmicBroker inner, Task nativeQuantityEntered, Action releaseNativeQuantity)
        : ITradingAccount, ITrackedOrderReplacement
    {
        private readonly List<Task> _providerQueries = new();
        private int _resetThreadId;
        private bool _capturedResetSnapshot;
        private Action? _startNativeFill;
        public bool ProviderQueryTimedOut { get; private set; }

        public bool RunReset(Func<bool> reset, Action startNativeFill)
        {
            _startNativeFill = startNativeFill;
            _resetThreadId = Environment.CurrentManagedThreadId;
            try { return reset(); }
            finally
            {
                _resetThreadId = 0;
                releaseNativeQuantity();
            }
        }

        private bool OnResetThread => _resetThreadId != 0 && _resetThreadId == Environment.CurrentManagedThreadId;

        private T QueryNative<T>(Func<T> query)
        {
            if (!OnResetThread || !_capturedResetSnapshot) return query();
            // Release the native producer before querying so a stalled account getter cannot trap cleanup.
            releaseNativeQuantity();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(() =>
            {
                started.TrySetResult();
                return query();
            });
            _providerQueries.Add(task);
            started.Task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            try { return task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                ProviderQueryTimedOut = true;
                throw;
            }
        }

        public async Task JoinProviderQueriesAsync() =>
            await Task.WhenAll(_providerQueries).WaitAsync(TimeSpan.FromSeconds(8));

        public IEnumerable<Order> ActiveOrders
        {
            get
            {
                if (!OnResetThread || _capturedResetSnapshot)
                    return QueryNative(() => inner.ActiveOrders.ToArray());
                var snapshot = inner.ActiveOrders.ToArray();
                _capturedResetSnapshot = true;
                _startNativeFill!();
                nativeQuantityEntered.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
                return snapshot;
            }
        }

        public bool OrdersReady => QueryNative(() => inner.OrdersReady);
        public bool PositionReady => inner.PositionReady;
        public string Name => inner.Name;
        public bool IsReal => inner.IsReal;
        public Instrument Instrument => inner.Instrument;
        public Position Position => inner.Position;
        public string? LastError => inner.LastError;
        public decimal LastPrice => inner.LastPrice;
        public OrderContext OrderContext => inner.OrderContext;
        public IExecutionSource Executions => inner.Executions;
        public event Action? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }
        public Order Submit(PreparedOrderIntent intent) => inner.Submit(intent);
        public Order Replace(Order order, PreparedOrderIntent intent) => inner.Replace(order, intent);
        public bool IsReplacementPending(Order order) => inner.IsReplacementPending(order);
        public void Cancel(Order order) => inner.Cancel(order);
        public void CancelAt(decimal price) => inner.CancelAt(price);
        public void CancelAll() => inner.CancelAll();
    }

    private sealed class ResetFenceFlatStrategy : Strategy { }
    private sealed class ResetFenceFilledStrategy : Strategy { }

    private void BreakOwnedWrites()
    {
        // Fail the real durable writer's lease, not a mocked Save callback. Reads and existing
        // persisted identities remain available; only this test's private namespace is affected.
        string directory = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "execution-state")));
        string lease = Path.Combine(directory, ".lease");
        File.Delete(lease);
        Directory.CreateDirectory(lease);
    }

    private static void InitializeField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        SearchZoomFixture.Set(target, name, Activator.CreateInstance(field.FieldType)!);
    }

    private static InstrumentData Data(string symbol)
    {
        var data = new InstrumentData(new Instrument
        {
            Symbol = symbol, FeedId = RithmicFeed.FeedId, Exchange = "CME", TickSize = 1, VolumeStep = 1, PointValue = 1,
        }, new SimulatedFeed());
        for (int i = 0; i < 5; i++) data.Trades.Add(new Trade(i + 1, T0.AddMinutes(i), 99, 1, TradeSide.Buy));
        return data;
    }

    private static async Task Until(Func<bool> condition, Func<string>? diagnosis = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), diagnosis?.Invoke() ?? "The owned native fixture did not publish the expected protection/order/position state.");
    }

    private sealed class ProtectedStrategy : Strategy
    {
        public ProtectedStrategy() { StopLossTicks = 5; TakeProfitTicks = 10; }
        protected override void OnBar(Bar bar) { }
    }
}
