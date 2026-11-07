using System.Collections.Concurrent;
using TradingHub.App.Services;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed partial class AccountsRithmicCloseRegressionTests
{
    [Fact]
    public async Task Native_admission_outcome_a_known_zero_unseen_target_survives_a_definite_precommit_route_refusal()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        await server.PushOrder("precommit-unseen-stop", "external-precommit-unseen", false, 2, 95, type: 4);
        await Until(() => fixture.Session.OrdersOf("ACC1").Any(order =>
            order.BasketId == "precommit-unseen-stop" && order.CumulativeQuantityKnown));
        await fixture.Session.CancelAsync("ACC1", "precommit-unseen-stop", CancellationToken.None).WaitAsync(Deadline);
        await Until(() => fixture.Session.OrdersOf("ACC1").Any(order =>
            order.BasketId == "precommit-unseen-stop" && order.NativeTerminal && order.Cancelled));
        var historical = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        var historicalFact = Assert.Single(historical.Confirmation.State.Cancellations);
        Assert.True(historicalFact.Terminal);
        Assert.False(historicalFact.Buy);
        Assert.Equal(0m, historicalFact.Baseline);
        Assert.Equal(0m, historicalFact.Observed);
        await fixture.RestartAsync();
        Assert.DoesNotContain(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == historicalFact.Basket);
        var row = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(row.Ref);
        var context = captured.Confirmation.Context;
        Assert.Empty(captured.Confirmation.State.Cancellations);
        var original = context.BeginPositionClose(captured.Broker.Instrument.Key, captured.Confirmation.State);
        Assert.Null(original.Error);
        var writer = new OrderContext(context.Descriptor, fixture.ExecutionRoot);
        var writerTicket = Assert.Single(writer.PositionCloses());
        var writerState = writer.PositionCloseState(writerTicket);
        Assert.Equal(original.Id, writerTicket.Id);
        Assert.Empty(writerState.Cancellations);
        Assert.False(context.HasStorageFault);
        var routes = NativeField<ConcurrentDictionary<(string Context, string Instrument), WeakReference<ITradingAccount>>>(
            fixture.Services, "_wireAccounts");
        var actualPermission = Assert.IsType<Func<OrderRequest, decimal, string?>>(
            NativeField<object>(context, "_nativePermission"));
        int injected = 0;
        bool routeRestored = false;
        string? policyDenial = null;
        SearchZoomFixture.Set(context, "_nativePermission", (Func<OrderRequest, decimal, string?>)((request, position) =>
        {
            string? denied = actualPermission(request, position);
            if (denied != null || Interlocked.CompareExchange(ref injected, 1, 0) != 0) return denied;
            writer.UpdatePositionClose(writerTicket, writerState with { Cancellations = new[] { historicalFact } });
            var key = (context.Descriptor.Id, request.Instrument);
            if (!routes.TryRemove(key, out var route))
                throw new InvalidOperationException("The native admission fixture could not withdraw its captured route.");
            try
            {
                policyDenial = actualPermission(request, position);
                return policyDenial ?? throw new InvalidOperationException("The real native policy did not refuse the unavailable route.");
            }
            finally { routeRestored = routes.TryAdd(key, route); }
        }));
        string? outcome;
        try { outcome = await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline); }
        finally { SearchZoomFixture.Set(context, "_nativePermission", actualPermission); }

        Assert.Equal(1, Volatile.Read(ref injected));
        Assert.True(routeRestored);
        Assert.NotNull(policyDenial);
        Assert.NotNull(outcome);
        var state = context.PositionCloseState(original);
        Assert.Equal(historicalFact, Assert.Single(state.Cancellations));
        Assert.Equal(0m, state.KnownReductions);
        Assert.False(state.Invalidated);
        Assert.False(context.Find(original.Id)!.DispatchUncertain);
        Assert.False(context.HasStorageFault);
        Assert.Equal(original.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(original.ClientOrderId, context.Find(original.Id)!.ClientOrderId);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
        Assert.Equal(1, Requests(server, "316"));
        Assert.Equal(2, server.Position);
        Assert.Equal(PositionClosePhase.Unresolved, state.Phase);
        Assert.True(fixture.Session.HasUnresolvedPositionClose);
        await server.PublishPosition();
        await Until(() => fixture.Position().Ref is RithmicPositionTarget fresh &&
            fresh.Confirmation.State.ConfirmationRevision > state.SettledRevision);
        Assert.NotNull(await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline));
        Assert.Equal(original.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(original.ClientOrderId, context.Find(original.Id)!.ClientOrderId);
        Assert.Equal(PositionClosePhase.Unresolved, context.PositionCloseState(original).Phase);
        Assert.False(context.HasStorageFault);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
        Assert.Equal(2, server.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_admission_outcome_an_unknown_reducing_fact_at_the_consuming_lease_is_not_a_storage_fault(bool surveillanceOnly)
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        await server.PushOrder("leased-null-counter-stop", "external-leased-null-counter", false, 2, 95,
            type: 4, cumulativeKnown: false);
        await Until(() => fixture.Session.OrdersOf("ACC1").Any(order => order.BasketId == "leased-null-counter-stop"));
        var native = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "leased-null-counter-stop");
        Assert.False(native.CumulativeQuantityKnown);
        await fixture.Session.CancelAsync("ACC1", native.BasketId, CancellationToken.None).WaitAsync(Deadline);
        await Until(() => native.NativeTerminal && native.Cancelled);
        Assert.False(native.CumulativeQuantityKnown);
        var historical = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        var historicalFact = Assert.Single(historical.Confirmation.State.Cancellations);
        Assert.True(historicalFact.Terminal);
        Assert.False(historicalFact.Buy);
        Assert.Null(historicalFact.Baseline);
        Assert.Null(historicalFact.Observed);
        await fixture.RestartAsync();
        Assert.DoesNotContain(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == historicalFact.Basket);
        var row = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(row.Ref);
        var context = captured.Confirmation.Context;
        Assert.Empty(captured.Confirmation.State.Cancellations);
        var original = context.BeginPositionClose(captured.Broker.Instrument.Key, captured.Confirmation.State);
        Assert.Null(original.Error);
        var writer = new OrderContext(context.Descriptor, fixture.ExecutionRoot);
        var writerTicket = Assert.Single(writer.PositionCloses());
        var writerState = writer.PositionCloseState(writerTicket);
        Assert.Equal(original.Id, writerTicket.Id);
        Assert.Empty(writerState.Cancellations);
        Assert.Empty(writerState.Surveillance ?? Array.Empty<PositionCloseObservation>());
        Assert.False(context.HasStorageFault);
        var actualPermission = Assert.IsType<Func<OrderRequest, decimal, string?>>(
            NativeField<object>(context, "_nativePermission"));
        int injected = 0;
        SearchZoomFixture.Set(context, "_nativePermission", (Func<OrderRequest, decimal, string?>)((request, position) =>
        {
            string? denied = actualPermission(request, position);
            if (denied == null && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
                writer.UpdatePositionClose(writerTicket, writerState with
                {
                    Cancellations = surveillanceOnly ? writerState.Cancellations : new[] { historicalFact },
                    Surveillance = surveillanceOnly ? new[] { new PositionCloseObservation(historicalFact.Basket,
                        historicalFact.Buy, historicalFact.Baseline, historicalFact.Observed, historicalFact.Terminal) } : writerState.Surveillance,
                });
            return denied;
        }));
        string? outcome;
        try { outcome = await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline); }
        finally { SearchZoomFixture.Set(context, "_nativePermission", actualPermission); }

        Assert.Equal(1, Volatile.Read(ref injected));
        Assert.NotNull(outcome);
        var state = context.PositionCloseState(original);
        if (surveillanceOnly)
        {
            Assert.Empty(state.Cancellations);
            var observed = Assert.Single(state.Surveillance!);
            Assert.Equal(historicalFact.Basket, observed.Basket);
            Assert.False(observed.Buy);
            Assert.Null(observed.Baseline);
            Assert.Null(observed.Observed);
            Assert.True(observed.Terminal);
        }
        else Assert.Equal(historicalFact, Assert.Single(state.Cancellations));
        Assert.Equal(0m, state.KnownReductions);
        Assert.False(state.Invalidated);
        Assert.Equal(PositionClosePhase.Unresolved, state.Phase);
        Assert.False(context.Find(original.Id)!.DispatchUncertain);
        Assert.Equal(original.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(original.ClientOrderId, context.Find(original.Id)!.ClientOrderId);
        Assert.True(fixture.Session.HasUnresolvedPositionClose);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
        Assert.Equal(1, Requests(server, "316"));
        Assert.Equal(2, server.Position);
        Assert.NotNull(await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline));
        Assert.Equal(original.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(original.ClientOrderId, context.Find(original.Id)!.ClientOrderId);
        Assert.Equal(PositionClosePhase.Unresolved, context.PositionCloseState(original).Phase);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
        Assert.False(context.HasStorageFault);

        var publicContext = new OrderContext(context.Descriptor, fixture.ExecutionRoot);
        var publicTicket = Assert.Single(publicContext.PositionCloses());
        Assert.Null(publicContext.CheckDispatch(publicTicket, 1));
        Assert.NotNull(publicContext.StartDispatch(publicTicket, "ESZ6", "CME", 1));
        Assert.False(publicContext.HasStorageFault);
        Assert.Equal(original.Id, publicTicket.Id);
        Assert.Equal(PositionClosePhase.Unresolved, publicContext.PositionCloseState(publicTicket).Phase);
        Assert.False(publicContext.Find(original.Id)!.DispatchUncertain);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_admission_outcome_an_exhausted_public_journal_ceiling_is_not_a_storage_fault(bool secondIsSurveillance)
    {
        string root = CreateCloseJournalRoot();
        try
        {
            var descriptor = ClaimDescriptor();
            string storage = Path.Combine(root, "shared-execution");
            var context = new OrderContext(descriptor, storage);
            var firstFact = new PositionCloseCancellation("journal-reduction-one", false, 0, 0, true);
            var secondFact = new PositionCloseCancellation("journal-reduction-two", false, 0, 0, true);
            var initial = NativeClose() with
            {
                Cancellations = secondIsSurveillance ? new[] { firstFact } : new[] { firstFact, secondFact },
                Surveillance = secondIsSurveillance ? new[] { new PositionCloseObservation(secondFact.Basket, false, 0, 0, true) } : null,
            };
            var original = context.BeginPositionClose("rithmic:ESZ6", initial);
            Assert.Null(original.Error);
            var cached = new OrderContext(descriptor, storage);
            var ticket = Assert.Single(cached.PositionCloses());
            var cachedState = cached.PositionCloseState(ticket);
            Assert.Equal(original.Id, ticket.Id);
            Assert.Equal(0m, cachedState.KnownReductions);
            using var child = StartCloseJournalProcess(root, "merge-first");
            var childError = child.StandardError.ReadToEndAsync();
            try
            {
                Assert.Equal("checkpoint", await child.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
                using var first = await ReleaseCloseJournalProcess(child, childError);
                Assert.Null(first.RootElement.GetProperty("Error").GetString());
                Assert.Equal(original.Id, first.RootElement.GetProperty("Id").GetString());
                Assert.Equal(1m, first.RootElement.GetProperty("KnownReductions").GetDecimal());
                cached.UpdatePositionClose(ticket, cachedState with
                {
                    Cancellations = cachedState.Cancellations.Select(fact =>
                        fact.Basket == secondFact.Basket ? fact with { Observed = 1 } : fact).ToArray(),
                    Surveillance = cachedState.Surveillance?.Select(fact =>
                        fact.Basket == secondFact.Basket ? fact with { Observed = 1 } : fact).ToArray(),
                    KnownReductions = 1,
                });
                var merged = cached.PositionCloseState(ticket);
                Assert.Equal(2m, merged.KnownReductions);
                Assert.Equal(1m, Assert.Single(merged.Cancellations, fact => fact.Basket == firstFact.Basket).Observed);
                if (secondIsSurveillance) Assert.Equal(1m, Assert.Single(merged.Surveillance!).Observed);
                else Assert.Equal(1m, Assert.Single(merged.Cancellations, fact => fact.Basket == secondFact.Basket).Observed);
                Assert.False(merged.Invalidated);
                Assert.Equal(PositionClosePhase.Reconciling, merged.Phase);
                Assert.False(cached.HasStorageFault);
                Assert.Null(cached.CheckDispatch(ticket, 1));
                string? denied = cached.StartDispatch(ticket, "ESZ6", "CME", 1);
                Assert.NotNull(denied);
                var durable = new OrderContext(descriptor, storage);
                Assert.Equal(2m, durable.PositionCloseState(Assert.Single(durable.PositionCloses())).KnownReductions);
                Assert.Equal(original.Id, Assert.Single(durable.PositionCloses()).Id);
                Assert.Equal(original.ClientOrderId, durable.Find(original.Id)!.ClientOrderId);
                Assert.False(durable.Find(original.Id)!.DispatchUncertain);
                Assert.Equal(2m, cached.PositionCloseState(ticket).KnownReductions);
                Assert.False(cached.HasStorageFault);
            }
            finally { await FinishCloseJournalProcesses(child); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
