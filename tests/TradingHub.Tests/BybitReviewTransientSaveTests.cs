using System.Text.Json;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>A failed durable save of Bybit native state blocks new exposure only until a later save succeeds; because
/// every save writes the whole tracked state, that later save also persists what the failed one could not.</summary>
public sealed class BybitReviewTransientSaveTests : IDisposable
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
    private readonly string _root = Directory.CreateTempSubdirectory("tradinghub-bybit-review-save-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static JsonElement[] SavedOrders(string statePath)
    {
        using var envelope = JsonDocument.Parse(File.ReadAllText(statePath));
        using var payload = JsonDocument.Parse(envelope.RootElement.GetProperty("Payload").GetString()!);
        return payload.RootElement.GetProperty("Orders").EnumerateArray().Select(order => order.Clone()).ToArray();
    }

    private static bool Saved(string statePath, Order order) =>
        SavedOrders(statePath).Any(saved => saved.GetProperty("LinkId").GetString() == order.ClientOrderId);

    [Fact]
    public async Task A_failed_state_save_blocks_new_exposure_until_a_later_save_persists_the_missed_state()
    {
        string statePath = Path.Combine(_root, "bybit", "orders.json");
        string publication = "->" + DurabilityRecorder.Full(statePath);
        bool failing = false;
        int refusedPublications = 0;
        // Only the state file's own publication fails; ownership and execution journals keep the OS behavior.
        var recorder = new DurabilityRecorder
        {
            FailWhen = step =>
            {
                if (!failing || !step.StartsWith("rename:", StringComparison.Ordinal) || !step.EndsWith(publication, StringComparison.Ordinal)) return false;
                refusedPublications++;
                return true;
            },
        };
        using var scope = recorder.Install();
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, statePath: statePath, httpClient: http,
            clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        await session.RefreshAsync();
        Assert.True(session.PositionReady && session.OrdersReady, session.LastError);

        // One failed save: the opening order that needed it is rejected before transport and stays tracked in memory.
        failing = true;
        var unsaved = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        Assert.Equal(OrderStates.Failed, unsaved.State);
        Assert.Equal(1, refusedPublications);
        Assert.Contains("could not be saved; new exposure is blocked", session.LastError);
        Assert.Contains(unsaved, session.Orders);
        Assert.False(Saved(statePath, unsaved));

        // While storage keeps failing, the next state change retries the save and new exposure stays blocked.
        await session.RefreshAsync();
        Assert.Equal(2, refusedPublications);
        Assert.False(session.OrdersReady);
        var whileFailing = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        Assert.Equal(OrderStates.Failed, whileFailing.State);
        Assert.Contains("could not be saved; new exposure is blocked", session.LastError);

        // Storage recovers, but nothing has been saved yet: still blocked.
        failing = false;
        var beforeSave = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        Assert.Equal(OrderStates.Failed, beforeSave.State);
        Assert.Contains("could not be saved; new exposure is blocked", session.LastError);
        Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");

        // The next reconciliation's save succeeds: it persists the change the failed save missed and lifts the block.
        await session.RefreshAsync();
        Assert.Equal(2, refusedPublications);
        Assert.True(session.PositionReady && session.OrdersReady, session.LastError);
        Assert.DoesNotContain("could not be saved", session.LastError ?? "");
        var persisted = Assert.Single(SavedOrders(statePath), saved => saved.GetProperty("LinkId").GetString() == unsaved.ClientOrderId);
        Assert.Equal((int)OrderStates.Failed, persisted.GetProperty("State").GetInt32());

        var allowed = session.Place(Btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
        Assert.Equal(OrderStates.Active, allowed.State);
        await protocol.CreateReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
    }
}
