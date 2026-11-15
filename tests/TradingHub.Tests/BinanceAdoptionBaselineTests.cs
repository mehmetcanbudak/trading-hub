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
}
