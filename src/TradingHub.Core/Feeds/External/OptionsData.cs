// Behavior adapted from the KVP capability reference (commit 7fbf237); see THIRD_PARTY_NOTICES.md.
using System.Collections.Concurrent;
using System.Text.Json;

namespace TradingHub.Feeds.External;

/// <summary>
/// The options data the options studies share. A provider exists only while the user has an enabled options-data
/// connection; without one, every study shows the waiting state and makes no request. Reads never block: they return
/// what is cached and schedule what is missing; <see cref="Changed"/> announces arrivals. The awaitable
/// <c>…Async</c> methods load the same caches directly.
/// </summary>
public sealed class OptionsData : ExternalSeriesHub
{
    /// <summary>The application's instance; null outside the app (studies then show the waiting state).</summary>
    public static OptionsData? Shared { get; set; }

    /// <summary>The message shown when no options-data connection is configured.</summary>
    public const string NotConfigured = "Waiting for an options data connection: add one in Connections → Options data.";

    private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SnapshotEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SnapshotKeep = TimeSpan.FromDays(7);

    private sealed record ChainEntry(DateTime FetchedAt, OptionsChain? Chain, string? Error);

    /// <summary>Contiguous loaded trades: every trade in [From, To) that the provider published is in Items.</summary>
    private sealed record TradeStore(DateTime From, DateTime To, DateTime FetchedAt, List<OptionTrade> Items);

    private sealed record DailyEntry(DateTime FetchedAt, List<OptionDaily> Days);

    private readonly string? _root;
    private IOptionsProvider? _provider;
    private readonly ConcurrentDictionary<string, ChainEntry> _chains = new();
    private readonly ConcurrentDictionary<string, TradeStore> _trades = new();
    private readonly ConcurrentDictionary<string, DailyEntry> _dailies = new();
    private readonly ConcurrentDictionary<string, List<StrikeSnapshot>> _snapshots = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSnapshot = new();
    private readonly ConcurrentDictionary<string, DateTime> _tradeRetry = new();

    /// <param name="root">Folder for recorded daily rows and strike snapshots; null keeps them in memory.</param>
    public OptionsData(string? root = null, Func<DateTime>? utcNow = null) : base(utcNow) => _root = root;

    /// <summary>The last provider error, cleared by the next success.</summary>
    public string? LastError { get; private set; }

    /// <summary>Oldest trades requested, in days before now.</summary>
    public int MaxTradeDays { get; set; } = 5;

    public IOptionsProvider? Provider
    {
        get => _provider;
        set
        {
            _provider = value;
            _chains.Clear();
            _trades.Clear();
            _dailies.Clear();
            LastError = null;
            Bump();
        }
    }

    private DateTime Now => UtcNow();

    /// <summary>
    /// The options underlying of a chart symbol: its base asset when known, otherwise the symbol without a USD-family
    /// quote or perpetual suffix (BTCUSDT, BTC-PERP, BTCUSD.P → BTC; SPY → SPY).
    /// </summary>
    public static string UnderlyingOf(string? symbol, string? baseAsset = null)
    {
        if (!string.IsNullOrWhiteSpace(baseAsset)) return baseAsset.Trim().ToUpperInvariant();
        var s = (symbol ?? "").Trim().ToUpperInvariant().Replace("-SWAP", "").Replace(".P", "").Replace("PERP", "").Replace("-", "").Replace("/", "").Replace("_", "");
        foreach (var q in new[] { "USDT", "USDC", "BUSD", "USD" })
            if (s.Length > q.Length && s.EndsWith(q, StringComparison.Ordinal))
                return s[..^q.Length];
        return s;
    }

    /// <summary>Why there is no data for <paramref name="underlying"/> (null when a provider covers it).</summary>
    public string? Unavailable(string underlying)
    {
        var p = _provider;
        if (p == null) return NotConfigured;
        if (underlying.Length == 0 || !p.Supports(underlying)) return $"{p.Name} has no options on {(underlying.Length == 0 ? "this symbol" : underlying)}.";
        return null;
    }

    // ------------------------------------------------------------------ chain

    /// <summary>The latest snapshot; schedules a refresh when older than <paramref name="maxAge"/> (default 60 s).</summary>
    public OptionsChain? Chain(string underlying, TimeSpan? maxAge = null)
    {
        if (Unavailable(underlying) != null) return null;
        _chains.TryGetValue(underlying, out var hit);
        var age = Now - (hit?.FetchedAt ?? DateTime.MinValue);
        if (age > (hit?.Error != null ? RetryAfterError : maxAge ?? TimeSpan.FromSeconds(60)))
            Schedule("chain:" + underlying, () => RefreshChainAsync(underlying, CancellationToken.None));
        return hit?.Chain;
    }

    /// <summary>Why <see cref="Chain"/> returned null after a failed request.</summary>
    public string? ChainError(string underlying) => _chains.TryGetValue(underlying, out var hit) ? hit.Error : null;

    public async Task<OptionsChain?> RefreshChainAsync(string underlying, CancellationToken ct)
    {
        var p = _provider;
        if (p == null || !p.Supports(underlying)) return null;
        _chains.TryGetValue(underlying, out var hit);
        try
        {
            var c = await p.ChainAsync(underlying, ct).ConfigureAwait(false);
            if (!ReferenceEquals(p, _provider)) return null;
            if (c == null || c.Contracts.Count == 0)
            {
                _chains[underlying] = new ChainEntry(Now, hit?.Chain, $"{p.Name} returned no options on {underlying}.");
                return hit?.Chain;
            }

            _chains[underlying] = new ChainEntry(Now, c, null);
            LastError = null;
            Record(underlying, c);
            return c;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            _chains[underlying] = new ChainEntry(Now, hit?.Chain, ex.Message);
            return hit?.Chain;
        }
        finally { Bump(); }
    }

    // ------------------------------------------------------------------ trades

    private DateTime Floor => Now.AddDays(-MaxTradeDays);

    /// <summary>
    /// Loaded trades with <c>from ≤ time &lt; to</c>. Missing coverage is requested in the background one day at a time
    /// (newest first), and new trades about once a minute.
    /// </summary>
    public IReadOnlyList<OptionTrade> TradesBetween(string underlying, DateTime from, DateTime to)
    {
        if (Unavailable(underlying) != null) return Array.Empty<OptionTrade>();
        var now = Now;
        var want = from < Floor ? Floor : from;
        _trades.TryGetValue(underlying, out var store);
        bool missingOld = store == null || store.From > want.AddMinutes(1);
        bool stale = store != null && to > store.To && now - store.FetchedAt > TimeSpan.FromMinutes(1);
        if ((missingOld || stale) && now >= (_tradeRetry.TryGetValue(underlying, out var retry) ? retry : DateTime.MinValue))
            Schedule("trades:" + underlying, () => LoadTradesAsync(underlying, want, now, CancellationToken.None));
        if (store == null) return Array.Empty<OptionTrade>();
        int lo = LowerBound(store.Items, from), hi = LowerBound(store.Items, to);
        return hi > lo ? store.Items.GetRange(lo, hi - lo) : Array.Empty<OptionTrade>();
    }

    /// <summary>Whether every trade in [from, to) has been loaded (so an empty interval is a real zero, not a gap).</summary>
    public bool TradesCovered(string underlying, DateTime from, DateTime to) =>
        _trades.TryGetValue(underlying, out var s) && s.From <= from && s.To >= to;

    /// <summary>The loaded trade interval, or null.</summary>
    public (DateTime From, DateTime To)? TradeCoverage(string underlying) =>
        _trades.TryGetValue(underlying, out var s) ? (s.From, s.To) : null;

    /// <summary>Loads trades until [from, to) is covered, one day per request window.</summary>
    public async Task LoadTradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        var p = _provider;
        if (p == null || !p.Supports(underlying)) return;
        if (from < Floor) from = Floor;
        for (int guard = 0; guard < MaxTradeDays + 3; guard++)
        {
            _trades.TryGetValue(underlying, out var s);
            try
            {
                if (s == null)
                {
                    var start = to.AddDays(-1) > from ? to.AddDays(-1) : from;
                    var got = await p.TradesAsync(underlying, start, to, ct).ConfigureAwait(false);
                    if (!ReferenceEquals(p, _provider)) return;
                    _trades[underlying] = new TradeStore(start, to, Now, Merge(new List<OptionTrade>(), got, start, to));
                }
                else if (s.From > from.AddMinutes(1))
                {
                    var start = s.From.AddDays(-1) > from ? s.From.AddDays(-1) : from;
                    var got = await p.TradesAsync(underlying, start, s.From, ct).ConfigureAwait(false);
                    if (!ReferenceEquals(p, _provider)) return;
                    _trades[underlying] = s with { From = start, Items = Merge(s.Items, got, start, s.From) };
                }
                else if (s.To < to.AddMinutes(-1) || (s.To < to && Now - s.FetchedAt > TimeSpan.FromMinutes(1)))
                {
                    var start = s.To.AddMinutes(-5);
                    var got = await p.TradesAsync(underlying, start, to, ct).ConfigureAwait(false);
                    if (!ReferenceEquals(p, _provider)) return;
                    var items = Merge(s.Items, got, start, to);
                    var keepFrom = Now.AddDays(-MaxTradeDays - 1);
                    if (items.Count > 0 && items[0].Time < keepFrom) items.RemoveRange(0, LowerBound(items, keepFrom));
                    _trades[underlying] = new TradeStore(s.From < keepFrom ? keepFrom : s.From, to, Now, items);
                }
                else break;

                LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LastError = ex.Message;
                _tradeRetry[underlying] = Now + RetryAfterError;
                Bump();
                return;
            }

            _tradeRetry.TryRemove(underlying, out _);
            Bump();
        }
    }

    /// <summary>Existing trades plus newly fetched ones in [from, to), de-duplicated and in time order.</summary>
    private static List<OptionTrade> Merge(List<OptionTrade> existing, IEnumerable<OptionTrade> got, DateTime from, DateTime to)
    {
        var seen = new HashSet<(DateTime, string, double, double, int)>(existing.Where(t => t.Time >= from && t.Time < to).Select(t => (t.Time, t.Symbol, t.Price, t.Size, t.Aggressor)));
        var fresh = got.Where(t => t.Time >= from && t.Time < to && seen.Add((t.Time, t.Symbol, t.Price, t.Size, t.Aggressor)));
        var list = existing.Concat(fresh).ToList();
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    private static int LowerBound(List<OptionTrade> list, DateTime t)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Time < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    // ------------------------------------------------------------------ daily history

    /// <summary>
    /// Daily history (provider rows completed field by field with recorded snapshots), refreshed hourly. Row D describes
    /// the UTC day D and is known only after that day ends.
    /// </summary>
    public IReadOnlyList<OptionDaily> Daily(string underlying)
    {
        if (Unavailable(underlying) != null) return Array.Empty<OptionDaily>();
        _dailies.TryGetValue(underlying, out var hit);
        if (Now - (hit?.FetchedAt ?? DateTime.MinValue) > TimeSpan.FromHours(1))
            Schedule("daily:" + underlying, () => RefreshDailyAsync(underlying, CancellationToken.None));
        return hit?.Days ?? (IReadOnlyList<OptionDaily>)Array.Empty<OptionDaily>();
    }

    public async Task RefreshDailyAsync(string underlying, CancellationToken ct, int days = 400)
    {
        var p = _provider;
        if (p == null || !p.Supports(underlying)) return;
        try
        {
            var to = Now;
            var got = await p.DailyAsync(underlying, to.AddDays(-days), to, ct).ConfigureAwait(false);
            if (!ReferenceEquals(p, _provider)) return;
            _dailies[underlying] = new DailyEntry(Now, MergeDaily(got, LoadRecorded(underlying)));
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            _dailies.TryGetValue(underlying, out var hit);
            // Try again in about 15 minutes, keeping recorded rows meanwhile.
            _dailies[underlying] = new DailyEntry(Now.AddMinutes(-45), hit?.Days ?? MergeDaily(Array.Empty<OptionDaily>(), LoadRecorded(underlying)));
        }
        finally { Bump(); }
    }

    /// <summary>Per day, each provider field that is unknown (NaN or 0) is taken from the recorded row.</summary>
    public static List<OptionDaily> MergeDaily(IEnumerable<OptionDaily> provider, IEnumerable<OptionDaily> recorded)
    {
        static double P(double a, double b) => double.IsFinite(a) && a != 0 ? a : b;
        var byDay = new Dictionary<DateTime, OptionDaily>();
        foreach (var d in recorded) byDay[d.Date.Date] = d with { Date = d.Date.Date };
        foreach (var d in provider)
        {
            var k = d.Date.Date;
            byDay[k] = byDay.TryGetValue(k, out var r)
                ? new OptionDaily(k, P(d.Spot, r.Spot), P(d.AtmIv, r.AtmIv), P(d.Rr25, r.Rr25), P(d.PutCallVolume, r.PutCallVolume), P(d.PutCallOi, r.PutCallOi),
                    P(d.CallOi, r.CallOi), P(d.PutOi, r.PutOi), P(d.CallVolume, r.CallVolume), P(d.PutVolume, r.PutVolume), P(d.Gex, r.Gex), P(d.CallWall, r.CallWall),
                    P(d.PutWall, r.PutWall), P(d.GammaFlip, r.GammaFlip), P(d.ExpectedMove, r.ExpectedMove))
                : d with { Date = k };
        }

        return byDay.Values.OrderBy(d => d.Date).ToList();
    }

    // ------------------------------------------------------------------ recorded snapshots

    /// <summary>
    /// Per-strike snapshots recorded at most every 30 minutes while a study requests the chain, kept for a week. They let
    /// studies show open-interest changes and historical gamma without inventing the past.
    /// </summary>
    public IReadOnlyList<StrikeSnapshot> Snapshots(string underlying)
    {
        var list = _snapshots.GetOrAdd(underlying, LoadSnapshots);
        lock (list) return list.ToList();
    }

    private string? PathOf(string underlying, string kind) =>
        _root == null || underlying.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : Path.Combine(_root, underlying + kind);

    private void Record(string underlying, OptionsChain c)
    {
        if (_lastSnapshot.TryGetValue(underlying, out var last) && c.Time - last < SnapshotEvery - TimeSpan.FromMinutes(1)) return;
        _lastSnapshot[underlying] = c.Time;
        var snap = OptionsMath.SnapshotOf(c);
        var list = _snapshots.GetOrAdd(underlying, LoadSnapshots);
        List<StrikeSnapshot> copy;
        lock (list)
        {
            list.RemoveAll(x => x.Time >= snap.Time || x.Time < snap.Time - SnapshotKeep);
            list.Add(snap);
            copy = list.ToList();
        }

        Write(PathOf(underlying, ".strikes.json"), copy);
        var day = OptionsMath.DailyOf(c);
        var days = LoadRecorded(underlying).Where(d => d.Date.Date != day.Date).Append(day).OrderBy(d => d.Date).TakeLast(800).ToList();
        Write(PathOf(underlying, ".daily.json"), days);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static void Write<T>(string? path, T value)
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Recording is an optional cache; studies keep working from memory.
        }
    }

    private static T? Read<T>(string? path) where T : class
    {
        try { return path != null && File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null; }
        catch (Exception) { return null; }
    }

    private List<StrikeSnapshot> LoadSnapshots(string underlying) => Read<List<StrikeSnapshot>>(PathOf(underlying, ".strikes.json")) ?? new List<StrikeSnapshot>();

    private List<OptionDaily> LoadRecorded(string underlying) => Read<List<OptionDaily>>(PathOf(underlying, ".daily.json")) ?? new List<OptionDaily>();
}
