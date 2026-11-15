// Independent closed-bar implementations; capability reference gouqizi2214730-source/trading-hub @ 97a8521
// (MIT with Commons Clause). Not a port of its code.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TradingHub.Indicators.Native;

/// <summary>Small causal helpers shared by the sweep and breakout studies.</summary>
internal static class SweepMath
{
    /// <summary>
    /// The three-bar fair value gap completed by bar <paramref name="bar"/> (bars <c>bar-2..bar</c>, all causal): bullish when
    /// its low is above the high two bars back, bearish when its high is below that bar's low; at least <paramref name="min"/> wide.
    /// </summary>
    internal static (bool Bull, decimal Top, decimal Bottom)? Gap(IReadOnlyList<StudyBar> bars, int bar, decimal min)
    {
        if (bar < 2 || !bars[bar - 2].Causal || !bars[bar - 1].Causal || !bars[bar].Causal) return null;
        var a = bars[bar - 2]; var c = bars[bar];
        if (c.Low > a.High && c.Low - a.High >= min) return (true, c.Low, a.High);
        if (c.High < a.Low && a.Low - c.High >= min) return (false, a.Low, c.High);
        return null;
    }

    internal static string Volume(decimal v) => v switch
    {
        >= 1_000_000_000 => (v / 1_000_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (v / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (v / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => v.ToString("0.##", CultureInfo.InvariantCulture),
    };

    internal static string Name(WorldSession session) =>
        typeof(WorldSession).GetField(session.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? session.ToString();

    internal static OptionalValueDataSeries Series(string id, string name) => new(id, name) { VisualType = VisualMode.Hide, ShowZeroValue = true };

    /// <summary>Setting description of every display-only visibility filter.</summary>
    internal const string DisplayOnly = "Display only: hides drawings; never changes detection, strategy series or alerts.";
}

public enum LiquiditySweepMode
{
    [Display(Name = "Wicks and retests")] All,
    [Display(Name = "Wicks only")] WicksOnly,
    [Display(Name = "Breaks and retests only")] RetestsOnly,
}

[DisplayName("Liquidity sweeps (wick and retest)")]
[Category(IndicatorCategories.Structure)]
[Description("Every confirmed swing high and low becomes a liquidity level from the close that confirms it. A wick sweep is a bar that trades through an unbroken level and closes back on its original side; a retest sweep is a bar that, after a close has broken the level, trades back through it and closes on the broken side again. Each sweep draws the swept area from the sweeping bar until a close goes through it or it ages out. Heuristic; a sweep is known at the sweeping bar's close.")]
public class LiquiditySweeps : ClosedBarStudy
{
    private const int LevelLifetime = 2000;

    private sealed class SweepLevel(StudySwing swing, StudyLine line)
    {
        public StudySwing Swing { get; } = swing;
        public StudyLine Line { get; } = line;
        public bool Broken, Wicked, Dead;
    }

    private int _length = 5, _maxBars = 300;
    private LiquiditySweepMode _mode = LiquiditySweepMode.All;
    private bool _extend = true, _showZones = true, _showLevels = true, _showLabels = true, _showBackground = true;
    private readonly List<SweepLevel> _levels = new();
    private readonly List<(StudyBox Box, bool Bull)> _zones = new();
    private int _bullWick, _bearWick, _bullRetest, _bearRetest, _barWick, _barRetest;
    private string _last = "None";
    private Color _lastColor;
    private readonly OptionalValueDataSeries _signal = SweepMath.Series("LiquiditySweeps.Signal", "Sweep direction (+1 bullish, -1 bearish)");
    private readonly OptionalValueDataSeries _wick = SweepMath.Series("LiquiditySweeps.Wick", "Wick sweep (+1 bullish, -1 bearish)");
    private readonly OptionalValueDataSeries _retest = SweepMath.Series("LiquiditySweeps.Retest", "Retest sweep (+1 bullish, -1 bearish)");

    public LiquiditySweeps() { DataSeries[0] = _signal; DataSeries.Add(_wick); DataSeries.Add(_retest); }

    [Display(Name = "Swing strength (bars each side)", GroupName = "Calculation", Order = 10), Range(1, 100)]
    public int SwingLength { get => _length; set { _length = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Sweeps", GroupName = "Calculation", Order = 11)]
    public LiquiditySweepMode Mode { get => _mode; set { _mode = value; RecalculateValues(); } }
    [Display(Name = "Extend the swept area until broken", GroupName = "Calculation", Order = 12)]
    public bool ExtendUntilBroken { get => _extend; set { _extend = value; RecalculateValues(); } }
    [Display(Name = "Swept area lifetime (bars)", GroupName = "Calculation", Order = 13), Range(1, 5000)]
    public int MaxZoneBars { get => _maxBars; set { _maxBars = Math.Clamp(value, 1, 5000); RecalculateValues(); } }
    // display-only filters: every area, level, label and tint is always recorded (and alertable); IsShown hides them
    [Display(Name = "Swept areas", GroupName = "Drawing", Order = 20, Description = SweepMath.DisplayOnly)]
    public bool ShowZones { get => _showZones; set { _showZones = value; RedrawChart(); } }
    [Display(Name = "Liquidity levels", GroupName = "Drawing", Order = 21, Description = SweepMath.DisplayOnly)]
    public bool ShowLevels { get => _showLevels; set { _showLevels = value; RedrawChart(); } }
    [Display(Name = "Labels (wick / retest)", GroupName = "Drawing", Order = 22, Description = SweepMath.DisplayOnly)]
    public bool ShowLabels { get => _showLabels; set { _showLabels = value; RedrawChart(); } }
    [Display(Name = "Background on the sweep bar", GroupName = "Drawing", Order = 23, Description = SweepMath.DisplayOnly)]
    public bool ShowBackground { get => _showBackground; set { _showBackground = value; RedrawChart(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 30)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 31)] public Color BearColor { get; set; } = StudyPalette.Bear;

    // the only boxes, lines, tags and tints of this study are swept areas, liquidity levels, sweep labels and sweep tints
    public override bool IsShown(StudyBox box) => ShowZones;
    public override bool IsShown(StudyLine line) => ShowLevels;
    public override bool IsShown(StudyTag tag) => ShowLabels;
    public override bool IsTintShown(int bar, Color color) => ShowBackground;

    protected override void ResetState()
    {
        _levels.Clear(); _zones.Clear();
        _bullWick = _bearWick = _bullRetest = _bearRetest = 0; _last = "None"; _lastColor = default;
    }

    protected override void OnCoverageGap(int bar)
    {
        foreach (var (box, _) in _zones) box.End(bar);
        foreach (var level in _levels) level.Line.End(bar);
        ResetState();
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; _barWick = _barRetest = 0;
        // swept areas end on a close through them, by age, or at once when they do not extend
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var (box, bull) = _zones[i];
            bool broken = bull ? b.Close < box.Bottom : b.Close > box.Top;
            if (!broken && ExtendUntilBroken && bar - box.From < MaxZoneBars) continue;
            box.End(bar); _zones.RemoveAt(i);
        }
        // levels confirmed at earlier closes only
        foreach (var level in _levels)
        {
            if (bar - level.Swing.Bar > LevelLifetime) { level.Dead = true; continue; }
            decimal p = level.Swing.Price; bool high = level.Swing.High;
            bool beyond = high ? b.Close > p : b.Close < p, back = high ? b.Close < p : b.Close > p;
            bool through = high ? b.High > p : b.Low < p, reclaimed = high ? b.Low < p : b.High > p;
            if (!level.Broken)
            {
                if (beyond) { if (Mode == LiquiditySweepMode.WicksOnly) level.Dead = true; else level.Broken = true; }
                else if (Mode != LiquiditySweepMode.RetestsOnly && !level.Wicked && through && back)
                {
                    // traded through the untouched level and closed back: a sweep against the level's side
                    Sweep(bar, !high, high ? b.High : p, high ? p : b.Low, true); level.Wicked = true;
                }
            }
            else if (back) level.Dead = true;
            else if (reclaimed && beyond)
            {
                // after the break, price came back through the level and closed on the broken side again
                Sweep(bar, high, high ? p : b.High, high ? b.Low : p, false); level.Dead = true;
            }
        }
        for (int i = _levels.Count - 1; i >= 0; i--)
            if (_levels[i].Dead) { _levels[i].Line.End(bar); _levels.RemoveAt(i); }

        var (swingHigh, swingLow) = StudySwings.Confirm(Bars, bar, SwingLength, SwingLength);
        if (swingHigh is { } h) AddLevel(h, bar);
        if (swingLow is { } l) AddLevel(l, bar);

        int signal = Math.Sign(_barWick + _barRetest);
        if (_barWick != 0 || _barRetest != 0) Scene.Tints[bar] = StudyPalette.A(_lastColor, 18);
        _signal.Set(bar, signal); _wick.Set(bar, Math.Sign(_barWick)); _retest.Set(bar, Math.Sign(_barRetest));
        Dashboard();
    }

    private void AddLevel(StudySwing swing, int bar)
    {
        var color = swing.High ? BearColor : BullColor;
        var line = Scene.Level(swing.Bar, bar, swing.Price, StudyPalette.A(color, 150), null, swing.High ? "Liquidity high" : "Liquidity low", style: LineDashStyle.Dot);
        _levels.Add(new SweepLevel(swing, line));
    }

    private void Sweep(int bar, bool bull, decimal top, decimal bottom, bool wick)
    {
        var b = Bars[bar]; var color = bull ? BullColor : BearColor;
        _zones.Add((Scene.Box(bar, bar, top, bottom, color, null, "Swept area"), bull));
        Scene.Tag(bar, bar, bull ? b.Low : b.High, wick ? "Wick" : "Retest", color, !bull);
        int dir = bull ? 1 : -1;
        if (wick) { _barWick += dir; if (bull) _bullWick++; else _bearWick++; }
        else { _barRetest += dir; if (bull) _bullRetest++; else _bearRetest++; }
        _last = (bull, wick) switch { (true, true) => "Bullish wick", (false, true) => "Bearish wick", (true, false) => "Bullish retest", _ => "Bearish retest" };
        _lastColor = color;
    }

    private void Dashboard()
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Liquidity sweeps";
        Scene.Row("Wick sweeps (bullish / bearish)", $"{_bullWick} / {_bearWick}");
        Scene.Row("Retest sweeps (bullish / bearish)", $"{_bullRetest} / {_bearRetest}");
        Scene.Row("Last sweep", _last, _lastColor);
        Scene.Row("Live liquidity levels", _levels.Count.ToString(CultureInfo.InvariantCulture));
    }
}

public enum SessionSweepStop
{
    [Display(Name = "Inverse FVG edge (ATR fallback)")] InverseFvg,
    [Display(Name = "ATR distance")] Atr,
}

[DisplayName("Session sweep with inverse FVG")]
[Category(IndicatorCategories.Structure)]
[Description("The range of one trading session in its own time zone (daylight saving followed). After the session, a bar that takes its high or low (no farther than an ATR multiple) is a sweep; a close back inside the range within the validity window makes it valid. A close through the trendline drawn from the session's opposite extreme then gives a hypothetical signal, with a stop at the widest inverse FVG since the sweep (or one ATR) and a target in ATR. Strict mode allows one valid sweep per session and cancels on a close outside the range. Heuristic study; signals are hypothetical, known at the signal bar's close, and never place orders.")]
public class SessionSweepInverseFvg : ClosedBarStudy
{
    private WorldSession _session = WorldSession.London;
    private LocalSessionWindow _window = LocalSessionWindow.Of(WorldSession.London);
    private int _sweepBars = 10, _atrLength = 14;
    private decimal _maxSweepAtr = 5, _targetAtr = 2;
    private SessionSweepStop _stopKind = SessionSweepStop.InverseFvg;
    private bool _strict, _showSessionBox = true, _showSweeps = true, _showSignals = true;
    private RunningAtr _atr = new(14);
    // session range
    private bool _seen, _prevIn, _prevClosedInside, _ranged;
    private DateOnly _date, _prevDate;
    private int _start = -1, _end = -1;
    private decimal _high, _low;
    private StudyLine? _highLine, _lowLine;
    private readonly List<StudyBox> _ranges = new();
    // sweep idea
    private int _sweepDir, _sweepBar = -1, _armed, _validThisSession, _lineA = -1, _lineB = -1;
    private bool _upDone, _downDone;
    // hypothetical signal
    private StudyLine? _stopLine, _targetLine;
    private bool _long;
    private int _valid, _invalid, _signals, _wins, _losses, _lastDir;
    private decimal _lastPrice;
    private readonly OptionalValueDataSeries _signal = SweepMath.Series("SessionSweepInverseFvg.Signal", "Hypothetical signal (+1 long, -1 short)");
    private readonly OptionalValueDataSeries _sweep = SweepMath.Series("SessionSweepInverseFvg.Sweep", "Valid session sweep (+1 low swept, -1 high swept)");
    private readonly OptionalValueDataSeries _stopSeries = SweepMath.Series("SessionSweepInverseFvg.Stop", "Hypothetical stop at the signal bar");
    private readonly OptionalValueDataSeries _targetSeries = SweepMath.Series("SessionSweepInverseFvg.Target", "Hypothetical target at the signal bar");

    public SessionSweepInverseFvg() { DataSeries[0] = _signal; DataSeries.Add(_sweep); DataSeries.Add(_stopSeries); DataSeries.Add(_targetSeries); }

    public override string Status => "Hypothetical signals, not orders; closed bars only";

    [Display(Name = "Session", GroupName = "Calculation", Order = 10)]
    public WorldSession Session { get => _session; set { _session = value; RecalculateValues(); } }
    [Display(Name = "Bars to return inside the range", GroupName = "Calculation", Order = 11), Range(1, 200)]
    public int SweepBars { get => _sweepBars; set { _sweepBars = Math.Clamp(value, 1, 200); RecalculateValues(); } }
    [Display(Name = "ATR length", GroupName = "Calculation", Order = 12), Range(1, 200)]
    public int AtrLength { get => _atrLength; set { _atrLength = Math.Clamp(value, 1, 200); RecalculateValues(); } }
    [Display(Name = "Maximum sweep (ATR)", GroupName = "Calculation", Order = 13), Range(0.1, 50)]
    public decimal MaxSweepAtr { get => _maxSweepAtr; set { _maxSweepAtr = Math.Clamp(value, 0.1m, 50); RecalculateValues(); } }
    [Display(Name = "Stop", GroupName = "Calculation", Order = 14)]
    public SessionSweepStop Stop { get => _stopKind; set { _stopKind = value; RecalculateValues(); } }
    [Display(Name = "Strict (one valid sweep per session)", GroupName = "Calculation", Order = 15)]
    public bool Strict { get => _strict; set { _strict = value; RecalculateValues(); } }
    [Display(Name = "Target (ATR)", GroupName = "Calculation", Order = 16), Range(0.1, 50)]
    public decimal TargetAtr { get => _targetAtr; set { _targetAtr = Math.Clamp(value, 0.1m, 50); RecalculateValues(); } }
    // display-only filters: every range, sweep mark and hypothetical signal drawing is always recorded (and alertable)
    [Display(Name = "Session box and range levels", GroupName = "Drawing", Order = 20, Description = SweepMath.DisplayOnly)]
    public bool ShowSessionBox { get => _showSessionBox; set { _showSessionBox = value; RedrawChart(); } }
    [Display(Name = "Sweeps", GroupName = "Drawing", Order = 21, Description = SweepMath.DisplayOnly)]
    public bool ShowSweeps { get => _showSweeps; set { _showSweeps = value; RedrawChart(); } }
    [Display(Name = "Hypothetical signals (trendline, inverse FVG, stop, target)", GroupName = "Drawing", Order = 22, Description = SweepMath.DisplayOnly)]
    public bool ShowSignals { get => _showSignals; set { _showSignals = value; RedrawChart(); } }
    [Display(Name = "Session", GroupName = "Colors", Order = 30)] public Color SessionColor { get; set; } = StudyPalette.Neutral;
    [Display(Name = "Bullish", GroupName = "Colors", Order = 31)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 32)] public Color BearColor { get; set; } = StudyPalette.Bear;
    [Display(Name = "Invalid sweep", GroupName = "Colors", Order = 33)] public Color FailColor { get; set; } = StudyPalette.Muted;

    /// <summary>Completed session ranges (source bar = first session bar, <c>To</c> = last session bar).</summary>
    [Browsable(false)] public IReadOnlyList<StudyBox> SessionRanges => _ranges;

    // range boxes have no text and only the range levels carry the session alert names; sweeps are circles and tags;
    // everything else (trendline, signal and exit marks, inverse FVG, stop and target) belongs to a hypothetical signal
    public override bool IsShown(StudyBox box) => box.Text == null ? ShowSessionBox : ShowSignals;
    public override bool IsShown(StudyLine line) => line.Alert is "Session high" or "Session low" ? ShowSessionBox : ShowSignals;
    public override bool IsShown(StudyMark mark) => mark.Kind == StudyMarkKind.Circle ? ShowSweeps : ShowSignals;
    public override bool IsShown(StudyTag tag) => ShowSweeps;

    protected override void ResetState()
    {
        _window = LocalSessionWindow.Of(_session); _atr = new(AtrLength);
        _seen = _prevIn = _prevClosedInside = _ranged = false; _date = _prevDate = default;
        _start = _end = -1; _high = _low = 0; _highLine = _lowLine = null; _ranges.Clear();
        _sweepDir = _armed = _validThisSession = 0; _sweepBar = _lineA = _lineB = -1; _upDone = _downDone = false;
        _stopLine = _targetLine = null; _long = false;
        _valid = _invalid = _signals = _wins = _losses = _lastDir = 0; _lastPrice = 0;
    }

    protected override void OnCoverageGap(int bar)
    {
        _highLine?.End(bar); _lowLine?.End(bar); _stopLine?.End(bar); _targetLine?.End(bar);
        var ranges = _ranges.ToList();
        ResetState();
        _ranges.AddRange(ranges);
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; decimal atr = _atr.Add(b);
        int signal = 0, sweep = 0; decimal? stop = null, target = null;
        CheckExit(bar, b);

        bool inside = _window.Contains(b.Time, out var date);
        if (inside && _seen && (!_prevIn || date != _prevDate))
        {
            // a new session starts (a session already running when the history begins is skipped: its range is incomplete)
            if (_start >= 0 && !_ranged) CloseRange(bar);
            _highLine?.End(bar); _lowLine?.End(bar); _highLine = _lowLine = null;
            _start = _end = bar; _date = date; _high = b.High; _low = b.Low; _ranged = false;
            _sweepDir = _armed = _validThisSession = 0; _upDone = _downDone = false; _lineA = _lineB = -1;
        }
        else if (inside && _start >= 0 && date == _date) { _high = Math.Max(_high, b.High); _low = Math.Min(_low, b.Low); _end = bar; }

        bool closedInside = _start >= 0 && b.Close <= _high && b.Close >= _low;
        if (!inside && _start >= 0)
        {
            if (!_ranged)
            {
                CloseRange(bar);
                _highLine = Scene.Level(_start, bar, _high, SessionColor, "Session high", "Session high", style: LineDashStyle.Dot);
                _lowLine = Scene.Level(_start, bar, _low, SessionColor, "Session low", "Session low", style: LineDashStyle.Dot);
            }
            bool limit = Strict && _validThisSession >= 1;
            if (_sweepDir == 0 && !limit && _prevClosedInside)
            {
                // a sweep takes the session extreme (by no more than the ATR cap) right after a close inside the range
                decimal cap = atr * MaxSweepAtr;
                if (!_upDone && b.High > _high && b.High - _high <= cap) { _sweepDir = -1; _sweepBar = bar; _upDone = true; }
                else if (!_downDone && b.Low < _low && _low - b.Low <= cap) { _sweepDir = 1; _sweepBar = bar; _downDone = true; }
                if (_sweepDir != 0)
                    Scene.Mark(bar, bar, _sweepDir < 0 ? b.High : b.Low, StudyMarkKind.Circle, StudyPalette.A(_sweepDir > 0 ? BullColor : BearColor, 160), null, _sweepDir < 0);
            }
            if (_sweepDir != 0 && _armed == 0)
            {
                if (bar - _sweepBar <= SweepBars && closedInside)
                {
                    _valid++; _validThisSession++; sweep = _sweepDir;
                    Scene.Tag(bar, bar, _sweepDir < 0 ? b.High : b.Low, "Valid sweep", _sweepDir > 0 ? BullColor : BearColor, _sweepDir < 0);
                    Arm(bar);
                }
                else if (bar - _sweepBar > SweepBars)
                {
                    _invalid++;
                    Scene.Tag(bar, bar, _sweepDir < 0 ? b.High : b.Low, "Invalid sweep", FailColor, _sweepDir < 0);
                    _sweepDir = 0;
                }
            }
            else if (_armed != 0)
            {
                if (Strict && !closedInside) _armed = _sweepDir = 0;
                else if (_lineA >= 0 && _lineB > _lineA)
                {
                    decimal pa = LinePrice(_lineA), pb = LinePrice(_lineB);
                    decimal line = pa + (pb - pa) * (bar - _lineA) / (_lineB - _lineA);
                    if ((_armed < 0 ? b.Close < line : b.Close > line) && closedInside)
                        (signal, stop, target) = Enter(bar, b, atr, pa, line);
                }
            }
        }

        _prevIn = inside; if (inside) _prevDate = date;
        _prevClosedInside = closedInside; _seen = true;
        _signal.Set(bar, signal); _sweep.Set(bar, sweep);
        if (stop is { } s) _stopSeries.Set(bar, s); else _stopSeries.SetUnavailable(bar);
        if (target is { } t) _targetSeries.Set(bar, t); else _targetSeries.SetUnavailable(bar);
        Dashboard(inside);
    }

    private decimal LinePrice(int k) => _armed < 0 ? Bars[k].Low : Bars[k].High;

    private void CloseRange(int bar)
    {
        var box = Scene.Box(_start, bar, _high, _low, SessionColor, fillAlpha: 26);
        box.To = _end; box.EndAvailable = bar;
        _ranges.Add(box); if (_ranges.Count > 60) _ranges.RemoveAt(0);
        _ranged = true;
    }

    /// <summary>
    /// Arms the idea at a valid sweep: after a high sweep, the rising support line from the session's lowest low (up to the
    /// sweep) whose slope keeps every later low above it; mirrored after a low sweep. Uses only bars up to <paramref name="bar"/>.
    /// </summary>
    private void Arm(int bar)
    {
        _armed = _sweepDir; bool sweptHigh = _armed < 0;
        _lineA = _lineB = -1;
        decimal anchor = sweptHigh ? decimal.MaxValue : decimal.MinValue;
        for (int k = _start; k <= _sweepBar; k++)
        {
            decimal v = LinePrice(k);
            if (sweptHigh ? v < anchor : v > anchor) { anchor = v; _lineA = k; }
        }
        decimal? best = null;
        for (int k = _lineA + 1; k <= bar; k++)
        {
            decimal v = LinePrice(k);
            if (sweptHigh ? v <= anchor : v >= anchor) continue;
            decimal slope = (v - anchor) / (k - _lineA);
            if (best is not { } current || (sweptHigh ? slope < current : slope > current)) { best = slope; _lineB = k; }
        }
        if (best is not { } final || (sweptHigh ? final <= 0 : final >= 0)) _lineB = -1;
    }

    private (int Signal, decimal Stop, decimal Target) Enter(int bar, StudyBar b, decimal atr, decimal lineStart, decimal lineNow)
    {
        bool isLong = _armed > 0;
        // the widest inverse FVG since the sweep: a bearish gap reclaimed for a long, a bullish gap lost for a short
        decimal width = 0, gapTop = 0, gapBottom = 0; int gapBar = -1;
        for (int k = Math.Max(_sweepBar, 2); k <= bar; k++)
        {
            if (SweepMath.Gap(Bars, k, 0) is not { } g || g.Bull == isLong || g.Top - g.Bottom <= width) continue;
            width = g.Top - g.Bottom; gapTop = g.Top; gapBottom = g.Bottom; gapBar = k - 1;
        }
        decimal stop = isLong ? b.Close - atr : b.Close + atr;
        if (Stop == SessionSweepStop.InverseFvg && gapBar >= 0)
        {
            decimal edge = isLong ? gapBottom : gapTop;
            if (isLong ? edge < b.Close : edge > b.Close) stop = edge; // an edge on the wrong side of the close falls back to ATR
        }
        decimal target = isLong ? b.Close + atr * TargetAtr : b.Close - atr * TargetAtr;
        var color = isLong ? BullColor : BearColor;
        Scene.Segment(_lineA, bar, bar, lineStart, lineNow, color, 1, LineDashStyle.Dash);
        Scene.Mark(bar, bar, isLong ? b.Low : b.High, isLong ? StudyMarkKind.TriangleUp : StudyMarkKind.TriangleDown, color, isLong ? "Hypothetical long" : "Hypothetical short", !isLong);
        if (gapBar >= 0) Scene.Box(gapBar, bar, gapTop, gapBottom, StudyPalette.Muted, "Inverse FVG").End(bar);
        _stopLine?.End(bar); _targetLine?.End(bar);
        _targetLine = Scene.Level(bar, bar, target, BullColor, "Hypothetical target", "Hypothetical target", style: LineDashStyle.Dash);
        _stopLine = Scene.Level(bar, bar, stop, BearColor, "Hypothetical stop", "Hypothetical stop", style: LineDashStyle.Dash);
        _long = isLong; _signals++; _lastDir = isLong ? 1 : -1; _lastPrice = b.Close;
        _armed = _sweepDir = 0;
        return (_lastDir, stop, target);
    }

    private void CheckExit(int bar, StudyBar b)
    {
        if (_stopLine is not { } stop || _targetLine is not { } target) return;
        bool hitStop = _long ? b.Low <= stop.Price : b.High >= stop.Price;
        bool hitTarget = _long ? b.High >= target.Price : b.Low <= target.Price;
        if (!hitStop && !hitTarget) return;
        // both inside one bar counts as the stop (conservative)
        if (hitStop) _losses++; else _wins++;
        Scene.Mark(bar, bar, _long ? b.High : b.Low, StudyMarkKind.Cross, hitStop ? BearColor : BullColor, hitStop ? "Stop reached" : "Target reached", _long);
        stop.End(bar); target.End(bar); _stopLine = _targetLine = null;
    }

    private void Dashboard(bool inside)
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Session sweep and inverse FVG";
        Scene.Row("Session", SweepMath.Name(_session), SessionColor);
        Scene.Row("In session", inside ? "Yes" : "No");
        Scene.Row("Valid / invalid sweeps", $"{_valid} / {_invalid}");
        Scene.Row("Hypothetical signals", _signals.ToString(CultureInfo.InvariantCulture));
        Scene.Row("Targets / stops", $"{_wins} / {_losses}");
        Scene.Row("Last signal", _lastDir > 0 ? "Long" : _lastDir < 0 ? "Short" : "None", _lastDir > 0 ? BullColor : _lastDir < 0 ? BearColor : default);
        if (_lastDir != 0) Scene.Row("Last signal price", Price(_lastPrice));
    }
}

public enum PriorDayEntryTiming
{
    [Display(Name = "When the FVG forms")] OnFormation,
    [Display(Name = "On a return to the FVG midpoint")] AtMidpoint,
}

[DisplayName("Prior-day sweep with FVG signals")]
[Category(IndicatorCategories.Structure)]
[Description("The high and low of each completed exchange-session day stay as levels until first touched. When price sweeps one, the first fair value gap in the signal's direction within the window gives a hypothetical signal, either when the gap forms or on a return to its midpoint, with stop and target in ATR, an optional EMA trend filter and running target/stop statistics. The first, possibly partial, day of the history gives no levels. Heuristic; signals are hypothetical, known at the signal bar's close, and never place orders.")]
public class PriorDayFvg : ClosedBarStudy
{
    private sealed class DayLevel(decimal price, bool high, StudyLine line)
    {
        public decimal Price { get; } = price;
        public bool High { get; } = high;
        public StudyLine Line { get; } = line;
        public bool Touched;
    }

    /// <summary>Unfilled FVGs tracked (and alertable) at most; older ones are retired. The display filters draw a subset.</summary>
    private const int TrackedFvgs = 50;

    private int _days = 10, _maxFvgs = 10, _fvgAtrLength = 14, _window = 30, _stopAtrLength = 14, _trendEma = 200;
    private decimal _fvgAtrMult = 0.25m, _stopAtr = 1.5m, _targetAtr = 3;
    private bool _showFvgs = true, _keepFilled, _sizeFilter = true, _showMid = true, _longs = true, _shorts = true, _invert, _trendFilter;
    private PriorDayEntryTiming _entry = PriorDayEntryTiming.AtMidpoint;
    private RunningAtr _fvgAtr = new(14), _tradeAtr = new(14);
    private RunningEma _ema = new(200);
    private readonly List<DayLevel> _levels = new();
    private readonly List<(StudyBox Box, StudyLine Mid, bool Bull)> _fvgs = new();
    // presentation only: source bars of the FVGs currently drawn, cached per (bars, boxes, MaxFvgs, KeepFilled)
    private readonly HashSet<int> _drawnFvgs = new();
    private (int Bars, int Boxes, int Max, bool Keep) _drawnKey = (-1, 0, 0, false);
    private readonly List<(decimal Mid, bool Long)> _queue = new();
    private List<(int Bar, decimal Price)>? _emaPoints;
    private bool _dayComplete;
    private int _dayStart = -1, _setupDir, _setupBar = -1, _pos, _wins, _losses;
    private decimal _dayHigh, _dayLow;
    private StudyLine? _stopLine, _targetLine;
    private readonly OptionalValueDataSeries _signal = SweepMath.Series("PriorDayFvg.Signal", "Hypothetical signal (+1 long, -1 short)");
    private readonly OptionalValueDataSeries _sweep = SweepMath.Series("PriorDayFvg.Sweep", "Prior-day level sweep (+1 low swept, -1 high swept, 2 both)");
    private readonly OptionalValueDataSeries _exit = SweepMath.Series("PriorDayFvg.Exit", "Hypothetical exit (+1 target, -1 stop)");
    private readonly OptionalValueDataSeries _stopSeries = SweepMath.Series("PriorDayFvg.Stop", "Hypothetical stop at the signal bar");
    private readonly OptionalValueDataSeries _targetSeries = SweepMath.Series("PriorDayFvg.Target", "Hypothetical target at the signal bar");

    public PriorDayFvg()
    {
        DataSeries[0] = _signal; DataSeries.Add(_sweep); DataSeries.Add(_exit); DataSeries.Add(_stopSeries); DataSeries.Add(_targetSeries);
    }

    public override string Status => "Hypothetical signals, not orders; closed bars only";

    [Display(Name = "Days kept", GroupName = "Levels", Order = 10), Range(1, 60)]
    public int DaysToKeep { get => _days; set { _days = Math.Clamp(value, 1, 60); RecalculateValues(); } }
    // display-only filters: every FVG and midpoint line is always recorded and alertable until filled; IsShown hides them
    [Display(Name = "Show the FVGs", GroupName = "FVG", Order = 20, Description = SweepMath.DisplayOnly)]
    public bool ShowFvgs { get => _showFvgs; set { _showFvgs = value; RedrawChart(); } }
    [Display(Name = "FVGs drawn", GroupName = "FVG", Order = 21, Description = SweepMath.DisplayOnly), Range(1, 50)]
    public int MaxFvgs { get => _maxFvgs; set { _maxFvgs = Math.Clamp(value, 1, 50); RedrawChart(); } }
    [Display(Name = "Keep filled FVGs", GroupName = "FVG", Order = 22, Description = SweepMath.DisplayOnly)]
    public bool KeepFilled { get => _keepFilled; set { _keepFilled = value; RedrawChart(); } }
    [Display(Name = "Size filter (ATR)", GroupName = "FVG", Order = 23)]
    public bool SizeFilter { get => _sizeFilter; set { _sizeFilter = value; RecalculateValues(); } }
    [Display(Name = "FVG ATR length", GroupName = "FVG", Order = 24), Range(1, 200)]
    public int FvgAtrLength { get => _fvgAtrLength; set { _fvgAtrLength = Math.Clamp(value, 1, 200); RecalculateValues(); } }
    [Display(Name = "Minimum size (ATR)", GroupName = "FVG", Order = 25), Range(0, 10)]
    public decimal FvgAtrMult { get => _fvgAtrMult; set { _fvgAtrMult = Math.Clamp(value, 0, 10); RecalculateValues(); } }
    [Display(Name = "Midpoint line", GroupName = "FVG", Order = 26, Description = SweepMath.DisplayOnly)]
    public bool ShowMid { get => _showMid; set { _showMid = value; RedrawChart(); } }
    [Display(Name = "Long signals", GroupName = "Signals", Order = 30)]
    public bool Longs { get => _longs; set { _longs = value; RecalculateValues(); } }
    [Display(Name = "Short signals", GroupName = "Signals", Order = 31)]
    public bool Shorts { get => _shorts; set { _shorts = value; RecalculateValues(); } }
    [Display(Name = "Invert the signals", GroupName = "Signals", Order = 32)]
    public bool Invert { get => _invert; set { _invert = value; RecalculateValues(); } }
    [Display(Name = "Signal timing", GroupName = "Signals", Order = 33)]
    public PriorDayEntryTiming Entry { get => _entry; set { _entry = value; RecalculateValues(); } }
    [Display(Name = "Bars after the sweep", GroupName = "Signals", Order = 34), Range(1, 500)]
    public int Window { get => _window; set { _window = Math.Clamp(value, 1, 500); RecalculateValues(); } }
    [Display(Name = "Stop ATR length", GroupName = "Signals", Order = 35), Range(1, 200)]
    public int StopAtrLength { get => _stopAtrLength; set { _stopAtrLength = Math.Clamp(value, 1, 200); RecalculateValues(); } }
    [Display(Name = "Stop (ATR)", GroupName = "Signals", Order = 36), Range(0.1, 20)]
    public decimal StopAtr { get => _stopAtr; set { _stopAtr = Math.Clamp(value, 0.1m, 20); RecalculateValues(); } }
    [Display(Name = "Target (ATR)", GroupName = "Signals", Order = 37), Range(0.1, 50)]
    public decimal TargetAtr { get => _targetAtr; set { _targetAtr = Math.Clamp(value, 0.1m, 50); RecalculateValues(); } }
    [Display(Name = "Trend filter (EMA)", GroupName = "Signals", Order = 38)]
    public bool TrendFilter { get => _trendFilter; set { _trendFilter = value; RecalculateValues(); } }
    [Display(Name = "Trend EMA length", GroupName = "Signals", Order = 39), Range(2, 1000)]
    public int TrendEma { get => _trendEma; set { _trendEma = Math.Clamp(value, 2, 1000); RecalculateValues(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 40)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 41)] public Color BearColor { get; set; } = StudyPalette.Bear;
    [Display(Name = "Prior-day levels", GroupName = "Colors", Order = 42)] public Color LevelColor { get; set; } = StudyPalette.Amber;

    /// <summary>
    /// FVG boxes are this study's only boxes: the newest <see cref="MaxFvgs"/> unfilled ones (with <see cref="KeepFilled"/>,
    /// the newest of all) are drawn. Midpoint lines (no alert, no label) follow their FVG.
    /// </summary>
    public override bool IsShown(StudyBox box) => ShowFvgs && DrawnFvgs().Contains(box.From);
    public override bool IsShown(StudyLine line) => line.Alert != null || ShowMid && ShowFvgs && DrawnFvgs().Contains(line.From);

    // one FVG completes per bar, so its source bar identifies it (and its midpoint line)
    private HashSet<int> DrawnFvgs()
    {
        var key = (Bars.Count, Scene.Boxes.Count, MaxFvgs, KeepFilled);
        if (key == _drawnKey) return _drawnFvgs;
        _drawnKey = key; _drawnFvgs.Clear();
        for (int i = Scene.Boxes.Count - 1; i >= 0 && _drawnFvgs.Count < MaxFvgs; i--)
            if (KeepFilled || Scene.Boxes[i].Live) _drawnFvgs.Add(Scene.Boxes[i].From);
        return _drawnFvgs;
    }

    protected override void ResetState()
    {
        _fvgAtr = new(FvgAtrLength); _tradeAtr = new(StopAtrLength); _ema = new(TrendEma);
        _levels.Clear(); _fvgs.Clear(); _queue.Clear(); _emaPoints = null; _drawnKey = (-1, 0, 0, false);
        _dayComplete = false; _dayStart = _setupBar = -1; _setupDir = _pos = _wins = _losses = 0; _dayHigh = _dayLow = 0;
        _stopLine = _targetLine = null;
    }

    protected override void OnCoverageGap(int bar)
    {
        foreach (var level in _levels) if (!level.Touched) level.Line.End(bar);
        foreach (var (box, mid, _) in _fvgs) { box.End(bar); mid.End(bar); }
        _stopLine?.End(bar); _targetLine?.End(bar);
        ResetState();
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar];
        decimal fvgAtr = _fvgAtr.Add(b), tradeAtr = _tradeAtr.Add(b), ema = _ema.Add(b.Close);
        int signal = 0, sweep = 0, exit = 0; decimal? stop = null, target = null;

        if (_dayStart < 0) { _dayStart = bar; _dayHigh = b.High; _dayLow = b.Low; }
        else if (b.SessionDate != Bars[bar - 1].SessionDate)
        {
            // the finished exchange-session day leaves its high and low (known from this bar's open)
            if (_dayComplete) { AddLevel(_dayHigh, true, bar); AddLevel(_dayLow, false, bar); }
            _dayComplete = true; _dayStart = bar; _dayHigh = b.High; _dayLow = b.Low;
            if (_pos == 0) { _setupDir = 0; _queue.Clear(); }
        }
        else { _dayHigh = Math.Max(_dayHigh, b.High); _dayLow = Math.Min(_dayLow, b.Low); }

        // a sweep is the first touch of an untouched prior-day level
        bool highSwept = false, lowSwept = false;
        foreach (var level in _levels)
        {
            if (level.Touched || (level.High ? b.High < level.Price : b.Low > level.Price)) continue;
            level.Touched = true; level.Line.End(bar);
            if (level.High) highSwept = true; else lowSwept = true;
        }
        if (highSwept || lowSwept)
        {
            _setupDir = highSwept && lowSwept ? 2 : lowSwept ? 1 : -1; _setupBar = bar; sweep = _setupDir;
            if (_pos == 0) _queue.Clear();
            Scene.Mark(bar, bar, lowSwept ? b.Low : b.High, StudyMarkKind.Circle, StudyPalette.A(LevelColor, 200), null, !lowSwept);
        }

        // an open hypothetical position ends at its stop or target (from the bar after the signal)
        if (_pos != 0 && _stopLine is { } stopLine && _targetLine is { } targetLine)
        {
            bool hitStop = _pos > 0 ? b.Low <= stopLine.Price : b.High >= stopLine.Price;
            bool hitTarget = _pos > 0 ? b.High >= targetLine.Price : b.Low <= targetLine.Price;
            if (hitStop || hitTarget)
            {
                // both inside one bar counts as the stop (conservative)
                if (hitStop) _losses++; else _wins++;
                exit = hitStop ? -1 : 1;
                Scene.Mark(bar, bar, _pos > 0 ? b.High : b.Low, StudyMarkKind.Cross, hitStop ? BearColor : BullColor, hitStop ? "Stop reached" : "Target reached", _pos > 0);
                stopLine.End(bar); targetLine.End(bar); _stopLine = _targetLine = null; _pos = 0;
            }
        }

        if (SweepMath.Gap(Bars, bar, SizeFilter ? fvgAtr * FvgAtrMult : 0) is { } gap)
        {
            var color = gap.Bull ? BullColor : BearColor; decimal mid = (gap.Top + gap.Bottom) / 2;
            var box = Scene.Box(bar - 1, bar, gap.Top, gap.Bottom, color, null, "Fair value gap", 35);
            var midLine = Scene.Level(bar - 1, bar, mid, StudyPalette.A(color, 150), style: LineDashStyle.Dot);
            _fvgs.Add((box, midLine, gap.Bull));
            while (_fvgs.Count > TrackedFvgs) { _fvgs[0].Box.End(bar); _fvgs[0].Mid.End(bar); _fvgs.RemoveAt(0); }
            bool inWindow = _setupDir != 0 && bar - _setupBar <= Window;
            bool longSignal = gap.Bull && inWindow && _setupDir is 1 or 2;
            bool shortSignal = !gap.Bull && inWindow && _setupDir is -1 or 2;
            if (TrendFilter) { longSignal &= _ema.Ready && b.Close > ema; shortSignal &= _ema.Ready && b.Close < ema; }
            if (Invert) (longSignal, shortSignal) = (shortSignal, longSignal);
            longSignal &= Longs; shortSignal &= Shorts;
            if (_pos == 0 && (longSignal || shortSignal))
            {
                if (Entry == PriorDayEntryTiming.OnFormation) (signal, stop, target) = Open(bar, b.Close, longSignal, tradeAtr);
                else _queue.Add((mid, longSignal));
            }
        }
        if (_pos == 0 && _queue.Count > 0)
        {
            int q = _queue.FindIndex(x => b.Low <= x.Mid && b.High >= x.Mid);
            if (q >= 0) { (signal, stop, target) = Open(bar, _queue[q].Mid, _queue[q].Long, tradeAtr); _queue.Clear(); }
        }

        // tracked FVGs end when filled (from the bar after the one that completes them)
        for (int i = _fvgs.Count - 1; i >= 0; i--)
        {
            var (box, midLine, bull) = _fvgs[i];
            if (box.Available >= bar || (bull ? b.Low > box.Bottom : b.High < box.Top)) continue;
            box.End(bar); midLine.End(bar); _fvgs.RemoveAt(i);
        }

        if (TrendFilter && _ema.Ready)
        {
            if (_emaPoints == null || _emaPoints.Count >= 500)
            {
                var points = new List<(int Bar, decimal Price)>();
                if (_emaPoints is { Count: > 0 }) points.Add(_emaPoints[^1]);
                _emaPoints = points; Scene.Paths.Add(new StudyPath(bar, points, StudyPalette.Muted));
            }
            _emaPoints.Add((bar, ema));
        }

        _signal.Set(bar, signal); _sweep.Set(bar, sweep); _exit.Set(bar, exit);
        if (stop is { } s) _stopSeries.Set(bar, s); else _stopSeries.SetUnavailable(bar);
        if (target is { } t) _targetSeries.Set(bar, t); else _targetSeries.SetUnavailable(bar);
        Dashboard();
    }

    private void AddLevel(decimal price, bool high, int bar)
    {
        string name = high ? "Prior-day high" : "Prior-day low";
        _levels.Add(new DayLevel(price, high, Scene.Level(_dayStart, bar, price, LevelColor, name, name)));
        while (_levels.Count > DaysToKeep * 2)
        {
            if (!_levels[0].Touched) _levels[0].Line.End(bar); // retired untouched
            _levels.RemoveAt(0);
        }
    }

    private (int Signal, decimal Stop, decimal Target) Open(int bar, decimal price, bool isLong, decimal atr)
    {
        var b = Bars[bar];
        _pos = isLong ? 1 : -1;
        decimal stop = isLong ? price - atr * StopAtr : price + atr * StopAtr;
        decimal target = isLong ? price + atr * TargetAtr : price - atr * TargetAtr;
        Scene.Mark(bar, bar, isLong ? b.Low : b.High, isLong ? StudyMarkKind.TriangleUp : StudyMarkKind.TriangleDown, isLong ? BullColor : BearColor, isLong ? "Hypothetical long" : "Hypothetical short", !isLong);
        _stopLine = Scene.Level(bar, bar, stop, BearColor, "Hypothetical stop", "Hypothetical stop", style: LineDashStyle.Dash);
        _targetLine = Scene.Level(bar, bar, target, BullColor, "Hypothetical target", "Hypothetical target", style: LineDashStyle.Dash);
        _setupDir = 0;
        return (_pos, stop, target);
    }

    private void Dashboard()
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Prior-day sweep FVG (hypothetical)";
        Scene.Row("Hypothetical position", _pos > 0 ? "Long" : _pos < 0 ? "Short" : "Flat", _pos > 0 ? BullColor : _pos < 0 ? BearColor : default);
        Scene.Row("Targets / stops", $"{_wins} / {_losses}");
        int total = _wins + _losses;
        Scene.Row("Win rate", total == 0 ? "–" : (100m * _wins / total).ToString("0", CultureInfo.InvariantCulture) + "%");
        Scene.Row("Untouched prior-day levels", _levels.Count(l => !l.Touched).ToString(CultureInfo.InvariantCulture), LevelColor);
    }
}

[DisplayName("Equal highs and lows with breakout FVG")]
[Category(IndicatorCategories.Structure)]
[Description("Liquidity zones where two confirmed swing highs (or lows) are nearly equal, known from the close that confirms the second swing. A close through the zone by a bar whose body is at least an ATR fraction is a breakout candidate; it is confirmed at the next close only if the three bars leave a fair value gap, which is then projected until filled or for a set number of bars. Zones also end on a close through them without a large body, or by age. Heuristic.")]
public class EqualHighsLowsFvg : ClosedBarStudy
{
    private int _left = 10, _right = 2, _maxZones = 60, _maxApart = 30, _maxAge = 30, _atrLength = 14, _projection = 5;
    private decimal _thresholdPct = 0.03m, _bodyAtr = 0.5m, _fvgAtr = 0.1m;
    private bool _showVolume = true, _showBreakouts = true, _showFvgs = true;
    private RunningAtr _atr = new(14);
    private readonly List<StudySwing> _highs = new(), _lows = new();
    private readonly List<(StudyBox Box, bool High)> _zones = new();
    private readonly List<(StudyBox Box, bool Bull)> _gaps = new();
    private int _pendingBar = -1, _pendingDir, _bullBreaks, _bearBreaks;
    private readonly OptionalValueDataSeries _signal = SweepMath.Series("EqualHighsLowsFvg.Signal", "Breakout with FVG (+1 up through equal highs, -1 down through equal lows)");
    private readonly OptionalValueDataSeries _zone = SweepMath.Series("EqualHighsLowsFvg.Zone", "New zone (+1 equal highs, -1 equal lows, 2 both)");

    public EqualHighsLowsFvg() { DataSeries[0] = _signal; DataSeries.Add(_zone); }

    [Display(Name = "Bars to the left", GroupName = "Calculation", Order = 10), Range(1, 100)]
    public int Left { get => _left; set { _left = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Bars to the right", GroupName = "Calculation", Order = 11), Range(1, 50)]
    public int Right { get => _right; set { _right = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    [Display(Name = "Maximum difference (%)", GroupName = "Calculation", Order = 12), Range(0.001, 5)]
    public decimal ThresholdPct { get => _thresholdPct; set { _thresholdPct = Math.Clamp(value, 0.001m, 5); RecalculateValues(); } }
    [Display(Name = "Live zones at most", GroupName = "Calculation", Order = 13), Range(1, 200)]
    public int MaxZones { get => _maxZones; set { _maxZones = Math.Clamp(value, 1, 200); RecalculateValues(); } }
    [Display(Name = "Maximum bars between the swings", GroupName = "Calculation", Order = 14), Range(2, 500)]
    public int MaxApart { get => _maxApart; set { _maxApart = Math.Clamp(value, 2, 500); RecalculateValues(); } }
    [Display(Name = "Maximum zone age (bars)", GroupName = "Calculation", Order = 15), Range(1, 1000)]
    public int MaxAge { get => _maxAge; set { _maxAge = Math.Clamp(value, 1, 1000); RecalculateValues(); } }
    [Display(Name = "Volume in the zone", GroupName = "Drawing", Order = 20)]
    public bool ShowVolume { get => _showVolume; set { _showVolume = value; RecalculateValues(); } }
    // display-only filters: breakout marks and gaps are always recorded (gaps stay alertable until filled or projected out)
    [Display(Name = "Breakout marks", GroupName = "Breakout", Order = 30, Description = SweepMath.DisplayOnly)]
    public bool ShowBreakouts { get => _showBreakouts; set { _showBreakouts = value; RedrawChart(); } }
    [Display(Name = "ATR length", GroupName = "Breakout", Order = 31), Range(1, 200)]
    public int AtrLength { get => _atrLength; set { _atrLength = Math.Clamp(value, 1, 200); RecalculateValues(); } }
    [Display(Name = "Minimum body (ATR)", GroupName = "Breakout", Order = 32), Range(0, 10)]
    public decimal BodyAtr { get => _bodyAtr; set { _bodyAtr = Math.Clamp(value, 0, 10); RecalculateValues(); } }
    [Display(Name = "Minimum FVG (ATR)", GroupName = "Breakout", Order = 33), Range(0, 10)]
    public decimal FvgAtr { get => _fvgAtr; set { _fvgAtr = Math.Clamp(value, 0, 10); RecalculateValues(); } }
    [Display(Name = "Breakout FVG", GroupName = "Breakout", Order = 34, Description = SweepMath.DisplayOnly)]
    public bool ShowFvgs { get => _showFvgs; set { _showFvgs = value; RedrawChart(); } }
    [Display(Name = "FVG projection (bars)", GroupName = "Breakout", Order = 35), Range(1, 200)]
    public int FvgProjection { get => _projection; set { _projection = Math.Clamp(value, 1, 200); RecalculateValues(); } }
    [Display(Name = "Equal highs", GroupName = "Colors", Order = 40)] public Color EqualHighsColor { get; set; } = StudyPalette.Bear;
    [Display(Name = "Equal lows", GroupName = "Colors", Order = 41)] public Color EqualLowsColor { get; set; } = StudyPalette.Bull;

    // breakout marks are this study's only marks; breakout gaps are the boxes alerted as "Breakout gap"
    public override bool IsShown(StudyMark mark) => ShowBreakouts;
    public override bool IsShown(StudyBox box) => box.Alert != "Breakout gap" || ShowFvgs;

    protected override void ResetState()
    {
        _atr = new(AtrLength); _highs.Clear(); _lows.Clear(); _zones.Clear(); _gaps.Clear();
        _pendingBar = -1; _pendingDir = _bullBreaks = _bearBreaks = 0;
    }

    protected override void OnCoverageGap(int bar)
    {
        foreach (var (box, _) in _zones) box.End(bar);
        foreach (var (box, _) in _gaps) box.End(bar);
        ResetState();
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; decimal atr = _atr.Add(b);
        int signal = 0, zone = 0;

        // a breakout candidate from the previous close is confirmed only if the three bars leave a gap its way
        if (_pendingDir != 0 && _pendingBar == bar - 1 && SweepMath.Gap(Bars, bar, atr * FvgAtr) is { } gap && gap.Bull == _pendingDir > 0)
        {
            bool up = _pendingDir > 0; var color = up ? EqualLowsColor : EqualHighsColor;
            signal = _pendingDir; if (up) _bullBreaks++; else _bearBreaks++;
            Scene.Mark(bar, bar, up ? b.Low : b.High, up ? StudyMarkKind.TriangleUp : StudyMarkKind.TriangleDown, color, "Breakout", !up);
            _gaps.Add((Scene.Box(bar - 1, bar, gap.Top, gap.Bottom, color, "FVG", "Breakout gap", 45), up));
        }
        _pendingDir = 0;

        for (int i = _gaps.Count - 1; i >= 0; i--)
        {
            var (box, bull) = _gaps[i];
            if (box.Available >= bar) continue;
            bool filled = bull ? b.Low <= box.Bottom : b.High >= box.Top;
            if (!filled && bar - box.Available < FvgProjection) continue;
            box.End(bar); _gaps.RemoveAt(i);
        }

        // live zones: a close through ends them (a large body makes it a breakout candidate), or age does
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var (box, high) = _zones[i];
            if (box.Available >= bar) continue;
            if (high ? b.Close > box.Top : b.Close < box.Bottom)
            {
                box.End(bar); _zones.RemoveAt(i);
                if (b.Body >= atr * BodyAtr) { _pendingBar = bar; _pendingDir = high ? 1 : -1; }
            }
            else if (bar - box.Available > MaxAge) { box.End(bar); _zones.RemoveAt(i); }
        }

        var (swingHigh, swingLow) = StudySwings.Confirm(Bars, bar, Left, Right);
        if (swingHigh is { } h && Pair(h, _highs, bar, atr)) zone += 1;
        if (swingLow is { } l && Pair(l, _lows, bar, atr)) zone = zone == 0 ? -1 : 2;

        _signal.Set(bar, signal); _zone.Set(bar, zone);
        Dashboard();
    }

    /// <summary>Pairs a newly confirmed swing with the latest earlier confirmed swing of its side within tolerance and distance.</summary>
    private bool Pair(StudySwing swing, List<StudySwing> earlier, int bar, decimal atr)
    {
        bool made = false;
        for (int i = earlier.Count - 1; i >= 0; i--)
        {
            var q = earlier[i];
            if (swing.Bar - q.Bar > MaxApart) break;
            if (q.Price <= 0 || Math.Abs(swing.Price - q.Price) / q.Price * 100 > ThresholdPct) continue;
            decimal volume = 0;
            for (int k = q.Bar; k <= swing.Bar; k++) volume += Bars[k].Volume;
            decimal top = Math.Max(swing.Price, q.Price), bottom = Math.Min(swing.Price, q.Price);
            if (top == bottom) top += atr * 0.05m;
            var color = swing.High ? EqualHighsColor : EqualLowsColor;
            string text = (swing.High ? "EQH" : "EQL") + (ShowVolume ? " " + SweepMath.Volume(volume) : "");
            _zones.Add((Scene.Box(q.Bar, bar, top, bottom, color, text, swing.High ? "Equal highs" : "Equal lows"), swing.High));
            // only the newest MaxZones live zones stay active
            int live = _zones.Count;
            for (int z = 0; z < _zones.Count && live > MaxZones; z++) { _zones[z].Box.End(bar); live--; }
            _zones.RemoveAll(x => !x.Box.Live);
            made = true; break;
        }
        earlier.Add(swing); if (earlier.Count > 50) earlier.RemoveAt(0);
        return made;
    }

    private void Dashboard()
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Equal highs and lows";
        Scene.Row("Live equal-high zones", _zones.Count(z => z.High).ToString(CultureInfo.InvariantCulture), EqualHighsColor);
        Scene.Row("Live equal-low zones", _zones.Count(z => !z.High).ToString(CultureInfo.InvariantCulture), EqualLowsColor);
        Scene.Row("Breakouts (bullish / bearish)", $"{_bullBreaks} / {_bearBreaks}");
    }
}

[DisplayName("Gap fill breakouts")]
[Category(IndicatorCategories.Structure)]
[Description("Fair value gaps at least an ATR multiple wide, redrawn smaller as price fills them and removed once filled. Each live gap takes a structural level: the first confirmed candle-body swing on its far side (for a bullish gap, a body low above the unfilled part). The close that cuts through that level is the breakout: breaking a bullish gap's level is bearish, and the reverse. Heuristic; levels exist from the close that confirms the body swing.")]
public class GapFillBreakouts : ClosedBarStudy
{
    private sealed class FillGap
    {
        public int Bar;
        public decimal Top, Bottom, Remain, LevelPrice;
        public bool Bull, Used, HasLevel, LevelDone;
        public int LevelBar = -1;
        public StudyBox? Box;
        public StudyLine? Level;
    }

    private int _pivot = 5, _maxBack = 200, _maxGaps = 5;
    private decimal _atrMult = 1;
    private bool _showGaps = true, _showLevels = true, _showLabels = true;
    private RunningAtr _atr = new(14);
    private readonly List<FillGap> _book = new();
    private int _bullBreaks, _bearBreaks;
    private readonly OptionalValueDataSeries _signal = SweepMath.Series("GapFillBreakouts.Signal", "Gap level break (+1 bullish, -1 bearish)");

    public GapFillBreakouts() { DataSeries[0] = _signal; }

    [Display(Name = "Minimum size (ATR)", GroupName = "Calculation", Order = 10), Range(0, 10)]
    public decimal AtrMult { get => _atrMult; set { _atrMult = Math.Clamp(value, 0, 10); RecalculateValues(); } }
    [Display(Name = "Body swing length", GroupName = "Calculation", Order = 11), Range(1, 50)]
    public int PivotLength { get => _pivot; set { _pivot = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    [Display(Name = "Maximum gap age for a break (bars)", GroupName = "Calculation", Order = 12), Range(10, 2000)]
    public int MaxBarsBack { get => _maxBack; set { _maxBack = Math.Clamp(value, 10, 2000); RecalculateValues(); } }
    [Display(Name = "Live gaps at most", GroupName = "Calculation", Order = 13), Range(1, 50)]
    public int MaxGaps { get => _maxGaps; set { _maxGaps = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    // display-only filters: every gap, level and break label is always recorded (and alertable); IsShown hides them
    [Display(Name = "Gaps", GroupName = "Drawing", Order = 20, Description = SweepMath.DisplayOnly)]
    public bool ShowGaps { get => _showGaps; set { _showGaps = value; RedrawChart(); } }
    [Display(Name = "Levels", GroupName = "Drawing", Order = 21, Description = SweepMath.DisplayOnly)]
    public bool ShowLevels { get => _showLevels; set { _showLevels = value; RedrawChart(); } }
    [Display(Name = "Break labels", GroupName = "Drawing", Order = 22, Description = SweepMath.DisplayOnly)]
    public bool ShowLabels { get => _showLabels; set { _showLabels = value; RedrawChart(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 30)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 31)] public Color BearColor { get; set; } = StudyPalette.Bear;

    // the only boxes, lines and tags of this study are gaps, structure levels and break labels
    public override bool IsShown(StudyBox box) => ShowGaps;
    public override bool IsShown(StudyLine line) => ShowLevels;
    public override bool IsShown(StudyTag tag) => ShowLabels;

    protected override void ResetState() { _atr = new(14); _book.Clear(); _bullBreaks = _bearBreaks = 0; }

    protected override void OnCoverageGap(int bar)
    {
        foreach (var gap in _book) Retire(gap, bar);
        ResetState();
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; decimal atr = _atr.Add(b); int signal = 0;
        if (SweepMath.Gap(Bars, bar, atr * AtrMult) is { } g)
        {
            var gap = new FillGap { Bar = bar - 1, Top = g.Top, Bottom = g.Bottom, Remain = g.Bull ? g.Top : g.Bottom, Bull = g.Bull };
            gap.Box = Draw(gap, bar - 1, bar);
            _book.Add(gap);
            if (_book.Count > MaxGaps) { Retire(_book[0], bar); _book.RemoveAt(0); }
        }

        var (bodyHigh, bodyLow, pivotBar) = BodySwing(bar);
        // newest to oldest: fill, take a structural level, break it
        for (int k = _book.Count - 1; k >= 0; k--)
        {
            var gap = _book[k];
            decimal before = gap.Remain;
            if (gap.Bull ? b.Low < gap.Remain : b.High > gap.Remain)
                gap.Remain = gap.Bull ? Math.Max(gap.Bottom, b.Low) : Math.Min(gap.Top, b.High);
            if (gap.Bull ? gap.Remain <= gap.Bottom : gap.Remain >= gap.Top) { Retire(gap, bar); _book.RemoveAt(k); continue; }
            if (gap.Remain != before) { gap.Box?.End(bar); gap.Box = Draw(gap, bar, bar); }

            decimal? candidate = gap.Bull ? bodyLow : bodyHigh;
            if (!gap.HasLevel && !gap.Used && candidate is { } level && pivotBar > gap.Bar && (gap.Bull ? level > gap.Remain : level < gap.Remain))
            {
                gap.HasLevel = true; gap.LevelPrice = level; gap.LevelBar = pivotBar;
                gap.Level = Scene.Level(pivotBar, bar, level, StudyPalette.A(gap.Bull ? BullColor : BearColor, 200), null, "Gap structure level", style: LineDashStyle.Dash);
            }
            if (gap.HasLevel && !gap.LevelDone && bar > gap.LevelBar + PivotLength && (gap.Bull ? b.Close < gap.LevelPrice : b.Close > gap.LevelPrice))
            {
                gap.LevelDone = gap.Used = true; gap.Level?.End(bar);
                if (bar - gap.Bar <= MaxBarsBack)
                {
                    // a bullish gap leaves support above it: cutting it is bearish (and the reverse)
                    bool bearish = gap.Bull; signal += bearish ? -1 : 1;
                    if (bearish) _bearBreaks++; else _bullBreaks++;
                    Scene.Tag(bar, bar, bearish ? b.High : b.Low, bearish ? "Bearish break" : "Bullish break", bearish ? BearColor : BullColor, bearish);
                }
            }
            if (bar - gap.Bar > MaxBarsBack && gap.HasLevel && !gap.LevelDone) { gap.LevelDone = true; gap.Level?.End(bar); }
        }

        _signal.Set(bar, Math.Sign(signal));
        Scene.Dash.Clear(); Scene.DashTitle = "Gap fill breakouts";
        Scene.Row("Live gaps", _book.Count.ToString(CultureInfo.InvariantCulture));
        Scene.Row("Breaks (bullish / bearish)", $"{_bullBreaks} / {_bearBreaks}");
    }

    private StudyBox Draw(FillGap gap, int from, int available) =>
        Scene.Box(from, available, gap.Bull ? gap.Remain : gap.Top, gap.Bull ? gap.Bottom : gap.Remain, gap.Bull ? BullColor : BearColor, "FVG", "Unfilled gap");

    private static void Retire(FillGap gap, int bar)
    {
        gap.Box?.End(bar);
        if (!gap.LevelDone) { gap.LevelDone = true; gap.Level?.End(bar); }
    }

    /// <summary>The candle-body swing (max/min of open and close) confirmed at <paramref name="bar"/>, <see cref="PivotLength"/> bars back.</summary>
    private (decimal? High, decimal? Low, int Bar) BodySwing(int bar)
    {
        int p = bar - PivotLength;
        if (p - PivotLength < 0) return (null, null, -1);
        decimal ph = Math.Max(Bars[p].Open, Bars[p].Close), pl = Math.Min(Bars[p].Open, Bars[p].Close);
        bool isHigh = true, isLow = true;
        for (int k = p - PivotLength; k <= bar && (isHigh || isLow); k++)
        {
            if (!Bars[k].Causal) return (null, null, -1);
            if (k == p) continue;
            decimal hk = Math.Max(Bars[k].Open, Bars[k].Close), lk = Math.Min(Bars[k].Open, Bars[k].Close);
            if (k < p ? hk > ph : hk >= ph) isHigh = false;
            if (k < p ? lk < pl : lk <= pl) isLow = false;
        }
        return (isHigh ? ph : (decimal?)null, isLow ? pl : (decimal?)null, p);
    }
}

public enum BreakoutZoneSides
{
    [Display(Name = "Bullish and bearish")] Both,
    [Display(Name = "Bullish only")] Bullish,
    [Display(Name = "Bearish only")] Bearish,
}

[DisplayName("Breakout tests and retests")]
[Category(IndicatorCategories.Structure)]
[Description("Each confirmed swing low starts a support zone and each confirmed swing high a resistance zone, an ATR(200) multiple wide, from the confirming close. Marks a test (opens inside and closes away on the zone's side), the breakout (a close through it), retests of the broken zone (opens inside and closes on the broken side) and zones that expire untested. Only opens and closes trigger events; wicks do not. Heuristic.")]
public class BreakoutTestsRetests : ClosedBarStudy
{
    private sealed class Zone(bool bull)
    {
        public bool Bull { get; } = bull;
        public int State, Since, Tests, Retests;
        public decimal Top, Bottom;
        public StudyBox? Box;
        // presentation only (never feeds detection or series): MinBars spacing and the zone's latest test / retest mark
        public int LastSpaced = int.MinValue / 2;
        public StudyMark? LatestTest, LatestRetest;
    }

    /// <summary>Presentation-only facts of one test or retest mark.</summary>
    private sealed class GlyphInfo(Zone zone, bool retest, bool spaced)
    {
        public Zone Zone { get; } = zone;
        public bool Retest { get; } = retest;
        public bool Spaced { get; } = spaced;
    }

    private BreakoutZoneSides _sides = BreakoutZoneSides.Both;
    private decimal _width = 1;
    private int _maxBars = 500, _minBars, _left = 10, _right = 1;
    private bool _onlyLast, _showZones = true, _showGlyphs = true, _showBackground = true;
    // keyed by mark identity; entries go with the marks the scene drops
    private readonly ConditionalWeakTable<StudyMark, GlyphInfo> _glyphs = new();
    private RunningAtr _atr = new(200);
    private Zone _bull = new(true), _bear = new(false);
    private int _breaksUp, _breaksDown;
    private readonly OptionalValueDataSeries _signal = SweepMath.Series("BreakoutTestsRetests.Signal", "Breakout (+1 up through resistance, -1 down through support)");
    private readonly OptionalValueDataSeries _test = SweepMath.Series("BreakoutTestsRetests.Test", "Zone test (+1 support held, -1 resistance held)");
    private readonly OptionalValueDataSeries _retest = SweepMath.Series("BreakoutTestsRetests.Retest", "Broken-zone retest (+1 broken resistance held, -1 broken support held)");

    public BreakoutTestsRetests() { DataSeries[0] = _signal; DataSeries.Add(_test); DataSeries.Add(_retest); }

    [Display(Name = "Zones", GroupName = "Calculation", Order = 10)]
    public BreakoutZoneSides Sides { get => _sides; set { _sides = value; RecalculateValues(); } }
    [Display(Name = "Width (× ATR 200)", GroupName = "Calculation", Order = 11), Range(0.05, 10)]
    public decimal Width { get => _width; set { _width = Math.Clamp(value, 0.05m, 10); RecalculateValues(); } }
    [Display(Name = "Zone lifetime (bars)", GroupName = "Calculation", Order = 12), Range(10, 5000)]
    public int MaxBars { get => _maxBars; set { _maxBars = Math.Clamp(value, 10, 5000); RecalculateValues(); } }
    // display-only filters: every zone, test/retest mark and tint is always recorded (and zones alertable); IsShown hides them
    [Display(Name = "Only the latest test and retest", GroupName = "Calculation", Order = 13, Description = SweepMath.DisplayOnly)]
    public bool OnlyLast { get => _onlyLast; set { _onlyLast = value; RedrawChart(); } }
    /// <summary>Display only; changing it rebuilds the (identical) scene to re-space the drawn marks.</summary>
    [Display(Name = "Minimum bars between marks", GroupName = "Calculation", Order = 14, Description = SweepMath.DisplayOnly), Range(0, 100)]
    public int MinBars { get => _minBars; set { _minBars = Math.Clamp(value, 0, 100); RecalculateValues(); } }
    [Display(Name = "Bars to the left", GroupName = "Calculation", Order = 15), Range(1, 100)]
    public int Left { get => _left; set { _left = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Bars to the right", GroupName = "Calculation", Order = 16), Range(1, 50)]
    public int Right { get => _right; set { _right = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    [Display(Name = "Zones", GroupName = "Drawing", Order = 20, Description = SweepMath.DisplayOnly)]
    public bool ShowZones { get => _showZones; set { _showZones = value; RedrawChart(); } }
    [Display(Name = "Test and retest marks", GroupName = "Drawing", Order = 21, Description = SweepMath.DisplayOnly)]
    public bool ShowGlyphs { get => _showGlyphs; set { _showGlyphs = value; RedrawChart(); } }
    [Display(Name = "Background on the breakout", GroupName = "Drawing", Order = 22, Description = SweepMath.DisplayOnly)]
    public bool ShowBreakoutBackground { get => _showBackground; set { _showBackground = value; RedrawChart(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 30)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 31)] public Color BearColor { get; set; } = StudyPalette.Bear;

    // zones are this study's only boxes and breakout tints its only tints
    public override bool IsShown(StudyBox box) => ShowZones;
    public override bool IsTintShown(int bar, Color color) => ShowBreakoutBackground;

    /// <summary>
    /// Test and retest marks: hidden with <see cref="ShowGlyphs"/> off; with <see cref="OnlyLast"/> only each zone's latest
    /// test and latest retest are drawn, otherwise only marks at least <see cref="MinBars"/> after the previous drawn one.
    /// </summary>
    public override bool IsShown(StudyMark mark)
    {
        if (!_glyphs.TryGetValue(mark, out var glyph)) return true;
        if (!ShowGlyphs) return false;
        return OnlyLast ? ReferenceEquals(mark, glyph.Retest ? glyph.Zone.LatestRetest : glyph.Zone.LatestTest) : glyph.Spaced;
    }

    private bool UseBull => Sides != BreakoutZoneSides.Bearish;
    private bool UseBear => Sides != BreakoutZoneSides.Bullish;

    protected override void ResetState() { _atr = new(200); _bull = new(true); _bear = new(false); _breaksUp = _breaksDown = 0; }

    protected override void OnCoverageGap(int bar) { Close(_bull, bar); Close(_bear, bar); ResetState(); }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; decimal width = _atr.Add(b) * Width;
        int signal = 0, test = 0, retest = 0;
        var (swingHigh, swingLow) = StudySwings.Confirm(Bars, bar, Left, Right);
        if (swingLow is { } low && UseBull) _bull = Open(_bull, low, bar, low.Price + width, low.Price);
        if (swingHigh is { } high && UseBear) _bear = Open(_bear, high, bar, high.Price, high.Price - width);
        if (UseBull) Step(_bull, bar, b, ref signal, ref test, ref retest);
        if (UseBear) Step(_bear, bar, b, ref signal, ref test, ref retest);
        _signal.Set(bar, Math.Sign(signal)); _test.Set(bar, Math.Sign(test)); _retest.Set(bar, Math.Sign(retest));

        Scene.Dash.Clear(); Scene.DashTitle = "Breakout tests and retests";
        if (UseBull) Scene.Row("Support zone", State(_bull), BullColor);
        if (UseBull) Scene.Row("Support tests / retests", $"{_bull.Tests} / {_bull.Retests}", BullColor);
        if (UseBear) Scene.Row("Resistance zone", State(_bear), BearColor);
        if (UseBear) Scene.Row("Resistance tests / retests", $"{_bear.Tests} / {_bear.Retests}", BearColor);
        Scene.Row("Breakouts (up / down)", $"{_breaksUp} / {_breaksDown}");
    }

    private static string State(Zone zone) => zone.State switch { 1 => "Live", 2 => "Broken", _ => "None" };

    private Zone Open(Zone previous, StudySwing swing, int bar, decimal top, decimal bottom)
    {
        Close(previous, bar);
        var zone = new Zone(previous.Bull) { State = 1, Since = bar, Top = top, Bottom = bottom };
        var color = zone.Bull ? BullColor : BearColor;
        zone.Box = Scene.Box(swing.Bar, bar, top, bottom, color, null, zone.Bull ? "Support zone" : "Resistance zone");
        Scene.Mark(swing.Bar, bar, swing.Price, StudyMarkKind.Circle, StudyPalette.A(color, 160), null, !zone.Bull);
        return zone;
    }

    private void Step(Zone zone, int bar, StudyBar b, ref int signal, ref int test, ref int retest)
    {
        if (zone.State == 0) return;
        bool bull = zone.Bull, openInside = b.Open >= zone.Bottom && b.Open <= zone.Top;
        Color own = bull ? BullColor : BearColor, flipped = bull ? BearColor : BullColor;
        if (zone.State == 1)
        {
            if (bull ? b.Close < zone.Bottom : b.Close > zone.Top)
            {
                zone.State = 2; zone.Since = bar; signal += bull ? -1 : 1;
                if (bull) _breaksDown++; else _breaksUp++;
                Scene.Tints[bar] = StudyPalette.A(flipped, 30);
                Scene.Mark(bar, bar, bull ? b.Low : b.High, bull ? StudyMarkKind.TriangleDown : StudyMarkKind.TriangleUp, flipped, null, !bull);
                zone.Box?.End(bar);
                zone.Box = Scene.Box(bar, bar, zone.Top, zone.Bottom, flipped, "Broken", bull ? "Broken support zone" : "Broken resistance zone");
            }
            else if (openInside && (bull ? b.Close > zone.Top : b.Close < zone.Bottom))
            {
                zone.Tests++; test += bull ? 1 : -1;
                Glyph(zone, bar, "Test", bull ? b.Low : b.High, own, !bull, false);
            }
            else if (bar - zone.Since >= MaxBars)
            {
                if (zone.Tests == 0) Scene.Mark(bar, bar, bull ? b.Low : b.High, StudyMarkKind.Cross, StudyPalette.A(own, 150), null, !bull);
                Close(zone, bar);
            }
        }
        else if (openInside && (bull ? b.Close < zone.Bottom : b.Close > zone.Top))
        {
            zone.Retests++; retest += bull ? -1 : 1;
            Glyph(zone, bar, "Retest", bull ? b.High : b.Low, flipped, bull, true);
        }
        else if (bar - zone.Since >= MaxBars)
        {
            if (zone.Retests == 0) Scene.Mark(bar, bar, bull ? b.High : b.Low, StudyMarkKind.Cross, StudyPalette.A(flipped, 150), null, bull);
            Close(zone, bar);
        }
    }

    private void Glyph(Zone zone, int bar, string text, decimal price, Color color, bool above, bool retest)
    {
        Scene.Mark(bar, bar, price, retest ? StudyMarkKind.Diamond : StudyMarkKind.Square, color, text, above);
        var mark = Scene.Marks[^1];
        bool spaced = bar - zone.LastSpaced >= MinBars;
        if (spaced) zone.LastSpaced = bar;
        if (retest) zone.LatestRetest = mark; else zone.LatestTest = mark;
        _glyphs.Add(mark, new GlyphInfo(zone, retest, spaced));
    }

    private static void Close(Zone zone, int bar)
    {
        if (zone.State == 0) return;
        zone.Box?.End(bar); zone.State = 0;
    }
}
