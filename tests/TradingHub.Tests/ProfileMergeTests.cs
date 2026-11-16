using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>The unified volume profile (head-to-tail merging, row sizing, cross-timeframe reuse) and the VWAP suite.</summary>
public sealed class ProfileMergeTests
{
    internal static readonly DateTime Monday = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Eight UTC days of 10-minute prints with overlapping daily ranges and both aggressor sides.</summary>
    internal static InstrumentData Market(int days = 8, string symbol = "PROFILE-MERGE")
    {
        var instrument = new Instrument { Symbol = symbol, FeedId = "sim", TickSize = 0.1m, VolumeStep = 1 };
        var data = new InstrumentData(instrument, new SimulatedFeed());
        long id = 0;
        for (int minute = 0; minute < days * 24 * 60; minute += 10)
        {
            int day = minute / 1440;
            decimal price = 100m + day * 0.5m + Math.Round(5m * (decimal)Math.Sin(minute / 97.0), 1);
            var side = minute % 30 == 0 ? TradeSide.Sell : TradeSide.Buy;
            data.Trades.Add(new Trade(++id, Monday.AddMinutes(minute), price, 1 + minute % 4, side));
        }
        return data;
    }

    private static (ChartModel Model, VolumeProfile Profile) Chart(InstrumentData data, string period, Action<VolumeProfile>? setup = null)
    {
        var model = new ChartModel(data, ChartPeriod.Parse(period), new ChartSettings { TimeZoneId = "UTC" });
        var profile = new VolumeProfile();
        setup?.Invoke(profile);
        Assert.Null(model.AddIndicator(profile, applyDefaultColors: false).Error);
        model.Update();
        return (model, profile);
    }

    private static string Utc(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Merging_from_a_start_to_an_end_leaves_one_profile_with_conserved_rows()
    {
        using var data = Market();
        var (model, vp) = Chart(data, "H1");
        using var _ = model;
        var before = vp.Profiles.ToList();
        Assert.Equal(8, before.Count);
        var expectedRows = new Dictionary<decimal, decimal>();
        foreach (var day in before.Skip(1).Take(3))
            foreach (var (price, volume) in day.Levels) { expectedRows.TryGetValue(price, out var old); expectedRows[price] = old + volume; }

        // Tuesday 00:00 to Thursday 00:00: three source periods become one head-to-tail profile.
        vp.MergedPeriods = Utc(Monday.AddDays(1)) + ".." + Utc(Monday.AddDays(3));
        model.Update();

        Assert.Equal(6, vp.Profiles.Count);
        var merged = Assert.Single(vp.Profiles, p => p.Parts == 3);
        Assert.Equal(CompositeVolumeProfile.MergeKind.Manual, merged.Merge);
        Assert.Equal(before[1].StartBar, merged.StartBar);
        Assert.Equal(before[3].EndBar, merged.EndBar);
        Assert.Equal(expectedRows.OrderBy(r => r.Key), merged.Levels.OrderBy(r => r.Key));
        Assert.Equal(before.Skip(1).Take(3).Sum(p => p.Total), merged.Total);
        Assert.Equal(before.Sum(p => p.Total), vp.Profiles.Sum(p => p.Total));
        Assert.Equal(Utc(Monday.AddDays(1)) + ".." + Utc(Monday.AddDays(3)), vp.MergedPeriods);
    }

    [Fact]
    public void Merged_POC_and_value_area_are_recomputed_with_deterministic_ties()
    {
        using var data = Market();
        var (model, vp) = Chart(data, "H1", p => p.MergedPeriods = Utc(Monday) + ".." + Utc(Monday.AddDays(7)));
        using var _ = model;
        var merged = Assert.Single(vp.Profiles);
        Assert.Equal(8, merged.Parts);
        var rows = merged.Levels.OrderBy(r => r.Key).ToArray();
        decimal max = rows.Max(r => r.Value);
        // Lowest price wins a POC tie.
        Assert.Equal(rows.First(r => r.Value == max).Key, merged.Poc);
        // Value area grows from the POC; the upper neighbour wins expansion ties; empty rows are skipped as gaps.
        int low = Array.FindIndex(rows, r => r.Key == merged.Poc), high = low;
        decimal accumulated = rows[low].Value, target = merged.Total * 70 / 100, step = vp.EffectivePriceGrouping;
        while (accumulated < target && (low > 0 || high < rows.Length - 1))
        {
            decimal above = high == rows.Length - 1 ? -1 : rows[high + 1].Key - rows[high].Key == step ? rows[high + 1].Value : 0;
            decimal below = low == 0 ? -1 : rows[low].Key - rows[low - 1].Key == step ? rows[low - 1].Value : 0;
            if (above >= below) accumulated += rows[++high].Value; else accumulated += rows[--low].Value;
        }
        Assert.Equal(rows[low].Key, merged.ValueAreaLow);
        Assert.Equal(rows[high].Key, merged.ValueAreaHigh);
    }

    [Fact]
    public void Overlapping_neighbours_merge_automatically_but_never_into_a_manual_block()
    {
        using var data = Market();
        var (plainModel, plain) = Chart(data, "H1");
        var (autoModel, auto) = Chart(data, "H1", p =>
        {
            p.ComposeProfiles = true;
            p.MergeRule = CompositeVolumeProfile.ComposeRule.Overlap;
            p.OverlapPercent = 1;
            p.MergeBasis = CompositeVolumeProfile.OverlapBasis.Range;
        });
        using var a = plainModel;
        using var b = autoModel;
        Assert.True(auto.Profiles.Count < plain.Profiles.Count);
        Assert.Contains(auto.Profiles, p => p.Parts > 1 && p.Merge == CompositeVolumeProfile.MergeKind.Automatic);
        Assert.Equal(plain.Profiles.Sum(p => p.Total), auto.Profiles.Sum(p => p.Total));

        auto.MergedPeriods = Utc(Monday.AddDays(2)) + ".." + Utc(Monday.AddDays(3));
        autoModel.Update();
        var manual = Assert.Single(auto.Profiles, p => p.Merge == CompositeVolumeProfile.MergeKind.Manual);
        Assert.Equal(2, manual.Parts);
        Assert.Equal(plain.Profiles[2].StartBar, manual.StartBar);
        Assert.Equal(plain.Profiles[3].EndBar, manual.EndBar);
    }

    [Fact]
    public void Overlap_fraction_uses_the_smaller_range_and_rejects_gaps()
    {
        var a = new CompositeVolumeProfile.Profile { ValueAreaLow = 10, ValueAreaHigh = 20, LowPrice = 5, HighPrice = 25 };
        var b = new CompositeVolumeProfile.Profile { ValueAreaLow = 18, ValueAreaHigh = 22, LowPrice = 18, HighPrice = 40 };
        Assert.Equal(0.5m, CompositeVolumeProfile.OverlapFraction(a, b, CompositeVolumeProfile.OverlapBasis.ValueArea));
        Assert.Equal(7m / 20m, CompositeVolumeProfile.OverlapFraction(a, b, CompositeVolumeProfile.OverlapBasis.Range));
        var apart = new CompositeVolumeProfile.Profile { ValueAreaLow = 30, ValueAreaHigh = 31 };
        Assert.Equal(0m, CompositeVolumeProfile.OverlapFraction(a, apart, CompositeVolumeProfile.OverlapBasis.ValueArea));
    }

    [Fact]
    public void Invalid_merge_ranges_are_rejected_and_overlapping_ranges_are_joined()
    {
        var vp = new VolumeProfile();
        Assert.Throws<FormatException>(() => vp.MergedPeriods = "yesterday..today");
        vp.MergedPeriods = "2026-10-22T00:00:00Z..2026-10-20T00:00:00Z; 2026-10-21T00:00:00Z..2026-10-23T00:00:00Z";
        Assert.Equal("2026-10-20T00:00:00Z..2026-10-23T00:00:00Z", vp.MergedPeriods);
    }

    [Fact]
    public void Ticks_per_row_align_rows_to_the_instrument_tick()
    {
        using var data = Market();
        var (model, vp) = Chart(data, "H1", p => p.TicksPerRow = 50);
        using var _ = model;
        Assert.Equal(5m, vp.EffectivePriceGrouping);
        Assert.All(vp.Profiles.SelectMany(p => p.Levels.Keys), price => Assert.Equal(0m, price % 5m));
    }

    [Fact]
    public void All_history_period_builds_a_single_profile()
    {
        using var data = Market(3);
        var (model, vp) = Chart(data, "H1", p => p.Period = CompositeVolumeProfile.ProfilePeriod.AllHistory);
        using var _ = model;
        var profile = Assert.Single(vp.Profiles);
        Assert.Equal(0, profile.StartBar);
        Assert.Equal(model.Series.Count - 1, profile.EndBar);
        Assert.Empty(vp.GetChartMenuItems(0, 0));
    }

    private static List<string> Snapshot(CompositeVolumeProfile p) => p.Profiles.Select(x => new
    {
        x.Total, x.Poc, x.ValueAreaLow, x.ValueAreaHigh, x.OuterValueAreaLow, x.OuterValueAreaHigh, x.BuyerPoc, x.SellerPoc,
        Rows = string.Join(",", x.Levels.OrderBy(r => r.Key).Select(r => r.Key + ":" + r.Value)),
    }).Select(x => x.ToString()!).ToList();

    [Fact]
    public void Closed_profiles_are_reused_across_timeframes_of_the_same_history()
    {
        var cache = new CompletedStudyResultCache();
        using var data = Market();
        var (m15, quarter) = Chart(data, "M15", p => p.ClosedRows = cache);
        using var a = m15;
        Assert.Equal(0, quarter.ReusedClosedProfiles);
        var (h1, hour) = Chart(data, "H1", p => p.ClosedRows = cache);
        using var b = h1;
        // Six closed days restored; the first loaded day and the developing day are always recalculated.
        Assert.Equal(6, hour.ReusedClosedProfiles);
        Assert.Equal(Snapshot(quarter).Skip(1), Snapshot(hour).Skip(1));

        var (fresh, reference) = Chart(data, "H1", p => p.ClosedRows = new CompletedStudyResultCache());
        using var c = fresh;
        Assert.Equal(0, reference.ReusedClosedProfiles);
        Assert.Equal(Snapshot(reference), Snapshot(hour));
        Assert.Equal(reference.Profiles.Select(p => p.Coverage), hour.Profiles.Select(p => p.Coverage));
    }

    [Fact]
    public void A_history_correction_or_a_different_row_grouping_never_reuses_closed_rows()
    {
        var cache = new CompletedStudyResultCache();
        using var data = Market();
        var (m15, quarter) = Chart(data, "M15", p => p.ClosedRows = cache);
        decimal tuesday = quarter.Profiles[1].Total;
        m15.Dispose();

        // Same history, coarser row grouping: a different configuration is a different key.
        var (coarse, coarseProfile) = Chart(data, "H1", p => { p.ClosedRows = cache; p.TicksPerRow = 20; });
        using (coarse) Assert.Equal(0, coarseProfile.ReusedClosedProfiles);

        // Correct one print inside Tuesday: every closed row of this generation becomes unreachable.
        int index = data.Trades.LowerBound(Monday.AddDays(1).AddHours(5));
        var original = data.Trades.Get(index);
        data.Trades.Update(index, new Trade(original.Id, original.Time, original.Price, original.Volume + 100, original.Side, original.Provenance));
        var (corrected, profile) = Chart(data, "H1", p => p.ClosedRows = cache);
        using var _2 = corrected;
        Assert.Equal(0, profile.ReusedClosedProfiles);
        Assert.Equal(tuesday + 100, profile.Profiles[1].Total);
        var (fresh, reference) = Chart(data, "H1", p => p.ClosedRows = new CompletedStudyResultCache());
        using var _3 = fresh;
        Assert.Equal(Snapshot(reference), Snapshot(profile));
    }

    [Fact]
    public void The_VWAP_suite_calculates_its_parts_and_saves_their_settings()
    {
        using var data = Market(4);
        using var model = new ChartModel(data, ChartPeriod.M15, new ChartSettings { TimeZoneId = "UTC" });
        var suite = new VwapSuite { UseClaire = true, UseMultiPeriod = true, UseRolling = true, UsePriorClosing = true, UseSessionWindow = true };
        suite.Anchored.Band3 = 2.5m;
        Assert.Null(model.AddIndicator(suite, applyDefaultColors: false).Error);
        model.Update();
        int last = model.Series.Count - 1;
        var main = (OptionalValueDataSeries)suite.DataSeries.Single(s => s.Id == "VWAP");
        Assert.True(main.IsAvailable(last) && main[last] > 0);
        Assert.True(((OptionalValueDataSeries)suite.DataSeries.Single(s => s.Id == "Up3"))[last] > main[last]);
        Assert.True(((OptionalValueDataSeries)suite.DataSeries.Single(s => s.Id == "RollingVwap")).IsAvailable(last));
        Assert.True(((OptionalValueDataSeries)suite.DataSeries.Single(s => s.Id == "MultiVwap.Day")).IsAvailable(last));
        Assert.Equal(suite.DataSeries.Count, suite.DataSeries.Select(s => s.Id).Distinct().Count());

        suite.Anchored.Band1 = 1.5m;
        suite.Rolling.Length = 90;
        suite.UseClaire = false;
        var props = Workspace.SaveIndicator(suite, out var series);
        var copy = new VwapSuite();
        Workspace.LoadIndicator(copy, props, series);
        Assert.Equal(1.5m, copy.Anchored.Band1);
        Assert.Equal(2.5m, copy.Anchored.Band3);
        Assert.Equal(90, copy.Rolling.Length);
        Assert.False(copy.UseClaire);
        Assert.True(copy.UseSessionWindow);
        Assert.True(copy.DataSeries.Single(s => s.Id == "Claire.R30").IsHidden);
        Assert.False(copy.DataSeries.Single(s => s.Id == "RollingVwap").IsHidden);
    }

    [Fact]
    public void A_disabled_VWAP_part_is_not_calculated()
    {
        using var data = Market(2);
        using var model = new ChartModel(data, ChartPeriod.M15, new ChartSettings { TimeZoneId = "UTC" });
        var suite = new VwapSuite();
        Assert.Null(model.AddIndicator(suite, applyDefaultColors: false).Error);
        model.Update();
        var rolling = (OptionalValueDataSeries)suite.DataSeries.Single(s => s.Id == "RollingVwap");
        Assert.All(Enumerable.Range(0, model.Series.Count), bar => Assert.False(rolling.IsAvailable(bar)));
        suite.UseRolling = true;
        model.Update();
        Assert.True(rolling.IsAvailable(model.Series.Count - 1));
    }
}
