using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed class BybitProviderTests : IDisposable
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-provider-state-" + Guid.NewGuid().ToString("N"));
    private string NewStatePath() => Path.Combine(_stateRoot, Guid.NewGuid().ToString("N"), "orders.json");
    public void Dispose()
    {
        if (Directory.Exists(_stateRoot)) Directory.Delete(_stateRoot, recursive: true);
    }
    private static async Task Observe(BybitTradingSession session, Func<bool> condition)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (condition()) completion.TrySetResult(); }
        session.Changed += Changed;
        try { Changed(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { session.Changed -= Changed; }
    }

    [Fact]
    public async Task Signed_protocol_uses_server_clock_exact_payload_and_selected_environment()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        using var client = new BybitAccountClient(protocol.Settings with { Environment = BybitEnvironment.Testnet }, http, new FixedClock(FakeBybitProtocol.Now.AddSeconds(-90)));
        var wallet = await client.GetWalletAsync(default);
        Assert.Equal(1000m, wallet!.Equity);
        var request = Assert.Single(protocol.Requests, r => r.Path == "/v5/account/wallet-balance");
        Assert.Equal("accountType=UNIFIED", request.Query);
        Assert.Equal("1793786400000", request.Headers["X-BAPI-TIMESTAMP"]);
        Assert.Equal("10000", request.Headers["X-BAPI-RECV-WINDOW"]);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("fake-secret"));
        string expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("1793786400000fake-key10000accountType=UNIFIED"))).ToLowerInvariant();
        Assert.Equal(expected, request.Headers["X-BAPI-SIGN"]);
        Assert.Equal("https://api-demo.bybit.com", new BybitAccountSettings().RestBase);
        Assert.Equal("https://api-testnet.bybit.com", new BybitAccountSettings { Environment = BybitEnvironment.Testnet }.RestBase);
        Assert.Equal("https://api.bybit.com", new BybitAccountSettings { Environment = BybitEnvironment.Real }.RestBase);
    }

    [Fact]
    public async Task Native_session_without_durable_roots_cannot_send_opening_exposure_through_an_injected_loopback_transport()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, httpClient: http,
            clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        Assert.True(session.PositionReady);
        Assert.True(session.OrdersReady);
        var rejected = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        Assert.Equal(OrderStates.Failed, rejected.State);
        Assert.Equal(OrderStatus.Rejected, rejected.Status);
        Assert.Empty(session.Orders);
        Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");
    }

    [Fact]
    public async Task Held_create_ack_and_cancel_keep_one_handle_until_authoritative_terminal_report()
    {
        using var protocol = new FakeBybitProtocol { HoldCreate = true, ApplyCancelImmediately = false };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var order = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await protocol.CreateReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Cancel(order);
        Assert.Same(order, Assert.Single(session.OrdersFor(Btc.Symbol)));
        Assert.Equal(OrderStatus.None, order.Status);
        Assert.False(session.OrdersReady);
        await session.RefreshAsync();
        Assert.Same(order, Assert.Single(session.Orders));
        Assert.DoesNotContain(protocol.Requests, r => r.Path == "/v5/order/cancel");
        protocol.CreateRelease.TrySetResult();
        await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Observe(session, () => !session.IsSubmissionUnknown(order));
        Assert.Equal(OrderStates.Active, order.State); // REST cancel acceptance is not cancellation.
        await session.RefreshAsync();
        Assert.Equal(OrderStates.Active, order.State);
        protocol.ConfirmCancel();
        await session.RefreshAsync();
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Single(protocol.Requests, r => r.Path == "/v5/order/create");
    }

    [Fact]
    public async Task Ambiguous_create_and_missing_final_lookup_never_invent_cancellation_or_resubmit()
    {
        using var protocol = new FakeBybitProtocol { UnknownCreate = true, HideOrders = true };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var order = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await Observe(session, () => session.LastError?.Contains("placement outcome", StringComparison.Ordinal) == true);
        for (int i = 0; i < 5; i++) await session.RefreshAsync();
        Assert.Equal(OrderStates.Active, order.State);
        Assert.True(session.IsSubmissionUnknown(order));
        Assert.False(session.OrdersReady);
        Assert.Single(protocol.Requests, r => r.Path == "/v5/order/create");
        session.Cancel(order);
        protocol.HideOrders = false;
        await session.RefreshAsync();
        await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.RefreshAsync();
        Assert.Same(order, Assert.Single(session.Orders));
        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public async Task Native_amendment_needs_matching_price_and_total_quantity_not_command_acceptance()
    {
        using var protocol = new FakeBybitProtocol { ApplyAmendImmediately = false, NetPosition = 1 };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var order = session.Place(Btc, 100, OrderDirections.Sell, OrderTypes.Stop, 1, 90, reduceOnly: true);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        session.Replace(order, 92, .8m, Btc);
        await protocol.AmendReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.RefreshAsync();
        Assert.True(session.IsReplacementPending(order));
        Assert.Equal(90m, order.TriggerPrice);
        Assert.Equal(1m, order.QuantityToFill);
        protocol.ConfirmAmend();
        await session.RefreshAsync();
        Assert.False(session.IsReplacementPending(order));
        Assert.Same(order, Assert.Single(session.Orders));
        Assert.Equal(92m, order.TriggerPrice);
        Assert.Equal(.8m, order.Unfilled);
        var create = JsonNode.Parse(Assert.Single(protocol.Requests, r => r.Path == "/v5/order/create").Body)!;
        Assert.True(create["reduceOnly"]!.GetValue<bool>());
        Assert.Null(create["closeOnTrigger"]); // That flag can affect other same-contract orders on margin shortage.
        Assert.Equal(2, create["triggerDirection"]!.GetValue<int>());
        Assert.Single(protocol.Requests, r => r.Path == "/v5/order/create");
        Assert.DoesNotContain(protocol.Requests, r => r.Path == "/v5/order/cancel");
    }

    [Fact]
    public async Task Partial_and_late_fills_keep_actual_price_fee_and_execution_identity_once()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        var delivered = new List<BybitExecution>();
        session.FillReceived += delivered.Add;
        await session.RefreshAsync();
        var order = session.Place(Btc, 999, OrderDirections.Buy, OrderTypes.Limit, 1, 100);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        protocol.Fill("fill-a", .3m, 101, .015m);
        await session.RefreshAsync();
        Assert.Equal(.3m, order.Filled);
        Assert.Equal(101m, Assert.Single(delivered).Price);
        Assert.Equal(.015m, delivered[0].Fee);
        Assert.Equal("USDT", delivered[0].FeeCurrency);
        session.Cancel(order);
        await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.RefreshAsync();
        Assert.Equal(OrderStatus.Canceled, order.Status);
        protocol.Fill("fill-b", .2m, 102, .010m, preserveCanceled: true);
        await session.RefreshAsync();
        await session.RefreshAsync();
        Assert.Equal(.5m, order.Filled);
        Assert.Equal(.5m, order.Unfilled);
        Assert.Equal(101.4m, session.AverageFillFor(order));
        Assert.Equal(2, delivered.Count);
        Assert.Equal(.025m, delivered.Sum(f => f.Fee!.Value));
        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public async Task Terminal_identity_review_recovers_a_late_execution_outside_the_discovery_result_without_resurrecting_the_order()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        var delivered = new List<BybitExecution>();
        session.FillReceived += delivered.Add;
        await session.RefreshAsync();
        var order = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        Assert.True(session.IsSubmissionUnknown(order));
        session.Cancel(order);
        Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/cancel");
        await session.RefreshAsync();
        await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.RefreshAsync();
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Equal(0m, order.Filled);
        protocol.Fill("late-after-terminal", .25m, 102, .013m, preserveCanceled: true);
        protocol.ResponseFor = request => request.Path == "/v5/execution/list" && !request.Query.Contains("orderId=", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"retCode":0,"result":{"list":[],"nextPageCursor":""}}""") } : null;
        await session.RefreshAsync();
        await session.RefreshAsync();
        Assert.True(session.ExecutionsReady);
        Assert.Equal(OrderStates.Done, order.State);
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Equal(.25m, order.Filled);
        Assert.Equal(102m, Assert.Single(delivered).Price);
        Assert.Equal(.013m, delivered[0].Fee);
        Assert.Contains(protocol.Requests, request => request.Path == "/v5/execution/list" && request.Query.Contains("orderId=exchange-1", StringComparison.Ordinal));
        Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
    }

    [Fact]
    public async Task A_second_exchange_identity_cannot_silently_replace_the_original_owned_client_handle()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var order = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        string link = JsonNode.Parse(Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create").Body)!["orderLinkId"]!.GetValue<string>();
        protocol.ResponseFor = request => request.Path != "/v5/order/realtime" ? null : new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent($$$"""{"retCode":0,"result":{"list":[{"orderId":"unexpected-child-id","orderLinkId":"{{{link}}}","symbol":"BTCUSDT","side":"Buy","orderType":"Limit","price":"99","triggerPrice":"0","qty":"1","cumExecQty":"0","leavesQty":"1","orderStatus":"New","reduceOnly":false,"positionIdx":0,"avgPrice":"0","createdTime":"1793786400000","updatedTime":"1793786400001"}],"nextPageCursor":""}}""") };
        await Assert.ThrowsAsync<InvalidDataException>(() => session.RefreshAsync());
        Assert.Same(order, Assert.Single(session.Orders));
        Assert.Equal("exchange-1", order.ExchangeOrderId);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.False(session.OrdersReady);
        Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
    }

    [Fact]
    public async Task Failed_snapshot_keeps_exposure_unknown_instead_of_fabricating_flat()
    {
        using var protocol = new FakeBybitProtocol { NetPosition = .75m };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, session, "Bybit fake");
        await session.RefreshAsync();
        Assert.True(broker.PositionReady);
        Assert.Equal(.75m, broker.Position.Volume);
        protocol.FailPositions = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => session.RefreshAsync());
        Assert.False(broker.PositionReady);
        Assert.False(broker.OrdersReady);
        Assert.Equal(.75m, broker.Position.Volume);
        var blocked = broker.Market(OrderDirections.Buy, .1m);
        Assert.Equal(OrderStates.Failed, blocked.State);
        Assert.DoesNotContain(protocol.Requests, r => r.Path == "/v5/order/create");
        protocol.FailPositions = false;
        protocol.NetPosition = 0;
        await session.RefreshAsync();
        Assert.True(broker.PositionReady);
        Assert.Equal(0, broker.Position.Volume);
    }

    [Fact]
    public async Task Restart_restores_unknown_client_identity_without_a_second_create_or_plaintext_credentials()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "orders.json");
        using var protocol = new FakeBybitProtocol { UnknownCreate = true, HideOrders = true };
        using var http = new HttpClient(protocol);
        try
        {
            await using (var first = new BybitTradingSession(protocol.Settings, state, http, clock: new FixedClock(FakeBybitProtocol.Now)))
            {
                await first.RefreshAsync();
                first.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
                await Observe(first, () => first.LastError?.Contains("placement outcome", StringComparison.Ordinal) == true);
            }
            string saved = File.ReadAllText(state);
            Assert.DoesNotContain("fake-secret", saved);
            Assert.DoesNotContain("fake-key", saved);
            protocol.HideOrders = false;
            await using var restored = new BybitTradingSession(protocol.Settings, state, http, clock: new FixedClock(FakeBybitProtocol.Now));
            Assert.False(restored.OrdersReady);
            var handle = Assert.Single(restored.Orders);
            await restored.RefreshAsync();
            Assert.Same(handle, Assert.Single(restored.Orders));
            Assert.Equal("exchange-1", handle.ExchangeOrderId);
            Assert.True(restored.OrdersReady);
            Assert.Single(protocol.Requests, r => r.Path == "/v5/order/create");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Revoked_and_retired_contexts_block_increases_but_native_market_reductions_and_cancel_remain_possible()
    {
        using var protocol = new FakeBybitProtocol { NetPosition = 1 };
        using var http = new HttpClient(protocol);
        bool allowed = true;
        await using var session = new BybitTradingSession(protocol.Settings with { Environment = BybitEnvironment.Real }, statePath: NewStatePath(), httpClient: http,
            sendGuard: (_, _, _, _) => allowed ? null : "Permission revoked", clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        allowed = false;
        var denied = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Market, .1m, 0);
        Assert.Equal(OrderStatus.Rejected, denied.Status);
        session.Retire();
        var reduction = session.Place(Btc, 100, OrderDirections.Sell, OrderTypes.Market, .4m, 0, reduceOnly: true);
        await Observe(session, () => reduction.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        session.Cancel(reduction);
        await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var create = Assert.Single(protocol.Requests, r => r.Path == "/v5/order/create");
        Assert.Equal("fake-key", create.Headers["X-BAPI-API-KEY"]);
        Assert.True(JsonNode.Parse(create.Body)!["reduceOnly"]!.GetValue<bool>());
        Assert.Equal("Market", JsonNode.Parse(create.Body)!["orderType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Above_exchange_maximum_is_not_auto_split_and_hedge_mode_never_becomes_ready()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var tooLarge = session.Place(Btc, 100, OrderDirections.Sell, OrderTypes.Market, 11, 0, reduceOnly: true);
        await Observe(session, () => tooLarge.State == OrderStates.Failed);
        Assert.DoesNotContain(protocol.Requests, r => r.Path == "/v5/order/create");
        protocol.HedgeMode = true;
        await Assert.ThrowsAsync<NotSupportedException>(() => session.RefreshAsync());
        Assert.False(session.PositionReady);
        Assert.False(session.OrdersReady);
    }

    [Fact]
    public void Connection_credentials_are_secret_only_and_testnet_has_a_distinct_market_identity()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-connections-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "connections.json");
        var secrets = new MemorySecrets();
        var connections = new ConnectionManager(secretStore: secrets, configurationPath: path);
        try
        {
            var profile = new ConnectionProfile { Kind = ConnectionKind.Bybit, Id = "bybit-owned", ApiKey = "private-key-id" };
            connections.Profiles.Add(profile);
            connections.SetSecret(profile, "private-signing-secret");
            connections.Save();
            Assert.True(connections.HasTradingCredentials(profile));
            Assert.DoesNotContain("private-key-id", File.ReadAllText(path));
            Assert.DoesNotContain("private-signing-secret", File.ReadAllText(path));
            Assert.Equal(BybitFeed.LinearId, profile.MarketDataId);
            profile.BybitEnvironment = BybitEnvironment.Testnet;
            Assert.Equal(BybitFeed.TestnetId, profile.MarketDataId);
            connections.SetSecret(profile, "apikey", "replacement-key");
            Assert.Equal("replacement-key", connections.BybitSettingsOf(profile).ApiKey);
            Assert.Equal(11, (int)ConnectionKind.Databento);
            Assert.Equal(12, (int)ConnectionKind.Longbridge);
        }
        finally { connections.DisposeAll(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Silent_private_store_failure_does_not_erase_the_legacy_Bybit_key_or_admit_an_unpersisted_context()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-secret-failure-" + Guid.NewGuid().ToString("N"));
        var secrets = new MemorySecrets();
        var connections = new ConnectionManager(secretStore: secrets, configurationPath: Path.Combine(root, "connections.json"));
        try
        {
            var profile = new ConnectionProfile { Kind = ConnectionKind.Bybit, Id = "unpersisted", ApiKey = "legacy-not-yet-durable" };
            connections.Profiles.Add(profile);
            connections.SetSecret(profile, "fake-signing-secret");
            secrets.RejectWrite = _ => true;
            Assert.Throws<IOException>(() => connections.BybitSettingsOf(profile));
            Assert.Throws<IOException>(() => connections.Save());
            Assert.Equal("legacy-not-yet-durable", profile.ApiKey);
            var settings = new BybitAccountSettings { ApiKey = "fake-new-key", ApiSecret = "fake-new-secret" };
            var failure = Assert.Throws<IOException>(() => connections.PreserveBybitContext(profile, settings));
            Assert.DoesNotContain(settings.ApiKey, failure.Message);
            Assert.DoesNotContain(settings.ApiSecret, failure.Message);
            Assert.Null(connections.SavedBybitContext(profile, settings.ContextIdentity));
        }
        finally { connections.DisposeAll(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_nonempty_invalid_private_context_must_be_repaired_and_confirmed_before_admission(bool malformed)
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-invalid-secret-" + Guid.NewGuid().ToString("N"));
        var secrets = new MemorySecrets();
        var connections = new ConnectionManager(secretStore: secrets, configurationPath: Path.Combine(root, "connections.json"));
        try
        {
            var profile = new ConnectionProfile { Kind = ConnectionKind.Bybit, Id = "invalid-private-context" };
            var settings = new BybitAccountSettings { ApiKey = "expected-fake-key", ApiSecret = "expected-fake-secret" };
            string account = profile.SecretAccountFor("bybit-context-" + settings.ContextIdentity);
            secrets.Set(account, malformed ? "{invalid" : JsonSerializer.Serialize(settings with { ApiKey = "other-context" }));
            secrets.RejectWrite = name => name == account;
            Assert.Throws<IOException>(() => connections.PreserveBybitContext(profile, settings));
            secrets.RejectWrite = null;
            connections.PreserveBybitContext(profile, settings);
            Assert.Equal(settings.ContextIdentity, connections.SavedBybitContext(profile, settings.ContextIdentity)!.ContextIdentity);
        }
        finally { connections.DisposeAll(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Execution_pagination_preserves_equal_time_distinct_order_identity_and_real_fee_currency()
    {
        using var protocol = new FakeBybitProtocol();
        protocol.ResponseFor = request =>
        {
            if (request.Path != "/v5/execution/list") return null;
            bool second = request.Query.Contains("cursor=next%2B%2F", StringComparison.Ordinal);
            var fill = new JsonObject
            {
                ["execId"] = "same-provider-id", ["orderId"] = second ? "order-a" : "order-b", ["orderLinkId"] = "",
                ["symbol"] = "BTCUSDT", ["side"] = "Buy", ["execType"] = "Trade", ["execQty"] = second ? "0.3" : "0.2",
                ["execPrice"] = second ? "102" : "101", ["execTime"] = "1793786400000", ["execFee"] = second ? "0.03" : "-0.01",
                ["feeCurrency"] = second ? "USDT" : "MNT",
            };
            var result = new JsonObject { ["retCode"] = 0, ["result"] = new JsonObject { ["list"] = new JsonArray(fill), ["nextPageCursor"] = second ? "" : "next+/" } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString()) };
        };
        using var http = new HttpClient(protocol);
        using var client = new BybitAccountClient(protocol.Settings, http, new FixedClock(FakeBybitProtocol.Now));
        var executions = await client.GetExecutionsAsync(FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), FakeBybitProtocol.Now.UtcDateTime, default);
        Assert.Equal(2, executions.Count);
        Assert.Equal("order-a", executions[0].OrderId);
        Assert.Equal(.3m, executions[0].Quantity);
        Assert.True(executions[0].HasQuoteFee);
        Assert.Equal("order-b", executions[1].OrderId);
        Assert.Equal(-.01m, executions[1].Fee);
        Assert.False(executions[1].HasQuoteFee);
        Assert.Equal("MNT", executions[1].FeeCurrency);
        Assert.Equal(2, protocol.Requests.Count(r => r.Path == "/v5/execution/list"));
    }

    [Fact]
    public async Task Repeated_snapshot_cursor_fails_instead_of_publishing_a_truncated_ready_snapshot()
    {
        using var protocol = new FakeBybitProtocol();
        protocol.ResponseFor = request => request.Path != "/v5/order/realtime" ? null :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"retCode":0,"result":{"list":[],"nextPageCursor":"loop"}}""") };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.RefreshAsync());
        Assert.False(session.PositionReady);
        Assert.False(session.OrdersReady);
        Assert.Equal(2, protocol.Requests.Count(r => r.Path == "/v5/order/realtime"));
    }

    [Fact]
    public async Task Rotated_credentials_keep_private_restart_context_and_stable_authenticated_account_identity()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-context-" + Guid.NewGuid().ToString("N"));
        var secrets = new MemorySecrets();
        var connections = new ConnectionManager(secretStore: secrets, configurationPath: Path.Combine(root, "connections.json"));
        var profile = new ConnectionProfile { Id = "owned", Kind = ConnectionKind.Bybit };
        connections.Profiles.Add(profile);
        using var protocol = new FakeBybitProtocol { UnknownCreate = true, HideOrders = true };
        using var http = new HttpClient(protocol);
        string state = Path.Combine(root, "owned-orders.json");
        ConnectionManager? reloaded = null;
        try
        {
            var oldSettings = protocol.Settings;
            connections.PreserveBybitContext(profile, oldSettings);
            string accountIdentity;
            await using (var original = new BybitTradingSession(oldSettings, state, http, clock: new FixedClock(FakeBybitProtocol.Now)))
            {
                await original.RefreshAsync();
                accountIdentity = original.AccountIdentity;
                original.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, .5m, 99);
                await Observe(original, () => original.LastError?.Contains("placement outcome", StringComparison.Ordinal) == true);
            }
            Assert.True(BybitTradingSession.RequiresRecovery(state));
            connections.SetSecret(profile, "apikey", "new-api-key");
            connections.SetSecret(profile, "new-signing-secret");
            connections.Save();
            // A silent failure of the new backup cannot erase the confirmed old account's recovery credentials.
            secrets.RejectWrite = name => name.Contains("bybit-context-", StringComparison.Ordinal);
            var unpersistedReplacement = connections.BybitSettingsOf(profile);
            Assert.Throws<IOException>(() => connections.PreserveBybitContext(profile, unpersistedReplacement));
            Assert.Null(connections.SavedBybitContext(profile, unpersistedReplacement.ContextIdentity));
            reloaded = new ConnectionManager(secretStore: secrets, configurationPath: Path.Combine(root, "connections.json"));
            var restoredSettings = reloaded.SavedBybitContext(reloaded.Find(profile.Id)!, oldSettings.ContextIdentity);
            Assert.NotNull(restoredSettings);
            Assert.Equal("fake-key", restoredSettings.ApiKey);
            Assert.DoesNotContain("fake-key", File.ReadAllText(Path.Combine(root, "connections.json")));
            protocol.HideOrders = false;
            await using var restored = new BybitTradingSession(restoredSettings, state, http, clock: new FixedClock(FakeBybitProtocol.Now));
            restored.Retire();
            await restored.RefreshAsync();
            Assert.Equal(accountIdentity, restored.AccountIdentity);
            Assert.Equal("demo-1234567", accountIdentity);
            Assert.Equal(OrderStatus.Rejected, restored.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Market, .1m, 0).Status);
            restored.Cancel(Assert.Single(restored.Orders));
            await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("fake-key", protocol.Requests.Last(r => r.Path == "/v5/order/cancel").Headers["X-BAPI-API-KEY"]);
            Assert.Single(protocol.Requests, r => r.Path == "/v5/order/create");
        }
        finally { reloaded?.DisposeAll(); connections.DisposeAll(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Reactivating_exact_context_does_not_reenable_a_retired_broker_view()
    {
        using var protocol = new FakeBybitProtocol { NetPosition = 1 };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        using var old = new BybitBroker(data, session, "old view");
        await session.RefreshAsync();
        old.Retire();
        session.Retire();
        session.Reactivate();
        Assert.Equal(OrderStatus.Rejected, old.Market(OrderDirections.Buy, .1m).Status);
        var exit = old.Exit(OrderTypes.Market, OrderDirections.Sell, .3m, 0);
        await Observe(session, () => exit.ExchangeOrderId.Length > 0);
        Assert.True(JsonNode.Parse(Assert.Single(protocol.Requests, r => r.Path == "/v5/order/create").Body)!["reduceOnly"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Local_fill_delivery_failure_is_retried_without_losing_or_duplicating_the_exchange_fill()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var order = session.Place(Btc, 999, OrderDirections.Buy, OrderTypes.Limit, 1, 100);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        protocol.Fill("durable-fill", .25m, 101, .0125m);
        int attempts = 0;
        var delivered = new List<BybitExecution>();
        session.FillReceived += fill =>
        {
            if (++attempts == 1) throw new IOException("Fake local history failure");
            delivered.Add(fill);
        };
        await session.RefreshAsync();
        Assert.False(session.ExecutionsReady);
        Assert.Empty(delivered);
        await session.RefreshAsync();
        await session.RefreshAsync();
        Assert.True(session.ExecutionsReady);
        Assert.Equal(.25m, Assert.Single(delivered).Quantity);
        Assert.Equal(101m, delivered[0].Price);
        Assert.Equal(2, attempts);
        Assert.Equal(.25m, order.Filled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Permission_or_context_revocation_during_held_limits_prevents_the_actual_create_POST(bool retire)
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        bool permitted = true;
        await using var session = new BybitTradingSession(protocol.Settings with { Environment = BybitEnvironment.Real }, statePath: NewStatePath(), httpClient: http,
            sendGuard: (_, _, _, _) => permitted ? null : "Permission revoked at transport", clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ResponseForAsync = async (request, _) =>
        {
            if (request.Path == "/v5/market/instruments-info") { arrived.TrySetResult(); await release.Task; }
            return null;
        };
        var order = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (retire) session.Retire(); else permitted = false;
        release.TrySetResult();
        await Observe(session, () => order.State == OrderStates.Failed);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.False(session.IsSubmissionUnknown(order));
        Assert.Same(order, Assert.Single(session.Orders));
        Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");
    }

    [Fact]
    public async Task Permission_revocation_during_held_amend_limits_preserves_the_authoritative_order()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        bool permitted = true;
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http,
            sendGuard: (_, _, _, _) => permitted ? null : "Permission revoked at transport", clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var order = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        await session.RefreshAsync();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ResponseForAsync = async (request, _) =>
        {
            if (request.Path == "/v5/market/instruments-info") { arrived.TrySetResult(); await release.Task; }
            return null;
        };
        session.Replace(order, 98, .8m, Btc);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        permitted = false;
        release.TrySetResult();
        await Observe(session, () => !session.IsReplacementPending(order));
        Assert.Equal(99m, order.Price);
        Assert.Equal(1m, order.QuantityToFill);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/amend");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_transport_guard_is_rechecked_after_initial_time_or_rejected_timestamp_resynchronization(bool retry)
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        using var client = new BybitAccountClient(protocol.Settings, http, new FixedClock(FakeBybitProtocol.Now));
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int clocks = 0, rejected = 0;
        bool permitted = true;
        protocol.ResponseForAsync = async (request, _) =>
        {
            if (request.Path == "/v5/order/create" && retry && Interlocked.Increment(ref rejected) == 1)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"retCode":10002,"retMsg":"Timestamp rejected","result":{}}""") };
            if (request.Path == "/v5/market/time" && Interlocked.Increment(ref clocks) == (retry ? 2 : 1))
            { arrived.TrySetResult(); await release.Task; }
            return null;
        };
        var placing = client.PlaceOrderAsync("BTCUSDT", OrderDirections.Buy, OrderTypes.Limit, 1, 99, false,
            "th-held-clock", 100, default, () => permitted ? null : "Permission revoked at transport");
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        permitted = false;
        release.TrySetResult();
        var rejection = await Assert.ThrowsAsync<BybitApiException>(() => placing);
        Assert.Equal(-1, rejection.Code);
        Assert.Equal(retry ? 1 : 0, protocol.Requests.Count(request => request.Path == "/v5/order/create"));
        Assert.False(protocol.CreateReceived.Task.IsCompleted);
    }

    [Fact]
    public async Task Native_reducing_exit_is_not_blocked_by_exposure_revocation_while_metadata_is_held()
    {
        using var protocol = new FakeBybitProtocol { NetPosition = 1 };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http,
            sendGuard: (_, _, _, _) => "New exposure disabled", clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ResponseForAsync = async (request, _) =>
        {
            if (request.Path == "/v5/market/instruments-info") { arrived.TrySetResult(); await release.Task; }
            return null;
        };
        var exit = session.Place(Btc, 100, OrderDirections.Sell, OrderTypes.Limit, .4m, 110, reduceOnly: true);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Retire();
        release.TrySetResult();
        await Observe(session, () => exit.ExchangeOrderId.Length > 0);
        Assert.True(JsonNode.Parse(Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create").Body)!["reduceOnly"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Missing_nonflat_average_or_unrealized_result_is_unknown_not_confirmed_zero()
    {
        using var protocol = new FakeBybitProtocol();
        protocol.ResponseFor = request => request.Path != "/v5/position/list" ? null : new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"retCode":0,"result":{"list":[{"symbol":"BTCUSDT","positionIdx":0,"size":"1","side":"Buy","avgPrice":"","unrealisedPnl":""}]}}""") };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.RefreshAsync());
        Assert.False(session.PositionReady);
        Assert.Null(BybitAccountMetrics.From(session, TimeZoneInfo.Utc, FakeBybitProtocol.Now.UtcDateTime).OpenPnl);
    }

    [Fact]
    public async Task Cumulative_fill_without_execution_ids_stays_financially_unknown_until_exact_order_history_arrives()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var order = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        protocol.Fill("late-execution", .5m, 101, .025m);
        bool exactVisible = false;
        protocol.ResponseFor = request => request.Path == "/v5/execution/list" && (!exactVisible || !request.Query.Contains("orderId=", StringComparison.Ordinal))
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"retCode":0,"result":{"list":[],"nextPageCursor":""}}""") } : null;
        await session.RefreshAsync();
        Assert.Equal(.5m, order.Filled);
        Assert.False(session.ExecutionsReady);
        Assert.Contains("financial results remain unknown", session.LastError);
        Assert.Empty(session.Executions);
        exactVisible = true;
        await session.RefreshAsync();
        Assert.True(session.ExecutionsReady);
        Assert.Equal(.5m, Assert.Single(session.Executions).Quantity);
        Assert.Equal(101m, session.Executions[0].Price);
        Assert.Contains(protocol.Requests, request => request.Path == "/v5/execution/list" && request.Query.Contains("orderId=exchange-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Old_holding_closed_today_uses_reported_PnL_without_inventing_an_opening_fill()
    {
        using var protocol = new FakeBybitProtocol();
        const string closedJson = """{"retCode":0,"result":{"list":[{"symbol":"BTCUSDT","orderId":"closing-old-holding","closedPnl":"10","closedSize":"1","createdTime":"1793786400000","updatedTime":"1793786400000"}],"nextPageCursor":""}}""";
        protocol.ResponseFor = request => request.Path != "/v5/position/closed-pnl" ? null : new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(closedJson) };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var metrics = BybitAccountMetrics.From(session, TimeZoneInfo.Utc, FakeBybitProtocol.Now.UtcDateTime);
        Assert.Equal(10m, metrics.ReportedRealizedToday);
        Assert.False(metrics.HasCompleteOpeningBasis);
        Assert.Empty(session.Executions);
        Assert.Equal(1000m, session.Wallet!.Equity);
        Assert.False(session.ExecutionsReady); // Closed size is known, but its real execution identity and fee have not arrived.
        session.FillReceived += _ => { };
        protocol.ResponseFor = request => request.Path switch
        {
            "/v5/position/closed-pnl" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(closedJson) },
            "/v5/execution/list" when request.Query.Contains("orderId=closing-old-holding", StringComparison.Ordinal) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"retCode":0,"result":{"list":[{"execId":"actual-close","orderId":"closing-old-holding","orderLinkId":"","symbol":"BTCUSDT","side":"Sell","execQty":"1","execPrice":"110","execTime":"1793786400000","execFee":"0.05","feeCurrency":"USDT","closedSize":"1","execType":"Trade"}],"nextPageCursor":""}}""") },
            _ => null,
        };
        await session.RefreshAsync();
        Assert.True(session.ExecutionsReady);
        Assert.Equal(110m, Assert.Single(session.Executions).Price);
        Assert.Equal(.05m, session.Executions[0].Fee);
        metrics = BybitAccountMetrics.From(session, TimeZoneInfo.Utc, FakeBybitProtocol.Now.UtcDateTime);
        Assert.Equal(10m, metrics.ReportedRealizedToday);
        Assert.False(metrics.HasCompleteOpeningBasis);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("1", false)]
    [InlineData("0", true)]
    public async Task Missing_or_unreported_closing_execution_coverage_does_not_invent_zero_realized_today(string? closedQuantity, bool zeroKnown)
    {
        using var protocol = new FakeBybitProtocol();
        string executionJson = JsonSerializer.Serialize(new
        {
            retCode = 0,
            result = new
            {
                list = new[] { new { execId = "closing-or-unknown", orderId = "resting-old-order", orderLinkId = "", symbol = "BTCUSDT",
                    side = "Sell", execQty = "1", execPrice = "110", execTime = "1793786400000", execFee = "0.05",
                    feeCurrency = "USDT", closedSize = closedQuantity, execType = "Trade" } },
                nextPageCursor = "",
            },
        });
        protocol.ResponseFor = request => request.Path == "/v5/execution/list" ?
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(executionJson) } : null;
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        session.FillReceived += _ => { };
        await session.RefreshAsync();
        var metrics = BybitAccountMetrics.From(session, TimeZoneInfo.Utc, FakeBybitProtocol.Now.UtcDateTime);
        if (zeroKnown) Assert.Equal(0m, metrics.ReportedRealizedToday); else Assert.Null(metrics.ReportedRealizedToday);
        Assert.False(metrics.HasCompleteOpeningBasis);
        Assert.Equal(997m, session.Wallet!.WalletBalance);
    }

    [Fact]
    public async Task Cross_day_cumulative_closed_PnL_is_unknown_for_today_instead_of_being_allocated_to_the_latest_update()
    {
        using var protocol = new FakeBybitProtocol();
        long opened = FakeBybitProtocol.Now.AddDays(-1).ToUnixTimeMilliseconds(), updated = FakeBybitProtocol.Now.ToUnixTimeMilliseconds();
        protocol.ResponseFor = request => request.Path != "/v5/position/closed-pnl" ? null : new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent($$$"""{"retCode":0,"result":{"list":[{"symbol":"BTCUSDT","orderId":"cross-day-close","closedPnl":"15","closedSize":"2","createdTime":"{{{opened}}}","updatedTime":"{{{updated}}}"}],"nextPageCursor":""}}""") };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var metrics = BybitAccountMetrics.From(session, TimeZoneInfo.Utc, FakeBybitProtocol.Now.UtcDateTime);
        Assert.True(session.ClosedPnlReady);
        Assert.Null(metrics.ReportedRealizedToday);
        Assert.False(metrics.HasCompleteOpeningBasis);
        Assert.Contains("spans trading days", metrics.Warning, StringComparison.Ordinal);
        Assert.Equal(997m, session.Wallet!.WalletBalance);
    }

    [Fact]
    public async Task A_confirmed_flat_account_with_an_unpublished_fill_cannot_forget_its_recovery_context()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        var order = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await Observe(session, () => order.ExchangeOrderId.Length > 0);
        protocol.Fill("not-yet-journalled", 1, 101, .05m);
        await session.RefreshAsync();
        Assert.True(session.SnapshotReady);
        Assert.Equal(OrderStates.Done, order.State);
        Assert.DoesNotContain(session.Positions, position => position.Size != 0);
        Assert.True(session.HasOpenPositionsOrOrders);
        Assert.False(session.TryBeginIdleShutdown(out _));
        int delivered = 0;
        session.FillReceived += _ => delivered++;
        await session.RefreshAsync();
        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task Shutdown_drains_held_private_HTTP_and_manual_fill_publication_before_owned_transport_disposal()
    {
        using var protocol = new FakeBybitProtocol();
        var http = new HttpClient(protocol);
        var session = new BybitTradingSession(protocol.Settings, statePath: NewStatePath(), httpClient: http, clock: new FixedClock(FakeBybitProtocol.Now), ownsHttpClient: true);
        await session.RefreshAsync();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ResponseForAsync = async (request, _) =>
        {
            if (request.Path == "/v5/market/instruments-info") { arrived.TrySetResult(); await release.Task; }
            return null;
        };
        session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = session.DisposeAsync().AsTask();
        Assert.False(stopping.IsCompleted);
        Assert.False(protocol.WasDisposed);
        release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(protocol.WasDisposed);
        Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");

        using var fillsProtocol = new FakeBybitProtocol();
        var fillsHttp = new HttpClient(fillsProtocol);
        var fillsSession = new BybitTradingSession(fillsProtocol.Settings, statePath: NewStatePath(), httpClient: fillsHttp, clock: new FixedClock(FakeBybitProtocol.Now), ownsHttpClient: true);
        await fillsSession.RefreshAsync();
        var order = fillsSession.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        await Observe(fillsSession, () => order.ExchangeOrderId.Length > 0);
        fillsProtocol.Fill("held-publication", 1, 101, .05m);
        using var publicationRelease = new ManualResetEventSlim();
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fillsSession.FillReceived += _ => { publicationEntered.TrySetResult(); if (!publicationRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fake publication not released"); };
        var refreshing = Task.Run(() => fillsSession.RefreshAsync());
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var draining = fillsSession.DisposeAsync().AsTask();
        try
        {
            Assert.True(fillsSession.HasOpenPositionsOrOrders);
            Assert.False(draining.IsCompleted);
            Assert.False(fillsProtocol.WasDisposed);
        }
        finally { publicationRelease.Set(); }
        await refreshing.WaitAsync(TimeSpan.FromSeconds(10));
        await draining.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fillsProtocol.WasDisposed);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public Func<string, bool>? RejectWrite { get; set; }
        public string Get(string account) => _values.GetValueOrDefault(account, "");
        public void Set(string account, string? secret) { if (RejectWrite?.Invoke(account) == true) return; if (string.IsNullOrEmpty(secret)) _values.Remove(account); else _values[account] = secret; }
    }
    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}

/// <summary>Fake v5 protocol, including asynchronous command acceptance and separate authoritative reports. No sockets or providers.</summary>
internal sealed class FakeBybitProtocol : HttpMessageHandler
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1793786400000);
    private readonly object _gate = new();
    private JsonObject? _order;
    private readonly Dictionary<string, JsonObject> _orders = new(StringComparer.Ordinal);
    private JsonObject? _amendment;
    private readonly List<JsonObject> _fills = new();
    private int _version;
    public bool HoldCreate { get; set; }
    public bool UnknownCreate { get; set; }
    public bool HideOrders { get; set; }
    public bool ApplyCancelImmediately { get; set; } = true;
    public bool ApplyAmendImmediately { get; set; } = true;
    public bool FailPositions { get; set; }
    public bool HedgeMode { get; set; }
    public Func<Request, HttpResponseMessage?>? ResponseFor { get; set; }
    public Func<Request, CancellationToken, Task<HttpResponseMessage?>>? ResponseForAsync { get; set; }
    public bool WasDisposed { get; private set; }
    protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    public decimal NetPosition { get; set; }
    public decimal AveragePositionPrice { get; set; } = 100;
    public ConcurrentQueue<Request> Requests { get; } = new();
    public TaskCompletionSource CreateReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CreateRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancelReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AmendReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public BybitAccountSettings Settings => new() { ApiKey = "fake-key", ApiSecret = "fake-secret", BaseUrl = "http://127.0.0.1:1", PollInterval = TimeSpan.FromMilliseconds(20) };
    internal sealed record Request(string Path, string Query, string Body, Dictionary<string, string> Headers);
    private static string F(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static JsonObject Ok(JsonNode result) => new() { ["retCode"] = 0, ["retMsg"] = "OK", ["time"] = Now.ToUnixTimeMilliseconds(), ["result"] = result };
    private static HttpResponseMessage Response(JsonObject body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    private static JsonObject List(IEnumerable<JsonObject> rows) => new() { ["list"] = new JsonArray(rows.Select(r => (JsonNode)r.DeepClone()).ToArray()), ["nextPageCursor"] = "" };
    private void Stamp(JsonObject? order = null, DateTimeOffset? observedAt = null)
    {
        order ??= _order;
        if (order == null) return;
        long prior = long.TryParse(order["updatedTime"]?.GetValue<string>(), out long value) ? value : 0;
        order["updatedTime"] = (Math.Max(prior, (observedAt ?? Now).ToUnixTimeMilliseconds()) + ++_version).ToString(CultureInfo.InvariantCulture);
    }
    public void ConfirmCancel() { lock (_gate) { _order!["orderStatus"] = "Cancelled"; Stamp(); } }
    /// <summary>Bybit triggers a conditional under the same order id and it then works as its orderType (v5 docs: orderType
    /// "is the order type after the order was triggered"). By default the report keeps its triggerPrice: a live Bybit capture
    /// (ccxt test/static/response/bybit.json) shows a Filled triggered spot BidirectionalTpslOrder still carrying
    /// "triggerPrice": "1.9407". Linear conditionals keeping it is inferred, so tests also cover a cleared triggerPrice.</summary>
    public void TriggerStop(string orderId, bool keepTriggerPrice = true)
    {
        lock (_gate)
        {
            var order = _orders[orderId];
            if (!order.ContainsKey("triggerPrice") || decimal.Parse(order["triggerPrice"]!.GetValue<string>(), CultureInfo.InvariantCulture) <= 0 ||
                order["orderStatus"]!.GetValue<string>() != "Untriggered")
                throw new InvalidOperationException("The native fixture order is not a waiting conditional stop.");
            if (!keepTriggerPrice) order["triggerPrice"] = "0";
            order["orderStatus"] = "New";
            Stamp(order);
        }
    }
    /// <summary>A conditional placed outside Trading Hub (no client link id), waiting at Bybit.</summary>
    public string AddExternalConditional(string side, decimal quantity, decimal triggerPrice, decimal? limitPrice = null, bool reduceOnly = false)
    {
        lock (_gate)
        {
            string orderId = "exchange-" + (_orders.Count + 1).ToString(CultureInfo.InvariantCulture);
            var order = new JsonObject { ["orderId"] = orderId, ["orderLinkId"] = "", ["symbol"] = "BTCUSDT", ["side"] = side,
                ["orderType"] = limitPrice.HasValue ? "Limit" : "Market", ["price"] = F(limitPrice ?? 0), ["triggerPrice"] = F(triggerPrice),
                ["qty"] = F(quantity), ["cumExecQty"] = "0", ["leavesQty"] = F(quantity), ["avgPrice"] = "0", ["orderStatus"] = "Untriggered",
                ["reduceOnly"] = reduceOnly, ["positionIdx"] = 0, ["createdTime"] = Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) };
            _orders.Add(orderId, order);
            Stamp(order);
            return orderId;
        }
    }
    public void ConfirmAmend()
    {
        lock (_gate)
        {
            if (_amendment == null) return;
            _order!["qty"] = _amendment["qty"]!.GetValue<string>();
            string field = _amendment.ContainsKey("triggerPrice") ? "triggerPrice" : "price";
            _order[field] = _amendment[field]!.GetValue<string>();
            _order["leavesQty"] = F(decimal.Parse(_order["qty"]!.GetValue<string>(), CultureInfo.InvariantCulture) - decimal.Parse(_order["cumExecQty"]!.GetValue<string>(), CultureInfo.InvariantCulture));
            Stamp();
        }
    }
    public void Fill(string id, decimal quantity, decimal price, decimal? fee, bool preserveCanceled = false,
        string? orderId = null, DateTimeOffset? executionAt = null, string? feeCurrency = "USDT", decimal? closedQuantity = null)
    {
        lock (_gate)
        {
            var order = orderId == null ? _order! : _orders[orderId];
            decimal prior = decimal.Parse(order["cumExecQty"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            decimal total = decimal.Parse(order["qty"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            decimal average = decimal.Parse(order["avgPrice"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            order["cumExecQty"] = F(prior + quantity); order["leavesQty"] = F(total - prior - quantity);
            order["avgPrice"] = F((average * prior + price * quantity) / (prior + quantity));
            if (!preserveCanceled) order["orderStatus"] = prior + quantity == total ? "Filled" : "PartiallyFilled";
            _fills.Add(new JsonObject { ["execId"] = id, ["orderId"] = order["orderId"]!.GetValue<string>(), ["orderLinkId"] = order["orderLinkId"]!.GetValue<string>(),
                ["symbol"] = order["symbol"]!.GetValue<string>(), ["side"] = order["side"]!.GetValue<string>(), ["execQty"] = F(quantity), ["execPrice"] = F(price),
                ["execFee"] = fee.HasValue ? F(fee.Value) : null, ["feeCurrency"] = feeCurrency ?? "",
                ["closedSize"] = closedQuantity.HasValue ? F(closedQuantity.Value) : null,
                ["execTime"] = (executionAt ?? Now).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ["execType"] = "Trade" });
            Stamp(order, executionAt);
        }
    }
    public void CorrectFee(string id, decimal? fee, string? currency = null, string? orderId = null)
    {
        lock (_gate)
        {
            var fill = _fills.Single(fill => fill["execId"]!.GetValue<string>() == id && (orderId == null || fill["orderId"]!.GetValue<string>() == orderId));
            fill["execFee"] = fee.HasValue ? F(fee.Value) : null;
            if (currency != null) fill["feeCurrency"] = currency;
        }
    }
    public void CorrectQuantity(string id, decimal quantity, string? orderId = null)
    {
        lock (_gate)
        {
            var fill = _fills.Single(fill => fill["execId"]!.GetValue<string>() == id && (orderId == null || fill["orderId"]!.GetValue<string>() == orderId));
            fill["execQty"] = F(quantity);
            if (fill["closedSize"] is { } closed)
                fill["closedSize"] = F(Math.Min(decimal.Parse(closed.GetValue<string>(), CultureInfo.InvariantCulture), quantity));
        }
    }
    private JsonObject? FindOrder(JsonObject input)
    {
        string? id = input["orderId"]?.GetValue<string>();
        return id != null ? _orders.GetValueOrDefault(id) :
            _orders.Values.FirstOrDefault(order => order["orderLinkId"]!.GetValue<string>() == input["orderLinkId"]?.GetValue<string>());
    }
    private static string QueryValue(string query, string key)
    {
        foreach (string pair in query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == key) return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        return "";
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new Request(path, request.RequestUri.Query.TrimStart('?'), body,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
        Requests.Enqueue(captured);
        if (ResponseFor?.Invoke(captured) is { } scripted) return scripted;
        if (ResponseForAsync != null && await ResponseForAsync(captured, cancellationToken) is { } scriptedAsync) return scriptedAsync;
        if (path == "/v5/market/time") return Response(Ok(new JsonObject { ["timeSecond"] = "1793786400" }));
        if (path == "/v5/account/info") return Response(Ok(new JsonObject { ["unifiedMarginStatus"] = 5 }));
        if (path == "/v5/user/query-api") return Response(Ok(new JsonObject { ["userID"] = 1234567, ["readOnly"] = 0 }));
        if (path == "/v5/position/closed-pnl") return Response(Ok(List(Array.Empty<JsonObject>())));
        if (path == "/v5/market/instruments-info") return Response(Ok(List(new[] { new JsonObject {
            ["symbol"]="BTCUSDT",["baseCoin"]="BTC",["quoteCoin"]="USDT",["settleCoin"]="USDT",["contractType"]="LinearPerpetual",["status"]="Trading",
            ["lotSizeFilter"]=new JsonObject{["minOrderQty"]="0.001",["maxOrderQty"]="10",["maxMktOrderQty"]="10",["qtyStep"]="0.001"},
            ["priceFilter"]=new JsonObject{["minPrice"]="0.1",["maxPrice"]="1000000",["tickSize"]="0.1"} } })));
        if (path == "/v5/account/wallet-balance") return Response(Ok(List(new[] { new JsonObject { ["totalEquity"] = "1000", ["totalAvailableBalance"] = "800", ["totalPerpUPL"] = "3", ["totalWalletBalance"] = "997" } })));
        if (path == "/v5/position/list")
        {
            if (FailPositions) throw new HttpRequestException("Fake snapshot disconnected");
            return Response(Ok(List(new[] { new JsonObject { ["symbol"] = "BTCUSDT", ["positionIdx"] = HedgeMode ? 1 : 0, ["size"] = F(Math.Abs(NetPosition)),
                ["side"] = NetPosition > 0 ? "Buy" : NetPosition < 0 ? "Sell" : "", ["avgPrice"] = F(AveragePositionPrice), ["unrealisedPnl"] = "3", ["leverage"] = "2" } })));
        }
        if (path == "/v5/order/create")
        {
            var input = JsonNode.Parse(body)!.AsObject();
            string orderId;
            lock (_gate)
            {
                if (_orders.Values.Any(order => order["orderLinkId"]!.GetValue<string>() == input["orderLinkId"]!.GetValue<string>()))
                    return Response(new JsonObject { ["retCode"] = 10014, ["retMsg"] = "Duplicate fake client identity", ["result"] = new JsonObject() });
                orderId = "exchange-" + (_orders.Count + 1).ToString(CultureInfo.InvariantCulture);
                _order = input.DeepClone().AsObject();
                _order["orderId"] = orderId; _order["orderStatus"] = input.ContainsKey("triggerPrice") ? "Untriggered" : "New";
                _order["cumExecQty"] = "0"; _order["leavesQty"] = input["qty"]!.GetValue<string>(); _order["avgPrice"] = "0";
                _order["createdTime"] = Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                _orders.Add(orderId, _order);
                Stamp();
            }
            CreateReceived.TrySetResult();
            if (HoldCreate) await CreateRelease.Task.WaitAsync(cancellationToken);
            if (UnknownCreate) return Response(new JsonObject { ["retCode"] = 10000, ["retMsg"] = "Server timeout", ["result"] = new JsonObject() }, HttpStatusCode.ServiceUnavailable);
            return Response(Ok(new JsonObject { ["orderId"] = orderId, ["orderLinkId"] = input["orderLinkId"]!.GetValue<string>() }));
        }
        lock (_gate)
        {
            if (path == "/v5/order/cancel")
            {
                _order = FindOrder(JsonNode.Parse(body)!.AsObject());
                if (_order == null) return Response(new JsonObject { ["retCode"] = 110001, ["retMsg"] = "Fake order not found", ["result"] = new JsonObject() });
                if (ApplyCancelImmediately) ConfirmCancel();
                CancelReceived.TrySetResult();
                return Response(Ok(new JsonObject { ["orderId"] = _order["orderId"]!.GetValue<string>() }));
            }
            if (path == "/v5/order/amend")
            {
                _amendment = JsonNode.Parse(body)!.AsObject();
                _order = FindOrder(_amendment);
                if (_order == null) return Response(new JsonObject { ["retCode"] = 110001, ["retMsg"] = "Fake order not found", ["result"] = new JsonObject() });
                if (ApplyAmendImmediately) ConfirmAmend();
                AmendReceived.TrySetResult();
                return Response(Ok(new JsonObject { ["orderId"] = _order["orderId"]!.GetValue<string>() }));
            }
            if (path == "/v5/order/realtime" || path == "/v5/order/history")
            {
                string id = QueryValue(request.RequestUri.Query, "orderId"), link = QueryValue(request.RequestUri.Query, "orderLinkId");
                bool lookup = id.Length > 0 || link.Length > 0;
                var rows = _orders.Values.Where(order => (id.Length == 0 || order["orderId"]!.GetValue<string>() == id) &&
                    (link.Length == 0 || order["orderLinkId"]!.GetValue<string>() == link) &&
                    (lookup || order["orderStatus"]!.GetValue<string>() is not ("Filled" or "Cancelled" or "Rejected" or "Deactivated" or "PartiallyFilledCanceled")));
                return Response(Ok(List(HideOrders ? Array.Empty<JsonObject>() : rows)));
            }
            if (path == "/v5/execution/list") return Response(Ok(List(_fills)));
        }
        throw new InvalidOperationException("Unexpected fake Bybit route: " + path);
    }
}
