using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>Historial de la cuenta: operaciones armadas desde las ejecuciones, guardado en disco y datos de Rithmic.</summary>
public class AccountTests
{
    private static readonly DateTime T = new(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc);

    private static AccountFill F(int sec, bool buy, decimal qty, decimal price, decimal pv = 50, decimal comm = 0, string sym = "ESZ6") =>
        new(Guid.NewGuid().ToString("N"), T.AddSeconds(sec), sym, buy, qty, price, pv, comm);

    [Fact]
    public void Sumar_reducir_y_dar_vuelta_arma_las_operaciones_justas()
    {
        var r = RoundTrips.Analyze(new[]
        {
            F(0, true, 1, 5000),      // largo 1
            F(10, true, 1, 5002),     // suma: promedio 5001
            F(20, false, 1, 5005),    // reduce: +4 puntos = +200
            F(30, false, 1, 5003),    // cierra: +2 puntos = +100 → operación 1 (+300)
            F(40, false, 2, 5003),    // corto 2
            F(50, true, 3, 5000),     // cierra el corto (+3 × 2 × 50 = +300) y queda largo 1
            F(60, false, 1, 4999),    // cierra el largo (−1 × 50 = −50)
        });

        Assert.Equal(3, r.Trades.Count);
        var t1 = r.Trades[0];
        Assert.True(t1.IsLong);
        Assert.Equal(2, t1.Quantity);
        Assert.Equal(5001m, t1.OpenPrice);
        Assert.Equal(5004m, t1.ClosePrice);
        Assert.Equal(300m, t1.NetPnl);
        Assert.False(r.Trades[1].IsLong);
        Assert.Equal(300m, r.Trades[1].NetPnl);
        Assert.True(r.Trades[2].IsLong);
        Assert.Equal(-50m, r.Trades[2].NetPnl);
        Assert.Equal(550m, r.RealizedNet);
        Assert.Equal(r.Realized.Sum(x => x.Net), r.RealizedNet);
    }

    [Fact]
    public void Las_comisiones_se_descuentan_y_cada_contrato_va_por_separado()
    {
        var r = RoundTrips.Analyze(new[]
        {
            F(0, true, 2, 5000, comm: 4),
            F(5, true, 10, 20000, pv: 2, comm: 5, sym: "MNQZ6"),
            F(10, false, 2, 5001, comm: 4),
            F(15, false, 10, 19990, pv: 2, comm: 5, sym: "MNQZ6"),
        });

        Assert.Equal(2, r.Trades.Count);
        var es = r.Trades.Single(t => t.Symbol == "ESZ6");
        Assert.Equal(100m, es.Pnl);
        Assert.Equal(8m, es.Commission);
        Assert.Equal(92m, es.NetPnl);
        var mnq = r.Trades.Single(t => t.Symbol == "MNQZ6");
        Assert.Equal(-200m, mnq.Pnl);
        Assert.Equal(-210m, mnq.NetPnl);
        Assert.Equal(18m, r.Commission);
        Assert.Equal(92m - 210m, r.RealizedNet);
    }

    [Fact]
    public void Una_posicion_abierta_no_es_una_operacion_pero_lo_parcial_si_suma()
    {
        var r = RoundTrips.Analyze(new[] { F(0, true, 3, 5000), F(10, false, 1, 5004) });
        Assert.Empty(r.Trades);
        Assert.Equal(200m, r.RealizedNet);
    }

    [Fact]
    public void El_historial_queda_guardado_y_no_repite_ejecuciones()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tradinghub-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var j = new AccountJournal(dir);
            var a = F(0, true, 1, 5000);
            Assert.True(j.Add("rithmic|r|ACC1", a));
            Assert.False(j.Add("rithmic|r|ACC1", a)); // la misma (Rithmic la repite al reconectar)
            Assert.True(j.Add("rithmic|r|ACC1", F(10, false, 1, 5002)));
            File.AppendAllText(Directory.GetFiles(dir).Single(), "{\"Id\":\"cortad"); // línea a medio escribir

            var again = new AccountJournal(dir);
            var fills = again.FillsOf("rithmic|r|ACC1");
            Assert.Equal(2, fills.Count);
            Assert.Equal(100m, RoundTrips.Analyze(fills).RealizedNet);

            again.Restart("rithmic|r|ACC1");
            Assert.Empty(again.FillsOf("rithmic|r|ACC1"));
            Assert.Empty(new AccountJournal(dir).FillsOf("rithmic|r|ACC1"));
            Assert.Single(Directory.GetFiles(dir, "*.bak")); // lo anterior queda guardado aparte
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Rithmic_manda_el_saldo_y_cada_ejecucion()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000, StartBalance = 50_000 };
        var settings = new RithmicSettings { Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000 };
        using var session = new RithmicTradingSession(settings);
        var fills = new List<RithmicFill>();
        var entryFill = new TaskCompletionSource<RithmicFill>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FillReceived += f =>
        {
            lock (fills) fills.Add(f);
            if (f.IsBuy) entryFill.TrySetResult(f);
        };
        session.Start();
        for (int i = 0; i < 200 && !session.IsConnected; i++) await Task.Delay(30);
        Assert.True(session.IsConnected, session.StatusText);
        for (int i = 0; i < 100 && session.SummaryFor("ACC1")?.Balance == null; i++) await Task.Delay(30);
        Assert.Equal(50_000m, session.SummaryFor("ACC1")!.Balance);

        await session.PlaceAsync("ACC1", "ESZ6", "CME", true, 2, 2, 0, 0, "t1", CancellationToken.None);
        // Placement acknowledgement precedes execution; do not move the fake market before the entry actually fills.
        var entry = await entryFill.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(5000m, entry.Price);
        Assert.Equal(2m, entry.Quantity);
        server.MarketPrice = 5004;
        await session.PlaceAsync("ACC1", "ESZ6", "CME", false, 2, 2, 0, 0, "t2", CancellationToken.None);
        // el saldo (planta de PnL) y las ejecuciones (planta de órdenes) llegan por conexiones distintas: se esperan las dos cosas
        for (int i = 0; i < 200 && (session.SummaryFor("ACC1")?.Balance != 50_400m || fills.Count < 2 || session.OrdersOf("ACC1").Any(o => !o.Done)); i++) await Task.Delay(30);

        var sum = session.SummaryFor("ACC1")!;
        Assert.Equal(50_400m, sum.Balance); // +4 puntos × 2 × 50
        Assert.Equal(400m, sum.ClosedPnl);
        Assert.Equal(400m, sum.DayPnl);
        Assert.Equal(2, fills.Count);
        Assert.Equal(2, session.FillsOf("ACC1").Count);
        Assert.Equal(2, session.OrdersOf("ACC1").Count);
        Assert.All(session.OrdersOf("ACC1"), o => Assert.True(o.Done));
        var p = Assert.Single(session.PositionsOf("ACC1"));
        Assert.Equal("ESZ6", p.Symbol);
        Assert.Equal(0m, p.Position.Net);

        // las operaciones salen de las ejecuciones con el valor del punto del contrato
        var r = RoundTrips.Analyze(fills.Select(f => new AccountFill(f.Id, f.Time, f.Symbol, f.IsBuy, f.Quantity, f.Price, RithmicFeed.PointValueOfContract(f.Symbol))));
        Assert.Equal(400m, Assert.Single(r.Trades).NetPnl);
    }
}
