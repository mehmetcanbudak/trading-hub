using SkiaSharp;
using TradingHub.Data;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Charting.Drawings;

// Ellipse, arrow, cross line, path and the freehand brush/highlighter strokes.
public sealed partial class ChartDrawing
{
    /// <summary>Freehand (brush and highlighter): many points and no per-point handles.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFreehand => Kind is DrawingKind.Brush or DrawingKind.Highlighter;

    /// <summary>A free number of points (path and freehand strokes).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMultiPoint => IsFreehand || Kind == DrawingKind.Path;

    /// <summary>Highlighter stroke width: thick and translucent, like a marker.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float HighlighterWidth => Math.Max(10, Width * 6);

    /// <summary>The drawing's points in screen coordinates.</summary>
    public List<SKPoint> ScreenPoints(ChartLayout layout, CandleSeries series)
    {
        var main = layout.Main;
        var res = new List<SKPoint>(Points.Count);
        foreach (var p in Points) res.Add(new SKPoint(XOf(layout, series, p.Time), main.Y(p.Price)));
        return res;
    }

    private void RenderShape(SKCanvas canvas, ChartLayout layout, CandleSeries series, int decimals, SKPaint stroke, SKPaint fill, SKPaint text, SKFont font,
        SKFont labelFont, string label, float x0, float y0, float x1, float y1)
    {
        var plot = layout.Main.Rect;
        switch (Kind)
        {
            case DrawingKind.Ellipse:
            {
                var r = new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
                canvas.DrawOval(r, fill);
                canvas.DrawOval(r, stroke);
                if (label.Length > 0)
                {
                    float w = RenderContext.MeasureWidth(labelFont, label);
                    canvas.DrawUiText(label, r.MidX - w / 2, r.MidY + labelFont.Size / 3, labelFont, text);
                }

                break;
            }
            case DrawingKind.Arrow:
            {
                canvas.DrawLine(x0, y0, x1, y1, stroke);
                DrawArrowHead(canvas, x0, y0, x1, y1, Math.Max(9, stroke.StrokeWidth * 3.2f), Color);
                if (label.Length > 0)
                {
                    float w = RenderContext.MeasureWidth(labelFont, label);
                    canvas.DrawUiText(label, (x0 + x1) / 2 - w / 2, (y0 + y1) / 2 - 6, labelFont, text);
                }

                break;
            }
            case DrawingKind.CrossLine:
                canvas.DrawLine(plot.Left, y0, plot.Right, y0, stroke);
                canvas.DrawLine(x0, plot.Top, x0, layout.Panels[^1].Rect.Bottom, stroke);
                if (ShowPriceLabel)
                {
                    string pl = Format(Points[0].Price, decimals);
                    canvas.DrawUiText(pl, plot.Right - 4 - RenderContext.MeasureWidth(font, pl), y0 - 3, font, text);
                }

                if (label.Length > 0) canvas.DrawUiText(label, x0 + 6, y0 - 4, labelFont, text);
                if (IsAlert) DrawBell(canvas, plot.Right - (ShowPriceLabel ? 70 : 14), y0 - 8, Color);
                break;
            default:
            {
                if (Points.Count < 2) break;
                using var path = BuildPath(layout, series);
                if (Kind == DrawingKind.Highlighter)
                {
                    stroke.StrokeWidth = HighlighterWidth + (Selected ? 2 : 0);
                    stroke.Color = Color.WithAlpha((byte)Math.Min((int)Color.Alpha, 90));
                    stroke.PathEffect = null;
                }

                if (IsFreehand)
                {
                    stroke.StrokeCap = SKStrokeCap.Round;
                    stroke.StrokeJoin = SKStrokeJoin.Round;
                }

                canvas.DrawPath(path, stroke);
                if (label.Length > 0) canvas.DrawUiText(label, x0 + 6, y0 - 6, labelFont, text);
                break;
            }
        }
    }

    /// <summary>Freehand strokes have no handles: a dashed box marks the selection.</summary>
    private void DrawFreehandSelection(SKCanvas canvas, ChartLayout layout, CandleSeries series, ChartTheme theme)
    {
        var pts = ScreenPoints(layout, series);
        if (pts.Count < 2) return;
        var b = new SKRect(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
        b.Inflate(4, 4);
        using var box = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = theme.Selection, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };
        canvas.DrawRect(b, box);
    }

    /// <summary>A path is straight between points; a freehand stroke is smoothed through the midpoints.</summary>
    private SKPath BuildPath(ChartLayout layout, CandleSeries series)
    {
        var pts = ScreenPoints(layout, series);
        var path = new SKPath();
        if (pts.Count == 0) return path;
        path.MoveTo(pts[0]);
        if (!IsFreehand || pts.Count < 3)
        {
            for (int i = 1; i < pts.Count; i++) path.LineTo(pts[i]);
            return path;
        }

        for (int i = 1; i < pts.Count - 1; i++)
        {
            var mid = new SKPoint((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
            path.QuadTo(pts[i], mid);
        }

        path.LineTo(pts[^1]);
        return path;
    }

    private static void DrawArrowHead(SKCanvas canvas, float x0, float y0, float x1, float y1, float size, SKColor color)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float ux = dx / len, uy = dy / len;
        float bx = x1 - ux * size, by = y1 - uy * size;
        float wx = -uy * size * 0.45f, wy = ux * size * 0.45f;
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        using var head = new SKPath();
        head.MoveTo(x1, y1);
        head.LineTo(bx + wx, by + wy);
        head.LineTo(bx - wx, by - wy);
        head.Close();
        canvas.DrawPath(head, p);
    }

    private bool HitTestShape(ChartLayout layout, CandleSeries series, float x, float y, float tol, float x0, float y0, float x1, float y1)
    {
        switch (Kind)
        {
            case DrawingKind.CrossLine:
                return Math.Abs(y - y0) <= tol || Math.Abs(x - x0) <= tol;
            case DrawingKind.Ellipse:
            {
                // inside the ellipse or on its border
                float cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
                float rx = Math.Abs(x1 - x0) / 2 + tol, ry = Math.Abs(y1 - y0) / 2 + tol;
                float nx = (x - cx) / rx, ny = (y - cy) / ry;
                return nx * nx + ny * ny <= 1;
            }
            default:
            {
                var pts = ScreenPoints(layout, series);
                float t = Kind == DrawingKind.Highlighter ? Math.Max(tol, HighlighterWidth / 2 + 2) : tol;
                for (int i = 1; i < pts.Count; i++)
                    if (DistanceToSegment(x, y, pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y, false) <= t) return true;
                return false;
            }
        }
    }
}
