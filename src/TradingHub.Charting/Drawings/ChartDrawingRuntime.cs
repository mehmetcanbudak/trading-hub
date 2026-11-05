using SkiaSharp;
using TradingHub.Data;

namespace TradingHub.Charting.Drawings;

/// <summary>
/// UI-thread-confined calculation and screen state for one drawing on one chart. Workspace drawing
/// parameters may be shared, but this state belongs to ChartRenderState and is retired with that chart.
/// </summary>
public sealed class ChartDrawingRuntime
{
    private readonly TradeDrawingCalc _calculator = new();
    internal readonly record struct VwapScreenPoint(SKPoint Center, float DeviationPixels);
    internal readonly List<VwapScreenPoint> VwapGeometry = new();
    internal SKPoint? VwapAnchor;
    internal SKRect? ProfileGeometry;
    internal ChartLayout? Layout;
    internal uint RenderFrame;

    public TradeDrawingCalc.ProfileResult? CalculateProfile(ChartDrawing drawing, TradeStore store, CandleSeries series, long generation = 0)
    {
        if (drawing.Points.Count < 2) return null;
        DateTime from = drawing.Points[0].Time, to = drawing.Points[1].Time;
        if (to < from) (from, to) = (to, from);
        if (drawing.ProfileWholeSessions)
        {
            from = series.Session.SessionStartUtc(series.Session.SessionDate(from));
            var endDate = series.Session.SessionDate(to);
            if (endDate < DateOnly.MaxValue) to = series.Session.SessionStartUtc(endDate.AddDays(1)).AddTicks(-1);
        }
        return _calculator.FixedRangeProfile(store, series, from, to, drawing.ProfileRows, drawing.ValueAreaPercent, generation);
    }

    public TradeDrawingCalc.VwapResult CalculateVwap(ChartDrawing drawing, TradeStore store, CandleSeries series, long generation = 0) =>
        _calculator.AnchoredVwap(store, series, drawing.Points.Count > 0 ? drawing.Points[0].Time : DateTime.MaxValue, generation);

    internal void UpdateVwapGeometry(ChartDrawing drawing, ChartLayout layout, CandleSeries series, IReadOnlyList<TradeDrawingCalc.VwapPoint> points)
    {
        VwapGeometry.Clear();
        VwapAnchor = null;
        if (points.Count == 0) return;
        VwapAnchor = new SKPoint(ChartDrawing.XOf(layout, series, drawing.Points[0].Time), layout.Main.Y(points[0].Vwap));
        // Keep one endpoint on each side, even if no decimated sample lies inside the visible interval.
        int first = Math.Max(0, LowerBound(points, layout.FirstVisible) - 1);
        int end = Math.Min(points.Count, LowerBound(points, layout.LastVisible + 1) + 1);
        VwapGeometry.EnsureCapacity(end - first);
        for (int i = first; i < end; i++)
        {
            var point = points[i];
            float x = layout.CenterX(point.Bar), y = layout.Main.Y(point.Vwap);
            float deviation = layout.Main.Y((double)point.Vwap + point.Deviation) - y;
            VwapGeometry.Add(new VwapScreenPoint(new SKPoint(x, y), deviation));
        }
    }

    private static int LowerBound(IReadOnlyList<TradeDrawingCalc.VwapPoint> points, int bar)
    {
        int low = 0, high = points.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (points[middle].Bar < bar) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
