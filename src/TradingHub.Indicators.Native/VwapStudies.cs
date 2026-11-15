// Independent causal implementation of KVP VWAP capabilities; reference
// gouqizi2214730-source/trading-hub @ 50220ba91613161685f84384825d855cb4dbeb76, MIT with Commons Clause.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

internal readonly record struct PeriodVwapState(long Key, WeightedStudyMoments Moments, StudyCoverage Coverage, WeightedStudyMoments Prior, StudyCoverage PriorCoverage)
{
    internal PeriodVwapState Add(long key, WeightedStudyMoments contribution, StudyCoverage coverage, bool first)
    {
        var state = first ? new PeriodVwapState(key, default, 0, default, 0) : key == Key ? this : new PeriodVwapState(key, default, 0, Moments, Coverage);
        var moments = state.Moments; moments.Merge(contribution);
        return state with { Moments = moments, Coverage = state.Coverage | coverage };
    }
}

[DisplayName("Multi-period VWAP")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Simultaneous session, week, calendar month, quarter and year VWAP. Measured compact rows stay labelled aggregated; optional OHLC estimates are explicit.")]
public class MultiPeriodVwap : Indicator
{
    private readonly OptionalValueDataSeries[] _lines = new OptionalValueDataSeries[5];
    // weighted ±σ bands per horizon (+first, -first, +second, -second), appended after the original series
    private readonly OptionalValueDataSeries[][] _bands = new OptionalValueDataSeries[5][];
    private readonly bool[] _bandsOn = new bool[5];
    private readonly List<PeriodVwapState>[] _states = new List<PeriodVwapState>[5];
    private StudyVwapSource _source; private bool _estimate, _secondBand; private decimal _first = 1, _second = 2;
    public MultiPeriodVwap() : base(true)
    {
        Color[] colors = { Color.Coral, Color.White, Color.DodgerBlue, Color.MediumSeaGreen, Color.Gold };
        for (int i = 0; i < 5; i++)
        {
            string name = StudyMath.Horizons[i].ToString();
            _states[i] = new(); _lines[i] = new($"MultiVwap.{name}", $"{name} VWAP") { Color = colors[i], Width = 2, ShowZeroValue = true };
            if (i == 0) DataSeries[0] = _lines[i]; else DataSeries.Add(_lines[i]);
        }
        for (int i = 0; i < 5; i++)
        {
            string name = StudyMath.Horizons[i].ToString();
            _bands[i] = new OptionalValueDataSeries[4];
            for (int j = 0; j < 4; j++)
            {
                string band = (j % 2 == 0 ? "Upper" : "Lower") + (j < 2 ? "1" : "2");
                _bands[i][j] = new($"MultiVwap.{name}.{band}", $"{name} VWAP {(j % 2 == 0 ? "+" : "-")}{(j < 2 ? "first" : "second")} band") { Color = Color.FromArgb(130, colors[i]), ShowZeroValue = true, LineDashStyle = LineDashStyle.Dash, VisualType = VisualMode.Hide };
                DataSeries.Add(_bands[i][j]);
            }
        }
        EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final);
    }
    private void ApplyBandVisibility()
    {
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 4; j++) _bands[i][j].VisualType = _bandsOn[i] && (j < 2 || _secondBand) ? VisualMode.Line : VisualMode.Hide;
    }
    private void SetBands(int horizon, bool on) { _bandsOn[horizon] = on; ApplyBandVisibility(); RedrawChart(); }
    [Display(Name = "Price source", GroupName = "Calculation")]
    public StudyVwapSource Source { get => _source; set { _source = value; RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC fallback", GroupName = "Calculation")]
    public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "Session bands", GroupName = "Bands")] public bool SessionBands { get => _bandsOn[0]; set => SetBands(0, value); }
    [Display(Name = "Week bands", GroupName = "Bands")] public bool WeekBands { get => _bandsOn[1]; set => SetBands(1, value); }
    [Display(Name = "Month bands", GroupName = "Bands")] public bool MonthBands { get => _bandsOn[2]; set => SetBands(2, value); }
    [Display(Name = "Quarter bands", GroupName = "Bands")] public bool QuarterBands { get => _bandsOn[3]; set => SetBands(3, value); }
    [Display(Name = "Year bands", GroupName = "Bands")] public bool YearBands { get => _bandsOn[4]; set => SetBands(4, value); }
    [Display(Name = "Show the second band", GroupName = "Bands")] public bool ShowSecondBand { get => _secondBand; set { _secondBand = value; ApplyBandVisibility(); RedrawChart(); } }
    [Display(Name = "First band (deviations)", GroupName = "Bands"), Range(0.1, 5)]
    public decimal FirstBandDeviations { get => _first; set { _first = Math.Clamp(value, 0.1m, 5); RecalculateValues(); } }
    [Display(Name = "Second band (deviations)", GroupName = "Bands"), Range(0.1, 10)]
    public decimal SecondBandDeviations { get => _second; set { _second = Math.Clamp(value, 0.1m, 10); RecalculateValues(); } }
    [Display(Name = "Price at the end of the line", GroupName = "Visualization")] public bool ShowPriceLabels { get; set; }
    [Browsable(false)] public StudyCoverage Coverage => _states[0].Count == 0 ? 0 : _states[0][^1].Coverage;
    [Browsable(false)] public string Status => FootprintStudyData.Label(Coverage);
    public StudyCoverage CoverageAt(StudyTimeFrame period, int bar)
    {
        int index = Array.IndexOf(StudyMath.Horizons, period);
        return index >= 0 && bar >= 0 && bar < _states[index].Count ? _states[index][bar].Coverage : StudyCoverage.Missing;
    }
    protected override void OnRecalculate() { foreach (var list in _states) list.Clear(); }
    protected override void OnCalculate(int bar, decimal value)
    {
        int target = bar;
        if (bar < _states[0].Count - 1) { OnRecalculate(); Clear(); }
        if (_states[0].Count == bar + 1) foreach (var list in _states) list.RemoveAt(bar);
        for (int b = _states[0].Count; b <= target; b++)
        {
            var c = GetCandle(b); var contribution = StudyMath.Vwap(c, Source, AllowEstimated, out var coverage);
            for (int k = 0; k < 5; k++)
            {
                var sourceCoverage = coverage | (b == 0 && !StudyMath.IsPeriodOpening(c, StudyMath.Horizons[k]) ? StudyCoverage.Partial : 0)
                    | (!StudyMath.IsContained(c, StudyMath.Horizons[k]) ? StudyCoverage.MissingBars : 0);
                var state = (b == 0 ? default : _states[k][b - 1]).Add(StudyMath.PeriodKey(c, StudyMath.Horizons[k]), contribution, sourceCoverage, b == 0);
                _states[k].Add(state); bool has = StudyMath.HasValue(state.Moments, state.Coverage);
                _lines[k].Set(b, state.Moments.Mean, has);
                decimal deviation = has ? state.Moments.Deviation : 0, mean = state.Moments.Mean;
                _bands[k][0].Set(b, mean + _first * deviation, has); _bands[k][1].Set(b, mean - _first * deviation, has);
                _bands[k][2].Set(b, mean + _second * deviation, has); _bands[k][3].Set(b, mean - _second * deviation, has);
                if (b > 0 && state.Key != _states[k][b - 1].Key)
                {
                    _lines[k].SetPointOfEndLine(b - 1);
                    foreach (var band in _bands[k]) band.SetPointOfEndLine(b - 1);
                }
            }
        }
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, $"Multi-period VWAP: {Status}", Color.LightSkyBlue);
        int bar = _states[0].Count - 1;
        if (!ShowPriceLabels || bar < 0) return;
        int x = ChartInfo.GetXByBar(bar, false) + 8; decimal tick = InstrumentInfo?.TickSize is > 0 and var t ? t : 0.01m;
        for (int k = 0; k < 5; k++)
        {
            var line = _lines[k];
            if (line.VisualType == VisualMode.Hide || line.IsHidden || !line.IsAvailable(bar)) continue;
            string text = TradingHub.Rendering.UiText.Tr(line.Name) + " " + FootprintStudyData.Nearest(line[bar], tick).ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.DrawString(text, FootprintStudyData.SmallFont, line.Color, x, ChartInfo.GetYByPrice(line[bar], false) - 6);
        }
    }
}

[DisplayName("Prior and closing VWAP levels")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Last completed period VWAP and weighted bands, plus a bounded history of closing levels. A close becomes available at the next observed period, never backfilled into a strategy series.")]
public class PriorClosingVwap : Indicator
{
    public readonly record struct ClosingLevel(int SourceEndBar, int AvailableBar, decimal Value, decimal Deviation, StudyCoverage Coverage);
    private readonly List<PeriodVwapState> _states = new();
    private readonly List<ClosingLevel> _closed = new();
    private readonly IReadOnlyList<ClosingLevel> _closedView;
    private static readonly RenderPen ClosingPen = new(Color.FromArgb(150, Color.Gold)), BandPen = new(Color.FromArgb(90, Color.Gold));
    private readonly OptionalValueDataSeries _prior = new("PriorVwap", "Prior VWAP") { Color = Color.Gold, VisualType = VisualMode.Steps, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _upper = new("PriorVwap.Upper", "Prior upper band") { Color = Color.DarkGoldenrod };
    private readonly OptionalValueDataSeries _lower = new("PriorVwap.Lower", "Prior lower band") { Color = Color.DarkGoldenrod };
    private StudyTimeFrame _period = StudyTimeFrame.Day; private int _keep = 5; private decimal _sigma = 1;
    private StudyVwapSource _source; private bool _estimate;
    public PriorClosingVwap() : base(true)
    { _closedView = _closed.AsReadOnly(); DataSeries[0] = _prior; DataSeries.Add(_upper); DataSeries.Add(_lower); EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final); }
    [Display(Name = "Period", GroupName = "Calculation")]
    public StudyTimeFrame Period { get => _period; set { _period = value; RecalculateValues(); } }
    [Display(Name = "Price source", GroupName = "Calculation")]
    public StudyVwapSource Source { get => _source; set { _source = value; RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC fallback", GroupName = "Calculation")]
    public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "Keep closing periods", GroupName = "Levels"), Range(1, 50)]
    public int Keep { get => _keep; set { _keep = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    [Display(Name = "Band deviations", GroupName = "Levels"), Range(0, 10)]
    public decimal Sigma { get => _sigma; set { _sigma = Math.Clamp(value, 0, 10); RecalculateValues(); } }
    [Display(Name = "Show closing-level history", GroupName = "Levels")] public bool ShowClosingLevels { get; set; } = true;
    [Browsable(false)] public IReadOnlyList<ClosingLevel> ClosingLevels => _closedView;
    [Browsable(false)] public StudyCoverage Coverage => _states.Count == 0 ? StudyCoverage.Missing : _states[^1].PriorCoverage;
    [Browsable(false)] public string Status => FootprintStudyData.Label(Coverage);
    protected override void OnRecalculate() { _states.Clear(); _closed.Clear(); }
    protected override void OnCalculate(int bar, decimal value)
    {
        int target = bar;
        if (bar < _states.Count - 1) { OnRecalculate(); Clear(); }
        if (_states.Count == bar + 1) { _states.RemoveAt(bar); _closed.RemoveAll(x => x.AvailableBar >= bar); }
        for (int b = _states.Count; b <= target; b++)
        {
            var c = GetCandle(b); var current = StudyMath.Vwap(c, Source, AllowEstimated, out var coverage);
            if (!StudyMath.IsContained(c, Period)) coverage |= StudyCoverage.MissingBars;
            if (b == 0 && !StudyMath.IsPeriodOpening(c, Period)) coverage |= StudyCoverage.Partial;
            var previous = b == 0 ? default : _states[b - 1]; long key = StudyMath.PeriodKey(c, Period);
            if (b > 0 && key != previous.Key && StudyMath.HasValue(previous.Moments, previous.Coverage))
            {
                _closed.Add(new(b - 1, b, previous.Moments.Mean, previous.Moments.Deviation, previous.Coverage));
                if (_closed.Count > Keep + 1) _closed.RemoveAt(0);
            }
            var state = previous.Add(key, current, coverage, b == 0); _states.Add(state);
            bool available = StudyMath.HasValue(state.Prior, state.PriorCoverage); decimal width = state.Prior.Deviation * Sigma;
            _prior.Set(b, state.Prior.Mean, available); _upper.Set(b, state.Prior.Mean + width, available && Sigma > 0); _lower.Set(b, state.Prior.Mean - width, available && Sigma > 0);
        }
    }
    /// <summary>False when a composite (the VWAP suite) draws one combined fidelity legend for all its parts.</summary>
    internal bool DrawLegend { get; set; } = true;
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        if (DrawLegend) FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, $"Prior VWAP: {Status}", Color.Goldenrod);
        if (!ShowClosingLevels) return;
        for (int i = Math.Max(0, _closed.Count - Keep); i < _closed.Count; i++)
        {
            var level = _closed[i]; if (level.AvailableBar > LastVisibleBarNumber) continue;
            int x = ChartInfo.GetXByBar(Math.Max(FirstVisibleBarNumber, level.AvailableBar));
            int end = ChartInfo.GetXByBar(LastVisibleBarNumber), y = ChartInfo.GetYByPrice(level.Value);
            context.DrawLine(ClosingPen, x, y, end, y);
            context.DrawString($"Closing VWAP: {FootprintStudyData.Label(level.Coverage)}", FootprintStudyData.SmallFont, Color.Goldenrod, x + 2, y - 10);
            if (Sigma > 0) for (int sign = -1; sign <= 1; sign += 2) { int band = ChartInfo.GetYByPrice(level.Value + sign * Sigma * level.Deviation); context.DrawLine(BandPen, x, band, end, band); }
        }
    }
}

/// <summary>Two-stack aggregate queue. Removing old rows never subtracts nearly equal moments.</summary>
internal sealed class StudyMomentQueue
{
    internal readonly record struct Value(DateTime Time, WeightedStudyMoments Moments, StudyCoverage Coverage);
    private readonly record struct Entry(Value Value, WeightedStudyMoments Total, StudyCoverage Coverage);
    private readonly List<Entry> _incoming = new(), _outgoing = new();
    internal int Count => _incoming.Count + _outgoing.Count;
    internal void Clear() { _incoming.Clear(); _outgoing.Clear(); }
    private static void Push(List<Entry> stack, Value value)
    {
        var total = stack.Count == 0 ? default : stack[^1].Total; total.Merge(value.Moments);
        stack.Add(new(value, total, value.Coverage | (stack.Count == 0 ? 0 : stack[^1].Coverage)));
    }
    internal void Add(Value value) => Push(_incoming, value);
    private void Transfer() { if (_outgoing.Count > 0) return; for (int i = _incoming.Count - 1; i >= 0; i--) Push(_outgoing, _incoming[i].Value); _incoming.Clear(); }
    internal void Expire(DateTime start) { while (Count > 0) { Transfer(); if (_outgoing[^1].Value.Time >= start) break; _outgoing.RemoveAt(_outgoing.Count - 1); } }
    internal WeightedStudyMoments Total { get { var total = _outgoing.Count == 0 ? default : _outgoing[^1].Total; if (_incoming.Count > 0) total.Merge(_incoming[^1].Total); return total; } }
    internal StudyCoverage Coverage => (_outgoing.Count == 0 ? 0 : _outgoing[^1].Coverage) | (_incoming.Count == 0 ? 0 : _incoming[^1].Coverage);
}

[DisplayName("Rolling VWAP")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("VWAP over whole opening-time source bars, queried at the current bar opening. Calendar windows preserve exchange-session date and local clock, never 30-day months. DST gaps move forward; folds prefer the source offset, otherwise later UTC. No intrabar edge split.")]
public class RollingVwap : Indicator
{
    public enum WindowKind
    {
        [Display(Name = "Elapsed minutes")] Minutes,
        [Display(Name = "Exchange-session calendar days")] CalendarDays,
        [Display(Name = "Exchange-session calendar months")] CalendarMonths,
        [Display(Name = "Exchange-session calendar years")] CalendarYears
    }
    private readonly StudyMomentQueue _queue = new(); private int _last = -1, _length = 60, _min = 1;
    private StudyMomentQueue.Value _live;
    private WindowKind _window; private StudyVwapSource _source; private bool _estimate, _partialHistory; private decimal _sigma = 1;
    private readonly OptionalValueDataSeries _vwap = new("RollingVwap", "Rolling VWAP") { Color = Color.Orange, Width = 2, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _upper = new("RollingVwap.Upper", "Upper band") { Color = Color.DarkOrange };
    private readonly OptionalValueDataSeries _lower = new("RollingVwap.Lower", "Lower band") { Color = Color.DarkOrange };
    public RollingVwap() : base(true) { DataSeries[0] = _vwap; DataSeries.Add(_upper); DataSeries.Add(_lower); EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final); }
    [Display(Name = "Window unit", GroupName = "Calculation")] public WindowKind Window { get => _window; set { _window = value; RecalculateValues(); } }
    [Display(Name = "Window length", GroupName = "Calculation"), Range(1, 10000)] public int Length { get => _length; set { _length = Math.Clamp(value, 1, 10000); RecalculateValues(); } }
    [Display(Name = "Minimum source bars", GroupName = "Calculation"), Range(1, 1000)] public int MinimumBars { get => _min; set { _min = Math.Clamp(value, 1, 1000); RecalculateValues(); } }
    [Display(Name = "Price source", GroupName = "Calculation")] public StudyVwapSource Source { get => _source; set { _source = value; RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC fallback", GroupName = "Calculation")] public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "Band deviations", GroupName = "Bands"), Range(0, 10)] public decimal Sigma { get => _sigma; set { _sigma = Math.Clamp(value, 0, 10); RecalculateValues(); } }
    [Browsable(false)] public StudyCoverage Coverage => _queue.Coverage | _live.Coverage | (_partialHistory ? StudyCoverage.Partial : 0);
    [Browsable(false)] public string Status => $"{FootprintStudyData.Label(Coverage)}; opening-time source-bar boundary resolution";
    protected override void OnRecalculate() { _last = -1; _queue.Clear(); _live = default; _partialHistory = false; }
    private DateTime Start(IndicatorCandle candle) => Window switch
    {
        WindowKind.CalendarDays => StudyMath.ShiftSessionCalendar(candle, StudyTimeFrame.Day, -Length),
        WindowKind.CalendarMonths => StudyMath.ShiftSessionCalendar(candle, StudyTimeFrame.Month, -Length),
        WindowKind.CalendarYears => StudyMath.ShiftSessionCalendar(candle, StudyTimeFrame.Year, -Length),
        _ => candle.Time.AddMinutes(-Length)
    };
    protected override void OnCalculate(int bar, decimal value)
    {
        int target = bar;
        if (bar < _last) { OnRecalculate(); Clear(); }
        int from = bar == _last ? bar : _last + 1;
        for (int b = from; b <= target; b++)
        {
            if (b > _last && _last >= 0) _queue.Add(_live);
            var c = GetCandle(b); var contribution = StudyMath.Vwap(c, Source, AllowEstimated, out var coverage);
            var start = Start(c); _partialHistory = GetCandle(0).Time > start;
            _live = new(c.Time, contribution, coverage); _queue.Expire(start);
            var total = _queue.Total; total.Merge(contribution); bool available = _queue.Count + 1 >= MinimumBars && StudyMath.HasValue(total, _queue.Coverage | coverage);
            _vwap.Set(b, total.Mean, available); decimal width = total.Deviation * Sigma;
            _upper.Set(b, total.Mean + width, available && Sigma > 0); _lower.Set(b, total.Mean - width, available && Sigma > 0); _last = b;
        }
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    { if (ChartInfo != null) FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, $"Rolling VWAP: {Status}", Color.Orange); }
}

[DisplayName("Session-window VWAP")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("VWAP within a chosen local-time session, including overnight windows and DST. Only bars whose opening time is inside contribute; a bar spanning a boundary is not silently split.")]
public class TimedSessionVwap : Indicator
{
    /// <summary>A world-session preset; <see cref="Custom"/> uses the time zone and local times set below.</summary>
    public enum SessionPreset
    {
        [Display(Name = "Custom")] Custom,
        [Display(Name = "Sydney")] Sydney,
        [Display(Name = "Tokyo")] Tokyo,
        [Display(Name = "London")] London,
        [Display(Name = "New York")] NewYork,
    }
    private readonly List<(DateOnly Date, WeightedStudyMoments Moments, StudyCoverage Coverage)> _states = new();
    private SessionPreset _preset = SessionPreset.Custom;
    private readonly OptionalValueDataSeries _line = new("TimedSessionVwap", "Session VWAP") { Color = Color.Turquoise, ShowZeroValue = true, Width = 2 };
    private string _zone = "America/New_York"; private TimeSpan _start = TimeSpan.FromHours(9.5), _end = TimeSpan.FromHours(16);
    private StudyVwapSource _source; private bool _estimate;
    private TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    public TimedSessionVwap() : base(true) { DataSeries[0] = _line; EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final); }
    // presets never overwrite the custom fields, so switching back to Custom restores them in any load order
    [Display(Name = "Session preset", GroupName = "Session")] public SessionPreset Preset { get => _preset; set { _preset = value; RecalculateValues(); } }
    [Display(Name = "Time zone", GroupName = "Session")] public string TimeZoneId { get => _zone; set { _timeZone = TimeZoneInfo.FindSystemTimeZoneById(value); _zone = value; RecalculateValues(); } }
    [Display(Name = "Start local time", GroupName = "Session")] public TimeSpan StartLocal { get => _start; set { if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(value)); _start = value; RecalculateValues(); } }
    [Display(Name = "End local time", GroupName = "Session")] public TimeSpan EndLocal { get => _end; set { if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(value)); _end = value; RecalculateValues(); } }
    [Display(Name = "Price source", GroupName = "Calculation")] public StudyVwapSource Source { get => _source; set { _source = value; RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC fallback", GroupName = "Calculation")] public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Browsable(false)] public StudyCoverage Coverage => _states.Count == 0 ? StudyCoverage.Missing : _states[^1].Coverage;
    [Browsable(false)] public string Status => $"{FootprintStudyData.Label(Coverage)}; source-bar opening-time membership; no intrabar session split";
    protected override void OnRecalculate() => _states.Clear();
    protected override void OnCalculate(int bar, decimal value)
    {
        int target = bar; if (bar < _states.Count - 1) { _states.Clear(); Clear(); }
        if (bar == _states.Count - 1) _states.RemoveAt(bar);
        var (zone, startLocal, endLocal) = Window();
        for (int b = _states.Count; b <= target; b++)
        {
            var c = GetCandle(b); var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(c.Time, DateTimeKind.Utc), zone);
            var date = DateOnly.FromDateTime(local); var time = local.TimeOfDay;
            bool inside = startLocal == endLocal || (startLocal < endLocal ? time >= startLocal && time < endLocal : time >= startLocal || time < endLocal);
            if (startLocal >= endLocal && time < endLocal) date = date.AddDays(-1);
            var state = b > 0 && _states[b - 1].Date == date ? _states[b - 1] : (date, new WeightedStudyMoments(), StudyCoverage.Missing);
            if (inside)
            {
                var contribution = StudyMath.Vwap(c, Source, AllowEstimated, out var coverage);
                if (b == 0 && time != startLocal) coverage |= StudyCoverage.Partial;
                state.Item2.Merge(contribution); state.Item3 |= coverage;
            }
            _states.Add(state); _line.Set(b, state.Item2.Mean, inside && StudyMath.HasValue(state.Item2, state.Item3));
            if (!inside && b > 0) _line.SetPointOfEndLine(b - 1);
        }
    }
    /// <summary>The effective zone and local window: the preset's when one is selected, otherwise the custom settings.</summary>
    internal (TimeZoneInfo Zone, TimeSpan Start, TimeSpan End) Window()
    {
        if (_preset == SessionPreset.Custom) return (_timeZone, _start, _end);
        var window = LocalSessionWindow.Of(_preset switch
        {
            SessionPreset.Sydney => WorldSession.Sydney, SessionPreset.Tokyo => WorldSession.Tokyo,
            SessionPreset.London => WorldSession.London, _ => WorldSession.NewYork,
        });
        return (window.Zone, window.Start, window.End);
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    { if (ChartInfo != null) FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, Status, Color.Turquoise); }
}
