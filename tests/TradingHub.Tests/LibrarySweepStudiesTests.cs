using TradingHub.Data;
using TradingHub.Indicators.Native;
using static TradingHub.Tests.LibraryStudyFixture;

namespace TradingHub.Tests;

public class LibrarySweepStudiesTests
{
    private static readonly DateTime Monday = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    // swing high 20 at bar 2 (radius 2) is confirmed at the close of bar 4
    private static readonly Ohlc[] SwingHigh = { new(10, 11, 9, 10), new(10, 12, 9, 11), new(11, 20, 10, 15), new(15, 16, 12, 13), new(13, 14, 11, 12) };

    [Fact]
    public void Wick_sweep_is_known_at_the_sweeping_close_after_the_swing_confirmation()
    {
        using var model = Model(SwingHigh.Concat(new Ohlc[] { new(12, 22, 11, 18), new(18, 19, 15, 16), new(16, 24, 16, 23), new(23, 24, 22, 23) }).ToArray());
        var study = Add(model, new LiquiditySweeps { SwingLength = 2 });
        var level = Assert.Single(study.Scene.Lines, l => l.Alert == "Liquidity high");
        Assert.Equal(2, level.From); Assert.Equal(4, level.Available); Assert.Equal(20m, level.Price);
        var tag = Assert.Single(study.Scene.Tags);
        Assert.Equal(("Wick", 5, 5, 22m), (tag.Text, tag.Bar, tag.Available, tag.Price));
        var area = Assert.Single(study.Scene.Boxes);
        Assert.Equal((5, 5, 22m, 20m, "Swept area"), (area.From, area.Available, area.Top, area.Bottom, area.Alert));
        Assert.Equal(7, area.To); Assert.Equal(7, area.EndAvailable); // closed above the swept area
        var signal = Series(study, "LiquiditySweeps.Signal");
        for (int bar = 0; bar <= 4; bar++) Assert.Equal(0m, signal[bar]);
        Assert.Equal(-1m, signal[5]); Assert.Equal(-1m, Series(study, "LiquiditySweeps.Wick")[5]); Assert.Equal(0m, Series(study, "LiquiditySweeps.Retest")[5]);
        Assert.Equal(0m, signal[6]);
    }

    [Fact]
    public void Retest_sweep_follows_a_break_and_is_ignored_in_wick_only_mode()
    {
        var bars = SwingHigh.Concat(new Ohlc[] { new(12, 22, 11, 21), new(21, 23, 19, 22), new(22, 23, 21, 22) }).ToArray();
        using var model = Model(bars);
        var study = Add(model, new LiquiditySweeps { SwingLength = 2 });
        var tag = Assert.Single(study.Scene.Tags);
        Assert.Equal(("Retest", 6, 6, 19m), (tag.Text, tag.Bar, tag.Available, tag.Price));
        var area = Assert.Single(study.Scene.Boxes);
        Assert.Equal((6, 6, 20m, 19m), (area.From, area.Available, area.Top, area.Bottom));
        Assert.Equal(0m, Series(study, "LiquiditySweeps.Signal")[5]); // the break itself is not a sweep
        Assert.Equal(1m, Series(study, "LiquiditySweeps.Signal")[6]);
        Assert.Equal(1m, Series(study, "LiquiditySweeps.Retest")[6]);

        using var wicksModel = Model(bars);
        var wicks = Add(wicksModel, new LiquiditySweeps { SwingLength = 2, Mode = LiquiditySweepMode.WicksOnly });
        Assert.Empty(wicks.Scene.Tags);
        Assert.Equal(5, Assert.Single(wicks.Scene.Lines, l => l.Alert == "Liquidity high").To); // the break retires the level
    }

    // Monday 2026-01-05, H1 bars from 00:00 UTC (GMT: the London session is 08:00-17:00 UTC). Range 110/90; bar 17 takes
    // the high and closes outside; bar 20 is the first close back inside, three bars after the sweep.
    private static Ohlc[] SessionSweepBars()
    {
        var bars = Enumerable.Repeat(new Ohlc(100, 101, 99, 100), 8).ToList();
        bars.AddRange(new Ohlc[]
        {
            new(100, 102, 98, 101), new(101, 104, 99, 103), new(103, 110, 102, 105), new(105, 106, 97, 99), new(99, 100, 90, 95),
            new(95, 101, 94, 100), new(100, 103, 98, 101), new(101, 104, 99, 102), new(102, 103, 99, 100),
            new(100, 112, 99, 111), new(111, 112, 110, 111), new(111, 112, 110, 111), new(111, 111, 104, 105), new(105, 106, 104, 105),
        });
        return bars.ToArray();
    }

    [Theory]
    [InlineData(3, "Valid sweep", -1)]
    [InlineData(2, "Invalid sweep", 0)]
    public void Session_sweep_is_valid_only_when_price_returns_inside_within_the_window(int window, string label, int sweep)
    {
        using var model = Model(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), ChartPeriod.H1, null, SessionSweepBars());
        var study = Add(model, new SessionSweepInverseFvg { Session = WorldSession.London, SweepBars = window });
        var range = Assert.Single(study.SessionRanges);
        Assert.Equal((8, 16, 17, 110m, 90m), (range.From, range.To, range.Available, range.Top, range.Bottom));
        var high = Assert.Single(study.Scene.Lines, l => l.Alert == "Session high");
        Assert.Equal((8, 17, 110m), (high.From, high.Available, high.Price));
        Assert.Contains(study.Scene.Marks, m => m.Bar == 17 && m.Available == 17 && m.Kind == StudyMarkKind.Circle && m.Price == 112);
        var tag = Assert.Single(study.Scene.Tags);
        Assert.Equal((label, 20, 20), (tag.Text, tag.Bar, tag.Available));
        var series = Series(study, "SessionSweepInverseFvg.Sweep");
        for (int bar = 0; bar < 20; bar++) Assert.Equal(0m, series[bar]);
        Assert.Equal((decimal)sweep, series[20]);
    }

    [Fact]
    public void London_session_boundaries_follow_gmt_and_bst_across_the_uk_clock_change()
    {
        // Friday 2026-03-27 (GMT, 08:00 local = 08:00 UTC) to Monday 2026-03-30 (BST, 08:00 local = 07:00 UTC); H1 bars
        using var model = Model(new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc), ChartPeriod.H1, null, Walk(96));
        var study = Add(model, new SessionSweepInverseFvg { Session = WorldSession.London });
        Assert.Equal(new[] { (8, 16, 17), (72 + 7, 72 + 15, 72 + 16) }, study.SessionRanges.Select(r => (r.From, r.To, r.Available)));
        Assert.Equal(new DateTime(2026, 3, 30, 7, 0, 0, DateTimeKind.Utc), study.Bars[study.SessionRanges[1].From].Time);
        Assert.Equal(new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc), study.Bars[study.SessionRanges[0].From].Time);
    }

    // H4 bars from Monday 00:00 UTC (six per session day). Day 0 is the possibly partial first day (no levels); day 1 sets
    // high 110 / low 90; bar 13 on day 2 sweeps the low; bars 14-16 leave a bullish FVG (99-103) completed at bar 16.
    private static Ohlc[] PriorDayBars() =>
        Enumerable.Repeat(new Ohlc(100, 102, 98, 100), 6).Concat(new Ohlc[]
        {
            new(100, 105, 95, 100), new(100, 110, 98, 102), new(102, 104, 90, 96), new(96, 100, 94, 98), new(98, 101, 95, 99), new(99, 102, 96, 100),
            new(100, 103, 96, 97), new(97, 98, 89, 95), new(95, 99, 94, 98), new(98, 108, 97, 107), new(107, 109, 103, 108),
            new(108, 109, 100, 104), new(104, 105, 103, 104),
        }).ToArray();

    [Theory]
    [InlineData(PriorDayEntryTiming.OnFormation, 16)]
    [InlineData(PriorDayEntryTiming.AtMidpoint, 17)]
    public void Prior_day_low_sweep_then_the_first_fvg_gives_the_hypothetical_signal(PriorDayEntryTiming timing, int entry)
    {
        using var model = Model(Monday, ChartPeriod.H4, null, PriorDayBars());
        var study = Add(model, new PriorDayFvg { Entry = timing });
        var low = Assert.Single(study.Scene.Lines, l => l.Label == "Prior-day low");
        Assert.Equal((6, 12, 90m, 13), (low.From, low.Available, low.Price, low.To));
        var high = Assert.Single(study.Scene.Lines, l => l.Label == "Prior-day high");
        Assert.Equal((6, 12, 110m), (high.From, high.Available, high.Price)); Assert.True(high.Live);
        Assert.Equal(1m, Series(study, "PriorDayFvg.Sweep")[13]);
        var gap = Assert.Single(study.Scene.Boxes, b => b.Alert == "Fair value gap" && b.From == 15);
        Assert.Equal((16, 103m, 99m), (gap.Available, gap.Top, gap.Bottom));
        var signal = Series(study, "PriorDayFvg.Signal");
        for (int bar = 0; bar < entry; bar++) Assert.Equal(0m, signal[bar]);
        Assert.Equal(1m, signal[entry]);
        Assert.Contains(study.Scene.Marks, m => m.Bar == entry && m.Available == entry && m.Text == "Hypothetical long");
        var stop = Assert.Single(study.Scene.Lines, l => l.Alert == "Hypothetical stop");
        Assert.Equal(entry, stop.Available);
        var stops = Series(study, "PriorDayFvg.Stop"); var targets = Series(study, "PriorDayFvg.Target");
        Assert.True(stops.IsAvailable(entry)); Assert.False(stops.IsAvailable(entry - 1));
        Assert.Equal(stop.Price, stops[entry]);
        // entry at the close (108) when the gap forms, or at the gap midpoint (101); target 3 ATR vs stop 1.5 ATR
        decimal price = timing == PriorDayEntryTiming.OnFormation ? 108m : 101m;
        Assert.True(Math.Abs(targets[entry] - price - 2 * (price - stops[entry])) < 0.0001m);
    }

    // equal highs at bars 2 and 6 (20), confirmed at the closes of bars 4 and 8; bar 9 closes through with a large body and
    // bar 10 leaves a bullish FVG over bar 8's high
    private static readonly Ohlc[] EqualHighs =
    {
        new(10, 12, 9, 11), new(11, 15, 10, 14), new(14, 20, 13, 15), new(15, 16, 12, 13), new(13, 14, 11, 12),
        new(12, 17, 11, 16), new(16, 20, 15, 17), new(17, 18, 14, 15), new(15, 16, 13, 14), new(17, 26, 17, 25), new(25, 28, 22, 27), new(27, 28, 26, 27),
    };

    [Fact]
    public void Equal_highs_zone_needs_both_swings_confirmed_and_breaks_with_an_fvg()
    {
        using (var early = Model(EqualHighs.Take(9).ToArray())) // bar 8 is still forming: the second swing is unconfirmed
            Assert.DoesNotContain(Add(early, new EqualHighsLowsFvg { Left = 2, Right = 2 }).Scene.Boxes, b => b.Alert == "Equal highs");

        using var model = Model(EqualHighs);
        var study = Add(model, new EqualHighsLowsFvg { Left = 2, Right = 2 });
        var zone = Assert.Single(study.Scene.Boxes, b => b.Alert == "Equal highs");
        Assert.Equal((2, 8, 20m), (zone.From, zone.Available, zone.Bottom)); Assert.True(zone.Top > 20m);
        Assert.Equal(9, zone.To);
        Assert.Equal(1m, Series(study, "EqualHighsLowsFvg.Zone")[8]);
        var gap = Assert.Single(study.Scene.Boxes, b => b.Alert == "Breakout gap");
        Assert.Equal((9, 10, 22m, 16m), (gap.From, gap.Available, gap.Top, gap.Bottom));
        Assert.Equal(0m, Series(study, "EqualHighsLowsFvg.Signal")[9]);
        Assert.Equal(1m, Series(study, "EqualHighsLowsFvg.Signal")[10]);
        Assert.Contains(study.Scene.Marks, m => m.Bar == 10 && m.Available == 10 && m.Text == "Breakout");
    }

    [Fact]
    public void Gap_fills_progressively_and_its_structural_level_breaks()
    {
        using var model = Model(new(10, 12, 9, 11), new(11, 20, 11, 19), new(19, 22, 16, 21), new(21, 22, 14, 15), new(16, 19, 15, 18),
            new(18, 18, 13, 14), new(14, 15, 11, 12), new(12, 13, 11, 12));
        var study = Add(model, new GapFillBreakouts { AtrMult = 0.5m, PivotLength = 1 });
        var gaps = study.Scene.Boxes.Where(b => b.Alert == "Unfilled gap").Select(b => (b.From, b.Available, b.Top, b.Bottom, b.To)).ToList();
        Assert.Equal(new[] { (1, 2, 16m, 12m, 3), (3, 3, 14m, 12m, 5), (5, 5, 13m, 12m, 6) }, gaps);
        var level = Assert.Single(study.Scene.Lines, l => l.Alert == "Gap structure level");
        Assert.Equal((3, 4, 15m, 5), (level.From, level.Available, level.Price, level.To));
        var tag = Assert.Single(study.Scene.Tags);
        Assert.Equal(("Bearish break", 5, 5), (tag.Text, tag.Bar, tag.Available));
        Assert.Equal(-1m, Series(study, "GapFillBreakouts.Signal")[5]);
        Assert.Equal(0m, Series(study, "GapFillBreakouts.Signal")[4]);
    }

    [Fact]
    public void Breakout_zone_is_tested_broken_and_retested_on_opens_and_closes()
    {
        using var model = Model(new(20, 21, 18, 19), new(19, 20, 16, 17), new(17, 18, 10, 12), new(12, 15, 11, 14), new(13, 17, 12, 16),
            new(15, 16, 8, 9), new(11, 12, 7, 9), new(9, 10, 8, 9));
        var study = Add(model, new BreakoutTestsRetests { Sides = BreakoutZoneSides.Bullish, Left = 2, Right = 1, Width = 1 });
        var atr = new RunningAtr(200);
        for (int i = 0; i <= 3; i++) atr.Add(study.Bars[i]);
        var support = Assert.Single(study.Scene.Boxes, b => b.Alert == "Support zone");
        Assert.Equal((2, 3, 10m, 10m + atr.Value, 5), (support.From, support.Available, support.Bottom, support.Top, support.To));
        var broken = Assert.Single(study.Scene.Boxes, b => b.Alert == "Broken support zone");
        Assert.Equal((5, 5, "Broken"), (broken.From, broken.Available, broken.Text)); Assert.True(broken.Live);
        Assert.Contains(study.Scene.Marks, m => m.Bar == 2 && m.Available == 3 && m.Kind == StudyMarkKind.Circle);
        Assert.Contains(study.Scene.Marks, m => m.Bar == 4 && m.Available == 4 && m.Kind == StudyMarkKind.Square && m.Text == "Test");
        Assert.Contains(study.Scene.Marks, m => m.Bar == 5 && m.Available == 5 && m.Kind == StudyMarkKind.TriangleDown);
        Assert.Contains(study.Scene.Marks, m => m.Bar == 6 && m.Available == 6 && m.Kind == StudyMarkKind.Diamond && m.Text == "Retest");
        Assert.Equal(new decimal[] { 0, 0, 0, 0, 1, 0, 0 }, Enumerable.Range(0, 7).Select(i => Series(study, "BreakoutTestsRetests.Test")[i]));
        Assert.Equal(new decimal[] { 0, 0, 0, 0, 0, -1, 0 }, Enumerable.Range(0, 7).Select(i => Series(study, "BreakoutTestsRetests.Signal")[i]));
        Assert.Equal(new decimal[] { 0, 0, 0, 0, 0, 0, -1 }, Enumerable.Range(0, 7).Select(i => Series(study, "BreakoutTestsRetests.Retest")[i]));
    }

    public static TheoryData<string> Studies => new() { "sweeps", "session", "priorday", "equal", "gapfill", "breakout" };

    private static ClosedBarStudy Create(string name) => name switch
    {
        "sweeps" => new LiquiditySweeps { SwingLength = 3, MaximumObjects = 5000 },
        "session" => new SessionSweepInverseFvg { SweepBars = 6, MaximumObjects = 5000 },
        "priorday" => new PriorDayFvg { Entry = PriorDayEntryTiming.AtMidpoint, TrendFilter = true, TrendEma = 50, MaximumObjects = 5000 },
        "equal" => new EqualHighsLowsFvg { Left = 4, Right = 2, ThresholdPct = 0.1m, MaximumObjects = 5000 },
        "gapfill" => new GapFillBreakouts { AtrMult = 0.3m, PivotLength = 2, MaximumObjects = 5000 },
        _ => new BreakoutTestsRetests { Left = 4, Right = 1, Width = 0.5m, MaxBars = 60, MaximumObjects = 5000 },
    };

    [Theory]
    [MemberData(nameof(Studies))]
    public void Sweep_studies_never_redraw_backwards(string name) =>
        AssertPrefixInvariant(() => Create(name), Walk(700, 11), 450, ChartPeriod.M15, Monday);

    [Theory]
    [MemberData(nameof(Studies))]
    public void Sweep_studies_render_with_unique_alert_ids(string name)
    {
        foreach (var period in new[] { ChartPeriod.M5, ChartPeriod.M15, ChartPeriod.H1 })
        {
            using var model = Model(Monday, period, null, Walk(500, 5));
            var study = Add(model, Create(name));
            Render(model);
            var ids = study.GetAlertLevels().Select(l => l.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.NotEmpty(study.Scene.Dash);
        }
    }
}
