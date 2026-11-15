using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// A chart gesture's account call only starts a native order: the request itself is sent later. Requests of one native
/// account reach the venue in the order their gestures were issued: a Close position issued after a market buy is not
/// sent while that buy's request is still unanswered.
/// </summary>
public sealed class NativeSendOrderTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private readonly NativeOrderTestFixture _orders = new();

    public void Dispose() => _orders.Dispose();

    [Fact]
    public async Task Tradovate_close_position_is_not_sent_before_an_earlier_buy_is_answered()
    {
        using var fake = new FakeTradovate();
        fake.ReplayPositions.Add(JsonNode.Parse("{\"id\":1,\"accountId\":101,\"contractId\":555,\"netPos\":1,\"netPrice\":5000.25,\"bought\":1,\"boughtValue\":5000.25,\"sold\":0,\"soldValue\":0}"));
        var normalHttp = fake.Server.OnHttp!;
        var buyReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdNextBuy = 0;
        var arrivals = new List<string>(); // order requests in arrival order: "<route> <action>"
        fake.Server.OnHttpAsync = async (request, _) =>
        {
            if (request.Route.StartsWith("/v1/order/", StringComparison.Ordinal))
            {
                string action = request.Route == "/v1/order/placeorder" ? JsonNode.Parse(request.Body)!["action"]!.GetValue<string>() : "";
                lock (arrivals) arrivals.Add((request.Route + " " + action).Trim());
                if (action == "Buy" && Interlocked.Exchange(ref holdNextBuy, 0) == 1) await buyReply.Task;
            }
            return normalHttp(request);
        };
        string[] Arrivals() { lock (arrivals) return arrivals.ToArray(); }
        using var data = new InstrumentData(new Instrument
        {
            Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD",
        }, new SimulatedFeed()) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady, () => session.StatusText);
        using var broker = _orders.Tradovate(data, session, Assert.Single(session.Accounts), "Tradovate send order");
        await Until(() => broker.PositionReady && broker.OrdersReady && broker.Position.Volume == 1, () => broker.LastError);

        Interlocked.Exchange(ref holdNextBuy, 1);
        var buy = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1);
        await Until(() => Arrivals().Contains("/v1/order/placeorder Buy"), () => broker.LastError);
        try
        {
            // Close position, issued after the buy: cancel all, then a reducing market sell sized from the position.
            OrderActions.Flatten(broker);
            await Until(() => Arrivals().Length > 1 || session.QueuedSends(101) > 0, () => broker.LastError);
            Assert.Equal(new[] { "/v1/order/placeorder Buy" }, Arrivals());
        }
        finally { buyReply.TrySetResult(); }

        await Until(() => Arrivals().Contains("/v1/order/placeorder Sell"), () => $"{broker.LastError}; {string.Join(", ", Arrivals())}");
        var sent = Arrivals().Where(route => route.StartsWith("/v1/order/placeorder", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "/v1/order/placeorder Buy", "/v1/order/placeorder Sell" }, sent);
        await Until(() => buy.ExchangeOrderId.Length > 0, () => broker.LastError);
    }

    [Fact]
    public async Task Binance_close_position_is_not_sent_before_an_earlier_buy_is_answered()
    {
        using var fake = new FakeBinance(futures: true);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
        using var broker = _orders.Binance(new InstrumentData(instrument, new SimulatedFeed()), fake.Settings, "Binance send order");
        await Until(() => broker.PositionReady && broker.OrdersReady, () => broker.LastError);
        var opening = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 0.01m);
        await Until(() => broker.Position.Volume == 0.01m && opening.ExchangeOrderId.Length > 0, () => broker.LastError);
        int callsBefore = fake.Calls.Count;

        var buyReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdNextPost = 1;
        fake.BeforePostReply = async _ =>
        {
            if (Interlocked.Exchange(ref holdNextPost, 0) == 1) await buyReply.Task;
        };
        string[] Sends() => fake.Calls.Skip(callsBefore).Where(call => !call.StartsWith("GET ", StringComparison.Ordinal)).ToArray();
        var buy = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 0.01m);
        await Until(() => Sends().Length == 1, () => broker.LastError);
        try
        {
            OrderActions.Flatten(broker);
            await Until(() => Sends().Length > 1 || broker.QueuedSends > 0, () => broker.LastError);
            Assert.Equal(new[] { "POST /fapi/v1/order" }, Sends());
        }
        finally { buyReply.TrySetResult(); }

        await Until(() => fake.Placed.Count(order => order.Side == "SELL") == 1, () => $"{broker.LastError}; {string.Join(", ", Sends())}");
        Assert.Equal("POST /fapi/v1/order", Sends()[0]);
        var placed = fake.Placed.Skip(1).ToArray();
        Assert.Equal(new[] { ("BUY", false), ("SELL", true) }, placed.Select(order => (order.Side, order.ReduceOnly)));
        await Until(() => buy.ExchangeOrderId.Length > 0, () => broker.LastError);
    }

    private static async Task Until(Func<bool> condition, Func<string?> diagnostics)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "The native send-order fixture did not reach the expected state: " + (diagnostics() ?? "(no error)"));
    }
}
