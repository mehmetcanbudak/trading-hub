namespace TradingHub.App.Services;

public sealed partial class AppSettings
{
    /// <summary>Opt-in: fetch third-party estimated liquidation levels (off by default; listed in the privacy guide).</summary>
    public bool ThirdPartyLiquidationLevels { get; set; }
}
