// Quote framing/endpoints adapted from kvp61/trading-hub at 50220ba91613161685f84384825d855cb4dbeb76,
// original ralphtradesz/trading-hub, MIT with Commons Clause. Reconnect always obtains a fresh generation-bound OTP.
using System.Collections.Concurrent;
using System.Net.WebSockets;
using TradingHub.Networking;

namespace TradingHub.Feeds.Longbridge;

public interface ILongbridgeTransport : IDisposable
{
    event Action<byte, ReadOnlyMemory<byte>>? Push;
    event Action<FeedStatus, string?>? Status;
    Task<byte[]> RequestAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct);
}

/// <summary>Whole-binary-message socket seam. Null receive denotes remote close. Implementations must honor cancellation.</summary>
public interface ILongbridgeSocketConnection : IDisposable
{
    Task ConnectAsync(Uri endpoint, CancellationToken ct);
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);
    Task<byte[]?> ReceiveAsync(CancellationToken ct);
    void Abort();
}

public sealed class LongbridgeWebSocketConnection : ILongbridgeSocketConnection
{
    private readonly ClientWebSocket _socket;
    private readonly HttpMessageInvoker _invoker;
    private int _disposed;
    /// <summary>Owns both transports. Handshake routing and redirect policy belong to the supplied invoker.</summary>
    public LongbridgeWebSocketConnection(ClientWebSocket socket, HttpMessageInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(invoker);
        _socket = socket;
        _invoker = invoker;
        _socket.Options.Proxy = null;
    }
    public Task ConnectAsync(Uri endpoint, CancellationToken ct) => _socket.ConnectAsync(endpoint, _invoker, ct);
    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
        await _socket.SendAsync(message, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16384];
        ValueWebSocketReceiveResult part;
        do
        {
            part = await _socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Longbridge quote transport received a nonbinary message.");
            if (message.Length + part.Count > LongbridgeProtocol.MaximumBodyLength + 11) throw new InvalidDataException("Longbridge socket message exceeds the size limit.");
            message.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return message.ToArray();
    }
    public void Abort() => _socket.Abort();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _socket.Dispose(); }
        finally { _invoker.Dispose(); }
    }
}

/// <summary>Owned reconnect, request correlation, heartbeat and bounded framing. The feed restores subscriptions after its provider baseline.</summary>
public sealed class LongbridgeSocket : ILongbridgeTransport, IAsyncDisposable
{
    private readonly LongbridgeSession _session;
    private readonly Uri _endpoint;
    private readonly Func<ILongbridgeSocketConnection> _factory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LongbridgeOwnedTasks _owned;
    private readonly object _stopGate = new();
    private Task? _stop;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly SemaphoreSlim _rate = new(1, 1);
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<uint, Pending> _pending = new();
    private sealed record Pending(byte Command, TaskCompletionSource<LongbridgePacket> Completion);
    private TaskCompletionSource _ready = Ready();
    private Task? _run;
    private ILongbridgeSocketConnection? _connection;
    private LongbridgeOwnedTasks? _connectionOwned;
    private long _connectionGeneration;
    private int _id;
    private bool _disposed;
    private DateTimeOffset _lastRequest;

    public LongbridgeSocket(LongbridgeSession session, LongbridgeSettings settings,
        Func<ILongbridgeSocketConnection>? connectionFactory = null, Func<ClientWebSocket>? socketFactory = null,
        ProxyHandlerFactory? network = null)
    {
        _session = session;
        _owned = new(nameof(LongbridgeSocket), _lifetime);
        var builder = new UriBuilder(settings.QuoteEndpoint);
        builder.Query = (builder.Query.TrimStart('?') is { Length: > 0 } query ? query + "&" : "") + "version=1&codec=1&platform=9";
        _endpoint = builder.Uri;
        if (connectionFactory != null) _factory = connectionFactory;
        else
        {
            var capturedNetwork = network ?? new ProxyHandlerFactory(new NetworkProxyPolicy());
            _factory = () =>
            {
                var socket = socketFactory?.Invoke() ?? new ClientWebSocket();
                HttpMessageInvoker? invoker = null;
                try
                {
                    invoker = capturedNetwork.CreateWebSocketInvoker();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    return new LongbridgeWebSocketConnection(socket, invoker);
                }
                catch
                {
                    try { socket.Dispose(); }
                    finally { invoker?.Dispose(); }
                    throw;
                }
            };
        }
        _session.CredentialsChanged += CredentialsChanged;
    }

    public event Action<byte, ReadOnlyMemory<byte>>? Push;
    public event Action<FeedStatus, string?>? Status;
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromMilliseconds(120);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource Ready() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void CredentialsChanged(long generation)
    {
        if (!_owned.TryEnter(out var admission)) return;
        using var callback = admission;
        if (_session.IsRetired) { Dispose(); return; }
        ILongbridgeSocketConnection? connection;
        LongbridgeOwnedTasks? owned;
        lock (_sync)
        {
            if (generation == _connectionGeneration) return;
            connection = _connection; owned = _connectionOwned;
        }
        if (owned != null) _ = owned.CloseAsync();
        connection?.Abort();
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        Task ready;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _run ??= _owned.Track(Task.Run(RunAsync));
            ready = _ready.Task;
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        wait.CancelAfter(RequestTimeout);
        try { await ready.WaitAsync(wait.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TimeoutException("Longbridge quote connection did not become ready."); }
    }

    public Task<byte[]> RequestAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(RequestCoreAsync(command, body, ct));
    }
    private async Task<byte[]> RequestCoreAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _owned.Token);
        ct = request.Token;
        if (command == (byte)LongbridgeCommand.Unsubscribe)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_ready.Task.IsCompletedSuccessfully) return Array.Empty<byte>();
            }
        }
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _rate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var delay = MinimumRequestInterval - (_session.UtcNow - _lastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
            _lastRequest = _session.UtcNow;
            EnsureGeneration();
            return await RawAsync(command, body, ct).ConfigureAwait(false);
        }
        finally { _rate.Release(); }
    }


    private void EnsureGeneration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session.IsRetired || _session.CredentialGeneration != _connectionGeneration)
        {
            ILongbridgeSocketConnection? connection;
            LongbridgeOwnedTasks? owned;
            lock (_sync) { connection = _connection; owned = _connectionOwned; }
            if (owned != null) _ = owned.CloseAsync();
            connection?.Abort();
            throw new InvalidOperationException("Longbridge quote connection belongs to retired credentials.");
        }
    }

    private async Task RunAsync()
    {
        int failures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            ILongbridgeSocketConnection? connection = null;
            Task? reader = null;
            Task? heartbeat = null;
            using var lifetime = new CancellationTokenSource();
            var connectionOwned = new LongbridgeOwnedTasks(nameof(LongbridgeWebSocketConnection), lifetime);
            Exception? failure = null;
            try
            {
                lock (_sync)
                {
                    _connectionOwned = connectionOwned;
                    ObjectDisposedException.ThrowIf(_disposed, this);
                }
                Status?.Invoke(failures == 0 ? FeedStatus.Connecting : FeedStatus.Reconnecting, "Connecting Longbridge data-only quote session.");
                var credential = await _session.SocketOtpAsync(lifetime.Token).ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_disposed, this);
                connection = _factory();
                lock (_sync)
                {
                    _connection = connection; _connectionGeneration = credential.Generation;
                    if (_disposed) throw new ObjectDisposedException(nameof(LongbridgeSocket));
                }
                EnsureGeneration();
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    connect.CancelAfter(RequestTimeout);
                    await connection.ConnectAsync(_endpoint, connect.Token).ConfigureAwait(false);
                }
                using (var startup = connectionOwned.Enter()) reader = connectionOwned.Track(ReadAsync(connection, lifetime.Token));
                await RawAsync((byte)LongbridgeCommand.Auth, LbMessages.Symbol(credential.Otp), lifetime.Token).ConfigureAwait(false);
                EnsureGeneration();
                lock (_sync) _ready.TrySetResult();
                Status?.Invoke(FeedStatus.Connected, "Longbridge quote data; entitlement and delivery completeness are not assumed.");
                failures = 0;
                using (var startup = connectionOwned.Enter()) heartbeat = connectionOwned.Track(HeartbeatAsync(lifetime.Token));
                var stopped = await Task.WhenAny(reader, heartbeat).ConfigureAwait(false);
                await stopped.ConfigureAwait(false);
                throw new IOException("Longbridge quote socket closed; data continuity is interrupted.");
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception ex) { failure = ex; failures++; }
            finally
            {
                var connectionDrained = connectionOwned.CloseAsync();
                try { connection?.Abort(); }
                finally
                {
                    FailPending(failure ?? new ObjectDisposedException(nameof(LongbridgeSocket)));
                    try
                    {
                        await Task.WhenAll(connectionDrained, _owned.CancellationCompletion).ConfigureAwait(false);
                    }
                    finally
                    {
                        try { connection?.Dispose(); }
                        finally
                        {
                            lock (_sync)
                            {
                                _connection = null; _connectionOwned = null; _connectionGeneration = 0;
                                _ready.TrySetException(failure ?? new ObjectDisposedException(nameof(LongbridgeSocket)));
                                if (!_disposed) _ready = Ready();
                            }
                        }
                    }
                }
            }
            if (_lifetime.IsCancellationRequested) break;
            Status?.Invoke(FeedStatus.Reconnecting, failure?.Message ?? "Longbridge quote connection interrupted.");
            try { await Task.Delay(LiveSubscription.Backoff(failures), _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
        }
        Status?.Invoke(FeedStatus.Disconnected, "Longbridge quote connection stopped.");
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        if (HeartbeatInterval <= TimeSpan.Zero) throw new InvalidOperationException("Longbridge heartbeat interval must be positive.");
        while (true)
        {
            await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
            EnsureGeneration();
            var writer = new LbWriter();
            writer.Number(1, _session.UtcNow.ToUnixTimeMilliseconds());
            byte[] request = writer.Build();
            byte[] echo = await RawAsync((byte)LongbridgeCommand.Heartbeat, request, ct).ConfigureAwait(false);
            if (!echo.AsSpan().SequenceEqual(request)) throw new InvalidDataException("Longbridge heartbeat response did not echo its request.");
        }
    }

    private async Task ReadAsync(ILongbridgeSocketConnection connection, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                byte[]? bytes = await connection.ReceiveAsync(ct).ConfigureAwait(false);
                if (bytes == null) throw new IOException("Longbridge quote socket closed; data continuity is interrupted.");
                EnsureGeneration();
                var packet = LongbridgeProtocol.Parse(bytes);
                if (packet.Type == 2)
                {
                    if (_pending.TryRemove(packet.RequestId, out var pending))
                    {
                        if (pending.Command != packet.Command)
                        {
                            var error = new InvalidDataException("Longbridge response command does not match its request.");
                            pending.Completion.TrySetException(error);
                            throw error;
                        }
                        pending.Completion.TrySetResult(packet);
                    }
                }
                else if (packet.Type == 3)
                {
                    if (packet.Command == (byte)LongbridgeCommand.Close) throw new IOException("Longbridge server closed the data session.");
                    Push?.Invoke(packet.Command, packet.Body);
                }
                else if (packet.Command == (byte)LongbridgeCommand.Heartbeat)
                    await SendAsync(connection, LongbridgeProtocol.Response(packet.Command, packet.RequestId, 0, packet.Body), ct).ConfigureAwait(false);
                else if (packet.Command == (byte)LongbridgeCommand.Close)
                    throw new IOException("Longbridge server closed the data session.");
                else throw new InvalidDataException("Unsupported Longbridge server request.");
            }
        }
        catch (Exception ex) { FailPending(ex); throw; }
    }

    private Task<byte[]> RawAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        ILongbridgeSocketConnection connection;
        LongbridgeOwnedTasks owned;
        lock (_sync)
        {
            connection = _connection ?? throw new IOException("Longbridge quote socket is not connected.");
            owned = _connectionOwned ?? throw new IOException("Longbridge quote context is not connected.");
        }
        using var admission = owned.Enter();
        return owned.Track(RawCoreAsync(connection, owned.Token, command, body, ct));
    }
    private async Task<byte[]> RawCoreAsync(ILongbridgeSocketConnection connection, CancellationToken connectionToken,
        byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        uint id = unchecked((uint)Interlocked.Increment(ref _id));
        var completion = new TaskCompletionSource<LongbridgePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new(command, completion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, connectionToken);
        timeout.CancelAfter(RequestTimeout);
        // Arbitrate response, send failure and cancellation once; teardown cannot replace a received protocol error.
        using var cancellation = timeout.Token.UnsafeRegister(static (state, token) =>
            ((TaskCompletionSource<LongbridgePacket>)state!).TrySetCanceled(token), completion);
        try
        {
            ushort timeoutMs = (ushort)Math.Clamp(RequestTimeout.TotalMilliseconds, 1, ushort.MaxValue);
            try
            {
                await SendAsync(connection, LongbridgeProtocol.Request(command, id, timeoutMs, body.Span), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception sendFailure) { completion.TrySetException(sendFailure); }
            var packet = await completion.Task.ConfigureAwait(false);
            EnsureGeneration();
            if (packet.Status == 0) return packet.Body;
            var error = LbMessages.Error(packet.Body);
            throw new LongbridgeException(packet.Status, error.Code, command == (byte)LongbridgeCommand.Auth
                ? "Quote-session authentication was rejected; response credentials are not logged." : error.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !connectionToken.IsCancellationRequested)
        { throw new TimeoutException($"Longbridge quote command {command} timed out."); }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task SendAsync(ILongbridgeSocketConnection connection, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await connection.SendAsync(message, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }
    private void FailPending(Exception error)
    {
        foreach (var pair in _pending)
        {
            if (!_pending.TryRemove(pair.Key, out var pending)) continue;
            if (error is OperationCanceledException canceled)
                pending.Completion.TrySetCanceled(canceled.CancellationToken);
            else pending.Completion.TrySetException(error);
        }
    }
    public void Dispose() => _ = BeginStop();
    public ValueTask DisposeAsync() => new(BeginStop());
    private Task BeginStop()
    {
        lock (_stopGate)
        {
            if (_stop != null) return _stop;
            Volatile.Write(ref _disposed, true);
            var drained = _owned.CloseAsync();
            FailPending(new OperationCanceledException(_owned.Token));
            _stop = Task.Run(() => CompleteStopAsync(drained));
            return _stop;
        }
    }
    private async Task CompleteStopAsync(Task drained)
    {
        try
        {
            try
            {
                _session.CredentialsChanged -= CredentialsChanged;
                ILongbridgeSocketConnection? connection;
                LongbridgeOwnedTasks? owned;
                lock (_sync)
                {
                    connection = _connection; owned = _connectionOwned;
                    _ready.TrySetCanceled(_owned.Token);
                }
                if (owned != null) _ = owned.CloseAsync();
                connection?.Abort();
            }
            finally { await drained.ConfigureAwait(false); }
        }
        finally
        {
            try { if (_run != null) await _run.ConfigureAwait(false); }
            finally { _send.Dispose(); _rate.Dispose(); _lifetime.Dispose(); }
        }
    }
}

public sealed class LongbridgeException : Exception
{
    public LongbridgeException(byte status, long code, string message) : base($"Longbridge quote request failed (status {status}, code {code}): {message}") { Status = status; Code = code; }
    public byte Status { get; }
    public long Code { get; }
}
