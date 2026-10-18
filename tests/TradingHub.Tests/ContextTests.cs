using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>Contexto del mercado, filtro de contexto de las estrategias y sus dibujos en el gráfico.</summary>
public class ContextTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Una operación por minuto: un día y medio subiendo derecho y después un día y medio de vaivén.</summary>
    private static TickData TrendThenRange()
    {
        var list = new List<Trade>();
        decimal price = 1000;
        for (int i = 0; i < 72 * 60; i++)
        {
            if (i < 36 * 60) price += 1;
            else price = 3160 + (i / 60 % 2 == 0 ? 40 : -40) + i % 7;
            list.Add(new Trade(i + 1, T0.AddMinutes(i), price, 1, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        return TickData.FromTrades(Inst, list);
    }

    private static BacktestSettings Settings(int days = 4) => new()
    {
        FeedId = "test", Symbol = "TEST", FromUtc = T0, ToUtc = T0.AddDays(days), Period = "H1", CommissionPercent = 0, SlippageTicks = 0, InitialCapital = 100_000,
    };

    /// <summary>Entra largo cada vez que está plana y sale a las dos velas.</summary>
    private sealed class AlwaysIn : Strategy
    {
        private int _entry = -10;

        protected override void OnBar(Bar bar)
        {
            if (IsFlat && ActiveOrders.Count == 0)
            {
                Buy(1);
                _entry = CurrentBar;
            }
            else if (IsLong && CurrentBar - _entry >= 2) ClosePosition();
        }
    }

    [Fact]
    public void El_filtro_de_contexto_solo_deja_entrar_en_ese_regimen()
    {
        var free = new BacktestEngine(new AlwaysIn(), TrendThenRange(), Settings()).Run();
        Assert.Contains(free.Trades, t => t.EntryTrend >= Robustness.TrendingTrend);

        var onlyRange = new AlwaysIn { RegimeFilter = RegimeFilter.RangeOnly };
        var r = new BacktestEngine(onlyRange, TrendThenRange(), Settings()).Run();
        var judged = r.Trades.Where(t => t.EntryTrend != null).ToList();
        Assert.NotEmpty(judged);
        Assert.All(judged, t => Assert.True(t.EntryTrend <= Robustness.RangeTrend, $"entró con tendencia {t.EntryTrend}"));
        Assert.True(r.Trades.Count < free.Trades.Count);
        // las salidas siempre pasan: nada queda abierto de más
        Assert.True(r.OpenPosition == null || r.OpenPosition.Quantity == 1);
    }

    [Fact]
    public void Cada_operacion_guarda_el_contexto_y_el_percentil_de_volatilidad()
    {
        var r = new BacktestEngine(new AlwaysIn(), TrendThenRange(), Settings()).Run();
        var late = r.Trades.Where(t => t.EntryTime > T0.AddDays(2)).ToList();
        Assert.NotEmpty(late);
        Assert.All(late, t =>
        {
            Assert.NotNull(t.EntryTrend);
            Assert.NotNull(t.EntryVolatility);
            Assert.NotNull(t.EntryVolatilityRank);
            Assert.InRange(t.EntryVolatilityRank!.Value, 0, 100);
        });
    }

    /// <summary>Dibuja el rango de cada día, una línea vela a vela y una flecha en cada entrada.</summary>
    private sealed class Drawer : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            var day = bar.Time.Date;
            DrawBox($"rango-{day:yyyyMMdd}", day, bar.High + 5, bar.Time, bar.Low - 5, "verde", 20, "range");
            Plot("Cierre", bar.Close, "#FF8800");
            if (CurrentBar % 10 == 0) DrawArrowUp($"flecha-{CurrentBar}", bar.Time, bar.Low, "azul", "entrada");
        }
    }

    [Fact]
    public void Las_estrategias_dibujan_rangos_lineas_y_flechas()
    {
        var s = new Drawer();
        var r = new BacktestEngine(s, TrendThenRange(), Settings()).Run();
        var drawings = s.Canvas.Drawings();
        var boxes = drawings.Where(d => d.Kind == DrawingKind.Box).ToList();
        Assert.Equal(r.Bars > 0 ? drawings.Count(d => d.Id.StartsWith("rango-", StringComparison.Ordinal)) : 0, boxes.Count);
        Assert.InRange(boxes.Count, 3, 4); // un rango por día: el mismo id se estira, no se repite
        Assert.Contains(drawings, d => d.Kind == DrawingKind.Marker && d.Shape == MarkerShape.ArrowUp && d.Text == "entrada");
        var plot = Assert.Single(s.Canvas.Plots());
        Assert.Equal("Cierre", plot.Name);
        Assert.Equal(r.Bars, plot.Points.Count);
    }

    [Fact]
    public void El_lienzo_tiene_un_tope_y_se_van_los_mas_viejos()
    {
        var c = new StrategyCanvas();
        for (int i = 0; i < StrategyCanvas.MaxDrawings + 50; i++) c.Set(new StrategyDrawing { Id = $"d{i}", Kind = DrawingKind.Text, Text = "x" });
        var list = c.Drawings();
        Assert.Equal(StrategyCanvas.MaxDrawings, list.Count);
        Assert.Equal("d50", list[0].Id);
    }

    [Fact]
    public void Si_gana_solo_en_rango_con_volatilidad_baja_sugiere_ese_filtro()
    {
        var trades = Enumerable.Range(0, 90).Select(i =>
        {
            bool range = i % 3 != 0, calm = i % 2 == 0;
            decimal pnl = range && calm ? (i % 5 == 0 ? -60 : 150) : range ? (i % 4 == 0 ? 90 : -70) : -120;
            return new ClosedTrade(i + 1, true, T0.AddHours(i), 100, T0.AddHours(i + 0.5), 100 + pnl, 1, pnl, 0, 0, 0, "", "", 1)
            {
                EntryTrend = range ? 0.08 : 0.5,
                EntryVolatility = calm ? 0.2 : 0.8,
                EntryVolatilityRank = calm ? 20 : 80,
            };
        }).ToList();
        var f = Robustness.SuggestContextFilter(trades);
        Assert.NotNull(f);
        Assert.True(f!.Regime is RegimeFilter.RangeOnly or RegimeFilter.NoTrend, $"régimen {f.Regime}");
        Assert.True(f.MaxVolatilityRank <= 66, $"volatilidad hasta {f.MaxVolatilityRank}");
        Assert.True(f.ProfitFactor >= 1.5m && f.DroppedNet < 0 && f.Trades >= 20);
        Assert.Contains("range", f.Description);

        // si gana parejo en todos los contextos, no hay nada que filtrar
        var even = trades.Select((t, i) => t with { Pnl = i % 3 == 0 ? -50 : 100 }).ToList();
        Assert.Null(Robustness.SuggestContextFilter(even));
    }
    /// <summary>Cierres por hora: un día y medio subiendo, uno y medio bajando y uno y medio de vaivén.</summary>
    private static TickData UpDownRange()
    {
        var list = new List<Trade>();
        decimal price = 2000;
        for (int i = 0; i < 108 * 60; i++)
        {
            if (i < 36 * 60) price += 1;
            else if (i < 72 * 60) price -= 1;
            else price = 2000 + (i / 60 % 2 == 0 ? 40 : -40) + i % 7;
            list.Add(new Trade(i + 1, T0.AddMinutes(i), price, 1, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        return TickData.FromTrades(Inst, list);
    }

    [Fact]
    public void El_contexto_dice_si_la_tendencia_es_alcista_o_bajista()
    {
        var up = new MarketContextTracker();
        var down = new MarketContextTracker();
        for (int h = 0; h < 30; h++)
        {
            up.Add(T0.AddHours(h), 100 + h);
            down.Add(T0.AddHours(h), 200 - h);
        }

        Assert.Equal(MarketRegime.Trend, up.Regime);
        Assert.Equal(1, up.Direction);
        Assert.True(up.Change > 0);
        Assert.Equal("Uptrend", up.RegimeLabel);
        Assert.Equal(-1, down.Direction);
        Assert.Equal("Downtrend", down.RegimeLabel);
        Assert.StartsWith("downtrend", down.Describe());
        Assert.True(up.Allows(RegimeFilter.UpTrendOnly, 0, 100) && !up.Allows(RegimeFilter.DownTrendOnly, 0, 100));
        Assert.True(down.Allows(RegimeFilter.DownTrendOnly, 0, 100) && !down.Allows(RegimeFilter.UpTrendOnly, 0, 100));
        Assert.False(down.Allows(RegimeFilter.RangeOnly, 0, 100));
        Assert.Equal("low", MarketContextTracker.VolatilityLabel(20));
        Assert.Equal("high", MarketContextTracker.VolatilityLabel(80));
    }

    [Fact]
    public void Solo_en_tendencia_alcista_entra_solo_cuando_venia_subiendo()
    {
        var free = new BacktestEngine(new AlwaysIn(), UpDownRange(), Settings(5)).Run();
        Assert.Contains(free.Trades, t => t.EntryMarketDirection < 0 && t.EntryTrend >= Robustness.TrendingTrend);

        var r = new BacktestEngine(new AlwaysIn { RegimeFilter = RegimeFilter.UpTrendOnly }, UpDownRange(), Settings(5)).Run();
        var judged = r.Trades.Where(t => t.EntryTrend != null).ToList();
        Assert.NotEmpty(judged);
        Assert.All(judged, t =>
        {
            Assert.True(t.EntryTrend >= Robustness.TrendingTrend, $"entró con tendencia {t.EntryTrend}");
            Assert.Equal(1, t.EntryMarketDirection);
        });

        var down = new BacktestEngine(new AlwaysIn { RegimeFilter = RegimeFilter.DownTrendOnly }, UpDownRange(), Settings(5)).Run();
        Assert.All(down.Trades.Where(t => t.EntryTrend != null), t => Assert.Equal(-1, t.EntryMarketDirection));
    }

    [Fact]
    public void La_historia_por_hora_arma_el_mismo_contexto_que_las_velas()
    {
        // con velas de menos de una hora: sumar la historia es lo mismo que haberla visto hora a hora
        var hourly = Enumerable.Range(0, 40 * 24).Select(i => (T0.AddHours(i), 1000m + (decimal)Math.Sin(i / 5.0) * 30 + i * 0.2m)).ToList();
        var seeded = new MarketContextTracker();
        seeded.AddHistory(hourly, TimeSpan.FromMinutes(15));
        var direct = new MarketContextTracker();
        foreach (var (t, c) in hourly) direct.Add(t, c);
        Assert.Equal(direct.Trend, seeded.Trend);
        Assert.Equal(direct.VolatilityRank, seeded.VolatilityRank);

        // con velas de 4 horas se agrupa: el último cierre de cada tramo de 4 horas
        var coarse = new MarketContextTracker(coarse: true);
        coarse.AddHistory(hourly, TimeSpan.FromHours(4));
        var fours = new MarketContextTracker(coarse: true);
        foreach (var g in hourly.GroupBy(h => h.Item1.Ticks / TimeSpan.FromHours(4).Ticks)) fours.Add(g.First().Item1, g.Last().Item2);
        Assert.Equal(fours.Trend, coarse.Trend);
        Assert.NotNull(coarse.Trend);
    }

    [Fact]
    public void Si_gana_solo_en_tendencia_alcista_sugiere_ese_filtro_y_la_tabla_la_separa()
    {
        var trades = Enumerable.Range(0, 90).Select(i =>
        {
            int kind = i % 3; // 0 = tendencia alcista, 1 = tendencia bajista, 2 = rango
            decimal pnl = kind == 0 ? (i % 5 == 0 ? -60 : 160) : kind == 1 ? (i % 4 == 0 ? 50 : -110) : (i % 4 == 0 ? 60 : -90);
            return new ClosedTrade(i + 1, true, T0.AddHours(i), 100, T0.AddHours(i + 0.5), 100 + pnl, 1, pnl, 0, 0, 0, "", "", 1)
            {
                EntryTrend = kind == 2 ? 0.08 : 0.5,
                EntryVolatility = 0.4,
                EntryMarketDirection = kind == 1 ? -1 : 1,
            };
        }).ToList();
        var f = Robustness.SuggestContextFilter(trades);
        Assert.NotNull(f);
        Assert.Equal(RegimeFilter.UpTrendOnly, f!.Regime);
        Assert.Contains("uptrend", f.Description);

        var regimes = Robustness.Regimes(trades);
        var up = regimes.Single(r => r.Name == "uptrend");
        Assert.True(up.Detail && up.Trades == 30 && up.Net > 0);
        Assert.True(regimes.Single(r => r.Name == "downtrend").Net < 0);

        // lo que deja y lo que saca el filtro en otro mercado
        var (kept, keptNet, dropped, droppedNet) = Robustness.ApplyFilter(trades, RegimeFilter.UpTrendOnly, 0, 100);
        Assert.Equal(30, kept);
        Assert.Equal(60, dropped);
        Assert.True(keptNet > 0 && droppedNet < 0);
    }
    [Fact]
    public void Se_puede_elegir_cualquier_combinacion_de_condiciones()
    {
        // las 15 combinaciones de rango, mixto, tendencia alcista y bajista tienen su filtro, y vuelven a la misma
        for (int bits = 1; bits <= 15; bits++)
        {
            var set = (RegimeSet)bits;
            var f = MarketContextTracker.FilterOf(set);
            Assert.Equal(set, MarketContextTracker.SetOf(f));
        }

        Assert.Equal(RegimeFilter.All, MarketContextTracker.FilterOf(RegimeSet.All));
        Assert.True(MarketContextTracker.RegimeAllows(RegimeFilter.RangeOrUpTrend, MarketRegime.Range, 0));
        Assert.True(MarketContextTracker.RegimeAllows(RegimeFilter.RangeOrUpTrend, MarketRegime.Trend, 1));
        Assert.False(MarketContextTracker.RegimeAllows(RegimeFilter.RangeOrUpTrend, MarketRegime.Trend, -1));
        Assert.False(MarketContextTracker.RegimeAllows(RegimeFilter.RangeOrUpTrend, MarketRegime.Mixed, 1));
        Assert.True(MarketContextTracker.RegimeAllows(RegimeFilter.MixedOnly, MarketRegime.Unknown, null)); // sin datos no bloquea
        Assert.Equal("range or uptrend", Robustness.DescribeRegimes(RegimeSet.Range | RegimeSet.UpTrend));
        Assert.Equal("all except downtrend", Robustness.DescribeRegimes(RegimeSet.Range | RegimeSet.Mixed | RegimeSet.UpTrend));
        Assert.Equal("only in mixed", Robustness.DescribeRegimes(RegimeSet.Mixed));
        Assert.Equal("only in range, low volatility", Robustness.DescribeFilter(RegimeFilter.RangeOnly, 0, 33));
    }

    [Fact]
    public void Con_la_historia_previa_el_filtro_vale_desde_la_primera_vela()
    {
        // arranca en plena tendencia (un día y medio subiendo): sin historia, las primeras horas no sabe que es tendencia y
        // «solo en rango» la deja entrar; con el mes anterior (también subiendo), ya lo sabe desde la primera vela
        var data = TrendThenRange();
        var free = new BacktestEngine(new AlwaysIn { RegimeFilter = RegimeFilter.RangeOnly }, data, Settings()).Run();
        Assert.Contains(free.Trades, t => t.EntryTime < T0.AddHours(8));

        var before = Enumerable.Range(1, 30 * 24).Select(h => (T0.AddHours(-h), 1000m - h * 0.5m)).Reverse().ToList();
        var seeded = new BacktestEngine(new AlwaysIn { RegimeFilter = RegimeFilter.RangeOnly }, data, Settings()) { ContextHistory = before }.Run();
        Assert.DoesNotContain(seeded.Trades, t => t.EntryTime < T0.AddHours(30));
        Assert.All(seeded.Trades, t => Assert.True(t.EntryTrend <= Robustness.RangeTrend, $"entró con tendencia {t.EntryTrend}"));
        // y cada operación ya trae su contexto (no hay «sin datos» al principio)
        var all = new BacktestEngine(new AlwaysIn(), data, Settings()) { ContextHistory = before }.Run();
        Assert.All(all.Trades, t => Assert.NotNull(t.EntryTrend));
    }

    [Fact]
    public void Cada_ventana_del_walk_forward_arranca_sabiendo_como_venia_el_mercado()
    {
        var data = UpDownRange();
        var settings = Settings(5);
        var wf = Optimizer.WalkForward(() => new AlwaysIn(), new Dictionary<string, object?>(), new List<ParameterRange>(), data, settings,
            FitnessMetric.NetProfit, 1, 1, false, 0, null, CancellationToken.None);
        // las ventanas fuera de muestra empiezan en el día 1, 2 y 3: con lo anterior, su primera operación ya sabe si hay
        // rango o tendencia (antes, cada ventana arrancaba sin contexto)
        var firstOfDay = wf.OutSampleTrades.GroupBy(t => t.EntryTime.Date).Select(g => g.OrderBy(t => t.EntryTime).First()).ToList();
        Assert.True(firstOfDay.Count >= 2, $"ventanas con operaciones: {firstOfDay.Count}");
        Assert.All(firstOfDay, t => Assert.NotNull(t.EntryTrend));
    }
}
