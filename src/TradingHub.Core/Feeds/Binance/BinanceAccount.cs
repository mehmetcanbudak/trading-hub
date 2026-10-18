using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

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

public sealed record BinanceOpenOrder(long OrderId, string Symbol, string Side, string Type, decimal Price, decimal StopPrice, decimal Quantity, decimal Executed, string Status, bool ReduceOnly = false);

/// <summary>
/// API privada de Binance (firmada con HMAC-SHA256): saldos, posiciones y órdenes.
/// Funciona contra el testnet o contra la cuenta real según la configuración.
/// </summary>
public sealed class BinanceAccountClient : IDisposable
{
    private readonly BinanceAccountSettings _s;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private long _timeOffsetMs;

    public BinanceAccountClient(BinanceAccountSettings settings)
    {
        _s = settings;
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
            try
            {
                using var e = JsonDocument.Parse(body);
                msg = e.RootElement.GetProperty("msg").GetString() ?? body;
            }
            catch
            {
                // cuerpo no JSON
            }

            throw new InvalidOperationException($"Binance rejected operation ({(int)resp.StatusCode}): {msg}");
        }

        return JsonDocument.Parse(body);
    }

    private static decimal D(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? decimal.Parse(v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture) : 0m;

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
        return doc.RootElement.EnumerateArray().Select(o => new BinanceOpenOrder(
            o.GetProperty("orderId").GetInt64(), symbol, o.GetProperty("side").GetString()!, o.GetProperty("type").GetString()!,
            D(o, "price"), D(o, "stopPrice"), D(o, "origQty"), D(o, "executedQty"), o.GetProperty("status").GetString()!,
            o.TryGetProperty("reduceOnly", out var ro) && ro.ValueKind == System.Text.Json.JsonValueKind.True)).ToList();
    }

    /// <summary>Envía una orden. type: MARKET, LIMIT o STOP_MARKET (futuros) / STOP_LOSS (spot).</summary>
    public async Task<long> PlaceOrderAsync(string symbol, OrderDirections dir, OrderTypes type, decimal qty, decimal price, bool reduceOnly, CancellationToken ct)
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
        return doc.RootElement.GetProperty("orderId").GetInt64();
    }

    public async Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct)
    {
        using var _ = await SignedAsync(HttpMethod.Delete, P("/fapi/v1/order", "/api/v3/order"), $"symbol={symbol}&orderId={orderId}", ct).ConfigureAwait(false);
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
/// Cuenta de Binance como <see cref="ITradingAccount"/>: envía órdenes por REST y refresca
/// posición y órdenes abiertas cada 2 segundos.
/// </summary>
public sealed class BinanceBroker : ITradingAccount, IDisposable
{
    private readonly BinanceAccountClient _client;
    private readonly BinanceAccountSettings _settings;
    private readonly InstrumentData _data;
    private readonly CancellationTokenSource _cts = new();
    private List<Order> _orders = new();
    private readonly Dictionary<long, long> _exchangeIds = new();

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
    public IEnumerable<Order> ActiveOrders => _orders;
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
                var open = await _client.GetOpenOrdersAsync(Instrument.Symbol, _cts.Token).ConfigureAwait(false);
                var orders = open.Select(o =>
                {
                    var type = o.Type.Contains("STOP") ? OrderTypes.Stop : o.Type == "MARKET" ? OrderTypes.Market : OrderTypes.Limit;
                    var ord = new Order
                    {
                        ExchangeOrderId = o.OrderId.ToString(CultureInfo.InvariantCulture),
                        Direction = o.Side == "BUY" ? OrderDirections.Buy : OrderDirections.Sell,
                        Type = type,
                        Price = o.Price,
                        TriggerPrice = o.StopPrice,
                        QuantityToFill = o.Quantity,
                        Unfilled = o.Quantity - o.Executed,
                        State = OrderStates.Active,
                        Status = o.Executed > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed,
                        ReduceOnly = o.ReduceOnly,
                    };
                    _exchangeIds[ord.Id] = o.OrderId;
                    return ord;
                }).ToList();
                _orders = orders;

                if (_settings.Futures)
                {
                    var pos = (await _client.GetPositionsAsync(_cts.Token).ConfigureAwait(false)).FirstOrDefault(p => p.Symbol == Instrument.Symbol);
                    Position.Volume = pos?.Quantity ?? 0;
                    Position.AveragePrice = pos?.EntryPrice ?? 0;
                    Position.UnrealizedPnL = pos?.UnrealizedPnl ?? 0;
                }

                LastError = null;
                Changed?.Invoke();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Changed?.Invoke();
            }

            try { await Task.Delay(2000, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private Order Send(OrderDirections dir, OrderTypes type, decimal qty, decimal price, string comment, bool reduceOnly = false)
    {
        var order = new Order { Direction = dir, Type = type, QuantityToFill = qty, Unfilled = qty, Price = price, TriggerPrice = price, Comment = comment, State = OrderStates.Active, Status = OrderStatus.Placed, ReduceOnly = reduceOnly };
        _ = Task.Run(async () =>
        {
            try
            {
                long id = await _client.PlaceOrderAsync(Instrument.Symbol, dir, type, qty, Instrument.RoundPrice(price), reduceOnly, _cts.Token).ConfigureAwait(false);
                order.ExchangeOrderId = id.ToString(CultureInfo.InvariantCulture);
                LastError = null;
            }
            catch (Exception ex)
            {
                order.State = OrderStates.Failed;
                order.Status = OrderStatus.Rejected;
                LastError = ex.Message;
            }

            Changed?.Invoke();
        });
        return order;
    }

    public Order Market(OrderDirections dir, decimal qty, string comment = "") => Send(dir, OrderTypes.Market, qty, 0, comment);
    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => Send(dir, OrderTypes.Limit, qty, price, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => Send(dir, OrderTypes.Stop, qty, trigger, comment);

    /// <summary>Stop loss / objetivo como orden solo-reducir (en futuros).</summary>
    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") =>
        Send(dir, type == OrderTypes.Stop ? OrderTypes.Stop : OrderTypes.Limit, qty, price, comment, reduceOnly: _settings.Futures);

    /// <summary>Binance no modifica precio ni cantidad: cancela y envía una orden nueva.</summary>
    public Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        Cancel(order);
        return Send(order.Direction, order.Type == OrderTypes.Stop ? OrderTypes.Stop : OrderTypes.Limit, newQuantity, newPrice, order.Comment, order.ReduceOnly);
    }

    public void Cancel(Order order)
    {
        if (!long.TryParse(order.ExchangeOrderId, out var id) && !_exchangeIds.TryGetValue(order.Id, out id)) return;
        _ = Task.Run(async () =>
        {
            try { await _client.CancelOrderAsync(Instrument.Symbol, id, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { LastError = ex.Message; }
            Changed?.Invoke();
        });
    }

    public void CancelAt(decimal price)
    {
        foreach (var o in _orders.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price).ToList()) Cancel(o);
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
