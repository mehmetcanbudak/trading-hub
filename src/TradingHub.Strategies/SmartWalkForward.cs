using System.Collections.Concurrent;
using System.Globalization;
using TradingHub.Data;

namespace TradingHub.Strategies;

/// <summary>Cómo se buscan los parámetros en cada ventana del walk-forward.</summary>
public enum SearchMode
{
    /// <summary>Grilla completa si es chica (el mapa entero, para ver la meseta); bayesiana si es grande.</summary>
    Auto,

    /// <summary>Todas las combinaciones.</summary>
    Grid,

    /// <summary>Optimización bayesiana (TPE): prueba primero donde es más probable que haya algo bueno.</summary>
    Bayesian,
}

/// <summary>Un camino fuera de muestra de la validación cruzada combinatoria purgada (CPCV).</summary>
public sealed class CpcvPath
{
    public int Number { get; set; }
    public decimal? Net { get; set; }
    public double? Sharpe { get; set; }
    public decimal? MaxDrawdown { get; set; }
    public int Days { get; set; }
    public bool Available { get; set; } = true;
    public string? UnavailableReason { get; set; }
    public int? ActualClosingFills { get; set; }
}

/// <summary>Los valores posibles de cada parámetro que se optimiza (por índice) y cómo se arma cada combinación.</summary>
public sealed class ParameterSpace
{
    private readonly List<(string Name, Type Type, double[] Values)> _dims = new();
    private readonly Dictionary<int, string[]> _choices = new();

    public ParameterSpace(IReadOnlyList<ParameterRange> ranges, Type strategyType)
    {
        var props = Strategy.Parameters(strategyType).ToDictionary(p => p.Name);
        foreach (var r in ranges)
        {
            if (r.IsPeriod)
            {
                // el período de las velas: una dimensión más, cuyos valores son los lugares en la lista (de corto a largo)
                if (!r.Enabled || r.Choices.Count == 0) continue;
                _choices[_dims.Count] = r.Choices.ToArray();
                _dims.Add((r.Name, typeof(string), Enumerable.Range(0, r.Choices.Count).Select(i => (double)i).ToArray()));
            }
            else if (props.TryGetValue(r.Name, out var prop))
                _dims.Add((r.Name, prop.PropertyType, r.Values().ToArray()));
        }
    }

    /// <summary>Los períodos de las velas que se prueban (vacío si el período no se optimiza).</summary>
    public IReadOnlyList<string> Periods => _choices.Values.FirstOrDefault() ?? Array.Empty<string>();

    /// <summary>Para pruebas: dimensiones con estos tamaños (valores 0, 1, 2…).</summary>
    public ParameterSpace(params int[] sizes)
    {
        for (int k = 0; k < sizes.Length; k++) _dims.Add(($"p{k}", typeof(int), Enumerable.Range(0, sizes[k]).Select(v => (double)v).ToArray()));
    }

    public int Dimensions => _dims.Count;
    public int Length(int dim) => _dims[dim].Values.Length;
    public long Size => _dims.Aggregate(1L, (a, d) => a * Math.Max(1, d.Values.Length));

    public Dictionary<string, object?> Config(int[] idx)
    {
        var d = new Dictionary<string, object?>();
        for (int k = 0; k < _dims.Count; k++)
            d[_dims[k].Name] = _choices.TryGetValue(k, out var choices) ? choices[idx[k]] : Convert.ChangeType(_dims[k].Values[idx[k]], _dims[k].Type, CultureInfo.InvariantCulture);
        return d;
    }

    public static string Key(int[] idx) => string.Join(",", idx);

    /// <summary>Todas las combinaciones (la grilla).</summary>
    public IEnumerable<int[]> All()
    {
        var idx = new int[_dims.Count];
        while (true)
        {
            yield return (int[])idx.Clone();
            int j = 0;
            while (j < idx.Length)
            {
                if (++idx[j] < _dims[j].Values.Length) break;
                idx[j] = 0;
                j++;
            }

            if (j == idx.Length) yield break;
        }
    }

    /// <summary>Cuántos pasos de grilla las separan en el parámetro que más difiere (1 = vecinas).</summary>
    public static int Distance(int[] a, int[] b)
    {
        int d = 0;
        for (int k = 0; k < a.Length; k++) d = Math.Max(d, Math.Abs(a[k] - b[k]));
        return d;
    }
}

internal sealed class ConfigRun
{
    public required int[] Index { get; init; }
    public required Dictionary<string, object?> Parameters { get; init; }
    public required AccountingTrace Accounting { get; init; }
    public ConcurrentDictionary<(long, long), Lazy<WindowStatistics?>> Windows { get; } = new();
}

internal sealed class ConfigCache
{
    private readonly ConcurrentDictionary<string, Lazy<ConfigRun>> _runs = new();
    private readonly Func<Strategy> _factory;
    private readonly Dictionary<string, object?> _base;
    private readonly TickData _data;
    private readonly BacktestSettings _settings;
    private readonly IReadOnlyList<(DateTime Time, decimal Close)>? _context;
    private readonly ParameterSpace _space;
    private readonly DateTime[] _cutoffs;
    private readonly CancellationToken _ct;

    public ConfigCache(Func<Strategy> factory, Dictionary<string, object?> baseParameters, TickData data, BacktestSettings settings,
        IReadOnlyList<(DateTime Time, decimal Close)>? context, ParameterSpace space,
        IReadOnlyList<DateTime> cutoffs, IReadOnlyList<(DateTime From, DateTime To)> calendar, CancellationToken ct)
    {
        _factory = factory;
        _base = baseParameters;
        _data = data;
        _settings = settings;
        _context = context;
        _space = space;
        _cutoffs = cutoffs.Distinct().OrderBy(t => t).ToArray();
        Calendar = calendar;
        _ct = ct;
        Crypto = data.Instrument.FeedId.StartsWith("binance", StringComparison.Ordinal) || data.Instrument.FeedId.StartsWith("bybit", StringComparison.Ordinal);
    }

    public bool Crypto { get; }
    public decimal Capital => _settings.InitialCapital;
    public IReadOnlyList<(DateTime From, DateTime To)> Calendar { get; }
    public int Count => _runs.Count;
    public IEnumerable<ConfigRun> Done => _runs.Values.Where(l => l.IsValueCreated).Select(l => l.Value);

    public ConfigRun Get(int[] idx) =>
        _runs.GetOrAdd(ParameterSpace.Key(idx), _ => new Lazy<ConfigRun>(() => Run(idx), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private bool Has(int[] idx) => _runs.TryGetValue(ParameterSpace.Key(idx), out var l) && l.IsValueCreated;

    public void Ensure(IReadOnlyList<int[]> list, int maxParallel, Action? done)
    {
        var options = new ParallelOptions { CancellationToken = _ct, MaxDegreeOfParallelism = maxParallel };
        Parallel.ForEach(list, options, idx =>
        {
            bool had = Has(idx);
            Get(idx);
            if (!had) done?.Invoke();
        });
    }

    private ConfigRun Run(int[] idx)
    {
        var pars = _space.Config(idx);
        var strategy = _factory();
        strategy.SetParameters(_base);
        strategy.SetParameters(pars);
        var settings = Optimizer.SettingsFor(_settings, pars).Clone();
        var accounting = new BacktestEngine(strategy, _data, settings) { ContextHistory = _context }.RunWindows(_cutoffs, _ct);
        return new ConfigRun { Index = idx, Parameters = pars, Accounting = accounting };
    }

    public WindowStatistics? Measure(ConfigRun run, DateTime from, DateTime to) =>
        run.Windows.GetOrAdd((from.Ticks, to.Ticks), _ => new Lazy<WindowStatistics?>(() =>
        {
            var measured = run.Accounting.Measure(from, to, Capital, Crypto);
            return measured == null ? null : new(measured.Metrics, measured.OpeningEquity, measured.ClosingEquity);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public DailyPerformance[] Daily(ConfigRun run) =>
        Calendar.Select(day => run.Accounting.Day(day.From, day.To, Capital)).ToArray();
}

/// <summary>
/// Optimización bayesiana con TPE (Tree-structured Parzen Estimator, Bergstra y otros, 2011) sobre una grilla discreta:
/// arranca con puntos bien repartidos (hipercubo latino) y después propone donde la densidad de las combinaciones buenas
/// supera a la de las malas. Evalúa de a tandas para usar todos los núcleos.
/// </summary>
public static class TpeSearch
{
    /// <param name="evaluate">Devuelve el puntaje de cada combinación (−∞ si no sirve, por ejemplo por falta de operaciones).</param>
    public static List<(int[] Idx, double Score)> Run(ParameterSpace space, Func<IReadOnlyList<int[]>, double[]> evaluate, int budget, int seed, int batch,
        CancellationToken ct = default)
    {
        var rnd = new Random(seed);
        var seen = new HashSet<string>();
        var done = new List<(int[] Idx, double Score)>();
        budget = (int)Math.Min(budget, space.Size);
        batch = Math.Max(1, batch);

        void Take(List<int[]> points)
        {
            if (points.Count == 0) return;
            var scores = evaluate(points);
            for (int i = 0; i < points.Count; i++) done.Add((points[i], scores[i]));
        }

        int startup = (int)Math.Min(budget, Math.Clamp(budget / 4, Math.Min(8, budget), 30));
        Take(LatinHypercube(space, startup, rnd, seen));
        while (done.Count < budget && seen.Count < space.Size)
        {
            ct.ThrowIfCancellationRequested();
            var next = Propose(space, done, Math.Min(batch, budget - done.Count), rnd, seen);
            if (next.Count == 0) break;
            Take(next);
        }

        return done;
    }

    private static List<int[]> LatinHypercube(ParameterSpace space, int n, Random rnd, HashSet<string> seen)
    {
        var points = new List<int[]>();
        var perms = Enumerable.Range(0, space.Dimensions).Select(_ => Enumerable.Range(0, n).OrderBy(_ => rnd.Next()).ToArray()).ToArray();
        for (int i = 0; i < n; i++)
        {
            var idx = new int[space.Dimensions];
            for (int k = 0; k < space.Dimensions; k++)
                idx[k] = Math.Min(space.Length(k) - 1, (int)((perms[k][i] + rnd.NextDouble()) / n * space.Length(k)));
            if (seen.Add(ParameterSpace.Key(idx))) points.Add(idx);
        }

        // si el hipercubo repitió puntos (dimensiones chicas), se completa al azar
        for (int tries = 0; points.Count < n && tries < n * 20; tries++)
        {
            var idx = RandomPoint(space, rnd);
            if (seen.Add(ParameterSpace.Key(idx))) points.Add(idx);
        }

        return points;
    }

    private static int[] RandomPoint(ParameterSpace space, Random rnd)
    {
        var idx = new int[space.Dimensions];
        for (int k = 0; k < idx.Length; k++) idx[k] = rnd.Next(space.Length(k));
        return idx;
    }

    private static List<int[]> Propose(ParameterSpace space, List<(int[] Idx, double Score)> done, int want, Random rnd, HashSet<string> seen)
    {
        var finite = done.Where(d => double.IsFinite(d.Score)).OrderByDescending(d => d.Score).ToList();
        var result = new List<int[]>();
        if (finite.Count < 2)
        {
            // todavía no hay nada bueno que imitar: al azar
            for (int tries = 0; result.Count < want && tries < want * 50; tries++)
            {
                var idx = RandomPoint(space, rnd);
                if (seen.Add(ParameterSpace.Key(idx))) result.Add(idx);
            }

            return result;
        }

        int nGood = Math.Clamp((int)Math.Ceiling(0.2 * finite.Count), 1, 25);
        var good = finite.Take(nGood).Select(d => d.Idx).ToList();
        var bad = done.Except(finite.Take(nGood)).Select(d => d.Idx).ToList();
        int dims = space.Dimensions;
        var pg = new double[dims][];
        var pb = new double[dims][];
        for (int k = 0; k < dims; k++)
        {
            int len = space.Length(k);
            double h = Math.Max(0.75, len / 8.0);
            pg[k] = Parzen(good.Select(x => x[k]), len, h);
            pb[k] = Parzen(bad.Select(x => x[k]), len, h);
        }

        // muchas candidatas sacadas de la densidad de las buenas; se quedan las de mejor cociente buenas/malas
        var cands = new Dictionary<string, (int[] Idx, double Score)>();
        for (int c = 0; c < Math.Max(48, want * 24); c++)
        {
            var idx = new int[dims];
            double score = 0;
            for (int k = 0; k < dims; k++)
            {
                idx[k] = Sample(pg[k], rnd);
                score += Math.Log(pg[k][idx[k]] / pb[k][idx[k]]);
            }

            string key = ParameterSpace.Key(idx);
            if (!seen.Contains(key)) cands[key] = (idx, score);
        }

        foreach (var c in cands.Values.OrderByDescending(c => c.Score).Take(want))
            if (seen.Add(ParameterSpace.Key(c.Idx)))
                result.Add(c.Idx);
        for (int tries = 0; result.Count < want && tries < want * 50; tries++)
        {
            var idx = RandomPoint(space, rnd);
            if (seen.Add(ParameterSpace.Key(idx))) result.Add(idx);
        }

        return result;
    }

    /// <summary>Densidad de Parzen sobre los índices de un parámetro (con un piso para no descartar nada del todo).</summary>
    private static double[] Parzen(IEnumerable<int> points, int len, double h)
    {
        var p = new double[len];
        for (int v = 0; v < len; v++) p[v] = 1.0 / len;
        int n = 0;
        foreach (var x in points)
        {
            n++;
            for (int v = 0; v < len; v++) p[v] += Math.Exp(-0.5 * Math.Pow((v - x) / h, 2));
        }

        double sum = p.Sum();
        for (int v = 0; v < len; v++) p[v] /= sum;
        return p;
    }

    private static int Sample(double[] p, Random rnd)
    {
        double u = rnd.NextDouble(), acc = 0;
        for (int v = 0; v < p.Length; v++)
        {
            acc += p[v];
            if (u <= acc) return v;
        }

        return p.Length - 1;
    }
}

/// <summary>
/// La mejor meseta, no el mejor pico: el puntaje de cada combinación es el promedio con sus vecinas (un paso de grilla en
/// cada parámetro), achicado hacia la mediana cuando tiene pocas vecinas medidas. Una combinación que gana sola entre
/// vecinas que pierden es casi siempre suerte; una zona entera que gana, no.
/// </summary>
public static class RobustSelection
{
    public static int Select(IReadOnlyList<(int[] Idx, double Score)> rows)
    {
        var finite = rows.Where(r => double.IsFinite(r.Score)).Select(r => r.Score).OrderBy(s => s).ToList();
        if (finite.Count == 0) return -1;
        double median = finite[finite.Count / 2];
        double worst = finite[0] - (finite[^1] - finite[0]) * 0.1 - Math.Abs(finite[0]) * 0.1;
        int best = -1;
        double bestScore = double.NegativeInfinity, bestOwn = double.NegativeInfinity;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!double.IsFinite(rows[i].Score)) continue;
            double sum = 0;
            int n = 0;
            for (int j = 0; j < rows.Count; j++)
            {
                if (ParameterSpace.Distance(rows[i].Idx, rows[j].Idx) > 1) continue;
                sum += double.IsFinite(rows[j].Score) ? rows[j].Score : worst;
                n++;
            }

            double score = (sum + median) / (n + 1);
            if (score > bestScore || (score == bestScore && rows[i].Score > bestOwn))
            {
                best = i;
                bestScore = score;
                bestOwn = rows[i].Score;
            }
        }

        return best;
    }
}

/// <summary>Medidas de sobreoptimización sobre el resultado diario de todas las combinaciones probadas.</summary>
public static class Overfitting
{
    /// <summary>
    /// Probabilidad de sobreoptimización con validación cruzada combinatoriamente simétrica (Bailey, Borwein, López de
    /// Prado y Zhu, «The Probability of Backtest Overfitting», 2017): parte la historia en bloques y, para cada forma de
    /// elegir la mitad como «dentro de muestra», toma la combinación con mejor Sharpe ahí y mira dónde queda en la otra
    /// mitad. PBO = proporción de veces que queda por debajo de la mediana; Loss = proporción de veces que pierde plata.
    /// </summary>
    public static (double Pbo, double Loss, int Splits)? Pbo(IReadOnlyList<double[]> daily, int blocks = 10)
    {
        int n = daily.Count;
        if (n < 4) return null;
        int days = daily[0].Length;
        while (blocks >= 4 && days < blocks * 5) blocks -= 2;
        if (blocks < 4) return null;
        var bounds = Enumerable.Range(0, blocks + 1).Select(b => (int)((long)b * days / blocks)).ToArray();
        var stats = new (double Sum, double Sq, int N)[n, blocks];
        for (int c = 0; c < n; c++)
            for (int b = 0; b < blocks; b++)
                for (int d = bounds[b]; d < bounds[b + 1]; d++)
                {
                    double v = daily[c][d];
                    stats[c, b] = (stats[c, b].Sum + v, stats[c, b].Sq + v * v, stats[c, b].N + 1);
                }

        int half = blocks / 2, splits = 0, below = 0, losses = 0;
        var isPerf = new double[n];
        var osPerf = new double[n];
        var osNet = new double[n];
        foreach (var subset in Combinations(blocks, half))
        {
            var inSet = new bool[blocks];
            foreach (var b in subset) inSet[b] = true;
            for (int c = 0; c < n; c++)
            {
                double si = 0, qi = 0, so = 0, qo = 0;
                int ni = 0, no = 0;
                for (int b = 0; b < blocks; b++)
                {
                    var (s, q, k) = stats[c, b];
                    if (inSet[b])
                    {
                        si += s;
                        qi += q;
                        ni += k;
                    }
                    else
                    {
                        so += s;
                        qo += q;
                        no += k;
                    }
                }

                isPerf[c] = Sharpe(si, qi, ni);
                osPerf[c] = Sharpe(so, qo, no);
                osNet[c] = so;
            }

            int star = 0;
            for (int c = 1; c < n; c++)
                if (isPerf[c] > isPerf[star])
                    star = c;
            // rango relativo fuera de muestra (1 = la peor): ω = rango / (n + 1); λ = log(ω / (1 − ω)) ≤ 0 → mitad de abajo
            int rank = 1;
            for (int c = 0; c < n; c++)
                if (c != star && osPerf[c] < osPerf[star])
                    rank++;
            double w = rank / (n + 1.0);
            if (Math.Log(w / (1 - w)) <= 0) below++;
            if (osNet[star] <= 0) losses++;
            splits++;
        }

        return splits == 0 ? null : ((double)below / splits, (double)losses / splits, splits);
    }

    /// <summary>Retrospective purged combinatorial validation using exact marked daily paths.</summary>
    public static List<CpcvPath> Cpcv(IReadOnlyList<int[]> index, IReadOnlyList<DailyPerformance[]> daily, int minTrades,
        decimal capital, bool crypto, int groups = 8, int embargo = 1)
    {
        var paths = new List<CpcvPath>();
        int n = daily.Count;
        if (n == 0) return paths;
        int days = daily[0].Length;
        while (groups >= 4 && days < groups * 5) groups--;
        if (groups < 4) return paths;
        var bounds = Enumerable.Range(0, groups + 1).Select(g => (int)((long)g * days / groups)).ToArray();
        var pieces = Enumerable.Range(0, groups).Select(_ => new List<int>()).ToArray();
        var rows = new (int[] Idx, double Score)[n];
        foreach (var pair in Combinations(groups, 2))
        {
            var train = Enumerable.Repeat(true, days).ToArray();
            foreach (int group in pair)
                for (int d = Math.Max(0, bounds[group] - embargo); d < Math.Min(days, bounds[group + 1] + embargo); d++)
                    train[d] = false;
            for (int c = 0; c < n; c++)
            {
                double sum = 0, sq = 0;
                decimal equity = capital;
                int count = 0, exits = 0;
                bool available = true;
                for (int d = 0; d < days; d++)
                {
                    if (!train[d]) continue;
                    var day = daily[c][d];
                    if (!day.Available) { available = false; break; }
                    decimal basis = Math.Max(capital, equity);
                    double value = basis > 0 ? (double)(day.Path.Net / basis) : 0;
                    equity += day.Path.Net;
                    sum += value;
                    sq += value * value;
                    count++;
                    exits += day.ActualClosingFills;
                }
                rows[c] = (index[c], available && exits >= minTrades && count > 0 ? Sharpe(sum, sq, count) : double.NegativeInfinity);
            }

            int pick = RobustSelection.Select(rows);
            foreach (int group in pair)
            {
                bool available = pick >= 0;
                for (int d = bounds[group]; available && d < bounds[group + 1]; d++)
                    available = daily[pick][d].Available;
                pieces[group].Add(available ? pick : -1);
            }
        }

        int pathCount = pieces.Min(p => p.Count);
        double annualization = Math.Sqrt(crypto ? 365 : 252);
        for (int j = 0; j < pathCount; j++)
        {
            if (pieces.Any(p => p[j] < 0))
            {
                paths.Add(new CpcvPath
                {
                    Number = j + 1,
                    Available = false,
                    UnavailableReason = "A split has no eligible training configuration or its selected test range is incomplete.",
                });
                continue;
            }
            EquityPathSummary summary = default;
            decimal equity = capital;
            double sum = 0, sq = 0;
            int exits = 0;
            for (int group = 0; group < groups; group++)
                for (int d = bounds[group]; d < bounds[group + 1]; d++)
                {
                    var day = daily[pieces[group][j]][d];
                    summary = summary.Append(day.Path);
                    decimal basis = Math.Max(capital, equity);
                    double value = basis > 0 ? (double)(day.Path.Net / basis) : 0;
                    equity += day.Path.Net;
                    sum += value;
                    sq += value * value;
                    exits += day.ActualClosingFills;
                }
            double mean = sum / days;
            double sd = days > 1 ? Math.Sqrt(Math.Max(0, (sq - days * mean * mean) / (days - 1))) : 0;
            paths.Add(new CpcvPath
            {
                Number = j + 1,
                Net = summary.Net,
                Sharpe = sd > 0 ? mean / sd * annualization : 0,
                MaxDrawdown = summary.MaxDrawdown,
                Days = days,
                ActualClosingFills = exits,
            });
        }
        return paths;
    }

    /// <summary>Sharpe (sin anualizar) de una suma de días: media sobre desvío.</summary>
    private static double Sharpe(double sum, double sq, int n)
    {
        if (n < 2) return n == 1 ? sum : 0;
        double mean = sum / n;
        double var = Math.Max(0, (sq - n * mean * mean) / (n - 1));
        return var > 0 ? mean / Math.Sqrt(var) : mean > 0 ? 1e6 : mean < 0 ? -1e6 : 0;
    }

    /// <summary>Todas las formas de elegir k de n (en orden).</summary>
    public static IEnumerable<int[]> Combinations(int n, int k)
    {
        var c = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])c.Clone();
            int i = k - 1;
            while (i >= 0 && c[i] == n - k + i) i--;
            if (i < 0) yield break;
            c[i]++;
            for (int j = i + 1; j < k; j++) c[j] = c[j - 1] + 1;
        }
    }
}
