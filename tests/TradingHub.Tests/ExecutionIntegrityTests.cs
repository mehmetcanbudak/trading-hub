using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public class ExecutionIntegrityTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Instrument Contract = new()
    {
        Symbol = "EXECUTION-TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 10m,
    };

    [Fact]
    public async Task LimitRoundTripUsesExecutionPricesAndCommissionsInLiveAccounting()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_execution_" + Guid.NewGuid().ToString("N"));
        string previousRoot = LiveStore.Root;
        Directory.CreateDirectory(root);
        LiveStore.Root = root;
        try
        {
            using var feed = new ManualFeed();
            using var data = new InstrumentData(Contract, feed) { RecordLive = false };
            data.Start(T0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (data.IsLoadingHistory)
            {
                data.Pump();
                if (!data.IsLoadingHistory) break;
                try { await Task.Delay(5, timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    data.Pump();
                    if (data.IsLoadingHistory)
                        throw new TimeoutException("The execution fixture did not publish its initial history.");
                }
            }
            Assert.Null(data.HistoryError);
            Assert.Equal(110m, data.LastPrice);

            var portfolio = new TradingHub.Trading.Portfolio();
            using var broker = new SimBroker(data, portfolio, new TradingStatistics()) { CommissionPerUnit = 1.5m };
            var strategy = new PassiveLimits();
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
            runner.Start();

            // Resting limits fill at 100 and 105, not at the market prints crossing them (90 and 110).
            strategy.BuyAt(100m, 2m);
            feed.Emit(new Trade(2, T0.AddMinutes(1), 90m, 1m, TradeSide.Sell));
            data.Pump();
            Assert.Equal(2m, broker.Position!.Volume);
            Assert.Equal(2m, runner.OwnPosition);
            Assert.Equal(2m, strategy.Position);

            strategy.SellAt(105m, 2m);
            feed.Emit(new Trade(3, T0.AddMinutes(2), 110m, 1m, TradeSide.Buy));
            data.Pump();
            Assert.Equal(0m, broker.Position.Volume);
            Assert.Equal(0m, runner.OwnPosition);
            Assert.Equal(0m, strategy.Position);
            Assert.Empty(broker.ActiveOrders);
            Assert.True(runner.IsRunning);

            // Independent economics: (105 - 100) * 2 * $10 = $100 gross;
            // two fills * 2 units * $1.50 = $6 commission, hence $94 net.
            Assert.Collection(broker.MyTrades,
                entry => { Assert.Equal(100m, entry.Price); Assert.Equal(2m, entry.Volume); Assert.Equal(3m, entry.Commission); },
                exit => { Assert.Equal(105m, exit.Price); Assert.Equal(2m, exit.Volume); Assert.Equal(3m, exit.Commission); });
            Assert.Equal(100m, broker.Position.RealizedPnL);
            Assert.Equal(6m, portfolio.Commission);
            Assert.Equal(94m, portfolio.RealizedPnL);

            Assert.Equal(94m, runner.Ledger.Realized);
            Assert.Equal(94m, strategy.NetProfit);
            var trade = Assert.Single(runner.Ledger.Trades);
            Assert.Equal(100m, trade.EntryPrice);
            Assert.Equal(105m, trade.ExitPrice);
            Assert.Equal(100m, trade.Pnl);
            Assert.Equal(6m, trade.Commission);
            Assert.Equal(94m, trade.NetPnl);

            var recovered = LiveStore.LoadLedger(runner.Key, Contract.PointValue);
            Assert.Equal(0m, recovered.Position);
            Assert.Equal(94m, recovered.Realized);
            Assert.Equal(94m, Assert.Single(recovered.Trades).NetPnl);
        }
        finally
        {
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedPartialTargetBooksOnlyNewQuantityAndKeepsSellProtectionForTheRemainingLong()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_adopted_execution_" + Guid.NewGuid().ToString("N"));
        string previousRoot = LiveStore.Root;
        Directory.CreateDirectory(root);
        LiveStore.Root = root;
        try
        {
            using var feed = new ManualFeed();
            using var data = new InstrumentData(Contract, feed) { RecordLive = false };
            data.Start(T0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (data.IsLoadingHistory)
            {
                data.Pump();
                if (!data.IsLoadingHistory) break;
                try { await Task.Delay(5, timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    data.Pump();
                    if (data.IsLoadingHistory)
                        throw new TimeoutException("The execution fixture did not publish its initial history.");
                }
            }
            Assert.Null(data.HistoryError);

            using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
            OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 4m);
            var adopted = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 4m, 120m, reduceOnly: true);
            adopted.ExchangeOrderId = "kept-partial-target";
            feed.Emit(new Trade(2, T0.AddMinutes(1), 120m, 2m, TradeSide.Buy));
            data.Pump();
            Assert.Equal(2m, adopted.Filled);
            Assert.Equal(2m, adopted.Unfilled);
            Assert.Equal(2m, broker.Position!.Volume);

            // The existing target is amended before the new runner adopts its remaining two units.
            OrderActions.Reprice(broker, adopted, 121m);
            var strategy = new PassiveLimits { StopLossTicks = 5, TakeProfitTicks = 10 };
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1)
            {
                ProtectOnStart = true,
                AdoptProtectionIds = new[] { "kept-partial-target" },
            };
            runner.SeedPosition(new LiveFill(T0, true, 2m, 111m, Contract.Symbol, Contract.PointValue));
            runner.Start();
            Assert.Contains("kept-partial-target", runner.ProtectionOrderIds);
            Assert.Same(adopted, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Limit));
            Assert.Equal(2m, runner.OwnPosition);
            Assert.Equal(2m, strategy.Position);
            Assert.Equal(2m, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop).Unfilled);

            feed.Emit(new Trade(3, T0.AddMinutes(2), 121m, 1m, TradeSide.Buy));
            data.Pump();

            // Four were bought, two sold before adoption, and only one sold after adoption: still long one.
            Assert.Equal(3m, adopted.Filled);
            Assert.Equal(1m, broker.Position.Volume);
            Assert.Equal(1m, runner.OwnPosition);
            Assert.Equal(1m, strategy.Position);
            Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop);
            Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Limit);
            Assert.All(broker.ActiveOrders, order =>
            {
                Assert.True(order.ReduceOnly);
                Assert.Equal(OrderDirections.Sell, order.Direction);
                Assert.Equal(1m, order.Unfilled);
            });
            Assert.All(runner.ActiveOrders, order =>
            {
                Assert.True(order.IsProtective);
                Assert.Equal(OrderSide.Sell, order.Side);
            });
        }
        finally
        {
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QuantityFirstProtectionReanchorsToActualExecutionPriceWithoutChangingExposure()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_price_anchor_" + Guid.NewGuid().ToString("N"));
        string previousRoot = LiveStore.Root;
        Directory.CreateDirectory(root);
        LiveStore.Root = root;
        try
        {
            using var feed = new ManualFeed();
            using var data = new InstrumentData(Contract, feed) { RecordLive = false };
            data.Start(T0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (data.IsLoadingHistory)
            {
                data.Pump();
                if (!data.IsLoadingHistory) break;
                try { await Task.Delay(5, timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    data.Pump();
                    if (data.IsLoadingHistory)
                        throw new TimeoutException("The execution fixture did not publish its initial history.");
                }
            }
            Assert.Null(data.HistoryError);

            using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
            using var account = new QuantityBeforePriceAccount(broker);
            var strategy = new PassiveLimits { StopLossTicks = 5, TakeProfitTicks = 10 };
            using var runner = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1);
            runner.Start();
            strategy.BuyAt(100m, 2m);
            feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
            data.Pump();

            // The real resting limit filled at 100. The account first reports quantity, with no average or executions.
            var execution = Assert.Single(broker.MyTrades);
            Assert.Equal(100m, execution.Price);
            Assert.Equal(2m, execution.Volume);
            Assert.Equal(2m, broker.Position!.Volume);
            Assert.Equal(100m, broker.Position.AveragePrice);
            Assert.Equal(2m, account.Position.Volume);
            Assert.Equal(0m, account.Position.AveragePrice);
            Assert.Equal(2m, runner.OwnPosition);
            Assert.Equal(2m, strategy.Position);
            Assert.True(runner.Ledger.HasPendingPrices);

            // The provisional anchor 98 keeps both stops below the market, isolating re-anchoring from stop triggering.
            Assert.Equal(93m, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop).TriggerPrice);
            Assert.Equal(108m, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Limit).Price);
            Assert.All(broker.ActiveOrders, order =>
            {
                Assert.True(order.ReduceOnly);
                Assert.Equal(OrderDirections.Sell, order.Direction);
                Assert.Equal(2m, order.Unfilled);
            });

            account.ReleaseExecutionReports();

            // No new trade or position change: actual basis 100 requires stop 95 and target 110, not 93 and 108.
            Assert.False(runner.Ledger.HasPendingPrices);
            Assert.Equal(100m, runner.Ledger.AveragePrice);
            Assert.Equal(2m, broker.Position.Volume);
            Assert.Equal(2m, runner.OwnPosition);
            Assert.Equal(2m, strategy.Position);
            Assert.Single(broker.MyTrades);
            Assert.Equal(95m, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop).TriggerPrice);
            Assert.Equal(110m, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Limit).Price);
            Assert.All(broker.ActiveOrders, order =>
            {
                Assert.True(order.ReduceOnly);
                Assert.Equal(OrderDirections.Sell, order.Direction);
                Assert.Equal(2m, order.Unfilled);
            });
        }
        finally
        {
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QuantityFirstOwnedProtectionIgnoresSameAccountManualAverage()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 4m);
        Assert.Equal(4m, broker.Position!.Volume);
        Assert.True(broker.Position.AveragePrice > 100m);

        using var account = new QuantityBeforePriceAccount(broker, exposeAccountAverage: true);
        var strategy = new PassiveLimits { StopLossTicks = 5, TakeProfitTicks = 10 };
        using var runner = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1);
        runner.Start();
        strategy.BuyAt(100m, 2m);
        feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
        data.Pump();

        // Four unrelated units with a higher basis must not price two owned units whose execution is unavailable.
        Assert.Equal(6m, broker.Position.Volume);
        Assert.Equal(2m, runner.OwnPosition);
        Assert.Equal(2m, strategy.Position);
        Assert.True(runner.Ledger.HasPendingPrices);
        Assert.Equal(0m, runner.Ledger.AveragePrice);
        Assert.True(account.Position.AveragePrice > 100m);
        var stop = Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop);
        var target = Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Limit);
        Assert.Equal(93m, stop.TriggerPrice);
        Assert.Equal(108m, target.Price);
        Assert.All(broker.ActiveOrders, order =>
        {
            Assert.True(order.ReduceOnly);
            Assert.Equal(OrderDirections.Sell, order.Direction);
            Assert.Equal(2m, order.Unfilled);
        });

        feed.Emit(new Trade(3, T0.AddMinutes(2), 99m, 1m, TradeSide.Buy));
        data.Pump();
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1m);
        Assert.Same(stop, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop));
        Assert.Same(target, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Limit));
        Assert.Equal(93m, stop.TriggerPrice);
        Assert.Equal(108m, target.Price);
        Assert.Equal(7m, broker.Position.Volume);
        Assert.Equal(2m, runner.OwnPosition);

        account.ReleaseExecutionReports();

        // Only the owned resting-limit execution establishes basis 100, without changing any native exposure.
        Assert.False(runner.Ledger.HasPendingPrices);
        Assert.Equal(100m, runner.Ledger.AveragePrice);
        Assert.Equal(7m, broker.Position.Volume);
        Assert.Equal(2m, runner.OwnPosition);
        Assert.Equal(2m, strategy.Position);
        Assert.Equal(95m, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop).TriggerPrice);
        Assert.Equal(110m, Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Limit).Price);
        Assert.All(broker.ActiveOrders, order => Assert.Equal(2m, order.Unfilled));
    }

    [Fact]
    public async Task PriorSessionUnknownFeesDoNotBlockAFlatCurrentSessionButCurrentSessionFeesStillGateRisk()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_fee_session_" + Guid.NewGuid().ToString("N"));
        string previousRoot = LiveStore.Root;
        Directory.CreateDirectory(root);
        LiveStore.Root = root;
        try
        {
            using var feed = new ManualFeed();
            using var data = new InstrumentData(Contract, feed) { RecordLive = false };
            data.Start(T0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (data.IsLoadingHistory)
            {
                data.Pump();
                if (!data.IsLoadingHistory) break;
                try { await Task.Delay(5, timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    data.Pump();
                    if (data.IsLoadingHistory)
                        throw new TimeoutException("The execution fixture did not publish its initial history.");
                }
            }
            Assert.Null(data.HistoryError);

            using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics()) { CommissionPerUnit = 1.5m };
            DateTime providerTime = T0;
            var account = new FeePendingAccount(broker, () => providerTime);
            var strategy = new PassiveLimits();
            using var runner = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1, timeZoneId: "UTC", sessionStart: TimeSpan.Zero)
            {
                MaxDailyLoss = 1_000m,
                MaxDrawdown = 0m,
                CloseOnLimit = false,
            };
            runner.Start();
            strategy.BuyAt(100m, 2m);
            providerTime = T0.AddMinutes(1);
            feed.Emit(new Trade(2, providerTime, 98m, 1m, TradeSide.Sell));
            data.Pump();
            Assert.Equal(2m, runner.OwnPosition);
            Assert.False(runner.Ledger.HasPendingPrices);
            Assert.True(runner.Paused);
            var sameDayAdd = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m, "same-session addition");
            Assert.Equal(StrategyOrderState.Canceled, sameDayAdd.State);
            Assert.Empty(broker.ActiveOrders);

            // Reduction remains possible while this session's execution fees are unknown.
            strategy.SellAt(105m, 2m);
            providerTime = T0.AddMinutes(2);
            feed.Emit(new Trade(3, providerTime, 106m, 1m, TradeSide.Buy));
            data.Pump();
            Assert.Equal(0m, broker.Position!.Volume);
            Assert.Equal(0m, runner.OwnPosition);
            Assert.Equal(0m, strategy.Position);
            Assert.Equal(ExecutionAvailability.Ready, account.Executions.Capture(ExecutionCursor.Start).Availability);
            Assert.False(runner.Ledger.HasPendingPrices);
            Assert.Equal(LiveFinancialQuality.Pending, runner.Ledger.FinancialQuality);
            Assert.Equal(100m, runner.Ledger.AuthoritativeGross);
            Assert.Null(runner.Ledger.AuthoritativeNet);
            runner.SetPaused(false);
            Assert.True(runner.Paused);

            // The public market clock crosses the UTC midnight session boundary with no open exposure.
            providerTime = T0.AddDays(1);
            feed.Emit(new Trade(4, providerTime, 106m, 1m, TradeSide.Buy));
            data.Pump();
            runner.SetPaused(false);
            Assert.False(runner.Paused);
            var currentDayEntry = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m, "current-session entry");
            Assert.Equal(StrategyOrderState.Active, currentDayEntry.State);
            var working = Assert.Single(broker.ActiveOrders);
            Assert.Equal(OrderDirections.Buy, working.Direction);
            Assert.Equal(1m, working.Unfilled);
            Assert.Equal(90m, working.Price);

            // Allowing today's entry does not erase history or promote yesterday's unknown fees to confirmed zero.
            var recovered = LiveStore.LoadLedger(runner.Key, Contract.PointValue);
            Assert.Equal(0m, recovered.Position);
            Assert.Equal(LiveFinancialQuality.Pending, recovered.FinancialQuality);
            Assert.Equal(100m, recovered.AuthoritativeGross);
            Assert.Null(recovered.AuthoritativeNet);
            Assert.Collection(recovered.Fills,
                entry => { Assert.True(entry.Buy); Assert.Equal(2m, entry.Quantity); Assert.Equal(100m, entry.Price); },
                exit => { Assert.False(exit.Buy); Assert.Equal(2m, exit.Quantity); Assert.Equal(105m, exit.Price); });

            // Once today's accepted entry fills, today's unknown fees must again block additional risk.
            providerTime = T0.AddDays(1).AddMinutes(1);
            feed.Emit(new Trade(5, providerTime, 89m, 1m, TradeSide.Sell));
            data.Pump();
            Assert.Equal(1m, broker.Position.Volume);
            Assert.Equal(1m, runner.OwnPosition);
            Assert.Equal(1m, strategy.Position);
            Assert.True(runner.Paused);
            runner.SetPaused(false);
            Assert.True(runner.Paused);
            var currentDayAdd = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 80m, "current-session addition");
            Assert.Equal(StrategyOrderState.Canceled, currentDayAdd.State);
            Assert.Empty(broker.ActiveOrders);
            Assert.Null(runner.Ledger.AuthoritativeNet);
        }
        finally
        {
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StoppedFlatRunnerExecutionCorrectionsPersistExactlyOnceByScopedIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_stopped_correction_" + Guid.NewGuid().ToString("N"));
        string previousRoot = LiveStore.Root;
        Directory.CreateDirectory(root);
        LiveStore.Root = root;
        try
        {
            using var feed = new ManualFeed();
            using var data = new InstrumentData(Contract, feed) { RecordLive = false };
            data.Start(T0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (data.IsLoadingHistory)
            {
                data.Pump();
                if (!data.IsLoadingHistory) break;
                try { await Task.Delay(5, timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    data.Pump();
                    if (data.IsLoadingHistory)
                        throw new TimeoutException("The execution fixture did not publish its initial history.");
                }
            }
            Assert.Null(data.HistoryError);

            using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics()) { CommissionPerUnit = 1.5m };
            using var account = new CorrectingExecutionAccount(broker);
            var strategy = new PassiveLimits();
            using var runner = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1);
            runner.Start();
            strategy.BuyAt(100m, 2m);
            feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
            data.Pump();
            strategy.SellAt(105m, 2m);
            feed.Emit(new Trade(3, T0.AddMinutes(2), 106m, 1m, TradeSide.Buy));
            data.Pump();

            // Original economics: (105 - 100) * 2 * $10 - $3 - $3 = $94.
            Assert.Equal(94m, runner.Ledger.AuthoritativeNet);
            Assert.Equal(0m, runner.OwnPosition);
            Assert.Equal(0m, broker.Position!.Volume);
            var original = account.Executions.Capture(ExecutionCursor.Start);
            var entry = Assert.Single(original.Changes, report => report.Buy);
            Assert.Equal(100m, entry.Price);
            Assert.Equal(2m, entry.Quantity);
            runner.Stop(flatten: false);
            Assert.False(runner.IsRunning);
            Assert.False(runner.HasWorkingOrders);
            Assert.Empty(broker.ActiveOrders);

            // A venue amendment preserves the original scoped execution ID and quantity, but corrects price/fee.
            account.PublishCorrection(entry.Identity, price: 102m, commission: 4m);
            var amendment = account.Executions.Capture(new ExecutionCursor(original.Epoch, original.Revision));
            Assert.True(amendment.Revision > original.Revision);
            var correctedReport = Assert.Single(amendment.Changes);
            Assert.Equal(entry.Identity, correctedReport.Identity);
            Assert.Equal(2m, correctedReport.Quantity);

            // Corrected economics: (105 - 102) * 2 * $10 - $4 - $3 = $53, still flat.
            // Observe the durable public projection; a stopped runner need not remain a live strategy subscription.
            var corrected = LiveStore.LoadLedger(runner.Key, Contract.PointValue);
            Assert.Equal(53m, corrected.AuthoritativeNet);
            Assert.Equal(60m, corrected.AuthoritativeGross);
            Assert.Equal(0m, corrected.Position);
            var trade = Assert.Single(corrected.Trades);
            Assert.Equal(102m, trade.EntryPrice);
            Assert.Equal(105m, trade.ExitPrice);
            Assert.Equal(60m, trade.Pnl);
            Assert.Equal(7m, trade.Commission);
            Assert.Equal(53m, trade.NetPnl);
            Assert.Collection(corrected.Fills,
                opening => { Assert.Equal(entry.Identity, opening.ExecutionId); Assert.True(opening.Buy); Assert.Equal(2m, opening.Quantity); Assert.Equal(102m, opening.Price); },
                closing => { Assert.False(closing.Buy); Assert.Equal(2m, closing.Quantity); Assert.Equal(105m, closing.Price); });

            account.PublishCorrection(entry.Identity, price: 102m, commission: 4m);
            var repeated = LiveStore.LoadLedger(runner.Key, Contract.PointValue);
            Assert.Equal(53m, repeated.AuthoritativeNet);
            Assert.Equal(0m, repeated.Position);
            Assert.Equal(2, repeated.Fills.Count);
            Assert.Equal(53m, Assert.Single(repeated.Trades).NetPnl);
            Assert.Equal(0m, broker.Position.Volume);
            Assert.Equal(2, broker.MyTrades.Count);
            Assert.False(runner.IsRunning);
        }
        finally
        {
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunningFlatHistoryResetRetainsTradingWithAFreshOwnerGeneration()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        var strategy = new PassiveLimits();
        using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
        runner.Start();
        strategy.BuyAt(100m, 2m);
        feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
        data.Pump();
        strategy.SellAt(105m, 2m);
        feed.Emit(new Trade(3, T0.AddMinutes(2), 106m, 1m, TradeSide.Buy));
        data.Pump();
        Assert.Equal(0m, runner.OwnPosition);
        Assert.Equal(100m, Assert.Single(runner.Ledger.Trades).NetPnl);
        Guid oldGeneration = LiveStore.CaptureLedgerGeneration(root.Root, runner.Key);
        Assert.True(runner.ResetLedger());
        Assert.True(runner.IsRunning);
        Guid newGeneration = LiveStore.CaptureLedgerGeneration(root.Root, runner.Key);
        Assert.NotEqual(oldGeneration, newGeneration);

        var next = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m, "entry after reset");

        Assert.Equal(StrategyOrderState.Active, next.State);
        var opening = Assert.Single(broker.ActiveOrders);
        Assert.Equal(1m, opening.Unfilled);
        Assert.Equal(broker.OrderContext.LedgerOwner(runner.Key, newGeneration).Id, opening.OwnerId);
        feed.Emit(new Trade(4, T0.AddMinutes(3), 89m, 1m, TradeSide.Sell));
        data.Pump();
        Assert.Equal(1m, broker.Position!.Volume);
        Assert.Equal(1m, runner.OwnPosition);
        Assert.Equal(1m, runner.VirtualPosition);
        Assert.Equal(1m, strategy.Position);
        Assert.False(runner.Ledger.HasPendingPrices);
        var recovered = LiveStore.LoadLedger(root.Root, runner.Key, Contract.PointValue);
        Assert.Equal(1m, recovered.Position);
        var fill = Assert.Single(recovered.Fills);
        Assert.Equal(90m, fill.Price);
        Assert.Equal(opening.LogicalOrderId, fill.OrderId);
        Assert.Equal(opening.OwnerId, fill.OwnerId);
        Assert.Equal(newGeneration, fill.OwnerGeneration);
        Assert.Empty(recovered.Trades);
        Assert.Empty(broker.OrderContext.PendingReceipts());
    }

    [Fact]
    public void RetryingAnOlderUnacknowledgedReceiptCannotUndoANewerCorrectionOrItsCheckpoint()
    {
        using var root = new ProjectionRegressionRoot();
        var source = new ExecutionLog();
        var descriptor = new AccountContextDescriptor(Guid.NewGuid().ToString("N"), "simulator", "paper",
            "fixture", "projection-receipt-fixture", "generation-1");
        var context = new OrderContext(descriptor, root.Root, source);
        const string key = "receipt-supersession";
        var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root.Root, key));
        var intent = context.Prepare(owner, new OrderRequest(Contract.Key, OrderDirections.Buy, OrderTypes.Limit,
            2m, 100m, StrategyQuantity: 2m));
        Assert.Null(intent.Error);
        var identity = new ExecutionIdentity(new ExecutionScope("simulator", "fixture-account", "paper", Contract.Key)
            { ContextId = descriptor.Id }, "execution-1");
        var first = new ExecutionReport(identity, 41, Contract.Symbol, T0, true, 2m, 100m, Contract.PointValue,
            "USD", new[] { new ExecutionFee(3m, "USD") }, true)
        { ContextId = descriptor.Id, IntentId = intent.Id, ClientOrderId = intent.ClientOrderId };
        var second = first with { Price = 102m, Fees = new[] { new ExecutionFee(4m, "USD") } };
        var projector = AccountExecutionProjector.For(source, context);

        // An owned namespace fault occurs only AFTER the real ledger application: the checkpoint directory
        // cannot be created. The actual receipt remains durable and unacknowledged; no callback is fabricated.
        string state = Assert.Single(Directory.GetDirectories(Path.Combine(root.Root, "execution-state")));
        string projected = Path.Combine(state, "projected");
        File.WriteAllText(projected, "owned checkpoint namespace blocker");
        source.Publish(first);
        Assert.Equal(100m, LiveStore.LoadLedger(root.Root, key, Contract.PointValue).AveragePrice);
        var pending = Assert.Single(context.PendingReceipts());
        Assert.Equal(new ExecutionCursor(source.Epoch, 1), pending.Cursor);
        Assert.Equal(first.Identity, pending.Report.Identity);
        Assert.Equal(2m, pending.Report.Quantity);
        Assert.Equal(100m, pending.Report.Price);
        Assert.Equal(3m, Assert.Single(pending.Report.Fees).Amount);
        Assert.Null(context.Projected(identity));
        Assert.True(context.HasStorageFault);

        // Retain the exact valid old receipt outside the pending inbox while the newer receipt completes.
        // Restoring these unchanged durable bytes models delayed delivery/recovery of the older receipt.
        string pendingPath = Path.Combine(state, "pending", pending.Id + ".json");
        string delayedPath = Path.Combine(root.Root, "delayed-receipt.json");
        File.Move(pendingPath, delayedPath);
        File.Delete(projected);
        source.Publish(second);
        Assert.Empty(context.PendingReceipts());
        var newer = context.Projected(identity);
        Assert.NotNull(newer);
        Assert.Equal(new ExecutionCursor(source.Epoch, 2), newer.Cursor);
        Assert.Equal(102m, newer.Report.Price);
        Assert.Equal(owner, newer.Owner);
        Assert.Equal(intent.LogicalOrderId, newer.LogicalOrderId);
        Assert.Equal(102m, LiveStore.LoadLedger(root.Root, key, Contract.PointValue).AveragePrice);

        File.Move(delayedPath, pendingPath);
        projector.Synchronize();
        Assert.Empty(context.PendingReceipts());
        var afterRetry = LiveStore.LoadLedger(root.Root, key, Contract.PointValue);
        Assert.Equal(2m, afterRetry.Position);
        Assert.Equal(102m, afterRetry.AveragePrice);
        var effective = Assert.Single(afterRetry.Fills);
        Assert.Equal(identity, effective.ExecutionId);
        Assert.Equal(intent.LogicalOrderId, effective.OrderId);
        Assert.Equal(4m, Assert.Single(effective.Fees!).Amount);
        var retained = context.Projected(identity);
        Assert.NotNull(retained);
        Assert.Equal(newer.Cursor, retained.Cursor);
        Assert.Equal(owner, retained.Owner);
        Assert.Equal(intent.LogicalOrderId, retained.LogicalOrderId);
        Assert.Equal(102m, retained.Report.Price);
        Assert.Equal(4m, Assert.Single(retained.Report.Fees).Amount);
        Assert.False(context.HasStorageFault);

        // Recreate the ownership journal and a source incarnation: neither cursor reset nor restart may regress facts.
        var restartedSource = new ExecutionLog();
        restartedSource.Publish(second);
        var restarted = new OrderContext(descriptor, root.Root, restartedSource);
        var durableCheckpoint = restarted.Projected(identity);
        Assert.NotNull(durableCheckpoint);
        Assert.Equal(newer.Cursor, durableCheckpoint.Cursor);
        Assert.Equal(owner, durableCheckpoint.Owner);
        Assert.Equal(intent.LogicalOrderId, durableCheckpoint.LogicalOrderId);
        Assert.Equal(102m, durableCheckpoint.Report.Price);
        AccountExecutionProjector.For(restartedSource, restarted).Synchronize();
        var reloaded = LiveStore.LoadLedger(root.Root, key, Contract.PointValue);
        Assert.Equal(2m, reloaded.Position);
        Assert.Equal(102m, reloaded.AveragePrice);
        Assert.Equal(4m, Assert.Single(Assert.Single(reloaded.Fills).Fees!).Amount);
        Assert.Empty(restarted.PendingReceipts());
        var checkpoint = restarted.Projected(identity);
        Assert.NotNull(checkpoint);
        Assert.Equal(owner, checkpoint.Owner);
        Assert.Equal(intent.LogicalOrderId, checkpoint.LogicalOrderId);
        Assert.Equal(102m, checkpoint.Report.Price);
    }

    [Fact]
    public async Task OldExecutionLocalIdCollisionCannotBookAnUnfilledCurrentLogicalOrder()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics()) { CommissionPerUnit = 1.5m };
        using var account = new CorrectingExecutionAccount(broker);
        var strategy = new PassiveLimits();
        using var runner = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1);
        runner.Start();
        strategy.BuyAt(100m, 2m);
        feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
        data.Pump();
        strategy.SellAt(105m, 2m);
        feed.Emit(new Trade(3, T0.AddMinutes(2), 106m, 1m, TradeSide.Buy));
        data.Pump();
        Assert.Equal(94m, runner.Ledger.AuthoritativeNet);
        var entry = Assert.Single(account.Executions.Capture(ExecutionCursor.Start).Changes, report => report.Buy);
        var oldIntent = account.OrderContext.Resolve(entry);
        Assert.NotNull(oldIntent);

        var current = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 5m, 90m, "unfilled current entry");
        var wire = Assert.Single(broker.ActiveOrders);
        Assert.NotEqual(oldIntent.LogicalOrderId, wire.LogicalOrderId);
        Assert.Equal(oldIntent.Owner.Id, wire.OwnerId);
        Assert.Equal(0m, wire.Filled);
        Assert.Equal(5m, wire.Unfilled);

        // Local IDs are old process scalars, not durable lineage. Only this old report's scalar collides;
        // its execution identity, intent/client identity and owner remain the legitimate historical ones.
        account.PublishCorrection(entry.Identity, price: 102m, commission: 4m, localOrderId: wire.Id);
        var snapshot = account.Executions.Capture(ExecutionCursor.Start);
        var correction = snapshot.Changes[^1];
        Assert.Equal(wire.Id, correction.LocalOrderId);
        Assert.Equal(oldIntent.LogicalOrderId, account.OrderContext.Resolve(correction)!.LogicalOrderId);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, wire.Filled);
        Assert.Equal(5m, wire.Unfilled);
        Assert.Equal(0m, current.FillPrice);
        Assert.Equal(StrategyOrderState.Active, current.State);
        Assert.Equal(0m, runner.OwnPosition);
        Assert.Equal(0m, runner.VirtualPosition);
        Assert.Equal(0m, strategy.Position);
        Assert.False(runner.Ledger.HasPendingPrices);
        Assert.Equal(53m, runner.Ledger.AuthoritativeNet);
        var recovered = LiveStore.LoadLedger(root.Root, runner.Key, Contract.PointValue);
        Assert.Equal(0m, recovered.Position);
        Assert.False(recovered.HasPendingPrices);
        Assert.Equal(53m, recovered.AuthoritativeNet);
        Assert.Equal(2, recovered.Fills.Count);
        Assert.DoesNotContain(recovered.Fills, fill => fill.OrderId == wire.LogicalOrderId);
        var checkpoint = account.OrderContext.Projected(entry.Identity);
        Assert.NotNull(checkpoint);
        Assert.Equal(oldIntent.LogicalOrderId, checkpoint.LogicalOrderId);
        Assert.Equal(new ExecutionCursor(snapshot.Epoch, snapshot.Revision), checkpoint.Cursor);
        Assert.Empty(account.OrderContext.PendingReceipts());
    }

    [Fact]
    public async Task TrackedDownwardCorrectionRescalesVirtualQuantityProtectionAndTheNextWireDelta()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics()) { CommissionPerUnit = 1m };
        using var account = new CorrectingExecutionAccount(broker);
        var strategy = new PassiveLimits { StopLossTicks = 5, TakeProfitTicks = 10 };
        using var runner = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1) { SizeMultiplier = 1m };
        runner.Start();
        strategy.BuyAt(100m, 10m);
        feed.Emit(new Trade(2, T0.AddMinutes(1), 100m, 5m, TradeSide.Sell));
        data.Pump();
        var opening = Assert.Single(broker.ActiveOrders, order => !order.ReduceOnly);
        Assert.Equal(5m, opening.Filled);
        Assert.Equal(5m, opening.Unfilled);
        Assert.Equal(5m, runner.OwnPosition);
        Assert.Equal(5m, runner.VirtualPosition);
        var original = Assert.Single(account.Executions.Capture(ExecutionCursor.Start).Changes);
        account.PublishCorrection(original.Identity, price: 101m, commission: 3m, quantity: 3m);

        // This is an execution amendment, not a fabricated position snapshot: the simulator handle still
        // reports its original cumulative five. The owned execution correction must nonetheless supersede it.
        Assert.Equal(5m, opening.Filled);
        Assert.Equal(3m, runner.OwnPosition);
        Assert.Equal(3m, runner.VirtualPosition);
        Assert.Equal(3m, strategy.Position);
        Assert.False(runner.Ledger.HasPendingPrices);
        Assert.Equal(101m, runner.Ledger.AveragePrice);
        Assert.Equal(60m, runner.Ledger.KnownOpenPnL(103m));
        Assert.Single(broker.ActiveOrders, order => order.Type == OrderTypes.Stop);
        Assert.Single(broker.ActiveOrders, order => order.ReduceOnly && order.Type == OrderTypes.Limit);
        Assert.All(broker.ActiveOrders.Where(order => order.ReduceOnly), order =>
        {
            Assert.Equal(OrderDirections.Sell, order.Direction);
            Assert.Equal(3m, order.Unfilled);
        });
        var next = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m, "one more strategy unit");
        Assert.Equal(StrategyOrderState.Active, next.State);
        var nextWire = Assert.Single(broker.ActiveOrders, order => !order.ReduceOnly && order.Id != opening.Id);
        Assert.Equal(1m, nextWire.Unfilled);
        Assert.Equal(90m, nextWire.Price);
        Assert.Equal(opening.OwnerId, nextWire.OwnerId);
        Assert.NotEqual(opening.LogicalOrderId, nextWire.LogicalOrderId);
        var recovered = LiveStore.LoadLedger(root.Root, runner.Key, Contract.PointValue);
        Assert.Equal(3m, recovered.Position);
        Assert.False(recovered.HasPendingPrices);
        var fill = Assert.Single(recovered.Fills);
        Assert.Equal(original.Identity, fill.ExecutionId);
        Assert.Equal(opening.LogicalOrderId, fill.OrderId);
        Assert.Equal(3m, fill.Quantity);
        Assert.Equal(101m, fill.Price);
        Assert.Equal(3m, Assert.Single(fill.Fees!).Amount);
        var snapshot = account.Executions.Capture(ExecutionCursor.Start);
        var checkpoint = account.OrderContext.Projected(original.Identity);
        Assert.NotNull(checkpoint);
        Assert.Equal(new ExecutionCursor(snapshot.Epoch, snapshot.Revision), checkpoint.Cursor);
        Assert.Equal(3m, checkpoint.Report.Quantity);
        Assert.Empty(account.OrderContext.PendingReceipts());
    }

    [Fact]
    public async Task RawCumulativeAdvanceAfterExecutionCorrectionAddsOnlyTheNewActualUnits()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics()) { CommissionPerUnit = 1m };
        using var account = new CorrectingExecutionAccount(broker);
        var strategy = new PassiveLimits { StopLossTicks = 5, TakeProfitTicks = 10 };
        using var runner = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1);
        runner.Start();
        strategy.BuyAt(100m, 10m);
        feed.Emit(new Trade(2, T0.AddMinutes(1), 100m, 5m, TradeSide.Sell));
        data.Pump();
        var opening = Assert.Single(broker.ActiveOrders, order => !order.ReduceOnly);
        Assert.Equal(5m, opening.Filled);
        Assert.Equal(5m, runner.OwnPosition);
        var original = Assert.Single(account.Executions.Capture(ExecutionCursor.Start).Changes);
        account.PublishCorrection(original.Identity, price: 100m, commission: 3m, quantity: 3m);
        Assert.Equal(5m, opening.Filled);
        Assert.Equal(3m, runner.OwnPosition);
        Assert.Equal(3m, runner.VirtualPosition);
        Assert.False(runner.Ledger.HasPendingFinancials);
        // Start a fresh queue interval through the public amendment path: the simulator's
        // previous negative queue balance must not supply the next trade's fill volume.
        OrderActions.Reprice(broker, opening, 100m);
        Assert.Equal(5m, opening.Filled);

        // The raw native counter is deliberately NOT rebased by the correction. Its next increase
        // is two real units: raw five -> seven, but corrected owned three -> five, not seven.
        feed.Emit(new Trade(3, T0.AddMinutes(2), 100m, 2m, TradeSide.Sell));
        data.Pump();
        Assert.Equal(7m, opening.Filled);
        Assert.Equal(3m, opening.Unfilled);
        Assert.Equal(7m, broker.Position!.Volume);
        var reports = account.Executions.Capture(ExecutionCursor.Start).Changes;
        var added = Assert.Single(reports, report => report.Identity != original.Identity);
        Assert.Equal(5m, runner.OwnPosition);
        Assert.Equal(5m, runner.VirtualPosition);
        Assert.Equal(5m, strategy.Position);
        Assert.Single(broker.ActiveOrders, order => order.ReduceOnly && order.Type == OrderTypes.Stop);
        Assert.Single(broker.ActiveOrders, order => order.ReduceOnly && order.Type == OrderTypes.Limit);
        Assert.All(broker.ActiveOrders.Where(order => order.ReduceOnly), order =>
        {
            Assert.Equal(OrderDirections.Sell, order.Direction);
            Assert.Equal(5m, order.Unfilled);
        });

        foreach (var actual in new[] { runner.Ledger, LiveStore.LoadLedger(root.Root, runner.Key, Contract.PointValue) })
        {
            Assert.Equal(5m, actual.Position);
            Assert.Equal(100m, actual.AveragePrice);
            Assert.Equal(150m, actual.KnownOpenPnL(103m)); // (103 - 100) * five owned units * $10.
            Assert.False(actual.HasPendingPrices);
            Assert.False(actual.HasPendingFinancials);
            Assert.Equal(LiveFinancialQuality.Authoritative, actual.FinancialQuality);
            Assert.Equal(0m, actual.AuthoritativeGross);
            Assert.Equal(-5m, actual.AuthoritativeNet); // Corrected fee three plus new actual fee two.
            Assert.Equal(-5m, actual.NetRealized);
            Assert.Empty(actual.Trades);
            Assert.Equal(2, actual.Fills.Count);
            var corrected = Assert.Single(actual.Fills, fill => fill.ExecutionId == original.Identity);
            Assert.Equal(3m, corrected.Quantity);
            Assert.Equal(100m, corrected.Price);
            Assert.Equal(3m, Assert.Single(corrected.Fees!).Amount);
            var newFill = Assert.Single(actual.Fills, fill => fill.ExecutionId == added.Identity);
            Assert.Equal(2m, newFill.Quantity);
            Assert.Equal(100m, newFill.Price);
            Assert.Equal(2m, Assert.Single(newFill.Fees!).Amount);
        }
    }

    [Fact]
    public async Task FirstCapturedExecutionBatchRetainsDownwardSupersessionOfAnEarlierQuantityObservation()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics()) { CommissionPerUnit = 1m };
        using var account = new CorrectingExecutionAccount(broker) { HoldExecutionReports = true };
        var strategy = new PassiveLimits();
        using var runner = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1);
        runner.Start();
        strategy.BuyAt(100m, 5m);
        feed.Emit(new Trade(2, T0.AddMinutes(1), 100m, 5m, TradeSide.Sell));
        data.Pump();
        Assert.Equal(5m, broker.Position!.Volume);
        Assert.Equal(5m, runner.OwnPosition);
        Assert.True(runner.Ledger.HasPendingPrices);
        var original = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes);
        account.PublishCorrection(original.Identity, price: 101m, commission: 3m, quantity: 3m);

        account.ReleaseExecutionReports();
        Assert.Equal(3m, runner.OwnPosition);
        Assert.Equal(3m, runner.VirtualPosition);
        Assert.Equal(3m, strategy.Position);
        Assert.False(runner.Ledger.HasPendingPrices);
        Assert.Equal(101m, runner.Ledger.AveragePrice);
        var recovered = LiveStore.LoadLedger(root.Root, runner.Key, Contract.PointValue);
        Assert.Equal(3m, recovered.Position);
        Assert.False(recovered.HasPendingPrices);
        Assert.Equal(LiveFinancialQuality.Authoritative, recovered.FinancialQuality);
        var fill = Assert.Single(recovered.Fills);
        Assert.Equal(original.Identity, fill.ExecutionId);
        Assert.Equal(3m, fill.Quantity);
        Assert.Equal(101m, fill.Price);
        Assert.Equal(3m, Assert.Single(fill.Fees!).Amount);
        var checkpoint = account.OrderContext.Projected(original.Identity);
        Assert.NotNull(checkpoint);
        Assert.Equal(3m, checkpoint.Report.Quantity);
        Assert.Empty(account.OrderContext.PendingReceipts());
        AccountExecutionProjector.For(account.Executions, account.OrderContext).Synchronize();
        Assert.Equal(3m, LiveStore.LoadLedger(root.Root, runner.Key, Contract.PointValue).Position);
        Assert.Empty(account.OrderContext.PendingReceipts());
    }

    [Fact]
    public async Task KeptStopExecutionDuringSuccessorWarmupIsFlatWithoutAnExtraLedgerClose()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics())
        {
            CommissionPerUnit = 1.5m,
        };
        using var first = new LiveStrategyRunner(new WarmupHandoffStrategy { StopLossTicks = 5 }, data, broker, ChartPeriod.M1);
        first.Start();
        first.Submit(OrderSide.Buy, StrategyOrderType.Limit, 2m, 100m, "owned entry before handoff");
        feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
        data.Pump();
        Assert.Equal(2m, first.OwnPosition);
        var keptStop = Assert.Single(broker.ActiveOrders);
        Assert.Equal(OrderTypes.Stop, keptStop.Type);
        Assert.True(keptStop.ReduceOnly);
        Assert.Equal(95m, keptStop.TriggerPrice);
        Assert.Equal(2m, keptStop.Unfilled);
        Guid generation = LiveStore.CaptureLedgerGeneration(root.Root, first.Key);
        first.Stop(flatten: false, keepProtection: true);
        Assert.False(first.HasWorkingOrders);
        Assert.Same(keptStop, Assert.Single(broker.ActiveOrders));
        int ordersBeforeWarmup = broker.Orders.Count;

        var strategy = new WarmupHandoffStrategy { StopLossTicks = 5 };
        using var successor = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1)
        {
            ProtectOnStart = true,
            AdoptProtectionIds = first.ProtectionOrderIds.ToArray(),
        };
        Assert.Equal(first.Key, successor.Key);
        bool filledDuringWarmup = false;
        strategy.OnNextBar = () =>
        {
            // Deliver the actual kept-stop execution during historical warmup, before Start returns.
            // The assertion below checks durable attribution, not the runner's bootstrap ordering.
            feed.Emit(new Trade(3, T0.AddMinutes(2), 94m, 1m, TradeSide.Sell));
            data.Pump();
            Assert.Equal(OrderStatus.Filled, keptStop.Status);
            Assert.Equal(0m, broker.Position!.Volume);
            var stopExecution = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes,
                report => report.NativeOrder == keptStop.NativeIdentity);
            Assert.False(stopExecution.Buy);
            Assert.Equal(2m, stopExecution.Quantity);
            Assert.Equal(94m, stopExecution.Price);
            var checkpoint = broker.OrderContext.Projected(stopExecution.Identity);
            Assert.NotNull(checkpoint);
            Assert.Equal(generation, checkpoint.Owner.LedgerGeneration);
            Assert.Equal(keptStop.OwnerId, checkpoint.Owner.Id);
            Assert.Equal(0m, LiveStore.LoadLedger(root.Root, successor.Key, Contract.PointValue).Position);
            Assert.Empty(broker.OrderContext.PendingReceipts());
            filledDuringWarmup = true;
        };

        successor.Start();

        Assert.True(filledDuringWarmup);
        Assert.True(successor.IsRunning);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, successor.OwnPosition);
        Assert.Equal(0m, successor.VirtualPosition);
        Assert.Equal(0m, strategy.Position);
        Assert.Empty(broker.ActiveOrders);
        Assert.Empty(successor.ActiveOrders);
        Assert.Equal(ordersBeforeWarmup, broker.Orders.Count);
        Assert.Equal(2, broker.MyTrades.Count);
        Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root.Root, successor.Key));
        foreach (var ledger in new[] { successor.Ledger, LiveStore.LoadLedger(root.Root, successor.Key, Contract.PointValue) })
        {
            Assert.Equal(0m, ledger.Position);
            Assert.False(ledger.HasPendingPrices);
            Assert.Equal(LiveFinancialQuality.Authoritative, ledger.FinancialQuality);
            // Actual economics: (94 - 100) * 2 * $10 - (2 + 2) * $1.50 = -$126.
            Assert.Equal(-126m, ledger.AuthoritativeNet);
            Assert.Collection(ledger.Fills,
                entry =>
                {
                    Assert.Equal(LiveFillKind.Execution, entry.Kind);
                    Assert.True(entry.Buy);
                    Assert.Equal(2m, entry.Quantity);
                    Assert.Equal(100m, entry.Price);
                    Assert.Equal(generation, entry.OwnerGeneration);
                },
                exit =>
                {
                    Assert.Equal(LiveFillKind.Execution, exit.Kind);
                    Assert.False(exit.Buy);
                    Assert.Equal(2m, exit.Quantity);
                    Assert.Equal(94m, exit.Price);
                    Assert.Equal(keptStop.LogicalOrderId, exit.OrderId);
                    Assert.Equal(generation, exit.OwnerGeneration);
                });
            Assert.Equal(-126m, Assert.Single(ledger.Trades).NetPnl);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessorCannotResetFlatHistoryUntilThePredecessorsOpeningCancellationSettles(bool openingFillsBeforeAcknowledgement)
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var broker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        var account = new LiveRunnerTests.SlowAccount(broker) { DeferCancels = true };
        account.ReportPosition();
        try
        {
            using var first = new LiveStrategyRunner(new PassiveLimits { StopLossTicks = 5 }, data, account, ChartPeriod.M1);
            first.Start();
            first.Submit(OrderSide.Buy, StrategyOrderType.Limit, 2m, 100m, "cancel-pending predecessor opening");
            var opening = Assert.Single(broker.ActiveOrders);
            Assert.False(opening.ReduceOnly);
            Assert.Equal(0m, opening.Filled);
            Guid generation = LiveStore.CaptureLedgerGeneration(root.Root, first.Key);
            first.Stop(flatten: false);
            Assert.False(first.IsRunning);
            Assert.True(first.HasWorkingOrders);
            Assert.Equal(0m, first.OwnPosition);
            Assert.Same(opening, Assert.Single(account.PendingCancels));
            Assert.Equal(OrderStates.Active, opening.State);

            var strategy = new PassiveLimits { StopLossTicks = 5 };
            using var successor = new LiveStrategyRunner(strategy, data, account, ChartPeriod.M1);
            Assert.Equal(first.Key, successor.Key);
            successor.Start();
            Assert.True(successor.IsRunning);
            Assert.Equal(0m, successor.OwnPosition);
            Assert.False(successor.HasWorkingOrders);
            Assert.Empty(successor.ActiveOrders);
            bool resetAcceptedBeforeAcknowledgement = successor.ResetLedger();

            // Deliver the actual broker outcome before asserting the reset result, so the late-fill case
            // also exercises the old intent's real execution even on the initially failing source.
            if (openingFillsBeforeAcknowledgement)
            {
                feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
                data.Pump();
                Assert.Equal(OrderStatus.Filled, opening.Status);
                Assert.Equal(2m, broker.Position!.Volume);
                var report = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes,
                    execution => execution.NativeOrder == opening.NativeIdentity);
                Assert.True(report.Buy);
                Assert.Equal(2m, report.Quantity);
                Assert.Equal(100m, report.Price);
                var owner = broker.OrderContext.Resolve(report);
                Assert.NotNull(owner);
                Assert.Equal(generation, owner.Owner.LedgerGeneration);
                Assert.Equal(opening.OwnerId, owner.Owner.Id);
            }
            else
            {
                account.ConfirmCancels();
                Assert.True(opening.Canceled);
                Assert.False(first.HasWorkingOrders);
                Assert.Empty(broker.ActiveOrders);
            }

            Assert.False(resetAcceptedBeforeAcknowledgement);
            Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root.Root, successor.Key));
            if (openingFillsBeforeAcknowledgement)
            {
                Assert.Equal(2m, successor.OwnPosition);
                Assert.Equal(2m, successor.VirtualPosition);
                Assert.Equal(2m, strategy.Position);
                Assert.False(first.HasWorkingOrders);
                Assert.Single(broker.MyTrades);
                var stop = Assert.Single(broker.ActiveOrders);
                Assert.Equal(OrderTypes.Stop, stop.Type);
                Assert.Equal(OrderDirections.Sell, stop.Direction);
                Assert.True(stop.ReduceOnly);
                Assert.Equal(2m, stop.Unfilled);
                Assert.Equal(95m, stop.TriggerPrice);
                Assert.Equal(opening.OwnerId, stop.OwnerId);
                Assert.Single(successor.ActiveOrders, order => order.IsProtective);
                foreach (var ledger in new[] { successor.Ledger, LiveStore.LoadLedger(root.Root, successor.Key, Contract.PointValue) })
                {
                    Assert.Equal(2m, ledger.Position);
                    Assert.False(ledger.HasPendingPrices);
                    var fill = Assert.Single(ledger.Fills);
                    Assert.Equal(LiveFillKind.Execution, fill.Kind);
                    Assert.True(fill.Buy);
                    Assert.Equal(2m, fill.Quantity);
                    Assert.Equal(100m, fill.Price);
                    Assert.Equal(opening.LogicalOrderId, fill.OrderId);
                    Assert.Equal(opening.OwnerId, fill.OwnerId);
                    Assert.Equal(generation, fill.OwnerGeneration);
                }
                account.ConfirmCancels();
                Assert.Same(stop, Assert.Single(broker.ActiveOrders));
                Assert.False(successor.ResetLedger());
            }
            else
            {
                // Settled, flat history remains resettable and the new generation can still trade.
                Assert.True(successor.ResetLedger());
                Guid nextGeneration = LiveStore.CaptureLedgerGeneration(root.Root, successor.Key);
                Assert.NotEqual(generation, nextGeneration);
                var next = successor.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m, "entry after old cancel acknowledgement");
                Assert.Equal(StrategyOrderState.Active, next.State);
                feed.Emit(new Trade(2, T0.AddMinutes(1), 89m, 1m, TradeSide.Sell));
                data.Pump();
                Assert.Equal(1m, broker.Position!.Volume);
                Assert.Equal(1m, successor.OwnPosition);
                Assert.Equal(1m, strategy.Position);
                var recovered = LiveStore.LoadLedger(root.Root, successor.Key, Contract.PointValue);
                Assert.Equal(1m, recovered.Position);
                Assert.False(recovered.HasPendingPrices);
                var fill = Assert.Single(recovered.Fills);
                Assert.Equal(90m, fill.Price);
                Assert.Equal(nextGeneration, fill.OwnerGeneration);
            }
            Assert.Empty(broker.OrderContext.PendingReceipts());
        }
        finally
        {
            account.DeferCancels = false;
            account.ConfirmCancels();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroNetCrossContextExposureCannotAuthorizeResetOrAnotherOpening(bool resetHistory)
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var firstBroker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        using var firstAccount = new CorrectingExecutionAccount(firstBroker);
        using var first = new LiveStrategyRunner(new PassiveLimits(), data, firstAccount, ChartPeriod.M1);
        first.Start();
        first.Submit(OrderSide.Buy, StrategyOrderType.Limit, 2m, 100m, "context A entry");
        feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
        data.Pump();
        first.Submit(OrderSide.Sell, StrategyOrderType.Limit, 2m, 105m, "context A exit");
        feed.Emit(new Trade(3, T0.AddMinutes(2), 106m, 1m, TradeSide.Buy));
        data.Pump();
        Assert.Equal(0m, first.OwnPosition);
        Assert.Equal(0m, firstBroker.Position!.Volume);
        var firstFacts = firstAccount.Executions.Capture(ExecutionCursor.Start).Changes;
        var oldEntry = Assert.Single(firstFacts, report => report.Buy);
        var oldExit = Assert.Single(firstFacts, report => !report.Buy);
        first.Stop(flatten: false);

        using var currentBroker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        using var current = new LiveStrategyRunner(new PassiveLimits(), data, currentBroker, ChartPeriod.M1);
        Assert.Equal(first.Key, current.Key);
        Assert.NotEqual(firstBroker.OrderContext.Descriptor.Id, currentBroker.OrderContext.Descriptor.Id);
        current.Start();
        current.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 100m, "context B entry");
        feed.Emit(new Trade(4, T0.AddMinutes(3), 98m, 1m, TradeSide.Sell));
        data.Pump();
        Assert.Equal(1m, current.OwnPosition);
        Assert.Equal(1m, currentBroker.Position!.Volume);
        var currentEntry = Assert.Single(currentBroker.Executions.Capture(ExecutionCursor.Start).Changes);
        current.Stop(flatten: false);
        Guid generation = LiveStore.CaptureLedgerGeneration(root.Root, current.Key);

        // Actual simulator fills supply all lineage; only the existing provider-correction seam revises
        // A's entry. No ledger injection, position mutation, private runner state or fabricated callback.
        firstAccount.PublishCorrection(oldEntry.Identity, price: 100m, commission: 0m, quantity: 1m);
        using var blocked = new LiveStrategyRunner(new PassiveLimits(), data, currentBroker, ChartPeriod.M1);
        blocked.Start();
        int ordersBeforeAttempt = currentBroker.Orders.Count;
        if (resetHistory)
        {
            Assert.False(blocked.ResetLedger());
            Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root.Root, blocked.Key));
        }
        else
        {
            var rejected = blocked.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m, "must not add risk across conflicting contexts");
            Assert.Equal(StrategyOrderState.Canceled, rejected.State);
        }
        Assert.Equal(ordersBeforeAttempt, currentBroker.Orders.Count);
        Assert.Empty(currentBroker.ActiveOrders);
        Assert.Equal(1m, currentBroker.Position!.Volume);
        blocked.Stop(flatten: false);

        // Correcting A's closing execution restores only B's real long. The refusal must not retire
        // old receipts/history, permanently freeze the healthy owner, or manufacture a closing estimate.
        firstAccount.PublishCorrection(oldExit.Identity, price: 105m, commission: 0m, quantity: 1m);
        var strategy = new PassiveLimits();
        using var reconciled = new LiveStrategyRunner(strategy, data, currentBroker, ChartPeriod.M1);
        reconciled.Start();
        Assert.Equal(1m, reconciled.OwnPosition);
        Assert.Equal(1m, reconciled.VirtualPosition);
        Assert.Equal(1m, strategy.Position);
        Assert.False(reconciled.Paused);
        Assert.False(reconciled.Ledger.OpenContextConflict);
        Assert.Equal(currentBroker.OrderContext.Descriptor.Id, reconciled.Ledger.OpenContextId);
        var recovered = LiveStore.LoadLedger(root.Root, reconciled.Key, Contract.PointValue);
        Assert.Equal(1m, recovered.Position);
        Assert.Equal(3, recovered.Fills.Count);
        Assert.All(recovered.Fills, fill => Assert.Equal(LiveFillKind.Execution, fill.Kind));
        Assert.Equal(1m, Assert.Single(recovered.Fills, fill => fill.ExecutionId == oldEntry.Identity).Quantity);
        Assert.Equal(1m, Assert.Single(recovered.Fills, fill => fill.ExecutionId == oldExit.Identity).Quantity);
        Assert.Equal(1m, Assert.Single(recovered.Fills, fill => fill.ExecutionId == currentEntry.Identity).Quantity);
        Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root.Root, reconciled.Key));
        Assert.Empty(firstBroker.OrderContext.PendingReceipts());
        Assert.Empty(currentBroker.OrderContext.PendingReceipts());
    }

    [Fact]
    public async Task ActiveRunnerLosesAdmissionWhenAnotherContextCorrectionCreatesZeroNetConflictingExposure()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var firstBroker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        using var firstAccount = new CorrectingExecutionAccount(firstBroker);
        using var first = new LiveStrategyRunner(new PassiveLimits(), data, firstAccount, ChartPeriod.M1);
        first.Start();
        first.Submit(OrderSide.Buy, StrategyOrderType.Limit, 2m, 100m, "context A entry");
        feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
        data.Pump();
        first.Submit(OrderSide.Sell, StrategyOrderType.Limit, 2m, 105m, "context A exit");
        feed.Emit(new Trade(3, T0.AddMinutes(2), 106m, 1m, TradeSide.Buy));
        data.Pump();
        Assert.Equal(0m, first.OwnPosition);
        Assert.Equal(0m, firstBroker.Position!.Volume);
        Assert.Equal(100m, first.Ledger.AuthoritativeNet);
        var firstFacts = firstAccount.Executions.Capture(ExecutionCursor.Start).Changes;
        var oldEntry = Assert.Single(firstFacts, report => report.Buy);
        var oldExit = Assert.Single(firstFacts, report => !report.Buy);
        first.Stop(flatten: false);

        using var currentBroker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        var strategy = new PassiveLimits { StopLossTicks = 5 };
        using var current = new LiveStrategyRunner(strategy, data, currentBroker, ChartPeriod.M1);
        Assert.Equal(first.Key, current.Key);
        Assert.NotEqual(firstBroker.OrderContext.Descriptor.Id, currentBroker.OrderContext.Descriptor.Id);
        current.Start();
        current.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 100m, "context B entry");
        feed.Emit(new Trade(4, T0.AddMinutes(3), 98m, 1m, TradeSide.Sell));
        data.Pump();
        Assert.Equal(1m, current.OwnPosition);
        Assert.Equal(1m, currentBroker.Position!.Volume);
        Assert.False(current.Paused);
        Assert.Equal(-20m, current.Info().AvailableOpenPnL);
        var currentEntry = Assert.Single(currentBroker.Executions.Capture(ExecutionCursor.Start).Changes);
        var protection = Assert.Single(currentBroker.ActiveOrders);
        Assert.Equal((OrderTypes.Stop, OrderDirections.Sell, 1m), (protection.Type, protection.Direction, protection.Unfilled));
        Assert.True(protection.ReduceOnly);
        Guid generation = LiveStore.CaptureLedgerGeneration(root.Root, current.Key);
        int ordersBeforeAttempt = currentBroker.Orders.Count;

        // B never stops or restarts. A's genuine execution identity is amended only through the
        // existing provider seam; it now contributes short 1 while the real B account stays long 1.
        firstAccount.PublishCorrection(oldEntry.Identity, price: 100m, commission: 0m, quantity: 1m);
        var conflicted = LiveStore.LoadLedger(root.Root, current.Key, Contract.PointValue);
        Assert.Equal(0m, conflicted.Position);
        Assert.True(conflicted.OpenContextConflict);
        Assert.True(current.IsRunning);
        Assert.True(current.Ledger.OpenContextConflict);
        Assert.Null(current.Ledger.KnownOpenPnL(data.LastPrice));
        Assert.Null(current.Info().AvailableOpenPnL);
        Assert.Same(protection, Assert.Single(currentBroker.ActiveOrders));
        feed.Emit(new Trade(5, T0.AddMinutes(4), 98m, 1m, TradeSide.Sell));
        data.Pump();
        Assert.Same(protection, Assert.Single(currentBroker.ActiveOrders));
        Assert.Equal(1m, protection.Unfilled);
        Assert.Equal(1m, current.VirtualPosition);
        Assert.Equal(1m, strategy.Position);
        var rejected = current.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m,
            "must not add risk while another context has offsetting exposure");
        Assert.Equal(StrategyOrderState.Canceled, rejected.State);
        Assert.Equal(ordersBeforeAttempt, currentBroker.Orders.Count);
        Assert.Same(protection, Assert.Single(currentBroker.ActiveOrders));
        Assert.Equal(1m, currentBroker.Position.Volume);
        Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root.Root, current.Key));

        // Correcting A's other real execution resolves A flat and leaves only B long 1. A normal
        // explicit resume is allowed if the safety transition paused B; no restart is permitted.
        firstAccount.PublishCorrection(oldExit.Identity, price: 105m, commission: 0m, quantity: 1m);
        if (current.Paused) current.SetPaused(false);
        Assert.True(current.IsRunning);
        Assert.False(current.Paused);
        Assert.False(current.Ledger.OpenContextConflict);
        Assert.Equal(1m, current.OwnPosition);
        Assert.Equal(1m, current.VirtualPosition);
        Assert.Equal(1m, strategy.Position);
        Assert.Equal(currentBroker.OrderContext.Descriptor.Id, current.Ledger.OpenContextId);
        Assert.Equal(-20m, current.Info().AvailableOpenPnL);
        var reconciledProtection = Assert.Single(currentBroker.ActiveOrders);
        Assert.Equal((OrderTypes.Stop, OrderDirections.Sell, 1m),
            (reconciledProtection.Type, reconciledProtection.Direction, reconciledProtection.Unfilled));
        foreach (var ledger in new[] { current.Ledger, LiveStore.LoadLedger(root.Root, current.Key, Contract.PointValue) })
        {
            Assert.False(ledger.OpenContextConflict);
            Assert.Equal(1m, ledger.Position);
            Assert.Equal(100m, ledger.AveragePrice);
            Assert.Equal(50m, ledger.AuthoritativeNet);
            Assert.Equal(3, ledger.Fills.Count);
            Assert.All(ledger.Fills, fill => Assert.Equal(LiveFillKind.Execution, fill.Kind));
            Assert.Equal(1m, Assert.Single(ledger.Fills, fill => fill.ExecutionId == oldEntry.Identity).Quantity);
            Assert.Equal(1m, Assert.Single(ledger.Fills, fill => fill.ExecutionId == oldExit.Identity).Quantity);
            Assert.Equal(1m, Assert.Single(ledger.Fills, fill => fill.ExecutionId == currentEntry.Identity).Quantity);
        }
        int ordersBeforeNewOpening = currentBroker.Orders.Count;
        var admitted = current.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m, "reconciled context B opening");
        var live = Assert.IsType<Order>(admitted.Live);
        Assert.Equal(ordersBeforeNewOpening + 1, currentBroker.Orders.Count);
        Assert.Same(live, Assert.Single(currentBroker.ActiveOrders, order => !order.ReduceOnly));
        Assert.Equal((OrderDirections.Buy, 1m, 90m), (live.Direction, live.Unfilled, live.Price));
        var claim = Assert.IsType<OwnedOrderIntent>(currentBroker.OrderContext.Find(live.IntentId));
        Assert.Equal(currentBroker.OrderContext.LedgerOwner(current.Key, generation), claim.Owner);
        Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root.Root, current.Key));
        Assert.Empty(firstBroker.OrderContext.PendingReceipts());
        Assert.Empty(currentBroker.OrderContext.PendingReceipts());
        current.Cancel(admitted);
        Assert.Same(reconciledProtection, Assert.Single(currentBroker.ActiveOrders));
        Assert.Equal(1m, currentBroker.Position.Volume);
    }

    [Fact]
    public async Task QuantityOnlyForeignCommitRevokesActiveRunnerAdmissionWithoutAnotherSourceEventOrTick()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var firstBroker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        using var firstAccount = new CorrectingExecutionAccount(firstBroker);
        using var first = new LiveStrategyRunner(new PassiveLimits(), data, firstAccount, ChartPeriod.M1);
        first.Start();

        // Prime the adapter's actual Synchronizing transition BEFORE the quantity-only fill.
        // This real, never-filled limit is canceled; no execution or availability notification
        // may accidentally rescue the later quantity notification under test.
        var primer = first.Submit(OrderSide.Sell, StrategyOrderType.Limit, 1m, 120m, "prime context A report hold");
        Assert.IsType<Order>(primer.Live);
        firstAccount.HoldExecutionReports = true;
        first.Cancel(primer);
        first.Stop(flatten: false);
        Assert.False(first.IsRunning);
        Assert.False(first.HasWorkingOrders);
        Assert.Empty(firstBroker.ActiveOrders);
        Assert.Empty(firstBroker.Executions.Capture(ExecutionCursor.Start).Changes);
        Assert.Equal(ExecutionAvailability.Synchronizing,
            firstAccount.Executions.Capture(ExecutionCursor.Start).Availability);

        using var currentBroker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        var strategy = new PassiveLimits();
        using var current = new LiveStrategyRunner(strategy, data, currentBroker, ChartPeriod.M1);
        Assert.Equal(first.Key, current.Key);
        Assert.NotEqual(firstAccount.OrderContext.Descriptor.Id, currentBroker.OrderContext.Descriptor.Id);
        current.Start();
        current.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 100m, "context B entry");
        feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
        data.Pump();
        Assert.Equal(1m, currentBroker.Position!.Volume);
        Assert.Equal(1m, current.OwnPosition);
        Assert.Equal(1m, current.VirtualPosition);
        Assert.Equal(1m, strategy.Position);
        Assert.Equal(-20m, current.Info().AvailableOpenPnL);
        Guid generation = LiveStore.CaptureLedgerGeneration(root.Root, current.Key);
        Assert.Equal(root.Root, firstAccount.OrderContext.StorageRoot);
        Assert.Equal(root.Root, currentBroker.OrderContext.StorageRoot);
        var owner = firstAccount.OrderContext.LedgerOwner(first.Key, generation);
        var ticket = firstAccount.OrderContext.Prepare(owner,
            new OrderRequest(Contract.Key, OrderDirections.Sell, OrderTypes.Market, 1m,
                StrategyQuantity: 1m, Comment: "stopped context A quantity-only opening"));
        Assert.Null(ticket.Error);

        int sourceNotifications = 0;
        void SourceChanged() => Interlocked.Increment(ref sourceNotifications);
        var notified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void CurrentChanged()
        {
            if (current.Ledger.OpenContextConflict) notified.TrySetResult(true);
        }
        firstAccount.Executions.Changed += SourceChanged;
        current.Changed += CurrentChanged;
        Order opening;
        try
        {
            // Actual transport -> ObserveQuantity -> account-lifetime projector -> durable ledger.
            // No data.Pump, source release, projector synchronization or B order/tick follows this.
            opening = firstAccount.Submit(ticket);
            Assert.Equal(OrderStatus.Filled, opening.Status);
            Assert.Equal(-1m, firstBroker.Position!.Volume);
            Assert.Equal(1m, currentBroker.Position.Volume);
            Assert.Equal(owner.Id, opening.OwnerId);
            Assert.Equal(firstAccount.OrderContext.Descriptor.Id, opening.ContextId);
            Assert.Equal(0, Volatile.Read(ref sourceNotifications));
            var pending = LiveStore.LoadLedger(root.Root, current.Key, Contract.PointValue);
            Assert.Equal(0m, pending.Position); // A short 1 and B long 1, not settled flat.
            Assert.True(pending.OpenContextConflict);
            Assert.True(pending.HasPendingPrices);
            Assert.Null(pending.AuthoritativeNet);
            Assert.Single(pending.Fills); // Only B has a priced execution; no invented A price.
            Assert.Equal(currentBroker.OrderContext.Descriptor.Id, pending.Fills[0].ContextId);

            // Permit a safe outside-provider-lock drain, but never stimulate it with another event.
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref sourceNotifications));
            Assert.True(firstAccount.HoldExecutionReports);
            Assert.True(current.IsRunning);
            Assert.True(current.Ledger.OpenContextConflict);
            Assert.True(current.Ledger.HasPendingPrices);
            Assert.Null(current.Ledger.KnownOpenPnL(data.LastPrice));
            Assert.Null(current.Info().AvailableOpenPnL);
            Assert.Equal(1m, current.OwnPosition);
            Assert.Equal(1m, current.VirtualPosition);
            Assert.Equal(1m, strategy.Position);
            int before = currentBroker.Orders.Count;
            var denied = current.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m,
                "quantity-only foreign conflict must block new B risk");
            Assert.Equal(StrategyOrderState.Canceled, denied.State);
            Assert.Equal(before, currentBroker.Orders.Count);
            Assert.Empty(currentBroker.ActiveOrders);
            Assert.Equal(1m, currentBroker.Position.Volume);
            Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root.Root, current.Key));
        }
        finally
        {
            current.Changed -= CurrentChanged;
            firstAccount.Executions.Changed -= SourceChanged;
        }

        // Only AFTER the admission proof, restore the actual execution replay and close both
        // actual positions. Prices below come from the simulator, never a fake ledger publish.
        firstAccount.ReleaseExecutionReports();
        var aEntry = Assert.Single(firstAccount.Executions.Capture(ExecutionCursor.Start).Changes);
        Assert.Equal((false, 1m, 97m), (aEntry.Buy, aEntry.Quantity, aEntry.Price));
        Assert.Equal(owner, firstAccount.OrderContext.Resolve(aEntry)!.Owner);
        var closeTicket = firstAccount.OrderContext.Prepare(owner,
            new OrderRequest(Contract.Key, OrderDirections.Buy, OrderTypes.Market, 1m,
                ReduceOnly: true, StrategyQuantity: 1m, MaximumOwnedQuantity: 1m));
        Assert.Null(closeTicket.Error);
        Assert.Equal(OrderStatus.Filled, firstAccount.Submit(closeTicket).Status);
        Assert.Equal(0m, firstBroker.Position.Volume);
        if (current.Paused) current.SetPaused(false);
        var closeReconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveClose()
        {
            if (current.Ledger.Position == 0 && current.VirtualPosition == 0 &&
                !current.Ledger.OpenContextConflict && !current.Ledger.HasPendingPrices)
                closeReconciled.TrySetResult();
        }
        current.Changed += ObserveClose;
        try
        {
            var closeB = current.Submit(OrderSide.Sell, StrategyOrderType.Market, 1m, 0m, "close actual B unit");
            Assert.Equal(1m, Assert.IsType<Order>(closeB.Live).QuantityToFill);
            Assert.Equal(0m, currentBroker.Position.Volume);
            ObserveClose();
            await closeReconciled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException error)
        {
            var durable = LiveStore.LoadLedger(root.Root, current.Key, Contract.PointValue);
            var bContext = currentBroker.OrderContext;
            throw new TimeoutException(
                $"Close reconciliation: memory={current.Ledger.Position}, A={current.Ledger.PositionInContext(firstAccount.OrderContext.Descriptor.Id)}, " +
                $"B={current.Ledger.PositionInContext(bContext.Descriptor.Id)}, virtual={current.VirtualPosition}, native={currentBroker.Position.Volume}, " +
                $"pendingPrices={current.Ledger.HasPendingPrices}, conflict={current.Ledger.OpenContextConflict}, " +
                $"capture={bContext.HasUnprojectedQuantity(bContext.LedgerOwner(current.Key, generation))}; " +
                $"disk={durable.Position}, diskA={durable.PositionInContext(firstAccount.OrderContext.Descriptor.Id)}, " +
                $"diskB={durable.PositionInContext(bContext.Descriptor.Id)}, diskPending={durable.HasPendingPrices}; " +
                $"memoryFills=[{string.Join(" | ", current.Ledger.Fills.Select(fill => $"{fill.Buy}:{fill.Quantity}:{fill.OrderId}:{fill.ExecutionId?.Id}"))}]; " +
                $"diskFills=[{string.Join(" | ", durable.Fills.Select(fill => $"{fill.Buy}:{fill.Quantity}:{fill.OrderId}:{fill.ExecutionId?.Id}"))}]; " +
                $"nativeFacts=[{string.Join(" | ", currentBroker.Executions.Capture(ExecutionCursor.Start).Changes.Select(report => $"{report.Buy}:{report.Quantity}:{report.IntentId}:{report.Identity.Id}"))}]; " +
                $"log=[{string.Join(" | ", current.LogLines.TakeLast(12))}]", error);
        }
        finally { current.Changed -= ObserveClose; }
        foreach (var ledger in new[] { current.Ledger, LiveStore.LoadLedger(root.Root, current.Key, Contract.PointValue) })
        {
            Assert.Equal(0m, ledger.Position);
            Assert.False(ledger.OpenContextConflict);
            Assert.False(ledger.HasPendingPrices);
            Assert.Equal(-50m, ledger.AuthoritativeNet); // A: (97-99)*10; B: (97-100)*10.
            Assert.Equal(4, ledger.Fills.Count);
            Assert.All(ledger.Fills, fill => Assert.Equal(LiveFillKind.Execution, fill.Kind));
        }
        Assert.Empty(firstAccount.OrderContext.PendingReceipts());
        Assert.Empty(currentBroker.OrderContext.PendingReceipts());
    }

    [Fact]
    public async Task OwnedStopFillDuringForeignContextConflictLeavesNoPhantomVirtualExposureAfterReconciliation()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var firstBroker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        using var firstAccount = new CorrectingExecutionAccount(firstBroker);
        using var first = new LiveStrategyRunner(new PassiveLimits(), data, firstAccount, ChartPeriod.M1);
        first.Start();
        first.Submit(OrderSide.Buy, StrategyOrderType.Limit, 2m, 100m, "context A entry");
        feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
        data.Pump();
        first.Submit(OrderSide.Sell, StrategyOrderType.Limit, 2m, 105m, "context A exit");
        feed.Emit(new Trade(3, T0.AddMinutes(2), 106m, 1m, TradeSide.Buy));
        data.Pump();
        Assert.Equal(0m, firstBroker.Position!.Volume);
        Assert.Equal(0m, first.OwnPosition);
        var aReports = firstAccount.Executions.Capture(ExecutionCursor.Start).Changes;
        var oldEntry = Assert.Single(aReports, report => report.Buy);
        var oldExit = Assert.Single(aReports, report => !report.Buy);
        first.Stop(flatten: false);

        using var currentBroker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        var strategy = new PassiveLimits { StopLossTicks = 5 };
        using var current = new LiveStrategyRunner(strategy, data, currentBroker, ChartPeriod.M1);
        Assert.Equal(first.Key, current.Key);
        Assert.NotEqual(firstAccount.OrderContext.Descriptor.Id, currentBroker.OrderContext.Descriptor.Id);
        current.Start();
        current.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 100m, "context B entry");
        feed.Emit(new Trade(4, T0.AddMinutes(3), 98m, 1m, TradeSide.Sell));
        data.Pump();
        Assert.Equal(1m, currentBroker.Position!.Volume);
        Assert.Equal(1m, current.VirtualPosition);
        var bEntry = Assert.Single(currentBroker.Executions.Capture(ExecutionCursor.Start).Changes);
        var protection = Assert.Single(currentBroker.ActiveOrders);
        Assert.Equal((OrderTypes.Stop, OrderDirections.Sell, 1m, 95m),
            (protection.Type, protection.Direction, protection.Unfilled, protection.TriggerPrice));
        Assert.True(protection.ReduceOnly);
        Guid generation = LiveStore.CaptureLedgerGeneration(root.Root, current.Key);
        var bOwner = currentBroker.OrderContext.LedgerOwner(current.Key, generation);
        Assert.Equal(bOwner.Id, protection.OwnerId);
        Assert.Equal(currentBroker.OrderContext.Descriptor.Id, protection.ContextId);

        firstAccount.PublishCorrection(oldEntry.Identity, price: 100m, commission: 0m, quantity: 1m);
        Assert.Equal(0m, LiveStore.LoadLedger(root.Root, current.Key, Contract.PointValue).Position);
        Assert.True(current.Ledger.OpenContextConflict);
        Assert.Same(protection, Assert.Single(currentBroker.ActiveOrders));
        Assert.Equal(1m, current.VirtualPosition);
        Assert.Equal(1m, strategy.Position);

        // The retained owned native stop ACTUALLY fills while A short 1 offsets B long 1.
        // The aggregate now becomes A short 1 alone; B's own exposure is nevertheless exactly zero.
        feed.Emit(new Trade(5, T0.AddMinutes(4), 94m, 1m, TradeSide.Sell));
        data.Pump();
        Assert.Equal(OrderStatus.Filled, protection.Status);
        Assert.Equal(1m, protection.Filled);
        Assert.Equal(0m, currentBroker.Position.Volume);
        Assert.Empty(currentBroker.ActiveOrders);
        var stopReport = Assert.Single(currentBroker.Executions.Capture(ExecutionCursor.Start).Changes,
            report => !report.Buy);
        Assert.Equal((1m, 94m), (stopReport.Quantity, stopReport.Price));
        Assert.Equal(protection.LogicalOrderId, currentBroker.OrderContext.Resolve(stopReport)!.LogicalOrderId);
        Assert.Equal(bOwner, currentBroker.OrderContext.Resolve(stopReport)!.Owner);
        Assert.Null(firstAccount.OrderContext.Resolve(stopReport));
        Assert.Null(currentBroker.OrderContext.Resolve(oldEntry));
        Assert.Equal(-1m, current.Ledger.Position);
        Assert.Equal(firstAccount.OrderContext.Descriptor.Id, current.Ledger.OpenContextId);
        Assert.Equal(0m, current.OwnPosition); // B is flat even while A's aggregate short remains.
        Assert.Equal(0m, current.VirtualPosition); // local-owned invariant, NOT aggregate -1
        Assert.Equal(0m, strategy.Position);

        firstAccount.PublishCorrection(oldExit.Identity, price: 105m, commission: 0m, quantity: 1m);
        if (current.Paused) current.SetPaused(false); // explicit resume allowed; never restart B
        Assert.True(current.IsRunning);
        Assert.False(current.Paused);
        Assert.Equal(0m, current.OwnPosition);
        Assert.Equal(0m, current.VirtualPosition);
        Assert.Equal(0m, strategy.Position);
        Assert.Equal(0m, currentBroker.Position.Volume);
        foreach (var ledger in new[] { current.Ledger, LiveStore.LoadLedger(root.Root, current.Key, Contract.PointValue) })
        {
            Assert.Equal(0m, ledger.Position);
            Assert.False(ledger.OpenContextConflict);
            Assert.False(ledger.HasPendingPrices);
            Assert.Equal(-10m, ledger.AuthoritativeNet); // corrected A +50, actual B stop -60
            Assert.Equal(4, ledger.Fills.Count);
            Assert.All(ledger.Fills, fill => Assert.Equal(LiveFillKind.Execution, fill.Kind));
            Assert.Equal(1m, Assert.Single(ledger.Fills, fill => fill.ExecutionId == oldEntry.Identity).Quantity);
            Assert.Equal(1m, Assert.Single(ledger.Fills, fill => fill.ExecutionId == oldExit.Identity).Quantity);
            Assert.Equal(1m, Assert.Single(ledger.Fills, fill => fill.ExecutionId == bEntry.Identity).Quantity);
            Assert.Equal(1m, Assert.Single(ledger.Fills, fill => fill.ExecutionId == stopReport.Identity).Quantity);
        }
        int beforeOpening = currentBroker.Orders.Count;
        var next = current.Submit(OrderSide.Sell, StrategyOrderType.Limit, 1m, 110m, "new B sell after its real stop");
        var wire = Assert.IsType<Order>(next.Live);
        Assert.Equal(beforeOpening + 1, currentBroker.Orders.Count);
        Assert.Same(wire, Assert.Single(currentBroker.ActiveOrders));
        Assert.Equal((OrderDirections.Sell, 1m, 1m, 110m),
            (wire.Direction, wire.QuantityToFill, wire.Unfilled, wire.Price));
        Assert.False(wire.ReduceOnly);
        Assert.Equal(bOwner, currentBroker.OrderContext.Find(wire.IntentId)!.Owner);
        Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root.Root, current.Key));
        Assert.Empty(firstAccount.OrderContext.PendingReceipts());
        Assert.Empty(currentBroker.OrderContext.PendingReceipts());
        current.Cancel(next);
        Assert.Empty(currentBroker.ActiveOrders);
        Assert.Equal(0m, currentBroker.Position.Volume);
    }

    [Fact]
    public async Task NativePartialStopDuringForeignConflictKeepsTheOwnedBasisAndUnrelatedManualHoldings()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        using var firstBroker = new SimBroker(data, new TradingHub.Trading.Portfolio(), new TradingStatistics());
        using var firstAccount = new CorrectingExecutionAccount(firstBroker);
        using var first = new LiveStrategyRunner(new PassiveLimits(), data, firstAccount, ChartPeriod.M1);
        first.Start();
        first.Submit(OrderSide.Buy, StrategyOrderType.Limit, 2m, 100m, "context A entry");
        feed.Emit(new Trade(2, T0.AddMinutes(1), 98m, 1m, TradeSide.Sell));
        data.Pump();
        first.Submit(OrderSide.Sell, StrategyOrderType.Limit, 2m, 105m, "context A exit");
        feed.Emit(new Trade(3, T0.AddMinutes(2), 106m, 1m, TradeSide.Buy));
        data.Pump();
        Assert.Equal(0m, first.OwnPosition);
        Assert.Equal(0m, firstBroker.Position!.Volume);
        var aReports = firstAccount.Executions.Capture(ExecutionCursor.Start).Changes;
        var oldEntry = Assert.Single(aReports, report => report.Buy);
        var oldExit = Assert.Single(aReports, report => !report.Buy);
        first.Stop(flatten: false);

        using var server = new FakeRithmicServer();
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        string contextId = Guid.NewGuid().ToString("N");
        var context = new OrderContext(new AccountContextDescriptor(contextId, "Rithmic", "futures",
            "owned-loopback-fixture", contextId, "fixture-generation"), root.Root);
        using var native = new RithmicBroker(data, session, "ACC1", firstAccount.Name,
            _ => Task.FromResult(("ESZ6", "CME")), context);
        var strategy = new PassiveLimits { StopLossTicks = 5 };
        using var current = new LiveStrategyRunner(strategy, data, native, ChartPeriod.M1) { ProtectOnStart = true };
        Assert.Equal(first.Key, current.Key);
        Assert.NotEqual(firstAccount.OrderContext.Descriptor.Id, contextId);

        string NativeState() => $"owned={current.OwnPosition}; virtual={current.VirtualPosition}; " +
            $"account={native.Position.Volume}@{native.Position.AveragePrice}; venue={server.Position}@{server.AvgPrice}; " +
            $"brokerError={native.LastError}; ownershipError={context.LastError}; " +
            $"orders=[{string.Join(" | ", native.ActiveOrders.Select(order => $"{order.ExchangeOrderId}:{order.Type}:{order.TriggerPrice}:{order.Filled}/{order.Unfilled}:{order.State}"))}]; " +
            $"runner=[{string.Join(" | ", current.LogLines.TakeLast(12))}]";

        // Observe actual native/consumer publications, not elapsed-time sleeps or manufactured notifications.
        async Task ObserveNative(Func<bool> condition, Func<Task> stimulus)
        {
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe()
            {
                try { if (condition()) observed.TrySetResult(); }
                catch (Exception error) { observed.TrySetException(error); }
            }
            native.Changed += Observe;
            current.Changed += Observe;
            try
            {
                await stimulus();
                Observe();
                try { await observed.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (TimeoutException error) { throw new TimeoutException(NativeState(), error); }
            }
            finally
            {
                native.Changed -= Observe;
                current.Changed -= Observe;
            }
        }

        await ObserveNative(() => native.PositionReady && native.OrdersReady, () =>
        {
            session.Start();
            return Task.CompletedTask;
        });
        Order? submitted = null;
        await ObserveNative(() => submitted is { ExchangeOrderId.Length: > 0 }, () =>
        {
            submitted = OrderActions.Manual(native, OrderTypes.Limit, OrderDirections.Buy, 2m, 200m,
                "unrelated manual holding");
            return Task.CompletedTask;
        });
        var unrelated = Assert.IsType<Order>(submitted);
        await ObserveNative(() => unrelated.Status == OrderStatus.Filled && native.Position.Volume == 2m &&
            native.Position.AveragePrice == 200m, () => server.FillOrder(unrelated.ExchangeOrderId, 2, 200));

        submitted = null;
        await ObserveNative(() => submitted is { ExchangeOrderId.Length: > 0 }, () =>
        {
            submitted = OrderActions.Manual(native, OrderTypes.Limit, OrderDirections.Buy, 2m, 100m,
                "known inherited lot to allocate explicitly");
            return Task.CompletedTask;
        });
        var inherited = Assert.IsType<Order>(submitted);
        await ObserveNative(() => inherited.Status == OrderStatus.Filled && native.Position.Volume == 4m &&
            native.Position.AveragePrice == 150m, () => server.FillOrder(inherited.ExchangeOrderId, 2, 100));
        Assert.Equal((4, 150d), (server.Position, server.AvgPrice));
        Assert.Equal(context.ManualOwner, context.Find(unrelated.IntentId)!.Owner);
        Assert.Equal(context.ManualOwner, context.Find(inherited.IntentId)!.Owner);

        // Explicit user-facing allocation of ONLY the known two-unit 100 lot. Rithmic's basket
        // counters are not an authoritative fee-complete execution source; this stays a legacy estimate.
        Guid generation = LiveStore.CaptureLedgerGeneration(root.Root, current.Key);
        var owner = context.LedgerOwner(current.Key, generation);
        current.SeedPosition(new LiveFill(oldExit.Time.AddTicks(1), true, 2m, 100m, Contract.Symbol, Contract.PointValue)
        {
            ContextId = contextId, OwnerId = owner.Id, OwnerGeneration = generation,
        });
        await ObserveNative(() => current.IsRunning && current.OwnPosition == 2m && current.VirtualPosition == 2m &&
            native.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && order.ExchangeOrderId.Length > 0) &&
            native.OrdersReady, () =>
        {
            current.Start();
            return Task.CompletedTask;
        });
        var stop = Assert.Single(native.ActiveOrders);
        Assert.Equal((OrderTypes.Stop, OrderDirections.Sell, 2m, 95m),
            (stop.Type, stop.Direction, stop.Unfilled, stop.TriggerPrice));
        Assert.True(stop.ReduceOnly);
        Assert.Equal(owner, context.Find(stop.IntentId)!.Owner);
        Assert.Equal(contextId, stop.ContextId);
        Assert.Equal(LiveFinancialQuality.Estimated, current.FinancialQuality);
        Assert.Null(current.Ledger.AuthoritativeNet);
        Assert.Equal(ExecutionAvailability.Unavailable, native.Executions.Capture(ExecutionCursor.Start).Availability);
        string stopBasket = stop.ExchangeOrderId;
        string stopIntent = Assert.IsType<string>(stop.IntentId);
        string stopLogical = Assert.IsType<string>(stop.LogicalOrderId);
        var stopNative = stop.NativeIdentity;
        Assert.NotNull(stopNative);
        int NewOrders() { lock (server.Tags) return server.Tags.Count; }
        Assert.Equal(3, NewOrders()); // two actual manual opening orders and one acknowledged strategy stop
        int modifications = server.ModifyRequests;

        await ObserveNative(() => current.Ledger.OpenContextConflict && current.Paused, () =>
        {
            firstAccount.PublishCorrection(oldEntry.Identity, price: 100m, commission: 0m, quantity: 1m);
            return Task.CompletedTask;
        });
        Assert.Equal(2m, current.OwnPosition);
        Assert.Equal(2m, current.VirtualPosition);
        Assert.Same(stop, Assert.Single(native.ActiveOrders));
        Assert.Equal(150m, native.Position.AveragePrice);

        // Genuine native partial execution: a two-unit Stop, filled ONE at its 95 trigger.
        // SimBroker cannot exercise this transition: it fills the whole stop regardless of trade volume.
        await ObserveNative(() => current.OwnPosition == 1m && current.VirtualPosition == 1m &&
            strategy.Position == 1m && native.Position.Volume == 3m && stop.Filled == 1m &&
            native.PositionReady && native.OrdersReady, () => server.FillOrder(stopBasket, 1, 95));

        // Assert the consumer still MANAGES this exact acknowledged native stop. This catches cancellation
        // immediately, even before a wrong replacement's asynchronous new-order request reaches the venue.
        var managed = Assert.Single(current.ActiveOrders);
        Assert.True(managed.IsProtective);
        Assert.True(managed.IsActive);
        Assert.Same(stop, managed.Live);
        Assert.Same(stop, Assert.Single(native.ActiveOrders));
        Assert.Equal((OrderTypes.Stop, OrderDirections.Sell, OrderStatus.PartlyFilled, 1m, 1m, 95m),
            (stop.Type, stop.Direction, stop.Status, stop.Filled, stop.Unfilled, stop.TriggerPrice));
        Assert.False(stop.Canceled);
        Assert.Equal(stopBasket, stop.ExchangeOrderId);
        Assert.Equal(stopIntent, stop.IntentId);
        Assert.Equal(stopLogical, stop.LogicalOrderId);
        Assert.Equal(stopNative, stop.NativeIdentity);
        Assert.True(stop.ReduceOnly);
        Assert.Equal(owner, context.Find(stop.IntentId)!.Owner);
        Assert.Equal(3, NewOrders()); // no cancellation/replacement POST at 145 or unsafe quantity
        Assert.Equal(modifications, server.ModifyRequests);
        Assert.Equal(1m, current.Info().Position);
        Assert.Equal((3m, 150m), (native.Position.Volume, native.Position.AveragePrice));
        Assert.Equal((3, 150d), (server.Position, server.AvgPrice));
        // FakeRithmicServer's native ES accounting uses $50/point: (95 - 150) * 1 * 50.
        // This verifies the actual venue reduction, not an authoritative strategy PnL assertion.
        Assert.Equal(-2750d, server.ClosedPnl);
        Assert.Equal(OrderStatus.Filled, unrelated.Status);
        Assert.Equal((2m, 0m), (unrelated.Filled, unrelated.Unfilled));
        Assert.Equal(context.ManualOwner, context.Find(unrelated.IntentId)!.Owner);
        Assert.Equal(2m, native.Position.Volume - current.OwnPosition); // unrelated two units remain untouched

        foreach (var ledger in new[] { current.Ledger, LiveStore.LoadLedger(root.Root, current.Key, Contract.PointValue) })
        {
            Assert.True(ledger.OpenContextConflict);
            Assert.Equal(0m, ledger.Position); // A short one and B long one; never use this to flatten B
            Assert.Equal(1m, ledger.PositionInContext(contextId));
            Assert.Equal(-1m, ledger.PositionInContext(firstAccount.OrderContext.Descriptor.Id));
            Assert.True(ledger.HasPendingPrices); // actual native quantity has no fee-complete price report
            Assert.Equal(LiveFinancialQuality.Pending, ledger.FinancialQuality);
            Assert.Null(ledger.AuthoritativeNet);
            Assert.Null(ledger.NetRealized);
            Assert.Equal(3, ledger.Fills.Count); // two genuine A reports plus exactly one explicit B legacy seed
            Assert.Equal(2, ledger.Fills.Count(fill => fill.Kind == LiveFillKind.Execution));
            var inheritedFill = Assert.Single(ledger.Fills, fill => fill.Kind == LiveFillKind.LegacyEstimate);
            Assert.Equal((2m, 100m, contextId), (inheritedFill.Quantity, inheritedFill.Price, inheritedFill.ContextId));
            Assert.DoesNotContain(ledger.Fills, fill => fill.Price == 200m);
        }
        Assert.Equal(LiveFinancialQuality.Pending, current.Info().FinancialQuality);
        Assert.Null(current.Info().CompleteNet);
        Assert.Null(current.Info().AvailableOpenPnL);
        var denied = current.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 90m,
            "foreign financial conflict must block new risk without changing owned protection");
        Assert.Equal(StrategyOrderState.Canceled, denied.State);
        Assert.Equal(3, NewOrders());
        Assert.Same(stop, Assert.Single(native.ActiveOrders));
        Assert.Same(stop, Assert.Single(current.ActiveOrders).Live);
        Assert.Equal(1m, stop.Unfilled);
        Assert.Equal(95m, stop.TriggerPrice);
        Assert.Equal(1m, current.OwnPosition);
        Assert.Equal(1m, current.VirtualPosition);
        Assert.Equal(1m, strategy.Position);
    }

    [Fact]
    public async Task EphemeralSimulatorSustainsThreeHundredActualMarketRoundTripsWithExactFlatEconomics()
    {
        using var root = new ProjectionRegressionRoot();
        using var feed = new ManualFeed();
        using var data = await StartProjectionData(feed);
        feed.Emit(new Trade(2, T0.AddMinutes(1), 100m, 1m, TradeSide.Buy));
        data.Pump();
        Assert.Equal(100m, data.LastPrice);
        Assert.False(data.Depth.HasBid);
        Assert.False(data.Depth.HasAsk);
        var portfolio = new TradingHub.Trading.Portfolio();
        var statistics = new TradingStatistics();
        using var broker = new SimBroker(data, portfolio, statistics)
        {
            CommissionPerUnit = 0m,
            CommissionRate = 0m,
        };
        var strategy = new PassiveLimits();
        using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1) { Persist = false };
        runner.Start();
        Assert.Null(broker.OrderContext.StorageRoot); // ordinary visual/nonpersistent path, no journal fallback

        // A fixed mark of 100 and one-tick spread fills buys at 101 and sells at 99. With point
        // value 10, each actual one-unit round trip loses exactly 20, independent of cache policy.
        long tradeId = 3;
        DateTime time = T0.AddMinutes(2);
        for (int roundTrip = 0; roundTrip < 300; roundTrip++)
        {
            var buy = runner.Submit(OrderSide.Buy, StrategyOrderType.Market, 1m, 0m, "ephemeral entry");
            feed.Emit(new Trade(tradeId++, time = time.AddSeconds(1), 100m, 1m, TradeSide.Buy));
            data.Pump();
            var buyLive = Assert.IsType<Order>(buy.Live);
            Assert.Equal(OrderStatus.Filled, buyLive.Status);
            Assert.Equal(101m, buy.FillPrice);
            Assert.Equal(1m, runner.OwnPosition);
            Assert.Equal(1m, broker.Position!.Volume);
            var sell = runner.Submit(OrderSide.Sell, StrategyOrderType.Market, 1m, 0m, "ephemeral close");
            feed.Emit(new Trade(tradeId++, time = time.AddSeconds(1), 100m, 1m, TradeSide.Buy));
            data.Pump();
            var sellLive = Assert.IsType<Order>(sell.Live);
            Assert.Equal(OrderStatus.Filled, sellLive.Status);
            Assert.Equal(99m, sell.FillPrice);
            Assert.Equal(0m, runner.OwnPosition);
            Assert.Equal(0m, runner.VirtualPosition);
            Assert.Equal(0m, strategy.Position);
            Assert.Equal(0m, broker.Position.Volume);
            Assert.Equal(-20m * (roundTrip + 1), runner.Ledger.AuthoritativeNet);
            Assert.False(runner.Paused);
        }

        Assert.True(runner.IsRunning);
        Assert.Null(broker.OrderContext.StorageRoot);
        Assert.False(runner.HasWorkingOrders);
        Assert.Empty(broker.ActiveOrders);
        Assert.Equal(600, broker.Orders.Count);
        Assert.Equal(600, broker.MyTrades.Count);
        Assert.All(broker.Orders, order => Assert.Equal(OrderStatus.Filled, order.Status));
        Assert.Equal(600, broker.Orders.Select(order => order.ExchangeOrderId).Distinct().Count());
        var facts = broker.Executions.Capture(ExecutionCursor.Start);
        Assert.Equal(ExecutionAvailability.Ready, facts.Availability);
        Assert.Equal(600, facts.Changes.Count);
        Assert.Equal(600, facts.Changes.Select(report => report.Identity).Distinct().Count());
        Assert.All(facts.Changes, report =>
        {
            Assert.Equal(1m, report.Quantity);
            Assert.Equal(report.Buy ? 101m : 99m, report.Price);
            Assert.True(report.FeesComplete);
            Assert.Empty(report.Fees);
        });
        Assert.Equal(600, runner.Ledger.Fills.Count);
        Assert.Equal(600, runner.Ledger.Fills.Select(fill => fill.ExecutionId).Distinct().Count());
        Assert.All(runner.Ledger.Fills, fill => Assert.Equal(LiveFillKind.Execution, fill.Kind));
        Assert.Equal(300, runner.Ledger.Trades.Count);
        Assert.All(runner.Ledger.Trades, trade =>
        {
            Assert.Equal(-20m, trade.Pnl);
            Assert.Equal(0m, trade.Commission);
            Assert.Equal(-20m, trade.NetPnl);
        });
        Assert.Equal(0m, runner.Ledger.Position);
        Assert.Equal(-6000m, runner.Ledger.AuthoritativeGross);
        Assert.Equal(-6000m, runner.Ledger.AuthoritativeNet);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(-6000m, broker.Position.RealizedPnL);
        Assert.Equal(0m, broker.Position.Commission);
        Assert.Equal(-6000m, portfolio.RealizedPnL);
        Assert.Equal(0m, portfolio.Commission);
        Assert.Equal(300, statistics.HistoryMyTrades.Count);
        Assert.Equal(-6000m, statistics.HistoryMyTrades.Sum(trade => trade.PnL));
    }

    private sealed class WarmupHandoffStrategy : Strategy
    {
        public Action? OnNextBar { get; set; }
        protected override void OnBar(Bar bar)
        {
            var callback = OnNextBar;
            OnNextBar = null;
            callback?.Invoke();
        }
    }

    private sealed class ProjectionRegressionRoot : IDisposable
    {
        private readonly string _previous = LiveStore.Root;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tradinghub_projection_regression_" + Guid.NewGuid().ToString("N"));
        public ProjectionRegressionRoot()
        {
            Directory.CreateDirectory(Root);
            LiveStore.Root = Root;
        }
        public void Dispose()
        {
            LiveStore.Root = _previous;
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<InstrumentData> StartProjectionData(ManualFeed feed)
    {
        var data = new InstrumentData(Contract, feed) { RecordLive = false };
        try
        {
            data.Start(T0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (data.IsLoadingHistory)
            {
                data.Pump();
                if (!data.IsLoadingHistory) break;
                try { await Task.Delay(5, timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    data.Pump();
                    if (data.IsLoadingHistory)
                        throw new TimeoutException("The execution fixture did not publish its initial history.");
                }
            }
            Assert.Null(data.HistoryError);
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private sealed class PassiveLimits : Strategy
    {
        public void BuyAt(decimal price, decimal quantity) => BuyLimit(price, quantity);
        public void SellAt(decimal price, decimal quantity) => SellLimit(price, quantity);
    }

    /// <summary>Transparent simulator adapter modeling an independently delivered quantity snapshot before its
    /// average price and execution replay. It never invents a fill, execution price, fee or broker position.</summary>
    private sealed class QuantityBeforePriceAccount : ITradingAccount, ITrackedOrderReplacement, IDisposable
    {
        private readonly SimBroker _inner;
        private readonly Position _quantityOnly = new();
        private bool _holdingReports = true;
        private readonly bool _exposeAccountAverage;

        public QuantityBeforePriceAccount(SimBroker inner, bool exposeAccountAverage = false)
        {
            _inner = inner;
            _exposeAccountAverage = exposeAccountAverage;
            _quantityOnly.Volume = inner.Position!.Volume;
            _quantityOnly.AveragePrice = exposeAccountAverage ? inner.Position.AveragePrice : 0;
            inner.Changed += OnBrokerChanged;
        }

        public string Name => _inner.Name;
        public bool IsReal => _inner.IsReal;
        public Instrument Instrument => _inner.Instrument;
        public Position Position => _holdingReports ? _quantityOnly : _inner.Position!;
        public IEnumerable<Order> ActiveOrders => _inner.ActiveOrders;
        public bool PositionReady => ((ITradingAccount)_inner).PositionReady;
        public bool OrdersReady => ((ITradingAccount)_inner).OrdersReady;
        public string? LastError => _inner.LastError;
        public decimal LastPrice => _inner.LastPrice;
        public event Action? Changed;

        private void OnBrokerChanged()
        {
            _quantityOnly.Volume = _inner.Position!.Volume;
            if (_exposeAccountAverage) _quantityOnly.AveragePrice = _inner.Position.AveragePrice;
            Changed?.Invoke();
        }

        public ExecutionSnapshot CaptureExecutions(long afterRevision) => _holdingReports
            ? new ExecutionSnapshot(afterRevision, Array.Empty<ExecutionReport>(), ExecutionAvailability.Synchronizing)
            : _inner.Executions.Capture(new(_inner.Executions.Epoch, afterRevision));

        public void ReleaseExecutionReports()
        {
            _holdingReports = false;
            Changed?.Invoke();
        }

        public OrderContext OrderContext => _inner.OrderContext;
        private IExecutionSource? _executions;
        public IExecutionSource Executions => _executions ??= new DelegateExecutionSource(CaptureExecutions, h => Changed += h, h => Changed -= h);
        public Order Submit(PreparedOrderIntent intent) => _inner.Submit(intent);
        public Order Replace(Order order, PreparedOrderIntent intent) => _inner.Replace(order, intent);
        public bool IsReplacementPending(Order order) => _inner.IsReplacementPending(order);
        public void Cancel(Order order) => _inner.Cancel(order);
        public void CancelAt(decimal price) => _inner.CancelAt(price);
        public void CancelAll() => _inner.CancelAll();
        public void Dispose() => _inner.Changed -= OnBrokerChanged;
    }

    /// <summary>Real simulator executions with fees not yet delivered and an explicit provider UTC execution clock.
    /// Price, quantity and scoped identities remain the simulator's facts; fee absence is never confirmed zero.</summary>
    private sealed class FeePendingAccount(SimBroker inner, Func<DateTime> providerClock) : ITradingAccount, ITrackedOrderReplacement
    {
        private readonly Dictionary<ExecutionIdentity, DateTime> _executionTimes = new();
        public string Name => inner.Name;
        public bool IsReal => inner.IsReal;
        public Instrument Instrument => inner.Instrument;
        public Position Position => inner.Position!;
        public IEnumerable<Order> ActiveOrders => inner.ActiveOrders;
        public bool PositionReady => ((ITradingAccount)inner).PositionReady;
        public bool OrdersReady => ((ITradingAccount)inner).OrdersReady;
        public string? LastError => inner.LastError;
        public decimal LastPrice => inner.LastPrice;
        public event Action? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public ExecutionSnapshot CaptureExecutions(long afterRevision)
        {
            var snapshot = inner.Executions.Capture(new(inner.Executions.Epoch, afterRevision));
            if (snapshot.Changes.Count == 0) return snapshot;
            var changes = new ExecutionReport[snapshot.Changes.Count];
            for (int i = 0; i < changes.Length; i++)
            {
                var report = snapshot.Changes[i];
                if (!_executionTimes.TryGetValue(report.Identity, out var time))
                    _executionTimes.Add(report.Identity, time = providerClock());
                changes[i] = report with { Time = time, Fees = Array.Empty<ExecutionFee>(), FeesComplete = false };
            }
            return snapshot with { Changes = changes };
        }

        public OrderContext OrderContext => inner.OrderContext;
        private IExecutionSource? _executions;
        public IExecutionSource Executions => _executions ??= new DelegateExecutionSource(CaptureExecutions, h => Changed += h, h => Changed -= h);
        public Order Submit(PreparedOrderIntent intent) => inner.Submit(intent);
        public Order Replace(Order order, PreparedOrderIntent intent) => inner.Replace(order, intent);
        public bool IsReplacementPending(Order order) => inner.IsReplacementPending(order);
        public void Cancel(Order order) => inner.Cancel(order);
        public void CancelAt(decimal price) => inner.CancelAt(price);
        public void CancelAll() => inner.CancelAll();
    }

    /// <summary>Delegates orders and initial execution facts to the real simulator. Explicit venue amendments
    /// append higher-revision facts for the same durable execution without calling any ledger method.</summary>
    private sealed class CorrectingExecutionAccount : ITradingAccount, ITrackedOrderReplacement, IDisposable
    {
        private readonly SimBroker _inner;
        private readonly List<ExecutionReport> _updates = new();
        private readonly Dictionary<ExecutionIdentity, ExecutionReport> _latest = new();
        private long _innerRevision;
        public bool HoldExecutionReports { get; set; }

        public void ReleaseExecutionReports()
        {
            HoldExecutionReports = false;
            Changed?.Invoke();
        }

        public CorrectingExecutionAccount(SimBroker inner)
        {
            _inner = inner;
            inner.Changed += OnBrokerChanged;
        }

        public string Name => _inner.Name;
        public bool IsReal => _inner.IsReal;
        public Instrument Instrument => _inner.Instrument;
        public Position Position => _inner.Position!;
        public IEnumerable<Order> ActiveOrders => _inner.ActiveOrders;
        public bool PositionReady => ((ITradingAccount)_inner).PositionReady;
        public bool OrdersReady => ((ITradingAccount)_inner).OrdersReady;
        public string? LastError => _inner.LastError;
        public decimal LastPrice => _inner.LastPrice;
        public event Action? Changed;
        private void OnBrokerChanged() => Changed?.Invoke();

        private ExecutionAvailability CaptureInner()
        {
            var snapshot = _inner.Executions.Capture(new(_inner.Executions.Epoch, _innerRevision));
            foreach (var report in snapshot.Changes)
            {
                _updates.Add(report);
                _latest[report.Identity] = report;
            }
            _innerRevision = snapshot.Revision;
            return snapshot.Availability;
        }

        public ExecutionSnapshot CaptureExecutions(long afterRevision)
        {
            var availability = CaptureInner();
            if (HoldExecutionReports)
                return new ExecutionSnapshot(afterRevision, Array.Empty<ExecutionReport>(), ExecutionAvailability.Synchronizing);
            if (afterRevision < 0 || afterRevision > _updates.Count)
                throw new ArgumentOutOfRangeException(nameof(afterRevision));
            int start = (int)afterRevision, count = _updates.Count - start;
            if (count == 0) return new ExecutionSnapshot(afterRevision, Array.Empty<ExecutionReport>(), availability);
            var changes = new ExecutionReport[count];
            _updates.CopyTo(start, changes, 0, count);
            return new ExecutionSnapshot(_updates.Count, changes, availability);
        }

        public void PublishCorrection(ExecutionIdentity identity, decimal price, decimal commission,
            decimal? quantity = null, long? localOrderId = null)
        {
            CaptureInner();
            var original = _latest[identity];
            var correction = original with
            {
                Price = price,
                Quantity = quantity ?? original.Quantity,
                LocalOrderId = localOrderId ?? original.LocalOrderId,
                Fees = new[] { new ExecutionFee(commission, original.AccountingCurrency) },
                FeesComplete = true,
            };
            _latest[identity] = correction;
            _updates.Add(correction);
            Changed?.Invoke();
        }

        public OrderContext OrderContext => _inner.OrderContext;
        private IExecutionSource? _executions;
        public IExecutionSource Executions => _executions ??= new DelegateExecutionSource(CaptureExecutions, h => Changed += h, h => Changed -= h);
        public Order Submit(PreparedOrderIntent intent) => _inner.Submit(intent);
        public Order Replace(Order order, PreparedOrderIntent intent) => _inner.Replace(order, intent);
        public bool IsReplacementPending(Order order) => _inner.IsReplacementPending(order);
        public void Cancel(Order order) => _inner.Cancel(order);
        public void CancelAt(decimal price) => _inner.CancelAt(price);
        public void CancelAll() => _inner.CancelAll();
        public void Dispose() => _inner.Changed -= OnBrokerChanged;
    }

    private sealed class ManualFeed : IMarketDataFeed
    {
        private IMarketDataSink? _sink;
        public string Id => "test";
        public string DisplayName => "Execution fixture";
        public bool SupportsHistory => true;
        public bool IsLive => false;
        public DateTime Now => T0.AddSeconds(1);
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Instrument>>(new[] { Contract });
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            onBatch(new[] { new Trade(1, T0, 110m, 1m, TradeSide.Buy) });
            return Task.CompletedTask;
        }
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            _sink = sink;
            return new Subscription(this);
        }
        public void Emit(Trade trade) => _sink!.OnTrades(Contract, new[] { trade });
        public void Dispose() => _sink = null;
        private sealed class Subscription(ManualFeed feed) : IDisposable
        {
            public void Dispose() => feed._sink = null;
        }
    }
}
