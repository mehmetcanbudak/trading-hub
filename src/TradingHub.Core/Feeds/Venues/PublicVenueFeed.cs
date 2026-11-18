// Behavior adapted from the KVP capability reference (commit 239bdb8); see THIRD_PARTY_NOTICES.md.
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Venues;

/// <summary>A crypto market, for spot/perpetual filtering and per-exchange flow grouping.</summary>
public interface ICryptoVenue
{
    /// <summary>Exchange name shared by its spot and derivative markets (Binance Futures and Binance Spot are both "Binance").</summary>
    string Venue { get; }

    bool IsSpot { get; }

    /// <summary>Quote preferred when one venue lists the same base in several USD-family quotes.</summary>
    string PreferredQuote => "USDT";
}

/// <summary>
/// Caller-supplied transport for a public venue feed. Injected HTTP is caller-owned unless <see cref="OwnsHttp"/> is set;
/// the socket connector and delay are no-network test seams.
/// </summary>
public sealed record PublicVenueTransport(
    HttpClient? Http = null,
    bool OwnsHttp = false,
    Action<ClientWebSocketOptions>? ConfigureSocket = null,
    Func<Uri, CancellationToken, Task<WebSocket>>? ConnectWebSocket = null,
    Uri? RestOrigin = null,
    Func<TimeSpan, CancellationToken, Task>? Delay = null,
    TimeProvider? Clock = null);

/// <summary>Venue metadata needed to convert native sizes into base-asset quantities.</summary>
/// <param name="Native">The venue's own instrument name.</param>
/// <param name="Multiplier">Base-asset units per native size unit (contract value).</param>
/// <param name="Inverse">Native size is quote currency (USD contracts); base quantity is size ÷ price.</param>
public sealed record VenueSpec(string Native, decimal Multiplier = 1, bool Inverse = false);

/// <summary>A listed venue instrument and its size conversion.</summary>
public sealed record VenueInstrument(Instrument Instrument, VenueSpec Spec);

/// <summary>
/// Credential-free public market data from one crypto venue: instrument listing with exact tick/lot metadata, live
/// trades and order book over WebSocket, and REST candles. Sizes are converted to base-asset quantities (contracts ×
/// contract value; inverse USD contracts ÷ price). The public trade REST endpoints only expose a recent window, so
/// trade history is what Trading Hub records; candles cover the past. Data only: no account, no orders.
/// </summary>
public abstract class PublicVenueFeed : IMarketDataFeed, IKlineHistory, IRecentTradeHistoryFeed, ICryptoVenue
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action<ClientWebSocketOptions>? _configureSocket;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>>? _connectWebSocket;
    private readonly Uri? _restOrigin;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private readonly ConcurrentDictionary<string, VenueSpec> _specs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private long _lastRequestTimestamp;
    private long _nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    private int _disposed;

    protected PublicVenueFeed(PublicVenueTransport? transport)
    {
        transport ??= new PublicVenueTransport();
        _http = transport.Http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttp = transport.Http == null || transport.OwnsHttp;
        if (transport.Http == null) _http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingHub/1.0");
        _configureSocket = transport.ConfigureSocket;
        _connectWebSocket = transport.ConnectWebSocket;
        _restOrigin = transport.RestOrigin;
        _delay = transport.Delay ?? ((duration, ct) => Task.Delay(duration, ct));
        Clock = transport.Clock ?? TimeProvider.System;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Venue { get; }
    public abstract bool IsSpot { get; }
    public virtual string PreferredQuote => "USDT";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public bool RecentTradesOnly => true;
    public virtual string CacheTag => "public-venue-v1:" + Id;
    protected TimeProvider Clock { get; }

    /// <summary>Default REST origin, replaced by <see cref="PublicVenueTransport.RestOrigin"/> in isolated runs.</summary>
    protected abstract Uri DefaultRestOrigin { get; }

    /// <summary>The WebSocket endpoint for an instrument.</summary>
    protected abstract Uri SocketEndpoint(Instrument instrument, VenueSpec spec);

    /// <summary>Text frames sent after connecting (subscriptions).</summary>
    protected abstract IEnumerable<string> SubscribeFrames(Instrument instrument, VenueSpec spec);

    /// <summary>Parses one socket message into <paramref name="session"/>. Call <see cref="VenueSession.Resync"/> on a book sequence gap.</summary>
    protected abstract void OnMessage(string text, VenueSession session);

    protected abstract Task<IReadOnlyList<VenueInstrument>> ListAsync(CancellationToken ct);

    /// <summary>Candles of 1, 5, 15 or 60 minutes covering [from, to), any order; the base class sorts, de-duplicates and clips them.</summary>
    protected abstract Task<IReadOnlyList<Kline>> FetchKlinesAsync(Instrument instrument, VenueSpec spec, long fromMs, long toMs, int minutes, CancellationToken ct);

    /// <summary>Silence after which the socket is considered stale and reconnected.</summary>
    protected virtual TimeSpan Stale => TimeSpan.FromSeconds(30);

    /// <summary>Application keepalive frame and period (null: protocol keepalive only).</summary>
    protected virtual (TimeSpan Every, string Frame)? Keepalive => null;

    /// <summary>What the venue's book stream represents (for example a top-N or grouped book).</summary>
    protected virtual DepthCoverage BookCoverage => DepthCoverage.None;

    /// <summary>Minimum spacing between REST requests (public rate limits).</summary>
    protected virtual TimeSpan RequestSpacing => TimeSpan.FromMilliseconds(120);

    /// <summary>Minimum spacing between book publications; a throttled change is published when it elapses.</summary>
    private static readonly TimeSpan BookThrottle = TimeSpan.FromMilliseconds(100);

    // ======================================================================= listing

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var listed = await ListAsync(linked.Token).ConfigureAwait(false);
        foreach (var item in listed) _specs[item.Instrument.Symbol] = item.Spec;
        string[] popular = { "BTC", "ETH", "SOL", "XRP", "DOGE" };
        return listed.Select(x => x.Instrument)
            .OrderBy(i => Array.IndexOf(popular, i.BaseAsset) is var k && k >= 0 ? k : 999)
            .ThenBy(i => i.Symbol, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The listed conversion for an instrument, or null before a successful listing.</summary>
    public VenueSpec? SpecOf(Instrument instrument) => _specs.TryGetValue(instrument.Symbol, out var spec) ? spec : null;

    private async Task<VenueSpec> RequireSpecAsync(Instrument instrument, CancellationToken ct)
    {
        if (SpecOf(instrument) is { } known) return known;
        await GetInstrumentsAsync(ct).ConfigureAwait(false);
        return SpecOf(instrument) ?? throw new InvalidOperationException($"{instrument.Symbol} is not listed by {DisplayName}.");
    }

    // ======================================================================= history

    /// <summary>Public trade REST endpoints only cover a recent window; recorded trades and candles provide history.</summary>
    public virtual Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        ThrowIfDisposed();
        progress?.Report(1);
        return Task.CompletedTask;
    }

    public virtual Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (minutes is not (1 or 5 or 15 or 60)) throw new ArgumentOutOfRangeException(nameof(minutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        var spec = await RequireSpecAsync(instrument, ct).ConfigureAwait(false);
        long step = minutes * 60_000L;
        long from = ToMs(fromUtc) / step * step, to = ToMs(toUtc);
        if (to <= from) { progress?.Report(1); return; }
        var bars = await FetchKlinesAsync(instrument, spec, from, to, minutes, ct).ConfigureAwait(false);
        var ordered = new SortedDictionary<long, Kline>();
        foreach (var bar in bars)
            if (bar.OpenTimeMs >= from && bar.OpenTimeMs < to && bar.OpenTimeMs % step == 0 && bar.High >= bar.Low && bar.Volume >= 0)
                ordered[bar.OpenTimeMs] = bar;
        if (ordered.Count > 0) onBatch(ordered.Values.ToList());
        progress?.Report(1);
    }

    // ======================================================================= REST helpers

    protected string Rest(string pathAndQuery) => (_restOrigin ?? DefaultRestOrigin).GetLeftPart(UriPartial.Authority) + pathAndQuery;

    protected async Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        await PaceAsync(ct).ConfigureAwait(false);
        using var response = await _http.GetAsync(Rest(pathAndQuery), ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    protected async Task<JsonDocument> PostJsonAsync(string pathAndQuery, string json, CancellationToken ct)
    {
        await PaceAsync(ct).ConfigureAwait(false);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Rest(pathAndQuery), content, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{DisplayName} {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}", null, response.StatusCode);
        return JsonDocument.Parse(body);
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        await _pace.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long now = Clock.GetTimestamp();
            if (_lastRequestTimestamp != 0)
            {
                var wait = RequestSpacing - Clock.GetElapsedTime(_lastRequestTimestamp, now);
                if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);
            }
            _lastRequestTimestamp = Clock.GetTimestamp();
        }
        finally { _pace.Release(); }
    }

    // ======================================================================= JSON helpers

    /// <summary>A JSON number or numeric string; malformed values fail instead of becoming zero.</summary>
    public static decimal D(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetDecimal(out var d) ? d : throw new InvalidDataException("A venue number is out of range."),
        JsonValueKind.String when e.GetString() is { Length: > 0 } s =>
            decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("A venue number is missing."),
    };

    /// <summary>An optional number: absent, null or empty values yield <paramref name="fallback"/>.</summary>
    public static decimal D(JsonElement obj, string name, decimal fallback = 0) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e) && e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(e.ValueKind == JsonValueKind.String && e.GetString() is null or "") ? D(e) : fallback;

    public static string S(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e)
            ? e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ValueKind is JsonValueKind.Null ? "" : e.ToString()
            : "";

    public static DateTime Ms(decimal ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;
    public static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>Removes trailing zeros (0.10000 → 0.1) so ticks display correctly.</summary>
    public static decimal Norm(decimal v) => v / 1.0000000000000000000000000000m;

    public static decimal Pow10(int exponent)
    {
        decimal result = 1;
        for (int i = 0; i < Math.Abs(exponent); i++) result = exponent >= 0 ? result * 10 : result / 10;
        return result;
    }

    protected static bool IsUsdQuote(string quote) => quote is "USDT" or "USD" or "USDC";

    // ======================================================================= live

    /// <summary>Per-connection parse state: new trades and the venue book.</summary>
    public sealed class VenueSession
    {
        internal readonly List<Trade> Trades = new();
        private readonly PublicVenueFeed _feed;
        internal bool BookChanged;
        internal bool Snapshot;
        /// <summary>Book and sequence writes so far; a frame that fails after one has left the book half-applied.</summary>
        internal int BookWrites;
        private long _seq = -1;

        internal VenueSession(PublicVenueFeed feed, Instrument instrument, VenueSpec spec)
        {
            _feed = feed;
            Instrument = instrument;
            Spec = spec;
        }

        public Instrument Instrument { get; }
        public VenueSpec Spec { get; }
        public OrderBookBuilder Book { get; } = new();

        /// <summary>Venue book sequence (-1 until the first snapshot).</summary>
        public long Seq
        {
            get => _seq;
            set
            {
                _seq = value;
                BookWrites++;
            }
        }

        /// <summary>Base-asset quantity of a native size.</summary>
        public decimal Coins(decimal size, decimal price) => Spec.Inverse ? price > 0 ? size / price : 0 : size * Spec.Multiplier;

        /// <summary>
        /// Records a trade at its exchange time. A native trade id becomes the source event identity; without one each
        /// reception is a distinct observation and marked partial, never silently reconciled.
        /// </summary>
        public void Trade(DateTime time, decimal price, decimal size, bool buy, string? nativeId)
        {
            decimal coins = Coins(size, price);
            if (price <= 0 || coins <= 0) return;
            bool identified = !string.IsNullOrEmpty(nativeId);
            var provenance = new TradeProvenance(DataQuality.Raw | (identified ? DataQuality.None : DataQuality.Partial),
                SourceIdentity: _feed.Id, SourceEventId: identified ? nativeId : $"observation:{Guid.NewGuid():N}");
            Trades.Add(new Trade(_feed.NextId(), time, price, coins, buy ? TradeSide.Buy : TradeSide.Sell, provenance));
        }

        /// <summary>A book level; size zero removes it.</summary>
        public void Level(BookSide side, decimal price, decimal size)
        {
            if (price <= 0) return;
            Book.Set(side, Instrument.ToTicks(price), Math.Max(0, Coins(size, price)));
            BookChanged = true;
            BookWrites++;
        }

        /// <summary>Rows [price, size, …] of one side.</summary>
        public void Levels(BookSide side, JsonElement rows)
        {
            if (rows.ValueKind != JsonValueKind.Array) return;
            foreach (var row in rows.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 2) Level(side, D(row[0]), D(row[1]));
        }

        /// <summary>Starts a new full book.</summary>
        public void Clear()
        {
            Book.Clear();
            Snapshot = true;
            BookChanged = true;
            BookWrites++;
        }

        /// <summary>The book lost sequence: the connection is restarted and the book requested again.</summary>
        public static void Resync(string reason) => throw new VenueResyncException(reason);
    }

    public sealed class VenueResyncException(string message) : Exception(message);

    private long NextId() => Interlocked.Increment(ref _nextId);

    /// <summary>Creates parse state for an instrument (tests feed recorded frames through <see cref="Process"/>).</summary>
    public VenueSession CreateSession(Instrument instrument, VenueSpec spec) => new(this, instrument, spec);

    /// <summary>Parses one frame as if received from the socket and returns the trades it produced.</summary>
    public IReadOnlyList<Trade> Process(string text, VenueSession session)
    {
        OnMessage(text, session);
        var result = session.Trades.ToArray();
        session.Trades.Clear();
        return result;
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        ThrowIfDisposed();
        var subscription = new LiveSubscription(DisplayName, (status, message) => sink.OnStatus(instrument, status, message));
        _ = Task.Run(() => RunAsync(instrument, sink, subscription));
        return subscription;
    }

    private async Task RunAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription live)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(live.Token, _lifetime.Token);
        var ct = linked.Token;
        int failures = 0, requested = 0;
        bool everConnected = false, bookPublished = false;
        while (!ct.IsCancellationRequested)
        {
            var connection = live.BeginConnection();
            using var connectionScope = CancellationTokenSource.CreateLinkedTokenSource(connection.Token, _lifetime.Token);
            var token = connectionScope.Token;
            bool receivedAny = false, resync = false;
            string reason = "connection lost";
            try
            {
                if (!everConnected) live.SetChannel("data", FeedStatus.Connecting, $"Connecting to {DisplayName}…");
                // Unknown contract sizes would scale volumes wrongly; never stream without listed metadata.
                var spec = await RequireSpecAsync(instrument, token).ConfigureAwait(false);
                var session = new VenueSession(this, instrument, spec);
                WebSocket ws;
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    ws = await ConnectSocketAsync(SocketEndpoint(instrument, spec), connect.Token).ConfigureAwait(false);
                }
                using var ownedSocket = ws;
                var send = new SemaphoreSlim(1, 1);
                foreach (var frame in SubscribeFrames(instrument, spec))
                    await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                live.SetChannel("data", FeedStatus.Connected, null);
                everConnected = true;
                connection.CancelAfter(Stale);
                // Some venues replay recent prints on subscribe; those predate this connection.
                var connectedAt = Clock.GetUtcNow().UtcDateTime.AddSeconds(-2);
                using var keepalive = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (Keepalive is { } ka) _ = KeepAliveAsync(ws, send, ka.Every, ka.Frame, keepalive.Token);

                var buffer = new byte[1 << 16];
                using var message = new MemoryStream();
                // Frame handling and the trailing timer publish under one gate, so sink callbacks never overlap.
                var gate = new object();
                bool open = true;
                DateTime lastPublish = DateTime.MinValue;
                ITimer? trailing = null;
                void PublishBook()
                {
                    var now = Clock.GetUtcNow().UtcDateTime;
                    var wait = BookThrottle - (now - lastPublish);
                    if (!session.Snapshot && wait > TimeSpan.Zero)
                    {
                        // A quiet book would otherwise keep showing a superseded level until the next frame.
                        trailing?.Change(wait, Timeout.InfiniteTimeSpan);
                        return;
                    }
                    sink.OnDepth(instrument, session.Book.BuildUpdate(now, 500, session.Snapshot, Id, DisplayName,
                        session.Seq >= 0 ? session.Seq : null, additionalCoverage: BookCoverage, receivedAt: now));
                    bookPublished = true;
                    lastPublish = now;
                    session.Snapshot = false;
                    session.BookChanged = false;
                }
                trailing = Clock.CreateTimer(_ =>
                {
                    lock (gate)
                        if (open && session.BookChanged) PublishBook();
                }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                try
                {
                    while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        message.SetLength(0);
                        WebSocketReceiveResult received;
                        do
                        {
                            received = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                            if (received.MessageType == WebSocketMessageType.Close) break;
                            message.Write(buffer, 0, received.Count);
                        } while (!received.EndOfMessage);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            reason = "server closed the connection";
                            break;
                        }
                        receivedAny = true;
                        failures = 0;
                        connection.CancelAfter(Stale);
                        string text = IsGzip(message) ? Gunzip(message) : Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                        lock (gate)
                        {
                            int bookWrites = session.BookWrites;
                            try { OnMessage(text, session); }
                            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                            {
                                // Control frames (pong, acknowledgements) have other shapes. A frame that failed after it
                                // changed the book or its sequence left a half-applied delta: request the book again.
                                // Trades parsed before the failure are valid prints and are still published.
                                if (session.BookWrites != bookWrites) VenueSession.Resync("unparseable book frame");
                            }

                            if (session.Trades.Count > 0)
                            {
                                var trades = session.Trades.Where(t => t.Time >= connectedAt).ToArray();
                                session.Trades.Clear();
                                if (trades.Length > 0) sink.OnTrades(instrument, trades);
                            }

                            if (session.BookChanged) PublishBook();
                        }
                    }
                }
                finally
                {
                    // No trailing publication after the connection ends: the outage handling below owns the book.
                    lock (gate) open = false;
                    trailing.Dispose();
                }
                keepalive.Cancel();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                reason = live.WasRequested(ref requested) ? "reconnection requested" : receivedAny ? "no data arriving" : "response timed out";
                if (!receivedAny) failures++;
            }
            catch (VenueResyncException ex)
            {
                resync = true;
                reason = "book sequence gap (" + ex.Message + ")";
            }
            catch (Exception ex)
            {
                failures++;
                reason = ex.Message;
            }
            finally
            {
                live.EndConnection(connection);
            }

            if (ct.IsCancellationRequested) break;
            if (bookPublished)
            {
                // A stale book must not remain displayable across the outage.
                sink.OnDepth(instrument, new DepthUpdate
                {
                    Snapshot = DepthSnapshot.Empty, ProviderId = Id, Venue = DisplayName, ReceivedAt = Clock.GetUtcNow().UtcDateTime,
                    Coverage = DepthCoverage.Missing | DepthCoverage.SequenceGap | (resync ? DepthCoverage.None : DepthCoverage.Disconnected),
                    GapBefore = resync ? DepthGapReason.Sequence : DepthGapReason.Disconnected,
                    Message = $"{DisplayName} book unavailable until the next full snapshot ({reason}).",
                });
                bookPublished = false;
            }
            var wait = LiveSubscription.Backoff(failures);
            live.SetChannel("data", FeedStatus.Reconnecting, failures == 0
                ? $"Reconnecting ({reason})…"
                : $"Reconnecting in {wait.TotalSeconds:0} s · attempt {failures} ({reason})");
            try { await _delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        live.SetChannel("data", FeedStatus.Disconnected, null);
    }

    private static async Task KeepAliveAsync(WebSocket ws, SemaphoreSlim send, TimeSpan every, string frame, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(every, ct).ConfigureAwait(false);
                await send.WaitAsync(ct).ConfigureAwait(false);
                try { await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
                finally { send.Release(); }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || ws.State != WebSocketState.Open)
        {
            // The connection ended; the receive loop reports why.
        }
    }

    private async Task<WebSocket> ConnectSocketAsync(Uri endpoint, CancellationToken ct)
    {
        if (_connectWebSocket != null) return await _connectWebSocket(endpoint, ct).ConfigureAwait(false);
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.SetRequestHeader("User-Agent", "TradingHub/1.0");
            _configureSocket?.Invoke(socket.Options);
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsGzip(MemoryStream ms) => ms.Length > 2 && ms.GetBuffer()[0] == 0x1f && ms.GetBuffer()[1] == 0x8b;

    private static string Gunzip(MemoryStream ms)
    {
        ms.Position = 0;
        using var gz = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Cancelled, not disposed: a live loop or request that already passed its disposal check still reads the
        // token. The source has no timer or wait handle, so there is nothing to release.
        _lifetime.Cancel();
        if (_ownsHttp) _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
