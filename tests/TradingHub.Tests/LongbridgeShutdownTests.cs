using System.Net.WebSockets;
using System.Threading.Channels;
using TradingHub.Feeds;
using TradingHub.Feeds.Longbridge;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class LongbridgeShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeedDisposalWaitsForHeldRequestOrSubscriptionRestore(bool subscriptionRestore)
    {
        var settings = LongbridgeFeedTests.Settings();
        var transport = new HeldTransport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        await using var feed = new LongbridgeFeed(settings, LongbridgeFeedTests.Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        using var releaseOnExit = new ReleaseOnExit(() => transport.Release.TrySetResult());
        transport.Hold = subscriptionRestore ? LongbridgeCommand.Depth : LongbridgeCommand.Quote;
        var sink = new LongbridgeFeedTests.Sink();
        IDisposable? lease = null;
        Task? request = null;
        if (subscriptionRestore) lease = feed.Subscribe(instrument, sink);
        else request = feed.GetQuoteAsync(instrument, default);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = feed.DisposeAsync().AsTask();
        await transport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopping.IsCompleted);
        Assert.Equal(0, transport.DisposeCalls);
        transport.Release.TrySetResult();
        if (request != null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        lease?.Dispose();
        Assert.Equal(1, transport.DisposeCalls);
        Assert.Empty(sink.Depth);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => feed.GetQuoteAsync(instrument, default));
    }

    [Fact]
    public async Task FeedDisposalWaitsForAnInFlightSinkCallback()
    {
        var settings = LongbridgeFeedTests.Settings();
        var transport = new HeldTransport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        await using var feed = new LongbridgeFeed(settings, LongbridgeFeedTests.Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new BlockingSink(entered, release);
        using var lease = feed.Subscribe(instrument, sink);
        sink.Hold = true;
        var callback = Task.Run(() => transport.Emit(LongbridgeCommand.PushDepth, LongbridgeFeedTests.DepthPush(1, (1, "202.10", 30))));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = feed.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, transport.DisposeCalls);
            release.Set();
            await callback.WaitAsync(TimeSpan.FromSeconds(5));
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, transport.DisposeCalls);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionDisposalCancelsButDrainsHeldExchangeOrRefreshBeforeReturning(bool refresh)
    {
        string? secret = null;
        int writes = 0;
        var handler = new LongbridgeOAuthTests.HttpFixture
        { Reply = _ => Task.FromResult(LongbridgeOAuthTests.Json("{\"access_token\":\"accepted\",\"refresh_token\":\"saved-refresh\",\"expires_in\":120}")) };
        using var http = new HttpClient(handler);
        var clock = new LongbridgeOAuthTests.Clock();
        await using var session = new LongbridgeSession(LongbridgeFeedTests.Settings(), http, () => secret,
            value => { secret = value; Interlocked.Increment(ref writes); }, clock);
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        if (refresh) await session.CompleteAuthorizationAsync(LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default);
        string? original = secret;
        int originalWrites = writes;
        clock.Utc = clock.Utc.AddSeconds(90);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Reply = _ => { entered.TrySetResult(); return reply.Task; };
        using var releaseOnExit = new ReleaseOnExit(() =>
        {
            if (!reply.Task.IsCompleted)
                reply.TrySetResult(LongbridgeOAuthTests.Json("{\"access_token\":\"cleanup\",\"refresh_token\":\"cleanup-refresh\",\"expires_in\":3600}"));
        });
        Task held = refresh ? session.AccessTokenAsync(default) : session.CompleteAuthorizationAsync(
            LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = session.DisposeAsync().AsTask();
        Assert.True(session.LifetimeToken.IsCancellationRequested);
        Assert.True(session.IsRetired);
        Assert.False(stopping.IsCompleted);
        reply.SetResult(LongbridgeOAuthTests.Json("{\"access_token\":\"late\",\"refresh_token\":\"late-refresh\",\"expires_in\":3600}"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original, secret);
        Assert.Equal(originalWrites, writes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AccessTokenAsync(default));
    }

    [Fact]
    public async Task SessionDisposalWaitsForCredentialPublicationCallbacksWithoutClearingSavedTokens()
    {
        string? secret = null;
        var handler = new LongbridgeOAuthTests.HttpFixture
        { Reply = _ => Task.FromResult(LongbridgeOAuthTests.Json("{\"access_token\":\"accepted\",\"refresh_token\":\"saved\",\"expires_in\":3600}")) };
        using var http = new HttpClient(handler);
        await using var session = new LongbridgeSession(LongbridgeFeedTests.Settings(), http, () => secret, value => secret = value,
            new LongbridgeOAuthTests.Clock());
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CredentialsChanged += _ => { entered.TrySetResult(); release.Wait(); };
        var authorization = Task.Run(() => session.CompleteAuthorizationAsync(
            LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string saved = secret!;
            var stopping = session.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            release.Set();
            await authorization.WaitAsync(TimeSpan.FromSeconds(5));
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(saved, secret);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SocketDisposalWaitsForHeldRequestOrHeartbeatBeforeDisposingConnection(bool heartbeat)
    {
        string? secret = null;
        var handler = new LongbridgeOAuthTests.HttpFixture
        {
            Reply = request => Task.FromResult(LongbridgeOAuthTests.Json(request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"accepted\",\"refresh_token\":\"saved\",\"expires_in\":3600}"
                : "{\"data\":{\"otp\":\"fixture-otp\"}}")),
        };
        using var http = new HttpClient(handler);
        var settings = LongbridgeFeedTests.Settings();
        await using var session = new LongbridgeSession(settings, http, () => secret, value => secret = value, new LongbridgeOAuthTests.Clock());
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default);
        var connection = new HeldConnection { Hold = heartbeat ? LongbridgeCommand.Heartbeat : LongbridgeCommand.Quote };
        await using var socket = new LongbridgeSocket(session, settings, () => connection)
        { MinimumRequestInterval = TimeSpan.Zero, HeartbeatInterval = heartbeat ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromMinutes(1) };
        using var releaseOnExit = new ReleaseOnExit(() => connection.Release.TrySetResult());
        Task<byte[]> request = socket.RequestAsync((byte)LongbridgeCommand.Quote, Array.Empty<byte>(), default);
        if (heartbeat) await request;
        await connection.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = socket.DisposeAsync().AsTask();
        await connection.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopping.IsCompleted);
        Assert.Equal(0, connection.DisposeCalls);
        connection.Release.TrySetResult();
        if (!heartbeat) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, connection.DisposeCalls);
        Assert.False(connection.UsedAfterDispose);
    }

    [Fact]
    public async Task SocketDisposalWaitsForAnInFlightReaderCallback()
    {
        string? secret = null;
        var handler = new LongbridgeOAuthTests.HttpFixture
        {
            Reply = request => Task.FromResult(LongbridgeOAuthTests.Json(request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"accepted\",\"refresh_token\":\"saved\",\"expires_in\":3600}"
                : "{\"data\":{\"otp\":\"fixture-otp\"}}")),
        };
        using var http = new HttpClient(handler);
        var settings = LongbridgeFeedTests.Settings();
        await using var session = new LongbridgeSession(settings, http, () => secret, value => secret = value, new LongbridgeOAuthTests.Clock());
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default);
        var connection = new HeldConnection();
        await using var socket = new LongbridgeSocket(session, settings, () => connection)
        { MinimumRequestInterval = TimeSpan.Zero, HeartbeatInterval = TimeSpan.FromMinutes(1) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int callbacks = 0;
        socket.Push += (_, _) => { entered.TrySetResult(); release.Wait(); Interlocked.Increment(ref callbacks); };
        await socket.RequestAsync((byte)LongbridgeCommand.Quote, Array.Empty<byte>(), default);
        connection.EmitPush();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = socket.DisposeAsync().AsTask();
            await connection.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, connection.DisposeCalls);
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, callbacks);
            Assert.Equal(1, connection.DisposeCalls);
        }
        finally { release.Set(); }
    }

    private sealed class ReleaseOnExit(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private sealed class HeldTransport : ILongbridgeTransport
    {
        private readonly LongbridgeFeedTests.Transport _inner = new();
        public event Action<byte, ReadOnlyMemory<byte>>? Push { add => _inner.Push += value; remove => _inner.Push -= value; }
        public event Action<FeedStatus, string?>? Status { add => _inner.Status += value; remove => _inner.Status -= value; }
        public LongbridgeCommand? Hold;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCalls;
        public async Task<byte[]> RequestAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            if (Hold is { } held && command == (byte)held)
            {
                using var registration = ct.Register(() => Cancelled.TrySetResult());
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
            return await _inner.RequestAsync(command, body, CancellationToken.None).ConfigureAwait(false);
        }
        public void Emit(LongbridgeCommand command, byte[] body) => _inner.Emit(command, body);
        public void Dispose() { Interlocked.Increment(ref DisposeCalls); _inner.Dispose(); }
    }

    private sealed class BlockingSink(TaskCompletionSource entered, ManualResetEventSlim release) : IMarketDataSink
    {
        public bool Hold;
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
        public void OnDepth(Instrument instrument, DepthUpdate update) { if (Hold) { entered.TrySetResult(); release.Wait(); } }
    }

    private sealed class HeldConnection : ILongbridgeSocketConnection
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        public LongbridgeCommand Hold;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCalls;
        public bool UsedAfterDispose;
        public Task ConnectAsync(Uri endpoint, CancellationToken ct) => Task.CompletedTask;
        public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            if (Volatile.Read(ref DisposeCalls) > 0) UsedAfterDispose = true;
            var packet = LongbridgeProtocol.Parse(message.Span);
            if (packet.Type != 1) return;
            if (packet.Command == (byte)Hold)
            {
                using var registration = ct.Register(() => Cancelled.TrySetResult());
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
                if (Volatile.Read(ref DisposeCalls) > 0) UsedAfterDispose = true;
            }
            byte[] body = packet.Command == (byte)LongbridgeCommand.Heartbeat ? packet.Body : Array.Empty<byte>();
            _incoming.Writer.TryWrite(LongbridgeProtocol.Response(packet.Command, packet.RequestId, 0, body));
        }
        public async Task<byte[]?> ReceiveAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct).ConfigureAwait(false);
        public void EmitPush() => _incoming.Writer.TryWrite(new byte[] { 3, (byte)LongbridgeCommand.PushQuote, 0, 0, 0 });
        public void Abort() { _incoming.Writer.TryComplete(); Aborted.TrySetResult(); }
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }
}
