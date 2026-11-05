using TradingHub.Market;

namespace TradingHub.Feeds;

/// <summary>Feeds que pueden bajar un día completo de historia (para la sección Datos y los backtests).</summary>
public interface IDayDownloader
{
    /// <summary>Un día UTC completo en orden cronológico; null si ese día no hay datos.</summary>
    Task<List<Trade>?> DownloadDayAsync(Instrument instrument, DateOnly day, IProgress<double>? progress, CancellationToken ct);

    /// <summary>El mercado no opera los sábados (futuros, CFDs, acciones): no hace falta pedirlos.</summary>
    bool ClosedOnSaturdays => true;

    /// <summary>Texto corto sobre de dónde sale la historia (para mostrar antes de bajar).</summary>
    string HistoryNote => "";
}

/// <summary>Ayudas para armar historia: ids crecientes por tiempo, lado por regla del tick y velas cortas a operaciones.</summary>
public static class HistoryBuilder
{
    /// <summary>Id creciente a partir del tiempo (microsegundos Unix × 8, igual que Rithmic): nunca repite ni retrocede.</summary>
    public static long TimeId(DateTime utc, ref long last)
    {
        long us = (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        long id = us * 8;
        if (id <= last) id = last + 1;
        last = id;
        return id;
    }

    /// <summary>Lado de una cotización sin operaciones reales (CFDs, forex): sube = compra, baja = venta, igual = el anterior.</summary>
    public static TradeSide TickRule(decimal price, ref decimal lastPrice, ref TradeSide lastSide)
    {
        var side = lastPrice == 0 ? TradeSide.Buy : price > lastPrice ? TradeSide.Buy : price < lastPrice ? TradeSide.Sell : lastSide;
        lastPrice = price;
        lastSide = side;
        return side;
    }

    /// <summary>
    /// Velas de pocos segundos (por ejemplo las de 5 s de OANDA) a cuatro operaciones cada una: apertura, el extremo
    /// que se tocó primero según la dirección, el otro extremo y el cierre. El volumen (de ticks) se reparte entre las
    /// cuatro: así el OHLC queda exacto en cualquier período y los stops del backtest ven el recorrido dentro de la vela.
    /// </summary>
    public static void Synthesize(DateTime open, TimeSpan length, decimal o, decimal h, decimal l, decimal c, decimal volume, Instrument inst, ref long lastId, List<Trade> output, TradeProvenance provenance = default)
    {
        if (length <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(length));
        if (volume < 0) throw new ArgumentOutOfRangeException(nameof(volume));
        int seconds = checked((int)Math.Ceiling(length.TotalSeconds));
        provenance = provenance with
        {
            Quality = (provenance.Quality & ~DataQuality.Raw) | DataQuality.Synthetic | DataQuality.InferredSide,
            ResolutionSeconds = seconds,
            ResolutionOffsetSeconds = (int)(open.Ticks / TimeSpan.TicksPerSecond % seconds),
            HasKnownAvailability = (provenance.Quality == DataQuality.None || provenance.HasKnownAvailability) &&
                length.Ticks % TimeSpan.TicksPerSecond == 0 && open.Ticks % TimeSpan.TicksPerSecond == 0,
            SourceIdentity = provenance.SourceIdentity ?? inst.Key,
            ObservedTradeCount = 0,
        };
        o = inst.RoundPrice(o);
        h = inst.RoundPrice(h);
        l = inst.RoundPrice(l);
        c = inst.RoundPrice(c);
        bool up = c >= o;
        decimal q = volume / 4;
        long step = length.Ticks / 4;
        output.Add(new Trade(TimeId(open, ref lastId), open, o, q, up ? TradeSide.Buy : TradeSide.Sell, provenance));
        output.Add(new Trade(TimeId(open.AddTicks(step), ref lastId), open.AddTicks(step), up ? l : h, q, up ? TradeSide.Sell : TradeSide.Buy, provenance));
        output.Add(new Trade(TimeId(open.AddTicks(step * 2), ref lastId), open.AddTicks(step * 2), up ? h : l, q, up ? TradeSide.Buy : TradeSide.Sell, provenance));
        output.Add(new Trade(TimeId(open.AddTicks(step * 3), ref lastId), open.AddTicks(step * 3), c, q, up ? TradeSide.Sell : TradeSide.Buy, provenance));
    }
}
