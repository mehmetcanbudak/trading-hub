using Newtonsoft.Json;
using TradingHub.App.Views;
using TradingHub.Charting;

namespace TradingHub.App.Services;

internal static partial class Workspace
{
    /// <summary>
    /// Restores a chart's saved alerts and those waiting for the instruments it showed before (dropping malformed ones);
    /// each re-arms on the bar forming after loading.
    /// </summary>
    private static void RestoreChartAlerts(ChartTab tab, TabDto td)
    {
        var model = tab.Model!;
        model.Alerts.Clear();
        model.Alerts.AddRange(ChartAlertEngine.Sanitize(td.ChartAlerts));
        tab.ParkedAlerts.Clear();
        if (td.ParkedChartAlerts == null) return;
        foreach (var (instrument, saved) in td.ParkedChartAlerts)
        {
            var alerts = ChartAlertEngine.Sanitize(saved);
            if (alerts.Count > 0) tab.ParkedAlerts[instrument] = alerts;
        }
    }

    /// <summary>
    /// The alerts a chart keeps when it opens an instrument again or another one. The same instrument keeps its alerts,
    /// their indicator keys moved to the copied indicators (<paramref name="copied"/>: old key → new key, null when that
    /// copy failed; keys not in it, such as an indicator that failed to load, stay as they are). Another instrument's
    /// alerts wait in <paramref name="parked"/> under <paramref name="oldInstrument"/>; the ones that waited for
    /// <paramref name="newInstrument"/> come back. Lost: alerts on an indicator that could not be copied (its key would
    /// now name another one).
    /// </summary>
    internal static (List<ChartAlert> Alerts, int Parked, int Resumed, int Lost) CarryChartAlerts(IReadOnlyList<ChartAlert>? old,
        IReadOnlyDictionary<string, string?> copied, bool instrumentChanged, string oldInstrument, string newInstrument,
        Dictionary<string, List<ChartAlert>> parked)
    {
        var alerts = new List<ChartAlert>();
        int parkedCount = 0, resumed = 0, lost = 0;
        if (old is { Count: > 0 })
        {
            var carried = new List<ChartAlert>();
            foreach (var a in old)
            {
                if (FollowCopiedIndicators(a, copied)) carried.Add(a);
                else lost++;
            }

            if (!instrumentChanged) alerts.AddRange(carried);
            else if (carried.Count > 0)
            {
                parked[oldInstrument] = carried;
                parkedCount = carried.Count;
            }
        }

        if (parked.Remove(newInstrument, out var waiting))
        {
            alerts.AddRange(waiting);
            resumed = waiting.Count;
        }

        return (ChartAlertEngine.Sanitize(alerts), parkedCount, resumed, lost);
    }

    private static bool FollowCopiedIndicators(ChartAlert alert, IReadOnlyDictionary<string, string?> copied)
    {
        if (alert.Operands().Any(o => o.Indicator != null && copied.TryGetValue(o.Indicator, out var to) && to == null)) return false;
        foreach (var o in alert.Operands())
            if (o.Indicator != null && copied.TryGetValue(o.Indicator, out var to)) o.Indicator = to;
        return true;
    }

    /// <summary>The workspace JSON of a chart's alerts (same serializer settings as the workspace file); a test seam.</summary>
    internal static string ChartAlertsJson(List<ChartAlert> alerts) => JsonConvert.SerializeObject(new TabDto { ChartAlerts = alerts }, Settings);

    /// <summary>Reads a chart tab's alerts from workspace JSON (an older tab without alerts gives none); a test seam.</summary>
    internal static List<ChartAlert> ChartAlertsFromJson(string tabJson) =>
        ChartAlertEngine.Sanitize(JsonConvert.DeserializeObject<TabDto>(tabJson, Settings)?.ChartAlerts);
}
