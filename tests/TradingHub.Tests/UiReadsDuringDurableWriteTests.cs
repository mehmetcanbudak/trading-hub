using System.Reflection;
using System.Threading.Channels;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.Rithmic;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Native order entry runs off the UI thread and holds its broker gate across durable journal writes. The account
/// members the UI thread reads on every refresh must never wait on that gate: each test submits one manual order from a
/// background flow whose durable flushes are held one at a time, and while each flush is held reads those members from
/// the test thread under a bound.
/// </summary>
public sealed class UiReadsDuringDurableWriteTests : IDisposable
{
    private static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StepBound = TimeSpan.FromSeconds(30);
    private readonly NativeOrderTestFixture _orders = new();
    private readonly string _bybitRoot = Path.Combine(Path.GetTempPath(), "TradingHub-ui-reads-bybit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _orders.Dispose();
        if (Directory.Exists(_bybitRoot)) Directory.Delete(_bybitRoot, recursive: true);
    }

    [Fact]
    public async Task Binance_ui_reads_never_wait_for_a_durable_write_of_order_entry()
    {
        using var fake = new FakeBinance(futures: true);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
        using var broker = _orders.Binance(new InstrumentData(instrument, new SimulatedFeed()), fake.Settings, "Binance UI reads");
        await Until(broker, () => broker.PositionReady && broker.OrdersReady, "Binance readiness");

        var order = await SubmitWhileReading(broker, GateOf(broker),
            () => OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 0.01m, 59000m), BrokerReads(broker));

        await Until(broker, () => order.NativeIdentity != null && broker.ActiveOrders.Contains(order), "Binance acceptance");
    }

    [Fact]
    public async Task Rithmic_ui_reads_never_wait_for_a_durable_write_of_order_entry()
    {
        using var server = new FakeRithmicServer();
        using var market = new SimulatedFeed();
        using var data = new InstrumentData(Futures(RithmicFeed.FeedId), market) { RecordLive = false };
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000
        });
        using var broker = _orders.Rithmic(data, session, "ACC1", "Rithmic UI reads", _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(broker, () => broker.OrdersReady && broker.PositionReady, "Rithmic replay readiness");

        var order = await SubmitWhileReading(broker, GateOf(broker),
            () => OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1m, 4990m), BrokerReads(broker));

        await Until(broker, () => order.NativeIdentity != null && broker.ActiveOrders.Contains(order) && broker.OrdersReady,
            "Rithmic acceptance");
    }

    [Fact]
    public async Task Tradovate_ui_reads_never_wait_for_a_durable_write_of_order_entry()
    {
        using var fake = new FakeTradovate();
        using var market = new SimulatedFeed();
        using var data = new InstrumentData(Futures(TradovateFeed.FeedId), market) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(handler => session.Changed += handler, handler => session.Changed -= handler,
            () => session.SnapshotReady, "Tradovate snapshot readiness", () => session.StatusText);
        using var broker = _orders.Tradovate(data, session, Assert.Single(session.Accounts), "Tradovate UI reads");
        await Until(broker, () => broker.OrdersReady && broker.PositionReady, "Tradovate readiness");

        // Chart Trader also shows the protection mode and note (BracketManager reads these), and adoption groups legs.
        var working = new Order { ExchangeOrderId = "424242" };
        var order = await SubmitWhileReading(broker, GateOf(broker),
            () => OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1m, 4990m),
            [
                .. BrokerReads(broker),
                ("NativeProtection", () => _ = broker.NativeProtection),
                ("NativeProtectionFault", () => _ = broker.NativeProtectionFault),
                ("ProtectionGroupOf", () => _ = broker.ProtectionGroupOf(working)),
            ]);

        await Until(broker, () => order.NativeIdentity != null && broker.ActiveOrders.Contains(order) && broker.OrdersReady,
            "Tradovate acceptance");
    }

    [Fact]
    public async Task Bybit_ui_reads_never_wait_for_a_durable_write_of_order_entry()
    {
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: Path.Combine(_bybitRoot, "orders.json"),
            httpClient: http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        using var broker = new BybitBroker(new InstrumentData(instrument, new SimulatedFeed()), session, "Bybit UI reads");
        await session.RefreshAsync();
        Assert.True(broker.PositionReady && broker.OrdersReady);
        var unknown = new Order();

        // AccountCenter's Bybit snapshot reads the session directly, besides the broker members every account shares.
        var order = await SubmitWhileReading(broker, GateOf(session),
            () => OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1m, 99m),
            [
                .. BrokerReads(broker),
                ("Session.PositionReady", () => _ = session.PositionReady),
                ("Session.OrdersReady", () => _ = session.OrdersReady),
                ("Session.Orders", () => _ = session.Orders.Count),
                ("Session.Positions", () => _ = session.Positions.Count),
                ("Session.IsSubmissionUnknown", () => _ = session.IsSubmissionUnknown(unknown)),
                ("Session.IsReplacementPending", () => _ = session.IsReplacementPending(unknown)),
                ("Session.AverageFillFor", () => _ = session.AverageFillFor(unknown)),
            ]);

        await Until(broker, () => order.NativeIdentity != null && !session.IsSending(order) && broker.ActiveOrders.Contains(order) &&
            session.Orders.Contains(order) && session.IsSubmissionUnknown(order), "Bybit placement");
        Assert.False(session.IsReplacementPending(order));
    }

    private static (string Member, Action Read)[] BrokerReads(ITradingAccount account) =>
    [
        ("ActiveOrders", () => _ = account.ActiveOrders.ToList()),
        ("LastError", () => _ = account.LastError),
        ("PositionReady", () => _ = account.PositionReady),
        ("OrdersReady", () => _ = account.OrdersReady),
        ("Position.Volume", () => _ = account.Position.Volume),
    ];

    /// <summary>
    /// Submits from a background flow whose durable flushes are held one at a time. While each is held, every read must
    /// finish within <see cref="ReadBound"/>; returns once the order has been dispatched (an exchange id or a terminal
    /// state), after releasing the flow. Fails listing each member that waited, and unless at least one held flush ran
    /// under <paramref name="gate"/>, which is what makes the scenario meaningful.
    /// </summary>
    private static async Task<Order> SubmitWhileReading(ITradingAccount account, object gate, Func<Order> submit,
        (string Member, Action Read)[] reads)
    {
        var held = new HeldFlushes(gate);
        var submission = Task.Run(() =>
        {
            using (DurableFile.Use(held)) return submit();
        });
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (!submission.IsCompleted) return;
            if (!submission.IsCompletedSuccessfully) dispatched.TrySetResult();
            else if (submission.Result is { } order && (order.State != OrderStates.Active || order.ExchangeOrderId.Length > 0))
                dispatched.TrySetResult();
        }
        account.Changed += Check;
        _ = submission.ContinueWith(_ => Check(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var blocked = new SortedDictionary<string, List<int>>(StringComparer.Ordinal); // member → held flushes it waited on
        int flushes = 0, underGate = 0;
        var next = held.NextAsync();
        try
        {
            while (true)
            {
                var first = await Task.WhenAny(next, dispatched.Task).WaitAsync(StepBound);
                if (first == dispatched.Task) break;
                var flush = await next;
                next = held.NextAsync();
                flushes++;
                if (flush.UnderGate) underGate++;
                try
                {
                    var tasks = reads.Select(read => (read.Member, Task: Task.Factory.StartNew(read.Read, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default))).ToArray();
                    try { await Task.WhenAll(tasks.Select(task => task.Task)).WaitAsync(ReadBound); }
                    catch (TimeoutException)
                    {
                        foreach (var (member, task) in tasks.Where(task => !task.Task.IsCompleted))
                        {
                            if (!blocked.TryGetValue(member, out var during)) blocked[member] = during = new();
                            during.Add(flushes);
                        }
                    }
                }
                finally { flush.Release(); }
            }
        }
        finally
        {
            account.Changed -= Check;
            held.Open();
            _ = next.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Release(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        var placed = await submission.WaitAsync(StepBound);
        Assert.True(blocked.Count == 0, $"{account.GetType().Name} UI reads waited on a durable write (held flushes {flushes}, " +
            $"{underGate} under the gate): {string.Join("; ", blocked.Select(pair => $"{pair.Key} during flushes {string.Join(",", pair.Value)}"))}.");
        Assert.True(underGate > 0, $"None of {flushes} held flushes ran under the {account.GetType().Name} gate; the scenario was not exercised.");
        Assert.True(placed.State == OrderStates.Active, $"The submission was not dispatched: {placed.State}/{placed.Status}; {account.LastError}");
        return placed;
    }

    private static object GateOf(object owner) =>
        owner.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException(owner.GetType().Name + " has no _gate field.");

    private static Task Until(ITradingAccount account, Func<bool> condition, string what) =>
        Until(handler => account.Changed += handler, handler => account.Changed -= handler, condition, what, () => account.LastError);

    private static async Task Until(Action<Action> subscribe, Action<Action> unsubscribe, Func<bool> condition, string what,
        Func<string?> diagnostics)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (condition()) reached.TrySetResult(); }
        subscribe(Changed);
        try
        {
            Changed(); // the state may have been published before subscription
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException) { Assert.Fail($"Timed out waiting for {what}; {diagnostics() ?? "(no error)"}."); }
        finally { unsubscribe(Changed); }
    }

    private static Instrument Futures(string feedId) => new()
    {
        Symbol = "ES", FeedId = feedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD"
    };

    /// <summary>Holds each flush of the flow it is installed in until the test releases it; <see cref="Open"/> lets every
    /// current and later flush through. Records whether the flushing thread held the broker gate.</summary>
    private sealed class HeldFlushes(object gate) : DurabilityOperations
    {
        private readonly object _sync = new();
        private readonly Channel<HeldFlush> _entered = Channel.CreateUnbounded<HeldFlush>();
        private bool _open;

        public sealed class HeldFlush(bool underGate)
        {
            private readonly ManualResetEventSlim _released = new();
            public bool UnderGate { get; } = underGate;
            public void Release() => _released.Set();
            public void Wait()
            {
                if (!_released.Wait(StepBound)) throw new TimeoutException("A held durable flush was never released.");
            }
        }

        public Task<HeldFlush> NextAsync() => _entered.Reader.ReadAsync().AsTask();

        public void Open()
        {
            lock (_sync) _open = true;
            while (_entered.Reader.TryRead(out var flush)) flush.Release();
        }

        public override void Flush(Stream stream)
        {
            HeldFlush? held = null;
            lock (_sync)
                if (!_open)
                {
                    held = new HeldFlush(Monitor.IsEntered(gate));
                    _entered.Writer.TryWrite(held);
                }
            held?.Wait();
            base.Flush(stream);
        }
    }
}
