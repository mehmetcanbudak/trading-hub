namespace TradingHub.Common;

/// <summary>
/// System fonts: use macOS families on Mac and installed Windows alternatives on Windows.
/// </summary>
public static class PlatformFonts
{
    /// <summary>UI monospace fallbacks, in preference order.</summary>
    public static string Mono { get; } = OperatingSystem.IsWindows() ? "Cascadia Mono, Consolas, monospace" : "Menlo, SF Mono, monospace";

    /// <summary>One monospace family for Skia, which does not accept a fallback list.</summary>
    public static string MonoSkia { get; } = OperatingSystem.IsWindows() ? "Consolas" : "Menlo";

    /// <summary>System sans-serif family for charts and reports.</summary>
    public static string SansSkia { get; } = OperatingSystem.IsWindows() ? "Segoe UI" : "Helvetica Neue";
}
