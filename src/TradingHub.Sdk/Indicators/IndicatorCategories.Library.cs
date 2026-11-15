namespace TradingHub.Indicators
{
    /// <summary>
    /// Indicator library groups. A study joins a group through its ordinary category attribute, for example
    /// <c>[Category(IndicatorCategories.Vwap)]</c>; nothing else needs registering. Older categories
    /// (<see cref="VolumeOrderFlow"/>, <see cref="Trend"/>, <see cref="Levels"/>) map to their nearest group, and any other
    /// category becomes its own group after these. The app may also assign a built-in type explicitly
    /// (TradingHub.App.Services.IndicatorLibrary.DefaultGroups) when its category is broader than its group.
    /// Group ids are these English strings: they are persisted in the user's library layout, so never rename one.
    /// </summary>
    public static partial class IndicatorCategories
    {
        public const string MovingAverages = "Moving averages";
        public const string TrendStrength = "Trend and strength";
        public const string Volatility = "Volatility and channels";
        public const string Volume = "Volume";
        public const string OrderFlow = "Order flow (delta, CVD, footprint)";
        public const string VolumeProfile = "Volume profile";
        public const string Vwap = "VWAP";
        public const string Derivatives = "Derivatives (open interest, liquidations)";
        public const string Options = "Options";
        public const string Structure = "Market structure and key levels";
        public const string Patterns = "Candles and patterns";
        public const string Sessions = "Sessions";
        public const string Macro = "Macro and other tools";

        /// <summary>Default order of the library groups (custom indicators are listed first, unknown categories last).</summary>
        public static IReadOnlyList<string> LibraryOrder { get; } = new[]
        {
            MovingAverages, TrendStrength, Oscillators, Volatility, Volume, OrderFlow, VolumeProfile, Vwap, OrderBook, Derivatives,
            Options, Structure, Patterns, Sessions, Macro,
        };

        /// <summary>The library group of a category: a group itself, a legacy category's nearest group, or the category as given.</summary>
        public static string LibraryGroup(string category) => category switch
        {
            VolumeOrderFlow => OrderFlow,
            Trend => TrendStrength,
            Levels => Structure,
            _ => category,
        };
    }
}
