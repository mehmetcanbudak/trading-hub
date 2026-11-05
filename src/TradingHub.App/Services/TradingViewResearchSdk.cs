using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TradingHub.Networking;

namespace TradingHub.App.Services;

/// <summary>Only the dedicated grant's SDK token container is persisted, exclusively in ISecretStore.</summary>
public sealed class TradingViewSecretTokenCache(ISecretStore secrets, string grantId, Func<bool> grantIsCurrent) : ITokenCache
{
    private readonly object _gate = new();
    public static string Account(string grant) => "tradingview-research-oauth:" + grant;
    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!grantIsCurrent()) throw new TradingViewResearchException("TradingView authorization grant was revoked.");
            string json = secrets.Get(Account(grantId));
            if (string.IsNullOrEmpty(json)) return ValueTask.FromResult<TokenContainer?>(null);
            try { return ValueTask.FromResult(JsonSerializer.Deserialize<TokenContainer>(json)); }
            catch (JsonException) { throw new TradingViewResearchException("Saved TradingView authorization is invalid. Reconnect outside chat."); }
        }
    }
    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!grantIsCurrent()) throw new TradingViewResearchException("TradingView authorization grant was revoked.");
            if (string.IsNullOrEmpty(tokens.AccessToken) || tokens.TokenType != "Bearer")
                throw new TradingViewResearchException("TradingView did not issue a supported bearer token.");
            secrets.Set(Account(grantId), JsonSerializer.Serialize(tokens));
            // A revoke racing the secret write cannot resurrect a usable dedicated entry.
            if (!grantIsCurrent()) { secrets.Set(Account(grantId), null); throw new TradingViewResearchException("TradingView authorization was revoked."); }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Official ModelContextProtocol.Core 2.3.1 (Apache-2.0), csharp-sdk source 61420937cbef3eab895cafc8a89418811fb71f54.
/// SDK owns PRM/issuer discovery, S256 PKCE, state/iss validation, resource binding, registration and refresh.
/// Application owns the loopback callback, dedicated secret cache and explicit interactive permission.
/// </summary>
public sealed class TradingViewResearchSdk : ITradingViewResearchTransport
{
    private readonly McpClient _client;
    private readonly HttpClientTransport _transport;
    private readonly ResearchLoopbackCallback? _callback;
    private bool _disposed;
    private readonly object _notificationGate = new();
    private Action? _catalogChanged;
    private bool _pendingCatalogChange;
    public event Action? CatalogChanged
    {
        add
        {
            lock (_notificationGate)
            {
                _catalogChanged += value;
                if (_pendingCatalogChange && value != null)
                {
                    _pendingCatalogChange = false;
                    value.Invoke();
                }
            }
        }
        remove { lock (_notificationGate) _catalogChanged -= value; }
    }
    private void NotifyCatalogChanged()
    {
        lock (_notificationGate)
        {
            if (_catalogChanged == null) _pendingCatalogChange = true;
            else _catalogChanged.Invoke();
        }
    }
    public event Action? Disconnected;
    private TradingViewResearchSdk(McpClient client, HttpClientTransport transport, ResearchLoopbackCallback? callback)
    {
        _client = client;
        _transport = transport;
        _callback = callback;
        _ = ObserveCompletionAsync();
    }
    private async Task ObserveCompletionAsync()
    {
        await _client.Completion.ConfigureAwait(false);
        if (!_disposed) Disconnected?.Invoke();
    }
    public static async Task<TradingViewResearchSdk> ConnectAsync(TradingViewResearchConnection connection,
        ITokenCache cache, CancellationToken cancellationToken = default,
        HttpClient? httpClient = null,
        Func<AuthorizationCallbackContext, CancellationToken, Task<AuthorizationResult?>>? authorization = null,
        ProxyHandlerFactory? network = null)
    {
        ResearchLoopbackCallback? callback = connection.Interactive && authorization == null ? new() : null;
        var options = new ClientOAuthOptions
        {
            RedirectUri = callback?.RedirectUri ?? new Uri("http://127.0.0.1:49152/tradingview-callback/"),
            TokenCache = cache,
            DynamicClientRegistration = new() { ClientName = "Trading Hub Research", ApplicationType = "native" },
            AuthorizationCallbackHandler = async (context, ct) =>
            {
                if (!connection.Interactive)
                    throw new TradingViewResearchException("TradingView requires authorization. Reconnect in Research settings, never in chat.");
                if (authorization != null) return await authorization(context, ct).ConfigureAwait(false);
                return await callback!.AuthorizeAsync(context, ct).ConfigureAwait(false);
            }
        };
        var transport = new HttpClientTransport(new()
        {
            Endpoint = connection.Endpoint,
            Name = "TradingView research",
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = options,
            MaxReconnectionAttempts = 0
        }, httpClient ?? new HttpClient(new ResearchHttpsHandler(connection.Endpoint, network)), ownsHttpClient: httpClient == null);
        TradingViewResearchSdk? result = null;
        bool changedBeforeAttach = false;
        var connectionGate = new object();
        try
        {
            var client = await McpClient.CreateAsync(transport, new()
            {
                ClientInfo = new() { Name = "Trading Hub Research", Version = "1.0" },
                // TradingView currently documents initialize + paginated tools/list, not model execution.
                ProtocolVersion = "2025-06-18",
                Capabilities = new(),
                InitializationTimeout = TimeSpan.FromMinutes(5),
                Handlers = new()
                {
                    NotificationHandlers = [new("notifications/tools/list_changed", (_, _) =>
                    {
                        lock (connectionGate)
                        {
                            if (result != null) result.NotifyCatalogChanged(); else changedBeforeAttach = true;
                        }
                        return ValueTask.CompletedTask;
                    })]
                }
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            lock (connectionGate)
            {
                if (!changedBeforeAttach) result = new(client, transport, callback);
            }
            if (result == null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new TradingViewResearchException("TradingView catalog changed during connection. Reconnect to review.");
            }
            return result;
        }
        catch
        {
            callback?.Dispose();
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    public async Task<TradingViewToolPage> ListToolsAsync(string? cursor, CancellationToken cancellationToken)
    {
        var result = await _client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
        return new(result.Tools.Select(t => JsonSerializer.SerializeToElement(t)).ToImmutableArray(), result.NextCursor);
    }
    public async Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var values = arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        var result = await _client.CallToolAsync(name, values, cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _callback?.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    public static async Task<string> RevokeTokensAsync(string saved, CancellationToken cancellationToken = default,
        HttpClient? httpClient = null, ProxyHandlerFactory? network = null)
    {
        var tokens = JsonSerializer.Deserialize<TokenContainer>(saved) ?? throw new TradingViewResearchException("Saved authorization was invalid.");
        if (!Uri.TryCreate(tokens.AuthorizationServer, UriKind.Absolute, out var issuer) || issuer.Scheme != "https")
            return "No supported server revocation metadata is available. Remove the app grant in TradingView account settings if needed.";
        using var ownedHttp = httpClient == null ? new HttpClient(CreateResearchHandler(network)) : null;
        var http = httpClient ?? ownedHttp!;
        string path = issuer.AbsolutePath.TrimEnd('/');
        var locations = new[] {
            new Uri(issuer.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-authorization-server" + path),
            new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration") };
        JsonElement metadata = default;
        foreach (var location in locations)
        {
            using var response = await http.GetAsync(location, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            metadata = document.RootElement.Clone();
            break;
        }
        if (metadata.ValueKind == JsonValueKind.Undefined || !metadata.TryGetProperty("issuer", out var advertised)
            || advertised.GetString() != tokens.AuthorizationServer)
            throw new TradingViewResearchException("Server revocation metadata issuer could not be validated.");
        if (!metadata.TryGetProperty("revocation_endpoint", out var endpoint))
            return "The authorization server does not advertise token revocation. Remove the app grant in TradingView account settings if needed.";
        if (!Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var revoke) || revoke.Scheme != "https")
            throw new TradingViewResearchException("Server revocation endpoint is not HTTPS.");
        foreach (var item in new[] { (tokens.RefreshToken, "refresh_token"), ((string?)tokens.AccessToken, "access_token") })
        {
            if (string.IsNullOrEmpty(item.Item1)) continue;
            using var request = new HttpRequestMessage(HttpMethod.Post, revoke);
            var fields = new Dictionary<string, string> { ["token"] = item.Item1, ["token_type_hint"] = item.Item2 };
            if (tokens.ClientId != null) fields["client_id"] = tokens.ClientId;
            if (tokens.ClientSecret != null)
            {
                if (tokens.TokenEndpointAuthMethod == "client_secret_basic")
                    request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        Uri.EscapeDataString(tokens.ClientId ?? "") + ":" + Uri.EscapeDataString(tokens.ClientSecret))));
                else if (tokens.TokenEndpointAuthMethod == "client_secret_post") fields["client_secret"] = tokens.ClientSecret;
                else throw new TradingViewResearchException("Server revocation client authentication is not supported.");
            }
            request.Content = new FormUrlEncodedContent(fields);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        return "Server token revocation completed.";
    }

    private static SocketsHttpHandler CreateResearchHandler(ProxyHandlerFactory? network)
    {
        var handler = (network ?? new ProxyHandlerFactory(new NetworkProxyPolicy())).CreateHandler();
        handler.AllowAutoRedirect = false;
        return handler;
    }

    private sealed class ResearchHttpsHandler(Uri resource, ProxyHandlerFactory? network) : DelegatingHandler(CreateResearchHandler(network))
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { Scheme: "https", UserInfo.Length: 0, Fragment.Length: 0 } target)
                throw new TradingViewResearchException("TradingView authorization and metadata endpoints must use HTTPS without user credentials or fragments.");
            if (request.Headers.Authorization?.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) == true && target != resource)
                throw new TradingViewResearchException("A research bearer token cannot be sent to another resource.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>Bound ephemeral loopback listener. No manual token/code input, URL logging or CLI credential access.</summary>
internal sealed class ResearchLoopbackCallback : IDisposable
{
    private readonly TcpListener _listener;
    public Uri RedirectUri { get; }
    public ResearchLoopbackCallback()
    {
        _listener = new(IPAddress.Loopback, 0);
        _listener.Start();
        RedirectUri = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/tradingview-callback/");
    }
    public async Task<AuthorizationResult?> AuthorizeAsync(AuthorizationCallbackContext context, CancellationToken cancellationToken)
    {
        if (context.RedirectUri != RedirectUri || context.AuthorizationUri.Scheme != "https")
            throw new TradingViewResearchException("Unsupported OAuth browser redirect. HTTPS authorization and the bound loopback callback are required.");
        Process.Start(new ProcessStartInfo(context.AuthorizationUri.AbsoluteUri) { UseShellExecute = true });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            using var socket = await _listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            string? first = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            string[] request = first?.Split(' ') ?? [];
            bool valid = request.Length == 3 && request[0] == "GET" && request[1].StartsWith("/tradingview-callback/?", StringComparison.Ordinal)
                && request[1].Length < 16384;
            string responseText = valid ? "Return to Trading Hub. Research access still requires explicit tool review and approval." : "Invalid callback.";
            byte[] body = Encoding.UTF8.GetBytes(responseText);
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, deadline.Token).ConfigureAwait(false);
            if (!valid) continue;
            var uri = new Uri(RedirectUri, request[1]);
            var values = HttpUtility.ParseQueryString(uri.Query);
            // The SDK validates exact state and the metadata-bound issuer BEFORE exchanging the code.
            return new() { Code = values["code"], State = values["state"], Iss = values["iss"] };
        }
    }
    public void Dispose() => _listener.Stop();
}
