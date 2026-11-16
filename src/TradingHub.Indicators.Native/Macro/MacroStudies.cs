// Behavior adapted from the KVP capability reference (commits 7fbf237, 644b8ca); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds;
using TradingHub.Feeds.External;
using TradingHub.Indicators.Native.Options;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Macro;

// Macro and other tools: CUSUM change points, a market HUD, spot ETF flows, correlation with another Binance symbol and
// Polymarket odds against price. Unknown values are gaps, never zero; each bar uses only data known when it closes.

internal static class MacroMath
{
    /// <summary>Pearson correlation of the <paramref name="n"/> pairs ending at <paramref name="end"/>; NaN if any is missing or a side is flat.</summary>
    public static double Pearson(IReadOnlyList<double> a, IReadOnlyList<double> b, int end, int n)
    {
        if (end - n + 1 < 0 || end >= a.Count || end >= b.Count) return double.NaN;
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        for (int i = end - n + 1; i <= end; i++)
        {
            double x = a[i], y = b[i];
            if (!double.IsFinite(x) || !double.IsFinite(y)) return double.NaN;
            sa += x;
            sb += y;
            saa += x * x;
            sbb += y * y;
            sab += x * y;
        }

        double cov = sab / n - sa / n * (sb / n), va = saa / n - sa / n * (sa / n), vb = sbb / n - sb / n * (sb / n);
        return va <= 1e-18 || vb <= 1e-18 ? double.NaN : Math.Clamp(cov / Math.Sqrt(va * vb), -1, 1);
    }

    /// <summary>z-score of the value at <paramref name="end"/> against the <paramref name="n"/> values ending there; NaN if any is missing.</summary>
    public static double Z(IReadOnlyList<double> x, int end, int n)
    {
        if (end - n + 1 < 0 || end >= x.Count) return double.NaN;
        double s = 0, q = 0;
        for (int i = end - n + 1; i <= end; i++)
        {
            if (!double.IsFinite(x[i])) return double.NaN;
            s += x[i];
            q += x[i] * x[i];
        }

        double m = s / n, sd = Math.Sqrt(Math.Max(0, q / n - m * m));
        return sd <= 1e-12 ? 0 : (x[end] - m) / sd;
    }

    public static void Set(List<double> l, int i, double v)
    {
        while (l.Count <= i) l.Add(double.NaN);
        l[i] = v;
    }

    public static string Signed(double v, string fmt = "0.00") => double.IsFinite(v) ? (v > 0 ? "+" : "") + v.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    public static void SetOrGap(OptionalValueDataSeries s, int bar, double v)
    {
        if (double.IsFinite(v)) s.Set(bar, (decimal)v);
        else s.SetUnavailable(bar);
    }

    public static TimeSpan Period(ChartInfo? chart, TimeSpan fallback) =>
        chart?.GetTimeFrameTypeChartPeriod() is { } p && p > TimeSpan.Zero ? p : fallback;
}

/// <summary>Recalculates an indicator when an external hub announces new data.</summary>
internal sealed class HubWatch(Action onChanged)
{
    private ExternalSeriesHub? _hub;

    public long Generation { get; private set; } = -1;

    /// <summary>Follows <paramref name="hub"/> (null detaches) and records its generation.</summary>
    public void Track(ExternalSeriesHub? hub)
    {
        if (!ReferenceEquals(hub, _hub))
        {
            if (_hub != null) _hub.Changed -= onChanged;
            _hub = hub;
            if (hub != null) hub.Changed += onChanged;
        }

        Generation = hub?.Generation ?? -1;
    }

    public void Detach() => Track(null);
}

[DisplayName("Change-point detection (CUSUM)")]
[Category(IndicatorCategories.Macro)]
[Description("Two-sided cumulative sum (CUSUM) of normalized log returns: S+ accumulates unusual rises and S− unusual falls, each minus a drift. When one passes the threshold the market's character changed (marked, and that sum resets to zero). Each bar uses only itself and earlier bars.")]
public class CusumChangePoint : Indicator
{
    private readonly OptionalValueDataSeries _pos = new("SP", "S+") { Color = Color.FromArgb(255, 38, 194, 129), Width = 2 };
    private readonly OptionalValueDataSeries _neg = new("SN", "S−") { Color = Color.FromArgb(255, 240, 82, 90), Width = 2 };
    private readonly List<double> _r = new(), _spAfter = new(), _snAfter = new(), _since = new(), _alarm = new();
    private int _period = 50;
    private decimal _drift = 0.5m, _thr = 5;

    public CusumChangePoint() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _pos;
        DataSeries.Add(_neg);
        LineSeries.Add(new LineSeries("U", "+threshold") { Value = 5, Color = Color.FromArgb(110, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("D", "−threshold") { Value = -5, Color = Color.FromArgb(110, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(60, 140, 145, 160) });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Period (bars)", GroupName = "Calculation", Order = 10)]
    [Range(5, 1000)]
    public int Period { get => _period; set { _period = Math.Clamp(value, 5, 1000); RecalculateValues(); } }

    [Display(Name = "Drift (deviations subtracted per bar)", GroupName = "Calculation", Order = 11)]
    [Range(0, 5)]
    public decimal Drift { get => _drift; set { _drift = value; RecalculateValues(); } }

    [Display(Name = "Threshold", GroupName = "Calculation", Order = 12)]
    [Range(0.5, 50)]
    public decimal Threshold
    {
        get => _thr;
        set
        {
            _thr = value;
            LineSeries[0].Value = value;
            LineSeries[1].Value = -value;
            RecalculateValues();
        }
    }

    [Display(Name = "Mark change points", GroupName = "Drawing", Order = 20)]
    public bool ShowMarks { get; set; } = true;

    [Display(Name = "Show the card", GroupName = "Card", Order = 900)]
    public bool ShowCard { get; set; } = true;

    [Display(Name = "Card position", GroupName = "Card", Order = 901)]
    public CardCorner CardPosition { get; set; } = CardCorner.TopRight;

    [Display(Name = "Change point marker", GroupName = "Colors", Order = 32)]
    public Color MarkColor { get; set; } = Color.FromArgb(255, 251, 191, 36);

    /// <summary>One CUSUM step: S+ = max(0, S+ + z − k), S− = max(0, S− − z − k); alarms reset their sum.</summary>
    internal static (double Pos, double Neg, int Alarm, double PosAfter, double NegAfter) Step(double posBefore, double negBefore, double z, double drift, double threshold)
    {
        double sp = Math.Max(0, posBefore + z - drift), sn = Math.Max(0, negBefore - z - drift);
        bool up = sp > threshold, dn = sn > threshold;
        return (sp, sn, up ? 1 : dn ? -1 : 0, up ? 0 : sp, dn ? 0 : sn);
    }

    protected override void OnRecalculate()
    {
        _r.Clear();
        _spAfter.Clear();
        _snAfter.Clear();
        _since.Clear();
        _alarm.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        double close = (double)GetCandle(bar).Close, prev = bar > 0 ? (double)GetCandle(bar - 1).Close : double.NaN;
        MacroMath.Set(_r, bar, close > 0 && prev > 0 ? Math.Log(close / prev) : double.NaN);
        double z = double.NaN;
        if (bar >= _period)
        {
            double s = 0, q = 0;
            for (int i = bar - _period + 1; i <= bar; i++)
            {
                s += _r[i];
                q += _r[i] * _r[i];
            }

            double mu = s / _period, sd = Math.Sqrt(Math.Max(0, q / _period - mu * mu));
            z = sd > 0 ? (_r[bar] - mu) / sd : 0;
        }

        if (!double.IsFinite(z))
        {
            _pos.SetUnavailable(bar);
            _neg.SetUnavailable(bar);
            MacroMath.Set(_spAfter, bar, 0);
            MacroMath.Set(_snAfter, bar, 0);
            MacroMath.Set(_alarm, bar, 0);
            MacroMath.Set(_since, bar, double.NaN);
            return;
        }

        var st = Step(bar > 0 ? _spAfter[bar - 1] : 0, bar > 0 ? _snAfter[bar - 1] : 0, z, (double)_drift, (double)_thr);
        _pos.Set(bar, (decimal)st.Pos);
        _neg.Set(bar, (decimal)-st.Neg);
        MacroMath.Set(_alarm, bar, st.Alarm);
        double since0 = bar > 0 ? _since[bar - 1] : double.NaN;
        MacroMath.Set(_since, bar, st.Alarm != 0 ? 0 : double.IsNaN(since0) ? double.NaN : since0 + 1);
        MacroMath.Set(_spAfter, bar, st.PosAfter);
        MacroMath.Set(_snAfter, bar, st.NegAfter);
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box || CurrentBar == 0) return;
        if (ShowMarks)
            for (int bar = Math.Max(0, FirstVisibleBarNumber); bar <= Math.Min(CurrentBar - 1, LastVisibleBarNumber) && bar < _alarm.Count; bar++)
            {
                if (_alarm[bar] == 0) continue;
                bool up = _alarm[bar] > 0;
                int x = ChartInfo.GetXByBar(bar, false), y = box.GetYByValue(up ? _pos[bar] : _neg[bar]);
                int r = Math.Clamp(ChartInfo.PriceChartContainer.BarsWidth / 2 + 2, 3, 6);
                ctx.FillEllipse(MarkColor, new Rectangle(x - r, y - r, 2 * r, 2 * r));
            }

        if (!ShowCard) return;
        int last = CurrentBar - 1;
        double sp = _pos.IsAvailable(last) ? (double)_pos[last] : double.NaN, sn = _neg.IsAvailable(last) ? -(double)_neg[last] : double.NaN;
        double since = last < _since.Count ? _since[last] : double.NaN;
        int lastAlarm = -1;
        for (int i = Math.Min(last, _alarm.Count - 1); i >= 0; i--)
            if (_alarm[i] != 0)
            {
                lastAlarm = i;
                break;
            }

        StudyCard.Draw(ctx, box.Region, "CUSUM", new List<(string, string, Color)>
        {
            ("Statistic", MacroMath.Signed(sp >= sn ? sp : -sn), sp >= sn ? _pos.Color : _neg.Color),
            ("Bars since the last change", double.IsFinite(since) ? since.ToString("0", CultureInfo.InvariantCulture) : "—", default),
            ("Last change", lastAlarm < 0 ? "—" : _alarm[lastAlarm] > 0 ? "up" : "down", lastAlarm < 0 ? default : _alarm[lastAlarm] > 0 ? _pos.Color : _neg.Color),
        }, CardPosition);
    }
}

[DisplayName("Market HUD")]
[Category(IndicatorCategories.Macro)]
[Description("A card in a corner of the price pane reading the market at a glance: the trend (up with the close above the EMA and RSI above its midline, down below both, mixed otherwise), three rings (RSI, the aggressor-buy share of the flow window where the feed reports sides, and this bar's volume against its average), ATR as % of price and the open-interest change over the window as a line where the chart has open interest. The EMA is drawn on price.")]
public class MarketHud : Indicator
{
    private int _ema = 50, _rsi = 14, _atr = 14, _flow = 20;
    private decimal _mid = 50;
    private readonly OptionalValueDataSeries _emaSeries = new("EMA", "EMA") { Color = Color.FromArgb(255, 56, 189, 248), Width = 2 };
    private string _trend = "Mixed trend";
    private Color _trendColor = Opt.Muted;
    private double _rsiNow = double.NaN, _buyShare = double.NaN, _volRatio = double.NaN, _atrPct = double.NaN, _oiChange = double.NaN, _emaDist = double.NaN;
    private List<double> _oiLine = new();
    private readonly List<double> _emaRaw = new();

    public MarketHud() : base(true)
    {
        DataSeries[0] = _emaSeries;
        DrawAbovePrice = true;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "EMA length (bars)", GroupName = "Calculation", Order = 10)]
    [Range(2, 500)]
    public int EmaLength { get => _ema; set { _ema = Math.Clamp(value, 2, 500); RecalculateValues(); } }

    [Display(Name = "RSI length (bars)", GroupName = "Calculation", Order = 11)]
    [Range(2, 200)]
    public int RsiLength { get => _rsi; set { _rsi = Math.Clamp(value, 2, 200); RecalculateValues(); } }

    [Display(Name = "ATR length (bars)", GroupName = "Calculation", Order = 12)]
    [Range(2, 200)]
    public int AtrLength { get => _atr; set { _atr = Math.Clamp(value, 2, 200); RecalculateValues(); } }

    [Display(Name = "Flow window (bars)", GroupName = "Calculation", Order = 13)]
    [Range(2, 500)]
    public int FlowWindow { get => _flow; set { _flow = Math.Clamp(value, 2, 500); RecalculateValues(); } }

    [Display(Name = "RSI midline", GroupName = "Calculation", Order = 14)]
    [Range(1, 99)]
    public decimal RsiMidline { get => _mid; set { _mid = value; RecalculateValues(); } }

    [Display(Name = "Draw the EMA", GroupName = "Drawing", Order = 20)]
    public bool ShowEma { get => _emaSeries.VisualType != VisualMode.Hide; set => _emaSeries.VisualType = value ? VisualMode.Line : VisualMode.Hide; }

    [Display(Name = "Card position", GroupName = "Card", Order = 901)]
    public CardCorner CardPosition { get; set; } = CardCorner.BottomLeft;

    /// <summary>Wilder RSI (simple average seed over the first <paramref name="len"/> changes); NaN before it.</summary>
    internal static double[] Rsi(IReadOnlyList<double> c, int len)
    {
        var res = new double[c.Count];
        double up = 0, dn = 0;
        for (int i = 0; i < c.Count; i++)
        {
            res[i] = double.NaN;
            if (i == 0) continue;
            double d = c[i] - c[i - 1], u = Math.Max(0, d), w = Math.Max(0, -d);
            if (i <= len)
            {
                up += u / len;
                dn += w / len;
                if (i < len) continue;
            }
            else
            {
                up = (up * (len - 1) + u) / len;
                dn = (dn * (len - 1) + w) / len;
            }

            res[i] = dn <= 0 ? 100 : 100 - 100 / (1 + up / dn);
        }

        return res;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // The EMA per bar; the card is computed for the last bar only.
        double close = (double)GetCandle(bar).Close, k = 2.0 / (_ema + 1);
        double ema = bar == 0 ? close : _emaRaw[bar - 1] + k * (close - _emaRaw[bar - 1]);
        MacroMath.Set(_emaRaw, bar, ema);
        _emaSeries.Set(bar, (decimal)ema, bar >= _ema - 1);
        if (bar != CurrentBar - 1) return;
        int n = CurrentBar;
        var closes = Enumerable.Range(0, n).Select(i => (double)GetCandle(i).Close).ToList();
        var rsi = Rsi(closes, _rsi);
        double atr = 0;
        int atrN = 0;
        for (int i = Math.Max(1, n - _atr); i < n; i++)
        {
            var c = GetCandle(i);
            double pc = (double)GetCandle(i - 1).Close;
            atr += Math.Max((double)c.High - (double)c.Low, Math.Max(Math.Abs((double)c.High - pc), Math.Abs((double)c.Low - pc)));
            atrN++;
        }

        _rsiNow = rsi[n - 1];
        _emaDist = ema > 0 && bar >= _ema - 1 ? (close / ema - 1) * 100 : double.NaN;
        _atrPct = atrN > 0 && close > 0 ? atr / atrN / close * 100 : double.NaN;
        bool above = close > ema, hot = _rsiNow > (double)_mid;
        (_trend, _trendColor) = !double.IsFinite(_rsiNow) || bar < _ema - 1 ? ("Mixed trend", Opt.Muted)
            : above && hot ? ("Uptrend", Opt.Call) : !above && !hot ? ("Downtrend", Opt.Put) : ("Mixed trend", Opt.Muted);

        double buy = 0, vol = 0, avg = 0;
        bool sided = false;
        for (int i = Math.Max(0, n - _flow); i < n; i++)
        {
            var c = GetCandle(i);
            if (c.Bid + c.Ask > 0) sided = true;
            buy += (double)c.Ask;
            vol += (double)(c.Bid + c.Ask);
        }

        int avgN = 0;
        for (int i = Math.Max(0, n - 1 - _flow); i < n - 1; i++, avgN++) avg += (double)GetCandle(i).Volume;
        avg = avgN > 0 ? avg / avgN : 0;
        _buyShare = sided && vol > 0 ? buy / vol : double.NaN;
        _volRatio = avg > 0 ? (double)GetCandle(n - 1).Volume / avg : double.NaN;

        _oiLine = new List<double>();
        for (int i = Math.Max(0, n - _flow * 3); i < n; i++)
            if (GetCandle(i).HasOpenInterest) _oiLine.Add((double)GetCandle(i).OI);
        int then = Math.Max(0, n - 1 - _flow);
        _oiChange = GetCandle(n - 1).HasOpenInterest && GetCandle(then).HasOpenInterest && GetCandle(then).OI > 0
            ? ((double)GetCandle(n - 1).OI / (double)GetCandle(then).OI - 1) * 100 : double.NaN;
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar < 3) return;
        var region = ChartInfo.PriceChartContainer.Region;
        var big = new RenderFont("Arial", 15, FontStyle.Bold);
        int w = 250, h = 140;
        int col = (int)CardPosition % 3, row = (int)CardPosition / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 70 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        ctx.FillRectangle(Color.FromArgb(220, 18, 20, 26), new Rectangle(x, y, w, h), 6);
        ctx.DrawRectangle(new RenderPen(Color.FromArgb(90, 120, 130, 150), 1), new Rectangle(x, y, w, h), 6);
        ctx.DrawString(UiText.Tr(_trend), big, _trendColor, x + 10, y + 6);
        string dist = UiText.Tr($"vs EMA {MacroMath.Signed(_emaDist, "0.00")} %");
        ctx.DrawString(dist, StudyCard.Font, Opt.Text, x + w - ctx.MeasureString(dist, StudyCard.Font).Width - 10, y + 12);
        var rings = new (string Name, double Frac, string Text, Color Color)[]
        {
            ("RSI", _rsiNow / 100, Opt.N(_rsiNow, "0"), _rsiNow > (double)_mid ? Opt.Call : Opt.Put),
            ("Aggressor buys", _buyShare, double.IsFinite(_buyShare) ? (_buyShare * 100).ToString("0", CultureInfo.InvariantCulture) + " %" : "—", _buyShare >= 0.5 ? Opt.Call : Opt.Put),
            ("Volume", _volRatio / 3, double.IsFinite(_volRatio) ? _volRatio.ToString("0.0", CultureInfo.InvariantCulture) + "×" : "—", _volRatio >= 1 ? Opt.Flip : Opt.Muted),
        };
        for (int k = 0; k < rings.Length; k++)
        {
            int cx = x + 30 + k * 78, cy = y + 60;
            StudyCard.Ring(ctx, cx, cy, 15, rings[k].Frac, rings[k].Color);
            var ts = ctx.MeasureString(rings[k].Text, StudyCard.Bold);
            ctx.DrawString(rings[k].Text, StudyCard.Bold, Opt.Text, cx + 20, cy - ts.Height / 2 - 6);
            ctx.DrawString(UiText.Tr(rings[k].Name), StudyCard.Font, Opt.Dim, cx + 20, cy + 1);
        }

        int ly = y + 92;
        ctx.DrawString("ATR", StudyCard.Font, Opt.Dim, x + 10, ly);
        ctx.DrawString(Opt.Pct(_atrPct, "0.00"), StudyCard.Bold, Opt.Text, x + 44, ly);
        ctx.DrawString(UiText.Tr("Open interest"), StudyCard.Font, Opt.Dim, x + 110, ly);
        string oi = double.IsFinite(_oiChange) ? MacroMath.Signed(_oiChange, "0.00") + " %" : "—";
        ctx.DrawString(oi, StudyCard.Bold, _oiChange >= 0 ? Opt.Call : Opt.Put, x + w - ctx.MeasureString(oi, StudyCard.Bold).Width - 10, ly);
        if (_oiLine.Count >= 2)
        {
            double lo = _oiLine.Min(), hi = _oiLine.Max();
            var rect = new Rectangle(x + 10, y + 110, w - 20, 22);
            var pts = _oiLine.Select((v, i) => new Point(rect.Left + i * rect.Width / (_oiLine.Count - 1), rect.Bottom - (int)(hi > lo ? (v - lo) / (hi - lo) * rect.Height : rect.Height / 2.0))).ToArray();
            ctx.DrawLines(new RenderPen(Opt.Blue, 1), pts);
        }
        else
            ctx.DrawString(UiText.Tr("No open interest on this chart"), StudyCard.Font, Opt.Dim, x + 10, y + 114);
    }
}

[DisplayName("Spot ETF flows")]
[Category(IndicatorCategories.Macro)]
[Description("Daily net flow of the chart coin's spot ETFs (USD millions): inflows green, outflows red, the cumulative sum as a line and a card with the latest day, the sum, the streak and the largest day. Issuers publish a day's flow after the US close, so by default each day appears on the next UTC day (when it is known); days without a published value are gaps. Needs an ETF flows connection.")]
public class SpotEtfFlow : Indicator
{
    public enum Placement
    {
        [Display(Name = "When published (next UTC day)")] WhenKnown,
        [Display(Name = "On its trade date (hindsight)")] TradeDate,
    }

    private readonly OptionalValueDataSeries _in = new("IN", "Inflows") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Call, 200) };
    private readonly OptionalValueDataSeries _out = new("OUT", "Outflows") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Put, 200) };
    private readonly OptionalValueDataSeries _cum = new("CUM", "Cumulative") { VisualType = VisualMode.Hide, Color = Opt.Flip };
    private readonly HubWatch _watch;
    private readonly List<(int Bar, DateTime Day, double Flow)> _days = new();
    private string _asset = "";
    private string? _message;
    private Placement _placement = Placement.WhenKnown;
    private IReadOnlyList<EtfFlowDay>? _flows;

    public SpotEtfFlow() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _in;
        DataSeries.Add(_out);
        DataSeries.Add(_cum);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(70, 140, 145, 160) });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _watch = new HubWatch(() => DoActionInGuiThread(() =>
        {
            if (Hub is { } h && h.Generation != _watch.Generation) RecalculateValues();
        }));
    }

    [Display(Name = "Plot each day", GroupName = "Calculation", Order = 9)]
    public Placement PlotAt { get => _placement; set { _placement = value; RecalculateValues(); } }

    [Display(Name = "Large day (× the average day)", GroupName = "Calculation", Order = 10)]
    [Range(1, 20)]
    public decimal BigDay { get; set; } = 2;

    [Display(Name = "Draw the cumulative line", GroupName = "Drawing", Order = 20)]
    public bool ShowCumulative { get; set; } = true;

    [Display(Name = "Show the card", GroupName = "Card", Order = 900)]
    public bool ShowCard { get; set; } = true;

    [Browsable(false)]
    public EtfFlows? Service { get; set; }

    [Browsable(false)]
    public string? Status => _message;

    private EtfFlows? Hub => Service ?? EtfFlows.Shared;

    protected override void OnDispose() => _watch.Detach();

    /// <summary>The flow plotted on a bar of UTC day <paramref name="barDay"/>: the trade date itself, or the previous UTC day when placed when known.</summary>
    internal static EtfFlowDay? FlowFor(IReadOnlyList<EtfFlowDay> flows, DateTime barDay, Placement placement)
    {
        var target = placement == Placement.TradeDate ? barDay.Date : barDay.Date.AddDays(-1);
        int lo = 0, hi = flows.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var d = flows[mid].Date.Date;
            if (d == target) return flows[mid];
            if (d < target) lo = mid + 1;
            else hi = mid - 1;
        }

        return null;
    }

    protected override void OnRecalculate()
    {
        _watch.Track(Hub);
        _days.Clear();
        _asset = OptionsData.UnderlyingOf(InstrumentInfo?.Instrument, InstrumentInfo?.BaseAsset);
        _message = Hub == null ? EtfFlows.NotConfigured : Hub.Unavailable(_asset);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0 || bar == CurrentBar - 1) _flows = _message == null ? Hub!.Daily(_asset, GetCandle(0).Time.AddDays(-1)) : null;
        var flows = _flows;
        if (flows == null)
        {
            if (_message == null) _message = Hub!.Error(_asset) is { } e ? "ETF flows: " + e : "Loading ETF flows…";
            _in.SetUnavailable(bar);
            _out.SetUnavailable(bar);
            _cum.SetUnavailable(bar);
            return;
        }

        var day = GetCandle(bar).Time.Date;
        bool newDay = bar == 0 || GetCandle(bar - 1).Time.Date != day;
        var f = FlowFor(flows, day, _placement);
        double prevCum = bar > 0 && _cum.IsAvailable(bar - 1) ? (double)_cum[bar - 1] : 0;
        if (f == null)
        {
            _in.SetUnavailable(bar);
            _out.SetUnavailable(bar);
            _cum.Set(bar, (decimal)prevCum, bar > 0 && _cum.IsAvailable(bar - 1));
            return;
        }

        double m = f.NetUsd / 1e6;
        if (m >= 0) { _in.Set(bar, (decimal)m); _out.SetUnavailable(bar); }
        else { _out.Set(bar, (decimal)m); _in.SetUnavailable(bar); }
        // A flow counts once in the cumulative line, on the first bar where it is plotted.
        _cum.Set(bar, (decimal)(newDay ? prevCum + m : prevCum));
        if (newDay) _days.Add((bar, f.Date, m));
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        if (_message != null)
        {
            StudyCard.Message(ctx, box.Region, _message);
            return;
        }

        if (_days.Count == 0)
        {
            StudyCard.Message(ctx, box.Region, "No published ETF flows in this range.");
            return;
        }

        double mean = _days.Average(d => Math.Abs(d.Flow));
        foreach (var d in _days)
        {
            if (d.Bar < FirstVisibleBarNumber - 200 || d.Bar > LastVisibleBarNumber || mean <= 0 || Math.Abs(d.Flow) < mean * (double)BigDay) continue;
            int x = ChartInfo.GetXByBar(d.Bar, true), y = box.GetYByValue((decimal)d.Flow);
            string t = (d.Flow > 0 ? "+" : "−") + Math.Abs(d.Flow).ToString("0", CultureInfo.InvariantCulture) + "M";
            ctx.DrawString(t, StudyCard.Bold, d.Flow > 0 ? Opt.Call : Opt.Put, x + 2, d.Flow > 0 ? y - 12 : y + 2);
        }

        if (ShowCumulative)
        {
            int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
            var vals = Enumerable.Range(first, Math.Max(0, last - first + 1)).Where(_cum.IsAvailable).Select(i => (Bar: i, V: (double)_cum[i])).ToList();
            if (vals.Count > 1)
            {
                double lo = vals.Min(v => v.V), hi = vals.Max(v => v.V);
                var r = box.Region;
                int top = r.Top + 18, bottom = r.Bottom - 6;
                var pts = vals.Select(v => new Point(ChartInfo.GetXByBar(v.Bar, false), bottom - (int)(hi > lo ? (v.V - lo) / (hi - lo) * (bottom - top) : (bottom - top) / 2.0))).ToArray();
                ctx.DrawLines(new RenderPen(Opt.A(_cum.Color, 220), 2), pts);
                string label = UiText.Tr($"Cumulative {MacroMath.Signed(vals[^1].V, "0")}M");
                ctx.DrawString(label, StudyCard.Bold, _cum.Color, Math.Min(pts[^1].X + 4, r.Right - ctx.MeasureString(label, StudyCard.Bold).Width - 4), pts[^1].Y - 12);
            }
        }

        if (!ShowCard) return;
        var lastDay = _days[^1];
        int streak = 0;
        for (int i = _days.Count - 1; i >= 0 && lastDay.Flow != 0 && Math.Sign(_days[i].Flow) == Math.Sign(lastDay.Flow); i--) streak++;
        var bigDay = _days.OrderByDescending(d => Math.Abs(d.Flow)).First();
        double sum = _days.Sum(d => d.Flow);
        StudyCard.Draw(ctx, box.Region, $"Spot ETFs · {_asset}", new List<(string, string, Color)>
        {
            ($"{UiText.Tr("Latest day")} ({lastDay.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})", MacroMath.Signed(lastDay.Flow, "0.0") + "M", lastDay.Flow >= 0 ? Opt.Call : Opt.Put),
            ("Sum of the range", MacroMath.Signed(sum, "0") + "M", sum >= 0 ? Opt.Call : Opt.Put),
            ("Streak", UiText.Tr(lastDay.Flow >= 0 ? $"{streak} days of inflows" : $"{streak} days of outflows"), default),
            ($"{UiText.Tr("Largest day")} ({bigDay.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})", MacroMath.Signed(bigDay.Flow, "0") + "M", bigDay.Flow >= 0 ? Opt.Call : Opt.Put),
        }, CardCorner.TopRight, _placement == Placement.WhenKnown ? $"{Hub?.Provider?.Name} · each day shown when published" : $"{Hub?.Provider?.Name} · on trade dates (hindsight)");
    }
}

/// <summary>Price source of a bar.</summary>
public enum PriceSource
{
    [Display(Name = "Open")] Open,
    [Display(Name = "High")] High,
    [Display(Name = "Low")] Low,
    [Display(Name = "Close")] Close,
    [Display(Name = "(High + Low) / 2")] Hl2,
    [Display(Name = "(High + Low + Close) / 3")] Hlc3,
    [Display(Name = "(Open + High + Low + Close) / 4")] Ohlc4,
}

[DisplayName("Correlation coefficient")]
[Category(IndicatorCategories.Macro)]
[Description("Rolling Pearson correlation between this chart and another Binance symbol (ETHUSDT by default), on prices or log returns, with the area shaded, the +1, 0 and −1 levels and marks where it crosses zero. Bars where the other symbol has no candle are gaps. Each bar uses only itself and earlier bars.")]
public class CorrelationCoefficient : Indicator
{
    private readonly OptionalValueDataSeries _corr = new("CORR", "Correlation") { Color = Opt.Violet, Width = 2 };
    private readonly List<double> _a = new(), _b = new(), _rawA = new(), _rawB = new();
    private readonly HubWatch _watch;
    private string _symbol = "ETHUSDT";
    private bool _futures = true, _log;
    private int _len = 20;
    private PriceSource _src = PriceSource.Close;
    private IReadOnlyList<Kline> _kl = Array.Empty<Kline>();
    private string? _message;

    public CorrelationCoefficient() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _corr;
        LineSeries.Add(new LineSeries("P1", "+1") { Value = 1, Color = Color.FromArgb(80, 140, 145, 160), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(110, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("M1", "−1") { Value = -1, Color = Color.FromArgb(80, 140, 145, 160), LineDashStyle = LineDashStyle.Dot });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _watch = new HubWatch(() => DoActionInGuiThread(() =>
        {
            if (Hub is { } h && h.Generation != _watch.Generation) RecalculateValues();
        }));
    }

    [Display(Name = "Symbol to compare", GroupName = "Calculation", Order = 10)]
    public string Symbol { get => _symbol; set { _symbol = (value ?? "").Trim().ToUpperInvariant(); RecalculateValues(); } }

    [Display(Name = "From Binance Futures (otherwise Spot)", GroupName = "Calculation", Order = 11)]
    public bool Futures { get => _futures; set { _futures = value; RecalculateValues(); } }

    [Display(Name = "Length (bars)", GroupName = "Calculation", Order = 12)]
    [Range(3, 1000)]
    public int Length { get => _len; set { _len = Math.Clamp(value, 3, 1000); RecalculateValues(); } }

    [Display(Name = "Price", GroupName = "Calculation", Order = 13)]
    public PriceSource Source { get => _src; set { _src = value; RecalculateValues(); } }

    [Display(Name = "Use log returns", GroupName = "Calculation", Order = 14)]
    public bool LogReturns { get => _log; set { _log = value; RecalculateValues(); } }

    [Display(Name = "Shade the area", GroupName = "Drawing", Order = 20)]
    public bool ShowFill { get; set; } = true;

    [Display(Name = "Mark zero crossings", GroupName = "Drawing", Order = 21)]
    public bool ShowCrosses { get; set; } = true;

    [Browsable(false)]
    public ComparisonKlines? Service { get; set; }

    [Browsable(false)]
    public string? Status => _message;

    private ComparisonKlines? Hub => Service ?? ComparisonKlines.Shared;

    protected override void OnDispose() => _watch.Detach();

    internal static double Of(PriceSource src, decimal o, decimal h, decimal l, decimal c) => (double)(src switch
    {
        PriceSource.Open => o,
        PriceSource.High => h,
        PriceSource.Low => l,
        PriceSource.Hl2 => (h + l) / 2,
        PriceSource.Hlc3 => (h + l + c) / 3,
        PriceSource.Ohlc4 => (o + h + l + c) / 4,
        _ => c,
    });

    /// <summary>The other symbol's bar over [start, end) built from its candles (open of the first, high/low over all, close of the last); NaN when none.</summary>
    internal static double Aggregate(IReadOnlyList<Kline> kl, DateTime start, DateTime end, PriceSource src)
    {
        long s = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), e = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        int lo = 0, hi = kl.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (kl[mid].OpenTimeMs < s) lo = mid + 1;
            else hi = mid;
        }

        if (lo >= kl.Count || kl[lo].OpenTimeMs >= e) return double.NaN;
        decimal o = kl[lo].Open, h = kl[lo].High, l = kl[lo].Low, cl = kl[lo].Close;
        for (int k = lo; k < kl.Count && kl[k].OpenTimeMs < e; k++)
        {
            h = Math.Max(h, kl[k].High);
            l = Math.Min(l, kl[k].Low);
            cl = kl[k].Close;
        }

        return Of(src, o, h, l, cl);
    }

    protected override void OnRecalculate()
    {
        _watch.Track(Hub);
        _a.Clear();
        _b.Clear();
        _rawA.Clear();
        _rawB.Clear();
        _message = Hub == null ? "Comparison data is unavailable in this session." : null;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        var len = MacroMath.Period(ChartInfo, TimeSpan.FromMinutes(15));
        if (bar == 0 || bar == CurrentBar - 1)
        {
            _kl = Array.Empty<Kline>();
            if (Hub is { } hub)
            {
                var got = hub.Get(_symbol, _futures, GetCandle(0).Time, ComparisonKlines.MinutesFor(len));
                _kl = got.Klines;
                _message = _kl.Count == 0 ? got.Message : null;
            }
        }

        var kl = _kl;

        var end = bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : c.Time + len;
        double pb = Aggregate(kl, c.Time, end, _src), pa = Of(_src, c.Open, c.High, c.Low, c.Close);
        MacroMath.Set(_rawA, bar, pa);
        MacroMath.Set(_rawB, bar, pb);
        if (_log)
        {
            MacroMath.Set(_a, bar, bar > 0 && _rawA[bar - 1] > 0 && pa > 0 ? Math.Log(pa / _rawA[bar - 1]) : double.NaN);
            MacroMath.Set(_b, bar, bar > 0 && _rawB[bar - 1] > 0 && pb > 0 ? Math.Log(pb / _rawB[bar - 1]) : double.NaN);
        }
        else
        {
            MacroMath.Set(_a, bar, pa);
            MacroMath.Set(_b, bar, pb);
        }

        MacroMath.SetOrGap(_corr, bar, MacroMath.Pearson(_a, _b, bar, _len));
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        if (_message != null)
        {
            StudyCard.Message(ctx, box.Region, _message);
            return;
        }

        int first = Math.Max(1, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        int y0 = box.GetYByValue(0);
        for (int bar = first; bar <= last; bar++)
        {
            if (!_corr.IsAvailable(bar)) continue;
            double v = (double)_corr[bar];
            int x0 = ChartInfo.GetXByBar(bar, true), x1 = ChartInfo.GetXByBar(bar + 1, true);
            if (ShowFill && v != 0)
            {
                int y = box.GetYByValue((decimal)v);
                ctx.FillRectangle(Opt.A(v > 0 ? Opt.Call : Opt.Put, 45), new Rectangle(x0, Math.Min(y, y0), Math.Max(1, x1 - x0), Math.Abs(y - y0)));
            }

            if (ShowCrosses && _corr.IsAvailable(bar - 1) && Math.Sign((double)_corr[bar - 1]) != Math.Sign(v) && v != 0 && _corr[bar - 1] != 0)
            {
                int x = ChartInfo.GetXByBar(bar, false);
                bool up = v > 0;
                ctx.FillPolygon(up ? Opt.Call : Opt.Put, up
                    ? new[] { new Point(x, y0 - 9), new Point(x + 4, y0 - 3), new Point(x - 4, y0 - 3) }
                    : new[] { new Point(x, y0 + 9), new Point(x + 4, y0 + 3), new Point(x - 4, y0 + 3) });
            }
        }

        ctx.DrawString(_symbol + (_futures ? " · Binance Futures" : " · Binance Spot"), StudyCard.Font, Opt.Dim, box.Region.Left + 6, box.Region.Bottom - 14);
    }
}

[DisplayName("Polymarket odds vs price")]
[Category(IndicatorCategories.Macro)]
[Description("Compares a Polymarket market's probability (the chosen outcome, 0–100 %) with this chart's price: the rolling correlation of probability changes with returns, a divergence histogram (probability z-score minus price z-score, highlighted past the threshold) and a card with the reading. Each bar uses the last probability published before it closed. Public data; requests start only after you enter a market.")]
public class OddsVsPrice : Indicator
{
    public enum Outcome
    {
        [Display(Name = "Yes")] Yes,
        [Display(Name = "No")] No,
    }

    private readonly OptionalValueDataSeries _corr = new("CORR", "Correlation") { Color = Opt.Blue, Width = 2 };
    private readonly OptionalValueDataSeries _div = new("DIV", "Divergence") { VisualType = VisualMode.Histogram, Color = Opt.A(Opt.Muted, 150) };
    private readonly OptionalValueDataSeries _odds = new("ODDS", "Probability") { VisualType = VisualMode.Hide, Color = Opt.Flip };
    private readonly List<double> _p = new(), _o = new(), _dp = new(), _dr = new();
    private readonly HubWatch _watch;
    private string _market = "";
    private int _outcome, _window = 48, _zwin = 96;
    private decimal _thr = 2;
    private string? _message, _question;
    private IReadOnlyList<(DateTime T, double P)> _hist = Array.Empty<(DateTime, double)>();

    public OddsVsPrice() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _corr;
        DataSeries.Add(_div);
        DataSeries.Add(_odds);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _watch = new HubWatch(() => DoActionInGuiThread(() =>
        {
            if (Hub is { } h && h.Generation != _watch.Generation) RecalculateValues();
        }));
    }

    [Display(Name = "Polymarket market (URL or slug)", GroupName = "Calculation", Order = 10)]
    public string Market { get => _market; set { _market = (value ?? "").Trim(); RecalculateValues(); } }

    [Display(Name = "Outcome", GroupName = "Calculation", Order = 11)]
    public Outcome Side { get => (Outcome)_outcome; set { _outcome = (int)value; RecalculateValues(); } }

    [Display(Name = "Correlation window (bars)", GroupName = "Calculation", Order = 12)]
    [Range(5, 2000)]
    public int Window { get => _window; set { _window = Math.Clamp(value, 5, 2000); RecalculateValues(); } }

    [Display(Name = "z-score window (bars)", GroupName = "Calculation", Order = 13)]
    [Range(5, 5000)]
    public int ZWindow { get => _zwin; set { _zwin = Math.Clamp(value, 5, 5000); RecalculateValues(); } }

    [Display(Name = "Stretch threshold", GroupName = "Calculation", Order = 14)]
    [Range(0.5, 10)]
    public decimal Threshold { get => _thr; set { _thr = value; RecalculateValues(); } }

    [Display(Name = "Show the card", GroupName = "Card", Order = 900)]
    public bool ShowCard { get; set; } = true;

    [Browsable(false)]
    public PolymarketOdds? Service { get; set; }

    [Browsable(false)]
    public string? Status => _message;

    private PolymarketOdds? Hub => Service ?? PolymarketOdds.Shared;

    protected override void OnDispose() => _watch.Detach();

    /// <summary>The last probability published strictly before <paramref name="end"/> and at most two days before <paramref name="start"/>; NaN otherwise.</summary>
    internal static double ProbabilityAt(IReadOnlyList<(DateTime T, double P)> hist, DateTime start, DateTime end)
    {
        int lo = 0, hi = hist.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (hist[mid].T < end)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans >= 0 && hist[ans].T >= start - TimeSpan.FromDays(2) ? hist[ans].P : double.NaN;
    }

    protected override void OnRecalculate()
    {
        _watch.Track(Hub);
        _p.Clear();
        _o.Clear();
        _dp.Clear();
        _dr.Clear();
        _message = Hub == null ? "Polymarket data is unavailable in this session." : null;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var len = MacroMath.Period(ChartInfo, TimeSpan.FromMinutes(15));
        if (bar == 0 || bar == CurrentBar - 1)
        {
            _hist = Array.Empty<(DateTime, double)>();
            if (Hub is { } hub)
            {
                var st = hub.Odds(_market, _outcome, GetCandle(0).Time, (int)Math.Clamp(len.TotalMinutes, 1, 1440));
                _hist = st.History;
                _message = _hist.Count == 0 ? st.Message : null;
                _question = st.Info?.Question;
            }
        }

        var hist = _hist;

        var c = GetCandle(bar);
        var end = bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : DateTime.UtcNow;
        double odds = ProbabilityAt(hist, c.Time, end), px = (double)c.Close;
        MacroMath.Set(_o, bar, odds);
        MacroMath.Set(_p, bar, px);
        MacroMath.Set(_dp, bar, bar > 0 ? odds - _o[bar - 1] : double.NaN);
        MacroMath.Set(_dr, bar, bar > 0 && _p[bar - 1] > 0 ? Math.Log(px / _p[bar - 1]) : double.NaN);
        MacroMath.SetOrGap(_odds, bar, odds * 100);
        MacroMath.SetOrGap(_corr, bar, MacroMath.Pearson(_dp, _dr, bar, _window));
        double d = MacroMath.Z(_o, bar, _zwin) - MacroMath.Z(_p, bar, _zwin);
        MacroMath.SetOrGap(_div, bar, d);
        if (double.IsFinite(d)) _div.Colors[bar] = Math.Abs(d) >= (double)_thr ? (d > 0 ? Opt.A(Opt.Call, 220) : Opt.A(Opt.Put, 220)) : Opt.A(Opt.Muted, 120);
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        if (_message != null)
        {
            StudyCard.Message(ctx, box.Region, _message);
            return;
        }

        int first = Math.Max(1, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 2, LastVisibleBarNumber);
        double thr = (double)_thr;
        for (int bar = first; bar <= last; bar++)
        {
            if (!_div.IsAvailable(bar) || !_div.IsAvailable(bar - 1)) continue;
            double d = (double)_div[bar], p = (double)_div[bar - 1];
            if (Math.Abs(d) < thr || Math.Abs(p) >= thr) continue;
            int x = ChartInfo.GetXByBar(bar, false), y = box.GetYByValue((decimal)d);
            bool up = d > 0;
            ctx.FillPolygon(up ? Opt.Call : Opt.Put, up
                ? new[] { new Point(x, y - 12), new Point(x + 4, y - 5), new Point(x - 4, y - 5) }
                : new[] { new Point(x, y + 12), new Point(x + 4, y + 5), new Point(x - 4, y + 5) });
        }

        if (!ShowCard || CurrentBar == 0) return;
        int lb = CurrentBar - 1;
        double odds = _odds.IsAvailable(lb) ? (double)_odds[lb] : double.NaN, corr = _corr.IsAvailable(lb) ? (double)_corr[lb] : double.NaN,
            div = _div.IsAvailable(lb) ? (double)_div[lb] : double.NaN;
        string q = _question ?? PolymarketClient.SlugOf(_market);
        if (q.Length > 48) q = q[..47] + "…";
        StudyCard.Draw(ctx, box.Region, q, new List<(string, string, Color)>
        {
            (Side == Outcome.Yes ? "Probability of YES" : "Probability of NO", double.IsFinite(odds) ? odds.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "—", Opt.Flip),
            ("Correlation", MacroMath.Signed(corr), corr >= 0 ? Opt.Call : Opt.Put),
            ("Divergence (z)", MacroMath.Signed(div), Math.Abs(div) >= thr ? div > 0 ? Opt.Call : Opt.Put : default),
            ("Reading", !double.IsFinite(div) ? "—" : Math.Abs(div) >= thr ? div > 0 ? "odds ran ahead of price" : "odds lag price" : Math.Abs(corr) >= 0.5 ? "moving together" : "no clear relation", default),
        }, CardCorner.TopRight, "Polymarket · public odds");
    }
}
