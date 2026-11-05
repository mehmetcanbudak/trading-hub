using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingHub.Tests")]

namespace TradingHub.Feeds.Binance;

/// <summary>Configuración de una cuenta de Binance (las claves se guardan en el llavero de macOS).</summary>
public sealed class BinanceAccountSettings
{
    public bool Futures { get; set; } = true;
    public bool Testnet { get; set; } = true;
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";

    /// <summary>URL REST propia (vacío = la estándar según mercado y testnet).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Time allowed for each REST request. Not part of the saved connection; tests shorten it.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Time between refreshes of the open orders and the position. Not part of the saved connection; tests shorten it.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public string RestBase => !string.IsNullOrWhiteSpace(BaseUrl)
        ? BaseUrl!.TrimEnd('/')
        : (Futures, Testnet) switch
        {
            (true, true) => "https://testnet.binancefuture.com",
            (true, false) => "https://fapi.binance.com",
            (false, true) => "https://testnet.binance.vision",
            _ => "https://api.binance.com",
        };

    public bool HasKeys => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}

public sealed record BinanceBalance(string Asset, decimal Wallet, decimal Available, decimal Unrealized);

public sealed record BinancePosition(string Symbol, decimal Quantity, decimal EntryPrice, decimal UnrealizedPnl, decimal Leverage);

/// <summary>
/// An order as Binance reports it: in the open-order list, when looked up, and in the answers to new and cancel requests
/// (fields an answer leaves out are empty or 0).
/// </summary>
public sealed record BinanceOpenOrder(long OrderId, string Symbol, string Side, string Type, decimal Price, decimal StopPrice, decimal Quantity, decimal Executed, string Status, bool ReduceOnly = false, string ClientOrderId = "");

/// <summary>
/// A USD-M futures conditional (algo) order as Binance reports it: in the open algo-order list, when looked up, and in the
/// answer to a new one (fields an answer leaves out are empty or 0). Once it triggers, <see cref="ActualOrderId"/> is the
/// regular order it placed (0 before), and a lookup gives in <see cref="ActualQuantity"/> what that order executed.
/// <see cref="ReduceOnly"/> is also set for a close-position order.
/// </summary>
public sealed record BinanceAlgoOrder(long AlgoId, string Symbol, string Side, string Type, decimal Price, decimal TriggerPrice, decimal Quantity, string Status, long ActualOrderId, decimal ActualQuantity, bool ReduceOnly = false, string ClientAlgoId = "");

/// <summary>A native Spot/USD-M trade. Missing commission fields are unknown, not zero.</summary>
internal sealed record BinanceTrade(long Id, long OrderId, string Symbol, decimal Quantity, decimal Price,
    long Time, bool Buy, decimal? Commission, string? CommissionAsset);

/// <summary>The request may have executed; only an exact order query can resolve it.</summary>
internal sealed class BinanceUnknownException(string message) : IOException(message);

/// <summary>Binance refused a private request; <see cref="Code"/> is its error code (0 if the answer had none).</summary>
internal sealed class BinanceRejectedException : InvalidOperationException
{
    public BinanceRejectedException(string message, int code) : base(message) => Code = code;

    public int Code { get; }
}

/// <summary>
/// API privada de Binance (firmada con HMAC-SHA256): saldos, posiciones y órdenes.
/// Funciona contra el testnet o contra la cuenta real según la configuración.
/// </summary>
public sealed class BinanceAccountClient : IDisposable
{
    private readonly BinanceAccountSettings _s;
    private readonly HttpClient _http;
    private long _timeOffsetMs;

    public BinanceAccountClient(BinanceAccountSettings settings)
    {
        _s = settings;
        _http = new HttpClient { Timeout = settings.RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingHub/0.2");
        if (settings.HasKeys) _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", settings.ApiKey);
    }

    private string P(string futuresPath, string spotPath) => _s.Futures ? futuresPath : spotPath;

    /// <summary>Firma una query con la clave secreta (HMAC-SHA256 en hexadecimal).</summary>
    public static string Sign(string query, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(query))).ToLowerInvariant();
    }

    public async Task SyncTimeAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync(_s.RestBase + P("/fapi/v1/time", "/api/v3/time"), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        long server = doc.RootElement.GetProperty("serverTime").GetInt64();
        _timeOffsetMs = server - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private async Task<JsonDocument> SignedAsync(HttpMethod method, string path, string query, CancellationToken ct)
    {
        if (!_s.HasKeys) throw new InvalidOperationException("Binance API key and secret are missing (Connections).");
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;
        string q = (string.IsNullOrEmpty(query) ? "" : query + "&") + $"recvWindow=5000&timestamp={ts}";
        string url = $"{_s.RestBase}{path}?{q}&signature={Sign(q, _s.ApiSecret)}";
        using var req = new HttpRequestMessage(method, url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = body;
            int code = 0;
            try
            {
                using var e = JsonDocument.Parse(body);
                if (e.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n)) code = n;
                msg = e.RootElement.GetProperty("msg").GetString() ?? body;
            }
            catch
            {
                // cuerpo no JSON
            }

            // An unreadable gateway response is not rejection proof. Spot 5xx, -1006/-1007 and
            // futures' unknown 503 variant can mean accepted exposure.
            bool definite503 = _s.Futures && (code == -1008 || ((int)resp.StatusCode == 503 &&
                msg is "Service Unavailable." or "Internal error; unable to process your request. Please try again."));
            if (code is -1006 or -1007 || (!definite503 && (code is -1000 or -1001)) || (int)resp.StatusCode == 408 ||
                ((int)resp.StatusCode >= 500 && !definite503) || (code == 0 && !definite503))
                throw new BinanceUnknownException($"Binance execution status unknown ({(int)resp.StatusCode}): {msg}");
            throw new BinanceRejectedException($"Binance rejected operation ({(int)resp.StatusCode}): {msg}", code);
        }

        return JsonDocument.Parse(body);
    }

    private static decimal D(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? decimal.Parse(v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture) : 0m;

    // Algo-order answers write fields that do not apply as empty strings (the documented examples: "activatePrice": "").
    private static decimal DecimalOrZero(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return 0m;
        string s = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        return s.Length == 0 ? 0m : decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>An id Binance may write as a number or as a string; empty (none yet) or missing is 0.</summary>
    private static long IdOrZero(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static BinanceOpenOrder ParseOrder(JsonElement o, string symbol) => new(
        o.GetProperty("orderId").GetInt64(), S(o, "symbol") is { Length: > 0 } nativeSymbol ? nativeSymbol : symbol, S(o, "side"), S(o, "type"),
        D(o, "price"), D(o, "stopPrice"), D(o, "origQty"), D(o, "executedQty"), S(o, "status"),
        o.TryGetProperty("reduceOnly", out var ro) && ro.ValueKind == JsonValueKind.True, S(o, "clientOrderId"));

    private static BinanceAlgoOrder ParseAlgoOrder(JsonElement a, string symbol) => new(
        a.GetProperty("algoId").GetInt64(), S(a, "symbol") is { Length: > 0 } nativeSymbol ? nativeSymbol : symbol, S(a, "side"), S(a, "orderType"),
        DecimalOrZero(a, "price"), DecimalOrZero(a, "triggerPrice"), DecimalOrZero(a, "quantity"), S(a, "algoStatus"),
        IdOrZero(a, "actualOrderId"), DecimalOrZero(a, "actualQty"), Flag(a, "reduceOnly") || Flag(a, "closePosition"), S(a, "clientAlgoId"));

    private static string Fmt(decimal v) => v.ToString("0.############", CultureInfo.InvariantCulture);
    internal static decimal NormalizeWireNumber(decimal value) => decimal.Parse(Fmt(value), CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<BinanceBalance>> GetBalancesAsync(CancellationToken ct)
    {
        var list = new List<BinanceBalance>();
        if (_s.Futures)
        {
            using var doc = await SignedAsync(HttpMethod.Get, "/fapi/v2/balance", "", ct).ConfigureAwait(false);
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                var w = D(a, "balance");
                if (w != 0) list.Add(new BinanceBalance(a.GetProperty("asset").GetString()!, w, D(a, "availableBalance"), D(a, "crossUnPnl")));
            }
        }
        else
        {
            using var doc = await SignedAsync(HttpMethod.Get, "/api/v3/account", "omitZeroBalances=true", ct).ConfigureAwait(false);
            foreach (var b in doc.RootElement.GetProperty("balances").EnumerateArray())
            {
                decimal free = decimal.Parse(b.GetProperty("free").GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
                decimal locked = decimal.Parse(b.GetProperty("locked").GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (free + locked > 0) list.Add(new BinanceBalance(b.GetProperty("asset").GetString()!, free + locked, free, 0));
            }
        }

        return list;
    }

    /// <summary>Resolves missing instrument metadata from Binance, never by splitting a symbol or guessing its quote asset.</summary>
    internal async Task<string> GetSpotBaseAssetAsync(string symbol, CancellationToken ct)
    {
        string json = await _http.GetStringAsync($"{_s.RestBase}/api/v3/exchangeInfo?symbol={Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        foreach (var s in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (S(s, "symbol") == symbol && S(s, "baseAsset") is { Length: > 0 } asset) return asset;
        }

        throw new InvalidOperationException($"Binance did not report a base asset for {symbol}.");
    }

    internal async Task<string?> GetQuoteAssetAsync(string symbol, CancellationToken ct)
    {
        string json = await _http.GetStringAsync($"{_s.RestBase}{P("/fapi/v1/exchangeInfo", "/api/v3/exchangeInfo")}?symbol={Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.GetProperty("symbols").EnumerateArray())
            if (S(item, "symbol") == symbol && S(item, "quoteAsset") is { Length: > 0 } quote) return quote;
        return null;
    }

    /// <summary>Inclusive native-ID pagination, without incompatible time filters; both venues cap a page at 1000.</summary>
    internal async Task<IReadOnlyList<BinanceTrade>> GetTradesAsync(string symbol, long fromId, CancellationToken ct)
    {
        using var doc = await SignedAsync(HttpMethod.Get, P("/fapi/v1/userTrades", "/api/v3/myTrades"),
            $"symbol={Uri.EscapeDataString(symbol)}&fromId={fromId.ToString(CultureInfo.InvariantCulture)}&limit=1000", ct).ConfigureAwait(false);
        var result = new List<BinanceTrade>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            long id = IdOrZero(item, "id"), orderId = IdOrZero(item, "orderId"), time = IdOrZero(item, "time");
            string nativeSymbol = S(item, "symbol");
            decimal quantity = D(item, "qty"), price = D(item, "price");
            bool hasSide = item.TryGetProperty(_s.Futures ? "buyer" : "isBuyer", out var side) &&
                side.ValueKind is JsonValueKind.True or JsonValueKind.False;
            if (!item.TryGetProperty("id", out _) || id < 0 || orderId <= 0 || time <= 0 || nativeSymbol != symbol ||
                quantity <= 0 || price <= 0 || !hasSide)
                throw new InvalidDataException("Binance trade history contains an incomplete native execution.");
            decimal? commission = item.TryGetProperty("commission", out var fee) &&
                fee.ValueKind is JsonValueKind.Number or JsonValueKind.String && fee.ToString().Length > 0
                ? D(item, "commission") : null;
            string asset = S(item, "commissionAsset");
            result.Add(new(id, orderId, nativeSymbol, quantity, price, time, side.GetBoolean(), commission,
                string.IsNullOrWhiteSpace(asset) ? null : asset));
        }
        return result;
    }

    public async Task<IReadOnlyList<BinancePosition>> GetPositionsAsync(CancellationToken ct)
    {
        var list = new List<BinancePosition>();
        if (!_s.Futures) return list;
        using var doc = await SignedAsync(HttpMethod.Get, "/fapi/v2/positionRisk", "", ct).ConfigureAwait(false);
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var qty = D(p, "positionAmt");
            if (qty != 0) list.Add(new BinancePosition(p.GetProperty("symbol").GetString()!, qty, D(p, "entryPrice"), D(p, "unRealizedProfit"), D(p, "leverage")));
        }

        return list;
    }

    public async Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string symbol, CancellationToken ct)
    {
        using var doc = await SignedAsync(HttpMethod.Get, P("/fapi/v1/openOrders", "/api/v3/openOrders"), $"symbol={symbol}", ct).ConfigureAwait(false);
        return doc.RootElement.EnumerateArray().Select(o => ParseOrder(o, symbol)).ToList();
    }

    /// <summary>
    /// One order in any state (open, filled, canceled, expired…), signed like the other private calls. Null if Binance
    /// no longer has it: it drops canceled or expired orders without fills after a few days.
    /// </summary>
    public async Task<BinanceOpenOrder?> GetOrderAsync(string symbol, long orderId, CancellationToken ct)
    {
        try
        {
            using var doc = await SignedAsync(HttpMethod.Get, P("/fapi/v1/order", "/api/v3/order"), $"symbol={symbol}&orderId={orderId}", ct).ConfigureAwait(false);
            return ParseOrder(doc.RootElement, symbol);
        }
        catch (BinanceRejectedException ex) when (ex.Code == -2013)
        {
            return null; // "Order does not exist."
        }
    }
    internal async Task<BinanceOpenOrder?> GetOrderByClientAsync(string symbol, string clientId, CancellationToken ct)
    {
        try
        {
            using var doc = await SignedAsync(HttpMethod.Get, P("/fapi/v1/order", "/api/v3/order"),
                $"symbol={symbol}&origClientOrderId={Uri.EscapeDataString(clientId)}", ct).ConfigureAwait(false);
            return ParseOrder(doc.RootElement, symbol);
        }
        catch (BinanceRejectedException ex) when (ex.Code == -2013) { return null; }
    }

    internal async Task<BinanceAlgoOrder?> GetAlgoByClientAsync(string symbol, string clientId, CancellationToken ct)
    {
        try
        {
            using var doc = await SignedAsync(HttpMethod.Get, "/fapi/v1/algoOrder",
                $"clientAlgoId={Uri.EscapeDataString(clientId)}", ct).ConfigureAwait(false);
            // This endpoint is client-ID scoped, not symbol scoped: recovery needs the returned route itself.
            return ParseAlgoOrder(doc.RootElement, "");
        }
        catch (BinanceRejectedException ex) when (ex.Code == -2013) { return null; }
    }


    /// <summary>
    /// Sends an order (type: MARKET or LIMIT, or STOP_LOSS in spot) and returns Binance's answer: always the order id;
    /// status and quantities unless the answer is a bare acknowledgement (spot stops). USD-M futures stops are conditional
    /// orders, sent with <see cref="PlaceAlgoOrderAsync"/>.
    /// </summary>
    public async Task<BinanceOpenOrder> PlaceOrderAsync(string symbol, OrderDirections dir, OrderTypes type, decimal qty, decimal price, bool reduceOnly, CancellationToken ct, string? clientId = null)
    {
        var sb = new StringBuilder($"symbol={symbol}&side={(dir == OrderDirections.Buy ? "BUY" : "SELL")}&quantity={Fmt(qty)}");
        if (clientId != null) sb.Append("&newClientOrderId=").Append(Uri.EscapeDataString(clientId));
        switch (type)
        {
            case OrderTypes.Market:
                sb.Append("&type=MARKET");
                break;
            case OrderTypes.Limit:
                sb.Append($"&type=LIMIT&timeInForce=GTC&price={Fmt(price)}");
                break;
            case OrderTypes.Stop when !_s.Futures:
                sb.Append($"&type=STOP_LOSS&stopPrice={Fmt(price)}");
                break;
            case OrderTypes.Stop:
                // since 2025-12-09 Binance refuses them on /fapi/v1/order (error -4120)
                throw new NotSupportedException("USD-M futures stops are conditional orders: send them with PlaceAlgoOrderAsync");
            default:
                throw new NotSupportedException("Unsupported order type");
        }

        if (reduceOnly && _s.Futures) sb.Append("&reduceOnly=true");
        using var doc = await SignedAsync(HttpMethod.Post, P("/fapi/v1/order", "/api/v3/order"), sb.ToString(), ct).ConfigureAwait(false);
        return ParseOrder(doc.RootElement, symbol);
    }

    /// <summary>Cancels an order and returns its final state as Binance answers it (status and executed quantity).</summary>
    public async Task<BinanceOpenOrder> CancelOrderAsync(string symbol, long orderId, CancellationToken ct)
    {
        using var doc = await SignedAsync(HttpMethod.Delete, P("/fapi/v1/order", "/api/v3/order"), $"symbol={symbol}&orderId={orderId}", ct).ConfigureAwait(false);
        return ParseOrder(doc.RootElement, symbol);
    }

    public async Task CancelAllAsync(string symbol, CancellationToken ct)
    {
        using var _ = await SignedAsync(HttpMethod.Delete, P("/fapi/v1/allOpenOrders", "/api/v3/openOrders"), $"symbol={symbol}", ct).ConfigureAwait(false);
    }

    /// <summary>Working conditional (algo) orders of a USD-M futures symbol (GET /fapi/v1/openAlgoOrders).</summary>
    public async Task<IReadOnlyList<BinanceAlgoOrder>> GetOpenAlgoOrdersAsync(string symbol, CancellationToken ct)
    {
        using var doc = await SignedAsync(HttpMethod.Get, "/fapi/v1/openAlgoOrders", $"symbol={symbol}", ct).ConfigureAwait(false);
        return doc.RootElement.EnumerateArray().Select(a => ParseAlgoOrder(a, symbol)).ToList();
    }

    /// <summary>
    /// One conditional order in any state (GET /fapi/v1/algoOrder by its algo id), with the order it placed once it
    /// triggered. Null if Binance answers that it does not exist (-2013): canceled or expired ones without fills are
    /// dropped after 3 days.
    /// </summary>
    public async Task<BinanceAlgoOrder?> GetAlgoOrderAsync(string symbol, long algoId, CancellationToken ct)
    {
        try
        {
            using var doc = await SignedAsync(HttpMethod.Get, "/fapi/v1/algoOrder", $"algoId={algoId}", ct).ConfigureAwait(false);
            return ParseAlgoOrder(doc.RootElement, symbol);
        }
        catch (BinanceRejectedException ex) when (ex.Code == -2013)
        {
            return null; // "Order does not exist."
        }
    }

    /// <summary>
    /// Sends a USD-M futures stop as a conditional order through the Algo Order API (POST /fapi/v1/algoOrder,
    /// algoType=CONDITIONAL, type STOP_MARKET): Binance sends a market order once the price reaches
    /// <paramref name="triggerPrice"/>, by its default working type (contract price). Returns Binance's answer.
    /// </summary>
    public async Task<BinanceAlgoOrder> PlaceAlgoOrderAsync(string symbol, OrderDirections dir, decimal qty, decimal triggerPrice, bool reduceOnly, CancellationToken ct, string? clientId = null)
    {
        if (!_s.Futures) throw new NotSupportedException("Conditional (algo) orders exist only in USD-M futures");
        string query = $"algoType=CONDITIONAL&symbol={symbol}&side={(dir == OrderDirections.Buy ? "BUY" : "SELL")}&type=STOP_MARKET&quantity={Fmt(qty)}&triggerPrice={Fmt(triggerPrice)}";
        if (clientId != null) query += "&clientAlgoId=" + Uri.EscapeDataString(clientId);
        if (reduceOnly) query += "&reduceOnly=true";
        using var doc = await SignedAsync(HttpMethod.Post, "/fapi/v1/algoOrder", query, ct).ConfigureAwait(false);
        return ParseAlgoOrder(doc.RootElement, symbol);
    }

    /// <summary>
    /// Cancels a conditional order that has not triggered (DELETE /fapi/v1/algoOrder by its algo id). Binance's answer
    /// carries no state; one whose code is not 200 counts as refused.
    /// </summary>
    public async Task CancelAlgoOrderAsync(long algoId, CancellationToken ct)
    {
        using var doc = await SignedAsync(HttpMethod.Delete, "/fapi/v1/algoOrder", $"algoId={algoId}", ct).ConfigureAwait(false);
        EnsureSucceeded(doc.RootElement);
    }

    /// <summary>Cancels every conditional order of a USD-M futures symbol that has not triggered (DELETE /fapi/v1/algoOpenOrders).</summary>
    public async Task CancelAllAlgoOrdersAsync(string symbol, CancellationToken ct)
    {
        using var doc = await SignedAsync(HttpMethod.Delete, "/fapi/v1/algoOpenOrders", $"symbol={symbol}", ct).ConfigureAwait(false);
        EnsureSucceeded(doc.RootElement);
    }

    /// <summary>Refuses an answer that carries a code other than 200, which Binance writes as a number or as a string.</summary>
    private static void EnsureSucceeded(JsonElement answer)
    {
        if (!answer.TryGetProperty("code", out var c)) return;
        string text = c.ValueKind == JsonValueKind.String ? c.GetString()! : c.GetRawText();
        if (text == "200") return;
        int code = int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 0;
        throw new BinanceRejectedException($"Binance rejected operation (code {text}): {S(answer, "msg")}", code);
    }

    /// <summary>Prueba la conexión: hora del servidor y, si hay claves, el saldo.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        try
        {
            await SyncTimeAsync(ct).ConfigureAwait(false);
            if (!_s.HasKeys) return (true, $"Public data OK ({_s.RestBase}). No credentials: market data only.");
            var balances = await GetBalancesAsync(ct).ConfigureAwait(false);
            var main = balances.OrderByDescending(b => b.Asset is "USDT" or "USDC" ? 1 : 0).ThenByDescending(b => b.Wallet).FirstOrDefault();
            return (true, main == null
                ? $"Account connected ({(_s.Testnet ? "testnet" : "REAL")}) with no balance"
                : $"Account connected ({(_s.Testnet ? "testnet" : "REAL")}): {main.Wallet:0.##} {main.Asset} (available {main.Available:0.##})");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Binance account as an <see cref="ITradingAccount"/>: sends orders over REST and refreshes the position and the open
/// orders every 2 seconds.
/// </summary>
/// <remarks>
/// Every order handle the broker returns or lists stays the same object while its order works. Each poll updates it from
/// the open orders (partial fills included); when it leaves the open list, its final state is looked up (filled,
/// canceled, expired or rejected, with the filled quantity). Binance cannot modify an order, so a prepared replacement
/// cancels it and sends a new one, which the same handle then follows. Operations on one handle run one after another.
/// <para>
/// USD-M futures stops are conditional orders, which Binance takes only through its Algo Order API since 2025-12-09: they
/// are sent, listed, looked up and canceled there, with algo ids numbered apart from order ids (their
/// <see cref="Order.ExchangeOrderId"/> is "algo:" and the algo id). Once one triggers, its handle follows the regular
/// order it placed until that finishes, and shows that order's id. Spot stops stay STOP_LOSS orders.
/// </para>
/// <para>
/// Spot's native quantity is the symbol's base-asset wallet balance (free plus locked), not a strategy's ledger or a
/// separate position for each quote pair. Binance supplies no aggregate spot entry price or unrealized profit: both
/// remain unavailable (0); strategy cost accounting continues to come from its own fills.
/// </para>
/// </remarks>
public sealed class BinanceBroker : ITradingAccount, ITrackedOrderReplacement, IDisposable
{
    /// <summary>Starts the <see cref="Order.ExchangeOrderId"/> of a conditional order: an algo id is not an order id.</summary>
    private const string AlgoPrefix = "algo:";

    private readonly BinanceAccountClient _client;
    private readonly BinanceAccountSettings _settings;
    private readonly InstrumentData _data;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private volatile bool _disposed;
    private bool _ownershipChanged;
    private readonly Queue<ExecutionPublication> _publications = new();
    private bool _publishing;
    private int _publisherThread;
    private bool _executionChanged, _accountChanged;
    private readonly record struct ExecutionPublication(bool Executions = false,
        OwnedOrderQuantity? Quantity = null, bool Ownership = false, bool Account = false);
    private ExecutionAvailability _producerAvailability = ExecutionAvailability.Synchronizing;
    private string? _nativeError, _financialError;
    private readonly Dictionary<string, decimal> _nativeQuantities = new(), _pricedQuantities = new(), _quantityBaselines = new();
    private readonly Dictionary<long, string> _publishedTradeLogicalIds = new();
    private int _uncoveredNative;
    private Queue<OwnedOrderIntent>? _recoverySubmissions;
    private readonly Queue<Tracked> _baselineRecovery = new();
    private const int RecoveryBatchSize = 16;
    private readonly TaskCompletionSource _initialNativeReplay = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string? _spotBaseAsset; // reuse the instrument catalog metadata; resolve an older metadata-less instrument once
    private string? _quoteAsset;
    private readonly object _executionProducer = new();
    private AccountContextDescriptor? _executionContext;
    private ExecutionScope? _executionScope;
    private string? _executionRestBase, _executionSymbol;
    private readonly Dictionary<long, ExecutionReport> _publishedTrades = new();
    private readonly Dictionary<long, BinanceTrade> _deferredTrades = new();
    private readonly Queue<long> _incompleteTrades = new();
    private readonly HashSet<long> _incompleteTradeIds = new();
    private long _nextTradeId, _replayTradeId;
    private bool _tradeReplayComplete;

    // Handles not finished yet, by object and by the exchange order each follows now: a regular order by its order id or,
    // in USD-M futures, a conditional order by its algo id until it triggers.
    private readonly Dictionary<Order, Tracked> _byOrder = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, Tracked> _byId = new();
    private readonly Dictionary<long, Tracked> _byAlgoId = new();
    private readonly Dictionary<string, Tracked> _byClient = new(StringComparer.Ordinal);

    // Exchange ids that finished, were replaced or triggered, and when: a list fetched around then may still show them.
    private readonly Dictionary<long, DateTime> _finished = new();
    private readonly Dictionary<long, DateTime> _finishedAlgo = new();
    private static readonly TimeSpan FinishedMemory = TimeSpan.FromSeconds(30);
    private List<long> _listed = new();
    private List<long> _listedAlgo = new();
    private int _sending; // new orders waiting for Binance's answer: an unknown open order may be one of them
    private List<Order> _orders = new();

    /// <summary>An order handle kept up to date from Binance.</summary>
    private sealed class Tracked
    {
        public Tracked(Order order, bool own)
        {
            Order = order;
            Own = own;
        }

        public Order Order { get; }

        /// <summary>Sent by this broker; otherwise listed from Binance (a manual order, or one from an earlier session).</summary>
        public bool Own { get; }

        /// <summary>Current exchange order id (0 until Binance answers the submission, and while it follows a conditional order).</summary>
        public long Id;

        /// <summary>
        /// Current conditional (algo) order id in USD-M futures until it triggers; then the handle follows the order it
        /// placed by <see cref="Id"/>. 0 otherwise.
        /// </summary>
        public long AlgoId;

        /// <summary>The conditional order's last known algo status (empty while it is not known).</summary>
        public string AlgoStatus = "";
        public NativeOrderIdentity? ConditionalParent;

        /// <summary>Binance accepted the submission: the handle follows an exchange order.</summary>
        public bool Accepted => Id != 0 || AlgoId != 0;
        public string ClientId = "";
        public PreparedOrderIntent? Intent;
        public bool SubmissionUnknown;
        public bool CancelRequested;
        public bool CancelQueued;
        public bool ReplacementPending;
        public bool RestoreReplacement;
        public decimal ReplacementPrice;
        public decimal ReplacementTotal;
        public PreparedOrderIntent? QueuedReprice;

        /// <summary>Filled by this handle's earlier exchange orders (Modify cancels and sends a new one).</summary>
        public decimal FilledBefore;
        public bool BaselinePending;
        public string? BaselineCursor;
        public decimal BaselineAccumulated;
        public HashSet<string>? BaselineVisited;

        /// <summary>Executed quantity of the current exchange order; it never goes back.</summary>
        public decimal Executed;

        /// <summary>
        /// Cancels or replaces in flight or queued: the order missing from the open list is expected, not final, and a
        /// queued Modify's price and quantity stay on the handle.
        /// </summary>
        public int Busy;

        /// <summary>
        /// Error of a Modify whose cancel had an unknown outcome and sent no replacement. A later working snapshot
        /// cannot prove that this cancel will not arrive late; retain uncertainty until a definitive outcome.
        /// </summary>
        public string? ReplaceFailure;

        /// <summary>Consecutive lookups for which Binance answered that the order does not exist.</summary>
        public int NotFound;

        /// <summary>The last operation queued on this handle; the next one waits for it.</summary>
        public Task Last = Task.CompletedTask;
    }

    public BinanceBroker(InstrumentData data, BinanceAccountSettings settings, string name, OrderContext? orderContext = null)
    {
        _data = data;
        _settings = settings;
        _client = new BinanceAccountClient(settings);
        _spotBaseAsset = string.IsNullOrWhiteSpace(data.Instrument.BaseAsset) ? null : data.Instrument.BaseAsset;
        _quoteAsset = string.IsNullOrWhiteSpace(data.Instrument.QuoteAsset) ? null : data.Instrument.QuoteAsset;
        Name = name;
        OrderContext = orderContext ?? new OrderContext(new AccountContextDescriptor("", "Binance", settings.Futures ? "usd-m-futures" : "spot", "unconfigured", "", ""));
        Position = new Position { Security = new Security { Code = data.Instrument.Symbol, SecurityId = data.Instrument.Key } };
        (Executions as ExecutionLog)?.SetProducerAvailability(_executionProducer, ExecutionAvailability.Synchronizing);
        _ = Task.Run(PollLoop);
        _ = Task.Run(FinancialLoop);
    }

    public string Name { get; }
    public bool IsReal => !_settings.Testnet;
    public decimal LastPrice => _data.LastPrice;
    public Instrument Instrument => _data.Instrument;
    public Position Position { get; }
    public IEnumerable<Order> ActiveOrders
    {
        get
        {
            lock (_gate)
                foreach (var order in _orders)
                    if (string.IsNullOrEmpty(order.OwnerId) && order.NativeIdentity != null)
                        order.ContextId = OrderContext.Descriptor.Id;
            return _orders.Where(o => o.State == OrderStates.Active);
        }
    }
    private string? _lastError;
    private string? _ownershipFault;
    public string? LastError
    {
        get
        {
            string? fault = _ownershipFault ?? OrderContext.LastError;
            string? error = _lastError ?? _nativeError ?? _financialError;
            return fault == null ? error : error == null ? fault : error + "; " + fault;
        }
        private set => _lastError = value;
    }
    public bool PositionReady { get; private set; }
    public bool OrdersReady { get; private set; }
    private volatile bool _nativeReplayReady;
    private bool _nativeOrdersReady;
    public event Action? Changed;

    // Order snapshots do not contain trade prices or commissions. Do not synthesize execution facts from them.
    public OrderContext OrderContext { get; }
    public IExecutionSource Executions => OrderContext.Executions;

    private async Task PollLoop()
    {
        try { await _client.SyncTimeAsync(_cts.Token).ConfigureAwait(false); }
        catch { /* se reintenta en el bucle */ }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _nativeReplayReady = false;
                string? lookupError = await RefreshOrdersAsync(_cts.Token).ConfigureAwait(false);

                decimal volume, averagePrice, unrealizedPnl;
                if (_settings.Futures)
                {
                    var pos = (await _client.GetPositionsAsync(_cts.Token).ConfigureAwait(false)).FirstOrDefault(p => p.Symbol == Instrument.Symbol);
                    volume = pos?.Quantity ?? 0;
                    averagePrice = pos?.EntryPrice ?? 0;
                    unrealizedPnl = pos?.UnrealizedPnl ?? 0;
                }
                else
                {
                    _spotBaseAsset ??= await _client.GetSpotBaseAssetAsync(Instrument.Symbol, _cts.Token).ConfigureAwait(false);
                    var balances = await _client.GetBalancesAsync(_cts.Token).ConfigureAwait(false);
                    decimal quantity = 0m;
                    for (int i = 0; i < balances.Count; i++)
                    {
                        if (balances[i].Asset != _spotBaseAsset) continue;
                        quantity = balances[i].Wallet;
                        break;
                    }
                    volume = quantity;
                    averagePrice = 0m;
                    unrealizedPnl = 0m;
                }

                _cts.Token.ThrowIfCancellationRequested();
                // Native order/position replay authorizes bounded reductions independently of priced history.
                lock (_gate)
                {
                    if (_disposed) break;
                    Position.Volume = volume;
                    Position.AveragePrice = averagePrice;
                    Position.UnrealizedPnL = unrealizedPnl;
                    PositionReady = true;
                    OrdersReady = lookupError == null && !HasUnknownSubmission();
                    _nativeReplayReady = _nativeOrdersReady;
                    _lastError = null;
                    _nativeError = lookupError;
                    UpdateProducerAvailability();
                }
                _initialNativeReplay.TrySetResult();
                PublishOwnershipObservations();
                PublishAccountChanged();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // also a request that timed out (HttpClient reports it as a cancellation): reported, and polling goes on
                PositionReady = false;
                OrdersReady = false;
                _nativeReplayReady = false;
                lock (_gate)
                {
                    if (_disposed) break;
                    _nativeOrdersReady = false;
                    _nativeError = ex.Message;
                    UpdateProducerAvailability();
                }
                PublishOwnershipObservations();
                PublishAccountChanged();
            }

            try { await Task.Delay(_settings.PollInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        PositionReady = false;
        OrdersReady = false;
        _nativeReplayReady = false;
    }

    // A single worker owns history pagination and fee replay. Held financial HTTP never blocks native polling.
    private async Task FinancialLoop()
    {
        try { await _initialNativeReplay.Task.WaitAsync(_cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                string? error = await RefreshExecutionsAsync(_cts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) break;
                    _financialError = error;
                    UpdateProducerAvailability();
                }
                PublishOwnershipObservations();
                PublishAccountChanged();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_disposed) break;
                    _financialError = "Binance execution history remains unresolved: " + ex.Message;
                    UpdateProducerAvailability();
                }
                PublishOwnershipObservations();
                PublishAccountChanged();
            }
            try { await Task.Delay(_settings.PollInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Install visibility under the broker gate; notifications alone are deferred outside it.
    private void UpdateProducerAvailability()
    {
        var availability = _nativeError != null || _financialError != null || _ownershipFault != null ? ExecutionAvailability.Unavailable
            : _tradeReplayComplete && _nativeOrdersReady && !HasUnknownSubmission() && _uncoveredNative == 0
                ? ExecutionAvailability.Ready : ExecutionAvailability.Synchronizing;
        QueueProducerAvailability(availability);
    }

    private void QueueProducerAvailability(ExecutionAvailability availability)
    {
        if (_producerAvailability == availability) return;
        _producerAvailability = availability;
        if (Executions is ExecutionLog log && log.SetProducerAvailability(_executionProducer, availability, notify: false))
            _executionChanged = true;
    }

    private void ObservePriceCoverage(Order order, decimal filled)
    {
        string? logicalId = order.LogicalOrderId;
        if (string.IsNullOrEmpty(order.OwnerId) || string.IsNullOrEmpty(logicalId)) return;
        if (!_quantityBaselines.TryGetValue(logicalId, out var baseline))
        {
            var logical = OrderContext.Find(logicalId) ??
                throw new InvalidDataException("Binance native quantity lost its exact durable logical attribution.");
            _quantityBaselines[logicalId] = baseline = logical.QuantityBaseline;
        }
        decimal executed = filled - baseline;
        decimal before = _nativeQuantities.GetValueOrDefault(logicalId);
        if (executed <= before) return;
        decimal priced = _pricedQuantities.GetValueOrDefault(logicalId);
        _nativeQuantities[logicalId] = executed;
        if (before <= priced && executed > priced) _uncoveredNative++;
        if (_uncoveredNative > 0 && _producerAvailability == ExecutionAvailability.Ready)
            QueueProducerAvailability(ExecutionAvailability.Synchronizing);
    }

    private void AdjustPricedQuantity(string logicalId, decimal delta)
    {
        decimal native = _nativeQuantities.GetValueOrDefault(logicalId);
        decimal before = _pricedQuantities.GetValueOrDefault(logicalId);
        if (native > before) _uncoveredNative--;
        decimal after = before + delta;
        _pricedQuantities[logicalId] = after;
        if (native > after) _uncoveredNative++;
    }

    /// <summary>
    /// Replays the venue's accessible symbol history by inclusive native ID, one bounded page per poll. After the
    /// initial pass, a second rotating pass revisits old IDs (including late ownership and corrected fees), while
    /// the forward pass follows new executions. A separate round-robin page prioritizes incomplete charges.
    /// Ready describes this accessible replay, not unlimited retention or finality of a broker cash statement.
    /// </summary>
    private async Task<string?> RefreshExecutionsAsync(CancellationToken ct)
    {
        if (Executions is not ExecutionLog) return null;
        var descriptor = OrderContext.Descriptor;
        if (string.IsNullOrWhiteSpace(descriptor.Id) || string.IsNullOrWhiteSpace(descriptor.LocalNamespace) ||
            string.IsNullOrWhiteSpace(descriptor.Generation) || string.IsNullOrWhiteSpace(OrderContext.StorageRoot) ||
            string.IsNullOrWhiteSpace(descriptor.Environment) || descriptor.Environment == "unconfigured") return null;
        if (descriptor.Provider != "Binance" || descriptor.Product != (_settings.Futures ? "usd-m-futures" : "spot"))
            throw new InvalidOperationException("Binance execution history does not match the configured account product.");
        if (_executionContext == null)
        {
            _executionContext = descriptor;
            _executionRestBase = _settings.RestBase;
            _executionSymbol = Instrument.Symbol;
            // Native UID evidence is deliberately not the canonical namespace, even if verified later.
            _executionScope = new(descriptor.Provider, "local:" + descriptor.LocalNamespace, descriptor.Environment,
                Instrument.Key, descriptor.Product + ":" + descriptor.Generation) { ContextId = descriptor.Id };
        }
        else if (_executionContext.Id != descriptor.Id || _executionContext.Provider != descriptor.Provider ||
            _executionContext.Product != descriptor.Product || _executionContext.Environment != descriptor.Environment ||
            _executionContext.LocalNamespace != descriptor.LocalNamespace || _executionContext.Generation != descriptor.Generation ||
            _executionRestBase != _settings.RestBase || _executionSymbol != Instrument.Symbol || _executionScope!.Instrument != Instrument.Key)
            throw new InvalidOperationException("A Binance execution producer cannot move to another account generation.");

        // Quote currency is venue/catalog evidence, never inferred from a symbol suffix. Metadata failure must not
        // hide already known execution prices: publish them with unknown accounting currency and revisit later.
        string? metadataError = null;
        if (_quoteAsset == null)
        {
            try { _quoteAsset = await _client.GetQuoteAssetAsync(Instrument.Symbol, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { metadataError = ex.Message; }
        }

        var captured = new Dictionary<long, BinanceTrade>(_deferredTrades);
        bool wasComplete = _tradeReplayComplete;
        var forward = await _client.GetTradesAsync(Instrument.Symbol, _nextTradeId, ct).ConfigureAwait(false);
        long next = CaptureTradePage(forward, _nextTradeId, captured);
        bool replayComplete = wasComplete || forward.Count < 1000;
        if (wasComplete)
        {
            var replay = await _client.GetTradesAsync(Instrument.Symbol, _replayTradeId, ct).ConfigureAwait(false);
            long replayNext = CaptureTradePage(replay, _replayTradeId, captured);
            _replayTradeId = replay.Count < 1000 ? 0 : replayNext;
        }
        if (_incompleteTrades.Count > 0)
        {
            long id = _incompleteTrades.Dequeue();
            if (_incompleteTradeIds.Contains(id))
            {
                _incompleteTrades.Enqueue(id);
                var incomplete = await _client.GetTradesAsync(Instrument.Symbol, id, ct).ConfigureAwait(false);
                CaptureTradePage(incomplete, id, captured);
            }
        }
        ct.ThrowIfCancellationRequested();
        var changes = new List<ExecutionReport>();
        HashSet<long>? recoveryLookups = null;
        _deferredTrades.Clear();
        foreach (var trade in captured.Values)
        {
            var native = new NativeOrderIdentity("binance:order", trade.Symbol + ":" + trade.OrderId.ToString(CultureInfo.InvariantCulture));
            if (_publishedTrades.TryGetValue(trade.Id, out var priced) && priced.NativeOrder != native)
                throw new InvalidDataException("Binance changed a trade's native order identity.");
            var fees = trade.Commission is { } commission ? new[] { new ExecutionFee(commission, trade.CommissionAsset) } : Array.Empty<ExecutionFee>();
            var report = new ExecutionReport(new(_executionScope!, trade.Id.ToString(CultureInfo.InvariantCulture)), 0,
                trade.Symbol, DateTimeOffset.FromUnixTimeMilliseconds(trade.Time).UtcDateTime, trade.Buy,
                trade.Quantity, trade.Price, Instrument.PointValue, _quoteAsset, fees,
                trade.Commission.HasValue && trade.CommissionAsset != null)
                { ContextId = descriptor.Id, NativeOrder = native };
            var owner = OrderContext.Resolve(report);
            if (owner == null && (recoveryLookups ??= new HashSet<long>()).Add(trade.OrderId))
            {
                // Trade rows have no client ID. Query the exact order, including terminal history, before
                // recovering durable client lineage; matching prices or quantities never establish ownership.
                var order = await _client.GetOrderAsync(trade.Symbol, trade.OrderId, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (order != null && order.OrderId == trade.OrderId && order.Symbol == trade.Symbol &&
                    order.Side == (trade.Buy ? "BUY" : "SELL"))
                {
                    lock (_gate)
                    {
                        if (_disposed) return null;
                        RecoverRegularOrder(order, out _);
                    }
                    PublishOwnershipObservations();
                    owner = OrderContext.Resolve(report);
                }
            }
            if (owner == null)
            {
                // Bounded early-delivery cache; older unresolved facts remain reachable in the rotating REST replay.
                if (_deferredTrades.Count < 1000) _deferredTrades[trade.Id] = trade;
                continue;
            }
            if (owner.Request.Instrument != Instrument.Key ||
                (owner.NativeInstrument is { Length: > 0 } instrument && instrument != trade.Symbol) ||
                (owner.Request.Direction == OrderDirections.Buy) != trade.Buy)
                throw new InvalidDataException("Binance execution disagrees with its exact durable native owner.");
            report = report with { IntentId = owner.Id, ClientOrderId = owner.ClientOrderId };
            if (!report.FeesComplete)
            {
                if (_incompleteTradeIds.Add(trade.Id)) _incompleteTrades.Enqueue(trade.Id);
            }
            else _incompleteTradeIds.Remove(trade.Id);
            if (!_publishedTrades.TryGetValue(trade.Id, out var previous) || !SameTradeReport(previous, report))
                changes.Add(report);
        }
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed) return null;
            // Install the complete captured batch before Ready, without invoking consumers under the broker gate.
            if (changes.Count > 0)
            {
                ((ExecutionLog)Executions).PublishBatch(changes, notify: false);
                _executionChanged = true;
            }
            foreach (var report in changes)
            {
                long id = long.Parse(report.Identity.Id, CultureInfo.InvariantCulture);
                var owner = OrderContext.Find(report.IntentId) ??
                    throw new InvalidDataException("Binance execution lost its exact durable owner.");
                if (_publishedTrades.TryGetValue(id, out var previous))
                    AdjustPricedQuantity(_publishedTradeLogicalIds[id], -previous.Quantity);
                _publishedTradeLogicalIds[id] = owner.LogicalOrderId;
                AdjustPricedQuantity(owner.LogicalOrderId, report.Quantity);
                _publishedTrades[id] = report;
            }
            _nextTradeId = next;
            _tradeReplayComplete = replayComplete;
            _financialError = metadataError == null ? null : "Binance accounting currency remains unknown: " + metadataError;
            UpdateProducerAvailability();
        }
        PublishOwnershipObservations();
        return metadataError == null ? null : "Binance accounting currency remains unknown: " + metadataError;
    }

    private static long CaptureTradePage(IReadOnlyList<BinanceTrade> page, long fromId, Dictionary<long, BinanceTrade> captured)
    {
        long next = fromId;
        foreach (var trade in page)
        {
            if (trade.Id < fromId || trade.Id == long.MaxValue)
                throw new InvalidDataException("Binance returned a trade outside its requested native-ID page.");
            captured[trade.Id] = trade;
            next = Math.Max(next, trade.Id + 1);
        }
        return next;
    }

    private static bool SameTradeReport(ExecutionReport left, ExecutionReport right) =>
        left.Identity == right.Identity && left.LocalOrderId == right.LocalOrderId && left.Symbol == right.Symbol &&
        left.Time == right.Time && left.Buy == right.Buy && left.Quantity == right.Quantity && left.Price == right.Price &&
        left.PointValue == right.PointValue && left.AccountingCurrency == right.AccountingCurrency &&
        left.FeesComplete == right.FeesComplete && left.ContextId == right.ContextId && left.IntentId == right.IntentId &&
        left.ClientOrderId == right.ClientOrderId && left.NativeOrder == right.NativeOrder && left.Fees.AsSpan().SequenceEqual(right.Fees);

    /// <summary>
    /// Brings the handles up to date with Binance's open orders and looks up the final state of those that left the list.
    /// Returns the lookup error to report, if any (those orders are looked up again at the next poll).
    /// </summary>
    /// <remarks>
    /// In USD-M futures the conditional (algo) orders are listed after the regular ones but gone through first: an order
    /// one of them placed when it triggered then already belongs to that handle when the regular orders are gone through.
    /// </remarks>
    private async Task<string?> RefreshOrdersAsync(CancellationToken ct)
    {
        var open = await _client.GetOpenOrdersAsync(Instrument.Symbol, ct).ConfigureAwait(false);
        string? submissionError = await ResolveSubmissionsAsync(ct).ConfigureAwait(false);
        string? recoveryError = _settings.Futures ? await RecoverConditionalSubmissionsAsync(ct).ConfigureAwait(false) : null;
        string? algoError = null;
        if (_settings.Futures)
        {
            try
            {
                algoError = await RefreshAlgoOrdersAsync(await _client.GetOpenAlgoOrdersAsync(Instrument.Symbol, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                algoError = ex.Message; // reported; the regular orders are still brought up to date
            }
        }

        var gone = new List<(Tracked Handle, long Id)>();
        lock (_gate)
        {
            var listed = new List<long>(open.Count);
            bool triggering = Triggering();
            foreach (var s in open)
            {
                if (_finished.ContainsKey(s.OrderId)) continue; // finished or replaced moments ago: a stale listing
                if (!_byId.TryGetValue(s.OrderId, out var t))
                {
                    // it may be one of ours whose answer has not arrived, or the order a conditional order placed when it
                    // triggered, not known yet: next poll
                    if (s.ClientOrderId.Length > 0 && _byClient.TryGetValue(s.ClientOrderId, out var owned) &&
                        (!owned.Accepted || owned.Id == s.OrderId))
                    {
                        if (!owned.Last.IsCompleted || owned.Busy > 0) continue;
                        t = owned;
                        Bind(t, s.OrderId);
                    }
                    else
                    {
                        if (_sending > 0 || triggering) continue;
                        t = RecoverRegular(s);
                        _byOrder[t.Order] = t;
                        if (t.ClientId.Length > 0) _byClient[t.ClientId] = t;
                        Bind(t, s.OrderId);
                    }
                }

                listed.Add(s.OrderId);
                t.NotFound = 0;
                if (t.Busy == 0) Apply(t, s);
            }

            _listed = listed;
            Forget(_finished);
            var still = new HashSet<long>(listed);
            foreach (var (id, t) in _byId)
                if (t.Busy == 0 && !still.Contains(id)) gone.Add((t, id));
        }

        string? error = null;
        foreach (var (t, id) in gone)
        {
            BinanceOpenOrder? s;
            try
            {
                s = await _client.GetOrderAsync(Instrument.Symbol, id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = ex.Message;
                continue;
            }

            lock (_gate)
            {
                if (t.Id != id || t.Busy > 0 || !_byOrder.ContainsKey(t.Order)) continue; // replaced or finished meanwhile
                if (s != null)
                {
                    t.NotFound = 0;
                    Apply(t, s);
                }
                else if (!t.ReplacementPending && ++t.NotFound >= 3)
                {
                    FinishCanceled(t); // Binance dropped it: canceled or expired without fills
                }

                if (t.Order.State == OrderStates.Failed && t.ReplaceFailure is { } why)
                    error = "Modify failed: the order was canceled but its replacement was not sent (" + why + ")";
            }
        }

        string? baselineError = await RestoreReplacementBaselinesAsync(ct).ConfigureAwait(false);
        DispatchQueuedReprices();
        Publish();
        QueueDeferredCancels();
        lock (_gate) _nativeOrdersReady = error == null && algoError == null && recoveryError == null && baselineError == null;
        return submissionError ?? error ?? algoError ?? recoveryError ?? baselineError;
    }

    private async Task<string?> RecoverConditionalSubmissionsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            _recoverySubmissions ??= new(OrderContext.RecoverableNativeSubmissions(Instrument.Key, OrderTypes.Stop));
        }
        string? error = null;
        int count = Math.Min(RecoveryBatchSize, _recoverySubmissions.Count);
        for (int i = 0; i < count; i++)
        {
            var candidate = _recoverySubmissions.Dequeue();
            lock (_gate)
            {
                if (_disposed) return null;
                if (!candidate.Adopted && _byClient.ContainsKey(candidate.ClientOrderId)) continue;
            }
            try
            {
                long adoptedParent = 0;
                if (candidate.Adopted)
                {
                    foreach (var identity in candidate.NativeOrders ?? Array.Empty<NativeOrderIdentity>())
                    {
                        if (identity.Kind != "binance:algo") continue;
                        if (adoptedParent != 0 || !long.TryParse(identity.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                            throw new InvalidDataException("An adopted Binance conditional order has no unique exact native parent.");
                        adoptedParent = id;
                    }
                    if (adoptedParent == 0)
                        throw new InvalidDataException("An adopted Binance conditional order has no exact native parent.");
                }
                bool adoptedUnknownRoute = candidate.Adopted && candidate.NativeInstrument == null && candidate.NativeExchange == null;
                if (!adoptedUnknownRoute && (candidate.NativeInstrument != Instrument.Symbol || candidate.NativeExchange != ""))
                    throw new InvalidDataException("Binance conditional recovery disagrees with its persisted native route.");
                var native = adoptedParent != 0
                    ? await _client.GetAlgoOrderAsync(Instrument.Symbol, adoptedParent, ct).ConfigureAwait(false)
                    : await _client.GetAlgoByClientAsync(Instrument.Symbol, candidate.ClientOrderId, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (native == null)
                {
                    _recoverySubmissions.Enqueue(candidate);
                    error ??= "Binance conditional submission remains unresolved by its exact client identity.";
                    continue;
                }
                if ((!candidate.Adopted && (native.ClientAlgoId != candidate.ClientOrderId || native.Type != "STOP_MARKET")) ||
                    native.Symbol != Instrument.Symbol || native.AlgoId <= 0 || (adoptedParent != 0 && native.AlgoId != adoptedParent) ||
                    (candidate.NativeOrders is { Length: > 0 } identities &&
                        !identities.Contains(new NativeOrderIdentity("binance:algo", native.AlgoId.ToString(CultureInfo.InvariantCulture)))) ||
                    native.Side != (candidate.Request.Direction == OrderDirections.Buy ? "BUY" : "SELL") ||
                    native.ReduceOnly != candidate.Request.ReduceOnly)
                    throw new InvalidDataException("Binance conditional parent disagrees with its persisted client dispatch.");
                lock (_gate)
                {
                    if (_disposed) return null;
                    if (!candidate.Adopted && _byClient.ContainsKey(candidate.ClientOrderId)) continue;
                    if (_byAlgoId.TryGetValue(native.AlgoId, out var existing))
                    {
                        if (existing.Intent?.Id == candidate.Id) continue;
                        throw new InvalidDataException("Binance conditional recovery conflicts with an already tracked native parent.");
                    }
                    var tracked = RecoverAlgo(native);
                    if (tracked.Intent?.Id != candidate.Id)
                        throw new InvalidDataException("Binance conditional parent has no matching durable dispatch owner.");
                    _byOrder[tracked.Order] = tracked;
                    if (tracked.ClientId.Length > 0) _byClient[tracked.ClientId] = tracked;
                    BindAlgo(tracked, native.AlgoId);
                    ApplyAlgo(tracked, native);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _recoverySubmissions.Enqueue(candidate);
                error = ex.Message;
            }
        }
        return error ?? (_recoverySubmissions.Count > 0 ? "Binance conditional client recovery is synchronizing." : null);
    }

    // Legacy replacement records without a raw baseline are reconciled from one exact predecessor per pass.
    // Retain the traversal cursor rather than scanning/copying a logical order's history on prices or ordinary fills.
    private async Task<string?> RestoreReplacementBaselinesAsync(CancellationToken ct)
    {
        int count;
        lock (_gate)
        {
            if (_disposed) return null;
            count = Math.Min(RecoveryBatchSize, _baselineRecovery.Count);
        }
        if (count == 0) return null;
        string? error = null;
        for (int i = 0; i < count; i++)
        {
            Tracked tracked;
            lock (_gate)
            {
                if (_disposed) return null;
                tracked = _baselineRecovery.Dequeue();
            }
            try
            {
                OwnedOrderIntent predecessor;
                lock (_gate)
                {
                    if (_disposed) return null;
                    if (!_byOrder.ContainsKey(tracked.Order) || !tracked.BaselinePending) continue;
                    predecessor = OrderContext.Find(tracked.BaselineCursor) ??
                        throw new InvalidDataException("Binance replacement lost its durable predecessor.");
                    var intent = tracked.Intent!;
                    if (predecessor.ContextId != OrderContext.Descriptor.Id ||
                        predecessor.Generation != OrderContext.Descriptor.Generation || predecessor.Owner != intent.Owner ||
                        predecessor.LogicalOrderId != intent.LogicalOrderId || predecessor.Request.Instrument != Instrument.Key ||
                        predecessor.NativeInstrument != Instrument.Symbol || predecessor.NativeExchange != "" ||
                        predecessor.Request.Direction != tracked.Order.Direction)
                        throw new InvalidDataException("Binance replacement predecessor disagrees with its exact durable lineage.");
                    if (tracked.BaselineVisited?.Contains(predecessor.Id) == true)
                        throw new InvalidDataException("Binance replacement predecessor lineage contains a cycle.");
                }
                decimal executed = await GetTerminalPredecessorQuantityAsync(predecessor, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_disposed) return null;
                    if (!_byOrder.ContainsKey(tracked.Order) || !tracked.BaselinePending) continue;
                    decimal accumulated = tracked.BaselineAccumulated + executed;
                    decimal? prefix = predecessor.PredecessorId == null ? 0 : predecessor.PredecessorFilled;
                    if (prefix == null)
                    {
                        (tracked.BaselineVisited ??= new(StringComparer.Ordinal)).Add(predecessor.Id);
                        tracked.BaselineAccumulated = accumulated;
                        tracked.BaselineCursor = predecessor.PredecessorId;
                        continue;
                    }
                    decimal baseline = prefix.Value + accumulated;
                    if (OrderContext.RecordPredecessorFilled(tracked.Intent!, baseline) is { } failure)
                        throw new InvalidDataException(failure);
                    decimal previousFilled = tracked.Order.Filled;
                    var previousState = tracked.Order.State;
                    var previousStatus = tracked.Order.Status;
                    ObservePriceCoverage(tracked.Order, previousFilled + baseline);
                    tracked.FilledBefore = baseline;
                    tracked.Order.QuantityToFill += baseline;
                    tracked.BaselinePending = false;
                    tracked.BaselineVisited = null;
                    ObserveNativeQuantity(tracked.Order, previousFilled, previousState, previousStatus);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { error = ex.Message; }
            finally
            {
                lock (_gate)
                    if (!_disposed && tracked.BaselinePending && _byOrder.ContainsKey(tracked.Order))
                        _baselineRecovery.Enqueue(tracked);
            }
        }
        return error ?? "Binance replacement raw cumulative baseline is synchronizing.";
    }

    private async Task<decimal> GetTerminalPredecessorQuantityAsync(OwnedOrderIntent predecessor, CancellationToken ct)
    {
        BinanceOpenOrder? native;
        if (_settings.Futures && predecessor.Request.Type == OrderTypes.Stop)
        {
            var parent = await _client.GetAlgoByClientAsync(Instrument.Symbol, predecessor.ClientOrderId, ct).ConfigureAwait(false);
            if (parent == null || parent.ClientAlgoId != predecessor.ClientOrderId || parent.Symbol != Instrument.Symbol ||
                parent.Type != "STOP_MARKET" || parent.Side != (predecessor.Request.Direction == OrderDirections.Buy ? "BUY" : "SELL") ||
                parent.ReduceOnly != predecessor.Request.ReduceOnly ||
                !(predecessor.NativeOrders ?? Array.Empty<NativeOrderIdentity>()).Contains(
                    new("binance:algo", parent.AlgoId.ToString(CultureInfo.InvariantCulture))))
                throw new InvalidDataException("Binance replacement conditional predecessor is not evidenced by its exact parent.");
            if (parent.ActualOrderId == 0)
            {
                if (parent.Status is not ("CANCELED" or "EXPIRED" or "REJECTED" or "FINISHED"))
                    throw new InvalidDataException("Binance replacement predecessor has not reached a final state.");
                return parent.ActualQuantity;
            }
            native = await _client.GetOrderAsync(Instrument.Symbol, parent.ActualOrderId, ct).ConfigureAwait(false);
            if (native?.OrderId != parent.ActualOrderId)
                throw new InvalidDataException("Binance replacement child disagrees with its evidenced conditional parent.");
        }
        else
        {
            native = await _client.GetOrderByClientAsync(Instrument.Symbol, predecessor.ClientOrderId, ct).ConfigureAwait(false);
            if (native == null || native.ClientOrderId != predecessor.ClientOrderId || TypeOf(native.Type) != predecessor.Request.Type ||
                !(predecessor.NativeOrders ?? Array.Empty<NativeOrderIdentity>()).Contains(
                    new("binance:order", Instrument.Symbol + ":" + native.OrderId.ToString(CultureInfo.InvariantCulture))))
                throw new InvalidDataException("Binance replacement predecessor has no exact native client lineage.");
        }
        if (native == null || native.Symbol != Instrument.Symbol ||
            native.Side != (predecessor.Request.Direction == OrderDirections.Buy ? "BUY" : "SELL") ||
            (_settings.Futures && native.ReduceOnly != predecessor.Request.ReduceOnly) ||
            native.Status is not ("FILLED" or "CANCELED" or "EXPIRED" or "EXPIRED_IN_MATCH" or "REJECTED"))
            throw new InvalidDataException("Binance replacement predecessor quantity has no final native evidence.");
        return native.Executed;
    }

    /// <summary>
    /// Brings the handles of conditional (algo) orders up to date with Binance's open ones and looks up those that left
    /// the list: canceled, expired or rejected, or triggered, in which case the handle follows the regular order it
    /// placed from now on. Returns the lookup error to report, if any.
    /// </summary>
    private async Task<string?> RefreshAlgoOrdersAsync(IReadOnlyList<BinanceAlgoOrder> open, CancellationToken ct)
    {
        var gone = new List<(Tracked Handle, long AlgoId)>();
        lock (_gate)
        {
            var listed = new List<long>(open.Count);
            foreach (var a in open)
            {
                if (_finishedAlgo.ContainsKey(a.AlgoId)) continue; // finished, replaced or triggered moments ago
                if (!_byAlgoId.TryGetValue(a.AlgoId, out var t))
                {
                    // it may be one of ours whose answer has not arrived; one that triggered is followed through the order
                    // it placed, which the regular open orders list
                    if (a.ClientAlgoId.Length > 0 && _byClient.TryGetValue(a.ClientAlgoId, out var owned) &&
                        (!owned.Accepted || owned.AlgoId == a.AlgoId))
                    {
                        if (!owned.Last.IsCompleted || owned.Busy > 0) continue;
                        t = owned;
                        BindAlgo(t, a.AlgoId);
                    }
                    else
                    {
                        if (_sending > 0) continue;
                        t = RecoverAlgo(a);
                        if (a.ActualOrderId != 0 && !t.Own) continue;
                        _byOrder[t.Order] = t;
                        if (t.ClientId.Length > 0) _byClient[t.ClientId] = t;
                        BindAlgo(t, a.AlgoId);
                    }
                }

                listed.Add(a.AlgoId);
                t.NotFound = 0;
                if (t.Busy == 0) ApplyAlgo(t, a);
            }

            _listedAlgo = listed;
            Forget(_finishedAlgo);
            var still = new HashSet<long>(listed);
            foreach (var (id, t) in _byAlgoId)
            {
                if (t.Busy > 0 || still.Contains(id)) continue;
                t.AlgoStatus = ""; // not known until looked up: it may have triggered
                gone.Add((t, id));
            }
        }

        string? error = null;
        foreach (var (t, id) in gone)
        {
            BinanceAlgoOrder? a;
            try
            {
                a = await _client.GetAlgoOrderAsync(Instrument.Symbol, id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = ex.Message;
                continue;
            }

            lock (_gate)
            {
                if (t.AlgoId != id || t.Busy > 0 || !_byOrder.ContainsKey(t.Order)) continue; // replaced or finished meanwhile
                if (a != null)
                {
                    t.NotFound = 0;
                    ApplyAlgo(t, a);
                }
                else if (!t.ReplacementPending && ++t.NotFound >= 3)
                {
                    FinishCanceled(t); // Binance dropped it: canceled or expired without fills
                }

                if (t.Order.State != OrderStates.Failed) continue;
                if (a is { Status: "REJECTED" })
                    error = $"Binance rejected conditional order {AlgoPrefix}{id} (algo status REJECTED, for example a failed margin check)";
                else if (t.ReplaceFailure is { } why)
                    error = "Modify failed: the order was canceled but its replacement was not sent (" + why + ")";
            }
        }

        return error;
    }

    /// <summary>
    /// Whether a conditional order may already have triggered and placed an order whose id is not known yet (its state is
    /// not known, it is triggering, or a cancel or replacement of it is under way): an unknown regular order may be that one.
    /// </summary>
    private bool Triggering()
    {
        foreach (var t in _byAlgoId.Values)
            if (t.Busy > 0 || t.AlgoStatus != "NEW") return true;
        return false;
    }

    /// <summary>Forgets finished exchange ids once a list can no longer show them.</summary>
    private static void Forget(Dictionary<long, DateTime> finished)
    {
        var forget = DateTime.UtcNow - FinishedMemory;
        foreach (var (id, at) in finished)
            if (at < forget) finished.Remove(id);
    }

    /// <summary>
    /// Copies Binance's view of the handle's current exchange order onto it; a final state finishes the handle. While a
    /// Modify is queued, the handle keeps showing its price and quantity until the order is final.
    /// </summary>
    private void Apply(Tracked t, BinanceOpenOrder s)
    {
        if (_disposed || s.Status.Length == 0 || t.BaselinePending) return; // a bare acknowledgement adds nothing
        var o = t.Order;
        decimal previousFilled = o.Filled;
        var previousState = o.State;
        var previousStatus = o.Status;
        if (string.IsNullOrEmpty(o.OwnerId) && s.ClientOrderId.Length > 0) o.ClientOrderId = s.ClientOrderId;
        bool final = s.Status is "FILLED" or "CANCELED" or "EXPIRED" or "EXPIRED_IN_MATCH" or "REJECTED";
        ObservePriceCoverage(o, t.FilledBefore + Math.Max(t.Executed,
            s.Status == "FILLED" ? Math.Max(s.Executed, s.Quantity) : s.Executed));
        if (t.Busy == 0 || final)
        {
            if (!t.Own)
            {
                o.Direction = s.Side == "BUY" ? OrderDirections.Buy : OrderDirections.Sell;
                o.Type = TypeOf(s.Type);
                o.Price = s.Price;
                o.TriggerPrice = s.StopPrice;
                o.ReduceOnly = s.ReduceOnly;
            }
            else if (o.Type == OrderTypes.Stop)
            {
                if (s.StopPrice > 0) o.TriggerPrice = s.StopPrice;
            }
            else if (s.Price > 0)
            {
                o.Price = s.Price;
            }
            if (t.ConditionalParent is { } parent && s.OrderId == t.Id && s.Symbol == Instrument.Symbol &&
                (s.Type is "LIMIT" or "MARKET") && s.Side == (o.Direction == OrderDirections.Buy ? "BUY" : "SELL") &&
                s.ReduceOnly == o.ReduceOnly && !string.IsNullOrEmpty(o.OwnerId))
            {
                var child = new NativeOrderIdentity("binance:order", s.Symbol + ":" + s.OrderId.ToString(CultureInfo.InvariantCulture));
                var workingType = s.Type == "MARKET" ? OrderTypes.Market : OrderTypes.Limit;
                var error = OrderContext.RecordNativeWorkingType(o, parent, child, workingType,
                    Instrument.Key, s.Symbol, notify: false);
                if (error == null)
                {
                    t.ConditionalParent = null;
                    _ownershipChanged = true;
                    o.Type = workingType;
                    o.Price = s.Price;
                }
                else _ownershipFault = "Binance working order type remains unresolved: " + error;
            }

            if (s.Quantity > 0) o.QuantityToFill = t.FilledBefore + s.Quantity;
            ConfirmReplacement(t, s.Quantity > 0 ? t.FilledBefore + s.Quantity : 0, o.Type == OrderTypes.Stop ? s.StopPrice : s.Price);
        }

        t.Executed = Math.Max(t.Executed, s.Executed);
        o.Unfilled = Math.Max(0, o.QuantityToFill - t.FilledBefore - t.Executed);
        switch (s.Status)
        {
            case "FILLED":
                o.Unfilled = 0;
                Finish(t, OrderStates.Done, OrderStatus.Filled);
                break;
            case "CANCELED" or "EXPIRED" or "EXPIRED_IN_MATCH":
                FinishCanceled(t); // there is no Expired status: an expiry is the exchange's cancel
                break;
            case "REJECTED":
                if (t.Intent is { } rejected) RecordRejected(rejected, "Binance rejected the order.");
                Finish(t, OrderStates.Failed, OrderStatus.Rejected);
                break;
            default: // NEW, PARTIALLY_FILLED (futures also NEW_INSURANCE, NEW_ADL): still working
                o.State = OrderStates.Active;
                o.Status = t.FilledBefore + t.Executed > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed;
                break;
        }
        ObserveNativeQuantity(o, previousFilled, previousState, previousStatus);
        if (final && t.Busy == 0 && !t.ReplacementPending) RecordNativeTerminal(o);
    }

    /// <summary>
    /// The order type a Binance type shows as: stops (also a stop-limit, a trailing stop) at their trigger price, market
    /// orders, and everything else, take profits included, as limits.
    /// </summary>
    private static OrderTypes TypeOf(string type) => type.Contains("STOP") ? OrderTypes.Stop : type == "MARKET" ? OrderTypes.Market : OrderTypes.Limit;

    /// <summary>
    /// Copies Binance's view of the handle's conditional order onto it. Once it triggered, the handle follows the regular
    /// order it placed, which tells how it ends; a final state before that finishes the handle. While a Modify is queued,
    /// the handle keeps showing its price and quantity until the order is final.
    /// </summary>
    private void ApplyAlgo(Tracked t, BinanceAlgoOrder a)
    {
        if (_disposed || a.Status.Length == 0 || t.BaselinePending) return; // an answer without a state adds nothing
        var o = t.Order;
        decimal previousFilled = o.Filled;
        var previousState = o.State;
        var previousStatus = o.Status;
        if (string.IsNullOrEmpty(o.OwnerId) && a.ClientAlgoId.Length > 0) o.ClientOrderId = a.ClientAlgoId;
        t.AlgoStatus = a.Status;
        bool final = a.ActualOrderId != 0 || a.Status is "CANCELED" or "EXPIRED" or "REJECTED" or "FINISHED";
        ObservePriceCoverage(o, t.FilledBefore + Math.Max(t.Executed,
            a.ActualOrderId != 0 || a.Status == "FINISHED" ? a.ActualQuantity : 0));
        if (t.Busy == 0 || final)
        {
            if (!t.Own)
            {
                o.Direction = a.Side == "BUY" ? OrderDirections.Buy : OrderDirections.Sell;
                o.Type = TypeOf(a.Type);
                o.Price = o.Type == OrderTypes.Limit && a.Price == 0 ? a.TriggerPrice : a.Price; // a take profit at market works at its trigger
                o.TriggerPrice = a.TriggerPrice;
                o.ReduceOnly = a.ReduceOnly;
            }
            else if (a.TriggerPrice > 0)
            {
                o.TriggerPrice = a.TriggerPrice;
            }

            if (a.Quantity > 0) o.QuantityToFill = t.FilledBefore + a.Quantity;
            ConfirmReplacement(t, a.Quantity > 0 ? t.FilledBefore + a.Quantity : 0, a.TriggerPrice);
        }

        o.Unfilled = Math.Max(0, o.QuantityToFill - t.FilledBefore - t.Executed);
        if (a.ActualOrderId != 0)
        {
            t.Executed = Math.Max(t.Executed, a.ActualQuantity);
            o.Unfilled = Math.Max(0, o.QuantityToFill - t.FilledBefore - t.Executed);
            Follow(t, a.ActualOrderId); // triggered: the regular orders tell how the order it placed ends
            ObserveNativeQuantity(o, previousFilled, previousState, previousStatus);
            return;
        }

        switch (a.Status)
        {
            case "CANCELED" or "EXPIRED": // EXPIRED: canceled by the system
                FinishCanceled(t);
                break;
            case "REJECTED": // refused by the matching engine when it triggered
                if (t.Intent is { } rejected) RecordRejected(rejected, "Binance rejected the conditional order.");
                Finish(t, OrderStates.Failed, OrderStatus.Rejected);
                break;
            case "FINISHED":
                // filled or canceled in the matching engine, but the answer does not say which order it placed: the
                // quantity that order executed decides
                t.Executed = Math.Max(t.Executed, a.ActualQuantity);
                o.Unfilled = Math.Max(0, o.QuantityToFill - t.FilledBefore - t.Executed);
                if (a.ActualQuantity > 0 && o.Unfilled == 0) Finish(t, OrderStates.Done, OrderStatus.Filled);
                else FinishCanceled(t);
                break;
            default: // NEW, TRIGGERING, TRIGGERED until the order it placed is known: still working
                o.State = OrderStates.Active;
                o.Status = t.FilledBefore + t.Executed > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed;
                break;
        }
        ObserveNativeQuantity(o, previousFilled, previousState, previousStatus);
        if (final && a.ActualOrderId == 0 && a.Status != "FINISHED" && t.Busy == 0 && !t.ReplacementPending)
            RecordNativeTerminal(o);
    }

    /// <summary>
    /// The handle's conditional order triggered: from now on the handle follows the regular order it placed. If a list
    /// already showed that order as one of unknown origin (Binance's lists may disagree for a moment), that second handle
    /// is dropped: it was this order.
    /// </summary>
    private void Follow(Tracked t, long orderId)
    {
        _byAlgoId.Remove(t.AlgoId);
        _finishedAlgo[t.AlgoId] = DateTime.UtcNow;
        t.AlgoId = 0;
        t.AlgoStatus = "";
        t.NotFound = 0;
        if (_byId.TryGetValue(orderId, out var shown) && shown != t)
        {
            _byId.Remove(orderId);
            _byOrder.Remove(shown.Order);
            shown.Order.State = OrderStates.Done;
            shown.Order.Status = OrderStatus.Canceled;
        }

        _finished.Remove(orderId);
        Bind(t, orderId);
    }

    /// <summary>The order was canceled or expired at Binance; a Modify that lost its replacement reports Rejected.</summary>
    private void FinishCanceled(Tracked t)
    {
        if (t.ReplaceFailure is null) Finish(t, OrderStates.Done, OrderStatus.Canceled);
        else Finish(t, OrderStates.Failed, OrderStatus.Rejected);
    }

    private void Bind(Tracked t, long id)
    {
        t.Id = id;
        t.SubmissionUnknown = false;
        t.Order.ExchangeOrderId = id.ToString(CultureInfo.InvariantCulture);
        _byId[id] = t;
        // Regular order numbers are symbol-scoped; the transport and handle keep Binance's raw number.
        BindNative(t, new NativeOrderIdentity("binance:order", Instrument.Symbol + ":" + t.Order.ExchangeOrderId));
    }

    private void BindAlgo(Tracked t, long algoId)
    {
        t.AlgoId = algoId;
        t.ConditionalParent = new NativeOrderIdentity("binance:algo", algoId.ToString(CultureInfo.InvariantCulture));
        t.SubmissionUnknown = false;
        t.Order.ExchangeOrderId = AlgoPrefix + algoId.ToString(CultureInfo.InvariantCulture);
        _byAlgoId[algoId] = t;
        BindNative(t, new NativeOrderIdentity("binance:algo", algoId.ToString(CultureInfo.InvariantCulture)));
    }

    private void BindNative(Tracked tracked, NativeOrderIdentity native)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(tracked.Order.OwnerId))
        {
            tracked.Order.ContextId = OrderContext.Descriptor.Id;
            tracked.Order.NativeIdentity = native;
            return;
        }
        string? error = tracked.Intent is { } intent && intent.Id == tracked.Order.IntentId
            ? PersistNativeBinding(intent, native)
            : OrderContext.BindNative(tracked.Order, native, notify: false);
        tracked.Order.NativeIdentity = native;
        if (error != null) _ownershipFault = "Binance accepted order; ownership persistence failed: " + error;
        else _ownershipChanged = true;
    }

    // Called under the disposal fence. Only Core can recover an exact persisted dispatched client identity.
    private Tracked RecoverRegular(BinanceOpenOrder native)
    {
        var order = RecoverRegularOrder(native, out var intent);
        return RecoveredHandle(order, intent);
    }

    private Order RecoverRegularOrder(BinanceOpenOrder native, out PreparedOrderIntent? intent)
    {
        var order = new Order
        {
            Security = Position.Security, Direction = native.Side == "BUY" ? OrderDirections.Buy : OrderDirections.Sell,
            Type = TypeOf(native.Type), Price = native.Price, TriggerPrice = native.StopPrice, ReduceOnly = native.ReduceOnly,
            QuantityToFill = native.Quantity, Unfilled = native.Quantity,
            State = OrderStates.Active, Status = OrderStatus.Placed
        };
        intent = null;
        if (!_disposed && native.OrderId > 0 && native.Symbol == Instrument.Symbol && (native.Side is "BUY" or "SELL"))
        {
            var identity = new NativeOrderIdentity("binance:order", native.Symbol + ":" + native.OrderId.ToString(CultureInfo.InvariantCulture));
            intent = OrderContext.RecoverNativeOrder(order, native.ClientOrderId, identity, Instrument.Key, native.Symbol,
                notify: false, allowClientRecovery: native.Type is "LIMIT" or "MARKET" ||
                    (!_settings.Futures && native.Type == "STOP_LOSS"));
        }
        if (intent != null) _ownershipChanged = true;
        return order;
    }

    private Tracked RecoverAlgo(BinanceAlgoOrder native)
    {
        var order = new Order
        {
            Security = Position.Security, Direction = native.Side == "BUY" ? OrderDirections.Buy : OrderDirections.Sell,
            Type = TypeOf(native.Type), Price = native.Price, TriggerPrice = native.TriggerPrice, ReduceOnly = native.ReduceOnly,
            QuantityToFill = native.Quantity, Unfilled = native.Quantity, State = OrderStates.Active, Status = OrderStatus.Placed
        };
        PreparedOrderIntent? intent = null;
        if (!_disposed && _settings.Futures && native.AlgoId > 0 &&
            native.Symbol == Instrument.Symbol && (native.Side is "BUY" or "SELL"))
        {
            var identity = new NativeOrderIdentity("binance:algo", native.AlgoId.ToString(CultureInfo.InvariantCulture));
            intent = OrderContext.RecoverNativeOrder(order, native.ClientAlgoId, identity, Instrument.Key, native.Symbol,
                notify: false, allowClientRecovery: native.Type == "STOP_MARKET");
        }
        if (intent == null) return new Tracked(order, own: false);
        _ownershipChanged = true;
        return RecoveredHandle(order, intent);
    }

    private Tracked RecoveredHandle(Order order, PreparedOrderIntent? intent)
    {
        decimal? predecessorFilled = intent?.PredecessorFilled;
        decimal filledBefore = predecessorFilled ?? 0;
        order.QuantityToFill += filledBefore;
        var tracked = new Tracked(order, own: intent != null)
        {
            Intent = intent, ClientId = intent?.ClientOrderId ?? "", FilledBefore = filledBefore,
            BaselinePending = intent?.IsReplacement == true && predecessorFilled == null,
            BaselineCursor = intent?.PredecessorId
        };
        if (tracked.BaselinePending) _baselineRecovery.Enqueue(tracked);
        return tracked;
    }

    private void PublishOwnershipObservations()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_ownershipChanged)
            {
                _publications.Enqueue(new(Ownership: true));
                _ownershipChanged = false;
            }
            if (_publishing) return;
            _publishing = true;
            _publisherThread = Environment.CurrentManagedThreadId;
        }
        try
        {
            while (true)
            {
                ExecutionPublication publication;
                lock (_gate)
                {
                    if (_disposed || (!_executionChanged && _publications.Count == 0 && !_accountChanged))
                    {
                        _publishing = false;
                        _publisherThread = 0;
                        Monitor.PulseAll(_gate);
                        return;
                    }
                    if (_executionChanged)
                    {
                        _executionChanged = false;
                        publication = new(Executions: true);
                    }
                    else if (_publications.Count > 0) publication = _publications.Dequeue();
                    else
                    {
                        _accountChanged = false;
                        publication = new(Account: true);
                    }
                }
                if (Executions is ExecutionLog log)
                {
                    if (publication.Executions) log.NotifyChanged();
                }
                if (!_disposed && publication.Quantity is { } quantity) OrderContext.NotifyQuantityChanged(quantity);
                if (!_disposed && publication.Ownership) OrderContext.NotifyOwnershipChanged();
                if (!_disposed && publication.Account) Changed?.Invoke();
            }
        }
        catch
        {
            lock (_gate)
            {
                _publishing = false;
                _publisherThread = 0;
                Monitor.PulseAll(_gate);
            }
            throw;
        }
    }

    private void PublishAccountChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _accountChanged = true;
        }
        PublishOwnershipObservations();
    }

    /// <summary>Gives the handle its final state and stops following it.</summary>
    private void Finish(Tracked t, OrderStates state, OrderStatus status)
    {
        t.Order.State = state;
        t.Order.Status = status;
        RejectQueuedReprice(t, "The order became terminal before the queued price update.");
        _byOrder.Remove(t.Order);
        if (t.ClientId.Length > 0) _byClient.Remove(t.ClientId);
        if (t.AlgoId != 0)
        {
            _byAlgoId.Remove(t.AlgoId);
            _finishedAlgo[t.AlgoId] = DateTime.UtcNow;
        }

        if (t.Id == 0) return;
        _byId.Remove(t.Id);
        _finished[t.Id] = DateTime.UtcNow;
    }

    /// <summary>
    /// Publishes the working handles as <see cref="ActiveOrders"/>: in Binance's listing order, regular orders first and
    /// then conditional ones, then the rest.
    /// </summary>
    private void Publish()
    {
        lock (_gate)
        {
            var list = new List<Order>(_byOrder.Count);
            foreach (var id in _listed)
                if (_byId.TryGetValue(id, out var t) && t.Order.State == OrderStates.Active) list.Add(t.Order);
            foreach (var id in _listedAlgo)
                if (_byAlgoId.TryGetValue(id, out var t) && t.Order.State == OrderStates.Active && !list.Contains(t.Order)) list.Add(t.Order);
            foreach (var t in _byOrder.Values)
                if (t.Order.State == OrderStates.Active && !list.Contains(t.Order)) list.Add(t.Order);
            _orders = list;
        }
        PublishOwnershipObservations();
    }

    /// <summary>Runs an operation on a handle after the ones already queued on it.</summary>
    private void Enqueue(Tracked t, Func<Task> operation)
    {
        lock (_gate) t.Last = t.Last.ContinueWith(_ => operation(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
    private void SetClientIdentity(Tracked t, PreparedOrderIntent intent)
    {
        if (t.ClientId.Length > 0) _byClient.Remove(t.ClientId); // Retire active lookup only; OrderContext retains durable lineage.
        t.Intent = intent;
        t.ClientId = intent.ClientOrderId;
        t.SubmissionUnknown = false; // an in-flight send is serialized; only an ambiguous result starts client-ID recovery
        t.ConditionalParent = null;
        _byClient[t.ClientId] = t;
    }

    private bool HasUnknownSubmission()
    {
        foreach (var t in _byOrder.Values)
            if (t.SubmissionUnknown) return true;
        return false;
    }

    private async Task<string?> ResolveSubmissionsAsync(CancellationToken ct)
    {
        List<Tracked>? pending = null;
        lock (_gate)
        {
            foreach (var t in _byOrder.Values)
                if (t.SubmissionUnknown && t.Busy == 0 && t.Last.IsCompleted)
                    (pending ??= new List<Tracked>()).Add(t);
            if (pending == null) return HasUnknownSubmission() ? "Binance submission outcome remains unknown." : null;
        }
        string? error = null;
        foreach (var t in pending)
        {
            string clientId;
            lock (_gate) clientId = t.ClientId;
            BinanceOpenOrder? regular = null;
            BinanceAlgoOrder? algo = null;
            try
            {
                if (IsConditional(t.Order.Type)) algo = await _client.GetAlgoByClientAsync(Instrument.Symbol, clientId, ct).ConfigureAwait(false);
                else regular = await _client.GetOrderByClientAsync(Instrument.Symbol, clientId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = ex.Message;
            }

            lock (_gate)
            {
                if (!_byOrder.ContainsKey(t.Order) || t.ClientId != clientId || !t.SubmissionUnknown || t.Busy > 0) continue;
                if (regular != null)
                {
                    Bind(t, regular.OrderId);
                    Apply(t, regular);
                }
                else if (algo != null)
                {
                    BindAlgo(t, algo.AlgoId);
                    ApplyAlgo(t, algo);
                }
                else
                {
                    // "Not found" is not evidence that a timed-out submission cannot arrive later.
                    error ??= "Binance submission outcome remains unknown; awaiting exact client identity reconciliation.";
                }
            }
        }

        lock (_gate)
            if (HasUnknownSubmission())
                error ??= "Binance submission outcome remains unknown.";
        return error;
    }

    private void QueueDeferredCancels()
    {
        lock (_gate)
        {
            foreach (var t in _byOrder.Values)
            {
                if (!t.CancelRequested || t.CancelQueued || !t.Accepted || t.SubmissionUnknown || t.Busy > 0) continue;
                t.CancelQueued = true;
                Enqueue(t, () => CancelAsync(t));
            }
        }
    }


    public Order Submit(PreparedOrderIntent intent)
    {
        lock (_gate)
            if (_byClient.TryGetValue(intent.ClientOrderId, out var existing)) return existing.Order;
        var request = intent.Request;
        var dir = request.Direction;
        var type = request.Type;
        var qty = BinanceAccountClient.NormalizeWireNumber(request.Quantity);
        var price = request.Price;
        var reduceOnly = request.ReduceOnly;
        var order = new Order { Security = Position.Security, Direction = dir, Type = type, QuantityToFill = qty, Unfilled = qty, Price = price, TriggerPrice = price, Comment = request.Comment, State = OrderStates.Active, Status = OrderStatus.Placed, ReduceOnly = reduceOnly };
        var bindingError = request.Instrument == Instrument.Key
            ? BindOwnedOrder(order, intent) : "The prepared instrument does not match this broker.";
        if (bindingError != null)
        {
            order.State = OrderStates.Failed;
            order.Status = OrderStatus.Rejected;
            LastError = bindingError;
            Changed?.Invoke();
            return order;
        }
        var t = new Tracked(order, own: true);
        decimal px = BinanceAccountClient.NormalizeWireNumber(Instrument.RoundPrice(price));
        lock (_gate)
        {
            if (_byClient.TryGetValue(intent.ClientOrderId, out var existing)) return existing.Order;
            _byOrder[order] = t;
            SetClientIdentity(t, intent);
            t.Last = Task.Run(() => SubmitAsync(t, type, qty, px, reduceOnly));
        }

        Publish();
        return order;
    }

    private async Task SubmitAsync(Tracked t, OrderTypes type, decimal qty, decimal price, bool reduceOnly)
    {
        var order = t.Order;
        BinanceOpenOrder? placed = null;
        BinanceAlgoOrder? placedAlgo = null;
        string? error = null;
        bool rejected = false;
        bool submissionStarted = false;
        try
        {
            await WaitForNativeReplayAsync().ConfigureAwait(false);
            Task<BinanceOpenOrder>? placement = null;
            Task<BinanceAlgoOrder>? algoPlacement = null;
            lock (_gate)
            {
                if (_disposed) return;
                var dispatchError = StartOwnedDispatch(t.Intent!, Instrument.Symbol, actualQuantity: qty, actualPrice: price);
                if (dispatchError != null) throw new InvalidOperationException(dispatchError);
                _sending++;
                submissionStarted = true;
                if (IsConditional(type)) algoPlacement = _client.PlaceAlgoOrderAsync(Instrument.Symbol, order.Direction, qty, price, reduceOnly, _cts.Token, t.ClientId);
                else placement = _client.PlaceOrderAsync(Instrument.Symbol, order.Direction, type, qty, price, reduceOnly, _cts.Token, t.ClientId);
            }
            if (algoPlacement != null) placedAlgo = await algoPlacement.ConfigureAwait(false);
            else placed = await placement!.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            rejected = !submissionStarted || ex is BinanceRejectedException;
        }

        lock (_gate)
        {
            if (submissionStarted) _sending--;
            if (_disposed) return;
            if (placed != null)
            {
                Bind(t, placed.OrderId);
                Apply(t, placed);
            }
            else if (placedAlgo != null)
            {
                BindAlgo(t, placedAlgo.AlgoId);
                ApplyAlgo(t, placedAlgo);
            }
            else if (rejected)
            {
                Finish(t, OrderStates.Failed, OrderStatus.Rejected);
                RecordRejected(t.Intent!, error ?? "Binance rejected the order.");
            }
            else if (!t.Accepted)
            {
                t.SubmissionUnknown = true;
                OrdersReady = false;
            }
        }

        LastError = error;
        DispatchQueuedReprices();
        Publish();
        QueueDeferredCancels();
        PublishAccountChanged();
    }

    /// <summary>USD-M futures stops are conditional orders, which Binance takes only through its Algo Order API.</summary>
    private bool IsConditional(OrderTypes type) => _settings.Futures && type == OrderTypes.Stop;


    public bool IsReplacementPending(Order order)
    {
        lock (_gate)
            return _byOrder.TryGetValue(order, out var t) &&
                (!t.Accepted || t.Busy > 0 || t.SubmissionUnknown || t.BaselinePending || t.ReplaceFailure != null || t.ReplacementPending || t.QueuedReprice != null);
    }

    private static void ConfirmReplacement(Tracked t, decimal total, decimal price)
    {
        if (!t.ReplacementPending || t.Busy > 0 || t.SubmissionUnknown || t.ReplaceFailure != null || t.CancelRequested || total <= 0 || price <= 0) return;
        if (!t.RestoreReplacement && (total != t.ReplacementTotal || price != t.ReplacementPrice)) return;
        t.ReplacementPending = false;
        t.RestoreReplacement = false;
    }

    /// <summary>
    /// Binance does not modify price or quantity: the order is canceled and a new one sent, which the same handle (returned)
    /// then follows. As in Rithmic, the handle's total becomes its filled part plus the prepared remaining quantity, and the
    /// new price and quantity show on it at once. When the cancel is refused because the order is no longer open (it just
    /// filled), nothing new is sent and the handle reports what happened. A handle that is no longer working is left as it is.
    /// </summary>
    public Order Replace(Order order, PreparedOrderIntent intent)
    {
        lock (_gate)
        {
            if (intent.Id == order.IntentId) return order;
            var validationError = OrderContext.Validate(intent, order);
            if (validationError == null && (intent.Request.Instrument != Instrument.Key ||
                intent.Request.Direction != order.Direction || intent.Request.Type != order.Type || intent.Request.ReduceOnly != order.ReduceOnly))
                validationError = "A native amendment cannot change the instrument, direction, type or reduce-only identity.";
            if (validationError != null)
            {
                LastError = validationError;
                return order;
            }
            if (!_byOrder.TryGetValue(order, out var t))
            {
                LastError = "The order is no longer available for replacement.";
                RecordRejected(intent, "The order is no longer available for replacement.");
                return order;
            }
            if (t.CancelRequested || order.State != OrderStates.Active ||
                (!intent.IsPriceOnlyReplacement && (t.SubmissionUnknown || t.BaselinePending || !t.Accepted || t.Busy > 0 ||
                    t.ReplacementPending || t.ReplaceFailure != null || t.QueuedReprice != null)))
            {
                LastError = "Cannot replace an order while its submission or previous replacement outcome is unresolved.";
                RecordRejected(intent, "Cannot replace an order while its submission or previous replacement outcome is unresolved.");
                return order;
            }
            if (intent.IsPriceOnlyReplacement)
            {
                RejectQueuedReprice(t, "A later prepared price update superseded this unsent price update.");
                t.QueuedReprice = intent;
            }
            else StartReplacement(t, intent);
        }

        DispatchQueuedReprices();
        return order;
    }

    private void StartReplacement(Tracked tracked, PreparedOrderIntent intent)
    {
        var order = tracked.Order;
        var predecessor = new Order
        {
            Security = order.Security, Portfolio = order.Portfolio, Direction = order.Direction, Type = order.Type,
            ContextId = order.ContextId, OwnerId = order.OwnerId, IntentId = order.IntentId,
            LogicalOrderId = order.LogicalOrderId, ClientOrderId = order.ClientOrderId,
            NativeIdentity = order.NativeIdentity, ExchangeOrderId = order.ExchangeOrderId, ReduceOnly = order.ReduceOnly,
            QuantityToFill = order.QuantityToFill, Unfilled = order.Unfilled
        };
        if (BindOwnedOrder(order, intent) is { } bindingError)
        {
            LastError = bindingError;
            RecordRejected(intent, bindingError);
            return;
        }
        var type = order.Type == OrderTypes.Stop ? OrderTypes.Stop : OrderTypes.Limit;
        decimal price = BinanceAccountClient.NormalizeWireNumber(Instrument.RoundPrice(intent.Request.Price));
        decimal remaining = BinanceAccountClient.NormalizeWireNumber(intent.IsPriceOnlyReplacement ? order.Unfilled : intent.Request.Quantity);
        decimal total = order.Filled + remaining;
        tracked.ReplacementPending = true;
        tracked.ReplacementPrice = price;
        tracked.ReplacementTotal = total;
        tracked.RestoreReplacement = false;
        tracked.Busy++;
        if (type == OrderTypes.Stop) order.TriggerPrice = price;
        else order.Price = price;
        order.QuantityToFill = total;
        order.Unfilled = Math.Max(0, remaining);
        Enqueue(tracked, () => ReplaceAsync(tracked, intent, predecessor, type, price, total));
    }

    private void RejectQueuedReprice(Tracked tracked, string reason)
    {
        if (tracked.QueuedReprice is not { } intent) return;
        tracked.QueuedReprice = null;
        RecordRejected(intent, reason);
    }

    private void DispatchQueuedReprices()
    {
        lock (_gate)
            foreach (var tracked in _byOrder.Values)
            {
                if (tracked.QueuedReprice is not { } intent || tracked.Order.State != OrderStates.Active || tracked.CancelRequested ||
                    !tracked.Accepted || tracked.Busy > 0 || tracked.SubmissionUnknown || tracked.BaselinePending || tracked.ReplaceFailure != null || tracked.ReplacementPending) continue;
                tracked.QueuedReprice = null;
                if (tracked.Order.Unfilled <= 0 || (tracked.Order.Type == OrderTypes.Stop ? tracked.Order.TriggerPrice : tracked.Order.Price) == Instrument.RoundPrice(intent.Request.Price))
                {
                    RecordRejected(intent, "The prepared price update requires no native replacement.");
                    continue;
                }
                StartReplacement(tracked, intent);
            }
    }

    private async Task ReplaceAsync(Tracked t, PreparedOrderIntent intent, Order predecessor, OrderTypes type, decimal price, decimal total)
    {
        var order = t.Order;
        long oldId, oldAlgoId;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_byOrder.ContainsKey(order) || !t.Accepted || t.SubmissionUnknown)
            {
                t.Busy--; // finished meanwhile, or never accepted
                RecordRejected(intent, "The predecessor finished or its acceptance became unresolved before replacement.");
                return;
            }

            oldId = t.Id;
            oldAlgoId = t.AlgoId;
        }

        string? startError;
        Task? algoCancellation = null;
        Task<BinanceOpenOrder>? cancellation = null;
        try
        {
            await WaitForNativeReplayAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) return;
                decimal remaining = BinanceAccountClient.NormalizeWireNumber(Math.Max(0, total - t.FilledBefore - t.Executed));
                startError = StartOwnedDispatch(intent, Instrument.Symbol, actualQuantity: remaining, actualPrice: price);
                if (startError == null)
                {
                    if (oldAlgoId != 0) algoCancellation = _client.CancelAlgoOrderAsync(oldAlgoId, _cts.Token);
                    else cancellation = _client.CancelOrderAsync(Instrument.Symbol, oldId, _cts.Token);
                }
            }
        }
        catch (Exception ex) { startError = ex.Message; }
        if (startError != null)
        {
            lock (_gate)
            {
                if (_disposed) return;
                t.Busy--;
                t.RestoreReplacement = true;
                RejectUnsentReplacement(intent, order, predecessor, startError);
                LastError = startError;
            }
            Publish();
            PublishAccountChanged();
            return; // An unjournaled replacement must not cancel the accepted protection.
        }

        decimal? canceled = null; // once the old order is known to be canceled: what it had executed
        BinanceOpenOrder? found = null;
        BinanceAlgoOrder? foundAlgo = null;
        Exception? cancelError = null;
        try
        {
            if (oldAlgoId != 0)
            {
                await algoCancellation!.ConfigureAwait(false);
                canceled = 0; // canceled before it triggered: nothing executed
            }
            else
            {
                canceled = (await cancellation!.ConfigureAwait(false)).Executed;
            }
        }
        catch (Exception ex)
        {
            cancelError = ex;
            LastError = ex.Message;
        }

        // Refused as unknown (-2011): the order is no longer open (filled, or canceled elsewhere) and is not replaced. Any
        // other failure (timeout, 5xx) may still have canceled it, so Binance is asked before deciding. A conditional order
        // is always asked about: Binance documents no error code for one that can no longer be canceled.
        bool notOpen = oldAlgoId == 0 && cancelError is BinanceRejectedException { Code: -2011 };
        if (cancelError != null && !notOpen && !_cts.IsCancellationRequested)
        {
            try
            {
                if (oldAlgoId != 0) foundAlgo = await _client.GetAlgoOrderAsync(Instrument.Symbol, oldAlgoId, _cts.Token).ConfigureAwait(false);
                else found = await _client.GetOrderAsync(Instrument.Symbol, oldId, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // still unknown: decided below
            }

            if (found is { Status: "CANCELED" or "EXPIRED" or "EXPIRED_IN_MATCH" })
            {
                canceled = found.Executed;
                found = null;
            }
            else if (foundAlgo is { ActualOrderId: 0, Status: "CANCELED" or "EXPIRED" })
            {
                canceled = 0;
                foundAlgo = null;
            }
        }

        // a conditional order that triggered or ended can no longer be canceled: no doubt about the cancel remains
        bool pastCancel = notOpen || foundAlgo is { ActualOrderId: not 0 } or { Status: "TRIGGERED" or "FINISHED" or "REJECTED" };

        decimal filled = 0, rest = 0;
        lock (_gate)
        {
            if (_disposed) return;
            decimal previousFilled = order.Filled;
            var previousState = order.State;
            var previousStatus = order.Status;
            if (canceled is { } executed)
            {
                t.Executed = Math.Max(t.Executed, executed);
                ObservePriceCoverage(order, t.FilledBefore + t.Executed);
                filled = t.FilledBefore + t.Executed;
                predecessor.QuantityToFill = Math.Max(predecessor.QuantityToFill, filled);
                predecessor.Unfilled = predecessor.QuantityToFill - filled;
                predecessor.State = OrderStates.Done;
                predecessor.Status = predecessor.Unfilled == 0 ? OrderStatus.Filled : OrderStatus.Canceled;
                rest = total - filled;
                order.QuantityToFill = Math.Max(order.QuantityToFill, filled);
                order.Unfilled = Math.Max(0, order.QuantityToFill - filled);
                if (filled > 0) order.Status = OrderStatus.PartlyFilled;
            }

            if (canceled == null || rest <= 0)
            {
                // Dispatch covers the cancellation too. Reject the child that was never submitted,
                // without claiming that an ambiguous predecessor cancellation has completed.
                RejectUnsentReplacement(intent, order, predecessor,
                    canceled != null ? "The predecessor cancellation completed, but no child quantity remained to submit."
                        : "No replacement child was submitted; predecessor cancellation still requires native reconciliation.");
                t.Busy--;
                if (canceled != null)
                {
                    // nothing is left to send: what the caller wants had already filled before the cancel
                    if (filled > 0)
                    {
                        order.QuantityToFill = filled;
                        order.Unfilled = 0;
                    }

                    Finish(t, OrderStates.Done, filled > 0 ? OrderStatus.Filled : OrderStatus.Canceled);
                    RecordNativeTerminal(predecessor);
                }
                else if (_byOrder.ContainsKey(order) && t.Id == oldId && t.AlgoId == oldAlgoId)
                {
                    // not replaced. After an unknown outcome the cancel may still be carried out later: if the order then
                    // turns out canceled, that is reported as a failed Modify
                    if (!pastCancel && cancelError is not BinanceRejectedException)
                    {
                        t.ReplaceFailure = cancelError!.Message;
                    }
                    else
                    {
                        // Definite failure is not complete until authoritative old/native values have been restored.
                        t.RestoreReplacement = true;
                    }

                    if (found != null) Apply(t, found); // still working (shown as it is at Binance again) or filled
                    if (foundAlgo != null) ApplyAlgo(t, foundAlgo); // still working, or triggered: then the order it placed is followed
                }
            }
            else
            {
                _sending++;
                SetClientIdentity(t, intent);
            }
            if (canceled != null) ObserveNativeQuantity(order, previousFilled, previousState, previousStatus);
        }

        if (canceled == null || rest <= 0)
        {
            DispatchQueuedReprices();
            Publish();
            PublishAccountChanged();
            return;
        }

        BinanceOpenOrder? placed = null;
        BinanceAlgoOrder? placedAlgo = null;
        string? error = null;
        bool rejected = false;
        bool submissionStarted = false;
        try
        {
            await WaitForNativeReplayAsync().ConfigureAwait(false);
            rest = BinanceAccountClient.NormalizeWireNumber(rest);
            Task<BinanceOpenOrder>? placement = null;
            Task<BinanceAlgoOrder>? algoPlacement = null;
            lock (_gate)
            {
                if (_disposed) return;
                var dispatchError = OrderContext.CheckDispatch(intent, rest);
                if (dispatchError != null) throw new InvalidOperationException(dispatchError);
                if (OrderContext.RecordPredecessorFilled(intent, filled) is { } baselineError)
                    throw new InvalidOperationException(baselineError);
                submissionStarted = true;
                if (IsConditional(type)) algoPlacement = _client.PlaceAlgoOrderAsync(Instrument.Symbol, order.Direction, rest, price, order.ReduceOnly, _cts.Token, t.ClientId);
                else placement = _client.PlaceOrderAsync(Instrument.Symbol, order.Direction, type, rest, price, order.ReduceOnly, _cts.Token, t.ClientId);
            }
            if (algoPlacement != null) placedAlgo = await algoPlacement.ConfigureAwait(false);
            else placed = await placement!.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            rejected = !submissionStarted || ex is BinanceRejectedException;
        }

        lock (_gate)
        {
            if (_disposed) return;
            _sending--;
            t.Busy--;
            if (oldAlgoId != 0)
            {
                _byAlgoId.Remove(oldAlgoId);
                _finishedAlgo[oldAlgoId] = DateTime.UtcNow;
                t.AlgoId = 0;
            }
            else
            {
                _byId.Remove(oldId);
                _finished[oldId] = DateTime.UtcNow;
                t.Id = 0;
            }

            t.FilledBefore = filled;
            t.Executed = 0;
            t.ReplaceFailure = null;
            order.Type = type;
            if (t.Busy == 0)
            {
                // no later Modify waiting (a waiting one keeps showing its own request)
                if (type == OrderTypes.Stop) order.TriggerPrice = price;
                else order.Price = price;
                order.QuantityToFill = filled + rest;
                t.ReplacementTotal = filled + rest;
            }

            order.Unfilled = Math.Max(0, order.QuantityToFill - filled);
            if (placed != null)
            {
                Bind(t, placed.OrderId);
                order.State = OrderStates.Active;
                order.Status = filled > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed;
                Apply(t, placed);
                RecordNativeTerminal(predecessor);
            }
            else if (placedAlgo != null)
            {
                BindAlgo(t, placedAlgo.AlgoId);
                order.State = OrderStates.Active;
                order.Status = filled > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed;
                ApplyAlgo(t, placedAlgo);
                RecordNativeTerminal(predecessor);
            }
            else if (rejected)
            {
                Finish(t, OrderStates.Failed, OrderStatus.Rejected);
                RecordRejected(intent, error ?? "Binance rejected the replacement.");
                RecordNativeTerminal(predecessor);
            }
            else
            {
                t.SubmissionUnknown = true;
                OrdersReady = false;
                order.ExchangeOrderId = "";
            }
        }

        LastError = error;
        DispatchQueuedReprices();
        Publish();
        QueueDeferredCancels();
        PublishAccountChanged();
    }

    public void Cancel(Order order)
    {
        Tracked? t;
        lock (_gate)
        {
            if (order.State is OrderStates.Done or OrderStates.Failed) return;
            _byOrder.TryGetValue(order, out t);
            if (t != null)
            {
                t.CancelRequested = true;
                RejectQueuedReprice(t, "Cancellation superseded the queued price update.");
            }
        }
        if (t != null)
        {
            QueueDeferredCancels();
            return;
        }

        // A detached cancellation handle (for example a saved restart identity) still routes by its native id.
        if (order.ExchangeOrderId.StartsWith(AlgoPrefix, StringComparison.Ordinal))
        {
            if (long.TryParse(order.ExchangeOrderId.AsSpan(AlgoPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var algoId))
                CancelUntracked(() => _client.CancelAlgoOrderAsync(algoId, _cts.Token));
            return;
        }

        if (!long.TryParse(order.ExchangeOrderId, out var id)) return;
        CancelUntracked(() => _client.CancelOrderAsync(Instrument.Symbol, id, _cts.Token));
    }

    private void CancelUntracked(Func<Task> cancel) => _ = Task.Run(async () =>
    {
        try { await cancel().ConfigureAwait(false); }
        catch (Exception ex) { LastError = ex.Message; }
        PublishAccountChanged();
    });

    private async Task CancelAsync(Tracked t)
    {
        long id, algoId;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_byOrder.ContainsKey(t.Order) || !t.Accepted || t.SubmissionUnknown)
            {
                t.CancelQueued = false;
                return;
            }
            id = t.Id;
            algoId = t.AlgoId;
            t.Busy++;
            t.CancelQueued = true;
            t.ReplaceFailure = null; // canceling it is what is wanted now
        }

        BinanceOpenOrder? canceled = null;
        bool algoCanceled = false, terminalObserved = false;
        try
        {
            if (algoId != 0)
            {
                await _client.CancelAlgoOrderAsync(algoId, _cts.Token).ConfigureAwait(false);
                algoCanceled = true;
            }
            else
            {
                bool terminal = false;
                if (t.Own && t.Order.Type == OrderTypes.Market)
                {
                    // Futures' placement ACK can still say NEW/executedQty=0 after the market order filled.
                    // Resolve that execution before sending a redundant cancel, without assuming all markets filled.
                    try
                    {
                        var native = await _client.GetOrderAsync(Instrument.Symbol, id, _cts.Token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            if (t.Id == id && _byOrder.ContainsKey(t.Order) && native != null) Apply(t, native);
                            terminalObserved = native?.Status is "FILLED" or "CANCELED" or "EXPIRED" or "EXPIRED_IN_MATCH" or "REJECTED";
                            terminal = !_byOrder.ContainsKey(t.Order);
                        }
                    }
                    catch (Exception ex) when (!_cts.IsCancellationRequested)
                    {
                        LastError = ex.Message; // A failed lookup is not proof that working exposure cannot be canceled.
                    }
                }
                if (!terminal) canceled = await _client.CancelOrderAsync(Instrument.Symbol, id, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message; // already finished at Binance (a conditional order: triggered), or not reached: the next poll tells
        }

        lock (_gate)
        {
            if (_disposed) return;
            t.Busy--;
            t.CancelQueued = false;
            if (canceled != null && t.Id == id && _byOrder.ContainsKey(t.Order)) Apply(t, canceled);
            else if (algoCanceled && t.AlgoId == algoId && _byOrder.ContainsKey(t.Order)) FinishCanceled(t); // before it triggered: nothing more filled
            if ((canceled != null || algoCanceled || terminalObserved) &&
                t.Order.State is (OrderStates.Done or OrderStates.Failed) && !t.ReplacementPending)
                RecordNativeTerminal(t.Order);
        }

        Publish();
        PublishAccountChanged();
    }

    public void CancelAt(decimal price)
    {
        foreach (var o in ActiveOrders.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price).ToList()) Cancel(o);
    }

    public void CancelAll()
    {
        lock (_gate)
            foreach (var t in _byOrder.Values)
            {
                t.CancelRequested = true;
                RejectQueuedReprice(t, "Cancellation superseded the queued price update.");
            }
        _ = Task.Run(async () =>
        {
            var errors = new List<string>();
            try { await _client.CancelAllAsync(Instrument.Symbol, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex.Message); }

            // Conditional orders have a separate route. Local pending sends still require cancellation after binding.
            if (_settings.Futures)
            {
                try { await _client.CancelAllAlgoOrdersAsync(Instrument.Symbol, _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ex.Message); }
            }

            if (errors.Count > 0) LastError = string.Join("; ", errors);
            QueueDeferredCancels();
            PublishAccountChanged();
        });
    }


    private async Task WaitForNativeReplayAsync()
    {
        while (!_nativeReplayReady)
        {
            await Task.Delay(100, _cts.Token).ConfigureAwait(false);
        }
        _cts.Token.ThrowIfCancellationRequested();
    }

    // Dispose shares this gate with every journal mutation, including late transport callbacks.
    private string? BindOwnedOrder(Order order, PreparedOrderIntent intent)
    {
        lock (_gate) return _disposed ? "Binance broker has been disposed." : OrderContext.BindOrder(order, intent);
    }

    private string? StartOwnedDispatch(PreparedOrderIntent intent, string instrument, string exchange = "", decimal? actualQuantity = null, decimal? actualPrice = null)
    {
        lock (_gate) return _disposed ? "Binance broker has been disposed." : OrderContext.StartDispatch(intent, instrument, exchange, actualQuantity, actualPrice);
    }

    private string? PersistNativeBinding(PreparedOrderIntent intent, NativeOrderIdentity native)
    {
        lock (_gate) return _disposed ? null : OrderContext.BindNative(intent, native, notify: false);
    }

    private void ObserveNativeQuantity(Order order, decimal previousFilled, OrderStates previousState, OrderStatus previousStatus)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                ObservePriceCoverage(order, order.Filled);
                if (order.Filled == previousFilled && order.State == previousState && order.Status == previousStatus) return;
                if (OrderContext.CaptureQuantityObservation(order, Instrument.Symbol, Instrument.PointValue) is { } observation)
                    _publications.Enqueue(new(Quantity: observation));
            }
            catch (Exception ex)
            {
                _ownershipFault = "Binance native quantity projection is unresolved: " + ex.Message;
                UpdateProducerAvailability();
            }
        }
    }

    private void RecordRejected(PreparedOrderIntent intent, string reason)
    {
        lock (_gate)
            if (!_disposed) OrderContext.RecordRejected(intent, reason);
    }

    private void RejectUnsentReplacement(PreparedOrderIntent intent, Order order, Order predecessor, string reason)
    {
        RecordRejected(intent, reason);
        if (OrderContext.RestoreAcceptedBinding(order, predecessor) is { } bindingError)
        {
            _ownershipFault = "The accepted Binance predecessor could not be restored after an unsent replacement: " + bindingError;
            UpdateProducerAvailability();
        }
    }

    private void RecordNativeTerminal(Order order)
    {
        if (string.IsNullOrEmpty(order.OwnerId)) return;
        if (OrderContext.RecordNativeTerminal(order) is { } error)
        {
            _ownershipFault = "Binance terminal ownership evidence remains unresolved: " + error;
            UpdateProducerAvailability();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _publications.Clear();
            _executionChanged = false;
            _accountChanged = false;
            _baselineRecovery.Clear();
            PositionReady = false;
            OrdersReady = false;
            _nativeReplayReady = false;
        }
        _cts.Cancel();
        lock (_gate)
            while (_publishing && _publisherThread != Environment.CurrentManagedThreadId)
                Monitor.Wait(_gate);
        bool changed;
        lock (_gate)
            changed = Executions is ExecutionLog log && log.SetProducerAvailability(_executionProducer, null, notify: false);
        _client.Dispose();
        if (changed) ((ExecutionLog)Executions).NotifyChanged();
        Changed?.Invoke();
    }
}
