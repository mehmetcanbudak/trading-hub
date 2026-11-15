using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;

namespace TradingHub.App.Views;

/// <summary>Chart alerts: evaluation after each update, right-click line capture and the alert dialog.</summary>
public sealed partial class TradingView
{
    /// <summary>What the last evaluation of a chart saw: alerts are only re-checked when a bar price or the alert list changes.</summary>
    private sealed class ChartAlertWatch
    {
        public int Count;
        public long Time, Close, High, Low;
        public int Alerts = -1;
        public int Drawings = -1;
    }

    private readonly ConditionalWeakTable<ChartModel, ChartAlertWatch> _chartAlertWatch = new();
    private bool _chartAlertDialogOpen;

    /// <summary>Evaluates a chart's alerts and publishes the ones that fire (never for history or a still-loading chart).</summary>
    private void CheckChartAlerts(ChartTab t)
    {
        var m = t.Model;
        if (m == null || m.Alerts.Count == 0) return;
        var watch = _chartAlertWatch.GetOrCreateValue(m);
        bool changed = watch.Alerts != m.Alerts.Count || watch.Drawings != m.Drawings.Count;
        if (m.Series.Count > 0)
        {
            var c = m.Series[^1];
            changed |= watch.Count != m.Series.Count || watch.Time != c.Time.Ticks || watch.Close != c.CloseTicks || watch.High != c.HighTicks || watch.Low != c.LowTicks;
            (watch.Count, watch.Time, watch.Close, watch.High, watch.Low) = (m.Series.Count, c.Time.Ticks, c.CloseTicks, c.HighTicks, c.LowTicks);
        }

        if (!changed) return;
        bool save = ChartAlertEngine.Prune(m).Count > 0;
        watch.Alerts = m.Alerts.Count;
        watch.Drawings = m.Drawings.Count;
        foreach (var fire in ChartAlertEngine.Evaluate(m, DateTime.UtcNow))
        {
            var a = fire.Alert;
            bool indicator = a.Operands().Any(o => o.Source is ChartAlertSource.Series or ChartAlertSource.Level or ChartAlertSource.DrawnLine);
            string message = $"{t.DisplaySymbol}: {a.Describe()}" + (a.Message.Length > 0 ? " — " + a.Message : "");
            _svc.Alerts.Raise(indicator ? AlertKind.Indicator : AlertKind.Price, t.DisplaySymbol, "Chart alert", message,
                id: fire.EventId, timeUtc: DateTime.UtcNow, allowExternal: !t.IsReplay);
            save |= a.Trigger == ChartAlertTrigger.Once;
        }

        if (save && !t.IsReplay) Workspace.Save(this);
    }

    /// <summary>
    /// Right-click entries for alerts: on a selected drawing with alertable lines, or on any indicator line under the pointer
    /// (series, declared level or any captured line), which is highlighted like a selected drawing while the menu is open.
    /// </summary>
    private List<Control> ChartAlertMenuItems(ChartTab tab, ChartDrawing? selected, float x, float y)
    {
        var items = new List<Control>();
        var model = tab.Model;
        if (model == null) return items;
        if (selected != null && ChartAlertEngine.DrawingLines(model, selected) is { Count: > 0 } parts)
        {
            bool channel = selected.IsChannel && parts.Count >= 2;
            var upper = parts[0];
            var lower = channel ? parts[^1] : null;
            var add = Item("Add alert on drawing…", MaterialIconKind.BellPlusOutline, () =>
                _ = OpenChartAlertsAsync(tab, upper, lower, channel ? ChartAlertCondition.EnteringChannel : ChartAlertCondition.Crossing));
            add.Name = "ChartAlertAddDrawing";
            items.Add(add);
        }
        else if (ChartAlertEngine.LineAt(model, x, y) is { } line)
        {
            model.HighlightedLine = line;
            model.Invalidate();
            var add = Item($"Add alert on {line.DisplayLabel()}…", MaterialIconKind.BellPlusOutline, () => _ = OpenChartAlertsAsync(tab, line));
            add.Name = "ChartAlertAddLine";
            items.Add(add);
        }

        int count = model.Alerts.Count;
        var manage = Item(count > 0 ? $"Chart alerts ({count})…" : "Chart alerts…", MaterialIconKind.BellOutline, () => _ = OpenChartAlertsAsync(tab));
        manage.Name = "ChartAlertManage";
        items.Add(manage);
        items.Add(new Separator());
        return items;
    }

    /// <summary>Clears the right-click highlight when the menu closes without opening the alert dialog.</summary>
    private void ChartAlertMenuClosed(ChartTab tab) => Dispatcher.UIThread.Post(() =>
    {
        if (_chartAlertDialogOpen || tab.Model is not { HighlightedLine: not null } m) return;
        m.HighlightedLine = null;
        m.Invalidate();
    });

    internal async Task OpenChartAlertsAsync(ChartTab tab, ChartAlertOperand? right = null, ChartAlertOperand? lower = null, ChartAlertCondition? condition = null)
    {
        var model = tab.Model;
        if (model == null || _chartAlertDialogOpen) return;
        _chartAlertDialogOpen = true;
        if (right is { Source: not ChartAlertSource.Drawing and not ChartAlertSource.Value }) model.HighlightedLine = right;
        model.Invalidate();
        try
        {
            if (await ChartAlertDialog.ShowAsync(TopLevel.GetTopLevel(tab.View) as Window, model, right, lower, condition) && !tab.IsReplay)
                Workspace.Save(this);
        }
        finally
        {
            _chartAlertDialogOpen = false;
            model.HighlightedLine = null;
            model.Invalidate();
        }
    }
}
