using TradingHub.Feeds.Tradovate;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>Accounts-page and quit regressions from the order-path review: the Rithmic cancel's "sent anyway" note and
/// the quit drain's deadline.</summary>
public sealed partial class AccountsRithmicCloseRegressionTests
{
    /// <summary>An Accounts-page cancel on Rithmic queued behind an earlier request whose reply the venue holds: it waits
    /// out the reducing bound, is sent anyway and returns the "sent anyway" note for the status bar, as on Tradovate.</summary>
    [Fact]
    public async Task Accounts_rithmic_cancel_sent_after_the_bounded_wait_returns_the_unsettled_note()
    {
        using var server = new FakeRithmicServer { SuppressModifyNotification = true };
        await server.PushOrder("review-working-limit", "external-limit", true, 1, 4990);
        await using var fixture = new AccountsCloseFixture(server);
        fixture.Session.SendTurnWait = TimeSpan.FromMilliseconds(100); // before the account's send-turn queue exists
        await fixture.StartAsync();
        var row = Assert.Single(fixture.Center.Snapshot(fixture.Account).Orders);
        var working = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == "review-working-limit");
        var modifyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ModifyResponseRelease = modifyRelease;
        // The earlier request: an amendment of that order, sent, whose reply is held, so its send turn stays taken.
        var amendment = Record.ExceptionAsync(() => fixture.Session.ModifyAsync("ACC1", working, 1, 4989, CancellationToken.None));
        string? note;
        try
        {
            await Until(() => server.ModifyRequests == 1);
            note = await fixture.Center.CancelAsync(fixture.Account, row).WaitAsync(Deadline);
            Assert.False(amendment.IsCompleted, "The earlier amendment's reply was released before the cancel returned.");
            Assert.Equal(1, Requests(server, "316"));
        }
        finally { modifyRelease.TrySetResult(); }

        Assert.Equal(NativeSendTurns.UnsettledMessage, note);
        Assert.Null(await amendment.WaitAsync(Deadline));
        await Until(() => fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == row.Key).Cancelled);
    }

    /// <summary>
    /// Quit's first native drain found nothing pending; a dialog then stayed open past the limit while quitting issued a
    /// close that still waits for its reply. The shutdown drain still waits for that close: the deadline starts with the
    /// first drain that has something to wait for.
    /// </summary>
    [Fact]
    public async Task A_quit_drain_with_nothing_pending_leaves_the_whole_limit_to_a_later_drain()
    {
        using var tradovate = new FakeTradovate();
        using var feed = new TradovateFeed(() => tradovate.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        using var rithmic = new FakeRithmicServer();
        await using var fixture = new AccountsCloseFixture(rithmic);
        Private<Dictionary<string, TradovateTradingSession>>(fixture.Services, "_tradovateSessions")["drain-tradovate"] = session;
        // A zero limit stands for one that elapsed while the dialog was open: a deadline this drain started is spent.
        fixture.Services.NativeSendDrainLimit = TimeSpan.Zero;
        await fixture.Services.DrainNativeSendsAsync().WaitAsync(Deadline); // quit: nothing pending
        fixture.Services.NativeSendDrainLimit = Deadline;
        var close = session.SendTurns(101).Take(); // issued after that drain; its reply has not arrived
        Task shutdown = Task.CompletedTask;
        try
        {
            shutdown = fixture.Services.DrainNativeSendsAsync();
            Assert.False(shutdown.IsCompleted, "The shutdown drain found its deadline spent by a drain that had nothing to wait for.");
        }
        finally { close.Release(); }
        await shutdown.WaitAsync(Deadline);
    }
}
