using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class LiveLedgerConfirmedCostsTests
{
    private static readonly DateTime Start = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Physical_quantity_corrections_use_the_original_baseline_without_reinstating_native_high_water()
    {
        const string key = "physical-quantity-fixture", context = "verified-context";
        var owner = new OrderOwner("strategy", key, Guid.NewGuid(), OrderOwnerKind.Strategy);
        var native = new NativeOrderIdentity("fixture:order", "physical-exit");
        var original = new OwnedOrderIntent("original", "original", "client", owner,
            new("fixture:FUTURE", OrderDirections.Sell, OrderTypes.Stop, 2, 95, ReduceOnly: true, Protective: true),
            context, "credentials-a", QuantityBaseline: .5m);
        var adopted = original with { Id = "adopted", LogicalOrderId = "adopted", Generation = "credentials-b", QuantityBaseline = 1, Adopted = true };
        var scope = new ExecutionScope("fixture", "account", "fake", "FUTURE") { ContextId = context };
        var ledger = new LiveLedger(key, 10);
        ledger.Apply(Fill("entry", 0, true, 2, 100, 0) with
        { ContextId = context, OwnerId = owner.Id, OwnerGeneration = owner.LedgerGeneration });
        var observation = new LiveFill(Start.AddSeconds(1), false, 0, 0, "FUTURE", 10)
        {
            Kind = LiveFillKind.QuantityObservation, OrderId = original.LogicalOrderId,
            ContextId = context, OwnerId = owner.Id, OwnerGeneration = owner.LedgerGeneration,
            CumulativeQuantity = .75m, AccountingOrder = new(native, .5m, 1.25m),
        };
        ledger.Apply(observation);
        Assert.Equal(1.25m, ledger.Position);
        Assert.True(ledger.HasPendingPrices);
        Assert.Equal(1.25m, ledger.KnownPhysicalQuantity(original, native, "FUTURE", 10));
        Assert.Null(ledger.KnownPhysicalQuantity(adopted, native, "FUTURE", 10));

        LiveFill Exit(string id, int second, decimal quantity, decimal price, decimal fee) =>
            Fill(id, second, false, quantity, price, fee) with
            {
                OrderId = original.LogicalOrderId, ExecutionId = new(scope, id),
                ContextId = context, OwnerId = owner.Id, OwnerGeneration = owner.LedgerGeneration,
                AccountingOrder = new(native, original.QuantityBaseline), ProjectionRevision = 1,
            };
        var first = Exit("first", 2, .5m, 94, .2m);
        var further = Exit("further", 3, .25m, 93, .05m);
        ledger.Apply(first);
        ledger.Apply(further);
        ledger.Apply(first with { OrderId = adopted.LogicalOrderId, AccountingOrder = new(native, adopted.QuantityBaseline) });
        Assert.False(ledger.HasPendingPrices);
        Assert.Equal(1.25m, ledger.KnownPhysicalQuantity(adopted, native, "FUTURE", 10));

        ledger.Apply(further with { Quantity = .1m, ProjectionRevision = 2 });
        ledger.Apply(observation with
        {
            Time = Start.AddSeconds(4), OrderId = adopted.LogicalOrderId, CumulativeQuantity = .25m,
            AccountingOrder = new(native, adopted.QuantityBaseline, 1.25m),
        });
        Assert.Equal(1.4m, ledger.Position);
        Assert.Equal(-37.25m, ledger.Realized);
        Assert.False(ledger.HasPendingPrices);
        Assert.Equal(1.1m, ledger.KnownPhysicalQuantity(adopted, native, "FUTURE", 10));
        Assert.Null(ledger.KnownPhysicalQuantity(adopted with { LogicalOrderId = "unbound" }, native, "FUTURE", 10));
        Assert.Null(ledger.KnownPhysicalQuantity(adopted with { Owner = owner with { LedgerGeneration = Guid.NewGuid() } }, native, "FUTURE", 10));
    }

    [Fact]
    public void Confirmed_costs_are_conserved_across_partial_close_reverse_curve_and_fee_correction()
    {
        var ledger = new LiveLedger("confirmed-fee-fixture", 10);
        ledger.Apply(Fill("a", 0, true, 3, 100, 3));
        Assert.Equal(-3m, ledger.Realized);
        ledger.Apply(Fill("b", 1, false, 1, 110, 1));
        Assert.Equal(96m, ledger.Realized);
        Assert.Equal(98m, Assert.Single(ledger.Trades).NetPnl);
        ledger.Apply(Fill("c", 2, false, 4, 90, 4));
        Assert.Equal(-2m, ledger.Position);
        Assert.Equal(90m, ledger.AveragePrice);
        Assert.Equal(-108m, ledger.Realized);
        ledger.Apply(Fill("d", 3, true, 2, 80, 2));
        Assert.Equal(0m, ledger.Position);
        Assert.Equal(10m, ledger.Fees);
        Assert.Equal(90m, ledger.Realized);
        Assert.True(ledger.CostsComplete);
        Assert.Equal(new[] { 2m, 4m, 4m }, ledger.Trades.Select(t => t.Commission));
        Assert.Equal(ledger.Realized, ledger.Trades.Sum(t => t.NetPnl));
        Assert.Equal(1090m, ledger.Curve(1000)[^1].Equity);

        var corrected = Fill("b", 1, false, 1, 110, 2);
        ledger.Apply(corrected);
        long revision = ledger.Revision;
        ledger.Apply(corrected);
        Assert.Equal(revision, ledger.Revision);
        Assert.Equal(4, ledger.Fills.Count);
        Assert.Equal(11m, ledger.Fees);
        Assert.Equal(89m, ledger.Realized);
        Assert.Equal(ledger.Realized, ledger.Trades.Sum(t => t.NetPnl));
        Assert.Equal(1089m, ledger.Curve(1000)[^1].Equity);
        Assert.Equal(Start.AddSeconds(1), Assert.Single(ledger.Fills, fill => fill.ExecutionId?.Id == "b").Time);
    }

    [Fact]
    public void Legacy_rows_and_foreign_currency_fees_remain_unknown_not_confirmed_zero()
    {
        const string legacy = "{\"Time\":\"2026-09-07T12:00:00Z\",\"Buy\":true,\"Quantity\":1,\"Price\":100}";
        var fill = JsonSerializer.Deserialize<LiveFill>(legacy)!;
        Assert.Null(fill.ExecutionId);
        Assert.Null(fill.Fees);
        var ledger = new LiveLedger("legacy-cost-fixture", 10);
        ledger.Apply(fill);
        ledger.Apply(new LiveFill(Start.AddSeconds(1), false, 1, 110)
        {
            Kind = LiveFillKind.Execution, OrderId = "foreign-order", ExecutionId = new(new("fixture", "account", "fake", "FUTURE"), "foreign-fee"),
            Fees = new[] { new ExecutionFee(2, "OTHER") }, FeesComplete = true, Currency = "USD",
        });
        Assert.False(ledger.CostsComplete);
        Assert.Equal(100m, ledger.Realized);
        Assert.Equal(2m, Assert.Single(Assert.Single(ledger.Fills, row => row.ExecutionId?.Id == "foreign-fee").Fees!).Amount);
        Assert.Null(ledger.NetRealized);
        Assert.Null(fill.ExecutionId);
        Assert.DoesNotContain("AccountFee", JsonSerializer.Serialize(fill));
    }

    [Fact]
    public void Persisted_Kvp_execution_keeps_its_bytes_and_is_corrected_once_by_the_actual_typed_projection()
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-kvp-ledger-cutover-" + Guid.NewGuid().ToString("N"));
        const string account = "bybit:demo-1234567:http://127.0.0.1:1";
        const string key = "Strategy|bybit-linear:BTCUSDT|" + account;
        const string id = "bybit:demo-1234567:BTCUSDT:native-order:actual-fill";
        const string original = "{\"Time\":\"2026-09-07T12:00:00Z\",\"Buy\":true,\"Quantity\":1,\"Price\":100,\"Symbol\":\"BTCUSDT\",\"PointValue\":1,\"ExecutionId\":\"" + id +
            "\",\"Fee\":0.25,\"FeeCurrency\":\"USDT\",\"AccountFee\":0.25,\"ExecutionSequence\":7}\n";
        try
        {
            LiveStore.Root = root;
            string path = LiveStore.LedgerFile(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, original);
            var restored = LiveStore.LoadLedger(root, key, 1);
            var imported = Assert.Single(restored.Fills);
            Assert.Equal(LiveFillKind.ImportedExecution, imported.Kind);
            Assert.Equal(id, imported.ImportedExecutionId);
            Assert.Equal(7L, imported.ExecutionSequence);
            Assert.Equal(1m, restored.Position);
            Assert.Equal(-.25m, restored.Realized);
            Assert.True(restored.CostsComplete);
            Assert.Equal(original, File.ReadAllText(path));

            var source = new ExecutionLog();
            var context = new OrderContext(new("cutover-context", "Bybit", "linear-uta", "demo", "profile", "generation"), root, source);
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            var ticket = context.Prepare(owner, new("bybit-linear:BTCUSDT", OrderDirections.Buy, OrderTypes.Limit, 1, 100));
            var order = new Order { Security = new Security { Code = "BTCUSDT", SecurityId = "bybit-linear:BTCUSDT" },
                Direction = OrderDirections.Buy, Type = OrderTypes.Limit, QuantityToFill = 1, Unfilled = 1 };
            Assert.Null(context.BindOrder(order, ticket));
            Assert.Null(context.StartDispatch(ticket, "BTCUSDT"));
            var native = new NativeOrderIdentity("bybit:order", "native-order");
            order.NativeIdentity = native;
            order.ExchangeOrderId = native.Id;
            Assert.Null(context.BindNative(order, native));
            var projector = AccountExecutionProjector.For(source, context);
            projector.Synchronize();
            var correction = new ExecutionReport(new(new("Bybit", account, "demo", "bybit-linear:BTCUSDT") { ContextId = context.Descriptor.Id }, id),
                order.Id, "BTCUSDT", Start, true, 1, 102, 1, "USDT", new[] { new ExecutionFee(.5m, "USDT") }, true)
            { ContextId = context.Descriptor.Id, IntentId = ticket.Id, ClientOrderId = ticket.ClientOrderId, NativeOrder = native, Sequence = 7 };
            source.Publish(correction);
            restored = LiveStore.LoadLedger(root, key, 1);
            Assert.Equal(1m, restored.Position);
            Assert.Equal(102m, restored.AveragePrice);
            Assert.Equal(-.5m, restored.Realized);
            Assert.Equal(correction.Identity, Assert.Single(restored.Fills).ExecutionId);
            string correctedBytes = File.ReadAllText(path);
            Assert.StartsWith(original, correctedBytes);
            source.Publish(correction);
            Assert.Equal(correctedBytes, File.ReadAllText(path));
            Assert.Single(LiveStore.LoadLedger(root, key, 1).Fills);

            var foreign = LiveStore.LoadLedger(root, key, 1);
            var unrelated = imported with { ImportedExecutionId = id + "-foreign" };
            foreign.Apply(unrelated);
            var wrongAccount = new LiveFill(Start, true, 1, 102, "BTCUSDT", 1)
            {
                Kind = LiveFillKind.Execution, OrderId = "another-order",
                ExecutionId = new(new("Bybit", "bybit:demo-7654321:http://127.0.0.1:1", "demo", "bybit-linear:BTCUSDT"), id + "-foreign"),
                Currency = "USDT", FeesComplete = true, Fees = Array.Empty<ExecutionFee>(),
            };
            Assert.Throws<IOException>(() => foreign.Apply(wrongAccount));
            Assert.Throws<IOException>(() => foreign.Apply(wrongAccount with
            {
                ExecutionId = new(new("Bybit", "bybit:demo-1234567:http://127.0.0.1:2", "demo", "bybit-linear:BTCUSDT"), id + "-foreign"),
            }));
            Assert.Equal(2m, foreign.Position);
            Assert.Equal(2, foreign.Fills.Count);
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Persisted_Kvp_unknown_fees_stay_pending_and_malformed_fee_metadata_blocks_recovery(bool malformed)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-kvp-unknown-fees-" + Guid.NewGuid().ToString("N"));
        const string key = "Strategy|bybit-linear:BTCUSDT|bybit:demo-1234567:http://127.0.0.1:1";
        string row = "{\"Time\":\"2026-09-07T12:00:00Z\",\"Buy\":true,\"Quantity\":1,\"Price\":100,\"Symbol\":\"BTCUSDT\",\"PointValue\":1," +
            "\"ExecutionId\":\"bybit:demo-1234567:BTCUSDT:native-order:unknown-fee\",\"Fee\":" +
            (malformed ? "\"unsupported\"" : "null") + ",\"FeeCurrency\":\"USDT\",\"AccountFee\":null}\n";
        try
        {
            LiveStore.Root = root;
            string path = LiveStore.LedgerFile(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, row);
            if (malformed) Assert.Throws<IOException>(() => LiveStore.LoadLedger(root, key, 1));
            else
            {
                var ledger = LiveStore.LoadLedger(root, key, 1);
                Assert.Equal(1m, ledger.Position);
                Assert.Equal(100m, ledger.AveragePrice);
                Assert.False(ledger.CostsComplete);
                Assert.Null(ledger.NetRealized);
                Assert.False(Assert.Single(ledger.Fills).FeesComplete);
                Assert.Empty(Assert.Single(ledger.Fills).Fees!);
            }
            Assert.Equal(row, File.ReadAllText(path));
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Admitted_physical_head_recovers_without_native_receipt_and_retries_keep_their_original_binding()
    {
        WithAuthorityRoot(root =>
        {
            const string key = "admitted-head-fixture";
            Guid generation = LiveStore.CaptureLedgerGeneration(root, key);
            var original = AuthorityFill(generation, 1);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "receipt-a", original).Status);
            // Deliberately stop at the public admission boundary: no native receipt or financial projection was saved.
            Assert.True(LiveStore.PendingLedgerEvents(root, key) > 0);
            var recovered = LiveStore.LoadLedger(root, key, 10);
            Assert.Equal(2m, recovered.Position);
            Assert.Equal(1m, recovered.Fees);
            Assert.Single(recovered.Fills);
            Assert.Equal(0, LiveStore.PendingLedgerEvents(root, key));
            byte[] bytes = File.ReadAllBytes(LiveStore.LedgerFile(key));
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Unchanged,
                LiveStore.AdmitExecution(root, key, generation, "receipt-a", original).Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Unchanged,
                LiveStore.ProjectExecution(root, key, generation, original, "receipt-a").Status);
            Assert.Equal(bytes, File.ReadAllBytes(LiveStore.LedgerFile(key)));

            var decreased = AuthorityFill(generation, .25m);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "receipt-b", decreased).Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Unchanged,
                LiveStore.AdmitExecution(root, key, generation, "receipt-a", original).Status);
            var latest = LiveStore.LoadLedger(root, key, 10);
            Assert.Equal(.25m, latest.Fees);
            Assert.Equal(2m, latest.Position);
            Assert.Single(latest.Fills);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Superseded,
                LiveStore.ProjectExecution(root, key, generation, original, "receipt-a").Status);
            latest.Apply(original with { ProjectionRevision = 1 });
            Assert.Equal(.25m, latest.Fees); // A delayed local queue cannot undo the recovered newer head either.
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Error,
                LiveStore.AdmitExecution(root, key, generation, "receipt-a", decreased).Status);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_late_subscription_cannot_readmit_a_deleted_owner_but_a_fresh_generation_can_book(bool interrupted)
    {
        WithAuthorityRoot(root =>
        {
            const string key = "late-retired-subscription-fixture";
            var source = new ExecutionLog();
            var context = new OrderContext(new("late-subscriber", "simulator", "paper", "fixture", key, "generation"), root, source);
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            var oldHead = ScopedHead(context, owner, "late-old-head", true);
            string history = LiveStore.LedgerFile(key);
            string obstruction = history + ".id.tmp";
            if (interrupted)
            {
                Directory.CreateDirectory(obstruction);
                var failure = Record.Exception(() => LiveStore.DeleteFills(root, key));
                Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
                Assert.True(File.Exists(history + ".delete"));
            }
            else LiveStore.DeleteFills(root, key);
            try
            {
                // Owner capture predates deletion, but registration happens after any existing-slot fence.
                var projector = AccountExecutionProjector.For(source, context);
                using var late = projector.Subscribe(owner, () => { });
                Assert.True(late.Retired);
                projector.Synchronize();
                Assert.True(late.Retired);
                Assert.Equal(LiveStore.ExecutionProjectionStatus.Retired,
                    LiveStore.AdmitExecution(root, key, owner.LedgerGeneration, "old-owner-receipt", oldHead).Status);
                Assert.False(File.Exists(history + ".id"));
                if (Directory.Exists(obstruction)) Directory.Delete(obstruction);
                Assert.Empty(LiveStore.LoadLedger(root, key, 1).Fills);

                Guid generation = LiveStore.CaptureLedgerGeneration(root, key);
                Assert.NotEqual(owner.LedgerGeneration, generation);
                var freshOwner = context.LedgerOwner(key, generation);
                var head = ScopedHead(context, freshOwner, "late-fresh-head", true);
                var freshChanged = new ManualResetEventSlim(); // Not disposed: a late pass may still notify it.
                using var fresh = projector.Subscribe(freshOwner, freshChanged.Set);
                Assert.False(fresh.Retired);
                var intent = Assert.IsType<OwnedOrderIntent>(context.Find(head.OrderId));
                source.Publish(new(Assert.IsType<ExecutionIdentity>(head.ExecutionId), 1, Assert.IsType<string>(head.Symbol), head.Time,
                    head.Buy, head.Quantity, head.Price, head.PointValue, "USD", Array.Empty<ExecutionFee>(), true)
                {
                    ContextId = context.Descriptor.Id, IntentId = intent.Id, ClientOrderId = intent.ClientOrderId,
                });
                projector.Synchronize();
                // Synchronize coalesces into an in-flight pass, such as the one scheduled when the late owner retired,
                // and can return before that pass captures this execution. The owner is notified after its durable commit.
                AwaitExecution(fresh, freshChanged, head.ExecutionId!);
                var booked = LiveStore.LoadLedger(root, key, 1);
                Assert.Equal(1m, booked.Position);
                var actual = Assert.Single(booked.Fills);
                Assert.Equal(freshOwner.Id, actual.OwnerId);
                Assert.Equal(generation, actual.OwnerGeneration);
                Assert.False(fresh.Retired);
                Assert.True(late.Retired);
            }
            finally { if (Directory.Exists(obstruction)) Directory.Delete(obstruction); }
        });
    }

    [Fact]
    public void Reset_cannot_retire_a_generation_reopened_by_a_committed_head_before_its_admission_fence()
    {
        WithAuthorityRoot(root =>
        {
            const string key = "reset-committed-head-fixture";
            var source = new CaptureActionSource();
            var context = new OrderContext(new("reset-target", "simulator", "paper", "fixture", key, "generation"), root, source);
            var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
            var projector = AccountExecutionProjector.For(source, context);
            var opening = ScopedHead(context, owner, "reset-entry", true);
            var closing = ScopedHead(context, owner, "reset-exit", false);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, owner.LedgerGeneration, "entry", opening).Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.ProjectExecution(root, key, owner.LedgerGeneration, opening, "entry").Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, owner.LedgerGeneration, "exit", closing).Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.ProjectExecution(root, key, owner.LedgerGeneration, closing, "exit").Status);
            var local = LiveStore.LoadLedger(root, key, 1);
            AccountExecutionProjector.ProjectionSubscription? current = null;
            current = projector.Subscribe(owner, () =>
            {
                if (current == null) return;
                var updates = current.DrainCommitted();
                foreach (var fill in updates) local.Apply(fill);
                current.AcknowledgeCommitted(updates.Count);
            });
            using var subscription = current;
            projector.Synchronize();
            Assert.Equal(0m, local.Position);

            var gateSource = new ExecutionLog();
            var gateContext = new OrderContext(new("reset-other", "simulator", "paper", "fixture", "other", "generation"), root, gateSource);
            const string gateKey = "unrelated-reset-fixture";
            var gateOwner = gateContext.LedgerOwner(gateKey, LiveStore.CaptureLedgerGeneration(root, gateKey));
            var gateProjector = AccountExecutionProjector.For(gateSource, gateContext);
            using var gateSubscription = gateProjector.Subscribe(gateOwner, () => { });
            bool? accepted = null;
            var correction = closing with { Quantity = .5m };
            Assert.True(gateProjector.TryResetOwner(gateOwner, gateSubscription, 1, () =>
            {
                // A different real reset owns its admission gate. The current source capture then
                // commits a genuine owned financial amendment before the target's reset fence.
                source.BeforeCapture = () =>
                {
                    Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                        LiveStore.AdmitExecution(root, key, owner.LedgerGeneration, "exit-correction", correction).Status);
                    Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                        LiveStore.ProjectExecution(root, key, owner.LedgerGeneration, correction, "exit-correction").Status);
                };
                accepted = projector.TryResetOwner(owner, subscription, 1, () =>
                {
                    LiveStore.CaptureLedgerGeneration(root, key);
                    subscription.Dispose();
                });
            }));
            Assert.False(accepted);
            Assert.False(subscription.Retired);
            Assert.Equal(owner.LedgerGeneration, LiveStore.CaptureLedgerGeneration(root, key));
            Assert.Equal(.5m, LiveStore.LoadLedger(root, key, 1).Position);
        });
    }

    [Fact]
    public void Recovery_notifications_nested_in_reset_leave_other_account_admission_unlocked()
    {
        WithAuthorityRoot(root =>
        {
            var resetSource = new ExecutionLog();
            var resetContext = new OrderContext(new("callback-reset", "simulator", "paper", "fixture", "reset", "generation"), root, resetSource);
            const string resetKey = "callback-reset-fixture";
            var resetOwner = resetContext.LedgerOwner(resetKey, LiveStore.CaptureLedgerGeneration(root, resetKey));
            var resetProjector = AccountExecutionProjector.For(resetSource, resetContext);
            using var resetSubscription = resetProjector.Subscribe(resetOwner, () => { });
            var recoveredSource = new ExecutionLog();
            var recoveredContext = new OrderContext(new("callback-recovered", "simulator", "paper", "fixture", "recovered", "generation"), root, recoveredSource);
            const string recoveredKey = "callback-recovered-fixture";
            var recoveredOwner = recoveredContext.LedgerOwner(recoveredKey, LiveStore.CaptureLedgerGeneration(root, recoveredKey));
            var recoveredProjector = AccountExecutionProjector.For(recoveredSource, recoveredContext);
            var otherSource = new ExecutionLog();
            var otherContext = new OrderContext(new("callback-independent", "simulator", "paper", "fixture", "independent", "generation"), root, otherSource);
            var otherOwner = otherContext.LedgerOwner("callback-independent-fixture",
                LiveStore.CaptureLedgerGeneration(root, "callback-independent-fixture"));
            var otherProjector = AccountExecutionProjector.For(otherSource, otherContext);
            Task? admission = null;
            bool observed = false, admissionAvailableDuringCallback = true;
            using var completed = new ManualResetEventSlim();
            using var recoveredSubscription = recoveredProjector.Subscribe(recoveredOwner, () =>
            {
                observed = true;
                admission = Task.Run(() =>
                {
                    using var independent = otherProjector.Subscribe(otherOwner, () => { });
                });
                admissionAvailableDuringCallback &= admission.Wait(TimeSpan.FromSeconds(5));
                completed.Set();
            });
            var head = ScopedHead(recoveredContext, recoveredOwner, "callback-head", true);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, recoveredKey, recoveredOwner.LedgerGeneration, "callback-receipt", head).Status);
            Assert.True(resetProjector.TryResetOwner(resetOwner, resetSubscription, 1, () =>
            {
                LiveStore.LoadLedger(root, recoveredKey, 1);
                // This is the same ordinary nested synchronization used by a running reset consumer.
                resetProjector.Synchronize();
            }));
            Assert.True(completed.Wait(TimeSpan.FromSeconds(10)));
            admission?.GetAwaiter().GetResult();
            Assert.True(observed);
            Assert.True(admissionAvailableDuringCallback);
            Assert.Equal(1m, LiveStore.LoadLedger(root, recoveredKey, 1).Position);
        });
    }

    [Fact]
    public void Identical_current_fact_receipts_share_a_revision_but_changed_metadata_does_not()
    {
        WithAuthorityRoot(root =>
        {
            const string key = "complete-fact-alias-fixture";
            Guid generation = LiveStore.CaptureLedgerGeneration(root, key);
            var original = AuthorityFill(generation, .1m) with { ExecutionSequence = 1 };
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "first-receipt", original).Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.ProjectExecution(root, key, generation, original, "first-receipt").Status);
            byte[] committed = File.ReadAllBytes(LiveStore.LedgerFile(key));
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "duplicate-receipt", original).Status);
            var duplicate = LiveStore.ProjectExecution(root, key, generation, original, "duplicate-receipt");
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Unchanged, duplicate.Status);
            Assert.Equal(1L, duplicate.Revision);
            Assert.Equal(committed, File.ReadAllBytes(LiveStore.LedgerFile(key)));

            var changed = original with { ExecutionSequence = 2 };
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Error,
                LiveStore.AdmitExecution(root, key, generation, "duplicate-receipt", changed).Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "metadata-correction", changed).Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.ProjectExecution(root, key, generation, changed, "metadata-correction").Status);
            var latest = Assert.Single(LiveStore.LoadLedger(root, key, 10).Fills);
            Assert.Equal(2L, latest.ProjectionRevision);
            Assert.Equal(2L, latest.ExecutionSequence);
            Assert.Equal(.1m, LiveStore.LoadLedger(root, key, 10).Fees);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Superseded,
                LiveStore.ProjectExecution(root, key, generation, original, "duplicate-receipt").Status);
        });
    }

    [Fact]
    public void A_failed_attempt_reuses_its_existing_WAL_without_appending_the_same_revision_twice()
    {
        WithAuthorityRoot(root =>
        {
            const string key = "attempt-retry-fixture";
            Guid generation = LiveStore.CaptureLedgerGeneration(root, key);
            var original = AuthorityFill(generation, .1m);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "attempt-receipt", original).Status);
            string history = LiveStore.LedgerFile(key);
            // The ordinary drain succeeds, but publishing the new head's real WAL cannot open this path.
            Directory.CreateDirectory(history + ".wal.tmp");
            try
            {
                Assert.Equal(LiveStore.ExecutionProjectionStatus.Error,
                    LiveStore.ProjectExecution(root, key, generation, original, "attempt-receipt").Status);
            }
            finally { Directory.Delete(history + ".wal.tmp"); }
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.ProjectExecution(root, key, generation, original, "attempt-receipt").Status);
            Assert.Single(File.ReadAllLines(history));
            byte[] committed = File.ReadAllBytes(history);
            Assert.Equal(1L, Assert.Single(LiveStore.LoadLedger(root, key, 10).Fills).ProjectionRevision);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Unchanged,
                LiveStore.ProjectExecution(root, key, generation, original, "attempt-receipt").Status);
            Assert.Equal(committed, File.ReadAllBytes(history));
            Assert.Equal(0, LiveStore.PendingLedgerEvents(root, key));
        });
    }

    [Fact]
    public void A_durable_attempt_recovers_the_row_commit_before_authority_commit_gap_without_another_row()
    {
        WithAuthorityRoot(root =>
        {
            const string key = "attempt-commit-gap-fixture";
            Guid generation = LiveStore.CaptureLedgerGeneration(root, key);
            var original = AuthorityFill(generation, .1m);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "gap-receipt", original).Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.ProjectExecution(root, key, generation, original, "gap-receipt").Status);
            string history = LiveStore.LedgerFile(key);
            byte[] committed = File.ReadAllBytes(history);
            string authority = Assert.Single(Directory.EnumerateFiles(history + ".executions", "*.json", SearchOption.AllDirectories));
            var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(authority)));
            var payload = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsAssignableFrom<JsonValue>(envelope["Payload"]).GetValue<string>()));
            // Stage the actual durable phase: attempted revision and ledger row exist, but the final
            // authority commit did not occur. No native receipt is required to recover the admitted head.
            payload["AttemptedRevision"] = 1;
            payload["CommittedRevision"] = 0;
            payload["CommittedReceipt"] = null;
            string serialized = payload.ToJsonString();
            envelope["Payload"] = serialized;
            envelope["Hash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
            using (var stream = new FileStream(authority, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, envelope);
                stream.Flush(flushToDisk: true);
            }
            using (var marker = new FileStream(Path.ChangeExtension(authority, ".pending"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                marker.Flush(flushToDisk: true);
            var recovered = LiveStore.LoadLedger(root, key, 10);
            Assert.Equal(.1m, recovered.Fees);
            Assert.Equal(2m, recovered.Position);
            Assert.Equal(1L, Assert.Single(recovered.Fills).ProjectionRevision);
            Assert.Equal(committed, File.ReadAllBytes(history));
            Assert.Equal(0, LiveStore.PendingLedgerEvents(root, key));
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Unchanged,
                LiveStore.ProjectExecution(root, key, generation, original, "gap-receipt").Status);
            Assert.Equal(committed, File.ReadAllBytes(history));
        });
    }

    [Fact]
    public void Retired_history_cannot_recover_or_readmit_an_old_physical_head()
    {
        WithAuthorityRoot(root =>
        {
            const string key = "retired-admission-fixture";
            Guid originalGeneration = LiveStore.CaptureLedgerGeneration(root, key);
            var original = AuthorityFill(originalGeneration, 1);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, originalGeneration, "retired-receipt", original).Status);
            LiveStore.DeleteFills(root, key);
            Guid replacement = LiveStore.CaptureLedgerGeneration(root, key);
            Assert.NotEqual(originalGeneration, replacement);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Retired,
                LiveStore.ProjectExecution(root, key, originalGeneration, original, "retired-receipt").Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Retired,
                LiveStore.AdmitExecution(root, key, originalGeneration, "retired-receipt", original).Status);
            var current = LiveStore.LoadLedger(root, key, 10);
            Assert.Equal(0m, current.Position);
            Assert.Empty(current.Fills);
            Assert.Equal(0, LiveStore.PendingLedgerEvents(root, key));
        });
    }

    [Fact]
    public void Interrupted_deletion_is_completed_before_an_ordinary_flush_can_recover_a_retired_admitted_head()
    {
        WithAuthorityRoot(root =>
        {
            const string key = "interrupted-admission-delete-fixture";
            Guid retiredGeneration = LiveStore.CaptureLedgerGeneration(root, key);
            var retired = AuthorityFill(retiredGeneration, 1);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, retiredGeneration, "retired-during-delete", retired).Status);
            // Exact durable boundary of DeleteFills: marker published, old generation/head still present.
            using (var deletion = new FileStream(LiveStore.LedgerFile(key) + ".delete", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                deletion.Flush(flushToDisk: true);
            string? error = LiveStore.SavePendingLedgerEvents(root, key);
            var current = LiveStore.LoadLedger(root, key, 10);
            Assert.Empty(current.Fills); // An error after appending the old fill would not undo resurrection.
            Assert.Equal(0m, current.Position);
            Assert.Null(error);
            Guid replacement = LiveStore.CaptureLedgerGeneration(root, key);
            Assert.NotEqual(retiredGeneration, replacement);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Retired,
                LiveStore.ProjectExecution(root, key, retiredGeneration, retired, "retired-during-delete").Status);
            Assert.Empty(LiveStore.LoadLedger(root, key, 10).Fills);
        });
    }

    [Fact]
    public void A_new_head_equal_to_the_old_committed_fact_still_overrides_an_older_pending_correction()
    {
        WithAuthorityRoot(root =>
        {
            const string key = "equal-head-pending-fixture";
            Guid generation = LiveStore.CaptureLedgerGeneration(root, key);
            var first = AuthorityFill(generation, .1m);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "receipt-a", first).Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.ProjectExecution(root, key, generation, first, "receipt-a").Status);
            var intermediate = AuthorityFill(generation, .2m);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "receipt-b", intermediate).Status);
            string history = LiveStore.LedgerFile(key);
            string preserved = history + ".owned-equal-head-history";
            File.Move(history, preserved);
            try
            {
                Directory.CreateDirectory(history);
                Assert.Equal(LiveStore.ExecutionProjectionStatus.Error,
                    LiveStore.ProjectExecution(root, key, generation, intermediate, "receipt-b").Status);
                // This genuinely newer observation equals A, not B. Fee magnitude is not an ordering rule.
                Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                    LiveStore.AdmitExecution(root, key, generation, "receipt-c", first).Status);
            }
            finally
            {
                if (Directory.Exists(history)) Directory.Delete(history);
                File.Move(preserved, history);
            }
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.ProjectExecution(root, key, generation, first, "receipt-c").Status);
            var final = LiveStore.LoadLedger(root, key, 10);
            Assert.Equal(.1m, final.Fees);
            Assert.Equal(2m, final.Position);
            Assert.Single(final.Fills);
            Assert.Equal(3L, Assert.Single(final.Fills).ProjectionRevision);
            Assert.Equal(0, LiveStore.PendingLedgerEvents(root, key));
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Superseded,
                LiveStore.ProjectExecution(root, key, generation, intermediate, "receipt-b").Status);
            Assert.Equal(.1m, LiveStore.LoadLedger(root, key, 10).Fees);
        });
    }

    [Fact]
    public void Corrupted_pending_physical_authority_is_visible_and_cannot_be_reinterpreted_as_empty_history()
    {
        WithAuthorityRoot(root =>
        {
            const string key = "corrupt-admission-fixture";
            Guid generation = LiveStore.CaptureLedgerGeneration(root, key);
            var original = AuthorityFill(generation, 1);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Applied,
                LiveStore.AdmitExecution(root, key, generation, "corrupt-receipt", original).Status);
            string authority = Assert.Single(Directory.EnumerateFiles(LiveStore.LedgerFile(key) + ".executions", "*.json", SearchOption.AllDirectories));
            File.WriteAllText(authority, """{"Payload":"damaged","Hash":"not-a-valid-checksum"}""");
            Assert.Throws<IOException>(() => LiveStore.LoadLedger(root, key, 10));
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Error,
                LiveStore.ProjectExecution(root, key, generation, original, "corrupt-receipt").Status);
            Assert.Equal(LiveStore.ExecutionProjectionStatus.Error,
                LiveStore.AdmitExecution(root, key, generation, "new-receipt", original).Status);
        });
    }

    private static LiveFill ScopedHead(OrderContext context, OrderOwner owner, string id, bool buy)
    {
        var ticket = context.Prepare(owner, new("test:TEST", buy ? OrderDirections.Buy : OrderDirections.Sell,
            OrderTypes.Limit, 1, 100, StrategyQuantity: 1));
        Assert.Null(ticket.Error);
        return new(Start.AddMinutes(buy ? 0 : 1), buy, 1, 100, "TEST", 1)
        {
            Kind = LiveFillKind.Execution, OrderId = ticket.LogicalOrderId,
            ExecutionId = new(new("simulator", context.Descriptor.Id, "fixture", "test:TEST")
                { ContextId = context.Descriptor.Id }, id),
            ContextId = context.Descriptor.Id, OwnerId = owner.Id, OwnerGeneration = owner.LedgerGeneration,
            Fees = Array.Empty<ExecutionFee>(), FeesComplete = true, Currency = "USD",
        };
    }

    private static void AwaitExecution(AccountExecutionProjector.ProjectionSubscription subscription,
        ManualResetEventSlim changed, ExecutionIdentity execution)
    {
        while (!subscription.Drain().Any(update => update.Report.Identity == execution))
        {
            Assert.True(changed.Wait(TimeSpan.FromSeconds(10)), "The subscribed owner was not notified of its execution.");
            changed.Reset();
        }
    }

    private sealed class CaptureActionSource : IExecutionSource
    {
        private readonly ExecutionLog _inner = new();
        public Action? BeforeCapture { get; set; }
        public Guid Epoch => _inner.Epoch;
        public event Action? Changed { add => _inner.Changed += value; remove => _inner.Changed -= value; }
        public ExecutionSnapshot Capture(ExecutionCursor cursor)
        {
            var action = BeforeCapture;
            BeforeCapture = null;
            action?.Invoke();
            return _inner.Capture(cursor);
        }
    }

    private static LiveFill AuthorityFill(Guid generation, decimal fee)
    {
        var fill = Fill("physical-execution", 0, true, 2, 100, fee);
        var identity = Assert.IsType<ExecutionIdentity>(fill.ExecutionId);
        return fill with
        {
            ContextId = "owned-physical-fixture", OwnerId = "owned-physical-owner", OwnerGeneration = generation,
            ExecutionId = identity with { Scope = identity.Scope with { ContextId = "owned-physical-fixture" } },
        };
    }

    private static void WithAuthorityRoot(Action<string> action)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-physical-authority-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = root;
        try { action(root); }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static LiveFill Fill(string id, int seconds, bool buy, decimal quantity, decimal price, decimal fee) =>
        new(Start.AddSeconds(seconds), buy, quantity, price, "FUTURE", 10)
        {
            Kind = LiveFillKind.Execution, OrderId = id, ExecutionId = new(new("fixture", "account", "fake", "FUTURE"), id),
            Fees = new[] { new ExecutionFee(fee, "USD") }, FeesComplete = true, Currency = "USD",
        };
}
