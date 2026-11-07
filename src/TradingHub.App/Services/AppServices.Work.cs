namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    /// <summary>Owns whole ordinary consumers through their final persistence and UI callbacks, not activity display.</summary>
    public OwnedOperationRegistry Work { get; } = new();
}
