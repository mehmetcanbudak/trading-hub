using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;

namespace TradingHub.Tests;

/// <summary>Level-state conditions, the every-time trigger with its minimum interval, expiry and legacy drawing labels.</summary>
public sealed class ChartAlertStateTests
{
    private static readonly DateTime Now = AlertTape.T0.AddDays(1);

    /// <summary>Bars 0..29 alternate 110 / 100; bar 29 is forming at 100.</summary>
    private static AlertTape History()
    {
        var tape = new AlertTape();
        for (int m = 0; m < 30; m++) tape.At(m, m % 2 == 0 ? 110 : 100);
        return tape;
    }

    private static ChartAlert Add(AlertTape tape, ChartAlertCondition condition, ChartAlertTrigger trigger, decimal right, decimal? lower = null)
    {
        var alert = new ChartAlert { Condition = condition, Trigger = trigger, Right = ChartAlertOperand.Fixed(right), Lower = lower is { } l ? ChartAlertOperand.Fixed(l) : null };
        tape.Model.Alerts.Add(alert);
        return alert;
    }

    [Fact]
    public void Greater_than_fires_on_the_transition_into_the_state_not_while_it_holds()
    {
        using var tape = History();
        var above = Add(tape, ChartAlertCondition.GreaterThan, ChartAlertTrigger.OncePerBar, 105);
        Assert.Empty(tape.Evaluate()); // 100 when armed: below
        tape.At(30, 100);
        Assert.Empty(tape.Evaluate());
        tape.At(30, 110, 10);
        Assert.Same(above, Assert.Single(tape.Evaluate()).Alert);
        tape.At(31, 110); // still above on a new bar: no new transition
        Assert.Empty(tape.Evaluate());
        tape.At(31, 100, 10);
        Assert.Empty(tape.Evaluate());
        tape.At(31, 110, 20); // back above inside bar 31
        Assert.Single(tape.Evaluate());
        tape.At(31, 100, 30);
        Assert.Empty(tape.Evaluate());
        tape.At(31, 110, 40); // once per bar: bar 31 already fired
        Assert.Empty(tape.Evaluate());
    }

    [Fact]
    public void A_state_already_held_when_arming_does_not_fire_until_it_is_entered_again()
    {
        using var tape = History();
        var below = Add(tape, ChartAlertCondition.LessThan, ChartAlertTrigger.OncePerBar, 105);
        Assert.Empty(tape.Evaluate()); // already below when armed
        tape.At(30, 100);
        Assert.Empty(tape.Evaluate());
        tape.At(30, 110, 10);
        Assert.Empty(tape.Evaluate());
        tape.At(30, 100, 20);
        Assert.Same(below, Assert.Single(tape.Evaluate()).Alert);
    }

    [Fact]
    public void Inside_and_outside_channel_fire_on_entering_each_state()
    {
        using var tape = History();
        var inside = Add(tape, ChartAlertCondition.InsideChannel, ChartAlertTrigger.OncePerBar, 106, 104);
        var outside = Add(tape, ChartAlertCondition.OutsideChannel, ChartAlertTrigger.OncePerBar, 106, 104);
        Assert.Empty(tape.Evaluate()); // 100: outside when armed
        tape.At(30, 105);
        Assert.Same(inside, Assert.Single(tape.Evaluate()).Alert);
        tape.At(30, 110, 10);
        Assert.Same(outside, Assert.Single(tape.Evaluate()).Alert);
        Assert.True(ChartAlertEngine.Check(tape.Model, inside, 30) == false && ChartAlertEngine.Check(tape.Model, outside, 30) == true);
    }

    [Fact]
    public void Level_states_on_bar_close_compare_closed_bars_with_the_state_when_armed()
    {
        using var tape = History();
        var above = Add(tape, ChartAlertCondition.GreaterThan, ChartAlertTrigger.OncePerBarClose, 105);
        Assert.Empty(tape.Evaluate()); // 29 forming at 100: below when armed
        tape.At(29, 110, 30); // 29 goes above while forming: nothing until it closes
        Assert.Empty(tape.Evaluate());
        tape.At(30, 110); // 29 closed above after being below when armed
        Assert.Same(above, Assert.Single(tape.Evaluate()).Alert);
        tape.At(31, 100); // 30 closed above again: no new transition
        Assert.Empty(tape.Evaluate());
        tape.At(32, 100); // 31 closed below
        Assert.Empty(tape.Evaluate());
        tape.At(32, 110, 10).At(33, 100); // 32 closed above after a closed bar below
        Assert.Single(tape.Evaluate());
    }

    [Fact]
    public void Every_time_fires_on_each_new_crossing_but_never_twice_within_the_minimum_interval()
    {
        using var tape = History();
        var every = Add(tape, ChartAlertCondition.Crossing, ChartAlertTrigger.EveryTime, 105);
        var model = tape.Model;
        Assert.Empty(ChartAlertEngine.Evaluate(model, Now));
        tape.At(30, 100);
        Assert.Empty(ChartAlertEngine.Evaluate(model, Now.AddSeconds(5)));
        tape.At(30, 110, 10);
        var first = Assert.Single(ChartAlertEngine.Evaluate(model, Now.AddSeconds(10)));
        tape.At(30, 100, 20);
        Assert.Empty(ChartAlertEngine.Evaluate(model, Now.AddSeconds(20)));
        tape.At(30, 110, 30);
        Assert.Empty(ChartAlertEngine.Evaluate(model, Now.AddSeconds(30))); // flapping inside the minimum interval
        tape.At(30, 100, 40);
        Assert.Empty(ChartAlertEngine.Evaluate(model, Now.AddSeconds(40)));
        tape.At(30, 110, 50);
        var second = Assert.Single(ChartAlertEngine.Evaluate(model, Now.AddSeconds(10) + ChartAlert.EveryTimeMinimumInterval));
        Assert.NotEqual(first.EventId, second.EventId); // same bar, distinct stable ids
        Assert.True(every.Active);
    }

    [Fact]
    public void Expired_alerts_stop_evaluating_and_the_expiry_persists()
    {
        using var tape = History();
        var alert = Add(tape, ChartAlertCondition.Crossing, ChartAlertTrigger.OncePerBar, 105);
        alert.ExpiresUtc = Now.AddHours(1);
        Assert.Empty(ChartAlertEngine.Evaluate(tape.Model, Now));
        tape.At(30, 100).At(30, 110, 10);
        Assert.Empty(ChartAlertEngine.Evaluate(tape.Model, Now.AddHours(2)));
        Assert.True(alert.IsExpired(Now.AddHours(2)));
        Assert.False(alert.IsExpired(Now));
        Assert.True(alert.Active);

        var back = Workspace.ChartAlertsFromJson(Workspace.ChartAlertsJson(new List<ChartAlert> { alert,
            new() { Condition = ChartAlertCondition.InsideChannel, Trigger = ChartAlertTrigger.EveryTime, Right = ChartAlertOperand.Fixed(2), Lower = ChartAlertOperand.Fixed(1) } }));
        Assert.Equal(alert.ExpiresUtc, back[0].ExpiresUtc);
        Assert.Equal(ChartAlertTrigger.EveryTime, back[1].Trigger);
        Assert.Equal(ChartAlertCondition.InsideChannel, back[1].Condition);
        // a channel state without its lower edge is malformed
        Assert.Empty(Workspace.ChartAlertsFromJson("{\"ChartAlerts\":[{\"Id\":\"x1\",\"Condition\":\"OutsideChannel\",\"Right\":{\"Source\":\"Value\",\"Value\":1}}]}"));
    }

    [Fact]
    public void A_legacy_alert_line_lists_its_kind_not_the_marker_text()
    {
        using var tape = History();
        var legacy = new ChartDrawing { Kind = DrawingKind.HorizontalLine, Alert = true, Text = "alerta" };
        legacy.Points.Add(new ChartPoint(tape.Model.Series[0].Time, 105));
        var named = new ChartDrawing { Kind = DrawingKind.HorizontalLine, Text = "Weekly pivot" };
        named.Points.Add(new ChartPoint(tape.Model.Series[0].Time, 106));
        tape.Model.Drawings.AddRange(new[] { legacy, named });
        Assert.Equal("Horizontal line", ChartAlertEngine.DrawingLines(tape.Model, legacy).Single().Label);
        Assert.Equal("Weekly pivot", ChartAlertEngine.DrawingLines(tape.Model, named).Single().Label);
    }
}
