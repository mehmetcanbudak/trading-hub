using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TradingHub.Common;
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
public static partial class TickFile
{
    private static readonly byte[] Magic = "VTK1"u8.ToArray();
    private static readonly byte[] ProvenanceMagic = "VTK2"u8.ToArray();
    private static readonly UTF8Encoding ProvenanceEncoding = new(false, true);
    private const int MaximumEventIdBytes = 16_384;
    private const int ShortEventIdBufferSize = 256;

    public static string? ExistingDayPath(string root, string feedId, string symbol, DateOnly day) =>
        TickArchive.Resolve(DayPath(root, feedId, symbol, day));

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
        var seen = new HashSet<DateOnly>();
        foreach (var f in Directory.EnumerateFiles(dir).Where(p => p.EndsWith(".vtk", StringComparison.Ordinal) || p.EndsWith(".vtk.br", StringComparison.Ordinal)).OrderBy(x => x))
        {
            var name = Path.GetFileNameWithoutExtension(f.EndsWith(".br", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(f) : f);
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && seen.Add(d))
                yield return d;
        }
    }

    /// <summary>Escritor en modo append (thread-safe por instancia con lock externo).</summary>
    public sealed class Writer : IDisposable
    {
        private readonly FileStream _fs;
        private readonly BufferedStream _bs;
        private readonly Instrument _instrument;
        private readonly HistoryWriteOwnership? _ownership;
        private bool _disposed;
        private bool _hasPrev;
        private long _prevId, _prevMs, _prevPrice;
        private int _sinceKey;
        private bool _withProvenance;
        private bool _headerWritten;
        private readonly Dictionary<TradeProvenance, int> _provenance = new();

        public Writer(string path, Instrument instrument, bool? includeProvenance = null)
            : this(path, instrument, includeProvenance, null) { }

        internal Writer(string path, Instrument instrument, HistoryWriteOwnership ownership, bool? includeProvenance = null)
            : this(path, instrument, includeProvenance, ownership) { }

        private Writer(string path, Instrument instrument, bool? includeProvenance, HistoryWriteOwnership? ownership)
        {
            _instrument = instrument;
            ownership?.RequirePath(path);
            if (ownership == null && instrument.FeedId is not (IbBarImport.FeedId or IbBarStore.BarFolder))
                _ownership = HistoryWriteOwnership.AcquirePath(path);
            FileStream? opened = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                bool exists = File.Exists(path) && new FileInfo(path).Length > 0;
                if (exists)
                {
                    using var existing = File.OpenRead(path);
                    Span<byte> header = stackalloc byte[4];
                    existing.ReadExactly(header);
                    _withProvenance = header.SequenceEqual(ProvenanceMagic);
                    if (!header.SequenceEqual(Magic) && !_withProvenance) throw new InvalidDataException("Invalid tick file header: " + path);
                }
                else _withProvenance = includeProvenance ?? false;
                if (exists && includeProvenance == true && !_withProvenance)
                    throw new InvalidOperationException("Upgrade the legacy day before appending provenance-aware records.");
                _fs = opened = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _bs = new BufferedStream(_fs, 1 << 16);
                _headerWritten = exists;
                if (!exists && includeProvenance.HasValue) WriteHeader(_withProvenance);
            }
            catch
            {
                opened?.Dispose();
                _ownership?.Dispose();
                throw;
            }
        }

        private void WriteHeader(bool withProvenance)
        {
            if (_headerWritten) return;
            _withProvenance = withProvenance;
            _bs.Write(_withProvenance ? ProvenanceMagic : Magic);
            var tickSize = Encoding.UTF8.GetBytes(_instrument.TickSize.ToString(CultureInfo.InvariantCulture));
            _bs.WriteByte(checked((byte)tickSize.Length));
            _bs.Write(tickSize);
            _headerWritten = true;
        }

        public void Write(in Trade t)
        {
            if (t.Volume < 0) throw new ArgumentOutOfRangeException(nameof(t), "Trade volume cannot be negative.");
            if ((byte)t.Side > (byte)TradeSide.Sell) throw new ArgumentOutOfRangeException(nameof(t), "Invalid trade side.");
            var utc = t.Time.ToUniversalTime();
            var (mantissa, scale) = SplitDecimal(t.Volume, out bool legacyVolume);
            bool extended = (!_headerWritten || !_withProvenance) && (RequiresExtendedFields(t, utc) || !legacyVolume);
            if (_headerWritten && !_withProvenance && extended)
                throw new InvalidOperationException("Legacy VTK1 cannot store this event losslessly; upgrade the day before appending.");
            bool known = false;
            int index = 0;
            int eventIdLength = 0;
            var palette = t.Provenance with { SourceEventId = null };
            byte[] source = Array.Empty<byte>();
            if ((_headerWritten && _withProvenance) || (!_headerWritten && extended))
            {
                if (!string.IsNullOrEmpty(t.Provenance.SourceEventId))
                {
                    eventIdLength = ProvenanceEncoding.GetByteCount(t.Provenance.SourceEventId);
                    if (eventIdLength > MaximumEventIdBytes) throw new InvalidDataException("Invalid tick source-event identity length.");
                }
                known = _provenance.TryGetValue(palette, out index);
                if (!known)
                {
                    ValidateProvenance(palette);
                    source = ProvenanceEncoding.GetBytes(palette.SourceIdentity ?? "");
                    if (source.Length > 16_384) throw new InvalidDataException("Invalid tick provenance source length.");
                }
            }
            long ms = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
            long price = _instrument.ToTicks(t.Price);
            if (!_withProvenance && !extended && price <= 0)
                throw new InvalidOperationException("Legacy VTK1 cannot store a nonpositive tick price; use a provenance-aware day.");
            WriteHeader(extended);
            long idDelta = unchecked(t.Id - _prevId), priceDelta = unchecked(price - _prevPrice);
            bool priceDeltaOverflow = price >= _prevPrice ? priceDelta < 0 : priceDelta > 0;
            bool key = !_hasPrev || ms < _prevMs || _sinceKey >= 4096 || t.Id < _prevId || idDelta < 0 || priceDeltaOverflow ||
                (t.Id == _prevId && price != _prevPrice);
            if (!_withProvenance && _hasPrev && _prevPrice > 0)
            {
                long maximumDelta = Math.Max(10, _prevPrice / 33);
                key |= priceDelta < -maximumDelta || priceDelta > maximumDelta;
            }
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
                n += WriteVarint(buf[n..], ZigZag(idDelta));
                n += WriteVarint(buf[n..], (ulong)(ms - _prevMs));
                n += WriteVarint(buf[n..], ZigZag(priceDelta));
                _sinceKey++;
            }

            n += WriteVarint(buf[n..], mantissa);
            _bs.Write(buf[..n]);
            if (_withProvenance)
            {
                if (!known) { index = _provenance.Count; _provenance.Add(palette, index); }
                Span<byte> metadata = stackalloc byte[48];
                int size = WriteVarint(metadata, (ulong)index * 2 + (known ? 0UL : 1UL));
                _bs.Write(metadata[..size]);
                if (!known)
                {
                    _bs.WriteByte((byte)t.Provenance.Quality);
                    size = WriteVarint(metadata, (ulong)t.Provenance.ResolutionSeconds);
                    size += WriteVarint(metadata[size..], ZigZag(t.Provenance.CorrectionRevision));
                    size += WriteVarint(metadata[size..], (ulong)t.Provenance.ObservedTradeCount);
                    size += WriteVarint(metadata[size..], (ulong)source.Length);
                    _bs.Write(metadata[..size]);
                    _bs.Write(source);
                    _bs.WriteByte(t.Provenance.HasKnownAvailability ? (byte)1 : (byte)0);
                    size = WriteVarint(metadata, (ulong)t.Provenance.ResolutionOffsetSeconds);
                    _bs.Write(metadata[..size]);
                }
                Span<byte> exact = stackalloc byte[24];
                BinaryPrimitives.WriteInt64LittleEndian(exact, utc.Ticks);
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(t.Volume, bits);
                for (int part = 0; part < bits.Length; part++) BinaryPrimitives.WriteInt32LittleEndian(exact[(8 + part * 4)..], bits[part]);
                _bs.Write(exact);
                WriteSourceEventId(t.Provenance.SourceEventId, eventIdLength);
            }
            _hasPrev = true;
            _prevId = t.Id;
            _prevMs = ms;
            _prevPrice = price;
        }

        private void WriteSourceEventId(string? eventId, int byteCount)
        {
            Span<byte> length = stackalloc byte[10];
            int size = WriteVarint(length, (ulong)byteCount);
            _bs.Write(length[..size]);
            if (byteCount == 0) return;
            byte[]? rented = null;
            Span<byte> bytes = stackalloc byte[ShortEventIdBufferSize];
            if (byteCount > bytes.Length) bytes = rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                ProvenanceEncoding.GetBytes(eventId.AsSpan(), bytes);
                _bs.Write(bytes[..byteCount]);
            }
            finally { if (rented != null) ArrayPool<byte>.Shared.Return(rented); }
        }

        public bool IncludesProvenance => _withProvenance;

        public void Flush()
        {
            WriteHeader(withProvenance: false);
            _bs.Flush();
            _fs.Flush();
        }

        internal void FlushToDisk()
        {
            WriteHeader(withProvenance: false);
            _bs.Flush();
            StorageSync.Flush(_fs);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { WriteHeader(withProvenance: false); _bs.Flush(); }
            finally
            {
                try { _bs.Dispose(); }
                finally
                {
                    try { _fs.Dispose(); }
                    finally { _ownership?.Dispose(); }
                }
            }
        }
    }

    /// <summary>Whether a legacy record would discard information from this event.</summary>
    internal static bool RequiresExtended(in Trade trade) =>
        RequiresExtendedFields(trade, trade.Time.ToUniversalTime()) || !IsLegacyVolume(trade.Volume);

    private static bool RequiresExtendedFields(in Trade trade, DateTime utc) =>
        trade.Id < 0 || trade.Price <= 0 || utc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
        trade.Provenance != new TradeProvenance(DataQuality.Raw);

    private static bool IsLegacyVolume(decimal volume)
    {
        volume = Math.Abs(volume) / 1.0000000000000000000000000000m;
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(volume, bits);
        return bits[2] == 0;
    }

    private static void ValidateProvenance(in TradeProvenance provenance)
    {
        const DataQuality kinds = DataQuality.Raw | DataQuality.Compact | DataQuality.Synthetic;
        const DataQuality supported = kinds | DataQuality.InferredSide | DataQuality.Partial;
        if ((provenance.Quality & ~supported) != 0 || (provenance.Quality & kinds) == 0 ||
            ((provenance.Quality & DataQuality.Raw) != 0 && (provenance.Quality & (DataQuality.Compact | DataQuality.Synthetic)) != 0) ||
            provenance.ObservedTradeCount < 0 || provenance.ResolutionSeconds < 0 || provenance.ResolutionOffsetSeconds < 0 ||
            (provenance.ResolutionSeconds == 0 ? provenance.ResolutionOffsetSeconds != 0 : provenance.ResolutionOffsetSeconds >= provenance.ResolutionSeconds))
            throw new InvalidDataException("Invalid tick provenance metadata.");
    }

    private static string ShareSource(string source, HashSet<string> sources)
    {
        if (sources.TryGetValue(source, out var shared)) return shared;
        sources.Add(source);
        return source;
    }

    private static void ValidateExactPayload(DateTime time, decimal volume, long milliseconds, ulong mantissa, int scale)
    {
        if (new DateTimeOffset(time).ToUnixTimeMilliseconds() != milliseconds || volume < 0)
            throw new InvalidDataException("Exact tick payload conflicts with its compact fields.");
        var expected = SplitDecimal(volume);
        if (expected.Mantissa != mantissa || expected.Scale != scale)
            throw new InvalidDataException("Exact tick volume conflicts with its compact fields.");
    }

    /// <summary>Tamaño del tick guardado en el encabezado del archivo (null si no se puede leer).</summary>
    public static decimal? ReadTickSize(string path)
    {
        try
        {
            using var source = File.OpenRead(path);
            bool archive = path.EndsWith(".br", StringComparison.Ordinal);
            using var fs = archive ? OpenInput(source, archive: true) : new NonOwningStream(source);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4 || (!magic.SequenceEqual(Magic) && !magic.SequenceEqual(ProvenanceMagic))) return null;
            int len = fs.ReadByte();
            if (len <= 0) return null;
            var buf = new byte[len];
            fs.ReadExactly(buf);
            var tick = decimal.Parse(Encoding.UTF8.GetString(buf), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (archive)
            {
                Span<byte> buffer = stackalloc byte[16_384];
                while (fs.Read(buffer) > 0) { } // Verify the complete archived digest without materializing the raw day.
            }
            return tick;
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

    internal static List<Trade> ReadCaptured(string path, out string contentIdentity)
    {
        string captured = "missing";
        var rows = ReadCore(path, out _, requireComplete: false, allowIncompleteHeader: false,
            captureIdentity: identity => captured = identity);
        contentIdentity = captured;
        return rows;
    }

    internal static string ContentIdentity(string path, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool archive = path.EndsWith(".br", StringComparison.Ordinal);
        long remaining = archive ? long.MaxValue : file.Length;
        if (!archive && remaining > TickArchive.MaximumExpandedBytes)
            throw new InvalidDataException("Tick day exceeds the bounded read size: " + path);
        using var input = OpenInput(file, archive);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0)
                {
                    if (!archive && remaining > 0) throw new EndOfStreamException("Tick prefix changed while being read: " + path);
                    break;
                }
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>Read an append snapshot only while the caller has established another writer owns the instrument lease.</summary>
    internal static List<Trade> ReadAppendPrefix(string path, out int dropped, decimal? expectedTickSize = null) =>
        ReadCore(path, out dropped, requireComplete: false, allowIncompleteHeader: true, expectedTickSize);

    internal static void EnsureProvenanceFormat(string path, Instrument instrument)
    {
        using var ownership = instrument.FeedId is IbBarImport.FeedId or IbBarStore.BarFolder
            ? null : HistoryWriteOwnership.AcquirePath(path);
        EnsureProvenanceFormatCore(path, instrument, ownership);
    }

    internal static void EnsureProvenanceFormat(string path, Instrument instrument, HistoryWriteOwnership ownership)
    {
        ownership.RequirePath(path);
        EnsureProvenanceFormatCore(path, instrument, ownership);
    }

    private static void EnsureProvenanceFormatCore(string path, Instrument instrument, HistoryWriteOwnership? ownership)
    {
        if (!File.Exists(path)) return;
        string? temporary = null;
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Span<byte> magic = stackalloc byte[4];
                try { source.ReadExactly(magic); }
                catch (EndOfStreamException error) { throw new InvalidDataException("Truncated tick-file header: " + path, error); }
                if (magic.SequenceEqual(ProvenanceMagic)) return;
                if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Invalid tick-file header: " + path);
                if (source.Length > TickArchive.MaximumExpandedBytes) throw new InvalidDataException("Tick day exceeds the bounded read size: " + path);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".upgrade";
                source.Position = 0;
                using var input = new BufferedStream(new NonOwningStream(source), 64 * 1024);
                using var writer = ownership == null ? new Writer(temporary, instrument, includeProvenance: true)
                    : new Writer(temporary, instrument, ownership, includeProvenance: true);
                foreach (var trade in EnumerateCore(input, path, allowIncompleteTail: false, requireOrdered: false, expectedTickSize: instrument.TickSize))
                    writer.Write(trade);
            }
            if (new FileInfo(temporary).Length > TickArchive.MaximumExpandedBytes)
                throw new InvalidDataException("The upgraded tick day exceeds the bounded read size; the original remains authoritative: " + path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
    }

    private static List<Trade> ReadCore(string path, out int dropped, bool requireComplete, bool allowIncompleteHeader, decimal? expectedTickSize = null, Action<string>? captureIdentity = null)
    {
        dropped = 0;
        var result = new List<Trade>();
        if (!File.Exists(path)) return result;
        byte[] data;
        if (path.EndsWith(".br", StringComparison.Ordinal)) data = TickArchive.ReadBytes(path);
        else
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > TickArchive.MaximumExpandedBytes) throw new InvalidDataException("Tick day exceeds the bounded read size: " + path);
            data = new byte[(int)fs.Length];
            fs.ReadExactly(data);
        }
        captureIdentity?.Invoke(Convert.ToHexString(SHA256.HashData(data)));
        bool withProvenance = data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(ProvenanceMagic);
        if (allowIncompleteHeader)
        {
            int prefix = Math.Min(data.Length, Magic.Length);
            if (!data.AsSpan(0, prefix).SequenceEqual(Magic.AsSpan(0, prefix)) && !data.AsSpan(0, prefix).SequenceEqual(ProvenanceMagic.AsSpan(0, prefix)))
                throw new InvalidDataException("Invalid tick-file magic: " + path);
            if (data.Length < 5) return result; // A real writer has not flushed its complete initial header yet.
        }
        else if (data.Length < 5 || (!data.AsSpan(0, 4).SequenceEqual(Magic) && !withProvenance))
        {
            if (requireComplete) throw new InvalidDataException("Invalid tick-file header: " + path);
            return result;
        }
        int pos = 4;
        int tsLen = data[pos++];
        if (allowIncompleteHeader && tsLen == 0) throw new InvalidDataException("Invalid tick-size header: " + path);
        if (tsLen == 0 || pos + tsLen > data.Length)
        {
            if (requireComplete) throw new InvalidDataException("Truncated tick-size header: " + path);
            return result;
        }
        if (!decimal.TryParse(Encoding.UTF8.GetString(data, pos, tsLen), NumberStyles.Float, CultureInfo.InvariantCulture, out var tickSize) || tickSize <= 0)
            throw new InvalidDataException("Invalid tick-size header: " + path);
        pos += tsLen;
        if (expectedTickSize.HasValue && tickSize != expectedTickSize) throw new InvalidDataException("Tick metadata differs from the catalog: " + path);

        // el archivo es de un día UTC: un tiempo fuera de ese día es basura
        long minMs = long.MinValue, maxMs = long.MaxValue;
        var dayName = Path.GetFileNameWithoutExtension(path.EndsWith(".br", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(path) : path);
        bool dated = DateOnly.TryParseExact(dayName, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day);
        if (dated)
        {
            minMs = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds() - 60_000;
            maxMs = minMs + 86_400_000 + 120_000;
        }

        var keys = new List<int>();
        List<int>? bad = null;
        long prevId = 0, prevMs = 0, prevPrice = 0;
        long firstId = -1; // el primer registro del archivo siempre es un keyframe sano
        var provenance = new Dictionary<int, TradeProvenance>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
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
                    id = unchecked((long)ReadVarint(data, ref pos, withProvenance || allowIncompleteHeader));
                    ms = unchecked((long)ReadVarint(data, ref pos, withProvenance || allowIncompleteHeader));
                    price = UnZigZag(ReadVarint(data, ref pos, withProvenance || allowIncompleteHeader));
                }
                else
                {
                    dId = UnZigZag(ReadVarint(data, ref pos, withProvenance || allowIncompleteHeader));
                    id = withProvenance ? checked(prevId + dId) : unchecked(prevId + dId);
                    ulong elapsed = ReadVarint(data, ref pos, withProvenance || allowIncompleteHeader);
                    ms = withProvenance ? checked(prevMs + checked((long)elapsed)) : unchecked(prevMs + (long)elapsed);
                    dPrice = UnZigZag(ReadVarint(data, ref pos, withProvenance || allowIncompleteHeader));
                    price = withProvenance ? checked(prevPrice + dPrice) : unchecked(prevPrice + dPrice);
                }

                ulong mant = ReadVarint(data, ref pos, withProvenance || allowIncompleteHeader);
                TradeProvenance quality = default;
                DateTime? exactTime = null;
                decimal? exactVolume = null;
                if (withProvenance)
                {
                    ulong token = ReadVarint(data, ref pos, rejectMalformed: true);
                    int index = checked((int)(token >> 1));
                    if ((token & 1) != 0)
                    {
                        var flagsQuality = (DataQuality)data[pos++];
                        int resolution = checked((int)ReadVarint(data, ref pos, true));
                        long revision = UnZigZag(ReadVarint(data, ref pos, true));
                        int count = checked((int)ReadVarint(data, ref pos, true));
                        int length = checked((int)ReadVarint(data, ref pos, true));
                        if (length > 16_384 || length > data.Length - pos) throw new InvalidDataException("Invalid tick provenance source.");
                        string? source = length == 0 ? null : ShareSource(ProvenanceEncoding.GetString(data, pos, length), sources);
                        pos += length;
                        byte knownAvailability = data[pos++];
                        if (knownAvailability > 1) throw new InvalidDataException("Invalid tick availability flag.");
                        int phase = checked((int)ReadVarint(data, ref pos, true));
                        quality = new TradeProvenance(flagsQuality, resolution, source, revision, count, knownAvailability != 0, phase);
                        ValidateProvenance(quality);
                        provenance[index] = quality;
                    }
                    else if (!provenance.TryGetValue(index, out quality)) throw new InvalidDataException("Invalid tick provenance reference.");
                    if (data.Length - pos < 24)
                    {
                        if (requireComplete) throw new InvalidDataException("Truncated exact tick payload: " + path);
                        break;
                    }
                    exactTime = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos, 8)), DateTimeKind.Utc);
                    int lo = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 8, 4)), mid = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 12, 4));
                    int hi = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 16, 4)), decimalFlags = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 20, 4));
                    if ((decimalFlags & 0x7F00FFFF) != 0) throw new InvalidDataException("Invalid exact tick volume flags.");
                    exactVolume = new decimal(lo, mid, hi, (decimalFlags & int.MinValue) != 0, (byte)(decimalFlags >> 16));
                    ValidateExactPayload(exactTime.Value, exactVolume.Value, ms, mant, scale);
                    pos += 24;
                    int eventIdLength = checked((int)ReadVarint(data, ref pos, rejectMalformed: true));
                    if (eventIdLength > MaximumEventIdBytes) throw new InvalidDataException("Invalid tick source-event identity length.");
                    if (eventIdLength > data.Length - pos)
                    {
                        if (requireComplete) throw new InvalidDataException("Truncated tick source-event identity: " + path);
                        break;
                    }
                    quality = quality with { SourceEventId = eventIdLength == 0 ? null : ProvenanceEncoding.GetString(data.AsSpan(pos, eventIdLength)) };
                    pos += eventIdLength;
                }
                if (pos > data.Length)
                {
                    if (requireComplete) throw new InvalidDataException("Truncated tick record: " + path);
                    break;
                }

                if (firstId < 0) firstId = id;

                // un escritor solo nunca deja estos registros: si aparecen, el archivo está mezclado.
                // Un keyframe con un id de otra escala es un registro leído desde la mitad (varint cortado).
                bool ok = sideBits != 3 && scale <= 28 && (result.Count > 0 || key);
                if (withProvenance)
                    ok &= !dated || DateOnly.FromDateTime(exactTime!.Value) == day;
                else
                    ok &= price > 0 && ms >= minMs && ms < maxMs
                          && (!key || firstId < 1L << 28 || (id > firstId / 64 && id / 64 < firstId))
                          && (key || dId > 0 || (dId == 0 && dPrice == 0))
                          && (key || prevPrice <= 0 || (dPrice >= -Math.Max(10, prevPrice / 33) && dPrice <= Math.Max(10, prevPrice / 33)));
                if (key) keys.Add(result.Count);
                if (ok)
                {
                    var vol = exactVolume ?? new decimal((int)(mant & 0xFFFFFFFF), (int)(mant >> 32), 0, false, (byte)scale);
                    var trade = new Trade(id, exactTime ?? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, price * tickSize, vol, (TradeSide)sideBits, quality);
                    if (withProvenance && trade.Provenance != quality) throw new InvalidDataException("Tick provenance conflicts with its identity: " + path);
                    result.Add(trade);
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
        catch (Exception error) when (error is OverflowException or ArgumentException)
        {
            throw new InvalidDataException("Invalid tick record: " + path, error);
        }

        if (bad == null) return result;
        if (requireComplete) throw new InvalidDataException("Corrupt or conflicting tick records: " + path);

        // fase 1: tramos alrededor de los registros imposibles
        var drop = Zones(bad, keys, result.Count);

        // fase 2: mismo id con datos distintos fuera de esos tramos: la cadena de diferencias de un escritor quedó
        // aplicada sobre la base de otro (los dos grababan el mismo vivo). De cada par se descarta la copia más
        // cercana a un tramo dañado (la otra es la buena); si no se puede saber, las dos.
        if (!withProvenance && !Increasing(result, drop))
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
    /// Rewrites damaged history under physical day ownership and returns the number of removed records.
    /// </summary>
    public static int Repair(string path, Instrument instrument)
    {
        if (instrument.FeedId is IbBarImport.FeedId or IbBarStore.BarFolder)
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(full)));
            if (root == null || !DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(full), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ||
                full != Path.GetFullPath(DayPath(root, instrument.FeedId, instrument.Symbol, day)))
                throw new InvalidDataException("IB repair requires its standard owned day path.");
            return IbBarStore.RepairDay(root, instrument, day);
        }
        using var ownership = HistoryWriteOwnership.AcquirePath(path);
        return Repair(path, instrument, ownership);
    }

    internal static int Repair(string path, Instrument instrument, HistoryWriteOwnership ownership)
    {
        ownership.RequirePath(path);
        var trades = Read(path, out int dropped);
        if (dropped == 0) return 0;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".repair";
        try
        {
            using (var w = new Writer(tmp, instrument, ownership, includeProvenance: true))
                foreach (var t in trades) w.Write(t);
            File.Move(tmp, path, overwrite: true);
            return dropped;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    /// <summary>
    /// Exclusive recorder identity, also retained by whole-symbol imports. Non-IB physical mutations
    /// additionally own their affected days through HistoryWriteOwnership.
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
    internal static bool IsSharingViolation(IOException error) => (error.HResult & 0xFFFF) is 11 or 35 or 32 or 33;

    /// <summary>Cross-process proof of an actual append recorder, not merely an import/replacement lease.</summary>
    internal static bool HasActiveRecordingWriter(string root, string feedId, string symbol)
    {
        string path = Path.Combine(root, Sanitize(feedId), Sanitize(symbol), ".recording-active.lock");
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException error) when (IsSharingViolation(error)) { return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
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

    /// <summary>Compact the last occurrence of each canonical identity in place, then order by UTC time and numeric ID with stable surviving ties.</summary>
    public static List<Trade> SortAndDedupe(List<Trade> trades)
    {
        if (trades.Count < 2) return trades;
        bool legacyUnique = true, sorted = true;
        {
            var rows = CollectionsMarshal.AsSpan(trades);
            for (int i = 0; i < rows.Length; i++)
            {
                ref readonly var trade = ref rows[i];
                if (!string.IsNullOrEmpty(trade.Provenance.SourceEventId) || (i > 0 && trade.Id <= rows[i - 1].Id))
                {
                    legacyUnique = false;
                    break;
                }
                if (i > 0 && trade.Time < rows[i - 1].Time) sorted = false;
            }
        }
        if (legacyUnique)
        {
            if (!sorted) trades.Sort(static (left, right) => CompareTradeLocations(left, right));
            return trades;
        }

        var latest = new Dictionary<TradeIdentity, int>(trades.Count);
        int write = 0;
        sorted = true;
        {
            var rows = CollectionsMarshal.AsSpan(trades);
            for (int read = 0; read < rows.Length; read++) latest[TradeIdentity.From(in rows[read])] = read;
            for (int read = 0; read < rows.Length; read++)
            {
                ref readonly var trade = ref rows[read];
                if (latest[TradeIdentity.From(trade)] != read) continue;
                if (write > 0 && CompareTradeLocations(in rows[write - 1], in trade) > 0) sorted = false;
                if (write != read) rows[write] = trade;
                write++;
            }
        }
        if (write != trades.Count) trades.RemoveRange(write, trades.Count - write);
        if (!sorted)
        {
            var ordinals = latest;
            trades.Sort((left, right) =>
            {
                int order = CompareTradeLocations(left, right);
                return order != 0 ? order : ordinals[TradeIdentity.From(left)].CompareTo(ordinals[TradeIdentity.From(right)]);
            });
        }
        return trades;
    }

    private static int CompareTradeLocations(in Trade left, in Trade right)
    {
        int order = left.Time.CompareTo(right.Time);
        return order != 0 ? order : left.Id.CompareTo(right.Id);
    }

    internal static (ulong Mantissa, int Scale) SplitDecimal(decimal d) => SplitDecimal(d, out _);

    private static (ulong Mantissa, int Scale) SplitDecimal(decimal d, out bool legacyVolume)
    {
        d = Math.Abs(d) / 1.0000000000000000000000000000m; // normaliza (quita ceros)
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(d, bits);
        int scale = (bits[3] >> 16) & 0xFF;
        legacyVolume = bits[2] == 0 && scale <= 28;
        if (!legacyVolume)
        {
            // fuera de rango: se redondea a 8 decimales
            d = Math.Round(d, 8);
            decimal.GetBits(d, bits);
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
            if (rejectMalformed && shift == 63 && b > 1)
                throw new InvalidDataException("Invalid overlong tick varint.");
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
