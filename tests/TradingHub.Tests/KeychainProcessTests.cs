using System.Diagnostics;
using TradingHub.App.Services;

namespace TradingHub.Tests;

/// <summary>In-memory process/pipe ownership only; no native credential executable is started.</summary>
public sealed class KeychainProcessTests
{
    private const string Sensitive = "fake-private-account-and-secret";

    [Fact]
    public async Task SilentHungChild_DeadlineKillsTreeAndWaitsForExitAndBothDrains()
    {
        var clock = new ManualClock();
        var child = new FakeProcess();
        await using var store = Store(child, clock);
        var pending = store.GetAsync(Sensitive);
        await child.Started;

        clock.Advance(TimeSpan.FromSeconds(5));
        await child.Killed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(child.KillEntireTree);
        Assert.False(pending.IsCompleted);
        Assert.False(child.Disposed);

        child.Exit(137);
        child.Output.Complete("");
        await child.Output.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.False(child.Disposed);
        child.Error.Complete(Sensitive);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => pending);
        AssertSanitized(error);
        AssertDrainedAndDisposed(child);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExitedChildWithHeldPipe_DeadlineOrCancellationStillOwnsDrainAndDisposeWaits(bool holdOutput, bool useDeadline)
    {
        var clock = new ManualClock();
        var child = new FakeProcess();
        child.Exit(0);
        (holdOutput ? child.Error : child.Output).Complete(Sensitive);
        var store = Store(child, clock);
        using var cancel = new CancellationTokenSource();
        var pending = store.GetAsync(Sensitive, cancel.Token);
        await child.Started;
        await (holdOutput ? child.Error : child.Output).Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(child.ExitObserved);

        if (useDeadline) clock.Advance(TimeSpan.FromSeconds(5));
        else cancel.Cancel();
        await child.Killed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await (holdOutput ? child.Output : child.Error).CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.False(child.Disposed);

        var disposing = store.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetAsync("not admitted"));
        (holdOutput ? child.Output : child.Error).ReleaseCancellationCleanup.TrySetResult();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        AssertSanitized(error);
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pending.IsCompleted);
        AssertDrainedAndDisposed(child);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExitedChildWithHeldPipe_CanceledReadTerminatesWithoutReceivingEof(bool holdOutput, bool useDeadline)
    {
        var clock = new ManualClock();
        var child = new FakeProcess();
        child.Exit(0);
        var held = holdOutput ? child.Output : child.Error;
        held.HoldCancellationCleanup = false;
        (holdOutput ? child.Error : child.Output).Complete(Sensitive);
        await using var store = Store(child, clock);
        using var cancel = new CancellationTokenSource();
        var pending = store.GetAsync(Sensitive, cancel.Token);
        await child.Started;
        if (useDeadline)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            AssertSanitized(await Assert.ThrowsAsync<TimeoutException>(() => pending));
        }
        else
        {
            cancel.Cancel();
            AssertSanitized(await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending));
        }
        Assert.True(child.Killed.Task.IsCompletedSuccessfully);
        Assert.True(held.CancellationObserved.Task.IsCompletedSuccessfully);
        Assert.True(pending.IsCompleted);
        AssertDrainedAndDisposed(child);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task CallerCancellation_SaveWaitsForAllOwnedCleanupBeforeReportingCancellation()
    {
        var child = new FakeProcess();
        await using var store = Store(child);
        using var cancel = new CancellationTokenSource();
        var pending = store.SetAsync(Sensitive, Sensitive, cancel.Token);
        await child.Started;

        cancel.Cancel();
        await child.Killed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(child.KillEntireTree);
        Assert.False(pending.IsCompleted);
        child.Output.Complete(Sensitive);
        child.Error.Complete(Sensitive);
        await Task.WhenAll(child.Output.Drained.Task, child.Error.Drained.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.False(child.Disposed);
        child.Exit(137);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        AssertSanitized(error);
        AssertDrainedAndDisposed(child);
    }

    [Fact]
    public async Task Dispose_ClosesAdmissionCancelsAllChildrenAndWaitsForLastDrain()
    {
        var first = new FakeProcess();
        var second = new FakeProcess();
        int starts = 0;
        var store = new MacKeychainStore("test service", _ => Interlocked.Increment(ref starts) == 1 ? first : second);
        var reading = store.GetAsync(Sensitive);
        var writing = store.SetAsync(Sensitive, Sensitive);
        await Task.WhenAll(first.Started, second.Started);

        var disposing = store.DisposeAsync().AsTask();
        await Task.WhenAll(first.Killed.Task, second.Killed.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disposing.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetAsync("not admitted"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SetAsync("not admitted", Sensitive));
        Assert.Equal(2, starts);

        first.Finish(137, "", "");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.False(disposing.IsCompleted);
        second.Exit(137);
        second.Output.Complete("");
        await second.Output.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disposing.IsCompleted);
        Assert.False(second.Disposed);
        second.Error.Complete("");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        await store.DisposeAsync();
        AssertDrainedAndDisposed(first);
        AssertDrainedAndDisposed(second);
    }

    [Fact]
    public async Task SimultaneouslyFullOutputAndError_AreDrainedBeforeAwaitingEither()
    {
        var child = new FakeProcess();
        child.Output.BeforeRead = child.Error.Entered.Task;
        child.Error.BeforeRead = child.Output.Entered.Task;
        string secret = new('s', 512 * 1024);
        child.Finish(0, secret + "\n", new string('e', 512 * 1024));
        ProcessStartInfo? startInfo = null;
        await using var store = new MacKeychainStore("test service", info => { startInfo = info; return child; });

        Assert.Equal(secret, await store.GetAsync("test account").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(startInfo);
        Assert.Equal("/usr/bin/security", startInfo.FileName);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(new[] { "find-generic-password", "-s", "test service", "-a", "test account", "-w" }, startInfo.ArgumentList);
        Assert.False(child.Killed.Task.IsCompleted);
        AssertDrainedAndDisposed(child);
    }

    [Theory]
    [InlineData("read", 0, false)]
    [InlineData("read", 44, false)]
    [InlineData("read", 1, true)]
    [InlineData("read", 36, true)]
    [InlineData("read", 45, true)]
    [InlineData("save", 0, false)]
    [InlineData("save", 44, true)]
    [InlineData("save", 1, true)]
    [InlineData("delete", 0, false)]
    [InlineData("delete", 44, false)]
    [InlineData("delete", 1, true)]
    public async Task OnlySuccessfulCommandsAndProvenAbsentReadOrDeleteSucceed(string operation, int exitCode, bool fails)
    {
        var child = new FakeProcess();
        child.Finish(exitCode, Sensitive + "\n", Sensitive);
        await using var store = Store(child);
        Task command = operation == "read" ? store.GetAsync(Sensitive) : store.SetAsync(Sensitive, operation == "save" ? Sensitive : null);

        if (fails)
        {
            var error = await Assert.ThrowsAsync<IOException>(() => command);
            AssertSanitized(error);
        }
        else
        {
            await command;
            if (command is Task<string> read)
                Assert.Equal(exitCode == 44 ? "" : Sensitive, await read);
        }
        AssertDrainedAndDisposed(child);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamReadFailure_IsObservableAndSanitizedAfterOtherDrains(bool standardOutput)
    {
        var child = new FakeProcess();
        (standardOutput ? child.Output : child.Error).Fail(new IOException(Sensitive));
        (standardOutput ? child.Error : child.Output).Complete(Sensitive);
        child.Exit(0);
        await using var store = Store(child);

        var error = await Assert.ThrowsAsync<IOException>(() => store.GetAsync(Sensitive));
        AssertSanitized(error);
        AssertDrainedAndDisposed(child);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    public async Task SuccessfulFindWithoutASecret_IsNotMistakenForKnownAbsence(string output)
    {
        var child = new FakeProcess();
        child.Finish(0, output, Sensitive);
        await using var store = Store(child);
        var error = await Assert.ThrowsAsync<IOException>(() => store.GetAsync(Sensitive));
        AssertSanitized(error);
        AssertDrainedAndDisposed(child);
    }

    [Fact]
    public async Task TreeTerminationFailure_AttemptsOwnedRootAndReportsSanitizedFailureAfterAllDrains()
    {
        var child = new FakeProcess { FailTreeKill = true };
        await using var store = Store(child);
        using var cancel = new CancellationTokenSource();
        var pending = store.GetAsync(Sensitive, cancel.Token);
        await child.Started;
        cancel.Cancel();
        await child.RootKillAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.False(child.Disposed);
        child.Finish(137, "", "");
        var error = await Assert.ThrowsAsync<IOException>(() => pending);
        AssertSanitized(error);
        AssertDrainedAndDisposed(child);
    }

    [Fact]
    public async Task ProcessStartFailure_IsObservableWithoutExposingExecutableArgumentsOrInnerError()
    {
        await using var store = new MacKeychainStore("test service", _ => throw new InvalidOperationException(Sensitive), executablePath: "/fake/" + Sensitive);
        var error = await Assert.ThrowsAsync<IOException>(() => store.SetAsync(Sensitive, Sensitive));
        AssertSanitized(error);
    }

    [Fact]
    public async Task ProcessWaitFailure_DoesNotReleaseBeforeBothStreamsDrain()
    {
        var child = new FakeProcess();
        await using var store = Store(child);
        var pending = store.GetAsync(Sensitive);
        await child.Started;
        child.FailExit(new IOException(Sensitive));
        child.Output.Complete(Sensitive);
        await child.Output.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.False(child.Disposed);
        child.Error.Complete(Sensitive);
        var error = await Assert.ThrowsAsync<IOException>(() => pending);
        AssertSanitized(error);
        AssertDrainedAndDisposed(child);
    }

    [Fact]
    public async Task ProcessReleaseFailure_RemainsObservableAndSanitized()
    {
        var child = new FakeProcess { FailDispose = true };
        child.Finish(0, Sensitive, Sensitive);
        await using var store = Store(child);
        var error = await Assert.ThrowsAsync<IOException>(() => store.GetAsync(Sensitive));
        AssertSanitized(error);
        AssertDrainedAndDisposed(child);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacySynchronousWrappers_UseTheSameBoundedProcessOwnership(bool read)
    {
        var clock = new ManualClock();
        var child = new FakeProcess();
        await using var store = Store(child, clock);
        var pending = Task.Run(() => { if (read) store.Get(Sensitive); else store.Set(Sensitive, Sensitive); });
        await child.Started.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        await child.Killed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        child.Finish(137, "", "");
        var error = await Assert.ThrowsAsync<TimeoutException>(() => pending);
        AssertSanitized(error);
        AssertDrainedAndDisposed(child);
    }

    [Fact]
    public async Task AlreadyCanceledOperation_DoesNotStartAChild()
    {
        int starts = 0;
        await using var store = new MacKeychainStore("test service", _ => { starts++; throw new InvalidOperationException(Sensitive); });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync(Sensitive, cancel.Token));
        Assert.Equal(0, starts);
    }

    private static MacKeychainStore Store(FakeProcess child, TimeProvider? clock = null) =>
        new("test service", _ => child, operationTimeout: TimeSpan.FromSeconds(5), timeProvider: clock);

    private static void AssertSanitized(Exception error)
    {
        Assert.DoesNotContain(Sensitive, error.ToString());
        Assert.Null(error.InnerException);
    }

    private static void AssertDrainedAndDisposed(FakeProcess child)
    {
        Assert.True(child.ExitObserved);
        Assert.True(child.Output.Drained.Task.IsCompletedSuccessfully);
        Assert.True(child.Error.Drained.Task.IsCompletedSuccessfully);
        Assert.True(child.Disposed);
    }

    private sealed class FakeProcess : IKeychainProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exitCode;
        public PipeReader Output { get; } = new();
        public PipeReader Error { get; } = new();
        public TextReader StandardOutput => Output;
        public TextReader StandardError => Error;
        public Task Started => Task.WhenAll(Output.Entered.Task, Error.Entered.Task);
        public TaskCompletionSource Killed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RootKillAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool KillEntireTree { get; private set; }
        public bool ExitObserved { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailDispose { get; init; }
        public bool FailTreeKill { get; init; }
        public int ExitCode => _exitCode;
        public async Task WaitForExitAsync()
        {
            try { await _exit.Task.ConfigureAwait(false); }
            finally { ExitObserved = true; }
        }
        public void Exit(int exitCode) { _exitCode = exitCode; _exit.TrySetResult(); }
        public void FailExit(Exception error) => _exit.TrySetException(error);
        public void Finish(int exitCode, string output, string error)
        {
            Output.Complete(output);
            Error.Complete(error);
            Exit(exitCode);
        }
        public void Kill(bool entireProcessTree)
        {
            KillEntireTree = entireProcessTree;
            Killed.TrySetResult();
            if (entireProcessTree && FailTreeKill) throw new IOException(Sensitive);
            if (!entireProcessTree) RootKillAttempted.TrySetResult();
        }
        public void Dispose()
        {
            Disposed = true;
            if (FailDispose) throw new IOException(Sensitive);
        }
    }

    private sealed class PipeReader : TextReader
    {
        private readonly TaskCompletionSource<string> _content = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancellationCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldCancellationCleanup { get; set; } = true;
        public Task BeforeRead { get; set; } = Task.CompletedTask;
        public void Complete(string value)
        {
            _content.TrySetResult(value);
            ReleaseCancellationCleanup.TrySetResult();
        }
        public void Fail(Exception error) => _content.TrySetException(error);
        public override Task<string> ReadToEndAsync() => ReadToEndAsync(CancellationToken.None);
        public override async Task<string> ReadToEndAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await BeforeRead.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await _content.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ObserveCancellationAsync().ConfigureAwait(false);
                throw;
            }
            finally { Drained.TrySetResult(); }
        }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await BeforeRead.WaitAsync(cancellationToken).ConfigureAwait(false);
                string content = await _content.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                int count = Math.Min(buffer.Length, content.Length - _offset);
                content.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                if (count == 0) Drained.TrySetResult();
                return count;
            }
            catch (OperationCanceledException)
            {
                await ObserveCancellationAsync().ConfigureAwait(false);
                Drained.TrySetResult();
                throw;
            }
            catch
            {
                Drained.TrySetResult();
                throw;
            }
        }
        private async Task ObserveCancellationAsync()
        {
            CancellationObserved.TrySetResult();
            if (HoldCancellationCleanup) await ReleaseCancellationCleanup.Task.ConfigureAwait(false);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        private long _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_timers) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan time)
        {
            Interlocked.Add(ref _ticks, time.Ticks);
            ManualTimer[] timers;
            lock (_timers) timers = _timers.ToArray();
            foreach (var timer in timers) timer.Fire(Interlocked.Read(ref _ticks));
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (this)
                {
                    if (_disposed) return false;
                    _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : Interlocked.Read(ref clock._ticks) + dueTime.Ticks;
                    return true;
                }
            }
            public void Fire(long ticks)
            {
                lock (this)
                {
                    if (_disposed || _due > ticks) return;
                    _due = long.MaxValue;
                }
                callback(state);
            }
            public void Dispose() { lock (this) _disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
