using System.Globalization;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class BinanceNativeExecutionFactsTests
{
    [Fact]
    public Task Spot_signed_trade_facts_enrich_pending_roundtrip_once_and_survive_reload() => VerifySignedTradeFacts(futures: false);

    [Fact]
    public Task Futures_signed_trade_facts_enrich_pending_roundtrip_once_and_survive_reload() => VerifySignedTradeFacts(futures: true);

    private static async Task VerifySignedTradeFacts(bool futures)
    {
        string previousRoot = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_binance_facts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        LiveStore.Root = root;
        try
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures, firstNativeId: 7001) { HoldFuturesTradeHistory = futures };
            fake.SetSpotBalance("BTC", 0m, 0m);
            var instrument = new Instrument
            {
                Symbol = "BTCUSDT", FeedId = futures ? "binance-futures" : BinanceFeed.SpotId, BaseAsset = "BTC", QuoteAsset = "USDT",
                TickSize = 0.1m, VolumeStep = 0.001m, PointValue = 1m,
            };
            using var feed = new SimulatedFeed();
            using var data = new InstrumentData(instrument, feed) { RecordLive = false };
            DateTime time = DateTime.UtcNow.AddMinutes(-5);
            for (int i = 0; i < 5; i++)
                data.Trades.Add(new Trade(i + 1, time.AddSeconds(i * 30), 170m, 1m, TradeSide.Buy));
            string contextId = Guid.NewGuid().ToString("N");
            var context = new OrderContext(new AccountContextDescriptor(contextId, "Binance", futures ? "usd-m-futures" : "spot",
                fake.Settings.RestBase, "local:" + contextId, "owned-loopback-generation"), root);
            using var broker = orders.Binance(data, fake.Settings, "Native Spot facts fixture", context);
            int HistoryReplies() => futures ? fake.FuturesTradeReplies : fake.SpotTradeReplies;
            void ReportTrades(params FakeBinanceSpotTrade[] trades)
            {
                if (futures)
                    fake.ReportFuturesTrades(trades.Select(trade => new FakeBinanceFuturesTrade(trade.Id, trade.OrderId,
                        trade.Symbol, trade.IsBuyer, trade.Quantity, trade.Price, trade.Time, trade.Commission,
                        trade.CommissionAsset, trade.IsBuyer ? 0m : 10m)).ToArray());
                else fake.ReportSpotTrades(trades);
            }
            await Until(() => broker.OrdersReady && broker.PositionReady, broker, fake);
            var strategy = new PassiveLimits();
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
            runner.Start();
            Assert.Equal(170m, broker.LastPrice);

            strategy.BuyAt(103m, 2m);
            await Until(() => fake.Placed.Count == 1 && broker.ActiveOrders.Any(o => o.ExchangeOrderId == "7001"), broker, fake);
            Order opening = Assert.Single(broker.ActiveOrders);
            Assert.Equal("7001", opening.ExchangeOrderId);
            fake.Fill(7001, 2m);
            await Until(() => runner.OwnPosition == 2m && broker.Position.Volume == 2m, broker, fake);
            Assert.Equal(2m, strategy.Position);
            Assert.True(runner.Ledger.HasPendingPrices);
            Assert.Null(runner.Ledger.AuthoritativeNet);

            // Reduction remains possible before the execution history (and its fees) arrives.
            strategy.SellAt(104m, 2m);
            await Until(() => fake.Placed.Count == 2 && broker.ActiveOrders.Any(o => o.ExchangeOrderId == "7002"), broker, fake);
            Order closing = Assert.Single(broker.ActiveOrders);
            Assert.Equal("7002", closing.ExchangeOrderId);
            fake.Fill(7002, 2m);
            await Until(() => runner.OwnPosition == 0m && broker.Position.Volume == 0m && !runner.HasWorkingOrders, broker, fake);
            Assert.Equal(0m, strategy.Position);
            Assert.Equal(LiveFinancialQuality.Pending, runner.Ledger.FinancialQuality);
            Assert.Null(runner.Ledger.AuthoritativeGross);
            Assert.Null(runner.Ledger.AuthoritativeNet);
            Assert.Empty(runner.Ledger.Fills); // order prices/cumulative quantities must not fabricate native fills
            runner.Stop(flatten: false);

            long timestamp = new DateTimeOffset(time.AddMinutes(3)).ToUnixTimeMilliseconds();
            // Identical economics/time are two distinct native trades. Prices improve both limits;
            // neither order price (103/104) nor the mark (170) is execution-price authority.
            var first = new FakeBinanceSpotTrade(91001, 7001, "BTCUSDT", true, 1m, 100m, timestamp, 0.4m, "USDT");
            var second = first with { Id = 91002 };
            var exit = new FakeBinanceSpotTrade(91003, 7002, "BTCUSDT", false, 2m, 105m, timestamp + 1000, 0.6m, "USDT");

            // Deliberately incomplete transport rows, NOT a claim that Binance normally omits this field.
            // The documented commissionAsset is required evidence before these amounts can be USDT net.
            int beforeIncomplete = HistoryReplies();
            ReportTrades(first with { CommissionAsset = null }, second with { CommissionAsset = null }, exit with { CommissionAsset = null });
            await Until(() => HistoryReplies() > beforeIncomplete, broker, fake);
            await ConsumedPolls(fake, broker);
            var pending = LiveStore.LoadLedger(root, runner.Key, 1m);
            Assert.Equal(0m, pending.Position);
            Assert.Equal(LiveFinancialQuality.Pending, pending.FinancialQuality);
            Assert.Null(pending.AuthoritativeNet);
            Assert.Null(pending.NetRealized);

            int beforeComplete = HistoryReplies();
            ReportTrades(first, second, exit);
            await Until(() => LiveStore.LoadLedger(root, runner.Key, 1m).AuthoritativeNet == 8.6m, broker, fake);
            // A second signed delivery of an already-seen native ID must not add quantity or fees.
            await Until(() => HistoryReplies() >= beforeComplete + 2, broker, fake);
            await ConsumedPolls(fake, broker);

            var reloaded = LiveStore.LoadLedger(root, runner.Key, 1m);
            Assert.False(runner.IsRunning);
            Assert.Equal(0m, reloaded.Position);
            Assert.Equal(LiveFinancialQuality.Authoritative, reloaded.FinancialQuality);
            Assert.Equal(10m, reloaded.AuthoritativeGross); // (105 - 100) * 2 * 1
            Assert.Equal(8.6m, reloaded.AuthoritativeNet); // 10 - 0.4 - 0.4 - 0.6 USDT
            Assert.Equal(8.6m, reloaded.NetRealized);
            Assert.Equal(3, reloaded.Fills.Count);
            Assert.All(reloaded.Fills, fill => Assert.Equal(LiveFillKind.Execution, fill.Kind));
            Assert.Collection(reloaded.Fills.OrderBy(fill => fill.ExecutionId!.Id, StringComparer.Ordinal),
                fill => { Assert.True(fill.Buy); Assert.Equal(1m, fill.Quantity); Assert.Equal(100m, fill.Price); },
                fill => { Assert.True(fill.Buy); Assert.Equal(1m, fill.Quantity); Assert.Equal(100m, fill.Price); },
                fill => { Assert.False(fill.Buy); Assert.Equal(2m, fill.Quantity); Assert.Equal(105m, fill.Price); });
            Assert.Equal(3, reloaded.Fills.Select(fill => fill.ExecutionId).Distinct().Count());
            // Verify the raw history trade IDs without inventing an undocumented canonical ID encoding.
            var reports = broker.Executions.Capture(ExecutionCursor.Start).Changes;
            foreach (long id in new long[] { 91001, 91002, 91003 })
                Assert.Contains(reports, report => report.Identity.Id == id.ToString(CultureInfo.InvariantCulture) &&
                    reloaded.Fills.Any(fill => fill.ExecutionId == report.Identity));
            // Three executions can produce multiple partial closing rows: assert their independent total economics.
            Assert.Equal(10m, reloaded.Trades.Sum(trade => trade.Pnl));
            Assert.Equal(1.4m, reloaded.Trades.Sum(trade => trade.Commission));
            Assert.Equal(8.6m, reloaded.Trades.Sum(trade => trade.NetPnl));
        }
        finally
        {
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Financial_publication_does_not_let_native_quantity_overtake_its_synchronizing_availability()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 0m, 0m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            using var broker = orders.Binance(data, fake.Settings, "Held financial publication", context);
            await Until(() => broker.OrdersReady && broker.PositionReady &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
            var strategy = new PassiveLimits();
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
            runner.Start();
            strategy.BuyAt(103m, 5m);
            await Until(() => fake.Placed.Count == 1 && broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0), broker, fake);
            var opening = Assert.Single(broker.ActiveOrders);
            long nativeId = long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture);
            var projector = AccountExecutionProjector.For(broker.Executions, context);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var first = new FakeBinanceSpotTrade(96001, nativeId, "BTCUSDT", true, 1m, 100m, timestamp, 0.4m, "USDT");
            var second = first with { Id = 96002 };
            var third = first with { Id = 96003, Time = timestamp + 1000 };
            fake.Fill(nativeId, 2m);
            await Until(() => runner.OwnPosition == 2m && runner.Ledger.HasPendingPrices &&
                projector.Availability == ExecutionAvailability.Synchronizing, broker, fake);

            var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publicationLeft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeReplayParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseNativeReplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeCycleCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var historyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource<(ExecutionAvailability Source, ExecutionAvailability Projector, bool Paused)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int heldPublication = 0;
            void HoldPublishedFinancialSet()
            {
                var snapshot = broker.Executions.Capture(ExecutionCursor.Start);
                if (snapshot.Availability != ExecutionAvailability.Ready ||
                    !snapshot.Changes.Any(report => report.Identity.Id == "96001") ||
                    !snapshot.Changes.Any(report => report.Identity.Id == "96002") ||
                    runner.Ledger.HasPendingFinancials || runner.Ledger.AuthoritativeNet != -0.8m ||
                    Interlocked.CompareExchange(ref heldPublication, 1, 0) != 0) return;
                // The real projector subscribed before this listener. Both native-priced fills and Ready
                // are already applied; this blocks the producer's synchronous drain, not a fake availability.
                publicationEntered.TrySetResult();
                try { releasePublication.Task.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult(); }
                finally { publicationLeft.TrySetResult(); }
            }
            void ObserveAccountNotification()
            {
                if (opening.Filled == 3m)
                    observed.TrySetResult((broker.Executions.Capture(ExecutionCursor.Start).Availability,
                        projector.Availability, runner.Paused));
            }
            // Runner account/source listeners are already attached, so this captures their actual outcome.
            broker.Executions.Changed += HoldPublishedFinancialSet;
            broker.Changed += ObserveAccountNotification;
            try
            {
                // Either worker can own the shared publication drainer. Park the native worker
                // in its real HTTP reply first, so this Ready publication is owned by financial replay.
                fake.BeforeGetReply = (request, ct) =>
                {
                    if (request.Route != "/api/v3/openOrders") return Task.CompletedTask;
                    nativeReplayParked.TrySetResult();
                    return releaseNativeReplay.Task.WaitAsync(ct);
                };
                await nativeReplayParked.Task.WaitAsync(TimeSpan.FromSeconds(12));
                fake.ReportSpotTrades(first, second);
                await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.Equal(2m, runner.OwnPosition);
                Assert.Equal(100m, runner.Ledger.AveragePrice);
                Assert.Equal(-0.8m, LiveStore.LoadLedger(root, runner.Key, 1m).AuthoritativeNet);
                fake.BeforeTradeReply = ct =>
                {
                    historyEntered.TrySetResult();
                    return releaseHistory.Task.WaitAsync(ct);
                };
                fake.BeforeGetReply = (request, _) =>
                {
                    // A *subsequent* signed openOrders request is not sent until the native loop's
                    // preceding position update and account notification attempt have returned.
                    if ((request.Route is "/api/v3/openOrders" or "/fapi/v1/openOrders") && opening.Filled == 3m && broker.Position.Volume == 3m) nativeCycleCompleted.TrySetResult();
                    return Task.CompletedTask;
                };
                fake.Fill(nativeId, 1m);
                releaseNativeReplay.TrySetResult();
                await nativeCycleCompleted.Task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.False(releasePublication.Task.IsCompleted);
                // One financial worker owns both the synchronous publication and its next HTTP request.
                // Its next reply cannot be in flight while this listener is held; do not claim otherwise.
                Assert.False(historyEntered.Task.IsCompleted);
                if (observed.Task.IsCompletedSuccessfully)
                {
                    var duringHold = observed.Task.Result;
                    Assert.Equal(ExecutionAvailability.Synchronizing, duringHold.Source);
                    Assert.Equal(ExecutionAvailability.Synchronizing, duringHold.Projector);
                    Assert.False(duringHold.Paused);
                }
                Assert.False(runner.Paused);
                Assert.True(runner.IsRunning);
                Assert.Same(opening, Assert.Single(broker.ActiveOrders));
                Assert.Equal((3m, 2m), (opening.Filled, opening.Unfilled));
                Assert.DoesNotContain(fake.Calls, call => call.StartsWith("DELETE ", StringComparison.Ordinal));

                releasePublication.TrySetResult();
                await publicationLeft.Task.WaitAsync(TimeSpan.FromSeconds(12));
                await historyEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                var notification = await observed.Task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.Equal(ExecutionAvailability.Synchronizing, notification.Source);
                Assert.Equal(ExecutionAvailability.Synchronizing, notification.Projector);
                Assert.False(notification.Paused);
                await Until(() => runner.OwnPosition == 3m && LiveStore.LoadLedger(root, runner.Key, 1m).Position == 3m, broker, fake);
                Assert.False(releaseHistory.Task.IsCompleted);
                Assert.False(runner.Paused);
                var pending = LiveStore.LoadLedger(root, runner.Key, 1m);
                Assert.True(pending.HasPendingPrices);
                Assert.Null(pending.AuthoritativeNet);
                Assert.Equal(2, pending.Fills.Count);
                Assert.Same(opening, Assert.Single(broker.ActiveOrders));
            }
            finally
            {
                broker.Executions.Changed -= HoldPublishedFinancialSet;
                broker.Changed -= ObserveAccountNotification;
                fake.BeforeGetReply = null;
                fake.BeforeTradeReply = null;
                releaseNativeReplay.TrySetResult();
                releasePublication.TrySetResult();
                releaseHistory.TrySetResult();
                if (publicationEntered.Task.IsCompletedSuccessfully)
                    await publicationLeft.Task.WaitAsync(TimeSpan.FromSeconds(12));
            }
            int replayReplies = fake.SpotTradeReplies;
            fake.ReportSpotTrades(first, second, third);
            await Until(() => runner.OwnPosition == 3m && runner.Ledger.AuthoritativeNet == -1.2m &&
                !runner.Ledger.HasPendingFinancials && fake.SpotTradeReplies >= replayReplies + 2, broker, fake);
            await ConsumedPolls(fake, broker);
            var durable = LiveStore.LoadLedger(root, runner.Key, 1m);
            Assert.Equal((3m, 100m, -1.2m), (durable.Position, durable.AveragePrice, durable.AuthoritativeNet));
            Assert.Equal(3, durable.Fills.Count);
            Assert.Equal(3, durable.Fills.Select(fill => fill.ExecutionId).Distinct().Count());
            Assert.All(durable.Fills, fill => { Assert.Equal(LiveFillKind.Execution, fill.Kind); Assert.Equal(1m, fill.Quantity); Assert.Equal(100m, fill.Price); });
            var reports = broker.Executions.Capture(ExecutionCursor.Start).Changes;
            foreach (string id in new[] { "96001", "96002", "96003" })
                Assert.Contains(reports, report => report.Identity.Id == id && report.NativeOrder == opening.NativeIdentity &&
                    durable.Fills.Any(fill => fill.ExecutionId == report.Identity));
            Assert.False(runner.Paused);
            Assert.True(runner.IsRunning);
            Assert.Same(opening, Assert.Single(broker.ActiveOrders));
            Assert.Equal(2m, opening.Unfilled);
            Assert.Single(fake.Placed);
            Assert.Single(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("DELETE ", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Queued_price_update_during_predecessor_delete_preserves_owned_working_protection(bool futures, bool conditional)
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures) { HoldFuturesTradeHistory = futures };
            fake.SetSpotBalance("BTC", 0m, 0m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures);
            var context = RecoveryContext(root, fake);
            const string key = "binance-reprice-during-delete";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            using var broker = orders.Binance(data, fake.Settings, "Queued owned protection during DELETE", context);
            var projector = AccountExecutionProjector.For(broker.Executions, context);
            projector.Synchronize();
            await Until(() => broker.OrdersReady && broker.PositionReady &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
            var entry = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Buy, 2m, 100m);
            await Until(() => entry.ExchangeOrderId.Length > 0 && fake.Sent.Count == 1, broker, fake);
            long entryId = long.Parse(entry.ExchangeOrderId, CultureInfo.InvariantCulture);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var entryFact = new FakeBinanceSpotTrade(97001, entryId, "BTCUSDT", true, 2m, 100m, timestamp, 0.4m, "USDT");
            if (futures)
            {
                fake.Fill(entryId, 2m);
                fake.ReportFuturesTrades(new FakeBinanceFuturesTrade(entryFact.Id, entryId, "BTCUSDT", true,
                    2m, 100m, timestamp, 0.4m, "USDT"));
            }
            else fake.FillWithSpotTrades(entryId, 2m, entryFact);
            await Until(() => broker.Position.Volume == 2m && LiveStore.LoadLedger(root, key, 1m).Position == 2m &&
                LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == -0.4m, broker, fake);
            decimal priceA = conditional ? 95m : 105m;
            decimal priceB = conditional ? 96m : 104m;
            decimal priceC = conditional ? 97m : 103m;
            var protection = OrderActions.Place(broker, owner, conditional ? OrderTypes.Stop : OrderTypes.Limit,
                OrderDirections.Sell, 2m, priceA, "Owned protection", reduceOnly: true, protective: true, maximumOwnedQuantity: 2m);
            await Until(() => protection.ExchangeOrderId.Length > 0 && fake.Sent.Count == 2 && !broker.IsReplacementPending(protection), broker, fake);
            string nativeA = protection.ExchangeOrderId;
            string logical = Assert.IsType<string>(protection.LogicalOrderId);
            var claimA = Assert.IsType<NativeOrderIdentity>(protection.NativeIdentity);
            var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int deletes = 0;
            fake.BeforeDeleteReply = ct =>
            {
                if (Interlocked.Increment(ref deletes) != 1) return Task.CompletedTask;
                deleteEntered.TrySetResult();
                return releaseDelete.Task.WaitAsync(ct);
            };
            void ReplacementSettled()
            {
                decimal price = conditional ? protection.TriggerPrice : protection.Price;
                if (!broker.IsReplacementPending(protection) && (protection.State != OrderStates.Active ||
                    (protection.ExchangeOrderId != nativeA && protection.ExchangeOrderId.Length > 0 && price == priceC)))
                    settled.TrySetResult();
            }
            broker.Changed += ReplacementSettled;
            try
            {
                Assert.Same(protection, OrderActions.Reprice(broker, protection, priceB));
                await deleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.Equal("CANCELED", fake.View(nativeA).Status);
                Assert.Empty(fake.Open);
                Assert.Equal(2, fake.Sent.Count);
                Assert.True(broker.IsReplacementPending(protection));
                Assert.Equal(OrderStates.Active, protection.State);
                // C is a real public price-only reprice while B awaits its successful cancellation reply.
                // No private IntentId is restored or pinned to make B's predecessor check pass.
                Assert.Same(protection, OrderActions.Reprice(broker, protection, priceC));
                Assert.True(broker.IsReplacementPending(protection));
                Assert.False(releaseDelete.Task.IsCompleted);
                Assert.Equal(2, fake.Sent.Count);
                releaseDelete.TrySetResult();
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(12));
            }
            finally
            {
                fake.BeforeDeleteReply = null;
                releaseDelete.TrySetResult();
                broker.Changed -= ReplacementSettled;
            }
            Assert.Equal(OrderStates.Active, protection.State);
            Assert.False(broker.IsReplacementPending(protection));
            Assert.Same(protection, Assert.Single(broker.ActiveOrders));
            var working = Assert.Single(fake.Open);
            Assert.NotEqual(nativeA, working.ExchangeId);
            Assert.Equal(protection.ExchangeOrderId, working.ExchangeId);
            Assert.Equal((priceC, 2m, futures), (working.Level, working.Qty, working.ReduceOnly));
            Assert.Equal((0m, 2m, 2m), (protection.Filled, protection.Unfilled, protection.QuantityToFill));
            Assert.True(protection.ReduceOnly);
            Assert.Equal((owner.Id, logical), (protection.OwnerId, protection.LogicalOrderId));
            var nativeC = Assert.IsType<NativeOrderIdentity>(protection.NativeIdentity);
            Assert.NotEqual(claimA, nativeC);
            var loaded = new OrderContext(context.Descriptor, root);
            var current = Assert.IsType<OwnedOrderIntent>(loaded.Find(protection.IntentId));
            Assert.Equal((owner, logical), (current.Owner, current.LogicalOrderId));
            Assert.Null(current.Rejection);
            Assert.True(current.Request.ReduceOnly);
            Assert.True(current.Request.Protective);
            Assert.Equal(2m, current.Request.MaximumOwnedQuantity);
            Assert.Contains(nativeC, current.NativeOrders!);
            var sent = fake.Sent;
            // Either dispatch B then C, or coalesce the now-unsent B into C; never duplicate either POST.
            Assert.InRange(sent.Count, 3, 4);
            Assert.Single(sent, order => order.Level == priceC);
            Assert.InRange(sent.Count(order => order.Level == priceB), 0, 1);
            Assert.All(sent.Where(order => order.ExchangeId != entry.ExchangeOrderId && order.ExchangeId != working.ExchangeId),
                order => Assert.Equal("CANCELED", fake.View(order.ExchangeId).Status));
            Assert.Equal(sent.Count, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
            var clientIds = fake.Placed.Select(order => order.ClientId).Concat(fake.PlacedAlgo.Select(order => order.ClientId)).ToArray();
            Assert.All(clientIds, clientId => Assert.NotEmpty(clientId));
            Assert.Equal(clientIds.Length, clientIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Single(fake.Placed, order => order.Side == "BUY");

            // Exercise the surviving protection all the way through native child binding and the real ledger.
            // The existing USD-M matching engine executes MARKET children at its documented fixture price 60000.
            fake.HoldFuturesTradeHistory = futures;
            fake.Fill(working.ExchangeId, 2m);
            long closingId = conditional
                ? fake.AlgoOrder(long.Parse(working.ExchangeId["algo:".Length..], CultureInfo.InvariantCulture)).ActualOrderId
                : long.Parse(working.ExchangeId, CultureInfo.InvariantCulture);
            decimal closePrice = conditional ? 60000m : priceC;
            decimal gross = (closePrice - 100m) * 2m;
            if (futures)
                fake.ReportFuturesTrades(
                    new FakeBinanceFuturesTrade(entryFact.Id, entryId, "BTCUSDT", true, 2m, 100m, timestamp, 0.4m, "USDT"),
                    new FakeBinanceFuturesTrade(97002, closingId, "BTCUSDT", false, 2m, closePrice, timestamp + 1000, 0.6m, "USDT", gross));
            else fake.ReportSpotTrades(entryFact,
                new FakeBinanceSpotTrade(97002, closingId, "BTCUSDT", false, 2m, closePrice, timestamp + 1000, 0.6m, "USDT"));
            int replies = futures ? fake.FuturesTradeReplies : fake.SpotTradeReplies;
            await Until(() => broker.Position.Volume == 0m && !broker.ActiveOrders.Any() &&
                LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == gross - 1m &&
                (futures ? fake.FuturesTradeReplies : fake.SpotTradeReplies) >= replies + 2, broker, fake);
            await ConsumedPolls(fake, broker);
            var durable = LiveStore.LoadLedger(root, key, 1m);
            Assert.Equal(0m, durable.Position);
            Assert.Equal(LiveFinancialQuality.Authoritative, durable.FinancialQuality);
            Assert.Equal(gross, durable.AuthoritativeGross);
            Assert.Equal(gross - 1m, durable.AuthoritativeNet);
            Assert.Equal(2, durable.Fills.Count);
            Assert.Equal(2, durable.Fills.Select(fill => fill.ExecutionId).Distinct().Count());
            Assert.All(durable.Fills, fill => { Assert.Equal(LiveFillKind.Execution, fill.Kind); Assert.Equal(2m, fill.Quantity); });
            Assert.Equal(gross, durable.Trades.Sum(trade => trade.Pnl));
            Assert.Equal(1m, durable.Trades.Sum(trade => trade.Commission));
            Assert.Equal(gross - 1m, durable.Trades.Sum(trade => trade.NetPnl));
            var report = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes, execution => execution.Identity.Id == "97002");
            Assert.Equal(protection.NativeIdentity, report.NativeOrder);
            if (conditional) Assert.NotEqual(nativeC, report.NativeOrder);
            else Assert.Equal(nativeC, report.NativeOrder);
            Assert.Contains(durable.Fills, fill => fill.ExecutionId == report.Identity && !fill.Buy && fill.Price == closePrice);
            var closingClaim = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Resolve(report));
            Assert.Equal((owner, logical), (closingClaim.Owner, closingClaim.LogicalOrderId));
            Assert.Equal(sent.Count, fake.Sent.Count);
            Assert.Equal(sent.Count, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
            Assert.Empty(fake.Refused);
            if (conditional)
            {
                Assert.Equal(OrderTypes.Market, protection.Type);
                var persisted = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(protection.IntentId));
                var evidence = Assert.IsType<NativeWorkingOrder>(persisted.WorkingOrder);
                Assert.Equal((nativeC, report.NativeOrder, OrderTypes.Market, "BTCUSDT", ""),
                    (evidence.Parent, evidence.Child, evidence.Type, evidence.NativeInstrument, evidence.NativeExchange));
                Assert.Equal(OrderTypes.Stop, persisted.Request.Type);
                Assert.Contains(nativeC, persisted.NativeOrders!);
                Assert.Contains(report.NativeOrder!, persisted.NativeOrders!);
                Assert.Equal(new NativeOrderTerminal(report.NativeOrder!, 2m), persisted.TerminalOrder);
            }
            await Until(() => broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready,
                broker, fake);
            // A terminal/economic ledger alone misses a sticky native ownership fault.
            var next = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Buy, 1m, 101m);
            await Until(() => next.ExchangeOrderId.Length > 0 && fake.Sent.Count == sent.Count + 1, broker, fake);
            Assert.Equal(OrderStates.Active, next.State);
            Assert.Equal(owner.Id, next.OwnerId);
            Assert.Single(fake.Placed, submitted => submitted.ClientId == next.ClientOrderId &&
                submitted.Id.ToString(CultureInfo.InvariantCulture) == next.ExchangeOrderId &&
                submitted.Side == "BUY" && submitted.Qty == 1m);
            Assert.NotEqual(logical, next.LogicalOrderId);
            broker.Cancel(next);
            await Until(() => next.Canceled && !broker.ActiveOrders.Any() &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
            Assert.Equal(sent.Count + 1, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
            Assert.Empty(fake.Refused);
        });
    }

    [Fact]
    public async Task Captured_spot_partial_execution_batch_does_not_pause_or_cancel_the_unfilled_opening()
    {
        string previousRoot = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_binance_batch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        LiveStore.Root = root;
        try
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 0m, 0m);
            var instrument = new Instrument
            {
                Symbol = "BTCUSDT", FeedId = BinanceFeed.SpotId, BaseAsset = "BTC", QuoteAsset = "USDT",
                TickSize = 0.1m, VolumeStep = 0.001m, PointValue = 1m,
            };
            using var feed = new SimulatedFeed();
            using var data = new InstrumentData(instrument, feed) { RecordLive = false };
            DateTime time = DateTime.UtcNow.AddMinutes(-5);
            for (int i = 0; i < 5; i++)
                data.Trades.Add(new Trade(i + 1, time.AddSeconds(i * 30), 170m, 1m, TradeSide.Buy));
            string contextId = Guid.NewGuid().ToString("N");
            var context = new OrderContext(new AccountContextDescriptor(contextId, "Binance", "spot",
                fake.Settings.RestBase, "local:" + contextId, "owned-loopback-batch"), root);
            using var broker = orders.Binance(data, fake.Settings, "Native partial batch fixture", context);
            await Until(() => broker.OrdersReady && broker.PositionReady &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
            var strategy = new PassiveLimits();
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
            runner.Start();
            strategy.BuyAt(103m, 5m);
            await Until(() => fake.Placed.Count == 1 && broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0), broker, fake);
            var opening = Assert.Single(broker.ActiveOrders);
            long nativeOrderId = long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            fake.FillWithSpotTrades(nativeOrderId, 2m,
                new FakeBinanceSpotTrade(92001, nativeOrderId, "BTCUSDT", true, 1m, 100m, timestamp, 0.4m, "USDT"),
                new FakeBinanceSpotTrade(92002, nativeOrderId, "BTCUSDT", true, 1m, 100m, timestamp, 0.4m, "USDT"));
            await Until(() => runner.OwnPosition == 2m && runner.Ledger.AveragePrice == 100m &&
                !runner.Ledger.HasPendingFinancials, broker, fake);
            await ConsumedPolls(fake, broker);
            Assert.False(runner.Paused);
            Assert.True(runner.IsRunning);
            Assert.Same(opening, Assert.Single(broker.ActiveOrders));
            Assert.Equal(3m, opening.Unfilled);
            Assert.Equal(2m, broker.Position.Volume);
            var durable = LiveStore.LoadLedger(root, runner.Key, 1m);
            Assert.Equal(2m, durable.Position);
            Assert.Equal(100m, durable.AveragePrice);
            Assert.Equal(-0.8m, durable.AuthoritativeNet);
            Assert.Equal(2, durable.Fills.Count);
        }
        finally
        {
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Held_spot_history_does_not_block_a_bounded_close_or_authorize_new_exposure()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 0m, 0m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            string key;
            using (var first = orders.Binance(data, fake.Settings, "Held history recovery", context))
            {
                await Until(() => first.OrdersReady && first.PositionReady, first, fake);
                var strategy = new PassiveLimits();
                using var runner = new LiveStrategyRunner(strategy, data, first, ChartPeriod.M1);
                runner.Start();
                key = runner.Key;
                strategy.BuyAt(103m, 2m);
                await Until(() => fake.Placed.Count == 1 && first.ActiveOrders.Any(o => o.ExchangeOrderId == "7001"), first, fake);
                fake.Fill(7001, 2m);
                // Genuine native cumulative quantity, projected by the actual account projector; no test-created
                // execution or quantity-observation row is injected into the ledger.
                await Until(() => runner.OwnPosition == 2m && first.Position.Volume == 2m, first, fake);
                Assert.True(LiveStore.LoadLedger(root, key, 1m).HasPendingPrices);
                runner.Stop(flatten: false);
            }

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.BeforeTradeReply = async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            };
            try
            {
                // A fresh source starts Synchronizing: the earlier empty history reply cannot masquerade as
                // financial readiness for this incarnation. This is in-process recreation, not an OS crash.
                var loaded = new OrderContext(context.Descriptor, root);
                using var broker = orders.Binance(data, fake.Settings, "Held history recovery", loaded);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.True(broker.PositionReady);
                Assert.Equal(2m, broker.Position.Volume);
                Assert.Equal(ExecutionAvailability.Synchronizing, broker.Executions.Capture(ExecutionCursor.Start).Availability);
                using var resumed = new LiveStrategyRunner(new PassiveLimits(), data, broker, ChartPeriod.M1);
                resumed.Start();
                Assert.Equal(key, resumed.Key);
                Assert.True(resumed.Ledger.HasPendingPrices);
                Assert.Null(resumed.Ledger.AuthoritativeNet);

                var opening = resumed.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 99m, "Blocked opening");
                Assert.Equal(StrategyOrderState.Canceled, opening.State);
                Assert.Single(fake.Placed);
                var close = resumed.Submit(OrderSide.Sell, StrategyOrderType.Limit, 2m, 104m, "Bounded close");
                // The reply stays held for this entire assertion. The handler's wait is outside _gate, so
                // native POST acceptance is independent evidence, not a matching-engine-lock deadlock.
                await Until(() => fake.Placed.Count == 2, broker, fake);
                Assert.False(release.Task.IsCompleted);
                Assert.True(broker.OrdersReady);
                var sent = fake.Placed[1];
                Assert.Equal(("SELL", 2m, 104m), (sent.Side, sent.Qty, sent.Price));
                var live = Assert.IsType<Order>(close.Live);
                var intent = Assert.IsType<OwnedOrderIntent>(loaded.Find(live.IntentId));
                Assert.True(intent.Request.ReduceOnly);
                Assert.Equal(2m, intent.Request.MaximumOwnedQuantity);
                // Spot has no native reduceOnly parameter; assert the durable bounded reduction request,
                // not a made-up vendor field. The actual native POST is SELL 2 against observed BTC 2.
                Assert.Equal(ExecutionAvailability.Synchronizing, broker.Executions.Capture(ExecutionCursor.Start).Availability);
                Assert.True(LiveStore.LoadLedger(root, key, 1m).HasPendingPrices);
                Assert.Null(LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet);
                release.TrySetResult();
                fake.BeforeTradeReply = null;
                await Until(() => live.ExchangeOrderId == sent.Id.ToString(CultureInfo.InvariantCulture), broker, fake);
                resumed.Stop(flatten: false);
                await Until(() => !broker.ActiveOrders.Any(), broker, fake);
                Assert.Empty(fake.Refused);
            }
            finally
            {
                release.TrySetResult();
                fake.BeforeTradeReply = null;
            }
        });
    }

    [Fact]
    public async Task Broker_recreation_recovers_an_accepted_unbound_client_intent_without_resubmitting_or_claiming_a_lookalike()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false) { HideOwnedOrders = true };
            fake.SetSpotBalance("BTC", 0m, 0m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            const string key = "binance-accepted-before-bind-recreation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.BeforePostReply = async ct =>
            {
                entered.TrySetResult();
                try { await release.Task.WaitAsync(ct); }
                finally { left.TrySetResult(); }
            };
            string intentId, logicalId, clientId;
            long nativeId;
            try
            {
                using (var first = orders.Binance(data, fake.Settings, "Accepted before bind", context))
                {
                    await Until(() => first.OrdersReady && first.PositionReady, first, fake);
                    var opening = OrderActions.Place(first, owner, OrderTypes.Limit, OrderDirections.Buy, 2m, 103m);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                    var accepted = Assert.Single(fake.Placed);
                    nativeId = accepted.Id;
                    intentId = Assert.IsType<string>(opening.IntentId);
                    logicalId = Assert.IsType<string>(opening.LogicalOrderId);
                    clientId = Assert.IsType<string>(opening.ClientOrderId);
                    Assert.Equal(clientId, accepted.ClientId);
                    Assert.Equal("", opening.ExchangeOrderId);
                    Assert.Null(opening.NativeIdentity);
                    var persisted = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(intentId));
                    Assert.Equal(owner, persisted.Owner);
                    Assert.Empty(persisted.NativeOrders ?? Array.Empty<NativeOrderIdentity>());
                    // Dispose before releasing the successful POST reply. No replay, native bind, or transient
                    // _byClient dictionary from this broker may contribute ownership to the replacement.
                }
                release.TrySetResult();
                await left.Task.WaitAsync(TimeSpan.FromSeconds(12));
                fake.BeforePostReply = null;
                fake.HideOwnedOrders = false;
                string foreignClient = clientId[..^1] + (clientId[^1] == '0' ? "1" : "0");
                long foreignId = fake.AddExternal("BUY", "LIMIT", 2m, 103m, clientId: foreignClient);
                var loaded = new OrderContext(context.Descriptor, root);
                Assert.NotSame(context, loaded);
                Assert.Empty(Assert.IsType<OwnedOrderIntent>(loaded.Find(intentId)).NativeOrders ?? Array.Empty<NativeOrderIdentity>());
                using var broker = orders.Binance(data, fake.Settings, "Accepted before bind", loaded);
                var projector = AccountExecutionProjector.For(broker.Executions, loaded);
                projector.Synchronize();
                await Until(() => broker.OrdersReady && broker.PositionReady && broker.ActiveOrders.Count() == 2, broker, fake);
                var recovered = Assert.Single(broker.ActiveOrders, order => order.ExchangeOrderId == nativeId.ToString(CultureInfo.InvariantCulture));
                var foreign = Assert.Single(broker.ActiveOrders, order => order.ExchangeOrderId == foreignId.ToString(CultureInfo.InvariantCulture));
                Assert.Equal((clientId, owner.Id, intentId, logicalId),
                    (recovered.ClientOrderId, recovered.OwnerId, recovered.IntentId, recovered.LogicalOrderId));
                Assert.Equal(foreignClient, foreign.ClientOrderId);
                Assert.Null(foreign.OwnerId);
                Assert.Null(foreign.IntentId);
                Assert.Null(foreign.LogicalOrderId);
                Assert.Single(fake.Placed);

                fake.Fill(nativeId, 2m);
                fake.Fill(foreignId, 2m);
                long timestamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
                fake.ReportSpotTrades(
                    new FakeBinanceSpotTrade(92001, nativeId, "BTCUSDT", true, 2m, 100m, timestamp, 0.4m, "USDT"),
                    new FakeBinanceSpotTrade(92002, foreignId, "BTCUSDT", true, 2m, 999m, timestamp + 1, 9m, "USDT"));
                await Until(() => LiveStore.LoadLedger(root, key, 1m).Fills.Any(fill => fill.Kind == LiveFillKind.Execution), broker, fake);
                await ConsumedPolls(fake, broker);
                projector.Synchronize();
                var ledger = LiveStore.LoadLedger(root, key, 1m);
                Assert.Equal(4m, broker.Position.Volume); // both venue fills, only one durable owner
                Assert.Equal(2m, ledger.Position);
                Assert.Equal(100m, ledger.AveragePrice);
                Assert.False(ledger.HasPendingPrices);
                var fill = Assert.Single(ledger.Fills, fill => fill.Kind == LiveFillKind.Execution);
                Assert.Equal((2m, 100m, logicalId, owner.Id), (fill.Quantity, fill.Price, fill.OrderId, fill.OwnerId));
                Assert.Equal("92001", fill.ExecutionId!.Id);
                var fee = Assert.Single(fill.Fees!);
                Assert.Equal((0.4m, "USDT"), (fee.Amount, fee.Currency));
                Assert.True(fill.FeesComplete);
                Assert.Equal(1, fake.Calls.Count(call => call == "POST /api/v3/order"));
                Assert.DoesNotContain(broker.Executions.Capture(ExecutionCursor.Start).Changes, report => report.Identity.Id == "92002");
                Assert.Empty(loaded.PendingReceipts());
                var durable = new OrderContext(context.Descriptor, root);
                var claim = Assert.IsType<OwnedOrderIntent>(durable.Find(intentId));
                Assert.Contains(Assert.IsType<NativeOrderIdentity>(recovered.NativeIdentity), claim.NativeOrders!);
                Assert.Equal(owner, claim.Owner);
                Assert.Equal(fill.ExecutionId, Assert.IsType<ExecutionProjectionCheckpoint>(durable.Projected(fill.ExecutionId!)).Report.Identity);
                Assert.Empty(fake.Refused);
            }
            finally
            {
                release.TrySetResult();
                fake.BeforePostReply = null;
            }
        });
    }

    [Fact]
    public async Task Adopting_a_partially_filled_manual_native_target_excludes_its_durable_execution_prefix_and_corrections()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 3m, 0m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            using var broker = orders.Binance(data, fake.Settings, "Native target adoption", context);
            await Until(() => broker.OrdersReady && broker.PositionReady, broker, fake);
            var target = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 3m, 104m, "Manual target", reduceOnly: true);
            await Until(() => target.ExchangeOrderId == "7001", broker, fake);
            string manualIntent = Assert.IsType<string>(target.IntentId);
            var native = Assert.IsType<NativeOrderIdentity>(target.NativeIdentity);
            var prepared = Assert.IsType<OwnedOrderIntent>(context.Find(manualIntent));
            Assert.Equal(context.ManualOwner, prepared.Owner);
            Assert.Contains(native, prepared.NativeOrders!);
            fake.Fill(7001, 1m);
            long timestamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            var prior = new FakeBinanceSpotTrade(93001, 7001, "BTCUSDT", false, 1m, 105m, timestamp, 0.2m, "USDT");
            fake.ReportSpotTrades(prior);
            await Until(() => target.Filled == 1m && broker.Position.Volume == 2m &&
                broker.Executions.Capture(ExecutionCursor.Start).Changes.Any(report => report.Identity.Id == "93001") &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
            var baseline = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes, report => report.Identity.Id == "93001");
            Assert.Equal((native, manualIntent, prepared.ClientOrderId), (baseline.NativeOrder, baseline.IntentId, baseline.ClientOrderId));
            Assert.Equal(1m, baseline.Quantity);
            Assert.True(baseline.FeesComplete);

            using var runner = new LiveStrategyRunner(new PassiveLimits(), data, broker, ChartPeriod.M1)
            {
                ProtectOnStart = true,
                AdoptProtectionIds = new[] { target.ExchangeOrderId },
            };
            // Explicit inherited-position fixture only. This legacy row is not called a native execution and
            // is not the result under test; every adopted execution must come from signed fake REST history.
            LiveStore.AppendFill(runner.Key, new LiveFill(DateTime.UtcNow.AddDays(-1), true, 2m, 100m, "BTCUSDT", 1m));
            runner.Start();
            Assert.Equal(2m, runner.OwnPosition);
            Assert.Equal(new[] { "7001" }, runner.ProtectionOrderIds);
            var adopted = Assert.IsType<OwnedOrderIntent>(context.Find(target.IntentId));
            Assert.NotEqual(manualIntent, adopted.Id);
            Assert.Equal(OrderOwnerKind.Strategy, adopted.Owner.Kind);
            Assert.True(adopted.BaselineKnown);
            Assert.Equal(1m, adopted.QuantityBaseline);
            Assert.Equal(new[] { baseline.Identity }, adopted.PriorExecutions);
            Assert.Contains(native, adopted.NativeOrders!);
            Assert.Single(fake.Placed);

            fake.Fill(7001, 1m);
            var after = new FakeBinanceSpotTrade(93002, 7001, "BTCUSDT", false, 1m, 107m, timestamp + 1000, 0.3m, "USDT");
            fake.ReportSpotTrades(prior, after);
            await Until(() => runner.OwnPosition == 1m && broker.Position.Volume == 1m &&
                LiveStore.LoadLedger(root, runner.Key, 1m).Fills.Any(fill => fill.ExecutionId?.Id == "93002"), broker, fake);
            AssertAdoptedEconomics();
            int replies = fake.SpotTradeReplies;
            fake.ReportSpotTrades(prior with { Price = 1000m, Commission = 9m }, after);
            await Until(() => fake.SpotTradeReplies > replies, broker, fake);
            await ConsumedPolls(fake, broker);
            AssertAdoptedEconomics();
            var loaded = new OrderContext(context.Descriptor, root);
            var durable = Assert.IsType<OwnedOrderIntent>(loaded.Find(adopted.Id));
            Assert.True(durable.BaselineKnown);
            Assert.Equal(adopted.PriorExecutions, durable.PriorExecutions);
            Assert.Equal(1, fake.Calls.Count(call => call == "POST /api/v3/order"));
            runner.Stop(flatten: false, keepProtection: true);
            Assert.Empty(fake.Refused);

            void AssertAdoptedEconomics()
            {
                foreach (var ledger in new[] { runner.Ledger, LiveStore.LoadLedger(root, runner.Key, 1m) })
                {
                    Assert.Equal(1m, ledger.Position);
                    Assert.False(ledger.HasPendingPrices);
                    var fill = Assert.Single(ledger.Fills, fill => fill.Kind == LiveFillKind.Execution);
                    Assert.Equal(("93002", 1m, 107m, adopted.LogicalOrderId),
                        (fill.ExecutionId!.Id, fill.Quantity, fill.Price, fill.OrderId));
                    Assert.DoesNotContain(ledger.Fills, fill => fill.ExecutionId == baseline.Identity);
                    Assert.Equal(7m, ledger.Trades.Sum(trade => trade.Pnl));
                    Assert.Equal(0.3m, ledger.Trades.Sum(trade => trade.Commission));
                    Assert.Equal(6.7m, ledger.Trades.Sum(trade => trade.NetPnl));
                }
                Assert.Equal(1m, runner.OwnPosition);
                Assert.Equal(1m, broker.Position.Volume);
                Assert.Equal(1m, target.Unfilled);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Healthy_native_replay_keeps_projecting_owned_fills_while_signed_history_is_held(bool futures)
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures);
            fake.SetSpotBalance("BTC", 0m, 0m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures);
            var context = RecoveryContext(root, fake);
            var settings = fake.Settings;
            settings.RequestTimeout = TimeSpan.FromMinutes(2);
            using var broker = orders.Binance(data, settings, "Native polling during held history", context);
            await Until(() => broker.OrdersReady && broker.PositionReady &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
            var strategy = new PassiveLimits();
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
            runner.Start();
            strategy.BuyAt(103m, 5m);
            await Until(() => fake.Placed.Count == 1 && broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length > 0), broker, fake);
            var opening = Assert.Single(broker.ActiveOrders);
            long nativeId = long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.BeforeTradeReply = ct =>
            {
                entered.TrySetResult();
                return release.Task.WaitAsync(ct);
            };
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                // This is AFTER successful native and financial replay and AFTER accepting the owned order.
                // The fake barrier snapshots history outside _gate; Fill remains an actual venue mutation.
                Assert.True(broker.OrdersReady);
                Assert.True(broker.PositionReady);
                fake.Fill(nativeId, 2m);
                await Until(() => opening.Filled == 2m && opening.Unfilled == 3m && broker.Position.Volume == 2m &&
                    runner.OwnPosition == 2m && strategy.Position == 2m &&
                    LiveStore.LoadLedger(root, runner.Key, 1m).Position == 2m, broker, fake);
                Assert.False(release.Task.IsCompleted);
                Assert.True(runner.IsRunning);
                Assert.False(runner.Paused);
                Assert.Same(opening, Assert.Single(broker.ActiveOrders));
                Assert.Single(fake.Placed);
                Assert.Single(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
                var pending = LiveStore.LoadLedger(root, runner.Key, 1m);
                Assert.True(pending.HasPendingPrices);
                Assert.Null(pending.AuthoritativeNet);
                Assert.DoesNotContain(pending.Fills, fill => fill.Kind == LiveFillKind.Execution);
            }
            finally
            {
                fake.BeforeTradeReply = null;
                release.TrySetResult();
            }
            runner.Stop(flatten: false);
            await Until(() => !broker.ActiveOrders.Any(), broker, fake);
        });
    }

    [Fact]
    public async Task Adopted_external_limit_with_client_identity_recovers_its_existing_claim_after_broker_recreation()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 0m, 0m);
            long externalId = fake.AddExternal("BUY", "LIMIT", 5m, 103m, clientId: "external-adopted-limit");
            long foreignId = fake.AddExternal("BUY", "LIMIT", 5m, 103m, clientId: "external-adopted-limits");
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            const string key = "binance-adopted-external-limit-recreation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            OwnedOrderIntent adopted;
            NativeOrderIdentity native;
            using (var first = orders.Binance(data, fake.Settings, "Adopted external recovery", context))
            {
                await Until(() => first.OrdersReady && first.PositionReady && first.ActiveOrders.Count() == 2, first, fake);
                var external = Assert.Single(first.ActiveOrders, order => order.ExchangeOrderId == externalId.ToString(CultureInfo.InvariantCulture));
                native = Assert.IsType<NativeOrderIdentity>(external.NativeIdentity);
                Assert.Equal("external-adopted-limit", external.ClientOrderId);
                adopted = context.Adopt(external, owner, 0m, Array.Empty<ExecutionIdentity>(), baselineKnown: true);
                Assert.True(adopted.Adopted);
                Assert.False(adopted.DispatchUncertain);
                Assert.Contains(native, adopted.NativeOrders!);
            }
            var loaded = new OrderContext(context.Descriptor, root);
            Assert.NotSame(context, loaded);
            var persisted = Assert.IsType<OwnedOrderIntent>(loaded.Find(adopted.Id));
            Assert.Equal((adopted.Id, adopted.Owner, adopted.LogicalOrderId, adopted.ClientOrderId),
                (persisted.Id, persisted.Owner, persisted.LogicalOrderId, persisted.ClientOrderId));
            Assert.Contains(native, persisted.NativeOrders!);
            using var broker = orders.Binance(data, fake.Settings, "Adopted external recovery", loaded);
            var projector = AccountExecutionProjector.For(broker.Executions, loaded);
            projector.Synchronize();
            await Until(() => broker.OrdersReady && broker.PositionReady && broker.ActiveOrders.Count() == 2 &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
            var recovered = Assert.Single(broker.ActiveOrders, order => order.ExchangeOrderId == externalId.ToString(CultureInfo.InvariantCulture));
            var foreign = Assert.Single(broker.ActiveOrders, order => order.ExchangeOrderId == foreignId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal((owner.Id, adopted.Id, adopted.LogicalOrderId, adopted.ClientOrderId, native),
                (recovered.OwnerId, recovered.IntentId, recovered.LogicalOrderId, recovered.ClientOrderId, recovered.NativeIdentity));
            Assert.Equal((0m, 5m), (recovered.Filled, recovered.Unfilled));
            Assert.Null(foreign.OwnerId);
            Assert.Null(foreign.IntentId);
            Assert.Null(foreign.LogicalOrderId);
            fake.Fill(externalId, 2m);
            fake.Fill(foreignId, 1m);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            fake.ReportSpotTrades(
                new FakeBinanceSpotTrade(94001, externalId, "BTCUSDT", true, 2m, 100m, timestamp, 0.4m, "USDT"),
                new FakeBinanceSpotTrade(94002, foreignId, "BTCUSDT", true, 1m, 999m, timestamp + 1, 9m, "USDT"));
            await Until(() => recovered.Filled == 2m && recovered.Unfilled == 3m && broker.Position.Volume == 3m &&
                LiveStore.LoadLedger(root, key, 1m).Fills.Any(fill => fill.ExecutionId?.Id == "94001"), broker, fake);
            await ConsumedPolls(fake, broker);
            var ledger = LiveStore.LoadLedger(root, key, 1m);
            Assert.Equal((2m, 100m, -0.4m), (ledger.Position, ledger.AveragePrice, ledger.AuthoritativeNet));
            var fill = Assert.Single(ledger.Fills, fill => fill.Kind == LiveFillKind.Execution);
            Assert.Equal(("94001", adopted.LogicalOrderId, owner.Id), (fill.ExecutionId!.Id, fill.OrderId, fill.OwnerId));
            Assert.DoesNotContain(broker.Executions.Capture(ExecutionCursor.Start).Changes, report => report.Identity.Id == "94002");
            Assert.Empty(fake.Sent);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
            Assert.Null(broker.LastError);
            var durable = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(adopted.Id));
            Assert.True(durable.Adopted);
            Assert.False(durable.DispatchUncertain);
            Assert.Contains(native, durable.NativeOrders!);
        });
    }

    [Fact]
    public async Task Terminal_unbound_usdm_stop_recovers_exact_client_parent_and_projects_its_closing_child_once()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: true);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures: true);
            var context = RecoveryContext(root, fake);
            const string key = "binance-terminal-unbound-stop-recreation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            var settings = fake.Settings;
            settings.RequestTimeout = TimeSpan.FromMinutes(2);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string intentId, logicalId, clientId;
            long parentId;
            try
            {
                using (var first = orders.Binance(data, settings, "Terminal unbound conditional recovery", context))
                {
                    var firstProjector = AccountExecutionProjector.For(first.Executions, context);
                    firstProjector.Synchronize();
                    await Until(() => first.OrdersReady && first.PositionReady &&
                        first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                    var opening = OrderActions.Place(first, owner, OrderTypes.Limit, OrderDirections.Buy, 2m, 59990m);
                    await Until(() => opening.ExchangeOrderId.Length > 0, first, fake);
                    long openingId = long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture);
                    fake.Fill(openingId, 2m);
                    await Until(() => first.Position.Volume == 2m && LiveStore.LoadLedger(root, key, 1m).Position == 2m &&
                        LiveStore.LoadLedger(root, key, 1m).AveragePrice == 59990m &&
                        LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 0m, first, fake);
                    fake.HideOwnedOrders = true;
                    fake.BeforePostReply = async ct =>
                    {
                        entered.TrySetResult();
                        try { await release.Task.WaitAsync(ct); }
                        finally { left.TrySetResult(); }
                    };
                    var stop = OrderActions.Place(first, owner, OrderTypes.Stop, OrderDirections.Sell, 2m, 59000m,
                        "Native holding stop", reduceOnly: true, protective: true, maximumOwnedQuantity: 2m);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                    var accepted = Assert.Single(fake.PlacedAlgo);
                    parentId = accepted.AlgoId;
                    intentId = Assert.IsType<string>(stop.IntentId);
                    logicalId = Assert.IsType<string>(stop.LogicalOrderId);
                    clientId = Assert.IsType<string>(stop.ClientOrderId);
                    Assert.Equal(("STOP_MARKET", "SELL", 2m, true, clientId),
                        (accepted.Type, accepted.Side, accepted.Qty, accepted.ReduceOnly, accepted.ClientId));
                    Assert.Empty(stop.ExchangeOrderId);
                    Assert.Null(stop.NativeIdentity);
                    var beforeBind = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(intentId));
                    Assert.True(beforeBind.DispatchUncertain);
                    Assert.Equal(owner, beforeBind.Owner);
                    Assert.Empty(beforeBind.NativeOrders ?? Array.Empty<NativeOrderIdentity>());
                    // Dispose the actual broker before permitting the acknowledgement to return or any bind.
                }
                release.TrySetResult();
                await left.Task.WaitAsync(TimeSpan.FromSeconds(12));
                fake.BeforePostReply = null;
                long childId = fake.Trigger(parentId, fill: 2m);
                Assert.Equal("FINISHED", fake.AlgoOrder(parentId).Status);
                Assert.Equal(childId, fake.AlgoOrder(parentId).ActualOrderId);
                Assert.Equal(("MARKET", "FILLED", 2m), (fake.Order(childId).Type, fake.Order(childId).Status, fake.Order(childId).Executed));
                Assert.Empty(fake.Open);
                fake.HideOwnedOrders = false;
                int lookupStart = fake.ClientLookups.Count;
                var loaded = new OrderContext(context.Descriptor, root);
                Assert.Empty(Assert.IsType<OwnedOrderIntent>(loaded.Find(intentId)).NativeOrders ?? Array.Empty<NativeOrderIdentity>());
                using var broker = orders.Binance(data, settings, "Terminal unbound conditional recovery", loaded);
                var projector = AccountExecutionProjector.For(broker.Executions, loaded);
                projector.Synchronize();
                await Until(() => broker.OrdersReady && broker.PositionReady && broker.Position.Volume == 0m &&
                    LiveStore.LoadLedger(root, key, 1m).Position == 0m &&
                    LiveStore.LoadLedger(root, key, 1m).Fills.Any(fill => fill.Kind == LiveFillKind.Execution && !fill.Buy), broker, fake);
                await ConsumedPolls(fake, broker);
                Assert.Contains(fake.ClientLookups.Skip(lookupStart), lookup => lookup.ClientId == clientId && lookup.Status == 200);
                Assert.Single(fake.PlacedAlgo);
                Assert.Single(fake.Placed); // Only the original BUY; the triggered child is venue-created.
                Assert.Equal(2, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
                Assert.Empty(broker.ActiveOrders);
                var ledger = LiveStore.LoadLedger(root, key, 1m);
                var closing = Assert.Single(ledger.Fills, fill => fill.Kind == LiveFillKind.Execution && !fill.Buy);
                Assert.Equal((false, 2m, 60000m, logicalId, owner.Id),
                    (closing.Buy, closing.Quantity, closing.Price, closing.OrderId, closing.OwnerId));
                Assert.Equal((20m, 20m), (ledger.AuthoritativeGross, ledger.AuthoritativeNet));
                Assert.Equal(20m, ledger.Trades.Sum(trade => trade.NetPnl));
                Assert.True(closing.FeesComplete);
                var checkpointContext = new OrderContext(context.Descriptor, root);
                var recovered = Assert.IsType<OwnedOrderIntent>(checkpointContext.Find(intentId));
                Assert.Equal((owner, logicalId, clientId, context.Descriptor.Id, context.Descriptor.Generation),
                    (recovered.Owner, recovered.LogicalOrderId, recovered.ClientOrderId, recovered.ContextId, recovered.Generation));
                Assert.Contains(new NativeOrderIdentity("binance:algo", parentId.ToString(CultureInfo.InvariantCulture)), recovered.NativeOrders!);
                Assert.Contains(new NativeOrderIdentity("binance:order", "BTCUSDT:" + childId.ToString(CultureInfo.InvariantCulture)), recovered.NativeOrders!);
                var report = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes,
                    report => report.Identity == closing.ExecutionId);
                Assert.Equal((intentId, clientId, false, 2m, 60000m),
                    (report.IntentId, report.ClientOrderId, report.Buy, report.Quantity, report.Price));
                Assert.Equal(closing.ExecutionId, Assert.IsType<ExecutionProjectionCheckpoint>(checkpointContext.Projected(closing.ExecutionId!)).Report.Identity);
                Assert.Empty(checkpointContext.PendingReceipts());
            }
            finally
            {
                fake.BeforePostReply = null;
                release.TrySetResult();
            }
        });
    }

    [Fact]
    public async Task Recovered_replacement_projects_parent_plus_child_native_quantity_before_held_price_history()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 0m, 0m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            const string key = "binance-replacement-cumulative-native-recreation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            var settings = fake.Settings;
            settings.RequestTimeout = TimeSpan.FromMinutes(2);
            string parentIntentId, childIntentId, logicalId;
            NativeOrderIdentity parentNative, childNative;
            long childId;
            using (var first = orders.Binance(data, settings, "Replacement cumulative recovery", context))
            {
                var projector = AccountExecutionProjector.For(first.Executions, context);
                projector.Synchronize();
                await Until(() => first.OrdersReady && first.PositionReady &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                var opening = OrderActions.Place(first, owner, OrderTypes.Limit, OrderDirections.Buy, 5m, 103m);
                await Until(() => opening.ExchangeOrderId.Length > 0 && fake.Placed.Count == 1, first, fake);
                long parentId = long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture);
                parentIntentId = Assert.IsType<string>(opening.IntentId);
                logicalId = Assert.IsType<string>(opening.LogicalOrderId);
                parentNative = Assert.IsType<NativeOrderIdentity>(opening.NativeIdentity);
                fake.Fill(parentId, 2m);
                await Until(() => opening.Filled == 2m && first.Position.Volume == 2m &&
                    LiveStore.LoadLedger(root, key, 1m).Position == 2m, first, fake);
                Assert.True(LiveStore.LoadLedger(root, key, 1m).HasPendingPrices);
                OrderActions.Replace(first, opening, 102m, 3m);
                await Until(() => fake.Placed.Count == 2 && opening.ExchangeOrderId.Length > 0 &&
                    opening.ExchangeOrderId != parentId.ToString(CultureInfo.InvariantCulture) && !first.IsReplacementPending(opening), first, fake);
                childId = long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture);
                childNative = Assert.IsType<NativeOrderIdentity>(opening.NativeIdentity);
                childIntentId = Assert.IsType<string>(opening.IntentId);
                Assert.Equal((2m, 3m), (opening.Filled, opening.Unfilled));
                Assert.Equal(("CANCELED", 2m), (fake.Order(parentId).Status, fake.Order(parentId).Executed));
                Assert.Equal(("BUY", 3m, 102m), (fake.Order(childId).Side, fake.Order(childId).Qty, fake.Order(childId).Price));
                Assert.Equal(parentIntentId, Assert.IsType<OwnedOrderIntent>(context.Find(childIntentId)).PredecessorId);
            }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.BeforeTradeReply = ct =>
            {
                entered.TrySetResult();
                return release.Task.WaitAsync(ct);
            };
            try
            {
                var loaded = new OrderContext(context.Descriptor, root);
                Assert.Equal(2m, Assert.IsType<OwnedOrderIntent>(loaded.Find(logicalId)).QuantityProjected);
                var parent = Assert.IsType<OwnedOrderIntent>(loaded.Find(parentIntentId));
                var child = Assert.IsType<OwnedOrderIntent>(loaded.Find(childIntentId));
                Assert.Equal((owner, logicalId, parentIntentId), (child.Owner, child.LogicalOrderId, child.PredecessorId));
                Assert.Contains(parentNative, parent.NativeOrders!);
                Assert.Contains(childNative, child.NativeOrders!);
                using var broker = orders.Binance(data, settings, "Replacement cumulative recovery", loaded);
                var projector = AccountExecutionProjector.For(broker.Executions, loaded);
                projector.Synchronize();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.True(broker.OrdersReady);
                Assert.True(broker.PositionReady);
                Assert.Equal(2m, broker.Position.Volume);
                var recovered = Assert.Single(broker.ActiveOrders);
                Assert.Equal((childId.ToString(CultureInfo.InvariantCulture), owner.Id, childIntentId, logicalId),
                    (recovered.ExchangeOrderId, recovered.OwnerId, recovered.IntentId, recovered.LogicalOrderId));
                // Finding 1 is a prerequisite to observing finding 4: never release history to force a green result.
                fake.Fill(childId, 1m);
                await Until(() => recovered.Filled == 3m && recovered.Unfilled == 2m && broker.Position.Volume == 3m &&
                    LiveStore.LoadLedger(root, key, 1m).Position == 3m, broker, fake);
                Assert.False(release.Task.IsCompleted);
                Assert.Same(recovered, Assert.Single(broker.ActiveOrders));
                var pending = LiveStore.LoadLedger(root, key, 1m);
                Assert.True(pending.HasPendingPrices);
                Assert.Null(pending.AuthoritativeNet);
                Assert.DoesNotContain(pending.Fills, fill => fill.Kind == LiveFillKind.Execution);
                Assert.Equal(3m, Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(logicalId)).QuantityProjected);
                // Bounded protection is sized to the logical quantity of three, not child-only one or original five.
                var protection = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Sell, 3m, 104m,
                    "Recovered logical quantity protection", reduceOnly: true, protective: true, maximumOwnedQuantity: 3m);
                await Until(() => fake.Placed.Count == 3, broker, fake);
                Assert.False(release.Task.IsCompleted);
                var sent = fake.Placed[2];
                Assert.Equal(("SELL", 3m, 104m), (sent.Side, sent.Qty, sent.Price));
                var protectiveIntent = Assert.IsType<OwnedOrderIntent>(loaded.Find(protection.IntentId));
                Assert.True(protectiveIntent.Request.ReduceOnly);
                Assert.True(protectiveIntent.Request.Protective);
                Assert.Equal(3m, protectiveIntent.Request.MaximumOwnedQuantity);
                Assert.Equal(2, fake.Placed.Count(order => order.Side == "BUY"));
                Assert.Equal(3, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
            }
            finally
            {
                fake.BeforeTradeReply = null;
                release.TrySetResult();
            }
        });
    }


    [Fact]
    public async Task RecoveredUnfilledOwnedOpeningBlocksResetAndItsLaterFillRemainsOwnedAndProtected()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 0m, 0m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            string key, intentId, logicalId, clientId;
            long nativeId;
            Guid generation;
            OrderOwner owner;
            NativeOrderIdentity native;
            const string accountName = "Recovered working strategy opening";
            using (var first = orders.Binance(data, fake.Settings, accountName, context))
            {
                await Until(() => first.OrdersReady && first.PositionReady &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                // Obtain the real consumer key without ever subscribing a predecessor runner. The opening
                // uses the public prepared-order path with its exact durable strategy owner and admission.
                using (var keyProbe = new LiveStrategyRunner(new PassiveLimits(), data, first, ChartPeriod.M1))
                    key = keyProbe.Key;
                generation = LiveStore.CaptureLedgerGeneration(root, key);
                owner = context.LedgerOwner(key, generation);
                var opening = OrderActions.Place(first, owner, OrderTypes.Limit, OrderDirections.Buy, 2m, 173m);
                await Until(() => opening.ExchangeOrderId.Length > 0, first, fake);
                nativeId = long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture);
                native = Assert.IsType<NativeOrderIdentity>(opening.NativeIdentity);
                intentId = Assert.IsType<string>(opening.IntentId);
                logicalId = Assert.IsType<string>(opening.LogicalOrderId);
                clientId = Assert.IsType<string>(opening.ClientOrderId);
                Assert.Equal((0m, 2m, owner.Id), (opening.Filled, opening.Unfilled, opening.OwnerId));
                Assert.Equal(0m, first.Position.Volume);
                Assert.Single(fake.Placed);
            }

            // In-process broker AND context recreation, not an OS crash. The old broker's maps and any
            // predecessor subscription cannot supply the recovered order's ownership.
            var loaded = new OrderContext(context.Descriptor, root);
            Assert.NotSame(context, loaded);
            var persisted = Assert.IsType<OwnedOrderIntent>(loaded.Find(intentId));
            Assert.Equal(owner, persisted.Owner);
            Assert.Contains(native, persisted.NativeOrders!);
            Assert.Equal((context.Descriptor.Id, context.Descriptor.Generation, logicalId, clientId),
                (persisted.ContextId, persisted.Generation, persisted.LogicalOrderId, persisted.ClientOrderId));
            Assert.Equal((OrderDirections.Buy, OrderTypes.Limit, 2m, 173m),
                (persisted.Request.Direction, persisted.Request.Type, persisted.Request.Quantity, persisted.Request.Price));
            Assert.False(persisted.Request.ReduceOnly);
            using var broker = orders.Binance(data, fake.Settings, accountName, loaded);
            await Until(() => broker.OrdersReady && broker.PositionReady &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready &&
                broker.ActiveOrders.Any(order => order.ExchangeOrderId == nativeId.ToString(CultureInfo.InvariantCulture)), broker, fake);
            var recovered = Assert.Single(broker.ActiveOrders);
            Assert.Equal((intentId, logicalId, clientId, owner.Id, native),
                (recovered.IntentId, recovered.LogicalOrderId, recovered.ClientOrderId, recovered.OwnerId, recovered.NativeIdentity));
            Assert.Equal((0m, 2m), (recovered.Filled, recovered.Unfilled));
            var strategy = new PassiveLimits { StopLossTicks = 5 };
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
            runner.Start();
            Assert.Equal(key, runner.Key);
            Assert.Equal(0m, runner.OwnPosition);
            Assert.Empty(runner.Ledger.Fills);
            Assert.Single(broker.ActiveOrders);
            Assert.Single(fake.Placed); // recovery itself must not POST

            Assert.False(runner.ResetLedger());
            Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root, key));
            Assert.True(runner.IsRunning);
            Assert.Equal(owner, Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(intentId)).Owner);

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Price equals the fixed mark so a temporary quantity-first protection anchor and the
            // signed execution price agree. Limit price 173 remains explicitly non-authoritative.
            fake.FillWithSpotTrades(nativeId, 2m,
                new FakeBinanceSpotTrade(94001, nativeId, "BTCUSDT", true, 2m, 170m, timestamp, 0.4m, "USDT"));
            await Until(() => runner.OwnPosition == 2m && broker.Position.Volume == 2m &&
                runner.Ledger.AveragePrice == 170m && !runner.Ledger.HasPendingFinancials &&
                broker.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && order.ExchangeOrderId.Length > 0), broker, fake);
            await ConsumedPolls(fake, broker);
            Assert.Equal(2m, runner.VirtualPosition);
            Assert.Equal(2m, strategy.Position);
            Assert.Equal(2, fake.Placed.Count); // original opening plus one acknowledged stop, no replay or duplicate
            var stop = Assert.Single(broker.ActiveOrders);
            Assert.Equal((OrderTypes.Stop, OrderDirections.Sell, 2m, 169.5m),
                (stop.Type, stop.Direction, stop.Unfilled, stop.TriggerPrice));
            Assert.Equal(owner.Id, stop.OwnerId);
            var protection = Assert.IsType<OwnedOrderIntent>(loaded.Find(stop.IntentId));
            Assert.Equal(owner, protection.Owner);
            Assert.True(protection.Request.Protective);
            Assert.True(protection.Request.ReduceOnly);
            Assert.Equal(2m, protection.Request.MaximumOwnedQuantity);
            Assert.Equal(new[] { stop.ExchangeOrderId }, runner.ProtectionOrderIds);
            var sentStop = Assert.Single(fake.Placed, order => order.Id != nativeId);
            Assert.Equal(("SELL", "STOP_LOSS", 2m, 169.5m),
                (sentStop.Side, sentStop.Type, sentStop.Qty, sentStop.StopPrice));
            foreach (var ledger in new[] { runner.Ledger, LiveStore.LoadLedger(root, key, 1m) })
            {
                Assert.Equal(2m, ledger.Position);
                Assert.Equal(170m, ledger.AveragePrice);
                Assert.Equal(-0.4m, ledger.AuthoritativeNet);
                var fill = Assert.Single(ledger.Fills);
                Assert.Equal(LiveFillKind.Execution, fill.Kind);
                Assert.Equal((2m, 170m, logicalId, owner.Id, generation),
                    (fill.Quantity, fill.Price, fill.OrderId, fill.OwnerId, fill.OwnerGeneration));
                Assert.Equal("94001", fill.ExecutionId!.Id);
                Assert.Equal((0.4m, "USDT"), (Assert.Single(fill.Fees!).Amount, Assert.Single(fill.Fees!).Currency));
                Assert.Empty(ledger.Trades);
            }
            var durable = new OrderContext(context.Descriptor, root);
            var unchanged = Assert.IsType<OwnedOrderIntent>(durable.Find(intentId));
            Assert.Equal(persisted.Request, unchanged.Request);
            var execution = Assert.Single(runner.Ledger.Fills).ExecutionId!;
            Assert.Equal(execution, Assert.IsType<ExecutionProjectionCheckpoint>(durable.Projected(execution)).Report.Identity);
            Assert.Empty(durable.PendingReceipts());
            Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root, key));
            Assert.Empty(fake.Refused);
        });
    }

    [Fact]
    public async Task Adopted_usdm_stop_that_fills_while_disposed_recovers_exact_parent_claim_with_empty_open_lists()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: true);
            long parentId = fake.AddExternalAlgo("BUY", "STOP", 3m, triggerPrice: 61000m, price: 61100m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures: true);
            var context = RecoveryContext(root, fake);
            const string key = "binance-adopted-stop-filled-offline";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            OwnedOrderIntent adopted;
            NativeOrderIdentity parentNative;
            using (var first = orders.Binance(data, fake.Settings, "Adopted offline STOP", context))
            {
                await ConditionalState(() => first.OrdersReady && first.PositionReady && first.ActiveOrders.Any(), first, fake);
                var external = Assert.Single(first.ActiveOrders);
                parentNative = Assert.IsType<NativeOrderIdentity>(external.NativeIdentity);
                adopted = context.Adopt(external, owner, 0m, Array.Empty<ExecutionIdentity>(), baselineKnown: true);
                Assert.Equal((true, false, OrderTypes.Stop, 3m, 61000m),
                    (adopted.Adopted, adopted.DispatchUncertain, adopted.Request.Type, adopted.Request.Quantity, adopted.Request.Price));
                Assert.Contains(parentNative, adopted.NativeOrders!);
                Assert.Equal(("NEW", 0L), (fake.AlgoOrder(parentId).Status, fake.AlgoOrder(parentId).ActualOrderId));
            }
            // The first broker is disposed. No consumer or replacement broker exists during either execution.
            long childId = fake.Trigger(parentId, fill: 1m);
            fake.Fill(childId, 2m);
            var childNative = new NativeOrderIdentity("binance:order", "BTCUSDT:" + childId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(("FINISHED", childId), (fake.AlgoOrder(parentId).Status, fake.AlgoOrder(parentId).ActualOrderId));
            Assert.Equal(("LIMIT", "FILLED", 3m, 61100m),
                (fake.Order(childId).Type, fake.Order(childId).Status, fake.Order(childId).Executed, fake.Order(childId).Price));
            Assert.Empty(fake.Open);
            Assert.Empty(fake.OpenOrders);
            var loaded = new OrderContext(context.Descriptor, root);
            var before = Assert.IsType<OwnedOrderIntent>(loaded.Find(adopted.Id));
            Assert.True(before.Adopted);
            Assert.False(before.DispatchUncertain);
            Assert.Contains(parentNative, before.NativeOrders!);
            Assert.DoesNotContain(childNative, before.NativeOrders!);
            Assert.Empty(LiveStore.LoadLedger(root, key, 1m).Fills);
            int regularLists = 0, conditionalLists = 0, exactParentLookups = 0;
            var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.BeforeGetReply = (request, ct) =>
            {
                if (request.Route == "/fapi/v1/openOrders") Interlocked.Increment(ref regularLists);
                if (request.Route == "/fapi/v1/openAlgoOrders") Interlocked.Increment(ref conditionalLists);
                if (request.Route == "/fapi/v1/algoOrder" &&
                    request.Query("algoId") == parentId.ToString(CultureInfo.InvariantCulture))
                    Interlocked.Increment(ref exactParentLookups);
                // Avoid coupling this lineage test to pre-projector capture: native replay cannot consume
                // its first list until the real projector is attached. The response body is already empty.
                return request.Route == "/fapi/v1/openOrders" ? releaseStartup.Task.WaitAsync(ct) : Task.CompletedTask;
            };
            try
            {
                using var broker = orders.Binance(data, fake.Settings, "Adopted offline STOP", loaded);
                var projector = AccountExecutionProjector.For(broker.Executions, loaded);
                projector.Synchronize();
                releaseStartup.TrySetResult();
                await ConditionalState(() => broker.OrdersReady && broker.PositionReady && broker.Position.Volume == 3m &&
                    LiveStore.LoadLedger(root, key, 1m).Position == 3m &&
                    LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 0m &&
                    broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
                await ConditionalPolls(fake, broker);
                Assert.Equal(1, Volatile.Read(ref exactParentLookups));
                Assert.Empty(broker.ActiveOrders);
                Assert.Empty(fake.Open); // both GET lists remained genuinely empty through recovery
                Assert.Empty(fake.Sent);
                Assert.DoesNotContain(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
                var ledger = LiveStore.LoadLedger(root, key, 1m);
                Assert.Equal((3m, 61100m, 0m), (ledger.Position, ledger.AveragePrice, ledger.AuthoritativeNet));
                Assert.Equal(LiveFinancialQuality.Authoritative, ledger.FinancialQuality);
                Assert.Equal(new[] { "91001", "91002" }, ledger.Fills.Select(fill => fill.ExecutionId!.Id).OrderBy(id => id));
                Assert.Equal(3m, ledger.Fills.Sum(fill => fill.Quantity));
                Assert.All(ledger.Fills, fill => Assert.Equal((LiveFillKind.Execution, true, 61100m, owner.Id, adopted.LogicalOrderId, true),
                    (fill.Kind, fill.Buy, fill.Price, fill.OwnerId, fill.OrderId, fill.FeesComplete)));
                var disk = new OrderContext(context.Descriptor, root);
                var recovered = Assert.IsType<OwnedOrderIntent>(disk.Find(adopted.Id));
                Assert.Equal((adopted.Request, adopted.Owner, adopted.ClientOrderId, 3m),
                    (recovered.Request, recovered.Owner, recovered.ClientOrderId, recovered.QuantityProjected));
                Assert.Contains(parentNative, recovered.NativeOrders!);
                Assert.Contains(childNative, recovered.NativeOrders!);
                Assert.Equal(new NativeWorkingOrder(parentNative, childNative, OrderTypes.Limit, "BTCUSDT", ""), recovered.WorkingOrder);
                Assert.Equal(new NativeOrderTerminal(childNative, 3m), recovered.TerminalOrder);
                foreach (var fill in ledger.Fills)
                {
                    var report = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes,
                        report => report.Identity == fill.ExecutionId);
                    Assert.Equal((adopted.Id, childNative), (Assert.IsType<OwnedOrderIntent>(disk.Resolve(report)).Id, report.NativeOrder));
                    Assert.Equal(fill.ExecutionId, Assert.IsType<ExecutionProjectionCheckpoint>(disk.Projected(fill.ExecutionId!)).Report.Identity);
                }
                Assert.Empty(disk.PendingReceipts());
                await ConditionalPolls(fake, broker);
                Assert.Equal(1, Volatile.Read(ref exactParentLookups));
                Assert.Equal(2, LiveStore.LoadLedger(root, key, 1m).Fills.Count);
                // New non-protective logical work must pass real producer admission, not just a readiness property.
                var next = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Buy, 1m, 61200m);
                await ConditionalState(() => next.ExchangeOrderId.Length > 0 && fake.Sent.Count == 1, broker, fake);
                Assert.Equal((owner.Id, next.ExchangeOrderId), (next.OwnerId, Assert.Single(fake.Sent).ExchangeId));
                Assert.NotEqual(adopted.LogicalOrderId, next.LogicalOrderId);
                fake.Fill(long.Parse(next.ExchangeOrderId, CultureInfo.InvariantCulture), 1m);
                await ConditionalState(() => LiveStore.LoadLedger(root, key, 1m).Position == 4m &&
                    LiveStore.LoadLedger(root, key, 1m).Fills.Count == 3 &&
                    broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
                var admitted = LiveStore.LoadLedger(root, key, 1m);
                Assert.Equal((4m, 61125m, 0m), (admitted.Position, admitted.AveragePrice, admitted.AuthoritativeNet));
                Assert.Contains(admitted.Fills, fill => fill.ExecutionId!.Id == "91003" && fill.Quantity == 1m &&
                    fill.Price == 61200m && fill.OrderId == next.LogicalOrderId && fill.OwnerId == owner.Id);
                Assert.Single(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
                Assert.Null(broker.LastError);
                Assert.Empty(fake.Refused);
            }
            finally
            {
                releaseStartup.TrySetResult();
                fake.BeforeGetReply = null;
            }
        });
    }

    [Fact]
    public async Task Superseded_manual_stop_is_not_startup_eligible_and_its_adopted_claim_recovers_offline_fills()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: true);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures: true);
            var context = RecoveryContext(root, fake);
            const string key = "binance-superseded-manual-stop-offline";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            OwnedOrderIntent original, adopted;
            NativeOrderIdentity parentNative;
            long parentId;
            using (var first = orders.Binance(data, fake.Settings, "Manual stop adopted before offline fill", context))
            {
                await ConditionalState(() => first.OrdersReady && first.PositionReady &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                // A is a real locally dispatched manual BUY stop, not an external imported/adopted parent.
                var manual = OrderActions.Manual(first, OrderTypes.Stop, OrderDirections.Buy, 3m, 61000m,
                    "Manual parent subsequently adopted by strategy");
                await ConditionalState(() => manual.ExchangeOrderId.StartsWith("algo:", StringComparison.Ordinal) &&
                    manual.NativeIdentity != null &&
                    new OrderContext(context.Descriptor, root).Find(manual.IntentId)?.NativeOrders?.Contains(manual.NativeIdentity) == true,
                    first, fake);
                parentId = Assert.Single(fake.PlacedAlgo).AlgoId;
                parentNative = Assert.IsType<NativeOrderIdentity>(manual.NativeIdentity);
                original = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(manual.IntentId));
                Assert.Equal((context.ManualOwner, false, true, OrderTypes.Stop, OrderDirections.Buy, 3m, 61000m),
                    (original.Owner, original.Adopted, original.DispatchUncertain, original.Request.Type,
                        original.Request.Direction, original.Request.Quantity, original.Request.Price));
                Assert.False(string.IsNullOrEmpty(original.ClientOrderId));
                Assert.Contains(parentNative, original.NativeOrders!);
                Assert.Equal(("STOP_MARKET", "NEW", 0L, original.ClientOrderId),
                    (fake.AlgoOrder(parentId).Type, fake.AlgoOrder(parentId).Status,
                        fake.AlgoOrder(parentId).ActualOrderId, fake.AlgoOrder(parentId).ClientId));
                Assert.Equal(original.Id, Assert.Single(new OrderContext(context.Descriptor, root)
                    .RecoverableNativeSubmissions(data.Instrument.Key, OrderTypes.Stop)).Id);

                // B explicitly takes the SAME acknowledged parent with a known zero execution prefix.
                // A remains immutable history; revocation of startup eligibility must follow the current claim.
                adopted = context.Adopt(manual, owner, 0m, Array.Empty<ExecutionIdentity>(), baselineKnown: true);
                Assert.NotEqual(original.Id, adopted.Id);
                Assert.NotEqual(original.LogicalOrderId, adopted.LogicalOrderId);
                Assert.Equal((owner, true, false, 0m, true, original.ClientOrderId),
                    (adopted.Owner, adopted.Adopted, adopted.DispatchUncertain, adopted.QuantityBaseline,
                        adopted.BaselineKnown, adopted.ClientOrderId));
                Assert.Empty(adopted.PriorExecutions!);
                Assert.Equal(new[] { parentNative }, adopted.NativeOrders!);
                Assert.Equal((adopted.Id, adopted.LogicalOrderId, owner.Id, parentNative),
                    (manual.IntentId, manual.LogicalOrderId, manual.OwnerId, manual.NativeIdentity));
            }

            // The broker is disposed before either native execution. The immutable original remains unresolved
            // by its own counters; only the current native claim can determine whether it is a startup candidate.
            long childId = fake.Trigger(parentId, fill: 1m);
            fake.Fill(childId, 2m);
            var childNative = new NativeOrderIdentity("binance:order", "BTCUSDT:" + childId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(("FINISHED", childId), (fake.AlgoOrder(parentId).Status, fake.AlgoOrder(parentId).ActualOrderId));
            Assert.Equal(("MARKET", "FILLED", 3m),
                (fake.Order(childId).Type, fake.Order(childId).Status, fake.Order(childId).Executed));
            Assert.Empty(fake.Open);
            // Unrelated regular work must stay visible and unowned throughout recovery and admission.
            long foreignId = fake.AddExternal("SELL", "LIMIT", 1m, 65000m, clientId: "foreign-unadopted-stop-recovery");
            var loaded = new OrderContext(context.Descriptor, root);
            AssertOriginalUnchanged(loaded);
            Assert.Empty(LiveStore.LoadLedger(root, key, 1m).Fills);

            // Public ownership-recovery eligibility contract: no queue mutation, fake enumeration, GUID pin,
            // file-order assumption or sorting. This fails deterministically if both A and B are eligible.
            var candidates = loaded.RecoverableNativeSubmissions(data.Instrument.Key, OrderTypes.Stop);
            Assert.DoesNotContain(candidates, candidate => candidate.Id == original.Id);
            Assert.Equal(adopted.Id, Assert.Single(candidates).Id);
            Assert.DoesNotContain(childNative, Assert.IsType<OwnedOrderIntent>(loaded.Find(adopted.Id)).NativeOrders!);

            int exactParentLookups = 0, supersededClientLookups = 0;
            var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.BeforeGetReply = (request, ct) =>
            {
                if (request.Route == "/fapi/v1/algoOrder")
                {
                    if (request.Query("algoId") == parentId.ToString(CultureInfo.InvariantCulture))
                        Interlocked.Increment(ref exactParentLookups);
                    if (request.Query("clientAlgoId") == original.ClientOrderId)
                        Interlocked.Increment(ref supersededClientLookups);
                }
                // Hold only the real startup transport response until the actual projector subscribes.
                return request.Route == "/fapi/v1/openOrders" ? releaseStartup.Task.WaitAsync(ct) : Task.CompletedTask;
            };
            try
            {
                using var broker = orders.Binance(data, fake.Settings, "Manual stop adopted before offline fill", loaded);
                var projector = AccountExecutionProjector.For(broker.Executions, loaded);
                projector.Synchronize();
                releaseStartup.TrySetResult();
                await ConditionalState(() => broker.OrdersReady && broker.PositionReady && broker.Position.Volume == 3m &&
                    LiveStore.LoadLedger(root, key, 1m).Position == 3m &&
                    LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 0m &&
                    broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
                await ConditionalPolls(fake, broker);
                Assert.Equal(1, Volatile.Read(ref exactParentLookups));
                Assert.Equal(0, Volatile.Read(ref supersededClientLookups));
                Assert.Equal(ExecutionAvailability.Ready, projector.Availability);
                var foreign = Assert.Single(broker.ActiveOrders);
                Assert.Equal(foreignId.ToString(CultureInfo.InvariantCulture), foreign.ExchangeOrderId);
                Assert.Null(foreign.OwnerId);
                Assert.Null(foreign.IntentId);
                Assert.Null(foreign.LogicalOrderId);
                Assert.Single(fake.PlacedAlgo);
                Assert.Empty(fake.Placed);
                Assert.Single(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));

                var ledger = LiveStore.LoadLedger(root, key, 1m);
                Assert.Equal((3m, 60000m, 0m, 0m),
                    (ledger.Position, ledger.AveragePrice, ledger.AuthoritativeGross, ledger.AuthoritativeNet));
                Assert.Equal(LiveFinancialQuality.Authoritative, ledger.FinancialQuality);
                Assert.False(ledger.HasPendingFinancials);
                Assert.Empty(ledger.Trades);
                Assert.Equal(new[] { "91001", "91002" }, ledger.Fills.Select(fill => fill.ExecutionId!.Id).OrderBy(id => id));
                Assert.Equal(new[] { 1m, 2m }, ledger.Fills.OrderBy(fill => fill.ExecutionId!.Id).Select(fill => fill.Quantity));
                Assert.All(ledger.Fills, fill => Assert.Equal(
                    (LiveFillKind.Execution, true, 60000m, owner.Id, owner.LedgerGeneration, adopted.LogicalOrderId, true),
                    (fill.Kind, fill.Buy, fill.Price, fill.OwnerId, fill.OwnerGeneration, fill.OrderId, fill.FeesComplete)));
                var disk = new OrderContext(context.Descriptor, root);
                AssertOriginalUnchanged(disk);
                var recovered = Assert.IsType<OwnedOrderIntent>(disk.Find(adopted.Id));
                Assert.Equal((adopted.Owner, adopted.Request, adopted.ClientOrderId, 3m),
                    (recovered.Owner, recovered.Request, recovered.ClientOrderId, recovered.QuantityProjected));
                Assert.Contains(parentNative, recovered.NativeOrders!);
                Assert.Contains(childNative, recovered.NativeOrders!);
                Assert.Equal(new NativeWorkingOrder(parentNative, childNative, OrderTypes.Market, "BTCUSDT", ""), recovered.WorkingOrder);
                Assert.Equal(new NativeOrderTerminal(childNative, 3m), recovered.TerminalOrder);
                Assert.Empty(disk.RecoverableNativeSubmissions(data.Instrument.Key, OrderTypes.Stop));
                var facts = broker.Executions.Capture(ExecutionCursor.Start);
                Assert.Equal(2, facts.Changes.Count);
                foreach (var fill in ledger.Fills)
                {
                    var report = Assert.Single(facts.Changes, report => report.Identity == fill.ExecutionId);
                    Assert.Equal((adopted.Id, adopted.ClientOrderId, childNative, fill.Quantity, 60000m),
                        (report.IntentId, report.ClientOrderId, report.NativeOrder, report.Quantity, report.Price));
                    Assert.Equal(adopted.Id, Assert.IsType<OwnedOrderIntent>(disk.Resolve(report)).Id);
                    var projected = Assert.IsType<ExecutionProjectionCheckpoint>(disk.Projected(fill.ExecutionId!));
                    Assert.Equal((owner, adopted.LogicalOrderId, report.Identity),
                        (projected.Owner, projected.LogicalOrderId, projected.Report.Identity));
                }
                Assert.Empty(disk.PendingReceipts());
                await ConditionalPolls(fake, broker);
                Assert.Equal(1, Volatile.Read(ref exactParentLookups));
                Assert.Equal(0, Volatile.Read(ref supersededClientLookups));
                Assert.Equal(2, LiveStore.LoadLedger(root, key, 1m).Fills.Count);

                // Exercise actual non-protective producer admission, acceptance, signed fill and projection.
                var next = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Buy, 1m, 61200m);
                await ConditionalState(() => next.ExchangeOrderId.Length > 0 && fake.Placed.Count == 1, broker, fake);
                Assert.Equal((owner.Id, next.ExchangeOrderId),
                    (next.OwnerId, Assert.Single(fake.Placed).Id.ToString(CultureInfo.InvariantCulture)));
                Assert.NotEqual(adopted.LogicalOrderId, next.LogicalOrderId);
                fake.Fill(long.Parse(next.ExchangeOrderId, CultureInfo.InvariantCulture), 1m);
                await ConditionalState(() => broker.Position.Volume == 4m &&
                    LiveStore.LoadLedger(root, key, 1m).Position == 4m &&
                    LiveStore.LoadLedger(root, key, 1m).Fills.Count == 3 &&
                    broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
                var admitted = LiveStore.LoadLedger(root, key, 1m);
                Assert.Equal((4m, 60300m, 0m), (admitted.Position, admitted.AveragePrice, admitted.AuthoritativeNet));
                Assert.Contains(admitted.Fills, fill => fill.ExecutionId!.Id == "91003" && fill.Quantity == 1m &&
                    fill.Price == 61200m && fill.OrderId == next.LogicalOrderId && fill.OwnerId == owner.Id);
                Assert.Same(foreign, Assert.Single(broker.ActiveOrders));
                Assert.Null(foreign.OwnerId);
                Assert.Equal(("NEW", 0m), (fake.Order(foreignId).Status, fake.Order(foreignId).Executed));
                Assert.Equal(2, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
                Assert.Equal(1, Volatile.Read(ref exactParentLookups));
                Assert.Equal(0, Volatile.Read(ref supersededClientLookups));
                AssertOriginalUnchanged(new OrderContext(context.Descriptor, root));
                Assert.Null(broker.LastError);
                Assert.Empty(fake.Refused);
            }
            finally
            {
                releaseStartup.TrySetResult();
                fake.BeforeGetReply = null;
            }

            void AssertOriginalUnchanged(OrderContext current)
            {
                var historical = Assert.IsType<OwnedOrderIntent>(current.Find(original.Id));
                // Records contain array-valued fields: compare those by sequence and all other frozen data by value.
                Assert.Equal(original with { NativeOrders = null, PriorExecutions = null },
                    historical with { NativeOrders = null, PriorExecutions = null });
                Assert.Equal(original.NativeOrders, historical.NativeOrders);
                Assert.Equal(original.PriorExecutions, historical.PriorExecutions);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adopted_external_usdm_stop_limit_recovers_before_trigger_and_projects_child_once(bool listedWhileTriggered)
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: true) { ListTriggeredAlgoOrders = listedWhileTriggered };
            long parentId = fake.AddExternalAlgo("BUY", "STOP", 3m, triggerPrice: 61000m, price: 61100m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures: true);
            var context = RecoveryContext(root, fake);
            const string key = "binance-adopted-stop-limit-before-trigger-recreation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            OwnedOrderIntent adopted;
            NativeOrderIdentity parentNative;
            using (var first = orders.Binance(data, fake.Settings, "Adopted stop-limit recovery", context))
            {
                await ConditionalState(() => first.OrdersReady && first.PositionReady && first.ActiveOrders.Any(), first, fake);
                var external = Assert.Single(first.ActiveOrders);
                Assert.Equal("algo:" + parentId.ToString(CultureInfo.InvariantCulture), external.ExchangeOrderId);
                Assert.Null(external.OwnerId);
                parentNative = Assert.IsType<NativeOrderIdentity>(external.NativeIdentity);
                adopted = context.Adopt(external, owner, 0m, Array.Empty<ExecutionIdentity>(), baselineKnown: true);
                var durable = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(adopted.Id));
                Assert.True(durable.Adopted);
                Assert.False(durable.DispatchUncertain);
                Assert.Contains(parentNative, durable.NativeOrders!);
            }
            // Recreate both objects while the external STOP parent is still NEW, not after Follow has run.
            Assert.Equal(("STOP", "NEW", 0L),
                (fake.AlgoOrder(parentId).Type, fake.AlgoOrder(parentId).Status, fake.AlgoOrder(parentId).ActualOrderId));
            var loaded = new OrderContext(context.Descriptor, root);
            Assert.NotSame(context, loaded);
            using var broker = orders.Binance(data, fake.Settings, "Adopted stop-limit recovery", loaded);
            var projector = AccountExecutionProjector.For(broker.Executions, loaded);
            projector.Synchronize();
            await ConditionalState(() => broker.OrdersReady && broker.PositionReady && broker.ActiveOrders.Any(), broker, fake);
            var recovered = Assert.Single(broker.ActiveOrders);
            Assert.Equal((owner.Id, adopted.Id, adopted.LogicalOrderId, adopted.ClientOrderId, parentNative),
                (recovered.OwnerId, recovered.IntentId, recovered.LogicalOrderId, recovered.ClientOrderId, recovered.NativeIdentity));
            long childId = fake.Trigger(parentId, fill: 1m);
            var childNative = new NativeOrderIdentity("binance:order", "BTCUSDT:" + childId.ToString(CultureInfo.InvariantCulture));
            await ConditionalState(() => recovered.ExchangeOrderId == childId.ToString(CultureInfo.InvariantCulture) && recovered.Filled == 1m &&
                LiveStore.LoadLedger(root, key, 1m).AveragePrice == 61100m, broker, fake);
            Assert.Same(recovered, Assert.Single(broker.ActiveOrders));
            Assert.Equal((OrderTypes.Limit, 61100m, 3m, 1m, 2m),
                (recovered.Type, recovered.Price, recovered.QuantityToFill, recovered.Filled, recovered.Unfilled));
            Assert.Equal((parentId, childId), (fake.Order(childId).PlacedBy, fake.AlgoOrder(parentId).ActualOrderId));
            fake.Fill(childId, 2m);
            await ConditionalState(() => !broker.ActiveOrders.Any() && LiveStore.LoadLedger(root, key, 1m).Position == 3m &&
                LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 0m, broker, fake);
            await ConditionalPolls(fake, broker);
            var ledger = LiveStore.LoadLedger(root, key, 1m);
            Assert.Equal((3m, 61100m, 0m), (ledger.Position, ledger.AveragePrice, ledger.AuthoritativeNet));
            var fills = ledger.Fills.Where(fill => fill.Kind == LiveFillKind.Execution).ToArray();
            Assert.Equal(2, fills.Length);
            Assert.Equal(new[] { "91001", "91002" }, fills.Select(fill => fill.ExecutionId!.Id).OrderBy(id => id));
            Assert.Equal(3m, fills.Sum(fill => fill.Quantity));
            Assert.All(fills, fill =>
            {
                Assert.Equal((true, 61100m, adopted.LogicalOrderId, owner.Id, true),
                    (fill.Buy, fill.Price, fill.OrderId, fill.OwnerId, fill.FeesComplete));
            });
            var checkpoint = new OrderContext(context.Descriptor, root);
            var claim = Assert.IsType<OwnedOrderIntent>(checkpoint.Find(adopted.Id));
            Assert.Equal((owner, adopted.LogicalOrderId, adopted.Request, 3m),
                (claim.Owner, claim.LogicalOrderId, claim.Request, claim.QuantityProjected));
            Assert.Contains(parentNative, claim.NativeOrders!);
            Assert.Contains(childNative, claim.NativeOrders!);
            Assert.All(fills, fill => Assert.Equal(fill.ExecutionId,
                Assert.IsType<ExecutionProjectionCheckpoint>(checkpoint.Projected(fill.ExecutionId!)).Report.Identity));
            Assert.Empty(checkpoint.PendingReceipts());
            Assert.Empty(fake.Sent);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
            Assert.Null(broker.LastError);
            Assert.Empty(fake.Refused);
        });
    }

    [Fact]
    public async Task Adopted_external_spot_stop_loss_limit_recovers_same_native_claim_and_projects_native_fills_once()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 0m, 0m);
            // Spot stop-limit executions retain the original regular order ID; no fabricated Algo/child is introduced.
            long nativeId = fake.AddExternal("BUY", "STOP_LOSS_LIMIT", 3m, 103m, stopPrice: 104m, clientId: "external-stop-limit");
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            const string key = "binance-adopted-spot-stop-limit-recreation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            OwnedOrderIntent adopted;
            NativeOrderIdentity native;
            using (var first = orders.Binance(data, fake.Settings, "Adopted Spot stop-limit recovery", context))
            {
                await ConditionalState(() => first.OrdersReady && first.PositionReady && first.ActiveOrders.Any(), first, fake);
                var external = Assert.Single(first.ActiveOrders);
                native = Assert.IsType<NativeOrderIdentity>(external.NativeIdentity);
                adopted = context.Adopt(external, owner, 0m, Array.Empty<ExecutionIdentity>(), baselineKnown: true);
                Assert.Contains(native, Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(adopted.Id)).NativeOrders!);
            }
            var loaded = new OrderContext(context.Descriptor, root);
            using var broker = orders.Binance(data, fake.Settings, "Adopted Spot stop-limit recovery", loaded);
            var projector = AccountExecutionProjector.For(broker.Executions, loaded);
            projector.Synchronize();
            await ConditionalState(() => broker.OrdersReady && broker.PositionReady && broker.ActiveOrders.Any(), broker, fake);
            var recovered = Assert.Single(broker.ActiveOrders);
            Assert.Equal((owner.Id, adopted.Id, adopted.LogicalOrderId, adopted.ClientOrderId, native),
                (recovered.OwnerId, recovered.IntentId, recovered.LogicalOrderId, recovered.ClientOrderId, recovered.NativeIdentity));
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var firstTrade = new FakeBinanceSpotTrade(94101, nativeId, "BTCUSDT", true, 1m, 102m, timestamp, 0.1m, "USDT");
            fake.FillWithSpotTrades(nativeId, 1m, firstTrade);
            await ConditionalState(() => recovered.Filled == 1m && recovered.Unfilled == 2m &&
                LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == -0.1m, broker, fake);
            fake.FillWithSpotTrades(nativeId, 2m, firstTrade,
                new FakeBinanceSpotTrade(94102, nativeId, "BTCUSDT", true, 2m, 102m, timestamp + 1, 0.2m, "USDT"));
            await ConditionalState(() => !broker.ActiveOrders.Any() && LiveStore.LoadLedger(root, key, 1m).Position == 3m &&
                LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == -0.3m, broker, fake);
            await ConditionalPolls(fake, broker);
            var ledger = LiveStore.LoadLedger(root, key, 1m);
            Assert.Equal((3m, 102m, -0.3m), (ledger.Position, ledger.AveragePrice, ledger.AuthoritativeNet));
            var fills = ledger.Fills.Where(fill => fill.Kind == LiveFillKind.Execution).ToArray();
            Assert.Equal(new[] { "94101", "94102" }, fills.Select(fill => fill.ExecutionId!.Id).OrderBy(id => id));
            Assert.Equal(3m, fills.Sum(fill => fill.Quantity));
            Assert.All(fills, fill => Assert.Equal((adopted.LogicalOrderId, owner.Id, true), (fill.OrderId, fill.OwnerId, fill.FeesComplete)));
            var checkpoint = new OrderContext(context.Descriptor, root);
            var claim = Assert.IsType<OwnedOrderIntent>(checkpoint.Find(adopted.Id));
            Assert.Equal((adopted.Request, adopted.ClientOrderId, 3m), (claim.Request, claim.ClientOrderId, claim.QuantityProjected));
            Assert.Contains(native, claim.NativeOrders!);
            Assert.All(fills, fill => Assert.Equal(fill.ExecutionId,
                Assert.IsType<ExecutionProjectionCheckpoint>(checkpoint.Projected(fill.ExecutionId!)).Report.Identity));
            Assert.Empty(checkpoint.PendingReceipts());
            Assert.Empty(fake.Sent);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
            Assert.Null(broker.LastError);
            Assert.Empty(fake.Refused);
        });
    }

    [Fact]
    public async Task Acknowledged_usdm_stop_that_finishes_offline_recovers_parent_child_and_closes_book_once()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: true);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures: true);
            var context = RecoveryContext(root, fake);
            const string key = "binance-terminal-acknowledged-stop-recreation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            OwnedOrderIntent acknowledged;
            long parentId;
            NativeOrderIdentity parentNative;
            using (var first = orders.Binance(data, fake.Settings, "Terminal acknowledged conditional recovery", context))
            {
                var projector = AccountExecutionProjector.For(first.Executions, context);
                projector.Synchronize();
                await ConditionalState(() => first.OrdersReady && first.PositionReady &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                var opening = OrderActions.Place(first, owner, OrderTypes.Limit, OrderDirections.Buy, 2m, 59990m);
                await ConditionalState(() => opening.ExchangeOrderId.Length > 0, first, fake);
                fake.Fill(long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture), 2m);
                await ConditionalState(() => first.Position.Volume == 2m && LiveStore.LoadLedger(root, key, 1m).Position == 2m &&
                    LiveStore.LoadLedger(root, key, 1m).AveragePrice == 59990m && LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 0m, first, fake);
                var stop = OrderActions.Place(first, owner, OrderTypes.Stop, OrderDirections.Sell, 2m, 59000m,
                    "Acknowledged holding stop", reduceOnly: true, protective: true, maximumOwnedQuantity: 2m);
                // Unlike Terminal_unbound..., do not hold or lose the POST acknowledgement.
                await ConditionalState(() => stop.ExchangeOrderId.StartsWith("algo:", StringComparison.Ordinal) && stop.NativeIdentity != null &&
                    new OrderContext(context.Descriptor, root).Find(stop.IntentId)?.NativeOrders?.Contains(stop.NativeIdentity) == true, first, fake);
                parentId = Assert.Single(fake.PlacedAlgo).AlgoId;
                parentNative = Assert.IsType<NativeOrderIdentity>(stop.NativeIdentity);
                acknowledged = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(stop.IntentId));
                Assert.Contains(parentNative, acknowledged.NativeOrders!);
                Assert.Equal(("STOP_MARKET", acknowledged.ClientOrderId, 0L),
                    (fake.AlgoOrder(parentId).Type, fake.AlgoOrder(parentId).ClientId, fake.AlgoOrder(parentId).ActualOrderId));
            }
            // All triggering and closing happen with the original broker disposed and no replacement broker yet.
            long childId = fake.Trigger(parentId, fill: 2m);
            var childNative = new NativeOrderIdentity("binance:order", "BTCUSDT:" + childId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(("FINISHED", childId), (fake.AlgoOrder(parentId).Status, fake.AlgoOrder(parentId).ActualOrderId));
            Assert.Equal(("MARKET", "FILLED", 2m), (fake.Order(childId).Type, fake.Order(childId).Status, fake.Order(childId).Executed));
            Assert.Empty(fake.Open);
            var loaded = new OrderContext(context.Descriptor, root);
            var beforeRecovery = Assert.IsType<OwnedOrderIntent>(loaded.Find(acknowledged.Id));
            Assert.Contains(parentNative, beforeRecovery.NativeOrders!);
            Assert.DoesNotContain(childNative, beforeRecovery.NativeOrders!);
            int postCount = fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal));
            Assert.Equal(2, postCount);
            using var broker = orders.Binance(data, fake.Settings, "Terminal acknowledged conditional recovery", loaded);
            var resumedProjector = AccountExecutionProjector.For(broker.Executions, loaded);
            resumedProjector.Synchronize();
            await ConditionalState(() => broker.OrdersReady && broker.PositionReady && broker.Position.Volume == 0m &&
                LiveStore.LoadLedger(root, key, 1m).Position == 0m && LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 20m, broker, fake);
            await ConditionalPolls(fake, broker);
            int parentLookups = fake.Calls.Count(call => call == "GET /fapi/v1/algoOrder");
            await ConditionalPolls(fake, broker);
            Assert.Equal(parentLookups, fake.Calls.Count(call => call == "GET /fapi/v1/algoOrder"));
            var ledger = LiveStore.LoadLedger(root, key, 1m);
            var closing = Assert.Single(ledger.Fills, fill => fill.Kind == LiveFillKind.Execution && !fill.Buy);
            Assert.Equal(("91002", 2m, 60000m, acknowledged.LogicalOrderId, owner.Id, true),
                (closing.ExecutionId!.Id, closing.Quantity, closing.Price, closing.OrderId, closing.OwnerId, closing.FeesComplete));
            Assert.Equal(2, ledger.Fills.Count(fill => fill.Kind == LiveFillKind.Execution));
            Assert.Equal((20m, 20m, 20m), (ledger.AuthoritativeGross, ledger.AuthoritativeNet, ledger.Trades.Sum(trade => trade.NetPnl)));
            var checkpoint = new OrderContext(context.Descriptor, root);
            var claim = Assert.IsType<OwnedOrderIntent>(checkpoint.Find(acknowledged.Id));
            Assert.Equal((owner, acknowledged.Request, acknowledged.LogicalOrderId, acknowledged.ClientOrderId, 2m),
                (claim.Owner, claim.Request, claim.LogicalOrderId, claim.ClientOrderId, claim.QuantityProjected));
            Assert.Contains(parentNative, claim.NativeOrders!);
            Assert.Contains(childNative, claim.NativeOrders!);
            var report = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes, report => report.Identity == closing.ExecutionId);
            Assert.Equal((acknowledged.Id, acknowledged.ClientOrderId, false, 2m, 60000m),
                (report.IntentId, report.ClientOrderId, report.Buy, report.Quantity, report.Price));
            Assert.Equal(closing.ExecutionId, Assert.IsType<ExecutionProjectionCheckpoint>(checkpoint.Projected(closing.ExecutionId!)).Report.Identity);
            Assert.Empty(checkpoint.PendingReceipts());
            Assert.Empty(broker.ActiveOrders);
            Assert.Single(fake.PlacedAlgo);
            Assert.Single(fake.Placed);
            Assert.Equal(postCount, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
            Assert.Null(broker.LastError);
            Assert.Empty(fake.Refused);
        });
    }

    [Fact]
    public async Task Nonterminal_owned_usdm_market_child_recovers_immutable_stop_claim_and_projects_remaining_quantity_once()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: true);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures: true);
            var context = RecoveryContext(root, fake);
            const string key = "binance-nonterminal-market-child-recreation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            OwnedOrderIntent original;
            NativeOrderIdentity parentNative, childNative;
            long childId;
            using (var first = orders.Binance(data, fake.Settings, "Market child recovery", context))
            {
                var projector = AccountExecutionProjector.For(first.Executions, context);
                projector.Synchronize();
                await ConditionalState(() => first.OrdersReady && first.PositionReady &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                var opening = OrderActions.Place(first, owner, OrderTypes.Limit, OrderDirections.Buy, 3m, 59990m);
                await ConditionalState(() => opening.ExchangeOrderId.Length > 0, first, fake);
                fake.Fill(long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture), 3m);
                await ConditionalState(() => first.Position.Volume == 3m && LiveStore.LoadLedger(root, key, 1m).Position == 3m &&
                    LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 0m, first, fake);
                var stop = OrderActions.Place(first, owner, OrderTypes.Stop, OrderDirections.Sell, 3m, 59000m,
                    "Three-unit holding stop", reduceOnly: true, protective: true, maximumOwnedQuantity: 3m);
                await ConditionalState(() => stop.ExchangeOrderId.StartsWith("algo:", StringComparison.Ordinal) && stop.NativeIdentity != null, first, fake);
                parentNative = Assert.IsType<NativeOrderIdentity>(stop.NativeIdentity);
                original = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(stop.IntentId));
                Assert.Contains(parentNative, original.NativeOrders!);
                var parent = Assert.Single(fake.PlacedAlgo);
                Assert.Equal(("STOP_MARKET", 3m, original.ClientOrderId), (parent.Type, parent.Qty, parent.ClientId));
                childId = fake.Trigger(parent.AlgoId, fill: 1m);
                childNative = new NativeOrderIdentity("binance:order", "BTCUSDT:" + childId.ToString(CultureInfo.InvariantCulture));
                await ConditionalState(() => stop.ExchangeOrderId == childId.ToString(CultureInfo.InvariantCulture) && stop.Filled == 1m && stop.Unfilled == 2m &&
                    new OrderContext(context.Descriptor, root).Find(original.Id)?.NativeOrders?.Contains(childNative) == true &&
                    LiveStore.LoadLedger(root, key, 1m).Position == 2m && LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 10m, first, fake);
                // A genuine nonterminal MARKET child and its exact durable claim are prerequisites, not fabricated evidence.
                Assert.Equal(("MARKET", "PARTIALLY_FILLED", 1m, ""),
                    (fake.Order(childId).Type, fake.Order(childId).Status, fake.Order(childId).Executed, fake.Order(childId).ClientId));
                var durable = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(original.Id));
                Assert.Equal((original.Request, original.ClientOrderId, 1m), (durable.Request, durable.ClientOrderId, durable.QuantityProjected));
                Assert.Contains(parentNative, durable.NativeOrders!);
                Assert.Contains(childNative, durable.NativeOrders!);
            }
            var loaded = new OrderContext(context.Descriptor, root);
            using var broker = orders.Binance(data, fake.Settings, "Market child recovery", loaded);
            var resumedProjector = AccountExecutionProjector.For(broker.Executions, loaded);
            resumedProjector.Synchronize();
            await ConditionalState(() => broker.OrdersReady && broker.PositionReady && broker.ActiveOrders.Any() &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
            var recovered = Assert.Single(broker.ActiveOrders);
            Assert.Equal((childId.ToString(CultureInfo.InvariantCulture), owner.Id, original.Id, original.LogicalOrderId, original.ClientOrderId, childNative),
                (recovered.ExchangeOrderId, recovered.OwnerId, recovered.IntentId, recovered.LogicalOrderId, recovered.ClientOrderId, recovered.NativeIdentity));
            Assert.Equal((1m, 2m), (recovered.Filled, recovered.Unfilled));
            var recoveredClaim = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(original.Id));
            Assert.Equal(original.Request, recoveredClaim.Request);
            Assert.Equal(OrderTypes.Stop, recoveredClaim.Request.Type);
            Assert.Equal(original.ClientOrderId, recoveredClaim.ClientOrderId);
            var working = Assert.IsType<NativeWorkingOrder>(recoveredClaim.WorkingOrder);
            Assert.Equal((parentNative, childNative, OrderTypes.Market, "BTCUSDT", ""),
                (working.Parent, working.Child, working.Type, working.NativeInstrument, working.NativeExchange));
            fake.Fill(childId, 2m);
            await ConditionalState(() => !broker.ActiveOrders.Any() && broker.Position.Volume == 0m &&
                LiveStore.LoadLedger(root, key, 1m).Position == 0m && LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 30m, broker, fake);
            await ConditionalPolls(fake, broker);
            var ledger = LiveStore.LoadLedger(root, key, 1m);
            var closing = ledger.Fills.Where(fill => fill.Kind == LiveFillKind.Execution && !fill.Buy).ToArray();
            Assert.Equal(new[] { "91002", "91003" }, closing.Select(fill => fill.ExecutionId!.Id).OrderBy(id => id));
            Assert.Equal(new[] { 1m, 2m }, closing.Select(fill => fill.Quantity).OrderBy(quantity => quantity));
            Assert.All(closing, fill => Assert.Equal((60000m, original.LogicalOrderId, owner.Id, true),
                (fill.Price, fill.OrderId, fill.OwnerId, fill.FeesComplete)));
            Assert.Equal(3, ledger.Fills.Count(fill => fill.Kind == LiveFillKind.Execution));
            Assert.Equal((30m, 30m, 30m), (ledger.AuthoritativeGross, ledger.AuthoritativeNet, ledger.Trades.Sum(trade => trade.NetPnl)));
            var checkpoint = new OrderContext(context.Descriptor, root);
            var claim = Assert.IsType<OwnedOrderIntent>(checkpoint.Find(original.Id));
            Assert.Equal((owner, original.Request, original.ClientOrderId, 3m), (claim.Owner, claim.Request, claim.ClientOrderId, claim.QuantityProjected));
            Assert.Contains(parentNative, claim.NativeOrders!);
            Assert.Contains(childNative, claim.NativeOrders!);
            Assert.Equal(working, claim.WorkingOrder);
            Assert.All(closing, fill => Assert.Equal(fill.ExecutionId,
                Assert.IsType<ExecutionProjectionCheckpoint>(checkpoint.Projected(fill.ExecutionId!)).Report.Identity));
            Assert.Empty(checkpoint.PendingReceipts());
            Assert.Single(fake.PlacedAlgo);
            Assert.Single(fake.Placed);
            Assert.Equal(2, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
            Assert.Null(broker.LastError);
            Assert.Empty(fake.Refused);
        });
    }

    [Fact]
    public async Task NativeQuantityCapturedBeforeTheFirstProjectorAllowsAdmissionAfterOfflineStopIsPriced()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: true);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures: true);
            var context = RecoveryContext(root, fake);
            const string accountName = "Late projector after offline native stop";
            string key;
            Guid generation;
            OrderOwner owner;
            OwnedOrderIntent acknowledged;
            long parentId;
            NativeOrderIdentity parentNative;
            using (var first = orders.Binance(data, fake.Settings, accountName, context))
            {
                await ConditionalState(() => first.OrdersReady && first.PositionReady &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                // Constructing the probe computes the real runner key; Start is deliberately never called.
                using (var keyProbe = new LiveStrategyRunner(new PassiveLimits(), data, first, ChartPeriod.M1))
                    key = keyProbe.Key;
                generation = LiveStore.CaptureLedgerGeneration(root, key);
                owner = context.LedgerOwner(key, generation);
                AccountExecutionProjector.For(first.Executions, context).Synchronize();
                var opening = OrderActions.Place(first, owner, OrderTypes.Limit, OrderDirections.Buy, 2m, 59990m);
                await ConditionalState(() => opening.ExchangeOrderId.Length > 0, first, fake);
                fake.Fill(long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture), 2m);
                await ConditionalState(() => first.Position.Volume == 2m && LiveStore.LoadLedger(root, key, 1m).Position == 2m &&
                    LiveStore.LoadLedger(root, key, 1m).AveragePrice == 59990m &&
                    LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 0m, first, fake);
                var stop = OrderActions.Place(first, owner, OrderTypes.Stop, OrderDirections.Sell, 2m, 59000m,
                    "Acknowledged offline stop before late projector", reduceOnly: true, protective: true, maximumOwnedQuantity: 2m);
                await ConditionalState(() => stop.ExchangeOrderId.StartsWith("algo:", StringComparison.Ordinal) && stop.NativeIdentity != null &&
                    new OrderContext(context.Descriptor, root).Find(stop.IntentId)?.NativeOrders?.Contains(stop.NativeIdentity) == true, first, fake);
                parentId = Assert.Single(fake.PlacedAlgo).AlgoId;
                parentNative = Assert.IsType<NativeOrderIdentity>(stop.NativeIdentity);
                acknowledged = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(stop.IntentId));
                Assert.Contains(parentNative, acknowledged.NativeOrders!);
            }

            // Both the trigger and its complete native fill happen while the original broker is disposed.
            long childId = fake.Trigger(parentId, fill: 2m);
            var childNative = new NativeOrderIdentity("binance:order", "BTCUSDT:" + childId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(("FINISHED", childId), (fake.AlgoOrder(parentId).Status, fake.AlgoOrder(parentId).ActualOrderId));
            Assert.Equal(("MARKET", "FILLED", 2m), (fake.Order(childId).Type, fake.Order(childId).Status, fake.Order(childId).Executed));
            Assert.Empty(fake.Open);
            int postsBeforeRecovery = fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal));
            Assert.Equal(2, postsBeforeRecovery);

            var loaded = new OrderContext(context.Descriptor, root);
            Assert.NotSame(context, loaded);
            using var broker = orders.Binance(data, fake.Settings, accountName, loaded);
            // NO projector, runner Start, QuantityChanged listener or Core Observe call may precede this barrier.
            // Ready includes real native replay and the actual signed userTrades consumer, not just a witness.
            await ConditionalState(() => broker.OrdersReady && broker.PositionReady && broker.Position.Volume == 0m &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready &&
                broker.Executions.Capture(ExecutionCursor.Start).Changes.Count == 2 &&
                loaded.Find(acknowledged.Id)?.TerminalOrder == new NativeOrderTerminal(childNative, 2m), broker, fake);
            await ConditionalPolls(fake, broker);
            Assert.Empty(broker.ActiveOrders);
            Assert.Equal(2m, LiveStore.LoadLedger(root, key, 1m).Position);
            Assert.True(loaded.HasUnprojectedQuantity(owner)); // the actual recovery capture, not an injected flag
            Assert.Equal(postsBeforeRecovery, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
            var nativeFacts = broker.Executions.Capture(ExecutionCursor.Start);
            Assert.Equal(ExecutionAvailability.Ready, nativeFacts.Availability);
            var closeReport = Assert.Single(nativeFacts.Changes, report => !report.Buy);
            Assert.Equal(("91002", 2m, 60000m, acknowledged.Id, acknowledged.ClientOrderId),
                (closeReport.Identity.Id, closeReport.Quantity, closeReport.Price, closeReport.IntentId, closeReport.ClientOrderId));

            // The first consumer of the replacement context attaches only AFTER terminal native/price recovery.
            var projector = AccountExecutionProjector.For(broker.Executions, loaded);
            projector.Synchronize();
            await ConditionalState(() => LiveStore.LoadLedger(root, key, 1m).Position == 0m &&
                LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == 20m && loaded.PendingReceipts().Count == 0, broker, fake);
            var strategy = new PassiveLimits();
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
            runner.Start();
            Assert.Equal(key, runner.Key);
            foreach (var ledger in new[] { runner.Ledger, LiveStore.LoadLedger(root, key, 1m) })
            {
                Assert.Equal(0m, ledger.Position);
                Assert.Equal((20m, 20m, 20m), (ledger.AuthoritativeGross, ledger.AuthoritativeNet, ledger.Trades.Sum(trade => trade.NetPnl)));
                Assert.False(ledger.HasPendingFinancials);
                Assert.Equal(2, ledger.Fills.Count);
                Assert.All(ledger.Fills, fill => Assert.Equal(LiveFillKind.Execution, fill.Kind));
                var close = Assert.Single(ledger.Fills, fill => !fill.Buy);
                Assert.Equal((closeReport.Identity, 2m, 60000m, acknowledged.LogicalOrderId, owner.Id, generation),
                    (close.ExecutionId, close.Quantity, close.Price, close.OrderId, close.OwnerId, close.OwnerGeneration));
            }
            Assert.Equal((0m, 0m, 0m, 0m), (runner.OwnPosition, runner.VirtualPosition, strategy.Position, runner.Info().Position));
            var checkpoint = new OrderContext(context.Descriptor, root);
            Assert.Equal(closeReport.Identity,
                Assert.IsType<ExecutionProjectionCheckpoint>(checkpoint.Projected(closeReport.Identity)).Report.Identity);
            Assert.Empty(checkpoint.PendingReceipts());
            Assert.Equal(ExecutionAvailability.Ready, projector.Availability);
            Assert.False(loaded.HasStorageFault);
            Assert.Equal(postsBeforeRecovery, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));

            // No second native fill and no Observe/re-notification is used to free admission.
            runner.SetPaused(false);
            var next = runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 1m, 100m, "bounded passive entry after recovered close");
            Assert.Equal(StrategyOrderState.Active, next.State); // expected current RED: the orphaned capture keeps this canceled
            Assert.False(runner.Paused);
            Assert.False(loaded.HasUnprojectedQuantity(owner));
            await ConditionalState(() => fake.Placed.Count == 2 && broker.ActiveOrders.Count() == 1, broker, fake);
            var accepted = Assert.Single(broker.ActiveOrders);
            Assert.Equal((OrderTypes.Limit, OrderDirections.Buy, 1m, 0m, 1m, 100m, owner.Id),
                (accepted.Type, accepted.Direction, accepted.QuantityToFill, accepted.Filled, accepted.Unfilled, accepted.Price, accepted.OwnerId));
            var admitted = Assert.IsType<OwnedOrderIntent>(loaded.Find(accepted.IntentId));
            Assert.Equal(owner, admitted.Owner);
            Assert.Equal((1m, 1m), (admitted.Request.StrategyQuantity, admitted.Request.MaximumOwnedQuantity));
            Assert.Equal(postsBeforeRecovery + 1, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
            Assert.Equal(2, broker.Executions.Capture(ExecutionCursor.Start).Changes.Count);
            Assert.Equal(0m, broker.Position.Volume);
            Assert.Equal(0m, runner.OwnPosition);
            Assert.Equal(20m, runner.Ledger.AuthoritativeNet);
            runner.Cancel(next);
            await ConditionalState(() => !broker.ActiveOrders.Any() && !runner.HasWorkingOrders, broker, fake);
            Assert.True(runner.ResetLedger());
            Assert.NotEqual(generation, LiveStore.CaptureLedgerGeneration(root, key));
            projector.Synchronize();
            Assert.Empty(LiveStore.LoadLedger(root, key, 1m).Fills);
            Assert.Equal(postsBeforeRecovery + 1, fake.Calls.Count(call => call.StartsWith("POST ", StringComparison.Ordinal)));
            Assert.Null(broker.LastError);
            Assert.Empty(fake.Refused);
        });
    }

    private static Task ConditionalPolls(FakeBinance fake, BinanceBroker broker)
    {
        int start = fake.OpenOrderPolls;
        return ConditionalState(() => fake.OpenOrderPolls >= start + 3, broker, fake);
    }

    private static async Task ConditionalState(Func<bool> condition, BinanceBroker broker, FakeBinance fake)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            try
            {
                if (condition()) settled.TrySetResult();
            }
            catch (Exception ex) { settled.TrySetException(ex); }
        }
        // Subscribe before the first observation: the public notification cannot be lost between check and await.
        broker.Changed += Observe;
        try
        {
            Observe();
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(12));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Conditional recovery did not settle; broker: {broker.LastError ?? "(none)"}; " +
                $"recent routes: {string.Join(" | ", fake.Calls.TakeLast(20))}", ex);
        }
        finally { broker.Changed -= Observe; }
    }

    [Fact]
    public async Task RecoveredTerminalOpeningWithUnpublishedNativeQuantityCannotRetireItsLedgerGeneration()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 0m, 0m);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            const string accountName = "Recovered terminal publication fence";
            string key, intentId, logicalId;
            long openingId;
            Guid generation;
            OrderOwner owner;
            NativeOrderIdentity native;
            using (var first = orders.Binance(data, fake.Settings, accountName, context))
            {
                await Until(() => first.OrdersReady && first.PositionReady &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                using (var keyProbe = new LiveStrategyRunner(new PassiveLimits(), data, first, ChartPeriod.M1))
                    key = keyProbe.Key;
                generation = LiveStore.CaptureLedgerGeneration(root, key);
                owner = context.LedgerOwner(key, generation);
                var opening = OrderActions.Place(first, owner, OrderTypes.Limit, OrderDirections.Buy, 2m, 173m);
                await Until(() => opening.ExchangeOrderId.Length > 0, first, fake);
                openingId = long.Parse(opening.ExchangeOrderId, CultureInfo.InvariantCulture);
                intentId = Assert.IsType<string>(opening.IntentId);
                logicalId = Assert.IsType<string>(opening.LogicalOrderId);
                native = Assert.IsType<NativeOrderIdentity>(opening.NativeIdentity);
            }

            // A second, independently placed native order makes the real sequential lookup loop
            // observable. It is canceled, not filled, and can never supply strategy ownership.
            long otherId = fake.AddExternal("SELL", "LIMIT", 1m, 190m, clientId: "outside-reset-owner");
            var loaded = new OrderContext(context.Descriptor, root);
            var persisted = Assert.IsType<OwnedOrderIntent>(loaded.Find(intentId));
            Assert.Equal(owner, persisted.Owner);
            Assert.Contains(native, persisted.NativeOrders!);
            using var broker = orders.Binance(data, fake.Settings, accountName, loaded);
            await Until(() => broker.OrdersReady && broker.PositionReady &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready &&
                broker.ActiveOrders.Count() == 2, broker, fake);
            var recovered = Assert.Single(broker.ActiveOrders, order => order.ExchangeOrderId == openingId.ToString(CultureInfo.InvariantCulture));
            var other = Assert.Single(broker.ActiveOrders, order => order.ExchangeOrderId == otherId.ToString(CultureInfo.InvariantCulture));
            Assert.Null(other.OwnerId);
            Assert.Equal((owner.Id, intentId, logicalId, native),
                (recovered.OwnerId, recovered.IntentId, recovered.LogicalOrderId, recovered.NativeIdentity));
            var strategy = new PassiveLimits { StopLossTicks = 5 };
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
            runner.Start();
            Assert.Equal(key, runner.Key);
            Assert.Empty(runner.ActiveOrders); // no local StrategyOrder may hide this recovered-order gap
            Assert.Equal(0m, runner.OwnPosition);

            var historyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var historyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var openSnapshotEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var openSnapshotRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondLookupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondLookupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var balanceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var balanceRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool resetAccepted = false;
            Guid generationAfterAttempt = default;
            decimal quantityAtBalance = -1m;
            fake.BeforeTradeReply = ct =>
            {
                historyEntered.TrySetResult();
                return historyRelease.Task.WaitAsync(ct);
            };
            try
            {
                await historyEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                int firstOpenSnapshot = 0;
                fake.BeforeGetReply = async (request, ct) =>
                {
                    if (request.Route == "/api/v3/openOrders" && Interlocked.Exchange(ref firstOpenSnapshot, 1) == 0)
                    {
                        openSnapshotEntered.TrySetResult();
                        await openSnapshotRelease.Task.WaitAsync(ct);
                    }
                    else if (request.Route == "/api/v3/order" && request.Query("orderId") == otherId.ToString(CultureInfo.InvariantCulture))
                    {
                        secondLookupEntered.TrySetResult();
                        await secondLookupRelease.Task.WaitAsync(ct);
                    }
                    else if (request.Route == "/api/v3/account" && recovered.State == OrderStates.Done)
                    {
                        balanceEntered.TrySetResult();
                        await balanceRelease.Task.WaitAsync(ct);
                    }
                };
                // Snapshot both still-working native orders before changing the fake exchange.
                // This guarantees the next poll sees both gone; it cannot publish the first fill
                // in an intervening one-order lookup pass.
                await openSnapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                fake.FillWithSpotTrades(openingId, 2m,
                    new FakeBinanceSpotTrade(94002, openingId, "BTCUSDT", true, 2m, 170m, timestamp, 0.4m, "USDT"));
                fake.CancelAtExchange(otherId);
                openSnapshotRelease.TrySetResult();
                await secondLookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));

                // The first real GetOrder reply has run Apply/Finish. The second real GetOrder
                // reply, not the later balance reply, is the stable pre-publication window.
                Assert.Equal((OrderStates.Done, OrderStatus.Filled, 2m, 0m),
                    (recovered.State, recovered.Status, recovered.Filled, recovered.Unfilled));
                Assert.True(broker.OrdersReady);
                Assert.DoesNotContain(recovered, broker.ActiveOrders);
                Assert.Equal(0m, runner.OwnPosition);
                Assert.Equal(0m, LiveStore.LoadLedger(root, key, 1m).Position);
                // The earlier native balance may already show two; only the owned ledger must
                // still be flat here. Never inject or suppress the real account quantity.
                Assert.Empty(runner.Ledger.Fills);
                Assert.Empty(broker.Executions.Capture(ExecutionCursor.Start).Changes);
                Assert.False(historyRelease.Task.IsCompleted);
                Assert.False(balanceEntered.Task.IsCompleted);
                Assert.Equal(0m, Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(logicalId)).QuantityProjected);

                resetAccepted = runner.ResetLedger();
                generationAfterAttempt = LiveStore.CaptureLedgerGeneration(root, key);
                secondLookupRelease.TrySetResult();
                await balanceEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                quantityAtBalance = LiveStore.LoadLedger(root, key, 1m).Position;
                Assert.False(historyRelease.Task.IsCompleted);
            }
            finally
            {
                fake.BeforeGetReply = null;
                fake.BeforeTradeReply = null;
                openSnapshotRelease.TrySetResult();
                secondLookupRelease.TrySetResult();
                balanceRelease.TrySetResult();
                historyRelease.TrySetResult();
            }

            // Finish native/financial transport consumption even on the RED implementation;
            // the first regression assertion reports the reset decision, not a stop timeout.
            await Until(() => broker.Position.Volume == 2m &&
                broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
            Assert.False(resetAccepted);
            Assert.Equal(generation, generationAfterAttempt);
            Assert.Equal(2m, quantityAtBalance);
            await Until(() => runner.OwnPosition == 2m && runner.Ledger.AveragePrice == 170m &&
                !runner.Ledger.HasPendingFinancials && broker.ActiveOrders.Any(order =>
                    order.Type == OrderTypes.Stop && order.ExchangeOrderId.Length > 0), broker, fake);
            await ConsumedPolls(fake, broker);
            Assert.Equal(2m, runner.VirtualPosition);
            Assert.Equal(2m, strategy.Position);
            Assert.Equal(2, fake.Placed.Count); // original owned opening plus exactly one acknowledged stop
            var stop = Assert.Single(broker.ActiveOrders);
            Assert.Equal((OrderTypes.Stop, OrderDirections.Sell, 2m, 169.5m, owner.Id),
                (stop.Type, stop.Direction, stop.Unfilled, stop.TriggerPrice, stop.OwnerId));
            var protection = Assert.IsType<OwnedOrderIntent>(loaded.Find(stop.IntentId));
            Assert.Equal(owner, protection.Owner);
            Assert.True(protection.Request.Protective);
            Assert.True(protection.Request.ReduceOnly);
            Assert.Equal(2m, protection.Request.MaximumOwnedQuantity);
            Assert.Equal(new[] { stop.ExchangeOrderId }, runner.ProtectionOrderIds);
            foreach (var ledger in new[] { runner.Ledger, LiveStore.LoadLedger(root, key, 1m) })
            {
                Assert.Equal(2m, ledger.Position);
                Assert.Equal(170m, ledger.AveragePrice);
                Assert.Equal(-0.4m, ledger.AuthoritativeNet);
                var fill = Assert.Single(ledger.Fills);
                Assert.Equal(LiveFillKind.Execution, fill.Kind);
                Assert.Equal((2m, 170m, logicalId, owner.Id, generation),
                    (fill.Quantity, fill.Price, fill.OrderId, fill.OwnerId, fill.OwnerGeneration));
                Assert.Equal("94002", fill.ExecutionId!.Id);
                var fee = Assert.Single(fill.Fees!);
                Assert.Equal((0.4m, "USDT"), (fee.Amount, fee.Currency));
                Assert.Empty(ledger.Trades);
            }
            var durable = new OrderContext(context.Descriptor, root);
            Assert.Equal(2m, Assert.IsType<OwnedOrderIntent>(durable.Find(logicalId)).QuantityProjected);
            Assert.Equal(persisted.Request, Assert.IsType<OwnedOrderIntent>(durable.Find(intentId)).Request);
            var execution = Assert.Single(runner.Ledger.Fills).ExecutionId!;
            Assert.Equal(execution, Assert.IsType<ExecutionProjectionCheckpoint>(durable.Projected(execution)).Report.Identity);
            Assert.Empty(durable.PendingReceipts());
            Assert.Equal(generation, LiveStore.CaptureLedgerGeneration(root, key));
            Assert.Empty(fake.Refused);
        });
    }

    [Fact]
    public async Task Accepted_offline_conditional_child_is_not_rebound_to_a_foreign_parent_reusing_its_client_id()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: true);
            const string client = "external-reusable-conditional";
            long parentId = fake.AddExternalAlgo("BUY", "STOP", 3m, 61000m, 61100m, clientId: client);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed, futures: true);
            var context = RecoveryContext(root, fake);
            const string key = "binance-accepted-child-reused-client-isolation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            OwnedOrderIntent adopted;
            NativeOrderIdentity parentNative;
            using (var first = orders.Binance(data, fake.Settings, "Reused conditional client", context))
            {
                await ConditionalState(() => first.OrdersReady && first.PositionReady && first.ActiveOrders.Any(), first, fake);
                var external = Assert.Single(first.ActiveOrders);
                Assert.Equal(client, external.ClientOrderId);
                parentNative = Assert.IsType<NativeOrderIdentity>(external.NativeIdentity);
                adopted = context.Adopt(external, owner, 0m, Array.Empty<ExecutionIdentity>(), baselineKnown: true);
                Assert.True(adopted.Adopted);
                Assert.False(adopted.DispatchUncertain);
                Assert.Equal(new[] { parentNative }, adopted.NativeOrders);
            }
            // No broker exists while P1 triggers, partially executes, then fully executes.
            long childId = fake.Trigger(parentId, fill: 1m);
            Assert.Equal(("PARTIALLY_FILLED", 1m, 3m, 61100m, false),
                (fake.Order(childId).Status, fake.Order(childId).Executed, fake.Order(childId).Qty,
                    fake.Order(childId).Price, fake.Order(childId).ReduceOnly));
            fake.Fill(childId, 2m);
            Assert.Equal(("FINISHED", childId), (fake.AlgoOrder(parentId).Status, fake.AlgoOrder(parentId).ActualOrderId));
            Assert.Equal(("FILLED", 3m), (fake.Order(childId).Status, fake.Order(childId).Executed));
            Assert.Empty(fake.Open);
            // Reuse is legal only AFTER P1 is terminal. P2 is a distinct real venue parent.
            long foreignId = fake.AddExternalAlgo("BUY", "STOP", 5m, 62000m, 62100m, clientId: client);
            Assert.NotEqual(parentId, foreignId);
            Assert.Equal(client, fake.AlgoOrder(foreignId).ClientId);
            var childNative = new NativeOrderIdentity("binance:order", "BTCUSDT:" + childId.ToString(CultureInfo.InvariantCulture));
            var foreignNative = new NativeOrderIdentity("binance:algo", foreignId.ToString(CultureInfo.InvariantCulture));
            var loaded = new OrderContext(context.Descriptor, root);
            Assert.Equal(new[] { parentNative }, Assert.IsType<OwnedOrderIntent>(loaded.Find(adopted.Id)).NativeOrders);
            Assert.Empty(LiveStore.LoadLedger(root, key, 1m).Fills);
            var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseParent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var foreignListEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseForeignList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int openLists = 0, parentGets = 0, algoLists = 0;
            fake.BeforeGetReply = (request, ct) =>
            {
                if (request.Route == "/fapi/v1/openOrders" && Interlocked.Increment(ref openLists) == 1)
                {
                    events.Enqueue("regular-list");
                    startupEntered.TrySetResult();
                    return releaseStartup.Task.WaitAsync(ct);
                }
                if (request.Route == "/fapi/v1/algoOrder" && request.Query("algoId") == parentId.ToString(CultureInfo.InvariantCulture))
                {
                    if (Interlocked.Increment(ref parentGets) == 1)
                    {
                        events.Enqueue("exact-P1");
                        parentEntered.TrySetResult();
                        return releaseParent.Task.WaitAsync(ct);
                    }
                }
                if (request.Route == "/fapi/v1/openAlgoOrders" && Interlocked.Increment(ref algoLists) == 1)
                {
                    events.Enqueue("foreign-P2-list");
                    foreignListEntered.TrySetResult();
                    return releaseForeignList.Task.WaitAsync(ct);
                }
                if (request.Route == "/fapi/v1/order" && request.Query("orderId") == childId.ToString(CultureInfo.InvariantCulture))
                    events.Enqueue("exact-X");
                return Task.CompletedTask;
            };
            try
            {
                using var broker = orders.Binance(data, fake.Settings, "Reused conditional client", loaded);
                await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                var projector = AccountExecutionProjector.For(broker.Executions, loaded);
                projector.Synchronize();
                releaseStartup.TrySetResult();
                await parentEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.Equal(new[] { parentNative }, Assert.IsType<OwnedOrderIntent>(loaded.Find(adopted.Id)).NativeOrders);
                releaseParent.TrySetResult();
                await foreignListEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                // Exact P1's reply has been consumed by real recovery, including Follow(X)/native binding.
                // The signed P2 list body has been snapshotted but NOT consumed by RefreshAlgoOrders.
                var beforeForeign = Assert.IsType<OwnedOrderIntent>(loaded.Find(adopted.Id));
                Assert.Contains(parentNative, beforeForeign.NativeOrders!);
                Assert.Contains(childNative, beforeForeign.NativeOrders!);
                Assert.DoesNotContain(foreignNative, beforeForeign.NativeOrders!);
                Assert.Equal(new[] { "regular-list", "exact-P1", "foreign-P2-list" }, events.ToArray());
                releaseForeignList.TrySetResult();
                await ConditionalState(() => broker.OrdersReady && broker.PositionReady && broker.Position.Volume == 3m &&
                    broker.ActiveOrders.Any(order => order.ExchangeOrderId == "algo:" + foreignId.ToString(CultureInfo.InvariantCulture)) &&
                    LiveStore.LoadLedger(root, key, 1m).Fills.Count == 2 &&
                    broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
                await ConditionalPolls(fake, broker);
                Assert.Contains("exact-X", events);
                Assert.Equal(1, Volatile.Read(ref parentGets));
                var foreign = Assert.Single(broker.ActiveOrders);
                Assert.Equal(("algo:" + foreignId.ToString(CultureInfo.InvariantCulture), client, foreignNative, 0m, 5m),
                    (foreign.ExchangeOrderId, foreign.ClientOrderId, foreign.NativeIdentity, foreign.Filled, foreign.Unfilled));
                Assert.Null(foreign.OwnerId);
                Assert.Null(foreign.IntentId);
                Assert.Null(foreign.LogicalOrderId);
                var ledger = LiveStore.LoadLedger(root, key, 1m);
                Assert.Equal((3m, 61100m, 0m), (ledger.Position, ledger.AveragePrice, ledger.AuthoritativeNet));
                Assert.Equal(LiveFinancialQuality.Authoritative, ledger.FinancialQuality);
                Assert.False(ledger.HasPendingFinancials);
                Assert.Equal(new[] { "91001", "91002" }, ledger.Fills.Select(fill => fill.ExecutionId!.Id).OrderBy(id => id));
                Assert.Equal(3m, ledger.Fills.Sum(fill => fill.Quantity));
                Assert.All(ledger.Fills, fill =>
                {
                    Assert.Equal((LiveFillKind.Execution, true, 61100m, owner.Id, adopted.LogicalOrderId, true),
                        (fill.Kind, fill.Buy, fill.Price, fill.OwnerId, fill.OrderId, fill.FeesComplete));
                    Assert.Equal(0m, Assert.Single(fill.Fees!).Amount);
                });
                var disk = new OrderContext(context.Descriptor, root);
                var recovered = Assert.IsType<OwnedOrderIntent>(disk.Find(adopted.Id));
                Assert.Equal(2, recovered.NativeOrders!.Length);
                Assert.Contains(parentNative, recovered.NativeOrders);
                Assert.Contains(childNative, recovered.NativeOrders);
                Assert.DoesNotContain(foreignNative, recovered.NativeOrders);
                Assert.Equal(new NativeWorkingOrder(parentNative, childNative, OrderTypes.Limit, "BTCUSDT", ""), recovered.WorkingOrder);
                Assert.Equal(new NativeOrderTerminal(childNative, 3m), recovered.TerminalOrder);
                Assert.Equal(3m, recovered.QuantityProjected);
                foreach (var fill in ledger.Fills)
                {
                    var report = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes, report => report.Identity == fill.ExecutionId);
                    Assert.Equal((adopted.Id, childNative), (Assert.IsType<OwnedOrderIntent>(disk.Resolve(report)).Id, report.NativeOrder));
                    Assert.Equal(fill.ExecutionId, Assert.IsType<ExecutionProjectionCheckpoint>(disk.Projected(fill.ExecutionId!)).Report.Identity);
                }
                Assert.DoesNotContain(broker.Executions.Capture(ExecutionCursor.Start).Changes, report => report.NativeOrder == foreignNative);
                Assert.Empty(disk.PendingReceipts());
                Assert.Empty(fake.Sent);
                var next = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Buy, 1m, 61200m);
                await ConditionalState(() => next.ExchangeOrderId.Length > 0 && fake.Sent.Count == 1, broker, fake);
                Assert.Equal((owner.Id, next.ExchangeOrderId), (next.OwnerId, Assert.Single(fake.Sent).ExchangeId));
                Assert.NotEqual(adopted.LogicalOrderId, next.LogicalOrderId);
                fake.Fill(long.Parse(next.ExchangeOrderId, CultureInfo.InvariantCulture), 1m);
                await ConditionalState(() => broker.Position.Volume == 4m && next.State == OrderStates.Done &&
                    LiveStore.LoadLedger(root, key, 1m).Fills.Count == 3 &&
                    broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
                ledger = LiveStore.LoadLedger(root, key, 1m);
                Assert.Equal((4m, 61125m, 0m), (ledger.Position, ledger.AveragePrice, ledger.AuthoritativeNet));
                Assert.Equal(2, ledger.Fills.Count(fill => fill.OrderId == adopted.LogicalOrderId));
                Assert.Contains(ledger.Fills, fill => fill.ExecutionId!.Id == "91003" && fill.OrderId == next.LogicalOrderId && fill.Price == 61200m);
                Assert.Same(foreign, Assert.Single(broker.ActiveOrders));
                Assert.Null(foreign.OwnerId);
                Assert.Single(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
                Assert.True(broker.OrdersReady);
                Assert.Null(broker.LastError);
                Assert.Empty(fake.Refused);
            }
            finally
            {
                releaseStartup.TrySetResult();
                releaseParent.TrySetResult();
                releaseForeignList.TrySetResult();
                fake.BeforeGetReply = null;
            }
        });
    }

    [Fact]
    public async Task Settled_adopted_limit_does_not_poison_foreign_limit_reusing_exact_client_after_recreation()
    {
        await WithRecoveryRoot(async root =>
        {
            using var orders = new NativeOrderTestFixture();
            using var fake = new FakeBinance(futures: false);
            fake.SetSpotBalance("BTC", 0m, 0m);
            const string client = "external-reusable-regular";
            long originalId = fake.AddExternal("BUY", "LIMIT", 3m, 103m, clientId: client);
            using var feed = new SimulatedFeed();
            using var data = RecoveryData(feed);
            var context = RecoveryContext(root, fake);
            const string key = "binance-settled-adoption-reused-client-isolation";
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            long timestamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            var firstTrade = new FakeBinanceSpotTrade(94901, originalId, "BTCUSDT", true, 1m, 103m, timestamp, 0.1m, "USDT");
            var secondTrade = new FakeBinanceSpotTrade(94902, originalId, "BTCUSDT", true, 2m, 103m, timestamp + 1, 0.2m, "USDT");
            OwnedOrderIntent adopted;
            NativeOrderIdentity originalNative;
            using (var first = orders.Binance(data, fake.Settings, "Reused regular client", context))
            {
                var projector = AccountExecutionProjector.For(first.Executions, context);
                projector.Synchronize();
                await ConditionalState(() => first.OrdersReady && first.PositionReady && first.ActiveOrders.Any() &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                var external = Assert.Single(first.ActiveOrders);
                Assert.Equal((client, false, 0m, 3m), (external.ClientOrderId, external.ReduceOnly, external.Filled, external.Unfilled));
                originalNative = Assert.IsType<NativeOrderIdentity>(external.NativeIdentity);
                adopted = context.Adopt(external, owner, 0m, Array.Empty<ExecutionIdentity>(), baselineKnown: true);
                fake.FillWithSpotTrades(originalId, 1m, firstTrade);
                await ConditionalState(() => external.Filled == 1m && external.Unfilled == 2m && first.Position.Volume == 1m &&
                    LiveStore.LoadLedger(root, key, 1m).Position == 1m &&
                    LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == -0.1m &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                Assert.Equal("PARTIALLY_FILLED", fake.Order(originalId).Status);
                fake.FillWithSpotTrades(originalId, 2m, firstTrade, secondTrade);
                await ConditionalState(() => !first.ActiveOrders.Any() && first.Position.Volume == 3m &&
                    LiveStore.LoadLedger(root, key, 1m).Fills.Count == 2 &&
                    LiveStore.LoadLedger(root, key, 1m).AuthoritativeNet == -0.3m &&
                    first.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, first, fake);
                Assert.Equal(("FILLED", 3m, 103m), (fake.Order(originalId).Status, fake.Order(originalId).Executed, fake.Order(originalId).Price));
                AssertOriginalBook();
                var settled = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, root).Find(adopted.Id));
                Assert.True(settled.Adopted);
                Assert.False(settled.DispatchUncertain);
                Assert.Equal(3m, settled.QuantityProjected);
                Assert.Equal(new NativeOrderTerminal(originalNative, 3m), settled.TerminalOrder);
                Assert.Empty(new OrderContext(context.Descriptor, root).PendingReceipts());
            }
            Assert.Empty(fake.Open);
            long foreignId = fake.AddExternal("BUY", "LIMIT", 5m, 109m, clientId: client);
            Assert.NotEqual(originalId, foreignId);
            Assert.Equal(client, fake.Order(foreignId).ClientId);
            var foreignNative = new NativeOrderIdentity("binance:order", "BTCUSDT:" + foreignId.ToString(CultureInfo.InvariantCulture));
            var loaded = new OrderContext(context.Descriptor, root);
            Assert.Equal(new[] { originalNative }, Assert.IsType<OwnedOrderIntent>(loaded.Find(adopted.Id)).NativeOrders);
            var listEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var foreignHistoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var foreignHistoryConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int lists = 0, sawForeignHistory = 0;
            fake.BeforeGetReply = (request, ct) =>
            {
                if (request.Route == "/api/v3/openOrders" && Interlocked.Increment(ref lists) == 1)
                {
                    listEntered.TrySetResult();
                    return releaseList.Task.WaitAsync(ct);
                }
                if (request.Route == "/api/v3/order" && request.Query("orderId") == foreignId.ToString(CultureInfo.InvariantCulture))
                {
                    Interlocked.Exchange(ref sawForeignHistory, 1);
                    foreignHistoryEntered.TrySetResult();
                }
                if (request.Route == "/api/v3/myTrades" && Volatile.Read(ref sawForeignHistory) != 0)
                    foreignHistoryConsumed.TrySetResult();
                return Task.CompletedTask;
            };
            try
            {
                using var broker = orders.Binance(data, fake.Settings, "Reused regular client", loaded);
                await listEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                var projector = AccountExecutionProjector.For(broker.Executions, loaded);
                projector.Synchronize();
                releaseList.TrySetResult();
                await ConditionalState(() => broker.OrdersReady && broker.PositionReady && broker.Position.Volume == 3m &&
                    broker.ActiveOrders.Any(order => order.ExchangeOrderId == foreignId.ToString(CultureInfo.InvariantCulture)) &&
                    broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
                var foreign = Assert.Single(broker.ActiveOrders);
                AssertForeignUnowned();
                AssertOriginalBook();
                var foreignTrade = new FakeBinanceSpotTrade(94903, foreignId, "BTCUSDT", true, 1m, 109m, timestamp + 2, 0.9m, "USDT");
                fake.FillWithSpotTrades(foreignId, 1m, firstTrade, secondTrade, foreignTrade);
                await foreignHistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                // A subsequent history request proves the exact B reply's financial cycle has returned.
                await foreignHistoryConsumed.Task.WaitAsync(TimeSpan.FromSeconds(12));
                await ConditionalState(() => foreign.Filled == 1m && foreign.Unfilled == 4m && broker.Position.Volume == 4m &&
                    broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
                await ConditionalPolls(fake, broker);
                AssertForeignUnowned();
                AssertOriginalBook();
                Assert.DoesNotContain(broker.Executions.Capture(ExecutionCursor.Start).Changes,
                    report => report.Identity.Id == "94903" || report.NativeOrder == foreignNative);
                var disk = new OrderContext(context.Descriptor, root);
                var unchanged = Assert.IsType<OwnedOrderIntent>(disk.Find(adopted.Id));
                Assert.Equal(new[] { originalNative }, unchanged.NativeOrders);
                Assert.Equal(3m, unchanged.QuantityProjected);
                Assert.Equal(new NativeOrderTerminal(originalNative, 3m), unchanged.TerminalOrder);
                Assert.Empty(disk.PendingReceipts());
                Assert.Empty(fake.Sent);
                var next = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Buy, 1m, 110m);
                await ConditionalState(() => next.ExchangeOrderId.Length > 0 && fake.Sent.Count == 1, broker, fake);
                Assert.Equal((owner.Id, next.ExchangeOrderId), (next.OwnerId, Assert.Single(fake.Sent).ExchangeId));
                Assert.NotEqual(adopted.LogicalOrderId, next.LogicalOrderId);
                long nextId = long.Parse(next.ExchangeOrderId, CultureInfo.InvariantCulture);
                var nextTrade = new FakeBinanceSpotTrade(94904, nextId, "BTCUSDT", true, 1m, 110m, timestamp + 3, 0.11m, "USDT");
                fake.FillWithSpotTrades(nextId, 1m, firstTrade, secondTrade, foreignTrade, nextTrade);
                await ConditionalState(() => broker.Position.Volume == 5m && LiveStore.LoadLedger(root, key, 1m).Fills.Count == 3 &&
                    broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready, broker, fake);
                await ConditionalPolls(fake, broker);
                var ledger = LiveStore.LoadLedger(root, key, 1m);
                Assert.Equal((4m, 104.75m, -0.41m), (ledger.Position, ledger.AveragePrice, ledger.AuthoritativeNet));
                Assert.Equal(2, ledger.Fills.Count(fill => fill.OrderId == adopted.LogicalOrderId));
                Assert.Contains(ledger.Fills, fill => fill.ExecutionId!.Id == "94904" && fill.Price == 110m && fill.OrderId == next.LogicalOrderId);
                Assert.DoesNotContain(ledger.Fills, fill => fill.ExecutionId!.Id == "94903");
                AssertForeignUnowned();
                disk = new OrderContext(context.Descriptor, root);
                Assert.Equal(new[] { originalNative }, Assert.IsType<OwnedOrderIntent>(disk.Find(adopted.Id)).NativeOrders);
                Assert.DoesNotContain(foreignNative, Assert.IsType<OwnedOrderIntent>(disk.Find(next.IntentId)).NativeOrders!);
                Assert.Empty(disk.PendingReceipts());
                Assert.Single(fake.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
                Assert.True(broker.OrdersReady);
                Assert.Null(broker.LastError);
                Assert.Empty(fake.Refused);

                void AssertForeignUnowned()
                {
                    Assert.Same(foreign, Assert.Single(broker.ActiveOrders));
                    Assert.Equal((foreignId.ToString(CultureInfo.InvariantCulture), client, foreignNative),
                        (foreign.ExchangeOrderId, foreign.ClientOrderId, foreign.NativeIdentity));
                    Assert.Null(foreign.OwnerId);
                    Assert.Null(foreign.IntentId);
                    Assert.Null(foreign.LogicalOrderId);
                }
            }
            finally
            {
                releaseList.TrySetResult();
                fake.BeforeGetReply = null;
            }

            void AssertOriginalBook()
            {
                var ledger = LiveStore.LoadLedger(root, key, 1m);
                Assert.Equal((3m, 103m, -0.3m), (ledger.Position, ledger.AveragePrice, ledger.AuthoritativeNet));
                Assert.Equal(LiveFinancialQuality.Authoritative, ledger.FinancialQuality);
                Assert.False(ledger.HasPendingFinancials);
                Assert.Equal(new[] { "94901", "94902" }, ledger.Fills.Select(fill => fill.ExecutionId!.Id).OrderBy(id => id));
                Assert.All(ledger.Fills, fill =>
                {
                    Assert.Equal((LiveFillKind.Execution, true, 103m, owner.Id, adopted.LogicalOrderId, true),
                        (fill.Kind, fill.Buy, fill.Price, fill.OwnerId, fill.OrderId, fill.FeesComplete));
                    Assert.Equal("USDT", Assert.Single(fill.Fees!).Currency);
                });
                Assert.Equal(0.3m, ledger.Fills.Sum(fill => Assert.Single(fill.Fees!).Amount));
                Assert.Empty(ledger.Trades);
            }
        });
    }

    private static InstrumentData RecoveryData(SimulatedFeed feed, bool futures = false)
    {
        var instrument = new Instrument
        {
            Symbol = "BTCUSDT", FeedId = futures ? "binance-futures" : BinanceFeed.SpotId, BaseAsset = "BTC", QuoteAsset = "USDT",
            TickSize = 0.1m, VolumeStep = 0.001m, PointValue = 1m,
        };
        var data = new InstrumentData(instrument, feed) { RecordLive = false };
        DateTime time = DateTime.UtcNow.AddMinutes(-5);
        for (int i = 0; i < 5; i++)
            data.Trades.Add(new Trade(i + 1, time.AddSeconds(i * 30), 170m, 1m, TradeSide.Buy));
        return data;
    }

    private static OrderContext RecoveryContext(string root, FakeBinance fake)
    {
        string id = Guid.NewGuid().ToString("N");
        return new OrderContext(new AccountContextDescriptor(id, "Binance", fake.Futures ? "usd-m-futures" : "spot", fake.Settings.RestBase,
            "local:" + id, "owned-loopback-recovery-generation"), root);
    }

    private static async Task WithRecoveryRoot(Func<string, Task> run)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_binance_recovery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        LiveStore.Root = root;
        Exception? scenarioFailure = null;
        try { await run(root); }
        catch (Exception ex)
        {
            scenarioFailure = ex;
            throw;
        }
        finally
        {
            LiveStore.Root = previous;
            try { Directory.Delete(root, recursive: true); }
            catch (Exception cleanupFailure) when (scenarioFailure != null)
            {
                throw new AggregateException("The native scenario and its private-root cleanup both failed.",
                    scenarioFailure, cleanupFailure);
            }
        }
    }

    private static async Task Until(Func<bool> condition, BinanceBroker broker, FakeBinance fake)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(30);
        Assert.True(condition(), $"Native execution consumer did not settle; broker: {broker.LastError ?? "(none)"}; " +
            $"recent routes: {string.Join(" | ", fake.Calls.TakeLast(20))}");
    }

    private static async Task ConsumedPolls(FakeBinance fake, BinanceBroker broker)
    {
        // Next-loop requests are a consumer barrier, not a substitute for financial assertions.
        int start = fake.OpenOrderPolls;
        await Until(() => fake.OpenOrderPolls >= start + 3, broker, fake);
    }

    private sealed class PassiveLimits : Strategy
    {
        public void BuyAt(decimal price, decimal quantity) => BuyLimit(price, quantity);
        public void SellAt(decimal price, decimal quantity) => SellLimit(price, quantity);
    }
}
