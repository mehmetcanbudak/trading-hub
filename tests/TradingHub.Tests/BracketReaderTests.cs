using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// BracketManager actions and synchronization hold the manager's gate across account calls, which on native accounts
/// wait on durable journal writes (on an order lane or a broker thread). The chart, DOM and Chart Trader read the
/// manager on the UI thread every frame: those reads must never wait for that gate.
/// </summary>
public sealed class BracketReaderTests
{
    [Fact]
    public async Task Readers_return_the_published_state_while_an_action_is_inside_its_account_call()
    {
        var account = new HeldAccount();
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90m);
        var stop = Assert.IsType<Order>(brackets.StopLoss);
        decimal pnlBefore = brackets.PnLAt(95m);

        account.Hold = true;
        var action = Task.Run(() => brackets.AddTarget(110m));
        Assert.True(account.Entered.Wait(TimeSpan.FromSeconds(10)), "the target's account call never started");
        var reads = Task.Run(() => (Stop: brackets.StopLoss, Targets: brackets.Targets.Count, Role: brackets.RoleOf(stop),
            Number: brackets.TargetNumber(stop), brackets.Mode, brackets.ProtectionNote, Pending: brackets.Pending.Count,
            Attached: brackets.PendingFor(stop), brackets.EntryPrice, PnL: brackets.PnLAt(95m)));
        bool returned = await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(5))) == reads;
        account.Release.Set();
        Assert.True(returned, "a bracket read waited for the action holding the account call");

        // the state published before the action, not a partial one
        var seen = await reads;
        Assert.Same(stop, seen.Stop);
        Assert.Equal(0, seen.Targets);
        Assert.Equal(BracketRole.StopLoss, seen.Role);
        Assert.Equal(0, seen.Number);
        Assert.Equal(ProtectionMode.AppManaged, seen.Mode);
        Assert.Null(seen.ProtectionNote);
        Assert.Equal(0, seen.Pending);
        Assert.Null(seen.Attached);
        Assert.Equal(100m, seen.EntryPrice);
        Assert.Equal(pnlBefore, seen.PnL);

        Assert.StartsWith("Target for", await action);
        var target = Assert.Single(brackets.Targets);
        Assert.Equal(BracketRole.Target, brackets.RoleOf(target));
        Assert.Equal(1, brackets.TargetNumber(target));
        Assert.Same(stop, brackets.StopLoss);
    }

    [Fact]
    public void Pending_attachments_are_read_as_copies_and_edited_through_the_manager()
    {
        var account = new HeldAccount();
        account.Position.Volume = 0;
        using var brackets = new BracketManager(account);
        brackets.Template = new BracketTemplate { Enabled = true, StopTicks = 10 };
        brackets.Template.Targets.Clear();
        brackets.Template.Targets.Add(new BracketTarget { Ticks = 20, Percent = 100 });
        var entry = OrderActions.Manual(account, OrderTypes.Limit, OrderDirections.Buy, 1m, 95m);
        brackets.AttachTemplate(entry);

        // what the chart renders and drags: a copy, which later edits never change under it
        var copy = Assert.IsType<PendingBracket>(brackets.PendingFor(entry));
        Assert.Equal(10, copy.StopTicks);
        Assert.Equal(20, Assert.Single(copy.Targets).Ticks);

        Assert.Equal("Stop attached at 15 ticks", brackets.MovePending(copy, -1, 80m));
        Assert.Equal(10, copy.StopTicks);
        Assert.Equal(15, brackets.PendingFor(entry)!.StopTicks);

        Assert.Equal("Target attached at 30 ticks", brackets.MovePending(copy, 0, 125m));
        Assert.Equal(20, Assert.Single(copy.Targets).Ticks);
        Assert.Equal(30, Assert.Single(brackets.PendingFor(entry)!.Targets).Ticks);

        brackets.RemovePending(copy, 0);
        Assert.Single(copy.Targets);
        Assert.Empty(brackets.PendingFor(entry)!.Targets);
        Assert.Equal(15, Assert.Single(brackets.Pending).StopTicks);
    }

    /// <summary>A thread-safe account whose order calls can be held inside the call, as a durable write holds them.</summary>
    private sealed class HeldAccount : ITradingAccount, IThreadSafeOrderEntry
    {
        private readonly object _gate = new();
        private readonly List<Order> _working = new();
        public volatile bool Hold;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public string Name => "Bracket reader fixture";
        public bool IsReal => false;
        public Instrument Instrument { get; } = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m };
        public Position Position { get; } = new() { Volume = 4m, AveragePrice = 100m };
        public IEnumerable<Order> ActiveOrders { get { lock (_gate) return _working.ToArray(); } }
        public decimal LastPrice => 100m;
        public string? LastError => null;
        public OrderContext OrderContext { get; } = OrderContext.EphemeralSimulation("Bracket reader fixture");
        public IExecutionSource Executions { get; } = new UnavailableExecutionSource();
        public event Action? Changed;

        public Order Submit(PreparedOrderIntent intent)
        {
            var request = intent.Request;
            var order = new Order
            {
                Type = request.Type, Direction = request.Direction, QuantityToFill = request.Quantity, Unfilled = request.Quantity,
                Price = request.Type == OrderTypes.Limit ? request.Price : 0, TriggerPrice = request.Type == OrderTypes.Stop ? request.Price : 0,
                State = OrderStates.Active, Status = OrderStatus.Placed, Comment = request.Comment, ReduceOnly = request.ReduceOnly,
            };
            OrderContext.BindOrder(order, intent);
            if (Hold)
            {
                Entered.Set();
                Assert.True(Release.Wait(TimeSpan.FromSeconds(30)), "the held account call was never released");
            }
            order.ExchangeOrderId = order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (_gate) _working.Add(order);
            Changed?.Invoke();
            return order;
        }

        public Order Replace(Order order, PreparedOrderIntent intent) => throw new NotSupportedException();
        public void Cancel(Order order) => throw new NotSupportedException();
        public void CancelAt(decimal price) => throw new NotSupportedException();
        public void CancelAll() => throw new NotSupportedException();
    }
}
