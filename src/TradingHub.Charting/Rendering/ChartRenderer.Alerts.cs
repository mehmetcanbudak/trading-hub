using SkiaSharp;
using TradingHub.Indicators;
using PointF = System.Drawing.PointF;

namespace TradingHub.Charting.Rendering;

/// <summary>Line capture for alerts and the highlighted (right-click picked) indicator line.</summary>
public sealed partial class ChartRenderer
{
    private static readonly SKColor HighlightColor = new(242, 181, 58);

    private static void BeginLineCapture(ChartModel model)
    {
        foreach (var slot in model.Indicators)
            if (slot.Panel == null) slot.LineCapture.Begin();
    }

    private static void EndLineCapture(ChartModel model, ChartLayout layout)
    {
        foreach (var slot in model.Indicators)
        {
            if (slot.Panel != null || !slot.Visible) slot.LineCapture.Clear();
            else slot.LineCapture.End(model, layout, slot.Indicator);
        }
    }

    /// <summary>The indicator line picked with a right-click, highlighted like a selected drawing.</summary>
    private static void DrawHighlightedLine(SKCanvas canvas, ChartModel model, ChartLayout layout)
    {
        var op = model.HighlightedLine!;
        if (op.Line != null)
        {
            DrawHighlightedPath(canvas, ChartAlertEngine.PixelsOf(model, layout, op), layout.Main);
            return;
        }

        var main = layout.Main;
        if (ChartAlertEngine.LevelOf(model, op) is { } lv)
        {
            float y = ((IChartContainer)main).GetYByPrice(lv.Price, false);
            float x0 = Math.Max(main.Rect.Left, layout.CenterX(lv.FromBar));
            float x1 = lv.ToBar == int.MaxValue ? main.Rect.Right : Math.Min(main.Rect.Right, layout.CenterX(lv.ToBar));
            if (x1 > x0) DrawHighlightedPath(canvas, new[] { new PointF(x0, y), new PointF(x1, y) }, main);
            return;
        }

        if (op.Source != ChartAlertSource.Series || op.Indicator == null || ChartAlertEngine.SlotOf(model, op.Indicator) is not { } slot) return;
        if (slot.Indicator.DataSeries.FindById(op.Field ?? "") is not ValueDataSeries series) return;
        var panel = slot.Panel == null ? main : layout.Panels.FirstOrDefault(p => ReferenceEquals(p.SubPanel, slot.Panel));
        if (panel == null) return;
        var pts = new List<PointF>(Math.Max(0, layout.LastVisible - layout.FirstVisible + 1));
        for (int bar = Math.Max(0, layout.FirstVisible); bar <= layout.LastVisible && bar < model.Series.Count; bar++)
        {
            decimal v = series[bar];
            if (v == 0 && !series.ShowZeroValue) continue;
            pts.Add(new PointF(layout.CenterX(bar), panel.Y(v)));
        }

        DrawHighlightedPath(canvas, pts.ToArray(), panel);
    }

    private static void DrawHighlightedPath(SKCanvas canvas, PointF[] pts, PanelLayout panel)
    {
        if (pts.Length < 2) return;
        canvas.Save();
        canvas.ClipRect(panel.Rect);
        using var path = new SKPath();
        path.MoveTo(pts[0].X, pts[0].Y);
        for (int i = 1; i < pts.Length; i++) path.LineTo(pts[i].X, pts[i].Y);
        using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, Color = HighlightColor.WithAlpha(90) };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = HighlightColor };
        canvas.DrawPath(path, glow);
        canvas.DrawPath(path, line);
        using var dot = new SKPaint { IsAntialias = true, Color = HighlightColor };
        canvas.DrawCircle(pts[0].X, pts[0].Y, 3.5f, dot);
        canvas.DrawCircle(pts[^1].X, pts[^1].Y, 3.5f, dot);
        canvas.Restore();
    }
}
