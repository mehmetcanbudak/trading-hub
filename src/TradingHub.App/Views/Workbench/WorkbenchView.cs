using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Market;

namespace TradingHub.App.Views.Workbench;

/// <summary>
/// The Workbench page: market-analysis panels (aggregated trades, depth heat map, liquidity/liquidation map,
/// derivatives, options and a toolbox) that follow one selected instrument, by default the active chart's. Each panel
/// can be detached into its own window; panels do network or subscription work only while they are visible.
/// Nothing on this page places orders.
/// </summary>
internal sealed class WorkbenchView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly MainWindow _main;
    private readonly WorkbenchContext _context;
    private readonly List<WorkbenchPanel> _panels;
    private readonly Dictionary<string, Window> _windows = new();
    private readonly Dictionary<string, ToggleButton> _nav = new();
    private readonly ContentControl _host = new() { Name = "WorkbenchHost" };
    private readonly InstrumentPicker _picker;
    private readonly CheckBox _follow = new() { Name = "WorkbenchFollowChart", Content = "Follow the active chart" };
    private readonly TextBlock _selectionText = new() { Name = "WorkbenchSelection", Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private string _selected;
    private bool _closingAll;

    public WorkbenchView(AppServices svc, Action<string> status, MainWindow main)
    {
        _svc = svc;
        _main = main;
        _context = new WorkbenchContext(svc, status);
        _panels = new List<WorkbenchPanel>
        {
            new TapePanel(_context), new HeatmapPanel(_context), new LiquidityPanel(_context),
            new DerivativesPanel(_context), new OptionsPanel(_context), new ToolboxPanel(_context),
        };
        _selected = _panels.Any(p => p.Key == _context.Settings.Panel) ? _context.Settings.Panel : _panels[0].Key;
        _picker = new InstrumentPicker(svc) { Name = "WorkbenchInstrument", MinWidth = 220 };
        _picker.Picked += (profileId, instrument, _) => Pick(profileId, instrument);
        _follow.IsChecked = _context.Settings.FollowActiveChart;
        _follow.IsCheckedChanged += (_, _) =>
        {
            _context.Settings.FollowActiveChart = _follow.IsChecked == true;
            _context.Save();
            if (_context.Settings.FollowActiveChart) FollowActiveChart();
        };
        Content = Build();
        Select(_selected);
    }

    internal WorkbenchContext Context => _context;
    internal IReadOnlyList<WorkbenchPanel> Panels => _panels;
    internal WorkbenchSelection? Selection { get; private set; }
    internal string SelectedPanel => _selected;
    internal Window? PanelWindow(string key) => _windows.GetValueOrDefault(key);

    private Control Build()
    {
        var header = new DockPanel { Margin = new Thickness(18, 14, 18, 10), LastChildFill = true };
        var titles = Ui.V(2, Ui.H1("Workbench"), Ui.Faint("Market analysis panels for one instrument. Read-only: nothing here places orders."));
        DockPanel.SetDock(titles, Dock.Left);
        header.Children.Add(titles);
        var controls = Ui.H(12, _picker, _follow, _selectionText);
        controls.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(controls);

        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(12, 4, 8, 12), Width = 210 };
        foreach (var panel in _panels)
        {
            var p = panel;
            var button = new ToggleButton
            {
                Name = "WorkbenchNav" + p.Key, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = Ui.H(8, Ui.Icon(p.Icon, 16), new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center }),
            }.Classes("nav");
            button.Click += (_, _) => Select(p.Key);
            var detach = Ui.IconButton(MaterialIconKind.OpenInNew, "Open this panel in a separate window", () => Detach(p.Key), 14);
            detach.Name = "WorkbenchDetach" + p.Key;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(button);
            Grid.SetColumn(detach, 1);
            row.Children.Add(detach);
            _nav[p.Key] = button;
            nav.Children.Add(row);
        }

        var body = new DockPanel();
        var navBorder = new Border { Child = nav, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) };
        DockPanel.SetDock(navBorder, Dock.Left);
        body.Children.Add(navBorder);
        _host.Margin = new Thickness(14, 4, 18, 14);
        body.Children.Add(_host);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        return root;
    }

    /// <summary>Shows a panel in the page (or brings its window to the front when it is detached).</summary>
    internal void Select(string key)
    {
        var panel = _panels.FirstOrDefault(p => p.Key == key);
        if (panel == null) return;
        _selected = key;
        if (_context.Settings.Panel != key)
        {
            _context.Settings.Panel = key;
            _context.Save();
        }
        foreach (var (k, b) in _nav) b.IsChecked = k == key;
        if (_windows.TryGetValue(key, out var window))
        {
            _host.Content = DetachedNote(panel);
            window.Activate();
        }
        else _host.Content = panel;
        UpdateActivity();
    }

    private Control DetachedNote(WorkbenchPanel panel)
    {
        var back = Ui.Button("Return it here", MaterialIconKind.DockLeft, () => Reattach(panel.Key));
        back.Name = "WorkbenchDock";
        return new StackPanel
        {
            Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Name = "WorkbenchDetachedTitle", Text = panel.Title, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                Ui.Muted("This panel is open in a separate window."), back,
            },
        };
    }

    // ===================================================================== selection

    private void Pick(string profileId, Instrument instrument)
    {
        if (_svc.IsShuttingDown) return;
        try
        {
            var data = _svc.Open(profileId, instrument);
            _follow.IsChecked = false;
            Apply(new WorkbenchSelection(profileId, instrument, data));
        }
        catch (Exception ex)
        {
            _context.Status($"Could not open the instrument: {ex.Message}");
        }
    }

    /// <summary>Follows the active Trading chart unless it is a blind practice replay.</summary>
    private void FollowActiveChart()
    {
        if (_main.Trading.ActiveTab is not { Model: { } model } tab || model.Settings.BlindReplay || tab.IsReplay) return;
        if (Selection is { } s && ReferenceEquals(s.Data, model.Data) && s.ProfileId == tab.FeedId) return;
        Apply(new WorkbenchSelection(tab.FeedId, model.Instrument, model.Data));
    }

    /// <summary>Points every panel at a selection (scenes use this with fixture data).</summary>
    internal void Apply(WorkbenchSelection selection)
    {
        Selection = selection;
        _picker.SetCurrent(selection.ProfileId, selection.Instrument);
        string connection = _svc.Connections.Find(selection.ProfileId)?.Name ?? selection.Data.Feed.DisplayName;
        _selectionText.Text = $"{selection.Instrument.Symbol} · {connection}";
        foreach (var panel in _panels) panel.Follow(selection);
        UpdateActivity();
    }

    // ===================================================================== page lifecycle

    public void OnShown()
    {
        if (_context.Settings.FollowActiveChart || Selection == null) FollowActiveChart();
        UpdateActivity();
        Tick();
    }

    public void OnTimer()
    {
        if (_svc.IsShuttingDown) return;
        if (_context.Settings.FollowActiveChart && (IsEffectivelyVisible || _windows.Count > 0)) FollowActiveChart();
        UpdateActivity();
        Tick();
    }

    private void Tick()
    {
        var now = _context.Clock();
        foreach (var panel in _panels)
            if (panel.IsActive)
            {
                try { panel.Tick(now); }
                catch (Exception ex) { CrashLog.Write(ex); }
            }
    }

    private void UpdateActivity()
    {
        bool pageVisible = IsEffectivelyVisible && !_svc.IsShuttingDown;
        foreach (var panel in _panels)
            panel.SetActive(!_svc.IsShuttingDown && (_windows.ContainsKey(panel.Key) || pageVisible && panel.Key == _selected));
    }

    // ===================================================================== detached windows

    private static string WindowKey(string key) => "workbench-" + key;

    internal void Detach(string key)
    {
        var panel = _panels.FirstOrDefault(p => p.Key == key);
        if (panel == null || _svc.IsShuttingDown) return;
        if (_windows.TryGetValue(key, out var open))
        {
            open.Activate();
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (ReferenceEquals(_host.Content, panel)) _host.Content = null;
        var st = WindowState(key);
        var window = new Window
        {
            // Panel titles and the page name are separate display texts joined by the " · " separator.
            Title = string.Join(" · ", panel.Title, "Workbench"), Width = Math.Clamp(st.Width, 360, 2400), Height = Math.Clamp(st.Height, 300, 1800),
            MinWidth = 360, MinHeight = 280, Background = Ui.Bg1, Content = new Border { Padding = new Thickness(12), Child = panel },
            Icon = AppIcon.Window,
        };
        var pos = new PixelPoint(st.X, st.Y);
        if (st.X >= 0 && window.Screens.All.Any(screen => screen.WorkingArea.Contains(pos)))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = pos;
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Closing += (_, _) => SaveBounds(key, window);
        window.Closed += (_, _) => Reattach(key);
        _windows[key] = window;
        st.Floating = true;
        _svc.Settings.Save();
        if (_selected == key) _host.Content = DetachedNote(panel);
        window.Show(owner);
        UpdateActivity();
        _context.Status(string.Join(" · ", panel.Title, "Opened in a separate window: it follows the Workbench instrument and returns to the page when closed."));
    }

    internal void Reattach(string key)
    {
        if (!_windows.Remove(key, out var window)) return;
        if (window.Content is Border border) border.Child = null;
        window.Content = null;
        if (!_closingAll)
        {
            WindowState(key).Floating = false;
            _svc.Settings.Save();
        }
        if (window.IsVisible) window.Close();
        if (_selected == key) Select(key);
        UpdateActivity();
    }

    private PanelWindowState WindowState(string key)
    {
        var all = _svc.Settings.PanelWindows;
        if (all.TryGetValue(WindowKey(key), out var st)) return st;
        st = new PanelWindowState { Width = 960, Height = 680 };
        all[WindowKey(key)] = st;
        return st;
    }

    private void SaveBounds(string key, Window window)
    {
        var st = WindowState(key);
        st.X = window.Position.X;
        st.Y = window.Position.Y;
        if (window.Width > 100) st.Width = window.Width;
        if (window.Height > 100) st.Height = window.Height;
        _svc.Settings.Save();
    }

    /// <summary>Reopens the panels that were detached at the last quit (called once the page exists).</summary>
    internal void RestoreWindows()
    {
        foreach (var panel in _panels)
            if (WindowState(panel.Key).Floating && !_windows.ContainsKey(panel.Key))
            {
                var key = panel.Key;
                Dispatcher.UIThread.Post(() => Detach(key), DispatcherPriority.Background);
            }
    }

    /// <summary>At quit: closes the panel windows (remembering they were detached) and releases every panel's subscriptions.</summary>
    internal void CloseForShutdown()
    {
        _closingAll = true;
        foreach (var window in _windows.Values.ToList()) window.Close();
        foreach (var panel in _panels) panel.Dispose();
    }
}
