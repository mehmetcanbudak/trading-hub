using System.Runtime.InteropServices;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class TickRecorderLeaseTests
{
    [MacRecorderFact]
    public async Task Bounded_dispose_keeps_real_writer_lease_until_blocked_filesystem_writer_finishes()
    {
        string root = Path.Combine(Path.GetTempPath(), "recorder-drain-lease-" + Guid.NewGuid().ToString("N"));
        var instrument = new Instrument { FeedId = "lease-fixture", Symbol = "LEASE", TickSize = 1, VolumeStep = 1 };
        var time = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        string path = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(time));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Equal(0, MakeFifo(path, 0x180)); // Owner-only Unix FIFO; no reader means the real writer cannot open yet.
        TickRecorder? recorder = null;
        try
        {
            recorder = new TickRecorder(root, instrument);
            Assert.True(recorder.IsActive);
            recorder.WriteLive(new[] { new Trade(1, time, 100, 1, TradeSide.Buy) });
            try
            {
                recorder.Dispose(); // Bounded wait returns while the filesystem writer is deliberately blocked.
                Assert.True(recorder.IsActive);
                using var blockedLease = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol);
                Assert.Null(blockedLease);
                using var contender = new TickRecorder(root, instrument);
                Assert.False(contender.IsActive);
            }
            finally
            {
                // A real read/write FIFO endpoint releases the blocked open. The tiny record fits the pipe buffer.
                // This is an OS-backed filesystem fixture, not a production thread/timing test hook.
                using var reader = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (recorder.IsActive) await Task.Delay(5, timeout.Token);
            }
            using var releasedLease = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol);
            Assert.NotNull(releasedLease);
        }
        finally
        {
            recorder?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}

public sealed class MacRecorderFactAttribute : FactAttribute
{
    public MacRecorderFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "macOS filesystem-backed recorder lifecycle proof";
    }
}
