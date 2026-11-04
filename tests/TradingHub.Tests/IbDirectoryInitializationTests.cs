using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class IbDirectoryInitializationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-ib-directory-init-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private static readonly DateTime Start = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    public IbDirectoryInitializationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void An_import_retries_failed_managed_parent_sync_before_mutating_existing_history()
    {
        var instrument = IbBarImport.InstrumentFor("MNQ");
        string history = TickFile.DayPath(Data, "ib", "MNQ", DateOnly.FromDateTime(Start));
        // Seed real owned history without initializing any coordinator directory.
        using (var writer = new TickFile.Writer(history, instrument))
            writer.Write(new Trade(101, Start.AddSeconds(20), 90, 3, TradeSide.Buy));
        byte[] original = File.ReadAllBytes(history);
        string csv = Path.Combine(_root, "csv");
        Directory.CreateDirectory(csv);
        long unix = new DateTimeOffset(Start).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(csv, "MNQ.csv"), $"time,open,high,low,close,volume\n{unix},100,102,99,101,12\n");

        string managedParent = Path.Combine(Data, ".ib-history");
        string managedSymbol = Path.Combine(managedParent, "MNQ");
        Directory.CreateDirectory(managedParent); // Existing ancestor; only the symbol directory is created by acquisition.
        string failingTail = Path.DirectorySeparatorChar + Path.Combine(Path.GetFileName(_root), "data", ".ib-history");
        bool faultActive = true;
        using var watchdog = new CancellationTokenSource(Watchdog);

        Assert.Throws<IOException>(Import);
        Assert.True(Directory.Exists(managedSymbol)); // The failure was after mkdir, not before the managed child existed.
        Assert.Equal(original, File.ReadAllBytes(history));

        // Keep precisely the same parent-sync fault active. Existence cannot stand in for completed synchronization.
        var retryFailure = Record.Exception(Import);
        byte[] afterRetry = File.ReadAllBytes(history);
        Assert.Equal(original, afterRetry);
        Assert.IsType<IOException>(retryFailure);

        faultActive = false;
        Import();
        var repaired = HistoricalData.Read(Data, "ib", "MNQ", Start, Start.AddMinutes(1)).ToList();
        Assert.Equal(new[] { 100m, 99m, 102m, 101m }, repaired.Select(t => t.Price).ToArray());
        Assert.All(repaired, trade => Assert.True(trade.Id < 0));
        Assert.Equal(12m, repaired.Sum(t => t.Volume));

        bool SyncDirectory(string directory)
        {
            // Match this explicit owned parent across /var and /private/var aliases, never an unrelated backup sync.
            if (faultActive && directory.EndsWith(failingTail, StringComparison.Ordinal) && Directory.Exists(managedSymbol))
                throw new IOException($"Owned fixture parent synchronization failed: {directory}");
            return StorageSync.SyncDirectory(directory);
        }

        void Import()
        {
            var preview = IbBarImport.PreviewFolder(csv, Data, "MNQ", watchdog.Token, SyncDirectory);
            IbBarImport.Commit(preview, Data, IbBarImport.ImportPolicy.ReplaceCoveredMinutes, null, watchdog.Token, null, SyncDirectory);
        }
    }
}
