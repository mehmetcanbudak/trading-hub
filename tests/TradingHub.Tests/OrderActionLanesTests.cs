using TradingHub.App.Services;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// The per-account FIFO lanes that keep confirmed manual order gestures off the UI thread: issue order, queuing of a
/// second gesture while the first is pending, error delivery, per-account independence and the quit drain.
/// </summary>
public sealed class OrderActionLanesTests
{
    /// <summary>A UI-affine account (like the simulator): not <see cref="IThreadSafeOrderEntry"/>.</summary>
    private sealed class LaneAccount(OrderContext context) : ITradingAccount
    {
        public OrderContext OrderContext { get; } = context;
        public IExecutionSource Executions => OrderContext.Executions;
        public string Name => "Lane fixture";
        public bool IsReal => false;
        public Instrument Instrument { get; } = new() { FeedId = "sim", Symbol = "LANE", TickSize = 1, VolumeStep = 1, PointValue = 1 };
        public Position Position { get; } = new();
        public IEnumerable<Order> ActiveOrders => Array.Empty<Order>();
        public string? LastError => null;
        public event Action? Changed { add { } remove { } }
        public Order Submit(PreparedOrderIntent intent) => throw new NotSupportedException();
        public Order Replace(Order order, PreparedOrderIntent intent) => throw new NotSupportedException();
        public void Cancel(Order order) { }
        public void CancelAt(decimal price) { }
        public void CancelAll() { }
    }

    private sealed class NativeAccount(OrderContext context) : ITradingAccount, IThreadSafeOrderEntry
    {
        private readonly LaneAccount _inner = new(context);
        public OrderContext OrderContext => _inner.OrderContext;
        public IExecutionSource Executions => _inner.Executions;
        public string Name => _inner.Name;
        public bool IsReal => false;
        public Instrument Instrument => _inner.Instrument;
        public Position Position => _inner.Position;
        public IEnumerable<Order> ActiveOrders => _inner.ActiveOrders;
        public string? LastError => null;
        public event Action? Changed { add { } remove { } }
        public Order Submit(PreparedOrderIntent intent) => throw new NotSupportedException();
        public Order Replace(Order order, PreparedOrderIntent intent) => throw new NotSupportedException();
        public void Cancel(Order order) { }
        public void CancelAt(decimal price) { }
        public void CancelAll() { }
    }

    private static NativeAccount Native(string name = "lane") => new(OrderContext.EphemeralSimulation(name));

    [Fact]
    public async Task Gestures_run_once_each_in_issue_order_off_the_calling_thread()
    {
        var lanes = new OrderActionLanes();
        var account = Native();
        using var gate = new ManualResetEventSlim();
        var ran = new List<int>();
        // Set only while the caller is inside RunAsync: a gesture that sees it ran inline on the caller's stack. A pool
        // thread id is not evidence either way, since the awaiting caller's thread can later serve the lane.
        using var onCallerStack = new ThreadLocal<bool>();
        var inline = new List<bool>();
        onCallerStack.Value = true;
        var first = lanes.RunAsync(account, "first", () =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            lock (ran) { ran.Add(0); inline.Add(onCallerStack.Value); }
            return 0;
        });
        var rest = Enumerable.Range(1, 49).Select(i => lanes.RunAsync(account, "gesture " + i, () =>
        {
            lock (ran) { ran.Add(i); inline.Add(onCallerStack.Value); }
            return i;
        })).ToArray();
        onCallerStack.Value = false;

        // Every later gesture is queued behind the first; none ran or was dropped.
        Assert.Equal(50, lanes.Pending.Count);
        Assert.Equal("first", lanes.Pending[0]);
        lock (ran) Assert.Empty(ran);
        gate.Set();

        int[] results = await Task.WhenAll(rest.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Enumerable.Range(0, 50), results);
        Assert.Equal(Enumerable.Range(0, 50), ran);
        Assert.Equal(50, inline.Count);
        Assert.DoesNotContain(true, inline);
        Assert.Empty(lanes.Pending);
    }

    [Fact]
    public async Task A_second_gesture_on_the_same_order_while_the_first_is_pending_is_queued_once_in_order()
    {
        var lanes = new OrderActionLanes();
        var account = Native();
        using var gate = new ManualResetEventSlim();
        var moves = new List<decimal>();
        var firstMove = lanes.RunAsync(account, "Move buy order to 100", () =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            lock (moves) moves.Add(100);
            return "moved to 100";
        });
        var secondMove = lanes.RunAsync(account, "Move buy order to 101", () =>
        {
            lock (moves) moves.Add(101);
            return "moved to 101";
        });
        Assert.Equal(new[] { "Move buy order to 100", "Move buy order to 101" }, lanes.Pending);
        Assert.False(secondMove.IsCompleted);
        gate.Set();

        Assert.Equal("moved to 100", await firstMove.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("moved to 101", await secondMove.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(new[] { 100m, 101m }, moves);
    }

    [Fact]
    public async Task An_account_call_failure_reaches_the_awaiting_caller_and_the_lane_continues()
    {
        var lanes = new OrderActionLanes();
        var account = Native();
        var failing = lanes.RunAsync<int>(account, "fails", () => throw new InvalidOperationException("Broker refused the gesture."));
        var next = lanes.RunAsync(account, "next", () => 7);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("Broker refused the gesture.", error.Message);
        Assert.Equal(7, await next.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task One_lane_per_account_context_shared_by_its_instruments_and_independent_of_other_accounts()
    {
        var lanes = new OrderActionLanes();
        var context = OrderContext.EphemeralSimulation("shared");
        var instrumentA = new NativeAccount(context);
        var instrumentB = new NativeAccount(context);
        var otherAccount = Native("other");
        using var gate = new ManualResetEventSlim();
        var blocked = lanes.RunAsync(instrumentA, "blocked", () => gate.Wait(TimeSpan.FromSeconds(30)));
        var sameAccount = lanes.RunAsync(instrumentB, "same account", () => true);

        // A different account is not held behind this one.
        Assert.True(await lanes.RunAsync(otherAccount, "other account", () => true).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(sameAccount.IsCompleted);
        gate.Set();
        Assert.True(await blocked.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await sameAccount.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Ui_affine_accounts_run_inline_on_the_caller_as_before()
    {
        var lanes = new OrderActionLanes();
        var simulated = new LaneAccount(OrderContext.EphemeralSimulation("sim"));
        int caller = Environment.CurrentManagedThreadId, ranOn = -1;
        var result = lanes.RunAsync(simulated, "inline", () => { ranOn = Environment.CurrentManagedThreadId; return 3; });
        Assert.True(result.IsCompletedSuccessfully);
        Assert.Equal(3, await result); // already complete: the inline call ran before RunAsync returned
        Assert.Equal(caller, ranOn);
        Assert.Empty(lanes.Pending);

        var failed = lanes.RunAsync<int>(simulated, "inline failure", () => throw new InvalidOperationException("inline"));
        Assert.Equal("inline", Assert.IsType<InvalidOperationException>(failed.Exception!.GetBaseException()).Message);
    }

    [Fact]
    public async Task Drain_completes_every_handed_over_gesture_and_refuses_later_ones()
    {
        var lanes = new OrderActionLanes();
        var account = Native();
        var simulated = new LaneAccount(OrderContext.EphemeralSimulation("sim"));
        using var gate = new ManualResetEventSlim();
        var ran = new List<string>();
        var first = lanes.RunAsync(account, "first", () => { gate.Wait(TimeSpan.FromSeconds(30)); lock (ran) ran.Add("first"); return true; });
        var queued = lanes.RunAsync(account, "queued", () => { lock (ran) ran.Add("queued"); return true; });

        var drain = lanes.DrainAsync();
        Assert.False(drain.IsCompleted);
        var late = lanes.RunAsync(account, "late", () => { lock (ran) ran.Add("late"); return true; });
        var lateInline = lanes.RunAsync(simulated, "late inline", () => { lock (ran) ran.Add("late inline"); return true; });
        Assert.IsType<OrderActionsClosedException>(late.Exception!.GetBaseException());
        Assert.IsType<OrderActionsClosedException>(lateInline.Exception!.GetBaseException());

        gate.Set();
        await drain.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(first.IsCompletedSuccessfully && queued.IsCompletedSuccessfully);
        Assert.Equal(new[] { "first", "queued" }, ran);
        Assert.Empty(lanes.Pending);
        await lanes.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30)); // idempotent
    }
}
