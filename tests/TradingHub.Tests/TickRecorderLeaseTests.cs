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
        var queued = new Trade(2, time.AddDays(1), 101, 3, TradeSide.Sell);
        var queuedDay = DateOnly.FromDateTime(queued.Time);
        string queuedPath = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, queuedDay);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Equal(0, MakeFifo(path, 0x180)); // Owner-only Unix FIFO; no reader means the real writer cannot open yet.
        TickRecorder? recorder = null;
        try
        {
            recorder = new TickRecorder(root, instrument);
            Assert.True(recorder.IsActive);
            recorder.WriteLive(new[] { new Trade(1, time, 100, 1, TradeSide.Buy) });
            recorder.WriteHistory(new[] { queued });
            try
            {
                recorder.Dispose(); // Bounded wait returns while the filesystem writer is deliberately blocked.
                Assert.True(recorder.IsActive);
                using var blockedLease = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol);
                Assert.Null(blockedLease);
                using var contender = new TickRecorder(root, instrument);
                Assert.False(contender.IsActive);
                Assert.Throws<IOException>(() => HistoricalData.DeleteDays(root, instrument.FeedId, instrument.Symbol,
                    new[] { queuedDay, DateOnly.FromDateTime(time) }));
                Assert.Throws<IOException>(() => HistoricalData.WriteDay(root, instrument, queuedDay,
                    new[] { new Trade(3, queued.Time, 999, 1, TradeSide.Buy) }, replace: true));
                Assert.False(File.Exists(queuedPath));
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
            Assert.Null(recorder.Error);
            Assert.Equal(queued, Assert.Single(TickFile.Read(queuedPath, out int dropped, requireComplete: true)));
            Assert.Equal(0, dropped);
        }
        finally
        {
            recorder?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [MacRecorderFact]
    public async Task Deleting_recorded_day_refuses_until_recorder_finishes_and_preserves_accepted_tick()
    {
        string root = Path.Combine(Path.GetTempPath(), "recorder-delete-lease-" + Guid.NewGuid().ToString("N"));
        var instrument = new Instrument { FeedId = "lease-fixture", Symbol = "LEASE", TickSize = 1, VolumeStep = 1 };
        var time = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var day = DateOnly.FromDateTime(time);
        string path = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, day);
        var accepted = new Trade(1, time, 100, 2, TradeSide.Buy);
        try
        {
            using var recorder = new TickRecorder(root, instrument);
            Task<Exception?>? deletion = null;
            try
            {
                Assert.True(recorder.IsActive);
                recorder.WriteLive(new[] { accepted });
                using (var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    while (true)
                    {
                        flushTimeout.Token.ThrowIfCancellationRequested();
                        Assert.Null(recorder.Error);
                        Assert.True(recorder.IsActive);
                        var persisted = TickFile.ReadAppendPrefix(path, out int dropped, instrument.TickSize);
                        Assert.Equal(0, dropped);
                        if (persisted.Count > 0)
                        {
                            Assert.Equal(accepted, Assert.Single(persisted));
                            break;
                        }
                        await Task.Delay(5, flushTimeout.Token);
                    }
                }

                deletion = Task.Run<Exception?>(() => Record.Exception(() =>
                    HistoricalData.DeleteDays(root, instrument.FeedId, instrument.Symbol, new[] { day })));
                var error = await deletion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsType<IOException>(error);
                Assert.True(recorder.IsActive);
                Assert.Null(recorder.Error);
                Assert.Equal(accepted, Assert.Single(TickFile.ReadAppendPrefix(path, out int droppedAfterRefusal, instrument.TickSize)));
                Assert.Equal(0, droppedAfterRefusal);
            }
            finally
            {
                recorder.Dispose();
                using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (recorder.IsActive) await Task.Delay(5, drainTimeout.Token);
                if (deletion != null) await deletion.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.False(recorder.IsActive);
            Assert.Null(recorder.Error);
            Assert.Equal(accepted, Assert.Single(TickFile.Read(path, out int droppedAfterDrain, requireComplete: true)));
            Assert.Equal(0, droppedAfterDrain);
            HistoricalData.DeleteDays(root, instrument.FeedId, instrument.Symbol, new[] { day });
            Assert.Empty(TickFile.AvailableDays(root, instrument.FeedId, instrument.Symbol));
            Assert.Empty(TickFile.Read(path, out int droppedAfterDelete, requireComplete: true));
            Assert.Equal(0, droppedAfterDelete);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
