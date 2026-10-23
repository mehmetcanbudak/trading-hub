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

    private T _answer = default!;

    /// <summary>Closes it with that answer, whether it was shown over a window or on its own.</summary>
    protected void Answer(T answer)
    {
        _answer = answer;
        Close(answer);
    }

    /// <summary>
    /// Shows it over <paramref name="owner"/> while that window is on screen; otherwise on its own, centered and on top (the
    /// main window can be closed to the menu bar). Esc, or closing it, answers the default.
    /// </summary>
    internal Task<T> ShowOverAsync(Window? owner)
    {
        if (owner is { IsVisible: true }) return ShowDialog<T>(owner);
        var answered = new TaskCompletionSource<T>();
        Closed += (_, _) => answered.TrySetResult(_answer);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Show();
        Activate();
        return answered.Task;
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

/// <summary>
/// Quitting while live strategies hold positions: what happens to their orders at the broker. Keep the stops (the default
/// button), cancel them, close the positions (danger style: a click, never Enter) or don't quit (Esc).
/// </summary>
internal sealed class QuitDialog : TradingHubDialog<QuitMode?>
{
    internal QuitDialog(IReadOnlyList<string> strategies) : base("Quit Trading Hub", 500)
    {
        var icon = Ui.Icon(MaterialIconKind.AlertOutline, 26, Ui.Warn);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var list = new StackPanel { Spacing = 4 };
        foreach (var s in strategies) list.Children.Add(new TextBlock { Text = "• " + s, TextWrapping = TextWrapping.Wrap });
        var choices = new StackPanel { Spacing = 2 };
        foreach (var line in new[]
                 {
                     "Keep stops: their stop loss and take profit stay working at the broker while Trading Hub is closed; each strategy takes them over when it resumes.",
                     "Cancel stops: the positions stay open without them.",
                     "Close positions: market orders close them now.",
                 })
            choices.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } });
        var text = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Quit Trading Hub", Classes = { "h2" } },
                new TextBlock { Text = strategies.Count == 1 ? "This live strategy holds an open position:" : "These live strategies hold open positions:", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                list,
                choices,
            },
        };
        var body = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        icon.Margin = new Thickness(0, 2, 14, 0);
        body.Children.Add(icon);
        body.Children.Add(text);

        var keep = Choice("Keep stops at broker and quit", QuitMode.KeepStops, "primary");
        keep.IsDefault = true;
        var cancelStops = Choice("Cancel stops and quit", QuitMode.CancelStops, null);
        var close = Choice("Close positions and quit", QuitMode.ClosePositions, "danger");
        ToolTip.SetTip(close, "Click to close the positions at market (Enter never chooses this)");
        var stay = Choice("Don't quit", null, null);
        stay.IsCancel = true;
        // in the tunnel phase, before a focused button sees it: a plain Enter never closes the positions
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && (ReferenceEquals(e.Source, close) || close.IsFocused)) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Content = new StackPanel { Margin = new Thickness(20, 18, 20, 16), Spacing = 18, Children = { body, new StackPanel { Spacing = 8, Children = { keep, cancelStops, close, stay } } } };
    }

    private Button Choice(string text, QuitMode? mode, string? style)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (style != null) button.Classes.Add(style);
        button.Click += (_, _) => Answer(mode);
        return button;
    }
}

/// <summary>A message with one button: it only informs.</summary>
internal sealed class NoticeDialog : TradingHubDialog<bool>
{
    private NoticeDialog(string title, string message, string okText) : base(title, 460)
    {
        var icon = Ui.Icon(MaterialIconKind.AlertOutline, 26, Ui.Warn);
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
        var ok = new Button { Content = okText, IsDefault = true, IsCancel = true }.Classes("primary");
        ok.Click += (_, _) => Answer(true);
        Content = Shell(body, ok);
    }

    /// <summary>Over <paramref name="owner"/> while it is on screen, otherwise on its own (see <see cref="TradingHubDialog{T}.ShowOverAsync"/>).</summary>
    public static Task ShowAsync(Window? owner, string title, string message, string okText = "OK") =>
        new NoticeDialog(title, message, okText).ShowOverAsync(owner);
}
