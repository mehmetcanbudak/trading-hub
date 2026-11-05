using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Feeds.Bybit;

// Protocol reference: KVP 50220ba91613161685f84384825d855cb4dbeb76, MIT + Commons Clause.
// The request acknowledgement is not a working-order or execution acknowledgement.
public enum BybitEnvironment { Demo, Testnet, Real }

/// <summary>Immutable credential context. Applications store both keys in their secret store, not this object in settings.</summary>
public sealed record BybitAccountSettings
{
    public BybitEnvironment Environment { get; init; } = BybitEnvironment.Demo;
    public string ApiKey { get; init; } = "";
    public string ApiSecret { get; init; } = "";
    public string? BaseUrl { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public bool IsReal => Environment == BybitEnvironment.Real;
    public bool HasKeys => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
    public string EnvironmentName => Environment switch { BybitEnvironment.Demo => "demo", BybitEnvironment.Testnet => "testnet", _ => "REAL" };
    public string RestBase => !string.IsNullOrWhiteSpace(BaseUrl) ? BaseUrl.Trim().TrimEnd('/') : Environment switch
    {
        BybitEnvironment.Demo => "https://api-demo.bybit.com",
        BybitEnvironment.Testnet => "https://api-testnet.bybit.com",
        _ => "https://api.bybit.com",
    };
    public string ContextIdentity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Environment}\n{RestBase}\n{ApiKey}\n{ApiSecret}"))).ToLowerInvariant();
}

public sealed record BybitWallet(decimal? Equity, decimal? Available, decimal? UnrealizedPnl, decimal? WalletBalance);
public sealed record BybitPosition(string Symbol, decimal Size, decimal AvgPrice, decimal UnrealizedPnl, decimal Leverage);
public sealed record BybitOpenOrder(string OrderId, string LinkId, string Symbol, bool Buy, bool Conditional, bool IsLimit,
    decimal Price, decimal TriggerPrice, decimal Quantity, decimal Executed, decimal Leaves, string Status, bool ReduceOnly,
    decimal AvgPrice = 0, long UpdatedTimeMs = 0, long CreatedTimeMs = 0)
{
    public bool Terminal => Status is "Filled" or "Cancelled" or "Canceled" or "Rejected" or "Deactivated" or "PartiallyFilledCanceled";
}
public sealed record BybitExecution(string Id, string OrderId, string LinkId, string Symbol, bool Buy, decimal Quantity,
    decimal Price, DateTime Time, decimal? Fee, string FeeCurrency, decimal? ClosedQuantity = null)
{
    public bool HasQuoteFee => Fee.HasValue && FeeCurrency == "USDT";
}
public sealed record BybitClosedPnl(string Symbol, string OrderId, decimal Value, DateTime CreatedAt, DateTime UpdatedAt, decimal ClosedQuantity);

/// <summary>Provider-reported USDT-contract metrics, distinct from round trips reconstructed without a complete opening basis.</summary>
public sealed record BybitAccountMetrics(decimal? OpenPnl, decimal? ReportedRealizedToday, bool HasCompleteOpeningBasis, string? Warning)
{
    public static BybitAccountMetrics From(BybitTradingSession session, TimeZoneInfo timeZone, DateTime utcNow)
    {
        DateTime Local(DateTime value) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), timeZone);
        DateTime today = Local(utcNow).Date;
        bool crossesDay = false;
        decimal reported = 0;
        Dictionary<(string Symbol, string OrderId), decimal>? reportedOrders = null;
        foreach (var record in session.ClosedPnl)
        {
            if (Local(record.UpdatedAt).Date != today) continue;
            crossesDay |= Local(record.CreatedAt).Date != today;
            reported += record.Value;
            (reportedOrders ??= new())[(record.Symbol, record.OrderId)] = record.ClosedQuantity;
        }
        bool missingCloseCoverage = session.HasUnreportedDailyClose(reportedOrders, timeZone, today);
        decimal? realized = session.ClosedPnlReady && !crossesDay && !missingCloseCoverage ? reported : null;
        decimal? open = session.PositionReady ? session.Positions.Sum(position => position.UnrealizedPnl) : null;
        return new(open, realized, false, crossesDay
            ? "A provider closed-PnL record spans trading days; today's realized result cannot be allocated exactly."
            : missingCloseCoverage
                ? "An execution has missing or unreported closed-PnL coverage; today's realized result remains unknown."
                : "Bounded execution history does not establish a complete opening basis; full round-trip analysis remains unavailable.");
    }
}

public sealed class BybitApiException(int code, string message) : InvalidOperationException($"Bybit rejected operation ({code}): {message}")
{
    public int Code { get; } = code;
}

/// <summary>The request may have executed. Never retry a placement under a new client identity.</summary>
public sealed class BybitUnknownException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Bybit v5 UTA, USDT-settled linear contracts, one-way mode only. Inject HTTP for fake protocols and explicit proxy policy.</summary>
public sealed class BybitAccountClient : IDisposable
{
    public const string Category = "linear";
    public const int ReceiveWindow = 10000;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _timeGate = new(1, 1);
    private long _offsetMs;
    private bool _timeSynced;
    public BybitAccountSettings Settings { get; }

    public BybitAccountClient(BybitAccountSettings settings, HttpClient? httpClient = null, TimeProvider? clock = null)
    {
        Settings = settings with { };
        _clock = clock ?? TimeProvider.System;
        _ownsHttp = httpClient == null;
        var endpoint = new Uri(Settings.RestBase);
        if ((endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address))) ||
            endpoint.UserInfo.Length > 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("Bybit requires an HTTPS origin without credentials, query or fragment; plain HTTP is limited to literal loopback fake endpoints.", nameof(settings));
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = settings.RequestTimeout };
    }

    public static string Sign(long timestamp, string apiKey, int receiveWindow, string payload, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}{apiKey}{receiveWindow}{payload}"))).ToLowerInvariant();

    public async Task SyncTimeAsync(CancellationToken ct)
    {
        await _timeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long before = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            using var response = await _http.GetAsync(Settings.RestBase + "/v5/market/time", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (!root.TryGetProperty("retCode", out var code) || code.GetInt32() != 0)
                throw new InvalidDataException("Bybit did not confirm server time.");
            long server = root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number
                ? time.GetInt64() : long.Parse(root.GetProperty("result").GetProperty("timeSecond").GetString()!, CultureInfo.InvariantCulture) * 1000;
            long after = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            Interlocked.Exchange(ref _offsetMs, server - (before + (after - before) / 2));
            _timeSynced = true;
        }
        finally { _timeGate.Release(); }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string query, JsonObject? body, CancellationToken ct, bool timeRetried = false, Func<string?>? beforeSend = null)
    {
        if (!Settings.HasKeys) throw new InvalidOperationException("Bybit API key and secret are missing (Connections).");
        bool mutation = method != HttpMethod.Get;
        if (!_timeSynced)
        {
            try { await SyncTimeAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (mutation) { throw new BybitApiException(-1, "Server time could not be confirmed; no signed order request was sent."); }
        }
        long timestamp = _clock.GetUtcNow().ToUnixTimeMilliseconds() + Interlocked.Read(ref _offsetMs);
        string json = body?.ToJsonString() ?? "";
        string payload = mutation ? json : query;
        using var request = new HttpRequestMessage(method, Settings.RestBase + path + (!mutation && query.Length > 0 ? "?" + query : ""));
        request.Headers.Add("X-BAPI-API-KEY", Settings.ApiKey);
        request.Headers.Add("X-BAPI-TIMESTAMP", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-BAPI-RECV-WINDOW", ReceiveWindow.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-BAPI-SIGN", Sign(timestamp, Settings.ApiKey, ReceiveWindow, payload, Settings.ApiSecret));
        if (mutation) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            // All asynchronous metadata/time work is finished. Recheck permission at the actual mutation transport seam.
            if (mutation && beforeSend?.Invoke() is { } denied) throw new BybitApiException(-1, denied);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (!root.TryGetProperty("retCode", out var codeElement) || !codeElement.TryGetInt32(out int code))
                throw new InvalidDataException("Bybit response has no authoritative result code.");
            string message = S(root, "retMsg");
            if (code == 10002 && !timeRetried)
            {
                // A documented timestamp rejection is definitive nonacceptance, unlike a lost response.
                await SyncTimeAsync(ct).ConfigureAwait(false);
                return await SendAsync(method, path, query, body, ct, true, beforeSend).ConfigureAwait(false);
            }
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout ||
                (mutation && code is 10000 or 10016 or 10019 or 20006 or 110072))
                throw new BybitUnknownException($"Bybit execution status unknown ({code}): {message}");
            if (code != 0) throw new BybitApiException(code, message);
            if (!response.IsSuccessStatusCode || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Bybit returned an incomplete success response.");
            return result.Clone();
        }
        catch (Exception ex) when (mutation && ex is not BybitApiException && ex is not BybitUnknownException)
        {
            throw new BybitUnknownException("Bybit request outcome is unknown; reconciling its exact client/order identity.", ex);
        }
    }

    private Task<JsonElement> GetAsync(string path, string query, CancellationToken ct) => SendAsync(HttpMethod.Get, path, query, null, ct);
    private Task<JsonElement> PostAsync(string path, JsonObject body, CancellationToken ct, Func<string?>? beforeSend = null) => SendAsync(HttpMethod.Post, path, "", body, ct, beforeSend: beforeSend);
    private static string E(string value) => Uri.EscapeDataString(value);
    internal static string Fmt(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
    internal static string S(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    internal static decimal? N(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        return text.Length == 0 ? null : decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
    internal static decimal D(JsonElement e, string key) => N(e, key) ?? 0;
    private static long L(JsonElement e, string key) => (long)(N(e, key) ?? 0);
    private static JsonElement List(JsonElement result) => result.GetProperty("list") is var list && list.ValueKind == JsonValueKind.Array
        ? list : throw new InvalidDataException("Bybit snapshot has no complete list.");

    public async Task ValidateAccountAsync(CancellationToken ct)
    {
        var info = await GetAsync("/v5/account/info", "", ct).ConfigureAwait(false);
        int mode = info.GetProperty("unifiedMarginStatus").GetInt32();
        if (mode is not (3 or 4 or 5 or 6)) throw new NotSupportedException("Bybit execution requires a unified trading account (UTA).");
    }

    public async Task<BybitWallet?> GetWalletAsync(CancellationToken ct)
    {
        var result = await GetAsync("/v5/account/wallet-balance", "accountType=UNIFIED", ct).ConfigureAwait(false);
        var list = List(result);
        if (list.GetArrayLength() == 0) return null;
        var wallet = list[0];
        return new(N(wallet, "totalEquity"), N(wallet, "totalAvailableBalance"), N(wallet, "totalPerpUPL"), N(wallet, "totalWalletBalance"));
    }

    private async Task<List<JsonElement>> PagesAsync(string path, string query, CancellationToken ct)
    {
        var rows = new List<JsonElement>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string cursor = "";
        do
        {
            var result = await GetAsync(path, query + (cursor.Length > 0 ? "&cursor=" + E(cursor) : ""), ct).ConfigureAwait(false);
            foreach (var row in List(result).EnumerateArray()) rows.Add(row.Clone());
            cursor = S(result, "nextPageCursor");
            if (cursor.Length > 0 && !cursors.Add(cursor)) throw new InvalidDataException("Bybit repeated a snapshot cursor; coverage is incomplete.");
        } while (cursor.Length > 0);
        return rows;
    }

    public async Task<IReadOnlyList<BybitPosition>> GetPositionsAsync(CancellationToken ct)
    {
        var rows = await PagesAsync("/v5/position/list", "category=linear&settleCoin=USDT&limit=200", ct).ConfigureAwait(false);
        var positions = new List<BybitPosition>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("positionIdx", out _)) throw new InvalidDataException("Bybit position mode is unknown.");
            if (L(row, "positionIdx") != 0) throw new NotSupportedException("Bybit hedge-mode positions are unsupported. Select one-way mode in Bybit; Trading Hub does not change it automatically.");
            string symbol = S(row, "symbol");
            if (symbol.Length == 0 || N(row, "size") is not { } size || size < 0) throw new InvalidDataException("Bybit returned an incomplete position.");
            string side = S(row, "side");
            if (size != 0 && side is not ("Buy" or "Sell")) throw new InvalidDataException("Bybit returned a position without its side.");
            decimal? average = N(row, "avgPrice"), unrealized = N(row, "unrealisedPnl");
            if (size != 0 && (average is not > 0 || !unrealized.HasValue))
                throw new InvalidDataException("Bybit nonflat position is missing its authoritative average price or unrealized PnL.");
            positions.Add(new(symbol, side == "Sell" ? -size : size, average ?? 0, unrealized ?? 0, D(row, "leverage")));
        }
        return positions;
    }

    private static BybitOpenOrder ParseOrder(JsonElement row)
    {
        string id = S(row, "orderId"), symbol = S(row, "symbol"), status = S(row, "orderStatus");
        if (id.Length == 0 || symbol.Length == 0 || status.Length == 0 || N(row, "qty") is not { } quantity || N(row, "cumExecQty") is not { } executed)
            throw new InvalidDataException("Bybit returned an incomplete order report.");
        if (S(row, "side") is not ("Buy" or "Sell") || S(row, "orderType") is not ("Market" or "Limit") || quantity <= 0 ||
            executed < 0 || executed > quantity || (status == "Filled" && executed != quantity))
            throw new InvalidDataException("Bybit returned an inconsistent order report.");
        if (!row.TryGetProperty("positionIdx", out _)) throw new InvalidDataException("Bybit order position mode is unknown.");
        if (L(row, "positionIdx") != 0) throw new NotSupportedException("Bybit hedge-mode orders are unsupported; one-way mode is required.");
        decimal trigger = D(row, "triggerPrice");
        return new(id, S(row, "orderLinkId"), symbol, S(row, "side") == "Buy", trigger > 0 || status == "Untriggered",
            S(row, "orderType") == "Limit", D(row, "price"), trigger, quantity, executed, N(row, "leavesQty") ?? Math.Max(0, quantity - executed), status,
            row.TryGetProperty("reduceOnly", out var ro) && ro.ValueKind == JsonValueKind.True, D(row, "avgPrice"), L(row, "updatedTime"), L(row, "createdTime"));
    }

    public async Task<IReadOnlyList<BybitOpenOrder>> GetOpenOrdersAsync(CancellationToken ct) =>
        (await PagesAsync("/v5/order/realtime", "category=linear&settleCoin=USDT&openOnly=0&limit=50", ct).ConfigureAwait(false)).Select(ParseOrder).ToArray();

    public async Task<BybitOpenOrder?> GetOrderAsync(string symbol, string orderId, string linkId, CancellationToken ct)
    {
        string query = "category=linear&symbol=" + E(symbol) + (orderId.Length > 0 ? "&orderId=" + E(orderId) : "&orderLinkId=" + E(linkId)) + "&limit=50";
        foreach (string path in new[] { "/v5/order/realtime", "/v5/order/history" })
        {
            var result = await GetAsync(path, query, ct).ConfigureAwait(false);
            foreach (var row in List(result).EnumerateArray())
            {
                var order = ParseOrder(row);
                if (order.Symbol == symbol && (orderId.Length > 0 ? order.OrderId == orderId : order.LinkId == linkId)) return order;
            }
        }
        return null; // Absence is unknown, never cancellation proof.
    }

    public async Task<IReadOnlyList<BybitExecution>> GetExecutionsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct, string? orderId = null, string? linkId = null)
    {
        long from = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long to = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        string identity = !string.IsNullOrEmpty(orderId) ? "&orderId=" + E(orderId) : !string.IsNullOrEmpty(linkId) ? "&orderLinkId=" + E(linkId) : "";
        var rows = await PagesAsync("/v5/execution/list", $"category=linear&settleCoin=USDT&execType=Trade&startTime={from}&endTime={to}&limit=100" + identity, ct).ConfigureAwait(false);
        var fills = new List<BybitExecution>(rows.Count);
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(orderId) && S(row, "orderId") != orderId) continue;
            if (string.IsNullOrEmpty(orderId) && !string.IsNullOrEmpty(linkId) && S(row, "orderLinkId") != linkId) continue;
            if (S(row, "execType") is { Length: > 0 } type && type != "Trade") continue;
            string id = S(row, "execId"), symbol = S(row, "symbol");
            if (id.Length == 0 || symbol.Length == 0 || N(row, "execPrice") is not { } price || N(row, "execQty") is not { } qty || N(row, "execTime") is not { } time)
                throw new InvalidDataException("Bybit returned an incomplete execution.");
            if (S(row, "orderId").Length == 0 || S(row, "side") is not ("Buy" or "Sell") || price <= 0 || qty <= 0)
                throw new InvalidDataException("Bybit returned an execution without its identity, side, price or quantity.");
            decimal? closed = N(row, "closedSize");
            if (closed is { } closedQuantity && (closedQuantity < 0 || closedQuantity > qty))
                throw new InvalidDataException("Bybit returned an inconsistent executed closing quantity.");
            fills.Add(new(id, S(row, "orderId"), S(row, "orderLinkId"), symbol, S(row, "side") == "Buy", qty, price,
                DateTimeOffset.FromUnixTimeMilliseconds((long)time).UtcDateTime, N(row, "execFee"), S(row, "feeCurrency"), closed));
        }
        return fills.OrderBy(f => f.Time).ThenBy(f => f.OrderId, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<BybitClosedPnl>> GetClosedPnlAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        long from = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long to = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var rows = await PagesAsync("/v5/position/closed-pnl", $"category=linear&startTime={from}&endTime={to}&limit=100", ct).ConfigureAwait(false);
        var records = new Dictionary<string, BybitClosedPnl>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string symbol = S(row, "symbol"), orderId = S(row, "orderId");
            if (symbol.Length == 0 || orderId.Length == 0 || N(row, "closedPnl") is not { } pnl ||
                N(row, "createdTime") is not { } created || N(row, "updatedTime") is not { } updated || N(row, "closedSize") is not { } quantity)
                throw new InvalidDataException("Bybit closed-PnL report is incomplete.");
            var record = new BybitClosedPnl(symbol, orderId, pnl, DateTimeOffset.FromUnixTimeMilliseconds((long)created).UtcDateTime,
                DateTimeOffset.FromUnixTimeMilliseconds((long)updated).UtcDateTime, quantity);
            string key = symbol + "|" + orderId;
            if (!records.TryGetValue(key, out var prior) || record.UpdatedAt >= prior.UpdatedAt) records[key] = record;
        }
        return records.Values.OrderBy(record => record.UpdatedAt).ToArray();
    }

    public async Task<string> GetSettlementAsync(string symbol, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Settings.RestBase + "/v5/market/instruments-info?category=linear&symbol=" + E(symbol), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.GetProperty("retCode").GetInt32() != 0) throw new InvalidDataException("Bybit settlement currency lookup failed.");
        foreach (var row in List(doc.RootElement.GetProperty("result")).EnumerateArray())
            if (S(row, "symbol") == symbol && S(row, "settleCoin") is { Length: > 0 } settlement) return settlement;
        throw new InvalidDataException("Bybit did not confirm the closed-PnL instrument settlement currency.");
    }

    public async Task<string> GetAccountIdentityAsync(CancellationToken ct)
    {
        var identity = await GetAsync("/v5/user/query-api", "", ct).ConfigureAwait(false);
        decimal uid = N(identity, "userID") ?? 0;
        if (uid <= 0) uid = N(identity, "userIDInt64") ?? 0;
        if (uid <= 0 || uid != decimal.Truncate(uid)) throw new InvalidDataException("Bybit did not confirm the authenticated account UID.");
        return Settings.Environment.ToString().ToLowerInvariant() + "-" + uid.ToString("0", CultureInfo.InvariantCulture);
    }

    private async Task<JsonElement> InstrumentRecordAsync(string symbol, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Settings.RestBase + "/v5/market/instruments-info?category=linear&symbol=" + E(symbol), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (!root.TryGetProperty("retCode", out var code) || code.GetInt32() != 0)
            throw new BybitApiException(-1, "Instrument limits could not be confirmed; no order was sent.");
        foreach (var instrument in List(root.GetProperty("result")).EnumerateArray())
        {
            if (S(instrument, "symbol") != symbol) continue;
            if (S(instrument, "settleCoin") != "USDT" || S(instrument, "quoteCoin") != "USDT" ||
                S(instrument, "contractType") != "LinearPerpetual" || S(instrument, "status") != "Trading")
                throw new BybitApiException(-1, "Only trading USDT-settled linear perpetuals are supported.");
            return instrument.Clone();
        }
        throw new BybitApiException(-1, "Bybit did not return the requested instrument; no order was sent.");
    }

    public async Task<Instrument> GetInstrumentAsync(string symbol, CancellationToken ct)
    {
        var record = await InstrumentRecordAsync(symbol, ct).ConfigureAwait(false);
        decimal tick = N(record.GetProperty("priceFilter"), "tickSize") ?? 0;
        decimal step = N(record.GetProperty("lotSizeFilter"), "qtyStep") ?? 0;
        if (tick <= 0 || step <= 0) throw new InvalidDataException("Bybit instrument tick/quantity step is unavailable.");
        return new Instrument { Symbol = symbol, FeedId = Settings.Environment == BybitEnvironment.Testnet ? BybitFeed.TestnetId : BybitFeed.LinearId,
            Exchange = "Bybit", TickSize = tick, VolumeStep = step, BaseAsset = S(record, "baseCoin"), QuoteAsset = "USDT" };
    }

    private async Task ValidateOrderLimitsAsync(string symbol, OrderTypes type, decimal quantity, decimal price, CancellationToken ct)
    {
        try
        {
            var instrument = await InstrumentRecordAsync(symbol, ct).ConfigureAwait(false);
            var lots = instrument.GetProperty("lotSizeFilter");
            decimal minimum = N(lots, "minOrderQty") ?? throw new InvalidDataException("Bybit minimum order quantity is unknown.");
            decimal maximum = N(lots, type == OrderTypes.Limit ? "maxOrderQty" : "maxMktOrderQty") ??
                throw new InvalidDataException("Bybit maximum order quantity is unknown.");
            decimal step = N(lots, "qtyStep") ?? throw new InvalidDataException("Bybit quantity step is unknown.");
            if (step <= 0 || quantity < minimum || quantity > maximum || quantity % step != 0)
                throw new BybitApiException(-1, "Quantity is outside the confirmed Bybit order limits; split-child orders are not submitted.");
            if (type != OrderTypes.Market)
            {
                var prices = instrument.GetProperty("priceFilter");
                decimal tick = N(prices, "tickSize") ?? throw new InvalidDataException("Bybit price step is unknown.");
                decimal min = N(prices, "minPrice") ?? throw new InvalidDataException("Bybit minimum price is unknown.");
                decimal max = N(prices, "maxPrice") ?? throw new InvalidDataException("Bybit maximum price is unknown.");
                if (tick <= 0 || price < min || price > max || price % tick != 0)
                    throw new BybitApiException(-1, "Price is outside the confirmed Bybit instrument limits.");
            }
        }
        catch (Exception ex) when (ex is not BybitApiException)
        {
            throw new BybitApiException(-1, "Instrument limits are unavailable; no order mutation was sent.");
        }
    }

    public async Task<string> PlaceOrderAsync(string symbol, OrderDirections direction, OrderTypes type, decimal quantity, decimal price,
        bool reduceOnly, string linkId, decimal lastPrice, CancellationToken ct, Func<string?>? beforeSend = null)
    {
        if (quantity <= 0 || linkId.Length is 0 or > 36) throw new ArgumentException("A positive quantity and a stable client identity of at most 36 characters are required.");
        var body = new JsonObject { ["category"] = Category, ["symbol"] = symbol, ["side"] = direction == OrderDirections.Buy ? "Buy" : "Sell",
            ["qty"] = Fmt(quantity), ["positionIdx"] = 0, ["orderLinkId"] = linkId, ["reduceOnly"] = reduceOnly };
        switch (type)
        {
            case OrderTypes.Market: body["orderType"] = "Market"; break;
            case OrderTypes.Limit:
                body["orderType"] = "Limit"; body["price"] = Fmt(price); body["timeInForce"] = "GTC"; break;
            case OrderTypes.Stop:
                if (lastPrice <= 0 || price <= 0 || price == lastPrice) throw new InvalidOperationException("A current venue last price is required to determine the Bybit stop trigger direction.");
                body["orderType"] = "Market"; body["triggerPrice"] = Fmt(price); body["triggerBy"] = "LastPrice";
                body["triggerDirection"] = price > lastPrice ? 1 : 2;
                break;
            default: throw new NotSupportedException("Bybit supports market, limit and stop-market orders here.");
        }
        await ValidateOrderLimitsAsync(symbol, type, quantity, price, ct).ConfigureAwait(false);
        var result = await PostAsync("/v5/order/create", body, ct, beforeSend).ConfigureAwait(false);
        string id = S(result, "orderId");
        if (id.Length == 0 || (S(result, "orderLinkId") is { Length: > 0 } returned && returned != linkId))
            throw new BybitUnknownException("Bybit placement acknowledgement did not identify the submitted order.");
        return id;
    }

    public async Task AmendOrderAsync(string symbol, string orderId, decimal quantity, decimal price, bool conditional, CancellationToken ct, Func<string?>? beforeSend = null)
    {
        await ValidateOrderLimitsAsync(symbol, conditional ? OrderTypes.Stop : OrderTypes.Limit, quantity, price, ct).ConfigureAwait(false);
        var body = new JsonObject { ["category"] = Category, ["symbol"] = symbol, ["orderId"] = orderId, ["qty"] = Fmt(quantity),
            [conditional ? "triggerPrice" : "price"] = Fmt(price) };
        await PostAsync("/v5/order/amend", body, ct, beforeSend).ConfigureAwait(false);
    }
    public async Task CancelOrderAsync(string symbol, string orderId, string linkId, CancellationToken ct)
    {
        var body = new JsonObject { ["category"] = Category, ["symbol"] = symbol };
        if (orderId.Length > 0) body["orderId"] = orderId; else body["orderLinkId"] = linkId;
        await PostAsync("/v5/order/cancel", body, ct).ConfigureAwait(false);
    }
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        await SyncTimeAsync(ct).ConfigureAwait(false);
        if (!Settings.HasKeys) return (true, "Bybit public data only; account credentials are not configured.");
        await ValidateAccountAsync(ct).ConfigureAwait(false);
        var wallet = await GetWalletAsync(ct).ConfigureAwait(false);
        return (true, $"Bybit UTA account connected ({Settings.EnvironmentName}); equity: {(wallet?.Equity is { } equity ? equity.ToString("0.##", CultureInfo.InvariantCulture) + " USD" : "unavailable")}. One-way mode is checked on position/order snapshots.");
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
