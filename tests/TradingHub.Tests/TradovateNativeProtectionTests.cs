using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Opt-in exchange-side protection through Tradovate's documented OCO strategy (POST /order/placeoco) against the owned
/// fake transport: wire shape, durable per-leg client identities, venue sibling cancellation, refusal and unknown-reply
/// fallbacks, and restart re-ownership. Fake-venue conformance only; a demo account must confirm the real behaviour.
/// </summary>
public sealed class TradovateNativeProtectionTests : IDisposable
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 50 };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-native-protection-" + Guid.NewGuid().ToString("N"));
    private readonly string _contextId = Guid.NewGuid().ToString("N");

    private OrderContext Context()
    {
        var context = new OrderContext(new AccountContextDescriptor("", "Tradovate", "futures", "unconfigured", "", ""));
        context.Configure(new AccountContextDescriptor(_contextId, "Tradovate", "futures", "owned-loopback-fixture", _contextId, "fixture-generation"),
            Path.Combine(_root, _contextId));
        return context;
    }

    private static BracketManager Brackets(ITradingAccount account) => new(account)
    {
        Template = new BracketTemplate
        {
            Enabled = true, StopTicks = 20, BreakevenAfterFirstTarget = false,
        },
    };

    private static async Task Until(Func<bool> condition, Func<string>? diagnostics = null)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > end) Assert.Fail("Condition not reached. " + diagnostics?.Invoke());
            await Task.Delay(20);
        }
    }

    private static int Count(FakeTradovate fake, string route)
    {
        lock (fake.Orders) return fake.Orders.Count(o => route == "placeoco" ? o["other"] != null : o["other"] == null && o["orderId"] == null);
    }

    private static async Task<(TradovateFeed Feed, TradovateTradingSession Session, TradovateAccount Account)> Connect(FakeTradovate fake)
    {
        var feed = new TradovateFeed(() => fake.Settings());
        var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.Accounts.Count == 1 && session.SnapshotReady);
        return (feed, session, Assert.Single(session.Accounts));
    }

    private static void UseTargetOnly(BracketManager brackets)
    {
        brackets.Template.Targets.Clear();
        brackets.Template.Targets.Add(new BracketTarget { Ticks = 40, Percent = 100 });
    }

    [Fact]
    public async Task Exchange_side_pair_is_one_placeoco_with_both_client_ids_and_the_venue_cancels_the_sibling()
    {
        using var fake = new FakeTradovate();
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        using var brackets = Brackets(broker);
        UseTargetOnly(brackets);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => brackets.Mode == ProtectionMode.ExchangeSide, () => $"mode {brackets.Mode}; error {broker.LastError}");

        var stop = brackets.StopLoss!;
        var target = Assert.Single(brackets.Targets);
        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder")); // only the entry
        JsonObject oco;
        lock (fake.Orders) oco = fake.Orders.Single(o => o["other"] != null);
        Assert.Equal("Sell", oco["action"]!.GetValue<string>());
        Assert.Equal("Stop", oco["orderType"]!.GetValue<string>());
        Assert.Equal(4995.25m, oco["stopPrice"]!.GetValue<decimal>());
        Assert.Equal(2, oco["orderQty"]!.GetValue<int>());
        Assert.Equal(stop.ClientOrderId, oco["clOrdId"]!.GetValue<string>());
        var other = oco["other"]!.AsObject();
        Assert.Equal("Sell", other["action"]!.GetValue<string>());
        Assert.Equal("Limit", other["orderType"]!.GetValue<string>());
        Assert.Equal(5010.25m, other["price"]!.GetValue<decimal>());
        Assert.Equal(target.ClientOrderId, other["clOrdId"]!.GetValue<string>());
        Assert.NotEqual(stop.ClientOrderId, target.ClientOrderId);
        Assert.Equal(broker.ProtectionGroupOf(stop), broker.ProtectionGroupOf(target));
        Assert.Equal(broker.OrderContext.ManualOwner.Id, stop.OwnerId);
        Assert.Equal(broker.OrderContext.ManualOwner.Id, target.OwnerId);

        await fake.FillOcoLeg(fake.OcoLegFor(target.ClientOrderId!));
        await Until(() => broker.Position.Volume == 0 && stop.Canceled && target.Status == OrderStatus.Filled);
        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder"));
        Assert.Equal(ProtectionMode.None, brackets.Mode);
    }

    [Fact]
    public async Task Switched_off_sends_the_same_pair_as_two_ordinary_orders()
    {
        using var fake = new FakeTradovate();
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context());
        using var brackets = Brackets(broker);
        UseTargetOnly(brackets);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => brackets.StopLoss?.ExchangeOrderId.Length > 0 && brackets.Targets.Count == 1 && brackets.Targets[0].ExchangeOrderId.Length > 0);

        Assert.Equal(NativeProtectionFeatures.None, broker.NativeProtection);
        Assert.Equal(0, fake.OcoRequests);
        Assert.Equal(3, Count(fake, "placeorder"));
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode);
        Assert.Null(brackets.ProtectionNote);
    }

    [Fact]
    public async Task A_refused_placeoco_falls_back_to_app_managed_orders_with_a_visible_reason()
    {
        using var fake = new FakeTradovate();
        var normal = fake.Server.OnHttp!;
        fake.Server.OnHttp = request => request.Route == "/v1/order/placeoco"
            ? Refuse(fake, request)
            : normal(request);
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        using var brackets = Brackets(broker);
        UseTargetOnly(brackets);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => Count(fake, "placeorder") == 3 && brackets.StopLoss?.ExchangeOrderId.Length > 0 &&
            brackets.Targets.Count == 1 && brackets.Targets[0].ExchangeOrderId.Length > 0, () => $"note {brackets.ProtectionNote}; error {broker.LastError}");

        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(NativeProtectionFeatures.None, broker.NativeProtection);
        Assert.Contains("Unsupported", broker.NativeProtectionFault);
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode);
        Assert.Contains("refused", brackets.ProtectionNote);
        Assert.Equal(2, brackets.StopLoss!.Unfilled);
        Assert.Equal(4995.25m, brackets.StopLoss!.TriggerPrice);
    }

    private static (int, string) Refuse(FakeTradovate fake, FakeRequest request)
    {
        lock (fake.Orders) fake.Orders.Add(JsonNode.Parse(request.Body)!.AsObject());
        Interlocked.Increment(ref fake.OcoRequests);
        return (200, "{\"failureReason\":\"Unsupported\",\"failureText\":\"\"}");
    }

    [Fact]
    public async Task An_unknown_placeoco_reply_is_never_resent_and_both_legs_bind_by_client_id()
    {
        using var fake = new FakeTradovate();
        var normal = fake.Server.OnHttp!;
        fake.Server.OnHttp = request =>
        {
            if (request.Route != "/v1/order/placeoco") return normal(request);
            normal(request); // the venue accepted it ...
            return (502, "Upstream response lost"); // ... but the reply was lost
        };
        var (feed, session, account) = await Connect(fake);
        using var _f = feed;
        using var _s = session;
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
        using var brackets = Brackets(broker);
        UseTargetOnly(brackets);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => brackets.Mode == ProtectionMode.ExchangeSide, () => $"mode {brackets.Mode}; error {broker.LastError}");

        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder"));
        Assert.Null(broker.NativeProtectionFault);
        Assert.True(broker.OrdersReady);
        Assert.DoesNotContain("acceptance is unknown", broker.LastError ?? "");
    }

    [Fact]
    public async Task A_restarted_app_re_owns_and_adopts_the_working_oco_without_sending_again()
    {
        using var fake = new FakeTradovate();
        string stopClient, targetClient;
        {
            var (feed, session, account) = await Connect(fake);
            using var _f = feed;
            using var _s = session;
            using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, account, "DEMO101", Context(), () => true);
            using var brackets = Brackets(broker);
            UseTargetOnly(brackets);
            OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
            await Until(() => brackets.Mode == ProtectionMode.ExchangeSide);
            stopClient = brackets.StopLoss!.ClientOrderId!;
            targetClient = brackets.Targets[0].ClientOrderId!;
        }
        fake.ReplayPositions.Add(JsonNode.Parse("{\"id\":1,\"accountId\":101,\"contractId\":555,\"netPos\":2,\"netPrice\":5000.25,\"bought\":2,\"boughtValue\":10000.5,\"sold\":0,\"soldValue\":0}"));

        var (feed2, session2, account2) = await Connect(fake);
        using var _f2 = feed2;
        using var _s2 = session2;
        using var restarted = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session2, account2, "DEMO101", Context(), () => true);
        using var adopted = Brackets(restarted);
        UseTargetOnly(adopted);
        await Until(() => adopted.Mode == ProtectionMode.ExchangeSide, () => $"mode {adopted.Mode}; error {restarted.LastError}; position {restarted.Position.Volume}");

        var stop = adopted.StopLoss!;
        var target = Assert.Single(adopted.Targets);
        Assert.Equal(stopClient, stop.ClientOrderId);
        Assert.Equal(targetClient, target.ClientOrderId);
        Assert.Equal(restarted.OrderContext.ManualOwner.Id, stop.OwnerId);
        Assert.Equal(restarted.OrderContext.ManualOwner.Id, target.OwnerId);
        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder"));

        await fake.FillOcoLeg(fake.OcoLegFor(stopClient));
        await Until(() => restarted.Position.Volume == 0 && target.Canceled);
        Assert.Equal(1, fake.OcoRequests);
        Assert.Equal(1, Count(fake, "placeorder"));
    }

    public void Dispose()
    {
        // Execution projection outlives the adapters and may still write their journals; delete after it settles.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!AccountExecutionProjector.BackgroundWorkIdle && DateTime.UtcNow < deadline) Thread.Sleep(20);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
