using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public class StrategyTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Una operación por segundo con los precios dados.</summary>
    private static TickData Ticks(params decimal[] prices)
    {
        var list = new List<Trade>();
        for (int i = 0; i < prices.Length; i++) list.Add(new Trade(i + 1, T0.AddSeconds(i * 20), prices[i], 1, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        return TickData.FromTrades(Inst, list);
    }

    private static BacktestSettings Settings() => new()
    {
        FeedId = "test",
        Symbol = "TEST",
        FromUtc = T0,
        ToUtc = T0.AddDays(1),
        Period = "M1",
        CommissionPercent = 0,
        SlippageTicks = 0,
        InitialCapital = 1000,
    };

    /// <summary>Compra en la primera vela cerrada y vende en la tercera.</summary>
    private sealed class BuyThenSell : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            if (CurrentBar == 0) Buy(2);
            if (CurrentBar == 2 && IsLong) ClosePosition();
        }
    }

    [Fact]
    public void Orden_a_mercado_se_llena_con_la_operacion_siguiente_al_cierre_de_la_vela()
    {
        // 3 operaciones por vela de 1 minuto: 100,101,102 | 103,104,105 | 106,107,108 | 109
        var data = Ticks(100, 101, 102, 103, 104, 105, 106, 107, 108, 109);
        var r = new BacktestEngine(new BuyThenSell(), data, Settings()).Run();
        Assert.Null(r.Error);
        var t = Assert.Single(r.Trades);
        Assert.True(t.IsLong);
        Assert.Equal(103m, t.EntryPrice); // primera operación de la vela 2
        Assert.Equal(109m, t.ExitPrice);  // primera operación después del cierre de la vela 3
        Assert.Equal(12m, t.Pnl);         // (109 - 103) * 2
        Assert.Equal(12m, r.Metrics.NetProfit);
    }

    [Fact]
    public void Comision_y_deslizamiento_se_descuentan()
    {
        var s = Settings();
        s.SlippageTicks = 1;
        s.CommissionPerUnit = 0.5m;
        var data = Ticks(100, 101, 102, 103, 104, 105, 106, 107, 108, 109);
        var r = new BacktestEngine(new BuyThenSell(), data, s).Run();
        var t = Assert.Single(r.Trades);
        Assert.Equal(104m, t.EntryPrice); // 103 + 1 tick
        Assert.Equal(108m, t.ExitPrice);  // 109 - 1 tick
        Assert.Equal(8m, t.Pnl);
        Assert.Equal(2m, t.Commission);   // 2 unidades x 0,5 x 2 lados
        Assert.Equal(6m, r.Metrics.NetProfit);
    }

    /// <summary>Entra largo con stop y target automáticos.</summary>
    private sealed class Bracket : Strategy
    {
        public Bracket()
        {
            StopLossTicks = 3;
            TakeProfitTicks = 5;
        }

        protected override void OnBar(Bar bar)
        {
            if (CurrentBar == 0 && IsFlat) Buy();
        }
    }

    [Fact]
    public void Take_profit_cierra_y_cancela_el_stop()
    {
        var data = Ticks(100, 100, 100, 100, 102, 104, 106, 103, 99, 98);
        var r = new BacktestEngine(new Bracket(), data, Settings()).Run();
        var t = Assert.Single(r.Trades);
        Assert.Equal(100m, t.EntryPrice);
        Assert.Equal(105m, t.ExitPrice); // límite a +5 ticks, atravesado por 106
        Assert.Equal("Take profit", t.ExitName);
    }

    [Fact]
    public void Stop_loss_se_ejecuta_como_mercado()
    {
        var data = Ticks(100, 100, 100, 100, 99, 98, 96, 95, 99, 98);
        var r = new BacktestEngine(new Bracket(), data, Settings()).Run();
        var t = Assert.Single(r.Trades);
        Assert.Equal(96m, t.ExitPrice); // el stop en 97 se dispara con 96 (hueco)
        Assert.Equal(-4m, t.Pnl);
        Assert.Equal("Stop loss", t.ExitName);
    }

    /// <summary>Límite de compra por debajo del precio.</summary>
    private sealed class LimitBuyer : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            if (CurrentBar == 0) BuyLimit(98);
        }
    }

    [Fact]
    public void Limite_solo_se_llena_si_el_precio_lo_atraviesa()
    {
        var touchOnly = Ticks(100, 100, 100, 99, 98, 99, 100, 101, 102, 103);
        var r = new BacktestEngine(new LimitBuyer(), touchOnly, Settings()).Run();
        Assert.Empty(r.Trades);

        var through = Ticks(100, 100, 100, 99, 97, 99, 100, 101, 102, 103);
        var r2 = new BacktestEngine(new LimitBuyer(), through, Settings()).Run();
        var t = Assert.Single(r2.Trades); // se cierra al final del backtest
        Assert.Equal(98m, t.EntryPrice);
        Assert.Equal("End of backtest", t.ExitName);
    }

    /// <summary>Da vuelta la posición de largo a corto.</summary>
    private sealed class Reverser : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            if (CurrentBar == 0) EnterLong();
            if (CurrentBar == 1) EnterShort();
        }
    }

    [Fact]
    public void Dar_vuelta_la_posicion_registra_dos_operaciones()
    {
        var data = Ticks(100, 100, 100, 101, 102, 103, 104, 105, 106, 103);
        var r = new BacktestEngine(new Reverser(), data, Settings()).Run();
        Assert.Equal(2, r.Trades.Count);
        Assert.True(r.Trades[0].IsLong);
        Assert.Equal(101m, r.Trades[0].EntryPrice);
        Assert.Equal(104m, r.Trades[0].ExitPrice);
        Assert.False(r.Trades[1].IsLong);
        Assert.Equal(104m, r.Trades[1].EntryPrice);
        Assert.Equal(103m, r.Trades[1].ExitPrice); // fin del backtest
        Assert.Equal(4m, r.Metrics.NetProfit);
    }

    private sealed class SmaCross : Strategy
    {
        [Parameter, Optimize(2, 4, 1)]
        public int Fast { get; set; } = 2;

        private Sma _fast = null!, _slow = null!;

        protected override void OnInit()
        {
            _fast = Sma(Fast);
            _slow = Sma(5);
        }

        protected override void OnBar(Bar bar)
        {
            if (CrossAbove(_fast, _slow)) EnterLong();
            else if (CrossBelow(_fast, _slow)) EnterShort();
        }
    }

    [Fact]
    public void Optimizacion_prueba_todas_las_combinaciones_y_ordena_por_metrica()
    {
        var prices = Enumerable.Range(0, 600).Select(i => 1000m + (decimal)Math.Round(30 * Math.Sin(i / 15.0))).ToArray();
        var data = Ticks(prices);
        var ranges = ParameterRange.FromStrategy(typeof(SmaCross), new SmaCross());
        var fast = ranges.Single(r => r.Name == nameof(SmaCross.Fast));
        Assert.True(fast.Enabled);
        Assert.Equal(3, fast.Count);
        var res = Optimizer.Optimize(() => new SmaCross(), new(), ranges, data, Settings(), FitnessMetric.NetProfit, 0, null, CancellationToken.None, 2);
        Assert.Equal(3, res.Rows.Count);
        Assert.True(res.Rows[0].Fitness >= res.Rows[1].Fitness);

        var mc = Optimizer.MonteCarlo(new BacktestEngine(new SmaCross(), data, Settings()).Run().Trades, 1000, 200);
        Assert.True(mc.DrawdownP95 >= mc.DrawdownP50);
    }

    [Fact]
    public void Huecos_de_historia_se_detectan_antes_entre_y_despues()
    {
        var t0 = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
        var local = new List<Trade> { new(10, t0.AddMinutes(30), 1, 1, TradeSide.Buy), new(11, t0.AddMinutes(31), 1, 1, TradeSide.Buy), new(12, t0.AddMinutes(50), 1, 1, TradeSide.Buy) };
        var gaps = InstrumentData.FindGaps(local, t0, t0.AddHours(1), tailById: true);
        Assert.Equal(3, gaps.Count);
        Assert.Equal((t0, t0.AddMinutes(30), (long?)null), gaps[0]);
        Assert.Equal(t0.AddMinutes(31), gaps[1].From);
        Assert.Equal(13, gaps[2].FromId);
    }

    /// <summary>Ruptura de canal con ATR: el canal es parámetro y el período del ATR una constante del código.</summary>
    private sealed class CanalConAtr : Strategy
    {
        private const int PeriodoAtr = 14;
        private Highest _max = null!;
        private Atr _atr = null!;

        [Parameter("Canal"), Optimize(10, 40, 10)]
        public int Canal { get; set; } = 20;

        /// <summary>Velas cerradas cuando estuvieron listos todos los indicadores (-1 = nunca).</summary>
        public int ReadyAt { get; private set; } = -1;

        protected override void OnInit()
        {
            _max = Highest(Canal);
            Lowest(Canal);
            _atr = Atr(PeriodoAtr);
            StopLossTicks = TickSize > 0 ? 50 : 0; // usa el instrumento en OnInit (el medidor tiene que tenerlo)
        }

        protected override void OnBar(Bar bar)
        {
            if (ReadyAt < 0 && _max.IsReady && _atr.IsReady) ReadyAt = CurrentBar + 1;
        }
    }

    private sealed class RsiYMacd : Strategy
    {
        protected override void OnInit()
        {
            Rsi(14);
            Macd();
        }
    }

    private sealed class SinIndicadores : Strategy
    {
    }

    private sealed class FallaAlEmpezar : Strategy
    {
        protected override void OnInit()
        {
            Sma(5);
            throw new InvalidOperationException("roto");
        }
    }

    [Fact]
    public void Calentamiento_informa_los_indicadores_con_su_periodo_y_las_velas_que_necesitan()
    {
        var w = StrategyWarmup.Measure(typeof(CanalConAtr));
        Assert.Null(w.Error);
        Assert.True(w.Complete);
        Assert.Equal(new[] { "Highest(20)", "Lowest(20)", "ATR(14)" }, w.Indicators);
        Assert.Equal(20, w.Bars);
        // con el canal en el máximo de [Optimize] (los parámetros llegan como double, como desde el laboratorio)
        var max = StrategyWarmup.Measure(typeof(CanalConAtr), new Dictionary<string, object?> { ["Canal"] = 40.0 });
        Assert.Equal(40, max.Bars);
        Assert.Contains("Highest(40)", max.Indicators);
        // con el canal más corto que el ATR, manda el ATR
        Assert.Equal(14, StrategyWarmup.Measure(typeof(CanalConAtr), new Dictionary<string, object?> { ["Canal"] = 10.0 }).Bars);

        Assert.Equal(26, StrategyWarmup.Measure(typeof(RsiYMacd)).Bars); // la EMA lenta del MACD (el RSI pide 15)
        var none = StrategyWarmup.Measure(typeof(SinIndicadores));
        Assert.True(none.Complete);
        Assert.Equal(0, none.Bars);
        Assert.Empty(none.Indicators);
        var bad = StrategyWarmup.Measure(typeof(FallaAlEmpezar));
        Assert.False(bad.Complete);
        Assert.Contains("roto", bad.Error);
    }

    [Fact]
    public void Calentamiento_medido_coincide_con_el_del_backtest()
    {
        var prices = Enumerable.Range(0, 300).Select(i => 1000m + (decimal)Math.Round(30 * Math.Sin(i / 9.0))).ToArray();
        var s = new CanalConAtr { Canal = 30 };
        var r = new BacktestEngine(s, Ticks(prices), Settings()).Run();
        Assert.Null(r.Error);
        Assert.Equal(StrategyWarmup.Measure(typeof(CanalConAtr), s.GetParameters()).Bars, s.ReadyAt);
        Assert.Equal(30, s.ReadyAt);
    }
}
