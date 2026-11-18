using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services.LocalApi;

/// <summary>Limits and policy of the local market-data API. Defaults are the production bounds.</summary>
public sealed class LocalMarketDataApiOptions
{
    public const int MaxRequestLineBytes = 2048;
    public const int MaxHeaderBytes = 8 * 1024;
    public const int MaxHeaderLines = 64;
    public const int MaxClientMessageBytes = 4 * 1024;

    /// <summary>Loopback TCP port; 0 selects an ephemeral port (tests).</summary>
    public int Port { get; init; }

    /// <summary>Browser origins allowed to call the API (normalized with <see cref="TryNormalizeOrigin"/>).</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = Array.Empty<string>();

    public int MaxConnections { get; init; } = 16;
    public int MaxStreams { get; init; } = 4;
    public double RequestsPerSecond { get; init; } = 30;
    public int RequestBurst { get; init; } = 60;
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan StreamInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int StreamQueueCapacity { get; init; } = 20_000;

    /// <summary>Deadline of each response write and stream message; a client that stops reading is disconnected.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time a stream client has to answer a keep-alive ping before the stream is closed.</summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Clock of the request rate limiter.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Normalizes an absolute http/https origin without path, query, fragment or credentials to scheme://host[:port].</summary>
    public static bool TryNormalizeOrigin(string? value, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        if (text.EndsWith('/')) text = text[..^1];
        int authority = text.IndexOf("://", StringComparison.Ordinal);
        if (authority <= 0 || text.IndexOfAny(['/', '?', '#', '@', '\\'], authority + 3) >= 0) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/") return false;
        origin = uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }

    /// <summary>Valid, distinct normalized origins; every rejected entry is added to <paramref name="invalid"/>.</summary>
    public static List<string> NormalizeOrigins(IEnumerable<string>? values, List<string>? invalid = null)
    {
        var result = new List<string>();
        foreach (var value in values ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (TryNormalizeOrigin(value, out var origin))
            {
                if (!result.Contains(origin, StringComparer.Ordinal)) result.Add(origin);
            }
            else invalid?.Add(value.Trim());
        }
        return result;
    }
}

/// <summary>
/// Opt-in, read-only market-data API on 127.0.0.1 for the user's own local tools. Minimal HTTP/1.1 (one request per
/// connection) and RFC 6455 streams over a loopback <see cref="TcpListener"/>. Host and Origin are validated against
/// DNS rebinding and cross-site requests; every input and response is bounded, every write has a deadline, and the
/// projection-heavy routes run one at a time. No route mutates anything.
/// </summary>
public sealed class LocalMarketDataApi
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxTrades = 5000, DefaultTrades = 500;
    private const int MaxKlines = 5000, DefaultKlines = 1000;
    private const int MaxDepthLevels = 500, DefaultDepthLevels = 100;
    private const int MaxLiquidityMinutes = 120, DefaultLiquidityMinutes = 30;
    private const int MaxLiquidityLevels = 100, DefaultLiquidityLevels = 20;
    private const int MaxLiquiditySamples = 2000;
    private const int StreamTradesPerMessage = 5000, StreamBookLevels = 100;

    private static readonly Dictionary<string, int> Timeframes = new(StringComparer.Ordinal)
    {
        ["1m"] = 1, ["5m"] = 5, ["15m"] = 15, ["1h"] = 60, ["4h"] = 240, ["1d"] = 1440,
    };

    private readonly ILocalMarketDataSource _source;
    private readonly LocalMarketDataApiOptions _options;
    private readonly HashSet<string> _origins;
    private readonly OwnedOperationRegistry _operations = new();
    private readonly RateLimiter _limiter;
    // Candles and recorded depth project large ranges: one at a time bounds their memory and CPU.
    private readonly SemaphoreSlim _heavy = new(1, 1);
    private readonly object _gate = new();
    private TcpListener? _listener;
    private Task? _stop;
    private string? _fault;
    private int _connections;
    private int _streams;

    public LocalMarketDataApi(ILocalMarketDataSource source, LocalMarketDataApiOptions options)
    {
        _source = source;
        _options = options;
        _origins = new HashSet<string>(LocalMarketDataApiOptions.NormalizeOrigins(options.AllowedOrigins), StringComparer.Ordinal);
        _limiter = new RateLimiter(options.RequestsPerSecond, options.RequestBurst, options.Time);
    }

    /// <summary>The bound loopback endpoint once started.</summary>
    public IPEndPoint? Endpoint { get; private set; }

    /// <summary>True while the listener accepts connections: false once stopped or after the accept loop failed (see <see cref="Fault"/>).</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _listener != null && _stop == null && _fault == null; }
    }

    /// <summary>Why the listener stopped accepting on its own, or null.</summary>
    public string? Fault
    {
        get { lock (_gate) return _fault; }
    }

    /// <summary>Raised once, on the accept loop's thread, when the listener stops accepting on its own.</summary>
    public event Action? Faulted;

    /// <summary>Binds 127.0.0.1 only. Throws <see cref="SocketException"/> when the port is unavailable.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stop != null, this);
            if (_listener != null) throw new InvalidOperationException("The local market-data API is already running.");
            var listener = new TcpListener(IPAddress.Loopback, _options.Port);
            if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
            try { listener.Start(64); }
            catch
            {
                listener.Stop();
                throw;
            }
            _listener = listener;
            Endpoint = (IPEndPoint)listener.LocalEndpoint;
            _ = _operations.RunAsync(ct => AcceptLoopAsync(listener, ct));
        }
    }

    /// <summary>Stops accepting immediately, cancels every request and stream, and completes once all of them returned. Idempotent.</summary>
    public Task StopAsync()
    {
        TaskCompletionSource completion;
        TcpListener? listener;
        lock (_gate)
        {
            if (_stop != null) return _stop;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = completion.Task;
            listener = _listener;
        }
        listener?.Stop();
        _ = CompleteStopAsync(completion);
        return completion.Task;
    }

    private async Task CompleteStopAsync(TaskCompletionSource completion)
    {
        try
        {
            await _operations.StopAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private bool Stopping
    {
        get { lock (_gate) return _stop != null; }
    }

    // ===================================================================== connections

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception) when (Stopping || ct.IsCancellationRequested) { return; }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                continue; // that client gave up before it was accepted
            }
            catch (SocketException error)
            {
                // Persistent accept failures (such as too many open files) back off instead of spinning; logged once per run.
                if (++failures == 1) this.LogWarn($"Local market-data API: accepting a connection failed: {error.Message}");
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 50 << Math.Min(failures, 5))), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
            {
                Fail(listener, error.Message);
                return;
            }
            Admit(client);
        }
    }

    /// <summary>The listener stopped on its own: report it so the host shows it and the next settings apply restarts the server.</summary>
    private void Fail(TcpListener listener, string reason)
    {
        lock (_gate)
        {
            if (_stop != null) return;
            _fault = reason;
        }
        listener.Stop();
        this.LogWarn($"Local market-data API stopped accepting connections: {reason}");
        Faulted?.Invoke();
    }

    private void Admit(TcpClient client)
    {
        bool admitted = Interlocked.Increment(ref _connections) <= _options.MaxConnections;
        int released = 0;
        // Exactly once, whether the handler ran or the registry cancelled it before it started.
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            client.Dispose();
            Interlocked.Decrement(ref _connections);
        }
        try
        {
            client.NoDelay = true;
            _ = _operations.RunAsync(ct => HandleConnectionAsync(client, admitted, Release, ct))
                .ContinueWith(_ => Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception)
        {
            Release();
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, bool admitted, Action release, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            if (!admitted)
            {
                await WriteErrorAsync(stream, 503, "busy", "Too many connections.", null, ct).ConfigureAwait(false);
                await CloseGracefullyAsync(client, stream, ct).ConfigureAwait(false);
                return;
            }
            if (await ServeAsync(stream, ct).ConfigureAwait(false))
                await CloseGracefullyAsync(client, stream, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException or WebSocketException)
        {
            // client went away or the server is stopping
        }
        catch (Exception error)
        {
            this.LogWarn("Local market-data API request failed", error);
        }
        finally
        {
            release();
        }
    }

    /// <summary>Half-closes and discards any unread request bytes so the response is not lost to a reset.</summary>
    private static async Task CloseGracefullyAsync(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drain.CancelAfter(TimeSpan.FromSeconds(1));
            var buffer = new byte[1024];
            for (int total = 0; total < 64 * 1024;)
            {
                int read = await stream.ReadAsync(buffer, drain.Token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // closing anyway
        }
    }

    // ===================================================================== HTTP

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        public string Param(string name) => Query.TryGetValue(name, out var value) ? value.Trim() : "";
    }

    private readonly record struct ReadResult(HttpRequest? Request, int Status, string Code, string Message, bool Closed)
    {
        public static ReadResult Reject(int status, string code, string message) => new(null, status, code, message, false);
    }

    /// <summary>Returns true when the connection should be half-closed and drained after the response.</summary>
    private async Task<bool> ServeAsync(NetworkStream stream, CancellationToken ct)
    {
        var read = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
        if (read.Closed) return false;
        if (read.Request is not { } request)
        {
            await WriteErrorAsync(stream, read.Status, read.Code, read.Message, null, ct).ConfigureAwait(false);
            return true;
        }

        if (request.Header("Host") is not { } host)
        {
            await WriteErrorAsync(stream, 400, "bad_host", "A Host header is required.", null, ct).ConfigureAwait(false);
            return true;
        }
        if (!HostAllowed(host))
        {
            await WriteErrorAsync(stream, 421, "bad_host", "The Host header must be 127.0.0.1 or localhost with this port.", null, ct).ConfigureAwait(false);
            return true;
        }

        string? allowedOrigin = null;
        if (request.Header("Origin") is { } origin)
        {
            if (!LocalMarketDataApiOptions.TryNormalizeOrigin(origin, out var normalized) || !_origins.Contains(normalized))
            {
                await WriteErrorAsync(stream, 403, "origin_not_allowed", "This origin is not in the allowed origins list.", null, ct).ConfigureAwait(false);
                return true;
            }
            allowedOrigin = origin.Trim();
        }
        else if (string.Equals(request.Header("Sec-Fetch-Site"), "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(stream, 403, "origin_not_allowed", "Cross-site requests are not allowed.", null, ct).ConfigureAwait(false);
            return true;
        }

        if (request.Header("Transfer-Encoding") != null)
        {
            await WriteErrorAsync(stream, 413, "body_not_allowed", "Request bodies are not accepted.", allowedOrigin, ct).ConfigureAwait(false);
            return true;
        }
        if (request.Header("Content-Length") is { } contentLength)
        {
            if (!long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var bodyLength))
            {
                await WriteErrorAsync(stream, 400, "bad_request", "Invalid Content-Length.", allowedOrigin, ct).ConfigureAwait(false);
                return true;
            }
            if (bodyLength > 0)
            {
                await WriteErrorAsync(stream, 413, "body_not_allowed", "Request bodies are not accepted.", allowedOrigin, ct).ConfigureAwait(false);
                return true;
            }
        }

        if (!_limiter.TryAcquire(out int retryAfter))
        {
            await WriteResponseAsync(stream, 429, ErrorBody("rate_limited", "Too many requests."), allowedOrigin,
                [("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture))], ct).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "OPTIONS")
        {
            if (allowedOrigin == null)
            {
                await WriteErrorAsync(stream, 403, "origin_not_allowed", "Preflight requests need an allowed Origin.", null, ct).ConfigureAwait(false);
                return true;
            }
            await WriteResponseAsync(stream, 204, ReadOnlyMemory<byte>.Empty, allowedOrigin,
                [("Access-Control-Allow-Methods", "GET"), ("Access-Control-Max-Age", "600")], ct).ConfigureAwait(false);
            return true;
        }
        if (request.Method != "GET")
        {
            await WriteResponseAsync(stream, 405, ErrorBody("method_not_allowed", "This API is read-only; only GET is supported."), allowedOrigin,
                [("Allow", "GET")], ct).ConfigureAwait(false);
            return true;
        }

        if (request.Path == "/api/v1/stream")
        {
            await StreamAsync(stream, request, allowedOrigin, ct).ConfigureAwait(false);
            return false;
        }

        var (status, body) = await RouteAsync(request, ct).ConfigureAwait(false);
        await WriteResponseAsync(stream, status, body, allowedOrigin, [], ct).ConfigureAwait(false);
        return true;
    }

    private bool HostAllowed(string host)
    {
        int colon = host.LastIndexOf(':');
        if (colon <= 0 || Endpoint is not { } endpoint) return false;
        string name = host[..colon], port = host[(colon + 1)..];
        return (name.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) &&
               port == endpoint.Port.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<ReadResult> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        const int maxLine = LocalMarketDataApiOptions.MaxRequestLineBytes, maxHeaders = LocalMarketDataApiOptions.MaxHeaderBytes;
        var buffer = new byte[maxLine + 2 + maxHeaders + 4];
        int length = 0, lineEnd = -1;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HeaderTimeout);
        try
        {
            while (true)
            {
                if (lineEnd < 0)
                {
                    int crlf = buffer.AsSpan(0, length).IndexOf("\r\n"u8);
                    if (crlf > maxLine || (crlf < 0 && length > maxLine))
                        return ReadResult.Reject(414, "uri_too_long", "The request line is too long.");
                    lineEnd = crlf;
                }
                if (lineEnd >= 0)
                {
                    int end = buffer.AsSpan(lineEnd, length - lineEnd).IndexOf("\r\n\r\n"u8);
                    if (end >= 0)
                    {
                        if (end > maxHeaders) return ReadResult.Reject(431, "headers_too_large", "The request headers are too large.");
                        return Parse(Encoding.Latin1.GetString(buffer, 0, lineEnd + end));
                    }
                    if (length - lineEnd - 2 > maxHeaders) return ReadResult.Reject(431, "headers_too_large", "The request headers are too large.");
                }
                int read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0) return new ReadResult(null, 0, "", "", true);
                length += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ReadResult.Reject(408, "request_timeout", "The request headers were not received in time.");
        }
    }

    private static ReadResult Parse(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || !parts[0].All(c => c is >= 'A' and <= 'Z') ||
            !parts[1].StartsWith('/') || parts[1].Contains('#') || parts[2] is not ("HTTP/1.1" or "HTTP/1.0"))
            return ReadResult.Reject(400, "bad_request", "Malformed request line.");
        if (lines.Length - 1 > LocalMarketDataApiOptions.MaxHeaderLines)
            return ReadResult.Reject(431, "headers_too_large", "Too many request headers.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            int colon = line.IndexOf(':');
            if (colon <= 0 || line.AsSpan(0, colon).IndexOfAny(' ', '\t') >= 0) return ReadResult.Reject(400, "bad_request", "Malformed header.");
            string name = line[..colon], value = line[(colon + 1)..].Trim(' ', '\t');
            if (headers.TryGetValue(name, out var existing))
            {
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Origin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    return ReadResult.Reject(400, "bad_request", "Duplicate " + name + " header.");
                headers[name] = existing + ", " + value;
            }
            else headers[name] = value;
        }
        string target = parts[1];
        int question = target.IndexOf('?');
        string path = question < 0 ? target : target[..question];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (question >= 0)
        {
            foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                string key = Decode(equals < 0 ? pair : pair[..equals]), value = equals < 0 ? "" : Decode(pair[(equals + 1)..]);
                query.TryAdd(key, value);
            }
        }
        return new ReadResult(new HttpRequest(parts[0], path, query, headers), 0, "", "", false);
    }

    private static string Decode(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return value; }
    }

    // ===================================================================== routes

    private async Task<(int Status, ReadOnlyMemory<byte> Body)> RouteAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            switch (request.Path)
            {
                case "/api/v1/info":
                    return Result(await _source.GetInfoAsync(ct).ConfigureAwait(false), (w, info) =>
                    {
                        w.WriteStartObject();
                        w.WriteString("app", info.App);
                        w.WriteString("version", info.Version);
                        w.WriteNumber("apiVersion", 1);
                        w.WriteBoolean("readOnly", true);
                        w.WriteNumber("port", Endpoint?.Port ?? 0);
                        w.WriteEndObject();
                    });
                case "/api/v1/connections":
                    return Result(await _source.GetConnectionsAsync(ct).ConfigureAwait(false), (w, list) =>
                    {
                        w.WriteStartArray();
                        foreach (var c in list)
                        {
                            w.WriteStartObject();
                            w.WriteString("id", c.Id);
                            w.WriteString("name", c.Name);
                            w.WriteString("kind", c.Kind);
                            w.WriteBoolean("enabled", c.Enabled);
                            w.WriteString("marketData", c.MarketData);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    });
                case "/api/v1/open":
                    return Result(await _source.GetOpenInstrumentsAsync(ct).ConfigureAwait(false), (w, list) =>
                    {
                        w.WriteStartArray();
                        foreach (var o in list)
                        {
                            w.WriteStartObject();
                            w.WriteString("connection", o.Connection);
                            w.WriteString("symbol", o.Symbol);
                            w.WriteNumber("tickSize", o.TickSize);
                            if (o.LastPrice is { } last) w.WriteNumber("lastPrice", last);
                            else w.WriteNull("lastPrice");
                            w.WriteNumber("trades", o.Trades);
                            w.WriteBoolean("hasDepth", o.HasDepth);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    });
                case "/api/v1/klines":
                    return await HeavyAsync(() => KlinesAsync(request, ct), ct).ConfigureAwait(false);
                case "/api/v1/trades":
                {
                    if (!Instrument(request, out var conn, out var symbol, out var missing)) return missing;
                    if (!Bounded(request, "limit", DefaultTrades, MaxTrades, out int limit, out var bad)) return bad;
                    return Result(await _source.GetTradesAsync(conn, symbol, limit, ct).ConfigureAwait(false), (w, trades) =>
                    {
                        w.WriteStartArray();
                        foreach (var trade in trades.Count > limit ? trades.Skip(trades.Count - limit) : trades) WriteTrade(w, trade);
                        w.WriteEndArray();
                    });
                }
                case "/api/v1/depth":
                {
                    if (!Instrument(request, out var conn, out var symbol, out var missing)) return missing;
                    if (!Bounded(request, "levels", DefaultDepthLevels, MaxDepthLevels, out int levels, out var bad)) return bad;
                    return Result(await _source.GetDepthAsync(conn, symbol, levels, ct).ConfigureAwait(false), (w, book) =>
                    {
                        w.WriteStartObject();
                        WriteBook(w, book, levels);
                        w.WriteEndObject();
                    });
                }
                case "/api/v1/liquidity":
                    return await HeavyAsync(() => LiquidityAsync(request, ct), ct).ConfigureAwait(false);
                default:
                    return (404, ErrorBody("not_found", "Unknown endpoint."));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            this.LogWarn($"Local market-data API: {request.Path} failed: {error.Message}");
            return (502, ErrorBody("provider_error", "The market-data request failed."));
        }
    }

    /// <summary>Runs a projection-heavy route alone, so local clients cannot run several large projections at once.</summary>
    private async Task<(int Status, ReadOnlyMemory<byte> Body)> HeavyAsync(Func<Task<(int Status, ReadOnlyMemory<byte> Body)>> route, CancellationToken ct)
    {
        await _heavy.WaitAsync(ct).ConfigureAwait(false);
        try { return await route().ConfigureAwait(false); }
        finally { _heavy.Release(); }
    }

    private async Task<(int Status, ReadOnlyMemory<byte> Body)> LiquidityAsync(HttpRequest request, CancellationToken ct)
    {
        if (!Instrument(request, out var conn, out var symbol, out var missing)) return missing;
        if (!Bounded(request, "minutes", DefaultLiquidityMinutes, MaxLiquidityMinutes, out int minutes, out var bad)) return bad;
        if (!Bounded(request, "levels", DefaultLiquidityLevels, MaxLiquidityLevels, out int levels, out bad)) return bad;
        var result = await _source.GetLiquidityAsync(conn, symbol, TimeSpan.FromMinutes(minutes), levels, MaxLiquiditySamples, ct).ConfigureAwait(false);
        return Result(result, (w, liquidity) =>
        {
            int skip = Math.Max(0, liquidity.Samples.Count - MaxLiquiditySamples);
            w.WriteStartObject();
            w.WriteBoolean("recording", liquidity.Recording);
            w.WriteNumber("tickSize", liquidity.TickSize);
            w.WriteBoolean("truncated", liquidity.Truncated || skip > 0);
            w.WriteStartArray("samples");
            for (int i = skip; i < liquidity.Samples.Count; i++)
            {
                var sample = liquidity.Samples[i];
                w.WriteStartObject();
                w.WriteNumber("time", sample.TimeMs);
                w.WriteBoolean("usable", sample.Usable);
                w.WriteBoolean("gapBefore", sample.GapBefore);
                WriteLevels(w, "bids", sample.Bids, levels);
                WriteLevels(w, "asks", sample.Asks, levels);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private async Task<(int Status, ReadOnlyMemory<byte> Body)> KlinesAsync(HttpRequest request, CancellationToken ct)
    {
        if (!Instrument(request, out var conn, out var symbol, out var missing)) return missing;
        string tf = request.Param("tf");
        if (tf.Length == 0) tf = "1m";
        if (!Timeframes.TryGetValue(tf, out int minutes))
            return (400, ErrorBody("bad_timeframe", "tf must be one of 1m, 5m, 15m, 1h, 4h, 1d."));
        if (!Bounded(request, "limit", DefaultKlines, MaxKlines, out int limit, out var bad)) return bad;
        if (!Millis(request, "to", out var to, out bad) || !Millis(request, "from", out var from, out bad)) return bad;
        var toUtc = to ?? _options.Time.GetUtcNow().UtcDateTime;
        var earliest = toUtc - TimeSpan.FromMinutes((double)minutes * limit);
        var fromUtc = from is { } f && f > earliest ? f : earliest;
        if (fromUtc >= toUtc) return (400, ErrorBody("bad_range", "from must be before to."));
        var result = await _source.GetKlinesAsync(new ApiKlineRequest(conn, symbol, minutes, fromUtc, toUtc), ct).ConfigureAwait(false);
        return Result(result, (w, bars) =>
        {
            w.WriteStartArray();
            foreach (var k in bars.Count > limit ? bars.Skip(bars.Count - limit) : bars)
            {
                w.WriteStartArray();
                w.WriteNumberValue(k.OpenTimeMs);
                w.WriteNumberValue(k.Open);
                w.WriteNumberValue(k.High);
                w.WriteNumberValue(k.Low);
                w.WriteNumberValue(k.Close);
                w.WriteNumberValue(k.Volume);
                if (k.BuyVolume is { } buy) w.WriteNumberValue(buy);
                else w.WriteNullValue();
                w.WriteEndArray();
            }
            w.WriteEndArray();
        });
    }

    private static bool Instrument(HttpRequest request, out string conn, out string symbol, out (int, ReadOnlyMemory<byte>) error)
    {
        conn = request.Param("conn");
        symbol = request.Param("symbol");
        bool valid = conn.Length is > 0 and <= 128 && symbol.Length is > 0 and <= 64;
        error = valid ? default : (400, ErrorBody("bad_request", "conn and symbol are required."));
        return valid;
    }

    private static bool Bounded(HttpRequest request, string name, int fallback, int max, out int value, out (int, ReadOnlyMemory<byte>) error)
    {
        error = default;
        string text = request.Param(name);
        if (text.Length == 0)
        {
            value = fallback;
            return true;
        }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = (400, ErrorBody("bad_request", name + " must be an integer."));
            return false;
        }
        value = Math.Clamp(value, 1, max);
        return true;
    }

    private static bool Millis(HttpRequest request, string name, out DateTime? value, out (int, ReadOnlyMemory<byte>) error)
    {
        value = null;
        error = default;
        string text = request.Param(name);
        if (text.Length == 0) return true;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) || ms < 0 || ms > 253_402_300_799_999)
        {
            error = (400, ErrorBody("bad_request", name + " must be Unix milliseconds."));
            return false;
        }
        value = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return true;
    }

    private static (int Status, ReadOnlyMemory<byte> Body) Result<T>(ApiResult<T> result, Action<Utf8JsonWriter, T> write) => result.Status switch
    {
        ApiResultStatus.Ok when result.Value is { } value => (200, Json(w => write(w, value))),
        ApiResultStatus.Ok => (500, ErrorBody("internal", "The source returned no value.")),
        ApiResultStatus.NotFound or ApiResultStatus.NotOpen => (404, ErrorBody(result.Code, result.Message)),
        _ => (503, ErrorBody(result.Code.Length == 0 ? "unavailable" : result.Code, result.Message)),
    };

    // ===================================================================== WebSocket stream

    private async Task StreamAsync(NetworkStream stream, HttpRequest request, string? allowedOrigin, CancellationToken ct)
    {
        bool upgrade = string.Equals(request.Header("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase) &&
                       (request.Header("Connection") ?? "").Split(',').Any(token => token.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase));
        if (!upgrade)
        {
            await WriteResponseAsync(stream, 426, ErrorBody("websocket_required", "This endpoint requires a WebSocket upgrade."), allowedOrigin,
                [("Upgrade", "websocket")], ct).ConfigureAwait(false);
            return;
        }
        if (request.Header("Sec-WebSocket-Version") != "13")
        {
            await WriteResponseAsync(stream, 426, ErrorBody("websocket_version", "Only WebSocket version 13 is supported."), allowedOrigin,
                [("Sec-WebSocket-Version", "13")], ct).ConfigureAwait(false);
            return;
        }
        string key = request.Header("Sec-WebSocket-Key") ?? "";
        var decoded = new byte[24];
        if (!Convert.TryFromBase64String(key, decoded, out int keyBytes) || keyBytes != 16)
        {
            await WriteErrorAsync(stream, 400, "bad_request", "Invalid Sec-WebSocket-Key.", allowedOrigin, ct).ConfigureAwait(false);
            return;
        }
        if (!Instrument(request, out var conn, out var symbol, out var missing))
        {
            await WriteResponseAsync(stream, missing.Item1, missing.Item2, allowedOrigin, [], ct).ConfigureAwait(false);
            return;
        }
        if (Interlocked.Increment(ref _streams) > _options.MaxStreams)
        {
            Interlocked.Decrement(ref _streams);
            await WriteErrorAsync(stream, 503, "too_many_streams", "Too many open streams.", allowedOrigin, ct).ConfigureAwait(false);
            return;
        }
        try
        {
            var queue = new TradeQueue(Math.Max(1, _options.StreamQueueCapacity));
            var book = new BookSlot();
            ApiResult<ApiStreamSubscription> result;
            try
            {
                result = await _source.SubscribeStreamAsync(conn, symbol, queue.Add, latest => Volatile.Write(ref book.Latest, latest), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                this.LogWarn($"Local market-data API: stream subscription failed: {error.Message}");
                await WriteErrorAsync(stream, 502, "provider_error", "The market-data request failed.", allowedOrigin, ct).ConfigureAwait(false);
                return;
            }
            if (result.Status != ApiResultStatus.Ok || result.Value is not { } subscription)
            {
                var (status, body) = Result(result, (_, _) => { });
                await WriteResponseAsync(stream, status, body, allowedOrigin, [], ct).ConfigureAwait(false);
                return;
            }
            using (subscription.Subscription)
            {
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
                byte[] handshake = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                await WriteBoundedAsync(stream, handshake, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                // A peer that stops answering pings (connected but not reading) is closed instead of holding a stream slot.
                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = _options.KeepAliveTimeout,
                });
                await RunStreamAsync(socket, subscription.Symbol, queue, book, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _streams);
        }
    }

    private async Task RunStreamAsync(WebSocket socket, string symbol, TradeQueue queue, BookSlot book, CancellationToken ct)
    {
        using var receiveStop = new CancellationTokenSource();
        var receive = ReceiveLoopAsync(socket, receiveStop.Token);
        try
        {
            using var timer = new PeriodicTimer(_options.StreamInterval);
            while (!receive.IsCompleted && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await FlushAsync(socket, symbol, queue, book, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // stopping, or the client went away
        }
        finally
        {
            var status = receive.IsCompletedSuccessfully && receive.Result ? WebSocketCloseStatus.MessageTooBig : WebSocketCloseStatus.NormalClosure;
            try
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(status, ct.IsCancellationRequested ? "Server stopping" : "", closing.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // the peer is gone
            }
            // Wait briefly for the client's close acknowledgement; never longer than the bound.
            receiveStop.CancelAfter(TimeSpan.FromSeconds(2));
            await receive.ConfigureAwait(false);
        }
    }

    /// <summary>Discards client messages; returns true when a message exceeded the size bound.</summary>
    private static async Task<bool> ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[LocalMarketDataApiOptions.MaxClientMessageBytes];
        int messageBytes = 0;
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close) return false;
                messageBytes += received.Count;
                if (messageBytes > LocalMarketDataApiOptions.MaxClientMessageBytes) return true;
                if (received.EndOfMessage) messageBytes = 0;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task FlushAsync(WebSocket socket, string symbol, TradeQueue queue, BookSlot book, CancellationToken ct)
    {
        var trades = queue.Take(StreamTradesPerMessage, out long dropped);
        if (dropped > 0)
            await SendAsync(socket, Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "gap");
                w.WriteString("symbol", symbol);
                w.WriteNumber("dropped", dropped);
                w.WriteEndObject();
            }), ct).ConfigureAwait(false);
        if (trades.Count > 0)
            await SendAsync(socket, Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "trades");
                w.WriteString("symbol", symbol);
                w.WriteStartArray("data");
                foreach (var trade in trades) WriteTrade(w, trade);
                w.WriteEndArray();
                w.WriteEndObject();
            }), ct).ConfigureAwait(false);
        if (Interlocked.Exchange(ref book.Latest, null) is { } latest)
        {
            var snapshot = latest(StreamBookLevels);
            await SendAsync(socket, Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "book");
                w.WriteString("symbol", symbol);
                WriteBook(w, snapshot, StreamBookLevels);
                w.WriteEndObject();
            }), ct).ConfigureAwait(false);
        }
    }

    /// <summary>One stream message within the write deadline; a client that stops reading aborts its stream.</summary>
    private async Task SendAsync(WebSocket socket, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.WriteTimeout);
        await socket.SendAsync(message, WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Bounded trade queue: overflow drops the oldest trades and reports how many were dropped.</summary>
    private sealed class TradeQueue(int capacity)
    {
        private readonly Queue<ApiTrade> _items = new();
        private long _dropped;

        public void Add(ApiTradeBatch batch)
        {
            lock (_items)
            {
                _dropped += batch.Skipped;
                foreach (var trade in batch.Trades)
                {
                    if (_items.Count >= capacity)
                    {
                        _items.Dequeue();
                        _dropped++;
                    }
                    _items.Enqueue(trade);
                }
            }
        }

        public List<ApiTrade> Take(int max, out long dropped)
        {
            lock (_items)
            {
                dropped = _dropped;
                _dropped = 0;
                var result = new List<ApiTrade>(Math.Min(max, _items.Count));
                while (result.Count < max && _items.TryDequeue(out var trade)) result.Add(trade);
                return result;
            }
        }
    }

    private sealed class BookSlot
    {
        public Func<int, ApiBook>? Latest;
    }

    // ===================================================================== JSON and responses

    private static void WriteTrade(Utf8JsonWriter w, ApiTrade trade)
    {
        w.WriteStartArray();
        w.WriteNumberValue(trade.TimeMs);
        w.WriteNumberValue(trade.Price);
        w.WriteNumberValue(trade.Size);
        if (trade.Side is { } side) w.WriteStringValue(side);
        else w.WriteNullValue();
        if (trade.Venue is { } venue) w.WriteStringValue(venue);
        else w.WriteNullValue();
        w.WriteEndArray();
    }

    private static void WriteBook(Utf8JsonWriter w, ApiBook book, int levels)
    {
        if (book.TimeMs is { } time) w.WriteNumber("time", time);
        else w.WriteNull("time");
        WriteLevels(w, "bids", book.Bids, levels);
        WriteLevels(w, "asks", book.Asks, levels);
    }

    private static void WriteLevels(Utf8JsonWriter w, string name, IReadOnlyList<ApiBookLevel> levels, int max)
    {
        w.WriteStartArray(name);
        for (int i = 0; i < levels.Count && i < max; i++)
        {
            w.WriteStartArray();
            w.WriteNumberValue(levels[i].Price);
            w.WriteNumberValue(levels[i].Size);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    /// <summary>The written JSON without a further copy.</summary>
    private static ReadOnlyMemory<byte> Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return buffer.WrittenMemory;
    }

    private static ReadOnlyMemory<byte> ErrorBody(string code, string message) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteStartObject("error");
        w.WriteString("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    private Task WriteErrorAsync(Stream stream, int status, string code, string message, string? origin, CancellationToken ct) =>
        WriteResponseAsync(stream, status, ErrorBody(code, message), origin, [], ct);

    private Task WriteResponseAsync(Stream stream, int status, ReadOnlyMemory<byte> body, string? origin,
        IReadOnlyList<(string Name, string Value)> extra, CancellationToken ct)
    {
        var head = new StringBuilder(256);
        head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(status)).Append("\r\n");
        if (status != 204)
        {
            head.Append("Content-Type: application/json; charset=utf-8\r\n");
            head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }
        head.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n");
        if (origin != null) head.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\nVary: Origin\r\n");
        foreach (var (name, value) in extra) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");
        return WriteBoundedAsync(stream, Encoding.ASCII.GetBytes(head.ToString()), status == 204 ? ReadOnlyMemory<byte>.Empty : body, ct);
    }

    /// <summary>Head then body, both within the write deadline: a client that stops reading releases its connection slot.</summary>
    private async Task WriteBoundedAsync(Stream stream, ReadOnlyMemory<byte> head, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.WriteTimeout);
        await stream.WriteAsync(head, deadline.Token).ConfigureAwait(false);
        if (!body.IsEmpty) await stream.WriteAsync(body, deadline.Token).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        413 => "Content Too Large",
        414 => "URI Too Long",
        421 => "Misdirected Request",
        426 => "Upgrade Required",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Internal Server Error",
    };

    /// <summary>Token bucket shared by every client (all of them are local).</summary>
    private sealed class RateLimiter(double rate, int burst, TimeProvider time)
    {
        private readonly object _gate = new();
        private readonly double _rate = Math.Max(rate, 0.001);
        private readonly double _burst = Math.Max(burst, 1);
        private double _tokens = Math.Max(burst, 1);
        private DateTimeOffset _last = time.GetUtcNow();

        public bool TryAcquire(out int retryAfterSeconds)
        {
            lock (_gate)
            {
                var now = time.GetUtcNow();
                double elapsed = (now - _last).TotalSeconds;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(_burst, _tokens + elapsed * _rate);
                    _last = now;
                }
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    retryAfterSeconds = 0;
                    return true;
                }
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((1 - _tokens) / _rate));
                return false;
            }
        }
    }
}
