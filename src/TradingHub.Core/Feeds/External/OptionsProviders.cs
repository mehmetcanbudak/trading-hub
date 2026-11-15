// Behavior adapted from the KVP capability reference (commit 7fbf237); see THIRD_PARTY_NOTICES.md.
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TradingHub.Feeds.External;

/// <summary>
/// Deribit's public options data for BTC and ETH (no account, no key). Chain: <c>get_book_summary_by_currency</c>
/// (option prices in coin converted to USD with each expiry's forward; spot reference is Deribit's index, the
/// estimated delivery price); trades: <c>get_last_trades_by_currency_and_time</c> (aggressor side from
/// <c>direction</c>); daily IV history: <c>get_volatility_index_data</c> (DVOL, a 30-day implied volatility index).
/// Deribit contracts are one coin, so the multiplier is 1.
/// </summary>
public sealed class DeribitOptions : IOptionsProvider
{
    /// <summary>Production REST root.</summary>
    public static readonly Uri DefaultRoot = new("https://www.deribit.com/api/v2/public/");

    /// <summary>Most pages of 1,000 trades one request window reads; later windows continue from where it stopped.</summary>
    public const int MaxTradePages = 50;

    private readonly HttpClient _http;
    private readonly Uri _root;
    private readonly Func<DateTime> _utcNow;

    public DeribitOptions(HttpClient http, Uri? root = null, Func<DateTime>? utcNow = null)
    {
        _http = http;
        var r = root ?? DefaultRoot;
        _root = r.AbsoluteUri.EndsWith('/') ? r : new Uri(r.AbsoluteUri + "/");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Name => "Deribit";

    public bool Supports(string underlying) => underlying is "BTC" or "ETH";

    private async Task<JsonElement> Get(string method, string query, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(new Uri(_root, method + "?" + query), ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new InvalidOperationException("Deribit: " + (err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString()));
        resp.EnsureSuccessStatusCode();
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static double D(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>BTC-27DEC26-100000-C → (expiry at 08:00 UTC, strike, right); null for other instrument names.</summary>
    public static (DateTime Expiry, double Strike, OptionRight Right)? ParseName(string name)
    {
        var p = name.Split('-');
        if (p.Length != 4 || p[3] is not ("C" or "P")) return null;
        if (!DateTime.TryParseExact(p[1], new[] { "dMMMyy", "ddMMMyy" }, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var exp)) return null;
        if (!double.TryParse(p[2].Replace('d', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var k)) return null;
        return (DateTime.SpecifyKind(exp.Date.AddHours(8), DateTimeKind.Utc), k, p[3] == "C" ? OptionRight.Call : OptionRight.Put);
    }

    /// <summary>Parses a <c>get_book_summary_by_currency</c> result into a chain (null when it lists no options).</summary>
    public static OptionsChain? ParseChain(string underlying, JsonElement result, DateTime fallbackTime)
    {
        var list = new List<(OptionContract Contract, double Forward)>();
        double index = 0;
        long stamp = 0;
        foreach (var o in result.EnumerateArray())
        {
            string name = o.TryGetProperty("instrument_name", out var n) ? n.GetString() ?? "" : "";
            if (ParseName(name) is not { } info) continue;
            double forward = D(o, "underlying_price");
            if (forward <= 0) continue;
            if (D(o, "estimated_delivery_price") is > 0 and var idx) index = idx;
            if (o.TryGetProperty("creation_timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number) stamp = Math.Max(stamp, ts.GetInt64());
            // Coin prices for now; converted to USD with the index once it is known (as Deribit values premiums).
            list.Add((new OptionContract(name, info.Expiry, info.Strike, info.Right,
                D(o, "bid_price"), D(o, "ask_price"), D(o, "mark_price"), D(o, "mark_iv") / 100,
                0, 0, 0, 0, D(o, "open_interest"), D(o, "volume"), 1), forward));
        }

        if (list.Count == 0) return null;
        var time = stamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(stamp).UtcDateTime : fallbackTime;
        // Without an index print, the nearest expiry's forward is the closest available spot reference.
        if (index <= 0) index = list.OrderBy(x => x.Contract.Expiry).First().Forward;
        // Greeks on each contract's own forward (what Deribit prices against); exposures then use the index as spot.
        var priced = list.Select(x =>
        {
            var (delta, gamma) = OptionsMath.Greeks(x.Forward, x.Contract.Strike, x.Contract.DaysToExpiry(time) / 365, x.Contract.Iv, x.Contract.Right);
            return x.Contract with { Bid = x.Contract.Bid * index, Ask = x.Contract.Ask * index, Mark = x.Contract.Mark * index, Delta = delta, Gamma = gamma };
        }).ToList();
        return new OptionsChain(underlying, time, index, priced, "Deribit", "Deribit index (estimated delivery price)");
    }

    public async Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct)
    {
        var r = await Get("get_book_summary_by_currency", $"currency={underlying}&kind=option", ct).ConfigureAwait(false);
        return ParseChain(underlying, r, _utcNow());
    }

    /// <summary>Parses one <c>get_last_trades_by_currency_and_time</c> page.</summary>
    public static List<OptionTrade> ParseTrades(JsonElement result, out bool hasMore, out long lastTimestamp)
    {
        var res = new List<OptionTrade>();
        lastTimestamp = 0;
        foreach (var t in result.GetProperty("trades").EnumerateArray())
        {
            string name = t.TryGetProperty("instrument_name", out var n) ? n.GetString() ?? "" : "";
            if (ParseName(name) is not { } info) continue;
            long ts = t.GetProperty("timestamp").GetInt64();
            lastTimestamp = Math.Max(lastTimestamp, ts);
            double index = D(t, "index_price");
            string dir = t.TryGetProperty("direction", out var dd) ? dd.GetString() ?? "" : "";
            res.Add(new OptionTrade(DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime, name, info.Expiry, info.Strike, info.Right,
                D(t, "price") * index, D(t, "amount"), dir == "buy" ? 1 : dir == "sell" ? -1 : 0, D(t, "iv") / 100, index, 1, "Deribit"));
        }

        hasMore = result.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
        return res;
    }

    public async Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        var res = new List<OptionTrade>();
        long start = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long end = new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        for (int page = 0; page < MaxTradePages && start < end; page++)
        {
            var r = await Get("get_last_trades_by_currency_and_time",
                $"currency={underlying}&kind=option&start_timestamp={start}&end_timestamp={end}&count=1000&sorting=asc", ct).ConfigureAwait(false);
            res.AddRange(ParseTrades(r, out bool more, out long last));
            if (!more || last < start) break;
            // Trades sharing the last millisecond may straddle pages; the hub de-duplicates them.
            start = last;
        }

        return res;
    }

    public async Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        long s = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long e = new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var r = await Get("get_volatility_index_data", $"currency={underlying}&start_timestamp={s}&end_timestamp={e}&resolution=1D", ct).ConfigureAwait(false);
        return ParseVolatilityIndex(r);
    }

    /// <summary>DVOL daily candles [time, open, high, low, close] → daily rows with only the 30-day IV known.</summary>
    public static List<OptionDaily> ParseVolatilityIndex(JsonElement result)
    {
        var res = new List<OptionDaily>();
        foreach (var row in result.GetProperty("data").EnumerateArray())
        {
            var day = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()).UtcDateTime.Date;
            res.Add(new OptionDaily(day, double.NaN, row[4].GetDouble() / 100, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN));
        }

        return res;
    }
}

/// <summary>Shared JSON reading and key placement for the user-configured HTTP APIs.</summary>
internal static class CustomApi
{
    public static void Authorize(HttpRequestMessage req, string? header, string? key)
    {
        if (key == null) return;
        if (header == null || header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        else req.Headers.TryAddWithoutValidation(header, key);
    }

    public static double D(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN
            : double.NaN;

    public static double Or(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    public static DateTime? T(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;

    public static string S(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static async Task<JsonElement> GetAsync(HttpClient http, string url, string? header, string? key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(req, header, key);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode} from {new Uri(url).Host}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.Clone();
    }

    /// <summary>An array, or the <c>data</c> array of an object.</summary>
    public static JsonElement Rows(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;
}

/// <summary>
/// A user-configured options API (paid or self-hosted) returning documented JSON under its base URL, with an optional key
/// sent as a Bearer token or in a named header:
/// <c>GET {base}/chain?underlying=BTC</c> → <c>{"time","spot","contracts":[{"symbol","expiry","strike","right":"C|P","bid","ask","mark","iv","delta","gamma","vega","theta","oi","volume","contractSize"}]}</c>;
/// <c>GET {base}/trades?underlying=BTC&amp;from=ISO&amp;to=ISO</c> → <c>[{"time","symbol","expiry","strike","right","price","size","side":"buy|sell","iv","spot","contractSize","venue"}]</c>;
/// <c>GET {base}/daily?underlying=BTC&amp;from=ISO&amp;to=ISO</c> → <c>[{"date","spot","atmIv","rr25","pcVolume","pcOi","callOi","putOi","callVolume","putVolume","gex","callWall","putWall","gammaFlip","expectedMove"}]</c>.
/// IV is a fraction; prices are USD per unit of the underlying; <c>contractSize</c> (default 1) is the multiplier.
/// Missing greeks are computed; other missing numbers are unknown, never zero.
/// </summary>
public sealed class JsonOptionsApi : IOptionsProvider
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string? _header, _key;
    private readonly HashSet<string>? _only;

    public JsonOptionsApi(HttpClient http, string baseUrl, string? header, string? key, IEnumerable<string>? underlyings = null)
    {
        _http = http;
        _base = baseUrl.Trim().TrimEnd('/');
        _header = string.IsNullOrWhiteSpace(header) ? null : header.Trim();
        _key = string.IsNullOrWhiteSpace(key) ? null : key;
        var only = underlyings?.Select(u => u.Trim().ToUpperInvariant()).Where(u => u.Length > 0).ToHashSet();
        _only = only is { Count: > 0 } ? only : null;
    }

    public string Name => "Custom options API";

    public bool Supports(string underlying) => _only == null || _only.Contains(underlying);

    private static OptionRight R(JsonElement o) => CustomApi.S(o, "right").StartsWith("P", StringComparison.OrdinalIgnoreCase) ? OptionRight.Put : OptionRight.Call;

    private static string Iso(DateTime t) => Uri.EscapeDataString(DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    public async Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct)
    {
        var r = await CustomApi.GetAsync(_http, $"{_base}/chain?underlying={Uri.EscapeDataString(underlying)}", _header, _key, ct).ConfigureAwait(false);
        if (CustomApi.T(r, "time") is not { } time) throw new FormatException("The chain has no \"time\".");
        double spot = CustomApi.D(r, "spot");
        if (!(spot > 0)) throw new FormatException("The chain has no positive \"spot\".");
        var list = new List<OptionContract>();
        foreach (var o in r.GetProperty("contracts").EnumerateArray())
        {
            if (CustomApi.T(o, "expiry") is not { } expiry || !(CustomApi.D(o, "strike") is > 0 and var strike)) continue;
            double size = CustomApi.D(o, "contractSize");
            list.Add(new OptionContract(CustomApi.S(o, "symbol"), expiry, strike, R(o),
                CustomApi.Or(CustomApi.D(o, "bid"), 0), CustomApi.Or(CustomApi.D(o, "ask"), 0), CustomApi.Or(CustomApi.D(o, "mark"), 0), CustomApi.Or(CustomApi.D(o, "iv"), 0),
                CustomApi.Or(CustomApi.D(o, "delta"), 0), CustomApi.Or(CustomApi.D(o, "gamma"), 0), CustomApi.Or(CustomApi.D(o, "vega"), 0), CustomApi.Or(CustomApi.D(o, "theta"), 0),
                CustomApi.Or(CustomApi.D(o, "oi"), 0), CustomApi.Or(CustomApi.D(o, "volume"), 0), size > 0 ? size : 1));
        }

        return new OptionsChain(underlying, time, spot, list, Name, "provider spot");
    }

    public async Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        var r = CustomApi.Rows(await CustomApi.GetAsync(_http, $"{_base}/trades?underlying={Uri.EscapeDataString(underlying)}&from={Iso(from)}&to={Iso(to)}", _header, _key, ct).ConfigureAwait(false));
        var res = new List<OptionTrade>();
        foreach (var o in r.EnumerateArray())
        {
            if (CustomApi.T(o, "time") is not { } time || CustomApi.T(o, "expiry") is not { } expiry) continue;
            string side = CustomApi.S(o, "side");
            double size = CustomApi.D(o, "contractSize");
            res.Add(new OptionTrade(time, CustomApi.S(o, "symbol"), expiry, CustomApi.Or(CustomApi.D(o, "strike"), 0), R(o), CustomApi.Or(CustomApi.D(o, "price"), 0),
                CustomApi.Or(CustomApi.D(o, "size"), 0), side == "buy" ? 1 : side == "sell" ? -1 : 0, CustomApi.Or(CustomApi.D(o, "iv"), 0), CustomApi.Or(CustomApi.D(o, "spot"), 0),
                size > 0 ? size : 1, CustomApi.S(o, "venue")));
        }

        return res;
    }

    public async Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        var r = CustomApi.Rows(await CustomApi.GetAsync(_http, $"{_base}/daily?underlying={Uri.EscapeDataString(underlying)}&from={Iso(from)}&to={Iso(to)}", _header, _key, ct).ConfigureAwait(false));
        var res = new List<OptionDaily>();
        foreach (var o in r.EnumerateArray())
        {
            if (CustomApi.T(o, "date") is not { } date) continue;
            res.Add(new OptionDaily(date.Date, CustomApi.D(o, "spot"), CustomApi.D(o, "atmIv"), CustomApi.D(o, "rr25"), CustomApi.D(o, "pcVolume"), CustomApi.D(o, "pcOi"),
                CustomApi.D(o, "callOi"), CustomApi.D(o, "putOi"), CustomApi.D(o, "callVolume"), CustomApi.D(o, "putVolume"), CustomApi.D(o, "gex"), CustomApi.D(o, "callWall"),
                CustomApi.D(o, "putWall"), CustomApi.D(o, "gammaFlip"), CustomApi.D(o, "expectedMove")));
        }

        return res;
    }
}
