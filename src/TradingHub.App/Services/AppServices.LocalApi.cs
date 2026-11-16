using System.Globalization;
using System.Net;
using System.Net.Sockets;
using TradingHub.App.Services.LocalApi;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    private LocalApiHost? _localApiHost;

    private LocalApiHost LocalApiHost =>
        LazyInitializer.EnsureInitialized(ref _localApiHost, () => new LocalApiHost(() => new AppLocalMarketDataSource(this)));

    /// <summary>The running local market-data API, or null when it is off or could not bind.</summary>
    internal LocalMarketDataApi? LocalApi => LocalApiHost.Api;

    /// <summary>User-visible state of the local market-data API (shown in Settings).</summary>
    public string LocalApiStatus => LocalApiHost.Status;

    /// <summary>Starts, stops or restarts the opt-in loopback API to match the settings (also at startup).</summary>
    internal void ApplyLocalApi()
    {
        if (IsShuttingDown) return;
        LocalApiHost.Apply(Settings);
    }

    /// <summary>Closes admission permanently and awaits the drain of the current and every previously replaced server.</summary>
    internal Task ShutdownLocalApiAsync() => LocalApiHost.ShutdownAsync();
}

/// <summary>
/// Lifecycle of the opt-in local market-data API: starts, stops or restarts it to match the settings, reports a
/// user-visible status and retains every replaced server's drain so shutdown awaits all of them.
/// </summary>
internal sealed class LocalApiHost(Func<ILocalMarketDataSource> source)
{
    private readonly object _gate = new();
    private readonly List<Task> _drains = new();
    private string? _configuration;
    private bool _closed;

    /// <summary>The running API, or null when it is off, could not bind or was shut down.</summary>
    public LocalMarketDataApi? Api { get; private set; }

    /// <summary>One complete display text per state; holes are only the port, URL or system error.</summary>
    public string Status { get; private set; } = "Off";

    /// <summary>Idempotent; a port or origin change restarts the server, and a failed bind is retried on the next call.</summary>
    public void Apply(AppSettings settings)
    {
        lock (_gate)
        {
            if (_closed) return;
            _drains.RemoveAll(drain => drain.IsCompletedSuccessfully);
            int port = Math.Clamp(settings.LocalApiPort, 1024, 65535);
            var origins = LocalMarketDataApiOptions.NormalizeOrigins(settings.LocalApiAllowedOrigins);
            string configuration = port.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", origins);
            if (settings.LocalApiEnabled && Api is { IsRunning: true } && _configuration == configuration) return;

            StopLocked();
            if (!settings.LocalApiEnabled)
            {
                Status = "Off";
                return;
            }

            var api = new LocalMarketDataApi(source(), new LocalMarketDataApiOptions { Port = port, AllowedOrigins = origins });
            try
            {
                if (WildcardListenerHolds(port))
                    throw new SocketException((int)SocketError.AddressAlreadyInUse);
                api.Start();
                Api = api;
                _configuration = configuration;
                Status = $"Listening on http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/api/v1";
                this.LogInfo($"Local market-data API listening on 127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)} ({origins.Count} allowed browser origins).");
            }
            catch (Exception error)
            {
                _drains.Add(api.StopAsync());
                Status = error is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    ? $"Port {port.ToString(CultureInfo.InvariantCulture)} is already in use."
                    : $"The local market-data API could not start: {error.Message}";
                this.LogWarn("Local market-data API could not start", error);
            }
        }
    }

    /// <summary>Closes admission permanently and awaits the drain of the current and every previously replaced server.</summary>
    public Task ShutdownAsync()
    {
        Task[] drains;
        lock (_gate)
        {
            _closed = true;
            StopLocked();
            Status = "Off";
            drains = _drains.ToArray();
        }
        return Task.WhenAll(drains);
    }

    /// <summary>
    /// True when another socket holds the IPv4 wildcard (0.0.0.0) of the port, directly or as a dual-stack [::] socket
    /// (which reserves the port on both stacks). Both macOS and Windows let our 127.0.0.1 bind succeed beside such a
    /// holder, which would silently take that program's loopback connections; the probe is bound, never listens, and is
    /// closed at once.
    /// <para>macOS (BSD sockets): a wildcard bind with SO_REUSEADDR (tolerating TIME_WAIT) fails exactly when a wildcard
    /// listener exists; a holder on a specific address does not conflict.</para>
    /// <para>Windows (Server 2003+ enhanced socket security, "Using SO_REUSEADDR and SO_EXCLUSIVEADDRUSE",
    /// https://learn.microsoft.com/windows/win32/winsock/using-so-reuseaddr-and-so-exclusiveaddruse): after a wildcard
    /// bind without SO_EXCLUSIVEADDRUSE (the default), a later same-user specific bind succeeds even with
    /// SO_EXCLUSIVEADDRUSE, which is why our exclusive 127.0.0.1 listener alone does not detect it. A later wildcard bind
    /// with no options fails with WSAEADDRINUSE for a wildcard holder with any option, by the same or another user, and
    /// succeeds beside specific-address holders. SO_REUSEADDR is not set here: on Windows it can bind over (hijack) a
    /// holder that set it instead of detecting it.</para>
    /// </summary>
    internal static bool WildcardListenerHolds(int port)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (!OperatingSystem.IsWindows()) probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            probe.Bind(new IPEndPoint(IPAddress.Any, port));
            return false;
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
    }

    private void StopLocked()
    {
        if (Api is not { } api) return;
        Api = null;
        _configuration = null;
        _drains.Add(api.StopAsync());
        this.LogInfo("Local market-data API stopped.");
    }
}
