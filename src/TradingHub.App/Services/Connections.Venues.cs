using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.Venues;

namespace TradingHub.App.Services;

/// <summary>Which constituent markets an aggregate crypto connection keeps.</summary>
public enum AggregateMarkets
{
    [Display(Name = "Spot and perpetual")] Both,
    [Display(Name = "Spot only")] Spot,
    [Display(Name = "Perpetual only")] Perpetual,
}

public sealed partial class ConnectionProfile
{
    /// <summary>Public crypto connection: the venue market id (okx-swap, coinbase…).</summary>
    public string PublicVenueId { get; set; } = OkxFeed.SwapId;

    /// <summary>Aggregate crypto connection: constituent market ids, first one first (it anchors price and the default DOM).</summary>
    public List<string> AggregateSources { get; set; } = CryptoVenues.DefaultAggregate.ToList();

    public AggregateMarkets AggregateMarkets { get; set; } = AggregateMarkets.Both;

    /// <summary>First venue's book (default, unchanged behavior) or a composite of every available venue book.</summary>
    public AggregateBookMode AggregateBook { get; set; } = AggregateBookMode.FirstVenue;

    /// <summary>Known, distinct constituents after the spot/perpetual filter, in configured order.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveAggregateSources => AggregateSources
        .Where(id => CryptoVenues.Find(id) is { } entry && AggregateMarkets switch
        {
            AggregateMarkets.Spot => entry.IsSpot,
            AggregateMarkets.Perpetual => !entry.IsSpot,
            _ => true,
        })
        .Select(id => CryptoVenues.Find(id)!.Id)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public sealed partial class ConnectionManager
{
    private PublicVenueTransport VenueTransport()
    {
        var proxy = _networkFactory();
        // Isolated UI runs point every public venue at one fake origin; production uses each venue's own host.
        string? origin = Environment.GetEnvironmentVariable("TRADINGHUB_VENUE_REST");
        return new PublicVenueTransport(proxy.CreateHttpClient(TimeSpan.FromSeconds(20)), OwnsHttp: true, ConfigureSocket: proxy.Configure,
            RestOrigin: string.IsNullOrWhiteSpace(origin) ? null : new Uri(origin));
    }

    private PublicVenueFeed PublicVenueFeedOf(string id) => CryptoVenues.Find(id) is { Create: { } create }
        ? create(VenueTransport())
        : throw new NotSupportedException($"Unknown public crypto market: {id}.");

    private IMarketDataFeed AggregateSourceOf(string id)
    {
        var proxy = _networkFactory();
        return id switch
        {
            BinanceFeed.FuturesId => BinanceFeedOf(futures: true),
            BinanceFeed.SpotId => BinanceFeedOf(futures: false),
            BybitFeed.LinearId or BybitFeed.SpotId => new BybitFeed(proxy.CreateHttpClient(TimeSpan.FromSeconds(20)), configureSocket: proxy.Configure,
                ownsHttpClient: true, feedId: id),
            _ => PublicVenueFeedOf(id),
        };
    }

    /// <summary>The aggregate owns its constituent feeds; a filter that leaves no market is an explicit configuration error.</summary>
    private AggregateFeed AggregateFeedOf(ConnectionProfile p)
    {
        var ids = p.EffectiveAggregateSources;
        if (ids.Count == 0) throw new NotSupportedException("No selected aggregate market matches the spot/perpetual filter.");
        var sources = new List<IMarketDataFeed>();
        try
        {
            foreach (var id in ids) sources.Add(AggregateSourceOf(id));
            return new AggregateFeed(sources, ownsSources: true, bookMode: p.AggregateBook);
        }
        catch
        {
            foreach (var source in sources) source.Dispose();
            throw;
        }
    }
}
