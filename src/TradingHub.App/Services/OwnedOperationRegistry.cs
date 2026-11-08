namespace TradingHub.App.Services;

/// <summary>Owns admission, terminal cancellation and the actual return of each complete consumer.</summary>
public sealed class OwnedOperationRegistry
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<TaskCompletionSource> _active = new();
    private readonly List<Exception> _failures = new();
    private bool _stopping;
    private Task? _shutdown;

    public bool IsStopping { get { lock (_gate) return _stopping; } }

    public Task RunAsync(Func<CancellationToken, Task> operation) => RunAsync(async cancellation =>
    {
        await operation(cancellation);
        return true;
    });

    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        TaskCompletionSource completion;
        CancellationToken cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _active.Add(completion);
            cancellation = _stop.Token;
        }
        return CompleteOperationAsync(operation, completion, cancellation);
    }

    private async Task<T> CompleteOperationAsync<T>(Func<CancellationToken, Task<T>> operation,
        TaskCompletionSource completion, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            return await operation(cancellation);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            lock (_gate) _failures.Add(error);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                completion.TrySetResult();
                _active.Remove(completion);
            }
        }
    }

    /// <summary>Closes admission immediately. Cancellation cannot abort arbitrary in-process C#; its actual return remains owned.</summary>
    public Task StopAsync()
    {
        Task operations;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_shutdown != null) return _shutdown;
            _stopping = true;
            operations = Task.WhenAll(_active.Select(operation => operation.Task));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdown = completion.Task;
        }
        _ = CompleteStopAsync(operations, completion);
        return completion.Task;
    }

    private async Task CompleteStopAsync(Task operations, TaskCompletionSource completion)
    {
        try
        {
            try { _stop.Cancel(); }
            catch (Exception error) { lock (_gate) _failures.Add(error); }
            await operations.ConfigureAwait(false);
            _stop.Dispose();
            Exception[] failures;
            lock (_gate) failures = _failures.ToArray();
            if (failures.Length != 0) completion.TrySetException(new AggregateException("Owned operations did not complete cleanly.", failures));
            else completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
