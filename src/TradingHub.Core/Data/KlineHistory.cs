using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Provider OHLC history, cached by calendar month and displayed as synthetic price paths.
/// Exact source volume, side totals, resolution, phase and availability survive; price-level order flow is inferred.
/// </summary>
public static class KlineHistory
{
    /// <summary>Optional application-owned cache root.</summary>
    public static string? CacheRoot { get; set; }

    /// <summary>Stable negative synthetic identities, increasing with source open time.</summary>
    public const long SyntheticIdBase = long.MinValue / 2;

    public static bool IsSynthetic(in Trade trade) => (trade.Provenance.Quality & DataQuality.Synthetic) != 0;

    /// <summary>Uses coarser requested history for longer ranges.</summary>
    public static int MinutesFor(TimeSpan range) => range.TotalDays <= 400 ? 1 : range.TotalDays <= 1500 ? 5 : 15;

    /// <summary>Estimated display events, not an observed trade count.</summary>
    public static long EstimateTrades(TimeSpan range) => (long)(range.TotalMinutes / MinutesFor(range) * 4);

    private static long ToMs(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private const int CacheMagic = 0x334B4854;
    private const int MaximumCacheBars = 50_000;
    private const int MaximumUncachedBars = 1_000_000;
    private const int MaximumCacheBytes = 32 * 1024 * 1024;
    private const DataQuality KnownQuality = DataQuality.Raw | DataQuality.Compact | DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial;
    private static readonly SemaphoreSlim[] CacheGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private sealed record MonthCache(DateTime From, DateTime To, DateTime Updated, List<Kline> Bars);

    /// <summary>
    /// Calendar-month cache with overlapping active tails. Warnings report partial-cache fallback or failed cache persistence;
    /// valid source data remains usable when only derived cache publication fails.
    /// </summary>
    public static async Task<List<Kline>> LoadAsync(IKlineHistory source, Instrument inst, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        IProgress<double>? progress, CancellationToken ct, Action<string>? warning = null)
    {
        if (!CompactHistory.Granularities.Contains(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes));
        ct.ThrowIfCancellationRequested();
        string sourceIdentity = inst.Key;
        var result = new List<Kline>();
        if (toUtc <= fromUtc) return result;
        if (inst.FeedId == SimulatedFeed.FeedId || CacheRoot == null)
        {
            await Fetch(fromUtc, toUtc, result, MaximumUncachedBars).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Normalize(result, fromUtc, toUtc, minutes, sourceIdentity);
        }
        var months = new List<DateTime>();
        for (var month = new DateTime(fromUtc.Year, fromUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc); month < toUtc; month = month.AddMonths(1)) months.Add(month);
        string tag = source.CacheTag + (source is IDataScopeFeed scoped ? "\u001f" + scoped.DataScopeKey : "");
        for (int index = 0; index < months.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var month = months[index];
            var end = month.AddMonths(1);
            var requestedFrom = fromUtc > month ? fromUtc : month;
            var requestedTo = toUtc < end ? toUtc : end;
            bool closed = end <= DateTime.UtcNow.AddDays(-2);
            var path = CachePath(inst, month, minutes, tag);
            var gate = CacheGates[(StringComparer.Ordinal.GetHashCode(path) & int.MaxValue) % CacheGates.Length];
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var cache = ReadCache(path, inst, minutes, month);
                ct.ThrowIfCancellationRequested();
                bool covered = cache != null && cache.From <= requestedFrom && cache.To >= requestedTo;
                if (cache == null || !covered || !closed || DateTime.UtcNow - cache.Updated > TimeSpan.FromDays(1))
                {
                    DateTime fetchFrom, fetchTo;
                    if (closed) { fetchFrom = month; fetchTo = end; }
                    else if (cache == null || requestedFrom < cache.From) { fetchFrom = requestedFrom; fetchTo = cache != null && cache.To > requestedTo ? cache.To : requestedTo; }
                    else
                    {
                        fetchTo = requestedTo;
                        var tail = cache.To < requestedTo ? cache.To : requestedTo;
                        fetchFrom = tail.AddMinutes(-2 * minutes);
                        if (fetchFrom < cache.From) fetchFrom = cache.From;
                    }
                    var fresh = new List<Kline>();
                    MonthCache refreshed;
                    try
                    {
                        await Fetch(fetchFrom, fetchTo, fresh, MaximumCacheBars).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        var normalized = Normalize(fresh, fetchFrom, fetchTo, minutes, sourceIdentity);
                        var merged = cache?.Bars.Where(k => k.OpenTimeMs < ToMs(fetchFrom) || k.OpenTimeMs >= ToMs(fetchTo)).ToList() ?? new List<Kline>();
                        merged.AddRange(normalized);
                        var coverageFrom = cache != null && cache.From < fetchFrom ? cache.From : fetchFrom;
                        var coverageTo = cache != null && cache.To > fetchTo ? cache.To : fetchTo;
                        refreshed = new MonthCache(coverageFrom, coverageTo, DateTime.UtcNow, Normalize(merged, month, end, minutes, sourceIdentity));
                    }
                    catch (Exception error) when (error is not OperationCanceledException && cache != null)
                    {
                        ct.ThrowIfCancellationRequested();
                        warning?.Invoke($"Candle history refresh failed; using the previous cache with partial coverage: {error.Message}");
                        result.AddRange(cache.Bars.Where(k => k.OpenTimeMs >= ToMs(requestedFrom) && k.OpenTimeMs < ToMs(requestedTo))
                            .Select(k => k with { Provenance = k.Provenance with { Quality = k.Provenance.Quality | DataQuality.Synthetic | DataQuality.Partial } }));
                        progress?.Report((index + 1d) / months.Count);
                        continue;
                    }
                    cache = refreshed;
                    try { WriteCache(path, inst, minutes, cache, ct); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        ct.ThrowIfCancellationRequested();
                        warning?.Invoke($"Unable to write the candle history cache '{path}': {error.Message} Loaded source history remains available but was not cached.");
                    }
                }
                result.AddRange(cache!.Bars.Where(k => k.OpenTimeMs >= ToMs(requestedFrom) && k.OpenTimeMs < ToMs(requestedTo)));
            }
            finally { gate.Release(); }
            progress?.Report((index + 1d) / months.Count);
        }
        return result;

        async Task Fetch(DateTime start, DateTime finish, List<Kline> target, int maximumBars)
        {
            await source.LoadKlinesAsync(inst, start, finish, minutes, endPrice, batch =>
            {
                ct.ThrowIfCancellationRequested();
                if (batch.Count > maximumBars - target.Count) throw new InvalidDataException($"The provider exceeded the bounded candle history capacity of {maximumBars} bars.");
                target.AddRange(batch);
                return !ct.IsCancellationRequested;
            }, null, ct).ConfigureAwait(false);
        }
    }

    private static void ValidateBar(in Kline bar)
    {
        if (bar.High < bar.Low || bar.Open < bar.Low || bar.Open > bar.High || bar.Close < bar.Low || bar.Close > bar.High ||
            bar.Volume < 0 || (bar.HasBuyVolume && (bar.BuyVolume < 0 || bar.BuyVolume > bar.Volume)))
            throw new InvalidDataException("The provider returned invalid OHLCV or aggressor-side totals.");
    }

    private static TradeProvenance SourceProvenance(in Kline bar, int minutes, string sourceIdentity)
    {
        var provenance = bar.Provenance;
        if ((provenance.Quality & ~KnownQuality) != 0 || provenance.ResolutionSeconds < 0 || provenance.ObservedTradeCount < 0 ||
            provenance.ResolutionOffsetSeconds < 0 || (provenance.ResolutionSeconds == 0 ? provenance.ResolutionOffsetSeconds != 0 : provenance.ResolutionOffsetSeconds >= provenance.ResolutionSeconds))
            throw new InvalidDataException("Candle history contains invalid source metadata.");
        var open = FromMs(bar.OpenTimeMs);
        int resolution = provenance.ResolutionSeconds > 0 ? provenance.ResolutionSeconds : minutes * 60;
        int phase = (int)(open.Ticks / TimeSpan.TicksPerSecond % resolution);
        bool exactPhase = open.Ticks % TimeSpan.TicksPerSecond == 0;
        if (provenance.ResolutionOffsetSeconds != 0 && provenance.ResolutionOffsetSeconds != phase)
            throw new InvalidDataException("The declared candle phase does not match its source open.");
        provenance = provenance with {
            Quality = (provenance.Quality & ~(DataQuality.Raw | DataQuality.Compact)) | DataQuality.Synthetic | (exactPhase ? DataQuality.None : DataQuality.Partial),
            ResolutionSeconds = resolution, ResolutionOffsetSeconds = phase,
            SourceIdentity = provenance.SourceIdentity ?? sourceIdentity,
            SourceEventId = null,
            HasKnownAvailability = exactPhase && (provenance == default || provenance.HasKnownAvailability),
        };
        _ = provenance.SourceWindow(open);
        return provenance;
    }

    private static List<Kline> Normalize(IEnumerable<Kline> bars, DateTime from, DateTime to, int minutes, string sourceIdentity)
    {
        var latest = new Dictionary<long, Kline>();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        long first = ToMs(from), last = ToMs(to);
        foreach (var bar in bars)
        {
            if (bar.OpenTimeMs < first || bar.OpenTimeMs >= last) continue;
            ValidateBar(bar);
            var provenance = SourceProvenance(bar, minutes, sourceIdentity);
            var identity = provenance.SourceIdentity!;
            if (!sources.TryGetValue(identity, out var shared)) sources.Add(identity, shared = identity);
            latest[bar.OpenTimeMs] = bar with { Provenance = provenance with { SourceIdentity = shared } };
        }
        return latest.Values.OrderBy(k => k.OpenTimeMs).ToList();
    }

    private static string CachePath(Instrument inst, DateTime month, int minutes, string tag)
    {
        static string Clean(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tag + "\u001f" + inst.TickSize.ToString(CultureInfo.InvariantCulture))))[..16];
        return Path.Combine(CacheRoot!, Clean(inst.FeedId), Clean(inst.Symbol), $"{month:yyyy-MM}.{minutes}m.{identity}.bin");
    }

    /// <summary>Last stored candle endpoint, not a promise of continuous trade coverage.</summary>
    public static DateTime? CachedUntil(IKlineHistory source, Instrument instrument, int minutes)
    {
        if (CacheRoot == null) return null;
        var now = DateTime.UtcNow;
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        string tag = source.CacheTag + (source is IDataScopeFeed scoped ? "\u001f" + scoped.DataScopeKey : "");
        for (int i = 0; i < 3; i++, month = month.AddMonths(-1))
        {
            var cache = ReadCache(CachePath(instrument, month, minutes, tag), instrument, minutes, month);
            if (cache?.Bars is { Count: > 0 } bars) return FromMs(bars[^1].OpenTimeMs).AddSeconds(bars[^1].Provenance.ResolutionSeconds);
        }
        return null;
    }

    private static byte[] CacheDigest(Instrument instrument, int minutes, DateTime from, DateTime to, DateTime updated, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(header, CacheMagic);
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(instrument.TickSize, bits);
        for (int part = 0; part < bits.Length; part++) BinaryPrimitives.WriteInt32LittleEndian(header[(4 + part * 4)..], bits[part]);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], minutes);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], from.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(header[32..], to.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(header[40..], updated.Ticks);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header); hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    private static MonthCache? ReadCache(string path, Instrument inst, int minutes, DateTime month)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumCacheBytes) return null;
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != CacheMagic || reader.ReadDecimal() != inst.TickSize || reader.ReadInt32() != minutes) return null;
            var from = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var to = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var updated = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            if (from < month || from >= to || to > month.AddMonths(1)) return null;
            byte[] digest = reader.ReadBytes(32);
            var payload = reader.ReadBytes(checked((int)(stream.Length - stream.Position)));
            if (!CacheDigest(inst, minutes, from, to, updated, payload).AsSpan().SequenceEqual(digest)) return null;
            using var data = new BinaryReader(new MemoryStream(payload));
            int sourceCount = data.ReadInt32();
            if (sourceCount < 0 || sourceCount > MaximumCacheBars || sourceCount > payload.Length) return null;
            var sources = new string[sourceCount];
            var uniqueSources = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = data.ReadString();
                if (!uniqueSources.Add(sources[i])) return null;
            }
            int metadataCount = data.ReadInt32();
            if (metadataCount < 0 || metadataCount > MaximumCacheBars || (long)metadataCount * 26 > payload.Length - data.BaseStream.Position) return null;
            var metadata = new TradeProvenance[metadataCount];
            for (int i = 0; i < metadata.Length; i++)
            {
                var quality = (DataQuality)data.ReadByte();
                int resolution = data.ReadInt32(), sourceIndex = data.ReadInt32();
                long revision = data.ReadInt64();
                int observed = data.ReadInt32();
                byte known = data.ReadByte();
                int phase = data.ReadInt32();
                if ((quality & ~KnownQuality) != 0 || (quality & DataQuality.Synthetic) == 0 || (quality & (DataQuality.Raw | DataQuality.Compact)) != 0 ||
                    resolution <= 0 || phase < 0 || phase >= resolution || observed < 0 || known > 1 || sourceIndex < 0 || sourceIndex >= sources.Length) return null;
                metadata[i] = new TradeProvenance(quality, resolution, sources[sourceIndex], revision, observed, known != 0, phase);
            }
            int count = data.ReadInt32();
            if (count < 0 || count > MaximumCacheBars || (long)count * 109 > payload.Length - data.BaseStream.Position) return null;
            var bars = new List<Kline>(count);
            long first = ToMs(from), last = ToMs(to), previous = long.MinValue;
            for (int i = 0; i < count; i++)
            {
                long time = data.ReadInt64();
                decimal open = data.ReadDecimal(), high = data.ReadDecimal(), low = data.ReadDecimal(), close = data.ReadDecimal(), volume = data.ReadDecimal(), buy = data.ReadDecimal();
                byte hasBuy = data.ReadByte();
                int metadataIndex = data.ReadInt32();
                if (time < first || time >= last || time <= previous || hasBuy > 1 || (uint)metadataIndex >= (uint)metadata.Length) return null;
                var provenance = metadata[metadataIndex];
                _ = provenance.SourceWindow(FromMs(time));
                var bar = new Kline(time, open, high, low, close, volume, buy, hasBuy != 0, provenance);
                ValidateBar(bar);
                if (SourceProvenance(bar, minutes, provenance.SourceIdentity!) != provenance) return null;
                bars.Add(bar);
                previous = time;
            }
            return data.BaseStream.Position == data.BaseStream.Length ? new MonthCache(from, to, updated, bars) : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException) { return null; }
    }

    private static void WriteCache(string path, Instrument inst, int minutes, MonthCache cache, CancellationToken ct)
    {
        if (cache.Bars.Count > MaximumCacheBars) throw new InvalidDataException("Candle history exceeds the bounded monthly cache capacity.");
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        var metadata = new Dictionary<TradeProvenance, int>();
        foreach (var bar in cache.Bars)
        {
            ct.ThrowIfCancellationRequested();
            var provenance = bar.Provenance;
            sources.TryAdd(provenance.SourceIdentity!, sources.Count);
            metadata.TryAdd(provenance, metadata.Count);
        }
        long capacity = 12L + metadata.Count * 26L + cache.Bars.Count * 109L;
        foreach (var source in sources.Keys)
        {
            ct.ThrowIfCancellationRequested();
            int bytes = Encoding.UTF8.GetByteCount(source);
            capacity += bytes + 1L;
            for (int length = bytes; length >= 128; length >>= 7) capacity++;
            if (capacity + 80 > MaximumCacheBytes) throw new InvalidDataException("Candle history exceeds the bounded monthly cache capacity.");
        }
        if (capacity + 80 > MaximumCacheBytes) throw new InvalidDataException("Candle history exceeds the bounded monthly cache capacity.");
        using var payload = new MemoryStream(checked((int)capacity));
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(sources.Count);
            foreach (var source in sources.Keys) writer.Write(source);
            writer.Write(metadata.Count);
            foreach (var provenance in metadata.Keys)
            {
                writer.Write((byte)provenance.Quality); writer.Write(provenance.ResolutionSeconds); writer.Write(sources[provenance.SourceIdentity!]);
                writer.Write(provenance.CorrectionRevision); writer.Write(provenance.ObservedTradeCount);
                writer.Write(provenance.HasKnownAvailability); writer.Write(provenance.ResolutionOffsetSeconds);
            }
            writer.Write(cache.Bars.Count);
            foreach (var bar in cache.Bars)
            {
                ct.ThrowIfCancellationRequested();
                writer.Write(bar.OpenTimeMs); writer.Write(bar.Open); writer.Write(bar.High); writer.Write(bar.Low); writer.Write(bar.Close); writer.Write(bar.Volume); writer.Write(bar.BuyVolume); writer.Write(bar.HasBuyVolume);
                writer.Write(metadata[bar.Provenance]);
            }
        }
        if (cache.Bars.Count > MaximumCacheBars || payload.Length + 80 > MaximumCacheBytes) throw new InvalidDataException("Candle history exceeds the bounded monthly cache capacity.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new BinaryWriter(stream);
                writer.Write(CacheMagic); writer.Write(inst.TickSize); writer.Write(minutes); writer.Write(cache.From.Ticks); writer.Write(cache.To.Ticks); writer.Write(cache.Updated.Ticks);
                writer.Write(CacheDigest(inst, minutes, cache.From, cache.To, cache.Updated, payload.GetBuffer().AsSpan(0, (int)payload.Length)));
                writer.Write(payload.GetBuffer().AsSpan(0, (int)payload.Length));
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// Displays OHLC as four negative-ID synthetic events without manufacturing volume or price-level side observations.
    /// Only complete declared source windows inside a selected fallback window may be synthesized.
    /// </summary>
    public static void Synthesize(IReadOnlyList<Kline> klines, Instrument inst, int minutes, IReadOnlyList<(DateTime From, DateTime To)> windows, List<Trade> output)
    {
        if (!CompactHistory.Granularities.Contains(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes));
        int w = 0;
        var prints = new (decimal Vol, TradeSide Side)[4];
        var prices = new decimal[4];
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        string sourceIdentity = inst.Key;
        foreach (var k in klines)
        {
            ValidateBar(k);
            var open = FromMs(k.OpenTimeMs);
            var provenance = SourceProvenance(k, minutes, sourceIdentity);
            var sourceWindow = provenance.SourceWindow(open);
            while (w < windows.Count && windows[w].To <= sourceWindow.From) w++;
            if (w >= windows.Count) break;
            var actualEnd = open.AddSeconds(provenance.ResolutionSeconds);
            if (sourceWindow.From < windows[w].From || sourceWindow.To > windows[w].To || actualEnd > windows[w].To) continue;
            var identity = provenance.SourceIdentity!;
            if (!sources.TryGetValue(identity, out var shared)) sources.Add(identity, shared = identity);
            provenance = provenance with {
                Quality = provenance.Quality | DataQuality.InferredSide,
                SourceIdentity = shared, ObservedTradeCount = 0,
            };
            long quarter = (sourceWindow.To.Ticks - open.Ticks) / 4;
            decimal buy = k.HasBuyVolume ? k.BuyVolume : 0;
            decimal sell = k.HasBuyVolume ? k.Volume - buy : 0;
            bool up = k.Close >= k.Open;
            prices[0] = inst.RoundPrice(k.Open);
            prices[1] = inst.RoundPrice(up ? k.Low : k.High);
            prices[2] = inst.RoundPrice(up ? k.High : k.Low);
            prices[3] = inst.RoundPrice(k.Close);

            if (!k.HasBuyVolume || buy == 0 || sell == 0)
            {
                var side = !k.HasBuyVolume ? TradeSide.None : buy == 0 ? TradeSide.Sell : TradeSide.Buy;
                decimal part = k.Volume / 4;
                for (int j = 0; j < 3; j++) prints[j] = (part, side);
                prints[3] = (k.Volume - part - part - part, side);
            }
            else
            {
                decimal firstBuy = buy / 2, firstSell = sell / 2;
                prints[0] = (firstBuy, TradeSide.Buy);
                prints[1] = (firstSell, TradeSide.Sell);
                prints[2] = (buy - firstBuy, TradeSide.Buy);
                prints[3] = (sell - firstSell, TradeSide.Sell);
            }

            long baseId = SyntheticIdBase + k.OpenTimeMs * 4;
            for (int j = 0; j < 4; j++)
                output.Add(new Trade(baseId + j, open.AddTicks(quarter * j), prices[j], prints[j].Vol, prints[j].Side, provenance));
        }
    }

    /// <summary>Measured source windows, with point prints rounded to full minutes before candle fallback.</summary>
    public static List<(DateTime From, DateTime To)> Coverage(IReadOnlyList<Trade> real)
    {
        var res = new List<(DateTime From, DateTime To)>();
        foreach (var trade in real)
        {
            if (!trade.Provenance.IsMeasured) continue;
            DateTime start, end;
            if (trade.Provenance.ResolutionSeconds > 0) (start, end) = trade.Provenance.SourceWindow(trade.Time);
            else
            {
                start = new DateTime(trade.Time.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
                end = start.AddMinutes(1);
            }
            while (res.Count > 0 && start.Ticks <= res[^1].To.Ticks + 2 * TimeSpan.TicksPerMinute)
            {
                var previous = res[^1];
                res.RemoveAt(res.Count - 1);
                if (previous.From < start) start = previous.From;
                if (previous.To > end) end = previous.To;
            }
            res.Add((start, end));
        }
        return res;
    }

    /// <summary>Subtracts covered intervals from the requested fallback windows.</summary>
    public static List<(DateTime From, DateTime To)> Subtract(IReadOnlyList<(DateTime From, DateTime To)> windows, IReadOnlyList<(DateTime From, DateTime To)> covered)
    {
        var res = new List<(DateTime, DateTime)>();
        foreach (var (wf, wt) in windows)
        {
            var pieces = new List<(DateTime From, DateTime To)> { (wf, wt) };
            foreach (var (cf, ct) in covered)
            {
                var next = new List<(DateTime, DateTime)>();
                foreach (var (pf, pt) in pieces)
                {
                    if (ct <= pf || cf >= pt)
                    {
                        next.Add((pf, pt));
                        continue;
                    }

                    if (cf > pf) next.Add((pf, cf));
                    if (ct < pt) next.Add((ct, pt));
                }

                pieces = next;
            }

            res.AddRange(pieces.Where(p => p.To > p.From));
        }

        return res.OrderBy(p => p.Item1).ToList();
    }

    /// <summary>Merges two chronological lists without changing stable trade identities.</summary>
    public static List<Trade> MergeByTime(List<Trade> a, List<Trade> b)
    {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var res = new List<Trade>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count) res.Add(a[i].Time <= b[j].Time ? a[i++] : b[j++]);
        while (i < a.Count) res.Add(a[i++]);
        while (j < b.Count) res.Add(b[j++]);
        return res;
    }

    /// <summary>Reads public Binance OHLC CSV, with or without a header.</summary>
    public static List<Kline> ParseBinanceCsv(TextReader reader)
    {
        var list = new List<Kline>(45_000);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || !char.IsDigit(line[0])) continue;
            var f = line.Split(',');
            if (f.Length < 10) continue;
            long t = long.Parse(f[0], CultureInfo.InvariantCulture);
            if (t > 100_000_000_000_000) t /= 1000; // Spot archives use microseconds from 2025 onward.
            decimal D(int k) => decimal.Parse(f[k], NumberStyles.Float, CultureInfo.InvariantCulture);
            list.Add(new Kline(t, D(1), D(2), D(3), D(4), D(5), D(9), true));
        }

        return list;
    }
}
