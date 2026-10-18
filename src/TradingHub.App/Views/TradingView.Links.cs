using TradingHub.App.Services;
using TradingHub.Common.Logging;
using TradingHub.Data;

namespace TradingHub.App.Views;

/// <summary>Gráficos vinculados por color: instrumento, período y cruz del cursor compartidos.</summary>
public sealed partial class TradingView
{
    private bool _linkSync;

    /// <summary>Gráficos (pestañas y ventanas aparte) de un grupo de color (0 = ninguno).</summary>
    internal IEnumerable<ChartTab> ChartsInGroup(int group) =>
        group == 0 ? Enumerable.Empty<ChartTab>() : AllCharts.Where(c => c.LinkGroup == group);

    /// <summary>Cambia el grupo de un gráfico; al entrar toma el instrumento (y el período) del grupo.</summary>
    internal async void SetLinkGroup(ChartTab chart, int group)
    {
        try
        {
            if (chart.LinkGroup == group) return;
            var leader = ChartsInGroup(group).FirstOrDefault(c => !ReferenceEquals(c, chart) && c.Model != null);
            chart.LinkGroup = group;
            RefreshLinkButtons();
            if (group == 0)
            {
                _status("Chart unlinked");
                return;
            }

            string name = ChartLinks.NameOf(group).ToLowerInvariant();
            if (leader?.Model == null || chart.Model == null)
            {
                _status($"Group {name}: select the same color on another chart to synchronize");
                return;
            }

            var s = _svc.Settings;
            _linkSync = true;
            try
            {
                if (s.LinkSymbol && !SameInstrument(chart, leader))
                    await OpenInTabAsync(chart, leader.FeedId, leader.Symbol, chart.Model.Period, chart.Model.Settings.Clone());
                if (s.LinkPeriod && chart.Model != null && leader.Model != null && chart.Model.Period != leader.Model.Period)
                    ApplyPeriod(chart, leader.Model.Period);
            }
            finally
            {
                _linkSync = false;
            }

            var synced = Synced().ToList();
            string what = synced.Count switch
            {
                0 => "nothing (enable synchronization in the color menu)",
                1 => synced[0],
                _ => string.Join(", ", synced.Take(synced.Count - 1)) + " and " + synced[^1],
            };
            _status($"Linked to group {name}: {what} synchronized{(synced.Count > 1 ? "n" : "")} across charts");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private IEnumerable<string> Synced()
    {
        var s = _svc.Settings;
        if (s.LinkSymbol) yield return "instrument";
        if (s.LinkPeriod) yield return "period";
        if (s.LinkCrosshair) yield return "crosshair";
    }

    private static bool SameInstrument(ChartTab a, ChartTab b) =>
        a.FeedId == b.FeedId && a.Symbol.Equals(b.Symbol, StringComparison.OrdinalIgnoreCase);

    /// <summary>Un gráfico cambió de período desde su barra o un atajo: los de su grupo lo siguen.</summary>
    internal void OnPeriodChanged(ChartTab chart)
    {
        if (chart.Strategies.Count > 0) RestartStrategies(chart, instrumentChanged: false);
        if (_linkSync || chart.LinkGroup == 0 || !_svc.Settings.LinkPeriod || chart.Model == null) return;
        foreach (var other in ChartsInGroup(chart.LinkGroup).ToList())
        {
            if (!ReferenceEquals(other, chart) && other.Model != null && other.Model.Period != chart.Model.Period)
                ApplyPeriod(other, chart.Model.Period);
        }
    }

    private void ApplyPeriod(ChartTab chart, ChartPeriod period)
    {
        chart.Model!.SetPeriod(period);
        UpdateTitles(chart);
        if (ReferenceEquals(chart, _active)) _toolbar.Sync();
        chart.Window?.Toolbar.Sync();
    }

    /// <summary>Un gráfico cambió de instrumento: los de su grupo pasan al mismo (cada uno con su período).</summary>
    private async Task PropagateSymbolAsync(ChartTab source)
    {
        if (_linkSync || source.LinkGroup == 0 || !_svc.Settings.LinkSymbol) return;
        var targets = ChartsInGroup(source.LinkGroup).Where(c => !ReferenceEquals(c, source) && !SameInstrument(c, source)).ToList();
        if (targets.Count == 0) return;
        _linkSync = true;
        try
        {
            foreach (var t in targets)
                await OpenInTabAsync(t, source.FeedId, source.Symbol, t.Model?.Period ?? ChartPeriod.M5, t.Model?.Settings.Clone());
        }
        finally
        {
            _linkSync = false;
        }
    }

    /// <summary>La cruz del cursor de un gráfico se muestra en los demás de su grupo (el precio, si es el mismo instrumento).</summary>
    private void OnCrosshair(ChartTab source, DateTime? time, double? price)
    {
        if (source.LinkGroup == 0) return;
        bool on = _svc.Settings.LinkCrosshair;
        foreach (var other in ChartsInGroup(source.LinkGroup))
        {
            if (ReferenceEquals(other, source)) continue;
            other.View.SetLinkedCrosshair(on ? time : null, on && SameInstrument(other, source) ? price : null);
        }
    }

    internal void RefreshLinkButtons()
    {
        foreach (var c in AllCharts) c.LinkButton?.Refresh();
        _toolbar.RefreshLink();
        foreach (var w in _floating) w.Toolbar.RefreshLink();
    }
}
