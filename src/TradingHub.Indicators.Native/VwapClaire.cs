// Independent bar-based Claire-style study. KVP capability reference:
// gouqizi2214730-source/trading-hub @ 50220ba91613161685f84384825d855cb4dbeb76 (MIT with Commons Clause).
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("VWAP Claire (bar-based, 30 / 90 / 365 calendar days)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Volume-weighted HLC3 of completed exchange-session daily candles over 30/90/365 calendar days, held constant within each session. Optional session/week/month anchors use source-bar HLC3. These are bar-based estimates, not tick-exact VWAP; short history is labelled partial.")]
public class VwapClaire : Indicator
{
    private readonly OptionalValueDataSeries[] _rolling = new OptionalValueDataSeries[3], _anchor = new OptionalValueDataSeries[3];
    private readonly List<PeriodVwapState>[] _states = { new(), new(), new() };
    private HigherTimeframeData _days = new(StudyTimeFrame.Day, 367); private int _last = -1, _minimum = 1;
    private bool _daily, _weekly, _monthly, _full;
    private DateOnly? _rollingDate; private readonly decimal[] _means = new decimal[3]; private readonly bool[] _available = new bool[3]; private bool _partialHistory;
    private static readonly int[] Days = { 30, 90, 365 };
    public VwapClaire() : base(true)
    {
        Color[] colors = { Color.Khaki, Color.PaleGreen, Color.YellowGreen };
        for (int i = 0; i < 3; i++)
        {
            _rolling[i] = new($"Claire.R{Days[i]}", $"Bar-based {Days[i]} calendar-day VWAP") { Color = colors[i], ShowZeroValue = true, Width = 2, VisualType = VisualMode.Steps };
            if (i == 0) DataSeries[0] = _rolling[i]; else DataSeries.Add(_rolling[i]);
            _anchor[i] = new($"Claire.{StudyMath.Horizons[i]}", $"Bar-based {StudyMath.Horizons[i]} VWAP") { Color = colors[i], ShowZeroValue = true }; DataSeries.Add(_anchor[i]);
        }
        EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final);
    }
    [Display(Name = "Session HLC3 anchor", GroupName = "Anchors")] public bool Daily { get => _daily; set { _daily = value; RecalculateValues(); } }
    [Display(Name = "Weekly HLC3 anchor", GroupName = "Anchors")] public bool Weekly { get => _weekly; set { _weekly = value; RecalculateValues(); } }
    [Display(Name = "Monthly HLC3 anchor", GroupName = "Anchors")] public bool Monthly { get => _monthly; set { _monthly = value; RecalculateValues(); } }
    [Display(Name = "Minimum completed sessions", GroupName = "Calculation"), Range(1, 365)] public int MinimumSessions { get => _minimum; set { _minimum = Math.Clamp(value, 1, 365); RecalculateValues(); } }
    [Display(Name = "Require full calendar span", GroupName = "Calculation")] public bool RequireFullWindow { get => _full; set { _full = value; RecalculateValues(); } }
    [Browsable(false)] public StudyCoverage Coverage => StudyCoverage.Estimated | (_days.Closed.Count == 0 ? StudyCoverage.MissingBars : _days.Closed[^1].Coverage);
    [Browsable(false)] public string Status { get; private set; } = "Bar-based HLC3 estimates; waiting for completed session";
    protected override void OnRecalculate() { _last = -1; _days.Clear(); _rollingDate = null; Array.Clear(_means); Array.Clear(_available); foreach (var list in _states) list.Clear(); }
    protected override void OnCalculate(int bar, decimal value)
    {
        if (!StudyMath.CanResolve(ChartInfo?.TimeFrame, StudyTimeFrame.Day))
        {
            foreach (var line in _rolling) line.SetUnavailable(bar);
            foreach (var line in _anchor) line.SetUnavailable(bar);
            Status = "Bar-based Claire unavailable: source candles cannot resolve exchange-session daily boundaries";
            return;
        }
        int target = bar; if (bar < _last) { OnRecalculate(); Clear(); }
        int from = bar == _last ? bar : _last + 1;
        if (bar == _last) foreach (var list in _states) list.RemoveAt(bar);
        for (int b = from; b <= target; b++)
        {
            var c = GetCandle(b); _days.Update(b, c); var completed = _days.Closed;
            if (_rollingDate != c.SessionDate)
            {
                _partialHistory = false;
                for (int k = 0; k < 3; k++)
                {
                    DateOnly start = c.SessionDate.AddDays(-Days[k]); var moments = new WeightedStudyMoments(); int count = 0;
                    bool complete = completed.Count > 0 && completed[0].SessionDate <= start;
                    foreach (var day in completed)
                    {
                        if (day.SessionDate < start || day.SessionDate >= c.SessionDate) continue;
                        if (!day.SourceCompatible) { complete = false; continue; }
                        moments.Add(day.High / 3 + day.Low / 3 + day.Close / 3, day.Volume); count++; complete &= !day.Partial;
                    }
                    _partialHistory |= !complete; _means[k] = moments.Mean; _available[k] = moments.Weight > 0 && count >= MinimumSessions && (!RequireFullWindow || complete);
                }
                _rollingDate = c.SessionDate;
            }
            var contribution = StudyMath.Vwap(c, StudyVwapSource.BarHlc3, true, out var coverage);
            for (int k = 0; k < 3; k++)
            {
                _rolling[k].Set(b, _means[k], _available[k]);
                var sourceCoverage = coverage | (!StudyMath.IsContained(c, StudyMath.Horizons[k]) ? StudyCoverage.MissingBars : 0)
                    | (b == 0 && !StudyMath.IsPeriodOpening(c, StudyMath.Horizons[k]) ? StudyCoverage.Partial : 0);
                var state = (b == 0 ? default : _states[k][b - 1]).Add(StudyMath.PeriodKey(c, StudyMath.Horizons[k]), contribution, sourceCoverage, b == 0);
                _states[k].Add(state); _anchor[k].Set(b, state.Moments.Mean, (k == 0 ? Daily : k == 1 ? Weekly : Monthly) && StudyMath.HasValue(state.Moments, state.Coverage));
            }
            Status = "Bar-based daily HLC3 estimates; " + (_partialHistory ? "partial loaded calendar history" : "full requested calendar span") + "; current session excluded from rolling values"; _last = b;
        }
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    { if (ChartInfo != null) FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, $"Claire: {Status}", Color.Khaki); }
}
