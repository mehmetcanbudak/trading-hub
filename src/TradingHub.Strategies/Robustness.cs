using System.Globalization;
using System.Text.Json;

namespace TradingHub.Strategies;

/// <summary>
/// Pruebas contra la sobreoptimización: Sharpe probabilístico y deflactado (Bailey y López de Prado), estabilidad de
/// los parámetros elegidos en el walk-forward y meseta alrededor de la mejor combinación de la optimización.
/// </summary>
public static class Robustness
{
    private const double EulerGamma = 0.5772156649015329;

    /// <summary>Distribución normal estándar acumulada.</summary>
    public static double NormalCdf(double x)
    {
        // erfc con la aproximación de Numerical Recipes (error < 1.2e-7)
        double z = Math.Abs(x) / Math.Sqrt(2);
        double t = 1 / (1 + 0.5 * z);
        double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 +
            t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? 1 - r / 2 : r / 2;
    }

    /// <summary>Inversa de la normal estándar (algoritmo de Acklam, error relativo &lt; 1.2e-9).</summary>
    public static double NormalInv(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double low = 0.02425, high = 1 - low;
        if (p < low)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        if (p > high)
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        double u = p - 0.5, r = u * u;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * u / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }

    /// <summary>Máximo esperado de <paramref name="n"/> normales estándar independientes (lo que da la suerte sola al probar n cosas).</summary>
    public static double ExpectedMaxZ(int n)
    {
        if (n <= 1) return 0;
        return (1 - EulerGamma) * NormalInv(1 - 1.0 / n) + EulerGamma * NormalInv(1 - 1.0 / (n * Math.E));
    }

    /// <summary>Sharpe por operación (media / desvío del resultado neto), asimetría, curtosis (normal = 3) y cantidad.</summary>
    public static (double Sharpe, double Skew, double Kurtosis, int Count) TradeSharpe(IReadOnlyList<ClosedTrade> trades)
    {
        int n = trades.Count;
        if (n < 2) return (0, 0, 3, n);
        double mean = trades.Average(t => (double)t.NetPnl);
        double m2 = 0, m3 = 0, m4 = 0;
        foreach (var t in trades)
        {
            double d = (double)t.NetPnl - mean;
            m2 += d * d;
            m3 += d * d * d;
            m4 += d * d * d * d;
        }

        m2 /= n;
        m3 /= n;
        m4 /= n;
        if (m2 <= 0) return (0, 0, 3, n);
        double sd = Math.Sqrt(m2);
        return (mean / sd, m3 / (sd * sd * sd), m4 / (m2 * m2), n);
    }

    /// <summary>Probabilidad de que el Sharpe verdadero supere <paramref name="benchmark"/> dado lo observado (PSR).</summary>
    public static double ProbabilisticSharpe(double sharpe, double benchmark, int count, double skew, double kurtosis)
    {
        if (count < 2) return 0;
        double denom = 1 - skew * sharpe + (kurtosis - 1) / 4 * sharpe * sharpe;
        if (denom <= 1e-9) denom = 1e-9;
        return NormalCdf((sharpe - benchmark) * Math.Sqrt(count - 1) / Math.Sqrt(denom));
    }

    /// <summary>
    /// Sharpe deflactado: compara contra el mejor Sharpe que se esperaría por pura suerte después de probar
    /// <paramref name="trials"/> configuraciones cuyos Sharpe tienen varianza <paramref name="trialVariance"/>.
    /// La varianza nunca se toma menor que la del ruido de estimar un Sharpe con esa muestra (1/n).
    /// </summary>
    public static double DeflatedSharpe(double sharpe, int count, double skew, double kurtosis, int trials, double trialVariance)
    {
        if (count < 2) return 0;
        return ProbabilisticSharpe(sharpe, DeflatedBenchmark(count, trials, trialVariance), count, skew, kurtosis);
    }

    /// <summary>
    /// La vara del Sharpe deflactado: el Sharpe por operación que mostraría por puro azar la mejor de
    /// <paramref name="trials"/> estrategias sin ninguna ventaja, con <paramref name="count"/> operaciones cada una.
    /// </summary>
    public static double DeflatedBenchmark(int count, int trials, double trialVariance) =>
        count < 2 ? 0 : Math.Sqrt(Math.Max(trialVariance, 1.0 / count)) * ExpectedMaxZ(Math.Max(1, trials));

    /// <summary>
    /// Desgaste de la ventaja: ¿rinde igual al final que al principio? Ganancia media por operación del primer y del último
    /// tercio (por orden de salida) y la t de la pendiente del resultado por operación en el tiempo (negativa = se apaga).
    /// </summary>
    public static (decimal Early, decimal Late, double T, int Count) EdgeDecay(IReadOnlyList<ClosedTrade> trades)
    {
        var list = trades.OrderBy(t => t.ExitTime).ToList();
        int n = list.Count;
        if (n < 6) return (0, 0, 0, n);
        int third = n / 3;
        decimal early = list.Take(third).Average(t => t.NetPnl);
        decimal late = list.Skip(n - third).Average(t => t.NetPnl);
        double mx = (n - 1) / 2.0, my = list.Average(t => (double)t.NetPnl);
        double sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            sxx += (i - mx) * (i - mx);
            sxy += (i - mx) * ((double)list[i].NetPnl - my);
        }

        double slope = sxx > 0 ? sxy / sxx : 0, sse = 0;
        for (int i = 0; i < n; i++)
        {
            double e = (double)list[i].NetPnl - (my + slope * (i - mx));
            sse += e * e;
        }

        double se = sxx > 0 ? Math.Sqrt(sse / (n - 2) / sxx) : 0;
        return (early, late, se > 0 ? slope / se : 0, n);
    }

    /// <summary>Eficiencia de la tendencia hasta la que se considera rango, y desde la que se considera tendencia.</summary>
    public const double RangeTrend = MarketContextTracker.RangeLimit, TrendingTrend = MarketContextTracker.TrendLimit;

    /// <summary>Percentiles de volatilidad (del último mes) que se consideran baja y alta.</summary>
    public const double LowVolatilityRank = MarketContextTracker.LowVolatility, HighVolatilityRank = MarketContextTracker.HighVolatility;

    /// <summary>
    /// Resultado de las operaciones según el mercado en que entraron: en rango, mixto o en tendencia (eficiencia de la
    /// tendencia de las últimas 24 horas; la tendencia, además, alcista o bajista) y con volatilidad baja o alta (el tercio de
    /// menos y el de más volatilidad). Vacío si las operaciones no traen el régimen (corridas de antes de que existiera).
    /// </summary>
    public static List<RegimeStats> Regimes(IReadOnlyList<ClosedTrade> trades)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        var list = new List<RegimeStats>();
        if (known.Count == 0) return list;
        static RegimeStats Of(string name, IEnumerable<ClosedTrade> group, bool detail = false)
        {
            var g = group.ToList();
            decimal win = g.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), loss = -g.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
            return new RegimeStats(name, g.Count, g.Sum(t => t.NetPnl), loss > 0 ? Math.Round(win / loss, 2) : win > 0 ? 999 : 0, detail);
        }

        list.Add(Of("range", known.Where(t => t.EntryTrend <= RangeTrend)));
        list.Add(Of("mixed", known.Where(t => t.EntryTrend > RangeTrend && t.EntryTrend < TrendingTrend)));
        var trending = known.Where(t => t.EntryTrend >= TrendingTrend).ToList();
        list.Add(Of("trend", trending));
        // la tendencia, separada en alcista y bajista (detalle: no cuenta aparte para la regla de regímenes)
        if (trending.Count > 0 && trending.Count(t => t.EntryMarketDirection != null) >= trending.Count * 0.8)
        {
            list.Add(Of("uptrend", trending.Where(t => t.EntryMarketDirection > 0), detail: true));
            list.Add(Of("downtrend", trending.Where(t => t.EntryMarketDirection < 0), detail: true));
        }
        // volatilidad: con el percentil del último mes (el mismo que usa el filtro de contexto); las corridas de antes,
        // por tercios de las propias operaciones
        if (known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8)
        {
            list.Add(Of("low volatility", known.Where(t => t.EntryVolatilityRank <= LowVolatilityRank)));
            list.Add(Of("high volatility", known.Where(t => t.EntryVolatilityRank >= HighVolatilityRank)));
        }
        else
        {
            var vols = known.Where(t => t.EntryVolatility != null).Select(t => t.EntryVolatility!.Value).OrderBy(v => v).ToList();
            if (vols.Count >= 9)
            {
                double low = vols[vols.Count / 3], high = vols[vols.Count * 2 / 3];
                list.Add(Of("low volatility", known.Where(t => t.EntryVolatility < low)));
                list.Add(Of("high volatility", known.Where(t => t.EntryVolatility >= high)));
            }
        }

        return list;
    }

    /// <summary>
    /// ¿Gana solo en ciertos contextos? Prueba los filtros de régimen (solo rango, sin tendencia, solo tendencia, sin rango,
    /// solo tendencia alcista o bajista) y de volatilidad (percentil del último mes) sobre las operaciones fuera de muestra y se queda con el que más mejora:
    /// lo que deja tiene que ganar con PF ≥ 1,5 y al menos 20 operaciones (y el 30 % de todas), y lo que saca tiene que
    /// perder. null si no hay un filtro así (o las operaciones no traen el contexto).
    /// </summary>
    public static ContextFilterSuggestion? SuggestContextFilter(IReadOnlyList<ClosedTrade> trades)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        if (known.Count < 30) return null;
        bool ranks = known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8;
        bool directions = known.Count(t => t.EntryMarketDirection != null) >= known.Count * 0.8;
        var regimes = new List<RegimeFilter> { RegimeFilter.All, RegimeFilter.RangeOnly, RegimeFilter.NoTrend, RegimeFilter.TrendOnly, RegimeFilter.NoRange };
        if (directions) regimes.AddRange(new[] { RegimeFilter.UpTrendOnly, RegimeFilter.DownTrendOnly });
        var vols = ranks ? new[] { (0, 100), (0, 33), (0, 66), (34, 100), (67, 100) } : new[] { (0, 100) };

        static (decimal Net, decimal Pf, double T) Stats(List<ClosedTrade> g)
        {
            decimal win = g.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), loss = -g.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
            double mean = g.Count > 0 ? g.Average(t => (double)t.NetPnl) : 0;
            double sd = g.Count > 1 ? Math.Sqrt(g.Sum(t => Math.Pow((double)t.NetPnl - mean, 2)) / (g.Count - 1)) : 0;
            return (g.Sum(t => t.NetPnl), loss > 0 ? win / loss : win > 0 ? 999 : 0, sd > 0 ? mean / sd * Math.Sqrt(g.Count) : 0);
        }

        var all = Stats(known);
        int minKept = Math.Max(20, (int)Math.Ceiling(known.Count * 0.3));
        ContextFilterSuggestion? best = null;
        double bestT = all.T;
        foreach (var regime in regimes)
        foreach (var (lo, hi) in vols)
        {
            if (regime == RegimeFilter.All && lo == 0 && hi == 100) continue;
            var kept = known.Where(t => InRegime(t, regime) && (!ranks || (t.EntryVolatilityRank is { } r && r >= lo && r <= hi))).ToList();
            if (kept.Count < minKept || kept.Count == known.Count) continue;
            var k = Stats(kept);
            var dropped = known.Except(kept).ToList();
            decimal droppedNet = dropped.Sum(t => t.NetPnl);
            if (k.Net <= 0 || k.Pf < 1.5m || droppedNet >= 0 || k.Pf < Math.Min(all.Pf, 99) + 0.3m || k.T <= bestT) continue;
            bestT = k.T;
            best = new ContextFilterSuggestion(regime, lo, hi, DescribeFilter(regime, lo, hi), kept.Count, k.Net, Math.Round(Math.Min(k.Pf, 999), 2), dropped.Count, droppedNet);
        }

        return best;
    }

    /// <summary>¿Entró en la condición de mercado que pide el filtro de régimen? (con el contexto guardado en la operación)</summary>
    private static bool InRegime(ClosedTrade t, RegimeFilter f) =>
        f == RegimeFilter.All || (MarketContextTracker.SetOf(f) & MarketContextTracker.SetOf(t.EntryTrend!.Value, t.EntryMarketDirection)) != 0;

    /// <summary>
    /// Qué deja y qué saca un filtro de contexto de unas operaciones (las que traen el contexto guardado): cuántas y con
    /// qué neto. La volatilidad solo se mira si las operaciones traen su percentil.
    /// </summary>
    public static (int Kept, decimal KeptNet, int Dropped, decimal DroppedNet) ApplyFilter(IReadOnlyList<ClosedTrade> trades, RegimeFilter regime, int minRank, int maxRank)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        bool ranks = known.Count > 0 && known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8;
        var kept = known.Where(t => InRegime(t, regime) && (!ranks || (t.EntryVolatilityRank is { } r && r >= minRank && r <= maxRank))).ToList();
        var dropped = known.Except(kept).ToList();
        return (kept.Count, kept.Sum(t => t.NetPnl), dropped.Count, dropped.Sum(t => t.NetPnl));
    }

    /// <summary>
    /// En palabras, las condiciones en que opera: «solo en rango», «rango o tendencia alcista», «todo menos tendencia
    /// bajista»… null si son todas (sin filtro).
    /// </summary>
    public static string? DescribeRegimes(RegimeSet set)
    {
        if (set is RegimeSet.All or RegimeSet.None) return null;
        if (set == (RegimeSet.UpTrend | RegimeSet.DownTrend)) return "trend only";
        if (set == (RegimeSet.Range | RegimeSet.Mixed)) return "no trend (range or mixed)";
        if (set == (RegimeSet.Mixed | RegimeSet.UpTrend | RegimeSet.DownTrend)) return "no range (mixed or trend)";
        static string Name(RegimeSet one) => one switch
        {
            RegimeSet.Range => "range",
            RegimeSet.Mixed => "mixed",
            RegimeSet.UpTrend => "uptrend",
            _ => "downtrend",
        };
        var on = new[] { RegimeSet.Range, RegimeSet.Mixed, RegimeSet.UpTrend, RegimeSet.DownTrend }.Where(x => set.HasFlag(x)).ToList();
        if (on.Count == 1) return "only in " + Name(on[0]);
        if (on.Count == 3) return "all except " + Name(RegimeSet.All & ~set);
        return string.Join(" or ", on.Select(Name));
    }

    /// <summary>«solo en rango, volatilidad hasta el percentil 66».</summary>
    public static string DescribeFilter(RegimeFilter regime, int minRank, int maxRank)
    {
        var parts = new List<string>();
        if (DescribeRegimes(MarketContextTracker.SetOf(regime)) is { } r) parts.Add(r);
        if (minRank > 0 && maxRank < 100) parts.Add($"volatility between percentiles {minRank} and {maxRank}");
        else if (maxRank < 100) parts.Add(maxRank <= 33 ? "low volatility" : $"volatility up to percentile {maxRank}");
        else if (minRank > 0) parts.Add(minRank >= 67 ? "high volatility" : $"volatility from percentile {minRank}");
        return string.Join(", ", parts);
    }

    /// <summary>Número de un valor de parámetro (los cargados de JSON vienen como JsonElement).</summary>
    public static double? ToDouble(object? v) => v switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.String } je when double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        JsonElement => null,
        bool => null,
        IConvertible c when double.TryParse(Convert.ToString(c, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>
    /// Inestabilidad de los parámetros que eligió cada ventana del walk-forward: desvío de los valores elegidos sobre el
    /// ancho del rango probado, promediado entre parámetros (0 = siempre el mismo; más de ~0.35 = saltan por todo el rango).
    /// </summary>
    public static double? ParameterInstability(IReadOnlyList<WalkForwardWindow> windows, IReadOnlyList<ParameterRange>? ranges)
    {
        if (windows.Count < 2) return null;
        var names = ranges is { Count: > 0 }
            ? ranges.Where(r => r.Enabled && r.Max > r.Min).Select(r => r.Name).ToList()
            : windows.SelectMany(w => w.BestParameters.Keys).Distinct().ToList();
        var scores = new List<double>();
        foreach (var name in names)
        {
            var range = ranges?.FirstOrDefault(r => r.Name == name);
            var values = windows.Select(w => w.BestParameters.TryGetValue(name, out var v) ? range?.Numeric(v) ?? ToDouble(v) : null).Where(v => v != null).Select(v => v!.Value).ToList();
            if (values.Count < 2) continue;
            double mean = values.Average();
            double sd = Math.Sqrt(values.Sum(x => (x - mean) * (x - mean)) / values.Count);
            double width = range != null ? range.Max - range.Min : 0;
            if (width > 0) scores.Add(sd / width);
            else if (Math.Abs(mean) > 1e-12) scores.Add(Math.Min(1, sd / Math.Abs(mean)));
        }

        return scores.Count == 0 ? 0 : scores.Average();
    }

    /// <summary>
    /// Meseta alrededor de la mejor combinación: de las vecinas (un paso de distancia en cada parámetro), qué parte gana
    /// plata. Un pico aislado (las vecinas pierden) suele ser sobreajuste.
    /// </summary>
    public static (double ProfitableShare, int Neighbors)? Plateau(OptimizationResult opt)
    {
        if (opt.Rows.Count < 3 || opt.Best is not { } best) return null;
        var ranges = opt.Ranges.Where(r => r.Enabled && r.Step > 0 && r.Max > r.Min).ToList();
        if (ranges.Count == 0) return null;
        int neighbors = 0, profitable = 0;
        foreach (var row in opt.Rows.Skip(1))
        {
            bool near = true, same = true;
            foreach (var r in ranges)
            {
                double? a = best.Parameters.TryGetValue(r.Name, out var av) ? r.Numeric(av) : null;
                double? b = row.Parameters.TryGetValue(r.Name, out var bv) ? r.Numeric(bv) : null;
                if (a == null || b == null) continue;
                double diff = Math.Abs(a.Value - b.Value);
                if (diff > r.Step * 1.0001) near = false;
                if (diff > 1e-9) same = false;
            }

            if (!near || same) continue;
            neighbors++;
            if (row.Metrics.NetProfit > 0) profitable++;
        }

        return neighbors == 0 ? null : ((double)profitable / neighbors, neighbors);
    }

    /// <summary>Parte de las combinaciones probadas que ganan plata (robustez general de la idea).</summary>
    public static double? ProfitableShare(OptimizationResult opt) =>
        opt.Rows.Count == 0 ? null : (double)opt.Rows.Count(r => r.Metrics.NetProfit > 0) / opt.Rows.Count;
}

/// <summary>Resultado de una prueba de la validación.</summary>
public enum GateStatus
{
    Pass,
    Warn,
    Fail,
}

/// <summary>Una prueba de la validación con su explicación.</summary>
public sealed record GateCheck(string Name, GateStatus Status, string Text);

/// <summary>Lo que se le pide a una estrategia validada con walk-forward.</summary>
public sealed class GateInput
{
    public required WalkForwardResult WalkForward { get; init; }
    public OptimizationResult? Optimization { get; init; }

    /// <summary>Configuraciones (estrategia × mercado) que ya se probaron, contando esta.</summary>
    public int Trials { get; init; } = 1;

    /// <summary>Varianza de los Sharpe por operación de esas configuraciones.</summary>
    public double TrialVariance { get; init; }

    public int MinTrades { get; init; } = 30;
}

/// <summary>Resultado de las operaciones que entraron en un régimen de mercado.</summary>
/// <summary>Resultado en una condición de mercado. Detail: subgrupo de otra (tendencia alcista dentro de tendencia).</summary>
public sealed record RegimeStats(string Name, int Trades, decimal Net, decimal ProfitFactor, bool Detail = false);

/// <summary>
/// Filtro de contexto que convendría: en ese contexto la estrategia gana con claridad y fuera de él pierde. Son los
/// valores para sus parámetros RegimeFilter, MinVolatilityRank y MaxVolatilityRank.
/// </summary>
public sealed record ContextFilterSuggestion(RegimeFilter Regime, int MinVolatilityRank, int MaxVolatilityRank, string Description,
    int Trades, decimal Net, decimal ProfitFactor, int DroppedTrades, decimal DroppedNet);

/// <summary>Veredicto de la validación con todas las pruebas.</summary>
public sealed class GateResult
{
    public GateStatus Status { get; init; }
    public List<GateCheck> Checks { get; init; } = new();
    public double TradeSharpe { get; init; }
    public double Deflated { get; init; }
    public double? Instability { get; init; }
    public (double ProfitableShare, int Neighbors)? Plateau { get; init; }
    public double? ProfitableCombos { get; init; }

    /// <summary>
    /// No pasa por falta de muestra, no por falta de ventaja: gana fuera de muestra y lo único que falla depende de cuántas
    /// operaciones hay (la muestra, el Sharpe deflactado y, con pocas operaciones, la consistencia y la eficiencia). Con la
    /// misma ventaja por operación pasaría con más operaciones: antes de descartarla hay que juntar más historia.
    /// </summary>
    public bool NeedsMoreSample { get; init; }

    /// <summary>Cuántas veces más operaciones fuera de muestra harían falta para concluir (con la misma ventaja por operación).</summary>
    public double SampleFactor { get; init; } = 1;

    /// <summary>Resultado fuera de muestra por régimen de mercado (rango, mixto, tendencia, volatilidad baja y alta).</summary>
    public List<RegimeStats> Regimes { get; init; } = new();

    /// <summary>Desgaste: ganancia media por operación del primer y último tercio, y la t de la pendiente en el tiempo.</summary>
    public decimal DecayEarly { get; init; }
    public decimal DecayLate { get; init; }
    public double DecayT { get; init; }

    public string StatusText => Status switch
    {
        GateStatus.Pass => "pass",
        GateStatus.Warn => "uncertain",
        _ when NeedsMoreSample => "insufficient sample",
        _ => "fail",
    };
}

/// <summary>
/// Reglas fijas contra la sobreoptimización (no las decide un agente): se juzga solo lo de fuera de muestra, con muestra
/// suficiente, eficiencia del walk-forward, consistencia entre ventanas, estabilidad de parámetros, meseta y Sharpe
/// deflactado por la cantidad de cosas que se probaron.
/// </summary>
public static class ValidationGate
{
    public const double MinProfitFactor = 1.2, FailProfitFactor = 1.0;
    public const double MinEfficiency = 0.5, FailEfficiency = 0.3;
    public const double MinWindowsPercent = 60, FailWindowsPercent = 40;
    public const int MinWindows = 4;
    public const double MaxInstability = 0.35;
    public const double MinPlateau = 0.5, MinProfitableCombos = 0.25;
    public const double MinDeflated = 0.9, FailDeflated = 0.5;

    /// <summary>Cómo empieza la línea del Sharpe deflactado (las de antes decían «Probabilidad de que no sea suerte»).</summary>
    public const string LuckPassPrefix = "Probability that the edge is real rather than luck:", LuckFailPrefix = "May be luck:";

    /// <summary>¿Es la línea del Sharpe deflactado? (la de ahora o la de las pruebas guardadas antes)</summary>
    public static bool IsLuckLine(string text) =>
        text.StartsWith(LuckFailPrefix, StringComparison.Ordinal) || text.StartsWith(LuckPassPrefix, StringComparison.Ordinal) ||
        text.StartsWith("Probabilidad de que no sea suerte", StringComparison.Ordinal) ||
        text.StartsWith("Probabilidad de que la ventaja sea real y no suerte:", StringComparison.Ordinal) ||
        text.StartsWith("Puede ser suerte:", StringComparison.Ordinal);

    /// <summary>Hasta cuántas veces más operaciones se considera ir a buscar para que el Sharpe deflactado pase.</summary>
    public const double MaxSampleFactor = 8;

    /// <summary>Pruebas que dependen del tamaño de la muestra (con pocas operaciones, y con suficientes).</summary>
    private static readonly HashSet<string> SmallSampleChecks = new() { "Sample", "Deflated Sharpe", "Consistency", "Efficiency", "Windows", "Overfitting", "Paths" };

    /// <summary>Sobreajuste (PBO): la elegida pierde fuera de muestra en menos del 20 % de las particiones (pasa) o en la mitad o más (no pasa).</summary>
    public const double MaxPboLoss = 0.2, FailPboLoss = 0.5;

    /// <summary>Caminos de la CPCV que tienen que ganar (y la mediana tiene que ser positiva).</summary>
    public const double MinProfitablePaths = 0.7;
    private static readonly HashSet<string> LargeSampleChecks = new() { "Deflated Sharpe", "Windows" };

    public static GateResult Evaluate(GateInput input)
    {
        var wf = input.WalkForward;
        var m = wf.OutSampleMetrics;
        var checks = new List<GateCheck>();
        void Add(string name, GateStatus st, string text) => checks.Add(new GateCheck(name, st, text));
        var es = CultureInfo.GetCultureInfo("en-US");

        int trades = m.TotalTrades;
        Add("Sample", trades >= input.MinTrades ? GateStatus.Pass : GateStatus.Fail,
            trades >= input.MinTrades ? $"{trades} out-of-sample trades"
            : trades == 0 ? "No out-of-sample trades (0 trades): entry conditions did not occur during the test period"
            : $"Only {trades} out-of-sample trades (minimum {input.MinTrades}): insufficient evidence for a conclusion");
        Add("PnL", m.NetProfit > 0 ? GateStatus.Pass : GateStatus.Fail,
            m.NetProfit > 0 ? $"Earns {m.NetProfit.ToString("N2", es)} out of sample" : $"Loses out of sample ({m.NetProfit.ToString("N2", es)})");
        decimal pf = m.ProfitFactor;
        Add("Profit factor", pf >= (decimal)MinProfitFactor ? GateStatus.Pass : pf >= (decimal)FailProfitFactor ? GateStatus.Warn : GateStatus.Fail,
            $"Out-of-sample PF {(pf >= 999 ? "∞" : pf.ToString("0.00", es))} (requires ≥ {MinProfitFactor.ToString("0.0", es)})");
        double wfe = wf.WalkForwardEfficiency;
        Add("Efficiency", wfe >= MinEfficiency ? GateStatus.Pass : wfe >= FailEfficiency ? GateStatus.Warn : GateStatus.Fail,
            wfe >= MinEfficiency ? $"Walk-forward efficiency {wfe.ToString("0.00", es)}: out-of-sample performance is similar to optimization"
            : wfe >= FailEfficiency ? $"Walk-forward efficiency {wfe.ToString("0.00", es)}: substantial degradation out of sample (requires ≥ {MinEfficiency.ToString("0.0", es)})"
            : $"Walk-forward efficiency {wfe.ToString("0.00", es)}: overfit (optimized performance does not repeat out of sample)");
        double win = wf.ProfitableWindowsPercent;
        Add("Consistency", win >= MinWindowsPercent ? GateStatus.Pass : win >= FailWindowsPercent ? GateStatus.Warn : GateStatus.Fail,
            $"{win.ToString("0", es)}% of out-of-sample windows are profitable (requires ≥ {MinWindowsPercent}%)");
        Add("Windows", wf.Windows.Count >= MinWindows ? GateStatus.Pass : GateStatus.Warn,
            wf.Windows.Count >= MinWindows ? $"{wf.Windows.Count} walk-forward windows" : $"Only {wf.Windows.Count} windows: little evidence of repeatability over time");

        double? instability = Robustness.ParameterInstability(wf.Windows, wf.Ranges);
        if (instability is { } inst)
            Add("Stability", inst <= MaxInstability ? GateStatus.Pass : GateStatus.Warn,
                inst <= MaxInstability ? $"Selected parameters are stable across windows (dispersion {inst.ToString("0.00", es)})"
                : $"Selected parameters vary across the entire range between windows (dispersion {inst.ToString("0.00", es)}): the edge depends on fine-tuning them");

        (double, int)? plateau = null;
        double? profitableCombos = null;
        if (input.Optimization is { } opt)
        {
            plateau = Robustness.Plateau(opt);
            if (plateau is { } p)
                Add("Plateau", p.Item1 >= MinPlateau ? GateStatus.Pass : GateStatus.Warn,
                    p.Item1 >= MinPlateau ? $"{p.Item1 * 100:0}% of combinations neighboring the best are also profitable (plateau)"
                    : $"Only {p.Item1 * 100:0}% of neighbors of the best combination are profitable: an isolated peak, typical of overfitting");
            profitableCombos = Robustness.ProfitableShare(opt);
            if (profitableCombos is { } pc)
                Add("Robustness", pc >= MinProfitableCombos ? GateStatus.Pass : GateStatus.Warn,
                    $"{pc * 100:0}% of all tested combinations are profitable");
        }

        // sobreajuste (Bailey, Borwein, López de Prado y Zhu): con la mitad de la historia se elige la mejor combinación y se
        // mira la otra mitad, de todas las formas posibles. Lo que decide es si la elegida pierde plata en la otra mitad; la
        // PBO (si queda en la mitad de abajo de las demás) se informa: alta con todas ganando es una meseta, no un problema
        if (wf.Pbo is { } pbo && wf.PboLoss is { } pboLoss && wf.PboSplits > 0)
            Add("Overfitting", pboLoss < MaxPboLoss ? GateStatus.Pass : pboLoss < FailPboLoss ? GateStatus.Warn : GateStatus.Fail,
                (pboLoss < MaxPboLoss
                    ? $"Optimization is not selecting luck: the best combination on half the history was profitable on the other half in {(1 - pboLoss) * 100:0}% of the {wf.PboSplits} partitions"
                    : pboLoss < FailPboLoss
                        ? $"Some overfitting: the best combination on half the history lost money on the other half in {pboLoss * 100:0}% of the {wf.PboSplits} partitions (requires less than {MaxPboLoss * 100:0}%)"
                        : $"Overfit: the best combination on half the history lost money on the other half in {pboLoss * 100:0}% of the {wf.PboSplits} partitions; selecting the best is selecting luck") +
                $" (among the {wf.Evaluated} tested combinations, it ranked in the bottom half in {pbo * 100:0}%: PBO)");

        // caminos fuera de muestra de la validación cruzada combinatoria purgada: no un solo camino como el walk-forward
        if (wf.CpcvPaths.Count >= 3)
        {
            var paths = wf.CpcvPaths.OrderBy(p => p.Net).ToList();
            int good = paths.Count(p => p.Net > 0);
            decimal median = paths[paths.Count / 2].Net, worst = paths[0].Net;
            double share = (double)good / paths.Count;
            Add("Paths", median <= 0 ? GateStatus.Fail : share >= MinProfitablePaths ? GateStatus.Pass : GateStatus.Warn,
                $"{good} of {paths.Count} out-of-sample paths are profitable (cross-validation with {wf.CpcvGroups} groups and {wf.EmbargoDays} embargo day(s)): " +
                $"median {median.ToString("N0", es)}, worst {worst.ToString("N0", es)}" +
                (median <= 0 ? ". With a different historical split, it loses money" : share >= MinProfitablePaths ? "" : $" (requires at least this percentage to be profitable: {MinProfitablePaths * 100:0}%)"));
        }

        var (sr, skew, kurt, n) = Robustness.TradeSharpe(wf.OutSampleTrades);
        double dsr = Robustness.DeflatedSharpe(sr, n, skew, kurt, input.Trials, input.TrialVariance);
        double bar = Robustness.DeflatedBenchmark(n, input.Trials, input.TrialVariance);
        // en plata, para que se entienda: cuánto gana por operación, cuánto varía cada una y cuánto mostraría por pura
        // suerte la mejor de las estrategias sin ventaja que se probaron, con esa misma variación
        var pnls = wf.OutSampleTrades.Select(t => (double)t.NetPnl).ToList();
        double mean = pnls.Count > 0 ? pnls.Average() : 0;
        double sd = pnls.Count > 1 ? Math.Sqrt(pnls.Sum(x => (x - mean) * (x - mean)) / (pnls.Count - 1)) : 0;
        string money = n >= 2 && sd > 0
            ? $". Average profit {mean.ToString("N0", es)} per trade, but individual trades vary by about ±{sd.ToString("N0", es)}: among {input.Trials} strategies with no edge " +
              $"(the number tested), the luckiest typically shows ~{(bar * sd).ToString("N0", es)} per trade with that variability"
            : "";
        Add("Deflated Sharpe", dsr >= MinDeflated ? GateStatus.Pass : dsr >= FailDeflated ? GateStatus.Warn : GateStatus.Fail,
            dsr >= MinDeflated
                ? $"{LuckPassPrefix} {dsr * 100:0}% (requires ≥ {MinDeflated * 100:0}%, accounting for the {input.Trials} tested strategies){money}"
                : $"{LuckFailPrefix} only {dsr * 100:0}% probability that the edge is real (requires ≥ {MinDeflated * 100:0}%){money}" +
                  (dsr < FailDeflated ? ". This is indistinguishable from chance" : ""));

        // desgaste de la ventaja: con 30 operaciones o más, ¿el último tercio rinde como el primero?
        var (early, late, tDecay, _) = Robustness.EdgeDecay(wf.OutSampleTrades);
        if (trades >= 30)
        {
            bool dying = early > 0 && late < 0 && tDecay <= -2;
            bool fading = (early > 0 && late < early * 0.5m) || tDecay <= -1.5;
            Add("Edge decay", dying ? GateStatus.Fail : fading ? GateStatus.Warn : GateStatus.Pass,
                dying ? $"The edge is fading: the last third of trades loses ({late.ToString("N2", es)} per trade), whereas the first third earned {early.ToString("N2", es)} (adverse trend, t {tDecay.ToString("0.0", es)})"
                : fading ? $"The edge weakens over time: the last third of trades earns {late.ToString("N2", es)} per trade versus {early.ToString("N2", es)} in the first third"
                : $"The edge holds up over time: the last third of trades earns {late.ToString("N2", es)} per trade (first third {early.ToString("N2", es)})");
        }

        // regímenes de mercado: cómo le va en rango, en tendencia y con volatilidad baja o alta (grupos con muestra)
        var regimes = Robustness.Regimes(wf.OutSampleTrades);
        int minGroup = Math.Max(8, trades * 15 / 100);
        var judged = regimes.Where(r => !r.Detail && r.Trades >= minGroup).ToList();
        if (judged.Count >= 2)
        {
            string Line(RegimeStats r) => $"{r.Name} ({r.Trades} op, PF {(r.ProfitFactor >= 999 ? "∞" : r.ProfitFactor.ToString("0.00", es))})";
            var losing = judged.Where(r => r.Net < 0).ToList();
            var winning = judged.Except(losing).ToList();
            Add("Regimes", losing.Count == 0 ? GateStatus.Pass : GateStatus.Warn,
                losing.Count == 0 ? $"Profitable in every market condition with sufficient data: {string.Join(", ", judged.Select(Line))}"
                : winning.Count == 0 ? $"Loses in every market condition: {string.Join(", ", losing.Select(Line))}"
                : $"Regime-dependent: loses in {string.Join(", ", losing.Select(Line))} and earns in {string.Join(", ", winning.Select(Line))}. Consider a regime filter");
        }

        var status = checks.Any(c => c.Status == GateStatus.Fail) ? GateStatus.Fail : checks.Any(c => c.Status == GateStatus.Warn) ? GateStatus.Warn : GateStatus.Pass;
        var (needs, factor) = status == GateStatus.Pass ? (false, 1.0)
            : SampleNeed(checks.Select(c => (c.Name, c.Status)), m.NetProfit, trades, input.MinTrades, sr, skew, kurt, input.Trials, input.TrialVariance, dsr);
        return new GateResult
        {
            Status = status,
            Checks = checks,
            TradeSharpe = sr,
            Deflated = dsr,
            Instability = instability,
            Plateau = plateau,
            ProfitableCombos = profitableCombos,
            NeedsMoreSample = needs,
            SampleFactor = factor,
            Regimes = regimes,
            DecayEarly = early,
            DecayLate = late,
            DecayT = tDecay,
        };
    }

    /// <summary>
    /// ¿Le falta muestra o le falta ventaja? Solo si gana fuera de muestra y todo lo que falla depende del tamaño de la
    /// muestra. Con 10 operaciones o más se proyecta el Sharpe deflactado con la misma ventaja por operación: si ni con
    /// <see cref="MaxSampleFactor"/> veces más operaciones pasaría, no es la muestra, es que no se distingue del azar.
    /// Con menos de 10 el Sharpe no dice nada todavía: alcanza con que gane.
    /// </summary>
    public static (bool Needs, double Factor) SampleNeed(IEnumerable<(string Name, GateStatus Status)> checks, decimal net, int trades, int minTrades,
                                                         double sr, double skew, double kurt, int trials, double trialVariance, double dsr)
    {
        if (net <= 0 || trades <= 0 || (trades >= minTrades && dsr >= MinDeflated)) return (false, 1);
        var bound = trades < minTrades ? SmallSampleChecks : LargeSampleChecks;
        if (checks.Any(c => c.Status == GateStatus.Fail && !bound.Contains(c.Name))) return (false, 1);
        double factor = Math.Max(1, (double)minTrades / trades);
        if (trades < 10 || sr <= 0) return (true, Math.Round(factor, 2));
        double f = factor;
        while (Robustness.DeflatedSharpe(sr, (int)Math.Ceiling(trades * f), skew, kurt, trials, trialVariance) < MinDeflated)
        {
            f *= 1.15;
            if (f > MaxSampleFactor * factor) return (false, 1);
        }

        return (true, Math.Round(f, 2));
    }
}
