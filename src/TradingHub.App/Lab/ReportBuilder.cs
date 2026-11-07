using System.Globalization;
using System.Text;
using TradingHub.Strategies;

namespace TradingHub.App.Lab;

/// <summary>Qué entra en un informe: corridas, portafolio y el análisis escrito (de Claude o del usuario).</summary>
internal sealed class ReportInput
{
    public string Title { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string? StrategyFile { get; set; }
    public string? StrategyName { get; set; }
    public string? Description { get; set; }
    public StrategyRun? Backtest { get; set; }
    public StrategyRun? Optimization { get; set; }
    public StrategyRun? WalkForward { get; set; }
    public PortfolioResult? Portfolio { get; set; }
    public decimal PortfolioCapital { get; set; } = 10_000;
    public string Summary { get; set; } = "";
    public string Analysis { get; set; } = "";
    public string Risks { get; set; } = "";
    public string NextSteps { get; set; } = "";
    public string Extra { get; set; } = "";
}

/// <summary>
/// Arma un informe profesional en Markdown con todas las métricas, tablas y gráficos a partir de las
/// corridas guardadas. Las imágenes se referencian como {{assets}}/archivo.png (las ubica ReportStore).
/// </summary>
internal static class ReportBuilder
{
    private static CultureInfo Es => TradingHub.Common.UiCulture.Current;

    private static string Money(decimal v, bool sign = false) => (sign && v > 0 ? "+" : "") + v.ToString("N2", Es);
    private static string Pct(decimal v, bool sign = false) => (sign && v > 0 ? "+" : "") + v.ToString("0.00", Es) + " %";
    private static string Pct(double v, bool sign = false) => Pct((decimal)(double.IsFinite(v) ? v : 0), sign);
    private static string Num(double v, int dec = 2) => double.IsFinite(v) ? v.ToString("N" + dec, Es) : "—";
    private static string Num(decimal v, int dec = 2) => v.ToString("N" + dec, Es);
    private static string Date(DateTime t) => t.ToString("dd/MM/yyyy", Es);
    private static string DateTimeText(DateTime t) => t.ToString("dd/MM/yyyy HH:mm", Es);
    private static string Ratio(decimal v) => v >= 999 ? "∞" : Num(v);

    private static string Money(decimal? v) => v is { } value ? Money(value) : "Unavailable";
    private static string Pct(double? v) => v is { } value ? Pct(value) : "Unavailable";
    private static string Num(double? v) => v is { } value ? Num(value) : "Unavailable";
    private static string Exact(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";

    private static void ContributionTables(StringBuilder sb, IReadOnlyList<ClosedTrade> contributions)
    {
        sb.AppendLine("### Contribution amounts");
        sb.AppendLine();
        Table(sb, new[] { "#", "Attribution", "Actual closing fill", "Side", "Quantity", "Gross PnL", "Commission", "Attribution net", "Net PnL", "Carry net credit", "Carry commission credit" },
            contributions.Select(t => (IReadOnlyList<string>)new[]
            {
                t.Number.ToString(Es), t.Attribution.ToString(), t.IsActualClosingFill ? "Yes" : "No", t.IsLong ? "Long" : "Short",
                Exact(t.Quantity), Exact(t.Pnl), Exact(t.Commission), Exact(t.AttributionNet), Exact(t.NetPnl), Exact(t.CarryNetCredit), Exact(t.CarryCommissionCredit),
            }));
        sb.AppendLine("### Contribution boundaries");
        sb.AppendLine();
        Table(sb, new[] { "#", "Actual entry", "Entry", "Exit", "Window from", "Window to", "Entry price", "Exit price", "Opening mark", "Closing mark" },
            contributions.Select(t => (IReadOnlyList<string>)new[]
            {
                t.Number.ToString(Es), t.ActualEntryTime?.ToString("O", CultureInfo.InvariantCulture) ?? "Unavailable",
                t.EntryTime.ToString("O", CultureInfo.InvariantCulture), t.ExitTime.ToString("O", CultureInfo.InvariantCulture),
                t.WindowFrom?.ToString("O", CultureInfo.InvariantCulture) ?? "Unavailable", t.WindowTo?.ToString("O", CultureInfo.InvariantCulture) ?? "Unavailable",
                Exact(t.EntryPrice), Exact(t.ExitPrice), Exact(t.OpeningMark), Exact(t.ClosingMark),
            }));
    }

    /// <summary>CAGR y Calmar solo tienen sentido con períodos largos (anualizar 5 días exagera todo).</summary>
    private static string Annualized(TradeAnalytics a, double value, bool percent) =>
        a.Days < 90 ? "— (less than 90 days)" : percent ? Pct(value, true) : Num(value);

    private static string Duration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return $"{d.TotalSeconds:0} s";
        if (d.TotalHours < 1) return $"{d.TotalMinutes:0} min";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h {d.Minutes} min";
        return $"{(int)d.TotalDays} d {d.Hours} h";
    }

    private static string Cell(object? v) => (v switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    }).Replace("|", "/");

    private static void Table(StringBuilder sb, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        sb.AppendLine("| " + string.Join(" | ", headers.Select(TradingHub.Common.EnglishText.Of)) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => "---|")));
        foreach (var r in rows) sb.AppendLine("| " + string.Join(" | ", r.Select(x => TradingHub.Common.EnglishText.Of(x).Replace("|", "/"))) + " |");
        sb.AppendLine();
    }

    private static void KeyValues(StringBuilder sb, IEnumerable<(string Key, string Value)> rows) =>
        Table(sb, new[] { "Item", "Value" }, rows.Select(r => (IReadOnlyList<string>)new[] { r.Key, r.Value }));

    private static void InputEvidenceTable(StringBuilder sb, StrategyRun run) =>
        InputEvidenceTable(sb, run.Id, run.Kind, run.CodeHash, run.InputEvidence);

    private static void InputEvidenceTable(StringBuilder sb, string runId, RunKind? kind, string? codeHash, RunInputEvidence? evidence)
    {
        KeyValues(sb, new[]
        {
            ("Run", runId),
            ("Code version", string.IsNullOrEmpty(codeHash) ? "Unknown" : codeHash),
            ("Run kind", kind?.ToString() ?? "Unknown"),
            ("Input evidence", evidence == null ? "Unknown legacy input evidence" : "Captured from the run input"),
            ("Input quality", evidence?.DataQuality.ToString() ?? "Unknown"),
            ("Resolution (seconds)", evidence?.ResolutionSeconds.ToString(CultureInfo.InvariantCulture) ?? "Unknown"),
            ("Source identity", evidence?.SourceIdentity ?? "Unknown"),
            ("Correction revision", evidence?.CorrectionRevision.ToString(CultureInfo.InvariantCulture) ?? "Unknown"),
            ("Known availability", evidence == null ? "Unknown" : evidence.HasKnownAvailability ? "Yes" : "No"),
            ("Data warning", evidence == null ? "Unknown" : evidence.DataWarning ?? "None"),
            ("Content identity", evidence?.ContentIdentity ?? "Unknown"),
        });
        sb.AppendLine();
    }

    private static TimeZoneInfo Zone(BacktestSettings s)
    {
        if (string.IsNullOrEmpty(s.TimeZoneId)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static string VerdictText(string v) => v switch
    {
        "ganadora" => "PROFITABLE",
        "prometedora" => "PROMISING",
        "perdedora" => "UNPROFITABLE",
        "descartada" => "REJECTED",
        _ => "",
    };

    public static (string Markdown, List<(string Name, byte[] Png)> Images) Build(ReportInput x)
    {
        var sb = new StringBuilder();
        var images = new List<(string, byte[])>();
        var main = x.Backtest;
        var s = main?.Settings ?? x.WalkForward?.Settings ?? x.Optimization?.Settings;
        var walkForward = x.WalkForward?.WalkForward ?? main?.WalkForward;
        int completedWalkForwardWindows = walkForward?.Windows.Count(win => win.InSampleAvailable && win.InSample != null && win.OutSampleAvailable && win.OutSample != null) ?? 0;
        var subject = main ?? x.WalkForward ?? x.Optimization;

        // ----------------------------------------------------------------- encabezado
        sb.AppendLine($"# {x.Title}");
        sb.AppendLine();
        var meta = new List<string>();
        if (x.StrategyName != null) meta.Add($"**Strategy:** {x.StrategyName}{(x.StrategyFile != null ? $" ({x.StrategyFile}.cs)" : "")}");
        if (s != null)
        {
            meta.Add($"**Instrument:** {s.Symbol} ({s.FeedId})");
            meta.Add($"**Bars:** {s.Period}");
            meta.Add($"**Period:** {Date(s.FromUtc)} – {Date(s.ToUtc.AddSeconds(-1))}");
        }
        else if (x.Portfolio != null)
        {
            meta.Add($"**Portfolio:** {x.Portfolio.Components.Count} strategies");
            meta.Add($"**Period:** {Date(x.Portfolio.From)} – {Date(x.Portfolio.To)}");
        }

        meta.Add($"**Generated:** {DateTimeText(DateTime.Now)} with Trading Hub");
        sb.AppendLine("> " + string.Join(" · ", meta));
        sb.AppendLine();
        if (VerdictText(x.Verdict) is { Length: > 0 } vt)
        {
            sb.AppendLine($"> **Verdict: {vt}**{(x.Summary.Length > 0 ? " — " + x.Summary.Trim().Replace("\n", " ") : "")}");
            sb.AppendLine();
        }

        // sujeto principal de las estadísticas: el backtest o el portafolio
        List<ClosedTrade>? trades = main?.Trades;
        List<EquityPoint>? equity = main?.Equity;
        PerformanceMetrics? m = main?.Metrics;
        decimal capital = s?.InitialCapital ?? x.PortfolioCapital;
        if (main == null && x.Portfolio != null)
        {
            trades = x.Portfolio.Trades;
            equity = x.Portfolio.Equity;
            m = x.Portfolio.Metrics;
            capital = x.PortfolioCapital;
        }

        TradeAnalytics? a = null;
        if (trades != null && m != null && trades.All(t => t.HasCompleteTradeEvidence) && m.AttributionUnit == PerformanceAttributionUnit.ClosingFill)
            a = TradeAnalytics.Compute(trades, equity ?? new List<EquityPoint>(), capital, m, s != null ? Zone(s) : TimeZoneInfo.Utc,
                s?.FromUtc ?? x.Portfolio?.From, s?.ToUtc ?? x.Portfolio?.To);

        int section = 0;
        string H2(string title) => $"## {++section}. {title}";

        // ----------------------------------------------------------------- 1. resumen ejecutivo
        sb.AppendLine(H2("Executive summary"));
        sb.AppendLine();
        // el resumen va en el recuadro del veredicto; si no hay veredicto, acá
        if (x.Summary.Length > 0 && VerdictText(x.Verdict).Length == 0)
        {
            sb.AppendLine(x.Summary.Trim());
            sb.AppendLine();
        }

        if (m != null && a != null)
        {
            var wf = x.WalkForward?.WalkForward;
            var mc = main?.MonteCarlo;
            var pairs = new List<(string, string)>
            {
                ("Net profit", Money(m.NetProfit, true)),
                ("Total return", Pct(m.ReturnPercent, true)),
                ("CAGR (annualized)", Annualized(a, a.CagrPercent, true)),
                ("Profit factor", Ratio(m.ProfitFactor)),
                ("Sharpe", Num(m.SharpeRatio)),
                ("Sortino", Num(m.SortinoRatio)),
                ("Maximum drawdown", $"{Money(m.MaxDrawdown)} ({Pct(m.MaxDrawdownPercent)})"),
                ("Calmar", Annualized(a, a.Calmar, false)),
                ("Trades", m.TotalTrades.ToString(Es)),
                ("Win rate", Pct(m.WinRate)),
                ("Expectancy per trade", Money(m.Expectancy, true)),
                ("SQN", Num(a.Sqn)),
            };
            if (wf != null)
            {
                pairs.Add(("Walk-forward efficiency", completedWalkForwardWindows > 0 ? Num(wf.WalkForwardEfficiency) : "Unavailable"));
                pairs.Add(("Profitable windows (WF)", completedWalkForwardWindows > 0 ? Pct(wf.ProfitableWindowsPercent) : "Unavailable"));
            }

            if (mc != null)
            {
                if (mc.Available)
                {
                    pairs.Add(("Drawdown p95 (Monte Carlo)", Money(mc.DrawdownP95)));
                    pairs.Add(("Risk of ruin", Pct(mc.RuinProbabilityPercent)));
                }
                else pairs.Add(("Monte Carlo", mc.UnavailableReason ?? "Unavailable"));
            }

            var rows = new List<IReadOnlyList<string>>();
            for (int i = 0; i < pairs.Count; i += 2)
                rows.Add(new[] { pairs[i].Item1, pairs[i].Item2, i + 1 < pairs.Count ? pairs[i + 1].Item1 : "", i + 1 < pairs.Count ? pairs[i + 1].Item2 : "" });
            Table(sb, new[] { "Indicator", "Value", "Indicator", "Value" }, rows);
        }
        else if (m != null)
        {
            KeyValues(sb, new[]
            {
                ("Net profit", Money(m.NetProfit, true)),
                ("Actual closing fills", m.TotalTrades.ToString(Es)),
                ("Attribution samples", m.AttributionSamples.ToString(Es)),
                ("Profit factor per contribution", Ratio(m.ProfitFactor)),
                ("Win rate per contribution", Pct(m.WinRate)),
                ("Expectancy per contribution", Money(m.Expectancy, true)),
            });
            sb.AppendLine("Closing-fill contributions and final boundary valuations are not independent completed trades. Trade-only confidence, duration and excursion diagnostics are unavailable.");
            sb.AppendLine();
        }

        if (equity is { Count: > 1 })
        {
            var extra = x.Portfolio != null && main == null ? PortfolioCurves(x.Portfolio, capital) : null;
            images.Add(("capital.png", ReportCharts.Equity(equity.Select(p => (p.AvailableAt ?? p.Time, p.Equity)).ToList(), capital, main == null ? "Portfolio equity curve" : "Equity curve and drawdown", extra)));
            sb.AppendLine("![Equity curve and drawdown]({{assets}}/capital.png)");
            sb.AppendLine();
        }

        // ----------------------------------------------------------------- 2. estrategia
        if (subject != null || x.StrategyName != null)
        {
            sb.AppendLine(H2("Strategy"));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(x.Description))
            {
                sb.AppendLine(x.Description.Trim());
                sb.AppendLine();
            }

            var parms = subject?.Parameters;
            if (parms is { Count: > 0 })
            {
                sb.AppendLine(main == null && subject?.Kind == RunKind.Optimization ? "**Best parameters**" : "**Parameters used**");
                sb.AppendLine();
                Table(sb, new[] { "Parameter", "Value" }, parms.Select(kv => (IReadOnlyList<string>)new[] { kv.Key, Cell(kv.Value) }));
            }

            var hash = subject?.CodeHash;
            if (!string.IsNullOrEmpty(hash)) sb.AppendLine($"Code version: `{hash}` (retained in the strategy history).\n");
        }

        // ----------------------------------------------------------------- 3. datos y supuestos
        if (main != null && s != null)
        {
            sb.AppendLine(H2("Data and assumptions"));
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Instrument", $"{s.Symbol} ({s.FeedId})"),
                ("Candles", s.Period),
                ("From", DateTimeText(s.FromUtc) + " UTC"),
                ("To", DateTimeText(s.ToUtc) + " UTC"),
                ("Calendar days", Num((s.ToUtc - s.FromUtc).TotalDays, 1)),
                ("Ticks processed", main.Ticks.ToString("N0", Es)),
                ("Bars processed", main.Bars.ToString("N0", Es)),
                ("Initial capital", Money(s.InitialCapital)),
                ("Commission", s.CommissionPercent > 0 ? $"{s.CommissionPercent.ToString("0.####", Es)} % of notional per side" : $"{Money(s.CommissionPerUnit)} per unit"),
                ("Slippage", $"{s.SlippageTicks} tick(s) on market and stop orders"),
                ("Limit orders", s.LimitFill == LimitFillMode.TradeThrough ? "filled only when price trades through" : "filled when price touches"),
                ("Session time zone", string.IsNullOrEmpty(s.TimeZoneId) ? "UTC" : s.TimeZoneId),
                ("Session start", s.SessionStart.ToString(@"hh\:mm", Es)),
                ("Final position", s.CloseAtEnd ? "closed at the end of the period" : "remains open"),
                ("Computation time", $"{Num(main.Seconds, 1)} s"),
            });
        }

        // ----------------------------------------------------------------- 4. rendimiento
        if (m != null && a != null)
        {
            sb.AppendLine(H2("Performance"));
            sb.AppendLine();
            sb.AppendLine("### Profitability");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Net profit", Money(m.NetProfit, true)),
                ("Gross profit", Money(m.GrossProfit)),
                ("Gross loss", Money(-m.GrossLoss)),
                ("Commissions paid", Money(m.Commission)),
                ("Final capital", Money(a.FinalEquity)),
                ("Total return", Pct(m.ReturnPercent, true)),
                ("CAGR (annualized)", Annualized(a, a.CagrPercent, true)),
                ("Profit factor", Ratio(m.ProfitFactor)),
                ("Expectancy per trade", Money(m.Expectancy, true)),
                ("Trading days", a.TradingDays.ToString(Es)),
                ("Average PnL per trading day", Money(a.AverageDailyPnl, true)),
                ("Best day", Money(a.BestDay, true)),
                ("Worst day", Money(a.WorstDay, true)),
                ("Profitable days", Pct(a.ProfitableDaysPercent)),
            });

            sb.AppendLine("### Trades");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Trades", m.TotalTrades.ToString(Es)),
                ("Winners / losers", $"{m.Winners} / {m.Losers}"),
                ("Win rate", Pct(m.WinRate)),
                ("Average PnL", Money(m.AverageTrade, true)),
                ("Average winning trade", Money(m.AverageWin, true)),
                ("Average losing trade", Money(m.AverageLoss, true)),
                ("Average win/loss ratio", Num(m.PayoffRatio)),
                ("Largest winning trade", Money(m.LargestWin, true)),
                ("Largest losing trade", Money(m.LargestLoss, true)),
                ("Longest winning streak", m.MaxConsecutiveWins.ToString(Es)),
                ("Longest losing streak", m.MaxConsecutiveLosses.ToString(Es)),
                ("Standard deviation per trade", Money(a.TradeStdDev)),
                ("Trades per day", Num(a.TradesPerDay)),
                ("Average / median / maximum duration", $"{Duration(a.AverageDuration)} / {Duration(a.MedianDuration)} / {Duration(a.MaxDuration)}"),
                ("Average winning / losing trade duration", $"{Duration(a.AverageWinDuration)} / {Duration(a.AverageLossDuration)}"),
                ("Average bars in winning / losing trades", $"{Num(a.AverageBarsWin, 1)} / {Num(a.AverageBarsLoss, 1)}"),
                ("Time in market", m.ExposurePercent > 0 ? Pct(m.ExposurePercent) : "—"),
            });

            sb.AppendLine("### Risk");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Maximum drawdown", $"{Money(m.MaxDrawdown)} ({Pct(m.MaxDrawdownPercent)})"),
                ("Longest drawdown (closed trades)", $"{Num(a.MaxDrawdownDays, 1)} days"),
                ("Longest period without a new high", $"{Num(a.LongestFlatDays, 1)} days"),
                ("Recovery factor", Ratio(m.RecoveryFactor)),
                ("Sharpe (annualized)", Num(m.SharpeRatio)),
                ("Sortino (annualized)", Num(m.SortinoRatio)),
                ("Calmar", Annualized(a, a.Calmar, false)),
                ("Ulcer index", Num(a.UlcerIndex)),
                ("SQN (system quality)", Num(a.Sqn)),
                ("Kelly", Pct(a.KellyPercent)),
                ("Average MAE (adverse excursion)", Money(a.AverageMae)),
                ("Average MFE (favorable excursion)", Money(a.AverageMfe)),
                ("Edge ratio (MFE / MAE)", Num(a.EdgeRatio)),
            });

            sb.AppendLine("### Long and short trades");
            sb.AppendLine();
            var all = new BucketStat { Label = "Total" };
            foreach (var t in trades!) all.Add(t);
            var cols = new[] { a.Long, a.Short, all };
            Table(sb, new[] { "", "Long trades", "Short trades", "Total" }, new IReadOnlyList<string>[]
            {
                new[] { "Trades" }.Concat(cols.Select(c => c.Trades.ToString(Es))).ToArray(),
                new[] { "Win rate" }.Concat(cols.Select(c => Pct(c.WinRate))).ToArray(),
                new[] { "Net PnL" }.Concat(cols.Select(c => Money(c.Net, true))).ToArray(),
                new[] { "Profit factor" }.Concat(cols.Select(c => Ratio(c.ProfitFactor))).ToArray(),
                new[] { "Average" }.Concat(cols.Select(c => Money(c.Average, true))).ToArray(),
                new[] { "Largest winning trade" }.Concat(cols.Select(c => Money(c.LargestWin, true))).ToArray(),
                new[] { "Largest losing trade" }.Concat(cols.Select(c => Money(c.LargestLoss, true))).ToArray(),
            });

            // ----------------------------------------------------------------- 5. mensual
            if (a.Monthly.Count > 0)
            {
                sb.AppendLine(H2("Monthly performance"));
                sb.AppendLine();
                images.Add(("mensual.png", ReportCharts.Monthly(a.Monthly)));
                sb.AppendLine("![Monthly performance]({{assets}}/mensual.png)");
                sb.AppendLine();
                string[] months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
                Table(sb, new[] { "Year" }.Concat(months).Append("Year").ToArray(),
                    a.Monthly.Select(y => (IReadOnlyList<string>)new[] { y.Year.ToString(CultureInfo.InvariantCulture) }
                        .Concat(y.MonthPercent.Select(v => v.HasValue ? Pct(v.Value, true) : ""))
                        .Append(Pct(y.YearPercent, true)).ToArray()));
            }

            // ----------------------------------------------------------------- 6. operaciones
            sb.AppendLine(H2("Trade analysis"));
            sb.AppendLine();
            if (a.Histogram.Count > 0)
            {
                images.Add(("distribucion.png", ReportCharts.Histogram(a.Histogram)));
                sb.AppendLine("![PnL distribution]({{assets}}/distribucion.png)");
                sb.AppendLine();
            }

            if (a.ByHour.Count > 0)
            {
                sb.AppendLine($"### By entry hour ({(s == null || string.IsNullOrEmpty(s.TimeZoneId) ? "UTC" : s.TimeZoneId)})");
                sb.AppendLine();
                Table(sb, new[] { "Time", "Trades", "Win rate", "Net", "Profit factor", "Average" },
                    a.ByHour.Select(b => (IReadOnlyList<string>)new[] { b.Label, b.Trades.ToString(Es), Pct(b.WinRate), Money(b.Net, true), Ratio(b.ProfitFactor), Money(b.Average, true) }));
            }

            if (a.ByWeekday.Count > 0)
            {
                sb.AppendLine("### By weekday");
                sb.AppendLine();
                Table(sb, new[] { "Day", "Trades", "Win rate", "Net", "Profit factor", "Average" },
                    a.ByWeekday.Select(b => (IReadOnlyList<string>)new[] { b.Label, b.Trades.ToString(Es), Pct(b.WinRate), Money(b.Net, true), Ratio(b.ProfitFactor), Money(b.Average, true) }));
            }

            IReadOnlyList<string> TradeRow(ClosedTrade t) => new[]
            {
                t.Number.ToString(Es), t.IsLong ? "Long" : "Short", DateTimeText(t.EntryTime), DateTimeText(t.ExitTime),
                t.EntryPrice.ToString("0.########", Es), t.ExitPrice.ToString("0.########", Es), Money(t.NetPnl, true), Money(t.Mae), Money(t.Mfe), t.Bars.ToString(Es),
            };
            string[] tradeHeaders = { "#", "Side", "Entry", "Exit", "Entry price", "Exit price", "PnL", "MAE", "MFE", "Candles" };
            if (a.Best.Count > 0)
            {
                sb.AppendLine("### Best trades");
                sb.AppendLine();
                Table(sb, tradeHeaders, a.Best.Select(TradeRow));
                sb.AppendLine("### Worst trades");
                sb.AppendLine();
                Table(sb, tradeHeaders, a.Worst.Select(TradeRow));
            }

            // ----------------------------------------------------------------- 7. drawdowns
            if (a.Drawdowns.Count > 0)
            {
                sb.AppendLine(H2("Largest drawdowns"));
                sb.AppendLine();
                Table(sb, new[] { "#", "Start", "Minimum", "Recovery", "Depth", "%", "Days" },
                    a.Drawdowns.Select((d, i) => (IReadOnlyList<string>)new[]
                    {
                        (i + 1).ToString(Es), DateTimeText(d.Start), DateTimeText(d.Trough), d.Recovery is { } r ? DateTimeText(r) : "not recovered",
                        Money(-d.Depth), Pct(-d.DepthPercent), Num(d.Days, 1),
                    }));
            }
        }

        foreach (var run in new[] { x.Backtest, x.Optimization, x.WalkForward }.OfType<StrategyRun>().DistinctBy(run => run.Id))
        {
            sb.AppendLine(H2("Input evidence"));
            sb.AppendLine();
            InputEvidenceTable(sb, run);
        }

        // ----------------------------------------------------------------- 8. optimización
        if (x.Optimization?.Optimization is { } opt)
        {
            sb.AppendLine(H2("Optimization"));
            sb.AppendLine();
            var enabled = opt.Ranges.Where(r => r.Enabled).ToList();
            var population = opt.GetPopulation();
            KeyValues(sb, new[]
            {
                ("Optimized metric", opt.Metric.ToString()),
                ("Combinations tested", opt.Combinations.ToString("N0", Es)),
                ("Profitable combinations", population?.ProfitablePercent is { } share ? Pct(share) : "Unavailable"),
                ("Measured combinations", population?.MeasuredCombinations.ToString("N0", Es) ?? "Unavailable"),
                ("Retained rows", opt.Rows.Count.ToString("N0", Es)),
                ("Time", $"{Num(opt.Seconds, 1)} s"),
            }.Concat(enabled.Select(r => ($"Range from {r.Label}", $"{Num(r.Min, 4)} to {Num(r.Max, 4)} in steps of {Num(r.Step, 4)}"))));

            sb.AppendLine("### Best combinations");
            sb.AppendLine();
            var names = enabled.Select(r => r.Name).ToList();
            Table(sb, new[] { "#" }.Concat(enabled.Select(r => r.Label)).Concat(new[] { "Net", "PF", "Sharpe", "Max DD", "Ops", "Win rate" }).ToArray(),
                opt.Rows.Take(10).Select((r, i) => (IReadOnlyList<string>)new[] { (i + 1).ToString(Es) }
                    .Concat(names.Select(n => Cell(r.Parameters.GetValueOrDefault(n))))
                    .Concat(new[] { Money(r.Metrics.NetProfit, true), Ratio(r.Metrics.ProfitFactor), Num(r.Metrics.SharpeRatio), Money(r.Metrics.MaxDrawdown), r.Metrics.TotalTrades.ToString(Es), Pct(r.Metrics.WinRate) })
                    .ToArray()));

            sb.AppendLine("### Recommended combination");
            sb.AppendLine();
            if (opt.Recommended is { } recommended)
            {
                KeyValues(sb, recommended.Parameters.Select(pair => (pair.Key, Cell(pair.Value))).Concat(new[]
                {
                    ("Net profit", Money(recommended.Metrics.NetProfit, true)),
                    ("Profit factor", Ratio(recommended.Metrics.ProfitFactor)),
                    ("Sharpe", Num(recommended.Metrics.SharpeRatio)),
                    ("Maximum drawdown", Money(recommended.Metrics.MaxDrawdown)),
                }));
            }
            else sb.AppendLine("The exact recommended combination is unavailable.");
            sb.AppendLine();

            sb.AppendLine("### Parameter sensitivity");
            sb.AppendLine();
            sb.AppendLine(population == null ? "Complete-population sensitivity is unavailable for this truncated legacy run."
                : "Average of all measured combinations at each value. A broad plateau suggests robustness; an isolated peak suggests overfitting.");
            sb.AppendLine();
            foreach (var r in enabled.Where(_ => population != null))
            {
                sb.AppendLine($"**{r.Label}**");
                sb.AppendLine();
                var groups = population!.Parameters.FirstOrDefault(parameter => parameter.Name == r.Name)?.Values ?? new();
                Table(sb, new[] { "Value", "Combinations", "Profitable", "Average net PnL", "Average Sharpe" },
                    groups.OrderBy(group => double.TryParse(group.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0)
                        .ThenBy(group => group.Value, StringComparer.Ordinal).Select(group => (IReadOnlyList<string>)new[]
                    {
                        group.Value, group.Combinations.ToString(Es), group.ProfitablePercent is { } profitable ? Pct(profitable) : "Unavailable",
                        group.AverageNetProfit is { } average ? Money(average, true) : "Unavailable", Num(group.AverageSharpe),
                    }));
            }
        }

        // ----------------------------------------------------------------- 9. walk-forward
        if (walkForward is { } w)
        {
            sb.AppendLine(H2("Walk-forward (out-of-sample validation)"));
            sb.AppendLine();
            var om = w.OutSampleMetrics;
            bool available = completedWalkForwardWindows > 0;
            bool attributed = w.AccountingPolicy != null || om.AttributionUnit == PerformanceAttributionUnit.WindowClosingFillAndBoundaryValuation;
            KeyValues(sb, new[]
            {
                ("Coverage", w.IsComplete && available ? "Complete" : available ? "Completed prefix only" : "Unavailable"),
                ("Completed windows", $"{completedWalkForwardWindows} of {w.Windows.Count}"),
                ("Walk-forward windows", $"{w.Windows.Count} · {w.InSampleDays} optimization days and {w.OutSampleDays} test days ({(w.Anchored ? "anchored" : "rolling")})"),
                ("Metric", w.Metric.ToString()),
                ("Accounting policy", w.AccountingPolicy ?? "Unknown"),
                ("Attribution policy", w.AttributionPolicy ?? "Unknown"),
                ("CPCV policy", w.CpcvPolicy ?? "Unknown"),
                ("Walk-forward efficiency", available ? Num(w.WalkForwardEfficiency) : "Unavailable"),
                ("Profitable windows", available ? Pct(w.ProfitableWindowsPercent) : "Unavailable"),
                ("Out-of-sample net PnL", available ? Money(om.NetProfit, true) : "Unavailable"),
                (attributed ? "Profit factor per contribution" : "Out-of-sample profit factor", available ? Ratio(om.ProfitFactor) : "Unavailable"),
                ("Out-of-sample Sharpe", available ? Num(om.SharpeRatio) : "Unavailable"),
                ("Out-of-sample maximum drawdown", available ? $"{Money(om.MaxDrawdown)} ({Pct(om.MaxDrawdownPercent)})" : "Unavailable"),
                (attributed ? "Actual out-of-sample closing fills" : "Out-of-sample trades", available ? om.TotalTrades.ToString(Es) : "Unavailable"),
                (attributed ? "Win rate per contribution" : "Out-of-sample win rate", available ? Pct(om.WinRate) : "Unavailable"),
                (attributed ? "Expectancy per contribution" : "Expectancy per trade", available ? Money(om.Expectancy, true) : "Unavailable"),
            });
            if (attributed)
            {
                sb.AppendLine("This warm mark-to-market research splice is not an executable portfolio. Selected configurations retain their own warm state, positions and capital. The splice does not transfer outgoing positions or charge deployment turnover.");
                sb.AppendLine();
                sb.AppendLine("Profit factor, win rate and expectancy use closing-fill contributions and final boundary valuations. Eligibility counts only actual closing fills. Boundary marks are not executions or independent completed trades.");
                sb.AppendLine();
                KeyValues(sb, new[]
                {
                    ("Attribution unit", om.AttributionUnit.ToString()),
                    ("Attribution samples", available ? om.AttributionSamples.ToString(Es) : "Unavailable"),
                    ("Trade-only confidence", "Unavailable for marked window contributions"),
                });
            }
            Table(sb, new[] { "Window", "Optimization", "Test", "Selected parameters", "Net (optimization)", "Net (test)", attributed ? "Actual closing fills" : "Test trades", "Efficiency", "Unavailable reason" },
                w.Windows.Select(win => (IReadOnlyList<string>)new[]
                {
                    win.Number.ToString(Es), $"{Date(win.InSampleFrom)} to {Date(win.InSampleTo)}", $"{Date(win.OutSampleFrom)} to {Date(win.OutSampleTo)}",
                    string.Join(", ", win.BestParameters.Select(kv => $"{TradingHub.Common.EnglishText.Of(kv.Key)}={Cell(kv.Value)}")),
                    win.InSampleAvailable && win.InSample is { } im ? Money(im.NetProfit, true) : "Unavailable",
                    win.OutSampleAvailable && win.OutSample is { } osm ? Money(osm.NetProfit, true) : "Unavailable",
                    win.OutSampleAvailable && win.OutSample != null ? win.OutSample.TotalTrades.ToString(Es) : "Unavailable",
                    win.InSampleAvailable && win.OutSampleAvailable ? Num(win.Efficiency) : "Unavailable", win.UnavailableReason ?? "",
                }));
            if (attributed)
            {
                sb.AppendLine("### Boundary equity");
                sb.AppendLine();
                sb.AppendLine("Source book equity belongs to each warm configuration. Stitched equity rebases its measured change onto the completed prefix. Amounts below retain their exact decimals.");
                sb.AppendLine();
                Table(sb, new[] { "Window", "Training opening", "Training closing", "Test source opening", "Test source closing", "Stitched opening", "Stitched closing" },
                    w.Windows.Select(win => (IReadOnlyList<string>)new[]
                    {
                        win.Number.ToString(Es), Exact(win.InSampleOpeningEquity), Exact(win.InSampleClosingEquity),
                        Exact(win.OutSampleOpeningEquity), Exact(win.OutSampleClosingEquity),
                        Exact(win.OutSampleStitchedOpeningEquity), Exact(win.OutSampleStitchedClosingEquity),
                    }));
                ContributionTables(sb, w.OutSampleTrades);
            }
            if (w.OutSampleEquity.Count > 1)
            {
                images.Add(("walkforward.png", ReportCharts.Equity(w.OutSampleEquity.Select(p => (p.AvailableAt ?? p.Time, p.Equity)).ToList(), w.OutSampleEquity[0].Equity, "Out-of-sample equity (stitched)")));
                sb.AppendLine("![Out-of-sample equity]({{assets}}/walkforward.png)");
                sb.AppendLine();
            }
            var availablePaths = w.CpcvPaths.Where(p => p.Available && p.Net.HasValue && p.Sharpe.HasValue && p.MaxDrawdown.HasValue).OrderBy(p => p.Net).ToList();
            sb.AppendLine("### Retrospective CPCV");
            sb.AppendLine();
            sb.AppendLine("Retrospective purged cross-validation of warm marked ranges is not a forward-only executable handoff. Shares and medians use only available paths.");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Available paths", $"{availablePaths.Count} of {w.CpcvPaths.Count}"),
                ("Groups", w.CpcvGroups.ToString(Es)),
                ("Embargo days", w.EmbargoDays.ToString(Es)),
                ("Profitable paths", availablePaths.Count > 0 ? $"{availablePaths.Count(p => p.Net > 0)} of {availablePaths.Count}" : "Unavailable"),
                ("Profitable path share", availablePaths.Count > 0 ? Pct(100.0 * availablePaths.Count(p => p.Net > 0) / availablePaths.Count) : "Unavailable"),
                ("Median path net PnL", availablePaths.Count > 0 ? Exact(availablePaths[availablePaths.Count / 2].Net) : "Unavailable"),
            });
            if (availablePaths.Count == 0)
            {
                sb.AppendLine("No eligible retrospective CPCV path is available.");
                sb.AppendLine();
            }
            Table(sb, new[] { "Path", "Available", "Net PnL", "Sharpe", "Maximum drawdown", "Actual closing fills", "Days", "Unavailable reason" },
                w.CpcvPaths.Select(p => (IReadOnlyList<string>)new[]
                {
                    p.Number.ToString(Es), p.Available ? "Yes" : "No", p.Available ? Exact(p.Net) : "Unavailable",
                    p.Available ? Num(p.Sharpe) : "Unavailable", p.Available ? Exact(p.MaxDrawdown) : "Unavailable",
                    p.Available ? p.ActualClosingFills?.ToString(Es) ?? "Unavailable" : "Unavailable", p.Days.ToString(Es), p.UnavailableReason ?? "",
                }));
            var trainingWindows = w.Windows.Where(win => win.InSampleAvailable && win.InSample != null).ToList();
            var paramNames = trainingWindows.SelectMany(win => win.BestParameters.Keys).Distinct().ToList();
            var varying = paramNames.Where(p => trainingWindows.Select(win => Cell(win.BestParameters.GetValueOrDefault(p))).Distinct().Count() > 1).ToList();
            if (varying.Count > 0)
            {
                sb.AppendLine("### Parameter stability");
                sb.AppendLine();
                Table(sb, new[] { "Parameter", "Selected values (count)" }, varying.Select(p => (IReadOnlyList<string>)new[]
                {
                    p, string.Join(" · ", trainingWindows.GroupBy(win => Cell(win.BestParameters.GetValueOrDefault(p))).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})")),
                }));
            }
        }

        // ----------------------------------------------------------------- 10. Monte Carlo
        if ((main?.MonteCarlo ?? x.WalkForward?.MonteCarlo) is { } mcr)
        {
            sb.AppendLine(H2("Monte Carlo"));
            sb.AppendLine();
            if (!mcr.Available)
            {
                sb.AppendLine(mcr.UnavailableReason ?? "Monte Carlo is unavailable.");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"The {main?.Trades?.Count ?? x.WalkForward?.Trades?.Count ?? 0} trades were randomly reordered {mcr.Runs:N0} times to estimate possible drawdowns with the same edge.");
                sb.AppendLine();
                KeyValues(sb, new[]
                {
                    ("Simulations", mcr.Runs.ToString("N0", Es)),
                    ("Median drawdown", Money(mcr.DrawdownP50)),
                    ("Drawdown p95", Money(mcr.DrawdownP95)),
                    ("Drawdown p99", Money(mcr.DrawdownP99)),
                    ("Worst simulated drawdown", Money(mcr.WorstDrawdown)),
                    ("Risk of ruin (drawdown ≥ 50% of capital)", Pct(mcr.RuinProbabilityPercent)),
                    ("Suggested capital (2 × p99 drawdown)", Money(mcr.DrawdownP99 * 2)),
                });
                if (mcr.SampleCurves.Count > 0)
                {
                    images.Add(("montecarlo.png", ReportCharts.Curves(mcr.SampleCurves.Select(cv => (IReadOnlyList<decimal>)cv).ToList(), capital, "Monte Carlo: equity paths with reordered trades")));
                    sb.AppendLine("![Monte Carlo]({{assets}}/montecarlo.png)");
                    sb.AppendLine();
                }
            }
        }

        // ----------------------------------------------------------------- 11. portafolio
        if (x.Portfolio is { } pf && pf.Components.Count > 0)
        {
            sb.AppendLine(H2("Portfolio"));
            sb.AppendLine();
            if (pf.NoOverlap) sb.AppendLine("> Strategies have no common dates: periods were combined and correlation is unreliable.\n");
            if (pf.Components.Any(c => c.ResearchCoverage != null))
            {
                sb.AppendLine("Includes warm mark-to-market research contributions, not an executable portfolio. Only common available OOS coverage is combined. Partial components retain their completed prefix.");
                sb.AppendLine();
                Table(sb, new[] { "Component", "Available from", "Available to", "Completed windows", "Coverage", "Accounting policy", "Attribution policy", "CPCV policy" },
                    pf.Components.Where(c => c.ResearchCoverage != null).Select(c =>
                    {
                        var coverage = c.ResearchCoverage!;
                        return (IReadOnlyList<string>)new[]
                        {
                            c.Label, coverage.From.ToString("O", CultureInfo.InvariantCulture), coverage.To.ToString("O", CultureInfo.InvariantCulture),
                            $"{coverage.CompletedWindows} of {coverage.TotalWindows}", coverage.IsComplete ? "Complete" : "Completed prefix only",
                            coverage.AccountingPolicy ?? "Unknown", coverage.AttributionPolicy ?? "Unknown", coverage.CpcvPolicy ?? "Unknown",
                        };
                    }));
                ContributionTables(sb, pf.Trades);
            }
            Table(sb, new[] { "#", "Strategy", "Instrument", "Weight", "Net", "Sharpe", "Max DD", pf.Metrics.AttributionUnit == PerformanceAttributionUnit.WindowClosingFillAndBoundaryValuation ? "Actual closing fills" : "Ops" },
                pf.Components.Select((c, i) => (IReadOnlyList<string>)new[]
                {
                    (i + 1).ToString(Es), c.StrategyName, $"{c.Settings.Symbol} {c.Settings.Period}", Num(c.Weight),
                    Money(pf.ComponentMetrics[i].NetProfit, true), Num(pf.ComponentMetrics[i].SharpeRatio), Money(pf.ComponentMetrics[i].MaxDrawdown), pf.ComponentMetrics[i].TotalTrades.ToString(Es),
                }));
            sb.AppendLine("### Component input evidence");
            sb.AppendLine();
            foreach (var component in pf.Components)
            {
                sb.AppendLine($"**{component.Label}**");
                sb.AppendLine();
                InputEvidenceTable(sb, component.RunId, component.Kind, component.CodeHash, component.InputEvidence);
            }
            KeyValues(sb, new[]
            {
                ("Common period", $"{Date(pf.From)} – {Date(pf.To)}"),
                ("Portfolio net PnL", Money(pf.Metrics.NetProfit, true)),
                ("Portfolio Sharpe / average strategy Sharpe", $"{Num(pf.Metrics.SharpeRatio)} / {Num(pf.AverageComponentSharpe)}"),
                ("Portfolio drawdown / sum of individual drawdowns", $"{Money(pf.Metrics.MaxDrawdown)} / {Money(pf.SumOfComponentDrawdowns)}"),
                ("Drawdown reduction through diversification", Pct(pf.DrawdownReductionPercent)),
                ("Average / maximum correlation", $"{Num(pf.AverageCorrelation)} / {Num(pf.MaxCorrelation)}"),
            });
            var labels = pf.Components.Select(c => c.Label).ToList();
            images.Add(("correlacion.png", ReportCharts.Correlation(labels, pf.Correlation)));
            sb.AppendLine("![Correlation]({{assets}}/correlacion.png)");
            sb.AppendLine();
            if (main != null)
            {
                images.Add(("portafolio.png", ReportCharts.Equity(pf.Equity.Select(p => (p.AvailableAt ?? p.Time, p.Equity)).ToList(), x.PortfolioCapital, "Portfolio equity curve", PortfolioCurves(pf, x.PortfolioCapital))));
                sb.AppendLine("![Portfolio equity curve]({{assets}}/portafolio.png)");
                sb.AppendLine();
            }
        }

        // ----------------------------------------------------------------- análisis escrito
        void Text(string title, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            sb.AppendLine(H2(title));
            sb.AppendLine();
            sb.AppendLine(body.Trim());
            sb.AppendLine();
        }

        Text("Analysis", x.Analysis);
        Text("Risks", x.Risks);
        Text("Next steps", x.NextSteps);
        if (!string.IsNullOrWhiteSpace(x.Extra))
        {
            sb.AppendLine(x.Extra.Trim());
            sb.AppendLine();
        }

        // ----------------------------------------------------------------- glosario
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("### Glossary");
        sb.AppendLine();
        foreach (var (k, v) in new[]
                 {
                     ("Profit factor", "gross profit divided by gross loss. Above 1.5 is good; above 2 is very good."),
                     ("Sharpe / Sortino", "average daily return divided by volatility (Sortino penalizes only downside), annualized."),
                     ("CAGR", "equivalent compound annual growth."),
                     ("Calmar", "CAGR divided by maximum percentage drawdown."),
                     ("Drawdown", "decline from the previous equity-curve high."),
                     ("SQN", "System Quality Number (Van Tharp): average PnL divided by its standard deviation, multiplied by the square root of the trade count (capped at 100). Above 2 is good."),
                     ("Ulcer index", "depth and duration of drawdowns (lower is better)."),
                     ("Kelly", "fraction of capital maximizing growth given win rate and win/loss ratio (in practice, use a fraction of this estimate)."),
                     ("MAE / MFE", "maximum adverse / favorable excursion of each trade before it closed."),
                     ("Walk-forward efficiency", "out-of-sample performance divided by optimization performance (per day). Above 0.5 suggests less overfitting."),
                     ("Monte Carlo", "randomly reorders trades to estimate possible drawdowns with the same edge."),
                 })
            sb.AppendLine($"- **{k}:** {v}");
        sb.AppendLine();
        sb.AppendLine("> Simulated results on historical data, including commissions and slippage. They do not guarantee future performance.");
        return (sb.ToString(), images);
    }

    private static List<(string Label, IReadOnlyList<(DateTime Time, decimal Equity)> Points)> PortfolioCurves(PortfolioResult pf, decimal capital)
    {
        var res = new List<(string, IReadOnlyList<(DateTime, decimal)>)>();
        for (int i = 0; i < pf.Components.Count; i++)
        {
            decimal eq = capital;
            var pts = new List<(DateTime, decimal)> { (pf.From, capital) };
            for (int k = 0; k < pf.Days.Count; k++)
            {
                eq += pf.DailyPnl[i][k];
                pts.Add((pf.Days[k].ToDateTime(new TimeOnly(23, 59)), eq));
            }

            res.Add((pf.Components[i].Label, pts));
        }

        return res;
    }
}
