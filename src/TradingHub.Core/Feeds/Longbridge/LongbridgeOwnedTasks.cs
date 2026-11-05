namespace TradingHub.Feeds.Longbridge;

/// <summary>
/// Closed admission plus terminal ownership, including synchronous callback/startup sections before an async
/// method returns its task. Caller-visible failures stay on their original tasks; closing waits for completion,
/// not merely cancellation. The enclosing owner disposes the cancellation source after this barrier completes.
/// </summary>
internal sealed class LongbridgeOwnedTasks(string name, CancellationTokenSource cancellation, bool cancelClosedAdmission = false)
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = new();
    private readonly TaskCompletionSource _empty = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _synchronous;
    private bool _closed;
    private Task? _closing;
    private Task _cancellation = Task.CompletedTask;
    public CancellationToken Token { get; } = cancellation.Token;
    public bool IsClosed { get { lock (_gate) return _closed; } }
    public Task CancellationCompletion { get { lock (_gate) return _cancellation; } }

    public Lease Enter()
    {
        if (TryEnter(out var lease)) return lease;
        if (cancelClosedAdmission) throw new OperationCanceledException("Longbridge session admission is closed.", Token);
        throw new ObjectDisposedException(name);
    }
    public bool TryEnter(out Lease lease)
    {
        lock (_gate)
        {
            if (_closed) { lease = default; return false; }
            _synchronous++;
            lease = new(this);
            return true;
        }
    }
    public Task<T> Track<T>(Task<T> task) { Track((Task)task); return task; }
    public Task Track(Task task)
    {
        if (task.IsCompleted) { _ = task.Exception; return task; }
        lock (_gate) _tasks.Add(task);
        _ = task.ContinueWith(static (completed, state) => ((LongbridgeOwnedTasks)state!).Completed(completed), this,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    private void Completed(Task task)
    {
        // Observe infrastructure-owned tasks too; API callers still receive the unchanged faulted task.
        _ = task.Exception;
        lock (_gate) { _tasks.Remove(task); SignalEmpty(); }
    }
    private void Leave()
    {
        lock (_gate) { _synchronous--; SignalEmpty(); }
    }
    private void SignalEmpty()
    {
        if (_closed && _synchronous == 0 && _tasks.Count == 0) _empty.TrySetResult();
    }
    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closing != null) return _closing;
            _closed = true;
            SignalEmpty();
            _cancellation = cancellation.CancelAsync();
            _closing = Task.WhenAll(_cancellation, _empty.Task);
            return _closing;
        }
    }
    public readonly struct Lease(LongbridgeOwnedTasks? owner) : IDisposable
    {
        public void Dispose() => owner?.Leave();
    }
}
