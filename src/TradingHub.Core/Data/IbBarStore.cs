using System.Security.Cryptography;
using TradingHub.Common;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>IB OHLCV is never appended to the real tick recorder. Negative synthetic IDs remain negative at every read boundary.</summary>
public static class IbBarStore
{
    public const string BarFolder = "ib-bars";
    internal static event Action<string, string>? HistoryChanged;
    internal static void NotifyHistoryChanged(string root, string symbol) => HistoryChanged?.Invoke(root, symbol);
    /// <summary>Owns one publication generation across a complete load; dispose before network awaits or playback.</summary>
    public static IbHistorySnapshot OpenSnapshot(string root, string symbol, CancellationToken ct = default) =>
        OpenSnapshot(root, symbol, ct, StorageSync.SyncDirectory);
    internal static IbHistorySnapshot OpenSnapshot(string root, string symbol, CancellationToken ct, Func<string, bool> syncDirectory) =>
        new(IbHistoryCoordinator.Enter(root, symbol, ct, syncDirectory));
    public static IbHistoryStamp GetStamp(string root, string symbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var snapshot = OpenSnapshot(root, symbol, ct);
        return snapshot.GetStamp(fromUtc, toUtc);
    }
    public static string Fingerprint(string path)
    {
        if (!File.Exists(path)) return "absent";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static List<Trade> ReadChecked(string path, decimal? expectedTickSize = null)
    {
        if (!File.Exists(path)) return new();
        var tickSize = TickFile.ReadTickSize(path);
        if (tickSize == null || (expectedTickSize.HasValue && tickSize != expectedTickSize))
            throw new InvalidDataException($"Invalid or catalog-mismatched IB history tick metadata; refusing to overwrite: {path}");
        var rows = TickFile.Read(path, out int dropped, requireComplete: true);
        if (dropped != 0) throw new InvalidDataException($"IB history has {dropped} damaged rows; refusing to overwrite: {path}");
        return rows;
    }
    internal static List<Trade> ReadRecordedDay(string root, string symbol, DateOnly day, decimal? expectedTickSize = null)
    {
        using var snapshot = OpenSnapshot(root, symbol);
        return snapshot.ReadRecordedDay(day, expectedTickSize);
    }
    internal static List<Trade> ReadRecordedDayOwned(string root, string symbol, DateOnly day, decimal? expectedTickSize = null)
    {
        string path = TickFile.DayPath(root, IbBarImport.FeedId, symbol, day);
        if (!File.Exists(path)) return new();
        using var lease = TickFile.TryLock(root, IbBarImport.FeedId, symbol, failOnNonSharingError: true);
        if (lease != null || !TickFile.HasActiveRecordingWriter(root, IbBarImport.FeedId, symbol)) return ReadChecked(path, expectedTickSize);
        var rows = TickFile.ReadAppendPrefix(path, out int dropped, expectedTickSize);
        if (dropped != 0) throw new InvalidDataException($"IB recorded history has {dropped} corrupt rows: {path}");
        if (!TickFile.HasActiveRecordingWriter(root, IbBarImport.FeedId, symbol)) return ReadChecked(path, expectedTickSize);
        return rows;
    }

    public static List<Trade> ReadDay(string root, string symbol, DateOnly day, decimal? expectedTickSize = null)
    {
        using var snapshot = OpenSnapshot(root, symbol);
        return snapshot.ReadDay(day, expectedTickSize);
    }
    internal static List<Trade> ReadDayOwned(string root, string symbol, DateOnly day, decimal? expectedTickSize = null)
    {
        var recorded = ReadRecordedDayOwned(root, symbol, day, expectedTickSize);
        string source = HistorySources.DefaultSourceIdentity(IbBarImport.FeedId, symbol);
        for (int i = 0; i < recorded.Count; i++) recorded[i] = HistorySources.AnnotateSource(recorded[i], IbBarImport.FeedId, source);
        recorded = TickFile.SortAndDedupe(recorded);
        var measuredTimes = recorded.Where(t => t.Provenance.IsMeasured).Select(t => t.Time.Ticks).Order().ToList();
        var synthetic = ReadBarDayOwned(root, symbol, day, expectedTickSize);
        recorded.RemoveAll(t => HistoricalData.OverlapsMeasured(t, measuredTimes));
        var legacyMinutes = recorded.Where(t => !t.Provenance.IsMeasured).Select(t => t.Time.Ticks / TimeSpan.TicksPerMinute).ToHashSet();
        return recorded.Concat(synthetic.Where(t => !HistoricalData.OverlapsMeasured(t, measuredTimes) && !legacyMinutes.Contains(t.Time.Ticks / TimeSpan.TicksPerMinute)))
            .OrderBy(t => t.Time).ThenBy(t => t.Id).ToList();
    }
    internal static List<Trade> ReadBarDayOwned(string root, string symbol, DateOnly day, decimal? expectedTickSize = null)
    {
        var rows = ReadChecked(TickFile.DayPath(root, BarFolder, symbol, day), expectedTickSize);
        if (rows.Any(t => t.Id >= 0)) throw new InvalidDataException("IB bar store contains a nonsynthetic trade ID.");
        return rows;
    }
    public static IReadOnlyList<Trade> LoadRange(string root, string symbol, DateTime from, DateTime to, CancellationToken ct = default, decimal? expectedTickSize = null)
    {
        using var snapshot = OpenSnapshot(root, symbol, ct);
        var rows = new List<Trade>();
        for (var day = DateOnly.FromDateTime(from); day <= DateOnly.FromDateTime(to); day = day.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(snapshot.ReadDay(day, expectedTickSize).Where(t => t.Time >= from && t.Time < to));
        }
        return rows;
    }
    public static void MergeBars(string root, Instrument instrument, IReadOnlyList<Kline> bars, CancellationToken ct)
    {
        if (bars.Count == 0) return;
        using (var ownership = IbHistoryCoordinator.Enter(root, instrument.Symbol, ct))
        using (var writers = ownership.Writers(raw: false, bars: true))
        {
            var outputs = new Dictionary<string, IReadOnlyList<Trade>>(StringComparer.Ordinal);
            foreach (var group in bars.Where(b => b.Volume > 0).GroupBy(b => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(b.OpenTimeMs).UtcDateTime)))
            {
                ct.ThrowIfCancellationRequested();
                string path = TickFile.DayPath(ownership.Root, BarFolder, instrument.Symbol, group.Key);
                IbHistoryCoordinator.CheckPath(ownership.Root, path);
                var old = ReadChecked(path, instrument.TickSize);
                if (old.Any(t => t.Id >= 0)) throw new InvalidDataException("IB bar store contains nonsynthetic IDs; refusing to overwrite it.");
                var covered = group.Select(b => b.OpenTimeMs / 60000).ToHashSet();
                old.RemoveAll(t => covered.Contains(new DateTimeOffset(t.Time).ToUnixTimeMilliseconds() / 60000));
                var fresh = Synthesize(group.GroupBy(b => b.OpenTimeMs).Select(b => b.Last()).OrderBy(b => b.OpenTimeMs).ToList(), instrument, group.Key);
                old.AddRange(fresh);
                outputs[path] = old.OrderBy(t => t.Time).ThenBy(t => t.Id).ToList();
            }
            Publish(ownership, instrument, outputs, ct);
        }
    }
    internal static List<Trade> Synthesize(IReadOnlyList<Kline> bars, Instrument instrument, DateOnly day)
    {
        var rows = new List<Trade>();
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        KlineHistory.Synthesize(bars, instrument, 1, new[] { (start, start.AddDays(1)) }, rows);
        if (rows.Any(t => t.Id >= 0)) throw new InvalidDataException("IB synthesis lost its synthetic provenance.");
        return rows.OrderBy(t => t.Time).ThenBy(t => t.Id).ToList();
    }
    public sealed record PublishResult(string BackupDirectory, int Files);
    internal static PublishResult Publish(IbHistoryCoordinator.Ownership ownership, Instrument instrument, IReadOnlyDictionary<string, IReadOnlyList<Trade>> outputs, CancellationToken ct, Action<int>? beforePublish = null) =>
        ownership.Publish(instrument, outputs.Select(pair => new IbHistoryCoordinator.Output(pair.Key, pair.Value)).ToArray(), ct, beforePublish);

    internal static void WriteDay(string root, Instrument instrument, DateOnly day, IReadOnlyList<Trade> trades, bool replace)
    {
        bool recorded = instrument.FeedId == IbBarImport.FeedId;
        using var ownership = IbHistoryCoordinator.Enter(root, instrument.Symbol);
        using var writers = ownership.Writers(raw: recorded, bars: !recorded);
        string folder = recorded ? IbBarImport.FeedId : BarFolder;
        string path = TickFile.DayPath(ownership.Root, folder, instrument.Symbol, day);
        IbHistoryCoordinator.CheckPath(ownership.Root, path);
        bool rawReplacement = replace && (!recorded || trades.All(t => t.IsObservedTrade));
        var all = rawReplacement ? new List<Trade>() : ReadChecked(path, instrument.TickSize);
        string source = HistorySources.DefaultSourceIdentity(instrument.FeedId, instrument.Symbol);
        for (int i = 0; i < all.Count; i++) all[i] = HistorySources.AnnotateSource(all[i], instrument.FeedId, source);
        foreach (var trade in trades) all.Add(HistorySources.AnnotateSource(trade, instrument.FeedId, source));
        var rows = TickFile.SortAndDedupe(all).Where(t => DateOnly.FromDateTime(t.Time) == day).ToList();
        if (recorded)
        {
            var measuredTimes = rows.Where(t => t.Provenance.IsMeasured).Select(t => t.Time.Ticks).Order().ToList();
            rows.RemoveAll(t => HistoricalData.OverlapsMeasured(t, measuredTimes));
        }
        if (!recorded && rows.Any(t => t.Id >= 0)) throw new InvalidDataException("IB bar store requires synthetic trade IDs.");
        WriteSingleDay(ownership, instrument, path, rows);
    }

    internal static int RepairDay(string root, Instrument instrument, DateOnly day)
    {
        bool recorded = instrument.FeedId == IbBarImport.FeedId;
        using var ownership = IbHistoryCoordinator.Enter(root, instrument.Symbol);
        using var writers = ownership.Writers(raw: recorded, bars: !recorded);
        string path = TickFile.DayPath(ownership.Root, recorded ? IbBarImport.FeedId : BarFolder, instrument.Symbol, day);
        IbHistoryCoordinator.CheckPath(ownership.Root, path);
        var rows = TickFile.Read(path, out int dropped);
        if (dropped == 0) return 0;
        if (!recorded && rows.Any(t => t.Id >= 0)) throw new InvalidDataException("IB bar store requires synthetic trade IDs.");
        WriteSingleDay(ownership, instrument, path, rows);
        return dropped;
    }

    private static void WriteSingleDay(IbHistoryCoordinator.Ownership ownership, Instrument instrument, string path, IReadOnlyList<Trade> rows)
    {
        // One atomic day replacement needs no multi-target rollback journal. Keep import backups import-specific.
        string prepared = path + ".ib-write-" + Guid.NewGuid().ToString("N");
        IbHistoryCoordinator.CheckPath(ownership.Root, prepared);
        bool published = false;
        try
        {
            using (var writer = new TickFile.Writer(prepared, instrument, includeProvenance: true))
            {
                foreach (var row in rows) writer.Write(row);
                writer.FlushToDisk();
            }
            ownership.Invalidate();
            File.Move(prepared, path, true);
            published = true;
            StorageSync.SyncDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception error) when (published)
        {
            throw new IOException($"IB day replacement was published, but namespace synchronization failed: {path}", error);
        }
        finally { if (File.Exists(prepared)) File.Delete(prepared); }
    }

    internal static void DeleteDays(string root, string symbol, IEnumerable<DateOnly> days)
    {
        using (var ownership = IbHistoryCoordinator.Enter(root, symbol))
        using (var writers = ownership.Writers(raw: true, bars: true))
        {
            var outputs = new List<IbHistoryCoordinator.Output>();
            foreach (var day in days.Distinct())
            {
                foreach (string folder in new[] { IbBarImport.FeedId, BarFolder })
                {
                    string path = TickFile.DayPath(ownership.Root, folder, symbol, day);
                    IbHistoryCoordinator.CheckPath(ownership.Root, path);
                    if (File.Exists(path)) outputs.Add(new(path, null));
                }
            }
            ownership.Publish(IbBarImport.InstrumentFor(symbol), outputs, default, null);
        }
        NotifyHistoryChanged(root, symbol);
    }
}
