using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public class NonKvpFlowStudiesTests
{
    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static ChartModel Model(params (decimal Price, decimal Volume, TradeSide Side, bool Synthetic)[] bars)
    {
        var instrument = new Instrument { Symbol = "FLOW-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 };
        var data = new InstrumentData(instrument, new SimulatedFeed());
        for (int i = 0; i < bars.Length; i++) data.Trades.Add(new Trade(bars[i].Synthetic ? -i - 1 : i + 1, Start.AddMinutes(i), bars[i].Price, bars[i].Volume, bars[i].Side));
        return new ChartModel(data, ChartPeriod.M1, new ChartSettings());
    }
    private static T Add<T>(ChartModel model, T study) where T : Indicator
    {
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error); return study;
    }
    private static AnchoredFlowVwap Manual(bool estimate = false) => new() { Mode = AnchoredFlowVwap.AnchorMode.Manual, Weight = AnchoredFlowVwap.FlowWeight.Volume, AllowEstimated = estimate };
    private static decimal Value(Indicator study, int bar) => ((ValueDataSeries)study.DataSeries[0])[bar];
    private static void Replace(TradingHub.Data.Candle candle, long price, decimal volume, TradeSide side = TradeSide.None, bool synthetic = false)
    {
        candle.Levels.Clear(); candle.Trades = 0; candle.Volume = candle.Bid = candle.Ask = candle.Between = 0;
        candle.HasRealTrades = candle.HasSyntheticTrades = false;
        candle.AddTrade(price, price, volume, side, candle.Time.Ticks, 0, synthetic);
    }
    [Fact]
    public void One_observed_row_is_usable_and_between_volume_is_not_discarded()
    {
        using var model = Model((10, 7, TradeSide.None, false));
        var flow = Add(model, Manual());
        var profile = Add(model, new CompositeVolumeProfile { PriceGrouping = 1 });
        Assert.True(flow.IsAvailable); Assert.Equal(10m, Value(flow, 0));
        Assert.Equal(StudyCoverage.Observed, flow.Coverage);
        Assert.Equal(7m, Assert.Single(profile.Profiles).Total);
        Assert.Equal(7m, profile.Profiles[0].Levels[10]);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Aggregate_history_is_explicitly_estimated_or_unavailable(bool estimate)
    {
        using var model = Model((12, 100, TradeSide.Buy, true));
        var flow = Add(model, Manual(estimate));
        var profile = Add(model, new CompositeVolumeProfile { PriceGrouping = 1, AllowEstimated = estimate });
        var refill = Add(model, new RefillZones { MinimumVolume = 1, PriceGrouping = 1, AbsorptionOnly = false, AllowEstimated = estimate });
        Assert.Equal(estimate, flow.IsAvailable);
        Assert.Equal(estimate ? 100m : 0m, profile.Profiles[0].Total);
        Assert.Equal(estimate ? 1 : 0, refill.Zones.Count);
        Assert.Contains(estimate ? "Estimated" : "missing", flow.Status, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Mixed_aggregate_and_single_observed_row_uses_only_observed_volume_and_warns()
    {
        using var model = Model((10, 100, TradeSide.Buy, true));
        model.Series[0].AddTrade(20, 20, 3, TradeSide.Sell, Start.Ticks, 1);
        var flow = Add(model, Manual(true));
        var profile = Add(model, new CompositeVolumeProfile { PriceGrouping = 1, AllowEstimated = true });
        Assert.Equal(20m, Value(flow, 0)); Assert.Equal(3m, profile.Profiles[0].Total);
        Assert.True(flow.Coverage.HasFlag(StudyCoverage.Incomplete)); Assert.False(flow.Coverage.HasFlag(StudyCoverage.Estimated));
        Assert.Contains("incomplete", flow.Status);
    }
    [Fact]
    public void Absolute_delta_zero_weight_is_unavailable_not_a_zero_price_claim()
    {
        using var model = Model((5, 10, TradeSide.None, false));
        var flow = Add(model, new AnchoredFlowVwap { Mode = AnchoredFlowVwap.AnchorMode.Manual });
        Assert.False(flow.IsAvailable);
    }
    [Fact]
    public void Absolute_price_notional_keeps_negative_price_weights_positive()
    {
        using var model = Model((-10, 2, TradeSide.Buy, false), (-20, 1, TradeSide.Sell, false));
        var flow = Add(model, new AnchoredFlowVwap { Mode = AnchoredFlowVwap.AnchorMode.Manual, Weight = AnchoredFlowVwap.FlowWeight.AbsolutePriceNotional });
        Assert.Equal(-15m, Value(flow, 1)); Assert.True(flow.IsAvailable);
        Assert.Equal(-10m, ((ValueDataSeries)flow.DataSeries[1])[1]);
    }
    [Theory]
    [InlineData("2026-10-01 00:01")] [InlineData("2026-10-01T00:01:00Z")]
    public void Manual_anchor_is_UTC_and_excludes_preceding_bars(string utc)
    {
        using var model = Model((10, 1, TradeSide.Buy, false), (20, 1, TradeSide.Buy, false));
        var flow = Manual(); flow.ManualUtc = utc; Add(model, flow);
        Assert.Equal(1, flow.AnchorBar); Assert.Equal(20m, Value(flow, 1));
    }
    [Fact]
    public void Invalid_nonempty_manual_anchor_is_a_visible_indicator_error()
    {
        using var model = Model((10, 1, TradeSide.Buy, false));
        var flow = Manual(); flow.ManualUtc = "not a UTC date";
        var slot = model.AddIndicator(flow, applyDefaultColors: false);
        Assert.NotNull(slot.Error); Assert.Contains("UTC", slot.Error);
    }
    [Fact]
    public void Empty_manual_anchor_explicitly_reports_loaded_history_start()
    {
        using var model = Model((10, 1, TradeSide.Buy, false)); var flow = Add(model, Manual());
        Assert.Equal(0, flow.AnchorBar); Assert.Contains("explicit loaded-history start", flow.Status);
    }
    [Fact]
    public void Spike_equality_uses_preceding_bars_only_and_replacement_can_remove_anchor()
    {
        using var model = Model((10, 2, TradeSide.Buy, false), (20, 4, TradeSide.Buy, false), (30, 6, TradeSide.Buy, false));
        var flow = Add(model, new AnchoredFlowVwap { Mode = AnchoredFlowVwap.AnchorMode.VolumeSpike, SpikeLookback = 2, SpikeMultiplier = 2, Weight = AnchoredFlowVwap.FlowWeight.Volume });
        Assert.Equal(2, flow.AnchorBar); Assert.Equal(30m, Value(flow, 2));
        Replace(model.Series[2], 30, 5, TradeSide.Buy); flow.HostCalculate(2);
        Assert.Equal(-1, flow.AnchorBar); Assert.False(flow.IsAvailable); Assert.Equal(0m, Value(flow, 2));
    }
    [Theory]
    [InlineData(AnchoredFlowVwap.SwingKind.Low, 10, 5, 11)]
    [InlineData(AnchoredFlowVwap.SwingKind.High, 10, 15, 11)]
    [InlineData(AnchoredFlowVwap.SwingKind.Both, 10, 5, 11)]
    public void Swings_require_closed_right_confirmation_and_paint_retrospectively(AnchoredFlowVwap.SwingKind kind, int a, int pivot, int b)
    {
        using var model = Model((a, 1, TradeSide.Buy, false), (pivot, 1, TradeSide.Buy, false), (b, 1, TradeSide.Buy, false));
        var flow = Add(model, new AnchoredFlowVwap { Mode = AnchoredFlowVwap.AnchorMode.Swing, SwingRadius = 1, Swing = kind, Weight = AnchoredFlowVwap.FlowWeight.Volume });
        Assert.Equal(-1, flow.AnchorBar);
        model.Series[2].IsClosed = true; flow.HostCalculate(2);
        Assert.Equal(1, flow.AnchorBar); Assert.Equal(2, flow.ConfirmationBar); Assert.Equal((decimal)pivot, Value(flow, 1));
        model.Series[2].IsClosed = false; flow.HostCalculate(2);
        Assert.Equal(-1, flow.AnchorBar); Assert.Equal(0m, Value(flow, 1));
    }
    [Fact]
    public void Equal_low_plateau_chooses_earliest_pivot_not_last_equal_low()
    {
        using var model = Model((10, 1, TradeSide.Buy, false), (5, 1, TradeSide.Buy, false), (5, 1, TradeSide.Buy, false), (10, 1, TradeSide.Buy, false));
        model.Series[3].IsClosed = true;
        var flow = Add(model, new AnchoredFlowVwap { Mode = AnchoredFlowVwap.AnchorMode.Swing, SwingRadius = 1, Swing = AnchoredFlowVwap.SwingKind.Low, Weight = AnchoredFlowVwap.FlowWeight.Volume });
        Assert.Equal(1, flow.AnchorBar); Assert.Equal(2, flow.ConfirmationBar);
    }
    [Fact]
    public void Flow_latest_replacement_and_arbitrary_rewind_match_full_replay()
    {
        using var model = Model((10, 2, TradeSide.Buy, false), (20, 3, TradeSide.Sell, false), (30, 1, TradeSide.None, false));
        var flow = Add(model, Manual()); Replace(model.Series[2], 40, 5); flow.HostCalculate(2);
        decimal latest = Value(flow, 2); flow.HostCalculate(2); Assert.Equal(latest, Value(flow, 2));
        Replace(model.Series[0], -10, 4); flow.HostCalculate(0);
        var replay = Add(model, Manual());
        for (int i = 0; i < 3; i++) Assert.Equal(Value(replay, i), Value(flow, i));
        Assert.Equal(replay.Coverage, flow.Coverage);
    }
    [Fact]
    public void Refill_floor_buckets_are_distinct_for_negative_prices()
    {
        using var model = Model((-1, 60, TradeSide.None, false));
        model.Series[0].AddTrade(1, 1, 60, TradeSide.None, Start.Ticks, 1);
        var refill = Add(model, new RefillZones { PriceGrouping = 10, MinimumVolume = 50, AbsorptionOnly = false });
        Assert.Equal(new[] { -10m, 0m }, refill.Zones.Select(z => z.Low).OrderBy(x => x));
        Assert.All(refill.Zones, z => Assert.Equal(60m, z.Volume));
    }
    [Theory]
    [InlineData(52, 48, RefillZones.ZoneDirection.Buyer)]
    [InlineData(51, 49, RefillZones.ZoneDirection.Neutral)]
    public void Refill_dominant_threshold_is_inclusive_and_neutral_is_configurable(int bid, int ask, RefillZones.ZoneDirection expected)
    {
        using var model = Model((0, bid, TradeSide.Sell, false));
        model.Series[0].AddTrade(0, 0, ask, TradeSide.Buy, Start.Ticks, 1);
        model.Series[0].HighTicks = 10; model.Series[0].CloseTicks = 9;
        var refill = Add(model, new RefillZones { PriceGrouping = 1, MinimumVolume = 100, AbsorptionOnly = false });
        Assert.Equal(expected, Assert.Single(refill.Zones).Direction);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Directional_gap_through_mitigates_and_latest_replacement_undoes_it(bool buyer)
    {
        using var model = Model((10, 100, buyer ? TradeSide.Sell : TradeSide.Buy, false), (buyer ? 0 : 30, 1, TradeSide.None, false));
        var first = model.Series[0]; first.LowTicks = buyer ? 10 : 0; first.HighTicks = buyer ? 20 : 10; first.CloseTicks = buyer ? 20 : 0;
        var refill = Add(model, new RefillZones { PriceGrouping = 1, MinimumVolume = 50 });
        var zone = Assert.Single(refill.Zones); Assert.Equal(1, zone.MitigatedBar);
        Replace(model.Series[1], buyer ? 30 : -10, 1); refill.HostCalculate(1);
        Assert.Equal(-1, Assert.Single(refill.Zones).MitigatedBar);
        refill.HostCalculate(1); Assert.Equal(-1, Assert.Single(refill.Zones).MitigatedBar);
        refill.HostCalculate(0); Assert.Equal(-1, Assert.Single(refill.Zones).MitigatedBar);
    }
    [Fact]
    public void Estimated_buckets_conserve_volume_are_capped_and_use_relaxed_threshold()
    {
        using var model = Model((-10003, 1000, TradeSide.Sell, true));
        model.Series[0].HighTicks = 10000; model.Series[0].CloseTicks = 9999;
        var refill = Add(model, new RefillZones { PriceGrouping = 1, MinimumVolume = 0.001m, AbsorptionOnly = false, AllowEstimated = true });
        Assert.InRange(refill.Zones.Count, 1, 200); Assert.Equal(1000m, refill.Zones.Sum(z => z.Volume));
        Assert.All(refill.Zones, z => Assert.Equal(StudyCoverage.Estimated, z.Coverage));
        using var flat = Model((10, 40, TradeSide.None, true));
        Assert.Single(Add(flat, new RefillZones { PriceGrouping = 1, MinimumVolume = 100, AbsorptionOnly = false, AllowEstimated = true }).Zones);
    }
    [Fact]
    public void Lifetime_expiry_is_undone_by_rewind_and_does_not_invent_mitigation()
    {
        using var model = Model((10, 100, TradeSide.Sell, false), (30, 1, TradeSide.None, false), (30, 1, TradeSide.None, false));
        model.Series[0].HighTicks = model.Series[0].CloseTicks = 20;
        var refill = Add(model, new RefillZones { MinimumVolume = 50, PriceGrouping = 1, MaximumLifetime = 1 });
        var zone = Assert.Single(refill.Zones); Assert.Equal(1, zone.ExpiredBar); Assert.Equal(-1, zone.MitigatedBar);
        refill.HostCalculate(0); zone = Assert.Single(refill.Zones); Assert.Equal(1, zone.ExpiredBar); Assert.Equal(-1, zone.MitigatedBar);
    }
    [Fact]
    public void Zero_is_an_available_VWAP_price_not_a_missing_sentinel()
    {
        using var model = Model((0, 1, TradeSide.Buy, false));
        var flow = Add(model, Manual());
        Assert.True(flow.IsAvailable); Assert.Equal(0m, Value(flow, 0));
        Assert.True(((IValueDataSeriesAvailability)flow.DataSeries[0]).IsAvailable(0));
    }
    [Theory]
    [InlineData(7, RefillZones.ZoneDirection.Buyer)]
    [InlineData(8, RefillZones.ZoneDirection.Neutral)]
    public void Lower_edge_35_percent_threshold_is_inclusive(int price, RefillZones.ZoneDirection expected)
    {
        using var model = Model((price, 100, TradeSide.Sell, false));
        model.Series[0].LowTicks = 0; model.Series[0].HighTicks = 20; model.Series[0].CloseTicks = 10;
        var refill = Add(model, new RefillZones { PriceGrouping = 1, MinimumVolume = 100, AbsorptionOnly = false });
        Assert.Equal(expected, Assert.Single(refill.Zones).Direction);
    }
    [Fact]
    public void Neutral_zones_require_overlap_not_directional_gap_through()
    {
        using var model = Model((10, 100, TradeSide.None, false), (0, 1, TradeSide.None, false));
        var refill = Add(model, new RefillZones { PriceGrouping = 1, MinimumVolume = 50, AbsorptionOnly = false });
        Assert.Equal(-1, Assert.Single(refill.Zones).MitigatedBar);
        Replace(model.Series[1], 11, 1); refill.HostCalculate(1);
        Assert.Equal(1, Assert.Single(refill.Zones).MitigatedBar);
    }
    [Theory]
    [InlineData(1024)] [InlineData(4096)]
    public void Flow_and_refill_replay_allocate_linearly_without_per_bar_history_copies(int bars)
    {
        using var model = Model(Enumerable.Range(0, bars).Select(i => ((decimal)i, 1m, TradeSide.Buy, false)).ToArray());
        long before = GC.GetAllocatedBytesForCurrentThread();
        var flow = Add(model, Manual());
        Add(model, new RefillZones { MinimumVolume = 50, PriceGrouping = 1 });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0L, bars * 8192L);
        Assert.InRange(Math.Abs((bars - 1) / 2m - Value(flow, bars - 1)), 0m, 0.000000000000000001m);
    }
    [Fact]
    public void Replacing_a_new_swing_confirmation_restores_prior_retrospective_values_and_line_breaks()
    {
        using var model = Model((10, 1, TradeSide.Buy, false), (11, 1, TradeSide.Buy, false), (5, 1, TradeSide.Buy, false),
            (20, 1, TradeSide.Buy, false), (8, 1, TradeSide.Buy, false), (12, 1, TradeSide.Buy, false));
        model.Series[5].IsClosed = true;
        var flow = Add(model, new AnchoredFlowVwap { SwingRadius = 2, Swing = AnchoredFlowVwap.SwingKind.Both, Weight = AnchoredFlowVwap.FlowWeight.Volume });
        Assert.Equal(3, flow.AnchorBar); Assert.Equal(20m, Value(flow, 3));
        model.Series[5].IsClosed = false; flow.HostCalculate(5);
        Assert.Equal(2, flow.AnchorBar); Assert.Equal(12.5m, Value(flow, 3));
        Assert.False(((ValueDataSeries)flow.DataSeries[0]).IsThisPointOfEndLine(2));
        flow.HostCalculate(5);
        Assert.Equal(12.5m, Value(flow, 3));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Disabled_sigma_bands_are_unavailable_to_renderer_and_scaling_consumers(int bands)
    {
        using var model = Model((10, 1, TradeSide.Buy, false), (20, 1, TradeSide.Buy, false));
        var flow = Manual(); flow.Bands = bands; Add(model, flow);
        for (int i = 0; i < 3; i++)
        {
            var upper = (OptionalValueDataSeries)flow.DataSeries[1 + i * 2];
            var lower = (OptionalValueDataSeries)flow.DataSeries[2 + i * 2];
            Assert.Equal(i < bands, ((IValueDataSeriesAvailability)upper).IsAvailable(1));
            Assert.Equal(i < bands, ((IValueDataSeriesAvailability)lower).IsAvailable(1));
            if (i < bands) { Assert.Equal(15m + 5m * (i + 1), upper[1]); Assert.Equal(15m - 5m * (i + 1), lower[1]); }
            else { Assert.Equal(0m, upper[1]); Assert.Equal(0m, lower[1]); }
        }
        flow.DataSeries[1].Visible = false;
        flow.Bands = 3; model.Update();
        Assert.False(flow.DataSeries[1].Visible); // Band-count changes do not overwrite the user's per-line visibility.
        Assert.True(((IValueDataSeriesAvailability)flow.DataSeries[5]).IsAvailable(1));
        flow.Bands = 0; model.Update();
        Assert.All(flow.DataSeries.Skip(1).Cast<IValueDataSeriesAvailability>(), series => Assert.False(series.IsAvailable(1)));
    }
}
