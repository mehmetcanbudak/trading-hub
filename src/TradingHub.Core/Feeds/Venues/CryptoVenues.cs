using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;

namespace TradingHub.Feeds.Venues;

/// <summary>Every public, credential-free crypto market that can be a connection or an aggregate constituent.</summary>
public static class CryptoVenues
{
    /// <summary>
    /// A selectable market. <see cref="Create"/> builds the public venue feeds defined here; Binance and Bybit feeds
    /// are created by their connection owners (shared proxy, credentials and environment policy) and have no factory.
    /// </summary>
    public sealed record Entry(string Id, string Name, string Venue, bool IsSpot, Func<PublicVenueTransport?, PublicVenueFeed>? Create);

    public static readonly IReadOnlyList<Entry> All = new Entry[]
    {
        new(BinanceFeed.FuturesId, "Binance Futures", "Binance", false, null),
        new(BybitFeed.LinearId, "Bybit", "Bybit", false, null),
        new(OkxFeed.SwapId, "OKX", "OKX", false, t => new OkxFeed(false, t)),
        new(BitgetFeed.FuturesId, "Bitget", "Bitget", false, t => new BitgetFeed(false, t)),
        new(HyperliquidFeed.PerpId, "Hyperliquid", "Hyperliquid", false, t => new HyperliquidFeed(t)),
        new(DeribitFeed.PerpId, "Deribit", "Deribit", false, t => new DeribitFeed(t)),
        new(MexcFeed.PerpId, "MEXC", "MEXC", false, t => new MexcFeed(t)),
        new(BinanceFeed.SpotId, "Binance Spot", "Binance", true, null),
        new(CoinbaseFeed.SpotId, "Coinbase", "Coinbase", true, t => new CoinbaseFeed(t)),
        new(BybitFeed.SpotId, "Bybit Spot", "Bybit", true, null),
        new(OkxFeed.SpotId, "OKX Spot", "OKX", true, t => new OkxFeed(true, t)),
        new(BitgetFeed.SpotId, "Bitget Spot", "Bitget", true, t => new BitgetFeed(true, t)),
    };

    /// <summary>The public venue feeds that can be a standalone connection (Binance and Bybit have their own).</summary>
    public static IEnumerable<Entry> Standalone => All.Where(e => e.Create != null);

    public static Entry? Find(string id) => All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The previous fixed aggregate, kept as the default so saved aggregate connections keep their data scope.</summary>
    public static readonly IReadOnlyList<string> DefaultAggregate = new[] { BinanceFeed.FuturesId, BybitFeed.LinearId };

    /// <summary>A broad suggested set: the large perpetual venues plus the two largest spot venues.</summary>
    public static readonly IReadOnlyList<string> Recommended = new[]
    {
        BinanceFeed.FuturesId, BybitFeed.LinearId, OkxFeed.SwapId, BitgetFeed.FuturesId, HyperliquidFeed.PerpId,
        DeribitFeed.PerpId, BinanceFeed.SpotId, CoinbaseFeed.SpotId,
    };
}
