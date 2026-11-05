using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Networking;

namespace TradingHub.App.Views;

internal sealed partial class SettingsView
{
    private readonly TextBlock _candleState = new() { Name = "CandleMaintenanceStatus", TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } };
    private ListBox? _candleInstruments;
    private IReadOnlyDictionary<string, string> _candleStatuses = new Dictionary<string, string>();

    private Control BuildHistorySettings()
    {
        var settings = _svc.Settings;
        var compact = Switch(settings.UseCompactHistory, value =>
        {
            settings.UseCompactHistory = value;
            _svc.ApplyOperationsHistorySettings();
        });
        compact.Name = "UseCompactHistory";
        var resolution = new ComboBox { Name = "CompactHistoryMinutes", MinWidth = 130, ItemsSource = new[] { 1, 5, 15, 60 }, SelectedItem = settings.CompactHistoryMinutes };
        resolution.SelectionChanged += (_, _) =>
        {
            if (resolution.SelectedItem is not int minutes) return;
            settings.CompactHistoryMinutes = minutes;
            SaveHistory();
        };
        var offline = Switch(settings.OfflineCatalogOnly, value =>
        {
            settings.OfflineCatalogOnly = value;
            InstrumentPicker.ClearCache();
        });
        offline.Name = "OfflineCatalogOnly";
        var depth = Switch(settings.RecordDepth, value =>
        {
            settings.RecordDepth = value;
            _svc.ApplyOperationsHistorySettings();
        });
        depth.Name = "RecordDepth";
        return Section("History and depth", MaterialIconKind.DatabaseOutline,
            Setting("Recent raw history (hours)", "Keep recent individual trades distinct from compact aggregates and estimated candle history. One hour to 90 days; bounded by the raw trade limit.",
                HistoryNumber("RawHistoryHours", (decimal)settings.RawHistoryHours, 1, 2160, v => settings.RawHistoryHours = (double)v)),
            Setting("Raw trade memory limit", "Maximum initial raw trades retained per instrument. Original history files are never replaced with aggregates.",
                HistoryNumber("MaxDiskTrades", settings.MaxDiskTrades, 10_000, 20_000_000, v => settings.MaxDiskTrades = (int)v, 100_000)),
            Setting("Compact display history", "Use explicitly labelled measured aggregates for older display history. Aggregate buckets are not individual large trades or event-complete footprints.", compact),
            Setting("Compact bucket (minutes)", "1, 5, 15 or 60 minute measured display buckets; original raw history remains available.", resolution),
            Setting("Compact memory limit", "Bound the initial compact result independently of the raw-trade window.",
                HistoryNumber("MaxCompactTrades", settings.MaxCompactTrades, 10_000, 2_000_000, v => settings.MaxCompactTrades = (int)v, 50_000)),
            Setting("Use saved instrument catalogs only", "No instrument-discovery or ticker-statistics requests. Rows retain provider identity and a visible saved-time/stale warning. Selecting a symbol never changes the trading account.", offline),
            Setting("Record received depth", "Explicit opt-in: received L2 changes and bounded snapshots, not event-complete or MBO history. Heatmap visibility does not enable recording. Gaps, staleness and write failures remain visible on the chart.", depth),
            Setting("Depth retention (days)", "Per-instrument retention. Saved historical selected-day reads remain available while recording is disabled.",
                HistoryNumber("DepthRetentionDays", settings.DepthRetentionDays, 1, 365, v => settings.DepthRetentionDays = (int)v)),
            Setting("Depth disk limit (MiB)", "Bounded disk retention per instrument; storage errors never become a fresh or empty book.",
                HistoryNumber("DepthMaxDiskMegabytes", settings.DepthMaxDiskMegabytes, 1, 4096, v => settings.DepthMaxDiskMegabytes = (int)v, 16)));
    }
    private NumericUpDown HistoryNumber(string name, decimal value, decimal min, decimal max, Action<decimal> set, decimal step = 1)
    {
        var number = new NumericUpDown { Name = name, Value = Math.Clamp(value, min, max), Minimum = min, Maximum = max, Increment = step, FormatString = "0", MinWidth = 140 };
        number.ValueChanged += (_, _) => { set(number.Value ?? min); SaveHistory(); };
        return number;
    }
    private void SaveHistory()
    {
        _svc.Settings.Save();
        _svc.ApplyOperationsHistorySettings();
    }

    private Control BuildProxySettings()
    {
        var proxy = _svc.NetworkProxy;
        var policy = proxy.Policy;
        var mode = new ComboBox { Name = "NetworkProxyMode", MinWidth = 180 };
        foreach (var item in new[] { (NetworkProxyMode.System, "Use system proxy"), (NetworkProxyMode.Direct, "Direct connection"), (NetworkProxyMode.Custom, "Custom proxy") })
            mode.Items.Add(new ComboBoxItem { Content = item.Item2, Tag = item.Item1 });
        mode.SelectedIndex = (int)policy.Mode;
        var address = new TextBox { Name = "NetworkProxyAddress", Text = policy.Address, Watermark = "http://127.0.0.1:8080 or socks5://127.0.0.1:1080" };
        var user = new TextBox { Name = "NetworkProxyUsername", Watermark = "Private proxy username (optional)" };
        var password = new TextBox { Name = "NetworkProxyPassword", Watermark = "Private proxy password (optional)", PasswordChar = '●' };
        var replace = new CheckBox { Name = "NetworkProxyReplaceCredentials", Content = "Replace private proxy credentials" };
        var status = new TextBlock { Name = "NetworkProxyStatus", TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } };
        var apply = Ui.Button("Save proxy policy", MaterialIconKind.ContentSaveOutline, () =>
        {
            try
            {
                if (mode.SelectedItem is not ComboBoxItem { Tag: NetworkProxyMode selected }) return;
                proxy.Save(new NetworkProxyPolicy { Mode = selected, Address = address.Text?.Trim() ?? "" }, user.Text, password.Text, replace.IsChecked == true);
                user.Text = password.Text = "";
                replace.IsChecked = false;
                status.Text = "Proxy policy saved. Existing transports are unchanged; restart safely to apply to new integrated provider transports. No public proxy test was sent.";
            }
            catch (Exception ex) { status.Text = "Proxy policy was not applied: " + ex.Message; }
        });
        apply.Name = "NetworkProxySave";
        var clear = Ui.Button("Delete private credentials", MaterialIconKind.DeleteOutline, () =>
        {
            try { proxy.ClearCredentials(); user.Text = password.Text = ""; status.Text = "Private proxy credentials deleted. Restart to replace existing transports."; }
            catch (Exception ex) { status.Text = "Private proxy credentials could not be deleted: " + ex.Message; }
        });
        clear.Name = "NetworkProxyClearCredentials";
        void Enabled()
        {
            bool custom = mode.SelectedItem is ComboBoxItem { Tag: NetworkProxyMode.Custom };
            address.IsEnabled = replace.IsEnabled = custom;
            user.IsEnabled = password.IsEnabled = custom && replace.IsChecked == true;
        }
        mode.SelectionChanged += (_, _) => Enabled();
        replace.IsCheckedChanged += (_, _) => Enabled();
        Enabled();
        return Section("Network proxy", MaterialIconKind.ServerNetwork,
            Ui.V(8, Ui.Muted("Explicit routing for app-owned HTTP and WebSocket transports, including market data, downloads, accounts, OAuth and notifications. System preserves platform defaults; Direct bypasses proxies. Native IB Gateway/TWS TCP and external assistant CLI networking use their own connection policy. Existing transports keep their captured policy until safely replaced or restarted; no process-wide network default is changed."),
                mode, address, replace, user, password, Ui.H(8, apply, clear, Ui.Button("Restart safely", MaterialIconKind.Restart, Localization.Lang.Restart)), status));
    }

    private Control BuildCandleMaintenance()
    {
        var keeper = _svc.KlineKeeper;
        var settings = keeper.Settings;
        var enabled = new ToggleSwitch { Name = "CandleMaintenanceEnabled", IsChecked = settings.Enabled, OnContent = "", OffContent = "" };
        enabled.IsCheckedChanged += async (_, _) =>
        {
            enabled.IsEnabled = false;
            await ConfigureCandleAsync(s => s.Enabled = enabled.IsChecked == true);
            enabled.IsEnabled = true;
        };
        var auto = new ToggleSwitch { Name = "CandleMaintenanceAutoTrack", IsChecked = settings.AutoTrackOpened, OnContent = "", OffContent = "" };
        auto.IsCheckedChanged += async (_, _) => await ConfigureCandleAsync(s => s.AutoTrackOpened = auto.IsChecked == true);
        var years = new NumericUpDown { Name = "CandleMaintenanceYears", Minimum = 1, Maximum = 10, Value = settings.LookbackYears, FormatString = "0", Width = 120 };
        years.ValueChanged += async (_, _) => await ConfigureCandleAsync(s => s.LookbackYears = (int)(years.Value ?? 1));
        var interval = new NumericUpDown { Name = "CandleMaintenanceInterval", Minimum = 5, Maximum = 1440, Value = settings.IntervalMinutes, FormatString = "0", Width = 120 };
        interval.ValueChanged += async (_, _) => await ConfigureCandleAsync(s => s.IntervalMinutes = (int)(interval.Value ?? 30));
        var limit = new NumericUpDown { Name = "CandleMaintenanceLimit", Minimum = 1, Maximum = 60, Value = settings.MaxInstruments, FormatString = "0", Width = 120 };
        limit.ValueChanged += async (_, _) => await ConfigureCandleAsync(s => s.MaxInstruments = (int)(limit.Value ?? 30));
        var picker = new InstrumentPicker(_svc) { Name = "CandleMaintenanceSelect" };
        picker.Picked += (profileId, instrument, _) =>
        {
            var profile = _svc.Connections.Find(profileId);
            if (profile != null) keeper.Track(profileId, InstrumentCatalog.IdentityFor(profile), instrument, explicitlySelected: true);
            RefreshCandleMaintenance();
        };
        var instruments = new ListBox
        {
            Name = "CandleMaintenanceInstruments", MaxHeight = 220,
            ItemTemplate = new FuncDataTemplate<TrackedCandleInstrument>((item, _) => new TextBlock
            {
                Text = item == null ? "" : item.Instrument.Symbol + " · " + item.Instrument.Exchange + " · " + item.ProfileId +
                    "\n" + (_candleStatuses.GetValueOrDefault(item.Key) ?? "Waiting for candle maintenance."),
                TextWrapping = TextWrapping.Wrap,
            }),
        };
        _candleInstruments = instruments;
        var remove = Ui.Button("Remove selected", MaterialIconKind.DeleteOutline, async () =>
        {
            if (instruments.SelectedItem is TrackedCandleInstrument item)
                await ConfigureCandleAsync(s => s.Instruments.RemoveAll(i => i.Key == item.Key));
        });
        remove.Name = "CandleMaintenanceRemove";
        var run = Ui.Button("Update now", MaterialIconKind.Refresh, async () =>
        {
            try { await keeper.RunNowAsync(); }
            catch (Exception ex) { _candleState.Text = "Candle maintenance failed: " + ex.Message; }
            RefreshCandleMaintenance();
        });
        run.Name = "CandleMaintenanceRun";
        var cancel = Ui.Button("Cancel and drain", MaterialIconKind.Cancel, async () => { await keeper.CancelAsync(); RefreshCandleMaintenance(); });
        cancel.Name = "CandleMaintenanceCancel";
        RefreshCandleMaintenance();
        return Section("Background candle history", MaterialIconKind.Update,
            Setting("Maintain selected candle history", "Off by default. Disabled means no background requests. One owned nonoverlapping run, at most 24 monthly loads and a five-minute cancellation budget; cancellation drains in-flight work before stores close.", enabled),
            Setting("Track newly opened instruments", "Only while maintenance is enabled. Up to 60 selected/opened instruments; opening/searching never changes a trading account.", auto),
            Setting("Lookback (years)", "Incremental current-month refresh plus bounded older-month catch-up at 1/5/15/60 minute resolution. Provider gaps and failures remain visible.", years),
            Setting("Interval (minutes)", "No overlapping refresh writes; foreground history shares the same monthly cache coordination.", interval),
            Setting("Instrument limit", "Bound selected/opened instruments independently of the monthly-request limit. Disabled providers do not receive requests.", limit),
            Ui.V(8, picker, instruments, Ui.H(8, remove, run, cancel), _candleState));
    }
    private async Task ConfigureCandleAsync(Action<KlineMaintenanceSettings> change)
    {
        try
        {
            var settings = _svc.KlineKeeper.Settings;
            change(settings);
            await _svc.KlineKeeper.ConfigureAsync(settings);
            RefreshCandleMaintenance();
        }
        catch (Exception ex) { _candleState.Text = "Candle maintenance settings could not be applied: " + ex.Message; }
    }
    private void RefreshCandleMaintenance()
    {
        var keeper = _svc.KlineKeeper;
        var state = keeper.Status;
        _candleState.Text = state.Message + (state.LastRunUtc is { } last ? $" Last run: {last:yyyy-MM-dd HH:mm} UTC." : "");
        bool statusChanged = _candleStatuses.Count != state.Instruments.Count ||
            _candleStatuses.Any(pair => !state.Instruments.TryGetValue(pair.Key, out var value) || pair.Value != value);
        _candleStatuses = state.Instruments;
        if (_candleInstruments != null)
        {
            var instruments = keeper.Settings.Instruments;
            var current = (_candleInstruments.ItemsSource as IEnumerable<TrackedCandleInstrument>)?.Select(i => i.Key);
            if (statusChanged || current == null || !current.SequenceEqual(instruments.Select(i => i.Key)))
            {
                string? selected = (_candleInstruments.SelectedItem as TrackedCandleInstrument)?.Key;
                _candleInstruments.ItemsSource = instruments;
                _candleInstruments.SelectedItem = instruments.FirstOrDefault(i => i.Key == selected);
            }
        }
    }
}
