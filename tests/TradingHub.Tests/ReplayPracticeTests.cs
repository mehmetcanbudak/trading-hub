using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Replay;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

public sealed class ReplayPracticeTests
{
    private static readonly DateTime Start = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
    private static Instrument Instrument(string symbol = "PRACTICE") => new() { Symbol = symbol, FeedId = "fixture", TickSize = 1, VolumeStep = 0.5m, PointValue = 10 };
    private static Trade Tick(int second, decimal price, decimal quantity = 10) => new(second + 1, Start.AddSeconds(second), price, quantity, TradeSide.Buy);

    [Fact]
    public async Task Two_replays_and_normal_simulation_have_independent_portfolios_clocks_fills_and_journals()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var instrument = Instrument();
            using (var writer = new TickFile.Writer(TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start)), instrument))
            { writer.Write(Tick(0, 100)); writer.Write(Tick(1, 110)); writer.Write(Tick(2, 90)); writer.Write(Tick(3, 105)); }
            using var http = new HttpClient(new NoNetwork());
            await using var alerts = new AlertCenter(Path.Combine(root, "alerts.json"), new FixtureSecrets(), http);
            using var hub = new MarketDataHub();
            using var registry = new ReplaySessions(hub, root, Path.Combine(root, "results"), alerts);
            var selection = ReplaySelection.Selected(instrument, Start, Start.AddSeconds(4), 37, blind: true);
            var first = registry.Open(selection, 1000, new ReplayFees(1, 0.001m), TimeSpan.FromMinutes(1));
            var second = registry.Open(selection, 1000, new ReplayFees(1, 0.001m), TimeSpan.FromMinutes(1));
            await UntilAsync(() => !first.Data.IsLoadingHistory && !second.Data.IsLoadingHistory, hub.PumpAll);
            first.Feed!.SeekForward(Start.AddTicks(1));
            second.Feed!.SeekForward(Start.AddTicks(1));
            await UntilAsync(() => first.Account.HasObservedPrice && second.Account.HasObservedPrice, hub.PumpAll);
            Assert.NotEqual(first.Data.Instrument.Key, second.Data.Instrument.Key);
            Assert.NotSame(first.Account.Portfolio, second.Account.Portfolio);
            Assert.Same(first.Account, registry.Resolve(first.Data, first.AccountId));
            Assert.Throws<InvalidOperationException>(() => registry.Resolve(first.Data, "broker-real"));
            Assert.Throws<InvalidOperationException>(() => registry.Resolve(first.Data, second.AccountId));
            Assert.Throws<InvalidOperationException>(() => registry.Resolve(first.Data, "sim"));
            first.Account.Market(OrderDirections.Buy, 1);
            second.Account.Market(OrderDirections.Sell, 2);
            var concealed = registry.Snapshot(first.AccountId);
            Assert.False(concealed.FinancialsKnown);
            Assert.False(concealed.OpenPnlKnown);
            Assert.False(concealed.RealizedPnlKnown);
            Assert.False(concealed.AnalysisKnown);
            Assert.False(concealed.OrdersKnown);
            Assert.True(concealed.PositionsKnown);
            Assert.False(Assert.Single(concealed.Positions).ClosedPnlKnown);
            Assert.Equal(1m, concealed.Positions[0].Net);
            Assert.Equal(100m, concealed.Positions[0].AvgPrice);
            Assert.Empty(concealed.Fills);
            Assert.Empty(concealed.Orders);
            Assert.Equal("Practice", Assert.Single(concealed.Positions).Symbol);
            Assert.Single(first.Account.Journal); // Presentation concealment never removes a booked fill.
            first.Reveal();
            var revealed = registry.Snapshot(first.AccountId);
            Assert.True(revealed.FinancialsKnown);
            Assert.True(revealed.OpenPnlKnown);
            Assert.True(revealed.RealizedPnlKnown);
            Assert.True(revealed.AnalysisKnown);
            Assert.True(revealed.OrdersKnown);
            Assert.True(revealed.PositionsKnown);
            Assert.True(Assert.Single(revealed.Positions).ClosedPnlKnown);
            Assert.Single(revealed.Fills);
            Assert.Equal(Start, revealed.Fills[0].Time);

            using var normalFeed = new ManualFeed(instrument, Tick(0, 100));
            using var normalData = new InstrumentData(instrument, normalFeed);
            normalData.Start(Start.AddMinutes(-1));
            await UntilAsync(() => !normalData.IsLoadingHistory, normalData.Pump);
            var normalPortfolio = new Portfolio { BeginBalance = 1000, Balance = 1000 };
            using var normal = new SimBroker(normalData, normalPortfolio, new TradingStatistics()) { CommissionPerUnit = 0.5m };
            var journal = new AccountJournal(Path.Combine(root, "normal-journal"));
            normal.NewMyTrade += fill => journal.Add("sim", new(fill.Id.ToString(), fill.Time, instrument.Symbol,
                fill.OrderDirection == OrderDirections.Buy, fill.Volume, fill.Price, instrument.PointValue, fill.Commission));
            // History is end-exclusive at Feed.Now; this current print must arrive through the live sink.
            normalFeed.Emit(Tick(0, 100));
            normalData.Pump();
            Assert.Equal(100m, normal.LastPrice);
            normal.Market(OrderDirections.Buy, 1);
            Assert.Equal(101m, Assert.Single(normal.MyTrades).Price);
            normalFeed.Emit(Tick(20, 120));
            normalData.Pump();
            normal.Flatten();

            first.Feed!.SeekForward(Start.AddSeconds(1));
            await UntilAsync(() => first.ClockUtc == Start.AddSeconds(1), hub.PumpAll);
            first.Account.Market(OrderDirections.Sell, 1);
            second.Feed!.SeekForward(Start.AddSeconds(2));
            await UntilAsync(() => second.ClockUtc == Start.AddSeconds(2), hub.PumpAll);
            second.Account.Market(OrderDirections.Buy, 2);
            var a = registry.End(first);
            var b = registry.End(second);
            Assert.Equal(95.9m, a.RealizedNet); // 100 gross less 2.00 entry and 2.10 exit fees
            Assert.Equal(192.2m, b.RealizedNet); // 200 gross less 4.00 entry and 3.80 exit fees
            Assert.Equal(179m, normalPortfolio.RealizedPnL); // (119 - 101) * 10 less two 0.50 fees
            Assert.Equal(new[] { 101m, 119m }, normal.MyTrades.Select(fill => fill.Price));
            Assert.Equal(179m, normal.MyTrades.Sum(fill => fill.RealizedPnL - fill.Commission));
            Assert.Equal(1179m, normalPortfolio.Balance);
            Assert.Equal(1m, normalPortfolio.Commission);
            Assert.Equal(Start.AddSeconds(1), a.ClockUtc);
            Assert.Equal(Start.AddSeconds(2), b.ClockUtc);
            Assert.Equal(37, a.Seed);
            Assert.Equal(2, a.Fills.Count);
            Assert.Equal(2, b.Fills.Count);
            Assert.Equal(2, journal.FillsOf("sim").Count);
            Assert.All(a.Fills, fill => Assert.StartsWith(first.AccountId, fill.Id));
            Assert.All(b.Fills, fill => Assert.StartsWith(second.AccountId, fill.Id));
            Assert.DoesNotContain(journal.FillsOf("sim"), fill => fill.Id.StartsWith("REPLAY", StringComparison.Ordinal));
            Assert.Equal(95.9m, registry.Snapshot(first.AccountId).Analysis.RealizedNet);
            Assert.Equal(192.2m, registry.Snapshot(second.AccountId).Analysis.RealizedNet);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Repeated_selection_is_order_independent_and_blind_reveal_does_not_change_financial_results()
    {
        var days = new[] { new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 9) };
        var a = new ReplayCandidate(Instrument("A"), days);
        var b = new ReplayCandidate(Instrument("B"), days.Reverse().ToArray());
        var first = ReplaySelection.Random(new[] { a, b }, 743, true, 3);
        var repeated = ReplaySelection.Random(new[] { b, a }, 743, true, 3);
        Assert.Equal(first.Source.Key, repeated.Source.Key);
        Assert.Equal(first.StartUtc, repeated.StartUtc);
        Assert.Equal(first.EndUtc, repeated.EndUtc);
        Assert.Equal(new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc), first.EndUtc); // the isolated September 9 day cannot participate
        var selected = ReplaySelection.Selected(Instrument(), Start, Start.AddMinutes(1), 743, true);
        using var one = new ReplaySession(selected, fees: new ReplayFees(1, 0.001m));
        using var two = new ReplaySession(selected, fees: new ReplayFees(1, 0.001m));
        foreach (var run in new[] { one, two }) { run.Advance(Tick(0, 100)); run.Account.Market(OrderDirections.Buy, 1); }
        one.Reveal();
        foreach (var run in new[] { one, two }) { run.Advance(Tick(1, 110)); run.Account.Flatten(); }
        Assert.False(one.IsBlind);
        Assert.True(two.IsBlind);
        Assert.Equal(95.9m, one.Result().RealizedNet);
        Assert.Equal(one.Result().RealizedNet, two.Result().RealizedNet);
        Assert.Equal(one.Account.Journal.Select(f => (f.Time, f.Price, f.Quantity, f.Commission)), two.Account.Journal.Select(f => (f.Time, f.Price, f.Quantity, f.Commission)));
    }

    [Fact]
    public void Quantity_steps_shared_print_volume_reduce_only_and_fees_are_not_silently_rounded_or_reversed()
    {
        using var run = new ReplaySession(ReplaySelection.Selected(Instrument(), Start, Start.AddMinutes(1)), fees: new ReplayFees(1));
        run.Advance(Tick(0, 100));
        Assert.Equal(OrderStatus.Rejected, run.Account.Market(OrderDirections.Buy, 0.75m).Status);
        var first = run.Account.Limit(OrderDirections.Buy, 1, 99);
        var second = run.Account.Limit(OrderDirections.Buy, 1, 99);
        run.Advance(Tick(1, 99, 1.5m));
        Assert.Equal(1m, first.Filled);
        Assert.Equal(0.5m, second.Filled);
        var close = run.Account.Exit(OrderTypes.Market, OrderDirections.Sell, 3, 0);
        Assert.Equal(1.5m, close.Filled);
        Assert.Equal(OrderStatus.Canceled, close.Status);
        Assert.Equal(0m, run.Account.Position!.Volume);
        Assert.Equal(3m, run.Account.Portfolio!.Commission);
        Assert.Equal(-3m, run.Result().RealizedNet);
        run.End();
        Assert.Equal(OrderStatus.Rejected, run.Account.Market(OrderDirections.Buy, 1).Status);
        Assert.Throws<ArgumentException>(() => ReplaySelection.Selected(Instrument(), Start, Start));
    }

    [Fact]
    public void Compact_history_cannot_become_executable_prints_and_zero_or_negative_prices_keep_fee_sign_correct()
    {
        using var run = new ReplaySession(ReplaySelection.Selected(Instrument(), Start, Start.AddMinutes(2)), fees: new ReplayFees(0, 0.001m));
        run.Advance(new Trade(1, Start, 100, 20, TradeSide.None, new TradeProvenance(DataQuality.Compact, 60, "fixture", 7)));
        Assert.Equal(OrderStatus.Rejected, run.Account.Market(OrderDirections.Buy, 1).Status);
        Assert.NotNull(run.DataWarning);
        Assert.Equal(DataQuality.Compact, run.Result().DataQuality);
        Assert.Equal(60, run.Result().ResolutionSeconds);
        Assert.Equal(7, run.Result().CorrectionRevision);
        Assert.Equal(new[] { "fixture" }, run.Result().SourceIdentities);
        Assert.Equal(Start.AddMinutes(1), run.ClockUtc);
        run.Advance(Tick(61, -10));
        run.Account.Market(OrderDirections.Buy, 1);
        run.Advance(Tick(62, 0));
        run.Account.Flatten();
        Assert.Equal(0.1m, run.Result().Commission);
        Assert.Equal(99.9m, run.Result().RealizedNet);
    }

    [Fact]
    public async Task Indicator_trading_manager_orders_keep_their_handle_and_cannot_be_adopted_or_cancelled_by_another_run()
    {
        var selection = ReplaySelection.Selected(Instrument(), Start, Start.AddMinutes(1));
        using var first = new ReplaySession(selection);
        using var second = new ReplaySession(selection);
        first.Advance(Tick(0, 100));
        second.Advance(Tick(0, 100));
        var order = new Order { Direction = OrderDirections.Buy, Type = OrderTypes.Limit, QuantityToFill = 1,
            Price = 99, Portfolio = first.Account.Portfolio! };
        await first.Account.OpenOrderAsync(order);
        Assert.Same(order, Assert.Single(first.Account.ActiveOrders));
        second.Account.Cancel(order);
        Assert.Equal(OrderStates.Active, order.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.Account.OpenOrderAsync(order));
        await first.Account.ModifyOrderAsync(order, 98);
        first.Advance(Tick(1, 97));
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(98m, Assert.Single(first.Account.Journal).Price);
        Assert.Equal(1m, first.Account.Position!.Volume);
        Assert.Empty(second.Account.Journal);
        Assert.Equal(0m, second.Account.Position!.Volume);
    }

    [Fact]
    public async Task A_bucket_starting_before_the_run_is_published_only_when_its_source_becomes_available()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-replay-availability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var instrument = Instrument();
            using (var writer = new TickFile.Writer(TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start)),
                instrument, includeProvenance: true))
            {
                writer.Write(new Trade(-1, Start, 400, 20, TradeSide.None, new TradeProvenance(DataQuality.Compact, 60, "fixture-compact")));
                writer.Write(Tick(61, 100));
                writer.Write(Tick(62, 110));
                writer.Write(Tick(88, 110));
            }
            using var http = new HttpClient(new NoNetwork());
            await using var alerts = new AlertCenter(Path.Combine(root, "alerts.json"), new FixtureSecrets(), http);
            using var hub = new MarketDataHub();
            using var registry = new ReplaySessions(hub, root, Path.Combine(root, "results"), alerts);
            var run = registry.Open(ReplaySelection.Selected(instrument, Start.AddSeconds(30), Start.AddSeconds(90)),
                1000, new ReplayFees(), TimeSpan.FromMinutes(2));
            await UntilAsync(() => !run.Data.IsLoadingHistory, hub.PumpAll);
            run.Feed!.SeekForward(Start.AddSeconds(40));
            await UntilAsync(() => run.Feed.Clock >= Start.AddSeconds(40), hub.PumpAll);
            Assert.Equal(0, run.Data.Trades.Count);
            Assert.False(run.Account.HasPrice);
            Assert.Equal(Start.AddSeconds(30), run.ClockUtc);

            run.Feed.SeekForward(Start.AddMinutes(1));
            await UntilAsync(() => run.ClockUtc == Start.AddMinutes(1), hub.PumpAll);
            Assert.Equal(Start, run.Data.Trades.GetTime(0)); // The location is preserved; availability is not painted backward.
            Assert.Equal(400m, run.Account.LastPrice);
            Assert.False(run.Account.HasObservedPrice);
            Assert.Equal(OrderStatus.Rejected, run.Account.Market(OrderDirections.Buy, 1).Status);
            Assert.Equal(DataQuality.Compact, run.Result().DataQuality);

            run.Feed.SeekForward(Start.AddSeconds(61));
            await UntilAsync(() => run.ClockUtc == Start.AddSeconds(61), hub.PumpAll);
            run.Account.Market(OrderDirections.Buy, 1);
            run.Feed.SeekForward(Start.AddSeconds(62));
            await UntilAsync(() => run.ClockUtc == Start.AddSeconds(62), hub.PumpAll);
            run.Account.Flatten();
            Assert.Equal(100m, run.Result().RealizedNet);
            Assert.Equal(Start.AddSeconds(61), run.Account.Journal[0].Time);
            Assert.Equal(Start.AddSeconds(62), run.Account.Journal[1].Time);
            Assert.True(run.Result().HasKnownAvailability);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Unknown_availability_cannot_mark_equity_or_execute_a_pending_exit_even_when_the_source_is_flagged_raw()
    {
        using var run = new ReplaySession(ReplaySelection.Selected(Instrument(), Start, Start.AddMinutes(1)));
        run.Advance(Tick(0, 100));
        run.Account.Market(OrderDirections.Buy, 1);
        var target = run.Account.Limit(OrderDirections.Sell, 1, 105);
        var unknown = new Trade(2, Start.AddSeconds(1), 1000, 10, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "unknown-fixture", HasKnownAvailability: false));
        Assert.Throws<ArgumentException>(() => run.Advance(unknown));
        Assert.False(run.Result().HasKnownAvailability);
        Assert.NotNull(run.DataWarning);
        Assert.Equal(Start, run.ClockUtc);
        Assert.Equal(100m, run.Account.LastPrice);
        Assert.Equal(0m, run.Account.Position!.UnrealizedPnL);
        Assert.Equal(1m, run.Account.Position.Volume);
        Assert.Equal(0m, target.Filled);
        Assert.Single(run.Account.Journal);
        Assert.Equal(OrderStatus.Rejected, run.Account.Market(OrderDirections.Sell, 1).Status);
        run.Advance(Tick(2, 110));
        Assert.Equal(1m, target.Filled);
        Assert.Equal(0m, run.Account.Position.Volume);
        Assert.Equal(50m, run.Result().RealizedNet);
    }

    [Fact]
    public async Task A_recorded_unknown_availability_event_blocks_playback_with_a_visible_warning_instead_of_releasing_its_price()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-replay-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var instrument = Instrument();
            using (var writer = new TickFile.Writer(TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start)),
                instrument, includeProvenance: true))
                writer.Write(new Trade(1, Start, 1000, 10, TradeSide.Buy,
                    new TradeProvenance(DataQuality.Raw, SourceIdentity: "unknown-fixture", HasKnownAvailability: false)));
            using var http = new HttpClient(new NoNetwork());
            await using var alerts = new AlertCenter(Path.Combine(root, "alerts.json"), new FixtureSecrets(), http);
            using var hub = new MarketDataHub();
            using var registry = new ReplaySessions(hub, root, Path.Combine(root, "results"), alerts);
            var run = registry.Open(ReplaySelection.Selected(instrument, Start, Start.AddMinutes(1)),
                1000, new ReplayFees(), TimeSpan.FromMinutes(1));
            run.Feed!.SeekForward(Start.AddSeconds(1));
            await UntilAsync(() => !run.Data.IsLoadingHistory && run.Data.Status == FeedStatus.Error, hub.PumpAll);
            Assert.False(run.Account.HasPrice);
            Assert.False(run.Result().HasKnownAvailability);
            Assert.NotNull(run.DataWarning);
            Assert.Equal(0, run.Data.Trades.Count);
            Assert.Equal(OrderStatus.Rejected, run.Account.Market(OrderDirections.Buy, 1).Status);
            Assert.Empty(run.Account.Journal);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task UntilAsync(Func<bool> ready, Action pump)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) { deadline.Token.ThrowIfCancellationRequested(); pump(); await Task.Delay(5, deadline.Token); }
        pump();
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("No external destination is enabled in this fixture.");
    }
    private sealed class FixtureSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string Get(string account) => _values.GetValueOrDefault(account, "");
        public void Set(string account, string? secret) { if (secret == null) _values.Remove(account); else _values[account] = secret; }
    }
    internal sealed class ManualFeed(Instrument instrument, params Trade[] history) : IMarketDataFeed
    {
        private IMarketDataSink? _sink;
        public string Id => instrument.FeedId;
        public string DisplayName => "Fixture tape";
        public bool SupportsHistory => true;
        public bool IsLive => false;
        public DateTime Now => Start;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { instrument });
        public Task LoadTradesAsync(Instrument i, DateTime from, DateTime to, long? id, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        { onBatch(history); return Task.CompletedTask; }
        public IDisposable Subscribe(Instrument i, IMarketDataSink sink) { _sink = sink; return this; }
        public void Emit(Trade trade) => _sink!.OnTrades(instrument, new[] { trade });
        public void Dispose() => _sink = null;
    }
}
