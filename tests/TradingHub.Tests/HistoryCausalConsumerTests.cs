using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class HistoryCausalConsumerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-history-causal-" + Guid.NewGuid().ToString("N"));
    private readonly Instrument _instrument = new() { FeedId = "causal-fixture", Symbol = "TEST", TickSize = 1, VolumeStep = 1, PointValue = 1 };
    private static readonly DateTime Start = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private BacktestSettings Settings() => new() { FeedId = _instrument.FeedId, Symbol = _instrument.Symbol, FromUtc = Start,
        ToUtc = Start.AddMinutes(2), Period = "M1", CloseAtEnd = false, CommissionPercent = 0, SlippageTicks = 0 };

    [Fact]
    public void ClosedSourceWindowCannotFillNewSignalAgainstItsOwnEarlierAnchors()
    {
        var interval = new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide, 60, "ohlc", ObservedTradeCount: 0);
        var rows = new[] {
            new Trade(-1, Start, 100, 1, TradeSide.None, interval),
            new Trade(1, Start.AddSeconds(30), 90, 1, TradeSide.Buy),
            new Trade(-2, Start.AddSeconds(45), 120, 1, TradeSide.None, interval),
            new Trade(2, Start.AddSeconds(65), 150, 1, TradeSide.Sell),
        };
        var strategy = new ClockProbe(buyOnInterval: true);
        var result = new BacktestEngine(strategy, TickData.FromTrades(_instrument, rows), Settings()).Run();
        Assert.Null(result.Error);
        Assert.NotNull(result.DataWarning);
        Assert.Equal(new[] { 1L, -1L, -2L, 2L }, strategy.CapturedTrades.Select(t => t.Id));
        Assert.Equal(new[] { Start.AddSeconds(30), Start.AddMinutes(1), Start.AddMinutes(1), Start.AddSeconds(65) }, strategy.Clocks);
        Assert.Equal(new[] { 1L, 2L }, strategy.CapturedTrades.Where(t => t.IsObservedTrade).Select(t => t.Id));
        Assert.NotNull(strategy.Order);
        Assert.Equal(Start.AddMinutes(1), strategy.Order.Time);
        Assert.Equal(Start.AddSeconds(65), strategy.Order.FillTime);
        Assert.Equal(150m, strategy.Order.FillPrice);
        var first = strategy.BarsSeen[0];
        Assert.Equal((100m, 120m, 90m, 120m, 3m), (first.Open, first.High, first.Low, first.Close, first.Volume));
        Assert.Equal(3, first.Trades);
        Assert.Equal(1, first.ObservedTrades);
        Assert.True(first.HasKnownAvailability && first.IsTimeResolutionCompatible);
        Assert.True(first.AvailableAt >= Start.AddMinutes(1));
    }

    [Fact]
    public void RawPointSequenceRetainsNextEventFillsAtEqualTimestamp()
    {
        var rows = new[] { new Trade(1, Start, 100, 1, TradeSide.Buy), new Trade(2, Start, 101, 1, TradeSide.Sell),
            new Trade(3, Start.AddSeconds(1), 102, 1, TradeSide.Buy) };
        var strategy = new ClockProbe(buyOnFirst: true);
        var result = new BacktestEngine(strategy, TickData.FromTrades(_instrument, rows), Settings()).Run();
        Assert.Null(result.Error);
        Assert.Equal(new[] { 1L, 2L, 3L }, strategy.CapturedTrades.Select(t => t.Id));
        Assert.Equal(Start, strategy.Order!.FillTime);
        Assert.Equal(101m, strategy.Order.FillPrice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownOrCrossingSourceTimingStopsBeforeStrategyCallbacks(bool crossing)
    {
        var provenance = crossing
            ? new TradeProvenance(DataQuality.Compact, 60, "phased", ObservedTradeCount: 10, ResolutionOffsetSeconds: 30)
            : new TradeProvenance(DataQuality.Raw, HasKnownAvailability: false);
        var strategy = new ClockProbe();
        var rows = new[] { new Trade(1, Start.AddSeconds(30), 100, 1, TradeSide.Buy, provenance) };
        var result = new BacktestEngine(strategy, TickData.FromTrades(_instrument, rows), Settings()).Run();
        Assert.NotNull(result.Error);
        Assert.False(strategy.Initialized);
        Assert.Empty(strategy.CapturedTrades);
        Assert.Empty(result.Trades);
        Assert.Equal(crossing, result.HasKnownAvailability);
    }

    [Fact]
    public void AvailabilitySelectionIncludesEarlierLocationsAndRemovesMovedCorrections()
    {
        var path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start));
        var coarse = new TradeProvenance(DataQuality.Compact, 300, "source", ObservedTradeCount: 30);
        using (var writer = new TickFile.Writer(path, _instrument, includeProvenance: true))
        {
            writer.Write(new Trade(-1, Start, 100, 30, TradeSide.Buy, coarse));
            writer.Write(Native(1, 270, 1, "point"));
        }
        var selected = HistoricalData.ReadAvailable(_root, _instrument.FeedId, _instrument.Symbol, Start.AddMinutes(4), Start.AddMinutes(6));
        Assert.Equal(new[] { 1L, -1L }, selected.Select(t => t.Id));
        Assert.Equal(Start, selected[1].Time);
        Assert.Equal(Start.AddMinutes(5), selected[1].AvailableAt);
        Assert.Empty(HistoricalData.ReadAvailable(_root, _instrument.FeedId, _instrument.Symbol, Start.AddMinutes(5), Start.AddMinutes(5)));
        using (var writer = new TickFile.Writer(path, _instrument))
        {
            writer.Write(Native(2, 280, 2, "moved"));
            writer.Write(Native(3, 400, 3, "moved"));
        }
        selected = HistoricalData.ReadAvailable(_root, _instrument.FeedId, _instrument.Symbol, Start.AddMinutes(4), Start.AddMinutes(6));
        Assert.DoesNotContain(selected, t => t.Provenance.SourceEventId == "moved");
    }

    [Fact]
    public async Task ActualLoaderRetainsNewestCorrectedRawWindowWithoutErasingNativeIds()
    {
        var path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start));
        using (var writer = new TickFile.Writer(path, _instrument, includeProvenance: true))
        {
            for (int i = 1; i <= 4; i++) writer.Write(Native(i, i, i, "e" + i));
            writer.Write(Native(5, 1, 10, "e1"));
            writer.Write(Native(6, 3, 30, "e3"));
        }
        byte[] original = File.ReadAllBytes(path);
        using var feed = new FixtureFeed(_instrument, false, Array.Empty<Trade>());
        using var data = new InstrumentData(_instrument, feed) { RecordingRoot = _root, RawTradeLimit = 3 };
        data.Start(Start);
        await Ready(data);
        Assert.Null(data.HistoryError);
        Assert.Equal(new[] { "e2", "e3", "e4" }, Enumerable.Range(0, data.Trades.Count).Select(i => data.Trades.Get(i).Provenance.SourceEventId));
        Assert.Equal(36m, Enumerable.Range(0, data.Trades.Count).Sum(data.Trades.GetVolume));
        Assert.All(Enumerable.Range(0, data.Trades.Count), i => Assert.True(data.Trades.GetProvenance(i).IsPartial));
        var ticks = TickData.FromStore(data.Trades);
        data.Trades.Replace(Enumerable.Range(ticks.Start, ticks.Count).Select(ticks.Get));
        Assert.Equal("e3", data.Trades.Get(1).Provenance.SourceEventId);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task LabelledQuoteHistoryRemainsDisplayableAndRecordedWithoutObservedTrades()
    {
        var provenance = new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial,
            SourceIdentity: "quote-source", ObservedTradeCount: 0);
        var rows = new[] { new Trade(1, Start.AddSeconds(1), 100, 0, TradeSide.Buy, provenance),
            new Trade(2, Start.AddSeconds(2), 101, 0, TradeSide.Sell, provenance) };
        using (var feed = new FixtureFeed(_instrument, true, rows))
        using (var data = new InstrumentData(_instrument, feed) { RecordingRoot = _root })
        {
            data.Start(Start);
            await Ready(data);
            Assert.Null(data.HistoryError);
            Assert.Equal(2, data.Trades.Count);
            Assert.Equal(DateTime.MaxValue, data.RealTicksFrom);
            var series = new CandleSeries(_instrument, new ChartPeriod(PeriodType.Minutes, 1));
            series.Rebuild(data.Trades);
            var candle = new IndicatorCandle(series[0], series);
            Assert.True(candle.IsSynthetic);
            Assert.False(candle.HasRawTrades || candle.HasAggregatedFootprint);
            Assert.Equal(0m, candle.Volume);
            Assert.Equal(0m, candle.MaxTradeVolume);
            Assert.Empty(candle.GetAllPriceLevels());
        }
        var stored = HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start));
        Assert.Equal(2, stored.Count);
        Assert.All(stored, row => { Assert.False(row.IsObservedTrade); Assert.Equal(0m, row.Volume); Assert.Equal(0, row.Provenance.ObservedTradeCount); });
    }

    [Fact]
    public void ZeroPriceRemainsAValidObservedPoc()
    {
        var rows = new[] { new Trade(1, Start, 0, 10, TradeSide.Buy), new Trade(2, Start.AddSeconds(1), -1, 1, TradeSide.Sell) };
        var strategy = new ClockProbe();
        var result = new BacktestEngine(strategy, TickData.FromTrades(_instrument, rows), Settings()).Run();
        Assert.Null(result.Error);
        var bar = Assert.Single(strategy.BarsSeen);
        Assert.True(bar.HasObservedPoc);
        Assert.Equal(0m, bar.PocPrice);
    }

    [Fact]
    public async Task ReplayWarmupKeepsEarlierLocatedEventsAvailableInsideRequestedRange()
    {
        var path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start));
        using (var writer = new TickFile.Writer(path, _instrument, includeProvenance: true))
        {
            writer.Write(new Trade(-1, Start, 100, 20, TradeSide.Buy,
                new TradeProvenance(DataQuality.Compact, 300, "source", ObservedTradeCount: 20)));
            writer.Write(Native(1, 270, 1, "point"));
        }
        using var feed = new ReplayFeed(_root, _instrument, Start.AddMinutes(6), Start.AddMinutes(7)) { Paused = true };
        using var data = new InstrumentData(feed.Instrument, feed);
        data.Start(Start.AddMinutes(4));
        await Ready(data);
        Assert.Null(data.HistoryError);
        Assert.Equal(2, data.Trades.Count);
        Assert.Equal(Start, data.Trades.GetTime(0));
        Assert.Equal(Start.AddMinutes(5), data.Trades.Get(0).AvailableAt);
        Assert.True(data.Trades.GetProvenance(0).Quality.HasFlag(DataQuality.Compact));
    }

    [Fact]
    public async Task LegacyQuoteFileHasSameNonObservedMeaningInLiveChartAndHistoryReader()
    {
        var instrument = new Instrument
        {
            FeedId = "ctrader-fixture", Symbol = _instrument.Symbol, TickSize = _instrument.TickSize,
            VolumeStep = _instrument.VolumeStep, PointValue = _instrument.PointValue,
        };
        var path = TickFile.DayPath(_root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start));
        using (var writer = new TickFile.Writer(path, instrument, includeProvenance: false))
            writer.Write(new Trade(1, Start.AddSeconds(1), 100, 1, TradeSide.Buy));
        byte[] original = File.ReadAllBytes(path);
        using var feed = new FixtureFeed(instrument, false, Array.Empty<Trade>());
        using var data = new InstrumentData(instrument, feed) { RecordingRoot = _root };
        data.Start(Start);
        await Ready(data);
        Assert.Null(data.HistoryError);
        Assert.Equal(1, data.Trades.Count);
        var loaded = data.Trades.Get(0);
        var offline = Assert.Single(HistorySources.ReadDay(_root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start)));
        Assert.Equal(loaded.Provenance, offline.Provenance);
        Assert.False(loaded.IsObservedTrade || loaded.Provenance.IsMeasured);
        Assert.True(loaded.HasKnownAvailability && loaded.Provenance.IsPartial);
        Assert.Equal(DateTime.MaxValue, data.RealTicksFrom);
        var series = new CandleSeries(instrument, new ChartPeriod(PeriodType.Minutes, 1));
        series.Rebuild(data.Trades);
        var candle = new IndicatorCandle(series[0], series);
        Assert.False(candle.HasRawTrades || candle.HasAggregatedFootprint);
        Assert.Empty(candle.GetAllPriceLevels());
        Assert.Equal(0m, candle.MaxTradeVolume);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void MixedUnknownCompactCountsKeepExactVolumeWithoutClaimingCompleteTradeCounts()
    {
        var store = new TradeStore(_instrument);
        store.Add(new Trade(1, Start, 100, 5, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "source")));
        store.Add(new Trade(-1, Start.AddSeconds(1), 100, 7, TradeSide.Buy,
            new TradeProvenance(DataQuality.Compact, 60, "source", ObservedTradeCount: 0)));
        store.Add(new Trade(-2, Start.AddSeconds(2), 100, 11, TradeSide.Buy,
            new TradeProvenance(DataQuality.Compact, 60, "source", ObservedTradeCount: 4)));
        var series = new CandleSeries(_instrument, new ChartPeriod(PeriodType.Minutes, 1));
        series.Rebuild(store);
        series.AdvanceAvailability(Start.AddMinutes(1));
        var candle = new IndicatorCandle(series[0], series);
        var aggregate = Assert.Single(candle.GetAggregatedPriceLevels());
        Assert.Equal(18m, aggregate.Volume);
        Assert.False(aggregate.HasTradeCount);
        var profile = Assert.Single(candle.GetProfilePriceLevels());
        Assert.Equal(23m, profile.Volume);
        Assert.False(profile.HasTradeCount);
        Assert.Equal(5m, candle.MaxTradeVolume);
        Assert.Equal(5m, Assert.Single(candle.GetAllPriceLevels()).Volume);
    }

    private static Trade Native(long id, int second, decimal volume, string eventId) => new(id, Start.AddSeconds(second), 100 + second, volume,
        TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "native-source", SourceEventId: eventId));
    private static async Task Ready(InstrumentData data)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        do { data.Pump(); if (!data.IsLoadingHistory) return; await Task.Delay(10, timeout.Token); } while (true);
    }
    private sealed class ClockProbe(bool buyOnInterval = false, bool buyOnFirst = false) : Strategy
    {
        public bool Initialized { get; private set; }
        public List<Trade> CapturedTrades { get; } = [];
        public List<DateTime> Clocks { get; } = [];
        public List<Bar> BarsSeen { get; } = [];
        public StrategyOrder? Order { get; private set; }
        protected override void OnInit() { Initialized = true; UseFootprint = true; }
        protected override void OnBar(Bar bar) => BarsSeen.Add(bar);
        protected override void OnTick(Trade trade)
        {
            CapturedTrades.Add(trade); Clocks.Add(Time);
            if (Order == null && (buyOnFirst || buyOnInterval && trade.Provenance.ResolutionSeconds > 0)) Order = Buy(1);
        }
    }
    private sealed class FixtureFeed(Instrument instrument, bool history, IReadOnlyList<Trade> rows) : IMarketDataFeed
    {
        public string Id => instrument.FeedId;
        public string DisplayName => "History quality fixture";
        public bool SupportsHistory => history;
        public bool IsLive => true;
        public DateTime Now => Start.AddSeconds(10);
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { instrument });
        public IDisposable Subscribe(Instrument selected, IMarketDataSink sink) { sink.OnStatus(selected, FeedStatus.Connected, null); return new Subscription(); }
        public Task LoadTradesAsync(Instrument selected, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch,
            IProgress<double>? progress, CancellationToken ct) { onBatch(rows); return Task.CompletedTask; }
        public void Dispose() { }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
