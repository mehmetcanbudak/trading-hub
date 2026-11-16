namespace TradingHub.Feeds.Binance;

public sealed partial class BinanceFeed : Venues.ICryptoVenue
{
    public string Venue => "Binance";
    public bool IsSpot => !_futures;
}
