using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using TradingHub.Feeds.Longbridge;
using TradingHub.Networking;

namespace TradingHub.Tests;

public sealed class LongbridgeProtocolTests
{
    [Fact]
    public void RequestFramingMatchesIndependentBigEndianWireExampleAndRejectsTruncation()
    {
        byte[] request = LongbridgeProtocol.Request(17, 0x01020304, 10000, new byte[] { 0x0A, 0x01, 0x41 });
        Assert.Equal(new byte[] { 1, 17, 1, 2, 3, 4, 0x27, 0x10, 0, 0, 3, 0x0A, 1, 0x41 }, request);
        var parsed = LongbridgeProtocol.Parse(new byte[] { 2, 17, 1, 2, 3, 4, 0, 0, 0, 2, 0x08, 1 });
        Assert.Equal(0x01020304u, parsed.RequestId);
        Assert.Equal(new byte[] { 0x08, 1 }, parsed.Body);
        Assert.Throws<InvalidDataException>(() => LongbridgeProtocol.Parse(new byte[] { 2, 17, 1, 2, 3 }));
        Assert.Throws<InvalidDataException>(() => LongbridgeProtocol.Parse(new byte[] { 3, 102, 0, 0, 2, 1 }));
        Assert.Throws<InvalidDataException>(() => LongbridgeProtocol.Parse(new byte[] { 0x13, 102, 0, 0, 0 }));
        Assert.Throws<InvalidDataException>(() => LongbridgeProtocol.Parse(new byte[] { 3, 102, 0, 0, 0, 99 }));
    }

    [Fact]
    public async Task InjectedSocketAuthenticatesWithOtpCorrelatesResponseAndEchoesHeartbeat()
    {
        var settings = LongbridgeFeedTests.Settings();
        string? secret = null;
        var handler = new LongbridgeOAuthTests.HttpFixture
        {
            Reply = request => Task.FromResult(LongbridgeOAuthTests.Json(request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"us_fixture\",\"refresh_token\":\"refresh\",\"expires_in\":3600}"
                : "{\"code\":0,\"data\":{\"otp\":\"quote-otp\",\"limit\":5,\"online\":0}}")),
        };
        using var http = new HttpClient(handler);
        var session = new LongbridgeSession(settings, http, () => secret, value => secret = value, new LongbridgeOAuthTests.Clock());
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default);
        var connection = new SocketFixture();
        await using var socket = new LongbridgeSocket(session, settings, () => connection)
        { MinimumRequestInterval = TimeSpan.Zero, HeartbeatInterval = TimeSpan.FromMilliseconds(10) };
        byte[] result = await socket.RequestAsync((byte)LongbridgeCommand.Quote, new byte[] { 0x0A, 1, 0x41 }, default);
        Assert.Equal(new byte[] { 0x08, 123 }, result);
        Assert.Contains(connection.Sent, p => p.Command == (byte)LongbridgeCommand.Auth && p.Body.SequenceEqual(new byte[] { 0x0A, 9, 113, 117, 111, 116, 101, 45, 111, 116, 112 }));
        Assert.Equal("wss://quote.invalid/v2?version=1&codec=1&platform=9", connection.Endpoint!.AbsoluteUri);
        var heartbeat = new byte[] { 1, 1, 0, 0, 0, 77, 0x13, 0x88, 0, 0, 2, 8, 1 };
        connection.Incoming.Writer.TryWrite(heartbeat);
        var reply = await connection.Heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(77u, reply.RequestId);
        Assert.Equal(new byte[] { 8, 1 }, reply.Body);
        Assert.Equal("us_fixture", handler.Requests.Last().Bearer);
        var clientHeartbeat = await connection.ClientHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((byte)LongbridgeCommand.Heartbeat, clientHeartbeat.Command);
        Assert.NotEmpty(clientHeartbeat.Body);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.Status += (status, _) => { if (status == TradingHub.Feeds.FeedStatus.Reconnecting) closed.TrySetResult(); };
        connection.Incoming.Writer.TryWrite(new byte[] { 3, 0, 0, 0, 0 }); // A control Close is a push, not a request.
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(connection.Aborted);
    }

    [Fact]
    public async Task CredentialRetirementAbortsSocketAndCannotDeleteSavedAuthorization()
    {
        var settings = LongbridgeFeedTests.Settings(); string? secret = null;
        var handler = new LongbridgeOAuthTests.HttpFixture
        {
            Reply = request => Task.FromResult(LongbridgeOAuthTests.Json(request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"fixture\",\"refresh_token\":\"refresh\",\"expires_in\":3600}"
                : "{\"data\":{\"otp\":\"quote-otp\"}}")),
        };
        using var http = new HttpClient(handler);
        var session = new LongbridgeSession(settings, http, () => secret, value => secret = value, new LongbridgeOAuthTests.Clock());
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default);
        string saved = secret!;
        var connection = new SocketFixture();
        await using var socket = new LongbridgeSocket(session, settings, () => connection) { MinimumRequestInterval = TimeSpan.Zero };
        await socket.RequestAsync((byte)LongbridgeCommand.Quote, Array.Empty<byte>(), default);
        session.Retire();
        Assert.True(session.LifetimeToken.IsCancellationRequested);
        await session.DisposeAsync();
        await socket.DisposeAsync();
        Assert.True(connection.Aborted);
        Assert.Equal(saved, secret);
        Assert.False(session.HasAuthorization);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AccessTokenAsync(default));
        Assert.Throws<InvalidOperationException>(() => session.ForgetAuthorization());
        Assert.Equal(saved, secret);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidResponseCommandFailsCallerInsteadOfPublishingUnrelatedPayload(bool holdSendUntilAbort)
    {
        var settings = LongbridgeFeedTests.Settings(); string? secret = null;
        var handler = new LongbridgeOAuthTests.HttpFixture
        {
            Reply = request => Task.FromResult(LongbridgeOAuthTests.Json(request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"fixture\",\"expires_in\":3600}"
                : "{\"data\":{\"otp\":\"quote-otp\"}}")),
        };
        using var http = new HttpClient(handler);
        var session = new LongbridgeSession(settings, http, () => secret, value => secret = value, new LongbridgeOAuthTests.Clock());
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default);
        var connection = new SocketFixture { WrongCommand = true, HoldQuoteSendUntilAbort = holdSendUntilAbort };
        await using var socket = new LongbridgeSocket(session, settings, () => connection) { MinimumRequestInterval = TimeSpan.Zero };
        await Assert.ThrowsAsync<InvalidDataException>(() => socket.RequestAsync((byte)LongbridgeCommand.Quote, Array.Empty<byte>(), default));
        if (holdSendUntilAbort) Assert.True(connection.Aborted);
    }

    [Fact]
    public async Task CapturedNetworkPolicyNeverFollowsUpgradeRedirectOrSendsOtpToItsTarget()
    {
        using var source = new FakeHttpServer();
        using var target = new FakeHttpServer();
        int sourceHandshakes = 0, targetUpgrades = 0;
        source.SocketRedirect = _ =>
        {
            Interlocked.Increment(ref sourceHandshakes);
            return target.Http + "/capture";
        };
        target.OnSocket = (_, _, ct) =>
        {
            Interlocked.Increment(ref targetUpgrades);
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var settings = LongbridgeFeedTests.Settings();
        settings.QuoteWsUrl = source.Ws + "/v2";
        string? secret = null;
        var handler = new LongbridgeOAuthTests.HttpFixture
        {
            Reply = request => Task.FromResult(LongbridgeOAuthTests.Json(request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"fixture\",\"refresh_token\":\"fixture-refresh\",\"expires_in\":3600}"
                : "{\"data\":{\"otp\":\"must-not-reach-redirect-target\"}}")),
        };
        using var http = new HttpClient(handler);
        var session = new LongbridgeSession(settings, http, () => secret, value => secret = value, new LongbridgeOAuthTests.Clock());
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default);
        var network = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Direct });
        await using var socket = new LongbridgeSocket(session, settings, socketFactory: () =>
        {
            var created = new ClientWebSocket();
            created.Options.Proxy = new WebProxy("http://127.0.0.1:9");
            return created;
        }, network: network) { RequestTimeout = TimeSpan.FromSeconds(2), MinimumRequestInterval = TimeSpan.Zero };
        await Assert.ThrowsAsync<WebSocketException>(() => socket.RequestAsync((byte)LongbridgeCommand.Quote, Array.Empty<byte>(), default));
        await socket.DisposeAsync();
        Assert.True(Volatile.Read(ref sourceHandshakes) > 0);
        Assert.Equal(0, Volatile.Read(ref targetUpgrades));
    }

    [Fact]
    public async Task ConcreteSocketUsesOwnedInvokerRejectsRedirectAndDoesNotKeepFactoryProxyOptions()
    {
        var handler = new RedirectHandshakeHandler();
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        var socket = new ClientWebSocket();
        socket.Options.Proxy = new WebProxy("http://127.0.0.1:9");
        var connection = new LongbridgeWebSocketConnection(socket, invoker);
        try
        {
            await Assert.ThrowsAsync<WebSocketException>(() => connection.ConnectAsync(new Uri("ws://127.0.0.1:1/quote"), default));
            var request = Assert.Single(handler.Requests);
            Assert.Equal(1, request.Port);
            Assert.Equal("/quote", request.AbsolutePath);
            Assert.Equal(0, handler.DisposeCalls);
        }
        finally { connection.Dispose(); }
        connection.Dispose();
        Assert.Equal(1, handler.DisposeCalls);
    }

    private sealed class RedirectHandshakeHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        public int DisposeCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
            response.Headers.Location = new Uri("http://127.0.0.1:2/redirected");
            return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCalls++;
            base.Dispose(disposing);
        }
    }

    internal sealed class SocketFixture : ILongbridgeSocketConnection
    {
        public readonly Channel<byte[]> Incoming = Channel.CreateUnbounded<byte[]>();
        public readonly ConcurrentQueue<LongbridgePacket> Sent = new();
        public readonly TaskCompletionSource<LongbridgePacket> Heartbeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<LongbridgePacket> ClientHeartbeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _abort = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri? Endpoint;
        public bool Aborted;
        public bool WrongCommand;
        public bool HoldQuoteSendUntilAbort;
        public Func<LongbridgePacket, byte[]>? ResponseBody;
        public Task ConnectAsync(Uri endpoint, CancellationToken ct) { Endpoint = endpoint; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var packet = LongbridgeProtocol.Parse(message.Span); Sent.Enqueue(packet);
            if (packet.Type == 2) Heartbeat.TrySetResult(packet);
            else
            {
                byte command = WrongCommand && packet.Command == (byte)LongbridgeCommand.Quote ? (byte)LongbridgeCommand.Trades : packet.Command;
                if (packet.Command == (byte)LongbridgeCommand.Heartbeat) ClientHeartbeat.TrySetResult(packet);
                byte[] body = ResponseBody?.Invoke(packet) ?? (packet.Command == (byte)LongbridgeCommand.Auth ? Array.Empty<byte>() :
                    packet.Command == (byte)LongbridgeCommand.Heartbeat ? packet.Body : new byte[] { 8, 123 });
                var response = new byte[10 + body.Length]; response[0] = 2; response[1] = command;
                BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(2), packet.RequestId);
                response[9] = (byte)body.Length; body.CopyTo(response.AsSpan(10));
                Incoming.Writer.TryWrite(response);
                if (HoldQuoteSendUntilAbort && packet.Command == (byte)LongbridgeCommand.Quote) return _abort.Task;
            }
            return Task.CompletedTask;
        }
        public async Task<byte[]?> ReceiveAsync(CancellationToken ct) => await Incoming.Reader.ReadAsync(ct);
        public void Abort() { Aborted = true; Incoming.Writer.TryComplete(); _abort.TrySetResult(); }
        public void Dispose() => Abort();
    }
}
