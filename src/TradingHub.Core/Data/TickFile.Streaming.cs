using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using TradingHub.Market;

namespace TradingHub.Data;

public static partial class TickFile
{
    /// <summary>Bounded-memory strict scan. Ordered consumers require chronology; syntax scans preserve every complete stored event.</summary>
    public static IEnumerable<Trade> Enumerate(string path, bool allowIncompleteTail = false, bool requireOrdered = true)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > TickArchive.MaximumExpandedBytes && !path.EndsWith(".br", StringComparison.Ordinal))
            throw new InvalidDataException("Tick day exceeds the bounded read size: " + path);
        using var input = new BufferedStream(OpenInput(file, path.EndsWith(".br", StringComparison.Ordinal)), 64 * 1024);
        foreach (var trade in EnumerateCore(input, path, allowIncompleteTail, requireOrdered)) yield return trade;
    }

    internal static byte[] ValidateStream(Stream stream, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var input = new BufferedStream(new HashingReadStream(stream, hash), 64 * 1024);
        foreach (var _ in EnumerateCore(input, path, allowIncompleteTail: false, requireOrdered: false, ct)) { }
        return hash.GetHashAndReset();
    }

    internal static Stream OpenArchiveStream(Stream stream) => OpenInput(stream, archive: true);

    private static IEnumerable<Trade> EnumerateCore(Stream input, string path, bool allowIncompleteTail, bool requireOrdered, CancellationToken ct = default, decimal? expectedTickSize = null)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        byte[] magic = reader.ReadBytes(4);
        if (allowIncompleteTail && magic.Length < 4 &&
            (magic.AsSpan().SequenceEqual(Magic.AsSpan(0, magic.Length)) || magic.AsSpan().SequenceEqual(ProvenanceMagic.AsSpan(0, magic.Length)))) yield break;
        bool metadata = magic.AsSpan().SequenceEqual(ProvenanceMagic);
        if (!metadata && !magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Invalid tick-file header: " + path);
        int length = input.ReadByte();
        if (length < 0 && allowIncompleteTail) yield break;
        if (length <= 0) throw new InvalidDataException("Invalid tick-size header: " + path);
        byte[] header = reader.ReadBytes(length);
        if (header.Length < length && allowIncompleteTail) yield break;
        if (header.Length != length || !decimal.TryParse(Encoding.UTF8.GetString(header), NumberStyles.Float, CultureInfo.InvariantCulture, out var tick) || tick <= 0)
            throw new InvalidDataException("Invalid tick-size header: " + path);
        if (expectedTickSize.HasValue && tick != expectedTickSize.Value) throw new InvalidDataException("Tick metadata differs from the writer instrument: " + path);
        var definitions = new Dictionary<int, TradeProvenance>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var segment = new List<Trade>(4097);
        long previousId = 0, previousTime = 0, previousPrice = 0, firstId = 0;
        Trade previous = default;
        TradeIdentity previousIdentity = default;
        bool hasPrevious = false;
        var name = Path.GetFileNameWithoutExtension(path.EndsWith(".br", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(path) : path);
        bool dated = DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int flags = input.ReadByte();
            if (flags < 0) break;
            bool key = (flags & 4) != 0;
            if (!key && !hasPrevious) throw new InvalidDataException("Missing initial tick keyframe: " + path);
            if (key && segment.Count > 0)
            {
                foreach (var trade in segment) yield return trade;
                segment.Clear();
            }
            bool interrupted = false;
            try
            {
                long idDelta = 0, priceDelta = 0;
                long id = key ? unchecked((long)ReadUnsigned(reader)) : checked(previousId + (idDelta = UnZigZag(ReadUnsigned(reader))));
                long ms = key ? unchecked((long)ReadUnsigned(reader)) : checked(previousTime + checked((long)ReadUnsigned(reader)));
                long price = key ? UnZigZag(ReadUnsigned(reader)) : checked(previousPrice + (priceDelta = UnZigZag(ReadUnsigned(reader))));
                ulong mantissa = ReadUnsigned(reader);
                int scale = flags >> 3;
                if ((flags & 3) == 3 || scale > 28) throw new InvalidDataException("Invalid tick flags: " + path);
                if (!hasPrevious) firstId = id;
                if (!metadata)
                {
                    long jump = Math.Max(10, previousPrice / 33);
                    if (price <= 0 || (key && firstId >= 1L << 28 && (id <= firstId / 64 || id / 64 >= firstId)) ||
                        (!key && (idDelta < 0 || (idDelta == 0 && priceDelta != 0))) ||
                        (!key && previousPrice > 0 && (priceDelta < -jump || priceDelta > jump)))
                        throw new InvalidDataException("Corrupt legacy tick segment: " + path);
                }
                TradeProvenance provenance = default;
                DateTime? exactTime = null;
                decimal? exactVolume = null;
                if (metadata)
                {
                    ulong token = ReadUnsigned(reader);
                    int index = checked((int)(token >> 1));
                    if ((token & 1) != 0)
                    {
                        var quality = (DataQuality)reader.ReadByte();
                        int resolution = checked((int)ReadUnsigned(reader));
                        long correction = UnZigZag(ReadUnsigned(reader));
                        int count = checked((int)ReadUnsigned(reader)), bytes = checked((int)ReadUnsigned(reader));
                        if (bytes > 16_384) throw new InvalidDataException("Invalid provenance source length.");
                        var sourceBytes = reader.ReadBytes(bytes);
                        if (sourceBytes.Length != bytes) throw new EndOfStreamException();
                        string? source = bytes == 0 ? null : ShareSource(ProvenanceEncoding.GetString(sourceBytes), sources);
                        byte knownAvailability = reader.ReadByte();
                        if (knownAvailability > 1) throw new InvalidDataException("Invalid tick availability flag.");
                        int phase = checked((int)ReadUnsigned(reader));
                        provenance = new TradeProvenance(quality, resolution, source, correction, count, knownAvailability != 0, phase);
                        ValidateProvenance(provenance);
                        definitions[index] = provenance;
                    }
                    else if (!definitions.TryGetValue(index, out provenance)) throw new InvalidDataException("Invalid provenance reference.");
                    exactTime = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                    exactVolume = reader.ReadDecimal();
                    ValidateExactPayload(exactTime.Value, exactVolume.Value, ms, mantissa, scale);
                    provenance = provenance with { SourceEventId = ReadSourceEventId(reader) };
                }
                var time = exactTime ?? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                if (dated && DateOnly.FromDateTime(time) != day) throw new InvalidDataException("A tick is outside its UTC day: " + path);
                var volume = exactVolume ?? new decimal((int)mantissa, (int)(mantissa >> 32), 0, false, (byte)scale);
                var current = new Trade(id, time, price * tick, volume, (TradeSide)(flags & 3), provenance);
                if (metadata && current.Provenance != provenance) throw new InvalidDataException("Tick provenance conflicts with its identity: " + path);
                if (requireOrdered)
                {
                    var identity = TradeIdentity.From(current);
                    if (hasPrevious)
                    {
                        bool sameRawSource = current.IsObservedTrade && previous.IsObservedTrade &&
                            current.Provenance.SourceIdentity == previous.Provenance.SourceIdentity;
                        bool legacyTie = sameRawSource && string.IsNullOrEmpty(current.Provenance.SourceEventId) &&
                            string.IsNullOrEmpty(previous.Provenance.SourceEventId);
                        if (time < previous.Time || identity == previousIdentity || (!metadata && id <= previousId) ||
                            (time == previous.Time && legacyTie && id < previousId))
                            throw new InvalidDataException("Tick day requires chronological correction before ordered streaming: " + path);
                    }
                    previousIdentity = identity;
                    previous = current;
                }
                segment.Add(current);
                if (segment.Count > 4097) throw new InvalidDataException("Missing bounded tick keyframe: " + path);
                previousId = id; previousTime = ms; previousPrice = price; hasPrevious = true;
            }
            catch (EndOfStreamException) when (allowIncompleteTail) { interrupted = true; }
            catch (Exception error) when (error is IOException or OverflowException or ArgumentException)
            { throw new InvalidDataException("Truncated or invalid tick record: " + path, error); }
            if (interrupted) yield break;
        }
        foreach (var trade in segment) yield return trade;
    }

    private static Stream OpenInput(Stream file, bool archive)
    {
        if (!archive) return new NonOwningStream(file);
        using var header = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (!header.ReadBytes(4).AsSpan().SequenceEqual("VTA1"u8)) throw new InvalidDataException("Invalid tick archive.");
        long length = header.ReadInt64();
        var digest = header.ReadBytes(32);
        if (length < 5 || length > TickArchive.MaximumExpandedBytes || digest.Length != 32) throw new InvalidDataException("Invalid tick archive length.");
        return new VerifiedArchiveStream(new BrotliStream(file, CompressionMode.Decompress, leaveOpen: true), length, digest);
    }

    private static string? ReadSourceEventId(BinaryReader reader)
    {
        int length = checked((int)ReadUnsigned(reader));
        if (length > MaximumEventIdBytes) throw new InvalidDataException("Invalid tick source-event identity length.");
        if (length == 0) return null;
        byte[]? rented = null;
        Span<byte> bytes = stackalloc byte[ShortEventIdBufferSize];
        if (length > bytes.Length) bytes = rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            reader.BaseStream.ReadExactly(bytes[..length]);
            return ProvenanceEncoding.GetString(bytes[..length]);
        }
        finally { if (rented != null) ArrayPool<byte>.Shared.Return(rented); }
    }

    private static ulong ReadUnsigned(BinaryReader reader)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte current = reader.ReadByte();
            if (shift == 63 && current > 1) throw new InvalidDataException("Overlong tick varint.");
            value |= (ulong)(current & 127) << shift;
            if ((current & 128) == 0) return value;
        }
        throw new InvalidDataException("Overlong tick varint.");
    }

    private class NonOwningStream(Stream inner) : Stream
    {
        protected Stream Inner { get; } = inner;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => Inner.Read(buffer);
        public override int ReadByte()
        {
            Span<byte> value = stackalloc byte[1];
            return Read(value) == 0 ? -1 : value[0];
        }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HashingReadStream(Stream inner, IncrementalHash hash) : NonOwningStream(inner)
    {
        public override int Read(Span<byte> buffer)
        {
            int count = base.Read(buffer);
            hash.AppendData(buffer[..count]);
            return count;
        }
    }

    private sealed class VerifiedArchiveStream(Stream inner, long expectedLength, byte[] expectedDigest) : NonOwningStream(inner)
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _read;
        private bool _verified;
        public override int Read(Span<byte> buffer)
        {
            int count = base.Read(buffer);
            _read += count;
            _hash.AppendData(buffer[..count]);
            if (_read > expectedLength) throw new InvalidDataException("Tick archive exceeds its declared size.");
            if (count == 0 && !_verified)
            {
                _verified = true;
                if (_read != expectedLength || !CryptographicOperations.FixedTimeEquals(_hash.GetHashAndReset(), expectedDigest)) throw new InvalidDataException("Truncated or corrupt tick archive.");
            }
            return count;
        }
        protected override void Dispose(bool disposing) { if (disposing) { _hash.Dispose(); Inner.Dispose(); } base.Dispose(disposing); }
    }
}
