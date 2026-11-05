using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.Longbridge;
using TradingHub.Market;
using TradingHub.Tests;

// Fake wire reports, not an alternative broker. The production clients, sessions, account factory,
// journal and UI own every interpretation. Fixtures follow BybitProviderTests/LongbridgeFeedTests.
internal sealed class KvpProvidersExchange : IDisposable
{
    public FakeHttpServer Server { get; } = new();
    public ConcurrentDictionary<string, Context> Contexts { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<string> UnexpectedRoutes { get; } = new();
    private int _invalidSignatures, _signedRequests, _adjustedRequests;
    public int InvalidSignatures => Volatile.Read(ref _invalidSignatures);
    public int SignedRequests => Volatile.Read(ref _signedRequests);
    public int AdjustedRequests => Volatile.Read(ref _adjustedRequests);
    public KvpProvidersExchange() => Server.OnHttpAsync = Respond;
    public Context Add(string key, string secret, long uid, decimal initialHolding = 0)
    {
        var context = new Context(key, secret, uid) { NetPosition = initialHolding };
        if (!Contexts.TryAdd(key, context)) throw new InvalidOperationException("Duplicate fixture credential context");
        return context;
    }
    /// <summary>Reuse the same wire fixture without sockets; an optional hook may deliberately ignore cancellation until released.</summary>
    public HttpMessageHandler CreateHandler(Func<FakeRequest, CancellationToken, Task>? beforeResponse = null) => new Handler(this, beforeResponse);
    private sealed class Handler(KvpProvidersExchange exchange, Func<FakeRequest, CancellationToken, Task>? beforeResponse) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new FakeRequest(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
                request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (beforeResponse != null) await beforeResponse(captured, cancellationToken).ConfigureAwait(false);
            var (status, body) = await exchange.Respond(captured, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    public sealed class UidGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class CreatePlan(bool hold = false, bool ambiguous = false)
    {
        public bool Hold { get; } = hold;
        public bool Ambiguous { get; } = ambiguous;
        public TaskCompletionSource<string> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Context(string key, string secret, long uid)
    {
        private sealed class AccountReports
        {
            internal readonly object Gate = new();
            internal readonly Dictionary<string, JsonObject> Orders = new(StringComparer.Ordinal);
            internal readonly HashSet<string> Hidden = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, JsonObject> Amendments = new(StringComparer.Ordinal);
            internal readonly List<JsonObject> Executions = new();
            internal readonly List<JsonObject> ClosedPnl = new();
            internal decimal NetPosition;
            internal int Version;
        }
        private AccountReports _reports = new();
        internal object Gate => _reports.Gate;
        internal Dictionary<string, JsonObject> Orders => _reports.Orders;
        internal HashSet<string> Hidden => _reports.Hidden;
        internal Dictionary<string, JsonObject> Amendments => _reports.Amendments;
        internal List<JsonObject> Executions => _reports.Executions;
        internal List<JsonObject> ClosedPnl => _reports.ClosedPnl;
        internal int Version { get => _reports.Version; set => _reports.Version = value; }
        public string Key { get; } = key;
        internal string Secret { get; } = secret;
        public long Uid { get; } = uid;
        public decimal NetPosition { get { lock (Gate) return _reports.NetPosition; } set { lock (Gate) _reports.NetPosition = value; } }
        // Call before publishing the rotated key to AppServices. These are two credentials for one native account,
        // not two independent copies that could hide a duplicate create or contradictory cancellation report.
        public void ShareAccountReports(Context prior)
        {
            if (Uid != prior.Uid) throw new InvalidOperationException("Only the same fake native UID can share account reports.");
            _reports = prior._reports;
        }
        public bool FailPositions { get; set; }
        public bool FailExecutions { get; set; }
        public bool HedgeMode { get; set; }
        public int UtaStatus { get; set; } = 5;
        public bool WalletKnown { get; set; } = true;
        public bool ConfirmCancelImmediately { get; set; } = true;
        public bool ConfirmAmendImmediately { get; set; } = true;
        public CreatePlan? NextCreate { get; set; }
        public UidGate? HoldUid { get; set; }
        public ConcurrentQueue<FakeRequest> Requests { get; } = new();
        public int Count(string route) => Requests.Count(request => request.Route == route);
        /// <summary>Historical reports for an already-closed holding; initialHolding is its authoritative post-close remainder.</summary>
        public void ReportPriorClose(decimal quantity, decimal price, decimal fee, decimal reportedPnl)
        {
            lock (Gate)
            {
                string time = F(DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds());
                Executions.Add(new JsonObject
                {
                    ["execId"] = "ui-prior-close", ["orderId"] = "preexisting-holding-close", ["orderLinkId"] = "",
                    ["symbol"] = "BTCUSDT", ["side"] = "Sell", ["execQty"] = F(quantity), ["execPrice"] = F(price),
                    ["execFee"] = F(fee), ["feeCurrency"] = "USDT", ["closedSize"] = F(quantity),
                    ["execTime"] = time, ["execType"] = "Trade",
                });
                ClosedPnl.Add(new JsonObject { ["symbol"] = "BTCUSDT", ["orderId"] = "preexisting-holding-close",
                    ["closedPnl"] = F(reportedPnl), ["closedSize"] = F(quantity), ["createdTime"] = time, ["updatedTime"] = time });
            }
        }
        public void Reveal(string id) { lock (Gate) Hidden.Remove(id); }
        public void ConfirmCancel(string id) { lock (Gate) { Orders[id]["orderStatus"] = "Cancelled"; Stamp(Orders[id]); } }
        public void ConfirmAmend(string id)
        {
            lock (Gate)
            {
                var amendment = Amendments[id]; var order = Orders[id];
                order["qty"] = amendment["qty"]!.GetValue<string>();
                string field = amendment.ContainsKey("triggerPrice") ? "triggerPrice" : "price";
                order[field] = amendment[field]!.GetValue<string>();
                order["leavesQty"] = F(D(order, "qty") - D(order, "cumExecQty"));
                Stamp(order);
            }
        }
        internal void Stamp(JsonObject order) => order["updatedTime"] = F(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ++Version);
        public void Fill(string orderId, string executionId, decimal quantity, decimal price, decimal? fee, string currency, bool preserveCanceled = false, decimal? closedQuantity = 0)
        {
            lock (Gate)
            {
                var order = Orders[orderId];
                decimal prior = D(order, "cumExecQty"), total = D(order, "qty"), average = D(order, "avgPrice");
                order["cumExecQty"] = F(prior + quantity); order["leavesQty"] = F(total - prior - quantity);
                order["avgPrice"] = F((average * prior + price * quantity) / (prior + quantity));
                if (!preserveCanceled) order["orderStatus"] = prior + quantity == total ? "Filled" : "PartiallyFilled";
                bool buy = order["side"]!.GetValue<string>() == "Buy";
                NetPosition += buy ? quantity : -quantity;
                Executions.Add(new JsonObject
                {
                    ["execId"] = executionId, ["orderId"] = orderId, ["orderLinkId"] = order["orderLinkId"]!.GetValue<string>(),
                    ["symbol"] = "BTCUSDT", ["side"] = buy ? "Buy" : "Sell", ["execQty"] = F(quantity), ["execPrice"] = F(price),
                    ["execFee"] = fee.HasValue ? F(fee.Value) : "", ["feeCurrency"] = currency,
                    ["closedSize"] = closedQuantity.HasValue ? F(closedQuantity.Value) : "",
                    ["execTime"] = F(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ["execType"] = "Trade",
                });
                Stamp(order);
            }
        }
        public void CorrectFee(string executionId, decimal? fee, string currency)
        {
            lock (Gate)
            {
                var execution = Executions.Single(row => row["execId"]!.GetValue<string>() == executionId);
                execution["execFee"] = fee.HasValue ? F(fee.Value) : "";
                execution["feeCurrency"] = currency;
            }
        }
        public void ReportExecutionPrice(string executionId, decimal price)
        {
            lock (Gate) Executions.Single(row => row["execId"]!.GetValue<string>() == executionId)["execPrice"] = F(price);
        }
        public JsonObject Report(string id) { lock (Gate) return Orders[id].DeepClone().AsObject(); }
    }
    private static string F(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal D(JsonObject row, string name) => decimal.Parse(row[name]!.GetValue<string>(), CultureInfo.InvariantCulture);
    private static JsonObject List(IEnumerable<JsonObject> rows) => new()
    { ["list"] = new JsonArray(rows.Select(row => (JsonNode)row.DeepClone()).ToArray()), ["nextPageCursor"] = "" };
    private static (int Status, string Body) Ok(JsonObject result) => (200, new JsonObject
    { ["retCode"] = 0, ["retMsg"] = "OK", ["time"] = DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeMilliseconds(), ["result"] = result }.ToJsonString());
    private static (int Status, string Body) Unavailable() => (503, "{\"retCode\":10000,\"retMsg\":\"Isolated fixture unavailable\",\"result\":{}}");
    private static JsonObject Instrument() => new()
    {
        ["symbol"] = "BTCUSDT", ["baseCoin"] = "BTC", ["quoteCoin"] = "USDT", ["settleCoin"] = "USDT",
        ["contractType"] = "LinearPerpetual", ["status"] = "Trading", ["launchTime"] = "1609459200000",
        ["lotSizeFilter"] = new JsonObject { ["minOrderQty"] = "0.001", ["maxOrderQty"] = "10", ["maxMktOrderQty"] = "10", ["qtyStep"] = "0.001" },
        ["priceFilter"] = new JsonObject { ["minPrice"] = "0.1", ["maxPrice"] = "1000000", ["tickSize"] = "0.1" },
    };
    private async ValueTask<(int Status, string Body)> Respond(FakeRequest request, CancellationToken ct)
    {
        if (request.Route == "/v5/market/time") return Ok(new JsonObject { ["timeSecond"] = F(DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeSeconds()) });
        if (request.Route == "/v5/market/instruments-info") return Ok(List([Instrument()]));
        if (request.Route == "/v5/market/kline") return Ok(List([]));
        if (!request.Headers.TryGetValue("X-BAPI-API-KEY", out var key) || !Contexts.TryGetValue(key, out var context))
        {
            UnexpectedRoutes.Enqueue(request.Route);
            return (401, "{\"retCode\":10003,\"retMsg\":\"Unknown fixture context\",\"result\":{}}");
        }
        context.Requests.Enqueue(request);
        Interlocked.Increment(ref _signedRequests);
        string payload = request.Method == "GET" ? request.Path[(request.Path.IndexOf('?') + 1)..] : request.Body;
        if (request.Method == "GET" && !request.Path.Contains('?')) payload = "";
        string timestamp = request.Headers.GetValueOrDefault("X-BAPI-TIMESTAMP", "");
        string window = request.Headers.GetValueOrDefault("X-BAPI-RECV-WINDOW", "");
        string expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(context.Secret), Encoding.UTF8.GetBytes(timestamp + key + window + payload))).ToLowerInvariant();
        if (window != "10000" || expected != request.Headers.GetValueOrDefault("X-BAPI-SIGN"))
        {
            Interlocked.Increment(ref _invalidSignatures);
            return (200, "{\"retCode\":10004,\"retMsg\":\"Fixture signature mismatch\",\"result\":{}}");
        }
        if (long.TryParse(timestamp, out long ms) && Math.Abs(ms - DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeMilliseconds()) < 5000)
            Interlocked.Increment(ref _adjustedRequests);
        if (request.Route == "/v5/account/info") return Ok(new JsonObject { ["unifiedMarginStatus"] = context.UtaStatus });
        if (request.Route == "/v5/user/query-api")
        {
            if (context.HoldUid is { } hold)
            {
                hold.Entered.TrySetResult();
                await hold.Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return Ok(new JsonObject { ["userID"] = context.Uid, ["readOnly"] = 0 });
        }
        if (request.Route == "/v5/account/wallet-balance") return Ok(List([context.WalletKnown
            ? new JsonObject { ["totalEquity"] = "1000", ["totalAvailableBalance"] = "800", ["totalPerpUPL"] = "3", ["totalWalletBalance"] = "997" }
            : new JsonObject { ["totalEquity"] = "", ["totalAvailableBalance"] = "", ["totalPerpUPL"] = "", ["totalWalletBalance"] = "" }]));
        if (request.Route == "/v5/position/list")
        {
            if (context.FailPositions) return Unavailable();
            decimal position = context.NetPosition;
            return Ok(List([new JsonObject
            {
                ["symbol"] = "BTCUSDT", ["positionIdx"] = context.HedgeMode ? 1 : 0, ["size"] = F(Math.Abs(position)),
                ["side"] = position > 0 ? "Buy" : position < 0 ? "Sell" : "", ["avgPrice"] = "100",
                ["unrealisedPnl"] = position == 0 ? "0" : "3", ["leverage"] = "2",
            }]));
        }
        if (request.Route == "/v5/position/closed-pnl")
        {
            lock (context.Gate) return Ok(List(context.ClosedPnl));
        }
        if (request.Route == "/v5/order/create")
        {
            var input = JsonNode.Parse(request.Body)!.AsObject();
            CreatePlan plan; string id;
            lock (context.Gate)
            {
                plan = context.NextCreate ?? new CreatePlan(); context.NextCreate = null;
                id = "fixture-exchange-" + (context.Orders.Count + 1).ToString(CultureInfo.InvariantCulture);
                var report = input.DeepClone().AsObject();
                report["orderId"] = id; report["orderStatus"] = input.ContainsKey("triggerPrice") ? "Untriggered" : "New";
                report["cumExecQty"] = "0"; report["leavesQty"] = input["qty"]!.GetValue<string>(); report["avgPrice"] = "0";
                report["createdTime"] = F(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); context.Stamp(report);
                context.Orders.Add(id, report);
                if (plan.Hold || plan.Ambiguous) context.Hidden.Add(id);
            }
            plan.Received.TrySetResult(id);
            if (plan.Hold) await plan.Release.Task.WaitAsync(ct).ConfigureAwait(false);
            if (plan.Ambiguous) return Unavailable();
            context.Reveal(id);
            return Ok(new JsonObject { ["orderId"] = id, ["orderLinkId"] = input["orderLinkId"]!.GetValue<string>() });
        }
        lock (context.Gate)
        {
            if (request.Route is "/v5/order/cancel" or "/v5/order/amend")
            {
                var input = JsonNode.Parse(request.Body)!.AsObject();
                string id = input["orderId"]?.GetValue<string>() ?? context.Orders.First(pair => pair.Value["orderLinkId"]!.GetValue<string>() == input["orderLinkId"]!.GetValue<string>()).Key;
                if (request.Route == "/v5/order/cancel")
                {
                    if (context.ConfirmCancelImmediately) context.ConfirmCancel(id);
                }
                else
                {
                    context.Amendments[id] = input;
                    if (context.ConfirmAmendImmediately) context.ConfirmAmend(id);
                }
                return Ok(new JsonObject { ["orderId"] = id });
            }
            if (request.Route is "/v5/order/realtime" or "/v5/order/history")
            {
                string id = request.Query("orderId"), link = request.Query("orderLinkId");
                bool lookup = id.Length > 0 || link.Length > 0;
                var selected = context.Orders.Where(pair => !context.Hidden.Contains(pair.Key) &&
                    (id.Length == 0 || pair.Key == id) && (link.Length == 0 || pair.Value["orderLinkId"]!.GetValue<string>() == link) &&
                    (lookup || pair.Value["orderStatus"]!.GetValue<string>() is not ("Filled" or "Cancelled" or "Rejected"))).Select(pair => pair.Value);
                return Ok(List(selected));
            }
            if (request.Route == "/v5/execution/list")
            {
                if (context.FailExecutions) return Unavailable();
                string id = request.Query("orderId");
                return Ok(List(context.Executions.Where(fill => id.Length == 0 || fill["orderId"]!.GetValue<string>() == id)));
            }
        }
        UnexpectedRoutes.Enqueue(request.Route);
        return (404, "{\"retCode\":10001,\"retMsg\":\"Unexpected fixture route\",\"result\":{}}");
    }
    public void Dispose() => Server.Dispose();
}

internal sealed class KvpProvidersOAuth : IDisposable
{
    public const string Access = "fixture-private-longbridge-access";
    public const string Refresh = "fixture-private-longbridge-refresh";
    public const string Renewed = "fixture-private-longbridge-renewed";
    public FakeHttpServer Server { get; } = new();
    private readonly object _gate = new();
    private string? _challenge;
    private int _tokenRequests, _refreshRequests;
    public int TokenRequests => Volatile.Read(ref _tokenRequests);
    public int RefreshRequests => Volatile.Read(ref _refreshRequests);
    public bool PkceVerified { get; private set; }
    public bool HoldToken { get; set; }
    public TaskCompletionSource TokenEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource TokenRelease { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public KvpProvidersOAuth() => Server.OnHttpAsync = Respond;
    public void ObserveAuthorization(Uri authorization)
    {
        var query = Parse(authorization.Query);
        if (query.GetValueOrDefault("code_challenge_method") != "S256" || query.GetValueOrDefault("state", "").Length < 32)
            throw new InvalidDataException("The actual authorization request omitted secure state/PKCE");
        lock (_gate) _challenge = query["code_challenge"];
    }
    public void HoldNextToken()
    {
        HoldToken = true;
        TokenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TokenRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public static Dictionary<string, string> Parse(string text) => text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair.Length == 1 ? "" : pair[1].Replace('+', ' ')), StringComparer.Ordinal);
    private async ValueTask<(int Status, string Body)> Respond(FakeRequest request, CancellationToken ct)
    {
        if (request.Route != "/oauth2/token") return (404, "{}");
        Interlocked.Increment(ref _tokenRequests);
        var form = Parse(request.Body);
        if (form.GetValueOrDefault("grant_type") == "refresh_token")
        {
            Interlocked.Increment(ref _refreshRequests);
            if (form.GetValueOrDefault("refresh_token") != Refresh) return (400, "{}");
            return (200, JsonSerializer.Serialize(new { access_token = Renewed, expires_in = 3600, token_type = "Bearer" }));
        }
        string verifier = form.GetValueOrDefault("code_verifier", "");
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate) PkceVerified = verifier.Length >= 43 && challenge == _challenge && form.GetValueOrDefault("code") == "fixture-code";
        if (!PkceVerified) return (400, "{}");
        if (HoldToken)
        {
            HoldToken = false; TokenEntered.TrySetResult();
            await TokenRelease.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        return (200, JsonSerializer.Serialize(new { access_token = Access, refresh_token = Refresh, expires_in = 45, token_type = "Bearer" }));
    }
    public void Dispose() { TokenRelease.TrySetResult(); Server.Dispose(); }
}

internal sealed class KvpProvidersLongbridgeTransport(DateTime start) : ILongbridgeTransport
{
    public event Action<byte, ReadOnlyMemory<byte>>? Push;
    public event Action<FeedStatus, string?>? Status;
    public ConcurrentQueue<LongbridgeCommand> Commands { get; } = new();
    public bool UnknownQuote { get; set; }
    private long Seconds => new DateTimeOffset(start).ToUnixTimeSeconds();
    public Task<byte[]> RequestAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Commands.Enqueue((LongbridgeCommand)command);
        return Task.FromResult((LongbridgeCommand)command switch
        {
            LongbridgeCommand.StaticInfo => Message(w => w.Bytes(1, Message(info => { info.Text(1, "AAPL.US"); info.Text(2, "Fixture local name"); info.Text(3, "Apple Inc."); info.Text(6, "NASDAQ"); info.Text(7, "USD"); info.Number(8, 100); }))),
            LongbridgeCommand.Quote => Message(w => w.Bytes(1, Message(q => { q.Text(1, "AAPL.US"); if (!UnknownQuote) { q.Text(2, "201.25"); q.Number(7, Seconds); q.Number(8, 1234); } }))),
            LongbridgeCommand.Trades => Message(w => { w.Text(1, "AAPL.US"); w.Bytes(2, Trade("201", 7, Seconds + 61, 2)); w.Bytes(2, Trade("200.50", 4, Seconds + 60, 0)); }),
            LongbridgeCommand.HistoryCandles => Message(w => { w.Text(1, "AAPL.US"); w.Bytes(2, Candle(Seconds, 0)); w.Bytes(2, Candle(Seconds + 60, 10)); }),
            LongbridgeCommand.Depth => Message(w => { w.Text(1, "AAPL.US"); w.Bytes(2, Message(level => { level.Number(1, 1); level.Text(2, "202.00"); level.Number(3, 20); })); w.Bytes(3, Message(level => { level.Number(1, 1); level.Text(2, "201.00"); level.Number(3, 15); })); }),
            LongbridgeCommand.Subscribe or LongbridgeCommand.Unsubscribe => [],
            _ => throw new InvalidDataException("Unexpected fake Longbridge command"),
        });
    }
    private static byte[] Candle(long time, long volume) => Message(w => { w.Text(1, "201"); w.Text(2, "200"); w.Text(3, "199"); w.Text(4, "202"); if (volume != 0) w.Number(5, volume); w.Number(7, time); });
    private static byte[] Trade(string price, long volume, long time, int direction) => Message(w => { w.Text(1, price); w.Number(2, volume); w.Number(3, time); w.Number(5, direction); });
    public void QuotePush() => Push?.Invoke((byte)LongbridgeCommand.PushQuote, Message(w => { w.Text(1, "AAPL.US"); w.Number(2, 50); w.Text(3, "202.10"); w.Number(7, Seconds + 120); w.Number(8, 5000); }));
    public void DepthPush(long sequence, params (int Position, string Price, long Size)[] asks) => Push?.Invoke((byte)LongbridgeCommand.PushDepth, Message(w =>
    {
        w.Text(1, "AAPL.US"); w.Number(2, sequence);
        foreach (var item in asks) w.Bytes(3, Message(level => { level.Number(1, item.Position); if (item.Price.Length > 0) level.Text(2, item.Price); level.Number(3, item.Size); }));
    }));
    public void Reconnect() => Status?.Invoke(FeedStatus.Connected, "Isolated quote transport reconnected");
    public void Disconnect() => Status?.Invoke(FeedStatus.Reconnecting, "Isolated quote transport interrupted");
    internal static byte[] Message(Action<Writer> build) { var writer = new Writer(); build(writer); return writer.Build(); }
    internal sealed class Writer
    {
        private readonly MemoryStream _bytes = new();
        private void Varint(ulong n) { while (n >= 128) { _bytes.WriteByte((byte)(n | 128)); n >>= 7; } _bytes.WriteByte((byte)n); }
        public void Number(int field, long value) { Varint((ulong)(field * 8)); Varint((ulong)value); }
        public void Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
        public void Bytes(int field, byte[] value) { Varint((ulong)(field * 8 + 2)); Varint((ulong)value.Length); _bytes.Write(value); }
        public byte[] Build() => _bytes.ToArray();
    }
    public void Dispose() { }
}

internal sealed class KvpProvidersVenue(KvpCandleSceneFeed inner, bool recentOnly = false) : IMarketDataFeed, IKlineHistory, IRecentTradeHistoryFeed, IDataScopeFeed
{
    private readonly List<(Instrument Instrument, IMarketDataSink Sink)> _listeners = new();
    public string Id => inner.Id;
    public string DisplayName => Id;
    public bool SupportsHistory => true;
    public bool IsLive => false;
    public DateTime Now => inner.Now;
    public bool RecentTradesOnly => recentOnly;
    public bool FailTrades { get; set; }
    public string CacheTag => inner.CacheTag;
    public string DataScopeKey { get; } = "kvp-provider-ui:" + Guid.NewGuid().ToString("N");
    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => inner.GetInstrumentsAsync(ct);
    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return FailTrades ? Task.FromException(new HttpRequestException("Isolated Bybit constituent unavailable")) : inner.LoadTradesAsync(instrument, fromUtc, toUtc, fromId, onBatch, progress, ct); }
    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => inner.FirstAvailableAsync(instrument, ct);
    public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice, Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => inner.LoadKlinesAsync(instrument, fromUtc, toUtc, minutes, endPrice, onBatch, progress, ct);
    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        _listeners.Add((instrument, sink)); return new Subscription(inner.Subscribe(instrument, sink), () => _listeners.Remove((instrument, sink)));
    }
    public void ReportStatus(FeedStatus status, string message)
    { foreach (var item in _listeners.ToArray()) item.Sink.OnStatus(item.Instrument, status, message); }
    private sealed class Subscription(IDisposable subscription, Action remove) : IDisposable
    { public void Dispose() { subscription.Dispose(); remove(); } }
    public void Dispose() { _listeners.Clear(); inner.Dispose(); }
}

internal sealed class KvpProvidersClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
