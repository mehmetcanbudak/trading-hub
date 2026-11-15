using System.Text.Json;
using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

/// <summary>Gesture drawing (hold still, then trace a shape), the new drawing kinds and the gesture's precedence rules.</summary>
public sealed class InkGestureTests
{
    private const float Right = 900;

    private static List<SKPoint> Line(float x0, float y0, float x1, float y1, int n = 40)
    {
        var res = new List<SKPoint>();
        for (int i = 0; i <= n; i++) res.Add(new SKPoint(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n));
        return res;
    }

    private static List<SKPoint> Join(params List<SKPoint>[] parts)
    {
        var res = new List<SKPoint>();
        foreach (var p in parts) res.AddRange(res.Count > 0 ? p.Skip(1) : p);
        return res;
    }

    private static InkShapeKind? Kind(List<SKPoint> pts) => InkRecognizer.Recognize(pts, Right)?.Kind;

    // ------------------------------------------------------------------ recognizer

    [Fact]
    public void A_horizontal_stroke_is_a_horizontal_line_at_its_average_height()
    {
        var s = InkRecognizer.Recognize(Line(100, 200, 400, 203), Right)!;
        Assert.Equal(InkShapeKind.Horizontal, s.Kind);
        Assert.InRange(s.Level!.Value, 200, 203);
    }

    [Fact]
    public void Vertical_diagonal_ray_and_too_short_strokes()
    {
        Assert.Equal(InkShapeKind.Vertical, Kind(Line(300, 100, 302, 400)));
        Assert.Equal(InkShapeKind.TrendLine, Kind(Line(100, 400, 500, 150)));
        Assert.Equal(InkShapeKind.Ray, Kind(Line(100, 400, Right, 150)));
        Assert.Null(InkRecognizer.Recognize(Line(100, 100, 108, 104), Right)); // only held, nothing traced
        Assert.Null(InkRecognizer.Recognize(new[] { new SKPoint(5, 5) }, Right));
    }

    [Fact]
    public void A_loop_is_an_ellipse_and_a_box_a_rectangle()
    {
        var circle = new List<SKPoint>();
        for (int i = 0; i <= 60; i++)
        {
            double a = i / 60.0 * Math.PI * 2;
            circle.Add(new SKPoint(300 + (float)Math.Cos(a) * 120, 250 + (float)Math.Sin(a) * 70));
        }

        var e = InkRecognizer.Recognize(circle, Right)!;
        Assert.Equal(InkShapeKind.Ellipse, e.Kind);
        Assert.InRange(e.A.X, 175, 185);
        Assert.InRange(e.B.Y, 315, 325);

        var box = Join(Line(100, 100, 400, 100), Line(400, 100, 400, 300), Line(400, 300, 100, 300), Line(100, 300, 100, 104));
        Assert.Equal(InkShapeKind.Rectangle, Kind(box));
    }

    [Fact]
    public void A_line_with_a_hook_is_an_arrow_whose_tip_is_the_farthest_point()
    {
        var arrow = Join(Line(100, 400, 500, 150), Line(500, 150, 470, 175, 6));
        var s = InkRecognizer.Recognize(arrow, Right)!;
        Assert.Equal(InkShapeKind.Arrow, s.Kind);
        Assert.InRange(s.B.X, 490, 505);
    }

    [Fact]
    public void A_z_is_a_fibonacci_between_the_first_and_last_strokes()
    {
        var z = Join(Line(100, 100, 400, 100), Line(400, 100, 110, 300), Line(110, 300, 420, 300));
        var s = InkRecognizer.Recognize(z, Right)!;
        Assert.Equal(InkShapeKind.Fib, s.Kind);
        Assert.InRange(s.A.Y, 95, 110);
        Assert.InRange(s.B.Y, 290, 305);

        var back = Join(Line(100, 100, 400, 100), Line(400, 100, 120, 300), Line(120, 300, 380, 300));
        Assert.Equal(InkShapeKind.Fib, Kind(back));
    }

    [Fact]
    public void A_scribble_erases_and_any_other_shape_is_a_path()
    {
        var scribble = new List<SKPoint>();
        for (int i = 0; i < 8; i++) scribble.AddRange(Line(i % 2 == 0 ? 200 : 300, 200 + i * 4, i % 2 == 0 ? 300 : 200, 202 + i * 4, 10));
        Assert.Equal(InkShapeKind.Scribble, Kind(scribble));

        var w = Join(Line(100, 100, 200, 300), Line(200, 300, 300, 120), Line(300, 120, 420, 330));
        Assert.Equal(InkShapeKind.Path, Kind(w));
    }

    [Fact]
    public void Retyping_reuses_the_same_stroke()
    {
        var t0 = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc);
        var g = new InkGeometry
        {
            Start = new ChartPoint(t0, 100),
            End = new ChartPoint(t0.AddHours(2), 110),
            Corners = new[] { new ChartPoint(t0.AddHours(1), 120) },
            Raw = new[] { new ChartPoint(t0, 100), new ChartPoint(t0.AddMinutes(30), 105), new ChartPoint(t0.AddHours(2), 110) },
        };
        Assert.Equal(2, InkDrawings.PointsFor(DrawingKind.Ray, g).Count);
        Assert.Equal(100, Assert.Single(InkDrawings.PointsFor(DrawingKind.HorizontalLine, g)).Price);
        Assert.Equal(3, InkDrawings.PointsFor(DrawingKind.Path, g).Count);
        Assert.Equal(3, InkDrawings.PointsFor(DrawingKind.Brush, g).Count);
        Assert.Contains(DrawingKind.Ray, InkDrawings.Alternatives(DrawingKind.TrendLine));
        Assert.Contains(DrawingKind.Ellipse, InkDrawings.Alternatives(DrawingKind.Rectangle));
        Assert.Null(InkDrawings.KindOf(InkShapeKind.Scribble));
    }

    [Fact]
    public void New_kinds_take_explicit_numbers_and_saved_drawings_stay_readable()
    {
        // drawings persist the kind as a number: existing values never move; 14 is the parallel channel
        Assert.Equal(12, (int)DrawingKind.AnchoredVwap);
        Assert.Equal(13, (int)DrawingKind.FixedRangeProfile);
        Assert.Equal(14, (int)DrawingKind.ParallelChannel);
        Assert.Equal(15, (int)DrawingKind.Ellipse);
        Assert.Equal(16, (int)DrawingKind.Arrow);
        Assert.Equal(17, (int)DrawingKind.Path);
        Assert.Equal(18, (int)DrawingKind.Brush);
        Assert.Equal(19, (int)DrawingKind.Highlighter);
        Assert.Equal(20, (int)DrawingKind.CrossLine);

        var brush = new ChartDrawing { Kind = DrawingKind.Brush, Points = { new ChartPoint(SearchZoomFixture.Start, 1), new ChartPoint(SearchZoomFixture.Start.AddMinutes(1), 2) } };
        var back = JsonSerializer.Deserialize<ChartDrawing>(JsonSerializer.Serialize(brush))!;
        Assert.Equal(DrawingKind.Brush, back.Kind);
        Assert.Equal(2, back.Points.Count);
        Assert.DoesNotContain("IsFreehand", JsonSerializer.Serialize(brush));
    }

    // ------------------------------------------------------------------ on a real chart

    private sealed class Rig : IDisposable
    {
        public required ChartModel Model { get; init; }
        public required ChartRenderState State { get; init; }
        public required ChartController Controller { get; init; }
        public required ChartLayout Layout { get; set; }
        public long Now;

        public void Dispose() => Model.Dispose();

        public void Render()
        {
            using var surface = SKSurface.Create(new SKImageInfo(1000, 600));
            Layout = new ChartRenderer().Render(surface.Canvas, Model, 1000, 600, State);
        }

        /// <summary>Presses and holds still until the gesture starts (clock advanced, never slept).</summary>
        public void Hold(float x, float y)
        {
            Controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.None);
            Assert.True(Controller.InkPending);
            Now += InkRecognizer.RingStartMs + 10;
            Assert.True(Controller.InkTick());
            Assert.NotNull(State.InkHold);
            Now += InkRecognizer.HoldMs;
            Assert.True(Controller.InkTick());
            Assert.True(Controller.IsInking);
        }

        public void Trace(IEnumerable<SKPoint> points)
        {
            foreach (var p in points) Controller.MouseMove(p.X, p.Y, InputModifiers.None);
        }
    }

    private static Rig Chart(bool ink = true)
    {
        var model = SearchZoomFixture.Model(300);
        model.Settings.Style = ChartStyle.Candles;
        model.Viewport.BarWidth = 8;
        var state = new ChartRenderState();
        var rig = new Rig { Model = model, State = state, Controller = new ChartController(model, state) { InkGestures = ink }, Layout = null! };
        rig.Controller.Clock = () => rig.Now;
        rig.Render();
        return rig;
    }

    [Fact]
    public void Holding_still_then_tracing_leaves_the_drawing_and_reports_it()
    {
        using var r = Chart();
        var c = r.Controller;
        InkResult? result = null;
        c.InkFinished += x => result = x;
        float y = r.Layout.Main.Rect.MidY;
        r.Hold(200, y);
        r.Trace(Line(200, y, 500, y + 2).Skip(1));
        Assert.NotNull(r.State.InkStroke);
        c.MouseUp(500, y + 2, PointerButton.Left, InputModifiers.None);

        var d = Assert.Single(r.Model.Drawings);
        Assert.Equal(DrawingKind.HorizontalLine, d.Kind);
        Assert.True(d.Selected);
        Assert.Null(r.State.InkStroke);
        Assert.Same(d, result!.Drawing);
        Assert.Contains(DrawingKind.HorizontalRay, result.Alternatives);
        Assert.False(c.InkPending);

        c.RetypeInk(d, result.Geometry!, DrawingKind.TrendLine);
        Assert.Equal(DrawingKind.TrendLine, d.Kind);
        Assert.Equal(2, d.Points.Count);
    }

    [Fact]
    public void Releasing_before_the_hold_completes_never_draws()
    {
        using var r = Chart();
        float y = r.Layout.Main.Rect.MidY;
        r.Controller.MouseDown(300, y, PointerButton.Left, 1, InputModifiers.None);
        r.Now += InkRecognizer.HoldMs - 1;
        r.Controller.InkTick();
        Assert.False(r.Controller.IsInking);
        r.Controller.MouseUp(300, y, PointerButton.Left, InputModifiers.None);
        r.Now += InkRecognizer.HoldMs;
        Assert.False(r.Controller.InkTick());
        Assert.Empty(r.Model.Drawings);
        Assert.Null(r.State.InkHold);
    }

    [Fact]
    public void Moving_before_the_hold_is_a_chart_drag_not_a_gesture()
    {
        using var r = Chart();
        float y = r.Layout.Main.Rect.MidY;
        double scroll = r.Model.Viewport.ScrollBars;
        r.Controller.MouseDown(200, y, PointerButton.Left, 1, InputModifiers.None);
        r.Controller.MouseMove(260, y, InputModifiers.None);
        r.Now += InkRecognizer.HoldMs + 60;
        r.Controller.InkTick();
        Assert.False(r.Controller.InkPending);
        r.Controller.MouseUp(260, y, PointerButton.Left, InputModifiers.None);
        Assert.Empty(r.Model.Drawings);
        Assert.NotEqual(scroll, r.Model.Viewport.ScrollBars);
    }

    [Fact]
    public void A_scribble_over_a_drawing_erases_it_and_undo_restores_it()
    {
        using var r = Chart();
        var (m, c, l) = (r.Model, r.Controller, r.Layout);
        float y = l.Main.Rect.MidY;
        var line = new ChartDrawing { Kind = DrawingKind.HorizontalLine };
        line.Points.Add(new ChartPoint(m.Series[^10].Time, l.Main.ValueAt(y)));
        var locked = new ChartDrawing { Kind = DrawingKind.HorizontalLine, Locked = true };
        locked.Points.Add(new ChartPoint(m.Series[^10].Time, l.Main.ValueAt(y + 10)));
        m.Drawings.Add(line);
        m.Drawings.Add(locked);
        InkResult? result = null;
        c.InkFinished += x => result = x;
        r.Hold(300, y - 20);
        for (int i = 0; i < 8; i++)
            r.Trace(Line(i % 2 == 0 ? 300 : 420, y - 20 + i * 5, i % 2 == 0 ? 420 : 300, y - 18 + i * 5, 10));
        Assert.Contains(line, r.State.InkVictims!);
        c.MouseUp(300, y + 20, PointerButton.Left, InputModifiers.None);

        Assert.Same(locked, Assert.Single(m.Drawings)); // locked drawings are never erased
        Assert.True(result!.Erase);
        Assert.Single(result.Removed);
        c.UndoErase(result.Removed);
        Assert.Equal(new[] { line, locked }, m.Drawings);
    }

    [Fact]
    public void Escape_cancels_the_stroke_in_progress()
    {
        using var r = Chart();
        float y = r.Layout.Main.Rect.MidY;
        r.Hold(200, y);
        r.Trace(Line(200, y, 500, y).Skip(1));
        Assert.True(r.Controller.KeyDown("Escape", InputModifiers.None));
        Assert.Null(r.State.InkStroke);
        r.Controller.MouseUp(500, y, PointerButton.Left, InputModifiers.None);
        Assert.Empty(r.Model.Drawings);
    }

    [Fact]
    public void Brush_path_and_shift_drag_measuring()
    {
        using var r = Chart();
        var (m, c, l) = (r.Model, r.Controller, r.Layout);
        float y = l.Main.Rect.MidY;

        c.ActiveTool = DrawingKind.Brush;
        c.MouseDown(100, y, PointerButton.Left, 1, InputModifiers.None);
        for (int i = 1; i <= 30; i++) c.MouseMove(100 + i * 6, y + (float)Math.Sin(i / 3.0) * 30, InputModifiers.None);
        c.MouseUp(280, y, PointerButton.Left, InputModifiers.None);
        var brush = Assert.Single(m.Drawings);
        Assert.Equal(DrawingKind.Brush, brush.Kind);
        Assert.True(brush.Points.Count > 5);
        Assert.True(brush.HitTest(l, m.Series, null, 100, y, out int handle));
        Assert.Equal(-1, handle); // freehand moves as a whole
        Assert.Empty(brush.HandlePositions(l, m.Series, null));

        c.ActiveTool = DrawingKind.Path;
        c.MouseDown(400, y, PointerButton.Left, 1, InputModifiers.None);
        c.MouseUp(400, y, PointerButton.Left, InputModifiers.None);
        Assert.True(c.TryCompletePending(450, y - 40, InputModifiers.None, 1));
        Assert.True(c.TryCompletePending(500, y + 30, InputModifiers.None, 1));
        Assert.True(c.TryCompletePending(500, y + 30, InputModifiers.None, 2));
        var path = m.Drawings[^1];
        Assert.Equal(DrawingKind.Path, path.Kind);
        Assert.Equal(3, path.Points.Count);
        Assert.False(c.HasPending);

        int before = m.Drawings.Count;
        c.MouseDown(600, y, PointerButton.Left, 1, InputModifiers.Shift);
        c.MouseMove(700, y - 50, InputModifiers.Shift);
        Assert.NotNull(r.State.Measure);
        c.MouseUp(700, y - 50, PointerButton.Left, InputModifiers.Shift);
        Assert.Equal(before, m.Drawings.Count);
        c.MouseDown(650, y + 80, PointerButton.Left, 1, InputModifiers.None);
        Assert.Null(r.State.Measure);
        c.MouseUp(650, y + 80, PointerButton.Left, InputModifiers.None);
    }

    [Fact]
    public void Ellipse_and_cross_line_hit_inside_and_on_both_strokes()
    {
        using var r = Chart();
        var (m, l) = (r.Model, r.Layout);
        float y = l.Main.Rect.MidY;
        var e = new ChartDrawing { Kind = DrawingKind.Ellipse };
        e.Points.Add(new ChartPoint(ChartDrawing.TimeAt(l, m.Series, 200), l.Main.ValueAt(y - 50)));
        e.Points.Add(new ChartPoint(ChartDrawing.TimeAt(l, m.Series, 400), l.Main.ValueAt(y + 50)));
        Assert.True(e.HitTest(l, m.Series, null, 300, y, out _));
        Assert.False(e.HitTest(l, m.Series, null, 210, y - 40, out _)); // the bounding box corner is outside

        var cross = new ChartDrawing { Kind = DrawingKind.CrossLine };
        cross.Points.Add(new ChartPoint(ChartDrawing.TimeAt(l, m.Series, 500), l.Main.ValueAt(y)));
        Assert.Equal(1, cross.RequiredPoints);
        Assert.True(cross.HitTest(l, m.Series, null, 100, y, out _));
        Assert.True(cross.HitTest(l, m.Series, null, ChartDrawing.XOf(l, m.Series, cross.Points[0].Time), y - 100, out _));
    }

    [Fact]
    public void Weak_magnet_snaps_only_near_a_bar_price_and_strong_always()
    {
        using var r = Chart();
        var (m, c, l) = (r.Model, r.Controller, r.Layout);
        var bar = m.Series[^5];
        float x = l.CenterX(m.Series.Count - 5);
        double high = (double)m.Series.TicksToPrice(bar.HighTicks);
        float near = l.Main.Y(high) - 4, far = l.Main.Rect.Top + 4;

        c.Magnet = MagnetMode.Weak;
        c.ActiveTool = DrawingKind.HorizontalLine;
        c.MouseDown(x, near, PointerButton.Left, 1, InputModifiers.None);
        Assert.Equal(high, m.Drawings[^1].Points[0].Price);
        c.ActiveTool = DrawingKind.HorizontalLine;
        c.MouseDown(x, far, PointerButton.Left, 1, InputModifiers.None);
        Assert.NotEqual(high, m.Drawings[^1].Points[0].Price);

        c.Magnet = MagnetMode.Strong;
        c.ActiveTool = DrawingKind.HorizontalLine;
        c.MouseDown(x, far, PointerButton.Left, 1, InputModifiers.None);
        double[] ohlc = new[] { bar.HighTicks, bar.LowTicks, bar.OpenTicks, bar.CloseTicks }.Select(t => (double)m.Series.TicksToPrice(t)).ToArray();
        Assert.Contains(m.Drawings[^1].Points[0].Price, ohlc);
    }

    [Fact]
    public void Hidden_drawings_are_not_hit_and_locked_ones_do_not_move()
    {
        using var r = Chart();
        var (m, c, l) = (r.Model, r.Controller, r.Layout);
        float y = l.Main.Rect.MidY;
        var line = new ChartDrawing { Kind = DrawingKind.HorizontalLine };
        line.Points.Add(new ChartPoint(m.Series[^10].Time, l.Main.ValueAt(y)));
        m.Drawings.Add(line);

        m.Settings.HideDrawings = true;
        c.MouseDown(300, y, PointerButton.Left, 1, InputModifiers.None);
        Assert.False(line.Selected);
        c.MouseUp(300, y, PointerButton.Left, InputModifiers.None);

        m.Settings.HideDrawings = false;
        m.Settings.LockDrawings = true;
        double price = line.Points[0].Price;
        c.MouseDown(300, y, PointerButton.Left, 1, InputModifiers.None);
        Assert.True(line.Selected);
        c.MouseMove(300, y + 60, InputModifiers.None);
        c.MouseUp(300, y + 60, PointerButton.Left, InputModifiers.None);
        Assert.Equal(price, line.Points[0].Price);
    }

    // ------------------------------------------------------------------ precedence: trading always wins

    [Fact]
    public void An_order_line_under_the_pointer_is_dragged_and_never_starts_a_gesture()
    {
        using var r = Chart();
        using var broker = new SimBroker(r.Model.Data, new Portfolio(), new TradingStatistics());
        r.Model.Account = broker;
        decimal price = r.Model.Instrument.RoundPrice((decimal)r.Layout.Main.ValueAt(r.Layout.Main.Rect.MidY));
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, price);
        Assert.Equal(OrderStates.Active, order.State);
        (Order Order, decimal Price)? dragged = null;
        r.Controller.OrderDragged += (o, p) => dragged = (o, p);
        InkResult? ink = null;
        r.Controller.InkFinished += x => ink = x;

        float y = r.Layout.Main.Y(price);
        r.Controller.MouseDown(300, y, PointerButton.Left, 1, InputModifiers.None);
        Assert.Same(order, r.State.DragOrder);
        Assert.False(r.Controller.InkPending);
        r.Now += InkRecognizer.HoldMs * 3;
        Assert.False(r.Controller.InkTick());
        Assert.Null(r.State.InkHold);
        r.Controller.MouseMove(300, y + 40, InputModifiers.None);
        r.Controller.MouseUp(300, y + 40, PointerButton.Left, InputModifiers.None);

        Assert.Null(ink);
        Assert.Empty(r.Model.Drawings);
        Assert.Same(order, dragged!.Value.Order); // the view still decides whether it confirms the move
    }

    [Fact]
    public void A_stop_target_handle_wins_over_a_hold_and_a_click_creates_nothing()
    {
        using var r = Chart();
        float x = r.Layout.Main.Rect.MidX, y = r.Layout.Main.Rect.MidY;
        r.State.Handles.Add(new BracketHandle(new SKRect(x - 20, y - 20, x + 20, y + 20), new BracketDrag(null, BracketRole.StopLoss), true));
        int dropped = 0;
        r.Controller.BracketDropped += (_, _) => dropped++;
        r.Controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.None);
        Assert.NotNull(r.State.BracketDrag);
        Assert.False(r.Controller.InkPending);
        r.Now += InkRecognizer.HoldMs * 3;
        Assert.False(r.Controller.InkTick());
        r.Controller.MouseUp(x, y, PointerButton.Left, InputModifiers.None);
        Assert.Equal(0, dropped);
        Assert.Empty(r.Model.Drawings);
    }

    [Fact]
    public void Holding_on_a_drawing_moves_it_instead_of_starting_a_gesture()
    {
        using var r = Chart();
        float y = r.Layout.Main.Rect.MidY;
        var line = new ChartDrawing { Kind = DrawingKind.HorizontalLine };
        line.Points.Add(new ChartPoint(r.Model.Series[^10].Time, r.Layout.Main.ValueAt(y)));
        r.Model.Drawings.Add(line);
        r.Controller.MouseDown(300, y, PointerButton.Left, 1, InputModifiers.None);
        Assert.False(r.Controller.InkPending);
        Assert.True(r.Controller.IsDragging);
        r.Controller.CancelInteraction();
    }

    [Fact]
    public void Cancelling_during_the_hold_disarms_the_gesture()
    {
        using var r = Chart();
        float y = r.Layout.Main.Rect.MidY;
        r.Controller.MouseDown(300, y, PointerButton.Left, 1, InputModifiers.None);
        r.Now += InkRecognizer.RingStartMs + 50;
        Assert.True(r.Controller.InkTick());
        // a context menu, a confirmation dialog or lost focus cancels the interaction
        r.Controller.CancelInteraction();
        Assert.False(r.Controller.InkPending);
        Assert.Null(r.State.InkHold);
        r.Now += InkRecognizer.HoldMs;
        Assert.False(r.Controller.InkTick());
        r.Controller.MouseMove(500, y, InputModifiers.None);
        r.Controller.MouseUp(500, y, PointerButton.Left, InputModifiers.None);
        Assert.Empty(r.Model.Drawings);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Gestures_never_arm_when_off_locked_or_with_a_tool(bool ink, bool lockAll, bool tool)
    {
        using var r = Chart(ink);
        r.Model.Settings.LockDrawings = lockAll;
        if (tool) r.Controller.ActiveTool = DrawingKind.Rectangle;
        float y = r.Layout.Main.Rect.MidY;
        r.Controller.MouseDown(300, y, PointerButton.Left, 1, InputModifiers.None);
        Assert.False(r.Controller.InkPending);
        r.Controller.CancelInteraction();
    }

    [Fact]
    public void Double_clicks_and_other_buttons_never_arm_a_gesture()
    {
        using var r = Chart();
        float y = r.Layout.Main.Rect.MidY;
        r.Controller.MouseDown(300, y, PointerButton.Left, 2, InputModifiers.None);
        Assert.False(r.Controller.InkPending);
        r.Controller.MouseDown(300, y, PointerButton.Middle, 1, InputModifiers.None);
        Assert.False(r.Controller.InkPending);
        r.Controller.MouseDown(300, y, PointerButton.Right, 1, InputModifiers.None);
        Assert.False(r.Controller.InkPending);
    }
}
