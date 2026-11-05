using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using TradingHub.App.Services;
using TradingHub.Networking;

namespace TradingHub.Tests;

/// <summary>Real CONNECT failure paths, without relaxing HTTPS validation or invoking OAuth/model services.</summary>
public sealed class KvpResearchProxyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResearchResourceAndRevocationMetadataUseTheSelectedProxyBeforeAnyHttpsApplicationTraffic(bool revocation)
    {
        await using var origin = new ClosedTlsOrigin();
        await using var proxy = new FakeProxyServer();
        var network = Network(proxy);
        var secrets = new PrivateFixtureSecrets();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Record.ExceptionAsync(() => InvokeAsync(revocation, origin.Resource, network, secrets, timeout.Token));
        Assert.NotNull(error);
        Assert.True(origin.AcceptedConnections > 0);
        Assert.NotEmpty(proxy.Requests);
        Assert.All(proxy.Requests, request =>
        {
            Assert.Equal("CONNECT", request.Method);
            Assert.Equal(origin.Resource.Host, request.Target.Host);
            Assert.Equal(origin.Resource.Port, request.Target.Port);
        });
        Assert.Equal(0, secrets.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResearchNeverFallsBackToDirectResourceOrIssuerWhenItsExplicitProxyIsUnavailable(bool revocation)
    {
        await using var origin = new ClosedTlsOrigin();
        var proxy = new FakeProxyServer();
        var network = Network(proxy);
        await proxy.DisposeAsync();
        var secrets = new PrivateFixtureSecrets();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Record.ExceptionAsync(() => InvokeAsync(revocation, origin.Resource, network, secrets, timeout.Token));
        Assert.NotNull(error);
        Assert.Equal(0, origin.AcceptedConnections);
        Assert.Equal(0, secrets.Writes);
        Assert.Empty(proxy.Requests);
    }

    private static ProxyHandlerFactory Network(FakeProxyServer proxy) => new(new NetworkProxyPolicy
    {
        Mode = NetworkProxyMode.Custom, Address = proxy.Address,
    });
    private static async Task InvokeAsync(bool revocation, Uri resource, ProxyHandlerFactory network, ISecretStore secrets, CancellationToken ct)
    {
        if (revocation)
        {
            string saved = JsonSerializer.Serialize(new TokenContainer
            {
                AuthorizationServer = resource.GetLeftPart(UriPartial.Authority),
                AccessToken = "fixture-private-access-token", RefreshToken = "fixture-private-refresh-token", TokenType = "Bearer",
                ObtainedAt = DateTimeOffset.UtcNow,
            });
            await TradingViewResearchSdk.RevokeTokensAsync(saved, ct, network: network);
            return;
        }
        const string grant = "fixture-research-proxy-grant";
        var cache = new TradingViewSecretTokenCache(secrets, grant, () => true);
        await using var client = await TradingViewResearchSdk.ConnectAsync(new TradingViewResearchConnection(resource, grant, Interactive: false),
            cache, ct, network: network);
    }

    private sealed class PrivateFixtureSecrets : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        private int _writes;
        public int Writes => Volatile.Read(ref _writes);
        public string Get(string account) => _values.GetValueOrDefault(account, "");
        public void Set(string account, string? secret)
        {
            Interlocked.Increment(ref _writes);
            if (string.IsNullOrEmpty(secret)) _values.TryRemove(account, out _);
            else _values[account] = secret;
        }
    }

    /// <summary>Accepts and closes only loopback TCP; no certificate trust bypass or fake application success.</summary>
    private sealed class ClosedTlsOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private int _accepted;
        public Uri Resource { get; }
        public int AcceptedConnections => Volatile.Read(ref _accepted);
        public ClosedTlsOrigin()
        {
            _listener.Start();
            Resource = new Uri($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/mcp");
            _accept = DropConnectionsAsync();
        }
        private async Task DropConnectionsAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _accepted);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accept.ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
