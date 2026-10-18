using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

/// <summary>Pregunta de confirmación (Sí/No).</summary>
internal sealed class ConfirmDialog : TradingHubDialog<bool>
{
    private ConfirmDialog(string title, string message, string okText, bool danger) : base(title, 420)
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
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = okText, IsDefault = true }.Classes(danger ? "danger" : "primary");
        ok.Click += (_, _) => Close(true);
        Content = Shell(body, cancel, ok);
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, string okText = "OK", bool danger = false) =>
        new ConfirmDialog(title, message, okText, danger).ShowDialog<bool>(owner);
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
