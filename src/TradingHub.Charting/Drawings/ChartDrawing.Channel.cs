using System.Globalization;
using SkiaSharp;
using TradingHub.Data;
using TradingHub.Rendering;

namespace TradingHub.Charting.Drawings;

/// <summary>Parallel channel geometry and the drawing lines alerts can follow.</summary>
public sealed partial class ChartDrawing
{
    /// <summary>
    /// Parallel channel: price distance from the base line (points 0–1) to the parallel edge. The third point carries it and
    /// stays at the first point's time, so the width is the same along the whole channel.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double ChannelOffset => Points.Count >= 3 ? Points[2].Price - Points[0].Price : 0;

    /// <summary>A freshly drawn channel: the parallel edge starts <paramref name="offset"/> away from the base line.</summary>
    public void InitChannel(double offset)
    {
        if (Kind != DrawingKind.ParallelChannel || Points.Count < 2) return;
        while (Points.Count > 2) Points.RemoveAt(Points.Count - 1);
        Points.Add(new ChartPoint(Points[0].Time, Points[0].Price + offset));
    }

    /// <summary>
    /// Channel handles: 0 and 1 move the base line keeping the width; 2 sets the width where it is dropped, measured from
    /// the base line as it is drawn: along the bars of <paramref name="series"/> (gaps take no room), along time without it.
    /// </summary>
    private bool MoveChannelHandle(int handle, ChartPoint p, CandleSeries? series)
    {
        if (Kind != DrawingKind.ParallelChannel || Points.Count < 3 || handle is < 0 or > 2) return false;
        double off = ChannelOffset;
        switch (handle)
        {
            case 0:
                Points[0] = p;
                Points[2] = new ChartPoint(p.Time, p.Price + off);
                break;
            case 1:
                Points[1] = p;
                break;
            default:
            {
                var a = Points[0];
                var b = Points[1];
                double t;
                if (series is { Count: > 0 })
                {
                    double b0 = BarPos(series, a.Time), b1 = BarPos(series, b.Time);
                    t = Math.Abs(b1 - b0) < 1e-9 ? 0 : (BarPos(series, p.Time) - b0) / (b1 - b0);
                }
                else
                {
                    double span = (b.Time - a.Time).Ticks;
                    t = Math.Abs(span) < 1 ? 0 : (p.Time - a.Time).Ticks / span;
                }

                double basis = a.Price + t * (b.Price - a.Price);
                Points[2] = new ChartPoint(a.Time, a.Price + (p.Price - basis));
                break;
            }
        }

        return true;
    }

    /// <summary>Screen geometry of both channel edges, extended as configured: (base, parallel).</summary>
    private ((float X0, float Y0, float X1, float Y1) Base, (float X0, float Y0, float X1, float Y1) Parallel) ChannelEdges(ChartLayout layout, CandleSeries series)
    {
        var main = layout.Main;
        float x0 = XOf(layout, series, Points[0].Time), y0 = main.Y(Points[0].Price);
        var second = Points.Count > 1 ? Points[1] : Points[0];
        float x1 = XOf(layout, series, second.Time), y1 = main.Y(second.Price);
        double off = ChannelOffset;
        float uy0 = main.Y(Points[0].Price + off), uy1 = main.Y(second.Price + off);
        return (Extend(x0, y0, x1, y1, main.Rect, ExtendLeft, ExtendRight), Extend(x0, uy0, x1, uy1, main.Rect, ExtendLeft, ExtendRight));
    }

    private void RenderChannel(SKCanvas canvas, ChartLayout layout, CandleSeries series, SKPaint stroke, SKPaint fill, SKPaint text, SKFont labelFont, string label)
    {
        var ((ax, ay, bx, by), (cx, cy, dx, dy)) = ChannelEdges(layout, series);
        using (var path = new SKPath())
        {
            path.MoveTo(ax, ay);
            path.LineTo(bx, by);
            path.LineTo(dx, dy);
            path.LineTo(cx, cy);
            path.Close();
            canvas.DrawPath(path, fill);
        }

        canvas.DrawLine(ax, ay, bx, by, stroke);
        canvas.DrawLine(cx, cy, dx, dy, stroke);
        using (var mid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Color.WithAlpha(150), PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0) })
            canvas.DrawLine((ax + cx) / 2, (ay + cy) / 2, (bx + dx) / 2, (by + dy) / 2, mid);
        if (label.Length > 0) canvas.DrawUiText(label, Math.Min(ax, bx) + 5, Math.Min(Math.Min(ay, by), Math.Min(cy, dy)) - 5, labelFont, text);
    }

    private bool HitTestChannel(ChartLayout layout, CandleSeries series, float x, float y, float tol)
    {
        var ((ax, ay, bx, by), (cx, cy, dx, dy)) = ChannelEdges(layout, series);
        if (DistanceToSegment(x, y, ax, ay, bx, by, false) <= tol || DistanceToSegment(x, y, cx, cy, dx, dy, false) <= tol) return true;
        // inside the band
        if (x < Math.Min(ax, bx) || x > Math.Max(ax, bx)) return false;
        float t = Math.Abs(bx - ax) < 0.5f ? 0 : (x - ax) / (bx - ax);
        float ya = ay + t * (by - ay), yc = cy + t * (dy - cy);
        return y >= Math.Min(ya, yc) && y <= Math.Max(ya, yc);
    }

    // ------------------------------------------------------------------ alertable lines

    /// <summary>Bar position (fractional) of an instant; past the last bar it advances at the period's pace.</summary>
    public static double BarPos(CandleSeries series, DateTime time)
    {
        if (series.Count == 0) return 0;
        var lastTime = series[^1].Time;
        if (time > lastTime)
        {
            var dur = series.Period.ApproxDuration;
            double bars = dur > TimeSpan.Zero ? (time - lastTime).Ticks / (double)dur.Ticks : 1;
            return series.Count - 1 + bars;
        }

        int bar = series.FindBar(time);
        if (bar < 0) return 0;
        double frac = 0;
        if (bar + 1 < series.Count)
        {
            var t0 = series[bar].Time;
            var t1 = series[bar + 1].Time;
            if (t1 > t0) frac = Math.Clamp((time - t0).Ticks / (double)(t1 - t0).Ticks, 0, 0.999);
        }

        return bar + frac;
    }

    /// <summary>The lines of this drawing an alert can follow: (stable part key, English display name).</summary>
    public IReadOnlyList<(string Part, string Name)> AlertParts()
    {
        var ic = CultureInfo.InvariantCulture;
        switch (Kind)
        {
            case DrawingKind.HorizontalLine:
            case DrawingKind.HorizontalRay:
            case DrawingKind.TrendLine:
            case DrawingKind.Ray:
            case DrawingKind.ExtendedLine:
            case DrawingKind.Arrow:
            case DrawingKind.CrossLine:
                return new[] { ("line", "line") };
            case DrawingKind.Rectangle:
                return new[] { ("top", "top edge"), ("bottom", "bottom edge") };
            case DrawingKind.ParallelChannel:
                return Points.Count >= 3 ? new[] { ("upper", "upper edge"), ("middle", "middle line"), ("lower", "lower edge") } : Array.Empty<(string, string)>();
            case DrawingKind.AnchoredVwap:
            {
                var parts = new List<(string, string)> { ("vwap", "VWAP") };
                if (Band1 > 0) parts.AddRange(new[] { ("u1", "+" + Band1.ToString("0.##", ic) + "σ"), ("d1", "−" + Band1.ToString("0.##", ic) + "σ") });
                if (Band2 > 0) parts.AddRange(new[] { ("u2", "+" + Band2.ToString("0.##", ic) + "σ"), ("d2", "−" + Band2.ToString("0.##", ic) + "σ") });
                return parts;
            }
            case DrawingKind.FibRetracement:
                return EffectiveLevels.Where(l => l.Visible).Select(l => ("fib:" + l.Value.ToString(ic), $"Level {l.Value.ToString("0.###", ic)}")).ToArray();
            default:
                return Array.Empty<(string, string)>();
        }
    }

    /// <summary>Drawings with two edges (the channel conditions use both).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsChannel => Kind is DrawingKind.Rectangle or DrawingKind.ParallelChannel;

    /// <summary>
    /// Price of one of this drawing's lines at fractional bar <paramref name="bar"/>; null where the line does not exist
    /// (before a ray starts, outside a segment that is not extended). The anchored VWAP is computed from trades by the engine.
    /// </summary>
    public double? ValueAt(CandleSeries series, double bar, string part)
    {
        if (Points.Count == 0) return null;
        double b0 = BarPos(series, Points[0].Time), p0 = Points[0].Price;
        double b1 = Points.Count > 1 ? BarPos(series, Points[1].Time) : b0, p1 = Points.Count > 1 ? Points[1].Price : p0;
        const double eps = 1e-9;
        double Line(double at) => Math.Abs(b1 - b0) < eps ? p0 : p0 + (at - b0) * (p1 - p0) / (b1 - b0);
        bool Within(bool left, bool right) => (left || bar >= Math.Min(b0, b1) - eps) && (right || bar <= Math.Max(b0, b1) + eps);

        switch (Kind)
        {
            case DrawingKind.HorizontalLine:
            case DrawingKind.CrossLine:
                return p0;
            case DrawingKind.HorizontalRay:
                return ExtendLeft || bar >= b0 - eps ? p0 : null;
            case DrawingKind.TrendLine:
                return Within(ExtendLeft, ExtendRight) ? Line(bar) : null;
            case DrawingKind.Arrow:
                return Within(false, false) ? Line(bar) : null;
            case DrawingKind.Ray:
                return (b1 >= b0 ? bar >= b0 - eps : bar <= b0 + eps) ? Line(bar) : null;
            case DrawingKind.ExtendedLine:
                return Line(bar);
            case DrawingKind.Rectangle:
                if (!Within(ExtendLeft, ExtendRight)) return null;
                return part == "bottom" ? Math.Min(p0, p1) : Math.Max(p0, p1);
            case DrawingKind.ParallelChannel:
            {
                if (Points.Count < 3 || !Within(ExtendLeft, ExtendRight)) return null;
                double a = Line(bar), b = a + ChannelOffset;
                return part switch
                {
                    "middle" => (a + b) / 2,
                    "lower" => Math.Min(a, b),
                    _ => Math.Max(a, b),
                };
            }
            case DrawingKind.FibRetracement:
            {
                if (!part.StartsWith("fib:", StringComparison.Ordinal) || !double.TryParse(part[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var lvl)) return null;
                if (!ExtendLeft && bar < Math.Min(b0, b1) - eps) return null;
                return p1 + (p0 - p1) * (1 - lvl);
            }
            default:
                return null;
        }
    }
}
