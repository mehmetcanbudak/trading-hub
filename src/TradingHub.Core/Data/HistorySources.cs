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
            var path = TickFile.DayPath(root, folder, symbol, day);
            if (File.Exists(path) && new FileInfo(path).Length > 16) files.Add(path);
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

    /// <summary>
    /// Las operaciones de un día, ordenadas. Con más de una carpeta, la primera manda: de las demás solo entran los
    /// minutos en que la primera no tiene ninguna operación (por ejemplo, las horas en que la app estuvo cerrada y no grabó).
    /// </summary>
    public static List<Trade> ReadDay(string root, string feedId, string symbol, DateOnly day)
    {
        var files = DayFiles(root, feedId, symbol, day);
        if (files.Count == 0) return new List<Trade>();
        var main = TickFile.SortAndDedupe(TickFile.Read(files[0]));
        if (files.Count == 1) return main;

        var covered = new HashSet<long>();
        foreach (var t in main) covered.Add(t.Time.Ticks / TimeSpan.TicksPerMinute);
        var merged = new List<Trade>(main);
        bool added = false;
        for (int f = 1; f < files.Count; f++)
        {
            var extra = TickFile.SortAndDedupe(TickFile.Read(files[f]));
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

        // cada fuente numera a su manera: se ordena por hora (estable: a la misma hora, primero la que manda) y se
        // vuelven a numerar, para que los ids sigan creciendo y no se repitan entre fuentes
        var ordered = merged.Select((t, i) => (t, i)).OrderBy(x => x.t.Time.Ticks).ThenBy(x => x.i).Select(x => x.t).ToList();
        long baseId = Math.Max(0, ordered.Min(t => t.Id));
        for (int i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            ordered[i] = new Trade(baseId + i, t.Time, t.Price, t.Volume, t.Side);
        }

        return ordered;
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
