using System.Net;
using System.Net.Sockets;
using System.Web;
using TradingHub.App.Services;
using TradingHub.Feeds;
using TradingHub.Feeds.Longbridge;

namespace TradingHub.Tests;

public sealed class LongbridgeConnectionLifetimeTests
{
    [Fact]
    public async Task Cached_feed_request_owns_terminal_manager_drain_and_HTTP_until_the_ignored_cancellation_finishes()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-longbridge-manager-" + Guid.NewGuid().ToString("N"));
        var handler = new RejectNetwork();
        var transport = new HeldTransport();
        var connections = new ConnectionManager(secretStore: new MemorySecrets(), configurationPath: Path.Combine(root, "connections.json"),
            longbridgeHttpFactory: (_, _) => new HttpClient(handler), longbridgeTransportFactory: (_, _) => transport);
        var profile = new ConnectionProfile { Id = "longbridge-owned", Kind = ConnectionKind.Longbridge, Longbridge = LongbridgeFeedTests.Settings() };
        connections.Profiles.Add(profile);
        try
        {
            var feed = (LongbridgeFeed)connections.GetFeed(profile.Id);
            var request = feed.GetInstrumentsAsync(default);
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stopping = connections.StopLongbridgeAsync();
            await transport.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(stopping, connections.StopLongbridgeAsync());
            Assert.False(stopping.IsCompleted);
            Assert.False(handler.WasDisposed);
            Assert.True(transport.Closing);
            Assert.Throws<ObjectDisposedException>(() => connections.GetFeed(profile.Id));
            Assert.Throws<ObjectDisposedException>(() => connections.LongbridgeSessionFor(profile));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => LongbridgeAuthorizationFlow.AuthorizeAsync(connections, profile,
                (_, _) => throw new InvalidOperationException("A new browser must not be launched after shutdown admission closes")));
            transport.Release.TrySetCanceled(); // The fake wire ignored cancellation until its owned stream explicitly finished.
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
            Assert.True(handler.WasDisposed);
            Assert.True(transport.Drained);
            Assert.Equal(0, handler.Requests);
        }
        finally
        {
            transport.Release.TrySetCanceled();
            await connections.StopLongbridgeAsync();
            connections.DisposeAll();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_loopback_authorization_is_canceled_and_drained_even_when_a_browser_callback_ignores_cancellation(bool holdBrowser)
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-longbridge-auth-owner-" + Guid.NewGuid().ToString("N"));
        var handler = new RejectNetwork();
        var connections = new ConnectionManager(secretStore: new MemorySecrets(), configurationPath: Path.Combine(root, "connections.json"),
            longbridgeHttpFactory: (_, _) => new HttpClient(handler));
        var profile = new ConnectionProfile { Id = "longbridge-auth-owned", Kind = ConnectionKind.Longbridge, Longbridge = LongbridgeFeedTests.Settings() };
        connections.Profiles.Add(profile);
        var entered = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? authorization = null;
        try
        {
            authorization = LongbridgeAuthorizationFlow.AuthorizeAsync(connections, profile, async (uri, _) =>
            {
                var redirect = new Uri(HttpUtility.ParseQueryString(uri.Query)["redirect_uri"]!);
                entered.TrySetResult(redirect);
                if (holdBrowser) await release.Task;
            });
            Uri callback = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stopping = connections.StopLongbridgeAsync();
            if (holdBrowser)
            {
                Assert.False(stopping.IsCompleted);
                Assert.False(handler.WasDisposed);
                release.TrySetResult();
            }
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorization);
            Assert.True(handler.WasDisposed);
            Assert.Equal(0, handler.Requests); // A known public client creates PKCE/state without registration or token exchange.
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<SocketException>(async () => await client.ConnectAsync(callback.Host, callback.Port, timeout.Token));
        }
        finally
        {
            release.TrySetResult();
            await connections.StopLongbridgeAsync();
            if (authorization != null)
                try { await authorization; } catch (OperationCanceledException) { }
            connections.DisposeAll();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string Get(string account) => _values.GetValueOrDefault(account, "");
        public void Set(string account, string? secret)
        {
            if (string.IsNullOrEmpty(secret)) _values.Remove(account); else _values[account] = secret;
        }
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool WasDisposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("This lifecycle fixture must not register, authorize or contact a provider");
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class HeldTransport : ILongbridgeTransport, IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        public bool Closing { get; private set; }
        public bool Drained => _drained.Task.IsCompleted;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<byte[]> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<byte, ReadOnlyMemory<byte>>? Push;
        public event Action<FeedStatus, string?>? Status;
        public void Emit(byte command, byte[] bytes) => Push?.Invoke(command, bytes);
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(FeedStatus status) => Status?.Invoke(status, null);
        public async Task<byte[]> RequestAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            lock (_gate)
            {
                if (Closing) throw new ObjectDisposedException(nameof(HeldTransport));
                _active++;
            }
            Entered.TrySetResult();
            try { return await Release.Task; }
            finally { lock (_gate) if (--_active == 0 && Closing) _drained.TrySetResult(); }
        }
        public void Dispose() => _ = DisposeAsync();
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                Closing = true;
                DisposeEntered.TrySetResult();
                if (_active == 0) _drained.TrySetResult();
            }
            return new(_drained.Task);
        }
    }
}
