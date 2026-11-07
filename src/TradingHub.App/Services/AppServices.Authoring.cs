namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    /// <summary>Navigation only: opens the full local source/egress preview, never sends a model request.</summary>
    public Action<PineImportSource>? ReviewPineSource { get; set; }

    public OwnedOperationRegistry PineHandoffs { get; } = new();

    /// <summary>True shutdown only. Await while the UI dispatcher is alive; tray hiding must not call this.</summary>
    public Task ShutdownAuthoringAsync() => PineHandoffs.StopAsync();
}
