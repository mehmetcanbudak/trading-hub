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

    /// <summary>Raised (on any thread) when the API stops on its own, so Settings can show it.</summary>
    public event Action? LocalApiStatusChanged
    {
        add => LocalApiHost.StatusChanged += value;
        remove => LocalApiHost.StatusChanged -= value;
    }

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
    private string _status = "Off";
    private bool _closed;

    /// <summary>The running API, or null when it is off, could not bind or was shut down.</summary>
    public LocalMarketDataApi? Api { get; private set; }

    /// <summary>One complete display text per state; holes are only the port, URL or system error.</summary>
    public string Status
    {
        get
        {
            lock (_gate) return Api is { Fault: { } fault } ? $"The local market-data API stopped: {fault}" : _status;
        }
        private set => _status = value;
    }

    /// <summary>Raised (on any thread) when the running API stops accepting connections on its own.</summary>
    public event Action? StatusChanged;

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
                if (ForeignListenerHolds(port))
                    throw new SocketException((int)SocketError.AddressAlreadyInUse);
                api.Faulted += () => StatusChanged?.Invoke();
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
    /// True when another socket would receive this port's local connections beside our 127.0.0.1 listener: a wildcard
    /// (0.0.0.0, [::] or dual-stack [::]) listener, which both macOS and Windows let our specific bind coexist with (so
    /// we would silently take that program's loopback connections), or a listener on the IPv6 loopback [::1], which
    /// clients that resolve "localhost" to ::1 reach instead of Trading Hub. The probes are bound, never listen, and are
    /// closed at once; without an IPv6 loopback nothing can listen there and its probes are skipped.
    /// <para>macOS (BSD sockets): a wildcard bind with SO_REUSEADDR (tolerating TIME_WAIT) fails exactly when a wildcard
    /// listener of the same family exists (an IPv4 probe also fails beside a dual-stack listener; the IPv6 probe is
    /// IPv6-only); a holder on a specific address does not conflict with it. Duplicate exact TCP binds fail even with
    /// SO_REUSEADDR, so a [::1] probe fails beside a [::1] holder.</para>
    /// <para>Windows (Server 2003+ enhanced socket security, "Using SO_REUSEADDR and SO_EXCLUSIVEADDRUSE",
    /// https://learn.microsoft.com/windows/win32/winsock/using-so-reuseaddr-and-so-exclusiveaddruse): after a wildcard
    /// bind without SO_EXCLUSIVEADDRUSE (the default), a later same-user specific bind succeeds even with
    /// SO_EXCLUSIVEADDRUSE, which is why our exclusive 127.0.0.1 listener alone does not detect it. A later wildcard bind
    /// of the same family with no options fails with WSAEADDRINUSE for a wildcard holder with any option, by the same or
    /// another user, and succeeds beside specific-address holders. An IPv4 wildcard bind did not conflict with an
    /// existing dual-stack [::] listener on the Windows CI runners, so a second probe binds a dual-stack [::] socket:
    /// per the same page, a dual-stack wildcard bind reserves the port on both stacks and its checks must succeed on
    /// both, so it fails beside an IPv4 or IPv6 wildcard holder — except an IPv6-only [::] holder, which the Windows CI
    /// runners let a dual-stack probe coexist with, so an IPv6-only [::] probe is bound as well. A specific [::1] bind with no options fails beside a
    /// [::1] holder. SO_REUSEADDR is never set on Windows: there it can bind over (hijack) a holder that set it instead
    /// of detecting it.</para>
    /// </summary>
    internal static bool ForeignListenerHolds(int port)
    {
        bool ipv6 = Socket.OSSupportsIPv6;
        if (!OperatingSystem.IsWindows())
            return Conflicts(new IPEndPoint(IPAddress.Any, port), reuseAddress: true, dualMode: null) ||
                ipv6 && (Conflicts(new IPEndPoint(IPAddress.IPv6Any, port), reuseAddress: true, dualMode: false) ||
                         Conflicts(new IPEndPoint(IPAddress.IPv6Loopback, port), reuseAddress: true, dualMode: null));
        return Conflicts(new IPEndPoint(IPAddress.Any, port), reuseAddress: false, dualMode: null) ||
            ipv6 && (Conflicts(new IPEndPoint(IPAddress.IPv6Any, port), reuseAddress: false, dualMode: true) ||
                     Conflicts(new IPEndPoint(IPAddress.IPv6Any, port), reuseAddress: false, dualMode: false) ||
                     Conflicts(new IPEndPoint(IPAddress.IPv6Loopback, port), reuseAddress: false, dualMode: null));
    }

    private static bool Conflicts(IPEndPoint endpoint, bool reuseAddress, bool? dualMode)
    {
        try
        {
            using var probe = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (reuseAddress) probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (dualMode is { } dual) probe.DualMode = dual;
            probe.Bind(endpoint);
            return false;
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
        catch (SocketException error) when (endpoint.AddressFamily == AddressFamily.InterNetworkV6 &&
            error.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
        {
            return false; // no usable IPv6 loopback: nothing can listen there either
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
