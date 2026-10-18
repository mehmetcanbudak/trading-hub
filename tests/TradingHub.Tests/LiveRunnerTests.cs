using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

/// <summary>
/// Ejecución en vivo de una estrategia: el tamaño de la cartera no rompe las salidas, la pausa solo deja achicar, los
/// límites de riesgo frenan o detienen, y cerrar nunca manda más de lo que la cuenta tiene.
/// </summary>
[Collection("LiveStore")] // cambian la carpeta de la cartera (estática): no en paralelo
public class LiveRunnerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub_live_" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = LiveStore.Root;
    private readonly Instrument _inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
    private InstrumentData _data = null!;
    private long _id = 1;
    private DateTime _time = T0;

    public LiveRunnerTests() => LiveStore.Root = _root;

    public void Dispose()
    {
        LiveStore.Root = _previousRoot;
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // limpieza
        }
    }

    /// <summary>Estrategia que hace lo que el test le pide en la próxima vela.</summary>
    private sealed class Scripted : Strategy
    {
        public readonly Queue<Action<Scripted>> Next = new();
        public readonly List<decimal> Seen = new();

        protected override void OnBar(Bar bar)
        {
            Seen.Add(Position);
            if (Next.Count > 0) Next.Dequeue()(this);
        }

        public void DoBuy(decimal q) => Buy(q);
        public void DoSell(decimal q) => Sell(q);
        public void DoClose() => ClosePosition();
        public void DoShort(decimal q) => EnterShort(q);
        public void DoLong(decimal q) => EnterLong(q);
    }

    private (Scripted Strategy, SimBroker Broker, LiveStrategyRunner Runner) Setup(decimal size = 1, Action<LiveStrategyRunner>? configure = null, SimBroker? broker = null)
    {
        _data = new InstrumentData(_inst, new SimulatedFeed());
        for (int i = 0; i < 5; i++) _data.Trades.Add(new Trade(_id++, (_time = T0.AddSeconds(i * 30)), 100, 1, TradeSide.Buy));
        broker ??= new SimBroker(_data, new Portfolio(), new TradingStatistics());
        var s = new Scripted();
        var r = new LiveStrategyRunner(s, _data, broker, ChartPeriod.M1) { SizeMultiplier = size };
        configure?.Invoke(r);
        r.Start();
        return (s, broker, r);
    }

    /// <summary>Llega una operación nueva un minuto después (cierra la vela anterior).</summary>
    private void Tick(decimal price, double minutes = 1)
    {
        int start = _data.Trades.Count;
        _time = _time.AddMinutes(minutes);
        _data.Trades.Add(new Trade(_id++, _time, price, 1, TradeSide.Buy));
        var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        ((Action<int, int>?)field!.GetValue(_data))?.Invoke(start, 1);
    }

    [Fact]
    public void En_vivo_el_contexto_arranca_con_el_mes_de_historia_que_le_pasa_la_app()
    {
        var (s, _, r) = Setup();
        // con dos minutos de calentamiento no hay contexto (y sin datos el filtro no bloquea)
        Assert.Equal(MarketRegime.Unknown, s.Regime);

        // un mes de cierres por hora subiendo derecho hasta el precio de ahora
        var hourly = Enumerable.Range(0, 31 * 24).Select(i => (T0.AddHours(-31 * 24 + i), 100m - (31 * 24 - i) * 0.05m)).ToList();
        r.SeedContext(hourly);
        Assert.Equal(MarketRegime.Trend, s.Regime);
        Assert.Equal(1, s.MarketDirection);
        Assert.NotNull(s.VolatilityRank);
        Assert.Equal(31, r.ContextDays);
        Assert.Contains(r.LogLines, l => l.Contains("Market context with 31 days", StringComparison.Ordinal));

        // un filtro «solo en rango» ahora no deja entrar (antes, sin datos, habría entrado en cualquier contexto)
        s.RegimeFilter = RegimeFilter.RangeOnly;
        Assert.False(s.ContextAllowsEntries);
        s.RegimeFilter = RegimeFilter.UpTrendOnly;
        Assert.True(s.ContextAllowsEntries);
    }

    [Fact]
    public void La_condicion_de_mercado_se_cambia_en_vivo_sin_reiniciar()
    {
        var (s, _, r) = Setup();
        var hourly = Enumerable.Range(0, 31 * 24).Select(i => (T0.AddHours(-31 * 24 + i), 100m - (31 * 24 - i) * 0.05m)).ToList();
        r.SeedContext(hourly);
        r.SetContextFilter(RegimeFilter.RangeOrDownTrend, 0, 100);
        Assert.Equal(RegimeFilter.RangeOrDownTrend, s.RegimeFilter);
        Assert.False(s.ContextAllowsEntries); // viene subiendo
        Assert.Contains(r.LogLines, l => l.Contains("trades in range or downtrend", StringComparison.Ordinal));
        Assert.True(r.IsRunning);
        r.SetContextFilter(RegimeFilter.NoDownTrend, 0, 100);
        Assert.True(s.ContextAllowsEntries);
        Assert.Equal(RegimeFilter.NoDownTrend, r.Info().Parameters["RegimeFilter"]);
    }

    [Fact]
    public void Cambiar_el_tamano_con_la_posicion_abierta_no_rompe_la_salida()
    {
        var (s, broker, r) = Setup(size: 1.5m);
        s.Next.Enqueue(x => x.DoBuy(2));
        Tick(101);
        Tick(102);
        Assert.Equal(3m, broker.Position!.Volume); // 2 × 1,5
        Assert.Equal(2m, r.VirtualPosition); // la estrategia ve sus 2

        r.SizeMultiplier = 0.5m;
        s.Next.Enqueue(x => x.DoClose()); // vende «su» posición (2): se cierran los 3 reales
        Tick(103);
        Tick(104);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Equal(0m, r.VirtualPosition);

        s.Next.Enqueue(x => x.DoBuy(2)); // con ×0,5 entra 1
        Tick(105);
        Tick(106);
        Assert.Equal(1m, broker.Position!.Volume);

        // una salida con cantidad fija (la de la estrategia) también cierra justo lo real
        r.SizeMultiplier = 2m;
        s.Next.Enqueue(x => x.DoSell(2));
        Tick(107);
        Tick(108);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
    }

    [Fact]
    public void Un_tamano_que_redondea_a_cero_manda_igual_el_minimo()
    {
        // 1 contrato × 0,5 no existe en un futuro: antes la orden «quedaba en cero» y la estrategia no operaba nunca
        var (s, broker, r) = Setup(size: 0.5m);
        Assert.Equal(1m, r.UnitsFor(1)); // se muestra «cada orden: 1 contrato (mínimo)»
        s.Next.Enqueue(x => x.DoLong(1));
        Tick(101);
        Tick(102);
        Assert.Equal(1m, broker.Position!.Volume);
        Assert.Equal(1m, r.VirtualPosition);
        Assert.True(r.SizeBelowMinimum(1));

        s.Next.Enqueue(x => x.DoClose()); // y la salida cierra justo ese contrato
        Tick(103);
        Tick(104);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
    }

    [Fact]
    public void Con_el_micro_la_fraccion_del_mini_se_ejecuta_entera_en_micros()
    {
        // la estrategia mira «TEST» (el mini) y ejecuta en «MTEST» (su micro, 10 veces más chico): 1 × 0,5 = 5 micros
        var micro = new Instrument { Symbol = "MTEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 0.1m };
        var microData = new InstrumentData(micro, new SimulatedFeed());
        microData.Trades.Add(new Trade(1, T0, 100, 1, TradeSide.Buy));
        var microBroker = new SimBroker(microData, new Portfolio(), new TradingStatistics());
        var (s, _, r) = Setup(size: 0.5m, configure: x =>
        {
            x.ExecInstrument = micro;
            x.ExecRatio = 10;
        }, broker: microBroker);
        var notices = new List<string>();
        r.Executed += (_, text) => notices.Add(text);
        Assert.Equal(5m, r.UnitsFor(1)); // lo que se muestra como «cada orden: 5 MTEST»
        s.Next.Enqueue(x => x.DoLong(1));
        Tick(101);
        Tick(102);
        Assert.Equal(5m, microBroker.Position!.Volume);
        Assert.Equal(1m, r.VirtualPosition); // la estrategia ve su contrato
        Assert.Equal("MTEST", r.Ledger.OpenSymbol);
        Assert.True(r.CrossExecution);
        Assert.False(r.SizeBelowMinimum(1));
        // el aviso dice cuántos contratos, de cuál y que es el micro (con su equivalente en el mini)
        var notice = Assert.Single(notices);
        Assert.Contains("bought 5 MTEST", notice);
        Assert.Contains("micro: equivalent to 0.5 TEST", notice);

        microData.Trades.Add(new Trade(2, T0.AddMinutes(10), 110, 1, TradeSide.Buy)); // el micro sube 10 puntos
        s.Next.Enqueue(x => x.DoClose());
        Tick(110);
        Tick(110);
        Assert.Equal(0m, microBroker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Null(r.Ledger.OpenSymbol);
        // resultado con el valor del punto del micro: 5 × (precio de salida − entrada) × 0,1
        var trade = Assert.Single(r.Ledger.Trades);
        Assert.Equal(5m * (trade.ExitPrice - trade.EntryPrice) * 0.1m, trade.NetPnl);
    }

    [Fact]
    public void Una_contabilidad_vieja_sin_contrato_usa_el_valor_del_punto_de_la_estrategia()
    {
        var l = new LiveLedger("x", 20);
        l.Apply(new LiveFill(T0, true, 1, 100)); // llenado viejo: sin contrato ni valor del punto
        l.Apply(new LiveFill(T0.AddMinutes(1), false, 1, 101));
        Assert.Equal(20m, l.Realized);
        l.Apply(new LiveFill(T0.AddMinutes(2), true, 5, 100, "MNQ", 2)); // ahora en micros
        Assert.Equal("MNQ", l.OpenSymbol);
        Assert.Equal(10m, l.OpenPnL(101)); // 5 × 1 × 2
        l.Apply(new LiveFill(T0.AddMinutes(3), false, 5, 102, "MNQ", 2));
        Assert.Equal(40m, l.Realized); // 20 + 5 × 2 × 2
    }

    [Fact]
    public void La_vuelta_manda_el_cierre_real_mas_la_entrada_con_el_tamano()
    {
        var (s, broker, r) = Setup(size: 2m);
        s.Next.Enqueue(x => x.DoLong(1));
        Tick(101);
        Tick(102);
        Assert.Equal(2m, broker.Position!.Volume);
        s.Next.Enqueue(x => x.DoShort(1)); // la estrategia pide 1 + 1: en la cuenta, cerrar 2 y abrir 2
        Tick(101);
        Tick(100);
        Assert.Equal(-2m, broker.Position!.Volume);
        Assert.Equal(-1m, r.VirtualPosition);
    }

    [Fact]
    public void En_pausa_no_abre_pero_deja_cerrar()
    {
        var (s, broker, r) = Setup();
        r.SetPaused(true);
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(101);
        Tick(102);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Contains(r.LogLines, l => l.Contains("Paused: did not submit", StringComparison.Ordinal));

        r.SetPaused(false);
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(103);
        Tick(104);
        Assert.Equal(1m, broker.Position!.Volume);

        // pausada con posición: una vuelta se recorta a cerrar
        r.SetPaused(true);
        s.Next.Enqueue(x => x.DoShort(1));
        Tick(103);
        Tick(102);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.VirtualPosition);
        Assert.True(r.Info().Paused);
    }

    [Fact]
    public void La_perdida_maxima_del_dia_cierra_y_frena_hasta_la_proxima_sesion()
    {
        var (s, broker, r) = Setup(configure: x => x.MaxDailyLoss = 5);
        string? hit = null;
        r.LimitHit += (_, msg) => hit = msg;
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        Assert.Equal(1m, broker.Position!.Volume);

        Tick(94); // −6 abierto: toca el límite
        Tick(94);
        Assert.NotNull(hit);
        Assert.True(r.DailyLimitActive);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
        Assert.True(r.IsRunning);

        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(95);
        Tick(95);
        Assert.Equal(0m, broker.Position!.Volume); // frenada

        Tick(96, minutes: 24 * 60); // otro día de sesión
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(96);
        Tick(96);
        Assert.False(r.DailyLimitActive);
        Assert.Equal(1m, broker.Position!.Volume);
    }

    [Fact]
    public void El_drawdown_maximo_la_detiene_y_cierra_su_posicion()
    {
        var (s, broker, r) = Setup(configure: x => x.MaxDrawdown = 8);
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        Tick(91); // −9
        Assert.False(r.IsRunning);
        Assert.Contains("drawdown", r.StopReason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position); // el cierre quedó en su contabilidad aunque ya estaba detenida
    }

    [Fact]
    public void Detener_y_cerrar_solo_cierra_lo_suyo_y_queda_contabilizado()
    {
        var (s, broker, r) = Setup();
        s.Next.Enqueue(x => x.DoBuy(2));
        Tick(101);
        Tick(102);
        broker.Market(OrderDirections.Buy, 3, "manual"); // vos, a mano, en la misma cuenta
        Assert.Equal(5m, broker.Position!.Volume);
        r.Stop(flatten: true);
        Assert.Equal(3m, broker.Position!.Volume); // lo tuyo queda
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Equal(0m, LiveStore.LoadLedger(r.Key, 1).Position);
    }

    [Fact]
    public void Cerrar_nunca_manda_mas_de_lo_que_la_cuenta_tiene()
    {
        var (s, broker, r) = Setup();
        s.Next.Enqueue(x => x.DoBuy(2));
        Tick(101);
        Tick(102);
        broker.Market(OrderDirections.Sell, 2, "manual"); // la cerraste a mano desde el Chart Trader
        Assert.Equal(0m, broker.Position!.Volume);
        r.ClosePosition();
        Assert.Equal(0m, broker.Position!.Volume); // no abrió un corto
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Contains(r.LogLines, l => l.Contains("did not have", StringComparison.Ordinal));
    }

    [Fact]
    public void Una_posicion_heredada_que_la_simulada_no_tiene_se_cierra_en_la_contabilidad()
    {
        // quedó corta 2 de la última vez y Trading Hub se cerró (la simulada nueva arranca plana)
        var key = $"{typeof(Scripted).FullName}|test:TEST|Cuenta simulada";
        LiveStore.AppendFill(key, new LiveFill(T0.AddDays(-1), false, 2, 100));
        var (s, broker, r) = Setup();
        Assert.Equal(key, r.Key);
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Equal(0m, r.VirtualPosition);
        s.Next.Enqueue(x => x.DoBuy(1)); // y no compra 2 de más para «cerrar» algo que no existe
        Tick(101);
        Tick(102);
        Assert.Equal(1m, broker.Position!.Volume);
    }

    [Fact]
    public void Una_posicion_heredada_que_la_cuenta_tiene_se_sigue_gestionando()
    {
        _data = new InstrumentData(_inst, new SimulatedFeed());
        for (int i = 0; i < 5; i++) _data.Trades.Add(new Trade(_id++, (_time = T0.AddSeconds(i * 30)), 100, 1, TradeSide.Buy));
        var broker = new SimBroker(_data, new Portfolio(), new TradingStatistics());
        broker.Market(OrderDirections.Sell, 2, "de antes");
        var key = $"{typeof(Scripted).FullName}|test:TEST|Cuenta simulada";
        LiveStore.AppendFill(key, new LiveFill(T0.AddDays(-1), false, 2, 100));
        var s = new Scripted();
        var r = new LiveStrategyRunner(s, _data, broker, ChartPeriod.M1) { SizeMultiplier = 2 };
        r.Start();
        Assert.Equal(-2m, r.Ledger.Position);
        Assert.Equal(-1m, r.VirtualPosition); // con ×2, la estrategia ve 1
        s.Next.Enqueue(x => x.DoClose());
        Tick(99);
        Tick(98);
        Assert.Equal(0m, broker.Position!.Volume);
    }

    [Fact]
    public void Al_reiniciar_le_vuelve_a_poner_el_stop_pero_nunca_lo_duplica()
    {
        // reinicio (por ejemplo con otros parámetros): la nueva vuelve a proteger la posición
        var (s, broker, r) = Setup(configure: x => x.ProtectOnStart = true);
        s.StopLossTicks = 5;
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        r.Stop(flatten: false);
        Assert.Empty(broker.ActiveOrders);
        var s2 = new Scripted { StopLossTicks = 5 };
        var r2 = new LiveStrategyRunner(s2, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r2.Start();
        Assert.Equal(1m, r2.Ledger.Position);
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);

        // Trading Hub se cerró de golpe y el stop quedó vivo en el broker: no se pone otro
        r2.Dispose();
        broker.Stop(OrderDirections.Sell, 1, 95, "Scripted: Stop loss");
        var r3 = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r3.Start();
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.True(r3.LogLines.Any(l => l.Contains("no additional stop", StringComparison.Ordinal)), string.Join(" | ", r3.LogLines));
    }

    /// <summary>Limitador de prueba: deja pasar hasta cierta posición.</summary>
    private sealed class MaxPosition(decimal max) : ILiveOrderGuard
    {
        public int Blocked;

        public string? Check(LiveStrategyRunner runner, OrderSide side, decimal quantity, decimal positionAfter)
        {
            if (Math.Abs(positionAfter) <= max) return null;
            Blocked++;
            return "pasa el tope";
        }
    }

    [Fact]
    public void El_limitador_bloquea_lo_que_abre_de_mas_pero_nunca_una_salida()
    {
        var guard = new MaxPosition(1);
        var (s, broker, r) = Setup(configure: x => x.Guard = guard);
        s.Next.Enqueue(x => x.DoBuy(2)); // quedaría en 2: bloqueada
        Tick(101);
        Tick(102);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(1, guard.Blocked);
        Assert.Contains(r.LogLines, l => l.Contains("Risk guard", StringComparison.Ordinal));
        s.Next.Enqueue(x => x.DoBuy(1)); // dentro del tope
        Tick(103);
        Tick(104);
        Assert.Equal(1m, broker.Position!.Volume);
        guard = new MaxPosition(0); // ahora no deja abrir nada… pero salir sí
        r.Guard = guard;
        s.Next.Enqueue(x => x.DoClose());
        Tick(105);
        Tick(106);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public void Una_posicion_sembrada_se_sigue_gestionando_y_su_salida_queda_unida()
    {
        _data = new InstrumentData(_inst, new SimulatedFeed());
        for (int i = 0; i < 5; i++) _data.Trades.Add(new Trade(_id++, (_time = T0.AddSeconds(i * 30)), 100, 1, TradeSide.Buy));
        var broker = new SimBroker(_data, new Portfolio(), new TradingStatistics()) { Name = "Visual" };
        broker.Market(OrderDirections.Buy, 1, "historia");
        var s = new Scripted();
        var r = new LiveStrategyRunner(s, _data, broker, ChartPeriod.M1) { Persist = false };
        r.SeedPosition(new LiveFill(T0.AddMinutes(-30), true, 1, 95));
        r.Start();
        Assert.Equal(1m, r.VirtualPosition);
        s.Next.Enqueue(x => x.DoClose());
        Tick(101);
        Tick(102);
        Assert.Equal(0m, broker.Position!.Volume);
        var t = Assert.Single(r.Ledger.Trades);
        Assert.Equal(T0.AddMinutes(-30), t.EntryTime); // la salida se une a la entrada de la historia
        Assert.Equal(95m, t.EntryPrice);
        Assert.False(File.Exists(LiveStore.LedgerFile(r.Key))); // visual: no se guarda
    }

    [Fact]
    public void Empezar_de_cero_solo_sin_posicion()
    {
        var (s, broker, r) = Setup();
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(101);
        Tick(102);
        Assert.False(r.ResetLedger()); // con posición: no
        s.Next.Enqueue(x => x.DoClose());
        Tick(103);
        Tick(104);
        Assert.Single(r.Ledger.Trades);
        Assert.True(r.ResetLedger());
        Assert.Empty(r.Ledger.Trades);
        Assert.Empty(LiveStore.LoadLedger(r.Key, 1).Fills);
    }

    [Fact]
    public void Las_excursiones_salen_de_los_precios_entre_entrada_y_salida()
    {
        var t = new ClosedTrade(1, true, T0, 100, T0.AddMinutes(5), 104, 2, 8, 0, 0, 0, "", "", 0);
        var (mae, mfe) = TradeExcursion.Compute(t, new decimal[] { 100, 97, 103, 106, 104 }, 5);
        Assert.Equal(-30m, mae); // (97−100) × 2 × 5
        Assert.Equal(60m, mfe); // (106−100) × 2 × 5
        var shortTrade = t with { IsLong = false, ExitPrice = 98 };
        var (smae, smfe) = TradeExcursion.Compute(shortTrade, new decimal[] { 100, 101, 95, 98 }, 1);
        Assert.Equal(-2m, smae);
        Assert.Equal(10m, smfe);
    }
}
