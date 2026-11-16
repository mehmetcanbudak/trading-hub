namespace TradingHub.App.Services;

public sealed partial class AppSettings
{
    /// <summary>Opt-in, read-only loopback market-data API for the user's own local tools (off by default).</summary>
    public bool LocalApiEnabled { get; set; }

    /// <summary>Loopback TCP port of the local market-data API (clamped to 1024..65535 when applied).</summary>
    public int LocalApiPort { get; set; } = 48231;

    /// <summary>Browser origins (scheme://host[:port]) allowed to call the local API; requests from any other site are refused.</summary>
    public List<string> LocalApiAllowedOrigins { get; set; } = new();
}
