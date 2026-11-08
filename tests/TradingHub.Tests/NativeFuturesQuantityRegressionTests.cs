using System.Diagnostics;
using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

public class NativeFuturesQuantityRegressionTests
{
    [Fact]
    public async Task RithmicManualFractionalQuantityRejectsBeforeNativeCreate()
    {
        using var server = new FakeRithmicServer();
        using var orders = new NativeOrderTestFixture();
        using var market = new SimulatedFeed();
        using var data = new InstrumentData(FuturesInstrument(RithmicFeed.FeedId), market) { RecordLive = false };
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000
        });
        using var broker = orders.Rithmic(data, session, "ACC1", "Native quantity fixture",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady, "Rithmic native replay did not become ready.");

        var whole = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1m, 4990m);
        await Until(() => whole.NativeIdentity != null || whole.Status == OrderStatus.Rejected,
            "Rithmic whole-contract control did not settle.");
        Assert.NotNull(whole.NativeIdentity);
        lock (server.Tags) Assert.Equal(whole.ClientOrderId, Assert.Single(server.Tags));

        var fractional = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1.6m, 4990m);
        await Until(() => fractional.Status == OrderStatus.Rejected || fractional.NativeIdentity != null,
            "Rithmic fractional request did not settle.");

        Assert.Equal(OrderStatus.Rejected, fractional.Status);
        Assert.Equal(1.6m, fractional.QuantityToFill);
        Assert.Equal(1.6m, fractional.Unfilled);
        Assert.Equal(OrderStates.Failed, fractional.State);
        Assert.Null(fractional.NativeIdentity);
        Assert.Empty(fractional.ExchangeOrderId);
        Assert.DoesNotContain(fractional, broker.ActiveOrders);
        lock (server.Tags) Assert.Equal(whole.ClientOrderId, Assert.Single(server.Tags));
    }

    [Fact]
    public async Task TradovateManualFractionalQuantityRejectsBeforeNativeCreate()
    {
        using var fake = new FakeTradovate();
        using var orders = new NativeOrderTestFixture();
        using var market = new SimulatedFeed();
        using var data = new InstrumentData(FuturesInstrument(TradovateFeed.FeedId), market) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady, "Tradovate native snapshot did not become ready.");
        using var broker = orders.Tradovate(data, session, Assert.Single(session.Accounts), "Native quantity fixture");
        await Until(() => broker.OrdersReady && broker.PositionReady, "Tradovate native broker did not become ready.");

        var whole = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1m, 4990m);
        await Until(() => whole.NativeIdentity != null || whole.Status == OrderStatus.Rejected,
            "Tradovate whole-contract control did not settle.");
        Assert.NotNull(whole.NativeIdentity);
        var create = Assert.Single(TradovateCreates(fake));
        var payload = JsonNode.Parse(create.Body)!;
        Assert.Equal(1, payload["orderQty"]!.GetValue<int>());
        Assert.Equal(whole.ClientOrderId, payload["clOrdId"]!.GetValue<string>());

        var fractional = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1.6m, 4990m);
        await Until(() => fractional.Status == OrderStatus.Rejected || fractional.NativeIdentity != null,
            "Tradovate fractional request did not settle.");

        Assert.Equal(OrderStatus.Rejected, fractional.Status);
        Assert.Equal(1.6m, fractional.QuantityToFill);
        Assert.Equal(1.6m, fractional.Unfilled);
        Assert.Equal(OrderStates.Failed, fractional.State);
        Assert.Null(fractional.NativeIdentity);
        Assert.Empty(fractional.ExchangeOrderId);
        Assert.DoesNotContain(fractional, broker.ActiveOrders);
        Assert.Equal(create, Assert.Single(TradovateCreates(fake)));
    }

    [Fact]
    public async Task RithmicOutOfRangeRequestsRejectAndFractionalVirtualSizingRemainsLegal()
    {
        using var server = new FakeRithmicServer();
        using var orders = new NativeOrderTestFixture();
        using var market = new SimulatedFeed();
        using var data = new InstrumentData(FuturesInstrument(RithmicFeed.FeedId), market) { RecordLive = false };
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000
        });
        using var broker = orders.Rithmic(data, session, "ACC1", "Native quantity fixture",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady, "Rithmic native replay did not become ready.");

        var maximum = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, int.MaxValue, 4990m);
        await Until(() => maximum.NativeIdentity != null, "Rithmic maximum whole quantity did not settle.");
        Assert.Equal(int.MaxValue, server.Open[maximum.ExchangeOrderId].Qty);
        var owner = broker.OrderContext.LedgerOwner("fractional-virtual-sizing", Guid.NewGuid());
        var strategy = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Buy, 2m, 4990m,
            strategyQuantity: 0.25m);
        await Until(() => strategy.NativeIdentity != null, "Rithmic converted strategy quantity did not settle.");
        Assert.Equal(2, server.Open[strategy.ExchangeOrderId].Qty);
        Assert.Equal(0.25m, broker.OrderContext.Find(strategy.IntentId)!.Request.StrategyQuantity);

        foreach (decimal quantity in new[] { int.MaxValue + 1m, decimal.MaxValue, 0m, -1m })
        {
            var intent = broker.OrderContext.Prepare(broker.OrderContext.ManualOwner,
                new OrderRequest(data.Instrument.Key, OrderDirections.Buy, OrderTypes.Limit, quantity, 4990m));
            var rejected = broker.Submit(intent);
            AssertRejectedQuantity(broker, rejected, quantity);
            Assert.Equal(quantity, intent.Request.Quantity);
            if (intent.Error == null)
            {
                var saved = Assert.IsType<OwnedOrderIntent>(broker.OrderContext.Find(intent.Id));
                Assert.Equal(quantity, saved.Request.Quantity);
                Assert.NotNull(saved.Rejection);
                Assert.False(saved.DispatchUncertain);
                Assert.Null(saved.NativeOrders);
            }
            lock (server.Tags) Assert.Equal(2, server.Tags.Count);
            Assert.Equal(0, server.ModifyRequests);
            Assert.Equal(2, server.Open.Count);
        }
    }

    [Fact]
    public async Task TradovateOutOfRangeRequestsRejectAndFractionalVirtualSizingRemainsLegal()
    {
        using var fake = new FakeTradovate();
        using var orders = new NativeOrderTestFixture();
        using var market = new SimulatedFeed();
        using var data = new InstrumentData(FuturesInstrument(TradovateFeed.FeedId), market) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady, "Tradovate native snapshot did not become ready.");
        using var broker = orders.Tradovate(data, session, Assert.Single(session.Accounts), "Native quantity fixture");
        await Until(() => broker.OrdersReady && broker.PositionReady, "Tradovate native broker did not become ready.");

        var maximum = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, int.MaxValue, 4990m);
        await Until(() => maximum.NativeIdentity != null, "Tradovate maximum whole quantity did not settle.");
        Assert.Equal(int.MaxValue, JsonNode.Parse(Assert.Single(TradovateCreates(fake)).Body)!["orderQty"]!.GetValue<int>());
        var owner = broker.OrderContext.LedgerOwner("fractional-virtual-sizing", Guid.NewGuid());
        var strategy = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Buy, 2m, 4990m,
            strategyQuantity: 0.25m);
        await Until(() => strategy.NativeIdentity != null, "Tradovate converted strategy quantity did not settle.");
        var creates = TradovateCreates(fake);
        Assert.Equal(2, creates.Length);
        Assert.Equal(2, JsonNode.Parse(creates[1].Body)!["orderQty"]!.GetValue<int>());
        Assert.Equal(0.25m, broker.OrderContext.Find(strategy.IntentId)!.Request.StrategyQuantity);

        foreach (decimal quantity in new[] { int.MaxValue + 1m, decimal.MaxValue, 0m, -1m })
        {
            var intent = broker.OrderContext.Prepare(broker.OrderContext.ManualOwner,
                new OrderRequest(data.Instrument.Key, OrderDirections.Buy, OrderTypes.Limit, quantity, 4990m));
            var rejected = broker.Submit(intent);
            AssertRejectedQuantity(broker, rejected, quantity);
            Assert.Equal(quantity, intent.Request.Quantity);
            if (intent.Error == null)
            {
                var saved = Assert.IsType<OwnedOrderIntent>(broker.OrderContext.Find(intent.Id));
                Assert.Equal(quantity, saved.Request.Quantity);
                Assert.NotNull(saved.Rejection);
                Assert.False(saved.DispatchUncertain);
                Assert.Null(saved.NativeOrders);
            }
            Assert.Equal(creates, TradovateCreates(fake));
            Assert.Empty(TradovateRequests(fake, "/v1/order/modifyorder"));
            Assert.Empty(TradovateRequests(fake, "/v1/order/cancelorder"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RithmicInvalidReplacementPreservesIdentityAndLaterMaintenance(bool stop)
    {
        using var server = new FakeRithmicServer();
        using var orders = new NativeOrderTestFixture();
        using var market = new SimulatedFeed();
        using var data = new InstrumentData(FuturesInstrument(RithmicFeed.FeedId), market) { RecordLive = false };
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000
        });
        using var broker = orders.Rithmic(data, session, "ACC1", "Native replacement fixture",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady, "Rithmic native replay did not become ready.");
        var order = OrderActions.Manual(broker, stop ? OrderTypes.Stop : OrderTypes.Limit,
            stop ? OrderDirections.Sell : OrderDirections.Buy, 3m, 4990m);
        await Until(() => order.NativeIdentity != null && broker.ActiveOrders.Contains(order),
            "Rithmic predecessor did not become active.");
        string basket = order.ExchangeOrderId;
        await server.FillOrder(basket, 1, 4990);
        await Until(() => order.Filled == 1m, "Rithmic predecessor fill did not arrive.");
        var identity = (order.ContextId, order.OwnerId, order.IntentId, order.LogicalOrderId,
            order.ClientOrderId, order.ExchangeOrderId, order.NativeIdentity);

        foreach (decimal quantity in new[] { 0.4m, 1.6m, (decimal)int.MaxValue, int.MaxValue + 1m, decimal.MaxValue })
        {
            var intent = broker.OrderContext.PrepareReplacement(order, 4980m, quantity);
            Assert.Null(intent.Error);
            Assert.Same(order, broker.Replace(order, intent));
            Assert.Equal(identity, (order.ContextId, order.OwnerId, order.IntentId, order.LogicalOrderId,
                order.ClientOrderId, order.ExchangeOrderId, order.NativeIdentity));
            AssertRejectedReplacement(broker, order, intent, quantity);
            Assert.False(broker.IsReplacementPending(order));
            Assert.Equal(0, server.ModifyRequests);
            Assert.Equal(3, server.Open[basket].Qty);
            Assert.Equal(4990d, server.Open[basket].Price);
            lock (server.Tags) Assert.Equal(order.ClientOrderId, Assert.Single(server.Tags));
        }

        Assert.Same(order, OrderActions.Reprice(broker, order, 4991m));
        await Until(() => server.ModifyRequests == 1 && !broker.IsReplacementPending(order) &&
            (stop ? order.TriggerPrice : order.Price) == 4991m, "Rithmic later price maintenance did not settle.");
        Assert.Equal(3, server.Open[basket].Qty);
        Assert.Equal(basket, order.ExchangeOrderId);
        Assert.Equal(identity.NativeIdentity, order.NativeIdentity);
        Assert.Same(order, OrderActions.Replace(broker, order, 4992m, 1m));
        await Until(() => server.ModifyRequests == 2 && !broker.IsReplacementPending(order) &&
            order.QuantityToFill == 2m, "Rithmic later valid resize did not settle.");
        Assert.Equal(2, server.Open[basket].Qty);
        Assert.Equal(1m, order.Filled);
        Assert.Equal(1m, order.Unfilled);
        broker.Cancel(order);
        await Until(() => order.Status == OrderStatus.Canceled && !broker.ActiveOrders.Contains(order),
            "Rithmic preserved predecessor could not be canceled.");
        Assert.Equal(basket, order.ExchangeOrderId);
        Assert.Equal(identity.NativeIdentity, order.NativeIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TradovateInvalidReplacementPreservesIdentityAndLaterMaintenance(bool stop)
    {
        using var fake = new FakeTradovate();
        using var orders = new NativeOrderTestFixture();
        using var market = new SimulatedFeed();
        using var data = new InstrumentData(FuturesInstrument(TradovateFeed.FeedId), market) { RecordLive = false };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady, "Tradovate native snapshot did not become ready.");
        using var broker = orders.Tradovate(data, session, Assert.Single(session.Accounts), "Native replacement fixture");
        await Until(() => broker.OrdersReady && broker.PositionReady, "Tradovate native broker did not become ready.");
        var order = OrderActions.Manual(broker, stop ? OrderTypes.Stop : OrderTypes.Limit,
            stop ? OrderDirections.Sell : OrderDirections.Buy, 3m, 4990m);
        await Until(() => order.NativeIdentity != null && broker.ActiveOrders.Contains(order) &&
            session.OrdersOf(101, 555).Any(state => state.Id.ToString() == order.ExchangeOrderId &&
                state.Quantity == 3m && state.Type == (stop ? "Stop" : "Limit")),
            "Tradovate predecessor did not become active.");
        long nativeId = long.Parse(order.ExchangeOrderId, System.Globalization.CultureInfo.InvariantCulture);
        await fake.PublishFill(new JsonObject
        {
            ["id"] = 9901, ["orderId"] = nativeId, ["contractId"] = 555,
            ["action"] = stop ? "Sell" : "Buy", ["qty"] = 1, ["price"] = 4990,
            ["timestamp"] = "2026-11-06T12:00:00Z", ["active"] = true
        });
        await Until(() => order.Filled == 1m, "Tradovate predecessor fill did not arrive.");
        var identity = (order.ContextId, order.OwnerId, order.IntentId, order.LogicalOrderId,
            order.ClientOrderId, order.ExchangeOrderId, order.NativeIdentity);
        var create = Assert.Single(TradovateCreates(fake));

        foreach (decimal quantity in new[] { 0.4m, 1.6m, (decimal)int.MaxValue, int.MaxValue + 1m, decimal.MaxValue })
        {
            var intent = broker.OrderContext.PrepareReplacement(order, 4980m, quantity);
            Assert.Null(intent.Error);
            Assert.Same(order, broker.Replace(order, intent));
            Assert.Equal(identity, (order.ContextId, order.OwnerId, order.IntentId, order.LogicalOrderId,
                order.ClientOrderId, order.ExchangeOrderId, order.NativeIdentity));
            AssertRejectedReplacement(broker, order, intent, quantity);
            Assert.False(broker.IsReplacementPending(order));
            Assert.Equal(create, Assert.Single(TradovateCreates(fake)));
            Assert.Empty(TradovateRequests(fake, "/v1/order/modifyorder"));
            Assert.Empty(TradovateRequests(fake, "/v1/order/cancelorder"));
        }

        Assert.Same(order, OrderActions.Reprice(broker, order, 4991m));
        await Until(() => TradovateRequests(fake, "/v1/order/modifyorder").Length == 1 &&
            !broker.IsReplacementPending(order) && (stop ? order.TriggerPrice : order.Price) == 4991m,
            "Tradovate later price maintenance did not settle.");
        var maintenance = JsonNode.Parse(Assert.Single(TradovateRequests(fake, "/v1/order/modifyorder")).Body)!;
        Assert.Equal(nativeId, maintenance["orderId"]!.GetValue<long>());
        Assert.Equal(3, maintenance["orderQty"]!.GetValue<int>());
        Assert.Equal(identity.NativeIdentity, order.NativeIdentity);
        Assert.Same(order, OrderActions.Replace(broker, order, 4992m, 1m));
        await Until(() => TradovateRequests(fake, "/v1/order/modifyorder").Length == 2 &&
            !broker.IsReplacementPending(order) && order.QuantityToFill == 2m,
            "Tradovate later valid resize did not settle.");
        var resize = JsonNode.Parse(TradovateRequests(fake, "/v1/order/modifyorder")[1].Body)!;
        Assert.Equal(nativeId, resize["orderId"]!.GetValue<long>());
        Assert.Equal(2, resize["orderQty"]!.GetValue<int>());
        Assert.Equal(1m, order.Filled);
        Assert.Equal(1m, order.Unfilled);
        broker.Cancel(order);
        await Until(() => order.Status == OrderStatus.Canceled && !broker.ActiveOrders.Contains(order),
            "Tradovate preserved predecessor could not be canceled.");
        Assert.Equal(identity.ExchangeOrderId, order.ExchangeOrderId);
        Assert.Equal(identity.NativeIdentity, order.NativeIdentity);
        var cancellation = JsonNode.Parse(Assert.Single(TradovateRequests(fake, "/v1/order/cancelorder")).Body)!;
        Assert.Equal(nativeId, cancellation["orderId"]!.GetValue<long>());
    }

    private static void AssertRejectedQuantity(ITradingAccount broker, Order order, decimal requested)
    {
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(OrderStates.Failed, order.State);
        Assert.Equal(requested, order.QuantityToFill);
        Assert.Equal(requested, order.Unfilled);
        Assert.Equal(0m, order.Filled);
        Assert.Null(order.NativeIdentity);
        Assert.Empty(order.ExchangeOrderId);
        Assert.DoesNotContain(order, broker.ActiveOrders);
    }

    private static void AssertRejectedReplacement(ITradingAccount broker, Order order, PreparedOrderIntent intent, decimal requested)
    {
        var saved = Assert.IsType<OwnedOrderIntent>(broker.OrderContext.Find(intent.Id));
        Assert.Equal(requested, saved.Request.Quantity);
        Assert.NotNull(saved.Rejection);
        Assert.False(saved.DispatchUncertain);
        Assert.Null(saved.NativeOrders);
        Assert.Null(broker.OrderContext.Find(order.IntentId)!.Rejection);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Equal(OrderStatus.PartlyFilled, order.Status);
        Assert.Equal(3m, order.QuantityToFill);
        Assert.Equal(2m, order.Unfilled);
        Assert.Equal(1m, order.Filled);
        Assert.Equal(4990m, order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
    }

    private static Instrument FuturesInstrument(string feedId) => new()
    {
        Symbol = "ES", FeedId = feedId, Exchange = "CME", TickSize = 0.25m,
        VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD"
    };

    private static FakeRequest[] TradovateCreates(FakeTradovate fake) => TradovateRequests(fake, "/v1/order/placeorder");

    private static FakeRequest[] TradovateRequests(FakeTradovate fake, string route)
    {
        lock (fake.Server.Requests)
            return fake.Server.Requests.Where(request => request.Route == route).ToArray();
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }
}
