namespace TradingHub.Trading;

public enum OrderDirections
{
    Buy,
    Sell,
}

public enum OrderTypes
{
    Market,
    Limit,
    Stop,
    StopLimit,
}

public enum OrderStates
{
    None,
    Active,
    Done,
    Failed,
}

public enum OrderStatus
{
    None,
    Placed,
    PartlyFilled,
    Filled,
    Canceled,
    Rejected,
}

/// <summary>Instrumento visto por el módulo de trading.</summary>
public class Security
{
    public string Code { get; set; } = "";
    public string SecurityId { get; set; } = "";
    public string Exchange { get; set; } = "";
    public decimal TickSize { get; set; } = 1m;
    public decimal TickCost { get; set; } = 1m;
    public decimal LotSize { get; set; } = 1m;

    public override string ToString() => SecurityId;
}

/// <summary>Cuenta de trading (en esta plataforma, la cuenta simulada).</summary>
public class Portfolio
{
    public string AccountID { get; set; } = "SIM";
    public string Name { get; set; } = "Cuenta simulada";
    public decimal BeginBalance { get; set; } = 10_000m;
    public decimal Balance { get; set; } = 10_000m;
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL { get; set; }
    public decimal TotalPnL => RealizedPnL + UnrealizedPnL;
    public decimal Commission { get; set; }
    public MoneyCurrency? Currency { get; set; } = MoneyCurrency.USD;
    public decimal? BalanceAvailable { get; set; }
    public decimal BlockedMargin { get; set; }
    public decimal Leverage { get; set; } = 1;
    public decimal OpenPnL => UnrealizedPnL;
    public decimal ClosedPnL => RealizedPnL;

    /// <summary>Si la cuenta es de un replay.</summary>
    public bool IsReplay() => AccountID.StartsWith("REPLAY", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => AccountID;
}

public class Order
{
    private static long _nextId;

    public long Id { get; set; } = Interlocked.Increment(ref _nextId);
    public string ExchangeOrderId { get; set; } = "";
    public Security Security { get; set; } = new();
    public Portfolio Portfolio { get; set; } = new();
    public OrderDirections Direction { get; set; }
    public OrderTypes Type { get; set; }
    public decimal Price { get; set; }
    public decimal TriggerPrice { get; set; }
    public decimal QuantityToFill { get; set; }
    public decimal Unfilled { get; set; }
    public OrderStates State { get; set; } = OrderStates.None;
    public OrderStatus Status { get; set; } = OrderStatus.None;
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string Comment { get; set; } = "";
    public long Priority { get; set; }

    /// <summary>Solo puede reducir la posición (stops y objetivos).</summary>
    public bool ReduceOnly { get; set; }

    /// <summary>Volumen total de la orden.</summary>
    public decimal Volume => QuantityToFill;
    public decimal Filled => QuantityToFill - Unfilled;
    public bool Canceled => Status == OrderStatus.Canceled;

    public override string ToString() => $"#{Id} {Direction} {Type} {QuantityToFill} @ {(Type == OrderTypes.Stop ? TriggerPrice : Price)} [{Status}]";
}

/// <summary>Una ejecución propia.</summary>
public class MyTrade
{
    private static long _nextId;

    public long Id { get; set; } = Interlocked.Increment(ref _nextId);
    public Order? Order { get; set; }
    public Security Security { get; set; } = new();
    public Portfolio Portfolio { get; set; } = new();
    public OrderDirections OrderDirection { get; set; }
    public decimal Price { get; set; }
    public decimal Volume { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public decimal Commission { get; set; }

    /// <summary>PnL realizado por esta ejecución (si cerró posición).</summary>
    public decimal RealizedPnL { get; set; }

    public override string ToString() => $"{OrderDirection} {Volume} @ {Price} {Time:HH:mm:ss}";
}

/// <summary>Posición neta en un instrumento.</summary>
public class Position
{
    public Security Security { get; set; } = new();
    public Portfolio Portfolio { get; set; } = new();

    /// <summary>Volumen con signo (+ comprado, − vendido).</summary>
    public decimal Volume { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL { get; set; }
    public decimal OpenVolume => Volume;
    public decimal Commission { get; set; }
    public bool IsFlat => Volume == 0;

    public override string ToString() => IsFlat ? "No position" : $"{(Volume > 0 ? "LONG" : "SHORT")} {Math.Abs(Volume)} @ {AveragePrice}";
}

/// <summary>
/// Gestor de trading que ven los indicadores y la UI (órdenes, posición, ejecuciones).
/// La implementación concreta es el broker simulado.
/// </summary>
public class TradingManager
{
    public Security? Security { get; protected set; }
    public Portfolio? Portfolio { get; protected set; }
    public Position? Position { get; protected set; }

    public event Action<Security>? SecuritySelected;
    public event Action<Portfolio>? PortfolioSelected;
    public event Action<MyTrade>? NewMyTrade;
    public event Action<Order>? OrderChanged;
    public event Action<Position>? PositionChanged;

    public virtual IReadOnlyList<Order> Orders => Array.Empty<Order>();
    public virtual IReadOnlyList<MyTrade> MyTrades => Array.Empty<MyTrade>();

    public virtual Task OpenOrderAsync(Order order) => Task.CompletedTask;
    public virtual Task CancelOrderAsync(Order order) => Task.CompletedTask;
    public virtual Task ModifyOrderAsync(Order order, decimal newPrice) => Task.CompletedTask;

    protected void RaiseSecuritySelected(Security s) => SecuritySelected?.Invoke(s);
    protected void RaisePortfolioSelected(Portfolio p) => PortfolioSelected?.Invoke(p);
    protected void RaiseNewMyTrade(MyTrade t) => NewMyTrade?.Invoke(t);
    protected void RaiseOrderChanged(Order o) => OrderChanged?.Invoke(o);
    protected void RaisePositionChanged(Position p) => PositionChanged?.Invoke(p);
}

/// <summary>Monedas para mostrar valores en dinero.</summary>
public enum MoneyCurrency
{
    USD,
    EUR,
    GBP,
    JPY,
    CHF,
    AUD,
    CAD,
    USDT,
    USDC,
    BTC,
    ETH,
}

/// <summary>Factor de conversión volumen→dinero.</summary>
public class Valuation
{
    public decimal Factor { get; set; } = 1m;
    public bool IsEnabled { get; set; }
    public MoneyCurrency Currency { get; set; } = MoneyCurrency.USD;

    public bool TryGetDisplayFactor(out TradingHub.Indicators.VolumeValueFactor factor)
    {
        factor = new TradingHub.Indicators.VolumeValueFactor(Factor, Currency);
        return IsEnabled;
    }
}


