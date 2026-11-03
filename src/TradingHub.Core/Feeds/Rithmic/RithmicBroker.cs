using System.Globalization;
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
    private readonly Dictionary<string, Order> _byTag = new();
    private readonly Dictionary<Order, string> _tagOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Order> _byBasket = new();
    private readonly HashSet<Order> _cancelRequested = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Order> _cancelInFlight = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Order> _cancelAttempted = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Order, Replacement> _replacements = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Order, decimal> _queuedModifyPrices = new(ReferenceEqualityComparer.Instance);
    private sealed record Replacement(int TotalQuantity, decimal Price, long WorkingRevision, long RejectionRevision);
    private (string Symbol, string Exchange)? _resolved;
    private volatile List<Order> _active = new();

    /// <summary>
    /// The part of every order tag that identifies this process (<c>th-{SessionTag}-{order id}</c>). Order ids restart at 1
    /// in every process, so without it a later process could reuse an earlier one's same-day tags and take that process's
    /// orders for its own.
    /// </summary>
    public static string SessionTag { get; } = SessionTagFor(ProcessStartUtc());

    /// <summary>Session part of the tags for a process started at <paramref name="startUtc"/>: its Unix milliseconds in base 36 (8 ASCII characters).</summary>
    public static string SessionTagFor(DateTime startUtc)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var utc = startUtc.Kind == DateTimeKind.Local ? startUtc.ToUniversalTime() : DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        long ms = Math.Max(0, new DateTimeOffset(utc).ToUnixTimeMilliseconds());
        Span<char> text = stackalloc char[13];
        int i = text.Length;
        do
        {
            text[--i] = digits[(int)(ms % 36)];
            ms /= 36;
        }
        while (ms > 0);
        return new string(text[i..]);
    }

    private static DateTime ProcessStartUtc()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            return DateTime.UtcNow; // start time unavailable: the first use in this process is just as distinct
        }
    }

    public RithmicBroker(InstrumentData data, RithmicTradingSession session, string accountId, string name,
        Func<CancellationToken, Task<(string Symbol, string Exchange)>> contract)
    {
        _data = data;
        _session = session;
        _account = accountId;
        _contract = contract;
        Name = name;
        Position = new Position { Security = new Security { Code = data.Instrument.Symbol, SecurityId = data.Instrument.Key } };
        _session.Changed += Sync;
        _ = ContractAsync();
    }

    public string Name { get; }

    /// <summary>Las cuentas de práctica (Paper Trading, Test) no son reales; el resto sí.</summary>
    public bool IsReal => !_session.IsPractice;

    public Instrument Instrument => _data.Instrument;
    public decimal LastPrice => _data.LastPrice;
    public Position Position { get; }
    public IEnumerable<Order> ActiveOrders => _active;
    public string? LastError { get; private set; }
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
        lock (_gate) return _replacements.ContainsKey(order) || _queuedModifyPrices.ContainsKey(order);
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
                    o = new Order { ExchangeOrderId = s.BasketId, Comment = "Rithmic" };
                }

                if (s.BasketId.Length > 0)
                {
                    o.ExchangeOrderId = s.BasketId;
                    _byBasket[s.BasketId] = o;
                }

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
                        }
                        Apply(s, o);
                        _replacements.Remove(o);
                    }
                    else if (native.WorkingRevision > replacement.WorkingRevision &&
                        native.Quantity == replacement.TotalQuantity && native.Price == replacement.Price)
                        _replacements.Remove(o);
                }
                if (o.State != OrderStates.Active) _queuedModifyPrices.Remove(o);
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
            // A queued trailing price is not a saved sizing request. Reuse Replace only after the
            // previous outcome is authoritative and replay is ready, with the remaining quantity now.
            foreach (var (order, price) in _queuedModifyPrices)
            {
                if (order.State != OrderStates.Active || _cancelRequested.Contains(order))
                    _queuedModifyPrices.Remove(order);
                else if (!_replacements.ContainsKey(order) && _session.IsConnected && _session.OrdersReadyFor(_account))
                {
                    _queuedModifyPrices.Remove(order);
                    Replace(order, price, order.Unfilled);
                }
            }
        }
        Changed?.Invoke();
        if (cancels != null)
            foreach (var order in cancels) _ = CancelBoundAsync(order);
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

        string tag = "th-" + SessionTag + "-" + order.Id.ToString(CultureInfo.InvariantCulture);
        lock (_gate)
        {
            _byTag[tag] = order;
            _tagOf[order] = tag;
        }

        Sync();
        _ = Task.Run(async () =>
        {
            bool submissionStarted = false;
            try
            {
                var (sym, exch) = await ContractAsync().ConfigureAwait(false);
                int priceType = type == OrderTypes.Market ? 2 : type == OrderTypes.Stop ? 4 : 1;
                decimal px = price > 0 ? Instrument.RoundPrice(price) : 0;
                submissionStarted = true;
                var basket = await _session.PlaceAsync(_account, sym, exch, dir == OrderDirections.Buy, priceType, q, px, px, tag, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    if (basket.Length > 0)
                    {
                        order.ExchangeOrderId = basket;
                        _byBasket[basket] = order;
                    }
                }

                LastError = null;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (!submissionStarted || ex is RithmicTradingSession.RithmicRejectedException)
                    {
                        order.State = OrderStates.Failed;
                        order.Status = OrderStatus.Rejected;
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

    public Order Market(OrderDirections dir, decimal qty, string comment = "") => Send(dir, OrderTypes.Market, qty, 0, comment);
    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => Send(dir, OrderTypes.Limit, qty, price, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => Send(dir, OrderTypes.Stop, qty, trigger, comment);

    /// <summary>Stop loss / objetivo: Trading Hub los maneja en OCO (Rithmic no tiene "solo reducir" en una orden simple).</summary>
    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") =>
        Send(dir, type is OrderTypes.Market or OrderTypes.Stop ? type : OrderTypes.Limit, qty, price, comment, reduceOnly: true);

    /// <summary>Rithmic's state of an order, found by basket id or by the tag this broker gave it (restored orders: basket id only).</summary>
    private RithmicOrderState? StateOf(Order order)
    {
        if (_resolved is not { } c) return null;
        string? tag;
        lock (_gate) _tagOf.TryGetValue(order, out tag);
        return _session.OrdersFor(_account, c.Symbol, c.Exchange)
            .FirstOrDefault(s => (order.ExchangeOrderId.Length > 0 && s.BasketId == order.ExchangeOrderId) || (tag != null && s.UserTag == tag));
    }

    public void Cancel(Order order)
    {
        lock (_gate)
        {
            if (order.State != OrderStates.Active) return;
            _cancelRequested.Add(order);
            _queuedModifyPrices.Remove(order);
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

    public void Modify(Order order, decimal newPrice)
    {
        lock (_gate)
        {
            if (order.State != OrderStates.Active || _cancelRequested.Contains(order)) return;
            if (_replacements.ContainsKey(order) || _queuedModifyPrices.ContainsKey(order))
                _queuedModifyPrices[order] = newPrice;
            else
                Replace(order, newPrice, order.Unfilled);
        }
    }

    /// <summary>Rithmic modifica precio y cantidad sobre la misma orden.</summary>
    public Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        RithmicOrderState? state;
        Replacement? replacement = null;
        lock (_gate)
        {
            if (_replacements.ContainsKey(order) || _queuedModifyPrices.ContainsKey(order) ||
                _cancelRequested.Contains(order) || order.State != OrderStates.Active) return order;
            state = StateOf(order);
            if (order.ExchangeOrderId.Length == 0 || state == null)
            {
                LastError = "The order is not available in Rithmic yet; no replacement was sent.";
            }
            else
            {
                var native = _session.ReplacementState(state);
                replacement = new Replacement((int)Math.Round(state.Filled + newQuantity),
                    Instrument.RoundPrice(newPrice), native.WorkingRevision, native.RejectionRevision);
                _replacements.Add(order, replacement);
            }
        }
        if (state != null && replacement != null) _ = ReplaceAsync(order, state, replacement);
        else Changed?.Invoke();
        return order;
    }

    private async Task ReplaceAsync(Order order, RithmicOrderState state, Replacement replacement)
    {
        try
        {
            await _session.ModifyAsync(_account, state, replacement.TotalQuantity, replacement.Price, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_replacements.TryGetValue(order, out var pending) && ReferenceEquals(pending, replacement))
                {
                    if (ex is RithmicTradingSession.RithmicRejectedException)
                    {
                        _session.RestoreRejectedReplacement(state);
                        Apply(state, order);
                        _replacements.Remove(order);
                        LastError = ex.Message;
                    }
                    else LastError = "Rithmic amendment outcome is unknown: " + ex.Message;
                }
            }
        }
        // A successful command receipt is not a working-order acknowledgment. Sync restores native values;
        // only an exact later working report, a terminal state, or a definite rejection clears pending intent.
        Sync();
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
