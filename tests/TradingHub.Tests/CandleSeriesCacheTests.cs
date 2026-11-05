using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class CandleSeriesCacheTests
{
    private static readonly DateTime Start = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Instrument Instrument = CreateInstrument();

    [Fact]
    public void HotRestorePreservesActualAggregationAndProvenanceButNotOpenInterestDecorations()
    {
        const decimal preciseQuantity = 0.1234567890123456789012345678m;
        var store = CreateStore(
            new Trade(1, Start, 100, preciseQuantity, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide, SourceIdentity: "venue:tape", CorrectionRevision: 7)),
            new Trade(2, Start.AddSeconds(1), 101, 3, TradeSide.Sell,
                new TradeProvenance(DataQuality.Compact, 60, "venue:compact", 9, 11)),
            new Trade(-3, Start.AddSeconds(2), 102, 4, TradeSide.None,
                new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, 60, "venue:bars", 12, 0, false)),
            new Trade(4, Start.AddMinutes(1), 103, 5, TradeSide.Buy));
        var original = Build(store, ChartPeriod.M1);
        original.AdvanceAvailability(Start.AddMinutes(2));
        original[0].OI = 77;
        original[0].MaxOI = 80;
        original[0].MinOI = 70;
        original[0].HasOpenInterest = original[0].HasOpenInterestRange = original[0].HasOpenInterestChange = true;
        original[0].OpenInterestTime = Start;
        original[0].OpenInterestChange = 4;
        original[0].OpenInterestStatus = "provider decoration";
        var cache = new CandleSeriesCache();
        cache.Store(original, store, "account:venue");

        var restored = new CandleSeries(Instrument, ChartPeriod.M1);
        Guid chartIdentity = restored.Identity;
        restored.Rebuild(CreateStore(new Trade(90, Start.AddDays(-1), 999, 99, TradeSide.None)));
        Assert.True(cache.TryRestore(restored, store, "account:venue"));
        AssertAggregationEqual(original, restored);
        Assert.Equal(chartIdentity, restored.Identity);
        Assert.NotEqual(original.Identity, restored.Identity);
        Assert.Equal(original.Revision, restored.Revision);
        Assert.Equal(original.Candles.Select(candle => candle.Revision), restored.Candles.Select(candle => candle.Revision));
        Assert.False(restored.HasPendingTrades(store));
        Assert.Equal(2, restored.Count);
        Assert.Equal((100L, 102L, 100L, 102L),
            (restored[0].OpenTicks, restored[0].HighTicks, restored[0].LowTicks, restored[0].CloseTicks));
        Assert.Equal(preciseQuantity + 7m, restored[0].Volume);
        Assert.Equal(preciseQuantity, restored[0].Levels.Get(100).Ask);
        Assert.Equal(3m, restored[0].AggregatedLevels.Get(101).Bid);
        Assert.Equal(11, restored[0].AggregatedTrades);
        Assert.Equal(1, restored[0].ObservedTrades);
        Assert.Equal(4m, restored[0].LastTradeVolume);
        Assert.Equal(12, restored[0].CorrectionRevision);
        Assert.False(restored[0].HasKnownAvailability);
        Assert.All(restored.Candles, candle =>
        {
            Assert.Equal(0m, candle.OI);
            Assert.Equal(0m, candle.MaxOI);
            Assert.Equal(0m, candle.MinOI);
            Assert.False(candle.HasOpenInterest);
            Assert.False(candle.HasOpenInterestRange);
            Assert.False(candle.HasOpenInterestChange);
            Assert.Null(candle.OpenInterestTime);
            Assert.Null(candle.OpenInterestStatus);
            Assert.Equal(0m, candle.OpenInterestChange);
        });
    }

    [Fact]
    public void AppendedTimeTradesReuseTheCachedPrefixAndResumeItsFormingBucket()
    {
        var store = CreateStore(new Trade(1, Start, 100, 2, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(30), 101, 3, TradeSide.Sell));
        var original = Build(store, ChartPeriod.M1);
        var cache = new CandleSeriesCache();
        cache.Store(original, store, "venue");
        store.Add(new Trade(3, Start.AddSeconds(45), 102, 5, TradeSide.Buy));
        store.Add(new Trade(4, Start.AddSeconds(65), 99, 7, TradeSide.Sell));

        var restored = new CandleSeries(Instrument, ChartPeriod.M1);
        Assert.True(cache.TryRestore(restored, store, "venue"));
        Assert.Equal(2, restored.ProcessedTrades);
        Assert.True(restored.HasPendingTrades(store));
        Assert.Equal(5m, Assert.Single(restored.Candles).Volume);
        restored.Append(store, 1);
        Assert.Equal(10m, Assert.Single(restored.Candles).Volume);
        Assert.Equal(102, restored[0].CloseTicks);
        restored.Append(store, 1);
        AssertAggregationEqual(Build(store, ChartPeriod.M1), restored);
        Assert.Equal(new[] { 10m, 7m }, restored.Candles.Select(candle => candle.Volume));
        Assert.Equal(new[] { 5m, 7m }, restored.Candles.Select(candle => candle.LastTradeVolume));
        Assert.False(restored.Append(store).Changed);
    }

    [Theory]
    [InlineData(PeriodType.Tick, 3)]
    [InlineData(PeriodType.Range, 3)]
    [InlineData(PeriodType.Delta, 4)]
    public void EventBasedRestoreResumesTheExistingFormingBarBeforeOpeningTheNextOne(PeriodType type, int value)
    {
        var store = CreateStore(new Trade(1, Start, 100, 2, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(1), 102, 3, TradeSide.Sell));
        var period = new ChartPeriod(type, value);
        var original = Build(store, period);
        var cache = new CandleSeriesCache();
        cache.Store(original, store, "venue");
        store.Add(new Trade(3, Start.AddSeconds(2), 99, 6, TradeSide.Buy));
        store.Add(new Trade(4, Start.AddSeconds(3), 106, 2, TradeSide.Sell));
        store.Add(new Trade(5, Start.AddSeconds(4), 103, 5, TradeSide.None));
        var restored = new CandleSeries(Instrument, period);
        Assert.True(cache.TryRestore(restored, store, "venue"));
        while (restored.HasPendingTrades(store)) restored.Append(store, 1);
        AssertAggregationEqual(Build(store, period), restored);
        Assert.Equal(new[] { 11m, 7m }, restored.Candles.Select(candle => candle.Volume));
        Assert.Equal(new[] { 6m, 5m }, restored.Candles.Select(candle => candle.LastTradeVolume));
    }

    [Theory]
    [InlineData(11, 7, 2)]
    [InlineData(14, 10, 5)]
    public void VolumeRestoreResumesBothSplitAndExactlyFullFormingBars(int secondVolume, int formingVolume, int finalVolume)
    {
        var period = new ChartPeriod(PeriodType.Volume, 10);
        var store = CreateStore(new Trade(1, Start, 100, 6, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(1), 102, secondVolume, TradeSide.Sell));
        var original = Build(store, period);
        Assert.Equal(new[] { 10m, (decimal)formingVolume }, original.Candles.Select(candle => candle.Volume));
        Assert.Equal(new[] { 4m, (decimal)formingVolume }, original.Candles.Select(candle => candle.LastTradeVolume));
        var cache = new CandleSeriesCache();
        cache.Store(original, store, "venue");
        store.Add(new Trade(3, Start.AddSeconds(2), 101, 15, TradeSide.Buy));

        var restored = new CandleSeries(Instrument, period);
        Assert.True(cache.TryRestore(restored, store, "venue"));
        Assert.Equal(2, restored.ProcessedTrades);
        Assert.True(restored.Append(store, 1).Changed);
        AssertAggregationEqual(Build(store, period), restored);
        Assert.Equal(new[] { 10m, 10m, 10m, (decimal)finalVolume }, restored.Candles.Select(candle => candle.Volume));
        Assert.Equal(finalVolume, restored.Last!.LastTradeVolume);
        Assert.Equal(formingVolume, original.Last!.Volume);
        Assert.False(restored.Append(store).Changed);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void RenkoRestoreResumesDirectionalReversalThresholdsAndOriginalFootprints(int prefixCount)
    {
        var trades = new[]
        {
            new Trade(1, Start, 100, 2, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(1), 115, 3, TradeSide.Sell),
            new Trade(3, Start.AddSeconds(2), 124, 4, TradeSide.Buy),
            new Trade(4, Start.AddSeconds(3), 105, 5, TradeSide.Sell),
            new Trade(5, Start.AddSeconds(4), 99, 6, TradeSide.Buy),
            new Trade(6, Start.AddSeconds(5), 115, 7, TradeSide.Buy),
            new Trade(7, Start.AddSeconds(6), 121, 8, TradeSide.Sell)
        };
        var store = CreateStore(trades[..prefixCount]);
        var period = new ChartPeriod(PeriodType.Renko, 10);
        var original = Build(store, period);
        var cache = new CandleSeriesCache();
        cache.Store(original, store, "venue");
        foreach (var trade in trades[prefixCount..]) store.Add(trade);

        var restored = new CandleSeries(Instrument, period);
        Assert.True(cache.TryRestore(restored, store, "venue"));
        while (restored.HasPendingTrades(store)) restored.Append(store, 1);
        AssertAggregationEqual(Build(store, period), restored);
        Assert.Equal(new long[] { 110, 120, 100, 120, 121 }, restored.Candles.Select(candle => candle.CloseTicks));
        Assert.Equal(new[] { 2m, 3m, 9m, 13m, 8m }, restored.Candles.Select(candle => candle.Volume));
        Assert.Equal(new[] { 2m, 3m, 5m, 7m, 8m }, restored.Candles.Select(candle => candle.LastTradeVolume));
        Assert.Equal(4m, restored[2].Levels.Get(124).Ask);
        Assert.Equal(5m, restored[2].Levels.Get(105).Bid);
    }

    [Fact]
    public void OnlyFullyProcessedStoresArePublishedAndAnUnprocessedChartCannotReplaceAnAdmittedPrefix()
    {
        var store = CreateStore(new Trade(1, Start, 100, 2, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(1), 101, 3, TradeSide.Sell));
        var building = new CandleSeries(Instrument, ChartPeriod.M1);
        building.BeginRebuild(store);
        building.Append(store, 1);
        var cache = new CandleSeriesCache();
        cache.Store(building, store, "venue");
        Assert.Equal(0, cache.EntryCount);
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), store, "venue"));

        building.Append(store, 1);
        cache.Store(building, store, "venue");
        store.Add(new Trade(3, Start.AddSeconds(2), 102, 5, TradeSide.Buy));
        cache.Store(building, store, "venue");
        var restored = new CandleSeries(Instrument, ChartPeriod.M1);
        Assert.True(cache.TryRestore(restored, store, "venue"));
        Assert.Equal(2, restored.ProcessedTrades);
        Assert.Equal(5m, restored.Last!.Volume);
        restored.Append(store);
        AssertAggregationEqual(Build(store, ChartPeriod.M1), restored);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("replace")]
    [InlineData("clear")]
    public void EqualCountEqualTotalCorrectionsMissInsteadOfReusingTheOldDistribution(string mutation)
    {
        var first = new Trade(1, Start, 100, 2, TradeSide.Buy);
        var second = new Trade(2, Start.AddSeconds(1), 101, 3, TradeSide.Sell);
        var store = CreateStore(first, second);
        var cache = new CandleSeriesCache();
        var original = Build(store, ChartPeriod.M1);
        cache.Store(original, store, "venue");
        var restored = new CandleSeries(Instrument, ChartPeriod.M1);
        Assert.True(cache.TryRestore(restored, store, "venue"));
        long previousCorrection = restored.CorrectionRevision;
        var correction = new Trade(2, second.Time, 104, 3, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide, SourceIdentity: "revised", CorrectionRevision: 14));
        switch (mutation)
        {
            case "update": store.Update(1, correction); break;
            case "replace": store.Replace(new[] { first, correction }); break;
            case "clear": store.Clear(); store.Add(first); store.Add(correction); break;
        }

        Assert.Equal(2, store.Count);
        Assert.Equal(5m, store.GetVolume(0) + store.GetVolume(1));
        cache.Store(original, store, "venue");
        Assert.False(cache.TryRestore(restored, store, "venue"));
        Assert.Equal(3m, restored[0].Levels.Get(101).Bid);
        Assert.True(restored.Append(store).Changed);
        Assert.True(restored.CorrectionRevision > previousCorrection);
        Assert.Equal(5m, restored[0].Volume);
        Assert.False(restored[0].Levels.TryGet(101, out _));
        Assert.Equal(3m, restored[0].Levels.Get(104).Ask);
        Assert.Equal(14, restored[0].CorrectionRevision);
        cache.Store(restored, store, "venue");
        var correctedCopy = new CandleSeries(Instrument, ChartPeriod.M1);
        Assert.True(cache.TryRestore(correctedCopy, store, "venue"));
        AssertAggregationEqual(restored, correctedCopy);
    }

    [Fact]
    public void ProviderStoreActualInstrumentMetadataAndChartConfigurationCannotShareASnapshot()
    {
        var trade = new Trade(1, Start, 100, 2, TradeSide.Buy);
        var store = CreateStore(trade);
        var cache = new CandleSeriesCache();
        cache.Store(Build(store, ChartPeriod.M1), store, "provider:A");
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), store, "provider:B"));
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), CreateStore(trade), "provider:A"));
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M5), store, "provider:A"));
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1, 2), store, "provider:A"));
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1, session:
            new SessionSettings { Start = TimeSpan.FromHours(9) }), store, "provider:A"));
        var changedMetadata = new[]
        {
            CreateInstrument(tickSize: 0.25m), CreateInstrument(volumeStep: 0.5m),
            CreateInstrument(exchange: "Other venue"), CreateInstrument(pointValue: 10m),
            CreateInstrument(description: "Other contract"), CreateInstrument(baseAsset: "OTHER"),
            CreateInstrument(quoteAsset: "OTHER"), CreateInstrument(feedId: "other-feed"),
            CreateInstrument(symbol: "OTHER")
        };
        foreach (var instrument in changedMetadata)
            Assert.False(cache.TryRestore(new CandleSeries(instrument, ChartPeriod.M1), store, "provider:A"));

        Assert.True(cache.TryRestore(new CandleSeries(CreateInstrument(), ChartPeriod.M1), store, "provider:A"));
    }

    [Fact]
    public void SessionIdentityIncludesTimezoneRulesEvenWhenTheTimezoneIdentifierAndBaseOffsetMatch()
    {
        var store = CreateStore(new Trade(1, Start, 100, 2, TradeSide.Buy));
        var session = new SessionSettings { Start = TimeSpan.FromHours(9), TimeZone = CreateZone(1) };
        var cache = new CandleSeriesCache();
        cache.Store(Build(store, ChartPeriod.D1, session: session), store, "venue");
        var equivalent = new SessionSettings { Start = session.Start, TimeZone = CreateZone(1) };
        Assert.True(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.D1, session: equivalent), store, "venue"));

        var changedRules = new SessionSettings { Start = session.Start, TimeZone = CreateZone(2) };
        Assert.Equal(session.TimeZone.Id, changedRules.TimeZone.Id);
        Assert.Equal(session.TimeZone.BaseUtcOffset, changedRules.TimeZone.BaseUtcOffset);
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.D1, session: changedRules), store, "venue"));
        var changedName = new SessionSettings { Start = session.Start, TimeZone = CreateZone(1, "Different display name") };
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.D1, session: changedName), store, "venue"));
        var changedStart = new SessionSettings { Start = session.Start.Add(TimeSpan.FromMinutes(30)), TimeZone = session.TimeZone };
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.D1, session: changedStart), store, "venue"));
    }

    [Fact]
    public void CompletedAndFormingRawAndAggregatedRowsAreOwnedByEachRestoredChart()
    {
        var store = CreateStore(
            new Trade(1, Start, 100, 2, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(1), 101, 3, TradeSide.Sell, new TradeProvenance(DataQuality.Compact, 60, "compact", 0, 4)),
            new Trade(3, Start.AddMinutes(1), 102, 5, TradeSide.Buy),
            new Trade(4, Start.AddSeconds(61), 103, 7, TradeSide.Buy, new TradeProvenance(DataQuality.Compact, 60, "compact", 0, 6)));
        var original = Build(store, ChartPeriod.M1);
        var cache = new CandleSeriesCache();
        cache.Store(original, store, "venue");
        original[0].Levels.Clear();
        original[0].AggregatedLevels.Clear();
        original.Last!.Levels.Clear();
        original.Last.AggregatedLevels.Clear();
        original.Last.Volume = 999;
        var first = new CandleSeries(Instrument, ChartPeriod.M1);
        var second = new CandleSeries(Instrument, ChartPeriod.M1);
        Assert.True(cache.TryRestore(first, store, "venue"));
        Assert.True(cache.TryRestore(second, store, "venue"));
        first[0].Levels.GetOrAdd(100).Ask = 200;
        first[0].AggregatedLevels.GetOrAdd(101).Bid = 300;
        first.Last!.Levels.GetOrAdd(102).Bid = 500;
        first.Last.AggregatedLevels.GetOrAdd(103).Ask = 700;
        first.Last.Volume = first.Last.LastTradeVolume = 999;
        first.Last.CloseTicks = 999;

        Assert.Equal(2m, second[0].Levels.Get(100).Ask);
        Assert.Equal(3m, second[0].AggregatedLevels.Get(101).Bid);
        Assert.Equal(5m, second.Last!.Levels.Get(102).Ask);
        Assert.Equal(0m, second.Last.Levels.Get(102).Bid);
        Assert.Equal(7m, second.Last.AggregatedLevels.Get(103).Ask);
        Assert.Equal(12m, second.Last.Volume);
        Assert.Equal(103, second.Last.CloseTicks);
        Assert.Equal(7m, second.Last.LastTradeVolume);
        store.Add(new Trade(5, Start.AddSeconds(62), 104, 11, TradeSide.Sell));
        second.Append(store);
        Assert.Equal(23m, second.Last.Volume);
        var third = new CandleSeries(Instrument, ChartPeriod.M1);
        Assert.True(cache.TryRestore(third, store, "venue"));
        Assert.Equal(4, third.ProcessedTrades);
        Assert.Equal(12m, third.Last!.Volume);
        Assert.Equal(7m, third.Last.LastTradeVolume);
        Assert.False(third.Last.Levels.TryGet(104, out _));
        Assert.Equal(2m, third[0].Levels.Get(100).Ask);
        Assert.Equal(3m, third[0].AggregatedLevels.Get(101).Bid);
    }

    [Fact]
    public void SparseNegativeAndExtremePriceRowsRemainCacheableWithinASmallByteBudget()
    {
        var store = CreateStore(
            new Trade(1, Start, -1_000_000_000_001m, 1, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(1), 1_000_000_000_002m, 2, TradeSide.Sell),
            new Trade(3, Start.AddSeconds(2), -1_000_000_000_004m, 3, TradeSide.Sell, new TradeProvenance(DataQuality.Compact, 60, "compact", 0, 7)),
            new Trade(4, Start.AddSeconds(3), 1_000_000_000_005m, 4, TradeSide.Buy, new TradeProvenance(DataQuality.Compact, 60, "compact", 0, 9)));
        var original = Build(store, ChartPeriod.M1, priceStep: 3);
        var cache = new CandleSeriesCache(maxBytes: 16 * 1024, maxEntries: 2);
        cache.Store(original, store, "venue");
        var restored = new CandleSeries(Instrument, ChartPeriod.M1, 3);
        Assert.True(cache.TryRestore(restored, store, "venue"));
        AssertAggregationEqual(original, restored);
        Assert.Equal(new long[] { -333_333_333_334, 333_333_333_334 }, restored[0].Levels.Enumerate().Select(level => level.Row));
        Assert.Equal(new long[] { -333_333_333_335, 333_333_333_335 }, restored[0].AggregatedLevels.Enumerate().Select(level => level.Row));
        Assert.InRange(cache.RetainedBytes, 1, 16 * 1024);
        store.Add(new Trade(5, Start.AddSeconds(4), -1_000_000_000_001m, 5, TradeSide.None));
        restored.Append(store);
        AssertAggregationEqual(Build(store, ChartPeriod.M1, priceStep: 3), restored);
        Assert.Equal(15m, restored[0].Volume);
    }

    [Fact]
    public void EntryLimitEvictsTheLeastRecentlyRestoredAggregation()
    {
        var a = CreateStore(new Trade(1, Start, 100, 1, TradeSide.Buy));
        var b = CreateStore(new Trade(1, Start, 101, 2, TradeSide.Buy));
        var c = CreateStore(new Trade(1, Start, 102, 3, TradeSide.Buy));
        var cache = new CandleSeriesCache(maxBytes: 1024 * 1024, maxEntries: 2);
        cache.Store(Build(a, ChartPeriod.M1), a, "venue");
        cache.Store(Build(b, ChartPeriod.M1), b, "venue");
        Assert.True(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), a, "venue"));
        cache.Store(Build(c, ChartPeriod.M1), c, "venue");
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), b, "venue"));
        Assert.True(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), a, "venue"));
        Assert.True(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), c, "venue"));
        Assert.Equal(2, cache.EntryCount);
        Assert.InRange(cache.RetainedBytes, 1, 1024 * 1024);
    }

    [Fact]
    public void ByteLimitEvictsOlderSnapshotsAndRejectsOversizedRowsWithoutDiscardingAnAdmittedEntry()
    {
        var a = CreateStore(new Trade(1, Start, 100, 1, TradeSide.Buy));
        var b = CreateStore(new Trade(1, Start, 101, 2, TradeSide.Buy));
        var aSeries = Build(a, ChartPeriod.M1);
        var measuringCache = new CandleSeriesCache();
        measuringCache.Store(aSeries, a, "venue");
        long budget = measuringCache.RetainedBytes + measuringCache.RetainedBytes / 2;
        var cache = new CandleSeriesCache(maxBytes: budget, maxEntries: 8);
        cache.Store(aSeries, a, "venue");
        cache.Store(Build(b, ChartPeriod.M1), b, "venue");
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), a, "venue"));
        Assert.True(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), b, "venue"));
        var large = new TradeStore(Instrument);
        for (int i = 0; i < 512; i++) large.Add(new Trade(i + 1, Start.AddTicks(i), 1000 + i, 1, TradeSide.Buy));
        cache.Store(Build(large, ChartPeriod.M1), large, "venue");
        Assert.False(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), large, "venue"));
        Assert.True(cache.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), b, "venue"));
        Assert.Equal(1, cache.EntryCount);
        Assert.InRange(cache.RetainedBytes, 1, budget);
        var tooSmall = new CandleSeriesCache(maxBytes: 1);
        tooSmall.Store(aSeries, a, "venue");
        Assert.False(tooSmall.TryRestore(new CandleSeries(Instrument, ChartPeriod.M1), a, "venue"));
        Assert.Equal(0, tooSmall.EntryCount);
        Assert.Equal(0, tooSmall.RetainedBytes);
    }

    [Fact]
    public void KnownOffsetSourceAvailabilityResumesCausallyAndIsPreservedAfterReadmission()
    {
        var store = CreateStore(new Trade(1, Start.AddSeconds(35), 100, 4, TradeSide.Buy,
            new TradeProvenance(DataQuality.Compact, 60, "offset-source", 3, 9, true, 30)));
        var original = Build(store, ChartPeriod.M5);
        var cache = new CandleSeriesCache();
        cache.Store(original, store, "venue");
        var restored = new CandleSeries(Instrument, ChartPeriod.M5);
        Assert.True(cache.TryRestore(restored, store, "venue"));
        var sdk = new IndicatorCandle(restored[0], restored);
        Assert.Equal(Start.AddSeconds(30), restored[0].EarliestSourceTime);
        Assert.Equal(Start.AddSeconds(90), sdk.AvailableAt);
        Assert.False(sdk.CanCalculateCausally);
        Assert.False(restored.AdvanceAvailability(Start.AddSeconds(89)));
        Assert.True(restored.AdvanceAvailability(Start.AddSeconds(90)));
        Assert.True(sdk.CanCalculateCausally);
        Assert.False(restored.AdvanceAvailability(Start.AddSeconds(90)));
        cache.Store(restored, store, "venue");
        var later = new CandleSeries(Instrument, ChartPeriod.M5);
        Assert.True(cache.TryRestore(later, store, "venue"));
        Assert.Equal(restored.Revision, later.Revision);
        Assert.Equal(restored[0].Revision, later[0].Revision);
        Assert.True(new IndicatorCandle(later[0], later).CanCalculateCausally);
        Assert.False(later.AdvanceAvailability(Start.AddSeconds(90)));
    }

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 300)]
    public void UnknownOrIncompatibleAvailabilityDoesNotBecomeCausalAfterRestore(bool knownAvailability, int resolution)
    {
        var store = CreateStore(new Trade(1, Start.AddSeconds(1), 100, 4, TradeSide.Sell,
            new TradeProvenance(DataQuality.Compact, resolution, "coarse-source", 5, 9, knownAvailability)));
        var original = Build(store, ChartPeriod.M1);
        var cache = new CandleSeriesCache();
        cache.Store(original, store, "venue");
        var restored = new CandleSeries(Instrument, ChartPeriod.M1);
        Assert.True(cache.TryRestore(restored, store, "venue"));
        AssertAggregationEqual(original, restored);
        Assert.False(new IndicatorCandle(restored[0], restored).CanCalculateCausally);
        Assert.False(restored.AdvanceAvailability(Start.AddHours(1)));
        Assert.False(new IndicatorCandle(restored[0], restored).CanCalculateCausally);
    }

    private static Instrument CreateInstrument(decimal tickSize = 1m, decimal volumeStep = 0.00000001m,
        string exchange = "Fixture", decimal pointValue = 2m, string description = "Cache fixture",
        string baseAsset = "BASE", string quoteAsset = "QUOTE", string feedId = "cache-fixture", string symbol = "CACHE") => new()
    {
        Symbol = symbol, FeedId = feedId, TickSize = tickSize, VolumeStep = volumeStep,
        Exchange = exchange, PointValue = pointValue, BaseAsset = baseAsset, QuoteAsset = quoteAsset, Description = description
    };

    private static TimeZoneInfo CreateZone(int daylightHours, string displayName = "Cache fixture zone")
    {
        var transitionStart = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 10);
        var transitionEnd = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 3);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31),
            TimeSpan.FromHours(daylightHours), transitionStart, transitionEnd);
        return TimeZoneInfo.CreateCustomTimeZone("Cache/Rules", TimeSpan.Zero, displayName, "Fixture standard", "Fixture daylight", new[] { rule });
    }

    private static TradeStore CreateStore(params Trade[] trades)
    {
        var store = new TradeStore(Instrument);
        foreach (var trade in trades) store.Add(trade);
        return store;
    }

    private static CandleSeries Build(TradeStore store, ChartPeriod period, int priceStep = 1, SessionSettings? session = null)
    {
        var series = new CandleSeries(store.Instrument, period, priceStep, session);
        series.Rebuild(store);
        return series;
    }

    private static void AssertAggregationEqual(CandleSeries expected, CandleSeries actual)
    {
        Assert.Equal(expected.ProcessedTrades, actual.ProcessedTrades);
        Assert.Equal(expected.AvailableThrough, actual.AvailableThrough);
        Assert.Equal(expected.SourceIdentity, actual.SourceIdentity);
        Assert.Equal(expected.CorrectionRevision, actual.CorrectionRevision);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var left = expected[i];
            var right = actual[i];
            Assert.NotSame(left, right);
            Assert.Equal((left.Time, left.LastTime, left.OpenTicks, left.HighTicks, left.LowTicks, left.CloseTicks),
                (right.Time, right.LastTime, right.OpenTicks, right.HighTicks, right.LowTicks, right.CloseTicks));
            Assert.Equal((left.Volume, left.Bid, left.Ask, left.Between, left.MaxDelta, left.MinDelta, left.Trades),
                (right.Volume, right.Bid, right.Ask, right.Between, right.MaxDelta, right.MinDelta, right.Trades));
            Assert.Equal((left.PocRow, left.PocVolume, left.MaxTradeVolume, left.FirstTradeIndex, left.LastTradeIndex, left.LastTradeVolume),
                (right.PocRow, right.PocVolume, right.MaxTradeVolume, right.FirstTradeIndex, right.LastTradeIndex, right.LastTradeVolume));
            Assert.Equal((left.IsClosed, left.HasRealTrades, left.HasCompactTrades, left.HasSyntheticTrades, left.Quality, left.ResolutionSeconds),
                (right.IsClosed, right.HasRealTrades, right.HasCompactTrades, right.HasSyntheticTrades, right.Quality, right.ResolutionSeconds));
            Assert.Equal((left.SourceIdentity, left.CorrectionRevision, left.ObservedTrades, left.AggregatedTrades),
                (right.SourceIdentity, right.CorrectionRevision, right.ObservedTrades, right.AggregatedTrades));
            Assert.Equal((left.EarliestSourceTime, left.AvailableAt, left.HasKnownAvailability, left.HasIncompatibleResolution),
                (right.EarliestSourceTime, right.AvailableAt, right.HasKnownAvailability, right.HasIncompatibleResolution));
            Assert.NotSame(left.Levels, right.Levels);
            Assert.NotSame(left.AggregatedLevels, right.AggregatedLevels);
            Assert.Equal(left.Levels.Enumerate().ToArray(), right.Levels.Enumerate().ToArray());
            Assert.Equal(left.AggregatedLevels.Enumerate().ToArray(), right.AggregatedLevels.Enumerate().ToArray());
        }
    }
}
