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
    public bool HasRealTrades { get; internal set; }
    public bool HasSyntheticTrades { get; internal set; }
    public bool HasCompactTrades { get; internal set; }
    public DataQuality Quality { get; internal set; }
    public int ResolutionSeconds { get; internal set; }
    public string? SourceIdentity { get; internal set; }
    public long CorrectionRevision { get; internal set; }
    public int ObservedTrades { get; internal set; }
    public long AggregatedTrades { get; internal set; }
    public DateTime AvailableAt { get; internal set; }
    public bool HasKnownAvailability { get; internal set; } = true;
    public bool IsTimeResolutionCompatible { get; internal set; } = true;
    public bool HasInferredSide => (Quality & DataQuality.InferredSide) != 0;
    public bool HasAggregatedFootprint => _aggregatedLevels is { Count: > 0 };
    public bool IsFootprintComplete => HasRealTrades && !HasCompactTrades && !HasSyntheticTrades && (Quality & DataQuality.Partial) == 0;
    public bool IsSynthetic => HasSyntheticTrades && !HasRealTrades && !HasCompactTrades;
    public bool HasObservedPoc { get; internal set; }
    public bool HasObservedFootprint => _levels is { Count: > 0 };

    /// <summary>Source-reported/inferred buy volume; includes estimated allocations when HasSyntheticTrades is true.</summary>
    public decimal BuyVolume { get; internal set; }

    /// <summary>Source-reported/inferred sell volume; includes estimated allocations when HasSyntheticTrades is true.</summary>
    public decimal SellVolume { get; internal set; }
    public decimal BetweenVolume { get; internal set; }

    /// <summary>Aggregate side balance; includes synthetic allocation estimates when HasSyntheticTrades is true.</summary>
    public decimal Delta => BuyVolume - SellVolume;
    public decimal MaxDelta { get; internal set; }
    public decimal MinDelta { get; internal set; }
    /// <summary>Input event count; synthetic bars contribute proxy events, not an observed exchange trade count.</summary>
    public int Trades { get; internal set; }

    /// <summary>Observed footprint POC when HasObservedPoc; otherwise first real-trade fallback, or zero for synthetic-only bars.</summary>
    public decimal PocPrice { get; internal set; }

    /// <summary>Observed trades only, when UseFootprint is enabled. Synthetic aggregates never create levels.</summary>
    public IReadOnlyDictionary<decimal, (decimal Buy, decimal Sell)> Levels => _levels ?? Empty;
    public IReadOnlyDictionary<decimal, (decimal Buy, decimal Sell)> AggregatedLevels => _aggregatedLevels ?? Empty;
    public IReadOnlyDictionary<decimal, decimal> BetweenLevels => _betweenLevels ?? EmptyBetween;
    public IReadOnlyDictionary<decimal, decimal> AggregatedBetweenLevels => _aggregatedBetweenLevels ?? EmptyBetween;

    public bool IsBullish => Close > Open;
    public bool IsBearish => Close < Open;
    public decimal Range => High - Low;
    public decimal Body => Math.Abs(Close - Open);
    public decimal Typical => (High + Low + Close) / 3;

    internal Dictionary<decimal, (decimal Buy, decimal Sell)>? _levels;
    internal Dictionary<decimal, (decimal Buy, decimal Sell)>? _aggregatedLevels;
    internal Dictionary<decimal, decimal>? _betweenLevels, _aggregatedBetweenLevels;
    private static readonly Dictionary<decimal, decimal> EmptyBetween = new();
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
    private long _firstTimeTicks, _lastTimeTicks;
    private int _index;
    private decimal _pocVolume;

    public BarBuilder(Instrument instrument, ChartPeriod period, SessionSettings session, bool footprint, CandleSeries? timeHelper = null)
    {
        _instrument = instrument;
        _period = period;
        _footprint = footprint;
        if (period.Type is PeriodType.Seconds or PeriodType.Minutes or PeriodType.Hours or PeriodType.Days or PeriodType.Weeks or PeriodType.Months)
            _timeHelper = timeHelper ?? new CandleSeries(instrument, period, 1, session);
        else if (period.Type is not (PeriodType.Tick or PeriodType.Volume or PeriodType.Range or PeriodType.Delta))
            throw new NotSupportedException($"Period {period.DisplayName} is unavailable in backtests (use time, tick, volume, range or delta bars).");
    }

    public Bar? Current => _current;

    public bool IsResolutionCompatible(long locationTicks, TradeProvenance provenance) =>
        provenance.ResolutionSeconds <= 0 || _timeHelper?.IsResolutionCompatible(locationTicks, provenance) == true;

    /// <summary>
    /// Si esta operación abre una vela nueva, cierra la actual y la devuelve (todavía sin agregar la
    /// operación). Después hay que llamar a <see cref="Append"/>.
    /// </summary>
    public Bar? Roll(long timeTicks, long priceTicks)
    {
        if (_current == null || !ShouldStartNew(timeTicks, priceTicks)) return null;
        var closed = _current;
        _current = null;
        return closed;
    }

    /// <summary>Agrega la operación a la vela en formación (la crea si hace falta).</summary>
    public void Append(long timeTicks, long priceTicks, decimal volume, TradeSide side, bool synthetic) =>
        Append(timeTicks, priceTicks, volume, side, new TradeProvenance(synthetic ? DataQuality.Synthetic | DataQuality.InferredSide : DataQuality.Raw));

    public void Append(long timeTicks, long priceTicks, decimal volume, TradeSide side, TradeProvenance provenance)
    {
        if (_current == null) Start(timeTicks, priceTicks);
        var b = _current!;
        decimal price = _instrument.FromTicks(priceTicks);
        bool observed = provenance.IsObservedTrade;
        bool compact = (provenance.Quality & DataQuality.Compact) != 0 && (provenance.Quality & DataQuality.Synthetic) == 0;
        b.Quality |= provenance.Quality;
        b.ResolutionSeconds = Math.Max(b.ResolutionSeconds, provenance.ResolutionSeconds);
        b.CorrectionRevision = Math.Max(b.CorrectionRevision, provenance.CorrectionRevision);
        if (b.SourceIdentity == null) b.SourceIdentity = provenance.SourceIdentity;
        else if (provenance.SourceIdentity != null && b.SourceIdentity != provenance.SourceIdentity) b.SourceIdentity = "mixed";
        var location = new DateTime(timeTicks, DateTimeKind.Utc);
        DateTime available = provenance.AvailabilityTime(location);
        if (available > b.AvailableAt) b.AvailableAt = available;
        b.HasKnownAvailability &= provenance.HasKnownAvailability;
        b.IsTimeResolutionCompatible &= provenance.ResolutionSeconds <= 0 ||
            _timeHelper?.IsResolutionCompatible(timeTicks, provenance) == true;
        b.HasCompactTrades |= compact;
        b.HasSyntheticTrades |= (provenance.Quality & DataQuality.Synthetic) != 0;
        if (observed) b.ObservedTrades++;
        else if (compact) b.AggregatedTrades += provenance.ObservedTradeCount;
        if (observed)
        {
            if (!b.HasRealTrades) b.PocPrice = price;
            b.HasRealTrades = true;
        }
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

        if (timeTicks < _firstTimeTicks) { _firstTimeTicks = timeTicks; b.Open = price; }
        if (timeTicks >= _lastTimeTicks) { _lastTimeTicks = timeTicks; b.Close = price; b.CloseTime = location; }
        b.Volume += volume;
        if (side == TradeSide.Buy) b.BuyVolume += volume;
        else if (side == TradeSide.Sell) b.SellVolume += volume;
        else b.BetweenVolume += volume;
        decimal d = b.Delta;
        if (b.Trades == 0) b.MaxDelta = b.MinDelta = d;
        else { if (d > b.MaxDelta) b.MaxDelta = d; if (d < b.MinDelta) b.MinDelta = d; }
        b.Trades++;
        if (_footprint && (observed || compact))
        {
            var levels = observed ? b._levels ??= new Dictionary<decimal, (decimal Buy, decimal Sell)>() : b._aggregatedLevels ??= new Dictionary<decimal, (decimal Buy, decimal Sell)>();
            levels.TryGetValue(price, out var lv);
            lv = side == TradeSide.Buy ? (lv.Buy + volume, lv.Sell) : side == TradeSide.Sell ? (lv.Buy, lv.Sell + volume) : lv;
            levels[price] = lv;
            var between = observed ? b._betweenLevels : b._aggregatedBetweenLevels;
            decimal unknown = 0;
            between?.TryGetValue(price, out unknown);
            if (side == TradeSide.None)
            {
                between ??= new Dictionary<decimal, decimal>();
                if (observed) b._betweenLevels = between; else b._aggregatedBetweenLevels = between;
                unknown += volume; between[price] = unknown;
            }
            if (observed && lv.Buy + lv.Sell + unknown > _pocVolume)
            {
                _pocVolume = lv.Buy + lv.Sell + unknown;
                b.PocPrice = price;
                b.HasObservedPoc = true;
            }
        }
    }

    /// <summary>Cierra la vela en formación (fin de datos).</summary>
    public Bar? Flush()
    {
        var b = _current;
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
        _current = new Bar { Index = _index++, Time = open, CloseTime = open, Open = price, High = price, Low = price, Close = price };
        _highT = _lowT = priceTicks;
        _firstTimeTicks = _lastTimeTicks = timeTicks;
        _pocVolume = 0;
    }

}
