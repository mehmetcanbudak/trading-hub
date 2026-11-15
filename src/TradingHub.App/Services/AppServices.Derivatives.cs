using TradingHub.Feeds.Derivatives;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    /// <summary>Public funding, open-interest and liquidation data for the derivatives pane, under the configured proxy policy.</summary>
    private void InitializeDerivatives(Func<TradingHub.Networking.ProxyHandlerFactory> networkFactory)
    {
        var proxy = networkFactory();
        DerivativesData.Shared?.Dispose();
        DerivativesData.Shared = new DerivativesData(
            new DerivativesTransport(proxy.CreateHttpClient(TimeSpan.FromSeconds(20)), OwnsHttp: true, ConfigureSocket: proxy.Configure),
            Path.Combine(AppPaths.MarketRoot, "Derivatives"));
    }

    private static void DisposeDerivatives()
    {
        var shared = DerivativesData.Shared;
        DerivativesData.Shared = null;
        shared?.Dispose();
    }
}
