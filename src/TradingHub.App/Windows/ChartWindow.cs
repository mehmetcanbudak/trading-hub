using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Material.Icons;
using TradingHub.App.Views;

namespace TradingHub.App.Windows;

/// <summary>
/// Gráfico en su propia ventana (como en ATAS): se puede llevar a otro monitor. Tiene su barra; las
/// herramientas de dibujo y el Chart Trader viajan con el marco del gráfico. «Volver a la ventana
/// principal» lo devuelve a una pestaña.
/// </summary>
internal sealed class ChartWindow : Window
{
    private readonly TradingView _host;
    private readonly DockPanel _content = new();

    public ChartWindow(TradingView host, ChartTab chart)
    {
        _host = host;
        Chart = chart;
        chart.Window = this;
        Width = 1180;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        Background = Ui.Bg0;
        Icon = AppIcon.Window;

        var dock = Ui.IconButton(MaterialIconKind.DockWindow, "Return to main window", () => _host.DockBack(this));
        Toolbar = new ChartToolbar(host, true, dock) { Chart = chart };
        DockPanel.SetDock(Toolbar, Dock.Top);
        _content.Children.Add(Toolbar);

        // el marco del gráfico se mueve tal cual (sin encabezado: la ventana ya tiene título)
        chart.Header.IsVisible = false;
        chart.Frame.BorderThickness = new Thickness(1);
        chart.Frame.BorderBrush = Ui.Line;
        chart.Frame.Margin = new Thickness(6);
        if (chart.Frame.Parent is Panel old) old.Children.Remove(chart.Frame);
        _content.Children.Add(chart.Frame);
        Content = _content;
        UpdateTitle();

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || App.Services == null) return;
            if (e.Key == Avalonia.Input.Key.Escape && e.KeyModifiers == Avalonia.Input.KeyModifiers.None && Chart.Model?.FocusTrade != null)
            {
                _host.ClearFocusTrade(Chart);
                e.Handled = true;
                return;
            }

            if (TradingHub.App.Services.Hotkeys.Match(App.Services.Settings, e, this) is { } action && _host.ExecuteHotkey(action, Chart)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, Keys.Command), Command = new Relay(Close) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.I, Keys.Command), Command = new Relay(() => _host.OpenIndicators(Chart)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.K, Keys.Command), Command = new Relay(() => Toolbar.Picker.Open()) });
    }

    public ChartTab Chart { get; }
    public ChartToolbar Toolbar { get; }

    /// <summary>True si el gráfico volvió a la ventana principal (al cerrar no se descarta).</summary>
    public bool Released { get; private set; }

    public void UpdateTitle() => Title = $"Trading Hub — {Chart.Caption}";

    /// <summary>Suelta el marco del gráfico para que vuelva a una pestaña.</summary>
    public void ReleaseChart()
    {
        Released = true;
        _content.Children.Remove(Chart.Frame);
    }
}

/// <summary>Comando simple para atajos de teclado.</summary>
internal sealed class Relay : System.Windows.Input.ICommand
{
    private readonly Action _run;
    public Relay(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
}
