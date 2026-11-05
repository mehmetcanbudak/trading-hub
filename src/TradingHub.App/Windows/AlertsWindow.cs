using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradingHub.App.Services;

namespace TradingHub.App.Windows;

/// <summary>Private authorization/token values are never read back. Full webhook targets require an explicit in-memory review.</summary>
public sealed class AlertsWindow : Window
{
    private readonly AlertCenter _center;
    private readonly ComboBox _saved = new() { Name = "AlertDestinations", MinWidth = 260 };
    private readonly ComboBox _kind = new() { Name = "AlertDestinationKind", ItemsSource = Enum.GetValues<NotificationDestinationKind>(), SelectedIndex = 0 };
    private readonly TextBox _endpoint = new() { Name = "AlertWebhookEndpoint", Watermark = "https://example.invalid/alerts" };
    private readonly TextBox _target = new() { Name = "AlertTelegramChat", Watermark = "Telegram chat ID or @channel" };
    private readonly TextBox _secret = new() { Name = "AlertSecret", PasswordChar = '●', Watermark = "New Authorization value / bot token (secret store only); blank keeps existing" };
    private readonly CheckBox _enabled = new() { Name = "AlertExternalConsent", Content = "I approve sending selected alert content to this external destination", IsChecked = false };
    private readonly Dictionary<AlertKind, CheckBox> _kinds = new();
    private readonly TextBox _preview = new() { Name = "AlertPayloadPreview", IsReadOnly = true, AcceptsReturn = true, Height = 140, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Name = "AlertDeliveryStatus", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _history = new() { Name = "AlertRecentHistory", Spacing = 6 };
    private readonly StackPanel _deliveries = new() { Name = "AlertDeliveryHistory", Spacing = 6 };
    private bool _closed, _syncing;
    private string? _selectedId;
    public Task? PendingOperation { get; private set; }
    private int _refreshQueued;

    public AlertsWindow(AppServices services) : this(services.Alerts) { }
    public AlertsWindow(AlertCenter center)
    {
        _center = center;
        Title = "Alerts — local history and external delivery";
        Width = 980;
        Height = 840;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var kinds = new WrapPanel { ItemSpacing = 10, LineSpacing = 6 };
        foreach (var kind in Enum.GetValues<AlertKind>())
        {
            var checkbox = new CheckBox { Name = "AlertKind_" + kind, Content = kind.ToString(), IsChecked = true };
            _kinds.Add(kind, checkbox);
            kinds.Children.Add(checkbox);
        }
        var save = new Button { Name = "AlertSaveDestination", Content = "Save destination and permission" };
        var revoke = new Button { Name = "AlertRevokeDestination", Content = "Turn off / revoke immediately" };
        var preview = new Button { Name = "AlertPreviewPayload", Content = "Preview latest alert payload (no send)" };
        var test = new Button { Name = "AlertSendTest", Content = "Queue explicit test alert" };
        var fresh = new Button { Name = "AlertNewDestination", Content = "New destination" };
        var reviewTarget = new Button { Name = "AlertReviewPrivateTarget", Content = "Review private target (this window only)" };
        var hideTarget = new Button { Name = "AlertHidePrivateTarget", Content = "Hide private target" };
        reviewTarget.Click += (_, _) => PendingOperation = ExecuteAsync(() => { ReviewPrivateTarget(); return Task.CompletedTask; });
        hideTarget.Click += (_, _) => HidePrivateTarget();
        save.Click += (_, _) => PendingOperation = ExecuteAsync(SaveAsync);
        revoke.Click += (_, _) => PendingOperation = ExecuteAsync(RevokeAsync);
        preview.Click += (_, _) => ShowPreview();
        fresh.Click += (_, _) => Select(null);
        test.Click += (_, _) =>
        {
            _center.Raise(AlertKind.System, "TEST", "Notification test", "Explicit test alert from Trading Hub.");
            _status.Text = "Test queued locally. Only enabled destinations that include System can receive it.";
        };
        _saved.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            if (_saved.SelectedItem is ComboBoxItem { Tag: string id }) Select(id);
        };
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel {
            Margin = new Thickness(20), Spacing = 10, Children = {
                new TextBlock { Text = "Unified alerts", FontSize = 22, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Price, indicator, strategy, risk and execution alerts stay local unless you opt in. Practice alerts never leave this app. Authorization, tokens, full webhook targets and encryption keys use the private credential store. Ordinary history contains generic metadata; external pending bodies are encrypted. Current-session local content remains visible, and retained outbox content can be restored only with its original private permission.", TextWrapping = TextWrapping.Wrap },
                _status, _saved, fresh, _kind,
                new TextBlock { Text = "Webhook HTTPS URI (full path and query stored privately; blank keeps a stored target until revoked)" }, _endpoint,
                new TextBlock { Text = "Telegram chat ID / channel (bot token belongs in the secret field)" }, _target,
                _secret, kinds, _enabled,
                new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { preview, reviewTarget, hideTarget, save, revoke, test } }, _preview,
                new TextBlock { Text = "Pending delivery is persisted before a send. Up to 500 pending deliveries; five automatic attempts with bounded backoff. Retry keeps the same delivery ID. Deduplication retains the most recent 4,096 event IDs; older repeats may deliver again. HTTP acceptance is not proof of human receipt. Revocation/cancellation cannot recall a request already accepted remotely.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Delivery status / retry / cancel", FontSize = 17, FontWeight = FontWeight.SemiBold }, _deliveries,
                new TextBlock { Text = "Recent local alert log (latest 300)", FontSize = 17, FontWeight = FontWeight.SemiBold }, _history
            } } };
        _center.Changed += RefreshPosted;
        Closed += (_, _) => { _closed = true; _center.Changed -= RefreshPosted; _secret.Text = _endpoint.Text = _preview.Text = ""; };
        Refresh();
    }

    private void RefreshPosted()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_closed) Refresh();
        });
    }
    private void Refresh()
    {
        var snapshot = _center.Snapshot();
        _status.Text = snapshot.Status + (snapshot.DroppedLocalAlerts > 0 ? $" · {snapshot.DroppedLocalAlerts} intake alerts dropped at capacity/shutdown" : "");
        _syncing = true;
        _saved.ItemsSource = snapshot.Destinations.Select(d => new ComboBoxItem { Content = $"{d.Kind} · {(d.Enabled ? "enabled" : "off")} · {(d.Kind == NotificationDestinationKind.Telegram ? d.Target : d.Endpoint)} · {d.Id[..Math.Min(8, d.Id.Length)]}", Tag = d.Id }).ToArray();
        _saved.SelectedIndex = snapshot.Destinations.ToList().FindIndex(d => d.Id == _selectedId);
        _syncing = false;
        _history.Children.Clear();
        foreach (var alert in snapshot.Recent.Reverse())
            _history.Children.Add(new TextBlock { Text = $"{(alert.AllowExternal ? alert.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "Practice time (local only)")} · {alert.Kind} · {alert.Instrument} · {alert.Source}: {alert.Message}\nID: {alert.Id}", TextWrapping = TextWrapping.Wrap });
        _deliveries.Children.Clear();
        foreach (var delivery in snapshot.Deliveries.Reverse().Take(100))
        {
            var retry = new Button { Content = "Retry", IsEnabled = delivery.EncryptedAlert != null && delivery.State is NotificationDeliveryState.Pending or NotificationDeliveryState.Failed };
            var cancel = new Button { Content = "Cancel", IsEnabled = delivery.State is NotificationDeliveryState.Pending or NotificationDeliveryState.Delivering or NotificationDeliveryState.AwaitingPersistence };
            retry.Click += (_, _) => _center.Retry(delivery.Id);
            cancel.Click += (_, _) => _center.CancelDelivery(delivery.Id);
            _deliveries.Children.Add(new StackPanel { Spacing = 4, Children = {
                new TextBlock { Text = $"{delivery.Alert.Source} · {delivery.State} · attempt {delivery.Attempts}/5\n{delivery.Status}\nDelivery ID: {delivery.Id}", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { retry, cancel } } } });
        }
    }
    private void Select(string? id)
    {
        _selectedId = id;
        var destination = _center.Snapshot().Destinations.FirstOrDefault(d => d.Id == id);
        _kind.SelectedItem = destination?.Kind ?? NotificationDestinationKind.Webhook;
        _endpoint.Text = "";
        _target.Text = destination?.Target ?? "";
        _secret.Text = "";
        // Every edited/saved grant needs an explicit fresh permission action; opening a row never re-enables it.
        _enabled.IsChecked = false;
        foreach (var pair in _kinds) pair.Value.IsChecked = destination?.Kinds.Contains(pair.Key) ?? true;
        _preview.Text = "";
    }
    public async Task SaveAsync()
    {
        try
        {
            var selectedKinds = _kinds.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray();
            var destination = await _center.ConfigureAsync(_selectedId, (NotificationDestinationKind)(_kind.SelectedItem ?? NotificationDestinationKind.Webhook),
                _endpoint.Text?.Trim() ?? "", _target.Text?.Trim() ?? "", string.IsNullOrEmpty(_secret.Text) ? null : _secret.Text,
                _enabled.IsChecked == true, selectedKinds);
            _selectedId = destination.Id;
            Refresh();
        }
        finally { _secret.Text = _endpoint.Text = ""; _enabled.IsChecked = false; }
    }
    public async Task RevokeAsync()
    {
        try
        {
            if (_selectedId != null) await _center.RevokeAsync(_selectedId);
            Refresh();
        }
        finally { _secret.Text = _endpoint.Text = ""; _enabled.IsChecked = false; }
    }
    public void ReviewPrivateTarget()
    {
        if (_selectedId == null) throw new InvalidOperationException("Select a saved webhook destination to review.");
        _endpoint.Text = _center.ReviewPrivateTarget(_selectedId);
        _status.Text = "Private target is visible only in this window. Hide it before taking screenshots.";
    }
    public void HidePrivateTarget()
    {
        _endpoint.Text = "";
        _status.Text = "Private target hidden. A blank target retains it while stored; after revocation re-enter it before enabling.";
    }
    public void ShowPreview()
    {
        var kind = (NotificationDestinationKind)(_kind.SelectedItem ?? NotificationDestinationKind.Webhook);
        var saved = _center.Snapshot().Destinations.FirstOrDefault(d => d.Id == _selectedId);
        var destination = new NotificationDestination(_selectedId ?? "preview", kind,
            string.IsNullOrWhiteSpace(_endpoint.Text) ? saved?.Endpoint ?? "" : _endpoint.Text, _target.Text ?? "", false, "preview", _kinds.Keys.ToArray());
        var alert = _center.Snapshot().Recent.LastOrDefault() ?? new AlertEvent("preview", DateTime.UtcNow, AlertKind.System, "TEST", "Payload preview", "Preview only; nothing is sent.");
        var preview = _center.Preview(destination, alert);
        _preview.Text = preview.Destination + "\nAuthorization: " + preview.Authorization + "\n" + preview.Payload + "\n" + preview.DeliveryNotice;
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (ArgumentException error) { _status.Text = error.Message; }
        catch (InvalidOperationException error) { _status.Text = error.Message; }
        catch { _status.Text = "Notification operation failed. Credential and network details are not displayed."; }
    }
}
