using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public class NativeTradovateExecutionFactsTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 14, 30, 0, DateTimeKind.Utc);
    private static readonly Instrument Es = new()
    {
        Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME",
        TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD"
    };

    [Fact]
    public async Task EarlyNativeFillResolvesExactAccountAndRevisableFeeComponentsWithoutDuplicatingPosition()
    {
        using var root = new OwnedLiveRoot();
        using var fake = new FakeTradovate();
        using var orders = new NativeOrderTestFixture();
        var normalHttp = fake.Server.OnHttp!;
        var entryRequest = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitRequest = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        int placement = 0, feeStage = 0;
        fake.Server.OnHttp = request =>
        {
            if (!request.Headers.GetValueOrDefault("Authorization", "").StartsWith("Bearer AT", StringComparison.Ordinal))
                return normalHttp(request);
            if (request.Route == "/v1/order/placeorder")
            {
                var body = JsonNode.Parse(request.Body)!.AsObject();
                if (Interlocked.Increment(ref placement) == 1) entryRequest.TrySetResult(body);
                else exitRequest.TrySetResult(body);
                // Native acceptance occurred, but neither a successful reply nor its order entity has arrived.
                // Only a later order's exact account/contract/client lineage may attribute the early native fill.
                return (502, "Fixture: accepted placement response lost");
            }
            if (request.Route == "/v1/fillFee/deps")
            {
                if (request.Query("masterid") == "8101")
                    return (200, FeeSnapshot(9101, Volatile.Read(ref feeStage), entry: true));
                if (request.Query("masterid") == "8103")
                    return (200, FeeSnapshot(9103, Volatile.Read(ref feeStage), entry: false));
                return (200, "[]");
            }
            if (request.Route == "/v1/currency/item" && request.Query("id") == "91")
                return (200, "{\"id\":91,\"name\":\"USD\",\"symbol\":\"$\"}");
            return normalHttp(request);
        };

        using var market = new FixtureMarket();
        using var data = new InstrumentData(Es, market) { RecordLive = false };
        data.Start(T0);
        await Until(() => { data.Pump(); return !data.IsLoadingHistory && data.LastPrice == 5200m; });
        Assert.Null(data.HistoryError);
        Assert.Equal(5200m, data.LastPrice); // Deliberately different from both actual execution prices.
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        var context = new OrderContext(new AccountContextDescriptor(
            Guid.NewGuid().ToString("N"), "Tradovate", "futures", fake.Server.Http + "/v1",
            "owned-tradovate-native-facts", "fixture-generation"), root.Path);
        using var broker = orders.Tradovate(data, session, Assert.Single(session.Accounts), "DEMO101", context);
        await Until(() => broker.PositionReady && broker.OrdersReady);
        using var runner = new LiveStrategyRunner(new PassiveStrategy(), data, broker, ChartPeriod.M1);
        runner.Start();
        runner.Submit(OrderSide.Buy, StrategyOrderType.Limit, 2m, 4990m, "Owned entry");
        var entryWire = await entryRequest.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => broker.LastError != null);
        string entryClient = entryWire["clOrdId"]!.GetValue<string>();

        var entryFill = NativeFill(8101, 7101, buy: true, price: 5000.25m, T0.AddSeconds(1));
        await ReceiveProps(feed.Connection, "fill", entryFill, fake.PublishFill);
        Assert.Empty(runner.Ledger.Fills);
        Assert.Empty(LiveStore.LoadLedger(root.Path, runner.Key, 50m).Fills);

        // A second execution's fill/order/contract IDs are not account evidence. Even an equal client tag
        // on another account must not claim this runner's prepared order or early fill.
        await ReceiveProps(feed.Connection, "fill", NativeFill(8102, 7102, buy: true, price: 4000m, T0), fake.PublishFill);
        await ReceiveProps(feed.Connection, "order", NativeOrder(7102, 202, entryClient, buy: true), fake.PublishOrder);
        Assert.Empty(runner.Ledger.Fills);
        Assert.Equal(0m, LiveStore.LoadLedger(root.Path, runner.Key, 50m).Position);

        await ReceiveProps(feed.Connection, "orderVersion", NativeVersion(7201, 7101, 4990m), fake.PublishVersion);
        await ReceiveProps(feed.Connection, "order", NativeOrder(7101, 101, entryClient, buy: true, filled: true), fake.PublishOrder);
        await ReceiveProps(feed.Connection, "position", NativePosition(2m, 5000.25m), fake.PublishPosition);
        await Until(() => runner.OwnPosition == 2m);
        Assert.Equal(2m, broker.Position.Volume);
        // Current-source RED boundary: native quantity is observed, but no production execution report prices it.
        await Until(() => runner.Ledger.AveragePrice == 5000.25m);
        Assert.Equal(5000.25m, runner.Ledger.AveragePrice);
        Assert.False(runner.Ledger.HasPendingPrices);
        Assert.Equal(LiveFinancialQuality.Pending, runner.Ledger.FinancialQuality);
        Assert.Null(runner.Ledger.AuthoritativeNet);
        Assert.Single(runner.Ledger.Fills);

        // Reduction remains available while fees are unknown; no fabricated zero-fee confirmation is needed.
        runner.Submit(OrderSide.Sell, StrategyOrderType.Limit, 2m, 5010m, "Owned reduction");
        var exitWire = await exitRequest.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var exitFill = NativeFill(8103, 7103, buy: false, price: 5005.25m, T0.AddSeconds(2));
        await ReceiveProps(feed.Connection, "fill", exitFill, fake.PublishFill);
        await ReceiveProps(feed.Connection, "orderVersion", NativeVersion(7203, 7103, 5010m), fake.PublishVersion);
        await ReceiveProps(feed.Connection, "order", NativeOrder(7103, 101,
            exitWire["clOrdId"]!.GetValue<string>(), buy: false, filled: true), fake.PublishOrder);
        await ReceiveProps(feed.Connection, "position", NativePosition(0m, 0m), fake.PublishPosition);
        await Until(() => runner.Ledger.AuthoritativeGross == 500m);
        AssertSnapshot(runner, root.Path, 500m, 0m);
        Assert.Equal(0m, broker.Position.Volume);
        Assert.Equal(0m, runner.OwnPosition);

        // Seven entry charges total $2.80 and seven exit charges total $5.60. Initially there is no fee
        // entity. Then the entry's routing component is absent, then its amount lacks a currency ID.
        // Neither incomplete snapshot proves zero. Repeated native Fill.id events refresh facts, not units.
        Volatile.Write(ref feeStage, 1);
        await ReceiveProps(feed.Connection, "fill", entryFill, fake.PublishFill);
        await ReceiveProps(feed.Connection, "fill", exitFill, fake.PublishFill);
        await Until(() => runner.Ledger.Realized == 492.30m);
        AssertSnapshot(runner, root.Path, 492.30m, 7.70m);

        Volatile.Write(ref feeStage, 2);
        await ReceiveProps(feed.Connection, "fill", entryFill, fake.PublishFill);
        // Same known subtotal: the additional $0.70 has no currency evidence and cannot be converted to USD.
        await Until(() => runner.Ledger.Fills.Single(f => f.Buy).Fees?.Any(f => f.Amount == 0.70m && f.Currency == null) == true);
        AssertSnapshot(runner, root.Path, 492.30m, 7.70m);

        Volatile.Write(ref feeStage, 3);
        await ReceiveProps(feed.Connection, "fill", entryFill, fake.PublishFill);
        await Until(() => runner.Ledger.Realized == 491.60m);
        AssertSnapshot(runner, root.Path, 491.60m, 8.40m);

        // A fully observed but nonfinal fee entity remains independently revisable after its runner stops.
        // No native Fill or Order is resent until the corrected durable economics have been observed.
        Assert.Empty(broker.ActiveOrders);
        runner.Stop(flatten: false);
        Assert.False(runner.IsRunning);
        Volatile.Write(ref feeStage, 4); // Same entry fee entity; commission alone changes $0.60 -> $1.60.
        await Until(() => LiveStore.LoadLedger(root.Path, runner.Key, 50m).Realized == 490.60m);
        AssertFlatSnapshot(LiveStore.LoadLedger(root.Path, runner.Key, 50m), 490.60m, 9.40m);
        // Only now replay native fills to retain the existing same-ID idempotence proof.
        await ReceiveProps(feed.Connection, "fill", entryFill, fake.PublishFill);
        await ReceiveProps(feed.Connection, "fill", exitFill, fake.PublishFill);
        AssertFlatSnapshot(LiveStore.LoadLedger(root.Path, runner.Key, 50m), 490.60m, 9.40m);
        Assert.Equal(0m, runner.OwnPosition);
        Assert.Equal(0m, broker.Position.Volume);
    }

    [Fact]
    public async Task TerminalNativeFillRecoversAfterExactNativeIndexReadFaultWithoutFillReplay()
    {
        using var root = new OwnedLiveRoot();
        using var fake = new FakeTradovate();
        using var orders = new NativeOrderTestFixture();
        var normalHttp = fake.Server.OnHttp!;
        fake.Server.OnHttp = request => request.Route == "/v1/fillFee/deps" &&
            request.Headers.GetValueOrDefault("Authorization", "").StartsWith("Bearer AT", StringComparison.Ordinal)
                ? (200, "[]") : normalHttp(request);

        var descriptor = new AccountContextDescriptor(Guid.NewGuid().ToString("N"), "Tradovate", "futures",
            fake.Server.Http + "/v1", "owned-tradovate-native-recovery", "fixture-generation");
        var context = new OrderContext(descriptor, root.Path);
        const string key = "owned-tradovate-terminal-recovery";
        var generation = LiveStore.CaptureLedgerGeneration(root.Path, key);
        var owner = context.LedgerOwner(key, generation);
        var intent = context.Prepare(owner,
            new OrderRequest(Es.Key, OrderDirections.Buy, OrderTypes.Limit, 2m, 4990m, StrategyQuantity: 2m));
        Assert.Null(intent.Error);
        Assert.Null(context.StartDispatch(intent, fake.Contract));
        var native = new NativeOrderIdentity("tradovate:order", "7101");
        Assert.Null(context.BindNative(intent, native));

        // The order is already terminal and the fill is already account-attributed before this broker exists.
        // It therefore never enters the producer's unowned-fill queue as an early, account-less fill.
        fake.ReplayOrders.Add(NativeOrder(7101, 101, intent.ClientOrderId, buy: true, filled: true));
        fake.ReplayVersions.Add(NativeVersion(7201, 7101, 4990m));
        fake.ReplayFills.Add(NativeFill(8101, 7101, buy: true, price: 5000.25m, T0.AddSeconds(1)));
        using var market = new FixtureMarket();
        using var data = new InstrumentData(Es, market) { RecordLive = false };
        data.Start(T0);
        await Until(() => { data.Pump(); return !data.IsLoadingHistory && data.LastPrice == 5200m; });
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        var attributed = Assert.Single(session.FillsOf(101));
        Assert.Equal(8101L, attributed.Id);
        Assert.Equal(5000.25m, attributed.Price);
        Assert.Equal(2m, attributed.Quantity);

        // Target only the real native index, leaving intents, client index, lease and ledger readable.
        // There is exactly one native binding. Inspect its envelope to reject accidentally locking another file.
        string index = Assert.Single(Directory.GetFiles(root.Path, "*.json", SearchOption.AllDirectories),
            path => new DirectoryInfo(Path.GetDirectoryName(path)!).Name == "native");
        using (var document = JsonDocument.Parse(File.ReadAllText(index)))
        {
            var identity = document.RootElement.GetProperty("Value").GetProperty("Native");
            Assert.Equal("tradovate:order", identity.GetProperty("Kind").GetString());
            Assert.Equal("7101", identity.GetProperty("Id").GetString());
        }
        using var blocked = new FileStream(index, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => File.ReadAllText(index));
        using var broker = orders.Tradovate(data, session, Assert.Single(session.Accounts), "DEMO101", context);
        _ = AccountExecutionProjector.For(broker.Executions, context);
        await Until(() => broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Unavailable);
        Assert.Empty(broker.Executions.Capture(ExecutionCursor.Start).Changes);
        Assert.Empty(LiveStore.LoadLedger(root.Path, key, 50m).Fills);

        blocked.Dispose(); // Actual file access is restored before the real ownership operation is retried.
        // Revalidates the exact durable native binding and raises the real OwnershipChanged event.
        // There is no Fill/Order replay, private queue mutation, fake Resolve result or direct report publication.
        Assert.Null(context.BindNative(intent, native));
        await Until(() => broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready &&
            LiveStore.LoadLedger(root.Path, key, 50m).AveragePrice == 5000.25m);

        var ledger = LiveStore.LoadLedger(root.Path, key, 50m);
        Assert.Equal(2m, ledger.Position);
        Assert.False(ledger.HasPendingPrices);
        Assert.Equal(5000.25m, ledger.AveragePrice); // Neither the 4990 limit nor the 5200 market quote.
        var fill = Assert.Single(ledger.Fills);
        Assert.Equal(LiveFillKind.Execution, fill.Kind);
        Assert.Equal(2m, fill.Quantity);
        Assert.Equal(5000.25m, fill.Price);
        Assert.Equal("8101", fill.ExecutionId!.Id);
        Assert.Equal("101", fill.ExecutionId.Scope.Account);
        Assert.Equal(owner.Id, fill.OwnerId);
        Assert.Equal(generation, fill.OwnerGeneration);
        Assert.False(fill.FeesComplete);
        var report = Assert.Single(broker.Executions.Capture(ExecutionCursor.Start).Changes);
        var recoveredContext = new OrderContext(descriptor, root.Path);
        var recoveredIntent = recoveredContext.Resolve(report with { IntentId = null, ClientOrderId = null });
        Assert.NotNull(recoveredIntent);
        Assert.Equal(intent.Id, recoveredIntent.Id);
        Assert.Equal(owner, recoveredIntent.Owner);

        // A tombstone supplies no replacement action/economics. Preserve the last proven quantity
        // while exposing required reconciliation; do not silently turn an owned BUY into an unowned SELL.
        await ReceiveProps(feed.Connection, "fill", new JsonObject { ["id"] = 8101L }, fake.DeleteFill);
        await Until(() => broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Unavailable);
        var unresolved = LiveStore.LoadLedger(root.Path, key, 50m);
        Assert.Equal(2m, unresolved.Position);
        Assert.Equal(5000.25m, unresolved.AveragePrice);
        Assert.Equal(2m, Assert.Single(unresolved.Fills).Quantity);
    }

    [Fact]
    public async Task SameIdNativeEntryCorrectionRepricesAccountRoundTripInMemoryAndAfterReload()
    {
        using var root = new OwnedLiveRoot();
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        string journalRoot = Path.Combine(root.Path, "accounts");
        var journal = new AccountJournal(journalRoot);
        var profile = new ConnectionProfile
        {
            Id = "owned-tradovate-journal", Name = "Owned Tradovate journal", Kind = ConnectionKind.Tradovate
        };
        string account = AppServices.TradovateAccountId(profile, 101);
        // The actual AppServices consumer mapping; use its static helpers, never construct AppServices.
        session.FillReceived += f => journal.Add(AppServices.TradovateAccountId(profile, f.AccountId),
            new AccountFill("tv-" + f.Id.ToString(CultureInfo.InvariantCulture), f.Time, f.Symbol, f.Buy, f.Quantity, f.Price,
                AccountCenter.PointValueOf(f.Symbol), 0, f.OrderId.ToString(CultureInfo.InvariantCulture)));
        session.Start();
        await Until(() => session.SnapshotReady);
        await ReceiveProps(feed.Connection, "order", NativeOrder(7101, 101, "journal-entry", buy: true, filled: true), fake.PublishOrder);
        await ReceiveProps(feed.Connection, "fill", NativeFill(8101, 7101, buy: true, price: 5000.25m, T0.AddSeconds(1)), fake.PublishFill);
        Assert.Equal(5000.25m, Assert.Single(journal.FillsOf(account)).Price);
        await ReceiveProps(feed.Connection, "fill", NativeFill(8101, 7101, buy: true, price: 5001.25m, T0.AddSeconds(1)), fake.PublishFill);
        await ReceiveProps(feed.Connection, "order", NativeOrder(7103, 101, "journal-exit", buy: false, filled: true), fake.PublishOrder);
        await ReceiveProps(feed.Connection, "fill", NativeFill(8103, 7103, buy: false, price: 5005.25m, T0.AddSeconds(2)), fake.PublishFill);

        // Materialize both real consumers before any economic assertion, including the fresh disk reader.
        var histories = new[] { journal.FillsOf(account), new AccountJournal(journalRoot).FillsOf(account) };
        var results = histories.Select(RoundTrips.Analyze).ToArray();
        // Independent round-trip economics: (5005.25 - 5001.25) * 2 * 50 = 400, not the stale 500.
        Assert.Equal(new[] { 400m, 400m }, results.Select(result => Assert.Single(result.Trades).Pnl));
        Assert.Equal(new[] { 400m, 400m }, results.Select(result => result.RealizedNet));
        foreach (var history in histories)
        {
            Assert.Equal(2, history.Count);
            Assert.Equal(new[] { "tv-8101", "tv-8103" }, history.Select(fill => fill.Id));
            Assert.Equal(new[] { "7101", "7103" }, history.Select(fill => fill.OrderId));
            Assert.All(history, fill =>
            {
                Assert.Equal(fake.Contract, fill.Symbol);
                Assert.Equal(2m, fill.Quantity);
                Assert.Equal(50m, fill.PointValue);
            });
            Assert.Equal(5001.25m, history.Single(fill => fill.Buy).Price);
            Assert.Equal(5005.25m, history.Single(fill => !fill.Buy).Price);
        }
        Assert.All(results, result =>
        {
            var trade = Assert.Single(result.Trades);
            Assert.Equal(2m, trade.Quantity);
            Assert.Equal(5001.25m, trade.OpenPrice);
            Assert.Equal(5005.25m, trade.ClosePrice);
        });
    }

    private static void AssertSnapshot(LiveStrategyRunner runner, string root, decimal knownNet, decimal knownCharges)
    {
        foreach (var ledger in new[] { runner.Ledger, LiveStore.LoadLedger(root, runner.Key, 50m) })
            AssertFlatSnapshot(ledger, knownNet, knownCharges);
    }

    private static void AssertFlatSnapshot(LiveLedger ledger, decimal knownNet, decimal knownCharges)
    {
        Assert.Equal(0m, ledger.Position);
        Assert.False(ledger.HasPendingPrices);
        Assert.Equal(500m, ledger.AuthoritativeGross); // (5005.25 - 5000.25) * 2 contracts * $50.
        Assert.Equal(knownNet, ledger.Realized);
        Assert.Equal(LiveFinancialQuality.Pending, ledger.FinancialQuality);
        Assert.Null(ledger.NetRealized);
        Assert.Null(ledger.AuthoritativeNet); // A FillFee snapshot does not establish cash-statement finality.
        Assert.Equal(2, ledger.Fills.Count);
        Assert.All(ledger.Fills, fill =>
        {
            Assert.Equal(LiveFillKind.Execution, fill.Kind);
            Assert.Equal(2m, fill.Quantity);
            Assert.Equal("101", fill.ExecutionId!.Scope.Account);
            Assert.False(fill.FeesComplete);
        });
        Assert.Equal(new[] { "8101", "8103" }, ledger.Fills.Select(f => f.ExecutionId!.Id).OrderBy(id => id));
        var trade = Assert.Single(ledger.Trades);
        Assert.Equal(5000.25m, trade.EntryPrice);
        Assert.Equal(5005.25m, trade.ExitPrice);
        Assert.Equal(500m, trade.Pnl);
        Assert.Equal(knownCharges, trade.Commission);
        Assert.Equal(knownNet, trade.NetPnl);
    }

    private static JsonObject NativeFill(long id, long order, bool buy, decimal price, DateTime time) => new()
    {
        ["id"] = id, ["orderId"] = order, ["contractId"] = 555, ["timestamp"] = time.ToString("O"),
        ["tradeDate"] = new JsonObject { ["year"] = 2026, ["month"] = 10, ["day"] = 12 },
        ["action"] = buy ? "Buy" : "Sell", ["qty"] = 2, ["price"] = price, ["active"] = true,
        ["finallyPaired"] = 0
    };

    private static JsonObject NativeOrder(long id, long account, string client, bool buy, bool filled = false) => new()
    {
        ["id"] = id, ["accountId"] = account, ["contractId"] = 555, ["clOrdId"] = client,
        ["action"] = buy ? "Buy" : "Sell", ["ordStatus"] = filled ? "Filled" : "Working",
        ["timestamp"] = T0.ToString("O")
    };

    private static JsonObject NativeVersion(long id, long order, decimal price) => new()
    {
        ["id"] = id, ["orderId"] = order, ["orderQty"] = 2, ["orderType"] = "Limit", ["price"] = price
    };

    private static JsonObject NativePosition(decimal quantity, decimal price) => new()
    {
        ["id"] = 7301L, ["accountId"] = 101, ["contractId"] = 555,
        ["netPos"] = quantity, ["netPrice"] = price,
        ["bought"] = 2, ["boughtValue"] = 10000.50m,
        ["sold"] = quantity == 0 ? 2 : 0, ["soldValue"] = quantity == 0 ? 10010.50m : 0m
    };

    private static string FeeSnapshot(long id, int stage, bool entry)
    {
        if (stage == 0) return "[]";
        var fee = entry
            ? new JsonObject
            {
                ["id"] = id, ["clearingFee"] = 0.10m, ["clearingCurrencyId"] = 91,
                ["exchangeFee"] = 0.20m, ["exchangeCurrencyId"] = 91,
                ["nfaFee"] = 0.30m, ["nfaCurrencyId"] = 91,
                ["brokerageFee"] = 0.40m, ["brokerageCurrencyId"] = 91,
                ["ipFee"] = 0.50m, ["ipCurrencyId"] = 91,
                ["commission"] = stage >= 4 ? 1.60m : 0.60m, ["commissionCurrencyId"] = 91
            }
            : new JsonObject
            {
                ["id"] = id, ["clearingFee"] = 0.20m, ["clearingCurrencyId"] = 91,
                ["exchangeFee"] = 0.40m, ["exchangeCurrencyId"] = 91,
                ["nfaFee"] = 0.60m, ["nfaCurrencyId"] = 91,
                ["brokerageFee"] = 0.80m, ["brokerageCurrencyId"] = 91,
                ["ipFee"] = 1.00m, ["ipCurrencyId"] = 91,
                ["commission"] = 1.20m, ["commissionCurrencyId"] = 91,
                ["orderRoutingFee"] = 1.40m, ["orderRoutingCurrencyId"] = 91
            };
        if (entry && stage >= 2) fee["orderRoutingFee"] = 0.70m;
        if (entry && stage >= 3) fee["orderRoutingCurrencyId"] = 91;
        return new JsonArray(fee).ToJsonString();
    }

    // A delivery barrier only: it never publishes reports or ledger rows. The session subscribed first,
    // so its actual props deserialization/account synchronization runs before this observer completes.
    private static async Task ReceiveProps(TradovateConnection connection, string type, JsonObject entity,
        Func<JsonObject, Task> publish)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long expected = entity["id"]!.GetValue<long>();
        void Observe(string kind, JsonElement data)
        {
            if (kind == "props" && data.GetProperty("entityType").GetString() == type &&
                data.GetProperty("entity").GetProperty("id").GetInt64() == expected)
                received.TrySetResult();
        }
        connection.User.Event += Observe;
        try
        {
            await publish(entity);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally { connection.User.Event -= Observe; }
    }

    private static async Task Until(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8))
                throw new TimeoutException("Native Tradovate financial consumer did not reach the required state.");
            await Task.Delay(10);
        }
    }

    private sealed class PassiveStrategy : Strategy { }

    private sealed class OwnedLiveRoot : IDisposable
    {
        private readonly string _previous = LiveStore.Root;
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "TradingHub-native-tradovate-facts-" + Guid.NewGuid().ToString("N"));
        public OwnedLiveRoot() { Directory.CreateDirectory(Path); LiveStore.Root = Path; }
        public void Dispose() { LiveStore.Root = _previous; Directory.Delete(Path, recursive: true); }
    }

    private sealed class FixtureMarket : IMarketDataFeed
    {
        public string Id => "owned-tradovate-fixture";
        public string DisplayName => "Owned Tradovate execution market";
        public bool SupportsHistory => true;
        public bool IsLive => false;
        public DateTime Now => T0.AddMinutes(10);
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Instrument>>(new[] { Es });
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            onBatch(Enumerable.Range(0, 5).Select(index => new Trade(index + 1, T0.AddSeconds(index * 30), 5200m, 1m, TradeSide.Buy)).ToArray());
            return Task.CompletedTask;
        }
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => new Subscription();
        public void Dispose() { }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
