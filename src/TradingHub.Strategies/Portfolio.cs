namespace TradingHub.Strategies;

public sealed record PortfolioResearchCoverage(DateTime From, DateTime To, bool IsComplete, int CompletedWindows, int TotalWindows,
    string? AccountingPolicy, string? AttributionPolicy, string? CpcvPolicy, IReadOnlyList<(DateTime From, DateTime To)> AvailableIntervals);

/// <summary>Una estrategia dentro de un portafolio: una corrida guardada y su peso.</summary>
public sealed class PortfolioComponent
{
    public string StrategyId { get; set; } = "";
    public string StrategyName { get; set; } = "";
    public string RunId { get; set; } = "";

    /// <summary>Multiplicador del tamaño (1 = como en la corrida).</summary>
    public decimal Weight { get; set; } = 1;

    public string Label { get; set; } = "";
    public BacktestSettings Settings { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public List<ClosedTrade> Trades { get; set; } = new();
    public PortfolioResearchCoverage? ResearchCoverage { get; set; }
    public RunKind? Kind { get; set; }
    public string? CodeHash { get; set; }
    public RunInputEvidence? InputEvidence { get; set; }

    public static PortfolioComponent FromRun(StrategyRun run, decimal weight = 1) => new()
    {
        StrategyId = run.StrategyId,
        StrategyName = run.StrategyName,
        RunId = run.Id,
        Weight = weight,
        Label = $"{run.StrategyName} · {run.Settings.Symbol} {run.Settings.Period}",
        Settings = run.Settings,
        Metrics = run.Metrics ?? new PerformanceMetrics(),
        Trades = run.Trades ?? new List<ClosedTrade>(),
        ResearchCoverage = Coverage(run),
        Kind = run.Kind,
        CodeHash = run.CodeHash,
        InputEvidence = run.InputEvidence,
    };

    private static PortfolioResearchCoverage? Coverage(StrategyRun run)
    {
        bool attributed = run.WalkForward?.AccountingPolicy != null ||
                          run.Metrics?.AttributionUnit == PerformanceAttributionUnit.WindowClosingFillAndBoundaryValuation ||
                          run.Trades?.Any(t => !t.HasCompleteTradeEvidence) == true;
        if (!attributed) return null;
        var wf = run.WalkForward ?? throw new InvalidOperationException("Marked research components require saved walk-forward coverage.");
        WalkForwardWindow? first = null, previous = null;
        int previousIndex = -1;
        var intervals = new List<(DateTime From, DateTime To)>(wf.Windows.Count);
        for (int index = 0; index < wf.Windows.Count; index++)
        {
            var window = wf.Windows[index];
            if (!window.InSampleAvailable || window.InSample == null || !window.OutSampleAvailable || window.OutSample == null) continue;
            if (window.OutSampleTo <= window.OutSampleFrom)
                throw new InvalidOperationException("A marked research component has an invalid out-of-sample interval.");
            if (previous != null && (index != previousIndex + 1 || window.OutSampleFrom != previous.OutSampleTo))
                throw new InvalidOperationException("A marked research component has an internal unavailable gap. The portfolio cannot represent that coverage without inventing returns.");
            first ??= window;
            previous = window;
            previousIndex = index;
            intervals.Add((window.OutSampleFrom, window.OutSampleTo));
        }
        if (first == null || previous == null)
            throw new InvalidOperationException("No complete out-of-sample interval is available for this research component.");
        return new PortfolioResearchCoverage(first.OutSampleFrom, previous.OutSampleTo, wf.IsComplete && intervals.Count == wf.Windows.Count,
            intervals.Count, wf.Windows.Count, wf.AccountingPolicy, wf.AttributionPolicy, wf.CpcvPolicy, intervals);
    }
}

/// <summary>Resultado de combinar varias estrategias.</summary>
public sealed class PortfolioResult
{
    public List<PortfolioComponent> Components { get; set; } = new();
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    /// <summary>Backtests are modeled estimates. Live aggregation sets this from its participating ledgers.</summary>
    public LiveFinancialQuality FinancialQuality { get; set; } = LiveFinancialQuality.Estimated;

    /// <summary>True si los componentes no comparten fechas y se usó la unión de los rangos.</summary>
    public bool NoOverlap { get; set; }

    public List<DateOnly> Days { get; set; } = new();

    /// <summary>Resultado diario de cada componente (ya multiplicado por su peso), alineado con <see cref="Days"/>.</summary>
    public List<decimal[]> DailyPnl { get; set; } = new();

    /// <summary>Correlación de los resultados diarios (Pearson), componente × componente.</summary>
    public double[,] Correlation { get; set; } = new double[0, 0];

    public List<ClosedTrade> Trades { get; set; } = new();
    public List<EquityPoint> Equity { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public List<PerformanceMetrics> ComponentMetrics { get; set; } = new();

    public double AverageCorrelation { get; set; }
    public double MaxCorrelation { get; set; }
    public decimal SumOfComponentDrawdowns { get; set; }
    public double AverageComponentSharpe { get; set; }

    /// <summary>Cuánto menos drawdown tiene el portafolio que la suma de los drawdowns individuales (%).</summary>
    public double DrawdownReductionPercent => SumOfComponentDrawdowns > 0 ? (double)(1 - Metrics.MaxDrawdown / SumOfComponentDrawdowns) * 100 : 0;
}

/// <summary>
/// Portafolios de estrategias: suma los resultados diarios de cada una (con su peso), mide la correlación
/// entre ellas y busca combinaciones poco correlacionadas que mejoren el Sharpe y bajen el drawdown.
/// </summary>
public static class PortfolioAnalyzer
{
    private static DateTime Start(PortfolioComponent c) => c.ResearchCoverage?.From ??
        (c.Settings.FromUtc != default ? c.Settings.FromUtc : c.Trades.Count > 0 ? c.Trades.Min(t => t.EntryTime) : DateTime.MinValue);
    private static DateTime End(PortfolioComponent c) => c.ResearchCoverage?.To ??
        (c.Settings.ToUtc != default ? c.Settings.ToUtc : c.Trades.Count > 0 ? c.Trades.Max(t => t.ExitTime) : DateTime.MinValue);

    private static bool ClosingBoundary(ClosedTrade trade) =>
        trade.Attribution == TradeAttribution.WindowBoundaryValuation && trade.WindowTo == trade.ExitTime;

    private static bool InRange(ClosedTrade trade, DateTime from, DateTime to, bool halfOpen) =>
        !halfOpen ? trade.ExitTime >= from && trade.ExitTime <= to
            : ClosingBoundary(trade) ? trade.ExitTime > from && trade.ExitTime <= to
            : trade.ExitTime >= from && trade.ExitTime < to;

    private static ClosedTrade Scale(ClosedTrade trade, decimal weight, bool scaleQuantity) => trade with
    {
        Pnl = trade.Pnl * weight,
        Commission = trade.Commission * weight,
        Quantity = scaleQuantity || !trade.HasCompleteTradeEvidence ? trade.Quantity * weight : trade.Quantity,
        AttributionNet = trade.AttributionNet * weight,
        CarryNetCredit = trade.CarryNetCredit * weight,
        CarryCommissionCredit = trade.CarryCommissionCredit * weight,
        Mae = trade.HasCompleteTradeEvidence ? trade.Mae : trade.Mae * weight,
        Mfe = trade.HasCompleteTradeEvidence ? trade.Mfe : trade.Mfe * weight,
    };

    private static Dictionary<DateOnly, decimal> Daily(PortfolioComponent c, DateTime from, DateTime to, bool halfOpen)
    {
        var d = new Dictionary<DateOnly, decimal>();
        foreach (var t in c.Trades)
        {
            if (!InRange(t, from, to, halfOpen)) continue;
            var time = halfOpen && ClosingBoundary(t) ? t.ExitTime.AddTicks(-1) : t.ExitTime;
            var day = DateOnly.FromDateTime(time);
            d[day] = d.GetValueOrDefault(day) + t.NetPnl * c.Weight;
        }

        return d;
    }

    /// <summary>Correlación de Pearson (0 si alguno no varía).</summary>
    public static double Pearson(IReadOnlyList<decimal> a, IReadOnlyList<decimal> b)
    {
        int n = Math.Min(a.Count, b.Count);
        if (n < 3) return 0;
        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++)
        {
            ma += (double)a[i];
            mb += (double)b[i];
        }

        ma /= n;
        mb /= n;
        double cov = 0, va = 0, vb = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)a[i] - ma, y = (double)b[i] - mb;
            cov += x * y;
            va += x * x;
            vb += y * y;
        }

        return va > 0 && vb > 0 ? cov / Math.Sqrt(va * vb) : 0;
    }

    /// <summary>Combina los componentes en el período que comparten (o en la unión si no comparten nada).</summary>
    public static PortfolioResult Analyze(IReadOnlyList<PortfolioComponent> components, decimal capital)
    {
        var res = new PortfolioResult { Components = components.ToList() };
        if (components.Count == 0) return res;
        bool halfOpen = components.Any(c => c.ResearchCoverage != null);
        var from = components.Max(Start);
        var to = components.Min(End);
        if (to <= from)
        {
            if (halfOpen)
                throw new InvalidOperationException("Research components have no common available out-of-sample interval. The portfolio cannot fill unavailable coverage with zero returns.");
            res.NoOverlap = true;
            from = components.Min(Start);
            to = components.Max(End);
        }
        if (halfOpen)
        {
            for (int i = 0; i < components.Count; i++)
            {
                var coverage = components[i].ResearchCoverage;
                if (coverage == null) continue;
                for (int j = 0; j < coverage.AvailableIntervals.Count; j++)
                {
                    var interval = coverage.AvailableIntervals[j];
                    if ((interval.From < from && from < interval.To) || (interval.From < to && to < interval.To))
                        throw new InvalidOperationException("The common portfolio interval cuts through a marked out-of-sample window. Whole measured windows are required because their contributions cannot be rebased.");
                }
            }
        }

        res.From = from;
        res.To = to;
        var dailies = components.Select(c => Daily(c, from, to, halfOpen)).ToList();
        var lastDay = DateOnly.FromDateTime(halfOpen ? to.AddTicks(-1) : to);
        for (var d = DateOnly.FromDateTime(from); d <= lastDay; d = d.AddDays(1)) res.Days.Add(d);
        foreach (var daily in dailies) res.DailyPnl.Add(res.Days.Select(d => daily.GetValueOrDefault(d)).ToArray());

        int n = components.Count;
        res.Correlation = new double[n, n];
        double sum = 0, max = double.MinValue;
        int pairs = 0;
        for (int i = 0; i < n; i++)
        {
            res.Correlation[i, i] = 1;
            for (int j = i + 1; j < n; j++)
            {
                double c = Pearson(res.DailyPnl[i], res.DailyPnl[j]);
                res.Correlation[i, j] = res.Correlation[j, i] = c;
                sum += c;
                max = Math.Max(max, c);
                pairs++;
            }
        }

        res.AverageCorrelation = pairs > 0 ? sum / pairs : 0;
        res.MaxCorrelation = pairs > 0 ? max : 0;

        // operaciones del portafolio (con su peso) y curva diaria
        res.Trades = components
            .SelectMany(c => c.Trades.Where(t => InRange(t, from, to, halfOpen))
                .Select(t => Scale(t, c.Weight, scaleQuantity: true)))
            .OrderBy(t => t.ExitTime).ToList();
        decimal eq = capital, peak = capital;
        res.Equity.Add(new EquityPoint(from, capital, 0));
        for (int k = 0; k < res.Days.Count; k++)
        {
            eq += res.DailyPnl.Sum(p => p[k]);
            peak = Math.Max(peak, eq);
            res.Equity.Add(new EquityPoint(res.Days[k].ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc), eq, peak - eq));
        }

        bool crypto = components.All(c => c.Settings.FeedId.StartsWith("binance") || c.Settings.FeedId.StartsWith("bybit") || c.Settings.FeedId == "sim");
        res.Metrics = PerformanceMetrics.Compute(res.Trades, res.Equity, capital, crypto, 0, 0);

        // cada componente solo, en el mismo período, con su peso y la misma curva diaria (días sin operaciones incluidos),
        // así sus métricas son comparables con las del portafolio
        for (int i = 0; i < components.Count; i++)
        {
            var c = components[i];
            var trades = c.Trades.Where(t => InRange(t, from, to, halfOpen))
                .Select(t => Scale(t, c.Weight, scaleQuantity: false)).OrderBy(t => t.ExitTime).ToList();
            var curve = new List<EquityPoint> { new(from, capital, 0) };
            decimal e = capital, pk = capital;
            for (int k = 0; k < res.Days.Count; k++)
            {
                e += res.DailyPnl[i][k];
                pk = Math.Max(pk, e);
                curve.Add(new EquityPoint(res.Days[k].ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc), e, pk - e));
            }

            res.ComponentMetrics.Add(PerformanceMetrics.Compute(trades, curve, capital, crypto, 0, 0));
        }

        res.SumOfComponentDrawdowns = res.ComponentMetrics.Sum(m => m.MaxDrawdown);
        res.AverageComponentSharpe = res.ComponentMetrics.Count > 0 ? res.ComponentMetrics.Average(m => m.SharpeRatio) : 0;
        return res;
    }

    /// <summary>Pesos por riesgo parejo: cada estrategia pesa la inversa de la volatilidad de su resultado diario.</summary>
    public static decimal[] InverseVolatilityWeights(IReadOnlyList<PortfolioComponent> components, decimal capital)
    {
        var unit = components.Select(c => new PortfolioComponent { Settings = c.Settings, Trades = c.Trades, Metrics = c.Metrics, ResearchCoverage = c.ResearchCoverage, Weight = 1 }).ToList();
        var r = Analyze(unit, capital);
        var vols = r.DailyPnl.Select(p =>
        {
            double mean = p.Average(x => (double)x);
            return Math.Sqrt(p.Sum(x => ((double)x - mean) * ((double)x - mean)) / Math.Max(1, p.Length - 1));
        }).ToArray();
        double avg = vols.Where(v => v > 0).DefaultIfEmpty(1).Average();
        return vols.Select(v => v > 0 ? (decimal)Math.Round(avg / v, 2) : 1m).ToArray();
    }

    /// <summary>
    /// Arma una combinación poco correlacionada: empieza por el mejor Sharpe y agrega, de a una, la estrategia
    /// que más mejora el Sharpe del conjunto sin superar la correlación máxima con las ya elegidas.
    /// Devuelve los índices elegidos (en el orden en que entraron).
    /// </summary>
    public static List<int> SuggestUncorrelated(IReadOnlyList<PortfolioComponent> candidates, decimal capital, int maxComponents = 5, double maxCorrelation = 0.5)
    {
        var chosen = new List<int>();
        var usable = Enumerable.Range(0, candidates.Count).Where(i => candidates[i].Trades.Count(t => t.IsActualClosingFill) >= 5 && candidates[i].Metrics.NetProfit > 0).ToList();
        if (usable.Count == 0) return chosen;
        chosen.Add(usable.OrderByDescending(i => candidates[i].Metrics.SharpeRatio).First());
        double current = Analyze(new[] { candidates[chosen[0]] }, capital).Metrics.SharpeRatio;
        while (chosen.Count < maxComponents)
        {
            int best = -1;
            double bestSharpe = current;
            foreach (int i in usable.Except(chosen))
            {
                bool tooCorrelated = false;
                foreach (int j in chosen)
                {
                    var pair = Analyze(new[] { candidates[i], candidates[j] }, capital);
                    if (!pair.NoOverlap && pair.Correlation[0, 1] > maxCorrelation)
                    {
                        tooCorrelated = true;
                        break;
                    }
                }

                if (tooCorrelated) continue;
                var set = chosen.Append(i).Select(k => candidates[k]).ToList();
                double s = Analyze(set, capital).Metrics.SharpeRatio;
                if (s > bestSharpe + 0.01)
                {
                    bestSharpe = s;
                    best = i;
                }
            }

            if (best < 0) break;
            chosen.Add(best);
            current = bestSharpe;
        }

        return chosen;
    }
}
