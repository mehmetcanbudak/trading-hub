using System.Globalization;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>Rithmic contra un servidor de mentira: historia paginada, velas, contrato del mes y órdenes.</summary>
public class RithmicTests : IDisposable
{
    private readonly NativeOrderTestFixture _orders = new();
    public void Dispose() => _orders.Dispose();

    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = RithmicFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 50 };
    private const long T0 = 1_790_000_000;

    private static RithmicSettings Settings(FakeRithmicServer s, string password = "demo") =>
        new() { Uri = s.Uri, System = "Rithmic Paper Trading", User = "demo", Password = password, TimeoutMs = 5000 };

    private static async Task Until(Func<bool> condition, int ms = 6000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    [Fact]
    public async Task NativeOwnershipNotificationAllowsPublicProgressCancellationAndDisposal()
    {
        using var server = new FakeRithmicServer();
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady);
        Assert.True(broker.OrdersReady);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0, releaseTimedOut = 0;
        void HoldFirstNotification()
        {
            if (Interlocked.Increment(ref notifications) != 1) return;
            entered.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(20))) Interlocked.Exchange(ref releaseTimedOut, 1);
            }
            finally { returned.TrySetResult(); }
        }
        broker.OrderContext.OwnershipChanged += HoldFirstNotification;
        Task progress = Task.CompletedTask;
        try
        {
            var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 2m, 4990m);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
            progress = Task.Run(async () =>
            {
                Assert.True(broker.OrdersReady);
                Assert.Same(order, Assert.Single(broker.ActiveOrders));
                broker.Cancel(order);
                await Until(() => { lock (server.Received) return server.Received.Contains("316"); });
                lock (server.Received) Assert.Contains("316", server.Received);
                broker.Dispose();
            });
            await progress.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(0, Volatile.Read(ref releaseTimedOut));
            Assert.NotNull(order.NativeIdentity);
        }
        finally
        {
            release.Set();
            broker.OrderContext.OwnershipChanged -= HoldFirstNotification;
            await progress.WaitAsync(TimeSpan.FromSeconds(6));
            if (entered.Task.IsCompleted) await returned.Task.WaitAsync(TimeSpan.FromSeconds(6));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ready_and_queued_amendments_publish_ownership_outside_the_provider_gate(bool queued)
    {
        using var server = new FakeRithmicServer
        {
            ModifyResponseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SuppressModifyNotification = true,
        };
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 2m, 4990m);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        string basket = order.ExchangeOrderId;
        Assert.Equal(basket, Assert.Single(server.Open).Key);
        if (queued)
        {
            OrderActions.Reprice(broker, order, 4991m);
            await Until(() => server.ModifyRequests == 1);
            Assert.Equal(1, server.ModifyRequests);
            OrderActions.Reprice(broker, order, 4992m);
        }
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0, releaseTimedOut = 0;
        void HoldAmendmentOwnership()
        {
            if (!broker.IsReplacementPending(order) || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
            entered.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(20))) Interlocked.Exchange(ref releaseTimedOut, 1);
            }
            finally { returned.TrySetResult(); }
        }
        broker.OrderContext.OwnershipChanged += HoldAmendmentOwnership;
        Task mutation = Task.CompletedTask, progress = Task.CompletedTask;
        try
        {
            mutation = queued
                ? server.PushOrder(basket, server.Open[basket].Tag, true, 2, 4991, 1)
                : Task.Run(() => OrderActions.Reprice(broker, order, 4991m));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
            server.ModifyResponseRelease.TrySetResult();
            progress = Task.Run(async () =>
            {
                Assert.True(broker.OrdersReady);
                Assert.Same(order, Assert.Single(broker.ActiveOrders));
                broker.Cancel(order);
                await Until(() => { lock (server.Received) return server.Received.Contains("316"); });
                lock (server.Received) Assert.Contains("316", server.Received);
                broker.Dispose();
            });
            await progress.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(0, Volatile.Read(ref releaseTimedOut));
            Assert.Equal(0m, broker.Position.Volume);
            Assert.InRange(server.ModifyRequests, queued ? 1 : 0, queued ? 2 : 1);
        }
        finally
        {
            release.Set();
            broker.OrderContext.OwnershipChanged -= HoldAmendmentOwnership;
            server.ModifyResponseRelease.TrySetResult();
            await mutation.WaitAsync(TimeSpan.FromSeconds(6));
            await progress.WaitAsync(TimeSpan.FromSeconds(6));
            if (entered.Task.IsCompleted) await returned.Task.WaitAsync(TimeSpan.FromSeconds(6));
        }
    }

    [Fact]
    public async Task NativeQuantityNotificationAllowsPublicProgressAndPreservesOrderedAdmittedQuantities()
    {
        using var server = new FakeRithmicServer();
        using var session = new RithmicTradingSession(Settings(server));
        using var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady);
        Assert.True(broker.OrdersReady);
        var context = broker.OrderContext;
        string root = context.StorageRoot!;
        const string key = "owned-rithmic-quantity-notification";
        var owner = context.LedgerOwner(key, LiveStore.CaptureLedgerGeneration(root, key));
        _ = AccountExecutionProjector.For(broker.Executions, context);
        var order = OrderActions.Place(broker, owner, OrderTypes.Limit, OrderDirections.Buy, 3m, 4990m);
        await Until(() => order.NativeIdentity != null && broker.ActiveOrders.Contains(order));
        Assert.NotNull(order.NativeIdentity);
        string basket = order.ExchangeOrderId;
        await Until(() => server.Open.ContainsKey(basket));
        Assert.True(server.Open.ContainsKey(basket));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new System.Collections.Concurrent.ConcurrentQueue<decimal>();
        int releaseTimedOut = 0;
        void HoldFirstQuantity(OwnedOrderQuantity observation)
        {
            observed.Enqueue(observation.CumulativeQuantity);
            if (observation.CumulativeQuantity == 2m) secondObserved.TrySetResult();
            if (observation.CumulativeQuantity != 1m) return;
            entered.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(20))) Interlocked.Exchange(ref releaseTimedOut, 1);
            }
            finally { returned.TrySetResult(); }
        }
        context.QuantityChanged += HoldFirstQuantity;
        Task progress = Task.CompletedTask;
        try
        {
            try
            {
                await server.FillOrder(basket, 1, 5000.25);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
                progress = Task.Run(() =>
                {
                    Assert.True(broker.OrdersReady);
                    Assert.Same(order, Assert.Single(broker.ActiveOrders));
                    Assert.Equal(1m, order.Filled);
                    Assert.Equal(1m, LiveStore.LoadLedger(root, key, 50m).Position);
                });
                await progress.WaitAsync(TimeSpan.FromSeconds(6));
                Assert.Equal(0, Volatile.Read(ref releaseTimedOut));
            }
            finally
            {
                release.Set();
                await progress.WaitAsync(TimeSpan.FromSeconds(6));
                if (entered.Task.IsCompleted) await returned.Task.WaitAsync(TimeSpan.FromSeconds(6));
            }
            await server.FillOrder(basket, 1, 5001.25);
            await Until(() => context.Find(order.IntentId)!.QuantityProjected == 2m);
            await secondObserved.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(new[] { 1m, 2m }, observed.ToArray());
            var ledger = LiveStore.LoadLedger(root, key, 50m);
            Assert.Equal(2m, ledger.Position);
            Assert.True(ledger.HasPendingPrices); // Cumulative Rithmic quantities are not execution-price evidence.
            Assert.Equal(0m, ledger.AveragePrice);
            broker.Dispose();
            await server.FillOrder(basket, 1, 5002.25);
            await Until(() => session.OrdersFor("ACC1", "ESZ6", "CME").Single(s => s.BasketId == basket).Filled == 3m);
            Assert.Equal(3m, session.OrdersFor("ACC1", "ESZ6", "CME").Single(s => s.BasketId == basket).Filled);
            Assert.Equal(2m, context.Find(order.IntentId)!.QuantityProjected);
            Assert.Equal(2m, LiveStore.LoadLedger(root, key, 50m).Position);
            Assert.Equal(new[] { 1m, 2m }, observed.ToArray());
        }
        finally { context.QuantityChanged -= HoldFirstQuantity; }
    }

    [Fact]
    public async Task La_historia_tick_a_tick_se_pagina_sin_perder_ni_repetir()
    {
        using var server = new FakeRithmicServer { PageSize = 7 };
        // 31 operaciones; varias en el mismo segundo justo donde se corta una página
        int n = 0;
        for (int sec = 0; sec < 10; sec++)
            for (int k = 0; k < (sec == 2 ? 6 : 3); k++)
                server.Ticks.Add((T0 + sec, k * 1000, 5000 + sec * 0.25, 1 + k, (n++ % 2) == 0));
        server.Ticks.Add((T0 + 30, 0, 5005, 4, true));
        var feed = new RithmicFeed(Settings(server));
        var got = new List<Trade>();
        await feed.LoadTradesAsync(Es, DateTimeOffset.FromUnixTimeSeconds(T0 - 5).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(T0 + 60).UtcDateTime, null,
            b => { got.AddRange(b); return true; }, null, CancellationToken.None);

        Assert.Equal(server.Ticks.Count, got.Count);
        Assert.Equal(got.Select(t => t.Id).Distinct().Count(), got.Count);
        Assert.True(got.Zip(got.Skip(1)).All(p => p.Second.Time >= p.First.Time && p.Second.Id > p.First.Id));
        Assert.Equal(server.Ticks.Sum(t => t.Size), (int)got.Sum(t => t.Volume));
        Assert.Equal(server.Ticks.Count(t => t.Buy), got.Count(t => t.Side == TradeSide.Buy));
        Assert.Equal(5005m, got[^1].Price);
    }

    [Fact]
    public async Task Las_velas_de_un_minuto_llegan_con_volumen_comprador()
    {
        using var server = new FakeRithmicServer { PageSize = 5 };
        for (int i = 1; i <= 17; i++) server.Bars.Add((T0 + i * 60, 5000, 5002, 4999, 5001, 100 + i, 60));
        var feed = new RithmicFeed(Settings(server));
        var got = new List<Kline>();
        await feed.LoadKlinesAsync(Es, DateTimeOffset.FromUnixTimeSeconds(T0).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(T0 + 3600).UtcDateTime, 1, 0,
            b => { got.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(17, got.Count);
        Assert.Equal(T0 * 1000, got[0].OpenTimeMs); // la marca es el cierre: abre un minuto antes
        Assert.All(got, k => Assert.Equal(60m, k.BuyVolume));
        Assert.Equal(117m, got[^1].Volume);
    }

    [Fact]
    public async Task Sabe_el_contrato_del_mes_y_la_ultima_operacion()
    {
        using var server = new FakeRithmicServer { LastTradeSsboe = T0 + 123 };
        var feed = new RithmicFeed(Settings(server));
        var (symbol, exchange) = await feed.ContractAsync(Es, CancellationToken.None);
        Assert.Equal("ESZ6", symbol);
        Assert.Equal("CME", exchange);
        var last = await feed.LastTradeTimeAsync(Es, CancellationToken.None);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(T0 + 123).UtcDateTime, last);
    }

    [Fact]
    public async Task La_cuenta_opera_mercado_limite_modifica_y_cancela()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        var feed = new RithmicFeed(Settings(server));
        using var session = new RithmicTradingSession(Settings(server));
        session.Start();
        await Until(() => session.IsConnected);
        Assert.True(session.IsConnected, session.StatusText);
        Assert.Equal("ACC1", Assert.Single(session.Accounts).AccountId);
        Assert.True(session.IsPractice);

        var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = _orders.Rithmic(data, session, "ACC1", "ACC1", ct => feed.ContractAsync(Es, ct));
        Assert.False(broker.IsReal);

        // mercado: se llena y la posición llega por la planta de PnL
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 2);
        await Until(() => broker.Position.Volume == 2);
        Assert.Equal(2m, broker.Position.Volume);
        Assert.Equal(5000m, broker.Position.AveragePrice);
        await Until(() => !broker.ActiveOrders.Any());
        Assert.Empty(broker.ActiveOrders);

        // límite: queda activa con su número de Rithmic
        var limit = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 1, 5010);
        await Until(() => limit.ExchangeOrderId.Length > 0);
        Assert.StartsWith("B", limit.ExchangeOrderId);
        Assert.Contains(limit, broker.ActiveOrders);
        Assert.Equal(5010.0, server.Open[limit.ExchangeOrderId].Price);

        // modificar el precio
        OrderActions.Reprice(broker, limit, 5012);
        await Until(() => limit.Price == 5012 && !broker.IsReplacementPending(limit));
        Assert.Equal(5012.0, server.Open[limit.ExchangeOrderId].Price);
        Assert.Equal(5012m, limit.Price);

        // cancelar
        broker.Cancel(limit);
        await Until(() => !broker.ActiveOrders.Contains(limit));
        Assert.DoesNotContain(limit, broker.ActiveOrders);
        Assert.Equal(OrderStatus.Canceled, limit.Status);
        Assert.Equal(0m, limit.Filled); // a cancellation is not an execution

        // cerrar la posición
        OrderActions.Flatten(broker);
        await Until(() => broker.Position.Volume == 0);
        Assert.Equal(0m, broker.Position.Volume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_requires_native_working_price_and_total_quantity_even_after_late_fills(bool stop)
    {
        using var server = new FakeRithmicServer
        {
            ModifyResponseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SuppressModifyNotification = true
        };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady);
        var order = stop ? OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 5, 4990) : OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 5, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        var basket = order.ExchangeOrderId;
        await server.FillOrder(basket, 1, 4990);
        await Until(() => order.Filled == 1);
        var tracked = (ITrackedOrderReplacement)broker;
        Assert.Same(order, OrderActions.Replace(broker, order, 4988, 3)); // one filled + three remaining = four total
        Assert.True(tracked.IsReplacementPending(order));
        await Until(() => server.ModifyRequests == 1);
        Assert.Equal(1, server.ModifyRequests);

        // Neither caller-owned optimistic fields nor a fill packet is a working amendment acknowledgment.
        order.Price = order.TriggerPrice = 4988;
        order.QuantityToFill = 4;
        order.Unfilled = 3;
        Assert.True(tracked.IsReplacementPending(order));
        await server.FillOrder(basket, 1, 4988);
        await Until(() => order.Filled == 2);
        Assert.Equal(2m, order.Filled);
        Assert.True(tracked.IsReplacementPending(order));
        server.ModifyResponseRelease.SetResult();
        await server.ModifyResponseSent.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.True(tracked.IsReplacementPending(order)); // command receipt alone is not acknowledgment

        var tag = server.Open[basket].Tag;
        await server.PushOrder(basket, tag, !stop, 3, 4988, stop ? 4 : 1);
        await Until(() => order.QuantityToFill == 3);
        Assert.Equal(3m, order.QuantityToFill);
        Assert.True(tracked.IsReplacementPending(order)); // initial remaining is not the requested TOTAL
        await server.PushOrder(basket, tag, !stop, 4, 4987, stop ? 4 : 1);
        await Until(() => (stop ? order.TriggerPrice : order.Price) == 4987);
        Assert.True(tracked.IsReplacementPending(order)); // exact quantity with the wrong price is insufficient
        int resolved = 0;
        broker.Changed += () => { if (!tracked.IsReplacementPending(order)) Interlocked.Increment(ref resolved); };
        await server.PushOrder(basket, tag, !stop, 4, 4988, stop ? 4 : 1);
        await Until(() => !tracked.IsReplacementPending(order) && Volatile.Read(ref resolved) > 0);
        Assert.False(tracked.IsReplacementPending(order));
        Assert.True(Volatile.Read(ref resolved) > 0);
        Assert.Equal(4m, order.QuantityToFill);
        Assert.Equal(2m, order.Unfilled);
        Assert.Equal(basket, order.ExchangeOrderId);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
        Assert.Equal(1, server.ModifyRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trailing_modify_coalesces_latest_price_until_native_ack_using_current_remaining_quantity(bool stop)
    {
        using var server = new FakeRithmicServer
        {
            ModifyResponseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SuppressModifyNotification = true
        };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady);
        var order = stop ? OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 5, 4990) : OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 5, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        string basket = order.ExchangeOrderId;
        string tag = server.Open[basket].Tag;
        OrderActions.Reprice(broker, order, 4991);
        await Until(() => server.ModifyRequests == 1);
        OrderActions.Reprice(broker, order, 4992);
        OrderActions.Reprice(broker, order, 4993);
        Assert.Same(order, OrderActions.Replace(broker, order, 4970, 1)); // explicit sizing requests remain suppressed
        await server.FillOrder(basket, 1, 4991);
        await Until(() => order.Filled == 1);
        Assert.True(broker.IsReplacementPending(order));
        Assert.Equal(1, server.ModifyRequests);
        server.ModifyResponseRelease.SetResult();
        await server.ModifyResponseSent.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.True(broker.IsReplacementPending(order)); // receipt alone cannot dispatch the queued price
        Assert.Equal(1, server.ModifyRequests);

        int prematureResolution = 0;
        broker.Changed += () =>
        {
            if (!broker.IsReplacementPending(order)) Interlocked.Increment(ref prematureResolution);
        };
        await server.PushOrder(basket, tag, false, 5, 4991, stop ? 4 : 1);
        await Until(() => server.ModifyRequests == 2 && server.Open[basket].Price == 4993);
        Assert.Equal(2, server.ModifyRequests);
        Assert.Equal(4993d, server.Open[basket].Price);
        Assert.Equal(5, server.Open[basket].Qty); // one filled + the THEN-current four remaining
        Assert.True(broker.IsReplacementPending(order));
        Assert.Equal(0, Volatile.Read(ref prematureResolution));

        // No new high/Modify call is needed to send B, and only B's native report resolves pending.
        await server.PushOrder(basket, tag, false, 5, 4993, stop ? 4 : 1);
        await Until(() => !broker.IsReplacementPending(order) && Volatile.Read(ref prematureResolution) > 0);
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(4993m, stop ? order.TriggerPrice : order.Price);
        Assert.Equal(4m, order.Unfilled);
        Assert.Equal(basket, order.ExchangeOrderId);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
        Assert.Equal(2, server.ModifyRequests);
        lock (server.Tags) Assert.Single(server.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_trailing_price_waits_through_unknown_amendment_and_nonmatching_replay(bool applied)
    {
        using var server = new FakeRithmicServer { LoseModifyResponse = true, ApplyLostModification = applied };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady);
        var order = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 5, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        string basket = order.ExchangeOrderId;
        string tag = server.Open[basket].Tag;
        server.OrderReplayRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OrderActions.Reprice(broker, order, 4991);
        await Until(() => broker.LastError?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains("unknown", broker.LastError, StringComparison.OrdinalIgnoreCase);
        OrderActions.Reprice(broker, order, 4992);
        OrderActions.Reprice(broker, order, 4993);
        Assert.True(broker.IsReplacementPending(order));
        Assert.Equal(1, server.ModifyRequests);
        server.LoseModifyResponse = false;
        server.SuppressModifyNotification = true;
        server.OrderReplayRelease.SetResult();
        await Until(() => broker.OrdersReady);
        Assert.True(broker.OrdersReady);
        if (!applied)
        {
            Assert.True(broker.IsReplacementPending(order));
            Assert.Equal(1, server.ModifyRequests); // cached old values cannot authorize B
            await server.PushOrder(basket, tag, false, 5, 4991, 4);
        }
        await Until(() => server.ModifyRequests == 2 && server.Open[basket].Price == 4993);
        Assert.Equal(2, server.ModifyRequests);
        Assert.Equal(4993d, server.Open[basket].Price);
        Assert.Equal(5, server.Open[basket].Qty);
        Assert.True(broker.IsReplacementPending(order));
        await server.PushOrder(basket, tag, false, 5, 4993, 4);
        await Until(() => !broker.IsReplacementPending(order));
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(4993m, order.TriggerPrice);
        Assert.Equal(basket, order.ExchangeOrderId);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
        lock (server.Tags) Assert.Single(server.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_trailing_price_survives_definite_amendment_rejection_on_the_working_order(bool notification)
    {
        using var server = new FakeRithmicServer
        {
            ModifyResponseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ModifyRejection = notification ? null : "amendment permission denied",
            RejectModifyByNotification = notification,
            SuppressModifyNotification = true
        };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady);
        var order = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 5, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        string basket = order.ExchangeOrderId;
        string tag = server.Open[basket].Tag;
        OrderActions.Replace(broker, order, 4991, 3);
        await Until(() => server.ModifyRequests == 1);
        OrderActions.Reprice(broker, order, 4993);
        await server.FillOrder(basket, 1, 4990);
        await Until(() => order.Filled == 1);
        server.ModifyRejection = null;
        server.RejectModifyByNotification = false;
        server.ModifyResponseRelease.SetResult();
        await Until(() => server.ModifyRequests == 2 && server.Open[basket].Price == 4993);
        Assert.Equal(2, server.ModifyRequests);
        Assert.Equal(4993d, server.Open[basket].Price);
        Assert.Equal(5, server.Open[basket].Qty); // A's rejected sizing cannot leak into the price-only B
        Assert.True(broker.IsReplacementPending(order));
        Assert.Contains(notification ? "refused" : "permission denied", broker.LastError);
        await server.PushOrder(basket, tag, false, 5, 4993, 4);
        await Until(() => !broker.IsReplacementPending(order));
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(4993m, order.TriggerPrice);
        Assert.Equal(4m, order.Unfilled);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
        lock (server.Tags) Assert.Single(server.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_trailing_price_is_discarded_on_cancel_intent_or_terminal_fill(bool cancel)
    {
        using var server = new FakeRithmicServer
        {
            ModifyResponseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady);
        var order = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 2, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        OrderActions.Reprice(broker, order, 4991);
        await Until(() => server.ModifyRequests == 1);
        OrderActions.Reprice(broker, order, 4993);
        Assert.True(broker.IsReplacementPending(order));
        if (cancel)
        {
            broker.Cancel(order);
            OrderActions.Reprice(broker, order, 4994); // cancellation remains authoritative even before its native reply
        }
        else
        {
            await server.FillOrder(order.ExchangeOrderId, 2, 4991);
            await Until(() => order.State == OrderStates.Done);
            Assert.Equal(OrderStates.Done, order.State);
            Assert.False(broker.IsReplacementPending(order));
        }
        server.ModifyResponseRelease.SetResult();
        await server.ModifyResponseSent.Task.WaitAsync(TimeSpan.FromSeconds(6));
        await Until(() => order.State == OrderStates.Done && !broker.IsReplacementPending(order));
        Assert.Equal(OrderStates.Done, order.State);
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(cancel, order.Canceled);
        Assert.DoesNotContain(order, broker.ActiveOrders);
        Assert.Equal(1, server.ModifyRequests);
        lock (server.Tags) Assert.Single(server.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definite_replacement_rejection_restores_native_values_on_the_original_order(bool notification)
    {
        using var server = new FakeRithmicServer
        {
            ModifyResponseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ModifyRejection = notification ? null : "amendment permission denied",
            RejectModifyByNotification = notification
        };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady);
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 5, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        var basket = order.ExchangeOrderId;
        var tracked = (ITrackedOrderReplacement)broker;
        OrderActions.Replace(broker, order, 4988, 3);
        await Until(() => server.ModifyRequests == 1);
        order.Price = 4988;
        order.QuantityToFill = order.Unfilled = 3;
        Assert.True(tracked.IsReplacementPending(order));
        server.ModifyResponseRelease.SetResult();
        await Until(() => !tracked.IsReplacementPending(order));
        Assert.False(tracked.IsReplacementPending(order));
        Assert.Equal(4990m, order.Price);
        Assert.Equal(5m, order.QuantityToFill);
        Assert.Equal(5m, order.Unfilled);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Equal(basket, order.ExchangeOrderId);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
        Assert.Contains(notification ? "refused" : "permission denied", broker.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_replacement_blocks_duplicate_amendments_until_exact_replay(bool applied)
    {
        using var server = new FakeRithmicServer { LoseModifyResponse = true, ApplyLostModification = applied };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady);
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 5, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        var basket = order.ExchangeOrderId;
        server.OrderReplayRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracked = (ITrackedOrderReplacement)broker;
        OrderActions.Replace(broker, order, 4988, 3);
        await Until(() => broker.LastError?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains("unknown", broker.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.True(tracked.IsReplacementPending(order));
        for (int i = 0; i < 4; i++) Assert.Same(order, OrderActions.Replace(broker, order, 4987, 2));
        Assert.Equal(1, server.ModifyRequests);
        server.OrderReplayRelease.SetResult();
        await Until(() => broker.OrdersReady);
        Assert.True(broker.OrdersReady);
        if (!applied)
        {
            Assert.True(tracked.IsReplacementPending(order)); // an old working replay cannot resolve an unknown command
            Assert.Same(order, OrderActions.Replace(broker, order, 4987, 2));
            var tag = server.Open[basket].Tag;
            await server.PushOrder(basket, tag, true, 3, 4988);
        }
        await Until(() => !tracked.IsReplacementPending(order));
        Assert.False(tracked.IsReplacementPending(order));
        Assert.Equal(3m, order.QuantityToFill);
        Assert.Equal(4988m, order.Price);
        Assert.Equal(basket, order.ExchangeOrderId);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
        Assert.Equal(1, server.ModifyRequests);
    }

    [Fact]
    public async Task Reconnect_replay_confirms_a_cached_order_was_canceled_while_disconnected()
    {
        using var server = new FakeRithmicServer();
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => session.IsConnected);
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(order));
        Assert.Contains(order, broker.ActiveOrders);
        await server.RemoveOrderWhileDisconnected(order.ExchangeOrderId);
        await Until(() => order.Canceled && session.IsConnected);
        Assert.True(order.Canceled, $"Order plant logins: {server.Logins.GetValueOrDefault(2)}; connected: {session.IsConnected}; replay ready: {broker.OrdersReady}; status: {session.StatusText}");
        Assert.Equal(0m, order.Filled);
        Assert.DoesNotContain(order, broker.ActiveOrders);
    }

    [Fact]
    public async Task Lost_submission_response_replays_the_exact_tag_into_the_original_order_once()
    {
        using var server = new FakeRithmicServer { LoseNewOrderResponse = true };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        Assert.True(broker.OrdersReady && broker.PositionReady, session.StatusText);
        var replayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OrderReplayRelease = replayRelease;
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 3, 4990);
        string basket = await server.UnacknowledgedOrderAccepted.Task.WaitAsync(TimeSpan.FromSeconds(6));
        await Until(() => broker.LastError != null && session.IsConnected);
        Assert.NotNull(broker.LastError);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Contains(order, broker.ActiveOrders);
        Assert.Empty(order.ExchangeOrderId);
        Assert.False(broker.OrdersReady);
        Assert.Same(order, OrderActions.Replace(broker, order, 4989, 3));
        server.FillWhileDisconnected(basket, 1, 4990);

        // Matching side/price/quantity is not ownership; only the pre-send tag may bind our handle.
        await server.PushOrder("B900", "manual-lookalike", buy: true, qty: 3, price: 4990);
        await Until(() => broker.ActiveOrders.Any(o => o.ExchangeOrderId == "B900"));
        var manual = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == "B900");
        Assert.NotSame(order, manual);
        Assert.Empty(order.ExchangeOrderId);
        replayRelease.SetResult();
        await Until(() => order.ExchangeOrderId == basket && order.Filled == 1 && broker.OrdersReady);
        Assert.Equal(basket, order.ExchangeOrderId);
        Assert.True(broker.OrdersReady);
        Assert.Equal(1m, order.Filled);
        Assert.Equal(2m, order.Unfilled);
        Assert.Same(order, Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == basket));
        string tag;
        lock (server.Tags) tag = Assert.Single(server.Tags);
        Assert.Equal(basket, session.OrderByTag(tag)?.BasketId);
        Assert.Single(session.OrdersOf("ACC1"), o => o.UserTag == tag);

        await server.FillOrder(basket, 1, 4990);
        await Until(() => order.Filled == 2);
        Assert.Equal(2m, order.Filled);
        await server.ReplayFill(basket, cumulative: 2, quantity: 1, price: 4990);
        await server.ReplayFill(basket, cumulative: 1, quantity: 1, price: 4990);
        await server.ReplayFill(basket, cumulative: 1, quantity: 1, price: 4990);
        broker.Cancel(order);
        await Until(() => order.Canceled);
        Assert.True(order.Canceled);
        Assert.Equal(2m, order.Filled);
        Assert.Equal(1m, order.Unfilled);
        Assert.Equal(2, session.FillsOf("ACC1").Count);
        Assert.Equal(2m, session.FillsOf("ACC1").Sum(f => f.Quantity));
        Assert.Contains(manual, broker.ActiveOrders);
        lock (server.Tags) Assert.Single(server.Tags);
        lock (server.Received) Assert.DoesNotContain("314", server.Received);
    }

    [Fact]
    public async Task Cancel_before_unknown_submission_binding_survives_until_delayed_replay()
    {
        using var server = new FakeRithmicServer { LoseNewOrderResponse = true };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        Assert.True(broker.OrdersReady && broker.PositionReady, session.StatusText);
        var replayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OrderReplayRelease = replayRelease;
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 2, 5010);
        broker.Cancel(order);
        string basket = await server.UnacknowledgedOrderAccepted.Task.WaitAsync(TimeSpan.FromSeconds(6));
        await Until(() => broker.LastError != null && session.IsConnected);
        Assert.NotNull(broker.LastError);
        // Beyond the old two-second ID wait, the cancel is still an unresolved intent, not a completed action.
        await Task.Delay(2200);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Contains(order, broker.ActiveOrders);
        Assert.Empty(order.ExchangeOrderId);
        Assert.False(broker.OrdersReady);
        lock (server.Received) Assert.DoesNotContain("316", server.Received);
        await server.PushOrder("B900", "manual-lookalike", buy: false, qty: 2, price: 5010);
        await Until(() => broker.ActiveOrders.Any(o => o.ExchangeOrderId == "B900"));
        Assert.Empty(order.ExchangeOrderId);
        replayRelease.SetResult();
        await Until(() => order.Canceled);
        Assert.Equal(basket, order.ExchangeOrderId);
        Assert.True(order.Canceled);
        Assert.Equal(0m, order.Filled);
        Assert.DoesNotContain(order, broker.ActiveOrders);
        Assert.Contains(broker.ActiveOrders, o => o.ExchangeOrderId == "B900");
        lock (server.Tags) Assert.Single(server.Tags);
        lock (server.Received) Assert.Single(server.Received, template => template == "316");
    }

    [Fact]
    public async Task Explicit_protocol_submission_rejection_is_terminal()
    {
        using var server = new FakeRithmicServer { NewOrderRejection = "account order permission denied" };
        using var session = new RithmicTradingSession(Settings(server));
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1",
            _ => Task.FromResult(("ESZ6", "CME")));
        session.Start();
        await Until(() => broker.OrdersReady && broker.PositionReady);
        Assert.True(broker.OrdersReady && broker.PositionReady, session.StatusText);
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 4990);
        broker.Cancel(order);
        await Until(() => order.State == OrderStates.Failed && !broker.ActiveOrders.Contains(order));
        Assert.Equal(OrderStates.Failed, order.State);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Empty(order.ExchangeOrderId);
        Assert.Empty(session.OrdersOf("ACC1"));
        Assert.DoesNotContain(order, broker.ActiveOrders);
        Assert.Contains("account order permission denied", broker.LastError);
        lock (server.Tags) Assert.Single(server.Tags);
        lock (server.Received) Assert.DoesNotContain("316", server.Received);
    }

    [Fact]
    public async Task An_earlier_process_order_with_the_same_counter_stays_separate()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        using var feed = new RithmicFeed(Settings(server));
        using var session = new RithmicTradingSession(Settings(server));
        session.Start();
        await Until(() => session.IsConnected);
        Assert.True(session.IsConnected, session.StatusText);
        using var broker = _orders.Rithmic(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1", ct => feed.ContractAsync(Es, ct));

        var ours = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 1, 5010);
        await Until(() => ours.ExchangeOrderId.Length > 0 && server.Open.ContainsKey(ours.ExchangeOrderId));
        string basket = ours.ExchangeOrderId;

        // Earlier processes also numbered their orders from 1 and left working orders today with this order's number:
        // one from a version whose tags had no session part, one from another session.
        string n = ours.Id.ToString(CultureInfo.InvariantCulture);
        await server.PushOrder("B900", "th-" + n, buy: true, qty: 3, price: 4990);
        await server.PushOrder("B901", "th-0a1b2c3d-" + n, buy: true, qty: 2, price: 4985);
        await Until(() => broker.ActiveOrders.Count() == 3);

        Assert.Equal(3, broker.ActiveOrders.Count());
        Assert.Equal(basket, ours.ExchangeOrderId);
        Assert.Equal(OrderDirections.Sell, ours.Direction);
        Assert.Equal(5010m, ours.Price);
        Assert.Equal(1m, ours.QuantityToFill);
        var earlier = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == "B900");
        Assert.NotSame(ours, earlier);
        Assert.Equal("Rithmic", earlier.Comment);
        Assert.Equal(4990m, earlier.Price);

        // restored orders keep matching by basket id
        OrderActions.Reprice(broker, earlier, 4995);
        await Until(() => server.Open["B900"].Price == 4995);
        Assert.Equal(4995.0, server.Open["B900"].Price);
        Assert.Equal(5010.0, server.Open[basket].Price);

        broker.Cancel(ours);
        await Until(() => ours.Status == OrderStatus.Canceled);
        Assert.Equal(OrderStatus.Canceled, ours.Status);
        Assert.False(server.Open.ContainsKey(basket));
        Assert.True(server.Open.ContainsKey("B900"));
        Assert.True(server.Open.ContainsKey("B901"));
    }

    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public volatile FeedStatus Status;
        public string? Message;

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            lock (Trades) Trades.AddRange(trades);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update)
        {
        }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            Status = status;
            Message = message;
        }
    }

    private static readonly Instrument Mes = new() { Symbol = "MES", FeedId = RithmicFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 5 };

    [Fact]
    public async Task Varios_graficos_y_la_historia_comparten_una_sola_sesion_por_planta()
    {
        // Rithmic deja una sola sesión por usuario en cada planta: antes cada gráfico abría la suya y se cortaban entre sí
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var server = new FakeRithmicServer { PageSize = 50, LastTradeSsboe = now - 2 };
        for (int i = 0; i < 30; i++) server.Ticks.Add((now - 600 + i * 10, 0, 5000 + i * 0.25, 1, i % 2 == 0));
        using var feed = new RithmicFeed(Settings(server));
        var es = new Sink();
        var mes = new Sink();
        using var s1 = feed.Subscribe(Es, es);
        using var s2 = feed.Subscribe(Mes, mes);
        await Until(() => es.Message?.Contains("ESZ6") == true && mes.Message?.Contains("MESZ6") == true);
        Assert.Equal(FeedStatus.Connected, es.Status);
        Assert.Equal(FeedStatus.Connected, mes.Status);

        // historia de los dos a la vez + el contrato del mes, por las mismas sesiones
        var got = new List<Trade>[] { new(), new() };
        var from = DateTimeOffset.FromUnixTimeSeconds(now - 700).UtcDateTime;
        var to = DateTime.UtcNow;
        await Task.WhenAll(
            feed.LoadTradesAsync(Es, from, to, null, b => { got[0].AddRange(b); return true; }, null, CancellationToken.None),
            feed.LoadTradesAsync(Mes, from, to, null, b => { got[1].AddRange(b); return true; }, null, CancellationToken.None),
            feed.ContractAsync(Mes, CancellationToken.None));
        Assert.Equal(30, got[0].Count);
        Assert.Equal(30, got[1].Count);

        // en vivo cada uno recibe lo suyo
        await server.PushTrade("ESZ6", 5010.25, 3, true);
        await server.PushTrade("MESZ6", 5010.5, 7, false);
        await Until(() => es.Trades.Count > 0 && mes.Trades.Count > 0);
        Assert.Equal(5010.25m, Assert.Single(es.Trades).Price);
        var m = Assert.Single(mes.Trades);
        Assert.Equal(7m, m.Volume);
        Assert.Equal(TradeSide.Sell, m.Side);

        Assert.Equal(0, server.Kicks);
        Assert.Equal(1, server.Logins[1]); // una sola sesión de cotizaciones
        Assert.Equal(1, server.Logins[3]); // una sola sesión de historia (reusada)
        Assert.Equal(1, feed.TickerLogins);
    }

    [Fact]
    public async Task Probar_la_conexion_usa_la_sesion_de_los_graficos()
    {
        using var server = new FakeRithmicServer();
        using var feed = new RithmicFeed(Settings(server));
        var es = new Sink();
        using var sub = feed.Subscribe(Es, es);
        await Until(() => es.Status == FeedStatus.Connected);
        var (ok, _) = await feed.TestAsync(CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(0, server.Kicks);
        Assert.Equal(1, server.Logins[1]);
    }

    [Theory]
    [InlineData("2026-09-01", "ESU6")] // antes del roll de septiembre
    [InlineData("2026-09-11", "ESU6")] // el viernes anterior todavía opera el de septiembre (ESZ6: 127 mil contratos)
    [InlineData("2026-09-14", "ESZ6")] // roll: lunes de la semana del vencimiento (viernes 18) → 845 mil contratos
    [InlineData("2026-10-11", "ESZ6")]
    [InlineData("2026-12-11", "ESZ6")]
    [InlineData("2026-12-14", "ESH7")] // roll de diciembre: pasa al de marzo del año siguiente
    [InlineData("2026-03-13", "ESH6")] // vencimiento 20/03/2026 → roll lunes 16/03
    [InlineData("2026-03-16", "ESM6")]
    public void El_contrato_de_cada_fecha_sigue_los_rolls(string date, string expected)
    {
        var t = DateTime.SpecifyKind(DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        Assert.Equal(expected, FuturesRolls.On("ES", t).Symbol);
    }

    [Fact]
    public async Task La_historia_larga_pide_cada_contrato_en_su_tramo()
    {
        using var server = new FakeRithmicServer { PageSize = 500, LastTradeSsboe = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        long sep5 = new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        long sep15 = new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        server.Ticks.Add((sep5, 0, 6500, 2, true));
        server.Ticks.Add((sep15, 0, 6550, 3, false));
        using var feed = new RithmicFeed(Settings(server));
        var got = new List<Trade>();
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await feed.LoadTradesAsync(Es, from, from.AddDays(20), null, b => { got.AddRange(b); return true; }, null, CancellationToken.None);

        var roll = new DateTimeOffset(FuturesRolls.Roll(2026, 9)).ToUnixTimeSeconds();
        Assert.Contains(server.TickRequests, r => r.Symbol == "ESU6" && r.Start == new DateTimeOffset(from).ToUnixTimeSeconds() && r.End == roll);
        Assert.Contains(server.TickRequests, r => r.Symbol == "ESZ6" && r.Start == roll);
        Assert.Equal(2, got.Count);

        // un día suelto para el backtest: con el contrato de ese día
        server.TickRequests.Clear();
        var day = await feed.DownloadDayAsync(Es, new DateOnly(2026, 9, 5), null, CancellationToken.None);
        Assert.Single(day!);
        Assert.Equal("ESU6", Assert.Single(server.TickRequests.Select(r => r.Symbol).Distinct()));
        Assert.Null(await feed.DownloadDayAsync(Es, new DateOnly(2026, 9, 6), null, CancellationToken.None));
    }

    [Fact]
    public async Task Un_contrato_vencido_sin_historia_se_prueba_con_dos_cifras_y_se_avisa()
    {
        // el servidor (como Rithmic) solo tiene el contrato actual: los vencidos vuelven vacíos
        using var server = new FakeRithmicServer { PageSize = 500, OnlySymbol = "ESZ6", LastTradeSsboe = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        server.Ticks.Add((new DateTimeOffset(2026, 9, 3, 15, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), 0, 6500, 2, true));
        using var feed = new RithmicFeed(Settings(server));
        var day = await feed.DownloadDayAsync(Es, new DateOnly(2026, 9, 3), null, CancellationToken.None);
        Assert.Null(day);
        Assert.Contains(server.TickRequests, r => r.Symbol == "ESU6");
        Assert.Contains(server.TickRequests, r => r.Symbol == "ESU26");
        Assert.Equal(new[] { "ESU6" }, feed.MissingContracts);
        Assert.Equal("NQU26", FuturesRolls.TwoDigitYear("NQU6", new DateTime(2026, 10, 1)));
        Assert.Equal("ESZ25", FuturesRolls.TwoDigitYear("ESZ5", new DateTime(2026, 10, 1)));
    }

    [Fact]
    public async Task Un_dia_con_cien_mil_operaciones_baja_completo()
    {
        // páginas grandes como las de Rithmic: no se pierde ni se repite nada y no tarda una eternidad
        using var server = new FakeRithmicServer { PageSize = 10_000 };
        long t0 = new DateTimeOffset(2026, 10, 6, 13, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        for (int i = 0; i < 100_000; i++) server.Ticks.Add((t0 + i / 20, (i % 20) * 50_000, 6700 + (i % 40) * 0.25, 1 + i % 3, i % 2 == 0));
        using var feed = new RithmicFeed(Settings(server));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var day = await feed.DownloadDayAsync(Es, new DateOnly(2026, 10, 6), null, CancellationToken.None);
        sw.Stop();
        Assert.Equal(100_000, day!.Count);
        Assert.Equal(day.Count, day.Select(t => t.Id).Distinct().Count());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"tardó {sw.Elapsed}");
        Console.WriteLine($"100.000 operaciones en {sw.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task Con_la_contrasena_mal_la_sesion_lo_dice()
    {
        using var server = new FakeRithmicServer();
        using var session = new RithmicTradingSession(Settings(server, password: "mal"));
        session.Start();
        await Until(() => session.StatusText.Contains("permission", StringComparison.OrdinalIgnoreCase) || session.StatusText.Contains("rechaz", StringComparison.OrdinalIgnoreCase));
        Assert.False(session.IsConnected);
        Assert.Empty(session.Accounts);
        Assert.Contains("permission denied", session.StatusText);
    }

    [Theory]
    [InlineData("2026-10-04T03:35:00Z", true, "2026-10-04T22:00:00Z")] // sábado a la noche en Chicago: abre el domingo 17:00
    [InlineData("2026-10-02T20:59:00Z", false, "2026-10-02T20:59:00Z")] // viernes 15:59 en Chicago: abierto
    [InlineData("2026-10-02T21:00:00Z", true, "2026-10-04T22:00:00Z")] // viernes 16:00: cierra el fin de semana
    [InlineData("2026-09-30T21:30:00Z", true, "2026-09-30T22:00:00Z")] // miércoles en la pausa diaria
    [InlineData("2026-09-30T22:00:00Z", false, "2026-09-30T22:00:00Z")] // miércoles 17:00: abierto otra vez
    [InlineData("2026-12-06T22:30:00Z", true, "2026-12-06T23:00:00Z")] // domingo de invierno (CST, UTC-6): abre a las 23:00 UTC
    public void El_horario_de_Globex_sabe_cuando_esta_cerrado(string utc, bool closed, string nextOpen)
    {
        var t = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(closed, FuturesSessions.IsGlobexClosed(t));
        Assert.Equal(DateTime.Parse(nextOpen, null, System.Globalization.DateTimeStyles.AdjustToUniversal), FuturesSessions.NextGlobexOpen(t));
    }

    [Fact]
    public async Task Con_el_mercado_cerrado_la_historia_muestra_la_ultima_sesion()
    {
        // la última operación fue hace 2 días: la historia se arma hasta ahí
        long last = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds();
        using var server = new FakeRithmicServer { LastTradeSsboe = last, PageSize = 50 };
        for (int i = 0; i < 40; i++) server.Ticks.Add((last - 600 + i * 10, 0, 5000 + i * 0.25, 1, i % 2 == 0));
        var feed = new RithmicFeed(Settings(server));
        var data = new InstrumentData(Es, feed) { TickWindow = TimeSpan.FromHours(2) };
        data.Start(DateTime.UtcNow.AddHours(-1.5));
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; }, 15000);
        data.Stop();
        Assert.False(data.IsLoadingHistory);
        Assert.Equal(40, data.Trades.Count);
        Assert.True(data.HistoryFrom < DateTime.UtcNow.AddDays(-1.9));
    }
}
