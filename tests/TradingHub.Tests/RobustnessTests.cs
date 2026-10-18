using System.Text.Json;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>Las reglas contra la sobreoptimización (la vara de la validación).</summary>
public class RobustnessTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<ClosedTrade> Trades(params decimal[] pnls) =>
        pnls.Select((p, i) => new ClosedTrade(i + 1, true, T0.AddHours(i), 100, T0.AddHours(i + 0.5), 100 + p, 1, p, 0, 0, 0, "", "", 1)).ToList();

    /// <summary>Resultados que alternan ganancias y pérdidas con una ventaja dada (determinístico).</summary>
    private static List<ClosedTrade> Edge(int n, decimal win, decimal loss, int winsEvery = 2) =>
        Trades(Enumerable.Range(0, n).Select(i => i % winsEvery == 0 ? win : -loss).ToArray());

    private static WalkForwardResult Wf(List<ClosedTrade> oos, double efficiency, double windowsPct, int windows, IEnumerable<Dictionary<string, object?>>? best = null, List<ParameterRange>? ranges = null)
    {
        var m = PerformanceMetrics.Compute(oos, new List<EquityPoint>(), 10_000, true, 0, 0);
        var list = best?.ToList() ?? Enumerable.Range(0, windows).Select(_ => new Dictionary<string, object?> { ["Rapida"] = 10 }).ToList();
        return new WalkForwardResult
        {
            OutSampleTrades = oos,
            OutSampleMetrics = m,
            WalkForwardEfficiency = efficiency,
            ProfitableWindowsPercent = windowsPct,
            Ranges = ranges ?? new List<ParameterRange> { new() { Name = "Rapida", Min = 5, Max = 30, Step = 5 } },
            Windows = list.Select((p, i) => new WalkForwardWindow { Number = i + 1, BestParameters = p, OutSampleFrom = T0.AddDays(i * 10), OutSampleTo = T0.AddDays(i * 10 + 10) }).ToList(),
        };
    }

    [Fact]
    public void Normal_y_su_inversa_son_consistentes()
    {
        Assert.Equal(0.5, Robustness.NormalCdf(0), 6);
        Assert.Equal(0.975, Robustness.NormalCdf(1.959964), 4);
        foreach (var p in new[] { 0.001, 0.02, 0.3, 0.5, 0.8, 0.99, 0.9999 })
            Assert.Equal(p, Robustness.NormalCdf(Robustness.NormalInv(p)), 5);
    }

    [Fact]
    public void Maximo_esperado_crece_con_la_cantidad_de_pruebas()
    {
        Assert.Equal(0, Robustness.ExpectedMaxZ(1));
        double prev = 0;
        foreach (var n in new[] { 2, 10, 100, 1000 })
        {
            double z = Robustness.ExpectedMaxZ(n);
            Assert.True(z > prev, $"n={n}");
            prev = z;
        }

        // valores conocidos: ~1.54 con 10 pruebas, ~2.53 con 100
        Assert.InRange(Robustness.ExpectedMaxZ(10), 1.45, 1.65);
        Assert.InRange(Robustness.ExpectedMaxZ(100), 2.4, 2.65);
    }

    [Fact]
    public void Sharpe_deflactado_baja_cuantas_mas_cosas_se_prueban()
    {
        var trades = Edge(200, 150, 100); // PF 1.5, mitad ganadoras
        var (sr, skew, kurt, n) = Robustness.TradeSharpe(trades);
        Assert.True(sr > 0);
        double one = Robustness.DeflatedSharpe(sr, n, skew, kurt, 1, 0);
        double many = Robustness.DeflatedSharpe(sr, n, skew, kurt, 500, 0.01);
        Assert.True(one > 0.95, $"con una sola prueba {one}");
        Assert.True(many < one, $"{many} < {one}");
        // con muchas pruebas y poca muestra, una ventaja chica no se distingue del azar
        var small = Edge(30, 110, 100);
        var s2 = Robustness.TradeSharpe(small);
        Assert.True(Robustness.DeflatedSharpe(s2.Sharpe, s2.Count, s2.Skew, s2.Kurtosis, 200, 0) < 0.5);
    }

    [Fact]
    public void El_motivo_de_la_suerte_se_explica_en_plata()
    {
        // ventaja chica con muchas pruebas: dice cuánto gana por operación, cuánto varía y cuánto daría la suerte
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(40, 110, 100), 0.7, 80, 8), Trials = 26, MinTrades = 30 });
        var luck = gate.Checks.Single(c => c.Name == "Deflated Sharpe");
        Assert.NotEqual(GateStatus.Pass, luck.Status);
        Assert.StartsWith(ValidationGate.LuckFailPrefix, luck.Text);
        Assert.Contains("per trade", luck.Text);
        Assert.Contains("26 strategies with no edge", luck.Text);
        Assert.True(ValidationGate.IsLuckLine(luck.Text));
        // las pruebas guardadas antes con el texto viejo se siguen reconociendo
        Assert.True(ValidationGate.IsLuckLine("Probabilidad de que no sea suerte, descontando las 4 configuraciones que se probaron: 40%"));
    }

    [Fact]
    public void Estrategia_solida_pasa_todas_las_pruebas()
    {
        var oos = Edge(120, 150, 100);
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(oos, 0.7, 80, 8), Trials = 1, MinTrades = 30 });
        Assert.Equal(GateStatus.Pass, gate.Status);
        Assert.All(gate.Checks, c => Assert.Equal(GateStatus.Pass, c.Status));
        Assert.Equal("pass", gate.StatusText);
    }

    [Fact]
    public void Sobreoptimizada_o_con_poca_muestra_no_pasa()
    {
        // eficiencia 0.2: lo optimizado no se repite fuera de muestra
        var overfit = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(120, 150, 100), 0.2, 80, 8) });
        Assert.Equal(GateStatus.Fail, overfit.Status);
        Assert.Contains(overfit.Checks, c => c.Name == "Efficiency" && c.Status == GateStatus.Fail);

        // 12 operaciones: no alcanza
        var tiny = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(12, 150, 100), 0.8, 80, 6) });
        Assert.Equal(GateStatus.Fail, tiny.Status);
        Assert.Contains(tiny.Checks, c => c.Name == "Sample" && c.Status == GateStatus.Fail);

        // pierde fuera de muestra
        var losing = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(100, 90, 100), 0.8, 30, 6) });
        Assert.Equal(GateStatus.Fail, losing.Status);
    }

    [Fact]
    public void Si_gana_con_pocas_operaciones_le_falta_muestra_no_ventaja()
    {
        // 8 operaciones, PF 3, dos ventanas: no alcanza para concluir, pero no es una perdedora
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(8, 300, 100), 0.8, 50, 2), Trials = 4, MinTrades = 30 });
        Assert.Equal(GateStatus.Fail, gate.Status);
        Assert.True(gate.NeedsMoreSample);
        Assert.Equal("insufficient sample", gate.StatusText);
        Assert.True(gate.SampleFactor >= 30 / 8.0, $"factor {gate.SampleFactor}");
    }

    [Fact]
    public void Si_pierde_o_no_opera_no_es_falta_de_muestra()
    {
        var losing = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(8, 90, 100), 0.8, 50, 2), Trials = 4 });
        Assert.False(losing.NeedsMoreSample);
        Assert.Equal("fail", losing.StatusText);

        var none = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(new List<ClosedTrade>(), 0, 0, 4), Trials = 4 });
        Assert.False(none.NeedsMoreSample);
        Assert.Contains(none.Checks, c => c.Name == "Sample" && c.Text.StartsWith("No out-of-sample trades", StringComparison.Ordinal));

        // con pocas operaciones pero sobreajustada de verdad y perdiendo plata, tampoco
        var overfit = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(12, 95, 100), 0.1, 20, 4), Trials = 4 });
        Assert.False(overfit.NeedsMoreSample);
    }

    [Fact]
    public void Sharpe_deflactado_bajo_con_ventaja_clara_pide_mas_muestra()
    {
        // 40 operaciones con PF 2 y ya se probaron 100 cosas: todavía puede ser suerte, pero con la misma ventaja por
        // operación pasaría con unas 3 veces más operaciones
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(40, 200, 100), 0.8, 80, 8), Trials = 100, MinTrades = 30 });
        Assert.Equal(GateStatus.Fail, gate.Status);
        Assert.Equal(GateStatus.Fail, gate.Checks.First(c => c.Name == "Deflated Sharpe").Status);
        Assert.True(gate.NeedsMoreSample);
        Assert.InRange(gate.SampleFactor, 1.5, ValidationGate.MaxSampleFactor);

        // dudosa por el Sharpe deflactado (entre 50 y 90 %): también le falta muestra
        var doubtful = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(40, 200, 100), 0.8, 80, 8), Trials = 20, MinTrades = 30 });
        Assert.Equal(GateStatus.Warn, doubtful.Status);
        Assert.True(doubtful.NeedsMoreSample);
        Assert.Equal("uncertain", doubtful.StatusText);
    }

    [Fact]
    public void Ventaja_chica_que_no_se_distingue_del_azar_no_es_falta_de_muestra()
    {
        // PF 1,05 con 200 pruebas: ni con 8 veces más operaciones se distinguiría del azar
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(100, 105, 100), 0.8, 80, 8), Trials = 200, TrialVariance = 0.02, MinTrades = 30 });
        Assert.Equal(GateStatus.Fail, gate.Status);
        Assert.False(gate.NeedsMoreSample);
        Assert.Equal("fail", gate.StatusText);
    }

    [Fact]
    public void La_ventaja_que_se_apaga_con_el_tiempo_no_pasa()
    {
        // 90 operaciones que empiezan ganando y terminan perdiendo (en promedio gana: no lo frena ninguna otra regla)
        var dying = Trades(Enumerable.Range(0, 90).Select(i => 240m - 4m * i + (i % 2 == 0 ? 60 : -60)).ToArray());
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(dying, 0.8, 80, 8), Trials = 1, MinTrades = 30 });
        var decay = gate.Checks.Single(c => c.Name == "Edge decay");
        Assert.Equal(GateStatus.Fail, decay.Status);
        Assert.Contains("is fading", decay.Text);
        Assert.True(gate.DecayEarly > 0 && gate.DecayLate < 0 && gate.DecayT < -2);

        // la misma ventaja de punta a punta: se sostiene
        var steady = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(90, 150, 100), 0.8, 80, 8), Trials = 1, MinTrades = 30 });
        Assert.Equal(GateStatus.Pass, steady.Checks.Single(c => c.Name == "Edge decay").Status);
    }

    [Fact]
    public void Se_mide_como_le_va_en_rango_en_tendencia_y_con_volatilidad()
    {
        // gana cuando entra con tendencia y pierde cuando entra en rango
        var trades = Enumerable.Range(0, 60).Select(i =>
        {
            bool trend = i % 2 == 0;
            decimal pnl = trend ? (i % 6 == 0 ? -80 : 200) : (i % 6 == 1 ? 60 : -120);
            return new ClosedTrade(i + 1, true, T0.AddHours(i), 100, T0.AddHours(i + 0.5), 100 + pnl, 1, pnl, 0, 0, 0, "", "", 1)
            {
                EntryTrend = trend ? 0.5 : 0.08,
                EntryVolatility = 0.2 + i % 10 * 0.05,
            };
        }).ToList();
        var regimes = Robustness.Regimes(trades);
        Assert.Equal(30, regimes.Single(r => r.Name == "trend").Trades);
        Assert.True(regimes.Single(r => r.Name == "trend").Net > 0);
        Assert.True(regimes.Single(r => r.Name == "range").Net < 0);
        Assert.Contains(regimes, r => r.Name == "high volatility");

        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(trades, 0.8, 80, 8), Trials = 1, MinTrades = 30 });
        var check = gate.Checks.Single(c => c.Name == "Regimes");
        Assert.Equal(GateStatus.Warn, check.Status);
        Assert.Contains("loses in range", check.Text);
        Assert.Contains("earns in trend", check.Text);

        // las corridas viejas (sin el régimen de cada operación) no tienen esta prueba
        var old = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(60, 150, 100), 0.8, 80, 8) });
        Assert.DoesNotContain(old.Checks, c => c.Name == "Regimes");
    }

    [Fact]
    public void El_regimen_de_entrada_sale_de_las_velas_anteriores()
    {
        // una suba limpia de 24 horas da una tendencia eficiente; un vaivén, rango
        var up = new MarketContextTracker();
        var chop = new MarketContextTracker();
        for (int h = 0; h < 30; h++)
        {
            up.Add(T0.AddHours(h), 100 + h);
            chop.Add(T0.AddHours(h), h % 2 == 0 ? 100 : 102);
        }

        Assert.True(up.Trend > Robustness.TrendingTrend, $"suba {up.Trend}");
        Assert.Equal(MarketRegime.Trend, up.Regime);
        Assert.True(chop.Trend < Robustness.RangeTrend, $"vaivén {chop.Trend}");
        Assert.Equal(MarketRegime.Range, chop.Regime);
        Assert.True(chop.Volatility > up.Volatility);
    }

    [Fact]
    public void Parametros_que_saltan_entre_ventanas_dan_aviso()
    {
        var jumping = new[] { 5, 30, 5, 30, 5, 30 }.Select(v => new Dictionary<string, object?> { ["Rapida"] = v });
        var stable = new[] { 10, 10, 15, 10, 15, 10 }.Select(v => new Dictionary<string, object?> { ["Rapida"] = v });
        var j = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(120, 150, 100), 0.7, 80, 6, jumping) });
        var s = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(120, 150, 100), 0.7, 80, 6, stable) });
        Assert.True(j.Instability > ValidationGate.MaxInstability);
        Assert.Equal(GateStatus.Warn, j.Checks.First(c => c.Name == "Stability").Status);
        Assert.True(s.Instability < ValidationGate.MaxInstability);
        Assert.Equal(GateStatus.Pass, s.Checks.First(c => c.Name == "Stability").Status);
    }

    [Fact]
    public void Parametros_cargados_de_json_se_leen_como_numeros()
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, object?>>("{\"Rapida\":12,\"Texto\":\"7.5\",\"Bool\":true}")!;
        Assert.Equal(12, Robustness.ToDouble(json["Rapida"]));
        Assert.Equal(7.5, Robustness.ToDouble(json["Texto"]));
        Assert.Null(Robustness.ToDouble(json["Bool"]));
        Assert.Equal(3.0, Robustness.ToDouble(3m));
    }

    private static OptimizationResult Grid(Func<int, int, decimal> net)
    {
        var res = new OptimizationResult
        {
            Ranges = new List<ParameterRange>
            {
                new() { Name = "A", Min = 1, Max = 9, Step = 1, Enabled = true },
                new() { Name = "B", Min = 1, Max = 9, Step = 1, Enabled = true },
            },
        };
        for (int a = 1; a <= 9; a++)
        for (int b = 1; b <= 9; b++)
            res.Rows.Add(new OptimizationRow { Parameters = new() { ["A"] = a, ["B"] = b }, Metrics = new PerformanceMetrics { NetProfit = net(a, b) }, Fitness = (double)net(a, b) });
        res.Rows = res.Rows.OrderByDescending(r => r.Fitness).ToList();
        return res;
    }

    [Fact]
    public void Meseta_contra_pico_aislado()
    {
        // meseta: la zona alrededor de (5,5) gana toda
        var plateau = Robustness.Plateau(Grid((a, b) => 1000 - 50 * (Math.Abs(a - 5) + Math.Abs(b - 5))))!.Value;
        Assert.Equal(1.0, plateau.ProfitableShare, 3);
        Assert.Equal(8, plateau.Neighbors);
        // pico: solo (5,5) gana, todo lo demás pierde
        var spike = Robustness.Plateau(Grid((a, b) => a == 5 && b == 5 ? 5000 : -200))!.Value;
        Assert.Equal(0.0, spike.ProfitableShare, 3);
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(120, 150, 100), 0.7, 80, 8), Optimization = Grid((a, b) => a == 5 && b == 5 ? 5000 : -200) });
        Assert.Equal(GateStatus.Warn, gate.Checks.First(c => c.Name == "Plateau").Status);
        Assert.Equal(GateStatus.Warn, gate.Checks.First(c => c.Name == "Robustness").Status);
    }
}
