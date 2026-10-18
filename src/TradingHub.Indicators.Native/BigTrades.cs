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
    private readonly Queue<decimal> _recent = new();
    private decimal _adaptiveThreshold;
    private int _sinceRecalc;
    private bool _requested;
    private CumulativeTrade _liveTrade;
    private Bubble _liveBubble;

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

    // ------------------------------------------------------------------ cálculo

    protected override void OnRecalculate()
    {
        _bubbles.Clear();
        _recent.Clear();
        _adaptiveThreshold = 0;
        _requested = false;
        _liveTrade = null;
        _liveBubble = null;
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
        var list = cumulativeTrades as IList<CumulativeTrade> ?? cumulativeTrades.ToList();
        _bubbles.Clear();
        _recent.Clear();

        // umbral adaptativo inicial: percentil de toda la historia cargada
        if (_mode == ThresholdMode.Adaptive && list.Count > 0)
        {
            var vols = list.Select(t => t.Volume).ToArray();
            Array.Sort(vols);
            _adaptiveThreshold = PercentileOf(vols, _percentile);
            foreach (var v in list.Skip(Math.Max(0, list.Count - _sample))) _recent.Enqueue(v.Volume);
        }

        int bar = 0;
        foreach (var t in list)
        {
            bar = FindBar(t.Time, bar);
            if (t.Volume >= CurrentThreshold && t.Volume > 0) _bubbles.Add(MakeBubble(t, bar));
        }

        RedrawChart();
    }

    protected override void OnCumulativeTrade(CumulativeTrade trade)
    {
        TrackAdaptive(trade.Volume);
        _liveTrade = trade;
        _liveBubble = null;
        Consider(trade);
    }

    protected override void OnUpdateCumulativeTrade(CumulativeTrade trade)
    {
        if (!ReferenceEquals(trade, _liveTrade))
        {
            _liveTrade = trade;
            _liveBubble = null;
        }

        Consider(trade);
    }

    private void Consider(CumulativeTrade trade)
    {
        if (trade.Volume < CurrentThreshold || trade.Volume <= 0) return;
        int bar = FindBar(trade.Time, Math.Max(0, CurrentBar - 5));
        if (_liveBubble == null)
        {
            _liveBubble = MakeBubble(trade, bar);
            _bubbles.Add(_liveBubble);
        }
        else
        {
            var b = MakeBubble(trade, bar);
            _liveBubble.Price = b.Price;
            _liveBubble.Volume = b.Volume;
            _liveBubble.Bar = b.Bar;
        }

        RedrawChart();
    }

    private Bubble MakeBubble(CumulativeTrade t, int bar)
    {
        decimal price = _anchor switch
        {
            PriceAnchor.Last => t.Lastprice,
            PriceAnchor.First => t.FirstPrice,
            _ => AveragePrice(t),
        };
        if (InstrumentInfo != null && InstrumentInfo.TickSize > 0)
            price = Math.Round(price / InstrumentInfo.TickSize) * InstrumentInfo.TickSize;
        return new Bubble { Bar = bar, Price = price, Volume = t.Volume, Buy = t.Direction == TradeDirection.Buy, Time = t.Time };
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

    private void TrackAdaptive(decimal volume)
    {
        if (_mode != ThresholdMode.Adaptive) return;
        _recent.Enqueue(volume);
        while (_recent.Count > _sample) _recent.Dequeue();
        if (++_sinceRecalc >= 250 || _adaptiveThreshold == 0)
        {
            _sinceRecalc = 0;
            var arr = _recent.ToArray();
            Array.Sort(arr);
            _adaptiveThreshold = PercentileOf(arr, _percentile);
        }
    }

    private static decimal PercentileOf(decimal[] sorted, decimal pct)
    {
        if (sorted.Length == 0) return 0;
        double pos = (double)pct / 100.0 * (sorted.Length - 1);
        int idx = Math.Clamp((int)Math.Round(pos), 0, sorted.Length - 1);
        return sorted[idx];
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
        if (ChartInfo == null || _bubbles.Count == 0) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        decimal thr = Math.Max(CurrentThreshold, 0.0000001m);
        var font = new RenderFont("Arial", 8, FontStyle.Bold);

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
            if (ShowBorder) context.DrawEllipse(new RenderPen(Color.FromArgb(220, col.R, col.G, col.B)), rect);
            if (ShowVolume && size >= 22)
            {
                string txt = VolumeFormat.Compact(b.Volume, ChartInfo.VolumeDecimals > 0 && b.Volume < 100 ? 1 : 0);
                context.DrawString(txt, font, Color.White, rect, new RenderStringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoClip });
            }
        }
    }
}
