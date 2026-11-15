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

// Studies of the daily options history. A daily row describes one UTC day and is known only after that day ends, so a
// bar uses the latest row of an earlier day (at most four days old); bars that end after today's live snapshot use that
// snapshot instead. Each row is compared only with rows before it, so values never repaint.

/// <summary>Base of the panes that read the daily options history.</summary>
public abstract class OptionsDailyPane : OptionsPane
{
    private OptionsChain? _chain;
    private OptionDaily? _live;

    protected IReadOnlyList<OptionDaily> Days { get; private set; } = Array.Empty<OptionDaily>();

    protected override void Reset()
    {
        Days = Array.Empty<OptionDaily>();
        _chain = null;
        _live = null;
    }

    protected override void Poll(OptionsData data) => data.Chain(Underlying, TimeSpan.FromMinutes(10));

    protected sealed override void Calc(int bar, OptionsData data)
    {
        if (bar == 0)
        {
            Days = data.Daily(Underlying);
            _chain = data.Chain(Underlying, TimeSpan.FromMinutes(10));
            _live = _chain == null ? null : OptionsMath.DailyOf(_chain);
        }

        CalcDay(bar, KnownAt(bar));
    }

    /// <summary>The daily row known when bar <paramref name="bar"/> closes, or null (a gap).</summary>
    protected OptionDaily? KnownAt(int bar)
    {
        var start = GetCandle(bar).Time;
        if (_live != null && _chain != null && _chain.Time.Date == start.Date && BarEnd(bar) > _chain.Time) return _live;
        var row = Opt.KnownBefore(Days, start);
        return row != null && row.Date.Date >= start.Date.AddDays(-4) ? row : null;
    }

    /// <summary>Whether <paramref name="row"/> is today's live snapshot rather than a completed day.</summary>
    protected bool IsLive(OptionDaily? row) => row != null && ReferenceEquals(row, _live);

    protected abstract void CalcDay(int bar, OptionDaily? row);

    /// <summary>Finite values of the rows dated before <paramref name="day"/> within <paramref name="lookback"/> days.</summary>
    protected IEnumerable<double> Past(DateTime day, Func<OptionDaily, double> f, int lookback = 365) =>
        Days.Where(d => d.Date.Date < day.Date && d.Date.Date >= day.Date.AddDays(-lookback)).Select(f).Where(double.IsFinite);

    protected string? Footer(OptionDaily? row) =>
        row == null ? null : IsLive(row) && _chain != null ? StudyCard.Provenance(_chain) : $"Daily row {row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} (known from the next day)";

    protected void NoHistoryNote(RenderContext ctx, IChartContainer box, Func<OptionDaily, double> f)
    {
        int n = Days.Count(d => double.IsFinite(f(d)) && f(d) != 0);
        if (n < 5) Note(ctx, box, $"Daily history: {n} days (completed by one recorded snapshot per day while a study is open)");
    }

    protected OptionDaily? LastRow => CurrentBar > 0 ? KnownAt(CurrentBar - 1) : null;
}

[DisplayName("IV rank")]
[Category(IndicatorCategories.Options)]
[Description("Where the latest known implied volatility sits between the lowest and highest of the previous year (0–100), with its percentile and a card saying whether options are cheap, fairly priced or rich. IV rank = (IV − min) ÷ (max − min) × 100.")]
public class IvRank : OptionsDailyPane
{
    private readonly OptionalValueDataSeries _rank = new("RANK", "IV rank") { Color = Opt.Violet, Width = 2 };
    private readonly OptionalValueDataSeries _pct = new("PCT", "IV percentile") { Color = Opt.A(Opt.Muted, 200), LineDashStyle = LineDashStyle.Dot };

    public IvRank()
    {
        DataSeries[0] = _rank;
        DataSeries.Add(_pct);
        LineSeries.Add(new LineSeries("H", "70") { Value = 70, Color = Color.FromArgb(70, 240, 82, 90), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("L", "30") { Value = 30, Color = Color.FromArgb(70, 38, 194, 129), LineDashStyle = LineDashStyle.Dash });
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        if (d == null)
        {
            _rank.SetUnavailable(bar);
            _pct.SetUnavailable(bar);
            return;
        }

        var past = Past(d.Date, x => x.AtmIv).ToList();
        Set(_rank, bar, OptionsMath.Rank(past, d.AtmIv));
        Set(_pct, bar, OptionsMath.Percentile(past, d.AtmIv));
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.AtmIv);
        if (CurrentBar == 0 || LastRow is not { } d) return;
        double r = At(_rank, CurrentBar - 1) ?? double.NaN;
        Card(ctx, box, $"IV rank · {Underlying}", new List<(string, string, Color)>
        {
            ("IV", Opt.Pct(d.AtmIv * 100), default),
            ("IV rank", Opt.N(r, "0"), default),
            ("Percentile", Opt.N(At(_pct, CurrentBar - 1) ?? double.NaN, "0"), default),
            ("Options", !double.IsFinite(r) ? "—" : r >= 70 ? "rich" : r <= 30 ? "cheap" : "fairly priced", r >= 70 ? Opt.Put : r <= 30 ? Opt.Call : default),
        }, Footer(d));
    }
}

[DisplayName("Volatility regime")]
[Category(IndicatorCategories.Options)]
[Description("How far implied volatility is from the previous year's normal, in standard deviations: red bars on stressed days, blue on calm ones, and a card with the number of stressed days in the last month.")]
public class VolatilityRegime : OptionsDailyPane
{
    private readonly OptionalValueDataSeries _z = new("Z", "IV deviations") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Muted, 160) };

    public VolatilityRegime()
    {
        DataSeries[0] = _z;
        LineSeries.Add(new LineSeries("S", "stressed") { Value = 1.5m, Color = Color.FromArgb(80, 240, 82, 90), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("C", "calm") { Value = -1, Color = Color.FromArgb(80, 41, 98, 255), LineDashStyle = LineDashStyle.Dash });
    }

    [Display(Name = "Stressed from (deviations)", GroupName = "Calculation", Order = 10)]
    [Range(0.5, 5)]
    public decimal Stressed { get => LineSeries[0].Value; set { LineSeries[0].Value = value; RecalculateValues(); } }

    /// <summary>(v − mean) ÷ population standard deviation of <paramref name="past"/>; NaN with fewer than five values.</summary>
    internal static double ZScore(IReadOnlyList<double> past, double v)
    {
        if (past.Count < 5 || !double.IsFinite(v)) return double.NaN;
        double m = past.Average(), sd = Math.Sqrt(past.Sum(x => (x - m) * (x - m)) / past.Count);
        return sd > 0 ? (v - m) / sd : 0;
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        double z = d == null ? double.NaN : ZScore(Past(d.Date, x => x.AtmIv).ToList(), d.AtmIv);
        Set(_z, bar, z);
        if (double.IsFinite(z)) _z.Colors[bar] = z >= (double)Stressed ? Opt.A(Opt.Put, 220) : z <= -1 ? Opt.A(Opt.Blue, 200) : Opt.A(Opt.Muted, 140);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.AtmIv);
        if (CurrentBar == 0 || LastRow is not { } d) return;
        double z = At(_z, CurrentBar - 1) ?? double.NaN;
        int stressed = Days.Where(x => x.Date.Date <= d.Date.Date && x.Date.Date > d.Date.AddDays(-30))
            .Count(x => ZScore(Past(x.Date, y => y.AtmIv).ToList(), x.AtmIv) >= (double)Stressed);
        Card(ctx, box, $"Volatility regime · {Underlying}", new List<(string, string, Color)>
        {
            ("Deviations from normal", Opt.N(z, "+0.00;-0.00"), z >= (double)Stressed ? Opt.Put : default),
            ("Reading", !double.IsFinite(z) ? "—" : z >= (double)Stressed ? "stressed" : z <= -1 ? "calm" : "normal", z >= (double)Stressed ? Opt.Put : z <= -1 ? Opt.Blue : default),
            ("Stressed days (30 d)", stressed.ToString(CultureInfo.InvariantCulture), default),
        }, Footer(d));
    }
}

[DisplayName("Skew history")]
[Category(IndicatorCategories.Options)]
[Description("How much richer puts are than calls (25-delta put IV minus call IV, in points), day by day, with the previous year's range and its percentile: fear rises as the line rises.")]
public class SkewHistory : OptionsDailyPane
{
    private readonly OptionalValueDataSeries _skew = new("SKEW", "Put skew") { Color = Opt.Put, Width = 2 };
    private readonly OptionalValueDataSeries _hi = new("HI", "Year high") { Color = Opt.A(Opt.Muted, 120), LineDashStyle = LineDashStyle.Dot };
    private readonly OptionalValueDataSeries _lo = new("LO", "Year low") { Color = Opt.A(Opt.Muted, 120), LineDashStyle = LineDashStyle.Dot };

    public SkewHistory()
    {
        DataSeries[0] = _skew;
        DataSeries.Add(_hi);
        DataSeries.Add(_lo);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(80, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        var past = d == null ? new List<double>() : Past(d.Date, x => -x.Rr25).ToList();
        Set(_skew, bar, d == null ? double.NaN : -d.Rr25);
        Set(_hi, bar, past.Count > 1 ? past.Max() : double.NaN);
        Set(_lo, bar, past.Count > 1 ? past.Min() : double.NaN);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.Rr25);
        if (CurrentBar == 0 || LastRow is not { } d) return;
        double v = At(_skew, CurrentBar - 1) ?? double.NaN;
        double p = OptionsMath.Percentile(Past(d.Date, x => -x.Rr25), v);
        Card(ctx, box, $"Put skew · {Underlying}", new List<(string, string, Color)>
        {
            ("Puts − calls (25Δ)", Opt.N(v, "+0.0;-0.0") + " pts", v > 0 ? Opt.Put : Opt.Call),
            ("Percentile of the year", Opt.N(p, "0"), default),
            ("Fear", !double.IsFinite(p) ? "—" : p >= 80 ? "high" : p <= 20 ? "low" : "normal", p >= 80 ? Opt.Put : default),
        }, Footer(d));
    }
}

[DisplayName("Risk reversal skew")]
[Category(IndicatorCategories.Options)]
[Description("Which side is richer, puts or calls: the 25-delta risk reversal (call IV minus put IV) per day, plus the live snapshot at the chosen tenor once it is known. Negative (red) means traders fear a drop.")]
public class RiskReversalSkew : OptionsDailyPane
{
    private readonly OptionalValueDataSeries _rr = new("RR", "25Δ risk reversal") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Muted, 160) };
    private OptionsChain? _chain;

    public RiskReversalSkew() => DataSeries[0] = _rr;

    [Display(Name = "Live tenor (days)", GroupName = "Calculation", Order = 10)]
    [Range(1, 365)]
    public int Tenor { get; set; } = 30;

    protected override void Reset()
    {
        base.Reset();
        _chain = null;
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        double v = d?.Rr25 ?? double.NaN;
        if (IsLive(d))
        {
            _chain ??= Data?.Chain(Underlying, TimeSpan.FromMinutes(10));
            if (_chain != null) v = OptionsMath.RiskReversal25(_chain, OptionsMath.ExpiryNear(_chain, Tenor));
        }

        Set(_rr, bar, v);
        if (double.IsFinite(v)) _rr.Colors[bar] = v >= 0 ? Opt.A(Opt.Call, 200) : Opt.A(Opt.Put, 200);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.Rr25);
        if (CurrentBar == 0) return;
        double v = At(_rr, CurrentBar - 1) ?? double.NaN;
        Card(ctx, box, $"Risk reversal {Tenor} d · {Underlying}", new List<(string, string, Color)>
        {
            ("Calls − puts (25Δ)", Opt.N(v, "+0.0;-0.0") + " pts", v >= 0 ? Opt.Call : Opt.Put),
            ("Richer", !double.IsFinite(v) ? "—" : v >= 0 ? "calls" : "puts", v >= 0 ? Opt.Call : Opt.Put),
        }, Footer(LastRow));
    }
}

[DisplayName("Exposure history")]
[Category(IndicatorCategories.Options)]
[Description("A column per day with dealer gamma (USD per 1 %; green positive, red negative) and a card placing the latest against the previous year: whether it is unusual.")]
public class ExposureHistory : OptionsDailyPane
{
    private readonly OptionalValueDataSeries _gex = new("GEX", "Gamma (USD per 1 %)") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Muted, 160) };

    public ExposureHistory() => DataSeries[0] = _gex;

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        double v = d != null && d.Gex != 0 ? d.Gex : double.NaN;
        Set(_gex, bar, v);
        if (double.IsFinite(v)) _gex.Colors[bar] = v >= 0 ? Opt.A(Opt.Call, 200) : Opt.A(Opt.Put, 200);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.Gex);
        if (CurrentBar == 0 || LastRow is not { } d) return;
        double v = At(_gex, CurrentBar - 1) ?? double.NaN;
        double p = OptionsMath.Percentile(Past(d.Date, x => x.Gex).Where(x => x != 0), v);
        Card(ctx, box, $"Latest exposure · {Underlying}", new List<(string, string, Color)>
        {
            ("Gamma", Opt.SignedUsd(v), Opt.BiasColor(double.IsFinite(v) ? v : 0)),
            ("Percentile", Opt.N(p, "0"), default),
            ("Reading", !double.IsFinite(p) ? "—" : p >= 90 ? "unusually high" : p <= 10 ? "unusually low" : "normal", p >= 90 || p <= 10 ? Opt.Flip : default),
        }, Footer(d));
    }
}

[DisplayName("Variance risk premium (VRP)")]
[Category(IndicatorCategories.Options)]
[Description("For option sellers: implied volatility minus realized volatility of the last 30 daily closes (points; realized = annualized sample deviation of log returns). When options price more movement than happens, selling them pays; the card reads sell, neutral or wait.")]
public class VarianceRiskPremium : OptionsDailyPane
{
    private readonly OptionalValueDataSeries _vrp = new("VRP", "VRP") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Muted, 160) };
    private readonly OptionalValueDataSeries _iv = new("IV", "IV") { Color = Opt.Violet, VisualType = VisualMode.Hide };
    private readonly OptionalValueDataSeries _rv = new("RV", "Realized") { Color = Opt.Flip, VisualType = VisualMode.Hide };
    private readonly List<double> _closes = new();

    public VarianceRiskPremium()
    {
        DataSeries[0] = _vrp;
        DataSeries.Add(_iv);
        DataSeries.Add(_rv);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(80, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    [Display(Name = "Realized days", GroupName = "Calculation", Order = 10)]
    [Range(5, 120)]
    public int RealizedDays { get; set; } = 30;

    [Display(Name = "Sell from (points)", GroupName = "Calculation", Order = 11)]
    [Range(0, 50)]
    public decimal SellAbove { get; set; } = 5;

    protected override void Reset()
    {
        base.Reset();
        _closes.Clear();
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        if (NewDay(bar) && bar > 0) _closes.Add((double)GetCandle(bar - 1).Close);
        // Completed daily closes plus this bar's close: only what is known when the bar closes.
        var closes = _closes.Skip(Math.Max(0, _closes.Count - RealizedDays)).Append((double)GetCandle(bar).Close).ToList();
        double rv = OptionsMath.RealizedVol(closes, Opt.Year(Underlying)) * 100;
        double iv = (d?.AtmIv ?? double.NaN) * 100, vrp = iv - rv;
        Set(_iv, bar, iv);
        Set(_rv, bar, rv);
        Set(_vrp, bar, vrp);
        if (double.IsFinite(vrp)) _vrp.Colors[bar] = vrp >= (double)SellAbove ? Opt.A(Opt.Call, 210) : vrp < 0 ? Opt.A(Opt.Put, 210) : Opt.A(Opt.Muted, 150);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.AtmIv);
        if (CurrentBar == 0) return;
        int l = CurrentBar - 1;
        double v = At(_vrp, l) ?? double.NaN;
        Card(ctx, box, $"VRP · {Underlying}", new List<(string, string, Color)>
        {
            ("IV", Opt.Pct(At(_iv, l) ?? double.NaN), default),
            ("Realized", Opt.Pct(At(_rv, l) ?? double.NaN), default),
            ("Premium", Opt.N(v, "+0.0;-0.0") + " pts", v >= 0 ? Opt.Call : Opt.Put),
            ("For option sellers", !double.IsFinite(v) ? "—" : v >= (double)SellAbove ? "sell" : v < 0 ? "wait" : "neutral", v >= (double)SellAbove ? Opt.Call : v < 0 ? Opt.Put : default),
        }, Footer(LastRow));
    }
}

[DisplayName("Options history")]
[Category(IndicatorCategories.Options)]
[Description("Daily option volume or open interest (calls up, puts down), with a card of the latest day, the 20-day average and the last three days.")]
public class OptionsHistory : OptionsDailyPane
{
    public enum Metric
    {
        [Display(Name = "Volume")] Volume,
        [Display(Name = "Open interest")] OpenInterest,
    }

    private readonly OptionalValueDataSeries _c = new("C", "Calls") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Call, 200) };
    private readonly OptionalValueDataSeries _p = new("P", "Puts") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Put, 200) };
    private Metric _m = Metric.Volume;

    public OptionsHistory()
    {
        DataSeries[0] = _c;
        DataSeries.Add(_p);
    }

    [Display(Name = "Measure", GroupName = "Calculation", Order = 10)]
    public Metric Measure { get => _m; set { _m = value; RecalculateValues(); } }

    private (double C, double P) Of(OptionDaily d) => _m == Metric.Volume ? (d.CallVolume, d.PutVolume) : (d.CallOi, d.PutOi);

    private bool Has(OptionDaily d) => double.IsFinite(Of(d).C) && Of(d).C != 0;

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        var (c, p) = d != null && Has(d) ? Of(d) : (double.NaN, double.NaN);
        Set(_c, bar, c);
        Set(_p, bar, -p);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => Of(x).C);
        var valid = Days.Where(Has).ToList();
        if (valid.Count == 0) return;
        var last = valid[^1];
        var avg = valid.TakeLast(20).ToList();
        var rows = new List<(string, string, Color)>
        {
            ($"{UiText.Tr("Latest day")} ({last.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})", Opt.N(Of(last).C, "0") + " / " + Opt.N(Of(last).P, "0"), default),
            ("20-day average", Opt.N(avg.Average(x => Of(x).C), "0") + " / " + Opt.N(avg.Average(x => Of(x).P), "0"), default),
        };
        foreach (var x in valid.TakeLast(3).Reverse())
        {
            var (c, p) = Of(x);
            rows.Add((x.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Opt.N(c, "0") + " / " + Opt.N(p, "0") + (c > 0 ? "  P/C " + Opt.N(p / c, "0.00") : ""), p > c ? Opt.Put : Opt.Call));
        }

        Card(ctx, box, $"{UiText.Tr(_m == Metric.Volume ? "Option volume (calls / puts)" : "Open interest (calls / puts)")} · {Underlying}", rows);
    }
}

[DisplayName("Options sentiment gauge")]
[Category(IndicatorCategories.Options)]
[Description("A 0–100 fear and greed gauge comparing six daily signals with the previous year: IV, skew, put/call by volume and by open interest, dealer gamma and price momentum (close against its 50-day average). Daily line in the pane and a gauge with the latest reading.")]
public class OptionsSentimentGauge : OptionsDailyPane
{
    private readonly OptionalValueDataSeries _g = new("G", "Fear and greed") { Color = Opt.Flip, Width = 2 };
    private readonly List<double> _closes = new();
    private readonly List<double> _mom = new();
    private int _signals;

    public OptionsSentimentGauge()
    {
        DataSeries[0] = _g;
        LineSeries.Add(new LineSeries("H", "75") { Value = 75, Color = Color.FromArgb(60, 38, 194, 129), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("M", "50") { Value = 50, Color = Color.FromArgb(70, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("L", "25") { Value = 25, Color = Color.FromArgb(60, 240, 82, 90), LineDashStyle = LineDashStyle.Dot });
    }

    protected override void Reset()
    {
        base.Reset();
        _closes.Clear();
        _mom.Clear();
        _signals = 0;
    }

    /// <summary>Mean of the available signal percentiles (fear-high signals inverted); NaN without any.</summary>
    internal static double Gauge(IEnumerable<double> greedPercentiles)
    {
        var list = greedPercentiles.Where(double.IsFinite).ToList();
        return list.Count > 0 ? list.Average() : double.NaN;
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        if (NewDay(bar) && bar > 0)
        {
            _closes.Add((double)GetCandle(bar - 1).Close);
            if (_closes.Count > 50) _mom.Add(_closes[^1] / _closes.Skip(_closes.Count - 51).Take(50).Average() - 1);
        }

        var parts = new List<double>();
        if (d != null)
        {
            void Add(double v, Func<OptionDaily, double> f, bool greedHigh)
            {
                double p = OptionsMath.Percentile(Past(d.Date, f), v);
                if (double.IsFinite(p)) parts.Add(greedHigh ? p : 100 - p);
            }

            Add(d.AtmIv, x => x.AtmIv, false);
            Add(d.Rr25, x => x.Rr25, true);
            Add(d.PutCallVolume, x => x.PutCallVolume, false);
            Add(d.PutCallOi, x => x.PutCallOi, false);
            if (d.Gex != 0) Add(d.Gex, x => x.Gex != 0 ? x.Gex : double.NaN, true);
        }

        if (_mom.Count > 10 && _closes.Count >= 50)
        {
            double now = (double)GetCandle(bar).Close / _closes.Skip(_closes.Count - 50).Average() - 1;
            double p = OptionsMath.Percentile(_mom.Skip(Math.Max(0, _mom.Count - 365)), now);
            if (double.IsFinite(p)) parts.Add(p);
        }

        Set(_g, bar, Gauge(parts));
        if (bar == CurrentBar - 1) _signals = parts.Count;
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.AtmIv);
        if (CurrentBar == 0 || !ShowCard) return;
        double v = At(_g, CurrentBar - 1) ?? double.NaN;
        var r = box.Region;
        int w = 170, h = 110;
        int x = r.Right - w - 8, y = r.Top + 20;
        ctx.FillRectangle(Color.FromArgb(220, 18, 20, 26), new Rectangle(x, y, w, h), 6);
        var arc = new Rectangle(x + 25, y + 18, w - 50, w - 50);
        ctx.DrawArc(new RenderPen(Color.FromArgb(80, 140, 145, 160), 8), arc, 180, 180);
        string label = !double.IsFinite(v) ? "—" : v >= 75 ? "Extreme greed" : v >= 55 ? "Greed" : v > 45 ? "Neutral" : v > 25 ? "Fear" : "Extreme fear";
        var col = v >= 55 ? Opt.Call : v <= 45 ? Opt.Put : Opt.Muted;
        if (double.IsFinite(v)) ctx.DrawArc(new RenderPen(col, 8), arc, 180, (float)(180 * Math.Clamp(v, 0, 100) / 100));
        var big = new RenderFont("Arial", 16, FontStyle.Bold);
        string t = double.IsFinite(v) ? v.ToString("0", CultureInfo.InvariantCulture) : "—";
        ctx.DrawString(t, big, Opt.Text, x + w / 2 - ctx.MeasureString(t, big).Width / 2, y + 48);
        string lt = UiText.Tr(label);
        ctx.DrawString(lt, StudyCard.Font, col, x + w / 2 - ctx.MeasureString(lt, StudyCard.Font).Width / 2, y + 74);
        string st = UiText.Tr($"{_signals}/6 signals");
        ctx.DrawString(st, StudyCard.Font, Opt.Dim, x + w / 2 - ctx.MeasureString(st, StudyCard.Font).Width / 2, y + 90);
    }
}
