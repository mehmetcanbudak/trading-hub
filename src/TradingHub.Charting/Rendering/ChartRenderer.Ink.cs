using SkiaSharp;
using TradingHub.Charting.Drawings;

namespace TradingHub.Charting.Rendering;

public sealed partial class ChartRenderState
{
    /// <summary>Gesture drawing: how full the hold ring is (0 to 1) and where.</summary>
    public float? InkHold { get; set; }

    public SKPoint InkHoldAt { get; set; }

    /// <summary>The gesture stroke in progress (pixels).</summary>
    public IReadOnlyList<SKPoint>? InkStroke { get; set; }

    /// <summary>Drawings the scribble in progress would erase (shown faded).</summary>
    public HashSet<ChartDrawing>? InkVictims { get; set; }

    /// <summary>Shift + drag measurement (kept until the next click; never saved).</summary>
    public ChartDrawing? Measure { get; set; }
}

public sealed partial class ChartRenderer
{
    /// <summary>Gesture drawing: the ring that fills while holding and the ink stroke.</summary>
    private static void DrawInk(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartRenderState state)
    {
        if (state.InkHold == null && state.InkStroke == null) return;
        var bg = model.Theme.BackgroundTop;
        // light ink on dark themes and dark ink on light ones, like a pencil on the chart
        bool dark = (bg.Red + bg.Green + bg.Blue) / 3 < 128;
        var ink = dark ? new SKColor(255, 255, 255) : new SKColor(27, 28, 40);
        canvas.Save();
        canvas.ClipRect(layout.Main.Rect);
        if (state.InkHold is { } h)
        {
            var c = state.InkHoldAt;
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = ink.WithAlpha(30) };
            canvas.DrawCircle(c, 20, ring);
            ring.Color = ink.WithAlpha(200);
            using var arc = new SKPath();
            arc.AddArc(new SKRect(c.X - 20, c.Y - 20, c.X + 20, c.Y + 20), -90, 360 * Math.Clamp(h, 0, 1));
            canvas.DrawPath(arc, ring);
        }

        if (state.InkStroke is { Count: > 0 } pts)
        {
            using var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, Color = ink.WithAlpha(230), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            if (pts.Count == 1)
            {
                pen.Style = SKPaintStyle.Fill;
                canvas.DrawCircle(pts[0], 2.2f, pen);
            }
            else
            {
                using var path = new SKPath();
                path.MoveTo(pts[0]);
                for (int i = 1; i < pts.Count; i++) path.LineTo(pts[i]);
                canvas.DrawPath(path, pen);
            }

            // the pencil tip
            using var tip = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ink };
            canvas.DrawCircle(pts[^1], 3, tip);
            tip.Color = ink.WithAlpha(40);
            canvas.DrawCircle(pts[^1], 5, tip);
        }

        canvas.Restore();
    }
}
