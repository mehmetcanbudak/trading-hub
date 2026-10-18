using System.Reflection;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>
/// Lo que pasó el 3/10: dos procesos grabando el mismo archivo de ticks lo mezclaron, la app cargó basura
/// (velas gigantes falsas) y un id adelantado hizo que se descartara todo el vivo (el gráfico quedó congelado).
/// </summary>
public class DataIntegrityTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m };
    private static readonly DateTime Day = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);

    private static List<Trade> Stream(int n, int seed = 7)
    {
        var rnd = new Random(seed);
        var list = new List<Trade>(n);
        decimal price = 84_700m;
        for (int i = 0; i < n; i++)
        {
            price += rnd.Next(-3, 4) * 0.1m;
            decimal vol = Math.Round((decimal)rnd.NextDouble() * 2m + 0.001m, 3);
            list.Add(new Trade(3_474_393_766 + i, Day.AddMilliseconds(i * 40), price, vol, rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        return list;
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tradinghub_integrity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Dos_escritores_mezclados_no_dejan_pasar_basura()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "2026-10-04.vtk");
            var trades = Stream(60_000);
            var byId = trades.ToDictionary(t => t.Id);

            // Reproduce historical overlapping writes offline: Windows prevents two writers opening one path.
            int sharedOffset, resumedOffset;
            using (var a = new TickFile.Writer(path, Btc))
            {
                for (int i = 0; i < 20_000; i++) a.Write(trades[i]);
                a.Flush();
                sharedOffset = checked((int)new FileInfo(path).Length);
                for (int i = 20_000; i < 21_000; i++) a.Write(trades[i]);
                a.Flush();
                resumedOffset = checked((int)new FileInfo(path).Length);
                for (int i = 21_000; i < 22_000; i++) a.Write(trades[i]);
            }
            byte[] firstWriter = File.ReadAllBytes(path);
            string secondPath = Path.Combine(dir, "second-writer.vtk");
            File.WriteAllBytes(secondPath, firstWriter[..sharedOffset]);
            using (var b = new TickFile.Writer(secondPath, Btc))
                for (int i = 20_812; i < 30_000; i++) b.Write(trades[i]);
            File.Copy(secondPath, path, overwrite: true);
            using (var overlap = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                overlap.Position = resumedOffset;
                overlap.Write(firstWriter.AsSpan(resumedOffset));
            }
            using (var c = new TickFile.Writer(path, Btc))
                for (int i = 30_000; i < trades.Count; i++) c.Write(trades[i]);

            var read = TickFile.Read(path, out int dropped);
            Assert.True(dropped > 0, "tenía que encontrar el tramo dañado");
            // todo lo que queda es real: mismo tiempo, precio y volumen que la operación original con ese id
            var fake = read.Where(t => !byId.TryGetValue(t.Id, out var o) || o.Time != t.Time || o.Price != t.Price || o.Volume != t.Volume).ToList();
            Assert.True(fake.Count == 0, $"{fake.Count} operaciones falsas de {read.Count} (descartadas {dropped}); la primera: {fake.FirstOrDefault()}");

            // lo de antes y lo de después de la mezcla se conserva (como mucho se pierde un tramo de 4096 al salir
            // de la zona mezclada; ese hueco se vuelve a pedir al exchange al cargar)
            int before = read.Count(t => t.Id < trades[15_000].Id), after = read.Count(t => t.Id >= trades[30_000].Id);
            Assert.True(before >= 15_000 && after >= 30_000 - 4_097, $"antes {before}, después {after}, leídas {read.Count}, descartadas {dropped}");

            // reparar deja el archivo limpio y legible sin descartes
            Assert.True(TickFile.Repair(path, Btc) > 0);
            var again = TickFile.Read(path, out int dropped2);
            Assert.Equal(0, dropped2);
            Assert.Equal(read.Count, again.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Un_archivo_sano_se_lee_entero()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "2026-10-04.vtk");
            var trades = Stream(10_000, seed: 3);
            using (var w = new TickFile.Writer(path, Btc))
                foreach (var t in trades) w.Write(t);
            // historia vieja agregada después (vuelve atrás: keyframe) también es válida
            using (var w = new TickFile.Writer(path, Btc))
                foreach (var t in trades.Take(500)) w.Write(t);

            var read = TickFile.Read(path, out int dropped);
            Assert.Equal(0, dropped);
            Assert.Equal(10_500, read.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Solo_un_proceso_puede_grabar_cada_instrumento()
    {
        var dir = TempDir();
        try
        {
            using var first = TickFile.TryLock(dir, "binance-futures", "BTCUSDT");
            Assert.NotNull(first);
            Assert.Null(TickFile.TryLock(dir, "binance-futures", "BTCUSDT"));
            using var otherInstrument = TickFile.TryLock(dir, "binance-futures", "ETHUSDT");
            Assert.NotNull(otherInstrument); // otro instrumento sí
            first!.Dispose();
            using var again = TickFile.TryLock(dir, "binance-futures", "BTCUSDT");
            Assert.NotNull(again);

            // el grabador que no consigue el permiso no escribe nada
            using var rec = new TickRecorder(dir, new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m });
            Assert.False(rec.IsActive);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Un_id_adelantado_no_congela_el_vivo()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        data.Trades.Add(new Trade(100, Day, 84_700m, 1, TradeSide.Buy));
        data.Trades.Add(new Trade(9_999_999, Day.AddSeconds(1), 84_701m, 1, TradeSide.Buy)); // basura con id del futuro
        typeof(InstrumentData).GetField("_historyMerged", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(data, true);

        IMarketDataSink sink = data;
        sink.OnTrades(Btc, new[]
        {
            new Trade(100, Day, 84_700m, 1, TradeSide.Buy), // repetida: se ignora
            new Trade(101, Day.AddSeconds(2), 84_702m, 1, TradeSide.Buy),
            new Trade(102, Day.AddSeconds(3), 84_703m, 1, TradeSide.Sell),
        });
        data.Pump();
        Assert.Equal(4, data.Trades.Count);
        Assert.Equal(84_703m, data.LastPrice);
    }

    [Fact]
    public void Una_operacion_fuera_de_orden_no_infla_la_vela_actual()
    {
        var store = new TradeStore(Btc);
        store.Add(new Trade(1, Day, 84_700m, 1, TradeSide.Buy));
        store.Add(new Trade(2, Day.AddMinutes(1), 84_710m, 1, TradeSide.Buy));
        store.Add(new Trade(3, Day.AddMinutes(1).AddSeconds(5), 84_705m, 1, TradeSide.Sell));
        store.Add(new Trade(4, Day.AddDays(-88), 166_543m, 25, TradeSide.Sell)); // tiempo de julio, precio absurdo
        var series = new CandleSeries(Btc, ChartPeriod.M1);
        series.Rebuild(store);
        Assert.Equal(2, series.Count);
        Assert.Equal(Btc.ToTicks(84_710m), series[1].HighTicks);
        Assert.Equal(2m, series[1].Volume);
    }
}
