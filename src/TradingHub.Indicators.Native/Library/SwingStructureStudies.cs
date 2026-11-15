// Independent closed-bar implementations; capability reference gouqizi2214730-source/trading-hub @ 97a8521
// (MIT with Commons Clause). Not a port of its code.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// A close through a tier's last confirmed swing. <see cref="SwingBar"/> is the broken swing's extreme (drawing source),
/// <see cref="Bar"/> the closing bar that broke it (known at its close). When validation runs, the first later close outside
/// the band around <see cref="Level"/> resolves it at <see cref="Resolved"/>: beyond the band in the break direction
/// validates it, beyond the opposite side invalidates it. <see cref="ZoneTop"/>/<see cref="ZoneBottom"/> span the bars
/// that stayed inside the band.
/// </summary>
public sealed class StructureBreak
{
    public int Tier { get; init; }
    public int SwingBar { get; init; }
    public int Bar { get; init; }
    public decimal Level { get; init; }
    public int Direction { get; init; }
    public bool Choch { get; init; }
    public int Resolved { get; internal set; } = -1;
    public bool Valid { get; internal set; }
    public decimal ZoneTop { get; internal set; }
    public decimal ZoneBottom { get; internal set; }
}

/// <summary>A wick through the tier's last unbroken swing whose close came back, at closing bar <see cref="Bar"/>.</summary>
public readonly record struct StructureSweep(int Tier, StudySwing Swing, int Bar);

/// <summary>
/// Incremental swing structure over completed bars in tiers. Tier 0 swings are confirmed by
/// <see cref="StudySwings.Confirm"/> with the given strength on both sides; a swing of tier <c>t</c> is promoted to tier
/// <c>t + 1</c> when it is the extreme middle of the last three same-side swings of tier <c>t</c> (known when the third is
/// confirmed). In each tier the first close beyond the last swing (after its confirmation) breaks it once: CHoCH when the
/// previous break was the other way, otherwise BOS. The protected level is the opposite swing at the break; a close beyond
/// it loses it. Each step reports only what became known at that close.
/// </summary>
public sealed class SwingStructureEngine
{
    private sealed class Tier
    {
        public StudySwing? High, Low;
        public bool HighDone = true, LowDone = true, HighSwept, LowSwept;
        public int Trend, ProtectedBar = -1;
        public decimal? Protected;
        public StructureBreak? Pending, Last;
        public readonly List<StudySwing> Highs = new(), Lows = new();
    }

    private readonly Tier[] _tiers;
    private readonly int _strength;

    public SwingStructureEngine(int strength, int tiers = 1)
    {
        _strength = Math.Max(1, strength);
        _tiers = new Tier[Math.Clamp(tiers, 1, 3)];
        Reset();
    }

    /// <summary>When set, a break whose bar has a rejecting wick against it (at least half the range) does not count.</summary>
    public bool RejectionFilter { get; set; }
    public int TierCount => _tiers.Length;
    public List<(int Tier, StudySwing Swing)> NewSwings { get; } = new();
    public List<StructureBreak> NewBreaks { get; } = new();
    public List<StructureBreak> NewResolved { get; } = new();
    public List<StructureSweep> NewSweeps { get; } = new();

    public int Trend(int tier) => _tiers[tier].Trend;
    public decimal? Protected(int tier) => _tiers[tier].Protected;
    public int ProtectedBar(int tier) => _tiers[tier].ProtectedBar;
    public StudySwing? LastHigh(int tier) => _tiers[tier].High;
    public StudySwing? LastLow(int tier) => _tiers[tier].Low;
    public StructureBreak? LastBreak(int tier) => _tiers[tier].Last;

    public void Reset()
    {
        for (int t = 0; t < _tiers.Length; t++) _tiers[t] = new Tier();
        NewSwings.Clear(); NewBreaks.Clear(); NewResolved.Clear(); NewSweeps.Clear();
    }

    /// <summary>Processes completed bar <paramref name="bar"/> of <paramref name="bars"/>; <paramref name="band"/> null disables validation.</summary>
    public void Step(IReadOnlyList<StudyBar> bars, int bar, decimal? band)
    {
        NewSwings.Clear(); NewBreaks.Clear(); NewResolved.Clear(); NewSweeps.Clear();
        var (high, low) = StudySwings.Confirm(bars, bar, _strength, _strength);
        if (high is { } h) AddSwing(0, h);
        if (low is { } l) AddSwing(0, l);
        var b = bars[bar];
        if (!b.Causal) return;
        for (int t = 0; t < _tiers.Length; t++) Close(t, b, bar, band);
    }

    private void AddSwing(int t, StudySwing swing)
    {
        var s = _tiers[t];
        if (swing.High) { s.High = swing; s.HighDone = false; s.HighSwept = false; }
        else { s.Low = swing; s.LowDone = false; s.LowSwept = false; }
        NewSwings.Add((t, swing));
        if (t + 1 >= _tiers.Length) return;
        var side = swing.High ? s.Highs : s.Lows;
        side.Add(swing);
        if (side.Count > 3) side.RemoveAt(0);
        if (side.Count < 3) return;
        decimal a = side[0].Price, m = side[1].Price, c = side[2].Price;
        if (swing.High ? a < m && m >= c : a > m && m <= c) AddSwing(t + 1, new StudySwing(side[1].Bar, swing.Confirmed, m, swing.High));
    }

    private void Close(int t, StudyBar b, int bar, decimal? band)
    {
        var s = _tiers[t];
        if (band is { } w && s.Pending is { } p && bar > p.Bar)
        {
            bool above = b.Close > p.Level + w, below = b.Close < p.Level - w;
            if (above || below) { p.Resolved = bar; p.Valid = p.Direction > 0 ? above : below; s.Pending = null; NewResolved.Add(p); }
            else { p.ZoneTop = Math.Max(p.ZoneTop, b.High); p.ZoneBottom = Math.Min(p.ZoneBottom, b.Low); }
        }
        decimal range = b.Range;
        bool bullOk = !RejectionFilter || range <= 0 || (b.High - Math.Max(b.Open, b.Close)) * 2 < range;
        bool bearOk = !RejectionFilter || range <= 0 || (Math.Min(b.Open, b.Close) - b.Low) * 2 < range;
        if (s.High is { } hi && !s.HighDone && bar > hi.Confirmed)
        {
            if (b.Close > hi.Price && bullOk) { s.HighDone = true; Break(t, s, hi, bar, 1, b, band != null); }
            else if (!s.HighSwept && b.High > hi.Price && b.Close < hi.Price) { s.HighSwept = true; NewSweeps.Add(new(t, hi, bar)); }
        }
        if (s.Low is { } lo && !s.LowDone && bar > lo.Confirmed)
        {
            if (b.Close < lo.Price && bearOk) { s.LowDone = true; Break(t, s, lo, bar, -1, b, band != null); }
            else if (!s.LowSwept && b.Low < lo.Price && b.Close > lo.Price) { s.LowSwept = true; NewSweeps.Add(new(t, lo, bar)); }
        }
        if (s.Protected is { } pr && (s.Trend > 0 ? b.Close < pr : s.Trend < 0 && b.Close > pr)) { s.Protected = null; s.ProtectedBar = -1; }
    }

    private void Break(int t, Tier s, StudySwing swing, int bar, int dir, StudyBar b, bool validate)
    {
        var br = new StructureBreak
        {
            Tier = t, SwingBar = swing.Bar, Bar = bar, Level = swing.Price, Direction = dir, Choch = s.Trend == -dir,
            ZoneTop = dir > 0 ? b.High : swing.Price, ZoneBottom = dir > 0 ? swing.Price : b.Low,
        };
        s.Trend = dir; s.Last = br;
        if (validate) s.Pending = br;
        NewBreaks.Add(br);
        var opposite = dir > 0 ? s.Low : s.High;
        s.Protected = opposite?.Price; s.ProtectedBar = opposite?.Bar ?? -1;
    }
}

internal static class SwingStudyText
{
    internal static string Trend(int trend) => trend > 0 ? "Bullish" : trend < 0 ? "Bearish" : "—";
    internal static Color TrendColor(int trend, Color bull, Color bear) => trend > 0 ? bull : trend < 0 ? bear : default;
    internal static string Kind(StructureBreak br) => br.Choch ? "CHoCH" : "BOS";
    internal static string Count(int a, int b) => $"{a} / {b}";
}

// ====================================================================================================== shifts and sweeps

[DisplayName("Structure shifts with sweeps")]
[Category(IndicatorCategories.Structure)]
[Description("Internal structure from swings confirmed a swing length of closed bars later: BOS when a close breaks the last swing in the trend direction, CHoCH when it breaks against the trend. A sweep is a wick through the last unbroken swing with a close back inside. The protected level is the opposite swing the trend must hold (alertable); a close beyond it removes it. Heuristic; every event is known at its closing bar.")]
public class StructureShiftSweeps : ClosedBarStudy
{
    private int _length = 5;
    private bool _wick, _bos = true, _choch = true, _sweeps = true, _protected = true;
    private SwingStructureEngine _engine = new(5);
    private StudyLine? _protectedLine, _highLine, _lowLine;
    private int _bosCount, _chochCount, _sweepCount;
    private readonly List<StructureBreak> _breaks = new();
    private readonly OptionalValueDataSeries _trend = new("StructureShift.Trend", "Internal structure trend (+1 / -1)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _signal = new("StructureShift.Signal", "Break at this close (+1/-1 BOS, +2/-2 CHoCH)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _sweep = new("StructureShift.Sweep", "Sweep at this close (+1 low swept, -1 high swept)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _level = new("StructureShift.Protected", "Protected level") { VisualType = VisualMode.Hide };

    public StructureShiftSweeps() { DataSeries[0] = _trend; DataSeries.Add(_signal); DataSeries.Add(_sweep); DataSeries.Add(_level); }

    [Display(Name = "Internal structure length", GroupName = "Calculation", Order = 10), Range(1, 100)]
    public int Length { get => _length; set { _length = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Ignore breaks with a rejecting wick", GroupName = "Calculation", Order = 11)]
    public bool WickFilter { get => _wick; set { _wick = value; RecalculateValues(); } }
    [Display(Name = "BOS", GroupName = "Drawing", Order = 20)]
    public bool ShowBos { get => _bos; set { _bos = value; RecalculateValues(); } }
    [Display(Name = "CHoCH", GroupName = "Drawing", Order = 21)]
    public bool ShowChoch { get => _choch; set { _choch = value; RecalculateValues(); } }
    [Display(Name = "Sweeps", GroupName = "Drawing", Order = 22)]
    public bool ShowSweeps { get => _sweeps; set { _sweeps = value; RecalculateValues(); } }
    [Display(Name = "Protected level", GroupName = "Drawing", Order = 23)]
    public bool ShowProtected { get => _protected; set { _protected = value; RecalculateValues(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 30)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 31)] public Color BearColor { get; set; } = StudyPalette.Bear;

    /// <summary>Breaks known so far (newest last, at most 1000).</summary>
    [Browsable(false)] public IReadOnlyList<StructureBreak> Breaks => _breaks;

    protected override void ResetState()
    {
        _engine = new SwingStructureEngine(Length) { RejectionFilter = WickFilter };
        _protectedLine = _highLine = _lowLine = null; _breaks.Clear(); _bosCount = _chochCount = _sweepCount = 0;
    }

    protected override void OnCoverageGap(int bar)
    {
        _protectedLine?.End(bar); _highLine?.End(bar); _lowLine?.End(bar);
        base.OnCoverageGap(bar);
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; int signal = 0, sweep = 0;
        _engine.Step(Bars, bar, null);
        foreach (var (_, sw) in _engine.NewSwings)
        {
            var line = Scene.Level(sw.Bar, bar, sw.Price, StudyPalette.A(sw.High ? BearColor : BullColor, 150), null,
                sw.High ? "Internal swing high" : "Internal swing low", 1, LineDashStyle.Dot);
            if (sw.High) { _highLine?.End(bar); _highLine = line; } else { _lowLine?.End(bar); _lowLine = line; }
        }
        foreach (var br in _engine.NewBreaks)
        {
            _breaks.Add(br); if (_breaks.Count > 1000) _breaks.RemoveAt(0);
            if (br.Choch) _chochCount++; else _bosCount++;
            signal = br.Direction * (br.Choch ? 2 : 1);
            if (br.Direction > 0) { _highLine?.End(bar); _highLine = null; } else { _lowLine?.End(bar); _lowLine = null; }
            if (!(br.Choch ? ShowChoch : ShowBos)) continue;
            var c = br.Direction > 0 ? BullColor : BearColor;
            Scene.Segment(br.SwingBar, bar, bar, br.Level, br.Level, c, 1, br.Choch ? LineDashStyle.Dash : LineDashStyle.Solid);
            Scene.Tag((br.SwingBar + bar) / 2, bar, br.Level, SwingStudyText.Kind(br), c, br.Direction > 0);
            Scene.Mark(bar, bar, br.Direction > 0 ? b.Low : b.High, br.Direction > 0 ? StudyMarkKind.TriangleUp : StudyMarkKind.TriangleDown, c, null, br.Direction < 0);
        }
        foreach (var sw in _engine.NewSweeps)
        {
            _sweepCount++; sweep = sw.Swing.High ? -1 : 1;
            if (ShowSweeps) Scene.Mark(bar, bar, sw.Swing.High ? b.High : b.Low, StudyMarkKind.Circle, sw.Swing.High ? BearColor : BullColor, "Sweep", sw.Swing.High);
        }
        decimal? level = _engine.Protected(0); int from = _engine.ProtectedBar(0);
        if (_protectedLine != null && (level != _protectedLine.Price || from != _protectedLine.From)) { _protectedLine.End(bar); _protectedLine = null; }
        if (level is { } p && _protectedLine == null && ShowProtected)
            _protectedLine = Scene.Level(from, bar, p, _engine.Trend(0) > 0 ? BullColor : BearColor, "Protected", "Protected swing", 1, LineDashStyle.Dash);
        int trend = _engine.Trend(0);
        _trend.Set(bar, trend); _signal.Set(bar, signal); _sweep.Set(bar, sweep);
        if (level is { } lv) _level.Set(bar, lv); else _level.SetUnavailable(bar);

        Scene.Dash.Clear(); Scene.DashTitle = "Internal structure";
        Scene.Row("Trend", SwingStudyText.Trend(trend), SwingStudyText.TrendColor(trend, BullColor, BearColor));
        Scene.Row("BOS / CHoCH / sweeps", $"{_bosCount} / {_chochCount} / {_sweepCount}");
        if (level is { } shown) Scene.Row("Protected level", Price(shown));
    }
}

// ====================================================================================================== anchored three tiers

[DisplayName("Anchored market structure (3 tiers)")]
[Category(IndicatorCategories.Structure)]
[Description("Short-term swings (confirmed a swing strength of closed bars later), intermediate swings (a short-term swing more extreme than its two same-side neighbours) and long-term swings (the same over intermediate swings), with each tier's BOS / CHoCH on closes. A break is validated by an ATR band around the broken level: the first later close outside the band confirms it (in the break direction) or invalidates it (on the other side). Structure boxes span the bars inside the band; heuristic, every shape is known at its closing bar.")]
public class AnchoredMarketStructure : ClosedBarStudy
{
    private static readonly string[] TierTrendRows = { "Short-term trend", "Intermediate trend", "Long-term trend" };
    private static readonly string[] TierBreakRows = { "Short-term last break", "Intermediate last break", "Long-term last break" };
    private int _strength = 1, _atrLength = 17;
    private decimal _band = 1;
    private bool _validate = true, _labels = true, _boxes = true, _background = true;
    private readonly bool[] _swingsOn = { true, true, true }, _structureOn = { true, true, true };
    private SwingStructureEngine _engine = new(1, 3);
    private RunningAtr _atr = new(17);
    private StudyLine? _longHigh, _longLow;
    private readonly List<StructureBreak> _breaks = new();
    private readonly OptionalValueDataSeries _short = new("AnchoredStructure.ShortTrend", "Short-term trend (+1 / -1)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _mid = new("AnchoredStructure.IntermediateTrend", "Intermediate trend (+1 / -1)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _long = new("AnchoredStructure.LongTrend", "Long-term trend (+1 / -1)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _break = new("AnchoredStructure.Break", "Highest-tier break at this close (direction x tier 1..3)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _validated = new("AnchoredStructure.Validated", "Highest-tier break validated at this close (direction x tier 1..3)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _invalidated = new("AnchoredStructure.Invalidated", "Highest-tier break invalidated at this close (direction x tier 1..3)") { VisualType = VisualMode.Hide, ShowZeroValue = true };

    public AnchoredMarketStructure()
    {
        DataSeries[0] = _short; DataSeries.Add(_mid); DataSeries.Add(_long); DataSeries.Add(_break); DataSeries.Add(_validated); DataSeries.Add(_invalidated);
    }

    [Display(Name = "Short-term swing strength (bars each side)", GroupName = "Calculation", Order = 10), Range(1, 20)]
    public int SwingStrength { get => _strength; set { _strength = Math.Clamp(value, 1, 20); RecalculateValues(); } }
    [Display(Name = "Validation ATR length", GroupName = "Calculation", Order = 11), Range(1, 200)]
    public int ValidationAtrLength { get => _atrLength; set { _atrLength = Math.Clamp(value, 1, 200); RecalculateValues(); } }
    [Display(Name = "Validation band (ATR)", GroupName = "Calculation", Order = 12), Range(0.1, 10)]
    public decimal ValidationBand { get => _band; set { _band = Math.Clamp(value, 0.1m, 10); RecalculateValues(); } }
    [Display(Name = "Short-term swings", GroupName = "Short term", Order = 20)]
    public bool ShortSwings { get => _swingsOn[0]; set { _swingsOn[0] = value; RecalculateValues(); } }
    [Display(Name = "Short-term structure", GroupName = "Short term", Order = 21)]
    public bool ShortStructure { get => _structureOn[0]; set { _structureOn[0] = value; RecalculateValues(); } }
    [Display(Name = "Intermediate swings", GroupName = "Intermediate term", Order = 30)]
    public bool IntermediateSwings { get => _swingsOn[1]; set { _swingsOn[1] = value; RecalculateValues(); } }
    [Display(Name = "Intermediate structure", GroupName = "Intermediate term", Order = 31)]
    public bool IntermediateStructure { get => _structureOn[1]; set { _structureOn[1] = value; RecalculateValues(); } }
    [Display(Name = "Long-term swings", GroupName = "Long term", Order = 40)]
    public bool LongSwings { get => _swingsOn[2]; set { _swingsOn[2] = value; RecalculateValues(); } }
    [Display(Name = "Long-term structure", GroupName = "Long term", Order = 41)]
    public bool LongStructure { get => _structureOn[2]; set { _structureOn[2] = value; RecalculateValues(); } }
    [Display(Name = "Validate breaks", GroupName = "Drawing", Order = 50)]
    public bool ValidateBreaks { get => _validate; set { _validate = value; RecalculateValues(); } }
    [Display(Name = "Labels", GroupName = "Drawing", Order = 51)]
    public bool ShowBreakLabels { get => _labels; set { _labels = value; RecalculateValues(); } }
    [Display(Name = "Structure boxes", GroupName = "Drawing", Order = 52)]
    public bool StructureBoxes { get => _boxes; set { _boxes = value; RecalculateValues(); } }
    [Display(Name = "Tint the break bar", GroupName = "Drawing", Order = 53)]
    public bool BreakTint { get => _background; set { _background = value; RecalculateValues(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 60)] public Color BullColor { get; set; } = Color.FromArgb(255, 0, 176, 79);
    [Display(Name = "Bearish", GroupName = "Colors", Order = 61)] public Color BearColor { get; set; } = Color.FromArgb(255, 156, 27, 65);

    /// <summary>Breaks of every tier known so far (newest last, at most 1000).</summary>
    [Browsable(false)] public IReadOnlyList<StructureBreak> Breaks => _breaks;

    protected override void ResetState()
    {
        _engine = new SwingStructureEngine(SwingStrength, 3); _atr = new(ValidationAtrLength);
        _longHigh = _longLow = null; _breaks.Clear();
    }

    protected override void OnCoverageGap(int bar)
    {
        _longHigh?.End(bar); _longLow?.End(bar);
        base.OnCoverageGap(bar);
    }

    private static string Label(int tier, bool choch) => (tier, choch) switch
    {
        (0, false) => "ST BOS", (0, true) => "ST CHoCH", (1, false) => "IT BOS", (1, true) => "IT CHoCH", (_, false) => "LT BOS", _ => "LT CHoCH",
    };

    private static string State(StructureBreak br) => (br.Choch, br.Resolved < 0, br.Valid) switch
    {
        (false, true, _) => "BOS (validating)", (true, true, _) => "CHoCH (validating)",
        (false, false, true) => "BOS validated", (false, false, false) => "BOS invalidated",
        (true, false, true) => "CHoCH validated", _ => "CHoCH invalidated",
    };

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; decimal atr = _atr.Add(b);
        _engine.Step(Bars, bar, atr * ValidationBand);
        foreach (var (t, sw) in _engine.NewSwings)
        {
            if (t == 2)
            {
                var line = Scene.Level(sw.Bar, bar, sw.Price, StudyPalette.A(sw.High ? BearColor : BullColor, 120), null,
                    sw.High ? "Long-term swing high" : "Long-term swing low", 1, LineDashStyle.Dot);
                if (sw.High) { _longHigh?.End(bar); _longHigh = line; } else { _longLow?.End(bar); _longLow = line; }
            }
            if (!_swingsOn[t]) continue;
            var kind = t switch { 0 => StudyMarkKind.Circle, 1 => sw.High ? StudyMarkKind.TriangleDown : StudyMarkKind.TriangleUp, _ => StudyMarkKind.Diamond };
            string? text = t switch { 1 => sw.High ? "ITH" : "ITL", 2 => sw.High ? "LTH" : "LTL", _ => null };
            Scene.Mark(sw.Bar, bar, sw.Price, kind, StudyPalette.A(sw.High ? BearColor : BullColor, t == 0 ? 110 : 220), text, sw.High);
        }
        int brk = 0, valid = 0, invalid = 0;
        foreach (var br in _engine.NewBreaks)
        {
            _breaks.Add(br); if (_breaks.Count > 1000) _breaks.RemoveAt(0);
            int t = br.Tier; if (Math.Abs(brk) <= t) brk = br.Direction * (t + 1);
            if (t == 2) { if (br.Direction > 0) { _longHigh?.End(bar); _longHigh = null; } else { _longLow?.End(bar); _longLow = null; } }
            if (!_structureOn[t]) continue;
            var c = br.Direction > 0 ? BullColor : BearColor;
            var style = t switch { 0 => LineDashStyle.Dot, 1 => LineDashStyle.Dash, _ => LineDashStyle.Solid };
            Scene.Segment(br.SwingBar, bar, bar, br.Level, br.Level, c, t == 2 ? 2 : 1, style);
            if (ShowBreakLabels) Scene.Tag((br.SwingBar + bar) / 2, bar, br.Level, Label(t, br.Choch), c, br.Direction > 0);
            if (BreakTint && t >= 1) Scene.Tints[bar] = StudyPalette.A(c, 20);
        }
        foreach (var br in _engine.NewResolved)
        {
            int t = br.Tier, code = br.Direction * (t + 1);
            if (br.Valid) { if (Math.Abs(valid) <= t) valid = code; } else if (Math.Abs(invalid) <= t) invalid = code;
            if (!ValidateBreaks || !_structureOn[t]) continue;
            var c = br.Direction > 0 ? BullColor : BearColor;
            if (StructureBoxes) Scene.Box(br.Bar, bar, br.ZoneTop, br.ZoneBottom, c, null, null, 22).End(bar);
            Scene.Mark(bar, bar, br.Direction > 0 ? b.Low : b.High, br.Valid ? StudyMarkKind.Square : StudyMarkKind.Cross, br.Valid ? c : StudyPalette.Muted,
                br.Valid ? "✓" : "✕", br.Direction < 0);
        }
        _short.Set(bar, _engine.Trend(0)); _mid.Set(bar, _engine.Trend(1)); _long.Set(bar, _engine.Trend(2));
        _break.Set(bar, brk); _validated.Set(bar, valid); _invalidated.Set(bar, invalid);

        Scene.Dash.Clear(); Scene.DashTitle = "Anchored structure";
        for (int t = 0; t < 3; t++)
        {
            int trend = _engine.Trend(t);
            Scene.Row(TierTrendRows[t], SwingStudyText.Trend(trend), SwingStudyText.TrendColor(trend, BullColor, BearColor));
            if (_engine.LastBreak(t) is { } last) Scene.Row(TierBreakRows[t], State(last));
        }
    }
}

// ====================================================================================================== regime oscillator

[DisplayName("Structure regime oscillator")]
[Category(IndicatorCategories.Structure)]
[Description("A 0-100 oscillator in its own panel from the three-tier anchored structure: each tier is above 50 in a bullish regime and below 50 in a bearish one, placed by where the close sits in the closing range since the break that started the regime; tiers are smoothed and weighted. The cycle is the oscillator minus its EMA (histogram around 50); 50-line and cycle crosses are marked. Heuristic; every value uses only closes up to its bar.")]
public class StructureRegimeOscillator : ClosedBarStudy
{
    private sealed class TierState
    {
        public int Regime; public decimal High, Low;
        public readonly Queue<decimal?> Raw = new();
    }

    private int _strength = 1, _w1 = 1, _w2 = 3, _w3 = 2, _smooth = 4, _cycleLength = 7;
    private SwingStructureEngine _engine = new(1, 3);
    private readonly TierState[] _tiers = new TierState[3];
    private RunningEma _cycleEma = new(7);
    private decimal? _prevOsc, _prevCycle;
    private readonly OptionalValueDataSeries _osc = new("StructureRegime.Oscillator", "Structure regime") { Width = 2, Color = Color.FromArgb(255, 230, 230, 230) };
    private readonly OptionalValueDataSeries _st = new("StructureRegime.Short", "Short-term regime") { Color = Color.FromArgb(160, 41, 182, 246), VisualType = VisualMode.Hide };
    private readonly OptionalValueDataSeries _it = new("StructureRegime.Intermediate", "Intermediate regime") { Color = Color.FromArgb(160, 171, 71, 188), VisualType = VisualMode.Hide };
    private readonly OptionalValueDataSeries _lt = new("StructureRegime.Long", "Long-term regime") { Color = Color.FromArgb(160, 255, 167, 38), VisualType = VisualMode.Hide };
    private readonly OptionalValueDataSeries _cycle = new("StructureRegime.Cycle", "Cycle (oscillator minus its EMA)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _cross = new("StructureRegime.Cross", "50-line cross at this close (+1 up / -1 down)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _cycleCross = new("StructureRegime.CycleCross", "Cycle cross at this close (+1 up / -1 down)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries[] _lines;

    public StructureRegimeOscillator()
    {
        Panel = IndicatorDataProvider.NewPanel;
        _lines = new[] { _st, _it, _lt };
        for (int t = 0; t < 3; t++) _tiers[t] = new TierState();
        DataSeries[0] = _osc; DataSeries.Add(_st); DataSeries.Add(_it); DataSeries.Add(_lt); DataSeries.Add(_cycle); DataSeries.Add(_cross); DataSeries.Add(_cycleCross);
        LineSeries.Add(new LineSeries("Mid", "50") { Value = 50, Color = Color.FromArgb(110, 140, 140, 150), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("Upper", "80") { Value = 80, Color = Color.FromArgb(60, 140, 140, 150), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("Lower", "20") { Value = 20, Color = Color.FromArgb(60, 140, 140, 150), LineDashStyle = LineDashStyle.Dot });
    }

    [Display(Name = "Short-term swing strength (bars each side)", GroupName = "Calculation", Order = 10), Range(1, 20)]
    public int SwingStrength { get => _strength; set { _strength = Math.Clamp(value, 1, 20); RecalculateValues(); } }
    [Display(Name = "Short-term weight", GroupName = "Calculation", Order = 11), Range(0, 10)]
    public int ShortWeight { get => _w1; set { _w1 = Math.Clamp(value, 0, 10); RecalculateValues(); } }
    [Display(Name = "Intermediate weight", GroupName = "Calculation", Order = 12), Range(0, 10)]
    public int IntermediateWeight { get => _w2; set { _w2 = Math.Clamp(value, 0, 10); RecalculateValues(); } }
    [Display(Name = "Long-term weight", GroupName = "Calculation", Order = 13), Range(0, 10)]
    public int LongWeight { get => _w3; set { _w3 = Math.Clamp(value, 0, 10); RecalculateValues(); } }
    [Display(Name = "Smoothing (bars)", GroupName = "Calculation", Order = 14), Range(1, 50)]
    public int Smoothing { get => _smooth; set { _smooth = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    [Display(Name = "Cycle EMA length", GroupName = "Calculation", Order = 15), Range(2, 100)]
    public int CycleLength { get => _cycleLength; set { _cycleLength = Math.Clamp(value, 2, 100); RecalculateValues(); } }
    [Display(Name = "Cycle histogram", GroupName = "Drawing", Order = 20)] public bool ShowCycle { get; set; } = true;
    [Display(Name = "Structure crosses (50)", GroupName = "Drawing", Order = 21)] public bool StructureCrosses { get; set; } = true;
    [Display(Name = "Cycle crosses", GroupName = "Drawing", Order = 22)] public bool CycleCrosses { get; set; } = true;
    [Display(Name = "Short-term line", GroupName = "Drawing", Order = 23)]
    public bool ShowShort { get => _st.VisualType != VisualMode.Hide; set => _st.VisualType = value ? VisualMode.Line : VisualMode.Hide; }
    [Display(Name = "Intermediate line", GroupName = "Drawing", Order = 24)]
    public bool ShowIntermediate { get => _it.VisualType != VisualMode.Hide; set => _it.VisualType = value ? VisualMode.Line : VisualMode.Hide; }
    [Display(Name = "Long-term line", GroupName = "Drawing", Order = 25)]
    public bool ShowLong { get => _lt.VisualType != VisualMode.Hide; set => _lt.VisualType = value ? VisualMode.Line : VisualMode.Hide; }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 30)] public Color BullColor { get; set; } = Color.FromArgb(255, 0, 176, 79);
    [Display(Name = "Bearish", GroupName = "Colors", Order = 31)] public Color BearColor { get; set; } = Color.FromArgb(255, 156, 27, 65);

    [Browsable(false)] public override string Status => "Closed bars only; each value uses closes up to its bar";

    protected override void ResetState()
    {
        _engine = new SwingStructureEngine(SwingStrength, 3); _cycleEma = new(CycleLength); _prevOsc = _prevCycle = null;
        for (int t = 0; t < 3; t++) _tiers[t] = new TierState();
    }

    protected override void OnBarClosed(int bar)
    {
        decimal close = Bars[bar].Close;
        _engine.Step(Bars, bar, null);
        int Weight(int t) => t switch { 0 => ShortWeight, 1 => IntermediateWeight, _ => LongWeight };
        decimal sum = 0, weight = 0;
        for (int t = 0; t < 3; t++)
        {
            var s = _tiers[t]; int now = _engine.Trend(t);
            if (now != s.Regime) { s.Regime = now; s.High = s.Low = close; }
            s.High = Math.Max(s.High, close); s.Low = Math.Min(s.Low, close);
            decimal position = s.High > s.Low ? (close - s.Low) / (s.High - s.Low) : 0.5m;
            s.Raw.Enqueue(s.Regime == 0 ? null : s.Regime > 0 ? 50 + 50 * position : 50 * position);
            while (s.Raw.Count > Smoothing) s.Raw.Dequeue();
            decimal raw = 0; int n = 0;
            foreach (var v in s.Raw) if (v is { } x) { raw += x; n++; }
            if (n == 0) { _lines[t].SetUnavailable(bar); continue; }
            decimal value = raw / n;
            _lines[t].Set(bar, value);
            if (Weight(t) > 0) { sum += value * Weight(t); weight += Weight(t); }
        }
        int cross = 0, cycleCross = 0;
        if (weight > 0)
        {
            decimal osc = sum / weight, cycle = osc - _cycleEma.Add(osc);
            _osc.Set(bar, osc); _osc.Colors[bar] = osc >= 50 ? BullColor : BearColor; _cycle.Set(bar, cycle);
            if (_prevOsc is { } po && (po - 50) * (osc - 50) < 0) cross = osc > 50 ? 1 : -1;
            if (_prevCycle is { } pc && pc * cycle < 0) cycleCross = cycle > 0 ? 1 : -1;
            _prevOsc = osc; _prevCycle = cycle;
        }
        else { _osc.SetUnavailable(bar); _cycle.SetUnavailable(bar); }
        _cross.Set(bar, cross); _cycleCross.Set(bar, cycleCross);
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box || Bars.Count == 0) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(LastVisibleBarNumber, Bars.Count - 1);
        int bw = Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth), y50 = box.GetYByValue(50);
        for (int i = first; i <= last; i++)
        {
            int x = ChartInfo.GetXByBar(i, false);
            if (ShowCycle && _cycle.IsAvailable(i))
            {
                int y = box.GetYByValue(Math.Clamp(50 + 2 * _cycle[i], 0, 100));
                ctx.FillRectangle(StudyPalette.A(_cycle[i] >= 0 ? BullColor : BearColor, 150),
                    new Rectangle(x - Math.Max(1, bw / 3), Math.Min(y, y50), Math.Max(1, bw * 2 / 3), Math.Max(1, Math.Abs(y - y50))));
            }
            if (StructureCrosses && _cross[i] != 0 && _osc.IsAvailable(i))
                ctx.FillEllipse(_cross[i] > 0 ? BullColor : BearColor, new Rectangle(x - 4, box.GetYByValue(_osc[i]) - 4, 8, 8));
            if (CycleCrosses && _cycleCross[i] != 0)
                DrawMark(ctx, _cycleCross[i] > 0 ? StudyMarkKind.TriangleUp : StudyMarkKind.TriangleDown, x, box.GetYByValue(_cycleCross[i] > 0 ? 8 : 92), 4, _cycleCross[i] > 0 ? BullColor : BearColor);
        }
        FootprintStudyData.Legend(ctx, box.Region, 2, UiText.Tr(Status), Color.LightSlateGray);
    }
}

// ====================================================================================================== scatter panel

/// <summary>A measured leg: impulse A→B (percent of A) and its retracement B→C (fraction of the impulse), known at C's confirmation.</summary>
public readonly record struct ScatterLeg(int Available, int ImpulseFrom, int ImpulseTo, decimal ImpulsePercent, decimal Retracement, bool Bullish);

[DisplayName("Structure scatter panel")]
[Category(IndicatorCategories.Structure)]
[Description("From alternating confirmed swings, measures each impulse (percent move) and its retracement (fraction of the impulse) and plots them as a scatter panel, with the current leg highlighted. Bars are painted by the percentile of the current leg against the retained legs; BOS / CHoCH are marked and a table shows the averages. Each leg is known when the swing ending its retracement is confirmed.")]
public class StructureScatterPanel : ClosedBarStudy
{
    public enum HeatMode
    {
        [Display(Name = "Combined")] Combined,
        [Display(Name = "Impulse")] Impulse,
        [Display(Name = "Retracement")] Retracement,
    }

    private int _length = 10, _sample = 150;
    private bool _heatmap = true, _structure = true;
    private HeatMode _mode = HeatMode.Retracement;
    private SwingStructureEngine _engine = new(10);
    private readonly List<StudySwing> _alt = new();
    private readonly List<ScatterLeg> _legs = new();
    private int _count, _breaksUp, _breaksDown;
    private decimal _sumImpulse, _sumRetrace;
    private (decimal Impulse, decimal Retrace, bool Bull)? _live;
    private StudyPath? _path;
    private readonly PaintbarsDataSeries _paint = new("StructureScatter.Paint", "Leg percentile colors");
    private readonly OptionalValueDataSeries _percentile = new("StructureScatter.Percentile", "Current leg percentile (0-100)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _trend = new("StructureScatter.Trend", "Structure trend (+1 / -1)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _signal = new("StructureScatter.Signal", "Break at this close (+1/-1 BOS, +2/-2 CHoCH)") { VisualType = VisualMode.Hide, ShowZeroValue = true };

    public StructureScatterPanel() { DataSeries[0] = _percentile; DataSeries.Add(_trend); DataSeries.Add(_signal); DataSeries.Add(_paint); }

    [Display(Name = "Pivot bars each side", GroupName = "Calculation", Order = 10), Range(1, 100)]
    public int PivotLength { get => _length; set { _length = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Retained legs (percentile sample)", GroupName = "Calculation", Order = 11), Range(10, 1000)]
    public int SampleSize { get => _sample; set { _sample = Math.Clamp(value, 10, 1000); RecalculateValues(); } }
    [Display(Name = "Paint bars by percentile", GroupName = "Drawing", Order = 20)]
    public bool Heatmap { get => _heatmap; set { _heatmap = value; RecalculateValues(); } }
    [Display(Name = "Percentile of", GroupName = "Drawing", Order = 21)]
    public HeatMode Mode { get => _mode; set { _mode = value; RecalculateValues(); } }
    [Display(Name = "Scatter panel", GroupName = "Drawing", Order = 22)] public bool ShowScatter { get; set; } = true;
    [Display(Name = "Structure breaks", GroupName = "Drawing", Order = 23)]
    public bool ShowStructure { get => _structure; set { _structure = value; RecalculateValues(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 30)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 31)] public Color BearColor { get; set; } = StudyPalette.Bear;

    /// <summary>Retained measured legs (newest last, at most <see cref="SampleSize"/>).</summary>
    [Browsable(false)] public IReadOnlyList<ScatterLeg> Legs => _legs;
    /// <summary>Legs measured since the start (not limited by retention).</summary>
    [Browsable(false)] public int LegCount => _count;
    [Browsable(false)] public decimal? AverageImpulse => _count == 0 ? null : _sumImpulse / _count;
    [Browsable(false)] public decimal? AverageRetracement => _count == 0 ? null : _sumRetrace / _count;

    protected override void ResetState()
    {
        _engine = new SwingStructureEngine(PivotLength); _alt.Clear(); _legs.Clear(); _count = _breaksUp = _breaksDown = 0;
        _sumImpulse = _sumRetrace = 0; _live = null; _path = null;
    }

    private static decimal Impulse(decimal from, decimal to) => from > 0 ? Math.Abs(to - from) / from * 100 : 0;

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; int signal = 0;
        _engine.Step(Bars, bar, null);
        bool changed = false;
        foreach (var (_, p) in _engine.NewSwings)
        {
            if (_alt.Count > 0 && _alt[^1].High == p.High)
            {
                if (p.High ? p.Price > _alt[^1].Price : p.Price < _alt[^1].Price) { _alt[^1] = p; changed = true; }
                continue;
            }
            _alt.Add(p); changed = true;
            if (_alt.Count > 100) _alt.RemoveAt(0);
            if (_alt.Count < 3) continue;
            StudySwing a = _alt[^3], m = _alt[^2], c = _alt[^1];
            decimal move = Math.Abs(m.Price - a.Price);
            var leg = new ScatterLeg(bar, a.Bar, m.Bar, Impulse(a.Price, m.Price), move > 0 ? Math.Abs(c.Price - m.Price) / move : 0, m.Price > a.Price);
            _legs.Add(leg); if (_legs.Count > SampleSize) _legs.RemoveAt(0);
            _count++; _sumImpulse += leg.ImpulsePercent; _sumRetrace += leg.Retracement;
        }
        if (changed && _alt.Count >= 2)
        {
            if (_path != null) Scene.Paths.Remove(_path);
            _path = new StudyPath(bar, _alt.Skip(Math.Max(0, _alt.Count - 12)).Select(x => (x.Bar, x.Price)).ToList(), StudyPalette.A(StudyPalette.Muted, 160));
            Scene.Paths.Add(_path);
        }
        foreach (var br in _engine.NewBreaks)
        {
            if (br.Direction > 0) _breaksUp++; else _breaksDown++;
            signal = br.Direction * (br.Choch ? 2 : 1);
            if (!ShowStructure) continue;
            var c = br.Direction > 0 ? BullColor : BearColor;
            Scene.Segment(br.SwingBar, bar, bar, br.Level, br.Level, c, 1, br.Choch ? LineDashStyle.Dash : LineDashStyle.Solid);
            Scene.Tag((br.SwingBar + bar) / 2, bar, br.Level, SwingStudyText.Kind(br), c, br.Direction > 0);
        }
        int trend = _engine.Trend(0);
        _live = null; _paint[bar] = null;
        if (_alt.Count >= 2)
        {
            StudySwing a = _alt[^2], c = _alt[^1];
            decimal move = Math.Abs(c.Price - a.Price), impulse = Impulse(a.Price, c.Price), retrace = move > 0 ? Math.Abs(b.Close - c.Price) / move : 0;
            _live = (impulse, retrace, c.Price > a.Price);
            if (_legs.Count > 0)
            {
                decimal pi = (decimal)_legs.Count(l => l.ImpulsePercent <= impulse) / _legs.Count, pr = (decimal)_legs.Count(l => l.Retracement <= retrace) / _legs.Count;
                decimal heat = Mode switch { HeatMode.Impulse => pi, HeatMode.Retracement => pr, _ => (pi + pr) / 2 };
                _percentile.Set(bar, heat * 100);
                if (Heatmap && heat > 0) _paint[bar] = StudyPalette.A(trend >= 0 ? BullColor : BearColor, (int)(80 + 175 * heat));
            }
            else _percentile.SetUnavailable(bar);
        }
        else _percentile.SetUnavailable(bar);
        _trend.Set(bar, trend); _signal.Set(bar, signal);

        Scene.Dash.Clear(); Scene.DashTitle = "Structure scatter";
        Scene.Row("Measured legs", _count.ToString());
        if (AverageImpulse is { } ai) Scene.Row("Average impulse", ai.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " %");
        if (AverageRetracement is { } ar) Scene.Row("Average retracement", (ar * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %");
        if (_live is { } lv)
            Scene.Row("Current leg", $"{lv.Impulse.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} % · {(lv.Retrace * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} %", lv.Bull ? BullColor : BearColor);
        if (_percentile.IsAvailable(bar)) Scene.Row("Current leg percentile", _percentile[bar].ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        Scene.Row("Breaks up / down", SwingStudyText.Count(_breaksUp, _breaksDown));
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (!ShowScatter || _legs.Count == 0) return;
        const int w = 190, h = 140;
        bool left = (int)DashboardPosition % 3 == 2;
        int x0 = left ? region.Left + 8 : region.Right - w - 8, y0 = region.Top + 26;
        if (y0 + h > region.Bottom) y0 = Math.Max(region.Top + 4, region.Bottom - h - 4);
        ctx.FillRectangle(Color.FromArgb(215, 18, 20, 26), new Rectangle(x0, y0, w, h), 5);
        ctx.DrawRectangle(new RenderPen(Color.FromArgb(90, 120, 130, 150), 1), new Rectangle(x0, y0, w, h), 5);
        var font = new RenderFont("Arial", 8);
        ctx.DrawString(UiText.Tr("Impulse (%) →"), font, StudyPalette.Muted, x0 + 6, y0 + h - 14);
        ctx.DrawString(UiText.Tr("↑ Retracement"), font, StudyPalette.Muted, x0 + 6, y0 + 4);
        double mx = Math.Max(1e-9, (double)_legs.Max(p => p.ImpulsePercent)) * 1.1, my = Math.Max(1e-9, Math.Min(3, (double)_legs.Max(p => p.Retracement))) * 1.1;
        if (_live is { } l0) { mx = Math.Max(mx, (double)l0.Impulse * 1.1); my = Math.Max(my, Math.Min(3, (double)l0.Retrace) * 1.1); }
        int px0 = x0 + 10, py0 = y0 + 18, pw = w - 20, ph = h - 36;
        var axis = new RenderPen(Color.FromArgb(70, 140, 140, 150), 1);
        ctx.DrawLine(axis, px0, py0 + ph, px0 + pw, py0 + ph);
        ctx.DrawLine(axis, px0, py0, px0, py0 + ph);
        int yFib = py0 + ph - (int)(ph * Math.Min(1, 0.618 / my));
        ctx.DrawLine(new RenderPen(StudyPalette.A(StudyPalette.Amber, 60), 1), px0, yFib, px0 + pw, yFib);
        Point At(decimal x, decimal y) => new(px0 + (int)(pw * Math.Min(1, (double)x / mx)), py0 + ph - (int)(ph * Math.Min(1, (double)y / my)));
        foreach (var p in _legs)
        {
            var q = At(p.ImpulsePercent, p.Retracement);
            ctx.FillEllipse(StudyPalette.A(p.Bullish ? BullColor : BearColor, 170), new Rectangle(q.X - 2, q.Y - 2, 5, 5));
        }
        if (_live is { } lv)
        {
            var q = At(lv.Impulse, lv.Retrace);
            ctx.DrawEllipse(new RenderPen(Color.White, 2), new Rectangle(q.X - 5, q.Y - 5, 10, 10));
        }
    }
}

// ====================================================================================================== trend panel

[DisplayName("Structure trend panel")]
[Category(IndicatorCategories.Structure)]
[Description("BOS / CHoCH on the chart period or a higher period, liquidity sweeps (a wick through a confirmed swing with a close back), active fair value gaps and trend bar painting. A higher-period bar is used only from the close of the first chart bar after it completes. The dashboard shows the structure trend of six periods (chart to week) from completed bars only. Heuristic.")]
public class StructureTrendPanel : ClosedBarStudy
{
    private sealed class Frame(HigherTimeframeData? data, int length)
    {
        public readonly HigherTimeframeData? Data = data;
        public readonly List<StudyBar> Bars = new();
        public readonly List<int> Start = new();
        public readonly SwingStructureEngine Engine = new(length);
    }

    private readonly record struct Liquidity(int From, int Available, decimal Price, bool High);

    private static readonly (StudyTimeFrame Period, string Name)[] Rows =
    {
        (StudyTimeFrame.Chart, "Chart"), (StudyTimeFrame.Minutes15, "15 minutes"), (StudyTimeFrame.Hour, "1 hour"),
        (StudyTimeFrame.Hours4, "4 hours"), (StudyTimeFrame.Day, "1 day"), (StudyTimeFrame.Week, "1 week"),
    };

    private int _length = 5, _fvgLimit = 5;
    private bool _structure = true, _paintOn = true, _liquidity = true, _fvg = true;
    private StudyTimeFrame _structureFrame = StudyTimeFrame.Chart, _liquidityFrame = StudyTimeFrame.Chart;
    private Dictionary<StudyTimeFrame, Frame>? _frames;
    private List<Frame>? _all;
    private readonly List<Liquidity> _pools = new();
    private readonly List<StudyBox> _gaps = new();
    private readonly PaintbarsDataSeries _paint = new("StructureTrend.Paint", "Trend colors");
    private readonly OptionalValueDataSeries _trend = new("StructureTrend.Trend", "Structure trend of the selected period (+1 / -1)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _signal = new("StructureTrend.Signal", "Break known at this close (+1/-1 BOS, +2/-2 CHoCH)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _sweep = new("StructureTrend.Sweep", "Sweep at this close (+1 low swept, -1 high swept)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _alignment = new("StructureTrend.Alignment", "Bullish minus bearish periods") { VisualType = VisualMode.Hide, ShowZeroValue = true };

    public StructureTrendPanel() { DataSeries[0] = _trend; DataSeries.Add(_signal); DataSeries.Add(_sweep); DataSeries.Add(_alignment); DataSeries.Add(_paint); }

    [Display(Name = "Structure", GroupName = "Structure", Order = 10)]
    public bool ShowStructure { get => _structure; set { _structure = value; RecalculateValues(); } }
    [Display(Name = "Structure period", GroupName = "Structure", Order = 11)]
    public StudyTimeFrame StructureFrame { get => _structureFrame; set { _structureFrame = value; RecalculateValues(); } }
    [Display(Name = "Swing length", GroupName = "Structure", Order = 12), Range(1, 50)]
    public int SwingLength { get => _length; set { _length = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    [Display(Name = "Paint bars by trend", GroupName = "Structure", Order = 13)]
    public bool ColorCandles { get => _paintOn; set { _paintOn = value; RecalculateValues(); } }
    [Display(Name = "Liquidity sweeps", GroupName = "Liquidity", Order = 20)]
    public bool ShowLiquidity { get => _liquidity; set { _liquidity = value; RecalculateValues(); } }
    [Display(Name = "Liquidity period", GroupName = "Liquidity", Order = 21)]
    public StudyTimeFrame LiquidityFrame { get => _liquidityFrame; set { _liquidityFrame = value; RecalculateValues(); } }
    [Display(Name = "Fair value gaps", GroupName = "FVG", Order = 30)]
    public bool ShowFvg { get => _fvg; set { _fvg = value; RecalculateValues(); } }
    [Display(Name = "Maximum active FVG", GroupName = "FVG", Order = 31), Range(1, 30)]
    public int FvgLimit { get => _fvgLimit; set { _fvgLimit = Math.Clamp(value, 1, 30); RecalculateValues(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 40)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 41)] public Color BearColor { get; set; } = StudyPalette.Bear;

    /// <summary>Structure trend of a period from its completed bars (0 when unavailable or not yet broken).</summary>
    public int FrameTrend(StudyTimeFrame period) => _frames != null && _frames.TryGetValue(Usable(period) ? period : StudyTimeFrame.Chart, out var f) ? f.Engine.Trend(0) : 0;

    protected override void ResetState() { _frames = null; _all = null; _pools.Clear(); _gaps.Clear(); }

    protected override void OnCoverageGap(int bar)
    {
        foreach (var g in _gaps) g.End(bar);
        base.OnCoverageGap(bar);
    }

    private bool Usable(StudyTimeFrame period)
    {
        if (period == StudyTimeFrame.Chart) return true;
        if (!StudyMath.CanResolve(ChartInfo?.TimeFrame, period)) return false;
        var duration = StudyMath.Duration(period);
        return duration == TimeSpan.Zero || duration != ChartInfo?.GetTimeFrameTypeChartPeriod();
    }

    private Frame Get(StudyTimeFrame period)
    {
        _frames ??= new();
        if (!Usable(period)) period = StudyTimeFrame.Chart;
        if (!_frames.TryGetValue(period, out var f))
            _frames[period] = f = new Frame(period == StudyTimeFrame.Chart ? null : new HigherTimeframeData(period), SwingLength);
        return f;
    }

    /// <summary>Steps a period at chart bar <paramref name="bar"/>; true when a completed bar of it became known at this close.</summary>
    private bool Advance(Frame f, int bar)
    {
        if (f.Data == null) { f.Engine.Step(Bars, bar, null); return true; }
        f.Data.Update(bar, Candle(bar));
        var closed = f.Data.Closed;
        if (closed.Count == 0 || closed[^1].AvailableBar != bar) return false;
        var c = closed[^1];
        f.Bars.Add(new StudyBar(Bars[c.StartBar].Time, c.Open, c.High, c.Low, c.Close, c.Volume, c.SessionDate, !c.Partial));
        f.Start.Add(c.StartBar);
        f.Engine.Step(f.Bars, f.Bars.Count - 1, null);
        return true;
    }

    private static int ChartBar(Frame f, int frameBar) => f.Data == null ? frameBar : f.Start[frameBar];

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; int signal = 0, sweep = 0;
        var structure = Get(StructureFrame); var liquidity = Get(LiquidityFrame);
        _all ??= Rows.Select(r => Get(r.Period)).Append(structure).Append(liquidity).Distinct().ToList();
        foreach (var f in _all)
        {
            if (!Advance(f, bar)) continue;
            if (f == structure)
                foreach (var br in f.Engine.NewBreaks)
                {
                    signal = br.Direction * (br.Choch ? 2 : 1);
                    if (!ShowStructure) continue;
                    var c = br.Direction > 0 ? BullColor : BearColor; int from = ChartBar(f, br.SwingBar);
                    Scene.Segment(from, bar, bar, br.Level, br.Level, c, 1, br.Choch ? LineDashStyle.Dash : LineDashStyle.Solid);
                    Scene.Tag((from + bar) / 2, bar, br.Level, SwingStudyText.Kind(br), c, br.Direction > 0);
                }
            if (f == liquidity && ShowLiquidity)
                foreach (var (_, sw) in f.Engine.NewSwings)
                {
                    _pools.Add(new Liquidity(ChartBar(f, sw.Bar), bar, sw.Price, sw.High));
                    if (_pools.Count > 100) _pools.RemoveAt(0);
                }
        }
        for (int i = _pools.Count - 1; i >= 0; i--)
        {
            var p = _pools[i]; if (bar <= p.Available) continue;
            bool swept = p.High ? b.High > p.Price && b.Close < p.Price : b.Low < p.Price && b.Close > p.Price;
            if (swept)
            {
                var c = p.High ? BearColor : BullColor; sweep = p.High ? -1 : 1;
                Scene.Segment(p.From, bar, bar, p.Price, p.Price, StudyPalette.A(c, 150), 1, LineDashStyle.Dot);
                Scene.Mark(bar, bar, p.High ? b.High : b.Low, StudyMarkKind.Circle, c, "Sweep", p.High);
                _pools.RemoveAt(i);
            }
            else if (p.High ? b.Close > p.Price : b.Close < p.Price) _pools.RemoveAt(i);
        }
        if (ShowFvg) Gaps(bar);
        int trend = structure.Engine.Trend(0);
        _paint[bar] = ColorCandles && trend != 0 ? StudyPalette.A(trend > 0 ? BullColor : BearColor, 230) : null;
        _trend.Set(bar, trend); _signal.Set(bar, signal); _sweep.Set(bar, sweep);

        Scene.Dash.Clear(); Scene.DashTitle = "Trend alignment";
        int up = 0, down = 0;
        foreach (var (period, name) in Rows)
        {
            if (!Usable(period)) continue;
            var f = Get(period);
            int t = f.Engine.Trend(0);
            if (t > 0) up++; else if (t < 0) down++;
            Scene.Row(name, SwingStudyText.Trend(t), SwingStudyText.TrendColor(t, BullColor, BearColor));
        }
        _alignment.Set(bar, up - down);
        Scene.Row("Alignment", up > down ? "Bullish" : down > up ? "Bearish" : "Mixed", SwingStudyText.TrendColor(up - down, BullColor, BearColor));
        Scene.Row("Bullish / bearish periods", SwingStudyText.Count(up, down));
    }

    private void Gaps(int bar)
    {
        var b = Bars[bar];
        for (int i = _gaps.Count - 1; i >= 0; i--)
        {
            var g = _gaps[i]; bool bull = g.Alert == "Bullish FVG";
            if (bull ? b.Low > g.Bottom : b.High < g.Top) continue;
            g.End(bar); _gaps.RemoveAt(i);
        }
        if (bar < 2 || !AllCausal(bar - 2, bar)) return;
        var a = Bars[bar - 2];
        if (b.Low > a.High) _gaps.Add(Scene.Box(bar - 1, bar, b.Low, a.High, BullColor, "FVG", "Bullish FVG", 35));
        else if (b.High < a.Low) _gaps.Add(Scene.Box(bar - 1, bar, a.Low, b.High, BearColor, "FVG", "Bearish FVG", 35));
        while (_gaps.Count > FvgLimit) { _gaps[0].End(bar); _gaps[0].FillAlpha = 15; _gaps.RemoveAt(0); }
    }
}

// ====================================================================================================== wave extensions

/// <summary>A five-wave impulse (six zigzag pivots) known at <see cref="Available"/>, the confirmation of its last pivot.</summary>
public sealed record WaveImpulse(int Available, IReadOnlyList<StudySwing> Pivots, bool Bullish, int ExtendedWave, decimal Ratio);

/// <summary>Wave-5 targets projected from a wave-4 pivot confirmed at <see cref="Available"/>.</summary>
public sealed record WaveTargets(int From, int Available, decimal Equality, decimal Fibonacci, bool Bullish, bool ExtendedFifthWatch);

[DisplayName("Wave extensions")]
[Category(IndicatorCategories.Structure)]
[Description("Builds a zigzag from confirmed swings and checks five-wave impulses against the rules (wave 2 stays beyond the start, wave 3 is not the shortest, wave 4 does not overlap wave 1, waves 3 and 5 make new extremes). The extended wave (1, 3 or 5) is marked, wave-5 targets (equality with wave 1 and 0.618 of waves 1-3) are projected while wave 4 forms, and a 38.2-61.8 % retracement zone follows an extension. Heuristic; a pattern exists only from the confirmation of its last pivot.")]
public class WaveExtensions : ClosedBarStudy
{
    private int _length = 10, _sub = 4;
    private decimal _ratio = 1.618m, _tolerance = 10;
    private bool _requireSub = true, _targetsOn = true, _watch = true, _zone = true, _labels = true, _pathOn = true;
    private SwingStructureEngine _engine = new(10);
    private readonly List<StudySwing> _zz = new();
    private readonly List<WaveImpulse> _impulses = new();
    private readonly List<StudyBox> _zones = new();
    private WaveTargets? _targets;
    private StudyLine? _eqLine, _fibLine;
    private StudyPath? _path;
    private int _bull, _bear, _e1, _e3, _e5;
    private readonly OptionalValueDataSeries _impulse = new("WaveExtensions.Impulse", "Impulse confirmed at this close (+1 / -1)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _extended = new("WaveExtensions.ExtendedWave", "Extended wave of the impulse confirmed at this close (0, 1, 3, 5)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _eq = new("WaveExtensions.EqualityTarget", "Wave-5 target: equality with wave 1") { VisualType = VisualMode.Hide };
    private readonly OptionalValueDataSeries _fib = new("WaveExtensions.FibonacciTarget", "Wave-5 target: 0.618 of waves 1-3") { VisualType = VisualMode.Hide };

    public WaveExtensions() { DataSeries[0] = _impulse; DataSeries.Add(_extended); DataSeries.Add(_eq); DataSeries.Add(_fib); }

    [Display(Name = "Swing length", GroupName = "Calculation", Order = 10), Range(1, 100)]
    public int SwingLength { get => _length; set { _length = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Sub-wave length", GroupName = "Calculation", Order = 11), Range(1, 50)]
    public int SubLength { get => _sub; set { _sub = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    [Display(Name = "Extension ratio", GroupName = "Calculation", Order = 12), Range(1.05, 5)]
    public decimal ExtensionRatio { get => _ratio; set { _ratio = Math.Clamp(value, 1.05m, 5); RecalculateValues(); } }
    [Display(Name = "Require sub-waves in the extended wave", GroupName = "Calculation", Order = 13)]
    public bool RequireSubwaves { get => _requireSub; set { _requireSub = value; RecalculateValues(); } }
    [Display(Name = "Equality tolerance (%)", GroupName = "Calculation", Order = 14), Range(1, 50)]
    public decimal EqualityTolerance { get => _tolerance; set { _tolerance = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    [Display(Name = "Wave-5 targets", GroupName = "Drawing", Order = 20)]
    public bool ShowTargets { get => _targetsOn; set { _targetsOn = value; RecalculateValues(); } }
    [Display(Name = "Extended wave-5 watch", GroupName = "Drawing", Order = 21)]
    public bool ShowWatch { get => _watch; set { _watch = value; RecalculateValues(); } }
    [Display(Name = "Retracement zone", GroupName = "Drawing", Order = 22)]
    public bool ShowZone { get => _zone; set { _zone = value; RecalculateValues(); } }
    [Display(Name = "Wave labels", GroupName = "Drawing", Order = 23)]
    public bool ShowLabels { get => _labels; set { _labels = value; RecalculateValues(); } }
    [Display(Name = "Wave path", GroupName = "Drawing", Order = 24)]
    public bool ShowPath { get => _pathOn; set { _pathOn = value; RecalculateValues(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 30)] public Color BullColor { get; set; } = Color.FromArgb(255, 0, 176, 79);
    [Display(Name = "Bearish", GroupName = "Colors", Order = 31)] public Color BearColor { get; set; } = Color.FromArgb(255, 156, 27, 65);
    [Display(Name = "Neutral", GroupName = "Colors", Order = 32)] public Color NeutralColor { get; set; } = StudyPalette.Neutral;

    /// <summary>Impulses found so far (newest last, at most 200).</summary>
    [Browsable(false)] public IReadOnlyList<WaveImpulse> Impulses => _impulses;
    /// <summary>Wave-5 targets while wave 4 is the last confirmed pivot; null otherwise.</summary>
    [Browsable(false)] public WaveTargets? Targets => _targets;

    protected override void ResetState()
    {
        _engine = new SwingStructureEngine(SwingLength); _zz.Clear(); _impulses.Clear(); _zones.Clear();
        _targets = null; _eqLine = _fibLine = null; _path = null; _bull = _bear = _e1 = _e3 = _e5 = 0;
    }

    protected override void OnCoverageGap(int bar)
    {
        _eqLine?.End(bar); _fibLine?.End(bar);
        foreach (var z in _zones) z.End(bar);
        base.OnCoverageGap(bar);
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; int impulse = 0, extended = 0;
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var z = _zones[i];
            if (b.Low > z.Top || b.High < z.Bottom) continue;
            z.End(bar); _zones.RemoveAt(i);
            Scene.Mark(bar, bar, b.Close, StudyMarkKind.Circle, NeutralColor, "Zone reached", false);
        }
        _engine.Step(Bars, bar, null);
        bool changed = false;
        foreach (var (_, p) in _engine.NewSwings)
        {
            if (_zz.Count > 0 && _zz[^1].High == p.High)
            {
                if (p.High ? p.Price >= _zz[^1].Price : p.Price <= _zz[^1].Price) { _zz[^1] = p; changed = true; }
                continue;
            }
            _zz.Add(p); changed = true;
            if (_zz.Count > 8) _zz.RemoveAt(0);
            Project(bar);
            if (Impulse(bar) is { } found) { impulse = found.Bullish ? 1 : -1; extended = found.ExtendedWave; }
        }
        if (changed && ShowPath && _zz.Count >= 2)
        {
            if (_path != null) Scene.Paths.Remove(_path);
            _path = new StudyPath(bar, _zz.Select(x => (x.Bar, x.Price)).ToList(), StudyPalette.A(StudyPalette.Muted, 120));
            Scene.Paths.Add(_path);
        }
        _impulse.Set(bar, impulse); _extended.Set(bar, extended);
        if (_targets is { } tg) { _eq.Set(bar, tg.Equality); _fib.Set(bar, tg.Fibonacci); } else { _eq.SetUnavailable(bar); _fib.SetUnavailable(bar); }
        Dashboard();
    }

    /// <summary>With five pivots the last is a wave-4 candidate: project wave-5 targets when waves 1-4 respect the rules.</summary>
    private void Project(int bar)
    {
        _eqLine?.End(bar); _fibLine?.End(bar); _eqLine = _fibLine = null; _targets = null;
        if (_zz.Count < 5) return;
        var w = _zz.Skip(_zz.Count - 5).ToArray();
        bool up = w[1].Price > w[0].Price;
        decimal l1 = Math.Abs(w[1].Price - w[0].Price), l3 = Math.Abs(w[3].Price - w[2].Price);
        bool rules = up ? w[2].Price > w[0].Price && w[3].Price > w[1].Price && w[4].Price > w[1].Price
                        : w[2].Price < w[0].Price && w[3].Price < w[1].Price && w[4].Price < w[1].Price;
        if (!rules) return;
        decimal dir = up ? 1 : -1, larger = Math.Max(l1, l3);
        bool watch = larger > 0 && Math.Abs(l1 - l3) / larger * 100 <= EqualityTolerance;
        _targets = new WaveTargets(w[4].Bar, bar, w[4].Price + dir * l1, w[4].Price + dir * 0.618m * Math.Abs(w[3].Price - w[0].Price), up, watch);
        if (!ShowTargets) return;
        var c = up ? BullColor : BearColor;
        _eqLine = Scene.Level(w[4].Bar, bar, _targets.Equality, c, "Wave 5 = wave 1", "Wave 5 target (equality)", 1, LineDashStyle.Dash);
        _fibLine = Scene.Level(w[4].Bar, bar, _targets.Fibonacci, c, "Wave 5 = 0.618 × waves 1-3", "Wave 5 target (0.618)", 1, LineDashStyle.Dot);
        if (ShowWatch && watch) Scene.Tag(w[4].Bar, bar, w[4].Price, "Watch: possible extended wave 5", StudyPalette.A(NeutralColor, 210), !up);
    }

    /// <summary>With six pivots: a complete impulse when every rule holds; the extended wave is the longest by the ratio.</summary>
    private WaveImpulse? Impulse(int bar)
    {
        if (_zz.Count < 6) return null;
        var w = _zz.Skip(_zz.Count - 6).ToArray();
        bool up = w[1].Price > w[0].Price;
        decimal l1 = Math.Abs(w[1].Price - w[0].Price), l3 = Math.Abs(w[3].Price - w[2].Price), l5 = Math.Abs(w[5].Price - w[4].Price);
        bool rules = up
            ? w[2].Price > w[0].Price && w[3].Price > w[1].Price && w[4].Price > w[1].Price && w[5].Price > w[3].Price
            : w[2].Price < w[0].Price && w[3].Price < w[1].Price && w[4].Price < w[1].Price && w[5].Price < w[3].Price;
        if (!rules || l3 < l1 && l3 < l5) return null;
        decimal[] lengths = { l1, l3, l5 };
        int ext = Array.IndexOf(lengths, lengths.Max());
        decimal next = lengths.Where((_, k) => k != ext).Max(), ratio = next > 0 ? lengths[ext] / next : 0;
        int wave = ratio >= ExtensionRatio ? ext * 2 + 1 : 0;
        if (wave > 0 && RequireSubwaves && Subwaves(w[ext * 2].Bar, w[ext * 2 + 1].Bar) < 3) wave = 0;
        var found = new WaveImpulse(bar, w, up, wave, ratio);
        _impulses.Add(found); if (_impulses.Count > 200) _impulses.RemoveAt(0);
        if (up) _bull++; else _bear++;
        if (wave == 1) _e1++; else if (wave == 3) _e3++; else if (wave == 5) _e5++;
        var c = up ? BullColor : BearColor;
        if (ShowPath) Scene.Paths.Add(new StudyPath(bar, w.Select(x => (x.Bar, x.Price)).ToList(), StudyPalette.A(c, 200), 2));
        if (ShowLabels)
            for (int k = 1; k <= 5; k++)
                Scene.Tag(w[k].Bar, bar, w[k].Price, k == wave ? $"Ext. {k}" : k.ToString(System.Globalization.CultureInfo.InvariantCulture), StudyPalette.A(k == wave ? NeutralColor : c, k == wave ? 220 : 150), w[k].High);
        if (wave > 0)
        {
            Scene.Segment(w[wave - 1].Bar, w[wave].Bar, bar, w[wave - 1].Price, w[wave].Price, NeutralColor, 3);
            if (ShowZone)
            {
                decimal a = w[wave - 1].Price, z = w[wave].Price;
                _zones.Add(Scene.Box(w[5].Bar, bar, z - (z - a) * 0.382m, z - (z - a) * 0.618m, NeutralColor, "Retracement zone", "Extension retracement zone", 28));
                while (_zones.Count > 6) { _zones[0].End(bar); _zones.RemoveAt(0); }
            }
        }
        return found;
    }

    /// <summary>Swings of <see cref="SubLength"/> whose whole window lies inside bars <paramref name="from"/>..<paramref name="to"/>.</summary>
    private int Subwaves(int from, int to)
    {
        int n = 0;
        for (int p = from + SubLength; p + SubLength <= to; p++)
        {
            var (h, l) = StudySwings.Confirm(Bars, p + SubLength, SubLength, SubLength);
            if (h != null) n++;
            if (l != null) n++;
        }
        return n;
    }

    private void Dashboard()
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Wave extensions";
        Scene.Row("Bullish / bearish impulses", SwingStudyText.Count(_bull, _bear));
        Scene.Row("Extended wave 1 / 3 / 5", $"{_e1} / {_e3} / {_e5}");
        if (_impulses.Count > 0)
        {
            var last = _impulses[^1];
            Scene.Row("Last impulse", last.Bullish ? "Bullish" : "Bearish", last.Bullish ? BullColor : BearColor);
            Scene.Row("Extended wave", last.ExtendedWave switch { 1 => "Wave 1", 3 => "Wave 3", 5 => "Wave 5", _ => "None" });
            Scene.Row("Extension ratio", last.Ratio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (_targets is { } tg) Scene.Row("Wave 4 in progress", tg.ExtendedFifthWatch ? "Possible extended wave 5" : "Targets projected");
    }
}
