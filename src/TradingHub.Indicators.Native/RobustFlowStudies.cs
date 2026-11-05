// Independent robust-volume/flow heuristics; KVP capability reference gouqizi2214730-source/trading-hub
// @ 50220ba91613161685f84384825d855cb4dbeb76 (MIT with Commons Clause). Heuristics are not evidence of institutions or absorption.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Robust volume and colors")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Volume with preceding-window log-volume median/MAD scores and optional price-bar colors. No current-bar contamination of the reference window; zero-MAD spikes remain distinguishable from ordinary zero scores.")]
public class RobustVolume : Indicator
{
    public enum VolumeView { Volume, RelativeToMedian, RobustLogZ }
    private readonly OptionalValueDataSeries _volume = new("RobustVolume", "Robust volume") { VisualType = VisualMode.Histogram, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _score = new("RobustVolume.Score", "Robust log-volume Z") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly PaintbarsDataSeries _paint = new("RobustVolume.Paint", "Robust volume colors");
    private int _lookback = 50; private decimal _high = 2, _extreme = 4; private bool _paintBars; private VolumeView _view;
    public RobustVolume() : base(true) { Panel = IndicatorDataProvider.NewPanel; DataSeries[0] = _volume; DataSeries.Add(_score); DataSeries.Add(_paint); }
    [Display(Name = "Preceding bars", GroupName = "Calculation"), Range(5, 1000)] public int Lookback { get => _lookback; set { _lookback = Math.Clamp(value, 5, 1000); RecalculateValues(); } }
    [Display(Name = "View", GroupName = "Visualization")] public VolumeView View { get => _view; set { _view = value; RecalculateValues(); } }
    [Display(Name = "High-volume robust Z", GroupName = "Colors"), Range(0.1, 20)] public decimal HighThreshold { get => _high; set { _high = Math.Max(0.1m, value); RecalculateValues(); } }
    [Display(Name = "Extreme-volume robust Z", GroupName = "Colors"), Range(0.1, 40)] public decimal ExtremeThreshold { get => _extreme; set { _extreme = Math.Max(0.1m, value); RecalculateValues(); } }
    [Display(Name = "Color price bars", GroupName = "Colors")] public bool ColorPriceBars { get => _paintBars; set { _paintBars = value; RecalculateValues(); } }
    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar); bool causal = c.CanCalculateCausally && (c.Quality & TradingHub.Market.DataQuality.Partial) == 0 && (StudyMath.Coverage(c) & StudyCoverage.Incomplete) == 0;
        bool warm = bar >= Lookback && causal; decimal z = 0, median = 0;
        if (warm)
        {
            Span<decimal> window = stackalloc decimal[Lookback];
            for (int i = 0; i < Lookback; i++) { var previous = GetCandle(bar - Lookback + i); window[i] = previous.Volume; warm &= previous.CanCalculateCausally && (previous.Quality & TradingHub.Market.DataQuality.Partial) == 0 && (StudyMath.Coverage(previous) & StudyCoverage.Incomplete) == 0; }
            median = StudyMath.Median(window);
            for (int i = 0; i < Lookback; i++) window[i] = (decimal)Math.Log(1 + (double)Math.Max(0, GetCandle(bar - Lookback + i).Volume));
            if (warm) z = StudyMath.RobustZ((decimal)Math.Log(1 + (double)Math.Max(0, c.Volume)), window);
        }
        _score.Set(bar, z, warm); decimal output = View switch { VolumeView.RelativeToMedian => median > 0 ? c.Volume / median : 0, VolumeView.RobustLogZ => z, _ => c.Volume };
        bool available = causal && (View == VolumeView.Volume || warm && (View != VolumeView.RelativeToMedian || median > 0));
        // Saturate infinite zero-MAD score for drawing only; exported score retains the exact sentinel.
        _volume.Set(bar, View == VolumeView.RobustLogZ ? Math.Clamp(output, -20, 20) : output, available);
        Color color = !warm ? Color.SlateGray : z >= ExtremeThreshold ? Color.Fuchsia : z >= HighThreshold ? Color.Gold : c.Close >= c.Open ? Color.SeaGreen : Color.IndianRed;
        _volume.Colors[bar] = color; _paint[bar] = ColorPriceBars && warm ? color : null;
    }
}

[DisplayName("Delta bubbles and absorption heuristic")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Closed-bar measured delta (or explicitly candle-signed volume) bubbles using fixed or preceding-window robust log thresholds. Low price impact is labelled a heuristic, never proof of absorption. Missing/inferred aggressors are not silently estimated.")]
public class DeltaBubbles : Indicator
{
    public enum BubbleMetric { MeasuredDelta, CandleSignedVolume }
    public enum TriggerKind { RobustLogZ, FixedMagnitude }
    public readonly record struct Bubble(int Bar, int AvailableBar, decimal Price, decimal Value, decimal RobustZ, bool LowImpactHeuristic, StudyCoverage Coverage, bool Preview);
    private readonly List<Bubble> _bubbles = new();
    private readonly IReadOnlyList<Bubble> _bubbleView;
    private readonly OptionalValueDataSeries _signal = new("DeltaBubbles.Signal", "Confirmed bubble signed flow") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private int _lookback = 100, _impact = 20, _keep = 1000; private decimal _threshold = 3, _minimum = 100, _percentile = 20;
    private BubbleMetric _metric; private TriggerKind _trigger; private bool _inferred, _preview;
    private int _last = -1, _lastClosed = -1, _beforeClosed = -1;
    public DeltaBubbles() : base(true) { _bubbleView = _bubbles.AsReadOnly(); DataSeries[0] = _signal; EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final); }
    [Display(Name = "Metric", GroupName = "Calculation")] public BubbleMetric Metric { get => _metric; set { _metric = value; RecalculateValues(); } }
    [Display(Name = "Trigger", GroupName = "Calculation")] public TriggerKind Trigger { get => _trigger; set { _trigger = value; RecalculateValues(); } }
    [Display(Name = "Preceding bars", GroupName = "Calculation"), Range(5, 1000)] public int Lookback { get => _lookback; set { _lookback = Math.Clamp(value, 5, 1000); RecalculateValues(); } }
    [Display(Name = "Robust Z threshold", GroupName = "Calculation"), Range(0.1, 20)] public decimal Threshold { get => _threshold; set { _threshold = Math.Max(0.1m, value); RecalculateValues(); } }
    [Display(Name = "Fixed minimum magnitude", GroupName = "Calculation"), Range(0, 1000000000)] public decimal MinimumMagnitude { get => _minimum; set { _minimum = Math.Max(0, value); RecalculateValues(); } }
    [Display(Name = "Allow explicitly inferred aggressors", GroupName = "Fidelity")] public bool AllowInferredAggressors { get => _inferred; set { _inferred = value; RecalculateValues(); } }
    [Display(Name = "Impact reference bars", GroupName = "Heuristic"), Range(3, 100)] public int ImpactLookback { get => _impact; set { _impact = Math.Clamp(value, 3, 100); RecalculateValues(); } }
    [Display(Name = "Low-impact maximum percentile", GroupName = "Heuristic"), Range(0, 100)] public decimal LowImpactPercentile { get => _percentile; set { _percentile = Math.Clamp(value, 0, 100); RecalculateValues(); } }
    [Display(Name = "Maximum bubbles", GroupName = "Visualization"), Range(10, 5000)] public int MaximumBubbles { get => _keep; set { _keep = Math.Clamp(value, 10, 5000); RecalculateValues(); } }
    [Display(Name = "Show forming-bar preview (not a strategy signal)", GroupName = "Visualization")] public bool ShowPreview { get => _preview; set { _preview = value; RecalculateValues(); } }
    [Browsable(false)] public IReadOnlyList<Bubble> Bubbles => _bubbleView;
    private bool Flow(int bar, out decimal value, out StudyCoverage coverage)
    {
        var c = GetCandle(bar);
        if (!c.CanCalculateCausally) { value = 0; coverage = StudyMath.Coverage(c) | StudyCoverage.MissingBars; return false; }
        if (Metric == BubbleMetric.MeasuredDelta) return StudyMath.TryDelta(c, AllowInferredAggressors, out value, out coverage);
        value = c.Close == c.Open ? 0 : c.Close > c.Open ? c.Volume : -c.Volume; coverage = StudyMath.Coverage(c) | StudyCoverage.Estimated; return true;
    }
    protected override void OnRecalculate() { _bubbles.Clear(); _last = _lastClosed = _beforeClosed = -1; }
    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < _last) { OnRecalculate(); Clear(); }
        if (bar == _last) { _bubbles.RemoveAll(x => x.AvailableBar >= bar); _lastClosed = _beforeClosed; }
        _beforeClosed = _lastClosed;
        int closed = GetCandle(bar).IsCandleFinished ? bar : bar - 1; decimal signal = 0;
        for (int b = _lastClosed + 1; b <= closed; b++) signal += Evaluate(b, false, bar);
        if (!GetCandle(bar).IsCandleFinished) Evaluate(bar, true, bar);
        bool available = closed >= 0 && Flow(closed, out _, out _);
        _signal.Set(bar, signal, available); _last = bar; _lastClosed = closed;
        if (_bubbles.Count > MaximumBubbles) _bubbles.RemoveRange(0, _bubbles.Count - MaximumBubbles);
    }
    private decimal Evaluate(int bar, bool preview, int availableBar)
    {
        _bubbles.RemoveAll(x => x.Bar == bar);
        bool observed = Flow(bar, out var flow, out var coverage), warm = bar >= Lookback; decimal score = 0;
        if (observed && warm)
        {
            Span<decimal> window = stackalloc decimal[Lookback];
            for (int i = 0; i < Lookback; i++)
            {
                if (!Flow(bar - Lookback + i, out var previous, out _)) { warm = false; break; }
                window[i] = (decimal)Math.Log(1 + (double)Math.Abs(previous));
            }
            if (warm) score = StudyMath.RobustZ((decimal)Math.Log(1 + (double)Math.Abs(flow)), window);
        }
        bool triggered = observed && flow != 0 && (Trigger == TriggerKind.FixedMagnitude ? Math.Abs(flow) >= MinimumMagnitude : warm && score >= Threshold);
        if (!triggered || preview && !ShowPreview) return 0;
        var c = GetCandle(bar); bool impact = false;
        if (bar >= ImpactLookback)
        {
            decimal currentImpact = Math.Abs(c.Close - c.Open) / Math.Abs(flow); int below = 0, samples = 0;
            for (int i = bar - ImpactLookback; i < bar; i++)
                if (Flow(i, out var old, out _) && old != 0) { samples++; if (Math.Abs(GetCandle(i).Close - GetCandle(i).Open) / Math.Abs(old) <= currentImpact) below++; }
            impact = samples == ImpactLookback && below * 100m / samples <= LowImpactPercentile;
        }
        _bubbles.Add(new(bar, availableBar, c.High / 2 + c.Low / 2, flow, score, impact, coverage, preview));
        return preview ? 0 : flow;
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, "Delta bubbles: low impact is an absorption heuristic, not observed absorption", Color.Gold);
        foreach (var bubble in _bubbles)
        {
            if (bubble.Bar < FirstVisibleBarNumber || bubble.Bar > LastVisibleBarNumber) continue;
            int x = ChartInfo.GetXByBar(bubble.Bar), y = ChartInfo.GetYByPrice(bubble.Price), radius = Math.Clamp(4 + (int)Math.Min(16, Math.Log(1 + (double)Math.Abs(bubble.Value))), 4, 20);
            Color color = bubble.LowImpactHeuristic ? Color.Gold : bubble.Value > 0 ? Color.LimeGreen : Color.IndianRed;
            context.FillEllipse(Color.FromArgb(bubble.Preview ? 60 : 130, color), new Rectangle(x - radius, y - radius, radius * 2, radius * 2));
            context.DrawString($"{(bubble.Preview ? "Preview " : "")}{(bubble.LowImpactHeuristic ? "Low-impact heuristic " : "")}{bubble.Value} ({FootprintStudyData.Label(bubble.Coverage)})", FootprintStudyData.SmallFont, color, x + radius, y);
        }
    }
}
