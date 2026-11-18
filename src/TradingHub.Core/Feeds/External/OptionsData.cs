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

    /// <summary>
    /// Loaded trades: every trade in [From, To) that the provider published is in Items, except inside Gaps, spans where a
    /// request stopped at the provider's page limit (unknown until a later request fills them).
    /// </summary>
    private sealed record TradeStore(DateTime From, DateTime To, DateTime FetchedAt, List<OptionTrade> Items, IReadOnlyList<(DateTime From, DateTime To)> Gaps);

    /// <summary>Merged daily rows plus the provider's own rows (<see cref="Published"/>, null until a request succeeded).</summary>
    private sealed record DailyEntry(DateTime FetchedAt, List<OptionDaily> Days, IReadOnlyList<OptionDaily>? Published = null, string? Error = null);

    private readonly string? _root;
    private IOptionsProvider? _provider;
    private readonly ConcurrentDictionary<string, ChainEntry> _chains = new();
    private readonly ConcurrentDictionary<string, TradeStore> _trades = new();
    private readonly ConcurrentDictionary<string, DailyEntry> _dailies = new();
    private readonly ConcurrentDictionary<string, List<StrikeSnapshot>> _snapshots = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSnapshot = new();
    private readonly ConcurrentDictionary<string, DateTime> _tradeRetry = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tradeLoads = new();

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
            _tradeRetry.Clear();
            _lastSnapshot.Clear();
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
            // A retired provider's failure (its client is disposed on a swap) must not reach the new provider's cache.
            if (!ReferenceEquals(p, _provider)) return null;
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
        bool gaps = store != null && store.Gaps.Count > 0 && now - store.FetchedAt > TimeSpan.FromSeconds(10);
        if ((missingOld || stale || gaps) && now >= (_tradeRetry.TryGetValue(underlying, out var retry) ? retry : DateTime.MinValue))
            Schedule("trades:" + underlying, () => LoadTradesAsync(underlying, want, now, CancellationToken.None));
        if (store == null) return Array.Empty<OptionTrade>();
        int lo = LowerBound(store.Items, from), hi = LowerBound(store.Items, to);
        return hi > lo ? store.Items.GetRange(lo, hi - lo) : Array.Empty<OptionTrade>();
    }

    /// <summary>Whether every trade in [from, to) has been loaded (so an empty interval is a real zero, not a gap).</summary>
    public bool TradesCovered(string underlying, DateTime from, DateTime to) =>
        _trades.TryGetValue(underlying, out var s) && s.From <= from && s.To >= to && !s.Gaps.Any(g => g.From < to && g.To > from);

    /// <summary>The loaded trade interval, or null. Spans in <see cref="TradeGaps"/> inside it are still unknown.</summary>
    public (DateTime From, DateTime To)? TradeCoverage(string underlying) =>
        _trades.TryGetValue(underlying, out var s) ? (s.From, s.To) : null;

    /// <summary>Spans inside the loaded interval where a request stopped at the provider's page limit (unknown, not empty).</summary>
    public IReadOnlyList<(DateTime From, DateTime To)> TradeGaps(string underlying) =>
        _trades.TryGetValue(underlying, out var s) ? s.Gaps : Array.Empty<(DateTime, DateTime)>();

    /// <summary>A note naming the page-limited span within [from, to) that is not loaded yet, or null.</summary>
    public string? TradeCoverageNote(string underlying, DateTime from, DateTime to)
    {
        var gaps = TradeGaps(underlying).Where(g => g.From < to && g.To > from).ToList();
        if (gaps.Count == 0) return null;
        DateTime a = gaps.Min(g => g.From) < from ? from : gaps.Min(g => g.From), b = gaps.Max(g => g.To) > to ? to : gaps.Max(g => g.To);
        string cap = _provider is IOptionTradeWindows w ? w.TradesPerWindow.ToString(System.Globalization.CultureInfo.InvariantCulture) : "a page limit";
        string span = a.Date == b.Date
            ? $"{a.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}–{b.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{a.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}–{b.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";
        return $"Partial: trades capped at {cap} per request; {span} UTC not loaded yet";
    }

    /// <summary>Trades of [from, to) and how far they are complete (the provider's page limit can stop a window early).</summary>
    private static async Task<OptionTradeWindow> FetchAsync(IOptionsProvider p, string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        if (p is IOptionTradeWindows windows)
        {
            var w = await windows.TradesWindowAsync(underlying, from, to, ct).ConfigureAwait(false);
            return w.CoveredTo > to ? w with { CoveredTo = to } : w;
        }

        return new OptionTradeWindow(await p.TradesAsync(underlying, from, to, ct).ConfigureAwait(false), to);
    }

    private static List<(DateTime From, DateTime To)> AddGap(IEnumerable<(DateTime From, DateTime To)> gaps, DateTime from, DateTime to) =>
        from < to ? gaps.Append((From: from, To: to)).OrderBy(g => g.From).ToList() : gaps.ToList();

    /// <summary>Loads trades until [from, to) is covered, one day per request window. Loads of one underlying run one
    /// at a time: each reads the store, awaits the provider and writes the store back, so overlapping loads would let the
    /// slower writer replace the faster one's progress.</summary>
    public async Task LoadTradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        var gate = _tradeLoads.GetOrAdd(underlying, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await LoadTradesCoreAsync(underlying, from, to, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task LoadTradesCoreAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
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
                    var got = await FetchAsync(p, underlying, start, to, ct).ConfigureAwait(false);
                    if (!ReferenceEquals(p, _provider)) return;
                    _trades[underlying] = new TradeStore(start, to, Now, Merge(new List<OptionTrade>(), got.Trades, start, got.CoveredTo),
                        AddGap(Array.Empty<(DateTime, DateTime)>(), got.CoveredTo, to));
                }
                else if (s.Gaps.Count > 0)
                {
                    // Continue paging from where the newest capped window stopped.
                    var gap = s.Gaps[^1];
                    var got = await FetchAsync(p, underlying, gap.From, gap.To, ct).ConfigureAwait(false);
                    if (!ReferenceEquals(p, _provider)) return;
                    var gaps = AddGap(s.Gaps.Take(s.Gaps.Count - 1), got.CoveredTo, gap.To);
                    _trades[underlying] = s with { FetchedAt = Now, Items = Merge(s.Items, got.Trades, gap.From, got.CoveredTo), Gaps = gaps };
                }
                else if (s.From > from.AddMinutes(1))
                {
                    var start = s.From.AddDays(-1) > from ? s.From.AddDays(-1) : from;
                    var got = await FetchAsync(p, underlying, start, s.From, ct).ConfigureAwait(false);
                    if (!ReferenceEquals(p, _provider)) return;
                    _trades[underlying] = s with { From = start, Items = Merge(s.Items, got.Trades, start, got.CoveredTo), Gaps = AddGap(s.Gaps, got.CoveredTo, s.From) };
                }
                else if (s.To < to.AddMinutes(-1) || (s.To < to && Now - s.FetchedAt > TimeSpan.FromMinutes(1)))
                {
                    var start = s.To.AddMinutes(-5);
                    var got = await FetchAsync(p, underlying, start, to, ct).ConfigureAwait(false);
                    if (!ReferenceEquals(p, _provider)) return;
                    var items = Merge(s.Items, got.Trades, start, got.CoveredTo);
                    var keepFrom = Now.AddDays(-MaxTradeDays - 1);
                    if (items.Count > 0 && items[0].Time < keepFrom) items.RemoveRange(0, LowerBound(items, keepFrom));
                    var gaps = AddGap(s.Gaps.Where(g => g.To > keepFrom), got.CoveredTo > s.To ? got.CoveredTo : s.To, to);
                    _trades[underlying] = new TradeStore(s.From < keepFrom ? keepFrom : s.From, to, Now, items, gaps);
                }
                else break;

                LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (!ReferenceEquals(p, _provider)) return;
                LastError = ex.Message;
                _tradeRetry[underlying] = Now + RetryAfterError;
                Bump();
                return;
            }

            _tradeRetry.TryRemove(underlying, out _);
            Bump();
        }
    }

    /// <summary>
    /// Existing trades plus newly fetched ones in [from, to), in time order. A fetch that re-reads part of the held span
    /// returns trades already held: those with a provider id are matched by id; without one, each fetched trade matches
    /// at most one identical held trade, so identical fills of one order inside a fetch are all kept.
    /// </summary>
    private static List<OptionTrade> Merge(List<OptionTrade> existing, IEnumerable<OptionTrade> got, DateTime from, DateTime to)
    {
        static (DateTime, string, double, double, int) Key(OptionTrade t) => (t.Time, t.Symbol, t.Price, t.Size, t.Aggressor);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var held = new Dictionary<(DateTime, string, double, double, int), int>();
        foreach (var t in existing)
        {
            if (t.Time < from || t.Time >= to) continue;
            if (t.TradeId.Length > 0) ids.Add(t.TradeId);
            else held[Key(t)] = held.GetValueOrDefault(Key(t)) + 1;
        }
        var list = new List<OptionTrade>(existing);
        foreach (var t in got)
        {
            if (t.Time < from || t.Time >= to) continue;
            if (t.TradeId.Length > 0)
            {
                if (!ids.Add(t.TradeId)) continue;
            }
            else if (held.TryGetValue(Key(t), out int count) && count > 0)
            {
                held[Key(t)] = count - 1;
                continue;
            }
            list.Add(t);
        }
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

    /// <summary>
    /// The provider's published volatility index (Deribit DVOL) as daily closes in percent points; null when the source
    /// publishes none. Only the provider's own rows are used, never recorded chain ATM IV. Schedules the same hourly daily
    /// request as <see cref="Daily"/>.
    /// </summary>
    public VolatilityIndexSeries? VolatilityIndex(string underlying)
    {
        if (Unavailable(underlying) != null || _provider is not IVolatilityIndexSource source) return null;
        Daily(underlying);
        _dailies.TryGetValue(underlying, out var hit);
        var points = hit?.Published?.Where(d => double.IsFinite(d.AtmIv) && d.AtmIv > 0).OrderBy(d => d.Date).Select(d => (d.Date.Date, d.AtmIv * 100)).ToArray();
        return new VolatilityIndexSeries(source.VolatilityIndexName, points, hit?.Error);
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
            _dailies[underlying] = new DailyEntry(Now, MergeDaily(got, LoadRecorded(underlying)), got.ToArray());
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (!ReferenceEquals(p, _provider)) return;
            LastError = ex.Message;
            _dailies.TryGetValue(underlying, out var hit);
            // Try again in about 15 minutes, keeping recorded rows meanwhile.
            _dailies[underlying] = new DailyEntry(Now.AddMinutes(-45), hit?.Days ?? MergeDaily(Array.Empty<OptionDaily>(), LoadRecorded(underlying)), hit?.Published, ex.Message);
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
