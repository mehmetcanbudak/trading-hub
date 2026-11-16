using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Rithmic orders have no send turns: a buy issued before a bounded close can have its native admission delayed (here
/// by an unresolved contract) past the close's whole lifecycle. This test records what the reservation check actually
/// does once the close completed and released its reservation.
/// </summary>
public sealed class RithmicBuyAfterCloseAdmissionTests : IDisposable
{
    private readonly NativeOrderTestFixture _orders = new();
    public void Dispose() => _orders.Dispose();

    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = RithmicFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 50 };
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static RithmicSettings Settings(FakeRithmicServer s) =>
        new() { Uri = s.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000 };

    private static async Task Until(Func<bool> condition, int ms = 8000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
        Assert.True(condition(), "Timed out waiting for the expected condition.");
    }

    [Fact]
    public async Task A_buy_whose_admission_is_delayed_past_a_completed_close_is_admitted_afterwards()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        server.SeedPosition(true, 2, 5000);
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        var contractGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var closeBroker = _orders.Rithmic(data, session, "ACC1", "close",
            _ => Task.FromResult(("ESZ6", "CME")));
        using var buyBroker = _orders.Rithmic(data, session, "ACC1", "buy",
            async ct => { await contractGate.Task.WaitAsync(ct); return ("ESZ6", "CME"); });
        session.Start();
        await Until(() => closeBroker.OrdersReady && closeBroker.PositionReady);
        Assert.Equal(2m, closeBroker.Position.Volume);
        Assert.False(buyBroker.PositionReady, "The buy broker's contract must still be unresolved.");

        // The user issues the buy while its native admission is deliberately unresolved.
        var buy = OrderActions.Manual(buyBroker, OrderTypes.Market, OrderDirections.Buy, 1m);
        await Until(() => buy.State == OrderStates.Active && buy.ExchangeOrderId.Length == 0);
        Assert.Empty(server.Tags);

        // The close runs to completion and releases its reservation while the buy is still unadmitted.
        var confirmation = closeBroker.CapturePositionClose(2m);
        Task<string?> close = session.ClosePositionAsync(confirmation);
        string? closeOutcome = await close.WaitAsync(Deadline);
        Assert.Null(closeOutcome);
        Assert.Equal(0, server.Position);
        await Until(() => !session.HasUnresolvedPositionClose);
        Assert.DoesNotContain(server.Tags, tag => server.Open.TryGetValue(tag, out var open) && open.Buy);

        // The buy's admission then proceeds against the fresh flat position.
        contractGate.TrySetResult();
        await Until(() => server.Position == 1);
        await Until(() => buy.State == OrderStates.Done && buy.Status == OrderStatus.Filled);
        Assert.Equal(1m, closeBroker.Position.Volume);
    }
}
