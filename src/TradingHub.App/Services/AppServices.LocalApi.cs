using System.Globalization;
using System.Net.Sockets;
using TradingHub.App.Services.LocalApi;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    private readonly object _localApiGate = new();
    private readonly List<Task> _localApiDrains = new();
    private string? _localApiConfiguration;
    private bool _localApiClosed;

    /// <summary>The running local market-data API, or null when it is off or could not bind.</summary>
    internal LocalMarketDataApi? LocalApi { get; private set; }

    /// <summary>User-visible state of the local market-data API (shown in Settings).</summary>
    public string LocalApiStatus { get; private set; } = "Off";

    /// <summary>
    /// Starts, stops or restarts the opt-in loopback API to match the settings. Idempotent; a port or origin change
    /// restarts it. Every replaced server's drain is retained so shutdown awaits all of them.
    /// </summary>
    internal void ApplyLocalApi()
    {
        lock (_localApiGate)
        {
            if (_localApiClosed || IsShuttingDown) return;
            _localApiDrains.RemoveAll(drain => drain.IsCompletedSuccessfully);
            int port = Math.Clamp(Settings.LocalApiPort, 1024, 65535);
            var origins = LocalMarketDataApiOptions.NormalizeOrigins(Settings.LocalApiAllowedOrigins);
            string configuration = port.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", origins);
            if (Settings.LocalApiEnabled && LocalApi is { IsRunning: true } && _localApiConfiguration == configuration) return;

            StopLocalApiLocked();
            if (!Settings.LocalApiEnabled)
            {
                LocalApiStatus = "Off";
                return;
            }

            var api = new LocalMarketDataApi(new AppLocalMarketDataSource(this), new LocalMarketDataApiOptions { Port = port, AllowedOrigins = origins });
            try
            {
                api.Start();
                LocalApi = api;
                _localApiConfiguration = configuration;
                LocalApiStatus = $"Listening on http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/api/v1";
                this.LogInfo($"Local market-data API listening on 127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)} ({origins.Count} allowed browser origins).");
            }
            catch (Exception error)
            {
                _localApiDrains.Add(api.StopAsync());
                LocalApiStatus = error is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    ? $"Port {port.ToString(CultureInfo.InvariantCulture)} is already in use."
                    : $"The local market-data API could not start: {error.Message}";
                this.LogWarn("Local market-data API could not start", error);
            }
        }
    }

    /// <summary>Closes admission permanently and awaits the drain of the current and every previously replaced server.</summary>
    internal Task ShutdownLocalApiAsync()
    {
        Task[] drains;
        lock (_localApiGate)
        {
            _localApiClosed = true;
            StopLocalApiLocked();
            LocalApiStatus = "Off";
            drains = _localApiDrains.ToArray();
        }
        return Task.WhenAll(drains);
    }

    private void StopLocalApiLocked()
    {
        if (LocalApi is not { } api) return;
        LocalApi = null;
        _localApiConfiguration = null;
        _localApiDrains.Add(api.StopAsync());
        this.LogInfo("Local market-data API stopped.");
    }
}
