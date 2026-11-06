using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds.Rithmic;
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
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 3, comment: "manual"); // vos, a mano, en la misma cuenta
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
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Sell, 2, comment: "manual"); // la cerraste a mano desde el Chart Trader
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
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Sell, 2, comment: "de antes");
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
        OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1, 95, "Scripted: Stop loss");
        var r3 = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r3.Start();
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.True(r3.LogLines.Any(l => l.Contains("no additional stop", StringComparison.Ordinal)), string.Join(" | ", r3.LogLines));
    }

    /// <summary>The account is long 1 from a previous run and the strategy's ledger remembers it (as after quitting Trading Hub).</summary>
    private SimBroker InheritedLong()
    {
        _data = new InstrumentData(_inst, new SimulatedFeed());
        for (int i = 0; i < 5; i++) _data.Trades.Add(new Trade(_id++, (_time = T0.AddSeconds(i * 30)), 100, 1, TradeSide.Buy));
        var broker = new SimBroker(_data, new Portfolio(), new TradingStatistics());
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1, comment: "before the restart");
        LiveStore.AppendFill($"{typeof(Scripted).FullName}|test:TEST|Cuenta simulada", new LiveFill(T0.AddDays(-1), true, 1, 100));
        return broker;
    }

    [Theory]
    [InlineData("Rithmic")]
    [InlineData("Tradovate")]
    [InlineData("")]
    public void Restart_never_duplicates_a_stop_the_broker_restored_with_its_own_comment(string comment)
    {
        // after a process restart the broker reports the surviving stop with its own comment (Binance: none), not "Scripted: …"
        var broker = InheritedLong();
        var restored = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1, 95, comment);
        restored.ExchangeOrderId = "restored-1";
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r.Start();
        Assert.Equal(1m, r.Ledger.Position);
        Assert.Same(restored, Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop));
        Assert.True(r.LogLines.Any(l => l.Contains("no additional stop", StringComparison.Ordinal) && l.Contains("Chart Trader", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
    }

    [Fact]
    public void An_order_that_would_not_close_the_inherited_position_does_not_block_its_protection()
    {
        var broker = InheritedLong();
        OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 1, 90, "Rithmic"); // adds to the long position: it is not a stop or target for it
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r.Start();
        var stop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.Equal(OrderDirections.Sell, stop.Direction);
        Assert.Equal(95m, stop.TriggerPrice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Rithmic")]
    public void Restart_places_its_stop_loss_but_no_extra_take_profit_while_a_users_closing_limit_is_working(string comment)
    {
        // stop first: the user's own take-profit limit (reported with the broker's comment, or none) never leaves the
        // inherited position without a stop; a take profit next to it would close the position twice, so it is left out
        var broker = InheritedLong();
        var limit = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 1, 108, comment);
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r.Start();
        Assert.Equal(1m, r.Ledger.Position);
        var stop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.Equal(OrderDirections.Sell, stop.Direction);
        Assert.Equal(95m, stop.TriggerPrice);
        Assert.Equal(1m, stop.Unfilled);
        Assert.Same(limit, Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Limit)); // no extra target
        Assert.Equal(StrategyOrderType.Stop, Assert.Single(r.ActiveOrders, o => o.IsProtective).Type);
        Assert.True(r.LogLines.Any(l => l.Contains("closing limit order", StringComparison.Ordinal) && l.Contains("no additional take profit", StringComparison.Ordinal)
                                        && l.Contains("Chart Trader", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restart_places_neither_stop_loss_nor_take_profit_while_a_closing_stop_is_working(bool withLimit)
    {
        // unchanged: a working stop that could be its stop loss (here restored by the broker, alone or with a target) means
        // nothing is added
        var broker = InheritedLong();
        var working = new List<Order> { OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1, 94, "Rithmic") };
        if (withLimit) working.Add(OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 1, 108, "Rithmic"));
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r.Start();
        Assert.Equal(1m, r.Ledger.Position);
        Assert.Equal(working, broker.ActiveOrders); // no stop and no target placed
        Assert.Empty(r.ActiveOrders);
        Assert.True(r.LogLines.Any(l => l.Contains("no additional stop or target", StringComparison.Ordinal) && l.Contains("Chart Trader", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
    }

    [Fact]
    public void Restart_places_both_stop_loss_and_take_profit_when_no_working_order_could_be_them()
    {
        var broker = InheritedLong();
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r.Start();
        var stop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.Equal(OrderDirections.Sell, stop.Direction);
        Assert.Equal(95m, stop.TriggerPrice);
        var target = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Limit);
        Assert.Equal(OrderDirections.Sell, target.Direction);
        Assert.Equal(110m, target.Price);
        Assert.Equal(2, r.ActiveOrders.Count(o => o.IsProtective));
    }

    [Fact]
    public void An_unsaved_execution_recovers_once_in_order_after_storage_repair()
    {
        var (s, _, r) = Setup();
        string file = LiveStore.LedgerFile(r.Key);
        string committed = file + ".fixture-committed";
        File.Move(file, committed);
        Directory.CreateDirectory(file); // the ledger path cannot be written as a file
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(101);
        Tick(102);
        Assert.Equal(1m, r.Ledger.Position);
        Assert.True(r.Paused);

        Directory.Delete(file); // writable again: the pending fill goes first, then the new one
        File.Move(committed, file);
        s.Next.Enqueue(x => x.DoClose());
        Tick(103);
        Tick(104);
        var saved = LiveStore.LoadLedger(r.Key, 1);
        Assert.Equal(new[] { (true, 1m, LiveFillKind.Execution), (false, 1m, LiveFillKind.Execution) },
            saved.Fills.Select(fill => (fill.Buy, fill.Quantity, fill.Kind)));
        Assert.Equal(0m, saved.Position);
        Assert.Equal(0, LiveStore.PendingLedgerEvents(r.Key));
    }

    [Fact]
    public void A_kept_stop_adopted_by_its_id_keeps_trailing_and_is_replaced_when_the_position_changes()
    {
        var broker = InheritedLong();
        var kept = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1, 96, "Rithmic"); // left working at the broker by the previous run
        kept.ExchangeOrderId = "basket-7";
        var s = new Scripted { TrailingStopTicks = 4 };
        var r = new LiveStrategyRunner(s, _data, broker, ChartPeriod.M1) { ProtectOnStart = true, AdoptProtectionIds = new[] { "basket-7" } };
        r.Start();
        Assert.Same(kept, Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop)); // adopted: no second stop
        Assert.Equal(new[] { "basket-7" }, r.ProtectionOrderIds);
        Assert.Single(r.ActiveOrders, o => o.IsProtective && o.Type == StrategyOrderType.Stop);

        Tick(102); // trailing continues on the adopted order: 102 − 4 ticks
        Assert.Equal(98m, kept.TriggerPrice);
        Assert.Same(kept, Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop));

        s.Next.Enqueue(x => x.DoBuy(1)); // the position changes: the refresh replaces the adopted stop, never adds a second one
        Tick(102);
        Tick(102);
        Assert.Equal(2m, broker.Position!.Volume);
        Assert.True(kept.Canceled);
        var stop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.Equal(2m, stop.Unfilled);
        Assert.DoesNotContain("basket-7", r.ProtectionOrderIds);
    }

    /// <summary>
    /// A broker account that reports its position only when told to, as a real broker can after connecting. Same name and
    /// orders as the simulated account it wraps, but not a <see cref="SimBroker"/>: the runner waits for it to confirm.
    /// </summary>
    internal sealed class SlowAccount : ITradingAccount
    {
        private readonly SimBroker _inner;
        private readonly Position _nothingYet = new();
        private bool _reporting;
        private bool _ordersReporting = true;
        public bool DeferCancels { get; set; }
        public List<Order> PendingCancels { get; } = new();
        public bool PositionReady => _reporting;
        public bool OrdersReady => _ordersReporting;

        public SlowAccount(SimBroker inner)
        {
            _inner = inner;
            inner.Changed += () => Changed?.Invoke();
        }

        public string Name => _inner.Name;
        public bool IsReal => false;
        public Instrument Instrument => _inner.Instrument;
        public Position Position => _reporting ? _inner.Position! : _nothingYet;
        public IEnumerable<Order> ActiveOrders => _ordersReporting ? _inner.ActiveOrders : Array.Empty<Order>();
        public string? LastError => _inner.LastError;
        public event Action? Changed;

        public void ReportPosition()
        {
            _reporting = true;
            Changed?.Invoke();
        }

        public void HideOrders() => _ordersReporting = false;

        public void ReportOrders()
        {
            _ordersReporting = true;
            Changed?.Invoke();
        }

        public void ConfirmCancels()
        {
            foreach (var order in PendingCancels.ToList()) _inner.Cancel(order);
            PendingCancels.Clear();
            Changed?.Invoke();
        }

        public OrderContext OrderContext => _inner.OrderContext;
        public IExecutionSource Executions => _inner.Executions;
        public Order Submit(PreparedOrderIntent intent) => _inner.Submit(intent);
        public Order Replace(Order order, PreparedOrderIntent intent) => _inner.Replace(order, intent);
        public void Cancel(Order order)
        {
            if (DeferCancels) PendingCancels.Add(order);
            else _inner.Cancel(order);
        }
        public void CancelAt(decimal price) => _inner.CancelAt(price);
        public void CancelAll() => _inner.CancelAll();
    }

    [Fact]
    public void Kept_stop_ids_survive_a_stop_before_the_broker_confirms_and_are_adopted_once_it_does()
    {
        var sim = InheritedLong();
        var kept = OrderActions.Manual(sim, OrderTypes.Stop, OrderDirections.Sell, 1, 96, "Rithmic");
        kept.ExchangeOrderId = "basket-7";
        var account = new SlowAccount(sim);
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, account, ChartPeriod.M1) { ProtectOnStart = true, AdoptProtectionIds = new[] { "basket-7" } };
        r.Start();
        Assert.True(r.LogLines.Any(l => l.Contains("waiting for", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
        Assert.Equal(new[] { "basket-7" }, r.Info().ProtectionOrderIds); // a save while it waits does not lose them

        r.Stop(flatten: false, keepProtection: true); // quit before the broker confirmed the position
        Assert.Same(kept, Assert.Single(sim.ActiveOrders)); // untouched
        Assert.Equal(new[] { "basket-7" }, r.ProtectionOrderIds);
        Assert.Equal(new[] { "basket-7" }, LiveStore.InfoFor(r.Key)?.ProtectionOrderIds);

        var r2 = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, account, ChartPeriod.M1)
        {
            ProtectOnStart = true,
            AdoptProtectionIds = LiveStore.InfoFor(r.Key)!.ProtectionOrderIds!,
        };
        r2.Start();
        Assert.Empty(r2.ActiveOrders); // still waiting: nothing placed, nothing adopted
        account.ReportPosition();
        Assert.Same(kept, Assert.Single(sim.ActiveOrders, o => o.Type == OrderTypes.Stop)); // adopted once confirmed, never duplicated
        Assert.Single(r2.ActiveOrders, o => o.IsProtective);
        Assert.Equal(new[] { "basket-7" }, r2.ProtectionOrderIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_stop_cancels_saved_orders_before_position_confirmation(bool flatten)
    {
        var sim = InheritedLong();
        var kept = OrderActions.Manual(sim, OrderTypes.Stop, OrderDirections.Sell, 1, 96, "Rithmic");
        kept.ExchangeOrderId = "saved-stop";
        var account = new SlowAccount(sim) { DeferCancels = true };
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, account, ChartPeriod.M1)
        {
            ProtectOnStart = true, AdoptProtectionIds = new[] { "saved-stop" },
        };
        r.Start();
        r.Stop(flatten, keepProtection: false);
        Assert.Contains(kept, account.PendingCancels);
        Assert.Empty(r.ProtectionOrderIds);
        Assert.Equal(1m, r.OwnPosition);
        Assert.True(r.HasWorkingOrders);
        r.Dispose(); // releasing the stopped runner cannot discard the pending cancel/close acknowledgments
        account.ReportPosition();
        Assert.Equal(1m, sim.Position!.Volume); // readiness alone must not race an unconfirmed cancellation
        Assert.Equal(1m, r.OwnPosition);
        Assert.Equal(flatten, r.IsClosing);
        account.ConfirmCancels();
        Assert.True(kept.Canceled);
        Assert.Equal(flatten ? 0m : 1m, sim.Position!.Volume);
        Assert.Equal(flatten ? 0m : 1m, r.OwnPosition);
        Assert.False(r.IsClosing);
        Assert.Empty(sim.ActiveOrders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Close_before_position_confirmation_preserves_ledger_and_waits_for_actual_close(bool stopRunner)
    {
        var sim = InheritedLong();
        var account = new SlowAccount(sim);
        var r = new LiveStrategyRunner(new Scripted(), _data, account, ChartPeriod.M1);
        r.Start();
        if (stopRunner) r.Stop(flatten: true);
        else r.ClosePosition();
        Assert.True(r.IsClosing);
        Assert.Equal(1m, r.OwnPosition);
        Assert.Equal(1m, LiveStore.LoadLedger(r.Key, 1).Position);
        Assert.Equal(1m, sim.Position!.Volume);
        account.ReportPosition();
        Assert.Equal(0m, sim.Position.Volume);
        Assert.Equal(0m, r.OwnPosition);
        Assert.Equal(0m, LiveStore.LoadLedger(r.Key, 1).Position);
        Assert.False(r.IsClosing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saved_protection_waits_for_order_replay_after_position_replay(bool savedOrderExists)
    {
        var sim = InheritedLong();
        Order? kept = savedOrderExists ? OrderActions.Manual(sim, OrderTypes.Stop, OrderDirections.Sell, 1, 96, "Rithmic") : null;
        if (kept != null) kept.ExchangeOrderId = "saved-stop";
        var account = new SlowAccount(sim);
        account.HideOrders();
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, account, ChartPeriod.M1)
        {
            ProtectOnStart = true, AdoptProtectionIds = new[] { "saved-stop" },
        };
        r.Start();
        account.ReportPosition();
        Assert.Empty(r.ActiveOrders);
        Assert.Equal(new[] { "saved-stop" }, r.ProtectionOrderIds);
        account.ReportOrders();
        var stop = Assert.Single(sim.ActiveOrders);
        if (savedOrderExists) Assert.Same(kept, stop);
        else Assert.Equal(95m, stop.TriggerPrice);
        Assert.Single(r.ActiveOrders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_before_order_replay_cancels_saved_protection_when_it_arrives(bool flatten)
    {
        var sim = InheritedLong();
        var kept = OrderActions.Manual(sim, OrderTypes.Stop, OrderDirections.Sell, 1, 96, "Rithmic");
        kept.ExchangeOrderId = "saved-stop";
        var account = new SlowAccount(sim);
        account.HideOrders();
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, account, ChartPeriod.M1)
        {
            ProtectOnStart = true, AdoptProtectionIds = new[] { "saved-stop" },
        };
        r.Start();
        r.Stop(flatten);
        account.ReportPosition();
        Assert.Equal(1m, r.OwnPosition);
        Assert.Equal(1m, sim.Position!.Volume);
        Assert.Empty(r.ProtectionOrderIds);
        account.ReportOrders();
        Assert.True(kept.Canceled);
        Assert.Equal(flatten ? 0m : 1m, r.OwnPosition);
        Assert.Equal(flatten ? 0m : 1m, sim.Position.Volume);
        Assert.False(r.IsClosing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observed_user_limit_and_runner_stop_cannot_leave_a_reversing_sibling(bool stopFirst)
    {
        var sim = InheritedLong();
        var limit = OrderActions.Manual(sim, OrderTypes.Limit, OrderDirections.Sell, 1, 108, "user target");
        var unrelated = OrderActions.Manual(sim, OrderTypes.Limit, OrderDirections.Buy, 1, 80, "user entry");
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, sim, ChartPeriod.M1) { ProtectOnStart = true };
        r.Start();
        var stop = Assert.Single(sim.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.True(stop.ReduceOnly);
        Assert.Equal(StrategyOrderType.Stop, Assert.Single(r.ActiveOrders).Type); // the user limit is not a runner order
        Tick(stopFirst ? 94 : 109);
        Assert.Equal(0m, r.OwnPosition);
        Assert.Equal(0m, sim.Position!.Volume);
        Assert.True(stopFirst ? limit.Canceled : stop.Canceled);
        Assert.Same(unrelated, Assert.Single(sim.ActiveOrders));
        Tick(stopFirst ? 109 : 94);
        Assert.Equal(0m, sim.Position.Volume);
        Assert.Equal(0m, r.OwnPosition);
    }

    [Fact]
    public void Partial_user_limit_fill_resizes_only_the_remaining_stop_and_preserves_user_ownership()
    {
        var sim = InheritedLong();
        OrderActions.Manual(sim, OrderTypes.Market, OrderDirections.Buy, 1);
        LiveStore.AppendFill($"{typeof(Scripted).FullName}|test:TEST|Cuenta simulada", new LiveFill(T0, true, 1, 100));
        var limit = OrderActions.Manual(sim, OrderTypes.Limit, OrderDirections.Sell, 2, 108, "user target");
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, sim, ChartPeriod.M1) { ProtectOnStart = true };
        r.Start();
        Tick(108); // one unit at the limit, not through it: partial fill
        Assert.Equal(1m, limit.Unfilled);
        Assert.Equal(1m, sim.Position!.Volume);
        Assert.Equal(1m, r.OwnPosition);
        var stop = Assert.Single(sim.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.Equal(1m, stop.Unfilled);
        Assert.Same(limit, Assert.Single(sim.ActiveOrders, o => o.Type == OrderTypes.Limit));
        Assert.Equal(StrategyOrderType.Stop, Assert.Single(r.ActiveOrders).Type); // only its resized stop is owned
        Tick(94);
        Assert.Equal(0m, r.OwnPosition);
        Assert.Equal(0m, sim.Position.Volume);
        Assert.True(limit.Canceled);
        Assert.Empty(sim.ActiveOrders);
    }

    [Fact]
    public void Stop_fill_resizes_a_user_limit_for_the_accounts_other_position_without_claiming_it()
    {
        var sim = InheritedLong();
        OrderActions.Manual(sim, OrderTypes.Market, OrderDirections.Buy, 1, comment: "user position"); // not in this strategy's ledger
        var limit = OrderActions.Manual(sim, OrderTypes.Limit, OrderDirections.Sell, 2, 108, "user target");
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, sim, ChartPeriod.M1) { ProtectOnStart = true };
        r.Start();
        Tick(94);
        Assert.Equal(0m, r.OwnPosition);
        Assert.Equal(1m, sim.Position!.Volume);
        Assert.False(limit.Canceled);
        Assert.Equal(1m, limit.Unfilled);
        Assert.Same(limit, Assert.Single(sim.ActiveOrders));
        Assert.Empty(r.ActiveOrders); // the user's remaining exit has not become a strategy order
        Tick(109);
        Assert.Equal(0m, sim.Position.Volume);
        Assert.Equal(0m, r.OwnPosition);
        Assert.Single(r.Ledger.Trades);
    }

    [Fact]
    public void A_confirmed_flat_snapshot_can_settle_an_unresolved_close_without_sending_an_order()
    {
        var sim = InheritedLong();
        OrderActions.Flatten(sim);
        int orders = sim.Orders.Count;
        var account = new SlowAccount(sim);
        var runner = new LiveStrategyRunner(new Scripted(), _data, account, ChartPeriod.M1);
        runner.Start();
        runner.Stop(flatten: true);
        Assert.Equal(1m, runner.OwnPosition);
        Assert.True(runner.IsClosing);
        account.ReportPosition();
        Assert.Equal(0m, runner.OwnPosition);
        Assert.False(runner.IsClosing);
        Assert.Equal(orders, sim.Orders.Count);
        Assert.Equal(0m, LiveStore.LoadLedger(runner.Key, 1).Position);
    }

    [Fact]
    public async Task Rithmic_close_before_position_snapshot_waits_and_closes_the_confirmed_position()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { PositionSnapshotRelease = release };
        server.SeedPosition(true, 1, 100);
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var data = new InstrumentData(new Instrument { Symbol = "ES", FeedId = "rithmic", TickSize = 1, VolumeStep = 1, PointValue = 1 }, new SimulatedFeed());
        using var orders = new NativeOrderTestFixture();
        using var broker = orders.Rithmic(data, session, "ACC1", "ACC1", _ => Task.FromResult(("ESZ6", "CME")));
        using var runner = new LiveStrategyRunner(new Scripted(), data, broker, ChartPeriod.M1) { Persist = false };
        runner.SeedPosition(new LiveFill(T0, true, 1, 100));
        runner.Start();
        session.Start();
        await AwaitBroker(() => session.IsConnected);
        runner.Stop(flatten: true);
        Assert.True(runner.IsClosing);
        Assert.Equal(1m, runner.OwnPosition);
        Assert.Equal(1, server.Position);
        lock (server.Tags) Assert.Empty(server.Tags);
        release.SetResult();
        await AwaitBroker(() => !runner.IsClosing && runner.OwnPosition == 0 && broker.Position.Volume == 0);
        Assert.Equal(0, server.Position);
        lock (server.Tags) Assert.Single(server.Tags);
    }

    [Fact]
    public void A_stop_with_an_unconfirmed_cancel_still_blocks_duplicate_inherited_protection()
    {
        var sim = InheritedLong();
        var account = new SlowAccount(sim) { DeferCancels = true };
        account.ReportPosition();
        var first = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, account, ChartPeriod.M1) { ProtectOnStart = true };
        first.Start();
        var stop = Assert.Single(sim.ActiveOrders);
        stop.ExchangeOrderId = "cancel-pending";
        first.Stop(flatten: false);
        Assert.True(first.HasWorkingOrders);
        var second = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, account, ChartPeriod.M1)
        {
            ProtectOnStart = true, AdoptProtectionIds = new[] { "cancel-pending" },
        };
        second.Start();
        Assert.Same(stop, Assert.Single(sim.ActiveOrders));
        Assert.Empty(second.ActiveOrders); // neither adopted while being canceled nor duplicated
        second.Stop(flatten: false);
        account.ConfirmCancels();
        Assert.Empty(sim.ActiveOrders);
        Assert.False(first.HasWorkingOrders);
        Assert.False(second.HasWorkingOrders);
    }

    [Fact]
    public void Explicit_stop_also_cancels_identified_orphaned_protection_after_a_confirmed_flat_snapshot()
    {
        var sim = InheritedLong();
        var stop = OrderActions.Manual(sim, OrderTypes.Stop, OrderDirections.Sell, 1, 96, "Rithmic");
        stop.ExchangeOrderId = "orphan-stop";
        OrderActions.Manual(sim, OrderTypes.Market, OrderDirections.Sell, 1, comment: "manual close");
        var runner = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, sim, ChartPeriod.M1)
        {
            ProtectOnStart = true, AdoptProtectionIds = new[] { "orphan-stop" },
        };
        runner.Start();
        Assert.Equal(0m, runner.OwnPosition);
        Assert.Same(stop, Assert.Single(sim.ActiveOrders));
        runner.Stop(flatten: false);
        Assert.True(stop.Canceled);
        Assert.Empty(runner.ProtectionOrderIds);
        Assert.Empty(sim.ActiveOrders);
    }

    private static async Task AwaitBroker(Func<bool> condition, Func<string>? state = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        bool settled = condition();
        Assert.True(settled, settled ? null : "The fake broker did not publish the expected account/order state. " + state?.Invoke());
    }

    [Fact]
    public async Task Rithmic_position_replay_does_not_replace_a_saved_stop_before_order_replay_completes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { OrderReplayRelease = release };
        server.SeedPosition(true, 1, 100);
        await server.PushOrder("SAVED", "previous-session", false, 1, 96, type: 4);
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var data = new InstrumentData(new Instrument { Symbol = "ES", FeedId = "rithmic", TickSize = 1, VolumeStep = 1, PointValue = 1 }, new SimulatedFeed());
        using var broker = new RithmicBroker(data, session, "ACC1", "ACC1", _ => Task.FromResult(("ESZ6", "CME")));
        using var runner = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, data, broker, ChartPeriod.M1)
        {
            Persist = false, ProtectOnStart = true, AdoptProtectionIds = new[] { "SAVED" },
        };
        runner.SeedPosition(new LiveFill(T0, true, 1, 100));
        runner.Start();
        session.Start();
        await server.OrderReplayRequested.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await AwaitBroker(() => broker.Position.Volume == 1);
        Assert.Empty(runner.ActiveOrders);
        Assert.Empty(broker.ActiveOrders);
        Assert.Equal(new[] { "SAVED" }, runner.ProtectionOrderIds);
        release.SetResult();
        await AwaitBroker(() => runner.ActiveOrders.Count == 1);
        var stop = Assert.Single(broker.ActiveOrders);
        Assert.Equal("SAVED", stop.ExchangeOrderId);
        Assert.Equal(96m, stop.TriggerPrice);
        var adopted = Assert.Single(runner.ActiveOrders);
        Assert.Equal(StrategyOrderType.Stop, adopted.Type);
        Assert.Equal(stop.TriggerPrice, adopted.Price);
        Assert.Equal(stop.Unfilled, adopted.Quantity);
        Assert.Equal(new[] { stop.ExchangeOrderId }, runner.ProtectionOrderIds);
        lock (server.Tags) Assert.Empty(server.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rithmic_partial_stop_and_user_limit_fills_resize_and_cancel_siblings_without_adopting_user_orders(bool stopFirst)
    {
        using var server = new FakeRithmicServer();
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("USER", "user-session", false, 2, 108);
        using var session = new RithmicTradingSession(new RithmicSettings
        {
            Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000,
        });
        var data = new InstrumentData(new Instrument { Symbol = "ES", FeedId = "rithmic", TickSize = 1, VolumeStep = 1, PointValue = 1 }, new SimulatedFeed());
        using var orders = new NativeOrderTestFixture();
        using var broker = orders.Rithmic(data, session, "ACC1", "ACC1", _ => Task.FromResult(("ESZ6", "CME")));
        using var runner = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, data, broker, ChartPeriod.M1)
        {
            Persist = false, ProtectOnStart = true,
        };
        runner.SeedPosition(new LiveFill(T0, true, 2, 100));
        session.Start();
        await AwaitBroker(() => broker.Position.Volume == 2 && broker.ActiveOrders.Any(o => o.ExchangeOrderId == "USER"));
        runner.Start();
        await AwaitBroker(() => broker.ActiveOrders.Any(o => o.Type == OrderTypes.Stop && o.ExchangeOrderId.Length > 0));
        var userLimit = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Limit);
        var firstStop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        string NativeState() => $"owned={runner.OwnPosition}; virtual={runner.VirtualPosition}; account={broker.Position.Volume}; server={server.Position}; " +
            $"user={userLimit.QuantityToFill}/{userLimit.Filled}/{userLimit.Unfilled}/{userLimit.State}; brokerError={broker.LastError}; ownershipError={broker.OrderContext.LastError}; " +
            $"orders=[{string.Join(" | ", broker.ActiveOrders.Select(order => $"{order.ExchangeOrderId}:{order.Type}:{order.QuantityToFill}/{order.Filled}/{order.Unfilled}:{order.State}"))}]; " +
            $"runner=[{string.Join(" | ", runner.LogLines.TakeLast(12))}]";
        await server.FillOrder(stopFirst ? firstStop.ExchangeOrderId : "USER", 1, stopFirst ? 95 : 108);
        await AwaitBroker(() => runner.OwnPosition == 1 && broker.Position.Volume == 1 &&
            userLimit.Unfilled == 1 && broker.ActiveOrders.Any(o => o.Type == OrderTypes.Stop && o.Unfilled == 1 && o.ExchangeOrderId.Length > 0), NativeState);
        Assert.Equal(StrategyOrderType.Stop, Assert.Single(runner.ActiveOrders).Type); // the user limit remains unowned
        Assert.Equal(1m, userLimit.Unfilled);
        var remainingStop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        await server.FillOrder(stopFirst ? "USER" : remainingStop.ExchangeOrderId, 1, stopFirst ? 108 : 95);
        await AwaitBroker(() => runner.OwnPosition == 0 && broker.Position.Volume == 0 && !broker.ActiveOrders.Any(), NativeState);
        Assert.Equal(0, server.Position);
        Assert.Equal(0m, runner.OwnPosition);
        Assert.True(stopFirst ? remainingStop.Canceled : userLimit.Canceled);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public void An_adopted_stop_larger_than_the_position_is_replaced_with_the_right_size()
    {
        var broker = InheritedLong();
        var kept = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 2, 96, "Tradovate"); // filling it would reverse the long 1 position
        kept.ExchangeOrderId = "9001";
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true, AdoptProtectionIds = new[] { "9001" } };
        r.Start();
        Assert.True(kept.Canceled);
        var stop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.Equal(1m, stop.Unfilled);
        Assert.True(r.LogLines.Any(l => l.Contains("size", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
    }

    [Fact]
    public void An_adopted_stop_stays_its_stop_loss_and_a_users_closing_limit_still_leaves_out_the_take_profit()
    {
        var broker = InheritedLong();
        var kept = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1, 96, "Rithmic"); // its stop loss, kept at the broker by the previous run
        kept.ExchangeOrderId = "basket-7";
        var limit = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 1, 108, "Rithmic"); // the user's own take profit
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true, AdoptProtectionIds = new[] { "basket-7" } };
        r.Start();
        Assert.Equal(new[] { kept, limit }, broker.ActiveOrders); // the kept stop is adopted: no second stop and no extra target
        Assert.Equal(new[] { "basket-7" }, r.ProtectionOrderIds);
        Assert.Equal(StrategyOrderType.Stop, Assert.Single(r.ActiveOrders, o => o.IsProtective).Type);
        Assert.True(r.LogLines.Any(l => l.Contains("Adopted the stop loss", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
        Assert.True(r.LogLines.Any(l => l.Contains("no additional take profit", StringComparison.Ordinal) && l.Contains("Chart Trader", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void After_adopting_a_kept_order_it_places_the_stop_loss_or_take_profit_it_still_lacks(bool keptStop)
    {
        // stop first: a kept take profit whose stop is gone (canceled at the broker) never leaves the position without a stop
        var broker = InheritedLong();
        var kept = keptStop ? OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1, 96, "Rithmic") : OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 1, 108, "Rithmic");
        kept.ExchangeOrderId = "basket-7";
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, broker, ChartPeriod.M1)
        {
            ProtectOnStart = true,
            AdoptProtectionIds = new[] { "basket-7", "basket-8" }, // the other order it kept is no longer working
        };
        r.Start();
        var stop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        var target = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Limit);
        Assert.Same(kept, keptStop ? stop : target); // adopted, not duplicated
        Assert.Equal(keptStop ? 96m : 95m, stop.TriggerPrice);
        Assert.Equal(keptStop ? 110m : 108m, target.Price);
        Assert.Equal(2, r.ActiveOrders.Count(o => o.IsProtective));
        Assert.Equal(2, r.ProtectionOrderIds.Count);
    }

    [Fact]
    public void An_adopted_stop_of_the_wrong_size_is_replaced_but_no_take_profit_is_added_next_to_a_users_closing_limit()
    {
        var broker = InheritedLong();
        var kept = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 2, 96, "Tradovate"); // filling it would reverse the long 1 position
        kept.ExchangeOrderId = "9001";
        var limit = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, 1, 108, "Tradovate"); // the user's own take profit
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true, AdoptProtectionIds = new[] { "9001" } };
        r.Start();
        Assert.True(kept.Canceled);
        var stop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.Equal(1m, stop.Unfilled);
        Assert.Same(limit, Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Limit));
        Assert.True(r.LogLines.Any(l => l.Contains("no additional take profit", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
    }

    [Fact]
    public void An_adopted_stop_of_the_wrong_size_is_left_for_review_while_another_closing_stop_is_working()
    {
        // nothing is placed next to a stop it did not adopt, not even a replacement: both are logged for review
        var broker = InheritedLong();
        var kept = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 2, 96, "Tradovate");
        kept.ExchangeOrderId = "9001";
        var other = OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, 1, 94, "Tradovate");
        var r = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true, AdoptProtectionIds = new[] { "9001" } };
        r.Start();
        Assert.Equal(new[] { kept, other }, broker.ActiveOrders);
        Assert.Equal(new[] { "9001" }, r.ProtectionOrderIds);
        Assert.True(r.LogLines.Any(l => l.Contains("no additional stop or target", StringComparison.Ordinal) && l.Contains("Chart Trader", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
        Assert.True(r.LogLines.Any(l => l.Contains("does not match the position", StringComparison.Ordinal) && l.Contains("Chart Trader", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
    }

    [Fact]
    public void Stopping_with_keep_protection_leaves_stop_and_target_working_records_their_ids_and_a_new_run_adopts_them()
    {
        var (s, broker, r) = Setup();
        s.StopLossTicks = 5;
        s.TakeProfitTicks = 10;
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        var stop = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        var target = Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Limit);
        var ids = r.ProtectionOrderIds.ToList();
        Assert.Equal(2, ids.Count);

        r.Stop(flatten: false, keepProtection: true);
        Assert.False(r.IsRunning);
        Assert.Equal(new[] { stop, target }, broker.ActiveOrders); // both still working at the broker
        Assert.Equal(1m, broker.Position!.Volume);
        Assert.Equal(ids, r.ProtectionOrderIds); // exposed for the next run
        Assert.Equal(ids, r.Info().ProtectionOrderIds);
        Assert.Equal(ids, LiveStore.InfoFor(r.Key)?.ProtectionOrderIds); // and recorded in its saved state

        var r2 = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, broker, ChartPeriod.M1)
        {
            ProtectOnStart = true,
            AdoptProtectionIds = LiveStore.InfoFor(r.Key)!.ProtectionOrderIds!,
        };
        r2.Start();
        Assert.Equal(new[] { stop, target }, broker.ActiveOrders); // adopted: nothing new placed
        Assert.Equal(ids, r2.ProtectionOrderIds);
        Assert.Equal(2, r2.ActiveOrders.Count(o => o.IsProtective));
    }

    [Fact]
    public void A_normal_stop_still_cancels_stop_and_target_and_their_old_ids_do_not_block_new_protection()
    {
        var (s, broker, r) = Setup();
        s.StopLossTicks = 5;
        s.TakeProfitTicks = 10;
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        var old = broker.ActiveOrders.ToList();
        Assert.Equal(2, old.Count);
        var ids = r.ProtectionOrderIds.ToList(); // what the periodic save records while it runs

        r.Stop(flatten: false);
        Assert.Empty(broker.ActiveOrders);
        Assert.Equal(1m, broker.Position!.Volume); // no liquidation
        Assert.Empty(r.ProtectionOrderIds);
        Assert.Null(r.Info().ProtectionOrderIds);

        // a restart with the ids saved before that stop finds nothing to adopt and protects the position again
        var r2 = new LiveStrategyRunner(new Scripted { StopLossTicks = 5, TakeProfitTicks = 10 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true, AdoptProtectionIds = ids };
        r2.Start();
        Assert.Equal(2, broker.ActiveOrders.Count());
        Assert.DoesNotContain(broker.ActiveOrders, old.Contains);
    }

    [Fact]
    public void Closing_the_position_on_stop_never_keeps_its_stop_or_target()
    {
        var (s, broker, r) = Setup();
        s.StopLossTicks = 5;
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        r.Stop(flatten: true, keepProtection: true); // once flat, a working stop could open a new position
        Assert.Empty(broker.ActiveOrders);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Empty(r.ProtectionOrderIds);
    }

    [Fact]
    public void A_kept_stop_for_a_position_the_account_no_longer_has_is_reported_and_not_adopted()
    {
        // same process on the simulated account: the stop was kept, then the position was closed by hand
        var (s, broker, r) = Setup();
        s.StopLossTicks = 5;
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        r.Stop(flatten: false, keepProtection: true);
        var kept = Assert.Single(broker.ActiveOrders);
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Sell, 1, comment: "manual close");
        Assert.Equal(0m, broker.Position!.Volume);

        var r2 = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true, AdoptProtectionIds = r.ProtectionOrderIds.ToList() };
        r2.Start();
        Assert.Equal(0m, r2.Ledger.Position); // closed in its ledger: the account does not have it
        Assert.Same(kept, Assert.Single(broker.ActiveOrders)); // nothing new placed; the kept stop is left for the user to review
        Assert.Empty(r2.ActiveOrders);
        Assert.True(r2.LogLines.Any(l => l.Contains("no longer has the position", StringComparison.Ordinal) && l.Contains("Chart Trader", StringComparison.Ordinal)), string.Join(" | ", r2.LogLines));
    }

    [Fact]
    public void A_runner_reports_unreadable_lines_in_its_ledger_and_keeps_the_fills_after_them()
    {
        var key = $"{typeof(Scripted).FullName}|test:TEST|Cuenta simulada";
        string file = LiveStore.LedgerFile(key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(new LiveFill(T0.AddDays(-1), false, 2, 100)) + "\n{\"Time\":\n"
                                + JsonSerializer.Serialize(new LiveFill(T0.AddDays(-1).AddMinutes(5), true, 2, 99)) + "\n");
        var (_, _, r) = Setup();
        Assert.Equal(0m, r.Ledger.Position); // the closing fill after the bad line was read
        Assert.Equal(1, r.Ledger.SkippedLines);
        Assert.True(r.LogLines.Any(l => l.Contains("unreadable", StringComparison.Ordinal)), string.Join(" | ", r.LogLines));
        Assert.DoesNotContain(r.LogLines, l => l.Contains("does not have", StringComparison.Ordinal)); // no position invented by a cut-off read
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
        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1, comment: "historia");
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
