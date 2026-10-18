using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;

namespace TradingHub.App.Views;

/// <summary>
/// Chart Trader, DOM y cinta en ventanas aparte (flotantes): siguen al gráfico activo, recuerdan dónde estaban y al
/// cerrar la ventana vuelven al panel derecho.
/// </summary>
public sealed partial class TradingView
{
    private readonly Dictionary<string, Window> _panelWindows = new();
    private readonly Dictionary<string, Button> _detachButtons = new();
    private bool _closingAll;

    internal bool DomIsFloating => _panelWindows.ContainsKey("dom");

    /// <summary>Hay alguna sección en su ventana (se actualiza aunque la ventana principal esté en otra sección).</summary>
    private bool AnyPanelFloating => _panelWindows.Count > 0;

    private static string PanelName(string key) => key switch
    {
        "dom" => "DOM",
        "tape" => "Trade tape",
        _ => "Chart Trader",
    };

    private Control? SectionOf(string key) => key switch
    {
        "dom" => _domSection,
        "tape" => _tapeSection,
        _ => _ticketSection,
    };

    private Button BuildDetachButton(string key)
    {
        var b = Ui.IconButton(MaterialIconKind.OpenInNew, $"Detach {Article(key)} into a separate window", () =>
        {
            if (_panelWindows.ContainsKey(key)) DockSection(key);
            else DetachSection(key);
        }, 15);
        _detachButtons[key] = b;
        return b;
    }

    private static string Article(string key) => key == "tape" ? "trade tape" : key == "dom" ? "DOM" : "Chart Trader";

    private void UpdateDetachButton(string key)
    {
        if (!_detachButtons.TryGetValue(key, out var b)) return;
        bool floating = _panelWindows.ContainsKey(key);
        b.Content = Ui.Icon(floating ? MaterialIconKind.DockRight : MaterialIconKind.OpenInNew, 15);
        ToolTip.SetTip(b, floating ? $"Return {Article(key)} in the right panel" : $"Detach {Article(key)} into a separate window");
    }

    /// <summary>Dónde y de qué tamaño estaba la ventana de una sección (el DOM trae lo de la versión anterior).</summary>
    private PanelWindowState WindowStateOf(string key)
    {
        var s = _svc.Settings;
        if (s.PanelWindows.TryGetValue(key, out var st)) return st;
        st = key == "dom"
            ? new PanelWindowState { Floating = s.DomFloating, X = s.DomWindowX, Y = s.DomWindowY, Width = s.DomWindowWidth, Height = s.DomWindowHeight }
            : new PanelWindowState { Width = key == "tape" ? 340 : 300, Height = key == "tape" ? 620 : 860 };
        s.PanelWindows[key] = st;
        return st;
    }

    /// <summary>Saca una sección a una ventana aparte (o la trae al frente si ya está afuera).</summary>
    internal void DetachSection(string key)
    {
        var section = SectionOf(key);
        if (section == null) return;
        if (_panelWindows.TryGetValue(key, out var open))
        {
            open.Activate();
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var s = _svc.Settings;
        switch (key)
        {
            case "dom": s.ShowDom = true; break;
            case "tape": s.ShowTape = true; break;
            default: s.ShowTicket = true; break;
        }

        if (section.Parent is Panel parent) parent.Children.Remove(section);
        var st = WindowStateOf(key);
        var w = new Window
        {
            Title = PanelTitle(key),
            Width = Math.Clamp(st.Width, 240, 1400),
            Height = Math.Clamp(st.Height, 240, 2400),
            MinWidth = 220,
            MinHeight = 200,
            Background = Ui.Bg1,
            Content = section,
            Icon = TradingHub.App.Windows.AppIcon.Bitmap is { } bmp ? new WindowIcon(bmp) : null,
        };
        var pos = new PixelPoint(st.X, st.Y);
        if (st.X >= 0 && w.Screens.All.Any(sc => sc.WorkingArea.Contains(pos)))
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = pos;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        w.Closing += (_, _) => SaveWindowBounds(key, w);
        w.Closed += (_, _) => DockSection(key);
        _panelWindows[key] = w;
        st.Floating = true;
        if (key == "dom") s.DomFloating = true;
        s.Save();
        UpdateDetachButton(key);
        ApplyPanelVisibility();
        w.Show(owner);
        _status($"{PanelName(key)} in a separate window: follows the active chart and returns to the right panel when closed.");
    }

    private void SaveWindowBounds(string key, Window w)
    {
        var st = WindowStateOf(key);
        st.X = w.Position.X;
        st.Y = w.Position.Y;
        if (w.Width > 100) st.Width = w.Width;
        if (w.Height > 100) st.Height = w.Height;
        _svc.Settings.Save();
    }

    /// <summary>Devuelve una sección al panel derecho.</summary>
    internal void DockSection(string key)
    {
        if (!_panelWindows.Remove(key, out var w)) return;
        w.Content = null;
        if (!_closingAll)
        {
            WindowStateOf(key).Floating = false;
            if (key == "dom") _svc.Settings.DomFloating = false;
            _svc.Settings.Save();
        }

        UpdateDetachButton(key);
        ApplyPanelVisibility();
        if (w.IsVisible) w.Close();
    }

    internal Window? PanelWindow(string key) => _panelWindows.GetValueOrDefault(key);

    internal void DetachDom() => DetachSection("dom");

    internal void DockDom() => DockSection("dom");

    private string PanelTitle(string key) => _active != null ? $"{PanelName(key)} · {_active.Symbol}" : PanelName(key);

    private void UpdatePanelTitles()
    {
        foreach (var (key, w) in _panelWindows) w.Title = PanelTitle(key);
    }

    /// <summary>Al abrir la app: las secciones que estaban afuera vuelven a su ventana.</summary>
    private void RestorePanelWindows()
    {
        // ahora cada gráfico tiene su Chart Trader: la primera vez se oculta el ticket general (vuelve desde Paneles)
        if (!_svc.Settings.ChartTraderIntroduced)
        {
            _svc.Settings.ChartTraderIntroduced = true;
            _svc.Settings.ShowTicket = false;
            _svc.Settings.Save();
            ApplyPanelVisibility();
        }

        foreach (var key in new[] { "ticket", "dom", "tape" })
            if (WindowStateOf(key).Floating && PanelOn(key) && !_panelWindows.ContainsKey(key))
            {
                var k = key;
                Dispatcher.UIThread.Post(() => DetachSection(k), DispatcherPriority.Background);
            }
    }

    /// <summary>Al cerrar la app: cierra las ventanas sin olvidar que estaban afuera.</summary>
    private void ClosePanelWindowsForShutdown()
    {
        _closingAll = true;
        foreach (var w in _panelWindows.Values.ToList()) w.Close();
    }
}
