using System.Globalization;
using System.Text.RegularExpressions;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>Rithmic contra un servidor de mentira: historia paginada, velas, contrato del mes y órdenes.</summary>
public class RithmicTests
{
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
        using var broker = new RithmicBroker(data, session, "ACC1", "ACC1", ct => feed.ContractAsync(Es, ct));
        Assert.False(broker.IsReal);

        // mercado: se llena y la posición llega por la planta de PnL
        broker.Market(OrderDirections.Buy, 2);
        await Until(() => broker.Position.Volume == 2);
        Assert.Equal(2m, broker.Position.Volume);
        Assert.Equal(5000m, broker.Position.AveragePrice);
        await Until(() => !broker.ActiveOrders.Any());
        Assert.Empty(broker.ActiveOrders);

        // límite: queda activa con su número de Rithmic
        var limit = broker.Limit(OrderDirections.Sell, 1, 5010);
        await Until(() => limit.ExchangeOrderId.Length > 0);
        Assert.StartsWith("B", limit.ExchangeOrderId);
        Assert.Contains(limit, broker.ActiveOrders);
        Assert.Equal(5010.0, server.Open[limit.ExchangeOrderId].Price);

        // modificar el precio
        broker.Modify(limit, 5012);
        await Until(() => server.Open[limit.ExchangeOrderId].Price == 5012);
        Assert.Equal(5012.0, server.Open[limit.ExchangeOrderId].Price);
        Assert.Equal(5012m, limit.Price);

        // cancelar
        broker.Cancel(limit);
        await Until(() => !broker.ActiveOrders.Contains(limit));
        Assert.DoesNotContain(limit, broker.ActiveOrders);
        Assert.Equal(OrderStatus.Canceled, limit.Status);

        // cerrar la posición
        broker.Flatten();
        await Until(() => broker.Position.Volume == 0);
        Assert.Equal(0m, broker.Position.Volume);
    }

    [Fact]
    public async Task Order_tags_carry_a_per_process_session_component()
    {
        // Order ids restart at 1 in every process: the tag also needs a part that tells this process from earlier ones
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        using var feed = new RithmicFeed(Settings(server));
        using var session = new RithmicTradingSession(Settings(server));
        session.Start();
        await Until(() => session.IsConnected);
        Assert.True(session.IsConnected, session.StatusText);
        using var broker = new RithmicBroker(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1", ct => feed.ContractAsync(Es, ct));

        var limit = broker.Limit(OrderDirections.Sell, 1, 5010);
        var market = broker.Market(OrderDirections.Buy, 1);
        await Until(() => limit.ExchangeOrderId.Length > 0 && market.ExchangeOrderId.Length > 0);
        string[] tags;
        lock (server.Tags) tags = server.Tags.ToArray();

        Assert.Equal(2, tags.Length);
        foreach (var tag in tags) Assert.Matches("^th-[0-9a-z]{4,12}-[0-9]+$", tag);
        var parts = tags.Select(t => Regex.Match(t, "^th-([0-9a-z]+)-([0-9]+)$")).ToArray();
        Assert.Single(parts.Select(m => m.Groups[1].Value).Distinct()); // one session component for the whole process
        Assert.Equal(new[] { limit.Id, market.Id }.Order(), parts.Select(m => long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).Order());

        // the session matches each order by the tag stored for it
        string limitTag = tags.Single(t => t.EndsWith("-" + limit.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.Equal(limit.ExchangeOrderId, session.OrderByTag(limitTag)?.BasketId);
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
        using var broker = new RithmicBroker(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1", ct => feed.ContractAsync(Es, ct));

        var ours = broker.Limit(OrderDirections.Sell, 1, 5010);
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
        broker.Modify(earlier, 4995);
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

    [Fact]
    public async Task Session_tag_comes_from_the_process_start_and_differs_between_processes()
    {
        var start = new DateTime(2026, 10, 23, 9, 30, 0, DateTimeKind.Utc);
        string a = RithmicBroker.SessionTagFor(start);
        Assert.Matches("^[0-9a-z]{8}$", a); // short ASCII
        Assert.Equal(a, RithmicBroker.SessionTagFor(start)); // stable for one process
        Assert.Equal(a, RithmicBroker.SessionTagFor(start.ToLocalTime())); // the same instant in local time
        Assert.NotEqual(a, RithmicBroker.SessionTagFor(start.AddMilliseconds(1))); // a process started 1 ms later
        Assert.NotEqual(a, RithmicBroker.SessionTagFor(start.AddHours(-2))); // an earlier process the same day

        // this process's component is its start time (base 36 Unix milliseconds)
        long ms = 0;
        foreach (char ch in RithmicBroker.SessionTag) ms = ms * 36 + (char.IsAsciiDigit(ch) ? ch - '0' : ch - 'a' + 10);
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            var started = process.StartTime.ToUniversalTime();
            Assert.InRange(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, started.AddSeconds(-1), started.AddSeconds(1));
        }

        // and it is the component this process puts in its tags
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        using var feed = new RithmicFeed(Settings(server));
        using var session = new RithmicTradingSession(Settings(server));
        session.Start();
        await Until(() => session.IsConnected);
        using var broker = new RithmicBroker(new InstrumentData(Es, new SimulatedFeed()), session, "ACC1", "ACC1", ct => feed.ContractAsync(Es, ct));
        var limit = broker.Limit(OrderDirections.Buy, 1, 4990);
        await Until(() => limit.ExchangeOrderId.Length > 0);
        string tag;
        lock (server.Tags) tag = Assert.Single(server.Tags);
        Assert.Equal("th-" + RithmicBroker.SessionTag + "-" + limit.Id.ToString(CultureInfo.InvariantCulture), tag);
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
