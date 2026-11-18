// Behavior adapted from the KVP capability reference (commit e43b23b); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Crypto derivatives context in one pane: funding rate, open interest (level or bar change) and liquidations, for
/// Binance USD-M and Bybit linear perpetuals separately and combined. Public data, no account. Values outside a
/// venue's coverage are gaps, not zero; liquidations exist only for periods a liquidation stream was observed.
/// </summary>
[DisplayName("Derivatives: funding, open interest, liquidations")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Funding rate, open interest (level or change) and observed liquidations from Binance and Bybit perpetuals, per exchange and combined. For USD-margined crypto symbols (BTCUSDT, ETHUSDT…). Unobserved periods are gaps, not zero.")]
public sealed class DerivativesPane : Indicator
{
    public enum DerivativesView
    {
        [Display(Name = "Funding rate (%)")] Funding,
        [Display(Name = "Open interest")] OpenInterest,
        [Display(Name = "Open interest change (bar to bar)")] OpenInterestChange,
        [Display(Name = "Liquidations (longs − / shorts +, notional)")] Liquidations,
    }

    private static readonly TimeSpan FundingMaxAge = TimeSpan.FromHours(9), OpenInterestMaxAge = TimeSpan.FromMinutes(10);
    private readonly OptionalValueDataSeries _combined = new("AGG", "Combined") { Color = Color.FromArgb(255, 230, 230, 230), Width = 2 };
    private readonly OptionalValueDataSeries _binance = new("BIN", "Binance") { Color = Color.FromArgb(255, 242, 181, 58), Width = 1 };
    private readonly OptionalValueDataSeries _bybit = new("BYB", "Bybit") { Color = Color.FromArgb(255, 86, 180, 255), Width = 1 };
    private readonly OptionalValueDataSeries _longs = new("LIQL", "Longs liquidated") { Color = Color.FromArgb(220, 240, 82, 90), VisualType = VisualMode.Histogram };
    private readonly OptionalValueDataSeries _shorts = new("LIQS", "Shorts liquidated") { Color = Color.FromArgb(220, 38, 194, 129), VisualType = VisualMode.Histogram };
    private readonly RenderFont _statusFont = new("Arial", 9);
    private readonly Action _tick;
    private readonly DerivativesSamples _openInterest = new(funding: false), _funding = new(funding: true);
    private DerivativesView _view = DerivativesView.Funding;
    private bool _useBinance = true, _useBybit = true, _showEach = true;
    private string[] _venues = { DerivativesData.Binance, DerivativesData.Bybit };
    private string _symbolOverride = "";
    private string? _message;
    private IDisposable? _watch;
    private string? _watchedSymbol;
    private long _liquidationGeneration;
    private LiquidationEvent[] _events = Array.Empty<LiquidationEvent>();
    private IReadOnlyList<ObservedWindow> _windows = Array.Empty<ObservedWindow>();
    private DateTime _liquidationsAt;

    public DerivativesPane() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _combined;
        DataSeries.Add(_binance);
        DataSeries.Add(_bybit);
        DataSeries.Add(_longs);
        DataSeries.Add(_shorts);
        LineSeries.Add(new LineSeries("Zero", "Zero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _tick = OnTimer;
    }

    [Display(Name = "Data", GroupName = "Calculation", Order = 10)]
    public DerivativesView View { get => _view; set { _view = value; RecalculateValues(); } }

    [Display(Name = "Binance (USD-M futures)", GroupName = "Exchanges", Order = 20)]
    public bool UseBinance { get => _useBinance; set { _useBinance = value; RecalculateValues(); } }

    [Display(Name = "Bybit (linear perpetuals)", GroupName = "Exchanges", Order = 21)]
    public bool UseBybit { get => _useBybit; set { _useBybit = value; RecalculateValues(); } }

    [Display(Name = "Show each exchange as well as the combined line", GroupName = "Visualization", Order = 30)]
    public bool ShowEach { get => _showEach; set { _showEach = value; RecalculateValues(); } }

    [Display(Name = "Perpetual symbol (blank = derived from the chart)", GroupName = "Calculation", Order = 40)]
    public string SymbolOverride { get => _symbolOverride; set { _symbolOverride = value ?? ""; RecalculateValues(); } }

    /// <summary>The data service; defaults to the application's shared one.</summary>
    [Browsable(false)]
    public DerivativesData? Service { get; set; }

    [Browsable(false)]
    public string Status => CurrentMessage ?? "";

    /// <summary>Live time (a test seam).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private DerivativesData? Data => Service ?? DerivativesData.Shared;

    private DerivativesSamples Samples => _view == DerivativesView.Funding ? _funding : _openInterest;

    private string? CurrentMessage => _message ?? (_view == DerivativesView.Liquidations ? null : Samples.Message);

    private IEnumerable<string> SelectedVenues
    {
        get
        {
            if (_useBinance) yield return DerivativesData.Binance;
            if (_useBybit) yield return DerivativesData.Bybit;
        }
    }

    /// <summary>Base asset plus USDT for USD-family charts (BTC-USDT-SWAP, BTC-USD, BTC → BTCUSDT); otherwise the chart symbol.</summary>
    public static string DerivedSymbol(string symbol, string baseAsset, string quoteAsset)
    {
        string quote = quoteAsset.Trim().ToUpperInvariant();
        if (baseAsset.Trim().Length > 0 && quote is "USDT" or "USD" or "USDC") return baseAsset.Trim().ToUpperInvariant() + "USDT";
        return symbol.Trim().ToUpperInvariant().Replace("-", "").Replace("/", "").Replace("_", "");
    }

    private string Symbol => _symbolOverride.Trim().Length > 0 ? _symbolOverride.Trim().ToUpperInvariant()
        : DerivedSymbol(InstrumentInfo?.Instrument ?? "", InstrumentInfo?.BaseAsset ?? "", InstrumentInfo?.QuoteAsset ?? "");

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(2), _tick);

    protected override void OnDispose()
    {
        UnsubscribeFromTimer(TimeSpan.FromSeconds(2), _tick);
        _watch?.Dispose();
        _watch = null;
    }

    private void OnTimer()
    {
        if (_view != DerivativesView.Liquidations) EnsureSamples();
        else if (_watchedSymbol != null && Data is { } data && data.LiquidationGeneration(_watchedSymbol) != _liquidationGeneration) RecalculateValues();
    }

    /// <summary>Loads the selected funding or open-interest series, or refreshes it as live time advances (a no-op while fresh).</summary>
    private void EnsureSamples()
    {
        if (_view == DerivativesView.Liquidations || CurrentBar <= 0 || Data is not { } data) return;
        string symbol = Symbol;
        if (symbol.Length == 0) return;
        var from = GetCandle(0).Time;
        Samples.Ensure(data, symbol, _venues, _view == DerivativesView.Funding ? from - FundingMaxAge : from, Clock(), DoActionInGuiThread, RecalculateValues);
    }

    private TimeSpan Step
    {
        get
        {
            var period = ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromHours(1);
            return period <= TimeSpan.Zero ? TimeSpan.FromHours(1) : period;
        }
    }

    protected override void OnRecalculate()
    {
        bool liquidations = _view == DerivativesView.Liquidations;
        _longs.VisualType = liquidations ? VisualMode.Histogram : VisualMode.Hide;
        _shorts.VisualType = liquidations ? VisualMode.Histogram : VisualMode.Hide;
        _combined.VisualType = liquidations ? VisualMode.Hide : _view == DerivativesView.OpenInterestChange ? VisualMode.Histogram : VisualMode.Line;
        _binance.VisualType = !liquidations && _showEach && _useBinance ? VisualMode.Line : VisualMode.Hide;
        _bybit.VisualType = !liquidations && _showEach && _useBybit ? VisualMode.Line : VisualMode.Hide;
        _venues = SelectedVenues.ToArray();
        _message = null;
        if (Data is not { } data)
        {
            _message = "Derivatives data is unavailable in this session.";
            return;
        }
        if (!liquidations || CurrentBar <= 0 || Symbol.Length == 0) return;
        string symbol = Symbol;
        if (_watchedSymbol != symbol)
        {
            _watch?.Dispose();
            _watch = data.WatchLiquidations(symbol);
            _watchedSymbol = symbol;
        }
        _liquidationGeneration = data.LiquidationGeneration(symbol);
        _liquidationsAt = Clock();
        var snapshot = data.Liquidations(symbol, GetCandle(0).Time, _liquidationsAt.AddMinutes(1));
        _events = ByTime(snapshot.Events);
        _windows = snapshot.Windows;
        _message = snapshot.Message ?? (_windows.Count == 0
            ? "Liquidations: listening live; nothing is known before the stream connected."
            : "Liquidations observed while streams were connected; Binance publishes at most one per second (sampled).");
    }

    /// <summary>The latest sample at or before <paramref name="time"/> within <paramref name="maxAge"/>, else null (a gap).</summary>
    public static decimal? At(IReadOnlyList<(DateTime Time, decimal Value)> samples, DateTime time, TimeSpan maxAge)
    {
        int lo = 0, hi = samples.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (samples[mid].Time <= time) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 && time - samples[found].Time <= maxAge ? samples[found].Value : null;
    }

    /// <summary>A copy of <paramref name="events"/> ordered by time (the venue streams interleave on arrival).</summary>
    internal static LiquidationEvent[] ByTime(IReadOnlyList<LiquidationEvent> events)
    {
        var sorted = events.ToArray();
        for (int i = 1; i < sorted.Length; i++)
            if (sorted[i].Time < sorted[i - 1].Time)
            {
                Array.Sort(sorted, (a, b) => a.Time.CompareTo(b.Time));
                break;
            }
        return sorted;
    }

    /// <summary>Index of the first of the time-ordered <paramref name="events"/> at or after <paramref name="time"/>.</summary>
    internal static int FirstAtOrAfter(LiquidationEvent[] events, DateTime time)
    {
        int lo = 0, hi = events.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (events[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Whether each of <paramref name="venues"/> (at least one) had a stream window covering [from, to].</summary>
    internal static bool Observed(IReadOnlyList<ObservedWindow> windows, IReadOnlyList<string> venues, DateTime from, DateTime to)
    {
        if (venues.Count == 0) return false;
        for (int v = 0; v < venues.Count; v++)
        {
            bool covered = false;
            for (int i = 0; i < windows.Count && !covered; i++)
                covered = windows[i].VenueId == venues[v] && windows[i].From <= from && windows[i].To >= to;
            if (!covered) return false;
        }
        return true;
    }

    private DateTime BarEnd(int bar) => bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : GetCandle(bar).Time + Step;

    protected override void OnCalculate(int bar, decimal value)
    {
        var start = GetCandle(bar).Time;
        var end = BarEnd(bar);
        if (_view == DerivativesView.Liquidations)
        {
            // A bar is known only when every selected venue's stream covered all of it (up to the snapshot).
            bool observed = Observed(_windows, _venues, start, Min(end, _liquidationsAt));
            decimal longs = 0, shorts = 0;
            for (int i = FirstAtOrAfter(_events, start); i < _events.Length && _events[i].Time < end; i++)
            {
                var l = _events[i];
                if (!(l.VenueId == DerivativesData.Binance ? _useBinance : l.VenueId == DerivativesData.Bybit && _useBybit)) continue;
                if (l.LongLiquidated) longs += l.Notional;
                else shorts += l.Notional;
            }
            bool any = longs + shorts > 0;
            _longs.Set(bar, -longs, observed || any);
            _shorts.Set(bar, shorts, observed || any);
            return;
        }

        if (bar == CurrentBar - 1) EnsureSamples();
        var data = Samples.ByVenue;
        var maxAge = _view == DerivativesView.Funding ? FundingMaxAge : OpenInterestMaxAge;
        decimal? Venue(string venue, DateTime time) => data.TryGetValue(venue, out var samples) ? At(samples, time, maxAge) : null;
        var at = Min(end, Clock()) - TimeSpan.FromTicks(1);
        var ends = _venues.Select(v => Venue(v, at)).ToArray();
        decimal? combined = _view == DerivativesView.Funding
            ? ends.All(v => v.HasValue) && ends.Length > 0 ? ends.Average(v => v!.Value) : null
            : ends.All(v => v.HasValue) && ends.Length > 0 ? ends.Sum(v => v!.Value) : null;
        decimal? binance = _useBinance ? Venue(DerivativesData.Binance, at) : null;
        decimal? bybit = _useBybit ? Venue(DerivativesData.Bybit, at) : null;
        if (_view == DerivativesView.OpenInterestChange)
        {
            var starts = _venues.Select(v => Venue(v, start - TimeSpan.FromTicks(1))).ToArray();
            decimal? previous = starts.All(v => v.HasValue) && starts.Length > 0 ? starts.Sum(v => v!.Value) : null;
            combined = combined - previous;
            binance -= _useBinance ? Venue(DerivativesData.Binance, start - TimeSpan.FromTicks(1)) : null;
            bybit -= _useBybit ? Venue(DerivativesData.Bybit, start - TimeSpan.FromTicks(1)) : null;
        }
        _combined.Set(bar, combined ?? 0, combined.HasValue);
        _binance.Set(bar, binance ?? 0, binance.HasValue);
        _bybit.Set(bar, bybit ?? 0, bybit.HasValue);
        if (combined is { } c) _combined.Colors[bar] = c >= 0 ? Color.FromArgb(220, 38, 194, 129) : Color.FromArgb(220, 240, 82, 90);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var region = Container?.Region ?? Rectangle.Empty;
        string? message = CurrentMessage;
        if (string.IsNullOrEmpty(message) || region.Width <= 16 || region.Height < 40) return;
        context.DrawString(message, _statusFont, Color.FromArgb(200, 180, 186, 196), new Rectangle(region.X + 8, region.Y + 20, region.Width - 16, 18));
    }
}
