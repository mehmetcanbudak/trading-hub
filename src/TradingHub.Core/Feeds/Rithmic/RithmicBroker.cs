using System.Text.Json;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Feeds.Rithmic;

/// <summary>
/// Una cuenta de Rithmic operando un instrumento (el contrato del mes: ES → ESZ6). El Chart Trader, el DOM,
/// el gráfico y el stop/objetivo operan contra esta cuenta igual que con la simulada o la de Binance.
/// </summary>
public sealed class RithmicBroker : ITradingAccount, ITrackedOrderReplacement, IDisposable
{
    private readonly InstrumentData _data;
    private readonly RithmicTradingSession _session;
    private readonly Func<CancellationToken, Task<(string Symbol, string Exchange)>> _contract;
    private readonly string _account;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private readonly Dictionary<string, Order> _byTag = new();
    private readonly Dictionary<Order, string> _tagOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Order> _byBasket = new();
    private readonly Dictionary<Order, PreparedOrderIntent> _intents = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Order> _cancelRequested = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Order> _cancelInFlight = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Order> _cancelAttempted = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Order, Replacement> _replacements = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Order, PreparedOrderIntent> _queuedReprices = new(ReferenceEqualityComparer.Instance);
    private sealed record Replacement(PreparedOrderIntent Intent, int TotalQuantity, decimal Price, long WorkingRevision, long RejectionRevision);
    private (string Symbol, string Exchange)? _resolved;
    private RithmicAccountInfo? _verifiedNativeAccount;
    private volatile List<Order> _active = new();

    public RithmicBroker(InstrumentData data, RithmicTradingSession session, string accountId, string name,
        Func<CancellationToken, Task<(string Symbol, string Exchange)>> contract, OrderContext? orderContext = null)
    {
        _data = data;
        _session = session;
        _account = accountId;
        _contract = contract;
        Name = name;
        OrderContext = orderContext ?? new OrderContext(new AccountContextDescriptor("", "Rithmic", "futures", "unconfigured", "", ""));
        Position = new Position { Security = new Security { Code = data.Instrument.Symbol, SecurityId = data.Instrument.Key } };
        _session.Changed += Sync;
        _ = ContractAsync();
    }

    public string Name { get; }

    /// <summary>Las cuentas de práctica (Paper Trading, Test) no son reales; el resto sí.</summary>
    public bool IsReal => !_session.IsPractice;

    public Instrument Instrument => _data.Instrument;
    public decimal LastPrice => _data.LastPrice;
    // Session basket/cumulative fills are not yet an authoritative, fee-complete execution-report source.
    public OrderContext OrderContext { get; }
    public IExecutionSource Executions => OrderContext.Executions;
    public Position Position { get; }
    public IEnumerable<Order> ActiveOrders
    {
        get
        {
            lock (_gate)
                foreach (var order in _active)
                    if (string.IsNullOrEmpty(order.OwnerId) && order.NativeIdentity != null)
                        order.ContextId = OrderContext.Descriptor.Id;
            return _active;
        }
    }
    private string? _lastError;
    private string? _ownershipFault;
    public string? LastError
    {
        get
        {
            string? fault = _ownershipFault ?? OrderContext.LastError;
            return fault == null ? _lastError : _lastError == null ? fault : _lastError + "; " + fault;
        }
        private set => _lastError = value;
    }
    public bool PositionReady => _resolved is { } c && _session.PositionReadyFor(_account, c.Symbol, c.Exchange);
    public bool OrdersReady
    {
        get
        {
            if (_resolved == null || !_session.OrdersReadyFor(_account)) return false;
            lock (_gate)
            {
                foreach (var order in _byTag.Values)
                    if (order.State == OrderStates.Active && order.ExchangeOrderId.Length == 0) return false;
            }
            return true;
        }
    }
    public event Action? Changed;

    public bool IsReplacementPending(Order order)
    {
        lock (_gate) return _replacements.ContainsKey(order) || _queuedReprices.ContainsKey(order);
    }

    /// <summary>Contrato donde opera (ESZ6, CME); null hasta que Rithmic lo confirma.</summary>
    public (string Symbol, string Exchange)? Contract => _resolved;

    private async Task<(string Symbol, string Exchange)> ContractAsync()
    {
        if (_resolved is { } r) return r;
        try
        {
            var c = await _contract(CancellationToken.None).ConfigureAwait(false);
            _resolved = c;
            Sync();
            return c;
        }
        catch (Exception ex)
        {
            LastError = "Could not determine active contract: " + ex.Message;
            Changed?.Invoke();
            throw;
        }
    }

    private static void Apply(RithmicOrderState s, Order o)
    {
        o.Direction = s.IsBuy ? OrderDirections.Buy : OrderDirections.Sell;
        o.Type = s.PriceType == 2 ? OrderTypes.Market : s.PriceType is 3 or 4 ? OrderTypes.Stop : OrderTypes.Limit;
        if (s.Price > 0) o.Price = s.Price;
        o.TriggerPrice = s.PriceType is 3 or 4 ? s.Trigger : o.Price;
        decimal filled = Math.Max(o.Filled, s.Filled);
        if (s.Quantity > 0) o.QuantityToFill = s.Quantity;
        o.Unfilled = s.Done && !s.Cancelled && !s.Rejected ? 0 : Math.Max(0, o.QuantityToFill - filled);
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

    /// <summary>Junta lo que cuenta Rithmic (órdenes y posición) con las órdenes de Trading Hub (mismos objetos).</summary>
    private void Sync()
    {
        if (_resolved is not { } c) return;
        var active = new List<Order>();
        List<Order>? cancels = null;
        lock (_gate)
        {
            if (_disposed) return;
            // Concurrent order/PnL callbacks must capture, apply and publish under the same gate.
            var states = _session.OrdersFor(_account, c.Symbol, c.Exchange);
            var seen = new HashSet<Order>(ReferenceEqualityComparer.Instance);
            foreach (var s in states)
            {
                Order? o = null;
                if (s.UserTag.Length > 0 && _byTag.TryGetValue(s.UserTag, out var t)) o = t;
                else if (s.BasketId.Length > 0 && _byBasket.TryGetValue(s.BasketId, out var b)) o = b;
                if (o == null)
                {
                    if (s.Done) continue; // ya terminada antes de abrir Trading Hub
                    o = new Order { Security = Position.Security, ExchangeOrderId = s.BasketId, Comment = "Rithmic" };
                }
                if (string.IsNullOrEmpty(o.OwnerId) && s.UserTag.Length > 0) o.ClientOrderId = s.UserTag;

                if (s.BasketId.Length > 0)
                {
                    o.ExchangeOrderId = s.BasketId;
                    _byBasket[s.BasketId] = o;
                    BindNative(o, s.BasketId);
                }

                decimal previousFilled = o.Filled;
                var previousState = o.State;
                var previousStatus = o.Status;
                Apply(s, o);
                if (_replacements.TryGetValue(o, out var replacement))
                {
                    var native = _session.ReplacementState(s);
                    if (s.Done || native.RejectionRevision > replacement.RejectionRevision)
                    {
                        if (native.RejectionRevision > replacement.RejectionRevision)
                        {
                            _session.RestoreRejectedReplacement(s);
                            LastError = "Rithmic rejected amendment: " + native.Reason;
                            RecordRejected(replacement.Intent, "Rithmic rejected amendment: " + native.Reason);
                        }
                        Apply(s, o);
                        _replacements.Remove(o);
                    }
                    else if (native.WorkingRevision > replacement.WorkingRevision &&
                        native.Quantity == replacement.TotalQuantity && native.Price == replacement.Price)
                        _replacements.Remove(o);
                }
                ObserveNativeQuantity(o, previousFilled, previousState, previousStatus);
                if (o.State != OrderStates.Active) RejectQueuedReprice(o, "The order became terminal before the queued price update.");
                if (s.Rejected && _intents.TryGetValue(o, out var rejected))
                    RecordRejected(rejected, s.Reason);
                if (s.Rejected && s.Reason.Length > 0) LastError = "Rithmic rejected order: " + s.Reason;
                if (o.State == OrderStates.Active && seen.Add(o)) active.Add(o);
            }

            // las nuestras que todavía no aparecieron en Rithmic (recién mandadas)
            foreach (var o in _byTag.Values)
                if (o.State == OrderStates.Active && o.ExchangeOrderId.Length == 0 && seen.Add(o)) active.Add(o);
            var p = _session.PositionFor(_account, c.Symbol, c.Exchange);
            Position.Volume = p?.Net ?? 0;
            Position.AveragePrice = p?.AvgPrice ?? 0;
            Position.RealizedPnL = p?.ClosedPnl ?? 0;
            decimal open = p?.OpenPnl ?? 0;
            if (open == 0 && Position.Volume != 0 && LastPrice > 0 && Position.AveragePrice > 0)
                open = (LastPrice - Position.AveragePrice) * Position.Volume * (Instrument.PointValue > 0 ? Instrument.PointValue : 1);
            Position.UnrealizedPnL = open;
            _active = active;
            if (!_session.IsConnected) _cancelAttempted.Clear();
            _cancelRequested.RemoveWhere(static order => order.State != OrderStates.Active);
            _cancelAttempted.RemoveWhere(static order => order.State != OrderStates.Active);
            foreach (var order in _cancelRequested)
            {
                if (_session.IsConnected && order.ExchangeOrderId.Length > 0 &&
                    !_cancelInFlight.Contains(order) && _cancelAttempted.Add(order))
                {
                    _cancelInFlight.Add(order);
                    (cancels ??= new()).Add(order);
                }
            }
            foreach (var (order, intent) in _queuedReprices.ToArray())
            {
                if (order.State != OrderStates.Active || _cancelRequested.Contains(order))
                    RejectQueuedReprice(order, "The order became terminal or cancellation was requested before the queued price update.");
                else if (!_replacements.ContainsKey(order) && _session.IsConnected && _session.OrdersReadyFor(_account))
                {
                    _queuedReprices.Remove(order);
                    StartReplacement(order, intent);
                }
            }
        }
        Changed?.Invoke();
        if (cancels != null)
            foreach (var order in cancels) _ = CancelBoundAsync(order);
    }

    public Order Submit(PreparedOrderIntent intent)
    {
        lock (_gate)
            if (_byTag.TryGetValue(intent.ClientOrderId, out var existing)) return existing;
        var request = intent.Request;
        var dir = request.Direction;
        var type = request.Type;
        var price = request.Price;
        int q = (int)Math.Round(request.Quantity);
        var order = new Order { Security = Position.Security, Direction = dir, Type = type, QuantityToFill = q, Unfilled = q, Price = price, TriggerPrice = price, Comment = request.Comment, State = OrderStates.Active, Status = OrderStatus.Placed, ReduceOnly = request.ReduceOnly };
        var bindingError = request.Instrument == Instrument.Key
            ? BindOwnedOrder(order, intent) : "The prepared instrument does not match this broker.";
        if (bindingError != null || q <= 0)
        {
            order.State = OrderStates.Failed;
            order.Status = OrderStatus.Rejected;
            LastError = bindingError ?? "Futures quantity must be a whole number of contracts (1, 2, 3…).";
            if (bindingError == null) RecordRejected(intent, "Futures quantity must be a whole number of contracts (1, 2, 3…).");
            Changed?.Invoke();
            return order;
        }

        string tag = intent.ClientOrderId;
        lock (_gate)
        {
            if (_byTag.TryGetValue(tag, out var existing)) return existing;
            _byTag[tag] = order;
            _tagOf[order] = tag;
            _intents[order] = intent;
        }

        Sync();
        _ = Task.Run(async () =>
        {
            bool submissionStarted = false;
            try
            {
                var (sym, exch) = await ContractAsync().ConfigureAwait(false);
                await WaitForNativeReplayAsync(sym, exch).ConfigureAwait(false);
                int priceType = type == OrderTypes.Market ? 2 : type == OrderTypes.Stop ? 4 : 1;
                decimal px = price > 0 ? Instrument.RoundPrice(price) : 0;
                Task<string> placement;
                lock (_gate)
                {
                    if (_disposed) return;
                    var accountError = VerifyAccountEvidence();
                    if (accountError != null) throw new InvalidOperationException(accountError);
                    var dispatchError = StartOwnedDispatch(intent, sym, exch, q, px);
                    if (dispatchError != null) throw new InvalidOperationException(dispatchError);
                    submissionStarted = true;
                    placement = _session.PlaceAsync(_account, sym, exch, dir == OrderDirections.Buy, priceType, q, px, px, tag, _cts.Token);
                }
                var basket = await placement.ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) return;
                    if (basket.Length > 0)
                    {
                        order.ExchangeOrderId = basket;
                        _byBasket[basket] = order;
                        BindNative(order, basket);
                    }
                }

                LastError = null;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    if (!submissionStarted || ex is RithmicTradingSession.RithmicRejectedException)
                    {
                        order.State = OrderStates.Failed;
                        order.Status = OrderStatus.Rejected;
                        RecordRejected(intent, ex.Message);
                    }
                    // A lost response is not a rejection. Keep the original tag and handle for exact replay.
                    LastError = submissionStarted && ex is not RithmicTradingSession.RithmicRejectedException
                        ? "Rithmic submission outcome is unknown: " + ex.Message : ex.Message;
                }
            }

            Sync();
        });
        return order;
    }


    /// <summary>Rithmic's state of an order, found by basket id or by the tag this broker gave it (restored orders: basket id only).</summary>
    private RithmicOrderState? StateOf(Order order)
    {
        if (_resolved is not { } c) return null;
        string? tag;
        lock (_gate) _tagOf.TryGetValue(order, out tag);
        return _session.OrdersFor(_account, c.Symbol, c.Exchange)
            .FirstOrDefault(s => (order.ExchangeOrderId.Length > 0 && s.BasketId == order.ExchangeOrderId) || (tag != null && s.UserTag == tag));
    }

    private void BindNative(Order order, string basket)
    {
        var native = new NativeOrderIdentity("rithmic:basket", basket);
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
        if (error != null) _ownershipFault = "Rithmic accepted order; ownership persistence failed: " + error;
    }

    public void Cancel(Order order)
    {
        lock (_gate)
        {
            if (order.State != OrderStates.Active) return;
            _cancelRequested.Add(order);
            RejectQueuedReprice(order, "Cancellation superseded the queued price update.");
            if (!_cancelInFlight.Contains(order)) _cancelAttempted.Remove(order);
        }
        Sync();
    }

    private async Task CancelBoundAsync(Order order)
    {
        try
        {
            await _session.CancelAsync(_account, order.ExchangeOrderId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            // Keep intent until terminal confirmation. Retry only on an explicit cancel or a new connection,
            // never on every order/PnL callback when a cancel response is missing or rejected.
            lock (_gate) _cancelInFlight.Remove(order);
            Sync();
        }
    }

    public void CancelAt(decimal price)
    {
        foreach (var o in _active.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price).ToList()) Cancel(o);
    }

    /// <summary>Cancela las órdenes de este instrumento (no las de otros contratos de la cuenta).</summary>
    public void CancelAll()
    {
        foreach (var o in _active.ToList()) Cancel(o);
    }

    /// <summary>Rithmic modifica precio y cantidad sobre la misma orden.</summary>
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
            if (_cancelRequested.Contains(order) || order.State != OrderStates.Active ||
                (!intent.IsPriceOnlyReplacement && (_replacements.ContainsKey(order) || _queuedReprices.ContainsKey(order))))
            {
                LastError = "Cannot replace an order while its previous outcome is unresolved or cancellation is pending.";
                RecordRejected(intent, "Cannot replace an order while its previous outcome is unresolved or cancellation is pending.");
                return order;
            }
            var bindingError = BindOwnedOrder(order, intent);
            if (bindingError != null)
            {
                LastError = bindingError;
                return order;
            }
            if (intent.IsPriceOnlyReplacement && (_replacements.ContainsKey(order) || _queuedReprices.ContainsKey(order)))
            {
                RejectQueuedReprice(order, "A later prepared price update superseded this unsent price update.");
                _queuedReprices[order] = intent;
            }
            else StartReplacement(order, intent);
        }
        Changed?.Invoke();
        return order;
    }

    // A queued price update retains its prepared identity but takes sizing only from authoritative replay.
    private void StartReplacement(Order order, PreparedOrderIntent intent)
    {
        var state = StateOf(order);
        if (order.ExchangeOrderId.Length == 0 || state == null)
        {
            LastError = "The order is not available in Rithmic yet; no replacement was sent.";
            RecordRejected(intent, "The order is not available in Rithmic yet; no replacement was sent.");
            return;
        }
        var native = _session.ReplacementState(state);
        decimal remaining = intent.IsPriceOnlyReplacement ? order.Unfilled : intent.Request.Quantity;
        var replacement = new Replacement(intent, (int)Math.Round(state.Filled + remaining),
            Instrument.RoundPrice(intent.Request.Price), native.WorkingRevision, native.RejectionRevision);
        _replacements.Add(order, replacement);
        _ = ReplaceAsync(order, state, replacement);
    }

    private void RejectQueuedReprice(Order order, string reason)
    {
        if (_queuedReprices.Remove(order, out var intent)) RecordRejected(intent, reason);
    }

    private async Task ReplaceAsync(Order order, RithmicOrderState state, Replacement replacement)
    {
        bool submissionStarted = false;
        try
        {
            var (symbol, exchange) = await ContractAsync().ConfigureAwait(false);
            await WaitForNativeReplayAsync(symbol, exchange).ConfigureAwait(false);
            Task amendment;
            lock (_gate)
            {
                if (_disposed) return;
                var accountError = VerifyAccountEvidence();
                if (accountError != null) throw new InvalidOperationException(accountError);
                decimal remaining = Math.Max(0, replacement.TotalQuantity - state.Filled);
                var dispatchError = StartOwnedDispatch(replacement.Intent, symbol, exchange, remaining, replacement.Price);
                if (dispatchError != null) throw new InvalidOperationException(dispatchError);
                _intents[order] = replacement.Intent;
                submissionStarted = true;
                amendment = _session.ModifyAsync(_account, state, replacement.TotalQuantity, replacement.Price, _cts.Token);
            }
            await amendment.ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) return;
                BindNative(order, state.BasketId);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_replacements.TryGetValue(order, out var pending) && ReferenceEquals(pending, replacement))
                {
                    if (!submissionStarted || ex is RithmicTradingSession.RithmicRejectedException)
                    {
                        _session.RestoreRejectedReplacement(state);
                        decimal previousFilled = order.Filled;
                        var previousState = order.State;
                        var previousStatus = order.Status;
                        Apply(state, order);
                        ObserveNativeQuantity(order, previousFilled, previousState, previousStatus);
                        _replacements.Remove(order);
                        LastError = ex.Message;
                        RecordRejected(replacement.Intent, ex.Message);
                    }
                    else LastError = "Rithmic amendment outcome is unknown: " + ex.Message;
                }
            }
        }
        // A successful command receipt is not a working-order acknowledgment. Sync restores native values;
        // only an exact later working report, a terminal state, or a definite rejection clears pending intent.
        Sync();
    }


    private async Task WaitForNativeReplayAsync(string symbol, string exchange)
    {
        while (!_session.PositionReadyFor(_account, symbol, exchange) || !_session.OrdersReadyFor(_account))
        {
            await Task.Delay(100, _cts.Token).ConfigureAwait(false);
        }
        _cts.Token.ThrowIfCancellationRequested();
        Sync(); // Permission must see the replayed position, not the pre-replay default.
    }

    // Dispose shares this gate with every journal mutation, including late transport callbacks.
    private string? BindOwnedOrder(Order order, PreparedOrderIntent intent)
    {
        lock (_gate) return _disposed ? "Rithmic broker has been disposed." : OrderContext.BindOrder(order, intent);
    }

    private string? StartOwnedDispatch(PreparedOrderIntent intent, string instrument, string exchange = "", decimal? actualQuantity = null, decimal? actualPrice = null)
    {
        lock (_gate) return _disposed ? "Rithmic broker has been disposed." : OrderContext.StartDispatch(intent, instrument, exchange, actualQuantity, actualPrice);
    }

    private string? PersistNativeBinding(PreparedOrderIntent intent, NativeOrderIdentity native)
    {
        lock (_gate) return _disposed ? null : OrderContext.BindNative(intent, native);
    }

    private void ObserveNativeQuantity(Order order, decimal previousFilled, OrderStates previousState, OrderStatus previousStatus)
    {
        lock (_gate)
        {
            if (_disposed || (order.Filled == previousFilled && order.State == previousState && order.Status == previousStatus)) return;
            try { OrderContext.ObserveQuantity(order, Instrument.Symbol, Instrument.PointValue); }
            catch (Exception ex) { _ownershipFault = "Rithmic native quantity projection is unresolved: " + ex.Message; }
        }
    }

    private void RecordRejected(PreparedOrderIntent intent, string reason)
    {
        lock (_gate)
            if (!_disposed) OrderContext.RecordRejected(intent, reason);
    }

    // Called under the broker gate only after native replay has established the route and account.
    private string? VerifyAccountEvidence()
    {
        var account = _session.Accounts.FirstOrDefault(a => a.AccountId == _account);
        if (account == null) return "The account has not been reported by this Rithmic session.";
        if (string.IsNullOrEmpty(account.FcmId) || string.IsNullOrEmpty(account.IbId) || string.IsNullOrEmpty(account.AccountId))
            return null; // Incomplete native identity stays unverified; never synthesize the missing fields.
        if (_verifiedNativeAccount is { } previous && previous.FcmId == account.FcmId &&
            previous.IbId == account.IbId && previous.AccountId == account.AccountId) return null;
        var error = OrderContext.VerifyNativeAccount(JsonSerializer.Serialize(new[] { account.FcmId, account.IbId, account.AccountId }));
        if (error == null) _verifiedNativeAccount = account;
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
        }
    }
}

