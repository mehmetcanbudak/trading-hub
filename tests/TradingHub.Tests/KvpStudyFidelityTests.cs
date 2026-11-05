using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public class KvpStudyFidelityTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static ChartModel Model(params Trade[] trades) => ModelWithTick(1, trades);
    private static ChartModel ModelWithTick(decimal tickSize, params Trade[] trades) => ModelWithPeriod(tickSize, ChartPeriod.M1, trades);
    private static ChartModel ModelWithPeriod(decimal tickSize, ChartPeriod period, params Trade[] trades)
    {
        var data = new InstrumentData(new Instrument { Symbol = "FIDELITY-FIXTURE", FeedId = "sim", TickSize = tickSize, VolumeStep = 1 }, new SimulatedFeed());
        foreach (var trade in trades) data.Trades.Add(trade);
        return new ChartModel(data, period, new ChartSettings());
    }
    private static T Add<T>(ChartModel model, T indicator) where T : Indicator { Assert.Null(model.AddIndicator(indicator, applyDefaultColors: false).Error); return indicator; }
    private static OptionalValueDataSeries Series(Indicator indicator, string id) => Assert.IsType<OptionalValueDataSeries>(indicator.DataSeries.Single(x => x.Id == id));
    [Fact]
    public void Measured_compact_footprint_is_usable_without_OHLC_estimation_but_never_becomes_raw()
    {
        var provenance = new TradeProvenance(DataQuality.Compact, 900, "compact-fixture");
        using var model = ModelWithPeriod(1, ChartPeriod.M15, new Trade(1, Start, 10, 8, TradeSide.Buy, provenance), new Trade(2, Start.AddSeconds(1), 20, 2, TradeSide.Sell, provenance), new Trade(3, Start.AddDays(1), 30, 1, TradeSide.Buy, provenance));
        var profileStudy = Add(model, new CompositeVolumeProfile { PriceGrouping = 1, ComposeProfiles = false }); var profile = profileStudy.Profiles[0];
        Assert.Equal(10m, profile.Total); Assert.Equal(10m, profile.Poc);
        Assert.True(profile.Coverage.HasFlag(StudyCoverage.Compact)); Assert.False(profile.Coverage.HasFlag(StudyCoverage.Observed)); Assert.False(profile.Coverage.HasFlag(StudyCoverage.Estimated));
        Assert.Equal(900, profile.ResolutionSeconds);
        var vwap = Add(model, new MultiPeriodVwap()); Assert.Equal(12m, Series(vwap, "MultiVwap.Day")[0]);
        var cvd = Add(model, new CumulativeDeltaCandles()); Assert.Equal(6m, Series(cvd, "Value")[0]); Assert.True(cvd.Coverage.HasFlag(StudyCoverage.Compact));
    }
    [Fact]
    public void Inferred_or_unknown_aggressors_are_missing_not_balanced_flow_and_a_reset_recovers()
    {
        using var model = Model(new Trade(1, Start, 10, 5, TradeSide.Buy, new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide)),
            new Trade(2, Start.AddMinutes(1), 11, 2, TradeSide.Buy), new Trade(3, Start.AddDays(1), 12, 3, TradeSide.Buy));
        var cvd = Add(model, new CumulativeDeltaCandles { Reset = CumulativeDeltaCandles.ResetPeriod.Session });
        Assert.False(Series(cvd, "Value").IsAvailable(0)); Assert.False(Series(cvd, "Value").IsAvailable(1));
        Assert.True(Series(cvd, "Value").IsAvailable(2)); Assert.Equal(3m, Series(cvd, "Value")[2]);
        var explicitEstimate = Add(model, new CumulativeDeltaCandles { AllowInferredAggressors = true });
        Assert.Equal(5m, Series(explicitEstimate, "Value")[0]); Assert.True(explicitEstimate.Coverage.HasFlag(StudyCoverage.InferredSide));
        using var unknown = Model(new Trade(1, Start, 10, 3, TradeSide.None));
        Assert.False(Series(Add(unknown, new CumulativeDeltaCandles()), "Value").IsAvailable(0));
    }
    [Fact]
    public void Synthetic_OHLC_only_contributes_when_explicitly_enabled_and_never_acquires_side_POCs()
    {
        using var model = Model(new Trade(-1, Start, 10, 100, TradeSide.Buy));
        var profile = Add(model, new CompositeVolumeProfile { PriceGrouping = 1 }); Assert.Equal(0m, Assert.Single(profile.Profiles).Total);
        profile.AllowEstimated = true; model.Update(); var estimated = Assert.Single(profile.Profiles);
        Assert.Equal(100m, estimated.Total); Assert.True(estimated.Coverage.HasFlag(StudyCoverage.Estimated)); Assert.False(estimated.HasBuyerPoc); Assert.False(estimated.HasSellerPoc);
        Assert.False(Series(Add(model, new CumulativeDeltaCandles()), "Value").IsAvailable(0));
    }
    [Fact]
    public void Marginal_POCs_outer_value_area_and_display_transforms_preserve_measured_totals()
    {
        using var model = Model(new Trade(1, Start, -1, 1, TradeSide.Buy), new Trade(2, Start.AddSeconds(1), 0, 5, TradeSide.Sell), new Trade(3, Start.AddSeconds(2), 1, 8, TradeSide.Buy), new Trade(4, Start.AddSeconds(3), 2, 1, TradeSide.Sell));
        var study = Add(model, new CompositeVolumeProfile { PriceGrouping = 1, ComposeProfiles = false, ValueAreaPercent = 70, OuterValueAreaPercent = 90 });
        var profile = Assert.Single(study.Profiles); Assert.Equal(1m, profile.BuyerPoc); Assert.Equal(0m, profile.SellerPoc); Assert.True(profile.HasSellerPoc);
        Assert.Equal(0m, profile.ValueAreaLow); Assert.Equal(1m, profile.ValueAreaHigh); Assert.Equal(0m, profile.OuterValueAreaLow); Assert.Equal(2m, profile.OuterValueAreaHigh); Assert.Equal(2m, profile.TailRatio);
        study.DisplayVolumeTransform = CompositeVolumeProfile.VolumeTransform.MedianCap; study.DisplayCapMultiple = 1; model.Update();
        Assert.Equal(15m, Assert.Single(study.Profiles).Total); Assert.Equal(1m, study.Profiles[0].Poc);
        study.DisplayVolumeTransform = CompositeVolumeProfile.VolumeTransform.Logarithmic; model.Update(); Assert.Equal(15m, Assert.Single(study.Profiles).Levels.Values.Sum());
    }
    [Fact]
    public void Rolling_calendar_month_is_not_thirty_days_and_current_replacement_is_bounded()
    {
        using var model = Model(new Trade(1, new DateTime(2026, 2, 27, 0, 0, 0, DateTimeKind.Utc), 100, 1, TradeSide.Buy),
            new Trade(2, new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc), 20, 1, TradeSide.Buy), new Trade(3, new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc), 30, 1, TradeSide.Buy));
        var study = Add(model, new RollingVwap { Window = RollingVwap.WindowKind.CalendarMonths, Length = 1 });
        Assert.Equal(25m, Series(study, "RollingVwap")[2]);
        model.Series[2].Levels.Clear(); model.Series[2].Volume = model.Series[2].Bid = model.Series[2].Ask = model.Series[2].Between = 0;
        model.Series[2].AddTrade(40, 40, 1, TradeSide.Buy, model.Series[2].Time.Ticks, 0); study.HostCalculate(2);
        long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 256; i++) study.HostCalculate(2);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256 * 2048L); Assert.Equal(30m, Series(study, "RollingVwap")[2]);
    }
    [Fact]
    public void Prior_zero_VWAP_is_available_only_in_next_period_and_yearly_POC_keeps_partial_source_visible()
    {
        using var model = Model(new Trade(1, new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), 0, 3, TradeSide.Buy), new Trade(2, Start, 10, 2, TradeSide.Buy));
        var prior = Add(model, new PriorClosingVwap()); Assert.False(Series(prior, "PriorVwap").IsAvailable(0)); Assert.True(Series(prior, "PriorVwap").IsAvailable(1)); Assert.Equal(0m, Series(prior, "PriorVwap")[1]);
        var naked = Add(model, new NakedPoc()); var year = Assert.Single(naked.Levels, x => x.Period == StudyTimeFrame.Year);
        Assert.Equal(0m, year.Price); Assert.Equal(1, year.AvailableBar); Assert.True(year.Coverage.HasFlag(StudyCoverage.Partial)); Assert.Equal(-1, year.TouchBar);
    }
    [Fact]
    public void Claire_uses_completed_daily_bar_HLC3_not_claimed_tick_VWAP_and_does_not_change_intraday()
    {
        using var model = Model(new Trade(1, Start, 10, 1, TradeSide.Buy), new Trade(2, Start.AddMinutes(1), 20, 3, TradeSide.Buy), new Trade(3, Start.AddDays(1), 100, 1, TradeSide.Buy));
        var study = Add(model, new VwapClaire()); var line = Series(study, "Claire.R30"); Assert.False(line.IsAvailable(1));
        Assert.InRange(Math.Abs(50m / 3 - line[2]), 0, 0.000000000000000000000001m); Assert.NotEqual(17.5m, line[2]);
        decimal before = line[2]; model.Series[2].HighTicks = 10000; study.HostCalculate(2); Assert.Equal(before, line[2]);
    }
    [Fact]
    public void Overnight_window_follows_both_DST_fallback_hours_and_closes_at_local_end()
    {
        using var model = Model(new Trade(1, new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), 10, 1, TradeSide.Buy),
            new Trade(2, new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), 20, 1, TradeSide.Buy), new Trade(3, new DateTime(2026, 11, 1, 7, 0, 0, DateTimeKind.Utc), 30, 1, TradeSide.Buy));
        var study = Add(model, new TimedSessionVwap { TimeZoneId = "America/New_York", StartLocal = TimeSpan.FromHours(20), EndLocal = TimeSpan.FromHours(2) });
        Assert.Equal(10m, Series(study, "TimedSessionVwap")[0]); Assert.Equal(15m, Series(study, "TimedSessionVwap")[1]); Assert.False(Series(study, "TimedSessionVwap").IsAvailable(2));
    }
    [Fact]
    public void Completed_profile_cache_recomputes_same_count_same_volume_price_distribution_correction()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tradinghub-profile-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var model = Model(new Trade(1, Start, 10, 5, TradeSide.Buy), new Trade(2, Start.AddSeconds(1), 20, 5, TradeSide.Buy), new Trade(3, Start.AddDays(1), 50, 1, TradeSide.Buy));
            var study = Add(model, new CompositeVolumeProfile { ComposeProfiles = false, PriceGrouping = 1, CompletedCacheDirectory = directory });
            Assert.Equal(10m, study.Profiles[0].Poc);
            var candle = model.Series[0]; candle.Levels.Clear(); candle.Trades = 0; candle.Volume = candle.Bid = candle.Ask = candle.Between = 0;
            candle.AddTrade(30, 30, 5, TradeSide.Buy, candle.Time.Ticks, 0); candle.AddTrade(20, 20, 5, TradeSide.Buy, candle.Time.Ticks, 0);
            study.HostCalculate(0); Assert.Equal(10m, study.Profiles[0].Total); Assert.Equal(20m, study.Profiles[0].Poc); Assert.Equal(20m, study.Profiles[0].BuyerPoc);
            foreach (string path in Directory.GetFiles(directory, "*.studycache")) File.WriteAllText(path, "{bad cache");
            var fresh = Add(model, new CompositeVolumeProfile { ComposeProfiles = false, PriceGrouping = 1, CompletedCacheDirectory = directory });
            Assert.Equal(20m, fresh.Profiles[0].Poc); Assert.Equal(10m, fresh.Profiles[0].Total);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void Sparse_value_area_uses_logical_empty_rows_not_adjacent_occupied_neighbors()
    {
        using var model = Model(new Trade(1, Start, -10, 9, TradeSide.None), new Trade(2, Start.AddSeconds(1), 0, 10, TradeSide.None), new Trade(3, Start.AddSeconds(2), 1, 8, TradeSide.None));
        var profile = Assert.Single(Add(model, new CompositeVolumeProfile { PriceGrouping = 1, ValueAreaPercent = 70 }).Profiles);
        Assert.Equal(0m, profile.Poc); Assert.Equal(-10m, profile.ValueAreaLow); Assert.Equal(1m, profile.ValueAreaHigh);
        var session = Add(model, new SessionVolumeProfile()); Assert.Equal(27m, session.LatestTotal); Assert.Equal(1m, session.LatestValueAreaHigh);
    }
    [Fact]
    public void Sparse_extreme_signed_prices_remain_row_bounded_and_nonunit_ticks_keep_grouping()
    {
        using var sparse = Model(new Trade(1, Start, -1000000000000m, 3, TradeSide.Buy), new Trade(2, Start.AddSeconds(1), 0, 5, TradeSide.Buy), new Trade(3, Start.AddSeconds(2), 1000000000000m, 2, TradeSide.Buy));
        var extremes = Assert.Single(Add(sparse, new CompositeVolumeProfile { PriceGrouping = 1 }).Profiles);
        Assert.Equal(10m, extremes.Total); Assert.Equal(3, extremes.Levels.Count); Assert.Equal(0m, extremes.Poc);
        using var quarterTick = ModelWithTick(0.25m, new Trade(1, Start, -1.25m, 3, TradeSide.Buy), new Trade(2, Start.AddSeconds(1), -0.75m, 5, TradeSide.Sell), new Trade(3, Start.AddSeconds(2), 0.25m, 2, TradeSide.Buy));
        var study = Add(quarterTick, new CompositeVolumeProfile { PriceGrouping = 0.5m }); var profile = Assert.Single(study.Profiles);
        Assert.Equal(0.5m, study.EffectivePriceGrouping); Assert.Equal(10m, profile.Total); Assert.Equal(new[] { -1.5m, -1m, 0.5m }, profile.Levels.Keys.Order());
    }
    [Fact]
    public void Big_trades_excludes_positive_id_compact_packets_and_inferred_side_prints()
    {
        using var model = Model(new Trade(1, Start, 10, 1, TradeSide.Buy), new Trade(2, Start.AddMinutes(1), 10, 100, TradeSide.Buy, new TradeProvenance(DataQuality.Compact, 60)),
            new Trade(3, Start.AddMinutes(2), 10, 20, TradeSide.Buy), new Trade(4, Start.AddMinutes(3), 10, 200, TradeSide.Buy, new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide)));
        var study = Add(model, new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 10 }); model.Update();
        Assert.Equal(1, study.BubbleCount); Assert.Equal(10m, study.CurrentThreshold);
    }
    [Fact]
    public void Robust_volume_warmup_uses_preceding_bars_and_distinguishes_zero_MAD_spike()
    {
        using var model = Model(Enumerable.Range(0, 7).Select(i => new Trade(i + 1, Start.AddMinutes(i), 10, i == 5 ? 100 : 10, TradeSide.Buy)).ToArray());
        var study = Add(model, new RobustVolume { Lookback = 5 });
        Assert.False(Series(study, "RobustVolume.Score").IsAvailable(4)); Assert.Equal(decimal.MaxValue, Series(study, "RobustVolume.Score")[5]);
        Assert.Equal(0m, Series(study, "RobustVolume.Score")[6]);
    }
    [Theory]
    [InlineData(DoubleInsideDay.BreakoutConfirmation.ChartClose, 3)]
    [InlineData(DoubleInsideDay.BreakoutConfirmation.DailyClose, 4)]
    public void Double_inside_day_waits_for_two_completed_inside_days_and_selected_breakout_close(DoubleInsideDay.BreakoutConfirmation confirmation, int available)
    {
        using var model = Model(Enumerable.Range(0, 5).Select(i => new Trade(i + 1, Start.AddDays(i), i == 0 ? 15 : i < 3 ? 14 : 21, 1, TradeSide.Buy)).ToArray());
        long[] opens = { 10, 12, 13, 14, 21 }, highs = { 20, 18, 17, 22, 22 }, lows = { 0, 2, 3, 10, 20 };
        for (int i = 0; i < 5; i++) { model.Series[i].OpenTicks = opens[i]; model.Series[i].HighTicks = highs[i]; model.Series[i].LowTicks = lows[i]; }
        var study = Add(model, new DoubleInsideDay { Confirmation = confirmation });
        var pattern = Assert.Single(study.Patterns); Assert.Equal(3, pattern.AvailableBar); Assert.Equal(1, pattern.State);
        Assert.Equal(0m, Series(study, "DoubleInsideDay.Signal")[2]); Assert.Equal(1m, Series(study, "DoubleInsideDay.Signal")[available]);
        study.HostCalculate(4); study.HostCalculate(4); Assert.Single(study.Patterns);
    }
    [Fact]
    public void Coarse_compact_buckets_remain_visible_profiles_but_cannot_be_finer_strategy_facts()
    {
        using var model = Model(new Trade(1, Start, 10, 8, TradeSide.Buy, new TradeProvenance(DataQuality.Compact, 900)),
            new Trade(2, Start.AddSeconds(1), 20, 2, TradeSide.Sell, new TradeProvenance(DataQuality.Compact, 900)), new Trade(3, Start.AddMinutes(15), 30, 1, TradeSide.Buy));
        var profile = Assert.Single(Add(model, new CompositeVolumeProfile { PriceGrouping = 1 }).Profiles);
        Assert.Equal(11m, profile.Total); Assert.False(profile.IsStrategyAvailable); Assert.Equal(900, profile.ResolutionSeconds);
        var vwap = Add(model, new MultiPeriodVwap()); Assert.False(Series(vwap, "MultiVwap.Day").IsAvailable(0));
        var cvd = Add(model, new CumulativeDeltaCandles()); Assert.False(Series(cvd, "Value").IsAvailable(0));
        var delta = Add(model, new DeltaBars()); Assert.False(Series(delta, "Delta").IsAvailable(0));
    }
    [Theory]
    [InlineData("2026-03-07T13:59:00Z", "2026-03-07T14:00:00Z", "2026-03-08T13:00:00Z", RollingVwap.WindowKind.CalendarDays)]
    [InlineData("2026-03-08T07:29:00Z", "2026-03-08T07:30:00Z", "2026-03-09T06:30:00Z", RollingVwap.WindowKind.CalendarDays)]
    [InlineData("2026-11-01T06:29:00Z", "2026-11-01T06:30:00Z", "2026-11-02T06:30:00Z", RollingVwap.WindowKind.CalendarDays)]
    [InlineData("2026-11-01T05:29:00Z", "2026-11-01T05:30:00Z", "2027-11-01T05:30:00Z", RollingVwap.WindowKind.CalendarYears)]
    [InlineData("2026-03-01T00:29:00Z", "2026-03-01T00:30:00Z", "2026-03-31T23:30:00Z", RollingVwap.WindowKind.CalendarMonths)]
    [InlineData("2023-03-01T00:29:00Z", "2023-03-01T00:30:00Z", "2024-03-01T00:30:00Z", RollingVwap.WindowKind.CalendarYears)]
    public void Rolling_exchange_calendar_preserves_local_clock_and_explicit_gap_fold_and_leap_rules(string before, string boundary, string current, RollingVwap.WindowKind kind)
    {
        DateTime Utc(string text) => DateTime.Parse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        var session = new SessionSettings { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Start = TimeSpan.FromHours(18) };
        using var model = ModelWithSession(session, new Trade(1, Utc(before), 100, 1, TradeSide.Buy),
            new Trade(2, Utc(boundary), 20, 1, TradeSide.Buy), new Trade(3, Utc(current), 30, 1, TradeSide.Buy));
        var study = Add(model, new RollingVwap { Window = kind, Length = 1 });
        Assert.True(Series(study, "RollingVwap").IsAvailable(2)); Assert.Equal(25m, Series(study, "RollingVwap")[2]);
        Assert.False(study.Coverage.HasFlag(StudyCoverage.Partial));
    }
    [Fact]
    public void Key_levels_exclude_partial_prior_period_and_publish_complete_following_session()
    {
        using var model = Model(new Trade(1, Start.AddHours(12), 100, 1, TradeSide.Buy),
            new Trade(2, Start.AddDays(1), 20, 1, TradeSide.Buy), new Trade(3, Start.AddDays(1).AddMinutes(1), 30, 1, TradeSide.Buy),
            new Trade(4, Start.AddDays(2), 40, 1, TradeSide.Buy));
        var study = Add(model, new KeyLevels());
        Assert.False(Series(study, "KeyLevels.Day.Open").IsAvailable(0));
        Assert.False(Series(study, "KeyLevels.Day.PriorHigh").IsAvailable(1));
        Assert.Equal(20m, Series(study, "KeyLevels.Day.Open")[1]);
        Assert.True(Series(study, "KeyLevels.Day.PriorHigh").IsAvailable(3));
        Assert.Equal(30m, Series(study, "KeyLevels.Day.PriorHigh")[3]);
        Assert.Equal(20m, Series(study, "KeyLevels.Day.PriorLow")[3]);
        Assert.Equal(25m, Series(study, "KeyLevels.Day.PriorMid")[3]);
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(12, false)]
    public void Monday_range_is_unavailable_until_complete_and_rejects_a_partial_loaded_Monday(int firstHour, bool available)
    {
        var monday = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        using var model = Model(new Trade(1, monday.AddHours(firstHour), 10, 1, TradeSide.Buy),
            new Trade(2, monday.AddHours(firstHour).AddMinutes(1), 20, 1, TradeSide.Buy), new Trade(3, monday.AddDays(1), 30, 1, TradeSide.Buy));
        var study = Add(model, new KeyLevels());
        Assert.False(Series(study, "KeyLevels.Monday.High").IsAvailable(1));
        Assert.Equal(available, Series(study, "KeyLevels.Monday.High").IsAvailable(2));
        if (available) { Assert.Equal(20m, Series(study, "KeyLevels.Monday.High")[2]); Assert.Equal(10m, Series(study, "KeyLevels.Monday.Low")[2]); }
    }
    [Fact]
    public void Session_drawing_range_is_a_prefix_snapshot_not_a_viewport_or_future_extreme()
    {
        using var model = Model(new Trade(1, Start.AddHours(9.5), 10, 1, TradeSide.Buy),
            new Trade(2, Start.AddHours(9.5).AddMinutes(1), 20, 1, TradeSide.Buy), new Trade(3, Start.AddHours(9.5).AddMinutes(2), 5, 1, TradeSide.Buy),
            new Trade(4, Start.AddHours(16), 100, 1, TradeSide.Buy));
        var study = Add(model, new SessionBackgrounds { TimeZoneId = "UTC", FirstStart = TimeSpan.FromHours(9.5), FirstEnd = TimeSpan.FromHours(16) });
        var prefix = study.DrawingRangeAt(0, 1)!.Value; Assert.Equal(10m, prefix.Open); Assert.Equal(20m, prefix.High); Assert.Equal(10m, prefix.Low); Assert.False(prefix.Partial);
        var complete = study.DrawingRangeAt(0, 2)!.Value; Assert.Equal(0, complete.SourceStartBar); Assert.Equal(10m, complete.Open); Assert.Equal(20m, complete.High); Assert.Equal(5m, complete.Low);
        Assert.Null(study.DrawingRangeAt(0, 3)); study.FirstStart = TimeSpan.FromHours(9.5).Add(TimeSpan.FromMinutes(1)); model.Update();
        Assert.Null(study.DrawingRangeAt(0, 0)); Assert.Equal(20m, study.DrawingRangeAt(0, 1)!.Value.Open);
    }
    [Fact]
    public void Robust_score_does_not_turn_an_incompatible_current_bucket_into_a_spike()
    {
        var trades = Enumerable.Range(0, 6).Select(i => new Trade(i + 1, Start.AddMinutes(i), 10, 10, TradeSide.Buy)).ToList();
        trades.Add(new Trade(7, Start.AddMinutes(6), 10, 1000, TradeSide.Buy, new TradeProvenance(DataQuality.Compact, 900)));
        using var model = Model(trades.ToArray()); var study = Add(model, new RobustVolume { Lookback = 5 });
        Assert.True(Series(study, "RobustVolume.Score").IsAvailable(5)); Assert.False(Series(study, "RobustVolume.Score").IsAvailable(6));
    }
    [Fact]
    public void A_partial_loaded_mother_day_cannot_create_a_double_inside_day()
    {
        using var model = Model(Enumerable.Range(0, 4).Select(i => new Trade(i + 1, Start.AddDays(i).AddHours(i == 0 ? 12 : 0), i == 0 ? 15 : i < 3 ? 14 : 21, 1, TradeSide.Buy)).ToArray());
        long[] highs = { 20, 18, 17, 22 }, lows = { 0, 2, 3, 10 };
        for (int i = 0; i < 4; i++) { model.Series[i].HighTicks = highs[i]; model.Series[i].LowTicks = lows[i]; }
        var study = Add(model, new DoubleInsideDay()); Assert.Empty(study.Patterns); Assert.False(Series(study, "DoubleInsideDay.Signal").IsAvailable(2));
    }
    private static ChartModel ModelWithSession(SessionSettings session, params Trade[] trades)
    {
        var data = new InstrumentData(new Instrument { Symbol = "SESSION-FIDELITY-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 }, new SessionFeed(session));
        foreach (var trade in trades) data.Trades.Add(trade); return new ChartModel(data, ChartPeriod.M1, new ChartSettings());
    }
    private sealed class SessionFeed(SessionSettings session) : IMarketDataFeed, ISessionSettingsFeed
    {
        private readonly SimulatedFeed _inner = new();
        public string Id => "sim"; public string DisplayName => "Session fidelity fixture"; public bool SupportsHistory => true; public bool IsLive => false;
        public SessionSettings GetSessionSettings(Instrument instrument) => session;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => _inner.GetInstrumentsAsync(ct);
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => _inner.LoadTradesAsync(instrument, fromUtc, toUtc, fromId, onBatch, progress, ct);
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => _inner.Subscribe(instrument, sink);
        public void Dispose() => _inner.Dispose();
    }
    [Fact]
    public void Weighted_merge_and_real_VWAP_handle_representable_small_weight_wide_spread()
    {
        var first = new WeightedStudyMoments(); first.Add(0, 0.01m);
        var second = new WeightedStudyMoments(); second.Add(300000000000000m, 0.01m); first.Merge(second);
        Assert.Equal(0.02m, first.Weight); Assert.Equal(150000000000000m, first.Mean);
        Assert.Equal(450000000000000000000000000m, first.M2);
        using var model = Model(new Trade(1, Start, 0, 0.01m, TradeSide.Buy), new Trade(2, Start.AddMinutes(1), 300000000000000m, 0.01m, TradeSide.Buy));
        var study = Add(model, new MultiPeriodVwap()); Assert.True(Series(study, "MultiVwap.Day").IsAvailable(1)); Assert.Equal(first.Mean, Series(study, "MultiVwap.Day")[1]);
    }
    [Fact]
    public void Known_zero_volume_bar_does_not_poison_the_rest_of_a_VWAP_period()
    {
        using var model = Model(new Trade(1, Start, 10, 1, TradeSide.Buy), new Trade(2, Start.AddMinutes(1), 20, 0, TradeSide.Buy), new Trade(3, Start.AddMinutes(2), 30, 1, TradeSide.Buy));
        var multi = Add(model, new MultiPeriodVwap()); var session = Add(model, new SessionVwap { AllowEstimated = false });
        Assert.True(Series(multi, "MultiVwap.Day").IsAvailable(2)); Assert.Equal(20m, Series(multi, "MultiVwap.Day")[2]);
        var line = Assert.IsType<OptionalValueDataSeries>(session.DataSeries[0]);
        Assert.True(line.IsAvailable(2)); Assert.Equal(20m, line[2]); Assert.False(multi.Coverage.HasFlag(StudyCoverage.MissingBars));
    }
    [Fact]
    public void Session_profile_tick_and_target_rows_conserve_volume_on_signed_nonunit_prices()
    {
        using var model = ModelWithTick(0.25m, new Trade(1, Start, -1.25m, 2, TradeSide.Buy), new Trade(2, Start.AddSeconds(1), -1m, 3, TradeSide.Sell), new Trade(3, Start.AddSeconds(2), 0.5m, 5, TradeSide.Buy));
        var study = Add(model, new SessionVolumeProfile { TicksPerRow = 2 });
        Assert.Equal(0.5m, study.LatestRowStep); Assert.Equal(3, study.LatestRowCount); Assert.Equal(10m, study.LatestTotal); Assert.Equal(0.5m, study.LatestPoc);
        study.Rows = SessionVolumeProfile.RowSizing.TargetCount; study.RowCount = 2; model.Update();
        Assert.Equal(1.75m, study.LatestRowStep); Assert.Equal(2, study.LatestRowCount); Assert.Equal(10m, study.LatestTotal); Assert.Equal(-1.75m, study.LatestPoc);
    }
    [Fact]
    public void Session_profile_marks_an_omitted_synthetic_bar_in_an_otherwise_observed_period()
    {
        using var model = Model(new Trade(1, Start, 10, 3, TradeSide.Buy), new Trade(-1, Start.AddMinutes(1), 20, 100, TradeSide.None, new TradeProvenance(DataQuality.Synthetic, 60)));
        var study = Add(model, new SessionVolumeProfile()); Assert.Equal(3m, study.LatestTotal);
        Assert.True(study.Coverage.HasFlag(StudyCoverage.Observed)); Assert.True(study.Coverage.HasFlag(StudyCoverage.MissingBars));
    }
    [Fact]
    public void Timed_and_prior_VWAP_export_partial_and_estimated_fidelity_without_wording_pins()
    {
        using var model = Model(new Trade(1, Start.AddHours(12), 10, 1, TradeSide.Buy), new Trade(2, Start.AddDays(1).AddHours(12), 20, 1, TradeSide.Buy));
        using var prefix = Model(new Trade(1, Start.AddHours(12), 10, 1, TradeSide.Buy));
        var timed = Add(prefix, new TimedSessionVwap { TimeZoneId = "UTC", StartLocal = TimeSpan.FromHours(9), EndLocal = TimeSpan.FromHours(16), Source = StudyVwapSource.BarHlc3 });
        var prior = Add(model, new PriorClosingVwap { Source = StudyVwapSource.BarHlc3 });
        Assert.True(prior.Coverage.HasFlag(StudyCoverage.Partial)); Assert.True(prior.Coverage.HasFlag(StudyCoverage.Estimated));
        Assert.True(timed.Coverage.HasFlag(StudyCoverage.Partial)); Assert.True(timed.Coverage.HasFlag(StudyCoverage.Estimated));
    }
    [Fact]
    public void Pure_partial_synthetic_volume_is_not_a_robust_spike()
    {
        var trades = Enumerable.Range(0, 6).Select(i => new Trade(i + 1, Start.AddMinutes(i), 10, 10, TradeSide.Buy)).ToList();
        trades.Add(new Trade(-1, Start.AddMinutes(6), 10, 1000, TradeSide.Buy, new TradeProvenance(DataQuality.Synthetic | DataQuality.Partial, 60)));
        using var model = Model(trades.ToArray()); var study = Add(model, new RobustVolume { Lookback = 5 });
        Assert.False(Series(study, "RobustVolume.Score").IsAvailable(6));
    }
    [Fact]
    public void Naked_POC_replacing_a_forming_bar_does_not_clone_four_growing_display_profiles()
    {
        using var model = Model(Enumerable.Range(0, 2000).Select(i => new Trade(i + 1, Start.AddMinutes(i), i, 1, TradeSide.Buy)).ToArray());
        var study = Add(model, new NakedPoc()); int last = model.Series.Count - 1;
        for (int i = 0; i < 8; i++) study.HostCalculate(last);
        long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 64; i++) study.HostCalculate(last);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 16384);
    }
    [Fact]
    public void Primary_PO3_remains_usable_when_only_the_secondary_FVG_layer_is_unresolvable()
    {
        using var model = ModelWithPeriod(1, ChartPeriod.M15, Enumerable.Range(0, 9).Select(i => new Trade(i + 1, Start.AddMinutes(i * 15), i < 4 ? 11 : i < 8 ? 10 : 12, 1, TradeSide.Buy)).ToArray());
        for (int i = 0; i < 8; i++) { model.Series[i].HighTicks = i < 4 ? 12 : 11; model.Series[i].LowTicks = i < 4 ? 9 : 8; }
        var study = Add(model, new HtfStructure { UseMappedPeriods = false, FirstPeriod = StudyTimeFrame.Hour, SecondPeriod = StudyTimeFrame.Minutes5 });
        Assert.Single(study.Patterns); Assert.True(Series(study, "HtfStructure.PO3").IsAvailable(8)); Assert.Equal(1m, Series(study, "HtfStructure.PO3")[8]);
        var unresolved = Add(model, new HtfStructure { UseMappedPeriods = false, FirstPeriod = StudyTimeFrame.Minutes2, SecondPeriod = StudyTimeFrame.Minutes5 });
        Assert.False(Series(unresolved, "HtfStructure.PO3").IsAvailable(8));
    }
    [Fact]
    public void Pure_partial_synthetic_session_ranges_and_confirmation_bars_remain_incomplete()
    {
        var quality = new TradeProvenance(DataQuality.Synthetic | DataQuality.Partial, 60);
        using var rangeModel = Model(new Trade(-1, Start.AddHours(9), 10, 100, TradeSide.Buy, quality));
        var ranges = Add(rangeModel, new SessionBackgrounds { TimeZoneId = "UTC", FirstStart = TimeSpan.FromHours(9), FirstEnd = TimeSpan.FromHours(16) });
        Assert.True(ranges.DrawingRangeAt(0, 0)!.Value.Partial);
        var trades = Enumerable.Range(0, 5).Select(i => new Trade(i + 1, Start.AddDays(i), i == 0 ? 15 : i < 3 ? 14 : 21, 1, TradeSide.Buy)).ToArray();
        trades[3] = new Trade(-1, Start.AddDays(3), 21, 1, TradeSide.Buy, quality);
        using var model = Model(trades); long[] highs = { 20, 18, 17, 22, 22 }, lows = { 0, 2, 3, 10, 20 };
        for (int i = 0; i < 5; i++) { model.Series[i].HighTicks = highs[i]; model.Series[i].LowTicks = lows[i]; }
        var inside = Add(model, new DoubleInsideDay());
        Assert.Equal(DoubleInsideDay.CoverageGapState, Assert.Single(inside.Patterns).State);
        Assert.False(Series(inside, "DoubleInsideDay.Signal").IsAvailable(3));
    }
    [Fact]
    public void Completed_profile_cache_keeps_same_symbol_identical_distributions_in_different_providers_separate()
    {
        string path = Path.Combine(Path.GetTempPath(), "tradinghub-provider-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string provider in new[] { "provider-a", "provider-b" })
            {
                var data = new InstrumentData(new Instrument { Symbol = "SAME-SYMBOL", FeedId = provider, TickSize = 1, VolumeStep = 1 }, new SimulatedFeed());
                var provenance = new TradeProvenance(DataQuality.Raw, SourceIdentity: provider);
                data.Trades.Add(new Trade(1, Start, 10, 5, TradeSide.Buy, provenance)); data.Trades.Add(new Trade(2, Start.AddDays(1), 20, 1, TradeSide.Buy, provenance));
                using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
                var study = Add(model, new CompositeVolumeProfile { PriceGrouping = 1, ComposeProfiles = false, CompletedCacheDirectory = path });
                Assert.Equal(10m, study.Profiles[0].Poc);
            }
            Assert.Equal(2, Directory.GetFiles(path, "*.studycache").Length);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact]
    public void Persisted_false_daily_switch_does_not_erase_an_explicit_non_daily_reset_period()
    {
        var study = new CumulativeDeltaCandles { Reset = CumulativeDeltaCandles.ResetPeriod.Quarter, ResetDaily = false };
        Assert.Equal(CumulativeDeltaCandles.ResetPeriod.Quarter, study.Reset);
        study.ResetDaily = true; Assert.Equal(CumulativeDeltaCandles.ResetPeriod.Session, study.Reset);
        study.ResetDaily = false; Assert.Equal(CumulativeDeltaCandles.ResetPeriod.Never, study.Reset);
    }
    [Fact]
    public void Smallest_positive_decimal_weights_keep_representable_second_moments_in_Add_and_Merge()
    {
        const decimal unit = 0.0000000000000000000000000001m;
        var added = new WeightedStudyMoments(); added.Add(0, unit); added.Add(2, unit);
        Assert.Equal(2 * unit, added.Weight); Assert.Equal(1m, added.Mean); Assert.Equal(2 * unit, added.M2); Assert.Equal(1m, added.Deviation);
        var merged = new WeightedStudyMoments(); merged.Add(0, unit);
        var other = new WeightedStudyMoments(); other.Add(2, unit); merged.Merge(other);
        Assert.Equal(added.Mean, merged.Mean); Assert.Equal(added.M2, merged.M2); Assert.Equal(added.Deviation, merged.Deviation);
        using var model = Model(new Trade(1, Start, 0, unit, TradeSide.Buy), new Trade(2, Start.AddMinutes(1), 2, unit, TradeSide.Buy));
        var study = Add(model, new MultiPeriodVwap()); Assert.True(Series(study, "MultiVwap.Day").IsAvailable(1)); Assert.Equal(1m, Series(study, "MultiVwap.Day")[1]);
    }
    [Fact]
    public void Tiny_price_spread_and_large_weights_do_not_round_the_spread_to_zero_before_weighting()
    {
        const decimal unit = 0.0000000000000000000000000001m, weight = 30000000000000000000000000000m;
        var moments = new WeightedStudyMoments(); moments.Add(0, weight); moments.Add(unit, weight);
        Assert.Equal(2 * unit, moments.M2);
    }
    [Fact]
    public void Unknown_availability_rows_remain_a_labelled_drawing_summary_not_ordinary_observed_timing()
    {
        using var model = Model(new Trade(1, Start, 10, 3, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, HasKnownAvailability: false)));
        var study = Add(model, new SessionVolumeProfile());
        Assert.Equal(3m, study.LatestTotal); Assert.Equal(10m, study.LatestPoc);
        Assert.True(study.Coverage.HasFlag(StudyCoverage.Observed)); Assert.True(study.Coverage.HasFlag(StudyCoverage.MissingBars));
    }
    [Fact]
    public void Partial_estimated_profile_cannot_publish_a_completed_naked_POC_as_complete_source()
    {
        using var model = Model(new Trade(-1, Start, 10, 100, TradeSide.Buy, new TradeProvenance(DataQuality.Synthetic | DataQuality.Partial, 60)),
            new Trade(2, Start.AddDays(1), 20, 1, TradeSide.Buy));
        var study = Add(model, new CompositeVolumeProfile { PriceGrouping = 1, AllowEstimated = true, ComposeProfiles = false });
        var profile = study.Profiles[0]; Assert.Equal(100m, profile.Total);
        Assert.True(profile.Coverage.HasFlag(StudyCoverage.Estimated)); Assert.True(profile.Coverage.HasFlag(StudyCoverage.Partial)); Assert.False(profile.IsStrategyAvailable);
        var naked = Add(model, new NakedPoc { AllowEstimated = true });
        var level = Assert.Single(naked.Levels, x => x.Period == StudyTimeFrame.Day);
        Assert.True(level.Coverage.HasFlag(StudyCoverage.Partial)); Assert.False(level.CanPublish);
        Assert.False(Series(naked, "NakedPoc.Day.Below").IsAvailable(1));
    }
    [Fact]
    public void Fractional_subnormal_moments_round_the_complete_product_not_each_tiny_factor()
    {
        const decimal unit = 0.0000000000000000000000000001m;
        foreach (var (spread, weight, expected) in new[] { (1.7m, unit, unit), (0.84m, 4 * unit, unit), (3 * unit, 30000000000000000000000000000m, 14 * unit) })
        {
            var moments = new WeightedStudyMoments(); moments.Add(0, weight); moments.Add(spread, weight);
            Assert.Equal(expected, moments.M2);
        }
    }
    [Fact]
    public void Representable_mean_increment_is_not_erased_by_rounding_the_weight_ratio_first()
    {
        const decimal unit = 0.0000000000000000000000000001m, expected = 0.00000000000000000000005m;
        foreach (int sign in new[] { -1, 1 })
        {
            var added = new WeightedStudyMoments(); added.Add(0, 2); added.Add(sign * 1000000m, unit);
            Assert.Equal(sign * expected, added.Mean); Assert.Equal(0.0000000000000001m, added.M2);
            var merged = new WeightedStudyMoments(); merged.Add(0, 2);
            var other = new WeightedStudyMoments(); other.Add(sign * 1000000m, unit); merged.Merge(other);
            Assert.Equal(sign * expected, merged.Mean);
            using var model = Model(new Trade(1, Start, 0, 2, TradeSide.Buy), new Trade(2, Start.AddMinutes(1), sign * 1000000m, unit, TradeSide.Buy));
            var study = Add(model, new MultiPeriodVwap()); Assert.True(Series(study, "MultiVwap.Day").IsAvailable(1)); Assert.Equal(sign * expected, Series(study, "MultiVwap.Day")[1]);
        }
    }
    [Fact]
    public void Mean_product_scaling_handles_tiny_products_and_overflowing_intermediate_products()
    {
        const decimal unit = 0.0000000000000000000000000001m;
        var tiny = new WeightedStudyMoments(); tiny.Add(0, unit); tiny.Add(2 * unit, unit); Assert.Equal(unit, tiny.Mean);
        var large = new WeightedStudyMoments(); large.Add(0, unit); large.Add(2, 70000000000000000000000000000m); Assert.Equal(2m, large.Mean);
    }
}
