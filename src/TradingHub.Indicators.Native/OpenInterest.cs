using System.ComponentModel;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>Timestamp-safe OI consumer; all transport and observation ownership belongs to InstrumentData.</summary>
[DisplayName("Open interest")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Server-observed open interest and signed change between adjacent known candle cutoffs. Gaps are unavailable, not zero; Binance Futures history is five-minute/latest-month only.")]
public sealed class OpenInterest : Indicator
{
    private readonly OptionalValueDataSeries _total = new("Total", "Open interest") { Color = Color.Orange, Width = 2 };
    private readonly OptionalValueDataSeries _change = new("Change", "Signed OI change") { VisualType = VisualMode.Histogram, Color = Color.SeaGreen };
    private readonly RenderFont _statusFont = new("Arial", 10);
    private readonly RenderStringFormat _statusFormat = new() { Trimming = StringTrimming.EllipsisCharacter };
    [Browsable(false)]
    public string Status { get; private set; } = "No open-interest observations are available.";

    public OpenInterest() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _total;
        DataSeries.Add(_change);
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var candle = GetCandle(bar);
        _total.Set(bar, candle.OI, candle.HasOpenInterest);
        _change.Set(bar, candle.OpenInterestChange, candle.HasOpenInterestChange);
        if (candle.HasOpenInterestChange)
            _change.Colors[bar] = candle.OpenInterestChange < 0 ? Color.IndianRed : Color.SeaGreen;
        if (bar == CurrentBar - 1)
            Status = candle.HasOpenInterest
                ? $"Observed {candle.OpenInterestTime:yyyy-MM-dd HH:mm:ss} UTC · {candle.OpenInterestStatus}"
                : candle.OpenInterestStatus ?? "No open-interest observation at this candle cutoff.";
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var region = Container?.Region ?? Rectangle.Empty;
        if (region.Width <= 16 || region.Height < 48) return;
        context.DrawString(Status, _statusFont, Color.LightGray,
            new Rectangle(region.X + 8, region.Y + 24, region.Width - 16, 20), _statusFormat);
    }
}
