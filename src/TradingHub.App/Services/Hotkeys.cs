using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;

namespace TradingHub.App.Services;

/// <summary>Una acción que se puede disparar con un atajo de teclado.</summary>
public sealed record HotkeyDef(string Id, string Group, string Name, string? DefaultGesture = null);

/// <summary>
/// Atajos de teclado configurables (Ajustes → Atajos de teclado). Los de operar vienen sin asignar a
/// propósito: cada uno elige los suyos. En cuentas reales siguen pidiendo confirmación si está activada.
/// </summary>
public static class Hotkeys
{
    internal static readonly object ChartQuickEntryScope = new();
    public static readonly HotkeyDef[] All =
    {
        new("buy-market", "Trade", "Buy at market"),
        new("sell-market", "Trade", "Sell at market"),
        new("buy-bid", "Trade", "Buy limit at bid"),
        new("sell-ask", "Trade", "Sell limit at ask"),
        new("flatten", "Trade", "Close position"),
        new("reverse", "Trade", "Reverse position"),
        new("cancel-all", "Trade", "Cancel all orders"),
        new("close-25", "Trade", "Close 25%"),
        new("close-50", "Trade", "Close 50%"),
        new("breakeven", "Trade", "Stop to breakeven"),
        new("brackets", "Trade", "Place stop and targets from template"),
        new("qty-next", "Trade", "Quantity: next preset"),
        new("qty-prev", "Trade", "Quantity: previous preset"),

        new("period-m1", "Chart", "1-minute period", "Alt+D1"),
        new("period-m5", "Chart", "5-minute period", "Alt+D2"),
        new("period-m15", "Chart", "15-minute period", "Alt+D3"),
        new("period-h1", "Chart", "1-hour period", "Alt+D4"),
        new("period-h4", "Chart", "4-hour period", "Alt+D5"),
        new("period-d1", "Chart", "Daily period", "Alt+D6"),
        new("style-candles", "Chart", "Candlestick style"),
        new("style-footprint", "Chart", "Footprint style"),
        new("style-heikin", "Chart", "Heikin Ashi style"),
        new("autoscale", "Chart", "Auto scale"),
        new("go-last", "Chart", "Go to latest price"),
        new("tool-hline", "Chart", "Draw horizontal line", "Alt+H"),
        // TradingView-style tool shortcuts; Alt+T stays with the Chart Trader toggle users already rely on
        new("tool-trend", "Chart", "Draw trend line", "Alt+Shift+T"),
        new("tool-hray", "Chart", "Draw horizontal ray", "Alt+J"),
        new("tool-vline", "Chart", "Draw vertical line", "Alt+V"),
        new("tool-cross", "Chart", "Draw cross line", "Alt+C"),
        new("tool-fib", "Chart", "Draw Fibonacci retracement", "Alt+F"),
        new("tool-rect", "Chart", "Draw rectangle", "Alt+Shift+R"),
        new("tool-brush", "Chart", "Draw with the brush", "Alt+B"),
        new("tool-text", "Chart", "Write a text", "Alt+N"),
        new("tool-cursor", "Chart", "Cursor (release tool)"),
        new("reset-view", "Chart", "Reset chart view", "Alt+R"),
        new("rectangle-zoom", "Chart", "Rectangle zoom tool"),
        new("toggle-session-lines", "Chart", "Show or hide session lines"),
        new("chart-timezone", "Chart", "Chart timezone and clock"),
        new("chart-shortcuts", "Chart", "Chart keyboard help"),
        new("chart-quick-entry", "Chart", "Symbol or period quick entry"),

        new("layout-1", "Chart windows", "Single-chart layout"),
        new("layout-4", "Chart windows", "Four-chart layout"),
        new("next-tab", "Chart windows", "Next tab", "Ctrl+Tab"),
        new("prev-tab", "Chart windows", "Previous tab", "Ctrl+Shift+Tab"),
        new("detach", "Chart windows", "Detach chart into a window"),
        new("toggle-dom", "Chart windows", "Show or hide DOM"),
        new("toggle-trader", "Chart windows", "Show or hide chart's Chart Trader", "Alt+T"),
        new("toggle-panel", "Chart windows", "Show or hide right panel"),
    };

    /// <summary>The shortcut id of each drawing tool that has one.</summary>
    public static readonly IReadOnlyDictionary<TradingHub.Charting.Drawings.DrawingKind, string> DrawingTools = new Dictionary<TradingHub.Charting.Drawings.DrawingKind, string>
    {
        [TradingHub.Charting.Drawings.DrawingKind.HorizontalLine] = "tool-hline",
        [TradingHub.Charting.Drawings.DrawingKind.TrendLine] = "tool-trend",
        [TradingHub.Charting.Drawings.DrawingKind.HorizontalRay] = "tool-hray",
        [TradingHub.Charting.Drawings.DrawingKind.VerticalLine] = "tool-vline",
        [TradingHub.Charting.Drawings.DrawingKind.CrossLine] = "tool-cross",
        [TradingHub.Charting.Drawings.DrawingKind.FibRetracement] = "tool-fib",
        [TradingHub.Charting.Drawings.DrawingKind.Rectangle] = "tool-rect",
        [TradingHub.Charting.Drawings.DrawingKind.Brush] = "tool-brush",
        [TradingHub.Charting.Drawings.DrawingKind.Text] = "tool-text",
    };

    /// <summary>
    /// The action's active shortcut. An explicit assignment wins over a default: a default whose combination the user
    /// gave to another action is not active (defaults added in an update must not take over existing bindings).
    /// </summary>
    public static string? GestureOf(AppSettings s, string id)
    {
        if (s.Hotkeys.TryGetValue(id, out var g)) return string.IsNullOrEmpty(g) ? null : g;
        string? fallback = null;
        foreach (var h in All)
            if (h.Id == id)
            {
                fallback = h.DefaultGesture;
                break;
            }

        if (fallback == null) return null;
        foreach (var (other, assigned) in s.Hotkeys)
            if (other != id && SameGesture(assigned, fallback)) return null;
        return fallback;
    }

    /// <summary>True when both texts are the same key combination ("Alt+Shift+T" and "Shift+Alt+T" are).</summary>
    internal static bool SameGesture(string? a, string? b) => Parse(a) is { } x && Parse(b) is { } y && x.Equals(y);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, KeyGesture?> Parsed = new(StringComparer.Ordinal);

    /// <summary>The parsed shortcut, or null when empty or badly saved; each text is parsed once.</summary>
    private static KeyGesture? Parse(string? gesture) =>
        string.IsNullOrEmpty(gesture) ? null : Parsed.GetOrAdd(gesture, static text =>
        {
            try
            {
                return KeyGesture.Parse(text);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return null; // atajo mal guardado: se ignora
            }
        });

    /// <summary>Formats shortcuts using macOS symbols or Windows modifier names.</summary>
    public static string Pretty(string? gesture) => Pretty(gesture, mac: !Os.IsWindows);

    internal static string Pretty(string? gesture, bool mac)
    {
        if (string.IsNullOrEmpty(gesture)) return "—";
        try
        {
            var g = KeyGesture.Parse(gesture);
            string key = g.Key switch
            {
                >= Key.D0 and <= Key.D9 => ((int)(g.Key - Key.D0)).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(g.Key - Key.NumPad0),
                Key.Space => "Space",
                Key.Return => mac ? "↩" : "Enter",
                Key.Tab => "Tab",
                Key.Up => "↑",
                Key.Down => "↓",
                Key.Left => "←",
                Key.Right => "→",
                _ => g.Key.ToString(),
            };
            return Keys.Format(g.KeyModifiers, key, mac);
        }
        catch
        {
            return gesture;
        }
    }

    /// <summary>Atajo a partir de una tecla presionada (null si es solo un modificador).</summary>
    public static string? FromKey(Key key, KeyModifiers mods)
    {
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return null;
        return new KeyGesture(key, mods).ToString();
    }

    /// <summary>Acción que corresponde a la tecla, o null. No interfiere con lo que se escribe en un campo de texto.</summary>
    public static string? Match(AppSettings s, KeyEventArgs e, TopLevel? top)
    {
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        if (top?.FocusManager?.GetFocusedElement() is TradingHub.App.Controls.ChartView { IsTextComposing: true }) return null;
        // A chart-entry popup is navigation-only, even when a trade shortcut has strong modifiers.
        for (Visual? node = top?.FocusManager?.GetFocusedElement() as Visual; node != null; node = node.GetVisualParent())
            if (node is Control control && ReferenceEquals(control.Tag, ChartQuickEntryScope)) return null;
        bool typing = top?.FocusManager?.GetFocusedElement() is TextBox;
        return Match(s, e, typing, mac: OperatingSystem.IsMacOS());
    }

    /// <summary>The matching rule once focus is known.</summary>
    internal static string? Match(AppSettings s, KeyEventArgs e, bool typing, bool mac)
    {
        // Ctrl and Cmd never type text. On macOS Option does (Option+N is the ñ dead key, Option+C types ç), so
        // Option-only shortcuts leave a focused text field alone there.
        bool strongModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 || !mac && e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool functionKey = e.Key is >= Key.F1 and <= Key.F24;
        if (typing && !strongModifier && !functionKey) return null;
        foreach (var h in All)
            if (Parse(GestureOf(s, h.Id)) is { } gesture && gesture.Matches(e))
                return h.Id;
        return null;
    }
}
