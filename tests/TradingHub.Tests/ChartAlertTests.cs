using System.Drawing;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Drawing;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Tests;

/// <summary>A tape of one-minute prints the tests extend bar by bar (forming-bar updates included).</summary>
internal sealed class AlertTape : IDisposable
{
    public static readonly DateTime T0 = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
    private long _id;

    public AlertTape(decimal tick = 0.1m)
    {
        Data = new InstrumentData(new Instrument { Symbol = "ALERTS", FeedId = "sim", TickSize = tick, VolumeStep = 1 }, new SimulatedFeed());
        Model = new ChartModel(Data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
    }

    public InstrumentData Data { get; }
    public ChartModel Model { get; }

    /// <summary>One print in minute <paramref name="minute"/>, <paramref name="second"/> seconds in; updates the chart.</summary>
    public AlertTape At(int minute, decimal price, int second = 0)
    {
        Data.Trades.Add(new Trade(++_id, T0.AddMinutes(minute).AddSeconds(second), price, 1, TradeSide.Buy));
        Model.Update();
        return this;
    }

    public List<ChartAlertFire> Evaluate() => ChartAlertEngine.Evaluate(Model, T0.AddDays(1));

    public void Dispose()
    {
        Model.Dispose();
        Data.Dispose();
    }
}

public sealed class ChartAlertConditionTests
{
    /// <summary>History alternating 100 / 110 every minute (bars 0..29): plenty of crossings an alert must never replay.</summary>
    private static AlertTape History()
    {
        var tape = new AlertTape();
        for (int m = 0; m < 30; m++) tape.At(m, m % 2 == 0 ? 110 : 100);
        return tape;
    }

    [Fact]
    public void A_parallel_channel_gives_its_three_lines_and_its_handles_keep_or_set_the_width()
    {
        using var tape = History();
        var s = tape.Model.Series;
        var d = new ChartDrawing { Kind = DrawingKind.ParallelChannel, ExtendRight = true };
        d.Points.Add(new ChartPoint(s[10].Time, 100));
        d.Points.Add(new ChartPoint(s[20].Time, 110));
        d.InitChannel(5);
        Assert.Equal(105, d.ValueAt(s, 10, "upper"));
        Assert.Equal(100, d.ValueAt(s, 10, "lower"));
        Assert.Equal(107.5, d.ValueAt(s, 15, "middle"));
        Assert.Equal(120, d.ValueAt(s, 30, "lower")); // extended right
        Assert.Null(d.ValueAt(s, 5, "lower")); // not extended left
        Assert.Equal(new[] { "upper", "middle", "lower" }, d.AlertParts().Select(p => p.Part));

        d.MoveHandle(0, new ChartPoint(s[10].Time, 90));
        Assert.Equal(5, d.ChannelOffset, 6);
        d.MoveHandle(2, new ChartPoint(s[15].Time, 120)); // the base half-way is at 100
        Assert.Equal(20, d.ChannelOffset, 6);

        // persisted as kind 14 and read back with its width
        var json = System.Text.Json.JsonSerializer.Serialize(d);
        Assert.Contains("\"Kind\":14", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<ChartDrawing>(json)!;
        Assert.Equal(DrawingKind.ParallelChannel, back.Kind);
        Assert.Equal(20, back.ChannelOffset, 6);
    }

    [Fact]
    public void Price_enters_and_exits_a_drawn_channel_touches_a_line_and_moves_by_a_percent()
    {
        using var tape = History();
        var m = tape.Model;
        var s = m.Series;
        var rect = new ChartDrawing { Kind = DrawingKind.Rectangle, ExtendLeft = true, ExtendRight = true };
        rect.Points.Add(new ChartPoint(s[0].Time, 106));
        rect.Points.Add(new ChartPoint(s[^1].Time, 104));
        m.Drawings.Add(rect);
        var lines = ChartAlertEngine.DrawingLines(m, rect);
        Assert.Equal(new[] { "top", "bottom" }, lines.Select(l => l.Field));

        // a channel inside the swings: 100 → 105 enters, 105 → 110 exits
        var tight = new ChartDrawing { Kind = DrawingKind.Rectangle, ExtendLeft = true, ExtendRight = true };
        tight.Points.Add(new ChartPoint(s[0].Time, 101));
        tight.Points.Add(new ChartPoint(s[^1].Time, 99));
        m.Drawings.Add(tight);
        var edges = ChartAlertEngine.DrawingLines(m, tight);
        var enter = new ChartAlert { Condition = ChartAlertCondition.EnteringChannel, Right = edges[0], Lower = edges[1] };
        var exit = new ChartAlert { Condition = ChartAlertCondition.ExitingChannel, Right = edges[0], Lower = edges[1] };
        Assert.True(ChartAlertEngine.Check(m, enter, 1)); // 110 → 100
        Assert.False(ChartAlertEngine.Check(m, exit, 1));
        Assert.True(ChartAlertEngine.Check(m, exit, 2)); // 100 → 110
        Assert.False(ChartAlertEngine.Check(m, enter, 2));

        // touching with the bar range: a line at a bar's high
        var h = new ChartDrawing { Kind = DrawingKind.HorizontalLine };
        h.Points.Add(new ChartPoint(s[4].Time, 110));
        m.Drawings.Add(h);
        var touch = new ChartAlert { Condition = ChartAlertCondition.Touching, Right = ChartAlertEngine.DrawingLines(m, h)[0] };
        Assert.True(ChartAlertEngine.Check(m, touch, 4));
        Assert.False(ChartAlertEngine.Check(m, touch, 5));
        Assert.True(touch.Uses(h.Id));

        // moving up 10 % in one bar: 100 → 110
        var up = new ChartAlert { Condition = ChartAlertCondition.MovingUpPercent, Right = ChartAlertOperand.Fixed(10), Bars = 1 };
        Assert.True(ChartAlertEngine.Check(m, up, 2));
        Assert.False(ChartAlertEngine.Check(m, up, 1));
        var down = new ChartAlert { Condition = ChartAlertCondition.MovingDownPercent, Right = ChartAlertOperand.Fixed(9), Bars = 1 };
        Assert.True(ChartAlertEngine.Check(m, down, 1)); // 110 → 100 is −9.09 %
        Assert.False(ChartAlertEngine.Check(m, new ChartAlert { Condition = ChartAlertCondition.MovingDownPercent, Right = ChartAlertOperand.Fixed(10), Bars = 1 }, 1));

        // crossing directions against a fixed value
        var crossUp = new ChartAlert { Condition = ChartAlertCondition.CrossingUp, Right = ChartAlertOperand.Fixed(105) };
        var crossDown = new ChartAlert { Condition = ChartAlertCondition.CrossingDown, Right = ChartAlertOperand.Fixed(105) };
        var cross = new ChartAlert { Condition = ChartAlertCondition.Crossing, Right = ChartAlertOperand.Fixed(105) };
        Assert.True(ChartAlertEngine.Check(m, crossUp, 2));
        Assert.False(ChartAlertEngine.Check(m, crossDown, 2));
        Assert.True(ChartAlertEngine.Check(m, crossDown, 1));
        Assert.True(ChartAlertEngine.Check(m, cross, 1) & ChartAlertEngine.Check(m, cross, 2));
    }

    [Fact]
    public void History_never_fires_and_a_forming_bar_fires_once_per_bar()
    {
        using var tape = History();
        var alert = new ChartAlert { Condition = ChartAlertCondition.CrossingUp, Right = ChartAlertOperand.Fixed(105), Trigger = ChartAlertTrigger.OncePerBar };
        tape.Model.Alerts.Add(alert);
        // bar 29 is forming at 100 after 28 closed at 110: no cross up now, and the many past crossings never fire
        Assert.Empty(tape.Evaluate());
        Assert.Empty(tape.Evaluate());

        tape.At(30, 100); // 29 closed at 100; 30 forming at 100
        Assert.Empty(tape.Evaluate());
        tape.At(30, 110, 10); // crosses up within the forming bar
        var fired = tape.Evaluate();
        Assert.Single(fired);
        Assert.Equal($"chart:{alert.Id}:{tape.Model.Series[^1].Time.Ticks}", fired[0].EventId);

        // the forming bar is replaced repeatedly: down, up again, … never a second fire in the same bar
        tape.At(30, 100, 20);
        Assert.Empty(tape.Evaluate());
        tape.At(30, 110, 30);
        Assert.Empty(tape.Evaluate());

        // the next bars: 31 at 100 (no cross up), 32 at 110 crosses again
        tape.At(31, 100);
        Assert.Empty(tape.Evaluate());
        tape.At(32, 110);
        Assert.Single(tape.Evaluate());
        Assert.True(alert.Active);
    }

    [Fact]
    public void A_condition_already_met_when_the_alert_arms_does_not_fire_until_it_changes()
    {
        using var tape = History();
        tape.At(30, 100).At(30, 110, 10); // bar 30 already crossed up before the alert exists
        var alert = new ChartAlert { Condition = ChartAlertCondition.CrossingUp, Right = ChartAlertOperand.Fixed(105) };
        tape.Model.Alerts.Add(alert);
        Assert.Empty(tape.Evaluate());
        tape.At(30, 111, 20);
        Assert.Empty(tape.Evaluate()); // still the same crossing that happened before arming
        tape.At(30, 100, 30);
        Assert.Empty(tape.Evaluate());
        tape.At(30, 110, 40);
        Assert.Single(tape.Evaluate()); // a new crossing observed after arming
    }

    [Fact]
    public void Once_switches_off_and_once_per_bar_close_waits_for_the_close()
    {
        using var tape = History();
        var once = new ChartAlert { Condition = ChartAlertCondition.Crossing, Right = ChartAlertOperand.Fixed(105), Trigger = ChartAlertTrigger.Once };
        var onClose = new ChartAlert { Condition = ChartAlertCondition.CrossingUp, Right = ChartAlertOperand.Fixed(105), Trigger = ChartAlertTrigger.OncePerBarClose };
        tape.Model.Alerts.Add(once);
        tape.Model.Alerts.Add(onClose);
        // bar 29 forming at 100 after 28 at 110: "crossing" holds already when arming, so it only records the state;
        // the close-only alert ignores bar 28, which closed before it existed
        Assert.Empty(tape.Evaluate());

        tape.At(30, 100); // 29 closes at 100 after 28 at 110: no cross up on that close
        Assert.Empty(tape.Evaluate());
        tape.At(30, 110, 10); // crosses inside the forming bar 30
        var fired = tape.Evaluate();
        Assert.Same(once, Assert.Single(fired).Alert);
        Assert.False(once.Active);

        tape.At(31, 100); // 30 closes at 110 after 29 at 100: cross up on the close (once is off)
        var closeFire = Assert.Single(tape.Evaluate());
        Assert.Same(onClose, closeFire.Alert);
        Assert.Equal(tape.Model.Series[^2].Time, closeFire.BarTime);
        tape.At(31, 120, 30);
        Assert.Empty(tape.Evaluate()); // each closed bar is checked once; 31 still forming
        tape.At(32, 100); // 31 closed at 120 after 30 at 110: no cross up
        Assert.Empty(tape.Evaluate());
        tape.At(32, 101, 10).At(33, 100); // 32 closes at 101: no cross
        Assert.Empty(tape.Evaluate());
        Assert.False(once.Active);
    }

    [Fact]
    public void Nothing_is_evaluated_while_trades_are_still_being_aggregated()
    {
        using var tape = History();
        var alert = new ChartAlert { Condition = ChartAlertCondition.Crossing, Right = ChartAlertOperand.Fixed(105) };
        tape.Model.Alerts.Add(alert);
        tape.Data.Trades.Add(new Trade(9999, AlertTape.T0.AddMinutes(40), 120, 1, TradeSide.Buy)); // not aggregated yet
        Assert.True(tape.Model.IsAggregating);
        Assert.Empty(tape.Evaluate());
        Assert.Null(alert.LastFiredUtc);
    }

    [Fact]
    public void Removing_a_drawing_prunes_its_alerts_and_moving_it_moves_the_alert()
    {
        using var tape = History();
        var m = tape.Model;
        var line = new ChartDrawing { Kind = DrawingKind.HorizontalLine };
        line.Points.Add(new ChartPoint(m.Series[0].Time, 105));
        m.Drawings.Add(line);
        var alert = new ChartAlert { Condition = ChartAlertCondition.Crossing, Right = ChartAlertEngine.DrawingLines(m, line)[0] };
        m.Alerts.Add(alert);
        Assert.True(ChartAlertEngine.TryValue(m, alert.Right, 5, out var v) && v == 105);
        line.MoveHandle(0, new ChartPoint(m.Series[0].Time, 107));
        Assert.True(ChartAlertEngine.TryValue(m, alert.Right, 5, out v) && v == 107);
        Assert.Empty(ChartAlertEngine.Prune(m));

        // a Fibonacci level that is hidden takes its alert with it
        var fib = new ChartDrawing { Kind = DrawingKind.FibRetracement, Levels = new() { new FibLevel { Value = 0.5 }, new FibLevel { Value = 1 } } };
        fib.Points.Add(new ChartPoint(m.Series[0].Time, 110));
        fib.Points.Add(new ChartPoint(m.Series[5].Time, 100));
        m.Drawings.Add(fib);
        var half = ChartAlertEngine.DrawingLines(m, fib).Single(o => o.Field == "fib:0.5");
        Assert.True(ChartAlertEngine.TryValue(m, half, 10, out var fv) && fv == 105);
        var fibAlert = new ChartAlert { Condition = ChartAlertCondition.Touching, Right = half };
        m.Alerts.Add(fibAlert);
        fib.Levels[0].Visible = false;
        Assert.Equal(new[] { fibAlert }, ChartAlertEngine.Prune(m));

        m.Drawings.Remove(line);
        Assert.Equal(new[] { alert }, ChartAlertEngine.Prune(m));
        Assert.Empty(m.Alerts);
    }

    [Fact]
    public void Removing_an_indicator_removes_its_alerts_and_keeps_later_ones_of_the_same_type()
    {
        using var tape = History();
        var m = tape.Model;
        var a = m.AddIndicator(new LevelsIndicator { Price = 104 });
        var b = m.AddIndicator(new LevelsIndicator { Price = 106 });
        m.Update();
        var ops = ChartAlertEngine.Lines(m).Where(o => o.Source == ChartAlertSource.Level).ToList();
        Assert.Equal(2, ops.Count);
        var onA = new ChartAlert { Condition = ChartAlertCondition.Crossing, Right = ops[0] };
        var onB = new ChartAlert { Condition = ChartAlertCondition.Crossing, Right = ops[1] };
        m.Alerts.AddRange(new[] { onA, onB });
        Assert.True(ChartAlertEngine.TryValue(m, onB.Right, 29, out var before) && before == 106);
        Assert.False(ChartAlertEngine.TryValue(m, onB.Right, 3, out _)); // before the level starts

        m.RemoveIndicator(a);
        Assert.Equal(new[] { onB }, m.Alerts);
        Assert.True(ChartAlertEngine.TryValue(m, onB.Right, 29, out var after) && after == 106);
        Assert.Same(b, ChartAlertEngine.SlotOf(m, onB.Right.Indicator!));
    }

    [Fact]
    public void Alerts_persist_additively_in_the_workspace_and_malformed_entries_are_dropped()
    {
        var line = new DrawnLine { Times = new[] { AlertTape.T0, AlertTape.T0.AddMinutes(5) }, Prices = new[] { 100.5m, 101m }, Color = 0xFF00FF00, Right = true, Text = "level" };
        var alerts = new List<ChartAlert>
        {
            new() { Condition = ChartAlertCondition.EnteringChannel, Right = new ChartAlertOperand { Source = ChartAlertSource.Drawing, Drawing = Guid.NewGuid().ToString("N"), Field = "upper", Label = "Parallel channel · upper edge" },
                Lower = new ChartAlertOperand { Source = ChartAlertSource.Drawing, Drawing = Guid.NewGuid().ToString("N"), Field = "lower", Label = "x" }, Trigger = ChartAlertTrigger.OncePerBarClose, Message = "note" },
            new() { Condition = ChartAlertCondition.Crossing, Right = new ChartAlertOperand { Source = ChartAlertSource.DrawnLine, Indicator = "T#0", Field = "drawn:k", Line = line, Label = "T · level" } },
            new() { Condition = ChartAlertCondition.MovingDownPercent, Right = ChartAlertOperand.Fixed(2.5m), Bars = 3, Active = false },
        };
        string json = Workspace.ChartAlertsJson(alerts);
        Assert.Contains("\"OncePerBarClose\"", json); // enums by name
        var back = Workspace.ChartAlertsFromJson(json);
        Assert.Equal(alerts.Select(a => a.Id), back.Select(a => a.Id));
        Assert.Equal(ChartAlertTrigger.OncePerBarClose, back[0].Trigger);
        Assert.Equal("note", back[0].Message);
        Assert.Equal(line.Prices, back[1].Right.Line!.Prices);
        Assert.Equal(line.Times, back[1].Right.Line!.Times);
        Assert.True(back[1].Right.Line!.Right);
        Assert.False(back[2].Active);
        Assert.Equal(3, back[2].Bars);

        // a tab saved before chart alerts existed, and malformed entries
        Assert.Empty(Workspace.ChartAlertsFromJson("{\"FeedId\":\"sim\",\"Symbol\":\"X\",\"Period\":\"M5\"}"));
        var bad = Workspace.ChartAlertsFromJson("{\"ChartAlerts\":[" +
            "{\"Id\":\"aa\",\"Condition\":\"EnteringChannel\",\"Right\":{\"Source\":\"Value\",\"Value\":1}}," + // channel without its lower edge
            "{\"Id\":\"bb\",\"Condition\":\"Crossing\",\"Right\":{\"Source\":\"Price\",\"Field\":\"volume\"}}," + // unknown price field
            "{\"Id\":\"cc\",\"Condition\":\"Crossing\",\"Right\":{\"Source\":\"Value\",\"Value\":1}}," +
            "{\"Id\":\"cc\",\"Condition\":\"Crossing\",\"Right\":{\"Source\":\"Value\",\"Value\":2}}]}"); // duplicate id renamed
        Assert.Equal(2, bad.Count);
        Assert.Equal("cc", bad[0].Id);
        Assert.NotEqual("cc", bad[1].Id);
    }

    [Fact]
    public void Two_clicks_draw_a_parallel_channel_with_a_width_and_three_handles()
    {
        using var tape = History();
        var m = tape.Model;
        var state = new ChartRenderState();
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(900, 500));
        new ChartRenderer().Render(surface.Canvas, m, 900, 500, state);
        var controller = new ChartController(m, state) { ActiveTool = DrawingKind.ParallelChannel };
        controller.MouseDown(300, 300, PointerButton.Left, 1, InputModifiers.None);
        controller.MouseUp(300, 300, PointerButton.Left, InputModifiers.None);
        Assert.True(controller.TryCompletePending(500, 200, InputModifiers.None));
        var channel = Assert.Single(m.Drawings);
        Assert.Equal(DrawingKind.ParallelChannel, channel.Kind);
        Assert.Equal(3, channel.Points.Count);
        Assert.True(channel.ChannelOffset > 0);
        new ChartRenderer().Render(surface.Canvas, m, 900, 500, state);
        var layout = m.LastLayout!;
        Assert.Equal(3, channel.HandlePositions(layout, m.Series, null).Count());
        double inside = (channel.Points[0].Price + channel.Points[1].Price) / 2 + channel.ChannelOffset / 2;
        Assert.True(channel.HitTest(layout, m.Series, null, 400, layout.Main.Y(inside), out _));
    }

    [Fact]
    public void Anchored_vwap_drawing_lines_follow_the_trades()
    {
        using var tape = History();
        var m = tape.Model;
        var vwap = new ChartDrawing { Kind = DrawingKind.AnchoredVwap, Band1 = 1, Band2 = 0 };
        vwap.Points.Add(new ChartPoint(m.Series[0].Time, 105));
        m.Drawings.Add(vwap);
        var parts = ChartAlertEngine.DrawingLines(m, vwap);
        Assert.Equal(new[] { "vwap", "u1", "d1" }, parts.Select(p => p.Field));
        Assert.True(ChartAlertEngine.TryValue(m, parts[0], 29, out var mid));
        Assert.Equal(105m, mid); // 15 prints at 100 and 15 at 110
        Assert.True(ChartAlertEngine.TryValue(m, parts[1], 29, out var upper) && upper > mid);
        Assert.True(ChartAlertEngine.TryValue(m, parts[2], 29, out var lower) && lower < mid);
    }
}

/// <summary>An indicator declaring one alertable level from bar 5 to the right edge.</summary>
public sealed class LevelsIndicator : Indicator
{
    public decimal Price { get; set; }

    public LevelsIndicator() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("X", "x") { VisualType = VisualMode.Hide, ShowZeroValue = false };
    }

    protected override void OnCalculate(int bar, decimal value) { }

    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        if (CurrentBar > 5) yield return new IndicatorLevel("lvl:" + Price, "My level", Price, 5);
    }
}

/// <summary>
/// Any custom indicator (like a freshly written one): it draws lines with DrawLine / DrawLines / DrawRectangle and its
/// trend-line objects and declares nothing for alerts. The chart captures them while drawing it.
/// </summary>
public sealed class PlainLinesIndicator : Indicator
{
    public decimal Level { get; set; }
    public bool Draw { get; set; } = true;
    public int ExtraSegments { get; set; }

    public PlainLinesIndicator() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("X", "x") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar != CurrentBar - 1) return;
        TrendLines.Clear();
        TrendLines.Add(new TrendLine(bar - 12, Level - 600, bar - 2, Level - 600, new TradingHub.Common.UniversalPen(Color.Teal)) { IsRay = true });
    }

    public decimal Curve(int bar) => Math.Round(Level - 700 + 60 * (decimal)Math.Sin(bar / 3.0), 1);

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar < 40 || !Draw) return;
        var pc = ChartInfo.PriceChartContainer;
        int last = CurrentBar - 1;
        var font = new RenderFont("Arial", 8);
        // a horizontal from bar last-30 to the right edge, named next to it
        int y = ChartInfo.GetYByPrice(Level, false);
        context.DrawLine(new RenderPen(Color.Orange, 2), ChartInfo.GetXByBar(last - 30), y, pc.Region.Right, y);
        context.DrawString("My level", font, Color.Orange, ChartInfo.GetXByBar(last - 30), y - 14);
        // a sloped two-point line reaching the last bar
        context.DrawLine(new RenderPen(Color.Cyan, 1), ChartInfo.GetXByBar(last - 20, false), ChartInfo.GetYByPrice(Level - 500, false),
            ChartInfo.GetXByBar(last, false), ChartInfo.GetYByPrice(Level - 300, false));
        // a bordered box (zone) continuing to the right
        int yt = ChartInfo.GetYByPrice(Level + 300, false), yb = ChartInfo.GetYByPrice(Level + 200, false);
        context.DrawRectangle(new RenderPen(Color.Magenta, 1), new Rectangle(ChartInfo.GetXByBar(last - 15), yt, pc.Region.Right - ChartInfo.GetXByBar(last - 15), yb - yt));
        // a curve drawn bar by bar with DrawLine (many touching segments) and another in one DrawLines call
        for (int b = last - 25; b < last; b++)
            context.DrawLine(new RenderPen(Color.Lime, 1), ChartInfo.GetXByBar(b, false), ChartInfo.GetYByPrice(Curve(b), false),
                ChartInfo.GetXByBar(b + 1, false), ChartInfo.GetYByPrice(Curve(b + 1), false));
        var pts = Enumerable.Range(last - 25, 26).Select(b => new Point(ChartInfo.GetXByBar(b, false), ChartInfo.GetYByPrice(Curve(b) - 400, false))).ToArray();
        context.DrawLines(new RenderPen(Color.Yellow, 1), pts);
        // a framed label (not a price line) and a small mark
        context.DrawString("label", font, Color.White, ChartInfo.GetXByBar(last - 5), y + 30);
        context.DrawRectangle(new RenderPen(Color.White, 1), new Rectangle(ChartInfo.GetXByBar(last - 5) - 3, y + 28, 50, 16));
        context.DrawLine(new RenderPen(Color.Red, 1), ChartInfo.GetXByBar(last - 3) - 3, y - 50, ChartInfo.GetXByBar(last - 3) + 3, y - 50);
        // a flood of tiny segments: capture stays bounded
        for (int i = 0; i < ExtraSegments; i++)
            context.DrawLine(new RenderPen(Color.FromArgb(255, 10, 20, (i % 200) + 30), 1), i % 900, 5 + i % 300, i % 900 + 1, 5 + i % 300);
    }
}

public sealed class DrawnLineAlertTests
{
    private static (AlertTape Tape, PlainLinesIndicator Ind, IndicatorSlot Slot) Setup()
    {
        var tape = new AlertTape();
        for (int m = 0; m < 80; m++) tape.At(m, 5000 + Math.Round(40 * (decimal)Math.Sin(m / 5.0), 1));
        var model = tape.Model;
        var ind = new PlainLinesIndicator();
        var slot = model.AddIndicator(ind);
        model.Update();
        ind.Level = model.Instrument.RoundPrice(model.Instrument.FromTicks(model.Series[^1].CloseTicks) + 150.25m);
        ind.RecalculateValues();
        model.Update();
        model.Viewport.BarWidth = 12;
        model.Viewport.AutoScale = false;
        model.Viewport.PriceTop = (double)ind.Level + 800;
        model.Viewport.PriceBottom = (double)ind.Level - 1200;
        Render(model);
        return (tape, ind, slot);
    }

    private static void Render(ChartModel model)
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1200, 700));
        new ChartRenderer().Render(surface.Canvas, model, 1200, 700, new ChartRenderState());
    }

    [Fact]
    public void Lines_of_an_indicator_that_declares_nothing_are_captured_with_exact_prices_and_merged_curves()
    {
        var (tape, ind, slot) = Setup();
        using var owned = tape;
        var lines = slot.DrawnLines;
        uint orange = (uint)Color.Orange.ToArgb(), cyan = (uint)Color.Cyan.ToArgb(), magenta = (uint)Color.Magenta.ToArgb();
        var h = Assert.Single(lines, l => l.Color == orange);
        Assert.Equal(ind.Level, h.LastPrice); // the converted price, not the pixel's
        Assert.Equal("My level", h.Text);
        Assert.True(h.Right);
        Assert.Single(lines, l => l.Color == cyan);
        Assert.Equal(2, lines.Count(l => l.Color == magenta));
        // each curve is one line (not one piece per bar), one point per bar
        var lime = Assert.Single(lines, l => l.Color == (uint)Color.Lime.ToArgb());
        Assert.Equal(26, lime.Prices.Length);
        Assert.Single(lines, l => l.Color == (uint)Color.Yellow.ToArgb());
        // not the label frame nor the small mark
        Assert.DoesNotContain(lines, l => l.Color == (uint)Color.White.ToArgb());
        Assert.DoesNotContain(lines, l => l.Color == (uint)Color.Red.ToArgb());
        // the trend-line object, already in bars and prices
        var teal = Assert.Single(lines, l => l.Color == (uint)Color.Teal.ToArgb());
        Assert.Equal(ind.Level - 600, teal.LastPrice);
        Assert.True(teal.Right);

        var ops = ChartAlertEngine.Lines(tape.Model).Where(o => o.Source == ChartAlertSource.DrawnLine).ToList();
        Assert.Contains(ops, o => o.Label.Contains("My level"));
        Assert.Equal(7, ops.Count); // orange, cyan, two box edges, lime, yellow, teal
    }

    [Fact]
    public void Right_click_picks_the_line_and_the_alert_follows_it()
    {
        var (tape, ind, slot) = Setup();
        using var owned = tape;
        var model = tape.Model;
        var layout = model.LastLayout!;
        int last = model.Series.Count - 1;
        float y = ((IChartContainer)layout.Main).GetYByPrice(ind.Level, false);
        var hit = ChartAlertEngine.LineAt(model, layout.CenterX(last - 10), y + 3, 10);
        Assert.NotNull(hit);
        Assert.Equal(ChartAlertSource.DrawnLine, hit!.Source);
        Assert.Contains("My level", hit.Label);
        Assert.True(ChartAlertEngine.TryValue(model, hit, last, out var v));
        Assert.Equal(ind.Level, v);
        Assert.False(ChartAlertEngine.TryValue(model, hit, last - 40, out _)); // before it starts

        model.HighlightedLine = hit;
        Render(model);
        Assert.NotEmpty(ChartAlertEngine.PixelsOf(model, layout, hit));

        var slope = ChartAlertEngine.Lines(model).First(o => o.Line?.Color == (uint)Color.Cyan.ToArgb());
        Assert.True(ChartAlertEngine.TryValue(model, slope, last - 10, out var mid));
        Assert.True(ChartAlertEngine.TryValue(model, slope, last, out var end));
        Assert.True(end > mid);

        foreach (var color in new[] { Color.Lime, Color.Yellow })
        {
            var curve = ChartAlertEngine.Lines(model).First(o => o.Line?.Color == (uint)color.ToArgb());
            for (int b = last - 25; b <= last; b += 5)
            {
                Assert.True(ChartAlertEngine.TryValue(model, curve, b, out var cv));
                Assert.Equal(ind.Curve(b) - (color == Color.Yellow ? 400 : 0), cv);
            }
        }

        // the saved copy (workspace JSON) still gives the level after loading
        var json = Workspace.ChartAlertsJson(new List<ChartAlert> { new() { Condition = ChartAlertCondition.Crossing, Right = hit.Copy() } });
        var back = Workspace.ChartAlertsFromJson(json).Single();
        Assert.True(ChartAlertEngine.TryValue(model, back.Right, last, out var v2));
        Assert.Equal(ind.Level, v2);
        model.Alerts.Add(back);
        Assert.NotNull(ChartAlertEngine.Check(model, back, last));

        // the indicator moves it a little (a forming level): the alert follows
        ind.Level += 40;
        Render(model);
        Assert.True(ChartAlertEngine.TryValue(model, back.Right, last, out var moved));
        Assert.Equal(ind.Level, moved);

        // when it is no longer drawn, its last shape stays
        ind.Draw = false;
        Render(model);
        Assert.DoesNotContain(slot.DrawnLines, l => l.Color == (uint)Color.Orange.ToArgb());
        Assert.True(ChartAlertEngine.TryValue(model, back.Right, last, out var kept));
        Assert.Equal(ind.Level, kept);
    }

    [Fact]
    public void Capture_stays_bounded_and_hidden_indicators_offer_no_lines()
    {
        var (tape, ind, slot) = Setup();
        using var owned = tape;
        ind.ExtraSegments = 200_000;
        Render(tape.Model);
        Assert.InRange(slot.DrawnLines.Count, 1, 3000);
        slot.Visible = false;
        Render(tape.Model);
        Assert.Empty(slot.DrawnLines);
    }

    [Fact]
    public void A_declared_level_is_picked_where_it_is_drawn_and_highlighted()
    {
        using var tape = new AlertTape();
        for (int m = 0; m < 60; m++) tape.At(m, 100 + m % 5);
        var model = tape.Model;
        model.AddIndicator(new LevelsIndicator { Price = 102 });
        model.Update();
        model.Viewport.AutoScale = false;
        model.Viewport.PriceTop = 110;
        model.Viewport.PriceBottom = 95;
        Render(model);
        var layout = model.LastLayout!;
        float y = ((IChartContainer)layout.Main).GetYByPrice(102, false);
        var hit = ChartAlertEngine.LineAt(model, layout.CenterX(model.Series.Count - 3), y, 10);
        Assert.Equal(ChartAlertSource.Level, hit!.Source);
        Assert.Equal(102, ChartAlertEngine.LevelOf(model, hit)!.Value.Price);
        model.HighlightedLine = hit;
        Render(model); // draws the highlight without throwing

        var alert = new ChartAlert { Condition = ChartAlertCondition.Touching, Right = hit };
        Assert.True(ChartAlertEngine.Check(model, alert, 12)); // bar 12 trades at 102
        Assert.False(ChartAlertEngine.Check(model, alert, 10)); // bar 10 trades at 100
    }
}
