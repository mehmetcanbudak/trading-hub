// CSV convention adapted from PANASV/trading-hub cdb84ea16eee7ac8d6c1b74136d2eeacedadf834.
// Copyright (c) 2026 ralphtradesz and Trading Hub contributors; MIT with Commons Clause (repository LICENSE).
using System.Globalization;
using System.Text.RegularExpressions;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

public static class IbBarImport
{
    public const string FeedId = "ib";
    public static IReadOnlySet<string> SupportedRoots { get; } = new HashSet<string>(new[] { "MNQ", "MES", "M2K", "MYM", "NQ", "ES", "RTY", "YM" }, StringComparer.OrdinalIgnoreCase);
    public enum ImportPolicy { PreserveRecorded, ReplaceCoveredMinutes }
    public sealed record DayPreview(DateOnly Day, int Bars, int RecordedTrades, int CoveredRealMinutes, int StoredSyntheticMinutes);
    public sealed record Preview(string StorageRoot, string Symbol, IReadOnlyList<string> Files, IReadOnlyList<Kline> Bars, IReadOnlyList<DayPreview> Days, IReadOnlyDictionary<string, string> Fingerprints)
    {
        public string? SourceFolder { get; init; }
        public int Conflicts => Days.Sum(d => d.CoveredRealMinutes);
        public string Description => $"{Symbol}: {Bars.Count:N0} one-minute bars across {Days.Count} UTC day(s); {Conflicts:N0} recorded minute conflict(s). Default preserves recorded tick files byte-for-byte. Synthetic history has no real aggressor side or footprint.";
    }
    public sealed record Result(string Symbol, int Bars, int Days, int FilesWritten, string BackupDirectory, ImportPolicy Policy);
    public static Instrument InstrumentFor(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (!SupportedRoots.Contains(symbol)) throw new InvalidDataException($"Unsupported IB root '{symbol}'. Supported: {string.Join(", ", SupportedRoots)}.");
        var spec = FuturesCatalog.Find(symbol) ?? throw new InvalidDataException($"No catalog metadata for {symbol}.");
        return new() { Symbol = spec.Root, FeedId = FeedId, Exchange = spec.Exchange, TickSize = spec.Tick, PointValue = spec.PointValue, VolumeStep = 1 };
    }
    public static string? SymbolFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        return SupportedRoots.OrderByDescending(r => r.Length).FirstOrDefault(r => Regex.IsMatch(name, "^" + r + @"(?:1M)?(?:_|$)"));
    }
    public static List<string> FilesIn(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"IB import folder not found: {folder}");
        return Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).Equals(".csv", StringComparison.OrdinalIgnoreCase)).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
    }
    public static Preview PreviewFolder(string folder, string root, string? symbol = null, CancellationToken ct = default) =>
        PreviewFiles(FilesIn(folder), root, symbol, ct) with { SourceFolder = Path.GetFullPath(folder) };
    public static Preview PreviewFiles(IReadOnlyList<string> files, string root, string? symbol = null, CancellationToken ct = default)
    {
        if (files.Count == 0) throw new InvalidDataException("No CSV files found.");
        var sorted = files.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
        symbol = (symbol ?? SymbolFromFileName(sorted[0]) ?? throw new InvalidDataException("Specify one catalog root, for example MNQ.")).ToUpperInvariant();
        var instrument = InstrumentFor(symbol);
        var bars = new SortedDictionary<long, Kline>();
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in sorted)
        {
            ct.ThrowIfCancellationRequested();
            string? inferred = SymbolFromFileName(file);
            if (inferred != null && inferred != symbol) throw new InvalidDataException($"Mixed-root folder: {Path.GetFileName(file)} identifies {inferred}, not {symbol}.");
            fingerprints[file] = IbBarStore.Fingerprint(file);
            ReadFile(file, instrument, bars, ct);
        }
        if (bars.Count == 0) throw new InvalidDataException("CSV files contain no nonzero-volume bars.");
        var days = new List<DayPreview>();
        foreach (var group in bars.Values.GroupBy(k => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime)))
        {
            var day = group.Key;
            string recordedPath = TickFile.DayPath(root, FeedId, symbol, day);
            string barPath = TickFile.DayPath(root, IbBarStore.BarFolder, symbol, day);
            fingerprints[recordedPath] = IbBarStore.Fingerprint(recordedPath);
            fingerprints[barPath] = IbBarStore.Fingerprint(barPath);
            var recorded = IbBarStore.ReadRecordedDay(root, symbol, day, instrument.TickSize).Where(t => t.Id >= 0).ToList();
            var importedMinutes = group.Select(k => k.OpenTimeMs / 60000).ToHashSet();
            int conflicts = recorded.Select(t => new DateTimeOffset(t.Time).ToUnixTimeMilliseconds() / 60000).Where(importedMinutes.Contains).Distinct().Count();
            int storedMinutes = IbBarStore.ReadChecked(barPath, instrument.TickSize).Where(t => t.Id < 0).Select(t => t.Time.Ticks / TimeSpan.TicksPerMinute).Distinct().Count();
            days.Add(new(day, group.Count(), recorded.Count, conflicts, storedMinutes));
        }
        return new(Path.GetFullPath(root), symbol, sorted.AsReadOnly(), bars.Values.ToList().AsReadOnly(), days.AsReadOnly(), fingerprints);
    }
    private static void ReadFile(string file, Instrument instrument, SortedDictionary<long, Kline> bars, CancellationToken ct)
    {
        using var reader = new StreamReader(file);
        var header = (reader.ReadLine() ?? throw new InvalidDataException($"Empty CSV: {file}")).Split(',').Select(s => s.Trim().Trim('"').ToLowerInvariant()).ToArray();
        int Column(string name) => Array.IndexOf(header, name) is var index && index >= 0 ? index : throw new InvalidDataException($"{Path.GetFileName(file)}: required column '{name}' missing.");
        int time = Column("time"), open = Column("open"), high = Column("high"), low = Column("low"), close = Column("close"), volume = Column("volume"), contract = Array.IndexOf(header, "contract");
        int max = new[] { time, open, high, low, close, volume, contract }.Max();
        string? line; int row = 1;
        while ((line = reader.ReadLine()) != null)
        {
            row++; ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var fields = line.Split(',').Select(s => s.Trim().Trim('"')).ToArray();
                if (fields.Length <= max) throw new FormatException("Missing CSV field.");
                decimal Number(int index) => decimal.Parse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture);
                long raw = long.Parse(fields[time], CultureInfo.InvariantCulture);
                long ms = raw > 100_000_000_000 ? raw : checked(raw * 1000);
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                if (timestamp.Year < 1970 || ms % 60000 != 0) throw new FormatException("Time must be a UTC minute boundary (Unix seconds or milliseconds).");
                decimal o = Number(open), h = Number(high), l = Number(low), c = Number(close), v = Number(volume);
                if (v < 0 || o <= 0 || l <= 0 || h < l || o > h || o < l || c > h || c < l) throw new FormatException("Invalid OHLCV values.");
                if (o % instrument.TickSize != 0 || h % instrument.TickSize != 0 || l % instrument.TickSize != 0 || c % instrument.TickSize != 0)
                    throw new FormatException("Price is not aligned with the catalog tick size.");
                if (contract >= 0 && fields[contract].Length > 0 && !fields[contract].StartsWith(instrument.Symbol, StringComparison.OrdinalIgnoreCase)) throw new FormatException("Contract does not match the target root.");
                if (v == 0) continue;
                bars[ms] = new(ms, o, h, l, c, v, 0, false); // Ordinal filename then last row wins, deterministically.
            }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentOutOfRangeException)
            { throw new InvalidDataException($"{Path.GetFileName(file)}:{row}: {error.Message}", error); }
        }
    }
    public static Result Commit(Preview preview, string root, ImportPolicy policy = ImportPolicy.PreserveRecorded, IProgress<double>? progress = null, CancellationToken ct = default, Action<int>? beforePublish = null)
    {
        if (!Path.GetFullPath(root).Equals(preview.StorageRoot, StringComparison.Ordinal)) throw new InvalidOperationException("Import target differs from the previewed storage root.");
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        ct.ThrowIfCancellationRequested();
        var instrument = InstrumentFor(preview.Symbol);
        IbBarStore.PublishResult result;
        using (var tickLease = TickFile.TryLock(root, FeedId, preview.Symbol) ?? throw new IOException("IB instrument is recording/in use; stop its live consumers before importing."))
        using (var barLease = TickFile.TryLock(root, IbBarStore.BarFolder, preview.Symbol) ?? throw new IOException("IB bar history is in use by another writer."))
        {
            if (preview.SourceFolder != null && !FilesIn(preview.SourceFolder).Select(Path.GetFullPath).OrderBy(f => f, StringComparer.Ordinal).SequenceEqual(preview.Files))
                throw new IOException("Import preview is stale; CSV folder membership changed. Preview again before publishing.");
            foreach (var fingerprint in preview.Fingerprints)
                if (IbBarStore.Fingerprint(fingerprint.Key) != fingerprint.Value) throw new IOException("Import preview is stale; files changed. Preview again before publishing.");
            var outputs = new Dictionary<string, IReadOnlyList<Trade>>(StringComparer.Ordinal);
            foreach (var group in preview.Bars.GroupBy(k => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime)))
            {
                ct.ThrowIfCancellationRequested();
                string ticks = TickFile.DayPath(root, FeedId, preview.Symbol, group.Key);
                string barPath = TickFile.DayPath(root, IbBarStore.BarFolder, preview.Symbol, group.Key);
                var existing = IbBarStore.ReadChecked(ticks, instrument.TickSize);
                var realMinutes = existing.Where(t => t.Id >= 0).Select(t => t.Time.Ticks / TimeSpan.TicksPerMinute).ToHashSet();
                var bars = group.Where(k => policy == ImportPolicy.ReplaceCoveredMinutes || !realMinutes.Contains(DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime.Ticks / TimeSpan.TicksPerMinute)).ToList();
                var imported = bars.Select(k => k.OpenTimeMs / 60000).ToHashSet();
                var saved = IbBarStore.ReadChecked(barPath, instrument.TickSize);
                if (saved.Any(t => t.Id >= 0)) throw new InvalidDataException("IB bar store contains nonsynthetic IDs; refusing to overwrite it.");
                var savedMinutes = saved.Select(t => new DateTimeOffset(t.Time).ToUnixTimeMilliseconds() / 60000).ToHashSet();
                if (policy == ImportPolicy.PreserveRecorded) bars.RemoveAll(k => savedMinutes.Contains(k.OpenTimeMs / 60000));
                else saved.RemoveAll(t => imported.Contains(new DateTimeOffset(t.Time).ToUnixTimeMilliseconds() / 60000));
                if (bars.Count > 0)
                {
                    saved.AddRange(IbBarStore.Synthesize(bars, instrument, group.Key));
                    outputs[barPath] = saved.OrderBy(t => t.Time).ThenBy(t => t.Id).ToList();
                }
                if (policy == ImportPolicy.ReplaceCoveredMinutes && existing.Any(t => imported.Contains(new DateTimeOffset(t.Time).ToUnixTimeMilliseconds() / 60000)))
                    outputs[ticks] = existing.Where(t => !imported.Contains(new DateTimeOffset(t.Time).ToUnixTimeMilliseconds() / 60000)).OrderBy(t => t.Time).ThenBy(t => t.Id).ToList();
                progress?.Report(0.5 * (outputs.Count + 1) / (preview.Days.Count + 1));
            }
            ct.ThrowIfCancellationRequested();
            result = IbBarStore.Publish(root, instrument, outputs, ct, beforePublish);
        }
        if (result.Files > 0) IbBarStore.NotifyHistoryChanged(root, preview.Symbol);
        progress?.Report(1);
        return new(preview.Symbol, preview.Bars.Count, preview.Days.Count, result.Files, result.BackupDirectory, policy);
    }
}
