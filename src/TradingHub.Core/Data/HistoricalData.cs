using System.Globalization;
using System.IO.Compression;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Un día de operaciones guardado en disco.</summary>
public sealed record DayData(DateOnly Day, string Path, long Bytes);

/// <summary>Todo lo guardado de un instrumento.</summary>
public sealed record SymbolData(string FeedId, string Symbol, IReadOnlyList<DayData> Days)
{
    /// <summary>Historia combinada de una conexión de futuros con la de Databento (ver <see cref="HistorySources"/>).</summary>
    public bool Combined { get; init; }

    public long Bytes => Days.Sum(d => d.Bytes);
    public DateOnly First => Days[0].Day;
    public DateOnly Last => Days[^1].Day;
}

/// <summary>
/// Datos históricos en disco ({raíz}/{feed}/{símbolo}/{día}.vtk): índice, lectura por rango,
/// escritura de días completos y borrado. Lo usan el gráfico, la sección Datos, el replay y el backtester.
/// </summary>
public static class HistoricalData
{
    public static List<SymbolData> Scan(string root)
    {
        var result = new List<SymbolData>();
        if (!Directory.Exists(root)) return result;
        foreach (var feedDir in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(feedDir) is IbBarImport.FeedId or IbBarStore.BarFolder) continue;
            foreach (var symDir in Directory.EnumerateDirectories(feedDir))
            {
                var days = new List<DayData>();
                foreach (var group in Directory.EnumerateFiles(symDir).Where(p => p.EndsWith(".vtk", StringComparison.Ordinal) || p.EndsWith(".vtk.br", StringComparison.Ordinal))
                    .GroupBy(p => Path.GetFileNameWithoutExtension(p.EndsWith(".br", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(p) : p)))
                {
                    if (!DateOnly.TryParseExact(group.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                    var selected = group.OrderBy(p => p.EndsWith(".br", StringComparison.Ordinal)).First();
                    days.Add(new DayData(d, selected, group.Sum(p => new FileInfo(p).Length)));
                }

                if (days.Count == 0) continue;
                days.Sort((a, b) => a.Day.CompareTo(b.Day));
                result.Add(new SymbolData(Path.GetFileName(feedDir), Path.GetFileName(symDir), days));
            }
        }

        // Recover before inspecting either constituent folder; one symbol is one logical dataset.
        foreach (string symbol in IbHistoryCoordinator.Symbols(root))
        {
            using var snapshot = IbBarStore.OpenSnapshot(root, symbol);
            var days = snapshot.Days();
            if (days.Count > 0) result.Add(new SymbolData(IbBarImport.FeedId, symbol, days));
        }
        result.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return result;
    }

    /// <summary>¿Hay operaciones guardadas ese día? Para «rithmic» cuenta también lo de Databento (ver <see cref="HistorySources"/>).</summary>
    public static bool HasDay(string root, string feedId, string symbol, DateOnly day) => HistorySources.HasDay(root, feedId, symbol, day);

    public static void DeleteDays(string root, string feedId, string symbol, IEnumerable<DateOnly> days)
    {
        if (feedId == IbBarImport.FeedId)
        {
            IbBarStore.DeleteDays(root, symbol, days);
            return;
        }
        foreach (var d in days)
        {
            var p = TickFile.DayPath(root, feedId, symbol, d);
            if (File.Exists(p)) File.Delete(p);
            var archive = TickArchive.PathFor(p);
            if (File.Exists(archive)) File.Delete(archive);
        }

        var dir = Path.GetDirectoryName(TickFile.DayPath(root, feedId, symbol, DateOnly.MinValue))!;
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    /// <summary>Operaciones entre dos instantes (UTC), ordenadas, leyendo día por día (de las fuentes de <see cref="HistorySources"/>).</summary>
    public static IEnumerable<Trade> Read(string root, string feedId, string symbol, DateTime fromUtc, DateTime toUtc)
    {
        if (feedId == IbBarImport.FeedId)
        {
            // Materialize before the first yield so replay never owns the coordinator for its playback lifetime.
            foreach (var trade in IbBarStore.LoadRange(root, symbol, fromUtc, toUtc)) yield return trade;
            yield break;
        }
        for (var d = DateOnly.FromDateTime(fromUtc); d <= DateOnly.FromDateTime(toUtc); d = d.AddDays(1))
        {
            var list = HistorySources.ReadDay(root, feedId, symbol, d);
            foreach (var t in list)
            {
                if (t.Time < fromUtc) continue;
                if (t.Time >= toUtc) yield break;
                yield return t;
            }
        }
    }

    /// <summary>
    /// Read by source availability rather than chart location. Earlier stored days are examined too:
    /// a long candle can open before the requested start and become available inside it.
    /// Matching events are bounded; cancellation remains observable while scanning stored days.
    /// </summary>
    public static List<Trade> ReadAvailable(string root, string feedId, string symbol, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default, int maxEvents = 2_000_000)
    {
        if (maxEvents <= 0) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        var window = new HistoryWindow(maxEvents);
        if (toUtc <= fromUtc) return [];
        using var ibSnapshot = feedId == IbBarImport.FeedId ? IbBarStore.OpenSnapshot(root, symbol, ct) : null;
        foreach (var day in ibSnapshot?.AvailableDays() ?? HistorySources.AvailableDays(root, feedId, symbol))
        {
            ct.ThrowIfCancellationRequested();
            if (day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) >= toUtc) break;
            IEnumerable<Trade> rows = ibSnapshot != null ? ibSnapshot.ReadDay(day) : HistorySources.EnumerateDay(root, feedId, symbol, day, ct);
            foreach (var trade in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (!trade.HasKnownAvailability && trade.Time < toUtc)
                    throw new InvalidDataException("Stored source availability is unknown. Reimport the source with declared timing before causal replay.");
                var available = trade.AvailableAt;
                window.Observe(trade, available >= fromUtc && available < toUtc);
                if (window.Truncated)
                    throw new InvalidDataException("The selected availability range exceeds the bounded replay event capacity. Select a shorter range.");
            }
        }
        var result = window.Finish();
        result.Sort(static (left, right) =>
        {
            int order = left.AvailableAt.CompareTo(right.AvailableAt);
            if (order == 0) order = left.Time.CompareTo(right.Time);
            return order != 0 ? order : left.Id.CompareTo(right.Id);
        });
        return result;
    }

    /// <summary>Guarda un día completo (se mezcla con lo que ya hubiera, sin duplicar).</summary>
    /// <param name="replace">El día bajado está completo: reemplaza lo grabado en vivo en vez de mezclarlo (no duplica).</param>
    public static void WriteDay(string root, Instrument instrument, DateOnly day, IReadOnlyList<Trade> trades, bool replace = false)
    {
        if (trades.Any(t => t.Id < 0 && (t.Provenance.Quality & DataQuality.Compact) != 0))
            throw new ArgumentException("Display summaries cannot replace lossless tick history.", nameof(trades));
        if (instrument.FeedId is IbBarImport.FeedId or IbBarStore.BarFolder)
        {
            IbBarStore.WriteDay(root, instrument, day, trades, replace);
            return;
        }
        var path = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, day);
        var all = new List<Trade>(trades.Count);
        var existing = TickArchive.Resolve(path);
        bool rawReplacement = replace && trades.All(t => t.IsObservedTrade);
        var previous = existing != null && !rawReplacement ? HistorySources.ReadSourceFile(existing, instrument.FeedId, instrument.Symbol, requireComplete: true) : null;
        if (!rawReplacement && previous != null) all.AddRange(previous);
        string sourceIdentity = HistorySources.DefaultSourceIdentity(instrument.FeedId, instrument.Symbol);
        foreach (var trade in trades)
            all.Add(trade.Provenance.SourceIdentity != null ? trade :
                new Trade(trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side, trade.Provenance with { SourceIdentity = sourceIdentity }));
        all = TickFile.SortAndDedupe(all);
        // Finer measurements win for the entire source window, not just the proxy's arbitrary print minute.
        var recordedTimes = all.Where(t => t.Provenance.IsMeasured).Select(t => t.Time.Ticks).Order().ToList();
        all.RemoveAll(t => OverlapsMeasured(t, recordedTimes));
        if (previous != null && all.Count == previous.Count && all.Zip(previous).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Time == pair.Second.Time && pair.First.Price == pair.Second.Price &&
            pair.First.Volume == pair.Second.Volume && pair.First.Side == pair.Second.Side && pair.First.Provenance == pair.Second.Provenance)) return;
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var w = new TickFile.Writer(tmp, instrument, includeProvenance: true))
                foreach (var t in all)
                    if (DateOnly.FromDateTime(t.Time) == day) w.Write(t);
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    internal static bool OverlapsMeasured(in Trade trade, List<long> recordedTimes)
    {
        if (trade.Provenance.IsMeasured || recordedTimes.Count == 0) return false;
        long from, to;
        if (trade.Provenance.ResolutionSeconds > 0)
        {
            var window = trade.Provenance.SourceWindow(trade.Time);
            from = window.From.Ticks; to = window.To.Ticks;
        }
        else
        {
            from = trade.Time.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute;
            to = Math.Min(DateTime.MaxValue.Ticks, from + TimeSpan.TicksPerMinute);
        }
        int index = recordedTimes.BinarySearch(from);
        if (index < 0) index = ~index;
        return index < recordedTimes.Count && recordedTimes[index] < to;
    }
}

/// <summary>
/// Descarga días completos de los archivos públicos de los exchanges (mucho más rápido que la API):
/// Binance (data.binance.vision, aggTrades diarios en zip) y Bybit (public.bybit.com, csv.gz).
/// </summary>
public static class PublicArchives
{
    private static readonly Lazy<HttpClient> DefaultHttp = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

    public static bool Supports(string feedId) => feedId is "binance-futures" or "binance-spot" or "bybit-linear";

    public static string? DailyUrl(string feedId, string symbol, DateOnly day)
    {
        string d = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return feedId switch
        {
            "binance-futures" => $"https://data.binance.vision/data/futures/um/daily/aggTrades/{symbol}/{symbol}-aggTrades-{d}.zip",
            "binance-spot" => $"https://data.binance.vision/data/spot/daily/aggTrades/{symbol}/{symbol}-aggTrades-{d}.zip",
            "bybit-linear" => $"https://public.bybit.com/trading/{symbol}/{symbol}{d}.csv.gz",
            _ => null,
        };
    }

    /// <summary>Descarga un día. Devuelve null si el archivo todavía no está publicado (404).</summary>
    public static async Task<List<Trade>?> DownloadDayAsync(Instrument instrument, DateOnly day, IProgress<double>? progress, CancellationToken ct,
        HttpClient? http = null, Uri? baseOrigin = null)
    {
        var url = DailyUrl(instrument.FeedId, instrument.Symbol, day) ?? throw new NotSupportedException("This connection has no public archives.");
        if (baseOrigin != null) url = new Uri(baseOrigin, new Uri(url).PathAndQuery).AbsoluteUri;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TradingHub/0.2");
        using var resp = await (http ?? DefaultHttp.Value).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0;

        // se baja a memoria con progreso
        var ms = new MemoryStream(total > 0 ? (int)Math.Min(total, int.MaxValue) : 1 << 20);
        await using (var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = await net.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buf, 0, n);
                if (total > 0) progress?.Report(0.8 * ms.Length / total);
            }
        }

        ms.Position = 0;
        var trades = instrument.FeedId == "bybit-linear" ? ParseBybit(ms, instrument.FeedId) : ParseBinance(ms, instrument.FeedId);
        progress?.Report(1);
        return trades;
    }

    private static List<Trade> ParseBinance(Stream zipStream, string sourceIdentity)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = zip.Entries.First(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        var list = new List<Trade>(1 << 18);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || !char.IsDigit(line[0])) continue; // encabezado
            // agg_id, precio, cantidad, primer_id, último_id, tiempo, comprador_es_maker[, mejor_precio]
            var span = line.AsSpan();
            long id = long.Parse(Next(ref span), CultureInfo.InvariantCulture);
            decimal price = decimal.Parse(Next(ref span), NumberStyles.Float, CultureInfo.InvariantCulture);
            decimal qty = decimal.Parse(Next(ref span), NumberStyles.Float, CultureInfo.InvariantCulture);
            long firstTradeId = long.Parse(Next(ref span), CultureInfo.InvariantCulture);
            long lastTradeId = long.Parse(Next(ref span), CultureInfo.InvariantCulture);
            long ts = long.Parse(Next(ref span), CultureInfo.InvariantCulture);
            var makerSpan = Next(ref span);
            bool buyerIsMaker = makerSpan.Equals("true", StringComparison.OrdinalIgnoreCase) || makerSpan.Equals("True", StringComparison.Ordinal);
            // spot publica microsegundos desde 2025
            var time = ts > 100_000_000_000_000 ? DateTime.UnixEpoch.AddTicks(ts * 10) : DateTime.UnixEpoch.AddMilliseconds(ts);
            int count = checked((int)(lastTradeId - firstTradeId + 1));
            if (count <= 0) throw new InvalidDataException("Invalid Binance aggregate trade range.");
            list.Add(new Trade(id, time, price, qty, buyerIsMaker ? TradeSide.Sell : TradeSide.Buy,
                new TradeProvenance(count == 1 ? DataQuality.Raw : DataQuality.Compact, SourceIdentity: sourceIdentity, ObservedTradeCount: count,
                    SourceEventId: id.ToString(CultureInfo.InvariantCulture))));
        }

        return list;
    }

    private static List<Trade> ParseBybit(Stream gzStream, string sourceIdentity)
    {
        using var gz = new GZipStream(gzStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var list = new List<Trade>(1 << 18);
        string? line;
        long lastMs = 0;
        int seq = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || !char.IsDigit(line[0])) continue;
            // timestamp(s), símbolo, lado, tamaño, precio, ...
            var span = line.AsSpan();
            decimal secs = decimal.Parse(Next(ref span), NumberStyles.Float, CultureInfo.InvariantCulture);
            Next(ref span);
            var side = Next(ref span);
            TradeSide aggressor = side.SequenceEqual("Buy") ? TradeSide.Buy : side.SequenceEqual("Sell") ? TradeSide.Sell : TradeSide.None;
            decimal size = decimal.Parse(Next(ref span), NumberStyles.Float, CultureInfo.InvariantCulture);
            decimal price = decimal.Parse(Next(ref span), NumberStyles.Float, CultureInfo.InvariantCulture);
            Next(ref span); // tick direction is not an event identity
            string nativeId = Next(ref span).ToString();
            bool hasNativeId = nativeId.Length > 0;
            long ticks = checked((long)decimal.Round(secs * TimeSpan.TicksPerSecond));
            long ms = checked((long)decimal.Round(secs * 1000));
            seq = ms == lastMs ? seq + 1 : 0;
            lastMs = ms;
            var quality = DataQuality.Raw | (hasNativeId ? DataQuality.None : DataQuality.Partial) |
                (aggressor == TradeSide.None ? DataQuality.InferredSide | DataQuality.Partial : DataQuality.None);
            var provenance = new TradeProvenance(quality, SourceIdentity: sourceIdentity,
                SourceEventId: hasNativeId ? nativeId : $"observation:{Guid.NewGuid():N}");
            list.Add(new Trade(ms * 1000 + seq, DateTime.UnixEpoch.AddTicks(ticks), price, size, aggressor, provenance));
        }

        // el archivo de Bybit viene del más nuevo al más viejo en algunos días
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    private static ReadOnlySpan<char> Next(ref ReadOnlySpan<char> span)
    {
        int i = span.IndexOf(',');
        ReadOnlySpan<char> field;
        if (i < 0)
        {
            field = span;
            span = ReadOnlySpan<char>.Empty;
        }
        else
        {
            field = span[..i];
            span = span[(i + 1)..];
        }

        return field.Trim();
    }
}
