// Independent OHLC/pivot heuristics, not a port of VPFM or ambiguously attributed SMC source.
// KVP capability reference gouqizi2214730-source/trading-hub @ 50220ba91613161685f84384825d855cb4dbeb76 (MIT with Commons Clause).
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

public enum StructureEventKind { SwingHigh, SwingLow, Bos, Choch, EqualHigh, EqualLow, Sweep, Cisd }
public enum StructureZoneKind { OrderBlock, Breaker, FairValueGap, Liquidity }
public readonly record struct StructureEvent(StructureEventKind Kind, int SourceBar, int AvailableBar, int Direction, decimal Price, int RelatedBar);
public readonly record struct StructureZone(StructureZoneKind Kind, int SourceBar, int AvailableBar, int Direction, decimal Low, decimal High, int EndBar, bool Invalidated, int EndAvailableBar = -1, bool CoverageGap = false)
{
    public bool IsActiveAt(int bar) => bar >= AvailableBar && (EndAvailableBar < 0 || bar < EndAvailableBar);
}

[DisplayName("Causal SMC / ICT structure heuristics")]
[Category(IndicatorCategories.Levels)]
[Description("Optional closed-pivot swings, BOS/CHoCH, opposite-candle order blocks, breakers, three-candle FVG, equal-high/low liquidity, sweeps and CISD. These are configurable OHLC heuristics, not observed institutional orders. Retrospective drawing never changes earlier strategy values.")]
public class MarketStructure : Indicator
{
    public enum GapMitigation { WickTouch, FullWickFill, CloseThrough }
    private readonly record struct Pivot(int Bar, decimal Price, bool Exists, bool Broken);
    private readonly record struct Run(int Start, int Length, decimal Open);
    private readonly record struct State(int Processed, int Trend, Pivot High, Pivot Low, int SweepHigh, int SweepLow, Run Bull, Run Bear, bool Eligible = true);
    private State _state = new(-1, 0, default, default, -1, -1, default, default), _saved;
    private readonly List<StructureEvent> _events = new(); private readonly List<StructureZone> _zones = new(); private readonly List<(int Index, StructureZone Zone)> _undo = new();
    private readonly IReadOnlyList<StructureEvent> _eventView; private readonly IReadOnlyList<StructureZone> _zoneView;
    private int _beforeEvents, _beforeZones, _lastRequested = -1, _radius = 5, _obLookback = 20, _minimumRun = 2, _retention = 500;
    private decimal _equalTicks = 1, _minimumGapTicks; private GapMitigation _mitigation = GapMitigation.FullWickFill;
    private readonly OptionalValueDataSeries _trend = new("Structure.Trend", "Confirmed structure trend") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _signal = new("Structure.Signal", "Confirmed structural event direction") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    public MarketStructure() : base(true) { _eventView = _events.AsReadOnly(); _zoneView = _zones.AsReadOnly(); DataSeries[0] = _trend; DataSeries.Add(_signal); EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final); }
    [Display(Name = "Closed bars each side of swing", GroupName = "Calculation"), Range(1, 100)] public int SwingRadius { get => _radius; set { _radius = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Order-block opposite-candle lookback", GroupName = "Calculation"), Range(1, 100)] public int OrderBlockLookback { get => _obLookback; set { _obLookback = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Equal-high/low tolerance in ticks", GroupName = "Calculation"), Range(0, 100)] public decimal EqualToleranceTicks { get => _equalTicks; set { _equalTicks = Math.Clamp(value, 0, 100); RecalculateValues(); } }
    [Display(Name = "Minimum FVG width in ticks", GroupName = "Calculation"), Range(0, 1000)] public decimal MinimumGapTicks { get => _minimumGapTicks; set { _minimumGapTicks = Math.Clamp(value, 0, 1000); RecalculateValues(); } }
    [Display(Name = "FVG mitigation", GroupName = "Calculation")] public GapMitigation Mitigation { get => _mitigation; set { _mitigation = value; RecalculateValues(); } }
    [Display(Name = "CISD minimum opposite run", GroupName = "Calculation"), Range(1, 20)] public int CisdMinimumRun { get => _minimumRun; set { _minimumRun = Math.Clamp(value, 1, 20); RecalculateValues(); } }
    [Display(Name = "Maximum retained events / zones", GroupName = "Calculation"), Range(20, 2000)] public int MaximumObjects { get => _retention; set { _retention = Math.Clamp(value, 20, 2000); RecalculateValues(); } }
    [Display(Name = "Swings", GroupName = "Layers")] public bool ShowSwings { get; set; } = true;
    [Display(Name = "BOS / CHoCH", GroupName = "Layers")] public bool ShowBreaks { get; set; } = true;
    [Display(Name = "Order blocks", GroupName = "Layers")] public bool ShowOrderBlocks { get; set; } = true;
    [Display(Name = "Breakers", GroupName = "Layers")] public bool ShowBreakers { get; set; } = true;
    [Display(Name = "Fair value gaps", GroupName = "Layers")] public bool ShowFvg { get; set; } = true;
    [Display(Name = "Equal-high/low liquidity", GroupName = "Layers")] public bool ShowLiquidity { get; set; } = true;
    [Display(Name = "Sweeps", GroupName = "Layers")] public bool ShowSweeps { get; set; } = true;
    [Display(Name = "CISD", GroupName = "Layers")] public bool ShowCisd { get; set; } = true;
    [Display(Name = "Retrospective pivot/zone drawing (series remains causal)", GroupName = "Visualization")] public bool RetrospectiveDrawing { get; set; } = true;
    [Display(Name = "Show ended zones", GroupName = "Visualization")] public bool ShowEndedZones { get; set; }
    [Browsable(false)] public IReadOnlyList<StructureEvent> Events => _eventView;
    [Browsable(false)] public IReadOnlyList<StructureZone> Zones => _zoneView;
    [Browsable(false)] public StudyCoverage Coverage => _state.Processed < 0 ? StudyCoverage.Missing : StudyMath.Coverage(GetCandle(_state.Processed));
    [Browsable(false)] public string Status => $"Closed-bar OHLC heuristics; source location differs from confirmation availability; {FootprintStudyData.Label(Coverage)}";
    protected override void OnRecalculate() { _state = new(-1, 0, default, default, -1, -1, default, default); _saved = _state; _events.Clear(); _zones.Clear(); _undo.Clear(); _lastRequested = -1; }
    internal void ResetForHigherOwner() { OnRecalculate(); Clear(); }
    private void UndoLast()
    {
        if (_events.Count > _beforeEvents) _events.RemoveRange(_beforeEvents, _events.Count - _beforeEvents);
        if (_zones.Count > _beforeZones) _zones.RemoveRange(_beforeZones, _zones.Count - _beforeZones);
        foreach (var item in _undo) _zones[item.Index] = item.Zone;
        _state = _saved;
    }
    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < _lastRequested)
        {
            OnRecalculate(); Clear();
            for (int b = 0; b <= bar; b++) OnCalculate(b, 0);
            return;
        }
        int closed = GetCandle(bar).IsCandleFinished ? bar : bar - 1;
        if (bar == _lastRequested && _state.Processed == bar) UndoLast();
        while (_state.Processed < closed && GetCandle(_state.Processed + 1).IsCandleFinished) Process(_state.Processed + 1, bar);
        decimal signal = 0;
        foreach (var item in _events)
            if (item.AvailableBar == bar && item.Kind is StructureEventKind.Bos or StructureEventKind.Choch or StructureEventKind.Sweep or StructureEventKind.Cisd) signal += item.Direction;
        _signal.Set(bar, signal, closed >= 0 && _state.Eligible); _trend.Set(bar, _state.Trend, closed >= 0 && _state.Eligible); _lastRequested = bar;
    }
    private void Event(StructureEventKind kind, int source, int available, int direction, decimal price, int related = -1) => _events.Add(new(kind, source, available, direction, price, related));
    private void Zone(StructureZoneKind kind, int source, int available, int direction, decimal low, decimal high) => _zones.Add(new(kind, source, available, direction, low, high, -1, false));
    private void Process(int b, int available)
    {
        if (_events.Count > MaximumObjects) _events.RemoveRange(0, _events.Count - MaximumObjects);
        if (_zones.Count > MaximumObjects) _zones.RemoveRange(0, _zones.Count - MaximumObjects);
        _saved = _state; _beforeEvents = _events.Count; _beforeZones = _zones.Count; _undo.Clear();
        var c = GetCandle(b); decimal tick = InstrumentInfo?.TickSize ?? 1;
        if (!c.CanCalculateCausally)
        {
            for (int i = 0; i < _zones.Count; i++)
                if (_zones[i].EndBar < 0) { _undo.Add((i, _zones[i])); _zones[i] = _zones[i] with { EndBar = b, EndAvailableBar = available, CoverageGap = true }; }
            _state = new(b, 0, default, default, -1, -1, default, default, false); return;
        }
        int existing = _zones.Count;
        for (int i = 0; i < existing; i++)
        {
            var zone = _zones[i]; if (zone.EndBar >= 0 || b <= zone.SourceBar) continue;
            bool invalid = zone.Direction > 0 ? c.Close < zone.Low : c.Close > zone.High;
            bool filled = zone.Kind == StructureZoneKind.FairValueGap && (Mitigation switch
            {
                GapMitigation.WickTouch => zone.Direction > 0 ? c.Low <= zone.High : c.High >= zone.Low,
                GapMitigation.CloseThrough => invalid,
                _ => zone.Direction > 0 ? c.Low <= zone.Low : c.High >= zone.High
            });
            if (zone.Kind == StructureZoneKind.Liquidity) filled = zone.Direction > 0 ? c.Low < zone.Low : c.High > zone.High;
            if (!invalid && !filled) continue;
            _undo.Add((i, zone)); _zones[i] = zone with { EndBar = b, Invalidated = invalid, EndAvailableBar = available };
            if (invalid && zone.Kind == StructureZoneKind.OrderBlock) Zone(StructureZoneKind.Breaker, zone.SourceBar, available, -zone.Direction, zone.Low, zone.High);
        }
        var high = _state.High; var low = _state.Low; int trend = _state.Trend, sweepHigh = _state.SweepHigh, sweepLow = _state.SweepLow;
        int pivot = b - SwingRadius;
        if (pivot >= SwingRadius)
        {
            var pc = GetCandle(pivot); bool isHigh = pc.CanCalculateCausally, isLow = pc.CanCalculateCausally;
            for (int i = pivot - SwingRadius; i <= b; i++)
            {
                if (i == pivot) continue; var other = GetCandle(i);
                if (!other.CanCalculateCausally) { isHigh = isLow = false; break; }
                if (other.High > pc.High || i < pivot && other.High == pc.High) isHigh = false;
                if (other.Low < pc.Low || i < pivot && other.Low == pc.Low) isLow = false;
            }
            if (isHigh)
            {
                Event(StructureEventKind.SwingHigh, pivot, available, -1, pc.High);
                if (high.Exists && Math.Abs(pc.High - high.Price) <= EqualToleranceTicks * tick)
                { Event(StructureEventKind.EqualHigh, pivot, available, -1, pc.High, high.Bar); Zone(StructureZoneKind.Liquidity, high.Bar, available, -1, Math.Min(high.Price, pc.High), Math.Max(high.Price, pc.High)); }
                high = new(pivot, pc.High, true, false);
            }
            if (isLow)
            {
                Event(StructureEventKind.SwingLow, pivot, available, 1, pc.Low);
                if (low.Exists && Math.Abs(pc.Low - low.Price) <= EqualToleranceTicks * tick)
                { Event(StructureEventKind.EqualLow, pivot, available, 1, pc.Low, low.Bar); Zone(StructureZoneKind.Liquidity, low.Bar, available, 1, Math.Min(low.Price, pc.Low), Math.Max(low.Price, pc.Low)); }
                low = new(pivot, pc.Low, true, false);
            }
        }
        if (high.Exists && b > high.Bar && sweepHigh != high.Bar && c.High > high.Price && c.Close < high.Price)
        { Event(StructureEventKind.Sweep, b, available, -1, high.Price, high.Bar); sweepHigh = high.Bar; }
        if (low.Exists && b > low.Bar && sweepLow != low.Bar && c.Low < low.Price && c.Close > low.Price)
        { Event(StructureEventKind.Sweep, b, available, 1, low.Price, low.Bar); sweepLow = low.Bar; }
        if (high.Exists && !high.Broken && c.Close > high.Price)
        { Event(trend < 0 ? StructureEventKind.Choch : StructureEventKind.Bos, b, available, 1, high.Price, high.Bar); OrderBlock(b, available, 1); high = high with { Broken = true }; trend = 1; }
        if (low.Exists && !low.Broken && c.Close < low.Price)
        { Event(trend > 0 ? StructureEventKind.Choch : StructureEventKind.Bos, b, available, -1, low.Price, low.Bar); OrderBlock(b, available, -1); low = low with { Broken = true }; trend = -1; }
        if (b >= 2 && GetCandle(b - 2).CanCalculateCausally && GetCandle(b - 1).CanCalculateCausally)
        {
            var old = GetCandle(b - 2); decimal minGap = MinimumGapTicks * tick;
            if (c.Low > old.High && c.Low - old.High >= minGap) Zone(StructureZoneKind.FairValueGap, b - 1, available, 1, old.High, c.Low);
            if (c.High < old.Low && old.Low - c.High >= minGap) Zone(StructureZoneKind.FairValueGap, b - 1, available, -1, c.High, old.Low);
        }
        Run bull = _state.Bull, bear = _state.Bear;
        if (c.Close > c.Open)
        {
            if (bear.Length >= CisdMinimumRun && b - bear.Start <= 100 && c.Close > bear.Open) { Event(StructureEventKind.Cisd, b, available, 1, bear.Open, bear.Start); bear = default; }
            bull = bull.Length > 0 && b > 0 && GetCandle(b - 1).CanCalculateCausally && GetCandle(b - 1).Close > GetCandle(b - 1).Open ? bull with { Length = bull.Length + 1 } : new(b, 1, c.Open);
        }
        else if (c.Close < c.Open)
        {
            if (bull.Length >= CisdMinimumRun && b - bull.Start <= 100 && c.Close < bull.Open) { Event(StructureEventKind.Cisd, b, available, -1, bull.Open, bull.Start); bull = default; }
            bear = bear.Length > 0 && b > 0 && GetCandle(b - 1).CanCalculateCausally && GetCandle(b - 1).Close < GetCandle(b - 1).Open ? bear with { Length = bear.Length + 1 } : new(b, 1, c.Open);
        }
        _state = new(b, trend, high, low, sweepHigh, sweepLow, bull, bear);
    }
    private void OrderBlock(int b, int available, int direction)
    {
        for (int i = b - 1; i >= Math.Max(0, b - OrderBlockLookback); i--)
        {
            var c = GetCandle(i); if (!c.CanCalculateCausally) break; if (direction > 0 ? c.Close >= c.Open : c.Close <= c.Open) continue;
            Zone(StructureZoneKind.OrderBlock, i, available, direction, c.Low, c.High); break;
        }
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, Status, Color.LightSlateGray);
        foreach (var zone in _zones)
        {
            bool enabled = zone.Kind switch { StructureZoneKind.OrderBlock => ShowOrderBlocks, StructureZoneKind.Breaker => ShowBreakers, StructureZoneKind.FairValueGap => ShowFvg, _ => ShowLiquidity };
            bool ended = zone.EndAvailableBar >= 0 && zone.EndAvailableBar <= LastVisibleBarNumber;
            if (!enabled || ended && !ShowEndedZones || zone.AvailableBar > LastVisibleBarNumber) continue;
            int start = Math.Max(FirstVisibleBarNumber, RetrospectiveDrawing ? zone.SourceBar : zone.AvailableBar), end = !ended ? LastVisibleBarNumber : Math.Min(LastVisibleBarNumber, zone.EndBar);
            if (end < start) continue;
            int x = ChartInfo.GetXByBar(start), right = ChartInfo.GetXByBar(end + 1), top = ChartInfo.GetYByPrice(zone.High), bottom = ChartInfo.GetYByPrice(zone.Low);
            Color color = zone.Direction > 0 ? Color.MediumSeaGreen : Color.IndianRed;
            context.FillRectangle(Color.FromArgb(35, color), new Rectangle(x, Math.Min(top, bottom), Math.Max(1, right - x), Math.Max(2, Math.Abs(bottom - top))));
            context.DrawString($"{zone.Kind} (available at close {zone.AvailableBar})", FootprintStudyData.SmallFont, color, x + 2, Math.Min(top, bottom));
        }
        foreach (var item in _events)
        {
            bool enabled = item.Kind switch { StructureEventKind.SwingHigh or StructureEventKind.SwingLow => ShowSwings, StructureEventKind.Bos or StructureEventKind.Choch => ShowBreaks, StructureEventKind.EqualHigh or StructureEventKind.EqualLow => ShowLiquidity, StructureEventKind.Sweep => ShowSweeps, _ => ShowCisd };
            int bar = RetrospectiveDrawing ? item.SourceBar : item.AvailableBar;
            if (!enabled || bar < FirstVisibleBarNumber || bar > LastVisibleBarNumber || item.AvailableBar > LastVisibleBarNumber) continue;
            context.DrawString($"{item.Kind} [close {item.AvailableBar}]", FootprintStudyData.SmallFont, item.Direction > 0 ? Color.LimeGreen : Color.IndianRed, ChartInfo.GetXByBar(bar), ChartInfo.GetYByPrice(item.Price));
        }
    }
}
