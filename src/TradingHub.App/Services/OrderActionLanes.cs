using TradingHub.Trading;

namespace TradingHub.App.Services;

/// <summary>The order action was not handed to its account because Trading Hub is quitting.</summary>
internal sealed class OrderActionsClosedException()
    : InvalidOperationException("Trading Hub is quitting: the order action was not sent.");

/// <summary>
/// Runs confirmed manual order gestures off the UI thread, in exactly the order they were issued, one FIFO lane per
/// account (its <see cref="OrderContext"/>, shared by every instrument of that account). The gesture's synchronous
/// account call is unchanged: owned-intent preparation, durable journaling, broker state commit and transport hand-off
/// still happen in that call, in the same order; only the thread that waits on the durable writes moves. Confirmations
/// and permission checks stay with the caller and run before the gesture is handed over; results and exceptions return
/// through the awaited task to the caller's context. Accounts that are not <see cref="IThreadSafeOrderEntry"/>
/// (simulated, replay) run inline on the caller, as before.
/// </summary>
internal sealed class OrderActionLanes
{
    private sealed class Lane
    {
        public Task Tail = Task.CompletedTask;
    }

    private sealed class Entry(string label)
    {
        public string Label { get; } = label;
    }

    private readonly object _gate = new();
    private readonly Dictionary<OrderContext, Lane> _lanes = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _pending = new();
    private bool _closed;

    /// <summary>Labels of handed-over gestures that have not finished, oldest first.</summary>
    public IReadOnlyList<string> Pending
    {
        get { lock (_gate) return _pending.Select(e => e.Label).ToArray(); }
    }

    /// <summary>True when gestures on <paramref name="account"/> leave the caller's thread.</summary>
    public static bool RunsOffCaller(ITradingAccount account) => account is IThreadSafeOrderEntry;

    /// <summary>Runs <paramref name="action"/> for <paramref name="account"/> after every gesture already handed to that
    /// account's lane. Inline for accounts that must stay on the caller's thread.</summary>
    public Task<T> RunAsync<T>(ITradingAccount account, string label, Func<T> action) =>
        RunsOffCaller(account) ? Enqueue(account.OrderContext, label, action) : Inline(action);

    /// <summary>Lane form for a thread-safe native session that is not itself an <see cref="ITradingAccount"/>.</summary>
    public Task<T> RunAsync<T>(OrderContext account, string label, Func<T> action) => Enqueue(account, label, action);

    public Task RunAsync(ITradingAccount account, string label, Action action) =>
        RunAsync(account, label, () => { action(); return true; });

    /// <summary>Stops admitting gestures and completes once every handed-over gesture has finished. Idempotent.</summary>
    public Task DrainAsync()
    {
        Task[] tails;
        lock (_gate)
        {
            _closed = true;
            tails = _lanes.Values.Select(l => l.Tail).ToArray();
        }
        return Task.WhenAll(tails);
    }

    private Task<T> Inline<T>(Func<T> action)
    {
        lock (_gate)
            if (_closed) return Task.FromException<T>(new OrderActionsClosedException());
        try { return Task.FromResult(action()); }
        catch (Exception error) { return Task.FromException<T>(error); }
    }

    private Task<T> Enqueue<T>(OrderContext account, string label, Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(label);
        lock (_gate)
        {
            if (_closed) return Task.FromException<T>(new OrderActionsClosedException());
            if (!_lanes.TryGetValue(account, out var lane)) _lanes[account] = lane = new Lane();
            _pending.Add(entry);
            // Continuations chained on the previous tail run one at a time in issue order. The tail never faults:
            // the gesture's outcome belongs to its own completion.
            lane.Tail = lane.Tail.ContinueWith(_ =>
            {
                T result = default!;
                Exception? failure = null;
                try { result = action(); }
                catch (Exception error) { failure = error; }
                // Leave the pending set before completing: the caller's result line counts only gestures still waiting.
                lock (_gate) _pending.Remove(entry);
                if (failure != null) completion.SetException(failure);
                else completion.SetResult(result);
            }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }
        return completion.Task;
    }
}
