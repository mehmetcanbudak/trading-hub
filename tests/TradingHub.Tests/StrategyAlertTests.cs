using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Scripting;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

public sealed class StrategyAlertTests
{
    [Fact]
    public async Task Compiled_strategy_alerts_are_causal_observations_and_failed_listeners_cannot_block_its_order()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-strategy-alert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var compiled = new ScriptCompiler().CompileSources([new ScriptSource("CausalAlert.cs", """
                using TradingHub.Strategies;
                using TradingHub.Market;
                public sealed class CausalAlert : Strategy
                {
                    protected override void OnInit() { Alert("initialization must stay silent"); }
                    protected override void OnBar(Bar bar) { Alert("bar signal"); }
                    protected override void OnTick(Trade tick)
                    {
                        Alert("tick signal", "tick-" + tick.Id);
                        if (IsFlat) Buy(1);
                    }
                }
                """)], "CausalAlertFixture");
            Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
            Type type = compiled.Assembly!.GetType("CausalAlert")!;
            var instrument = new Instrument { Symbol = "ALERT", FeedId = "fixture", TickSize = 1, VolumeStep = 1, PointValue = 1 };
            DateTime start = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
            var history = new[] { new Trade(1, start.AddMinutes(-2), 100, 1, TradeSide.Buy),
                new Trade(2, start.AddMinutes(-1), 101, 1, TradeSide.Buy), new Trade(3, start, 102, 1, TradeSide.Buy) };
            await using var alerts = new AlertCenter(Path.Combine(root, "alerts.json"), new Secrets());
            var historical = (Strategy)Activator.CreateInstance(type)!;
            using (alerts.Attach(historical))
            {
                var result = new BacktestEngine(historical, TickData.FromTrades(instrument, history), new BacktestSettings {
                    FeedId = "fixture", Symbol = "ALERT", FromUtc = start.AddMinutes(-3), ToUtc = start.AddMinutes(3), Period = "M1" }).Run();
                Assert.Null(result.Error);
                await alerts.FlushAsync();
                Assert.Empty(alerts.Snapshot().Recent);
            }
            using var feed = new ReplayPracticeTests.ManualFeed(instrument, history);
            using var data = new InstrumentData(instrument, feed);
            data.Start(start.AddMinutes(-3));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (data.IsLoadingHistory) { data.Pump(); await Task.Delay(5, deadline.Token); }
            using var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());
            var strategy = (Strategy)Activator.CreateInstance(type)!;
            strategy.AlertRaised += _ => throw new InvalidOperationException("Fixture failed toast listener");
            using var binding = alerts.Attach(strategy);
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1) { Persist = false };
            runner.Start();
            await alerts.FlushAsync();
            Assert.Empty(alerts.Snapshot().Recent); // Init and all warm-up bars are silent.
            feed.Emit(new Trade(4, start.AddMinutes(1), 103, 1, TradeSide.Buy));
            data.Pump();
            await alerts.FlushAsync();
            Assert.Contains(alerts.Snapshot().Recent, alert => alert.Id == "tick-4" && alert.Message == "tick signal" && alert.TimeUtc == start.AddMinutes(1));
            Assert.Equal(1m, broker.Position!.Volume);
            Assert.True(runner.IsRunning);
            runner.Stop(flatten: false);
            int count = alerts.Snapshot().Recent.Count;
            feed.Emit(new Trade(5, start.AddMinutes(2), 104, 1, TradeSide.Buy));
            data.Pump();
            await alerts.FlushAsync();
            Assert.Equal(count, alerts.Snapshot().Recent.Count);
            Assert.DoesNotContain(alerts.Snapshot().Recent, alert => alert.Message == "initialization must stay silent");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Compiled_strategy_consumes_batched_replay_ticks_at_their_own_prices_and_market_clock()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-causal-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var compiled = new ScriptCompiler().CompileSources([new ScriptSource("ReplayOrders.cs", """
                using TradingHub.Strategies;
                using TradingHub.Market;
                public sealed class ReplayOrders : Strategy
                {
                    protected override void OnTick(Trade tick)
                    {
                        if (tick.Id == 2) Buy(1);
                        if (tick.Id == 3) ClosePosition();
                        Alert("causal replay tick", "replay-" + tick.Id);
                    }
                }
                """)], "ReplayOrdersFixture");
            Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
            var instrument = new Instrument { Symbol = "CAUSAL", FeedId = "fixture", TickSize = 1, VolumeStep = 1, PointValue = 10 };
            DateTime start = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
            using (var writer = new TickFile.Writer(TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(start)), instrument))
            {
                writer.Write(new Trade(1, start, 100, 10, TradeSide.Buy));
                writer.Write(new Trade(2, start.AddSeconds(1), 110, 10, TradeSide.Buy));
                writer.Write(new Trade(3, start.AddSeconds(2), 120, 10, TradeSide.Buy));
            }
            await using var alerts = new AlertCenter(Path.Combine(root, "alerts.json"), new Secrets());
            using var hub = new MarketDataHub();
            using var registry = new ReplaySessions(hub, root, Path.Combine(root, "results"), alerts);
            var run = registry.Open(TradingHub.Replay.ReplaySelection.Selected(instrument, start, start.AddSeconds(4), 19),
                1000, new TradingHub.Replay.ReplayFees(1, 0.001m), TimeSpan.FromMinutes(1));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (run.Data.IsLoadingHistory) { hub.PumpAll(); await Task.Delay(5, deadline.Token); }
            var strategy = (Strategy)Activator.CreateInstance(compiled.Assembly!.GetType("ReplayOrders")!)!;
            using var binding = alerts.Attach(strategy);
            using var runner = new LiveStrategyRunner(strategy, run.Data, run.Account, ChartPeriod.M1) { Persist = false, AccountId = run.AccountId };
            runner.Start();
            run.Feed!.SeekForward(start.AddSeconds(2)); // Two ticks arrive in one feed batch.
            while (run.ClockUtc < start.AddSeconds(2)) { hub.PumpAll(); await Task.Delay(5, deadline.Token); }
            await alerts.FlushAsync();
            var fills = run.Account.Journal;
            Assert.Equal(2, fills.Count);
            Assert.Equal(110m, fills[0].Price);
            Assert.Equal(start.AddSeconds(1), fills[0].Time);
            Assert.Equal(120m, fills[1].Price);
            Assert.Equal(start.AddSeconds(2), fills[1].Time);
            Assert.Equal(95.7m, run.Result().RealizedNet);
            Assert.Equal(0m, run.Account.Position!.Volume);
            Assert.Contains(alerts.Snapshot().Recent, alert => alert.Id == "replay-2" && alert.TimeUtc == start.AddSeconds(1));
            runner.Stop(flatten: false);
            registry.End(run);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Secrets : ISecretStore
    {
        public string Get(string account) => "";
        public void Set(string account, string? secret) { }
    }
}
