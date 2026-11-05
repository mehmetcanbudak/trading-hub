// Capability inspired by gouqizi2214730-source/trading-hub at 50220ba91613161685f84384825d855cb4dbeb76; redesigned to distinguish measured aggregates from raw prints.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Price/side summaries for display. Raw files are never modified and synthetic candles never become measured rows.</summary>
public static class CompactHistory
{
    public const long CompressedIdBase = long.MinValue / 4;
    public static readonly int[] Granularities = { 1, 5, 15, 60 };
    private const int Magic = 0x34434854;
    private const int MaximumCacheRecords = 2_000_000;
    private const int MaximumCacheBytes = 256 * 1024 * 1024;
    private const DataQuality KnownQuality = DataQuality.Raw | DataQuality.Compact | DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial;
    private readonly record struct RowKey(decimal Price, TradeSide Side, TradeProvenance Provenance, bool HasKnownCount);
    public static string CachePath(string root, string feedId, string symbol, DateOnly day, int minutes = 5) =>
        Path.ChangeExtension(TickFile.DayPath(root, feedId, symbol, day), $".m{minutes}.thc");

    /// <summary>
    /// Preserves latest source OHLC, exact price/side totals and constituent counts, not an observed tick path.
    /// Corrections use exact source identity and final occurrence; source windows must fit wholly inside the requested UTC bucket.
    /// </summary>
    public static List<Trade> Compress(IReadOnlyList<Trade> trades, decimal tickSize, int minutes = 5)
    {
        ValidateMinutes(minutes);
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        return CompressStream(CanonicalRows(trades, MaximumCacheRecords, default), minutes, null, null, default, Math.Min(trades.Count, 65_536));
    }

    private static long NextId(DateTime time, int offset) => CompressedIdBase + (time.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond * 64 + offset;

    /// <summary>
    /// Reads a source-preserving daily derivation. Proven ordered unique streams need only per-source state;
    /// correction journals use a caller-lowerable, hard-bounded final-identity index or fail without publishing a cache.
    /// </summary>
    public static List<Trade> LoadDay(string ticksRoot, string cacheRoot, Instrument instrument, DateOnly day, int minutes = 5,
        CancellationToken ct = default, int maximumCorrectionIdentities = MaximumCacheRecords)
    {
        ValidateMinutes(minutes);
        if (instrument.TickSize <= 0) throw new ArgumentOutOfRangeException(nameof(instrument), "The instrument tick size must be positive.");
        if (maximumCorrectionIdentities <= 0 || maximumCorrectionIdentities > MaximumCacheRecords) throw new ArgumentOutOfRangeException(nameof(maximumCorrectionIdentities));
        ct.ThrowIfCancellationRequested();
        var source = TickFile.ExistingDayPath(ticksRoot, instrument.FeedId, instrument.Symbol, day);
        if (source == null) return new List<Trade>();
        byte[] digest = Fingerprint(source);
        string path = CachePath(cacheRoot, instrument.FeedId, instrument.Symbol, day, minutes);
        var cached = ReadCache(path, digest, instrument.TickSize, day, minutes);
        ct.ThrowIfCancellationRequested();
        if (cached != null) return cached;
        var sourceIdentity = HistorySources.DefaultSourceIdentity(instrument.FeedId, instrument.Symbol);
        long revision = BitConverter.ToInt64(digest, 0) & long.MaxValue;
        var canonical = CanonicalRows(AnnotatedRows(source, instrument.FeedId, sourceIdentity, ct), maximumCorrectionIdentities, ct);
        var compact = CompressStream(canonical, minutes, sourceIdentity, revision, ct);
        ct.ThrowIfCancellationRequested();
        // A concurrent raw append invalidates this derivation rather than publishing a plausible stale cache.
        if (!CryptographicOperations.FixedTimeEquals(digest, Fingerprint(source))) throw new IOException("Raw history changed while compacting; reload after the writer finishes.");
        ct.ThrowIfCancellationRequested();
        WriteCache(path, digest, instrument.TickSize, minutes, compact, ct);
        return compact;
    }

    private static IEnumerable<Trade> AnnotatedRows(string path, string feedId, string sourceIdentity, CancellationToken ct)
    {
        foreach (var trade in TickFile.Enumerate(path, requireOrdered: false))
        {
            ct.ThrowIfCancellationRequested();
            yield return HistorySources.AnnotateSource(trade, feedId, sourceIdentity);
        }
    }

    private static bool HasCanonicalOrder(IEnumerable<Trade> rows, CancellationToken ct)
    {
        var lastByNamespace = new Dictionary<(string Source, bool Opaque, int Day), TradeIdentity>();
        DateTime previousTime = DateTime.MinValue;
        foreach (var trade in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (trade.Time < previousTime) return false;
            previousTime = trade.Time;
            var identity = TradeIdentity.From(trade);
            var scope = (identity.SourceIdentity, identity.SourceEventId != null, identity.UtcDay);
            if (lastByNamespace.TryGetValue(scope, out var previous))
            {
                bool increasing = identity.SourceEventId != null
                    ? StringComparer.Ordinal.Compare(identity.SourceEventId, previous.SourceEventId) > 0
                    : identity.LegacyId > previous.LegacyId;
                if (!increasing) return false;
            }
            else if (lastByNamespace.Count >= MaximumCacheRecords)
                throw new InvalidDataException("Compact source namespaces exceed the bounded history capacity.");
            lastByNamespace[scope] = identity;
        }
        return true;
    }

    private static IEnumerable<Trade> CanonicalRows(IEnumerable<Trade> source, int maximumIdentities, CancellationToken ct)
    {
        if (HasCanonicalOrder(source, ct)) return source;
        var lastPositions = new Dictionary<TradeIdentity, long>();
        long ordinal = 0;
        foreach (var trade in source)
        {
            ct.ThrowIfCancellationRequested();
            var identity = TradeIdentity.From(trade);
            if (lastPositions.Count >= maximumIdentities && !lastPositions.ContainsKey(identity))
                throw new InvalidDataException("The source correction identity index exceeds its bounded capacity. Load a canonical source or increase the correction index budget within the supported limit.");
            lastPositions[identity] = ordinal++;
        }
        var selected = new List<(Trade Trade, long Ordinal)>(lastPositions.Count);
        ordinal = 0;
        foreach (var trade in source)
        {
            ct.ThrowIfCancellationRequested();
            if (lastPositions.TryGetValue(TradeIdentity.From(trade), out long final) && final == ordinal)
                selected.Add((trade, ordinal));
            ordinal++;
        }
        if (selected.Count != lastPositions.Count) throw new IOException("History changed while resolving source corrections; reload after the writer finishes.");
        ct.ThrowIfCancellationRequested();
        selected.Sort(static (left, right) =>
        {
            int order = left.Trade.Time.CompareTo(right.Trade.Time);
            return order != 0 ? order : left.Ordinal.CompareTo(right.Ordinal);
        });
        ct.ThrowIfCancellationRequested();
        return selected.Select(static row => row.Trade);
    }

    private static List<Trade> CompressStream(IEnumerable<Trade> raw, int minutes, string? source, long? revision, CancellationToken ct, int capacity = 0)
    {
        var output = new List<Trade>(capacity);
        var rows = new Dictionary<RowKey, (decimal Volume, int Count, int Events)>();
        Trade? first = null, last = null, high = null, low = null;
        long firstIndex = 0, lastIndex = 0, highIndex = 0, lowIndex = 0, index = 0;
        long bucket = 0, span = minutes * TimeSpan.TicksPerMinute;
        DateTime previousTime = DateTime.MinValue;
        foreach (var input in raw)
        {
            ct.ThrowIfCancellationRequested();
            var provenance = input.Provenance;
            if (!provenance.IsMeasured || (provenance.Quality & ~KnownQuality) != 0 || input.Volume < 0 ||
                input.Side > TradeSide.Sell || provenance.ObservedTradeCount < 0 || (provenance.IsObservedTrade && provenance.ObservedTradeCount == 0))
                throw new InvalidDataException("Only valid source-recorded measured events can produce compact rows.");
            var time = input.Time.Kind == DateTimeKind.Local ? input.Time.ToUniversalTime() : DateTime.SpecifyKind(input.Time, DateTimeKind.Utc);
            long nextBucket = time.Ticks / span;
            var window = provenance.SourceWindow(time);
            long bucketStart = nextBucket * span;
            if (window.From.Ticks < bucketStart || window.To.Ticks > checked(bucketStart + span))
                throw new InvalidDataException("A source window cannot be represented by the requested compact bucket.");
            if (time < previousTime) throw new InvalidDataException("History must be chronologically corrected before compaction.");
            previousTime = time;
            if (first != null && nextBucket != bucket) Flush();
            provenance = provenance with {
                SourceIdentity = provenance.SourceIdentity ?? source,
                SourceEventId = null,
                CorrectionRevision = provenance.CorrectionRevision == 0 && revision.HasValue ? revision.Value : provenance.CorrectionRevision,
            };
            var trade = new Trade(input.Id, time, input.Price, input.Volume, input.Side, provenance);
            if (first == null)
            {
                bucket = nextBucket; first = high = low = trade;
                firstIndex = highIndex = lowIndex = index;
            }
            last = trade; lastIndex = index++;
            if (trade.Price > high!.Value.Price) { high = trade; highIndex = lastIndex; }
            if (trade.Price < low!.Value.Price) { low = trade; lowIndex = lastIndex; }
            var key = Key(trade);
            rows.TryGetValue(key, out var old);
            rows[key] = (old.Volume + trade.Volume, checked(old.Count + trade.Provenance.ObservedTradeCount), checked(old.Events + 1));
            if ((long)rows.Count + output.Count > MaximumCacheRecords) throw new InvalidDataException("Compact price rows exceed the bounded history capacity. Choose a shorter history range.");
        }
        if (first != null) Flush();
        return output;

        static RowKey Key(in Trade trade) => new(trade.Price, trade.Side,
            trade.Provenance with { ObservedTradeCount = 0, SourceEventId = null }, trade.Provenance.ObservedTradeCount > 0);

        TradeProvenance Compacted(TradeProvenance provenance, int count) => provenance with {
            Quality = (provenance.Quality & ~DataQuality.Raw) | DataQuality.Compact,
            ResolutionSeconds = minutes * 60, ResolutionOffsetSeconds = 0, ObservedTradeCount = count, SourceEventId = null,
        };

        void Flush()
        {
            var open = first!.Value;
            var close = last!.Value;
            AddAnchor(open);
            if (highIndex != firstIndex && highIndex != lastIndex) AddAnchor(high!.Value);
            if (lowIndex != firstIndex && lowIndex != lastIndex && lowIndex != highIndex) AddAnchor(low!.Value);
            if (lastIndex != firstIndex) Remove(close);
            foreach (var (key, value) in rows.OrderBy(x => x.Key.Price).ThenBy(x => x.Key.Side))
            {
                if (value.Events <= 0) continue;
                AddOutput(open.Time, key.Price, value.Volume, key.Side, Compacted(key.Provenance, value.Count));
            }
            if (lastIndex != firstIndex)
            {
                AddOutput(close.Time, close.Price, close.Volume, close.Side, Compacted(close.Provenance, close.Provenance.ObservedTradeCount));
            }
            rows.Clear(); first = last = high = low = null;

            void AddAnchor(Trade trade)
            {
                Remove(trade);
                AddOutput(open.Time, trade.Price, trade.Volume, trade.Side, Compacted(trade.Provenance, trade.Provenance.ObservedTradeCount));
            }
            void Remove(in Trade trade)
            {
                var key = Key(trade);
                var value = rows[key];
                rows[key] = (value.Volume - trade.Volume, value.Count - trade.Provenance.ObservedTradeCount, value.Events - 1);
            }
        }

        void AddOutput(DateTime time, decimal price, decimal volume, TradeSide side, TradeProvenance provenance)
        {
            if (output.Count >= MaximumCacheRecords) throw new InvalidDataException("Compact price rows exceed the bounded history capacity. Choose a shorter history range.");
            output.Add(new Trade(NextId(time, output.Count), time, price, volume, side, provenance));
        }
    }

    public static void EnsureCached(string ticksRoot, string cacheRoot, Instrument instrument, DateOnly day, int minutes = 5) =>
        _ = LoadDay(ticksRoot, cacheRoot, instrument, day, minutes);

    internal static byte[] Fingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    private static byte[] CacheDigest(decimal tick, int minutes, ReadOnlySpan<byte> sourceDigest, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[60];
        BinaryPrimitives.WriteInt32LittleEndian(header, Magic);
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(tick, bits);
        for (int part = 0; part < bits.Length; part++) BinaryPrimitives.WriteInt32LittleEndian(header[(4 + part * 4)..], bits[part]);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], minutes);
        sourceDigest.CopyTo(header[24..56]);
        BinaryPrimitives.WriteInt32LittleEndian(header[56..], payload.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header); hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    private static List<Trade>? ReadCache(string path, byte[] sourceDigest, decimal tick, DateOnly day, int minutes)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumCacheBytes + 1_048_576L) return null;
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Magic || reader.ReadDecimal() != tick || reader.ReadInt32() != minutes || !reader.ReadBytes(32).AsSpan().SequenceEqual(sourceDigest)) return null;
            int length = reader.ReadInt32();
            if (length < 12 || length > MaximumCacheBytes) return null;
            byte[] digest = reader.ReadBytes(32);
            using var compressed = new BrotliStream(stream, CompressionMode.Decompress);
            var payload = new byte[length];
            try
            {
                compressed.ReadExactly(payload);
                if (compressed.ReadByte() != -1) return null;
            }
            catch (InvalidOperationException) { return null; } // Brotli reports malformed compressed input this way.
            if (!CacheDigest(tick, minutes, sourceDigest, payload).AsSpan().SequenceEqual(digest)) return null;
            using var data = new BinaryReader(new MemoryStream(payload));
            int sourceCount = data.ReadInt32();
            if (sourceCount < 0 || sourceCount > MaximumCacheRecords || sourceCount > payload.Length) return null;
            var sources = new string[sourceCount];
            var uniqueSources = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = data.ReadString();
                if (!uniqueSources.Add(sources[i])) return null;
            }
            int metadataCount = data.ReadInt32();
            if (metadataCount < 0 || metadataCount > MaximumCacheRecords || (long)metadataCount * 22 > payload.Length - data.BaseStream.Position) return null;
            var metadata = new TradeProvenance[metadataCount];
            for (int i = 0; i < metadata.Length; i++)
            {
                var quality = (DataQuality)data.ReadByte();
                int resolution = data.ReadInt32(), sourceIndex = data.ReadInt32();
                long correction = data.ReadInt64();
                byte known = data.ReadByte();
                int phase = data.ReadInt32();
                if ((quality & ~KnownQuality) != 0 || (quality & DataQuality.Compact) == 0 || (quality & (DataQuality.Raw | DataQuality.Synthetic)) != 0 ||
                    resolution != minutes * 60 || phase != 0 || known > 1 || sourceIndex < -1 || sourceIndex >= sources.Length) return null;
                metadata[i] = new TradeProvenance(quality, resolution, sourceIndex < 0 ? null : sources[sourceIndex], correction, 0, known != 0, phase);
            }
            int count = data.ReadInt32();
            if (count < 0 || count > MaximumCacheRecords || (long)count * 57 > payload.Length - data.BaseStream.Position) return null;
            var result = new List<Trade>(count);
            long previousTime = -1;
            long dayFrom = day.DayNumber * TimeSpan.TicksPerDay, dayTo = dayFrom + TimeSpan.TicksPerDay;
            for (int i = 0; i < count; i++)
            {
                long id = data.ReadInt64(), time = data.ReadInt64();
                decimal price = data.ReadDecimal(), volume = data.ReadDecimal();
                var side = (TradeSide)data.ReadByte();
                int metadataIndex = data.ReadInt32(), observed = data.ReadInt32();
                if (id >= 0 || time < previousTime || time < dayFrom || time >= dayTo || volume < 0 || side > TradeSide.Sell ||
                    observed < 0 || (uint)metadataIndex >= (uint)metadata.Length) return null;
                var location = new DateTime(time, DateTimeKind.Utc);
                var provenance = metadata[metadataIndex] with { ObservedTradeCount = observed };
                _ = provenance.SourceWindow(location);
                result.Add(new Trade(id, location, price, volume, side, provenance));
                previousTime = time;
            }
            return data.BaseStream.Position == data.BaseStream.Length ? result : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException) { return null; }
    }

    private static void WriteCache(string path, byte[] sourceDigest, decimal tick, int minutes, List<Trade> trades, CancellationToken ct)
    {
        if (trades.Count > MaximumCacheRecords) return;
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        var metadata = new Dictionary<TradeProvenance, int>();
        foreach (var trade in trades)
        {
            ct.ThrowIfCancellationRequested();
            var provenance = trade.Provenance with { ObservedTradeCount = 0 };
            if (provenance.SourceIdentity is { } source) sources.TryAdd(source, sources.Count);
            metadata.TryAdd(provenance, metadata.Count);
        }
        long capacity = 12L + metadata.Count * 22L + trades.Count * 57L;
        foreach (var source in sources.Keys)
        {
            ct.ThrowIfCancellationRequested();
            int bytes = System.Text.Encoding.UTF8.GetByteCount(source);
            capacity += bytes + 1L;
            for (int length = bytes; length >= 128; length >>= 7) capacity++;
            if (capacity > MaximumCacheBytes) return;
        }
        if (capacity > MaximumCacheBytes) return;
        using var payload = new MemoryStream(checked((int)capacity));
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(sources.Count);
            foreach (var source in sources.Keys) writer.Write(source);
            writer.Write(metadata.Count);
            foreach (var provenance in metadata.Keys)
            {
                writer.Write((byte)provenance.Quality); writer.Write(provenance.ResolutionSeconds);
                writer.Write(provenance.SourceIdentity is { } source ? sources[source] : -1);
                writer.Write(provenance.CorrectionRevision); writer.Write(provenance.HasKnownAvailability); writer.Write(provenance.ResolutionOffsetSeconds);
            }
            writer.Write(trades.Count);
            foreach (var trade in trades)
            {
                ct.ThrowIfCancellationRequested();
                writer.Write(trade.Id); writer.Write(trade.Time.Ticks); writer.Write(trade.Price); writer.Write(trade.Volume); writer.Write((byte)trade.Side);
                writer.Write(metadata[trade.Provenance with { ObservedTradeCount = 0 }]); writer.Write(trade.Provenance.ObservedTradeCount);
            }
        }
        if (payload.Length > MaximumCacheBytes) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.Write(Magic); writer.Write(tick); writer.Write(minutes); writer.Write(sourceDigest); writer.Write(checked((int)payload.Length));
                writer.Write(CacheDigest(tick, minutes, sourceDigest, payload.GetBuffer().AsSpan(0, (int)payload.Length)));
                using (var compressed = new BrotliStream(stream, CompressionLevel.Fastest, leaveOpen: true)) compressed.Write(payload.GetBuffer().AsSpan(0, (int)payload.Length));
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateMinutes(int minutes)
    {
        if (!Granularities.Contains(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes), "Compact history supports 1, 5, 15 or 60 minutes.");
    }
}
