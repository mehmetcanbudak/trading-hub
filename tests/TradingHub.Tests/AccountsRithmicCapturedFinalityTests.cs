using TradingHub.App.Services;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed partial class AccountsRithmicCloseRegressionTests
{
    [Fact]
    public async Task Captured_finality_replay_absence_before_peer_replay_cannot_skip_working_protection()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("captured-absent-stop", "external-captured-absence", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var original = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "captured-absent-stop");
        server.OrderReplayExclusions.Add(original.BasketId);
        await server.DisconnectOrderSession(0).WaitAsync(Deadline);
        await Until(() => Requests(server, "320") >= 2 && fixture.Session.OrdersReadyFor("ACC1") && original.ReplayAbsent);
        Assert.True(original.Done);
        Assert.False(original.NativeTerminal);
        Assert.True(original.CumulativeQuantityKnown);
        Assert.Equal(0m, original.Filled);
        var row = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(row.Ref);
        Assert.Empty(captured.Confirmation.Context.PositionCloses());
        Assert.Equal(2, Requests(server, "320"));
        server.OrderReplayExclusions.Remove(original.BasketId);
        await using var peer = await NativePeer.CreateAsync(fixture, server, captured);
        var working = Assert.Single(peer.Session.OrdersOf("ACC1"), order => order.BasketId == original.BasketId);
        Assert.False(working.Done);
        Assert.Equal(2m, working.Unfilled);
        Assert.Equal(0m, working.Filled);
        Assert.True(original.ReplayAbsent);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "316"));

        string? outcome = await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline);
        if (outcome != null)
        {
            Assert.False(string.IsNullOrWhiteSpace(outcome));
            Assert.Empty(Tags(server));
            Assert.Equal(0, Requests(server, "312"));
            Assert.Equal(2, server.Position);
            Assert.Equal(2m, fixture.Net);
            return;
        }

        await Until(() => fixture.Net == 0 && fixture.Session.OrdersOf("ACC1").Any(order => order.PriceType == 2 && order.Done));
        Assert.True(working.Cancelled, "A successful captured close must cancel the later known working protection.");
        Assert.True(working.Done);
        string[] requests;
        lock (server.Received) requests = server.Received.ToArray();
        int create = Array.IndexOf(requests, "312");
        int cancel = Array.IndexOf(requests, "316");
        Assert.True(cancel >= 0 && cancel < create, "Native cancellation must precede the market close request.");
        Assert.Equal(1, Requests(server, "316"));
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(0, server.Kicks);
    }

    [Fact]
    public async Task Captured_finality_peer_working_replay_after_claim_is_rechecked_at_the_actual_send_slot()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("claimed-absent-stop", "external-claimed-absence", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var original = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "claimed-absent-stop");
        server.OrderReplayExclusions.Add(original.BasketId);
        await server.DisconnectOrderSession(0).WaitAsync(Deadline);
        await Until(() => Requests(server, "320") >= 2 && fixture.Session.OrdersReadyFor("ACC1") && original.ReplayAbsent);
        var row = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(row.Ref);
        Assert.False(original.NativeTerminal);
        Assert.Empty(captured.Confirmation.Context.PositionCloses());
        var slot = await fixture.HoldSendSlotAsync();
        var close = fixture.Center.CloseAsync(fixture.Account, row);
        NativePeer? peer = null;
        try
        {
            try
            {
                await Until(() => fixture.PendingRequests > 0);
                Assert.Single(captured.Confirmation.Context.PositionCloses());
                Assert.Empty(Tags(server));
                server.OrderReplayExclusions.Remove(original.BasketId);
                peer = await NativePeer.CreateAsync(fixture, server, captured);
                var working = Assert.Single(peer.Session.OrdersOf("ACC1"), order => order.BasketId == original.BasketId);
                Assert.False(working.Done);
                Assert.Equal(2m, working.Unfilled);
                Assert.Equal(0m, working.Filled);
                Assert.True(original.ReplayAbsent);
            }
            finally { slot.Release(); }

            Assert.NotNull(await close.WaitAsync(Deadline));
            Assert.Empty(Tags(server));
            Assert.Equal(0, Requests(server, "312"));
            Assert.Equal(0, Requests(server, "316"));
            Assert.Equal(2, server.Position);
            Assert.Equal(2m, fixture.Net);
        }
        finally
        {
            try { await close.WaitAsync(Deadline); }
            finally { if (peer != null) await peer.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task Captured_finality_actual_native_terminal_evidence_still_resolves_stale_peer_work()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("native-terminal-stop", "external-native-terminal", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        await using var peer = await NativePeer.CreateAsync(fixture, server, target);
        var terminal = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "native-terminal-stop");
        var stale = Assert.Single(peer.Session.OrdersOf("ACC1"), order => order.BasketId == terminal.BasketId);
        await fixture.Session.CancelAsync("ACC1", terminal.BasketId, CancellationToken.None).WaitAsync(Deadline);
        await Until(() => terminal.Done && terminal.Cancelled && terminal.NativeTerminal);
        Assert.False(terminal.ReplayAbsent);
        Assert.False(stale.Done);
        var row = fixture.Position();

        Assert.Null(await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline));
        await Until(() => fixture.Net == 0 && fixture.Session.OrdersOf("ACC1").Any(order => order.PriceType == 2 && order.Done));
        Assert.Equal(1, Requests(server, "316"));
        Assert.False(stale.Done);
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(0, server.Kicks);
    }

    [Fact]
    public async Task Captured_finality_single_session_completed_replay_absence_remains_usable()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("single-session-absent-stop", "external-single-session-absence", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var absent = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "single-session-absent-stop");
        await server.RemoveOrderWhileDisconnected(absent.BasketId).WaitAsync(Deadline);
        await Until(() => Requests(server, "320") >= 2 && fixture.Session.OrdersReadyFor("ACC1") && absent.ReplayAbsent);
        Assert.True(absent.Done);
        Assert.False(absent.NativeTerminal);
        Assert.True(absent.CumulativeQuantityKnown);
        Assert.Equal(0m, absent.Filled);
        var row = fixture.Position();

        Assert.Null(await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline));
        await Until(() => fixture.Net == 0 && fixture.Session.OrdersOf("ACC1").Any(order => order.PriceType == 2 && order.Done));
        Assert.Equal(0, Requests(server, "316"));
        await AssertReductionAsync(fixture, server, 2);
    }

    [Fact]
    public async Task Captured_finality_persisted_inferred_terminal_is_reconciled_by_a_fresh_native_owner()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("recovered-absent-stop", "external-recovered-absence", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var absent = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "recovered-absent-stop");
        server.OrderReplayExclusions.Add(absent.BasketId);
        await server.DisconnectOrderSession(0).WaitAsync(Deadline);
        await Until(() => Requests(server, "320") >= 2 && fixture.Session.OrdersReadyFor("ACC1") && absent.ReplayAbsent);
        var captured = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        var originalContext = captured.Confirmation.Context;
        var original = originalContext.BeginPositionClose(captured.Broker.Instrument.Key, captured.Confirmation.State);
        Assert.Null(original.Error);
        var persisted = originalContext.PositionCloseState(original);
        Assert.Equal(PositionClosePhase.Reconciling, persisted.Phase);
        var cancellation = Assert.Single(persisted.Cancellations);
        Assert.True(cancellation.Terminal);
        Assert.Equal(0m, cancellation.Baseline);
        Assert.Equal(0m, cancellation.Observed);
        Assert.False(absent.NativeTerminal);
        Assert.False(originalContext.Find(original.Id)!.DispatchUncertain);
        Assert.Empty(Tags(server));

        server.OrderReplayExclusions.Remove(absent.BasketId);
        await fixture.RestartAsync();
        var row = fixture.Position();
        var recovered = Assert.IsType<RithmicPositionTarget>(row.Ref);
        var context = recovered.Confirmation.Context;
        Assert.NotSame(originalContext, context);
        var ticket = Assert.Single(context.PositionCloses());
        Assert.Equal(original.Id, ticket.Id);
        Assert.Equal(original.ClientOrderId, ticket.ClientOrderId);
        Assert.Equal(PositionClosePhase.Reconciling, context.PositionCloseState(ticket).Phase);
        Assert.True(Assert.Single(context.PositionCloseState(ticket).Cancellations).Terminal);
        Assert.False(context.Find(ticket.Id)!.DispatchUncertain);
        var working = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == absent.BasketId);
        Assert.False(working.Done);
        Assert.Equal(2m, working.Unfilled);
        Assert.Equal(0m, working.Filled);
        Assert.Empty(Tags(server));

        string? outcome = await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline);
        if (outcome == null)
        {
            await Until(() => fixture.Net == 0 && context.PositionCloseState(ticket).Phase == PositionClosePhase.Completed);
            Assert.True(working.Cancelled, "A recovered inferred terminal fact must not leave known working protection after success.");
            await AssertReductionAsync(fixture, server, 2);
            Assert.Equal(1, Requests(server, "316"));
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(outcome));
            Assert.Empty(Tags(server));
            Assert.Equal(0, Requests(server, "312"));
            Assert.Equal(2, server.Position);
            Assert.Equal(2m, fixture.Net);
            Assert.Contains(context.PositionCloseState(ticket).Phase,
                new[] { PositionClosePhase.NotSent, PositionClosePhase.Unresolved });
        }
        Assert.Equal(original.Id, Assert.Single(context.PositionCloses()).Id);
        await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline);
        Assert.Equal(original.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(original.ClientOrderId, context.Find(original.Id)!.ClientOrderId);
        Assert.Equal(outcome == null ? 1 : 0, Requests(server, "312"));
        Assert.Equal(0, server.Kicks);
    }

    [Fact]
    public async Task Captured_finality_a_public_close_claim_cannot_use_ordinary_broker_submit_to_bypass_reconciliation()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("ordinary-submit-absent-stop", "external-ordinary-submit-absence", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var absent = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "ordinary-submit-absent-stop");
        server.OrderReplayExclusions.Add(absent.BasketId);
        await server.DisconnectOrderSession(0).WaitAsync(Deadline);
        await Until(() => Requests(server, "320") >= 2 && fixture.Session.OrdersReadyFor("ACC1") && absent.ReplayAbsent);
        var captured = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        var context = captured.Confirmation.Context;
        var claim = context.BeginPositionClose(captured.Broker.Instrument.Key, captured.Confirmation.State);
        Assert.Null(claim.Error);
        Assert.Equal(PositionClosePhase.Reconciling, context.PositionCloseState(claim).Phase);
        Assert.True(Assert.Single(context.PositionCloseState(claim).Cancellations).Terminal);
        Assert.False(context.Find(claim.Id)!.DispatchUncertain);
        server.OrderReplayExclusions.Remove(absent.BasketId);
        await using var peer = await NativePeer.CreateAsync(fixture, server, captured);
        var working = Assert.Single(peer.Session.OrdersOf("ACC1"), order => order.BasketId == absent.BasketId);
        Assert.False(working.Done);
        Assert.Equal(2m, working.Unfilled);
        Assert.Empty(Tags(server));

        var submitted = captured.Broker.Submit(claim);
        await Until(() => submitted.Status == OrderStatus.Rejected || submitted.ExchangeOrderId.Length > 0);
        Assert.Equal(OrderStatus.Rejected, submitted.Status);
        Assert.Empty(submitted.ExchangeOrderId);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
        Assert.Equal(0, Requests(server, "316"));
        Assert.Equal(2, server.Position);
        Assert.Equal(2m, fixture.Net);
        Assert.Equal(PositionClosePhase.Reconciling, context.PositionCloseState(claim).Phase);
        Assert.False(context.Find(claim.Id)!.DispatchUncertain);
        Assert.Equal(claim.Id, Assert.Single(context.PositionCloses()).Id);
        captured.Broker.Submit(claim);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
        Assert.False(context.Find(claim.Id)!.DispatchUncertain);
        Assert.Equal(0, server.Kicks);

        var row = fixture.Position();
        Assert.Null(await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline));
        await Until(() => fixture.Net == 0 && context.PositionCloseState(claim).Phase == PositionClosePhase.Completed);
        Assert.True(working.Cancelled);
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(1, Requests(server, "316"));
        Assert.Equal(claim.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(claim.ClientOrderId, context.Find(claim.Id)!.ClientOrderId);
        await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline);
        Assert.Equal(claim.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(1, Requests(server, "312"));
    }

    [Fact]
    public async Task Captured_finality_an_unmatched_freshly_leased_target_keeps_the_original_close_unresolved()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("leased-unmatched-stop", "external-leased-unmatched", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        await fixture.Session.CancelAsync("ACC1", "leased-unmatched-stop", CancellationToken.None).WaitAsync(Deadline);
        await Until(() => fixture.Session.OrdersOf("ACC1").Any(order =>
            order.BasketId == "leased-unmatched-stop" && order.NativeTerminal && order.Cancelled));
        var historical = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        var historicalFacts = historical.Confirmation.State.Cancellations;
        Assert.True(Assert.Single(historicalFacts).Terminal);
        await fixture.RestartAsync();
        Assert.DoesNotContain(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "leased-unmatched-stop");
        var row = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(row.Ref);
        var context = captured.Confirmation.Context;
        Assert.Empty(captured.Confirmation.State.Cancellations);
        var claim = context.BeginPositionClose(captured.Broker.Instrument.Key, captured.Confirmation.State);
        Assert.Null(claim.Error);
        var writer = new OrderContext(context.Descriptor, fixture.ExecutionRoot);
        var writerTicket = Assert.Single(writer.PositionCloses());
        var writerState = writer.PositionCloseState(writerTicket);
        Assert.Empty(writerState.Cancellations);
        Assert.Equal(claim.Id, writerTicket.Id);
        var actualPermission = Assert.IsType<Func<OrderRequest, decimal, string?>>(
            NativeField<object>(context, "_nativePermission"));
        int injected = 0;
        SearchZoomFixture.Set(context, "_nativePermission", (Func<OrderRequest, decimal, string?>)((request, position) =>
        {
            string? denied = actualPermission(request, position);
            if (denied == null && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
                writer.UpdatePositionClose(writerTicket, writerState with { Cancellations = historicalFacts });
            return denied;
        }));
        string? outcome;
        try { outcome = await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline); }
        finally { SearchZoomFixture.Set(context, "_nativePermission", actualPermission); }

        Assert.Equal(1, Volatile.Read(ref injected));
        Assert.NotNull(outcome);
        var state = context.PositionCloseState(claim);
        Assert.Equal("leased-unmatched-stop", Assert.Single(state.Cancellations).Basket);
        Assert.Equal(PositionClosePhase.Unresolved, state.Phase);
        Assert.False(context.HasStorageFault);
        Assert.False(context.Find(claim.Id)!.DispatchUncertain);
        Assert.Equal(claim.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
        Assert.Equal(2, server.Position);
        Assert.NotNull(await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline));
        Assert.Equal(claim.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(PositionClosePhase.Unresolved, context.PositionCloseState(claim).Phase);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_finality_uncovered_current_work_at_the_consuming_lease_cannot_become_a_fresh_close(bool surveillanceOnly)
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(row.Ref);
        var context = captured.Confirmation.Context;
        var observer = new OrderContext(context.Descriptor, fixture.ExecutionRoot);
        var actualPermission = Assert.IsType<Func<OrderRequest, decimal, string?>>(
            NativeField<object>(context, "_nativePermission"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0, timedOut = 0;
        SearchZoomFixture.Set(context, "_nativePermission", (Func<OrderRequest, decimal, string?>)((request, position) =>
        {
            string? denied = actualPermission(request, position);
            if (denied == null && Interlocked.CompareExchange(ref held, 1, 0) == 0)
            {
                entered.TrySetResult();
                if (!release.Wait(Deadline))
                {
                    Interlocked.Exchange(ref timedOut, 1);
                    throw new TimeoutException("The captured finality permission barrier was not released.");
                }
            }
            return denied;
        }));
        var close = Task.Run(() => fixture.Center.CloseAsync(fixture.Account, row));
        string originalId;
        try
        {
            try
            {
                await entered.Task.WaitAsync(Deadline);
                var claim = Assert.Single(observer.PositionCloses());
                originalId = claim.Id;
                Assert.Empty(observer.PositionCloseState(claim).Cancellations);
                await server.PushOrder("uncovered-current-stop", "external-uncovered-current", false, 2, 95, type: 4);
                await Until(() => fixture.Session.OrdersOf("ACC1").Any(order =>
                    order.BasketId == "uncovered-current-stop" && !order.Done && order.Unfilled == 2));
                Assert.Empty(observer.PositionCloseState(claim).Cancellations);
                Assert.Empty(observer.PositionCloseState(claim).Surveillance ?? Array.Empty<PositionCloseObservation>());
                Assert.Empty(Tags(server));
                if (surveillanceOnly)
                {
                    var native = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "uncovered-current-stop");
                    Assert.True(native.CumulativeQuantityKnown);
                    observer.UpdatePositionClose(claim, observer.PositionCloseState(claim) with
                    {
                        Surveillance = new[] { new PositionCloseObservation(native.BasketId, native.IsBuy, null, native.Filled, native.Done) },
                    });
                    Assert.False(Assert.Single(observer.PositionCloseState(claim).Surveillance!).Terminal);
                }
            }
            finally { release.Set(); }

            Assert.NotNull(await close.WaitAsync(Deadline));
        }
        finally
        {
            release.Set();
            try { await close.WaitAsync(Deadline); }
            finally { SearchZoomFixture.Set(context, "_nativePermission", actualPermission); }
        }
        Assert.Equal(0, Volatile.Read(ref timedOut));
        Assert.Equal(1, Volatile.Read(ref held));
        var original = Assert.Single(context.PositionCloses());
        Assert.Equal(originalId, original.Id);
        Assert.Equal(PositionClosePhase.Unresolved, context.PositionCloseState(original).Phase);
        Assert.False(context.HasStorageFault);
        Assert.False(context.Find(original.Id)!.DispatchUncertain);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
        Assert.Equal(0, Requests(server, "316"));
        Assert.Equal(2, server.Position);
        Assert.NotNull(await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline));
        Assert.Equal(originalId, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(PositionClosePhase.Unresolved, context.PositionCloseState(original).Phase);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
    }

    [Fact]
    public async Task Captured_finality_a_newer_current_cumulative_counter_cannot_pass_the_older_leased_observation()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("newer-counter-stop", "external-newer-counter", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(row.Ref);
        var context = captured.Confirmation.Context;
        var observer = new OrderContext(context.Descriptor, fixture.ExecutionRoot);
        var actualPermission = Assert.IsType<Func<OrderRequest, decimal, string?>>(
            NativeField<object>(context, "_nativePermission"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0, timedOut = 0;
        SearchZoomFixture.Set(context, "_nativePermission", (Func<OrderRequest, decimal, string?>)((request, position) =>
        {
            string? denied = actualPermission(request, position);
            if (denied == null && Interlocked.CompareExchange(ref held, 1, 0) == 0)
            {
                entered.TrySetResult();
                if (!release.Wait(Deadline))
                {
                    Interlocked.Exchange(ref timedOut, 1);
                    throw new TimeoutException("The captured cumulative permission barrier was not released.");
                }
            }
            return denied;
        }));
        var close = Task.Run(() => fixture.Center.CloseAsync(fixture.Account, row));
        string originalId;
        try
        {
            try
            {
                await entered.Task.WaitAsync(Deadline);
                var claim = Assert.Single(observer.PositionCloses());
                originalId = claim.Id;
                var prior = Assert.Single(observer.PositionCloseState(claim).Cancellations);
                Assert.Equal("newer-counter-stop", prior.Basket);
                Assert.Equal(0m, prior.Observed);
                Assert.True(prior.Terminal);
                await server.FillCanceledOrder(prior.Basket, 1, 95).WaitAsync(Deadline);
                await Until(() => fixture.Session.OrdersOf("ACC1").Any(order => order.BasketId == prior.Basket && order.Filled == 1));
                Assert.Equal(0m, Assert.Single(observer.PositionCloseState(claim).Cancellations).Observed);
                Assert.Empty(Tags(server));
            }
            finally { release.Set(); }
            Assert.NotNull(await close.WaitAsync(Deadline));
        }
        finally
        {
            release.Set();
            try { await close.WaitAsync(Deadline); }
            finally { SearchZoomFixture.Set(context, "_nativePermission", actualPermission); }
        }
        Assert.Equal(0, Volatile.Read(ref timedOut));
        Assert.Equal(1, Volatile.Read(ref held));
        var original = Assert.Single(context.PositionCloses());
        Assert.Equal(originalId, original.Id);
        Assert.Equal(PositionClosePhase.Unresolved, context.PositionCloseState(original).Phase);
        Assert.Equal(1m, context.PositionCloseState(original).KnownReductions);
        Assert.False(context.HasStorageFault);
        Assert.False(context.Find(original.Id)!.DispatchUncertain);
        Assert.Empty(Tags(server));
        Assert.Equal(0, Requests(server, "312"));
        Assert.Equal(1, server.Position);
        Assert.NotNull(await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline));
        Assert.Equal(originalId, Assert.Single(context.PositionCloses()).Id);
        Assert.Empty(Tags(server));
    }

    [Fact]
    public async Task Captured_finality_a_real_stored_false_fact_can_use_current_native_nonworking_admission()
    {
        var fillRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, MarketFillRelease = fillRelease };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        string basket = await fixture.Session.PlaceAsync("ACC1", "ESZ6", "CME", true, 2, 1, 0, 0,
            "external-stored-false-entry", CancellationToken.None).WaitAsync(Deadline);
        await Until(() => fixture.Session.OrdersOf("ACC1").Any(order =>
            order.BasketId == basket && order.CumulativeQuantityKnown && !order.Done));
        var historical = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        var historicalFact = Assert.Single(historical.Confirmation.State.Cancellations);
        Assert.False(historicalFact.Terminal);
        Assert.True(historicalFact.Buy);
        fillRelease.TrySetResult();
        server.MarketFillRelease = null;
        await Until(() => fixture.Net == 3 && fixture.Session.OrdersOf("ACC1").Any(order =>
            order.BasketId == basket && order.NativeTerminal && order.Filled == 1));
        var native = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == basket);
        var row = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(row.Ref);
        Assert.Empty(captured.Confirmation.State.Cancellations);
        var context = captured.Confirmation.Context;
        var claim = context.BeginPositionClose(captured.Broker.Instrument.Key, captured.Confirmation.State);
        Assert.Null(claim.Error);
        var writer = new OrderContext(context.Descriptor, fixture.ExecutionRoot);
        var writerTicket = Assert.Single(writer.PositionCloses());
        var writerState = writer.PositionCloseState(writerTicket);
        var counterFact = historicalFact with { Observed = native.Filled };
        Assert.False(counterFact.Terminal);
        var actualPermission = Assert.IsType<Func<OrderRequest, decimal, string?>>(
            NativeField<object>(context, "_nativePermission"));
        int injected = 0;
        SearchZoomFixture.Set(context, "_nativePermission", (Func<OrderRequest, decimal, string?>)((request, position) =>
        {
            string? denied = actualPermission(request, position);
            if (denied == null && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                writer.UpdatePositionClose(writerTicket, writerState with { Cancellations = new[] { counterFact } });
                Assert.False(Assert.Single(writer.PositionCloseState(writerTicket).Cancellations).Terminal);
            }
            return denied;
        }));
        try { Assert.Null(await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline)); }
        finally
        {
            SearchZoomFixture.Set(context, "_nativePermission", actualPermission);
            fillRelease.TrySetResult();
        }
        Assert.Equal(1, Volatile.Read(ref injected));
        await Until(() => fixture.Net == 0 && context.PositionCloseState(claim).Phase == PositionClosePhase.Completed);
        Assert.Equal(claim.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(0m, context.PositionCloseState(claim).KnownReductions);
        Assert.False(context.HasStorageFault);
        var sent = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.UserTag == claim.ClientOrderId);
        Assert.False(sent.IsBuy);
        Assert.Equal(3m, sent.Quantity);
        Assert.Equal(3m, sent.Filled);
        Assert.Equal(2, Requests(server, "312"));
        Assert.Equal(0, Requests(server, "316"));
        Assert.Equal(0, server.Position);
        await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline);
        Assert.Equal(claim.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Equal(2, Requests(server, "312"));
    }
}
