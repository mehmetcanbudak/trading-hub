using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class SparseFootprintRendererTests
{
    private static readonly DateTime Start = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SKColor Blue = new(0, 0, 255);
    private static readonly SKColor Poc = new(255, 0, 255);

    private static ChartModel Model(FootprintLayout layout)
    {
        var instrument = new Instrument { Symbol = "SPARSE", FeedId = "footprint-fixture", TickSize = 1, VolumeStep = 1 };
        var data = new InstrumentData(instrument, new SimulatedFeed());
        data.Trades.Add(new Trade(1, Start, -1_000_000_000, 5, TradeSide.Sell));
        data.Trades.Add(new Trade(2, Start.AddSeconds(1), 1_000_000_000, 10, TradeSide.Buy));
        data.Trades.Add(new Trade(3, Start.AddSeconds(2), 0, 2, TradeSide.None));
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings
        {
            Style = ChartStyle.Footprint, PriceStep = 1, Layout = layout, ShowGrid = false, ShowSessionLines = false,
            ShowInfoPanel = false, ShowLegendTitle = false, ShowLegendValues = false, ShowLastPriceLine = false, ShowTradeMarks = false,
            ShowValueArea = true,
        });
        model.Viewport.BarWidth = 120;
        model.Viewport.RightMarginPx = 0;
        model.Viewport.AutoScale = false;
        model.Theme.BackgroundTop = model.Theme.BackgroundBottom = SKColors.Black;
        model.Theme.ProfileBarStrong = model.Theme.Poc = Poc;
        model.Theme.ValueArea = SKColors.Yellow;
        return model;
    }

    private static (SKBitmap Bitmap, ChartLayout Layout) Render(ChartRenderer renderer, ChartModel model, int scale = 1)
    {
        var bitmap = new SKBitmap(400 * scale, 220 * scale);
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(scale);
        var layout = renderer.Render(canvas, model, 400, 220, new ChartRenderState { Crosshair = false });
        return (bitmap, layout);
    }

    private static bool IsBlue(SKColor color) => color.Blue > 220 && color.Red < 20 && color.Green < 20;
    private static bool IsBlueCell(SKColor color) => color.Blue > color.Red + 40 && color.Blue > color.Green + 40;
    private static bool IsPoc(SKColor color) => color.Blue > 220 && color.Red > 220 && color.Green < 20;
    private static bool IsArea(SKColor color) => color.Red > 80 && color.Green > 80 && color.Blue < 20;

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void Wide_sparse_source_keeps_negative_zero_positive_cells_and_does_not_paint_empty_price_pixels(bool proportional, int scale)
    {
        var layout = new FootprintLayout { ProportionPerCandle = proportional, ShowPoc = true, ShowImbalances = false };
        layout.Columns.Add(new FootprintColumn { Kind = FootprintColumnKind.Cells, Value = FootprintValue.Volume, ColorScheme = ClusterColorScheme.Solid, NeutralColor = (uint)Blue });
        layout.Columns.Add(new FootprintColumn { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Volume, Highlight = HighlightRule.Poc, NeutralColor = (uint)Blue });
        layout.Columns.Add(new FootprintColumn { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, NeutralColor = (uint)Blue });
        using var model = Model(layout);
        model.Viewport.PriceTop = 1_100_000_000;
        model.Viewport.PriceBottom = -1_100_000_000;
        var rendered = Render(new ChartRenderer(), model, scale);
        using var bitmap = rendered.Bitmap;
        var chart = rendered.Layout;
        float slotLeft = chart.LeftX(0) + chart.BarSpacing / 2f;
        int x = (int)((slotLeft + 10) * scale);
        foreach (decimal price in new[] { -1_000_000_000m, 0m, 1_000_000_000m })
        {
            int y = (int)(chart.Main.Y(price) * scale);
            Assert.Contains(Enumerable.Range(-1, 3), offset => IsBlueCell(bitmap.GetPixel(x, y + offset)) || IsPoc(bitmap.GetPixel(x, y + offset)));
        }
        var empty = bitmap.GetPixel(x, (int)(chart.Main.Y(-500_000_000m) * scale));
        Assert.False(IsBlueCell(empty) || IsPoc(empty));
        Assert.Equal(1, model.Series.PriceStep * chart.FootprintRowFactor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clipping_retains_offscreen_maximum_POC_and_full_source_value_area_without_promoting_visible_rows(bool proportional)
    {
        var layout = new FootprintLayout { ProportionPerCandle = proportional, ShowPoc = true, ShowImbalances = false };
        layout.Columns.Add(new FootprintColumn { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, NeutralColor = (uint)Blue, ShowNumbers = false });
        using var model = Model(layout);
        var renderer = new ChartRenderer();
        model.Viewport.PriceTop = 5;
        model.Viewport.PriceBottom = -5;
        var zero = Render(renderer, model);
        using (zero.Bitmap)
        {
            float left = zero.Layout.LeftX(0) + zero.Layout.BarSpacing / 2f;
            float width = (float)(zero.Layout.BarWidth - zero.Layout.BarSpacing);
            int y = (int)zero.Layout.Main.Y(0m);
            Assert.True(IsBlue(zero.Bitmap.GetPixel((int)(left + 5), y)));
            Assert.False(IsBlue(zero.Bitmap.GetPixel((int)(left + width * 0.5f), y)));
            int areaX = (int)(left + width - 2);
            Assert.True(IsArea(zero.Bitmap.GetPixel(areaX, (int)zero.Layout.Main.Y(2m))));
            Assert.False(IsArea(zero.Bitmap.GetPixel(areaX, (int)zero.Layout.Main.Y(-2m))));
        }
        model.Viewport.PriceTop = -999_999_995;
        model.Viewport.PriceBottom = -1_000_000_005;
        var negative = Render(renderer, model);
        using (negative.Bitmap)
        {
            float left = negative.Layout.LeftX(0) + negative.Layout.BarSpacing / 2f;
            float width = (float)(negative.Layout.BarWidth - negative.Layout.BarSpacing);
            int y = (int)negative.Layout.Main.Y(-1_000_000_000m);
            Assert.True(IsBlue(negative.Bitmap.GetPixel((int)(left + width * 0.3f), y)));
            Assert.False(IsBlue(negative.Bitmap.GetPixel((int)(left + width * 0.8f), y)));
        }
        model.Viewport.PriceTop = 1_000_000_005;
        model.Viewport.PriceBottom = 999_999_995;
        var positive = Render(renderer, model);
        using (positive.Bitmap)
        {
            float left = positive.Layout.LeftX(0) + positive.Layout.BarSpacing / 2f;
            float width = (float)(positive.Layout.BarWidth - positive.Layout.BarSpacing);
            Assert.True(IsPoc(positive.Bitmap.GetPixel((int)(left + width * 0.75f), (int)positive.Layout.Main.Y(1_000_000_000m))));
        }
    }

    [Fact]
    public void Subpixel_decimation_keeps_a_real_peak_row_instead_of_summing_different_price_levels()
    {
        var layout = new FootprintLayout { ProportionPerCandle = true, ShowPoc = true, ShowImbalances = false };
        layout.Columns.Add(new FootprintColumn { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, NeutralColor = (uint)Blue, ShowNumbers = false });
        using var model = Model(layout);
        model.Data.Trades.Update(1, new Trade(2, Start.AddSeconds(1), 1_000_000_000, 200, TradeSide.Buy));
        model.Data.Trades.Update(2, new Trade(3, Start.AddSeconds(2), 0, 100, TradeSide.None));
        for (int row = 1; row <= 1000; row++)
            model.Data.Trades.Add(new Trade(row + 3, Start.AddSeconds(3).AddTicks(row), row, 1, TradeSide.Buy));
        model.Rebuild();
        model.Viewport.PriceTop = 1_105_000_000;
        model.Viewport.PriceBottom = -1_095_000_000;
        var rendered = Render(new ChartRenderer(), model);
        using var bitmap = rendered.Bitmap;
        float left = rendered.Layout.LeftX(0) + rendered.Layout.BarSpacing / 2f;
        float width = (float)(rendered.Layout.BarWidth - rendered.Layout.BarSpacing);
        int y = (int)rendered.Layout.Main.Y(0m);
        Assert.True(IsBlue(bitmap.GetPixel((int)(left + width * 0.3f), y)));
        Assert.False(IsBlue(bitmap.GetPixel((int)(left + width * 0.8f), y)));
        Assert.Equal(1, model.Series.PriceStep * rendered.Layout.FootprintRowFactor);
    }
}
