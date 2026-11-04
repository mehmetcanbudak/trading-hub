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

    private static void AssertRows(IEnumerable<Trade> rows)
    {
        // Independent full-range values/provenance: one recorded print, then a synthetic minute on the next UTC day.
        Assert.Equal(new[]
        {
            (Start.AddSeconds(20), 90m, 3m, TradeSide.Buy, false),
            (Start.AddMinutes(1), 100m, 3m, TradeSide.Buy, true),
            (Start.AddMinutes(1).AddSeconds(15), 99m, 3m, TradeSide.Sell, true),
            (Start.AddMinutes(1).AddSeconds(30), 102m, 3m, TradeSide.Buy, true),
            (Start.AddMinutes(1).AddSeconds(45), 101m, 3m, TradeSide.Sell, true),
        }, rows.Select(row => (row.Time, row.Price, row.Volume, row.Side, row.Id < 0)).ToArray());
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
