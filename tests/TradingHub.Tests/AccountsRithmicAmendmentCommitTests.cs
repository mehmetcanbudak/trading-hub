using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed partial class AccountsRithmicCloseRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_amendment_acceptance_requires_working_evidence_after_its_own_commit(bool delayBrokerPublication)
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        fixture.Profile.AllowRealOrders = true;
        fixture.Profile.MaxOrderQuantity = 10;
        var originalChart = fixture.Chart("ES");
        await Until(() => originalChart.PositionReady && originalChart.OrdersReady);
        var original = OrderActions.Manual(originalChart, OrderTypes.Limit, OrderDirections.Buy, 5, 90);
        await Until(() => original.ExchangeOrderId.Length > 0 && originalChart.ActiveOrders.Contains(original) &&
            fixture.PendingRequests == 0 && fixture.Session.OrdersOf("ACC1").Any(item =>
                item.BasketId == original.ExchangeOrderId && item.WorkingQuantity == 5 && item.CumulativeQuantityKnown));
        string basket = original.ExchangeOrderId;
        string originalTag = Assert.IsType<string>(original.ClientOrderId);
        string originalId = Assert.IsType<string>(original.IntentId);
        string logical = Assert.IsType<string>(original.LogicalOrderId);
        var identity = new NativeOrderIdentity("rithmic:basket", basket);

        await fixture.RestartAsync();
        var chart = fixture.Chart("ES");
        await Until(() => chart.PositionReady && chart.OrdersReady && chart.ActiveOrders.Any());
        var order = Assert.Single(chart.ActiveOrders);
        var tracked = Assert.IsAssignableFrom<ITrackedOrderReplacement>(chart);
        var context = chart.OrderContext;
        var native = Assert.Single(fixture.Session.OrdersOf("ACC1"), item => item.BasketId == basket);
        Assert.Equal(originalId, order.IntentId);
        Assert.False(context.HasStorageFault);
        Assert.Equal(0m, order.Filled);
        Assert.Equal(5m, order.Unfilled);
        Assert.Equal(90m, order.Price);
        Assert.Equal(0, server.ModifyRequests);
        long priorWorkingRevision = native.WorkingRevision;
        using var releaseAdmission = new ManualResetEventSlim();
        var admissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workingPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualPermission = Assert.IsType<Func<OrderRequest, decimal, string?>>(
            NativeField<object>(context, "_nativePermission"));
        if (delayBrokerPublication)
        {
            int held = 0;
            SearchZoomFixture.Set(context, "_nativePermission", (Func<OrderRequest, decimal, string?>)((request, position) =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0)
                {
                    admissionEntered.TrySetResult();
                    if (!releaseAdmission.Wait(Deadline)) throw new TimeoutException();
                }
                return actualPermission(request, position);
            }));
        }
        void ObserveWorking()
        {
            if (native.WorkingRevision > priorWorkingRevision) workingPublished.TrySetResult();
        }
        chart.Changed += ObserveWorking;
        server.SuppressModifyNotification = true;
        try
        {
            string amendmentId;
            var slot = await fixture.HoldSendSlotAsync();
            bool slotOwned = true;
            try
            {
                OrderActions.Reprice(chart, order, 90);
                amendmentId = Assert.IsType<string>(order.IntentId);
                Assert.NotEqual(originalId, amendmentId);
                await Until(() => fixture.PendingRequests == 1);
                Assert.True(tracked.IsReplacementPending(order));
                if (delayBrokerPublication)
                {
                    slot.Release();
                    slotOwned = false;
                    await admissionEntered.Task.WaitAsync(Deadline);
                }
                await server.PushOrder(basket, originalTag, true, 5, 90);
                await Until(() => native.WorkingRevision > priorWorkingRevision);
                var prepared = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, fixture.ExecutionRoot).Find(amendmentId));
                Assert.False(prepared.DispatchUncertain);
                Assert.Null(prepared.AcceptedNativeAmendment);
                Assert.Equal(0, server.ModifyRequests);
                if (delayBrokerPublication) releaseAdmission.Set();
                else
                {
                    await workingPublished.Task.WaitAsync(Deadline);
                    Assert.False(context.HasStorageFault);
                    Assert.Null(chart.LastError);
                    Assert.True(tracked.IsReplacementPending(order));
                    slot.Release();
                    slotOwned = false;
                }
            }
            finally
            {
                releaseAdmission.Set();
                if (slotOwned) slot.Release();
            }
            await workingPublished.Task.WaitAsync(Deadline);
            await server.ModifyResponseSent.Task.WaitAsync(Deadline);
            await Until(() => fixture.PendingRequests == 0);
            Assert.Equal(1, server.ModifyRequests);
            var committed = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, fixture.ExecutionRoot).Find(amendmentId));
            Assert.True(committed.DispatchUncertain);
            Assert.Equal(originalId, committed.PredecessorId);
            Assert.Equal(5m, committed.DispatchQuantity);
            Assert.Equal(0m, committed.PredecessorFilled);
            Assert.Null(committed.AcceptedNativeAmendment);
            Assert.Null(committed.Rejection);
            Assert.True(tracked.IsReplacementPending(order));
            Assert.Equal(5m, native.PendingModifyQuantity);
            Assert.False(context.HasStorageFault);
            Assert.Null(chart.LastError);

            await server.PushOrder(basket, originalTag, true, 5, 90);
            await Until(() => !tracked.IsReplacementPending(order));
            var accepted = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, fixture.ExecutionRoot).Find(amendmentId));
            Assert.Equal(new NativeOrderAmendment(identity, originalTag, 5, 90), accepted.AcceptedNativeAmendment);
            Assert.Equal(amendmentId, context.FindNativeSubmission(originalTag, identity)!.Id);
            Assert.Null(native.PendingModifyQuantity);
            Assert.Equal(logical, order.LogicalOrderId);
            Assert.Equal(5m, order.QuantityToFill);
            Assert.Equal(5m, order.Unfilled);
            Assert.False(context.HasStorageFault);

            server.SuppressModifyNotification = false;
            OrderActions.Reprice(chart, order, 91);
            string maintainedId = Assert.IsType<string>(order.IntentId);
            Assert.NotEqual(amendmentId, maintainedId);
            await Until(() => server.ModifyRequests == 2 && !tracked.IsReplacementPending(order) && order.Price == 91);
            var maintained = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, fixture.ExecutionRoot).Find(maintainedId));
            Assert.Equal(amendmentId, maintained.PredecessorId);
            Assert.Equal(new NativeOrderAmendment(identity, originalTag, 5, 91), maintained.AcceptedNativeAmendment);
            Assert.Equal(maintainedId, context.FindNativeSubmission(originalTag, identity)!.Id);
            var additional = OrderActions.Manual(chart, OrderTypes.Limit, OrderDirections.Buy, 1, 80);
            await Until(() => additional.ExchangeOrderId.Length > 0 && chart.ActiveOrders.Contains(additional));
            Assert.Equal(OrderStates.Active, additional.State);
            Assert.Equal(1m, additional.Unfilled);
            Assert.Equal(80m, additional.Price);
            Assert.Equal(0m, fixture.Net);
            Assert.False(context.HasStorageFault);
            Assert.Null(chart.LastError);
            Assert.Equal(2, Tags(server).Length);
            Assert.Equal(2, Requests(server, "312"));
            Assert.Equal(2, Requests(server, "314"));
            Assert.Equal(0, Requests(server, "316"));
        }
        finally
        {
            releaseAdmission.Set();
            chart.Changed -= ObserveWorking;
            SearchZoomFixture.Set(context, "_nativePermission", actualPermission);
        }
    }
}
