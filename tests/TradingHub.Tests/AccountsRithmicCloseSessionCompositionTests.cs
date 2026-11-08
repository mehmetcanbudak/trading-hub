using System.Reflection;
using TradingHub.App.Services;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed partial class AccountsRithmicCloseRegressionTests
{
    [Fact]
    public async Task Shared_replay_absence_cannot_hide_a_peer_working_stop_from_close_cancellation()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("peer-replay-stop", "external-peer-replay", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        await using var peer = await NativePeer.CreateAsync(fixture, server, target);
        var original = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "peer-replay-stop");
        var working = Assert.Single(peer.Session.OrdersOf("ACC1"), order => order.BasketId == original.BasketId);
        server.OrderReplayExclusions.Add(original.BasketId);
        await server.DisconnectOrderSession(0).WaitAsync(Deadline);
        await Until(() => Requests(server, "320") >= 3 && fixture.Session.OrdersReadyFor("ACC1") && original.ReplayAbsent);
        Assert.True(original.Done);
        Assert.False(working.Done);
        Assert.Equal(2m, working.Unfilled);
        var confirmation = target.Broker.CapturePositionClose(2);
        await target.Broker.ClosePositionAsync(confirmation).WaitAsync(Deadline);
        await fixture.Session.CancelAsync("ACC1", "peer-replay-wire-barrier", CancellationToken.None).ContinueWith(_ => { }).WaitAsync(Deadline);
        await Until(() => fixture.Net == 0 && fixture.Session.OrdersOf("ACC1").Any(order => order.PriceType == 2 && order.Done));
        Assert.Equal(2, Requests(server, "316"));
        Assert.True(working.Cancelled);
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(0, server.Kicks);
    }

    [Fact]
    public async Task Shared_inferred_completion_cannot_hide_a_larger_peer_working_stop_from_close_cancellation()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true, ReportedPositionOverride = 5 };
        server.SeedPosition(true, 5, 100);
        await server.PushOrder("peer-amended-stop", "external-peer-amendment", false, 1, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        await using var peer = await NativePeer.CreateAsync(fixture, server, target);
        var stale = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "peer-amended-stop");
        var working = Assert.Single(peer.Session.OrdersOf("ACC1"), order => order.BasketId == stale.BasketId);
        await peer.Session.ModifyAsync("ACC1", working, 3, 96, CancellationToken.None).WaitAsync(Deadline);
        await Until(() => working.WorkingQuantity == 3 && working.PendingModifyQuantity == null);
        Assert.Equal(1m, stale.Quantity);
        await server.FillOrder(stale.BasketId, 1, 96, includeQuantity: false, allOrderSessions: true).WaitAsync(Deadline);
        await Until(() => fixture.Session.OrdersOf("ACC1").Any(order => order.BasketId == stale.BasketId && order.Filled == 1 && order.Done) &&
            peer.Session.OrdersOf("ACC1").Any(order => order.BasketId == working.BasketId && order.Filled == 1 && order.Unfilled == 2 && !order.Done));
        Assert.True(stale.Done);
        Assert.False(working.Done);
        Assert.Equal(2m, working.Unfilled);
        await target.Broker.ClosePositionAsync(target.Confirmation).WaitAsync(Deadline);
        await peer.Session.CancelAsync("ACC1", "peer-amendment-wire-barrier", CancellationToken.None).ContinueWith(_ => { }).WaitAsync(Deadline);
        Assert.Equal(2, Requests(server, "316"));
        Assert.True(working.Cancelled);
        string tag = Assert.Single(Tags(server));
        Assert.Equal(4m, Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.UserTag == tag).Quantity);
        Assert.Equal(0, server.Position);
    }

    [Fact]
    public async Task Shared_inferred_completion_keeps_peer_outstanding_risk_in_ordinary_reduction_admission()
    {
        var fillRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true,
            ReportedPositionOverride = 5, MarketFillRelease = fillRelease };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        var confirmation = target.Broker.CapturePositionClose(2);
        server.SeedPosition(true, 3, 100);
        await server.PublishPosition();
        await using var peer = await NativePeer.CreateAsync(fixture, server, target);
        try
        {
            await target.Broker.ClosePositionAsync(confirmation).WaitAsync(Deadline);
            var protection = OrderActions.Manual(peer.Broker, OrderTypes.Stop, OrderDirections.Sell, 1, 95, reduceOnly: true);
            await Until(() => protection.ExchangeOrderId.Length > 0);
            string basket = protection.ExchangeOrderId;
            string tag = Assert.IsType<string>(protection.ClientOrderId);
            await server.PushOrder(basket, tag, false, 1, 95, type: 4, orderSession: 0);
            await Until(() => fixture.Session.OrdersOf("ACC1").Any(order => order.BasketId == basket));
            OrderActions.Replace(peer.Broker, protection, 96, 3);
            await Until(() => protection.QuantityToFill == 3 && !peer.Broker.IsReplacementPending(protection));
            await server.FillOrder(basket, 1, 96, includeQuantity: false, allOrderSessions: true).WaitAsync(Deadline);
            await Until(() => fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == basket).Done &&
                peer.Session.OrdersOf("ACC1").Single(order => order.BasketId == basket).Unfilled == 2);
            var excess = OrderActions.Manual(peer.Broker, OrderTypes.Stop, OrderDirections.Sell, 1, 94, reduceOnly: true);
            await Until(() => excess.Status == OrderStatus.Rejected || excess.ExchangeOrderId.Length > 0);
            Assert.Equal(OrderStatus.Rejected, excess.Status);
            Assert.Equal(2, Tags(server).Length);
            Assert.False(peer.Session.OrdersOf("ACC1").Single(order => order.BasketId == basket).Done);
            Assert.Equal(4, server.Position);
        }
        finally { fillRelease.TrySetResult(); }
    }

    [Fact]
    public async Task Shared_stopped_peer_terminal_fill_remains_charged_to_an_outstanding_confirmation()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true, ReportedPositionOverride = 2 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        await using var peer = await NativePeer.CreateAsync(fixture, server, target);
        var reduction = OrderActions.Manual(peer.Broker, OrderTypes.Market, OrderDirections.Sell, 1, reduceOnly: true);
        await Until(() => reduction.Filled == 1 && reduction.Status == OrderStatus.Filled && server.Position == 1);
        Assert.DoesNotContain(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == reduction.ExchangeOrderId);
        Assert.Equal(1, server.Position);
        await peer.Session.StopAsync().WaitAsync(Deadline);
        await target.Broker.ClosePositionAsync(target.Confirmation).WaitAsync(Deadline);
        await Until(() => Tags(server).Length == 2 && fixture.Session.OrdersOf("ACC1").Any(order => order.PriceType == 2 && order.Done));
        string closeTag = Tags(server)[1];
        var sent = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.UserTag == closeTag);
        Assert.Equal(1m, sent.Quantity);
        Assert.Equal(0, server.Position);
        var ticket = Assert.Single(target.Confirmation.Context.PositionCloses());
        Assert.Equal(1m, target.Confirmation.Context.PositionCloseState(ticket).KnownReductions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_peer_admission_enforces_a_stopped_close_without_reopening_its_journal_lifetime(bool filled)
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        await using var peer = await NativePeer.CreateAsync(fixture, server, target);
        server.SeedPosition(true, 3, 100);
        await server.PublishPosition();
        await Until(() => fixture.Net == 5);
        await target.Broker.ClosePositionAsync(target.Confirmation).WaitAsync(Deadline);
        await Until(() => fixture.Net == 3);
        var context = target.Confirmation.Context;
        var ticket = Assert.Single(context.PositionCloses());
        await Until(() => context.PositionCloseState(ticket).Phase == PositionClosePhase.Unresolved);
        await fixture.Session.StopAsync().WaitAsync(Deadline);
        string path = Path.Combine(fixture.ExecutionRoot, "execution-state", OrderContext.Hash(context.Descriptor.Id), "intent", OrderContext.Hash(ticket.Id) + ".json");
        byte[] terminal = await File.ReadAllBytesAsync(path);
        server.ReportedPositionOverride = 3;
        await server.PushOrder("post-stop-peer-risk", "external-post-stop", false, 1, 95, type: 4);
        await Until(() => peer.Session.OrdersOf("ACC1").Any(order => order.BasketId == "post-stop-peer-risk"));
        if (filled)
        {
            await server.FillOrder("post-stop-peer-risk", 1, 95).WaitAsync(Deadline);
            await Until(() => peer.Session.OrdersOf("ACC1").Single(order => order.BasketId == "post-stop-peer-risk").Filled == 1);
            Assert.Equal(2, server.Position);
            Assert.Equal(3m, peer.Session.PositionFor("ACC1", "ESZ6", "CME")!.Net);
        }
        Assert.Equal(terminal, await File.ReadAllBytesAsync(path));
        var excess = OrderActions.Manual(peer.Broker, OrderTypes.Stop, OrderDirections.Sell, 1, 94, reduceOnly: true);
        await Until(() => excess.Status == OrderStatus.Rejected || excess.ExchangeOrderId.Length > 0);
        Assert.Equal(OrderStatus.Rejected, excess.Status);
        Assert.Single(Tags(server));
        Assert.Equal(terminal, await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain(context.PositionCloseState(ticket).Surveillance ?? Array.Empty<PositionCloseObservation>(),
            fact => fact.Basket == "post-stop-peer-risk");
    }

    [Fact]
    public async Task Shared_captured_confirmation_cannot_move_to_a_reconfigured_session_environment()
    {
        using var original = new FakeRithmicServer { MarketPrice = 100 };
        using var replacement = new FakeRithmicServer { MarketPrice = 200 };
        original.SeedPosition(true, 2, 100);
        replacement.SeedPosition(true, 2, 200);
        await using var fixture = new AccountsCloseFixture(original);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        fixture.Session.Reconfigure(new RithmicSettings { Uri = replacement.Uri, System = "Rithmic 01",
            User = "demo", Password = "demo", TimeoutMs = 5000 });
        await Until(() => Requests(replacement, "320") == 1 && fixture.Session.OrdersReadyFor("ACC1") &&
            fixture.Session.PositionReadyFor("ACC1", "ESZ6", "CME"));
        string? outcome = await target.Broker.ClosePositionAsync(target.Confirmation).WaitAsync(Deadline);
        Assert.NotNull(outcome);
        Assert.Empty(Tags(original));
        Assert.Empty(Tags(replacement));
        Assert.Equal(0, Requests(replacement, "316"));
        Assert.Equal(2, original.Position);
        Assert.Equal(2, replacement.Position);
        Assert.Empty(target.Confirmation.Context.PositionCloses());
    }

    [Fact]
    public async Task Shared_completed_close_admission_refreshes_a_known_late_fill_before_selecting_a_successor()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("admission-late-stop", "external-admission-late-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var target = Assert.IsType<RithmicPositionTarget>(row.Ref);
        await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline);
        await Until(() => fixture.Net == 0);
        var context = target.Confirmation.Context;
        var original = Assert.Single(context.PositionCloses());
        await Until(() => context.PositionCloseState(original).Phase == PositionClosePhase.Completed);
        await Assert.ThrowsAsync<RithmicTradingSession.RithmicRejectedException>(() =>
            fixture.Session.CancelAsync("ACC1", "admission-reader-response-barrier", CancellationToken.None));
        object admission = typeof(RithmicTradingSession).GetField("NativeAdmissionGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Task? fill = null;
        var close = Task.Run(() =>
        {
            lock (admission)
            {
                fill = server.FillCanceledOrder("admission-late-stop", 1, 95);
                DateTime deadline = DateTime.UtcNow + Deadline;
                while (DateTime.UtcNow < deadline && (fixture.Net != -1 ||
                    fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "admission-late-stop").Filled != 1))
                    Thread.Sleep(1);
                Assert.Equal(-1m, fixture.Net);
                Assert.Equal(1m, fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "admission-late-stop").Filled);
                return fixture.Center.CloseAsync(fixture.Account, fixture.Position());
            }
        });
        string? outcome;
        try { outcome = await close.WaitAsync(Deadline); }
        finally { if (fill != null) await fill.WaitAsync(Deadline); }
        Assert.NotNull(outcome);
        var state = context.PositionCloseState(original);
        Assert.Equal(PositionClosePhase.Unresolved, state.Phase);
        Assert.True(state.LateFillUnresolved);
        Assert.Equal(1m, state.KnownReductions);
        Assert.Equal(original.Id, Assert.Single(context.PositionCloses()).Id);
        Assert.Single(Tags(server));
        Assert.Equal(-1, server.Position);
        var emergency = OrderActions.Manual(target.Broker, OrderTypes.Market, OrderDirections.Buy, 1, reduceOnly: true);
        await Until(() => fixture.Net == 0 && emergency.Filled == 1);
        Assert.Equal(2, Tags(server).Length);
        Assert.Equal(PositionClosePhase.Unresolved, context.PositionCloseState(original).Phase);
    }
}
