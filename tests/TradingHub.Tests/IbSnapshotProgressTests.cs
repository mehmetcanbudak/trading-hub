using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class IbSnapshotProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-ib-snapshot-progress-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    public IbSnapshotProgressTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Inline_progress_can_read_a_complete_ib_snapshot_during_a_tick_data_load()
    {
        var instrument = IbBarImport.InstrumentFor("MNQ");
        HistoricalData.WriteDay(_root, instrument, DateOnly.FromDateTime(Start),
            new[] { new Trade(101, Start.AddSeconds(20), 90, 3, TradeSide.Buy) }, replace: true);
        string csv = Path.Combine(_root, "csv");
        Directory.CreateDirectory(csv);
        long unix = new DateTimeOffset(Start.AddMinutes(1)).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(csv, "MNQ.csv"), $"time,open,high,low,close,volume\n{unix},100,102,99,101,12\n");
        IbBarImport.Commit(IbBarImport.PreviewFolder(csv, _root, "MNQ"), _root);

        // Cancellation only bounds a deadlock: callback and load run synchronously on this test's own thread.
        // No blocked Task/worker is abandoned if snapshot acquisition cannot reenter.
        using var watchdog = new CancellationTokenSource(Watchdog);
        List<Trade>? observed = null;
        var progress = new InlineProgress(_ =>
        {
            using var snapshot = IbBarStore.OpenSnapshot(_root, "MNQ", watchdog.Token);
            observed = snapshot.ReadDay(DateOnly.FromDateTime(Start));
            observed.AddRange(snapshot.ReadDay(DateOnly.FromDateTime(Start.AddMinutes(1))));
        });

        var loaded = TickData.Load(_root, instrument, Start, Start.AddMinutes(2), progress, watchdog.Token);

        AssertRows(Enumerable.Range(0, loaded.Count).Select(loaded.Get));
        Assert.NotNull(observed);
        AssertRows(observed!);
    }

    [Fact]
    public void Inline_import_progress_can_read_a_complete_published_ib_snapshot()
    {
        var instrument = IbBarImport.InstrumentFor("MNQ");
        HistoricalData.WriteDay(_root, instrument, DateOnly.FromDateTime(Start),
            new[] { new Trade(101, Start.AddSeconds(20), 90, 3, TradeSide.Buy) }, replace: true);
        string csv = Path.Combine(_root, "csv");
        Directory.CreateDirectory(csv);
        long unix = new DateTimeOffset(Start.AddMinutes(1)).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(csv, "MNQ.csv"), $"time,open,high,low,close,volume\n{unix},100,102,99,101,12\n");

        // Inline callbacks run on this importing thread. Cancellation unwinds a blocked acquisition without abandoning work.
        using var watchdog = new CancellationTokenSource(Watchdog);
        List<Trade>? observed = null;
        var progress = new InlineProgress(_ =>
        {
            using var snapshot = IbBarStore.OpenSnapshot(_root, "MNQ", watchdog.Token);
            var complete = snapshot.ReadDay(DateOnly.FromDateTime(Start));
            complete.AddRange(snapshot.ReadDay(DateOnly.FromDateTime(Start.AddMinutes(1))));
            AssertRows(complete); // Every callback must observe the complete publication, including its imported next day.
            observed = complete;
        });
        var preview = IbBarImport.PreviewFolder(csv, _root, "MNQ", watchdog.Token);

        IbBarImport.Commit(preview, _root, progress: progress, ct: watchdog.Token);

        Assert.NotNull(observed);
        AssertRows(observed!);
        AssertRows(HistoricalData.Read(_root, "ib", "MNQ", Start, Start.AddMinutes(2)));
    }

    [Fact]
    public void Loaded_decimal_provenance_views_retain_the_range_stamp_and_same_metadata_content_replacement_invalidates_it()
    {
        var instrument = IbBarImport.InstrumentFor("MNQ");
        var day = DateOnly.FromDateTime(Start);
        var rows = new[]
        {
            new Trade(1, Start.AddTicks(1), 100, 0.1234567890123456789012345678m, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: "ib:MNQ", SourceEventId: "one")),
            new Trade(2, Start.AddTicks(2), 100, 0.2m, TradeSide.Buy,
                new TradeProvenance(DataQuality.Raw, SourceIdentity: "ib:MNQ", SourceEventId: "two")),
        };
        HistoricalData.WriteDay(_root, instrument, day, rows, replace: true);
        var loaded = TickData.Load(_root, instrument, Start, Start.AddMinutes(1), null, default);
        Assert.Equal("one", loaded.Get(0).Provenance.SourceEventId);
        Assert.Equal(rows[0].Volume, loaded.Volume(0));
        var captured = Assert.IsType<IbHistoryStamp>(loaded.StorageStamp);
        Assert.Equal(captured, IbBarStore.GetStamp(_root, "MNQ", Start, Start.AddMinutes(1)));
        foreach (var compact in new[]
        {
            loaded.Slice(Start, Start.AddMinutes(1)).Compress(),
            loaded.Compress().Slice(Start, Start.AddMinutes(1)),
            TickData.Load(_root, instrument, Start, Start.AddMinutes(1), null, default, compress: true),
        })
        {
            Assert.Equal(captured, compact.StorageStamp);
            Assert.Equal(1, compact.Count);
            var row = compact.Get(compact.Start);
            Assert.Equal(0.3234567890123456789012345678m, row.Volume);
            Assert.Equal(Start.AddTicks(2), row.AvailableAt);
            Assert.True(row.HasKnownAvailability);
            Assert.False(row.IsObservedTrade);
            Assert.True((row.Provenance.Quality & DataQuality.Compact) != 0);
            Assert.Equal(2, row.Provenance.ObservedTradeCount);
            Assert.Null(row.Provenance.SourceEventId);
        }

        string path = TickFile.DayPath(_root, "ib", "MNQ", day);
        long bytes = new FileInfo(path).Length;
        var modified = File.GetLastWriteTimeUtc(path);
        string replacement = path + ".replacement";
        using (var writer = new TickFile.Writer(replacement, instrument, includeProvenance: true))
            foreach (var row in rows)
                writer.Write(new Trade(row.Id, row.Time, 101, row.Volume, row.Side, row.Provenance));
        File.Move(replacement, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, modified);
        Assert.Equal(bytes, new FileInfo(path).Length);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        var changed = IbBarStore.GetStamp(_root, "MNQ", Start, Start.AddMinutes(1));
        Assert.Equal(captured.ChangeEpoch, changed.ChangeEpoch); // This fixture bypasses cooperating publication.
        Assert.NotEqual(captured.FilesFingerprint, changed.FilesFingerprint);
        Assert.Equal(101m, TickData.Load(_root, instrument, Start, Start.AddMinutes(1), null, default).Get(0).Price);
    }

    private static void AssertRows(IEnumerable<Trade> rows)
    {
        // OHLC anchors retain their display locations, but unknown aggressor side remains unknown
        // and every anchor becomes available only when its declared source minute closes.
        var actual = rows.ToArray();
        Assert.Equal(new[]
        {
            (Start.AddSeconds(20), 90m, 3m, TradeSide.Buy, false),
            (Start.AddMinutes(1), 100m, 3m, TradeSide.None, true),
            (Start.AddMinutes(1).AddSeconds(15), 99m, 3m, TradeSide.None, true),
            (Start.AddMinutes(1).AddSeconds(30), 102m, 3m, TradeSide.None, true),
            (Start.AddMinutes(1).AddSeconds(45), 101m, 3m, TradeSide.None, true),
        }, actual.Select(row => (row.Time, row.Price, row.Volume, row.Side, row.Id < 0)).ToArray());
        Assert.Equal(101, actual[0].Id);
        Assert.True(actual[0].IsObservedTrade);
        Assert.Equal(DataQuality.Raw, actual[0].Provenance.Quality);
        Assert.Equal(1, actual[0].Provenance.ObservedTradeCount);
        Assert.True(actual[0].HasKnownAvailability);
        Assert.Equal(Start.AddSeconds(20), actual[0].AvailableAt);
        Assert.All(actual, row => Assert.Equal("ib:MNQ", row.Provenance.SourceIdentity));
        Assert.All(actual.Skip(1), row =>
        {
            Assert.False(row.IsObservedTrade);
            Assert.Equal(DataQuality.Synthetic | DataQuality.InferredSide, row.Provenance.Quality);
            Assert.Equal(0, row.Provenance.ObservedTradeCount);
            Assert.Equal(60, row.Provenance.ResolutionSeconds);
            Assert.Equal(0, row.Provenance.ResolutionOffsetSeconds);
            Assert.True(row.HasKnownAvailability);
            Assert.Equal(Start.AddMinutes(2), row.AvailableAt);
            Assert.Equal((Start.AddMinutes(1), Start.AddMinutes(2)), row.Provenance.SourceWindow(row.Time));
        });
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
