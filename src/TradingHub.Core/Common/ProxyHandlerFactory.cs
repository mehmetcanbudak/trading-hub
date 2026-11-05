using System.Net;
using System.Net.WebSockets;

namespace TradingHub.Networking;

public enum NetworkProxyMode { System, Direct, Custom }

/// <summary>Serializable routing policy only. Authentication belongs in the private credential store.</summary>
public sealed record NetworkProxyPolicy
{
    public NetworkProxyMode Mode { get; init; } = NetworkProxyMode.System;
    public string Address { get; init; } = "";
}

/// <summary>
/// Creates owned transports with explicit policy. No process-wide default, environment variable, connection,
/// account or already-running transport is mutated. Recreate consumers after a policy change.
/// </summary>
public sealed class ProxyHandlerFactory
{
    private readonly NetworkProxyPolicy _policy;
    private readonly NetworkCredential? _credentials;
    private readonly Uri? _address;
    public NetworkProxyPolicy Policy => _policy;

    public ProxyHandlerFactory(NetworkProxyPolicy policy, NetworkCredential? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy with { };
        if (!Enum.IsDefined(policy.Mode)) throw new ArgumentException("Unknown network proxy mode.", nameof(policy));
        if (policy.Mode == NetworkProxyMode.Custom)
        {
            if (!Uri.TryCreate(policy.Address, UriKind.Absolute, out var address) ||
                address.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrWhiteSpace(address.Host) ||
                address.UserInfo.Length > 0 || address.AbsolutePath is not ("" or "/") || address.Port is < 1 or > 65535 ||
                address.Query.Length > 0 || address.Fragment.Length > 0)
                throw new ArgumentException("Use an HTTP, HTTPS or SOCKS5 proxy address with host and port only. Enter credentials separately.", nameof(policy));
            _address = address;
        }
        _credentials = credentials == null ? null : new NetworkCredential(credentials.UserName, credentials.Password, credentials.Domain);
    }

    public SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = _policy.Mode != NetworkProxyMode.Direct,
        Proxy = _policy.Mode == NetworkProxyMode.Custom ? CreateProxy() : null,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = Timeout.InfiniteTimeSpan,
    };

    public HttpClient CreateHttpClient(TimeSpan? timeout = null) => new(CreateHandler(), disposeHandler: true)
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(30),
    };

    /// <summary>Owned handshake transport for sockets that send credentials after connecting; redirects are forbidden.</summary>
    public HttpMessageInvoker CreateWebSocketInvoker()
    {
        var handler = CreateHandler();
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        handler.PooledConnectionLifetime = TimeSpan.Zero;
        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public void Configure(ClientWebSocketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Proxy = _policy.Mode switch
        {
            NetworkProxyMode.Direct => null,
            NetworkProxyMode.Custom => CreateProxy(),
            _ => HttpClient.DefaultProxy,
        };
    }

    private WebProxy CreateProxy() => new(_address!)
    {
        BypassProxyOnLocal = false,
        Credentials = _credentials,
    };
}
