using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class CombinedHistoryIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-combined-identity-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = DateOnly.FromDateTime(Start);
    private const string NativeEventId = "native:東京/Event-A";
    private const decimal NativeQuantity = 12345678901234567890.12345678m;

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData("rithmic")]
    [InlineData("tradovate")]
    public void SameLegacyNumericIdFromDifferentSourcesSurvivesReloadAndCorrectsOnlyItsOwnPrint(string feedId)
    {
        WriteSource(feedId, [new Trade(7, Start.AddMinutes(1), 20_000m, 1.23456789m, TradeSide.Buy)], legacy: true);
        WriteSource(HistorySources.Databento, [
            new Trade(7, Start, 30_000m, 2.125m, TradeSide.Sell),
            new Trade(8, Start.AddMinutes(1), 30_000.25m, 100m, TradeSide.Buy),
            new Trade(9, Start.AddMinutes(2), 30_000.50m, 7.875m, TradeSide.Sell),
        ], legacy: true);
        var combined = HistorySources.ReadDay(_root, feedId, "NQ", Day);
        Assert.Equal(3, combined.Count);
        Assert.Equal(new[] { Start, Start.AddMinutes(1), Start.AddMinutes(2) }, combined.Select(trade => trade.Time));
        Assert.Equal(new[] { 30_000m, 20_000m, 30_000.50m }, combined.Select(trade => trade.Price));
        Assert.Equal(new[] { 2.125m, 1.23456789m, 7.875m }, combined.Select(trade => trade.Volume));

        var primaryKey = new TradeIdentity(feedId + ":NQ", null, Day.DayNumber, 7);
        var secondaryKey = new TradeIdentity("databento:NQ", null, Day.DayNumber, 7);
        var tailKey = new TradeIdentity("databento:NQ", null, Day.DayNumber, 9);
        var primaryOnly = Assert.Single(HistorySources.ReadDay(_root, feedId + HistorySources.OnlySuffix, "NQ", Day));
        Assert.Equal(primaryKey, TradeIdentity.From(primaryOnly));
        Assert.Equal(secondaryKey, TradeIdentity.From(HistorySources.ReadDay(_root, HistorySources.Databento, "NQ", Day)[0]));

        var store = new TradeStore(Instrument(feedId));
        store.Replace(combined);
        Assert.NotEqual(store.FindIdentity(primaryKey), store.FindIdentity(secondaryKey));
        string path = WriteSnapshot(feedId, Enumerable.Range(0, store.Count).Select(store.Get).ToArray());
        store.Replace(ReadSnapshot(path));
        Assert.Equal(3, store.Count);
        Assert.Equal((20_000m, 1.23456789m), (Find(store, primaryKey).Price, Find(store, primaryKey).Volume));
        Assert.Equal((30_000m, 2.125m), (Find(store, secondaryKey).Price, Find(store, secondaryKey).Volume));
        var slice = TickData.FromStore(store).Slice(Start, Start.AddMinutes(2));
        var slicedStore = new TradeStore(Instrument(feedId));
        slicedStore.Replace(Enumerable.Range(slice.Start, slice.Count).Select(slice.Get));
        Assert.Equal(2, slicedStore.Count);
        Assert.Equal(1.23456789m, Find(slicedStore, primaryKey).Volume);
        Assert.Equal(2.125m, Find(slicedStore, secondaryKey).Volume);

        // The correction uses the original source/day/numeric key, shared by no other provider's print.
        var correction = new Trade(7, Start.AddMinutes(1), 20_001m, 9.87654321m, TradeSide.Sell,
            primaryOnly.Provenance with { CorrectionRevision = 17 });
        store.Update(store.FindIdentity(TradeIdentity.From(correction)), correction);
        Assert.Equal(3, store.Count);
        Assert.Equal(2.125m, Find(store, secondaryKey).Volume);
        using (var writer = new TickFile.Writer(path, Instrument(feedId), includeProvenance: true)) writer.Write(correction);
        store.Replace(ReadSnapshot(path));
        Assert.Equal(3, store.Count);
        Assert.Equal((20_001m, 9.87654321m, TradeSide.Sell, 17L),
            (Find(store, primaryKey).Price, Find(store, primaryKey).Volume, Find(store, primaryKey).Side, Find(store, primaryKey).Provenance.CorrectionRevision));
        Assert.Equal((30_000m, 2.125m), (Find(store, secondaryKey).Price, Find(store, secondaryKey).Volume));
        Assert.Equal((30_000.50m, 7.875m), (Find(store, tailKey).Price, Find(store, tailKey).Volume));
        Assert.Equal(19.87654321m, Enumerable.Range(0, store.Count).Sum(store.GetVolume));
        Assert.All(Enumerable.Range(0, store.Count), index => Assert.True(store.Get(index).IsObservedTrade));
        var unchangedSource = Assert.Single(HistorySources.ReadDay(_root, feedId + HistorySources.OnlySuffix, "NQ", Day));
        Assert.Equal((7L, 20_000m, 1.23456789m), (unchangedSource.Id, unchangedSource.Price, unchangedSource.Volume));
    }

    [Fact]
    public async Task NativeCollisionsRemainProviderScopedAndNegativeSyntheticHistoryRetainsItsQuality()
    {
        WriteSource("rithmic", [new Trade(77, Start.AddMinutes(1), 20_000m, NativeQuantity, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw | DataQuality.Partial, SourceIdentity: "rithmic:NQ", CorrectionRevision: 19, SourceEventId: NativeEventId))]);
        WriteSource(HistorySources.Databento, [
            new Trade(77, Start, 30_000m, 2.125m, TradeSide.Sell,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: "databento:NQ", SourceEventId: NativeEventId)),
            new Trade(78, Start.AddMinutes(1), 30_000.25m, 100m, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: "databento:NQ", SourceEventId: "suppressed")),
            new Trade(-42, Start.AddMinutes(2), 0m, 3.5m, TradeSide.None,
                new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, 60, "databento:NQ:ohlcv", -7, 0)),
            new Trade(79, Start.AddMinutes(3).AddSeconds(10), -1m, 7.875m, TradeSide.Sell,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: "databento:NQ", SourceEventId: "native:東京/event-a")),
        ]);
        var combined = HistorySources.ReadDay(_root, "rithmic", "NQ", Day);
        var store = new TradeStore(Instrument("rithmic"));
        store.Replace(combined);
        AssertNativeHistory(store);
        var ticks = TickData.Load(_root, Instrument("rithmic"), Start, Start.AddMinutes(4), null, default);
        store.Replace(Enumerable.Range(ticks.Start, ticks.Count).Select(ticks.Get));
        AssertNativeHistory(store);
        using var replay = new ReplayFeed(_root, Instrument("rithmic"), Start.AddMinutes(4), Start.AddMinutes(5));
        var replayRows = new List<Trade>();
        await replay.LoadTradesAsync(replay.Instrument, Start, Start.AddMinutes(4), null,
            batch => { replayRows.AddRange(batch); return true; }, null, default);
        store.Replace(replayRows);
        AssertNativeHistory(store);
        string path = WriteSnapshot("rithmic", replayRows);
        store.Replace(TickFile.Enumerate(path));
        AssertNativeHistory(store);
        store.Replace(ReadSnapshot(path));
        AssertNativeHistory(store);

        var primaryKey = new TradeIdentity("rithmic:NQ", NativeEventId, 0, 0);
        var primary = Find(store, primaryKey);
        var correction = new Trade(9000, primary.Time, 20_001m, 9.87654321m, TradeSide.Sell,
            primary.Provenance with { CorrectionRevision = 20 });
        store.Update(store.FindIdentity(TradeIdentity.From(correction)), correction);
        using (var writer = new TickFile.Writer(path, Instrument("rithmic"), includeProvenance: true)) writer.Write(correction);
        store.Replace(ReadSnapshot(path));
        Assert.Equal(4, store.Count);
        Assert.Equal((9000L, 20_001m, 9.87654321m, NativeEventId),
            (Find(store, primaryKey).Id, Find(store, primaryKey).Price, Find(store, primaryKey).Volume, Find(store, primaryKey).Provenance.SourceEventId));
        Assert.Equal((77L, 30_000m, 2.125m), (Find(store, new("databento:NQ", NativeEventId, 0, 0)).Id,
            Find(store, new("databento:NQ", NativeEventId, 0, 0)).Price, Find(store, new("databento:NQ", NativeEventId, 0, 0)).Volume));
        Assert.Equal(-42L, Find(store, new("databento:NQ:ohlcv", null, Day.DayNumber, -42)).Id);
        Assert.Equal(7.875m, Find(store, new("databento:NQ", "native:東京/event-a", 0, 0)).Volume);
    }

    private static void AssertNativeHistory(TradeStore store)
    {
        Assert.Equal(4, store.Count);
        var primary = Find(store, new("rithmic:NQ", NativeEventId, 0, 0));
        Assert.Equal((77L, 20_000m, NativeQuantity, DataQuality.Raw | DataQuality.Partial, 19L),
            (primary.Id, primary.Price, primary.Volume, primary.Provenance.Quality, primary.Provenance.CorrectionRevision));
        var secondary = Find(store, new("databento:NQ", NativeEventId, 0, 0));
        Assert.Equal((77L, 30_000m, 2.125m), (secondary.Id, secondary.Price, secondary.Volume));
        var distinctCase = Find(store, new("databento:NQ", "native:東京/event-a", 0, 0));
        Assert.Equal((79L, -1m, 7.875m), (distinctCase.Id, distinctCase.Price, distinctCase.Volume));
        var synthetic = Find(store, new("databento:NQ:ohlcv", null, Day.DayNumber, -42));
        Assert.Equal((-42L, 0m, 3.5m, 0), (synthetic.Id, synthetic.Price, synthetic.Volume, synthetic.Provenance.ObservedTradeCount));
        Assert.False(synthetic.IsObservedTrade || synthetic.Provenance.IsMeasured);
        Assert.True(synthetic.Provenance.IsSideInferred && synthetic.Provenance.IsPartial);
        Assert.Equal(Start.AddMinutes(3), synthetic.AvailableAt);
    }

    private static Trade Find(TradeStore store, TradeIdentity identity)
    {
        int index = store.FindIdentity(identity);
        Assert.InRange(index, 0, store.Count - 1);
        return store.Get(index);
    }
    private static Instrument Instrument(string feedId) => new() { FeedId = feedId, Symbol = "NQ", TickSize = 0.25m, VolumeStep = 0.00000001m, PointValue = 20 };
    private void WriteSource(string feedId, IReadOnlyList<Trade> rows, bool legacy = false)
    {
        using var writer = new TickFile.Writer(TickFile.DayPath(_root, feedId, "NQ", Day), Instrument(feedId), includeProvenance: !legacy);
        foreach (var row in rows) writer.Write(row);
    }
    private string WriteSnapshot(string feedId, IReadOnlyList<Trade> rows)
    {
        string path = TickFile.DayPath(Path.Combine(_root, "combined-copy"), feedId, "NQ", Day);
        using var writer = new TickFile.Writer(path, Instrument(feedId), includeProvenance: true);
        foreach (var row in rows) writer.Write(row);
        return path;
    }
    private static List<Trade> ReadSnapshot(string path)
    {
        var rows = TickFile.Read(path, out int dropped, requireComplete: true);
        Assert.Equal(0, dropped);
        return TickFile.SortAndDedupe(rows);
    }
}
