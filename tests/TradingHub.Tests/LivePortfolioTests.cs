using System.Text.Json;
using TradingHub.App.Services;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_ledger_persistence_failure_pauses_entries_but_keeps_owned_protection_and_closing_alive(bool restoreBeforeClose)
    {
        InTemporaryStore(() =>
        {
            var inst = new Instrument { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
            var data = new InstrumentData(inst, new SimulatedFeed());
            for (int i = 0; i < 5; i++) data.Trades.Add(new Trade(i + 1, T0.AddSeconds(i * 30), 100, 1, TradeSide.Buy));
            var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());
            using var runner = new LiveStrategyRunner(new BuyTwo { StopLossTicks = 5, TakeProfitTicks = 10 }, data, broker, ChartPeriod.M1);
            runner.Start();
            string file = LiveStore.LedgerFile(runner.Key);
            string committed = file + ".fixture-committed";
            try
            {
                File.Move(file, committed);
                Directory.CreateDirectory(file);
                int start = data.Trades.Count;
                data.Trades.Add(new Trade(6, T0.AddMinutes(3), 100, 1, TradeSide.Buy));
                var appended = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                ((Action<int, int>?)appended!.GetValue(data))?.Invoke(start, 1);
                Assert.Equal(2m, runner.Ledger.Position);
                Assert.Equal(2m, broker.Position!.Volume);
                Assert.True(runner.IsRunning);
                Assert.True(runner.Paused);
                Assert.Equal(2, runner.ActiveOrders.Count(o => o.IsProtective));
                Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
                Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Limit);

                int submitted = broker.Orders.Count;
                var rejected = runner.Submit(OrderSide.Buy, StrategyOrderType.Market, 1, 0, "must remain blocked");
                Assert.Equal(StrategyOrderState.Canceled, rejected.State);
                Assert.Equal(submitted, broker.Orders.Count);
                runner.SetPaused(false);
                Assert.True(runner.Paused);

                if (restoreBeforeClose)
                {
                    Directory.Delete(file);
                    File.Move(committed, file);
                }
                runner.ClosePosition("Close while ledger entries are paused");
                Assert.Equal(0m, runner.Ledger.Position);
                Assert.Equal(0m, broker.Position.Volume);
                Assert.True(runner.IsRunning);
                Assert.True(runner.Paused); // A storage repair does not silently resume new exposure.
                Assert.False(runner.IsClosing);
                Assert.Empty(broker.ActiveOrders);
                runner.SetPaused(false);
                Assert.Equal(!restoreBeforeClose, runner.Paused);
                if (!restoreBeforeClose)
                {
                    Assert.Throws<IOException>(() => LiveStore.LoadLedger(runner.Key, 1));
                    Directory.Delete(file);
                    File.Move(committed, file);
                    var fillsBeforeResume = runner.Ledger.Fills.ToArray();
                    var tradesBeforeResume = runner.Ledger.Trades.ToArray();
                    int ordersBeforeResume = broker.Orders.Count;
                    runner.SetPaused(false); // Explicit resume must save real pending fills, without inventing an execution.
                    Assert.False(runner.Paused);
                    Assert.Equal(fillsBeforeResume, runner.Ledger.Fills);
                    Assert.Equal(tradesBeforeResume, runner.Ledger.Trades);
                    Assert.Equal(ordersBeforeResume, broker.Orders.Count);
                    Assert.Equal(fillsBeforeResume, LiveStore.LoadLedger(runner.Key, 1).Fills);
                    Assert.Equal(0, LiveStore.PendingLedgerEvents(runner.Key));
                }
                var saved = LiveStore.LoadLedger(runner.Key, 1);
                Assert.Equal(new[] { true, false }, saved.Fills.Select(fill => fill.Buy));
                Assert.Equal(new[] { 2m, 2m }, saved.Fills.Select(fill => fill.Quantity));
            }
            finally
            {
                runner.Stop(flatten: false);
                LiveStore.DeleteFills(runner.Key);
            }
        });
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
            LiveStore.DeleteFills(LedgerKey); // also release process-local pending state if an assertion failed
            LiveStore.Root = previousRoot;
            try { Directory.Delete(root, true); } catch { /* cleanup */ }
        }
    }

    private const string LedgerKey = "TradingHub.Tests.Strategy|test:TEST|Cuenta simulada";

    private static LiveFill CorrectionExecution(string context, string execution, string order,
        int minute, bool buy, decimal quantity, decimal price, decimal fee, bool feesComplete = true) =>
        new(T0.AddMinutes(minute), buy, quantity, price, "TEST", 1m)
        {
            Kind = LiveFillKind.Execution,
            OrderId = order,
            ContextId = context,
            ExecutionId = new(new ExecutionScope("ledger-correction-fixture", "sim", "paper", "test:TEST")
            {
                ContextId = context,
            }, execution),
            Currency = "USD",
            Fees = new[] { new ExecutionFee(fee, "USD") },
            FeesComplete = feesComplete,
        };

    [Fact]
    public void Correcting_known_five_to_four_retains_five_observed_units_without_prices()
    {
        InTemporaryStore(() =>
        {
            var ledger = new LiveLedger(LedgerKey, 1m);
            var observation = new LiveFill(T0, true, 10m, 0m, "TEST", 1m)
            {
                Kind = LiveFillKind.QuantityObservation,
                OrderId = "entry",
                CumulativeQuantity = 10m,
                ContextId = "context-A",
            };
            var execution = CorrectionExecution("context-A", "entry-1", "entry", 1, true, 5m, 100m, 1m);
            foreach (var fill in new[] { observation, execution })
            {
                Assert.Null(LiveStore.AppendFill(LedgerKey, fill));
                ledger.Apply(fill);
            }
            Assert.Equal(10m, ledger.Position);
            Assert.True(ledger.HasPendingPrices);

            var correction = execution with { Quantity = 4m, Fees = new[] { new ExecutionFee(0.75m, "USD") } };
            Assert.Null(LiveStore.AppendFill(LedgerKey, correction));
            ledger.Apply(correction);
            var reloaded = LiveStore.LoadLedger(LedgerKey, 1m);
            foreach (var actual in new[] { ledger, reloaded })
            {
                Assert.Equal(9m, actual.Position);
                Assert.Equal(4m, Assert.Single(actual.Fills).Quantity);
                Assert.True(actual.HasPendingPrices);
                Assert.True(actual.HasPendingFinancials);
                Assert.Equal(LiveFinancialQuality.Pending, actual.FinancialQuality);
                Assert.Null(actual.NetRealized);
                Assert.Null(actual.AuthoritativeNet);
                Assert.Null(actual.AuthoritativeGross);
                Assert.Null(actual.KnownOpenPnL(120m));
                Assert.Equal(0m, actual.GrossRealized);
                Assert.Equal(-0.75m, actual.Realized);
                Assert.Empty(actual.Trades);
            }

            // Price exactly the five remaining observed units, without inventing additional exposure.
            var remaining = CorrectionExecution("context-A", "entry-2", "entry", 2, true, 5m, 118m, 1.25m);
            Assert.Null(LiveStore.AppendFill(LedgerKey, remaining));
            ledger.Apply(remaining);
            foreach (var actual in new[] { ledger, LiveStore.LoadLedger(LedgerKey, 1m) })
            {
                Assert.Equal(9m, actual.Position);
                Assert.Equal(110m, actual.AveragePrice);
                Assert.Equal(90m, actual.KnownOpenPnL(120m));
                Assert.False(actual.HasPendingFinancials);
                Assert.Equal(LiveFinancialQuality.Authoritative, actual.FinancialQuality);
                Assert.Equal(-2m, actual.AuthoritativeNet);
                Assert.Equal(0m, actual.AuthoritativeGross);
                Assert.Equal(2, actual.Fills.Count);
            }
        });
    }

    [Theory]
    [InlineData(false, true, 0, 0)]
    [InlineData(false, true, 2, -2)]
    [InlineData(false, false, 0, 0)]
    [InlineData(false, false, 2, -2)]
    [InlineData(true, true, 0, -1)]
    [InlineData(true, true, 2, -3)]
    [InlineData(true, false, 0, -1)]
    [InlineData(true, false, 2, -3)]
    public void Zero_quantity_execution_bust_preserves_real_fees_and_remains_reloadable(
        bool bustExit, bool feesComplete, int remainingFee, int expectedNet)
    {
        InTemporaryStore(() =>
        {
            var ledger = new LiveLedger(LedgerKey, 1m);
            var opening = CorrectionExecution("context-A", "entry", "entry-order", 0, true, 2m, 100m, 1m);
            Assert.Null(LiveStore.AppendFill(LedgerKey, opening));
            ledger.Apply(opening);
            var original = opening;
            if (bustExit)
            {
                original = CorrectionExecution("context-A", "exit", "exit-order", 1, false, 2m, 110m, 3m);
                Assert.Null(LiveStore.AppendFill(LedgerKey, original));
                ledger.Apply(original);
                Assert.Equal(0m, ledger.Position);
                Assert.Equal(16m, ledger.AuthoritativeNet);
            }
            else
            {
                Assert.Equal(2m, ledger.Position);
                Assert.Equal(-1m, ledger.AuthoritativeNet);
            }

            var bust = original with
            {
                Quantity = 0m,
                Fees = new[] { new ExecutionFee(remainingFee, "USD") },
                FeesComplete = feesComplete,
            };
            Assert.Null(LiveStore.AppendFill(LedgerKey, bust));
            // Exercise both consumers even if one throws: a successful append must not poison a fresh load.
            var activeError = Record.Exception(() => ledger.Apply(bust));
            LiveLedger? reloaded = null;
            var reloadError = Record.Exception(() => { reloaded = LiveStore.LoadLedger(LedgerKey, 1m); });
            Assert.Null(activeError);
            Assert.Null(reloadError);
            Assert.NotNull(reloaded);
            foreach (var actual in new[] { ledger, reloaded! })
            {
                Assert.Equal(bustExit ? 2m : 0m, actual.Position);
                Assert.Equal(bustExit ? 100m : 0m, actual.AveragePrice);
                Assert.Equal(bustExit ? 20m : 0m, actual.KnownOpenPnL(110m));
                Assert.Equal(0m, actual.GrossRealized);
                Assert.Equal((decimal)expectedNet, actual.Realized);
                Assert.Empty(actual.Trades);
                Assert.False(actual.HasPendingPrices);
                Assert.Equal(!feesComplete, actual.HasPendingFinancials);
                Assert.Equal(feesComplete ? LiveFinancialQuality.Authoritative : LiveFinancialQuality.Pending,
                    actual.FinancialQuality);
                if (feesComplete)
                {
                    Assert.Equal((decimal)expectedNet, actual.AuthoritativeNet);
                    Assert.Equal((decimal)expectedNet, actual.NetRealized);
                }
                else
                {
                    Assert.Null(actual.AuthoritativeNet);
                    Assert.Null(actual.NetRealized);
                }
                Assert.Equal(0m, actual.AuthoritativeGross);
                var effectiveBust = Assert.Single(actual.Fills, fill => fill.ExecutionId == original.ExecutionId);
                Assert.Equal(0m, effectiveBust.Quantity);
                Assert.Equal((decimal)remainingFee, Assert.Single(effectiveBust.Fees!).Amount);
            }
        });
    }

    [Fact]
    public void Opposing_open_contexts_remain_conflicted_at_aggregate_zero_until_both_legs_are_corrected()
    {
        InTemporaryStore(() =>
        {
            var ledger = new LiveLedger(LedgerKey, 1m);
            var entryA = CorrectionExecution("context-A", "entry-A", "entry-order-A", 0, true, 2m, 100m, 2m);
            var exitA = CorrectionExecution("context-A", "exit-A", "exit-order-A", 1, false, 2m, 110m, 2m);
            var entryB = CorrectionExecution("context-B", "entry-B", "entry-order-B", 2, true, 1m, 200m, 1m);
            foreach (var fill in new[] { entryA, exitA, entryB })
            {
                Assert.Null(LiveStore.AppendFill(LedgerKey, fill));
                ledger.Apply(fill);
            }
            Assert.Equal(1m, ledger.Position);
            Assert.Equal("context-B", ledger.OpenContextId);
            Assert.False(ledger.OpenContextConflict);
            Assert.Equal(15m, ledger.AuthoritativeNet); // A gross twenty minus all five actual fees.
            Assert.Equal(10m, ledger.KnownOpenPnL(210m));

            var correctedEntryA = entryA with { Quantity = 1m, Fees = new[] { new ExecutionFee(1m, "USD") } };
            Assert.Null(LiveStore.AppendFill(LedgerKey, correctedEntryA));
            ledger.Apply(correctedEntryA);
            foreach (var actual in new[] { ledger, LiveStore.LoadLedger(LedgerKey, 1m) })
            {
                // A is short one and B is long one. Their aggregate zero does not settle either owner.
                Assert.Equal(0m, actual.Position);
                Assert.True(actual.OpenContextConflict);
                Assert.Null(actual.OpenContextId);
                Assert.Null(actual.KnownOpenPnL(210m));
                Assert.Null(actual.KnownOpenPnL(0m));
                var stopped = new LiveEntry
                {
                    Key = LedgerKey, Source = "fixture", Ledger = actual,
                    Info = new LiveRunnerInfo { Key = LedgerKey, PointValue = 1m },
                };
                Assert.Null(stopped.OpenPnL);
                Assert.Null(stopped.Net); // No confirmed flat open-value/net from opposing contexts.
            }

            var correctedExitA = exitA with { Quantity = 1m, Fees = new[] { new ExecutionFee(1m, "USD") } };
            Assert.Null(LiveStore.AppendFill(LedgerKey, correctedExitA));
            ledger.Apply(correctedExitA);
            foreach (var actual in new[] { ledger, LiveStore.LoadLedger(LedgerKey, 1m) })
            {
                Assert.Equal(1m, actual.Position);
                Assert.Equal("context-B", actual.OpenContextId);
                Assert.False(actual.OpenContextConflict);
                Assert.Equal(200m, actual.AveragePrice);
                Assert.Equal(10m, actual.KnownOpenPnL(210m));
                Assert.False(actual.HasPendingPrices);
                Assert.False(actual.HasPendingFinancials);
                Assert.Equal(LiveFinancialQuality.Authoritative, actual.FinancialQuality);
                Assert.Equal(10m, actual.AuthoritativeGross);
                Assert.Equal(7m, actual.AuthoritativeNet); // A gross ten minus A fees two and B fee one.
                Assert.Equal(7m, actual.NetRealized);
                var closedA = Assert.Single(actual.Trades);
                Assert.Equal(1m, closedA.Quantity);
                Assert.Equal(100m, closedA.EntryPrice);
                Assert.Equal(110m, closedA.ExitPrice);
                Assert.Equal(10m, closedA.Pnl);
                Assert.Equal(2m, closedA.Commission);
                Assert.Equal(8m, closedA.NetPnl);
                Assert.Equal(3, actual.Fills.Count);
                Assert.Equal(1m, Assert.Single(actual.Fills, fill => fill.ExecutionId == entryA.ExecutionId).Quantity);
                Assert.Equal(1m, Assert.Single(actual.Fills, fill => fill.ExecutionId == exitA.ExecutionId).Quantity);
                Assert.Equal(1m, Assert.Single(actual.Fills, fill => fill.ExecutionId == entryB.ExecutionId).Quantity);
            }
        });
    }

    [Fact]
    public void Correcting_both_legs_of_closed_context_clears_conflict_without_losing_current_owner()
    {
        InTemporaryStore(() =>
        {
            var ledger = new LiveLedger(LedgerKey, 1m);
            var entryA = CorrectionExecution("context-A", "entry-A", "entry-order-A", 0, true, 2m, 100m, 2m);
            var exitA = CorrectionExecution("context-A", "exit-A", "exit-order-A", 1, false, 2m, 110m, 2m);
            var entryB = CorrectionExecution("context-B", "entry-B", "entry-order-B", 2, true, 3m, 200m, 3m);
            foreach (var fill in new[] { entryA, exitA, entryB })
            {
                Assert.Null(LiveStore.AppendFill(LedgerKey, fill));
                ledger.Apply(fill);
            }
            Assert.Equal(3m, ledger.Position);
            Assert.Equal("context-B", ledger.OpenContextId);
            Assert.False(ledger.OpenContextConflict);
            Assert.Equal(13m, ledger.AuthoritativeNet);

            var correctedEntryA = entryA with { Quantity = 1m, Fees = new[] { new ExecutionFee(1m, "USD") } };
            Assert.Null(LiveStore.AppendFill(LedgerKey, correctedEntryA));
            ledger.Apply(correctedEntryA);
            Assert.Equal(2m, ledger.Position);
            Assert.True(ledger.OpenContextConflict);
            var intermediate = LiveStore.LoadLedger(LedgerKey, 1m);
            Assert.Equal(2m, intermediate.Position);
            Assert.True(intermediate.OpenContextConflict);

            var correctedExitA = exitA with { Quantity = 1m, Fees = new[] { new ExecutionFee(1m, "USD") } };
            Assert.Null(LiveStore.AppendFill(LedgerKey, correctedExitA));
            ledger.Apply(correctedExitA);
            foreach (var actual in new[] { ledger, LiveStore.LoadLedger(LedgerKey, 1m) })
            {
                Assert.Equal(3m, actual.Position);
                Assert.Equal("context-B", actual.OpenContextId);
                Assert.False(actual.OpenContextConflict);
                Assert.Equal("TEST", actual.OpenSymbol);
                Assert.Equal(1m, actual.OpenPointValue);
                Assert.Equal(200m, actual.AveragePrice);
                Assert.Equal(30m, actual.KnownOpenPnL(210m));
                Assert.False(actual.HasPendingFinancials);
                Assert.Equal(LiveFinancialQuality.Authoritative, actual.FinancialQuality);
                Assert.Equal(10m, actual.AuthoritativeGross);
                Assert.Equal(5m, actual.AuthoritativeNet);
                Assert.Equal(5m, actual.NetRealized);
                var closedA = Assert.Single(actual.Trades);
                Assert.Equal(1m, closedA.Quantity);
                Assert.Equal(100m, closedA.EntryPrice);
                Assert.Equal(110m, closedA.ExitPrice);
                Assert.Equal(10m, closedA.Pnl);
                Assert.Equal(2m, closedA.Commission);
                Assert.Equal(8m, closedA.NetPnl);
                Assert.Equal(3, actual.Fills.Count);
            }
            Assert.Equal(0, LiveStore.PendingLedgerEvents(LedgerKey));
        });
    }

    [Fact]
    public void Saving_existing_pending_fills_does_not_invent_a_fill_or_create_a_missing_history()
    {
        InTemporaryStore(() =>
        {
            Assert.Null(LiveStore.SavePendingLedgerEvents(LedgerKey));
            Assert.False(File.Exists(LiveStore.LedgerFile(LedgerKey)));
            Assert.Empty(LiveStore.LoadLedger(LedgerKey, 1).Fills);
            Assert.Equal(0, LiveStore.PendingLedgerEvents(LedgerKey));
        });
    }

    [Fact]
    public void Explicit_pending_save_recovers_a_fill_observed_before_the_store_could_be_opened()
    {
        InTemporaryStore(() =>
        {
            var fill = new LiveFill(T0, true, 1, 100);
            File.WriteAllText(LiveStore.Root, "artificial root I/O fault");
            try { Assert.NotNull(LiveStore.AppendFill(LedgerKey, fill)); }
            finally { File.Delete(LiveStore.Root); }
            Assert.Null(LiveStore.SavePendingLedgerEvents(LedgerKey));
            Assert.Equal(new[] { fill }, LiveStore.LoadLedger(LedgerKey, 1).Fills);
            Assert.Equal(0, LiveStore.PendingLedgerEvents(LedgerKey));
            Assert.Null(LiveStore.SavePendingLedgerEvents(LedgerKey));
            Assert.Equal(new[] { fill }, LiveStore.LoadLedger(LedgerKey, 1).Fills);
        });
    }

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
            Assert.Equal(1, LiveStore.PendingLedgerEvents(LedgerKey));
            Assert.Equal(1m, LiveStore.LoadLedger(LedgerKey, 1).Position); // still part of the ledger in this process

            Directory.Delete(file); // writable again
            Assert.Null(LiveStore.AppendFill(LedgerKey, second));
            Assert.Equal(0, LiveStore.PendingLedgerEvents(LedgerKey));
            Assert.Null(LiveStore.AppendFill(LedgerKey, third));
            // in order and once each
            Assert.Equal(new[] { first, second, third }.Select(f => JsonSerializer.Serialize(f)), File.ReadAllLines(file));
            Assert.Equal(2m, LiveStore.LoadLedger(LedgerKey, 1).Position);
        });
    }

    // Inject only the varying I/O adapter; observations still use the same append/load consumer seam.
    private static string? AppendWithStream(string key, LiveFill fill, Func<string, Stream> openLedger) =>
        LiveStore.AppendFill(key, fill, path => path.EndsWith(".jsonl", StringComparison.Ordinal)
            ? openLedger(path)
            : new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0));

    public enum LedgerFailure { BeforeWrite, PartialWrite, Rollback, Flush, Dispose }

    private sealed class FaultingLedgerStream(string path, LedgerFailure failure, bool failRollback = false)
        : Stream
    {
        private readonly FileStream _file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);

        public override bool CanRead => _file.CanRead;
        public override bool CanSeek => _file.CanSeek;
        public override bool CanWrite => _file.CanWrite;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set => _file.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _file.Read(buffer);
        public override int ReadByte() => _file.ReadByte();
        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (failure == LedgerFailure.BeforeWrite) throw new IOException("Injected write failure");
            if (failure == LedgerFailure.PartialWrite)
            {
                _file.Write(buffer[..Math.Min(buffer.Length, 7)]);
                throw new IOException("Injected partial write failure");
            }

            _file.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void SetLength(long value)
        {
            if (failRollback || failure == LedgerFailure.Rollback) throw new IOException("Injected truncation failure");
            _file.SetLength(value);
        }

        public override void Flush()
        {
            if (failure == LedgerFailure.Flush) throw new IOException("Injected flush failure");
            _file.Flush(flushToDisk: true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
            if (disposing && failure == LedgerFailure.Dispose) throw new IOException("Injected dispose failure");
        }
    }

    [Fact]
    public void A_partial_batch_with_failed_rollback_is_excluded_until_retried_in_order()
    {
        InTemporaryStore(() =>
        {
            var opening = new LiveFill(T0, true, 2, 100);
            var first = new LiveFill(T0.AddMinutes(1), false, 1, 110);
            var second = new LiveFill(T0.AddMinutes(2), false, 2, 90);
            var third = new LiveFill(T0.AddMinutes(3), true, 1, 80);
            var fourth = new LiveFill(T0.AddMinutes(4), true, 1, 70);
            Assert.Null(LiveStore.AppendFill(LedgerKey, opening));
            string file = LiveStore.LedgerFile(LedgerKey);
            File.AppendAllText(file, "{corrupt committed line}\n");
            Assert.NotNull(AppendWithStream(LedgerKey, first,
                path => new FaultingLedgerStream(path, LedgerFailure.BeforeWrite)));
            Assert.NotNull(AppendWithStream(LedgerKey, second,
                path => new FaultingLedgerStream(path, LedgerFailure.PartialWrite, failRollback: true)));

            var recovered = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(new[] { opening, first, second }, recovered.Fills);
            Assert.Equal(-1m, recovered.Position);
            Assert.Equal(90m, recovered.AveragePrice);
            Assert.Equal(0m, recovered.Realized);
            Assert.Equal(10m, recovered.OpenPnL(80));
            Assert.Equal(1, recovered.SkippedLines);

            byte[] damaged = File.ReadAllBytes(file);
            Assert.NotNull(AppendWithStream(LedgerKey, third,
                path => new FaultingLedgerStream(path, LedgerFailure.Rollback)));
            Assert.Equal(damaged, File.ReadAllBytes(file)); // no replay may precede a successful truncation
            var waiting = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(new[] { opening, first, second, third }, waiting.Fills);
            Assert.Equal(0m, waiting.Position);
            Assert.Equal(10m, waiting.Realized);
            Assert.Equal(1, waiting.SkippedLines);

            Assert.Null(LiveStore.AppendFill(LedgerKey, fourth));
            var saved = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(new[] { opening, first, second, third, fourth }, saved.Fills);
            Assert.Equal(1m, saved.Position);
            Assert.Equal(70m, saved.AveragePrice);
            Assert.Equal(10m, saved.Realized);
            Assert.Equal(5m, saved.OpenPnL(75));
            Assert.Equal(1, saved.SkippedLines);
            Assert.Equal(0, LiveStore.PendingLedgerEvents(LedgerKey));
            Assert.Equal(new[]
            {
                JsonSerializer.Serialize(opening), "{corrupt committed line}",
                JsonSerializer.Serialize(first), JsonSerializer.Serialize(second),
                JsonSerializer.Serialize(third), JsonSerializer.Serialize(fourth),
            }, File.ReadAllLines(file));
        });
    }

    [Theory]
    [InlineData(LedgerFailure.Flush)]
    [InlineData(LedgerFailure.Dispose)]
    public void A_batch_is_not_committed_until_flush_and_dispose_succeed(LedgerFailure failure)
    {
        InTemporaryStore(() =>
        {
            var opening = new LiveFill(T0, true, 2, 100);
            var closing = new LiveFill(T0.AddMinutes(1), false, 1, 110);
            var next = new LiveFill(T0.AddMinutes(2), false, 1, 105);
            Assert.Null(LiveStore.AppendFill(LedgerKey, opening));
            Assert.NotNull(AppendWithStream(LedgerKey, closing,
                path => new FaultingLedgerStream(path, failure, failRollback: true)));
            var recovered = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(new[] { opening, closing }, recovered.Fills);
            Assert.Equal(1m, recovered.Position);
            Assert.Equal(10m, recovered.Realized);

            Assert.Null(LiveStore.AppendFill(LedgerKey, next));
            var saved = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(new[] { opening, closing, next }, saved.Fills);
            Assert.Equal(0m, saved.Position);
            Assert.Equal(15m, saved.Realized);
            Assert.Equal(new[] { opening, closing, next }.Select(f => JsonSerializer.Serialize(f)), File.ReadAllLines(LiveStore.LedgerFile(LedgerKey)));
        });
    }

    [Theory]
    [InlineData(LedgerFailure.BeforeWrite)]
    [InlineData(LedgerFailure.PartialWrite)]
    [InlineData(LedgerFailure.Flush)]
    [InlineData(LedgerFailure.Dispose)]
    public void An_intent_that_cannot_be_published_does_not_modify_history_or_drop_local_pending_fills(LedgerFailure failure)
    {
        InTemporaryStore(() =>
        {
            var opening = new LiveFill(T0, true, 2, 100);
            var closing = new LiveFill(T0.AddMinutes(1), false, 1, 110);
            var next = new LiveFill(T0.AddMinutes(2), false, 1, 105);
            Assert.Null(LiveStore.AppendFill(LedgerKey, opening));
            string file = LiveStore.LedgerFile(LedgerKey);
            byte[] committed = File.ReadAllBytes(file);
            Assert.NotNull(LiveStore.AppendFill(LedgerKey, closing, path =>
                path.EndsWith(".wal.tmp", StringComparison.Ordinal)
                    ? new FaultingLedgerStream(path, failure)
                    : new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0)));
            Assert.Equal(committed, File.ReadAllBytes(file));
            Assert.Equal(1, LiveStore.PendingLedgerEvents(LedgerKey));
            var waiting = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(new[] { opening, closing }, waiting.Fills);
            Assert.Equal(1m, waiting.Position);
            Assert.Equal(10m, waiting.Realized);
            Assert.Null(LiveStore.AppendFill(LedgerKey, next));
            var saved = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(new[] { opening, closing, next }, saved.Fills);
            Assert.Equal(0m, saved.Position);
            Assert.Equal(15m, saved.Realized);
            Assert.Equal(0, LiveStore.PendingLedgerEvents(LedgerKey));
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
            Directory.Delete(file); // Remove the artificial I/O fault before querying the now-deleted history.
            Assert.Equal(0, LiveStore.PendingLedgerEvents(LedgerKey));
            Assert.Empty(LiveStore.LoadLedger(LedgerKey, 1).Fills);
            var next = new LiveFill(T0.AddMinutes(1), false, 1, 101);
            Assert.Null(LiveStore.AppendFill(LedgerKey, next));
            Assert.Equal(new[] { JsonSerializer.Serialize(next) }, File.ReadAllLines(file));
        });
    }

    [Fact]
    public void Identical_legitimate_fills_are_not_deduplicated_by_content_or_timestamp()
    {
        InTemporaryStore(() =>
        {
            var fill = new LiveFill(T0, true, 1, 100);
            Assert.Null(LiveStore.AppendFill(LedgerKey, fill));
            Assert.Null(LiveStore.AppendFill(LedgerKey, fill));
            var ledger = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(new[] { fill, fill }, ledger.Fills);
            Assert.Equal(2m, ledger.Position);
        });
    }

    [Fact]
    public void Unreadable_history_is_not_reported_as_a_flat_position()
    {
        InTemporaryStore(() =>
        {
            Directory.CreateDirectory(LiveStore.LedgerFile(LedgerKey));
            var error = Assert.Throws<IOException>(() => LiveStore.LoadLedger(LedgerKey, 1));
            Assert.Contains("Keep the strategy stopped", error.Message);
        });
    }

    [Fact]
    public void A_disappeared_acknowledged_history_is_not_an_ordinary_missing_ledger()
    {
        InTemporaryStore(() =>
        {
            Assert.Null(LiveStore.AppendFill(LedgerKey, new LiveFill(T0, true, 1, 100)));
            string file = LiveStore.LedgerFile(LedgerKey);
            File.Delete(file);
            Assert.Throws<IOException>(() => LiveStore.PendingLedgerEvents(LedgerKey));
            Assert.Throws<IOException>(() => LiveStore.LoadLedger(LedgerKey, 1));
            Assert.NotNull(LiveStore.AppendFill(LedgerKey, new LiveFill(T0.AddMinutes(1), false, 1, 110)));
            Assert.False(File.Exists(file));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_or_mismatched_committed_range_blocks_recovery_and_preserves_the_damage(bool mismatch)
    {
        InTemporaryStore(() =>
        {
            var opening = new LiveFill(T0, true, 2, 100);
            Assert.Null(LiveStore.AppendFill(LedgerKey, opening));
            Assert.NotNull(AppendWithStream(LedgerKey, new LiveFill(T0.AddMinutes(1), false, 1, 110),
                path => new FaultingLedgerStream(path, LedgerFailure.Dispose, failRollback: true)));
            string file = LiveStore.LedgerFile(LedgerKey);
            if (mismatch)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite);
                stream.Position = stream.Length - 2;
                stream.WriteByte((byte)'x');
            }
            else File.WriteAllText(file, "");
            byte[] damaged = File.ReadAllBytes(file);
            Assert.Throws<IOException>(() => LiveStore.LoadLedger(LedgerKey, 1));
            Assert.NotNull(LiveStore.AppendFill(LedgerKey, new LiveFill(T0.AddMinutes(2), true, 1, 90)));
            Assert.Equal(damaged, File.ReadAllBytes(file));
        });
    }

    [Fact]
    public void Appending_to_a_large_legacy_ledger_does_not_copy_its_history()
    {
        InTemporaryStore(() =>
        {
            var fill = new LiveFill(T0, true, 1, 100);
            Assert.Null(LiveStore.AppendFill(LedgerKey, fill)); // Warm the serializer and persistence path.
            string file = LiveStore.LedgerFile(LedgerKey);
            string line = JsonSerializer.Serialize(fill);
            using (var writer = new StreamWriter(file, append: true))
                for (int i = 0; i < 32_768; i++) writer.WriteLine(line);
            long before = GC.GetAllocatedBytesForCurrentThread();
            string? error = LiveStore.AppendFill(LedgerKey, new LiveFill(T0.AddMinutes(1), false, 1, 101));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Null(error);
            Assert.True(allocated < 512 * 1024, $"One append allocated {allocated:N0} bytes for an existing multi-megabyte ledger.");
            var ledger = LiveStore.LoadLedger(LedgerKey, 1);
            Assert.Equal(32_770, ledger.Fills.Count);
            Assert.Equal(32_768m, ledger.Position);
            Assert.Equal(1m, ledger.Realized);
        });
    }

    [Fact]
    public void Saved_live_state_without_protection_ids_still_loads_and_is_saved_without_them()
    {
        InTemporaryStore(() =>
        {
            Directory.CreateDirectory(LiveStore.Root);
            string file = Path.Combine(LiveStore.Root, "estrategias.json");
            // an entry as written before ProtectionOrderIds existed
            File.WriteAllText(file, """
                [
                  {
                    "Key": "TradingHub.Tests.Strategy|test:TEST|Cuenta simulada",
                    "Strategy": "Strategy",
                    "StrategyId": "TradingHub.Tests.Strategy",
                    "FeedId": "test",
                    "Symbol": "TEST",
                    "Period": "M1",
                    "Account": "Cuenta simulada",
                    "Running": true,
                    "StartedUtc": "2026-10-01T12:00:00Z",
                    "Size": 1,
                    "PointValue": 1,
                    "ExecSymbol": null,
                    "Position": 1,
                    "OpenPnL": 0,
                    "Realized": 0,
                    "Trades": 0,
                    "Source": "panel",
                    "AccountId": "sim",
                    "ProfileId": "sim",
                    "Parameters": { "StopLossTicks": 5 },
                    "TimeZoneId": null,
                    "SessionStartHours": 0,
                    "MaxDailyLoss": 0,
                    "MaxDrawdown": 0,
                    "CloseOnLimit": true,
                    "PortfolioId": null,
                    "StopReason": null,
                    "Paused": false,
                    "IsReal": false
                  }
                ]
                """);
            var info = Assert.Single(LiveStore.LoadRunners());
            Assert.Equal(LedgerKey, info.Key);
            Assert.True(info.Running);
            Assert.Equal(1m, info.Position);
            Assert.Null(info.ProtectionOrderIds);

            LiveStore.SaveRunners(new[] { info });
            Assert.DoesNotContain("ProtectionOrderIds", File.ReadAllText(file)); // nothing new is written while there is none
            info.ProtectionOrderIds = new List<string> { "basket-7" };
            LiveStore.SaveRunners(new[] { info });
            Assert.Equal(new[] { "basket-7" }, Assert.Single(LiveStore.LoadRunners()).ProtectionOrderIds);
        });
    }
}
