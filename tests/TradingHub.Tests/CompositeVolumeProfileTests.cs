using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public class CompositeVolumeProfileTests
{
    private static ChartModel Model(IEnumerable<(DateTime Time, decimal Price, decimal Volume, bool Synthetic)> trades, SessionSettings? session = null)
    {
        var instrument = new Instrument { Symbol = "COMPOSITE-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 };
        IMarketDataFeed feed = session == null ? new SimulatedFeed() : new SessionFeed(session);
        var data = new InstrumentData(instrument, feed); long id = 1;
        foreach (var trade in trades) { data.Trades.Add(new Trade(trade.Synthetic ? -id : id, trade.Time, trade.Price, trade.Volume, TradeSide.None)); id++; }
        return new ChartModel(data, ChartPeriod.M1, new ChartSettings());
    }
    private static CompositeVolumeProfile Add(ChartModel model, bool compose = false, decimal grouping = 1, decimal va = 70)
    {
        var study = new CompositeVolumeProfile { ComposeProfiles = compose, PriceGrouping = grouping, ValueAreaPercent = va };
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error); return study;
    }
    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    [Fact]
    public void POC_ties_choose_lower_price_and_value_area_expansion_ties_choose_upper()
    {
        using var model = Model(new[] { (Start, -1m, 5m, false), (Start.AddSeconds(1), 0m, 10m, false), (Start.AddSeconds(2), 1m, 5m, false) });
        var study = Add(model); var profile = Assert.Single(study.Profiles);
        Assert.Equal(0m, profile.Poc); Assert.Equal(0m, profile.ValueAreaLow); Assert.Equal(1m, profile.ValueAreaHigh); Assert.Equal(20m, profile.Total);
        using var tied = Model(new[] { (Start, -1m, 5m, false), (Start.AddSeconds(1), 0m, 5m, false) });
        Assert.Equal(-1m, Assert.Single(Add(tied).Profiles).Poc);
    }
    [Fact]
    public void Nearest_grouping_is_deterministic_on_signed_halfway_prices()
    {
        using var model = Model(new[] { (Start, -5m, 3m, false), (Start.AddSeconds(1), 5m, 4m, false) });
        var profile = Assert.Single(Add(model, grouping: 10).Profiles);
        Assert.Equal(new[] { -10m, 10m }, profile.Levels.Keys.OrderBy(x => x));
    }
    [Fact]
    public void Composition_is_chronological_inclusive_and_recomputes_value_area_after_each_merge()
    {
        using var model = Model(new[] { (Start, 0m, 10m, false), (Start.AddSeconds(1), 1m, 6m, false),
            (Start.AddDays(1), 1m, 20m, false), (Start.AddDays(2), 1m, 5m, false), (Start.AddDays(3), -3m, 7m, false) });
        var study = Add(model, compose: true);
        Assert.Equal(4, study.RawProfileCount); Assert.Equal(2, study.Profiles.Count);
        Assert.Equal(41m, study.Profiles[0].Total); Assert.Equal(1m, study.Profiles[0].Poc);
        Assert.Equal(-3m, study.Profiles[1].Poc);
    }
    [Fact]
    public void Developing_replacement_can_undo_composition_and_matches_full_replay()
    {
        using var model = Model(new[] { (Start, 0m, 10m, false), (Start.AddDays(1), 0m, 20m, false) });
        var study = Add(model, compose: true); Assert.Single(study.Profiles);
        Replace(model.Series[1], -10, 20); study.HostCalculate(1);
        Assert.Equal(2, study.Profiles.Count); Assert.Equal(30m, study.Profiles.Sum(p => p.Total));
        study.HostCalculate(1); Assert.Equal(2, study.Profiles.Count); Assert.Equal(30m, study.Profiles.Sum(p => p.Total));
        var replay = Add(model, compose: true);
        Assert.Equal(replay.Profiles.Select(p => (p.Poc, p.Total, p.StartBar, p.EndBar)), study.Profiles.Select(p => (p.Poc, p.Total, p.StartBar, p.EndBar)));
        Replace(model.Series[0], -10, 5); study.HostCalculate(0);
        Assert.Single(study.Profiles); Assert.Equal(25m, study.Profiles[0].Total);
    }
    [Fact]
    public void Projection_excludes_source_profile_and_accepts_zero_and_negative_POCs()
    {
        using var model = Model(new[] { (Start, -5m, 10m, false), (Start.AddDays(1), 0m, 10m, false), (Start.AddDays(2), 7m, 10m, false), (Start.AddDays(3), -5m, 1m, false) });
        var study = Add(model);
        var negative = Assert.Single(study.PocProjections, p => p.Price == -5);
        Assert.Equal(0, negative.SourceEndBar); Assert.Equal(3, negative.TouchBar);
        var zero = Assert.Single(study.PocProjections, p => p.Price == 0); Assert.Equal(-1, zero.TouchBar);
        Replace(model.Series[3], 10, 1); study.HostCalculate(3);
        Assert.Equal(-1, Assert.Single(study.PocProjections, p => p.Price == -5).TouchBar);
        study.HostCalculate(0); Assert.All(study.PocProjections, p => Assert.Equal(-1, p.TouchBar));
    }
    [Fact]
    public void Untested_zero_means_all_within_sixty_profiles_and_ranking_uses_distance()
    {
        using var model = Model(Enumerable.Range(0, 70).Select(i => (Start.AddDays(i), (decimal)i, 1m, false)));
        var study = Add(model); Assert.Equal(60, study.PocProjections.Count);
        Assert.Equal(68m, study.PocProjections[0].Price); Assert.Equal(9m, study.PocProjections[^1].Price);
        study.UntestedPocCount = 2; model.Update();
        Assert.Equal(new[] { 68m, 67m }, study.PocProjections.Select(p => p.Price));
    }
    [Fact]
    public void Tested_projection_retention_is_limited_to_ten_and_touch_is_first_later_bar()
    {
        using var model = Model(Enumerable.Range(0, 70).Select(i => (Start.AddDays(i), (decimal)i, 1m, false)));
        model.Series[69].LowTicks = 0;
        var study = Add(model);
        Assert.Equal(10, study.PocProjections.Count); Assert.All(study.PocProjections, p => Assert.Equal(69, p.TouchBar));
    }
    [Theory]
    [InlineData(CompositeVolumeProfile.ProfilePeriod.Month, "2026-12-01T22:59:00Z", "2026-12-01T23:00:00Z")]
    [InlineData(CompositeVolumeProfile.ProfilePeriod.Month, "2027-01-01T22:59:00Z", "2027-01-01T23:00:00Z")]
    [InlineData(CompositeVolumeProfile.ProfilePeriod.Week, "2026-11-02T22:59:00Z", "2026-11-02T23:00:00Z")]
    [InlineData(CompositeVolumeProfile.ProfilePeriod.Session, "2026-11-01T22:59:00Z", "2026-11-01T23:00:00Z")]
    public void Boundaries_use_host_New_York_session_start_not_UTC_calendar(CompositeVolumeProfile.ProfilePeriod period, string before, string after)
    {
        var session = new SessionSettings { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Start = TimeSpan.FromHours(18) };
        DateTime Parse(string value) => DateTime.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        using var model = Model(new[] { (Parse(before), 10m, 1m, false), (Parse(after), 20m, 1m, false) }, session);
        var study = new CompositeVolumeProfile { Period = period, ComposeProfiles = false, PriceGrouping = 1 };
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error); Assert.Equal(2, study.RawProfileCount);
    }
    [Fact]
    public void Spring_DST_session_boundary_is_22UTC_and_does_not_split_at_UTC_midnight()
    {
        var session = new SessionSettings { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Start = TimeSpan.FromHours(18) };
        var times = new[] { new DateTime(2026, 3, 8, 21, 59, 0, DateTimeKind.Utc), new DateTime(2026, 3, 8, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc) };
        using var model = Model(times.Select(t => (t, 10m, 1m, false)), session);
        var study = Add(model); Assert.Equal(2, study.RawProfileCount); Assert.Equal(2m, study.Profiles[1].Total);
    }
    [Fact]
    public void Estimated_profile_conserves_volume_with_bounded_samples_and_zero_volume_stays_empty()
    {
        using var model = Model(new[] { (Start, -10000m, 101m, true) }); model.Series[0].HighTicks = 10000;
        var study = new CompositeVolumeProfile { PriceGrouping = 1, AllowEstimated = true };
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
        var profile = Assert.Single(study.Profiles); Assert.Equal(101m, profile.Total); Assert.InRange(profile.Levels.Count, 1, 200);
        Assert.Equal(StudyCoverage.Estimated, profile.Coverage);
        Replace(model.Series[0], 0, 0); study.HostCalculate(0); Assert.Empty(Assert.Single(study.Profiles).Levels);
    }
    [Theory]
    [InlineData(1024)] [InlineData(2048)] [InlineData(4096)]
    public void Increasing_history_does_not_clone_and_sort_every_growing_snapshot(int bars)
    {
        using var model = Model(Enumerable.Range(0, bars).Select(i => (Start.AddMinutes(i), (decimal)i, 1m, false)));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var study = Add(model); long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0L, bars * 8192L);
        Assert.Equal((decimal)bars, study.Profiles.Sum(p => p.Total));
    }
    private static void Replace(TradingHub.Data.Candle candle, long price, decimal volume)
    {
        candle.Levels.Clear(); candle.Trades = 0; candle.Volume = candle.Bid = candle.Ask = candle.Between = 0; candle.HasRealTrades = candle.HasSyntheticTrades = false;
        candle.AddTrade(price, price, volume, TradeSide.None, candle.Time.Ticks, 0);
    }
    private sealed class SessionFeed(SessionSettings session) : IMarketDataFeed, ISessionSettingsFeed
    {
        private readonly SimulatedFeed _inner = new();
        public string Id => "sim"; public string DisplayName => "Session fixture"; public bool SupportsHistory => true; public bool IsLive => false;
        public SessionSettings GetSessionSettings(Instrument instrument) => session;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => _inner.GetInstrumentsAsync(ct);
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => _inner.LoadTradesAsync(instrument, fromUtc, toUtc, fromId, onBatch, progress, ct);
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => _inner.Subscribe(instrument, sink);
        public void Dispose() => _inner.Dispose();
    }
}
