using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Alpaca;

/// <summary>Conexión de Alpaca: claves de la cuenta (la de práctica alcanza y es gratis en todo el mundo).</summary>
public sealed class AlpacaSettings
{
    public string KeyId { get; set; } = "";
    public string Secret { get; set; } = "";

    /// <summary>Datos en vivo: iex (plan gratis) o sip (plan pago, todas las bolsas).</summary>
    public string LiveFeed { get; set; } = "iex";

    /// <summary>Servidores alternativos (pruebas).</summary>
    public string? DataUrl { get; set; }
    public string? TradingUrl { get; set; }
    public string? StreamUrl { get; set; }
}

/// <summary>
/// Acciones y ETFs de EE.UU. por Alpaca: historia tick a tick de TODAS las bolsas (SIP) desde 2016 gratis con el plan
/// básico (salvo los últimos 15 minutos), velas de 1 minuto y precios en vivo de IEX. SPY, QQQ, IWM y DIA sirven para
/// estudiar el S&amp;P 500, el Nasdaq 100, el Russell 2000 y el Dow con operaciones reales. Las operaciones no traen el
/// lado agresor: sale de si el precio subió o bajó (regla del tick).
/// </summary>
public sealed class AlpacaFeed : IMarketDataFeed, IKlineHistory, IDayDownloader
{
    public const string FeedId = "alpaca";
    private readonly AlpacaSettings _s;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _rate = new(1, 1);
    private DateTime _lastCall = DateTime.MinValue;
    private long _liveId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public AlpacaFeed(AlpacaSettings settings)
    {
        _s = settings;
        _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", settings.KeyId.Trim());
        _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", settings.Secret.Trim());
    }

    public string Id => FeedId;
    public string DisplayName => "Alpaca";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public string HistoryNote => "US consolidated (SIP) trade history via Alpaca, available from 2016 subject to provider entitlement";

    private string Data => (_s.DataUrl ?? "https://data.alpaca.markets").TrimEnd('/');
    private string Trading => (_s.TradingUrl ?? "https://paper-api.alpaca.markets").TrimEnd('/');
    private string Stream => (_s.StreamUrl ?? "wss://stream.data.alpaca.markets/v2/" + (_s.LiveFeed == "sip" ? "sip" : "iex")).TrimEnd('/');

    /// <summary>GET con el límite del plan gratis (200 por minuto) y reintento si Alpaca pide esperar.</summary>
    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.KeyId) || string.IsNullOrWhiteSpace(_s.Secret))
            throw new InvalidOperationException("Alpaca API key and secret are missing (generate them in your practice account).");
        for (int attempt = 0; ; attempt++)
        {
            await _rate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wait = TimeSpan.FromMilliseconds(320) - (DateTime.UtcNow - _lastCall);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                _lastCall = DateTime.UtcNow;
            }
            finally
            {
                _rate.Release();
            }

            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 429 && attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 + attempt * 5), ct).ConfigureAwait(false);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                // Alpaca a veces responde una página HTML (nginx): se muestra solo el código
                string msg = body.TrimStart().StartsWith('<') ? resp.ReasonPhrase ?? "" : body;
                try
                {
                    using var err = JsonDocument.Parse(body);
                    if (err.RootElement.TryGetProperty("message", out var m)) msg = m.GetString() ?? body;
                }
                catch (JsonException)
                {
                }

                throw new HttpRequestException(resp.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "Alpaca rejected credentials or your plan does not include these data: " + msg,
                    _ => $"Alpaca returned {(int)resp.StatusCode}: {msg}",
                });
            }

            return JsonDocument.Parse(body);
        }
    }

    private static readonly string[] Popular = { "SPY", "QQQ", "IWM", "DIA", "NVDA", "AAPL", "MSFT", "TSLA", "AMZN", "META", "GOOGL", "AMD" };

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        using var doc = await GetAsync($"{Trading}/v2/assets?status=active&asset_class=us_equity", ct).ConfigureAwait(false);
        var list = new List<Instrument>();
        foreach (var a in doc.RootElement.EnumerateArray())
        {
            if (a.TryGetProperty("tradable", out var tr) && tr.ValueKind == JsonValueKind.False) continue;
            string sym = a.GetProperty("symbol").GetString()!;
            list.Add(new Instrument
            {
                Symbol = sym,
                FeedId = FeedId,
                Exchange = a.TryGetProperty("exchange", out var ex) ? ex.GetString() ?? "Alpaca" : "Alpaca",
                TickSize = 0.01m,
                VolumeStep = 1,
                PointValue = 1,
                QuoteAsset = "USD",
                Description = a.TryGetProperty("name", out var n) ? n.GetString() ?? sym : sym,
            });
        }

        return list.OrderBy(x => Array.IndexOf(Popular, x.Symbol) is var k && k >= 0 ? k : 999).ThenBy(x => x.Symbol).ToList();
    }

    private static string Rfc(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", Inv);

    /// <summary>Lo más nuevo que deja pedir el plan gratis (SIP con 15 minutos de demora).</summary>
    private static DateTime Latest => DateTime.UtcNow.AddMinutes(-16);

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (toUtc > Latest) toUtc = Latest;
        if (toUtc <= fromUtc) return;
        string? token = null;
        long lastId = 0;
        decimal last = 0;
        var side = TradeSide.Buy;
        double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        do
        {
            var url = $"{Data}/v2/stocks/{Uri.EscapeDataString(instrument.Symbol)}/trades?start={Uri.EscapeDataString(Rfc(fromUtc))}&end={Uri.EscapeDataString(Rfc(toUtc))}&limit=10000&feed=sip"
                      + (token != null ? "&page_token=" + Uri.EscapeDataString(token) : "");
            using var doc = await GetAsync(url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var batch = new List<Trade>();
            if (root.TryGetProperty("trades", out var trades) && trades.ValueKind == JsonValueKind.Array)
                foreach (var t in trades.EnumerateArray())
                {
                    var time = DateTime.Parse(t.GetProperty("t").GetString()!, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                    decimal price = instrument.RoundPrice(t.GetProperty("p").GetDecimal());
                    decimal size = t.GetProperty("s").GetDecimal();
                    if (size <= 0) continue;
                    batch.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, price, size, HistoryBuilder.TickRule(price, ref last, ref side)));
                }

            token = root.TryGetProperty("next_page_token", out var nt) && nt.ValueKind == JsonValueKind.String ? nt.GetString() : null;
            if (batch.Count > 0)
            {
                if (!onBatch(batch)) return;
                progress?.Report(Math.Min(1, (batch[^1].Time - fromUtc).TotalSeconds / total));
            }
        } while (!string.IsNullOrEmpty(token) && !ct.IsCancellationRequested);

        progress?.Report(1);
    }

    public async Task<List<Trade>?> DownloadDayAsync(Instrument instrument, DateOnly day, IProgress<double>? progress, CancellationToken ct)
    {
        var list = new List<Trade>();
        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await LoadTradesAsync(instrument, from, from.AddDays(1), null, b =>
        {
            list.AddRange(b);
            return true;
        }, progress, ct).ConfigureAwait(false);
        return list.Count > 0 ? list : null;
    }

    // ===================================================================== velas

    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(new DateTime(2016, 1, 4, 0, 0, 0, DateTimeKind.Utc));

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (toUtc > Latest) toUtc = Latest;
        if (toUtc <= fromUtc) return;
        int m = minutes >= 15 ? 15 : minutes >= 5 ? 5 : 1;
        string? token = null;
        double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        do
        {
            var url = $"{Data}/v2/stocks/{Uri.EscapeDataString(instrument.Symbol)}/bars?timeframe={m}Min&start={Uri.EscapeDataString(Rfc(fromUtc))}&end={Uri.EscapeDataString(Rfc(toUtc))}&limit=10000&feed=sip&adjustment=raw"
                      + (token != null ? "&page_token=" + Uri.EscapeDataString(token) : "");
            using var doc = await GetAsync(url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var batch = new List<Kline>();
            if (root.TryGetProperty("bars", out var bars) && bars.ValueKind == JsonValueKind.Array)
                foreach (var b in bars.EnumerateArray())
                {
                    var time = DateTime.Parse(b.GetProperty("t").GetString()!, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                    batch.Add(new Kline(new DateTimeOffset(time).ToUnixTimeMilliseconds(), b.GetProperty("o").GetDecimal(), b.GetProperty("h").GetDecimal(),
                        b.GetProperty("l").GetDecimal(), b.GetProperty("c").GetDecimal(), Math.Max(1, b.GetProperty("v").GetDecimal()), 0, false));
                }

            token = root.TryGetProperty("next_page_token", out var nt) && nt.ValueKind == JsonValueKind.String ? nt.GetString() : null;
            if (batch.Count > 0)
            {
                if (!onBatch(batch)) return;
                progress?.Report(Math.Min(1, (DateTimeOffset.FromUnixTimeMilliseconds(batch[^1].OpenTimeMs).UtcDateTime - fromUtc).TotalSeconds / total));
            }
        } while (!string.IsNullOrEmpty(token) && !ct.IsCancellationRequested);
    }

    public string CacheTag => "alpaca";

    // ===================================================================== en vivo (IEX en el plan gratis)

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("Alpaca", (st, msg) => sink.OnStatus(instrument, st, msg));
        _ = Task.Run(() => RunAsync(instrument, sink, sub));
        return sub;
    }

    private async Task RunAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription live)
    {
        var ct = live.Token;
        int failures = 0, requested = 0;
        bool ever = false;
        decimal last = 0;
        var lastSide = TradeSide.Buy;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            var conn = live.BeginConnection();
            string reason = "connection lost";
            bool any = false;
            try
            {
                if (!ever) live.SetChannel("data", FeedStatus.Connecting, "Connecting to Alpaca…");
                using (var c = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                {
                    c.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(new Uri(Stream), c.Token).ConfigureAwait(false);
                }

                await SendAsync(ws, JsonSerializer.Serialize(new { action = "auth", key = _s.KeyId.Trim(), secret = _s.Secret.Trim() }), conn.Token).ConfigureAwait(false);
                await SendAsync(ws, JsonSerializer.Serialize(new { action = "subscribe", trades = new[] { instrument.Symbol }, quotes = new[] { instrument.Symbol } }), conn.Token).ConfigureAwait(false);
                var buf = new byte[1 << 16];
                var ms = new MemoryStream();
                var book = new OrderBookBuilder();
                while (ws.State == WebSocketState.Open && !conn.IsCancellationRequested)
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
                        reason = "Alpaca closed the connection";
                        break;
                    }

                    using var doc = JsonDocument.Parse(ms.ToArray());
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                    var trades = new List<Trade>();
                    foreach (var m in doc.RootElement.EnumerateArray())
                    {
                        string type = m.TryGetProperty("T", out var t) ? t.GetString() ?? "" : "";
                        switch (type)
                        {
                            case "success" when m.TryGetProperty("msg", out var msg) && msg.GetString() == "authenticated":
                                live.SetChannel("data", FeedStatus.Connected, null);
                                ever = true;
                                failures = 0;
                                break;
                            case "error":
                                throw new InvalidOperationException("Alpaca: " + (m.TryGetProperty("msg", out var em) ? em.GetString() : "error"));
                            case "t":
                            {
                                any = true;
                                var time = DateTime.Parse(m.GetProperty("t").GetString()!, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                                decimal price = instrument.RoundPrice(m.GetProperty("p").GetDecimal());
                                decimal size = m.GetProperty("s").GetDecimal();
                                if (size > 0) trades.Add(new Trade(Interlocked.Increment(ref _liveId), time, price, size, HistoryBuilder.TickRule(price, ref last, ref lastSide)));
                                break;
                            }

                            case "q":
                            {
                                any = true;
                                decimal bp = m.TryGetProperty("bp", out var bpe) ? bpe.GetDecimal() : 0, ap = m.TryGetProperty("ap", out var ape) ? ape.GetDecimal() : 0;
                                if (bp <= 0 || ap <= 0) break;
                                book.Clear();
                                book.Set(BookSide.Bid, instrument.ToTicks(bp), Math.Max(1, m.TryGetProperty("bs", out var bs) ? bs.GetDecimal() : 1));
                                book.Set(BookSide.Ask, instrument.ToTicks(ap), Math.Max(1, m.TryGetProperty("as", out var asz) ? asz.GetDecimal() : 1));
                                sink.OnDepth(instrument, book.BuildUpdate(DateTime.UtcNow, 2, true));
                                break;
                            }
                        }
                    }

                    if (trades.Count > 0) sink.OnTrades(instrument, trades);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = live.WasRequested(ref requested) ? "reconnection requested" : "response timed out";
                if (!any) failures++;
            }
            catch (Exception ex)
            {
                failures++;
                reason = ex.Message;
            }
            finally
            {
                live.EndConnection(conn);
            }

            if (ct.IsCancellationRequested) break;
            var wait = LiveSubscription.Backoff(failures);
            live.SetChannel("data", FeedStatus.Reconnecting, $"Reconnecting in {wait.TotalSeconds:0} s ({reason})");
            await Task.Delay(wait, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        live.SetChannel("data", FeedStatus.Disconnected, null);
    }

    private static Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    /// <summary>Prueba las claves pidiendo un día de operaciones de SPY.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        using var doc = await GetAsync($"{Data}/v2/stocks/SPY/trades?start={Uri.EscapeDataString(Rfc(DateTime.UtcNow.AddDays(-5)))}&end={Uri.EscapeDataString(Rfc(Latest))}&limit=5&feed=sip", ct).ConfigureAwait(false);
        int n = doc.RootElement.TryGetProperty("trades", out var t) && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength() : 0;
        return (true, n > 0 ? "Credentials OK: consolidated SIP trade history is available (SPY, QQQ…)." : "Credentials OK, but no recent SPY trades were returned.");
    }

    public void Dispose() => _http.Dispose();
}
