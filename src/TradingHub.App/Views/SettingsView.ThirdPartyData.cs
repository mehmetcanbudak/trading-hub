using Avalonia.Controls;
using Material.Icons;

namespace TradingHub.App.Views;

internal sealed partial class SettingsView
{
    private Control BuildThirdPartyDataSettings()
    {
        var settings = _svc.Settings;
        var levels = Switch(settings.ThirdPartyLiquidationLevels, value =>
        {
            settings.ThirdPartyLiquidationLevels = value;
            _svc.ApplyThirdPartyData();
        });
        levels.Name = "ThirdPartyLiquidationLevels";
        return Section("Third-party data", MaterialIconKind.CloudDownloadOutline,
            Setting("Third-party liquidation levels (estimate)",
                "Off by default. When on, the Liquidation levels study downloads estimated Hyperliquid BTC liquidation levels (HyperTracker position snapshots, binned by levels.hiverust.dev) about once a minute. No account data or credentials are sent; the service sees your IP address. The data is a third-party estimate, not exchange data.",
                levels));
    }
}
