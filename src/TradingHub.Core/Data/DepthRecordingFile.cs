using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Private versioned append segments. Existing market/tick history is never rewritten.</summary>
internal static class DepthRecordingFile
{
    private const int Magic = 0x50444854; // THDP
    private const int Version = 1;
    private const int MaximumFrameBytes = 16 * 1024 * 1024;
    private static readonly uint[] CrcTable = MakeCrcTable();

    internal static string DirectoryFor(string root, Instrument instrument)
    {
        string identity = instrument.FeedId + "\0" + instrument.Symbol + "\0" + instrument.TickSize.ToString(CultureInfo.InvariantCulture);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(root, "Depth", "v1", hash);
    }

    internal static long HeaderSize(Instrument instrument) =>
        24L + EncodedStringSize(instrument.FeedId) + EncodedStringSize(instrument.Symbol);

    private static long EncodedStringSize(string text)
    {
        uint bytes = checked((uint)Encoding.UTF8.GetByteCount(text));
        int prefix = 1;
        for (uint rest = bytes; rest >= 128; rest >>= 7) prefix++;
        return bytes + (long)prefix;
    }

    internal static void WriteHeader(BinaryWriter writer, Instrument instrument)
    {
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(instrument.FeedId);
        writer.Write(instrument.Symbol);
        writer.Write(instrument.TickSize);
    }

    internal static void WritePayload(BinaryWriter writer, DepthSample sample)
    {
        var update = sample.Update;
        Span<byte> recordingId = stackalloc byte[16];
        sample.RecordingId.TryWriteBytes(recordingId);
        writer.Write(recordingId);
        writer.Write(sample.Ordinal);
        writer.Write(sample.Time.Ticks);
        writer.Write(sample.SourceTime.Ticks);
        writer.Write(sample.ProviderId);
        writer.Write(sample.Venue);
        WriteNullable(writer, update.Sequence);
        WriteNullable(writer, update.FirstSequence);
        WriteNullable(writer, update.PreviousSequence);
        writer.Write((int)update.Coverage);
        writer.Write((byte)update.GapBefore);
        writer.Write(update.IsFullRefresh);
        writer.Write(update.Message ?? "");
        WriteLevels(writer, sample.Snapshot.Bids);
        WriteLevels(writer, sample.Snapshot.Asks);
        writer.Write(update.Changes.Count);
        foreach (var change in update.Changes)
        {
            writer.Write((byte)change.Side);
            writer.Write(change.PriceTicks);
            writer.Write(change.Size);
            writer.Write((byte)change.Origin);
        }
    }

    internal static void WriteFrame(BinaryWriter writer, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumFrameBytes) throw new InvalidDataException("The received depth record exceeds the safe frame limit.");
        writer.Write(payload.Length);
        writer.Write(Crc32(payload));
        writer.Write(payload);
    }

    internal static IEnumerable<DepthSample> Read(string path, Instrument instrument, long maxSampleBytes, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version || !ReadIdentity(reader, instrument.FeedId) ||
            !ReadIdentity(reader, instrument.Symbol) || reader.ReadDecimal() != instrument.TickSize)
            throw new InvalidDataException("The depth segment has an unsupported format or a different instrument identity.");
        while (file.Position < file.Length)
        {
            ct.ThrowIfCancellationRequested();
            int length = reader.ReadInt32();
            uint checksum = reader.ReadUInt32();
            if (length < 0 || length > MaximumFrameBytes || length > file.Length - file.Position)
                throw new InvalidDataException("The depth segment ends with a missing or incomplete frame; its complete prefix remains readable.");
            if (length > maxSampleBytes + (maxSampleBytes + 1) / 2)
            {
                yield return ReadSkippedFrame(reader, length, checksum, ct);
                continue;
            }
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length || Crc32(bytes) != checksum)
                throw new InvalidDataException("The depth segment contains a damaged frame; no inferred book is substituted.");
            DepthSample decoded;
            try { decoded = Decode(bytes, maxSampleBytes); }
            catch (SampleLimitException)
            {
                using var prefix = new BinaryReader(new MemoryStream(bytes, writable: false));
                decoded = ReadSkippedPrefix(prefix);
            }
            yield return decoded;
        }
    }

    private sealed class SampleLimitException : Exception { }

    private static DepthSample ReadSkippedFrame(BinaryReader reader, int length, uint checksum, CancellationToken ct)
    {
        Span<byte> prefix = stackalloc byte[40];
        reader.BaseStream.ReadExactly(prefix);
        uint crc = UpdateCrc(uint.MaxValue, prefix);
        Span<byte> scratch = stackalloc byte[1024];
        for (int remaining = length - prefix.Length; remaining > 0;)
        {
            ct.ThrowIfCancellationRequested();
            int count = reader.Read(scratch[..Math.Min(scratch.Length, remaining)]);
            if (count == 0) throw new EndOfStreamException("The skipped depth frame is incomplete.");
            crc = UpdateCrc(crc, scratch[..count]);
            remaining -= count;
        }
        if (~crc != checksum) throw new InvalidDataException("The depth segment contains a damaged frame; no inferred book is substituted.");
        return ReadSkippedPrefix(prefix);
    }

    private static DepthSample ReadSkippedPrefix(BinaryReader reader)
    {
        Span<byte> prefix = stackalloc byte[40];
        reader.BaseStream.ReadExactly(prefix);
        return ReadSkippedPrefix(prefix);
    }

    private static DepthSample ReadSkippedPrefix(ReadOnlySpan<byte> prefix)
    {
        var recording = new Guid(prefix[..16]);
        long ordinal = BinaryPrimitives.ReadInt64LittleEndian(prefix[16..]);
        if (ordinal <= 0) throw new InvalidDataException("The depth frame has invalid ordering.");
        var received = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(prefix[24..]), DateTimeKind.Utc);
        var source = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(prefix[32..]), DateTimeKind.Utc);
        return new(recording, ordinal, new DepthUpdate
        {
            Snapshot = new DepthSnapshot([], [], source), ReceivedAt = received,
            Coverage = DepthCoverage.Missing | DepthCoverage.Truncated,
            GapBefore = DepthGapReason.Missing,
            Message = "A saved depth record exceeded the single-sample byte limit and was not decoded.",
        });
    }

    private static DepthSample Decode(byte[] bytes, long maxSampleBytes)
    {
        using var payload = new MemoryStream(bytes, writable: false);
        using var fields = new BinaryReader(payload, Encoding.UTF8, leaveOpen: true);
        var recording = new Guid(fields.ReadBytes(16));
        long ordinal = fields.ReadInt64();
        var received = new DateTime(fields.ReadInt64(), DateTimeKind.Utc);
        var source = new DateTime(fields.ReadInt64(), DateTimeKind.Utc);
        long remaining = maxSampleBytes - 512L;
        string provider = ReadBudgetedString(fields, bytes, ref remaining), venue = ReadBudgetedString(fields, bytes, ref remaining);
        long? sequence = ReadNullable(fields), first = ReadNullable(fields), previous = ReadNullable(fields);
        var coverage = (DepthCoverage)fields.ReadInt32();
        var gap = (DepthGapReason)fields.ReadByte();
        bool full = fields.ReadBoolean();
        string message = ReadBudgetedString(fields, bytes, ref remaining);
        var bids = ReadLevels(fields, true, ref remaining);
        var asks = ReadLevels(fields, false, ref remaining);
        int count = fields.ReadInt32();
        if (count < 0 || count > 100_000) throw new InvalidDataException("The depth change count is invalid.");
        if (48L * count > remaining) throw new SampleLimitException();
        var changes = new BookChange[count];
        for (int i = 0; i < count; i++)
        {
            var side = (BookSide)fields.ReadByte();
            long price = fields.ReadInt64();
            decimal size = fields.ReadDecimal();
            var origin = (BookChangeOrigin)fields.ReadByte();
            if (side is not BookSide.Bid and not BookSide.Ask || size < 0 ||
                origin is not BookChangeOrigin.Received and not BookChangeOrigin.DerivedSnapshotDifference and not BookChangeOrigin.BookRepair)
                throw new InvalidDataException("The depth change is invalid.");
            changes[i] = new(side, price, size, origin);
        }
        if (payload.Position != payload.Length || ordinal <= 0) throw new InvalidDataException("The depth frame has invalid trailing fields or ordering.");
        return new(recording, ordinal, new DepthUpdate
        {
            Snapshot = new DepthSnapshot(bids, asks, source), Changes = changes, ReceivedAt = received,
            ProviderId = provider, Venue = venue, Sequence = sequence, FirstSequence = first, PreviousSequence = previous,
            Coverage = coverage, GapBefore = gap, IsFullRefresh = full, Message = message.Length == 0 ? null : message,
        });
    }

    private static bool ReadIdentity(BinaryReader reader, string expected)
    {
        int length = reader.Read7BitEncodedInt();
        if (length != Encoding.UTF8.GetByteCount(expected)) return false;
        var bytes = reader.ReadBytes(length);
        return bytes.Length == length && Encoding.UTF8.GetString(bytes) == expected;
    }

    private static string ReadBudgetedString(BinaryReader reader, byte[] payload, ref long remaining)
    {
        int length = reader.Read7BitEncodedInt();
        long position = reader.BaseStream.Position;
        if (length < 0 || length > payload.Length - position) throw new InvalidDataException("The depth string length is invalid.");
        var bytes = payload.AsSpan(checked((int)position), length);
        int characters = Encoding.UTF8.GetCharCount(bytes);
        if (2L * characters > remaining) throw new SampleLimitException();
        remaining -= 2L * characters;
        string value = Encoding.UTF8.GetString(bytes);
        reader.BaseStream.Position += length;
        return value;
    }

    private static void WriteLevels(BinaryWriter writer, BookLevel[] levels)
    {
        writer.Write(levels.Length);
        foreach (var level in levels) { writer.Write(level.PriceTicks); writer.Write(level.Size); }
    }

    private static BookLevel[] ReadLevels(BinaryReader reader, bool bid, ref long remaining)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 10_000) throw new InvalidDataException("The depth level count is invalid.");
        if (32L * count > remaining) throw new SampleLimitException();
        remaining -= 32L * count;
        var levels = new BookLevel[count];
        for (int i = 0; i < count; i++)
        {
            levels[i] = new(reader.ReadInt64(), reader.ReadDecimal());
            if (levels[i].Size <= 0 || i > 0 && (bid ? levels[i - 1].PriceTicks <= levels[i].PriceTicks : levels[i - 1].PriceTicks >= levels[i].PriceTicks))
                throw new InvalidDataException("The depth levels are not positive sizes in strict book order.");
        }
        return levels;
    }

    private static void WriteNullable(BinaryWriter writer, long? value) { writer.Write(value.HasValue); if (value is { } n) writer.Write(n); }
    private static long? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt64() : null;

    private static uint Crc32(ReadOnlySpan<byte> bytes) => ~UpdateCrc(uint.MaxValue, bytes);

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }
}
