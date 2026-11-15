using TradingHub.Common.Logging;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>Makes failing live runner/portfolio state saves visible: a structured warning log and a local system alert
/// (never forwarded externally) when a state file starts failing. The store keeps retrying on its own schedule.</summary>
internal sealed class LiveStateFaultReporter : IDisposable
{
    private readonly AlertCenter _alerts;

    public LiveStateFaultReporter(AlertCenter alerts)
    {
        _alerts = alerts;
        LiveStore.StateSaveFailed += OnStateSaveFailed;
    }

    private void OnStateSaveFailed(string message, Exception error)
    {
        this.LogWarn(message, error);
        _alerts.Raise(AlertKind.System, "Live", "Live strategy state", message, allowExternal: false);
    }

    public void Dispose() => LiveStore.StateSaveFailed -= OnStateSaveFailed;
}
