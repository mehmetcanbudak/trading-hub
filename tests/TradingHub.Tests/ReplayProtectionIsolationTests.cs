using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Replay;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

public sealed class ReplayProtectionIsolationTests
{
    private static readonly DateTime Start = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Cancelling_one_replay_cannot_poison_kept_protection_adoption_in_another_replay_or_normal_simulation()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-replay-protection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var instrument = new Instrument { Symbol = "PROTECTION", FeedId = "fixture", TickSize = 1, VolumeStep = 1, PointValue = 10 };
            using (var writer = new TickFile.Writer(TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start)), instrument))
            {
                writer.Write(Tick(1, 0, 100));
                writer.Write(Tick(2, 1, 100));
                writer.Write(Tick(3, 2, 100));
            }
            await using var alerts = new AlertCenter(Path.Combine(root, "alerts.json"), new NoSecrets());
            using var hub = new MarketDataHub();
            using var replays = new ReplaySessions(hub, root, Path.Combine(root, "results"), alerts);
            var selection = ReplaySelection.Selected(instrument, Start, Start.AddSeconds(5), 23);
            var first = replays.Open(selection, 1000, new ReplayFees(), TimeSpan.FromMinutes(1));
            var second = replays.Open(selection, 1000, new ReplayFees(), TimeSpan.FromMinutes(1));
            Assert.NotNull(second.Account.Position);
            using var normalFeed = new ReplayPracticeTests.ManualFeed(instrument, Tick(1, 0, 100));
            using var normalData = new InstrumentData(instrument, normalFeed);
            normalData.Start(Start.AddMinutes(-1));
            await UntilAsync(() => !first.Data.IsLoadingHistory && !second.Data.IsLoadingHistory && !normalData.IsLoadingHistory,
                () => { hub.PumpAll(); normalData.Pump(); });
            using var normal = new SimBroker(normalData, new Portfolio { BeginBalance = 1000, Balance = 1000 }, new TradingStatistics())
                { Name = "Normal simulation protection fixture" };
            Assert.NotNull(normal.Position);
            using var firstRunner = Runner(first.Data, first.Account);
            using var secondRunner = Runner(second.Data, second.Account);
            using var normalRunner = Runner(normalData, normal);
            firstRunner.Start();
            secondRunner.Start();
            normalRunner.Start();
            first.Feed!.SeekForward(Start.AddSeconds(1));
            second.Feed!.SeekForward(Start.AddSeconds(1));
            normalFeed.Emit(Tick(2, 1, 100));
            await UntilAsync(() => firstRunner.OwnPosition == 1 && secondRunner.OwnPosition == 1 && normalRunner.OwnPosition == 1,
                () => { hub.PumpAll(); normalData.Pump(); });

            var firstStop = Assert.Single(first.Account.ActiveOrders, order => order.Type == OrderTypes.Stop);
            var secondStop = Assert.Single(second.Account.ActiveOrders, order => order.Type == OrderTypes.Stop);
            var secondTarget = Assert.Single(second.Account.ActiveOrders, order => order.Type == OrderTypes.Limit);
            var normalStop = Assert.Single(normal.ActiveOrders, order => order.Type == OrderTypes.Stop);
            var normalTarget = Assert.Single(normal.ActiveOrders, order => order.Type == OrderTypes.Limit);
            Assert.EndsWith(":2", firstStop.ExchangeOrderId);
            Assert.EndsWith(":2", secondStop.ExchangeOrderId);
            Assert.NotEqual(firstStop.ExchangeOrderId, secondStop.ExchangeOrderId);

            firstRunner.Stop(flatten: false, keepProtection: false);
            Assert.Empty(first.Account.ActiveOrders);
            Assert.Equal(OrderStatus.Canceled, firstStop.Status);
            Assert.Equal(OrderStates.Active, secondStop.State);
            Assert.Equal(OrderStates.Active, normalStop.State);

            secondRunner.Stop(flatten: false, keepProtection: true);
            normalRunner.Stop(flatten: false, keepProtection: true);
            var secondIds = secondRunner.ProtectionOrderIds.ToArray();
            var normalIds = normalRunner.ProtectionOrderIds.ToArray();
            Assert.Equal(2, secondIds.Length);
            Assert.Equal(2, normalIds.Length);

            using var secondResumed = Runner(second.Data, second.Account, secondIds);
            secondResumed.RestoreLocalState(secondRunner.CaptureLocalState());
            secondResumed.Start();
            // This is the process-global cancellation consumer: the other replay's cancellation must not exclude these handles.
            Assert.Equal(2, secondResumed.ActiveOrders.Count(order => order.IsProtective));
            Assert.Equal(secondIds.Order(StringComparer.Ordinal), secondResumed.ProtectionOrderIds.Order(StringComparer.Ordinal));
            Assert.Same(secondStop, Assert.Single(second.Account.ActiveOrders, order => order.Type == OrderTypes.Stop));
            Assert.Same(secondTarget, Assert.Single(second.Account.ActiveOrders, order => order.Type == OrderTypes.Limit));
            Assert.Equal(2, second.Account.ActiveOrders.Count());

            secondResumed.Stop(flatten: false, keepProtection: false);
            Assert.Empty(second.Account.ActiveOrders);
            Assert.Equal(OrderStates.Active, normalStop.State);
            Assert.Equal(OrderStates.Active, normalTarget.State);
            using var normalResumed = Runner(normalData, normal, normalIds);
            normalResumed.RestoreLocalState(normalRunner.CaptureLocalState());
            normalResumed.Start();
            Assert.Equal(2, normalResumed.ActiveOrders.Count(order => order.IsProtective));
            Assert.Equal(normalIds.Order(StringComparer.Ordinal), normalResumed.ProtectionOrderIds.Order(StringComparer.Ordinal));
            Assert.Same(normalStop, Assert.Single(normal.ActiveOrders, order => order.Type == OrderTypes.Stop));
            Assert.Same(normalTarget, Assert.Single(normal.ActiveOrders, order => order.Type == OrderTypes.Limit));
            Assert.Equal(2, normal.ActiveOrders.Count());

            normalFeed.Emit(Tick(3, 2, 90));
            normalData.Pump();
            Assert.Equal(0m, normal.Position.Volume);
            Assert.Equal(0m, normalResumed.OwnPosition);
            Assert.Empty(normal.ActiveOrders);
            Assert.Equal(1m, first.Account.Position!.Volume);
            Assert.Equal(1m, second.Account.Position.Volume);
            Assert.Single(first.Account.Journal);
            Assert.Single(second.Account.Journal);
            var orders = first.Account.Orders.Concat(second.Account.Orders).Concat(normal.Orders).ToArray();
            Assert.Equal(orders.Length, orders.Select(order => order.Id).Distinct().Count());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(-10, -11)]
    [InlineData(-10, 0)]
    [InlineData(0, -1)]
    public async Task Actual_runner_uses_known_zero_and_negative_marks_and_known_entry_costs(decimal entry, decimal mark)
    {
        await RecordedAsync(new[] { Tick(1, 0, entry), Tick(2, 1, mark), Tick(3, 4, mark) }, new ReplayFees(1, 0), async (run, hub) =>
        {
            using var runner = new LiveStrategyRunner(new UnprotectedEntry(), run.Data, run.Account, ChartPeriod.M1) { Persist = false };
            runner.Start();
            run.Feed!.SeekForward(Start.AddTicks(1));
            await UntilAsync(() => runner.OwnPosition == 1, hub.PumpAll);
            var fill = Assert.Single(runner.Ledger.Fills);
            Assert.Equal(entry, fill.Price);
            Assert.Equal(Start, fill.Time);
            Assert.Equal(1m, Assert.Single(fill.Fees!).Amount);
            Assert.True(runner.Ledger.CostsComplete);
            run.Feed.SeekForward(Start.AddSeconds(1));
            await UntilAsync(() => run.ClockUtc >= Start.AddSeconds(1), hub.PumpAll);
            decimal open = (mark - entry) * 10;
            Assert.True(run.Account.HasObservedPrice);
            Assert.Equal(open, run.Account.Position!.UnrealizedPnL);
            Assert.Equal(open, runner.OpenPnL);
            Assert.Equal(-1m + open, runner.TodayPnL);
            Assert.Equal(Math.Max(0, 1 - open), runner.CurrentDrawdown);
            Assert.Equal(run.Account.Portfolio!.RealizedPnL, runner.Ledger.Realized);
        }, includeProvenance: true);
    }

    [Fact]
    public async Task Period_replacement_preserves_owned_ledger_protection_and_truthful_fills_during_a_stopped_gap()
    {
        await RecordedAsync(new[] { Tick(1, 0, 100), Tick(2, 1, 110), Tick(3, 2, 104), Tick(4, 4, 100) },
            new ReplayFees(1, 0.001m), async (run, hub) =>
        {
            using var original = new LiveStrategyRunner(new StopEntry(), run.Data, run.Account, ChartPeriod.M1) { Persist = false };
            original.Start();
            run.Feed!.SeekForward(Start.AddTicks(1));
            await UntilAsync(() => run.Account.ActiveOrders.Count() == 1, hub.PumpAll);
            run.Feed.SeekForward(Start.AddSeconds(1));
            await UntilAsync(() => original.OwnPosition == 1, hub.PumpAll);
            var entry = Assert.Single(original.Ledger.Fills);
            Assert.Equal(110m, original.Ledger.AveragePrice);
            Assert.Equal(Start.AddSeconds(1), entry.Time);
            Assert.Equal(2.10m, Assert.Single(entry.Fees!).Amount);
            var stop = Assert.Single(run.Account.ActiveOrders, o => o.Type == OrderTypes.Stop);
            var target = Assert.Single(run.Account.ActiveOrders, o => o.Type == OrderTypes.Limit);
            Assert.Equal(105m, stop.TriggerPrice);
            Assert.Equal(120m, target.Price);
            original.Stop(flatten: false, keepProtection: true);
            var ids = original.ProtectionOrderIds.ToArray();
            var local = original.CaptureLocalState();
            using var replacement = new LiveStrategyRunner(new StopEntry(true), run.Data, run.Account, ChartPeriod.M5)
                { Persist = false, ProtectOnStart = true, AdoptProtectionIds = ids };
            replacement.RestoreLocalState(local);
            replacement.Start();
            Assert.Same(original.Ledger, replacement.Ledger);
            Assert.Single(replacement.Ledger.Fills);
            Assert.Equal(ids.Order(StringComparer.Ordinal), replacement.ProtectionOrderIds.Order(StringComparer.Ordinal));
            Assert.Same(stop, Assert.Single(run.Account.ActiveOrders, o => o.Type == OrderTypes.Stop));
            Assert.Same(target, Assert.Single(run.Account.ActiveOrders, o => o.Type == OrderTypes.Limit));
            replacement.Stop(flatten: false, keepProtection: true);
            var gapState = replacement.CaptureLocalState();
            var gapIds = replacement.ProtectionOrderIds.ToArray();
            run.Feed.SeekForward(Start.AddSeconds(2));
            await UntilAsync(() => run.Account.Position!.Volume == 0, hub.PumpAll);
            Assert.Single(replacement.Ledger.Fills); // Stopped runner has not invented a settlement.
            using var resumed = new LiveStrategyRunner(new StopEntry(true), run.Data, run.Account, ChartPeriod.M1)
                { Persist = false, ProtectOnStart = true, AdoptProtectionIds = gapIds };
            resumed.RestoreLocalState(gapState);
            resumed.Start();
            Assert.Equal(0m, resumed.OwnPosition);
            Assert.Empty(run.Account.ActiveOrders);
            Assert.Equal(2, resumed.Ledger.Fills.Count);
            Assert.Equal(104m, resumed.Ledger.Fills[1].Price);
            Assert.Equal(Start.AddSeconds(2), resumed.Ledger.Fills[1].Time);
            Assert.Equal(2.04m, Assert.Single(resumed.Ledger.Fills[1].Fees!).Amount);
            var closed = Assert.Single(resumed.Ledger.Trades);
            Assert.Equal(4.14m, closed.Commission);
            Assert.Equal(-64.14m, closed.NetPnl);
            Assert.Equal(-64.14m, resumed.Ledger.Realized);
            Assert.Equal(run.Account.Portfolio!.RealizedPnL, resumed.Ledger.Realized);
            Assert.Equal(935.86m, resumed.Ledger.Curve(1000)[^1].Equity);
        });
    }

    [Fact]
    public async Task Incompatible_late_interval_pauses_new_entries_without_corrupting_current_bar_or_cancelling_protection()
    {
        var compact = new Trade(-1, Start, 500, 1, TradeSide.Buy,
            new TradeProvenance(DataQuality.Compact, ResolutionSeconds: 300, SourceIdentity: "compact-fixture"));
        await RecordedAsync(new[] { Tick(1, 0, 100), Tick(2, 60, 101), compact, Tick(3, 301, 102) }, new ReplayFees(), async (run, hub) =>
        {
            using var runner = Runner(run.Data, run.Account);
            runner.Start();
            run.Feed!.SeekForward(Start.AddTicks(1));
            await UntilAsync(() => runner.OwnPosition == 1, hub.PumpAll);
            run.Feed.SeekForward(Start.AddSeconds(60));
            await UntilAsync(() => run.ClockUtc >= Start.AddSeconds(60), hub.PumpAll);
            decimal close = runner.FormingBar!.Close;
            int bars = runner.Bars.Count;
            var protection = run.Account.ActiveOrders.ToArray();
            run.Feed.SeekForward(Start.AddSeconds(300));
            await UntilAsync(() => run.ClockUtc >= Start.AddSeconds(300), hub.PumpAll);
            Assert.True(runner.Paused);
            Assert.Equal(close, runner.FormingBar!.Close);
            Assert.Equal(bars, runner.Bars.Count);
            Assert.Equal(protection, run.Account.ActiveOrders);
            Assert.Equal(1m, runner.OwnPosition);
            Assert.Single(run.Account.Journal);
            Assert.NotEmpty(runner.LogLines);
        }, includeProvenance: true);
    }

    [Fact]
    public async Task Actual_live_consumer_preserves_source_qualified_opaque_identities_and_deduplicates_overlap()
    {
        var instrument = new Instrument { Symbol = "IDENTITY", FeedId = "fixture", TickSize = 1, VolumeStep = 1 };
        using var feed = new ReplayPracticeTests.ManualFeed(instrument, Tick(7, 0, 100));
        using var data = new InstrumentData(instrument, feed);
        data.Start(Start.AddMinutes(-1));
        await UntilAsync(() => !data.IsLoadingHistory, data.Pump);
        using var account = new SimBroker(data, new Portfolio { BeginBalance = 1000, Balance = 1000 }, new TradingStatistics());
        var strategy = new CountEvents();
        using var runner = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1) { Persist = false };
        runner.Start();
        var first = new Trade(1, Start.AddSeconds(1), 101, 1, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "source-a", SourceEventId: "native-event"));
        var second = new Trade(1, Start.AddSeconds(2), 102, 1, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "source-b", SourceEventId: "native-event"));
        feed.Emit(first);
        data.Pump();
        feed.Emit(second);
        data.Pump();
        feed.Emit(first);
        data.Pump();
        Assert.Equal(new[] { "source-a", "source-b" }, strategy.Seen);
    }

    private static async Task RecordedAsync(IReadOnlyList<Trade> tape, ReplayFees fees, Func<ReplaySession, MarketDataHub, Task> exercise,
        bool includeProvenance = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-confirmed-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var instrument = new Instrument { Symbol = "CONFIRMED", FeedId = "fixture", TickSize = 1, VolumeStep = 1, PointValue = 10, QuoteAsset = "USD" };
            using (var writer = new TickFile.Writer(TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start)), instrument,
                includeProvenance: includeProvenance))
                foreach (var trade in tape.OrderBy(t => t.Time).ThenBy(t => t.AvailableAt)) writer.Write(trade);
            await using var alerts = new AlertCenter(Path.Combine(root, "alerts.json"), new NoSecrets());
            using var hub = new MarketDataHub();
            using var replays = new ReplaySessions(hub, root, Path.Combine(root, "results"), alerts);
            var run = replays.Open(ReplaySelection.Selected(instrument, Start, tape.Max(t => t.AvailableAt).AddSeconds(2), 27),
                1000, fees, TimeSpan.FromMinutes(1));
            await UntilAsync(() => !run.Data.IsLoadingHistory, hub.PumpAll);
            await exercise(run, hub);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class UnprotectedEntry : Strategy
    {
        private bool _entered;
        protected override void OnTick(Trade trade) { if (!_entered) { _entered = true; Buy(1); } }
    }
    private sealed class StopEntry : Strategy
    {
        private bool _entered;
        public StopEntry(bool entered = false) { _entered = entered; StopLossTicks = 5; TakeProfitTicks = 10; }
        protected override void OnTick(Trade trade) { if (!_entered) { _entered = true; BuyStop(105, 1); } }
    }
    private sealed class CountEvents : Strategy
    {
        public List<string?> Seen { get; } = new();
        protected override void OnTick(Trade trade) => Seen.Add(trade.Provenance.SourceIdentity);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stopped_gap_merges_all_owned_exits_chronologically_and_rebuilds_late_corrected_risk(bool withholdTarget, bool equalUtc)
    {
        var tape = new[] {
            Tick(1, 0, 100),
            new Trade(2, Start.AddSeconds(1), 120, 1, TradeSide.Buy),
            new Trade(3, Start.AddSeconds(equalUtc ? 1 : 2), 90, 1, TradeSide.Buy),
            Tick(4, 4, 100) };
        await RecordedAsync(tape, new ReplayFees(PerUnit: 1), async (run, hub) =>
        {
            using var facts = new CorrectableExecutions(run.Account) { WithholdTarget = withholdTarget };
            using var original = new LiveStrategyRunner(new DoubleEntry(), run.Data, facts, ChartPeriod.M1) { Persist = false };
            var feed = run.Feed;
            Assert.NotNull(feed);
            original.Start();
            feed.SeekForward(Start.AddTicks(1));
            await UntilAsync(() => original.OwnPosition == 2, hub.PumpAll);
            original.Stop(flatten: false, keepProtection: true);
            var state = original.CaptureLocalState();
            string[] kept = original.ProtectionOrderIds.ToArray();
            Assert.Equal(2, kept.Length);

            // Target was submitted after the stop, but executes first. Equal UTC additionally
            // requires the replay account's confirmed global sequence, not dictionary order.
            feed.SeekForward(Start.AddSeconds(2));
            await UntilAsync(() => run.Account.Position!.Volume == 0, hub.PumpAll);
            Assert.Equal(3, run.Account.Journal.Count);
            Assert.Single(run.Account.ActiveOrders);
            using var resumed = new LiveStrategyRunner(new DoubleEntry(entered: true), run.Data, facts, ChartPeriod.M1) {
                Persist = false, ProtectOnStart = true, AdoptProtectionIds = kept };
            resumed.RestoreLocalState(state);
            resumed.Start();
            if (withholdTarget)
            {
                Assert.Equal(0m, resumed.OwnPosition); // Native quantity is known even while one execution's economics are withheld.
                Assert.True(resumed.Ledger.HasPendingPrices);
                Assert.Equal(2, resumed.Ledger.Fills.Count);
                Assert.Single(run.Account.ActiveOrders);
                facts.RevealTarget();
            }
            Assert.Equal(0m, resumed.OwnPosition);
            Assert.Empty(run.Account.ActiveOrders);
            Assert.Equal(new[] { 100m, 120m, 90m }, resumed.Ledger.Fills.Select(fill => fill.Price));
            Assert.Equal(new[] { 1L, 2L, 3L }, resumed.Ledger.Fills.Select(fill => fill.ExecutionSequence!.Value));
            Assert.Equal(new[] { 2m, 1m, 1m }, resumed.Ledger.Fills.Select(fill => Assert.Single(fill.Fees!).Amount));
            Assert.Equal(96m, resumed.Ledger.Realized);
            Assert.Equal(197m, resumed.Ledger.Curve(0).Max(point => point.Equity));
            Assert.Equal(101m, resumed.CurrentDrawdown);
            Assert.Equal(1096m, resumed.Ledger.Curve(1000).Last().Equity);
            Assert.Equal(101m, resumed.Ledger.Curve(1000).Last().Drawdown);

            // Provider corrections are execution facts, not synthetic native re-matching:
            // changing UTC must reorder the existing ID and recompute peak/curve/closed trades.
            string target = resumed.Ledger.Fills.Single(fill => fill.Price == 120).ExecutionId!.Id;
            facts.Correct(target, execution => execution with { Time = Start.AddSeconds(3), Sequence = null });
            Assert.Equal(new[] { 100m, 90m, 120m }, resumed.Ledger.Fills.Select(fill => fill.Price));
            Assert.Equal(new[] { 90m, 120m }, resumed.Ledger.Trades.Select(trade => trade.ExitPrice));
            Assert.Equal(96m, resumed.Ledger.Realized);
            Assert.Equal(96m, resumed.Ledger.Curve(0).Max(point => point.Equity));
            Assert.Equal(0m, resumed.CurrentDrawdown);
            Assert.Equal(0m, resumed.Ledger.Curve(1000).Last().Drawdown);

            string entry = resumed.Ledger.Fills[0].ExecutionId!.Id;
            facts.Correct(entry, execution => execution with { Fees = new[] { new ExecutionFee(3, "USD") }, FeesComplete = true });
            Assert.Equal(3, resumed.Ledger.Fills.Count);
            Assert.Equal(5m, resumed.Ledger.Fees);
            Assert.Equal(95m, resumed.Ledger.Realized);
            Assert.Equal(95m, resumed.Ledger.Trades.Sum(trade => trade.NetPnl));
            Assert.Equal(95m, resumed.Ledger.Curve(0).Max(point => point.Equity));
            Assert.Equal(1095m, resumed.Ledger.Curve(1000).Last().Equity);
            Assert.Equal(0m, resumed.CurrentDrawdown);
            Assert.Equal(3, run.Account.Journal.Count);
            Assert.Equal(0m, run.Account.Position!.Volume);
        });
    }

    private static LiveStrategyRunner Runner(InstrumentData data, ITradingAccount account, string[]? kept = null) =>
        new(new EnterOnce(alreadyEntered: kept != null), data, account, ChartPeriod.M1) {
            Persist = false, ProtectOnStart = kept != null, AdoptProtectionIds = kept ?? Array.Empty<string>() };
    private static Trade Tick(long id, int seconds, decimal price) => new(id, Start.AddSeconds(seconds), price, 10, TradeSide.Buy);
    private static async Task UntilAsync(Func<bool> ready, Action pump)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) { deadline.Token.ThrowIfCancellationRequested(); pump(); await Task.Delay(5, deadline.Token); }
    }
    private sealed class EnterOnce : Strategy
    {
        private bool _entered;
        public EnterOnce(bool alreadyEntered = false) { _entered = alreadyEntered; StopLossTicks = 5; TakeProfitTicks = 10; }
        protected override void OnTick(Trade trade)
        {
            if (_entered) return;
            _entered = true;
            Buy(1);
        }
    }
    private sealed class DoubleEntry : Strategy
    {
        private bool _entered;
        public DoubleEntry(bool entered = false) { _entered = entered; StopLossTicks = 5; TakeProfitTicks = 20; }
        protected override void OnTick(Trade trade)
        {
            if (_entered) return;
            _entered = true;
            Buy(2);
        }
    }
    // The real replay account performs all matching/routing. Only confirmed provider metadata is
    // withheld/corrected, exercising the actual runner's incomplete/late-fact consumer boundary.
    private sealed class CorrectableExecutions : ITradingAccount, IObservedMarketPrice, IDisposable
    {
        private readonly ReplayAccount _inner;
        private readonly ExecutionLog _source = new();
        private readonly Dictionary<string, ExecutionReport> _known = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionReport> _corrections = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionReport> _published = new(StringComparer.Ordinal);
        public CorrectableExecutions(ReplayAccount inner) { _inner = inner; inner.Changed += Relay; }
        public bool WithholdTarget { get; init; }
        private bool _revealed;
        public void RevealTarget() { _revealed = true; Relay(); }
        public void Correct(string id, Func<ExecutionReport, ExecutionReport> correction)
        {
            _corrections[id] = correction(_corrections.GetValueOrDefault(id, _known[id]));
            Relay();
        }
        public OrderContext OrderContext => _inner.OrderContext;
        public IExecutionSource Executions => _source;
        public Order Submit(PreparedOrderIntent intent) => _inner.Submit(intent);
        public Order Replace(Order order, PreparedOrderIntent intent) => _inner.Replace(order, intent);
        public bool HasObservedPrice => _inner.HasObservedPrice;
        public bool HasKnownPrice => _inner.HasKnownPrice;
        public string Name => _inner.Name;
        public bool IsReal => false;
        public Instrument Instrument => _inner.Instrument;
        public Position Position => _inner.Position!;
        public IEnumerable<Order> ActiveOrders => _inner.ActiveOrders;
        public string? LastError => _inner.LastError;
        public decimal LastPrice => _inner.LastPrice;
        public event Action? Changed;
        private void Relay()
        {
            bool incomplete = false;
            foreach (var original in _inner.Executions.Capture(ExecutionCursor.Start).Changes)
            {
                string id = original.Identity.Id;
                _known[id] = original;
                if (WithholdTarget && !_revealed && OrderContext.Find(original.IntentId)?.Request.Type == OrderTypes.Limit)
                { incomplete = true; continue; }
                var report = _corrections.GetValueOrDefault(id, original);
                if (_published.TryGetValue(id, out var previous) && previous == report) continue;
                _published[id] = report;
                _source.Publish(report);
            }
            _source.SetProducerAvailability(this, incomplete ? ExecutionAvailability.Synchronizing : ExecutionAvailability.Ready);
            Changed?.Invoke();
        }
        public void Cancel(Order order) => _inner.Cancel(order);
        public void CancelAt(decimal price) => _inner.CancelAt(price);
        public void CancelAll() => _inner.CancelAll();
        public void Modify(Order order, decimal price) => _inner.Modify(order, price);
        public void Flatten() => _inner.Flatten();
        public void Reverse() => _inner.Reverse();
        public void Dispose() => _inner.Changed -= Relay;
    }
    private sealed class NoSecrets : ISecretStore
    {
        public string Get(string account) => "";
        public void Set(string account, string? secret) { }
    }
}
