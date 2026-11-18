// Behavior adapted from the KVP capability reference (commits a9c40ae, 8cec920, 3629e91); see THIRD_PARTY_NOTICES.md.
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds.Derivatives;
using TradingHub.Market;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Measured aggressor flow of one bar, attributed to the instrument's markets by each print's recorded source identity.
/// Notional is price × size in the instrument's quote currency. Only individually observed prints with a known side
/// enter trade counts and trade-size bands; compact measured packets count toward per-market notional but are never
/// treated as single trades. Prints whose market is unknown stay unattributed, and a bar without measured prints is a
/// gap (<see cref="HasFlow"/> false), never zero flow.
/// </summary>
internal sealed class MarketFlowBar
{
    /// <summary>Upper notional limits of the trade-size bands; the last band is everything above the last limit.</summary>
    public static readonly decimal[] SizeLimits = { 25_000, 50_000, 100_000, 250_000, 500_000, 1_000_000, 5_000_000 };
    public const int SizeBands = 8;

    public MarketFlowBar(int sources)
    {
        BuyNotional = new decimal[sources];
        SellNotional = new decimal[sources];
        BuyBase = new decimal[sources];
        SellBase = new decimal[sources];
    }

    public decimal[] BuyNotional { get; }
    public decimal[] SellNotional { get; }
    public decimal[] BuyBase { get; }
    public decimal[] SellBase { get; }
    public decimal UnattributedBuy { get; set; }
    public decimal UnattributedSell { get; set; }
    public decimal[] SizeBuy { get; } = new decimal[SizeBands];
    public decimal[] SizeSell { get; } = new decimal[SizeBands];
    public int Trades { get; set; }
    public int Buys { get; set; }
    public int Sells { get; set; }
    public decimal BuyVolume { get; set; }
    public decimal SellVolume { get; set; }
    public decimal MaxTradeNotional { get; set; }

    /// <summary>Some measured print with a known side was attributed to this bar.</summary>
    public bool HasFlow { get; set; }

    /// <summary>Individual observed prints exist, so trade counts and size bands are meaningful.</summary>
    public bool HasIndividualPrints => Trades > 0;

    /// <summary>Compact measured packets contributed (their sizes are not individual trades).</summary>
    public bool HasCompact { get; set; }

    public decimal Net(int source) => BuyNotional[source] - SellNotional[source];
    public decimal Gross(int source) => BuyNotional[source] + SellNotional[source];

    public static int Band(decimal notional)
    {
        for (int i = 0; i < SizeLimits.Length; i++)
            if (notional < SizeLimits[i]) return i;
        return SizeBands - 1;
    }

    /// <summary>Builds the bar from its prints; <paramref name="sourceIndex"/> maps a provenance identity to a market index.</summary>
    public static MarketFlowBar Build(IReadOnlyList<BarTrade> trades, IReadOnlyDictionary<string, int> sourceIndex, int sources, decimal pointValue = 1)
    {
        var bar = new MarketFlowBar(sources);
        foreach (var trade in trades)
        {
            if (trade.Side is not (TradeSide.Buy or TradeSide.Sell) || !trade.Provenance.IsMeasured || trade.Provenance.IsSideInferred) continue;
            bool buy = trade.Side == TradeSide.Buy;
            decimal notional = trade.Price * trade.Volume * pointValue; // the contract multiplier makes futures notional real
            bar.HasFlow = true;
            if (trade.Provenance.SourceIdentity is { } identity && sourceIndex.TryGetValue(identity, out int index))
            {
                if (buy) { bar.BuyNotional[index] += notional; bar.BuyBase[index] += trade.Volume; }
                else { bar.SellNotional[index] += notional; bar.SellBase[index] += trade.Volume; }
            }
            else if (buy) bar.UnattributedBuy += notional;
            else bar.UnattributedSell += notional;
            if (!trade.Provenance.IsObservedTrade)
            {
                bar.HasCompact = true;
                continue;
            }
            int band = Band(notional);
            bar.Trades++;
            bar.MaxTradeNotional = Math.Max(bar.MaxTradeNotional, notional);
            if (buy) { bar.Buys++; bar.BuyVolume += trade.Volume; bar.SizeBuy[band] += notional; }
            else { bar.Sells++; bar.SellVolume += trade.Volume; bar.SizeSell[band] += notional; }
        }
        return bar;
    }

    /// <summary>Signed notional of the bands entirely at or below <paramref name="retailMax"/> and entirely at or above <paramref name="whaleMin"/>.</summary>
    public (decimal Retail, decimal Whales) SizeDelta(decimal retailMax, decimal whaleMin)
    {
        decimal retail = 0, whales = 0;
        for (int k = 0; k < SizeBands; k++)
        {
            decimal lower = k == 0 ? 0 : SizeLimits[k - 1];
            decimal upper = k < SizeLimits.Length ? SizeLimits[k] : decimal.MaxValue;
            decimal delta = SizeBuy[k] - SizeSell[k];
            if (upper <= retailMax) retail += delta;
            if (lower >= whaleMin) whales += delta;
        }
        return (retail, whales);
    }
}

/// <summary>
/// Per-bar flow summaries for one indicator: closed bars are kept until their revision changes; the forming bar is rebuilt.
/// Callers bound the memory with <see cref="Retain"/> to the bars they draw.
/// </summary>
internal sealed class MarketFlowCache
{
    private readonly Dictionary<int, (DateTime Time, long Revision, decimal Trades, MarketFlowBar Bar)> _closed = new();
    private readonly List<BarTrade> _buffer = new();
    private Dictionary<string, int> _sourceIndex = new(StringComparer.Ordinal);
    private int _sources;
    private decimal _pointValue = 1;

    public IReadOnlyList<InstrumentSource> Sources { get; private set; } = Array.Empty<InstrumentSource>();

    /// <summary>Resets the cache for the indicator's current market list.</summary>
    public void Reset(IReadOnlyList<InstrumentSource> sources, decimal pointValue = 1)
    {
        _closed.Clear();
        Sources = sources;
        _sources = sources.Count;
        _pointValue = pointValue > 0 ? pointValue : 1;
        _sourceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < sources.Count; i++) _sourceIndex.TryAdd(sources[i].Identity, i);
    }

    /// <summary>The bar's flow; <paramref name="copyTrades"/> is the indicator's own GetBarTrades.</summary>
    public MarketFlowBar Get(Indicator indicator, int bar, Action<int, List<BarTrade>> copyTrades)
    {
        var candle = indicator.GetCandle(bar);
        bool closed = candle.IsCandleFinished;
        if (closed && _closed.TryGetValue(bar, out var hit) && hit.Time == candle.Time && hit.Revision == candle.Revision && hit.Trades == candle.Ticks) return hit.Bar;
        var flow = Compute(bar, copyTrades);
        if (closed) _closed[bar] = (candle.Time, candle.Revision, candle.Ticks, flow);
        return flow;
    }

    /// <summary>The bar's flow, built without keeping it (for callers that read each bar once).</summary>
    public MarketFlowBar Compute(int bar, Action<int, List<BarTrade>> copyTrades)
    {
        copyTrades(bar, _buffer);
        return MarketFlowBar.Build(_buffer, _sourceIndex, _sources, _pointValue);
    }

    /// <summary>Forgets kept bars outside [<paramref name="from"/>, <paramref name="to"/>] and the optional second range.</summary>
    public void Retain(int from, int to, int from2 = 0, int to2 = -1)
    {
        int wanted = Math.Max(0, to - from + 1) + Math.Max(0, to2 - from2 + 1);
        if (_closed.Count <= wanted + 64) return;
        foreach (int bar in _closed.Keys)
            if ((bar < from || bar > to) && (bar < from2 || bar > to2)) _closed.Remove(bar);
    }
}

/// <summary>Display formatting shared by the flow studies (invariant digits; never translated).</summary>
internal static class FlowFormat
{
    public static string Compact(decimal value)
    {
        decimal a = Math.Abs(value);
        var c = CultureInfo.InvariantCulture;
        string s = a >= 1_000_000_000 ? (a / 1_000_000_000).ToString("0.##", c) + "B"
            : a >= 1_000_000 ? (a / 1_000_000).ToString("0.##", c) + "M"
            : a >= 1000 ? (a / 1000).ToString("0.#", c) + "K"
            : a >= 10 ? a.ToString("0.#", c) : a.ToString("0.###", c);
        return (value < 0 ? "−" : "") + s;
    }

    public static string Signed(decimal value) => (value > 0 ? "+" : "") + Compact(value);

    /// <summary>The chart instrument's contract multiplier (quote value of one point per unit), 1 when unknown.</summary>
    public static decimal PointValue(Indicator indicator) => indicator.InstrumentInfo is { PointValue: > 0 } info ? info.PointValue : 1;

    public static Color Alpha(Color color, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);

    public static readonly Color Spot = Color.FromArgb(255, 86, 160, 255);
    public static readonly Color Perpetual = Color.FromArgb(255, 242, 170, 58);
    public static readonly Color Buy = Color.FromArgb(255, 38, 194, 129);
    public static readonly Color Sell = Color.FromArgb(255, 240, 82, 90);
    public static readonly Color Dim = Color.FromArgb(220, 170, 176, 188);
    public static readonly Color Back = Color.FromArgb(210, 14, 17, 24);
    public static readonly Color Amber = Color.FromArgb(255, 242, 181, 58);
}

/// <summary>
/// Live-only, rate-limited admission for study notifications. An event older than <see cref="MaxAge"/> (historical
/// load, recalculation, replay) or dated in the future never fires; at most four notifications per ten seconds and one
/// per second. Delivery itself goes through the chart's alert path into the Alert Center, which owns sound and consent.
/// </summary>
internal sealed class LiveAlertGate
{
    private readonly Queue<DateTime> _recent = new();
    private DateTime _last = DateTime.MinValue;

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromSeconds(30);

    public bool Allow(DateTime eventUtc)
    {
        var now = Clock();
        if (now - eventUtc > MaxAge || eventUtc - now > TimeSpan.FromSeconds(5)) return false;
        while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromSeconds(10)) _recent.Dequeue();
        if (_recent.Count >= 4 || now - _last < TimeSpan.FromSeconds(1)) return false;
        _recent.Enqueue(now);
        _last = now;
        return true;
    }
}

/// <summary>
/// "Stands out" rule: a value above the mean plus k standard deviations of the preceding window (the bar never raises
/// its own threshold). Missing values are skipped; fewer than twelve known preceding values give no threshold.
/// </summary>
public static class StandOutRule
{
    public const int MinimumBaseline = 12;

    public static double? Threshold(Func<int, double?> value, int bar, int baseline, double k) =>
        Moments(value, bar, baseline) is { } m ? m.Mean + k * m.Deviation : null;

    public static double? Sigmas(Func<int, double?> value, int bar, int baseline) =>
        Moments(value, bar, baseline) is { } m && m.Deviation > 0 && value(bar) is { } v ? (v - m.Mean) / m.Deviation : null;

    private static (double Mean, double Deviation)? Moments(Func<int, double?> value, int bar, int baseline)
    {
        double sum = 0, squares = 0;
        int n = 0;
        for (int i = Math.Max(0, bar - baseline); i < bar; i++)
        {
            if (value(i) is not { } v || double.IsNaN(v)) continue;
            sum += v; squares += v * v; n++;
        }
        if (n < MinimumBaseline) return null;
        double mean = sum / n;
        return (mean, Math.Sqrt(Math.Max(0, squares / n - mean * mean)));
    }
}

/// <summary>
/// One derivatives series (open interest or funding) per venue for one symbol and range, through the shared
/// <see cref="DerivativesData"/> service. The first request covers the whole range; while live time advances, each
/// venue is then refreshed from its latest sample with one short request at most every refresh interval, so new bars
/// never become permanent gaps. Requests run in the background; state changes only inside the caller's GUI-thread post.
/// </summary>
internal sealed class DerivativesSamples
{
    /// <summary>Open interest is published every five minutes and is stale after ten; funding settles every few hours.</summary>
    internal static readonly TimeSpan OpenInterestRefresh = TimeSpan.FromMinutes(2), FundingRefresh = TimeSpan.FromMinutes(15);
    // A refresh never reaches further back than this, even for a venue that has no sample yet.
    private static readonly TimeSpan RefreshReach = TimeSpan.FromDays(1);
    private readonly bool _funding;
    private readonly Dictionary<string, IReadOnlyList<(DateTime Time, decimal Value)>> _samples = new();
    private string _loadedKey = "", _loadingKey = "";
    private string[] _venues = Array.Empty<string>();
    private DateTime _from, _loadedTo;
    private bool _refreshing;
    private string? _notes, _refreshNotes;

    public DerivativesSamples(bool funding) => _funding = funding;

    /// <summary>Time-ordered samples of each venue that loaded.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(DateTime Time, decimal Value)>> ByVenue => _samples;

    /// <summary>Changes whenever the samples change.</summary>
    public long Version { get; private set; }

    internal const string LoadingMessage = "Loading derivatives data…";

    public bool IsLoading => _loadingKey.Length > 0;

    /// <summary>Venue notes of the last load and refresh (null when there are none).</summary>
    public string? Notes => Join(_notes, _refreshNotes);

    public string? Message => IsLoading ? LoadingMessage : Notes;

    internal static string? Join(string? a, string? b) => a == null ? b : b == null ? a : a + " · " + b;

    /// <summary>
    /// Loads <paramref name="venues"/> over [<paramref name="from"/>, <paramref name="now"/>] when the symbol, venues or
    /// range changed; refreshes the loaded series once it is a refresh interval old; otherwise does nothing, so it is
    /// cheap to call on every calculation of the last bar. <paramref name="changed"/> runs through <paramref name="post"/>
    /// when samples changed.
    /// </summary>
    public void Ensure(DerivativesData data, string symbol, IReadOnlyList<string> venues, DateTime from, DateTime now, Action<Action> post, Action changed)
    {
        string key = string.Join("|", symbol, string.Join(",", venues), from.Ticks / TimeSpan.TicksPerHour, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(data));
        if (key == _loadingKey) return;
        if (key != _loadedKey) Load(data, symbol, venues.ToArray(), from, now, key, post, changed);
        else if (!_refreshing && _loadingKey.Length == 0 && now - _loadedTo >= (_funding ? FundingRefresh : OpenInterestRefresh)) Refresh(data, symbol, now, key, post, changed);
    }

    private void Load(DerivativesData data, string symbol, string[] venues, DateTime from, DateTime now, string key, Action<Action> post, Action changed)
    {
        _loadingKey = key;
        _ = Task.Run(async () =>
        {
            var loaded = new Dictionary<string, IReadOnlyList<(DateTime, decimal)>>();
            var notes = new List<string>();
            foreach (var venue in venues)
            {
                try
                {
                    var (samples, note) = await LoadAsync(data, venue, symbol, from, now).ConfigureAwait(false);
                    loaded[venue] = samples;
                    if (note != null) notes.Add(note);
                }
                catch (Exception ex)
                {
                    notes.Add(DerivativesData.VenueName(venue) + ": " + ex.Message);
                }
            }
            post(() =>
            {
                if (_loadingKey != key) return;
                _loadingKey = "";
                _loadedKey = key;
                _venues = venues;
                _from = from;
                _loadedTo = now;
                _samples.Clear();
                foreach (var (venue, samples) in loaded) _samples[venue] = samples;
                _notes = notes.Count == 0 ? null : string.Join(" · ", notes);
                _refreshNotes = null;
                Version++;
                changed();
            });
        });
    }

    private void Refresh(DerivativesData data, string symbol, DateTime now, string key, Action<Action> post, Action changed)
    {
        _refreshing = true;
        var starts = new (string Venue, DateTime From)[_venues.Length];
        for (int i = 0; i < _venues.Length; i++)
        {
            var start = now - RefreshReach > _from ? now - RefreshReach : _from;
            if (_samples.TryGetValue(_venues[i], out var known) && known.Count > 0 && known[^1].Time > start) start = known[^1].Time;
            starts[i] = (_venues[i], start);
        }
        _ = Task.Run(async () =>
        {
            var fresh = new List<(string Venue, IReadOnlyList<(DateTime Time, decimal Value)> Samples)>();
            var failures = new List<string>();
            foreach (var (venue, start) in starts)
            {
                try
                {
                    fresh.Add((venue, (await LoadAsync(data, venue, symbol, start, now).ConfigureAwait(false)).Samples));
                }
                catch (Exception ex)
                {
                    failures.Add(DerivativesData.VenueName(venue) + ": " + ex.Message);
                }
            }
            post(() =>
            {
                _refreshing = false;
                if (key != _loadedKey || _loadingKey.Length > 0) return;
                _loadedTo = now;
                _refreshNotes = failures.Count == 0 ? null : string.Join(" · ", failures);
                bool any = false;
                foreach (var (venue, samples) in fresh)
                {
                    var old = _samples.TryGetValue(venue, out var o) ? o : Array.Empty<(DateTime Time, decimal Value)>();
                    var merged = Merge(old, samples);
                    if (ReferenceEquals(merged, old)) continue;
                    _samples[venue] = merged;
                    any = true;
                }
                if (!any) return;
                Version++;
                changed();
            });
        });
    }

    /// <summary>
    /// <paramref name="old"/> before the first refreshed sample followed by <paramref name="fresh"/>; <paramref name="old"/>
    /// itself when the refresh brought nothing new.
    /// </summary>
    internal static IReadOnlyList<(DateTime Time, decimal Value)> Merge(IReadOnlyList<(DateTime Time, decimal Value)> old, IReadOnlyList<(DateTime Time, decimal Value)> fresh)
    {
        if (fresh.Count == 0) return old;
        int keep = 0, hi = old.Count;
        while (keep < hi)
        {
            int mid = (keep + hi) >> 1;
            if (old[mid].Time < fresh[0].Time) keep = mid + 1;
            else hi = mid;
        }
        bool same = old.Count - keep == fresh.Count;
        for (int i = 0; same && i < fresh.Count; i++) same = old[keep + i] == fresh[i];
        if (same) return old;
        var merged = new (DateTime Time, decimal Value)[keep + fresh.Count];
        for (int i = 0; i < keep; i++) merged[i] = old[i];
        for (int i = 0; i < fresh.Count; i++) merged[keep + i] = fresh[i];
        return merged;
    }

    private async Task<(IReadOnlyList<(DateTime Time, decimal Value)> Samples, string? Note)> LoadAsync(DerivativesData data, string venue, string symbol, DateTime from, DateTime to)
    {
        if (_funding)
        {
            var funding = await data.LoadFundingAsync(venue, symbol, from, to, CancellationToken.None).ConfigureAwait(false);
            return (funding.Samples.Select(s => (s.Time, s.RatePercent)).ToArray(), funding.Message);
        }
        var oi = await data.LoadOpenInterestAsync(venue, symbol, from, to, CancellationToken.None).ConfigureAwait(false);
        return (oi.Samples.Select(s => (s.Time, s.Quantity)).ToArray(), oi.Message == null ? null : DerivativesData.VenueName(venue) + ": " + oi.Message);
    }

    /// <summary>Test seam: installs already-loaded samples for a venue.</summary>
    internal void Set(string venue, IReadOnlyList<(DateTime Time, decimal Value)> samples)
    {
        _samples[venue] = samples;
        Version++;
    }
}

/// <summary>
/// Derivatives context for flow studies (Binance USD-M and Bybit linear): open interest, funding and observed
/// liquidations through the shared <see cref="DerivativesData"/> service, loaded per symbol and range and refreshed
/// while live time advances (<see cref="DerivativesSamples"/>). A value is returned only when every venue covers it;
/// otherwise null, so callers draw a gap, never zero.
/// </summary>
internal sealed class DerivativesContext : IDisposable
{
    private static readonly TimeSpan OpenInterestMaxAge = TimeSpan.FromMinutes(10), FundingMaxAge = TimeSpan.FromHours(9), SnapshotAge = TimeSpan.FromSeconds(1);
    private readonly Action<Action> _post;
    private readonly Action _loaded;
    private readonly bool _liquidations;
    private readonly DerivativesSamples? _oi, _fund;
    private string? _unavailable;
    private bool _fixture;
    private IDisposable? _watch;
    private string? _watched;
    private DerivativesData? _watchService;
    private LiquidationEvent[] _events = Array.Empty<LiquidationEvent>();
    private IReadOnlyList<ObservedWindow> _windows = Array.Empty<ObservedWindow>();
    private long _snapshotGeneration = -1;
    private DateTime _snapshotFrom = DateTime.MaxValue, _snapshotAt;

    public DerivativesContext(Action<Action> post, Action loaded, bool openInterest, bool funding, bool liquidations)
    {
        _post = post;
        _loaded = loaded;
        _liquidations = liquidations;
        if (openInterest) _oi = new DerivativesSamples(funding: false);
        if (funding) _fund = new DerivativesSamples(funding: true);
    }

    public DerivativesData? Service { get; set; }

    /// <summary>Live time (a test seam).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public string? Message => _unavailable ?? (_oi?.IsLoading == true || _fund?.IsLoading == true ? DerivativesSamples.LoadingMessage : DerivativesSamples.Join(_oi?.Notes, _fund?.Notes));
    public bool HasOpenInterest => _oi != null && _oi.ByVenue.Count == DerivativesData.Venues.Count && _oi.ByVenue.Values.All(v => v.Count > 0);

    /// <summary>Changes whenever the open-interest samples change.</summary>
    public long OpenInterestVersion => _oi?.Version ?? 0;

    private DerivativesData? Data => Service ?? DerivativesData.Shared;

    /// <summary>
    /// Starts loading when the symbol or range changed and refreshes loaded open interest and funding as live time
    /// advances; a cheap no-op otherwise, so callers run it on every calculation of the last bar.
    /// </summary>
    public void Ensure(string symbol, DateTime from)
    {
        if (_fixture) return;
        if (Data is not { } data)
        {
            _unavailable = "Derivatives data is unavailable in this session.";
            return;
        }
        _unavailable = null;
        if (symbol.Length == 0) return;
        if (_liquidations && (_watched != symbol || !ReferenceEquals(_watchService, data)))
        {
            _watch?.Dispose();
            _watch = data.WatchLiquidations(symbol);
            _watched = symbol;
            _watchService = data;
            _snapshotGeneration = -1;
        }
        var now = Clock();
        _oi?.Ensure(data, symbol, DerivativesData.Venues, from, now, _post, _loaded);
        _fund?.Ensure(data, symbol, DerivativesData.Venues, from - FundingMaxAge, now, _post, _loaded);
    }

    /// <summary>Test seam: installs already-loaded samples per venue; the context then never loads.</summary>
    internal void SetOpenInterest(string venue, IReadOnlyList<(DateTime Time, decimal Value)> samples)
    {
        _fixture = true;
        _unavailable = null;
        _oi?.Set(venue, samples);
    }

    /// <summary>Summed open interest (base units) at <paramref name="time"/>, or null unless every venue has a recent sample.</summary>
    public decimal? OpenInterestAt(DateTime time) => _oi == null ? null : Sum(_oi.ByVenue, time, OpenInterestMaxAge);

    /// <summary>Average funding (percent per period) in force at <paramref name="time"/>, or null unless every venue has one.</summary>
    public decimal? FundingAt(DateTime time) => _fund == null || Sum(_fund.ByVenue, time, FundingMaxAge) is not { } sum ? null : sum / _fund.ByVenue.Count;

    private static decimal? Sum(IReadOnlyDictionary<string, IReadOnlyList<(DateTime Time, decimal Value)>> data, DateTime time, TimeSpan maxAge)
    {
        if (data.Count != DerivativesData.Venues.Count) return null;
        decimal sum = 0;
        foreach (var samples in data.Values)
        {
            if (DerivativesPane.At(samples, time, maxAge) is not { } v) return null;
            sum += v;
        }
        return sum;
    }

    /// <summary>Changes with each new observed liquidation (for redraws).</summary>
    public long LiquidationGeneration => _watched != null && _watchService != null ? _watchService.LiquidationGeneration(_watched) : 0;

    /// <summary>
    /// Liquidated quantity (base units) of longs and shorts in [from, to), or null when the interval was not fully
    /// observed by every venue's stream and nothing was seen.
    /// </summary>
    public (decimal Long, decimal Short)? Liquidations(DateTime from, DateTime to, bool notional = false)
    {
        if (_watched == null || _watchService == null) return null;
        var now = Clock();
        long generation = _watchService.LiquidationGeneration(_watched);
        // One time-ordered snapshot serves every bar of a calculation or frame. It is retaken when the stream records
        // something, when an earlier range is asked for, or after a second (open windows end at the snapshot time).
        if (generation != _snapshotGeneration || from < _snapshotFrom || now - _snapshotAt >= SnapshotAge)
        {
            var snapshot = _watchService.Liquidations(_watched, from, DateTime.MaxValue);
            _events = DerivativesPane.ByTime(snapshot.Events);
            _windows = snapshot.Windows;
            _snapshotGeneration = generation;
            _snapshotFrom = from;
            _snapshotAt = now;
        }
        decimal longs = 0, shorts = 0;
        for (int i = DerivativesPane.FirstAtOrAfter(_events, from); i < _events.Length && _events[i].Time < to; i++)
        {
            var e = _events[i];
            decimal amount = notional ? e.Notional : e.Quantity;
            if (e.LongLiquidated) longs += amount; else shorts += amount;
        }
        bool observed = DerivativesPane.Observed(_windows, DerivativesData.Venues, from, to < _snapshotAt ? to : _snapshotAt);
        return observed || longs + shorts > 0 ? (longs, shorts) : null;
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _watch = null;
        _watched = null;
        _watchService = null;
        _events = Array.Empty<LiquidationEvent>();
        _windows = Array.Empty<ObservedWindow>();
        _snapshotGeneration = -1;
    }
}
