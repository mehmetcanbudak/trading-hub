using System.Globalization;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// BinanceBroker against a fake Binance REST server (USD-M futures and spot routes). The order handle a caller gets
/// back must follow its order at the exchange until it finishes, also across Modify (cancel and replace).
/// </summary>
public class BinanceBrokerTests
{
    private static readonly Instrument BtcUsdt = new() { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static async Task Until(Func<bool> condition, int ms = 12000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    /// <summary>Waits until the broker has fetched the open orders at least <paramref name="count"/> more times.</summary>
    private static async Task Polls(FakeBinance fake, int count)
    {
        int start = fake.OpenOrderPolls;
        await Until(() => fake.OpenOrderPolls >= start + count);
    }

    private static BinanceBroker Broker(FakeBinance fake) => new(new InstrumentData(BtcUsdt, new SimulatedFeed()), fake.Settings, "Binance test");

    private static long IdOf(Order o) => long.Parse(o.ExchangeOrderId, Inv);

    [Fact]
    public async Task Futures_market_order_handle_reaches_Filled_after_it_leaves_the_open_list()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Market(OrderDirections.Buy, 0.01m, "Test: entry");
        await Until(() => order.Status == OrderStatus.Filled);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(OrderStates.Done, order.State);
        Assert.Equal(0m, order.Unfilled);
        Assert.Equal(0.01m, order.Filled);
        var placed = Assert.Single(fake.Placed);
        Assert.Equal(placed.Id.ToString(Inv), order.ExchangeOrderId);
        Assert.Contains("GET /fapi/v1/order", fake.Calls); // the final state is looked up with a signed request
        Assert.DoesNotContain(order, broker.ActiveOrders);
    }

    [Fact]
    public async Task Spot_orders_reach_their_final_state_through_the_api_v3_routes()
    {
        using var fake = new FakeBinance(futures: false);
        using var broker = Broker(fake);
        var market = broker.Market(OrderDirections.Buy, 0.5m);
        var limit = broker.Limit(OrderDirections.Sell, 0.5m, 70000m);
        await Until(() => market.Status == OrderStatus.Filled && limit.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(limit));

        Assert.Equal(OrderStatus.Filled, market.Status); // the spot answer for a market order already says FILLED
        Assert.Equal(0m, market.Unfilled);
        Assert.Contains(limit, broker.ActiveOrders);

        fake.Fill(IdOf(limit), 0.5m);
        await Until(() => limit.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, limit.Status);
        Assert.Equal(OrderStates.Done, limit.State);
        Assert.Equal(0.5m, limit.Filled);
        Assert.DoesNotContain(limit, broker.ActiveOrders);
        Assert.Contains("GET /api/v3/order", fake.Calls);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("/fapi/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_fill_then_full_fill_update_the_same_handle()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Limit(OrderDirections.Buy, 3m, 60000m, "Test: entry");
        await Until(() => order.ExchangeOrderId.Length > 0);
        long id = IdOf(order);

        fake.Fill(id, 1m);
        await Until(() => order.Unfilled == 2m);
        Assert.Equal(2m, order.Unfilled);
        Assert.Equal(1m, order.Filled);
        Assert.Equal(OrderStatus.PartlyFilled, order.Status);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));

        fake.Fill(id, 2m);
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(3m, order.Filled);
        Assert.Equal(0m, order.Unfilled);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task Cancel_finishes_the_handle_as_Canceled_and_keeps_its_partial_fill()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Limit(OrderDirections.Sell, 2m, 61000m);
        await Until(() => order.ExchangeOrderId.Length > 0);
        long id = IdOf(order);
        fake.Fill(id, 0.5m); // filled a little right before the cancel

        broker.Cancel(order);
        await Until(() => order.Status == OrderStatus.Canceled);
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Equal(OrderStates.Done, order.State);
        Assert.Equal(0.5m, order.Filled);
        Assert.Equal("CANCELED", fake.Order(id).Status);
        Assert.DoesNotContain(order, broker.ActiveOrders);
    }

    [Fact]
    public async Task Cancel_sent_before_Binance_answers_the_submission_still_cancels_the_order()
    {
        using var fake = new FakeBinance(futures: true) { PostDelayMs = 700 };
        using var broker = Broker(fake);
        var stop = broker.Stop(OrderDirections.Sell, 1m, 55000m, "Test: stop");
        broker.Cancel(stop); // no exchange order id yet
        await Until(() => stop.Status == OrderStatus.Canceled);

        Assert.Equal(OrderStatus.Canceled, stop.Status);
        Assert.Empty(fake.OpenOrders);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task Modify_keeps_one_handle_that_follows_the_replacement_order()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 2m, 59000m, "Test: Stop loss");
        await Until(() => stop.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(stop));
        string first = stop.ExchangeOrderId;

        broker.Modify(stop, 59500m);
        await Until(() => stop.ExchangeOrderId != first && stop.ExchangeOrderId.Length > 0);
        Assert.NotEqual(first, stop.ExchangeOrderId);
        Assert.Equal(59500m, stop.TriggerPrice);
        Assert.Equal(OrderStates.Active, stop.State);
        Assert.Equal("CANCELED", fake.Order(long.Parse(first, Inv)).Status);

        // Replace returns the same handle as well
        string second = stop.ExchangeOrderId;
        Assert.Same(stop, broker.Replace(stop, 59600m, stop.Unfilled));
        await Until(() => stop.ExchangeOrderId != second);
        var open = Assert.Single(fake.OpenOrders);
        Assert.Equal(open.Id.ToString(Inv), stop.ExchangeOrderId);
        Assert.Equal(59600m, open.StopPrice);
        Assert.Equal(2m, open.Qty);
        Assert.True(open.ReduceOnly);

        await Polls(fake, 2);
        Assert.Same(stop, Assert.Single(broker.ActiveOrders)); // no orphan replacement object
        Assert.Equal(59600m, stop.TriggerPrice);

        fake.Fill(open.Id, 2m);
        await Until(() => stop.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, stop.Status);
        Assert.Equal(2m, stop.Filled);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task Modify_after_a_partial_fill_replaces_only_the_rest()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Limit(OrderDirections.Buy, 3m, 60000m);
        await Until(() => order.ExchangeOrderId.Length > 0);
        string first = order.ExchangeOrderId;
        fake.Fill(IdOf(order), 1m);
        await Until(() => order.Unfilled == 2m);

        broker.Modify(order, 60100m);
        await Until(() => order.ExchangeOrderId != first);
        var open = Assert.Single(fake.OpenOrders);
        Assert.Equal(2m, open.Qty);
        Assert.Equal(60100m, open.Price);
        Assert.Equal(open.Id.ToString(Inv), order.ExchangeOrderId);
        Assert.Equal(3m, order.QuantityToFill);
        Assert.Equal(1m, order.Filled);
        Assert.Equal(OrderStatus.PartlyFilled, order.Status);

        fake.Fill(open.Id, 2m);
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(3m, order.Filled);
    }

    [Fact]
    public async Task Modify_of_an_order_that_already_filled_does_not_send_a_new_order()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1m, 59000m, "Test: Stop loss");
        await Until(() => stop.ExchangeOrderId.Length > 0);
        fake.Fill(IdOf(stop), 1m); // triggered and filled before the broker's next poll

        broker.Modify(stop, 59200m); // the cancel fails at Binance: nothing is replaced
        await Until(() => stop.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, stop.Status);
        Assert.Equal(1m, stop.Filled);
        await Polls(fake, 1);
        Assert.Single(fake.Placed);
        Assert.Empty(fake.OpenOrders);
    }

    [Fact]
    public async Task Modify_refused_because_the_order_just_filled_sends_no_replacement()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1m, 59000m, "Test: Stop loss");
        await Until(() => stop.ExchangeOrderId.Length > 0);
        fake.FillWhenCanceled(IdOf(stop)); // it triggers and fills just before the cancel arrives

        broker.Modify(stop, 59200m);
        await Until(() => stop.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, stop.Status);
        Assert.Equal(1m, stop.Filled);
        Assert.Contains("DELETE /fapi/v1/order", fake.Calls); // the cancel was tried and refused (-2011)
        await Polls(fake, 1);
        Assert.Single(fake.Placed);
        Assert.Empty(fake.OpenOrders);
    }

    [Fact]
    public async Task External_open_orders_are_listed_with_stable_handles_until_they_finish()
    {
        using var fake = new FakeBinance(futures: true);
        long manual = fake.AddExternal("BUY", "LIMIT", 1m, 50000m);
        long old = fake.AddExternal("SELL", "STOP_MARKET", 0.2m, 0m, stopPrice: 52000m);
        using var broker = Broker(fake);
        await Until(() => broker.ActiveOrders.Count() == 2);

        var ext = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == manual.ToString(Inv));
        Assert.Equal(OrderDirections.Buy, ext.Direction);
        Assert.Equal(OrderTypes.Limit, ext.Type);
        Assert.Equal(50000m, ext.Price);
        Assert.Equal(1m, ext.Unfilled);
        Assert.Equal(OrderStatus.Placed, ext.Status);
        Assert.Equal("", ext.Comment);
        var stop = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == old.ToString(Inv));
        Assert.Equal(OrderTypes.Stop, stop.Type);
        Assert.Equal(52000m, stop.TriggerPrice);

        // an order sent through the broker is listed next to them
        var own = broker.Limit(OrderDirections.Sell, 1m, 70000m, "Test: target");
        await Until(() => own.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(own));
        await Polls(fake, 2);
        Assert.Equal(3, broker.ActiveOrders.Count());
        Assert.Same(ext, broker.ActiveOrders.Single(o => o.ExchangeOrderId == manual.ToString(Inv)));

        // canceled outside Trading Hub; the older one is no longer known to Binance at all
        fake.CancelAtExchange(manual);
        fake.CancelAtExchange(old, forget: true);
        await Until(() => ext.Status == OrderStatus.Canceled && stop.Status == OrderStatus.Canceled);
        Assert.Equal(OrderStatus.Canceled, ext.Status);
        Assert.Equal(OrderStatus.Canceled, stop.Status);
        Assert.Equal(0m, stop.Filled);
        Assert.Same(own, Assert.Single(broker.ActiveOrders));
    }

    [Fact]
    public async Task A_replacement_that_Binance_refuses_finishes_the_handle_as_Rejected()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1m, 59000m, "Test: Stop loss");
        await Until(() => stop.ExchangeOrderId.Length > 0);
        string first = stop.ExchangeOrderId;

        fake.RejectPosts = 1; // the new stop would trigger at once
        broker.Modify(stop, 61000m);
        await Until(() => stop.State == OrderStates.Failed);
        Assert.Equal(OrderStates.Failed, stop.State);
        Assert.Equal(OrderStatus.Rejected, stop.Status);
        Assert.Equal(first, stop.ExchangeOrderId);
        Assert.Equal("CANCELED", fake.Order(long.Parse(first, Inv)).Status); // the old stop is gone at the exchange
        Assert.Empty(fake.OpenOrders);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task A_request_timeout_is_reported_and_polling_goes_on()
    {
        using var fake = new FakeBinance(futures: true) { StallOpenOrdersOnceMs = 5000 };
        long manual = fake.AddExternal("BUY", "LIMIT", 1m, 50000m);
        var settings = fake.Settings;
        settings.RequestTimeout = TimeSpan.FromSeconds(2); // the first open-order request times out
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance test");
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        broker.Changed += () =>
        {
            if (broker.LastError is { } e) errors.Enqueue(e);
        };

        await Until(() => broker.ActiveOrders.Any(o => o.ExchangeOrderId == manual.ToString(Inv)));
        Assert.Contains(broker.ActiveOrders, o => o.ExchangeOrderId == manual.ToString(Inv)); // a later poll worked
        Assert.Contains(errors, e => e.Contains("Timeout", StringComparison.OrdinalIgnoreCase)); // the timeout was reported
        Assert.True(fake.Calls.Count(c => c == "GET /fapi/v1/openOrders") >= 2); // the stalled request was not the last
    }

    [Fact]
    public async Task Replace_shows_the_new_price_and_quantity_at_once_and_polls_keep_them()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var target = broker.Exit(OrderTypes.Limit, OrderDirections.Sell, 2m, 61000m, "Test: Take profit");
        await Until(() => target.ExchangeOrderId.Length > 0);
        string first = target.ExchangeOrderId;

        fake.PostDelayMs = 6000; // the replacement takes a while to be acknowledged
        Assert.Same(target, broker.Replace(target, 60500m, 1m));
        Assert.Equal(60500m, target.Price); // like the other brokers, the handle shows the request at once
        Assert.Equal(1m, target.Unfilled);
        Assert.Equal(1m, target.QuantityToFill);

        await Polls(fake, 1);
        await Task.Delay(300);
        Assert.Equal(first, target.ExchangeOrderId); // still waiting for Binance's answer
        Assert.Equal(60500m, target.Price); // a poll meanwhile did not put the old order's values back
        Assert.Equal(1m, target.Unfilled);
        Assert.Same(target, Assert.Single(broker.ActiveOrders)); // the new order is not taken for someone else's

        await Until(() => target.ExchangeOrderId != first);
        var open = Assert.Single(fake.OpenOrders);
        Assert.Equal(open.Id.ToString(Inv), target.ExchangeOrderId);
        Assert.Equal(1m, open.Qty);
        Assert.Equal(60500m, open.Price);
        await Polls(fake, 2);
        Assert.Equal(60500m, target.Price);
        Assert.Equal(1m, target.Unfilled);
        Assert.Same(target, Assert.Single(broker.ActiveOrders));
    }

    [Fact]
    public async Task A_cancel_that_times_out_but_went_through_still_gets_its_replacement()
    {
        using var fake = new FakeBinance(futures: true);
        var settings = fake.Settings;
        settings.RequestTimeout = TimeSpan.FromSeconds(2);
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance test");
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 2m, 59000m, "Test: Stop loss");
        await Until(() => stop.ExchangeOrderId.Length > 0);
        string first = stop.ExchangeOrderId;

        fake.StallCancelOnceMs = 5000; // Binance cancels it, but the answer never arrives in time
        broker.Modify(stop, 59500m);
        await Until(() => stop.ExchangeOrderId != first);
        Assert.NotEqual(first, stop.ExchangeOrderId);
        Assert.Equal("CANCELED", fake.Order(long.Parse(first, Inv)).Status);
        var open = Assert.Single(fake.OpenOrders);
        Assert.Equal(open.Id.ToString(Inv), stop.ExchangeOrderId);
        Assert.Equal(59500m, open.StopPrice);
        Assert.Equal(2m, open.Qty);
        Assert.Equal(OrderStates.Active, stop.State);
        Assert.Equal(59500m, stop.TriggerPrice);
    }

    [Fact]
    public async Task A_Modify_whose_cancel_cannot_be_confirmed_reports_the_lost_stop_as_rejected()
    {
        using var fake = new FakeBinance(futures: true);
        var settings = fake.Settings;
        settings.RequestTimeout = TimeSpan.FromSeconds(2);
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance test");
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        broker.Changed += () =>
        {
            if (broker.LastError is { } e) errors.Enqueue(e);
        };
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1m, 59000m, "Test: Stop loss");
        await Until(() => stop.ExchangeOrderId.Length > 0);

        await Polls(fake, 1); // no lookup from an earlier poll is still on its way
        fake.StallCancelOnceMs = 5000; // canceled at Binance, answer too late…
        fake.FailLookups = 1; // …and asking right away fails too
        broker.Modify(stop, 59500m);
        await Until(() => stop.State == OrderStates.Failed);
        Assert.Equal(OrderStates.Failed, stop.State); // not a silent Canceled: the stop is gone
        Assert.Equal(OrderStatus.Rejected, stop.Status);
        Assert.Contains(errors, e => e.Contains("replacement was not sent", StringComparison.Ordinal));
        Assert.Single(fake.Placed);
        Assert.Empty(fake.OpenOrders);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task An_unanswered_cancel_that_Binance_carries_out_later_is_reported_as_a_failed_Modify()
    {
        using var fake = new FakeBinance(futures: true);
        var settings = fake.Settings;
        settings.RequestTimeout = TimeSpan.FromSeconds(2);
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance test");
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        broker.Changed += () =>
        {
            if (broker.LastError is { } e) errors.Enqueue(e);
        };
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1m, 59000m, "Test: Stop loss");
        await Until(() => stop.ExchangeOrderId.Length > 0);
        long id = IdOf(stop);

        fake.StallCancelOnceMs = 5000;
        fake.StalledCancelIsCarriedOut = false; // not done yet when the client gives up
        broker.Modify(stop, 59500m);
        Assert.Equal(59500m, stop.TriggerPrice); // the request shows at once
        await Until(() => stop.TriggerPrice == 59000m); // asked right away: still working, so not replaced
        await Polls(fake, 1);
        Assert.Equal(OrderStates.Active, stop.State);
        Assert.Equal(59000m, stop.TriggerPrice); // not replaced: shown as it is at Binance again
        Assert.Single(fake.Placed);

        fake.CancelAtExchange(id); // Binance carries out the cancel late
        await Until(() => stop.State == OrderStates.Failed);
        Assert.Equal(OrderStatus.Rejected, stop.Status); // not a silent Canceled
        Assert.Contains(errors, e => e.Contains("replacement was not sent", StringComparison.Ordinal));
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task Cancel_of_a_market_order_sends_nothing_and_its_fill_is_reported()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Market(OrderDirections.Sell, 0.02m, "Test: exit");
        broker.Cancel(order); // it cannot be canceled: it fills at once
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(0.02m, order.Filled);
        Assert.DoesNotContain("DELETE /fapi/v1/order", fake.Calls);
    }
}

/// <summary>One order at the fake exchange.</summary>
internal sealed record FakeBinanceOrder(long Id, string Symbol, string Side, string Type, decimal Qty, decimal Price, decimal StopPrice, bool ReduceOnly, bool External)
{
    public decimal Executed { get; set; }
    public string Status { get; set; } = "NEW";
    public bool IsOpen => Status is "NEW" or "PARTIALLY_FILLED";
}

/// <summary>
/// Binance REST on a local fake server (USD-M futures or spot routes). Signed private calls are checked against the
/// secret; the test moves orders (fills, cancels at the exchange) to see how the broker follows them.
/// </summary>
internal sealed class FakeBinance : IDisposable
{
    public const string Key = "fake-api-key", Secret = "fake-api-secret";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly FakeHttpServer _server = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, FakeBinanceOrder> _orders = new();
    private readonly HashSet<long> _forgotten = new();
    private readonly HashSet<long> _fillOnCancel = new();
    private readonly List<string> _calls = new();
    private long _nextId = 7000;
    private int _openOrderPolls;

    public FakeBinance(bool futures)
    {
        Futures = futures;
        _server.OnHttp = Handle;
    }

    public bool Futures { get; }

    /// <summary>Delay before answering a new order (the order exists at the exchange meanwhile).</summary>
    public int PostDelayMs { get; set; }

    /// <summary>How many of the next new orders Binance refuses.</summary>
    public int RejectPosts { get; set; }

    /// <summary>The next open-order request waits this long before answering (a network stall).</summary>
    public int StallOpenOrdersOnceMs { get; set; }

    /// <summary>The next cancel is carried out, but its answer waits this long (the client gives up first).</summary>
    public int StallCancelOnceMs { get; set; }

    /// <summary>How many of the next order lookups fail with a server error.</summary>
    public int FailLookups { get; set; }

    /// <summary>Whether a stalled cancel is carried out before its late answer (otherwise it is not done at all).</summary>
    public bool StalledCancelIsCarriedOut { get; set; } = true;

    /// <summary>The order fills just before a cancel for it arrives, so Binance refuses the cancel (-2011).</summary>
    public void FillWhenCanceled(long id)
    {
        lock (_gate) _fillOnCancel.Add(id);
    }

    public BinanceAccountSettings Settings => new() { Futures = Futures, Testnet = true, ApiKey = Key, ApiSecret = Secret, BaseUrl = _server.Http };

    public int OpenOrderPolls
    {
        get
        {
            lock (_gate) return _openOrderPolls;
        }
    }

    /// <summary>Requests received, as "METHOD /route".</summary>
    public List<string> Calls
    {
        get
        {
            lock (_gate) return _calls.ToList();
        }
    }

    /// <summary>Orders sent by the broker, oldest first.</summary>
    public List<FakeBinanceOrder> Placed
    {
        get
        {
            lock (_gate) return _orders.Values.Where(o => !o.External).OrderBy(o => o.Id).Select(o => o with { }).ToList();
        }
    }

    public List<FakeBinanceOrder> OpenOrders
    {
        get
        {
            lock (_gate) return _orders.Values.Where(o => o.IsOpen).OrderBy(o => o.Id).Select(o => o with { }).ToList();
        }
    }

    public FakeBinanceOrder Order(long id)
    {
        lock (_gate) return _orders[id] with { };
    }

    /// <summary>An order placed outside Trading Hub (website, another app).</summary>
    public long AddExternal(string side, string type, decimal qty, decimal price, decimal stopPrice = 0)
    {
        lock (_gate)
        {
            var o = new FakeBinanceOrder(++_nextId, "BTCUSDT", side, type, qty, price, stopPrice, false, External: true);
            _orders[o.Id] = o;
            return o.Id;
        }
    }

    public void Fill(long id, decimal qty)
    {
        lock (_gate)
        {
            var o = _orders[id];
            o.Executed += qty;
            o.Status = o.Executed >= o.Qty ? "FILLED" : "PARTIALLY_FILLED";
        }
    }

    /// <summary>Canceled at the exchange; <paramref name="forget"/>: Binance no longer returns it when asked.</summary>
    public void CancelAtExchange(long id, bool forget = false)
    {
        lock (_gate)
        {
            _orders[id].Status = "CANCELED";
            if (forget) _forgotten.Add(id);
        }
    }

    private static decimal Dec(string s) => s.Length == 0 ? 0 : decimal.Parse(s, NumberStyles.Float, Inv);

    private static string Num(decimal v) => v.ToString("0.########", Inv);

    private static string Json(FakeBinanceOrder o) => string.Create(Inv,
        $"{{\"orderId\":{o.Id},\"symbol\":\"{o.Symbol}\",\"status\":\"{o.Status}\",\"side\":\"{o.Side}\",\"type\":\"{o.Type}\",\"price\":\"{Num(o.Price)}\",\"stopPrice\":\"{Num(o.StopPrice)}\",\"origQty\":\"{Num(o.Qty)}\",\"executedQty\":\"{Num(o.Executed)}\",\"reduceOnly\":{(o.ReduceOnly ? "true" : "false")},\"time\":1790000000000,\"updateTime\":1790000000000}}");

    private static (int, string) Error(int code, string msg) => (400, $"{{\"code\":{code},\"msg\":\"{msg}\"}}");

    private static bool Signed(FakeRequest r)
    {
        if (r.Headers.GetValueOrDefault("X-MBX-APIKEY") != Key) return false;
        int q = r.Path.IndexOf('?');
        if (q < 0) return false;
        string query = r.Path[(q + 1)..];
        const string marker = "&signature=";
        int s = query.LastIndexOf(marker, StringComparison.Ordinal);
        return s > 0 && query[(s + marker.Length)..] == BinanceAccountClient.Sign(query[..s], Secret);
    }

    private (int Status, string Body) Handle(FakeRequest r)
    {
        string route = r.Route;
        lock (_gate) _calls.Add(r.Method + " " + route);
        if (!route.StartsWith(Futures ? "/fapi/" : "/api/v3/", StringComparison.Ordinal)) return Error(-1, "Wrong market route.");
        if (route is "/fapi/v1/time" or "/api/v3/time") return (200, $"{{\"serverTime\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
        if (!Signed(r)) return (401, "{\"code\":-1022,\"msg\":\"Signature for this request is not valid.\"}");
        if (r.Method == "POST" && PostDelayMs > 0)
        {
            var placed = Place(r);
            Thread.Sleep(PostDelayMs);
            return placed;
        }

        if (r.Method == "GET" && route is "/fapi/v1/openOrders" or "/api/v3/openOrders")
        {
            int stall;
            lock (_gate)
            {
                stall = StallOpenOrdersOnceMs;
                StallOpenOrdersOnceMs = 0;
            }

            if (stall > 0) Thread.Sleep(stall);
        }

        if (r.Method == "DELETE" && route is "/fapi/v1/order" or "/api/v3/order")
        {
            int stall;
            lock (_gate)
            {
                stall = StallCancelOnceMs;
                StallCancelOnceMs = 0;
            }

            if (stall > 0)
            {
                // carried out at once (or not at all), answered late
                var answer = StalledCancelIsCarriedOut ? CancelOne(r) : Error(-1007, "Timeout waiting for response from backend server. Send status unknown; execution status unknown.");
                Thread.Sleep(stall);
                return answer;
            }
        }

        lock (_gate)
        {
            switch (r.Method, route)
            {
                case ("GET", "/fapi/v1/openOrders" or "/api/v3/openOrders"):
                    _openOrderPolls++;
                    return (200, "[" + string.Join(",", _orders.Values.Where(o => o.IsOpen && o.Symbol == r.Query("symbol")).OrderBy(o => o.Id).Select(Json)) + "]");
                case ("POST", "/fapi/v1/order" or "/api/v3/order"):
                    return Place(r);
                case ("DELETE", "/fapi/v1/order" or "/api/v3/order"):
                    return CancelOne(r);
                case ("GET", "/fapi/v1/order" or "/api/v3/order"):
                {
                    long id = long.Parse(r.Query("orderId"), Inv);
                    if (FailLookups > 0)
                    {
                        FailLookups--;
                        return (500, "{\"code\":-1001,\"msg\":\"Internal error; unable to process your request. Please try again.\"}");
                    }

                    if (!_orders.TryGetValue(id, out var o) || _forgotten.Contains(id)) return Error(-2013, "Order does not exist.");
                    return (200, Json(o));
                }
                case ("GET", "/fapi/v2/positionRisk"):
                    return (200, "[]");
                case ("DELETE", "/fapi/v1/allOpenOrders" or "/api/v3/openOrders"):
                {
                    var canceled = _orders.Values.Where(o => o.IsOpen && o.Symbol == r.Query("symbol")).ToList();
                    foreach (var o in canceled) o.Status = "CANCELED";
                    return Futures ? (200, "{\"code\":200,\"msg\":\"The operation of cancel all open order is done.\"}") : (200, "[" + string.Join(",", canceled.Select(Json)) + "]");
                }
            }
        }

        return Error(-1, "Unknown route.");
    }

    private (int, string) CancelOne(FakeRequest r)
    {
        lock (_gate)
        {
            long id = long.Parse(r.Query("orderId"), Inv);
            if (!_orders.TryGetValue(id, out var o) || !o.IsOpen) return Error(-2011, "Unknown order sent.");
            if (_fillOnCancel.Remove(id))
            {
                o.Executed = o.Qty;
                o.Status = "FILLED";
                return Error(-2011, "Unknown order sent.");
            }

            o.Status = "CANCELED";
            return (200, Json(o));
        }
    }

    private (int, string) Place(FakeRequest r)
    {
        lock (_gate)
        {
            if (RejectPosts > 0)
            {
                RejectPosts--;
                return Error(-2021, "Order would immediately trigger.");
            }

            string type = r.Query("type");
            var o = new FakeBinanceOrder(++_nextId, r.Query("symbol"), r.Query("side"), type, Dec(r.Query("quantity")), Dec(r.Query("price")), Dec(r.Query("stopPrice")), r.Query("reduceOnly") == "true", External: false);
            _orders[o.Id] = o;
            if (type == "MARKET")
            {
                // the matching engine fills it at once
                o.Executed = o.Qty;
                o.Status = "FILLED";
            }

            // futures answer ACK by default (accepted, nothing more); spot answers FULL for market and limit orders, ACK otherwise
            if (Futures) return (200, Json(o with { Executed = 0, Status = "NEW" }));
            return (200, type is "MARKET" or "LIMIT" ? Json(o) : $"{{\"symbol\":\"{o.Symbol}\",\"orderId\":{o.Id},\"orderListId\":-1,\"clientOrderId\":\"fake\",\"transactTime\":1790000000000}}");
        }
    }

    public void Dispose() => _server.Dispose();
}
