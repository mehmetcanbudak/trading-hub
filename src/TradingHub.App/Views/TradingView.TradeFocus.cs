using System.Globalization;
using Avalonia.Controls;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

public sealed partial class TradingView
{
    /// <summary>
    /// Muestra una operación en un gráfico del instrumento: usa el indicado, o uno abierto del mismo instrumento (mejor si
    /// tiene el mismo período), o abre una pestaña nueva. Carga la historia que haga falta, la resalta y la centra.
    /// Devuelve null si salió bien, o el motivo.
    /// </summary>
    internal async Task<string?> ShowTradeAsync(string profileId, string symbol, ChartPeriod period, ChartTab? prefer, ClosedTrade? trade, string strategyName)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow main) main.Navigate("trading");
        var chart = prefer is { Model: not null } ? prefer : null;
        chart ??= AllCharts.Where(c => c.Model != null && c.FeedId == profileId && c.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Model!.Period.Code == period.Code).FirstOrDefault();
        if (chart == null)
        {
            if (_svc.Connections.Find(profileId) == null) return $"Connection {profileId} does not exist; check Connections.";
            chart = await NewTabAsync(profileId, symbol, PeriodFor(period, trade), _active?.Model?.Settings.Clone());
        }

        if (chart.Window is { } w) w.Activate();
        else Activate(chart);
        if (chart.Model is not { } model) return $"Could not open {symbol}.";
        if (trade == null) return null;

        // historia hasta la entrada (con un poco de aire antes)
        for (int i = 0; i < 240 && model.Data.IsLoadingHistory; i++) await Task.Delay(250);
        var data = model.Data;
        if (data.Trades.Count == 0 || data.Trades.FirstTime > trade.EntryTime.AddMinutes(-30))
        {
            double hours = Math.Max(1, (data.Feed.Now - trade.EntryTime).TotalHours + 2);
            _status($"Loading history for {symbol} up to the trade…");
            await LoadHistoryAsync(chart, hours, quiet: true);
        }

        if (!ReferenceEquals(chart.Model, model)) return null;
        var inv = CultureInfo.InvariantCulture;
        string pnl = trade.NetPnl.ToString("+#,##0.00;-#,##0.00;0.00", inv);
        string when = TimeZoneInfo.ConvertTimeFromUtc(trade.EntryTime, model.Settings.TimeZone).ToString("dd/MM HH:mm:ss", inv) + " – " +
                      TimeZoneInfo.ConvertTimeFromUtc(trade.ExitTime, model.Settings.TimeZone).ToString("HH:mm:ss", inv);
        model.FocusTrade = new TradeFocus(trade.EntryTime, trade.EntryPrice, trade.ExitTime, trade.ExitPrice, trade.IsLong, trade.Quantity, trade.NetPnl,
            $"{strategyName} · #{trade.Number} {(trade.IsLong ? "Long" : "Short")} {trade.Quantity.ToString("0.####", inv)} · {pnl} · {when}");
        // la vela se arma con lo que ya está cargado: esperar un cuadro para tener el ancho real del gráfico
        model.Invalidate();
        await Task.Delay(80);
        model.CenterOn(trade.EntryTime, trade.ExitTime);
        if (model.Series.Count == 0 || model.Series[0].Time > trade.EntryTime)
            return $"The chart has no data for this time ({trade.EntryTime.ToLocalTime():dd/MM HH:mm}): download those days in Data.";
        return null;
    }

    /// <summary>Para un gráfico nuevo: velas más finas si la operación duró poco (que se vea en varias velas).</summary>
    private static ChartPeriod PeriodFor(ChartPeriod preferred, ClosedTrade? trade)
    {
        if (trade == null || !preferred.IsTimeBased) return preferred;
        double seconds = (trade.ExitTime - trade.EntryTime).TotalSeconds;
        if (seconds >= preferred.ApproxDuration.TotalSeconds * 4) return preferred;
        foreach (var code in new[] { "M1", "S30", "S15", "S5" })
            if (ChartPeriod.TryParse(code, out var p) && p.ApproxDuration < preferred.ApproxDuration && seconds >= p.ApproxDuration.TotalSeconds * 6)
                return p;
        return ChartPeriod.TryParse("S5", out var fine) ? fine : preferred;
    }

    /// <summary>Saca el resaltado de una operación (botón del gráfico o Escape).</summary>
    internal void ClearFocusTrade(ChartTab? chart)
    {
        if (chart?.Model is not { FocusTrade: not null } m) return;
        m.FocusTrade = null;
        m.Invalidate();
    }
}
