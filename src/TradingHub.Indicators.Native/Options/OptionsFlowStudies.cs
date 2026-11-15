// Behavior adapted from the KVP capability reference (commits 7fbf237, 644b8ca); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds.External;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Options;

// Studies of option trades (flow). A trade is bullish when it buys calls or sells puts and bearish when it buys puts or
// sells calls; unknown aggressor sides count as neither. Premium = price × contracts × multiplier (USD). Each bar uses
// only trades executed inside it, so closed bars never change; bars outside the loaded trade range are gaps, not zero.

/// <summary>Premium totals of a set of trades.</summary>
internal readonly record struct FlowTotals(double CallPremium, double PutPremium, double Bullish, double Bearish, double Bought, double Sold, int Count)
{
    public static FlowTotals Of(IEnumerable<OptionTrade> trades)
    {
        double cp = 0, pp = 0, bu = 0, be = 0, bo = 0, so = 0;
        int n = 0;
        foreach (var t in trades)
        {
            n++;
            double p = t.Premium;
            if (t.Right == OptionRight.Call) cp += p;
            else pp += p;
            if (t.Bias > 0) bu += p;
            else if (t.Bias < 0) be += p;
            if (t.Aggressor > 0) bo += p;
            else if (t.Aggressor < 0) so += p;
        }

        return new FlowTotals(cp, pp, bu, be, bo, so, n);
    }
}

[DisplayName("Option premium footprint by strike")]
[Category(IndicatorCategories.Options)]
[Description("Bar by bar, the option premium traded at each strike, drawn at the strike's price: green when the flow was bullish (bought calls or sold puts), red when bearish, stronger with more premium. Shows the strikes traders keep returning to.")]
public class StrikeFootprint : OptionsOverlay
{
    private readonly List<(int Bar, double Strike, double Premium, double Bias)> _cells = new();
    private double _max, _step;

    public StrikeFootprint() => ShowCard = false;

    [Display(Name = "Strikes per bar (0 = all)", GroupName = "Calculation", Order = 10)]
    [Range(0, 100)]
    public int TopStrikes { get; set; } = 4;

    [Display(Name = "Bars drawn", GroupName = "Calculation", Order = 11)]
    [Range(20, 3000)]
    public int MaxDrawnBars { get; set; } = 400;

    [Display(Name = "Maximum distance from price (%)", GroupName = "Calculation", Order = 12)]
    [Range(1, 100)]
    public decimal MaxDistancePct { get; set; } = 15;

    [Display(Name = "As a heatmap", GroupName = "Drawing", Order = 20)]
    public bool Heatmap { get; set; }

    [Display(Name = "Intensity", GroupName = "Drawing", Order = 21)]
    [Range(10, 255)]
    public int Alpha { get; set; } = 200;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        _cells.Clear();
        _max = 0;
        int first = Math.Max(0, LastBar - MaxDrawnBars);
        var strikes = new SortedSet<double>();
        for (int i = first; i <= LastBar; i++)
        {
            var tr = TradesOf(data, i);
            if (tr.Count == 0) continue;
            double px = Close(i);
            var by = tr.Where(t => Math.Abs(t.Strike / px - 1) * 100 <= (double)MaxDistancePct).GroupBy(t => t.Strike)
                .Select(g => (Strike: g.Key, Premium: g.Sum(t => t.Premium), Bias: g.Sum(t => t.Bias * t.Premium)))
                .OrderByDescending(x => x.Premium).ThenBy(x => x.Strike);
            foreach (var c in TopStrikes > 0 ? by.Take(TopStrikes) : by)
            {
                _cells.Add((i, c.Strike, c.Premium, c.Bias));
                _max = Math.Max(_max, c.Premium);
                strikes.Add(c.Strike);
            }
        }

        // Scale on the 90th percentile so one huge trade does not dim everything else.
        if (_cells.Count > 10) _max = _cells.Select(c => c.Premium).OrderBy(v => v).ElementAt((int)(_cells.Count * 0.9));
        _step = Opt.StrikeStep(strikes, Close(LastBar));
        if (_cells.Count == 0 && data.TradeCoverage(Underlying) == null) Status = "Loading option trades…";
    }

    protected override void RenderBefore(RenderContext ctx, Rectangle region)
    {
        if (_max <= 0) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        int bw = ChartInfo.PriceChartContainer.BarsWidth;
        foreach (var c in _cells)
        {
            if (c.Bar < first || c.Bar > last) continue;
            double f = Math.Min(1, Math.Sqrt(c.Premium / _max));
            int x0 = X(c.Bar), x1 = X(c.Bar + 1);
            int y0 = Y(c.Strike + _step / 2), y1 = Y(c.Strike - _step / 2);
            var col = c.Bias > 0 ? Opt.Call : c.Bias < 0 ? Opt.Put : Opt.Muted;
            var rect = Heatmap
                ? new Rectangle(x0, Math.Min(y0, y1), Math.Max(1, x1 - x0), Math.Max(1, Math.Abs(y1 - y0)))
                : new Rectangle(x0, Y(c.Strike) - 5, Math.Max(3, x1 - x0 - 1), 10);
            ctx.FillRectangle(Opt.A(col, (int)(Alpha * Math.Max(0.3, f))), rect);
            if (!Heatmap && bw >= 34) ctx.DrawString(Opt.Usd(c.Premium).Replace("$", ""), StudyCard.Font, Color.White, rect.Left + 1, rect.Top - 1);
        }
    }
}

[DisplayName("Option premium by strike heatmap")]
[Category(IndicatorCategories.Options)]
[Description("A heatmap of the option premium at every strike near the price, bar by bar: green where the flow was bullish, red where bearish. Shows the prices traders defend or target.")]
public class PremiumByStrike : StrikeFootprint
{
    public PremiumByStrike()
    {
        TopStrikes = 0;
        Heatmap = true;
        Alpha = 120;
    }
}

[DisplayName("Options at price")]
[Category(IndicatorCategories.Options)]
[Description("A profile of option premium stacked at the underlying price when each trade executed (1 to 30 days): where traders bet hardest. Green is the bullish part, red the bearish part; a line marks the price with the most premium.")]
public class OptionsAtPrice : OptionsOverlay
{
    private double[] _bull = Array.Empty<double>(), _bear = Array.Empty<double>();
    private double _low, _row;

    [Display(Name = "Days", GroupName = "Calculation", Order = 10)]
    [Range(1, 30)]
    public int Days { get; set; } = 3;

    [Display(Name = "Rows", GroupName = "Calculation", Order = 11)]
    [Range(10, 400)]
    public int Rows { get; set; } = 80;

    [Display(Name = "Profile width (px)", GroupName = "Drawing", Order = 20)]
    [Range(40, 600)]
    public int Width { get; set; } = 150;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        int days = Math.Clamp(Days, 1, 30);
        data.MaxTradeDays = Math.Max(data.MaxTradeDays, days);
        var now = EndOf(LastBar);
        var from = now.AddDays(-days);
        var tr = data.TradesBetween(Underlying, from, now).Where(t => t.Spot > 0).ToList();
        _bull = _bear = Array.Empty<double>();
        if (tr.Count == 0)
        {
            Status = data.TradesCovered(Underlying, from, DateTime.UtcNow.AddMinutes(-2) < now ? DateTime.UtcNow.AddMinutes(-2) : now)
                ? "No option trades in these days." : "Loading option trades…";
            return;
        }

        double lo = tr.Min(t => t.Spot), hi = tr.Max(t => t.Spot);
        _row = Math.Max((hi - lo) / Rows, Close(LastBar) * 0.0002);
        _low = lo;
        int n = (int)Math.Ceiling((hi - lo) / _row) + 1;
        _bull = new double[n];
        _bear = new double[n];
        foreach (var t in tr)
        {
            int k = Math.Clamp((int)((t.Spot - lo) / _row), 0, n - 1);
            if (t.Bias >= 0) _bull[k] += t.Premium;
            else _bear[k] += t.Premium;
        }

        int poc = Enumerable.Range(0, n).OrderByDescending(k => _bull[k] + _bear[k]).First();
        double pocPx = _low + (poc + 0.5) * _row;
        var cov = data.TradeCoverage(Underlying);
        s.Line(Math.Max(0, BarOf(from)), -1, pocPx, Opt.Flip, 1, LineDashStyle.Dash, $"Most premium {Price(pocPx)}");
        s.Title = $"Options at price · {days} d";
        s.Row("Bullish premium", Opt.Usd(_bull.Sum()), Opt.Call);
        s.Row("Bearish or unknown-side premium", Opt.Usd(_bear.Sum()), Opt.Put);
        s.Row("Most premium at", Price(pocPx), Opt.Flip);
        if (cov is { } c && c.From > from) s.Row("Trades loaded from", c.From.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC", Opt.Flip);
    }

    protected override void RenderBefore(RenderContext ctx, Rectangle region)
    {
        if (_bull.Length == 0) return;
        double max = Enumerable.Range(0, _bull.Length).Max(k => _bull[k] + _bear[k]);
        if (max <= 0) return;
        int right = region.Right - 4;
        for (int k = 0; k < _bull.Length; k++)
        {
            if (_bull[k] + _bear[k] <= 0) continue;
            int y0 = Y(_low + (k + 1) * _row), y1 = Y(_low + k * _row);
            int wb = (int)(Width * _bull[k] / max), wr = (int)(Width * _bear[k] / max);
            int top = Math.Min(y0, y1), h = Math.Max(1, Math.Abs(y1 - y0) - 1);
            if (wb > 0) ctx.FillRectangle(Opt.A(Opt.Call, 170), new Rectangle(right - wb, top, wb, h));
            if (wr > 0) ctx.FillRectangle(Opt.A(Opt.Put, 170), new Rectangle(right - wb - wr, top, wr, h));
        }
    }
}

[DisplayName("Option flow VWAP")]
[Category(IndicatorCategories.Options)]
[Description("A daily average of the underlying price weighted by option premium (not volume): where option buyers came in. Optionally one line for bullish and one for bearish flow. Resets each trading day; each point uses only trades up to that bar.")]
public class FlowVwap : OptionsOverlay
{
    public FlowVwap() => ShowCard = false;

    [Display(Name = "Bullish and bearish flow separately", GroupName = "Drawing", Order = 20)]
    public bool SplitSides { get; set; } = true;

    [Display(Name = "Days drawn", GroupName = "Drawing", Order = 21)]
    [Range(1, 30)]
    public int Days { get; set; } = 3;

    [Display(Name = "Color", GroupName = "Drawing", Order = 22)]
    public Color LineColor { get; set; } = Color.FromArgb(255, 251, 191, 36);

    internal override void Build(OptionsScene s, OptionsData data)
    {
        int first = LastBar;
        for (int d = 0; first > 0 && d < Days; d++) first = DayStart(first) - 1;
        first = Math.Max(0, first + 1);
        List<(int, double)>? all = null, bull = null, bear = null;
        double sp = 0, sw = 0, bp = 0, bw = 0, rp = 0, rw = 0;
        for (int i = first; i <= LastBar; i++)
        {
            if (i == first || DayOf(i) != DayOf(i - 1))
            {
                Flush();
                all = new();
                bull = new();
                bear = new();
                sp = sw = bp = bw = rp = rw = 0;
            }

            foreach (var t in TradesOf(data, i))
            {
                double spot = t.Spot > 0 ? t.Spot : Close(i), p = t.Premium;
                sp += spot * p;
                sw += p;
                if (t.Bias > 0) { bp += spot * p; bw += p; }
                else if (t.Bias < 0) { rp += spot * p; rw += p; }
            }

            if (sw > 0) all!.Add((i, sp / sw));
            if (bw > 0) bull!.Add((i, bp / bw));
            if (rw > 0) bear!.Add((i, rp / rw));
        }

        Flush();
        if (s.Paths.Count == 0 && data.TradeCoverage(Underlying) == null) Status = "Loading option trades…";

        void Flush()
        {
            if (all is { Count: > 1 }) s.Paths.Add(new OptionsScene.PathLine(all, LineColor, 2, LineDashStyle.Solid));
            if (SplitSides && bull is { Count: > 1 }) s.Paths.Add(new OptionsScene.PathLine(bull, Opt.A(Opt.Call, 200), 1, LineDashStyle.Dash));
            if (SplitSides && bear is { Count: > 1 }) s.Paths.Add(new OptionsScene.PathLine(bear, Opt.A(Opt.Put, 200), 1, LineDashStyle.Dash));
        }
    }
}

[DisplayName("Large option trade marks")]
[Category(IndicatorCategories.Options)]
[Description("Marks on the bars where large option trades executed (from the premium you set), at the underlying price of the trade: circle size follows premium and color the side (green bullish, red bearish, gray unknown).")]
public class FlowMarks : OptionsOverlay
{
    internal readonly List<OptionTrade> Big = new();

    public FlowMarks() => ShowCard = false;

    [Display(Name = "Minimum premium (USD)", GroupName = "Calculation", Order = 10)]
    [Range(1000, 1e10)]
    public decimal MinPremium { get; set; } = 100_000;

    [Display(Name = "At most per bar", GroupName = "Calculation", Order = 11)]
    [Range(1, 20)]
    public int PerBar { get; set; } = 3;

    [Display(Name = "Bars examined", GroupName = "Calculation", Order = 12)]
    [Range(20, 3000)]
    public int Lookback { get; set; } = 500;

    [Display(Name = "With text", GroupName = "Drawing", Order = 20)]
    public bool ShowText { get; set; } = true;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        Big.Clear();
        int first = Math.Max(0, LastBar - Lookback);
        double min = (double)MinPremium, top = 0;
        var marks = new List<(int Bar, OptionTrade T)>();
        for (int i = first; i <= LastBar; i++)
            foreach (var t in TradesOf(data, i).Where(t => t.Premium >= min).OrderByDescending(t => t.Premium).ThenBy(t => t.Time).Take(PerBar))
            {
                marks.Add((i, t));
                Big.Add(t);
                top = Math.Max(top, t.Premium);
            }

        foreach (var (bar, t) in marks)
        {
            int size = (int)Math.Clamp(4 + 10 * Math.Sqrt(t.Premium / Math.Max(1, top)), 4, 14);
            var col = Opt.A(t.Bias > 0 ? Opt.Call : t.Bias < 0 ? Opt.Put : Opt.Muted, 190);
            s.Marks.Add(new OptionsScene.Mark(bar, t.Spot > 0 ? t.Spot : Close(bar), col, size, ShowText ? $"{Opt.Usd(t.Premium)} {Opt.Contract(t.Right, t.Strike, t.Expiry)}" : null));
        }

        if (marks.Count == 0 && data.TradeCoverage(Underlying) == null) Status = "Loading option trades…";
    }
}

[DisplayName("Large option trades")]
[Category(IndicatorCategories.Options)]
[Description("Large option trades of the underlying marked on the chart (like Large option trade marks) with a card of the latest ones: time, contract, side and premium. Shows where big money is betting.")]
public class OptionPrints : FlowMarks
{
    public OptionPrints()
    {
        ShowCard = true;
        ShowText = false;
    }

    [Display(Name = "Card rows", GroupName = "Card", Order = 903)]
    [Range(1, 40)]
    public int TableRows { get; set; } = 10;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        base.Build(s, data);
        if (Big.Count == 0) return;
        s.Title = $"Large trades · {Underlying}";
        foreach (var t in Big.OrderByDescending(t => t.Time).Take(TableRows))
            s.Row(t.Time.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "  " + Opt.Contract(t.Right, t.Strike, t.Expiry) + "  " + UiText.Tr(t.Aggressor > 0 ? "buy" : t.Aggressor < 0 ? "sell" : "—"),
                Opt.Usd(t.Premium), Opt.BiasColor(t.Bias));
        s.Footer = "Times in UTC";
    }
}

[DisplayName("Option tape")]
[Category(IndicatorCategories.Options)]
[Description("The latest option orders first, with time, contract, side, size and premium. Fills of the same contract and side within one second are joined into one order; orders with several fills are marked as sweeps.")]
public class FlowTape : OptionsOverlay
{
    [Display(Name = "Minimum premium (USD)", GroupName = "Calculation", Order = 10)]
    [Range(0, 1e10)]
    public decimal MinPremium { get; set; } = 10_000;

    [Display(Name = "Rows", GroupName = "Calculation", Order = 11)]
    [Range(3, 60)]
    public int RowsShown { get; set; } = 15;

    [Display(Name = "Sweep from (fills within 1 s)", GroupName = "Calculation", Order = 12)]
    [Range(2, 50)]
    public int SweepFills { get; set; } = 3;

    /// <summary>Fills of the same contract and side within one second of the previous fill, joined.</summary>
    internal static List<(DateTime T, OptionTrade First, double Size, double Premium, int Fills)> Orders(IEnumerable<OptionTrade> trades)
    {
        var orders = new List<(DateTime T, OptionTrade First, double Size, double Premium, int Fills)>();
        foreach (var t in trades)
        {
            if (orders.Count > 0 && orders[^1].First.Symbol == t.Symbol && orders[^1].First.Aggressor == t.Aggressor && (t.Time - orders[^1].T).TotalSeconds <= 1)
            {
                var o = orders[^1];
                orders[^1] = (t.Time, o.First, o.Size + t.Size, o.Premium + t.Premium, o.Fills + 1);
            }
            else orders.Add((t.Time, t, t.Size, t.Premium, 1));
        }

        return orders;
    }

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var now = EndOf(LastBar);
        var from = now.AddHours(-12);
        var tr = data.TradesBetween(Underlying, from, now);
        if (tr.Count == 0)
        {
            Status = data.TradesCovered(Underlying, from, DateTime.UtcNow.AddMinutes(-2) < now ? DateTime.UtcNow.AddMinutes(-2) : now)
                ? "No option trades in the last 12 hours." : "Loading option trades…";
            return;
        }

        s.Title = $"Option tape · {Underlying}";
        foreach (var o in Orders(tr).Where(o => o.Premium >= (double)MinPremium).OrderByDescending(o => o.T).Take(RowsShown))
        {
            string sweep = o.Fills >= SweepFills ? " ⚡" + UiText.Tr("sweep") : "";
            s.Row(o.T.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + Opt.Contract(o.First.Right, o.First.Strike, o.First.Expiry) + "  " +
                  UiText.Tr(o.First.Aggressor > 0 ? "buy" : o.First.Aggressor < 0 ? "sell" : "—") + " " + Opt.N(o.Size, "0.##") + sweep,
                Opt.Usd(o.Premium), Opt.BiasColor(o.First.Bias));
        }

        s.Footer = "Times in UTC";
    }
}

[DisplayName("Option flow of the last bar")]
[Category(IndicatorCategories.Options)]
[Description("A card with the option contracts that gathered the most premium in the last closed bar, sorted by premium, with the side that dominated.")]
public class IntervalFlow : OptionsOverlay
{
    [Display(Name = "Contracts", GroupName = "Calculation", Order = 10)]
    [Range(1, 40)]
    public int Count { get; set; } = 10;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        int bar = Math.Max(0, LastBar - 1);
        var tr = TradesOf(data, bar);
        s.Title = $"Flow of the bar at {TimeOf(bar).ToString("HH:mm", CultureInfo.InvariantCulture)} UTC";
        if (tr.Count == 0)
        {
            s.Row(data.TradesCovered(Underlying, TimeOf(bar), EndOf(bar)) ? "No option trades" : "Loading trades…", "");
            return;
        }

        foreach (var g in tr.GroupBy(t => t.Symbol).Select(g => (First: g.First(), Premium: g.Sum(t => t.Premium), Bias: g.Sum(t => t.Bias * t.Premium)))
                     .OrderByDescending(x => x.Premium).ThenBy(x => x.First.Symbol, StringComparer.Ordinal).Take(Count))
            s.Row(Opt.Contract(g.First.Right, g.First.Strike, g.First.Expiry) + "  " + UiText.Tr(g.Bias > 0 ? "bullish" : g.Bias < 0 ? "bearish" : "—"), Opt.Usd(g.Premium), Opt.BiasColor(g.Bias));
        s.Row("Bar total", Opt.Usd(tr.Sum(t => t.Premium)));
    }
}

[DisplayName("Option statistics")]
[Category(IndicatorCategories.Options)]
[Description("A card showing whether calls or puts lead today: premium on each side, how much was bought and sold, the put/call premium ratio and the net bullish flow.")]
public class OptionsStats : OptionsOverlay
{
    internal override void Build(OptionsScene s, OptionsData data)
    {
        var (today, complete) = TodayTrades(data);
        var st = FlowTotals.Of(today);
        s.Title = $"Options today · {Underlying}";
        if (!complete)
        {
            s.Row("Loading trades…", "");
            return;
        }

        if (st.Count == 0)
        {
            s.Row("No option trades today", "");
            return;
        }

        bool calls = st.CallPremium >= st.PutPremium;
        double sided = Math.Max(1, st.Bought + st.Sold);
        s.Row("Leading", calls ? "calls" : "puts", calls ? Opt.Call : Opt.Put);
        s.Row("Call premium", Opt.Usd(st.CallPremium), Opt.Call);
        s.Row("Put premium", Opt.Usd(st.PutPremium), Opt.Put);
        s.Row("Puts / calls (premium)", st.CallPremium > 0 ? Opt.N(st.PutPremium / st.CallPremium, "0.00") : "—");
        s.Row("Bought / sold", Opt.Pct(st.Bought / sided * 100, "0") + " / " + Opt.Pct(st.Sold / sided * 100, "0"));
        s.Row("Net flow (bullish − bearish)", Opt.SignedUsd(st.Bullish - st.Bearish), Opt.BiasColor(st.Bullish - st.Bearish));
        s.Row("Trades", st.Count.ToString(CultureInfo.InvariantCulture));
    }
}

[DisplayName("Option market statistics")]
[Category(IndicatorCategories.Options)]
[Description("Today across the options of the underlyings you choose (BTC and ETH by default): for each, whether more calls or puts trade, total premium and net bullish flow, plus the total.")]
public class OptionsMarketStats : OptionsOverlay
{
    protected override bool UsesChartUnderlying => false;

    [Display(Name = "Underlyings", GroupName = "Calculation", Order = 10)]
    public string Underlyings { get; set; } = "BTC, ETH";

    internal override void Build(OptionsScene s, OptionsData data)
    {
        DateTime from = TimeOf(DayStart(LastBar)), to = EndOf(LastBar);
        DateTime coveredTo = DateTime.UtcNow.AddMinutes(-2) < to ? DateTime.UtcNow.AddMinutes(-2) : to;
        s.Title = "Option market today";
        double bull = 0, bear = 0;
        foreach (var u in Underlyings.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToUpperInvariant()).Distinct())
        {
            if (data.Unavailable(u) != null)
            {
                s.Row(u, "no data", Opt.Muted);
                continue;
            }

            var tr = data.TradesBetween(u, from, to);
            if (!data.TradesCovered(u, from, coveredTo))
            {
                s.Row(u, "loading…", Opt.Muted);
                continue;
            }

            var st = FlowTotals.Of(tr);
            bull += st.Bullish;
            bear += st.Bearish;
            s.Row(u + " · " + UiText.Tr(st.CallPremium >= st.PutPremium ? "more calls" : "more puts"), Opt.Usd(st.CallPremium + st.PutPremium) + "  " + Opt.SignedUsd(st.Bullish - st.Bearish),
                Opt.BiasColor(st.Bullish - st.Bearish));
        }

        s.Row("Total: bullish / bearish", Opt.Usd(bull) + " / " + Opt.Usd(bear), Opt.BiasColor(bull - bear));
    }
}

[DisplayName("Option venue share")]
[Category(IndicatorCategories.Options)]
[Description("Which share of today's option volume executed on each venue, when the provider reports where each trade executed (for US options, the individual exchanges).")]
public class MarketShare : OptionsOverlay
{
    [Display(Name = "By premium (otherwise contracts)", GroupName = "Calculation", Order = 10)]
    public bool ByPremium { get; set; } = true;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var (tr, complete) = TodayTrades(data);
        s.Title = $"Share by venue · {Underlying}";
        if (!complete || tr.Count == 0)
        {
            s.Row(complete ? "No trades today" : "Loading trades…", "");
            return;
        }

        double total = tr.Sum(t => ByPremium ? t.Premium : t.Size);
        foreach (var g in tr.GroupBy(t => t.Venue).Select(g => (g.Key, V: g.Sum(t => ByPremium ? t.Premium : t.Size))).OrderByDescending(x => x.V))
            s.Row(g.Key.Length == 0 ? "venue not reported" : g.Key, Opt.Pct(g.V / Math.Max(1e-9, total) * 100));
    }
}

[DisplayName("Option premium by expiry")]
[Category(IndicatorCategories.Options)]
[Description("Today's option premium in each expiry (calls and puts), to see when traders expect the move.")]
public class PremiumByExpiry : OptionsOverlay
{
    private List<(DateTime Expiry, double Call, double Put)> _rows = new();

    [Display(Name = "Expiries", GroupName = "Calculation", Order = 10)]
    [Range(1, 30)]
    public int Count { get; set; } = 10;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var (tr, complete) = TodayTrades(data);
        _rows = tr.GroupBy(t => t.Expiry)
            .Select(g => (g.Key, g.Where(t => t.Right == OptionRight.Call).Sum(t => t.Premium), g.Where(t => t.Right == OptionRight.Put).Sum(t => t.Premium)))
            .OrderByDescending(r => r.Item2 + r.Item3).Take(Count).OrderBy(r => r.Key).ToList();
        if (_rows.Count == 0) Status = complete ? "No option trades today." : "Loading option trades…";
    }

    protected override void RenderAfter(RenderContext ctx, Rectangle region)
    {
        if (_rows.Count == 0 || !ShowCard) return;
        int rh = 17, bw = 150, lw = 74, vw = 60, w = lw + bw + vw + 20, h = rh * _rows.Count + 30;
        int col = (int)CardPosition % 3, row = (int)CardPosition / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        ctx.FillRectangle(Color.FromArgb(225, 18, 20, 26), new Rectangle(x, y, w, h), 5);
        ctx.DrawString(UiText.Tr($"Today's premium by expiry · {Underlying}"), StudyCard.Bold, Opt.Text, x + 8, y + 5);
        double max = _rows.Max(r => r.Call + r.Put);
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            int ry = y + 24 + i * rh;
            ctx.DrawString(r.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StudyCard.Font, Opt.Dim, x + 8, ry);
            int wc = max > 0 ? (int)(bw * r.Call / max) : 0, wp = max > 0 ? (int)(bw * r.Put / max) : 0;
            ctx.FillRectangle(Opt.A(Opt.Call, 190), new Rectangle(x + lw, ry + 2, Math.Max(0, wc), rh - 6));
            ctx.FillRectangle(Opt.A(Opt.Put, 190), new Rectangle(x + lw + wc, ry + 2, Math.Max(0, wp), rh - 6));
            ctx.DrawString(Opt.Usd(r.Call + r.Put), StudyCard.Font, Opt.Text, x + lw + bw + 8, ry);
        }
    }
}

// ============================================================================ flow panes

[DisplayName("NOPE (net options pricing effect)")]
[Category(IndicatorCategories.Options)]
[Description("Net Options Pricing Effect: the net delta of the options traded in each bar (Σ Δ × contracts × multiplier, calls positive and puts negative) as a percentage of the underlying's volume in the same bar, with an EMA. Shows how much dealer hedging could push the price.")]
public class Nope : OptionsPane
{
    private readonly OptionalValueDataSeries _nope = new("NOPE", "NOPE") { Color = Opt.Violet, Width = 2 };
    private readonly OptionalValueDataSeries _raw = new("RAW", "NOPE unsmoothed") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Muted, 110) };
    private int _smooth = 3;

    public Nope()
    {
        DataSeries[0] = _nope;
        DataSeries.Add(_raw);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    [Display(Name = "Smoothing (bars)", GroupName = "Calculation", Order = 10)]
    [Range(1, 100)]
    public int Smooth { get => _smooth; set { _smooth = Math.Clamp(value, 1, 100); RecalculateValues(); } }

    /// <summary>NOPE in percent: net traded delta (underlying units) ÷ underlying volume × 100; NaN without volume.</summary>
    internal static double Value(IEnumerable<OptionTrade> trades, double underlyingVolume)
    {
        double delta = trades.Sum(t => t.Delta * t.Size * t.ContractSize);
        return underlyingVolume > 0 ? delta / underlyingVolume * 100 : double.NaN;
    }

    protected override void Calc(int bar, OptionsData data)
    {
        var tr = TradesOf(data, bar);
        double v = tr == null ? double.NaN : Value(tr, (double)GetCandle(bar).Volume);
        Set(_raw, bar, v);
        double k = 2.0 / (_smooth + 1);
        double? prev = bar > 0 ? At(_nope, bar - 1) : null;
        Set(_nope, bar, !double.IsFinite(v) ? double.NaN : prev is { } p ? p + k * (v - p) : v);
    }
}

[DisplayName("Option aggressor side")]
[Category(IndicatorCategories.Options)]
[Description("Bars per candle with the option premium bought at the ask (up, green) and sold at the bid (down, red), and a card with today's totals: whether option buyers or sellers lead.")]
public class ContractSides : OptionsPane
{
    private readonly OptionalValueDataSeries _buy = new("BUY", "Bought") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Call, 200) };
    private readonly OptionalValueDataSeries _sell = new("SELL", "Sold") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Put, 200) };

    public ContractSides()
    {
        DataSeries[0] = _buy;
        DataSeries.Add(_sell);
    }

    protected override void Calc(int bar, OptionsData data)
    {
        var tr = TradesOf(data, bar);
        if (tr == null)
        {
            _buy.SetUnavailable(bar);
            _sell.SetUnavailable(bar);
            return;
        }

        var st = FlowTotals.Of(tr);
        _buy.Set(bar, (decimal)st.Bought);
        _sell.Set(bar, (decimal)-st.Sold);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        if (CurrentBar == 0) return;
        double b = 0, s = 0;
        bool complete = true;
        for (int i = CurrentBar - 1; i >= 0; i--)
        {
            if (At(_buy, i) is not { } bv) { complete = false; break; }
            b += bv;
            s -= At(_sell, i) ?? 0;
            if (NewDay(i)) break;
        }

        double tot = b + s;
        Card(ctx, box, $"Today's side · {Underlying}", new List<(string, string, Color)>
        {
            ("Bought", Opt.Usd(b) + "  " + Opt.Pct(tot > 0 ? b / tot * 100 : double.NaN, "0"), Opt.Call),
            ("Sold", Opt.Usd(s) + "  " + Opt.Pct(tot > 0 ? s / tot * 100 : double.NaN, "0"), Opt.Put),
            ("Leading", tot <= 0 ? "—" : b >= s ? "buyers" : "sellers", b >= s ? Opt.Call : Opt.Put),
        }, complete ? null : "Part of today is not loaded");
    }
}

[DisplayName("Option net flow")]
[Category(IndicatorCategories.Options)]
[Description("So far today: premium bought minus sold for calls (green line) and for puts (red line). Rising calls and falling puts are bullish flow; the reverse is bearish. Resets each trading day; a day starts only once its first bar is loaded.")]
public class NetFlow : OptionsPane
{
    private readonly OptionalValueDataSeries _calls = new("CALLS", "Calls (bought − sold)") { Color = Opt.Call, Width = 2 };
    private readonly OptionalValueDataSeries _puts = new("PUTS", "Puts (bought − sold)") { Color = Opt.Put, Width = 2 };

    public NetFlow()
    {
        DataSeries[0] = _calls;
        DataSeries.Add(_puts);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    protected override void Calc(int bar, OptionsData data)
    {
        var tr = TradesOf(data, bar);
        bool reset = NewDay(bar);
        double? pc = reset ? 0 : At(_calls, bar - 1), pp = reset ? 0 : At(_puts, bar - 1);
        if (tr == null || pc == null || pp == null)
        {
            _calls.SetUnavailable(bar);
            _puts.SetUnavailable(bar);
            return;
        }

        double c = 0, p = 0;
        foreach (var t in tr)
            if (t.Right == OptionRight.Call) c += t.Aggressor * t.Premium;
            else p += t.Aggressor * t.Premium;
        _calls.Set(bar, (decimal)(pc.Value + c));
        _puts.Set(bar, (decimal)(pp.Value + p));
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        if (CurrentBar == 0 || At(_calls, CurrentBar - 1) is not { } c || At(_puts, CurrentBar - 1) is not { } p)
        {
            Note(ctx, box, "Loading option trades…");
            return;
        }

        Card(ctx, box, $"Net flow today · {Underlying}", new List<(string, string, Color)>
        {
            ("Calls", Opt.SignedUsd(c), Opt.Call),
            ("Puts", Opt.SignedUsd(p), Opt.Put),
            ("Reading", c - p > 0 ? "bullish flow" : c - p < 0 ? "bearish flow" : "—", Opt.BiasColor(c - p)),
        });
    }
}

[DisplayName("Option volume")]
[Category(IndicatorCategories.Options)]
[Description("Call (up) and put (down) contracts traded in each bar, with a card of today's and the window's put/call ratio.")]
public class OptionsVolume : OptionsPane
{
    private readonly OptionalValueDataSeries _c = new("CV", "Calls") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Call, 200) };
    private readonly OptionalValueDataSeries _p = new("PV", "Puts") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Put, 200) };

    public OptionsVolume()
    {
        DataSeries[0] = _c;
        DataSeries.Add(_p);
    }

    [Display(Name = "Window (bars)", GroupName = "Calculation", Order = 10)]
    [Range(2, 2000)]
    public int Window { get; set; } = 96;

    protected override void Calc(int bar, OptionsData data)
    {
        var tr = TradesOf(data, bar);
        if (tr == null)
        {
            _c.SetUnavailable(bar);
            _p.SetUnavailable(bar);
            return;
        }

        double c = 0, p = 0;
        foreach (var t in tr)
            if (t.Right == OptionRight.Call) c += t.Size;
            else p += t.Size;
        _c.Set(bar, (decimal)c);
        _p.Set(bar, (decimal)-p);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        int last = CurrentBar - 1;
        if (last < 0) return;
        double dc = 0, dp = 0, wc = 0, wp = 0;
        for (int i = last; i >= 0; i--)
        {
            dc += At(_c, i) ?? 0;
            dp -= At(_p, i) ?? 0;
            if (NewDay(i)) break;
        }

        for (int i = Math.Max(0, last - Window + 1); i <= last; i++)
        {
            wc += At(_c, i) ?? 0;
            wp -= At(_p, i) ?? 0;
        }

        Card(ctx, box, $"Option volume · {Underlying}", new List<(string, string, Color)>
        {
            ("Puts/calls today", dc > 0 ? Opt.N(dp / dc, "0.00") : "—", dp > dc ? Opt.Put : Opt.Call),
            ("Puts/calls window", wc > 0 ? Opt.N(wp / wc, "0.00") : "—", wp > wc ? Opt.Put : Opt.Call),
            ("Calls / puts today", Opt.N(dc, "0") + " / " + Opt.N(dp, "0"), default),
        });
    }
}

[DisplayName("Greek flow")]
[Category(IndicatorCategories.Options)]
[Description("So far each day: which way option traders lean (delta bought minus sold, USD: Σ side × contracts × multiplier × Δ × S) and whether they bet on volatility rising (vega bought minus sold, USD per volatility point; amber line on its own scale).")]
public class GreekFlow : OptionsPane
{
    private readonly OptionalValueDataSeries _delta = new("DELTA", "Net delta (USD)") { Color = Opt.Blue, Width = 2 };
    private readonly OptionalValueDataSeries _vega = new("VEGA", "Net vega (USD per point)") { VisualType = VisualMode.Hide, Color = Opt.Flip };

    public GreekFlow()
    {
        DataSeries[0] = _delta;
        DataSeries.Add(_vega);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    /// <summary>Signed traded delta (USD) and vega (USD per volatility point) of a set of trades.</summary>
    internal static (double Delta, double Vega) Of(IEnumerable<OptionTrade> trades)
    {
        double d = 0, v = 0;
        foreach (var t in trades)
        {
            double n = t.Aggressor * t.Size * t.ContractSize;
            d += n * t.Delta * t.Spot;
            v += n * OptionsMath.Vega(t.Spot, t.Strike, Math.Max(1.0 / 365, (t.Expiry - t.Time).TotalDays / 365), t.Iv > 0 ? t.Iv : 0.6);
        }

        return (d, v);
    }

    protected override void Calc(int bar, OptionsData data)
    {
        var tr = TradesOf(data, bar);
        bool reset = NewDay(bar);
        double? pd = reset ? 0 : At(_delta, bar - 1), pv = reset ? 0 : At(_vega, bar - 1);
        if (tr == null || pd == null || pv == null)
        {
            _delta.SetUnavailable(bar);
            _vega.SetUnavailable(bar);
            return;
        }

        var (d, v) = Of(tr);
        _delta.Set(bar, (decimal)(pd.Value + d));
        _vega.Set(bar, (decimal)(pv.Value + v));
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        if (ChartInfo == null || CurrentBar == 0) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        var vals = Enumerable.Range(first, Math.Max(0, last - first + 1)).Select(i => (Bar: i, V: At(_vega, i))).Where(x => x.V.HasValue).ToList();
        if (vals.Count > 1)
        {
            double lo = vals.Min(x => x.V!.Value), hi = vals.Max(x => x.V!.Value);
            var r = box.Region;
            var pts = vals.Select(x => new Point(ChartInfo.GetXByBar(x.Bar, false), r.Bottom - 6 - (int)(hi > lo ? (x.V!.Value - lo) / (hi - lo) * (r.Height - 30) : (r.Height - 30) / 2.0))).ToArray();
            ctx.DrawLines(new RenderPen(Opt.A(Opt.Flip, 200), 1), pts);
        }

        if (At(_delta, CurrentBar - 1) is not { } d || At(_vega, CurrentBar - 1) is not { } v)
        {
            Note(ctx, box, "Loading option trades…");
            return;
        }

        Card(ctx, box, $"Greek flow today · {Underlying}", new List<(string, string, Color)>
        {
            ("Net delta", Opt.SignedUsd(d), Opt.BiasColor(d)),
            ("Net vega", Opt.SignedUsd(v), Opt.Flip),
            ("Betting on", UiText.Tr(d >= 0 ? "a rise" : "a fall") + " · " + UiText.Tr(v >= 0 ? "more volatility" : "less volatility"), Opt.BiasColor(d)),
        });
    }
}

[DisplayName("Option sentiment")]
[Category(IndicatorCategories.Options)]
[Description("Today only: which way option traders lean, from −100 (all bearish) to +100 (all bullish): 100 × (bullish − bearish) ÷ (bullish + bearish) premium accumulated since the day opened.")]
public class OptionsSentiment : OptionsPane
{
    private readonly OptionalValueDataSeries _s = new("SENT", "Sentiment") { Color = Opt.Blue, Width = 2 };
    private readonly List<(double Bull, double Bear)?> _cum = new();

    public OptionsSentiment()
    {
        DataSeries[0] = _s;
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("U", "+50") { Value = 50, Color = Color.FromArgb(60, 38, 194, 129), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("D", "−50") { Value = -50, Color = Color.FromArgb(60, 240, 82, 90), LineDashStyle = LineDashStyle.Dot });
    }

    protected override void Reset() => _cum.Clear();

    protected override void Calc(int bar, OptionsData data)
    {
        while (_cum.Count <= bar) _cum.Add(null);
        var tr = TradesOf(data, bar);
        var prev = NewDay(bar) ? (0d, 0d) : _cum[bar - 1];
        if (tr == null || prev == null)
        {
            _cum[bar] = null;
            _s.SetUnavailable(bar);
            return;
        }

        var st = FlowTotals.Of(tr);
        var cum = (Bull: prev.Value.Bull + st.Bullish, Bear: prev.Value.Bear + st.Bearish);
        _cum[bar] = cum;
        _s.Set(bar, cum.Bull + cum.Bear > 0 ? (decimal)(100 * (cum.Bull - cum.Bear) / (cum.Bull + cum.Bear)) : 0);
        _s.Colors[bar] = _s[bar] >= 0 ? Opt.Call : Opt.Put;
        if (bar != CurrentBar - 1) return;
        // Today only: earlier days are not drawn.
        int start = bar;
        while (start > 0 && !NewDay(start)) start--;
        for (int i = 0; i < start; i++) _s.SetUnavailable(i);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        if (CurrentBar == 0 || CurrentBar > _cum.Count || _cum[CurrentBar - 1] is not { } cum)
        {
            Note(ctx, box, "Loading option trades…");
            return;
        }

        double v = (double)_s[CurrentBar - 1];
        Card(ctx, box, $"Sentiment today · {Underlying}", new List<(string, string, Color)>
        {
            ("Score", (v > 0 ? "+" : "") + v.ToString("0", CultureInfo.InvariantCulture), Opt.BiasColor(v)),
            ("Bullish premium", Opt.Usd(cum.Bull), Opt.Call),
            ("Bearish premium", Opt.Usd(cum.Bear), Opt.Put),
        });
    }
}

[DisplayName("Premium tide")]
[Category(IndicatorCategories.Options)]
[Description("Today across the options of the underlyings you choose: premium bet on a rise (green) against premium bet on a fall (red), accumulated since the day opened.")]
public class PremiumTide : OptionsPane
{
    private readonly OptionalValueDataSeries _bull = new("BULL", "Bullish premium") { Color = Opt.Call, Width = 2 };
    private readonly OptionalValueDataSeries _bear = new("BEAR", "Bearish premium") { Color = Opt.Put, Width = 2 };
    private string[] _unders = { "BTC", "ETH" };
    private string _list = "BTC, ETH";

    public PremiumTide()
    {
        DataSeries[0] = _bull;
        DataSeries.Add(_bear);
    }

    [Display(Name = "Underlyings", GroupName = "Calculation", Order = 10)]
    public string Underlyings
    {
        get => _list;
        set
        {
            _list = value ?? "";
            _unders = _list.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToUpperInvariant()).Distinct().ToArray();
            RecalculateValues();
        }
    }

    protected override bool UsesChartUnderlying => false;

    protected override void Poll(OptionsData data)
    {
        foreach (var u in _unders) data.TradesBetween(u, GetCandle(CurrentBar - 1).Time, DateTime.UtcNow);
    }

    protected override void Calc(int bar, OptionsData data)
    {
        DateTime from = GetCandle(bar).Time, to = BarEnd(bar), now = DateTime.UtcNow;
        bool reset = NewDay(bar);
        double? pb = reset ? 0 : At(_bull, bar - 1), pr = reset ? 0 : At(_bear, bar - 1);
        double bu = 0, be = 0;
        bool known = pb != null && pr != null;
        foreach (var u in _unders)
        {
            if (data.Unavailable(u) != null) continue;
            var tr = data.TradesBetween(u, from, to);
            if (!data.TradesCovered(u, from, to < now ? to : now.AddMinutes(-2))) known = false;
            var st = FlowTotals.Of(tr);
            bu += st.Bullish;
            be += st.Bearish;
        }

        if (!known)
        {
            _bull.SetUnavailable(bar);
            _bear.SetUnavailable(bar);
            return;
        }

        _bull.Set(bar, (decimal)(pb!.Value + bu));
        _bear.Set(bar, (decimal)(pr!.Value + be));
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        if (CurrentBar == 0 || At(_bull, CurrentBar - 1) is not { } bu || At(_bear, CurrentBar - 1) is not { } be)
        {
            Note(ctx, box, "Loading option trades…");
            return;
        }

        Card(ctx, box, $"Premium tide today · {string.Join("+", _unders)}", new List<(string, string, Color)>
        {
            ("On a rise", Opt.Usd(bu), Opt.Call),
            ("On a fall", Opt.Usd(be), Opt.Put),
            ("Difference", Opt.SignedUsd(bu - be), Opt.BiasColor(bu - be)),
        });
    }
}
