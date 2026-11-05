using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Lossless, checksummed older-day archives. Archiving never removes or rewrites the raw file.</summary>
public static class TickArchive
{
    private static ReadOnlySpan<byte> Magic => "VTA1"u8;
    public const long MaximumExpandedBytes = 512L * 1024 * 1024;
    public static string PathFor(string rawPath) => rawPath + ".br";

    public static string? Resolve(string rawPath) => File.Exists(rawPath) ? rawPath : File.Exists(PathFor(rawPath)) ? PathFor(rawPath) : null;

    /// <summary>Restore an archive-only day byte-for-byte before append. The caller must hold the instrument writer lease.</summary>
    internal static void EnsureRawDay(string rawPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(rawPath)) return;
        string archive = PathFor(rawPath);
        if (!File.Exists(archive)) return;
        using var sourceLease = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var verified = TickFile.OpenArchiveStream(sourceLease);
        string temporary = rawPath + "." + Guid.NewGuid().ToString("N") + ".restore.tmp";
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65_536);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = verified.Read(buffer, 0, buffer.Length);
                    if (count == 0) break; // EOF verifies the complete envelope length and digest before publication.
                    output.Write(buffer, 0, count);
                }
                ct.ThrowIfCancellationRequested();
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, rawPath, overwrite: false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string ArchiveDay(string root, Instrument instrument, DateOnly day, CancellationToken ct = default)
    {
        if (instrument.FeedId == IbBarImport.FeedId) throw new NotSupportedException("IB history uses its own atomic publication catalog and must not be archived independently.");
        ct.ThrowIfCancellationRequested();
        var source = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, day);
        if (!File.Exists(source)) throw new FileNotFoundException("The raw day is unavailable.", source);
        using var sourceLease = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = sourceLease.Length;
        if (length > MaximumExpandedBytes) throw new InvalidDataException("Raw day exceeds the bounded 512 MiB archive size.");
        byte[] validatedDigest = TickFile.ValidateStream(sourceLease, source, ct);
        sourceLease.Position = 0;
        var destination = PathFor(source);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65_536);
        try
        {
            byte[] originalDigest;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.Write(Magic); writer.Write(length);
                Span<byte> placeholder = stackalloc byte[32];
                placeholder.Clear(); writer.Write(placeholder);
                long copied = 0;
                using (var compressed = new BrotliStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    int count;
                    while ((count = sourceLease.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        hash.AppendData(buffer.AsSpan(0, count));
                        compressed.Write(buffer, 0, count);
                        copied += count;
                    }
                }
                if (copied != length) throw new IOException("The raw day changed during archival.");
                originalDigest = hash.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(validatedDigest, originalDigest) || sourceLease.Length != length)
                    throw new IOException("The raw day changed after validation; the original remains authoritative.");
                writer.Flush(); stream.Position = 12; writer.Write(originalDigest); writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            using (var verifyFile = File.OpenRead(temporary))
            using (var verified = TickFile.OpenArchiveStream(verifyFile))
            {
                long verifiedLength = 0;
                int count;
                while ((count = verified.Read(buffer, 0, buffer.Length)) > 0)
                { ct.ThrowIfCancellationRequested(); verifiedLength += count; }
                if (verifiedLength != length) throw new InvalidDataException("The archived tick length differs from its raw source.");
            }
            using (var current = File.OpenRead(source))
            using (var finalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                if (current.Length != length) throw new IOException("The raw day changed during archival.");
                int count;
                while ((count = current.Read(buffer, 0, buffer.Length)) > 0)
                { ct.ThrowIfCancellationRequested(); finalHash.AppendData(buffer.AsSpan(0, count)); }
                if (!CryptographicOperations.FixedTimeEquals(originalDigest, finalHash.GetHashAndReset()))
                    throw new IOException("The raw day changed during archival; the original remains authoritative.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Invalid tick archive header: " + path);
        long length = reader.ReadInt64();
        if (length < 5 || length > MaximumExpandedBytes) throw new InvalidDataException("Tick archive exceeds the bounded expanded size: " + path);
        byte[] digest = reader.ReadBytes(32);
        if (digest.Length != 32) throw new InvalidDataException("Truncated tick archive digest: " + path);
        byte[] bytes = new byte[(int)length];
        using var compressed = new BrotliStream(stream, CompressionMode.Decompress);
        compressed.ReadExactly(bytes);
        if (compressed.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(bytes)))
            throw new InvalidDataException("Truncated or corrupt tick archive: " + path);
        return bytes;
    }

}
