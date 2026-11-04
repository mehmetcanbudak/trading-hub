using System.Security.Cryptography;
using System.Text.Json;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>IB OHLCV is never appended to the real tick recorder. Negative synthetic IDs remain negative at every read boundary.</summary>
public static class IbBarStore
{
    public const string BarFolder = "ib-bars";
    internal static event Action<string, string>? HistoryChanged;
    internal static void NotifyHistoryChanged(string root, string symbol) => HistoryChanged?.Invoke(root, symbol);
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
        string path = TickFile.DayPath(root, IbBarImport.FeedId, symbol, day);
        if (!File.Exists(path)) return new();
        using var lease = TickFile.TryLock(root, IbBarImport.FeedId, symbol, failOnNonSharingError: true);
        if (lease != null || !TickFile.HasActiveRecordingWriter(root, IbBarImport.FeedId, symbol)) return ReadChecked(path, expectedTickSize);
        var rows = TickFile.ReadAppendPrefix(path, out int dropped, expectedTickSize);
        if (dropped != 0) throw new InvalidDataException($"IB recorded history has {dropped} corrupt rows: {path}");
        if (!TickFile.HasActiveRecordingWriter(root, IbBarImport.FeedId, symbol)) return ReadChecked(path, expectedTickSize);
        return rows;
    }

    public static List<Trade> ReadDay(string root, string symbol, DateOnly day)
    {
        var recorded = TickFile.SortAndDedupe(ReadRecordedDay(root, symbol, day));
        var realMinutes = recorded.Where(t => t.Id >= 0).Select(t => t.Time.Ticks / TimeSpan.TicksPerMinute).ToHashSet();
        var synthetic = ReadChecked(TickFile.DayPath(root, BarFolder, symbol, day));
        if (synthetic.Any(t => t.Id >= 0)) throw new InvalidDataException("IB bar store contains a nonsynthetic trade ID.");
        var legacyMinutes = recorded.Where(t => t.Id < 0).Select(t => t.Time.Ticks / TimeSpan.TicksPerMinute).ToHashSet();
        return recorded.Where(t => t.Id >= 0 || !realMinutes.Contains(t.Time.Ticks / TimeSpan.TicksPerMinute))
            .Concat(synthetic.Where(t => !realMinutes.Contains(t.Time.Ticks / TimeSpan.TicksPerMinute) && !legacyMinutes.Contains(t.Time.Ticks / TimeSpan.TicksPerMinute)))
            .OrderBy(t => t.Time).ThenBy(t => t.Id).ToList();
    }
    public static IReadOnlyList<Trade> LoadRange(string root, string symbol, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var rows = new List<Trade>();
        for (var day = DateOnly.FromDateTime(from); day <= DateOnly.FromDateTime(to); day = day.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(ReadDay(root, symbol, day).Where(t => t.Time >= from && t.Time < to));
        }
        return rows;
    }
    public static void MergeBars(string root, Instrument instrument, IReadOnlyList<Kline> bars, CancellationToken ct)
    {
        if (bars.Count == 0) return;
        // Bar-only writers have their own lease; the tick recorder can continue safely in ib/.
        using var lease = TickFile.TryLock(root, BarFolder, instrument.Symbol) ?? throw new IOException("IB bar history is in use by another writer.");
        var outputs = new Dictionary<string, IReadOnlyList<Trade>>(StringComparer.Ordinal);
        foreach (var group in bars.Where(b => b.Volume > 0).GroupBy(b => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(b.OpenTimeMs).UtcDateTime)))
        {
            ct.ThrowIfCancellationRequested();
            string path = TickFile.DayPath(root, BarFolder, instrument.Symbol, group.Key);
            var old = ReadChecked(path, instrument.TickSize);
            if (old.Any(t => t.Id >= 0)) throw new InvalidDataException("IB bar store contains nonsynthetic IDs; refusing to overwrite it.");
            var covered = group.Select(b => b.OpenTimeMs / 60000).ToHashSet();
            old.RemoveAll(t => covered.Contains(new DateTimeOffset(t.Time).ToUnixTimeMilliseconds() / 60000));
            var fresh = Synthesize(group.GroupBy(b => b.OpenTimeMs).Select(b => b.Last()).OrderBy(b => b.OpenTimeMs).ToList(), instrument, group.Key);
            old.AddRange(fresh);
            outputs[path] = old.OrderBy(t => t.Time).ThenBy(t => t.Id).ToList();
        }
        Publish(root, instrument, outputs, ct);
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
    /// <summary>Stages every output, preserves originals and journals each publish. Per-file rename is atomic; the set is not.</summary>
    internal static PublishResult Publish(string root, Instrument instrument, IReadOnlyDictionary<string, IReadOnlyList<Trade>> outputs, CancellationToken ct, Action<int>? beforePublish = null)
    {
        string transaction = Path.Combine(root, ".ib-import-backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        var entries = outputs.Select((pair, index) => new Entry(pair.Key, Path.Combine(transaction, index + ".original"), Path.Combine(transaction, index + ".staged"), File.Exists(pair.Key))).ToList();
        string journal = Path.Combine(transaction, "transaction.json");
        var published = new List<Entry>();
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                if (entry.Existed) File.Copy(entry.Target, entry.Backup);
                using var writer = new TickFile.Writer(entry.Staged, instrument);
                foreach (var row in outputs[entry.Target]) writer.Write(row);
                writer.Flush();
            }
            File.WriteAllText(journal, JsonSerializer.Serialize(new { State = "staged", Entries = entries }));
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                beforePublish?.Invoke(i);
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Target)!);
                File.Move(entry.Staged, entry.Target, true);
                published.Add(entry);
                File.WriteAllText(journal, JsonSerializer.Serialize(new { State = "publishing", Published = published.Count, Entries = entries }));
            }
            File.WriteAllText(journal, JsonSerializer.Serialize(new { State = "committed", Entries = entries }));
            return new(transaction, entries.Count);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<string>();
            foreach (var entry in published.AsEnumerable().Reverse())
            {
                try
                {
                    if (entry.Existed) File.Copy(entry.Backup, entry.Target, true);
                    else File.Delete(entry.Target);
                }
                catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
            }
            try { File.WriteAllText(journal, JsonSerializer.Serialize(new { State = rollbackErrors.Count == 0 ? "rolled-back" : "recovery-required", Error = error.Message, RollbackErrors = rollbackErrors, Entries = entries })); }
            catch (Exception journalError) { rollbackErrors.Add("Could not update recovery journal: " + journalError.Message); }
            if (error is OperationCanceledException && published.Count == 0 && rollbackErrors.Count == 0)
                throw new OperationCanceledException($"IB import canceled before publication. Staging journal: {transaction}", error, ct);
            throw new IOException($"IB publication failed; {(rollbackErrors.Count == 0 ? "original files restored" : "manual recovery required")}. Backups and journal: {transaction}", error);
        }
    }
    private sealed record Entry(string Target, string Backup, string Staged, bool Existed);
}
