using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Windows;

namespace TradingHub.App.Views;

/// <summary>Ajustes generales de la plataforma.</summary>
internal sealed class SettingsView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;

    public SettingsView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        // el margen va en el contenido: con Padding en el ScrollViewer el final quedaba tapado y no se podía bajar
        var content = Build();
        content.Margin = new Thickness(28, 24, 28, 32);
        Content = new ScrollViewer { Content = content };
    }

    public void OnShown()
    {
    }

    private int _tick;

    public void OnTimer()
    {
        if (IsVisible && ++_tick % 8 == 0) RefreshSleep(); // ~2 s
    }

    private Control Build()
    {
        var s = _svc.Settings;

        // zona horaria: las habituales primero
        var tz = new ComboBox { MinWidth = 320 };
        var common = new[] { "America/Argentina/Buenos_Aires", "America/New_York", "America/Chicago", "Europe/London", "Europe/Madrid", "UTC" };
        tz.Items.Add(new ComboBoxItem { Content = $"This Mac's time ({TimeZoneInfo.Local.Id})", Tag = "" });
        foreach (var id in common)
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z)) tz.Items.Add(new ComboBoxItem { Content = Label(z), Tag = z.Id });
        tz.Items.Add(new Separator());
        foreach (var z in TimeZoneInfo.GetSystemTimeZones().Where(z => !common.Contains(z.Id)).OrderBy(z => z.BaseUtcOffset).ThenBy(z => z.Id))
            tz.Items.Add(new ComboBoxItem { Content = Label(z), Tag = z.Id });
        tz.SelectedItem = tz.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == (s.TimeZoneId ?? "")) ?? tz.Items[0];
        tz.SelectionChanged += (_, _) =>
        {
            if (tz.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            s.TimeZoneId = id == "" ? null : id;
            s.Save();
            if (TopLevel.GetTopLevel(this) is MainWindow w)
            {
                foreach (var t in w.Trading.Tabs)
                {
                    if (t.Model == null) continue;
                    t.Model.Settings.TimeZoneId = s.TimeZoneId;
                    t.Model.Invalidate();
                }
            }

            _status("Time zone updated");
        };

        var confirm = Switch(s.ConfirmRealOrders, v => s.ConfirmRealOrders = v);
        var sounds = Switch(s.SoundAlerts, v => s.SoundAlerts = v);
        var record = Switch(s.RecordLiveData, v =>
        {
            s.RecordLiveData = v;
            _svc.Hub.RecordLive = v;
        });

        var appearance = new Ui.Segmented<string>(new[] { (AppTheme.Dark, "Dark", (string?)"Black"), (AppTheme.Light, "Light", "White") });
        appearance.Select(AppTheme.IsLight ? AppTheme.Light : AppTheme.Dark);
        appearance.SelectionChanged += mode =>
        {
            s.ThemeMode = mode;
            s.Save();
            AppTheme.Apply(mode);
        };
        var general = Section("General", MaterialIconKind.TuneVariant,
            Setting("Appearance", "Use dark or light appearance throughout the platform. Individual chart colors remain unchanged.", appearance),
            Setting("Chart time zone", "Time on chart axes, tape, and sessions.", tz),
            Setting("Alert sounds", "Price alerts, large trades, and indicator notices.", sounds)
            );

        var trading = Section("Trade", MaterialIconKind.SwapVertical,
            Setting("Confirm real orders", "Request confirmation before sending real orders (in addition to provider settings).", confirm),
            Setting("Simulated account", $"Initial balance {_svc.Portfolio.BeginBalance:N2}. Simulator orders model queue placement and recorded trade flow.", new TextBlock()));

        var data = Section("Data", MaterialIconKind.DatabaseOutline,
            Setting("Record incoming live data", "Save each incoming trade for charts, replay, and backtesting.", record),
            Setting("Data folder", AppPaths.Root, Ui.Button("Open", MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.Root))),
            Setting("Custom indicators (C#)", AppPaths.UserIndicators, Ui.Button("Open", MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.UserIndicators))),
            Setting("Strategies (C#)", AppPaths.Strategies, Ui.Button("Open", MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.Strategies))));

        var sleep = BuildSleep();
        var liveStart = BuildLiveStart();
        var hotkeys = BuildHotkeys();

        var logo = new Image { Source = AppIcon.Bitmap, Width = 56, Height = 56 };
        var about = Ui.Card(Ui.H(16, logo, Ui.V(4,
            new TextBlock { Text = TradingHub.Common.Product.Name, Classes = { "h1" } },
            Ui.Muted($"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · .NET {Environment.Version} · Avalonia + SkiaSharp"),
            Ui.Faint("Personal order-flow trading platform with footprint, DOM, tape, C# indicators, strategies and backtesting."),
            Creator())));

        return new StackPanel
        {
            Spacing = 18,
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { Ui.V(4, Ui.H1("Settings"), Ui.Muted("General Trading Hub preferences")), general, trading, liveStart, sleep, hotkeys, data, about },
        };
    }

    // ===================================================================== estrategias en vivo al abrir

    private Control BuildLiveStart()
    {
        var s = _svc.Settings;
        var auto = Switch(s.AutoResumeLive, v => s.AutoResumeLive = v);
        var real = Switch(s.AutoResumeLiveReal, v => s.AutoResumeLiveReal = v);
        real.IsEnabled = s.AutoResumeLive;
        auto.IsCheckedChanged += (_, _) => real.IsEnabled = auto.IsChecked == true;
        var smart = Switch(s.SmartContracts, v => s.SmartContracts = v);
        var menuBar = Switch(s.KeepInMenuBar, v =>
        {
            s.KeepInMenuBar = v;
            Services.MenuBar.Current?.Refresh();
        });
        return Section("Live strategies", MaterialIconKind.PlayCircleOutline,
            Setting("Keep running in menu bar after closing window", "Closing the window leaves Trading Hub running in the menu bar with active strategies. Use its icon to view live positions. To quit completely, use Command-Q or Quit Trading Hub.", menuBar),
            Setting("Resume automatically on startup", "Strategies that were running before shutdown resume at startup with their previous parameters, sizing and limits. Paused strategies stay paused. When disabled, resume manually from Live.", auto),
            Setting("Include real accounts", "Off (recommended): real-money strategies require manual resumption. On: they resume automatically.", real),
            Setting("Auto-select mini or micro contract", "For fractional mini-futures exposure (for example 0.5 NQ), use the equivalent micro-contract quantity (5 MNQ). Whole mini quantities stay on the mini. Open positions retain their contract until flat.", smart));
    }

    // ===================================================================== modo sin dormir

    private readonly TextBlock _sleepState = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    /// <summary>Quién lo hizo, con enlace a su perfil.</summary>
    private static Control Creator()
    {
        var handle = new TextBlock
        {
            Text = "@ralphtradesz",
            Foreground = Ui.Accent,
            FontWeight = FontWeight.SemiBold,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        handle.PointerPressed += (_, _) => Shell.OpenUrl("https://x.com/ralphtradesz");
        ToolTip.SetTip(handle, "x.com/ralphtradesz");
        return Ui.H(6, new TextBlock { Text = "Created by", Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center }, handle);
    }

    private Control BuildSleep()
    {
        var s = _svc.Settings;
        var awake = Switch(s.KeepAwake, v => s.KeepAwake = v);
        var power = Switch(s.LidClosedOnlyOnPower, v => s.LidClosedOnlyOnPower = v);
        var lid = Switch(s.KeepAwakeLidClosed, v =>
        {
            s.KeepAwakeLidClosed = v;
            _status(v ? "Closed lid: macOS requests authorization once while running; restored on quitting Trading Hub." : "Closed-lid mode disabled: closing the lid will sleep the Mac as usual");
        });
        power.IsEnabled = s.KeepAwakeLidClosed;
        lid.IsCheckedChanged += (_, _) => power.IsEnabled = lid.IsChecked == true;
        RefreshSleep();
        return Section("Prevent sleep", MaterialIconKind.SleepOff,
            Setting("Keep Mac awake", "Prevent idle sleep while live strategies or Claude agents run (display may still sleep). No password required.", awake),
            Setting("Keep running with lid closed", "By default macOS sleeps when the lid closes. If enabled while work is running, Trading Hub asks once for administrator permission and keeps the Mac active with the lid closed. The setting reverts when the app exits. Closed-lid ventilation is limited: never store a running Mac in a bag.", lid),
            Setting("Only with charger connected", "On battery, macOS may sleep when the lid is closed even if this option is enabled (recommended).", power),
            _sleepState);
    }

    private void RefreshSleep()
    {
        var g = _svc.Sleep;
        string reason = _svc.AwakeReason() ?? "nothing running";
        _sleepState.Text = (g.Awake ? $"Now: awake ({g.Reason})." : $"Now: may sleep ({reason}).") +
                           (g.LidHelperRunning ? " Closed lid: active." : _svc.Settings.KeepAwakeLidClosed ? " Closed lid: activates while work is running." : "") +
                           (g.LidError != null ? " " + g.LidError : "");
    }

    // ===================================================================== atajos de teclado

    private readonly Dictionary<string, Button> _hotkeyButtons = new();

    private Control BuildHotkeys()
    {
        var s = _svc.Settings;
        var list = new StackPanel { Spacing = 6 };
        foreach (var group in Hotkeys.All.GroupBy(h => h.Group))
        {
            list.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Classes = { "h3" }, Margin = new Thickness(0, list.Children.Count > 0 ? 12 : 0, 0, 2) });
            foreach (var h in group)
            {
                var def = h;
                var key = new Button { MinWidth = 150, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10, 4) };
                ToolTip.SetTip(key, "Click and press the key combination. Escape cancels · Backspace unassigns.");
                _hotkeyButtons[def.Id] = key;
                bool capturing = false;
                key.Click += (_, _) =>
                {
                    capturing = true;
                    key.Content = "Press a key…";
                    key.Classes.Add("primary");
                    key.Focus();
                };
                key.LostFocus += (_, _) =>
                {
                    if (!capturing) return;
                    capturing = false;
                    key.Classes.Remove("primary");
                    RefreshHotkey(def.Id);
                };
                key.AddHandler(KeyDownEvent, (_, e) =>
                {
                    if (!capturing) return;
                    e.Handled = true;
                    if (e.Key == Avalonia.Input.Key.Escape)
                    {
                        capturing = false;
                        key.Classes.Remove("primary");
                        RefreshHotkey(def.Id);
                        return;
                    }

                    if (e.Key is Avalonia.Input.Key.Back or Avalonia.Input.Key.Delete)
                    {
                        s.Hotkeys[def.Id] = "";
                    }
                    else
                    {
                        var gesture = Hotkeys.FromKey(e.Key, e.KeyModifiers);
                        if (gesture == null) return; // solo un modificador: sigue esperando
                        // si otra acción ya usaba ese atajo, se le saca
                        foreach (var other in Hotkeys.All.Where(o => o.Id != def.Id && Hotkeys.GestureOf(s, o.Id) == gesture))
                        {
                            s.Hotkeys[other.Id] = "";
                            RefreshHotkey(other.Id);
                            _status($"{Hotkeys.Pretty(gesture)} was assigned to «{other.Name}»: reassigned to «{def.Name}»");
                        }

                        s.Hotkeys[def.Id] = gesture;
                    }

                    capturing = false;
                    key.Classes.Remove("primary");
                    s.Save();
                    RefreshHotkey(def.Id);
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                RefreshHotkey(def.Id);

                var row = new DockPanel();
                DockPanel.SetDock(key, Dock.Right);
                row.Children.Add(key);
                row.Children.Add(new TextBlock { Text = def.Name, VerticalAlignment = VerticalAlignment.Center });
                list.Children.Add(row);
            }
        }

        var reset = Ui.Button("Restore defaults", MaterialIconKind.Restore, () =>
        {
            s.Hotkeys.Clear();
            s.Save();
            foreach (var h in Hotkeys.All) RefreshHotkey(h.Id);
            _status("Shortcuts reset");
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        var note = new TextBlock
        {
            Text = "Shortcuts apply to the active chart in Trading and detached chart windows. Trading shortcuts are unassigned by default; live-order confirmation still applies.",
            Classes = { "muted", "small" },
            TextWrapping = TextWrapping.Wrap,
        };
        return Ui.Card(Ui.V(16, Ui.H(10, Ui.Icon(MaterialIconKind.KeyboardOutline, 18, Ui.Accent), Ui.H2("Keyboard shortcuts")), note, list, reset), 20);
    }

    private void RefreshHotkey(string id)
    {
        if (_hotkeyButtons.TryGetValue(id, out var b)) b.Content = Hotkeys.Pretty(Hotkeys.GestureOf(_svc.Settings, id));
    }

    private static string Label(TimeZoneInfo z)
    {
        var off = z.BaseUtcOffset;
        return $"(UTC{(off < TimeSpan.Zero ? "-" : "+")}{off:hh\\:mm}) {z.Id.Replace('_', ' ')}";
    }

    private ToggleSwitch Switch(bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "" };
        t.IsCheckedChanged += (_, _) =>
        {
            set(t.IsChecked == true);
            _svc.Settings.Save();
        };
        return t;
    }

    private static Control Section(string title, MaterialIconKind icon, params Control[] rows)
    {
        var list = new StackPanel { Spacing = 0 };
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) list.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 14) }.Classes("divider"));
            list.Children.Add(rows[i]);
        }

        return Ui.Card(Ui.V(16, Ui.H(10, Ui.Icon(icon, 18, Ui.Accent), Ui.H2(title)), list), 20);
    }

    private static Control Setting(string title, string description, Control control)
    {
        var text = Ui.V(3, new TextBlock { Text = title, FontWeight = FontWeight.Medium }, new TextBlock { Text = description, Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel();
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(24, 0, 0, 0);
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(text);
        return row;
    }
}
