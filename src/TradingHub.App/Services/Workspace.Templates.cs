using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TradingHub.App.Views;
using TradingHub.Charting;
using TradingHub.Common.Logging;
using TradingHub.Indicators;

namespace TradingHub.App.Services;

/// <summary>
/// Plantillas guardadas en disco: de gráfico (estilo, footprint, colores, lo que se muestra e indicadores; sirven para
/// cualquier instrumento y período) y de indicador (sus ajustes, con una predeterminada para los nuevos).
/// </summary>
internal static partial class Workspace
{
    public static string ChartTemplatesDir => Path.Combine(AppPaths.Root, "ChartTemplates");
    public static string IndicatorTemplatesDir => Path.Combine(AppPaths.Root, "IndicatorTemplates");

    private sealed class ChartTemplateDto
    {
        public string Name { get; set; } = "";
        public JObject? Settings { get; set; }
        public double BarWidth { get; set; }
        public List<IndicatorDto> Indicators { get; set; } = new();
    }

    private sealed class IndicatorTemplateDto
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public JObject Properties { get; set; } = new();
        public List<JObject> Series { get; set; } = new();
    }

    private static string FileName(string name) =>
        string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '.' or '/' or ':' ? '_' : c)) + ".json";

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), Settings);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return null;
        }
    }

    private static IReadOnlyList<(string Name, string Path)> Listed<T>(string dir, Func<T, string> name) where T : class =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").Select(f => (Read<T>(f) is { } d ? name(d) : "", f)).Where(x => x.Item1.Length > 0)
                .OrderBy(x => x.Item1, StringComparer.CurrentCultureIgnoreCase).ToList()
            : Array.Empty<(string, string)>();

    // ===================================================================== gráfico

    public static IReadOnlyList<string> ChartTemplateNames() => Listed<ChartTemplateDto>(ChartTemplatesDir, d => d.Name).Select(x => x.Name).ToList();

    /// <summary>Guarda cómo se ve el gráfico (sin el instrumento ni el período) como plantilla.</summary>
    public static void SaveChartTemplate(ChartTab tab, string name)
    {
        var m = tab.Model ?? throw new InvalidOperationException("Chart is not loaded yet");
        var dto = new ChartTemplateDto { Name = name.Trim(), Settings = JObject.FromObject(m.Settings, Serializer), BarWidth = m.Viewport.BarWidth };
        dto.Settings.Remove(nameof(ChartSettings.TimeZoneId));
        foreach (var slot in m.Indicators) dto.Indicators.Add(ToIndicatorDto(slot));
        Directory.CreateDirectory(ChartTemplatesDir);
        File.WriteAllText(Path.Combine(ChartTemplatesDir, FileName(name)), JsonConvert.SerializeObject(dto, Formatting.Indented, Settings));
    }

    /// <summary>Aplica una plantilla a un gráfico: reemplaza su estilo, colores, lo que muestra y sus indicadores.</summary>
    public static bool ApplyChartTemplate(TradingView w, ChartTab tab, string name)
    {
        var path = Listed<ChartTemplateDto>(ChartTemplatesDir, d => d.Name).FirstOrDefault(x => x.Name == name).Path;
        if (path == null || tab.Model is not { } m || Read<ChartTemplateDto>(path) is not { } dto) return false;

        var fresh = new ChartSettings();
        if (dto.Settings != null)
        {
            using var r = dto.Settings.CreateReader();
            Serializer.Populate(r, fresh);
        }

        if (fresh.Layout.Columns.Count == 0) fresh.Layout = FootprintLayout.Presets[0].Clone();
        // todo lo del gráfico menos la zona horaria (es de la app)
        string? tz = m.Settings.TimeZoneId;
        foreach (var p in typeof(ChartSettings).GetProperties().Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0))
            p.SetValue(m.Settings, p.GetValue(fresh));
        m.Settings.TimeZoneId = tz;
        if (dto.BarWidth > 0) m.Viewport.BarWidth = dto.BarWidth;

        foreach (var slot in m.Indicators.ToList()) m.RemoveIndicator(slot);
        AddIndicators(w, m, dto.Indicators);
        m.Rebuild();
        m.ApplyColors();
        Save(w);
        return true;
    }

    public static void DeleteChartTemplate(string name)
    {
        var path = Listed<ChartTemplateDto>(ChartTemplatesDir, d => d.Name).FirstOrDefault(x => x.Name == name).Path;
        if (path != null) File.Delete(path);
    }

    // ===================================================================== indicador

    private static string IndicatorDir(Type t) => Path.Combine(IndicatorTemplatesDir, FileName(t.FullName ?? t.Name)[..^5]);

    public static IReadOnlyList<string> IndicatorTemplateNames(Type t) => Listed<IndicatorTemplateDto>(IndicatorDir(t), d => d.Name).Select(x => x.Name).ToList();

    public static void SaveIndicatorTemplate(Indicator ind, string name)
    {
        var props = SaveIndicator(ind, out var series);
        var dto = new IndicatorTemplateDto { Name = name.Trim(), Type = ind.GetType().FullName!, Properties = props, Series = series };
        var dir = IndicatorDir(ind.GetType());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, FileName(name)), JsonConvert.SerializeObject(dto, Formatting.Indented, Settings));
    }

    public static bool ApplyIndicatorTemplate(Indicator ind, string name)
    {
        var path = Listed<IndicatorTemplateDto>(IndicatorDir(ind.GetType()), d => d.Name).FirstOrDefault(x => x.Name == name).Path;
        if (path == null || Read<IndicatorTemplateDto>(path) is not { } dto) return false;
        LoadIndicator(ind, dto.Properties, dto.Series);
        return true;
    }

    public static void DeleteIndicatorTemplate(Type t, string name)
    {
        var path = Listed<IndicatorTemplateDto>(IndicatorDir(t), d => d.Name).FirstOrDefault(x => x.Name == name).Path;
        if (path != null) File.Delete(path);
    }

    /// <summary>Un indicador nuevo arranca con su plantilla predeterminada (si tiene). Devuelve true si la aplicó.</summary>
    public static bool ApplyDefaultIndicatorTemplate(AppServices svc, Indicator ind) =>
        svc.Settings.DefaultIndicatorTemplates.TryGetValue(ind.GetType().FullName!, out var name) && ApplyIndicatorTemplate(ind, name);
}
