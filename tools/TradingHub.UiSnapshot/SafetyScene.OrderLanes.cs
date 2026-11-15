// "safety" scene part: order gestures never wait on durable storage on the UI thread. A Chart Trader order on a native
// (thread-safe) account against a local fake Binance endpoint, with every durable flush slowed through the storage seam:
// the UI thread returns at once and keeps processing input while the unchanged account call journals off it, the
// status line shows the gesture pending, and no durable flush runs on the UI thread. Fake endpoint and fixture data only.
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using TradingHub.App;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Common;
using TradingHub.Feeds.Binance;
using TradingHub.Tests;
using TradingHub.Market;
using TradingHub.Trading;

internal static partial class SafetyScene
{
    /// <summary>Slows each durable flush and records which thread waited for it.</summary>
    private sealed class SlowDurability(TimeSpan delay) : DurabilityOperations
    {
        private int _ui, _other;
        public int UiThreadFlushes => Volatile.Read(ref _ui);
        public int OtherFlushes => Volatile.Read(ref _other);

        public override void Flush(Stream stream)
        {
            Interlocked.Increment(ref Dispatcher.UIThread.CheckAccess() ? ref _ui : ref _other);
            Thread.Sleep(delay); // the injected slow drive, not synchronization
            base.Flush(stream);
        }
    }

    private static IDisposable UseDurability(DurabilityOperations operations) =>
        (IDisposable)typeof(DurableFile).GetMethod("Use", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [operations])!;

    private static void OrderGesturesOffUiThread(MainWindow window, Action<double> pump, TradingView tv, ChartTab chart,
        Dictionary<string, ITradingAccount> cache, Action<bool, string> check, Func<string> status)
    {
        var svc = App.Services;
        using var binance = new FakeHttpServer();
        binance.OnHttp = r => r.Route switch
        {
            "/fapi/v1/time" => (200, $"{{\"serverTime\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}"),
            "/fapi/v1/openOrders" or "/fapi/v1/openAlgoOrders" or "/fapi/v2/positionRisk" => (200, "[]"),
            _ => (400, "{\"code\":-1000,\"msg\":\"safety fixture: orders are refused\"}"),
        };
        string? previousRest = Environment.GetEnvironmentVariable("TRADINGHUB_BINANCE_REST");
        Environment.SetEnvironmentVariable("TRADINGHUB_BINANCE_REST", binance.Http);
        var profile = new ConnectionProfile { Id = "safety-lanes", Kind = ConnectionKind.BinanceFutures, Name = "Fixture lanes", Testnet = true, AllowRealOrders = true, ApiKey = "fixture-lanes" };
        svc.Connections.Profiles.Add(profile);
        svc.Connections.SetSecret(profile, "fixture-secret");
        try
        {
            tv.SetChartAccount(chart, profile.Id);
            var until = DateTime.UtcNow.AddSeconds(15);
            ITradingAccount? account;
            while (((account = tv.AccountOf(chart)) is not { PositionReady: true, OrdersReady: true }) && DateTime.UtcNow < until) pump(0.05);
            check(account is BinanceBroker { IsReal: false, PositionReady: true, OrdersReady: true },
                $"the chart trades a native thread-safe account on the local fake endpoint ({account?.Name})");
            if (account is not BinanceBroker broker) return;
            decimal price = broker.Instrument.RoundPrice(Math.Max(broker.Instrument.TickSize, chart.Model!.Data.LastPrice * 0.5m));

            var delay = TimeSpan.FromSeconds(1.5);
            var slow = new SlowDurability(delay);
            // Dispatcher responsiveness: a background timer posts an input-priority job every 50 ms; each job records how
            // long it waited for the UI thread. A UI thread stuck in a durable write leaves them all waiting.
            var waits = new System.Collections.Concurrent.ConcurrentQueue<double>();
            using var ticker = new Timer(_ =>
            {
                long posted = Stopwatch.GetTimestamp();
                Dispatcher.UIThread.Post(() => waits.Enqueue(Stopwatch.GetElapsedTime(posted).TotalSeconds), DispatcherPriority.Input);
            }, null, 0, 50);
            pump(0.3);

            var call = Stopwatch.StartNew();
            Task<Order?> trade;
            using (UseDurability(slow)) trade = tv.TradeAsync(chart, OrderTypes.Limit, OrderDirections.Buy, price, 1);
            double handOff = call.Elapsed.TotalSeconds;
            string pendingStatus = status();
            var pendingLabels = svc.OrderLanes.Pending;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!trade.IsCompleted && DateTime.UtcNow < deadline) pump(0.02);
            ticker.Change(Timeout.Infinite, Timeout.Infinite);
            pump(0.2);
            double worstLatency = waits.DefaultIfEmpty().Max();
            Console.WriteLine($"  gesture hand-off {handOff:0.000}s, worst dispatcher wait {worstLatency:0.000}s ({waits.Count} probes), flushes UI {slow.UiThreadFlushes} / off-UI {slow.OtherFlushes}, total {call.Elapsed.TotalSeconds:0.000}s");

            check(trade.IsCompletedSuccessfully && trade.Result is { IntentId: { Length: > 0 } },
                $"the gesture completes through the unchanged account call ({trade.Status}, {trade.Exception?.GetBaseException()?.Message})");
            check(slow.OtherFlushes > 0, $"the gesture's durable writes went through the slowed storage seam ({slow.OtherFlushes} off the UI thread)");
            check(slow.UiThreadFlushes == 0, $"no durable flush ran on the UI thread ({slow.UiThreadFlushes})");
            check(handOff < delay.TotalSeconds / 3, $"the UI thread returned from the gesture in {handOff:0.000}s while each flush took {delay.TotalSeconds:0.0}s");
            check(worstLatency < delay.TotalSeconds / 3, $"the UI thread kept processing input during the durable writes (worst wait {worstLatency:0.000}s)");
            check(pendingLabels.Count == 1 && pendingStatus == Lang.T(pendingLabels[0] + ": pending…"),
                $"while journaling, the gesture is shown pending in the status line («{pendingStatus}», {pendingLabels.Count} pending)");
            check(svc.OrderLanes.Pending.Count == 0, "the lane is empty once the gesture returned");

            // Measurement only: how long one gesture holds the UI thread on this native account, and on the simulated
            // account, which stays UI-thread-affine and journals inline. The live strategy runner places its orders
            // through OrderActions.Place with its ledger owner on the UI thread (its synchronization context); that call
            // is measured directly on the simulated account, without the runner's own LiveStore writes.
            (double Worst, int UiFlushes) Hold(Func<Task> gesture, int samples, DurabilityOperations? storage = null)
            {
                double worst = 0;
                for (int i = 0; i < samples; i++)
                {
                    var started = Stopwatch.StartNew();
                    Task sent;
                    using (storage == null ? null : UseDurability(storage)) sent = gesture();
                    worst = Math.Max(worst, started.Elapsed.TotalSeconds);
                    var settle = DateTime.UtcNow.AddSeconds(30);
                    while (!sent.IsCompleted && DateTime.UtcNow < settle) pump(0.02);
                }
                return (worst, (storage as SlowDurability)?.UiThreadFlushes ?? 0);
            }
            Task ChartGesture() => tv.TradeAsync(chart, OrderTypes.Limit, OrderDirections.Buy, price, 1);
            var native = Hold(ChartGesture, 5);
            // the call the gesture made on the UI thread before order lanes: the same account call, inline
            Task InlineNative() => Task.FromResult(OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, price));
            var inline = Hold(InlineNative, 5);
            var inlineSlow = Hold(InlineNative, 1, new SlowDurability(delay));
            broker.CancelAll();
            pump(0.3);
            tv.SetChartAccount(chart, "sim");
            pump(0.3);
            var simulated = Hold(ChartGesture, 5);
            var simulatedSlow = Hold(ChartGesture, 1, new SlowDurability(delay));
            if (tv.AccountOf(chart) is { } sim)
            {
                var owner = sim.OrderContext.LedgerOwner("safety-lanes-runner-path", new Guid("5afe1a5e-0000-4000-8000-000000000001"));
                Task RunnerPlace() => Task.FromResult(OrderActions.Place(sim, owner, OrderTypes.Limit, OrderDirections.Buy, 1, price));
                var runner = Hold(RunnerPlace, 5);
                var runnerSlow = Hold(RunnerPlace, 1, new SlowDurability(delay));
                sim.CancelAll();
                Console.WriteLine($"  UI-thread hold per order (worst of 5, real storage): native gesture {native.Worst * 1000:0.0} ms, " +
                    $"native call inline (before lanes) {inline.Worst * 1000:0.0} ms, simulated gesture {simulated.Worst * 1000:0.0} ms, " +
                    $"runner-path place on simulated {runner.Worst * 1000:0.0} ms");
                Console.WriteLine($"  UI-thread hold per order ({delay.TotalSeconds:0.0} s flushes): native call inline (before lanes) {inlineSlow.Worst:0.000}s " +
                    $"({inlineSlow.UiFlushes} UI flushes), simulated gesture {simulatedSlow.Worst:0.000}s " +
                    $"({simulatedSlow.UiFlushes} UI flushes), runner-path place on simulated {runnerSlow.Worst:0.000}s ({runnerSlow.UiFlushes} UI flushes)");
            }
            pump(0.3);
        }
        finally
        {
            tv.SetChartAccount(chart, "sim");
            foreach (var key in cache.Keys.Where(k => k.StartsWith(profile.Id + "|", StringComparison.Ordinal)).ToList())
            {
                (cache[key] as IDisposable)?.Dispose();
                cache.Remove(key);
            }
            svc.Connections.SetSecret(profile, null);
            svc.Connections.Profiles.Remove(profile);
            Environment.SetEnvironmentVariable("TRADINGHUB_BINANCE_REST", previousRest);
            pump(0.2);
        }
    }

    /// <summary>
    /// A gesture run in a native account's lane always replaces its '&lt;label&gt;: pending…' line: with the label when the
    /// account call returns no message, with the failure when it throws, and with the error the account reported for
    /// that gesture, read in the lane before a later queued gesture can clear it. Fixture account; no order is routed.
    /// </summary>
    private static void GestureOutcomes(Action<double> pump, TradingView tv, ChartTab chart,
        Dictionary<string, ITradingAccount> cache, Action<bool, string> check)
    {
        var svc = App.Services;
        if (tv.AccountOf(chart) is not { } display) { check(false, "the chart has an account for the outcome fixture"); return; }
        var account = new FixtureOutcomeAccount(display);
        const string id = "safety-outcomes";
        string key = id + "|" + (chart.CrossData ?? chart.Model!.Data).Instrument.Key;
        var statusField = typeof(TradingView).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (Action<string>)statusField.GetValue(tv)!;
        var shown = new List<string>(); // the fixture gestures' status lines, in order (UI thread only)
        string previousAccount = chart.AccountId;
        statusField.SetValue(tv, (Action<string>)(text =>
        {
            if (text.StartsWith("Fixture", StringComparison.Ordinal) || text.StartsWith("Remove attached target", StringComparison.Ordinal) ||
                text.StartsWith("Cancel all orders", StringComparison.Ordinal)) shown.Add(text);
            original(text);
        }));
        cache[key] = account; // what AccountFor returns for this account id
        chart.AccountId = id;
        void Settle(Task gesture)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!gesture.IsCompleted && DateTime.UtcNow < until) pump(0.02);
            pump(0.05);
        }
        string Shown() => string.Join(" | ", shown);
        try
        {
            check(ReferenceEquals(tv.AccountOf(chart), account) && OrderActionLanes.RunsOffCaller(account),
                "the chart trades a thread-safe fixture account whose gestures run in its order lane");

            shown.Clear();
            var brackets = svc.BracketsFor(account);
            var remove = (Task)typeof(TradingView).GetMethod("BracketGestureAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(tv, [chart, account, brackets, "Remove attached target", (Func<BracketManager, string>)(_ => "")])!;
            Settle(remove);
            check(remove.IsCompletedSuccessfully && shown.FirstOrDefault() == "Remove attached target: pending…" && shown.LastOrDefault() == "Remove attached target",
                $"a gesture whose account call returns no message replaces its pending line with its label («{Shown()}»)");

            shown.Clear();
            var failing = tv.AccountActionAsync(chart, _ => throw new InvalidOperationException("Fixture broker refused the gesture."), "Fixture failing gesture");
            Settle(failing);
            check(failing.IsCompletedSuccessfully && shown.LastOrDefault() == "Fixture failing gesture failed with an error: Fixture broker refused the gesture.",
                $"a gesture whose account call throws shows the failure instead of staying pending ({failing.Status}, «{Shown()}»)");

            shown.Clear();
            using var durableWrite = new ManualResetEventSlim();
            var held = svc.OrderLanes.RunAsync(account, "Fixture slow durable write", () => durableWrite.Wait(TimeSpan.FromSeconds(30)));
            var refused = tv.AccountActionAsync(chart, a => a.CancelAll(), "Cancel all orders"); // the fixture reports an error
            var clearing = tv.AccountActionAsync(chart, a => a.CancelAt(1m), "Fixture clearing gesture"); // the next one clears it
            durableWrite.Set();
            // Both lane entries finish before the UI thread (not pumped here) runs either gesture's continuation.
            bool drained = SpinWait.SpinUntil(() => svc.OrderLanes.Pending.Count == 0, TimeSpan.FromSeconds(30));
            Settle(held);
            Settle(refused);
            Settle(clearing);
            check(drained && refused.IsCompletedSuccessfully && clearing.IsCompletedSuccessfully &&
                shown.SequenceEqual(new[]
                {
                    "Cancel all orders: pending…", "Fixture clearing gesture: pending…", FixtureOutcomeAccount.CancelAllError,
                    "Fixture clearing gesture requested; check account status for confirmation.",
                }),
                $"each gesture shows the error its own account call reported, even after a later queued gesture cleared it («{Shown()}»)");
        }
        finally
        {
            statusField.SetValue(tv, original);
            chart.AccountId = previousAccount;
            cache.Remove(key);
            pump(0.1);
        }
    }
}

/// <summary>A thread-safe account identity for the quit-drain proof: gestures handed to its lane are fixture actions;
/// it never routes an order.</summary>
internal sealed class FixtureLaneAccount(ITradingAccount display) : ITradingAccount, IThreadSafeOrderEntry
{
    public OrderContext OrderContext { get; } = OrderContext.EphemeralSimulation("SAFETY-LANE");
    public IExecutionSource Executions => OrderContext.Executions;
    public string Name => "Fixture lane";
    public bool IsReal => false;
    public Instrument Instrument => display.Instrument;
    public Position Position => display.Position;
    public IEnumerable<Order> ActiveOrders => Array.Empty<Order>();
    public string? LastError => null;
    public event Action? Changed { add { } remove { } }
    public Order Submit(PreparedOrderIntent intent) => throw new NotSupportedException("The lane fixture routes no orders.");
    public Order Replace(Order order, PreparedOrderIntent intent) => throw new NotSupportedException("The lane fixture routes no orders.");
    public void Cancel(Order order) { }
    public void CancelAt(decimal price) { }
    public void CancelAll() { }
}

/// <summary>A thread-safe fixture account for gesture outcomes: Cancel all reports an error, Cancel at clears it, like a
/// broker whose next action replaces its last error. It never routes an order.</summary>
internal sealed class FixtureOutcomeAccount(ITradingAccount display) : ITradingAccount, IThreadSafeOrderEntry
{
    public const string CancelAllError = "Fixture: the cancellation was not queued.";
    private volatile string? _error;
    public OrderContext OrderContext { get; } = OrderContext.EphemeralSimulation("SAFETY-OUTCOMES");
    public IExecutionSource Executions => OrderContext.Executions;
    public string Name => "Fixture outcomes";
    public bool IsReal => false;
    public Instrument Instrument => display.Instrument;
    public Position Position { get; } = new();
    public IEnumerable<Order> ActiveOrders => Array.Empty<Order>();
    public string? LastError => _error;
    public event Action? Changed { add { } remove { } }
    public Order Submit(PreparedOrderIntent intent) => throw new NotSupportedException("The outcome fixture routes no orders.");
    public Order Replace(Order order, PreparedOrderIntent intent) => throw new NotSupportedException("The outcome fixture routes no orders.");
    public void Cancel(Order order) { }
    public void CancelAt(decimal price) => _error = null;
    public void CancelAll() => _error = CancelAllError;
}
