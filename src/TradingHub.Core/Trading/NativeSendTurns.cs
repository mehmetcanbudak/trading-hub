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

    /// <summary>Close position or Reverse that sent nothing because the earlier outcome stayed unsettled.</summary>
    public const string NotSentUnsettledMessage =
        "An earlier order's outcome was still unsettled after 10 seconds; nothing was sent. Check orders and position.";

    /// <summary>Close position found the position on the other side than expected when it was issued and closed that.</summary>
    public const string CloseSideChangedMessage =
        "The position changed side before the close was sent; the close was sized from the position it found. Check the position.";

    /// <summary>Reverse found the position on the other side than expected when it was issued and sent nothing.</summary>
    public const string ReverseSideChangedMessage =
        "The position changed side before the reverse was sent; nothing was sent. Check the position.";

    /// <summary>
    /// The market order a Close position or Reverse sends, decided in its send turn. <paramref name="expected"/> is the
    /// position (including market orders in flight) when it was issued; <paramref name="net"/> the position now, confirmed
    /// to include those orders or the best known; <paramref name="pending"/> the signed unfilled quantity of those orders
    /// still working (only meaningful unconfirmed). Confirmed: the side comes from <paramref name="net"/>; a close of a
    /// position that changed side closes it and says so, a reverse refuses. Unconfirmed: a reverse refuses, and a close
    /// sends at most what is left on its side whether or not those orders execute, with <see cref="UnsettledMessage"/>.
    /// </summary>
    public static (OrderDirections Direction, decimal Quantity, string? Note, string? Refusal) PlanPositionOrder(
        bool reverse, decimal expected, decimal net, decimal pending, bool confirmed)
    {
        static OrderDirections Against(decimal position) => position > 0 ? OrderDirections.Sell : OrderDirections.Buy;
        if (confirmed)
        {
            if (net == 0) return (default, 0, null, "No open position.");
            bool changedSide = expected != 0 && Math.Sign(net) != Math.Sign(expected);
            if (reverse && changedSide) return (default, 0, null, ReverseSideChangedMessage);
            return (Against(net), Math.Abs(net) * (reverse ? 2 : 1), changedSide ? CloseSideChangedMessage : null, null);
        }
        if (reverse || net == 0) return (default, 0, null, NotSentUnsettledMessage);
        decimal side = Math.Sign(net);
        decimal quantity = Math.Min(Math.Abs(net), Math.Max(0, (net + pending) * side));
        return quantity > 0 ? (Against(net), quantity, UnsettledMessage, null) : (default, 0, null, NotSentUnsettledMessage);
    }

    private readonly object _gate = new();
    private readonly TimeSpan _reducingWait;
    private Task _tail = Task.CompletedTask;
    private int _waiting, _outstanding, _gaveUp;

    public NativeSendTurns() : this(ReducingWait) { }

    internal NativeSendTurns(TimeSpan reducingWait) => _reducingWait = reducingWait;

    /// <summary>Requests currently waiting for an earlier turn.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>
    /// Requests issued after the oldest one still unanswered: they wait for an earlier reply before they are sent.
    /// Counted from the moment a turn is taken, so it already includes a request issued a moment ago.
    /// </summary>
    public int Queued => Math.Max(0, Volatile.Read(ref _outstanding) - 1);

    /// <summary>Bounded waits that gave up after the reducing bound: requests sent anyway, ahead of an unanswered
    /// earlier one. Ordering checks count these instead of timing the bound.</summary>
    public int GaveUp => Volatile.Read(ref _gaveUp);

    /// <summary>Completes once every turn taken so far was released: the requests already issued were answered, refused
    /// or failed (quit waits for it before connections close). Never faults.</summary>
    public Task WhenIdle
    {
        get { lock (_gate) return _tail; }
    }

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
            Interlocked.Increment(ref _outstanding);
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
                catch (TimeoutException)
                {
                    Interlocked.Increment(ref _owner._gaveUp);
                    return false;
                }
            }
            finally { Interlocked.Decrement(ref _owner._waiting); }
        }

        /// <summary>Lets later requests go. Idempotent.</summary>
        public void Release()
        {
            if (_done.TrySetResult()) Interlocked.Decrement(ref _owner._outstanding);
        }
    }
}
