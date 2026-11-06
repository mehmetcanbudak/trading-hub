using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Feeds.Bybit;

/// <summary>A symbol view over one immutable UTA credential context. Native reducing exits are set before dispatch.</summary>
public sealed class BybitBroker : ITradingAccount, ITrackedOrderReplacement, IStableOrderIdentity, IAccountExecutionSource, ICoordinatedPositionOperations, IDisposable
{
    private readonly InstrumentData _data;
    private readonly Position _position;
    private static readonly AccountExecutionSnapshot UnknownExecutions = new(Array.Empty<AccountExecution>(), false);
    private readonly bool _ownsSession;
    private string? _localError;
    private bool _disposed;
    private volatile bool _retired;
    public BybitBroker(InstrumentData data, BybitTradingSession session, string name, bool ownsSession = false)
    {
        if (data.Instrument.FeedId != BybitFeed.LinearId && data.Instrument.FeedId != "bybit-linear-testnet")
            throw new ArgumentException("Bybit execution requires its own venue data, not a composite or another venue's price.", nameof(data));
        bool testnetData = data.Instrument.FeedId == "bybit-linear-testnet";
        if (testnetData != (session.Settings.Environment == BybitEnvironment.Testnet))
            throw new ArgumentException("Bybit testnet accounts require testnet market data; demo and real accounts require main-market data.", nameof(data));
        _data = data;
        Session = session;
        Name = name;
        _ownsSession = ownsSession;
        _position = new Position { Security = new Security { Code = Instrument.Symbol, SecurityId = Instrument.Key, TickSize = Instrument.TickSize } };
        session.Changed += OnChanged;
        session.RegisterInstrument(data.Instrument);
        OnChanged();
    }
    public BybitTradingSession Session { get; }
    public string Name { get; }
    public bool IsReal => Session.IsReal;
    public Instrument Instrument => _data.Instrument;
    public decimal LastPrice => _data.LastPrice;
    public Position Position
    {
        get
        {
            RefreshPosition();
            return _position;
        }
    }
    public OrderContext OrderContext => Session.OrderContext;
    public IExecutionSource Executions => Session.ExecutionSource;
    public bool PositionReady => Session.PositionReady;
    public bool OrdersReady => Session.OrdersReady;
    public long ExecutionRevision => Session.ExecutionRevision;
    public string? PersistentAccountIdentity => Session.PersistentAccountIdentity;
    public string? LastError => _localError ?? Session.LastError;
    public IEnumerable<Order> ActiveOrders => Session.OrdersFor(Instrument.Symbol).Where(o => o.State == OrderStates.Active);
    public event Action? Changed;
    private void RefreshPosition()
    {
        if (!Session.PositionReady) return;
        // Capture one committed native symbol snapshot. Execution callbacks can precede the final Changed event.
        var position = Session.PositionFor(Instrument.Symbol);
        _position.Volume = position?.Size ?? 0; // Only a complete authoritative account snapshot proves absent = flat.
        _position.AveragePrice = position?.AvgPrice ?? 0;
        _position.UnrealizedPnL = position?.UnrealizedPnl ?? 0;
    }

    private void OnChanged()
    {
        if (_disposed) return;
        RefreshPosition();
        Changed?.Invoke();
    }
    public Order Submit(PreparedOrderIntent intent)
    {
        if (_disposed || (_retired && !intent.Request.ReduceOnly))
        {
            _localError = "This Bybit account view is retired; only native reducing exits and cancel remain available.";
            return new Order { Direction = intent.Request.Direction, Type = intent.Request.Type,
                QuantityToFill = intent.Request.Quantity, Unfilled = intent.Request.Quantity,
                State = OrderStates.Failed, Status = OrderStatus.Rejected };
        }
        _localError = null;
        return Session.Submit(Instrument, LastPrice, intent, ViewPermission);
    }

    public Order Replace(Order order, PreparedOrderIntent intent)
    {
        if (_retired && !order.ReduceOnly) { _localError = "A retired Bybit account view cannot amend opening exposure."; Changed?.Invoke(); return order; }
        return Session.Replace(order, intent, Instrument, ViewPermission);
    }
    private Order Send(OrderDirections direction, OrderTypes type, decimal quantity, decimal price, string comment, bool reduceOnly = false)
    {
        if (_disposed || (_retired && !reduceOnly))
        {
            _localError = "This Bybit account view is retired; only native reducing exits and cancel remain available.";
            return new Order { Direction = direction, Type = type, QuantityToFill = quantity, Unfilled = quantity,
                Price = price, TriggerPrice = price, ReduceOnly = reduceOnly, Comment = comment, State = OrderStates.Failed, Status = OrderStatus.Rejected };
        }
        _localError = null;
        return Session.Place(Instrument, LastPrice, direction, type, quantity, price, comment, reduceOnly, ViewPermission);
    }
    public Order Market(OrderDirections dir, decimal qty, string comment = "") => Send(dir, OrderTypes.Market, qty, 0, comment);
    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => Send(dir, OrderTypes.Limit, qty, price, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => Send(dir, OrderTypes.Stop, qty, trigger, comment);
    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") => Send(dir, type, qty, price, comment, true);
    public void Cancel(Order order) => Session.Cancel(order);
    public void CancelAt(decimal price)
    {
        foreach (var order in ActiveOrders.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price)) Cancel(order);
    }
    public void CancelAll() => Session.CancelAll(Instrument.Symbol);
    public void Modify(Order order, decimal newPrice) => Replace(order, newPrice, order.Unfilled);
    public Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        if (_retired && !order.ReduceOnly) { _localError = "A retired Bybit account view cannot amend opening exposure."; Changed?.Invoke(); return order; }
        return Session.Replace(order, newPrice, newQuantity, Instrument, ViewPermission);
    }
    public bool IsReplacementPending(Order order) => Session.IsReplacementPending(order);
    public string? StableOrderIdentityOf(Order order) => Session.StableOrderIdentityOf(order);
    public AccountExecutionSnapshot GetExecutions(Order order) =>
        order.Security.Code == Instrument.Symbol ? Session.GetExecutions(order) : UnknownExecutions;
    public void Flatten()
    {
        if (_disposed) { _localError = "This Bybit account view is closed; flatten was not queued."; return; }
        _localError = null;
        Session.Flatten(Instrument);
    }
    public void Reverse()
    {
        if (_retired) { _localError = "A retired Bybit account view cannot reverse exposure."; Changed?.Invoke(); return; }
        _localError = null;
        Session.Reverse(Instrument, LastPrice, ViewPermission);
    }
    private string? ViewPermission() => _retired || _disposed ? "This Bybit broker view was retired before mutation transport." : null;
    public void Retire() { _retired = true; Changed?.Invoke(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Session.Changed -= OnChanged;
        if (_ownsSession) Session.Dispose();
    }
}
