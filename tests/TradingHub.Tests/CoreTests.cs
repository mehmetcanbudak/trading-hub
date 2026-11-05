using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

public class CoreTests
{
    private static readonly Instrument Btc = new() { Symbol = "TEST", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m };
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TradeStore Store(params (int Sec, decimal Price, decimal Vol, TradeSide Side)[] trades)
    {
        var s = new TradeStore(Btc);
        long id = 1;
        foreach (var (sec, p, v, side) in trades) s.Add(new Trade(id++, T0.AddSeconds(sec), p, v, side));
        return s;
    }

    [Fact]
    public void Velas_de_un_minuto_acumulan_bid_ask_y_delta_por_nivel()
    {
        var store = Store((0, 100.0m, 1, TradeSide.Buy), (10, 100.1m, 2, TradeSide.Sell), (59, 100.0m, 0.5m, TradeSide.Buy), (61, 101.0m, 1, TradeSide.Buy));
        var series = new CandleSeries(Btc, ChartPeriod.M1);
        series.Rebuild(store);
        Assert.Equal(2, series.Count);
        var c = series[0];
        Assert.Equal(3.5m, c.Volume);
        Assert.Equal(1.5m, c.Ask);
        Assert.Equal(2m, c.Bid);
        Assert.Equal(-0.5m, c.Delta);
        Assert.Equal(1000, c.OpenTicks);
        Assert.Equal(1001, c.HighTicks);
        var lvl = c.Levels.Get(series.RowOf(1000));
        Assert.Equal(1.5m, lvl.Ask);
        Assert.Equal(series.RowOf(1001), c.PocRow); // 2 de volumen en 100.1
    }

    [Fact]
    public void Velas_de_volumen_parten_las_operaciones_grandes()
    {
        var store = Store((0, 100m, 3, TradeSide.Buy), (1, 100m, 4, TradeSide.Sell));
        var series = new CandleSeries(Btc, new ChartPeriod(PeriodType.Volume, 5));
        series.Rebuild(store);
        Assert.Equal(2, series.Count);
        Assert.Equal(5m, series[0].Volume);
        Assert.Equal(2m, series[1].Volume);
    }

    [Fact]
    public void Archivo_de_ticks_guarda_y_lee_igual()
    {
        var path = Path.Combine(Path.GetTempPath(), "tradinghub_test_" + Guid.NewGuid().ToString("N") + ".vtk");
        try
        {
            var trades = Enumerable.Range(0, 1000).Select(i => new Trade(10_000 + i, T0.AddMilliseconds(i * 37), 86000m + (i % 17) * 0.1m, 0.001m * (1 + i % 50), i % 3 == 0 ? TradeSide.Sell : TradeSide.Buy)).ToList();
            using (var w = new TickFile.Writer(path, Btc))
                foreach (var t in trades) w.Write(t);
            var back = TickFile.Read(path);
            Assert.Equal(trades.Count, back.Count);
            for (int i = 0; i < trades.Count; i++)
            {
                Assert.Equal(trades[i].Id, back[i].Id);
                Assert.Equal(trades[i].Price, back[i].Price);
                Assert.Equal(trades[i].Volume, back[i].Volume);
                Assert.Equal(trades[i].Side, back[i].Side);
                Assert.Equal(trades[i].Time, back[i].Time);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Broker_simulado_llena_limite_cuando_el_precio_la_atraviesa_y_calcula_pnl()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        data.Trades.Add(new Trade(1, T0, 100m, 1, TradeSide.Buy));
        var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());
        var order = OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, 2, 99.5m);
        Assert.Equal(OrderStates.Active, order.State);

        // operación a 99.4 (atraviesa el límite)
        data.Trades.Add(new Trade(2, T0.AddSeconds(1), 99.4m, 1, TradeSide.Sell));
        typeof(InstrumentData).GetEvent("TradesAppended")!.GetRaiseMethod(true)?.Invoke(data, new object[] { 1, 1 });
        InvokeAppended(data, 1, 1);
        Assert.Equal(OrderStates.Done, order.State);
        Assert.Equal(2m, broker.Position!.Volume);
        Assert.Equal(99.5m, broker.Position.AveragePrice);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Sell, 2); // sin libro: al último precio menos un tick
        Assert.Equal(0m, broker.Position.Volume);
        Assert.Equal((99.3m - 99.5m) * 2, broker.Position.RealizedPnL);
    }

    private static void InvokeAppended(InstrumentData data, int start, int count)
    {
        var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var del = (Action<int, int>?)field!.GetValue(data);
        del?.Invoke(start, count);
    }
}
