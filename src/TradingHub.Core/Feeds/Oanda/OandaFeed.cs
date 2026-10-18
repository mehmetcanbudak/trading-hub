using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Oanda;

/// <summary>Conexión de OANDA (API v20): token personal y cuenta de práctica (gratis) o real.</summary>
public sealed class OandaSettings
{
    public bool Practice { get; set; } = true;
    public string Token { get; set; } = "";

    /// <summary>Cuenta (101-001-…); vacía = la primera del token.</summary>
    public string AccountId { get; set; } = "";

    /// <summary>Servidores alternativos (pruebas).</summary>
    public string? RestUrl { get; set; }
    public string? StreamUrl { get; set; }
}

/// <summary>
/// OANDA v20: CFDs de índices (NAS100_USD, SPX500_USD, US30_USD, DE30_EUR…), metales, petróleo y forex. Precios en vivo
/// por streaming y años de historia en velas de 5 segundos o 1 minuto, gratis con una cuenta de práctica. Los CFDs no
/// tienen operaciones reales: cada cotización (precio medio) cuenta como una operación de volumen 1 y el lado sale de
/// si el precio subió o bajó, igual que los archivos importados.
/// </summary>
public sealed class OandaFeed : IMarketDataFeed, IKlineHistory, IDayDownloader
{
    public const string FeedId = "oanda";
    private readonly OandaSettings _s;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _stream = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string? _account;
    private long _liveId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public OandaFeed(OandaSettings settings)
    {
        _s = settings;
        foreach (var h in new[] { _http, _stream })
        {
            h.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
            h.DefaultRequestHeaders.Add("Accept-Datetime-Format", "RFC3339");
        }
    }

    public string Id => FeedId;
    public string DisplayName => "OANDA";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public string HistoryNote => "OANDA 5-second bars converted to synthetic trades (CFDs have no centralized exchange trades)";

    private string Rest => (_s.RestUrl ?? (_s.Practice ? "https://api-fxpractice.oanda.com" : "https://api-fxtrade.oanda.com")).TrimEnd('/');
    private string StreamBase => (_s.StreamUrl ?? (_s.Practice ? "https://stream-fxpractice.oanda.com" : "https://stream-fxtrade.oanda.com")).TrimEnd('/');

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.Token)) throw new InvalidOperationException("OANDA token is missing (generate it under Manage API Access).");
        using var resp = await _http.GetAsync(Rest + path, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = body;
            try
            {
                using var err = JsonDocument.Parse(body);
                if (err.RootElement.TryGetProperty("errorMessage", out var em)) msg = em.GetString() ?? body;
            }
            catch (JsonException)
            {
            }

            throw new HttpRequestException(resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "OANDA rejected token (check that practice/real environment matches the token)"
                : $"OANDA returned {(int)resp.StatusCode}: {msg}");
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>La cuenta a usar (la configurada o la primera del token).</summary>
    public async Task<string> AccountAsync(CancellationToken ct)
    {
        if (_account != null) return _account;
        if (!string.IsNullOrWhiteSpace(_s.AccountId)) return _account = _s.AccountId.Trim();
        using var doc = await GetAsync("/v3/accounts", ct).ConfigureAwait(false);
        var first = doc.RootElement.GetProperty("accounts").EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The token has no accounts.");
        return _account = first.GetProperty("id").GetString()!;
    }

    private static readonly string[] Popular = { "NAS100_USD", "SPX500_USD", "US30_USD", "US2000_USD", "DE30_EUR", "UK100_GBP", "JP225_USD", "XAU_USD", "BCO_USD", "WTICO_USD", "EUR_USD", "GBP_USD", "USD_JPY" };

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var account = await AccountAsync(ct).ConfigureAwait(false);
        using var doc = await GetAsync($"/v3/accounts/{account}/instruments", ct).ConfigureAwait(false);
        var list = new List<Instrument>();
        foreach (var i in doc.RootElement.GetProperty("instruments").EnumerateArray())
        {
            string name = i.GetProperty("name").GetString()!;
            int precision = i.TryGetProperty("displayPrecision", out var dp) ? dp.GetInt32() : 5;
            int units = i.TryGetProperty("tradeUnitsPrecision", out var tu) ? tu.GetInt32() : 0;
            string type = i.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var parts = name.Split('_');
            list.Add(new Instrument
            {
                Symbol = name,
                FeedId = FeedId,
                Exchange = "OANDA",
                TickSize = Pow10(-precision),
                VolumeStep = Pow10(-units),
                PointValue = 1,
                BaseAsset = parts[0],
                QuoteAsset = parts.Length > 1 ? parts[1] : "",
                Description = (i.TryGetProperty("displayName", out var dn) ? dn.GetString() : name) + (type == "CFD" ? " · CFD" : type == "METAL" ? " · metal" : type == "CURRENCY" ? " · forex" : ""),
            });
        }

        return list.OrderBy(x => Array.IndexOf(Popular, x.Symbol) is var k && k >= 0 ? k : 999).ThenBy(x => x.Symbol).ToList();
    }

    private static decimal Pow10(int e)
    {
        decimal v = 1;
        for (int i = 0; i < Math.Abs(e); i++) v = e < 0 ? v / 10 : v * 10;
        return v;
    }

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.String ? decimal.Parse(e.GetString()!, NumberStyles.Float, Inv) : e.GetDecimal();

    private static string Rfc(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", Inv);

    /// <summary>Velas (precio medio) en orden, de a 5000; <paramref name="onBatch"/> devuelve false para cortar.</summary>
    private async Task CandlesAsync(string instrument, string granularity, TimeSpan length, DateTime fromUtc, DateTime toUtc,
        Func<List<(DateTime Time, decimal O, decimal H, decimal L, decimal C, decimal V, bool Complete)>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var cursor = fromUtc;
        double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        while (cursor < toUtc && !ct.IsCancellationRequested)
        {
            // de a 5000 velas como máximo por pedido
            var end = cursor + TimeSpan.FromTicks(length.Ticks * 4999);
            if (end > toUtc) end = toUtc;
            using var doc = await GetAsync($"/v3/instruments/{instrument}/candles?price=M&granularity={granularity}&from={Uri.EscapeDataString(Rfc(cursor))}&to={Uri.EscapeDataString(Rfc(end))}", ct).ConfigureAwait(false);
            var batch = new List<(DateTime, decimal, decimal, decimal, decimal, decimal, bool)>();
            foreach (var c in doc.RootElement.GetProperty("candles").EnumerateArray())
            {
                if (!c.TryGetProperty("mid", out var mid)) continue;
                var time = DateTime.Parse(c.GetProperty("time").GetString()!, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                if (time < cursor || time >= toUtc) continue;
                batch.Add((time, Dec(mid.GetProperty("o")), Dec(mid.GetProperty("h")), Dec(mid.GetProperty("l")), Dec(mid.GetProperty("c")),
                    c.TryGetProperty("volume", out var v) ? v.GetDecimal() : 1, c.TryGetProperty("complete", out var cp) && cp.GetBoolean()));
            }

            if (batch.Count > 0 && !onBatch(batch)) return;
            cursor = end;
            progress?.Report(Math.Min(1, (cursor - fromUtc).TotalSeconds / total));
        }
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        long lastId = 0;
        bool stop = false;
        await CandlesAsync(instrument.Symbol, "S5", TimeSpan.FromSeconds(5), fromUtc, toUtc, batch =>
        {
            var trades = new List<Trade>(batch.Count * 4);
            foreach (var c in batch)
                if (c.Complete) HistoryBuilder.Synthesize(c.Time, TimeSpan.FromSeconds(5), c.O, c.H, c.L, c.C, c.V, instrument, ref lastId, trades);
            if (trades.Count > 0 && !onBatch(trades)) stop = true;
            return !stop;
        }, progress, ct).ConfigureAwait(false);
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

    // ===================================================================== velas (historia larga del gráfico)

    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        string granularity = minutes switch { >= 15 => "M15", >= 5 => "M5", _ => "M1" };
        int m = minutes >= 15 ? 15 : minutes >= 5 ? 5 : 1;
        await CandlesAsync(instrument.Symbol, granularity, TimeSpan.FromMinutes(m), fromUtc, toUtc, batch =>
            onBatch(batch.Where(c => c.Complete).Select(c => new Kline(new DateTimeOffset(c.Time).ToUnixTimeMilliseconds(), c.O, c.H, c.L, c.C, Math.Max(1, c.V), 0, false)).ToList()),
            progress, ct).ConfigureAwait(false);
    }

    public string CacheTag => "oanda";

    // ===================================================================== en vivo

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("OANDA", (st, msg) => sink.OnStatus(instrument, st, msg));
        _ = Task.Run(() => RunAsync(instrument, sink, sub));
        return sub;
    }

    private async Task RunAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription live)
    {
        var ct = live.Token;
        int failures = 0, requested = 0;
        bool ever = false;
        decimal lastPrice = 0;
        var lastSide = TradeSide.Buy;
        while (!ct.IsCancellationRequested)
        {
            var conn = live.BeginConnection();
            string reason = "connection lost";
            bool any = false;
            try
            {
                if (!ever) live.SetChannel("prices", FeedStatus.Connecting, "Connecting to OANDA…");
                var account = await AccountAsync(conn.Token).ConfigureAwait(false);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{StreamBase}/v3/accounts/{account}/pricing/stream?instruments={instrument.Symbol}");
                using var resp = await _stream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException(resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "OANDA rejected token" : $"OANDA returned {(int)resp.StatusCode}");
                await using var body = await resp.Content.ReadAsStreamAsync(conn.Token).ConfigureAwait(false);
                using var reader = new StreamReader(body);
                live.SetChannel("prices", FeedStatus.Connected, null);
                ever = true;
                conn.CancelAfter(TimeSpan.FromSeconds(20)); // llega un latido cada 5 s
                var book = new OrderBookBuilder();
                while (!conn.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(conn.Token).ConfigureAwait(false);
                    if (line == null)
                    {
                        reason = "OANDA closed the connection";
                        break;
                    }

                    if (line.Length == 0) continue;
                    any = true;
                    failures = 0;
                    conn.CancelAfter(TimeSpan.FromSeconds(20));
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (type == "HEARTBEAT" || !root.TryGetProperty("bids", out var bids) || !root.TryGetProperty("asks", out var asks)) continue;
                    var b = bids.EnumerateArray().Select(x => (P: Dec(x.GetProperty("price")), L: x.TryGetProperty("liquidity", out var li) ? li.GetDecimal() : 0)).ToList();
                    var a = asks.EnumerateArray().Select(x => (P: Dec(x.GetProperty("price")), L: x.TryGetProperty("liquidity", out var li) ? li.GetDecimal() : 0)).ToList();
                    if (b.Count == 0 || a.Count == 0) continue;
                    var time = root.TryGetProperty("time", out var tm) && DateTime.TryParse(tm.GetString(), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTime.UtcNow;
                    decimal mid = instrument.RoundPrice((b[0].P + a[0].P) / 2);
                    var side = HistoryBuilder.TickRule(mid, ref lastPrice, ref lastSide);
                    sink.OnTrades(instrument, new[] { new Trade(Interlocked.Increment(ref _liveId), time, mid, 1, side) });
                    book.Clear();
                    foreach (var x in b) book.Set(BookSide.Bid, instrument.ToTicks(x.P), Math.Max(1, x.L));
                    foreach (var x in a) book.Set(BookSide.Ask, instrument.ToTicks(x.P), Math.Max(1, x.L));
                    sink.OnDepth(instrument, book.BuildUpdate(time, 50, true));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = live.WasRequested(ref requested) ? "reconnection requested" : any ? "no prices arriving" : "response timed out";
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
            live.SetChannel("prices", FeedStatus.Reconnecting, $"Reconnecting in {wait.TotalSeconds:0} s ({reason})");
            await Task.Delay(wait, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        live.SetChannel("prices", FeedStatus.Disconnected, null);
    }

    /// <summary>Prueba el token: cuenta e instrumentos.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        var account = await AccountAsync(ct).ConfigureAwait(false);
        var list = await GetInstrumentsAsync(ct).ConfigureAwait(false);
        int cfds = list.Count(i => i.Description.Contains("CFD", StringComparison.Ordinal));
        return (true, $"Account {account} ({(_s.Practice ? "demo" : "real")}): {list.Count} instruments, {cfds} CFDs (indices such as NAS100_USD and SPX500_USD).");
    }

    public void Dispose()
    {
        _http.Dispose();
        _stream.Dispose();
    }
}
