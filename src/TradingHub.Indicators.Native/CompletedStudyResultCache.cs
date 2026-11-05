using System.Security.Cryptography;
using System.Text.Json;

namespace TradingHub.Indicators.Native;

/// <summary>Completed input identity. ContentDigest must include the distribution, not only count/volume.</summary>
public readonly record struct CompletedStudyKey(string Provider, string Instrument, string Configuration, string SourceIdentity,
    long SourceRevision, long CorrectionRevision, StudyCoverage Coverage, int ResolutionSeconds, long StartTicks, long EndTicks, string ContentDigest);

/// <summary>Bounded immutable completed results. Disk is opt-in; a corrupt/unreadable entry is always a miss.</summary>
public sealed class CompletedStudyResultCache : IDisposable
{
    private const int Schema = 1;
    private sealed record Envelope(int Schema, CompletedStudyKey Key, string Digest, byte[] Payload);
    private sealed record Entry(byte[] Payload, LinkedListNode<string> Node);
    private readonly object _sync = new(); private readonly Dictionary<string, Entry> _entries = new(); private readonly LinkedList<string> _lru = new();
    private readonly string? _directory; private readonly int _maxEntries; private readonly long _maxBytes; private long _bytes; private bool _disposed;
    public CompletedStudyResultCache(string? directory = null, int maxEntries = 64, long maxBytes = 8 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1); ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _maxEntries = maxEntries; _maxBytes = maxBytes; _directory = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
    }
    public int EntryCount { get { lock (_sync) return _entries.Count; } }
    public long RetainedBytes { get { lock (_sync) return _bytes; } }
    public string? LastError { get; private set; }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Identity(CompletedStudyKey key) => Hash(JsonSerializer.SerializeToUtf8Bytes(new { Schema, Key = key }));
    private string FilePath(string id) => Path.Combine(_directory!, id + ".studycache");
    public bool TryRead(CompletedStudyKey key, out byte[] payload)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); payload = Array.Empty<byte>(); string id = Identity(key);
            if (_entries.TryGetValue(id, out var entry)) { _lru.Remove(entry.Node); _lru.AddLast(entry.Node); payload = (byte[])entry.Payload.Clone(); return true; }
            if (_directory == null) return false;
            string file = FilePath(id);
            try
            {
                if (!File.Exists(file)) return false;
                var info = new FileInfo(file);
                if (info.Length > _maxBytes * 2 + 65536) throw new InvalidDataException("Completed study cache exceeds its payload limit.");
                var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(file));
                if (envelope == null || envelope.Schema != Schema || envelope.Key != key || envelope.Payload == null || envelope.Payload.LongLength > _maxBytes || Hash(envelope.Payload) != envelope.Digest)
                    throw new InvalidDataException("Completed study cache identity/checksum mismatch.");
                Retain(id, envelope.Payload); payload = (byte[])envelope.Payload.Clone(); LastError = null; return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
            { LastError = ex.Message; return false; }
        }
    }
    public void Store(CompletedStudyKey key, ReadOnlySpan<byte> payload)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (payload.Length > _maxBytes) return;
            string id = Identity(key); byte[] copy = payload.ToArray(); Retain(id, copy);
            if (_directory == null) return;
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(_directory);
                temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".pending");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new Envelope(Schema, key, Hash(copy), copy)); stream.Flush(true);
                }
                File.Move(temporary, FilePath(id), true); temporary = null; TrimDisk(); LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            { LastError = ex.Message; }
            finally { if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        }
    }
    private void Retain(string id, byte[] bytes)
    {
        if (_entries.Remove(id, out var old)) { _lru.Remove(old.Node); _bytes -= old.Payload.Length; }
        var node = _lru.AddLast(id); _entries.Add(id, new(bytes, node)); _bytes += bytes.Length;
        while (_entries.Count > _maxEntries || _bytes > _maxBytes)
        { string victim = _lru.First!.Value; var entry = _entries[victim]; _bytes -= entry.Payload.Length; _entries.Remove(victim); _lru.RemoveFirst(); }
    }
    private void TrimDisk()
    {
        // Dedicated opt-in directory. Enumeration retains at most maxEntries candidates; it never materializes all paths.
        var keep = new SortedSet<(long Time, string Path, long Size)>(); long bytes = 0;
        foreach (string path in Directory.EnumerateFiles(_directory!, "*.studycache"))
        {
            var file = new FileInfo(path); var entry = (file.LastWriteTimeUtc.Ticks, path, file.Length); keep.Add(entry); bytes += entry.Length;
            while (keep.Count > _maxEntries || bytes > _maxBytes * 2 + _maxEntries * 65536L)
            { var oldest = keep.Min; File.Delete(oldest.Path); bytes -= oldest.Size; keep.Remove(oldest); }
        }
    }
    public void Dispose() { lock (_sync) { _disposed = true; _entries.Clear(); _lru.Clear(); _bytes = 0; } }
}
