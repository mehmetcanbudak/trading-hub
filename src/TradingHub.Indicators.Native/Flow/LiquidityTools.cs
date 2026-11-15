// Behavior adapted from the KVP capability reference (commit df8844a); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Liquidity tracker: resting notional on each side of the recorded book weighted by 2^(−d / half-life), with d the
/// distance in basis points from that side's best price (basis points so venues with different ticks agree). Each bar
/// averages its usable recorded snapshots; a bar without a usable recorded book is a gap. Resting orders, not executions.
/// </summary>
[DisplayName("Liquidity tracker (distance-weighted book)")]
[Category(IndicatorCategories.OrderBook)]
[Description("Resting bid and ask notional of the recorded book weighted by distance from the best price (half-life in basis points), as two lines or an imbalance. Requires depth recording; bars without a usable recorded book stay gaps.")]
public sealed class LiquidityTracker : Indicator
{
    public enum TrackerView
    {
        [Display(Name = "Bid and ask lines")] Lines,
        [Display(Name = "Imbalance (%)")] Imbalance,
    }

    private readonly OptionalValueDataSeries _bid = new("LTB", "Weighted bid") { Color = Color.FromArgb(255, 38, 194, 129), Width = 2 };
    private readonly OptionalValueDataSeries _ask = new("LTA", "Weighted ask") { Color = Color.FromArgb(255, 240, 82, 90), Width = 2 };
    private readonly OptionalValueDataSeries _imbalance = new("LTI", "Imbalance") { VisualType = VisualMode.Histogram, ShowZeroValue = true };
    private readonly RenderFont _font = new("Arial", 8);
    private TrackerView _view = TrackerView.Lines;
    private decimal _halfLife = 10, _min, _max;
    private bool _perLevel;
    private int _available;

    public LiquidityTracker() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _bid;
        DataSeries.Add(_ask);
        DataSeries.Add(_imbalance);
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "View", GroupName = "Visualization", Order = 10)]
    public TrackerView View { get => _view; set { _view = value; RecalculateValues(); } }

    [Display(Name = "Half-life (basis points)", GroupName = "Calculation", Order = 20, Description = "At this distance from the best price a level weighs one half; at twice the distance, one quarter.")]
    [Range(0.1, 10_000)]
    public decimal HalfLife { get => _halfLife; set { _halfLife = Math.Max(0.1m, value); RecalculateValues(); } }

    [Display(Name = "Only levels from (notional, 0 = all)", GroupName = "Calculation", Order = 30)]
    [Range(0, 1_000_000_000_000)]
    public decimal MinimumLevelNotional { get => _min; set { _min = Math.Max(0, value); RecalculateValues(); } }

    [Display(Name = "Only levels up to (notional, 0 = no limit)", GroupName = "Calculation", Order = 31)]
    [Range(0, 1_000_000_000_000)]
    public decimal MaximumLevelNotional { get => _max; set { _max = Math.Max(0, value); RecalculateValues(); } }

    [Display(Name = "Average per level (instead of the sum)", GroupName = "Calculation", Order = 40)]
    public bool PerLevelAverage { get => _perLevel; set { _perLevel = value; RecalculateValues(); } }

    /// <summary>Distance-weighted (bid, ask) notional of one snapshot; unavailable without a two-sided book.</summary>
    public static (decimal Bid, decimal Ask, bool Available) Measure(DepthSnapshot book, decimal tick, decimal halfLifeBasisPoints, decimal minimumNotional, decimal maximumNotional, bool perLevel, decimal pointValue = 1)
    {
        if (!book.HasBid || !book.HasAsk || tick <= 0 || halfLifeBasisPoints <= 0) return (0, 0, false);
        decimal bestBid = book.BestBidTicks * tick, bestAsk = book.BestAskTicks * tick;
        if (bestBid <= 0 || bestAsk <= 0) return (0, 0, false);
        double bid = 0, ask = 0, bidWeight = 0, askWeight = 0;
        foreach (var level in book.Bids)
        {
            decimal price = level.PriceTicks * tick, notional = level.Size * price * pointValue;
            if (notional < minimumNotional || maximumNotional > 0 && notional > maximumNotional) continue;
            double w = Math.Pow(2, -(double)((bestBid - price) / bestBid * 10_000 / halfLifeBasisPoints));
            bid += (double)notional * w;
            bidWeight += w;
        }
        foreach (var level in book.Asks)
        {
            decimal price = level.PriceTicks * tick, notional = level.Size * price * pointValue;
            if (notional < minimumNotional || maximumNotional > 0 && notional > maximumNotional) continue;
            double w = Math.Pow(2, -(double)((price - bestAsk) / bestAsk * 10_000 / halfLifeBasisPoints));
            ask += (double)notional * w;
            askWeight += w;
        }
        if (perLevel) { bid = bidWeight > 0 ? bid / bidWeight : 0; ask = askWeight > 0 ? ask / askWeight : 0; }
        return ((decimal)bid, (decimal)ask, true);
    }

    protected override void OnRecalculate()
    {
        bool lines = _view == TrackerView.Lines;
        _bid.VisualType = lines ? VisualMode.Line : VisualMode.Hide;
        _ask.VisualType = lines ? VisualMode.Line : VisualMode.Hide;
        _imbalance.VisualType = lines ? VisualMode.Hide : VisualMode.Histogram;
        _available = 0;
    }

    protected override void OnDepthHistoryChanged() => RecalculateValues();

    protected override void OnCalculate(int bar, decimal value)
    {
        decimal tick = BookChartTools.Tick(this);
        decimal bid = 0, ask = 0;
        int n = 0;
        foreach (var sample in GetDepthSamples(bar))
        {
            if (!sample.IsUsable) continue;
            var m = Measure(sample.Snapshot, tick, _halfLife, _min, _max, _perLevel, FlowFormat.PointValue(this));
            if (!m.Available) continue;
            bid += m.Bid;
            ask += m.Ask;
            n++;
        }
        bool available = n > 0;
        if (available) { bid /= n; ask /= n; _available++; }
        decimal imbalance = available && bid + ask > 0 ? (bid - ask) / (bid + ask) * 100 : 0;
        _bid.Set(bar, bid, available);
        _ask.Set(bar, ask, available);
        _imbalance.Set(bar, imbalance, available);
        _imbalance.Colors[bar] = imbalance >= 0 ? Color.FromArgb(220, 38, 194, 129) : Color.FromArgb(220, 240, 82, 90);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (Container == null || _available > 0) return;
        context.DrawString("No recorded book: enable depth recording in Settings to fill this pane. Gaps are not zero liquidity.", _font, FlowFormat.Dim, Container.Region.Left + 8, Container.Region.Top + 18);
    }
}

/// <summary>
/// Book mirror: with the mouse over the chart, the band from the mid to the mouse and the equally wide band on the other
/// side are framed in each side's color with the resting notional in each (current book, or the latest recorded snapshot
/// when no live book is available) and which side holds more. Moving away shows how the balance changes with distance.
/// </summary>
[DisplayName("Book mirror (equal-distance comparison)")]
[Category(IndicatorCategories.OrderBook)]
[Description("On hover, compares resting book notional between the mid price and the mouse with the equally wide band on the other side. Uses the live book, or the latest recorded snapshot.")]
public sealed class BookMirror : Indicator
{
    private readonly RenderFont _font = new("Arial", 8), _bold = new("Arial", 8, FontStyle.Bold);

    public BookMirror() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("MIR", "Mirror") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Frame width (px from the right)", GroupName = "Visualization", Order = 10)]
    [Range(60, 800)]
    public int FrameWidth { get; set; } = 180;

    [Display(Name = "Bid color", GroupName = "Colors", Order = 20)]
    public Color BidColor { get; set; } = Color.FromArgb(255, 38, 194, 129);

    [Display(Name = "Ask color", GroupName = "Colors", Order = 21)]
    public Color AskColor { get; set; } = Color.FromArgb(255, 240, 82, 90);

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    /// <summary>Size and notional of the levels between two prices (inclusive).</summary>
    public static (decimal Size, decimal Notional) Sum(IEnumerable<(decimal Price, decimal Size)> levels, decimal low, decimal high, decimal pointValue = 1)
    {
        decimal size = 0, notional = 0;
        foreach (var (price, s) in levels)
        {
            if (price < low || price > high) continue;
            size += s;
            notional += s * price * pointValue;
        }
        return (size, notional);
    }

    /// <summary>The live book if usable, else the latest usable recorded snapshot of the last bar.</summary>
    private DepthSnapshot? Book()
    {
        if (MarketDepthInfo.Current is { IsUsable: true } live) return live.Snapshot;
        if (CurrentBar <= 0) return null;
        var samples = GetDepthSamples(CurrentBar - 1);
        for (int i = samples.Count - 1; i >= 0; i--)
            if (samples[i].IsUsable) return samples[i].Snapshot;
        return null;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var mouse = MouseLocationInfo;
        if (mouse.IsMouseLeave) return;
        var region = ChartInfo.PriceChartContainer.Region;
        if (!region.Contains(mouse.LastPosition)) return;
        if (Book() is not { } book)
        {
            context.DrawString("Book mirror: no live or recorded book.", _font, FlowFormat.Dim, region.Left + 8, region.Bottom - 14);
            return;
        }
        decimal tick = BookChartTools.Tick(this);
        decimal mid = (book.BestBidTicks + book.BestAskTicks) * tick / 2;
        decimal distance = Math.Abs(mouse.PriceBelowMouse - mid);
        if (distance <= 0 || mid <= 0) return;
        bool mouseAbove = mouse.PriceBelowMouse > mid;
        var (askSize, askNotional) = Sum(book.Asks.Select(l => (l.PriceTicks * tick, l.Size)), mid, mid + distance, FlowFormat.PointValue(this));
        var (bidSize, bidNotional) = Sum(book.Bids.Select(l => (l.PriceTicks * tick, l.Size)), mid - distance, mid, FlowFormat.PointValue(this));
        int x1 = region.Right - 2, x0 = Math.Max(region.Left, x1 - Math.Clamp(FrameWidth, 60, 800));
        int yMid = ChartInfo.GetYByPrice(mid, false), yUp = ChartInfo.GetYByPrice(mid + distance, false), yDown = ChartInfo.GetYByPrice(mid - distance, false);
        var up = new Rectangle(x0, Math.Min(yUp, yMid), x1 - x0, Math.Abs(yMid - yUp));
        var down = new Rectangle(x0, Math.Min(yDown, yMid), x1 - x0, Math.Abs(yDown - yMid));
        context.FillRectangle(FlowFormat.Alpha(AskColor, 30), up);
        context.FillRectangle(FlowFormat.Alpha(BidColor, 30), down);
        context.DrawRectangle(new RenderPen(AskColor), up);
        context.DrawRectangle(new RenderPen(BidColor), down);
        context.DrawString($"asks {FlowFormat.Compact(askNotional)} · {FlowFormat.Compact(askSize)}", _bold, AskColor, x0 + 4, up.Top + 2);
        context.DrawString($"bids {FlowFormat.Compact(bidNotional)} · {FlowFormat.Compact(bidSize)}", _bold, BidColor, x0 + 4, down.Bottom - 13);
        decimal basisPoints = distance / mid * 10_000;
        string span = basisPoints >= 100 ? (basisPoints / 100).ToString("0.##", CultureInfo.InvariantCulture) + " %" : basisPoints.ToString("0.#", CultureInfo.InvariantCulture) + " bp";
        decimal near = mouseAbove ? askNotional : bidNotional, far = mouseAbove ? bidNotional : askNotional;
        string ratio = far >= near ? near > 0 ? (far / near).ToString("0.00", CultureInfo.InvariantCulture) : "∞" : far > 0 ? (near / far).ToString("0.00", CultureInfo.InvariantCulture) : "∞";
        string verdict = near <= 0 && far <= 0 ? "" : far >= near ? UiText.Tr($"the other side holds {ratio}× more") : UiText.Tr($"this side holds {ratio}× more");
        string text = "± " + span + (verdict.Length > 0 ? " · " + verdict : "");
        var size = context.MeasureString(text, _font);
        context.FillRectangle(FlowFormat.Back, new Rectangle(x0, yMid - 7, size.Width + 8, 14));
        context.DrawString(text, _font, Color.FromArgb(235, 235, 240), x0 + 4, yMid - 6);
    }
}
