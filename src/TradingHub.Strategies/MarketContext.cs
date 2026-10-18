using System.ComponentModel;

namespace TradingHub.Strategies;

/// <summary>Condición del mercado según la eficiencia de la tendencia de las últimas 24 horas.</summary>
public enum MarketRegime
{
    [Description("No data")] Unknown,
    [Description("Range")] Range,
    [Description("Mixed")] Mixed,
    [Description("Trend")] Trend,
}

/// <summary>
/// En qué condiciones de mercado puede abrir operaciones una estrategia (filtro de contexto): cualquier combinación de
/// rango, mixto, tendencia alcista y tendencia bajista. Los valores nuevos van siempre al final (se guardan como número).
/// </summary>
public enum RegimeFilter
{
    [Description("All")] All,
    [Description("Range only")] RangeOnly,
    [Description("No trend (range or mixed)")] NoTrend,
    [Description("Trend only")] TrendOnly,
    [Description("No range (mixed or trend)")] NoRange,
    [Description("Uptrend only")] UpTrendOnly,
    [Description("Downtrend only")] DownTrendOnly,
    [Description("Mixed only")] MixedOnly,
    [Description("Range or uptrend")] RangeOrUpTrend,
    [Description("Range or downtrend")] RangeOrDownTrend,
    [Description("Mixed or uptrend")] MixedOrUpTrend,
    [Description("Mixed or downtrend")] MixedOrDownTrend,
    [Description("All except downtrend")] NoDownTrend,
    [Description("All except uptrend")] NoUpTrend,
    [Description("Range or trend (not mixed)")] NoMixed,
}

/// <summary>Las cuatro condiciones de mercado, combinables.</summary>
[Flags]
public enum RegimeSet
{
    None = 0,
    Range = 1,
    Mixed = 2,
    UpTrend = 4,
    DownTrend = 8,
    All = Range | Mixed | UpTrend | DownTrend,
}

/// <summary>
/// Contexto del mercado para las estrategias y sus filtros, con las velas ya cerradas (sin mirar el futuro):
/// <list type="bullet">
/// <item>Tendencia: eficiencia de los cierres por hora de las últimas 24 horas, |movimiento neto| / recorrido (0 = rango
/// puro, 1 = tendencia limpia; una caminata al azar da ~0,2). Rango hasta 0,15, tendencia desde 0,35.</item>
/// <item>Dirección: si en esas 24 horas subió o bajó (una tendencia es alcista o bajista según eso).</item>
/// <item>Volatilidad: desvío de los retornos por hora de esas 24 horas, en %.</item>
/// <item>Percentil de volatilidad: dónde está esa volatilidad entre las de los últimos 30 días (0 = la más baja).</item>
/// </list>
/// Con velas de una hora o más se usan las últimas 25 velas en su propia escala. Se actualiza vela a vela.
/// </summary>
public sealed class MarketContextTracker
{
    public const double RangeLimit = 0.15, TrendLimit = 0.35;

    /// <summary>Percentiles de volatilidad (del último mes) hasta el que es baja y desde el que es alta.</summary>
    public const double LowVolatility = 33, HighVolatility = 67;
    private const int RankHours = 30 * 24;

    private readonly bool _coarse;
    private readonly List<decimal> _closes = new(); // cierres por hora (o por vela si son de una hora o más)
    private readonly Queue<double> _history = new(); // volatilidad de 24 horas tomada una vez por hora (30 días)
    private long _hour = long.MinValue;

    public MarketContextTracker(bool coarse = false) => _coarse = coarse;

    /// <summary>Eficiencia de la tendencia (null si todavía no hay 7 cierres).</summary>
    public double? Trend { get; private set; }

    /// <summary>Hacia dónde se movió en esas 24 horas: 1 subió, -1 bajó, 0 quedó igual (null sin datos).</summary>
    public int? Direction { get; private set; }

    /// <summary>Movimiento neto de esas 24 horas, en % (null sin datos).</summary>
    public double? Change { get; private set; }

    /// <summary>Volatilidad de las últimas 24 horas en % (null si todavía no hay datos).</summary>
    public double? Volatility { get; private set; }

    /// <summary>Percentil de la volatilidad actual entre la de los últimos 30 días (null con menos de un día de historia).</summary>
    public double? VolatilityRank { get; private set; }

    public MarketRegime Regime => Trend is not { } t ? MarketRegime.Unknown : t <= RangeLimit ? MarketRegime.Range : t >= TrendLimit ? MarketRegime.Trend : MarketRegime.Mixed;

    /// <summary>«Tendencia alcista», «Rango», «Mixto»… para mostrar.</summary>
    public string RegimeLabel => Label(Regime, Direction);

    public static string Label(MarketRegime regime, int? direction) => regime switch
    {
        MarketRegime.Range => "Range",
        MarketRegime.Mixed => "Mixed",
        MarketRegime.Trend => direction > 0 ? "Uptrend" : direction < 0 ? "Downtrend" : "Trend",
        _ => "No data",
    };

    /// <summary>Volatilidad baja (hasta el percentil 33 del último mes), normal o alta (desde el 67).</summary>
    public static string VolatilityLabel(double? rank) => rank is not { } r ? "no data" : r <= LowVolatility ? "low" : r >= HighVolatility ? "high" : "normal";

    /// <summary>
    /// Suma historia previa por hora (para arrancar en vivo con las 24 horas y el mes de volatilidad completos, como en un
    /// backtest largo). Con velas de una hora o más se agrupa en velas de <paramref name="barPeriod"/>.
    /// </summary>
    public void AddHistory(IEnumerable<(DateTime Time, decimal Close)> hourly, TimeSpan barPeriod)
    {
        if (!_coarse)
        {
            foreach (var (t, c) in hourly) Add(t, c);
            return;
        }

        long size = Math.Max(TimeSpan.TicksPerHour, barPeriod.Ticks), bucket = long.MinValue;
        decimal close = 0;
        foreach (var (t, c) in hourly)
        {
            long b = t.Ticks - t.Ticks % size;
            if (b != bucket && bucket != long.MinValue) Add(new DateTime(bucket, DateTimeKind.Utc), close);
            bucket = b;
            close = c;
        }

        if (bucket != long.MinValue) Add(new DateTime(bucket, DateTimeKind.Utc), close);
    }

    /// <summary>Suma una vela cerrada.</summary>
    public void Add(DateTime time, decimal close)
    {
        if (_coarse)
        {
            _closes.Add(close);
            if (_closes.Count > 25) _closes.RemoveAt(0);
            Recompute(sample: true);
            return;
        }

        long hour = time.Ticks / TimeSpan.TicksPerHour;
        if (hour != _hour)
        {
            // hora nueva: la anterior quedó cerrada (se guarda su volatilidad para el percentil)
            bool sample = _hour != long.MinValue;
            _closes.Add(close);
            _hour = hour;
            if (_closes.Count > 25) _closes.RemoveAt(0);
            Recompute(sample);
        }
        else if (_closes.Count > 0)
        {
            _closes[^1] = close; // la última vela de la hora es su cierre
            Recompute(sample: false);
        }
    }

    private void Recompute(bool sample)
    {
        if (_closes.Count < 7)
        {
            Trend = Volatility = VolatilityRank = Change = null;
            Direction = null;
            return;
        }

        decimal path = 0;
        double sum = 0, sumSq = 0;
        int n = 0;
        for (int i = 1; i < _closes.Count; i++)
        {
            path += Math.Abs(_closes[i] - _closes[i - 1]);
            if (_closes[i - 1] <= 0) continue;
            double r = (double)((_closes[i] - _closes[i - 1]) / _closes[i - 1]);
            sum += r;
            sumSq += r * r;
            n++;
        }

        Trend = path > 0 ? Math.Round((double)(Math.Abs(_closes[^1] - _closes[0]) / path), 4) : 0;
        Direction = Math.Sign(_closes[^1] - _closes[0]);
        Change = _closes[0] > 0 ? Math.Round((double)((_closes[^1] - _closes[0]) / _closes[0]) * 100, 3) : null;
        double vol = n > 1 ? Math.Sqrt(Math.Max(0, (sumSq - sum * sum / n) / (n - 1))) * 100 : 0;
        Volatility = Math.Round(vol, 5);
        if (sample)
        {
            _history.Enqueue(vol);
            if (_history.Count > RankHours) _history.Dequeue();
        }

        if (_history.Count < 24)
        {
            VolatilityRank = null;
            return;
        }

        int below = 0;
        foreach (var v in _history)
            if (v < vol)
                below++;
        VolatilityRank = Math.Round(100.0 * below / _history.Count, 1);
    }

    /// <summary>¿El filtro deja abrir una operación con este contexto? Sin datos para juzgar, no bloquea.</summary>
    public bool Allows(RegimeFilter filter, double minRank, double maxRank)
    {
        bool regimeOk = Regime == MarketRegime.Unknown || RegimeAllows(filter, Regime, Direction);
        bool volOk = VolatilityRank is not { } rank || (rank >= minRank && rank <= maxRank);
        return regimeOk && volOk;
    }

    /// <summary>¿El filtro de régimen deja entrar en esa condición de mercado (sin mirar la volatilidad)?</summary>
    public static bool RegimeAllows(RegimeFilter filter, MarketRegime regime, int? direction)
    {
        var now = SetOf(regime, direction);
        return now == RegimeSet.None || (SetOf(filter) & now) != 0;
    }

    /// <summary>Las condiciones en que deja entrar un filtro.</summary>
    public static RegimeSet SetOf(RegimeFilter filter) => filter switch
    {
        RegimeFilter.RangeOnly => RegimeSet.Range,
        RegimeFilter.NoTrend => RegimeSet.Range | RegimeSet.Mixed,
        RegimeFilter.TrendOnly => RegimeSet.UpTrend | RegimeSet.DownTrend,
        RegimeFilter.NoRange => RegimeSet.Mixed | RegimeSet.UpTrend | RegimeSet.DownTrend,
        RegimeFilter.UpTrendOnly => RegimeSet.UpTrend,
        RegimeFilter.DownTrendOnly => RegimeSet.DownTrend,
        RegimeFilter.MixedOnly => RegimeSet.Mixed,
        RegimeFilter.RangeOrUpTrend => RegimeSet.Range | RegimeSet.UpTrend,
        RegimeFilter.RangeOrDownTrend => RegimeSet.Range | RegimeSet.DownTrend,
        RegimeFilter.MixedOrUpTrend => RegimeSet.Mixed | RegimeSet.UpTrend,
        RegimeFilter.MixedOrDownTrend => RegimeSet.Mixed | RegimeSet.DownTrend,
        RegimeFilter.NoDownTrend => RegimeSet.Range | RegimeSet.Mixed | RegimeSet.UpTrend,
        RegimeFilter.NoUpTrend => RegimeSet.Range | RegimeSet.Mixed | RegimeSet.DownTrend,
        RegimeFilter.NoMixed => RegimeSet.Range | RegimeSet.UpTrend | RegimeSet.DownTrend,
        _ => RegimeSet.All,
    };

    /// <summary>El filtro que deja entrar justo en esas condiciones (sin ninguna, o con todas: sin filtro).</summary>
    public static RegimeFilter FilterOf(RegimeSet set)
    {
        if (set == RegimeSet.None) return RegimeFilter.All;
        foreach (var f in Enum.GetValues<RegimeFilter>())
            if (SetOf(f) == set)
                return f;
        return RegimeFilter.All;
    }

    /// <summary>La condición de un momento: rango, mixto o tendencia alcista o bajista (una tendencia sin dirección cuenta como las dos).</summary>
    public static RegimeSet SetOf(MarketRegime regime, int? direction) => regime switch
    {
        MarketRegime.Range => RegimeSet.Range,
        MarketRegime.Mixed => RegimeSet.Mixed,
        MarketRegime.Trend => direction > 0 ? RegimeSet.UpTrend : direction < 0 ? RegimeSet.DownTrend : RegimeSet.UpTrend | RegimeSet.DownTrend,
        _ => RegimeSet.None,
    };

    /// <summary>La condición según la eficiencia de la tendencia y su dirección (las que guarda cada operación).</summary>
    public static RegimeSet SetOf(double trend, int? direction) =>
        SetOf(trend <= RangeLimit ? MarketRegime.Range : trend >= TrendLimit ? MarketRegime.Trend : MarketRegime.Mixed, direction);

    /// <summary>Junta dos series de cierres por hora (la segunda pisa a la primera en las horas que se repiten), en orden.</summary>
    public static List<(DateTime Time, decimal Close)> Merge(IEnumerable<(DateTime Time, decimal Close)>? first, IEnumerable<(DateTime Time, decimal Close)> second)
    {
        var map = new SortedDictionary<DateTime, decimal>();
        if (first != null)
            foreach (var (t, c) in first)
                map[t] = c;
        foreach (var (t, c) in second) map[t] = c;
        return map.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Cierres por hora de unos datos (el último precio de cada hora), para darle historia al contexto.</summary>
    public static List<(DateTime Time, decimal Close)> HourlyCloses(TickData data)
    {
        var list = new List<(DateTime, decimal)>();
        long hour = long.MinValue;
        for (int i = data.Start; i < data.End; i++)
        {
            long t = data.TimeTicks(i);
            long h = t - t % TimeSpan.TicksPerHour;
            decimal price = data.Instrument.FromTicks(data.PriceTicks(i));
            if (h != hour)
            {
                list.Add((new DateTime(h, DateTimeKind.Utc), price));
                hour = h;
            }
            else list[^1] = (list[^1].Item1, price);
        }

        return list;
    }

    /// <summary>Texto corto del contexto: «tendencia alcista (0.42) · volatilidad percentil 28».</summary>
    public string Describe() =>
        Trend is not { } t ? "no market context yet"
        : $"{RegimeLabel.ToLowerInvariant()} ({t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})" +
          (VolatilityRank is { } r ? $" · volatility percentile {r:0}" : "");
}
