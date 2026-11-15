using System.Drawing;
using System.Globalization;
using TradingHub.Charting.Drawings;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Rendering.Context;

namespace TradingHub.Charting;

/// <summary>
/// A line an indicator drew on the price chart (DrawLine, DrawLines, a box edge or one of its trend/till-touch line
/// objects), kept in time and price, never pixels. The chart records them while drawing each indicator, so any line of any
/// indicator (including a freshly written one) can be picked with a right-click and carry an alert without declaring it.
/// </summary>
public sealed class DrawnLine
{
    /// <summary>Point instants, left to right…</summary>
    public DateTime[] Times { get; set; } = Array.Empty<DateTime>();

    /// <summary>…and their prices.</summary>
    public decimal[] Prices { get; set; } = Array.Empty<decimal>();

    /// <summary>Stroke colour (ARGB): tells the lines of one indicator apart when following them.</summary>
    public uint Color { get; set; }

    /// <summary>Reaches the last bar (or the right edge): continues to the right while the indicator keeps drawing it.</summary>
    public bool Right { get; set; }

    /// <summary>Starts at the chart's left edge: what came before was not seen.</summary>
    public bool CutLeft { get; set; }

    /// <summary>Text the indicator wrote next to the line (its name), if any.</summary>
    public string Text { get; set; } = "";

    /// <summary>From the latest capture: fractional bar positions in the captured series.</summary>
    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore] internal double[]? Bars;

    /// <summary>From the latest capture: pixel points (right-click hit tests and highlighting).</summary>
    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore] internal PointF[]? Pixels;

    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore] public decimal LastPrice => Prices.Length > 0 ? Prices[^1] : 0;

    internal double[] BarsIn(CandleSeries series)
    {
        if (Bars != null) return Bars;
        var bars = new double[Times.Length];
        for (int i = 0; i < bars.Length; i++) bars[i] = ChartDrawing.BarPos(series, Times[i]);
        return bars;
    }

    /// <summary>
    /// Price of the line at fractional bar <paramref name="bar"/> (null outside it). When it continues right, a two-point
    /// straight line keeps its slope and a curve keeps its last price.
    /// </summary>
    public decimal? ValueAt(CandleSeries series, double bar)
    {
        if (Prices.Length == 0) return null;
        var bars = BarsIn(series);
        if (bars.Length != Prices.Length) return null;
        if (bar < bars[0] - 0.01) return null;
        if (bar > bars[^1] + 0.01)
        {
            if (!Right) return null;
            if (bars.Length == 2 && bars[1] - bars[0] > 0.5 && Prices[0] != Prices[1])
                return Prices[1] + (Prices[1] - Prices[0]) * (decimal)((bar - bars[1]) / (bars[1] - bars[0]));
            return Prices[^1];
        }

        for (int i = 1; i < bars.Length; i++)
        {
            if (bar > bars[i] + 0.01) continue;
            double span = bars[i] - bars[i - 1];
            if (span <= 1e-9) return Prices[i];
            double f = Math.Clamp((bar - bars[i - 1]) / span, 0, 1);
            return Prices[i - 1] + (Prices[i] - Prices[i - 1]) * (decimal)f;
        }

        return Prices[^1];
    }

    /// <summary>Points persisted with an alert: for a long curve, the latest ones (what matters for alerting).</summary>
    internal const int MaxSnapshotPoints = 400;

    /// <summary>A copy saved with an alert, in instants: it survives more history being loaded and restarts.</summary>
    public DrawnLine Snapshot(CandleSeries series)
    {
        int n = Bars?.Length ?? Times.Length;
        int skip = Math.Max(0, n - MaxSnapshotPoints);
        var times = new DateTime[n - skip];
        for (int i = 0; i < times.Length; i++) times[i] = Bars != null ? TimeAt(series, Bars[skip + i]) : Times[skip + i];
        return new DrawnLine
        {
            Times = times,
            Prices = Prices[skip..],
            Color = Color,
            Right = Right,
            CutLeft = CutLeft || skip > 0,
            Text = Text,
        };
    }

    public DrawnLine Clone() => new()
    {
        Times = (DateTime[])Times.Clone(), Prices = (decimal[])Prices.Clone(), Color = Color, Right = Right, CutLeft = CutLeft, Text = Text,
        Bars = (double[]?)Bars?.Clone(), Pixels = (PointF[]?)Pixels?.Clone(),
    };

    /// <summary>Key that recognises the line in lists (colour, start and price).</summary>
    internal string Key(CandleSeries series)
    {
        var bars = BarsIn(series);
        string start = CutLeft || bars.Length == 0 ? "edge" : TimeAt(series, bars[0]).Ticks.ToString(CultureInfo.InvariantCulture);
        return $"{ChartAlertEngine.DrawnPrefix}{Color:X8}:{start}:{LastPrice.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Instant of a fractional bar position (past the last bar it advances at the period's pace).</summary>
    internal static DateTime TimeAt(CandleSeries series, double b)
    {
        if (series.Count == 0) return DateTime.UtcNow;
        int bar = (int)Math.Floor(b);
        double frac = b - bar;
        if (bar >= series.Count - 1)
        {
            var dur = series.Period.ApproxDuration;
            var last = series[^1].Time;
            return dur > TimeSpan.Zero ? last + TimeSpan.FromTicks((long)(dur.Ticks * (b - (series.Count - 1)))) : last;
        }

        if (bar < 0) return series[0].Time;
        var t0 = series[bar].Time;
        var t1 = series[bar + 1].Time;
        return t0 + TimeSpan.FromTicks((long)((t1 - t0).Ticks * frac));
    }

    /// <summary>
    /// The line among <paramref name="lines"/> (the latest capture of the same indicator) that is <paramref name="old"/>
    /// now: same colour, starting on the same bar and the closest price (a forming value-area edge moves). Null when it is
    /// not there (for example the chart shows another part of history).
    /// </summary>
    internal static DrawnLine? Match(CandleSeries series, DrawnLine old, IReadOnlyList<DrawnLine> lines)
    {
        var ob = old.BarsIn(series);
        if (ob.Length == 0) return null;
        decimal oldRef = old.LastPrice;
        DrawnLine? best = null;
        decimal bestD = decimal.MaxValue;
        foreach (var l in lines)
        {
            if (l.Color != old.Color || l.Prices.Length == 0) continue;
            var lb = l.BarsIn(series);
            decimal d;
            if (!old.CutLeft && !l.CutLeft)
            {
                if (Math.Abs(lb[0] - ob[0]) > 1.01) continue;
                // the same line moved a little (a forming level): up to 3 %
                d = Math.Abs(l.LastPrice - oldRef);
                if (oldRef != 0 && d / Math.Abs(oldRef) > 0.03m) continue;
            }
            else
            {
                // starts off screen: it must pass through the same price where both overlap (one bar before the end when
                // possible: a curve's tip moves with the live price)
                double at = Math.Max(lb[0], ob[0]), until = Math.Min(lb[^1], ob[^1]);
                if (until < at - 0.01) continue;
                double probe = Math.Max(at, until - 1);
                if (l.ValueAt(series, probe) is not { } a || old.ValueAt(series, probe) is not { } b) continue;
                d = Math.Abs(a - b);
                if (oldRef != 0 && d / Math.Abs(oldRef) > 0.0005m) continue;
            }

            if (old.Text.Length > 0 && l.Text != old.Text) d += Math.Abs(oldRef) * 0.0001m + 0.0000001m; // ties go to the same name
            if (d >= bestD) continue;
            bestD = d;
            best = l;
        }

        return best;
    }
}

/// <summary>
/// Records what one indicator draws on the price panel in one frame (lines, texts and the prices it converted to Y). One
/// instance per indicator slot, reused every frame: recording only appends to bounded buffers, and the lines are built on
/// demand from the latest frame (right-click, alert dialog, alert tracking).
/// </summary>
internal sealed class DrawnLineCapture : IRenderCapture
{
    internal const int MaxLines = 3000;
    internal const int MaxSegments = 40_000;
    internal const int MaxPoints = 100_000;
    internal const int MaxTexts = 2000;
    internal const int MaxConversions = 50_000;
    private const float MinWidth = 12;

    private readonly record struct Segment(PointF A, PointF B, uint Color, decimal? ExactA, decimal? ExactB);
    private readonly record struct Polyline(int Start, int Count, uint Color);

    // single DrawLine calls: a curve drawn bar by bar is many touching segments, chained into one line on demand
    private readonly List<Segment> _segments = new();
    private readonly List<Polyline> _polylines = new();
    private readonly List<PointF> _points = new();
    private readonly List<decimal?> _exact = new();
    private readonly List<(string Text, RectangleF Box)> _texts = new();

    /// <summary>Latest price converted to each Y.</summary>
    private readonly Dictionary<int, decimal> _yPrices = new();

    /// <summary>Conversions in order: a line's points are the ones requested just before drawing it.</summary>
    private readonly List<(int Y, decimal Price)> _log = new();

    private ChartModel? _model;
    private ChartLayout? _layout;
    private Indicator? _objects;
    private IReadOnlyList<DrawnLine> _lines = Array.Empty<DrawnLine>();
    private bool _built = true;

    /// <summary>Changes with every captured frame.</summary>
    public int Generation { get; private set; }

    /// <summary>Lines of the latest captured frame (built on first use after each frame).</summary>
    public IReadOnlyList<DrawnLine> Lines
    {
        get
        {
            if (!_built)
            {
                _built = true;
                _lines = Build();
            }

            return _lines;
        }
    }

    public void Begin()
    {
        _segments.Clear();
        _polylines.Clear();
        _points.Clear();
        _exact.Clear();
        _texts.Clear();
        _yPrices.Clear();
        _log.Clear();
        _objects = null;
        _model = null;
        _layout = null;
    }

    /// <summary>Ends a frame; <paramref name="objects"/> also contributes its trend/till-touch lines and box edges.</summary>
    public void End(ChartModel model, ChartLayout layout, Indicator? objects)
    {
        _model = model;
        _layout = layout;
        _objects = objects;
        _built = false;
        _lines = Array.Empty<DrawnLine>();
        Generation++;
    }

    /// <summary>Forgets everything (the indicator is hidden or not drawn).</summary>
    public void Clear()
    {
        bool had = _lines.Count > 0 || !_built;
        Begin();
        _lines = Array.Empty<DrawnLine>();
        _built = true;
        if (had) Generation++;
    }

    public void Convert(int y, decimal price)
    {
        _yPrices[y] = price;
        if (_log.Count < MaxConversions) _log.Add((y, price));
    }

    void IRenderCapture.Line(float x1, float y1, float x2, float y2, Color color)
    {
        if (color.A < 40 || !Finite(x1) || !Finite(y1) || !Finite(x2) || !Finite(y2) || MathF.Abs(x2 - x1) < 0.5f) return;
        if (_segments.Count >= MaxSegments) return;
        var a = new PointF(x1, y1);
        var b = new PointF(x2, y2);
        decimal? ea = null, eb = null;
        int cursor = ExactAt(y2, _log.Count - 1, ref eb);
        ExactAt(y1, cursor, ref ea);
        uint argb = (uint)color.ToArgb();
        _segments.Add(x1 <= x2 ? new Segment(a, b, argb, ea, eb) : new Segment(b, a, argb, eb, ea));
    }

    void IRenderCapture.Lines(Point[] points, Color color)
    {
        if (!Admit(points.Length, color)) return;
        int start = _points.Count;
        foreach (var p in points) _points.Add(p);
        Commit(start, color);
    }

    void IRenderCapture.Lines(PointF[] points, Color color)
    {
        if (!Admit(points.Length, color)) return;
        int start = _points.Count;
        foreach (var p in points)
        {
            if (!Finite(p.X) || !Finite(p.Y))
            {
                _points.RemoveRange(start, _points.Count - start);
                return;
            }

            _points.Add(p);
        }

        Commit(start, color);
    }

    void IRenderCapture.Text(string text, RectangleF box)
    {
        if (_texts.Count < MaxTexts && !string.IsNullOrWhiteSpace(text)) _texts.Add((text.Trim(), box));
    }

    private bool Admit(int count, Color color) =>
        color.A >= 40 && count >= 2 && _polylines.Count < MaxLines && _points.Count + count <= MaxPoints;

    private void Commit(int start, Color color)
    {
        int count = _points.Count - start;
        float minX = float.MaxValue, maxX = float.MinValue;
        for (int i = start; i < _points.Count; i++)
        {
            minX = Math.Min(minX, _points[i].X);
            maxX = Math.Max(maxX, _points[i].X);
        }

        if (maxX - minX < MinWidth)
        {
            _points.RemoveRange(start, count);
            return;
        }

        // exact prices, matched from the conversions requested just before drawing (walking backwards, so two points at the
        // same height with different prices are not confused)
        for (int i = 0; i < count; i++) _exact.Add(null);
        int cursor = _log.Count - 1;
        for (int i = count - 1; i >= 0; i--)
        {
            decimal? exact = null;
            cursor = ExactAt(_points[start + i].Y, cursor, ref exact);
            _exact[start + i] = exact;
        }

        _polylines.Add(new Polyline(start, count, (uint)color.ToArgb()));
    }

    /// <summary>The exact price for pixel <paramref name="y"/> from the last three conversions before <paramref name="cursor"/>.</summary>
    private int ExactAt(float y, int cursor, ref decimal? exact)
    {
        int iy = (int)MathF.Round(y);
        if (MathF.Abs(y - iy) >= 0.01f) return cursor;
        for (int k = cursor, n = 0; k >= 0 && n < 3; k--, n++)
        {
            if (_log[k].Y != iy) continue;
            exact = _log[k].Price;
            return k - 1;
        }

        if (_yPrices.TryGetValue(iy, out var price)) exact = price;
        return cursor;
    }

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    // ------------------------------------------------------------------ building the lines

    private List<DrawnLine> Build()
    {
        var res = new List<DrawnLine>();
        if (_model is not { } model || _layout is not { } layout) return res;
        var raw = new List<(PointF[] Pts, uint Color, decimal?[] Exact)>();
        for (int i = 0; i < _polylines.Count; i++)
        {
            var (start, count, color) = _polylines[i];
            raw.Add((_points.GetRange(start, count).ToArray(), color, _exact.GetRange(start, count).ToArray()));
        }

        ChainSegments(raw);
        var panel = layout.Main;
        var series = model.Series;
        int last = series.Count - 1;
        decimal tick = model.Instrument.TickSize > 0 ? model.Instrument.TickSize : 0;
        var rect = panel.Rect;
        var seen = new HashSet<(uint, int, int, int, int)>();
        foreach (var (rawPts, color, rawExact) in raw)
        {
            if (res.Count >= MaxLines) break;
            var pts = rawPts;
            var exact = rawExact;
            if (pts[^1].X < pts[0].X)
            {
                pts = pts.Reverse().ToArray();
                exact = exact.Reverse().ToArray();
            }

            // a polyline that turns back on itself is not a price line
            bool monotonic = true;
            for (int i = 1; i < pts.Length && monotonic; i++) monotonic = pts[i].X >= pts[i - 1].X - 0.01f;
            if (!monotonic) continue;
            if (pts.All(p => p.Y < rect.Top - 2) || pts.All(p => p.Y > rect.Bottom + 2)) continue;
            if (!seen.Add((color, (int)pts[0].X, (int)pts[0].Y, (int)pts[^1].X, (int)pts[^1].Y))) continue;
            if (IsLabelFrame(pts)) continue;

            var bars = new double[pts.Length];
            var prices = new decimal[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                bars[i] = layout.BarAtExact(pts[i].X);
                if (exact[i] is { } e) prices[i] = e;
                else
                {
                    decimal v = (decimal)Math.Clamp(panel.ValueAt(pts[i].Y), -7.9e27, 7.9e27);
                    prices[i] = tick > 0 ? Math.Round(v / tick) * tick : v;
                }
            }

            res.Add(new DrawnLine
            {
                Bars = bars,
                Prices = prices,
                Pixels = pts,
                Color = color,
                Right = pts[^1].X >= rect.Right - 2 || bars[^1] >= last - 0.6,
                CutLeft = pts[0].X <= rect.Left + 2,
                Text = LabelFor(pts),
            });
        }

        if (_objects != null) AddObjectLines(res, model, layout, _objects);
        return res;
    }

    /// <summary>Chains same-colour segments that touch end to end into polylines.</summary>
    private void ChainSegments(List<(PointF[] Pts, uint Color, decimal?[] Exact)> raw)
    {
        static (int, int) Key(PointF p) => ((int)MathF.Round(p.X * 2), (int)MathF.Round(p.Y * 2));
        foreach (var group in _segments.GroupBy(sg => sg.Color))
        {
            var chains = new List<(List<PointF> Pts, List<decimal?> Exact)>();
            var byEnd = new Dictionary<(int, int), int>();
            var seen = new HashSet<((int, int), (int, int))>();
            foreach (var sg in group.OrderBy(sg => sg.A.X).ThenBy(sg => sg.B.X))
            {
                if (!seen.Add((Key(sg.A), Key(sg.B)))) continue; // the same segment twice
                if (byEnd.Remove(Key(sg.A), out int c))
                {
                    chains[c].Pts.Add(sg.B);
                    chains[c].Exact.Add(sg.ExactB);
                }
                else
                {
                    c = chains.Count;
                    chains.Add((new List<PointF> { sg.A, sg.B }, new List<decimal?> { sg.ExactA, sg.ExactB }));
                }

                byEnd[Key(sg.B)] = c;
            }

            foreach (var (pts, exact) in chains)
            {
                if (raw.Count >= MaxLines) return;
                if (pts[^1].X - pts[0].X < MinWidth) continue; // small marks and crosses
                raw.Add((pts.ToArray(), group.Key, exact.ToArray()));
            }
        }
    }

    /// <summary>The indicator's trend lines, till-touch lines and box edges: already in bars and prices.</summary>
    private static void AddObjectLines(List<DrawnLine> res, ChartModel model, ChartLayout layout, Indicator ind)
    {
        var main = layout.Main;
        int last = model.Series.Count - 1;
        bool Visible(int a, int b) => Math.Max(a, b) >= layout.FirstVisible - 1 && Math.Min(a, b) <= layout.LastVisible + 1;
        void Add(int b0, decimal p0, int b1, decimal p1, Color color, bool right)
        {
            if (res.Count >= MaxLines || color.A < 40 || b1 < b0) return;
            if (b1 == b0 && !right) return;
            double end = right && b1 == b0 ? last : b1;
            var bars = new[] { (double)b0, end };
            res.Add(new DrawnLine
            {
                Bars = bars,
                Prices = new[] { p0, b1 == b0 ? p0 : p1 },
                Pixels = new[] { new PointF(layout.CenterX(b0), main.Y(p0)), new PointF(right ? layout.Plot.Right : layout.CenterX(end), main.Y(b1 == b0 ? p0 : p1)) },
                Color = (uint)color.ToArgb(),
                Right = right || end >= last - 0.6,
            });
        }

        foreach (var tl in ind.TrendLines)
            if (tl.Pen != null && Visible(tl.FirstBar, tl.SecondBar)) Add(tl.FirstBar, tl.FirstPrice, tl.SecondBar, tl.SecondPrice, tl.Pen.Color, tl.IsRay);
        foreach (var line in ind.HorizontalLinesTillTouch)
        {
            if (line.Pen == null) continue;
            int to = line.Finished ? line.SecondBar : Math.Max(line.SecondBar, line.FirstBar);
            if (!Visible(line.FirstBar, line.Finished ? to : last)) continue;
            bool right = !line.Finished && (line.IsRay || to >= last);
            Add(line.FirstBar, line.FirstPrice, right ? Math.Max(to, line.FirstBar) : to, line.FirstPrice, line.Pen.Color, right);
        }

        foreach (var r in ind.Rectangles)
        {
            if (r.Pen == null || !Visible(r.FirstBar, r.SecondBar)) continue;
            int a = Math.Min(r.FirstBar, r.SecondBar), b = Math.Max(r.FirstBar, r.SecondBar);
            bool right = b >= last;
            Add(a, Math.Max(r.FirstPrice, r.SecondPrice), b, Math.Max(r.FirstPrice, r.SecondPrice), r.Pen.Color, right);
            Add(a, Math.Min(r.FirstPrice, r.SecondPrice), b, Math.Min(r.FirstPrice, r.SecondPrice), r.Pen.Color, right);
        }
    }

    /// <summary>The top or bottom border of a label's frame (a rectangle barely wider than its text).</summary>
    private bool IsLabelFrame(PointF[] pts)
    {
        if (pts.Length != 2 || MathF.Abs(pts[0].Y - pts[1].Y) > 0.5f) return false;
        float minX = pts[0].X, maxX = pts[1].X, y = pts[0].Y;
        foreach (var (_, box) in _texts)
        {
            if (maxX - minX > box.Width * 1.4f + 30) continue;
            if (minX > box.Left + box.Width * 0.3f || maxX < box.Right - box.Width * 0.3f) continue;
            if (y >= box.Top - 6 && y <= box.Bottom + 6) return true;
        }

        return false;
    }

    /// <summary>The text written against the line (above or below, near its ends).</summary>
    private string LabelFor(PointF[] pts)
    {
        if (_texts.Count == 0) return "";
        float minX = pts[0].X, maxX = pts[^1].X;
        string best = "";
        float bestD = float.MaxValue;
        foreach (var (text, box) in _texts)
        {
            if (box.Right < minX - 8 || box.Left > maxX + 140) continue;
            float x = Math.Clamp(box.Left, minX, maxX);
            float y = YAt(pts, x);
            float d = y < box.Top ? box.Top - y : y > box.Bottom ? y - box.Bottom : 0;
            if (d > 6) continue;
            float dx = box.Left > maxX ? box.Left - maxX : 0;
            float score = d * 4 + dx * 0.05f;
            if (score >= bestD) continue;
            bestD = score;
            best = text;
        }

        return best.Length > 40 ? best[..40] + "…" : best;
    }

    internal static float YAt(PointF[] pts, float x)
    {
        if (x <= pts[0].X) return pts[0].Y;
        for (int i = 1; i < pts.Length; i++)
        {
            if (x > pts[i].X) continue;
            float span = pts[i].X - pts[i - 1].X;
            return span <= 0.001f ? pts[i].Y : pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * (x - pts[i - 1].X) / span;
        }

        return pts[^1].Y;
    }

    /// <summary>Pixel distance from (x, y) to the polyline (infinite when x is outside its span).</summary>
    internal static float Distance(PointF[] pts, float x, float y, float slack = 3)
    {
        if (pts.Length < 2 || x < pts[0].X - slack || x > pts[^1].X + slack) return float.MaxValue;
        float best = float.MaxValue;
        for (int i = 1; i < pts.Length; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len2 = dx * dx + dy * dy;
            float t = len2 <= 1e-6f ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
            float px = a.X + t * dx - x, py = a.Y + t * dy - y;
            best = Math.Min(best, MathF.Sqrt(px * px + py * py));
        }

        return best;
    }
}
