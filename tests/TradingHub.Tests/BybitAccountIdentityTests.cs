using System.Net;
using System.Text.Json;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class BybitAccountIdentityTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, PointValue = 1 };
    private sealed class Passive : Strategy { protected override void OnBar(Bar bar) { } }

    [Fact]
    public async Task Preauthenticated_consumers_bind_once_to_verified_account_without_reowning_a_retired_credentials_pending_order()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-admission-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 101, 1, TradeSide.Buy));
            using var protocol = new FakeBybitProtocol { HoldCreate = true };
            using var http = new HttpClient(protocol, disposeHandler: false);
            string executionRoot = Path.Combine(root, "frozen-executions");
            await using var original = new BybitTradingSession(protocol.Settings, Path.Combine(root, "original", "orders.json"), http,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: executionRoot);
            using var broker = new BybitBroker(data, original, "Verified account");
            using var brackets = new BracketManager(broker);
            var context = broker.OrderContext;
            var manualOwner = context.ManualOwner;
            var projector = AccountExecutionProjector.For(broker.Executions, context);

            var denied = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 99);
            Assert.Equal(OrderStatus.Rejected, denied.Status);
            Assert.Null(denied.IntentId); // Rejection must not create provisional native ownership.
            Assert.Null(context.StorageRoot);
            Assert.Null(broker.PersistentAccountIdentity);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");

            await original.RefreshAsync();
            Assert.Same(context, broker.OrderContext);
            Assert.Same(projector, AccountExecutionProjector.For(broker.Executions, context));
            Assert.Same(broker, brackets.Account);
            Assert.Equal(manualOwner, context.ManualOwner);
            Assert.Equal(broker.PersistentAccountIdentity, context.Descriptor.Id);
            Assert.Equal(original.ContextIdentity, context.Descriptor.Generation);
            Assert.Equal(Path.GetFullPath(executionRoot), context.StorageRoot);

            var pending = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 99);
            await protocol.CreateReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(original.IsSubmissionUnknown(pending));
            var originalIntent = Assert.IsType<OwnedOrderIntent>(context.Find(pending.IntentId));
            original.Retire();

            using var rotatedHttp = new HttpClient(protocol, disposeHandler: false);
            await using var rotated = new BybitTradingSession(protocol.Settings with { ApiKey = "rotated-key", ApiSecret = "rotated-secret" },
                Path.Combine(root, "rotated", "orders.json"), rotatedHttp,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: executionRoot);
            using var rotatedBroker = new BybitBroker(data, rotated, "Verified account");
            await rotated.RefreshAsync();
            Assert.Equal(context.Descriptor.Id, rotated.OrderContext.Descriptor.Id);
            Assert.NotEqual(context.Descriptor.Generation, rotated.OrderContext.Descriptor.Generation);
            Assert.Null(rotated.OrderContext.Find(pending.IntentId));
            Assert.Equal(originalIntent, context.Find(pending.IntentId));
            Assert.Equal(Path.GetFullPath(executionRoot), context.StorageRoot);
            Assert.Same(pending, Assert.Single(original.Orders));

            broker.Cancel(pending);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed()
            {
                if (pending.ExchangeOrderId.Length > 0) acknowledged.TrySetResult();
                if (pending.Status == OrderStatus.Canceled) canceled.TrySetResult();
            }
            original.Changed += Changed;
            try
            {
                protocol.HoldCreate = false;
                protocol.CreateRelease.TrySetResult();
                await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await original.RefreshAsync();
                await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await original.RefreshAsync();
                Changed();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { original.Changed -= Changed; }
            Assert.Equal(originalIntent.ContextId, pending.ContextId);
            Assert.Equal(originalIntent.Owner.Id, pending.OwnerId);
            Assert.Equal("fake-key", Assert.Single(protocol.Requests, request => request.Path == "/v5/order/cancel").Headers["X-BAPI-API-KEY"]);
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Verified_readonly_native_account_cannot_gain_dispatch_authority_from_a_persistent_runner_ambient_root()
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-readonly-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "ambient-live-store");
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 101, 1, TradeSide.Buy));
            using var protocol = new FakeBybitProtocol();
            using var http = new HttpClient(protocol);
            await using var session = new BybitTradingSession(protocol.Settings, httpClient: http,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var broker = new BybitBroker(data, session, "Read-only native account");
            using var runner = new LiveStrategyRunner(new Passive(), data, broker, ChartPeriod.M1);
            await session.RefreshAsync();
            Assert.True(runner.TryPrepareIdentity(out _));
            Assert.Null(broker.OrderContext.StorageRoot);
            Assert.Throws<InvalidOperationException>(runner.Start);
            Assert.False(runner.IsRunning);
            Assert.Null(broker.OrderContext.StorageRoot);
            Assert.False(Directory.Exists(LiveStore.Root));
            Assert.Equal(OrderStatus.Rejected, broker.Limit(OrderDirections.Buy, 1, 99).Status);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Actual_runner_and_account_history_share_same_UID_key_rotation_but_never_an_unrelated_UID_or_authority()
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-identity-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 101, 1, TradeSide.Buy));
            const string displayName = "Same configured Bybit account";
            string legacyNameKey = $"{typeof(Passive).FullName}|{Btc.FeedId}:{Btc.Symbol}|{displayName}";
            Assert.Null(LiveStore.AppendFill(legacyNameKey, new LiveFill(FakeBybitProtocol.Now.UtcDateTime.AddDays(-1), true, 777, 2)));

            using var originalProtocol = new FakeBybitProtocol { NetPosition = 1, AveragePositionPrice = 101 };
            using var originalHttp = new HttpClient(originalProtocol);
            await using var original = new BybitTradingSession(originalProtocol.Settings, Path.Combine(root, "original", "orders.json"), originalHttp,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var originalBroker = new BybitBroker(data, original, displayName);
            using var first = new LiveStrategyRunner(new Passive(), data, originalBroker, ChartPeriod.M1) { AccountId = "same-profile", ProfileId = "same-profile" };
            Assert.Null(((IAccountExecutionSource)originalBroker).PersistentAccountIdentity);
            Assert.False(first.TryPrepareIdentity(out var waiting));
            Assert.False(string.IsNullOrWhiteSpace(waiting));
            Assert.Throws<InvalidOperationException>(() => AppServices.BybitJournalId("same-profile", original));
            await original.RefreshAsync();
            Assert.True(first.TryPrepareIdentity(out _));
            string originalKey = first.Key;
            string originalJournal = AppServices.BybitJournalId("same-profile", original);
            Assert.NotEqual(legacyNameKey, originalKey);
            Assert.Null(LiveStore.AppendFill(originalKey, new LiveFill(FakeBybitProtocol.Now.UtcDateTime.AddDays(-1), true, 1, 101,
                Btc.Symbol, Btc.PointValue)
            {
                Kind = LiveFillKind.ImportedExecution, ImportedExecutionId = "bybit:demo-1234567:BTCUSDT:old-order:old-fill",
                Fees = new[] { new ExecutionFee(0, "USDT") }, FeesComplete = true, Currency = "USDT",
            }));
            first.Start();
            Assert.Equal(1m, first.Ledger.Position);
            first.Stop(flatten: false, keepProtection: true);

            using var rotatedProtocol = new FakeBybitProtocol { NetPosition = 1, AveragePositionPrice = 101 };
            using var rotatedHttp = new HttpClient(rotatedProtocol);
            await using var rotated = new BybitTradingSession(rotatedProtocol.Settings with { ApiKey = "rotated-key", ApiSecret = "rotated-secret" },
                Path.Combine(root, "rotated", "orders.json"), rotatedHttp, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var rotatedBroker = new BybitBroker(data, rotated, displayName);
            using var sameAccount = new LiveStrategyRunner(new Passive(), data, rotatedBroker, ChartPeriod.M1) { AccountId = "same-profile", ProfileId = "same-profile" };
            Assert.False(sameAccount.TryPrepareIdentity(out _));
            await rotated.RefreshAsync();
            Assert.NotEqual(original.ContextIdentity, rotated.ContextIdentity);
            Assert.Equal(((IAccountExecutionSource)originalBroker).PersistentAccountIdentity, ((IAccountExecutionSource)rotatedBroker).PersistentAccountIdentity);
            Assert.True(sameAccount.TryPrepareIdentity(out _));
            Assert.Equal(originalKey, sameAccount.Key);
            Assert.Equal(originalJournal, AppServices.BybitJournalId("same-profile", rotated));
            Assert.Equal(displayName, rotatedBroker.Name);
            sameAccount.Start();
            Assert.Equal(1m, sameAccount.Ledger.Position);
            sameAccount.Stop(flatten: false, keepProtection: true);

            using var unrelatedProtocol = new FakeBybitProtocol { NetPosition = 1, AveragePositionPrice = 202 };
            unrelatedProtocol.ResponseFor = request => request.Path == "/v5/user/query-api" ?
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"retCode\":0,\"result\":{\"userID\":9876543}}") } : null;
            using var unrelatedHttp = new HttpClient(unrelatedProtocol);
            await using var unrelated = new BybitTradingSession(unrelatedProtocol.Settings with { ApiKey = "other-user-key", ApiSecret = "other-user-secret" },
                Path.Combine(root, "unrelated", "orders.json"), unrelatedHttp, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var unrelatedBroker = new BybitBroker(data, unrelated, displayName);
            using var otherAccount = new LiveStrategyRunner(new Passive(), data, unrelatedBroker, ChartPeriod.M1) { AccountId = "same-profile", ProfileId = "same-profile" };
            await unrelated.RefreshAsync();
            Assert.True(otherAccount.TryPrepareIdentity(out _));
            Assert.NotEqual(originalKey, otherAccount.Key);
            Assert.NotEqual(originalJournal, AppServices.BybitJournalId("same-profile", unrelated));
            Assert.Equal(displayName, unrelatedBroker.Name);
            otherAccount.Start();
            Assert.Equal(0m, otherAccount.Ledger.Position); // Same display name and net account size do not transfer ownership.
            otherAccount.Stop(flatten: false, keepProtection: true);

            using var authorityProtocol = new FakeBybitProtocol { NetPosition = 1, AveragePositionPrice = 303 };
            using var authorityHttp = new HttpClient(authorityProtocol);
            await using var authority = new BybitTradingSession(authorityProtocol.Settings with { BaseUrl = "http://127.0.0.1:2" },
                Path.Combine(root, "authority", "orders.json"), authorityHttp, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var authorityBroker = new BybitBroker(data, authority, displayName);
            using var otherAuthority = new LiveStrategyRunner(new Passive(), data, authorityBroker, ChartPeriod.M1) { AccountId = "same-profile", ProfileId = "same-profile" };
            await authority.RefreshAsync();
            Assert.Equal(original.AccountIdentity, authority.AccountIdentity);
            Assert.True(otherAuthority.TryPrepareIdentity(out _));
            Assert.NotEqual(originalKey, otherAuthority.Key);
            Assert.NotEqual(originalJournal, AppServices.BybitJournalId("same-profile", authority));
            otherAuthority.Start();
            Assert.Equal(0m, otherAuthority.Ledger.Position);
            otherAuthority.Stop(flatten: false, keepProtection: true);
            Assert.DoesNotContain(originalProtocol.Requests, request => request.Path == "/v5/order/create");
            Assert.DoesNotContain(rotatedProtocol.Requests, request => request.Path == "/v5/order/create");
            Assert.DoesNotContain(unrelatedProtocol.Requests, request => request.Path == "/v5/order/create");
            Assert.DoesNotContain(authorityProtocol.Requests, request => request.Path == "/v5/order/create");
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task Real_Kvp_saved_partial_target_keeps_financial_ownership_through_verified_rotation_without_native_prefix_promotion()
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-legacy-prefix-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var originalHttp = new HttpClient(protocol, disposeHandler: false);
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 109, 1, TradeSide.Buy));
            await using var original = new BybitTradingSession(protocol.Settings, Path.Combine(root, "original", "orders.json"), originalHttp,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var originalBroker = new BybitBroker(data, original, "Saved KVP account");
            await original.RefreshAsync();
            var entry = originalBroker.Limit(OrderDirections.Buy, 2, 100);
            await Reconcile(original, () => entry.ExchangeOrderId.Length > 0 && original.OrdersReady);
            protocol.Fill("saved-entry", 2, 100, .1m, orderId: entry.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-20), closedQuantity: 0);
            protocol.NetPosition = 2;
            protocol.AveragePositionPrice = 100;
            await Reconcile(original, () => entry.Status == OrderStatus.Filled && original.OrdersReady);
            var target = originalBroker.Exit(OrderTypes.Limit, OrderDirections.Sell, 2, 110);
            await Reconcile(original, () => target.ExchangeOrderId.Length > 0 && original.OrdersReady);
            protocol.Fill("saved-owned-partial", .25m, 110, .025m, orderId: target.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-10), closedQuantity: .25m);
            protocol.NetPosition = 1.75m;
            await Reconcile(original, () => target.Filled == .25m && original.OrdersReady);
            var nativeEntry = Assert.Single(originalBroker.GetExecutions(entry).Executions);
            var nativeExit = Assert.Single(originalBroker.GetExecutions(target).Executions);
            using var savedIdentity = new LiveStrategyRunner(new Passive(), data, originalBroker, ChartPeriod.M1);
            Assert.True(savedIdentity.TryPrepareIdentity(out _));
            string key = savedIdentity.Key;
            string savedBytes = Row(nativeEntry, true, 1) + "\n" + Row(nativeExit, false, 2) + "\n";
            string file = LiveStore.LedgerFile(key);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, savedBytes);
            var saved = LiveStore.LoadLedger(LiveStore.Root, key, 1);
            Assert.Equal(1.75m, saved.Position);
            Assert.Equal(.125m, saved.Fees);
            Assert.All(saved.Fills, fill =>
            {
                Assert.Equal(LiveFillKind.ImportedExecution, fill.Kind);
                Assert.Null(fill.ContextId);
                Assert.Null(fill.OwnerId);
                Assert.Null(fill.AccountingOrder);
            });
            original.Retire();
            await original.DisposeAsync();

            using var currentHttp = new HttpClient(protocol, disposeHandler: false);
            await using var current = new BybitTradingSession(protocol.Settings with { ApiKey = "rotated-key", ApiSecret = "rotated-secret" },
                Path.Combine(root, "current", "orders.json"), currentHttp,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var broker = new BybitBroker(data, current, "Saved KVP account");
            await current.RefreshAsync();
            var currentTarget = Assert.Single(broker.ActiveOrders);
            Assert.Equal(.25m, currentTarget.Filled); // Only the real native report establishes this counter.
            using var resumed = new LiveStrategyRunner(new Passive { TakeProfitTicks = 100 }, data, broker, ChartPeriod.M1)
            { AdoptProtectionIds = new[] { target.ExchangeOrderId }, ProtectOnStart = true };
            resumed.Start();
            Assert.Equal(key, resumed.Key);
            Assert.Equal(1.75m, resumed.OwnPosition);
            var claim = Assert.IsType<OwnedOrderIntent>(current.OrderContext.Find(currentTarget.IntentId));
            Assert.Equal(.25m, claim.QuantityBaseline);
            await current.RefreshAsync();
            protocol.CorrectFee("saved-owned-partial", .05m, "USDT", currentTarget.ExchangeOrderId);
            await current.RefreshAsync();
            Assert.Equal(.05m, Assert.Single(broker.GetExecutions(currentTarget).Executions).AccountFee);
            Assert.Equal(.15m, resumed.Ledger.Fees);
            Assert.Equal(2.35m, resumed.Ledger.Realized);
            Assert.Equal(1.75m, resumed.OwnPosition);
            var recovered = LiveStore.LoadLedger(LiveStore.Root, key, 1);
            Assert.Equal(.15m, recovered.Fees);
            Assert.Equal(1.75m, recovered.Position);
            Assert.Equal(2, recovered.Fills.Count);
            Assert.StartsWith(savedBytes, File.ReadAllText(file));
            Assert.Equal(.25m, currentTarget.Filled);
            Assert.Equal(.25m, Assert.IsType<OwnedOrderIntent>(current.OrderContext.Find(currentTarget.IntentId)).QuantityBaseline);
            Assert.Same(currentTarget, Assert.Single(broker.ActiveOrders));
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));

            protocol.Fill("current-owned-post-prefix", .25m, 112, .03m, orderId: currentTarget.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-5), closedQuantity: .25m);
            protocol.NetPosition = 1.5m;
            await current.RefreshAsync();
            Assert.Equal(1.5m, resumed.OwnPosition);
            Assert.Equal(.5m, currentTarget.Filled);
            Assert.Equal(.25m, Assert.IsType<OwnedOrderIntent>(current.OrderContext.Find(currentTarget.IntentId)).QuantityBaseline);
            Assert.False(resumed.Ledger.HasPendingPrices);
            Assert.Equal(.18m, resumed.Ledger.Fees);
            Assert.Equal(5.32m, resumed.Ledger.Realized);
            protocol.CorrectFee("saved-owned-partial", .075m, "USDT", currentTarget.ExchangeOrderId);
            await current.RefreshAsync();
            foreach (var ledger in new[] { resumed.Ledger, LiveStore.LoadLedger(LiveStore.Root, key, 1) })
            {
                Assert.Equal(1.5m, ledger.Position);
                Assert.Equal(.205m, ledger.Fees);
                Assert.Equal(5.295m, ledger.Realized);
                Assert.False(ledger.HasPendingPrices);
                Assert.Equal(3, ledger.Fills.Count);
                var prefix = Assert.Single(ledger.Fills, fill => fill.ImportedExecutionId == nativeExit.Id);
                Assert.Equal(LiveFillKind.ImportedExecution, prefix.Kind);
                Assert.Null(prefix.AccountingOrder); // Financial legacy proof never became a native quantity claim.
                Assert.Equal(2L, prefix.ExecutionSequence);
            }
            Assert.Equal(.5m, currentTarget.Filled);
            Assert.Equal(.25m, Assert.IsType<OwnedOrderIntent>(current.OrderContext.Find(currentTarget.IntentId)).QuantityBaseline);
            Assert.StartsWith(savedBytes, File.ReadAllText(file));
            Assert.Same(currentTarget, Assert.Single(broker.ActiveOrders));
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            resumed.Stop(flatten: false, keepProtection: true);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        static string Row(AccountExecution fill, bool buy, long sequence) => JsonSerializer.Serialize(new
        {
            Time = fill.TimeUtc, Buy = buy, fill.Quantity, fill.Price, Symbol = Btc.Symbol, PointValue = Btc.PointValue,
            ExecutionId = fill.Id, fill.Fee, fill.FeeCurrency, fill.AccountFee, ExecutionSequence = sequence,
        });
        static async Task Reconcile(BybitTradingSession session, Func<bool> condition)
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Destructive_retirement_cannot_reauthorize_the_old_native_owner_after_healthy_projection(bool markerAlreadyPublished)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-retirement-failure-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 100, 1, TradeSide.Buy));
            await using var session = new BybitTradingSession(protocol.Settings, Path.Combine(root, "native", "orders.json"), http,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var broker = new BybitBroker(data, session, "Retirement failure account");
            await session.RefreshAsync();
            using var runner = new LiveStrategyRunner(new Passive(), data, broker, ChartPeriod.M1);
            runner.Start();
            Assert.True(runner.IsRunning);
            Assert.True(session.SnapshotReady);
            Assert.Equal(0m, runner.OwnPosition);
            string history = LiveStore.LedgerFile(runner.Key);
            string obstruction = history + ".id.tmp";
            Directory.CreateDirectory(obstruction);
            try
            {
                if (markerAlreadyPublished)
                {
                    // An actual interrupted deletion leaves the durable marker before this reset attempt.
                    var failure = Record.Exception(() => LiveStore.DeleteFills(LiveStore.Root, runner.Key));
                    Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
                }
                Assert.False(runner.ResetLedger());
                // This is a post-publication failure, not a preflight refusal or synthetic exception.
                Assert.True(File.Exists(history + ".delete"));
                Assert.True(Directory.Exists(obstruction));
            }
            finally { Directory.Delete(obstruction); }

            // Repair the owned filesystem fault and let ordinary recovery complete its real deletion.
            // A healthy account source must still not make the old strategy owner valid again.
            Assert.Empty(LiveStore.LoadLedger(LiveStore.Root, runner.Key, Btc.PointValue).Fills);
            AccountExecutionProjector.For(session.ExecutionSource, session.OrderContext).Synchronize();
            await session.RefreshAsync();
            Assert.False(session.OrderContext.HasStorageFault);
            Assert.True(session.SnapshotReady);
            runner.SetPaused(false); // An explicit healthy resume cannot revive the retiring owner either.
            var refused = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1, 100, "Opening after interrupted retirement");
            await session.RefreshAsync();
            Assert.Equal(StrategyOrderState.Canceled, refused.State);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");
            Assert.Empty(broker.ActiveOrders);
            await session.DisposeAsync();
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Initial_physical_admission_failure_retries_the_same_observation_after_storage_recovers()
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-admission-retry-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 100, 1, TradeSide.Buy));
            await using var session = new BybitTradingSession(protocol.Settings, Path.Combine(root, "native", "orders.json"), http,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var broker = new BybitBroker(data, session, "Admission retry account");
            await session.RefreshAsync();
            // Runner callbacks are marshalled to the context captured at construction. Drain them explicitly
            // so assertions never depend on which xUnit context the test continuation resumed on.
            var runnerContext = new BybitRunnerExecutionTests.QueuedSynchronizationContext();
            LiveStrategyRunner CreateRunner()
            {
                var previousContext = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(runnerContext);
                    return new LiveStrategyRunner(new Passive(), data, broker, ChartPeriod.M1);
                }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            }
            using var runner = CreateRunner();
            runner.Start();
            runnerContext.Drain();
            runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1, 100, "Actual admission retry");
            await ReconcileNative(session, () =>
            {
                runnerContext.Drain();
                return broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0) && session.OrdersReady;
            });
            var entry = Assert.Single(broker.ActiveOrders);
            string authorityDirectory = LiveStore.LedgerFile(runner.Key) + ".executions";
            Assert.False(Directory.Exists(authorityDirectory));
            File.WriteAllText(authorityDirectory, "owned temporary admission-path obstruction");
            try
            {
                protocol.Fill("admission-retry-fill", 1, 100, .1m, orderId: entry.ExchangeOrderId,
                    executionAt: FakeBybitProtocol.Now.AddSeconds(-10), closedQuantity: 0);
                protocol.NetPosition = 1;
                protocol.AveragePositionPrice = 100;
                await session.RefreshAsync();
                runnerContext.Drain();
                Assert.Equal(1m, runner.OwnPosition);
                Assert.True(runner.Ledger.HasPendingPrices);
            }
            finally { File.Delete(authorityDirectory); }
            var before = session.ExecutionSource.Capture(ExecutionCursor.Start);
            Assert.Single(before.Changes);
            AccountExecutionProjector.For(session.ExecutionSource, session.OrderContext).Synchronize();
            runnerContext.Drain();
            Assert.False(runner.Ledger.HasPendingPrices);
            Assert.Equal(.1m, runner.Ledger.Fees);
            Assert.Single(runner.Ledger.Fills);
            Assert.Equal(1m, LiveStore.LoadLedger(LiveStore.Root, runner.Key, 1).Position);
            Assert.Empty(session.OrderContext.PendingReceipts());
            Assert.Equal(before.Revision, session.ExecutionSource.Capture(ExecutionCursor.Start).Revision);
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
            runner.Stop(flatten: false, keepProtection: true);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_recovering_reader_or_native_quantity_append_delivers_the_actual_head_to_foreign_live_risk(bool quantityAppend)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-recovered-risk-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol();
        using var originalHttp = new HttpClient(protocol, disposeHandler: false);
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 100, 1, TradeSide.Buy));
            await using var original = new BybitTradingSession(protocol.Settings, Path.Combine(root, "original", "orders.json"), originalHttp,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var originalBroker = new BybitBroker(data, original, "Shared recovered risk");
            await original.RefreshAsync();
            using var first = new LiveStrategyRunner(new Passive(), data, originalBroker, ChartPeriod.M1);
            first.Start();
            first.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1, 100, "Actual original owner");
            await ReconcileNative(original, () => originalBroker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0) && original.OrdersReady);
            var entry = Assert.Single(originalBroker.ActiveOrders);
            protocol.Fill("reader-owned-entry", 1, 100, .1m, orderId: entry.ExchangeOrderId,
                executionAt: FakeBybitProtocol.Now.AddSeconds(-10), closedQuantity: 0);
            protocol.NetPosition = 1;
            protocol.AveragePositionPrice = 100;
            await ReconcileNative(original, () => first.OwnPosition == 1 && !first.Ledger.HasPendingPrices);
            string key = first.Key;
            var originalFill = Assert.Single(first.Ledger.Fills);
            var owner = Assert.IsType<OwnedOrderIntent>(original.OrderContext.Find(entry.IntentId)).Owner;
            first.Stop(flatten: false, keepProtection: true);
            original.Retire();

            using var currentHttp = new HttpClient(protocol, disposeHandler: false);
            await using var current = new BybitTradingSession(protocol.Settings with { ApiKey = "rotated-key", ApiSecret = "rotated-secret" },
                Path.Combine(root, "current", "orders.json"), currentHttp,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now), executionRoot: LiveStore.Root);
            using var broker = new BybitBroker(data, current, "Shared recovered risk");
            await current.RefreshAsync();
            using var resumed = new LiveStrategyRunner(new Passive(), data, broker, ChartPeriod.M1)
            { MaxDrawdown = .2m, CloseOnLimit = false };
            resumed.Start();
            var limitHit = new ManualResetEventSlim(); // Not disposed: a late delivery may still raise it.
            resumed.LimitHit += (_, _) => limitHit.Set();
            Assert.Equal(.1m, resumed.Ledger.Fees);
            Assert.True(resumed.IsRunning);

            // The public durable-admission boundary may complete before the originating native receipt/projector.
            // All fields except the genuine external fee correction come from the actual original owned execution.
            protocol.CorrectFee("reader-owned-entry", .3m, "USDT", entry.ExchangeOrderId);
            var source = original.ExecutionSource.Capture(ExecutionCursor.Start);
            var report = Assert.Single(source.Changes) with { Fees = new[] { new ExecutionFee(.3m, "USDT") } };
            var receipt = ExecutionReceipt.Create(new(source.Epoch, source.Revision + 1), report);
            var head = originalFill with { Fees = report.Fees, ProjectionRevision = null };
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(LiveStore.Root, key, owner.LedgerGeneration, receipt.Id, head).Status);
            if (quantityAppend)
            {
                var native = Assert.IsType<NativeOrderIdentity>(entry.NativeIdentity);
                var quantity = new LiveFill(report.Time, true, 0, 0, Btc.Symbol, Btc.PointValue)
                {
                    Kind = LiveFillKind.QuantityObservation, OrderId = originalFill.OrderId,
                    CumulativeQuantity = entry.Filled, ContextId = originalFill.ContextId,
                    OwnerId = owner.Id, OwnerGeneration = owner.LedgerGeneration,
                    AccountingOrder = new(native, 0, entry.Filled),
                };
                Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                    LiveStore.AppendOwnedEvent(LiveStore.Root, key, owner.LedgerGeneration, quantity).Status);
            }
            else Assert.Equal(.3m, LiveStore.LoadLedger(LiveStore.Root, key, 1).Fees);
            AccountExecutionProjector.For(current.ExecutionSource, current.OrderContext).Synchronize();
            // The recovered head is published to a thread-pool financial drain. Synchronize returns without delivering
            // it while that drain is in flight; the drawdown stop raises LimitHit once the head reaches the runner.
            Assert.True(limitHit.Wait(TimeSpan.FromSeconds(10)), "The recovered head did not reach the resumed runner's risk.");
            Assert.Equal(.3m, resumed.Ledger.Fees);
            Assert.Equal(-.3m, resumed.Ledger.Realized);
            Assert.Equal(.3m, resumed.AvailableCurrentDrawdown);
            Assert.False(resumed.IsRunning);
            Assert.Contains("Maximum drawdown", resumed.StopReason);
            Assert.Equal(1m, resumed.OwnPosition);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Unchanged,
                LiveStore.ProjectExecution(LiveStore.Root, key, owner.LedgerGeneration, head, receipt.Id).Status);
            Assert.Equal(.3m, resumed.Ledger.Fees);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task ReconcileNative(BybitTradingSession session, Func<bool> condition)
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
