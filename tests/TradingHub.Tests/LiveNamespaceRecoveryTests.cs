using TradingHub.Common;
using TradingHub.Strategies;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class LiveNamespaceRecoveryTests
{
    [Theory]
    [InlineData("generation")]
    [InlineData("intent")]
    public void A_post_publication_namespace_failure_remains_retryable_without_double_booking(string stage)
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-namespace-recovery-" + Guid.NewGuid().ToString("N"));
        string previous = LiveStore.Root;
        Directory.CreateDirectory(root);
        LiveStore.Root = root;
        const string key = "namespace-recovery|test:TEST|sim";
        try
        {
            string path = LiveStore.LedgerFile(key);
            bool failed = false;
            var recorder = new DurabilityRecorder
            {
                FailWhen = step =>
                {
                    // Fail the first namespace confirmation after the stage's publication (.id rename or .wal rename).
                    if (failed || !step.StartsWith("sync:", StringComparison.Ordinal) ||
                        !File.Exists(path + (stage == "generation" ? ".id" : ".wal"))) return false;
                    failed = true;
                    return true;
                },
            };
            var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            string? error;
            using (recorder.Install())
                error = LiveStore.AppendFill(key, new LiveFill(time, true, 2m, 100m),
                    file => new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0));
            Assert.NotNull(error);
            Assert.Equal(2m, LiveStore.LoadLedger(key, 10m).Position);
            Assert.Null(LiveStore.SavePendingLedgerEvents(key));
            Assert.Null(LiveStore.AppendFill(key, new LiveFill(time.AddMinutes(1), false, 2m, 105m)));
            var reloaded = LiveStore.LoadLedger(key, 10m);
            Assert.Equal(0m, reloaded.Position);
            Assert.Equal(100m, reloaded.Realized); // (105 - 100) * 2 * 10, once; these old-format values remain estimates.
            Assert.Equal(100m, Assert.Single(reloaded.Trades).NetPnl);
            Assert.Equal(0, LiveStore.PendingLedgerEvents(key));
        }
        finally
        {
            LiveStore.Root = previous;
            Directory.Delete(root, recursive: true);
        }
    }
}
