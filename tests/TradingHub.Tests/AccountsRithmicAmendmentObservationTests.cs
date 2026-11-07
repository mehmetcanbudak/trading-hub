using System.Reflection;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed partial class AccountsRithmicCloseRegressionTests
{
    [Fact]
    public async Task Native_amendment_uses_one_fill_observation_before_public_broker_publication()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        fixture.Profile.AllowRealOrders = true;
        fixture.Profile.MaxOrderQuantity = 5;
        var originalChart = fixture.Chart("ES");
        await Until(() => originalChart.PositionReady && originalChart.OrdersReady);
        var original = OrderActions.Manual(originalChart, OrderTypes.Limit, OrderDirections.Buy, 5, 90);
        await Until(() => original.ExchangeOrderId.Length > 0 && original.QuantityToFill == 5 &&
            originalChart.ActiveOrders.Contains(original) && fixture.PendingRequests == 0 &&
            fixture.Session.OrdersOf("ACC1").Any(item => item.BasketId == original.ExchangeOrderId &&
                item.WorkingQuantity == 5 && item.CumulativeQuantityKnown));
        string basket = original.ExchangeOrderId;
        string originalTag = Assert.IsType<string>(original.ClientOrderId);
        string originalId = Assert.IsType<string>(original.IntentId);
        string logical = Assert.IsType<string>(original.LogicalOrderId);
        var identity = new NativeOrderIdentity("rithmic:basket", basket);

        // A replayed owner has no original create continuation competing for the held broker gate.
        await fixture.RestartAsync();
        var chart = fixture.Chart("ES");
        await Until(() => chart.PositionReady && chart.OrdersReady && chart.ActiveOrders.Any());
        var order = Assert.Single(chart.ActiveOrders);
        var tracked = Assert.IsAssignableFrom<ITrackedOrderReplacement>(chart);
        var context = chart.OrderContext;
        var native = Assert.Single(fixture.Session.OrdersOf("ACC1"), item => item.BasketId == basket);
        Assert.Equal(originalId, order.IntentId);
        Assert.Equal(logical, order.LogicalOrderId);
        Assert.Equal(5m, order.QuantityToFill);
        Assert.Equal(0m, order.Filled);
        Assert.Equal(5m, native.WorkingQuantity);
        Assert.Equal(0m, native.Filled);
        Assert.False(native.Done);
        Assert.Equal(originalTag, Assert.Single(Tags(server)));
        Assert.Equal(0, server.ModifyRequests);

        long workingRevision = native.WorkingRevision;
        var amendmentPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenancePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveWorkingAmendment()
        {
            if (order.Filled != 1 || order.QuantityToFill != 3 || native.WorkingQuantity != 3 ||
                native.WorkingRevision <= workingRevision) return;
            if (order.Price == 91 && native.WorkingPrice == 91) amendmentPublished.TrySetResult();
            if (order.Price == 92 && native.WorkingPrice == 92) maintenancePublished.TrySetResult();
        }
        chart.Changed += ObserveWorkingAmendment;
        try
        {
            var slot = await fixture.HoldSendSlotAsync();
            bool slotOwned = true;
            GateHold? brokerHold = null;
            Task? fill = null;
            string amendmentId;
            try
            {
                OrderActions.Replace(chart, order, 91, 3);
                amendmentId = Assert.IsType<string>(order.IntentId);
                Assert.NotEqual(originalId, amendmentId);
                await Until(() => fixture.PendingRequests == 1);
                brokerHold = new GateHold(NativeField<object>(chart, "_gate"));
                object admission = typeof(RithmicTradingSession).GetField("NativeAdmissionGate",
                    BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                Assert.False(IsHeld(admission));
                slot.Release();
                slotOwned = false;
                await Until(() => IsHeld(admission));
                Assert.Equal(0, server.ModifyRequests);

                fill = server.FillOrder(basket, 1, 90);
                await Until(() => fixture.Session.OrdersOf("ACC1").Single(item => item.BasketId == basket).Filled == 1 && fixture.Net == 1);
                Assert.True(IsHeld(admission));
                Assert.Equal(1m, native.Filled);
                Assert.Equal(5m, native.Quantity);
                Assert.Equal(0m, order.Filled);
                Assert.Equal(5m, order.QuantityToFill);
                var execution = Assert.Single(fixture.Session.FillsOf("ACC1"));
                Assert.Equal(basket, execution.BasketId);
                Assert.Equal(1m, execution.Quantity);
                Assert.True(execution.IsBuy);
                Assert.Equal(0, server.ModifyRequests);
            }
            finally
            {
                try { brokerHold?.Dispose(); }
                finally { if (slotOwned) slot.Release(); }
            }
            Assert.NotNull(fill);
            await fill.WaitAsync(Deadline);
            await server.ModifyResponseSent.Task.WaitAsync(Deadline);
            await amendmentPublished.Task.WaitAsync(Deadline);
            Assert.Equal(1, server.ModifyRequests);
            Assert.Equal(3, server.Open[basket].Qty);
            Assert.Equal(91d, server.Open[basket].Price);
            Assert.Equal(originalTag, server.Open[basket].Tag);
            Assert.Equal(3m, native.WorkingQuantity);
            Assert.Equal(91m, native.WorkingPrice);
            Assert.Equal(1m, order.Filled);
            Assert.Equal(2m, order.Unfilled);

            var durable = new OrderContext(context.Descriptor, fixture.ExecutionRoot);
            var dispatched = Assert.IsType<OwnedOrderIntent>(durable.Find(amendmentId));
            Assert.True(dispatched.DispatchUncertain);
            Assert.Equal(originalId, dispatched.PredecessorId);
            Assert.Equal(2m, dispatched.DispatchQuantity);
            Assert.Equal(1m, dispatched.PredecessorFilled);
            Assert.Equal(3m, dispatched.DispatchQuantity + dispatched.PredecessorFilled);
            Assert.Equal(91m, dispatched.DispatchPrice);
            Assert.Null(dispatched.Rejection);
            Assert.Equal(new NativeOrderAmendment(identity, originalTag, 3, 91), dispatched.AcceptedNativeAmendment);
            Assert.Equal(amendmentId, durable.FindNativeSubmission(originalTag, identity)!.Id);
            Assert.False(tracked.IsReplacementPending(order));
            Assert.Null(native.PendingModifyQuantity);
            Assert.False(context.HasStorageFault);
            Assert.Null(chart.LastError);

            OrderActions.Reprice(chart, order, 92);
            string maintenanceId = Assert.IsType<string>(order.IntentId);
            Assert.NotEqual(amendmentId, maintenanceId);
            await maintenancePublished.Task.WaitAsync(Deadline);
            Assert.Equal(2, server.ModifyRequests);
            Assert.Equal(3, server.Open[basket].Qty);
            Assert.Equal(92d, server.Open[basket].Price);
            Assert.False(tracked.IsReplacementPending(order));
            Assert.Equal(1m, order.Filled);
            Assert.Equal(2m, order.Unfilled);
            var maintained = Assert.IsType<OwnedOrderIntent>(new OrderContext(context.Descriptor, fixture.ExecutionRoot).Find(maintenanceId));
            Assert.True(maintained.PriceOnly);
            Assert.Equal(amendmentId, maintained.PredecessorId);
            Assert.Equal(2m, maintained.DispatchQuantity);
            Assert.Equal(1m, maintained.PredecessorFilled);
            Assert.Equal(new NativeOrderAmendment(identity, originalTag, 3, 92), maintained.AcceptedNativeAmendment);
            Assert.Null(chart.LastError);
            await Until(() => fixture.PendingRequests == 0);

            await fixture.RestartAsync();
            var restored = fixture.Chart("ES");
            await Until(() => restored.PositionReady && restored.OrdersReady && restored.ActiveOrders.Any());
            var recovered = Assert.Single(restored.ActiveOrders);
            Assert.Equal(maintenanceId, recovered.IntentId);
            Assert.Equal(logical, recovered.LogicalOrderId);
            Assert.Equal(context.ManualOwner.Id, recovered.OwnerId);
            Assert.Equal(basket, recovered.ExchangeOrderId);
            Assert.Equal(identity, recovered.NativeIdentity);
            Assert.Equal(3m, recovered.QuantityToFill);
            Assert.Equal(1m, recovered.Filled);
            Assert.Equal(2m, recovered.Unfilled);
            Assert.Equal(92m, recovered.Price);
            Assert.Equal(OrderStates.Active, recovered.State);
            Assert.False(((ITrackedOrderReplacement)restored).IsReplacementPending(recovered));
            Assert.Equal(maintenanceId, restored.OrderContext.FindNativeSubmission(originalTag, identity)!.Id);
            Assert.Equal(dispatched.AcceptedNativeAmendment, restored.OrderContext.Find(amendmentId)!.AcceptedNativeAmendment);
            Assert.Equal(maintained.AcceptedNativeAmendment, restored.OrderContext.Find(maintenanceId)!.AcceptedNativeAmendment);
            Assert.False(restored.OrderContext.HasStorageFault);
            Assert.Null(restored.LastError);
            Assert.Equal(1m, fixture.Net);
            Assert.Equal(originalTag, Assert.Single(Tags(server)));
            Assert.Equal(1, Requests(server, "312"));
            Assert.Equal(2, Requests(server, "314"));
            Assert.Equal(0, Requests(server, "316"));
        }
        finally
        {
            chart.Changed -= ObserveWorkingAmendment;
        }
    }
}
