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
        Assert.DoesNotContain(limit, broker.ActiveOrders);
    }
}
