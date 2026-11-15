using TradingHub.Feeds.Binance;
using TradingHub.Feeds.External;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    private readonly List<IDisposable> _externalOwned = new();
    private Action? _externalApply;

    /// <summary>
    /// Shared data for the options and macro studies, under the configured proxy policy. Options and ETF flows stay
    /// unconfigured (no requests) until the user enables a connection; Polymarket and comparison candles are public and
    /// are requested only by a study the user adds.
    /// </summary>
    private void InitializeExternalData(Func<TradingHub.Networking.ProxyHandlerFactory> networkFactory)
    {
        DisposeExternalData();
        var proxy = networkFactory();
        OptionsData.Shared = new OptionsData(Path.Combine(AppPaths.MarketRoot, "Options"));
        EtfFlows.Shared = new EtfFlows();
        var polyHttp = proxy.CreateHttpClient(TimeSpan.FromSeconds(25));
        _externalOwned.Add(polyHttp);
        PolymarketOdds.Shared = new PolymarketOdds(new PolymarketClient(polyHttp, EnvUri("TRADINGHUB_POLYMARKET_GAMMA"), EnvUri("TRADINGHUB_POLYMARKET_CLOB")));
        var feeds = new Dictionary<bool, BinanceFeed>();
        ComparisonKlines.Shared = new ComparisonKlines(futures =>
        {
            lock (feeds)
            {
                if (feeds.TryGetValue(futures, out var feed)) return feed;
                var p = networkFactory();
                feed = new BinanceFeed(futures: futures, httpClient: p.CreateHttpClient(TimeSpan.FromMinutes(2)), configureSocket: p.Configure, ownsHttpClient: true);
                feeds[futures] = feed;
                lock (_externalOwned) _externalOwned.Add(feed);
                return feed;
            }
        });
        _externalApply = () => Connections.ApplyExternalData(OptionsData.Shared, EtfFlows.Shared);
        _externalApply();
        Connections.Changed += _externalApply;
    }

    private static Uri? EnvUri(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } u ? new Uri(u) : null;

    private void DisposeExternalData()
    {
        if (_externalApply != null) Connections.Changed -= _externalApply;
        _externalApply = null;
        Connections.DisposeExternalData();
        OptionsData.Shared = null;
        EtfFlows.Shared = null;
        PolymarketOdds.Shared = null;
        ComparisonKlines.Shared = null;
        lock (_externalOwned)
        {
            foreach (var d in _externalOwned) d.Dispose();
            _externalOwned.Clear();
        }
    }
}
