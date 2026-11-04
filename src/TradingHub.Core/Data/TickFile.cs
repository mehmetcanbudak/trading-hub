using System.Globalization;
using System.Text;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Formato binario compacto para guardar operaciones (≈6 bytes por tick).
/// Un archivo por instrumento y día UTC: {raíz}/{feed}/{símbolo}/{yyyy-MM-dd}.vtk
///
/// Registro: byte de flags (bits 0-1 lado, bit 2 keyframe, bits 3-7 escala del volumen),
/// luego varints: id, tiempo (ms Unix), precio (ticks, zigzag), mantisa del volumen.
/// En los registros que no son keyframe, id/tiempo/precio van como diferencias con el anterior.
/// </summary>
public static class TickFile
{
    private static readonly byte[] Magic = "VTK1"u8.ToArray();

    public static string DayPath(string root, string feedId, string symbol, DateOnly day) =>
        Path.Combine(root, Sanitize(feedId), Sanitize(symbol), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".vtk");

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    public static IEnumerable<DateOnly> AvailableDays(string root, string feedId, string symbol)
    {
        var dir = Path.Combine(root, Sanitize(feedId), Sanitize(symbol));
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.vtk").OrderBy(x => x))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                yield return d;
        }
    }

    /// <summary>Escritor en modo append (thread-safe por instancia con lock externo).</summary>
    public sealed class Writer : IDisposable
    {
        private readonly FileStream _fs;
        private readonly BufferedStream _bs;
        private readonly Instrument _instrument;
        private bool _hasPrev;
        private long _prevId, _prevMs, _prevPrice;
        private int _sinceKey;

        public Writer(string path, Instrument instrument)
        {
            _instrument = instrument;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool exists = File.Exists(path) && new FileInfo(path).Length > 0;
            _fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _bs = new BufferedStream(_fs, 1 << 16);
            if (!exists)
            {
                _bs.Write(Magic);
                var ts = Encoding.UTF8.GetBytes(instrument.TickSize.ToString(CultureInfo.InvariantCulture));
                _bs.WriteByte((byte)ts.Length);
                _bs.Write(ts);
            }
        }

        public void Write(in Trade t)
        {
            long ms = new DateTimeOffset(t.Time).ToUnixTimeMilliseconds();
            long price = _instrument.ToTicks(t.Price);
            var (mantissa, scale) = SplitDecimal(t.Volume);
            bool key = !_hasPrev || ms < _prevMs || _sinceKey >= 4096 || t.Id < _prevId;
            byte flags = (byte)(((byte)t.Side & 0x3) | (key ? 0x4 : 0) | ((scale & 0x1F) << 3));
            Span<byte> buf = stackalloc byte[48];
            int n = 0;
            buf[n++] = flags;
            if (key)
            {
                n += WriteVarint(buf[n..], (ulong)t.Id);
                n += WriteVarint(buf[n..], (ulong)ms);
                n += WriteVarint(buf[n..], ZigZag(price));
                _sinceKey = 0;
            }
            else
            {
                n += WriteVarint(buf[n..], ZigZag(t.Id - _prevId));
                n += WriteVarint(buf[n..], (ulong)(ms - _prevMs));
                n += WriteVarint(buf[n..], ZigZag(price - _prevPrice));
                _sinceKey++;
            }

            n += WriteVarint(buf[n..], mantissa);
            _bs.Write(buf[..n]);
            _hasPrev = true;
            _prevId = t.Id;
            _prevMs = ms;
            _prevPrice = price;
        }

        public void Flush()
        {
            _bs.Flush();
            _fs.Flush();
        }

        public void Dispose()
        {
            try { _bs.Flush(); }
            finally
            {
                try { _bs.Dispose(); }
                finally { _fs.Dispose(); }
            }
        }
    }

    /// <summary>Tamaño del tick guardado en el encabezado del archivo (null si no se puede leer).</summary>
    public static decimal? ReadTickSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4 || !magic.SequenceEqual(Magic)) return null;
            int len = fs.ReadByte();
            if (len <= 0) return null;
            var buf = new byte[len];
            fs.ReadExactly(buf);
            return decimal.Parse(Encoding.UTF8.GetString(buf), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lee un archivo completo. Devuelve las operaciones en el orden guardado, sin los tramos dañados.</summary>
    public static List<Trade> Read(string path) => Read(path, out _);

    /// <summary>
    /// Lee un archivo completo y descarta los tramos dañados (por ejemplo si dos procesos grabaron a la vez el
    /// mismo archivo y se mezclaron los registros): tiempos fuera del día del archivo, ids que retroceden dentro
    /// de un tramo, precios que saltan de golpe. Se descarta desde el keyframe anterior al primer registro
    /// imposible hasta el keyframe siguiente al último, así no queda basura "creíble" en el medio.
    /// Lo que falte se vuelve a pedir al exchange al cargar (los huecos se rellenan solos).
    /// </summary>
    public static List<Trade> Read(string path, out int dropped, bool requireComplete = false) =>
        ReadCore(path, out dropped, requireComplete, allowIncompleteHeader: false);

    /// <summary>Read an append snapshot only while the caller has established another writer owns the instrument lease.</summary>
    internal static List<Trade> ReadAppendPrefix(string path, out int dropped, decimal? expectedTickSize = null) =>
        ReadCore(path, out dropped, requireComplete: false, allowIncompleteHeader: true, expectedTickSize);

    private static List<Trade> ReadCore(string path, out int dropped, bool requireComplete, bool allowIncompleteHeader, decimal? expectedTickSize = null)
    {
        dropped = 0;
        var result = new List<Trade>();
        if (!File.Exists(path)) return result;
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[fs.Length];
            int read = 0;
            while (read < data.Length)
            {
                int r = fs.Read(data, read, data.Length - read);
                if (r <= 0) break;
                read += r;
            }
        }

        if (allowIncompleteHeader)
        {
            int prefix = Math.Min(data.Length, Magic.Length);
            if (!data.AsSpan(0, prefix).SequenceEqual(Magic.AsSpan(0, prefix)))
                throw new InvalidDataException("Invalid tick-file magic: " + path);
            if (data.Length < 5) return result; // A real writer has not flushed its complete initial header yet.
        }
        else if (data.Length < 5 || !data.AsSpan(0, 4).SequenceEqual(Magic)) return result;
        int pos = 4;
        int tsLen = data[pos++];
        if (allowIncompleteHeader && tsLen == 0) throw new InvalidDataException("Invalid tick-size header: " + path);
        if (pos + tsLen > data.Length) return result;
        var tickSize = decimal.Parse(Encoding.UTF8.GetString(data, pos, tsLen), CultureInfo.InvariantCulture);
        pos += tsLen;
        if (allowIncompleteHeader && tickSize <= 0) throw new InvalidDataException("Invalid tick-size header: " + path);
        if (expectedTickSize.HasValue && tickSize != expectedTickSize) throw new InvalidDataException("Tick metadata differs from the catalog: " + path);

        // el archivo es de un día UTC: un tiempo fuera de ese día es basura
        long minMs = long.MinValue, maxMs = long.MaxValue;
        if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            minMs = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds() - 60_000;
            maxMs = minMs + 86_400_000 + 120_000;
        }

        var keys = new List<int>();
        List<int>? bad = null;
        long prevId = 0, prevMs = 0, prevPrice = 0;
        long firstId = -1; // el primer registro del archivo siempre es un keyframe sano
        try
        {
            while (pos < data.Length)
            {
                byte flags = data[pos++];
                int sideBits = flags & 0x3;
                bool key = (flags & 0x4) != 0;
                int scale = (flags >> 3) & 0x1F;
                long id, ms, price, dId = 1, dPrice = 0;
                if (key)
                {
                    id = (long)ReadVarint(data, ref pos, allowIncompleteHeader);
                    ms = (long)ReadVarint(data, ref pos, allowIncompleteHeader);
                    price = UnZigZag(ReadVarint(data, ref pos, allowIncompleteHeader));
                }
                else
                {
                    dId = UnZigZag(ReadVarint(data, ref pos, allowIncompleteHeader));
                    id = prevId + dId;
                    ms = prevMs + (long)ReadVarint(data, ref pos, allowIncompleteHeader);
                    dPrice = UnZigZag(ReadVarint(data, ref pos, allowIncompleteHeader));
                    price = prevPrice + dPrice;
                }

                ulong mant = ReadVarint(data, ref pos, allowIncompleteHeader);
                if (pos > data.Length)
                {
                    if (requireComplete) throw new InvalidDataException("Truncated tick record: " + path);
                    break;
                }

                if (firstId < 0) firstId = id;

                // un escritor solo nunca deja estos registros: si aparecen, el archivo está mezclado.
                // Un keyframe con un id de otra escala es un registro leído desde la mitad (varint cortado).
                bool ok = sideBits != 3 && scale <= 28 && price > 0 && ms >= minMs && ms < maxMs
                          && (!key || firstId < 1L << 28 || (id > firstId / 64 && id / 64 < firstId))
                          && (key || dId > 0 || (dId == 0 && dPrice == 0))
                          && (key || prevPrice <= 0 || Math.Abs(dPrice) <= Math.Max(10, prevPrice / 33)); // salto > 3 % entre dos operaciones seguidas
                if (key) keys.Add(result.Count);
                if (ok)
                {
                    var vol = new decimal((int)(mant & 0xFFFFFFFF), (int)(mant >> 32), 0, false, (byte)scale);
                    result.Add(new Trade(id, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, price * tickSize, vol, (TradeSide)sideBits));
                }
                else
                {
                    (bad ??= new List<int>()).Add(result.Count);
                    result.Add(default);
                }

                prevId = id;
                prevMs = ms;
                prevPrice = price;
            }
        }
        catch (IndexOutOfRangeException)
        {
            if (requireComplete) throw new InvalidDataException("Truncated or invalid tick record: " + path);
            // último registro incompleto: se ignora
        }

        if (bad == null && Increasing(result, null)) return result;

        // fase 1: tramos alrededor de los registros imposibles
        var drop = Zones(bad, keys, result.Count);

        // fase 2: mismo id con datos distintos fuera de esos tramos: la cadena de diferencias de un escritor quedó
        // aplicada sobre la base de otro (los dos grababan el mismo vivo). De cada par se descarta la copia más
        // cercana a un tramo dañado (la otra es la buena); si no se puede saber, las dos.
        if (!Increasing(result, drop))
        {
            int n = result.Count;
            var dist = new int[n];
            int lastDrop = int.MinValue / 2;
            for (int i = 0; i < n; i++)
            {
                if (drop[i]) lastDrop = i;
                dist[i] = i - lastDrop;
            }

            int nextDrop = int.MaxValue / 2;
            for (int i = n - 1; i >= 0; i--)
            {
                if (drop[i]) nextDrop = i;
                dist[i] = Math.Min(dist[i], nextDrop - i);
            }

            var extra = new List<int>();
            var seen = new Dictionary<long, int>(n);
            for (int i = 0; i < n; i++)
            {
                if (drop[i]) continue;
                var t = result[i];
                if (!seen.TryGetValue(t.Id, out int j))
                {
                    seen[t.Id] = i;
                    continue;
                }

                var o = result[j];
                if (o.Time == t.Time && o.Price == t.Price && o.Volume == t.Volume) continue; // la misma, grabada dos veces
                if (dist[i] <= dist[j]) extra.Add(i);
                if (dist[j] <= dist[i]) extra.Add(j);
                if (dist[j] > dist[i]) seen[t.Id] = j; else seen[t.Id] = i;
            }

            if (extra.Count > 0)
            {
                var all = new List<int>(bad ?? new List<int>());
                all.AddRange(extra);
                all.Sort();
                drop = Zones(all, keys, n);
            }
        }

        var clean = new List<Trade>(result.Count);
        for (int i = 0; i < result.Count; i++)
        {
            if (drop[i]) dropped++;
            else clean.Add(result[i]);
        }

        return clean;
    }

    /// <summary>Si los ids (sin contar los descartados) vienen siempre en subida.</summary>
    private static bool Increasing(List<Trade> list, bool[]? drop)
    {
        long last = long.MinValue;
        for (int i = 0; i < list.Count; i++)
        {
            if (drop != null && drop[i]) continue;
            if (list[i].Id <= last) return false;
            last = list[i].Id;
        }

        return true;
    }

    /// <summary>
    /// Marca los tramos dañados: registros malos cercanos se juntan y se descarta desde el keyframe anterior
    /// al primero hasta el keyframe siguiente al último (lo del medio depende de la misma cadena de diferencias).
    /// </summary>
    private static bool[] Zones(List<int>? bad, List<int> keys, int count)
    {
        var drop = new bool[count];
        if (bad == null) return drop;
        for (int b = 0; b < bad.Count;)
        {
            int first = bad[b], last = first;
            while (b + 1 < bad.Count && bad[b + 1] - last <= 4096) last = bad[++b];
            b++;
            int k = keys.BinarySearch(first);
            int from = k >= 0 ? (k > 0 ? keys[k - 1] : 0) : (~k > 0 ? keys[~k - 1] : 0);
            int next = keys.BinarySearch(last + 1);
            int to = next >= 0 ? keys[next] : ~next < keys.Count ? keys[~next] : count;
            for (int i = from; i < to; i++) drop[i] = true;
        }

        return drop;
    }

    /// <summary>
    /// Reescribe un archivo sin los tramos dañados (se guarda aparte y se reemplaza de una).
    /// Solo llamar sin nadie grabando ese instrumento. Devuelve cuántos registros se sacaron.
    /// </summary>
    public static int Repair(string path, Instrument instrument)
    {
        var trades = Read(path, out int dropped);
        if (dropped == 0) return 0;
        var tmp = path + ".reparando";
        File.Delete(tmp);
        using (var w = new Writer(tmp, instrument))
            foreach (var t in trades) w.Write(t);
        File.Move(tmp, path, overwrite: true);
        return dropped;
    }

    /// <summary>
    /// Permiso exclusivo para grabar un instrumento: un solo proceso escribe sus archivos (dos escritores
    /// mezclan los registros y dañan el archivo). Devuelve null si otro ya lo tiene; se libera al cerrar.
    /// </summary>
    public static FileStream? TryLock(string root, string feedId, string symbol, bool failOnNonSharingError = false, bool recordingWriter = false)
    {
        var dir = Path.Combine(root, Sanitize(feedId), Sanitize(symbol));
        try
        {
            Directory.CreateDirectory(dir);
            if (recordingWriter)
                return new RecordingLease(Path.Combine(dir, ".grabando.lock"), Path.Combine(dir, ".recording-active.lock"));
            return new FileStream(Path.Combine(dir, ".grabando.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (!failOnNonSharingError || IsSharingViolation(error))
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (!failOnNonSharingError)
        {
            return null;
        }
    }

    // Win32 sharing/lock violations; POSIX EAGAIN/EWOULDBLOCK on Linux and Darwin.
    private static bool IsSharingViolation(IOException error) => (error.HResult & 0xFFFF) is 11 or 35 or 32 or 33;

    /// <summary>Cross-process proof of an actual append recorder, not merely an import/replacement lease.</summary>
    internal static bool HasActiveRecordingWriter(string root, string feedId, string symbol)
    {
        string path = Path.Combine(root, Sanitize(feedId), Sanitize(symbol), ".recording-active.lock");
        try
        {
            using var probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException error) when (IsSharingViolation(error)) { return true; }
    }

    private sealed class RecordingLease : FileStream
    {
        private readonly FileStream? _marker;
        public RecordingLease(string primary, string marker)
            : base(primary, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        {
            try { _marker = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch { base.Dispose(true); throw; }
        }
        protected override void Dispose(bool disposing)
        {
            // Writer closes first in its owner. Marker lifetime must be strictly inside primary ownership.
            try { if (disposing) _marker?.Dispose(); }
            finally { base.Dispose(disposing); }
        }
    }

    /// <summary>Ordena por id y elimina duplicados si hace falta.</summary>
    public static List<Trade> SortAndDedupe(List<Trade> trades)
    {
        bool sorted = true;
        for (int i = 1; i < trades.Count; i++)
        {
            if (trades[i].Id <= trades[i - 1].Id)
            {
                sorted = false;
                break;
            }
        }

        if (sorted) return trades;
        trades.Sort((a, b) => a.Id.CompareTo(b.Id));
        var res = new List<Trade>(trades.Count);
        long last = long.MinValue;
        foreach (var t in trades)
        {
            if (t.Id == last) continue;
            res.Add(t);
            last = t.Id;
        }

        return res;
    }

    internal static (ulong Mantissa, int Scale) SplitDecimal(decimal d)
    {
        d = Math.Abs(d) / 1.0000000000000000000000000000m; // normaliza (quita ceros)
        var bits = decimal.GetBits(d);
        int scale = (bits[3] >> 16) & 0xFF;
        if (bits[2] != 0 || scale > 28)
        {
            // fuera de rango: se redondea a 8 decimales
            d = Math.Round(d, 8);
            bits = decimal.GetBits(d);
            scale = (bits[3] >> 16) & 0xFF;
        }

        ulong mant = ((ulong)(uint)bits[1] << 32) | (uint)bits[0];
        return (mant, Math.Min(scale, 31));
    }

    private static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
    private static long UnZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    private static int WriteVarint(Span<byte> buf, ulong v)
    {
        int n = 0;
        while (v >= 0x80)
        {
            buf[n++] = (byte)(v | 0x80);
            v >>= 7;
        }

        buf[n++] = (byte)v;
        return n;
    }

    private static ulong ReadVarint(byte[] data, ref int pos, bool rejectMalformed = false)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63)
            {
                if (rejectMalformed) throw new InvalidDataException("Invalid overlong tick varint.");
                throw new IndexOutOfRangeException();
            }
        }

        return result;
    }
}
