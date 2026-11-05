using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Market;

namespace TradingHub.Tests;

[Collection("KvpHistoryCache")]
public sealed class HistoryCacheBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-history-boundaries-" + Guid.NewGuid().ToString("N"));
    private readonly Instrument _instrument = new() { FeedId = "cache-boundary-fixture", Symbol = "SIGNED", TickSize = 1m, VolumeStep = 0.00000001m };
    private static readonly DateTime Start = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
    private string CompactRoot => Path.Combine(_root, "compact");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void PersistedNegativeAndZeroPricesRemainMeasuredAggregatesThroughTheSdkAndReuseTheCache()
    {
        var day = DateOnly.FromDateTime(Start);
        string rawPath = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        var raw = new[] {
            new Trade(1, Start, 0, 2, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(1), -2, 3, TradeSide.Sell),
            new Trade(3, Start.AddSeconds(2), -1, 5, TradeSide.Buy),
            new Trade(4, Start.AddSeconds(3), -1, 7, TradeSide.Sell),
            new Trade(5, Start.AddSeconds(4), 0, 11, TradeSide.Buy),
        };
        using (var writer = new TickFile.Writer(rawPath, _instrument))
            foreach (var trade in raw) writer.Write(trade);
        byte[] original = File.ReadAllBytes(rawPath);
        var first = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        string cachePath = CompactHistory.CachePath(CompactRoot, _instrument.FeedId, _instrument.Symbol, day, 1);
        var persisted = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        Assert.Equal(first.Select(Value), persisted.Select(Value));
        Assert.Equal(original, File.ReadAllBytes(rawPath));
        AssertCompactFootprint(persisted);

        // The source fingerprint remains available, but decoding cannot rescue a rejected cache.
        // Re-authenticating its source digest makes this a direct cache-hit behavioral probe.
        try
        {
            File.WriteAllBytes(rawPath, "not a tick file"u8.ToArray());
            RewriteCompactCache(cachePath, SHA256.HashData(File.ReadAllBytes(rawPath)));
            var cachedOnly = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
            Assert.Equal(first.Select(Value), cachedOnly.Select(Value));
            AssertCompactFootprint(cachedOnly);
        }
        finally { File.WriteAllBytes(rawPath, original); }

        RewriteCompactCache(cachePath, SHA256.HashData(original));
        byte[] corrupt = File.ReadAllBytes(cachePath);
        corrupt[^1] ^= 0x5A;
        File.WriteAllBytes(cachePath, corrupt);
        var rebuilt = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        Assert.Equal(first.Select(Value), rebuilt.Select(Value));
        AssertCompactFootprint(rebuilt);
        Assert.Equal(original, File.ReadAllBytes(rawPath));
    }

    [Fact]
    public void MixedSourceAvailabilityAndConstituentCountsSurviveBothCompactionEntrypointsAndPersistence()
    {
        const string source = "local-reference:50220ba91613161685f84384825d855cb4dbeb76";
        var raw = new[] {
            new Trade(1, Start, 3, 1, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(1), -1, 2, TradeSide.Sell),
            new Trade(3, Start.AddSeconds(20), 0, 3, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, 60, source, 7, 2)),
            new Trade(4, Start.AddSeconds(21), 0, 5, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, 60, source, 7, 3, false)),
            new Trade(5, Start.AddSeconds(22), 0, 7, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, 30, source, 7, 5)),
            new Trade(6, Start.AddSeconds(23), 0, 11, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, 30, source, 7, 7, true, 15)),
            new Trade(7, Start.AddSeconds(50), 3, 13, TradeSide.Buy),
        };
        var day = DateOnly.FromDateTime(Start);
        string rawPath = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        using (var writer = new TickFile.Writer(rawPath, _instrument, includeProvenance: true))
            foreach (var trade in raw) writer.Write(trade);
        byte[] original = File.ReadAllBytes(rawPath);
        var direct = CompactHistory.Compress(raw, _instrument.TickSize, 1);
        var streamed = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        var cached = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        foreach (var compact in new[] { direct, streamed, cached })
        {
            Assert.Equal(42m, compact.Sum(trade => trade.Volume));
            Assert.Equal(20, compact.Sum(trade => trade.Provenance.ObservedTradeCount));
            var samePrice = compact.Where(trade => trade.Price == 0).ToArray();
            Assert.Equal(4, samePrice.Length);
            Assert.Equal(5m, samePrice.Where(trade => !trade.HasKnownAvailability).Sum(trade => trade.Volume));
            Assert.Equal(3, samePrice.Where(trade => !trade.HasKnownAvailability).Sum(trade => trade.Provenance.ObservedTradeCount));
            Assert.Equal(21m, samePrice.Where(trade => trade.HasKnownAvailability).Sum(trade => trade.Volume));
            Assert.All(samePrice, trade => {
                Assert.Equal(source, trade.Provenance.SourceIdentity);
                Assert.Equal(7, trade.Provenance.CorrectionRevision);
                Assert.Equal(60, trade.Provenance.ResolutionSeconds);
                Assert.Equal(0, trade.Provenance.ResolutionOffsetSeconds);
                Assert.Equal(Start.AddMinutes(1), trade.AvailableAt);
            });
            var series = Series(compact, ChartPeriod.M1);
            var sdk = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.False(sdk.HasKnownAvailability);
            Assert.Equal(0m, sdk.MaxTradeVolume);
            Assert.Equal(20, sdk.AggregatedTrades);
        }
        Assert.Equal(streamed.Select(Value), cached.Select(Value));
        var identities = cached.Where(trade => trade.Provenance.SourceIdentity == source).Select(trade => trade.Provenance.SourceIdentity).ToArray();
        Assert.All(identities, identity => Assert.Same(identities[0], identity));
        Assert.Equal(original, File.ReadAllBytes(rawPath));
    }

    [Fact]
    public void DerivedRowsNeverCarryIndividualOpaqueEventIdsOrSplitAggregatesByThem()
    {
        var raw = Enumerable.Range(1, 25).Select(id => new Trade(id, Start.AddSeconds(id), 0, 1, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "opaque-provider", SourceEventId: "opaque-event-" + id))).ToArray();
        var direct = CompactHistory.Compress(raw, _instrument.TickSize, 1);
        var day = DateOnly.FromDateTime(Start);
        string path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        using (var writer = new TickFile.Writer(path, _instrument))
            foreach (var trade in raw) writer.Write(trade);
        byte[] original = File.ReadAllBytes(path);
        var streamed = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        var cached = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        foreach (var compact in new[] { direct, streamed, cached })
        {
            Assert.Equal(3, compact.Count);
            Assert.Equal(25m, compact.Sum(trade => trade.Volume));
            Assert.Equal(25, compact.Sum(trade => trade.Provenance.ObservedTradeCount));
            Assert.All(compact, trade => Assert.Null(trade.Provenance.SourceEventId));
            var series = Series(compact, ChartPeriod.M1);
            var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.Equal(25, candle.AggregatedTrades);
            Assert.Equal(0m, candle.MaxTradeVolume);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        var proxies = new List<Trade>();
        KlineHistory.Synthesize(new[] { new Kline(Ms(Start), 0, 1, -1, 0, 7, 5, true,
            new TradeProvenance(DataQuality.Synthetic, 60, "bar-provider", SourceEventId: "original-bar-id")) },
            _instrument, 1, new[] { (Start, Start.AddMinutes(1)) }, proxies);
        Assert.Equal(7m, proxies.Sum(trade => trade.Volume));
        Assert.All(proxies, trade => Assert.Null(trade.Provenance.SourceEventId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonadjacentOpaqueCorrectionsUseOnlyTheirFinalOccurrenceAndKeepOtherSources(bool movesBackward)
    {
        const string firstId = "f7ccebc6-a5cd-4af8-a942-f4118f82668a";
        const string secondId = "0d661de7-5e75-4c11-bd7b-15b5e5a2a409";
        var raw = new[] {
            new Trade(1, Start, -1, 1, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-A", CorrectionRevision: 1, SourceEventId: firstId)),
            new Trade(2, Start.AddSeconds(5), 0, 2, TradeSide.Sell, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-A", CorrectionRevision: 1, SourceEventId: secondId)),
            new Trade(1, Start.AddSeconds(15), 1, 3, TradeSide.None, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-B", CorrectionRevision: 1, SourceEventId: firstId)),
            new Trade(999, Start.AddSeconds(movesBackward ? 2 : 20), -2, 7, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-A", CorrectionRevision: 2, SourceEventId: firstId)),
            new Trade(222, Start.AddSeconds(25), 0, 5, TradeSide.Sell, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-A", CorrectionRevision: 2, SourceEventId: secondId)),
        };
        var day = DateOnly.FromDateTime(Start);
        string path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        using (var writer = new TickFile.Writer(path, _instrument))
            foreach (var trade in raw) writer.Write(trade);
        byte[] original = File.ReadAllBytes(path);
        var direct = CompactHistory.Compress(raw, _instrument.TickSize, 1);
        var streamed = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        var cached = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        Assert.Equal(direct.Select(Value), streamed.Select(Value));
        Assert.Equal(streamed.Select(Value), cached.Select(Value));
        foreach (var compact in new[] { direct, streamed, cached })
        {
            Assert.Equal(15m, compact.Sum(trade => trade.Volume));
            Assert.Equal(3, compact.Sum(trade => trade.Provenance.ObservedTradeCount));
            Assert.All(compact, trade => Assert.Null(trade.Provenance.SourceEventId));
            var series = Series(compact, ChartPeriod.M1);
            var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.Equal(movesBackward ? -2m : 1m, candle.Open);
            Assert.Equal(1m, candle.High);
            Assert.Equal(-2m, candle.Low);
            Assert.Equal(0m, candle.Close);
            Assert.Equal(15m, candle.Volume);
            Assert.Equal(2m, candle.Delta);
            Assert.Equal(3, candle.AggregatedTrades);
            Assert.Equal(0m, candle.MaxTradeVolume);
            var levels = candle.GetAggregatedPriceLevels().ToDictionary(level => level.Price);
            Assert.Equal(7m, levels[-2].Ask);
            Assert.Equal(5m, levels[0].Bid);
            Assert.Equal(3m, levels[1].Between);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void CorrectionIndexCapacityFailurePreservesBothRawBytesAndPreviouslyPublishedCache()
    {
        var day = DateOnly.FromDateTime(Start);
        string path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        using (var writer = new TickFile.Writer(path, _instrument))
        {
            writer.Write(new Trade(1, Start, 0, 1, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddSeconds(1), 0, 1, TradeSide.Buy));
        }
        CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        string cache = CompactHistory.CachePath(CompactRoot, _instrument.FeedId, _instrument.Symbol, day, 1);
        byte[] previous = File.ReadAllBytes(cache);
        File.Delete(path);
        using (var writer = new TickFile.Writer(path, _instrument))
        {
            writer.Write(new Trade(2, Start, 0, 1, TradeSide.Buy));
            writer.Write(new Trade(1, Start.AddSeconds(1), 0, 2, TradeSide.Buy));
            writer.Write(new Trade(3, Start.AddSeconds(2), 0, 3, TradeSide.Buy));
            writer.Write(new Trade(4, Start.AddSeconds(3), 0, 4, TradeSide.Buy));
        }
        byte[] original = File.ReadAllBytes(path);
        var error = Assert.Throws<InvalidDataException>(() =>
            CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1, maximumCorrectionIdentities: 3));
        Assert.Contains("correction", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previous, File.ReadAllBytes(cache));
        Assert.Equal(original, File.ReadAllBytes(path));
        var complete = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1, maximumCorrectionIdentities: 4);
        Assert.Equal(10m, complete.Sum(trade => trade.Volume));
        Assert.Equal(4, complete.Sum(trade => trade.Provenance.ObservedTradeCount));
    }

    [Fact]
    public void ProvenCanonicalStreamsCanExceedTheCorrectionIndexBudgetWithoutDroppingPrints()
    {
        var day = DateOnly.FromDateTime(Start);
        string path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        using (var writer = new TickFile.Writer(path, _instrument))
            for (int id = 1; id <= 50; id++) writer.Write(new Trade(id, Start.AddMilliseconds(id), 0, 1, TradeSide.Buy));
        var compact = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1, maximumCorrectionIdentities: 1);
        var series = Series(compact, ChartPeriod.M1);
        var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
        Assert.Equal(50m, candle.Volume);
        Assert.Equal(50, candle.AggregatedTrades);
        Assert.Equal(0m, candle.MaxTradeVolume);
        Assert.False(candle.HasObservedFootprint);
    }

    [Theory]
    [InlineData("ctrader")]
    [InlineData("ctrader-legacy")]
    [InlineData("oanda")]
    [InlineData("importado")]
    public void LegacyQuoteFilesCannotBecomeMeasuredCompactFootprints(string feedId)
    {
        var instrument = new Instrument { FeedId = feedId, Symbol = "LEGACY", TickSize = 1m, VolumeStep = 1m };
        var day = DateOnly.FromDateTime(Start);
        string path = TickFile.DayPath(_root, feedId, instrument.Symbol, day);
        using (var writer = new TickFile.Writer(path, instrument, includeProvenance: false))
        {
            writer.Write(new Trade(1, Start, 100, 2, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddSeconds(5), 102, 3, TradeSide.Sell));
            writer.Write(new Trade(3, Start.AddSeconds(10), 101, 5, TradeSide.Buy));
        }
        byte[] original = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => CompactHistory.LoadDay(_root, CompactRoot, instrument, day, 1));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(CompactHistory.CachePath(CompactRoot, feedId, instrument.Symbol, day, 1)));
        var quotes = HistorySources.ReadDay(_root, feedId, instrument.Symbol, day);
        Assert.Equal(new long[] { 1, 2, 3 }, quotes.Select(trade => trade.Id));
        Assert.All(quotes, trade => {
            Assert.False(trade.IsObservedTrade);
            Assert.False(trade.Provenance.IsMeasured);
            Assert.True(trade.Provenance.IsSideInferred);
            Assert.True(trade.Provenance.IsPartial);
        });
        var store = new TradeStore(instrument);
        foreach (var quote in quotes) store.Add(quote);
        var series = new CandleSeries(instrument, ChartPeriod.M1);
        series.Rebuild(store);
        var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
        Assert.Equal(10m, candle.Volume);
        Assert.False(candle.HasObservedFootprint);
        Assert.False(candle.HasAggregatedFootprint);
        Assert.Equal(0m, candle.MaxTradeVolume);
    }

    [Theory]
    [InlineData("binance-futures", true)]
    [InlineData("binance-spot", true)]
    [InlineData("agregado", false)]
    public void LegacyAggregatePacketsKeepAllPriceSideVolumeWithoutInventingConstituentCounts(string feedId, bool hasNativeIdentity)
    {
        var instrument = new Instrument { FeedId = feedId, Symbol = "LEGACY", TickSize = 1m, VolumeStep = 1m };
        var day = DateOnly.FromDateTime(Start);
        string path = TickFile.DayPath(_root, feedId, instrument.Symbol, day);
        using (var writer = new TickFile.Writer(path, instrument, includeProvenance: false))
        {
            writer.Write(new Trade(1, Start, 100, 2, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddSeconds(5), 102, 3, TradeSide.Sell));
            writer.Write(new Trade(3, Start.AddSeconds(10), 101, 5, TradeSide.Buy));
            writer.Write(new Trade(4, Start.AddSeconds(15), 101, 7, TradeSide.Buy));
            writer.Write(new Trade(5, Start.AddSeconds(20), 100, 11, TradeSide.Sell));
        }
        byte[] original = File.ReadAllBytes(path);
        var source = HistorySources.ReadDay(_root, feedId, instrument.Symbol, day);
        Assert.Equal(5, source.Count);
        Assert.All(source, trade => {
            Assert.False(trade.IsObservedTrade);
            Assert.True(trade.Provenance.IsMeasured);
            Assert.True(trade.Provenance.IsPartial);
            Assert.Equal(0, trade.Provenance.ObservedTradeCount);
            Assert.Equal(hasNativeIdentity ? trade.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, trade.Provenance.SourceEventId);
        });
        var direct = CompactHistory.Compress(source, instrument.TickSize, 1);
        var streamed = CompactHistory.LoadDay(_root, CompactRoot, instrument, day, 1);
        var cached = CompactHistory.LoadDay(_root, CompactRoot, instrument, day, 1);
        Assert.Equal(streamed.Select(Value), cached.Select(Value));
        foreach (var compact in new[] { direct, streamed, cached })
        {
            Assert.Equal(28m, compact.Sum(trade => trade.Volume));
            Assert.All(compact, trade => {
                Assert.Equal(feedId, trade.Provenance.SourceIdentity);
                Assert.Equal(0, trade.Provenance.ObservedTradeCount);
                Assert.True(trade.Provenance.IsPartial);
                Assert.Null(trade.Provenance.SourceEventId);
                Assert.False(trade.IsObservedTrade);
            });
            var store = new TradeStore(instrument);
            foreach (var trade in compact) store.Add(trade);
            var series = new CandleSeries(instrument, ChartPeriod.M1);
            series.Rebuild(store);
            var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.Equal(28m, candle.Volume);
            Assert.Equal(14m, candle.Ask);
            Assert.Equal(14m, candle.Bid);
            Assert.Equal(0, candle.AggregatedTrades);
            Assert.Equal(0, candle.ObservedTrades);
            Assert.Equal(0m, candle.MaxTradeVolume);
            Assert.True(candle.HasAggregatedFootprint);
            Assert.False(candle.HasObservedFootprint);
            var levels = candle.GetAggregatedPriceLevels().ToDictionary(level => level.Price);
            Assert.Equal(2m, levels[100].Ask);
            Assert.Equal(11m, levels[100].Bid);
            Assert.Equal(12m, levels[101].Ask);
            Assert.Equal(3m, levels[102].Bid);
            Assert.All(levels.Values, level => Assert.False(level.HasTradeCount));
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExplicitNewSourceMetadataIsNotDowngradedByLegacyFeedClassification()
    {
        var instrument = new Instrument { FeedId = "binance-futures", Symbol = "EXPLICIT", TickSize = 1m, VolumeStep = 1m };
        var day = DateOnly.FromDateTime(Start);
        var trade = new Trade(7, Start, 100, 5, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "explicit-print-provider", CorrectionRevision: 19, SourceEventId: "native-print-7"));
        string path = TickFile.DayPath(_root, instrument.FeedId, instrument.Symbol, day);
        using (var writer = new TickFile.Writer(path, instrument)) writer.Write(trade);
        byte[] original = File.ReadAllBytes(path);
        var compact = CompactHistory.LoadDay(_root, CompactRoot, instrument, day, 1);
        var row = Assert.Single(compact);
        Assert.Equal("explicit-print-provider", row.Provenance.SourceIdentity);
        Assert.Equal(19, row.Provenance.CorrectionRevision);
        Assert.Equal(1, row.Provenance.ObservedTradeCount);
        Assert.False(row.Provenance.IsPartial);
        Assert.Null(row.Provenance.SourceEventId);
        Assert.Equal(5m, row.Volume);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void UnknownAndKnownMeasuredCountsRemainDistinctWhenPriceSideAndOtherMetadataMatch()
    {
        var known = new TradeProvenance(DataQuality.Compact | DataQuality.Partial, SourceIdentity: "count-source", CorrectionRevision: 7);
        var raw = new[] {
            new Trade(-1, Start, 2, 1, TradeSide.Buy, known),
            new Trade(-2, Start.AddSeconds(1), -1, 2, TradeSide.Buy, known),
            new Trade(-3, Start.AddSeconds(2), 0, 3, TradeSide.Buy, known with { ObservedTradeCount = 0 }),
            new Trade(-4, Start.AddSeconds(3), 0, 5, TradeSide.Buy, known with { ObservedTradeCount = 7 }),
            new Trade(-5, Start.AddSeconds(4), 2, 7, TradeSide.Buy, known),
        };
        var day = DateOnly.FromDateTime(Start);
        string path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        using (var writer = new TickFile.Writer(path, _instrument))
            foreach (var trade in raw) writer.Write(trade);
        var direct = CompactHistory.Compress(raw, _instrument.TickSize, 1);
        var streamed = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        var cached = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        foreach (var compact in new[] { direct, streamed, cached })
        {
            Assert.Equal(18m, compact.Sum(trade => trade.Volume));
            Assert.Equal(3m, compact.Where(trade => trade.Price == 0 && trade.Provenance.ObservedTradeCount == 0).Sum(trade => trade.Volume));
            Assert.Equal(5m, compact.Where(trade => trade.Price == 0 && trade.Provenance.ObservedTradeCount == 7).Sum(trade => trade.Volume));
            var series = Series(compact, ChartPeriod.M1);
            var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.Equal(18m, candle.Volume);
            Assert.Equal(10, candle.AggregatedTrades);
            Assert.Equal(0m, candle.MaxTradeVolume);
        }
    }

    [Fact]
    public void CompactionNeverShrinksCrossingSourceWindowsOrTreatsLegacyNegativeIdsAsRaw()
    {
        var coarser = new Trade(-22, Start, 0, 8, TradeSide.Buy, new TradeProvenance(DataQuality.Compact, 300, "measured-source", 2, 4, false));
        Assert.Throws<InvalidDataException>(() => CompactHistory.Compress(new[] { coarser }, 1, 1));
        var retained = Assert.Single(CompactHistory.Compress(new[] { coarser }, 1, 5));
        Assert.Equal(300, retained.Provenance.ResolutionSeconds);
        Assert.Equal(4, retained.Provenance.ObservedTradeCount);
        Assert.False(retained.HasKnownAvailability);
        Assert.False(retained.IsObservedTrade);
        Assert.Equal(Start.AddMinutes(5), retained.AvailableAt);
        var crossing = new Trade(1, Start.AddSeconds(50), 0, 1, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, 30, "phase", ResolutionOffsetSeconds: 15));
        Assert.Throws<InvalidDataException>(() => CompactHistory.Compress(new[] { crossing }, 1, 1));
        var legacy = new Trade(-77, Start, 100, 2, TradeSide.Buy);
        Assert.Throws<InvalidDataException>(() => CompactHistory.Compress(new[] { legacy }, 1, 1));
        string rawPath = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start));
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
        using (var stream = File.Create(rawPath))
        {
            // Historical VTK1 fixture: keyframe/buy, unsigned ID and milliseconds, zigzag price, integral volume.
            stream.Write("VTK1"u8);
            stream.WriteByte(1);
            stream.WriteByte((byte)'1');
            stream.WriteByte(5);
            WriteLegacyUnsigned(stream, unchecked((ulong)-77L));
            WriteLegacyUnsigned(stream, checked((ulong)Ms(Start)));
            WriteLegacyUnsigned(stream, 200);
            WriteLegacyUnsigned(stream, 2);
        }
        Assert.Throws<InvalidDataException>(() => CompactHistory.LoadDay(_root, CompactRoot, _instrument, DateOnly.FromDateTime(Start), 1));
        var decoded = Assert.Single(TickFile.Read(rawPath));
        Assert.Equal(-77, decoded.Id);
        Assert.Equal(100m, decoded.Price);
        Assert.Equal(2m, decoded.Volume);
        Assert.False(decoded.IsObservedTrade);
        Assert.False(decoded.HasKnownAvailability);
        Assert.True(decoded.Provenance.IsSideInferred);

        static void WriteLegacyUnsigned(Stream stream, ulong value)
        {
            do
            {
                byte part = (byte)(value & 0x7F);
                value >>= 7;
                stream.WriteByte(value == 0 ? part : (byte)(part | 0x80));
            } while (value != 0);
        }
    }

    [Fact]
    public void DefaultProviderBarsHavePhasedKnownAvailabilityAndExactTinySideVolumes()
    {
        var open = Start.AddSeconds(30);
        const decimal volume = 0.0000000000000000000000000003m;
        const decimal buy = 0.0000000000000000000000000001m;
        var bar = new Kline(Ms(open), 0, 1, -2, -1, volume, buy, true);
        var trades = new List<Trade>();
        KlineHistory.Synthesize(new[] { bar }, _instrument, 1, new[] { (open, open.AddMinutes(1)) }, trades);
        Assert.Equal(4, trades.Count);
        Assert.Equal(volume, trades.Sum(trade => trade.Volume));
        Assert.Equal(buy, trades.Where(trade => trade.Side == TradeSide.Buy).Sum(trade => trade.Volume));
        Assert.Equal(0.0000000000000000000000000002m, trades.Where(trade => trade.Side == TradeSide.Sell).Sum(trade => trade.Volume));
        Assert.Equal(new decimal[] { 0, 1, -2, -1 }, trades.Select(trade => trade.Price));
        Assert.Equal(Enumerable.Range(0, 4).Select(i => KlineHistory.SyntheticIdBase + bar.OpenTimeMs * 4 + i), trades.Select(trade => trade.Id));
        Assert.All(trades, trade => {
            Assert.True(trade.Id < 0);
            Assert.False(trade.IsObservedTrade);
            Assert.True(trade.HasKnownAvailability);
            Assert.True(trade.Provenance.IsSideInferred);
            Assert.Equal(open.AddMinutes(1), trade.AvailableAt);
            Assert.Equal((open, open.AddMinutes(1)), trade.Provenance.SourceWindow(trade.Time));
        });
    }

    [Fact]
    public void ExplicitUnknownCoarseBarsRequireTheirWholeSourceWindowAndNeverInventDirectionalVolume()
    {
        var open = Start.AddSeconds(30);
        var provenance = new TradeProvenance(DataQuality.Synthetic | DataQuality.Partial, 300, "calendar-provider", 19, 21, false, 30);
        var bar = new Kline(Ms(open), 0, 2, -2, 1, 7, 0, false, provenance);
        var trades = new List<Trade>();
        KlineHistory.Synthesize(new[] { bar }, _instrument, 1, new[] { (open, open.AddMinutes(1)) }, trades);
        Assert.Empty(trades);
        KlineHistory.Synthesize(new[] { bar }, _instrument, 1, new[] { (open.AddSeconds(1), open.AddMinutes(5)) }, trades);
        Assert.Empty(trades);
        KlineHistory.Synthesize(new[] { bar }, _instrument, 1, new[] { (open, open.AddMinutes(5)) }, trades);
        Assert.Equal(7m, trades.Sum(trade => trade.Volume));
        Assert.All(trades, trade => {
            Assert.False(trade.HasKnownAvailability);
            Assert.Equal(open.AddMinutes(5), trade.AvailableAt);
            Assert.Equal(300, trade.Provenance.ResolutionSeconds);
            Assert.Equal(30, trade.Provenance.ResolutionOffsetSeconds);
            Assert.Equal(19, trade.Provenance.CorrectionRevision);
            Assert.Equal("calendar-provider", trade.Provenance.SourceIdentity);
            Assert.Equal(TradeSide.None, trade.Side);
            Assert.True(trade.Provenance.IsSideInferred);
            Assert.True(trade.Provenance.IsPartial);
            Assert.Equal(0, trade.Provenance.ObservedTradeCount);
        });
        var series = Series(trades, ChartPeriod.M15);
        var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
        Assert.Equal(7m, candle.Volume);
        Assert.Equal(7m, candle.Between);
        Assert.Equal(0m, candle.Ask);
        Assert.Equal(0m, candle.Bid);
        Assert.False(candle.HasKnownAvailability);
        Assert.False(candle.HasObservedFootprint);
        Assert.False(candle.HasAggregatedFootprint);
    }

    [Fact]
    public void CalendarSourceDurationsRemainCalendarDurationsInsteadOfRequestedMinuteBars()
    {
        var january = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var february = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var march = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = new[] {
            new Kline(Ms(january), 0, 2, -2, 1, 11, 7, true, CalendarSource(january, february)),
            new Kline(Ms(february), 1, 3, -1, 2, 13, 5, true, CalendarSource(february, march)),
        };
        var trades = new List<Trade>();
        KlineHistory.Synthesize(bars, _instrument, 60, new[] { (january, march) }, trades);
        Assert.Equal(8, trades.Count);
        Assert.All(trades.Take(4), trade => {
            Assert.Equal(2_678_400, trade.Provenance.ResolutionSeconds);
            Assert.Equal((january, february), trade.Provenance.SourceWindow(trade.Time));
            Assert.Equal(february, trade.AvailableAt);
        });
        Assert.All(trades.Skip(4), trade => {
            Assert.Equal(2_419_200, trade.Provenance.ResolutionSeconds);
            Assert.Equal((february, march), trade.Provenance.SourceWindow(trade.Time));
            Assert.Equal(march, trade.AvailableAt);
        });
        Assert.Equal(24m, trades.Sum(trade => trade.Volume));
        Assert.Equal(12m, trades.Where(trade => trade.Side == TradeSide.Buy).Sum(trade => trade.Volume));
        var truncated = new List<Trade>();
        KlineHistory.Synthesize(bars, _instrument, 60, new[] { (january, january.AddHours(1)) }, truncated);
        Assert.Empty(truncated);
    }

    [Fact]
    public void SessionPhaseDefaultsAndSubsecondOpensCannotClaimEarlyKnowledge()
    {
        var sessionOpen = new DateTime(2026, 9, 7, 22, 0, 0, DateTimeKind.Utc);
        var weekly = new Kline(Ms(sessionOpen), 0, 2, -1, 1, 4, 3, true,
            new TradeProvenance(DataQuality.Synthetic, 604_800, "session-provider", 2, 0));
        var trades = new List<Trade>();
        KlineHistory.Synthesize(new[] { weekly }, _instrument, 60, new[] { (sessionOpen, sessionOpen.AddDays(7)) }, trades);
        Assert.Equal(4, trades.Count);
        Assert.All(trades, trade => {
            Assert.Equal((sessionOpen, sessionOpen.AddDays(7)), trade.Provenance.SourceWindow(trade.Time));
            Assert.Equal(sessionOpen.AddDays(7), trade.AvailableAt);
            Assert.True(trade.HasKnownAvailability);
        });
        var inconsistent = weekly with { Provenance = weekly.Provenance with { ResolutionOffsetSeconds = 1 } };
        Assert.Throws<InvalidDataException>(() => KlineHistory.Synthesize(new[] { inconsistent }, _instrument, 60,
            new[] { (sessionOpen, sessionOpen.AddDays(7)) }, new List<Trade>()));

        var fractionalOpen = Start.AddMilliseconds(500);
        var fractional = new Kline(Ms(fractionalOpen), 0, 2, -1, 1, 7, 0, false);
        var truncated = new List<Trade>();
        KlineHistory.Synthesize(new[] { fractional }, _instrument, 1, new[] { (Start, Start.AddMinutes(1)) }, truncated);
        Assert.Empty(truncated);
        var preserved = new List<Trade>();
        KlineHistory.Synthesize(new[] { fractional }, _instrument, 1, new[] { (Start, fractionalOpen.AddMinutes(1)) }, preserved);
        Assert.Equal(new decimal[] { 0, -1, 2, 1 }, preserved.Select(trade => trade.Price));
        Assert.Equal(7m, preserved.Sum(trade => trade.Volume));
        Assert.All(preserved, trade => {
            Assert.False(trade.HasKnownAvailability);
            Assert.True(trade.Provenance.IsPartial);
            Assert.False(trade.IsObservedTrade);
        });
    }

    [Fact]
    public void CoverageUsesTheFullMeasuredSourcePhaseAndDoesNotClaimLegacySyntheticEvents()
    {
        var phaseOpen = Start.AddSeconds(30);
        var real = new[] {
            new Trade(1, Start.AddMinutes(-5), 0, 1, TradeSide.Buy),
            new Trade(-9, phaseOpen, 1, 8, TradeSide.Sell, new TradeProvenance(DataQuality.Compact, 300, "source", 2, 4, true, 30)),
            new Trade(-10, Start.AddMinutes(20), 2, 99, TradeSide.Buy),
        };
        Assert.Equal(new[] { (Start.AddMinutes(-5), Start.AddMinutes(-4)), (phaseOpen, phaseOpen.AddMinutes(5)) }, KlineHistory.Coverage(real));
        var fallback = KlineHistory.Subtract(new[] { (Start, Start.AddMinutes(10)) }, KlineHistory.Coverage(real));
        Assert.Equal(new[] { (Start, phaseOpen), (phaseOpen.AddMinutes(5), Start.AddMinutes(10)) }, fallback);
        var delayedSummary = new[] {
            new Trade(2, Start.AddMinutes(1), 0, 1, TradeSide.Buy),
            new Trade(-11, Start.AddMinutes(2), 1, 8, TradeSide.Buy, new TradeProvenance(DataQuality.Compact, 300, "source", 2, 4, true, 30)),
        };
        Assert.Equal((phaseOpen, phaseOpen.AddMinutes(5)), Assert.Single(KlineHistory.Coverage(delayedSummary)));
    }

    [Fact]
    public async Task ThreeCurrentMonthRequestsFillTheContiguousGapBeforeClaimingMiddleCoverage()
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = CurrentMonth();
            var middleFrom = month.AddDays(1);
            var middleTo = month.AddDays(2);
            var rawDay = DateOnly.FromDateTime(middleFrom);
            string rawPath = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, rawDay);
            using (var writer = new TickFile.Writer(rawPath, _instrument))
                writer.Write(new Trade(234, middleFrom.AddHours(12), -1, 17, TradeSide.Buy));
            byte[] original = File.ReadAllBytes(rawPath);
            var source = new CandleSource((from, to, minutes) => MinuteBars(from, to, minutes, time => {
                int day = (int)(time - month).TotalDays + 1;
                return new Kline(Ms(time), day * 10, day * 10 + 1, day * 10 - 1, day * 10, day, 0, false);
            }));
            var prefix = await KlineHistory.LoadAsync(source, _instrument, month, middleFrom, 1, 0, null, default);
            Assert.Equal(1_440, prefix.Count);
            var disjoint = await KlineHistory.LoadAsync(source, _instrument, middleTo, month.AddDays(3), 1, 0, null, default);
            Assert.Equal(1_440, disjoint.Count);
            Assert.Equal((middleFrom.AddMinutes(-2), month.AddDays(3)), source.Requests[1]);
            var middle = await KlineHistory.LoadAsync(source, _instrument, middleFrom, middleTo, 1, 0, null, default);
            Assert.Equal(1_440, middle.Count);
            Assert.Equal(Ms(middleFrom), middle[0].OpenTimeMs);
            Assert.Equal(Ms(middleTo.AddMinutes(-1)), middle[^1].OpenTimeMs);
            Assert.Equal(1_440, middle.Select(bar => bar.OpenTimeMs).Distinct().Count());
            Assert.All(middle, bar => { Assert.Equal(20m, bar.Close); Assert.Equal(2m, bar.Volume); });
            var trades = new List<Trade>();
            KlineHistory.Synthesize(middle, _instrument, 1, new[] { (middleFrom, middleTo) }, trades);
            var series = Series(trades, ChartPeriod.D1);
            var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.Equal(2_880m, candle.Volume);
            Assert.Equal(20m, candle.Close);
            Assert.False(candle.HasObservedFootprint);
            Assert.Equal(original, File.ReadAllBytes(rawPath));
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public async Task RepeatedCorrectedFormingBarsReplaceVolumeInsteadOfAccumulatingIt()
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = CurrentMonth();
            bool corrected = false;
            var source = new CandleSource((from, to, minutes) => {
                var batch = MinuteBars(from, to, minutes, time => {
                    bool changed = corrected && time == month.AddMinutes(3);
                    return new Kline(Ms(time), 0, 2, -1, changed ? 2 : 1, changed ? 7 : 1, changed ? 5 : 1, true,
                        new TradeProvenance(DataQuality.Synthetic, 60, "forming-provider", corrected ? 2 : 1, 17));
                });
                if (batch.Count > 0) batch.Add(batch[^1]);
                return batch;
            });
            var first = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(4), 1, 0, null, default);
            Assert.Equal(4m, first.Sum(bar => bar.Volume));
            corrected = true;
            var second = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(5), 1, 0, null, default);
            var third = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(5), 1, 0, null, default);
            Assert.Equal(5, second.Count);
            Assert.Equal(second, third);
            Assert.Equal(11m, third.Sum(bar => bar.Volume));
            Assert.Equal(2m, third.Single(bar => bar.OpenTimeMs == Ms(month.AddMinutes(3))).Close);
            Assert.Equal(2, third.Single(bar => bar.OpenTimeMs == Ms(month.AddMinutes(3))).Provenance.CorrectionRevision);
            var trades = new List<Trade>();
            KlineHistory.Synthesize(third, _instrument, 1, new[] { (month, month.AddMinutes(5)) }, trades);
            var series = Series(trades, ChartPeriod.M5);
            var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.Equal(11m, candle.Volume);
            Assert.Equal(9m, candle.Ask);
            Assert.Equal(2m, candle.Bid);
            Assert.Equal(0m, candle.MaxTradeVolume);
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public async Task SimulatorHistoryCanCrossAMonthlyBatchWithConfiguredCacheWithoutLosingConsumerCoverage()
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var instrument = new Instrument { FeedId = SimulatedFeed.FeedId, Symbol = "SIM-BOUNDARY", TickSize = 1m, VolumeStep = 1m };
            var from = Start.Date;
            var to = from.AddDays(40);
            var source = new CandleSource((start, finish, minutes) => MinuteBars(start, finish, minutes,
                time => new Kline(Ms(time), 0, 2, -1, 1, 3, 2, true)));
            var bars = await KlineHistory.LoadAsync(source, instrument, from, to, 1, 0, null, default);
            Assert.Equal(57_600, bars.Count);
            Assert.Equal((from, to), Assert.Single(source.Requests));
            Assert.Equal(Ms(from), bars[0].OpenTimeMs);
            Assert.Equal(Ms(to.AddMinutes(-1)), bars[^1].OpenTimeMs);
            var trades = new List<Trade>(bars.Count * 4);
            KlineHistory.Synthesize(bars, instrument, 1, new[] { (from, to) }, trades);
            var store = new TradeStore(instrument);
            foreach (var trade in trades) store.Add(trade);
            var series = new CandleSeries(instrument, ChartPeriod.D1);
            series.Rebuild(store);
            Assert.Equal(40, series.Count);
            Assert.Equal(172_800m, series.Candles.Sum(candle => candle.Volume));
            Assert.All(series.Candles, candle => {
                var sdk = new IndicatorCandle(candle, series);
                Assert.Equal(4_320m, sdk.Volume);
                Assert.Equal(2_880m, sdk.Ask);
                Assert.Equal(1_440m, sdk.Bid);
                Assert.Equal(0m, sdk.MaxTradeVolume);
                Assert.False(sdk.HasObservedFootprint);
                Assert.False(sdk.HasAggregatedFootprint);
            });
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedRefreshRetainsThePublishedCacheAndReportsPartialSourceCoverage(bool invalidMetadata)
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = CurrentMonth();
            bool malformed = false;
            var source = new CandleSource((from, to, minutes) => MinuteBars(from, to, minutes, time => {
                var bar = new Kline(Ms(time), 0, 2, -1, 1, malformed ? 9 : 1, 0, false);
                if (!malformed || time != month.AddMinutes(4)) return bar;
                return invalidMetadata
                    ? bar with { Provenance = new TradeProvenance(DataQuality.Synthetic, -1, "invalid-provider-metadata") }
                    : bar with { High = -2 };
            }));
            await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(5), 1, 0, null, default);
            string cache = Assert.Single(Directory.EnumerateFiles(KlineHistory.CacheRoot, "*.bin", SearchOption.AllDirectories));
            byte[] before = File.ReadAllBytes(cache);
            malformed = true;
            var warnings = new List<string>();
            var retained = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(6), 1, 0, null, default, warnings.Add);
            Assert.Equal(5, retained.Count);
            Assert.All(retained, bar => { Assert.Equal(1m, bar.Volume); Assert.True(bar.Provenance.IsPartial); });
            Assert.Equal(before, File.ReadAllBytes(cache));
            string warning = Assert.Single(warnings);
            Assert.Contains(invalidMetadata ? "source metadata" : "OHLCV", warning, StringComparison.OrdinalIgnoreCase);
            var trades = new List<Trade>();
            KlineHistory.Synthesize(retained, _instrument, 1, new[] { (month, month.AddMinutes(6)) }, trades);
            var series = Series(trades, ChartPeriod.M5);
            var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.Equal(5m, candle.Volume);
            Assert.True((candle.Quality & DataQuality.Partial) != 0);
            Assert.False(candle.HasObservedFootprint);
            Assert.Equal(0m, candle.MaxTradeVolume);
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public async Task UnwritableColdCacheStillReturnsValidSourceHistoryWithTheActualStorageWarning()
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "blocked-candles");
        try
        {
            string rawPath = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start));
            using (var writer = new TickFile.Writer(rawPath, _instrument)) writer.Write(new Trade(17, Start, 0, 11, TradeSide.Buy));
            byte[] raw = File.ReadAllBytes(rawPath);
            byte[] obstruction = "owned fixture blocks cache directory creation"u8.ToArray();
            File.WriteAllBytes(KlineHistory.CacheRoot, obstruction);
            var month = CurrentMonth();
            var source = new CandleSource((from, to, minutes) => MinuteBars(from, to, minutes, time => new Kline(Ms(time), 0, 2, -1, 1, 3, 2, true)));
            var warnings = new List<string>();
            var loaded = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(5), 1, 0, null, default, warnings.Add);
            Assert.Equal(5, loaded.Count);
            Assert.All(loaded, bar => Assert.False(bar.Provenance.IsPartial));
            string warning = Assert.Single(warnings);
            Assert.Contains(KlineHistory.CacheRoot, warning, StringComparison.Ordinal);
            Assert.Contains("not cached", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(obstruction, File.ReadAllBytes(KlineHistory.CacheRoot));
            Assert.Equal(raw, File.ReadAllBytes(rawPath));
            var trades = new List<Trade>();
            KlineHistory.Synthesize(loaded, _instrument, 1, new[] { (month, month.AddMinutes(5)) }, trades);
            var series = Series(trades, ChartPeriod.M5);
            var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.Equal(15m, candle.Volume);
            Assert.Equal(10m, candle.Ask);
            Assert.Equal(5m, candle.Bid);
            Assert.Equal(0m, candle.MaxTradeVolume);
            Assert.False(candle.HasObservedFootprint);
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public async Task FailedCachePublicationKeepsFreshCorrectionsUsableWithoutReplacingThePriorCache()
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = CurrentMonth();
            bool corrected = false;
            var source = new CandleSource((from, to, minutes) => MinuteBars(from, to, minutes, time => {
                bool changed = corrected && time == month.AddMinutes(3);
                return new Kline(Ms(time), 0, 2, -1, changed ? 2 : 1, changed ? 7 : 1, changed ? 5 : 1, true);
            }));
            await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(4), 1, 0, null, default);
            string path = Assert.Single(Directory.EnumerateFiles(KlineHistory.CacheRoot, "*.bin", SearchOption.AllDirectories));
            byte[] before = File.ReadAllBytes(path);
            string directory = Path.GetDirectoryName(path)!;
            string preservedDirectory = directory + ".previous";
            source.AfterBatch = () => {
                Directory.Move(directory, preservedDirectory);
                File.WriteAllText(directory, "owned fixture blocks publication after the old cache was read");
            };
            corrected = true;
            var warnings = new List<string>();
            List<Kline> loaded;
            try
            {
                loaded = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(5), 1, 0, null, default, warnings.Add);
            }
            finally
            {
                source.AfterBatch = null;
                if (File.Exists(directory)) File.Delete(directory);
                if (Directory.Exists(preservedDirectory)) Directory.Move(preservedDirectory, directory);
            }
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(11m, loaded.Sum(bar => bar.Volume));
            Assert.Equal(2m, loaded.Single(bar => bar.OpenTimeMs == Ms(month.AddMinutes(3))).Close);
            Assert.All(loaded, bar => Assert.False(bar.Provenance.IsPartial));
            string warning = Assert.Single(warnings);
            Assert.Contains(path, warning, StringComparison.Ordinal);
            Assert.Contains("not cached", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(month.AddMinutes(4), KlineHistory.CachedUntil(source, _instrument, 1));
            var trades = new List<Trade>();
            KlineHistory.Synthesize(loaded, _instrument, 1, new[] { (month, month.AddMinutes(5)) }, trades);
            var series = Series(trades, ChartPeriod.M5);
            var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
            Assert.Equal(11m, candle.Volume);
            Assert.Equal(9m, candle.Ask);
            Assert.Equal(2m, candle.Bid);
            source.Unavailable = true;
            var previous = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(5), 1, 0, null, default);
            Assert.Equal(4m, previous.Sum(bar => bar.Volume));
            Assert.All(previous, bar => Assert.True(bar.Provenance.IsPartial));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public async Task ClosedMonthCachePreservesUnknownPhaseCountsAndSharedSourceIdentityWithoutProviderReuse()
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var open = CurrentMonth().AddMonths(-2).AddDays(7).AddHours(12).AddSeconds(30);
            var bars = new[] {
                new Kline(Ms(open), 0, 2, -1, 1, 11, 7, true, new TradeProvenance(DataQuality.Synthetic, 300, new string("shared-provider".ToCharArray()), 4, 17, true, 30)),
                new Kline(Ms(open.AddMinutes(5)), 1, 3, -2, 2, 13, 0, false, new TradeProvenance(DataQuality.Synthetic, 300, new string("shared-provider".ToCharArray()), 4, 29, false, 30)),
            };
            var source = new CandleSource((_, _, _) => bars);
            var first = await KlineHistory.LoadAsync(source, _instrument, open, open.AddMinutes(10), 1, 0, null, default);
            source.Unavailable = true;
            var cached = await KlineHistory.LoadAsync(source, _instrument, open, open.AddMinutes(10), 1, 0, null, default);
            Assert.Equal(bars, cached);
            Assert.Equal(first, cached);
            Assert.Single(source.Requests);
            Assert.Same(cached[0].Provenance.SourceIdentity, cached[1].Provenance.SourceIdentity);
            Assert.Equal(17, cached[0].Provenance.ObservedTradeCount);
            Assert.Equal(29, cached[1].Provenance.ObservedTradeCount);
            Assert.False(cached[1].Provenance.HasKnownAvailability);
            Assert.Equal(open.AddMinutes(10), KlineHistory.CachedUntil(source, _instrument, 1));
            var trades = new List<Trade>();
            KlineHistory.Synthesize(cached, _instrument, 1, new[] { (open, open.AddMinutes(10)) }, trades);
            Assert.Equal(24m, trades.Sum(trade => trade.Volume));
            Assert.Equal(7m, trades.Where(trade => trade.Side == TradeSide.Buy).Sum(trade => trade.Volume));
            Assert.Equal(4m, trades.Where(trade => trade.Side == TradeSide.Sell).Sum(trade => trade.Volume));
            Assert.Equal(13m, trades.Where(trade => trade.Side == TradeSide.None).Sum(trade => trade.Volume));
            Assert.All(trades.Skip(4), trade => {
                Assert.False(trade.HasKnownAvailability);
                Assert.Equal(open.AddMinutes(10), trade.AvailableAt);
            });
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Theory]
    [InlineData("resolution")]
    [InlineData("phase")]
    [InlineData("count")]
    [InlineData("known")]
    [InlineData("quality")]
    public void AuthenticatedButImpossibleCompactMetadataIsRecomputedWithoutChangingRawHistory(string field)
    {
        var day = DateOnly.FromDateTime(Start);
        string rawPath = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        using (var writer = new TickFile.Writer(rawPath, _instrument))
        {
            writer.Write(new Trade(1, Start, -1, 3, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddSeconds(10), 0, 5, TradeSide.Sell));
        }
        byte[] original = File.ReadAllBytes(rawPath);
        var expected = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        string cache = CompactHistory.CachePath(CompactRoot, _instrument.FeedId, _instrument.Symbol, day, 1);
        byte[] canonical = File.ReadAllBytes(cache);
        RewriteCompactCache(cache, SHA256.HashData(original), payload => CorruptFirstMetadata(payload, field, compact: true));
        var repaired = CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        Assert.Equal(expected.Select(Value), repaired.Select(Value));
        Assert.Equal(canonical, File.ReadAllBytes(cache));
        Assert.Equal(original, File.ReadAllBytes(rawPath));
        var series = Series(repaired, ChartPeriod.M1);
        var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
        Assert.Equal(8m, candle.Volume);
        Assert.Equal(2, candle.AggregatedTrades);
        Assert.False(candle.HasObservedFootprint);
    }

    [Theory]
    [InlineData("resolution")]
    [InlineData("phase")]
    [InlineData("count")]
    [InlineData("known")]
    [InlineData("quality")]
    public async Task AuthenticatedButImpossibleCandleMetadataCannotMasqueradeAsAValidClosedMonth(string field)
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var open = CurrentMonth().AddMonths(-2).AddDays(7).AddSeconds(30);
            decimal volume = 3;
            var provenance = new TradeProvenance(DataQuality.Synthetic, 300, "verified-provider", 5, 29, false, 30);
            var source = new CandleSource((_, _, _) => new[] { new Kline(Ms(open), 0, 2, -1, 1, volume, 1, true, provenance) });
            await KlineHistory.LoadAsync(source, _instrument, open, open.AddMinutes(5), 1, 0, null, default);
            string path = Assert.Single(Directory.EnumerateFiles(KlineHistory.CacheRoot, "*.bin", SearchOption.AllDirectories));
            RewriteKlineCache(path, payload => CorruptFirstMetadata(payload, field, compact: false));
            volume = 9;
            var repaired = Assert.Single(await KlineHistory.LoadAsync(source, _instrument, open, open.AddMinutes(5), 1, 0, null, default));
            Assert.Equal(9m, repaired.Volume);
            Assert.Equal(provenance, repaired.Provenance);
            Assert.Equal(2, source.Requests.Count);
            source.Unavailable = true;
            Assert.Equal(repaired, Assert.Single(await KlineHistory.LoadAsync(source, _instrument, open, open.AddMinutes(5), 1, 0, null, default)));
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandleChecksumsBindCoverageMetadataAsWellAsOtherwiseValidPayload(bool payloadCorruption)
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var open = CurrentMonth().AddMonths(-2).AddDays(7);
            decimal volume = 3;
            var source = new CandleSource((_, _, _) => new[] { new Kline(Ms(open), 0, 2, -1, 1, volume, 1, true) });
            await KlineHistory.LoadAsync(source, _instrument, open, open.AddMinutes(1), 1, 0, null, default);
            string path = Assert.Single(Directory.EnumerateFiles(KlineHistory.CacheRoot, "*.bin", SearchOption.AllDirectories));
            if (payloadCorruption)
                RewriteKlineCache(path, payload => CorruptFirstMetadata(payload, "partial", compact: false), authenticate: false);
            else
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var reader = new BinaryReader(new MemoryStream(bytes));
                reader.ReadInt32(); reader.ReadDecimal(); reader.ReadInt32(); reader.ReadInt64(); reader.ReadInt64();
                int updatedPosition = (int)reader.BaseStream.Position;
                long updated = reader.ReadInt64();
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(updatedPosition), updated + TimeSpan.TicksPerSecond);
                File.WriteAllBytes(path, bytes);
            }
            volume = 9;
            var repaired = Assert.Single(await KlineHistory.LoadAsync(source, _instrument, open, open.AddMinutes(1), 1, 0, null, default));
            Assert.Equal(9m, repaired.Volume);
            Assert.False(repaired.Provenance.IsPartial);
            Assert.Equal(2, source.Requests.Count);
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public async Task CancellationPreservesPublishedCachesAndOriginalRawBytes()
    {
        var day = DateOnly.FromDateTime(Start);
        string rawPath = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
        using (var writer = new TickFile.Writer(rawPath, _instrument)) writer.Write(new Trade(1, Start, 0, 7, TradeSide.Buy));
        byte[] original = File.ReadAllBytes(rawPath);
        CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1);
        string compact = CompactHistory.CachePath(CompactRoot, _instrument.FeedId, _instrument.Symbol, day, 1);
        byte[] compactBefore = File.ReadAllBytes(compact);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CompactHistory.LoadDay(_root, CompactRoot, _instrument, day, 1, canceled.Token));
        Assert.Equal(compactBefore, File.ReadAllBytes(compact));

        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = CurrentMonth();
            var source = new CandleSource((from, to, minutes) => MinuteBars(from, to, minutes, time => new Kline(Ms(time), 0, 2, -1, 1, 1, 0, false)));
            await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(5), 1, 0, null, default);
            string cache = Assert.Single(Directory.EnumerateFiles(KlineHistory.CacheRoot, "*.bin", SearchOption.AllDirectories));
            byte[] before = File.ReadAllBytes(cache);
            using var duringBatch = new CancellationTokenSource();
            source.AfterBatch = duringBatch.Cancel;
            source.FailAfterBatch = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(6), 1, 0, null, duringBatch.Token));
            Assert.Equal(before, File.ReadAllBytes(cache));
            source.Unavailable = true;
            var retained = await KlineHistory.LoadAsync(source, _instrument, month, month.AddMinutes(5), 1, 0, null, default);
            Assert.Equal(5m, retained.Sum(bar => bar.Volume));
            Assert.All(retained, bar => Assert.True(bar.Provenance.IsPartial));
            Assert.Equal(original, File.ReadAllBytes(rawPath));
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    private static byte[] CorruptFirstMetadata(byte[] payload, string field, bool compact)
    {
        using var stream = new MemoryStream(payload, writable: true);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int sources = reader.ReadInt32();
        for (int i = 0; i < sources; i++) reader.ReadString();
        int metadata = reader.ReadInt32();
        long qualityPosition = stream.Position;
        byte quality = reader.ReadByte();
        long resolutionPosition = stream.Position;
        reader.ReadInt32();
        reader.ReadInt32(); reader.ReadInt64();
        long countPosition = compact ? -1 : stream.Position;
        if (!compact) reader.ReadInt32();
        long knownPosition = stream.Position;
        reader.ReadByte();
        long phasePosition = stream.Position;
        reader.ReadInt32();
        if (compact && field == "count")
        {
            for (int i = 1; i < metadata; i++)
            {
                reader.ReadByte(); reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt64(); reader.ReadByte(); reader.ReadInt32();
            }
            reader.ReadInt32();
            reader.ReadInt64(); reader.ReadInt64(); reader.ReadDecimal(); reader.ReadDecimal(); reader.ReadByte(); reader.ReadInt32();
            countPosition = stream.Position;
        }
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        switch (field)
        {
            case "resolution": stream.Position = resolutionPosition; writer.Write(0); break;
            case "phase": stream.Position = phasePosition; writer.Write(compact ? 1 : 0); break;
            case "count": stream.Position = countPosition; writer.Write(-1); break;
            case "known": stream.Position = knownPosition; writer.Write((byte)2); break;
            case "quality": stream.Position = qualityPosition; writer.Write((byte)(quality | 0x80)); break;
            case "partial": stream.Position = qualityPosition; writer.Write((byte)(quality ^ (byte)DataQuality.Partial)); break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown corruption fixture.");
        }
        return payload;
    }

    private static void RewriteKlineCache(string path, Func<byte[], byte[]> changePayload, bool authenticate = true)
    {
        byte[] header, digest, payload;
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            using var copied = new MemoryStream();
            using (var writer = new BinaryWriter(copied, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(reader.ReadInt32()); writer.Write(reader.ReadDecimal()); writer.Write(reader.ReadInt32());
                writer.Write(reader.ReadInt64()); writer.Write(reader.ReadInt64()); writer.Write(reader.ReadInt64());
            }
            header = copied.ToArray(); digest = reader.ReadBytes(32);
            payload = reader.ReadBytes(checked((int)(reader.BaseStream.Length - reader.BaseStream.Position)));
        }
        payload = changePayload(payload);
        if (authenticate)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header); hash.AppendData(payload);
            digest = hash.GetHashAndReset();
        }
        using var output = File.Create(path);
        output.Write(header); output.Write(digest); output.Write(payload);
    }

    private static DateTime CurrentMonth() => new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<Kline> MinuteBars(DateTime from, DateTime to, int minutes, Func<DateTime, Kline> create)
    {
        var bars = new List<Kline>();
        for (var time = from; time < to; time = time.AddMinutes(minutes)) bars.Add(create(time));
        return bars;
    }

    private sealed class CandleSource(Func<DateTime, DateTime, int, IReadOnlyList<Kline>> create) : IKlineHistory
    {
        public List<(DateTime From, DateTime To)> Requests { get; } = new();
        public bool Unavailable { get; set; }
        public bool FailAfterBatch { get; set; }
        public Action? AfterBatch { get; set; }
        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);
        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add((fromUtc, toUtc));
            if (Unavailable) throw new IOException("Owned fixture candle source is unavailable.");
            onBatch(create(fromUtc, toUtc, minutes));
            AfterBatch?.Invoke();
            if (FailAfterBatch) throw new IOException("Owned fixture source failed after delivering its batch.");
            return Task.CompletedTask;
        }
    }

    private static TradeProvenance CalendarSource(DateTime from, DateTime to)
    {
        int seconds = checked((int)(to - from).TotalSeconds);
        int phase = (int)(from.Ticks / TimeSpan.TicksPerSecond % seconds);
        return new TradeProvenance(DataQuality.Synthetic, seconds, "calendar-provider", 4, 0, true, phase);
    }

    private static long Ms(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();

    private void AssertCompactFootprint(IReadOnlyList<Trade> trades)
    {
        Assert.All(trades, trade => {
            Assert.True(trade.Id < 0);
            Assert.False(trade.IsObservedTrade);
            Assert.True(trade.HasKnownAvailability);
            Assert.Equal(Start.AddMinutes(1), trade.AvailableAt);
            Assert.Equal(DateTimeKind.Utc, trade.Time.Kind);
        });
        var series = Series(trades, ChartPeriod.M1);
        var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
        Assert.Equal(28m, candle.Volume);
        Assert.Equal(8m, candle.Delta);
        Assert.Equal(0m, candle.MaxTradeVolume);
        Assert.False(candle.HasObservedFootprint);
        Assert.True(candle.HasAggregatedFootprint);
        Assert.Equal(5, candle.AggregatedTrades);
        var levels = candle.GetAggregatedPriceLevels().ToDictionary(level => level.Price);
        Assert.Equal(13m, levels[0].Ask);
        Assert.Equal(3m, levels[-2].Bid);
        Assert.Equal(5m, levels[-1].Ask);
        Assert.Equal(7m, levels[-1].Bid);
        Assert.Equal(5m, levels.Values.Sum(level => level.Ticks));
    }

    private CandleSeries Series(IReadOnlyList<Trade> trades, ChartPeriod period)
    {
        var store = new TradeStore(_instrument);
        foreach (var trade in trades) store.Add(trade);
        var series = new CandleSeries(_instrument, period);
        series.Rebuild(store);
        return series;
    }

    private static (long Id, DateTime Time, decimal Price, decimal Volume, TradeSide Side, TradeProvenance Provenance) Value(Trade trade) =>
        (trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side, trade.Provenance);

    private static void RewriteCompactCache(string path, byte[] sourceDigest, Func<byte[], byte[]>? changePayload = null)
    {
        int magic, minutes;
        decimal tick;
        byte[] payload;
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            magic = reader.ReadInt32(); tick = reader.ReadDecimal(); minutes = reader.ReadInt32();
            reader.ReadBytes(32);
            int length = reader.ReadInt32(); reader.ReadBytes(32);
            using var compressed = new BrotliStream(reader.BaseStream, CompressionMode.Decompress);
            payload = new byte[length]; compressed.ReadExactly(payload);
        }
        if (changePayload != null) payload = changePayload(payload);
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(magic); writer.Write(tick); writer.Write(minutes); writer.Write(sourceDigest); writer.Write(payload.Length);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header.GetBuffer().AsSpan(0, (int)header.Length)); hash.AppendData(payload);
        byte[] digest = hash.GetHashAndReset();
        using var stream = File.Create(path);
        stream.Write(header.GetBuffer().AsSpan(0, (int)header.Length)); stream.Write(digest);
        using var output = new BrotliStream(stream, CompressionLevel.Fastest);
        output.Write(payload);
    }
}
