using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class HistoryInputIdentityTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Directory.CreateTempSubdirectory("tradinghub-history-identity-").FullName;
    private readonly Instrument _instrument = Instrument("identity-fixture", "EXACT");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void SameLengthSameTimestampCorrectionInvalidatesOnlyTheAffectedInput()
    {
        Write(_instrument, Start, 100m);
        var loaded = Load(_instrument, Start, Start.AddDays(1));
        string path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start));
        long length = new FileInfo(path).Length;
        DateTime modified = File.GetLastWriteTimeUtc(path);
        var unrelated = Instrument(_instrument.FeedId, "OTHER");
        Write(unrelated, Start, 300m);
        Write(_instrument, Start.AddDays(2), 400m);
        Assert.True(loaded.IsCurrent(_root, Start, Start.AddDays(1)));

        Write(_instrument, Start, 101m);
        File.SetLastWriteTimeUtc(path, modified);

        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.Equal(100m, loaded.Get(loaded.Start).Price);
        Assert.False(loaded.IsCurrent(_root, Start, Start.AddDays(1)));
        Assert.Equal(101m, Load(_instrument, Start, Start.AddDays(1)).Get(0).Price);
    }

    [Fact]
    public void PartialMiddleDayDeletionInvalidatesTheRangeWithoutChangingRetainedGeneration()
    {
        for (int day = 0; day < 3; day++) Write(_instrument, Start.AddDays(day), 100m + day);
        var loaded = Load(_instrument, Start, Start.AddDays(3));
        File.Delete(TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start.AddDays(1))));

        Assert.False(loaded.IsCurrent(_root, Start, Start.AddDays(3)));
        Assert.Equal(6, loaded.Count);
        Assert.Equal(4, Load(_instrument, Start, Start.AddDays(3)).Count);
    }

    [Fact]
    public void EveryCombinedConstituentInvalidatesButThePhysicalPrimaryRemainsIndependent()
    {
        var primary = Instrument("rithmic", "NQ");
        var secondary = Instrument(HistorySources.Databento, "NQ");
        Write(primary, Start, 100m);
        Write(secondary, Start, 200m);
        var combined = Load(primary, Start, Start.AddDays(1));
        var primaryOnly = Load(Instrument("rithmic-solo", "NQ"), Start, Start.AddDays(1));
        var initialHours = HistorySources.ReadHourlyCloses(_root, primary.FeedId, primary.Symbol, DateOnly.FromDateTime(Start), out string initialIdentity);
        Write(secondary, Start, 201m);

        Assert.False(combined.IsCurrent(_root, Start, Start.AddDays(1)));
        Assert.True(primaryOnly.IsCurrent(_root, Start, Start.AddDays(1)));
        var correctedHours = HistorySources.ReadHourlyCloses(_root, primary.FeedId, primary.Symbol, DateOnly.FromDateTime(Start), out string correctedIdentity);
        Assert.NotEqual(initialIdentity, correctedIdentity);
        Assert.Equal(initialHours, correctedHours);
        Assert.Equal(101m, correctedHours[0]);
    }

    [Fact]
    public void ArchivingTheSameDecodedContentDoesNotInventANewSourceGeneration()
    {
        Write(_instrument, Start, 100m);
        var loaded = Load(_instrument, Start, Start.AddDays(1));
        TickArchive.ArchiveDay(_root, _instrument, DateOnly.FromDateTime(Start));
        File.Delete(TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(Start)));

        Assert.True(loaded.IsCurrent(_root, Start, Start.AddDays(1)));
        Assert.Equal(loaded.ContentIdentity, Load(_instrument, Start, Start.AddDays(1)).ContentIdentity);
    }

    [Fact]
    public void EvidenceUsesTheDecodedGenerationNotAPathRereadAfterTheLoad()
    {
        Write(_instrument, Start, 100m);
        Write(_instrument, Start.AddDays(1), 200m);
        bool changed = false;
        var loaded = TickData.Load(_root, _instrument, Start, Start.AddDays(2), new InlineProgress(_ =>
        {
            if (changed) return;
            changed = true;
            Write(_instrument, Start, 101m);
        }), CancellationToken.None);
        var evidence = RunInputEvidence.Capture(loaded);

        Assert.True(changed);
        Assert.Equal(100m, loaded.Get(0).Price);
        Assert.Equal(loaded.ContentIdentity, evidence.ContentIdentity);
        Assert.NotEqual(HistorySources.GetRangeContentIdentity(_root, _instrument.FeedId, _instrument.Symbol, Start, Start.AddDays(2)), evidence.ContentIdentity);
        Assert.False(loaded.IsCurrent(_root, Start, Start.AddDays(2)));
        Assert.Equal(evidence.ContentIdentity, loaded.Slice(Start, Start.AddDays(1)).ContentIdentity);
        Assert.Equal(evidence.ContentIdentity, loaded.Compress().ContentIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedCombinedSourcePreservesDistinctNativeEventOrderAtEqualLocations(bool withSecondary)
    {
        const int count = 32;
        var primary = Instrument("rithmic", "TIES");
        var nativeIds = Enumerable.Range(0, count).Select(index => "tie:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var rows = nativeIds.Select((nativeId, index) => new Trade(77, Start, 100m + index, 1m, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue:ties", SourceEventId: nativeId))).ToArray();
        HistoricalData.WriteDay(_root, primary, DateOnly.FromDateTime(Start), rows);
        if (withSecondary)
            Write(Instrument(HistorySources.Databento, primary.Symbol), Start.AddHours(1), 200m);

        var physical = Load(Instrument("rithmic-solo", primary.Symbol), Start, Start.AddDays(1));
        var combined = Load(primary, Start, Start.AddDays(1));

        Assert.Equal(nativeIds, Enumerable.Range(0, physical.Count).Select(index => physical.Get(index).Provenance.SourceEventId));
        Assert.Equal(withSecondary ? count + 2 : count, combined.Count);
        Assert.Equal(nativeIds, Enumerable.Range(0, count).Select(index => combined.Get(index).Provenance.SourceEventId));
        Assert.Equal(rows.Select(trade => trade.Price), Enumerable.Range(0, count).Select(index => combined.Get(index).Price));
        if (withSecondary)
            Assert.Equal(new[] { 200m, 201m }, Enumerable.Range(count, 2).Select(index => combined.Get(index).Price));
    }

    private TickData Load(Instrument instrument, DateTime from, DateTime to) => TickData.Load(_root, instrument, from, to, null, CancellationToken.None);

    private void Write(Instrument instrument, DateTime start, decimal price) => HistoricalData.WriteDay(_root, instrument, DateOnly.FromDateTime(start), new[]
    {
        new Trade(1, start, price, 1m, TradeSide.Buy),
        new Trade(2, start.AddSeconds(1), price + 1m, 1m, TradeSide.Sell),
    });

    private static Instrument Instrument(string feed, string symbol) => new() { FeedId = feed, Symbol = symbol, TickSize = 0.25m, VolumeStep = 1, PointValue = 1 };
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
