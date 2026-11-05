using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;

namespace TradingHub.App.Views;

public sealed partial class TradingView
{
    private readonly StackPanel _alertToasts = new() { Name = "AlertToasts", Spacing = 6, MaxWidth = 440,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16) };
    private readonly DispatcherTimer _alertToastTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _alertUiDisposed;
    private readonly object _alertDispatchGate = new();
    private readonly Queue<AlertEvent> _pendingToasts = new();
    private bool _alertDispatchQueued;
    private long _lastAlertSoundAt = long.MinValue;

    private void InitializeAlertUi()
    {
        if (Content is Control existing)
        {
            Content = null;
            Content = new Grid { Children = { existing, _alertToasts } };
        }
        _svc.Alerts.Raised += OnUnifiedAlert;
        _alertToastTimer.Tick += (_, _) => { _alertToasts.Children.Clear(); _alertToastTimer.Stop(); };
    }
    private void DisposeAlertUi()
    {
        lock (_alertDispatchGate) { _alertUiDisposed = true; _pendingToasts.Clear(); }
        _svc.Alerts.Raised -= OnUnifiedAlert;
        _alertToastTimer.Stop();
    }
    private void OnUnifiedAlert(AlertEvent alert)
    {
        lock (_alertDispatchGate)
        {
            if (_alertUiDisposed) return;
            _pendingToasts.Enqueue(alert);
            while (_pendingToasts.Count > 3) _pendingToasts.Dequeue();
            if (_alertDispatchQueued) return;
            _alertDispatchQueued = true;
        }
        Dispatcher.UIThread.Post(() =>
        {
            AlertEvent[] batch;
            lock (_alertDispatchGate)
            {
                _alertDispatchQueued = false;
                if (_alertUiDisposed) return;
                batch = _pendingToasts.ToArray();
                _pendingToasts.Clear();
            }
            foreach (var item in batch) ShowUnifiedToast(item);
            long now = Environment.TickCount64;
            if (batch.Length > 0 && _svc.Settings.SoundAlerts && (_lastAlertSoundAt == long.MinValue || now - _lastAlertSoundAt >= 2000))
            {
                _lastAlertSoundAt = now;
                Sound.Play();
            }
        });
    }
    private void ShowUnifiedToast(AlertEvent alert)
    {
        _status($"{alert.Source}: {alert.Message}");
        var history = new Button { Content = "History / delivery status", Padding = new Thickness(6, 2) };
        history.Click += (_, _) => OpenAlerts();
        var dismiss = new Button { Content = "Dismiss", Padding = new Thickness(6, 2) };
        var toast = new Border { Name = "UnifiedAlertToast", Background = Ui.Bg1, BorderBrush = Ui.Accent,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12),
            Child = new StackPanel { Spacing = 6, Children = {
                new TextBlock { Text = $"{alert.Kind} · {alert.Instrument} · {alert.Source}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = alert.Message, TextWrapping = TextWrapping.Wrap }, Ui.H(8, history, dismiss) } } };
        dismiss.Click += (_, _) => _alertToasts.Children.Remove(toast);
        _alertToasts.Children.Add(toast);
        while (_alertToasts.Children.Count > 3) _alertToasts.Children.RemoveAt(0);
        _alertToastTimer.Stop();
        _alertToastTimer.Start();
    }

    private void PublishChartAlert(AlertInfo alert) => _svc.Alerts.Raise(
        alert.Source == "Price alert" ? AlertKind.Price : AlertKind.Indicator, alert.Instrument, alert.Source,
        alert.Message, timeUtc: alert.Time.Kind == DateTimeKind.Utc ? alert.Time : alert.Time.ToUniversalTime());

    private void PublishChartAlert(ChartTab chart, AlertInfo alert)
    {
        bool blind = chart.Model?.Settings.BlindReplay == true;
        var run = chart.Model == null ? null : _svc.ReplaySessions.TryGet(chart.Model.Data);
        _svc.Alerts.Raise(alert.Source == "Price alert" ? AlertKind.Price : AlertKind.Indicator,
            blind ? "Practice" : alert.Instrument, alert.Source,
            blind ? "Practice indicator alert. Reveal the run to inspect its chart." : alert.Message,
            timeUtc: run?.ClockUtc ?? (alert.Time.Kind == DateTimeKind.Utc ? alert.Time : alert.Time.ToUniversalTime()), allowExternal: !chart.IsReplay);
    }

    internal void OpenAlerts()
    {
        if (TopLevel.GetTopLevel(this) is Window owner) new AlertsWindow(_svc).Show(owner);
    }
}
