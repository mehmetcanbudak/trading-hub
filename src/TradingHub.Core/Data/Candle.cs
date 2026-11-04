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

    /// <summary>Filas del footprint.</summary>
    public readonly PriceLevels Levels = new();

    /// <summary>Fila con más volumen (POC de la vela).</summary>
    public long PocRow;
    public decimal PocVolume;

    /// <summary>Volumen operado en esta vela por la operación más grande (para filtros de "big trades").</summary>
    public decimal MaxTradeVolume;

    public bool IsClosed;

    /// <summary>Tiene al menos una operación real (no armada a partir de velas del exchange).</summary>
    public bool HasRealTrades;
    public bool HasSyntheticTrades;

    /// <summary>Only aggregate bar history: OHLCV, possibly estimated side/delta, and no observed footprint.</summary>
    public bool IsSynthetic => Trades > 0 && !HasRealTrades;

    public decimal Delta => Ask - Bid;

    /// <summary>Agrega una operación a la vela. <paramref name="row"/> = priceTicks / paso.</summary>
    public void AddTrade(long priceTicks, long row, decimal volume, TradeSide side, long timeTicks, int tradeIndex, bool synthetic = false)
    {
        if (!synthetic) HasRealTrades = true;
        else HasSyntheticTrades = true;
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
        LastTime = new DateTime(timeTicks, DateTimeKind.Utc);
        Trades++;
        Volume += volume;
        if (volume > MaxTradeVolume && !synthetic) MaxTradeVolume = volume;

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
            ref var level = ref Levels.GetOrAdd(row);
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

            level.Trades++;
            level.LastTimeTicks = timeTicks;

            var lv = level.Volume;
            if (lv > PocVolume || (lv == PocVolume && row == PocRow))
            {
                PocVolume = lv;
                PocRow = row;
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
