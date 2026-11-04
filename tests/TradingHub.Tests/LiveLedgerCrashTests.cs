using System.Diagnostics;
using System.Text.Json;
using TradingHub.Strategies;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class LiveLedgerCrashTests : IDisposable
{
    private const string Key = "TradingHub.Tests.Strategy|test:TEST|Cuenta simulada";
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly LiveFill Opening = new(T0, true, 2, 100);
    private static readonly LiveFill FirstClose = new(T0.AddMinutes(1), false, 1, 110);
    private static readonly LiveFill SecondClose = new(T0.AddMinutes(2), false, 2, 90);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-ledger-crash-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = LiveStore.Root;
    // These are deadlock watchdogs, never timing-based evidence that a checkpoint or a commit happened.
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    public LiveLedgerCrashTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".ledger-fixture-root"), "owned temporary ledger fixture");
        LiveStore.Root = Path.Combine(_root, "Live");
    }

    public void Dispose()
    {
        try { LiveStore.DeleteFills(Key); }
        finally
        {
            LiveStore.Root = _previousRoot;
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("IntentTemporary", false)]
    [InlineData("IntentPublished", true)]
    [InlineData("PartialWrite", true)]
    [InlineData("MainFlushed", true)]
    [InlineData("MainClosed", true)]
    [InlineData("Acknowledged", true)]
    public async Task A_fresh_process_recovers_every_published_intent_once_after_an_actual_writer_crash(string checkpoint, bool durable)
    {
        Assert.Null(LiveStore.AppendFill(Key, Opening));
        string file = LiveStore.LedgerFile(Key);
        File.AppendAllText(file, "{corrupt committed line}\n");
        byte[] committedPrefix = File.ReadAllBytes(file);
        await CrashAt(checkpoint);

        var recovered = await ReadInFreshProcess();
        Assert.Equal(durable ? new[] { Opening, FirstClose } : new[] { Opening }, recovered.Fills);
        Assert.Equal(durable ? 1m : 2m, recovered.Position);
        Assert.Equal(100m, recovered.AveragePrice);
        Assert.Equal(durable ? 10m : 0m, recovered.Realized);
        Assert.Equal(durable ? -20m : -40m, recovered.OpenPnL);
        Assert.Equal(1, recovered.SkippedLines);
        Assert.Equal(durable && checkpoint != "Acknowledged" ? 1 : 0, recovered.Pending);
        var repeated = await ReadInFreshProcess();
        Assert.Equal(recovered.Fills, repeated.Fills);
        Assert.Equal(recovered.Position, repeated.Position);

        var appended = await ReadInFreshProcess("append");
        Assert.Equal(durable ? new[] { Opening, FirstClose, SecondClose } : new[] { Opening, SecondClose }, appended.Fills);
        Assert.Equal(durable ? -1m : 0m, appended.Position);
        Assert.Equal(durable ? 90m : 0m, appended.AveragePrice);
        Assert.Equal(durable ? 0m : -20m, appended.Realized);
        Assert.Equal(durable ? 10m : 0m, appended.OpenPnL);
        Assert.Equal(1, appended.SkippedLines);
        Assert.Equal(0, appended.Pending);
        Assert.Equal(committedPrefix, File.ReadAllBytes(file)[..committedPrefix.Length]);
        var restartedAgain = await ReadInFreshProcess();
        Assert.Equal(appended.Fills, restartedAgain.Fills);
        Assert.Equal(appended.Position, restartedAgain.Position);
    }

    [Fact]
    public async Task A_restarted_flat_runner_can_explicitly_resume_by_saving_its_closing_intent_without_new_fills()
    {
        Assert.Null(LiveStore.AppendFill(Key, Opening));
        await CrashAt("PartialWrite", "flat-write");
        var closing = new LiveFill(T0.AddMinutes(1), false, 2, 110);
        var recovered = await ReadInFreshProcess();
        Assert.Equal(new[] { Opening, closing }, recovered.Fills);
        Assert.Equal(0m, recovered.Position);
        Assert.Equal(20m, recovered.Realized);
        Assert.Equal(1, recovered.Pending);

        var resumed = await ReadInFreshProcess("resume-flat");
        Assert.True(resumed.InitiallyPaused);
        Assert.False(resumed.Paused);
        Assert.Equal(recovered.Fills, resumed.Fills);
        Assert.Equal(0m, resumed.Position);
        Assert.Equal(20m, resumed.Realized);
        Assert.Equal(0, resumed.Pending);
        var savedAgain = await ReadInFreshProcess("save-pending");
        Assert.Equal(recovered.Fills, savedAgain.Fills);
        Assert.Equal(20m, savedAgain.Realized);
        Assert.Equal(0, savedAgain.Pending);
    }

    [Fact]
    public async Task A_partial_queued_append_with_failed_rollback_recovers_all_journaled_fills_in_a_fresh_process()
    {
        Assert.Null(LiveStore.AppendFill(Key, Opening));
        string file = LiveStore.LedgerFile(Key);
        File.AppendAllText(file, "{corrupt committed line}\n");
        byte[] prefix = File.ReadAllBytes(file);
        await CrashAt("unused", "batch-crash");
        var recovered = await ReadInFreshProcess();
        Assert.Equal(new[] { Opening, FirstClose, SecondClose }, recovered.Fills);
        Assert.Equal(-1m, recovered.Position);
        Assert.Equal(90m, recovered.AveragePrice);
        Assert.Equal(0m, recovered.Realized);
        Assert.Equal(10m, recovered.OpenPnL);
        Assert.Equal(1, recovered.SkippedLines);
        Assert.Equal(1, recovered.Pending);
        Assert.Equal(recovered.Fills, (await ReadInFreshProcess()).Fills);
        var last = new LiveFill(T0.AddMinutes(3), true, 1, 80);
        Assert.Null(LiveStore.AppendFill(Key, last));
        var saved = await ReadInFreshProcess();
        Assert.Equal(new[] { Opening, FirstClose, SecondClose, last }, saved.Fills);
        Assert.Equal(0m, saved.Position);
        Assert.Equal(10m, saved.Realized);
        Assert.Equal(0, saved.Pending);
        Assert.Equal(prefix, File.ReadAllBytes(file)[..prefix.Length]);
    }

    [Fact]
    public async Task Deleting_a_crashed_writers_history_cannot_resurrect_its_durable_intent()
    {
        Assert.Null(LiveStore.AppendFill(Key, Opening));
        await CrashAt("PartialWrite");
        var deleted = await ReadInFreshProcess("delete");
        Assert.Empty(deleted.Fills);
        Assert.Equal(0m, deleted.Position);
        Assert.Equal(0, deleted.Pending);
        var afterRestart = await ReadInFreshProcess("append");
        Assert.Equal(new[] { SecondClose }, afterRestart.Fills);
        Assert.Equal(-2m, afterRestart.Position);
        Assert.Equal(0m, afterRestart.Realized);
        Assert.Equal(afterRestart.Fills, (await ReadInFreshProcess()).Fills);
    }

    [Fact]
    public async Task A_damaged_durable_intent_fails_closed_in_a_new_process_and_leaves_history_untouched()
    {
        Assert.Null(LiveStore.AppendFill(Key, Opening));
        await CrashAt("IntentPublished");
        string file = LiveStore.LedgerFile(Key);
        byte[] committed = File.ReadAllBytes(file);
        File.WriteAllText(file + ".wal", "{damaged durable intent");
        using var reader = Start("read");
        Assert.Equal("started", await reader.StandardOutput.ReadLineAsync().WaitAsync(Watchdog));
        await reader.WaitForExitAsync().WaitAsync(Watchdog);
        Assert.Equal(1, reader.ExitCode);
        Assert.Contains("Keep the strategy stopped", await reader.StandardError.ReadToEndAsync());
        Assert.NotNull(LiveStore.AppendFill(Key, SecondClose));
        Assert.Throws<IOException>(() => LiveStore.LoadLedger(Key, 1));
        Assert.Equal(committed, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task A_live_writer_and_independent_reader_preserve_one_complete_recovery_view()
    {
        Assert.Null(LiveStore.AppendFill(Key, Opening));
        using var writer = Start("write", "PartialWrite");
        Process? reader = null;
        try
        {
            Assert.Equal("checkpoint", await writer.StandardOutput.ReadLineAsync().WaitAsync(Watchdog));
            reader = Start("read");
            Assert.Equal("started", await reader.StandardOutput.ReadLineAsync().WaitAsync(Watchdog));
            await writer.StandardInput.WriteLineAsync("continue");
            await writer.StandardInput.FlushAsync();
            Assert.Equal("acknowledged", await writer.StandardOutput.ReadLineAsync().WaitAsync(Watchdog));
            await writer.WaitForExitAsync().WaitAsync(Watchdog);
            Assert.True(writer.ExitCode == 0, await writer.StandardError.ReadToEndAsync());
            var snapshot = JsonSerializer.Deserialize<Snapshot>((await reader.StandardOutput.ReadLineAsync().WaitAsync(Watchdog))!)!;
            await reader.WaitForExitAsync().WaitAsync(Watchdog);
            Assert.True(reader.ExitCode == 0, await reader.StandardError.ReadToEndAsync());
            Assert.Equal(new[] { Opening, FirstClose }, snapshot.Fills);
            Assert.Equal(1m, snapshot.Position);
            Assert.Equal(10m, snapshot.Realized);
            Assert.Equal(0, snapshot.Pending);
            Assert.Equal(new[] { Opening, FirstClose, SecondClose }, (await ReadInFreshProcess("append")).Fills);
        }
        finally
        {
            if (!writer.HasExited) { writer.Kill(entireProcessTree: true); await writer.WaitForExitAsync().WaitAsync(Watchdog); }
            if (reader != null)
            {
                if (!reader.HasExited) { reader.Kill(entireProcessTree: true); await reader.WaitForExitAsync().WaitAsync(Watchdog); }
                reader.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_second_process_can_finish_an_intent_without_the_original_process_accounting_it_twice()
    {
        Assert.Null(LiveStore.AppendFill(Key, Opening));
        string file = LiveStore.LedgerFile(Key);
        Assert.NotNull(LiveStore.AppendFill(Key, FirstClose, path => path == file
            ? new RejectingStream(path)
            : new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0)));
        Assert.Equal(new[] { Opening, FirstClose }, LiveStore.LoadLedger(Key, 1).Fills);
        Assert.Equal(new[] { Opening, FirstClose, SecondClose }, (await ReadInFreshProcess("append")).Fills);
        var reconciled = LiveStore.LoadLedger(Key, 1);
        Assert.Equal(new[] { Opening, FirstClose, SecondClose }, reconciled.Fills);
        Assert.Equal(-1m, reconciled.Position);
        Assert.Equal(0, LiveStore.PendingFills(Key));
        // The same content is still a new fill when explicitly submitted again.
        Assert.Null(LiveStore.AppendFill(Key, FirstClose));
        Assert.Equal(new[] { Opening, FirstClose, SecondClose, FirstClose }, (await ReadInFreshProcess()).Fills);
    }

    private async Task CrashAt(string checkpoint, string operation = "write")
    {
        var elapsed = Stopwatch.StartNew();
        using var writer = Start(operation, checkpoint);
        var stderrText = new System.Text.StringBuilder();
        bool stderrTruncated = false;
        Task stderr = DrainWriterError();
        string? line = null;
        DateTime? startedAt = null;
        string state = "not observed";
        string sample = "Native sample not requested.";
        bool checkpointTimedOut = false;
        Exception? failure = null;
        try
        {
            startedAt = writer.StartTime;
            try { line = await writer.StandardOutput.ReadLineAsync().WaitAsync(Watchdog); }
            catch (TimeoutException) { checkpointTimedOut = true; throw; }
            Assert.Equal("checkpoint", line);
        }
        catch (Exception ex)
        {
            failure = ex;
            state = writer.HasExited ? $"exited with code {writer.ExitCode}" : "still running";
        }

        // A sample explains a failed deadline; it must never turn a late checkpoint into a pass.
        if (checkpointTimedOut && OperatingSystem.IsMacOS() && !writer.HasExited && startedAt.HasValue)
        {
            try { sample = await SampleWriter(startedAt.Value); }
            catch (Exception ex)
            {
                failure = new AggregateException(failure!, ex);
                sample = "Native sampling failed; capture diagnostics are retained in the inner exception.";
            }
        }
        try
        {
            if (!writer.HasExited) writer.Kill(entireProcessTree: true);
            await writer.WaitForExitAsync().WaitAsync(Watchdog);
        }
        catch (Exception ex)
        {
            failure = failure == null ? ex : new AggregateException(failure, ex);
        }
        try { await stderr.WaitAsync(Watchdog); }
        catch (Exception ex)
        {
            failure = failure == null ? ex : new AggregateException(failure, ex);
        }
        if (failure == null)
        {
            try { Assert.NotEqual(0, writer.ExitCode); }
            catch (Exception ex)
            {
                failure = ex;
                state = $"exited with code {writer.ExitCode}";
            }
        }
        if (failure != null)
        {
            string error;
            lock (stderrText) error = stderrText.ToString() + (stderrTruncated ? "\n<stderr limited to 65,536 characters>" : "");
            throw new InvalidOperationException(
                $"Ledger writer '{operation}' checkpoint '{checkpoint}' (PID {writer.Id}, started {startedAt:O}) failed after {elapsed.Elapsed}; child was {state}.\n" +
                $"stdout checkpoint: {line ?? "<none>"}\nstderr:\n{error}\nnative sample:\n{sample}", failure);
        }

        async Task DrainWriterError()
        {
            var buffer = new char[4096];
            int count;
            while ((count = await writer.StandardError.ReadAsync(buffer.AsMemory())) != 0)
            {
                lock (stderrText)
                {
                    int keep = Math.Min(count, 65_536 - stderrText.Length);
                    stderrText.Append(buffer, 0, keep);
                    stderrTruncated |= keep != count;
                }
            }
        }

        async Task<string> SampleWriter(DateTime expectedStart)
        {
            // Recheck this exact child identity immediately before capture; never find a process by name.
            using var live = Process.GetProcessById(writer.Id);
            if (writer.HasExited || live.StartTime != expectedStart)
                throw new InvalidOperationException("The owned writer exited or changed identity before sampling.");
            string reportPath = Path.Combine(_root, $"writer-{writer.Id}.sample.txt");
            var start = new ProcessStartInfo("/usr/bin/sample")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = _root,
            };
            start.ArgumentList.Add(writer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("1");
            start.ArgumentList.Add("-mayDie");
            start.ArgumentList.Add("-file");
            start.ArgumentList.Add(reportPath);
            using var sampler = Process.Start(start) ?? throw new IOException("Could not start the native writer sampler.");
            Task<string> output = sampler.StandardOutput.ReadToEndAsync();
            Task<string> error = sampler.StandardError.ReadToEndAsync();
            var captureLimit = TimeSpan.FromSeconds(5);
            Exception? captureFailure = null;
            try
            {
                await sampler.WaitForExitAsync().WaitAsync(captureLimit);
                if (sampler.ExitCode != 0) throw new IOException($"Native sampler exited with code {sampler.ExitCode}.");
            }
            catch (Exception ex) { captureFailure = ex; }
            try
            {
                if (!sampler.HasExited) sampler.Kill(entireProcessTree: true);
                await sampler.WaitForExitAsync().WaitAsync(captureLimit);
            }
            catch (Exception ex)
            {
                captureFailure = captureFailure == null ? ex : new AggregateException(captureFailure, ex);
            }
            string helperOutput = "<sampler pipes incomplete>";
            try
            {
                await Task.WhenAll(output, error).WaitAsync(captureLimit);
                string stdout = await output, stderrOutput = await error;
                helperOutput = $"stdout: {stdout[..Math.Min(stdout.Length, 4096)]}\nstderr: {stderrOutput[..Math.Min(stderrOutput.Length, 4096)]}";
            }
            catch (Exception ex)
            {
                captureFailure = captureFailure == null ? ex : new AggregateException(captureFailure, ex);
            }
            string report = "<no native report>";
            try
            {
                using var reader = new StreamReader(reportPath);
                var buffer = new char[65_536];
                int count = await reader.ReadBlockAsync(buffer.AsMemory()).AsTask().WaitAsync(captureLimit);
                report = new string(buffer, 0, count) + (count == buffer.Length ? "\n<sample limited to 65,536 characters>" : "");
            }
            catch (Exception ex)
            {
                captureFailure = captureFailure == null ? ex : new AggregateException(captureFailure, ex);
            }
            string diagnostic = $"Owned writer PID {writer.Id}, started {expectedStart:O}; 1-second native sample.\n{helperOutput}\n{report}";
            if (captureFailure != null) throw new InvalidOperationException(diagnostic, captureFailure);
            return diagnostic;
        }
    }

    private async Task<Snapshot> ReadInFreshProcess(string operation = "read")
    {
        using var reader = Start(operation);
        // Drain from launch, not after exit: fixture diagnostics must never fill a redirected pipe.
        Task<string> stderr = reader.StandardError.ReadToEndAsync();
        var elapsed = Stopwatch.StartNew();
        string phase = "startup";
        string? started = null, json = null;
        Snapshot? snapshot = null;
        Exception? failure = null;
        string state = "not observed";
        try
        {
            started = await reader.StandardOutput.ReadLineAsync().WaitAsync(Watchdog);
            Assert.Equal("started", started);
            phase = "snapshot";
            json = await reader.StandardOutput.ReadLineAsync().WaitAsync(Watchdog);
            phase = "exit";
            await reader.WaitForExitAsync().WaitAsync(Watchdog);
            Assert.True(reader.ExitCode == 0, $"Fixture exited with code {reader.ExitCode}.");
            phase = "snapshot decoding";
            snapshot = JsonSerializer.Deserialize<Snapshot>(json!)!;
        }
        catch (Exception ex)
        {
            failure = ex;
            state = reader.HasExited ? $"exited with code {reader.ExitCode}" : "still running";
        }

        try
        {
            if (!reader.HasExited) reader.Kill(entireProcessTree: true);
            await reader.WaitForExitAsync().WaitAsync(Watchdog);
        }
        catch (Exception ex)
        {
            failure = failure == null ? ex : new AggregateException(failure, ex);
        }

        string error;
        try { error = await stderr.WaitAsync(Watchdog); }
        catch (Exception ex)
        {
            failure = failure == null ? ex : new AggregateException(failure, ex);
            error = "The fixture stderr pipe did not finish draining.";
        }
        if (failure != null)
            throw new InvalidOperationException(
                $"Ledger fixture '{operation}' (PID {reader.Id}) failed during {phase} after {elapsed.Elapsed}; child was {state}.\n" +
                $"stdout startup: {started ?? "<none>"}\nstdout snapshot: {json ?? "<none>"}\nstderr:\n{error}", failure);
        return snapshot!;
    }

    private Process Start(string operation, string? checkpoint = null)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "LedgerCrashFixture", "TradingHub.LedgerCrashFixture.dll");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _root,
        };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add(operation);
        if (checkpoint != null) start.ArgumentList.Add(checkpoint);
        foreach (string store in new[] { "DATA", "STRATEGIES", "INDICATORS", "BACKTESTS", "LIVE" })
            start.Environment["TRADINGHUB_" + store + "_DIR"] = Path.Combine(_root, store.ToLowerInvariant());
        start.Environment["TRADINGHUB_KEYCHAIN_FILE"] = Path.Combine(_root, "fake-keychain.json");
        start.Environment["TRADINGHUB_SLEEP_GUARD"] = "off";
        return Process.Start(start)!;
    }

    private sealed record Snapshot(List<LiveFill> Fills, decimal Position, decimal AveragePrice, decimal Realized, decimal OpenPnL, int SkippedLines, int Pending, bool? InitiallyPaused = null, bool? Paused = null);

    private sealed class RejectingStream(string path) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set => _file.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _file.Read(buffer);
        public override int ReadByte() => _file.ReadByte();
        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
        public override void SetLength(long value) => _file.SetLength(value);
        public override void Flush() => _file.Flush(flushToDisk: true);
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Injected write failure");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Injected write failure");
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
    }
}
