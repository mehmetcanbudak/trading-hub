namespace TradingHub.App.Services;

public sealed partial class AppSettings
{
    /// <summary>How the user arranged the indicator library: group order, custom group names, item order, collapsed groups.</summary>
    public IndicatorLibraryLayout IndicatorLibrary { get; set; } = new();
}
