using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Charting.Dom;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>A running total of the traded volume: a long-lookback value that more history moves.</summary>
public sealed class ChartReviewRunningVolume : Indicator
{
    public ChartReviewRunningVolume() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("Total", "Total volume") { ShowZeroValue = true };
    }

    protected override void OnCalculate(int bar, decimal value) => this[bar] = (bar > 0 ? this[bar - 1] : 0) + GetCandle(bar).Volume;
}

/// <summary>Prints on a non-time chart (several prints, or one large one, can share a bar time).</summary>
internal sealed class ChartReviewTape : IDisposable
{
    private long _id;

    public ChartReviewTape(string period)
    {
        Data = new InstrumentData(new Instrument { Symbol = "REVIEW", FeedId = "sim", TickSize = 0.1m, VolumeStep = 1 }, new SimulatedFeed());
        Model = new ChartModel(Data, ChartPeriod.Parse(period), new ChartSettings { TimeZoneId = "UTC" });
    }

    public InstrumentData Data { get; }
    public ChartModel Model { get; }

    public static DateTime At(int second) => AlertTape.T0.AddSeconds(second);

    public ChartReviewTape Print(int second, decimal price, decimal volume)
    {
        Data.Trades.Add(new Trade(++_id, At(second), price, volume, TradeSide.Buy));
        Model.Update();
        return this;
    }

    public List<string> Evaluate() => ChartAlertEngine.Evaluate(Model, AlertTape.T0.AddDays(1)).Select(f => f.EventId).ToList();

    public void Dispose()
    {
        Model.Dispose();
        Data.Dispose();
    }
}

/// <summary>Alert identity across timeframes, rebuilt history and shared bar times; alert lifecycle across erases and reopening.</summary>
public sealed class ChartReviewAlertTests
{
    /// <summary>Bars 0..29 alternate 110 / 100; bar 29 is forming at 100.</summary>
    private static AlertTape History()
    {
        var tape = new AlertTape();
        for (int m = 0; m < 30; m++) tape.At(m, m % 2 == 0 ? 110 : 100);
        return tape;
    }

    [Fact]
    public void A_timeframe_change_rearms_alerts_on_the_new_bars()
    {
        using var tape = new AlertTape();
        for (int minute = 0; minute < 90; minute++) tape.At(minute, minute % 2 == 0 ? 110 : 100);
        var model = tape.Model;
        var perBar = new ChartAlert { Condition = ChartAlertCondition.CrossingUp, Right = ChartAlertOperand.Fixed(105), Trigger = ChartAlertTrigger.OncePerBar };
        var onClose = new ChartAlert { Condition = ChartAlertCondition.CrossingUp, Right = ChartAlertOperand.Fixed(105), Trigger = ChartAlertTrigger.OncePerBarClose };
        model.Alerts.Add(perBar);
        model.Alerts.Add(onClose);
        Assert.Empty(tape.Evaluate()); // armed on the 01:29 minute

        // to H1 in the middle of the 01:00 hour: the forming hour opened before the minute the alerts armed on
        model.SetPeriod(ChartPeriod.H1);
        Assert.Equal(2, model.Series.Count);
        Assert.Empty(tape.Evaluate());
        tape.At(90, 100);
        Assert.Empty(tape.Evaluate());
        tape.At(90, 110, 10); // crosses up inside the forming hour
        var fired = Assert.Single(tape.Evaluate());
        Assert.Same(perBar, fired.Alert);
        Assert.Equal($"chart:{perBar.Id}:{AlertTape.T0.AddHours(1).Ticks}", fired.EventId);

        // back to M1: the forming minute crossed before the switch, which is history for the minute bars
        model.SetPeriod(ChartPeriod.M1);
        Assert.Empty(tape.Evaluate());

        // H1 again: the hour that fired does not fire twice, and its close is checked when it closes
        model.SetPeriod(ChartPeriod.H1);
        Assert.Empty(tape.Evaluate());
        tape.At(120, 100); // 01:00 closed at 110 after 00:00 closed at 100
        var close = Assert.Single(tape.Evaluate());
        Assert.Same(onClose, close.Alert);
        Assert.Equal(AlertTape.T0.AddHours(1), close.BarTime);
    }

    [Theory]
    [InlineData(false)] // a correction under the chart: aggregation rebuilds the candles
    [InlineData(true)] // loaded or extended history: the chart rebuilds (what HistoryLoaded does)
    public void Rebuilt_history_never_fires_alerts_and_they_keep_working_afterwards(bool rebuild)
    {
        using var tape = History();
        var m = tape.Model;
        var slot = m.AddIndicator(new ChartReviewRunningVolume());
        m.Update();
        var total = new ChartAlert
        {
            Left = new ChartAlertOperand { Source = ChartAlertSource.Series, Indicator = ChartAlertEngine.KeyOf(m, slot), Field = "Total", Label = "Total" },
            Condition = ChartAlertCondition.GreaterThan,
            Right = ChartAlertOperand.Fixed(100),
        };
        var cross = new ChartAlert { Condition = ChartAlertCondition.CrossingUp, Right = ChartAlertOperand.Fixed(105) };
        m.Alerts.Add(total);
        m.Alerts.Add(cross);
        Assert.Empty(tape.Evaluate());
        Assert.False(ChartAlertEngine.Check(m, total, m.Series.Count - 1)); // 30 traded

        // older prints arrive: the running total is now above 100 on every bar although price did nothing
        var trades = Enumerable.Range(0, tape.Data.Trades.Count).Select(tape.Data.Trades.Get).ToList();
        var older = Enumerable.Range(0, 5).Select(i => new Trade(10_000 + i, AlertTape.T0.AddHours(-1).AddMinutes(i), 100, 40, TradeSide.Buy));
        tape.Data.Trades.Replace(older.Concat(trades));
        if (rebuild) m.Rebuild();
        else m.Update();
        Assert.False(m.IsAggregating);
        Assert.True(ChartAlertEngine.Check(m, total, m.Series.Count - 1));
        Assert.Empty(tape.Evaluate());

        // real moves after the rebuild still fire
        tape.At(30, 100);
        Assert.Empty(tape.Evaluate());
        tape.At(30, 110, 10);
        Assert.Same(cross, Assert.Single(tape.Evaluate()).Alert);
    }

    [Fact]
    public void Bars_sharing_a_time_are_different_alert_bars_and_every_closed_bar_is_checked()
    {
        using var tape = new ChartReviewTape("Volume4");
        var onClose = new ChartAlert { Condition = ChartAlertCondition.Crossing, Right = ChartAlertOperand.Fixed(105), Trigger = ChartAlertTrigger.OncePerBarClose };
        var perBar = new ChartAlert { Condition = ChartAlertCondition.Crossing, Right = ChartAlertOperand.Fixed(105), Trigger = ChartAlertTrigger.OncePerBar };
        tape.Model.Alerts.Add(onClose);
        tape.Model.Alerts.Add(perBar);
        string Id(ChartAlert a, int second, int ordinal = 0) =>
            $"chart:{a.Id}:{ChartReviewTape.At(second).Ticks}" + (ordinal > 0 ? $":{ordinal}" : "");

        tape.Print(0, 100, 4).Print(1, 100, 3); // bar 0 full at 100, bar 1 forming at 100
        Assert.Empty(tape.Evaluate());
        tape.Print(2, 110, 1); // bar 1 crosses up while forming
        Assert.Equal(new[] { Id(perBar, 1) }, tape.Evaluate());
        tape.Print(2, 110, 4); // bar 2 (time 2 s): bar 1 closed above
        Assert.Equal(new[] { Id(onClose, 1) }, tape.Evaluate());
        tape.Print(2, 100, 4); // bar 3 shares time 2 s and crosses down; bar 2 closed without a cross
        Assert.Equal(new[] { Id(perBar, 2, 1) }, tape.Evaluate());
        tape.Print(2, 110, 4); // bar 4, the third bar at 2 s, crosses up; bar 3 closed across the line
        Assert.Equal(new[] { Id(onClose, 2, 1), Id(perBar, 2, 2) }, tape.Evaluate());

        // one print fills three bars at once: bars 4, 5 and 6 close together and each close is checked
        tape.Print(3, 100, 12);
        Assert.Equal(8, tape.Model.Series.Count);
        Assert.Equal(new[] { Id(onClose, 2, 2), Id(onClose, 3) }, tape.Evaluate());
    }

    [Fact]
    public void A_print_filling_several_volume_bars_gives_each_bar_only_its_part()
    {
        using var tape = new ChartReviewTape("Volume4");
        tape.Print(0, 100, 2).Print(1, 101, 11).Print(2, 102, 1); // 2 + 2 | 4 | 4 | 1 + 1
        var m = tape.Model;
        Assert.Equal(4, m.Series.Count);
        var trades = new List<BarTrade>();
        var parts = new List<decimal[]>();
        for (int bar = 0; bar < m.Series.Count; bar++)
        {
            m.CopyBarTrades(bar, trades);
            Assert.Equal(m.Series[bar].Volume, trades.Sum(t => t.Volume));
            parts.Add(trades.Select(t => t.Volume).ToArray());
        }

        Assert.Equal(new[] { 2m, 2m }, parts[0]);
        Assert.Equal(new[] { 4m }, parts[1]);
        Assert.Equal(new[] { 4m }, parts[2]);
        Assert.Equal(new[] { 1m, 1m }, parts[3]);
    }

    [Fact]
    public void Undoing_an_erase_brings_back_the_alerts_of_the_erased_drawing()
    {
        using var tape = History();
        var m = tape.Model;
        var line = new ChartDrawing { Kind = DrawingKind.HorizontalLine };
        line.Points.Add(new ChartPoint(m.Series[0].Time, 105));
        m.Drawings.Add(line);
        var alert = new ChartAlert { Condition = ChartAlertCondition.Crossing, Right = ChartAlertEngine.DrawingLines(m, line)[0] };
        m.Alerts.Add(alert);

        // a scribble erases the line: its alert goes with it and waits
        int index = m.Drawings.IndexOf(line);
        m.Drawings.Remove(line);
        Assert.Equal(new[] { alert }, ChartAlertEngine.Prune(m));
        Assert.Empty(m.Alerts);
        Assert.True(m.HasAlertsAwaitingDrawings);
        Assert.Empty(ChartAlertEngine.RestoreWithDrawings(m));

        // the gesture chip's undo puts the line back: its alert comes back, re-armed
        new ChartController(m, new ChartRenderState()).UndoErase(new[] { (index, line) });
        Assert.Equal(new[] { alert }, ChartAlertEngine.RestoreWithDrawings(m));
        Assert.Same(alert, Assert.Single(m.Alerts));
        Assert.False(m.HasAlertsAwaitingDrawings);
        Assert.Empty(ChartAlertEngine.Prune(m));
        Assert.Empty(tape.Evaluate()); // 28 → 29 crossed before it came back
        tape.At(30, 100);
        Assert.Empty(tape.Evaluate());
        tape.At(30, 110, 10);
        Assert.Same(alert, Assert.Single(tape.Evaluate()).Alert);

        // drawings that could not be read are not gone: nothing is removed while they are unreadable
        m.DrawingsUnreadable = true;
        m.Drawings.Remove(line);
        Assert.Empty(ChartAlertEngine.Prune(m));
        Assert.Same(alert, Assert.Single(m.Alerts));
    }

    [Fact]
    public void Reopening_a_chart_keeps_its_alerts_and_another_instrument_parks_them_until_it_comes_back()
    {
        static ChartAlert On(string? indicator) => new()
        {
            Condition = ChartAlertCondition.Crossing,
            Right = indicator == null ? ChartAlertOperand.Fixed(105) : new ChartAlertOperand { Source = ChartAlertSource.Series, Indicator = indicator, Field = "x", Label = "x" },
        };

        var price = On(null);
        var failed = On("A#0");
        var shifted = On("A#1");
        var unloaded = On("Missing#0");
        var plain = On("B#0");
        var parked = new Dictionary<string, List<ChartAlert>>(StringComparer.OrdinalIgnoreCase);

        // the same instrument (the picker, a linked chart, quick entry); the copy of the first A failed, so the second
        // A is now A#0
        var copied = new Dictionary<string, string?> { ["A#0"] = null, ["A#1"] = "A#0", ["B#0"] = "B#0" };
        var same = Workspace.CarryChartAlerts(new List<ChartAlert> { price, failed, shifted, unloaded, plain }, copied, false, "sim/BTC", "sim/BTC", parked);
        Assert.Equal(new[] { price, shifted, unloaded, plain }, same.Alerts);
        Assert.Equal("A#0", shifted.Right.Indicator);
        Assert.Equal("Missing#0", unloaded.Right.Indicator); // an indicator that failed to load keeps its alerts
        Assert.Equal((0, 0, 1), (same.Parked, same.Resumed, same.Lost));
        Assert.Empty(parked);

        // another instrument: they wait for BTC
        var kept = new Dictionary<string, string?> { ["A#0"] = "A#0", ["B#0"] = "B#0" };
        var eth = Workspace.CarryChartAlerts(same.Alerts, kept, true, "sim/BTC", "sim/ETH", parked);
        Assert.Empty(eth.Alerts);
        Assert.Equal((4, 0, 0), (eth.Parked, eth.Resumed, eth.Lost));
        Assert.Equal(4, parked["SIM/btc"].Count);

        // back to BTC: its alerts resume and the one made on ETH waits for ETH
        var ethAlert = On(null);
        var btc = Workspace.CarryChartAlerts(new List<ChartAlert> { ethAlert }, kept, true, "sim/ETH", "sim/BTC", parked);
        Assert.Equal(new[] { price, shifted, unloaded, plain }, btc.Alerts);
        Assert.Equal((1, 4, 0), (btc.Parked, btc.Resumed, btc.Lost));
        Assert.Same(ethAlert, Assert.Single(parked["sim/ETH"]));
        Assert.False(parked.ContainsKey("sim/BTC"));
    }

    [Fact]
    public void Saved_alerts_with_values_this_build_cannot_read_are_dropped_not_defaulted()
    {
        static List<ChartAlert> Load(string alert) => Workspace.ChartAlertsFromJson("{\"ChartAlerts\":[" + alert + "]}");
        Assert.Empty(Load("{\"Condition\":\"Sideways\",\"Right\":{\"Source\":\"Value\",\"Value\":1}}"));
        Assert.Empty(Load("{\"Condition\":\"Crossing\",\"Trigger\":\"Hourly\",\"Right\":{\"Source\":\"Value\",\"Value\":1}}"));
        Assert.Empty(Load("{\"Condition\":\"Crossing\",\"Right\":{\"Source\":\"Hologram\",\"Value\":1}}"));
        Assert.Empty(Load("{\"Condition\":\"Crossing\",\"Right\":{\"Source\":\"Value\",\"Value\":\"lots\"}}"));
        // a 0 % move holds on every flat bar
        Assert.Empty(Load("{\"Condition\":\"MovingUpPercent\",\"Right\":{\"Source\":\"Value\",\"Value\":0}}"));
        var readable = Assert.Single(Load("{\"Condition\":\"MovingUpPercent\",\"Right\":{\"Source\":\"Value\",\"Value\":0.5}}"));
        Assert.Equal(ChartAlertTrigger.OncePerBar, readable.Trigger); // a member that was not written keeps its default
    }

    [Fact]
    public void Drawing_with_a_tool_while_drawings_are_hidden_shows_them()
    {
        using var tape = History();
        var m = tape.Model;
        m.Settings.HideDrawings = true;
        var state = new ChartRenderState();
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(900, 500));
        new ChartRenderer().Render(surface.Canvas, m, 900, 500, state);
        var controller = new ChartController(m, state) { ActiveTool = DrawingKind.HorizontalLine };
        int shown = 0;
        controller.DrawingsShown += () => shown++;
        controller.MouseDown(300, 300, PointerButton.Left, 1, InputModifiers.None);
        Assert.False(m.Settings.HideDrawings);
        Assert.Equal(1, shown);
        Assert.Single(m.Drawings);
    }

    [Fact]
    public void A_channel_width_dropped_across_a_gap_passes_through_the_drop_point()
    {
        using var tape = new AlertTape();
        for (int minute = 0; minute < 10; minute++) tape.At(minute, 100);
        for (int minute = 100; minute < 110; minute++) tape.At(minute, 100); // 90 minutes without bars after bar 9
        var s = tape.Model.Series;
        var d = new ChartDrawing { Kind = DrawingKind.ParallelChannel };
        d.Points.Add(new ChartPoint(s[5].Time, 100));
        d.Points.Add(new ChartPoint(s[15].Time, 110));
        d.InitChannel(5);
        d.MoveHandle(2, new ChartPoint(s[12].Time, 130), s);
        // drawn along the bars, the base is at 107 on bar 12 (7 of its 10 bars): the width is 23
        Assert.Equal(23, d.ChannelOffset, 6);
        Assert.Equal(130, d.ValueAt(s, 12, "upper")!.Value, 6);
    }

    [Fact]
    public void The_recent_traded_window_slides_over_the_prints()
    {
        using var data = new InstrumentData(new Instrument { Symbol = "DOMREVIEW", FeedId = "sim", TickSize = 0.1m, VolumeStep = 1 }, new SimulatedFeed());
        var t0 = AlertTape.T0;
        data.Trades.Add(new Trade(1, t0, 100, 1, TradeSide.Buy));
        data.Trades.Add(new Trade(2, t0.AddSeconds(20), 100, 2, TradeSide.Sell));
        var dom = new DomModel(data, new DomSettings { ChangeWindowSeconds = 30 });
        long tick = data.Instrument.ToTicks(100);
        Assert.Equal((2m, 1m), dom.RecentTraded(t0.AddSeconds(25))[tick]);
        data.Trades.Add(new Trade(3, t0.AddSeconds(50), 100, 3, TradeSide.Buy));
        Assert.Equal((0m, 3m), dom.RecentTraded(t0.AddSeconds(55))[tick]); // the first two left the window
        Assert.Empty(dom.RecentTraded(t0.AddSeconds(85)));
        Assert.Equal((2m, 1m), dom.RecentTraded(t0.AddSeconds(25))[tick]); // the clock went back (a replay seek)
    }
}
