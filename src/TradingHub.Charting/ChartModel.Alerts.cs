using TradingHub.Charting.Drawings;

namespace TradingHub.Charting;

public sealed partial class IndicatorSlot
{
    /// <summary>What the indicator drew on the price panel in the latest frame (reused buffers, bounded per frame).</summary>
    internal DrawnLineCapture LineCapture { get; } = new();

    /// <summary>Lines the indicator drew on the price chart in the latest frame, in time and price (for alerts).</summary>
    public IReadOnlyList<DrawnLine> DrawnLines => LineCapture.Lines;
}

public sealed partial class ChartModel
{
    /// <summary>Alerts on this chart's price, indicator lines and drawings (persisted with the chart).</summary>
    public List<ChartAlert> Alerts { get; } = new();

    /// <summary>Indicator line picked with a right-click: drawn highlighted like a selected drawing.</summary>
    public ChartAlertOperand? HighlightedLine { get; set; }

    private readonly Dictionary<Guid, TradeDrawingCalc> _alertVwaps = new();

    /// <summary>Anchored-VWAP calculator owned by alert evaluation (independent of the one that draws).</summary>
    internal TradeDrawingCalc AlertVwapCalculator(ChartDrawing drawing)
    {
        if (!_alertVwaps.TryGetValue(drawing.Id, out var calc))
        {
            if (_alertVwaps.Count > 64) _alertVwaps.Clear();
            _alertVwaps[drawing.Id] = calc = new TradeDrawingCalc();
        }

        return calc;
    }

    /// <summary>
    /// Removing an indicator removes its alerts; alerts on later indicators of the same type keep pointing at them (their
    /// "type#n" key shifts down by one).
    /// </summary>
    private void RemoveAlertsOf(IndicatorSlot slot)
    {
        if (HighlightedLine?.Indicator is { } hk && ChartAlertEngine.SlotOf(this, hk) == slot) HighlightedLine = null;
        if (Alerts.Count == 0) return;
        string key = ChartAlertEngine.KeyOf(this, slot);
        string type = slot.Indicator.GetType().FullName!;
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        bool after = false;
        foreach (var s in Indicators)
        {
            if (ReferenceEquals(s, slot)) { after = true; continue; }
            if (!after || s.Indicator.GetType().FullName != type) continue;
            string old = ChartAlertEngine.KeyOf(this, s);
            int hash = old.LastIndexOf('#');
            remap[old] = type + "#" + (int.Parse(old.AsSpan(hash + 1), provider: System.Globalization.CultureInfo.InvariantCulture) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Alerts.RemoveAll(a => a.Operands().Any(o => o.Indicator == key));
        foreach (var a in Alerts)
            foreach (var o in a.Operands())
                if (o.Indicator != null && remap.TryGetValue(o.Indicator, out var to)) o.Indicator = to;
    }
}
