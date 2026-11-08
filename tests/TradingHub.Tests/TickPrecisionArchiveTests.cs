using System.Buffers.Binary;
using System.Security.Cryptography;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class TickPrecisionArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-tick-precision-" + Guid.NewGuid().ToString("N"));
    private readonly Instrument _instrument = new() { FeedId = "precision-fixture", Symbol = "EXACT", TickSize = 0.25m, VolumeStep = 0.00000001m };
    private static readonly DateTime Start = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = DateOnly.FromDateTime(Start);
    private string RawPath => TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, Day);

    [Fact]
    public void OrdinaryDefaultWriterRetainsLegacyCodecAndRejectsLossyLaterAppend()
    {
        var original = new Trade(41, Start, 100.25m, 12.345600m, TradeSide.Buy);
        using (var writer = new TickFile.Writer(RawPath, _instrument))
        {
            writer.Write(original);
            writer.Flush();
            byte[] before = ReadWhileWriterOpen(RawPath);
            Assert.Throws<InvalidOperationException>(() => writer.Write(new Trade(42, Start.AddTicks(1), 100.5m, 1, TradeSide.Sell)));
            writer.Flush();
            Assert.Equal(before, ReadWhileWriterOpen(RawPath));
        }
        using (var reopened = new TickFile.Writer(RawPath, _instrument))
        {
            Assert.Throws<InvalidOperationException>(() => reopened.Write(new Trade(42, Start.AddMilliseconds(1), 100.5m,
                0.1234567890123456789012345678m, TradeSide.Sell)));
            reopened.Write(new Trade(42, Start.AddMilliseconds(1), 100.5m, 2.5m, TradeSide.Sell));
        }
        var loaded = TickFile.Read(RawPath, out int dropped, requireComplete: true);
        Assert.Equal(0, dropped);
        Assert.Equal(new[] { 12.3456m, 2.5m }, loaded.Select(t => t.Volume));
        Assert.Equal(new long[] { 41, 42 }, loaded.Select(t => t.Id));
        Assert.All(loaded, t => Assert.True(t.IsObservedTrade && t.HasKnownAvailability));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyDefaultWriterPublishesReadableLegacyHeader(bool flush)
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument))
        {
            if (flush)
            {
                writer.Flush();
                Assert.Throws<InvalidOperationException>(() => writer.Write(new Trade(1, Start.AddTicks(1), 100, 1, TradeSide.Buy)));
            }
        }
        Assert.Equal(_instrument.TickSize, TickFile.ReadTickSize(RawPath));
        Assert.Empty(TickFile.Read(RawPath, out int dropped, requireComplete: true));
        Assert.Equal(0, dropped);
        Assert.Empty(TickFile.Enumerate(RawPath));
    }

    [Fact]
    public void ExplicitLegacyWriterFailsBeforeWritingAnUnrepresentableQuantity()
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: false))
        {
            Assert.Throws<InvalidOperationException>(() => writer.Write(new Trade(1, Start, 100,
                18446744073709551616m, TradeSide.Buy)));
            writer.Write(new Trade(1, Start, 100, 18446744073709551615m, TradeSide.Buy));
        }
        Assert.Equal(18446744073709551615m, Assert.Single(TickFile.Read(RawPath, out _, requireComplete: true)).Volume);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopenedExtendedFileRetainsItsFormatEvenWhenLegacyIsRequested(bool? requestedFormat)
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
            writer.Write(new Trade(1, Start, 100, 1, TradeSide.Buy));
        var exact = new Trade(2, Start.AddTicks(1), 0, 0.1234567890123456789012345678m, TradeSide.None);
        using (var writer = new TickFile.Writer(RawPath, _instrument, requestedFormat)) writer.Write(exact);
        var loaded = TickFile.Read(RawPath, out int dropped, requireComplete: true);
        Assert.Equal(0, dropped);
        Assert.Equal(2, loaded.Count);
        AssertEvents(new[] { exact }, new[] { loaded[1] });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameTimeRawAndSyntheticIdsAreNotMistakenForBackwardHistory(bool syntheticFirst)
    {
        var raw = new Trade(1000, Start, 100, 2, TradeSide.Buy);
        var synthetic = new Trade(-1, Start, 100, 3, TradeSide.None);
        var stored = syntheticFirst ? new[] { synthetic, raw } : new[] { raw, synthetic };
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
            foreach (var trade in stored) writer.Write(trade);
        AssertEvents(stored, TickFile.Enumerate(RawPath).ToList());
        var sorted = HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, Day);
        Assert.Equal(new long[] { -1, 1000 }, sorted.Select(t => t.Id));
        Assert.False(sorted[0].HasKnownAvailability);
        Assert.True(sorted[1].HasKnownAvailability);
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(200, true)]
    [InlineData(8192, true)]
    public void NativeIdentityAloneSelectsExtendedAndRoundTripsShortAndPooledUtf8(int length, bool multibyte)
    {
        string nativeId = new(multibyte ? 'é' : 'x', length);
        var trade = new Trade(77, Start, 100, 1, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceEventId: nativeId));
        using (var writer = new TickFile.Writer(RawPath, _instrument)) writer.Write(trade);
        AssertEvents(new[] { trade }, TickFile.Read(RawPath, out int dropped, requireComplete: true));
        Assert.Equal(0, dropped);
        AssertEvents(new[] { trade }, TickFile.Enumerate(RawPath).ToList());
        var archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        File.Delete(RawPath);
        AssertEvents(new[] { trade }, TickFile.Read(archive, out _, requireComplete: true));
        AssertEvents(new[] { trade }, TickFile.Enumerate(archive).ToList());
    }

    [Fact]
    public void OversizedNativeIdentityCannotPartiallyAppendOrRetireThePreviousRecord()
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
        {
            writer.Write(new Trade(1, Start, 100, 1, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceEventId: "retained")));
            writer.Flush();
            byte[] before = ReadWhileWriterOpen(RawPath);
            Assert.Throws<InvalidDataException>(() => writer.Write(new Trade(2, Start.AddSeconds(1), 100, 1, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceEventId: new string('é', 8193)))));
            string unpaired = new((char)0xD800, 1);
            Assert.Throws<System.Text.EncoderFallbackException>(() => writer.Write(new Trade(2, Start.AddSeconds(1), 100, 1, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceEventId: unpaired))));
            Assert.Throws<System.Text.EncoderFallbackException>(() => writer.Write(new Trade(2, Start.AddSeconds(1), 100, 1, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: unpaired, SourceEventId: "unpaired-source"))));
            writer.Flush();
            Assert.Equal(before, ReadWhileWriterOpen(RawPath));
            writer.Write(new Trade(2, Start.AddSeconds(1), 100, 3, TradeSide.Sell, new TradeProvenance(DataQuality.Raw, SourceEventId: "accepted")));
        }
        var read = TickFile.Read(RawPath, out _, requireComplete: true);
        Assert.Equal(new[] { "retained", "accepted" }, read.Select(t => t.Provenance.SourceEventId));
        Assert.Equal(new[] { 1m, 3m }, read.Select(t => t.Volume));
    }

    [Fact]
    public void SameLocationAndNumericProjectionDoNotCollapseDistinctNativeSourcesOrCase()
    {
        var trades = new[]
        {
            new Trade(77, Start, 100, 2, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-a", SourceEventId: "event-A")),
            new Trade(77, Start, 100, 3, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-a", SourceEventId: "event-a")),
            new Trade(77, Start, 100, 5, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-b", SourceEventId: "event-A")),
        };
        using (var writer = new TickFile.Writer(RawPath, _instrument))
            foreach (var trade in trades) writer.Write(trade);
        AssertEvents(trades, TickFile.Enumerate(RawPath).ToList());
        AssertEvents(trades, TickFile.Read(RawPath, out _, requireComplete: true));
        AssertEvents(trades, HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, Day));
    }

    [Fact]
    public void ExactEventsSurviveStreamingArchiveAndArchiveOnlyStrategyHistory()
    {
        const decimal large = 1234567890123456789012345.6789m;
        const decimal fraction = 0.1234567890123456789012345678m;
        const string source = "fixture:exact-source";
        var trades = new[]
        {
            new Trade(9_000_000_000, Start.AddTicks(1), 100.25m, large, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: source, CorrectionRevision: 17, SourceEventId: "native:東京/001")),
            new Trade(long.MinValue + 7, Start.AddTicks(2), 0, fraction, TradeSide.None,
                new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide, 60, source, -17, 0, false, 30, "bar:12:00")),
            new Trade(1, Start.AddTicks(3), -37.5m, fraction, TradeSide.Sell,
                new TradeProvenance(DataQuality.Raw | DataQuality.Partial, SourceIdentity: source, CorrectionRevision: 18, SourceEventId: "native:東京/002")),
            new Trade(-3, Start.AddMinutes(5).AddTicks(4567), 1.25m, 3, TradeSide.Buy,
                new TradeProvenance(DataQuality.Synthetic, 300, source, 2, 0, true, 60)),
        };
        using (var writer = new TickFile.Writer(RawPath, _instrument))
            foreach (var trade in trades) writer.Write(trade);
        AssertEvents(trades, TickFile.Read(RawPath, out int dropped, requireComplete: true));
        Assert.Equal(0, dropped);
        AssertEvents(trades, TickFile.Enumerate(RawPath).ToList());
        byte[] originalDigest = SHA256.HashData(File.ReadAllBytes(RawPath));
        var archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        Assert.Equal(originalDigest, SHA256.HashData(File.ReadAllBytes(RawPath)));
        AssertEvents(trades, TickFile.Enumerate(archive).ToList());
        Assert.Equal(new[] { Day }, TickFile.AvailableDays(_root, _instrument.FeedId, _instrument.Symbol));
        File.Delete(RawPath);
        var loaded = TickData.Load(_root, _instrument, Start, Start.AddMinutes(10), null, default);
        var actual = Enumerable.Range(loaded.Start, loaded.Count).Select(loaded.Get).ToList();
        AssertEvents(trades, actual);
        Assert.False(actual[1].HasKnownAvailability);
        Assert.Equal(Start.AddSeconds(30), actual[1].AvailableAt);
        Assert.Equal(Start.AddMinutes(6), actual[3].AvailableAt);
        Assert.True(actual[3].HasKnownAvailability);
    }

    [Fact]
    public void LastIdentityCorrectionWinsBeforeChronologicalOrdering()
    {
        var original = new Trade(1, Start.AddSeconds(2), 100, 2, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, CorrectionRevision: 900));
        var correction = new Trade(1, Start.AddSeconds(3), 101, 7, TradeSide.Sell,
            new TradeProvenance(DataQuality.Raw, CorrectionRevision: -40));
        var sorted = TickFile.SortAndDedupe(new List<Trade>
        {
            original, new(2, Start, 102, 3, TradeSide.Buy),
            new(-5, Start.AddSeconds(1), 103, 4, TradeSide.None), correction,
        });
        Assert.Equal(new long[] { 2, -5, 1 }, sorted.Select(t => t.Id));
        Assert.Equal(7m, sorted[^1].Volume);
        Assert.Equal(-40, sorted[^1].Provenance.CorrectionRevision);
        var strategyHistory = TickData.FromTrades(_instrument, sorted);
        Assert.Equal(Start, strategyHistory.FirstTime);
        Assert.Equal(Start.AddSeconds(3), strategyHistory.LastTime);
        Assert.Equal(7m, strategyHistory.Volume(2));
    }

    [Fact]
    public void CanonicalIdentityKeepsProviderAndCaseDistinctAndReplacesRenumberedNativeCorrection()
    {
        var rows = new List<Trade>
        {
            new(77, Start, 100, 2, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-a", SourceEventId: "event-A")),
            new(77, Start, 100, 3, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-a", SourceEventId: "event-a")),
            new(77, Start, 100, 5, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-b", SourceEventId: "event-A")),
            new(77, Start, 100, 7, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-a")),
            new(77, Start.AddDays(1), 100, 11, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-a")),
            new(999, Start.AddSeconds(1), 100.25m, 13, TradeSide.Sell,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue-a", CorrectionRevision: -7, SourceEventId: "event-A")),
        };
        var normalized = TickFile.SortAndDedupe(rows);
        Assert.Equal(5, normalized.Count);
        var corrected = Assert.Single(normalized, t => t.Provenance.SourceIdentity == "venue-a" && t.Provenance.SourceEventId == "event-A");
        Assert.Equal((999L, Start.AddSeconds(1), 13m, -7L), (corrected.Id, corrected.Time, corrected.Volume, corrected.Provenance.CorrectionRevision));
        Assert.Equal(3m, Assert.Single(normalized, t => t.Provenance.SourceEventId == "event-a").Volume);
        Assert.Equal(5m, Assert.Single(normalized, t => t.Provenance.SourceIdentity == "venue-b").Volume);
        Assert.Equal(new[] { 7m, 11m }, normalized.Where(t => t.Provenance.SourceEventId == null).Select(t => t.Volume));
    }

    [Fact]
    public void CanonicalCompactionKeepsSurvivingRawOrderAtEqualLocations()
    {
        const int count = 32;
        var nativeIds = Enumerable.Range(0, count).Select(i => "tie:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var rows = new List<Trade>(count + 2);
        for (int i = 0; i < count; i++)
            rows.Add(new Trade(77, Start, 100, 1, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue:ties", CorrectionRevision: i == 0 ? 100 : 0, SourceEventId: nativeIds[i])));
        var future = new Trade(88, Start.AddSeconds(1), 101, 3, TradeSide.Sell,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue:ties", SourceEventId: "future"));
        rows.Insert(1, future);
        rows.Add(new Trade(77, Start, 100, 9, TradeSide.Sell,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue:ties", CorrectionRevision: -1, SourceEventId: nativeIds[0])));
        var normalized = TickFile.SortAndDedupe(rows);
        Assert.Same(rows, normalized);
        Assert.Equal(count + 1, rows.Count);
        Assert.Equal(nativeIds.Skip(1).Append(nativeIds[0]), rows.Take(count).Select(t => t.Provenance.SourceEventId));
        Assert.Equal(9m, rows[count - 1].Volume);
        Assert.Equal(-1, rows[count - 1].Provenance.CorrectionRevision);
        AssertEvents(new[] { future }, new[] { rows[^1] });
    }

    [Fact]
    public void WideNativeWindowCompactsInPlaceWithoutASecondTradeValuedBuffer()
    {
        const int count = 100_000;
        var nativeIds = Enumerable.Range(0, count).Select(i => "window:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var rows = new List<Trade>(count + 1);
        for (int i = 0; i < count; i++)
            rows.Add(new Trade(77, Start.AddTicks(i), 100, 1, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue:window", SourceEventId: nativeIds[i])));
        rows.Add(new Trade(77, Start, 100, 7, TradeSide.Sell,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue:window", CorrectionRevision: -4, SourceEventId: nativeIds[0])));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var normalized = TickFile.SortAndDedupe(rows);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 16L * 1024 * 1024, $"Canonical compaction allocated {allocated:N0} bytes for an existing raw window.");
        Assert.Same(rows, normalized);
        Assert.Equal(count, rows.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(77, rows[i].Id);
            Assert.Equal(Start.AddTicks(i), rows[i].Time);
            Assert.Equal(nativeIds[i], rows[i].Provenance.SourceEventId);
        }
        Assert.Equal(7m, rows[0].Volume);
        Assert.Equal(-4, rows[0].Provenance.CorrectionRevision);
        Assert.Equal(100_006m, rows.Sum(t => t.Volume));
    }

    [Fact]
    public void MoreThanUshortManyNativeEventsSurviveArchiveStoreAndStrategyConsumers()
    {
        const int count = 65_537;
        var nativeIds = Enumerable.Range(0, count).Select(i => "event:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        using (var writer = new TickFile.Writer(RawPath, _instrument))
        {
            for (int i = 0; i < count; i++)
                writer.Write(new Trade(77, Start.AddTicks(i), 100, 1, TradeSide.Buy,
                    new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue:native", SourceEventId: nativeIds[i])));
        }
        var archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        File.Delete(RawPath);
        var store = new TradeStore(_instrument);
        foreach (var trade in TickFile.Enumerate(archive)) store.Add(trade);
        Assert.Equal(count, store.Count);
        Assert.Equal(65_537m, Enumerable.Range(0, store.Count).Sum(store.GetVolume));
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(77, store.GetId(i));
            Assert.Equal(nativeIds[i], store.GetProvenance(i).SourceEventId);
        }
        var ticks = TickData.FromStore(store);
        Assert.Equal(count, ticks.Count);
        for (int i = ticks.Start; i < ticks.End; i++)
        {
            Assert.Equal(77, ticks.Id(i));
            Assert.Equal(nativeIds[i], ticks.Provenance(i).SourceEventId);
        }
        Assert.Equal(65_537m, Enumerable.Range(ticks.Start, ticks.Count).Sum(ticks.Volume));
    }

    [Fact]
    public void IncreasingNumericProjectionCannotHideAnExactNativeIdentityCorrection()
    {
        var provenance = new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue:native", SourceEventId: "native-correction");
        var original = new Trade(100, Start, 100, 2, TradeSide.Buy, provenance);
        var correction = new Trade(101, Start.AddSeconds(1), 100.25m, 7, TradeSide.Sell, provenance);
        AssertEvents(new[] { correction }, TickFile.SortAndDedupe(new List<Trade> { original, correction }));
        using (var writer = new TickFile.Writer(RawPath, _instrument))
        { writer.Write(original); writer.Write(correction); }
        Assert.Throws<InvalidDataException>(() => TickFile.Enumerate(RawPath).ToList());
        AssertEvents(new[] { original, correction }, TickFile.Enumerate(RawPath, requireOrdered: false).ToList());
        AssertEvents(new[] { correction }, HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, Day));
    }

    [Fact]
    public void AdjacentForwardDatedCorrectionRequiresNormalizationBeforeOrderedStreaming()
    {
        var original = new Trade(1, Start, 100, 2, TradeSide.Buy, new TradeProvenance(DataQuality.Raw, SourceIdentity: "fixture:adjacent"));
        var correction = new Trade(1, Start.AddSeconds(1), 100, 7, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "fixture:adjacent", CorrectionRevision: 2));
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
        { writer.Write(original); writer.Write(correction); }
        Assert.Throws<InvalidDataException>(() => TickFile.Enumerate(RawPath).ToList());
        AssertEvents(new[] { original, correction }, TickFile.Enumerate(RawPath, requireOrdered: false).ToList());
        AssertEvents(new[] { correction }, HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, Day));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopenedAndDuplicateSegmentsArchiveWithoutDiscardingOriginalBytes(bool extended)
    {
        var first = new Trade(10, Start, 100, 2, TradeSide.Buy);
        var second = new Trade(11, Start.AddSeconds(1), 100.25m, 3, TradeSide.Sell);
        var correction = new Trade(10, Start, 100.5m, 9, TradeSide.Buy);
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: extended))
        { writer.Write(first); writer.Write(second); }
        using (var writer = new TickFile.Writer(RawPath, _instrument))
        { writer.Write(first); writer.Write(correction); }
        byte[] source = File.ReadAllBytes(RawPath);
        AssertEvents(new[] { first, second, first, correction }, TickFile.Enumerate(RawPath, requireOrdered: false).ToList());
        Assert.Throws<InvalidDataException>(() => TickFile.Enumerate(RawPath).ToList());
        var archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        Assert.Equal(source, File.ReadAllBytes(RawPath));
        var stored = TickFile.Read(archive, out int dropped, requireComplete: true);
        Assert.Equal(0, dropped);
        AssertEvents(new[] { first, second, first, correction }, stored);
        File.Delete(RawPath);
        var day = HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, Day);
        Assert.Equal(new long[] { 10, 11 }, day.Select(t => t.Id));
        Assert.Equal(9m, day[0].Volume);
        Assert.Equal(3m, day[1].Volume);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TruncatedSourceCannotPublishOrReplaceAnArchive(bool extended, bool existingArchive)
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: extended))
        {
            writer.Write(new Trade(1, Start, 100, 2, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddSeconds(1), 100.25m, 3, TradeSide.Sell));
        }
        string archive = TickArchive.PathFor(RawPath);
        byte[]? previousArchive = existingArchive ? File.ReadAllBytes(TickArchive.ArchiveDay(_root, _instrument, Day)) : null;
        byte[] truncated = File.ReadAllBytes(RawPath)[..^1];
        File.WriteAllBytes(RawPath, truncated);
        Assert.Throws<InvalidDataException>(() => TickArchive.ArchiveDay(_root, _instrument, Day));
        Assert.Equal(truncated, File.ReadAllBytes(RawPath));
        if (previousArchive == null) Assert.False(File.Exists(archive));
        else Assert.Equal(previousArchive, File.ReadAllBytes(archive));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(RawPath)!, "*.tmp"));
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("resolution")]
    [InlineData("count")]
    [InlineData("source-length")]
    [InlineData("source-encoding")]
    [InlineData("availability")]
    [InlineData("event-length")]
    [InlineData("event-encoding")]
    [InlineData("event-limit")]
    [InlineData("phase")]
    [InlineData("negative-volume")]
    [InlineData("decimal-flags")]
    [InlineData("volume-mismatch")]
    [InlineData("time-mismatch")]
    [InlineData("scale")]
    [InlineData("side")]
    [InlineData("missing-keyframe")]
    public void CorruptExactOrProvenanceFieldsNeverBecomeArchivedFacts(string damage)
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
            writer.Write(new Trade(-7, Start.AddTicks(123), 100, 2.5m, TradeSide.None,
                new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide, 60, "x", 1, 0, true, 30, "event:x")));
        byte[] bytes = File.ReadAllBytes(RawPath);
        var offsets = DefinitionOffsets(bytes);
        switch (damage)
        {
            case "quality": bytes[offsets.Quality] = 128; break;
            case "resolution": bytes[offsets.Resolution] = 0; break;
            case "count":
                bytes = bytes[..offsets.Count].Concat(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 }).Concat(bytes[(offsets.Count + 1)..]).ToArray();
                break;
            case "source-length": bytes[offsets.SourceLength] = 127; break;
            case "source-encoding": bytes[offsets.SourceLength + 1] = 0xFF; break;
            case "availability": bytes[offsets.Availability] = 2; break;
            case "event-length": bytes[offsets.EventLength] = 127; break;
            case "event-encoding": bytes[offsets.EventData] = 0xFF; break;
            case "event-limit":
                bytes = bytes[..offsets.EventLength].Concat(new byte[] { 0x81, 0x80, 0x01 }).Concat(bytes[(offsets.EventLength + 1)..]).ToArray();
                break;
            case "phase": bytes[offsets.Phase] = 60; break;
            case "negative-volume": bytes[offsets.Exact + 23] |= 0x80; break;
            case "decimal-flags": bytes[offsets.Exact + 20] |= 1; break;
            case "volume-mismatch": bytes[offsets.Exact + 8] ^= 1; break;
            case "time-mismatch":
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offsets.Exact), Start.AddMilliseconds(1).AddTicks(123).Ticks);
                break;
            case "scale": bytes[offsets.Record] |= 0xF8; break;
            case "side": bytes[offsets.Record] |= 3; break;
            case "missing-keyframe": bytes[offsets.Record] &= unchecked((byte)~4); break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        File.WriteAllBytes(RawPath, bytes);
        Assert.Throws<InvalidDataException>(() => TickFile.Read(RawPath, out _, requireComplete: true));
        Assert.Throws<InvalidDataException>(() => TickFile.Enumerate(RawPath, requireOrdered: false).ToList());
        Assert.Throws<InvalidDataException>(() => TickArchive.ArchiveDay(_root, _instrument, Day));
        Assert.Equal(bytes, File.ReadAllBytes(RawPath));
        Assert.False(File.Exists(TickArchive.PathFor(RawPath)));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(RawPath)!, "*.tmp"));
    }

    [Fact]
    public void RepairKeepsRecoveredExactQuantitiesAvailabilityAndSourcePhase()
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
            writer.Write(new Trade(1, Start, 100, 1, TradeSide.Buy));
        byte[] firstSegment = File.ReadAllBytes(RawPath);
        int firstRecord = 5 + firstSegment[4];
        firstSegment[firstRecord] |= 3;
        File.WriteAllBytes(RawPath, firstSegment);
        var recovered = new Trade(-9, Start.AddSeconds(1).AddTicks(4321), 0, 0.1234567890123456789012345678m, TradeSide.Sell,
            new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide, 300, "repair:source", -99, 0, false, 60, "repair:native"));
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true)) writer.Write(recovered);
        Assert.Equal(1, TickFile.Repair(RawPath, _instrument));
        AssertEvents(new[] { recovered }, TickFile.Read(RawPath, out int dropped, requireComplete: true));
        Assert.Equal(0, dropped);
        AssertEvents(new[] { recovered }, TickFile.Enumerate(RawPath).ToList());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(RawPath)!, "*.repair"));
    }

    [Fact]
    public void WideLegacyUpgradeStreamsBeforeAcceptingAnExactNativeEvent()
    {
        const int count = 200_000;
        const decimal quantity = 0.12345678m;
        using (var writer = new TickFile.Writer(RawPath, _instrument))
            for (int i = 0; i < count; i++)
                writer.Write(new Trade(i + 1, Start.AddMilliseconds(i), 100.25m, quantity, TradeSide.Buy));
        using var lease = TickFile.TryLock(_root, _instrument.FeedId, _instrument.Symbol);
        Assert.NotNull(lease);
        long before = GC.GetAllocatedBytesForCurrentThread();
        TickFile.EnsureProvenanceFormat(RawPath, _instrument);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 16L * 1024 * 1024, $"Legacy upgrade allocated {allocated:N0} bytes instead of streaming.");
        var native = new Trade(count + 1, Start.AddMilliseconds(count).AddTicks(123), 100.5m, 0.1234567890123456789012345678m, TradeSide.Sell,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "fixture:upgrade", SourceEventId: "event:extended"));
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true)) writer.Write(native);
        int index = 0;
        decimal originalVolume = 0;
        foreach (var trade in TickFile.Enumerate(RawPath))
        {
            if (index < count)
            {
                Assert.Equal(index + 1, trade.Id);
                Assert.Equal(Start.AddMilliseconds(index), trade.Time);
                Assert.Equal(100.25m, trade.Price);
                Assert.Equal(quantity, trade.Volume);
                Assert.Equal(TradeSide.Buy, trade.Side);
                Assert.True(trade.IsObservedTrade && trade.HasKnownAvailability);
                Assert.Null(trade.Provenance.SourceEventId);
                originalVolume += trade.Volume;
            }
            else AssertEvents(new[] { native }, new[] { trade });
            index++;
        }
        Assert.Equal(count + 1, index);
        Assert.Equal(24_691.356m, originalVolume);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(RawPath)!, "*.upgrade"));
    }

    [Fact]
    public void LegacyUpgradePreservesEveryHealthyReopenedCorrectionOccurrence()
    {
        var first = new Trade(1, Start, 100, 2, TradeSide.Buy);
        var second = new Trade(2, Start.AddSeconds(1), 100.25m, 3, TradeSide.Sell);
        var correction = new Trade(1, Start, 100.25m, 7, TradeSide.Buy);
        using (var writer = new TickFile.Writer(RawPath, _instrument))
        { writer.Write(first); writer.Write(second); }
        using (var writer = new TickFile.Writer(RawPath, _instrument))
        { writer.Write(first); writer.Write(correction); }
        using var lease = TickFile.TryLock(_root, _instrument.FeedId, _instrument.Symbol);
        Assert.NotNull(lease);
        TickFile.EnsureProvenanceFormat(RawPath, _instrument);
        var stored = new[] { first, second, first, correction };
        AssertEvents(stored, TickFile.Read(RawPath, out int dropped, requireComplete: true));
        Assert.Equal(0, dropped);
        AssertEvents(stored, TickFile.Enumerate(RawPath, requireOrdered: false).ToList());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(RawPath)!, "*.upgrade"));
    }

    [Theory]
    [InlineData("tail")]
    [InlineData("flags")]
    [InlineData("header")]
    [InlineData("tick-size")]
    public void FailedLegacyUpgradeRetainsTheOriginalAndRemovesItsStagingFile(string damage)
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument))
        {
            writer.Write(new Trade(1, Start, 100.25m, 2, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddSeconds(1), 100.5m, 3, TradeSide.Sell));
        }
        byte[] raw = File.ReadAllBytes(RawPath);
        var instrument = _instrument;
        switch (damage)
        {
            case "tail": raw = raw[..^1]; break;
            case "flags": raw[5 + raw[4]] |= 3; break;
            case "header": raw = raw[..3]; break;
            case "tick-size":
                instrument = new Instrument { FeedId = _instrument.FeedId, Symbol = _instrument.Symbol, TickSize = 0.1m, VolumeStep = _instrument.VolumeStep };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        File.WriteAllBytes(RawPath, raw);
        using var lease = TickFile.TryLock(_root, _instrument.FeedId, _instrument.Symbol);
        Assert.NotNull(lease);
        Assert.Throws<InvalidDataException>(() => TickFile.EnsureProvenanceFormat(RawPath, instrument));
        Assert.Equal(raw, File.ReadAllBytes(RawPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(RawPath)!, "*.upgrade"));
    }

    [Theory]
    [InlineData("sub-millisecond", false)]
    [InlineData("sub-millisecond", true)]
    [InlineData("zero-price", false)]
    [InlineData("zero-price", true)]
    [InlineData("negative-price", false)]
    [InlineData("negative-price", true)]
    [InlineData("wide-decimal", false)]
    [InlineData("wide-decimal", true)]
    [InlineData("wide-fraction", false)]
    [InlineData("wide-fraction", true)]
    public async Task RecorderUpgradesDefaultRawLegacyDayWithoutStopping(string extendedField, bool writerAlreadyOpen)
    {
        var seed = new Trade(1, Start, 100, 1, TradeSide.Buy);
        var ordinary = new Trade(2, Start.AddSeconds(1), 100.25m, 2, TradeSide.Buy);
        DateTime time = Start.AddSeconds(2);
        decimal price = 100.5m, quantity = 3;
        switch (extendedField)
        {
            case "sub-millisecond": time = time.AddTicks(1234); break;
            case "zero-price": price = 0; break;
            case "negative-price": price = -0.25m; break;
            case "wide-decimal": quantity = 1234567890123456789012345.6789m; break;
            case "wide-fraction": quantity = 0.1234567890123456789012345678m; break;
            default: throw new ArgumentOutOfRangeException(nameof(extendedField));
        }
        var exact = new Trade(3, time, price, quantity, TradeSide.Sell);
        var later = new Trade(4, Start.AddSeconds(3), 100.75m, 4, TradeSide.Buy);
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: false)) writer.Write(seed);
        using var recorder = new TickRecorder(_root, _instrument);
        Assert.True(recorder.IsActive);
        try
        {
            if (writerAlreadyOpen)
            {
                // A separately flushed ordinary batch leaves the existing legacy writer open before the exact event arrives.
                recorder.WriteLive(new[] { ordinary });
                using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    flushTimeout.Token.ThrowIfCancellationRequested();
                    Assert.Null(recorder.Error);
                    Assert.True(recorder.IsActive);
                    var persisted = TickFile.ReadAppendPrefix(RawPath, out int droppedPrefix, _instrument.TickSize);
                    Assert.Equal(0, droppedPrefix);
                    if (persisted.Count >= 2)
                    {
                        AssertEvents(new[] { seed, ordinary }, persisted);
                        break;
                    }
                    Assert.Equal(seed.Id, Assert.Single(persisted).Id);
                    await Task.Delay(5, flushTimeout.Token);
                }
            }
            recorder.WriteLive(new[] { exact, later });
        }
        finally
        {
            recorder.Dispose();
            using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (recorder.IsActive) await Task.Delay(5, drainTimeout.Token);
        }
        Assert.Null(recorder.Error);
        Assert.False(recorder.IsActive);
        var expected = writerAlreadyOpen ? new[] { seed, ordinary, exact, later } : new[] { seed, exact, later };
        AssertEvents(expected, TickFile.Read(RawPath, out int dropped, requireComplete: true));
        Assert.Equal(0, dropped);
        AssertEvents(expected, TickFile.Enumerate(RawPath).ToList());
        using var releasedLease = TickFile.TryLock(_root, _instrument.FeedId, _instrument.Symbol);
        Assert.NotNull(releasedLease);
        Assert.False(TickFile.HasActiveRecordingWriter(_root, _instrument.FeedId, _instrument.Symbol));
    }

    [Fact]
    public async Task ArchiveOnlyRecorderContinuationPreservesPriorEventsAndNativeCorrections()
    {
        const string source = "fixture:resume";
        var original = new Trade(17, Start.AddTicks(123), 100, 0.1234567890123456789012345678m, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: source, SourceEventId: "event:a"));
        var retained = new Trade(18, Start.AddSeconds(1), 100.25m, 3, TradeSide.Sell,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: source, SourceEventId: "event:b"));
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
        { writer.Write(original); writer.Write(retained); }
        byte[] originalRaw = File.ReadAllBytes(RawPath);
        var archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        byte[] archived = File.ReadAllBytes(archive);
        File.Delete(RawPath);
        var correction = new Trade(901, Start.AddSeconds(2), 100.5m, 5, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: source, CorrectionRevision: -7, SourceEventId: "event:a"));
        var next = new Trade(19, Start.AddSeconds(3), 100.75m, 7, TradeSide.Sell,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: source, SourceEventId: "event:c"));
        using var recorder = new TickRecorder(_root, _instrument);
        Assert.True(recorder.IsActive);
        recorder.WriteLive(new[] { correction, next });
        recorder.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (recorder.IsActive) await Task.Delay(5, timeout.Token);
        Assert.Null(recorder.Error);
        Assert.True(File.ReadAllBytes(RawPath).AsSpan().StartsWith(originalRaw));
        AssertEvents(new[] { original, retained, correction, next }, TickFile.Read(RawPath, out _, requireComplete: true));
        AssertEvents(new[] { retained, correction, next }, HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, Day));
        AssertEvents(new[] { original, retained }, TickFile.Read(archive, out _, requireComplete: true));
        Assert.Equal(archived, File.ReadAllBytes(archive));
    }

    [Fact]
    public void RestoringALegacyArchiveDoesNotMigrateItOrReplaceAContinuedRawDay()
    {
        var first = new Trade(1, Start, 100, 2, TradeSide.Buy);
        var second = new Trade(2, Start.AddSeconds(1), 100.25m, 3, TradeSide.Sell);
        var native = new Trade(3, Start.AddSeconds(2).AddTicks(1), 100.5m, 0.1234567890123456789012345678m, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "fixture:resume", SourceEventId: "event:third"));
        using (var writer = new TickFile.Writer(RawPath, _instrument)) writer.Write(first);
        byte[] raw = File.ReadAllBytes(RawPath);
        var archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        byte[] archived = File.ReadAllBytes(archive);
        File.Delete(RawPath);
        using var lease = TickFile.TryLock(_root, _instrument.FeedId, _instrument.Symbol);
        Assert.NotNull(lease);
        TickArchive.EnsureRawDay(RawPath);
        Assert.Equal(raw, File.ReadAllBytes(RawPath));
        using (var writer = new TickFile.Writer(RawPath, _instrument))
        {
            writer.Write(second);
            Assert.Throws<InvalidOperationException>(() => writer.Write(native));
        }
        byte[] continued = File.ReadAllBytes(RawPath);
        TickArchive.EnsureRawDay(RawPath);
        Assert.Equal(continued, File.ReadAllBytes(RawPath));
        TickFile.EnsureProvenanceFormat(RawPath, _instrument);
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true)) writer.Write(native);
        AssertEvents(new[] { first, second, native }, TickFile.Read(RawPath, out _, requireComplete: true));
        Assert.Equal(archived, File.ReadAllBytes(archive));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("length")]
    [InlineData("oversized")]
    [InlineData("header")]
    public void FailedArchiveRestorationCannotPublishAnUnverifiedRawDay(string damage)
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
            writer.Write(new Trade(1, Start.AddTicks(1), 100, 2, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceEventId: "event:retained")));
        long rawLength = new FileInfo(RawPath).Length;
        var archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        byte[] archived = File.ReadAllBytes(archive);
        switch (damage)
        {
            case "digest": archived[12] ^= 1; break;
            case "length": BinaryPrimitives.WriteInt64LittleEndian(archived.AsSpan(4), rawLength + 1); break;
            case "oversized": BinaryPrimitives.WriteInt64LittleEndian(archived.AsSpan(4), TickArchive.MaximumExpandedBytes + 1); break;
            case "header": archived = archived[..43]; break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        File.WriteAllBytes(archive, archived);
        File.Delete(RawPath);
        using var lease = TickFile.TryLock(_root, _instrument.FeedId, _instrument.Symbol);
        Assert.NotNull(lease);
        Assert.Throws<InvalidDataException>(() => TickArchive.EnsureRawDay(RawPath));
        Assert.False(File.Exists(RawPath));
        Assert.Equal(archived, File.ReadAllBytes(archive));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(RawPath)!, "*.tmp"));
    }

    [Fact]
    public void CanceledArchiveRestorationLeavesOnlyTheOriginalArchive()
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument)) writer.Write(new Trade(1, Start, 100, 2, TradeSide.Buy));
        var archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        byte[] archived = File.ReadAllBytes(archive);
        File.Delete(RawPath);
        using var lease = TickFile.TryLock(_root, _instrument.FeedId, _instrument.Symbol);
        Assert.NotNull(lease);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => TickArchive.EnsureRawDay(RawPath, canceled.Token));
        Assert.False(File.Exists(RawPath));
        Assert.Equal(archived, File.ReadAllBytes(archive));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(RawPath)!, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationPreservesRawAndPreviouslyPublishedArchive(bool existingArchive)
    {
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
            writer.Write(new Trade(1, Start.AddTicks(1), 100, 0.1234567890123456789012345678m, TradeSide.Buy));
        byte[] raw = File.ReadAllBytes(RawPath);
        string archive = TickArchive.PathFor(RawPath);
        byte[]? previousArchive = existingArchive ? File.ReadAllBytes(TickArchive.ArchiveDay(_root, _instrument, Day)) : null;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => TickArchive.ArchiveDay(_root, _instrument, Day, canceled.Token));
        Assert.Equal(raw, File.ReadAllBytes(RawPath));
        if (previousArchive == null) Assert.False(File.Exists(archive));
        else Assert.Equal(previousArchive, File.ReadAllBytes(archive));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(RawPath)!, "*.tmp"));
    }

    [Fact]
    public void OversizedRawSourceIsRejectedWithoutReplacingOrDeletingIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RawPath)!);
        using (var sparse = new FileStream(RawPath, FileMode.CreateNew, FileAccess.Write))
            sparse.SetLength(TickArchive.MaximumExpandedBytes + 1);
        Assert.Throws<InvalidDataException>(() => TickArchive.ArchiveDay(_root, _instrument, Day));
        Assert.Equal(TickArchive.MaximumExpandedBytes + 1, new FileInfo(RawPath).Length);
        Assert.False(File.Exists(TickArchive.PathFor(RawPath)));
    }

    [Fact]
    public void WideDayArchivalDoesNotAllocateRawOrExpandedDayCopies()
    {
        const int count = 600_000;
        const decimal quantity = 1234567890123456789012345.6789m;
        using (var writer = new TickFile.Writer(RawPath, _instrument, includeProvenance: true))
            for (int i = 0; i < count; i++)
                writer.Write(new Trade(i + 1, Start.AddMilliseconds(i), 100.25m, quantity, TradeSide.Buy));
        Assert.True(new FileInfo(RawPath).Length > 16L * 1024 * 1024, "The fixture must be wide enough to detect whole-day copies.");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 16L * 1024 * 1024, $"Archival allocated {allocated:N0} bytes for a streaming day.");
        int read = 0;
        Trade last = default;
        foreach (var trade in TickFile.Enumerate(archive)) { read++; last = trade; }
        Assert.Equal(count, read);
        Assert.Equal(quantity, last.Volume);
    }

    private static (int Record, int Quality, int Resolution, int Count, int SourceLength, int Availability, int Phase, int Exact, int EventLength, int EventData) DefinitionOffsets(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = new BinaryReader(input);
        input.Position = 4;
        int tickLength = reader.ReadByte();
        input.Position += tickLength;
        int record = checked((int)input.Position);
        reader.ReadByte();
        for (int field = 0; field < 5; field++) ReadUnsigned(reader); // ID, milliseconds, price, quantity, definition token.
        int quality = checked((int)input.Position);
        reader.ReadByte();
        int resolution = checked((int)input.Position);
        ReadUnsigned(reader);
        ReadUnsigned(reader); // Correction revision.
        int count = checked((int)input.Position);
        ReadUnsigned(reader);
        int sourceLength = checked((int)input.Position);
        ulong length = ReadUnsigned(reader);
        input.Position += checked((int)length);
        int availability = checked((int)input.Position);
        reader.ReadByte();
        int phase = checked((int)input.Position);
        ReadUnsigned(reader);
        int exact = checked((int)input.Position);
        input.Position += 24;
        int eventLength = checked((int)input.Position);
        ReadUnsigned(reader);
        return (record, quality, resolution, count, sourceLength, availability, phase, exact, eventLength, checked((int)input.Position));
    }

    private static ulong ReadUnsigned(BinaryReader reader)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte current = reader.ReadByte();
            value |= (ulong)(current & 127) << shift;
            if ((current & 128) == 0) return value;
        }
        throw new InvalidDataException("Invalid fixture varint.");
    }

    private static void AssertEvents(IReadOnlyList<Trade> expected, IReadOnlyList<Trade> actual)
    {
        Assert.Equal(expected.Select(t => (t.Id, t.Time, t.Price, t.Volume, t.Side, t.Provenance)),
            actual.Select(t => (t.Id, t.Time, t.Price, t.Volume, t.Side, t.Provenance)));
    }
    // A recording writer keeps write access, so readers must share writes like TickFile.Read does.
    private static byte[] ReadWhileWriterOpen(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
