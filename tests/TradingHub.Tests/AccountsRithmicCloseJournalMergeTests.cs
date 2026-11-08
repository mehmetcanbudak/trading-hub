using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed partial class AccountsRithmicCloseRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Separate_cached_processes_cannot_dispatch_a_close_after_their_merged_reductions_exhaust_its_ceiling(bool secondIsSurveillance)
    {
        string root = CreateCloseJournalRoot();
        try
        {
            var context = new OrderContext(ClaimDescriptor(), Path.Combine(root, "shared-execution"));
            var firstFact = new PositionCloseCancellation("journal-reduction-one", false, 0, 0, true);
            var secondFact = new PositionCloseCancellation("journal-reduction-two", false, 0, 0, true);
            var state = NativeClose() with
            {
                Cancellations = secondIsSurveillance ? new[] { firstFact } : new[] { firstFact, secondFact },
                Surveillance = secondIsSurveillance ? new[] { new PositionCloseObservation(secondFact.Basket, false, 0, 0, true) } : null,
            };
            var ticket = context.BeginPositionClose("rithmic:ESZ6", state);
            Assert.Null(ticket.Error);
            using var first = StartCloseJournalProcess(root, "merge-first");
            using var second = StartCloseJournalProcess(root, "merge-second");
            var firstError = first.StandardError.ReadToEndAsync();
            var secondError = second.StandardError.ReadToEndAsync();
            try
            {
                Assert.Equal("checkpoint", await first.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
                Assert.Equal("checkpoint", await second.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
                using var firstResult = await ReleaseCloseJournalProcess(first, firstError);
                Assert.Null(firstResult.RootElement.GetProperty("Error").GetString());
                using var secondResult = await ReleaseCloseJournalProcess(second, secondError);
                Assert.False(secondResult.RootElement.GetProperty("Dispatched").GetBoolean());
                Assert.Equal(2m, secondResult.RootElement.GetProperty("KnownReductions").GetDecimal());
                var durable = new OrderContext(ClaimDescriptor(), Path.Combine(root, "shared-execution"));
                Assert.False(durable.Find(ticket.Id)!.DispatchUncertain);
                Assert.Equal(ticket.Id, Assert.Single(durable.PositionCloses()).Id);
            }
            finally { await FinishCloseJournalProcesses(first, second); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("terminal")]
    public async Task A_stale_native_mutation_cannot_release_a_completed_closes_late_fill_reservation(string mutation)
    {
        string root = CreateCloseJournalRoot();
        try
        {
            var context = new OrderContext(ClaimDescriptor(), Path.Combine(root, "shared-execution"));
            var state = NativeClose() with
            {
                Cancellations = new[] { new PositionCloseCancellation("journal-reduction-one", false, 0, 0, true) },
            };
            var original = context.BeginPositionClose("rithmic:ESZ6", state);
            Assert.Null(original.Error);
            Assert.Null(context.StartDispatch(original, "ESZ6", "CME", 2));
            if (mutation == "terminal")
                Assert.Null(context.BindNative(original, new NativeOrderIdentity("rithmic:basket", "journal-close-basket")));
            context.UpdatePositionClose(original, state with { Phase = PositionClosePhase.Completed });
            using var observation = StartCloseJournalProcess(root, "sticky-fact");
            using var stale = StartCloseJournalProcess(root, mutation);
            var observationError = observation.StandardError.ReadToEndAsync();
            var staleError = stale.StandardError.ReadToEndAsync();
            try
            {
                Assert.Equal("checkpoint", await observation.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
                Assert.Equal("checkpoint", await stale.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
                using var observed = await ReleaseCloseJournalProcess(observation, observationError);
                Assert.Null(observed.RootElement.GetProperty("Error").GetString());
                Assert.Equal(1m, observed.RootElement.GetProperty("KnownReductions").GetDecimal());
                using var saved = await ReleaseCloseJournalProcess(stale, staleError);
                Assert.Null(saved.RootElement.GetProperty("Error").GetString());
                var consumer = new OrderContext(ClaimDescriptor(), Path.Combine(root, "shared-execution"));
                var subsequent = consumer.BeginPositionClose("rithmic:ESZ6", NativeClose() with { ConfirmationRevision = 10001 });
                Assert.Null(subsequent.Error);
                string? dispatch = consumer.StartDispatch(subsequent, "ESZ6", "CME", 2);
                Assert.NotNull(dispatch);
                Assert.Equal(original.Id, subsequent.Id);
                var retained = consumer.PositionCloseState(subsequent);
                Assert.Equal(1m, retained.KnownReductions);
                Assert.True(retained.Invalidated);
                Assert.True(retained.LateFillUnresolved);
                Assert.Equal(PositionClosePhase.Unresolved, retained.Phase);
            }
            finally { await FinishCloseJournalProcesses(observation, stale); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_released_send_slot_refusal_is_cleared_once_the_same_close_commits()
    {
        string root = CreateCloseJournalRoot();
        try
        {
            var context = new OrderContext(ClaimDescriptor(), Path.Combine(root, "shared-execution"));
            var close = context.BeginPositionClose("rithmic:ESZ6", NativeClose());
            Assert.Null(close.Error);
            const string refusal = "Rithmic replay or cancellation is unresolved at the send slot; no close was sent.";
            string? refused = context.StartDispatch(close, "ESZ6", "CME", 2m, 0m, 2m, _ => refusal);
            Assert.NotNull(refused);
            Assert.EndsWith(refusal, refused);
            Assert.Equal(refused, context.LastError);
            Assert.False(context.Find(close.Id)!.DispatchUncertain);
            Assert.Null(context.StartDispatch(close, "ESZ6", "CME", 2m, 0m, 2m, _ => null));
            Assert.Null(context.LastError);
            Assert.True(context.Find(close.Id)!.DispatchUncertain);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Terminal_replay_repairs_an_interrupted_accepted_amendment_index_without_promoting_other_successors()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, ReplayTerminalOrders = true };
        server.SeedPosition(true, 4, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var chart = fixture.Chart("ES");
        await Until(() => chart.PositionReady && chart.OrdersReady);
        var order = OrderActions.Manual(chart, OrderTypes.Limit, OrderDirections.Sell, 1, 110, reduceOnly: true);
        await Until(() => order.ExchangeOrderId.Length > 0 && order.QuantityToFill == 1);
        string basket = order.ExchangeOrderId;
        string originalTag = Assert.IsType<string>(order.ClientOrderId);
        string logical = Assert.IsType<string>(order.LogicalOrderId);
        string predecessor = Assert.IsType<string>(order.IntentId);
        var descriptor = chart.OrderContext.Descriptor;
        string journal = Path.Combine(fixture.ExecutionRoot, "execution-state", OrderContext.Hash(descriptor.OwnershipNamespace ?? descriptor.Id));
        string indexPath = Path.Combine(journal, "native", OrderContext.NativeKey(new NativeOrderIdentity("rithmic:basket", basket)) + ".json");
        await Until(() => File.Exists(indexPath));
        byte[] originalIndex = await File.ReadAllBytesAsync(indexPath);
        OrderActions.Replace(chart, order, 109, 2);
        await Until(() => order.QuantityToFill == 2 && !((ITrackedOrderReplacement)chart).IsReplacementPending(order));
        string accepted = Assert.IsType<string>(order.IntentId);
        Assert.NotEqual(predecessor, accepted);
        Assert.NotNull(chart.OrderContext.Find(accepted)!.AcceptedNativeAmendment);
        var unsent = chart.OrderContext.PrepareReplacement(order, 108, 3);
        Assert.Null(unsent.Error);
        server.ModifyRejection = "fixture rejected interrupted-index competitor";
        OrderActions.Replace(chart, order, 107, 3);
        await Until(() => !((ITrackedOrderReplacement)chart).IsReplacementPending(order) &&
            chart.LastError?.Contains("fixture rejected interrupted-index competitor", StringComparison.Ordinal) == true);
        await fixture.Session.StopAsync().WaitAsync(Deadline);
        string acceptedPath = Path.Combine(journal, "intent", OrderContext.Hash(accepted) + ".json");
        byte[] acceptedRecord = await File.ReadAllBytesAsync(acceptedPath);
        await File.WriteAllBytesAsync(indexPath, originalIndex);
        Assert.Equal(acceptedRecord, await File.ReadAllBytesAsync(acceptedPath));
        await server.RemoveOrderWhileDisconnected(basket);
        server.SeedCompletedOrder(basket, originalTag, false, 2, 109);
        await fixture.RestartAsync();
        var terminal = Assert.Single(fixture.Session.OrdersOf("ACC1"), native => native.BasketId == basket);
        Assert.True(terminal.Done);
        Assert.Equal(2m, terminal.Quantity);
        var restoredContext = new OrderContext(descriptor, fixture.ExecutionRoot);
        var quantities = new ConcurrentQueue<OwnedOrderQuantity>();
        restoredContext.QuantityChanged += quantities.Enqueue;
        using var restored = new RithmicBroker(chart.Instrument, fixture.Session, "ACC1", "Interrupted amendment recovery", "ESZ6", "CME", restoredContext);
        var projected = Assert.Single(quantities);
        Assert.Equal(logical, projected.Intent.LogicalOrderId);
        Assert.Equal(chart.OrderContext.ManualOwner, projected.Intent.Owner);
        Assert.Equal(2m, projected.CumulativeQuantity);
        Assert.False(projected.Buy);
        Assert.Empty(restored.ActiveOrders);
        var recovered = restoredContext.Find(accepted)!;
        Assert.True(recovered.Request.ReduceOnly);
        Assert.Equal(2m, recovered.TerminalOrder!.CumulativeQuantity);
        Assert.Equal(accepted, restoredContext.FindNativeSubmission(originalTag, new NativeOrderIdentity("rithmic:basket", basket))!.Id);
        Assert.NotEqual(unsent.Id, recovered.Id);
        Assert.DoesNotContain("recovery is unresolved", restored.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Single(Tags(server));
        Assert.Equal(2, server.Position);
    }

    private static string CreateCloseJournalRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-close-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".ledger-fixture-root"), "native journal fixture");
        return root;
    }

    private static Process StartCloseJournalProcess(string root, string operation)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LedgerCrashFixture", "TradingHub.LedgerCrashFixture.dll"));
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("native-close-journal");
        start.ArgumentList.Add(operation);
        foreach (string store in new[] { "DATA", "STRATEGIES", "INDICATORS", "BACKTESTS", "LIVE" })
            start.Environment["TRADINGHUB_" + store + "_DIR"] = Path.Combine(root, operation, store.ToLowerInvariant());
        start.Environment["TRADINGHUB_KEYCHAIN_FILE"] = Path.Combine(root, operation, "fake-keychain.json");
        start.Environment["TRADINGHUB_SLEEP_GUARD"] = "off";
        return Process.Start(start)!;
    }

    private static async Task<JsonDocument> ReleaseCloseJournalProcess(Process process, Task<string> error)
    {
        await process.StandardInput.WriteLineAsync("continue");
        await process.StandardInput.FlushAsync();
        string result = Assert.IsType<string>(await process.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
        await process.WaitForExitAsync().WaitAsync(Deadline);
        Assert.True(process.ExitCode == 0, await error);
        return JsonDocument.Parse(result);
    }

    private static async Task FinishCloseJournalProcesses(params Process[] processes)
    {
        foreach (var process in processes)
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("continue");
                await process.StandardInput.FlushAsync();
            }
        await Task.WhenAll(processes.Select(process => process.WaitForExitAsync())).WaitAsync(Deadline);
    }
}
