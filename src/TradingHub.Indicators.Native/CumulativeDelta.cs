// Independently implemented CVD capabilities; KVP reference gouqizi2214730-source/trading-hub
// @ 50220ba91613161685f84384825d855cb4dbeb76 (MIT with Commons Clause). Stable existing type/series IDs retained.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Cumulative delta (CVD)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Observed/compact delta as line, area, candles, histogram or steps; reset, smoothing and closed-pivot divergences. Missing or disallowed inferred aggressors create an explicit gap until reset. Divergence series are written at confirmation, never at the retrospective pivot.")]
public class CumulativeDeltaCandles : Indicator
{
    public enum CvdStyle { Candles, Line, Area, Histogram, Steps }
    public enum ResetPeriod { Never, Session, Week, Month, Quarter, Year }
    public enum SmoothingKind { Simple, Exponential }
    public readonly record struct Divergence(int PivotBar, int PriorPivotBar, int AvailableBar, bool Bullish, decimal Value);
    private readonly record struct Row(decimal Close, decimal Smoothed, bool Available, int Start, int HighPivot, int LowPivot, int LastConfirmation, StudyCoverage Coverage);
    private readonly List<Row> _rows = new(); private readonly List<Divergence> _divergences = new();
    private readonly IReadOnlyList<Divergence> _divergenceView;
    private readonly CandleDataSeries _cvd = new("CVD", "Cumulative delta") { DrawCandleBorder = false };
    private readonly OptionalValueDataSeries _value = new("Value", "Value") { VisualType = VisualMode.OnlyValueOnAxis, Color = Color.LightSkyBlue, ShowZeroValue = true, Width = 2 };
    private readonly OptionalValueDataSeries _smooth = new("CVD.Smoothed", "Smoothed CVD") { Color = Color.Orange, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _signal = new("CVD.Divergence", "Confirmed divergence (+1 bullish / -1 bearish)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly RangeDataSeries _area = new("CVD.Area", "CVD area") { RangeColor = Color.FromArgb(50, Color.LightSkyBlue) };
    private CvdStyle _style; private ResetPeriod _reset; private int _smoothing, _radius = 5; private SmoothingKind _smoothingKind;
    private bool _infer, _divergence = true;
    public CumulativeDeltaCandles() : base(true)
    {
        _divergenceView = _divergences.AsReadOnly();
        Panel = IndicatorDataProvider.NewPanel; DataSeries[0] = _value; DataSeries.Add(_cvd); DataSeries.Add(_smooth); DataSeries.Add(_signal); DataSeries.Add(_area);
        EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final);
    }
    // ResetDaily is a real persisted legacy setting, not a renamed/removed serialization path.
    [Display(Name = "Reset each session", GroupName = "Calculation", Order = 10)]
    public bool ResetDaily
    {
        get => _reset == ResetPeriod.Session;
        set { if (value) _reset = ResetPeriod.Session; else if (_reset == ResetPeriod.Session) _reset = ResetPeriod.Never; RecalculateValues(); }
    }
    [Display(Name = "Reset period", GroupName = "Calculation", Order = 11)] public ResetPeriod Reset { get => _reset; set { _reset = value; RecalculateValues(); } }
    [Display(Name = "Style", GroupName = "Visualization")] public CvdStyle Style { get => _style; set { _style = value; RecalculateValues(); } }
    [Display(Name = "Smoothing bars (0 = off)", GroupName = "Smoothing"), Range(0, 500)] public int SmoothingPeriod { get => _smoothing; set { _smoothing = Math.Clamp(value, 0, 500); RecalculateValues(); } }
    [Display(Name = "Smoothing type", GroupName = "Smoothing")] public SmoothingKind Smoothing { get => _smoothingKind; set { _smoothingKind = value; RecalculateValues(); } }
    [Display(Name = "Allow explicitly inferred aggressors", GroupName = "Fidelity")] public bool AllowInferredAggressors { get => _infer; set { _infer = value; RecalculateValues(); } }
    [Display(Name = "Detect divergences", GroupName = "Divergence")] public bool DetectDivergences { get => _divergence; set { _divergence = value; RecalculateValues(); } }
    [Display(Name = "Closed bars on each side", GroupName = "Divergence"), Range(1, 100)] public int PivotRadius { get => _radius; set { _radius = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Paint at retrospective pivot (series remains causal)", GroupName = "Divergence")] public bool RetrospectiveDrawing { get; set; } = true;
    [Display(Name = "Positive color", GroupName = "Visualization")] public Color PositiveColor { get; set; } = Color.LimeGreen;
    [Display(Name = "Negative color", GroupName = "Visualization")] public Color NegativeColor { get; set; } = Color.IndianRed;
    [Browsable(false)] public IReadOnlyList<Divergence> Divergences => _divergenceView;
    [Browsable(false)] public StudyCoverage Coverage => _rows.Count == 0 ? 0 : _rows[^1].Coverage;
    [Browsable(false)] public string Status => FootprintStudyData.Label(Coverage) + ((Coverage & StudyCoverage.Compact) != 0 ? "; compact candle extrema unavailable" : "");
    protected override void OnRecalculate() { _rows.Clear(); _divergences.Clear(); }
    private bool ResetAt(int b) => b == 0 || Reset switch
    {
        ResetPeriod.Session => IsNewSession(b), ResetPeriod.Week => IsNewWeek(b), ResetPeriod.Month => IsNewMonth(b),
        ResetPeriod.Quarter => StudyMath.PeriodKey(GetCandle(b), StudyTimeFrame.Quarter) != StudyMath.PeriodKey(GetCandle(b - 1), StudyTimeFrame.Quarter),
        ResetPeriod.Year => GetCandle(b).SessionDate.Year != GetCandle(b - 1).SessionDate.Year, _ => false
    };
    protected override void OnCalculate(int bar, decimal value)
    {
        int target = bar;
        if (bar < _rows.Count - 1) { OnRecalculate(); Clear(); }
        if (_rows.Count == bar + 1) { _rows.RemoveAt(bar); _divergences.RemoveAll(d => d.AvailableBar >= bar); }
        for (int b = _rows.Count; b <= target; b++) Step(b);
    }
    private void Step(int b)
    {
        var c = GetCandle(b); bool reset = ResetAt(b);
        Row previous = b == 0 ? new(0, 0, true, 0, -1, -1, -1, 0) : _rows[b - 1];
        decimal open = reset ? 0 : previous.Close;
        bool valid = StudyMath.TryDelta(c, AllowInferredAggressors, out var delta, out var coverage) && (reset || previous.Available);
        decimal close = open + delta; int start = reset ? b : previous.Start;
        coverage |= reset ? 0 : previous.Coverage;
        _value.Set(b, close, valid);
        bool fullExtrema = valid && !c.HasCompactTrades && !c.HasInferredSide;
        _cvd[b] = valid ? new Candle(open, fullExtrema ? open + Math.Max(Math.Max(c.MaxDelta, delta), 0) : Math.Max(open, close), fullExtrema ? open + Math.Min(Math.Min(c.MinDelta, delta), 0) : Math.Min(open, close), close) : null;
        _value.VisualType = Style switch { CvdStyle.Candles => VisualMode.OnlyValueOnAxis, CvdStyle.Histogram => VisualMode.Histogram, CvdStyle.Steps => VisualMode.Steps, _ => VisualMode.Line };
        _cvd.Visible = Style == CvdStyle.Candles; _cvd.UpCandleColor = PositiveColor; _cvd.DownCandleColor = NegativeColor;
        _value.Colors[b] = Style == CvdStyle.Histogram ? close >= 0 ? PositiveColor : NegativeColor : _value.Color;
        _area[b] = valid && Style == CvdStyle.Area ? new RangeValue(Math.Max(0, close), Math.Min(0, close)) : new RangeValue();
        decimal smooth = close; bool warm = valid && SmoothingPeriod > 0 && b - start + 1 >= SmoothingPeriod;
        if (SmoothingPeriod > 0 && valid)
        {
            if (Smoothing == SmoothingKind.Exponential) smooth = reset ? close : previous.Smoothed + 2m / (SmoothingPeriod + 1) * (close - previous.Smoothed);
            else if (warm) { decimal sum = close; for (int i = b - SmoothingPeriod + 1; i < b; i++) { sum += _rows[i].Close; warm &= _rows[i].Available; } smooth = sum / SmoothingPeriod; }
        }
        _smooth.Set(b, smooth, warm);
        int hp = reset ? -1 : previous.HighPivot, lp = reset ? -1 : previous.LowPivot, last = reset ? b - 1 : previous.LastConfirmation;
        int closed = c.IsCandleFinished ? b : b - 1; decimal signal = 0;
        if (DetectDivergences)
            for (int confirmation = Math.Max(last + 1, start + 2 * PivotRadius); confirmation <= closed; confirmation++)
            {
                int pivot = confirmation - PivotRadius; var pc = GetCandle(pivot); bool high = true, low = true, available = true;
                for (int i = pivot - PivotRadius; i <= confirmation; i++)
                {
                    var other = GetCandle(i); available &= other.IsCandleFinished && (i == b ? valid : _rows[i].Available);
                    if (i == pivot) continue;
                    if (other.High > pc.High || i < pivot && other.High == pc.High) high = false;
                    if (other.Low < pc.Low || i < pivot && other.Low == pc.Low) low = false;
                }
                if (!available) continue;
                decimal pvalue = pivot == b ? close : _rows[pivot].Close;
                if (high)
                {
                    if (hp >= start && pc.High > GetCandle(hp).High && pvalue < _rows[hp].Close) { _divergences.Add(new(pivot, hp, b, false, pvalue)); signal -= 1; }
                    hp = pivot;
                }
                if (low)
                {
                    if (lp >= start && pc.Low < GetCandle(lp).Low && pvalue > _rows[lp].Close) { _divergences.Add(new(pivot, lp, b, true, pvalue)); signal += 1; }
                    lp = pivot;
                }
            }
        _signal.Set(b, signal, valid); _rows.Add(new(close, smooth, valid, start, hp, lp, closed, coverage));
        if (_divergences.Count > 1000) _divergences.RemoveRange(0, _divergences.Count - 1000);
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (Container == null || ChartInfo == null) return;
        FootprintStudyData.Legend(context, Container.Region, 2, $"CVD: {Status}; divergence values available only at confirmation", Color.LightSkyBlue);
        foreach (var d in _divergences)
        {
            int bar = RetrospectiveDrawing ? d.PivotBar : d.AvailableBar;
            if (bar < FirstVisibleBarNumber || bar > LastVisibleBarNumber) continue;
            int x = ChartInfo.GetXByBar(bar), y = Container.GetYByValue(d.Value);
            if (d.AvailableBar <= LastVisibleBarNumber) context.DrawString($"{(d.Bullish ? "Bullish" : "Bearish")} divergence (confirmed close {d.AvailableBar})", FootprintStudyData.AnnotationFont, d.Bullish ? PositiveColor : NegativeColor, x + 3, y);
        }
    }
}
