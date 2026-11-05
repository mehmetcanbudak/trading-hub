using TradingHub.Strategies;

namespace TradingHub.App.Services;

internal sealed partial class LiveCenter
{
    internal const string AwaitingIdentityMessage = "Awaiting verified trading account identity; saved history and protection have not been adopted.";
    private readonly object _startsGate = new();
    private readonly HashSet<LiveStart> _starts = new();
    private bool _startsClosed;

    internal LiveStart? BeginStart(string? savedKey = null)
    {
        lock (_startsGate)
        {
            if (_startsClosed || _svc.IsShuttingDown) return null;
            var start = new LiveStart(this, savedKey);
            _starts.Add(start);
            return start;
        }
    }

    internal async Task<bool> WaitForAccountIdentityAsync(LiveStrategyRunner runner, CancellationToken cancellationToken)
    {
        bool notified = false;
        while (!_svc.IsShuttingDown && !cancellationToken.IsCancellationRequested)
        {
            if (runner.TryPrepareIdentity(out _)) return true;
            if (!notified)
            {
                Notify(AwaitingIdentityMessage);
                notified = true;
            }
            try { await Task.Delay(100, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        }
        return false;
    }

    private void CancelStartsFor(string key)
    {
        LiveStart[] pending;
        lock (_startsGate) pending = _starts.Where(s => s.SavedKey == key).ToArray();
        foreach (var start in pending) start.Cancel();
        _svc.RunningAtLastClose.RemoveAll(info => info.Key == key);
    }

    internal async Task ShutdownStartsAsync()
    {
        LiveStart[] pending;
        lock (_startsGate)
        {
            _startsClosed = true;
            pending = _starts.ToArray();
        }
        foreach (var start in pending) start.Cancel();
        await Task.WhenAll(pending.Select(s => s.Completion));
    }

    internal sealed class LiveStart : IDisposable
    {
        private readonly LiveCenter _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        internal LiveStart(LiveCenter owner, string? savedKey) { _owner = owner; SavedKey = savedKey; }
        internal string? SavedKey { get; }
        internal CancellationToken Token => _cancellation.Token;
        internal Task Completion => _completion.Task;
        internal void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cancellation.Dispose();
            lock (_owner._startsGate) _owner._starts.Remove(this);
            _completion.TrySetResult();
        }
    }
}
