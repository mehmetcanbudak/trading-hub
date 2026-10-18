using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>Una vela cerrada (o en formación) con su order flow.</summary>
public sealed class Bar
{
    public int Index { get; internal set; }
    public DateTime Time { get; internal set; }
    public DateTime CloseTime { get; internal set; }
    public decimal Open { get; internal set; }
    public decimal High { get; internal set; }
    public decimal Low { get; internal set; }
    public decimal Close { get; internal set; }
    public decimal Volume { get; internal set; }

    /// <summary>Volumen de compradores agresivos (pegaron al ask).</summary>
    public decimal BuyVolume { get; internal set; }

    /// <summary>Volumen de vendedores agresivos (pegaron al bid).</summary>
    public decimal SellVolume { get; internal set; }

    public decimal Delta => BuyVolume - SellVolume;
    public decimal MaxDelta { get; internal set; }
    public decimal MinDelta { get; internal set; }
    public int Trades { get; internal set; }

    /// <summary>Precio con más volumen de la vela (POC).</summary>
    public decimal PocPrice { get; internal set; }

    /// <summary>Volumen por precio (solo si la estrategia activa <c>UseFootprint</c>).</summary>
    public IReadOnlyDictionary<decimal, (decimal Buy, decimal Sell)> Levels => _levels ?? Empty;

    public bool IsBullish => Close > Open;
    public bool IsBearish => Close < Open;
    public decimal Range => High - Low;
    public decimal Body => Math.Abs(Close - Open);
    public decimal Typical => (High + Low + Close) / 3;

    internal Dictionary<decimal, (decimal Buy, decimal Sell)>? _levels;
    private static readonly Dictionary<decimal, (decimal, decimal)> Empty = new();

    public override string ToString() => $"#{Index} {Time:yyyy-MM-dd HH:mm} O {Open} H {High} L {Low} C {Close} V {Volume} Δ {Delta}";
}

/// <summary>Velas cerradas. El índice cuenta hacia atrás: <c>Bars[0]</c> es la última cerrada.</summary>
public sealed class BarSeries
{
    private readonly List<Bar> _bars = new();

    public int Count => _bars.Count;

    /// <summary>Vela de hace <paramref name="ago"/> velas (0 = la última cerrada).</summary>
    public Bar this[int ago] => _bars[_bars.Count - 1 - Math.Clamp(ago, 0, _bars.Count - 1)];

    /// <summary>Vela por número absoluto (0 = la primera).</summary>
    public Bar At(int index) => _bars[index];

    public IReadOnlyList<Bar> All => _bars;

    internal void Add(Bar b) => _bars.Add(b);
}

/// <summary>Arma velas a partir de operaciones (tiempo, ticks, volumen, rango o delta).</summary>
internal sealed class BarBuilder
{
    private readonly Instrument _instrument;
    private readonly ChartPeriod _period;
    private readonly CandleSeries? _timeHelper;
    private readonly bool _footprint;
    private long _bucketEnd;
    private Bar? _current;
    private long _highT, _lowT;
    private int _index;
    private decimal _pocVolume;

    public BarBuilder(Instrument instrument, ChartPeriod period, SessionSettings session, bool footprint)
    {
        _instrument = instrument;
        _period = period;
        _footprint = footprint;
        if (period.Type is PeriodType.Seconds or PeriodType.Minutes or PeriodType.Hours or PeriodType.Days or PeriodType.Weeks or PeriodType.Months)
            _timeHelper = new CandleSeries(instrument, period, 1, session);
        else if (period.Type is not (PeriodType.Tick or PeriodType.Volume or PeriodType.Range or PeriodType.Delta))
            throw new NotSupportedException($"Period {period.DisplayName} is unavailable in backtests (use time, tick, volume, range or delta bars).");
    }

    public Bar? Current => _current;

    /// <summary>
    /// Si esta operación abre una vela nueva, cierra la actual y la devuelve (todavía sin agregar la
    /// operación). Después hay que llamar a <see cref="Append"/>.
    /// </summary>
    public Bar? Roll(long timeTicks, long priceTicks)
    {
        if (_current == null || !ShouldStartNew(timeTicks, priceTicks)) return null;
        var closed = _current;
        Finish(closed);
        _current = null;
        return closed;
    }

    /// <summary>Agrega la operación a la vela en formación (la crea si hace falta).</summary>
    public void Append(long timeTicks, long priceTicks, decimal volume, TradeSide side)
    {
        if (_current == null) Start(timeTicks, priceTicks);
        var b = _current!;
        decimal price = _instrument.FromTicks(priceTicks);
        if (priceTicks > _highT)
        {
            _highT = priceTicks;
            b.High = price;
        }

        if (priceTicks < _lowT)
        {
            _lowT = priceTicks;
            b.Low = price;
        }

        b.Close = price;
        b.Volume += volume;
        if (side == TradeSide.Buy) b.BuyVolume += volume;
        else if (side == TradeSide.Sell) b.SellVolume += volume;
        decimal d = b.Delta;
        if (d > b.MaxDelta) b.MaxDelta = d;
        if (d < b.MinDelta) b.MinDelta = d;
        b.Trades++;
        b.CloseTime = new DateTime(timeTicks, DateTimeKind.Utc);
        if (_footprint)
        {
            b._levels ??= new Dictionary<decimal, (decimal Buy, decimal Sell)>();
            b._levels.TryGetValue(price, out var lv);
            lv = side == TradeSide.Buy ? (lv.Buy + volume, lv.Sell) : side == TradeSide.Sell ? (lv.Buy, lv.Sell + volume) : (lv.Buy + volume / 2, lv.Sell + volume / 2);
            b._levels[price] = lv;
            if (lv.Buy + lv.Sell > _pocVolume)
            {
                _pocVolume = lv.Buy + lv.Sell;
                b.PocPrice = price;
            }
        }
    }

    /// <summary>Cierra la vela en formación (fin de datos).</summary>
    public Bar? Flush()
    {
        var b = _current;
        if (b != null) Finish(b);
        _current = null;
        return b;
    }

    private bool ShouldStartNew(long timeTicks, long priceTicks)
    {
        var b = _current!;
        switch (_period.Type)
        {
            case PeriodType.Tick: return b.Trades >= _period.Value;
            case PeriodType.Volume: return b.Volume >= _period.Value;
            case PeriodType.Range: return Math.Max(_highT, priceTicks) - Math.Min(_lowT, priceTicks) > _period.Value;
            case PeriodType.Delta: return Math.Abs(b.Delta) >= _period.Value;
            default: return timeTicks >= _bucketEnd;
        }
    }

    private void Start(long timeTicks, long priceTicks)
    {
        DateTime open = new(timeTicks, DateTimeKind.Utc);
        if (_timeHelper != null)
        {
            var (start, end) = _timeHelper.TimeBucket(timeTicks);
            _bucketEnd = end;
            open = new DateTime(start, DateTimeKind.Utc);
        }

        decimal price = _instrument.FromTicks(priceTicks);
        _current = new Bar { Index = _index++, Time = open, CloseTime = open, Open = price, High = price, Low = price, Close = price, PocPrice = price };
        _highT = _lowT = priceTicks;
        _pocVolume = 0;
    }

    private static void Finish(Bar b)
    {
        if (b.PocPrice == 0) b.PocPrice = b.Close;
    }
}
