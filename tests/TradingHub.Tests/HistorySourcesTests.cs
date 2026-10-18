using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>
/// La historia de cada fuente por separado (Rithmic y Databento) y la combinada: Rithmic manda y Databento completa los
/// minutos que le faltan; «rithmic-solo» y «databento» son cada una sola.
/// </summary>
public class HistorySourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-sources-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Day1 = new(2026, 9, 14);
    private static readonly DateOnly Day2 = new(2026, 9, 15);
    private static readonly DateOnly Day3 = new(2026, 9, 16);

    public HistorySourcesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private static Instrument Nq(string feed) => new() { Symbol = "NQ", FeedId = feed, Exchange = "CME", TickSize = 0.25m, PointValue = 20 };

    /// <summary>Una operación por minuto entre dos horas (precio base distinto por fuente para reconocerlas).</summary>
    private void Write(string feed, string symbol, DateOnly day, int fromMinute, int toMinute, decimal basePrice, long firstId)
    {
        var trades = new List<Trade>();
        for (int m = fromMinute; m < toMinute; m++)
            trades.Add(new Trade(firstId + m, day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(m).AddSeconds(10), basePrice + m * 0.25m, 1, TradeSide.Buy));
        HistoricalData.WriteDay(_root, new Instrument { Symbol = symbol, FeedId = feed, Exchange = "CME", TickSize = 0.25m, PointValue = 20 }, day, trades, replace: true);
    }

    [Fact]
    public void Combinada_Rithmic_manda_y_Databento_completa_los_minutos_que_faltan()
    {
        // Rithmic grabó de 10:00 a 10:30; Databento tiene de 9:00 a 12:00
        Write("rithmic", "NQ", Day1, 600, 630, 20_000m, 5_000);
        Write("databento", "NQ", Day1, 540, 720, 30_000m, 1);

        var combined = HistorySources.ReadDay(_root, "rithmic", "NQ", Day1);
        Assert.Equal(180, combined.Count); // 30 de Rithmic + 150 de Databento, sin repetir minutos
        var inRecording = combined.Where(t => t.Time.Hour == 10 && t.Time.Minute < 30).ToList();
        Assert.Equal(30, inRecording.Count);
        Assert.All(inRecording, t => Assert.True(t.Price < 30_000m)); // en esos minutos, lo de Rithmic
        Assert.All(combined.Where(t => t.Time.Hour == 9 || t.Time.Hour == 11), t => Assert.True(t.Price >= 30_000m)); // afuera, Databento

        // ordenadas por hora, con ids que crecen y no se repiten entre fuentes
        for (int i = 1; i < combined.Count; i++)
        {
            Assert.True(combined[i].Time >= combined[i - 1].Time);
            Assert.True(combined[i].Id > combined[i - 1].Id);
        }
    }

    [Fact]
    public void Solo_Rithmic_y_solo_Databento_no_se_mezclan()
    {
        Write("rithmic", "NQ", Day1, 600, 630, 20_000m, 5_000);
        Write("databento", "NQ", Day1, 540, 720, 30_000m, 1);
        Write("databento", "NQ", Day2, 540, 600, 30_000m, 1);

        Assert.Equal(30, HistorySources.ReadDay(_root, "rithmic-solo", "NQ", Day1).Count);
        Assert.Equal(180, HistorySources.ReadDay(_root, "databento", "NQ", Day1).Count);
        Assert.Empty(HistorySources.ReadDay(_root, "rithmic-solo", "NQ", Day2));
        Assert.Equal(60, HistorySources.ReadDay(_root, "rithmic", "NQ", Day2).Count); // el día que Rithmic no tiene, entero de Databento

        Assert.True(HistoricalData.HasDay(_root, "rithmic", "NQ", Day2));
        Assert.False(HistoricalData.HasDay(_root, "rithmic-solo", "NQ", Day2));
        Assert.Equal(new[] { Day1, Day2 }, HistorySources.AvailableDays(_root, "rithmic", "NQ"));
        Assert.Equal(new[] { Day1 }, HistorySources.AvailableDays(_root, "rithmic-solo", "NQ"));
    }

    [Fact]
    public void Lo_de_otras_conexiones_no_se_completa_con_Databento()
    {
        Write("binance-futures", "NQ", Day1, 0, 10, 100m, 1);
        Write("databento", "NQ", Day1, 0, 60, 30_000m, 1);
        Assert.Equal(10, HistorySources.ReadDay(_root, "binance-futures", "NQ", Day1).Count);
        Assert.Equal(new[] { "binance-futures" }, HistorySources.Folders("binance-futures"));
        Assert.Equal(new[] { "rithmic", "databento" }, HistorySources.Folders("rithmic"));
        Assert.Equal(new[] { "tradovate" }, HistorySources.Folders("tradovate-solo"));
    }

    [Fact]
    public void Para_probar_aparecen_las_tres_historias_juntas()
    {
        Write("rithmic", "NQ", Day2, 600, 630, 20_000m, 1);
        Write("rithmic", "NQ", Day3, 600, 630, 20_000m, 1);
        Write("databento", "NQ", Day1, 0, 600, 30_000m, 1);
        Write("databento", "NQ", Day2, 0, 600, 30_000m, 1);
        Write("rithmic", "MNQ", Day1, 600, 610, 20_000m, 1);
        Write("databento", "ES", Day1, 600, 610, 6_000m, 1);

        var sets = HistorySources.Datasets(_root);
        var nq = sets.Where(s => s.Symbol == "NQ").ToList();
        Assert.Equal(new[] { "rithmic", "rithmic-solo", "databento" }, nq.Select(s => s.FeedId));
        Assert.True(nq[0].Combined);
        Assert.Equal(new[] { Day1, Day2, Day3 }, nq[0].Days.Select(d => d.Day));
        Assert.Equal(new[] { Day2, Day3 }, nq[1].Days.Select(d => d.Day));
        Assert.Equal(new[] { Day1, Day2 }, nq[2].Days.Select(d => d.Day));
        Assert.Equal("Rithmic + Databento", HistorySources.Describe(nq[0]));
        Assert.Equal("only Rithmic", HistorySources.Describe(nq[1]));
        Assert.Equal("Databento", HistorySources.Describe(nq[2]));

        // con una sola fuente, una sola entrada (sin «solo»)
        Assert.Equal(new[] { "rithmic" }, sets.Where(s => s.Symbol == "MNQ").Select(s => s.FeedId));
        Assert.False(sets.Single(s => s.Symbol == "MNQ").Combined);
        Assert.Equal(new[] { "databento" }, sets.Where(s => s.Symbol == "ES").Select(s => s.FeedId));
    }

    [Fact]
    public void El_backtest_carga_la_historia_que_se_eligio()
    {
        Write("rithmic", "NQ", Day1, 600, 630, 20_000m, 5_000);
        Write("databento", "NQ", Day1, 540, 720, 30_000m, 1);
        Write("databento", "NQ", Day2, 540, 600, 30_000m, 1);
        DateTime from = Day1.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), to = Day3.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        Assert.Equal(240, TickData.Load(_root, Nq("rithmic"), from, to, null, CancellationToken.None).Count);
        Assert.Equal(30, TickData.Load(_root, Nq("rithmic-solo"), from, to, null, CancellationToken.None).Count);
        Assert.Equal(240, TickData.Load(_root, Nq("databento"), from, to, null, CancellationToken.None).Count);

        // y la lectura por rango (replay, contexto) igual
        Assert.Equal(240, HistoricalData.Read(_root, "rithmic", "NQ", from, to).Count());
        Assert.Equal(0.25m, HistorySources.StoredTickSize(_root, "rithmic-solo", "NQ"));
    }
}
