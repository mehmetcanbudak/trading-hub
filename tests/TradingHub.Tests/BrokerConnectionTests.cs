using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.CTrader;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>cTrader (Open API JSON) y Tradovate (REST + WebSocket) contra servidores de mentira con sus mensajes reales.</summary>
public class BrokerConnectionTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);

    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public readonly List<DepthUpdate> Depth = new();
        public readonly List<(FeedStatus, string?)> Status = new();

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            lock (Trades) Trades.AddRange(trades);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update)
        {
            lock (Depth) Depth.Add(update);
        }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            lock (Status) Status.Add((status, message));
        }
    }

    private static async Task Until(Func<bool> condition, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    // ===================================================================== cTrader

    [Fact]
    public async Task CTrader_autentica_elige_la_cuenta_demo_y_lista_los_simbolos()
    {
        using var fake = new FakeCTrader();
        using var feed = new CTraderFeed(() => fake.Settings());
        var (ok, msg) = await feed.TestAsync(CancellationToken.None);
        Assert.True(ok);
        Assert.Contains("FakeBroker 5012345", msg); // la demo, no la real
        var list = await feed.GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal("US100", list[0].Symbol);
        Assert.Equal(0.01m, list[0].TickSize);
        Assert.Equal(1m, list[0].VolumeStep);
        Assert.Equal(0.00001m, list.First(i => i.Symbol == "EURUSD").TickSize);
        Assert.Equal(new[] { 2100, 2149, 2102, 2114, 2116 }, fake.Received.Where(t => t != 51).Take(5));
    }

    [Fact]
    public async Task CTrader_baja_ticks_con_paginas_y_diferencias_sin_perder_ni_repetir()
    {
        using var fake = new FakeCTrader { TickPage = 30 };
        using var feed = new CTraderFeed(() => fake.Settings());
        var us100 = (await feed.GetInstrumentsAsync(CancellationToken.None))[0];
        var trades = await feed.DownloadDayAsync(us100, Day, null, CancellationToken.None);
        Assert.NotNull(trades);
        Assert.Equal(100, trades!.Count);
        Assert.Equal(20000.00m, trades[0].Price);
        Assert.Equal(20000.99m, trades[^1].Price);
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.Second.Time > p.First.Time && p.Second.Id > p.First.Id));
        Assert.All(trades.Skip(1), t => Assert.Equal(TradeSide.Buy, t.Side)); // siempre sube
        Assert.True(fake.TickRequests >= 4); // 100 ticks de a 30
    }

    [Fact]
    public async Task CTrader_da_velas_y_precios_en_vivo()
    {
        using var fake = new FakeCTrader();
        using var feed = new CTraderFeed(() => fake.Settings());
        var us100 = (await feed.GetInstrumentsAsync(CancellationToken.None))[0];
        var klines = new List<Kline>();
        var from = Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await feed.LoadKlinesAsync(us100, from, from.AddDays(1), 1, 0, b => { klines.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(3, klines.Count);
        Assert.Equal(20000.20m, klines[0].Open);
        Assert.Equal(20000.90m, klines[0].High);
        Assert.Equal(20000.00m, klines[0].Low);
        Assert.Equal(20000.50m, klines[0].Close);
        Assert.Equal(50m, klines[0].Volume);

        var sink = new Sink();
        using (feed.Subscribe(us100, sink))
        {
            await Until(() => sink.Trades.Count >= 3);
        }

        Assert.Equal(new[] { 20001.00m, 20001.01m, 20001.02m }, sink.Trades.Select(t => t.Price)); // al bid
        Assert.NotEmpty(sink.Depth);
    }

    [Fact]
    public async Task CTrader_con_secret_malo_explica_el_error()
    {
        using var fake = new FakeCTrader();
        using var feed = new CTraderFeed(() => fake.Settings("MAL"));
        var ex = await Assert.ThrowsAsync<CTraderException>(() => feed.TestAsync(CancellationToken.None));
        Assert.Equal("CH_CLIENT_AUTH_FAILURE", ex.Code);
        Assert.Contains("incorrect", ex.Message);
    }

    // ===================================================================== Tradovate

    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 50 };

    [Fact]
    public async Task Tradovate_inicia_sesion_con_la_api_key_y_comparte_un_solo_token()
    {
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        var (ok, msg) = await feed.TestAsync(CancellationToken.None);
        Assert.True(ok);
        Assert.Contains("demo", msg);
        Assert.Contains("1 account", msg);
        Assert.Equal(1, fake.Logins); // un login: REST y los sockets usan el mismo token
        Assert.Equal("MD1", fake.MdToken); // el socket de datos usa el token de datos

        using var bad = new TradovateFeed(() => fake.Settings("mal"));
        var ex = await Assert.ThrowsAsync<TradovateException>(() => bad.TestAsync(CancellationToken.None));
        Assert.Contains("Incorrect username", ex.Message);
    }

    [Fact]
    public async Task Tradovate_baja_historia_tick_a_tick_paginada_con_el_lado_agresor()
    {
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        var trades = new List<Trade>();
        var from = Day.ToDateTime(new TimeOnly(14, 0), DateTimeKind.Utc);
        await feed.LoadTradesAsync(Es, from, from.AddHours(1), null, b => { trades.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(12, trades.Count); // de a 5 por pedido, sin repetir
        Assert.True(fake.ChartRequests >= 3);
        Assert.Equal(5000m, trades[0].Price);
        Assert.Equal(5002.75m, trades[^1].Price);
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.Second.Time > p.First.Time && p.Second.Id > p.First.Id));
        // id par: operó al ask (compra); impar: al bid (venta)
        Assert.Equal(Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell), trades.Select(t => t.Side));
        Assert.Contains("md/cancelChart", fake.MdRequests);
    }

    [Fact]
    public async Task Tradovate_en_vivo_manda_la_cinta_y_el_libro()
    {
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        var sink = new Sink();
        using (feed.Subscribe(Es, sink))
        {
            await Until(() => sink.Trades.Count >= 2 && sink.Depth.Count >= 1);
        }

        Assert.Equal(2, sink.Trades.Count); // el tick de historia de la suscripción no se repite
        Assert.Equal(5000.25m, sink.Trades[0].Price);
        Assert.Equal(TradeSide.Buy, sink.Trades[0].Side); // al ask
        Assert.Equal(TradeSide.Sell, sink.Trades[1].Side); // al bid
        Assert.Contains(sink.Status, s => s.Item1 == FeedStatus.Connected);
        Assert.Contains("md/subscribeDOM", fake.MdRequests);
    }

    [Fact]
    public async Task Tradovate_opera_la_cuenta_con_ordenes_ejecuciones_y_posicion()
    {
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        var fills = new List<TradovateFill>();
        session.FillReceived += f => { lock (fills) fills.Add(f); };
        session.Start();
        await Until(() => session.Accounts.Count == 1);
        var acc = Assert.Single(session.Accounts);
        Assert.Equal("DEMO101", acc.Name);

        var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = new TradovateBroker(data, session, acc, "DEMO101 · Tradovate (demo)");
        Assert.False(broker.IsReal);
        broker.Market(OrderDirections.Buy, 2, "Chart Trader");
        await Until(() => broker.Position.Volume == 2 && fills.Count == 1);
        Assert.Equal(2, broker.Position.Volume);
        Assert.Equal(5000.25m, broker.Position.AveragePrice);
        var f = Assert.Single(fills);
        Assert.Equal(101, f.AccountId);
        Assert.Equal(fake.Contract, f.Symbol);
        var sent = fake.Orders[0];
        Assert.Equal("DEMO101", sent["accountSpec"]!.GetValue<string>());
        Assert.Equal(fake.Contract, sent["symbol"]!.GetValue<string>());
        Assert.False(sent["isAutomated"]!.GetValue<bool>());

        // límite: queda activa, se modifica y se cancela
        var limit = broker.Limit(OrderDirections.Buy, 1, 4990, "Estrategia: entrada");
        await Until(() => limit.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(limit));
        Assert.True(fake.Orders[1]["isAutomated"]!.GetValue<bool>()); // de una estrategia
        Assert.Equal(4990m, fake.Orders[1]["price"]!.GetValue<decimal>());
        broker.Modify(limit, 4985);
        await Until(() => fake.Orders.Count >= 3);
        Assert.Equal(4985m, fake.Orders[2]["price"]!.GetValue<decimal>());
        broker.Cancel(limit);
        await Until(() => limit.State == OrderStates.Done);
        Assert.Equal(OrderStatus.Canceled, limit.Status);
        Assert.Equal(0m, limit.Filled); // a canceled order must not invent a full execution
        Assert.DoesNotContain(limit, broker.ActiveOrders);
    }

    [Fact]
    public async Task Tradovate_reconnect_snapshot_removes_absent_positions_and_cancels_cached_working_orders()
    {
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.Accounts.Count == 1);
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, Assert.Single(session.Accounts), "DEMO101");
        var entry = broker.Market(OrderDirections.Buy, 2);
        await Until(() => broker.Position.Volume == 2 && entry.Status == OrderStatus.Filled);
        Assert.Equal(2m, broker.Position.Volume);
        var limit = broker.Limit(OrderDirections.Buy, 1, 4990);
        await Until(() => limit.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(limit));
        Assert.Contains(limit, broker.ActiveOrders);
        await fake.DisconnectUserAndFlatten();
        await Until(() => session.IsConnected && broker.Position.Volume == 0 && limit.Canceled);
        Assert.True(broker.Position.Volume == 0m, $"Position: {broker.Position.Volume}; connected: {session.IsConnected}; snapshot ready: {session.SnapshotReady}; limit canceled: {limit.Canceled}; status: {session.StatusText}");
        Assert.True(limit.Canceled);
        Assert.Equal(0m, limit.Filled);
        Assert.DoesNotContain(limit, broker.ActiveOrders);
    }

    [Theory]
    [InlineData(502, "Upstream response lost")]
    [InlineData(200, "")]
    [InlineData(408, "Request timed out")]
    public async Task Tradovate_unknown_acceptance_replay_keeps_original_owner_and_deferred_cancel(int status, string response)
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        var accepted = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        int placements = 0, cancels = 0;
        fake.Server.OnHttp = req =>
        {
            if (req.Route == "/v1/order/placeorder")
            {
                Interlocked.Increment(ref placements);
                accepted.TrySetResult(JsonNode.Parse(req.Body)!.AsObject());
                // The vendor accepted the request, but its successful reply never reached the caller.
                return (status, response);
            }
            if (req.Route == "/v1/order/cancelorder")
            {
                Assert.Equal(7001, JsonNode.Parse(req.Body)!["orderId"]!.GetValue<long>());
                if (Interlocked.Increment(ref cancels) == 1)
                    return (503, "Cancel response unavailable");
            }
            return normalHttp(req);
        };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, Assert.Single(session.Accounts), "DEMO101");
        var owned = broker.Limit(OrderDirections.Buy, 3, 4990, "Strategy: unknown acceptance");
        var sent = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(8));
        string tag = sent["clOrdId"]!.GetValue<string>();
        Assert.InRange(tag.Length, 1, 64);
        Assert.True(sent["isAutomated"]!.GetValue<bool>());
        await Until(() => broker.LastError != null);
        Assert.NotNull(broker.LastError);
        Assert.Equal(OrderStates.Active, owned.State);
        Assert.Contains(owned, broker.ActiveOrders);
        Assert.Empty(owned.ExchangeOrderId);
        Assert.True(session.HasOpenPositionsOrOrders); // Unknown acceptance retains the transport.
        Assert.False(broker.OrdersReady);
        broker.Cancel(owned);
        // Cross the old finite binding wait; cancellation must remain an obligation, not a timer.
        await Task.Delay(3200);
        Assert.Equal(0, Volatile.Read(ref cancels));

        JsonObject NativeOrder(long id, string clientTag) => new()
        {
            ["id"] = id, ["accountId"] = 101, ["contractId"] = 555,
            ["action"] = "Buy", ["ordStatus"] = "Working",
            ["timestamp"] = DateTime.UtcNow.ToString("O"), ["clOrdId"] = clientTag
        };
        JsonObject NativeFill(long id) => new()
        {
            ["id"] = id, ["orderId"] = 7001, ["contractId"] = 555,
            ["action"] = "Buy", ["qty"] = 1, ["price"] = 4990,
            ["timestamp"] = DateTime.UtcNow.ToString("O"), ["active"] = true
        };
        fake.ReplayOrders.Add(NativeOrder(7002, ""));
        fake.ReplayVersions.Add(new JsonObject { ["id"] = 1, ["orderId"] = 7002, ["orderQty"] = 3, ["orderType"] = "Limit", ["price"] = 4990 });
        await fake.ReconnectUser();
        await Until(() => broker.ActiveOrders.Any(o => o.ExchangeOrderId == "7002"));
        var manual = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == "7002");
        Assert.NotSame(owned, manual);
        Assert.Empty(owned.ExchangeOrderId);
        Assert.False(broker.OrdersReady);
        Assert.Contains(owned, broker.ActiveOrders);
        Assert.Equal(0, Volatile.Read(ref cancels));

        fake.ReplayOrders.Add(NativeOrder(7001, tag));
        fake.ReplayVersions.Add(new JsonObject { ["id"] = 2, ["orderId"] = 7001, ["orderQty"] = 3, ["orderType"] = "Limit", ["price"] = 4990 });
        var firstFill = NativeFill(8001);
        fake.ReplayFills.Add(firstFill);
        await fake.ReconnectUser();
        await Until(() => owned.ExchangeOrderId == "7001" && Volatile.Read(ref cancels) == 1 && broker.LastError?.Contains("503", StringComparison.Ordinal) == true);
        Assert.Equal("7001", owned.ExchangeOrderId);
        Assert.Equal(1, owned.Filled);
        Assert.Equal(2, owned.Unfilled);
        Assert.Same(owned, Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == "7001"));
        Assert.Equal(OrderStates.Active, owned.State);
        Assert.Equal(1, Volatile.Read(ref cancels)); // Failed cancel must not busy-spin.

        await fake.ReconnectUser(); // A later replay retries the retained cancel, never placement.
        await Until(() => owned.Canceled);
        Assert.True(owned.Canceled);
        Assert.Equal(2, Volatile.Read(ref cancels));
        Assert.Equal(1, owned.Filled);
        await fake.PublishFill(firstFill);
        await fake.PublishFill(NativeFill(8002)); // Late fills still belong to the canceled original handle.
        await Until(() => owned.Filled == 2);
        Assert.Equal(2, owned.Filled);
        Assert.Equal(1, owned.Unfilled);
        Assert.True(owned.Canceled);
        Assert.Equal("7001", owned.ExchangeOrderId);
        Assert.Contains(manual, broker.ActiveOrders);
        Assert.Equal(0, manual.Filled);
        Assert.Equal(1, Volatile.Read(ref placements));
    }

    [Theory]
    [InlineData(200, "{\"failureReason\":\"InvalidPrice\",\"failureText\":\"Invalid price\"}")]
    [InlineData(400, "{\"errorText\":\"Invalid order\"}")]
    [InlineData(401, "{\"errorText\":\"Unauthorized\"}")]
    [InlineData(429, "{\"errorText\":\"Rate limit\"}")]
    public async Task Tradovate_definite_placement_rejection_is_terminal_without_resubmission(int status, string response)
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        int placements = 0;
        fake.Server.OnHttp = req =>
        {
            if (req.Route != "/v1/order/placeorder") return normalHttp(req);
            Interlocked.Increment(ref placements);
            return (status, response);
        };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, Assert.Single(session.Accounts), "DEMO101");
        var order = broker.Limit(OrderDirections.Buy, 1, 4990);
        await Until(() => order.State == OrderStates.Failed && !broker.ActiveOrders.Contains(order));
        Assert.Equal(OrderStates.Failed, order.State);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(0, order.Filled);
        Assert.DoesNotContain(order, broker.ActiveOrders);
        Assert.NotNull(broker.LastError);
        Assert.Equal(1, Volatile.Read(ref placements));
        Assert.False(session.HasOpenPositionsOrOrders);
    }
    [Theory]
    [InlineData(200, "{\"commandId\":91,\"failureReason\":\"Success\"}", false)]
    [InlineData(503, "Response unavailable", false)]
    [InlineData(200, "{\"failureReason\":\"Other\",\"failureText\":\"Amendment refused\"}", true)]
    public async Task Tradovate_replacement_requires_exact_native_total_and_price(int status, string response, bool rejected)
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        int amendments = 0;
        fake.Server.OnHttp = req =>
        {
            if (req.Route != "/v1/order/modifyorder") return normalHttp(req);
            Interlocked.Increment(ref amendments);
            return (status, response);
        };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, Assert.Single(session.Accounts), "DEMO101");
        var order = broker.Limit(OrderDirections.Buy, 3, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && order.QuantityToFill == 3);
        string originalId = order.ExchangeOrderId;
        long id = long.Parse(originalId, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Same(order, broker.Replace(order, 4985, 2));
        await Until(() => Volatile.Read(ref amendments) == 1);
        if (rejected)
        {
            await Until(() => broker.LastError != null && !broker.IsReplacementPending(order));
            Assert.Equal(4990, order.Price);
            Assert.Equal(3, order.QuantityToFill);
            return;
        }
        // Optimistic public fields cannot certify protection capacity.
        order.Price = 4985;
        order.Unfilled = 2;
        Assert.True(broker.IsReplacementPending(order));
        broker.Replace(order, 4980, 1);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref amendments));
        Assert.True(session.HasOpenPositionsOrOrders);
        fake.ReplayOrders.Add(new JsonObject
        {
            ["id"] = id, ["accountId"] = 101, ["contractId"] = 555,
            ["action"] = "Buy", ["ordStatus"] = "Working",
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        });
        fake.ReplayVersions.Add(new JsonObject { ["id"] = 90, ["orderId"] = id, ["orderQty"] = 3, ["orderType"] = "Limit", ["price"] = 4985 });
        await fake.ReconnectUser();
        await Until(() => session.SnapshotReady && order.QuantityToFill == 3);
        Assert.True(broker.IsReplacementPending(order)); // Matching price, wrong TOTAL quantity.
        fake.ReplayFills.Add(new JsonObject
        {
            ["id"] = 9901, ["orderId"] = id, ["contractId"] = 555,
            ["action"] = "Buy", ["qty"] = 1, ["price"] = 4985,
            ["timestamp"] = DateTime.UtcNow.ToString("O"), ["active"] = true
        });
        fake.ReplayVersions.Clear();
        fake.ReplayVersions.Add(new JsonObject { ["id"] = 91, ["orderId"] = id, ["orderQty"] = 2, ["orderType"] = "Limit", ["price"] = 4985 });
        await fake.ReconnectUser();
        await Until(() => !broker.IsReplacementPending(order));
        Assert.Equal(originalId, order.ExchangeOrderId);
        Assert.Same(order, Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == originalId));
        Assert.Equal(2, order.QuantityToFill);
        Assert.Equal(1, order.Unfilled);
        Assert.Equal(1, Volatile.Read(ref amendments));
    }

    [Theory]
    [InlineData(200, "{\"commandId\":91,\"failureReason\":\"Success\"}", false)]
    [InlineData(503, "Response unavailable", false)]
    [InlineData(200, "{\"failureReason\":\"Other\",\"failureText\":\"Amendment refused\"}", true)]
    public async Task Tradovate_trailing_modify_coalesces_until_authoritative_amendment_resolution(int status, string response, bool rejected)
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        var firstRequest = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int amendments = 0;
        fake.Server.OnHttpAsync = async (req, ct) =>
        {
            if (req.Route != "/v1/order/modifyorder") return normalHttp(req);
            var body = JsonNode.Parse(req.Body)!.AsObject();
            if (Interlocked.Increment(ref amendments) == 1)
            {
                firstRequest.TrySetResult(body);
                await release.Task.WaitAsync(ct);
                return (status, response);
            }
            secondRequest.TrySetResult(body);
            return (200, "{\"commandId\":92,\"failureReason\":\"Success\"}");
        };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, Assert.Single(session.Accounts), "DEMO101");
        var order = broker.Stop(OrderDirections.Sell, 3, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && order.TriggerPrice == 4990);
        long id = long.Parse(order.ExchangeOrderId, System.Globalization.CultureInfo.InvariantCulture);
        await Until(() => session.OrdersOf(101, 555).Any(s => s.Id == id && s.Type == "Stop"));
        broker.Modify(order, 4991);
        var first = await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(4991m, first["stopPrice"]!.GetValue<decimal>());
        broker.Modify(order, 4992);
        broker.Modify(order, 4993); // No subsequent market high is needed to deliver this latest price.
        order.Price = 4993; // The runner may already have advanced its public trailing-stop fields.
        order.TriggerPrice = 4993;
        broker.Replace(order, 4980, 1); // Explicit sizing requests remain duplicate-suppressed.
        await fake.PublishFill(new JsonObject
        {
            ["id"] = 9901, ["orderId"] = id, ["contractId"] = 555, ["action"] = "Sell",
            ["qty"] = 1, ["price"] = 4990, ["timestamp"] = DateTime.UtcNow.ToString("O"), ["active"] = true
        });
        await Until(() => order.Filled == 1);
        Assert.Equal(1, Volatile.Read(ref amendments));
        Assert.True(broker.IsReplacementPending(order));
        var firstReplyProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Changed += () =>
        {
            if (release.Task.IsCompleted && Volatile.Read(ref amendments) == 1)
                firstReplyProcessed.TrySetResult();
        };
        release.TrySetResult();
        if (!rejected)
        {
            // Neither a command receipt nor an unknown HTTP outcome resolves A.
            await firstReplyProcessed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await fake.PublishOrder(new JsonObject
            {
                ["id"] = id, ["accountId"] = 101, ["contractId"] = 555, ["action"] = "Sell",
                ["ordStatus"] = "Working", ["timestamp"] = DateTime.UtcNow.ToString("O")
            });
            Assert.Equal(1, Volatile.Read(ref amendments));
            Assert.False(secondRequest.Task.IsCompleted);
            await fake.PublishVersion(new JsonObject
            {
                ["id"] = 91, ["orderId"] = id, ["orderQty"] = 3, ["orderType"] = "Stop", ["stopPrice"] = 4991
            });
        }
        var second = await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(id, second["orderId"]!.GetValue<long>());
        Assert.Equal(4993m, second["stopPrice"]!.GetValue<decimal>());
        Assert.Equal(3, second["orderQty"]!.GetValue<int>()); // One filled plus the THEN-current two remaining.
        Assert.True(broker.IsReplacementPending(order));
        Assert.True(session.HasOpenPositionsOrOrders);
        await fake.PublishVersion(new JsonObject
        {
            ["id"] = 92, ["orderId"] = id, ["orderQty"] = 3, ["orderType"] = "Stop", ["stopPrice"] = 4993
        });
        await Until(() => !broker.IsReplacementPending(order));
        Assert.Equal(4993m, order.TriggerPrice);
        Assert.Equal(2m, order.Unfilled);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
        Assert.Equal(2, Volatile.Read(ref amendments));
        Assert.Single(fake.Orders); // Both amendments retain the one native placement and handle.
    }

    [Theory]
    [InlineData("Canceled", OrderStatus.Canceled, 1)]
    [InlineData("Filled", OrderStatus.Filled, 3)]
    [InlineData("Rejected", OrderStatus.Rejected, 1)]
    public async Task Tradovate_terminal_old_values_resolve_amendment_and_discard_queued_trailing_price(string nativeStatus, OrderStatus expectedStatus, int filled)
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        int amendments = 0;
        fake.Server.OnHttp = req =>
        {
            if (req.Route != "/v1/order/modifyorder") return normalHttp(req);
            Interlocked.Increment(ref amendments);
            return (503, "Response unavailable");
        };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, Assert.Single(session.Accounts), "DEMO101");
        var order = broker.Stop(OrderDirections.Sell, 3, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && order.TriggerPrice == 4990);
        long id = long.Parse(order.ExchangeOrderId, System.Globalization.CultureInfo.InvariantCulture);
        await Until(() => session.OrdersOf(101, 555).Any(s => s.Id == id && s.Type == "Stop"));
        broker.Replace(order, 4991, 2);
        await Until(() => Volatile.Read(ref amendments) == 1 && broker.LastError != null);
        broker.Modify(order, 4993);
        await fake.PublishFill(new JsonObject
        {
            ["id"] = 9902, ["orderId"] = id, ["contractId"] = 555, ["action"] = "Sell",
            ["qty"] = filled, ["price"] = 4990, ["timestamp"] = DateTime.UtcNow.ToString("O"), ["active"] = true
        });
        await fake.PublishOrder(new JsonObject
        {
            ["id"] = id, ["accountId"] = 101, ["contractId"] = 555, ["action"] = "Sell",
            ["ordStatus"] = nativeStatus, ["timestamp"] = DateTime.UtcNow.ToString("O")
        });
        await Until(() => order.Status == expectedStatus && !broker.IsReplacementPending(order));
        Assert.Equal(expectedStatus, order.Status);
        Assert.Equal(4990m, order.TriggerPrice);
        Assert.Equal(3m, order.QuantityToFill);
        Assert.Equal((decimal)filled, order.Filled);
        Assert.False(broker.IsReplacementPending(order));
        Assert.False(session.HasOpenPositionsOrOrders);
        Assert.Empty(broker.ActiveOrders);
        Assert.Equal(1, Volatile.Read(ref amendments));
    }

    [Fact]
    public async Task Tradovate_cancel_command_receipt_retains_obligation_until_native_terminal_replay()
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        int cancels = 0;
        fake.Server.OnHttp = req =>
        {
            if (req.Route != "/v1/order/cancelorder") return normalHttp(req);
            Interlocked.Increment(ref cancels);
            return (200, "{\"commandId\":93,\"failureReason\":\"Success\"}"); // No native cancellation yet.
        };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, Assert.Single(session.Accounts), "DEMO101");
        var order = broker.Limit(OrderDirections.Buy, 3, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0 && order.QuantityToFill == 3);
        long id = long.Parse(order.ExchangeOrderId, System.Globalization.CultureInfo.InvariantCulture);
        await Until(() => session.OrdersOf(101, 555).Any(s => s.Id == id && s.Type == "Limit"));
        var receiptProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Changed += () =>
        {
            if (Volatile.Read(ref cancels) == 1) receiptProcessed.TrySetResult();
        };
        broker.Cancel(order);
        await receiptProcessed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(order.Canceled);
        Assert.Contains(order, broker.ActiveOrders);
        Assert.True(session.HasOpenPositionsOrOrders);
        await fake.PublishFill(new JsonObject
        {
            ["id"] = 9903, ["orderId"] = id, ["contractId"] = 555, ["action"] = "Buy",
            ["qty"] = 1, ["price"] = 4990, ["timestamp"] = DateTime.UtcNow.ToString("O"), ["active"] = true
        });
        await Until(() => order.Filled == 1);
        Assert.Equal(1, Volatile.Read(ref cancels)); // Working/fill events must not resend a receipted cancel.
        fake.ReplayOrders.Add(new JsonObject
        {
            ["id"] = id, ["accountId"] = 101, ["contractId"] = 555, ["action"] = "Buy",
            ["ordStatus"] = "Working", ["timestamp"] = DateTime.UtcNow.ToString("O")
        });
        fake.ReplayVersions.Add(new JsonObject { ["id"] = 94, ["orderId"] = id, ["orderQty"] = 3, ["orderType"] = "Limit", ["price"] = 4990 });
        await fake.ReconnectUser();
        await Until(() => session.SnapshotReady && Volatile.Read(ref cancels) == 2);
        Assert.Equal(2, Volatile.Read(ref cancels));
        Assert.False(order.Canceled);
        await fake.PublishOrder(new JsonObject
        {
            ["id"] = id, ["accountId"] = 101, ["contractId"] = 555, ["action"] = "Buy",
            ["ordStatus"] = "Canceled", ["timestamp"] = DateTime.UtcNow.ToString("O")
        });
        await Until(() => order.Canceled && !broker.ActiveOrders.Any() && !session.HasOpenPositionsOrOrders);
        Assert.True(order.Canceled);
        Assert.Equal(1m, order.Filled);
        Assert.Empty(broker.ActiveOrders);
        Assert.False(session.HasOpenPositionsOrOrders);
    }

    [Fact]
    public async Task Tradovate_cancel_intent_discards_queued_trailing_price_even_if_prior_amendment_acknowledges()
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        int amendments = 0, cancels = 0;
        fake.Server.OnHttp = req =>
        {
            if (req.Route == "/v1/order/modifyorder")
            {
                Interlocked.Increment(ref amendments);
                return (200, "{\"commandId\":95,\"failureReason\":\"Success\"}");
            }
            if (req.Route == "/v1/order/cancelorder")
            {
                Interlocked.Increment(ref cancels);
                return (200, "{\"commandId\":96,\"failureReason\":\"Success\"}");
            }
            return normalHttp(req);
        };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, Assert.Single(session.Accounts), "DEMO101");
        var order = broker.Stop(OrderDirections.Sell, 3, 4990);
        await Until(() => order.ExchangeOrderId.Length > 0);
        long id = long.Parse(order.ExchangeOrderId, System.Globalization.CultureInfo.InvariantCulture);
        await Until(() => session.OrdersOf(101, 555).Any(s => s.Id == id && s.Type == "Stop"));
        broker.Modify(order, 4991);
        await Until(() => Volatile.Read(ref amendments) == 1);
        broker.Modify(order, 4993);
        broker.Cancel(order);
        await Until(() => Volatile.Read(ref cancels) == 1);
        broker.Modify(order, 4994); // Cancel intent also rejects later trailing updates.
        await fake.PublishVersion(new JsonObject
        {
            ["id"] = 95, ["orderId"] = id, ["orderQty"] = 3, ["orderType"] = "Stop", ["stopPrice"] = 4991
        });
        await Until(() => order.TriggerPrice == 4991 && !broker.IsReplacementPending(order));
        Assert.Equal(1, Volatile.Read(ref amendments));
        Assert.False(broker.IsReplacementPending(order));
        Assert.True(session.HasOpenPositionsOrOrders);
        await fake.PublishOrder(new JsonObject
        {
            ["id"] = id, ["accountId"] = 101, ["contractId"] = 555, ["action"] = "Sell",
            ["ordStatus"] = "Canceled", ["timestamp"] = DateTime.UtcNow.ToString("O")
        });
        await Until(() => order.Canceled);
        Assert.True(order.Canceled);
        Assert.False(session.HasOpenPositionsOrOrders);
        Assert.Equal(1, Volatile.Read(ref amendments));
    }

    [Fact]
    public async Task Tradovate_newer_ready_price_supersedes_the_disconnected_queue_before_broker_sync()
    {
        using var fake = new FakeTradovate();
        var normalHttp = fake.Server.OnHttp!;
        int amendments = 0;
        var firstRequest = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Server.OnHttp = req =>
        {
            if (req.Route != "/v1/order/modifyorder") return normalHttp(req);
            var body = JsonNode.Parse(req.Body)!.AsObject();
            if (Interlocked.Increment(ref amendments) == 1) firstRequest.TrySetResult(body);
            else secondRequest.TrySetResult(body);
            return (200, "{\"commandId\":10991,\"failureReason\":\"Success\"}");
        };
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        session.Start();
        await Until(() => session.SnapshotReady);
        TradovateBroker? observedBroker = null;
        Order? observedOrder = null;
        int phase = 0;
        // Registered first: deliver a newer user edit before the broker drains reconnect work.
        session.Changed += () =>
        {
            if (observedBroker == null || observedOrder == null) return;
            if (!session.SnapshotReady && Interlocked.CompareExchange(ref phase, 2, 1) == 1)
                observedBroker.Modify(observedOrder, 4991);
            else if (session.SnapshotReady && Interlocked.CompareExchange(ref phase, 3, 2) == 2)
                observedBroker.Modify(observedOrder, 4993);
        };
        using var broker = new TradovateBroker(new InstrumentData(Es, new SimulatedFeed()), session, Assert.Single(session.Accounts), "DEMO101");
        observedBroker = broker;
        var order = broker.Stop(OrderDirections.Sell, 3, 4990);
        observedOrder = order;
        await Until(() => order.ExchangeOrderId.Length > 0);
        long id = long.Parse(order.ExchangeOrderId, CultureInfo.InvariantCulture);
        await Until(() => session.OrdersOf(101, 555).Any(s => s.Id == id && s.Type == "Stop"));
        fake.ReplayOrders.Add(new JsonObject
        {
            ["id"] = id, ["accountId"] = 101, ["contractId"] = 555, ["action"] = "Sell",
            ["ordStatus"] = "Working", ["timestamp"] = DateTime.UtcNow.ToString("O")
        });
        fake.ReplayVersions.Add(new JsonObject { ["id"] = 10990, ["orderId"] = id, ["orderQty"] = 3, ["orderType"] = "Stop", ["stopPrice"] = 4990 });
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Changed += () =>
        {
            if (Volatile.Read(ref phase) == 3 && !broker.IsReplacementPending(order)) settled.TrySetResult();
        };
        Interlocked.Exchange(ref phase, 1);
        await fake.ReconnectUser();
        var first = await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(4993m, first["stopPrice"]!.GetValue<decimal>());
        Assert.Equal(3, first["orderQty"]!.GetValue<int>());
        await fake.PublishVersion(new JsonObject
        {
            ["id"] = 10991, ["orderId"] = id, ["orderQty"] = 3, ["orderType"] = "Stop", ["stopPrice"] = 4993
        });
        var outcome = await Task.WhenAny(settled.Task, secondRequest.Task).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Same(settled.Task, outcome);
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(4993m, order.TriggerPrice);
        Assert.Equal(1, Volatile.Read(ref amendments));
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
    }

}
