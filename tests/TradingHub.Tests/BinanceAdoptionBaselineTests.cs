using TradingHub.Feeds.Binance;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Dragging a partly filled order placed on Binance's website adopts it. Its executions before the drag belong to the
/// website, not to Trading Hub: the adoption must read them from Binance first, record them as the excluded prefix and
/// own only later fills, or refuse the drag and leave the order unowned. Either way new exposure must not stay paused.
/// </summary>
public partial class BinanceBrokerTests
{
    private static bool Ready(BinanceBroker broker) =>
        broker.Executions.Capture(ExecutionCursor.Start).Availability == ExecutionAvailability.Ready;

    private async Task<(FakeBinance Fake, BinanceBroker Broker, Order Order, long WebsiteId)> PartlyFilledWebsiteLimit()
    {
        var fake = new FakeBinance(futures: true);
        long website = fake.AddExternal("BUY", "LIMIT", 3m, 59000m);
        fake.Fill(website, 1m);
        var broker = Broker(fake);
        await Observed(broker, fake, () => broker.OrdersReady && broker.ActiveOrders.Any(o => o.Filled == 1m) && Ready(broker));
        var order = Assert.Single(broker.ActiveOrders);
        Assert.True(string.IsNullOrEmpty(order.OwnerId));
        return (fake, broker, order, website);
    }

    [Fact]
    public async Task Dragging_a_partly_filled_website_limit_adopts_its_exchange_execution_baseline_and_returns_to_Ready()
    {
        var (fake, broker, order, website) = await PartlyFilledWebsiteLimit();
        using var _ = fake;
        using var __ = broker;
        var websiteFill = Assert.Single(fake.FuturesTradeHistory);

        OrderActions.Reprice(broker, order, 59100m);
        await Observed(broker, fake, () => fake.Placed.Count == 1 && order.ExchangeOrderId != website.ToString(Inv) &&
            order.ExchangeOrderId.Length > 0 && !broker.IsReplacementPending(order));
        var adopted = Assert.IsType<OwnedOrderIntent>(broker.OrderContext.Find(order.LogicalOrderId));
        Assert.Equal((true, true, 1m, broker.OrderContext.ManualOwner),
            (adopted.Adopted, adopted.BaselineKnown, adopted.QuantityBaseline, adopted.Owner));
        Assert.Equal(new[] { websiteFill.Id.ToString(Inv) }, adopted.PriorExecutions!.Select(prior => prior.Id));
        Assert.Equal("CANCELED", fake.Order(website).Status);
        var replacement = Assert.Single(fake.OpenOrders);
        Assert.Equal((2m, 59100m), (replacement.Qty, replacement.Price));

        // Only the fill after adoption is owned; its trade covers it and new exposure is no longer paused.
        fake.Fill(replacement.Id, 2m);
        var ownedFill = fake.FuturesTradeHistory.Last();
        await Observed(broker, fake, () => order.Status == OrderStatus.Filled && Ready(broker) &&
            broker.Executions.Capture(ExecutionCursor.Start).Changes.Any(report => report.Identity.Id == ownedFill.Id.ToString(Inv)));
        await Polls(fake, 2);
        var reports = broker.Executions.Capture(ExecutionCursor.Start).Changes;
        Assert.DoesNotContain(reports, report => report.Identity.Id == websiteFill.Id.ToString(Inv));
        Assert.Equal(adopted.LogicalOrderId, Assert.IsType<OwnedOrderIntent>(
            broker.OrderContext.Find(Assert.Single(reports, report => report.Identity.Id == ownedFill.Id.ToString(Inv)).IntentId)).LogicalOrderId);
        Assert.Equal((OrderStatus.Filled, 3m), (order.Status, order.Filled));
        Assert.True(Ready(broker));
        Assert.False(broker.OrderContext.HasStorageFault);
    }

    [Fact]
    public async Task A_fill_while_the_adoption_baseline_is_read_refuses_the_drag_and_a_later_drag_adopts_the_new_baseline()
    {
        var (fake, broker, order, website) = await PartlyFilledWebsiteLimit();
        using var _ = fake;
        using var __ = broker;
        int raced = 0;
        // The website order executes again right after Binance answered the adoption's trade read for it.
        fake.BeforeGetReply = (request, _) =>
        {
            if (request.Route == "/fapi/v1/userTrades" && request.Query("orderId") == website.ToString(Inv) &&
                Interlocked.Exchange(ref raced, 1) == 0)
                fake.Fill(website, 1m);
            return Task.CompletedTask;
        };
        OrderActions.Reprice(broker, order, 59100m);
        await Observed(broker, fake, () => broker.LastError ==
            "Not modified: the order executed while its earlier executions were being read. It was left unowned and unchanged; try again." &&
            !broker.IsReplacementPending(order));
        fake.BeforeGetReply = null;
        Assert.True(string.IsNullOrEmpty(order.OwnerId));
        Assert.Null(NativeClaim(broker.OrderContext, BtcUsdt, order.NativeIdentity!));
        Assert.DoesNotContain("DELETE /fapi/v1/order", fake.Calls);
        Assert.Empty(fake.Sent);
        await Observed(broker, fake, () => order.Filled == 2m && Ready(broker));

        OrderActions.Reprice(broker, order, 59100m);
        await Observed(broker, fake, () => fake.Placed.Count == 1 && order.ExchangeOrderId != website.ToString(Inv) &&
            order.ExchangeOrderId.Length > 0 && !broker.IsReplacementPending(order));
        var adopted = Assert.IsType<OwnedOrderIntent>(broker.OrderContext.Find(order.LogicalOrderId));
        Assert.Equal((true, 2m), (adopted.BaselineKnown, adopted.QuantityBaseline));
        Assert.Equal(fake.FuturesTradeHistory.Select(trade => trade.Id.ToString(Inv)), adopted.PriorExecutions!.Select(prior => prior.Id));
        Assert.Equal(1m, Assert.Single(fake.OpenOrders).Qty);
        await Observed(broker, fake, () => Ready(broker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_baseline_read_refuses_the_drag_fail_closed_and_keeps_the_producer_Ready(bool historyMissesExecutions)
    {
        var (fake, broker, order, website) = await PartlyFilledWebsiteLimit();
        using var _ = fake;
        using var __ = broker;
        // Either the order's trade read fails, or it answers without the execution Binance reports as executed (for
        // example one older than USD-M's default seven-day window).
        fake.OverrideGetReply = request => request.Route == "/fapi/v1/userTrades" && request.Query("orderId") == website.ToString(Inv)
            ? historyMissesExecutions ? (200, "[]")
                : (503, "{\"code\":-1001,\"msg\":\"Internal error; unable to process your request. Please try again.\"}")
            : null;
        OrderActions.Reprice(broker, order, 59100m);
        await Observed(broker, fake, () => (historyMissesExecutions
            ? broker.LastError == "Not modified: Binance's trade history does not account for this order's executed quantity. It was left unowned and unchanged."
            : broker.LastError?.StartsWith("Not modified: Binance could not confirm this order's earlier executions (", StringComparison.Ordinal) == true) &&
            !broker.IsReplacementPending(order));
        fake.OverrideGetReply = null;
        Assert.True(string.IsNullOrEmpty(order.OwnerId));
        Assert.Null(NativeClaim(broker.OrderContext, BtcUsdt, order.NativeIdentity!));
        Assert.DoesNotContain("DELETE /fapi/v1/order", fake.Calls);
        Assert.Empty(fake.Sent);
        Assert.Equal(("PARTIALLY_FILLED", 59000m), (fake.Order(website).Status, fake.Order(website).Price));

        // The refused order stays the website's: its later fills are not Trading Hub's and never pause new exposure.
        fake.Fill(website, 2m);
        await Observed(broker, fake, () => order.Status == OrderStatus.Filled && Ready(broker));
        await Polls(fake, 2);
        Assert.True(Ready(broker));
        Assert.True(string.IsNullOrEmpty(order.OwnerId));
    }

    private const string OrderNotReturned =
        "Not modified: Binance did not return this order, so its earlier executions cannot be confirmed. It was left unowned and unchanged.";
    private const string HistoryBeyondLimit =
        "Not modified: Binance keeps USD-M futures executions for only three months, and this order is older. It was left unowned and unchanged.";

    /// <summary>The drag was refused and changed nothing: the order is still the website's, unbound and working as it was.</summary>
    private static void LeftUnownedAndUnchanged(FakeBinance fake, BinanceBroker broker, Order order, long website, decimal price)
    {
        Assert.True(string.IsNullOrEmpty(order.OwnerId));
        Assert.Null(NativeClaim(broker.OrderContext, BtcUsdt, order.NativeIdentity!));
        Assert.DoesNotContain(fake.Calls, call => call.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.Empty(fake.Sent);
        Assert.Equal(("PARTIALLY_FILLED", price), (fake.Order(website).Status, fake.Order(website).Price));
    }

    [Fact]
    public async Task A_drag_before_the_next_poll_shows_a_website_fill_adopts_the_exchange_baseline_not_the_stale_local_one()
    {
        using var fake = new FakeBinance(futures: true);
        long website = fake.AddExternal("BUY", "LIMIT", 3m, 59000m);
        using var broker = Broker(fake);
        await Observed(broker, fake, () => broker.OrdersReady && broker.ActiveOrders.Any() && Ready(broker));
        var order = Assert.Single(broker.ActiveOrders);

        // The website order executes after the last listing the broker saw, and the user drags it before the next poll.
        fake.FreezeListings();
        fake.Fill(website, 1m);
        var websiteFill = Assert.Single(fake.FuturesTradeHistory);
        await Polls(fake, 2);
        Assert.Equal((0m, OrderStates.Active), (order.Filled, order.State));
        Assert.True(string.IsNullOrEmpty(order.OwnerId));
        OrderActions.Reprice(broker, order, 59100m);
        fake.ThawListings();

        await Observed(broker, fake, () => fake.Placed.Count == 1 && order.ExchangeOrderId != website.ToString(Inv) &&
            order.ExchangeOrderId.Length > 0 && !broker.IsReplacementPending(order));
        var adopted = Assert.IsType<OwnedOrderIntent>(broker.OrderContext.Find(order.LogicalOrderId));
        Assert.Equal((true, true, 1m, broker.OrderContext.ManualOwner),
            (adopted.Adopted, adopted.BaselineKnown, adopted.QuantityBaseline, adopted.Owner));
        Assert.Equal(new[] { websiteFill.Id.ToString(Inv) }, adopted.PriorExecutions!.Select(prior => prior.Id));
        Assert.Equal((2m, 59100m), (Assert.Single(fake.OpenOrders).Qty, Assert.Single(fake.OpenOrders).Price));
        await Observed(broker, fake, () => Ready(broker));
        await Polls(fake, 2);
        Assert.DoesNotContain(broker.Executions.Capture(ExecutionCursor.Start).Changes,
            report => report.Identity.Id == websiteFill.Id.ToString(Inv));
        Assert.False(broker.OrderContext.HasStorageFault);
    }

    [Fact]
    public async Task An_unfilled_website_order_is_adopted_from_the_exchange_read_with_an_empty_prefix()
    {
        using var fake = new FakeBinance(futures: true);
        long website = fake.AddExternal("SELL", "LIMIT", 2m, 61000m);
        using var broker = Broker(fake);
        await Observed(broker, fake, () => broker.OrdersReady && broker.ActiveOrders.Any() && Ready(broker));
        var order = Assert.Single(broker.ActiveOrders);

        OrderActions.Reprice(broker, order, 60900m);
        await Observed(broker, fake, () => fake.Placed.Count == 1 && order.ExchangeOrderId != website.ToString(Inv) &&
            order.ExchangeOrderId.Length > 0 && !broker.IsReplacementPending(order));
        Assert.Contains("GET /fapi/v1/order", fake.Calls);
        var adopted = Assert.IsType<OwnedOrderIntent>(broker.OrderContext.Find(order.LogicalOrderId));
        Assert.Equal((true, true, 0m), (adopted.Adopted, adopted.BaselineKnown, adopted.QuantityBaseline));
        Assert.Empty(adopted.PriorExecutions!);
        Assert.Equal((2m, 60900m), (Assert.Single(fake.OpenOrders).Qty, Assert.Single(fake.OpenOrders).Price));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_drag_after_an_unpolled_trigger_and_fill_of_a_website_stop_is_refused_unowned(bool listedWhileTriggered)
    {
        using var fake = new FakeBinance(futures: true) { ListTriggeredAlgoOrders = listedWhileTriggered };
        long algoId = fake.AddExternalAlgo("BUY", "STOP", 3m, triggerPrice: 61000m, price: 61100m);
        using var broker = Broker(fake);
        await Observed(broker, fake, () => broker.OrdersReady && broker.ActiveOrders.Any() && Ready(broker));
        var order = Assert.Single(broker.ActiveOrders);

        // It triggers and its limit executes in part; the broker still lists it as the untriggered conditional order.
        fake.FreezeListings();
        long child = fake.Trigger(algoId, fill: 1m, staleListings: int.MaxValue);
        await Polls(fake, 2);
        Assert.Equal(("algo:" + algoId.ToString(Inv), 0m), (order.ExchangeOrderId, order.Filled));
        OrderActions.Reprice(broker, order, 61200m);
        await Observed(broker, fake, () => broker.LastError == "Not modified: the stop already triggered at Binance and its order is executing." &&
            !broker.IsReplacementPending(order));
        Assert.True(string.IsNullOrEmpty(order.OwnerId));
        Assert.Null(NativeClaim(broker.OrderContext, BtcUsdt, order.NativeIdentity!));
        Assert.DoesNotContain(fake.Calls, call => call.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.Empty(fake.Sent);
        Assert.Equal(("TRIGGERED", "PARTIALLY_FILLED"), (fake.AlgoOrder(algoId).Status, fake.Order(child).Status));
    }

    [Fact]
    public async Task An_order_Binance_no_longer_returns_is_refused_as_not_returned_rather_than_finished()
    {
        // USD-M Query Order stops finding an order 90 days after it was placed, although it still works and is listed.
        using var fake = new FakeBinance(futures: true);
        var placed = DateTimeOffset.UtcNow.AddDays(-91);
        long website = fake.AddExternal("BUY", "LIMIT", 3m, 59000m, placedAt: placed);
        fake.Fill(website, 1m, placed.AddHours(1));
        using var broker = Broker(fake);
        await Observed(broker, fake, () => broker.OrdersReady && broker.ActiveOrders.Any(o => o.Filled == 1m) && Ready(broker));
        var order = Assert.Single(broker.ActiveOrders);

        OrderActions.Reprice(broker, order, 59100m);
        await Observed(broker, fake, () => broker.LastError == OrderNotReturned && !broker.IsReplacementPending(order));
        LeftUnownedAndUnchanged(fake, broker, order, website, 59000m);
    }

    [Fact]
    public async Task A_partly_filled_futures_order_older_than_the_default_seven_day_trade_window_adopts_its_full_baseline()
    {
        using var fake = new FakeBinance(futures: true);
        var placed = DateTimeOffset.UtcNow.AddDays(-20);
        long website = fake.AddExternal("BUY", "LIMIT", 3m, 59000m, placedAt: placed);
        fake.Fill(website, 0.5m, placed.AddHours(1));
        fake.Fill(website, 0.25m, placed.AddDays(12)); // eight days ago
        fake.Fill(website, 0.25m, DateTimeOffset.UtcNow.AddDays(-1));
        using var broker = Broker(fake);
        await Observed(broker, fake, () => broker.OrdersReady && broker.ActiveOrders.Any(o => o.Filled == 1m) && Ready(broker));
        var order = Assert.Single(broker.ActiveOrders);

        OrderActions.Reprice(broker, order, 59100m);
        await Observed(broker, fake, () => fake.Placed.Count == 1 && order.ExchangeOrderId != website.ToString(Inv) &&
            order.ExchangeOrderId.Length > 0 && !broker.IsReplacementPending(order));
        var adopted = Assert.IsType<OwnedOrderIntent>(broker.OrderContext.Find(order.LogicalOrderId));
        Assert.Equal((true, 1m), (adopted.BaselineKnown, adopted.QuantityBaseline));
        Assert.Equal(fake.FuturesTradeHistory.Where(trade => trade.OrderId == website).Select(trade => trade.Id.ToString(Inv)),
            adopted.PriorExecutions!.Select(prior => prior.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(2m, Assert.Single(fake.OpenOrders).Qty);
        Assert.DoesNotContain(fake.Refused, refusal => refusal.Contains("userTrades", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_futures_order_placed_before_the_three_month_trade_history_is_refused_clearly()
    {
        using var fake = new FakeBinance(futures: true);
        // Still found by Query Order (under 90 days), but older than the shortest three calendar months (89 days).
        var placed = DateTimeOffset.UtcNow.AddDays(-89.5);
        long website = fake.AddExternal("BUY", "LIMIT", 3m, 59000m, placedAt: placed);
        fake.Fill(website, 1m, DateTimeOffset.UtcNow.AddDays(-2));
        using var broker = Broker(fake);
        await Observed(broker, fake, () => broker.OrdersReady && broker.ActiveOrders.Any(o => o.Filled == 1m) && Ready(broker));
        var order = Assert.Single(broker.ActiveOrders);

        OrderActions.Reprice(broker, order, 59100m);
        await Observed(broker, fake, () => broker.LastError == HistoryBeyondLimit && !broker.IsReplacementPending(order));
        LeftUnownedAndUnchanged(fake, broker, order, website, 59000m);
    }

    [Fact]
    public async Task A_spot_order_with_more_than_one_trade_page_of_fills_is_adopted_from_every_page()
    {
        using var fake = new FakeBinance(futures: false);
        fake.SetSpotBalance("BTC", 0m, 0m);
        long website = fake.AddExternal("BUY", "LIMIT", 3m, 59000m);
        long start = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        var fills = Enumerable.Range(0, 1001).Select(i =>
            new FakeBinanceSpotTrade(95001 + i, website, "BTCUSDT", true, 0.001m, 59000m, start + i, 0m, "USDT")).ToArray();
        fake.FillWithSpotTrades(website, 1.001m, fills);
        using var broker = Broker(fake);
        await Observed(broker, fake, () => broker.OrdersReady && broker.ActiveOrders.Any(o => o.Filled == 1.001m) && Ready(broker));
        var order = Assert.Single(broker.ActiveOrders);

        OrderActions.Reprice(broker, order, 59100m);
        await Observed(broker, fake, () => fake.Placed.Count == 1 && order.ExchangeOrderId != website.ToString(Inv) &&
            order.ExchangeOrderId.Length > 0 && !broker.IsReplacementPending(order));
        var adopted = Assert.IsType<OwnedOrderIntent>(broker.OrderContext.Find(order.LogicalOrderId));
        Assert.Equal((true, 1.001m), (adopted.BaselineKnown, adopted.QuantityBaseline));
        Assert.Equal(fills.Select(fill => fill.Id.ToString(Inv)), adopted.PriorExecutions!.Select(prior => prior.Id).Order(StringComparer.Ordinal));
        Assert.Equal(1.999m, Assert.Single(fake.OpenOrders).Qty);
    }
}
