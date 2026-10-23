using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;

namespace TradingHub.App.Windows;

/// <summary>Base de los diálogos modales con el estilo de Trading Hub.</summary>
internal abstract class TradingHubDialog<T> : Window
{
    protected TradingHubDialog(string title, double width)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg2;
        ShowInTaskbar = false;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(default(T));
        };
    }

    protected Control Shell(Control body, params Button[] buttons)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) bar.Children.Add(b);
        return new StackPanel { Margin = new Thickness(20, 18, 20, 16), Spacing = 18, Children = { body, bar } };
    }
}

/// <summary>
/// Confirmation question (yes/no). Danger and paid actions default to Cancel: Enter and Esc cancel, only a click or
/// ⌘Enter confirms. Ordinary questions keep the action as the default button.
/// </summary>
internal sealed class ConfirmDialog : TradingHubDialog<bool>
{
    private ConfirmDialog(string title, string message, string okText, bool danger, bool cancelByDefault) : base(title, 420)
    {
        var icon = Ui.Icon(danger ? MaterialIconKind.AlertOutline : MaterialIconKind.HelpCircleOutline, 26, danger ? Ui.Warn : Ui.Accent);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var text = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h2" } },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
            },
        };
        var body = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        icon.Margin = new Thickness(0, 2, 14, 0);
        body.Children.Add(icon);
        body.Children.Add(text);
        bool safe = danger || cancelByDefault;
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = safe };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = okText, IsDefault = !safe }.Classes(danger ? "danger" : "primary");
        ok.Click += (_, _) => Close(true);
        if (safe)
        {
            ToolTip.SetTip(ok, "Click or press ⌘Enter to confirm");
            // in the tunnel phase, before any button sees it (a focused action button would take a plain Enter as a click)
            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Close(e.KeyModifiers.HasFlag(KeyModifiers.Meta));
            }, RoutingStrategies.Tunnel);
        }

        Content = Shell(body, cancel, ok);
    }

    /// <param name="cancelByDefault">Cancel is the default even when the action is not danger-styled (a paid download).</param>
    public static Task<bool> ShowAsync(Window owner, string title, string message, string okText = "OK", bool danger = false, bool cancelByDefault = false) =>
        new ConfirmDialog(title, message, okText, danger, cancelByDefault).ShowDialog<bool>(owner);
}

/// <summary>Pide un texto (nombre de una estrategia, perfil, etc.).</summary>
internal sealed class PromptDialog : TradingHubDialog<string?>
{
    private PromptDialog(string title, string label, string initial, string okText) : base(title, 400)
    {
        var box = new TextBox { Text = initial };
        box.AttachedToVisualTree += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var ok = new Button { Content = okText, IsDefault = true }.Classes("primary");
        ok.Click += (_, _) => Close(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
        Content = Shell(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = title, Classes = { "h2" } }, Ui.Field(label, box) } }, cancel, ok);
    }

    public static Task<string?> ShowAsync(Window owner, string title, string label, string initial = "", string okText = "OK") =>
        new PromptDialog(title, label, initial, okText).ShowDialog<string?>(owner);
}
