using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Market;

namespace TradingHub.Feeds.CTrader;

/// <summary>
/// Datos de cualquier broker con cTrader (IC Markets, Pepperstone, FxPro, Fusion…) por su Open API gratuita: CFDs de
/// índices (US100/NAS100, US500, GER40…), forex, metales y más. Precios en vivo, historia tick a tick (bid) y velas.
/// Las cotizaciones no son operaciones: cada tick cuenta como una operación de volumen 1 al precio bid y el lado sale
/// de si subió o bajó (igual que los archivos importados).
/// </summary>
public sealed class CTraderFeed : IMarketDataFeed, IKlineHistory, IDayDownloader
{
    public const string FeedId = "ctrader";
    private const decimal PriceScale = 100000m;
    private readonly CTraderSession _session;
    private readonly string _feedId;
    private readonly ConcurrentDictionary<long, List<(Instrument Inst, IMarketDataSink Sink, LiveState State)>> _sinks = new();
    private long _liveId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public CTraderFeed(Func<CTraderSettings> settings, string feedId = FeedId)
    {
        _feedId = feedId;
        _session = new CTraderSession(settings);
        _session.Spot += OnSpot;
        _session.ConnectionChanged += (up, why) =>
        {
            foreach (var list in _sinks.Values)
                foreach (var (inst, sink, _) in list.ToList())
                    sink.OnStatus(inst, up ? FeedStatus.Connected : FeedStatus.Reconnecting, up ? "cTrader connected" : $"Reconnecting ({why})");
        };
    }

    public CTraderSession Session => _session;
    public string Id => _feedId;
    public string DisplayName => "cTrader";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public string HistoryNote => "broker bid ticks through cTrader Open API";

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        await _session.EnsureConnectedAsync(ct).ConfigureAwait(false);
        string[] popular = { "US100", "NAS100", "USTEC", "US500", "SPX500", "US30", "GER40", "DE40", "UK100", "XAUUSD", "XTIUSD", "EURUSD", "GBPUSD", "USDJPY", "BTCUSD" };
        return _session.Symbols
            .Select(s => new Instrument
            {
                Symbol = s.Name,
                FeedId = _feedId,
                Exchange = _session.AccountTitle,
                TickSize = Pow10(-s.Digits),
                VolumeStep = s.StepVolume,
                PointValue = 1,
                Description = s.Description.Length > 0 ? s.Description : s.Name,
            })
            .OrderBy(i => Array.IndexOf(popular, i.Symbol) is var k && k >= 0 ? k : 999).ThenBy(i => i.Symbol)
            .ToList();
    }

    private static decimal Pow10(int e)
    {
        decimal v = 1;
        for (int i = 0; i < Math.Abs(e); i++) v = e < 0 ? v / 10 : v * 10;
        return v;
    }

    private async Task<long> SymbolIdAsync(Instrument instrument, CancellationToken ct)
    {
        await _session.EnsureConnectedAsync(ct).ConfigureAwait(false);
        var s = _session.Symbols.FirstOrDefault(x => x.Name.Equals(instrument.Symbol, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{instrument.Symbol} is not in your cTrader account.");
        return s.Id;
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>Ticks bid de un rango (de a una semana como máximo por pedido, con páginas «hasMore»), en orden.</summary>
    private async Task<List<(long Ms, long Price)>> TicksAsync(long symbolId, long fromMs, long toMs, IProgress<double>? progress, CancellationToken ct)
    {
        var all = new List<(long, long)>();
        const long week = 7L * 24 * 3600 * 1000;
        double total = Math.Max(1, toMs - fromMs);
        for (long winEnd = toMs; winEnd > fromMs && !ct.IsCancellationRequested;)
        {
            long winStart = Math.Max(fromMs, winEnd - week);
            long to = winEnd;
            for (int page = 0; page < 1000 && to > winStart; page++)
            {
                var res = await _session.HistoryRequestAsync(CTraderSession.GetTickDataReq, new JsonObject
                {
                    ["ctidTraderAccountId"] = _session.AccountId,
                    ["symbolId"] = symbolId,
                    ["type"] = 1, // BID
                    ["fromTimestamp"] = winStart,
                    ["toTimestamp"] = to,
                }, ct).ConfigureAwait(false);
                // el primero viene absoluto y los demás como diferencia con el anterior
                long t = 0, p = 0, oldest = long.MaxValue;
                int n = 0;
                if (res.TryGetProperty("tickData", out var data))
                    foreach (var tick in data.EnumerateArray())
                    {
                        long dt = CTraderSession.Long(tick, "timestamp"), dp = CTraderSession.Long(tick, "tick");
                        if (n++ == 0)
                        {
                            t = dt;
                            p = dp;
                        }
                        else
                        {
                            t += dt;
                            p += dp;
                        }

                        if (t >= winStart && t <= to) all.Add((t, p));
                        oldest = Math.Min(oldest, t);
                    }

                bool more = res.TryGetProperty("hasMore", out var hm) && hm.ValueKind == JsonValueKind.True;
                if (!more || n == 0 || oldest == long.MaxValue) break;
                to = oldest - 1;
            }

            winEnd = winStart;
            progress?.Report(Math.Min(1, (toMs - winEnd) / total));
        }

        all.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return all;
    }

    private static List<Trade> ToTrades(Instrument instrument, List<(long Ms, long Price)> ticks)
    {
        var res = new List<Trade>(ticks.Count);
        long lastId = 0;
        decimal last = 0;
        var side = TradeSide.Buy;
        foreach (var (ms, raw) in ticks)
        {
            decimal price = instrument.RoundPrice(raw / PriceScale);
            var time = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            res.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, price, 1, HistoryBuilder.TickRule(price, ref last, ref side)));
        }

        return res;
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        long id = await SymbolIdAsync(instrument, ct).ConfigureAwait(false);
        var ticks = await TicksAsync(id, Ms(fromUtc), Ms(toUtc), progress, ct).ConfigureAwait(false);
        var trades = ToTrades(instrument, ticks);
        for (int i = 0; i < trades.Count; i += 20000)
            if (!onBatch(trades.GetRange(i, Math.Min(20000, trades.Count - i)))) return;
    }

    public async Task<List<Trade>?> DownloadDayAsync(Instrument instrument, DateOnly day, IProgress<double>? progress, CancellationToken ct)
    {
        long id = await SymbolIdAsync(instrument, ct).ConfigureAwait(false);
        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var ticks = await TicksAsync(id, Ms(from), Ms(from.AddDays(1)) - 1, progress, ct).ConfigureAwait(false);
        return ticks.Count > 0 ? ToTrades(instrument, ticks) : null;
    }

    // ===================================================================== velas

    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        long id = await SymbolIdAsync(instrument, ct).ConfigureAwait(false);
        int period = minutes >= 15 ? 7 : minutes >= 5 ? 5 : 1; // M15, M5, M1
        long chunk = (minutes >= 5 ? 28L : 7L) * 24 * 3600 * 1000;
        long from = Ms(fromUtc), to = Ms(toUtc);
        double total = Math.Max(1, to - from);
        for (long cur = from; cur < to && !ct.IsCancellationRequested;)
        {
            long end = Math.Min(to, cur + chunk);
            var res = await _session.HistoryRequestAsync(CTraderSession.GetTrendbarsReq, new JsonObject
            {
                ["ctidTraderAccountId"] = _session.AccountId,
                ["symbolId"] = id,
                ["period"] = period,
                ["fromTimestamp"] = cur,
                ["toTimestamp"] = end,
            }, ct).ConfigureAwait(false);
            var batch = new List<Kline>();
            if (res.TryGetProperty("trendbar", out var bars))
                foreach (var b in bars.EnumerateArray())
                {
                    long low = CTraderSession.Long(b, "low");
                    decimal Px(long v) => instrument.RoundPrice(v / PriceScale);
                    long openMs = CTraderSession.Long(b, "utcTimestampInMinutes") * 60_000;
                    if (openMs < cur || openMs >= end) continue;
                    batch.Add(new Kline(openMs, Px(low + CTraderSession.Long(b, "deltaOpen")), Px(low + CTraderSession.Long(b, "deltaHigh")), Px(low),
                        Px(low + CTraderSession.Long(b, "deltaClose")), Math.Max(1, CTraderSession.Long(b, "volume")), 0, false));
                }

            batch.Sort((a, b) => a.OpenTimeMs.CompareTo(b.OpenTimeMs));
            if (batch.Count > 0 && !onBatch(batch)) return;
            cur = end;
            progress?.Report((cur - from) / total);
        }
    }

    public string CacheTag => "ctrader";

    // ===================================================================== en vivo

    private sealed class LiveState
    {
        public decimal LastPrice;
        public TradeSide LastSide = TradeSide.Buy;
        public long Bid, Ask;
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var state = new LiveState();
        var sub = new Subscription(this, instrument, sink, state);
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; !sub.Disposed; attempt++)
            {
                try
                {
                    sink.OnStatus(instrument, FeedStatus.Connecting, "Connecting to cTrader…");
                    long id = await SymbolIdAsync(instrument, CancellationToken.None).ConfigureAwait(false);
                    sub.SymbolId = id;
                    _sinks.AddOrUpdate(id, _ => new() { (instrument, sink, state) }, (_, l) =>
                    {
                        lock (l) l.Add((instrument, sink, state));
                        return l;
                    });
                    await _session.SubscribeAsync(id, CancellationToken.None).ConfigureAwait(false);
                    sink.OnStatus(instrument, FeedStatus.Connected, "cTrader connected");
                    return;
                }
                catch (Exception ex)
                {
                    sink.OnStatus(instrument, FeedStatus.Reconnecting, ex.Message);
                    await Task.Delay(LiveSubscription.Backoff(attempt + 1)).ConfigureAwait(false);
                }
            }
        });
        return sub;
    }

    private void OnSpot(long symbolId, long? bid, long? ask, long? timestamp)
    {
        if (!_sinks.TryGetValue(symbolId, out var list)) return;
        (Instrument Inst, IMarketDataSink Sink, LiveState State)[] targets;
        lock (list) targets = list.ToArray();
        var time = timestamp is { } ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;
        foreach (var (inst, sink, st) in targets)
        {
            if (bid is { } b && b > 0)
            {
                bool changed = b != st.Bid;
                st.Bid = b;
                if (changed)
                {
                    decimal price = inst.RoundPrice(b / PriceScale);
                    var side = HistoryBuilder.TickRule(price, ref st.LastPrice, ref st.LastSide);
                    sink.OnTrades(inst, new[] { new Trade(Interlocked.Increment(ref _liveId), time, price, 1, side) });
                }
            }

            if (ask is { } a && a > 0) st.Ask = a;
            if (st.Bid > 0 && st.Ask > 0)
            {
                var book = new OrderBookBuilder();
                book.Set(BookSide.Bid, inst.ToTicks(st.Bid / PriceScale), 1);
                book.Set(BookSide.Ask, inst.ToTicks(st.Ask / PriceScale), 1);
                sink.OnDepth(inst, book.BuildUpdate(time, 2, true));
            }
        }
    }

    private sealed class Subscription(CTraderFeed feed, Instrument instrument, IMarketDataSink sink, LiveState state) : IDisposable, IReconnectable
    {
        public long SymbolId;
        public bool Disposed;

        public void Reconnect() => _ = feed._session.EnsureConnectedAsync(CancellationToken.None);

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (SymbolId == 0 || !feed._sinks.TryGetValue(SymbolId, out var list)) return;
            lock (list) list.RemoveAll(x => ReferenceEquals(x.Sink, sink) && ReferenceEquals(x.State, state) && x.Inst == instrument);
            _ = feed._session.UnsubscribeAsync(SymbolId);
        }
    }

    /// <summary>Prueba la conexión: aplicación, token, cuenta y símbolos.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        await _session.EnsureConnectedAsync(ct).ConfigureAwait(false);
        int indices = _session.Symbols.Count(s => s.Name is "US100" or "NAS100" or "USTEC" or "US500" or "SPX500" or "US30" or "GER40" or "DE40");
        return (true, $"Connected to {_session.AccountTitle}: {_session.Symbols.Count} symbols{(indices > 0 ? $" ({indices} indices)" : "")}.");
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Autorización OAuth de la Open API de cTrader (el usuario entra con su cTrader ID en el navegador).</summary>
public static class CTraderAuth
{
    public const string AuthBase = "https://openapi.ctrader.com/apps";

    public static string AuthorizeUrl(string clientId, string redirectUri) =>
        $"{AuthBase}/auth?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=trading";

    /// <summary>Cambia el código de la redirección por los tokens (acceso y renovación).</summary>
    public static async Task<(string Access, string Refresh, long ExpiresIn)> ExchangeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct, string authBase = AuthBase)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var url = $"{authBase}/token?grant_type=authorization_code&code={Uri.EscapeDataString(code)}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct).ConfigureAwait(false));
        var root = doc.RootElement;
        string access = CTraderSession.Str(root, "accessToken"), refresh = CTraderSession.Str(root, "refreshToken");
        if (access.Length == 0)
            throw new InvalidOperationException("cTrader returned no token: " + (CTraderSession.Str(root, "description") is { Length: > 0 } d ? d : CTraderSession.Str(root, "errorCode")));
        return (access, refresh, CTraderSession.Long(root, "expiresIn"));
    }
}
