using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Vela con footprint completo: OHLC, volumen bid/ask, delta y los niveles de precio.
/// Los precios están en ticks del instrumento; las filas del footprint en "filas" (ticks / paso).
/// </summary>
public sealed class Candle
{
    public DateTime Time;       // apertura (UTC)
    public DateTime LastTime;   // última operación (UTC)

    public long OpenTicks;
    public long HighTicks;
    public long LowTicks;
    public long CloseTicks;

    public decimal Volume;
    public decimal Bid;         // volumen vendedor agresivo
    public decimal Ask;         // volumen comprador agresivo
    public decimal Between;
    public decimal MaxDelta;
    public decimal MinDelta;
    public int Trades;

    public decimal OI;
    public decimal MaxOI;
    public decimal MinOI;
    public bool HasOpenInterest;
    public bool HasOpenInterestRange;
    public DateTime? OpenInterestTime;
    public bool HasOpenInterestChange;
    public decimal OpenInterestChange;
    public string? OpenInterestStatus;

    public int FirstTradeIndex = -1;
    public int LastTradeIndex = -1;
    /// <summary>Volume from the boundary source event actually applied to this bar (which can split a volume-bar event).</summary>
    public decimal LastTradeVolume;

    /// <summary>Filas del footprint.</summary>
    public readonly PriceLevels Levels = new();
    /// <summary>Measured compact price/side totals, never individual observed prints.</summary>
    public readonly PriceLevels AggregatedLevels = new();

    /// <summary>Fila con más volumen (POC de la vela).</summary>
    public long PocRow;
    public decimal PocVolume;

    /// <summary>Volumen operado en esta vela por la operación más grande (para filtros de "big trades").</summary>
    public decimal MaxTradeVolume;

    public bool IsClosed;

    /// <summary>Tiene al menos una operación real (no armada a partir de velas del exchange).</summary>
    public bool HasRealTrades;
    public bool HasSyntheticTrades;
    public bool HasCompactTrades;
    public DataQuality Quality;
    public int ResolutionSeconds;
    public string? SourceIdentity;
    public long Revision;
    public long CorrectionRevision;
    public int ObservedTrades;
    public long AggregatedTrades;
    public DateTime EarliestSourceTime = DateTime.MaxValue;
    public DateTime AvailableAt;
    public bool HasKnownAvailability = true;
    public bool HasIncompatibleResolution;
    public bool HasInferredSide => (Quality & DataQuality.InferredSide) != 0;
    public bool IsFootprintComplete => HasRealTrades && !HasCompactTrades && !HasSyntheticTrades && (Quality & DataQuality.Partial) == 0;

    /// <summary>Only aggregate bar history: OHLCV, possibly estimated side/delta, and no observed footprint.</summary>
    public bool IsSynthetic => Trades > 0 && !HasRealTrades && !HasCompactTrades;

    public decimal Delta => Ask - Bid;

    /// <summary>Agrega una operación a la vela. <paramref name="row"/> = priceTicks / paso.</summary>
    public void AddTrade(long priceTicks, long row, decimal volume, TradeSide side, long timeTicks, int tradeIndex, bool synthetic = false, TradeProvenance provenance = default, bool resolutionCompatible = true)
    {
        if (Trades == 0)
        {
            Quality = DataQuality.None;
            ResolutionSeconds = 0;
            HasRealTrades = HasSyntheticTrades = HasCompactTrades = false;
            ObservedTrades = 0; AggregatedTrades = 0;
            MaxTradeVolume = PocVolume = 0;
            Levels.Clear(); AggregatedLevels.Clear();
            EarliestSourceTime = DateTime.MaxValue; AvailableAt = DateTime.MinValue;
            HasKnownAvailability = true; HasIncompatibleResolution = false;
        }
        provenance = provenance.Normalize(synthetic ? -1 : 0);
        if (Trades == 0 && provenance.SourceIdentity != null) SourceIdentity = provenance.SourceIdentity;
        bool observed = provenance.IsObservedTrade;
        bool compact = (provenance.Quality & DataQuality.Compact) != 0 && (provenance.Quality & DataQuality.Synthetic) == 0;
        synthetic = !observed && !compact;
        HasRealTrades |= observed;
        HasCompactTrades |= compact;
        HasSyntheticTrades |= synthetic;
        Quality |= provenance.Quality;
        ResolutionSeconds = Math.Max(ResolutionSeconds, provenance.ResolutionSeconds);
        CorrectionRevision = Math.Max(CorrectionRevision, provenance.CorrectionRevision);
        var window = provenance.SourceWindow(new DateTime(timeTicks, DateTimeKind.Utc));
        if (window.From < EarliestSourceTime) EarliestSourceTime = window.From;
        if (window.To > AvailableAt) AvailableAt = window.To;
        HasKnownAvailability &= provenance.HasKnownAvailability;
        HasIncompatibleResolution |= !resolutionCompatible;
        if (SourceIdentity == null) SourceIdentity = provenance.SourceIdentity;
        else if (provenance.SourceIdentity != null && SourceIdentity != provenance.SourceIdentity) SourceIdentity = "mixed";
        Revision++;
        if (observed) ObservedTrades++;
        else if (compact) AggregatedTrades += provenance.ObservedTradeCount;
        if (Trades == 0)
        {
            OpenTicks = HighTicks = LowTicks = priceTicks;
            FirstTradeIndex = tradeIndex;
        }
        else
        {
            if (priceTicks > HighTicks) HighTicks = priceTicks;
            if (priceTicks < LowTicks) LowTicks = priceTicks;
        }

        CloseTicks = priceTicks;
        LastTradeIndex = tradeIndex;
        LastTradeVolume = volume;
        LastTime = new DateTime(timeTicks, DateTimeKind.Utc);
        Trades++;
        Volume += volume;
        if (volume > MaxTradeVolume && observed) MaxTradeVolume = volume;

        if (synthetic)
        {
            // Aggregate history contributes OHLCV/side estimates but never invents observed footprint rows.
            switch (side)
            {
                case TradeSide.Buy: Ask += volume; break;
                case TradeSide.Sell: Bid += volume; break;
                default: Between += volume; break;
            }

            if (!HasRealTrades) PocRow = row;
        }
        else
        {
            var levels = compact ? AggregatedLevels : Levels;
            ref var level = ref levels.GetOrAdd(row);
            switch (side)
            {
                case TradeSide.Buy:
                    Ask += volume;
                    level.Ask += volume;
                    break;
                case TradeSide.Sell:
                    Bid += volume;
                    level.Bid += volume;
                    break;
                default:
                    Between += volume;
                    level.Between += volume;
                    break;
            }

            level.Trades = checked(level.Trades + (compact ? provenance.ObservedTradeCount : 1));
            level.LastTimeTicks = timeTicks;

            if (observed)
            {
                var lv = level.Volume;
                if (lv > PocVolume || (lv == PocVolume && row == PocRow))
                {
                    PocVolume = lv;
                    PocRow = row;
                }
            }
        }

        var d = Ask - Bid;
        if (Trades == 1)
        {
            MaxDelta = MinDelta = d;
        }
        else
        {
            if (d > MaxDelta) MaxDelta = d;
            if (d < MinDelta) MinDelta = d;
        }
    }
}
