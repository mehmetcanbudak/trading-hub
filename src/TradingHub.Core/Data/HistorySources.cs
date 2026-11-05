using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// De dónde sale la historia de cada instrumento. Cada fuente guarda lo suyo en {raíz}/{fuente}/{símbolo}: Rithmic (o
/// Tradovate) lo que graba y baja de su servidor, Databento lo que se le compra. Al leer se elige:
/// <list type="bullet">
/// <item>«rithmic»: la historia combinada (lo de Rithmic y, en los minutos que a Rithmic le faltan, lo de Databento);</item>
/// <item>«rithmic-solo»: solo lo de Rithmic;</item>
/// <item>«databento»: solo lo de Databento.</item>
/// </list>
/// Así lo que ya estaba guardado como «rithmic» (backtests, el laboratorio de Claude, los gráficos) sigue viendo toda la historia.
/// </summary>
public static class HistorySources
{
    public const string Databento = "databento";

    /// <summary>Sufijo de «solo esa conexión», sin completar con Databento (rithmic-solo).</summary>
    public const string OnlySuffix = "-solo";

    /// <summary>Las conexiones de futuros de CME cuya historia completa Databento (los mismos contratos continuos).</summary>
    public static bool FilledByDatabento(string feedId) => feedId is "rithmic" or "tradovate";

    /// <summary>La carpeta propia de una fuente (rithmic-solo → rithmic).</summary>
    public static string Folder(string feedId) => feedId.EndsWith(OnlySuffix, StringComparison.Ordinal) ? feedId[..^OnlySuffix.Length] : feedId;

    /// <summary>Las carpetas que se leen para una fuente, en orden: la primera manda y las demás completan lo que le falta.</summary>
    public static IReadOnlyList<string> Folders(string feedId)
    {
        if (feedId.EndsWith(OnlySuffix, StringComparison.Ordinal)) return new[] { Folder(feedId) };
        return FilledByDatabento(feedId) ? new[] { feedId, Databento } : new[] { feedId };
    }

    /// <summary>Los archivos de un día que existen, en el orden de <see cref="Folders"/>.</summary>
    public static List<string> DayFiles(string root, string feedId, string symbol, DateOnly day)
    {
        var files = new List<string>(2);
        foreach (var folder in Folders(feedId))
        {
            var path = TickFile.ExistingDayPath(root, folder, symbol, day);
            if (path != null && new FileInfo(path).Length > 16) files.Add(path);
        }
            if (feedId == IbBarImport.FeedId)
            {
                var barPath = TickFile.DayPath(root, IbBarStore.BarFolder, symbol, day);
                if (File.Exists(barPath)) files.Add(barPath);
            }

        return files;
    }

    public static bool HasDay(string root, string feedId, string symbol, DateOnly day) => DayFiles(root, feedId, symbol, day).Count > 0;

    /// <summary>Los días con datos en cualquiera de las carpetas de la fuente, ordenados.</summary>
    public static List<DateOnly> AvailableDays(string root, string feedId, string symbol)
    {
        var days = new SortedSet<DateOnly>();
        foreach (var folder in Folders(feedId))
            foreach (var d in TickFile.AvailableDays(root, folder, symbol))
                days.Add(d);
        if (feedId == IbBarImport.FeedId)
            foreach (var day in TickFile.AvailableDays(root, IbBarStore.BarFolder, symbol)) days.Add(day);
        return days.ToList();
    }

    /// <summary>El tick con que se guardó el instrumento (del último día guardado), o null si no hay archivos.</summary>
    public static decimal? StoredTickSize(string root, string feedId, string symbol)
    {
        var days = AvailableDays(root, feedId, symbol);
        if (days.Count == 0) return null;
        foreach (var file in DayFiles(root, feedId, symbol, days[^1]))
            if (TickFile.ReadTickSize(file) is { } tick)
                return tick;
        return null;
    }

    internal static string DefaultSourceIdentity(string folder, string symbol) =>
        folder is "binance-futures" or "binance-spot" or "bybit-linear" or "agregado" ? folder :
        folder == "oanda" ? "oanda:legacy-untyped" :
        folder == "ctrader" || folder.StartsWith("ctrader-", StringComparison.Ordinal) ? folder + ":" + symbol + ":bid-quote" :
        folder + ":" + symbol;

    internal static Trade AnnotateSource(in Trade trade, string folder, string sourceIdentity)
    {
        if (trade.Provenance.SourceIdentity != null) return trade;
        bool legacy = trade.Id >= 0 && trade.Provenance.Quality == DataQuality.Raw && string.IsNullOrEmpty(trade.Provenance.SourceEventId);
        var provenance = trade.Provenance with { SourceIdentity = sourceIdentity };
        if (legacy && (folder is "oanda" or CsvImport.FeedId or "ctrader" || folder.StartsWith("ctrader-", StringComparison.Ordinal)))
            provenance = new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, 0,
                sourceIdentity, trade.Provenance.CorrectionRevision, 0, HasKnownAvailability: folder != "oanda");
        else if (legacy && folder is "binance-futures" or "binance-spot" or "agregado")
            provenance = new TradeProvenance(DataQuality.Compact | DataQuality.Partial, SourceIdentity: sourceIdentity, ObservedTradeCount: 0,
                SourceEventId: folder == "agregado" ? null : trade.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else if (legacy && folder == "bybit-linear")
            provenance = provenance with { Quality = provenance.Quality | DataQuality.Partial };
        return new Trade(trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side, provenance);
    }

    internal static List<Trade> ReadSourceFile(string path, string folder, string symbol, bool requireComplete = false)
    {
        var rows = TickFile.Read(path, out _, requireComplete);
        string source = DefaultSourceIdentity(folder, symbol);
        for (int i = 0; i < rows.Count; i++) rows[i] = AnnotateSource(rows[i], folder, source);
        return TickFile.SortAndDedupe(rows);
    }

    /// <summary>Stream source days without materializing the raw day. Primary-source minute priority is retained.</summary>
    internal static IEnumerable<Trade> EnumerateDay(string root, string feedId, string symbol, DateOnly day, CancellationToken ct)
    {
        if (feedId == IbBarImport.FeedId)
        {
            foreach (var trade in IbBarStore.ReadDay(root, symbol, day)) { ct.ThrowIfCancellationRequested(); yield return trade; }
            yield break;
        }
        var files = DayFiles(root, feedId, symbol, day);
        HashSet<long>? covered = files.Count > 1 ? [] : null;
        foreach (var path in files)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
            string source = DefaultSourceIdentity(folder, symbol);
            HashSet<long>? added = covered == null ? null : [];
            foreach (var row in TickFile.Enumerate(path, requireOrdered: false))
            {
                ct.ThrowIfCancellationRequested();
                long minute = row.Time.Ticks / TimeSpan.TicksPerMinute;
                if (covered?.Contains(minute) == true) continue;
                added?.Add(minute);
                yield return AnnotateSource(row, folder, source);
            }
            if (added != null) covered!.UnionWith(added);
        }
    }

    /// <summary>
    /// Las operaciones de un día, ordenadas. Con más de una carpeta, la primera manda: de las demás solo entran los
    /// minutos en que la primera no tiene ninguna operación (por ejemplo, las horas en que la app estuvo cerrada y no grabó).
    /// </summary>
    public static List<Trade> ReadDay(string root, string feedId, string symbol, DateOnly day)
    {
        if (feedId == IbBarImport.FeedId) return IbBarStore.ReadDay(root, symbol, day);
        var files = DayFiles(root, feedId, symbol, day);
        if (files.Count == 0) return new List<Trade>();
        string mainFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(files[0]))!);
        var main = ReadSourceFile(files[0], mainFolder, symbol);
        if (files.Count == 1) return main;

        var covered = new HashSet<long>();
        foreach (var t in main) covered.Add(t.Time.Ticks / TimeSpan.TicksPerMinute);
        var merged = new List<Trade>(main);
        bool added = false;
        for (int f = 1; f < files.Count; f++)
        {
            string extraFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(files[f]))!);
            var extra = ReadSourceFile(files[f], extraFolder, symbol);
            var minutes = new HashSet<long>();
            foreach (var t in extra)
            {
                long minute = t.Time.Ticks / TimeSpan.TicksPerMinute;
                if (covered.Contains(minute)) continue;
                merged.Add(t);
                minutes.Add(minute);
                added = true;
            }

            covered.UnionWith(minutes);
        }

        if (!added) return main;

        // Provider-qualified identity removes the need to renumber independent sources.
        return merged.OrderBy(t => t.Time).ThenBy(t => t.Id).ToList();
    }

    /// <summary>
    /// La historia guardada como se elige para probar: lo de cada carpeta y, para los futuros que tienen historia de su
    /// conexión y de Databento, además la combinada («rithmic», marcada <see cref="SymbolData.Combined"/>) y la de solo
    /// esa conexión («rithmic-solo»). Las de un mismo instrumento quedan juntas, primero la combinada.
    /// </summary>
    public static List<SymbolData> Datasets(string root)
    {
        var stored = HistoricalData.Scan(root);
        var databento = stored.Where(s => s.FeedId == Databento).ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<List<SymbolData>>();
        foreach (var s in stored.Where(s => s.FeedId != Databento))
        {
            if (!FilledByDatabento(s.FeedId) || !databento.TryGetValue(s.Symbol, out var db))
            {
                groups.Add(new List<SymbolData> { s });
                continue;
            }

            var byDay = new SortedDictionary<DateOnly, DayData>();
            foreach (var d in db.Days) byDay[d.Day] = d;
            foreach (var d in s.Days) byDay[d.Day] = byDay.TryGetValue(d.Day, out var other) ? d with { Bytes = d.Bytes + other.Bytes } : d;
            var group = new List<SymbolData>
            {
                new(s.FeedId, s.Symbol, byDay.Values.ToList()) { Combined = true },
                new(s.FeedId + OnlySuffix, s.Symbol, s.Days),
            };
            if (owned.Add(db.Symbol)) group.Add(db);
            groups.Add(group);
        }

        foreach (var db in databento.Values.Where(d => !owned.Contains(d.Symbol))) groups.Add(new List<SymbolData> { db });
        return groups.OrderByDescending(g => g.Max(x => x.Bytes)).SelectMany(g => g).ToList();
    }

    /// <summary>Cómo se llama la fuente de un conjunto de <see cref="Datasets"/> (sin el nombre propio de la conexión).</summary>
    public static string Describe(SymbolData s)
    {
        if (s.FeedId == Databento) return "Databento";
        if (s.FeedId == IbBarImport.FeedId) return "IB recorded ticks + synthetic OHLCV (not real order flow)";
        if (s.FeedId.EndsWith(OnlySuffix, StringComparison.Ordinal)) return "only " + Pretty(Folder(s.FeedId));
        return s.Combined ? $"{Pretty(s.FeedId)} + Databento" : Pretty(s.FeedId);
    }

    private static string Pretty(string feedId) => feedId switch
    {
        "rithmic" => "Rithmic",
        "tradovate" => "Tradovate",
        _ => TradingHub.Common.EnglishText.Of(feedId),
    };
}
