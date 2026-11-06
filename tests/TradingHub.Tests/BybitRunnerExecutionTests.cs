using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class BybitRunnerExecutionTests
{
    private static readonly Instrument Btc = new()
    { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, PointValue = 1 };

    private sealed class ProtectedEntry : Strategy
    {
        public void Open(decimal quantity = 1) => BuyLimit(105, quantity, "Actual entry");
        protected override void OnBar(Bar bar) { }
    }
    private sealed class ProviderClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = FakeBybitProtocol.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HeldExecutionPoll(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _next = new(inner, disposeHandler: false);
        private int _armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref _armed, 1);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/v5/execution/list" && Interlocked.Exchange(ref _armed, 0) != 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await _next.SendAsync(request, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _next.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public override void Post(SendOrPostCallback callback, object? state) => _pending.Enqueue((callback, state));
        public void Drain()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_pending.TryDequeue(out var pending)) pending.Callback(pending.State);
            }
            finally { SetSynchronizationContext(previous); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repriced_entry_keeps_original_strategy_units_when_execution_leads_native_quantity(bool persist)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-amended-allocation-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol, disposeHandler: false);
        try
        {
            await using var session = new BybitTradingSession(protocol.Settings, Path.Combine(root, "provider", "orders.json"), http,
                clock: new ProviderClock(), executionRoot: LiveStore.Root);
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 100, 1, TradeSide.Buy));
            using var broker = new BybitBroker(data, session, "Repriced entry fixture");
            await session.RefreshAsync();
            var runnerContext = new QueuedSynchronizationContext();
            var strategy = new ProtectedEntry();
            LiveStrategyRunner CreateRunner()
            {
                var previousContext = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(runnerContext);
                    return new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1) { Persist = persist };
                }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            }
            using var runner = CreateRunner();
            runner.Start();
            runnerContext.Drain();
            strategy.Open(6);
            await RefreshUntil(session, () =>
            {
                runnerContext.Drain();
                return session.OrdersReady && broker.ActiveOrders.Any(order => order.ExchangeOrderId != "");
            });
            var opening = Assert.Single(broker.ActiveOrders);
            string nativeId = opening.ExchangeOrderId;
            protocol.Fill("allocation-first", 3, 100, 0, orderId: nativeId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-20), closedQuantity: 0);
            protocol.NetPosition = 3;
            await RefreshUntil(session, () =>
            {
                runnerContext.Drain();
                return runner.OwnPosition == 3 && !runner.Ledger.HasPendingFinancials;
            });
            Assert.Equal(3m, runner.VirtualPosition);
            Assert.Equal(3m, opening.Filled);
            OrderActions.Reprice(broker, opening, 104);
            await RefreshUntil(session, () =>
            {
                runnerContext.Drain();
                return !broker.IsReplacementPending(opening) && opening.Price == 104 && session.OrdersReady;
            });
            var amended = Assert.IsType<OwnedOrderIntent>(session.OrderContext.Find(opening.IntentId));
            Assert.Equal(3m, amended.Request.Quantity);
            Assert.Equal(6m, amended.Request.StrategyQuantity);

            async Task<string> ActualSnapshot(string path)
            {
                using var response = await http.GetAsync(protocol.Settings.BaseUrl + path);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            var earlierNativeSnapshots = new Dictionary<string, string>
            {
                ["/v5/order/realtime"] = await ActualSnapshot("/v5/order/realtime"),
                ["/v5/order/history"] = await ActualSnapshot("/v5/order/history"),
                ["/v5/position/list"] = await ActualSnapshot("/v5/position/list"),
            };
            protocol.Fill("allocation-next", 1, 100, 0, orderId: nativeId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-10), closedQuantity: 0);
            protocol.NetPosition = 4;
            protocol.ResponseFor = request => earlierNativeSnapshots.TryGetValue(request.Path, out var snapshot)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(snapshot) } : null;
            await RefreshUntil(session, () =>
            {
                runnerContext.Drain();
                return runner.OwnPosition == 4 && !runner.Ledger.HasPendingFinancials;
            });
            Assert.Equal(3m, opening.Filled);
            Assert.Equal(4m, runner.VirtualPosition);
            Assert.Equal(4m, strategy.Position);
            protocol.ResponseFor = null;
            await RefreshUntil(session, () =>
            {
                runnerContext.Drain();
                return opening.Filled == 4 && session.OrdersReady && session.PositionReady;
            });
            Assert.Equal(4m, runner.OwnPosition);
            Assert.Equal(4m, runner.VirtualPosition);
            strategy.Open(1);
            await RefreshUntil(session, () =>
            {
                runnerContext.Drain();
                return protocol.Requests.Count(request => request.Path == "/v5/order/create") == 2;
            });
            Assert.Equal(1m, Assert.Single(broker.ActiveOrders, order => order.Id != opening.Id).QuantityToFill);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Held_actual_execution_facts_never_book_market_proxies_and_late_native_fees_correct_the_saved_protection_basis(bool acknowledgeBeforePricing)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-confirmed-runner-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol { HoldCreate = true };
        using var http = new HttpClient(protocol);
        var clock = new ProviderClock();
        bool hideExecutions = true;
        protocol.ResponseFor = request => request.Path == "/v5/execution/list" && hideExecutions
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"retCode\":0,\"result\":{\"list\":[],\"nextPageCursor\":\"\"}}") }
            : null;
        try
        {
            await using var session = new BybitTradingSession(protocol.Settings, Path.Combine(root, "provider", "context.json"), http, clock: clock,
                executionRoot: LiveStore.Root);
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, clock.Now.UtcDateTime.AddMinutes(-1), 109, 1, TradeSide.Buy));
            using var broker = new BybitBroker(data, session, "Confirmed Bybit fixture");
            await session.RefreshAsync();
            var strategy = new ProtectedEntry { StopLossTicks = 50 };
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1)
            { AccountId = "owned-bybit", ProfileId = "owned-bybit" };
            runner.Start();
            strategy.Open();
            await protocol.CreateReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            DateTime firstTime = FakeBybitProtocol.Now.AddSeconds(10).UtcDateTime;
            protocol.Fill("native-part-a", .4m, 101, .02m, orderId: "exchange-1", executionAt: new DateTimeOffset(firstTime), closedQuantity: 0);
            protocol.NetPosition = .4m;
            protocol.AveragePositionPrice = 101;
            clock.Now = FakeBybitProtocol.Now.AddSeconds(30);
            await session.RefreshAsync();
            Assert.False(session.ExecutionsReady);
            Assert.Equal(.4m, Assert.Single(session.Orders, order => order.ExchangeOrderId == "exchange-1").Filled);
            Assert.Equal(.4m, runner.Ledger.Position);
            Assert.True(runner.Ledger.HasPendingPrices);
            Assert.Empty(runner.Ledger.Fills);
            Assert.Empty(LiveStore.LoadLedger(runner.Key, 1).Fills);
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");

            // Actual identities must establish the basis even if placement acknowledges before pricing does.
            protocol.HoldCreate = false;
            protocol.CreateRelease.TrySetResult();
            if (acknowledgeBeforePricing)
            {
                await RefreshUntil(session, () => session.OrdersReady &&
                    broker.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && order.TriggerPrice == 104m));
                Assert.True(runner.Ledger.HasPendingPrices);
                Assert.Empty(runner.Ledger.Fills);
            }
            hideExecutions = false;
            await RefreshUntil(session, () => runner.Ledger.Position == .4m && runner.Ledger.AveragePrice == 101m &&
                !runner.Ledger.HasPendingPrices && session.OrdersReady &&
                broker.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && order.TriggerPrice == 96m));
            var firstStop = Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop);
            Assert.True(firstStop.ReduceOnly);
            Assert.Equal(96m, firstStop.TriggerPrice);
            Assert.Equal(101m, runner.Ledger.AveragePrice);
            var first = Assert.Single(runner.Ledger.Fills);
            Assert.Equal(firstTime, first.Time);
            Assert.Equal(101m, first.Price);
            var firstFee = Assert.Single(first.Fees!);
            Assert.Equal(.02m, firstFee.Amount);
            Assert.Equal("USDT", firstFee.Currency);
            Assert.True(first.FeesComplete);
            Assert.StartsWith("bybit:demo-1234567:BTCUSDT:exchange-1:", first.ExecutionId!.Id);
            Assert.Equal(.02m, runner.Ledger.Fees);

            DateTime secondTime = FakeBybitProtocol.Now.AddSeconds(40).UtcDateTime;
            protocol.Fill("native-part-b", .6m, 103, null, orderId: "exchange-1", executionAt: new DateTimeOffset(secondTime), closedQuantity: 0);
            protocol.NetPosition = 1;
            protocol.AveragePositionPrice = 102.2m;
            clock.Now = FakeBybitProtocol.Now.AddMinutes(1);
            data.Trades.Add(new Trade(2, clock.Now.UtcDateTime, 150, 1, TradeSide.Buy));
            await RefreshUntil(session, () => runner.Ledger.Position == 1 &&
                broker.ActiveOrders.Count(order => order.Type == OrderTypes.Stop) == 1 &&
                broker.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && order.TriggerPrice == 97.2m && order.Unfilled == 1));
            Assert.Equal(102.2m, runner.Ledger.AveragePrice);
            Assert.False(runner.Ledger.CostsComplete);
            Assert.Equal(.02m, runner.Ledger.Fees);
            Assert.Equal(new[] { firstTime, secondTime }, runner.Ledger.Fills.Select(fill => fill.Time));
            Assert.Equal(new[] { 101m, 103m }, runner.Ledger.Fills.Select(fill => fill.Price));
            Assert.All(broker.ActiveOrders.Where(order => order.Type == OrderTypes.Stop), order => Assert.True(order.ReduceOnly));

            protocol.CorrectFee("native-part-b", .06m, "USDT", "exchange-1");
            await RefreshUntil(session, () => runner.Ledger.CostsComplete && runner.Ledger.Fees == .08m);
            int createsBeforeCorrection = protocol.Requests.Count(request => request.Path == "/v5/order/create");
            protocol.CorrectFee("native-part-b", .09m, "USDT", "exchange-1");
            await RefreshUntil(session, () => runner.Ledger.Fees == .11m);
            Assert.Equal(1m, runner.Ledger.Position);
            Assert.Equal(102.2m, runner.Ledger.AveragePrice);
            Assert.Equal(-.11m, runner.Ledger.Realized);
            Assert.Equal(2, runner.Ledger.Fills.Count);
            Assert.Equal(createsBeforeCorrection, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            var restored = LiveStore.LoadLedger(runner.Key, 1);
            Assert.Equal(1m, restored.Position);
            Assert.Equal(102.2m, restored.AveragePrice);
            Assert.Equal(.11m, restored.Fees);
            Assert.True(restored.CostsComplete);
            Assert.Equal(2, restored.Fills.Count);
            Assert.Equal(new[] { firstTime, secondTime }, restored.Fills.Select(fill => fill.Time));
            Assert.Equal(.09m, Assert.Single(restored.Fills.Single(fill => fill.ExecutionId!.Id.EndsWith(":native-part-b", StringComparison.Ordinal)).Fees!).Amount);
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create" &&
                JsonNode.Parse(request.Body)!["orderLinkId"]!.GetValue<string>() ==
                JsonNode.Parse(protocol.Requests.First(item => item.Path == "/v5/order/create").Body)!["orderLinkId"]!.GetValue<string>());
            runner.Stop(flatten: false, keepProtection: true);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definitive_rejection_after_held_metadata_settles_the_actual_runner_and_allows_a_valid_next_submission(bool revokeAndStop)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-rejected-runner-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        var clock = new ProviderClock();
        bool permitted = true, holdMetadata = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ResponseForAsync = async (request, cancellationToken) =>
        {
            if (request.Path == "/v5/market/instruments-info" && holdMetadata)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return null;
        };
        try
        {
            await using var session = new BybitTradingSession(protocol.Settings, Path.Combine(root, "provider", "context.json"), http,
                sendGuard: (_, _, _, reduceOnly) => permitted || reduceOnly ? null : "Permission revoked before order transport", clock: clock,
                executionRoot: LiveStore.Root);
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, clock.Now.UtcDateTime.AddMinutes(-1), 109, 1, TradeSide.Buy));
            using var broker = new BybitBroker(data, session, "Rejected intent fixture");
            await session.RefreshAsync();
            Assert.NotNull(broker.PersistentAccountIdentity);
            var strategy = new ProtectedEntry();
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1)
            { AccountId = "bybit-rejected", ProfileId = "bybit-rejected" };
            runner.Start();
            strategy.Open(revokeAndStop ? 1 : 11); // Native instrument maximum is 10; this error is not known until metadata returns.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var rejected = Assert.Single(session.Orders);
            Assert.True(runner.HasWorkingOrders);
            Assert.Equal(OrderStates.Active, rejected.State);
            if (revokeAndStop)
            {
                permitted = false;
                runner.Stop(flatten: false, keepProtection: false); // Queues cancellation while the accepted local intent still awaits metadata.
            }
            release.TrySetResult();
            await RefreshUntil(session, () => rejected.Status == OrderStatus.Rejected && session.PositionReady && session.OrdersReady &&
                !runner.HasWorkingOrders && !runner.ActiveOrders.Any());
            Assert.Equal(OrderStates.Failed, rejected.State);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");
            var outcome = ((IAccountExecutionSource)broker).GetExecutions(rejected);
            Assert.True(outcome.CompleteCoverage);
            Assert.Empty(outcome.Executions);
            Assert.Empty(runner.Ledger.Fills);

            holdMetadata = false;
            permitted = true;
            if (!runner.IsRunning) runner.Start();
            clock.Now = clock.Now.AddMinutes(1);
            int next = data.Trades.Count;
            data.Trades.Add(new Trade(2, clock.Now.UtcDateTime, 109, 1, TradeSide.Buy));
            var appended = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ((Action<int, int>?)appended!.GetValue(data))?.Invoke(next, 1);
            strategy.Open(1);
            await protocol.CreateReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await RefreshUntil(session, () => session.OrdersReady && broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0));
            var valid = Assert.Single(broker.ActiveOrders);
            Assert.NotSame(rejected, valid);
            Assert.Equal(1m, valid.QuantityToFill);
            Assert.True(runner.HasWorkingOrders);
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
            runner.Stop(flatten: false, keepProtection: false);
            await RefreshUntil(session, () => session.OrdersReady && !broker.ActiveOrders.Any() && !runner.HasWorkingOrders);
            Assert.Empty(LiveStore.LoadLedger(runner.Key, 1).Fills);
        }
        finally
        {
            release.TrySetResult();
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Partial_manual_target_adoption_prices_only_actual_post_adoption_executions()
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-adopted-target-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol { NetPosition = 3 };
        using var http = new HttpClient(protocol);
        try
        {
            await using var session = new BybitTradingSession(protocol.Settings, Path.Combine(root, "native", "orders.json"), http,
                clock: new ProviderClock(), executionRoot: LiveStore.Root);
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 109, 1, TradeSide.Buy));
            using var broker = new BybitBroker(data, session, "Partial target adoption");
            await session.RefreshAsync();
            var target = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 3, 110, reduceOnly: true);
            await RefreshUntil(session, () => target.ExchangeOrderId.Length > 0 && session.OrdersReady);
            protocol.Fill("manual-prefix", 1, 110, .1m, orderId: target.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-20), closedQuantity: 1);
            protocol.NetPosition = 2;
            await RefreshUntil(session, () => target.Filled == 1 && broker.Position.Volume == 2 && session.ExecutionsReady && session.OrdersReady);
            var prior = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes);
            using var runner = new LiveStrategyRunner(new ProtectedEntry(), data, broker, ChartPeriod.M1)
            { ProtectOnStart = true, AdoptProtectionIds = new[] { target.ExchangeOrderId } };
            runner.SeedPosition(new LiveFill(FakeBybitProtocol.Now.UtcDateTime.AddDays(-1), true, 2, 100, Btc.Symbol, 1));
            runner.Start();
            Assert.Same(target, Assert.Single(runner.ActiveOrders, order => order.IsProtective).Live);
            protocol.Fill("adopted-fill", 1, 112, .2m, orderId: target.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-10), closedQuantity: 1);
            protocol.NetPosition = 1;
            await RefreshUntil(session, () => target.Filled == 2 && runner.OwnPosition == 1 && broker.Position.Volume == 1);
            Assert.False(runner.Ledger.HasPendingPrices);
            var execution = Assert.Single(runner.Ledger.Fills, fill => fill.Kind == LiveFillKind.Execution);
            Assert.Equal("bybit:demo-1234567:BTCUSDT:" + target.ExchangeOrderId + ":adopted-fill", execution.ExecutionId!.Id);
            Assert.Equal(112m, execution.Price);
            Assert.Equal(.2m, Assert.Single(execution.Fees!).Amount);
            Assert.Equal(11.8m, runner.Ledger.Realized);
            Assert.DoesNotContain(runner.Ledger.Fills, fill => fill.ExecutionId == prior.Identity);
            var adopted = Assert.IsType<OwnedOrderIntent>(session.OrderContext.Find(target.IntentId));
            Assert.True(adopted.BaselineKnown);
            Assert.Equal(new[] { prior.Identity }, adopted.PriorExecutions);
            protocol.CorrectFee("manual-prefix", 9, "USDT", target.ExchangeOrderId);
            await session.RefreshAsync();
            Assert.Equal(11.8m, runner.Ledger.Realized);
            Assert.Equal(.2m, runner.Ledger.Fees);
            var recovered = LiveStore.LoadLedger(LiveStore.Root, runner.Key, 1);
            Assert.False(recovered.HasPendingPrices);
            Assert.Equal(11.8m, recovered.Realized);
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
            runner.Stop(flatten: false, keepProtection: true);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Amended_owned_stop_keeps_its_actual_market_child_through_partial_execution_and_fee_correction()
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-amended-stop-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        try
        {
            await using var session = new BybitTradingSession(protocol.Settings, Path.Combine(root, "native", "orders.json"), http,
                clock: new ProviderClock(), executionRoot: LiveStore.Root);
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 109, 1, TradeSide.Buy));
            using var broker = new BybitBroker(data, session, "Amended stop child");
            await session.RefreshAsync();
            var strategy = new ProtectedEntry { StopLossTicks = 50 };
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
            runner.Start();
            strategy.Open(2);
            await RefreshUntil(session, () => broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0) && session.OrdersReady);
            var entry = Assert.Single(broker.ActiveOrders);
            protocol.Fill("owned-entry", 2, 100, .1m, orderId: entry.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-20), closedQuantity: 0);
            protocol.NetPosition = 2;
            await RefreshUntil(session, () => runner.OwnPosition == 2 && !runner.Ledger.HasPendingPrices &&
                broker.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && order.ExchangeOrderId.Length > 0) && session.OrdersReady);
            var strategyStop = Assert.Single(runner.ActiveOrders, order => order.IsProtective && order.Type == StrategyOrderType.Stop);
            var stop = Assert.IsType<Order>(strategyStop.Live);
            string originalIntent = Assert.IsType<string>(stop.IntentId);
            var native = Assert.IsType<NativeOrderIdentity>(stop.NativeIdentity);
            runner.Modify(strategyStop, 96);
            await protocol.AmendReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await RefreshUntil(session, () => !broker.IsReplacementPending(stop) && stop.TriggerPrice == 96 && session.OrdersReady);
            Assert.NotEqual(originalIntent, stop.IntentId);
            Assert.Equal(native, stop.NativeIdentity);
            protocol.TriggerStop(stop.ExchangeOrderId);
            protocol.Fill("executing-stop", 1, 94, .2m, orderId: stop.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-10), closedQuantity: 1);
            protocol.NetPosition = 1;
            await RefreshUntil(session, () => runner.OwnPosition == 1 && !runner.Ledger.HasPendingPrices && stop.Type == OrderTypes.Market);
            Assert.True(strategyStop.IsActive);
            Assert.Same(stop, Assert.Single(broker.ActiveOrders));
            Assert.Equal(1m, stop.Unfilled);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            protocol.CorrectFee("executing-stop", .3m, "USDT", stop.ExchangeOrderId);
            await RefreshUntil(session, () => runner.Ledger.Fees == .4m);
            Assert.True(strategyStop.IsActive);
            Assert.Same(stop, Assert.Single(broker.ActiveOrders));
            Assert.Equal(1m, runner.OwnPosition);
            Assert.Equal(-6.4m, runner.Ledger.Realized);
            Assert.Equal(native, stop.NativeIdentity);
            var current = Assert.IsType<OwnedOrderIntent>(session.OrderContext.Find(stop.IntentId));
            Assert.Equal(OrderTypes.Stop, current.Request.Type);
            Assert.Equal(OrderTypes.Market, Assert.IsType<NativeWorkingOrder>(current.WorkingOrder).Type);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            runner.Stop(flatten: false, keepProtection: true);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, false, true)]
    public async Task Same_UID_rotated_owner_adopts_kept_stop_and_books_shared_execution_and_correction_once(bool overlappingPoll, bool furtherNativeSnapshotLag, bool furtherExecutionIdentitiesHeld, bool rejectChangedQuantity)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-rotated-stop-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var originalHttp = new HttpClient(protocol, disposeHandler: false);
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 109, 1, TradeSide.Buy));
            await using var original = new BybitTradingSession(protocol.Settings, Path.Combine(root, "original", "orders.json"), originalHttp,
                clock: new ProviderClock(), executionRoot: LiveStore.Root);
            using var originalBroker = new BybitBroker(data, original, "Shared verified account");
            await original.RefreshAsync();
            var strategy = new ProtectedEntry { StopLossTicks = 50 };
            using var first = new LiveStrategyRunner(strategy, data, originalBroker, ChartPeriod.M1);
            first.Start();
            strategy.Open(2);
            await RefreshUntil(original, () => originalBroker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0) && original.OrdersReady);
            var entry = Assert.Single(originalBroker.ActiveOrders);
            protocol.Fill("shared-entry", 2, 100, .1m, orderId: entry.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-20), closedQuantity: 0);
            protocol.NetPosition = 2;
            protocol.AveragePositionPrice = 100;
            await RefreshUntil(original, () => first.OwnPosition == 2 && !first.Ledger.HasPendingPrices &&
                originalBroker.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && order.ExchangeOrderId.Length > 0) && original.OrdersReady);
            var originalStop = Assert.Single(originalBroker.ActiveOrders);
            string nativeStopId = originalStop.ExchangeOrderId;
            string key = first.Key;
            first.Stop(flatten: false, keepProtection: true);
            var keptIds = first.ProtectionOrderIds.ToArray();
            Assert.Single(keptIds);
            original.Retire();

            using var poll = new HeldExecutionPoll(protocol);
            using var rotatedHttp = new HttpClient(poll, disposeHandler: false);
            await using var rotated = new BybitTradingSession(protocol.Settings with { ApiKey = "rotated-key", ApiSecret = "rotated-secret" },
                Path.Combine(root, "rotated", "orders.json"), rotatedHttp, clock: new ProviderClock(), executionRoot: LiveStore.Root);
            using var currentBroker = new BybitBroker(data, rotated, "Shared verified account");
            await rotated.RefreshAsync();
            var runnerContext = furtherNativeSnapshotLag ? new QueuedSynchronizationContext() : null;
            var resumedStrategy = new ProtectedEntry { StopLossTicks = 50 };
            LiveStrategyRunner CreateResumedRunner()
            {
                var previousContext = SynchronizationContext.Current;
                try
                {
                    // Only this case queues runner callbacks for an explicit, nonoverlapping UI-like drain.
                    if (runnerContext != null) SynchronizationContext.SetSynchronizationContext(runnerContext);
                    return new LiveStrategyRunner(resumedStrategy, data, currentBroker, ChartPeriod.M1)
                    { AdoptProtectionIds = keptIds, ProtectOnStart = true };
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
            using var resumed = CreateResumedRunner();
            resumed.Start();
            runnerContext?.Drain();
            Assert.Equal(key, resumed.Key);
            Assert.Equal(2m, resumed.OwnPosition);
            Assert.Equal(keptIds, resumed.ProtectionOrderIds);
            var currentStop = Assert.Single(currentBroker.ActiveOrders);
            Assert.Equal(nativeStopId, currentStop.ExchangeOrderId);
            Assert.Single(resumed.Ledger.Fills);
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");

            bool ProjectedSharedExecution(BybitTradingSession session, decimal fee)
            {
                var report = session.ExecutionSource.Capture(ExecutionCursor.Start).Changes.LastOrDefault(report =>
                    report.Identity.Id.EndsWith(":shared-stop-partial", StringComparison.Ordinal));
                var projected = report == null ? null : session.OrderContext.Projected(report.Identity);
                return projected?.Report.Fees is { Length: 1 } fees && fees[0].Amount == fee;
            }

            protocol.TriggerStop(nativeStopId);
            protocol.Fill("shared-stop-partial", 1, 94, .2m, orderId: nativeStopId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-10), closedQuantity: 1);
            protocol.NetPosition = 1;
            if (overlappingPoll)
            {
                poll.Arm();
                Task currentRefresh = rotated.RefreshAsync();
                try
                {
                    await poll.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(1m, currentStop.Filled);
                    await RefreshUntil(original, () => ProjectedSharedExecution(original, .2m));
                    Assert.Equal(1m, resumed.OwnPosition);
                    Assert.Equal(1m, LiveStore.LoadLedger(LiveStore.Root, key, 1).Position);
                    Assert.Same(currentStop, Assert.Single(currentBroker.ActiveOrders));
                }
                finally
                {
                    poll.Release.TrySetResult();
                    await currentRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            else
                await RefreshUntil(original, () => ProjectedSharedExecution(original, .2m));
            await RefreshUntil(rotated, () => ProjectedSharedExecution(rotated, .2m));
            runnerContext?.Drain();
            var partial = LiveStore.LoadLedger(LiveStore.Root, key, 1);
            Assert.Equal(1m, partial.Position);
            Assert.Equal(.3m, partial.Fees);
            Assert.Equal(-6.3m, partial.Realized);
            Assert.False(partial.HasPendingPrices);
            Assert.Equal(2, partial.Fills.Count);
            Assert.Equal(1m, resumed.OwnPosition);
            Assert.Same(currentStop, Assert.Single(currentBroker.ActiveOrders));
            Assert.Equal(OrderTypes.Market, currentStop.Type);
            Assert.Equal(1m, currentStop.Unfilled);

            protocol.CorrectFee("shared-stop-partial", .3m, "USDT", nativeStopId);
            await RefreshUntil(rotated, () => ProjectedSharedExecution(rotated, .3m));
            await RefreshUntil(original, () => ProjectedSharedExecution(original, .3m));
            // Repeated complete reports from either retained source do not add quantity or another fee.
            await rotated.RefreshAsync();
            await original.RefreshAsync();
            runnerContext?.Drain();
            var corrected = LiveStore.LoadLedger(LiveStore.Root, key, 1);
            Assert.Equal(1m, corrected.Position);
            Assert.Equal(.4m, corrected.Fees);
            Assert.Equal(-6.4m, corrected.Realized);
            Assert.Equal(2, corrected.Fills.Count);
            Assert.True(corrected.CostsComplete);
            Assert.Equal(1m, resumed.OwnPosition);
            Assert.Same(currentStop, Assert.Single(currentBroker.ActiveOrders));
            Assert.Equal(nativeStopId, currentStop.ExchangeOrderId);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));

            foreach (bool otherAuthority in new[] { false, true })
            {
                protocol.ResponseFor = request => !otherAuthority && request.Path == "/v5/user/query-api"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"retCode":0,"result":{"userID":7654321,"readOnly":0}}""") }
                    : null;
                using var foreignHttp = new HttpClient(protocol, disposeHandler: false);
                var settings = protocol.Settings with
                {
                    ApiKey = otherAuthority ? "other-authority-key" : "other-uid-key",
                    BaseUrl = otherAuthority ? "http://127.0.0.1:2" : protocol.Settings.BaseUrl,
                };
                await using var foreign = new BybitTradingSession(settings,
                    Path.Combine(root, otherAuthority ? "other-authority" : "other-uid", "orders.json"), foreignHttp,
                    clock: new ProviderClock(), executionRoot: LiveStore.Root);
                using var foreignBroker = new BybitBroker(data, foreign, "Shared verified account");
                await foreign.RefreshAsync();
                using var foreignRunner = new LiveStrategyRunner(new ProtectedEntry { StopLossTicks = 50 }, data, foreignBroker, ChartPeriod.M1)
                { AdoptProtectionIds = keptIds, ProtectOnStart = true };
                foreignRunner.Start();
                Assert.NotEqual(key, foreignRunner.Key);
                Assert.Equal(0m, foreignRunner.OwnPosition);
                Assert.Empty(foreignRunner.Ledger.Fills);
                foreignRunner.Stop(flatten: false, keepProtection: true);
            }
            Assert.Equal(1m, LiveStore.LoadLedger(LiveStore.Root, key, 1).Position);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            if (furtherNativeSnapshotLag)
            {
                protocol.ResponseFor = null;
                Assert.True(ProjectedSharedExecution(rotated, .3m));
                Assert.True(resumed.IsRunning, resumed.StopReason);
                Assert.Equal(keptIds, resumed.ProtectionOrderIds);
                async Task<string> ActualSnapshot(string path)
                {
                    using var response = await originalHttp.GetAsync(protocol.Settings.BaseUrl + path);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();
                }
                var earlierSnapshots = new Dictionary<string, string>
                {
                    ["/v5/order/realtime"] = await ActualSnapshot("/v5/order/realtime"),
                    ["/v5/order/history"] = await ActualSnapshot("/v5/order/history"),
                    ["/v5/position/list"] = await ActualSnapshot("/v5/position/list"),
                };
                protocol.CorrectFee("shared-stop-partial", .4m, "USDT", nativeStopId);
                earlierSnapshots["/v5/execution/list"] = await ActualSnapshot("/v5/execution/list");
                // Independent polls can expose the prior fee while already seeing a further physical fill.
                protocol.CorrectFee("shared-stop-partial", .3m, "USDT", nativeStopId);
                string? retainedExecutionSnapshot = furtherExecutionIdentitiesHeld
                    ? await ActualSnapshot("/v5/execution/list") : null;
                protocol.Fill("shared-stop-further", .25m, 93, .05m, orderId: nativeStopId,
                    executionAt: FakeBybitProtocol.Now.AddSeconds(-5), closedQuantity: .25m);
                protocol.NetPosition = .75m;
                protocol.ResponseFor = request =>
                {
                    if (!request.Headers.TryGetValue("X-BAPI-API-KEY", out var apiKey)) return null;
                    string? snapshot = null;
                    if (apiKey == "rotated-key")
                        earlierSnapshots.TryGetValue(request.Path, out snapshot);
                    else if (furtherExecutionIdentitiesHeld && apiKey == protocol.Settings.ApiKey && request.Path == "/v5/execution/list")
                        snapshot = retainedExecutionSnapshot;
                    return snapshot == null ? null
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(snapshot) };
                };
                poll.Arm();
                Task feeRefresh = rotated.RefreshAsync();
                try
                {
                    await poll.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await RefreshUntil(original, () =>
                    {
                        if (furtherExecutionIdentitiesHeld)
                        {
                            var book = LiveStore.LoadLedger(LiveStore.Root, key, 1);
                            return book.Position == .75m && book.HasPendingPrices;
                        }
                        var report = original.ExecutionSource.Capture(ExecutionCursor.Start).Changes.LastOrDefault(item =>
                            item.Identity.Id.EndsWith(":shared-stop-further", StringComparison.Ordinal));
                        return report != null && original.OrderContext.Projected(report.Identity)?.Report.Fees[0].Amount == .05m;
                    });
                    AccountExecutionProjector.For(rotated.ExecutionSource, rotated.OrderContext).Synchronize();
                    runnerContext?.Drain();
                    Assert.Equal(.75m, resumed.OwnPosition);
                    Assert.Equal(1m, currentStop.Filled);
                    Assert.Equal(furtherExecutionIdentitiesHeld ? .4m : .45m, resumed.Ledger.Fees);
                    if (rejectChangedQuantity)
                    {
                        Assert.Equal(1.25m, originalStop.Filled);
                        protocol.CorrectQuantity("shared-stop-further", .1m, nativeStopId);
                        protocol.NetPosition = .9m;
                        await Assert.ThrowsAsync<InvalidDataException>(() => original.RefreshAsync());
                        Assert.False(original.PositionReady || original.OrdersReady || original.ExecutionsReady);
                        Assert.Equal(ExecutionAvailability.Unavailable, original.ExecutionSource.Capture(ExecutionCursor.Start).Availability);
                        Assert.Equal(.75m, LiveStore.LoadLedger(LiveStore.Root, key, 1).Position);
                        Assert.Equal(.75m, resumed.OwnPosition);
                        Assert.Equal(1.25m, originalStop.Filled);
                        Assert.Equal(1m, currentStop.Filled);
                    }
                }
                finally
                {
                    poll.Release.TrySetResult();
                    await feeRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                    runnerContext?.Drain();
                    protocol.ResponseFor = null;
                }
                Assert.True(ProjectedSharedExecution(rotated, .4m));
                Assert.True(resumed.IsRunning, resumed.StopReason);
                Assert.Equal(keptIds, resumed.ProtectionOrderIds);
                Assert.True(rotated.OrdersReady && rotated.ExecutionsReady && rotated.PositionReady);
                Assert.DoesNotContain(rotated.ExecutionSource.Capture(ExecutionCursor.Start).Changes, report =>
                    report.Identity.Id.EndsWith(":shared-stop-further", StringComparison.Ordinal));
                Assert.Equal(.75m, resumed.OwnPosition);
                if (furtherExecutionIdentitiesHeld)
                {
                    Assert.True(resumed.Ledger.HasPendingPrices);
                    Assert.Null(resumed.Ledger.NetRealized);
                    Assert.Null(resumed.Ledger.AuthoritativeNet);
                    Assert.Equal(.5m, resumed.Ledger.Fees);
                    Assert.Equal(2, resumed.Ledger.Fills.Count);
                    resumedStrategy.Open(.25m);
                    Assert.DoesNotContain(resumed.ActiveOrders, order => !order.IsProtective);
                }
                else
                {
                    Assert.Equal(.55m, resumed.Ledger.Fees);
                    Assert.Equal(-8.3m, resumed.Ledger.Realized);
                }
                Assert.Equal(1m, currentStop.Filled);
                Assert.Equal(1m, currentStop.Unfilled);
                Assert.Equal(OrderTypes.Market, currentStop.Type);
                Assert.False(currentStop.Canceled);
                Assert.Same(currentStop, Assert.Single(currentBroker.ActiveOrders));
                Assert.DoesNotContain(protocol.Requests, request => request.Path is "/v5/order/cancel" or "/v5/order/amend");
                Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            }
            resumed.Stop(flatten: false, keepProtection: true);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rotated_owner_after_owned_target_partial_prices_new_fill_and_corrects_owned_prefix_without_manual_prefix(bool manualPrefix)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-owned-prefix-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var originalHttp = new HttpClient(protocol, disposeHandler: false);
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 109, 1, TradeSide.Buy));
            await using var original = new BybitTradingSession(protocol.Settings, Path.Combine(root, "original", "orders.json"), originalHttp,
                clock: new ProviderClock(), executionRoot: LiveStore.Root);
            using var originalBroker = new BybitBroker(data, original, "Owned partial handoff");
            await original.RefreshAsync();
            var strategy = new ProtectedEntry();
            using var opening = new LiveStrategyRunner(strategy, data, originalBroker, ChartPeriod.M1);
            opening.Start();
            strategy.Open(2);
            await RefreshUntil(original, () => originalBroker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0) && original.OrdersReady);
            var entry = Assert.Single(originalBroker.ActiveOrders);
            protocol.Fill("owned-entry", 2, 100, .1m, orderId: entry.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-30), closedQuantity: 0);
            protocol.NetPosition = 2;
            protocol.AveragePositionPrice = 100;
            await RefreshUntil(original, () => opening.OwnPosition == 2 && !opening.Ledger.HasPendingPrices && original.OrdersReady);
            opening.Stop(flatten: false, keepProtection: true);

            var target = originalBroker.Exit(OrderTypes.Limit, OrderDirections.Sell, manualPrefix ? 2.25m : 2m, 110);
            await RefreshUntil(original, () => target.ExchangeOrderId.Length > 0 && original.OrdersReady);
            if (manualPrefix)
            {
                // A real independent manual opening offsets this target's pre-ownership exit.
                // Neither manual execution belongs to the strategy's typed entry or its subsequent owned exit.
                var manualOpening = originalBroker.Market(OrderDirections.Buy, .25m);
                await RefreshUntil(original, () => manualOpening.ExchangeOrderId.Length > 0 && original.OrdersReady);
                protocol.Fill("other-owner-opening", .25m, 100, .001m, orderId: manualOpening.ExchangeOrderId,
                    executionAt: FakeBybitProtocol.Now.AddSeconds(-25), closedQuantity: 0);
                protocol.NetPosition = 2.25m;
                await RefreshUntil(original, () => manualOpening.Status == OrderStatus.Filled && original.OrdersReady);
                protocol.Fill("manual-target-prefix", .25m, 120, .01m, orderId: target.ExchangeOrderId,
                    executionAt: FakeBybitProtocol.Now.AddSeconds(-24), closedQuantity: .25m);
                protocol.NetPosition = 2;
                await RefreshUntil(original, () => target.Filled == .25m && original.OrdersReady);
            }
            using var owner = new LiveStrategyRunner(new ProtectedEntry { TakeProfitTicks = 100 }, data, originalBroker, ChartPeriod.M1)
            { AdoptProtectionIds = new[] { target.ExchangeOrderId }, ProtectOnStart = true };
            owner.Start();
            Assert.Equal(2m, owner.OwnPosition);
            Assert.Single(owner.Ledger.Fills);
            protocol.Fill("owned-target-before-handoff", .25m, 110, .025m, orderId: target.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-20), closedQuantity: .25m);
            protocol.NetPosition = 1.75m;
            await RefreshUntil(original, () => owner.OwnPosition == 1.75m && !owner.Ledger.HasPendingPrices &&
                owner.Ledger.Fees == .125m && original.OrdersReady);
            string key = owner.Key;
            owner.Stop(flatten: false, keepProtection: true);
            var kept = owner.ProtectionOrderIds.ToArray();
            Assert.Single(kept);
            int creates = protocol.Requests.Count(request => request.Path == "/v5/order/create");
            original.Retire();
            await original.DisposeAsync(); // No retained source can repair or price this handoff.
            int originalRequests = protocol.Requests.Count(request => request.Headers.GetValueOrDefault("X-BAPI-API-KEY") == "fake-key");

            using var currentHttp = new HttpClient(protocol, disposeHandler: false);
            await using var current = new BybitTradingSession(protocol.Settings with { ApiKey = "rotated-key", ApiSecret = "rotated-secret" },
                Path.Combine(root, "current", "orders.json"), currentHttp, clock: new ProviderClock(), executionRoot: LiveStore.Root);
            using var broker = new BybitBroker(data, current, "Owned partial handoff");
            await current.RefreshAsync();
            var currentTarget = Assert.Single(broker.ActiveOrders);
            var known = broker.GetExecutions(currentTarget);
            Assert.True(known.CompleteCoverage);
            Assert.Equal(manualPrefix ? 2 : 1, known.Executions.Count);
            using var resumed = new LiveStrategyRunner(new ProtectedEntry { TakeProfitTicks = 100 }, data, broker, ChartPeriod.M1)
            { AdoptProtectionIds = kept, ProtectOnStart = true };
            resumed.Start();
            Assert.Equal(key, resumed.Key);
            Assert.Equal(1.75m, resumed.OwnPosition);
            Assert.Equal(kept, resumed.ProtectionOrderIds);
            Assert.Equal(target.ExchangeOrderId, currentTarget.ExchangeOrderId);
            await current.RefreshAsync();
            protocol.Fill("owned-target-after-handoff", .25m, 112, .03m, orderId: currentTarget.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-10), closedQuantity: .25m);
            protocol.NetPosition = 1.5m;
            await current.RefreshAsync();
            Assert.Contains(current.ExecutionSource.Capture(ExecutionCursor.Start).Changes,
                report => report.Identity.Id.EndsWith(":owned-target-after-handoff", StringComparison.Ordinal));
            Assert.Equal(1.5m, resumed.OwnPosition);
            Assert.False(resumed.Ledger.HasPendingPrices);
            Assert.Equal(.155m, resumed.Ledger.Fees);
            Assert.Equal(5.345m, resumed.Ledger.Realized);
            Assert.Equal(3, resumed.Ledger.Fills.Count);

            protocol.CorrectFee("owned-target-before-handoff", .05m, "USDT", currentTarget.ExchangeOrderId);
            await current.RefreshAsync();
            if (manualPrefix)
            {
                protocol.CorrectFee("manual-target-prefix", 9, "USDT", currentTarget.ExchangeOrderId);
                await current.RefreshAsync();
            }
            var restored = LiveStore.LoadLedger(LiveStore.Root, key, 1);
            foreach (var ledger in new[] { resumed.Ledger, restored })
            {
                Assert.Equal(1.5m, ledger.Position);
                Assert.Equal(.18m, ledger.Fees);
                Assert.Equal(5.32m, ledger.Realized);
                Assert.True(ledger.CostsComplete);
                Assert.False(ledger.HasPendingPrices);
                Assert.Equal(3, ledger.Fills.Count);
                Assert.DoesNotContain(ledger.Fills, fill => fill.ExecutionId?.Id.EndsWith(":manual-target-prefix", StringComparison.Ordinal) == true);
            }
            Assert.Same(currentTarget, Assert.Single(broker.ActiveOrders));
            Assert.Equal(1.5m, currentTarget.Unfilled);
            Assert.Equal(creates, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            Assert.Equal(originalRequests, protocol.Requests.Count(request => request.Headers.GetValueOrDefault("X-BAPI-API-KEY") == "fake-key"));
            resumed.Stop(flatten: false, keepProtection: true);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Older_retained_receipt_cannot_roll_back_a_later_committed_shared_execution_correction()
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-receipt-order-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var originalHttp = new HttpClient(protocol, disposeHandler: false);
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 109, 1, TradeSide.Buy));
            await using var original = new BybitTradingSession(protocol.Settings, Path.Combine(root, "original", "orders.json"), originalHttp,
                clock: new ProviderClock(), executionRoot: LiveStore.Root);
            using var originalBroker = new BybitBroker(data, original, "Retained receipt account");
            await original.RefreshAsync();
            var strategy = new ProtectedEntry { StopLossTicks = 50 };
            using var first = new LiveStrategyRunner(strategy, data, originalBroker, ChartPeriod.M1);
            first.Start();
            strategy.Open(2);
            await RefreshUntil(original, () => originalBroker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0) && original.OrdersReady);
            var entry = Assert.Single(originalBroker.ActiveOrders);
            protocol.Fill("receipt-entry", 2, 100, .1m, orderId: entry.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-20), closedQuantity: 0);
            protocol.NetPosition = 2;
            protocol.AveragePositionPrice = 100;
            await RefreshUntil(original, () => first.OwnPosition == 2 && !first.Ledger.HasPendingPrices &&
                originalBroker.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && order.ExchangeOrderId.Length > 0) && original.OrdersReady);
            var stop = Assert.Single(originalBroker.ActiveOrders);
            first.Stop(flatten: false, keepProtection: true);
            var kept = first.ProtectionOrderIds.ToArray();
            string key = first.Key;
            original.Retire();

            using var currentHttp = new HttpClient(protocol, disposeHandler: false);
            await using var current = new BybitTradingSession(protocol.Settings with { ApiKey = "rotated-key", ApiSecret = "rotated-secret" },
                Path.Combine(root, "current", "orders.json"), currentHttp, clock: new ProviderClock(), executionRoot: LiveStore.Root);
            using var broker = new BybitBroker(data, current, "Retained receipt account");
            await current.RefreshAsync();
            using var resumed = new LiveStrategyRunner(new ProtectedEntry { StopLossTicks = 50 }, data, broker, ChartPeriod.M1)
            { AdoptProtectionIds = kept, ProtectOnStart = true };
            resumed.Start();
            protocol.TriggerStop(stop.ExchangeOrderId);
            protocol.Fill("receipt-stop", 1, 94, .1m, orderId: stop.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-10), closedQuantity: 1);
            protocol.NetPosition = 1;
            await original.RefreshAsync();
            await current.RefreshAsync();
            Assert.Equal(.2m, LiveStore.LoadLedger(LiveStore.Root, key, 1).Fees);

            string ledgerFile = LiveStore.LedgerFile(key);
            string preservedHistory = ledgerFile + ".owned-receipt-fixture-history";
            // A directory cannot be opened as the real append stream on any supported filesystem.
            // Preserve the exact history bytes; never move the separate lease inode or ownership receipt store.
            File.Move(ledgerFile, preservedHistory);
            Directory.CreateDirectory(ledgerFile);
            try
            {
                protocol.CorrectFee("receipt-stop", .2m, "USDT", stop.ExchangeOrderId);
                await original.RefreshAsync();
                Assert.Contains(original.OrderContext.PendingReceipts(), receipt =>
                    receipt.Report.Identity.Id.EndsWith(":receipt-stop", StringComparison.Ordinal) &&
                    receipt.Report.Fees is { Length: 1 } fees && fees[0].Amount == .2m);
            }
            finally
            {
                Directory.Delete(ledgerFile);
                File.Move(preservedHistory, ledgerFile);
            }
            protocol.CorrectFee("receipt-stop", .3m, "USDT", stop.ExchangeOrderId);
            await current.RefreshAsync();
            Assert.Equal(.4m, LiveStore.LoadLedger(LiveStore.Root, key, 1).Fees);
            var originalProjector = AccountExecutionProjector.For(original.ExecutionSource, original.OrderContext);
            originalProjector.Synchronize(); // Retry the actual durable .2 receipt, not a fresh provider observation.
            var retained = LiveStore.LoadLedger(LiveStore.Root, key, 1);
            Assert.Equal(.4m, retained.Fees);
            Assert.Equal(-6.4m, retained.Realized);
            Assert.Equal(1m, retained.Position);
            Assert.Equal(2, retained.Fills.Count);
            Assert.Empty(original.OrderContext.PendingReceipts());
            Assert.Equal(.4m, resumed.Ledger.Fees);

            // A genuinely new correction from the old credential source remains authoritative, including a decrease.
            protocol.CorrectFee("receipt-stop", .05m, "USDT", stop.ExchangeOrderId);
            await original.RefreshAsync();
            await current.RefreshAsync();
            var latest = LiveStore.LoadLedger(LiveStore.Root, key, 1);
            Assert.Equal(.15m, latest.Fees);
            Assert.Equal(-6.15m, latest.Realized);
            Assert.Equal(1m, latest.Position);
            Assert.Equal(2, latest.Fills.Count);
            Assert.Equal(.15m, resumed.Ledger.Fees);
            Assert.Same(Assert.Single(broker.ActiveOrders), Assert.Single(resumed.ActiveOrders).Live);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            resumed.Stop(flatten: false, keepProtection: true);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RefreshUntil(BybitTradingSession session, Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        do
        {
            await session.RefreshAsync(deadline.Token);
            if (condition()) return;
            await Task.Delay(1, deadline.Token);
        }
        while (true);
    }
}
