using SkiaSharp;

namespace TradingHub.Charting.Drawings;

/// <summary>What a hand-drawn stroke was recognized as.</summary>
public enum InkShapeKind
{
    Scribble,
    Horizontal,
    Vertical,
    TrendLine,
    Ray,
    Rectangle,
    Ellipse,
    Arrow,
    Fib,
    Path,
}

/// <summary>A recognized stroke, in screen pixels.</summary>
public sealed class InkShape
{
    public InkShapeKind Kind { get; init; }

    /// <summary>Start and end (for the rectangle and the ellipse, the top-left and bottom-right corners).</summary>
    public SKPoint A { get; init; }

    public SKPoint B { get; init; }

    /// <summary>Height of a horizontal line (the stroke's average).</summary>
    public float? Level { get; init; }

    /// <summary>The stroke's corners (path).</summary>
    public IReadOnlyList<SKPoint> Corners { get; init; } = Array.Empty<SKPoint>();

    /// <summary>The whole stroke, resampled at even spacing.</summary>
    public IReadOnlyList<SKPoint> Raw { get; init; } = Array.Empty<SKPoint>();

    public SKRect Bounds { get; init; }
}

/// <summary>A recognized stroke in chart time and price (kept to retype the drawing afterwards).</summary>
public sealed class InkGeometry
{
    public ChartPoint Start { get; init; }
    public ChartPoint End { get; init; }
    public double? Level { get; init; }
    public IReadOnlyList<ChartPoint> Corners { get; init; } = Array.Empty<ChartPoint>();
    public IReadOnlyList<ChartPoint> Raw { get; init; } = Array.Empty<ChartPoint>();
}

/// <summary>What a gesture left: the new drawing (with the kinds it can be retyped to) or what a scribble erased.</summary>
public sealed class InkResult
{
    public ChartDrawing? Drawing { get; init; }
    public InkGeometry? Geometry { get; init; }
    public IReadOnlyList<DrawingKind> Alternatives { get; init; } = Array.Empty<DrawingKind>();

    /// <summary>Scribble: the drawings it erased, with their list position so they can be restored.</summary>
    public bool Erase { get; init; }

    public IReadOnlyList<(int Index, ChartDrawing Drawing)> Removed { get; init; } = Array.Empty<(int, ChartDrawing)>();

    /// <summary>Where the pointer was released (the gesture chip appears there).</summary>
    public float X { get; init; }

    public float Y { get; init; }

    /// <summary>Drawings were hidden and the gesture showed them again.</summary>
    public bool Unhid { get; init; }
}

/// <summary>
/// Recognizes a stroke drawn while holding the mouse button: a horizontal stroke is a horizontal line, a vertical one a
/// vertical line, a diagonal a trend line (a ray when it reaches the right edge), a loop an ellipse, a box a rectangle, a
/// line with a hook at the end an arrow, a Z a Fibonacci retracement, a scribble erases what it touches and anything else
/// becomes a path. Pure geometry: no clock, no chart state.
/// </summary>
public static class InkRecognizer
{
    /// <summary>How long the button must be held without moving before the gesture starts.</summary>
    public const int HoldMs = 320;

    /// <summary>How far the pointer may move while holding (more is a chart drag).</summary>
    public const float HoldSlop = 5;

    /// <summary>When the filling ring becomes visible.</summary>
    public const int RingStartMs = 80;

    private const float MinLength = 14;
    private const int Samples = 96;
    private const float CornerAngle = 38, SharpAngle = 65;

    public static InkShape? Recognize(IReadOnlyList<SKPoint> input, float paneRight)
    {
        if (input.Count < 2) return null;
        float len = Length(input);
        if (len < MinLength) return null;
        return Classify(Resample(input, Samples), len, paneRight, allowTrim: true);
    }

    private static InkShape Classify(List<SKPoint> e, float len, float paneRight, bool allowTrim)
    {
        var bounds = BoundsOf(e);
        float diag = Math.Max(1, MathF.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height));
        var start = e[0];
        var end = e[^1];

        // scribble: back and forth many times
        int reversals = Math.Max(Reversals(e, horizontal: true), Reversals(e, horizontal: false));
        if (reversals >= 5 || (reversals >= 4 && len > 2.5f * diag))
            return new InkShape { Kind = InkShapeKind.Scribble, A = start, B = end, Raw = e, Bounds = bounds };

        var simple = Simplify(e, Math.Max(7, diag * 0.06f));
        bool closed = simple.Count >= 4 && Dist(start, end) < Math.Max(14, len * 0.06f) && Dist(start, end) < diag * 0.3f;
        var cornerIdx = new List<int>();
        for (int i = 1; i < simple.Count - 1; i++)
            if (Turn(simple[i - 1], simple[i], simple[i + 1]) > CornerAngle) cornerIdx.Add(i);
        var corners = cornerIdx.Select(i => simple[i]).ToList();

        if (!closed && IsArrow(e, out var tip))
            return new InkShape { Kind = InkShapeKind.Arrow, A = start, B = tip, Raw = e, Bounds = bounds };

        if (closed)
        {
            int sharp = cornerIdx.Count(i => Turn(simple[i - 1], simple[i], simple[i + 1]) > SharpAngle);
            var kind = sharp < 3 && len < 3.6f * diag ? InkShapeKind.Ellipse : InkShapeKind.Rectangle;
            return new InkShape { Kind = kind, A = new SKPoint(bounds.Left, bounds.Top), B = new SKPoint(bounds.Right, bounds.Bottom), Raw = e, Bounds = bounds };
        }

        // an accidental hook when releasing does not change what was drawn
        if (allowTrim && TrimHook(e, len) is { } trimmed)
            return Classify(Resample(trimmed, Samples), Length(trimmed), paneRight, allowTrim: false);

        if (corners.Count == 0)
        {
            float angle = MathF.Abs(MathF.Atan2(end.Y - start.Y, end.X - start.X)) * 180 / MathF.PI;
            bool horizontal = angle < 3 || angle > 177 || Math.Abs(end.Y - start.Y) <= 6;
            bool vertical = Math.Abs(angle - 90) < 6;
            if (horizontal && Dist(start, end) > 30)
            {
                float y = e.Average(p => p.Y);
                return new InkShape { Kind = InkShapeKind.Horizontal, A = new SKPoint(start.X, y), B = new SKPoint(end.X, y), Level = y, Raw = e, Bounds = bounds };
            }

            if (vertical && Dist(start, end) > 30)
            {
                float x = e.Average(p => p.X);
                return new InkShape { Kind = InkShapeKind.Vertical, A = new SKPoint(x, start.Y), B = new SKPoint(x, end.Y), Raw = e, Bounds = bounds };
            }

            var lineKind = end.X >= paneRight - 2 ? InkShapeKind.Ray : InkShapeKind.TrendLine;
            return new InkShape { Kind = lineKind, A = start, B = end, Raw = e, Bounds = bounds };
        }

        // Z: three legs, the first and last parallel (same direction or back and forth) and the middle one slanted →
        // Fibonacci between those two levels
        if (corners.Count == 2)
        {
            var c1 = corners[0];
            var c2 = corners[1];
            float r = Angle(start, c1), mid = Angle(c1, c2), a = Angle(c2, end);
            bool parallel = LineDiff(r, a) < 30;
            bool slanted = LineDiff(r, mid) > 25;
            bool long3 = Dist(c1, c2) > 20 && Dist(start, c1) > 20 && Dist(c2, end) > 20;
            if (parallel && slanted && long3 && LineDiff(r, 0) < 45)
            {
                int i1 = Math.Max(1, e.IndexOf(c1)), i2 = Math.Max(0, e.IndexOf(c2));
                float y1 = e.Take(i1).Average(p => p.Y);
                float y2 = e.Skip(i2).Average(p => p.Y);
                return new InkShape
                {
                    Kind = InkShapeKind.Fib,
                    A = new SKPoint(Math.Min(start.X, c1.X), y1),
                    B = new SKPoint(Math.Max(c2.X, end.X), y2),
                    Corners = corners,
                    Raw = e,
                    Bounds = bounds,
                };
            }
        }

        return new InkShape { Kind = InkShapeKind.Path, A = start, B = end, Corners = corners, Raw = e, Bounds = bounds };
    }

    /// <summary>Arrow: a straight line and, at the end, a short hook back (the tip is the point farthest from the start).</summary>
    private static bool IsArrow(List<SKPoint> e, out SKPoint tip)
    {
        tip = default;
        var start = e[0];
        var end = e[^1];
        int far = 0;
        float maxD = 0;
        for (int i = 0; i < e.Count; i++)
        {
            float d = Dist(start, e[i]);
            if (d > maxD)
            {
                maxD = d;
                far = i;
            }
        }

        float hook = Dist(e[far], end);
        if (far <= e.Count * 0.6 || far >= e.Count - 2 || hook <= 18 || hook >= maxD * 0.35f) return false;
        if (Turn(e[Math.Max(0, far - 4)], e[far], end) <= 90) return false;
        float dev = 0;
        for (int i = 0; i < far; i++) dev = Math.Max(dev, SegmentDistance(e[i], start, e[far]));
        if (dev >= Math.Max(8, maxD * 0.06f)) return false;
        tip = e[far];
        return true;
    }

    /// <summary>Removes a short broken last leg (the flick when lifting the finger); null when there is none.</summary>
    private static List<SKPoint>? TrimHook(List<SKPoint> e, float len)
    {
        var s = Simplify(e, 5);
        if (s.Count < 3) return null;
        var r = s[^2];
        var last = s[^1];
        if (Dist(r, last) >= Math.Min(24, len * 0.12f) || Turn(s[^3], r, last) <= CornerAngle) return null;
        int i = e.IndexOf(r);
        return i > 0 ? e.Take(i + 1).ToList() : null;
    }

    // ------------------------------------------------------------------ geometry

    private static float Dist(SKPoint a, SKPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static float Length(IReadOnlyList<SKPoint> pts)
    {
        float t = 0;
        for (int i = 1; i < pts.Count; i++) t += Dist(pts[i - 1], pts[i]);
        return t;
    }

    /// <summary>The stroke with <paramref name="n"/> evenly spaced points.</summary>
    public static List<SKPoint> Resample(IReadOnlyList<SKPoint> pts, int n)
    {
        var res = new List<SKPoint>(n);
        if (pts.Count == 0) return res;
        float step = Length(pts) / (n - 1);
        if (!(step > 0))
        {
            for (int i = 0; i < n; i++) res.Add(pts[0]);
            return res;
        }

        res.Add(pts[0]);
        float acc = 0;
        var prev = pts[0];
        int k = 1;
        while (k < pts.Count && res.Count < n)
        {
            var cur = pts[k];
            float d = Dist(prev, cur);
            if (d > 0 && acc + d >= step)
            {
                float t = (step - acc) / d;
                var q = new SKPoint(prev.X + t * (cur.X - prev.X), prev.Y + t * (cur.Y - prev.Y));
                res.Add(q);
                prev = q;
                acc = 0;
                continue; // continue from the new point towards the same target
            }

            acc += d;
            prev = cur;
            k++;
        }

        while (res.Count < n) res.Add(pts[^1]);
        return res;
    }

    private static SKRect BoundsOf(IReadOnlyList<SKPoint> pts) =>
        new(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));

    public static float SegmentDistance(SKPoint p, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        float t = len2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1) : 0;
        return Dist(p, new SKPoint(a.X + t * dx, a.Y + t * dy));
    }

    /// <summary>Ramer–Douglas–Peucker: keeps the points needed so the stroke never deviates more than <paramref name="eps"/>.</summary>
    public static List<SKPoint> Simplify(IReadOnlyList<SKPoint> pts, float eps)
    {
        if (pts.Count < 3) return pts.ToList();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float best = 0;
            int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = SegmentDistance(pts[i], pts[a], pts[b]);
                if (d > best)
                {
                    best = d;
                    idx = i;
                }
            }

            if (idx < 0 || best <= eps) continue;
            keep[idx] = true;
            stack.Push((a, idx));
            stack.Push((idx, b));
        }

        var res = new List<SKPoint>();
        for (int i = 0; i < pts.Count; i++)
            if (keep[i]) res.Add(pts[i]);
        return res;
    }

    /// <summary>Indices kept by <see cref="Simplify"/> (so callers can keep the matching chart points).</summary>
    public static List<int> SimplifyIndices(IReadOnlyList<SKPoint> pts, float eps)
    {
        var kept = Simplify(pts, eps);
        var res = new List<int>(kept.Count);
        int k = 0;
        for (int i = 0; i < pts.Count && k < kept.Count; i++)
        {
            if (pts[i] != kept[k]) continue;
            res.Add(i);
            k++;
        }

        return res;
    }

    private static float Angle(SKPoint a, SKPoint b) => MathF.Atan2(b.Y - a.Y, b.X - a.X);

    /// <summary>How much the stroke turns at <paramref name="b"/> (0 = straight on, 180 = back).</summary>
    private static float Turn(SKPoint a, SKPoint b, SKPoint c) => DirDiff(Angle(a, b), Angle(b, c));

    private static float DirDiff(float a, float b)
    {
        float d = b - a;
        while (d > MathF.PI) d -= 2 * MathF.PI;
        while (d < -MathF.PI) d += 2 * MathF.PI;
        return MathF.Abs(d) * 180 / MathF.PI;
    }

    /// <summary>Difference between two directions as undirected lines: 0 to 90 degrees.</summary>
    private static float LineDiff(float a, float b)
    {
        float d = MathF.Abs(a - b) * 180 / MathF.PI % 180;
        return Math.Min(d, 180 - d);
    }

    /// <summary>How many times the stroke turns back along an axis (with hysteresis so hand tremor does not count).</summary>
    private static int Reversals(IReadOnlyList<SKPoint> e, bool horizontal)
    {
        const float hysteresis = 10;
        int n = 0, dir = 0;
        float acc = 0;
        for (int i = 1; i < e.Count; i++)
        {
            float d = horizontal ? e[i].X - e[i - 1].X : e[i].Y - e[i - 1].Y;
            if (dir == 0)
            {
                acc += d;
                if (Math.Abs(acc) > hysteresis)
                {
                    dir = Math.Sign(acc);
                    acc = 0;
                }

                continue;
            }

            if (Math.Sign(d) == dir || d == 0)
            {
                acc = 0;
            }
            else
            {
                acc += d;
                if (Math.Abs(acc) > hysteresis)
                {
                    n++;
                    dir = -dir;
                    acc = 0;
                }
            }
        }

        return n;
    }
}

/// <summary>How a recognized stroke becomes a chart drawing and which kinds it can be retyped to.</summary>
public static class InkDrawings
{
    /// <summary>The drawing for a recognized shape (a scribble has none: it erases).</summary>
    public static DrawingKind? KindOf(InkShapeKind k) => k switch
    {
        InkShapeKind.Horizontal => DrawingKind.HorizontalLine,
        InkShapeKind.Vertical => DrawingKind.VerticalLine,
        InkShapeKind.TrendLine => DrawingKind.TrendLine,
        InkShapeKind.Ray => DrawingKind.Ray,
        InkShapeKind.Rectangle => DrawingKind.Rectangle,
        InkShapeKind.Ellipse => DrawingKind.Ellipse,
        InkShapeKind.Arrow => DrawingKind.Arrow,
        InkShapeKind.Fib => DrawingKind.FibRetracement,
        InkShapeKind.Path => DrawingKind.Path,
        _ => null,
    };

    /// <summary>The kinds a gesture drawing can be switched to (in case the recognizer chose something else).</summary>
    public static IReadOnlyList<DrawingKind> Alternatives(DrawingKind k) => k switch
    {
        DrawingKind.TrendLine => new[] { DrawingKind.Ray, DrawingKind.ExtendedLine, DrawingKind.HorizontalLine },
        DrawingKind.Ray => new[] { DrawingKind.TrendLine, DrawingKind.ExtendedLine },
        DrawingKind.ExtendedLine => new[] { DrawingKind.TrendLine, DrawingKind.Ray },
        DrawingKind.HorizontalLine => new[] { DrawingKind.TrendLine, DrawingKind.HorizontalRay },
        DrawingKind.HorizontalRay => new[] { DrawingKind.HorizontalLine, DrawingKind.TrendLine },
        DrawingKind.VerticalLine => new[] { DrawingKind.TrendLine, DrawingKind.CrossLine },
        DrawingKind.Rectangle => new[] { DrawingKind.Ellipse },
        DrawingKind.Ellipse => new[] { DrawingKind.Rectangle },
        DrawingKind.FibRetracement => new[] { DrawingKind.Rectangle, DrawingKind.TrendLine },
        DrawingKind.Path => new[] { DrawingKind.Brush, DrawingKind.TrendLine },
        DrawingKind.Brush => new[] { DrawingKind.Path },
        DrawingKind.Arrow => new[] { DrawingKind.TrendLine, DrawingKind.Ray },
        _ => Array.Empty<DrawingKind>(),
    };

    /// <summary>The points of a <paramref name="kind"/> drawing made from this stroke.</summary>
    public static List<ChartPoint> PointsFor(DrawingKind kind, InkGeometry g)
    {
        double level = g.Level ?? g.Start.Price;
        switch (kind)
        {
            case DrawingKind.HorizontalLine:
                return new List<ChartPoint> { new(g.Start.Time, level) };
            case DrawingKind.HorizontalRay:
                return new List<ChartPoint> { new(g.Start.Time <= g.End.Time ? g.Start.Time : g.End.Time, level) };
            case DrawingKind.VerticalLine:
            case DrawingKind.CrossLine:
            case DrawingKind.Text:
                return new List<ChartPoint> { g.Start };
            case DrawingKind.Path:
                return new[] { g.Start }.Concat(g.Corners).Append(g.End).ToList();
            case DrawingKind.Brush:
            case DrawingKind.Highlighter:
                return g.Raw.Count >= 2 ? g.Raw.ToList() : new List<ChartPoint> { g.Start, g.End };
            default:
                return new List<ChartPoint> { g.Start, g.End };
        }
    }

    /// <summary>
    /// The drawings a scribble crosses at least twice. Hidden or locked drawings (one by one or the whole chart) are never
    /// erased.
    /// </summary>
    public static List<ChartDrawing> ScribbleVictims(ChartModel model, ChartLayout layout, IReadOnlyList<SKPoint> stroke, Func<ChartDrawing, ChartDrawingRuntime?> runtime)
    {
        var res = new List<ChartDrawing>();
        if (model.Settings.HideDrawings || model.Settings.LockDrawings) return res;
        foreach (var d in model.Drawings)
        {
            if (d.Locked || !d.IsVisibleOn(model.Series.Period)) continue;
            var rt = runtime(d);
            int hits = 0;
            foreach (var p in stroke)
            {
                if (!d.HitTest(layout, model.Series, rt, p.X, p.Y, out _)) continue;
                if (++hits >= 2) break;
            }

            if (hits >= 2) res.Add(d);
        }

        return res;
    }
}
