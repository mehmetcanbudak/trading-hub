// Independent closed-bar implementations; capability reference gouqizi2214730-source/trading-hub @ 97a8521
// (MIT with Commons Clause). Not a port of its code.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Supply and demand zones")]
[Category(IndicatorCategories.Structure)]
[Description("A zone at every confirmed swing high (supply) and swing low (demand), an ATR fraction deep, extended right until a close goes through it; the break is marked. The nearest live zone above and below price is named. Zones exist only from the swing's confirming close.")]
public class SupplyDemandZones : ClosedBarStudy
{
    private int _strength = 8, _perSide = 6; private decimal _zoneAtr = 0.25m;
    private RunningAtr _atr = new(14);
    private readonly List<StudyBox> _live = new();
    private int _supplyLive, _demandLive;
    private readonly OptionalValueDataSeries _signal = new("SupplyDemand.Break", "Zone break direction (+1 supply broken up, -1 demand broken down)") { VisualType = VisualMode.Hide, ShowZeroValue = true };

    public SupplyDemandZones() { DataSeries[0] = _signal; }

    [Display(Name = "Swing strength (bars each side)", GroupName = "Calculation", Order = 10), Range(1, 100)]
    public int SwingStrength { get => _strength; set { _strength = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Zone depth (ATR)", GroupName = "Calculation", Order = 20), Range(0.01, 10)]
    public decimal ZoneAtr { get => _zoneAtr; set { _zoneAtr = Math.Clamp(value, 0.01m, 10); RecalculateValues(); } }
    [Display(Name = "Live zones per side", GroupName = "Calculation", Order = 30), Range(1, 50)]
    public int ZonesPerSide { get => _perSide; set { _perSide = Math.Clamp(value, 1, 50); RecalculateValues(); } }
    [Display(Name = "Supply", GroupName = "Colors", Order = 40)] public Color SupplyColor { get; set; } = StudyPalette.Bear;
    [Display(Name = "Demand", GroupName = "Colors", Order = 41)] public Color DemandColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Break mark", GroupName = "Colors", Order = 42)] public Color BreakColor { get; set; } = StudyPalette.Rose;

    /// <summary>Live zones at the last processed close.</summary>
    [Browsable(false)] public IReadOnlyList<StudyBox> LiveZones => _live;

    protected override void ResetState() { _atr = new(14); _live.Clear(); }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; decimal atr = _atr.Add(b); int signal = 0;
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var zone = _live[i]; bool supply = zone.Alert == "Supply zone";
            if (supply ? b.Close <= zone.Top : b.Close >= zone.Bottom) continue;
            zone.End(bar); _live.RemoveAt(i); signal += supply ? 1 : -1;
            Scene.Mark(bar, bar, supply ? b.High : b.Low, StudyMarkKind.Diamond, BreakColor, null, supply);
        }
        var (high, low) = StudySwings.Confirm(Bars, bar, SwingStrength, SwingStrength);
        if (high is { } h) Add(Scene.Box(h.Bar, bar, h.Price, h.Price - atr * ZoneAtr, SupplyColor, alert: "Supply zone"), true);
        if (low is { } l) Add(Scene.Box(l.Bar, bar, l.Price + atr * ZoneAtr, l.Price, DemandColor, alert: "Demand zone"), false);
        _signal.Set(bar, signal);
        Dashboard(b.Close);
    }

    private void Add(StudyBox zone, bool supply)
    {
        _live.Add(zone);
        // only the newest ZonesPerSide live zones per side stay active (older ones retire, not broken)
        var side = _live.Where(z => z.Alert == zone.Alert).ToList();
        for (int i = 0; i < side.Count - ZonesPerSide; i++) { side[i].End(zone.Available); side[i].FillAlpha = 15; _live.Remove(side[i]); }
    }

    private void Dashboard(decimal close)
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Supply and demand";
        _supplyLive = _live.Count(z => z.Alert == "Supply zone"); _demandLive = _live.Count - _supplyLive;
        Scene.Row("Live supply zones", _supplyLive.ToString(), SupplyColor);
        Scene.Row("Live demand zones", _demandLive.ToString(), DemandColor);
        var above = _live.Where(z => z.Alert == "Supply zone" && z.Bottom > close).OrderBy(z => z.Bottom).FirstOrDefault();
        var below = _live.Where(z => z.Alert == "Demand zone" && z.Top < close).OrderByDescending(z => z.Top).FirstOrDefault();
        if (above != null) Scene.Row("Nearest supply", Price(above.Bottom), SupplyColor);
        if (below != null) Scene.Row("Nearest demand", Price(below.Top), DemandColor);
    }
}

public enum PivotLadderMethod
{
    [Display(Name = "Floor")] Floor,
    [Display(Name = "Traditional")] Traditional,
    [Display(Name = "Fibonacci")] Fibonacci,
    [Display(Name = "Woodie")] Woodie,
    [Display(Name = "Classic")] Classic,
    [Display(Name = "DeMark")] DeMark,
    [Display(Name = "Camarilla")] Camarilla,
}

public enum PivotFloorThird
{
    [Display(Name = "Classic: H + 2 × (P − L) / L − 2 × (H − P)")] Classic,
    [Display(Name = "Range multiple: P ± 2 × range")] RangeMultiple,
}

public enum PivotLadderPeriod
{
    [Display(Name = "Automatic (by chart period)")] Auto,
    [Display(Name = "Session")] Session,
    [Display(Name = "Week")] Week,
    [Display(Name = "Month")] Month,
    [Display(Name = "Quarter")] Quarter,
    [Display(Name = "Year")] Year,
}

[DisplayName("Pivot points (floor, traditional, Fibonacci, Woodie, classic, DeMark, Camarilla)")]
[Category(IndicatorCategories.Structure)]
[Description("Pivot ladders (P with R1–R5/S1–S5 depending on the method) from the open, high, low and close of the previous completed session, week, month, quarter or year on the exchange-session calendar; Automatic picks the period from the chart period. A period's levels become known at the first close of the next period and are drawn from that period's first bar; the first loaded period is skipped because it may be incomplete. Optional marks and a signal when a close crosses the pivot or closes beyond R2/S2. The current period's levels are alertable.")]
public class PivotPointLadders : ClosedBarStudy
{
    private PivotLadderMethod _method = PivotLadderMethod.Traditional;
    private PivotFloorThird _third = PivotFloorThird.Classic;
    private PivotLadderPeriod _period = PivotLadderPeriod.Auto;
    private int _back = 10, _tiers = 5;
    private bool _crosses = true, _labels = true;
    private bool _active, _partial, _hasLevels;
    private long _key;
    private decimal _open, _high, _low, _close, _pivot;
    private decimal? _r2, _s2;
    private readonly List<StudyLine> _current = new();
    private readonly Queue<int> _ladders = new();
    private readonly OptionalValueDataSeries _signal = new("PivotLadders.Signal", "Pivot crossing (+1/-1 close crosses the pivot up/down, +2/-2 close crosses beyond R2/S2)") { VisualType = VisualMode.Hide, ShowZeroValue = true };

    public PivotPointLadders() { DataSeries[0] = _signal; }

    [Display(Name = "Method", GroupName = "Calculation", Order = 10)]
    public PivotLadderMethod Method { get => _method; set { _method = value; RecalculateValues(); } }
    [Display(Name = "Floor R3 / S3", GroupName = "Calculation", Order = 11)]
    public PivotFloorThird FloorThird { get => _third; set { _third = value; RecalculateValues(); } }
    [Display(Name = "Period", GroupName = "Calculation", Order = 12)]
    public PivotLadderPeriod Period { get => _period; set { _period = value; RecalculateValues(); } }
    [Display(Name = "Periods drawn back", GroupName = "Calculation", Order = 13), Range(1, 60)]
    public int PeriodsBack { get => _back; set { _back = Math.Clamp(value, 1, 60); RecalculateValues(); } }
    [Display(Name = "Highest level shown (R/S 1–5)", GroupName = "Levels", Order = 20), Range(1, 5)]
    public int MaxTier { get => _tiers; set { _tiers = Math.Clamp(value, 1, 5); RecalculateValues(); } }
    [Display(Name = "Level names", GroupName = "Levels", Order = 21)]
    public bool ShowLabels { get => _labels; set { _labels = value; RecalculateValues(); } }
    [Display(Name = "Mark pivot and R2/S2 crossings", GroupName = "Levels", Order = 22)]
    public bool ShowCrosses { get => _crosses; set { _crosses = value; RecalculateValues(); } }
    [Display(Name = "Pivot", GroupName = "Colors", Order = 30)] public Color PivotColor { get; set; } = StudyPalette.Amber;
    [Display(Name = "Resistances", GroupName = "Colors", Order = 31)] public Color ResistanceColor { get; set; } = StudyPalette.Bear;
    [Display(Name = "Supports", GroupName = "Colors", Order = 32)] public Color SupportColor { get; set; } = StudyPalette.Bull;

    [Browsable(false)] public override string Status => "Levels of a completed period appear at the first close of the next period; the first loaded period is skipped";
    /// <summary>The live levels of the current period.</summary>
    [Browsable(false)] public IReadOnlyList<StudyLine> CurrentLevels => _current;

    /// <summary>
    /// The pivot ladder of one completed period (<paramref name="open"/>, <paramref name="high"/>, <paramref name="low"/>,
    /// <paramref name="close"/>) and the first open of the next period (<paramref name="nextOpen"/>, used by Woodie), in
    /// the order P, R1, S1, R2, S2, …
    /// </summary>
    public static List<(string Name, decimal Value)> Ladder(PivotLadderMethod method, decimal open, decimal high, decimal low, decimal close, decimal nextOpen, PivotFloorThird third = PivotFloorThird.Classic)
    {
        decimal r = high - low, p = (high + low + close) / 3;
        switch (method)
        {
            case PivotLadderMethod.Floor:
                return new()
                {
                    ("P", p), ("R1", 2 * p - low), ("S1", 2 * p - high), ("R2", p + r), ("S2", p - r),
                    ("R3", third == PivotFloorThird.Classic ? high + 2 * (p - low) : p + 2 * r), ("S3", third == PivotFloorThird.Classic ? low - 2 * (high - p) : p - 2 * r),
                };
            case PivotLadderMethod.Fibonacci:
                return new() { ("P", p), ("R1", p + 0.382m * r), ("S1", p - 0.382m * r), ("R2", p + 0.618m * r), ("S2", p - 0.618m * r), ("R3", p + r), ("S3", p - r) };
            case PivotLadderMethod.Woodie:
            {
                decimal w = (high + low + 2 * nextOpen) / 4, r3 = high + 2 * (w - low), s3 = low - 2 * (high - w);
                return new() { ("P", w), ("R1", 2 * w - low), ("S1", 2 * w - high), ("R2", w + r), ("S2", w - r), ("R3", r3), ("S3", s3), ("R4", r3 + r), ("S4", s3 - r) };
            }
            case PivotLadderMethod.Classic:
                return new() { ("P", p), ("R1", 2 * p - low), ("S1", 2 * p - high), ("R2", p + r), ("S2", p - r), ("R3", p + 2 * r), ("S3", p - 2 * r), ("R4", p + 3 * r), ("S4", p - 3 * r) };
            case PivotLadderMethod.DeMark:
            {
                decimal x = close < open ? high + 2 * low + close : close > open ? 2 * high + low + close : high + low + 2 * close;
                return new() { ("P", x / 4), ("R1", x / 2 - low), ("S1", x / 2 - high) };
            }
            case PivotLadderMethod.Camarilla:
            {
                var levels = new List<(string Name, decimal Value)>
                {
                    ("P", p), ("R1", close + 1.1m * r / 12), ("S1", close - 1.1m * r / 12), ("R2", close + 1.1m * r / 6), ("S2", close - 1.1m * r / 6),
                    ("R3", close + 1.1m * r / 4), ("S3", close - 1.1m * r / 4), ("R4", close + 1.1m * r / 2), ("S4", close - 1.1m * r / 2),
                };
                // R5 = H / L × C (multiplied first so whole-number inputs stay exact); S5 mirrors it around the close
                if (low != 0) { decimal r5 = high * close / low; levels.Add(("R5", r5)); levels.Add(("S5", close - (r5 - close))); }
                return levels;
            }
            default:
                return new()
                {
                    ("P", p), ("R1", 2 * p - low), ("S1", 2 * p - high), ("R2", p + r), ("S2", p - r), ("R3", 2 * p + (high - 2 * low)), ("S3", 2 * p - (2 * high - low)),
                    ("R4", 3 * p + (high - 3 * low)), ("S4", 3 * p - (3 * high - low)), ("R5", 4 * p + (high - 4 * low)), ("S5", 4 * p - (4 * high - low)),
                };
        }
    }

    protected override void ResetState()
    {
        _active = _partial = _hasLevels = false; _key = 0; _open = _high = _low = _close = _pivot = 0; _r2 = _s2 = null;
        _current.Clear(); _ladders.Clear();
    }

    /// <summary>A bar without causal coverage ends the live ladder; the period restarts (and is skipped unless it begins exactly at its opening).</summary>
    protected override void OnCoverageGap(int bar) { EndCurrent(bar); _active = false; }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; var calendar = Calendar(); long key = b.Key(calendar);
        if (!_active) Begin(b, key, !StudyMath.IsPeriodOpening(Candle(bar), Frame(calendar)));
        else if (key != _key)
        {
            // the previous period is complete: its ladder is known now, at the first close of the new period
            EndCurrent(bar);
            if (!_partial) Publish(bar, b.Open);
            Begin(b, key, false);
        }
        else { _high = Math.Max(_high, b.High); _low = Math.Min(_low, b.Low); _close = b.Close; }
        _signal.Set(bar, _hasLevels && bar > 0 && Bars[bar - 1].Causal ? Cross(bar) : 0);
        Dashboard(b.Close);
    }

    private void Begin(StudyBar b, long key, bool partial)
    {
        _active = true; _key = key; _partial = partial; _open = b.Open; _high = b.High; _low = b.Low; _close = b.Close;
    }

    private void EndCurrent(int bar)
    {
        foreach (var line in _current) { line.To = bar - 1; line.EndAvailable = bar; }
        _current.Clear(); _hasLevels = false;
    }

    private void Publish(int bar, decimal nextOpen)
    {
        _r2 = _s2 = null;
        foreach (var (name, value) in Ladder(Method, _open, _high, _low, _close, nextOpen, FloorThird))
        {
            if (name == "P") _pivot = value; else if (name == "R2") _r2 = value; else if (name == "S2") _s2 = value;
            int tier = name == "P" ? 0 : name[1] - '0';
            if (tier > MaxTier) continue;
            var color = name == "P" ? PivotColor : name[0] == 'R' ? ResistanceColor : SupportColor;
            _current.Add(Scene.Level(bar, bar, value, color, ShowLabels ? name : null, $"Pivot {name}", name == "P" ? 2 : 1));
        }
        _hasLevels = true;
        _ladders.Enqueue(bar);
        while (_ladders.Count > PeriodsBack) { int old = _ladders.Dequeue(); Scene.Lines.RemoveAll(l => l.From == old); }
    }

    private int Cross(int bar)
    {
        var b = Bars[bar]; decimal prev = Bars[bar - 1].Close, close = b.Close; int signal = 0;
        if (prev <= _pivot && close > _pivot) { signal = 1; if (ShowCrosses) Scene.Mark(bar, bar, b.Low, StudyMarkKind.TriangleUp, PivotColor, null, false); }
        else if (prev >= _pivot && close < _pivot) { signal = -1; if (ShowCrosses) Scene.Mark(bar, bar, b.High, StudyMarkKind.TriangleDown, PivotColor); }
        if (_r2 is { } r2 && prev <= r2 && close > r2) { signal = 2; if (ShowCrosses) Scene.Mark(bar, bar, b.Low, StudyMarkKind.Circle, ResistanceColor, "R2", false); }
        if (_s2 is { } s2 && prev >= s2 && close < s2) { signal = -2; if (ShowCrosses) Scene.Mark(bar, bar, b.High, StudyMarkKind.Circle, SupportColor, "S2"); }
        return signal;
    }

    private void Dashboard(decimal close)
    {
        Scene.Dash.Clear(); Scene.DashTitle = null;
        if (!_hasLevels) return;
        Scene.DashTitle = "Pivot points";
        Scene.Row("Method", Method switch
        {
            PivotLadderMethod.Floor => "Floor", PivotLadderMethod.Fibonacci => "Fibonacci", PivotLadderMethod.Woodie => "Woodie", PivotLadderMethod.Classic => "Classic",
            PivotLadderMethod.DeMark => "DeMark", PivotLadderMethod.Camarilla => "Camarilla", _ => "Traditional",
        });
        Scene.Row("Pivot", Price(_pivot), PivotColor);
        Scene.Row("Price vs pivot", close >= _pivot ? "Above" : "Below", close >= _pivot ? SupportColor : ResistanceColor);
        if (_r2 is { } r2 && _s2 is { } s2) Scene.Row("R2 / S2", $"{Price(r2)} / {Price(s2)}");
    }

    private StudyCalendar Calendar() => Period switch
    {
        PivotLadderPeriod.Session => StudyCalendar.Day,
        PivotLadderPeriod.Week => StudyCalendar.Week,
        PivotLadderPeriod.Month => StudyCalendar.Month,
        PivotLadderPeriod.Quarter => StudyCalendar.Quarter,
        PivotLadderPeriod.Year => StudyCalendar.Year,
        _ => (ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero) switch
        {
            var d when d <= TimeSpan.FromMinutes(15) => StudyCalendar.Day,
            var d when d < TimeSpan.FromDays(1) => StudyCalendar.Week,
            var d when d < TimeSpan.FromDays(7) => StudyCalendar.Month,
            _ => StudyCalendar.Year,
        },
    };

    private static StudyTimeFrame Frame(StudyCalendar calendar) => calendar switch
    {
        StudyCalendar.Week => StudyTimeFrame.Week,
        StudyCalendar.Month => StudyTimeFrame.Month,
        StudyCalendar.Quarter => StudyTimeFrame.Quarter,
        StudyCalendar.Year => StudyTimeFrame.Year,
        _ => StudyTimeFrame.Day,
    };
}

public enum LineBreakCloses
{
    [Display(Name = "Chart bars")] Chart,
    [Display(Name = "15 minutes")] Minutes15,
    [Display(Name = "1 hour")] Hour,
    [Display(Name = "4 hours")] Hours4,
    [Display(Name = "Session")] Session,
}

/// <summary>One line-break line: drawn from the bar whose close made it (<see cref="From"/>), known at <see cref="Available"/>.</summary>
public readonly record struct LineBreakLine(int From, int Available, decimal Open, decimal Close, bool Up)
{
    public decimal Top => Math.Max(Open, Close);
    public decimal Bottom => Math.Min(Open, Close);
}

[DisplayName("Line break")]
[Category(IndicatorCategories.Structure)]
[Description("N-line break on closes (chart closes, or 15-minute, 1-hour, 4-hour or session closes): a close beyond the last line extends the trend, a close beyond the extreme of the last N lines reverses it. A higher-frame close is known only at the first chart bar of the next block, so its line, marks and signal appear there. Shows line bodies, the continue and reverse trigger levels (current ones alertable), reversal and extended-run marks, optional bar colors, a mini line-break chart right of the last bar and a dashboard.")]
public class LineBreak : ClosedBarStudy
{
    private int _count = 3, _extended = 8, _chartLines = 30;
    private LineBreakCloses _source = LineBreakCloses.Chart;
    private bool _showLines = true, _markers = true, _reversalLabels = true, _triggers = true, _paintBars;
    private readonly List<LineBreakLine> _lines = new();
    private int _run; private bool _hasBlock; private long _block; private decimal _blockOpen;
    private StudyBox? _body; private StudyLine? _continue, _reverse;
    private readonly OptionalValueDataSeries _trend = new("LineBreak.Trend", "Line break trend (+1 up, -1 down)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _signal = new("LineBreak.Signal", "Line break reversal (+1 bullish, -1 bearish) at the confirming bar") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly PaintbarsDataSeries _paint = new("LineBreak.Paint", "Line break bar colors");

    public LineBreak() { DataSeries[0] = _trend; DataSeries.Add(_signal); DataSeries.Add(_paint); }

    [Display(Name = "Lines to reverse", GroupName = "Calculation", Order = 10), Range(1, 10)]
    public int LineCount { get => _count; set { _count = Math.Clamp(value, 1, 10); RecalculateValues(); } }
    [Display(Name = "Closes from", GroupName = "Calculation", Order = 11)]
    public LineBreakCloses CloseSource { get => _source; set { _source = value; RecalculateValues(); } }
    [Display(Name = "Extended run (lines)", GroupName = "Calculation", Order = 12), Range(2, 50)]
    public int ExtendedRun { get => _extended; set { _extended = Math.Clamp(value, 2, 50); RecalculateValues(); } }
    [Display(Name = "Line bodies", GroupName = "Drawing", Order = 20)]
    public bool ShowLines { get => _showLines; set { _showLines = value; RecalculateValues(); } }
    [Display(Name = "Reversal and extended-run marks", GroupName = "Drawing", Order = 21)]
    public bool ShowMarkers { get => _markers; set { _markers = value; RecalculateValues(); } }
    [Display(Name = "Reversal labels", GroupName = "Drawing", Order = 22)]
    public bool ShowReversalLabels { get => _reversalLabels; set { _reversalLabels = value; RecalculateValues(); } }
    [Display(Name = "Continue and reverse triggers", GroupName = "Drawing", Order = 23)]
    public bool ShowTriggers { get => _triggers; set { _triggers = value; RecalculateValues(); } }
    [Display(Name = "Color price bars by trend", GroupName = "Drawing", Order = 24)]
    public bool PaintBars { get => _paintBars; set { _paintBars = value; RecalculateValues(); } }
    [Display(Name = "Mini line-break chart right of the last bar", GroupName = "Drawing", Order = 25)]
    public bool ShowLineChart { get; set; } = true;
    [Display(Name = "Mini chart lines", GroupName = "Drawing", Order = 26), Range(5, 100)]
    public int ChartLines { get => _chartLines; set { _chartLines = Math.Clamp(value, 5, 100); RecalculateValues(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 30)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 31)] public Color BearColor { get; set; } = StudyPalette.Bear;

    [Browsable(false)] public override string Status => "Closed bars only; a higher-frame close is known at the first chart bar of the next block";
    /// <summary>The most recent lines (at least the last <see cref="LineCount"/> and <see cref="ChartLines"/>).</summary>
    [Browsable(false)] public IReadOnlyList<LineBreakLine> RecentLines => _lines;

    protected override void ResetState()
    {
        _lines.Clear(); _run = 0; _hasBlock = false; _block = 0; _blockOpen = 0; _body = null; _continue = _reverse = null;
    }

    protected override void OnCoverageGap(int bar) { EndLive(bar - 1, bar); ResetState(); }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; int signal = 0;
        StudyTimeFrame? frame = _source switch
        {
            LineBreakCloses.Minutes15 => StudyTimeFrame.Minutes15,
            LineBreakCloses.Hour => StudyTimeFrame.Hour,
            LineBreakCloses.Hours4 => StudyTimeFrame.Hours4,
            LineBreakCloses.Session => StudyTimeFrame.Day,
            _ => null,
        };
        // a frame that the chart period cannot build exactly falls back to chart closes
        if (frame is { } f && !StudyMath.CanResolve(ChartInfo?.TimeFrame, f)) frame = null;
        if (frame is not { } block) signal = Close(bar, bar, b.Close, b.Open);
        else
        {
            long key = block == StudyTimeFrame.Day ? b.Key(StudyCalendar.Day) : StudyMath.PeriodKey(Candle(bar), block);
            // the previous block's close is known only now, at the first chart bar of the next block
            if (_hasBlock && key != _block) signal = Close(bar - 1, bar, Bars[bar - 1].Close, _blockOpen);
            if (!_hasBlock || key != _block) { _hasBlock = true; _block = key; _blockOpen = b.Open; }
        }
        int trend = _lines.Count == 0 ? 0 : _lines[^1].Up ? 1 : -1;
        _trend.Set(bar, trend, trend != 0);
        _signal.Set(bar, signal);
        _paint[bar] = PaintBars && trend != 0 ? (trend > 0 ? BullColor : BearColor) : null;
        Dashboard();
    }

    /// <summary>Applies one counted close; returns +1/-1 on a reversal.</summary>
    private int Close(int from, int available, decimal close, decimal open)
    {
        if (_lines.Count == 0) { Push(new(from, available, open, close, close >= open)); _run = 1; return 0; }
        var last = _lines[^1];
        if (last.Up ? close > last.Close : close < last.Close)
        {
            Push(new(from, available, last.Close, close, last.Up)); _run++;
            if (_run == ExtendedRun && ShowMarkers)
                Scene.Mark(from, available, last.Up ? Bars[from].Low : Bars[from].High, last.Up ? StudyMarkKind.TriangleUp : StudyMarkKind.TriangleDown,
                    last.Up ? BullColor : BearColor, _run.ToString(System.Globalization.CultureInfo.InvariantCulture), !last.Up);
            return 0;
        }
        decimal extreme = ReverseLevel();
        if (last.Up ? close >= extreme : close <= extreme) return 0;
        bool up = !last.Up;
        Push(new(from, available, up ? last.Top : last.Bottom, close, up)); _run = 1;
        decimal at = up ? Bars[from].Low : Bars[from].High; var color = up ? BullColor : BearColor;
        if (ShowMarkers) Scene.Mark(from, available, at, StudyMarkKind.Circle, color, null, !up);
        if (ShowReversalLabels) Scene.Tag(from, available, at, up ? "Bullish reversal" : "Bearish reversal", color, !up);
        return up ? 1 : -1;
    }

    /// <summary>The close that reverses the current trend: the extreme of the last <see cref="LineCount"/> lines.</summary>
    private decimal ReverseLevel()
    {
        bool up = _lines[^1].Up; decimal level = up ? decimal.MaxValue : decimal.MinValue;
        for (int i = Math.Max(0, _lines.Count - LineCount); i < _lines.Count; i++) level = up ? Math.Min(level, _lines[i].Bottom) : Math.Max(level, _lines[i].Top);
        return level;
    }

    private void Push(LineBreakLine line)
    {
        EndLive(line.From - 1, line.Available);
        _lines.Add(line);
        int cap = Math.Max(LineCount, ChartLines) + 1;
        if (_lines.Count > cap) _lines.RemoveRange(0, _lines.Count - cap);
        var color = line.Up ? BullColor : BearColor;
        if (ShowLines) _body = Scene.Box(line.From, line.Available, line.Top, line.Bottom, color, fillAlpha: 25);
        if (!ShowTriggers) return;
        _continue = Scene.Level(line.From, line.Available, line.Close, color, alert: "Line break continue", style: LineDashStyle.Dot);
        _reverse = Scene.Level(line.From, line.Available, ReverseLevel(), line.Up ? BearColor : BullColor, alert: "Line break reverse", style: LineDashStyle.Dash);
    }

    private void EndLive(int to, int available)
    {
        static void End(StudyShape? shape, int to, int available) { if (shape != null) { shape.To = to; shape.EndAvailable = available; } }
        End(_body, to, available); End(_continue, to, available); End(_reverse, to, available);
        _body = null; _continue = _reverse = null;
    }

    private void Dashboard()
    {
        Scene.Dash.Clear(); Scene.DashTitle = null;
        if (_lines.Count == 0) return;
        var last = _lines[^1]; var color = last.Up ? BullColor : BearColor; var opposite = last.Up ? BearColor : BullColor;
        Scene.DashTitle = "Line break";
        Scene.Row("Lines to reverse", LineCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Scene.Row("Trend", last.Up ? "Up" : "Down", color);
        Scene.Row("Consecutive lines", _run.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Scene.Row(last.Up ? "Continue above" : "Continue below", Price(last.Close), color);
        Scene.Row("Reverses at", Price(ReverseLevel()), opposite);
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        var info = ChartInfo;
        if (info == null || _lines.Count == 0) return;
        var font = FootprintStudyData.SmallFont;
        if (_continue is { } next && _reverse is { } turn)
        {
            foreach (var (line, text) in new[] { (next, _lines[^1].Up ? "Continue above" : "Continue below"), (turn, "Reverses at") })
            {
                string t = UiText.Tr(text) + " " + Price(line.Price); var size = ctx.MeasureString(t, font);
                ctx.DrawString(t, font, line.Color, region.Right - size.Width - 6, info.GetYByPrice(line.Price, false) - size.Height - 1);
            }
        }
        if (!ShowLineChart) return;
        int lastBar = Bars.Count - 1, width = Math.Max(3, info.PriceChartContainer.BarsWidth), first = Math.Max(0, _lines.Count - ChartLines);
        for (int k = first; k < _lines.Count; k++)
        {
            int x = info.GetXByBar(lastBar + 3 + k - first);
            if (x + width > region.Right) break;
            var line = _lines[k]; var color = line.Up ? BullColor : BearColor;
            int y0 = info.GetYByPrice(line.Top, false), y1 = info.GetYByPrice(line.Bottom, false);
            var rect = new Rectangle(x, Math.Min(y0, y1), width, Math.Max(1, Math.Abs(y1 - y0)));
            ctx.FillRectangle(StudyPalette.A(color, 170), rect);
            ctx.DrawRectangle(new RenderPen(color, 1), rect);
        }
    }
}
