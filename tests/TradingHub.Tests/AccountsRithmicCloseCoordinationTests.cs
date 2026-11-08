using System.Text.Json;
using TradingHub.App.Services;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed partial class AccountsRithmicCloseRegressionTests
{
    [Fact]
    public async Task Confirmed_row_keeps_its_ceiling_and_original_route_after_refresh_and_reconfiguration()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        using var replacement = new FakeRithmicServer();
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var confirmed = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(confirmed.Ref);
        string generation = captured.Confirmation.Context.Descriptor.Generation;
        server.SeedPosition(true, 3, 100);
        await server.PublishPosition();
        await Until(() => fixture.Net == 5);
        Assert.Equal(5m, fixture.Position().Net);
        fixture.RetireOriginalSession();
        fixture.Profile.RithmicUri = replacement.Uri;
        fixture.Profile.RithmicSystem = "Rithmic Test";
        fixture.Services.Connections.EnsureTradingIdentity(fixture.Profile);

        await fixture.Center.CloseAsync(fixture.Account, confirmed).WaitAsync(Deadline);
        await Until(() => fixture.Net == 3);
        await fixture.Center.CloseAsync(fixture.Account, confirmed).WaitAsync(Deadline);

        string tag = Assert.Single(Tags(server));
        var sent = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.UserTag == tag);
        Assert.Equal(2m, sent.Quantity);
        Assert.False(sent.IsBuy);
        Assert.Equal(3, server.Position);
        var intent = Assert.Single(captured.Confirmation.Context.PositionCloses());
        var saved = captured.Confirmation.Context.Find(intent.Id)!;
        Assert.Equal(generation, saved.Generation);
        Assert.Equal(2m, saved.PositionClose!.ConfirmedNet);
        Assert.Equal(2m, saved.DispatchQuantity);
        Assert.Equal(PositionClosePhase.Unresolved, saved.PositionClose.Phase);
        Assert.Empty(Tags(replacement));
        Assert.Equal(0, Requests(replacement, "10"));
    }

    [Fact]
    public async Task Chart_strategy_and_protective_exits_cannot_spend_the_Accounts_close_reservation()
    {
        var cancellationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, CancelNotificationRelease = cancellationRelease };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("reserved-stop", "external-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var chart = fixture.Chart("ES");
        var strategy = fixture.Chart("ESZ6");
        await Until(() => chart.PositionReady && chart.OrdersReady && strategy.PositionReady && strategy.OrdersReady);
        var owner = strategy.OrderContext.LedgerOwner("close-competition", Guid.NewGuid());
        var close = fixture.Center.CloseAsync(fixture.Account, row);
        try
        {
            await server.CancelResponseSent.Task.WaitAsync(Deadline);
            OrderActions.Flatten(chart);
            var strategyExit = OrderActions.Place(strategy, owner, OrderTypes.Market, OrderDirections.Sell, 2, reduceOnly: true);
            var bracketExit = OrderActions.Place(chart, owner, OrderTypes.Stop, OrderDirections.Sell, 2, 95, reduceOnly: true, protective: true);
            await Until(() => strategyExit.Status == OrderStatus.Rejected && bracketExit.Status == OrderStatus.Rejected);
            var barrier = await Record.ExceptionAsync(() => fixture.Session.CancelAsync("ACC1", "missing-ack-only-barrier", CancellationToken.None));
            Assert.NotNull(barrier);
            Assert.False(fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "reserved-stop").Done);
            Assert.Empty(Tags(server));
            Assert.Equal(0, Requests(server, "312"));
            Assert.False(close.IsCompleted);
        }
        finally { cancellationRelease.TrySetResult(); }
        await close.WaitAsync(Deadline);
        await Until(() => fixture.Net == 0 && fixture.Session.OrdersOf("ACC1").Any(order => order.PriceType == 2 && order.Done));
        await AssertReductionAsync(fixture, server, 2);
    }

    [Fact]
    public async Task The_actual_send_slot_rechecks_flat_then_reopened_position_and_requires_new_confirmation()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var slot = await fixture.HoldSendSlotAsync();
        var close = fixture.Center.CloseAsync(fixture.Account, row);
        try
        {
            await Until(() => fixture.PendingRequests > 0);
            server.SeedPosition(false, 2, 100);
            await server.PublishPosition();
            await Until(() => fixture.Net == 0);
            server.SeedPosition(true, 2, 100);
            await server.PublishPosition();
            await Until(() => fixture.Net == 2);
        }
        finally { slot.Release(); }
        Assert.Contains("flat or changed side", await close.WaitAsync(Deadline));
        Assert.Empty(Tags(server));
        await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline);
        await Until(() => fixture.Net == 0);
        await AssertReductionAsync(fixture, server, 2);
    }

    [Fact]
    public async Task Cancellation_and_late_terminal_fill_increments_consume_the_ceiling_before_actual_send()
    {
        var cancellationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, CancelNotificationRelease = cancellationRelease };
        server.SeedPosition(true, 5, 100);
        await server.PushOrder("late-stop", "external-stop", false, 5, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var target = Assert.IsType<RithmicPositionTarget>(row.Ref);
        server.PositionUpdateRelease = positionRelease;
        var close = fixture.Center.CloseAsync(fixture.Account, row);
        Task? firstFill = null, lateFill = null;
        SemaphoreSlim? slot = null;
        try
        {
            await server.CancelResponseSent.Task.WaitAsync(Deadline);
            await Until(() => fixture.PendingRequests == 0);
            firstFill = server.FillCanceledOrder("late-stop", 2, 95);
            await Until(() => fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "late-stop").Filled == 2);
            slot = await fixture.HoldSendSlotAsync();
            cancellationRelease.TrySetResult();
            await Until(() => fixture.PendingRequests > 0);
            lateFill = server.FillCanceledOrder("late-stop", 1, 95);
            await Until(() => fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "late-stop").Filled == 3);
            Assert.Equal(5m, fixture.Net);
            slot.Release();
            slot = null;
            await close.WaitAsync(Deadline);
            await Until(() => server.Position == 0);
        }
        finally
        {
            slot?.Release();
            cancellationRelease.TrySetResult();
            positionRelease.TrySetResult();
            if (firstFill != null) await firstFill.WaitAsync(Deadline);
            if (lateFill != null) await lateFill.WaitAsync(Deadline);
        }
        await Until(() => fixture.Net == 0);
        await AssertReductionAsync(fixture, server, 2);
        var ticket = Assert.Single(target.Confirmation.Context.PositionCloses());
        var saved = target.Confirmation.Context.Find(ticket.Id)!;
        Assert.Equal(5m, saved.Request.Quantity);
        Assert.Equal(2m, saved.DispatchQuantity);
        Assert.Equal(3m, saved.PositionClose!.KnownReductions);
        Assert.Equal(3m, Assert.Single(saved.PositionClose.Cancellations).Observed);
        await server.PublishPosition();
        await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline);
        Assert.Single(Tags(server));
        Assert.Equal(3m, target.Confirmation.Context.PositionCloseState(ticket).KnownReductions);
    }

    [Fact]
    public async Task A_filled_market_with_a_lost_reply_keeps_its_durable_identity_across_restart_and_missing_working_replay()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, FillMarketBeforeLostReply = true, ReportedPositionOverride = 2 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var target = Assert.IsType<RithmicPositionTarget>(row.Ref);
        server.LoseNewOrderResponse = true;
        var first = fixture.Center.CloseAsync(fixture.Account, row);
        await server.UnacknowledgedOrderAccepted.Task.WaitAsync(Deadline);
        Assert.Equal(0, server.Position);
        server.LoseNewOrderResponse = false;
        Assert.NotNull(await first.WaitAsync(Deadline));
        var original = Assert.Single(target.Confirmation.Context.PositionCloses());
        string tag = original.ClientOrderId;
        var descriptor = target.Confirmation.Context.Descriptor;

        await fixture.RestartAsync();
        var reloaded = fixture.Position();
        Assert.Equal(2m, reloaded.Net);
        Assert.NotNull(await fixture.Center.CloseAsync(fixture.Account, reloaded).WaitAsync(Deadline));
        var restoredTarget = Assert.IsType<RithmicPositionTarget>(reloaded.Ref);
        var restored = Assert.Single(restoredTarget.Confirmation.Context.PositionCloses());
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(tag, restored.ClientOrderId);
        Assert.Single(Tags(server));
        Assert.Empty(fixture.Session.OrdersOf("ACC1"));
        var disk = new OrderContext(descriptor, fixture.ExecutionRoot);
        Assert.True(disk.Find(original.Id)!.DispatchUncertain);
        Assert.Null(disk.Find(original.Id)!.TerminalOrder);

        server.ReplayTerminalOrders = true;
        server.ReportedPositionOverride = null;
        await fixture.RestartAsync();
        var snapshot = fixture.Center.Snapshot(fixture.Account);
        var terminalTarget = Assert.IsType<RithmicPositionTarget>(Assert.Single(snapshot.Positions).Ref);
        await Until(() => terminalTarget.Confirmation.Context.Find(original.Id)?.TerminalOrder != null);
        var completed = terminalTarget.Confirmation.Context.Find(original.Id)!;
        Assert.Equal(PositionClosePhase.Completed, completed.PositionClose!.Phase);
        Assert.Equal(2m, completed.TerminalOrder!.CumulativeQuantity);
        Assert.Equal(tag, completed.ClientOrderId);
        Assert.Single(Tags(server));
    }

    [Fact]
    public async Task Another_ownership_namespace_cannot_reserve_the_same_native_close_capacity()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, CancelNotificationRelease = release };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("shared-native-stop", "external-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var target = Assert.IsType<RithmicPositionTarget>(row.Ref);
        var descriptor = target.Confirmation.Context.Descriptor with { Id = Guid.NewGuid().ToString("N"),
            LocalNamespace = Guid.NewGuid().ToString("N"), Generation = Guid.NewGuid().ToString("N") };
        var otherContext = new OrderContext(descriptor, fixture.ExecutionRoot);
        using var other = new RithmicBroker(target.Broker.Instrument, fixture.Session, "ACC1", "Separate namespace",
            "ESZ6", "CME", otherContext);
        var close = fixture.Center.CloseAsync(fixture.Account, row);
        try
        {
            await server.CancelResponseSent.Task.WaitAsync(Deadline);
            Assert.NotNull(await other.ClosePositionAsync(other.CapturePositionClose(2)).WaitAsync(Deadline));
            var exit = OrderActions.Manual(other, OrderTypes.Market, OrderDirections.Sell, 2, reduceOnly: true);
            await Until(() => exit.Status == OrderStatus.Rejected);
            Assert.Empty(Tags(server));
            Assert.Empty(otherContext.PositionCloses());
        }
        finally { release.TrySetResult(); }
        await close.WaitAsync(Deadline);
        await Until(() => fixture.Net == 0);
        await AssertReductionAsync(fixture, server, 2);
    }

    [Fact]
    public async Task An_unrelated_contract_retains_ordinary_submission_cancellation_and_accepted_price_maintenance()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, CancelNotificationRelease = release };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("es-protection", "external-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        fixture.Profile.AllowRealOrders = true;
        var nq = fixture.Chart("NQZ6", "NQZ6");
        await server.PublishPosition("NQZ6", 0);
        await Until(() => nq.PositionReady && nq.OrdersReady);
        var order = OrderActions.Manual(nq, OrderTypes.Limit, OrderDirections.Buy, 1, 200);
        await Until(() => order.ExchangeOrderId.Length > 0);
        var close = fixture.Center.CloseAsync(fixture.Account, row);
        try
        {
            await server.CancelResponseSent.Task.WaitAsync(Deadline);
            OrderActions.Reprice(nq, order, 199);
            await Until(() => server.ModifyRequests == 1 && !((ITrackedOrderReplacement)nq).IsReplacementPending(order));
            Assert.Equal(199m, order.Price);
            Assert.Equal(1m, order.Unfilled);
            Assert.Same(order, Assert.Single(nq.ActiveOrders));
            nq.Cancel(order);
            await Until(() => Requests(server, "316") == 2);
        }
        finally { release.TrySetResult(); }
        await close.WaitAsync(Deadline);
        await Until(() => fixture.Net == 0 && order.Canceled);
        Assert.Equal(2, Tags(server).Length);
        Assert.Equal(0, server.Position);
        Assert.Equal(2m, Assert.Single(fixture.Session.OrdersFor("ACC1", "ESZ6", "CME"),
            native => native.PriceType == 2).Quantity);
        Assert.True(Assert.Single(fixture.Session.OrdersFor("ACC1", "NQZ6", "CME")).Cancelled);
    }

    [Fact]
    public async Task A_saved_cancel_row_uses_its_original_session_after_profile_reconfiguration()
    {
        using var server = new FakeRithmicServer();
        using var replacement = new FakeRithmicServer();
        await server.PushOrder("saved-order", "external-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = Assert.Single(fixture.Center.Snapshot(fixture.Account).Orders);
        fixture.RetireOriginalSession();
        fixture.Profile.RithmicUri = replacement.Uri;
        fixture.Profile.RithmicSystem = "Rithmic Test";
        fixture.Services.Connections.EnsureTradingIdentity(fixture.Profile);
        await fixture.Center.CancelAsync(fixture.Account, row).WaitAsync(Deadline);
        await Until(() => fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == row.Key).Cancelled);
        Assert.Equal(1, Requests(server, "316"));
        Assert.Equal(0, Requests(replacement, "10"));
        Assert.Equal(0, Requests(replacement, "316"));
    }

    [Fact]
    public async Task An_unresolved_prior_configuration_is_reported_unavailable_instead_of_using_the_current_route()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, FillMarketBeforeLostReply = true, ReportedPositionOverride = 2 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var originalRow = fixture.Position();
        server.LoseNewOrderResponse = true;
        var close = fixture.Center.CloseAsync(fixture.Account, originalRow);
        await server.UnacknowledgedOrderAccepted.Task.WaitAsync(Deadline);
        server.LoseNewOrderResponse = false;
        await close.WaitAsync(Deadline);
        fixture.Profile.TradingGeneration = Guid.NewGuid().ToString("N");
        fixture.Services.Connections.EnsureTradingIdentity(fixture.Profile);
        await fixture.RestartAsync();
        var snapshot = fixture.Center.Snapshot(fixture.Account);
        Assert.Contains("original Rithmic close session is unavailable", snapshot.Status);
        Assert.Contains("unavailable", await fixture.Center.CloseAsync(fixture.Account, Assert.Single(snapshot.Positions)).WaitAsync(Deadline));
        Assert.Contains("unavailable", await fixture.Center.CloseAsync(fixture.Account, originalRow).WaitAsync(Deadline));
        Assert.Single(Tags(server));
        Assert.Equal(0, server.Position);
    }

    [Fact]
    public async Task A_venue_fill_delivered_after_completed_close_remains_explicitly_unresolved_without_an_automatic_resend()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("post-close-late-stop", "external-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var target = Assert.IsType<RithmicPositionTarget>(row.Ref);
        await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline);
        await Until(() => fixture.Net == 0);
        var ticket = Assert.Single(target.Confirmation.Context.PositionCloses());
        await Until(() => target.Confirmation.Context.PositionCloseState(ticket).Phase == PositionClosePhase.Completed);
        await server.FillCanceledOrder("post-close-late-stop", 1, 95);
        await Until(() => fixture.Net == -1);
        await Until(() => fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "post-close-late-stop").Filled == 1);
        await server.PublishPosition();
        var latest = fixture.Position();
        Assert.NotNull(await fixture.Center.CloseAsync(fixture.Account, latest).WaitAsync(Deadline));
        var state = target.Confirmation.Context.PositionCloseState(ticket);
        Assert.Equal(PositionClosePhase.Unresolved, state.Phase);
        Assert.True(state.LateFillUnresolved);
        Assert.Equal(1m, state.KnownReductions);
        Assert.Single(Tags(server));
        Assert.Equal(-1, server.Position);
        var emergency = OrderActions.Manual(target.Broker, OrderTypes.Market, OrderDirections.Buy, 1, reduceOnly: true);
        await Until(() => fixture.Net == 0 && emergency.Filled == 1);
        Assert.Equal(2, Tags(server).Length);
        Assert.Equal(PositionClosePhase.Unresolved, target.Confirmation.Context.PositionCloseState(ticket).Phase);
    }

    [Fact]
    public async Task Missing_native_cumulative_quantity_remains_unavailable_instead_of_becoming_zero_evidence()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        await server.PushOrder("unknown-counter-stop", "external-stop", false, 2, 95, type: 4, cumulativeKnown: false);
        await Until(() => fixture.Session.OrdersOf("ACC1").Any(order => order.BasketId == "unknown-counter-stop"));
        var row = fixture.Position();
        var target = Assert.IsType<RithmicPositionTarget>(row.Ref);
        Assert.Contains("cumulative quantity is unavailable", await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline));
        var ticket = Assert.Single(target.Confirmation.Context.PositionCloses());
        var state = target.Confirmation.Context.PositionCloseState(ticket);
        Assert.Equal(PositionClosePhase.Unresolved, state.Phase);
        var cancellation = Assert.Single(state.Cancellations);
        Assert.True(cancellation.Terminal);
        Assert.Null(cancellation.Baseline);
        Assert.Null(cancellation.Observed);
        await server.PublishPosition();
        Assert.NotNull(await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline));
        Assert.Equal(ticket.Id, Assert.Single(target.Confirmation.Context.PositionCloses()).Id);
        Assert.Empty(Tags(server));
        Assert.Equal(2, server.Position);
    }

    [Fact]
    public async Task Accepted_protection_targeted_for_cancellation_cannot_be_repriced_even_when_other_capacity_remains()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, CancelNotificationRelease = release };
        server.SeedPosition(true, 5, 100);
        await server.PushOrder("cancelled-price-target", "external-stop", false, 1, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        var chart = fixture.Chart("ES");
        await Until(() => chart.PositionReady && chart.OrdersReady && chart.ActiveOrders.Any());
        var protection = Assert.Single(chart.ActiveOrders);
        var confirmation = target.Broker.CapturePositionClose(2);
        var close = target.Broker.ClosePositionAsync(confirmation);
        try
        {
            await server.CancelResponseSent.Task.WaitAsync(Deadline);
            OrderActions.Reprice(chart, protection, 96);
            await Until(() => chart.LastError?.Contains("superseded this amendment", StringComparison.Ordinal) == true);
            Assert.Equal(0, server.ModifyRequests);
            Assert.Equal(95m, fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "cancelled-price-target").Trigger);
        }
        finally { release.TrySetResult(); }
        string? closeResult;
        try { closeResult = await close.WaitAsync(Deadline); }
        catch (TimeoutException error)
        {
            string evidence;
            try
            {
                bool captured = close.IsCompletedSuccessfully;
                string? outcome = captured ? await close : null;
                using var native = JsonDocument.Parse(CloseDiagnostics(fixture, server, confirmation, outcome));
                evidence = JsonSerializer.Serialize(new
                {
                    CloseResultCaptured = captured, CloseResult = outcome,
                    CloseTaskStatus = close.Status.ToString(), Native = native.RootElement,
                });
            }
            catch (Exception diagnosticError)
            {
                throw new AggregateException("Native close timed out and evidence capture failed.", error, diagnosticError);
            }
            throw new TimeoutException(evidence, error);
        }
        Assert.True(closeResult == null, closeResult == null ? null : CloseDiagnostics(fixture, server, confirmation, closeResult));
        await Until(() => fixture.Net == 3, () => CloseDiagnostics(fixture, server, confirmation, closeResult));
        Assert.Single(Tags(server));
        Assert.Equal(2m, Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.PriceType == 2).Quantity);
    }

    [Fact]
    public async Task Completed_close_does_not_expand_its_claim_to_a_new_position_and_a_fresh_confirmation_can_close_again()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var oldRow = fixture.Position();
        var target = Assert.IsType<RithmicPositionTarget>(oldRow.Ref);
        await fixture.Center.CloseAsync(fixture.Account, oldRow).WaitAsync(Deadline);
        await Until(() => fixture.Net == 0);
        var original = Assert.Single(target.Confirmation.Context.PositionCloses());
        await Until(() => target.Confirmation.Context.PositionCloseState(original).Phase == PositionClosePhase.Completed);
        fixture.Profile.AllowRealOrders = true;
        fixture.Profile.MaxOrderQuantity = 2;
        var chart = fixture.Chart("ES");
        await Until(() => chart.PositionReady && chart.OrdersReady);
        var opening = OrderActions.Manual(chart, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => fixture.Net == 2 && opening.Filled == 2);
        Assert.Equal(PositionClosePhase.Completed, target.Confirmation.Context.PositionCloseState(original).Phase);
        Assert.NotNull(await fixture.Center.CloseAsync(fixture.Account, oldRow).WaitAsync(Deadline));
        Assert.Equal(2, Tags(server).Length);
        await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline);
        await Until(() => fixture.Net == 0);
        Assert.Equal(3, Tags(server).Length);
        var current = Assert.Single(target.Confirmation.Context.PositionCloses());
        Assert.NotEqual(original.Id, current.Id);
        Assert.Equal(original.Id, target.Confirmation.Context.PositionCloseState(current).PreviousIntent);
        Assert.Equal(0m, target.Confirmation.Context.PositionCloseState(original).KnownReductions);
    }

    [Fact]
    public async Task Stop_waits_for_an_admitted_native_fill_callback_to_finish_its_real_account_journal_write()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("stop-drain-basket", "external-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline);
        await Until(() => fixture.Net == 0);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int timedOut = 0;
        void PersistNativeFill(RithmicFill fill)
        {
            if (fill.BasketId != "stop-drain-basket") return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) Interlocked.Exchange(ref timedOut, 1);
            fixture.Services.Journal.Add(fixture.Account.Id, new AccountFill(fill.Id, fill.Time, fill.Symbol, fill.IsBuy,
                fill.Quantity, fill.Price, 50, 0, fill.BasketId));
            persisted.TrySetResult();
        }
        fixture.Session.FillReceived += PersistNativeFill;
        Task? fillWork = null, stop = null;
        try
        {
            fillWork = server.FillCanceledOrder("stop-drain-basket", 1, 95);
            await entered.Task.WaitAsync(Deadline);
            await fillWork.WaitAsync(Deadline);
            await Until(() => fixture.Net == -1);
            stop = fixture.Session.StopAsync();
            Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(250)));
        }
        finally
        {
            release.Set();
            if (fillWork != null) await fillWork.WaitAsync(Deadline);
            if (stop != null) await stop.WaitAsync(Deadline);
            if (entered.Task.IsCompletedSuccessfully) await persisted.Task.WaitAsync(Deadline);
            fixture.Session.FillReceived -= PersistNativeFill;
        }
        Assert.Equal(0, Volatile.Read(ref timedOut));
        Assert.True(persisted.Task.IsCompletedSuccessfully);
        Assert.NotNull(stop);
        Assert.True(stop.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    public async Task Invalid_native_close_ceiling_is_rejected_without_transport_and_a_later_valid_close_still_works(string invalid)
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var target = Assert.IsType<RithmicPositionTarget>(row.Ref);
        decimal quantity = decimal.Parse(invalid, System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(await target.Broker.ClosePositionAsync(target.Broker.CapturePositionClose(quantity)).WaitAsync(Deadline));
        Assert.Empty(Tags(server));
        await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline);
        await Until(() => fixture.Net == 0);
        await AssertReductionAsync(fixture, server, 2);
    }
}
