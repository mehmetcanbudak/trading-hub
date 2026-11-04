using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>
/// Operaciones de un rango en memoria, en columnas compactas (~25 bytes por tick). Se carga una vez
/// y la comparten todas las corridas de una optimización. <see cref="Slice"/> no copia.
/// </summary>
public sealed class TickData
{
    private readonly long[] _times;
    private readonly long[] _prices;
    private readonly double[] _volumes;
    private readonly byte[] _sides;
    // Side occupies the low bits; the high bit preserves aggregate/synthetic origin without an ID array.
    private const byte SyntheticFlag = 0x80;
    private static byte EncodeSide(TradeSide side, bool synthetic) => (byte)((byte)side | (synthetic ? SyntheticFlag : 0));

    private TickData(Instrument instrument, long[] times, long[] prices, double[] volumes, byte[] sides, int start, int end, bool compressed = false)
    {
        Instrument = instrument;
        _times = times;
        _prices = prices;
        _volumes = volumes;
        _sides = sides;
        Start = start;
        End = end;
        Compressed = compressed;
    }

    /// <summary>
    /// Las operaciones seguidas al mismo precio, del mismo lado y en el mismo segundo están juntas en una (con el volumen
    /// sumado): ver <see cref="Compress"/>.
    /// </summary>
    public bool Compressed { get; }

    /// <summary>
    /// Junta las operaciones seguidas al mismo precio, del mismo lado y en el mismo segundo (suma el volumen). Las velas de
    /// tiempo o de rango quedan iguales (apertura, máximo, mínimo, cierre, volumen, delta y footprint) y las órdenes se
    /// llenan en los mismos precios, con muchos menos eventos que recorrer. Cambia la cantidad de operaciones de cada vela:
    /// no sirve para velas por cantidad de operaciones, de volumen o de delta, ni para estrategias que miran cada tick.
    /// </summary>
    public TickData Compress()
    {
        if (Compressed) return this;
        int n = 0;
        for (int i = Start; i < End; i++)
            if (i == Start || !SameRun(i - 1, i)) n++;
        var times = new long[n];
        var prices = new long[n];
        var volumes = new double[n];
        var sides = new byte[n];
        int k = -1;
        for (int i = Start; i < End; i++)
        {
            if (i > Start && SameRun(i - 1, i))
            {
                volumes[k] += _volumes[i];
                continue;
            }

            k++;
            times[k] = _times[i];
            prices[k] = _prices[i];
            volumes[k] = _volumes[i];
            sides[k] = _sides[i];
        }

        return new TickData(Instrument, times, prices, volumes, sides, 0, n, compressed: true);
    }

    private bool SameRun(int a, int b) =>
        _prices[a] == _prices[b] && _sides[a] == _sides[b] && _times[a] / TimeSpan.TicksPerSecond == _times[b] / TimeSpan.TicksPerSecond;

    public Instrument Instrument { get; }
    public int Start { get; }
    public int End { get; }
    public int Count => End - Start;
    public DateTime FirstTime => Count > 0 ? new DateTime(_times[Start], DateTimeKind.Utc) : DateTime.MinValue;
    public DateTime LastTime => Count > 0 ? new DateTime(_times[End - 1], DateTimeKind.Utc) : DateTime.MinValue;
    public long MemoryBytes => (long)_times.Length * 25;

    public long TimeTicks(int i) => _times[i];
    public long PriceTicks(int i) => _prices[i];
    public decimal Volume(int i) => (decimal)_volumes[i];
    public TradeSide Side(int i) => (TradeSide)(_sides[i] & ~SyntheticFlag);
    /// <summary>True for bar-derived proxy events, not observed individual trades. Views and compression preserve this flag.</summary>
    public bool IsSynthetic(int i) => (_sides[i] & SyntheticFlag) != 0;

    /// <summary>Derived event identity; its sign preserves provenance, not the original provider's trade ID.</summary>
    public Trade Get(int i) => new(IsSynthetic(i) ? -1L - i : i, new DateTime(_times[i], DateTimeKind.Utc), Instrument.FromTicks(_prices[i]), (decimal)_volumes[i], Side(i));

    /// <summary>Sub-rango por tiempo (sin copiar).</summary>
    public TickData Slice(DateTime fromUtc, DateTime toUtc)
    {
        int a = Lower(fromUtc.Ticks), b = Lower(toUtc.Ticks);
        return new TickData(Instrument, _times, _prices, _volumes, _sides, a, b, Compressed);
    }

    private int Lower(long t)
    {
        int lo = Start, hi = End;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_times[mid] < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// Carga de disco las operaciones de un instrumento entre dos instantes. Con <paramref name="compress"/> las junta
    /// mientras carga (como <see cref="Compress"/>), sin tener nunca en memoria todas las originales.
    /// </summary>
    public static TickData Load(string root, Instrument instrument, DateTime fromUtc, DateTime toUtc, IProgress<double>? progress, CancellationToken ct, bool compress = false)
    {
        var days = new List<DateOnly>();
        for (var d = DateOnly.FromDateTime(fromUtc); d <= DateOnly.FromDateTime(toUtc); d = d.AddDays(1)) days.Add(d);

        int n = 0, cap = 1 << 20;
        var times = new long[cap];
        var prices = new long[cap];
        var volumes = new double[cap];
        var sides = new byte[cap];
        for (int k = 0; k < days.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var list = HistorySources.ReadDay(root, instrument.FeedId, instrument.Symbol, days[k]);
            if (list.Count > 0)
            {
                foreach (var t in list)
                {
                    if (t.Time < fromUtc || t.Time >= toUtc) continue;
                    if (n == cap)
                    {
                        cap *= 2;
                        Array.Resize(ref times, cap);
                        Array.Resize(ref prices, cap);
                        Array.Resize(ref volumes, cap);
                        Array.Resize(ref sides, cap);
                    }

                    // si quedó algo fuera de orden por tiempo, se acomoda manteniendo el orden por id
                    long ticks = t.Time.Ticks;
                    if (n > 0 && ticks < times[n - 1]) ticks = times[n - 1];
                    long price = instrument.ToTicks(t.Price);
                    byte side = EncodeSide(t.Side, KlineHistory.IsSynthetic(t.Id));
                    if (compress && n > 0 && prices[n - 1] == price && sides[n - 1] == side && times[n - 1] / TimeSpan.TicksPerSecond == ticks / TimeSpan.TicksPerSecond)
                    {
                        volumes[n - 1] += (double)t.Volume;
                        continue;
                    }

                    times[n] = ticks;
                    prices[n] = price;
                    volumes[n] = (double)t.Volume;
                    sides[n] = side;
                    n++;
                }
            }

            progress?.Report((k + 1.0) / days.Count);
        }

        if (compress && n < times.Length)
        {
            // las comprimidas suelen ocupar mucho menos que lo reservado: se recorta
            Array.Resize(ref times, n);
            Array.Resize(ref prices, n);
            Array.Resize(ref volumes, n);
            Array.Resize(ref sides, n);
        }

        return new TickData(instrument, times, prices, volumes, sides, 0, n, compress);
    }

    /// <summary>Crea datos desde una lista (pruebas y datos sintéticos).</summary>
    /// <summary>Copia la historia que tiene cargada un gráfico (sin pasar por objetos Trade).</summary>
    public static TickData FromStore(TradeStore store)
    {
        int n = store.Count;
        var times = new long[n];
        var prices = new long[n];
        var volumes = new double[n];
        var sides = new byte[n];
        for (int i = 0; i < n; i++)
        {
            times[i] = store.GetTimeTicks(i);
            prices[i] = store.GetPriceTicks(i);
            volumes[i] = (double)store.GetVolume(i);
            sides[i] = EncodeSide(store.GetSide(i), KlineHistory.IsSynthetic(store.GetId(i)));
        }

        return new TickData(store.Instrument, times, prices, volumes, sides, 0, n);
    }

    public static TickData FromTrades(Instrument instrument, IReadOnlyList<Trade> trades)
    {
        int n = trades.Count;
        var times = new long[n];
        var prices = new long[n];
        var volumes = new double[n];
        var sides = new byte[n];
        for (int i = 0; i < n; i++)
        {
            times[i] = trades[i].Time.Ticks;
            prices[i] = instrument.ToTicks(trades[i].Price);
            volumes[i] = (double)trades[i].Volume;
            sides[i] = EncodeSide(trades[i].Side, KlineHistory.IsSynthetic(trades[i].Id));
        }

        return new TickData(instrument, times, prices, volumes, sides, 0, n);
    }
}
