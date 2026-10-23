using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

/// <summary>
/// Real-order permission checked when an order is sent: once real orders are revoked (or the connection is disabled or
/// removed), an order that opens, adds to or reverses a position on a real account is blocked, while reductions and
/// cancels stay possible and simulated accounts are never affected.
/// </summary>
[Collection("LiveStore")] // the runner test changes the static live-store root: never in parallel with the other live tests
public class RealOrderPermissionTests : IDisposable
{
    private static readonly (string Name, bool Enabled, bool AllowRealOrders) Allowed = ("Fixture REAL", true, true);
    private static readonly (string Name, bool Enabled, bool AllowRealOrders) Revoked = ("Fixture REAL", true, false);
    private static readonly (string Name, bool Enabled, bool AllowRealOrders) Disabled = ("Fixture REAL", false, true);
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub_permission_" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = LiveStore.Root;
    private readonly Instrument _inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
    private InstrumentData _data = null!;
    private long _id = 1;
    private DateTime _time = T0;

    public RealOrderPermissionTests() => LiveStore.Root = _root;

    public void Dispose()
    {
        LiveStore.Root = _previousRoot;
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // cleanup only
        }
    }

    [Fact]
    public void An_order_that_adds_exposure_on_a_real_account_is_blocked_after_revocation()
    {
        Assert.Null(RealOrderPermission.Check(true, Allowed, 0, 1));
        Assert.Null(RealOrderPermission.Check(true, Allowed, 2, -3));
        string? entry = RealOrderPermission.Check(true, Revoked, 0, 1);
        Assert.Contains("real orders are not allowed for Fixture REAL", entry);
        Assert.NotNull(RealOrderPermission.Check(true, Revoked, 1, 1)); // adds to a long
        Assert.NotNull(RealOrderPermission.Check(true, Revoked, -1, -2)); // adds to a short
        Assert.NotNull(RealOrderPermission.Check(true, Revoked, 2, -3)); // reverses through flat
        string? disabled = RealOrderPermission.Check(true, Disabled, 0, -1);
        Assert.Contains("connection Fixture REAL is disabled", disabled);
        string? removed = RealOrderPermission.Check(true, null, 0, 1);
        Assert.Contains("no longer exists", removed);
        Assert.All(new[] { entry, disabled, removed }, why => Assert.Contains("new orders are disabled; cancel/close allowed", why));
    }

    [Fact]
    public void Reductions_and_cancels_stay_allowed_after_revocation()
    {
        foreach (var connection in new (string, bool, bool)?[] { Revoked, Disabled, null })
        {
            Assert.Null(RealOrderPermission.Check(true, connection, 2, -1)); // part of a long
            Assert.Null(RealOrderPermission.Check(true, connection, 2, -2)); // the whole long
            Assert.Null(RealOrderPermission.Check(true, connection, -3, 1)); // part of a short
            Assert.Null(RealOrderPermission.Check(true, connection, -3, 3)); // the whole short
            Assert.Null(RealOrderPermission.Check(true, connection, 2, 0)); // a cancel adds no quantity
        }

        Assert.False(RealOrderPermission.IncreasesExposure(2, -1));
        Assert.False(RealOrderPermission.IncreasesExposure(-2, 2));
        Assert.False(RealOrderPermission.IncreasesExposure(0, 0));
        Assert.True(RealOrderPermission.IncreasesExposure(0, 1));
        Assert.True(RealOrderPermission.IncreasesExposure(1, 1));
        Assert.True(RealOrderPermission.IncreasesExposure(2, -3));
        Assert.True(RealOrderPermission.IncreasesExposure(-1, 1.5m));
    }

    [Fact]
    public void Simulated_and_practice_accounts_are_never_blocked()
    {
        foreach (var connection in new (string, bool, bool)?[] { Allowed, Revoked, Disabled, null })
        foreach (var (position, quantity) in new[] { (0m, 1m), (1m, 1m), (-1m, -2m), (2m, -3m), (2m, -1m), (2m, 0m) })
            Assert.Null(RealOrderPermission.Check(false, connection, position, quantity));
    }

    [Fact]
    public void A_live_runner_on_a_real_account_stops_adding_after_revocation_but_still_cancels_and_exits()
    {
        var guard = new ConnectionGuard { Connection = Allowed };
        var (s, broker, r) = Setup(guard, real: true);
        s.Next.Enqueue(x => x.DoBuy(1)); // allowed: enters
        Tick(101);
        Tick(102);
        Assert.Equal(1m, broker.Position!.Volume);
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop); // its protective stop
        s.Next.Enqueue(x => x.DoBuyLimit(50)); // allowed: a working entry far below the market
        Tick(103);
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Limit);

        guard.Connection = Revoked;
        s.Next.Enqueue(x => x.DoBuy(1)); // adds exposure: blocked at submit
        Tick(104);
        Tick(105);
        Assert.Equal(1m, broker.Position!.Volume);
        Assert.Equal(1, guard.Blocked);
        Assert.Contains(r.LogLines, l => l.Contains("Risk guard: real orders are not allowed for Fixture REAL", StringComparison.Ordinal));
        s.Next.Enqueue(x => x.DoShort(1)); // a reversal opens the other side: blocked as a whole
        Tick(106);
        Tick(107);
        Assert.Equal(1m, broker.Position!.Volume);
        Assert.Equal(2, guard.Blocked);

        s.Next.Enqueue(x => x.CancelWorking()); // cancels never need permission
        Tick(108);
        Assert.DoesNotContain(broker.ActiveOrders, o => o.Type == OrderTypes.Limit);
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop); // protection stays at the broker

        s.Next.Enqueue(x => x.DoClose()); // the exit always goes out
        Tick(109);
        Tick(110);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Empty(broker.ActiveOrders);
        Assert.Equal(2, guard.Blocked);
    }

    [Fact]
    public void A_live_runner_on_a_simulated_account_is_not_affected_by_a_revoked_connection()
    {
        var guard = new ConnectionGuard { Connection = Revoked };
        var (s, broker, _) = Setup(guard, real: false);
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(101);
        Tick(102);
        Assert.Equal(1m, broker.Position!.Volume);
        Assert.Equal(0, guard.Blocked);
    }

    /// <summary>The permission decision behind a runner, as the app wires it, with the connection state set by the test.</summary>
    private sealed class ConnectionGuard : ILiveOrderGuard
    {
        public (string Name, bool Enabled, bool AllowRealOrders)? Connection;
        public int Blocked;

        public string? Check(LiveStrategyRunner runner, OrderSide side, decimal quantity, decimal positionAfter)
        {
            decimal signed = side == OrderSide.Buy ? quantity : -quantity;
            string? why = RealOrderPermission.Check(runner.Account.IsReal, Connection, positionAfter - signed, signed);
            if (why != null) Blocked++;
            return why;
        }
    }

    /// <summary>Strategy that runs whatever the test queues on its next bar, with a protective stop.</summary>
    private sealed class Scripted : Strategy
    {
        public readonly Queue<Action<Scripted>> Next = new();
        private StrategyOrder? _working;

        public Scripted() => StopLossTicks = 5;

        protected override void OnBar(Bar bar)
        {
            if (Next.Count > 0) Next.Dequeue()(this);
        }

        public void DoBuy(decimal q) => Buy(q);
        public void DoShort(decimal q) => EnterShort(q);
        public void DoClose() => ClosePosition();
        public void DoBuyLimit(decimal price) => _working = BuyLimit(price, 1);

        public void CancelWorking()
        {
            if (_working != null) Cancel(_working);
        }
    }

    /// <summary>A simulated account that reports itself as real: the fills stay in memory.</summary>
    private sealed class RealLike(SimBroker inner) : ITradingAccount
    {
        public string Name => "Fixture REAL";
        public bool IsReal => true;
        public Instrument Instrument => inner.Instrument;
        public Position Position => ((ITradingAccount)inner).Position;
        public IEnumerable<Order> ActiveOrders => inner.ActiveOrders;
        public string? LastError => inner.LastError;
        public decimal LastPrice => inner.LastPrice;

        public event Action? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Order Market(OrderDirections dir, decimal qty, string comment = "") => inner.Market(dir, qty, comment);
        public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => inner.Limit(dir, qty, price, comment);
        public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => inner.Stop(dir, qty, trigger, comment);
        public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") => inner.Exit(type, dir, qty, price, comment);
        public Order Replace(Order order, decimal newPrice, decimal newQuantity) => inner.Replace(order, newPrice, newQuantity);
        public void Cancel(Order order) => inner.Cancel(order);
        public void CancelAt(decimal price) => inner.CancelAt(price);
        public void CancelAll() => inner.CancelAll();
        public void Modify(Order order, decimal newPrice) => inner.Modify(order, newPrice);
        public void Flatten() => inner.Flatten();
        public void Reverse() => inner.Reverse();
    }

    private (Scripted Strategy, ITradingAccount Broker, LiveStrategyRunner Runner) Setup(ILiveOrderGuard guard, bool real)
    {
        _data = new InstrumentData(_inst, new SimulatedFeed());
        for (int i = 0; i < 5; i++) _data.Trades.Add(new Trade(_id++, _time = T0.AddSeconds(i * 30), 100, 1, TradeSide.Buy));
        var sim = new SimBroker(_data, new Portfolio(), new TradingStatistics());
        ITradingAccount broker = real ? new RealLike(sim) : sim;
        var s = new Scripted();
        var r = new LiveStrategyRunner(s, _data, broker, ChartPeriod.M1) { Guard = guard };
        r.Start();
        return (s, broker, r);
    }

    /// <summary>A new trade one minute later (closes the previous bar).</summary>
    private void Tick(decimal price)
    {
        int start = _data.Trades.Count;
        _time = _time.AddMinutes(1);
        _data.Trades.Add(new Trade(_id++, _time, price, 1, TradeSide.Buy));
        var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        ((Action<int, int>?)field!.GetValue(_data))?.Invoke(start, 1);
    }
}
