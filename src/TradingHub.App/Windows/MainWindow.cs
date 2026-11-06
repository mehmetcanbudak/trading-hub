using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Windows;

/// <summary>Página de la ventana principal (Trading, Estrategias, Datos…).</summary>
internal sealed record Page(string Id, string Title, MaterialIconKind Icon, Func<Control> Create, KeyGesture? Gesture);

/// <summary>
/// Ventana principal: barra superior propia (integrada con los botones de macOS), barra lateral
/// de navegación, páginas y barra de estado.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly AppServices _svc;
    private readonly DispatcherTimer _timer;
    private readonly Panel _host = new();
    private readonly StackPanel _toasts = new();
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, ToggleButton> _navButtons = new();
    private readonly List<Page> _pageDefs;
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "muted", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _infoText = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "faint", "small" } };
    private readonly TextBlock _pnlText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, FontWeight = FontWeight.SemiBold };
    private readonly Avalonia.Controls.Shapes.Ellipse _connDot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _connText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private DateTime _lastStatus;
    private DateTime _lastTickAt;
    private string _current = "";
    private bool _startupStarted;

    public MainWindow(AppServices svc)
    {
        _svc = svc;
        Title = TradingHub.Common.Product.Name;
        Width = Math.Max(1000, svc.Settings.WindowWidth);
        Height = Math.Max(640, svc.Settings.WindowHeight);
        MinWidth = 1000;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Window;
        ExtendClientAreaToDecorationsHint = true;
        // barra de título "gruesa" de macOS (como Finder): los botones de la ventana quedan centrados en la barra,
        // a la misma altura que los de la app
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome | Avalonia.Platform.ExtendClientAreaChromeHints.OSXThickTitleBar;
        ExtendClientAreaTitleBarHeightHint = TopBarHeight;

        Trading = new TradingView(svc, SetStatus);
        _pages["trading"] = Trading;
        svc.Live.Trading = () => Trading;
        Action<PineImportSource> reviewPine = source =>
        {
            Navigate("strategies");
            if (Page("strategies") is StrategiesView strategies) _ = strategies.ReviewPineAsync(source);
        };
        svc.ReviewPineSource = reviewPine;
        svc.Live.Notice += text => Dispatcher.UIThread.Post(() => SetStatus(text));
        _pageDefs = new List<Page>
        {
            new("trading", "Trading", MaterialIconKind.ChartBoxOutline, () => Trading, new KeyGesture(Key.D1, Keys.Command)),
            new("accounts", "Account", MaterialIconKind.WalletOutline, () => new AccountsView(svc, SetStatus, this), new KeyGesture(Key.D5, Keys.Command)),
            new("strategies", "Backtesting", MaterialIconKind.FlaskOutline, () => new StrategiesView(svc, SetStatus), new KeyGesture(Key.D2, Keys.Command)),
            new("live", "Live", MaterialIconKind.AccessPoint, () => new LiveView(svc, SetStatus, this), new KeyGesture(Key.D7, Keys.Command)),
            new("data", "Data", MaterialIconKind.DatabaseOutline, () => new DataView(svc, SetStatus, this), new KeyGesture(Key.D3, Keys.Command)),
            new("connections", "Connections", MaterialIconKind.LanConnect, () => new ConnectionsView(svc, SetStatus), new KeyGesture(Key.D4, Keys.Command)),
            new("settings", "Settings", MaterialIconKind.CogOutline, () => new SettingsView(svc, SetStatus), new KeyGesture(Key.OemComma, Keys.Command)),
        };

        Content = BuildLayout();
        BuildMenu();
        if (Os.IsWindows) SetUpWindows();
        Navigate("trading");

        var scripts = _svc.Scripts ??= new UserScripts(_svc.Catalog);
        void OnScriptsCompiled(TradingHub.Scripting.ScriptCompileResult result) => SetStatus(result.Success
            ? $"Custom indicators compiled in {result.Elapsed.TotalMilliseconds:0} ms"
            : $"Error compiling custom indicators: {result.Diagnostics.FirstOrDefault(d => d.Severity == "error")}");
        scripts.Compiled += OnScriptsCompiled;
        if (scripts.LastResult == null) scripts.CompileAll();

        Log.Entry += e =>
        {
            if (e.Level >= LogLevel.Warning) Dispatcher.UIThread.Post(() => SetStatus("⚠ " + e.Message));
        };

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();

        Opened += async (_, _) =>
        {
            // Opened also fires after a tray reopen; restoration and automatic resume run only once per process.
            if (_startupStarted) return;
            _startupStarted = true;
            FitToScreen();
            await Trading.RestoreOrDefaultAsync();
            // y las tuyas en vivo, si así está en Ajustes (unos segundos después: las conexiones ya arrancaron)
            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (_svc.IsShuttingDown) return;
                    await _svc.Live.ResumeAtStartAsync();
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex);
                }
            }), TaskScheduler.Default);
        };

        // atajos de teclado configurables (solo con la sección Trading a la vista)
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_current != "trading" || e.Handled) return;
            if (e.Key == Key.Escape)
            {
                bool dismissed = false;
                for (int i = 0; i < Trading.Pages.Count; i++)
                    foreach (var chart in Trading.Pages[i].Charts)
                        dismissed |= chart.View.DismissRecordingWarning();
                if (dismissed)
                {
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && Trading.ActiveTab?.Model?.FocusTrade != null)
            {
                Trading.ClearFocusTrade(Trading.ActiveTab);
                e.Handled = true;
                return;
            }

            if (Hotkeys.Match(_svc.Settings, e, this) is { } action && Trading.ExecuteHotkey(action)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            Workspace.Save(Trading);
            if (WindowState == WindowState.Normal)
            {
                _svc.Settings.WindowWidth = Width;
                _svc.Settings.WindowHeight = Height;
            }

            _svc.Settings.Save();
            if (e.CloseReason == WindowCloseReason.WindowClosing &&
                App.Quit is { IsQuitting: true, IsTerminalComplete: false })
            {
                // A second red-close must not force a desktop Exit while UI-owned drains still need this dispatcher.
                e.Cancel = true;
                return;
            }
            // la cruz roja no cierra Trading Hub: queda en la barra de menú, con las estrategias y los agentes trabajando
            // (⌘Q o «Salir de Trading Hub» del panel sí la cierran del todo)
            if (e.CloseReason == WindowCloseReason.WindowClosing && _svc.Settings.KeepInMenuBar && MenuBar.Current != null)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            // otherwise the red button ends Trading Hub: it quits like ⌘Q and the menu bar's Quit (asking first while live
            // strategies hold positions) and nothing stops before that decision; the quit's own exit then closes the
            // windows as an application shutdown, which comes through here
            if (e.CloseReason == WindowCloseReason.WindowClosing && App.Quit is { IsQuitting: false } quit)
            {
                e.Cancel = true;
                Dispatcher.UIThread.Post(() => _ = quit.QuitAsync());
                return;
            }

            Trading.CloseFloating();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            scripts.Compiled -= OnScriptsCompiled;
            if (ReferenceEquals(svc.ReviewPineSource, reviewPine)) svc.ReviewPineSource = null;
        };
    }

    private int _liveSaveTick;

    /// <summary>La conexión de datos en una línea (la del botón de arriba a la derecha).</summary>
    internal string ConnectionSummary => _connText.Text ?? "";

    internal AppServices Services => _svc;
    internal TradingView Trading { get; }

    /// <summary>Una sección ya abierta (o null).</summary>
    internal Control? Page(string id) => _pages.GetValueOrDefault(id);

    // ===================================================================== layout

    private Control BuildLayout()
    {
        var root = new DockPanel { Background = Ui.Bg0 };

        var top = BuildTopBar();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        // los avisos flotan arriba a la derecha, sobre la página
        _toasts.HorizontalAlignment = HorizontalAlignment.Right;
        _toasts.VerticalAlignment = VerticalAlignment.Top;
        _toasts.Margin = new Thickness(0, 12, 16, 0);
        _toasts.Width = 430;
        _toasts.Spacing = 10;
        root.Children.Add(new Panel { Children = { _host, _toasts } });
        return root;
    }

    /// <summary>Keeps the initial Windows window inside the monitor's DPI-scaled working area.</summary>
    private void FitToScreen()
    {
        if (!Os.IsWindows || WindowState != WindowState.Normal || Screens.ScreenFromWindow(this) is not { } screen) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        double w = area.Width / scale, h = area.Height / scale;
        if (Width <= w && Height <= h && Position.Y >= area.Y) return;
        if (w < MinWidth || h < MinHeight)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        Width = Math.Min(Width, w);
        Height = Math.Min(Height, h);
        Position = new PixelPoint(area.X + (int)((area.Width - Width * scale) / 2), area.Y + (int)((area.Height - Height * scale) / 2));
    }

    /// <summary>Height shared by the custom title bar and native window controls.</summary>
    private const double TopBarHeight = 52;
    private const double WindowsCaptionButtonsWidth = 3 * 45 + 2 * 2;

    private Control BuildTopBar()
    {

        // navegación: integrada en la barra de título para no ocupar ancho
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var labels = new List<TextBlock>();
        foreach (var p in _pageDefs)
        {
            var label = new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center };
            labels.Add(label);
            var b = new ToggleButton { Content = Ui.H(7, Ui.Icon(p.Icon, 16), label) }.Classes("nav");
            ToolTip.SetTip(b, p.Gesture != null ? $"{p.Title} ({GestureText(p.Gesture)})" : p.Title);
            b.Click += (_, _) => Navigate(p.Id);
            _navButtons[p.Id] = b;
            nav.Children.Add(b);
        }

        // pegada a la izquierda: deja solo el lugar de los botones de la ventana (en pantalla completa no hay)
        var left = Ui.H(18, nav);
        void PlaceNav() => left.Margin = new Thickness(OperatingSystem.IsMacOS() && WindowState != WindowState.FullScreen ? 82 : 10, 0, 0, 0);
        PlaceNav();
        // con la ventana angosta la barra queda solo con íconos (el nombre sigue en el tooltip)
        SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < 1360;
            foreach (var l in labels) l.IsVisible = !compact;
        };

        var conn = new Border { Child = Ui.H(8, _connDot, _connText), Classes = { "pill" }, Padding = new Thickness(11, 4), VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        var reconnect = new MenuItem { Header = "Reconnect now" };
        reconnect.Click += (_, _) =>
        {
            _svc.Hub.ReconnectAll();
            SetStatus("Reconnecting… missing data from the interruption will be backfilled automatically.");
        };
        var toConnections = new MenuItem { Header = "Connections…" };
        toConnections.Click += (_, _) => Navigate("connections");
        var connMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        connMenu.Items.Add(reconnect);
        connMenu.Items.Add(toConnections);
        conn.PointerPressed += (_, e) =>
        {
            connMenu.ShowAt(conn);
            e.Handled = true;
        };
        ToolTip.SetTip(conn, "Active chart connection (click to reconnect or open Connections)");
        // el resultado de la simulada lleva a la sección Cuenta
        var pnl = new Border { Child = _pnlText, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(pnl, "View account: balance, orders, trades, and statistics");
        pnl.PointerPressed += (_, _) => Navigate("accounts");
        // modo claro / oscuro
        var themeButton = new Button { Classes = { "icon" }, VerticalAlignment = VerticalAlignment.Center };
        void ThemeIcon()
        {
            themeButton.Content = Ui.Icon(AppTheme.IsLight ? MaterialIconKind.WeatherNight : MaterialIconKind.WhiteBalanceSunny, 16);
            ToolTip.SetTip(themeButton, AppTheme.IsLight ? "Switch to dark mode" : "Switch to light mode");
        }

        ThemeIcon();
        themeButton.Click += (_, _) =>
        {
            _svc.Settings.ThemeMode = AppTheme.IsLight ? AppTheme.Dark : AppTheme.Light;
            _svc.Settings.Save();
            AppTheme.Apply(_svc.Settings.ThemeMode);
        };
        AppTheme.Changed += () =>
        {
            ThemeIcon();
            Trading.OnThemeChanged();
            InvalidateVisual();
        };
        var alerts = Ui.IconButton(MaterialIconKind.BellOutline, "Alerts / delivery status", Trading.OpenAlerts, 16);
        alerts.Name = "OpenUnifiedAlerts";
        var right = Ui.H(12, pnl, alerts, themeButton, conn);
        // Reserve the native Windows minimize, maximize and close buttons.
        void PlaceRight() => right.Margin = new Thickness(0, 0, 14 + (Os.IsWindows && WindowState != WindowState.FullScreen ? WindowsCaptionButtonsWidth : 0), 0);
        PlaceRight();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            PlaceNav();
            PlaceRight();
        };

        var bar = new DockPanel { Height = TopBarHeight, LastChildFill = false };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(left);
        var border = new Border { Child = bar, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };

        // arrastrar la ventana desde la barra y doble clic para maximizar
        border.PointerPressed += (_, e) =>
        {
            if (e.Handled || e.Source is not (Border or DockPanel or StackPanel) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };
        return border;
    }

    private static string GestureText(KeyGesture g)
    {
        string key = g.Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(g.Key - Key.D0)).ToString(),
            Key.OemComma => ",",
            _ => g.Key.ToString(),
        };
        return Keys.Format(g.KeyModifiers, key, mac: !Os.IsWindows);
    }

    private Control BuildStatusBar()
    {
        var bar = new DockPanel { Margin = new Thickness(14, 0), Height = 26 };
        DockPanel.SetDock(_infoText, Dock.Right);
        bar.Children.Add(_infoText);
        bar.Children.Add(_statusText);
        return new Border { Child = bar, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    // ===================================================================== navegación

    internal void Navigate(string id)
    {
        if (_svc.IsShuttingDown) return;
        var def = _pageDefs.FirstOrDefault(p => p.Id == id);
        if (def == null) return;
        if (!_pages.TryGetValue(id, out var page))
        {
            try
            {
                page = def.Create();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                page = new TextBlock { Text = "Could not open page: " + ex.Message, Margin = new Thickness(24) };
            }

            _pages[id] = page;
        }

        if (!_host.Children.Contains(page)) _host.Children.Add(page);
        foreach (var c in _host.Children) c.IsVisible = ReferenceEquals(c, page);
        foreach (var (pid, b) in _navButtons) b.IsChecked = pid == id;
        _current = id;
        Title = id == "trading" ? TradingHub.Common.Product.Name : $"{TradingHub.Common.Product.Name} — {def.Title}";
        (page as IPage)?.OnShown();
    }

    // ===================================================================== menú nativo

    private void BuildMenu()
    {
        var menu = new NativeMenu();

        var file = new NativeMenuItem("File") { Menu = new NativeMenu() };
        file.Menu.Add(Item("New chart", () =>
        {
            Navigate("trading");
            _ = Trading.NewChartAsync();
        }, new KeyGesture(Key.T, Keys.Command)));
        file.Menu.Add(Item("Close chart", Trading.CloseActiveTab, new KeyGesture(Key.W, Keys.Command)));
        file.Menu.Add(Item("Search instruments…", () =>
        {
            Navigate("trading");
            Trading.Picker.Open();
        }, new KeyGesture(Key.K, Keys.Command)));
        file.Menu.Add(new NativeMenuItemSeparator());
        file.Menu.Add(Item("Save workspace", () =>
        {
            Workspace.Save(Trading);
            SetStatus("Workspace saved");
        }, new KeyGesture(Key.S, Keys.Command)));
        file.Menu.Add(Item("Open data folder", () => Shell.OpenFolder(AppPaths.Root)));
        menu.Add(file);

        var chart = new NativeMenuItem("Chart") { Menu = new NativeMenu() };
        chart.Menu.Add(Item("Indicators…", Trading.OpenIndicators, new KeyGesture(Key.I, Keys.Command)));
        chart.Menu.Add(Item("Edit footprint…", Trading.OpenFootprintEditor, new KeyGesture(Key.F, Keys.Command | KeyModifiers.Shift)));
        chart.Menu.Add(Item("Auto scale", () =>
        {
            if (Trading.ActiveTab?.Model is { } m)
            {
                m.Viewport.AutoScale = true;
                m.Invalidate();
            }
        }, new KeyGesture(Key.A, Keys.Command | KeyModifiers.Alt)));
        chart.Menu.Add(Item("Go to latest price", () =>
        {
            if (Trading.ActiveTab?.Model is { } m)
            {
                m.Viewport.ScrollBars = 0;
                m.Invalidate();
            }
        }, new KeyGesture(Key.End, Keys.Command)));
        chart.Menu.Add(new NativeMenuItemSeparator());
        var layouts = new NativeMenuItem("Layout") { Menu = new NativeMenu() };
        foreach (var (layout, label, _) in TradingView.Layouts)
            layouts.Menu.Add(Item(label, () =>
            {
                Navigate("trading");
                _ = Trading.SetLayoutAsync(layout);
            }));
        chart.Menu.Add(layouts);
        chart.Menu.Add(Item("Detach chart into a window", () =>
        {
            Navigate("trading");
            Trading.DetachActive();
        }, new KeyGesture(Key.D, Keys.Command | KeyModifiers.Shift)));
        chart.Menu.Add(new NativeMenuItemSeparator());
        var panels = new NativeMenuItem("Panels") { Menu = new NativeMenu() };
        foreach (var (key, label) in new[] { ("side", "Right panel"), ("ticket", "Ticket"), ("dom", "DOM"), ("tape", "Trade tape") })
            panels.Menu.Add(Item($"Show or hide: {label}", () => Trading.TogglePanel(key)));
        chart.Menu.Add(panels);
        chart.Menu.Add(Item("DOM settings…", () =>
        {
            Navigate("trading");
            Trading.ShowDomSettings();
        }));
        menu.Add(chart);

        var go = new NativeMenuItem("Go") { Menu = new NativeMenu() };
        foreach (var p in _pageDefs) go.Menu.Add(Item(p.Title, () => Navigate(p.Id), p.Gesture));
        go.Menu.Add(new NativeMenuItemSeparator());
        go.Menu.Add(Item("Alerts / delivery status…", Trading.OpenAlerts));
        go.Menu.Add(Item("Random / blind practice…", () =>
        {
            Navigate("trading");
            Trading.OpenPracticeDialog();
        }));
        menu.Add(go);

        var code = new NativeMenuItem("Code") { Menu = new NativeMenu() };
        code.Menu.Add(Item("Indicator editor…", () => new CodeEditorWindow(_svc, CodeKind.Indicator).Show(this), new KeyGesture(Key.E, Keys.Command | KeyModifiers.Shift)));
        code.Menu.Add(Item("Open indicators folder", () => Shell.OpenFolder(AppPaths.UserIndicators)));
        code.Menu.Add(Item("Recompile indicators", () => _svc.Scripts?.CompileAll()));
        code.Menu.Add(new NativeMenuItemSeparator());
        code.Menu.Add(Item("Strategy backtesting…", () => Navigate("strategies")));
        code.Menu.Add(Item("Live strategies…", () => Navigate("live")));
        code.Menu.Add(Item("Open strategies folder", () => Shell.OpenFolder(AppPaths.Strategies)));
        menu.Add(code);

        NativeMenu.SetMenu(this, menu);
    }

    private readonly List<(KeyGesture Gesture, Action Run)> _menuShortcuts = new();

    private NativeMenuItem Item(string header, Action onClick, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header);
        if (gesture != null)
        {
            item.Gesture = gesture;
            _menuShortcuts.Add((gesture, onClick));
        }
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Registers native-menu shortcuts on Windows and routes Alt+F4 through the protected quit flow.</summary>
    private void SetUpWindows()
    {
        foreach (var (gesture, run) in _menuShortcuts)
            KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new Relay(run) });
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.F4 || e.KeyModifiers != KeyModifiers.Alt) return;
            e.Handled = true;
            MenuBar.Quit();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == OffScreenMarginProperty && Content is Control root) root.Margin = OffScreenMargin;
        };
    }

    // ===================================================================== estado

    internal void SetStatus(string text)
    {
        _statusText.Text = text;
        ToolTip.SetTip(_statusText, text);
    }

    internal void Tick()
    {
        try
        {
            // si el reloj saltó (la Mac durmió) las conexiones quedaron muertas: se reconecta al toque
            var now = DateTime.UtcNow;
            if (_lastTickAt != default && (now - _lastTickAt).TotalSeconds > 20)
            {
                _svc.Hub.ReconnectAll();
                SetStatus($"Resumed after {(now - _lastTickAt).TotalMinutes:0} minutes paused: reconnecting and backfilling missing data.");
            }

            _lastTickAt = now;
            _svc.Hub.PumpAll();
            // los gráficos en ventanas aparte se actualizan siempre; los de la principal, más lento si no se ven
            Trading.Tick(_current == "trading" && IsVisible);
            if ((DateTime.UtcNow - _lastStatus).TotalMilliseconds > 250)
            {
                _lastStatus = DateTime.UtcNow;
                UpdateStatusBar();
                if (++_liveSaveTick % 12 == 0) _svc.SaveLiveState(); // cada ~3 s
                if (_liveSaveTick % 4 == 0) _svc.Live.Tick();
                if (_liveSaveTick % 20 == 0) // cada ~5 s: modo sin dormir según lo que esté corriendo
                    _svc.Sleep.Update(_svc.Settings.KeepAwake, _svc.Settings.KeepAwakeLidClosed, _svc.Settings.LidClosedOnlyOnPower, _svc.AwakeReason());
                foreach (var p in _pages.Values.OfType<IPage>()) p.OnTimer();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }


    private static Border Toast(Control content) => new()
    {
        Child = content,
        Background = Ui.Bg1,
        BorderBrush = Ui.Warn,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12),
        BoxShadow = BoxShadows.Parse("0 6 24 0 #40000000"),
    };

    private void UpdateStatusBar()
    {
        var m = Trading.ActiveTab?.Model;
        if (m != null)
        {
            var d = m.Data;
            _connDot.Fill = d.Status switch
            {
                FeedStatus.Connected => Ui.Up,
                FeedStatus.Connecting or FeedStatus.Reconnecting => Ui.Warn,
                _ => Ui.Down,
            };
            string feedName = m.Settings.BlindReplay ? "Practice" : _svc.Connections.Find(Trading.ActiveTab!.FeedId)?.Name ?? d.Feed.DisplayName;
            string state = StatusName(d.Status);
            var quiet = DateTime.UtcNow - d.LastDataAt;
            string? tip = null;
            bool quietLong = d.LastDataAt.Year < 2000 || quiet.TotalSeconds > 60;
            if (d.Status == FeedStatus.Connected && d.Feed.IsLive && quietLong
                && (d.MarketClosed || (d.Feed is ISessionAwareFeed && FuturesSessions.IsGlobexClosed(DateTime.UtcNow))))
            {
                // futuros fuera de horario: no es un corte, el mercado está cerrado
                var tz = m.Settings.TimeZone;
                var open = FuturesSessions.NextGlobexOpen(DateTime.UtcNow);
                var openLocal = TimeZoneInfo.ConvertTimeFromUtc(open, tz);
                state = open > DateTime.UtcNow ? $"market closed · opens {Day(openLocal, tz)} {openLocal:HH:mm}" : "market closed";
                tip = d.ClosedAt is { } last
                    ? $"Latest trade: {Day(TimeZoneInfo.ConvertTimeFromUtc(last, tz), tz)} {TimeZoneInfo.ConvertTimeFromUtc(last, tz):HH:mm}. Connection remains active."
                    : "Connection remains active; data will resume when the market opens.";
                _connDot.Fill = Ui.Text2;
            }
            else if (d.Status == FeedStatus.Connected && d.Feed.IsLive && d.LastDataAt.Year > 2000 && quiet.TotalSeconds > 60)
            {
                state = $"no data for {(quiet.TotalMinutes < 60 ? $"{quiet.TotalMinutes:0} min" : $"{quiet.TotalHours:0} h")}";
                _connDot.Fill = Ui.Warn;
            }
            else if (d.Status == FeedStatus.Reconnecting && d.StatusMessage is { } rm
                     && System.Text.RegularExpressions.Regex.Match(rm, @"attempt (\d+)") is { Success: true } attempt)
            {
                state = $"reconnecting… (attempt {attempt.Groups[1].Value})";
            }

            _connText.Text = $"{feedName} · {state}";
            ToolTip.SetTip(_connText, m.Settings.BlindReplay ? "Practice replay · isolated account and clock" : tip ?? d.StatusMessage);
            _infoText.Text = d.IsLoadingHistory || d.IsExtending
                ? $"Loading history {d.HistoryProgress * 100:0}%"
                : m.Settings.BlindReplay ? $"{d.Trades.Count:N0} trades · {m.Series.Count:N0} bars · practice timeline"
                : $"{d.Trades.Count:N0} trades · {m.Series.Count:N0} bars · since {TimeZoneInfo.ConvertTimeFromUtc(d.HistoryFrom, m.Settings.TimeZone):dd/MM HH:mm}";
            if (_svc.Settings.RecordDepth) _infoText.Text += " · " + m.DepthStatusText;
            if (!string.IsNullOrWhiteSpace(d.HistoryWarning)) _infoText.Text += " · " + d.HistoryWarning;
            ToolTip.SetTip(_infoText, m.DepthStatusText + (d.HistoryWarning == null ? "" : "\n" + d.HistoryWarning) + (d.HistoryError == null ? "" : "\n" + d.HistoryError));
        }

        if (Trading.ActiveTab?.Model is { } active && _svc.ReplaySessions.TryGet(active.Data) is { } practice)
        {
            var result = practice.Result();
            _pnlText.Foreground = result.RealizedNet >= 0 ? Ui.Up : Ui.Down;
            _pnlText.Text = $"Practice  {result.RealizedNet:+0.00;-0.00;0.00}  ·  {result.Equity:N2}";
        }
        else
        {
            var (day, balance) = _svc.AccountCenter.SimSummary();
            _pnlText.Foreground = day >= 0 ? Ui.Up : Ui.Down;
            _pnlText.Text = $"Simulated  {day:+0.00;-0.00;0.00}  ·  {balance:N2}";
        }
        Trading.UpdatePosition();
    }

    private static string Day(DateTime local, TimeZoneInfo tz)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
        if (local.Date == today) return "today";
        if (local.Date == today.AddDays(1)) return "tomorrow";
        return local.ToString("ddd", TradingHub.Common.UiCulture.Current).TrimEnd('.');
    }

    private static string StatusName(FeedStatus s) => s switch
    {
        FeedStatus.Connected => "connected",
        FeedStatus.Connecting => "connecting…",
        FeedStatus.Reconnecting => "reconnecting…",
        FeedStatus.Error => "error",
        _ => "disconnected",
    };
}

/// <summary>Ícono de la app (Assets/icon.png).</summary>
internal static class AppIcon
{
    private static Avalonia.Media.Imaging.Bitmap? _bitmap;

    public static Avalonia.Media.Imaging.Bitmap? Bitmap
    {
        get
        {
            if (_bitmap != null) return _bitmap;
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(new Uri("avares://TradingHub/Assets/icon.png"));
                _bitmap = new Avalonia.Media.Imaging.Bitmap(s);
            }
            catch
            {
                // sin ícono
            }

            return _bitmap;
        }
    }

    public static WindowIcon? Window => Bitmap is { } b ? new WindowIcon(b) : null;
}

/// <summary>Una página que quiere enterarse cuando se muestra o refrescarse periódicamente.</summary>
internal interface IPage
{
    void OnShown();
    void OnTimer();
}

/// <summary>Opens files, folders and URLs through macOS Launch Services or the Windows shell.</summary>
internal static class Shell
{
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows()) StartWithShell(path);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = false });
        }
        catch
        {
            // ignorar
        }
    }

    /// <summary>Abre una dirección web en el navegador.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                StartWithShell(url);
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // ignorar
        }
    }

    /// <summary>Reveals a selected file in Finder or File Explorer.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", RevealArguments(Path.GetFullPath(path))) { UseShellExecute = false })?.Dispose();
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // ignorar
        }
    }

    public static void Open(string pathOrUrl)
    {
        try
        {
            if (OperatingSystem.IsWindows()) StartWithShell(pathOrUrl);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", $"\"{pathOrUrl}\"") { UseShellExecute = false });
        }
        catch
        {
            // ignorar
        }
    }

    /// <summary>Explorer requires its selected path quoted immediately after the /select comma.</summary>
    internal static string RevealArguments(string fullPath) => $"/select,\"{fullPath}\"";

    private static void StartWithShell(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    /// <summary>Opens Windows power options where the user can choose the lid-close behavior.</summary>
    public static void OpenWindowsPowerOptions()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "/name Microsoft.PowerOptions /page pageGlobalSettings") { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // The OS owns this settings surface.
        }
    }
}
