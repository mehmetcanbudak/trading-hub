using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Attributes;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Big Trades: marca con burbujas las órdenes agresivas grandes (todas las ejecuciones de una misma
/// orden a mercado se suman, aunque haya barrido varios niveles). Azul = compra, rojo = venta.
/// El umbral puede ser fijo o adaptativo (percentil de las operaciones recientes).
/// </summary>
[DisplayName("Big Trades")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class BigTrades : Indicator
{
    public enum ThresholdMode
    {
        [Display(Name = "Fixed")] Fixed,
        [Display(Name = "Adaptive (percentile)")] Adaptive,
    }

    public enum PriceAnchor
    {
        [Display(Name = "Order average price")] Average,
        [Display(Name = "Order last price")] Last,
        [Display(Name = "Order first price")] First,
    }

    private sealed class Bubble
    {
        public int Bar;
        public decimal Price;
        public decimal Volume;
        public bool Buy;
        public DateTime Time;
    }

    private readonly List<Bubble> _bubbles = new();
    // Completed eligible groups stay immutable; the mutable open group is never committed provisionally.
    private readonly Queue<(decimal Volume, long Order)> _recent = new();
    private readonly SortedSet<(decimal Volume, long Order)> _lowerSamples = new(), _upperSamples = new();
    private (decimal Volume, long Order)? _excludedOldest;
    private long _sampleOrder;
    private decimal _adaptiveThreshold;
    private bool _requested;
    private CumulativeTrade _liveTrade;
    private Bubble _liveBubble;
    private readonly RenderPen _borderPen = new(Color.SteelBlue);

    private ThresholdMode _mode = ThresholdMode.Adaptive;
    private decimal _minVolume = 5;
    private decimal _percentile = 99.7m;
    private int _sample = 5000;
    private PriceAnchor _anchor = PriceAnchor.Average;

    public BigTrades() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        DrawAbovePrice = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    // ------------------------------------------------------------------ parámetros

    [Display(Name = "Threshold type", GroupName = "Filter", Order = 10)]
    public ThresholdMode Mode
    {
        get => _mode;
        set { _mode = value; RecalculateValues(); }
    }

    [Display(Name = "Minimum volume (fixed)", GroupName = "Filter", Order = 20,
        Description = "Minimum order size to display when using a fixed threshold.")]
    [Range(0, 100000000)]
    public decimal MinVolume
    {
        get => _minVolume;
        set { _minVolume = Math.Max(0, value); RecalculateValues(); }
    }

    [Display(Name = "Percentile (adaptive)", GroupName = "Filter", Order = 30,
        Description = "Only highlight orders above this percentile of recent sizes (99.5 = largest 0.5%).")]
    [Range(50, 99.999)]
    public decimal Percentile
    {
        get => _percentile;
        set { _percentile = Math.Clamp(value, 50, 99.999m); RecalculateValues(); }
    }

    [Display(Name = "Sample (recent orders)", GroupName = "Filter", Order = 40)]
    [Range(100, 200000)]
    public int Sample
    {
        get => _sample;
        set { _sample = Math.Clamp(value, 100, 200000); RecalculateValues(); }
    }

    [Display(Name = "Price placement", GroupName = "Filter", Order = 50)]
    public PriceAnchor Anchor
    {
        get => _anchor;
        set { _anchor = value; RecalculateValues(); }
    }

    [Display(Name = "Buy color", GroupName = "Visualization", Order = 100)]
    public Color BuyColor { get; set; } = Color.FromArgb(150, 50, 140, 230);

    [Display(Name = "Sell color", GroupName = "Visualization", Order = 110)]
    public Color SellColor { get; set; } = Color.FromArgb(150, 232, 70, 70);

    [Display(Name = "Minimum size (px)", GroupName = "Visualization", Order = 120)]
    [Range(2, 100)]
    public int MinSize { get; set; } = 10;

    [Display(Name = "Maximum size (px)", GroupName = "Visualization", Order = 130)]
    [Range(4, 300)]
    public int MaxSize { get; set; } = 60;

    [Display(Name = "Show volume", GroupName = "Visualization", Order = 140)]
    public bool ShowVolume { get; set; } = true;

    [Display(Name = "Border", GroupName = "Visualization", Order = 150)]
    public bool ShowBorder { get; set; } = true;

    /// <summary>Umbral actual (para mostrar en la UI).</summary>
    [Browsable(false)]
    public decimal CurrentThreshold => _mode == ThresholdMode.Fixed ? _minVolume : _adaptiveThreshold;
    [Browsable(false)] public int BubbleCount => _bubbles.Count;
    [Browsable(false)] public string Status => "Observed raw-print grouping only; compact packets, inferred/unknown sides and synthetic candles excluded. Grouping is not exchange-order completeness.";

    // ------------------------------------------------------------------ cálculo

    protected override void OnRecalculate()
    {
        _requested = false;
        ResetCumulativeState();
    }

    private void ResetCumulativeState()
    {
        _bubbles.Clear(); _recent.Clear(); _lowerSamples.Clear(); _upperSamples.Clear();
        _excludedOldest = null; _sampleOrder = 0; _adaptiveThreshold = 0;
        _liveTrade = null; _liveBubble = null;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0 && !_requested && CurrentBar > 0)
        {
            _requested = true;
            RequestForCumulativeTrades(new CumulativeTradesRequest(GetCandle(0).Time));
        }
    }

    protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> cumulativeTrades)
    {
        // Replay the same causal rolling rule as live updates, retaining the exact final open-group identity.
        ResetCumulativeState();
        int bar = 0;
        foreach (var trade in cumulativeTrades)
        {
            SelectOpenTrade(trade);
            bar = FindBar(trade.Time, bar);
            UpdateOpenTrade(trade, bar);
        }
        RedrawChart();
    }

    protected override void OnCumulativeTrade(CumulativeTrade trade)
    {
        SelectOpenTrade(trade);
        UpdateOpenTrade(trade, FindBar(trade.Time, Math.Max(0, CurrentBar - 5)));
        RedrawChart();
    }

    protected override void OnUpdateCumulativeTrade(CumulativeTrade trade)
    {
        SelectOpenTrade(trade);
        UpdateOpenTrade(trade, FindBar(trade.Time, Math.Max(0, CurrentBar - 5)));
        RedrawChart();
    }

    private void SelectOpenTrade(CumulativeTrade trade)
    {
        if (ReferenceEquals(trade, _liveTrade)) return;
        if (_liveTrade != null && IsEligible(_liveTrade)) CommitAdaptive(_liveTrade.Volume);
        _liveTrade = trade; _liveBubble = null;
    }

    private void UpdateOpenTrade(CumulativeTrade trade, int bar)
    {
        bool eligible = IsEligible(trade);
        RefreshAdaptive(eligible);
        if (!eligible || trade.Volume < CurrentThreshold || trade.Volume <= 0)
        {
            if (_liveBubble != null) _bubbles.Remove(_liveBubble);
            _liveBubble = null; return;
        }
        if (_liveBubble == null) { _liveBubble = MakeBubble(trade, bar); _bubbles.Add(_liveBubble); }
        else { _liveBubble.Price = BubblePrice(trade); _liveBubble.Volume = trade.Volume; _liveBubble.Bar = bar; }
    }
    private static bool IsEligible(CumulativeTrade trade)
    {
        if (trade.Ticks.Count == 0 || trade.Direction is not (TradeDirection.Buy or TradeDirection.Sell)) return false;
        foreach (var tick in trade.Ticks)
            if (!tick.IsObservedTrade || (tick.Quality & TradingHub.Market.DataQuality.InferredSide) != 0) return false;
        return true;
    }

    private Bubble MakeBubble(CumulativeTrade trade, int bar) => new()
    { Bar = bar, Price = BubblePrice(trade), Volume = trade.Volume, Buy = trade.Direction == TradeDirection.Buy, Time = trade.Time };

    private decimal BubblePrice(CumulativeTrade trade)
    {
        decimal price = _anchor switch
        {
            PriceAnchor.Last => trade.Lastprice,
            PriceAnchor.First => trade.FirstPrice,
            _ => AveragePrice(trade),
        };
        return InstrumentInfo != null && InstrumentInfo.TickSize > 0 ? Math.Round(price / InstrumentInfo.TickSize) * InstrumentInfo.TickSize : price;
    }

    private static decimal AveragePrice(CumulativeTrade t)
    {
        if (t.Ticks == null || t.Ticks.Count == 0) return t.Lastprice;
        decimal pv = 0, v = 0;
        foreach (var tk in t.Ticks)
        {
            pv += tk.Price * tk.Volume;
            v += tk.Volume;
        }

        return v > 0 ? pv / v : t.Lastprice;
    }

    private void CommitAdaptive(decimal volume)
    {
        if (_mode != ThresholdMode.Adaptive) return;
        if (_recent.Count == _sample)
        {
            var oldest = _recent.Dequeue();
            if (_excludedOldest == oldest) _excludedOldest = null; else RemoveSample(oldest);
        }
        var sample = (Volume: volume, Order: ++_sampleOrder);
        _recent.Enqueue(sample); InsertSample(sample);
    }

    private void InsertSample((decimal Volume, long Order) sample)
    {
        if (_lowerSamples.Count > 0 && sample.CompareTo(_lowerSamples.Max) <= 0) _lowerSamples.Add(sample);
        else _upperSamples.Add(sample);
    }

    private void RemoveSample((decimal Volume, long Order) sample)
    { if (!_lowerSamples.Remove(sample)) _upperSamples.Remove(sample); }

    private int PercentileRank(int count) => count == 0 ? 0 : Math.Clamp((int)Math.Round((double)_percentile / 100 * (count - 1)), 0, count - 1);

    private void RefreshAdaptive(bool eligibleOpen)
    {
        if (_mode != ThresholdMode.Adaptive) return;
        // Keep one extra completed sample in the queue, so rejecting the open group restores the full window.
        if (eligibleOpen && _recent.Count == _sample && _excludedOldest == null)
        {
            _excludedOldest = _recent.Peek(); RemoveSample(_excludedOldest.Value);
        }
        else if (!eligibleOpen && _excludedOldest is { } excluded)
        {
            InsertSample(excluded); _excludedOldest = null;
        }
        int count = _lowerSamples.Count + _upperSamples.Count;
        // For an open group, partition exactly the first rank closed values; its insertion is a clamp.
        int lowerCount = eligibleOpen ? PercentileRank(count + 1) : count == 0 ? 0 : PercentileRank(count) + 1;
        while (_lowerSamples.Count > lowerCount)
        { var sample = _lowerSamples.Max; _lowerSamples.Remove(sample); _upperSamples.Add(sample); }
        while (_lowerSamples.Count < lowerCount)
        { var sample = _upperSamples.Min; _upperSamples.Remove(sample); _lowerSamples.Add(sample); }
        _adaptiveThreshold = eligibleOpen
            ? Math.Clamp(_liveTrade.Volume, _lowerSamples.Count == 0 ? decimal.MinValue : _lowerSamples.Max.Volume, _upperSamples.Count == 0 ? decimal.MaxValue : _upperSamples.Min.Volume)
            : count == 0 ? 0 : _lowerSamples.Max.Volume;
    }

    /// <summary>Barra que contiene un instante (búsqueda desde una pista).</summary>
    private int FindBar(DateTime time, int hint)
    {
        int n = CurrentBar;
        if (n == 0) return 0;
        int lo = 0, hi = n - 1;
        if (hint > 0 && hint < n && GetCandle(hint).Time <= time) lo = hint;
        int ans = lo;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (GetCandle(mid).Time <= time)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans;
    }

    // ------------------------------------------------------------------ dibujo

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, Status, Color.LightSkyBlue);
        if (_bubbles.Count == 0) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        decimal thr = Math.Max(CurrentThreshold, 0.0000001m);
        var font = FootprintStudyData.BoldFont;

        // las chicas primero para que las grandes queden arriba
        foreach (var b in _bubbles.Where(b => b.Bar >= first && b.Bar <= last).OrderBy(b => b.Volume))
        {
            int x = ChartInfo.GetXByBar(b.Bar, false);
            int y = ChartInfo.GetYByPrice(b.Price, false);
            double ratio = Math.Sqrt((double)(b.Volume / thr));
            int size = (int)Math.Clamp(MinSize * ratio, MinSize, Math.Max(MinSize, MaxSize));
            var rect = new Rectangle(x - size / 2, y - size / 2, size, size);
            var col = b.Buy ? BuyColor : SellColor;
            context.FillEllipse(col, rect);
            if (ShowBorder) { _borderPen.Color = Color.FromArgb(220, col.R, col.G, col.B); context.DrawEllipse(_borderPen, rect); }
            if (ShowVolume && size >= 22)
            {
                string txt = VolumeFormat.Compact(b.Volume, ChartInfo.VolumeDecimals > 0 && b.Volume < 100 ? 1 : 0);
                context.DrawString(txt, font, Color.White, rect, new RenderStringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoClip });
            }
        }
    }
}
