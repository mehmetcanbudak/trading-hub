namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    /// <summary>Navigation only: opens the full local source/egress preview, never sends a model request.</summary>
    public Action<PineImportSource>? ReviewPineSource { get; set; }

    public PineHandoffRegistry PineHandoffs { get; } = new();

    /// <summary>True shutdown only. Await while the UI dispatcher is alive; tray hiding must not call this.</summary>
    public Task ShutdownAuthoringAsync() => PineHandoffs.StopAsync();
}

/// <summary>App ownership starts before a review callback's first await and ends only after its complete drain.</summary>
public sealed class PineHandoffRegistry
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
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            lock (_gate) _failures.Add(error);
            throw;
        }
        finally
        {
            lock (_gate) _active.Remove(completion);
            completion.TrySetResult();
        }
    }

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
            if (failures.Length != 0) completion.TrySetException(new AggregateException("Pine authoring shutdown did not complete cleanly.", failures));
            else completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
