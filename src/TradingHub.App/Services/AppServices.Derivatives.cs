using TradingHub.Feeds.Derivatives;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    private Func<TradingHub.Networking.ProxyHandlerFactory>? _derivativesNetwork;

    /// <summary>Public funding, open-interest and liquidation data for the derivatives pane, under the configured proxy policy.</summary>
    private void InitializeDerivatives(Func<TradingHub.Networking.ProxyHandlerFactory> networkFactory)
    {
        _derivativesNetwork = networkFactory;
        var proxy = networkFactory();
        DerivativesData.Shared?.Dispose();
        DerivativesData.Shared = new DerivativesData(
            new DerivativesTransport(proxy.CreateHttpClient(TimeSpan.FromSeconds(20)), OwnsHttp: true, ConfigureSocket: proxy.Configure),
            Path.Combine(AppPaths.MarketRoot, "Derivatives"));
        ApplyThirdPartyData();
    }

    /// <summary>
    /// Creates the third-party liquidation-level client only while the user has opted in; turning the setting off
    /// disposes it, so no study can reach that service.
    /// </summary>
    internal void ApplyThirdPartyData()
    {
        var current = LiquidationLevelsService.Shared;
        if (Settings.ThirdPartyLiquidationLevels && _derivativesNetwork != null)
        {
            if (current != null) return;
            var proxy = _derivativesNetwork();
            LiquidationLevelsService.Shared = new LiquidationLevelsService(proxy.CreateHttpClient(TimeSpan.FromSeconds(20)), ownsHttp: true);
        }
        else
        {
            LiquidationLevelsService.Shared = null;
            current?.Dispose();
        }
    }

    private static void DisposeDerivatives()
    {
        var shared = DerivativesData.Shared;
        DerivativesData.Shared = null;
        shared?.Dispose();
        var levels = LiquidationLevelsService.Shared;
        LiquidationLevelsService.Shared = null;
        levels?.Dispose();
    }
}
