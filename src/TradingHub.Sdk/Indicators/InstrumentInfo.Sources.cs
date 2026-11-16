namespace TradingHub.Indicators
{
    /// <summary>
    /// One market feeding a chart instrument: the trade provenance identity it stamps, its display name, its exchange
    /// (Binance Futures and Binance Spot are both "Binance") and whether it is a spot market.
    /// </summary>
    public sealed record InstrumentSource(string Identity, string Name, string Venue, bool IsSpot);

    public partial class InstrumentInfo
    {
        public string BaseAsset { get; set; } = "";
        public string QuoteAsset { get; set; } = "";

        /// <summary>Markets forming the instrument: every constituent of an aggregate, otherwise the single feed.</summary>
        public IReadOnlyList<InstrumentSource> Sources { get; set; } = System.Array.Empty<InstrumentSource>();
    }
}
