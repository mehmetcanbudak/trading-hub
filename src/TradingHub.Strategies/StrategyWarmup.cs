using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>
/// Calentamiento de una estrategia: cuántas velas tarda en tener listos (IsReady) los indicadores que crea en OnInit, y
/// cuáles son (con su período). No cuenta lo que lea a mano de Bars ni los indicadores que cree fuera de OnInit.
/// </summary>
public sealed record StrategyWarmup(int Bars, IReadOnlyList<string> Indicators, bool Complete, string? Error)
{
    /// <summary>Tope de velas que se prueban (más que esto queda como incompleto).</summary>
    public const int MaxBars = 5000;

    /// <summary>
    /// Arranca la estrategia sin operar (solo OnInit) y le pasa velas inventadas a sus indicadores hasta que están todos
    /// listos. Con <paramref name="parameters"/> mide con esos valores; los que falten quedan por defecto.
    /// </summary>
    public static StrategyWarmup Measure(Type type, IReadOnlyDictionary<string, object?>? parameters = null, string period = "M15")
    {
        var names = new List<string>();
        int bars = 0;
        Strategy? strategy = null;
        try
        {
            var s = (Strategy)Activator.CreateInstance(type)!;
            strategy = s;
            if (parameters != null) s.SetParameters(parameters);
            var host = new ProbeHost(ChartPeriod.Parse(period));
            s.InternalInit(host);
            var indicators = s.Indicators.ToList();
            names.AddRange(indicators.Select(i => i.Name));
            while (bars < MaxBars && !indicators.All(i => i.IsReady))
            {
                host.Next();
                bars++;
                foreach (var i in indicators) i.Update(host.Bars);
            }

            return new StrategyWarmup(bars, names, indicators.All(i => i.IsReady), null);
        }
        catch (Exception ex)
        {
            return new StrategyWarmup(bars, names, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            strategy?.DisposeIndicators();
        }
    }

    /// <summary>Entorno mínimo para medir: velas que suben y bajan, sin órdenes.</summary>
    private sealed class ProbeHost(ChartPeriod period) : IStrategyHost
    {
        private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly BarSeries _bars = new();
        private DateTime _time = T0;

        public Instrument Instrument { get; } = new() { Symbol = "PRUEBA", FeedId = "calentamiento", TickSize = 0.25m, VolumeStep = 1m, PointValue = 1m };
        public ChartPeriod Period => period;
        public BarSeries Bars => _bars;
        public Bar? FormingBar => null;
        public DateTime Time => _time;
        public decimal LastPrice => _bars.Count > 0 ? _bars[0].Close : 100;
        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
        public SessionSettings Session { get; } = new();
        public decimal Position => 0;
        public decimal AveragePrice => 0;
        public decimal OpenPnL => 0;
        public decimal RealizedPnL => 0;
        public IReadOnlyList<StrategyOrder> ActiveOrders => Array.Empty<StrategyOrder>();
        public bool IsBacktest => true;

        /// <summary>Agrega una vela (precios que oscilan, con volumen de los dos lados).</summary>
        public void Next()
        {
            var step = period.IsTimeBased ? period.ApproxDuration : TimeSpan.FromMinutes(1);
            int n = _bars.Count;
            decimal open = 100 + n % 7 - n % 3;
            _bars.Add(new Bar
            {
                Index = n, Time = _time, CloseTime = _time + step, Open = open, High = open + 2, Low = open - 2, Close = open + (n % 2 == 0 ? 1 : -1),
                Volume = 10, BuyVolume = n % 2 == 0 ? 6 : 4, SellVolume = n % 2 == 0 ? 4 : 6, Trades = 10, PocPrice = open,
            });
            _time += step;
        }

        public StrategyOrder Submit(OrderSide side, StrategyOrderType type, decimal quantity, decimal price, string name) =>
            new() { Side = side, Type = type, Quantity = quantity, Price = price, Name = name, State = StrategyOrderState.Canceled, Time = _time };

        public void Cancel(StrategyOrder order)
        {
        }

        public void Modify(StrategyOrder order, decimal price)
        {
        }

        public void Log(string message)
        {
        }
    }
}
