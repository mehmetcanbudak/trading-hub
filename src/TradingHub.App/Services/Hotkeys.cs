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
        new("tool-cursor", "Chart", "Cursor (release tool)"),

        new("layout-1", "Windows", "Single-chart layout"),
        new("layout-4", "Windows", "Four-chart layout"),
        new("next-tab", "Windows", "Next tab", "Ctrl+Tab"),
        new("prev-tab", "Windows", "Previous tab", "Ctrl+Shift+Tab"),
        new("detach", "Windows", "Detach chart into a window"),
        new("toggle-dom", "Windows", "Show or hide DOM"),
        new("toggle-trader", "Windows", "Show or hide chart's Chart Trader", "Alt+T"),
        new("toggle-panel", "Windows", "Show or hide right panel"),
    };

    public static string? GestureOf(AppSettings s, string id) =>
        s.Hotkeys.TryGetValue(id, out var g) ? (string.IsNullOrEmpty(g) ? null : g) : All.FirstOrDefault(h => h.Id == id)?.DefaultGesture;

    /// <summary>Texto lindo para mostrar un atajo (⌥1, ⇧B, F5…).</summary>
    public static string Pretty(string? gesture)
    {
        if (string.IsNullOrEmpty(gesture)) return "—";
        try
        {
            var g = KeyGesture.Parse(gesture);
            var parts = new List<string>();
            if (g.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("⌃");
            if (g.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("⌥");
            if (g.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("⇧");
            if (g.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("⌘");
            string key = g.Key switch
            {
                >= Key.D0 and <= Key.D9 => ((int)(g.Key - Key.D0)).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(g.Key - Key.NumPad0),
                Key.Space => "Spacer",
                Key.Return => "↩",
                Key.Tab => "Tab",
                Key.Up => "↑",
                Key.Down => "↓",
                Key.Left => "←",
                Key.Right => "→",
                _ => g.Key.ToString(),
            };
            return string.Concat(parts) + key;
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
        bool typing = top?.FocusManager?.GetFocusedElement() is TextBox;
        bool strongModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        bool functionKey = e.Key is >= Key.F1 and <= Key.F24;
        if (typing && !strongModifier && !functionKey) return null;
        foreach (var h in All)
        {
            var g = GestureOf(s, h.Id);
            if (g == null) continue;
            try
            {
                if (KeyGesture.Parse(g).Matches(e)) return h.Id;
            }
            catch
            {
                // atajo mal guardado: se ignora
            }
        }

        return null;
    }
}
