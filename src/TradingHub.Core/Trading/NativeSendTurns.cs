namespace TradingHub.Trading;

/// <summary>
/// First-in-first-out send turns for one native account's order requests. A request's turn is taken synchronously when
/// the request is issued (before any background step), so turns follow issue order; the request waits for every earlier
/// turn to be released before it is sent, and releases its own once the venue answered, refused it or it failed.
/// Separate HTTP connections can reorder requests in flight, so waiting for the earlier reply, not only its send, is
/// what keeps a later close or cancel from reaching the venue ahead of an earlier order.
/// </summary>
public sealed class NativeSendTurns
{
    /// <summary>How long a reducing request (close, cancel, protective or amended order) waits for earlier ones.</summary>
    public static readonly TimeSpan ReducingWait = TimeSpan.FromSeconds(10);

    /// <summary>Shown when a reducing request was sent after <see cref="ReducingWait"/> without the earlier outcome.</summary>
    public const string UnsettledMessage =
        "An earlier order's outcome was still unsettled after 10 seconds; this reducing request was sent anyway. Check orders and position.";

    private readonly object _gate = new();
    private readonly TimeSpan _reducingWait;
    private Task _tail = Task.CompletedTask;
    private int _waiting;

    public NativeSendTurns() : this(ReducingWait) { }

    internal NativeSendTurns(TimeSpan reducingWait) => _reducingWait = reducingWait;

    /// <summary>Requests currently waiting for an earlier turn.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Takes the next turn. Call synchronously where the request is issued; release it in a finally.</summary>
    public Turn Take()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var previous = _tail;
            // A turn released early (a reducing request sent after its bounded wait) never lets a later one pass an
            // earlier one still outstanding. Neither task faults.
            _tail = Task.WhenAll(previous, done.Task);
            return new Turn(this, previous, done);
        }
    }

    public sealed class Turn
    {
        private readonly NativeSendTurns _owner;
        private readonly Task _previous;
        private readonly TaskCompletionSource _done;

        internal Turn(NativeSendTurns owner, Task previous, TaskCompletionSource done)
        {
            _owner = owner;
            _previous = previous;
            _done = done;
        }

        /// <summary>Waits for every earlier turn. A bounded wait returns false when it gave up after
        /// <see cref="ReducingWait"/>; the caller sends anyway and reports <see cref="UnsettledMessage"/>.</summary>
        public async Task<bool> WaitAsync(bool bounded, CancellationToken ct)
        {
            if (_previous.IsCompleted) return true;
            Interlocked.Increment(ref _owner._waiting);
            try
            {
                if (!bounded)
                {
                    await _previous.WaitAsync(ct).ConfigureAwait(false);
                    return true;
                }
                try
                {
                    await _previous.WaitAsync(_owner._reducingWait, ct).ConfigureAwait(false);
                    return true;
                }
                catch (TimeoutException) { return false; }
            }
            finally { Interlocked.Decrement(ref _owner._waiting); }
        }

        /// <summary>Lets later requests go. Idempotent.</summary>
        public void Release() => _done.TrySetResult();
    }
}
