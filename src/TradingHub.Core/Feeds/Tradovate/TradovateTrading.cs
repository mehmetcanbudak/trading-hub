using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Feeds.Tradovate;

public sealed record TradovateAccount(long Id, string Name, bool Active);

public sealed record TradovateFill(long Id, long OrderId, long AccountId, string Symbol, DateTime Time, bool Buy, decimal Quantity, decimal Price);

/// <summary>Una orden de Tradovate como la ve Trading Hub (la orden más su última versión: cantidad, tipo y precios).</summary>
public sealed class TradovateOrderState
{
    public long Id { get; init; }
    public long AccountId { get; set; }
    public long ContractId { get; set; }
    public string Symbol { get; set; } = "";
    public bool IsBuy { get; set; }
    public string Status { get; set; } = "";
    public string Type { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal StopPrice { get; set; }
    public decimal Filled { get; set; }
    public decimal AvgFill { get; set; }
    public DateTime Time { get; set; }
    public string Text { get; set; } = "";
    public bool Done => Status is "Filled" or "Canceled" or "Rejected" or "Expired" or "Completed";
    public bool Rejected => Status == "Rejected";
    public bool Cancelled => Status is "Canceled" or "Expired";
}

public sealed class TradovatePositionState
{
    public long AccountId { get; init; }
    public long ContractId { get; init; }
    public string Symbol { get; set; } = "";
    public decimal Net { get; set; }
    public decimal NetPrice { get; set; }
    public decimal Bought { get; set; }
    public decimal Sold { get; set; }
    public decimal BoughtValue { get; set; }
    public decimal SoldValue { get; set; }
}

public sealed class TradovateCashState
{
    public decimal? Balance { get; set; }
    public decimal? NetLiq { get; set; }
    public decimal? OpenPnl { get; set; }
    public decimal? RealizedPnl { get; set; }
    public decimal? WeekRealizedPnl { get; set; }
    public decimal? InitialMargin { get; set; }
}

/// <summary>
/// La cuenta de Tradovate en vivo: el socket de usuario sincroniza cuentas, órdenes, ejecuciones, posiciones y saldos
/// (user/syncrequest y después eventos «props»); las órdenes se mandan por REST. Las ejecuciones se avisan para que
/// queden en el historial de la cuenta en Trading Hub.
/// </summary>
public sealed class TradovateTradingSession : IDisposable
{
    private readonly TradovateConnection _conn;
    private readonly object _gate = new();
    private readonly Dictionary<long, TradovateAccount> _accounts = new();
    private readonly Dictionary<long, TradovateOrderState> _orders = new();
    private readonly Dictionary<long, JsonElement> _versions = new();
    private readonly Dictionary<(long, long), TradovatePositionState> _positions = new();
    private readonly Dictionary<long, TradovateFill> _fills = new();
    private readonly ConcurrentDictionary<long, TradovateCashState> _cash = new();
    private readonly CancellationTokenSource _life = new();
    private bool _started;

    public TradovateTradingSession(TradovateConnection conn)
    {
        _conn = conn;
        _conn.User.Event += OnEvent;
        _conn.User.StateChanged += (up, why) =>
        {
            IsConnected = up;
            StatusText = up ? "Connected" : "Tradovate: " + why;
            Changed?.Invoke();
            if (!up && !_life.IsCancellationRequested) _ = Task.Run(() => RunAsync(_life.Token));
        };
    }

    public bool IsConnected { get; private set; }
    public string StatusText { get; private set; } = "Connecting to Tradovate…";
    public bool IsDemo => _conn.IsDemo;
    public TradovateConnection Connection => _conn;

    public IReadOnlyList<TradovateAccount> Accounts
    {
        get
        {
            lock (_gate) return _accounts.Values.Where(a => a.Active).OrderBy(a => a.Name).ToList();
        }
    }

    public event Action? Changed;
    public event Action<TradovateFill>? FillReceived;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(() => RunAsync(_life.Token));
        _ = Task.Run(() => CashLoopAsync(_life.Token));
    }

    private int _running;

    private async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    await _conn.User.EnsureAsync(ct).ConfigureAwait(false);
                    await _conn.AccessTokenAsync(ct).ConfigureAwait(false);
                    var d = await _conn.User.RequestAsync("user/syncrequest", new JsonObject { ["users"] = new JsonArray(_conn.UserId) }, ct).ConfigureAwait(false);
                    ApplySnapshot(d);
                    IsConnected = true;
                    StatusText = "Connected";
                    Changed?.Invoke();
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    IsConnected = false;
                    StatusText = "Tradovate: " + ex.Message;
                    Changed?.Invoke();
                    await Task.Delay(LiveSubscription.Backoff(attempt + 1), ct).ContinueWith(_ => { }).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Saldos (la foto de cada cuenta) cada 10 s.</summary>
    private async Task CashLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => { }).ConfigureAwait(false);
            if (!IsConnected) continue;
            foreach (var a in Accounts)
            {
                try
                {
                    var s = await _conn.PostAsync("/cashBalance/getcashbalancesnapshot", new JsonObject { ["accountId"] = a.Id }, ct).ConfigureAwait(false);
                    var c = _cash.GetOrAdd(a.Id, _ => new TradovateCashState());
                    c.Balance = Dec(s, "totalCashValue") ?? c.Balance;
                    c.NetLiq = Dec(s, "netLiq");
                    c.OpenPnl = Dec(s, "openPnL");
                    c.RealizedPnl = Dec(s, "realizedPnL");
                    c.WeekRealizedPnl = Dec(s, "weekRealizedPnL");
                    c.InitialMargin = Dec(s, "initialMargin");
                }
                catch (Exception)
                {
                    // se reintenta en la próxima vuelta
                }
            }

            Changed?.Invoke();
        }
    }

    private static decimal? Dec(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static long Long(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTime Time(JsonElement e, string key) =>
        DateTime.TryParse(Str(e, key), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.UtcNow;

    // ===================================================================== sincronización

    private void ApplySnapshot(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return;
        IEnumerable<JsonElement> Arr(string key) => d.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
        foreach (var c in Arr("contracts")) _conn.RememberContract(Long(c, "id"), Str(c, "name"));
        foreach (var a in Arr("accounts")) Apply("account", a, false);
        foreach (var v in Arr("orderVersions")) Apply("orderVersion", v, false);
        foreach (var o in Arr("orders")) Apply("order", o, false);
        foreach (var p in Arr("positions")) Apply("position", p, false);
        foreach (var c in Arr("cashBalances")) Apply("cashBalance", c, false);
        foreach (var f in Arr("fills")) Apply("fill", f, false);
    }

    private void OnEvent(string kind, JsonElement d)
    {
        if (kind != "props" || d.ValueKind != JsonValueKind.Object) return;
        string type = Str(d, "entityType");
        var entity = d.TryGetProperty("entity", out var e) ? e : default;
        bool deleted = Str(d, "eventType") == "Deleted";
        if (entity.ValueKind != JsonValueKind.Object) return;
        if (type == "contract") _conn.RememberContract(Long(entity, "id"), Str(entity, "name"));
        Apply(type, entity, deleted);
        Changed?.Invoke();
    }

    private void Apply(string type, JsonElement e, bool deleted)
    {
        TradovateFill? newFill = null;
        lock (_gate)
        {
            switch (type)
            {
                case "account":
                    long aid = Long(e, "id");
                    if (aid == 0) break;
                    if (deleted) _accounts.Remove(aid);
                    else _accounts[aid] = new TradovateAccount(aid, Str(e, "name"), !e.TryGetProperty("active", out var act) || act.ValueKind != JsonValueKind.False);
                    break;
                case "orderVersion":
                {
                    long oid = Long(e, "orderId");
                    if (oid == 0) break;
                    _versions[oid] = e.Clone();
                    if (_orders.TryGetValue(oid, out var o)) ApplyVersion(o, e);
                    break;
                }

                case "order":
                {
                    long id = Long(e, "id");
                    if (id == 0) break;
                    if (!_orders.TryGetValue(id, out var o)) _orders[id] = o = new TradovateOrderState { Id = id };
                    o.AccountId = Long(e, "accountId");
                    o.ContractId = Long(e, "contractId");
                    o.Symbol = _conn.KnownContractName(o.ContractId) ?? o.Symbol;
                    o.IsBuy = Str(e, "action") == "Buy";
                    o.Status = Str(e, "ordStatus");
                    o.Time = Time(e, "timestamp");
                    if (_versions.TryGetValue(id, out var v)) ApplyVersion(o, v);
                    break;
                }

                case "position":
                {
                    var key = (Long(e, "accountId"), Long(e, "contractId"));
                    if (deleted)
                    {
                        _positions.Remove(key);
                        break;
                    }

                    if (!_positions.TryGetValue(key, out var p)) _positions[key] = p = new TradovatePositionState { AccountId = key.Item1, ContractId = key.Item2 };
                    p.Symbol = _conn.KnownContractName(key.Item2) ?? p.Symbol;
                    p.Net = Dec(e, "netPos") ?? 0;
                    p.NetPrice = Dec(e, "netPrice") ?? 0;
                    p.Bought = Dec(e, "bought") ?? 0;
                    p.Sold = Dec(e, "sold") ?? 0;
                    p.BoughtValue = Dec(e, "boughtValue") ?? 0;
                    p.SoldValue = Dec(e, "soldValue") ?? 0;
                    break;
                }

                case "cashBalance":
                {
                    var c = _cash.GetOrAdd(Long(e, "accountId"), _ => new TradovateCashState());
                    c.Balance = Dec(e, "amount") ?? c.Balance;
                    c.RealizedPnl = Dec(e, "realizedPnL") ?? c.RealizedPnl;
                    c.WeekRealizedPnl = Dec(e, "weekRealizedPnL") ?? c.WeekRealizedPnl;
                    break;
                }

                case "fill":
                {
                    long fid = Long(e, "id");
                    if (fid == 0 || _fills.ContainsKey(fid) || (e.TryGetProperty("active", out var fa) && fa.ValueKind == JsonValueKind.False)) break;
                    long orderId = Long(e, "orderId");
                    long accountId = _orders.TryGetValue(orderId, out var fo) ? fo.AccountId : _accounts.Count == 1 ? _accounts.Keys.First() : 0;
                    var fill = new TradovateFill(fid, orderId, accountId, _conn.KnownContractName(Long(e, "contractId")) ?? "", Time(e, "timestamp"),
                        Str(e, "action") == "Buy", Dec(e, "qty") ?? 0, Dec(e, "price") ?? 0);
                    _fills[fid] = fill;
                    if (fo != null)
                    {
                        decimal qty = _fills.Values.Where(x => x.OrderId == orderId).Sum(x => x.Quantity);
                        decimal value = _fills.Values.Where(x => x.OrderId == orderId).Sum(x => x.Quantity * x.Price);
                        fo.Filled = qty;
                        fo.AvgFill = qty > 0 ? value / qty : 0;
                    }

                    newFill = fill;
                    break;
                }
            }
        }

        if (newFill != null) FillReceived?.Invoke(newFill);
    }

    private static void ApplyVersion(TradovateOrderState o, JsonElement v)
    {
        o.Quantity = Dec(v, "orderQty") ?? o.Quantity;
        o.Type = Str(v, "orderType") is { Length: > 0 } t ? t : o.Type;
        o.Price = Dec(v, "price") ?? o.Price;
        o.StopPrice = Dec(v, "stopPrice") ?? o.StopPrice;
        o.Text = Str(v, "text");
    }

    // ===================================================================== consultas

    public List<TradovateOrderState> OrdersOf(long accountId, long? contractId = null)
    {
        lock (_gate)
        {
            foreach (var o in _orders.Values.Where(o => o.Symbol.Length == 0)) o.Symbol = _conn.KnownContractName(o.ContractId) ?? "";
            return _orders.Values.Where(o => o.AccountId == accountId && (contractId == null || o.ContractId == contractId)).OrderBy(o => o.Time).ToList();
        }
    }

    public List<TradovatePositionState> PositionsOf(long accountId)
    {
        lock (_gate)
        {
            foreach (var p in _positions.Values.Where(p => p.Symbol.Length == 0)) p.Symbol = _conn.KnownContractName(p.ContractId) ?? "";
            return _positions.Values.Where(p => p.AccountId == accountId).ToList();
        }
    }

    public TradovatePositionState? PositionFor(long accountId, long contractId)
    {
        lock (_gate) return _positions.TryGetValue((accountId, contractId), out var p) ? p : null;
    }

    public TradovateCashState? CashOf(long accountId) => _cash.TryGetValue(accountId, out var c) ? c : null;

    public List<TradovateFill> FillsOf(long accountId)
    {
        lock (_gate) return _fills.Values.Where(f => f.AccountId == accountId).OrderBy(f => f.Time).ToList();
    }

    // ===================================================================== órdenes

    /// <summary>Manda una orden; devuelve su número en Tradovate (o lanza el motivo del rechazo).</summary>
    public async Task<long> PlaceAsync(TradovateAccount account, string contract, bool buy, string orderType, int qty, decimal? price, decimal? stopPrice, bool automated, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["accountSpec"] = account.Name,
            ["accountId"] = account.Id,
            ["action"] = buy ? "Buy" : "Sell",
            ["symbol"] = contract,
            ["orderQty"] = qty,
            ["orderType"] = orderType,
            ["isAutomated"] = automated,
        };
        if (price is { } p) body["price"] = p;
        if (stopPrice is { } sp) body["stopPrice"] = sp;
        if (orderType != "Market") body["timeInForce"] = "GTC";
        var res = await _conn.PostAsync("/order/placeorder", body, ct).ConfigureAwait(false);
        string reason = Str(res, "failureReason");
        long id = Long(res, "orderId");
        if (id == 0 || (reason.Length > 0 && reason != "Success"))
            throw new TradovateException("Tradovate rejected order: " + (Str(res, "failureText") is { Length: > 0 } t ? t : reason.Length > 0 ? reason : "no reason supplied"));
        return id;
    }

    public async Task CancelAsync(long orderId, CancellationToken ct)
    {
        var res = await _conn.PostAsync("/order/cancelorder", new JsonObject { ["orderId"] = orderId }, ct).ConfigureAwait(false);
        if (Str(res, "failureReason") is { Length: > 0 } r && r != "Success") throw new TradovateException("Tradovate did not cancel order: " + (Str(res, "failureText") is { Length: > 0 } t ? t : r));
    }

    public async Task ModifyAsync(TradovateOrderState o, int qty, decimal price, CancellationToken ct)
    {
        var body = new JsonObject { ["orderId"] = o.Id, ["orderQty"] = qty, ["orderType"] = o.Type.Length > 0 ? o.Type : "Limit", ["timeInForce"] = "GTC" };
        if (o.Type is "Stop" or "StopLimit") body["stopPrice"] = price;
        else body["price"] = price;
        var res = await _conn.PostAsync("/order/modifyorder", body, ct).ConfigureAwait(false);
        if (Str(res, "failureReason") is { Length: > 0 } r && r != "Success") throw new TradovateException("Tradovate did not modify order: " + (Str(res, "failureText") is { Length: > 0 } t ? t : r));
    }

    public async Task LiquidateAsync(long accountId, long contractId, CancellationToken ct) =>
        await _conn.PostAsync("/order/liquidateposition", new JsonObject { ["accountId"] = accountId, ["contractId"] = contractId, ["admin"] = false }, ct).ConfigureAwait(false);

    public void Dispose() => _life.Cancel();
}

/// <summary>
/// Una cuenta de Tradovate operando un futuro (ES → el contrato del mes, ESZ6). Sirve con los gráficos de cualquier
/// conexión de futuros (Tradovate, Rithmic): el Chart Trader, el DOM, los stops y las estrategias operan igual.
/// </summary>
public sealed class TradovateBroker : ITradingAccount, IDisposable
{
    private readonly InstrumentData _data;
    private readonly TradovateTradingSession _session;
    private readonly TradovateAccount _account;
    private readonly object _gate = new();
    private readonly Dictionary<long, Order> _byId = new();
    private readonly List<Order> _pending = new();
    private (long Id, string Name)? _contract;
    private List<Order> _active = new();

    public TradovateBroker(InstrumentData data, TradovateTradingSession session, TradovateAccount account, string name)
    {
        _data = data;
        _session = session;
        _account = account;
        Name = name;
        Position = new Position { Security = new Security { Code = data.Instrument.Symbol, SecurityId = data.Instrument.Key } };
        _session.Changed += Sync;
        _ = ContractAsync();
    }

    public string Name { get; }
    public bool IsReal => !_session.IsDemo;
    public Instrument Instrument => _data.Instrument;
    public decimal LastPrice => _data.LastPrice;
    public Position Position { get; }
    public IEnumerable<Order> ActiveOrders => _active;
    public string? LastError { get; private set; }
    public event Action? Changed;

    /// <summary>Raíz del contrato (NQ) aunque el gráfico sea de otra conexión.</summary>
    private string Root => _data.Instrument.Symbol.Split(' ', ':')[0];

    private async Task<(long Id, string Name)> ContractAsync()
    {
        if (_contract is { } c) return c;
        try
        {
            var r = await _session.Connection.ContractAsync(Root, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
            _contract = r;
            Sync();
            return r;
        }
        catch (Exception ex)
        {
            LastError = "Could not determine Tradovate's active contract: " + ex.Message;
            Changed?.Invoke();
            throw;
        }
    }

    private static void Apply(TradovateOrderState s, Order o)
    {
        o.Direction = s.IsBuy ? OrderDirections.Buy : OrderDirections.Sell;
        o.Type = s.Type == "Market" ? OrderTypes.Market : s.Type is "Stop" or "StopLimit" or "TrailingStop" ? OrderTypes.Stop : OrderTypes.Limit;
        if (s.Price > 0) o.Price = s.Price;
        o.TriggerPrice = o.Type == OrderTypes.Stop && s.StopPrice > 0 ? s.StopPrice : o.Price;
        if (s.Quantity > 0) o.QuantityToFill = s.Quantity;
        o.Unfilled = s.Done ? 0 : Math.Max(0, o.QuantityToFill - s.Filled);
        if (s.Done)
        {
            o.State = s.Rejected ? OrderStates.Failed : OrderStates.Done;
            o.Status = s.Rejected ? OrderStatus.Rejected : s.Cancelled ? OrderStatus.Canceled : OrderStatus.Filled;
        }
        else
        {
            o.State = OrderStates.Active;
            o.Status = s.Filled > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed;
        }
    }

    private void Sync()
    {
        if (_contract is not { } c) return;
        var states = _session.OrdersOf(_account.Id, c.Id);
        var active = new List<Order>();
        lock (_gate)
        {
            foreach (var s in states)
            {
                if (!_byId.TryGetValue(s.Id, out var o))
                {
                    if (s.Done) continue; // terminada antes de abrir Trading Hub
                    o = new Order { ExchangeOrderId = s.Id.ToString(CultureInfo.InvariantCulture), Comment = "Tradovate" };
                    _byId[s.Id] = o;
                }

                Apply(s, o);
                if (s.Rejected && s.Text.Length > 0) LastError = "Tradovate rejected order: " + s.Text;
                if (o.State == OrderStates.Active) active.Add(o);
            }

            // las recién mandadas que todavía no aparecieron
            foreach (var o in _pending.Where(o => o.State == OrderStates.Active && !active.Contains(o))) active.Add(o);
            _pending.RemoveAll(o => o.State != OrderStates.Active || o.ExchangeOrderId.Length > 0);
        }

        _active = active;
        var p = _session.PositionFor(_account.Id, c.Id);
        Position.Volume = p?.Net ?? 0;
        Position.AveragePrice = p?.NetPrice ?? 0;
        decimal pv = Instrument.PointValue > 0 ? Instrument.PointValue : 1;
        // lo cerrado hoy: la cantidad que se compró y se vendió, al precio promedio de cada lado
        Position.RealizedPnL = p is { Bought: > 0, Sold: > 0 }
            ? Math.Min(p.Bought, p.Sold) * (p.SoldValue / p.Sold - p.BoughtValue / p.Bought) * pv
            : 0;
        Position.UnrealizedPnL = Position.Volume != 0 && LastPrice > 0 && Position.AveragePrice > 0 ? (LastPrice - Position.AveragePrice) * Position.Volume * pv : 0;
        Changed?.Invoke();
    }

    private Order Send(OrderDirections dir, OrderTypes type, decimal qty, decimal price, string comment, bool reduceOnly = false)
    {
        int q = (int)Math.Round(qty);
        var order = new Order { Direction = dir, Type = type, QuantityToFill = q, Unfilled = q, Price = price, TriggerPrice = price, Comment = comment, State = OrderStates.Active, Status = OrderStatus.Placed, ReduceOnly = reduceOnly };
        if (q <= 0)
        {
            order.State = OrderStates.Failed;
            order.Status = OrderStatus.Rejected;
            LastError = "Futures quantity must be a whole number of contracts (1, 2, 3…).";
            Changed?.Invoke();
            return order;
        }

        lock (_gate) _pending.Add(order);
        Sync();
        _ = Task.Run(async () =>
        {
            try
            {
                var (_, contract) = await ContractAsync().ConfigureAwait(false);
                decimal px = price > 0 ? Instrument.RoundPrice(price) : 0;
                string ot = type == OrderTypes.Market ? "Market" : type == OrderTypes.Stop ? "Stop" : "Limit";
                // las de una estrategia (comentario «Estrategia: …») van marcadas como automáticas, como pide Tradovate
                bool automated = comment.Contains(": ", StringComparison.Ordinal);
                long id = await _session.PlaceAsync(_account, contract, dir == OrderDirections.Buy, ot, q,
                    type == OrderTypes.Limit ? px : null, type == OrderTypes.Stop ? px : null, automated, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    order.ExchangeOrderId = id.ToString(CultureInfo.InvariantCulture);
                    _byId[id] = order;
                }

                LastError = null;
            }
            catch (Exception ex)
            {
                order.State = OrderStates.Failed;
                order.Status = OrderStatus.Rejected;
                LastError = ex.Message;
            }

            Sync();
        });
        return order;
    }

    public Order Market(OrderDirections dir, decimal qty, string comment = "") => Send(dir, OrderTypes.Market, qty, 0, comment);
    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => Send(dir, OrderTypes.Limit, qty, price, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => Send(dir, OrderTypes.Stop, qty, trigger, comment);

    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") =>
        Send(dir, type == OrderTypes.Stop ? OrderTypes.Stop : OrderTypes.Limit, qty, price, comment, reduceOnly: true);

    private long IdOf(Order o) => long.TryParse(o.ExchangeOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    public void Cancel(Order order)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 30 && IdOf(order) == 0 && order.State == OrderStates.Active; i++) await Task.Delay(100).ConfigureAwait(false);
                long id = IdOf(order);
                if (id != 0) await _session.CancelAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            Sync();
        });
    }

    public void CancelAt(decimal price)
    {
        foreach (var o in _active.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price).ToList()) Cancel(o);
    }

    public void CancelAll()
    {
        foreach (var o in _active.ToList()) Cancel(o);
    }

    public void Modify(Order order, decimal newPrice) => Replace(order, newPrice, order.Unfilled);

    public Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        decimal px = Instrument.RoundPrice(newPrice);
        int total = (int)Math.Round(order.Filled + newQuantity);
        if (order.Type == OrderTypes.Stop) order.TriggerPrice = px;
        else order.Price = px;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 30 && IdOf(order) == 0 && order.State == OrderStates.Active; i++) await Task.Delay(100).ConfigureAwait(false);
                long id = IdOf(order);
                var s = _contract is { } c ? _session.OrdersOf(_account.Id, c.Id).FirstOrDefault(x => x.Id == id) : null;
                if (s == null) throw new InvalidOperationException("The order is not available in Tradovate yet");
                await _session.ModifyAsync(s, total, px, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            Sync();
        });
        return order;
    }

    public void Flatten()
    {
        CancelAll();
        var q = Position.Volume;
        if (q > 0) Send(OrderDirections.Sell, OrderTypes.Market, q, 0, "Close");
        else if (q < 0) Send(OrderDirections.Buy, OrderTypes.Market, -q, 0, "Close");
    }

    public void Reverse()
    {
        var q = Position.Volume;
        if (q > 0) Market(OrderDirections.Sell, q * 2, "Reverse");
        else if (q < 0) Market(OrderDirections.Buy, -q * 2, "Reverse");
    }

    public void Dispose() => _session.Changed -= Sync;
}
