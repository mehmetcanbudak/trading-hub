// Independent closed-bar session studies; capability reference gouqizi2214730-source/trading-hub @ 9e6b790
// (MIT with Commons Clause). Not a port of its code.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>Smallest positive spacing between consecutive processed bars (the chart's bar length once two adjacent bars exist).</summary>
internal struct SessionBarStep
{
    private DateTime _last; private TimeSpan _min;
    public readonly TimeSpan Value => _min;
    public void Observe(DateTime time)
    {
        if (_last != default && time > _last && (_min == TimeSpan.Zero || time - _last < _min)) _min = time - _last;
        _last = time;
    }
}

/// <summary>A completed window session drawn from <see cref="From"/> to <see cref="Last"/>; its high/low are final at the close of <see cref="Available"/>.</summary>
public readonly record struct CompletedWindowSession(DateOnly Date, int From, int Last, int Available, decimal High, decimal Low);

/// <summary>
/// One <see cref="LocalSessionWindow"/> followed over closed bars. A session is complete at the close of its last bar when that
/// bar reaches the scheduled end, otherwise at the close of the first later bar outside it (or of another trading date).
/// </summary>
internal sealed class WindowSessionRun(LocalSessionWindow window)
{
    private DateOnly? _done;
    public bool Forming { get; private set; }
    public DateOnly Date { get; private set; }
    public int From { get; private set; }
    public int Last { get; private set; }
    public decimal High { get; private set; }
    public decimal Low { get; private set; }

    /// <summary>Adds closed bar <paramref name="bar"/>; returns whether it belongs to the session (forming or just completed).</summary>
    public bool Add(StudyBar b, int bar, TimeSpan step, out bool started, out CompletedWindowSession? before, out CompletedWindowSession? here)
    {
        started = false; before = here = null;
        bool inside = window.Contains(b.Time, out var date);
        if (Forming && (!inside || date != Date)) before = Finish(bar);
        if (!inside || date == _done) return false;
        if (!Forming) { Forming = true; started = true; Date = date; From = bar; High = b.High; Low = b.Low; }
        else { High = Math.Max(High, b.High); Low = Math.Min(Low, b.Low); }
        Last = bar;
        if (step > TimeSpan.Zero && b.Time + step >= window.EndUtc(date)) here = Finish(bar);
        return true;
    }

    /// <summary>The bar the forming session is scheduled to end on, counted in bars of <paramref name="step"/>.</summary>
    public int ScheduledLast(StudyBar b, int bar, TimeSpan step)
    {
        if (!Forming || step <= TimeSpan.Zero) return Last;
        long remaining = (window.EndUtc(Date) - b.Time).Ticks;
        return bar + (int)Math.Clamp((remaining + step.Ticks - 1) / step.Ticks - 1, 0, 100_000);
    }

    /// <summary>A bar without causal coverage: the forming session (and the one this bar belongs to) cannot complete truthfully.</summary>
    public void Abandon(DateTime time)
    {
        if (Forming) { _done = Date; Forming = false; }
        if (window.Contains(time, out var date)) _done = date;
    }

    private CompletedWindowSession Finish(int available)
    {
        Forming = false; _done = Date;
        return new(Date, From, Last, available, High, Low);
    }

    internal static string Name(WorldSession session) => session switch
    {
        WorldSession.Sydney => "Sydney",
        WorldSession.Tokyo => "Tokyo",
        WorldSession.London => "London",
        WorldSession.NewYork => "New York",
        _ => "Asia",
    };
}

[DisplayName("Market sessions (Sydney, Tokyo, London, New York)")]
[Category(IndicatorCategories.Sessions)]
[Description("Up to four world sessions in their own time zones (daylight saving followed automatically), boxed around their high and low with the session name. Optional midline, per-session VWAP with ±1σ bands (measured volume at price; a bar estimate only when enabled), overlap shading and the forming session extended to its scheduled end. A session's box, midline and alertable high/low levels exist only from the close of its last bar (or of the first bar after it); while it forms, its outline grows from closed bars only. Weekends and holidays without bars form no session.")]
public class MarketSessions : ClosedBarStudy
{
    /// <summary>The session forming at the last processed close.</summary>
    public readonly record struct FormingSession(WorldSession Session, int From, int Last, int ScheduledLast, decimal High, decimal Low);

    private sealed class Slot(WorldSession session, bool show, Color color)
    {
        public bool Show = show, Vwap;
        public WorldSession Session = session;
        public Color Color = color;
        public WindowSessionRun? Run;
        public WeightedStudyMoments Moments;
        public StudyCoverage Coverage;
        public List<(int Bar, decimal Price)>? Mid, Up, Down;
        public StudyLine? High, Low;
        public int Scheduled;
        public CompletedWindowSession? Previous;
    }

    private readonly Slot[] _slots =
    {
        new(WorldSession.Tokyo, true, StudyPalette.Bear),
        new(WorldSession.London, true, StudyPalette.Neutral),
        new(WorldSession.NewYork, true, StudyPalette.Bull),
        new(WorldSession.Sydney, false, StudyPalette.Amber),
    };
    private readonly OptionalValueDataSeries _signal;
    private SessionBarStep _step;
    private int _fillOpacity = 12, _vwapWidth = 2, _borderOpacity = 100, _daysKept = 10;
    private bool _names = true, _midline, _levels = true, _overlaps, _bands, _estimate;
    // presentation only: the session date of each drawn shape and the latest session date, for "Days kept"
    private readonly Dictionary<object, DateOnly> _dates = new(ReferenceEqualityComparer.Instance);
    private DateOnly _latest;

    public MarketSessions() : this("MarketSessions.Completed", "Sessions completed at this close (bit mask of slots 1-4)") { }

    protected MarketSessions(string seriesId, string seriesName)
    {
        _signal = new(seriesId, seriesName) { VisualType = VisualMode.Hide, ShowZeroValue = true };
        DataSeries[0] = _signal;
        ShowDashboard = false;
    }

    [Display(Name = "Show", GroupName = "Session 1", Order = 10)] public bool S1Show { get => _slots[0].Show; set { _slots[0].Show = value; RecalculateValues(); } }
    [Display(Name = "Session", GroupName = "Session 1", Order = 11)] public WorldSession S1Session { get => _slots[0].Session; set { _slots[0].Session = value; RecalculateValues(); } }
    [Display(Name = "VWAP", GroupName = "Session 1", Order = 12)] public bool S1Vwap { get => _slots[0].Vwap; set { _slots[0].Vwap = value; RecalculateValues(); } }
    [Display(Name = "Color", GroupName = "Session 1", Order = 13)] public Color S1Color { get => _slots[0].Color; set => _slots[0].Color = value; }
    [Display(Name = "Show", GroupName = "Session 2", Order = 20)] public bool S2Show { get => _slots[1].Show; set { _slots[1].Show = value; RecalculateValues(); } }
    [Display(Name = "Session", GroupName = "Session 2", Order = 21)] public WorldSession S2Session { get => _slots[1].Session; set { _slots[1].Session = value; RecalculateValues(); } }
    [Display(Name = "VWAP", GroupName = "Session 2", Order = 22)] public bool S2Vwap { get => _slots[1].Vwap; set { _slots[1].Vwap = value; RecalculateValues(); } }
    [Display(Name = "Color", GroupName = "Session 2", Order = 23)] public Color S2Color { get => _slots[1].Color; set => _slots[1].Color = value; }
    [Display(Name = "Show", GroupName = "Session 3", Order = 30)] public bool S3Show { get => _slots[2].Show; set { _slots[2].Show = value; RecalculateValues(); } }
    [Display(Name = "Session", GroupName = "Session 3", Order = 31)] public WorldSession S3Session { get => _slots[2].Session; set { _slots[2].Session = value; RecalculateValues(); } }
    [Display(Name = "VWAP", GroupName = "Session 3", Order = 32)] public bool S3Vwap { get => _slots[2].Vwap; set { _slots[2].Vwap = value; RecalculateValues(); } }
    [Display(Name = "Color", GroupName = "Session 3", Order = 33)] public Color S3Color { get => _slots[2].Color; set => _slots[2].Color = value; }
    [Display(Name = "Show", GroupName = "Session 4", Order = 40)] public bool S4Show { get => _slots[3].Show; set { _slots[3].Show = value; RecalculateValues(); } }
    [Display(Name = "Session", GroupName = "Session 4", Order = 41)] public WorldSession S4Session { get => _slots[3].Session; set { _slots[3].Session = value; RecalculateValues(); } }
    [Display(Name = "VWAP", GroupName = "Session 4", Order = 42)] public bool S4Vwap { get => _slots[3].Vwap; set { _slots[3].Vwap = value; RecalculateValues(); } }
    [Display(Name = "Color", GroupName = "Session 4", Order = 43)] public Color S4Color { get => _slots[3].Color; set => _slots[3].Color = value; }

    [Display(Name = "Fill opacity (%)", GroupName = "Drawing", Order = 50), Range(0, 100)]
    public int FillOpacity { get => _fillOpacity; set { _fillOpacity = Math.Clamp(value, 0, 100); RecalculateValues(); } }
    [Display(Name = "Session names", GroupName = "Drawing", Order = 51)]
    public bool ShowNames { get => _names; set { _names = value; RecalculateValues(); } }
    [Display(Name = "Midline", GroupName = "Drawing", Order = 52)]
    public bool Midline { get => _midline; set { _midline = value; RecalculateValues(); } }
    [Display(Name = "Alertable high/low of the last completed session", GroupName = "Drawing", Order = 53)]
    public bool LiveLevels { get => _levels; set { _levels = value; RecalculateValues(); } }
    [Display(Name = "Shade session overlaps", GroupName = "Drawing", Order = 54)]
    public bool HighlightOverlaps { get => _overlaps; set { _overlaps = value; RecalculateValues(); } }
    [Display(Name = "Overlap", GroupName = "Drawing", Order = 55)] public Color OverlapColor { get; set; } = StudyPalette.Muted;
    [Display(Name = "Show the forming session", GroupName = "Drawing", Order = 56)] public bool ShowLive { get; set; } = true;
    [Display(Name = "Extend the forming session to its scheduled end", GroupName = "Drawing", Order = 57)] public bool ExtendLive { get; set; } = true;
    [Display(Name = "Border opacity (%)", GroupName = "Drawing", Order = 58, Description = "Display only: changes how session outlines are drawn; never changes detection, strategy series or alerts."), Range(0, 100)]
    public int BorderOpacity { get => _borderOpacity; set { _borderOpacity = Math.Clamp(value, 0, 100); RedrawChart(); } }
    [Display(Name = "Days kept", GroupName = "Drawing", Order = 59, Description = "Display only: hides drawings of sessions dated this many days or more before the latest session; never changes detection, strategy series or alerts."), Range(1, 60)]
    public int DaysKept { get => _daysKept; set { _daysKept = Math.Clamp(value, 1, 60); RedrawChart(); } }
    [Display(Name = "±1σ VWAP bands", GroupName = "VWAP", Order = 60)]
    public bool VwapBands { get => _bands; set { _bands = value; RecalculateValues(); } }
    [Display(Name = "VWAP width", GroupName = "VWAP", Order = 61), Range(1, 6)]
    public int VwapWidth { get => _vwapWidth; set { _vwapWidth = Math.Clamp(value, 1, 6); RecalculateValues(); } }
    [Display(Name = "Estimate VWAP from the bar (HLC3) when volume at price is missing", GroupName = "VWAP", Order = 62)]
    public bool EstimateVwap { get => _estimate; set { _estimate = value; RecalculateValues(); } }

    /// <summary>Sessions forming at the last processed close (not yet final).</summary>
    [Browsable(false)]
    public IReadOnlyList<FormingSession> Forming => _slots.Where(s => s.Run is { Forming: true })
        .Select(s => new FormingSession(s.Session, s.Run!.From, s.Run.Last, s.Scheduled, s.Run.High, s.Run.Low)).ToList();

    protected override void ResetState()
    {
        _step = default; _dates.Clear(); _latest = default;
        foreach (var s in _slots)
        {
            s.Run = null; s.Moments = default; s.Coverage = StudyCoverage.Missing; s.Mid = s.Up = s.Down = null;
            s.High = s.Low = null; s.Scheduled = 0; s.Previous = null;
        }
    }

    protected override void OnCoverageGap(int bar)
    {
        foreach (var s in _slots) { s.Run?.Abandon(Bars[bar].Time); s.Mid = s.Up = s.Down = null; }
    }

    private bool Active(int k)
    {
        if (!_slots[k].Show) return false;
        for (int j = 0; j < k; j++) if (_slots[j].Show && _slots[j].Session == _slots[k].Session) return false; // a repeated session is drawn once
        return true;
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; _step.Observe(b.Time);
        int completed = 0, inside = 0;
        for (int k = 0; k < _slots.Length; k++)
        {
            if (!Active(k)) continue;
            var s = _slots[k];
            s.Run ??= new WindowSessionRun(LocalSessionWindow.Of(s.Session));
            bool member = s.Run.Add(b, bar, _step.Value, out bool started, out var before, out var here);
            if (before is { } ended) { Complete(s, ended); completed |= 1 << k; }
            if (!member) continue;
            if (s.Run.Date > _latest) _latest = s.Run.Date;
            inside++;
            if (started) StartVwap(s, bar);
            AddVwap(s, bar);
            s.Scheduled = s.Run.ScheduledLast(b, bar, _step.Value);
            if (here is { } closed) { Complete(s, closed); completed |= 1 << k; }
        }
        if (HighlightOverlaps && inside >= 2) Scene.Tints[bar] = StudyPalette.A(OverlapColor, 36);
        _signal.Set(bar, completed);
        Dashboard();
    }

    private T Dated<T>(T shape, DateOnly date) where T : class { _dates[shape] = date; return shape; }

    private void Complete(Slot s, CompletedWindowSession c)
    {
        string name = WindowSessionRun.Name(s.Session);
        // ended boxes render at half the fill alpha, so the configured opacity is doubled here
        var box = Dated(Scene.Box(c.From, c.Available, c.High, c.Low, s.Color, ShowNames ? name : null, fillAlpha: Math.Min(255, FillOpacity * 255 / 50)), c.Date);
        box.To = c.Last; box.EndAvailable = c.Available;
        decimal mid = (c.High + c.Low) / 2;
        if (Midline) Dated(Scene.Segment(c.From, c.Last, c.Available, mid, mid, StudyPalette.A(s.Color, 180), 1, LineDashStyle.Dash), c.Date);
        s.High?.End(c.Available); s.Low?.End(c.Available); s.High = s.Low = null;
        if (LiveLevels)
        {
            s.High = Dated(Scene.Level(c.From, c.Available, c.High, s.Color, name + " high", name + " high", 1, LineDashStyle.Dash), c.Date);
            s.Low = Dated(Scene.Level(c.From, c.Available, c.Low, s.Color, name + " low", name + " low", 1, LineDashStyle.Dash), c.Date);
        }
        s.Previous = c; s.Mid = s.Up = s.Down = null;
    }

    private void StartVwap(Slot s, int bar)
    {
        s.Mid = s.Up = s.Down = null;
        if (!s.Vwap) return;
        s.Moments = default; s.Coverage = StudyCoverage.Missing; s.Mid = new();
        Scene.Paths.Add(Dated(new StudyPath(bar, s.Mid, s.Color, VwapWidth), s.Run!.Date));
        if (!VwapBands) return;
        s.Up = new(); s.Down = new();
        Scene.Paths.Add(Dated(new StudyPath(bar, s.Up, StudyPalette.A(s.Color, 140), 1, LineDashStyle.Dash), s.Run.Date));
        Scene.Paths.Add(Dated(new StudyPath(bar, s.Down, StudyPalette.A(s.Color, 140), 1, LineDashStyle.Dash), s.Run.Date));
    }

    private void AddVwap(Slot s, int bar)
    {
        if (s.Mid is not { } path) return;
        var moments = StudyMath.Vwap(Candle(bar), StudyVwapSource.MeasuredPriceLevels, EstimateVwap, out var coverage);
        s.Moments.Merge(moments); s.Coverage |= coverage;
        if ((s.Coverage & StudyCoverage.MissingBars) != 0) { s.Mid = s.Up = s.Down = null; return; } // unknown from here on
        if (!StudyMath.HasValue(s.Moments, s.Coverage)) return;
        decimal mean = s.Moments.Mean, sd = s.Moments.Deviation;
        path.Add((bar, mean)); s.Up?.Add((bar, mean + sd)); s.Down?.Add((bar, mean - sd));
    }

    private void Dashboard()
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Market sessions";
        for (int k = 0; k < _slots.Length; k++)
        {
            if (!Active(k)) continue;
            var s = _slots[k]; string name = WindowSessionRun.Name(s.Session);
            if (s.Run is { Forming: true } run) Scene.Row(name, $"{Price(run.Low)} – {Price(run.High)}", s.Color);
            else if (s.Previous is { } p) Scene.Row(name, $"{Price(p.Low)} – {Price(p.High)}", s.Color);
        }
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (!ShowLive || ChartInfo == null) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber, bw = Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth);
        var font = new RenderFont("Arial", 9);
        foreach (var s in _slots)
        {
            if (s.Run is not { Forming: true } run) continue;
            int to = ExtendLive ? Math.Max(run.Last, s.Scheduled) : run.Last;
            if (run.From > last + 1 || to < first) continue;
            int x0 = ChartInfo.GetXByBar(run.From), x1 = Math.Min(region.Right, ChartInfo.GetXByBar(to, false) + bw / 2);
            int y0 = ChartInfo.GetYByPrice(run.High, false), y1 = ChartInfo.GetYByPrice(run.Low, false);
            var rect = new Rectangle(x0, Math.Min(y0, y1), Math.Max(2, x1 - x0), Math.Max(1, Math.Abs(y1 - y0)));
            ctx.FillRectangle(StudyPalette.A(s.Color, FillOpacity * 255 / 100), rect);
            ctx.DrawRectangle(Pen(StudyPalette.A(s.Color, 200 * BorderOpacity / 100), 1, LineDashStyle.Dash), rect);
            if (Midline)
            {
                int ym = ChartInfo.GetYByPrice((run.High + run.Low) / 2, false);
                ctx.DrawLine(Pen(StudyPalette.A(s.Color, 180), 1, LineDashStyle.Dash), rect.Left, ym, rect.Right, ym);
            }
            if (ShowNames) ctx.DrawString(UiText.Tr(WindowSessionRun.Name(s.Session)), font, s.Color, rect.Left + 3, rect.Top + 2);
        }
    }

    /// <summary>Display only: a session drawn more than <see cref="DaysKept"/> days before the latest session is hidden; live (alertable) levels always show.</summary>
    private bool Kept(StudyShape shape) => shape.Live || Kept((object)shape);
    private bool Kept(object shape) => !_dates.TryGetValue(shape, out var date) || _latest.DayNumber - date.DayNumber < DaysKept;

    public override bool IsShown(StudyBox box) => Kept(box);
    public override bool IsShown(StudyLine line) => Kept(line);
    public override bool IsShown(StudyPath path) => Kept(path);

    /// <summary>Display only: <see cref="BorderOpacity"/> scales the outline of every session box.</summary>
    protected override int BoxBorderAlpha(StudyBox box) => base.BoxBorderAlpha(box) * BorderOpacity / 100;
}

[DisplayName("Session map (Asia, London, New York)")]
[Category(IndicatorCategories.Sessions)]
[Description("Asia (19:00–04:00 New York time), London and New York boxed around their high and low with a name on each and their overlaps shaded, each in its own time zone with daylight saving followed. Shows which session built the range, where they overlapped and how far the current one has gone; the forming session grows from closed bars only and a box is final from the close of the session's last bar.")]
public class SessionMap : MarketSessions
{
    public SessionMap() : base("SessionMap.Completed", "Sessions completed at this close (bit mask of slots 1-4)")
    {
        S1Session = WorldSession.Asia; S1Color = StudyPalette.Amber;
        S2Session = WorldSession.London; S2Color = StudyPalette.Neutral;
        S3Session = WorldSession.NewYork; S3Color = StudyPalette.Bull;
        S4Show = false;
        HighlightOverlaps = true; ExtendLive = false; DaysKept = 5;
    }
}

[DisplayName("Asian session sweeps")]
[Category(IndicatorCategories.Sessions)]
[Description("The Asian range (19:00–04:00 New York time, daylight saving followed) becomes final at the close of its last bar. Outside it, the first sweep of each side is tagged; an internal change of character (CHoCH) against the sweep within the allowed bars gives a hypothetical entry at that close, with an optional EMA trend filter, the stop at the sweep extreme and the target in multiples of the risk. Sweeps, entries and exits are known at the close of the bar that shows them. Heuristic signals, not orders.")]
public class AsianSessionSweeps : ClosedBarStudy
{
    private int _internal = 2, _maxAfter = 10, _buffer, _trendLength = 200;
    private decimal _stopMultiple = 1, _rewardMultiple = 2;
    private bool _closeAtNew = true, _wick, _trendFilter = true;
    private readonly OptionalValueDataSeries _signal = new("AsianSessionSweeps.Signal", "Hypothetical entry (+1 long, -1 short)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _position = new("AsianSessionSweeps.Position", "Hypothetical position after this close (+1 long, -1 short, 0 flat)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _sweep = new("AsianSessionSweeps.Sweep", "Asian range sweep (+1 low swept, -1 high swept)") { VisualType = VisualMode.Hide, ShowZeroValue = true };

    private WindowSessionRun _run = new(LocalSessionWindow.Of(WorldSession.Asia));
    private SessionBarStep _step;
    private RunningEma _ema = new(200);
    private bool _rangeReady, _hiSwept, _loSwept, _ihCrossed = true, _ilCrossed = true;
    private decimal _rh, _rl, _sweepExt, _ih, _il, _entry, _stop, _target;
    private int _sweepDir, _sweepBar, _ihBar, _ilBar, _trend, _pos, _entryBar, _setups, _wins, _losses;
    private StudyLine? _hiLine, _loLine, _stopLine, _targetLine;
    // presentation only: the ordinal of the Asian session each range box/level belongs to, for "Sessions drawn"
    private readonly Dictionary<StudyShape, int> _sessionOf = new(ReferenceEqualityComparer.Instance);
    private int _sessions, _maxSessions = 20;

    public AsianSessionSweeps()
    {
        DataSeries[0] = _signal; DataSeries.Add(_position); DataSeries.Add(_sweep);
    }

    [Display(Name = "Internal structure length", GroupName = "Signals", Order = 10), Range(1, 20)]
    public int InternalLength { get => _internal; set { _internal = Math.Clamp(value, 1, 20); RecalculateValues(); } }
    [Display(Name = "Break by wick (otherwise by close)", GroupName = "Signals", Order = 11)]
    public bool WickBreak { get => _wick; set { _wick = value; RecalculateValues(); } }
    [Display(Name = "Sweep tags", GroupName = "Signals", Order = 12)] public bool ShowSweeps { get; set; } = true;
    [Display(Name = "Internal structure", GroupName = "Signals", Order = 13)] public bool ShowStructure { get; set; } = true;
    [Display(Name = "Close when a new session starts", GroupName = "Trade", Order = 20)]
    public bool CloseAtNewSession { get => _closeAtNew; set { _closeAtNew = value; RecalculateValues(); } }
    [Display(Name = "Stop (× risk)", GroupName = "Trade", Order = 21), Range(0.1, 10)]
    public decimal StopMultiple { get => _stopMultiple; set { _stopMultiple = Math.Clamp(value, 0.1m, 10); RecalculateValues(); } }
    [Display(Name = "Target (× risk)", GroupName = "Trade", Order = 22), Range(0.1, 20)]
    public decimal RewardMultiple { get => _rewardMultiple; set { _rewardMultiple = Math.Clamp(value, 0.1m, 20); RecalculateValues(); } }
    [Display(Name = "Bars allowed after the sweep", GroupName = "Trade", Order = 23), Range(1, 200)]
    public int MaxBarsAfter { get => _maxAfter; set { _maxAfter = Math.Clamp(value, 1, 200); RecalculateValues(); } }
    [Display(Name = "Stop buffer (ticks)", GroupName = "Trade", Order = 24), Range(0, 1000)]
    public int StopBufferTicks { get => _buffer; set { _buffer = Math.Clamp(value, 0, 1000); RecalculateValues(); } }
    [Display(Name = "Trend filter (EMA)", GroupName = "Trade", Order = 25)]
    public bool TrendFilter { get => _trendFilter; set { _trendFilter = value; RecalculateValues(); } }
    [Display(Name = "Trend EMA length", GroupName = "Trade", Order = 26), Range(2, 1000)]
    public int TrendLength { get => _trendLength; set { _trendLength = Math.Clamp(value, 2, 1000); RecalculateValues(); } }
    [Display(Name = "Session box", GroupName = "Drawing", Order = 30)] public bool ShowSessionBox { get; set; } = true;
    [Display(Name = "Trade boxes", GroupName = "Drawing", Order = 31)] public bool ShowTradeBoxes { get; set; } = true;
    [Display(Name = "Session background", GroupName = "Drawing", Order = 32)] public bool ShowBackground { get; set; } = true;
    [Display(Name = "Sessions drawn", GroupName = "Drawing", Order = 33, Description = "Display only: hides the range boxes and levels of older Asian sessions; never changes detection, strategy series or alerts."), Range(1, 100)]
    public int MaxSessions { get => _maxSessions; set { _maxSessions = Math.Clamp(value, 1, 100); RedrawChart(); } }
    [Display(Name = "Session", GroupName = "Colors", Order = 40)] public Color SessionColor { get; set; } = StudyPalette.Amber;
    [Display(Name = "Bullish", GroupName = "Colors", Order = 41)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 42)] public Color BearColor { get; set; } = StudyPalette.Bear;

    protected override void ResetState()
    {
        ResetRunning(); _sessionOf.Clear(); _sessions = 0;
    }

    private void ResetRunning()
    {
        _run = new(LocalSessionWindow.Of(WorldSession.Asia)); _step = default; _ema = new(TrendLength);
        _rangeReady = _hiSwept = _loSwept = false; _ihCrossed = _ilCrossed = true;
        _rh = _rl = _sweepExt = _ih = _il = _entry = _stop = _target = 0;
        _sweepDir = _sweepBar = _ihBar = _ilBar = _trend = _pos = _entryBar = _setups = _wins = _losses = 0;
        _hiLine = _loLine = _stopLine = _targetLine = null;
    }

    protected override void OnCoverageGap(int bar)
    {
        _hiLine?.End(bar); _loLine?.End(bar); _stopLine?.End(bar); _targetLine?.End(bar);
        ResetRunning();
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; _step.Observe(b.Time);
        decimal ema = _ema.Add(b.Close);
        int signal = 0, sweep = 0;
        bool inSession = _run.Add(b, bar, _step.Value, out bool started, out var before, out var here);
        if (before is { } prior) RangeDone(prior);
        if (started)
        {
            _hiSwept = _loSwept = false; _sweepDir = 0; _rangeReady = false;
            if (_pos != 0 && CloseAtNewSession)
            {
                Scene.Mark(bar, bar, b.Open, StudyMarkKind.Cross, StudyPalette.Muted, "Session close", _pos > 0);
                EndTrade(bar);
            }
        }
        if (here is { } range) RangeDone(range);
        if (inSession && ShowBackground) Scene.Tints[bar] = StudyPalette.A(SessionColor, 22);

        var (swingHigh, swingLow) = StudySwings.Confirm(Bars, bar, InternalLength, InternalLength);
        if (swingHigh is { } h) { _ih = h.Price; _ihBar = h.Bar; _ihCrossed = false; }
        if (swingLow is { } l) { _il = l.Price; _ilBar = l.Bar; _ilCrossed = false; }

        // manage an open trade before entries: an exit bar does not enter
        if (_pos != 0)
        {
            bool hitStop = _pos > 0 ? b.Low <= _stop : b.High >= _stop, hitTarget = _pos > 0 ? b.High >= _target : b.Low <= _target;
            if (hitStop || hitTarget)
            {
                if (hitStop) _losses++; else _wins++;
                Scene.Mark(bar, bar, _pos > 0 ? b.High : b.Low, StudyMarkKind.Cross, hitStop ? BearColor : BullColor, hitStop ? "Stop" : "Target", _pos > 0);
                if (ShowTradeBoxes)
                {
                    Scene.Box(_entryBar, bar, _target, _entry, BullColor, fillAlpha: 50).End(bar);
                    Scene.Box(_entryBar, bar, _entry, _stop, BearColor, fillAlpha: 50).End(bar);
                }
                EndTrade(bar);
            }
        }

        // the first sweep of each side outside the session, with the previous wick still inside the range
        if (!inSession && _rangeReady && bar > 0)
        {
            var pb = Bars[bar - 1];
            if (!_hiSwept && b.High > _rh && pb.High <= _rh)
            {
                _hiSwept = true; _sweepDir = -1; _sweepBar = bar; _sweepExt = b.High; sweep = -1;
                if (ShowSweeps) Scene.Tag(bar, bar, b.High, "Asia sweep", StudyPalette.A(BearColor, 200));
            }
            else if (!_loSwept && b.Low < _rl && pb.Low >= _rl)
            {
                _loSwept = true; _sweepDir = 1; _sweepBar = bar; _sweepExt = b.Low; sweep = 1;
                if (ShowSweeps) Scene.Tag(bar, bar, b.Low, "Asia sweep", StudyPalette.A(BullColor, 200), false);
            }
            if (_sweepDir < 0) _sweepExt = Math.Max(_sweepExt, b.High);
            else if (_sweepDir > 0) _sweepExt = Math.Min(_sweepExt, b.Low);
        }

        // internal CHoCH: a close (or wick) through the last internal swing against the internal trend
        int shift = 0;
        if (!_ilCrossed && bar > _ilBar + InternalLength && (WickBreak ? b.Low < _il : b.Close < _il))
        {
            _ilCrossed = true;
            if (_trend >= 0)
            {
                shift = -1;
                if (ShowStructure)
                {
                    Scene.Segment(_ilBar, bar, bar, _il, _il, BearColor, 1, LineDashStyle.Dash);
                    Scene.Tag((_ilBar + bar) / 2, bar, _il, "CHoCH", StudyPalette.A(BearColor, 200), false);
                }
            }
            _trend = -1;
        }
        else if (!_ihCrossed && bar > _ihBar + InternalLength && (WickBreak ? b.High > _ih : b.Close > _ih))
        {
            _ihCrossed = true;
            if (_trend <= 0)
            {
                shift = 1;
                if (ShowStructure)
                {
                    Scene.Segment(_ihBar, bar, bar, _ih, _ih, BullColor, 1, LineDashStyle.Dash);
                    Scene.Tag((_ihBar + bar) / 2, bar, _ih, "CHoCH", StudyPalette.A(BullColor, 200));
                }
            }
            _trend = 1;
        }

        if (_pos == 0 && _sweepDir != 0 && shift == _sweepDir && bar - _sweepBar <= MaxBarsAfter)
        {
            bool isLong = _sweepDir > 0;
            decimal risk = Math.Abs(b.Close - _sweepExt) + Tick * StopBufferTicks;
            if (risk > 0 && (!TrendFilter || _ema.Ready && (isLong ? b.Close > ema : b.Close < ema)))
            {
                _pos = isLong ? 1 : -1; _entry = b.Close; _entryBar = bar; _setups++; signal = _pos;
                _stop = isLong ? _entry - risk * StopMultiple : _entry + risk * StopMultiple;
                _target = isLong ? _entry + risk * RewardMultiple : _entry - risk * RewardMultiple;
                var color = isLong ? BullColor : BearColor;
                Scene.Mark(bar, bar, isLong ? b.Low : b.High, isLong ? StudyMarkKind.TriangleUp : StudyMarkKind.TriangleDown, color, isLong ? "Buy" : "Sell", !isLong);
                _stopLine = Scene.Level(bar, bar, _stop, BearColor, "Stop", "Stop", 1, LineDashStyle.Dash);
                _targetLine = Scene.Level(bar, bar, _target, BullColor, "Target", "Target", 1, LineDashStyle.Dash);
            }
            _sweepDir = 0;
        }

        _signal.Set(bar, signal); _position.Set(bar, _pos); _sweep.Set(bar, sweep);
        Dashboard();
    }

    private void RangeDone(CompletedWindowSession c)
    {
        _rh = c.High; _rl = c.Low; _rangeReady = true;
        int session = _sessions++;
        if (ShowSessionBox)
        {
            var box = Scene.Box(c.From, c.Available, c.High, c.Low, SessionColor, "Asia", fillAlpha: 44);
            box.To = c.Last; box.EndAvailable = c.Available; _sessionOf[box] = session;
        }
        _hiLine?.End(c.Available); _loLine?.End(c.Available);
        _hiLine = Scene.Level(c.From, c.Available, c.High, SessionColor, "Asia high", "Asia high", 1, LineDashStyle.Dash);
        _loLine = Scene.Level(c.From, c.Available, c.Low, SessionColor, "Asia low", "Asia low", 1, LineDashStyle.Dash);
        _sessionOf[_hiLine] = _sessionOf[_loLine] = session;
    }

    /// <summary>Display only: the last <see cref="MaxSessions"/> Asian ranges are drawn; live (alertable) levels always show.</summary>
    private bool Drawn(StudyShape shape) => shape.Live || !_sessionOf.TryGetValue(shape, out int session) || _sessions - session <= MaxSessions;

    public override bool IsShown(StudyBox box) => Drawn(box);
    public override bool IsShown(StudyLine line) => Drawn(line);

    private void EndTrade(int bar)
    {
        _stopLine?.End(bar); _targetLine?.End(bar); _stopLine = _targetLine = null; _pos = 0;
    }

    private void Dashboard()
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Asian session sweeps";
        Scene.Row("Position", _pos > 0 ? "Long" : _pos < 0 ? "Short" : "Flat", _pos > 0 ? BullColor : _pos < 0 ? BearColor : default);
        Scene.Row("Entries", _setups.ToString(CultureInfo.InvariantCulture));
        Scene.Row("Targets / stops", string.Create(CultureInfo.InvariantCulture, $"{_wins} / {_losses}"));
        if (_rangeReady) Scene.Row("Asia range", $"{Price(_rl)} – {Price(_rh)}", SessionColor);
    }
}

[DisplayName("Session sweeps (Asia, London, New York)")]
[Category(IndicatorCategories.Sessions)]
[Description("When a session (Asia = Tokyo 09:00–18:00, London 08:00–17:00, New York morning 08:00–13:00 and afternoon 13:00–19:00, each in its own time zone) completes, its high and low become defended. A bar that trades through a defended level after opening on the original side builds a sweep zone one ATR(21) margin deep; a decisive close beyond it kills the zone (a break) and a stall for the set number of bars ends it and re-arms. Optional changes of character (CHoCH) and fair value gaps. Everything is known at the close of the bar that shows it. Needs bars of 5 minutes or less.")]
public class SessionSweeps : ClosedBarStudy
{
    private sealed class Zone
    {
        public bool Armed, Live;
        public int From, Right;
        public decimal Level, Margin;
        public StudyBox? Box;
    }

    private static readonly LocalSessionWindow NewYorkMorningWindow = new(LocalSessionWindow.Of(WorldSession.NewYork).Zone, TimeSpan.FromHours(8), TimeSpan.FromHours(13));
    private static readonly LocalSessionWindow NewYorkAfternoonWindow = new(LocalSessionWindow.Of(WorldSession.NewYork).Zone, TimeSpan.FromHours(13), TimeSpan.FromHours(19));
    private static readonly (string Name, Color Color)[] Sessions =
    {
        ("Asia", StudyPalette.Amber), ("London", StudyPalette.Neutral), ("New York AM", StudyPalette.Bull), ("New York PM", Color.FromArgb(255, 171, 71, 188)),
    };

    private decimal _upMargin = 1.3m, _downMargin = 1.3m, _fvgFilter = 1;
    private int _stall = 5, _mssLength = 8;
    private bool _buyside = true, _sellside = true, _once, _asia = true, _london = true, _nyAm = true, _nyPm = true, _mss, _fvg;
    private readonly OptionalValueDataSeries _signal = new("SessionSweeps.Signal", "Sweep zone built (+1 below a defended low, -1 above a defended high)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _break = new("SessionSweeps.Break", "Sweep zone broken (+1 upward, -1 downward)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly WindowSessionRun?[] _runs = new WindowSessionRun?[4];
    private SessionBarStep _step;
    private RunningAtr _atr = new(21), _atrSlow = new(144);
    private decimal? _defendedHigh, _defendedLow;
    private string? _defended;
    private Zone _buy = new(), _sell = new();
    private int _upCount, _downCount, _shift;
    private StudyLine? _highLine, _lowLine;
    private decimal _ph, _pl;
    private bool _phCrossed = true, _plCrossed = true;

    public SessionSweeps()
    {
        DataSeries[0] = _signal; DataSeries.Add(_break);
    }

    [Display(Name = "Sweep zones above", GroupName = "Sweeps", Order = 10)]
    public bool Buyside { get => _buyside; set { _buyside = value; RecalculateValues(); } }
    [Display(Name = "Margin above (ATR 21)", GroupName = "Sweeps", Order = 11), Range(0.1, 10)]
    public decimal BuysideMargin { get => _upMargin; set { _upMargin = Math.Clamp(value, 0.1m, 10); RecalculateValues(); } }
    [Display(Name = "Sweep zones below", GroupName = "Sweeps", Order = 12)]
    public bool Sellside { get => _sellside; set { _sellside = value; RecalculateValues(); } }
    [Display(Name = "Margin below (ATR 21)", GroupName = "Sweeps", Order = 13), Range(0.1, 10)]
    public decimal SellsideMargin { get => _downMargin; set { _downMargin = Math.Clamp(value, 0.1m, 10); RecalculateValues(); } }
    [Display(Name = "Stalled bars to re-arm", GroupName = "Sweeps", Order = 14), Range(1, 100)]
    public int StallBars { get => _stall; set { _stall = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "One sweep per session", GroupName = "Sweeps", Order = 15)]
    public bool DetectOnce { get => _once; set { _once = value; RecalculateValues(); } }
    [Display(Name = "Asia (Tokyo 09:00–18:00)", GroupName = "Sessions", Order = 20)]
    public bool AsiaSession { get => _asia; set { _asia = value; RecalculateValues(); } }
    [Display(Name = "London (08:00–17:00)", GroupName = "Sessions", Order = 21)]
    public bool LondonSession { get => _london; set { _london = value; RecalculateValues(); } }
    [Display(Name = "New York morning (08:00–13:00)", GroupName = "Sessions", Order = 22)]
    public bool NewYorkMorning { get => _nyAm; set { _nyAm = value; RecalculateValues(); } }
    [Display(Name = "New York afternoon (13:00–19:00)", GroupName = "Sessions", Order = 23)]
    public bool NewYorkAfternoon { get => _nyPm; set { _nyPm = value; RecalculateValues(); } }
    [Display(Name = "Changes of character (CHoCH)", GroupName = "Extras", Order = 30)]
    public bool ShowMss { get => _mss; set { _mss = value; RecalculateValues(); } }
    [Display(Name = "Pivot length", GroupName = "Extras", Order = 31), Range(2, 50)]
    public int MssLength { get => _mssLength; set { _mssLength = Math.Clamp(value, 2, 50); RecalculateValues(); } }
    [Display(Name = "Fair value gaps", GroupName = "Extras", Order = 32)]
    public bool ShowFvg { get => _fvg; set { _fvg = value; RecalculateValues(); } }
    [Display(Name = "Fair value gap filter (ATR 144)", GroupName = "Extras", Order = 33), Range(0, 5)]
    public decimal FvgFilter { get => _fvgFilter; set { _fvgFilter = Math.Clamp(value, 0, 5); RecalculateValues(); } }
    [Display(Name = "Session boxes", GroupName = "Drawing", Order = 40)] public bool ShowSessionBoxes { get; set; } = true;
    [Display(Name = "Session names", GroupName = "Drawing", Order = 41)] public bool ShowSessionNames { get; set; } = true;
    [Display(Name = "Bullish", GroupName = "Colors", Order = 50)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 51)] public Color BearColor { get; set; } = StudyPalette.Bear;

    private bool Enabled(int k) => k switch { 0 => AsiaSession, 1 => LondonSession, 2 => NewYorkMorning, _ => NewYorkAfternoon };

    private static LocalSessionWindow Window(int k) => k switch
    {
        0 => LocalSessionWindow.Of(WorldSession.Tokyo), 1 => LocalSessionWindow.Of(WorldSession.London), 2 => NewYorkMorningWindow, _ => NewYorkAfternoonWindow,
    };

    protected override void ResetState()
    {
        Array.Clear(_runs); _step = default; _atr = new(21); _atrSlow = new(144);
        _defendedHigh = _defendedLow = null; _defended = null; _buy = new(); _sell = new();
        _upCount = _downCount = _shift = 0; _highLine = _lowLine = null;
        _ph = _pl = 0; _phCrossed = _plCrossed = true;
    }

    protected override void OnCoverageGap(int bar)
    {
        Retire(_buy, bar); Retire(_sell, bar); _highLine?.End(bar); _lowLine?.End(bar);
        ResetState();
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; _step.Observe(b.Time);
        decimal atr = _atr.Add(b), atrSlow = _atrSlow.Add(b);
        int signal = 0, broke = 0;
        if (_step.Value > TimeSpan.FromMinutes(5))
        {
            _signal.Set(bar, 0); _break.Set(bar, 0);
            Scene.Dash.Clear(); Scene.DashTitle = "Session sweeps"; Scene.Row("Bar period", "Use bars of 5 minutes or less");
            return;
        }
        for (int k = 0; k < Sessions.Length; k++)
        {
            if (!Enabled(k)) continue;
            var run = _runs[k] ??= new WindowSessionRun(Window(k));
            run.Add(b, bar, _step.Value, out bool started, out var before, out var here);
            if (before is { } ended) Defend(k, ended);
            if (started && ShowSessionNames) Scene.Tag(bar, bar, b.Low, Sessions[k].Name, Sessions[k].Color, false);
            if (here is { } closed) Defend(k, closed);
        }

        if (Buyside && _defendedHigh is { } high) signal += Step(_buy, true, high, BuysideMargin * atr, bar, b, ref broke);
        if (Sellside && _defendedLow is { } low) signal += Step(_sell, false, low, SellsideMargin * atr, bar, b, ref broke);

        if (ShowMss)
        {
            var (swingHigh, swingLow) = StudySwings.Confirm(Bars, bar, MssLength, MssLength);
            if (swingHigh is { } h) { _ph = h.Price; _phCrossed = false; }
            if (swingLow is { } l) { _pl = l.Price; _plCrossed = false; }
            if (!_phCrossed && b.Close > _ph)
            {
                _phCrossed = true;
                if (_shift < 0) Scene.Tag(bar, bar, b.Low, "CHoCH", StudyPalette.A(BullColor, 200), false);
                _shift = 1;
            }
            if (!_plCrossed && b.Close < _pl)
            {
                _plCrossed = true;
                if (_shift > 0) Scene.Tag(bar, bar, b.High, "CHoCH", StudyPalette.A(BearColor, 200));
                _shift = -1;
            }
        }

        if (ShowFvg && bar >= 2 && AllCausal(bar - 2, bar))
        {
            var a = Bars[bar - 2]; decimal threshold = FvgFilter * atrSlow;
            StudyBox? gap = null;
            if (b.Low - a.High > threshold && b.Low > a.High) gap = Scene.Box(bar - 1, bar, b.Low, a.High, BullColor, "FVG", fillAlpha: 50);
            else if (a.Low - b.High > threshold && b.High < a.Low) gap = Scene.Box(bar - 1, bar, a.Low, b.High, BearColor, "FVG", fillAlpha: 50);
            if (gap != null) { gap.To = bar + 10; gap.EndAvailable = bar; } // a fixed ten-bar projection, known at this close
        }

        _signal.Set(bar, signal); _break.Set(bar, broke);
        Scene.Dash.Clear(); Scene.DashTitle = "Session sweeps";
        Scene.Row("Defended session", _defended ?? "—");
        Scene.Row("Sweeps up / down", string.Create(CultureInfo.InvariantCulture, $"{_upCount} / {_downCount}"));
        Scene.Row("Upper zone", _buy.Live ? "Live" : "—", _buy.Live ? BearColor : default);
        Scene.Row("Lower zone", _sell.Live ? "Live" : "—", _sell.Live ? BullColor : default);
    }

    private void Defend(int k, CompletedWindowSession c)
    {
        var color = Sessions[k].Color;
        if (ShowSessionBoxes)
        {
            var box = Scene.Box(c.From, c.Available, c.High, c.Low, color, fillAlpha: 36);
            box.To = c.Last; box.EndAvailable = c.Available;
            decimal mid = (c.High + c.Low) / 2;
            Scene.Segment(c.From, c.Last, c.Available, mid, mid, StudyPalette.A(color, 120), 1, LineDashStyle.Dot);
        }
        _defendedHigh = c.High; _defendedLow = c.Low; _defended = Sessions[k].Name;
        Retire(_buy, c.Available); Retire(_sell, c.Available); _buy = new(); _sell = new();
        _highLine?.End(c.Available); _lowLine?.End(c.Available);
        _highLine = Scene.Level(c.From, c.Available, c.High, StudyPalette.A(BearColor, 200), "Defended high", "Defended high", 1, LineDashStyle.Dash);
        _lowLine = Scene.Level(c.From, c.Available, c.Low, StudyPalette.A(BullColor, 200), "Defended low", "Defended low", 1, LineDashStyle.Dash);
    }

    private static void Retire(Zone zone, int bar)
    {
        if (zone.Live) zone.Box?.End(bar);
        zone.Live = false;
    }

    private int Step(Zone z, bool up, decimal level, decimal margin, int bar, StudyBar b, ref int broke)
    {
        if (!z.Armed)
        {
            if (!(up ? b.High > level && b.Open < level : b.Low < level && b.Open > level)) return 0;
            z.Armed = z.Live = true; z.From = bar; z.Right = bar + 1; z.Level = level; z.Margin = margin;
            if (up) _upCount++; else _downCount++;
            var color = up ? BearColor : BullColor;
            Scene.Mark(bar, bar, up ? b.High : b.Low, StudyMarkKind.Circle, color, null, up);
            z.Box = Scene.Box(bar, bar, up ? level + margin : level, up ? level : level - margin, color, "Sweep", up ? "Buy-side sweep zone" : "Sell-side sweep zone");
            return up ? -1 : 1;
        }
        if (!z.Live) return 0;
        decimal m = z.Margin;
        if (b.Low > level - m && b.High < level + m)
        {
            if (up ? b.High > level : b.Low < level) z.Right = bar;
        }
        else if (up ? b.Close > level + m * 1.5m : b.Close < level - m * 1.5m)
        {
            z.Live = false; broke += up ? 1 : -1;
            if (z.Box != null) { z.Box.End(bar); z.Box.Color = StudyPalette.Muted; }
            Scene.Tag(bar, bar, up ? b.High : b.Low, "Break", StudyPalette.Muted, up);
        }
        else if (bar - z.Right >= StallBars)
        {
            z.Live = false;
            if (z.Box != null) { z.Box.To = z.Right; z.Box.EndAvailable = bar; }
            if (!DetectOnce) z.Armed = false;
        }
        return 0;
    }
}

public enum SessionGapKind
{
    [Display(Name = "All")] All,
    [Display(Name = "Overlapping the prior bar")] Overlapping,
    [Display(Name = "Clean (beyond the prior wick)")] Clean,
}

[DisplayName("Session gaps and fills")]
[Category(IndicatorCategories.Sessions)]
[Description("The gap between each exchange session's first open and the prior session's close (from the chart's session calendar): bullish or bearish, overlapping the prior bar or clean beyond its wick. Marks the bar that fills it (trades back to the prior close), the maximum adverse excursion and statistics of how many gaps fill and how many reverse. A gap is known at the close of the session's first bar; its statistics count once the session has ended.")]
public class SessionGapFill : ClosedBarStudy
{
    private SessionGapKind _filter = SessionGapKind.All;
    private readonly OptionalValueDataSeries _gap = new("SessionGapFill.Gap", "Session gap at this bar (+1 up, -1 down)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _filled = new("SessionGapFill.Filled", "Session gap filled at this bar (+1 gap up filled, -1 gap down filled)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private bool _valid, _bull;
    private int _start = -1, _fill = -1, _bullCount, _bearCount, _bullFilled, _bearFilled, _bullReversed, _bearReversed, _fills;
    private long _fillBars;
    private decimal _open, _priorClose, _excursion;
    private StudyBox? _box;
    private StudyLine? _level;
    private bool _showFilledBull = true, _showFilledBear = true, _showUnfilled = true;
    // presentation only: which gap each box/level/excursion line belongs to, for the filled/unfilled display filters
    private sealed class GapView(bool bull) { public readonly bool Bull = bull; public bool Filled, Done; }
    private readonly Dictionary<StudyShape, GapView> _views = new(ReferenceEqualityComparer.Instance);
    private GapView? _view;

    public SessionGapFill()
    {
        DataSeries[0] = _gap; DataSeries.Add(_filled);
    }

    [Display(Name = "Gaps", GroupName = "Calculation", Order = 10)]
    public SessionGapKind Filter { get => _filter; set { _filter = value; RecalculateValues(); } }
    [Display(Name = "Prior session close level", GroupName = "Drawing", Order = 20)] public bool ShowLevel { get; set; } = true;
    [Display(Name = "Maximum adverse excursion", GroupName = "Drawing", Order = 21)] public bool ShowDeviation { get; set; } = true;
    [Display(Name = "Filled bullish gaps", GroupName = "Drawing", Order = 22, Description = "Display only: hides the drawings of ended sessions' filled bullish gaps; never changes detection, strategy series or alerts.")]
    public bool ShowFilledBull { get => _showFilledBull; set { _showFilledBull = value; RedrawChart(); } }
    [Display(Name = "Filled bearish gaps", GroupName = "Drawing", Order = 23, Description = "Display only: hides the drawings of ended sessions' filled bearish gaps; never changes detection, strategy series or alerts.")]
    public bool ShowFilledBear { get => _showFilledBear; set { _showFilledBear = value; RedrawChart(); } }
    [Display(Name = "Unfilled gaps", GroupName = "Drawing", Order = 24, Description = "Display only: hides the drawings of ended sessions' unfilled gaps; never changes detection, strategy series or alerts.")]
    public bool ShowUnfilled { get => _showUnfilled; set { _showUnfilled = value; RedrawChart(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 30)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 31)] public Color BearColor { get; set; } = StudyPalette.Bear;

    protected override void ResetState()
    {
        ResetRunning(); _views.Clear();
    }

    private void ResetRunning()
    {
        _valid = _bull = false; _start = _fill = -1;
        _bullCount = _bearCount = _bullFilled = _bearFilled = _bullReversed = _bearReversed = _fills = 0; _fillBars = 0;
        _open = _priorClose = _excursion = 0; _box = null; _level = null; _view = null;
    }

    protected override void OnCoverageGap(int bar)
    {
        if (_box is { Live: true }) _box.End(bar);
        _level?.End(bar);
        if (_view != null) _view.Done = true;
        ResetRunning();
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; int gap = 0, filled = 0;
        if (bar > 0 && b.SessionDate != Bars[bar - 1].SessionDate)
        {
            var pb = Bars[bar - 1];
            Harvest(bar - 1, bar);
            _valid = false; _start = _fill = -1;
            if (pb.Causal && b.Open != pb.Close)
            {
                bool bull = b.Open > pb.Close;
                bool overlap = bull ? b.Low < pb.High : b.High > pb.Low, clean = bull ? b.Low > pb.High : b.High < pb.Low;
                bool keep = Filter switch { SessionGapKind.Overlapping => overlap, SessionGapKind.Clean => clean, _ => overlap || clean };
                if (keep)
                {
                    _valid = true; _bull = bull; _start = bar; _open = b.Open; _priorClose = pb.Close; _excursion = bull ? b.High : b.Low; gap = bull ? 1 : -1;
                    var color = bull ? BullColor : BearColor;
                    _box = Scene.Box(bar, bar, _open, _priorClose, color, "Gap", "Session gap", fillAlpha: 60);
                    if (ShowLevel) _level = Scene.Level(bar, bar, _priorClose, color, "Prior session close", "Prior session close", 1, LineDashStyle.Dot);
                    _view = _views[_box] = new GapView(bull);
                    if (_level != null) _views[_level] = _view;
                }
            }
        }
        if (_valid)
        {
            _excursion = _bull ? Math.Max(_excursion, b.High) : Math.Min(_excursion, b.Low);
            if (_fill < 0 && (_bull ? b.Low <= _priorClose : b.High >= _priorClose))
            {
                _fill = bar; filled = _bull ? 1 : -1; _box?.End(bar); if (_view != null) _view.Filled = true;
                Scene.Mark(bar, bar, _priorClose, _bull ? StudyMarkKind.TriangleDown : StudyMarkKind.TriangleUp, _bull ? BullColor : BearColor, "Filled", !_bull);
            }
        }
        _gap.Set(bar, gap); _filled.Set(bar, filled);
        Dashboard();
    }

    /// <summary>Closes the gap of the session that ended at <paramref name="end"/>, known at the close of <paramref name="available"/>.</summary>
    private void Harvest(int end, int available)
    {
        if (!_valid) return;
        bool done = _fill >= 0;
        if (_bull) { _bullCount++; if (done) _bullFilled++; if (Bars[end].Close < _open) _bullReversed++; }
        else { _bearCount++; if (done) _bearFilled++; if (Bars[end].Close > _open) _bearReversed++; }
        if (done) { _fillBars += _fill - _start; _fills++; }
        if (_box != null)
        {
            if (_box.Live) { _box.To = end; _box.EndAvailable = available; }
            _box.FillAlpha = done ? 30 : 60;
        }
        if (_level != null) { _level.To = end; _level.EndAvailable = available; }
        if (ShowDeviation && _view != null) _views[Scene.Segment(_start, end, available, _excursion, _excursion, StudyPalette.Muted, 1, LineDashStyle.Dash)] = _view;
        if (_view != null) _view.Done = true;
        _box = null; _level = null; _view = null;
    }

    /// <summary>Display only: an ended session's gap is drawn per the filled/unfilled filters; the current session's gap always shows.</summary>
    private bool Drawn(StudyShape shape) => !_views.TryGetValue(shape, out var v) || !v.Done
        || (v.Filled ? (v.Bull ? ShowFilledBull : ShowFilledBear) : ShowUnfilled);

    public override bool IsShown(StudyBox box) => Drawn(box);
    public override bool IsShown(StudyLine line) => Drawn(line);

    private void Dashboard()
    {
        static string Pct(int a, int n) => n == 0 ? "—" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * a / n:0}% ({a}/{n})");
        Scene.Dash.Clear(); Scene.DashTitle = "Session gaps";
        Scene.Row("Bullish gaps filled", Pct(_bullFilled, _bullCount), BullColor);
        Scene.Row("Bearish gaps filled", Pct(_bearFilled, _bearCount), BearColor);
        Scene.Row("Bullish gaps reversed", Pct(_bullReversed, _bullCount));
        Scene.Row("Bearish gaps reversed", Pct(_bearReversed, _bearCount));
        if (_fills > 0) Scene.Row("Average bars to fill", ((decimal)_fillBars / _fills).ToString("0.#", CultureInfo.InvariantCulture));
        if (!_valid) return;
        var color = _bull ? BullColor : BearColor;
        Scene.Row("Current gap", (_bull ? "Bullish" : "Bearish") + " · " + (_fill >= 0 ? "filled" : "open"), color);
        Scene.Row("Maximum adverse excursion", Price(_excursion), color);
        if (_fill >= 0) Scene.Row("Bars to fill", (_fill - _start).ToString(CultureInfo.InvariantCulture));
    }
}
