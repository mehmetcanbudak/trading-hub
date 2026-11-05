// Independently implemented OHLC heuristics; no VPFM/HTF SUITE/Bandz/Unicorn source copied.
// KVP capability reference gouqizi2214730-source/trading-hub @ 50220ba91613161685f84384825d855cb4dbeb76 (MIT with Commons Clause).
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Higher-timeframe candles / FVG / PO3 / projections")]
[Category(IndicatorCategories.Levels)]
[Description("Two real calendar/mapped higher periods, completed-candle FVG, closed sweep-and-return PO3, C3/C4 T-Spot zones and CISD range-multiple projections. Optional current aggregate is visibly preview-only. Breaker/FVG overlaps are labelled heuristics, not institutional observations.")]
public class HtfStructure : Indicator
{
    public readonly record struct Gap(StudyTimeFrame Period, int Direction, int SourceBar, int AvailableBar, decimal Low, decimal High, int FillBar, int FillAvailableBar, int CoverageGapAvailableBar = -1)
    {
        public bool IsActiveAt(int bar) => bar >= AvailableBar && (FillAvailableBar < 0 || bar < FillAvailableBar) && (CoverageGapAvailableBar < 0 || bar < CoverageGapAvailableBar);
    }
    public readonly record struct Po3(int C1StartBar, int C2StartBar, int C2EndBar, int AvailableBar, int Direction, bool TwoSided, decimal RangeHigh, decimal RangeLow,
        decimal Extreme, decimal TSpot3Low, decimal TSpot3High, decimal? TSpot4Low, decimal? TSpot4High, int TSpot4AvailableBar, int InvalidatedBar, int CisdBar, decimal CisdLevel, int ProjectionAvailableBar, int InvalidatedAvailableBar = -1, int CoverageGapAvailableBar = -1)
    {
        public bool IsActiveAt(int bar) => bar >= AvailableBar && (InvalidatedAvailableBar < 0 || bar < InvalidatedAvailableBar) && (CoverageGapAvailableBar < 0 || bar < CoverageGapAvailableBar);
    }
    private readonly List<Gap> _gaps = new(); private readonly List<Po3> _patterns = new();
    private readonly List<(int Index, Gap Value)> _gapUndo = new(); private readonly List<(int Index, Po3 Value)> _patternUndo = new();
    private readonly IReadOnlyList<Gap> _gapView; private readonly IReadOnlyList<Po3> _patternView;
    private static readonly decimal[] ProjectionMultiples = { 2m, 2.5m, 4m, 4.5m };
    private static readonly RenderPen BullCandlePen = new(Color.SeaGreen), BearCandlePen = new(Color.IndianRed);
    private HigherTimeframeData[] _frames = Array.Empty<HigherTimeframeData>();
    private readonly MarketStructure _lower = new() { SwingRadius = 3 };
    private readonly OptionalValueDataSeries _signal = new("HtfStructure.PO3", "Confirmed unambiguous PO3 direction") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private int _last = -1, _closed = -1, _beforeClosed, _gapAdds, _patternAdds, _retention = 100;
    private bool _mapped = true; private StudyTimeFrame _first = StudyTimeFrame.Hour, _second = StudyTimeFrame.Hours4;
    public HtfStructure() : base(true) { _gapView = _gaps.AsReadOnly(); _patternView = _patterns.AsReadOnly(); Add(_lower); DataSeries[0] = _signal; EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final); }
    [Display(Name = "Use mapped higher periods", GroupName = "Calculation")] public bool UseMappedPeriods { get => _mapped; set { _mapped = value; RecalculateValues(); } }
    [Display(Name = "First custom higher period", GroupName = "Calculation")] public StudyTimeFrame FirstPeriod { get => _first; set { _first = value; RecalculateValues(); } }
    [Display(Name = "Second custom higher period", GroupName = "Calculation")] public StudyTimeFrame SecondPeriod { get => _second; set { _second = value; RecalculateValues(); } }
    [Display(Name = "Maximum retained patterns per layer", GroupName = "Calculation"), Range(10, 500)] public int MaximumPatterns { get => _retention; set { _retention = Math.Clamp(value, 10, 500); RecalculateValues(); } }
    [Display(Name = "Higher-period candles", GroupName = "Layers")] public bool ShowCandles { get; set; } = true;
    [Display(Name = "Current aggregate preview (never a strategy value)", GroupName = "Layers")] public bool ShowPreview { get; set; }
    [Display(Name = "Higher-period FVG", GroupName = "Layers")] public bool ShowFvg { get; set; } = true;
    [Display(Name = "PO3 and T-Spot", GroupName = "Layers")] public bool ShowPo3 { get; set; } = true;
    [Display(Name = "CISD and range-multiple projections (not sigma)", GroupName = "Layers")] public bool ShowProjections { get; set; } = true;
    [Display(Name = "Breakers and breaker/FVG overlap heuristic", GroupName = "Layers")] public bool ShowBreakers { get; set; } = true;
    [Browsable(false)] public IReadOnlyList<Gap> Gaps => _gapView;
    [Browsable(false)] public IReadOnlyList<Po3> Patterns => _patternView;
    [Browsable(false)] public IReadOnlyList<HigherTimeframeCandle> FirstClosedCandles => _frames.Length == 0 ? Array.Empty<HigherTimeframeCandle>() : _frames[0].Closed;
    [Browsable(false)] public IReadOnlyList<HigherTimeframeCandle> SecondClosedCandles => _frames.Length == 0 ? Array.Empty<HigherTimeframeCandle>() : _frames[1].Closed;
    [Browsable(false)] public StudyCoverage Coverage => _frames.Length == 0 || !_frames[0].HasCurrent ? 0 : _frames[0].Current.Coverage;
    [Browsable(false)] public string Status => $"Closed-candle OHLC heuristics; incomplete first aggregates excluded; incompatible/coarser source periods unavailable; projections are range multiples, not sigma; {FootprintStudyData.Label(Coverage)}";
    public decimal? ProjectionAt(int patternIndex, decimal multiple, int bar)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(multiple);
        if (patternIndex < 0 || patternIndex >= _patterns.Count || bar > _last) return null;
        var pattern = _patterns[patternIndex];
        if (pattern.ProjectionAvailableBar < 0 || bar < pattern.ProjectionAvailableBar || !pattern.IsActiveAt(bar)) return null;
        return pattern.CisdLevel + multiple * (pattern.CisdLevel - pattern.Extreme);
    }
    protected override void OnRecalculate()
    { _last = _closed = -1; _gaps.Clear(); _patterns.Clear(); _gapUndo.Clear(); _patternUndo.Clear(); _frames = Array.Empty<HigherTimeframeData>(); _gapAdds = _patternAdds = 0; }
    protected override void OnDispose() { _lower.Dispose(); base.OnDispose(); }
    private void Initialize()
    {
        var map = StudyMath.BollingerMap(ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromMinutes(1));
        _frames = new[] { new HigherTimeframeData(UseMappedPeriods ? map.B : FirstPeriod), new HigherTimeframeData(UseMappedPeriods ? map.C : SecondPeriod) };
    }
    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < _last) { OnRecalculate(); _lower.ResetForHigherOwner(); Clear(); }
        if (_frames.Length == 0) Initialize();
        if (bar == _last)
        {
            if (_gapAdds > 0) _gaps.RemoveRange(_gaps.Count - _gapAdds, _gapAdds);
            if (_patternAdds > 0) _patterns.RemoveRange(_patterns.Count - _patternAdds, _patternAdds);
            for (int i = _gapUndo.Count - 1; i >= 0; i--) _gaps[_gapUndo[i].Index] = _gapUndo[i].Value;
            for (int i = _patternUndo.Count - 1; i >= 0; i--) _patterns[_patternUndo[i].Index] = _patternUndo[i].Value;
            _closed = _beforeClosed;
        }
        int from = bar == _last ? bar : _last + 1;
        for (int b = from; b <= bar; b++) Step(b, value);
    }
    private void SavePattern(int index, Po3 next)
    { if (index < _patterns.Count - _patternAdds) _patternUndo.Add((index, _patterns[index])); _patterns[index] = next; }
    private void Step(int b, decimal value)
    {
        _gapUndo.Clear(); _patternUndo.Clear(); _gapAdds = _patternAdds = 0; _beforeClosed = _closed;
        if (_gaps.Count > MaximumPatterns) _gaps.RemoveRange(0, _gaps.Count - MaximumPatterns);
        if (_patterns.Count > MaximumPatterns) _patterns.RemoveRange(0, _patterns.Count - MaximumPatterns);
        var c = GetCandle(b); _lower.Calculate(b, value);
        for (int k = 0; k < 2; k++)
        {
            var frame = _frames[k]; if (!StudyMath.CanResolve(ChartInfo?.TimeFrame, frame.Frame)) continue;
            frame.Update(b, c); var bars = frame.Closed;
            if (bars.Count == 0 || bars[^1].AvailableBar != b) continue;
            var third = bars[^1];
            if (bars.Count >= 3 && !bars[^3].Partial && !bars[^2].Partial && !third.Partial)
            {
                var first = bars[^3];
                if (third.Low > first.High) { _gaps.Add(new(frame.Frame, 1, bars[^2].StartBar, b, first.High, third.Low, -1, -1)); _gapAdds++; }
                if (third.High < first.Low) { _gaps.Add(new(frame.Frame, -1, bars[^2].StartBar, b, third.High, first.Low, -1, -1)); _gapAdds++; }
            }
            if (k != 0 || bars.Count < 2) continue;
            var previous = bars[^2]; bool inside = third.Open >= previous.Low && third.Open <= previous.High && third.Close > previous.Low && third.Close < previous.High;
            bool highSweep = third.High > previous.High, lowSweep = third.Low < previous.Low;
            if (inside && !previous.Partial && !third.Partial && (highSweep || lowSweep))
            {
                for (int direction = -1; direction <= 1; direction += 2)
                {
                    if (!(direction > 0 ? lowSweep : highSweep)) continue;
                    decimal target = third.Close - direction * (third.High - third.Low) / 2;
                    _patterns.Add(new(previous.StartBar, third.StartBar, third.EndBar, b, direction, highSweep && lowSweep, previous.High, previous.Low,
                        direction > 0 ? third.Low : third.High, Math.Min(third.Close, target), Math.Max(third.Close, target), null, null, -1, -1, -1, 0, -1)); _patternAdds++;
                }
            }
            for (int i = 0; i < _patterns.Count; i++)
            {
                var pattern = _patterns[i];
                if (pattern.C2EndBar == previous.EndBar && pattern.TSpot4AvailableBar < 0)
                { decimal target = third.Close - pattern.Direction * (third.High - third.Low) / 4; SavePattern(i, pattern with { TSpot4Low = Math.Min(third.Close, target), TSpot4High = Math.Max(third.Close, target), TSpot4AvailableBar = b }); }
            }
        }
        int closed = c.IsCandleFinished ? b : b - 1;
        for (int t = _closed + 1; t <= closed; t++)
        {
            var candle = GetCandle(t);
            if (!candle.CanCalculateCausally || (candle.Quality & TradingHub.Market.DataQuality.Partial) != 0)
            {
                for (int i = 0; i < _gaps.Count; i++)
                {
                    var gap = _gaps[i]; if (!gap.IsActiveAt(b) || t < gap.AvailableBar) continue;
                    if (i < _gaps.Count - _gapAdds) _gapUndo.Add((i, gap));
                    _gaps[i] = gap with { CoverageGapAvailableBar = b };
                }
                for (int i = 0; i < _patterns.Count; i++)
                    if (_patterns[i].IsActiveAt(b) && t >= _patterns[i].AvailableBar) SavePattern(i, _patterns[i] with { CoverageGapAvailableBar = b });
                continue;
            }
            for (int i = 0; i < _gaps.Count; i++)
            {
                var gap = _gaps[i]; if (!gap.IsActiveAt(b) || t < gap.AvailableBar) continue;
                if (gap.Direction > 0 ? candle.Low <= gap.Low : candle.High >= gap.High)
                { if (i < _gaps.Count - _gapAdds) _gapUndo.Add((i, gap)); _gaps[i] = gap with { FillBar = t, FillAvailableBar = b }; }
            }
            for (int i = 0; i < _patterns.Count; i++)
            {
                var pattern = _patterns[i]; if (!pattern.IsActiveAt(b) || t < pattern.AvailableBar) continue;
                if (pattern.Direction > 0 ? candle.Low < pattern.Extreme : candle.High > pattern.Extreme) SavePattern(i, pattern with { InvalidatedBar = t, InvalidatedAvailableBar = b });
            }
        }
        var confirmedEvents = _lower.Events;
        for (int i = 0; i < _patterns.Count; i++)
        {
            var pattern = _patterns[i]; if (!pattern.IsActiveAt(b) || pattern.CisdBar >= 0 || pattern.TwoSided) continue;
            for (int e = 0; e < confirmedEvents.Count; e++)
            {
                var item = confirmedEvents[e];
                if (item.Kind == StructureEventKind.Cisd && item.Direction == pattern.Direction && item.SourceBar >= pattern.C2StartBar && item.AvailableBar <= b)
                { SavePattern(i, pattern with { CisdBar = item.SourceBar, CisdLevel = item.Price, ProjectionAvailableBar = b }); break; }
            }
        }
        decimal signal = 0; foreach (var pattern in _patterns) if (pattern.AvailableBar == b && pattern.IsActiveAt(b) && !pattern.TwoSided) signal += pattern.Direction;
        // PO3 is produced by the first layer; an unresolved secondary candle/FVG layer cannot erase it.
        bool resolved = StudyMath.CanResolve(ChartInfo?.TimeFrame, _frames[0].Frame);
        _signal.Set(b, signal, resolved && closed >= 0 && GetCandle(closed).CanCalculateCausally && (GetCandle(closed).Quality & TradingHub.Market.DataQuality.Partial) == 0); _closed = closed; _last = b;
    }
    private void Box(RenderContext context, int source, int available, decimal low, decimal high, Color color, string label, int end = -1)
    {
        if (available > LastVisibleBarNumber) return; int start = Math.Max(FirstVisibleBarNumber, available), finish = end < 0 ? LastVisibleBarNumber : Math.Min(end, LastVisibleBarNumber);
        if (finish < start) return;
        int x = ChartInfo.GetXByBar(start), right = ChartInfo.GetXByBar(finish + 1), top = ChartInfo.GetYByPrice(high), bottom = ChartInfo.GetYByPrice(low);
        context.FillRectangle(Color.FromArgb(35, color), new Rectangle(x, Math.Min(top, bottom), Math.Max(1, right - x), Math.Max(2, Math.Abs(bottom - top))));
        context.DrawString($"{label} (source {source}, available {available})", FootprintStudyData.SmallFont, color, x + 2, Math.Min(top, bottom));
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, Status, Color.Khaki);
        if (ShowFvg) foreach (var gap in _gaps) if (gap.IsActiveAt(LastVisibleBarNumber)) Box(context, gap.SourceBar, gap.AvailableBar, gap.Low, gap.High, gap.Direction > 0 ? Color.SeaGreen : Color.IndianRed, $"{gap.Period} FVG");
        foreach (var pattern in _patterns)
        {
            if (!pattern.IsActiveAt(LastVisibleBarNumber)) continue; Color color = pattern.Direction > 0 ? Color.MediumSeaGreen : Color.IndianRed;
            if (ShowPo3)
            {
                Box(context, pattern.C1StartBar, pattern.AvailableBar, pattern.RangeLow, pattern.RangeHigh, Color.Gold, pattern.TwoSided ? "PO3 two-sided/ambiguous" : "PO3 sweep and return");
                Box(context, pattern.C2StartBar, pattern.AvailableBar, pattern.TSpot3Low, pattern.TSpot3High, color, "T-Spot C3");
                if (pattern.TSpot4AvailableBar >= 0) Box(context, pattern.C2EndBar + 1, pattern.TSpot4AvailableBar, pattern.TSpot4Low!.Value, pattern.TSpot4High!.Value, color, "T-Spot C4");
            }
            if (ShowProjections && pattern.CisdBar >= 0)
                foreach (decimal multiple in ProjectionMultiples)
                { decimal price = pattern.CisdLevel + multiple * (pattern.CisdLevel - pattern.Extreme); Box(context, pattern.CisdBar, pattern.ProjectionAvailableBar, price, price, color, $"CISD projection {multiple}R (not sigma)"); }
        }
        if (ShowBreakers)
        {
            var zones = _lower.Zones;
            for (int i = 0; i < zones.Count; i++)
            {
                var breaker = zones[i]; if (breaker.Kind != StructureZoneKind.Breaker || !breaker.IsActiveAt(LastVisibleBarNumber)) continue;
                bool overlap = false;
                for (int j = 0; j < zones.Count; j++)
                { var gap = zones[j]; if (gap.Kind == StructureZoneKind.FairValueGap && gap.IsActiveAt(LastVisibleBarNumber) && gap.Direction == breaker.Direction && gap.Low <= breaker.High && gap.High >= breaker.Low) { overlap = true; break; } }
                Box(context, breaker.SourceBar, breaker.AvailableBar, breaker.Low, breaker.High, breaker.Direction > 0 ? Color.SeaGreen : Color.IndianRed, overlap ? "Breaker / FVG overlap heuristic" : "Breaker heuristic");
            }
        }
        if (ShowCandles) DrawCandles(context);
    }
    private void DrawCandles(RenderContext context)
    {
        int right = ChartInfo.PriceChartContainer.Region.Right - 12;
        for (int k = _frames.Length - 1; k >= 0; k--)
        {
            var frame = _frames[k]; var closed = frame.Closed;
            int last = closed.Count - 1; while (last >= 0 && closed[last].AvailableBar > LastVisibleBarNumber) last--;
            int count = Math.Min(4, last + 1); bool showPreview = ShowPreview && frame.HasCurrent && frame.Current.EndBar <= LastVisibleBarNumber;
            int x = right - (count + (showPreview ? 1 : 0)) * 16;
            context.DrawString(frame.Frame + " closed candles", FootprintStudyData.SmallFont, Color.Silver, x, ChartInfo.PriceChartContainer.Region.Top + 8 + k * 12);
            for (int i = last - count + 1; i <= last + (showPreview ? 1 : 0); i++)
            {
                bool preview = i > last; var candle = preview ? frame.Current : closed[i]; Color color = candle.Close >= candle.Open ? Color.SeaGreen : Color.IndianRed;
                int high = ChartInfo.GetYByPrice(candle.High), low = ChartInfo.GetYByPrice(candle.Low), open = ChartInfo.GetYByPrice(candle.Open), close = ChartInfo.GetYByPrice(candle.Close);
                context.DrawLine(candle.Close >= candle.Open ? BullCandlePen : BearCandlePen, x + 4, high, x + 4, low);
                context.FillRectangle(Color.FromArgb(preview || candle.Partial ? 50 : 200, color), new Rectangle(x, Math.Min(open, close), 9, Math.Max(1, Math.Abs(close - open))));
                if (preview || candle.Partial) context.DrawString(preview ? candle.Partial ? "Preview / partial" : "Preview" : "Partial", FootprintStudyData.SmallFont, color, x, low + 3); x += 16;
            }
            right = Math.Max(ChartInfo.PriceChartContainer.Region.Left, x - (count + 2) * 16);
        }
    }
}
