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
    public string ClientOrderId { get; set; } = "";
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
    private readonly HashSet<string> _pendingSubmissions = new(StringComparer.Ordinal);
    private readonly HashSet<Order> _pendingReplacements = new();
    private readonly HashSet<long> _snapshotOrderCandidates = new();
    private readonly HashSet<(long, long)> _snapshotPositionCandidates = new();
    private readonly CancellationTokenSource _life = new();
    private bool _started;

    public TradovateTradingSession(TradovateConnection conn)
    {
        _conn = conn;
        _conn.User.Event += OnEvent;
        _conn.User.StateChanged += (up, why) =>
        {
            IsConnected = up;
            if (!up) _snapshotReady = false;
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

    private volatile bool _snapshotReady;
    public bool SnapshotReady => IsConnected && _snapshotReady;

    /// <summary>Native activity or unresolved placement acceptance; either retains the owning transport.</summary>
    public bool HasOpenPositionsOrOrders
    {
        get
        {
            lock (_gate)
            {
                if (_pendingSubmissions.Count > 0 || _pendingReplacements.Count > 0) return true;
                foreach (var position in _positions.Values)
                    if (position.Net != 0) return true;
                foreach (var order in _orders.Values)
                    if (!order.Done) return true;
                return false;
            }
        }
    }
    internal void TrackReplacement(Order order)
    {
        lock (_gate) _pendingReplacements.Add(order);
    }

    internal void ResolveReplacement(Order order)
    {
        lock (_gate) _pendingReplacements.Remove(order);
    }

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
                    lock (_gate)
                    {
                        _snapshotOrderCandidates.Clear();
                        foreach (var order in _orders.Values)
                            if (!order.Done) _snapshotOrderCandidates.Add(order.Id);
                        _snapshotPositionCandidates.Clear();
                        foreach (var key in _positions.Keys) _snapshotPositionCandidates.Add(key);
                    }
                    var d = await _conn.User.RequestAsync("user/syncrequest", new JsonObject { ["users"] = new JsonArray(_conn.UserId) }, ct).ConfigureAwait(false);
                    ApplySnapshot(d);
                    _snapshotReady = true;
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
        if (d.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Tradovate did not return an account snapshot.");
        IEnumerable<JsonElement> Arr(string key) => d.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
        foreach (var c in Arr("contracts")) _conn.RememberContract(Long(c, "id"), Str(c, "name"));
        foreach (var a in Arr("accounts")) Apply("account", a, false);
        foreach (var v in Arr("orderVersions")) Apply("orderVersion", v, false);
        foreach (var o in Arr("orders")) Apply("order", o, false);
        foreach (var p in Arr("positions")) Apply("position", p, false);
        foreach (var c in Arr("cashBalances")) Apply("cashBalance", c, false);
        foreach (var f in Arr("fills")) Apply("fill", f, false);
        lock (_gate)
        {
            foreach (long id in _snapshotOrderCandidates) _orders[id].Status = "Canceled";
            foreach (var key in _snapshotPositionCandidates) _positions.Remove(key);
            _snapshotOrderCandidates.Clear();
            _snapshotPositionCandidates.Clear();
        }
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
                    _snapshotOrderCandidates.Remove(id);
                    if (!_orders.TryGetValue(id, out var o))
                    {
                        _orders[id] = o = new TradovateOrderState { Id = id };
                        ApplyFills(o); // A fill can precede its order entity.
                    }
                    o.AccountId = Long(e, "accountId");
                    o.ContractId = Long(e, "contractId");
                    o.Symbol = _conn.KnownContractName(o.ContractId) ?? o.Symbol;
                    o.IsBuy = Str(e, "action") == "Buy";
                    o.Status = Str(e, "ordStatus");
                    o.Time = Time(e, "timestamp");
                    if (Str(e, "clOrdId") is { Length: > 0 } clientId)
                    {
                        o.ClientOrderId = clientId;
                        _pendingSubmissions.Remove(clientId);
                    }
                    if (_versions.TryGetValue(id, out var v)) ApplyVersion(o, v);
                    break;
                }

                case "position":
                {
                    var key = (Long(e, "accountId"), Long(e, "contractId"));
                    _snapshotPositionCandidates.Remove(key);
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
                        ApplyFills(fo);
                    }

                    newFill = fill;
                    break;
                }
            }
        }

        if (newFill != null) FillReceived?.Invoke(newFill);
    }

    private void ApplyFills(TradovateOrderState order)
    {
        decimal quantity = 0, value = 0;
        foreach (var fill in _fills.Values)
        {
            if (fill.OrderId != order.Id) continue;
            quantity += fill.Quantity;
            value += fill.Quantity * fill.Price;
        }
        order.Filled = quantity;
        order.AvgFill = quantity > 0 ? value / quantity : 0;
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
    public async Task<long> PlaceAsync(TradovateAccount account, string contract, bool buy, string orderType, int qty, decimal? price, decimal? stopPrice, bool automated, string clientOrderId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["accountSpec"] = account.Name,
            ["accountId"] = account.Id,
            // Official PlaceOrder schema: clOrdId is a string of at most 64 characters;
            // Order entities (including user/syncrequest replay) return that same field.
            // https://api.tradovate.com/#operation/placeOrder
            ["clOrdId"] = clientOrderId,
            ["action"] = buy ? "Buy" : "Sell",
            ["symbol"] = contract,
            ["orderQty"] = qty,
            ["orderType"] = orderType,
            ["isAutomated"] = automated,
        };
        if (price is { } p) body["price"] = p;
        if (stopPrice is { } sp) body["stopPrice"] = sp;
        if (orderType != "Market") body["timeInForce"] = "GTC";
        lock (_gate) _pendingSubmissions.Add(clientOrderId);
        try
        {
            var res = await _conn.PostAsync("/order/placeorder", body, ct).ConfigureAwait(false);
            string reason = Str(res, "failureReason");
            long id = Long(res, "orderId");
            if (reason.Length > 0 && reason != "Success")
                throw new TradovateOrderRejectedException("Tradovate rejected order: " + (Str(res, "failureText") is { Length: > 0 } t ? t : reason));
            if (id == 0) throw new InvalidOperationException("Tradovate order acceptance is unknown: no order ID returned.");
            return id;
        }
        catch (Exception ex) when (IsDefinitePlacementRejection(ex))
        {
            lock (_gate) _pendingSubmissions.Remove(clientOrderId);
            throw;
        }
    }

    internal static bool IsDefinitePlacementRejection(Exception ex) =>
        ex is TradovateOrderRejectedException ||
        ex is TradovateException { Status: 400 or 401 or 403 or 404 or 405 or 422 or 429 };

    public async Task CancelAsync(long orderId, CancellationToken ct)
    {
        var res = await _conn.PostAsync("/order/cancelorder", new JsonObject { ["orderId"] = orderId }, ct).ConfigureAwait(false);
        if (Str(res, "failureReason") is { Length: > 0 } r && r != "Success") throw new TradovateException("Tradovate did not cancel order: " + (Str(res, "failureText") is { Length: > 0 } t ? t : r));
        if (Str(res, "failureReason") != "Success" && Long(res, "commandId") == 0)
            throw new InvalidOperationException("Tradovate cancel acceptance is unknown: no command acknowledgment returned.");
    }

    public async Task ModifyAsync(TradovateOrderState o, int qty, decimal price, CancellationToken ct)
    {
        var body = new JsonObject { ["orderId"] = o.Id, ["orderQty"] = qty, ["orderType"] = o.Type.Length > 0 ? o.Type : "Limit", ["timeInForce"] = "GTC" };
        if (o.Type is "Stop" or "StopLimit") body["stopPrice"] = price;
        else body["price"] = price;
        var res = await _conn.PostAsync("/order/modifyorder", body, ct).ConfigureAwait(false);
        if (Str(res, "failureReason") is { Length: > 0 } r && r != "Success") throw new TradovateOrderRejectedException("Tradovate did not modify order: " + (Str(res, "failureText") is { Length: > 0 } t ? t : r));
    }

    public async Task LiquidateAsync(long accountId, long contractId, CancellationToken ct) =>
        await _conn.PostAsync("/order/liquidateposition", new JsonObject { ["accountId"] = accountId, ["contractId"] = contractId, ["admin"] = false }, ct).ConfigureAwait(false);

    public void Dispose() => _life.Cancel();
}

internal sealed class TradovateOrderRejectedException(string message) : Exception(message)
{
}

/// <summary>
/// Una cuenta de Tradovate operando un futuro (ES → el contrato del mes, ESZ6). Sirve con los gráficos de cualquier
/// conexión de futuros (Tradovate, Rithmic): el Chart Trader, el DOM, los stops y las estrategias operan igual.
/// </summary>
public sealed class TradovateBroker : ITradingAccount, ITrackedOrderReplacement, IDisposable
{
    private readonly InstrumentData _data;
    private readonly TradovateTradingSession _session;
    private readonly TradovateAccount _account;
    private readonly object _gate = new();
    private readonly Dictionary<long, Order> _byId = new();
    private readonly List<Order> _pending = new();
    private readonly Dictionary<string, Order> _byClientId = new(StringComparer.Ordinal);
    private readonly HashSet<Order> _cancelPending = new();
    private readonly HashSet<Order> _cancelInFlight = new();
    private readonly HashSet<Order> _cancelAwaitingAck = new();
    private sealed record Replacement(decimal Price, int Total);
    private readonly Dictionary<Order, Replacement> _replacements = new();
    private readonly Dictionary<Order, decimal> _queuedModifyPrices = new();
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
    public bool PositionReady => _contract != null && _session.SnapshotReady;
    public bool OrdersReady
    {
        get
        {
            if (_contract == null || !_session.SnapshotReady) return false;
            lock (_gate)
            {
                foreach (var order in _pending)
                    if (order.State == OrderStates.Active && order.ExchangeOrderId.Length == 0) return false;
                return true;
            }
        }
    }
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
        o.Unfilled = s.Done && !s.Cancelled && !s.Rejected ? 0 : Math.Max(0, o.QuantityToFill - s.Filled);
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
        List<Order>? cancellations = null;
        var active = new List<Order>();
        lock (_gate)
        {
            // A disconnect publishes cached native states, not a fresh working-order replay.
            // Retain obligations, but retry them only when the replacement user channel can report the result.
            bool replayReady = _session.SnapshotReady;
            if (!replayReady) _cancelAwaitingAck.Clear();
            var states = _session.OrdersOf(_account.Id, c.Id);
            foreach (var s in states)
            {
                Order o;
                if (s.ClientOrderId.Length > 0 && _byClientId.TryGetValue(s.ClientOrderId, out var owned) &&
                    (owned.ExchangeOrderId.Length == 0 || IdOf(owned) == s.Id))
                {
                    o = owned;
                    o.ExchangeOrderId = s.Id.ToString(CultureInfo.InvariantCulture);
                    _byId[s.Id] = o;
                    if (LastError?.StartsWith("Tradovate order acceptance is unknown;", StringComparison.Ordinal) == true)
                        LastError = null;
                }
                else if (!_byId.TryGetValue(s.Id, out o!))
                {
                    if (s.Done) continue; // Completed before this broker observed it.
                    o = new Order { ExchangeOrderId = s.Id.ToString(CultureInfo.InvariantCulture), Comment = "Tradovate" };
                    _byId[s.Id] = o;
                }

                Apply(s, o);
                if (_replacements.TryGetValue(o, out var replacement) &&
                    (s.Done || (s.Quantity == replacement.Total &&
                    (o.Type == OrderTypes.Stop ? s.StopPrice : s.Price) == replacement.Price)))
                {
                    _replacements.Remove(o);
                    if (!_queuedModifyPrices.ContainsKey(o)) _session.ResolveReplacement(o);
                }
                _pending.Remove(o);
                if (o.State != OrderStates.Active)
                {
                    _cancelPending.Remove(o);
                    _cancelAwaitingAck.Remove(o);
                    _queuedModifyPrices.Remove(o);
                    _session.ResolveReplacement(o);
                }
                if (s.Rejected && s.Text.Length > 0) LastError = "Tradovate rejected order: " + s.Text;
                if (o.State == OrderStates.Active) active.Add(o);
            }

            // las recién mandadas que todavía no aparecieron
            foreach (var o in _pending.Where(o => o.State == OrderStates.Active && !active.Contains(o))) active.Add(o);
            _pending.RemoveAll(o => o.State != OrderStates.Active);
            foreach (var order in _cancelPending)
                if (replayReady && order.State == OrderStates.Active && IdOf(order) != 0 &&
                    !_cancelAwaitingAck.Contains(order) && _cancelInFlight.Add(order))
                    (cancellations ??= new()).Add(order);
            if (replayReady)
            {
                foreach (var queued in _queuedModifyPrices.ToArray())
                {
                    if (_replacements.ContainsKey(queued.Key)) continue;
                    if (queued.Key.State == OrderStates.Active && queued.Key.Unfilled <= 0) continue;
                    _queuedModifyPrices.Remove(queued.Key);
                    if (queued.Key.State == OrderStates.Active && !_cancelPending.Contains(queued.Key))
                        StartReplacement(queued.Key, queued.Value, queued.Key.Unfilled);
                    else
                        _session.ResolveReplacement(queued.Key);
                }
            }
            _active = active;
        }

        if (cancellations != null)
            foreach (var order in cancellations) _ = CancelBoundAsync(order);
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

        // Register ownership before any send or websocket event can race the HTTP reply.
        string clientOrderId = "th-" + Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _byClientId.Add(clientOrderId, order);
            _pending.Add(order);
        }
        Sync();
        _ = Task.Run(async () =>
        {
            bool placementStarted = false;
            try
            {
                var (_, contract) = await ContractAsync().ConfigureAwait(false);
                decimal px = price > 0 ? Instrument.RoundPrice(price) : 0;
                string ot = type == OrderTypes.Market ? "Market" : type == OrderTypes.Stop ? "Stop" : "Limit";
                // las de una estrategia (comentario «Estrategia: …») van marcadas como automáticas, como pide Tradovate
                bool automated = comment.Contains(": ", StringComparison.Ordinal);
                placementStarted = true;
                long id = await _session.PlaceAsync(_account, contract, dir == OrderDirections.Buy, ot, q,
                    type == OrderTypes.Limit ? px : null, type == OrderTypes.Stop ? px : null, automated, clientOrderId, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    order.ExchangeOrderId = id.ToString(CultureInfo.InvariantCulture);
                    _byId[id] = order;
                }

                LastError = null;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    // A transport failure, timeout, 408/5xx, or unreadable/missing reply is
                    // not a rejection. Keep the exact handle owned until clOrdId replay.
                    bool rejected = !placementStarted || TradovateTradingSession.IsDefinitePlacementRejection(ex);
                    if (rejected && order.ExchangeOrderId.Length == 0)
                    {
                        order.State = OrderStates.Failed;
                        order.Status = OrderStatus.Rejected;
                        _cancelPending.Remove(order);
                    }
                    LastError = order.ExchangeOrderId.Length > 0 ? null : rejected ? ex.Message :
                        "Tradovate order acceptance is unknown; awaiting client-tag reconciliation: " + ex.Message;
                }
            }

            Sync();
        });
        return order;
    }

    public Order Market(OrderDirections dir, decimal qty, string comment = "") => Send(dir, OrderTypes.Market, qty, 0, comment);
    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => Send(dir, OrderTypes.Limit, qty, price, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => Send(dir, OrderTypes.Stop, qty, trigger, comment);

    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") =>
        Send(dir, type is OrderTypes.Market or OrderTypes.Stop ? type : OrderTypes.Limit, qty, price, comment, reduceOnly: true);

    private long IdOf(Order o) => long.TryParse(o.ExchangeOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    public void Cancel(Order order)
    {
        lock (_gate)
        {
            if (order.State != OrderStates.Active) return;
            _cancelPending.Add(order);
            _queuedModifyPrices.Remove(order);
            if (!_replacements.ContainsKey(order)) _session.ResolveReplacement(order);
        }
        Sync();
    }

    private async Task CancelBoundAsync(Order order)
    {
        try
        {
            await _session.CancelAsync(IdOf(order), CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                // CommandResult acknowledges the request, not the native cancellation.
                // Keep ownership until terminal replay; do not resend on every working/fill event.
                if (_session.SnapshotReady && order.State == OrderStates.Active && _cancelPending.Contains(order))
                    _cancelAwaitingAck.Add(order);
            }
        }
        catch (Exception ex)
        {
            // Keep the obligation. A later native update/replay may retry; do not
            // call Sync here and turn an unavailable cancel endpoint into a loop.
            LastError = ex.Message;
        }
        finally
        {
            lock (_gate) _cancelInFlight.Remove(order);
        }
        Changed?.Invoke();
    }

    public void CancelAt(decimal price)
    {
        foreach (var o in _active.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price).ToList()) Cancel(o);
    }

    public void CancelAll()
    {
        foreach (var o in _active.ToList()) Cancel(o);
    }

    public bool IsReplacementPending(Order order)
    {
        lock (_gate) return _replacements.ContainsKey(order) || _queuedModifyPrices.ContainsKey(order);
    }

    public void Modify(Order order, decimal newPrice)
    {
        decimal price = Instrument.RoundPrice(newPrice);
        lock (_gate)
        {
            if (order.State != OrderStates.Active || _cancelPending.Contains(order)) return;
            if (_replacements.ContainsKey(order) || _queuedModifyPrices.ContainsKey(order) || !_session.SnapshotReady)
            {
                _queuedModifyPrices[order] = price;
                _session.TrackReplacement(order);
            }
            else
                StartReplacement(order, price, order.Unfilled);
        }
        Changed?.Invoke();
    }

    public Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        lock (_gate)
        {
            // Explicit replacement quantities are not a trailing-price queue.
            if (order.State != OrderStates.Active || _cancelPending.Contains(order) ||
                _replacements.ContainsKey(order) || _queuedModifyPrices.ContainsKey(order)) return order;
            StartReplacement(order, Instrument.RoundPrice(newPrice), newQuantity);
        }
        Changed?.Invoke();
        return order;
    }

    // Called under the broker gate by both explicit replacements and deferred price-only modifies.
    private void StartReplacement(Order order, decimal price, decimal remaining)
    {
        var replacement = new Replacement(price, (int)Math.Round(order.Filled + remaining));
        _replacements.Add(order, replacement);
        _session.TrackReplacement(order);
        _ = Task.Run(async () =>
        {
            try
            {
                Task? submission = null;
                while (submission == null)
                {
                    lock (_gate)
                    {
                        if (!_replacements.TryGetValue(order, out var current) || !ReferenceEquals(current, replacement))
                            return;
                        if (order.State != OrderStates.Active || _cancelPending.Contains(order))
                        {
                            _replacements.Remove(order);
                            _queuedModifyPrices.Remove(order);
                            _session.ResolveReplacement(order);
                            return;
                        }
                        // Unknown placement/replay cannot authorize another native request.
                        long id = IdOf(order);
                        if (id != 0 && _session.SnapshotReady && _contract is { } contract)
                        {
                            var native = _session.OrdersOf(_account.Id, contract.Id).FirstOrDefault(s => s.Id == id);
                            if (native != null && !native.Done)
                                submission = _session.ModifyAsync(native, replacement.Total, replacement.Price, CancellationToken.None);
                        }
                    }
                    if (submission == null) await Task.Delay(100).ConfigureAwait(false);
                }
                await submission.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    LastError = ex.Message;
                    if (TradovateTradingSession.IsDefinitePlacementRejection(ex) &&
                        _replacements.TryGetValue(order, out var current) && ReferenceEquals(current, replacement))
                    {
                        var native = _contract is { } contract
                            ? _session.OrdersOf(_account.Id, contract.Id).FirstOrDefault(s => s.Id == IdOf(order))
                            : null;
                        if (native != null) Apply(native, order);
                        _replacements.Remove(order);
                        if (!_queuedModifyPrices.ContainsKey(order)) _session.ResolveReplacement(order);
                    }
                }
            }

            // Apply authoritative values even on rejection; optimistic caller fields
            // and successful command submission are never amendment acknowledgments.
            Sync();
        });
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
