using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using static TradingHub.Tests.LibraryStudyFixture;

namespace TradingHub.Tests;

/// <summary>Regressions of the structure/session/profile study review: alert identity, the live path and partial sessions.</summary>
public class StructureReviewTests
{
    private static readonly DateTime Monday = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Gap_edge_alerts_keep_their_ids_while_the_gap_fills_partly()
    {
        // bullish gap 12..16 between bars 0 and 2 (drawn from bar 1); bar 3 fills it to 14, bar 5 to 13, bar 6 closes it
        Ohlc[] bars = { new(10, 12, 9, 11), new(11, 20, 11, 19), new(19, 22, 16, 21), new(21, 22, 14, 15), new(16, 19, 15, 18),
            new(18, 18, 13, 14), new(14, 15, 11, 12), new(12, 13, 11, 12) };
        using var model = Model(Monday, ChartPeriod.M1, null, bars.Take(4).ToList()); // closed through bar 2
        var study = Add(model, new GapFillBreakouts { AtrMult = 0.5m, PivotLength = 1 });
        IndicatorLevel[] Edges() => study.GetAlertLevels().Where(l => l.Name.StartsWith("Unfilled gap", StringComparison.Ordinal)).OrderBy(l => l.Name, StringComparer.Ordinal).ToArray();

        var unfilled = Edges();
        Assert.Equal(new[] { ("Unfilled gap: bottom edge", 12m, 1), ("Unfilled gap: top edge", 16m, 1) }, unfilled.Select(l => (l.Name, l.Price, l.FromBar)));

        Append(model, Monday, ChartPeriod.M1, 4, bars[4]); // closes bar 3: the gap is redrawn from bar 3 as 12..14
        Assert.Contains(study.Scene.Boxes, b => b.Live && b.Alert == "Unfilled gap" && b.From == 3 && b.Top == 14 && b.Bottom == 12);
        var partly = Edges();
        Assert.Equal(unfilled.Select(l => l.Id), partly.Select(l => l.Id));
        Assert.Equal(new[] { (12m, 1), (14m, 1) }, partly.Select(l => (l.Price, l.FromBar)));

        Append(model, Monday, ChartPeriod.M1, 5, bars[5]);
        Append(model, Monday, ChartPeriod.M1, 6, bars[6]); // closes bar 5: 12..13
        var less = Edges();
        Assert.Equal(unfilled.Select(l => l.Id), less.Select(l => l.Id));
        Assert.Equal(new[] { 12m, 13m }, less.Select(l => l.Price));

        Append(model, Monday, ChartPeriod.M1, 7, bars[7]); // closes bar 6: filled, the levels are gone
        Assert.Empty(Edges());
    }

    public static TheoryData<string> LiveStudies => new()
    {
        "gapfill", "sweeps", "shifts", "waves", "zones", "sessions", "asia", "sessionsweeps", "reversion", "sliding", "execution",
    };

    private static ClosedBarStudy Create(string name) => name switch
    {
        "gapfill" => new GapFillBreakouts { AtrMult = 0.3m, PivotLength = 2, MaximumObjects = 5000 },
        "sweeps" => new LiquiditySweeps { SwingLength = 3, MaximumObjects = 5000 },
        "shifts" => new StructureShiftSweeps { Length = 3, MaximumObjects = 5000 },
        "waves" => new WaveExtensions { SwingLength = 2, SubLength = 1, MaximumObjects = 5000 },
        "zones" => new SupplyDemandZones { SwingStrength = 3, MaximumObjects = 5000 },
        "sessions" => new MarketSessions { S1Vwap = true, S2Vwap = true, VwapBands = true, Midline = true, HighlightOverlaps = true, MaximumObjects = 5000 },
        "asia" => new AsianSessionSweeps { TrendFilter = false, InternalLength = 2, MaximumObjects = 5000 },
        "sessionsweeps" => new SessionSweeps { ShowMss = true, ShowFvg = true, FvgFilter = 0, BuysideMargin = 0.3m, SellsideMargin = 0.3m, MaximumObjects = 5000 },
        "reversion" => new ValueAreaReversion { DevelopingSessionSignals = true, MaximumObjects = 5000 },
        "sliding" => new SlidingVolumeProfile { Length = 20, MaximumObjects = 5000 },
        _ => new ExecutionBenchmarks { Window = ExecutionWindow.Hour, ScheduleBars = 6, MaximumObjects = 5000 },
    };

    [Theory]
    [MemberData(nameof(LiveStudies))]
    public void Closed_bar_studies_fed_print_by_print_match_a_fresh_calculation(string name) =>
        AssertLiveMatchesReplay(() => Create(name), Walk(600, 13), ChartPeriod.M5, Monday);

    [Fact]
    public void A_session_already_running_when_the_history_begins_forms_no_range()
    {
        // M15 from Monday 2 March 2026 10:00 UTC: London (08:00–17:00 GMT) is two hours old; Tuesday's London is complete
        using var model = Model(Monday.AddHours(10), ChartPeriod.M15, null, Walk(130));
        var study = Add(model, new MarketSessions { S1Show = false, S3Show = false });
        var london = Assert.Single(study.Scene.Boxes, b => b.Text == "London");
        Assert.Equal(new DateTime(2026, 3, 3, 8, 0, 0, DateTimeKind.Utc), study.Bars[london.From].Time);
        Assert.All(study.GetAlertLevels().Where(l => l.Name.StartsWith("London", StringComparison.Ordinal)), l => Assert.Equal(london.From, l.FromBar));

        // H1 from 03:00 UTC = 22:00 New York: the Asian session (19:00–04:00) began before the history; the next one is whole
        using var hourly = Model(Monday.AddHours(3), ChartPeriod.H1, null, Walk(40));
        var asia = Add(hourly, new AsianSessionSweeps { TrendFilter = false });
        var range = Assert.Single(asia.Scene.Boxes, b => b.Text == "Asia");
        Assert.Equal((21, 29), (range.From, range.To)); // 00:00–08:00 UTC on 3 March
        Assert.All(asia.Scene.Lines.Where(l => l.Alert is "Asia high" or "Asia low"), l => Assert.Equal(21, l.From));
    }

    [Fact]
    public void Session_gap_whose_first_low_equals_the_prior_high_is_kept_by_the_all_filter()
    {
        // hourly bars with UTC sessions: bar 4 (00:00, 3 March) opens above the prior close 100 and its low touches the prior high 102
        Ohlc[] bars = { new(100, 101, 99, 100), new(100, 101, 99, 100), new(100, 101, 99, 100), new(100, 102, 99, 100), new(105, 108, 102, 104), new(104, 106, 103, 105) };
        using var model = Model(Monday.AddHours(20), ChartPeriod.H1, new SessionSettings(), bars);
        var study = Add(model, new SessionGapFill());
        Assert.Equal(1m, Series(study, "SessionGapFill.Gap")[4]);
        Assert.Contains(study.Scene.Boxes, b => b.Alert == "Session gap" && b.From == 4 && b.Top == 105 && b.Bottom == 100);
    }

    [Theory]
    [InlineData(CompositeVolumeProfile.ProfilePeriod.Session)]
    [InlineData(CompositeVolumeProfile.ProfilePeriod.AllHistory)]
    public void Composite_profile_fed_live_without_reads_matches_a_fresh_calculation(CompositeVolumeProfile.ProfilePeriod period)
    {
        var bars = Walk(120, 4);
        using var live = Model(Monday, ChartPeriod.H1, null, bars.Take(60).ToList());
        var study = Add(live, new CompositeVolumeProfile { Period = period, PriceGrouping = 1 });
        // nothing reads the developing profile while sessions close: the deferred update must still finish each of them
        for (int i = 60; i < bars.Count; i++) Append(live, Monday, ChartPeriod.H1, i, bars[i]);
        using var fresh = Model(Monday, ChartPeriod.H1, null, bars);
        var replay = Add(fresh, new CompositeVolumeProfile { Period = period, PriceGrouping = 1 });
        static string Text(CompositeVolumeProfile p) =>
            string.Join("\n", p.Profiles.Select(x => $"{x.StartBar}/{x.EndBar}/{x.Total}/{x.Poc}/{x.ValueAreaLow}/{x.ValueAreaHigh}/{x.MaximumVolume}/{x.Parts}/{x.Merge}"))
            + "|" + string.Join(";", p.PocProjections);
        Assert.Equal(Text(replay), Text(study));
        Assert.True(study.Profiles.Count > (period == CompositeVolumeProfile.ProfilePeriod.Session ? 1 : 0));
    }
}
