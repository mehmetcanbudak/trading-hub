using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class KvpBigTradesLifecycleTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inferred_open_group_retracts_through_feed_pump_with_or_without_history_seeding(bool historySeeded)
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-live-study-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instrument = new Instrument { Symbol = "GROUP-FIXTURE", FeedId = "native-pump", TickSize = 1, VolumeStep = 1 };
            using var feed = new PumpFeed(instrument);
            using var data = new InstrumentData(instrument, feed) { RecordLive = false, RecordingRoot = Path.Combine(root, "raw"), CompactRoot = Path.Combine(root, "compact") };
            data.Start(Start.AddMinutes(-1)); await Ready(data);
            DateTime time = Start.AddSeconds(1);
            if (historySeeded) { feed.Trades(new Trade(1, time, 10, 20, TradeSide.Buy)); data.Pump(); }
            using var live = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
            var study = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 10 };
            Assert.Null(live.AddIndicator(study, applyDefaultColors: false).Error);
            if (!historySeeded) { feed.Trades(new Trade(1, time, 10, 20, TradeSide.Buy)); data.Pump(); live.Update(); }
            Assert.Equal(1, study.BubbleCount);
            feed.Trades(new Trade(2, time, 10, 100, TradeSide.Buy, new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide)));
            data.Pump(); live.Update(); Assert.Equal(0, study.BubbleCount);
            using var replay = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
            var fresh = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 10 };
            Assert.Null(replay.AddIndicator(fresh, applyDefaultColors: false).Error); replay.Update();
            Assert.Equal(fresh.BubbleCount, study.BubbleCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejecting_the_open_group_rolls_back_its_adaptive_sample_and_next_bubble(bool historySeeded)
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-adaptive-study-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instrument = new Instrument { Symbol = "ADAPTIVE-FIXTURE", FeedId = "native-pump", TickSize = 1, VolumeStep = 1 };
            using var feed = new PumpFeed(instrument);
            using var data = new InstrumentData(instrument, feed) { RecordLive = false, RecordingRoot = Path.Combine(root, "raw"), CompactRoot = Path.Combine(root, "compact") };
            data.Start(Start.AddMinutes(-1)); await Ready(data);
            var baseline = Enumerable.Range(0, 250).Select(i => new Trade(i + 1, Start.AddMilliseconds(i), 10, 1, TradeSide.Buy)).ToArray();
            if (historySeeded) { feed.Trades(baseline); data.Pump(); }
            using var live = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
            var study = new BigTrades { Mode = BigTrades.ThresholdMode.Adaptive, Sample = 100, Percentile = 99.7m };
            Assert.Null(live.AddIndicator(study, applyDefaultColors: false).Error);
            if (!historySeeded) { feed.Trades(baseline); data.Pump(); live.Update(); }
            Assert.Equal(1m, study.CurrentThreshold);
            DateTime time = Start.AddMilliseconds(250);
            feed.Trades(new Trade(251, time, 10, 100, TradeSide.Buy)); data.Pump(); live.Update(); Assert.Equal(100m, study.CurrentThreshold);
            feed.Trades(new Trade(252, time, 10, 1, TradeSide.Buy, new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide)));
            data.Pump(); live.Update(); Assert.Equal(1m, study.CurrentThreshold);
            int before = study.BubbleCount;
            feed.Trades(new Trade(253, Start.AddMilliseconds(251), 10, 2, TradeSide.Buy)); data.Pump(); live.Update(); Assert.Equal(before + 1, study.BubbleCount);
            using var replay = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
            var fresh = new BigTrades { Mode = BigTrades.ThresholdMode.Adaptive, Sample = 100, Percentile = 99.7m };
            Assert.Null(replay.AddIndicator(fresh, applyDefaultColors: false).Error); replay.Update();
            Assert.Equal(fresh.CurrentThreshold, study.CurrentThreshold); Assert.Equal(fresh.BubbleCount, study.BubbleCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Rolling_quantile_matches_independent_nearest_rank_and_replacing_open_group_does_not_add_a_sample()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-rank-study-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instrument = new Instrument { Symbol = "RANK-FIXTURE", FeedId = "native-pump", TickSize = 1, VolumeStep = 1 };
            using var feed = new PumpFeed(instrument);
            using var data = new InstrumentData(instrument, feed) { RecordLive = false, RecordingRoot = Path.Combine(root, "raw"), CompactRoot = Path.Combine(root, "compact") };
            data.Start(Start.AddMinutes(-1)); await Ready(data);
            using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
            var study = new BigTrades { Mode = BigTrades.ThresholdMode.Adaptive, Sample = 100, Percentile = 50 };
            Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
            decimal[] volumes = { 5, 1, 9, 2, 7, 3 }, expected = { 5, 1, 5, 5, 5, 3 };
            for (int i = 0; i < volumes.Length; i++)
            {
                feed.Trades(new Trade(i + 1, Start.AddMilliseconds(i), 10, volumes[i], TradeSide.Buy)); data.Pump(); model.Update();
                Assert.Equal(expected[i], study.CurrentThreshold);
            }
            // The last group is replaced from volume 3 to 11, not counted as two groups.
            feed.Trades(new Trade(7, Start.AddMilliseconds(5), 10, 8, TradeSide.Buy)); data.Pump(); model.Update();
            Assert.Equal(5m, study.CurrentThreshold);
            study.Percentile = 99.7m; model.Update();
            feed.Trades(new Trade(8, Start.AddMilliseconds(6), 10, 1000, TradeSide.Buy)); data.Pump(); model.Update();
            feed.Trades(Enumerable.Range(1, 99).Select(i => new Trade(8 + i, Start.AddMilliseconds(6 + i), 10, 1, TradeSide.Buy)).ToArray());
            data.Pump(); model.Update(); Assert.Equal(1000m, study.CurrentThreshold);
            feed.Trades(new Trade(108, Start.AddMilliseconds(106), 10, 1, TradeSide.Buy)); data.Pump(); model.Update();
            Assert.Equal(1m, study.CurrentThreshold);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task Ready(InstrumentData data)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (data.IsLoadingHistory) { data.Pump(); await Task.Delay(1, timeout.Token); }
        data.Pump(); Assert.Null(data.HistoryError);
    }
    private sealed class PumpFeed(Instrument instrument) : IMarketDataFeed
    {
        private IMarketDataSink? _sink;
        public string Id => instrument.FeedId;
        public string DisplayName => "Native study pump fixture";
        public bool SupportsHistory => false;
        public bool IsLive => true;
        public DateTime Now => Start.AddMinutes(1);
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { instrument });
        public Task LoadTradesAsync(Instrument selected, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument selected, IMarketDataSink sink) { _sink = sink; sink.OnStatus(selected, FeedStatus.Connected, null); return new Subscription(); }
        public void Trades(params Trade[] trades) => _sink!.OnTrades(instrument, trades);
        public void Dispose() { }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
