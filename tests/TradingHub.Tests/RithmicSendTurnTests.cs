using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Rithmic send-turn ordering: one turn queue per native account. A new-exposure order waits for earlier replies
/// without a bound; a reducing request (close, cancel, protective amendment) waits at most the reducing bound, then
/// sends anyway with the unsettled note. The turn is released on every path, so a failing subscriber never holds
/// later requests.
/// </summary>
[Collection("LiveStore")] // static Rithmic close reservations: never in parallel with other native sessions
public sealed class RithmicSendTurnTests : IDisposable
{
    private readonly NativeOrderTestFixture _orders = new();
    public void Dispose() => _orders.Dispose();

    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = RithmicFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 50 };
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);

    private static RithmicSettings Settings(FakeRithmicServer s) =>
        new() { Uri = s.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000 };

    private static async Task Until(Func<bool> condition, Func<string>? why = null, int ms = 9000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
        Assert.True(condition(), $"Timed out: {why?.Invoke() ?? "the expected condition never held."}");
    }

    private static int Requests(FakeRithmicServer server, string template)
    {
        lock (server.Received) return server.Received.Count(item => item == template);
    }

    /// <summary>
    /// A buy issued before a bounded close, whose native admission is delayed (unresolved contract), can no longer be
    /// admitted after the close completed: the close's dispatch waits for the buy's turn, the buy's admission resolves
    /// first (refused while the close reserves the contract), and only then is the close sent. Final position equals
    /// the user's intent: the whole long 2 is closed, and the buy never reached the venue.
    /// </summary>
    [Fact]
    public async Task A_close_is_sent_only_after_the_earlier_delayed_buy_admission_resolved()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        server.SeedPosition(true, 2, 5000);
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        var contractGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        using var buyBroker = _orders.Rithmic(data, session, "ACC1", "delayed",
            async ct => { await contractGate.Task.WaitAsync(ct); return ("ESZ6", "CME"); });
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        Assert.Equal(2m, broker.Position.Volume);
        Assert.False(buyBroker.PositionReady, "The buy broker's contract must still be unresolved.");

        var buy = OrderActions.Manual(buyBroker, OrderTypes.Market, OrderDirections.Buy, 1m);
        await Until(() => buy.State == OrderStates.Active && buy.ExchangeOrderId.Length == 0);
        Assert.Empty(server.Tags);

        var confirmation = broker.CapturePositionClose(2m);
        Task<string?> close = session.ClosePositionAsync(confirmation);

        // The buy's admission resolves first, while the close still reserves the contract. Releasing the buy's turn
        // and sending the close are the same completion, so the venue can already hold the close's tag by the time
        // the rejection is observable. The post-conditions below are the ordering guarantee: one send, the close's,
        // and the buy never reaches the venue.
        contractGate.TrySetResult();
        await Until(() => buy.State == OrderStates.Failed && buy.Status == OrderStatus.Rejected, () => buyBroker.LastError ?? "buy not rejected");
        Assert.Contains("new exposure is disabled", buyBroker.LastError, StringComparison.Ordinal);

        // The close is sent only after that admission, and it closes the whole long 2.
        await Until(() => Requests(server, "312") == 1, () => $"sends: {string.Join(", ", server.Tags)}");
        await Until(() => server.Position == 0 && !session.HasUnresolvedPositionClose, () => broker.LastError ?? "close pending");
        Assert.Null(await close.WaitAsync(Deadline));
        Assert.Equal(OrderStates.Failed, buy.State);
        Assert.Equal(0, buy.ExchangeOrderId.Length);
        Assert.Single(server.Tags); // only the close's tag; the buy never reached the venue
        Assert.Equal(1, Requests(server, "312"));
    }

    /// <summary>The account reports its requests waiting for an earlier reply, so the order status line can show them.</summary>
    [Fact]
    public async Task The_account_reports_the_requests_waiting_for_an_earlier_reply()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1", _ => Task.FromResult(("ESZ6", "CME")));
        INativeSendQueue queue = broker;
        Assert.Equal(0, queue.QueuedRequests);

        var turns = session.SendTurns("ACC1");
        var first = turns.Take();
        var second = turns.Take();
        try { Assert.Equal(1, queue.QueuedRequests); }
        finally { second.Release(); first.Release(); }
        await turns.WhenIdle.WaitAsync(Deadline);
        Assert.Equal(0, queue.QueuedRequests);
    }

    /// <summary>
    /// A close behind an unanswered earlier protective amendment is sent after one reducing bound, not two: its
    /// dispatch gives up the bound once, then cancels the working protection and sends without waiting again. Proven by
    /// the account's bounded waits that gave up when the close arrives, with the amendment still unanswered, not by
    /// timing it.
    /// </summary>
    [Fact]
    public async Task A_close_behind_an_unanswered_earlier_amendment_is_sent_after_one_bound()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        server.SeedPosition(true, 2, 5000);
        using var session = new RithmicTradingSession(Settings(server));
        session.SendTurnWait = TimeSpan.FromMilliseconds(300);
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        var stop = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1m, 4900m);
        await Until(() => stop.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(stop), () => broker.LastError ?? "stop not working");
        var turns = session.SendTurns("ACC1");
        try
        {
            var amendReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ModifyResponseRelease = amendReply;
            OrderActions.Reprice(broker, stop, 4901m);
            await Until(() => server.ModifyRequests == 1, () => broker.LastError ?? "amend not sent");
            Assert.Equal(0, turns.GaveUp);

            int gaveUpAtClose = -1;
            bool answeredAtClose = true;
            server.OnReceived = template =>
            {
                // The stop's placement was the first new order; the close is the second.
                if (template != "312" || Requests(server, "312") != 2) return;
                answeredAtClose = server.ModifyResponseSent.Task.IsCompleted;
                Volatile.Write(ref gaveUpAtClose, turns.GaveUp);
            };
            Task<string?> close = session.ClosePositionAsync(broker.CapturePositionClose(2m));
            Assert.Equal(NativeSendTurns.UnsettledMessage, await close.WaitAsync(Deadline));
            await Until(() => server.Position == 0 && !session.HasUnresolvedPositionClose, () => broker.LastError ?? "close pending");
            // Sent while the amendment was unanswered, after exactly one bounded wait gave up: the protection cancel rode
            // the close's turn instead of waiting a second bound.
            Assert.False(answeredAtClose, "The close waited for the amendment's reply instead of its bound.");
            Assert.Equal(1, Volatile.Read(ref gaveUpAtClose));
            Assert.Equal(1, turns.GaveUp);
        }
        finally
        {
            server.OnReceived = null;
            server.ModifyResponseRelease?.TrySetResult();
        }
        await Until(() => session.SendsIdle.IsCompleted, () => "sends not idle after the amend reply");
    }

    /// <summary>
    /// A close that waited out its reducing bound behind an unanswered amendment still has its whole close budget: the
    /// budget starts once its turn came. Here the bound outlasts the budget, so a budget spent waiting would cancel the
    /// close before anything was sent ("A task was canceled", no close) instead of sending it anyway.
    /// </summary>
    [Fact]
    public async Task A_close_that_waited_out_its_bound_still_has_its_whole_budget()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        server.SeedPosition(true, 2, 5000);
        using var session = new RithmicTradingSession(Settings(server));
        session.SendTurnWait = TimeSpan.FromSeconds(4);
        session.CloseBudget = TimeSpan.FromSeconds(2);
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        var stop = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1m, 4900m);
        await Until(() => stop.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(stop), () => broker.LastError ?? "stop not working");
        var amendReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ModifyResponseRelease = amendReply;
        try
        {
            OrderActions.Reprice(broker, stop, 4901m);
            await Until(() => server.ModifyRequests == 1, () => broker.LastError ?? "amend not sent");
            Task<string?> close = session.ClosePositionAsync(broker.CapturePositionClose(2m));
            Assert.Equal(NativeSendTurns.UnsettledMessage, await close.WaitAsync(Deadline));
            await Until(() => server.Position == 0 && !session.HasUnresolvedPositionClose, () => broker.LastError ?? "close pending");
            Assert.False(server.ModifyResponseSent.Task.IsCompleted, "The amendment was answered before the close went.");
            Assert.Equal(1, session.SendTurns("ACC1").GaveUp);
        }
        finally { amendReply.TrySetResult(); }
        await Until(() => session.SendsIdle.IsCompleted, () => "sends not idle after the amend reply");
    }

    /// <summary>
    /// A session-level amendment of an entry (it can add exposure) waits for the earlier unanswered amendment without the
    /// reducing bound: a cancel issued after it gives up its bound and is sent while the entry amendment is still held,
    /// and the entry amendment goes once the earlier one was answered. (The earlier request is an amendment: the fake
    /// holds its reply without stalling the connection, so the later requests still reach it.)
    /// </summary>
    [Fact]
    public async Task A_session_entry_amendment_waits_for_earlier_replies_without_the_reducing_bound()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        using var session = new RithmicTradingSession(Settings(server));
        session.SendTurnWait = TimeSpan.FromMilliseconds(300);
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        var earlier = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1m, 4890m);
        var entry = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1m, 4900m);
        await Until(() => broker.ActiveOrders.Count(order => order.ExchangeOrderId.Length > 0) == 2, () => broker.LastError ?? "entries not working");
        var working = Assert.Single(session.OrdersOf("ACC1"), order => order.BasketId == entry.ExchangeOrderId);
        var turns = session.SendTurns("ACC1");
        var earlierReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ModifyResponseRelease = earlierReply;
        try
        {
            OrderActions.Reprice(broker, earlier, 4891m);
            await Until(() => server.ModifyRequests == 1, () => broker.LastError ?? "earlier amendment not sent");
            server.ModifyResponseRelease = null; // only the earlier amendment's reply stays held
            Task<string?> amendment = session.ModifyAsync("ACC1", working, 2, 4901m, CancellationToken.None);
            await Until(() => turns.Waiting == 1, () => "entry amendment not waiting for its turn");
            // A later reducing request gives up its bound behind the same earlier amendment and is sent.
            Task cancel = session.CancelAsync("ACC1", "later-cancel-target", CancellationToken.None).ContinueWith(_ => { });
            await Until(() => Requests(server, "316") == 1, () => "later cancel not sent after its bound");
            Assert.Equal(1, server.ModifyRequests);
            Assert.False(amendment.IsCompleted);
            Assert.Equal(1, turns.GaveUp);

            earlierReply.TrySetResult();
            Assert.Null(await amendment.WaitAsync(Deadline));
            Assert.Equal(2, server.ModifyRequests);
            await cancel.WaitAsync(Deadline);
        }
        finally { earlierReply.TrySetResult(); }
        await Until(() => session.SendsIdle.IsCompleted, () => "sends not idle");
    }

    /// <summary>
    /// A session-level amendment of a protective stop (it only reduces the long position) waits for the earlier
    /// unanswered amendment at most the reducing bound, is sent anyway, and reports that it went out of order.
    /// </summary>
    [Fact]
    public async Task A_session_reducing_amendment_sent_after_its_bound_reports_the_unsettled_note()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        server.SeedPosition(true, 2, 5000);
        using var session = new RithmicTradingSession(Settings(server));
        session.SendTurnWait = TimeSpan.FromMilliseconds(300);
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        var earlier = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1m, 4890m);
        var stop = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1m, 4900m);
        await Until(() => broker.ActiveOrders.Count(order => order.ExchangeOrderId.Length > 0) == 2, () => broker.LastError ?? "stops not working");
        var working = Assert.Single(session.OrdersOf("ACC1"), order => order.BasketId == stop.ExchangeOrderId);
        var turns = session.SendTurns("ACC1");
        var earlierReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ModifyResponseRelease = earlierReply;
        try
        {
            OrderActions.Reprice(broker, earlier, 4891m);
            await Until(() => server.ModifyRequests == 1, () => broker.LastError ?? "earlier amendment not sent");
            server.ModifyResponseRelease = null; // only the earlier amendment's reply stays held
            Assert.Equal(NativeSendTurns.UnsettledMessage,
                await session.ModifyAsync("ACC1", working, 1, 4899m, CancellationToken.None).WaitAsync(Deadline));
            Assert.Equal(2, server.ModifyRequests);
            Assert.Equal(1, turns.GaveUp);
            Assert.False(earlierReply.Task.IsCompleted);
        }
        finally { earlierReply.TrySetResult(); }
        await Until(() => session.SendsIdle.IsCompleted, () => "sends not idle");
    }

    /// <summary>
    /// A request issued before the session's first account list (a restored chart clicking during login) and every later
    /// request of that account share one send-turn queue: the queue does not switch to the native account identity once
    /// the list arrives, so a later cancel or close still waits for that earlier request.
    /// </summary>
    [Fact]
    public async Task Requests_before_and_after_the_account_list_share_one_send_turn_queue()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        var beforeLogin = session.SendTurns("ACC1");
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady && session.Accounts.Any(account => account.AccountId == "ACC1"));
        Assert.Same(beforeLogin, session.SendTurns("ACC1"));
    }

    /// <summary>
    /// An order takes its send turn before it is published: a request that a synchronous Changed subscriber issues
    /// when the new order first appears is sent after that order's reply, not ahead of it.
    /// </summary>
    [Fact]
    public async Task A_request_issued_while_an_order_is_published_is_sent_after_that_order()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        var orderReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.NewOrderResponseRelease = orderReply;
        int armed = 1;
        Task? published = null;
        broker.Changed += () =>
        {
            if (broker.ActiveOrders.Any(order => order.ExchangeOrderId.Length == 0) && Interlocked.Exchange(ref armed, 0) == 1)
                Volatile.Write(ref published, session.CancelAsync("ACC1", "published-cancel-target", CancellationToken.None).ContinueWith(_ => { }));
        };
        try
        {
            OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1m, 4900m);
            await Until(() => Volatile.Read(ref published) != null, () => "no request was issued while the order was published");
            await server.NewOrderResponseRequested.Task.WaitAsync(Deadline);
            // The order reached the venue first; the subscriber's cancel waits behind its unanswered reply.
            Assert.Equal(0, Requests(server, "316"));
            await Until(() => session.SendTurns("ACC1").Waiting == 1, () => "published cancel not waiting for the order's reply");
            orderReply.TrySetResult();
            await published!.WaitAsync(Deadline);
            lock (server.Received)
                Assert.True(server.Received.IndexOf("312") < server.Received.IndexOf("316"), string.Join(", ", server.Received));
        }
        finally { orderReply.TrySetResult(); }
        await Until(() => session.SendsIdle.IsCompleted, () => "sends not idle");
    }

    /// <summary>
    /// A cancel cancelled while it waits its bounded send-turn wait (the bounded close's protection cancels wait with
    /// its 15 s timeout token) releases the turn it took: later new-exposure sends proceed and the account's sends
    /// become idle. Before the fix the cancelled turn leaked and held every later request.
    /// </summary>
    [Fact]
    public async Task A_cancel_cancelled_during_its_turn_wait_releases_the_turn()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        server.SeedPosition(true, 2, 5000);
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        var replyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.NewOrderResponseRelease = replyGate;
        var earlier = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1m);
        await Until(() => Requests(server, "312") == 1, () => "earlier buy not sent");

        // Behind that unanswered buy, a cancel waits its bounded turn — cancelled mid-wait, like the bounded
        // close's protection cancel whose 15 s timeout fires.
        var cancelCts = new CancellationTokenSource();
        Task<string?> cancel = session.CancelAsync("ACC1", "held-cancel-target", cancelCts.Token);
        await Until(() => session.SendTurns("ACC1").Waiting == 1, () => "cancel not waiting for its turn");
        cancelCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancel.WaitAsync(Deadline));

        // The turn must be released despite the cancellation: once the earlier reply arrives the queue drains.
        replyGate.TrySetResult();
        await Until(() => earlier.State == OrderStates.Done && earlier.Status == OrderStatus.Filled, () => broker.LastError ?? "earlier buy pending");
        await session.SendsIdle.WaitAsync(Deadline);
        var later = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1m);
        await Until(() => Requests(server, "312") == 2 && later.Status == OrderStatus.Filled, () => broker.LastError ?? "later buy not sent");
        await session.SendsIdle.WaitAsync(Deadline);
    }

    /// <summary>
    /// The documented residual: a buy whose admission stays unresolved past the close's reducing bound. The close is
    /// sent anyway after one bound (unsettled note) and completes; the buy is then admitted against the fresh flat
    /// position and opens exposure after the close. Exactly what operar.md tells the user to check for.
    /// </summary>
    [Fact]
    public async Task A_buy_admission_unresolved_past_the_bound_is_admitted_after_the_bound_close()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        server.SeedPosition(true, 2, 5000);
        using var session = new RithmicTradingSession(Settings(server));
        session.SendTurnWait = TimeSpan.FromMilliseconds(500);
        using var data = new InstrumentData(Es, new SimulatedFeed());
        var contractGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        using var buyBroker = _orders.Rithmic(data, session, "ACC1", "delayed",
            async ct => { await contractGate.Task.WaitAsync(ct); return ("ESZ6", "CME"); });
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        Assert.Equal(2m, broker.Position.Volume);
        Assert.False(buyBroker.PositionReady, "The buy broker's contract must still be unresolved.");

        var buy = OrderActions.Manual(buyBroker, OrderTypes.Market, OrderDirections.Buy, 1m);
        await Until(() => buy.State == OrderStates.Active && buy.ExchangeOrderId.Length == 0);
        Assert.Empty(server.Tags);

        var confirmation = broker.CapturePositionClose(2m);
        Task<string?> close = session.ClosePositionAsync(confirmation);

        // The close's bounded wait expires while the buy's admission is still unresolved: it sends anyway.
        Assert.Equal(NativeSendTurns.UnsettledMessage, await close.WaitAsync(Deadline));
        Assert.Equal(0, server.Position);
        await Until(() => !session.HasUnresolvedPositionClose, () => "close not completed");

        // Only then does the buy's admission proceed, against the fresh flat position.
        contractGate.TrySetResult();
        await Until(() => buy.State == OrderStates.Done && buy.Status == OrderStatus.Filled, () => buyBroker.LastError ?? "buy not admitted");
        Assert.Equal(1, server.Position);
        await session.SendsIdle.WaitAsync(Deadline);
    }

    /// <summary>
    /// A Changed subscriber that throws while the close's dispatch is published does not hold the account's send
    /// turn: a later new-exposure order is still sent and the account's sends become idle.
    /// </summary>
    [Fact]
    public async Task A_subscriber_throwing_during_the_close_dispatch_does_not_hold_later_requests()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        server.SeedPosition(true, 1, 5000);
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        int armed = 1;
        session.Changed += () =>
        {
            lock (server.Received)
                if (server.Received.Contains("312") && Interlocked.Exchange(ref armed, 0) == 1)
                    throw new InvalidOperationException("Fixture subscriber failed.");
        };
        var confirmation = broker.CapturePositionClose(1m);
        _ = session.ClosePositionAsync(confirmation);
        await Until(() => Volatile.Read(ref armed) == 0 && server.Position == 0, () => "close not dispatched");
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1m);
        await Until(() => Requests(server, "312") == 2, () => "later buy not sent");
        await Until(() => server.Position == 1, () => "later buy not filled");
        await session.SendsIdle.WaitAsync(Deadline);
    }
}
