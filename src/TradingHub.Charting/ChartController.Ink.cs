using SkiaSharp;
using TradingHub.Charting.Drawings;

namespace TradingHub.Charting;

/// <summary>Drawing magnet: snaps drawing points to the bar's high, low, open or close.</summary>
public enum MagnetMode
{
    Off,
    Weak,
    Strong,
}

// Magnet, gesture drawing (hold still, then trace a shape), freehand strokes, paths, Shift + drag measuring and
// zooming around a point.
public sealed partial class ChartController
{
    /// <summary>Distance (pixels) within which the weak magnet snaps to the bar.</summary>
    public const float WeakMagnetPixels = 14;

    /// <summary>Drawing magnet; Shift always snaps while held.</summary>
    public MagnetMode Magnet { get; set; }

    /// <summary>
    /// Gesture drawing: holding the button still on empty chart space until the ring fills, then tracing a shape without
    /// releasing, creates the recognized drawing. Only reachable after order lines, stop/target handles, the legend,
    /// indicators and drawings have all declined the press.
    /// </summary>
    public bool InkGestures { get; set; }

    /// <summary>Tool kept active with a double-click on its button (does not return to the cursor after each drawing).</summary>
    public DrawingKind? KeepTool { get; set; }

    /// <summary>Monotonic milliseconds for the hold timer (replaceable so tests and scenes never sleep).</summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>A gesture finished: the drawing it left or what the scribble erased.</summary>
    public event Action<InkResult>? InkFinished;

    /// <summary>A drawing was selected with a click.</summary>
    public event Action<ChartDrawing>? DrawingSelected;

    private bool _inkCandidate, _startAuto;
    private long _downTick;
    private readonly List<SKPoint> _ink = new();

    /// <summary>Waiting for the hold or tracing a gesture (the view ticks <see cref="InkTick"/> meanwhile).</summary>
    public bool InkPending => _inkCandidate || _drag == DragMode.Ink;

    /// <summary>Tracing a gesture stroke.</summary>
    public bool IsInking => _drag == DragMode.Ink;

    private void ArmInk(ChartLayout l, float x, float y, int clicks)
    {
        _inkCandidate = InkGestures && clicks == 1 && !_model.Settings.LockDrawings && l.Main.Rect.Contains(x, y);
        _downTick = Clock();
    }

    private void DisarmInk()
    {
        _inkCandidate = false;
        _state.InkHold = null;
    }

    private void ResetInk()
    {
        DisarmInk();
        _ink.Clear();
        _state.InkStroke = null;
        _state.InkVictims = null;
    }

    /// <summary>
    /// Advances the hold: fills the ring and, after <see cref="InkRecognizer.HoldMs"/> ms without moving, starts the
    /// gesture. Returns true when the chart must be redrawn.
    /// </summary>
    public bool InkTick()
    {
        if (!_inkCandidate || _drag != DragMode.Pan)
        {
            _inkCandidate = false;
            if (_state.InkHold == null) return false;
            _state.InkHold = null;
            return true;
        }

        long elapsed = Clock() - _downTick;
        if (elapsed >= InkRecognizer.HoldMs)
        {
            BeginInk();
            return true;
        }

        if (elapsed < InkRecognizer.RingStartMs) return false;
        _state.InkHold = (elapsed - InkRecognizer.RingStartMs) / (float)(InkRecognizer.HoldMs - InkRecognizer.RingStartMs);
        _state.InkHoldAt = new SKPoint(_downX, _downY);
        _model.Invalidate();
        return true;
    }

    private void BeginInk()
    {
        // undo the few pixels the chart panned while the button was held
        var vp = _model.Viewport;
        vp.ScrollBars = _startScroll;
        vp.AutoScale = _startAuto;
        if (!vp.AutoScale)
        {
            vp.PriceTop = _startTop;
            vp.PriceBottom = _startBottom;
        }

        DisarmInk();
        _drag = DragMode.Ink;
        _ink.Clear();
        _ink.Add(new SKPoint(_downX, _downY));
        _state.InkStroke = _ink;
        _state.InkVictims = null;
        _model.Invalidate();
    }

    private void InkMove(float x, float y)
    {
        var last = _ink[^1];
        if ((last.X - x) * (last.X - x) + (last.Y - y) * (last.Y - y) < 2.25f) return;
        _ink.Add(new SKPoint(x, y));
        // while a scribble forms, what it would erase is shown faded
        if (L is { } l && _ink.Count % 3 == 0)
        {
            var shape = InkRecognizer.Recognize(_ink, l.Main.Rect.Right);
            _state.InkVictims = shape?.Kind == InkShapeKind.Scribble ? InkDrawings.ScribbleVictims(_model, l, shape.Raw, _state.FindDrawingRuntime).ToHashSet() : null;
        }
    }

    private void CommitInk(float x, float y)
    {
        var l = L;
        var stroke = _ink.ToList();
        ResetInk();
        _drag = DragMode.None;
        _model.Invalidate();
        if (l == null) return;
        var shape = InkRecognizer.Recognize(stroke, l.Main.Rect.Right);
        if (shape == null) return; // held without tracing anything

        if (shape.Kind == InkShapeKind.Scribble)
        {
            var victims = InkDrawings.ScribbleVictims(_model, l, shape.Raw, _state.FindDrawingRuntime);
            var removed = victims.Select(v => (_model.Drawings.IndexOf(v), v)).ToList();
            foreach (var v in victims) _model.Drawings.Remove(v);
            if (victims.Count > 0) DrawingsChanged?.Invoke();
            InkFinished?.Invoke(new InkResult { Erase = true, Removed = removed, X = x, Y = y });
            return;
        }

        if (InkDrawings.KindOf(shape.Kind) is not { } kind) return;
        bool unhid = _model.Settings.HideDrawings;
        _model.Settings.HideDrawings = false;
        var g = GeometryOf(l, shape);
        var d = new ChartDrawing { Kind = kind, Color = _model.Theme.DrawingDefault };
        if (DrawingDefaults?.Invoke(kind) is { } template) d.CopyStyleFrom(template);
        d.Points = InkDrawings.PointsFor(kind, g);
        foreach (var o in _model.Drawings) o.Selected = false;
        d.Selected = true;
        _model.Drawings.Add(d);
        DrawingsChanged?.Invoke();
        InkFinished?.Invoke(new InkResult { Drawing = d, Geometry = g, Alternatives = InkDrawings.Alternatives(kind), X = x, Y = y, Unhid = unhid });
    }

    /// <summary>The recognized stroke in time and price (with the magnet as configured).</summary>
    private InkGeometry GeometryOf(ChartLayout l, InkShape shape)
    {
        var main = l.Main.Rect;
        ChartPoint At(SKPoint p) => PointAt(Math.Clamp(p.X, main.Left, main.Right), Math.Clamp(p.Y, main.Top, main.Bottom), InputModifiers.None);
        var start = At(shape.A);
        if (shape.Kind == InkShapeKind.Vertical && _model.Series.Count > 0)
        {
            // a vertical line sits on a bar
            int bar = Math.Clamp(l.BarAt(shape.A.X), 0, _model.Series.Count - 1);
            start = new ChartPoint(_model.Series[bar].Time, start.Price);
        }

        return new InkGeometry
        {
            Start = start,
            End = At(shape.B),
            Level = shape.Kind == InkShapeKind.Horizontal && shape.Level is { } y ? LevelPrice(l, shape, y) : null,
            Corners = shape.Corners.Select(At).ToList(),
            Raw = InkRecognizer.Simplify(shape.Raw, 0.75f).Select(p => RawPoint(l, p.X, p.Y)).ToList(),
        };
    }

    /// <summary>Price of a gesture horizontal line: with the magnet, the nearest high/low/open/close of the bars it crossed.</summary>
    private double LevelPrice(ChartLayout l, InkShape shape, float y)
    {
        double price = l.Main.ValueAt(y);
        var series = _model.Series;
        if (Magnet != MagnetMode.Off && series.Count > 0)
        {
            int a = Math.Clamp(l.BarAt(shape.Bounds.Left), 0, series.Count - 1), b = Math.Clamp(l.BarAt(shape.Bounds.Right), 0, series.Count - 1);
            double? best = null;
            float bestD = Magnet == MagnetMode.Strong ? float.MaxValue : 6;
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
            {
                var c = series[i];
                foreach (var t in new[] { c.HighTicks, c.LowTicks, c.OpenTicks, c.CloseTicks })
                {
                    double p = (double)series.TicksToPrice(t);
                    float d = Math.Abs(l.Main.Y(p) - y);
                    if (d >= bestD) continue;
                    bestD = d;
                    best = p;
                }
            }

            if (best is { } snapped) return snapped;
        }

        double tick = (double)_model.Instrument.TickSize;
        return tick > 0 ? Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick : price;
    }

    /// <summary>Changes a gesture drawing's kind (the chip next to it) keeping the same stroke.</summary>
    public void RetypeInk(ChartDrawing d, InkGeometry g, DrawingKind kind)
    {
        d.Kind = kind;
        d.Points = InkDrawings.PointsFor(kind, g);
        DrawingsChanged?.Invoke();
        _model.Invalidate();
    }

    /// <summary>Restores what a scribble erased, at its previous list positions.</summary>
    public void UndoErase(IReadOnlyList<(int Index, ChartDrawing Drawing)> removed)
    {
        foreach (var (i, d) in removed.OrderBy(r => r.Index))
        {
            if (_model.Drawings.Contains(d)) continue;
            _model.Drawings.Insert(Math.Clamp(i, 0, _model.Drawings.Count), d);
        }

        DrawingsChanged?.Invoke();
        _model.Invalidate();
    }

    /// <summary>A point without magnet or tick rounding (freehand).</summary>
    private ChartPoint RawPoint(ChartLayout l, float x, float y) => new(ChartDrawing.TimeAt(l, _model.Series, x), l.Main.ValueAt(y));

    // ======================================================================= freehand and path

    private void StartFreehand(ChartLayout l, DrawingKind tool, float x, float y)
    {
        _active = new ChartDrawing { Kind = tool, Points = { RawPoint(l, x, y) }, Color = _model.Theme.DrawingDefault };
        if (DrawingDefaults?.Invoke(tool) is { } style) _active.CopyStyleFrom(style);
        foreach (var d in _model.Drawings) d.Selected = false;
        _active.Selected = true;
        _model.Drawings.Add(_active);
        _drag = DragMode.Freehand;
        _model.Invalidate();
    }

    private void FreehandMove(ChartLayout l, float x, float y)
    {
        var last = _active!.Points[^1];
        float lx = ChartDrawing.XOf(l, _model.Series, last.Time), ly = l.Main.Y(last.Price);
        if ((lx - x) * (lx - x) + (ly - y) * (ly - y) >= 4) _active.Points.Add(RawPoint(l, x, y));
    }

    private void FinishFreehand()
    {
        if (_active != null)
        {
            // keep only the points needed for the same stroke
            if (L is { } l && _active.Points.Count > 2)
            {
                var keep = InkRecognizer.SimplifyIndices(_active.ScreenPoints(l, _model.Series), 0.75f);
                _active.Points = keep.Select(i => _active.Points[i]).ToList();
            }

            if (_active.Points.Count < 2) _model.Drawings.Remove(_active);
        }

        FinishDrawing();
        _active = null;
        _model.Invalidate();
    }

    /// <summary>Finishes the path in progress, dropping the trailing point that followed the pointer when repeated.</summary>
    private void FinishPath()
    {
        if (_active == null) return;
        var pts = _active.Points;
        if (pts.Count > 2 && L is { } l)
        {
            var a = pts[^1];
            var b = pts[^2];
            if (Math.Abs(ChartDrawing.XOf(l, _model.Series, a.Time) - ChartDrawing.XOf(l, _model.Series, b.Time)) < 5 && Math.Abs(l.Main.Y(a.Price) - l.Main.Y(b.Price)) < 5)
                pts.RemoveAt(pts.Count - 1);
        }

        _pendingSecondClick = false;
        if (pts.Count < 2 || pts.All(q => q.Time == pts[0].Time && Math.Abs(q.Price - pts[0].Price) < 1e-12)) _model.Drawings.Remove(_active);
        FinishDrawing();
        _active = null;
        _model.Invalidate();
    }

    // ======================================================================= zoom

    /// <summary>
    /// Zooms in (factor &gt; 1) or out around (x, y): bars widen within the viewport bounds and, with
    /// <see cref="ChartSettings.ZoomBothAxes"/>, the price range shrinks by the applied proportion (like scaling an image).
    /// Without it only time zooms, as before.
    /// </summary>
    public void ZoomAround(ChartLayout l, float x, float y, double factor)
    {
        var vp = _model.Viewport;
        if (!double.IsFinite(factor) || factor <= 0) return;
        double before = l.BarAtExact(x);
        double newWidth = Math.Clamp(vp.BarWidth * factor, ChartViewport.MinBarWidth, ChartViewport.MaxBarWidth);
        if (Math.Abs(newWidth - vp.BarWidth) < 1e-9) return;
        double applied = newWidth / vp.BarWidth;
        bool followingLive = l.ScrollBars <= 0.01;
        vp.BarWidth = newWidth;
        if (!followingLive)
        {
            // keeps the bar under the mouse fixed: x = R - (N-1-b+M-S+0.5)*W  =>  S = N-1-b+M+0.5-(R-x)/W
            vp.ScrollBars = (l.Count - 1 - before + vp.RightMarginBars + 0.5) - (l.Plot.Right - x) / newWidth;
        }

        if (_model.Settings.ZoomBothAxes)
        {
            // the price under the mouse stays put (outside the price panel, the middle of the range)
            double top = l.Main.Top, bottom = l.Main.Bottom;
            double anchor = y >= l.Main.Rect.Top && y <= l.Main.Rect.Bottom ? l.Main.ValueAt(y) : (top + bottom) / 2;
            vp.AutoScale = false;
            vp.PriceTop = anchor + (top - anchor) / applied;
            vp.PriceBottom = anchor - (anchor - bottom) / applied;
        }

        _model.Invalidate();
    }
}
