// Independent public Bollinger formula and causal HTF mapping; no VPFM implementation imported.
// KVP capability reference gouqizi2214730-source/trading-hub @ 50220ba91613161685f84384825d855cb4dbeb76 (MIT with Commons Clause).
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Triple Bollinger (mapped or custom periods)")]
[Category(IndicatorCategories.Trend)]
[Description("Three independently configurable mapped periods, length and deviation. Only completed aggregate candles are projected forward; first warm-up stays unavailable. Monthly periods are real session-calendar months.")]
public class TripleBollinger : Indicator
{
    private readonly OptionalValueDataSeries[,] _series = new OptionalValueDataSeries[3, 3];
    private HigherTimeframeData[] _frames = Array.Empty<HigherTimeframeData>();
    private readonly int[] _computed = { -1, -1, -1 }; private readonly WeightedStudyMoments[] _values = new WeightedStudyMoments[3];
    private int _last = -1, _length = 20; private decimal _multiplier = 2; private bool _mapped = true;
    private StudyTimeFrame _first = StudyTimeFrame.Minutes5, _second = StudyTimeFrame.Minutes15, _third = StudyTimeFrame.Hour;
    public TripleBollinger() : base(true)
    {
        Color[] colors = { Color.DodgerBlue, Color.Orange, Color.MediumPurple };
        for (int k = 0; k < 3; k++) for (int j = 0; j < 3; j++)
        {
            string name = j switch { 0 => "Middle", 1 => "Upper", _ => "Lower" };
            var series = new OptionalValueDataSeries($"TripleBB.{k}.{name}", $"Bollinger {k + 1} {name}") { Color = colors[k], VisualType = VisualMode.Steps, ShowZeroValue = true, Width = j == 0 ? 1 : 2 };
            _series[k, j] = series; if (k == 0 && j == 0) DataSeries[0] = series; else DataSeries.Add(series);
        }
        EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final);
    }
    [Display(Name = "Use mapped periods", GroupName = "Periods")] public bool UseMappedPeriods { get => _mapped; set { _mapped = value; RecalculateValues(); } }
    [Display(Name = "First custom period", GroupName = "Periods")] public StudyTimeFrame FirstPeriod { get => _first; set { _first = value; RecalculateValues(); } }
    [Display(Name = "Second custom period", GroupName = "Periods")] public StudyTimeFrame SecondPeriod { get => _second; set { _second = value; RecalculateValues(); } }
    [Display(Name = "Third custom period", GroupName = "Periods")] public StudyTimeFrame ThirdPeriod { get => _third; set { _third = value; RecalculateValues(); } }
    [Display(Name = "Length in completed candles", GroupName = "Calculation"), Range(2, 500)] public int Length { get => _length; set { _length = Math.Clamp(value, 2, 500); RecalculateValues(); } }
    [Display(Name = "Population deviations", GroupName = "Calculation"), Range(0.1, 10)] public decimal Multiplier { get => _multiplier; set { _multiplier = Math.Clamp(value, 0.1m, 10); RecalculateValues(); } }
    [Browsable(false)] public string Status => "Closed aggregates only; first loaded aggregate may be partial; coarser source cannot resolve finer selections";
    protected override void OnRecalculate() { _last = -1; _frames = Array.Empty<HigherTimeframeData>(); Array.Fill(_computed, -1); Array.Clear(_values); }
    private void InitializeFrames()
    {
        var mapped = StudyMath.BollingerMap(ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromMinutes(1));
        var selected = UseMappedPeriods ? new[] { mapped.A, mapped.B, mapped.C } : new[] { FirstPeriod, SecondPeriod, ThirdPeriod };
        _frames = selected.Select(x => new HigherTimeframeData(x, Length + 2)).ToArray();
    }
    protected override void OnCalculate(int bar, decimal value)
    {
        int target = bar; if (bar < _last) { OnRecalculate(); Clear(); }
        if (_frames.Length == 0) InitializeFrames();
        int from = bar == _last ? bar : _last + 1;
        for (int b = from; b <= target; b++)
        {
            var c = GetCandle(b);
            for (int k = 0; k < 3; k++)
            {
                var frame = _frames[k]; frame.Update(b, c); var closed = frame.Closed;
                bool available = StudyMath.CanResolve(ChartInfo?.TimeFrame, frame.Frame) && closed.Count >= Length;
                if (available) for (int i = closed.Count - Length; i < closed.Count; i++) available &= !closed[i].Partial;
                int end = closed.Count == 0 ? -1 : closed[^1].EndBar;
                if (available && end != _computed[k])
                {
                    var moments = new WeightedStudyMoments();
                    for (int i = closed.Count - Length; i < closed.Count; i++) moments.Add(closed[i].Close, 1);
                    _values[k] = moments; _computed[k] = end;
                }
                var result = _values[k]; decimal width = available ? result.Deviation * Multiplier : 0;
                _series[k, 0].Set(b, result.Mean, available); _series[k, 1].Set(b, result.Mean + width, available); _series[k, 2].Set(b, result.Mean - width, available);
            }
            _last = b;
        }
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    { if (ChartInfo != null) FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, $"Triple Bollinger: {Status}", Color.MediumPurple); }
}
