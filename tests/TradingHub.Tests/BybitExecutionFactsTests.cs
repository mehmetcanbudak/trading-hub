using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed class BybitExecutionFactsTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m,
        VolumeStep = .001m, BaseAsset = "BTC", QuoteAsset = "USDT" };

    [Fact]
    public async Task Reported_partial_fill_keeps_only_confirmed_native_facts_until_missing_execution_ids_arrive()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, session, "Bybit fake");
        var source = Assert.IsAssignableFrom<IAccountExecutionSource>(broker);
        await session.RefreshAsync();
        var order = broker.Limit(OrderDirections.Buy, 1, 100);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        Assert.True(source.GetExecutions(order).CompleteCoverage);
        Assert.Empty(source.GetExecutions(order).Executions);
        string link = ClientLink(protocol);
        protocol.Fill("native-a", .3m, 101.25m, .015m);
        protocol.Fill("native-b", .4m, 104.5m, .020m);
        long firstTime = FakeBybitProtocol.Now.AddSeconds(-20).ToUnixTimeMilliseconds();
        long secondTime = FakeBybitProtocol.Now.AddSeconds(-10).ToUnixTimeMilliseconds();
        var first = Execution("native-a", "exchange-1", link, "BTCUSDT", .3m, 101.25m, firstTime, .015m, "USDT");
        protocol.ResponseFor = request => request.Path == "/v5/execution/list" ? Rows() : null;
        await session.RefreshAsync();
        Assert.Equal(.7m, order.Filled);
        Assert.Empty(source.GetExecutions(order).Executions);
        Assert.False(source.GetExecutions(order).CompleteCoverage);
        protocol.ResponseFor = request => request.Path == "/v5/execution/list" ? Rows(first) : null;

        await session.RefreshAsync();

        Assert.Equal(.7m, order.Filled);
        var partial = source.GetExecutions(order);
        Assert.False(partial.CompleteCoverage);
        var confirmed = Assert.Single(partial.Executions);
        Assert.Equal("bybit:demo-1234567:BTCUSDT:exchange-1:native-a", confirmed.Id);
        Assert.Equal(.3m, confirmed.Quantity);
        Assert.Equal(101.25m, confirmed.Price);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(firstTime).UtcDateTime, confirmed.TimeUtc);
        Assert.Equal(DateTimeKind.Utc, confirmed.TimeUtc.Kind);
        Assert.Equal(.015m, confirmed.Fee);
        Assert.Equal("USDT", confirmed.FeeCurrency);
        Assert.Equal(.015m, confirmed.AccountFee);
        var second = Execution("native-b", "exchange-1", link, "BTCUSDT", .4m, 104.5m, secondTime, .020m, "USDT");
        protocol.ResponseFor = request => request.Path == "/v5/execution/list" ? Rows(second, first) : null;

        await session.RefreshAsync();

        var complete = source.GetExecutions(order);
        Assert.True(complete.CompleteCoverage);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Equal(new[] { 101.25m, 104.5m }, complete.Executions.Select(execution => execution.Price));
        Assert.Equal(new[] { .3m, .4m }, complete.Executions.Select(execution => execution.Quantity));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(secondTime).UtcDateTime, complete.Executions[1].TimeUtc);
        Assert.Single(partial.Executions);
        Assert.False(partial.CompleteCoverage);
        Assert.Throws<NotSupportedException>(() => ((IList<AccountExecution>)complete.Executions)[0] = confirmed with { Price = 1 });
    }

    [Theory]
    [InlineData(null, "", null)]
    [InlineData(null, "USDT", null)]
    [InlineData("0.02", "MNT", null)]
    [InlineData("0.03", "", null)]
    [InlineData("0", "USDT", "0")]
    [InlineData("-0.01", "USDT", "-0.01")]
    public async Task Native_fee_knowledge_and_late_corrections_are_preserved_without_guessing_account_currency(
        string? feeText, string currency, string? accountFeeText)
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, session, "Bybit fake");
        var source = (IAccountExecutionSource)broker;
        await session.RefreshAsync();
        var order = broker.Limit(OrderDirections.Buy, 1, 100);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        decimal? fee = feeText == null ? null : decimal.Parse(feeText, CultureInfo.InvariantCulture);
        protocol.Fill("fee-correction", .25m, 102.75m, fee, executionAt: FakeBybitProtocol.Now.AddSeconds(-45), feeCurrency: currency);

        await session.RefreshAsync();

        var original = source.GetExecutions(order);
        Assert.True(original.CompleteCoverage);
        var fact = Assert.Single(original.Executions);
        Assert.Equal(fee, fact.Fee);
        Assert.Equal(currency, fact.FeeCurrency);
        Assert.Equal(accountFeeText == null ? (decimal?)null : decimal.Parse(accountFeeText, CultureInfo.InvariantCulture), fact.AccountFee);
        long revision = session.ExecutionRevision;
        await session.RefreshAsync();
        Assert.Equal(revision, session.ExecutionRevision);
        Assert.Same(original, source.GetExecutions(order));
        AccountExecutionSnapshot? changed = null;
        void OnChanged() => changed = source.GetExecutions(order);
        broker.Changed += OnChanged;
        try
        {
            protocol.CorrectFee("fee-correction", .019m, "USDT");
            await session.RefreshAsync();
        }
        finally { broker.Changed -= OnChanged; }

        Assert.True(session.ExecutionRevision > revision);
        var corrected = source.GetExecutions(order);
        Assert.Same(corrected, changed);
        var correctedFact = Assert.Single(corrected.Executions);
        Assert.Equal(fact.Id, correctedFact.Id);
        Assert.Equal(.019m, correctedFact.Fee);
        Assert.Equal("USDT", correctedFact.FeeCurrency);
        Assert.Equal(.019m, correctedFact.AccountFee);
        Assert.Equal(fact.TimeUtc, correctedFact.TimeUtc);
        Assert.Equal(fact.Price, correctedFact.Price);
        Assert.Equal(.25m, order.Filled);
        Assert.Equal(fee, fact.Fee);
        Assert.Equal(currency, fact.FeeCurrency);
        revision = session.ExecutionRevision;
        protocol.CorrectFee("fee-correction", null, "MNT");
        await session.RefreshAsync();
        var unknownAmount = Assert.Single(source.GetExecutions(order).Executions);
        Assert.Equal(fact.Id, unknownAmount.Id);
        Assert.Equal("MNT", unknownAmount.FeeCurrency);
        Assert.Null(unknownAmount.Fee);
        Assert.Null(unknownAmount.AccountFee);
        Assert.True(session.ExecutionRevision > revision);
        revision = session.ExecutionRevision;
        protocol.CorrectFee("fee-correction", .0002m, "MNT");
        await session.RefreshAsync();
        var nonQuote = Assert.Single(source.GetExecutions(order).Executions);
        Assert.Equal(fact.Id, nonQuote.Id);
        Assert.Equal(.0002m, nonQuote.Fee);
        Assert.Equal("MNT", nonQuote.FeeCurrency);
        Assert.Null(nonQuote.AccountFee);
        Assert.True(session.ExecutionRevision > revision);
        Assert.Equal(.019m, correctedFact.AccountFee);
    }

    [Fact]
    public async Task Ambiguous_submission_retains_unknown_execution_coverage_until_authoritative_lookup_and_untracked_rejection_stays_unknown()
    {
        using var protocol = new FakeBybitProtocol { UnknownCreate = true, HideOrders = true };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, session, "Bybit fake");
        var source = (IAccountExecutionSource)broker;
        await session.RefreshAsync();
        var order = broker.Limit(OrderDirections.Buy, 1, 100);
        await Observe(session, () => session.LastError?.Contains("placement outcome", StringComparison.Ordinal) == true);
        await session.RefreshAsync();

        Assert.True(session.IsSubmissionUnknown(order));
        Assert.Same(order, Assert.Single(session.Orders));
        Assert.Empty(source.GetExecutions(order).Executions);
        Assert.False(source.GetExecutions(order).CompleteCoverage);
        Assert.False(session.GetExecutions(order).CompleteCoverage);
        protocol.HideOrders = false;
        await session.RefreshAsync();
        Assert.False(session.IsSubmissionUnknown(order));
        Assert.True(source.GetExecutions(order).CompleteCoverage);
        Assert.Same(order, Assert.Single(session.Orders));

        var invalid = broker.Limit(OrderDirections.Buy, 0, 100);
        Assert.Equal(OrderStatus.Rejected, invalid.Status);
        Assert.Empty(source.GetExecutions(invalid).Executions);
        Assert.False(source.GetExecutions(invalid).CompleteCoverage);
    }

    [Fact]
    public async Task Rejected_status_with_reported_partial_execution_never_becomes_complete_empty()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, session, "Bybit fake");
        var source = (IAccountExecutionSource)broker;
        await session.RefreshAsync();
        var order = broker.Limit(OrderDirections.Buy, 1, 105);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        protocol.Fill("actual-before-rejection", .25m, 101, .01m, closedQuantity: 0);
        protocol.NetPosition = .25m;
        protocol.AveragePositionPrice = 101;
        var report = new JsonObject
        {
            ["symbol"] = "BTCUSDT", ["orderId"] = order.ExchangeOrderId, ["orderLinkId"] = ClientLink(protocol),
            ["side"] = "Buy", ["orderType"] = "Limit", ["price"] = "105", ["triggerPrice"] = "0", ["qty"] = "1",
            ["cumExecQty"] = "0.25", ["leavesQty"] = "0.75", ["avgPrice"] = "101", ["positionIdx"] = 0,
            ["orderStatus"] = "Rejected", ["reduceOnly"] = false,
            ["createdTime"] = FakeBybitProtocol.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["updatedTime"] = FakeBybitProtocol.Now.AddSeconds(1).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        };
        bool executionsVisible = false;
        protocol.ResponseFor = request => request.Path switch
        {
            "/v5/order/realtime" or "/v5/order/history" => Rows(report),
            "/v5/execution/list" when !executionsVisible => Rows(),
            _ => null,
        };
        await session.RefreshAsync();
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(.25m, order.Filled);
        Assert.Empty(source.GetExecutions(order).Executions);
        Assert.False(source.GetExecutions(order).CompleteCoverage);
        executionsVisible = true;
        await session.RefreshAsync();
        var known = source.GetExecutions(order);
        Assert.True(known.CompleteCoverage);
        Assert.Equal(.25m, Assert.Single(known.Executions).Quantity);
        Assert.Equal(101m, known.Executions[0].Price);
    }

    [Fact]
    public async Task Native_ids_isolate_orders_and_session_handles_while_recovered_tracked_orders_remain_readable()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, session, "Bybit fake");
        var source = (IAccountExecutionSource)broker;
        await session.RefreshAsync();
        var first = broker.Limit(OrderDirections.Buy, 1, 100);
        await Observe(session, () => first.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        var second = broker.Limit(OrderDirections.Sell, .5m, 110);
        await Observe(session, () => second.ExchangeOrderId.Length > 0);
        protocol.Fill("same-native-id", .2m, 101.5m, .01m, orderId: first.ExchangeOrderId);
        protocol.Fill("same-native-id", .3m, 109.5m, .03m, orderId: second.ExchangeOrderId);
        await session.RefreshAsync();

        var firstSnapshot = source.GetExecutions(first);
        var secondSnapshot = source.GetExecutions(second);
        Assert.True(firstSnapshot.CompleteCoverage);
        Assert.True(secondSnapshot.CompleteCoverage);
        Assert.Equal("bybit:demo-1234567:BTCUSDT:exchange-1:same-native-id", Assert.Single(firstSnapshot.Executions).Id);
        Assert.Equal("bybit:demo-1234567:BTCUSDT:exchange-2:same-native-id", Assert.Single(secondSnapshot.Executions).Id);
        Assert.Equal(.2m, firstSnapshot.Executions[0].Quantity);
        Assert.Equal(.3m, secondSnapshot.Executions[0].Quantity);
        protocol.CorrectFee("same-native-id", .04m, orderId: second.ExchangeOrderId);
        await session.RefreshAsync();
        Assert.Same(firstSnapshot, source.GetExecutions(first));
        Assert.Equal(.04m, Assert.Single(source.GetExecutions(second).Executions).AccountFee);
        Assert.Equal(.03m, secondSnapshot.Executions[0].AccountFee);

        var copiedHandle = new Order { ExchangeOrderId = first.ExchangeOrderId, Security = first.Security,
            QuantityToFill = first.QuantityToFill, Unfilled = first.Unfilled, State = first.State, Status = first.Status };
        Assert.Empty(source.GetExecutions(copiedHandle).Executions);
        Assert.False(source.GetExecutions(copiedHandle).CompleteCoverage);
        var eth = new Instrument { Symbol = "ETHUSDT", FeedId = BybitFeed.LinearId, TickSize = .01m, VolumeStep = .001m, QuoteAsset = "USDT" };
        using var ethData = new InstrumentData(eth, new SimulatedFeed());
        using var ethBroker = new BybitBroker(ethData, session, "Other symbol");
        Assert.Empty(ethBroker.GetExecutions(first).Executions);
        Assert.False(ethBroker.GetExecutions(first).CompleteCoverage);

        await using var recovered = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var recoveredBroker = new BybitBroker(data, recovered, "Recovered native orders");
        await recovered.RefreshAsync();
        var recoveredHandle = recovered.Orders.Single(order => order.ExchangeOrderId == first.ExchangeOrderId);
        Assert.NotSame(first, recoveredHandle);
        Assert.Empty(source.GetExecutions(recoveredHandle).Executions);
        Assert.False(source.GetExecutions(recoveredHandle).CompleteCoverage);
        Assert.Equal(firstSnapshot.Executions[0], Assert.Single(recoveredBroker.GetExecutions(recoveredHandle).Executions));
        Assert.True(recoveredBroker.GetExecutions(recoveredHandle).CompleteCoverage);
        Assert.Equal(session.AccountIdentity, recovered.AccountIdentity);
        Assert.NotEqual(session.ExecutionRevision, recovered.ExecutionRevision);
    }

    [Fact]
    public async Task A_late_fill_after_cancel_updates_the_same_handle_without_resurrecting_it_or_changing_native_time()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, session, "Bybit fake");
        await session.RefreshAsync();
        var order = broker.Limit(OrderDirections.Buy, 1, 100);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        broker.Cancel(order);
        await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.RefreshAsync();
        var canceled = broker.GetExecutions(order);
        Assert.True(canceled.CompleteCoverage);
        Assert.Empty(canceled.Executions);
        var nativeTime = FakeBybitProtocol.Now.AddSeconds(-15);
        protocol.Fill("late-after-cancel", .2m, 108.75m, .015m, preserveCanceled: true, executionAt: nativeTime);
        await session.RefreshAsync();
        await session.RefreshAsync();

        Assert.Same(order, Assert.Single(session.Orders));
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Equal(OrderStates.Done, order.State);
        Assert.Equal(.2m, order.Filled);
        var late = broker.GetExecutions(order);
        Assert.True(late.CompleteCoverage);
        var fact = Assert.Single(late.Executions);
        Assert.Equal(.2m, fact.Quantity);
        Assert.Equal(108.75m, fact.Price);
        Assert.Equal(nativeTime.UtcDateTime, fact.TimeUtc);
        Assert.Equal(.015m, fact.AccountFee);
        Assert.Empty(canceled.Executions);
    }

    [Fact]
    public async Task Durable_facts_restore_with_stable_UID_identity_across_sessions_and_credential_rotation_but_not_other_accounts()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-facts-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "orders.json");
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        try
        {
            AccountExecution original;
            string? stableOrder;
            long originalRevision;
            string? persistentAccount;
            await using (var first = new BybitTradingSession(protocol.Settings, state, http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now)))
            {
                using var data = new InstrumentData(Btc, new SimulatedFeed());
                using var broker = new BybitBroker(data, first, "Original");
                Assert.Null(first.PersistentAccountIdentity);
                Assert.Null(((IAccountExecutionSource)broker).PersistentAccountIdentity);
                long emptyRevision = first.ExecutionRevision;
                await first.RefreshAsync();
                persistentAccount = ((IAccountExecutionSource)broker).PersistentAccountIdentity;
                Assert.Equal("bybit:demo-1234567:http://127.0.0.1:1", persistentAccount);
                Assert.Equal(persistentAccount, first.PersistentAccountIdentity);
                Assert.Equal(emptyRevision, first.ExecutionRevision);
                var order = broker.Limit(OrderDirections.Buy, 1, 100);
                await Observe(first, () => order.ExchangeOrderId.Length > 0);
                protocol.Fill("restart-native", .4m, 103.25m, .021m, executionAt: FakeBybitProtocol.Now.AddSeconds(-30));
                await first.RefreshAsync();
                original = Assert.Single(broker.GetExecutions(order).Executions);
                Assert.True(broker.GetExecutions(order).CompleteCoverage);
                stableOrder = broker.StableOrderIdentityOf(order);
                originalRevision = first.ExecutionRevision;
            }

            await using var restored = new BybitTradingSession(protocol.Settings, state, http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var restoredData = new InstrumentData(Btc, new SimulatedFeed());
            using var restoredBroker = new BybitBroker(restoredData, restored, "Restored");
            var restoredHandle = Assert.Single(restored.Orders);
            Assert.Equal("demo-1234567", restored.AccountIdentity);
            Assert.Null(restored.PersistentAccountIdentity);
            Assert.Null(((IAccountExecutionSource)restoredBroker).PersistentAccountIdentity);
            var pending = restoredBroker.GetExecutions(restoredHandle);
            Assert.Equal(original, Assert.Single(pending.Executions));
            Assert.False(pending.CompleteCoverage);
            Assert.NotEqual(originalRevision, restored.ExecutionRevision);
            await restored.RefreshAsync();
            Assert.Equal(persistentAccount, ((IAccountExecutionSource)restoredBroker).PersistentAccountIdentity);
            Assert.True(restoredBroker.GetExecutions(restoredHandle).CompleteCoverage);
            Assert.Equal(original, Assert.Single(restoredBroker.GetExecutions(restoredHandle).Executions));
            Assert.Equal(stableOrder, restoredBroker.StableOrderIdentityOf(restoredHandle));

            var rotated = protocol.Settings with { ApiKey = "rotated-fake-key", ApiSecret = "rotated-fake-secret" };
            await using var rotatedSession = new BybitTradingSession(rotated, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var rotatedBroker = new BybitBroker(restoredData, rotatedSession, "Rotated credentials");
            Assert.Null(rotatedSession.PersistentAccountIdentity);
            await rotatedSession.RefreshAsync();
            var rotatedHandle = Assert.Single(rotatedSession.Orders);
            Assert.Equal(original, Assert.Single(rotatedBroker.GetExecutions(rotatedHandle).Executions));
            Assert.Equal(restored.AccountIdentity, rotatedSession.AccountIdentity);
            Assert.NotEqual(restored.ExecutionRevision, rotatedSession.ExecutionRevision);
            Assert.NotEqual(restored.ContextIdentity, rotatedSession.ContextIdentity);
            Assert.Equal(persistentAccount, ((IAccountExecutionSource)rotatedBroker).PersistentAccountIdentity);

            protocol.ResponseFor = request => request.Path == "/v5/user/query-api"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"retCode":0,"result":{"userID":7654321,"readOnly":0}}""") } : null;
            await using var differentAccount = new BybitTradingSession(rotated, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var differentBroker = new BybitBroker(restoredData, differentAccount, "Other UID");
            await differentAccount.RefreshAsync();
            var differentFact = Assert.Single(differentBroker.GetExecutions(Assert.Single(differentAccount.Orders)).Executions);
            Assert.Equal("bybit:demo-7654321:BTCUSDT:exchange-1:restart-native", differentFact.Id);
            Assert.NotEqual(original.Id, differentFact.Id);
            Assert.Equal("bybit:demo-7654321:http://127.0.0.1:1", differentAccount.PersistentAccountIdentity);
            Assert.NotEqual(persistentAccount, ((IAccountExecutionSource)differentBroker).PersistentAccountIdentity);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flatten_waits_for_authoritative_cancel_and_fresh_position_then_sends_one_native_reducing_close(bool ambiguousClose)
    {
        using var protocol = new FakeBybitProtocol { NetPosition = 1, ApplyCancelImmediately = false };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings with { Environment = BybitEnvironment.Real }, httpClient: http,
            sendGuard: (_, _, _, _) => "Real-account exposure permission revoked.", clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime, 100, 1, TradeSide.Buy));
        using var broker = new BybitBroker(data, session, "Bybit fake");
        session.FillReceived += _ => { };
        await session.RefreshAsync();
        var protection = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1, 90);
        await Observe(session, () => protection.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ResponseForAsync = async (request, ct) =>
        {
            if (request.Path != "/v5/order/cancel") return null;
            cancelEntered.TrySetResult();
            await cancelRelease.Task.WaitAsync(ct);
            return null;
        };
        try
        {
            broker.Flatten();
            await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            protocol.Fill("protective-race", .4m, 89.75m, .02m, orderId: protection.ExchangeOrderId, closedQuantity: .4m);
            protocol.NetPosition = .6m;
            await session.RefreshAsync();
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
            Assert.Equal(OrderStates.Active, protection.State);
            Assert.True(session.HasOpenPositionsOrOrders);
            protocol.ConfirmCancel();
            protocol.FailPositions = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => session.RefreshAsync());
            protocol.UnknownCreate = ambiguousClose;
            cancelRelease.TrySetResult();
            await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
            Assert.False(session.PositionReady);
            protocol.FailPositions = false;
            await session.RefreshAsync();
            await Observe(session, () => session.Orders.Count == 2);
            var close = session.Orders.Single(order => !ReferenceEquals(order, protection));
            await Observe(session, () => close.ExchangeOrderId.Length > 0 ||
                session.LastError?.Contains("placement outcome", StringComparison.Ordinal) == true);
            protocol.HideOrders = ambiguousClose;
            broker.Flatten();
            await session.RefreshAsync();
            await session.RefreshAsync();

            var creates = protocol.Requests.Where(request => request.Path == "/v5/order/create").ToArray();
            Assert.Equal(2, creates.Length);
            var nativeClose = JsonNode.Parse(creates[1].Body)!;
            Assert.Equal("Market", nativeClose["orderType"]!.GetValue<string>());
            Assert.Equal("Sell", nativeClose["side"]!.GetValue<string>());
            Assert.Equal("0.6", nativeClose["qty"]!.GetValue<string>());
            Assert.True(nativeClose["reduceOnly"]!.GetValue<bool>());
            Assert.Equal(OrderStatus.Canceled, protection.Status);
            Assert.Equal(.6m, close.QuantityToFill);
            Assert.True(close.ReduceOnly);
            Assert.True(session.HasOpenPositionsOrOrders);
            if (ambiguousClose)
            {
                Assert.True(session.IsSubmissionUnknown(close));
                Assert.False(broker.GetExecutions(close).CompleteCoverage);
                Assert.Empty(broker.GetExecutions(close).Executions);
            }
            else
            {
                protocol.Fill("flatten-complete", .6m, 100.25m, .03m, orderId: close.ExchangeOrderId, closedQuantity: .6m);
                protocol.NetPosition = 0;
                await session.RefreshAsync();
                using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (session.HasOpenPositionsOrOrders)
                {
                    await Task.Delay(1, drainTimeout.Token);
                    await session.RefreshAsync(drainTimeout.Token);
                }
                Assert.Equal(OrderStatus.Filled, close.Status);
                Assert.True(broker.GetExecutions(close).CompleteCoverage);
                Assert.Equal(.6m, Assert.Single(broker.GetExecutions(close).Executions).Quantity);
                Assert.True(session.TryBeginIdleShutdown(out var drained));
                await drained;
            }
        }
        finally { cancelRelease.TrySetResult(); }
    }

    [Fact]
    public async Task Flatten_intent_survives_restart_after_protection_cancel_and_never_repeats_an_ambiguous_close()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-flatten-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "orders.json");
        using var protocol = new FakeBybitProtocol { NetPosition = 1, ApplyCancelImmediately = false };
        using var http = new HttpClient(protocol);
        try
        {
            string protectionId;
            await using (var first = new BybitTradingSession(protocol.Settings, state, http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now)))
            {
                using var data = new InstrumentData(Btc, new SimulatedFeed());
                using var broker = new BybitBroker(data, first, "Original");
                await first.RefreshAsync();
                var protection = broker.Exit(OrderTypes.Limit, OrderDirections.Sell, 1, 110);
                await Observe(first, () => protection.ExchangeOrderId.Length > 0);
                protectionId = protection.ExchangeOrderId;
                await first.RefreshAsync();
                broker.Flatten();
                await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
                Assert.True(first.HasOpenPositionsOrOrders);
            }
            Assert.True(BybitTradingSession.RequiresRecovery(state));
            protocol.Fill("before-cancel-complete", .4m, 109.5m, .02m, orderId: protectionId, closedQuantity: .4m);
            protocol.ConfirmCancel();
            protocol.NetPosition = .6m;
            protocol.UnknownCreate = true;
            string? closeIdentity;
            await using (var restored = new BybitTradingSession(protocol.Settings, state, http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now)))
            {
                await restored.RefreshAsync();
                await Observe(restored, () => restored.Orders.Count == 2);
                var close = restored.Orders.Single(order => order.ExchangeOrderId != protectionId);
                await Observe(restored, () => restored.LastError?.Contains("placement outcome", StringComparison.Ordinal) == true);
                Assert.True(restored.IsSubmissionUnknown(close));
                closeIdentity = restored.StableOrderIdentityOf(close);
                protocol.HideOrders = true;
            }

            await using var recovered = new BybitTradingSession(protocol.Settings, state, http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            var retained = recovered.Orders.Single(order => order.ExchangeOrderId != protectionId);
            Assert.Equal(closeIdentity, recovered.StableOrderIdentityOf(retained));
            await recovered.RefreshAsync();
            await recovered.RefreshAsync();
            Assert.True(recovered.IsSubmissionUnknown(retained));
            Assert.True(recovered.HasOpenPositionsOrOrders);
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
            protocol.HideOrders = false;
            await recovered.RefreshAsync();
            Assert.Same(retained, recovered.Orders.Single(order => order.ExchangeOrderId == "exchange-2"));
            Assert.False(recovered.IsSubmissionUnknown(retained));
            Assert.True(retained.ReduceOnly);
            Assert.Equal(.6m, retained.QuantityToFill);
            Assert.Equal(2, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(OrderTypes.Limit)]
    [InlineData(OrderTypes.Stop)]
    public async Task Ordinary_opposite_orders_cannot_become_opening_exposure_after_permission_is_revoked_during_metadata(OrderTypes type)
    {
        using var protocol = new FakeBybitProtocol { NetPosition = 1 };
        using var http = new HttpClient(protocol);
        bool allowed = true;
        await using var session = new BybitTradingSession(protocol.Settings with { Environment = BybitEnvironment.Real }, httpClient: http,
            sendGuard: (_, _, _, _) => allowed ? null : "Real-account exposure permission revoked.",
            clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime, 100, 1, TradeSide.Buy));
        using var broker = new BybitBroker(data, session, "Bybit fake");
        await session.RefreshAsync();
        var protection = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1, 90);
        await Observe(session, () => protection.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        var metadataEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ResponseForAsync = async (request, ct) =>
        {
            if (request.Path != "/v5/market/instruments-info") return null;
            metadataEntered.TrySetResult();
            await metadataRelease.Task.WaitAsync(ct);
            return null;
        };
        try
        {
            var ordinary = type == OrderTypes.Limit
                ? broker.Limit(OrderDirections.Sell, 1, 105) : broker.Stop(OrderDirections.Sell, 1, 95);
            await metadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            protocol.Fill("protective-close-before-send", 1, 89.75m, .04m, orderId: protection.ExchangeOrderId, closedQuantity: 1);
            protocol.NetPosition = 0;
            allowed = false;
            await session.RefreshAsync();
            metadataRelease.TrySetResult();
            await Observe(session, () => ordinary.Status == OrderStatus.Rejected);
            Assert.Equal(OrderStates.Failed, ordinary.State);
            Assert.Equal(OrderStatus.Rejected, ordinary.Status);
            Assert.Equal(string.Empty, ordinary.ExchangeOrderId);
            Assert.False(session.IsSubmissionUnknown(ordinary));

            Assert.Equal(0m, broker.Position.Volume);
            Assert.False(ordinary.ReduceOnly);
            Assert.Equal(OrderStatus.Filled, protection.Status);
            var create = JsonNode.Parse(Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create").Body)!;
            Assert.True(create["reduceOnly"]!.GetValue<bool>());
            Assert.Empty(broker.GetExecutions(ordinary).Executions);
        }
        finally { metadataRelease.TrySetResult(); }
    }

    [Fact]
    public async Task Stale_closed_PnL_quantity_remains_unknown_until_the_report_covers_all_confirmed_closing_executions()
    {
        using var protocol = new FakeBybitProtocol { NetPosition = .2m };
        decimal reportedSize = .5m;
        decimal reportedPnl = 5;
        var first = Execution("close-a", "closing-order", "", "BTCUSDT", .5m, 110, FakeBybitProtocol.Now.ToUnixTimeMilliseconds(), .025m, "USDT");
        var second = Execution("close-b", "closing-order", "", "BTCUSDT", .5m, 112, FakeBybitProtocol.Now.ToUnixTimeMilliseconds(), .026m, "USDT");
        first["closedSize"] = "0.5";
        second["closedSize"] = "0.5";
        protocol.ResponseFor = request => request.Path switch
        {
            "/v5/execution/list" => Rows(first, second),
            "/v5/position/closed-pnl" => Rows(new JsonObject
            {
                ["symbol"] = "BTCUSDT", ["orderId"] = "closing-order", ["closedPnl"] = reportedPnl.ToString(CultureInfo.InvariantCulture),
                ["closedSize"] = reportedSize.ToString(CultureInfo.InvariantCulture),
                ["createdTime"] = FakeBybitProtocol.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["updatedTime"] = FakeBybitProtocol.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            }),
            _ => null,
        };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();

        var stale = BybitAccountMetrics.From(session, TimeZoneInfo.Utc, FakeBybitProtocol.Now.UtcDateTime);
        Assert.True(session.ClosedPnlReady);
        Assert.Null(stale.ReportedRealizedToday);
        Assert.Equal(3m, stale.OpenPnl);
        Assert.Equal(1000m, session.Wallet!.Equity);
        Assert.Equal(997m, session.Wallet.WalletBalance);
        reportedSize = 1;
        reportedPnl = 12;
        await session.RefreshAsync();
        var complete = BybitAccountMetrics.From(session, TimeZoneInfo.Utc, FakeBybitProtocol.Now.UtcDateTime);
        Assert.Equal(12m, complete.ReportedRealizedToday);
        Assert.Equal(3m, complete.OpenPnl);
        Assert.False(complete.HasCompleteOpeningBasis);
        Assert.Equal(1000m, session.Wallet!.Equity);
        Assert.Equal(997m, session.Wallet.WalletBalance);
    }

    [Fact]
    public async Task Execution_revision_changes_for_late_closed_quantity_metadata_but_not_identical_reconciliation()
    {
        using var protocol = new FakeBybitProtocol();
        var fill = Execution("late-close-size", "native-order", "", "BTCUSDT", .25m, 101, FakeBybitProtocol.Now.ToUnixTimeMilliseconds(), .01m, "USDT");
        protocol.ResponseFor = request => request.Path == "/v5/execution/list" ? Rows(fill) : null;
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        long empty = session.ExecutionRevision;
        await session.RefreshAsync();
        long actual = session.ExecutionRevision;
        Assert.True(actual > empty);
        var before = session.GetExecutionState();
        Assert.Equal(actual, before.Revision);
        Assert.Same(session.Executions, before.Executions);
        await session.RefreshAsync();
        Assert.Equal(actual, session.ExecutionRevision);
        Assert.Same(before.Executions, session.GetExecutionState().Executions);
        fill["closedSize"] = "0.25";
        await session.RefreshAsync();
        Assert.True(session.ExecutionRevision > actual);
        Assert.Equal(.25m, Assert.Single(session.Executions).ClosedQuantity);
        Assert.Equal(.01m, session.Executions[0].Fee);
        var corrected = session.GetExecutionState();
        Assert.Equal(session.ExecutionRevision, corrected.Revision);
        Assert.NotSame(before.Executions, corrected.Executions);
        Assert.Null(Assert.Single(before.Executions).ClosedQuantity);
        Assert.Equal(.25m, Assert.Single(corrected.Executions).ClosedQuantity);
        Assert.Throws<NotSupportedException>(() => ((IList<BybitExecution>)corrected.Executions)[0] = before.Executions[0]);
        actual = session.ExecutionRevision;
        await session.RefreshAsync();
        Assert.Equal(actual, session.ExecutionRevision);
    }

    [Fact]
    public async Task A_disposed_broker_view_cannot_queue_a_new_flatten_on_its_still_live_session()
    {
        using var protocol = new FakeBybitProtocol { NetPosition = 1 };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, session, "Closed view");
        await session.RefreshAsync();
        broker.Dispose();
        broker.Flatten();
        await session.RefreshAsync();

        Assert.DoesNotContain(protocol.Requests, request => request.Path is "/v5/order/create" or "/v5/order/cancel");
        Assert.Empty(session.Orders);
        Assert.Equal(1m, session.PositionFor(Btc.Symbol)!.Size);
        Assert.Equal("This Bybit account view is closed; flatten was not queued.", broker.LastError);
    }

    [Theory]
    [InlineData("HTTPS://API-DEMO.BYBIT.COM:443/", "https://api-demo.bybit.com", "bybit:demo-1234567:https://api-demo.bybit.com", true)]
    [InlineData("https://api-demo.bybit.com", "https://api.bybit.com", "bybit:demo-1234567:https://api-demo.bybit.com", false)]
    [InlineData("http://127.0.0.1:1", "http://127.0.0.1:2", "bybit:demo-1234567:http://127.0.0.1:1", false)]
    [InlineData("http://127.0.0.1:80/", "HTTP://127.0.0.1", "bybit:demo-1234567:http://127.0.0.1", true)]
    public async Task Verified_persistent_account_scope_normalizes_origin_but_never_uses_display_name_or_credentials(
        string firstOrigin, string secondOrigin, string expectedFirst, bool sameOrigin)
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        var settings = protocol.Settings with { BaseUrl = firstOrigin };
        await using var first = new BybitTradingSession(settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        var rotated = settings with { BaseUrl = secondOrigin, ApiKey = "rotated-fake-key", ApiSecret = "rotated-fake-secret" };
        await using var second = new BybitTradingSession(rotated, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var firstBroker = new BybitBroker(data, first, "Identical display name");
        using var secondBroker = new BybitBroker(data, second, "Identical display name");
        var firstSource = (IAccountExecutionSource)firstBroker;
        var secondSource = (IAccountExecutionSource)secondBroker;
        Assert.Null(firstSource.PersistentAccountIdentity);
        Assert.Null(secondSource.PersistentAccountIdentity);
        await first.RefreshAsync();
        await second.RefreshAsync();

        Assert.Equal(expectedFirst, firstSource.PersistentAccountIdentity);
        Assert.NotNull(secondSource.PersistentAccountIdentity);
        Assert.NotEqual(first.ContextIdentity, second.ContextIdentity);
        Assert.Equal(sameOrigin, firstSource.PersistentAccountIdentity == secondSource.PersistentAccountIdentity);
        Assert.Equal("Identical display name", firstBroker.Name);
        Assert.Equal(firstBroker.Name, secondBroker.Name);
        protocol.FailPositions = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => first.RefreshAsync());
        Assert.False(first.SnapshotReady);
        Assert.Equal(expectedFirst, firstSource.PersistentAccountIdentity);
    }

    [Fact]
    public async Task Verified_persistent_account_scope_distinguishes_demo_and_real_even_at_the_same_origin_and_UID()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var demo = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        await using var real = new BybitTradingSession(protocol.Settings with { Environment = BybitEnvironment.Real },
            httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        await demo.RefreshAsync();
        await real.RefreshAsync();

        Assert.Equal("bybit:demo-1234567:http://127.0.0.1:1", demo.PersistentAccountIdentity);
        Assert.Equal("bybit:real-1234567:http://127.0.0.1:1", real.PersistentAccountIdentity);
        Assert.NotEqual(demo.PersistentAccountIdentity, real.PersistentAccountIdentity);
    }

    [Fact]
    public async Task A_saved_UID_is_not_current_identity_proof_and_a_mismatch_cannot_authorize_a_reducing_order()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-identity-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "orders.json");
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        try
        {
            await using (var first = new BybitTradingSession(protocol.Settings, state, http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now)))
                await first.RefreshAsync();
            protocol.ResponseFor = request => request.Path == "/v5/user/query-api"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"retCode":0,"result":{"userID":7654321,"readOnly":0}}""") } : null;
            await using var restored = new BybitTradingSession(protocol.Settings, state, http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            using var broker = new BybitBroker(data, restored, "Unverified restored view");
            var source = (IAccountExecutionSource)broker;
            Assert.Equal("demo-1234567", restored.AccountIdentity);
            Assert.Null(source.PersistentAccountIdentity);
            var beforeProof = broker.Exit(OrderTypes.Limit, OrderDirections.Sell, 1, 110);
            Assert.Equal(OrderStatus.Rejected, beforeProof.Status);
            await Assert.ThrowsAsync<InvalidDataException>(() => restored.RefreshAsync());
            Assert.Null(source.PersistentAccountIdentity);
            Assert.Equal("demo-1234567", restored.AccountIdentity);
            Assert.False(restored.PositionReady);
            Assert.False(restored.OrdersReady);
            var afterMismatch = broker.Exit(OrderTypes.Limit, OrderDirections.Sell, 1, 110);
            Assert.Equal(OrderStatus.Rejected, afterMismatch.Status);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_later_UID_mismatch_invalidates_identity_and_blocks_held_reducing_mutations_but_not_captured_cancels(bool amend)
    {
        using var protocol = new FakeBybitProtocol { NetPosition = 1 };
        long uid = 1234567;
        protocol.ResponseFor = request => request.Path == "/v5/user/query-api"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$$"""{"retCode":0,"result":{"userID":{{{uid}}},"readOnly":0}}""") } : null;
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, session, "Pinned account view");
        var source = (IAccountExecutionSource)broker;
        await session.RefreshAsync();
        string? verified = source.PersistentAccountIdentity;
        Assert.NotNull(verified);
        var protection = broker.Exit(OrderTypes.Limit, OrderDirections.Sell, 1, 110);
        await Observe(session, () => protection.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        var metadataEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ResponseForAsync = async (request, ct) =>
        {
            if (request.Path != "/v5/market/instruments-info") return null;
            metadataEntered.TrySetResult();
            await metadataRelease.Task.WaitAsync(ct);
            return null;
        };
        try
        {
            var mutation = amend ? broker.Replace(protection, 111, 1) : broker.Exit(OrderTypes.Limit, OrderDirections.Sell, .5m, 111);
            await metadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            uid = 7654321;
            await Assert.ThrowsAsync<InvalidDataException>(() => session.RefreshAsync());
            Assert.Null(source.PersistentAccountIdentity);
            Assert.Equal("demo-1234567", session.AccountIdentity);
            Assert.False(session.PositionReady);
            Assert.False(session.OrdersReady);
            metadataRelease.TrySetResult();
            await Observe(session, () => amend ? !session.IsReplacementPending(protection) : mutation.Status == OrderStatus.Rejected);

            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/amend");
            Assert.Equal(110m, protection.Price);
            Assert.Null(source.PersistentAccountIdentity);
            Assert.False(source.GetExecutions(protection).CompleteCoverage);
            broker.Cancel(protection);
            await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancel = JsonNode.Parse(Assert.Single(protocol.Requests, request => request.Path == "/v5/order/cancel").Body)!;
            Assert.Equal(protection.ExchangeOrderId, cancel["orderId"]!.GetValue<string>());
            Assert.Equal(OrderStates.Active, protection.State);
            uid = 1234567;
            await Assert.ThrowsAsync<InvalidDataException>(() => session.RefreshAsync());
            Assert.Null(source.PersistentAccountIdentity);
        }
        finally { metadataRelease.TrySetResult(); }
    }

    private static async Task Observe(BybitTradingSession session, Func<bool> condition)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (condition()) completion.TrySetResult(); }
        session.Changed += Changed;
        try { Changed(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { session.Changed -= Changed; }
    }

    private static string ClientLink(FakeBybitProtocol protocol) => JsonNode.Parse(
        Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create").Body)!["orderLinkId"]!.GetValue<string>();

    private static JsonObject Execution(string id, string orderId, string linkId, string symbol, decimal quantity, decimal price,
        long time, decimal? fee, string currency) => new()
    {
        ["execId"] = id, ["orderId"] = orderId, ["orderLinkId"] = linkId, ["symbol"] = symbol, ["side"] = "Buy",
        ["execType"] = "Trade", ["execQty"] = quantity.ToString(CultureInfo.InvariantCulture),
        ["execPrice"] = price.ToString(CultureInfo.InvariantCulture), ["execTime"] = time.ToString(CultureInfo.InvariantCulture),
        ["execFee"] = fee?.ToString(CultureInfo.InvariantCulture), ["feeCurrency"] = currency,
    };

    private static HttpResponseMessage Rows(params JsonObject[] rows) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject
        {
            ["retCode"] = 0,
            ["result"] = new JsonObject { ["list"] = new JsonArray(rows.Select(row => (JsonNode)row.DeepClone()).ToArray()), ["nextPageCursor"] = "" },
        }.ToJsonString(), Encoding.UTF8, "application/json"),
    };
}
