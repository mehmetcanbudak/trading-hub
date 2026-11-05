// Independent daily containment heuristic; KVP capability reference gouqizi2214730-source/trading-hub
// @ 50220ba91613161685f84384825d855cb4dbeb76 (MIT with Commons Clause).
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Double inside day")]
[Category(IndicatorCategories.Levels)]
[Description("Exchange-session mother day plus two completed inside days. Configurable body/range containment, nested/mother containment and chart/daily close breakout. No pattern is available before the second inside day completes; expiry is counted in observed sessions.")]
public class DoubleInsideDay : Indicator
{
    public enum Containment { Range, Body }
    public enum Nesting { BothInMother, Nested }
    public enum BreakoutConfirmation { ChartClose, DailyClose }
    public readonly record struct Day(int StartBar, int EndBar, DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, DateTime AvailableAt = default, bool Partial = false);
    public readonly record struct Pattern(int SourceBar, int AvailableBar, decimal High, decimal Low, int Session, int EndBar, int State, int EndAvailableBar = -1)
    {
        public int StateAt(int bar) => EndAvailableBar >= 0 && EndAvailableBar <= bar ? State : 0;
    }
    public const int CoverageGapState = 3;
    private readonly record struct Snapshot(Day Current, Day? Prior, Day? Mother, int Session, int LastClosed);
    private readonly List<Snapshot> _rows = new(); private readonly List<Pattern> _patterns = new(); private readonly List<(int Index, Pattern Value)> _undo = new();
    private readonly IReadOnlyList<Pattern> _patternView;
    private readonly OptionalValueDataSeries _signal = new("DoubleInsideDay.Signal", "Confirmed breakout (+1 / -1)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private int _added, _expiry = 10; private decimal _motherMultiple; private Containment _containment; private Nesting _nesting; private BreakoutConfirmation _confirmation;
    private Containment _breakoutEdges;
    public DoubleInsideDay() : base(true) { _patternView = _patterns.AsReadOnly(); DataSeries[0] = _signal; EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final); }
    [Display(Name = "Containment", GroupName = "Pattern")] public Containment Contain { get => _containment; set { _containment = value; RecalculateValues(); } }
    [Display(Name = "Nesting", GroupName = "Pattern")] public Nesting Nest { get => _nesting; set { _nesting = value; RecalculateValues(); } }
    [Display(Name = "Breakout edges", GroupName = "Pattern")] public Containment BreakoutEdges { get => _breakoutEdges; set { _breakoutEdges = value; RecalculateValues(); } }
    [Display(Name = "Mother body minimum multiple (0 = off)", GroupName = "Pattern"), Range(0, 20)] public decimal MotherBodyMultiple { get => _motherMultiple; set { _motherMultiple = Math.Clamp(value, 0, 20); RecalculateValues(); } }
    [Display(Name = "Breakout confirmation", GroupName = "Pattern")] public BreakoutConfirmation Confirmation { get => _confirmation; set { _confirmation = value; RecalculateValues(); } }
    [Display(Name = "Expiry in observed sessions", GroupName = "Pattern"), Range(1, 100)] public int ExpirySessions { get => _expiry; set { _expiry = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Show ended patterns", GroupName = "Visualization")] public bool ShowEnded { get; set; } = true;
    [Browsable(false)] public IReadOnlyList<Pattern> Patterns => _patternView;
    [Browsable(false)] public string Status => "Only complete, contained session days form patterns; incompatible or missing confirmation data retires rather than confirms a pattern";
    protected override void OnRecalculate() { _rows.Clear(); _patterns.Clear(); _undo.Clear(); _added = 0; }
    private static (decimal Low, decimal High) Edges(Day day, Containment mode) => mode == Containment.Range ? (day.Low, day.High) : (Math.Min(day.Open, day.Close), Math.Max(day.Open, day.Close));
    private bool Inside(Day inner, Day outer) { var a = Edges(inner, Contain); var b = Edges(outer, Contain); return a.Low >= b.Low && a.High <= b.High; }
    private static bool Complete(Day day, DateTime availableThrough) => !day.Partial && day.AvailableAt <= availableThrough;
    protected override void OnCalculate(int bar, decimal value)
    {
        int target = bar;
        if (bar < _rows.Count - 1) { OnRecalculate(); Clear(); }
        if (_rows.Count == bar + 1)
        {
            _rows.RemoveAt(bar); if (_added > 0) _patterns.RemoveRange(_patterns.Count - _added, _added);
            foreach (var old in _undo) _patterns[old.Index] = old.Value;
        }
        for (int b = _rows.Count; b <= target; b++) Step(b);
    }
    private void Step(int b)
    {
        _undo.Clear(); _added = 0;
        if (_patterns.Count > 100) _patterns.RemoveRange(0, _patterns.Count - 100);
        var c = GetCandle(b); var date = c.SessionDate; bool first = b == 0;
        Snapshot prior = first ? default : _rows[b - 1]; bool newDay = first || date != prior.Current.Date;
        int session = first ? 0 : prior.Session + (newDay ? 1 : 0);
        Day? previous = newDay && !first ? prior.Current : prior.Prior, mother = newDay && !first ? prior.Prior : prior.Mother;
        bool partial = !StudyMath.IsContained(c, StudyTimeFrame.Day) || (c.Quality & TradingHub.Market.DataQuality.Partial) != 0 || (StudyMath.Coverage(c) & StudyCoverage.Incomplete) != 0 || first && !StudyMath.IsPeriodOpening(c, StudyTimeFrame.Day);
        var current = newDay ? new Day(b, b, date, c.Open, c.High, c.Low, c.Close, c.AvailableAt, partial)
            : prior.Current with { EndBar = b, High = Math.Max(prior.Current.High, c.High), Low = Math.Min(prior.Current.Low, c.Low), Close = c.Close,
                AvailableAt = c.AvailableAt > prior.Current.AvailableAt ? c.AvailableAt : prior.Current.AvailableAt, Partial = prior.Current.Partial || partial };
        if (newDay && !first && prior.Mother is { } m && prior.Prior is { } inside &&
            Complete(m, c.Time) && Complete(inside, c.Time) && Complete(prior.Current, c.Time) &&
            Inside(inside, m) && Inside(prior.Current, Nest == Nesting.Nested ? inside : m) &&
            Math.Abs(m.Close - m.Open) >= MotherBodyMultiple * Math.Max(Math.Abs(inside.Close - inside.Open), Math.Abs(prior.Current.Close - prior.Current.Open)))
        {
            var edges = Edges(m, BreakoutEdges);
            _patterns.Add(new(m.StartBar, b, edges.High, edges.Low, session, -1, 0)); _added++;
        }
        int closed = c.IsCandleFinished ? b : b - 1; decimal signal = 0;
        for (int i = 0; i < _patterns.Count; i++)
        {
            var pattern = _patterns[i]; if (pattern.State != 0) continue;
            int state = 0, end = b;
            if (session - pattern.Session >= ExpirySessions) state = 2;
            else if (Confirmation == BreakoutConfirmation.DailyClose && newDay && !first && prior.Current.EndBar >= pattern.AvailableBar)
            {
                if (!Complete(prior.Current, c.Time)) state = CoverageGapState;
                else if (prior.Current.Close > pattern.High) state = 1;
                else if (prior.Current.Close < pattern.Low) state = -1;
                end = prior.Current.EndBar;
            }
            else if (Confirmation == BreakoutConfirmation.ChartClose)
                for (int t = Math.Max(pattern.AvailableBar, first ? 0 : prior.LastClosed + 1); t <= closed; t++)
                {
                    var candle = GetCandle(t);
                    if (!candle.CanCalculateCausally || (candle.Quality & TradingHub.Market.DataQuality.Partial) != 0 || (StudyMath.Coverage(candle) & StudyCoverage.Incomplete) != 0) state = CoverageGapState;
                    else if (candle.Close > pattern.High) state = 1;
                    else if (candle.Close < pattern.Low) state = -1;
                    if (state != 0) { end = t; break; }
                }
            if (state == 0) continue;
            if (i < _patterns.Count - _added) _undo.Add((i, pattern));
            _patterns[i] = pattern with { State = state, EndBar = end, EndAvailableBar = b }; if (state == 1 || state == -1) signal += state;
        }
        bool ready = prior.Mother.HasValue && prior.Prior.HasValue && closed >= 0;
        bool available = ready && (Confirmation == BreakoutConfirmation.DailyClose
            ? !newDay || Complete(prior.Current, c.Time)
            : GetCandle(closed).CanCalculateCausally && (GetCandle(closed).Quality & TradingHub.Market.DataQuality.Partial) == 0 && (StudyMath.Coverage(GetCandle(closed)) & StudyCoverage.Incomplete) == 0);
        _signal.Set(b, signal, available); _rows.Add(new(current, previous, mother, session, closed));
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        foreach (var pattern in _patterns)
        {
            int state = pattern.StateAt(LastVisibleBarNumber);
            if (state != 0 && !ShowEnded || pattern.AvailableBar > LastVisibleBarNumber) continue;
            int start = Math.Max(FirstVisibleBarNumber, pattern.AvailableBar), end = state == 0 ? LastVisibleBarNumber : Math.Min(LastVisibleBarNumber, pattern.EndBar);
            if (end < start) continue;
            int x = ChartInfo.GetXByBar(start), right = ChartInfo.GetXByBar(end + 1), top = ChartInfo.GetYByPrice(pattern.High), bottom = ChartInfo.GetYByPrice(pattern.Low);
            Color color = state == 1 ? Color.LimeGreen : state == -1 ? Color.IndianRed : Color.Gold;
            context.FillRectangle(Color.FromArgb(30, color), new Rectangle(x, Math.Min(top, bottom), Math.Max(1, right - x), Math.Max(1, Math.Abs(bottom - top))));
            context.DrawString($"Double inside day ({(state == 0 ? "active" : state == 2 ? "expired" : state == CoverageGapState ? "source coverage gap" : "confirmed breakout")})", FootprintStudyData.AnnotationFont, color, x + 2, Math.Min(top, bottom));
        }
    }
}
