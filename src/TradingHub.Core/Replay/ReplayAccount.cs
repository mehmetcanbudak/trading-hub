using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Replay;

/// <summary>
/// A run-owned paper account. Market fills use the last delivered print; resting orders share the delivered
/// print's volume in submission order. This is a deterministic practice estimate, not exchange queue simulation.
/// It has no broker transport, shared simulation portfolio, wall clock or live account journal.
/// </summary>
public sealed class ReplayAccount : TradingManager, ITradingAccount, ITrackedOrderReplacement, IAccountExecutionSource, IObservedMarketPrice
{
    private static readonly AccountExecutionSnapshot EmptyExecutions = new(Array.Empty<AccountExecution>(), true);
    private static readonly AccountExecutionSnapshot UnknownExecutions = new(Array.Empty<AccountExecution>(), false);
    private readonly ReplaySession _run;
    private readonly ReplayFees _fees;
    private readonly List<Order> _orders = new();
    private readonly List<MyTrade> _trades = new();
    private readonly List<AccountFill> _journal = new();
    private readonly IReadOnlyList<Order> _orderView;
    private readonly IReadOnlyList<MyTrade> _tradeView;
    private readonly IReadOnlyList<AccountFill> _journalView;
    private readonly Dictionary<Order, ReplayExecutions> _executions = new();
    private long _nextOrder, _nextFill;

    internal ReplayAccount(ReplaySession run, Instrument instrument, decimal startBalance, ReplayFees fees)
    {
        _run = run;
        PersistentAccountIdentity = run.AccountId;
        _fees = fees;
        _orderView = _orders.AsReadOnly();
        _tradeView = _trades.AsReadOnly();
        _journalView = _journal.AsReadOnly();
        Instrument = instrument;
        Portfolio = new Portfolio { AccountID = run.AccountId, Name = "Replay practice", BeginBalance = startBalance, Balance = startBalance,
            Currency = Enum.TryParse<MoneyCurrency>(instrument.QuoteAsset, ignoreCase: true, out var currency) ? currency : null };
        Security = new Security { Code = instrument.Symbol, SecurityId = instrument.Key, Exchange = instrument.Exchange,
            TickSize = instrument.TickSize, TickCost = instrument.TickSize * instrument.PointValue, LotSize = instrument.VolumeStep };
        Position = new Position { Security = Security, Portfolio = Portfolio };
    }

    public Instrument Instrument { get; }
    public string Name => "Replay practice · " + _run.RunId.ToString("N")[..8];
    public bool IsReal => false;
    public string? LastError { get; private set; }
    public decimal LastPrice { get; private set; }
    public bool HasPrice { get; private set; }
    public bool HasObservedPrice { get; private set; }
    public bool PositionReady => true;
    public bool OrdersReady => true;
    Position ITradingAccount.Position => Position!;
    public override IReadOnlyList<Order> Orders => _orderView;
    public override IReadOnlyList<MyTrade> MyTrades => _tradeView;
    public IReadOnlyList<AccountFill> Journal => _journalView;
    public long ExecutionRevision => _nextFill;
    public string PersistentAccountIdentity { get; }
    public bool HasKnownPrice => HasPrice;
    public AccountExecutionSnapshot GetExecutions(Order order) => _executions.TryGetValue(order, out var executions)
        ? executions.Snapshot : _orders.Contains(order) && order.Filled == 0 ? EmptyExecutions : UnknownExecutions;
    private sealed class ReplayExecutions
    {
        private readonly List<AccountExecution> _facts = new();
        private AccountExecutionSnapshot? _snapshot;
        public AccountExecutionSnapshot Snapshot => _snapshot ??= new(_facts.ToArray(), true);
        public void Add(AccountExecution execution) { _facts.Add(execution); _snapshot = null; }
    }
    public IEnumerable<Order> ActiveOrders => _orders.Where(o => o.State == OrderStates.Active);
    public event Action? Changed;

    public Order Market(OrderDirections dir, decimal qty, string comment = "") => Submit(dir, OrderTypes.Market, qty, 0, false, comment);
    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => Submit(dir, OrderTypes.Limit, qty, price, false, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => Submit(dir, OrderTypes.Stop, qty, trigger, false, comment);
    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") => Submit(dir, type, qty, price, true, comment);
    public bool IsReplacementPending(Order order) => false;

    private bool ValidQuantity(decimal quantity) => quantity > 0 && quantity % Instrument.VolumeStep == 0;

    private Order Submit(OrderDirections dir, OrderTypes type, decimal quantity, decimal price, bool reduceOnly, string comment, Order? supplied = null)
    {
        if (supplied != null && (supplied.State != OrderStates.None || (supplied.Portfolio.IsReplay() && !ReferenceEquals(supplied.Portfolio, Portfolio))))
            throw new InvalidOperationException("An owned or already submitted order cannot be moved into another replay run.");
        var order = supplied ?? new Order();
        order.ExchangeOrderId = _run.AccountId + ":" + ++_nextOrder;
        order.Security = Security!;
        order.Portfolio = Portfolio!;
        order.Time = _run.ClockUtc;
        order.Direction = dir;
        order.Type = type;
        order.QuantityToFill = order.Unfilled = quantity;
        order.Comment = comment;
        order.ReduceOnly = reduceOnly;
        if (type == OrderTypes.Stop) order.TriggerPrice = Instrument.RoundPrice(price);
        else if (type == OrderTypes.Limit) order.Price = Instrument.RoundPrice(price);
        _orders.Add(order);
        if (_run.Ended || !ValidQuantity(quantity) || !Enum.IsDefined(dir) || type is not (OrderTypes.Market or OrderTypes.Limit or OrderTypes.Stop)
            || (type == OrderTypes.Market && !HasObservedPrice))
        {
            LastError = _run.Ended ? "Replay has ended; orders are disabled." : "Replay order needs a valid quantity step, price and delivered market print.";
            order.State = OrderStates.Failed;
            order.Status = OrderStatus.Rejected;
        }
        else
        {
            LastError = null;
            order.State = OrderStates.Active;
            order.Status = OrderStatus.Placed;
            if (type == OrderTypes.Market || (type == OrderTypes.Limit && HasObservedPrice &&
                (dir == OrderDirections.Buy ? price >= LastPrice : price <= LastPrice)))
                Fill(order, LastPrice, quantity);
        }
        Notify();
        return order;
    }

    public void Cancel(Order order)
    {
        if (!_orders.Contains(order) || order.State != OrderStates.Active) return;
        order.State = OrderStates.Done;
        order.Status = OrderStatus.Canceled;
        Notify();
    }
    public void CancelAt(decimal price)
    {
        foreach (var order in ActiveOrders.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price).ToArray()) Cancel(order);
    }
    public void CancelAll()
    {
        foreach (var order in ActiveOrders.ToArray()) Cancel(order);
    }
    public void Modify(Order order, decimal newPrice)
    {
        if (_run.Ended || !_orders.Contains(order) || order.State != OrderStates.Active) return;
        if (order.Type == OrderTypes.Stop) order.TriggerPrice = Instrument.RoundPrice(newPrice);
        else order.Price = Instrument.RoundPrice(newPrice);
        Notify();
    }
    public Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        if (_run.Ended || !_orders.Contains(order) || order.State != OrderStates.Active) return order;
        if (newQuantity == 0) { Cancel(order); return order; }
        if (!ValidQuantity(newQuantity)) { LastError = "Replacement quantity must match the instrument's quantity step."; Notify(); return order; }
        order.QuantityToFill = order.Filled + newQuantity;
        order.Unfilled = newQuantity;
        Modify(order, newPrice);
        return order;
    }
    public void Flatten()
    {
        CancelAll();
        decimal position = Position!.Volume;
        if (position != 0) Exit(OrderTypes.Market, position > 0 ? OrderDirections.Sell : OrderDirections.Buy, Math.Abs(position), 0, "Replay close");
    }
    public void Reverse()
    {
        decimal position = Position!.Volume;
        if (position != 0) Market(position > 0 ? OrderDirections.Sell : OrderDirections.Buy, 2 * Math.Abs(position), "Replay reverse");
    }

    internal void BlockAvailability()
    {
        HasObservedPrice = false;
        Notify();
    }

    internal void Advance(in Trade trade)
    {
        LastPrice = trade.Price;
        HasPrice = true;
        HasObservedPrice = trade.IsObservedTrade;
        // Compact/synthetic buckets are not individual executable prints. They may mark equity, not fill orders.
        if (trade.Provenance.IsObservedTrade)
        {
            decimal remaining = Math.Floor(trade.Volume / Instrument.VolumeStep) * Instrument.VolumeStep;
            int count = _orders.Count;
            for (int i = 0; i < count; i++)
            {
                var order = _orders[i];
                if (order.State != OrderStates.Active || remaining <= 0) continue;
                bool triggered = order.Type == OrderTypes.Market || (order.Type == OrderTypes.Stop
                    ? order.Direction == OrderDirections.Buy ? trade.Price >= order.TriggerPrice : trade.Price <= order.TriggerPrice
                    : order.Direction == OrderDirections.Buy ? trade.Price <= order.Price : trade.Price >= order.Price);
                if (!triggered) continue;
                if (order.Type == OrderTypes.Stop) order.Type = OrderTypes.Market;
                decimal filled = Fill(order, order.Type == OrderTypes.Limit ? order.Price : trade.Price, Math.Min(order.Unfilled, remaining));
                remaining -= filled;
            }
        }
        Mark();
        Notify();
    }

    private decimal Fill(Order order, decimal price, decimal quantity)
    {
        decimal before = Position!.Volume;
        if (order.ReduceOnly)
        {
            bool reduces = order.Direction == OrderDirections.Buy ? before < 0 : before > 0;
            if (!reduces) { Cancel(order); return 0; }
            quantity = Math.Min(quantity, Math.Abs(before));
        }
        if (quantity <= 0) return 0;
        int direction = order.Direction == OrderDirections.Buy ? 1 : -1;
        decimal signed = direction * quantity;
        decimal closing = before != 0 && Math.Sign(before) != direction ? Math.Min(quantity, Math.Abs(before)) : 0;
        decimal gross = closing * (price - Position.AveragePrice) * Math.Sign(before) * Instrument.PointValue;
        decimal fee = quantity * (_fees.PerUnit + Math.Abs(price) * Instrument.PointValue * _fees.NotionalRate);
        decimal after = before + signed;
        if (before == 0 || Math.Sign(before) == direction)
            Position.AveragePrice = (Position.AveragePrice * Math.Abs(before) + price * quantity) / Math.Abs(after);
        else if (after == 0) Position.AveragePrice = 0;
        else if (Math.Sign(after) != Math.Sign(before)) Position.AveragePrice = price;
        Position.Volume = after;
        Position.RealizedPnL += gross - fee;
        Position.Commission += fee;
        Portfolio!.RealizedPnL += gross - fee;
        Portfolio.Commission += fee;
        Portfolio.Balance = Portfolio.BeginBalance + Portfolio.RealizedPnL;
        order.Unfilled -= quantity;
        order.State = order.Unfilled == 0 ? OrderStates.Done : OrderStates.Active;
        order.Status = order.Unfilled == 0 ? OrderStatus.Filled : OrderStatus.PartlyFilled;
        long sequence = ++_nextFill;
        var fill = new AccountFill(_run.AccountId + ":" + sequence, _run.ClockUtc, Instrument.Symbol, direction > 0,
            quantity, price, Instrument.PointValue, fee, order.ExchangeOrderId);
        _journal.Add(fill);
        if (!_executions.TryGetValue(order, out var executions)) _executions.Add(order, executions = new ReplayExecutions());
        executions.Add(new AccountExecution(fill.Id, quantity, price, _run.ClockUtc, fee, Instrument.QuoteAsset, fee, sequence));
        var ownTrade = new MyTrade { Id = sequence, Order = order, Security = Security!, Portfolio = Portfolio,
            Time = _run.ClockUtc, OrderDirection = order.Direction, Price = price, Volume = quantity, Commission = fee, RealizedPnL = gross - fee };
        _trades.Add(ownTrade);
        Mark();
        // A notification listener cannot unwind a booked fill or interrupt sibling protection listeners.
        try { RaiseNewMyTrade(ownTrade); } catch { }
        try { RaiseOrderChanged(order); } catch { }
        try { RaisePositionChanged(Position); } catch { }
        if (order.ReduceOnly && after == 0 && order.Unfilled > 0) Cancel(order);
        return quantity;
    }

    private void Mark()
    {
        Position!.UnrealizedPnL = HasPrice ? (LastPrice - Position.AveragePrice) * Position.Volume * Instrument.PointValue : 0;
        Portfolio!.UnrealizedPnL = Position.UnrealizedPnL;
    }
    private void Notify()
    {
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList()) { try { handler(); } catch { } }
    }
    public override Task OpenOrderAsync(Order order)
    {
        Submit(order.Direction, order.Type, order.QuantityToFill, order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price, order.ReduceOnly, order.Comment, order);
        return Task.CompletedTask;
    }
    public override Task CancelOrderAsync(Order order) { Cancel(order); return Task.CompletedTask; }
    public override Task ModifyOrderAsync(Order order, decimal newPrice) { Modify(order, newPrice); return Task.CompletedTask; }
}
