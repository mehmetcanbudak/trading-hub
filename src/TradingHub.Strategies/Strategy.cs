using System.Reflection;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>Nombre y descripción de una estrategia.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class StrategyAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; set; } = "";
}

/// <summary>Propiedad configurable de la estrategia (aparece en el panel de parámetros).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ParameterAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
    public string Description { get; set; } = "";
}

/// <summary>Rango a recorrer en la optimización (mínimo, máximo y paso).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OptimizeAttribute(double min, double max, double step) : Attribute
{
    public double Min { get; } = min;
    public double Max { get; } = max;
    public double Step { get; } = step;
}

public enum OrderSide
{
    Buy,
    Sell,
}

public enum StrategyOrderType
{
    Market,
    Limit,
    Stop,
}

public enum StrategyOrderState
{
    Active,
    Filled,
    Canceled,
}

/// <summary>Una orden de la estrategia.</summary>
public sealed class StrategyOrder
{
    public long Id { get; internal init; }
    public OrderSide Side { get; internal init; }
    public StrategyOrderType Type { get; internal init; }
    public decimal Quantity { get; internal set; }
    public decimal Price { get; internal set; }
    public string Name { get; internal init; } = "";
    public StrategyOrderState State { get; internal set; } = StrategyOrderState.Active;
    public DateTime Time { get; internal init; }
    public decimal FillPrice { get; internal set; }
    public DateTime FillTime { get; internal set; }

    /// <summary>Stop/target automático de la posición.</summary>
    public bool IsProtective { get; internal init; }

    internal long SubmittedAt { get; init; }
    internal bool Marketable { get; init; }
    internal string? OcoGroup { get; init; }
    internal object? Live { get; set; }

    /// <summary>En vivo: cantidad real enviada a la cuenta (con el tamaño de la cartera) y cuánto ya se contabilizó.</summary>
    internal decimal LiveQuantity { get; set; }
    internal decimal Booked { get; set; }

    public bool IsActive => State == StrategyOrderState.Active;

    public override string ToString() => $"{Name}: {(Side == OrderSide.Buy ? "buy" : "sell")} {Type} {Quantity}{(Type == StrategyOrderType.Market ? "" : " @ " + Price)}";
}

/// <summary>Lo que la estrategia necesita del entorno (backtest o vivo).</summary>
internal interface IStrategyHost
{
    Instrument Instrument { get; }
    ChartPeriod Period { get; }
    BarSeries Bars { get; }
    Bar? FormingBar { get; }
    DateTime Time { get; }
    decimal LastPrice { get; }
    TimeZoneInfo TimeZone { get; }
    SessionSettings Session { get; }
    decimal Position { get; }
    decimal AveragePrice { get; }
    decimal OpenPnL { get; }
    decimal RealizedPnL { get; }
    IReadOnlyList<StrategyOrder> ActiveOrders { get; }
    bool IsBacktest { get; }
    StrategyOrder Submit(OrderSide side, StrategyOrderType type, decimal quantity, decimal price, string name);
    void Cancel(StrategyOrder order);
    void Modify(StrategyOrder order, decimal price);
    void Log(string message);
}

/// <summary>
/// Base de las estrategias. Sobrescribí <see cref="OnInit"/> para crear indicadores y
/// <see cref="OnBar"/> (cierre de cada vela) u <see cref="OnTick"/> (cada operación) para decidir.
/// </summary>
public abstract class Strategy
{
    private readonly List<Series> _indicators = new();
    internal IStrategyHost Host = null!;

    // ----------------------------------------------------------------- parámetros comunes

    /// <summary>Cantidad por defecto de cada orden.</summary>
    [Parameter("Quantity", Description = "Contracts or units per order")]
    public decimal Quantity { get; set; } = 1;

    /// <summary>Stop loss automático en ticks para cada posición (0 = sin stop).</summary>
    [Parameter("Stop loss (ticks)", Description = "0 = no automatic stop")]
    public int StopLossTicks { get; set; }

    /// <summary>Take profit automático en ticks (0 = sin objetivo).</summary>
    [Parameter("Take profit (ticks)", Description = "0 = no automatic target")]
    public int TakeProfitTicks { get; set; }

    /// <summary>Stop que sigue al precio a esta distancia en ticks (0 = no).</summary>
    [Parameter("Trailing stop (ticks)", Description = "0 = no trailing stop")]
    public int TrailingStopTicks { get; set; }

    /// <summary>Filtro de contexto: solo abre operaciones en esta condición de mercado (las salidas siempre pasan).</summary>
    [Parameter("Regime filter", Description = "Only opens trades in the selected market regime (range, mixed, uptrend or downtrend over the last 24 h). All = no filter")]
    public RegimeFilter RegimeFilter { get; set; } = RegimeFilter.All;

    /// <summary>Filtro de contexto: no abre si la volatilidad está por debajo de este percentil del último mes.</summary>
    [Parameter("Minimum volatility (percentile)", Description = "0 to 100: blocks entries when 24-hour volatility is below this percentile of the last month. 0 = no filter")]
    public int MinVolatilityRank { get; set; }

    /// <summary>Filtro de contexto: no abre si la volatilidad está por encima de este percentil del último mes.</summary>
    [Parameter("Maximum volatility (percentile)", Description = "0 to 100: blocks entries when 24-hour volatility is above this percentile of the last month. 100 = no filter")]
    public int MaxVolatilityRank { get; set; } = 100;

    /// <summary>Si las velas guardan el volumen por precio (footprint). Activalo en OnInit si lo usás.</summary>
    protected internal bool UseFootprint { get; set; }

    /// <summary>Contexto del mercado (tendencia o rango y volatilidad), vela a vela.</summary>
    internal MarketContextTracker Context { get; set; } = new();

    /// <summary>Lo que la estrategia dibuja en el gráfico (rangos, niveles, flechas, textos y líneas).</summary>
    public StrategyCanvas Canvas { get; } = new();

    // ----------------------------------------------------------------- datos

    public Instrument Instrument => Host.Instrument;
    public ChartPeriod Period => Host.Period;

    /// <summary>Velas cerradas: <c>Bars[0]</c> es la última, <c>Bars[1]</c> la anterior…</summary>
    public BarSeries Bars => Host.Bars;

    /// <summary>Última vela cerrada.</summary>
    public Bar Bar => Host.Bars[0];

    /// <summary>Vela que se está formando (para usar en <see cref="OnTick"/>).</summary>
    public Bar? FormingBar => Host.FormingBar;

    public int CurrentBar => Host.Bars.Count - 1;

    /// <summary>Hora del mercado (UTC).</summary>
    public DateTime Time => Host.Time;

    /// <summary>Hora del mercado en la zona horaria de la sesión.</summary>
    public DateTime LocalTime => TimeZoneInfo.ConvertTimeFromUtc(Host.Time, Host.TimeZone);

    public decimal Price => Host.LastPrice;
    public decimal TickSize => Host.Instrument.TickSize;
    public bool IsBacktest => Host.IsBacktest;

    // ----------------------------------------------------------------- contexto del mercado

    /// <summary>Eficiencia de la tendencia de las últimas 24 horas (0 = rango puro, 1 = tendencia limpia; al azar ~0,2). null sin datos.</summary>
    public double? MarketTrend => Context.Trend;

    /// <summary>Hacia dónde se movió el mercado en esas 24 horas: 1 subió, -1 bajó, 0 igual (null sin datos).</summary>
    public int? MarketDirection => Context.Direction;

    /// <summary>Movimiento neto del mercado en esas 24 horas, en %.</summary>
    public double? MarketChange => Context.Change;

    /// <summary>Volatilidad de las últimas 24 horas: desvío de los retornos por hora, en %.</summary>
    public double? MarketVolatility => Context.Volatility;

    /// <summary>Percentil de esa volatilidad entre la del último mes (0 = la más baja, 100 = la más alta).</summary>
    public double? VolatilityRank => Context.VolatilityRank;

    /// <summary>Condición del mercado: Range (eficiencia ≤ 0,15), Mixed o Trend (≥ 0,35); Unknown sin datos.</summary>
    public MarketRegime Regime => Context.Regime;

    /// <summary>Si el filtro de contexto (RegimeFilter y percentiles de volatilidad) deja abrir operaciones ahora.</summary>
    public bool ContextAllowsEntries => !HasContextFilter || Context.Allows(RegimeFilter, MinVolatilityRank, MaxVolatilityRank);

    private bool HasContextFilter => RegimeFilter != RegimeFilter.All || MinVolatilityRank > 0 || MaxVolatilityRank < 100;

    // ----------------------------------------------------------------- posición

    /// <summary>Posición: positiva = largo, negativa = corto, 0 = sin posición.</summary>
    public decimal Position => Host.Position;

    public bool IsFlat => Host.Position == 0;
    public bool IsLong => Host.Position > 0;
    public bool IsShort => Host.Position < 0;
    public decimal AveragePrice => Host.AveragePrice;
    public decimal OpenPnL => Host.OpenPnL;
    public decimal NetProfit => Host.RealizedPnL;
    public IReadOnlyList<StrategyOrder> ActiveOrders => Host.ActiveOrders;

    // ----------------------------------------------------------------- órdenes

    protected StrategyOrder Buy(decimal? quantity = null, string name = "Buy") => Send(OrderSide.Buy, StrategyOrderType.Market, quantity ?? Quantity, 0, name);
    protected StrategyOrder Sell(decimal? quantity = null, string name = "Sell") => Send(OrderSide.Sell, StrategyOrderType.Market, quantity ?? Quantity, 0, name);
    protected StrategyOrder BuyLimit(decimal price, decimal? quantity = null, string name = "Limit buy") => Send(OrderSide.Buy, StrategyOrderType.Limit, quantity ?? Quantity, Round(price), name);
    protected StrategyOrder SellLimit(decimal price, decimal? quantity = null, string name = "Limit sell") => Send(OrderSide.Sell, StrategyOrderType.Limit, quantity ?? Quantity, Round(price), name);
    protected StrategyOrder BuyStop(decimal price, decimal? quantity = null, string name = "Buy stop") => Send(OrderSide.Buy, StrategyOrderType.Stop, quantity ?? Quantity, Round(price), name);
    protected StrategyOrder SellStop(decimal price, decimal? quantity = null, string name = "Sell stop") => Send(OrderSide.Sell, StrategyOrderType.Stop, quantity ?? Quantity, Round(price), name);

    private DateTime _filterLogged = DateTime.MinValue;

    /// <summary>
    /// Manda la orden pasando por el filtro de contexto: fuera del contexto elegido solo sale la parte que cierra lo que
    /// hay abierto (nunca abre ni aumenta); si no queda nada, la orden vuelve cancelada.
    /// </summary>
    private StrategyOrder Send(OrderSide side, StrategyOrderType type, decimal quantity, decimal price, string name)
    {
        if (ContextAllowsEntries) return Host.Submit(side, type, quantity, price, name);
        decimal pos = Host.Position;
        decimal closing = side == OrderSide.Buy ? (pos < 0 ? Math.Min(quantity, -pos) : 0) : (pos > 0 ? Math.Min(quantity, pos) : 0);
        if (closing < quantity && Host.Time - _filterLogged >= (Host.IsBacktest ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1)))
        {
            _filterLogged = Host.Time;
            Host.Log($"Context filter: entries are currently blocked ({Context.Describe()})");
        }

        if (closing > 0) return Host.Submit(side, type, closing, price, name);
        return new StrategyOrder { Side = side, Type = type, Quantity = quantity, Price = price, Name = name + " (context filter)", State = StrategyOrderState.Canceled, Time = Host.Time };
    }

    /// <summary>Entra largo a mercado (si estaba corto, da vuelta la posición).</summary>
    protected void EnterLong(decimal? quantity = null, string name = "Long")
    {
        decimal q = quantity ?? Quantity;
        if (Position < 0) q += -Position;
        if (Position <= 0) Buy(q, name);
    }

    /// <summary>Entra corto a mercado (si estaba largo, da vuelta la posición).</summary>
    protected void EnterShort(decimal? quantity = null, string name = "Short")
    {
        decimal q = quantity ?? Quantity;
        if (Position > 0) q += Position;
        if (Position >= 0) Sell(q, name);
    }

    /// <summary>Cierra la posición a mercado y cancela las órdenes de entrada pendientes.</summary>
    protected void ClosePosition(string name = "Close")
    {
        foreach (var o in Host.ActiveOrders.Where(o => !o.IsProtective).ToList()) Host.Cancel(o);
        if (Position > 0) Sell(Position, name);
        else if (Position < 0) Buy(-Position, name);
    }

    protected void Cancel(StrategyOrder order) => Host.Cancel(order);

    protected void CancelAll()
    {
        foreach (var o in Host.ActiveOrders.Where(o => !o.IsProtective).ToList()) Host.Cancel(o);
    }

    protected void Modify(StrategyOrder order, decimal price) => Host.Modify(order, Round(price));

    // ----------------------------------------------------------------- indicadores

    protected T Add<T>(T series) where T : Series
    {
        _indicators.Add(series);
        return series;
    }

    /// <summary>Los indicadores que creó (para medir el calentamiento).</summary>
    internal IReadOnlyList<Series> Indicators => _indicators;

    protected Sma Sma(int period, PriceSource source = PriceSource.Close) => Add(new Sma(period, source));
    protected Ema Ema(int period, PriceSource source = PriceSource.Close) => Add(new Ema(period, source));
    protected Rsi Rsi(int period = 14, PriceSource source = PriceSource.Close) => Add(new Rsi(period, source));
    protected Atr Atr(int period = 14) => Add(new Atr(period));
    protected Highest Highest(int period, PriceSource source = PriceSource.High) => Add(new Highest(period, source));
    protected Lowest Lowest(int period, PriceSource source = PriceSource.Low) => Add(new Lowest(period, source));
    protected StdDev StdDev(int period, PriceSource source = PriceSource.Close) => Add(new StdDev(period, source));
    protected Bollinger Bollinger(int period = 20, decimal k = 2, PriceSource source = PriceSource.Close) => Add(new Bollinger(period, k, source));
    protected Macd Macd(int fast = 12, int slow = 26, int signal = 9) => Add(new Macd(fast, slow, signal));
    protected Vwap Vwap() => Add(new Vwap(t => Host.Session.SessionDate(t)));
    protected CumulativeDelta CumulativeDelta(bool resetEachSession = true) => Add(new CumulativeDelta(resetEachSession ? t => Host.Session.SessionDate(t) : null));

    /// <summary>Serie propia calculada en cada vela (por ejemplo, la diferencia entre dos medias).</summary>
    protected CustomSeries Custom(string name, Func<BarSeries, decimal> calc) => Add(new CustomSeries(name, calc));

    // ----------------------------------------------------------------- ayudas

    protected static bool CrossAbove(Series a, Series b) => a.Count > 1 && b.Count > 1 && a[1] <= b[1] && a[0] > b[0];
    protected static bool CrossBelow(Series a, Series b) => a.Count > 1 && b.Count > 1 && a[1] >= b[1] && a[0] < b[0];
    protected static bool CrossAbove(Series a, decimal level) => a.Count > 1 && a[1] <= level && a[0] > level;
    protected static bool CrossBelow(Series a, decimal level) => a.Count > 1 && a[1] >= level && a[0] < level;

    /// <summary>Distancia de N ticks en precio.</summary>
    protected decimal Ticks(int n) => n * Host.Instrument.TickSize;

    protected decimal Round(decimal price) => Host.Instrument.RoundPrice(price);

    /// <summary>Si la hora local de la sesión está entre dos horarios (ej. 09:30 y 16:00).</summary>
    protected bool TimeBetween(TimeSpan from, TimeSpan to)
    {
        var t = LocalTime.TimeOfDay;
        return from <= to ? t >= from && t < to : t >= from || t < to;
    }

    protected void Log(string message) => Host.Log(message);

    // ----------------------------------------------------------------- dibujos en el gráfico

    /// <summary>
    /// Rectángulo (por ejemplo el rango de apertura): de <paramref name="from"/> a <paramref name="to"/> y entre dos precios.
    /// Repetir el id lo reemplaza (sirve para estirarlo vela a vela). Color "#RRGGBB" o un nombre ("verde", "rojo"…).
    /// </summary>
    protected void DrawBox(string id, DateTime from, decimal price1, DateTime to, decimal price2, string? color = null, int fillOpacity = 15, string? text = null) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.Box, Time1 = from, Price1 = price1, Time2 = to, Price2 = price2, Color = color, FillOpacity = Math.Clamp(fillOpacity, 0, 100), Text = text ?? "" });

    /// <summary>Línea de un punto (tiempo, precio) a otro.</summary>
    protected void DrawLine(string id, DateTime time1, decimal price1, DateTime time2, decimal price2, string? color = null, int width = 1, LineStyle style = LineStyle.Solid) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.Line, Time1 = time1, Price1 = price1, Time2 = time2, Price2 = price2, Color = color, Width = Math.Clamp(width, 1, 6), Style = style });

    /// <summary>Nivel horizontal desde <paramref name="from"/> (o desde el principio) hasta el borde derecho, con una etiqueta opcional.</summary>
    protected void DrawHorizontalLine(string id, decimal price, DateTime? from = null, string? color = null, int width = 1, LineStyle style = LineStyle.Dashed, string? text = null) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.HorizontalLine, Time1 = from ?? DateTime.MinValue, Price1 = price, Price2 = price, Color = color, Width = Math.Clamp(width, 1, 6), Style = style, Text = text ?? "", ExtendRight = true });

    /// <summary>Flecha hacia arriba con la punta en el precio (por ejemplo una ruptura alcista), con texto opcional.</summary>
    protected void DrawArrowUp(string id, DateTime time, decimal price, string? color = null, string? text = null) => DrawMarker(id, time, price, MarkerShape.ArrowUp, color, text);

    /// <summary>Flecha hacia abajo con la punta en el precio.</summary>
    protected void DrawArrowDown(string id, DateTime time, decimal price, string? color = null, string? text = null) => DrawMarker(id, time, price, MarkerShape.ArrowDown, color, text);

    /// <summary>Marca en un precio: flecha, círculo, cuadrado, rombo o cruz, con texto opcional.</summary>
    protected void DrawMarker(string id, DateTime time, decimal price, MarkerShape shape = MarkerShape.Circle, string? color = null, string? text = null) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.Marker, Time1 = time, Price1 = price, Time2 = time, Price2 = price, Color = color, Shape = shape, Text = text ?? "" });

    /// <summary>Texto en un punto del gráfico.</summary>
    protected void DrawText(string id, DateTime time, decimal price, string text, string? color = null) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.Text, Time1 = time, Price1 = price, Time2 = time, Price2 = price, Color = color, Text = text });

    /// <summary>
    /// Línea continua que se traza vela a vela (llamala en OnBar): por ejemplo Plot("Techo", _max[0]). null corta la línea
    /// (por ejemplo fuera de la sesión). Cada nombre es una línea distinta.
    /// </summary>
    protected void Plot(string name, decimal? value, string? color = null, int width = 1, LineStyle style = LineStyle.Solid) =>
        Canvas.Plot(name, Host.Bars.Count > 0 ? Host.Bars[0].Time : Host.Time, value, color, Math.Clamp(width, 1, 6), style);

    /// <summary>Borra un dibujo por su id.</summary>
    protected void RemoveDrawing(string id) => Canvas.Remove(id);

    /// <summary>Borra todos los dibujos y líneas.</summary>
    protected void ClearDrawings() => Canvas.Clear();

    // ----------------------------------------------------------------- ciclo de vida

    /// <summary>Al empezar: creá acá los indicadores.</summary>
    protected virtual void OnInit()
    {
    }

    /// <summary>Al cerrar cada vela.</summary>
    protected virtual void OnBar(Bar bar)
    {
    }

    /// <summary>En cada operación del mercado (más lento: usalo solo si lo necesitás).</summary>
    protected virtual void OnTick(Trade trade)
    {
    }

    /// <summary>Cuando se ejecuta una orden.</summary>
    protected virtual void OnOrderFilled(StrategyOrder order)
    {
    }

    /// <summary>Al empezar una sesión nueva (día de trading).</summary>
    protected virtual void OnSessionStart(DateOnly session)
    {
    }

    /// <summary>Al terminar (fin del backtest o al detener la estrategia).</summary>
    protected virtual void OnStop()
    {
    }

    // ----------------------------------------------------------------- llamado por el motor

    internal bool WantsTicks { get; private set; }

    /// <summary>La estrategia mira cada operación (OnTick), no solo las velas.</summary>
    public static bool UsesTicks(Type t)
    {
        var m = t.GetMethod(nameof(OnTick), BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(Trade) });
        return m != null && m.DeclaringType != typeof(Strategy);
    }

    internal void InternalInit(IStrategyHost host)
    {
        Host = host;
        _indicators.Clear();
        Context = new MarketContextTracker(host.Period.IsTimeBased && host.Period.ApproxDuration >= TimeSpan.FromHours(1));
        Canvas.Clear();
        WantsTicks = UsesTicks(GetType());
        OnInit();
    }

    internal void InternalBar(Bar bar)
    {
        Context.Add(bar.Time, bar.Close);
        foreach (var s in _indicators) s.Update(Host.Bars);
        OnBar(bar);
    }

    internal void InternalTick(in Trade t) => OnTick(t);
    internal void InternalFilled(StrategyOrder o) => OnOrderFilled(o);
    internal void InternalSession(DateOnly d) => OnSessionStart(d);
    internal void InternalStop() => OnStop();

    // ----------------------------------------------------------------- metadatos

    public static string DisplayName(Type t) => TradingHub.Common.EnglishText.Of(t.GetCustomAttribute<StrategyAttribute>()?.Name ?? t.Name);
    public static string Description(Type t) => TradingHub.Common.EnglishText.Of(t.GetCustomAttribute<StrategyAttribute>()?.Description ?? "");

    /// <summary>Propiedades configurables (con [Parameter]).</summary>
    public static IEnumerable<PropertyInfo> Parameters(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<ParameterAttribute>() != null)
            .OrderBy(p => p.DeclaringType == typeof(Strategy) ? 1 : 0)
            .ThenBy(p => p.MetadataToken);

    public static string ParameterName(PropertyInfo p) => TradingHub.Common.EnglishText.Of(p.GetCustomAttribute<ParameterAttribute>()?.Name ?? p.Name);

    /// <summary>Valores actuales de los parámetros.</summary>
    public Dictionary<string, object?> GetParameters() => Parameters(GetType()).ToDictionary(p => p.Name, p => p.GetValue(this));

    /// <summary>Asigna parámetros por nombre (convierte tipos numéricos).</summary>
    public void SetParameters(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var p in Parameters(GetType()))
        {
            if (!values.TryGetValue(p.Name, out var v) || v == null) continue;
            try
            {
                object converted = v switch
                {
                    System.Text.Json.JsonElement je => ConvertJson(je, p.PropertyType),
                    _ when p.PropertyType.IsEnum => Enum.Parse(p.PropertyType, v.ToString()!),
                    _ => Convert.ChangeType(v, p.PropertyType, System.Globalization.CultureInfo.InvariantCulture),
                };
                p.SetValue(this, converted);
            }
            catch
            {
                // valor incompatible: se deja el actual
            }
        }
    }

    private static object ConvertJson(System.Text.Json.JsonElement je, Type t)
    {
        if (t == typeof(int)) return je.GetInt32();
        if (t == typeof(long)) return je.GetInt64();
        if (t == typeof(double)) return je.GetDouble();
        if (t == typeof(decimal)) return je.GetDecimal();
        if (t == typeof(bool)) return je.GetBoolean();
        if (t.IsEnum) return Enum.Parse(t, je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString()! : je.GetInt32().ToString());
        return je.GetString() ?? "";
    }
}
