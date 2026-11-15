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

/// <summary>Per-bar flow summaries for one indicator: closed bars are kept until their revision changes; the forming bar is rebuilt.</summary>
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
        copyTrades(bar, _buffer);
        var flow = MarketFlowBar.Build(_buffer, _sourceIndex, _sources, _pointValue);
        if (closed) _closed[bar] = (candle.Time, candle.Revision, candle.Ticks, flow);
        return flow;
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
/// Derivatives context for flow studies (Binance USD-M and Bybit linear): open interest, funding and observed
/// liquidations through the shared <see cref="DerivativesData"/> service, loaded once per symbol and range.
/// A value is returned only when every venue covers it; otherwise null, so callers draw a gap, never zero.
/// </summary>
internal sealed class DerivativesContext : IDisposable
{
    private static readonly TimeSpan OpenInterestMaxAge = TimeSpan.FromMinutes(10), FundingMaxAge = TimeSpan.FromHours(9);
    private readonly Action<Action> _post;
    private readonly Action _loaded;
    private readonly bool _openInterest, _funding, _liquidations;
    private readonly Dictionary<string, IReadOnlyList<(DateTime Time, decimal Value)>> _oi = new(), _fund = new();
    private string _loadedKey = "", _loadingKey = "";
    private IDisposable? _watch;
    private string? _watched;
    private DerivativesData? _watchService;

    public DerivativesContext(Action<Action> post, Action loaded, bool openInterest, bool funding, bool liquidations)
    {
        _post = post;
        _loaded = loaded;
        _openInterest = openInterest;
        _funding = funding;
        _liquidations = liquidations;
    }

    public DerivativesData? Service { get; set; }
    public string? Message { get; private set; }
    public bool HasOpenInterest => _oi.Count == DerivativesData.Venues.Count && _oi.Values.All(v => v.Count > 0);
    private DerivativesData? Data => Service ?? DerivativesData.Shared;

    /// <summary>Starts loading when the symbol or range changed; a no-op while loaded or loading.</summary>
    public void Ensure(string symbol, DateTime from)
    {
        if (_fixture) return;
        if (Data is not { } data)
        {
            Message = "Derivatives data is unavailable in this session.";
            return;
        }
        if (symbol.Length == 0) return;
        if (_liquidations && (_watched != symbol || !ReferenceEquals(_watchService, data)))
        {
            _watch?.Dispose();
            _watch = data.WatchLiquidations(symbol);
            _watched = symbol;
            _watchService = data;
        }
        if (!_openInterest && !_funding) return;
        string key = string.Join("|", symbol, from.Ticks / TimeSpan.TicksPerHour, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(data));
        if (key == _loadedKey || key == _loadingKey) return;
        _loadingKey = key;
        Message = "Loading derivatives data…";
        var to = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            var oi = new Dictionary<string, IReadOnlyList<(DateTime, decimal)>>();
            var funding = new Dictionary<string, IReadOnlyList<(DateTime, decimal)>>();
            var notes = new List<string>();
            foreach (var venue in DerivativesData.Venues)
            {
                try
                {
                    if (_openInterest)
                    {
                        var r = await data.LoadOpenInterestAsync(venue, symbol, from, to, CancellationToken.None).ConfigureAwait(false);
                        oi[venue] = r.Samples.Select(s => (s.Time, s.Quantity)).ToArray();
                        if (r.Message != null) notes.Add(DerivativesData.VenueName(venue) + ": " + r.Message);
                    }
                    if (_funding)
                    {
                        var r = await data.LoadFundingAsync(venue, symbol, from - FundingMaxAge, to, CancellationToken.None).ConfigureAwait(false);
                        funding[venue] = r.Samples.Select(s => (s.Time, s.RatePercent)).ToArray();
                        if (r.Message != null) notes.Add(DerivativesData.VenueName(venue) + ": " + r.Message);
                    }
                }
                catch (Exception ex)
                {
                    notes.Add(DerivativesData.VenueName(venue) + ": " + ex.Message);
                }
            }
            _post(() =>
            {
                if (_loadingKey != key) return;
                _loadingKey = "";
                _loadedKey = key;
                _oi.Clear();
                _fund.Clear();
                foreach (var (venue, samples) in oi) _oi[venue] = samples;
                foreach (var (venue, samples) in funding) _fund[venue] = samples;
                Message = notes.Count == 0 ? null : string.Join(" · ", notes);
                _loaded();
            });
        });
    }

    /// <summary>Test seam: installs already-loaded samples per venue; the context then never loads.</summary>
    internal void SetOpenInterest(string venue, IReadOnlyList<(DateTime Time, decimal Value)> samples)
    {
        _fixture = true;
        Message = null;
        _oi[venue] = samples;
    }

    private bool _fixture;

    /// <summary>Summed open interest (base units) at <paramref name="time"/>, or null unless every venue has a recent sample.</summary>
    public decimal? OpenInterestAt(DateTime time) => Sum(_oi, time, OpenInterestMaxAge);

    /// <summary>Average funding (percent per period) in force at <paramref name="time"/>, or null unless every venue has one.</summary>
    public decimal? FundingAt(DateTime time)
    {
        if (_fund.Count != DerivativesData.Venues.Count) return null;
        decimal sum = 0;
        foreach (var samples in _fund.Values)
        {
            if (DerivativesPane.At(samples, time, FundingMaxAge) is not { } v) return null;
            sum += v;
        }
        return sum / _fund.Count;
    }

    private static decimal? Sum(Dictionary<string, IReadOnlyList<(DateTime Time, decimal Value)>> data, DateTime time, TimeSpan maxAge)
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
        var snapshot = _watchService.Liquidations(_watched, from, to);
        decimal longs = 0, shorts = 0;
        foreach (var e in snapshot.Events)
        {
            if (e.Time < from || e.Time >= to) continue;
            decimal amount = notional ? e.Notional : e.Quantity;
            if (e.LongLiquidated) longs += amount; else shorts += amount;
        }
        var end = to < DateTime.UtcNow ? to : DateTime.UtcNow;
        bool observed = DerivativesData.Venues.All(v => snapshot.Windows.Any(w => w.VenueId == v && w.From <= from && w.To >= end));
        return observed || longs + shorts > 0 ? (longs, shorts) : null;
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _watch = null;
        _watched = null;
        _watchService = null;
    }
}
