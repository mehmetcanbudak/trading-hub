// Independent session/level studies; KVP capability reference gouqizi2214730-source/trading-hub
// @ 50220ba91613161685f84384825d855cb4dbeb76, MIT with Commons Clause.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Market;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Session backgrounds and ranges")]
[Category(IndicatorCategories.Levels)]
[Description("Three configurable local-time windows with DST/overnight membership, background, range and opening levels. Source bars are assigned by opening time; no invented intrabar session split.")]
public class SessionBackgrounds : Indicator
{
    private readonly ValueDataSeries _membership = new("Session.Membership", "Session membership bitmask") { VisualType = VisualMode.Hide };
    public readonly record struct WindowRange(int SourceStartBar, DateOnly Date, decimal Open, decimal High, decimal Low, bool Partial);
    private readonly List<WindowRange?>[] _ranges = { new(), new(), new() };
    private string _zone = "America/New_York"; private TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private TimeSpan _s1 = TimeSpan.FromHours(20), _e1 = TimeSpan.FromHours(0), _s2 = TimeSpan.FromHours(2), _e2 = TimeSpan.FromHours(5), _s3 = TimeSpan.FromHours(9.5), _e3 = TimeSpan.FromHours(16);
    private static readonly Color[] SessionColors = { Color.MediumPurple, Color.SteelBlue, Color.DarkOrange };
    private static readonly RenderPen[] SessionPens = { new(Color.MediumPurple), new(Color.SteelBlue), new(Color.DarkOrange) };
    private static readonly string[] SessionLabels = { "Window 1: opening-time bars", "Window 2: opening-time bars", "Window 3: opening-time bars" };
    public SessionBackgrounds() : base(true) { DataSeries[0] = _membership; EnableCustomDrawing = true; DrawAbovePrice = false; SubscribeToDrawingEvents(DrawingLayouts.Final); }
    [Display(Name = "Time zone", GroupName = "Session")]
    public string TimeZoneId { get => _zone; set { _timeZone = TimeZoneInfo.FindSystemTimeZoneById(value); _zone = value; RecalculateValues(); } }
    private static TimeSpan Valid(TimeSpan value) { if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(value)); return value; }
    [Display(Name = "Window 1 start", GroupName = "Session")] public TimeSpan FirstStart { get => _s1; set { _s1 = Valid(value); RecalculateValues(); } }
    [Display(Name = "Window 1 end", GroupName = "Session")] public TimeSpan FirstEnd { get => _e1; set { _e1 = Valid(value); RecalculateValues(); } }
    [Display(Name = "Window 2 start", GroupName = "Session")] public TimeSpan SecondStart { get => _s2; set { _s2 = Valid(value); RecalculateValues(); } }
    [Display(Name = "Window 2 end", GroupName = "Session")] public TimeSpan SecondEnd { get => _e2; set { _e2 = Valid(value); RecalculateValues(); } }
    [Display(Name = "Window 3 start", GroupName = "Session")] public TimeSpan ThirdStart { get => _s3; set { _s3 = Valid(value); RecalculateValues(); } }
    [Display(Name = "Window 3 end", GroupName = "Session")] public TimeSpan ThirdEnd { get => _e3; set { _e3 = Valid(value); RecalculateValues(); } }
    [Display(Name = "Window 1", GroupName = "Visualization")] public bool FirstEnabled { get; set; } = true;
    [Display(Name = "Window 2", GroupName = "Visualization")] public bool SecondEnabled { get; set; } = true;
    [Display(Name = "Window 3", GroupName = "Visualization")] public bool ThirdEnabled { get; set; } = true;
    [Display(Name = "Opening and high/low range", GroupName = "Visualization")] public bool ShowRanges { get; set; } = true;
    [Display(Name = "Background opacity", GroupName = "Visualization"), Range(0, 80)] public int Opacity { get; set; } = 20;
    private static bool Inside(TimeSpan t, TimeSpan start, TimeSpan end) => start == end || (start < end ? t >= start && t < end : t >= start || t < end);
    /// <summary>Drawing geometry through this bar, independent of the visible viewport; Partial never claims a complete session range.</summary>
    public WindowRange? DrawingRangeAt(int window, int bar) => window >= 0 && window < 3 && bar >= 0 && bar < _ranges[window].Count ? _ranges[window][bar] : null;
    protected override void OnRecalculate() { foreach (var list in _ranges) list.Clear(); }
    private (TimeSpan Start, TimeSpan End) Window(int k) => k switch { 0 => (FirstStart, FirstEnd), 1 => (SecondStart, SecondEnd), _ => (ThirdStart, ThirdEnd) };
    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < _ranges[0].Count - 1) { OnRecalculate(); Clear(); }
        int first = Math.Min(bar, _ranges[0].Count);
        for (int b = first; b <= bar; b++)
        {
            var c = GetCandle(b);
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(c.Time, DateTimeKind.Utc), _timeZone);
            int bits = 0;
            for (int k = 0; k < 3; k++)
            {
                var (start, end) = Window(k); WindowRange? current = null;
                if (Inside(local.TimeOfDay, start, end))
                {
                    bits |= 1 << k;
                    var date = DateOnly.FromDateTime(local); if (start >= end && local.TimeOfDay < end) date = date.AddDays(-1);
                    bool partial = !c.CanCalculateCausally || (c.Quality & DataQuality.Partial) != 0 || (StudyMath.Coverage(c) & StudyCoverage.Incomplete) != 0;
                    current = b > 0 && _ranges[k][b - 1] is { } previous && previous.Date == date
                        ? previous with { High = Math.Max(previous.High, c.High), Low = Math.Min(previous.Low, c.Low), Partial = previous.Partial || partial }
                        : new(b, date, c.Open, c.High, c.Low, partial || b == 0 && local.TimeOfDay != start);
                }
                if (_ranges[k].Count == b) _ranges[k].Add(current); else _ranges[k][b] = current;
            }
            _membership[b] = bits;
        }
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        var colors = SessionColors;
        var area = ChartInfo.PriceChartContainer.Region;
        for (int k = 0; k < 3; k++)
        {
            if (!(k == 0 ? FirstEnabled : k == 1 ? SecondEnabled : ThirdEnabled)) continue;
            int b = FirstVisibleBarNumber;
            while (b <= LastVisibleBarNumber && b < CurrentBar)
            {
                if (((int)_membership[b] & (1 << k)) == 0) { b++; continue; }
                var date = _ranges[k][b]!.Value.Date;
                while (b + 1 <= LastVisibleBarNumber && b + 1 < CurrentBar && _ranges[k][b + 1] is { } next && next.Date == date) b++;
                var range = _ranges[k][b]!.Value;
                decimal high = range.High, low = range.Low, open = range.Open;
                int x = ChartInfo.GetXByBar(Math.Max(FirstVisibleBarNumber, range.SourceStartBar)), end = ChartInfo.GetXByBar(b + 1);
                context.FillRectangle(Color.FromArgb(Math.Clamp(Opacity, 0, 80), colors[k]), new Rectangle(x, area.Top, Math.Max(1, end - x), area.Height));
                if (ShowRanges)
                {
                    context.DrawLine(SessionPens[k], x, ChartInfo.GetYByPrice(high), end, ChartInfo.GetYByPrice(high));
                    context.DrawLine(SessionPens[k], x, ChartInfo.GetYByPrice(low), end, ChartInfo.GetYByPrice(low));
                    context.DrawLine(SessionPens[k], x, ChartInfo.GetYByPrice(open), end, ChartInfo.GetYByPrice(open));
                    context.DrawString(range.Partial ? SessionLabels[k] + "; partial loaded/source coverage" : SessionLabels[k], FootprintStudyData.SmallFont, colors[k], x + 2, ChartInfo.GetYByPrice(high) - 12);
                }
                b++;
            }
        }
    }
}

[DisplayName("Calendar and session key levels")]
[Category(IndicatorCategories.Levels)]
[Description("Four-hour/session/week/month/quarter/year opens and completed-period high, low and midpoint, plus the completed Monday range. Calendar boundaries follow the exchange session. First loaded periods are explicitly partial.")]
public class KeyLevels : Indicator
{
    private readonly record struct State(long Key, decimal Open, decimal High, decimal Low, decimal PriorHigh, decimal PriorLow, bool HasPrior, bool HighTouched, bool LowTouched, bool Partial,
        DateTime OpenAvailableAt, DateTime AvailableAt, DateTime PriorAvailableAt, bool Compatible, bool PriorCompatible, bool PriorPartial, bool TouchCoverageComplete);
    private readonly List<State>[] _states = new List<State>[6];
    private readonly OptionalValueDataSeries[,] _series = new OptionalValueDataSeries[6, 4];
    private static readonly StudyTimeFrame[] Periods = { StudyTimeFrame.Hours4, StudyTimeFrame.Day, StudyTimeFrame.Week, StudyTimeFrame.Month, StudyTimeFrame.Quarter, StudyTimeFrame.Year };
    private readonly record struct MondayState(long Week, decimal High, decimal Low, bool HasMonday, bool Partial, DateTime AvailableAt);
    private readonly List<MondayState> _mondays = new();
    private readonly OptionalValueDataSeries _mondayHigh = new("KeyLevels.Monday.High", "Monday high") { Color = Color.Purple, VisualType = VisualMode.Steps };
    private readonly OptionalValueDataSeries _mondayLow = new("KeyLevels.Monday.Low", "Monday low") { Color = Color.Purple, VisualType = VisualMode.Steps };
    private bool _fourHour = true, _session = true, _weekly = true, _monthly = true, _quarterly, _yearly = true;
    private bool _hideTouched, _showOpen = true, _showPrior = true, _monday = true;
    public KeyLevels() : base(true)
    {
        Color[] colors = { Color.Gray, Color.Gold, Color.Silver, Color.DodgerBlue, Color.SeaGreen, Color.Coral };
        for (int k = 0; k < 6; k++)
        {
            _states[k] = new();
            for (int j = 0; j < 4; j++)
            {
                string name = j switch { 0 => "Open", 1 => "PriorHigh", 2 => "PriorLow", _ => "PriorMid" };
                var line = new OptionalValueDataSeries($"KeyLevels.{Periods[k]}.{name}", $"{Periods[k]} {name}") { Color = colors[k], VisualType = VisualMode.Steps, ShowZeroValue = true };
                _series[k, j] = line; if (k == 0 && j == 0) DataSeries[0] = line; else DataSeries.Add(line);
            }
        }
        DataSeries.Add(_mondayHigh); DataSeries.Add(_mondayLow); EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final);
    }
    [Display(Name = "Show period opens", GroupName = "Levels")] public bool ShowOpens { get => _showOpen; set { _showOpen = value; RecalculateValues(); } }
    [Display(Name = "Show prior high/low/midpoint", GroupName = "Levels")] public bool ShowPriorLevels { get => _showPrior; set { _showPrior = value; RecalculateValues(); } }
    [Display(Name = "Hide high/low after confirmed touch", GroupName = "Levels")] public bool HideTestedHighLow { get => _hideTouched; set { _hideTouched = value; RecalculateValues(); } }
    [Display(Name = "Completed Monday range", GroupName = "Levels")] public bool MondayRange { get => _monday; set { _monday = value; RecalculateValues(); } }
    [Display(Name = "Four-hour levels", GroupName = "Periods")] public bool FourHour { get => _fourHour; set { _fourHour = value; RecalculateValues(); } }
    [Display(Name = "Session levels", GroupName = "Periods")] public bool Session { get => _session; set { _session = value; RecalculateValues(); } }
    [Display(Name = "Weekly levels", GroupName = "Periods")] public bool Weekly { get => _weekly; set { _weekly = value; RecalculateValues(); } }
    [Display(Name = "Monthly levels", GroupName = "Periods")] public bool Monthly { get => _monthly; set { _monthly = value; RecalculateValues(); } }
    [Display(Name = "Quarterly levels", GroupName = "Periods")] public bool Quarterly { get => _quarterly; set { _quarterly = value; RecalculateValues(); } }
    [Display(Name = "Yearly levels", GroupName = "Periods")] public bool Yearly { get => _yearly; set { _yearly = value; RecalculateValues(); } }
    [Browsable(false)] public string Status => "Partial or boundary-incompatible periods are excluded; prior levels start only after source availability; touches require closed causal bars";
    protected override void OnRecalculate() { foreach (var list in _states) list.Clear(); _mondays.Clear(); }
    protected override void OnCalculate(int bar, decimal value)
    {
        int target = bar; if (bar < _states[0].Count - 1) { OnRecalculate(); Clear(); }
        if (_states[0].Count == bar + 1) { foreach (var list in _states) list.RemoveAt(bar); _mondays.RemoveAt(bar); }
        for (int b = _states[0].Count; b <= target; b++)
        {
            var c = GetCandle(b);
            DateTime knownAt = c.CanCalculateCausally && c.IsCandleFinished ? c.AvailableAt : c.LastTime;
            for (int k = 0; k < 6; k++)
            {
                long key = StudyMath.PeriodKey(c, Periods[k]); State previous = b == 0 ? default : _states[k][b - 1]; bool newPeriod = b == 0 || key != previous.Key;
                bool compatible = StudyMath.IsContained(c, Periods[k]) && (c.Quality & DataQuality.Partial) == 0;
                State state = newPeriod
                    ? new(key, c.Open, c.High, c.Low, previous.High, previous.Low, b > 0, false, false, b == 0 && !StudyMath.IsPeriodOpening(c, Periods[k]),
                        (c.Quality & (DataQuality.Compact | DataQuality.Synthetic)) != 0 ? c.AvailableAt : c.Time, c.AvailableAt, previous.AvailableAt, compatible, previous.Compatible, previous.Partial, true)
                    : previous with { High = Math.Max(previous.High, c.High), Low = Math.Min(previous.Low, c.Low), Compatible = previous.Compatible && compatible,
                        AvailableAt = c.AvailableAt > previous.AvailableAt ? c.AvailableAt : previous.AvailableAt };
                if (state.HasPrior)
                {
                    var closed = c.IsCandleFinished ? c : b > 0 && !newPeriod ? GetCandle(b - 1) : null;
                    if (closed != null)
                    {
                        bool causal = closed.CanCalculateCausally && (closed.Quality & DataQuality.Partial) == 0;
                        state = state with { TouchCoverageComplete = state.TouchCoverageComplete && causal,
                            HighTouched = state.HighTouched || causal && closed.Low <= state.PriorHigh && closed.High >= state.PriorHigh,
                            LowTouched = state.LowTouched || causal && closed.Low <= state.PriorLow && closed.High >= state.PriorLow };
                    }
                }
                _states[k].Add(state); bool enabled = k switch { 0 => FourHour, 1 => Session, 2 => Weekly, 3 => Monthly, 4 => Quarterly, _ => Yearly };
                bool priorKnown = enabled && ShowPriorLevels && state.HasPrior && state.PriorCompatible && !state.PriorPartial && state.PriorAvailableAt <= knownAt;
                _series[k, 0].Set(b, state.Open, enabled && ShowOpens && state.Compatible && !state.Partial && state.OpenAvailableAt <= knownAt);
                _series[k, 1].Set(b, state.PriorHigh, priorKnown && (!HideTestedHighLow || state.TouchCoverageComplete && !state.HighTouched));
                _series[k, 2].Set(b, state.PriorLow, priorKnown && (!HideTestedHighLow || state.TouchCoverageComplete && !state.LowTouched));
                _series[k, 3].Set(b, state.PriorHigh / 2 + state.PriorLow / 2, priorKnown);
                if (newPeriod && b > 0) for (int j = 0; j < 4; j++) _series[k, j].SetPointOfEndLine(b - 1);
            }
            long week = StudyMath.PeriodKey(c, StudyTimeFrame.Week);
            MondayState monday = b > 0 && _mondays[b - 1].Week == week ? _mondays[b - 1] : new(week, 0, 0, false, false, default);
            if (c.SessionDate.DayOfWeek == DayOfWeek.Monday)
                monday = new(week, monday.HasMonday ? Math.Max(monday.High, c.High) : c.High, monday.HasMonday ? Math.Min(monday.Low, c.Low) : c.Low, true,
                    monday.Partial || !StudyMath.IsContained(c, StudyTimeFrame.Day) || (c.Quality & DataQuality.Partial) != 0 || b == 0 && !StudyMath.IsPeriodOpening(c, StudyTimeFrame.Day),
                    c.AvailableAt > monday.AvailableAt ? c.AvailableAt : monday.AvailableAt);
            _mondays.Add(monday); bool complete = MondayRange && monday.HasMonday && !monday.Partial && c.SessionDate.DayOfWeek != DayOfWeek.Monday && monday.AvailableAt <= knownAt;
            _mondayHigh.Set(b, monday.High, complete); _mondayLow.Set(b, monday.Low, complete);
        }
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    { if (ChartInfo != null) FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, Status, Color.LightSlateGray); }
}
