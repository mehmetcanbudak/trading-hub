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

    /// <summary>
    /// A close behind an unanswered earlier protective amendment is sent after one reducing bound, not two: its
    /// dispatch expires the bound once, then cancels the working protection and sends without waiting again.
    /// </summary>
    [Fact]
    public async Task A_close_behind_an_unanswered_earlier_amendment_is_sent_after_one_bound()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        server.SeedPosition(true, 2, 5000);
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        var bound = TimeSpan.FromMilliseconds(1500);
        session.SendTurnWait = bound;
        await Until(() => broker.OrdersReady && broker.PositionReady);
        var stop = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1m, 4900m);
        await Until(() => stop.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(stop), () => broker.LastError ?? "stop not working");
        try
        {
            var amendReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ModifyResponseRelease = amendReply;
            OrderActions.Reprice(broker, stop, 4901m);
            await Until(() => server.ModifyRequests == 1, () => broker.LastError ?? "amend not sent");

            var issued = DateTime.UtcNow;
            var confirmation = broker.CapturePositionClose(2m);
            Task<string?> close = session.ClosePositionAsync(confirmation);
            long closeArrived = 0;
            _ = Task.Run(async () =>
            {
                while (Interlocked.Read(ref closeArrived) == 0)
                {
                    if (Requests(server, "312") >= 2) { Interlocked.CompareExchange(ref closeArrived, DateTime.UtcNow.Ticks, 0); return; } // the stop's placement is the first 312
                    await Task.Delay(15);
                }
            });
            await Until(() => Interlocked.Read(ref closeArrived) != 0, () => broker.LastError ?? "close not sent");
            var waited = new DateTime(Interlocked.Read(ref closeArrived), DateTimeKind.Utc) - issued;
            Assert.True(waited >= bound - TimeSpan.FromMilliseconds(60), $"The close was sent after {waited.TotalMilliseconds} ms, before its bound.");
            Assert.True(waited < bound * 2 - TimeSpan.FromMilliseconds(300), $"The close waited {waited.TotalMilliseconds} ms: a second bound.");
            Assert.Equal(NativeSendTurns.UnsettledMessage, await close.WaitAsync(Deadline));
            await Until(() => server.Position == 0 && !session.HasUnresolvedPositionClose, () => broker.LastError ?? "close pending");
        }
        finally { server.ModifyResponseRelease?.TrySetResult(); }
        await Until(() => session.SendsIdle.IsCompleted, () => "sends not idle after the amend reply");
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
