using Newtonsoft.Json;
using TradingHub.Charting;

namespace TradingHub.App.Services;

internal static partial class Workspace
{
    /// <summary>Restores a chart's saved alerts (dropping malformed ones); each re-arms on the bar forming after loading.</summary>
    private static void RestoreChartAlerts(ChartModel model, List<ChartAlert>? saved)
    {
        model.Alerts.Clear();
        model.Alerts.AddRange(ChartAlertEngine.Sanitize(saved));
    }

    /// <summary>The workspace JSON of a chart's alerts (same serializer settings as the workspace file).</summary>
    internal static string ChartAlertsJson(List<ChartAlert> alerts) => JsonConvert.SerializeObject(new TabDto { ChartAlerts = alerts }, Settings);

    /// <summary>Reads a chart tab's alerts from workspace JSON (an older tab without alerts gives none).</summary>
    internal static List<ChartAlert> ChartAlertsFromJson(string tabJson) =>
        ChartAlertEngine.Sanitize(JsonConvert.DeserializeObject<TabDto>(tabJson, Settings)?.ChartAlerts);
}
