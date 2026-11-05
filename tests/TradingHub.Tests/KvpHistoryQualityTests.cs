using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

[CollectionDefinition("KvpHistoryCache", DisableParallelization = true)]
public sealed class KvpHistoryCacheCollection { }

[Collection("KvpHistoryCache")]
public sealed class KvpHistoryQualityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-kvp-history-" + Guid.NewGuid().ToString("N"));
    private readonly Instrument _instrument = new() { FeedId = "quality-test", Symbol = "TEST", TickSize = 1m, VolumeStep = 0.00000001m };
    private static readonly DateTime Start = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private string Cache => Path.Combine(_root, "compact");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void LegacyRawSurvivesCompactionAndLosslessArchiveAndArchiveOnlyReload()
    {
        var day = DateOnly.FromDateTime(Start);
        var path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        var raw = new[] {
            new Trade(1, Start, 100, 2, TradeSide.Buy), new Trade(2, Start.AddSeconds(1), 101, 3, TradeSide.Sell),
            new Trade(3, Start.AddSeconds(2), 100, 5, TradeSide.Buy), new Trade(4, Start.AddSeconds(3), 100, 7, TradeSide.Buy),
            new Trade(5, Start.AddSeconds(4), 101, 1, TradeSide.Sell), new Trade(6, Start.AddSeconds(5), 100, 2, TradeSide.Buy),
        };
        using (var writer = new TickFile.Writer(path, _instrument, includeProvenance: false)) foreach (var trade in raw) writer.Write(trade);
        byte[] original = File.ReadAllBytes(path);
        var compact = CompactHistory.LoadDay(_root, Cache, _instrument, day, 5);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(20m, compact.Sum(t => t.Volume));
        Assert.Equal(12m, compact.Sum(t => t.Side == TradeSide.Buy ? t.Volume : -t.Volume));
        Assert.Equal(6, compact.Sum(t => t.Provenance.ObservedTradeCount));
        var series = Series(compact, ChartPeriod.M5);
        var candle = Assert.Single(series.Candles);
        Assert.True(candle.HasCompactTrades); Assert.False(candle.HasRealTrades); Assert.False(candle.HasSyntheticTrades);
        Assert.Equal(0m, candle.MaxTradeVolume); Assert.True(candle.Levels.IsEmpty);
        var sdk = new IndicatorCandle(candle, series);
        Assert.True(sdk.HasAggregatedFootprint); Assert.False(sdk.HasObservedFootprint); Assert.Equal(300, sdk.ResolutionSeconds);
        var prices = sdk.GetProfilePriceLevels().ToDictionary(level => level.Price, level => level.Volume);
        Assert.Equal(16m, prices[100]); Assert.Equal(4m, prices[101]);
        var archive = TickArchive.ArchiveDay(_root, _instrument, day);
        Assert.True(File.Exists(archive)); Assert.Equal(original, File.ReadAllBytes(path));
        File.Delete(path); // Only this owned fixture: prove reads do not depend on the retained raw copy.
        var reloaded = HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, day);
        Assert.Equal(raw.Select(t => (t.Id, t.Time, t.Price, t.Volume, t.Side)), reloaded.Select(t => (t.Id, t.Time, t.Price, t.Volume, t.Side)));
        Assert.All(reloaded, trade => Assert.True(trade.IsObservedTrade));
    }

    [Fact]
    public void SameLengthSameTimestampCorrectionInvalidatesCompactPriceDistribution()
    {
        var day = DateOnly.FromDateTime(Start);
        HistoricalData.WriteDay(_root, _instrument, day, new[] { new Trade(1, Start, 100, 9, TradeSide.Buy) });
        var path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        long length = new FileInfo(path).Length; var modified = File.GetLastWriteTimeUtc(path);
        var first = CompactHistory.LoadDay(_root, Cache, _instrument, day, 1);
        HistoricalData.WriteDay(_root, _instrument, day, new[] { new Trade(1, Start, 102, 9, TradeSide.Buy) }, replace: true);
        Assert.Equal(length, new FileInfo(path).Length);
        File.SetLastWriteTimeUtc(path, modified);
        var second = CompactHistory.LoadDay(_root, Cache, _instrument, day, 1);
        Assert.Equal(100m, Assert.Single(first).Price); Assert.Equal(102m, Assert.Single(second).Price);
        Assert.Equal(9m, Assert.Single(second).Volume);
        Assert.NotEqual(first[0].Provenance.CorrectionRevision, second[0].Provenance.CorrectionRevision);
        var cache = CompactHistory.CachePath(Cache, _instrument.FeedId, _instrument.Symbol, day, 1);
        byte[] broken = File.ReadAllBytes(cache); broken[^1] ^= 0x7F; File.WriteAllBytes(cache, broken);
        Assert.Equal(102m, Assert.Single(CompactHistory.LoadDay(_root, Cache, _instrument, day, 1)).Price);
        Assert.Equal(102m, Assert.Single(TickFile.Read(path)).Price);
    }

    [Fact]
    public void MixedQualityRemainsDistinctThroughSdkVectorsAndActualBacktestBars()
    {
        var raw = new Trade(11, Start, 100, 2, TradeSide.Buy, new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide, SourceIdentity: "venue", CorrectionRevision: 4));
        var compact = new Trade(CompactHistory.CompressedIdBase + 1, Start.AddSeconds(1), 101, 10, TradeSide.Sell,
            new TradeProvenance(DataQuality.Compact, 60, "venue", 4, 5));
        var synthetic = new Trade(-88, Start.AddSeconds(2), 102, 8, TradeSide.Buy,
            new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide, 60, "bars", 8, 0));
        var trades = new[] { raw, compact, synthetic };
        var series = Series(trades, ChartPeriod.M1);
        var candle = Assert.Single(series.Candles); var sdk = new IndicatorCandle(candle, series);
        Assert.Equal(20m, sdk.Volume); Assert.Equal(2m, sdk.MaxTradeVolume);
        Assert.True(sdk.HasObservedFootprint && sdk.HasAggregatedFootprint && sdk.HasInferredSide);
        Assert.False(sdk.IsFootprintComplete); Assert.Equal(1, sdk.ObservedTrades); Assert.Equal(5, sdk.AggregatedTrades);
        Assert.Equal(2m, Assert.Single(sdk.GetAllPriceLevels()).Volume);
        Assert.Equal(10m, Assert.Single(sdk.GetAggregatedPriceLevels()).Volume);
        Assert.Equal(12m, sdk.GetProfilePriceLevels().Sum(level => level.Volume));
        var store = new TradeStore(_instrument); foreach (var trade in trades) store.Add(trade);
        foreach (var ticks in new[] { TickData.FromTrades(_instrument, trades), TickData.FromStore(store), TickData.FromTrades(_instrument, trades).Slice(Start, Start.AddMinutes(1)) })
        {
            Assert.Equal(new long[] { 11, compact.Id, -88 }, Enumerable.Range(ticks.Start, ticks.Count).Select(i => ticks.Get(i).Id));
            var probe = new Probe(); var result = new BacktestEngine(probe, ticks, Settings()).Run();
            Assert.Null(result.Error); Assert.NotNull(result.DataWarning);
            Assert.Equal(candle.Quality, result.DataQuality);
            var bar = Assert.Single(probe.Seen);
            Assert.True(bar.HasRealTrades && bar.HasCompactTrades && bar.HasSyntheticTrades && bar.HasInferredSide);
            Assert.Equal(2m, bar.Levels[100].Buy); Assert.Equal(10m, bar.AggregatedLevels[101].Sell);
            Assert.False(bar.Levels.ContainsKey(102)); Assert.False(bar.AggregatedLevels.ContainsKey(102));
        }
    }

    [Fact]
    public void SameCountCorrectionAndRepeatedFormingUpdatesReplaceRatherThanAccumulate()
    {
        var store = new TradeStore(_instrument);
        store.Add(new Trade(1, Start, 100, 2, TradeSide.Buy));
        store.Add(new Trade(2, Start.AddSeconds(1), 101, 3, TradeSide.Sell));
        var series = new CandleSeries(_instrument, ChartPeriod.M1); series.Rebuild(store);
        long oldCorrection = series.CorrectionRevision;
        store.Update(1, new Trade(2, Start.AddSeconds(1), 104, 3, TradeSide.Sell));
        Assert.True(series.Append(store).Changed);
        Assert.True(series.CorrectionRevision > oldCorrection); Assert.Equal(5m, series[0].Volume);
        var sdk = new IndicatorCandle(series[0], series);
        Assert.Null(sdk.GetPriceVolumeInfo(101)); Assert.Equal(3m, sdk.GetPriceVolumeInfo(104).Bid);
        Assert.False(series.Append(store).Changed); Assert.False(series.Append(store).Changed);
        Assert.Equal(5m, series[0].Volume); Assert.Equal(2, series[0].ObservedTrades);
        store.Update(1, new Trade(2, Start.AddSeconds(1), 104, 7, TradeSide.Sell)); series.Append(store);
        Assert.Equal(9m, series[0].Volume); Assert.Equal(7m, new IndicatorCandle(series[0], series).GetPriceVolumeInfo(104).Bid);
    }

    [Fact]
    public void InitialAggregationHasBoundedContinuationAndSparseNegativePriceRows()
    {
        var store = new TradeStore(_instrument);
        for (int i = 0; i < 40; i++) store.Add(new Trade(i + 1, Start.AddSeconds(i), i % 2 == 0 ? -1_000_000_000 : 1_000_000_000, 1, TradeSide.None));
        var series = new CandleSeries(_instrument, ChartPeriod.M1); series.BeginRebuild(store);
        series.Append(store, 7); Assert.Equal(7, series.ProcessedTrades); Assert.True(series.HasPendingTrades(store));
        while (series.HasPendingTrades(store)) series.Append(store, 7);
        var sdk = new IndicatorCandle(Assert.Single(series.Candles), series);
        var levels = sdk.GetProfilePriceLevels().ToList();
        Assert.Equal(2, levels.Count); Assert.All(levels, level => Assert.Equal(20m, level.Volume));
        Assert.Equal(40m, sdk.Volume); Assert.Equal(0m, sdk.Delta);
    }

    [Fact]
    public void ExactDecimalQuantityAndOriginalIdentitySurviveStorageAndStrategyConsumer()
    {
        const decimal quantity = 0.1234567890123456789012345678m;
        var time = Start.AddTicks(1234);
        var trade = new Trade(987654321, time, 100, quantity, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide | DataQuality.Partial, 0, "provider:profile", 17));
        HistoricalData.WriteDay(_root, _instrument, DateOnly.FromDateTime(Start), new[] { trade });
        var loaded = TickData.Load(_root, _instrument, Start, Start.AddMinutes(1), null, default);
        Assert.Equal(quantity, loaded.Volume(0)); Assert.Equal(time, loaded.Get(0).Time); Assert.Equal(trade.Id, loaded.Get(0).Id);
        Assert.Equal(trade.Provenance, loaded.Get(0).Provenance);
        var probe = new Probe(); var result = new BacktestEngine(probe, loaded, Settings()).Run();
        Assert.Null(result.Error); Assert.Equal(quantity, Assert.Single(probe.Seen).Volume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedRawRecordCannotBecomeACompleteObservedEvent(bool provenance)
    {
        var path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start));
        using (var writer = new TickFile.Writer(path, _instrument, includeProvenance: provenance))
        { writer.Write(new Trade(1, Start, 100, 2, TradeSide.Buy)); writer.Write(new Trade(2, Start.AddSeconds(1), 101, 3, TradeSide.Sell)); }
        var bytes = File.ReadAllBytes(path); File.WriteAllBytes(path, bytes[..^1]);
        Assert.Throws<InvalidDataException>(() => TickFile.Read(path, out _, requireComplete: true));
        var committed = TickFile.Read(path);
        Assert.Equal(1, Assert.Single(committed).Id); Assert.Equal(2m, committed[0].Volume);
    }

    [Fact]
    public void CandleReplacementNeverErasesFinerRawHistoryAndProviderAggregatesRemainMeasured()
    {
        var day = DateOnly.FromDateTime(Start);
        var raw = new Trade(1, Start, 100, 2, TradeSide.Buy);
        HistoricalData.WriteDay(_root, _instrument, day, new[] { raw });
        var candleEvents = new List<Trade>();
        KlineHistory.Synthesize(new[] { new Kline(Ms(Start), 100, 102, 99, 101, 100, 0, false) }, _instrument, 1, new[] { (Start, Start.AddMinutes(1)) }, candleEvents);
        HistoricalData.WriteDay(_root, _instrument, day, candleEvents, replace: true);
        Assert.Equal(2m, Assert.Single(HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, day)).Volume);
        var packet = new Trade(2, Start.AddSeconds(5), 101, 9, TradeSide.Sell, new TradeProvenance(DataQuality.Compact, 0, "exchange", 0, 4));
        HistoricalData.WriteDay(_root, _instrument, day, new[] { packet });
        var measured = HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, day);
        Assert.Equal(2, measured.Count); Assert.False(measured[1].IsObservedTrade); Assert.True(measured[1].Provenance.IsMeasured);
        Assert.Equal(4, measured[1].Provenance.ObservedTradeCount);
    }

    [Fact]
    public async Task ActiveMonthRefreshFetchesOnlyOverlapAndReplacesCorrectedFormingCandles()
    {
        string? previousRoot = KlineHistory.CacheRoot; KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var source = new CandleSource(month);
            var first = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(10), 1, 0, null, default);
            Assert.Equal(10, first.Count); Assert.Equal(month, Assert.Single(source.Starts));
            source.CorrectedMinute = 8;
            var second = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(12), 1, 0, null, default);
            Assert.Equal(month.AddMinutes(8), source.Starts[1]); Assert.Equal(12, second.Count);
            Assert.Equal(105m, second.Single(k => k.OpenTimeMs == Ms(month.AddMinutes(8))).Close);
            source.CorrectedMinute = 11;
            var third = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(12), 1, 0, null, default);
            Assert.Equal(month.AddMinutes(10), source.Starts[2]); Assert.Equal(12, third.Select(k => k.OpenTimeMs).Distinct().Count());
            Assert.Equal(105m, third[^1].Close); Assert.Equal(1m, third[^1].Volume);
            source.Fail = true;
            var retained = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(12), 1, 0, null, default);
            Assert.Equal(12, retained.Count); Assert.All(retained, k => Assert.True(k.Provenance.IsPartial));
            source.Fail = false;
            string cache = Assert.Single(Directory.EnumerateFiles(KlineHistory.CacheRoot, "*.bin", SearchOption.AllDirectories));
            File.WriteAllBytes(cache, new byte[] { 1, 2, 3 });
            source.Starts.Clear();
            var rebuilt = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(12), 1, 0, null, default);
            Assert.Equal(month, Assert.Single(source.Starts)); Assert.Equal(12, rebuilt.Count);
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public async Task CanceledMonthRefreshKeepsThePreviouslyPublishedCacheIntact()
    {
        string? previousRoot = KlineHistory.CacheRoot; KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var source = new CandleSource(month);
            await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(5), 1, 0, null, default);
            string cache = Assert.Single(Directory.EnumerateFiles(KlineHistory.CacheRoot, "*.bin", SearchOption.AllDirectories));
            byte[] before = File.ReadAllBytes(cache);
            using var cancellation = new CancellationTokenSource(); source.AfterBatch = cancellation.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(6), 1, 0, null, cancellation.Token));
            Assert.Equal(before, File.ReadAllBytes(cache));
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public void LargeSamePriceRunIsOneMeasuredAggregateRatherThanAnObservedBigTrade()
    {
        var trades = Enumerable.Range(1, 70_000).Select(id => new Trade(id, Start, 100, 1, TradeSide.Buy)).ToArray();
        var ticks = TickData.FromTrades(_instrument, trades).Compress();
        Assert.Equal(1, ticks.Count); Assert.Equal(70_000m, ticks.Volume(0));
        Assert.False(ticks.IsObservedTrade(0)); Assert.True(ticks.IsCompact(0));
        Assert.Equal(70_000, ticks.Provenance(0).ObservedTradeCount);
        var series = Series(new[] { ticks.Get(0) }, ChartPeriod.M1);
        var sdk = new IndicatorCandle(Assert.Single(series.Candles), series);
        Assert.Equal(0m, sdk.MaxTradeVolume); Assert.False(sdk.HasObservedFootprint);
        var level = Assert.Single(sdk.GetAggregatedPriceLevels());
        Assert.Equal(70_000m, level.Volume); Assert.Equal(70_000m, level.Ticks); Assert.True(level.IsAggregated);
    }

    [Fact]
    public async Task LiveSameIdentityCorrectionReplacesTheExistingObservationWithoutDoubleCounting()
    {
        using var feed = new PumpFeed(_instrument);
        using var data = new InstrumentData(_instrument, feed);
        data.Start(Start.AddMinutes(-1)); await Ready(data);
        int rebuilds = 0; data.HistoryLoaded += () => rebuilds++;
        feed.Trades(new Trade(1, Start, 100, 2, TradeSide.Buy)); data.Pump();
        feed.Trades(new Trade(1, Start, 104, 2, TradeSide.Buy)); data.Pump();
        feed.Trades(new Trade(1, Start, 104, 2, TradeSide.Buy)); data.Pump();
        Assert.Equal(1, data.Trades.Count); Assert.Equal(104m, data.LastPrice);
        Assert.Equal(2m, data.Trades.GetVolume(0)); Assert.Equal(1, rebuilds);
    }

    [Fact]
    public async Task DepthStatusAndEqualTimestampUpdatesPublishInReceivedOrder()
    {
        using var feed = new PumpFeed(_instrument);
        using var data = new InstrumentData(_instrument, feed);
        data.Start(Start.AddMinutes(-1)); await Ready(data);
        var seen = new List<DepthUpdate>(); data.DepthUpdated += seen.Add;
        feed.Status(FeedStatus.Connecting);
        feed.Depth(2, 1, full: true); feed.Status(FeedStatus.Connected); feed.Depth(3, 2); feed.Depth(4, 3);
        data.Pump();
        Assert.Equal(4, seen.Count);
        Assert.Equal(DepthGapReason.Disconnected, seen[0].GapBefore); Assert.False(seen[0].Snapshot.HasBid);
        Assert.Equal(new decimal[] { 2, 3, 4 }, seen.Skip(1).Select(update => update.Snapshot.BestBidSize));
        Assert.Equal(4m, data.Depth.BestBidSize);
    }

    [Fact]
    public async Task ReplayBatchExposesEachPriceBeforeTheNextEventCanReachItsConsumers()
    {
        HistoricalData.WriteDay(_root, _instrument, DateOnly.FromDateTime(Start), new[] {
            new Trade(1, Start, 101, 1, TradeSide.Buy), new Trade(2, Start.AddSeconds(1), 102, 1, TradeSide.Buy),
            new Trade(3, Start.AddSeconds(2), 103, 1, TradeSide.Buy) });
        using var feed = new ReplayFeed(_root, _instrument, Start, Start.AddMinutes(1)) { Paused = true };
        using var data = new InstrumentData(feed.Instrument, feed);
        data.Start(Start.AddMinutes(-1)); await Ready(data);
        var seen = new List<(int Count, int Stored, decimal Price)>();
        data.TradesAppended += (_, count) => seen.Add((count, data.Trades.Count, data.LastPrice));
        feed.SeekForward(Start.AddMinutes(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (data.Trades.Count < 3) { data.Pump(); await Task.Delay(5, timeout.Token); }
        Assert.Equal(new[] { (1, 1, 101m), (1, 2, 102m), (1, 3, 103m) }, seen);
    }

    private static async Task Ready(InstrumentData data)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (data.IsLoadingHistory) { data.Pump(); await Task.Delay(5, timeout.Token); }
        data.Pump(); Assert.Null(data.HistoryError);
    }

    private CandleSeries Series(IReadOnlyList<Trade> trades, ChartPeriod period)
    { var store = new TradeStore(_instrument); foreach (var trade in trades) store.Add(trade); var series = new CandleSeries(_instrument, period); series.Rebuild(store); return series; }
    private BacktestSettings Settings() => new() { FeedId = _instrument.FeedId, Symbol = _instrument.Symbol, FromUtc = Start, ToUtc = Start.AddMinutes(2), Period = "M1", CommissionPercent = 0, SlippageTicks = 0 };
    private static long Ms(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();
    private sealed class Probe : Strategy
    {
        public List<Bar> Seen { get; } = new();
        protected override void OnInit() => UseFootprint = true;
        protected override void OnBar(Bar bar) => Seen.Add(bar);
    }
    private sealed class PumpFeed(Instrument instrument) : IMarketDataFeed
    {
        private IMarketDataSink? _sink;
        public string Id => instrument.FeedId;
        public string DisplayName => "Fake history pump";
        public bool SupportsHistory => false;
        public bool IsLive => true;
        public DateTime Now => Start.AddSeconds(10);
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { instrument });
        public IDisposable Subscribe(Instrument selected, IMarketDataSink sink) { _sink = sink; sink.OnStatus(selected, FeedStatus.Connected, null); return new Subscription(); }
        public Task LoadTradesAsync(Instrument selected, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public void Trades(params Trade[] trades) => _sink!.OnTrades(instrument, trades);
        public void Status(FeedStatus status) => _sink!.OnStatus(instrument, status, null);
        public void Depth(decimal size, long sequence, bool full = false) => _sink!.OnDepth(instrument, new DepthUpdate
        {
            Snapshot = new DepthSnapshot(new[] { new BookLevel(100, size) }, new[] { new BookLevel(101, size) }, Start),
            Changes = new[] { new BookChange(BookSide.Bid, 100, size) }, Sequence = sequence,
            PreviousSequence = full ? null : sequence - 1, IsFullRefresh = full, ProviderId = Id, ReceivedAt = Now,
            Coverage = DepthCoverage.ReceivedChanges,
        });
        public void Dispose() { }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }

    private sealed class CandleSource(DateTime month) : IKlineHistory
    {
        public List<DateTime> Starts { get; } = new();
        public int CorrectedMinute { get; set; } = -1;
        public bool Fail { get; set; }
        public Action? AfterBatch { get; set; }
        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(month);
        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice, Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            Starts.Add(fromUtc);
            if (Fail) throw new IOException("Fixture candle source unavailable.");
            var bars = new List<Kline>();
            for (var time = fromUtc; time < toUtc; time = time.AddMinutes(minutes))
            { decimal close = (int)(time - month).TotalMinutes == CorrectedMinute ? 105 : 100; bars.Add(new Kline(Ms(time), 100, 105, 99, close, 1, 0.5m, true)); }
            onBatch(bars); AfterBatch?.Invoke(); return Task.CompletedTask;
        }
    }
}
