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

// Account/client attribution comes only from the exact native order and matching contract.
internal sealed record TradovateNativeFill(TradovateFill Fill, long ContractId, string ClientOrderId,
    bool? Active, bool Deleted, JsonElement Raw);

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
    /// <summary>The venue's OCO strategy this order belongs to (Order.ocoId); 0 when it is not linked.</summary>
    public long OcoId { get; set; }
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
    /// <summary>The venue's timestamp of this position report; null when the report carried none.</summary>
    public DateTime? Time { get; set; }
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
    private readonly Dictionary<long, NativeSendTurns> _sendTurns = new();
    private readonly Dictionary<long, JsonElement> _versions = new();
    private readonly Dictionary<(long, long), TradovatePositionState> _positions = new();
    private readonly Dictionary<long, TradovateFill> _fills = new();
    private readonly Dictionary<long, TradovateNativeFill> _nativeFills = new();
    private readonly Dictionary<long, HashSet<long>> _fillsByOrder = new();
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
    internal event Action<long>? NativeFillChanged;

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
        List<TradovateFill>? attributed = null;
        List<long>? changed = null;
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
                    }
                    o.AccountId = Long(e, "accountId");
                    o.ContractId = Long(e, "contractId");
                    o.Symbol = _conn.KnownContractName(o.ContractId) ?? o.Symbol;
                    o.IsBuy = Str(e, "action") == "Buy";
                    o.Status = Str(e, "ordStatus");
                    o.Time = Time(e, "timestamp");
                    // Partial order events may omit the link; never erase a reported one.
                    if (Long(e, "ocoId") is > 0 and var ocoId) o.OcoId = ocoId;
                    if (Str(e, "clOrdId") is { Length: > 0 } clientId)
                    {
                        o.ClientOrderId = clientId;
                        _pendingSubmissions.Remove(clientId);
                    }
                    if (_versions.TryGetValue(id, out var v)) ApplyVersion(o, v);
                    if (_fillsByOrder.TryGetValue(id, out var fillIds))
                        foreach (long fillId in fillIds)
                        {
                            var previous = _nativeFills[fillId];
                            var updated = Attribute(previous, o);
                            _nativeFills[fillId] = updated;
                            _fills[fillId] = updated.Fill;
                            (changed ??= new()).Add(fillId);
                            if (updated.Fill != previous.Fill && updated.Fill.AccountId != 0 &&
                                updated.Active == true && !updated.Deleted)
                                (attributed ??= new()).Add(updated.Fill);
                        }
                    ApplyFills(o);
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
                    p.Time = DateTime.TryParse(Str(e, "timestamp"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var reported) ? reported : null;
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
                    if (fid == 0) break;
                    _nativeFills.TryGetValue(fid, out var previous);
                    long orderId = e.TryGetProperty("orderId", out _) ? Long(e, "orderId") : previous?.Fill.OrderId ?? 0;
                    long contractId = e.TryGetProperty("contractId", out _) ? Long(e, "contractId") : previous?.ContractId ?? 0;
                    if (previous != null && previous.Fill.OrderId != orderId &&
                        _fillsByOrder.TryGetValue(previous.Fill.OrderId, out var oldIds))
                        oldIds.Remove(fid);
                    var fill = new TradovateFill(fid, orderId, 0, _conn.KnownContractName(contractId) ?? previous?.Fill.Symbol ?? "",
                        DateTime.TryParse(Str(e, "timestamp"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) ? time : previous?.Fill.Time ?? DateTime.MinValue,
                        e.TryGetProperty("action", out _) ? Str(e, "action") == "Buy" : previous?.Fill.Buy ?? false,
                        Dec(e, "qty") ?? previous?.Fill.Quantity ?? 0, Dec(e, "price") ?? previous?.Fill.Price ?? 0);
                    bool? active = e.TryGetProperty("active", out var fa) &&
                        fa.ValueKind is JsonValueKind.True or JsonValueKind.False ? fa.GetBoolean() : null;
                    var native = new TradovateNativeFill(fill, contractId, "", active, deleted, e.Clone());
                    if (_orders.TryGetValue(orderId, out var order)) native = Attribute(native, order);
                    _nativeFills[fid] = native;
                    _fills[fid] = native.Fill;
                    if (!_fillsByOrder.TryGetValue(orderId, out var ids)) _fillsByOrder[orderId] = ids = new();
                    ids.Add(fid);
                    if (order != null) ApplyFills(order);
                    (changed ??= new()).Add(fid); // Equal Fill.id deliveries can revise their separate FillFee snapshot.
                    if (native.Fill.AccountId != 0 && active == true && !deleted && native.Fill != previous?.Fill)
                        (attributed ??= new()).Add(native.Fill);
                    break;
                }
            }
        }

        if (changed != null)
            foreach (long id in changed) NativeFillChanged?.Invoke(id);
        if (attributed != null)
            foreach (var fill in attributed) FillReceived?.Invoke(fill);
    }

    private TradovateNativeFill Attribute(TradovateNativeFill native, TradovateOrderState order)
    {
        bool exact = native.ContractId != 0 && native.ContractId == order.ContractId && order.AccountId != 0;
        return native with
        {
            Fill = native.Fill with
            {
                AccountId = exact ? order.AccountId : 0,
                Symbol = _conn.KnownContractName(native.ContractId) ?? native.Fill.Symbol
            },
            ClientOrderId = exact ? order.ClientOrderId : ""
        };
    }

    internal long[] NativeFillIds()
    {
        lock (_gate) return _nativeFills.Keys.ToArray();
    }

    internal TradovateNativeFill? NativeFill(long id)
    {
        lock (_gate) return _nativeFills.GetValueOrDefault(id);
    }

    internal bool ReportsAccount(long id)
    {
        lock (_gate) return _accounts.ContainsKey(id);
    }

    private void ApplyFills(TradovateOrderState order)
    {
        decimal quantity = 0, value = 0;
        if (!_fillsByOrder.TryGetValue(order.Id, out var ids)) return;
        foreach (long id in ids)
        {
            var native = _nativeFills[id];
            // The vendor's inactive/deleted correction semantics are not established. Keep the last
            // observed cumulative quantity; the execution producer exposes reconciliation-required.
            if (native.Active != true || native.Deleted) return;
            var fill = native.Fill;
            if (fill.AccountId == 0 || fill.AccountId != order.AccountId ||
                native.ContractId != order.ContractId || fill.Quantity <= 0) return;
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

    public TradovateOrderState? OrderState(long id)
    {
        lock (_gate) return _orders.TryGetValue(id, out var o) ? o : null;
    }

    /// <summary>The order's reported fill quantity and the venue timestamp of its latest fill report. Last is null when any
    /// report lacks a valid timestamp (stored as <see cref="DateTime.MinValue"/>): an undated fill cannot be ordered against
    /// a position report.</summary>
    public (decimal Quantity, DateTime? Last) FillTimes(long orderId)
    {
        lock (_gate)
        {
            if (!_fillsByOrder.TryGetValue(orderId, out var ids)) return (0, null);
            decimal quantity = 0;
            DateTime? last = null;
            foreach (long id in ids)
            {
                if (!_fills.TryGetValue(id, out var fill)) continue;
                if (fill.Time == DateTime.MinValue) return (quantity + fill.Quantity, null);
                quantity += fill.Quantity;
                if (last == null || fill.Time > last) last = fill.Time;
            }
            return (quantity, last);
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

    /// <summary>
    /// Sends a stop and a target as one OCO strategy (POST /order/placeoco): the stop is the primary order and the target
    /// its <c>other</c> leg, each with its own pre-send clOrdId. Returns the primary order's ID or throws the refusal.
    /// https://partner.tradovate.com/api/rest-api-endpoints/orders/place-oco
    /// </summary>
    public async Task<long> PlaceOcoAsync(TradovateAccount account, string contract, bool buy, int qty, decimal stopPrice,
        string stopClientOrderId, decimal targetPrice, string targetClientOrderId, bool automated, CancellationToken ct)
    {
        string action = buy ? "Buy" : "Sell";
        var body = new JsonObject
        {
            ["accountSpec"] = account.Name,
            ["accountId"] = account.Id,
            ["clOrdId"] = stopClientOrderId,
            ["action"] = action,
            ["symbol"] = contract,
            ["orderQty"] = qty,
            ["orderType"] = "Stop",
            ["stopPrice"] = stopPrice,
            ["timeInForce"] = "GTC",
            ["isAutomated"] = automated,
            ["other"] = new JsonObject
            {
                ["action"] = action,
                ["clOrdId"] = targetClientOrderId,
                ["orderType"] = "Limit",
                ["price"] = targetPrice,
                ["timeInForce"] = "GTC",
            },
        };
        lock (_gate)
        {
            _pendingSubmissions.Add(stopClientOrderId);
            _pendingSubmissions.Add(targetClientOrderId);
        }
        try
        {
            var res = await _conn.PostAsync("/order/placeoco", body, ct).ConfigureAwait(false);
            string reason = Str(res, "failureReason");
            long id = Long(res, "orderId");
            if (reason.Length > 0 && reason != "Success")
                throw new TradovateOrderRejectedException("Tradovate rejected OCO: " + (Str(res, "failureText") is { Length: > 0 } t ? t : reason));
            if (id == 0) throw new InvalidOperationException("Tradovate OCO acceptance is unknown: no order ID returned.");
            return id;
        }
        catch (Exception ex) when (IsDefinitePlacementRejection(ex))
        {
            lock (_gate)
            {
                _pendingSubmissions.Remove(stopClientOrderId);
                _pendingSubmissions.Remove(targetClientOrderId);
            }
            throw;
        }
    }

    internal static bool IsDefinitePlacementRejection(Exception ex) =>
        ex is TradovateOrderRejectedException ||
        ex is TradovateException { Status: 400 or 401 or 403 or 404 or 405 or 422 or 429 };

    /// <summary>Cancels an order. Sent in the account's send-turn order: after earlier requests were answered, or after
    /// <see cref="NativeSendTurns.ReducingWait"/>, then returning <see cref="NativeSendTurns.UnsettledMessage"/>.</summary>
    public async Task<string?> CancelAsync(long accountId, long orderId, CancellationToken ct)
    {
        var turn = SendTurns(accountId).Take();
        try
        {
            bool inOrder = await turn.WaitAsync(bounded: true, ct).ConfigureAwait(false);
            var res = await _conn.PostAsync("/order/cancelorder", new JsonObject { ["orderId"] = orderId }, ct).ConfigureAwait(false);
            if (Str(res, "failureReason") is { Length: > 0 } r && r != "Success") throw new TradovateException("Tradovate did not cancel order: " + (Str(res, "failureText") is { Length: > 0 } t ? t : r));
            if (Str(res, "failureReason") != "Success" && Long(res, "commandId") == 0)
                throw new InvalidOperationException("Tradovate cancel acceptance is unknown: no command acknowledgment returned.");
            return inOrder ? null : NativeSendTurns.UnsettledMessage;
        }
        finally { turn.Release(); }
    }

    public async Task ModifyAsync(TradovateOrderState o, int qty, decimal price, CancellationToken ct)
    {
        var body = new JsonObject { ["orderId"] = o.Id, ["orderQty"] = qty, ["orderType"] = o.Type.Length > 0 ? o.Type : "Limit", ["timeInForce"] = "GTC" };
        if (o.Type is "Stop" or "StopLimit") body["stopPrice"] = price;
        else body["price"] = price;
        var res = await _conn.PostAsync("/order/modifyorder", body, ct).ConfigureAwait(false);
        if (Str(res, "failureReason") is { Length: > 0 } r && r != "Success") throw new TradovateOrderRejectedException("Tradovate did not modify order: " + (Str(res, "failureText") is { Length: > 0 } t ? t : r));
    }

    /// <summary>
    /// Liquidates a contract's position, in the account's send-turn order like <see cref="CancelAsync"/>. The request
    /// carries no quantity: Tradovate cancels the contract's orders and closes the position it holds when it processes
    /// the request (https://partner.tradovate.com/api/rest-api-endpoints/orders/liquidate-position), so an earlier order
    /// answered before it is included. A refusal (PlaceOrderResult.failureReason) is thrown, not reported as sent.
    /// </summary>
    public async Task<string?> LiquidateAsync(long accountId, long contractId, CancellationToken ct)
    {
        var turn = SendTurns(accountId).Take();
        try
        {
            bool inOrder = await turn.WaitAsync(bounded: true, ct).ConfigureAwait(false);
            var res = await _conn.PostAsync("/order/liquidateposition", new JsonObject { ["accountId"] = accountId, ["contractId"] = contractId, ["admin"] = false }, ct).ConfigureAwait(false);
            if (Str(res, "failureReason") is { Length: > 0 } r && r != "Success")
                throw new TradovateException("Tradovate did not liquidate the position: " + (Str(res, "failureText") is { Length: > 0 } t ? t : r));
            return inOrder ? null : NativeSendTurns.UnsettledMessage;
        }
        finally { turn.Release(); }
    }

    /// <summary>
    /// The send turns of one native account, shared by every broker (instrument) and the Accounts page: each order
    /// request takes its turn where it is issued and is sent only after earlier requests of that account were answered.
    /// </summary>
    internal NativeSendTurns SendTurns(long accountId)
    {
        lock (_sendTurns)
        {
            if (!_sendTurns.TryGetValue(accountId, out var turns)) _sendTurns[accountId] = turns = new NativeSendTurns(SendTurnWait);
            return turns;
        }
    }

    internal int QueuedSends(long accountId) => SendTurns(accountId).Waiting;

    /// <summary>How long a reducing request waits for earlier ones (tests shorten it before the first request).</summary>
    internal TimeSpan SendTurnWait { get; set; } = NativeSendTurns.ReducingWait;

    /// <summary>How long an amendment waits for the venue to report the order it amends before it is refused unsent (tests
    /// shorten it). An entry amendment holds its send turn meanwhile, so without a bound one order the venue never
    /// reports would hold the account's queue until it was cancelled.</summary>
    internal TimeSpan UnreportedAmendmentWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Completes once every order request already issued on any account of this session was answered, refused
    /// or failed. Quit awaits it before connections close.</summary>
    public Task SendsIdle
    {
        get
        {
            lock (_sendTurns) return Task.WhenAll(_sendTurns.Values.Select(turns => turns.WhenIdle).ToArray());
        }
    }

    public void Dispose() => _life.Cancel();
}

internal sealed class TradovateOrderRejectedException(string message) : Exception(message)
{
}

/// <summary>
/// Una cuenta de Tradovate operando un futuro (ES → el contrato del mes, ESZ6). Sirve con los gráficos de cualquier
/// conexión de futuros (Tradovate, Rithmic): el Chart Trader, el DOM, los stops y las estrategias operan igual.
/// </summary>
public sealed class TradovateBroker : ITradingAccount, IThreadSafeOrderEntry, ITrackedOrderReplacement, INativeProtection, ICoordinatedPositionOperations, INativeSendQueue, IDisposable
{
    private readonly InstrumentData _data;
    private readonly TradovateTradingSession _session;
    private readonly TradovateAccount _account;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private bool _ownershipNotificationPending;
    private readonly Queue<OwnedOrderQuantity> _quantityNotifications = new();
    private bool _publishingQuantities;
    private readonly Dictionary<long, Order> _byId = new();
    private readonly List<Order> _pending = new();
    private readonly Dictionary<string, Order> _byClientId = new(StringComparer.Ordinal);
    private readonly Dictionary<Order, PreparedOrderIntent> _intents = new();
    private readonly HashSet<Order> _cancelPending = new();
    private readonly HashSet<Order> _cancelInFlight = new();
    private readonly HashSet<Order> _cancelAwaitingAck = new();
    private sealed record Replacement(PreparedOrderIntent Intent, decimal Price, int Total);
    private readonly Dictionary<Order, Replacement> _replacements = new();
    private readonly Dictionary<Order, PreparedOrderIntent> _queuedReprices = new();
    private (long Id, string Name)? _contract;
    private const string ContractLookupError = "Could not determine Tradovate's active contract: ";
    private readonly object _contractGate = new();
    // The contract lookup in flight, shared by its callers; a failed one is forgotten so the next caller looks again.
    private Task<(long Id, string Name)>? _contractLookup;
    private int _contractFailures;
    private long _contractRetryAt; // Environment.TickCount64 from which a session change retries a failed lookup
    private bool _nativeAccountVerified;
    private readonly Func<bool> _nativeProtectionEnabled;
    private LinkScope? _link;
    private volatile string? _nativeFault; // set under _gate; read and cleared lock-free by CurrentNativeFault
    private readonly HashSet<long> _recoveryChecked = new();
    // Linked legs whose re-ownership check failed, by native order ID; each is checked again on the next sync.
    private readonly Dictionary<long, string> _recoveryFaults = new();
    private volatile string? _recoveryFault; // the first of _recoveryFaults, read lock-free by LastError
    // Every order this app asked Tradovate to cancel, in request order. An entry outlives the acknowledgment by one sync
    // (consumers read it on the change that reports the cancellation) and while its OCO partner still works; beyond the
    // cap the oldest ended entries go first, and a working order's entry is never evicted.
    private readonly Dictionary<Order, long> _cancelRequested = new();
    private readonly List<Order> _cancelRequestPrunable = new();
    private long _cancelRequestSequence;

    /// <summary>Upper bound on remembered cancel requests of ended orders.</summary>
    public int CancelRequestMemory { get; init; } = 1024;

    /// <summary>How long Close position waits for a position that includes earlier market orders (tests shorten it).</summary>
    internal TimeSpan CloseConfirmationWait { get; set; } = NativeSendTurns.ReducingWait;

    /// <summary>Requests issued on this account (by any instrument or the Accounts page) waiting for an earlier reply.</summary>
    public int QueuedRequests => _session.SendTurns(_account.Id).Queued;
    // Published whole under _gate and never mutated afterwards, so readers need no lock: the working handles of the
    // last Sync, and _pending as of its last change.
    private volatile List<Order> _active = new();
    private volatile Order[] _pendingSnapshot = Array.Empty<Order>();
    private readonly object _executionProducer = new();
    private readonly SemaphoreSlim _executionWake = new(0, 1);
    private readonly HashSet<long> _executionPending = new();
    private readonly HashSet<long> _executionUnowned = new();
    private readonly HashSet<long> _executionAttributionRetry = new();
    private readonly Dictionary<long, ExecutionReport> _publishedExecutions = new();
    private readonly Dictionary<long, (ExecutionFee[] Fees, bool ComponentsKnown)> _executionFees = new();
    private readonly Dictionary<long, DateTime> _feeRefresh = new();
    private readonly Queue<long> _feeRefreshOrder = new();
    private readonly HashSet<long> _feeRefreshQueued = new();
    private readonly (long Id, long Request)[] _feeWork = new (long, long)[8];
    private readonly Dictionary<long, string> _currencies = new();
    private readonly Dictionary<long, string> _executionFaults = new();
    private readonly Dictionary<long, string> _feeErrors = new();
    private readonly Dictionary<long, long> _feeRequests = new();
    private readonly Queue<(ExecutionReport[] Reports, ExecutionAvailability Availability)> _executionPublications = new();
    private int _publishingExecutions;
    private ExecutionAvailability? _queuedExecutionAvailability;
    private string? _executionError;
    private static readonly (string Amount, string Currency)[] FeeComponents =
    {
        ("clearingFee", "clearingCurrencyId"), ("exchangeFee", "exchangeCurrencyId"),
        ("nfaFee", "nfaCurrencyId"), ("brokerageFee", "brokerageCurrencyId"),
        ("ipFee", "ipCurrencyId"), ("commission", "commissionCurrencyId"),
        ("orderRoutingFee", "orderRoutingCurrencyId")
    };

    private void WakeExecutions()
    {
        try { _executionWake.Release(); }
        catch (SemaphoreFullException) { }
    }

    // Caller holds _gate. Keep each nonfinal fill in one bounded round-robin slot, including
    // fully observed snapshots: their amounts can still change independently of the Fill entity.
    private void ScheduleFeeRefresh(long id, DateTime due)
    {
        _feeRefresh[id] = due;
        if (_feeRefreshQueued.Add(id)) _feeRefreshOrder.Enqueue(id);
    }

    private void QueueExecution(long id)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _executionPending.Add(id);
            _feeRequests[id] = _feeRequests.GetValueOrDefault(id) + 1;
            ScheduleFeeRefresh(id, DateTime.UtcNow);
        }
        WakeExecutions();
    }

    private void QueueUnownedExecutions()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _executionPending.UnionWith(_executionUnowned);
            _executionPending.UnionWith(_executionAttributionRetry);
        }
        WakeExecutions();
    }

    private bool ExecutionRouteReady => _contract != null && _session.SnapshotReady &&
        OrderContext.StorageRoot != null && OrderContext.Descriptor is
        { Id.Length: > 0, Generation.Length: > 0, LocalNamespace.Length: > 0, Provider: "Tradovate" };

    // Called after Sync releases the provider gate; unknown fees never delay actual price replay.
    private void PublishKnownExecutions()
    {
        if (Executions is not ExecutionLog) return;
        long[] ids;
        lock (_gate)
        {
            if (_disposed) return;
            ids = ExecutionRouteReady ? _executionPending.ToArray() : Array.Empty<long>();
            if (ids.Length > 0) _executionPending.Clear();
        }
        List<(TradovateNativeFill Native, ExecutionReport Report)>? candidates = null;
        foreach (long id in ids)
        {
            var native = _session.NativeFill(id);
            if (native == null) continue;
            var fill = native.Fill;
            var descriptor = OrderContext.Descriptor;
            if (fill.AccountId != _account.Id)
            {
                lock (_gate)
                {
                    if (_publishedExecutions.ContainsKey(id))
                        _executionFaults[id] = $"Tradovate fill {id} changed its native account/contract lineage; reconciliation is required.";
                    else if (fill.AccountId == 0) _executionUnowned.Add(id);
                    else { _executionUnowned.Remove(id); _feeRefresh.Remove(id); }
                }
                continue;
            }
            // Scope the execution to the exact native contract, not its numeric entity ID or
            // the chart's rolling root. This is the same route recorded before native dispatch.
            string? nativeInstrument = _session.Connection.KnownContractName(native.ContractId);
            if (string.IsNullOrWhiteSpace(nativeInstrument))
            {
                lock (_gate) _executionPending.Add(id);
                continue;
            }
            var orderIdentity = new NativeOrderIdentity("tradovate:order", fill.OrderId.ToString(CultureInfo.InvariantCulture));
            var report = new ExecutionReport(
                new(new ExecutionScope(descriptor.Provider, _account.Id.ToString(CultureInfo.InvariantCulture),
                    descriptor.Environment, nativeInstrument, descriptor.Generation)
                    { ContextId = descriptor.Id }, id.ToString(CultureInfo.InvariantCulture)),
                0, Instrument.Symbol, fill.Time, fill.Buy, fill.Quantity, fill.Price, Instrument.PointValue,
                string.IsNullOrWhiteSpace(Instrument.QuoteAsset) ? null : Instrument.QuoteAsset,
                Array.Empty<ExecutionFee>(), false)
            { ContextId = descriptor.Id, ClientOrderId = native.ClientOrderId, NativeOrder = orderIdentity };
            try
            {
                var intent = OrderContext.Resolve(report);
                // An exact account/contract Order entity can complete a lost-acceptance client binding.
                // A matching client tag on another account never reaches this branch.
                intent ??= native.ClientOrderId.Length == 0 ? null :
                    OrderContext.Resolve(report with { NativeOrder = null });
                if (intent == null || intent.Request.Instrument != Instrument.Key ||
                    intent.NativeInstrument != nativeInstrument ||
                    (intent.Request.Direction == OrderDirections.Buy) != fill.Buy)
                {
                    lock (_gate) _executionUnowned.Add(id);
                    continue;
                }
                if (!(intent.NativeOrders ?? Array.Empty<NativeOrderIdentity>()).Contains(orderIdentity))
                {
                    if (!TradingHub.Trading.OrderContext.CanRecoverNativeIdentity(intent, orderIdentity) ||
                        intent.ClientOrderId != native.ClientOrderId)
                    {
                        lock (_gate) _executionUnowned.Add(id);
                        continue;
                    }
                    var bindingError = PersistNativeBinding(new PreparedOrderIntent(OrderContext, intent), orderIdentity);
                    PublishOwnershipNotification();
                    if (bindingError != null) throw new IOException(bindingError);
                }
                lock (_gate)
                {
                    if (_disposed || descriptor != OrderContext.Descriptor || !ReferenceEquals(native, _session.NativeFill(id)))
                    {
                        if (!_disposed) _executionPending.Add(id);
                        continue;
                    }
                    var accountError = _session.ReportsAccount(_account.Id)
                        ? OrderContext.VerifyNativeAccount(_account.Id.ToString(CultureInfo.InvariantCulture))
                        : "The native account has not been reported by this Tradovate session.";
                    if (accountError != null) throw new InvalidOperationException(accountError);
                    _executionUnowned.Remove(id);
                    _executionAttributionRetry.Remove(id);
                    if (native.Active != true || native.Deleted)
                    {
                        _executionFaults[id] = native.Active == null && !native.Deleted
                            ? $"Tradovate fill {id} does not report Fill.active; reconciliation is required."
                            : $"Tradovate fill {id} requires reconciliation: vendor Fill.active=false/Deleted correction and bust semantics are not established; no zero-quantity reversal was inferred.";
                        continue;
                    }
                    if (fill.Quantity <= 0 || fill.Time == DateTime.MinValue ||
                        !native.Raw.TryGetProperty("price", out var nativePrice) || nativePrice.ValueKind != JsonValueKind.Number ||
                        !native.Raw.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                        action.GetString() is not ("Buy" or "Sell"))
                    {
                        _executionFaults[id] = $"Tradovate fill {id} lacks a valid native quantity, price, timestamp or action; reconciliation is required.";
                        continue;
                    }
                    if (_publishedExecutions.TryGetValue(id, out var previous) &&
                        (previous.NativeOrder != orderIdentity || previous.Identity.Scope != report.Identity.Scope))
                    {
                        _executionFaults[id] = $"Tradovate fill {id} changed its native identity lineage; reconciliation is required.";
                        continue;
                    }
                    (candidates ??= new()).Add((native, report with { IntentId = intent.Id }));
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _executionAttributionRetry.Add(id);
                    _executionFaults[id] = $"Tradovate fill {id} attribution is unresolved: {ex.Message}";
                }
            }
        }
        if (candidates is { Count: > 1 })
            candidates.Sort(static (a, b) =>
            {
                int time = a.Native.Fill.Time.CompareTo(b.Native.Fill.Time);
                return time != 0 ? time : a.Native.Fill.Id.CompareTo(b.Native.Fill.Id);
            });
        lock (_gate)
        {
            if (_disposed) return;
            List<ExecutionReport>? reports = null;
            if (candidates != null)
                foreach (var item in candidates)
                {
                    long id = item.Native.Fill.Id;
                    if (!ReferenceEquals(item.Native, _session.NativeFill(id)))
                    {
                        _executionPending.Add(id);
                        continue;
                    }
                    var report = item.Report with
                    {
                        LocalOrderId = _byId.TryGetValue(item.Native.Fill.OrderId, out var order) ? order.Id : 0,
                        Fees = _executionFees.TryGetValue(id, out var fee) ? fee.Fees : Array.Empty<ExecutionFee>()
                    };
                    _executionFaults.Remove(id);
                    _executionAttributionRetry.Remove(id);
                    if (_publishedExecutions.TryGetValue(id, out var previous) &&
                        (previous with { Fees = report.Fees }) == report &&
                        previous.Fees.AsSpan().SequenceEqual(report.Fees)) continue;
                    _publishedExecutions[id] = report;
                    (reports ??= new()).Add(report);
                    if (!_executionFees.ContainsKey(id) && !_feeRefresh.ContainsKey(id)) ScheduleFeeRefresh(id, DateTime.UtcNow);
                }
            _executionError = _executionFaults.Values.FirstOrDefault() ?? _feeErrors.Values.FirstOrDefault();
            var availability = !ExecutionRouteReady ? ExecutionAvailability.Synchronizing :
                _executionFaults.Count > 0 ? ExecutionAvailability.Unavailable : ExecutionAvailability.Ready;
            if (reports == null && availability == _queuedExecutionAvailability) return;
            _queuedExecutionAvailability = availability;
            _executionPublications.Enqueue((reports?.ToArray() ?? Array.Empty<ExecutionReport>(), availability));
        }
        DrainExecutionPublications();
    }

    private void DrainExecutionPublications()
    {
        if (Interlocked.CompareExchange(ref _publishingExecutions, 1, 0) != 0) return;
        try
        {
            while (true)
            {
                (ExecutionReport[] Reports, ExecutionAvailability Availability) batch;
                lock (_gate)
                {
                    if (_disposed || _executionPublications.Count == 0) return;
                    batch = _executionPublications.Dequeue();
                }
                var log = (ExecutionLog)Executions;
                if (batch.Reports.Length > 0) log.PublishBatch(batch.Reports);
                log.SetProducerAvailability(_executionProducer, batch.Availability);
                if (_disposed) log.SetProducerAvailability(_executionProducer, null);
            }
        }
        finally
        {
            Volatile.Write(ref _publishingExecutions, 0);
            bool pending;
            lock (_gate) pending = !_disposed && _executionPublications.Count > 0;
            if (pending) DrainExecutionPublications();
        }
    }

    private async Task ExecutionLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _executionWake.WaitAsync(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
                string? previousError = _executionError;
                lock (_gate)
                {
                    if (_disposed) return;
                    // Retry only unresolved attribution work, never the lifetime fill history.
                    _executionPending.UnionWith(_executionAttributionRetry);
                }
                PublishKnownExecutions();
                int dueCount = 0;
                lock (_gate)
                {
                    if (!ExecutionRouteReady) continue;
                    DateTime now = DateTime.UtcNow;
                    int visits = Math.Min(_feeRefreshOrder.Count, 32);
                    while (visits-- > 0 && dueCount < _feeWork.Length)
                    {
                        long id = _feeRefreshOrder.Dequeue();
                        if (!_feeRefresh.TryGetValue(id, out var due))
                        {
                            _feeRefreshQueued.Remove(id);
                            continue;
                        }
                        _feeRefreshOrder.Enqueue(id);
                        if (due > now || !_publishedExecutions.ContainsKey(id) || _executionFaults.ContainsKey(id)) continue;
                        _feeWork[dueCount++] = (id, _feeRequests.GetValueOrDefault(id));
                        _feeRefresh[id] = DateTime.MaxValue;
                    }
                }
                for (int i = 0; i < dueCount; i++)
                {
                    var item = _feeWork[i];
                    try
                    {
                        var fees = await ReadFillFeesAsync(item.Id, _cts.Token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            if (_disposed) return;
                            _executionFees[item.Id] = fees;
                            _feeErrors.Remove(item.Id);
                            _executionPending.Add(item.Id);
                            if (_feeRequests.GetValueOrDefault(item.Id) != item.Request)
                                ScheduleFeeRefresh(item.Id, DateTime.UtcNow);
                            else ScheduleFeeRefresh(item.Id, DateTime.UtcNow.AddSeconds(2));
                        }
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        lock (_gate)
                        {
                            if (_disposed) return;
                            _feeErrors[item.Id] = $"Tradovate fill {item.Id} fee snapshot is unavailable: {ex.Message}";
                            ScheduleFeeRefresh(item.Id, DateTime.UtcNow.AddSeconds(2));
                        }
                    }
                }
                PublishKnownExecutions();
                if (previousError != _executionError) Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    private async Task<(ExecutionFee[] Fees, bool ComponentsKnown)> ReadFillFeesAsync(long fillId, CancellationToken ct)
    {
        var snapshot = await _session.Connection.GetAsync(
            "/fillFee/deps?masterid=" + fillId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        if (snapshot.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("FillFee dependents were not an array.");
        var fees = new List<ExecutionFee>();
        var entities = new HashSet<long>();
        bool known = snapshot.GetArrayLength() > 0;
        foreach (var entity in snapshot.EnumerateArray())
        {
            if (entity.ValueKind != JsonValueKind.Object ||
                !entity.TryGetProperty("id", out var identity) || !identity.TryGetInt64(out long feeId) ||
                feeId == 0 || !entities.Add(feeId))
            { known = false; continue; }
            foreach (var component in FeeComponents)
            {
                if (!entity.TryGetProperty(component.Amount, out var value) ||
                    value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal amount))
                { known = false; continue; }
                string? currency = null;
                if (entity.TryGetProperty(component.Currency, out var currencyId) &&
                    currencyId.ValueKind == JsonValueKind.Number && currencyId.TryGetInt64(out long id) && id != 0)
                {
                    if (!_currencies.TryGetValue(id, out currency))
                    {
                        try
                        {
                            var data = await _session.Connection.GetAsync(
                                "/currency/item?id=" + id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                            if (data.ValueKind == JsonValueKind.Object &&
                                data.TryGetProperty("id", out var nativeId) && nativeId.TryGetInt64(out long actualId) && actualId == id &&
                                data.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(name.GetString()))
                                _currencies[id] = currency = name.GetString()!;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception) { /* Preserve this known amount with an explicitly unknown unit; retry it. */ }
                    }
                }
                if (currency == null) known = false;
                fees.Add(new(amount, currency));
            }
        }
        // Even seven known pairs are revisable charge observations, not cash-statement finality.
        return (fees.ToArray(), known);
    }

    public TradovateBroker(InstrumentData data, TradovateTradingSession session, TradovateAccount account, string name, OrderContext? orderContext = null,
        Func<bool>? exchangeSideProtection = null)
    {
        _data = data;
        _session = session;
        _account = account;
        Name = name;
        _nativeProtectionEnabled = exchangeSideProtection ?? (static () => false);
        OrderContext = orderContext ?? new OrderContext(new AccountContextDescriptor("", "Tradovate", "futures", "unconfigured", "", ""));
        Position = new Position { Security = new Security { Code = data.Instrument.Symbol, SecurityId = data.Instrument.Key } };
        if (Executions is ExecutionLog log)
        {
            log.SetProducerAvailability(_executionProducer, ExecutionAvailability.Synchronizing);
            _session.NativeFillChanged += QueueExecution;
            OrderContext.OwnershipChanged += QueueUnownedExecutions;
            lock (_gate)
                foreach (long id in _session.NativeFillIds()) _executionPending.Add(id);
            _ = Task.Run(ExecutionLoopAsync);
        }
        _session.Changed += Sync;
        _ = ContractAsync();
    }

    public string Name { get; }
    public bool IsReal => !_session.IsDemo;
    public Instrument Instrument => _data.Instrument;
    public decimal LastPrice => _data.LastPrice;
    public Position Position { get; }
    /// <summary>The working handles of the last Sync, less those that ended since. Never waits on the broker gate, which
    /// order entry holds across durable journal writes: readers (the UI thread among them) see the previous publication
    /// until the entry finishes.</summary>
    public IEnumerable<Order> ActiveOrders => _active.Where(o => o.State == OrderStates.Active);
    private string? _lastError;
    private string? _ownershipFault;
    public string? LastError
    {
        get
        {
            string? fault = _executionError ?? _ownershipFault ?? _recoveryFault ?? OrderContext.LastError;
            return fault == null ? _lastError : _lastError == null ? fault : _lastError + "; " + fault;
        }
        private set => _lastError = value;
    }
    public bool PositionReady => _contract != null && _session.SnapshotReady;
    /// <summary>Lock-free like <see cref="ActiveOrders"/>: false while any submitted handle still works without a
    /// Tradovate order id.</summary>
    public bool OrdersReady
    {
        get
        {
            if (_contract == null || !_session.SnapshotReady) return false;
            foreach (var order in _pendingSnapshot)
                if (order.State == OrderStates.Active && order.ExchangeOrderId.Length == 0) return false;
            return true;
        }
    }
    public event Action? Changed;

    // Native fill/fee attribution is distinct from order status and is not inferred from this order snapshot.
    public OrderContext OrderContext { get; }
    public IExecutionSource Executions => OrderContext.Executions;

    /// <summary>Raíz del contrato (NQ) aunque el gráfico sea de otra conexión.</summary>
    private string Root => _data.Instrument.Symbol.Split(' ', ':')[0];

    /// <summary>The contract this broker trades. Callers share the lookup in flight; a failure is reported, and the next
    /// call looks it up again.</summary>
    private Task<(long Id, string Name)> ContractAsync()
    {
        if (_contract is { } known) return Task.FromResult(known);
        TaskCompletionSource<(long Id, string Name)> lookup;
        lock (_contractGate)
        {
            if (_contract is { } c) return Task.FromResult(c);
            if (_contractLookup is { } running) return running;
            lookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _contractLookup = lookup.Task;
        }
        _ = LookUpContractAsync(lookup);
        return lookup.Task;
    }

    // Runs synchronously up to its first incomplete await: a contract the session already reported is known, and
    // synced, before the constructor returns.
    private async Task LookUpContractAsync(TaskCompletionSource<(long Id, string Name)> lookup)
    {
        (long Id, string Name) contract;
        try
        {
            contract = await _session.Connection.ContractAsync(Root, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Forgotten before it is reported, so a session change after the report looks it up again.
            lock (_contractGate)
            {
                _contractLookup = null;
                int failures = ++_contractFailures;
                Volatile.Write(ref _contractRetryAt, failures == 1 ? 0 :
                    Environment.TickCount64 + (long)LiveSubscription.Backoff(failures - 1).TotalMilliseconds);
            }
            LastError = ContractLookupError + ex.Message;
            try { Changed?.Invoke(); }
            finally { lookup.SetException(ex); }
            return;
        }
        lock (_contractGate)
        {
            _contract = contract;
            _contractLookup = null;
        }
        if (_lastError?.StartsWith(ContractLookupError, StringComparison.Ordinal) == true) LastError = null;
        try { Sync(); }
        finally { lookup.SetResult(contract); }
    }

    /// <summary>While the contract is unknown, each session change (its replay, a reconnect, the periodic balance refresh)
    /// looks it up again: at once after the first failure, then backing off. Never from a loop of its own, so readiness
    /// does not wait for an order that would look it up.</summary>
    private void RetryContract()
    {
        if (_disposed || Environment.TickCount64 < Volatile.Read(ref _contractRetryAt)) return;
        _ = ContractAsync();
    }

    private void Apply(TradovateOrderState s, Order o)
    {
        decimal previousFilled = o.Filled;
        var previousState = o.State;
        var previousStatus = o.Status;
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
        ObserveNativeQuantity(o, previousFilled, previousState, previousStatus);
    }

    private void Sync()
    {
        if (_contract is not { } c)
        {
            RetryContract();
            return;
        }
        List<Order>? cancellations = null;
        var active = new List<Order>();
        lock (_gate)
        {
            if (_disposed) return;
            // A disconnect publishes cached native states, not a fresh working-order replay.
            // Retain obligations, but retry them only when the replacement user channel can report the result.
            bool replayReady = _session.SnapshotReady;
            if (!replayReady) _cancelAwaitingAck.Clear();
            var states = _session.OrdersOf(_account.Id, c.Id);
            // Entries found ended on the previous sync have been reported to consumers; forget them now.
            foreach (var ended in _cancelRequestPrunable) _cancelRequested.Remove(ended);
            _cancelRequestPrunable.Clear();
            foreach (var s in states)
            {
                Order o;
                if (s.ClientOrderId.Length > 0 && _byClientId.TryGetValue(s.ClientOrderId, out var owned) &&
                    (owned.ExchangeOrderId.Length == 0 || IdOf(owned) == s.Id))
                {
                    o = owned;
                    o.ExchangeOrderId = s.Id.ToString(CultureInfo.InvariantCulture);
                    _byId[s.Id] = o;
                    if (LastError?.StartsWith("Tradovate order acceptance is unknown;", StringComparison.Ordinal) == true ||
                        LastError?.StartsWith("Tradovate OCO acceptance is unknown;", StringComparison.Ordinal) == true)
                        LastError = null;
                }
                else if (!_byId.TryGetValue(s.Id, out o!))
                {
                    if (s.Done) continue; // Completed before this broker observed it.
                    o = new Order { Security = Position.Security, ExchangeOrderId = s.Id.ToString(CultureInfo.InvariantCulture), Comment = "Tradovate" };
                    _byId[s.Id] = o;
                }
                if (string.IsNullOrEmpty(o.OwnerId) && s.ClientOrderId.Length > 0) o.ClientOrderId = s.ClientOrderId;

                BindNative(o, s.Id);
                Apply(s, o);
                if (s.OcoId > 0 && !s.Done && s.ClientOrderId.Length > 0 && string.IsNullOrEmpty(o.OwnerId) && !_intents.ContainsKey(o))
                    RecoverLinkedLeg(o, s, c.Name);
                if (_replacements.TryGetValue(o, out var replacement) &&
                    (s.Done || (s.Quantity == replacement.Total &&
                    (o.Type == OrderTypes.Stop ? s.StopPrice : s.Price) == replacement.Price)))
                {
                    _replacements.Remove(o);
                    if (!_queuedReprices.ContainsKey(o)) _session.ResolveReplacement(o);
                }
                _pending.Remove(o);
                if (o.State != OrderStates.Active)
                {
                    _cancelPending.Remove(o);
                    _cancelAwaitingAck.Remove(o);
                    RejectQueuedReprice(o, "The order became terminal before the queued price update.");
                    _session.ResolveReplacement(o);
                }
                if (s.Rejected && s.Text.Length > 0) LastError = "Tradovate rejected order: " + s.Text;
                if (s.Rejected && _intents.TryGetValue(o, out var rejected))
                    RecordRejected(rejected, s.Text);
                if (o.State == OrderStates.Active) active.Add(o);
            }

            // las recién mandadas que todavía no aparecieron
            foreach (var o in _pending.Where(o => o.State == OrderStates.Active && !active.Contains(o))) active.Add(o);
            _pending.RemoveAll(o => o.State != OrderStates.Active);
            _pendingSnapshot = _pending.ToArray();
            foreach (var order in _cancelPending)
                if (replayReady && order.State == OrderStates.Active && IdOf(order) != 0 &&
                    !_cancelAwaitingAck.Contains(order) && _cancelInFlight.Add(order))
                    (cancellations ??= new()).Add(order);
            if (replayReady)
                foreach (var (order, intent) in _queuedReprices.ToArray())
                {
                    if (_replacements.ContainsKey(order)) continue;
                    if (order.State == OrderStates.Active && order.Unfilled <= 0) continue;
                    _queuedReprices.Remove(order);
                    if (order.State == OrderStates.Active && !_cancelPending.Contains(order))
                        StartReplacement(order, intent, null);
                    else
                    {
                        RecordRejected(intent, "The order became terminal or cancellation was requested before the queued price update.");
                        _session.ResolveReplacement(order);
                    }
                }
            foreach (var order in active)
                if (string.IsNullOrEmpty(order.OwnerId) && order.NativeIdentity != null)
                    order.ContextId = OrderContext.Descriptor.Id;
            _active = active;
            PruneCancelRequests(states);
        }
        PublishOwnershipNotification();
        PublishQuantityNotifications();

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
        PublishKnownExecutions();
        WakeExecutions();
        Changed?.Invoke();
    }

    public Order Submit(PreparedOrderIntent intent) => Submit(intent, null);

    /// <summary>
    /// Close position. Working (non-market) orders are canceled; the account's market orders still in flight are part of
    /// what is closed. The close takes its send turn here; there, after the earlier requests were answered, its side and
    /// size come from a position confirmed to include those orders' fills (see <see cref="NativeSendTurns.PlanPositionOrder"/>).
    /// </summary>
    public void Flatten() => SendPositionOrder(reverse: false);

    /// <summary>
    /// Reverse, as before one opposite market order of twice the position, ending at minus that position. Planned like
    /// <see cref="Flatten"/> from the confirmed position in its send turn; refused when that position stays unconfirmed.
    /// </summary>
    public void Reverse() => SendPositionOrder(reverse: true);

    private void SendPositionOrder(bool reverse)
    {
        var (earlier, before, expected) = ExpectedPosition();
        if (!reverse) foreach (var working in _active.Where(o => o.Type != OrderTypes.Market).ToList()) Cancel(working);
        if (expected == 0 && earlier.Length == 0) return; // flat with nothing in flight
        var until = DateTime.UtcNow + CloseConfirmationWait;
        var turn = _session.SendTurns(_account.Id).Take();
        _ = Task.Run(async () =>
        {
            HandedTurn? handoff = null;
            try
            {
                await ContractAsync().ConfigureAwait(false);
                // A reverse adds exposure: it waits for earlier replies without the reducing bound.
                bool inOrder = await turn.WaitAsync(bounded: !reverse, _cts.Token).ConfigureAwait(false);
                await WaitForNativeReplayAsync().ConfigureAwait(false);
                var (net, pending, confirmed) = await PositionAfterAsync(earlier, until).ConfigureAwait(false);
                var plan = NativeSendTurns.PlanPositionOrder(reverse, expected, net, pending, confirmed);
                if (plan.Refusal != null)
                {
                    LastError = plan.Refusal;
                    Changed?.Invoke();
                    return;
                }
                var intent = OrderContext.Prepare(OrderContext.ManualOwner, new OrderRequest(Instrument.Key, plan.Direction, OrderTypes.Market,
                    plan.Quantity, 0, ReduceOnly: !reverse, Comment: reverse ? "Reverse" : "Close", Protective: !reverse,
                    MaximumOwnedQuantity: Math.Max(Math.Abs(before), Math.Abs(net))));
                handoff = new HandedTurn(turn, inOrder && confirmed, plan.Note);
                Submit(intent, handoff);
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) return;
                LastError = ex.Message;
                Changed?.Invoke();
            }
            finally
            {
                // Released here unless the order's dispatch took the turn over (whatever Submit threw).
                if (handoff is not { Claimed: true }) turn.Release();
            }
        });
    }

    /// <summary>The account's own market orders in flight, the current position and the position including them.</summary>
    private (Order[] Earlier, decimal Before, decimal Expected) ExpectedPosition()
    {
        decimal before = Position.Volume;
        lock (_gate)
        {
            var earlier = _byClientId.Values.Where(o => o.Type == OrderTypes.Market && o.State == OrderStates.Active).ToArray();
            return (earlier, before, before + earlier.Sum(o => (o.Direction == OrderDirections.Buy ? 1 : -1) * o.Unfilled));
        }
    }

    /// <summary>A send turn already taken and waited for by a position order, handed to the order it planned: whether it
    /// went in order with a confirmed position, and the note to show once the order is accepted. The order's dispatch
    /// claims it; until then the position order still owns (and releases) it.</summary>
    private sealed class HandedTurn(NativeSendTurns.Turn turn, bool inOrder, string? note)
    {
        public NativeSendTurns.Turn Turn { get; } = turn;
        public bool InOrder { get; } = inOrder;
        public string? Note { get; } = note;
        public bool Claimed { get; set; }
    }

    private Order Submit(PreparedOrderIntent intent, HandedTurn? handed)
    {
        lock (_gate)
            if (_byClientId.TryGetValue(intent.ClientOrderId, out var existing)) return existing;
        var request = intent.Request;
        var dir = request.Direction;
        var type = request.Type;
        var price = request.Price;
        var comment = request.Comment;
        bool validQuantity = NativeFuturesQuantity.TryConvert(request.Quantity, out int q);
        var order = new Order { Security = Position.Security, Direction = dir, Type = type, QuantityToFill = request.Quantity, Unfilled = request.Quantity, Price = price, TriggerPrice = price, Comment = comment, State = OrderStates.Active, Status = OrderStatus.Placed, ReduceOnly = request.ReduceOnly };
        var bindingError = request.Instrument == Instrument.Key
            ? BindOwnedOrder(order, intent) : "The prepared instrument does not match this broker.";
        if (bindingError != null || !validQuantity)
        {
            order.State = OrderStates.Failed;
            order.Status = OrderStatus.Rejected;
            LastError = bindingError ?? NativeFuturesQuantity.Error;
            if (bindingError == null) RecordRejected(intent, NativeFuturesQuantity.Error);
            Changed?.Invoke();
            return order;
        }

        // Register ownership before any send or websocket event can race the HTTP reply.
        string clientOrderId = intent.ClientOrderId;
        NativeSendTurns.Turn turn;
        lock (_gate)
        {
            if (_byClientId.TryGetValue(clientOrderId, out var existing)) return existing;
            // Taken where the order is issued, with its registration, so before any sync can publish it: a request a
            // Changed subscriber issues on seeing it, or a cancel that sync dispatches, goes after it. A position order
            // hands over the turn it took when it was issued.
            turn = handed?.Turn ?? _session.SendTurns(_account.Id).Take();
            if (handed != null) handed.Claimed = true;
            _byClientId.Add(clientOrderId, order);
            _pending.Add(order);
            _pendingSnapshot = _pending.ToArray();
            _intents[order] = intent;
        }
        var leg = new PendingLeg(order, intent, dir, type, price, q, turn, handed);
        // A registered order is always dispatched (and releases its turn), even when publishing it throws.
        try { Sync(); }
        finally
        {
            if (!TryDeferLinkedLeg(leg)) DispatchSingle(leg);
        }
        return order;
    }

    /// <summary>An order to send with the send turn taken when it was issued; a deferred linked leg keeps its turn until
    /// its link scope closes.</summary>
    private sealed record PendingLeg(Order Order, PreparedOrderIntent Intent, OrderDirections Direction, OrderTypes Type, decimal Price, int Quantity,
        NativeSendTurns.Turn Turn, HandedTurn? Handed = null);

    /// <summary>
    /// The position a position order is planned from: confirmed once every market order in flight when it was issued has
    /// ended and the reported position includes its fills; past the deadline, the best known position, with the signed
    /// unfilled quantity of those orders still working.
    /// </summary>
    private async Task<(decimal Net, decimal Pending, bool Confirmed)> PositionAfterAsync(Order[] earlier, DateTime until)
    {
        while (true)
        {
            bool settled = false;
            decimal net = 0, pending = 0;
            if (_contract is { } contract)
            {
                settled = earlier.All(o => o.State != OrderStates.Active && PositionReflects(o));
                net = _session.PositionFor(_account.Id, contract.Id)?.Net ?? 0;
                pending = earlier.Where(o => o.State == OrderStates.Active).Sum(o => (o.Direction == OrderDirections.Buy ? 1 : -1) * o.Unfilled);
            }
            if (settled || DateTime.UtcNow >= until)
            {
                Sync(); // admission checks the position this order is planned from
                return (net, pending, settled);
            }
            await Task.Delay(20, _cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>A reducing request waits for earlier sends only up to <see cref="NativeSendTurns.ReducingWait"/>.</summary>
    private static bool Reducing(PreparedOrderIntent intent) => intent.Request.ReduceOnly || intent.Request.Protective;

    private void DispatchSingle(PendingLeg leg)
    {
        var (order, intent, dir, type, price, q, turn, handed) = leg;
        string clientOrderId = intent.ClientOrderId;
        _ = Task.Run(async () =>
        {
            bool placementStarted = false;
            try
            {
                var (_, contract) = await ContractAsync().ConfigureAwait(false);
                // A handed-over turn was already waited for when it was planned: it is not waited for again.
                bool inOrder = handed?.InOrder ?? await turn.WaitAsync(Reducing(intent), _cts.Token).ConfigureAwait(false);
                await WaitForNativeReplayAsync().ConfigureAwait(false);
                decimal px = price > 0 ? Instrument.RoundPrice(price) : 0;
                string ot = type == OrderTypes.Market ? "Market" : type == OrderTypes.Stop ? "Stop" : "Limit";
                bool automated = intent.Owner.Kind == OrderOwnerKind.Strategy;
                Task<long> placement;
                lock (_gate)
                {
                    if (_disposed) return;
                    var accountError = VerifyAccountEvidence();
                    if (accountError != null) throw new InvalidOperationException(accountError);
                    var dispatchError = StartOwnedDispatch(intent, contract, actualQuantity: q, actualPrice: px);
                    if (dispatchError != null) throw new InvalidOperationException(dispatchError);
                    placementStarted = true;
                    placement = _session.PlaceAsync(_account, contract, dir == OrderDirections.Buy, ot, q,
                        type == OrderTypes.Limit ? px : null, type == OrderTypes.Stop ? px : null, automated, clientOrderId, _cts.Token);
                }
                long id = await placement.ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) return;
                    order.ExchangeOrderId = id.ToString(CultureInfo.InvariantCulture);
                    _byId[id] = order;
                    BindNative(order, id);
                }

                LastError = handed?.Note ?? (inOrder ? null : NativeSendTurns.UnsettledMessage);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    // A transport failure, timeout, 408/5xx, or unreadable/missing reply is
                    // not a rejection. Keep the exact handle owned until clOrdId replay.
                    bool rejected = !placementStarted || TradovateTradingSession.IsDefinitePlacementRejection(ex);
                    // The error is set first: lock-free readers that see the handle end also see why.
                    LastError = order.ExchangeOrderId.Length > 0 ? null : rejected ? ex.Message :
                        "Tradovate order acceptance is unknown; awaiting client-tag reconciliation: " + ex.Message;
                    if (rejected && order.ExchangeOrderId.Length == 0)
                    {
                        order.State = OrderStates.Failed;
                        order.Status = OrderStatus.Rejected;
                        _cancelPending.Remove(order);
                        RecordRejected(intent, ex.Message);
                    }
                }
            }
            finally { turn.Release(); }

            Sync();
        });
    }

    // ===================================================================== exchange-side protection (placeOCO)

    /// <summary>
    /// Opt-in: a stop and a target of one exit sent together become one Tradovate OCO strategy
    /// (POST /order/placeoco, https://partner.tradovate.com/api/rest-api-endpoints/orders/place-oco), whose venue cancels the
    /// other leg when one fills even if this app is closed. Each leg keeps its own clOrdId, durable intent and ownership.
    /// </summary>
    public NativeProtectionFeatures NativeProtection => CurrentNativeFault(out bool enabled) == null && enabled
        ? NativeProtectionFeatures.LinkedStopAndTarget | NativeProtectionFeatures.NativeAmendment
        : NativeProtectionFeatures.None;

    public string? NativeProtectionFault => CurrentNativeFault(out _);

    // Observing the option off (Chart Trader reads it on every refresh) clears a refusal, so switching off and on retries.
    // Never waits on _gate, which order entry holds across durable journal writes: the option is read outside any lock
    // either way, so a clear racing a refusal set under _gate has the same outcomes the gate allowed.
    private string? CurrentNativeFault(out bool enabled)
    {
        enabled = _nativeProtectionEnabled();
        if (!enabled) _nativeFault = null;
        return _nativeFault;
    }

    public IDisposable? LinkProtection(OrderOwner owner)
    {
        var scope = new LinkScope(this, Environment.CurrentManagedThreadId, owner);
        lock (_gate)
        {
            // A scope opened inside another (a consumer reacting synchronously to an account change) collects nothing.
            if (_link != null) return null;
            _link = scope;
        }
        return scope;
    }

    public string? ProtectionGroupOf(Order order)
    {
        long id = IdOf(order);
        if (id == 0) return null;
        long oco = _session.OrderState(id)?.OcoId ?? 0;
        return oco > 0 ? "tradovate-oco:" + oco.ToString(CultureInfo.InvariantCulture) : null;
    }

    public bool CancelRequested(Order order)
    {
        lock (_gate) return _cancelRequested.ContainsKey(order);
    }

    // Tradovate fill and position entities both carry the venue's timestamp. Executions that have no dated fill report
    // yet (or only part of one) cannot be proven included, so a position never confirms them.
    public bool PositionReflects(Order order)
    {
        decimal executed = order.Status == OrderStatus.Filled ? Math.Max(order.Filled, order.QuantityToFill) : order.Filled;
        if (executed <= 0) return true;
        long id = IdOf(order);
        if (id == 0) return false;
        var (reportedFills, last) = _session.FillTimes(id);
        if (reportedFills < executed || last is not { } fill) return false;
        return _contract is { } c && _session.PositionFor(_account.Id, c.Id)?.Time is { } reported && reported >= fill;
    }

    // Called under the broker gate at the end of a sync. An ended order whose OCO partner has also ended (or that has none)
    // is forgotten on the next sync; the cap evicts the oldest ended entries first.
    private void PruneCancelRequests(List<TradovateOrderState> states)
    {
        if (_cancelRequested.Count == 0) return;
        var working = new HashSet<long>(states.Where(s => !s.Done && s.OcoId > 0).Select(s => s.OcoId));
        var ended = new List<(Order Order, long Sequence)>();
        foreach (var (order, sequence) in _cancelRequested)
        {
            if (order.State == OrderStates.Active) continue;
            ended.Add((order, sequence));
            long oco = _session.OrderState(IdOf(order))?.OcoId ?? 0;
            if (oco == 0 || !working.Contains(oco)) _cancelRequestPrunable.Add(order);
        }
        int excess = _cancelRequested.Count - CancelRequestMemory;
        foreach (var (order, _) in ended.OrderBy(e => e.Sequence))
        {
            if (excess-- <= 0) break;
            _cancelRequestPrunable.Remove(order);
            _cancelRequested.Remove(order);
        }
    }

    // Called under the broker gate. After a restart, a still-working OCO leg of manual (chart) protection is re-owned only
    // from its exact durable native claim or uncertain clOrdId dispatch in this account's journal; it is never resent and
    // a foreign OCO stays unowned. Strategy legs keep their runner's own kept-protection adoption. A leg counts as checked
    // once its check completes: one that failed (a journal read another process held, say) is checked on the next sync.
    private void RecoverLinkedLeg(Order order, TradovateOrderState state, string contract)
    {
        if (_recoveryChecked.Contains(state.Id)) return;
        var native = new NativeOrderIdentity("tradovate:order", state.Id.ToString(CultureInfo.InvariantCulture));
        try
        {
            if (OrderContext.FindNativeSubmission(state.ClientOrderId, native) is { } claim && claim.Owner == OrderContext.ManualOwner)
            {
                // Tradovate reports no reduce-only flag; the durable request is the authority for this owned exit.
                order.ReduceOnly = claim.Request.ReduceOnly;
                if (OrderContext.RecoverNativeOrder(order, state.ClientOrderId, native, Instrument.Key, contract, notify: false) is { } ticket)
                {
                    _byClientId[state.ClientOrderId] = order;
                    _intents[order] = ticket;
                    _ownershipNotificationPending = true;
                }
            }
            _recoveryChecked.Add(state.Id);
            if (_recoveryFaults.Remove(state.Id)) _recoveryFault = _recoveryFaults.Values.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _recoveryFaults[state.Id] = "Tradovate linked protection could not be re-owned after restart: " + ex.Message;
            _recoveryFault = _recoveryFaults.Values.First();
        }
    }

    private sealed class LinkScope(TradovateBroker broker, int thread, OrderOwner owner) : IDisposable
    {
        public readonly int Thread = thread;
        public readonly OrderOwner Owner = owner;
        public readonly List<PendingLeg> Legs = new();
        public void Dispose() => broker.CloseLink(this);
    }

    private bool TryDeferLinkedLeg(PendingLeg leg)
    {
        lock (_gate)
        {
            // Only the scope owner's own exits are collected: another owner's order is never paired with them.
            if (_link is not { } scope || scope.Thread != Environment.CurrentManagedThreadId || scope.Legs.Count >= 2 ||
                leg.Intent.Owner != scope.Owner || leg.Type is not (OrderTypes.Stop or OrderTypes.Limit) ||
                !(leg.Intent.Request.ReduceOnly || leg.Intent.Request.Protective))
                return false;
            scope.Legs.Add(leg);
            return true;
        }
    }

    private void CloseLink(LinkScope scope)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_link, scope)) return;
            _link = null;
        }
        var legs = scope.Legs;
        var stop = legs.FirstOrDefault(l => l.Type == OrderTypes.Stop);
        var target = legs.FirstOrDefault(l => l.Type == OrderTypes.Limit);
        bool pair = legs.Count == 2 && stop != null && target != null && stop.Direction == target.Direction &&
            stop.Quantity == target.Quantity && stop.Price > 0 && target.Price > 0 &&
            (NativeProtection & NativeProtectionFeatures.LinkedStopAndTarget) != 0;
        if (pair)
        {
            // One request sends both legs, in the turn of the leg issued first; the other leg's turn is not needed.
            legs[1].Turn.Release();
            DispatchLinked(stop!, target!, legs[0].Turn);
        }
        else foreach (var leg in legs) DispatchSingle(leg);
    }

    private void DispatchLinked(PendingLeg stop, PendingLeg target, NativeSendTurns.Turn turn)
    {
        _ = Task.Run(async () =>
        {
            bool placementStarted = false;
            try
            {
                var (_, contract) = await ContractAsync().ConfigureAwait(false);
                bool inOrder = await turn.WaitAsync(bounded: true, _cts.Token).ConfigureAwait(false); // protection reduces
                await WaitForNativeReplayAsync().ConfigureAwait(false);
                decimal stopPrice = Instrument.RoundPrice(stop.Price), targetPrice = Instrument.RoundPrice(target.Price);
                bool automated = stop.Intent.Owner.Kind == OrderOwnerKind.Strategy;
                Task<long> placement;
                lock (_gate)
                {
                    if (_disposed) return;
                    var accountError = VerifyAccountEvidence();
                    if (accountError != null) throw new InvalidOperationException(accountError);
                    // Both legs are durable dispatches before the one request that sends them.
                    var dispatchError = StartOwnedDispatch(stop.Intent, contract, actualQuantity: stop.Quantity, actualPrice: stopPrice) ??
                        StartOwnedDispatch(target.Intent, contract, actualQuantity: target.Quantity, actualPrice: targetPrice);
                    if (dispatchError != null) throw new InvalidOperationException(dispatchError);
                    placementStarted = true;
                    placement = _session.PlaceOcoAsync(_account, contract, stop.Direction == OrderDirections.Buy, stop.Quantity,
                        stopPrice, stop.Intent.ClientOrderId, targetPrice, target.Intent.ClientOrderId, automated, _cts.Token);
                }
                long id = await placement.ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) return;
                    // The reply names the primary (stop) leg; the target binds by its clOrdId when the venue reports it.
                    stop.Order.ExchangeOrderId = id.ToString(CultureInfo.InvariantCulture);
                    _byId[id] = stop.Order;
                    BindNative(stop.Order, id);
                }
                LastError = inOrder ? null : NativeSendTurns.UnsettledMessage;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    bool rejected = !placementStarted || TradovateTradingSession.IsDefinitePlacementRejection(ex);
                    // A venue refusal switches linking off for this session; the caller falls back to app-managed legs.
                    // Fault and error are set first: lock-free readers that see the legs end also see why.
                    if (rejected && placementStarted) _nativeFault = "Tradovate refused the exchange-side OCO: " + ex.Message;
                    LastError = stop.Order.ExchangeOrderId.Length > 0 ? null : rejected ? ex.Message :
                        "Tradovate OCO acceptance is unknown; awaiting client-tag reconciliation: " + ex.Message;
                    foreach (var leg in new[] { stop, target })
                        if (rejected && leg.Order.ExchangeOrderId.Length == 0)
                        {
                            leg.Order.State = OrderStates.Failed;
                            leg.Order.Status = OrderStatus.Rejected;
                            _cancelPending.Remove(leg.Order);
                            RecordRejected(leg.Intent, ex.Message);
                        }
                }
            }
            finally { turn.Release(); }

            Sync();
        });
    }


    private long IdOf(Order o) => long.TryParse(o.ExchangeOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private void BindNative(Order order, long id)
    {
        var native = new NativeOrderIdentity("tradovate:order", id.ToString(CultureInfo.InvariantCulture));
        if (!_intents.TryGetValue(order, out var intent))
        {
            if (string.IsNullOrEmpty(order.OwnerId))
            {
                order.ContextId = OrderContext.Descriptor.Id;
                order.NativeIdentity = native;
            }
            return;
        }
        var error = PersistNativeBinding(intent, native);
        order.NativeIdentity = native;
        if (error != null) _ownershipFault = "Tradovate accepted order; ownership persistence failed: " + error;
    }

    public void Cancel(Order order)
    {
        lock (_gate)
        {
            if (order.State != OrderStates.Active) return;
            _cancelPending.Add(order);
            _cancelRequested.TryAdd(order, ++_cancelRequestSequence);
            RejectQueuedReprice(order, "Cancellation superseded the queued price update.");
            if (!_replacements.ContainsKey(order)) _session.ResolveReplacement(order);
        }
        Sync();
    }

    private async Task CancelBoundAsync(Order order)
    {
        try
        {
            // The session takes this cancel's send turn before its first await, i.e. where Sync issued it.
            if (await _session.CancelAsync(_account.Id, IdOf(order), CancellationToken.None).ConfigureAwait(false) is { } unsettled)
                LastError = unsettled;
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
        lock (_gate) return _replacements.ContainsKey(order) || _queuedReprices.ContainsKey(order);
    }

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
            // Explicit replacement quantities are not a trailing-price queue.
            if (order.State != OrderStates.Active || _cancelPending.Contains(order) ||
                (!intent.IsPriceOnlyReplacement && (_replacements.ContainsKey(order) || _queuedReprices.ContainsKey(order))))
            {
                LastError = "Cannot replace an order while its previous outcome is unresolved or cancellation is pending.";
                RecordRejected(intent, "Cannot replace an order while its previous outcome is unresolved or cancellation is pending.");
                return order;
            }
            decimal remaining = intent.IsPriceOnlyReplacement ? order.Unfilled : intent.Request.Quantity;
            if (!NativeFuturesQuantity.TryConvertReplacement(order.Filled, remaining, out int totalQuantity))
            {
                LastError = NativeFuturesQuantity.Error;
                RecordRejected(intent, NativeFuturesQuantity.Error);
                return order;
            }
            var bindingError = BindOwnedOrder(order, intent);
            if (bindingError != null)
            {
                LastError = bindingError;
                return order;
            }
            if (intent.IsPriceOnlyReplacement &&
                (_replacements.ContainsKey(order) || _queuedReprices.ContainsKey(order) || !_session.SnapshotReady))
            {
                RejectQueuedReprice(order, "A later prepared price update superseded this unsent price update.");
                _queuedReprices[order] = intent;
                _session.TrackReplacement(order);
            }
            else StartReplacement(order, intent, totalQuantity);
        }
        Changed?.Invoke();
        return order;
    }

    private void RejectQueuedReprice(Order order, string reason)
    {
        if (_queuedReprices.Remove(order, out var intent)) RecordRejected(intent, reason);
    }

    // Called under the broker gate after the replacement identity has been bound to the handle.
    private void StartReplacement(Order order, PreparedOrderIntent intent, int? preparedTotal)
    {
        int totalQuantity;
        if (preparedTotal is { } total) totalQuantity = total;
        else if (!NativeFuturesQuantity.TryConvertReplacement(order.Filled, order.Unfilled, out totalQuantity))
        {
            LastError = NativeFuturesQuantity.Error;
            RecordRejected(intent, NativeFuturesQuantity.Error);
            _session.ResolveReplacement(order);
            return;
        }
        var replacement = new Replacement(intent, Instrument.RoundPrice(intent.Request.Price), totalQuantity);
        _replacements.Add(order, replacement);
        _session.TrackReplacement(order);
        var turn = _session.SendTurns(_account.Id).Take(); // issue order
        _ = Task.Run(async () =>
        {
            bool submissionStarted = false, gaveUp = false;
            long waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // Only a reducing amendment waits at most the reducing bound; an entry's waits for earlier replies.
                bool inOrder = await turn.WaitAsync(Reducing(intent), _cts.Token).ConfigureAwait(false);
                Task? submission = null;
                while (submission == null)
                {
                    await WaitForNativeReplayAsync().ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_disposed) return;
                        if (!_replacements.TryGetValue(order, out var current) || !ReferenceEquals(current, replacement))
                            return;
                        if (order.State != OrderStates.Active || _cancelPending.Contains(order))
                        {
                            _replacements.Remove(order);
                            RejectQueuedReprice(order, "The order became terminal or cancellation was requested before the queued price update.");
                            RecordRejected(intent, "The order became terminal or cancellation was requested before amendment dispatch.");
                            _session.ResolveReplacement(order);
                            return;
                        }
                        // Unknown placement/replay cannot authorize another native request.
                        long id = IdOf(order);
                        if (id != 0 && _session.SnapshotReady && _contract is { } contract)
                        {
                            var native = _session.OrdersOf(_account.Id, contract.Id).FirstOrDefault(s => s.Id == id);
                            if (native != null && !native.Done)
                            {
                                var accountError = VerifyAccountEvidence();
                                if (accountError != null) throw new InvalidOperationException(accountError);
                                decimal remaining = Math.Max(0, replacement.Total - native.Filled);
                                var dispatchError = StartOwnedDispatch(intent, contract.Name, actualQuantity: remaining, actualPrice: replacement.Price);
                                if (dispatchError != null) throw new InvalidOperationException(dispatchError);
                                _intents[order] = intent;
                                submissionStarted = true;
                                submission = _session.ModifyAsync(native, replacement.Total, replacement.Price, _cts.Token);
                            }
                        }
                    }
                    if (submission == null)
                    {
                        // Not sendable yet (unknown or unreported order). A reducing amendment, which already waited at
                        // most the reducing bound, lets later requests go meanwhile; an entry's keeps its turn, so nothing
                        // issued after it reaches the venue first. A cancel (Close position cancels too) ends this wait above.
                        if (Reducing(intent)) turn.Release();
                        if (System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted) >= _session.UnreportedAmendmentWait)
                        {
                            // Nothing was sent, so refusing is definite; the venue state is what the user must check.
                            gaveUp = true;
                            throw new TimeoutException("Not amended: Tradovate has not reported this order for 30 seconds, so the amendment was not sent. Check the order at Tradovate.");
                        }
                        await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                    }
                }
                await submission.ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) return;
                    BindNative(order, IdOf(order));
                }
                if (!inOrder) LastError = NativeSendTurns.UnsettledMessage;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    LastError = !submissionStarted || TradovateTradingSession.IsDefinitePlacementRejection(ex)
                        ? ex.Message : "Tradovate amendment outcome is unknown: " + ex.Message;
                    if ((!submissionStarted || TradovateTradingSession.IsDefinitePlacementRejection(ex)) &&
                        _replacements.TryGetValue(order, out var current) && ReferenceEquals(current, replacement))
                    {
                        var native = _contract is { } contract
                            ? _session.OrdersOf(_account.Id, contract.Id).FirstOrDefault(s => s.Id == IdOf(order))
                            : null;
                        if (native != null) Apply(native, order);
                        _replacements.Remove(order);
                        if (gaveUp) RejectQueuedReprice(order, ex.Message); // else it would start another unreported wait
                        if (!_queuedReprices.ContainsKey(order)) _session.ResolveReplacement(order);
                        RecordRejected(intent, ex.Message);
                    }
                }
            }
            finally { turn.Release(); }

            // Apply authoritative values even on rejection; optimistic caller fields
            // and successful command submission are never amendment acknowledgments.
            Sync();
        });
    }


    private async Task WaitForNativeReplayAsync()
    {
        while (!_session.SnapshotReady)
        {
            await Task.Delay(100, _cts.Token).ConfigureAwait(false);
        }
        _cts.Token.ThrowIfCancellationRequested();
        Sync(); // Permission uses the authoritative account position from this user-channel replay.
    }

    // Dispose shares this gate with every journal mutation, including late transport callbacks.
    private string? BindOwnedOrder(Order order, PreparedOrderIntent intent)
    {
        lock (_gate) return _disposed ? "Tradovate broker has been disposed." : OrderContext.BindOrder(order, intent);
    }

    private string? StartOwnedDispatch(PreparedOrderIntent intent, string instrument, string exchange = "", decimal? actualQuantity = null, decimal? actualPrice = null)
    {
        lock (_gate) return _disposed ? "Tradovate broker has been disposed." : OrderContext.StartDispatch(intent, instrument, exchange, actualQuantity, actualPrice);
    }

    private string? PersistNativeBinding(PreparedOrderIntent intent, NativeOrderIdentity native)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            var error = OrderContext.BindNative(intent, native, notify: false);
            if (error == null) _ownershipNotificationPending = true;
            return error;
        }
    }

    private void PublishOwnershipNotification()
    {
        lock (_gate)
        {
            if (!_ownershipNotificationPending) return;
            _ownershipNotificationPending = false;
        }
        // Persistence is admitted under the disposal gate, but consumers may query any sibling facade.
        OrderContext.NotifyOwnershipChanged();
    }

    private void PublishQuantityNotifications()
    {
        lock (_gate)
        {
            if (_publishingQuantities) return;
            _publishingQuantities = true;
        }
        try
        {
            while (true)
            {
                OwnedOrderQuantity observation;
                lock (_gate)
                {
                    if (_quantityNotifications.Count == 0)
                    {
                        _publishingQuantities = false;
                        return;
                    }
                    observation = _quantityNotifications.Dequeue();
                }
                // Captures admitted before disposal remain ordered, even while a consumer is held.
                OrderContext.NotifyQuantityChanged(observation);
            }
        }
        catch
        {
            lock (_gate) _publishingQuantities = false;
            throw;
        }
    }

    private void ObserveNativeQuantity(Order order, decimal previousFilled, OrderStates previousState, OrderStatus previousStatus)
    {
        lock (_gate)
        {
            if (_disposed || (order.Filled == previousFilled && order.State == previousState && order.Status == previousStatus)) return;
            try
            {
                if (OrderContext.CaptureQuantityObservation(order, Instrument.Symbol, Instrument.PointValue) is { } observation)
                    _quantityNotifications.Enqueue(observation);
            }
            catch (Exception ex) { _ownershipFault = "Tradovate native quantity projection is unresolved: " + ex.Message; }
        }
    }

    private void RecordRejected(PreparedOrderIntent intent, string reason)
    {
        lock (_gate)
            if (!_disposed) OrderContext.RecordRejected(intent, reason);
    }

    // Called under the broker gate only after the synchronized native account list is ready.
    private string? VerifyAccountEvidence()
    {
        if (!_session.Accounts.Any(a => a.Id == _account.Id))
            return "The account has not been reported by this Tradovate session.";
        if (_nativeAccountVerified) return null;
        var error = OrderContext.VerifyNativeAccount(_account.Id.ToString(CultureInfo.InvariantCulture));
        if (error == null) _nativeAccountVerified = true;
        return error;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _session.Changed -= Sync;
            _session.NativeFillChanged -= QueueExecution;
            OrderContext.OwnershipChanged -= QueueUnownedExecutions;
        }
        PublishOwnershipNotification();
        PublishQuantityNotifications();
        if (Executions is ExecutionLog log) log.SetProducerAvailability(_executionProducer, null);
    }
}
