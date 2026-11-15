using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using static TradingHub.Tests.LibraryStudyFixture;

namespace TradingHub.Tests;

public class LibraryProfileStudiesTests
{
    private static readonly DateTime Start = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    private static Ohlc Flat(decimal price, decimal volume) => new(price, price, price, price, volume);

    // Session A (2026-03-02, H4): rows 100:40, 101:80, 102:320, 103:80, 104:40 -> POC 102, 70% value area 102..103.
    private static readonly Ohlc[] SessionA = { Flat(100, 40), Flat(101, 80), Flat(102, 160), Flat(102, 160), Flat(103, 80), Flat(104, 40) };

    private static ChartModel TradesModel(ChartPeriod period, params (int Bar, decimal Price, decimal Volume, bool Synthetic)[] trades)
    {
        var instrument = new Instrument { Symbol = "PROFILE-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 };
        var data = new InstrumentData(instrument, new SimulatedFeed()); long id = 1;
        foreach (var t in trades)
        {
            data.Trades.Add(new Trade(t.Synthetic ? -id : id, Start + period.ApproxDuration * t.Bar + TimeSpan.FromSeconds(id), t.Price, t.Volume, TradeSide.Buy));
            id++;
        }
        return new ChartModel(data, period, new ChartSettings());
    }

    [Fact]
    public void Rolling_profile_POC_and_value_area_match_a_hand_built_footprint()
    {
        using var model = Model(Start, ChartPeriod.H4, null, SessionA.Append(Flat(104, 40)).ToList());
        var sliding = Add(model, new SlidingVolumeProfile { Length = 6 });
        var axis = Add(model, new VolumeProfileValueArea { Length = 6 });
        Assert.False(Series(sliding, "SlidingVolumeProfile.Poc").IsAvailable(4)); // window not full yet
        Assert.Equal(102m, Series(sliding, "SlidingVolumeProfile.Poc")[5]);
        Assert.Equal(103m, Series(sliding, "SlidingVolumeProfile.Vah")[5]);
        Assert.Equal(102m, Series(sliding, "SlidingVolumeProfile.Val")[5]);
        Assert.Equal(102m, Series(axis, "VolumeProfileValueArea.Poc")[5]);
        Assert.Equal(560m, Current(sliding).Total);
        Assert.Equal(new[] { 100m, 101m, 102m, 103m, 104m }, sliding.Profile.Prices.ToArray());
        Assert.Equal(new[] { 40m, 80m, 320m, 80m, 40m }, sliding.Profile.Volumes.ToArray());
        var levels = sliding.GetAlertLevels().ToList();
        Assert.Equal(new[] { "SlidingVolumeProfile.Poc", "SlidingVolumeProfile.Vah", "SlidingVolumeProfile.Val" }, levels.Select(l => l.Id));
        Assert.Equal(0, levels[0].FromBar);
        Assert.True(axis.HourVolume(new DateOnly(2026, 3, 2), 0) > 0);
    }

    [Fact]
    public void Bars_without_footprint_are_missing_by_default_and_estimated_only_on_request()
    {
        // bar 1 holds only synthetic OHLC volume with unknown availability: not causal evidence
        using var model = TradesModel(ChartPeriod.M1, (0, 100, 40, false), (1, 110, 400, true), (2, 105, 4, false));
        var study = Add(model, new SlidingVolumeProfile { Length = 5 });
        Assert.Null(study.Current);
        Assert.False(Series(study, "SlidingVolumeProfile.Poc").IsAvailable(1));

        var estimated = Add(model, new SlidingVolumeProfile { Length = 5, AllowEstimated = true });
        Assert.Equal(110m, Current(estimated).Poc); Assert.Equal(440m, Current(estimated).Total);
        Assert.True(estimated.Coverage.HasFlag(StudyCoverage.Estimated)); Assert.True(estimated.Coverage.HasFlag(StudyCoverage.Observed));
        Assert.Equal(FootprintStudyData.Label(estimated.Coverage), estimated.Status);
        // drawn as an explicit estimate, never published as a strategy value
        Assert.False(Series(estimated, "SlidingVolumeProfile.Poc").IsAvailable(1));
    }

    [Fact]
    public void Rolling_window_add_and_remove_equals_a_fresh_profile_of_the_last_bars()
    {
        var random = new Random(3); var rolling = new StudyRowProfile(); rolling.Reset(1, 5);
        var history = new List<(List<StudyLevel> Rows, StudyCoverage Coverage)>();
        for (int i = 0; i < 80; i++)
        {
            var rows = Enumerable.Range(0, random.Next(0, 6)).Select(_ => new StudyLevel(95 + random.Next(0, 11), random.Next(0, 50), 0, 0)).ToList();
            var coverage = rows.Count == 0 ? StudyCoverage.Missing : i % 7 == 0 ? StudyCoverage.Compact : StudyCoverage.Observed;
            rolling.Add(rows, coverage); history.Add((rows, coverage));
            var fresh = new StudyRowProfile(); fresh.Reset(1, 0);
            foreach (var (r, c) in history.Skip(Math.Max(0, history.Count - 5))) fresh.Add(r, c);
            Assert.Equal(fresh.Levels(70), rolling.Levels(70));
            Assert.Equal(fresh.Prices.ToArray(), rolling.Prices.ToArray());
            Assert.Equal(fresh.Volumes.ToArray(), rolling.Volumes.ToArray());
            Assert.Equal(fresh.Total, rolling.Total); Assert.Equal(fresh.Coverage, rolling.Coverage);
            Assert.Equal(Math.Min(5, history.Count), rolling.BarCount);
        }
    }

    [Fact]
    public void Reversion_against_the_previous_session_value_area_is_signalled_at_the_re_entry_close()
    {
        var bars = SessionA.Concat(new[] { Flat(106, 40), new Ohlc(106, 106, 103, 103, 40), Flat(103, 40) }).ToList();
        using var model = Model(Start, ChartPeriod.H4, null, bars);
        var study = Add(model, new ValueAreaReversion());
        var high = Series(study, "ValueAreaReversion.PriorHigh"); var signal = Series(study, "ValueAreaReversion.Prior");
        // the previous value area is unknown until session A has completed
        Assert.False(high.IsAvailable(5)); Assert.False(signal.IsAvailable(5));
        Assert.True(high.IsAvailable(6)); Assert.Equal(103m, high[6]); Assert.Equal(102m, Series(study, "ValueAreaReversion.PriorLow")[6]);
        Assert.Equal(102m, Series(study, "ValueAreaReversion.PriorPoc")[6]);
        Assert.Equal(0m, signal[6]); // bar 6 leaves above
        Assert.Equal(-1m, signal[7]); // bar 7 closes back inside
        var mark = Assert.Single(study.Scene.Marks);
        Assert.Equal((7, 7, 106m, StudyMarkKind.TriangleDown, "VAH"), (mark.Bar, mark.Available, mark.Price, mark.Kind, mark.Text));
        var vah = Assert.Single(study.Scene.Lines, l => l.Alert == "Previous session VAH");
        Assert.Equal((6, 6, 103m, true), (vah.From, vah.Available, vah.Price, vah.Live));
        Assert.Contains(study.GetAlertLevels(), l => l.Name == "Previous session VAL" && l.Price == 102m);
    }

    [Fact]
    public void Profile_levels_are_series_and_closes_crossing_the_levels_in_force_are_marked()
    {
        using var model = Model(Start, ChartPeriod.H4, null, new[] { Flat(100, 160), Flat(102, 40), Flat(98, 40), new Ohlc(98, 101, 98, 101, 40), Flat(101, 40) });
        var study = Add(model, new ProfileLevelAlerts());
        Assert.Equal(100m, Series(study, "ProfileLevels.Poc")[0]);
        Assert.Equal((100m, 102m, 100m), (Series(study, "ProfileLevels.Poc")[2], Series(study, "ProfileLevels.Vah")[2], Series(study, "ProfileLevels.Val")[2]));
        var cross = Series(study, "ProfileLevels.Cross");
        Assert.False(cross.IsAvailable(0));
        Assert.Equal(3m, cross[1]); Assert.Equal(-3m, cross[2]);
        Assert.Equal(2m, cross[3]); // POC and VAL at 100 crossed up; VAH 102 not reached
        Assert.Equal(2, study.Scene.Marks.Count(m => m.Bar == 3 && m.Available == 3 && m.Price == 100m && m.Kind == StudyMarkKind.TriangleUp));
        var levels = study.GetAlertLevels().ToList();
        Assert.Equal(3, levels.Count); Assert.Equal(levels.Count, levels.Select(l => l.Id).Distinct().Count());
        study.RecalculateValues(); model.Update();
        Assert.Equal(levels.Select(l => l.Id), study.GetAlertLevels().Select(l => l.Id));

        var previous = Add(model, new ProfileLevelAlerts { LevelsOf = ProfileLevelSource.Previous });
        Assert.False(Series(previous, "ProfileLevels.Poc").IsAvailable(3)); // the first session is not complete yet
    }

    [Fact]
    public void Tails_are_the_outer_rows_below_the_threshold_of_the_POC_volume()
    {
        using var model = Model(Start, ChartPeriod.H1, null, new[] { Flat(100, 40), Flat(101, 40), Flat(102, 400), Flat(103, 400), Flat(104, 40), Flat(105, 40), Flat(105, 40) });
        var study = Add(model, new ProfileTails { Length = 6, ThresholdPercent = 30 });
        Assert.False(Series(study, "ProfileTails.SupplyLow").IsAvailable(4));
        Assert.Equal(104m, Series(study, "ProfileTails.SupplyLow")[5]); Assert.Equal(105m, Series(study, "ProfileTails.SupplyHigh")[5]);
        Assert.Equal(100m, Series(study, "ProfileTails.DemandLow")[5]); Assert.Equal(101m, Series(study, "ProfileTails.DemandHigh")[5]);

        using var heavy = Model(Start, ChartPeriod.H1, null, new[] { Flat(100, 40), Flat(101, 40), Flat(102, 400), Flat(103, 400), Flat(104, 40), Flat(105, 40), Flat(105, 40) });
        var none = Add(heavy, new ProfileTails { Length = 6, ThresholdPercent = 5 });
        Assert.False(Series(none, "ProfileTails.SupplyLow").IsAvailable(5)); Assert.False(Series(none, "ProfileTails.DemandLow").IsAvailable(5));
    }

    [Fact]
    public void Hot_hours_follow_the_exchange_clock_through_the_autumn_DST_change()
    {
        var session = new SessionSettings { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Start = TimeSpan.FromHours(18) };
        // 03:00 UTC = 23:00 EDT; bars 4-5 are 01:00 EDT and bars 6-7 the repeated 01:00 EST hour
        var bars = new[] { Flat(100, 40), Flat(100, 40), Flat(100, 40), Flat(100, 40), Flat(100, 40), Flat(100, 40), new Ohlc(100, 104, 99, 103, 400), Flat(103, 400), Flat(103, 40) };
        using var model = Model(new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc), ChartPeriod.M30, session, bars);
        var study = Add(model, new VolumeHeatHours());
        Assert.Equal(new[] { (2, 3), (4, 5), (6, 7) }, study.Hours.Select(h => (h.From, h.To)));
        var hot = study.Hours[^1];
        Assert.Equal((7, 10m, 80m, true), (hot.Available, hot.Ratio, hot.Average, hot.Hot));
        Assert.False(study.Hours[1].Hot); Assert.Equal(1m, study.Hours[1].Ratio);
        Assert.Equal(10m, Series(study, "VolumeHeatHours.Ratio")[7]); Assert.False(Series(study, "VolumeHeatHours.Ratio").IsAvailable(6));
        var box = Assert.Single(study.Scene.Boxes, b => b.From == 6);
        Assert.Equal((7, 7, 104m, 99m, TradingHub.Indicators.LineDashStyle.Solid), (box.To, box.Available, box.Top, box.Bottom, box.Style));
        Assert.All(study.Scene.Boxes.Where(b => b.From != 6), b => Assert.Equal(TradingHub.Indicators.LineDashStyle.Dot, b.Style));
        var tag = Assert.Single(study.Scene.Tags);
        Assert.Equal((6, 7, "10.0x"), (tag.Bar, tag.Available, tag.Text));
    }

    [Fact]
    public void Multi_period_VWAP_bands_are_mean_plus_minus_k_deviations_and_leave_the_old_series_unchanged()
    {
        using var model = Model(Flat(10, 4), Flat(20, 4), Flat(20, 4));
        var plain = Add(model, new MultiPeriodVwap { Source = StudyVwapSource.BarClose });
        var banded = Add(model, new MultiPeriodVwap { Source = StudyVwapSource.BarClose, SessionBands = true, FirstBandDeviations = 1, SecondBandDeviations = 2 });
        Assert.Equal(25, banded.DataSeries.Count);
        Assert.Equal(new[] { "MultiVwap.Day", "MultiVwap.Week", "MultiVwap.Month", "MultiVwap.Quarter", "MultiVwap.Year" }, banded.DataSeries.Take(5).Select(s => s.Id));
        Assert.Equal("MultiVwap.Day.Upper1", banded.DataSeries[5].Id);
        for (int s = 0; s < 5; s++)
            for (int bar = 0; bar < 2; bar++) Assert.Equal(((ValueDataSeries)plain.DataSeries[s])[bar], ((ValueDataSeries)banded.DataSeries[s])[bar]);
        Assert.Equal(15m, Series(banded, "MultiVwap.Day")[1]);
        Assert.Equal((20m, 10m, 25m, 5m), (Series(banded, "MultiVwap.Day.Upper1")[1], Series(banded, "MultiVwap.Day.Lower1")[1], Series(banded, "MultiVwap.Day.Upper2")[1], Series(banded, "MultiVwap.Day.Lower2")[1]));
        Assert.Equal(VisualMode.Line, Series(banded, "MultiVwap.Day.Upper1").VisualType);
        Assert.Equal(VisualMode.Hide, Series(banded, "MultiVwap.Day.Upper2").VisualType);
        Assert.Equal(VisualMode.Hide, Series(plain, "MultiVwap.Day.Upper1").VisualType);
        banded.ShowSecondBand = true; Assert.Equal(VisualMode.Line, Series(banded, "MultiVwap.Day.Upper2").VisualType);

        using var walk = Model(Start, ChartPeriod.M15, null, Walk(200));
        var study = Add(walk, new MultiPeriodVwap { WeekBands = true, FirstBandDeviations = 1.5m });
        var mean = Series(study, "MultiVwap.Week"); var upper = Series(study, "MultiVwap.Week.Upper1"); var lower = Series(study, "MultiVwap.Week.Lower1");
        for (int bar = 0; bar < 198; bar++)
        {
            Assert.Equal(mean.IsAvailable(bar), upper.IsAvailable(bar));
            if (mean.IsAvailable(bar)) Assert.True(Math.Abs(upper[bar] + lower[bar] - 2 * mean[bar]) < 0.000001m && upper[bar] >= mean[bar]);
        }
    }

    [Fact]
    public void Timed_session_VWAP_preset_uses_the_DST_correct_world_session_window()
    {
        // 2026-06-01: London 08:00 BST = 07:00 UTC
        var bars = new[] { Flat(100, 4), Flat(101, 4), Flat(102, 4), Flat(103, 4), Flat(104, 4) };
        using var model = Model(new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc), ChartPeriod.M30, null, bars);
        var london = Add(model, new TimedSessionVwap { Preset = TimedSessionVwap.SessionPreset.London });
        var line = Series(london, "TimedSessionVwap");
        Assert.False(line.IsAvailable(1)); Assert.True(line.IsAvailable(2));
        Assert.Equal(102m, line[2]); Assert.Equal(102.5m, line[3]);
        var custom = Add(model, new TimedSessionVwap()); // New York 09:30-16:00 by default
        Assert.Equal(TimedSessionVwap.SessionPreset.Custom, custom.Preset);
        Assert.False(Series(custom, "TimedSessionVwap").IsAvailable(2));
        london.Preset = TimedSessionVwap.SessionPreset.Custom;
        Assert.Equal(("America/New_York", TimeSpan.FromHours(9.5)), (london.TimeZoneId, london.StartLocal));
    }

    [Fact]
    public void Execution_benchmarks_score_a_hand_built_window_in_basis_points()
    {
        var study = Benchmarks(Flat(106, 40), out var model);
        using (model)
        {
            var result = Assert.Single(study.Results);
            Assert.Equal((4, 7, 7, 100m, 100m, 103m, 103m), (result.From, result.To, result.Available, result.Parent, result.Arrival, result.WindowVwap, result.WindowTwap));
            Assert.Equal(new[] { 0.1m, 0.2m, 0.3m, 0.4m }, result.VwapSchedule);
            var twap = Fill(result.Twap); var vwap = Fill(result.Vwap); var pov = Fill(result.Pov);
            Assert.Equal((100m, 103m, 300m, 0m), (twap.Filled, twap.AveragePrice, twap.VsArrivalBps, twap.VsVwapBps));
            Assert.Equal((100m, 104m, 400m, 97.0874m), (vwap.Filled, vwap.AveragePrice, vwap.VsArrivalBps, Math.Round(vwap.VsVwapBps, 4)));
            Assert.Equal((16m, 103m), (pov.Filled, pov.AveragePrice));
            var series = Series(study, "ExecutionBenchmarks.Vwap.VsArrival");
            Assert.False(series.IsAvailable(6)); Assert.Equal(400m, series[7]);
        }
    }

    [Fact]
    public void Execution_VWAP_schedule_uses_only_earlier_windows()
    {
        var baseline = Benchmarks(Flat(106, 40), out var a);
        var heavier = Benchmarks(Flat(106, 400), out var b); // the window's own last bar trades ten times more
        using (a) using (b)
        {
            var x = Assert.Single(baseline.Results); var y = Assert.Single(heavier.Results);
            Assert.Equal(x.VwapSchedule, y.VwapSchedule);
            Assert.Equal(Fill(x.Vwap).AveragePrice, Fill(y.Vwap).AveragePrice);
            Assert.NotEqual(x.WindowVwap, y.WindowVwap);
        }
    }

    private static StudyProfileLevels Current(RollingProfileStudy study) => study.Current ?? throw new InvalidOperationException("The study has no profile.");

    private static ExecutionBenchmarks.AlgoFill Fill(ExecutionBenchmarks.AlgoFill? fill) => fill ?? throw new InvalidOperationException("The algorithm did not fill.");

    private static ExecutionBenchmarks Benchmarks(Ohlc lastBar, out ChartModel model)
    {
        var bars = new List<Ohlc> { Flat(100, 40), Flat(100, 80), Flat(100, 120), Flat(100, 160), Flat(100, 40), Flat(102, 40), Flat(104, 40), lastBar, Flat(106, 40) };
        model = Model(Start, ChartPeriod.M15, null, bars);
        return Add(model, new ExecutionBenchmarks { Window = ExecutionWindow.Hour, Sizing = ExecutionSizing.Units, UnitSize = 100, PovRate = 10 });
    }

    public static IEnumerable<object[]> PrefixCases()
    {
        yield return new object[] { "M15", (Func<Indicator>)(() => new ValueAreaReversion { DevelopingSessionSignals = true, MaximumObjects = 5000 }) };
        yield return new object[] { "M15", (Func<Indicator>)(() => new ProfileLevelAlerts { MaximumObjects = 5000 }) };
        yield return new object[] { "M15", (Func<Indicator>)(() => new ProfileLevelAlerts { LevelsOf = ProfileLevelSource.Previous, MaximumObjects = 5000 }) };
        yield return new object[] { "M5", (Func<Indicator>)(() => new ProfileTails { Length = 30, MaximumObjects = 5000 }) };
        yield return new object[] { "M5", (Func<Indicator>)(() => new VolumeProfileValueArea { Length = 40, MaximumObjects = 5000 }) };
        yield return new object[] { "M5", (Func<Indicator>)(() => new SlidingVolumeProfile { Length = 40, MaximumObjects = 5000 }) };
        yield return new object[] { "M5", (Func<Indicator>)(() => new VolumeHeatHours { BaselineHours = 6, MaximumObjects = 5000 }) };
        yield return new object[] { "M5", (Func<Indicator>)(() => new ExecutionBenchmarks { Window = ExecutionWindow.Hour, MaximumObjects = 5000 }) };
        yield return new object[] { "M15", (Func<Indicator>)(() => new ExecutionBenchmarks { Window = ExecutionWindow.Session, Side = ExecutionSide.Sell, MaximumObjects = 5000 }) };
        yield return new object[] { "M15", (Func<Indicator>)(() => new ExecutionBenchmarks { Window = ExecutionWindow.FourHours, Sizing = ExecutionSizing.Units, MaximumObjects = 5000 }) };
        yield return new object[] { "M15", (Func<Indicator>)(() => new MultiPeriodVwap { SessionBands = true, WeekBands = true }) };
        yield return new object[] { "M15", (Func<Indicator>)(() => new TimedSessionVwap { Preset = TimedSessionVwap.SessionPreset.London }) };
    }

    [Theory]
    [MemberData(nameof(PrefixCases))]
    public void Profile_studies_never_redraw_backwards(string period, Func<Indicator> create) =>
        AssertPrefixInvariant(create, Walk(400), 250, ChartPeriod.Parse(period));

    [Theory]
    [InlineData("M5")] [InlineData("M15")] [InlineData("H1")]
    public void Profile_studies_render(string period)
    {
        using var model = Model(Start, ChartPeriod.Parse(period), null, Walk(300));
        Add(model, new ValueAreaReversion { DevelopingSessionSignals = true });
        Add(model, new ProfileLevelAlerts());
        Add(model, new ProfileTails { Length = 40 });
        Add(model, new VolumeProfileValueArea { Length = 60 });
        Add(model, new SlidingVolumeProfile { Length = 60 });
        Add(model, new VolumeHeatHours());
        Add(model, new ExecutionBenchmarks { Window = ExecutionWindow.Hour });
        Add(model, new MultiPeriodVwap { SessionBands = true, ShowSecondBand = true, ShowPriceLabels = true });
        Add(model, new TimedSessionVwap { Preset = TimedSessionVwap.SessionPreset.Tokyo });
        Render(model);
    }
}
