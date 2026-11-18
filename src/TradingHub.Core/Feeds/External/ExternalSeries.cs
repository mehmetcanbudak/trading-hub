// Behavior adapted from the KVP capability reference (commit 7fbf237); see THIRD_PARTY_NOTICES.md.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.External;

/// <summary>Single-flight background loads shared by the external series hubs.</summary>
public abstract class ExternalSeriesHub
{
    private readonly ConcurrentDictionary<string, Task> _loading = new();
    private long _generation;

    protected ExternalSeriesHub(Func<DateTime>? utcNow) => UtcNow = utcNow ?? (() => DateTime.UtcNow);

    protected Func<DateTime> UtcNow { get; }

    /// <summary>Raised from any thread when data arrives or the configuration changes.</summary>
    public event Action? Changed;

    public long Generation => Interlocked.Read(ref _generation);

    protected void Bump()
    {
        Interlocked.Increment(ref _generation);
        Changed?.Invoke();
    }

    protected bool IsLoading(string key) => _loading.ContainsKey(key);

    protected void Schedule(string key, Func<Task> work)
    {
        if (_loading.ContainsKey(key)) return;
        var task = new Task<Task>(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (Exception) { /* the work records its own error state */ }
            finally { _loading.TryRemove(key, out _); }
        });
        if (_loading.TryAdd(key, task.Unwrap())) task.Start(TaskScheduler.Default);
    }

    /// <summary>Waits for scheduled loads to finish (tests and the UI snapshot).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_loading.Values.ToArray());
}

// ============================================================================ spot ETF flows

/// <summary>
/// One US trading day of a coin's spot ETF net flow in USD (per fund when supplied). US issuers publish a day's flow
/// after the US close, so the value is known only after <see cref="Date"/> ends.
/// </summary>
public sealed record EtfFlowDay(DateTime Date, double NetUsd, IReadOnlyDictionary<string, double>? Funds = null);

public interface IEtfFlowsProvider
{
    string Name { get; }

    bool Supports(string asset);

    Task<IReadOnlyList<EtfFlowDay>> DailyAsync(string asset, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// Spot ETF flows for the studies: the provider of the user's enabled ETF-flows connection (none by default, so no
/// request is made) and an hourly cache per asset.
/// </summary>
public sealed class EtfFlows : ExternalSeriesHub
{
    public static EtfFlows? Shared { get; set; }

    public const string NotConfigured = "Waiting for an ETF flows connection: add one in Connections → ETF flows.";

    /// <param name="CoveredFrom">Earliest date the last full request asked for: no flows before the first day means none were published.</param>
    private sealed record Entry(DateTime FetchedAt, List<EtfFlowDay>? Days, string? Error, DateTime CoveredFrom);

    private IEtfFlowsProvider? _provider;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    public EtfFlows(Func<DateTime>? utcNow = null) : base(utcNow)
    {
    }

    public IEtfFlowsProvider? Provider
    {
        get => _provider;
        set
        {
            _provider = value;
            _cache.Clear();
            Bump();
        }
    }

    public string? Unavailable(string asset)
    {
        var p = _provider;
        if (p == null) return NotConfigured;
        if (!p.Supports(asset)) return $"{p.Name} has no spot ETFs for {asset}.";
        return null;
    }

    /// <summary>Why the last request for <paramref name="asset"/> failed, or null.</summary>
    public string? Error(string asset) => _cache.TryGetValue(asset, out var e) ? e.Error : null;

    /// <summary>Cached days in date order (null until the first answer); refreshed hourly and when <paramref name="from"/> is earlier than requested before.</summary>
    public IReadOnlyList<EtfFlowDay>? Daily(string asset, DateTime from)
    {
        if (Unavailable(asset) != null) return null;
        _cache.TryGetValue(asset, out var hit);
        var age = UtcNow() - (hit?.FetchedAt ?? DateTime.MinValue);
        // A fund launched after `from` never has days back to it; only a request that started later is missing data.
        bool earlier = hit is { Days: not null, Error: null } && hit.CoveredFrom > from.Date.AddDays(3);
        if (age > TimeSpan.FromHours(1) || (earlier && age > TimeSpan.FromMinutes(1)))
            Schedule(asset, () => RefreshAsync(asset, from, CancellationToken.None));
        return hit?.Days;
    }

    public async Task RefreshAsync(string asset, DateTime from, CancellationToken ct)
    {
        var p = _provider;
        if (p == null || !p.Supports(asset)) return;
        _cache.TryGetValue(asset, out var hit);
        try
        {
            var start = hit?.Days is { Count: > 0 } d && d[0].Date < from ? d[0].Date : from.Date.AddDays(-5);
            if (hit != null && hit.CoveredFrom < start) start = hit.CoveredFrom;
            var got = await p.DailyAsync(asset, start, UtcNow(), ct).ConfigureAwait(false);
            if (!ReferenceEquals(p, _provider)) return;
            _cache[asset] = new Entry(UtcNow(), got.GroupBy(x => x.Date.Date).Select(g => g.Last() with { Date = g.Key }).OrderBy(x => x.Date).ToList(), null, start);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Retried in about ten minutes.
            _cache[asset] = new Entry(UtcNow().AddMinutes(-50), hit?.Days, ex.Message, hit?.CoveredFrom ?? DateTime.MaxValue);
        }
        finally { Bump(); }
    }
}

/// <summary>
/// A user-configured ETF flows API: <c>GET {base}/flows?asset=BTC&amp;from=YYYY-MM-DD&amp;to=YYYY-MM-DD</c> →
/// <c>[{"date":"2026-11-03","net":-123400000,"funds":{"IBIT":-80000000}}]</c> in USD (or <c>"netMillions"</c> in
/// millions; <c>{"data":[…]}</c> also accepted). A day without a net value is skipped, not zero.
/// </summary>
public sealed class JsonEtfFlowsApi : IEtfFlowsProvider
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string? _header, _key;
    private readonly HashSet<string>? _only;

    public JsonEtfFlowsApi(HttpClient http, string baseUrl, string? header, string? key, IEnumerable<string>? assets = null)
    {
        _http = http;
        _base = baseUrl.Trim().TrimEnd('/');
        _header = string.IsNullOrWhiteSpace(header) ? null : header.Trim();
        _key = string.IsNullOrWhiteSpace(key) ? null : key;
        var only = assets?.Select(u => u.Trim().ToUpperInvariant()).Where(u => u.Length > 0).ToHashSet();
        _only = only is { Count: > 0 } ? only : null;
    }

    public string Name => "ETF flows API";

    public bool Supports(string asset) => _only == null || _only.Contains(asset);

    public async Task<IReadOnlyList<EtfFlowDay>> DailyAsync(string asset, DateTime from, DateTime to, CancellationToken ct)
    {
        string url = string.Create(CultureInfo.InvariantCulture, $"{_base}/flows?asset={Uri.EscapeDataString(asset)}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        var root = CustomApi.Rows(await CustomApi.GetAsync(_http, url, _header, _key, ct).ConfigureAwait(false));
        var res = new List<EtfFlowDay>();
        foreach (var o in root.EnumerateArray())
        {
            if (CustomApi.T(o, "date") is not { } date) continue;
            double net = o.TryGetProperty("net", out _) ? CustomApi.D(o, "net") : CustomApi.D(o, "netMillions") * 1e6;
            if (!double.IsFinite(net)) continue;
            Dictionary<string, double>? funds = null;
            if (o.TryGetProperty("funds", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                funds = new Dictionary<string, double>();
                foreach (var p in f.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number) funds[p.Name] = p.Value.GetDouble();
            }

            res.Add(new EtfFlowDay(DateTime.SpecifyKind(date.Date, DateTimeKind.Utc), net, funds));
        }

        return res;
    }
}

// ============================================================================ Polymarket

/// <summary>
/// Public Polymarket market lookup (Gamma API, by slug) and outcome price history (CLOB <c>prices-history</c>). No
/// account or key; called only for a market the user typed into a study.
/// </summary>
public sealed class PolymarketClient
{
    public static readonly Uri DefaultGamma = new("https://gamma-api.polymarket.com/");
    public static readonly Uri DefaultClob = new("https://clob.polymarket.com/");

    private readonly HttpClient _http;
    private readonly Uri _gamma, _clob;

    public PolymarketClient(HttpClient http, Uri? gamma = null, Uri? clob = null)
    {
        _http = http;
        _gamma = Slash(gamma ?? DefaultGamma);
        _clob = Slash(clob ?? DefaultClob);
    }

    private static Uri Slash(Uri u) => u.AbsoluteUri.EndsWith('/') ? u : new Uri(u.AbsoluteUri + "/");

    public sealed record MarketInfo(string Question, string[] Outcomes, string[] TokenIds);

    /// <summary>The slug of a Polymarket URL (or the text itself when it already is one).</summary>
    public static string SlugOf(string? text)
    {
        var s = (text ?? "").Trim();
        int q = s.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) s = s[..q];
        var parts = s.TrimEnd('/').Split('/');
        return parts.Length > 0 ? parts[^1] : s;
    }

    private static string[] JsonList(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return Array.Empty<string>();
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().Select(x => x.ToString()).ToArray();
        if (v.ValueKind != JsonValueKind.String) return Array.Empty<string>();
        try
        {
            using var d = JsonDocument.Parse(v.GetString() ?? "[]");
            return d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement.EnumerateArray().Select(x => x.ToString()).ToArray() : Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The market for a slug (a market slug, or the first market of an event slug); null when unknown.</summary>
    public async Task<MarketInfo?> MarketAsync(string slug, CancellationToken ct)
    {
        foreach (var path in new[] { "markets?slug=", "events?slug=" })
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(new Uri(_gamma, path + Uri.EscapeDataString(slug)), ct).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var market = item.TryGetProperty("markets", out var ms) && ms.ValueKind == JsonValueKind.Array && ms.GetArrayLength() > 0 ? ms[0] : item;
                var tokens = JsonList(market, "clobTokenIds");
                if (tokens.Length == 0) continue;
                string question = market.TryGetProperty("question", out var qq) ? qq.GetString() ?? slug : slug;
                return new MarketInfo(question, JsonList(market, "outcomes"), tokens);
            }
        }

        return null;
    }

    /// <summary>Outcome price history (0–1) in chunks, de-duplicated by time.</summary>
    public async Task<List<(DateTime T, double P)>> HistoryAsync(string tokenId, DateTime from, DateTime to, int fidelityMinutes, CancellationToken ct)
    {
        var res = new List<(DateTime, double)>();
        var chunk = TimeSpan.FromDays(Math.Clamp(fidelityMinutes * 10, 7, 120));
        for (var a = from; a < to; a += chunk)
        {
            var b = a + chunk < to ? a + chunk : to;
            long s = new DateTimeOffset(DateTime.SpecifyKind(a, DateTimeKind.Utc)).ToUnixTimeSeconds(), e = new DateTimeOffset(DateTime.SpecifyKind(b, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var json = await _http.GetStringAsync(new Uri(_clob, string.Create(CultureInfo.InvariantCulture,
                $"prices-history?market={Uri.EscapeDataString(tokenId)}&startTs={s}&endTs={e}&fidelity={fidelityMinutes}")), ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("history", out var h)) continue;
            foreach (var p in h.EnumerateArray())
                res.Add((DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("t").GetInt64()).UtcDateTime, p.GetProperty("p").GetDouble()));
        }

        return res.OrderBy(p => p.Item1).GroupBy(p => p.Item1).Select(g => g.Last()).ToList();
    }
}

/// <summary>Cached Polymarket odds for the studies (refreshed every two minutes per market, outcome and resolution).</summary>
public sealed class PolymarketOdds : ExternalSeriesHub
{
    public static PolymarketOdds? Shared { get; set; }

    public sealed record OddsState(PolymarketClient.MarketInfo? Info, IReadOnlyList<(DateTime T, double P)> History, string? Message);

    /// <param name="CoveredFrom">Start of the last request: a market created later has no earlier history to load.</param>
    private sealed record Entry(DateTime FetchedAt, PolymarketClient.MarketInfo? Info, List<(DateTime T, double P)> History, string? Error, DateTime CoveredFrom);

    private readonly PolymarketClient _client;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    public PolymarketOdds(PolymarketClient client, Func<DateTime>? utcNow = null) : base(utcNow) => _client = client;

    private static string Key(string slug, int outcome, int fidelity) => string.Create(CultureInfo.InvariantCulture, $"{slug}|{outcome}|{fidelity}");

    /// <summary>The outcome's history (what is cached) and why there is none.</summary>
    public OddsState Odds(string slugOrUrl, int outcome, DateTime from, int fidelityMinutes)
    {
        string slug = PolymarketClient.SlugOf(slugOrUrl);
        if (slug.Length == 0) return new OddsState(null, Array.Empty<(DateTime, double)>(), "Enter a Polymarket market (its URL or slug).");
        string key = Key(slug, outcome, fidelityMinutes);
        _cache.TryGetValue(key, out var hit);
        var age = UtcNow() - (hit?.FetchedAt ?? DateTime.MinValue);
        bool covers = hit != null && hit.CoveredFrom <= from.AddDays(1);
        if (age > TimeSpan.FromMinutes(2) || (!covers && hit?.Error == null && age > TimeSpan.FromSeconds(20)))
            Schedule(key, () => LoadAsync(slug, outcome, from, fidelityMinutes, CancellationToken.None));
        if (hit == null) return new OddsState(null, Array.Empty<(DateTime, double)>(), "Loading Polymarket odds…");
        return new OddsState(hit.Info, hit.History, hit.Error);
    }

    public async Task LoadAsync(string slug, int outcome, DateTime from, int fidelityMinutes, CancellationToken ct)
    {
        string key = Key(slug, outcome, fidelityMinutes);
        _cache.TryGetValue(key, out var hit);
        try
        {
            var info = hit?.Info ?? await _client.MarketAsync(slug, ct).ConfigureAwait(false);
            if (info == null)
            {
                _cache[key] = new Entry(UtcNow(), null, new List<(DateTime, double)>(), "Polymarket has no market with that slug.", from);
                return;
            }

            int k = Math.Clamp(outcome, 0, info.TokenIds.Length - 1);
            var hist = await _client.HistoryAsync(info.TokenIds[k], from, UtcNow(), fidelityMinutes, ct).ConfigureAwait(false);
            _cache[key] = new Entry(UtcNow(), info, hist, hist.Count == 0 ? "That market has no price history in this range." : null, from);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _cache[key] = new Entry(UtcNow(), hit?.Info, hit?.History ?? new List<(DateTime, double)>(), "Polymarket: " + ex.Message, hit?.CoveredFrom ?? DateTime.MaxValue);
        }
        finally { Bump(); }
    }
}

// ============================================================================ comparison candles

/// <summary>
/// Candles of another Binance symbol for comparison studies (1, 5 or 15 minutes), loaded from Binance's public klines in
/// the background and extended with new candles; the chart's own symbol is untouched.
/// </summary>
public sealed class ComparisonKlines : ExternalSeriesHub
{
    public static ComparisonKlines? Shared { get; set; }

    /// <param name="CoveredFrom">Start of the last full load: a symbol listed later has no earlier candles to load.</param>
    private sealed record Entry(DateTime FetchedAt, List<Kline>? Klines, string? Error, DateTime CoveredFrom);

    private readonly Func<bool, IKlineHistory> _source;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    /// <param name="source">Binance futures (true) or spot (false) kline source.</param>
    public ComparisonKlines(Func<bool, IKlineHistory> source, Func<DateTime>? utcNow = null) : base(utcNow) => _source = source;

    public static int MinutesFor(TimeSpan barLength) => barLength.TotalMinutes >= 15 ? 15 : barLength.TotalMinutes >= 5 ? 5 : 1;

    private static string Key(string symbol, bool futures, int minutes) => string.Create(CultureInfo.InvariantCulture, $"{(futures ? "f" : "s")}|{symbol}|{minutes}");

    /// <summary>Cached candles from <paramref name="from"/> on, and why there are none.</summary>
    public (IReadOnlyList<Kline> Klines, string? Message) Get(string symbol, bool futures, DateTime from, int minutes)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (symbol.Length == 0) return (Array.Empty<Kline>(), "Enter the symbol to compare.");
        string key = Key(symbol, futures, minutes);
        _cache.TryGetValue(key, out var hit);
        bool covers = Covers(hit, from, minutes);
        var age = UtcNow() - (hit?.FetchedAt ?? DateTime.MinValue);
        if (age > TimeSpan.FromSeconds(10) && (!covers && hit?.Error == null || age > TimeSpan.FromSeconds(Math.Max(20, minutes * 20)) || hit?.Error != null && age > TimeSpan.FromMinutes(1)))
            Schedule(key, () => LoadAsync(symbol, futures, from, minutes, CancellationToken.None));
        if (hit?.Klines is not { } list) return (Array.Empty<Kline>(), $"Loading {symbol}…");
        return (list, list.Count == 0 ? hit.Error ?? $"Binance has no candles for {symbol}." : null);
    }

    /// <summary>Whether the cache holds every candle from <paramref name="from"/>: loaded from there, or starting there.</summary>
    private static bool Covers(Entry? hit, DateTime from, int minutes) =>
        hit?.Klines is { } k && (hit.CoveredFrom <= from.AddMinutes(2 * minutes)
            || k.Count > 0 && k[0].OpenTimeMs <= new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeMilliseconds() + minutes * 120_000L);

    public async Task LoadAsync(string symbol, bool futures, DateTime from, int minutes, CancellationToken ct)
    {
        string key = Key(symbol, futures, minutes);
        _cache.TryGetValue(key, out var hit);
        try
        {
            bool covers = Covers(hit, from, minutes);
            // Only the tail is fetched once the start is covered; a symbol listed after `from` starts at its listing.
            bool tail = covers && hit!.Klines!.Count > 0;
            var start = tail ? DateTimeOffset.FromUnixTimeMilliseconds(hit!.Klines![^1].OpenTimeMs).UtcDateTime : from;
            var instrument = new Instrument { Symbol = symbol, FeedId = futures ? "binance-futures" : "binance-spot", TickSize = 0.0001m };
            var got = new List<Kline>();
            await _source(futures).LoadKlinesAsync(instrument, start, UtcNow(), minutes, 0, batch => { got.AddRange(batch); return true; }, null, ct).ConfigureAwait(false);
            long firstNew = got.Count > 0 ? got[0].OpenTimeMs : long.MaxValue;
            var list = tail ? hit!.Klines!.Where(x => x.OpenTimeMs < firstNew).Concat(got).ToList() : got;
            var coveredFrom = tail && hit!.CoveredFrom < from ? hit!.CoveredFrom : from;
            _cache[key] = new Entry(UtcNow(), list, null, coveredFrom);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _cache[key] = new Entry(UtcNow(), hit?.Klines, $"{symbol}: {ex.Message}", hit?.CoveredFrom ?? DateTime.MaxValue);
        }
        finally { Bump(); }
    }
}
