using System.Globalization;

namespace TradingHub.Common;

/// <summary>Display-only date and number formatting, selected before the UI or MCP services start.</summary>
public static class UiCulture
{
    public static CultureInfo Current { get; set; } = For("en");

    public static CultureInfo For(string? language) => CultureInfo.GetCultureInfo(language switch
    {
        "es" => "es-AR",
        "zh-CN" => "zh-CN",
        _ => "en-US",
    });
}
