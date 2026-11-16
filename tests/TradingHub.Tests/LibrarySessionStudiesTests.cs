using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using static TradingHub.Tests.LibraryStudyFixture;

namespace TradingHub.Tests;

public class LibrarySessionStudiesTests
{
    private static readonly DateTime March2 = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A chart of bars at explicit UTC opening times (tick 1); the last bar is forming.</summary>
    private static ChartModel At(ChartPeriod period, SessionSettings? session, IReadOnlyList<(DateTime Time, Ohlc Bar)> bars)
    {
        var instrument = new Instrument { Symbol = "LIBRARY-SESSION-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 };
        IMarketDataFeed feed = session == null ? new SimulatedFeed() : new SessionFeed(session);
        var data = new InstrumentData(instrument, feed); long id = 1;
        foreach (var (time, bar) in bars)
        {
            data.Trades.Add(new Trade(id++, time, bar.Open, bar.Volume / 4, TradeSide.Buy));
            data.Trades.Add(new Trade(id++, time.AddSeconds(1), bar.High, bar.Volume / 4, TradeSide.Buy));
            data.Trades.Add(new Trade(id++, time.AddSeconds(2), bar.Low, bar.Volume / 4, TradeSide.Sell));
            data.Trades.Add(new Trade(id++, time.AddSeconds(3), bar.Close, bar.Volume / 4, TradeSide.Sell));
        }
        return new ChartModel(data, period, new ChartSettings());
    }

    /// <summary>A walk whose price jumps at every <paramref name="every"/>-th bar, so session opens gap from the prior close.</summary>
    private static List<Ohlc> Gappy(IReadOnlyList<Ohlc> bars, int every, int seed = 11)
    {
        var random = new Random(seed); decimal offset = 0; var list = new List<Ohlc>(bars.Count);
        for (int i = 0; i < bars.Count; i++)
        {
            if (i > 0 && i % every == 0) offset += random.Next(-12, 13);
            var x = bars[i]; list.Add(new(x.Open + offset, x.High + offset, x.Low + offset, x.Close + offset, x.Volume));
        }
        return list;
    }

    private static StudyBox SessionBox(ClosedBarStudy study, string name, int day) =>
        Assert.Single(study.Scene.Boxes, b => b.Text == name && study.Bars[b.From].Time.Date == new DateTime(2026, 3, day));

    [Fact]
    public void London_and_New_York_open_on_the_right_utc_bar_across_the_us_and_uk_dst_changes()
    {
        // hourly bars from Thursday 5 March to Tuesday 31 March 2026 (US DST on 8 March, UK on 29 March)
        using var model = Model(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc), ChartPeriod.H1, null, Walk(26 * 24 + 1));
        var study = Add(model, new MarketSessions { MaximumObjects = 2000 });
        TimeSpan Open(string name, int day) => study.Bars[SessionBox(study, name, day).From].Time.TimeOfDay;

        Assert.Equal(TimeSpan.FromHours(8), Open("London", 6));
        Assert.Equal(TimeSpan.FromHours(8), Open("London", 9));
        Assert.Equal(TimeSpan.FromHours(8), Open("London", 27));
        Assert.Equal(TimeSpan.FromHours(7), Open("London", 30));
        Assert.Equal(TimeSpan.FromHours(13), Open("New York", 6));
        Assert.Equal(TimeSpan.FromHours(12), Open("New York", 9));
        Assert.Equal(TimeSpan.FromHours(12), Open("New York", 27));
        Assert.Equal(TimeSpan.FromHours(12), Open("New York", 30));

        // the last bar of a session is the one whose close reaches the scheduled end, and the box is final at that close
        var london30 = SessionBox(study, "London", 30);
        Assert.Equal(new TimeSpan(15, 0, 0), study.Bars[london30.Available].Time.TimeOfDay);
        Assert.Equal(london30.Available, london30.To);
        var newYork6 = SessionBox(study, "New York", 6);
        Assert.Equal(new TimeSpan(21, 0, 0), study.Bars[newYork6.Available].Time.TimeOfDay);
    }

    [Fact]
    public void Weekend_and_holiday_days_form_no_session()
    {
        // Monday 2, (Tuesday 3 holiday: no bars), Wednesday 4, Saturday 7 (bars present), Monday 9 March 2026
        var days = new[] { 2, 4, 7, 9 }; var walk = Walk(days.Length * 24 + 1); var bars = new List<(DateTime, Ohlc)>();
        foreach (var day in days)
            for (int h = 0; h < 24; h++) bars.Add((new DateTime(2026, 3, day, h, 0, 0, DateTimeKind.Utc), walk[bars.Count]));
        bars.Add((new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), walk[bars.Count]));
        using var model = At(ChartPeriod.H1, null, bars);
        var study = Add(model, new MarketSessions());

        Assert.DoesNotContain(study.Scene.Boxes, b => study.Bars[b.From].Time.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        foreach (var name in new[] { "Tokyo", "London", "New York" })
            Assert.Equal(new[] { 2, 4, 9 }, study.Scene.Boxes.Where(b => b.Text == name).Select(b => study.Bars[b.From].Time.Day).ToArray());
        Assert.Empty(study.Forming);
    }

    [Fact]
    public void Session_high_and_low_are_final_only_at_the_close_of_the_last_session_bar()
    {
        // M15 from Monday 2 March 2026 00:00 UTC; London (GMT) = bars 32 (08:00) .. 67 (16:45)
        var bars = Walk(80);
        decimal High(int last) => bars.Skip(32).Take(last - 31).Max(x => x.High);
        decimal Low(int last) => bars.Skip(32).Take(last - 31).Min(x => x.Low);
        MarketSessions LondonOnly() => new() { S1Show = false, S3Show = false };

        using (var early = Model(March2, ChartPeriod.M15, null, bars.Take(49).ToList())) // closed through 47 (11:45)
        {
            var study = Add(early, LondonOnly());
            Assert.DoesNotContain(study.Scene.Boxes, b => b.Text == "London");
            var forming = Assert.Single(study.Forming);
            Assert.Equal((WorldSession.London, 32, 47, 67), (forming.Session, forming.From, forming.Last, forming.ScheduledLast));
            Assert.Equal((High(47), Low(47)), (forming.High, forming.Low));
            Assert.DoesNotContain(study.GetAlertLevels(), l => l.Name.StartsWith("London", StringComparison.Ordinal));
        }
        using (var before = Model(March2, ChartPeriod.M15, null, bars.Take(68).ToList())) // closed through 66 (16:30)
        {
            var study = Add(before, LondonOnly());
            Assert.DoesNotContain(study.Scene.Boxes, b => b.Text == "London");
            Assert.Equal(66, Assert.Single(study.Forming).Last);
        }
        using var at = Model(March2, ChartPeriod.M15, null, bars.Take(69).ToList()); // closed through 67 (16:45)
        var done = Add(at, LondonOnly());
        var box = Assert.Single(done.Scene.Boxes, b => b.Text == "London");
        Assert.Equal((32, 67, 67, High(67), Low(67)), (box.From, box.Available, box.To, box.Top, box.Bottom));
        Assert.Empty(done.Forming);
        Assert.Equal(2m, Series(done, "MarketSessions.Completed")[67]);
        Assert.Equal(0m, Series(done, "MarketSessions.Completed")[66]);
        var high = Assert.Single(done.Scene.Lines, l => l.Alert == "London high");
        Assert.Equal((32, 67, High(67)), (high.From, high.Available, high.Price));
        Assert.Contains(done.GetAlertLevels(), l => l.Name == "London low" && l.Price == Low(67));
    }

    [Fact]
    public void Asian_sweep_and_internal_choch_give_the_entry_bar()
    {
        // H1 from 00:00 UTC = 19:00 New York (EST): bars 0..8 are the Asian session (final at 08:00 UTC, 03:00 New York)
        var bars = Enumerable.Repeat(new Ohlc(100, 110, 90, 100), 9).ToList();
        bars.AddRange(new Ohlc[]
        {
            new(100, 112, 99, 105), // 9: sweeps the Asian high (previous high 110 was inside)
            new(105, 106, 101, 103), // 10: confirms the internal swing high at 9
            new(103, 104, 100, 102), // 11: internal swing low 100
            new(102, 105, 101, 104), // 12: confirms it
            new(104, 104, 96, 97), // 13: closes below 100 against the sweep: short entry
            new(97, 98, 96, 97), // 14: forming
        });
        using var model = Model(March2, ChartPeriod.H1, null, bars);
        var study = Add(model, new AsianSessionSweeps { TrendFilter = false, InternalLength = 1 });

        var range = Assert.Single(study.Scene.Boxes, b => b.Text == "Asia");
        Assert.Equal((0, 8, 8, 110m, 90m), (range.From, range.Available, range.To, range.Top, range.Bottom));
        var sweep = Assert.Single(study.Scene.Tags, t => t.Text == "Asia sweep");
        Assert.Equal((9, 9, 112m), (sweep.Bar, sweep.Available, sweep.Price));
        Assert.Contains(study.Scene.Lines, l => l.From == 11 && l.To == 13 && l.Available == 13 && l.Price == 100 && l.EndPrice == 100);
        var entry = Assert.Single(study.Scene.Marks, m => m.Text == "Sell");
        Assert.Equal((13, 13, StudyMarkKind.TriangleDown), (entry.Bar, entry.Available, entry.Kind));
        Assert.Equal(-1m, Series(study, "AsianSessionSweeps.Sweep")[9]);
        Assert.Equal(0m, Series(study, "AsianSessionSweeps.Signal")[12]);
        Assert.Equal(-1m, Series(study, "AsianSessionSweeps.Signal")[13]);
        Assert.Equal(-1m, Series(study, "AsianSessionSweeps.Position")[13]);
        // stop at the sweep extreme (112), target two risks away (97 - 2 × 15)
        Assert.Contains(study.Scene.Lines, l => l.Alert == "Stop" && l.Price == 112 && l.From == 13 && l.Available == 13 && l.Live);
        Assert.Contains(study.Scene.Lines, l => l.Alert == "Target" && l.Price == 67 && l.Live);
    }

    [Fact]
    public void Session_sweep_zone_builds_stalls_and_re_arms_then_breaks()
    {
        // M5 from Monday 2 March 2026 08:00 UTC: London (GMT) = bars 0..107, defended high 110 from the close of bar 107
        var bars = Enumerable.Repeat(new Ohlc(100, 110, 90, 100), 108).ToList();
        bars.Add(new(105, 115, 95, 106)); // 108: trades through 110 after opening below: zone 110..120 (0.5 × ATR 20)
        bars.AddRange(Enumerable.Repeat(new Ohlc(100, 110, 90, 100), 6)); // 109..114: neither inside nor decisive: stalls at 114
        bars.Add(new(105, 115, 95, 106)); // 115: re-armed, builds again
        bars.Add(new(106, 130, 105, 128)); // 116: closes beyond 110 + 1.5 × 10: break
        bars.Add(new(128, 129, 127, 128)); // 117: forming
        using var model = Model(new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc), ChartPeriod.M5, null, bars);
        var study = Add(model, new SessionSweeps { AsiaSession = false, NewYorkMorning = false, NewYorkAfternoon = false, BuysideMargin = 0.5m });

        var london = Assert.Single(study.Scene.Boxes, b => b.Text == null);
        Assert.Equal((0, 107, 107, 110m, 90m), (london.From, london.Available, london.To, london.Top, london.Bottom));
        var zones = study.Scene.Boxes.Where(b => b.Text == "Sweep").ToList();
        Assert.Equal(2, zones.Count);
        Assert.Equal((108, 108, 120m, 110m, 109, 114), (zones[0].From, zones[0].Available, zones[0].Top, zones[0].Bottom, zones[0].To, zones[0].EndAvailable));
        Assert.Equal((115, 115, 120m, 110m, 116, 116), (zones[1].From, zones[1].Available, zones[1].Top, zones[1].Bottom, zones[1].To, zones[1].EndAvailable));
        Assert.Contains(study.Scene.Tags, t => t.Text == "Break" && t.Bar == 116 && t.Available == 116);
        var signal = Series(study, "SessionSweeps.Signal"); var broke = Series(study, "SessionSweeps.Break");
        Assert.Equal((-1m, 0m, -1m), (signal[108], signal[114], signal[115]));
        Assert.Equal((0m, 0m, 1m), (broke[114], broke[115], broke[116]));
        Assert.Contains(study.Scene.Lines, l => l.Alert == "Defended high" && l.Price == 110 && l.Available == 107 && l.Live);
        Assert.DoesNotContain(study.GetAlertLevels(), l => l.Name.Contains("sweep zone", StringComparison.Ordinal));
    }

    [Fact]
    public void Session_gap_fill_bar_and_statistics()
    {
        var utc = new SessionSettings();
        var bars = new List<(DateTime, Ohlc)>
        {
            (new DateTime(2026, 3, 2, 20, 0, 0, DateTimeKind.Utc), new(100, 101, 99, 100)),
            (new DateTime(2026, 3, 2, 21, 0, 0, DateTimeKind.Utc), new(100, 101, 99, 100)),
            (new DateTime(2026, 3, 2, 22, 0, 0, DateTimeKind.Utc), new(100, 101, 99, 100)),
            (new DateTime(2026, 3, 2, 23, 0, 0, DateTimeKind.Utc), new(100, 102, 99, 100)),
            (new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc), new(105, 108, 103, 104)), // 4: clean gap up from 100
            (new DateTime(2026, 3, 3, 1, 0, 0, DateTimeKind.Utc), new(104, 106, 101, 102)),
            (new DateTime(2026, 3, 3, 2, 0, 0, DateTimeKind.Utc), new(102, 103, 99, 100)), // 6: fills (low 99 <= 100)
            (new DateTime(2026, 3, 3, 3, 0, 0, DateTimeKind.Utc), new(100, 111, 99, 110)), // 7: excursion 111
            (new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), new(110, 112, 109, 111)), // 8: no gap; the 3 March gap is counted
            (new DateTime(2026, 3, 4, 1, 0, 0, DateTimeKind.Utc), new(111, 112, 110, 111)), // 9: forming
        };
        using var model = At(ChartPeriod.H1, utc, bars);
        var study = Add(model, new SessionGapFill());

        var gap = Assert.Single(study.Scene.Boxes);
        Assert.Equal(("Gap", 4, 4, 105m, 100m, 6, 6), (gap.Text, gap.From, gap.Available, gap.Top, gap.Bottom, gap.To, gap.EndAvailable));
        var fill = Assert.Single(study.Scene.Marks);
        Assert.Equal((6, 6, 100m, "Filled", StudyMarkKind.TriangleDown), (fill.Bar, fill.Available, fill.Price, fill.Text, fill.Kind));
        Assert.Equal((1m, 0m), (Series(study, "SessionGapFill.Gap")[4], Series(study, "SessionGapFill.Gap")[8]));
        Assert.Equal((0m, 1m), (Series(study, "SessionGapFill.Filled")[5], Series(study, "SessionGapFill.Filled")[6]));
        Assert.Contains(study.Scene.Lines, l => l.Price == 111 && l.From == 4 && l.To == 7 && l.Available == 8);
        var close = Assert.Single(study.Scene.Lines, l => l.Alert == "Prior session close");
        Assert.Equal((100m, 4, 7, 8), (close.Price, close.From, close.To, close.EndAvailable));
        string Row(string name) => study.Scene.Dash.Single(r => r.Name == name).Value;
        Assert.Equal("100% (1/1)", Row("Bullish gaps filled"));
        Assert.Equal("0% (0/1)", Row("Bullish gaps reversed"));
        Assert.Equal("—", Row("Bearish gaps filled"));
        Assert.Equal("2", Row("Average bars to fill"));
    }

    [Fact]
    public void Session_gap_is_alertable_while_unfilled()
    {
        var bars = new List<(DateTime, Ohlc)>
        {
            (new DateTime(2026, 3, 2, 23, 0, 0, DateTimeKind.Utc), new(100, 102, 99, 100)),
            (new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc), new(95, 97, 94, 96)), // 1: clean gap down
            (new DateTime(2026, 3, 3, 1, 0, 0, DateTimeKind.Utc), new(96, 98, 93, 94)),
            (new DateTime(2026, 3, 3, 2, 0, 0, DateTimeKind.Utc), new(94, 95, 93, 94)), // forming
        };
        using var model = At(ChartPeriod.H1, new SessionSettings(), bars);
        var study = Add(model, new SessionGapFill());
        Assert.Equal(-1m, Series(study, "SessionGapFill.Gap")[1]);
        var names = study.GetAlertLevels().Select(l => l.Name).ToList();
        Assert.Contains("Session gap: top edge", names);
        Assert.Contains("Prior session close", names);
        Assert.Equal("Bearish · open", study.Scene.Dash.Single(r => r.Name == "Current gap").Value);
    }

    [Fact]
    public void Market_sessions_never_redraw_backwards() =>
        AssertPrefixInvariant(() => new MarketSessions { S1Vwap = true, S2Vwap = true, S3Vwap = true, VwapBands = true, Midline = true, HighlightOverlaps = true, MaximumObjects = 5000 }, Walk(1500), 900);

    [Fact]
    public void Session_map_never_redraws_backwards() =>
        AssertPrefixInvariant(() => new SessionMap { S2Vwap = true, MaximumObjects = 5000 }, Walk(1500, 3), 1000, ChartPeriod.M5);

    [Fact]
    public void Asian_session_sweeps_never_redraw_backwards() =>
        AssertPrefixInvariant(() => new AsianSessionSweeps { TrendFilter = false, InternalLength = 2, MaximumObjects = 5000 }, Walk(2000, 5), 1300, ChartPeriod.M5);

    [Fact]
    public void Session_sweeps_never_redraw_backwards() =>
        AssertPrefixInvariant(() => new SessionSweeps { ShowMss = true, ShowFvg = true, FvgFilter = 0, BuysideMargin = 0.3m, SellsideMargin = 0.3m, MaximumObjects = 5000 }, Walk(1500, 9), 950, ChartPeriod.M5);

    [Fact]
    public void Session_gap_fill_never_redraws_backwards() =>
        AssertPrefixInvariant(() => new SessionGapFill { MaximumObjects = 5000 }, Gappy(Walk(400), 24), 250, ChartPeriod.H1, session: new SessionSettings());

    [Fact]
    public void Session_studies_render_on_m5_m15_and_h1()
    {
        var studies = new Func<ClosedBarStudy>[]
        {
            () => new MarketSessions { S2Vwap = true, VwapBands = true, Midline = true, ShowDashboard = true },
            () => new SessionMap(),
            () => new AsianSessionSweeps { TrendFilter = false },
            () => new SessionSweeps { ShowMss = true, ShowFvg = true },
            () => new SessionGapFill(),
        };
        foreach (var period in new[] { ChartPeriod.M5, ChartPeriod.M15, ChartPeriod.H1 })
            foreach (var create in studies)
            {
                using var model = Model(March2, period, new SessionSettings(), Gappy(Walk(600), 24));
                var study = Add(model, create());
                Assert.NotEmpty(study.Bars);
                Render(model);
            }
    }

    private sealed class BorderProbe : MarketSessions
    {
        public int Border(StudyBox box) => BoxBorderAlpha(box);
    }

    [Fact]
    public void Market_sessions_days_kept_hides_only_older_sessions()
    {
        Assert.Equal(10, new MarketSessions().DaysKept);
        Assert.Equal(5, new SessionMap().DaysKept);
        // H1 Monday 2 .. Friday 6 March 2026: the latest session date is 6 March
        var study = AssertDisplayOnly(() => new MarketSessions { S1Vwap = true, Midline = true }, () => new MarketSessions { S1Vwap = true, Midline = true, DaysKept = 1 }, Walk(5 * 24 + 1), ChartPeriod.H1);
        Assert.Contains(study.Scene.Boxes, b => !study.IsShown(b));
        Assert.All(study.Scene.Boxes, b => Assert.Equal(study.Bars[b.From].Time.Day == 6, study.IsShown(b)));
        Assert.All(study.Scene.Lines, l => Assert.Equal(l.Live || study.Bars[l.From].Time.Day == 6, study.IsShown(l)));
        Assert.Contains(study.Scene.Lines, l => l.Live && study.IsShown(l));
        Assert.All(study.Scene.Paths, p => Assert.Equal(study.Bars[p.Available].Time.Day == 6, study.IsShown(p)));
        Assert.Contains(study.Scene.Paths, p => !study.IsShown(p));
    }

    [Fact]
    public void Session_map_days_kept_is_display_only()
    {
        var study = AssertDisplayOnly(() => new SessionMap(), () => new SessionMap { DaysKept = 2 }, Walk(5 * 24 + 1), ChartPeriod.H1);
        var boxes = study.Scene.Boxes.Where(b => b.Text is "London" or "New York").ToList();
        Assert.Contains(boxes, b => !study.IsShown(b));
        Assert.All(boxes, b => Assert.Equal(study.Bars[b.From].Time.Day >= 5, study.IsShown(b)));
    }

    [Fact]
    public void Market_sessions_border_opacity_is_display_only()
    {
        Assert.Equal(100, new MarketSessions().BorderOpacity);
        var study = (BorderProbe)AssertDisplayOnly(() => new BorderProbe(), () => new BorderProbe { BorderOpacity = 40 }, Walk(2 * 24 + 1), ChartPeriod.H1);
        Assert.NotEmpty(study.Scene.Boxes);
        Assert.All(study.Scene.Boxes, b => Assert.Equal(70 * 40 / 100, study.Border(b)));
        Assert.All(study.Scene.Boxes, b => Assert.True(study.IsShown(b)));
        var hidden = new BorderProbe { BorderOpacity = 0 };
        Assert.All(study.Scene.Boxes, b => Assert.Equal(0, hidden.Border(b)));
    }

    [Fact]
    public void Asian_session_sweeps_draw_only_the_last_sessions()
    {
        Assert.Equal(20, new AsianSessionSweeps().MaxSessions);
        var study = AssertDisplayOnly(() => new AsianSessionSweeps { TrendFilter = false }, () => new AsianSessionSweeps { TrendFilter = false, MaxSessions = 2 }, Walk(5 * 24 + 1), ChartPeriod.H1);
        var boxes = study.Scene.Boxes.Where(b => b.Text == "Asia").ToList();
        Assert.True(boxes.Count >= 3);
        Assert.Equal(boxes.Select((_, i) => i >= boxes.Count - 2), boxes.Select(study.IsShown));
        foreach (var name in new[] { "Asia high", "Asia low" })
        {
            var levels = study.Scene.Lines.Where(l => l.Alert == name).ToList();
            Assert.Equal(boxes.Count, levels.Count);
            Assert.Equal(levels.Select((_, i) => i >= levels.Count - 2), levels.Select(study.IsShown));
            Assert.True(levels[^1].Live);
        }
    }

    /// <summary>H1 UTC sessions: a filled bullish gap (24), a filled bearish gap (48), an unfilled bullish gap (72) and the current session's bullish gap (96).</summary>
    private static List<Ohlc> GapDays()
    {
        var flat = new Ohlc(100, 101, 99, 100); var bars = new List<Ohlc>();
        bars.AddRange(Enumerable.Repeat(flat, 24));
        bars.Add(new(105, 108, 103, 104)); bars.Add(new(104, 104, 99, 100)); bars.AddRange(Enumerable.Repeat(flat, 22));
        bars.Add(new(95, 97, 94, 96)); bars.Add(new(96, 101, 95, 100)); bars.AddRange(Enumerable.Repeat(flat, 22));
        bars.Add(new(110, 112, 108, 111)); bars.AddRange(Enumerable.Repeat(new Ohlc(111, 112, 110, 111), 23));
        bars.Add(new(120, 122, 118, 121)); bars.AddRange(Enumerable.Repeat(new Ohlc(121, 122, 120, 121), 4));
        return bars;
    }

    [Theory]
    [InlineData(nameof(SessionGapFill.ShowFilledBull), 24)]
    [InlineData(nameof(SessionGapFill.ShowFilledBear), 48)]
    [InlineData(nameof(SessionGapFill.ShowUnfilled), 72)]
    public void Session_gap_filters_hide_only_ended_gaps_of_their_kind(string option, int hidden)
    {
        var defaults = new SessionGapFill();
        Assert.True(defaults.ShowFilledBull && defaults.ShowFilledBear && defaults.ShowUnfilled);
        SessionGapFill Filtered()
        {
            var filtered = new SessionGapFill();
            typeof(SessionGapFill).GetProperty(option)!.SetValue(filtered, false);
            return filtered;
        }
        var study = AssertDisplayOnly(() => new SessionGapFill(), Filtered, GapDays(), ChartPeriod.H1, session: new SessionSettings());
        Assert.Equal(new[] { 24, 48, 72, 96 }, study.Scene.Boxes.Select(b => b.From));
        Assert.Equal(study.Scene.Boxes.Select(b => b.From != hidden), study.Scene.Boxes.Select(study.IsShown));
        Assert.Equal(2, study.Scene.Lines.Count(l => l.From == hidden)); // prior close level and excursion line
        Assert.All(study.Scene.Lines, l => Assert.Equal(l.From != hidden, study.IsShown(l)));
        Assert.All(study.Scene.Marks, m => Assert.True(study.IsShown(m)));
        Assert.Contains("Session gap: top edge", study.GetAlertLevels().Select(l => l.Name));
    }
}
