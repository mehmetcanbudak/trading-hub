// Behavior adapted from the KVP capability reference (commit b163ae8); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Data;
using TradingHub.Feeds.Derivatives;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>Resting book between two mid-relative distances, from a bar's latest usable recorded snapshot.</summary>
internal static class RecordedBookBands
{
    public static readonly decimal[] Steps = { 0, 1, 2.5m, 5, 10 };

    public static DepthSnapshot? Latest(IReadOnlyList<DepthSample> samples)
    {
        for (int i = samples.Count - 1; i >= 0; i--)
            if (samples[i].IsUsable) return samples[i].Snapshot;
        return null;
    }

    /// <summary>Bid and ask size between <paramref name="fromPercent"/> (exclusive, 0 = from the mid) and <paramref name="toPercent"/> of the mid.</summary>
    public static (decimal Bid, decimal Ask)? Between(DepthSnapshot book, decimal fromPercent, decimal toPercent)
    {
        var far = DepthBands.WithinPercent(book, toPercent);
        if (!far.Available) return null;
        var near = fromPercent > 0 ? DepthBands.WithinPercent(book, fromPercent) : (Bid: 0m, Ask: 0m, Available: true);
        return (far.Bid - near.Bid, far.Ask - near.Ask);
    }
}

/// <summary>
/// Liquidity imbalance oscillator: (bid − ask) / (bid + ask) of the recorded resting book between two distances from the
/// mid, from −100 to 100, smoothed over the bars that have a recorded book. Extremes past the thresholds are highlighted.
/// Bars without a usable recorded snapshot are gaps.
/// </summary>
[DisplayName("Liquidity imbalance oscillator")]
[Category(IndicatorCategories.OrderBook)]
[Description("Bid versus ask resting liquidity of the recorded book between two distances from the mid, from −100 to 100, smoothed, with highlighted extremes. Requires depth recording; bars without a recorded book stay gaps.")]
public sealed class LiquidityImbalanceOscillator : Indicator
{
    public enum PlotStyle
    {
        [Display(Name = "Line")] Line,
        [Display(Name = "Bars")] Bars,
        [Display(Name = "Bars by strength")] StrengthBars,
    }

    public enum DepthFrom
    {
        [Display(Name = "0 %")] P0,
        [Display(Name = "1 %")] P1,
        [Display(Name = "2.5 %")] P2_5,
        [Display(Name = "5 %")] P5,
    }

    public enum DepthTo
    {
        [Display(Name = "1 %")] P1 = 1,
        [Display(Name = "2.5 %")] P2_5,
        [Display(Name = "5 %")] P5,
        [Display(Name = "10 %")] P10,
    }

    private readonly OptionalValueDataSeries _oscillator = new("LIO", "Imbalance") { Width = 2, ShowZeroValue = true };
    private readonly List<decimal?> _raw = new();
    private DepthFrom _from = DepthFrom.P0;
    private DepthTo _to = DepthTo.P2_5;
    private int _smooth = 3;
    private PlotStyle _style = PlotStyle.Bars;
    private decimal _bidThreshold = 40, _askThreshold = 40;

    public LiquidityImbalanceOscillator() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _oscillator;
        LineSeries.Add(new LineSeries("Mid", "0") { Value = 0, Color = Color.FromArgb(110, 140, 140, 150), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("Up", "+") { Value = 40, Color = Color.FromArgb(70, 100, 255, 218), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("Dn", "−") { Value = -40, Color = Color.FromArgb(70, 244, 67, 54), LineDashStyle = LineDashStyle.Dot });
    }

    [Display(Name = "From", GroupName = "Calculation", Order = 10)] public DepthFrom From { get => _from; set { _from = value; RecalculateValues(); } }
    [Display(Name = "To", GroupName = "Calculation", Order = 11)] public DepthTo To { get => _to; set { _to = value; RecalculateValues(); } }
    [Display(Name = "Smoothing (bars)", GroupName = "Calculation", Order = 12)] [Range(1, 100)] public int Smooth { get => _smooth; set { _smooth = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Style", GroupName = "Visualization", Order = 20)] public PlotStyle Style { get => _style; set { _style = value; RecalculateValues(); } }
    [Display(Name = "Bid threshold", GroupName = "Visualization", Order = 21)] [Range(1, 100)] public decimal BidThreshold { get => _bidThreshold; set { _bidThreshold = value; LineSeries[1].Value = value; RecalculateValues(); } }
    [Display(Name = "Ask threshold", GroupName = "Visualization", Order = 22)] [Range(1, 100)] public decimal AskThreshold { get => _askThreshold; set { _askThreshold = value; LineSeries[2].Value = -value; RecalculateValues(); } }
    [Display(Name = "Highlight extremes", GroupName = "Visualization", Order = 23)] public bool Highlight { get; set; } = true;
    [Display(Name = "Bids", GroupName = "Colors", Order = 30)] public Color BidColor { get; set; } = Color.FromArgb(255, 100, 255, 218);
    [Display(Name = "Asks", GroupName = "Colors", Order = 31)] public Color AskColor { get; set; } = Color.FromArgb(255, 244, 67, 54);
    [Display(Name = "Extremes", GroupName = "Colors", Order = 32)] public Color ExtremeColor { get; set; } = Color.FromArgb(255, 255, 235, 59);

    protected override void OnRecalculate()
    {
        _raw.Clear();
        _oscillator.VisualType = _style == PlotStyle.Line ? VisualMode.Line : VisualMode.Histogram;
    }

    protected override void OnDepthHistoryChanged() => RecalculateValues();

    /// <summary>Imbalance in percent of one book between the configured distances, or null when not measurable.</summary>
    public static decimal? Imbalance(DepthSnapshot book, decimal fromPercent, decimal toPercent) =>
        RecordedBookBands.Between(book, fromPercent, toPercent) is { } d && d.Bid + d.Ask > 0 ? (d.Bid - d.Ask) / (d.Bid + d.Ask) * 100 : null;

    protected override void OnCalculate(int bar, decimal value)
    {
        while (_raw.Count <= bar) _raw.Add(null);
        decimal from = RecordedBookBands.Steps[(int)_from], to = RecordedBookBands.Steps[Math.Max((int)_to, (int)_from + 1)];
        _raw[bar] = RecordedBookBands.Latest(GetDepthSamples(bar)) is { } book ? Imbalance(book, from, to) : null;
        if (_raw[bar] == null) { _oscillator.SetUnavailable(bar); return; }
        decimal sum = 0;
        int n = 0;
        for (int k = Math.Max(0, bar - _smooth + 1); k <= bar; k++)
            if (_raw[k] is { } r) { sum += r; n++; }
        decimal v = sum / n;
        _oscillator.Set(bar, v);
        bool extreme = Highlight && (v >= _bidThreshold || v <= -_askThreshold);
        var basec = v >= 0 ? BidColor : AskColor;
        _oscillator.Colors[bar] = extreme ? ExtremeColor : _style == PlotStyle.StrengthBars ? FlowFormat.Alpha(basec, (int)(60 + 195 * Math.Min(1, Math.Abs(v) / 100))) : basec;
    }
}

/// <summary>
/// Order book pressure around each bar's price: bands up to 1, 2.5, 5 and 10 % below (painted by bid weight) and above
/// (painted by ask weight), drawn only when a side passes its minimum share, from the bar's latest recorded snapshot.
/// Bars without a recorded book are left blank.
/// </summary>
[DisplayName("Order book pressure")]
[Category(IndicatorCategories.OrderBook)]
[Description("Around each bar's price, bands up to 1 %, 2.5 %, 5 % and 10 %: below painted by bid weight and above by ask weight when a side passes its minimum share, with highlighted extremes. Requires depth recording.")]
public sealed class OrderBookPressure : Indicator
{
    public OrderBookPressure() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical);
    }

    [Display(Name = "Bids: minimum share (%)", GroupName = "Calculation", Order = 10)] [Range(0, 100)] public decimal MinimumBidShare { get; set; } = 20;
    [Display(Name = "Asks: minimum share (%)", GroupName = "Calculation", Order = 11)] [Range(0, 100)] public decimal MinimumAskShare { get; set; } = 20;
    [Display(Name = "Intensity", GroupName = "Visualization", Order = 20)] [Range(0.1, 10)] public decimal Intensity { get; set; } = 1;
    [Display(Name = "Highlight extremes", GroupName = "Visualization", Order = 21)] public bool Highlight { get; set; } = true;
    [Display(Name = "Reference lines", GroupName = "Visualization", Order = 22)] public bool ShowReference { get; set; } = true;
    [Display(Name = "Bids", GroupName = "Colors", Order = 30)] public Color BidColor { get; set; } = Color.FromArgb(255, 165, 214, 167);
    [Display(Name = "Asks", GroupName = "Colors", Order = 31)] public Color AskColor { get; set; } = Color.FromArgb(255, 241, 98, 126);
    [Display(Name = "Extremes", GroupName = "Colors", Order = 32)] public Color ExtremeColor { get; set; } = Color.FromArgb(255, 255, 255, 0);

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    /// <summary>Per band (index 1..4): imbalance in [−1, 1] of the band, or null when not measurable.</summary>
    public static decimal?[] Pressure(DepthSnapshot book)
    {
        var result = new decimal?[RecordedBookBands.Steps.Length];
        for (int k = 1; k < RecordedBookBands.Steps.Length; k++)
            if (RecordedBookBands.Between(book, RecordedBookBands.Steps[k - 1], RecordedBookBands.Steps[k]) is { } d && d.Bid + d.Ask > 0)
                result[k] = (d.Bid - d.Ask) / (d.Bid + d.Ask);
        return result;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar == 0) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        int bw = Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth);
        for (int bar = first; bar <= last; bar++)
        {
            if (RecordedBookBands.Latest(GetDepthSamples(bar)) is not { } book) continue;
            decimal price = GetCandle(bar).Close;
            int x = ChartInfo.GetXByBar(bar);
            var pressure = Pressure(book);
            for (int k = 1; k < pressure.Length; k++)
            {
                if (pressure[k] is not { } imbalance) continue;
                decimal bidShare = (imbalance + 1) * 50, askShare = 100 - bidShare;
                int alpha = (int)Math.Clamp(Math.Abs(imbalance) * 200 * Intensity, 0, 160);
                bool extreme = Highlight && Math.Abs(imbalance) >= 0.6m;
                decimal near = RecordedBookBands.Steps[k - 1] / 100, far = RecordedBookBands.Steps[k] / 100;
                if (imbalance > 0 && bidShare >= MinimumBidShare) Band(x, price * (1 - far), price * (1 - near), extreme ? ExtremeColor : BidColor, alpha);
                if (imbalance < 0 && askShare >= MinimumAskShare) Band(x, price * (1 + near), price * (1 + far), extreme ? ExtremeColor : AskColor, alpha);
            }
        }
        if (!ShowReference) return;
        decimal p = GetCandle(CurrentBar - 1).Close;
        var pen = new RenderPen(Color.FromArgb(70, 150, 150, 160), 1, System.Drawing.Drawing2D.DashStyle.Dot);
        var r = ChartInfo.PriceChartContainer.Region;
        foreach (decimal s in RecordedBookBands.Steps.Skip(1))
            foreach (int sign in new[] { -1, 1 })
            {
                int y = ChartInfo.GetYByPrice(p * (1 + sign * s / 100), false);
                context.DrawLine(pen, r.Left, y, r.Right, y);
            }

        void Band(int x, decimal low, decimal high, Color color, int alpha)
        {
            int y0 = ChartInfo.GetYByPrice(high, false), y1 = ChartInfo.GetYByPrice(low, false);
            context.FillRectangle(FlowFormat.Alpha(color, alpha), new Rectangle(x, Math.Min(y0, y1), bw + 1, Math.Max(1, Math.Abs(y1 - y0))));
        }
    }
}

/// <summary>
/// Leverage reference bands: where a long (below) and a short (above) opened at each bar's price would be liquidated at
/// five chosen leverages, ignoring maintenance margin, fees and funding. A reference grid, not where positions really are.
/// </summary>
[DisplayName("Leverage liquidation bands (reference)")]
[Category(IndicatorCategories.Derivatives)]
[Description("From each bar's price, where a long (below) and a short (above) opened there would be liquidated at five leverages, ignoring maintenance margin and fees. A reference grid, not where positions are.")]
public sealed class LeverageLiquidationBands : Indicator
{
    private readonly int[] _leverage = { 5, 10, 20, 50, 100 };
    private readonly ValueDataSeries[] _long = new ValueDataSeries[5], _short = new ValueDataSeries[5];
    private bool _close = true, _longs = true, _shorts = true, _lines;

    public LeverageLiquidationBands() : base(true)
    {
        for (int k = 0; k < 5; k++)
        {
            _long[k] = new ValueDataSeries("L" + k, $"Longs {_leverage[k]}×") { Color = FlowFormat.Alpha(FlowFormat.Buy, 140), ShowZeroValue = false };
            _short[k] = new ValueDataSeries("S" + k, $"Shorts {_leverage[k]}×") { Color = FlowFormat.Alpha(FlowFormat.Sell, 140), ShowZeroValue = false };
            if (k == 0) DataSeries[0] = _long[k]; else DataSeries.Add(_long[k]);
            DataSeries.Add(_short[k]);
        }
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical);
    }

    [Display(Name = "From the close (otherwise HLC3)", GroupName = "Calculation", Order = 10)] public bool UseClose { get => _close; set { _close = value; RecalculateValues(); } }
    [Display(Name = "Leverage 1", GroupName = "Calculation", Order = 11)] [Range(1, 500)] public int Leverage1 { get => _leverage[0]; set { _leverage[0] = Math.Clamp(value, 1, 500); RecalculateValues(); } }
    [Display(Name = "Leverage 2", GroupName = "Calculation", Order = 12)] [Range(1, 500)] public int Leverage2 { get => _leverage[1]; set { _leverage[1] = Math.Clamp(value, 1, 500); RecalculateValues(); } }
    [Display(Name = "Leverage 3", GroupName = "Calculation", Order = 13)] [Range(1, 500)] public int Leverage3 { get => _leverage[2]; set { _leverage[2] = Math.Clamp(value, 1, 500); RecalculateValues(); } }
    [Display(Name = "Leverage 4", GroupName = "Calculation", Order = 14)] [Range(1, 500)] public int Leverage4 { get => _leverage[3]; set { _leverage[3] = Math.Clamp(value, 1, 500); RecalculateValues(); } }
    [Display(Name = "Leverage 5", GroupName = "Calculation", Order = 15)] [Range(1, 500)] public int Leverage5 { get => _leverage[4]; set { _leverage[4] = Math.Clamp(value, 1, 500); RecalculateValues(); } }
    [Display(Name = "Long bands (below)", GroupName = "Visualization", Order = 20)] public bool ShowLongs { get => _longs; set { _longs = value; RecalculateValues(); } }
    [Display(Name = "Short bands (above)", GroupName = "Visualization", Order = 21)] public bool ShowShorts { get => _shorts; set { _shorts = value; RecalculateValues(); } }
    [Display(Name = "Band lines", GroupName = "Visualization", Order = 22)] public bool ShowLines { get => _lines; set { _lines = value; RecalculateValues(); } }
    [Display(Name = "Fill", GroupName = "Visualization", Order = 23)] public bool Fill { get; set; } = true;
    [Display(Name = "Fill opacity (%)", GroupName = "Visualization", Order = 24)] [Range(0, 100)] public int FillOpacity { get; set; } = 10;

    /// <summary>Isolated-margin liquidation price without maintenance margin: entry × (1 ∓ 1/leverage).</summary>
    public static (decimal Long, decimal Short) Prices(decimal entry, int leverage)
    {
        decimal margin = 1m / Math.Max(1, leverage);
        return (entry * (1 - margin), entry * (1 + margin));
    }

    protected override void OnRecalculate()
    {
        for (int k = 0; k < 5; k++)
        {
            _long[k].Name = $"Longs {_leverage[k]}×";
            _short[k].Name = $"Shorts {_leverage[k]}×";
            _long[k].VisualType = _longs && _lines ? VisualMode.Line : VisualMode.Hide;
            _short[k].VisualType = _shorts && _lines ? VisualMode.Line : VisualMode.Hide;
        }
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        decimal entry = _close ? c.Close : (c.High + c.Low + c.Close) / 3;
        for (int k = 0; k < 5; k++)
        {
            var (l, s) = Prices(entry, _leverage[k]);
            _long[k][bar] = l;
            _short[k][bar] = s;
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (!Fill || ChartInfo == null) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        if (last <= first) return;
        var order = Enumerable.Range(0, 5).OrderBy(k => _leverage[k]).ToArray();
        for (int j = 1; j < 5; j++)
            foreach (var (series, on, color) in new[] { (_long, _longs, FlowFormat.Buy), (_short, _shorts, FlowFormat.Sell) })
            {
                if (!on) continue;
                var a = series[order[j - 1]];
                var b = series[order[j]];
                var top = new List<Point>();
                var bottom = new List<Point>();
                for (int i = first; i <= last; i++)
                {
                    int x = ChartInfo.GetXByBar(i, false);
                    top.Add(new Point(x, ChartInfo.GetYByPrice(a[i], false)));
                    bottom.Add(new Point(x, ChartInfo.GetYByPrice(b[i], false)));
                }
                bottom.Reverse();
                context.FillPolygon(FlowFormat.Alpha(color, (int)(255 * FillOpacity / 100.0 * (0.5 + j / 8.0))), top.Concat(bottom).ToArray());
            }
    }
}

/// <summary>
/// Estimated liquidation map: a model of where positions opened in the last N bars would be liquidated. Each bar's
/// open-interest increase (Binance + Bybit) is split between longs and shorts by the bar's measured buy/sell volume and
/// spread over a fixed leverage mix around the bar's typical price; levels price has since traded through are removed.
/// Bars without both OI cutoffs or a measured delta are skipped and counted. An estimate, not exchange data.
/// </summary>
[DisplayName("Liquidation map (estimate from open interest)")]
[Category(IndicatorCategories.Derivatives)]
[Description("A model of where recently opened positions would be liquidated, from open-interest increases split by measured buy/sell volume over a fixed leverage mix. Drawn as bars at the right edge with the largest clusters named. An estimate, not exchange data.")]
public sealed class EstimatedLiquidationMap : Indicator
{
    /// <summary>Leverage tiers and the share of new positions assumed at each.</summary>
    public static readonly (decimal Leverage, decimal Weight)[] Tiers = { (5, 0.1m), (10, 0.25m), (25, 0.3m), (50, 0.2m), (100, 0.15m) };

    /// <summary>One bar's input to the model; null delta or OI change means unknown.</summary>
    public readonly record struct Entry(decimal High, decimal Low, decimal Close, decimal Volume, decimal? Delta, decimal? OpenedInterest);

    /// <summary>The binned map: notional per row from <see cref="Low"/> in steps of <see cref="Step"/>, plus the bars used and skipped.</summary>
    public sealed record Map(decimal Low, decimal Step, decimal[] Rows, int Used, int Skipped);

    private readonly DerivativesContext _derivatives;
    private readonly RenderFont _font = new("Arial", 8), _bold = new("Arial", 8, FontStyle.Bold);
    private Map? _map;
    private decimal _price;
    // Inputs of the closed bars of the window, kept until the window, the bars or the open interest change.
    private readonly List<Entry> _entries = new();
    private (int From, int Closed, DateTime First, long Previous, long OpenInterest) _entriesKey = (-1, -1, default, -1, -1);

    public EstimatedLiquidationMap() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("LMAP", "Map") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _derivatives = new DerivativesContext(a => DoActionInGuiThread(a), RecalculateValues, openInterest: true, funding: false, liquidations: false);
    }

    [Display(Name = "Bars of opened positions", GroupName = "Calculation", Order = 10)] [Range(20, 5000)] public int Lookback { get; set; } = 672;
    [Display(Name = "Maintenance margin (%)", GroupName = "Calculation", Order = 11)] [Range(0, 5)] public decimal MaintenanceMargin { get; set; } = 0.5m;
    [Display(Name = "Profile range (% each side)", GroupName = "Calculation", Order = 12)] [Range(1, 50)] public decimal RangePercent { get; set; } = 12;
    [Display(Name = "Rows", GroupName = "Calculation", Order = 13)] [Range(20, 500)] public int RowCount { get; set; } = 160;
    [Display(Name = "Maximum width (px)", GroupName = "Visualization", Order = 21)] [Range(30, 600)] public int MaximumWidth { get; set; } = 220;

    [Browsable(false)]
    public DerivativesData? Service { get => _derivatives.Service; set => _derivatives.Service = value; }

    internal DerivativesContext Derivatives => _derivatives;

    [Browsable(false)]
    public Map? Current => _map;

    /// <summary>
    /// Builds the map at <paramref name="price"/>: rows smaller than 10 % of the largest are dropped. A long level counts only
    /// while every later bar stayed above it (and a short level below it). A leverage tier whose initial margin does not
    /// exceed the maintenance margin has no liquidation level away from entry and is skipped.
    /// </summary>
    public static Map Estimate(IReadOnlyList<Entry> bars, decimal price, decimal rangePercent, int rows, decimal maintenancePercent)
    {
        decimal range = price * rangePercent / 100, low = price - range, step = 2 * range / rows;
        var result = new decimal[rows];
        int used = 0, skipped = 0;
        int n = bars.Count;
        var minAfter = new decimal[n];
        var maxAfter = new decimal[n];
        decimal mn = decimal.MaxValue, mx = decimal.MinValue;
        for (int i = n - 1; i >= 0; i--)
        {
            minAfter[i] = mn;
            maxAfter[i] = mx;
            mn = Math.Min(mn, bars[i].Low);
            mx = Math.Max(mx, bars[i].High);
        }
        decimal mm = maintenancePercent / 100;
        for (int i = 0; i < n; i++)
        {
            var b = bars[i];
            if (b.OpenedInterest is not { } opened || b.Delta is not { } delta || b.Volume <= 0) { skipped++; continue; }
            used++;
            if (opened <= 0) continue;
            decimal buyShare = Math.Clamp((b.Volume + delta) / 2 / b.Volume, 0, 1);
            decimal entry = (b.High + b.Low + b.Close) / 3;
            foreach (var (leverage, weight) in Tiers)
            {
                if (mm >= 1 / leverage) continue;
                decimal longLiquidation = entry * (1 - 1 / leverage + mm), shortLiquidation = entry * (1 + 1 / leverage - mm);
                if (minAfter[i] > longLiquidation) Add(longLiquidation, opened * buyShare * weight * entry);
                if (maxAfter[i] < shortLiquidation) Add(shortLiquidation, opened * (1 - buyShare) * weight * entry);
            }
        }
        decimal max = result.Length > 0 ? result.Max() : 0;
        for (int k = 0; k < rows; k++)
            if (result[k] < max * 0.1m) result[k] = 0;
        return new Map(low, step, result, used, skipped);

        void Add(decimal at, decimal notional)
        {
            int k = (int)Math.Floor((at - low) / step);
            if (k >= 0 && k < rows) result[k] += notional;
        }
    }

    protected override void OnDispose() => _derivatives.Dispose();

    protected override void OnRecalculate()
    {
        _map = null;
        _entries.Clear();
        _entriesKey = (-1, -1, default, -1, -1);
    }

    private Entry EntryOf(int i)
    {
        var c = GetCandle(i);
        var end = i + 1 < CurrentBar ? GetCandle(i + 1).Time : DateTime.UtcNow;
        decimal? opened = _derivatives.OpenInterestAt(end - TimeSpan.FromTicks(1)) - _derivatives.OpenInterestAt(c.Time - TimeSpan.FromTicks(1));
        decimal? delta = StudyMath.TryDelta(c, false, out var d, out _) ? d : null;
        return new Entry(c.High, c.Low, c.Close, c.Volume, delta, opened);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar != CurrentBar - 1) return;
        int from = Math.Max(0, CurrentBar - Lookback);
        _derivatives.Ensure(DerivativesPane.DerivedSymbol(InstrumentInfo?.Instrument ?? "", InstrumentInfo?.BaseAsset ?? "", InstrumentInfo?.QuoteAsset ?? ""), GetCandle(from).Time);
        // Each live tick rebuilds only the forming bar's input; closed bars are rebuilt when a bar is added or new open
        // interest arrives.
        var key = (from, bar, GetCandle(from).Time, bar > 0 ? GetCandle(bar - 1).Revision : 0, _derivatives.OpenInterestVersion);
        if (key != _entriesKey)
        {
            _entries.Clear();
            for (int i = from; i < bar; i++) _entries.Add(EntryOf(i));
            _entriesKey = key;
        }
        _entries.Add(EntryOf(bar));
        _price = GetCandle(bar).Close;
        _map = _derivatives.HasOpenInterest ? Estimate(_entries, _price, RangePercent, RowCount, MaintenanceMargin) : null;
        _entries.RemoveAt(_entries.Count - 1);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var region = ChartInfo.PriceChartContainer.Region;
        if (_map is not { } map)
        {
            FootprintStudyData.Legend(context, region, 0, _derivatives.Message ?? "Liquidation map (estimate): waiting for open interest.", FlowFormat.Dim);
            return;
        }
        decimal max = map.Rows.Length > 0 ? map.Rows.Max() : 0;
        int right = region.Right - 2;
        decimal up5 = 0, down5 = 0;
        for (int k = 0; k < map.Rows.Length; k++)
        {
            decimal price = map.Low + (k + 0.5m) * map.Step;
            if (price > _price && price - _price <= _price * 0.05m) up5 += map.Rows[k];
            if (price < _price && _price - price <= _price * 0.05m) down5 += map.Rows[k];
            if (map.Rows[k] <= 0 || max <= 0) continue;
            int y0 = ChartInfo.GetYByPrice(map.Low + (k + 1) * map.Step, false), y1 = ChartInfo.GetYByPrice(map.Low + k * map.Step, false);
            int w = (int)(MaximumWidth * map.Rows[k] / max);
            context.FillRectangle(FlowFormat.Alpha(price > _price ? FlowFormat.Buy : FlowFormat.Sell, 170), new Rectangle(right - w, Math.Min(y0, y1), Math.Max(1, w), Math.Max(1, Math.Abs(y1 - y0))));
        }
        int x = region.Left + 8, y = region.Bottom - 130;
        context.FillRectangle(Color.FromArgb(215, 18, 20, 26), new Rectangle(x, y, 290, 58), 5);
        context.DrawString("Liquidation map (estimate)", _bold, Color.FromArgb(235, 235, 240), x + 8, y + 4);
        context.DrawString(UiText.Tr($"Shorts within 5 %: {FlowFormat.Compact(up5)}"), _font, FlowFormat.Buy, x + 8, y + 19);
        context.DrawString(UiText.Tr($"Longs within 5 %: {FlowFormat.Compact(down5)}"), _font, FlowFormat.Sell, x + 8, y + 31);
        context.DrawString(UiText.Tr($"{map.Used} bars modelled, {map.Skipped} skipped (no open interest or measured delta)"), _font, FlowFormat.Dim, x + 8, y + 43);
    }
}

/// <summary>
/// Liquidation bursts: observed long and short liquidation notional per bar (stacked) from the live Binance and Bybit
/// streams, with a bubble where a bar jumps above the preceding bars' norm. Bars outside the streams' observed windows are
/// gaps; liquidations exist only since the chart started listening (Binance publishes a one-per-second sample).
/// </summary>
[DisplayName("Liquidation bursts")]
[Category(IndicatorCategories.Derivatives)]
[Description("Observed long and short liquidation notional per bar (Binance and Bybit live streams), stacked, with bubbles where a bar jumps above the norm. Only periods when the streams were observed are shown; earlier bars are gaps.")]
public sealed class LiquidationBursts : Indicator
{
    private readonly OptionalValueDataSeries _long = new("LL", "Longs liquidated") { VisualType = VisualMode.Histogram, Color = FlowFormat.Alpha(FlowFormat.Sell, 220), ShowZeroValue = true };
    private readonly OptionalValueDataSeries _total = new("SL", "Total liquidated") { VisualType = VisualMode.Histogram, Color = FlowFormat.Alpha(FlowFormat.Buy, 220), ShowZeroValue = true };
    private readonly DerivativesContext _derivatives;
    private readonly Action _tick;
    private readonly RenderFont _font = new("Arial", 7, FontStyle.Bold), _status = new("Arial", 8);
    private long _generation = -1;

    public LiquidationBursts() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _total;
        DataSeries.Add(_long);
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _derivatives = new DerivativesContext(a => DoActionInGuiThread(a), RecalculateValues, openInterest: false, funding: false, liquidations: true);
        _tick = () =>
        {
            if (_derivatives.LiquidationGeneration == _generation) return;
            _generation = _derivatives.LiquidationGeneration;
            RecalculateValues();
        };
    }

    [Display(Name = "Burst from (deviations)", GroupName = "Calculation", Order = 10)] [Range(0.5, 20)] public decimal BurstZ { get; set; } = 3;
    [Display(Name = "Window (bars)", GroupName = "Calculation", Order = 11)] [Range(12, 2000)] public int Window { get; set; } = 96;
    [Display(Name = "Bubble unit (notional)", GroupName = "Visualization", Order = 20)] [Range(1, 1_000_000_000_000)] public decimal BubbleUnit { get; set; } = 1_000_000;

    [Browsable(false)]
    public DerivativesData? Service { get => _derivatives.Service; set => _derivatives.Service = value; }

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(3), _tick);

    protected override void OnDispose()
    {
        UnsubscribeFromTimer(TimeSpan.FromSeconds(3), _tick);
        _derivatives.Dispose();
    }

    protected override void OnRecalculate()
    {
        if (CurrentBar > 0)
            _derivatives.Ensure(DerivativesPane.DerivedSymbol(InstrumentInfo?.Instrument ?? "", InstrumentInfo?.BaseAsset ?? "", InstrumentInfo?.QuoteAsset ?? ""), GetCandle(0).Time);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        var end = bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : DateTime.UtcNow;
        var q = _derivatives.Liquidations(c.Time, end, notional: true);
        _long.Set(bar, q?.Long ?? 0, q.HasValue);
        _total.Set(bar, q is { } x ? x.Long + x.Short : 0, q.HasValue);
    }

    /// <summary>Z-score of <paramref name="bar"/>'s total against the observed preceding bars of the window; null with fewer than twelve.</summary>
    public static double? BurstScore(Func<int, double?> total, int bar, int window) => StandOutRule.Sigmas(total, bar, window);

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        bool any = false;
        for (int b = Math.Max(0, CurrentBar - Window); b < CurrentBar && !any; b++) any = _total.IsAvailable(b);
        if (!any)
        {
            context.DrawString(_derivatives.Message ?? "Liquidations are recorded only while the chart listens; earlier bars are unknown.", _status, FlowFormat.Dim, box.Region.Left + 8, box.Region.Top + 16);
            return;
        }
        double? Total(int b) => _total.IsAvailable(b) ? (double)_total[b] : null;
        for (int bar = first; bar <= last; bar++)
        {
            if (Total(bar) is not { } total || BurstScore(Total, bar, Window) is not { } z || z < (double)BurstZ) continue;
            bool longsFlushed = (double)_long[bar] >= total / 2;
            int x = ChartInfo.GetXByBar(bar, false), y = box.GetYByValue(_total[bar]) - 8;
            int r = (int)Math.Clamp(5 + 3 * Math.Sqrt(total / (double)BubbleUnit), 5, 22);
            context.FillEllipse(FlowFormat.Alpha(longsFlushed ? FlowFormat.Sell : FlowFormat.Buy, 170), new Rectangle(x - r, y - r, 2 * r, 2 * r));
            context.DrawString(FlowFormat.Compact((decimal)total), _font, Color.White, x - r, y - 5);
        }
    }
}
