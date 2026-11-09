namespace TradingHub.Tests;

public sealed partial class AccountsRithmicCloseRegressionTests
{
    // A stopped session no longer observes the venue, but stays registered while it knows a working order. When the
    // same native account reconnects, the new session's complete order replay is the authoritative working set.

    [Fact]
    public async Task Reconnect_close_is_admitted_after_a_stopped_session_stop_ended_at_the_venue()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("stopped-session-stop", "external-stopped-session", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var stopped = fixture.Session;
        var retained = Assert.Single(stopped.OrdersOf("ACC1"), order => order.BasketId == "stopped-session-stop");
        Assert.False(retained.Done);

        await fixture.RestartAsync(() =>
        {
            server.EndOrderWithoutSession(retained.BasketId);
            return Task.CompletedTask;
        });
        Assert.NotSame(stopped, fixture.Session);
        Assert.DoesNotContain(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == retained.BasketId);

        Assert.Null(await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline));
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(0, Requests(server, "316"));
        Assert.True(retained.Done);
        Assert.True(retained.ReplayAbsent);
        Assert.False(retained.NativeTerminal);
    }

    [Fact]
    public async Task Reconnect_close_is_bounded_by_the_fresh_position_when_a_stopped_session_stop_filled_at_the_venue()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("stopped-session-partial-stop", "external-stopped-partial", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var retained = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "stopped-session-partial-stop");

        await fixture.RestartAsync(() =>
        {
            server.FillWhileDisconnected(retained.BasketId, 1, 95);
            server.EndOrderWithoutSession(retained.BasketId);
            return Task.CompletedTask;
        });
        Assert.Equal(1, server.Position);
        var row = fixture.Position();
        Assert.Equal(1m, row.Net);

        Assert.Null(await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline));
        await AssertReductionAsync(fixture, server, 1);
        Assert.Equal(0, Requests(server, "316"));
        Assert.Equal(0m, retained.Filled);
        Assert.True(retained.Done);
    }

    [Fact]
    public async Task Reconnect_close_cancels_only_replayed_working_orders_after_a_stopped_session_stop_ended()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("stopped-session-ended-stop", "external-stopped-ended", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var retained = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "stopped-session-ended-stop");

        await fixture.RestartAsync(() =>
        {
            server.EndOrderWithoutSession(retained.BasketId);
            server.OpenOrderWithoutSession("replayed-live-stop", "external-replayed-live", false, 2, 94, type: 4);
            return Task.CompletedTask;
        });
        var live = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "replayed-live-stop");
        Assert.False(live.Done);

        Assert.Null(await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline));
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(1, Requests(server, "316"));
        Assert.True(live.Cancelled);
        Assert.True(live.NativeTerminal);
        Assert.True(retained.Done);
    }

    [Fact]
    public async Task Reconnect_close_still_cancels_a_stopped_session_stop_that_remains_working()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("stopped-session-working-stop", "external-stopped-working", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var retained = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "stopped-session-working-stop");

        await fixture.RestartAsync();
        var replayed = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == retained.BasketId);
        Assert.False(replayed.Done);
        Assert.False(retained.Done);

        Assert.Null(await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(Deadline));
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(1, Requests(server, "316"));
        Assert.True(replayed.Cancelled);
        Assert.True(replayed.NativeTerminal);
    }

    [Fact]
    public async Task Reconnect_close_still_refuses_while_a_stopped_session_market_dispatch_is_unresolved()
    {
        var fillRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, MarketFillRelease = fillRelease };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        string basket = await fixture.Session.PlaceAsync("ACC1", "ESZ6", "CME", false, 2, 1, 0, 0,
            "stopped-session-market", CancellationToken.None).WaitAsync(Deadline);
        var dispatched = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == basket);
        Assert.False(dispatched.Done);

        // The fake never fills the market order: its outcome stays unknown to every session.
        await fixture.RestartAsync();
        Assert.DoesNotContain(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == basket);
        Assert.Equal(2m, fixture.Net);

        // Without reconciliation the refusal only arrives at the bounded close's 15-second production timeout.
        string? outcome = await fixture.Center.CloseAsync(fixture.Account, fixture.Position()).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(string.IsNullOrWhiteSpace(outcome));
        Assert.Equal(new[] { "stopped-session-market" }, Tags(server));
        Assert.Equal(1, Requests(server, "312"));
        Assert.Equal(2, server.Position);
        Assert.Equal(2m, fixture.Net);
    }
}
