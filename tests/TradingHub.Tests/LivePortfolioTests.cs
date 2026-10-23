using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

/// <summary>Cartera en vivo: contabilidad por estrategia, reglas de tamaño y multiplicador.</summary>
[Collection("LiveStore")] // cambian la carpeta de la cartera (estática): no en paralelo
public class LivePortfolioTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void La_contabilidad_propia_cierra_parciales_y_vueltas()
    {
        var l = new LiveLedger("k", 1);
        l.Apply(new LiveFill(T0, true, 2, 100));
        l.Apply(new LiveFill(T0.AddMinutes(1), false, 1, 110)); // cierra la mitad: +10
        Assert.Equal(1m, l.Position);
        Assert.Equal(10m, l.Realized);
        Assert.Equal(100m, l.AveragePrice);
        l.Apply(new LiveFill(T0.AddMinutes(2), false, 3, 105)); // cierra 1 (+5) y queda corto 2 a 105
        Assert.Equal(-2m, l.Position);
        Assert.Equal(15m, l.Realized);
        Assert.Equal(105m, l.AveragePrice);
        Assert.Equal(2, l.Trades.Count);
        Assert.Equal(-10m, l.OpenPnL(110)); // corto 2 desde 105 con el precio en 110
    }

    private static LiveStats Stat(string key, decimal size, params decimal[] pnls)
    {
        var l = new LiveLedger(key, 1);
        for (int i = 0; i < pnls.Length; i++)
        {
            l.Apply(new LiveFill(T0.AddMinutes(i * 2), true, 1, 100));
            l.Apply(new LiveFill(T0.AddMinutes(i * 2 + 1), false, 1, 100 + pnls[i]));
        }

        return LiveStore.StatsOf(new LiveRunnerInfo { Key = key, Strategy = key, Running = true, Size = size }, l, 10_000);
    }

    [Fact]
    public void Las_reglas_suben_a_la_que_rinde_y_pausan_a_la_que_se_degrada()
    {
        var good = Stat("buena", 1, 30, -10, 25, 20, -10, 30, 15, -5, 40, 20, 10, -10);
        var bad = Stat("mala", 1, 20, 30, 10, -40, -30, -20, -35, -25, -30, -20, -15, -30, -25, -20, -10, -30);
        var young = Stat("nueva", 1, 50, 60);
        var p = LiveSizing.SuggestByRules(new[] { good, bad, young });
        Assert.Equal(1.25m, p.Items.Single(i => i.Key == "buena").ProposedSize);
        Assert.Equal(0m, p.Items.Single(i => i.Key == "mala").ProposedSize);
        Assert.DoesNotContain(p.Items, i => i.Key == "nueva"); // poca muestra: no se toca
        Assert.All(p.Items, i => Assert.Equal("pending", i.Status));
    }

    /// <summary>Compra 2 en la primera vela cerrada.</summary>
    private sealed class BuyTwo : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            if (IsFlat && ActiveOrders.Count == 0) Buy(2);
        }
    }

    [Fact]
    public void El_tamano_de_la_cartera_multiplica_las_ordenes_y_queda_registrado()
    {
        var inst = new Instrument { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m };
        string previousRoot = LiveStore.Root;
        LiveStore.Root = Path.Combine(Path.GetTempPath(), "tradinghub_live_" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new InstrumentData(inst, new SimulatedFeed());
            long id = 1;
            for (int i = 0; i < 5; i++) data.Trades.Add(new Trade(id++, T0.AddSeconds(i * 30), 100 + i, 1, TradeSide.Buy));
            var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());
            var runner = new LiveStrategyRunner(new BuyTwo(), data, broker, ChartPeriod.M1) { SizeMultiplier = 1.5m };
            runner.Start();

            // llega una operación nueva que cierra la vela: la estrategia pide 2 y con ×1,5 se mandan 3
            int start = data.Trades.Count;
            data.Trades.Add(new Trade(id++, T0.AddMinutes(3), 106, 1, TradeSide.Buy));
            var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ((Action<int, int>?)field!.GetValue(data))?.Invoke(start, 1);

            Assert.Equal(3m, broker.Position!.Volume);
            Assert.Equal(3m, runner.Ledger.Position);
            var saved = LiveStore.LoadLedger(runner.Key, 1);
            Assert.Equal(3m, saved.Position); // los llenados quedaron en disco

            runner.SizeMultiplier = 0; // pausada: no manda más
            runner.Stop(flatten: false);
        }
        finally
        {
            try { Directory.Delete(LiveStore.Root, true); } catch { /* limpieza */ }
            LiveStore.Root = previousRoot; // the folder is static: give the next test the one it had
        }
    }

    /// <summary>Runs <paramref name="test"/> with the live store in a new temporary folder, then restores the previous one.</summary>
    private static void InTemporaryStore(Action test)
    {
        string previousRoot = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_live_" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = root;
        try
        {
            test();
        }
        finally
        {
            LiveStore.Root = previousRoot;
            try { Directory.Delete(root, true); } catch { /* cleanup */ }
        }
    }

    private const string LedgerKey = "TradingHub.Tests.Strategy|test:TEST|Cuenta simulada";

    [Fact]
    public void A_corrupt_line_in_the_middle_of_a_ledger_only_skips_that_line()
    {
        InTemporaryStore(() =>
        {
            string file = LiveStore.LedgerFile(LedgerKey);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var lines = new[]
            {
                JsonSerializer.Serialize(new LiveFill(T0, true, 2, 100)),
                "{\"Time\":\"2026-10-01T12:01:00Z\",\"Buy\":fal", // cut off by a crash in the middle of the file
                JsonSerializer.Serialize(new LiveFill(T0.AddMinutes(2), false, 1, 110)),
                JsonSerializer.Serialize(new LiveFill(T0.AddMinutes(3), true, 1, 105)),
            };
            File.WriteAllText(file, string.Join("\n", lines) + "\n");
            byte[] before = File.ReadAllBytes(file);

            var ledger = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(3, ledger.Fills.Count); // the fills after the corrupt line are kept
            Assert.Equal(2m, ledger.Position);
            Assert.Equal(10m, ledger.Realized);
            Assert.Equal(1, ledger.SkippedLines); // and reported
            Assert.Equal(before, File.ReadAllBytes(file)); // loading never rewrites the file
        });
    }

    [Fact]
    public void A_fill_that_cannot_be_written_is_kept_and_written_first_with_the_next_one()
    {
        InTemporaryStore(() =>
        {
            string file = LiveStore.LedgerFile(LedgerKey);
            Directory.CreateDirectory(file); // the ledger path cannot be written as a file
            var first = new LiveFill(T0, true, 1, 100);
            var second = new LiveFill(T0.AddMinutes(1), false, 1, 101);
            var third = new LiveFill(T0.AddMinutes(2), true, 2, 102);
            Assert.NotNull(LiveStore.AppendFill(LedgerKey, first)); // reported, not swallowed
            Assert.Equal(1, LiveStore.PendingFills(LedgerKey));
            Assert.Equal(1m, LiveStore.LoadLedger(LedgerKey, 1).Position); // still part of the ledger in this process

            Directory.Delete(file); // writable again
            Assert.Null(LiveStore.AppendFill(LedgerKey, second));
            Assert.Equal(0, LiveStore.PendingFills(LedgerKey));
            Assert.Null(LiveStore.AppendFill(LedgerKey, third));
            // in order and once each
            Assert.Equal(new[] { first, second, third }.Select(f => JsonSerializer.Serialize(f)), File.ReadAllLines(file));
            Assert.Equal(2m, LiveStore.LoadLedger(LedgerKey, 1).Position);
        });
    }

    [Fact]
    public void A_fill_appended_after_a_cut_off_last_line_is_not_lost()
    {
        InTemporaryStore(() =>
        {
            string file = LiveStore.LedgerFile(LedgerKey);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var first = new LiveFill(T0, true, 1, 100);
            File.WriteAllText(file, JsonSerializer.Serialize(first) + "\n{\"Time\":\"2026-10-01T12:01:00Z\",\"Bu"); // the last write was cut off
            var next = new LiveFill(T0.AddMinutes(2), false, 1, 105);
            Assert.Null(LiveStore.AppendFill(LedgerKey, next));

            var ledger = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(new[] { first, next }, ledger.Fills); // the new fill starts its own line
            Assert.Equal(1, ledger.SkippedLines);
        });
    }

    [Fact]
    public void Deleting_a_ledger_also_drops_its_pending_fills()
    {
        InTemporaryStore(() =>
        {
            string file = LiveStore.LedgerFile(LedgerKey);
            Directory.CreateDirectory(file);
            Assert.NotNull(LiveStore.AppendFill(LedgerKey, new LiveFill(T0, true, 1, 100)));
            LiveStore.DeleteFills(LedgerKey); // starting fresh: the unsaved fill belongs to the deleted history
            Assert.Equal(0, LiveStore.PendingFills(LedgerKey));

            Directory.Delete(file);
            var next = new LiveFill(T0.AddMinutes(1), false, 1, 101);
            Assert.Null(LiveStore.AppendFill(LedgerKey, next));
            Assert.Equal(new[] { JsonSerializer.Serialize(next) }, File.ReadAllLines(file));
        });
    }
}
