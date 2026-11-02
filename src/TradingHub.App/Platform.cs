using Avalonia.Input;

namespace TradingHub.App;

/// <summary>
/// Platform-specific system names used in interface text. Long explanations can branch on <see cref="IsWindows"/>.
/// </summary>
internal static class Os
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();

    /// <summary>The user's device name.</summary>
    public static string Device => IsWindows ? "PC" : "Mac";

    /// <summary>The operating system's private credential storage.</summary>
    public static string SecretStore => IsWindows ? "Windows Credential Manager" : "macOS Keychain";

    /// <summary>Short label for credential fields.</summary>
    public static string SecretStoreShort => IsWindows ? "Windows" : "Keychain";
}

/// <summary>
/// Native command-key shortcuts: macOS uses symbols (⇧⌘D), while Windows uses names (Ctrl+Shift+D).
/// </summary>
internal static class Keys
{
    /// <summary>The native command modifier.</summary>
    public static KeyModifiers Command { get; } = Os.IsWindows ? KeyModifiers.Control : KeyModifiers.Meta;

    /// <summary>A display label for a command-key shortcut.</summary>
    public static string Label(string key, bool shift = false) =>
        Format(Command | (shift ? KeyModifiers.Shift : KeyModifiers.None), key, mac: !Os.IsWindows);

    /// <summary>
    /// Format modifiers in native order: ⌃⌥⇧⌘ on macOS; Ctrl+Alt+Shift+Win on Windows.
    /// </summary>
    public static string Format(KeyModifiers mods, string key, bool mac)
    {
        if (mac)
            return (mods.HasFlag(KeyModifiers.Control) ? "⌃" : "") + (mods.HasFlag(KeyModifiers.Alt) ? "⌥" : "") +
                   (mods.HasFlag(KeyModifiers.Shift) ? "⇧" : "") + (mods.HasFlag(KeyModifiers.Meta) ? "⌘" : "") + key;

        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }
}
