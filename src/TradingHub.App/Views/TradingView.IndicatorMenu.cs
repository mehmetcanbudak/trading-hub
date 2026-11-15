using Avalonia.Controls;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Common.Logging;
using TradingHub.Indicators;

namespace TradingHub.App.Views;

public sealed partial class TradingView
{
    /// <summary>Right-click actions contributed by the chart's indicators at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private List<Control> IndicatorMenuItems(ChartTab tab, float x, float y)
    {
        var items = new List<Control>();
        var model = tab.Model;
        if (model == null) return items;
        foreach (var slot in model.Indicators.ToList())
        {
            IReadOnlyList<IndicatorMenuItem> actions;
            try { actions = slot.Indicator.GetChartMenuItems((int)x, (int)y); }
            catch (Exception ex)
            {
                this.LogWarn($"Chart menu of {slot.Indicator.Name} failed: {ex.Message}");
                continue;
            }
            if (actions.Count == 0) continue;
            foreach (var action in actions)
            {
                var run = action.Run;
                items.Add(Item(action.Text, action.Checked ? MaterialIconKind.Check : MaterialIconKind.ChartBellCurve, () =>
                {
                    try { run(); }
                    catch (Exception ex) { _status(ex.Message); }
                    model.Invalidate();
                    Workspace.Save(this);
                }));
            }
            items.Add(new Separator());
        }
        return items;
    }
}
