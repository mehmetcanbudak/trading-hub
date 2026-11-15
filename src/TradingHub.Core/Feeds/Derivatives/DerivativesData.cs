// Behavior adapted from the KVP capability reference (commit e43b23b); see THIRD_PARTY_NOTICES.md.
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;

namespace TradingHub.Feeds.Derivatives;

/// <summary>Funding rate in percent per funding period, at the venue's settlement time.</summary>
public readonly record struct FundingSample(DateTime Time, decimal RatePercent);

public sealed record FundingHistoryResult(IReadOnlyList<FundingSample> Samples, OpenInterestAvailability Availability, string? Message = null);

/// <summary>A forced liquidation order: a long liquidated is a forced sell, a short liquidated a forced buy.</summary>
public readonly record struct LiquidationEvent(DateTime Time, string VenueId, decimal Price, decimal Quantity, bool LongLiquidated)
{
    public decimal Notional => Price * Quantity;
}

/// <summary>A span during which a venue's liquidation stream was connected. Outside these spans liquidations are unknown, not zero.</summary>
public readonly record struct ObservedWindow(string VenueId, DateTime From, DateTime To);

public sealed record LiquidationSnapshot(IReadOnlyList<LiquidationEvent> Events, IReadOnlyList<ObservedWindow> Windows, string? Message);

/// <summary>Caller-owned transport for derivatives data; overrides are isolated-run seams.</summary>
public sealed record DerivativesTransport(
    HttpClient? Http = null,
    bool OwnsHttp = false,
    Action<ClientWebSocketOptions>? ConfigureSocket = null,
    Func<Uri, CancellationToken, Task<WebSocket>>? ConnectWebSocket = null,
    Uri? BinanceRest = null,
    Uri? BybitRest = null,
    Uri? BinanceSocket = null,
    Uri? BybitSocket = null,
    Func<DateTime>? UtcNow = null,
    Func<TimeSpan, CancellationToken, Task>? Delay = null);

/// <summary>
/// Public, credential-free crypto derivatives data for Binance USD-M and Bybit linear perpetuals: funding history,
/// open interest (through each venue's <see cref="IOpenInterestProvider"/>) and live liquidations. Liquidations exist
/// only while a stream is connected; observed windows are recorded with the events so earlier periods remain unknown.
/// Binance's forceOrder stream publishes at most one liquidation per symbol per second, so it is a sample.
/// </summary>
public sealed class DerivativesData : IDisposable
{
    public const string Binance = "binance";
    public const string Bybit = "bybit";
    public static readonly IReadOnlyList<string> Venues = new[] { Binance, Bybit };

    /// <summary>The application-wide service, configured with the network policy and data root; null when not configured.</summary>
    public static DerivativesData? Shared { get; set; }

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DerivativesTransport _transport;
    private readonly string? _root;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly BinanceFeed _binance;
    private readonly BybitOpenInterest _bybitOpenInterest;
    private readonly Dictionary<string, LiquidationStream> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    /// <param name="root">Liquidation recording folder; null keeps observations in memory only.</param>
    public DerivativesData(DerivativesTransport? transport = null, string? root = null)
    {
        _transport = transport ?? new DerivativesTransport();
        _http = _transport.Http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttp = _transport.Http == null || _transport.OwnsHttp;
        _root = root;
        _utcNow = _transport.UtcNow ?? (() => DateTime.UtcNow);
        _delay = _transport.Delay ?? ((duration, ct) => Task.Delay(duration, ct));
        _binance = new BinanceFeed(true, restOrigin: _transport.BinanceRest, utcNow: _utcNow, delay: _delay, httpClient: _http);
        _bybitOpenInterest = new BybitOpenInterest(_http, _transport.BybitRest, utcNow: _utcNow, delay: _delay);
    }

    public static string VenueName(string venue) => venue == Binance ? "Binance Futures" : venue == Bybit ? "Bybit" : venue;

    private string BinanceOrigin => (_transport.BinanceRest ?? new Uri("https://fapi.binance.com")).GetLeftPart(UriPartial.Authority);
    private string BybitOrigin => (_transport.BybitRest ?? new Uri("https://api.bybit.com")).GetLeftPart(UriPartial.Authority);

    private static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDecimal()
        : decimal.Parse(e.GetString() ?? throw new InvalidDataException("A venue number is missing."), NumberStyles.Float, CultureInfo.InvariantCulture);

    // ================================================================ funding

    public async Task<FundingHistoryResult> LoadFundingAsync(string venue, string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        var samples = new SortedDictionary<DateTime, FundingSample>();
        if (venue == Binance)
        {
            long start = Ms(from), end = Ms(to);
            for (int page = 0; page < 1000 && start < end; page++)
            {
                using var doc = await GetAsync($"{BinanceOrigin}/fapi/v1/fundingRate?symbol={Uri.EscapeDataString(symbol)}&startTime={start}&endTime={end}&limit=1000", ct).ConfigureAwait(false);
                var rows = ParseBinanceFunding(doc.RootElement).ToList();
                foreach (var row in rows) samples[row.Time] = row;
                if (rows.Count < 1000) break;
                start = Ms(rows[^1].Time) + 1;
            }
        }
        else if (venue == Bybit)
        {
            long start = Ms(from), end = Ms(to);
            for (int page = 0; page < 1000 && end > start; page++)
            {
                using var doc = await GetAsync($"{BybitOrigin}/v5/market/funding/history?category=linear&symbol={Uri.EscapeDataString(symbol)}&startTime={start}&endTime={end}&limit=200", ct).ConfigureAwait(false);
                var rows = ParseBybitFunding(doc.RootElement).ToList();
                foreach (var row in rows) samples[row.Time] = row;
                if (rows.Count < 200) break;
                end = Ms(rows.Min(r => r.Time)) - 1;
            }
        }
        else throw new ArgumentException("Unknown derivatives venue.", nameof(venue));
        var values = samples.Values.Where(s => s.Time >= from && s.Time <= to).ToArray();
        return values.Length == 0
            ? new(values, OpenInterestAvailability.Unavailable, $"{VenueName(venue)} reports no funding for {symbol} in this range.")
            : new(values, OpenInterestAvailability.Available);
    }

    /// <summary>Binance /fapi/v1/fundingRate rows {fundingTime, fundingRate} (rate as a fraction).</summary>
    public static IEnumerable<FundingSample> ParseBinanceFunding(JsonElement root)
    {
        foreach (var e in root.EnumerateArray())
            yield return new FundingSample(FromMs(e.GetProperty("fundingTime").GetInt64()), Dec(e.GetProperty("fundingRate")) * 100);
    }

    /// <summary>Bybit /v5/market/funding/history rows {fundingRate, fundingRateTimestamp}, newest first.</summary>
    public static IEnumerable<FundingSample> ParseBybitFunding(JsonElement root)
    {
        foreach (var e in root.GetProperty("result").GetProperty("list").EnumerateArray())
            yield return new FundingSample(FromMs(long.Parse(e.GetProperty("fundingRateTimestamp").GetString()!, CultureInfo.InvariantCulture)),
                Dec(e.GetProperty("fundingRate")) * 100);
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}", null, response.StatusCode);
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("retCode", out var code) && code.GetInt32() != 0)
        {
            string message = root.TryGetProperty("retMsg", out var m) ? m.GetString() ?? "" : "";
            doc.Dispose();
            throw new InvalidOperationException(message);
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("msg", out var msg) && root.TryGetProperty("code", out _))
        {
            string message = msg.GetString() ?? "";
            doc.Dispose();
            throw new InvalidOperationException(message);
        }
        return doc;
    }

    // ================================================================ open interest

    /// <summary>Open interest through the venue's existing <see cref="IOpenInterestProvider"/> (base-asset units).</summary>
    public Task<OpenInterestHistoryResult> LoadOpenInterestAsync(string venue, string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        IOpenInterestProvider provider = venue switch
        {
            Binance => _binance,
            Bybit => _bybitOpenInterest,
            _ => throw new ArgumentException("Unknown derivatives venue.", nameof(venue)),
        };
        var instrument = new Instrument
        {
            Symbol = symbol, FeedId = venue == Binance ? BinanceFeed.FuturesId : BybitFeed.LinearId, TickSize = 0.0001m,
        };
        return provider.LoadOpenInterestAsync(instrument, from, to, ct);
    }

    // ================================================================ liquidations

    private sealed class LiquidationStream
    {
        public readonly List<LiquidationEvent> Events = new();
        public readonly List<ObservedWindow> Windows = new();
        public readonly CancellationTokenSource Cancellation;
        public int Users;
        /// <summary>Start of each venue's currently connected window.</summary>
        public readonly Dictionary<string, DateTime> Open = new();
        public long Generation;
        public string? Message;

        public LiquidationStream(CancellationToken lifetime) => Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    /// <summary>Changes whenever a liquidation or connection window for the symbol is recorded.</summary>
    public long LiquidationGeneration(string symbol)
    {
        lock (_streams) return _streams.TryGetValue(symbol, out var s) ? Volatile.Read(ref s.Generation) : 0;
    }

    /// <summary>Starts (or shares) the Binance and Bybit liquidation streams of one symbol; dispose to release.</summary>
    public IDisposable WatchLiquidations(string symbol)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        symbol = symbol.ToUpperInvariant();
        LiquidationStream stream;
        lock (_streams)
        {
            if (!_streams.TryGetValue(symbol, out stream!))
            {
                stream = new LiquidationStream(_lifetime.Token);
                var (events, windows) = LoadRecorded(symbol, 7);
                stream.Events.AddRange(events);
                stream.Windows.AddRange(windows);
                _streams[symbol] = stream;
                var token = stream.Cancellation.Token;
                foreach (var venue in Venues) _ = Task.Run(() => RunAsync(symbol, venue, stream, token));
            }
            stream.Users++;
        }
        return new Release(() =>
        {
            lock (_streams)
            {
                if (--stream.Users > 0) return;
                stream.Cancellation.Cancel();
                _streams.Remove(symbol);
            }
        });
    }

    /// <summary>Known liquidations and observed windows overlapping [from, to); without a live stream, recorded days are read.</summary>
    public LiquidationSnapshot Liquidations(string symbol, DateTime from, DateTime to)
    {
        symbol = symbol.ToUpperInvariant();
        LiquidationStream? stream;
        lock (_streams) _streams.TryGetValue(symbol, out stream);
        if (stream == null)
        {
            var (events, windows) = LoadRecorded(symbol, Math.Clamp((int)Math.Ceiling((_utcNow() - from).TotalDays), 1, 60));
            return new(events.Where(e => e.Time >= from && e.Time < to).ToArray(), windows.Where(w => w.To > from && w.From < to).ToArray(), null);
        }
        var now = _utcNow();
        lock (stream.Events)
            return new(stream.Events.Where(e => e.Time >= from && e.Time < to).ToArray(),
                stream.Windows.Select(w => stream.Open.TryGetValue(w.VenueId, out var open) && open == w.From && now > w.To ? w with { To = now } : w)
                    .Where(w => w.To > from && w.From < to).ToArray(), stream.Message);
    }

    private async Task RunAsync(string symbol, string venue, LiquidationStream stream, CancellationToken ct)
    {
        for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            DateTime? connectedAt = null;
            try
            {
                using var ws = await ConnectAsync(venue == Binance
                    ? new Uri((_transport.BinanceSocket?.GetLeftPart(UriPartial.Authority) ?? "wss://fstream.binance.com") + $"/ws/{symbol.ToLowerInvariant()}@forceOrder")
                    : _transport.BybitSocket ?? new Uri("wss://stream.bybit.com/v5/public/linear"), ct).ConfigureAwait(false);
                if (venue == Bybit)
                    await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"op\":\"subscribe\",\"args\":[\"allLiquidation.{symbol}\"]}}"), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                connectedAt = _utcNow();
                attempt = 0;
                OpenWindow(symbol, stream, venue, connectedAt.Value);
                using var ping = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (venue == Bybit) _ = PingAsync(ws, ping.Token);
                var buffer = new byte[1 << 16];
                using var message = new MemoryStream();
                DateTime lastWindowWrite = connectedAt.Value;
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                        if (received.MessageType == WebSocketMessageType.Close) break;
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    foreach (var liquidation in Parse(venue, message.ToArray())) Add(symbol, stream, liquidation);
                    var now = _utcNow();
                    if (now - lastWindowWrite >= TimeSpan.FromMinutes(1))
                    {
                        ExtendWindow(symbol, stream, venue, connectedAt.Value, now);
                        lastWindowWrite = now;
                    }
                }
                ping.Cancel();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (stream.Events) stream.Message = $"{VenueName(venue)} liquidations: {ex.Message}";
            }
            finally
            {
                if (connectedAt is { } started)
                {
                    lock (stream.Events) stream.Open.Remove(venue);
                    ExtendWindow(symbol, stream, venue, started, _utcNow());
                }
            }
            if (ct.IsCancellationRequested) return;
            try { await _delay(TimeSpan.FromSeconds(Math.Min(60, 3 << Math.Min(attempt, 4))), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task PingAsync(WebSocket ws, CancellationToken ct)
    {
        // Bybit drops public sockets without an application ping about every 20 s.
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(18), ct).ConfigureAwait(false);
                await ws.SendAsync("{\"op\":\"ping\"}"u8.ToArray(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || ws.State != WebSocketState.Open) { }
    }

    private async Task<WebSocket> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        if (_transport.ConnectWebSocket != null) return await _transport.ConnectWebSocket(endpoint, ct).ConfigureAwait(false);
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            _transport.ConfigureSocket?.Invoke(socket.Options);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(endpoint, connect.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Parses Binance forceOrder or Bybit allLiquidation messages; control frames yield nothing.</summary>
    public static IReadOnlyList<LiquidationEvent> Parse(string venue, byte[] json)
    {
        var result = new List<LiquidationEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (venue == Binance && root.TryGetProperty("o", out var o))
            {
                decimal average = o.TryGetProperty("ap", out var ap) ? Dec(ap) : 0;
                decimal filled = o.TryGetProperty("z", out var z) ? Dec(z) : 0;
                // A forced SELL closes a long position.
                result.Add(new LiquidationEvent(FromMs(o.GetProperty("T").GetInt64()), Binance, average > 0 ? average : Dec(o.GetProperty("p")),
                    filled > 0 ? filled : Dec(o.GetProperty("q")), o.GetProperty("S").GetString() == "SELL"));
            }
            else if (venue == Bybit && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                // Bybit reports the liquidated position side: "Buy" is a long.
                foreach (var e in data.EnumerateArray())
                    result.Add(new LiquidationEvent(FromMs(e.GetProperty("T").GetInt64()), Bybit, Dec(e.GetProperty("p")), Dec(e.GetProperty("v")),
                        e.GetProperty("S").GetString() == "Buy"));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        return result.Where(l => l.Price > 0 && l.Quantity > 0).ToArray();
    }

    private void Add(string symbol, LiquidationStream stream, LiquidationEvent liquidation)
    {
        lock (stream.Events) stream.Events.Add(liquidation);
        Interlocked.Increment(ref stream.Generation);
        Append(symbol, stream, liquidation.Time, string.Join(",", "E", liquidation.Time.Ticks.ToString(CultureInfo.InvariantCulture), liquidation.VenueId,
            liquidation.Price.ToString(CultureInfo.InvariantCulture), liquidation.Quantity.ToString(CultureInfo.InvariantCulture), liquidation.LongLiquidated ? "L" : "S"));
    }

    private void OpenWindow(string symbol, LiquidationStream stream, string venue, DateTime from)
    {
        lock (stream.Events) stream.Open[venue] = from;
        ExtendWindow(symbol, stream, venue, from, from);
    }

    private void ExtendWindow(string symbol, LiquidationStream stream, string venue, DateTime from, DateTime to)
    {
        lock (stream.Events)
        {
            int index = stream.Windows.FindIndex(w => w.VenueId == venue && w.From == from);
            var window = new ObservedWindow(venue, from, to);
            if (index >= 0) stream.Windows[index] = window;
            else stream.Windows.Add(window);
        }
        Interlocked.Increment(ref stream.Generation);
        Append(symbol, stream, from, string.Join(",", "W", venue, from.Ticks.ToString(CultureInfo.InvariantCulture), to.Ticks.ToString(CultureInfo.InvariantCulture)));
    }

    private string? DayFile(string symbol, DateTime day) => _root == null ? null
        : Path.Combine(_root, "Liquidations", string.Concat(symbol.Where(char.IsLetterOrDigit)), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");

    private void Append(string symbol, LiquidationStream stream, DateTime day, string line)
    {
        if (DayFile(symbol, day) is not { } file) return;
        try
        {
            lock (stream.Events)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, line + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (stream.Events) stream.Message = "Liquidation recording failed; observations stay in memory only: " + ex.Message;
        }
    }

    private (List<LiquidationEvent> Events, List<ObservedWindow> Windows) LoadRecorded(string symbol, int days)
    {
        var events = new List<LiquidationEvent>();
        var windows = new Dictionary<(string, DateTime), DateTime>();
        if (_root == null) return (events, new List<ObservedWindow>());
        var today = _utcNow().Date;
        for (int d = days; d >= 0; d--)
        {
            if (DayFile(symbol, today.AddDays(-d)) is not { } file || !File.Exists(file)) continue;
            foreach (var line in File.ReadLines(file))
            {
                var p = line.Split(',');
                try
                {
                    if (p is ["E", var ticks, var venue, var price, var quantity, var side])
                        events.Add(new LiquidationEvent(new DateTime(long.Parse(ticks, CultureInfo.InvariantCulture), DateTimeKind.Utc), venue,
                            decimal.Parse(price, CultureInfo.InvariantCulture), decimal.Parse(quantity, CultureInfo.InvariantCulture), side == "L"));
                    else if (p is ["W", var windowVenue, var start, var end])
                    {
                        var key = (windowVenue, new DateTime(long.Parse(start, CultureInfo.InvariantCulture), DateTimeKind.Utc));
                        var stop = new DateTime(long.Parse(end, CultureInfo.InvariantCulture), DateTimeKind.Utc);
                        if (!windows.TryGetValue(key, out var known) || stop > known) windows[key] = stop;
                    }
                }
                catch (FormatException) { } // A line cut by a crash is ignored; its window stays unobserved.
                catch (OverflowException) { }
            }
        }
        return (events, windows.Select(w => new ObservedWindow(w.Key.Item1, w.Key.Item2, w.Value)).OrderBy(w => w.From).ToList());
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _binance.Dispose();
        _bybitOpenInterest.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
