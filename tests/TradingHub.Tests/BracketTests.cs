using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

/// <summary>Stop loss, objetivos parciales, breakeven y OCO sobre la cuenta simulada.</summary>
public class BracketTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m };
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        private long _id = 1;
        private int _sec;
        public readonly InstrumentData Data = new(Inst, new SimulatedFeed());
        public readonly SimBroker Broker;
        public readonly BracketManager Brackets;
        public readonly List<string> Messages = new();

        public Rig(decimal price = 100)
        {
            Data.Trades.Add(new Trade(_id++, T0, price, 1, TradeSide.Buy));
            Broker = new SimBroker(Data, new Portfolio(), new TradingStatistics());
            Brackets = new BracketManager(Broker);
            Brackets.Message += Messages.Add;
        }

        /// <summary>Opera un precio (como si llegara del mercado).</summary>
        public void Trade(decimal price, decimal vol = 1)
        {
            int start = Data.Trades.Count;
            Data.Trades.Add(new Trade(_id++, T0.AddSeconds(++_sec), price, vol, TradeSide.Buy));
            var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ((Action<int, int>?)field!.GetValue(Data))?.Invoke(start, 1);
        }

        public decimal Position => Broker.Position!.Volume;
    }

    [Fact]
    public void Stop_y_objetivo_desde_la_posicion_y_oco_al_llenarse_el_objetivo()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Buy, 2);
        Assert.Equal(2m, r.Position);

        // hacia la pérdida = stop, hacia la ganancia = objetivo
        Assert.StartsWith("Stop loss", r.Brackets.DropFromPosition(90));
        Assert.StartsWith("Target", r.Brackets.DropFromPosition(110));
        Assert.Equal(BracketRole.StopLoss, r.Brackets.RoleOf(r.Brackets.StopLoss!));
        Assert.Single(r.Brackets.Targets);
        Assert.Equal(2m, r.Brackets.Targets[0].Unfilled);

        // el objetivo se llena: la posición queda plana y el stop se cancela solo
        r.Trade(111);
        Assert.Equal(0m, r.Position);
        Assert.Empty(r.Broker.ActiveOrders);
        Assert.Contains(r.Messages, m => m.Contains("canceled"));
    }

    [Fact]
    public void Lados_equivocados_se_rechazan()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Sell, 1);
        Assert.Contains("above", r.Brackets.SetStopLoss(95));
        Assert.Contains("below", r.Brackets.AddTarget(105));
        Assert.Empty(r.Broker.ActiveOrders);
    }

    [Fact]
    public void Parciales_achican_el_stop_y_mueven_a_breakeven()
    {
        var r = new Rig();
        r.Brackets.Template = new BracketTemplate
        {
            Enabled = true,
            StopTicks = 10,
            Targets = { },
            BreakevenAfterFirstTarget = true,
        };
        r.Brackets.Template.Targets.Clear();
        r.Brackets.Template.Targets.Add(new BracketTarget { Ticks = 5, Percent = 50 });
        r.Brackets.Template.Targets.Add(new BracketTarget { Ticks = 15, Percent = 50 });

        r.Broker.Market(OrderDirections.Buy, 4); // entra a 101 (último + 1 tick, sin libro)
        decimal entry = r.Broker.Position!.AveragePrice;
        Assert.Equal(4m, r.Position);
        Assert.Equal(4m, r.Brackets.StopLoss!.Unfilled);
        Assert.Equal(entry - 10, r.Brackets.StopLoss!.TriggerPrice);
        Assert.Equal(2, r.Brackets.Targets.Count);
        Assert.Equal(4m, r.Brackets.Targets.Sum(t => t.Unfilled));

        // objetivo 1 (la mitad): el stop baja a 2 y pasa a breakeven
        r.Trade(entry + 6);
        Assert.Equal(2m, r.Position);
        Assert.Equal(2m, r.Brackets.StopLoss!.Unfilled);
        Assert.Equal(entry, r.Brackets.StopLoss!.TriggerPrice);
        Assert.Single(r.Brackets.Targets);

        // vuelve a la entrada: sale en breakeven y no queda nada colgado
        r.Trade(entry);
        Assert.Equal(0m, r.Position);
        Assert.Empty(r.Broker.ActiveOrders);
    }

    [Fact]
    public void Cerrar_una_parte_a_mano_ajusta_stop_y_objetivos()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Buy, 4);
        r.Brackets.SetStopLoss(90);
        r.Brackets.AddTarget(120);
        r.Brackets.ClosePartial(0.5m);
        Assert.Equal(2m, r.Position);
        Assert.Equal(2m, r.Brackets.StopLoss!.Unfilled);
        Assert.Equal(2m, r.Brackets.Targets.Sum(t => t.Unfilled));
    }

    [Fact]
    public void Segundo_objetivo_divide_al_primero_en_parciales()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Buy, 4);
        r.Brackets.AddTarget(110);
        r.Brackets.AddTarget(120);
        var targets = r.Brackets.Targets.OrderBy(t => t.Price).ToList();
        Assert.Equal(2, targets.Count);
        Assert.Equal(2m, targets[0].Unfilled);
        Assert.Equal(2m, targets[1].Unfilled);
        Assert.Equal(1, r.Brackets.TargetNumber(targets[0]));
        Assert.Equal(2, r.Brackets.TargetNumber(targets[1]));

        // cambiar la cantidad de uno achica al otro para no pasarse de la posición
        r.Brackets.SetTargetFraction(targets[0], 0.75m);
        Assert.Equal(4m, r.Brackets.Targets.Sum(t => t.Unfilled));
        Assert.Equal(3m, r.Brackets.Targets.First(t => t.Price == 110).Unfilled);
    }

    [Fact]
    public void Orden_pendiente_con_stop_y_objetivo_adjuntos_se_activa_al_llenarse()
    {
        var r = new Rig();
        var entry = r.Broker.Limit(OrderDirections.Buy, 2, 95);
        Assert.Contains("Stop attached", r.Brackets.AttachToEntry(entry, 90));
        Assert.Contains("Target attached", r.Brackets.AttachToEntry(entry, 105));
        var pb = r.Brackets.PendingFor(entry)!;
        Assert.Equal(5, pb.StopTicks);
        Assert.Equal(10, pb.Targets[0].Ticks);

        // mover la entrada mueve también el stop y el objetivo (se guardan en ticks)
        r.Brackets.Move(entry, 96);
        Assert.Equal(96m, r.Brackets.PendingFor(entry)!.EntryPrice);

        r.Trade(95); // atraviesa el límite de 96
        Assert.Equal(2m, r.Position);
        Assert.Equal(91m, r.Brackets.StopLoss!.TriggerPrice);
        Assert.Equal(106m, r.Brackets.Targets.Single().Price);
        Assert.Empty(r.Brackets.Pending);
    }

    [Fact]
    public void Los_stops_nunca_dan_vuelta_la_posicion()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Buy, 1);
        var exit = ((ITradingAccount)r.Broker).Exit(OrderTypes.Stop, OrderDirections.Sell, 5, 95);
        r.Trade(94);
        Assert.Equal(0m, r.Position); // vende solo lo que había, no queda corto
        Assert.NotEqual(OrderStates.Active, exit.State);
    }
}
