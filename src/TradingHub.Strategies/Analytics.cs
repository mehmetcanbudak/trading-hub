namespace TradingHub.Strategies;

/// <summary>Resultado agrupado (por lado, hora, día de la semana…).</summary>
public sealed class BucketStat
{
    public string Label { get; set; } = "";
    public int Trades { get; set; }
    public int Winners { get; set; }
    public decimal Net { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal GrossLoss { get; set; }
    public decimal LargestWin { get; set; }
    public decimal LargestLoss { get; set; }
    public decimal WinRate => Trades > 0 ? 100m * Winners / Trades : 0;
    public decimal ProfitFactor => GrossLoss > 0 ? GrossProfit / GrossLoss : GrossProfit > 0 ? 999 : 0;
    public decimal Average => Trades > 0 ? Net / Trades : 0;

    public void Add(ClosedTrade t)
    {
        decimal p = t.NetPnl;
        Trades++;
        Net += p;
        if (p > 0)
        {
            Winners++;
            GrossProfit += p;
            LargestWin = Math.Max(LargestWin, p);
        }
        else
        {
            GrossLoss -= p;
            LargestLoss = Math.Min(LargestLoss, p);
        }
    }
}

/// <summary>Un período de drawdown: del máximo al mínimo y (si pasó) a la recuperación.</summary>
public sealed class DrawdownPeriod
{
    public DateTime Start { get; set; }
    public DateTime Trough { get; set; }
    public DateTime? Recovery { get; set; }
    public decimal Depth { get; set; }
    public decimal DepthPercent { get; set; }
    public double Days { get; set; }
}

/// <summary>Rendimiento de un año, mes por mes (en % del capital al empezar cada mes).</summary>
public sealed class YearReturns
{
    public int Year { get; set; }
    public decimal?[] MonthPercent { get; } = new decimal?[12];
    public decimal?[] MonthNet { get; } = new decimal?[12];
    public decimal YearPercent { get; set; }
    public decimal YearNet { get; set; }
}

/// <summary>
/// Estadísticas de un informe profesional a partir de las operaciones y la curva de capital:
/// CAGR, Calmar, SQN, Ulcer, Kelly, MAE/MFE, rachas, duración, días, mensual, por hora, por día,
/// drawdowns principales, distribución y mejores/peores operaciones.
/// </summary>
public sealed class TradeAnalytics
{
    public decimal Capital { get; private set; }
    public decimal FinalEquity { get; private set; }
    public DateTime From { get; private set; }
    public DateTime To { get; private set; }
    public double Days { get; private set; }

    public double CagrPercent { get; private set; }
    public double Calmar { get; private set; }
    public double Sqn { get; private set; }
    public decimal TradeStdDev { get; private set; }
    public double UlcerIndex { get; private set; }
    public double KellyPercent { get; private set; }

    public decimal AverageMae { get; private set; }
    public decimal AverageMfe { get; private set; }
    public double EdgeRatio { get; private set; }

    public TimeSpan AverageDuration { get; private set; }
    public TimeSpan MedianDuration { get; private set; }
    public TimeSpan MaxDuration { get; private set; }
    public TimeSpan AverageWinDuration { get; private set; }
    public TimeSpan AverageLossDuration { get; private set; }
    public double AverageBarsWin { get; private set; }
    public double AverageBarsLoss { get; private set; }
    public double TradesPerDay { get; private set; }

    public int TradingDays { get; private set; }
    public decimal AverageDailyPnl { get; private set; }
    public decimal BestDay { get; private set; }
    public decimal WorstDay { get; private set; }
    public double ProfitableDaysPercent { get; private set; }

    public double MaxDrawdownDays { get; private set; }
    public double LongestFlatDays { get; private set; }

    public BucketStat Long { get; } = new() { Label = "Long trades" };
    public BucketStat Short { get; } = new() { Label = "Short trades" };
    public List<YearReturns> Monthly { get; } = new();
    public List<BucketStat> ByHour { get; } = new();
    public List<BucketStat> ByWeekday { get; } = new();
    public List<DrawdownPeriod> Drawdowns { get; } = new();
    public List<(decimal From, decimal To, int Count)> Histogram { get; } = new();
    public List<ClosedTrade> Best { get; } = new();
    public List<ClosedTrade> Worst { get; } = new();

    /// <summary>Curva de capital al cierre de cada operación (para el drawdown "de cierre").</summary>
    public List<(DateTime Time, decimal Equity)> ClosedEquity { get; } = new();

    private static readonly string[] WeekdayNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    public static TradeAnalytics Compute(IReadOnlyList<ClosedTrade> trades, IReadOnlyList<EquityPoint> equity, decimal capital, PerformanceMetrics m,
        TimeZoneInfo? tz = null, DateTime? from = null, DateTime? to = null)
    {
        tz ??= TimeZoneInfo.Utc;
        var a = new TradeAnalytics { Capital = capital };
        var sorted = trades.OrderBy(t => t.ExitTime).ToList();
        a.From = from ?? (sorted.Count > 0 ? sorted[0].EntryTime : equity.Count > 0 ? equity[0].Time : DateTime.UtcNow);
        a.To = to ?? (sorted.Count > 0 ? sorted[^1].ExitTime : equity.Count > 0 ? equity[^1].Time : DateTime.UtcNow);
        a.Days = Math.Max(1e-9, (a.To - a.From).TotalDays);
        a.FinalEquity = capital + m.NetProfit;

        // rendimiento anualizado y relación con el drawdown
        if (capital > 0 && a.FinalEquity > 0 && a.Days >= 1)
            a.CagrPercent = (Math.Pow((double)(a.FinalEquity / capital), 365.0 / a.Days) - 1) * 100;
        else if (capital > 0) a.CagrPercent = (double)(m.NetProfit / capital) * 100 * 365.0 / Math.Max(1, a.Days);
        a.Calmar = m.MaxDrawdownPercent > 0 ? a.CagrPercent / (double)m.MaxDrawdownPercent : 0;

        if (sorted.Count > 0)
        {
            var pnls = sorted.Select(t => (double)t.NetPnl).ToArray();
            double mean = pnls.Average();
            double sd = pnls.Length > 1 ? Math.Sqrt(pnls.Sum(p => (p - mean) * (p - mean)) / (pnls.Length - 1)) : 0;
            a.TradeStdDev = (decimal)sd;
            a.Sqn = sd > 0 ? Math.Sqrt(Math.Min(pnls.Length, 100)) * mean / sd : 0;
            double w = (double)m.WinRate / 100, r = (double)m.PayoffRatio;
            a.KellyPercent = r > 0 ? (w - (1 - w) / r) * 100 : 0;

            a.AverageMae = sorted.Average(t => Math.Abs(t.Mae));
            a.AverageMfe = sorted.Average(t => Math.Abs(t.Mfe));
            a.EdgeRatio = a.AverageMae > 0 ? (double)(a.AverageMfe / a.AverageMae) : 0;

            var durations = sorted.Select(t => t.ExitTime - t.EntryTime).OrderBy(d => d).ToList();
            a.AverageDuration = TimeSpan.FromTicks((long)durations.Average(d => d.Ticks));
            a.MedianDuration = durations[durations.Count / 2];
            a.MaxDuration = durations[^1];
            var wins = sorted.Where(t => t.NetPnl > 0).ToList();
            var losses = sorted.Where(t => t.NetPnl <= 0).ToList();
            if (wins.Count > 0)
            {
                a.AverageWinDuration = TimeSpan.FromTicks((long)wins.Average(t => (t.ExitTime - t.EntryTime).Ticks));
                a.AverageBarsWin = wins.Average(t => t.Bars);
            }

            if (losses.Count > 0)
            {
                a.AverageLossDuration = TimeSpan.FromTicks((long)losses.Average(t => (t.ExitTime - t.EntryTime).Ticks));
                a.AverageBarsLoss = losses.Average(t => t.Bars);
            }

            a.TradesPerDay = sorted.Count / Math.Max(1, a.Days);

            foreach (var t in sorted) (t.IsLong ? a.Long : a.Short).Add(t);

            // por hora (de entrada, en la zona de la sesión) y por día de la semana
            var hours = new SortedDictionary<int, BucketStat>();
            var weekdays = new SortedDictionary<int, BucketStat>();
            foreach (var t in sorted)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t.EntryTime, DateTimeKind.Utc), tz);
                if (!hours.TryGetValue(local.Hour, out var h)) hours[local.Hour] = h = new BucketStat { Label = $"{local.Hour:00}:00" };
                h.Add(t);
                int wd = ((int)local.DayOfWeek + 6) % 7; // lunes primero
                if (!weekdays.TryGetValue(wd, out var d)) weekdays[wd] = d = new BucketStat { Label = WeekdayNames[(int)local.DayOfWeek] };
                d.Add(t);
            }

            a.ByHour.AddRange(hours.Values);
            a.ByWeekday.AddRange(weekdays.Values);

            // días operados
            var daily = sorted.GroupBy(t => DateOnly.FromDateTime(t.ExitTime)).Select(g => g.Sum(x => x.NetPnl)).ToList();
            a.TradingDays = daily.Count;
            a.AverageDailyPnl = daily.Average();
            a.BestDay = daily.Max();
            a.WorstDay = daily.Min();
            a.ProfitableDaysPercent = 100.0 * daily.Count(x => x > 0) / daily.Count;

            // mensual: % sobre el capital al empezar el mes
            decimal running = capital;
            foreach (var g in sorted.GroupBy(t => (t.ExitTime.Year, t.ExitTime.Month)).OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month))
            {
                var yr = a.Monthly.FirstOrDefault(y => y.Year == g.Key.Year);
                if (yr == null)
                {
                    yr = new YearReturns { Year = g.Key.Year };
                    a.Monthly.Add(yr);
                }

                decimal net = g.Sum(t => t.NetPnl);
                yr.MonthNet[g.Key.Month - 1] = net;
                yr.MonthPercent[g.Key.Month - 1] = running > 0 ? net / running * 100 : 0;
                running += net;
            }

            decimal startOfYear = capital;
            foreach (var yr in a.Monthly)
            {
                yr.YearNet = yr.MonthNet.Sum(x => x ?? 0);
                yr.YearPercent = startOfYear > 0 ? yr.YearNet / startOfYear * 100 : 0;
                startOfYear += yr.YearNet;
            }

            // distribución de resultados (hasta 12 tramos)
            decimal lo = sorted.Min(t => t.NetPnl), hi = sorted.Max(t => t.NetPnl);
            if (hi > lo)
            {
                int buckets = Math.Clamp((int)Math.Ceiling(Math.Sqrt(sorted.Count)), 5, 12);
                decimal step = (hi - lo) / buckets;
                for (int i = 0; i < buckets; i++)
                {
                    decimal f = lo + step * i, tt = i == buckets - 1 ? hi : f + step;
                    int count = sorted.Count(t => t.NetPnl >= f && (i == buckets - 1 ? t.NetPnl <= tt : t.NetPnl < tt));
                    a.Histogram.Add((f, tt, count));
                }
            }

            a.Best.AddRange(sorted.OrderByDescending(t => t.NetPnl).Take(5));
            a.Worst.AddRange(sorted.OrderBy(t => t.NetPnl).Take(5));

            // curva al cierre de cada operación y sus drawdowns
            decimal eq = capital;
            a.ClosedEquity.Add((a.From, capital));
            foreach (var t in sorted)
            {
                eq += t.NetPnl;
                a.ClosedEquity.Add((t.ExitTime, eq));
            }

            a.ComputeDrawdowns();
        }

        // Ulcer index (sobre la curva de capital): raíz del promedio de los drawdowns % al cuadrado
        if (equity.Count > 1)
        {
            decimal peak = capital;
            double sum = 0;
            foreach (var p in equity)
            {
                peak = Math.Max(peak, p.Equity);
                double dd = peak > 0 ? (double)((peak - p.Equity) / peak * 100) : 0;
                sum += dd * dd;
            }

            a.UlcerIndex = Math.Sqrt(sum / equity.Count);
        }

        return a;
    }

    private void ComputeDrawdowns()
    {
        var periods = new List<DrawdownPeriod>();
        decimal peak = ClosedEquity[0].Equity;
        DateTime peakTime = ClosedEquity[0].Time;
        DrawdownPeriod? cur = null;
        DateTime lastHigh = peakTime;
        double longestFlat = 0;
        foreach (var (time, eq) in ClosedEquity)
        {
            if (eq >= peak)
            {
                if (cur != null)
                {
                    cur.Recovery = time;
                    cur.Days = (time - cur.Start).TotalDays;
                    periods.Add(cur);
                    cur = null;
                }

                longestFlat = Math.Max(longestFlat, (time - lastHigh).TotalDays);
                peak = eq;
                peakTime = time;
                lastHigh = time;
                continue;
            }

            decimal depth = peak - eq;
            cur ??= new DrawdownPeriod { Start = peakTime, Trough = time };
            if (depth > cur.Depth)
            {
                cur.Depth = depth;
                cur.DepthPercent = peak > 0 ? depth / peak * 100 : 0;
                cur.Trough = time;
            }
        }

        if (cur != null)
        {
            cur.Days = (To - cur.Start).TotalDays;
            periods.Add(cur);
        }

        longestFlat = Math.Max(longestFlat, (To - lastHigh).TotalDays);
        LongestFlatDays = longestFlat;
        Drawdowns.AddRange(periods.OrderByDescending(p => p.Depth).Take(5));
        MaxDrawdownDays = periods.Count > 0 ? periods.Max(p => p.Days) : 0;
    }
}
