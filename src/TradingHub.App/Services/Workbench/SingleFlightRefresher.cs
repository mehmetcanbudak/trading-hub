namespace TradingHub.App.Services.Workbench;

/// <summary>
/// Runs one background computation at a time for an owner thread (the UI) and posts only its result back. Requests made
/// while a computation is running are coalesced: the latest one starts when the running one finishes, so refreshes never
/// overlap. <see cref="Cancel"/> cancels the running computation, drops the coalesced one and discards any late result;
/// a cancelled computation still finishes before the next one starts. Every member is called on the owner thread, and
/// <paramref name="post"/> must deliver completions there.
/// </summary>
/// <param name="run">Starts work in the background (Task.Run in the application; a manual queue in tests).</param>
/// <param name="post">Delivers a completion to the owner thread.</param>
public sealed class SingleFlightRefresher<T>(Action<Action> run, Action<Action> post) : IDisposable
{
    private readonly record struct Job(Func<CancellationToken, T> Compute, Action<T> Apply, Action<Exception>? Failed);

    private CancellationTokenSource? _running;
    private Job? _pending;
    private bool _disposed;

    /// <summary>True while a computation (possibly cancelled) has not reported back.</summary>
    public bool IsRunning => _running != null;

    /// <summary>True while a coalesced request waits for the running computation.</summary>
    public bool HasPending => _pending != null;

    /// <summary>
    /// Computes <paramref name="compute"/> in the background and calls <paramref name="apply"/> with its result on the owner
    /// thread unless cancelled first. While another computation runs this request replaces any waiting one.
    /// <paramref name="failed"/> receives a computation's exception (cancellation excluded) on the owner thread.
    /// </summary>
    public void Request(Func<CancellationToken, T> compute, Action<T> apply, Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentNullException.ThrowIfNull(apply);
        if (_disposed) return;
        var job = new Job(compute, apply, failed);
        if (_running != null) _pending = job;
        else Start(job);
    }

    /// <summary>Cancels the running computation and drops the waiting request; neither result is applied.</summary>
    public void Cancel()
    {
        _pending = null;
        _running?.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }

    private void Start(Job job)
    {
        var cancellation = new CancellationTokenSource();
        _running = cancellation;
        var token = cancellation.Token;
        run(() =>
        {
            T result = default!;
            Exception? error = null;
            try { result = job.Compute(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { error = ex; }
            post(() => Complete(cancellation, job, result, error));
        });
    }

    private void Complete(CancellationTokenSource cancellation, Job job, T result, Exception? error)
    {
        bool cancelled = cancellation.IsCancellationRequested;
        _running = null;
        cancellation.Dispose();
        if (!cancelled && !_disposed)
        {
            if (error == null) job.Apply(result);
            else job.Failed?.Invoke(error);
        }
        if (_pending is { } next && !_disposed)
        {
            _pending = null;
            Start(next);
        }
    }
}
