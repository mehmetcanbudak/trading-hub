using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Bybit;

/// <summary>
/// Datos públicos de Bybit (perpetuos USDT, API v5): tape y libro de 200 niveles en vivo.
/// La API REST solo da las últimas 1000 operaciones, así que la historia se completa con lo que
/// Trading Hub va grabando en disco.
/// </summary>
public sealed class BybitFeed : IMarketDataFeed, ITickerStatsProvider, IKlineHistory, IRecentTradeHistoryFeed
{
    public const string LinearId = "bybit-linear";
    public const string TestnetId = "bybit-linear-testnet";
    private readonly string _feedId;
    private readonly string _rest;
    private readonly Uri _webSocketEndpoint;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Action<ClientWebSocketOptions>? _configureSocket;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>>? _connectWebSocket;
    private long _nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    /// <summary>Injected HTTP is caller-owned unless requested otherwise; the socket connector is an optional no-network transport seam.</summary>
    public BybitFeed(HttpClient? httpClient = null, Uri? restOrigin = null, Uri? webSocketEndpoint = null,
        Action<ClientWebSocketOptions>? configureSocket = null, bool ownsHttpClient = false,
        Func<Uri, CancellationToken, Task<WebSocket>>? connectWebSocket = null, string? feedId = null)
    {
        _feedId = feedId ?? LinearId;
        bool testnet = _feedId == TestnetId;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttpClient = httpClient == null || ownsHttpClient;
        _rest = (restOrigin?.GetLeftPart(UriPartial.Authority) ?? (testnet ? "https://api-testnet.bybit.com" : "https://api.bybit.com")) + "/v5/market";
        _webSocketEndpoint = webSocketEndpoint ?? new Uri(testnet ? "wss://stream-testnet.bybit.com/v5/public/linear" : "wss://stream.bybit.com/v5/public/linear");
        _configureSocket = configureSocket;
        _connectWebSocket = connectWebSocket;
    }

    public string Id => _feedId;
    public string DisplayName => "Bybit";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public bool RecentTradesOnly => true;

    private static decimal Dec(JsonElement e) => decimal.Parse(e.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = new List<Instrument>();
        string? cursor = null;
        do
        {
            var url = $"{_rest}/instruments-info?category=linear&limit=1000" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct).ConfigureAwait(false));
            var result = doc.RootElement.GetProperty("result");
            foreach (var s in result.GetProperty("list").EnumerateArray())
            {
                if (s.GetProperty("status").GetString() != "Trading") continue;
                if (s.TryGetProperty("contractType", out var ctp) && ctp.GetString() != "LinearPerpetual") continue;
                list.Add(new Instrument
                {
                    Symbol = s.GetProperty("symbol").GetString()!,
                    FeedId = Id,
                    Exchange = DisplayName,
                    TickSize = Dec(s.GetProperty("priceFilter").GetProperty("tickSize")) / 1.0000000000000000000000000000m,
                    VolumeStep = Dec(s.GetProperty("lotSizeFilter").GetProperty("qtyStep")) / 1.0000000000000000000000000000m,
                    BaseAsset = s.GetProperty("baseCoin").GetString() ?? "",
                    QuoteAsset = s.GetProperty("quoteCoin").GetString() ?? "",
                    Description = $"{s.GetProperty("baseCoin").GetString()}/{s.GetProperty("quoteCoin").GetString()} perpetual",
                });
            }

            cursor = result.TryGetProperty("nextPageCursor", out var c) ? c.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor) && !ct.IsCancellationRequested);

        string[] popular = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "DOGEUSDT" };
        return list.OrderBy(i => Array.IndexOf(popular, i.Symbol) is var k && k >= 0 ? k : 999).ThenBy(i => i.Symbol).ToList();
    }

    public async Task<IReadOnlyDictionary<string, TickerStats>> GetTickerStatsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_rest}/tickers?category=linear", ct).ConfigureAwait(false));
        var res = new Dictionary<string, TickerStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in doc.RootElement.GetProperty("result").GetProperty("list").EnumerateArray())
        {
            string sym = t.GetProperty("symbol").GetString()!;
            decimal pct = Dec(t.GetProperty("price24hPcnt")) * 100m;
            res[sym] = new TickerStats(sym, Dec(t.GetProperty("lastPrice")), pct, Dec(t.GetProperty("turnover24h")),
                Dec(t.GetProperty("highPrice24h")), Dec(t.GetProperty("lowPrice24h")));
        }

        return res;
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_rest}/recent-trade?category=linear&symbol={instrument.Symbol}&limit=1000", ct).ConfigureAwait(false));
        var list = new List<Trade>();
        foreach (var t in doc.RootElement.GetProperty("result").GetProperty("list").EnumerateArray())
        {
            var tr = Parse(t);
            // This endpoint has no numeric cursor. Native identities reconcile inclusive boundary overlap.
            if (tr.Time >= fromUtc && tr.Time <= toUtc) list.Add(tr);
        }

        // vienen de la más nueva a la más vieja
        list.Reverse();
        if (list.Count > 0) onBatch(list);
        progress?.Report(1);
    }

    // ======================================================================= velas (historia larga)

    public async Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_rest}/instruments-info?category=linear&symbol={instrument.Symbol}", ct).ConfigureAwait(false));
        var list = doc.RootElement.GetProperty("result").GetProperty("list");
        if (list.GetArrayLength() == 0 || !list[0].TryGetProperty("launchTime", out var lt)) return null;
        return long.TryParse(lt.GetString(), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
    }

    /// <summary>Velas por REST de a 1000 (Bybit no informa el volumen comprador: el delta queda en cero).</summary>
    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        long step = minutes * 60_000L;
        long start = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / step * step;
        long end = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        double total = Math.Max(1, end - start);
        for (long cur = start; cur < end && !ct.IsCancellationRequested;)
        {
            long pageEnd = Math.Min(end, cur + 1000 * step) - 1;
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                $"{_rest}/kline?category=linear&symbol={instrument.Symbol}&interval={minutes}&start={cur}&end={pageEnd}&limit=1000", ct).ConfigureAwait(false));
            var list = doc.RootElement.GetProperty("result").GetProperty("list");
            var batch = new List<Kline>(list.GetArrayLength());
            foreach (var k in list.EnumerateArray())
            {
                long t = long.Parse(k[0].GetString()!, CultureInfo.InvariantCulture);
                batch.Add(new Kline(t, Dec(k[1]), Dec(k[2]), Dec(k[3]), Dec(k[4]), Dec(k[5]), 0, false));
            }

            // vienen de la más nueva a la más vieja
            batch.Reverse();
            if (batch.Count > 0 && !onBatch(batch)) return;
            cur = pageEnd + 1;
            progress?.Report((cur - start) / total);
        }

        progress?.Report(1);
    }

    private Trade Parse(JsonElement t)
    {
        long time = t.TryGetProperty("T", out var tt) ? (tt.ValueKind == JsonValueKind.String ? long.Parse(tt.GetString()!, CultureInfo.InvariantCulture) : tt.GetInt64())
            : long.Parse(t.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);
        string side = t.TryGetProperty("S", out var s) ? s.GetString() ?? "" : t.GetProperty("side").GetString() ?? "";
        decimal price = Dec(t.TryGetProperty("p", out var p) ? p : t.GetProperty("price"));
        decimal qty = Dec(t.TryGetProperty("v", out var v) ? v : t.GetProperty("size"));
        string? nativeId = t.TryGetProperty("execId", out var executionId) && executionId.ValueKind == JsonValueKind.String
            ? executionId.GetString() : null;
        if (string.IsNullOrEmpty(nativeId) && t.TryGetProperty("i", out var streamId) && streamId.ValueKind == JsonValueKind.String)
            nativeId = streamId.GetString();
        bool hasNativeIdentity = !string.IsNullOrEmpty(nativeId);
        // Without a native key, each reception is a distinct observation, not an exactly reconciled overlap.
        string eventId = hasNativeIdentity ? nativeId! : $"observation:{Guid.NewGuid():N}";
        var provenance = new TradeProvenance(DataQuality.Raw | (hasNativeIdentity ? DataQuality.None : DataQuality.Partial),
            SourceIdentity: Id, SourceEventId: eventId);
        return new Trade(Interlocked.Increment(ref _nextId), DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime,
            price, qty, side == "Buy" ? TradeSide.Buy : TradeSide.Sell, provenance);
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("Bybit", (st, msg) => sink.OnStatus(instrument, st, msg));
        _ = Task.Run(() => RunAsync(instrument, sink, sub));
        return sub;
    }

    private async Task RunAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription liveSub)
    {
        var ct = liveSub.Token;
        int failures = 0;
        int requested = 0;
        bool everConnected = false;
        DateTime lastTradeTime = DateTime.MinValue;
        var stale = TimeSpan.FromSeconds(15); // el libro llega varias veces por segundo
        while (!ct.IsCancellationRequested)
        {
            var conn = liveSub.BeginConnection();
            var book = new OrderBookBuilder();
            long? lastDepthSequence = null;
            bool receivedAny = false;
            string reason = "connection lost";
            try
            {
                if (!everConnected) liveSub.SetChannel("data", FeedStatus.Connecting, "Connecting to Bybit…");
                WebSocket ws;
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                    ws = await ConnectSocketAsync(connectCts.Token).ConfigureAwait(false);
                }
                using var ownedSocket = ws;

                var sub = $"{{\"op\":\"subscribe\",\"args\":[\"publicTrade.{instrument.Symbol}\",\"orderbook.200.{instrument.Symbol}\"]}}";
                await ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, conn.Token).ConfigureAwait(false);

                // rellena lo que se perdió durante el corte (Bybit da las últimas 1000 operaciones)
                if (lastTradeTime > DateTime.MinValue)
                {
                    try
                    {
                        await LoadTradesAsync(instrument, lastTradeTime, DateTime.UtcNow.AddMinutes(1), 0, batch =>
                        {
                            sink.OnTrades(instrument, batch);
                            lastTradeTime = batch[^1].Time;
                            return true;
                        }, null, conn.Token).ConfigureAwait(false);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // el relleno es de mejor esfuerzo
                    }
                }

                liveSub.SetChannel("data", FeedStatus.Connected, null);
                everConnected = true;
                conn.CancelAfter(stale);

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Token);
                _ = Task.Run(async () =>
                {
                    while (!pingCts.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20), pingCts.Token).ConfigureAwait(false);
                        await ws.SendAsync("{\"op\":\"ping\"}"u8.ToArray(), WebSocketMessageType.Text, true, pingCts.Token).ConfigureAwait(false);
                    }
                }, pingCts.Token);

                var buf = new byte[1 << 16];
                var ms = new MemoryStream();
                DateTime lastPublish = DateTime.MinValue;
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buf, conn.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        reason = "server closed the connection";
                        break;
                    }

                    receivedAny = true;
                    failures = 0;
                    conn.CancelAfter(stale);
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("topic", out var topicEl)) continue;
                    string topic = topicEl.GetString() ?? "";
                    if (topic.StartsWith("publicTrade.", StringComparison.Ordinal))
                    {
                        var trades = new List<Trade>();
                        foreach (var t in root.GetProperty("data").EnumerateArray())
                        {
                            var tr = Parse(t);
                            if (tr.Time < lastTradeTime) continue; // Already included by the reconnect backfill.
                            trades.Add(tr);
                        }

                        if (trades.Count > 0)
                        {
                            lastTradeTime = trades[^1].Time;
                            sink.OnTrades(instrument, trades);
                        }
                    }
                    else if (topic.StartsWith("orderbook.", StringComparison.Ordinal))
                    {
                        string type = root.GetProperty("type").GetString() ?? "";
                        var data = root.GetProperty("data");
                        bool snapshot = type == "snapshot" || (data.TryGetProperty("u", out var u) && u.GetInt64() == 1);
                        if (snapshot) book.Clear();
                        foreach (var b in data.GetProperty("b").EnumerateArray())
                            book.Set(BookSide.Bid, instrument.ToTicks(Dec(b[0])), Dec(b[1]));
                        foreach (var a in data.GetProperty("a").EnumerateArray())
                            book.Set(BookSide.Ask, instrument.ToTicks(Dec(a[0])), Dec(a[1]));
                        var now = DateTime.UtcNow;
                        if (snapshot || (now - lastPublish).TotalMilliseconds >= 90)
                        {
                            var update = book.BuildUpdate(now, 500, snapshot);
                            long? sequence = data.TryGetProperty("u", out var updateId) ? updateId.GetInt64() : null;
                            sink.OnDepth(instrument, new DepthUpdate
                            {
                                Snapshot = update.Snapshot,
                                Changes = update.Changes,
                                IsFullRefresh = update.IsFullRefresh,
                                ProviderId = Id,
                                Venue = DisplayName,
                                ReceivedAt = now,
                                Sequence = sequence,
                                PreviousSequence = snapshot ? null : lastDepthSequence,
                                Coverage = update.Coverage | DepthCoverage.PartialBook,
                            });
                            lastDepthSequence = sequence;
                            lastPublish = now;
                        }
                    }
                }

                pingCts.Cancel();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = liveSub.WasRequested(ref requested) ? "reconnection requested" : receivedAny ? "no data arriving" : "response timed out";
                if (!receivedAny) failures++;
            }
            catch (Exception ex)
            {
                failures++;
                reason = ex.Message;
            }
            finally
            {
                liveSub.EndConnection(conn);
            }

            if (ct.IsCancellationRequested) break;
            var wait = LiveSubscription.Backoff(failures);
            liveSub.SetChannel("data", FeedStatus.Reconnecting, failures == 0
                ? $"Reconnecting ({reason})…"
                : $"Reconnecting in {wait.TotalSeconds:0} s · attempt {failures} ({reason})");
            await Task.Delay(wait, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        liveSub.SetChannel("data", FeedStatus.Disconnected, null);
    }

    private async Task<WebSocket> ConnectSocketAsync(CancellationToken ct)
    {
        if (_connectWebSocket != null)
            return await _connectWebSocket(_webSocketEndpoint, ct).ConfigureAwait(false);
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            _configureSocket?.Invoke(socket.Options);
            await socket.ConnectAsync(_webSocketEndpoint, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
