namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    /// <summary>Owns whole ordinary consumers through their final persistence and UI callbacks, not activity display.</summary>
    public OwnedOperationRegistry Work { get; } = new();

    /// <summary>Confirmed manual order gestures, off the UI thread for thread-safe native accounts; drained at quit.</summary>
    internal OrderActionLanes OrderLanes { get; } = new();
}
