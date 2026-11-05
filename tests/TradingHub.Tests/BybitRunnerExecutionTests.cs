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

    [Fact]
    public async Task Held_actual_execution_facts_never_book_market_proxies_and_late_native_fees_correct_the_saved_protection_basis()
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
            await using var session = new BybitTradingSession(protocol.Settings, Path.Combine(root, "provider", "context.json"), http, clock: clock);
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
            Assert.Equal(0m, runner.Ledger.Position);
            Assert.Empty(runner.Ledger.Fills);
            Assert.Empty(LiveStore.LoadLedger(runner.Key, 1).Fills);
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");

            // Release both independent delays. A real execution identity, not the cumulative order report, now supplies the basis.
            protocol.HoldCreate = false;
            protocol.CreateRelease.TrySetResult();
            hideExecutions = false;
            await RefreshUntil(session, () => runner.Ledger.Position == .4m && broker.ActiveOrders.Any(order => order.Type == OrderTypes.Stop));
            var firstStop = Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop);
            Assert.True(firstStop.ReduceOnly);
            Assert.Equal(96m, firstStop.TriggerPrice);
            Assert.Equal(101m, runner.Ledger.AveragePrice);
            var first = Assert.Single(runner.Ledger.Fills);
            Assert.Equal(firstTime, first.Time);
            Assert.Equal(101m, first.Price);
            Assert.Equal(.02m, first.Fee);
            Assert.Equal("USDT", first.FeeCurrency);
            Assert.Equal(.02m, first.AccountFee);
            Assert.StartsWith("bybit:demo-1234567:BTCUSDT:exchange-1:", first.ExecutionId);
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
            Assert.Equal(.09m, restored.Fills.Single(fill => fill.ExecutionId!.EndsWith(":native-part-b", StringComparison.Ordinal)).AccountFee);
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
                sendGuard: (_, _, _, reduceOnly) => permitted || reduceOnly ? null : "Permission revoked before order transport", clock: clock);
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
