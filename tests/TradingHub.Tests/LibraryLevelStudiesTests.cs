using System.Globalization;
using TradingHub.Data;
using TradingHub.Indicators.Native;
using static TradingHub.Tests.LibraryStudyFixture;

namespace TradingHub.Tests;

public class LibraryLevelStudiesTests
{
    [Fact]
    public void Supply_zone_exists_from_the_confirming_close_and_ends_at_the_breaking_close()
    {
        // swing high at bar 2 (radius 2) is confirmed at the close of bar 4; bar 6 closes above the zone top
        using var model = Model(new(10, 11, 9, 10), new(10, 12, 9, 11), new(11, 20, 10, 15), new(15, 16, 12, 13), new(13, 14, 11, 12),
            new(12, 15, 11, 14), new(14, 22, 13, 21), new(21, 22, 20, 21));
        var study = Add(model, new SupplyDemandZones { SwingStrength = 2 });
        var zone = Assert.Single(study.Scene.Boxes, b => b.Alert == "Supply zone");
        Assert.Equal(2, zone.From); Assert.Equal(4, zone.Available); Assert.Equal(20m, zone.Top);
        Assert.Equal(6, zone.To); Assert.Equal(6, zone.EndAvailable);
        Assert.Equal(1m, Series(study, "SupplyDemand.Break")[6]);
        Assert.Equal(0m, Series(study, "SupplyDemand.Break")[5]);
        Assert.Contains(study.Scene.Marks, m => m.Bar == 6 && m.Kind == StudyMarkKind.Diamond);
    }

    [Fact]
    public void Supply_and_demand_never_redraw_backwards() =>
        AssertPrefixInvariant(() => new SupplyDemandZones { SwingStrength = 3 }, Walk(400), 250);

    [Fact]
    public void Live_zones_are_alertable_with_stable_ids()
    {
        using var model = Model(new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), TradingHub.Data.ChartPeriod.M5, null, Walk(300));
        var study = Add(model, new SupplyDemandZones { SwingStrength = 3 });
        var levels = study.GetAlertLevels().ToList();
        Assert.NotEmpty(levels);
        Assert.Equal(levels.Count, levels.Select(l => l.Id).Distinct().Count());
        study.RecalculateValues(); model.Update();
        Assert.Equal(levels.Select(l => l.Id), study.GetAlertLevels().Select(l => l.Id));
    }

    // ------------------------------------------------------------------ calendar key levels (prior close, right-edge labels)

    private static readonly DateTime SundayMay31 = new(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Hourly bars rising 2 per bar: open 1000 + 2i, high +3, low −1, close +1.</summary>
    private static List<Ohlc> Ramp(int count) =>
        Enumerable.Range(0, count).Select(i => { decimal open = 1000 + 2 * i; return new Ohlc(open, open + 3, open - 1, open + 1); }).ToList();

    [Fact]
    public void Key_levels_prior_close_appears_only_after_the_period_completes()
    {
        using var model = Model(SundayMay31, ChartPeriod.H1, null, Ramp(50));
        var study = Add(model, new KeyLevels { ShowPriorClose = true });
        var close = Series(study, "KeyLevels.Day.PriorClose");
        for (int bar = 0; bar < 24; bar++) Assert.False(close.IsAvailable(bar));
        // Sunday's session (bars 0–23) completes at Monday's first bar; its close was bar 23's
        Assert.True(close.IsAvailable(24)); Assert.Equal(1047m, close[24]);
        Assert.Equal(1047m, close[30]);
        // the first loaded week is partial, so it has no prior close
        Assert.False(Series(study, "KeyLevels.Week.PriorClose").IsAvailable(24));
        // existing series keep their ids and order; the prior closes are appended at the end
        Assert.Equal(new[] { "KeyLevels.Monday.High", "KeyLevels.Monday.Low", "KeyLevels.Hours4.PriorClose", "KeyLevels.Day.PriorClose", "KeyLevels.Week.PriorClose",
            "KeyLevels.Month.PriorClose", "KeyLevels.Quarter.PriorClose", "KeyLevels.Year.PriorClose" }, study.DataSeries.Skip(24).Select(s => s.Id).ToArray());
        Assert.Equal("KeyLevels.Hours4.Open", study.DataSeries[0].Id);

        using var plain = Model(SundayMay31, ChartPeriod.H1, null, Ramp(50));
        Assert.False(Series(Add(plain, new KeyLevels()), "KeyLevels.Day.PriorClose").IsAvailable(24));
    }

    [Fact]
    public void Key_level_labels_name_each_level_and_merge_equal_prices()
    {
        using var model = Model(SundayMay31, ChartPeriod.H1, null, Ramp(50));
        var study = Add(model, new KeyLevels { ShowPriorClose = true });
        Assert.Equal(new[] { "P4HH / PDH 1049", "4HO / DO / WO / MO 1048", "P4HC / PDC 1047", "4H EQ 1044", "P4HL 1039", "D EQ 1024", "PDL 999" },
            study.LevelLabels(24).Select(l => l.Text).ToArray());
    }

    [Fact]
    public void Key_levels_never_redraw_backwards() =>
        AssertPrefixInvariant(() => new KeyLevels { ShowPriorClose = true, Quarterly = true }, Walk(700), 450, ChartPeriod.M15);

    // ------------------------------------------------------------------ pivot point ladders

    private static readonly SessionSettings NewYork18 = new() { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Start = TimeSpan.FromHours(18) };
    /// <summary>Thursday 15:00 New York (EST): bars 0–2 are a partial Thursday session.</summary>
    private static readonly DateTime ThursdayAfternoon = new(2026, 3, 6, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Hourly bars: Friday's session is bars 3–26 (from 23:00 UTC), Saturday's bars 27–49 (23 hours: New York enters DST on
    /// Sunday), Sunday's starts at bar 50 = 22:00 UTC. Saturday: open 100, high 120, low 90, close 102; Sunday opens 103.
    /// </summary>
    private static List<Ohlc> DstSessions()
    {
        var bars = Enumerable.Repeat(new Ohlc(100, 101, 99, 100), 53).ToList();
        bars[30] = new(100, 120, 99, 100); bars[40] = new(100, 101, 90, 100); bars[49] = new(100, 102, 99, 102);
        bars[50] = new(103, 125, 85, 103); // a boundary at the standard-time 23:00 UTC would fold this bar into Saturday
        return bars;
    }

    private static List<(string, decimal)> Expected(string text) =>
        text.Split(' ').Select(p => p.Split('=')).Select(p => (p[0], decimal.Parse(p[1], CultureInfo.InvariantCulture))).ToList();

    [Theory]
    [InlineData(PivotLadderMethod.Floor, PivotFloorThird.Classic, "P=104 R1=118 S1=88 R2=134 S2=74 R3=148 S3=58")]
    [InlineData(PivotLadderMethod.Floor, PivotFloorThird.RangeMultiple, "P=104 R1=118 S1=88 R2=134 S2=74 R3=164 S3=44")]
    [InlineData(PivotLadderMethod.Traditional, PivotFloorThird.Classic, "P=104 R1=118 S1=88 R2=134 S2=74 R3=148 S3=58 R4=162 S4=42 R5=176 S5=26")]
    [InlineData(PivotLadderMethod.Fibonacci, PivotFloorThird.Classic, "P=104 R1=115.46 S1=92.54 R2=122.54 S2=85.46 R3=134 S3=74")]
    [InlineData(PivotLadderMethod.Woodie, PivotFloorThird.Classic, "P=104 R1=118 S1=88 R2=134 S2=74 R3=148 S3=58 R4=178 S4=28")]
    [InlineData(PivotLadderMethod.Classic, PivotFloorThird.Classic, "P=104 R1=118 S1=88 R2=134 S2=74 R3=164 S3=44 R4=194 S4=14")]
    [InlineData(PivotLadderMethod.DeMark, PivotFloorThird.Classic, "P=108 R1=126 S1=96")]
    [InlineData(PivotLadderMethod.Camarilla, PivotFloorThird.Classic, "P=104 R1=104.75 S1=99.25 R2=107.5 S2=96.5 R3=110.25 S3=93.75 R4=118.5 S4=85.5 R5=136 S5=68")]
    public void Pivot_ladders_match_their_formulas_and_appear_at_the_next_sessions_first_close_across_DST(PivotLadderMethod method, PivotFloorThird third, string expected)
    {
        // prior period O 100, H 120, L 90, C 102; next period opens at 103 (Woodie)
        Assert.Equal(Expected(expected), PivotPointLadders.Ladder(method, 100, 120, 90, 102, 103, third));

        using var model = Model(ThursdayAfternoon, ChartPeriod.H1, NewYork18, DstSessions());
        var study = Add(model, new PivotPointLadders { Method = method, FloorThird = third, Period = PivotLadderPeriod.Session, PeriodsBack = 60 });
        Assert.Equal(52, study.Bars.Count);
        Assert.Equal(new DateTime(2026, 3, 8, 22, 0, 0, DateTimeKind.Utc), study.Bars[50].Time);
        // the partial Thursday session is skipped; Friday's ladder appears at Saturday's first bar, Saturday's at Sunday's (22:00 UTC, EDT)
        Assert.Equal(new[] { 27, 50 }, study.Scene.Lines.Select(l => l.From).Distinct().ToArray());
        var latest = study.Scene.Lines.Where(l => l.From == 50).ToList();
        Assert.All(latest, l => { Assert.Equal(50, l.Available); Assert.True(l.Live); });
        Assert.Equal(Expected(expected), latest.Select(l => (l.Alert!["Pivot ".Length..], l.Price)).ToList());
        Assert.All(study.Scene.Lines.Where(l => l.From == 27), l => { Assert.Equal(49, l.To); Assert.Equal(50, l.EndAvailable); });
        // only the current period's levels are alertable
        var alerts = study.GetAlertLevels().ToList();
        Assert.Equal(latest.Count, alerts.Count);
        Assert.All(alerts, a => { Assert.Equal(50, a.FromBar); Assert.StartsWith("Pivot ", a.Name); });
    }

    [Fact]
    public void Pivot_crossing_signal_is_written_at_the_crossing_close()
    {
        using var model = Model(ThursdayAfternoon, ChartPeriod.H1, NewYork18, DstSessions());
        var study = Add(model, new PivotPointLadders { Period = PivotLadderPeriod.Session });
        var signal = Series(study, "PivotLadders.Signal");
        // Friday's pivot is 100 (H 101, L 99, C 100); bar 49 closes 102 from 100, R2 = 102 is not passed
        Assert.Equal(0m, signal[48]);
        Assert.Equal(1m, signal[49]);
        Assert.Contains(study.Scene.Marks, m => m.Bar == 49 && m.Available == 49 && m.Kind == StudyMarkKind.TriangleUp);
    }

    [Fact]
    public void Pivot_ladders_never_redraw_backwards()
    {
        AssertPrefixInvariant(() => new PivotPointLadders { Period = PivotLadderPeriod.Session, PeriodsBack = 60, MaximumObjects = 5000 }, Walk(1200), 800, ChartPeriod.M15,
            new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), NewYork18);
        AssertPrefixInvariant(() => new PivotPointLadders { Method = PivotLadderMethod.Camarilla, PeriodsBack = 60, MaximumObjects = 5000 }, Walk(1500), 1000);
    }

    // ------------------------------------------------------------------ line break

    [Fact]
    public void Line_break_reverses_exactly_when_a_close_passes_the_extreme_of_the_last_three_lines()
    {
        using var model = Model(new(100, 102, 99, 101), new(101, 104, 100, 103), new(103, 106, 102, 105), new(105, 106, 103, 104),
            new(104, 105, 99, 100), new(100, 101, 97, 99), new(99, 100, 97, 98), new(98, 99, 97, 98));
        var study = Add(model, new LineBreak());
        Assert.Equal(new[] { (0, 100m, 101m, true), (1, 101m, 103m, true), (2, 103m, 105m, true), (5, 103m, 99m, false), (6, 99m, 98m, false) },
            study.RecentLines.Select(l => (l.From, l.Open, l.Close, l.Up)).ToArray());
        var signal = Series(study, "LineBreak.Signal"); var trend = Series(study, "LineBreak.Trend");
        // bar 4 closes at 100, exactly the lowest bottom of the last three lines: no reversal; bar 5 closes below it
        Assert.Equal(0m, signal[4]); Assert.Equal(1m, trend[4]);
        Assert.Equal(-1m, signal[5]); Assert.Equal(-1m, trend[5]);
        Assert.Contains(study.Scene.Tags, t => t.Bar == 5 && t.Available == 5 && t.Text == "Bearish reversal");
        Assert.Contains(study.Scene.Marks, m => m.Bar == 5 && m.Available == 5 && m.Kind == StudyMarkKind.Circle);
        // current triggers: continue below 98, reverse above the highest top of the last three lines (105)
        var alerts = study.GetAlertLevels().ToList();
        Assert.Contains(alerts, a => a.Name == "Line break continue" && a.Price == 98m && a.FromBar == 6);
        Assert.Contains(alerts, a => a.Name == "Line break reverse" && a.Price == 105m && a.FromBar == 6);
        Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public void Higher_frame_close_is_known_at_the_first_chart_bar_of_the_next_block()
    {
        // M5 bars, 15-minute blocks of three: block closes 101, 103, 105, then 99 (bar 11) after an intrablock dip to 95
        using var model = Model(new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), ChartPeriod.M5, null, new List<Ohlc>
        {
            new(100, 101, 99, 100), new(100, 101, 99, 100), new(100, 102, 99, 101),
            new(101, 102, 100, 101), new(101, 102, 100, 101), new(101, 104, 100, 103),
            new(103, 104, 102, 103), new(103, 104, 102, 103), new(103, 106, 102, 105),
            new(105, 106, 104, 105), new(105, 105, 94, 95), new(95, 100, 94, 99),
            new(99, 100, 98, 99), new(99, 100, 98, 99),
        });
        var study = Add(model, new LineBreak { CloseSource = LineBreakCloses.Minutes15 });
        Assert.Equal(new[] { (2, 3, 100m, 101m), (5, 6, 101m, 103m), (8, 9, 103m, 105m), (11, 12, 103m, 99m) },
            study.RecentLines.Select(l => (l.From, l.Available, l.Open, l.Close)).ToArray());
        var signal = Series(study, "LineBreak.Signal"); var trend = Series(study, "LineBreak.Trend");
        Assert.False(trend.IsAvailable(2)); Assert.True(trend.IsAvailable(3));
        Assert.Equal(0m, signal[10]); Assert.Equal(0m, signal[11]); Assert.Equal(1m, trend[11]);
        Assert.Equal(-1m, signal[12]); Assert.Equal(-1m, trend[12]);
        Assert.Contains(study.Scene.Marks, m => m.Bar == 11 && m.Available == 12 && m.Kind == StudyMarkKind.Circle);
    }

    [Fact]
    public void Line_break_never_redraws_backwards()
    {
        AssertPrefixInvariant(() => new LineBreak { PaintBars = true, ExtendedRun = 4, MaximumObjects = 5000 }, Walk(600), 400);
        AssertPrefixInvariant(() => new LineBreak { CloseSource = LineBreakCloses.Hour, MaximumObjects = 5000 }, Walk(900), 600);
        AssertPrefixInvariant(() => new LineBreak { CloseSource = LineBreakCloses.Session, LineCount = 2, MaximumObjects = 5000 }, Walk(1200), 800, ChartPeriod.M15,
            new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), NewYork18);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Level_studies_render(int period)
    {
        using var model = Model(new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), new[] { ChartPeriod.M5, ChartPeriod.M15, ChartPeriod.H1 }[period], NewYork18, Walk(600));
        Add(model, new KeyLevels { ShowPriorClose = true });
        Add(model, new PivotPointLadders { Method = PivotLadderMethod.Camarilla });
        Add(model, new LineBreak { PaintBars = true, CloseSource = LineBreakCloses.Hour });
        Render(model);
    }

    [Fact]
    public void Cme_sunday_evening_session_opens_the_new_week_and_counts_in_the_monday_range()
    {
        // hourly bars from Friday 2026-03-06 19:00 UTC; Sunday 2026-03-08 18:00 New York (22:00 UTC, after the DST change) is bar 51
        var cme = new SessionSettings { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Start = TimeSpan.FromHours(18) };
        var bars = Enumerable.Range(0, 80).Select(i => new Ohlc(1000 + i, i == 51 ? 2000 : 1002 + i, 998 + i, 1001 + i)).ToList();
        using var model = Model(new DateTime(2026, 3, 6, 19, 0, 0, DateTimeKind.Utc), ChartPeriod.H1, cme, bars);
        var study = Add(model, new KeyLevels());
        Assert.True(model.IsNewWeek(51)); Assert.False(model.IsNewWeek(50)); Assert.False(model.IsNewWeek(52));
        var weekOpen = Series(study, "KeyLevels.Week.Open");
        Assert.True(weekOpen.IsAvailable(51)); Assert.Equal(1051m, weekOpen[51]); Assert.Equal(1051m, weekOpen[60]);
        // the Monday trading day runs from Sunday 22:00 UTC to Monday 22:00 UTC (bars 51..74): its range includes the Sunday spike
        var mondayHigh = Series(study, "KeyLevels.Monday.High");
        Assert.False(mondayHigh.IsAvailable(74)); Assert.True(mondayHigh.IsAvailable(75)); Assert.Equal(2000m, mondayHigh[75]);
    }

    // ------------------------------------------------------------------ display-only filters

    private static PivotPointLadders Ladders(string? hide = null)
    {
        var study = new PivotPointLadders { Period = PivotLadderPeriod.Session, PeriodsBack = 60 };
        switch (hide)
        {
            case nameof(PivotPointLadders.ShowP): study.ShowP = false; break;
            case nameof(PivotPointLadders.Tier1): study.Tier1 = false; break;
            case nameof(PivotPointLadders.Tier2): study.Tier2 = false; break;
            case nameof(PivotPointLadders.Tier3): study.Tier3 = false; break;
            case nameof(PivotPointLadders.Tier4): study.Tier4 = false; break;
            case nameof(PivotPointLadders.Tier5): study.Tier5 = false; break;
            case nameof(PivotPointLadders.MaxTier): study.MaxTier = 2; break;
            case nameof(PivotPointLadders.ShowCrosses): study.ShowCrosses = false; break;
        }
        return study;
    }

    [Theory]
    [InlineData(nameof(PivotPointLadders.ShowP), 0)]
    [InlineData(nameof(PivotPointLadders.Tier1), 1)]
    [InlineData(nameof(PivotPointLadders.Tier2), 2)]
    [InlineData(nameof(PivotPointLadders.Tier3), 3)]
    [InlineData(nameof(PivotPointLadders.Tier4), 4)]
    [InlineData(nameof(PivotPointLadders.Tier5), 5)]
    [InlineData(nameof(PivotPointLadders.MaxTier), -1)]
    [InlineData(nameof(PivotPointLadders.ShowCrosses), -1)]
    public void Pivot_level_toggles_only_hide_drawings(string option, int hiddenTier)
    {
        var study = AssertDisplayOnly(() => Ladders(), () => Ladders(option), DstSessions(), ChartPeriod.H1, ThursdayAfternoon, NewYork18);
        // every tier of the current (Traditional) ladder stays alertable: P and R1–R5/S1–S5
        Assert.Equal(11, study.GetAlertLevels().Count());
        Assert.NotEmpty(study.Scene.Lines);
        Assert.All(study.Scene.Lines, l =>
        {
            int tier = l.Alert![^1] == 'P' ? 0 : l.Alert[^1] - '0';
            bool shown = option switch { nameof(PivotPointLadders.MaxTier) => tier <= 2, nameof(PivotPointLadders.ShowCrosses) => true, _ => tier != hiddenTier };
            Assert.Equal(shown, study.IsShown(l));
        });
        Assert.NotEmpty(study.Scene.Marks);
        Assert.All(study.Scene.Marks, m => Assert.Equal(option != nameof(PivotPointLadders.ShowCrosses), study.IsShown(m)));
    }

    private static LineBreak Breaks(string? hide = null)
    {
        var study = new LineBreak { ExtendedRun = 4, MaximumObjects = 5000 };
        switch (hide)
        {
            case nameof(LineBreak.ShowLines): study.ShowLines = false; break;
            case nameof(LineBreak.ShowMarkers): study.ShowMarkers = false; break;
            case nameof(LineBreak.ShowReversalLabels): study.ShowReversalLabels = false; break;
            case nameof(LineBreak.ShowTriggers): study.ShowTriggers = false; break;
        }
        return study;
    }

    [Theory]
    [InlineData(nameof(LineBreak.ShowLines))]
    [InlineData(nameof(LineBreak.ShowMarkers))]
    [InlineData(nameof(LineBreak.ShowReversalLabels))]
    [InlineData(nameof(LineBreak.ShowTriggers))]
    public void Line_break_drawing_toggles_only_hide_drawings(string option)
    {
        var study = AssertDisplayOnly(() => Breaks(), () => Breaks(option), Walk(600));
        // the current continue and reverse triggers stay alertable even when hidden
        Assert.Equal(new[] { "Line break continue", "Line break reverse" }, study.GetAlertLevels().Select(a => a.Name).OrderBy(n => n).ToArray());
        Assert.NotEmpty(study.Scene.Boxes); Assert.NotEmpty(study.Scene.Lines); Assert.NotEmpty(study.Scene.Marks); Assert.NotEmpty(study.Scene.Tags);
        Assert.Contains(study.Scene.Marks, m => m.Kind == StudyMarkKind.TriangleUp || m.Kind == StudyMarkKind.TriangleDown);
        Assert.All(study.Scene.Boxes, b => Assert.Equal(option != nameof(LineBreak.ShowLines), study.IsShown(b)));
        Assert.All(study.Scene.Lines, l => Assert.Equal(option != nameof(LineBreak.ShowTriggers), study.IsShown(l)));
        Assert.All(study.Scene.Marks, m => Assert.Equal(option != nameof(LineBreak.ShowMarkers), study.IsShown(m)));
        Assert.All(study.Scene.Tags, t => Assert.Equal(option != nameof(LineBreak.ShowReversalLabels), study.IsShown(t)));
    }
}
