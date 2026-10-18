using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Feeds.Rithmic;

/// <summary>
/// Una cuenta de Rithmic operando un instrumento (el contrato del mes: ES → ESZ6). El Chart Trader, el DOM,
/// el gráfico y el stop/objetivo operan contra esta cuenta igual que con la simulada o la de Binance.
/// </summary>
public sealed class RithmicBroker : ITradingAccount, IDisposable
{
    private readonly InstrumentData _data;
    private readonly RithmicTradingSession _session;
    private readonly Func<CancellationToken, Task<(string Symbol, string Exchange)>> _contract;
    private readonly string _account;
    private readonly object _gate = new();
    private readonly Dictionary<string, Order> _byTag = new();
    private readonly Dictionary<string, Order> _byBasket = new();
    private (string Symbol, string Exchange)? _resolved;
    private List<Order> _active = new();

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
    public event Action? Changed;

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
        if (s.Quantity > 0) o.QuantityToFill = s.Quantity;
        o.Unfilled = s.Done ? 0 : s.Unfilled;
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
        var states = _session.OrdersFor(_account, c.Symbol, c.Exchange);
        var active = new List<Order>();
        lock (_gate)
        {
            var seen = new HashSet<Order>(ReferenceEqualityComparer.Instance);
            foreach (var s in states)
            {
                Order? o = null;
                if (s.BasketId.Length > 0 && _byBasket.TryGetValue(s.BasketId, out var b)) o = b;
                else if (s.UserTag.Length > 0 && _byTag.TryGetValue(s.UserTag, out var t)) o = t;
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
                if (s.Rejected && s.Reason.Length > 0) LastError = "Rithmic rejected order: " + s.Reason;
                if (o.State == OrderStates.Active && seen.Add(o)) active.Add(o);
            }

            // las nuestras que todavía no aparecieron en Rithmic (recién mandadas)
            foreach (var o in _byTag.Values)
                if (o.State == OrderStates.Active && o.ExchangeOrderId.Length == 0 && seen.Add(o)) active.Add(o);
        }

        _active = active;
        var p = _session.PositionFor(_account, c.Symbol, c.Exchange);
        Position.Volume = p?.Net ?? 0;
        Position.AveragePrice = p?.AvgPrice ?? 0;
        Position.RealizedPnL = p?.ClosedPnl ?? 0;
        decimal open = p?.OpenPnl ?? 0;
        if (open == 0 && Position.Volume != 0 && LastPrice > 0 && Position.AveragePrice > 0)
            open = (LastPrice - Position.AveragePrice) * Position.Volume * (Instrument.PointValue > 0 ? Instrument.PointValue : 1);
        Position.UnrealizedPnL = open;
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

        string tag = "th-" + order.Id;
        lock (_gate) _byTag[tag] = order;
        Sync();
        _ = Task.Run(async () =>
        {
            try
            {
                var (sym, exch) = await ContractAsync().ConfigureAwait(false);
                int priceType = type == OrderTypes.Market ? 2 : type == OrderTypes.Stop ? 4 : 1;
                decimal px = price > 0 ? Instrument.RoundPrice(price) : 0;
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

    /// <summary>Stop loss / objetivo: Trading Hub los maneja en OCO (Rithmic no tiene "solo reducir" en una orden simple).</summary>
    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") =>
        Send(dir, type == OrderTypes.Stop ? OrderTypes.Stop : OrderTypes.Limit, qty, price, comment, reduceOnly: true);

    private RithmicOrderState? StateOf(Order order) =>
        _resolved is { } c
            ? _session.OrdersFor(_account, c.Symbol, c.Exchange).FirstOrDefault(s => (order.ExchangeOrderId.Length > 0 && s.BasketId == order.ExchangeOrderId) || s.UserTag == "th-" + order.Id)
            : null;

    public void Cancel(Order order)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // si todavía no tiene número en Rithmic, se espera un momento a que llegue
                for (int i = 0; i < 20 && order.ExchangeOrderId.Length == 0 && order.State == OrderStates.Active; i++) await Task.Delay(100).ConfigureAwait(false);
                if (order.ExchangeOrderId.Length == 0) return;
                await _session.CancelAsync(_account, order.ExchangeOrderId, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>Cancela las órdenes de este instrumento (no las de otros contratos de la cuenta).</summary>
    public void CancelAll()
    {
        foreach (var o in _active.ToList()) Cancel(o);
    }

    public void Modify(Order order, decimal newPrice) => Replace(order, newPrice, order.Unfilled);

    /// <summary>Rithmic modifica precio y cantidad sobre la misma orden.</summary>
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
                for (int i = 0; i < 20 && order.ExchangeOrderId.Length == 0 && order.State == OrderStates.Active; i++) await Task.Delay(100).ConfigureAwait(false);
                var s = StateOf(order) ?? throw new InvalidOperationException("The order is not available in Rithmic yet");
                await _session.ModifyAsync(_account, s, total, px, CancellationToken.None).ConfigureAwait(false);
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
