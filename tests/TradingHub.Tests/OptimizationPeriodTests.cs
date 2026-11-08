using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>El período de las velas como un parámetro más del optimizador y del walk-forward.</summary>
public class OptimizationPeriodTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };

    /// <summary>Días de operaciones con ráfagas en el mismo segundo, una onda lenta y ruido.</summary>
    private static TickData Data(int days, int seed = 5)
    {
        var rnd = new Random(seed);
        var list = new List<Trade>();
        var t0 = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        double price = 1000;
        long id = 1;
        for (int m = 0; m < days * 24 * 60; m++)
        {
            price += 4 * Math.Sin(m / 120.0) * 0.1 + (rnd.NextDouble() - 0.5) * 2;
            decimal p = Math.Round((decimal)price);
            var side = rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell;
            int burst = 1 + rnd.Next(4);
            for (int k = 0; k < burst; k++) list.Add(new Trade(id++, t0.AddMinutes(m).AddMilliseconds(k * 30), p, 1, side));
        }

        return TickData.FromTrades(Inst, list);
    }

    private static BacktestSettings Settings(TickData data) => new()
    {
        FeedId = "test", Symbol = "TEST", FromUtc = data.FirstTime.Date, ToUtc = data.LastTime.Date.AddDays(1), Period = "M15",
        CommissionPercent = 0, SlippageTicks = 0, InitialCapital = 100_000,
    };

    /// <summary>Compra al romper el máximo de N velas y sale a las 3 velas.</summary>
    private sealed class Breakout : Strategy
    {
        [Parameter, Optimize(6, 12, 6)]
        public int Channel { get; set; } = 6;

        private int _entry = -1000;

        protected override void OnBar(Bar bar)
        {
            if (Bars.Count <= Channel) return;
            decimal max = 0;
            for (int i = 1; i <= Channel; i++) max = Math.Max(max, Bars[i].High);
            if (IsFlat && bar.Close > max)
            {
                Buy(1);
                _entry = CurrentBar;
            }
            else if (IsLong && CurrentBar - _entry >= 3) ClosePosition();
        }
    }

    private static List<ParameterRange> Ranges(params string[] periods) =>
        new() { new ParameterRange { Name = nameof(Breakout.Channel), Label = "Canal", Enabled = true, Min = 6, Max = 12, Step = 6, IsInteger = true }, ParameterRange.Periods(periods) };

    [Fact]
    public void Los_periodos_quedan_validos_ordenados_y_sin_repetir()
    {
        var r = ParameterRange.Periods(new[] { "H1", "m5", "M15", "cualquiera", "M5", "D1" });
        Assert.Equal(new[] { "M5", "M15", "H1", "Daily" }, r.Choices);
        Assert.Equal(4, r.Count);
        Assert.Equal(1, ParameterRange.Periods(new[] { "H1" }, enabled: false).Count);
        Assert.Equal(2d, r.Numeric("H1"));
        Assert.Null(r.Numeric("H4"));
    }

    [Fact]
    public void El_optimizador_prueba_cada_combinacion_en_cada_periodo()
    {
        var data = Data(3);
        var res = Optimizer.Optimize(() => new Breakout(), new(), Ranges("M5", "M15", "H1"), data, Settings(data), FitnessMetric.NetProfit, 0, null, CancellationToken.None);
        Assert.Equal(6, res.Combinations);
        Assert.Equal(6, res.Rows.Count);
        Assert.Equal(new[] { "H1", "M15", "M5" }, res.Rows.Select(r => (string)r.Parameters[ParameterRange.PeriodName]!).Distinct().OrderBy(p => p));

        // el mismo canal en velas más chicas da más operaciones: cada combinación corrió en su período
        int Trades(string period) => res.Rows.First(r => (string)r.Parameters[ParameterRange.PeriodName]! == period && Equals(r.Parameters[nameof(Breakout.Channel)], 6)).Metrics.TotalTrades;
        Assert.True(Trades("M5") > Trades("M15") && Trades("M15") > Trades("H1"), $"{Trades("M5")} {Trades("M15")} {Trades("H1")}");
        Assert.Equal(Trades("M15"), new BacktestEngine(new Breakout(), data, Settings(data)).Run().Metrics.TotalTrades);
    }

    [Fact]
    public void Sin_el_periodo_tildado_se_usa_el_del_backtest()
    {
        var data = Data(2);
        var ranges = Ranges("M5", "H1");
        ranges[1].Enabled = false;
        var res = Optimizer.Optimize(() => new Breakout(), new(), ranges, data, Settings(data), FitnessMetric.NetProfit, 0, null, CancellationToken.None);
        Assert.Equal(2, res.Combinations);
        Assert.All(res.Rows, r => Assert.False(r.Parameters.ContainsKey(ParameterRange.PeriodName)));
    }

    [Fact]
    public void El_walk_forward_elige_el_periodo_en_cada_ventana()
    {
        var data = Data(8);
        var res = Optimizer.WalkForward(() => new Breakout(), new(), Ranges("M15", "H1"), data, Settings(data), FitnessMetric.NetProfit, 3, 1, false, 0, null, CancellationToken.None,
            search: SearchMode.Grid);
        Assert.Equal(4, res.Evaluated);
        Assert.Equal(5, res.Windows.Count);
        Assert.All(res.Windows, w => Assert.Contains((string)w.BestParameters[ParameterRange.PeriodName]!, new[] { "M15", "H1" }));
    }

}
