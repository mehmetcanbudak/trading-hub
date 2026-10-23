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
public sealed record BinanceOpenOrder(long OrderId, string Symbol, string Side, string Type, decimal Price, decimal StopPrice, decimal Quantity, decimal Executed, string Status, bool ReduceOnly = false);

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

            throw new BinanceRejectedException($"Binance rejected operation ({(int)resp.StatusCode}): {msg}", code);
        }

        return JsonDocument.Parse(body);
    }

    private static decimal D(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? decimal.Parse(v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture) : 0m;

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static BinanceOpenOrder ParseOrder(JsonElement o, string symbol) => new(
        o.GetProperty("orderId").GetInt64(), symbol, S(o, "side"), S(o, "type"),
        D(o, "price"), D(o, "stopPrice"), D(o, "origQty"), D(o, "executedQty"), S(o, "status"),
        o.TryGetProperty("reduceOnly", out var ro) && ro.ValueKind == JsonValueKind.True);

    private static string Fmt(decimal v) => v.ToString("0.############", CultureInfo.InvariantCulture);

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
                decimal free = D(b, "free"), locked = D(b, "locked");
                if (free + locked > 0) list.Add(new BinanceBalance(b.GetProperty("asset").GetString()!, free + locked, free, 0));
            }
        }

        return list;
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

    /// <summary>
    /// Sends an order (type: MARKET, LIMIT or STOP_MARKET in futures / STOP_LOSS in spot) and returns Binance's answer:
    /// always the order id; status and quantities unless the answer is a bare acknowledgement (spot stops).
    /// </summary>
    public async Task<BinanceOpenOrder> PlaceOrderAsync(string symbol, OrderDirections dir, OrderTypes type, decimal qty, decimal price, bool reduceOnly, CancellationToken ct)
    {
        var sb = new StringBuilder($"symbol={symbol}&side={(dir == OrderDirections.Buy ? "BUY" : "SELL")}&quantity={Fmt(qty)}");
        switch (type)
        {
            case OrderTypes.Market:
                sb.Append("&type=MARKET");
                break;
            case OrderTypes.Limit:
                sb.Append($"&type=LIMIT&timeInForce=GTC&price={Fmt(price)}");
                break;
            case OrderTypes.Stop:
                sb.Append(_s.Futures ? $"&type=STOP_MARKET&stopPrice={Fmt(price)}" : $"&type=STOP_LOSS&stopPrice={Fmt(price)}");
                break;
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
/// canceled, expired or rejected, with the filled quantity). Binance cannot modify an order, so Modify and Replace cancel
/// it and send a new one, which the same handle then follows. Operations on one handle run one after another.
/// </remarks>
public sealed class BinanceBroker : ITradingAccount, IDisposable
{
    private readonly BinanceAccountClient _client;
    private readonly BinanceAccountSettings _settings;
    private readonly InstrumentData _data;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    // Handles not finished yet, by object and by current exchange order id.
    private readonly Dictionary<Order, Tracked> _byOrder = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, Tracked> _byId = new();

    // Exchange ids that finished or were replaced, and when: an open-order list fetched around then may still show them.
    private readonly Dictionary<long, DateTime> _finished = new();
    private static readonly TimeSpan FinishedMemory = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CancelDoubt = TimeSpan.FromSeconds(60); // an unanswered cancel may still be carried out
    private List<long> _listed = new();
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

        /// <summary>Current exchange order id (0 until Binance answers the submission).</summary>
        public long Id;

        /// <summary>Filled by this handle's earlier exchange orders (Modify cancels and sends a new one).</summary>
        public decimal FilledBefore;

        /// <summary>Executed quantity of the current exchange order; it never goes back.</summary>
        public decimal Executed;

        /// <summary>
        /// Cancels or replaces in flight or queued: the order missing from the open list is expected, not final, and a
        /// queued Modify's price and quantity stay on the handle.
        /// </summary>
        public int Busy;

        /// <summary>
        /// Error of a Modify whose cancel had an unknown outcome and that sent no replacement, and when: if the order turns
        /// out canceled, the handle is reported Rejected with it instead of silently Canceled. Binance may carry out such a
        /// cancel late, so it is kept for a while even when the order is seen working.
        /// </summary>
        public string? ReplaceFailure;

        public DateTime ReplaceFailedAt;

        /// <summary>Consecutive lookups for which Binance answered that the order does not exist.</summary>
        public int NotFound;

        /// <summary>The last operation queued on this handle; the next one waits for it.</summary>
        public Task Last = Task.CompletedTask;
    }

    public BinanceBroker(InstrumentData data, BinanceAccountSettings settings, string name)
    {
        _data = data;
        _settings = settings;
        _client = new BinanceAccountClient(settings);
        Name = name;
        Position = new Position { Security = new Security { Code = data.Instrument.Symbol, SecurityId = data.Instrument.Key } };
        _ = Task.Run(PollLoop);
    }

    public string Name { get; }
    public bool IsReal => !_settings.Testnet;
    public decimal LastPrice => _data.LastPrice;
    public Instrument Instrument => _data.Instrument;
    public Position Position { get; }
    public IEnumerable<Order> ActiveOrders => _orders.Where(o => o.State == OrderStates.Active);
    public string? LastError { get; private set; }
    public event Action? Changed;

    private async Task PollLoop()
    {
        try { await _client.SyncTimeAsync(_cts.Token).ConfigureAwait(false); }
        catch { /* se reintenta en el bucle */ }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                string? lookupError = await RefreshOrdersAsync(_cts.Token).ConfigureAwait(false);

                if (_settings.Futures)
                {
                    var pos = (await _client.GetPositionsAsync(_cts.Token).ConfigureAwait(false)).FirstOrDefault(p => p.Symbol == Instrument.Symbol);
                    Position.Volume = pos?.Quantity ?? 0;
                    Position.AveragePrice = pos?.EntryPrice ?? 0;
                    Position.UnrealizedPnL = pos?.UnrealizedPnl ?? 0;
                }

                LastError = lookupError;
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // also a request that timed out (HttpClient reports it as a cancellation): reported, and polling goes on
                LastError = ex.Message;
                Changed?.Invoke();
            }

            try { await Task.Delay(_settings.PollInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Brings the handles up to date with Binance's open orders and looks up the final state of those that left the list.
    /// Returns the lookup error to report, if any (those orders are looked up again at the next poll).
    /// </summary>
    private async Task<string?> RefreshOrdersAsync(CancellationToken ct)
    {
        var open = await _client.GetOpenOrdersAsync(Instrument.Symbol, ct).ConfigureAwait(false);
        var gone = new List<(Tracked Handle, long Id)>();
        lock (_gate)
        {
            var listed = new List<long>(open.Count);
            foreach (var s in open)
            {
                if (_finished.ContainsKey(s.OrderId)) continue; // finished or replaced moments ago: a stale listing
                if (!_byId.TryGetValue(s.OrderId, out var t))
                {
                    if (_sending > 0) continue; // it may be one of ours whose answer has not arrived: next poll
                    t = new Tracked(new Order(), own: false);
                    _byOrder[t.Order] = t;
                    Bind(t, s.OrderId);
                }

                listed.Add(s.OrderId);
                t.NotFound = 0;
                if (t.Busy == 0) Apply(t, s);
            }

            _listed = listed;
            var forget = DateTime.UtcNow - FinishedMemory;
            foreach (var (id, at) in _finished)
                if (at < forget) _finished.Remove(id);
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
                else if (++t.NotFound >= 3)
                {
                    FinishCanceled(t); // Binance dropped it: canceled or expired without fills
                }

                if (t.Order.State == OrderStates.Failed && t.ReplaceFailure is { } why)
                    error = "Modify failed: the order was canceled but its replacement was not sent (" + why + ")";
            }
        }

        Publish();
        return error;
    }

    /// <summary>
    /// Copies Binance's view of the handle's current exchange order onto it; a final state finishes the handle. While a
    /// Modify is queued, the handle keeps showing its price and quantity until the order is final.
    /// </summary>
    private void Apply(Tracked t, BinanceOpenOrder s)
    {
        if (s.Status.Length == 0) return; // a bare acknowledgement (spot stops) adds nothing
        var o = t.Order;
        bool final = s.Status is "FILLED" or "CANCELED" or "EXPIRED" or "EXPIRED_IN_MATCH" or "REJECTED";
        if (t.Busy == 0 || final)
        {
            if (!t.Own)
            {
                o.Direction = s.Side == "BUY" ? OrderDirections.Buy : OrderDirections.Sell;
                o.Type = s.Type.Contains("STOP") ? OrderTypes.Stop : s.Type == "MARKET" ? OrderTypes.Market : OrderTypes.Limit;
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

            if (s.Quantity > 0) o.QuantityToFill = t.FilledBefore + s.Quantity;
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
                Finish(t, OrderStates.Failed, OrderStatus.Rejected);
                break;
            default: // NEW, PARTIALLY_FILLED (futures also NEW_INSURANCE, NEW_ADL): still working
                if (t.ReplaceFailure != null && DateTime.UtcNow - t.ReplaceFailedAt > CancelDoubt) t.ReplaceFailure = null; // that cancel never happened
                o.State = OrderStates.Active;
                o.Status = t.FilledBefore + t.Executed > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed;
                break;
        }
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
        t.Order.ExchangeOrderId = id.ToString(CultureInfo.InvariantCulture);
        _byId[id] = t;
    }

    /// <summary>Gives the handle its final state and stops following it.</summary>
    private void Finish(Tracked t, OrderStates state, OrderStatus status)
    {
        t.Order.State = state;
        t.Order.Status = status;
        _byOrder.Remove(t.Order);
        if (t.Id == 0) return;
        _byId.Remove(t.Id);
        _finished[t.Id] = DateTime.UtcNow;
    }

    /// <summary>Publishes the working handles as <see cref="ActiveOrders"/>: in Binance's listing order, then the rest.</summary>
    private void Publish()
    {
        lock (_gate)
        {
            var list = new List<Order>(_byOrder.Count);
            foreach (var id in _listed)
                if (_byId.TryGetValue(id, out var t) && t.Order.State == OrderStates.Active) list.Add(t.Order);
            foreach (var t in _byOrder.Values)
                if (t.Order.State == OrderStates.Active && !list.Contains(t.Order)) list.Add(t.Order);
            _orders = list;
        }
    }

    /// <summary>Runs an operation on a handle after the ones already queued on it.</summary>
    private void Enqueue(Tracked t, Func<Task> operation)
    {
        lock (_gate) t.Last = t.Last.ContinueWith(_ => operation(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private Order Send(OrderDirections dir, OrderTypes type, decimal qty, decimal price, string comment, bool reduceOnly = false)
    {
        var order = new Order { Direction = dir, Type = type, QuantityToFill = qty, Unfilled = qty, Price = price, TriggerPrice = price, Comment = comment, State = OrderStates.Active, Status = OrderStatus.Placed, ReduceOnly = reduceOnly };
        var t = new Tracked(order, own: true);
        decimal px = Instrument.RoundPrice(price);
        lock (_gate)
        {
            _byOrder[order] = t;
            _sending++;
            t.Last = Task.Run(() => SubmitAsync(t, type, qty, px, reduceOnly));
        }

        Publish();
        return order;
    }

    private async Task SubmitAsync(Tracked t, OrderTypes type, decimal qty, decimal price, bool reduceOnly)
    {
        var order = t.Order;
        BinanceOpenOrder? placed = null;
        string? error = null;
        try
        {
            placed = await _client.PlaceOrderAsync(Instrument.Symbol, order.Direction, type, qty, price, reduceOnly, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        lock (_gate)
        {
            _sending--;
            if (placed != null)
            {
                Bind(t, placed.OrderId);
                Apply(t, placed);
            }
            else
            {
                _byOrder.Remove(order);
                order.State = OrderStates.Failed;
                order.Status = OrderStatus.Rejected;
            }
        }

        LastError = error;
        Publish();
        Changed?.Invoke();
    }

    public Order Market(OrderDirections dir, decimal qty, string comment = "") => Send(dir, OrderTypes.Market, qty, 0, comment);
    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => Send(dir, OrderTypes.Limit, qty, price, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => Send(dir, OrderTypes.Stop, qty, trigger, comment);

    /// <summary>Stop loss / objetivo como orden solo-reducir (en futuros).</summary>
    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") =>
        Send(dir, type == OrderTypes.Stop ? OrderTypes.Stop : OrderTypes.Limit, qty, price, comment, reduceOnly: _settings.Futures);

    /// <summary>
    /// Binance does not modify price or quantity: the order is canceled and a new one sent, which the same handle (returned)
    /// then follows. As in Rithmic, the handle's total becomes its filled part plus <paramref name="newQuantity"/>, and the
    /// new price and quantity show on it at once. When the cancel is refused because the order is no longer open (it just
    /// filled), nothing new is sent and the handle reports what happened. A handle that is no longer working is left as it is.
    /// </summary>
    public Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        var type = order.Type == OrderTypes.Stop ? OrderTypes.Stop : OrderTypes.Limit;
        decimal price = Instrument.RoundPrice(newPrice);
        Tracked? t;
        decimal total;
        lock (_gate)
        {
            if (!_byOrder.TryGetValue(order, out t)) return order;
            total = order.Filled + newQuantity;
            t.Busy++; // from now on polls leave the requested price and quantity on the handle
            if (type == OrderTypes.Stop) order.TriggerPrice = price;
            else order.Price = price;
            order.QuantityToFill = total;
            order.Unfilled = Math.Max(0, newQuantity);
        }

        Enqueue(t, () => ReplaceAsync(t, type, price, total));
        return order;
    }

    private async Task ReplaceAsync(Tracked t, OrderTypes type, decimal price, decimal total)
    {
        var order = t.Order;
        long oldId;
        lock (_gate)
        {
            if (!_byOrder.ContainsKey(order) || t.Id == 0)
            {
                t.Busy--; // finished meanwhile, or never accepted
                return;
            }

            oldId = t.Id;
        }

        BinanceOpenOrder? canceled = null, found = null;
        Exception? cancelError = null;
        try
        {
            canceled = await _client.CancelOrderAsync(Instrument.Symbol, oldId, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            cancelError = ex;
            LastError = ex.Message;
        }

        // Refused as unknown (-2011): the order is no longer open (filled, or canceled elsewhere) and is not replaced. Any
        // other failure (timeout, 5xx) may still have canceled it, so Binance is asked before deciding.
        bool notOpen = cancelError is BinanceRejectedException { Code: -2011 };
        if (cancelError != null && !notOpen && !_cts.IsCancellationRequested)
        {
            try
            {
                found = await _client.GetOrderAsync(Instrument.Symbol, oldId, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // still unknown: decided below
            }

            if (found is { Status: "CANCELED" or "EXPIRED" or "EXPIRED_IN_MATCH" })
            {
                canceled = found;
                found = null;
            }
        }

        decimal filled = 0, rest = 0;
        lock (_gate)
        {
            if (canceled != null)
            {
                t.Executed = Math.Max(t.Executed, canceled.Executed);
                filled = t.FilledBefore + t.Executed;
                rest = total - filled;
            }

            if (canceled == null || rest <= 0)
            {
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
                }
                else if (_byOrder.ContainsKey(order) && t.Id == oldId)
                {
                    // not replaced. After an unknown outcome the cancel may still be carried out later: if the order then
                    // turns out canceled, that is reported as a failed Modify
                    if (!notOpen)
                    {
                        t.ReplaceFailure = cancelError!.Message;
                        t.ReplaceFailedAt = DateTime.UtcNow;
                    }

                    if (found != null) Apply(t, found); // still working (shown as it is at Binance again) or filled
                }
            }
            else
            {
                _sending++;
            }
        }

        if (canceled == null || rest <= 0)
        {
            Publish();
            Changed?.Invoke();
            return;
        }

        BinanceOpenOrder? placed = null;
        string? error = null;
        try
        {
            placed = await _client.PlaceOrderAsync(Instrument.Symbol, order.Direction, type, rest, price, order.ReduceOnly, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        lock (_gate)
        {
            _sending--;
            t.Busy--;
            _byId.Remove(oldId);
            _finished[oldId] = DateTime.UtcNow;
            t.Id = 0;
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
            }

            order.Unfilled = Math.Max(0, order.QuantityToFill - filled);
            if (placed != null)
            {
                Bind(t, placed.OrderId);
                order.State = OrderStates.Active;
                order.Status = filled > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed;
                Apply(t, placed);
            }
            else
            {
                Finish(t, OrderStates.Failed, OrderStatus.Rejected); // the old order is canceled and Binance refused the new one
            }
        }

        LastError = error;
        Publish();
        Changed?.Invoke();
    }

    public void Cancel(Order order)
    {
        Tracked? t;
        lock (_gate) _byOrder.TryGetValue(order, out t);
        if (t != null)
        {
            // a market order fills at once (Binance would answer "Unknown order"): the next poll reports how it ended
            if (t.Own && order.Type == OrderTypes.Market) return;
            Enqueue(t, () => CancelAsync(t));
            return;
        }

        // not followed any more (it finished): cancel by its exchange id, as before
        if (!long.TryParse(order.ExchangeOrderId, out var id)) return;
        _ = Task.Run(async () =>
        {
            try { await _client.CancelOrderAsync(Instrument.Symbol, id, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { LastError = ex.Message; }
            Changed?.Invoke();
        });
    }

    private async Task CancelAsync(Tracked t)
    {
        long id;
        lock (_gate)
        {
            if (!_byOrder.ContainsKey(t.Order) || t.Id == 0) return; // finished meanwhile, or never accepted
            id = t.Id;
            t.Busy++;
            t.ReplaceFailure = null; // canceling it is what is wanted now
        }

        BinanceOpenOrder? canceled = null;
        try
        {
            canceled = await _client.CancelOrderAsync(Instrument.Symbol, id, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message; // already finished at Binance, or not reached: the next poll tells
        }

        lock (_gate)
        {
            t.Busy--;
            if (canceled != null && t.Id == id && _byOrder.ContainsKey(t.Order)) Apply(t, canceled);
        }

        Publish();
        Changed?.Invoke();
    }

    public void CancelAt(decimal price)
    {
        foreach (var o in ActiveOrders.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price).ToList()) Cancel(o);
    }

    public void CancelAll() => _ = Task.Run(async () =>
    {
        try { await _client.CancelAllAsync(Instrument.Symbol, _cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { LastError = ex.Message; }
        Changed?.Invoke();
    });

    public void Modify(Order order, decimal newPrice) => Replace(order, newPrice, order.Unfilled);

    public void Flatten()
    {
        CancelAll();
        var q = Position.Volume;
        if (q > 0) Send(OrderDirections.Sell, OrderTypes.Market, q, 0, "Close", reduceOnly: true);
        else if (q < 0) Send(OrderDirections.Buy, OrderTypes.Market, -q, 0, "Close", reduceOnly: true);
    }

    public void Reverse()
    {
        var q = Position.Volume;
        if (q > 0) Market(OrderDirections.Sell, q * 2, "Reverse");
        else if (q < 0) Market(OrderDirections.Buy, -q * 2, "Reverse");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
    }
}
