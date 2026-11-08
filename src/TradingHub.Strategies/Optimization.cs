using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using TradingHub.Data;

namespace TradingHub.Strategies;

/// <summary>
/// Rango de un parámetro a optimizar. Además de los de la estrategia, puede ser el período de las velas
/// (<see cref="PeriodName"/>): no cambia la estrategia sino cómo se arman sus velas, y se recorre como uno más.
/// </summary>
public sealed class ParameterRange
{
    /// <summary>Nombre del «parámetro» del período de las velas.</summary>
    public const string PeriodName = "__Periodo";

    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; } = 1;
    public bool IsInteger { get; set; }

    /// <summary>Para el período de las velas: los períodos a probar (códigos como M15 o H1), del más corto al más largo.</summary>
    public List<string> Choices { get; set; } = new();

    public bool IsPeriod => Name == PeriodName;

    public ParameterRange Clone() =>
        new() { Name = Name, Label = Label, Enabled = Enabled, Min = Min, Max = Max, Step = Step, IsInteger = IsInteger, Choices = Choices.ToList() };

    /// <summary>El período de las velas como un parámetro más: se prueba cada uno de <paramref name="periods"/>.</summary>
    public static ParameterRange Periods(IEnumerable<string> periods, bool enabled = true)
    {
        var r = new ParameterRange { Name = PeriodName, Label = "Period", Enabled = enabled, IsInteger = true };
        r.SetChoices(periods);
        return r;
    }

    /// <summary>Los períodos a probar: solo los válidos, sin repetir y del más corto al más largo (vecinos para la meseta).</summary>
    public void SetChoices(IEnumerable<string> periods)
    {
        Choices = periods.Select(p => ChartPeriod.TryParse(p, out var cp) ? cp : (ChartPeriod?)null).OfType<ChartPeriod>().Distinct()
            .OrderBy(p => p.IsTimeBased ? 0 : 1).ThenBy(p => p.ApproxDuration).ThenBy(p => p.Type).ThenBy(p => p.Value)
            .Select(p => p.Code).ToList();
        Min = 0;
        Max = Math.Max(0, Choices.Count - 1);
        Step = 1;
    }

    /// <summary>El valor de una combinación como número (un período, por su lugar en la lista).</summary>
    public double? Numeric(object? value)
    {
        if (Choices.Count == 0) return Robustness.ToDouble(value);
        int i = value == null ? -1 : Choices.IndexOf(value.ToString()!);
        return i >= 0 ? i : null;
    }

    public IEnumerable<double> Values()
    {
        if (!Enabled || Step <= 0 || Max < Min)
        {
            yield return Min;
            yield break;
        }

        int n = (int)Math.Floor((Max - Min) / Step + 1e-9);
        for (int i = 0; i <= n; i++) yield return Math.Round(Min + i * Step, 8);
    }

    public int Count => !Enabled || Step <= 0 || Max < Min ? 1 : (int)Math.Floor((Max - Min) / Step + 1e-9) + 1;

    /// <summary>Rangos sugeridos a partir de [Optimize] (o alrededor del valor actual).</summary>
    public static List<ParameterRange> FromStrategy(Type t, Strategy current)
    {
        var list = new List<ParameterRange>();
        foreach (var p in Strategy.Parameters(t))
        {
            var type = p.PropertyType;
            bool numeric = type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal);
            if (!numeric) continue;
            var opt = p.GetCustomAttribute<OptimizeAttribute>();
            double value = Convert.ToDouble(p.GetValue(current), CultureInfo.InvariantCulture);
            bool integer = type == typeof(int) || type == typeof(long);
            list.Add(new ParameterRange
            {
                Name = p.Name,
                Label = Strategy.ParameterName(p),
                Enabled = opt != null,
                Min = opt?.Min ?? value,
                Max = opt?.Max ?? value,
                Step = opt?.Step ?? (integer ? 1 : Math.Max(Math.Abs(value) / 10, 0.01)),
                IsInteger = integer,
            });
        }

        return list;
    }
}

/// <summary>Una combinación probada en la optimización.</summary>
public sealed class OptimizationRow
{
    /// <summary>Su lugar en la grilla (para medir la meseta).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int[]? Index { get; set; }

    public Dictionary<string, object?> Parameters { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public double Fitness { get; set; }
}

/// <summary>Resultado de una optimización.</summary>
public sealed class OptimizationResult
{
    public FitnessMetric Metric { get; set; }
    public List<ParameterRange> Ranges { get; set; } = new();
    public List<OptimizationRow> Rows { get; set; } = new();
    public int Combinations { get; set; }
    public double Seconds { get; set; }
    public OptimizationRow? Best => Rows.Count > 0 ? Rows[0] : null;

    /// <summary>The selected plateau's index in the full measured population, or -1 when no eligible selection exists.</summary>
    public int RecommendedIndex { get; set; } = -1;
    public OptimizationRow? RecommendedRow { get; set; }
    public OptimizationPopulation? Population { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public OptimizationRow? Recommended => RecommendedRow ??
        ((Population != null || Rows.Count == Combinations) && RecommendedIndex >= 0 && RecommendedIndex < Rows.Count ? Rows[RecommendedIndex] : null);

    public OptimizationPopulation? GetPopulation() => Population ??
        (Rows.Count == Combinations ? OptimizationPopulation.Capture(Rows, Ranges) : null);

    public OptimizationResult WithCapturedPopulation() => Project(int.MaxValue, OptimizationPopulation.Capture(Rows, Ranges));

    internal OptimizationResult ForStorage(int maximumRows) => Project(maximumRows, GetPopulation());

    private OptimizationResult Project(int maximumRows, OptimizationPopulation? population) => new()
    {
        Metric = Metric,
        Ranges = Ranges,
        Rows = Rows.Count > maximumRows ? Rows.Take(maximumRows).ToList() : Rows,
        Combinations = Combinations,
        Seconds = Seconds,
        RecommendedIndex = RecommendedIndex,
        RecommendedRow = Recommended,
        Population = population,
    };
}

/// <summary>Una ventana del walk-forward.</summary>
public sealed class WalkForwardWindow
{
    public int Number { get; set; }
    public DateTime InSampleFrom { get; set; }
    public DateTime InSampleTo { get; set; }
    public DateTime OutSampleFrom { get; set; }
    public DateTime OutSampleTo { get; set; }
    public Dictionary<string, object?> BestParameters { get; set; } = new();
    public PerformanceMetrics? InSample { get; set; } = new();
    public PerformanceMetrics? OutSample { get; set; } = new();

    /// <summary>Rendimiento fuera de muestra / dentro de muestra (anualizado por días).</summary>
    public double? Efficiency { get; set; } = 0;
    public bool InSampleAvailable { get; set; } = true;
    public bool OutSampleAvailable { get; set; } = true;
    public string? UnavailableReason { get; set; }
    public decimal? InSampleOpeningEquity { get; set; }
    public decimal? InSampleClosingEquity { get; set; }
    /// <summary>Source configuration marks retain its original warm capital, not the stitched capital.</summary>
    public decimal? OutSampleOpeningEquity { get; set; }
    public decimal? OutSampleClosingEquity { get; set; }
    public decimal? OutSampleStitchedOpeningEquity { get; set; }
    public decimal? OutSampleStitchedClosingEquity { get; set; }
}

/// <summary>Resultado del walk-forward.</summary>
public sealed class WalkForwardResult
{
    public int InSampleDays { get; set; }
    public int OutSampleDays { get; set; }
    public bool Anchored { get; set; }
    public FitnessMetric Metric { get; set; }
    public string? AccountingPolicy { get; set; }
    public string? AttributionPolicy { get; set; }
    public string? CpcvPolicy { get; set; }
    public bool IsComplete { get; set; } = true;

    /// <summary>Rangos que se optimizaron en cada ventana (para medir la estabilidad de los parámetros elegidos).</summary>
    public List<ParameterRange> Ranges { get; set; } = new();
    public List<WalkForwardWindow> Windows { get; set; } = new();

    /// <summary>Closing-fill contributions and final boundary valuations, not independent completed trades.</summary>
    public List<ClosedTrade> OutSampleTrades { get; set; } = new();
    public List<EquityPoint> OutSampleEquity { get; set; } = new();
    public PerformanceMetrics OutSampleMetrics { get; set; } = new();
    public double WalkForwardEfficiency { get; set; }
    public double ProfitableWindowsPercent { get; set; }
    public double Seconds { get; set; }

    /// <summary>Cómo se buscaron los parámetros en cada ventana: "grilla" o "bayesiana".</summary>
    public string Search { get; set; } = "grilla";

    /// <summary>Distinct configurations, each executed once with warm state through the final availability cutoff.</summary>
    public int Evaluated { get; set; }
    public long SpaceSize { get; set; }

    /// <summary>Input compression ratio. Walk-forward does not coalesce events across its accounting cutoffs.</summary>
    public double Compression { get; set; } = 1;

    /// <summary>
    /// Probabilidad de sobreoptimización (PBO, validación cruzada combinatoriamente simétrica): en qué proporción de las
    /// particiones la mejor combinación de una mitad de la historia quedó por debajo de la mediana en la otra mitad. Loss:
    /// en qué proporción perdió plata en la otra mitad. null si no se pudo medir (menos de 4 combinaciones).
    /// </summary>
    public double? Pbo { get; set; }
    public double? PboLoss { get; set; }
    public int PboSplits { get; set; }

    /// <summary>Caminos fuera de muestra de la validación cruzada combinatoria purgada (vacío si no se pudo medir).</summary>
    public List<CpcvPath> CpcvPaths { get; set; } = new();
    public int CpcvGroups { get; set; }
    public int EmbargoDays { get; set; }
}

/// <summary>Resultado de Monte Carlo (reordenar operaciones al azar).</summary>
public sealed class MonteCarloResult
{
    public int Runs { get; set; }
    public decimal? DrawdownP50 { get; set; } = 0;
    public decimal? DrawdownP95 { get; set; } = 0;
    public decimal? DrawdownP99 { get; set; } = 0;
    public decimal? WorstDrawdown { get; set; } = 0;
    public decimal? NetProfit { get; set; } = 0;
    public double? RuinProbabilityPercent { get; set; } = 0;
    public List<List<decimal>> SampleCurves { get; set; } = new();
    public bool Available { get; set; } = true;
    public string? UnavailableReason { get; set; }
}

/// <summary>Optimización, walk-forward y Monte Carlo.</summary>
public static class Optimizer
{
    /// <summary>Total de combinaciones de una grilla.</summary>
    public static long Combinations(IEnumerable<ParameterRange> ranges) => ranges.Aggregate(1L, (acc, r) => acc * Math.Max(1, r.Count));

    /// <summary>Hasta cuántas combinaciones se recorre la grilla entera en el walk-forward (con más, búsqueda bayesiana).</summary>
    public const int GridLimit = 300;

    /// <summary>Combinaciones que mide la búsqueda bayesiana en cada ventana (si no se pide otra cosa).</summary>
    public const int DefaultBudget = 80;

    /// <summary>
    /// Se pueden juntar los ticks repetidos (<see cref="TickData.Compress"/>) sin cambiar el resultado: velas de tiempo o de
    /// rango y una estrategia que decide con las velas (no con cada tick).
    /// </summary>
    public static bool CanCompress(Type strategyType, string period) =>
        ChartPeriod.TryParse(period, out var p) && (p.IsTimeBased || p.Type == PeriodType.Range) && !Strategy.UsesTicks(strategyType);

    /// <summary>Se pueden juntar los ticks si se puede con todos los períodos que se prueban.</summary>
    private static bool CanCompress(Type strategyType, ParameterSpace space, BacktestSettings settings) =>
        (space.Periods.Count > 0 ? space.Periods : new[] { settings.Period }).All(p => CanCompress(strategyType, p));

    /// <summary>Los ajustes de una combinación: con su período si el período se optimiza (si no, los mismos).</summary>
    public static BacktestSettings SettingsFor(BacktestSettings settings, IReadOnlyDictionary<string, object?> combo)
    {
        if (!combo.TryGetValue(ParameterRange.PeriodName, out var p) || p is not string period) return settings;
        var s = settings.Clone();
        s.Period = period;
        return s;
    }

    /// <summary>Prueba todas las combinaciones en paralelo, ordena por la métrica elegida y recomienda la mejor meseta.</summary>
    public static OptimizationResult Optimize(Func<Strategy> factory, Dictionary<string, object?> baseParameters, IReadOnlyList<ParameterRange> ranges,
        TickData data, BacktestSettings settings, FitnessMetric metric, int minTrades, IProgress<double>? progress, CancellationToken ct, int maxParallel = 0,
        IReadOnlyList<(DateTime Time, decimal Close)>? contextHistory = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var type = factory().GetType();
        var space = new ParameterSpace(ranges, type);
        if (!data.Compressed && CanCompress(type, space, settings)) data = data.Compress();
        var combos = space.All().ToList();
        var rows = new ConcurrentBag<OptimizationRow>();
        int done = 0;
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxParallel > 0 ? maxParallel : Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.ForEach(combos, options, idx =>
        {
            var combo = space.Config(idx);
            var s = factory();
            s.SetParameters(baseParameters);
            s.SetParameters(combo);
            var r = new BacktestEngine(s, data, SettingsFor(settings, combo)) { ContextHistory = contextHistory }.Run(ct);
            if (r.Ok)
            {
                double fitness = r.Metrics.TotalTrades < minTrades ? double.NegativeInfinity : r.Metrics.Fitness(metric);
                rows.Add(new OptimizationRow { Index = idx, Parameters = combo, Metrics = r.Metrics, Fitness = fitness });
            }

            progress?.Report(Interlocked.Increment(ref done) / (double)combos.Count);
        });

        var sorted = rows.OrderByDescending(r => r.Fitness).ThenByDescending(r => r.Metrics.NetProfit).ToList();
        return new OptimizationResult
        {
            Metric = metric,
            Ranges = ranges.ToList(),
            Rows = sorted,
            Combinations = combos.Count,
            Seconds = sw.Elapsed.TotalSeconds,
            RecommendedIndex = RobustSelection.Select(sorted.Select(r => (r.Index!, r.Fitness)).ToList()),
        };
    }

    /// <summary>
    /// Executes each configuration once with warm state. Training uses marked accounting frozen strictly before each
    /// availability cutoff. Selected out-of-sample changes form a research splice, not an executable position handoff.
    /// Closing fills determine eligibility; monetary distributions also include the final valuation of remaining exposure.
    /// </summary>
    public static WalkForwardResult WalkForward(Func<Strategy> factory, Dictionary<string, object?> baseParameters, IReadOnlyList<ParameterRange> ranges,
        TickData data, BacktestSettings settings, FitnessMetric metric, int inSampleDays, int outSampleDays, bool anchored, int minTrades,
        IProgress<double>? progress, CancellationToken ct, IReadOnlyList<(DateTime Time, decimal Close)>? contextHistory = null,
        SearchMode search = SearchMode.Auto, int budget = 0, int maxParallel = 0)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var type = factory().GetType();
        int raw = data.Count;
        var space = new ParameterSpace(ranges, type);
        bool grid = search == SearchMode.Grid || (search == SearchMode.Auto && space.Size <= GridLimit);
        int parallel = maxParallel > 0 ? maxParallel : Math.Max(1, Environment.ProcessorCount - 1);
        if (budget <= 0) budget = DefaultBudget;
        var res = new WalkForwardResult
        {
            InSampleDays = inSampleDays, OutSampleDays = outSampleDays, Anchored = anchored, Metric = metric, Ranges = ranges.Where(r => r.Enabled).ToList(),
            Search = grid ? "grilla" : "bayesiana", SpaceSize = space.Size, Compression = raw > 0 ? Math.Round((double)data.Count / raw, 4) : 1,
            AccountingPolicy = "warm-mark-to-market-splice",
            AttributionPolicy = "closing-fill-proportional-carry-residual-and-final-mark",
        };
        var windows = Windows(settings, inSampleDays, outSampleDays, anchored);
        if (windows.Count == 0) throw new InvalidOperationException("The date range is too short for these windows: extend the range or shorten the windows.");

        var calendar = new List<(DateTime From, DateTime To)>();
        for (var day = settings.FromUtc.Date; day < settings.ToUtc; day = day.AddDays(1))
            calendar.Add((day, day.AddDays(1) < settings.ToUtc ? day.AddDays(1) : settings.ToUtc));
        var cutoffs = windows.SelectMany(w => new[] { w.IsFrom, w.IsTo, w.OsTo })
            .Concat(calendar.SelectMany(day => new[] { day.From, day.To })).Distinct().OrderBy(t => t).ToArray();
        var cache = new ConfigCache(factory, baseParameters, data, settings.Clone(), contextHistory, space, cutoffs, calendar, ct);
        long expected = grid ? space.Size : Math.Min(space.Size, (long)budget * windows.Count / 2 + budget);
        int ran = 0;
        void Ran() => progress?.Report(Math.Min(0.95, 0.95 * Interlocked.Increment(ref ran) / Math.Max(1, expected)));
        if (grid) cache.Ensure(space.All().ToList(), parallel, Ran);

        double isProfit = 0, isDaysTotal = 0, osProfit = 0, osDaysTotal = 0;
        decimal capital = settings.InitialCapital;
        decimal peak = capital;
        for (int w = 0; w < windows.Count; w++)
        {
            ct.ThrowIfCancellationRequested();
            var (isFrom, isTo, osTo) = windows[w];
            double Score(ConfigRun run)
            {
                var measured = cache.Measure(run, isFrom, isTo);
                return measured == null || measured.Metrics.TotalTrades < minTrades
                    ? double.NegativeInfinity : measured.Metrics.Fitness(metric);
            }

            var rows = grid
                ? space.All().Select(idx => (Idx: idx, Score: Score(cache.Get(idx)))).ToList()
                : TpeSearch.Run(space, points =>
                {
                    cache.Ensure(points, parallel, Ran);
                    return points.Select(p => Score(cache.Get(p))).ToArray();
                }, budget, 1000 + w, parallel, ct);
            int pick = RobustSelection.Select(rows);
            if (pick < 0)
            {
                res.IsComplete = false;
                res.Windows.Add(new WalkForwardWindow
                {
                    Number = w + 1, InSampleFrom = isFrom, InSampleTo = isTo, OutSampleFrom = isTo, OutSampleTo = osTo,
                    InSampleAvailable = false, OutSampleAvailable = false,
                    InSample = null, OutSample = null, Efficiency = null,
                    UnavailableReason = "No complete training configuration satisfies the actual closing-fill minimum.",
                });
                continue;
            }
            var best = cache.Get(rows[pick].Idx);
            var training = cache.Measure(best, isFrom, isTo)!;
            var isM = training.Metrics;
            var testing = best.Accounting.Measure(isTo, osTo, capital, cache.Crypto);
            if (testing == null || !res.IsComplete)
            {
                res.IsComplete = false;
                res.Windows.Add(new WalkForwardWindow
                {
                    Number = w + 1, InSampleFrom = isFrom, InSampleTo = isTo, OutSampleFrom = isTo, OutSampleTo = osTo,
                    BestParameters = best.Parameters, InSample = isM, OutSampleAvailable = false,
                    OutSample = null, Efficiency = null,
                    InSampleOpeningEquity = training.OpeningEquity, InSampleClosingEquity = training.ClosingEquity,
                    UnavailableReason = testing == null ? best.Accounting.Error ?? "The selected test range is incomplete."
                        : "An earlier out-of-sample segment is unavailable.",
                });
                continue;
            }
            var osM = testing.Metrics;
            decimal stitchedOpening = capital;
            foreach (var contribution in testing.Contributions)
                res.OutSampleTrades.Add(contribution with { Number = res.OutSampleTrades.Count + 1 });
            foreach (var point in testing.Equity)
            {
                peak = Math.Max(peak, point.Equity);
                res.OutSampleEquity.Add(point with { Drawdown = peak - point.Equity });
            }
            capital += testing.ClosingEquity - testing.OpeningEquity;

            double isDays = Math.Max(1, (isTo - isFrom).TotalDays), osDays = Math.Max(1, (osTo - isTo).TotalDays);
            double isRate = (double)isM.NetProfit / isDays, osRate = (double)osM.NetProfit / osDays;
            isProfit += (double)isM.NetProfit;
            isDaysTotal += isDays;
            osProfit += (double)osM.NetProfit;
            osDaysTotal += osDays;
            res.Windows.Add(new WalkForwardWindow
            {
                Number = w + 1,
                InSampleFrom = isFrom,
                InSampleTo = isTo,
                OutSampleFrom = isTo,
                OutSampleTo = osTo,
                BestParameters = best.Parameters,
                InSample = isM,
                OutSample = osM,
                Efficiency = isRate > 0 ? osRate / isRate : 0,
                InSampleOpeningEquity = training.OpeningEquity,
                InSampleClosingEquity = training.ClosingEquity,
                OutSampleOpeningEquity = testing.OpeningEquity,
                OutSampleClosingEquity = testing.ClosingEquity,
                OutSampleStitchedOpeningEquity = stitchedOpening,
                OutSampleStitchedClosingEquity = capital,
            });
        }

        res.OutSampleMetrics = PerformanceMetrics.Compute(res.OutSampleTrades, res.OutSampleEquity, settings.InitialCapital, cache.Crypto, 0, 0);
        res.OutSampleMetrics.AttributionUnit = PerformanceAttributionUnit.WindowClosingFillAndBoundaryValuation;
        // eficiencia global: ganancia diaria fuera de muestra / ganancia diaria dentro de muestra
        double isDaily = isDaysTotal > 0 ? isProfit / isDaysTotal : 0, osDaily = osDaysTotal > 0 ? osProfit / osDaysTotal : 0;
        res.WalkForwardEfficiency = isDaily > 0 ? osDaily / isDaily : 0;
        var completeWindows = res.Windows.Where(w => w.InSampleAvailable && w.OutSampleAvailable).ToList();
        res.ProfitableWindowsPercent = completeWindows.Count > 0 ? 100.0 * completeWindows.Count(x => x.OutSample!.NetProfit > 0) / completeWindows.Count : 0;
        Overfit(res, cache, settings, minTrades);
        res.Evaluated = cache.Count;
        res.Seconds = sw.Elapsed.TotalSeconds;
        progress?.Report(1);
        return res;
    }

    /// <summary>Las ventanas: (desde, hasta dentro de muestra, hasta fuera de muestra).</summary>
    private static List<(DateTime IsFrom, DateTime IsTo, DateTime OsTo)> Windows(BacktestSettings settings, int inSampleDays, int outSampleDays, bool anchored)
    {
        var start = settings.FromUtc.Date;
        var end = settings.ToUtc;
        var windows = new List<(DateTime IsFrom, DateTime IsTo, DateTime OsTo)>();
        for (var isFrom = start; ; isFrom = anchored ? start : isFrom.AddDays(outSampleDays))
        {
            var isTo = anchored ? start.AddDays(inSampleDays + windows.Count * outSampleDays) : isFrom.AddDays(inSampleDays);
            var osTo = isTo.AddDays(outSampleDays);
            if (isTo >= end) break;
            if (osTo > end) osTo = end;
            windows.Add((isFrom, isTo, osTo));
            if (osTo >= end) break;
        }

        return windows;
    }

    /// <summary>PBO y CPCV con el resultado diario de todas las combinaciones que se corrieron (hacen falta 4 o más).</summary>
    private static void Overfit(WalkForwardResult res, ConfigCache cache, BacktestSettings settings, int minTrades)
    {
        var runs = cache.Done.OrderBy(r => ParameterSpace.Key(r.Index), StringComparer.Ordinal).ToList();
        if (runs.Count < 4 || cache.Calendar.Count < 20) return;
        var daily = runs.Select(cache.Daily).ToList();
        var completeDaily = daily.Where(days => days.All(day => day.Available))
            .Select(days => days.Select(day => (double)day.Path.Net).ToArray()).ToList();
        if (Overfitting.Pbo(completeDaily) is { } pbo)
        {
            res.Pbo = Math.Round(pbo.Pbo, 4);
            res.PboLoss = Math.Round(pbo.Loss, 4);
            res.PboSplits = pbo.Splits;
        }

        var holds = runs.SelectMany(r => r.Accounting.Fills.Where(f => f.ClosingTrade != null).Take(200))
            .Select(f => (f.AvailableAt - f.ClosingTrade!.EntryTime).TotalDays).OrderBy(days => days).ToList();
        int embargo = holds.Count > 0 ? Math.Clamp((int)Math.Ceiling(holds[holds.Count / 2]), 1, 5) : 1;
        res.CpcvPaths = Overfitting.Cpcv(runs.Select(r => r.Index).ToList(), daily, minTrades, settings.InitialCapital, cache.Crypto, groups: 8, embargo: embargo);
        res.CpcvGroups = res.CpcvPaths.Count > 0 ? res.CpcvPaths.Count + 1 : 0;
        res.EmbargoDays = embargo;
        res.CpcvPolicy = "retrospective-warm-marked-ranges";
    }

    /// <summary>Reordena las operaciones al azar muchas veces para ver qué drawdowns son posibles.</summary>
    public static MonteCarloResult MonteCarlo(IReadOnlyList<ClosedTrade> trades, decimal capital, int runs = 1000, decimal ruinDrawdownPercent = 50, int seed = 7)
    {
        if (trades.Any(t => !t.HasCompleteTradeEvidence))
            return new MonteCarloResult
            {
                Available = false, UnavailableReason = "Marked window contributions are not independent completed trades.",
                DrawdownP50 = null, DrawdownP95 = null, DrawdownP99 = null, WorstDrawdown = null,
                NetProfit = null, RuinProbabilityPercent = null,
            };
        var res = new MonteCarloResult { Runs = runs, NetProfit = trades.Sum(t => t.NetPnl) };
        if (trades.Count < 2) return res;
        var rnd = new Random(seed);
        var pnls = trades.Select(t => t.NetPnl).ToArray();
        var dds = new decimal[runs];
        int ruined = 0;
        for (int r = 0; r < runs; r++)
        {
            var order = pnls.OrderBy(_ => rnd.Next()).ToArray();
            decimal eq = capital, peak = capital, maxDd = 0;
            List<decimal>? curve = r < 40 ? new List<decimal>(order.Length + 1) { capital } : null;
            foreach (var p in order)
            {
                eq += p;
                peak = Math.Max(peak, eq);
                maxDd = Math.Max(maxDd, peak - eq);
                curve?.Add(eq);
            }

            dds[r] = maxDd;
            if (capital > 0 && maxDd / capital * 100 >= ruinDrawdownPercent) ruined++;
            if (curve != null) res.SampleCurves.Add(curve);
        }

        Array.Sort(dds);
        res.DrawdownP50 = dds[(int)(runs * 0.50)];
        res.DrawdownP95 = dds[Math.Min(runs - 1, (int)(runs * 0.95))];
        res.DrawdownP99 = dds[Math.Min(runs - 1, (int)(runs * 0.99))];
        res.WorstDrawdown = dds[^1];
        res.RuinProbabilityPercent = 100.0 * ruined / runs;
        return res;
    }
}
