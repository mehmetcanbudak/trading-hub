using SkiaSharp;
using TradingHub.App.Views;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;

namespace TradingHub.Tests;

/// <summary>Wheel and keyboard zoom on one or both axes, and one chart context menu at a time.</summary>
public sealed class ChartZoomAndMenuTests
{
    private static (ChartModel Model, ChartController Controller, ChartLayout Layout, Func<ChartLayout> Render) Chart(bool bothAxes, double scrollBars = 40)
    {
        var model = SearchZoomFixture.Model(600);
        model.Settings.Style = ChartStyle.Candles;
        model.Settings.ZoomBothAxes = bothAxes;
        model.Viewport.BarWidth = 8;
        model.Viewport.ScrollBars = scrollBars;
        var state = new ChartRenderState();
        var renderer = new ChartRenderer();
        ChartLayout Render()
        {
            using var surface = SKSurface.Create(new SKImageInfo(1000, 600));
            return renderer.Render(surface.Canvas, model, 1000, 600, state);
        }

        var layout = Render();
        return (model, new ChartController(model, state), layout, Render);
    }

    [Fact]
    public void Default_wheel_zoom_changes_only_time_and_keeps_auto_scale()
    {
        var (m, c, l, render) = Chart(bothAxes: false);
        using var _ = m;
        float x = 400, y = l.Main.Rect.MidY;
        double barUnder = l.BarAtExact(x);
        c.Wheel(x, y, 0, 1, InputModifiers.None);
        Assert.Equal(8 * 1.12, m.Viewport.BarWidth, 9);
        Assert.True(m.Viewport.AutoScale);
        var after = render();
        Assert.Equal(barUnder, after.BarAtExact(x), 6); // the bar under the mouse stays put
    }

    [Fact]
    public void Both_axes_zoom_scales_price_by_the_applied_factor_around_the_mouse()
    {
        var (m, c, l, render) = Chart(bothAxes: true);
        using var _ = m;
        float x = 400, y = l.Main.Rect.Top + l.Main.Rect.Height * 0.3f;
        double priceUnder = l.Main.ValueAt(y), span = l.Main.Top - l.Main.Bottom, barUnder = l.BarAtExact(x);
        c.Wheel(x, y, 0, 1, InputModifiers.None);
        Assert.False(m.Viewport.AutoScale);
        Assert.Equal(span / 1.12, m.Viewport.PriceTop - m.Viewport.PriceBottom, 6);
        var after = render();
        Assert.Equal(priceUnder, after.Main.ValueAt(y), 6);
        Assert.Equal(barUnder, after.BarAtExact(x), 6);

        // zooming back out restores the same range
        c.Wheel(x, y, 0, -1, InputModifiers.None);
        Assert.Equal(span, m.Viewport.PriceTop - m.Viewport.PriceBottom, 6);
    }

    [Fact]
    public void Both_axes_zoom_respects_the_bar_width_bounds_and_leaves_price_alone_at_the_limit()
    {
        var (m, c, _, render) = Chart(bothAxes: true);
        using var __ = m;
        m.Viewport.BarWidth = ChartViewport.MaxBarWidth / 1.05;
        var l = render();
        double span = l.Main.Top - l.Main.Bottom;
        c.Wheel(500, l.Main.Rect.MidY, 0, 1, InputModifiers.None);
        Assert.Equal(ChartViewport.MaxBarWidth, m.Viewport.BarWidth);
        // the price range shrinks only by what the time axis could apply (1.05, not 1.12)
        Assert.Equal(span / 1.05, m.Viewport.PriceTop - m.Viewport.PriceBottom, 6);
        double top = m.Viewport.PriceTop, bottom = m.Viewport.PriceBottom;
        l = render();
        c.Wheel(500, l.Main.Rect.MidY, 0, 1, InputModifiers.None);
        Assert.Equal(ChartViewport.MaxBarWidth, m.Viewport.BarWidth);
        Assert.Equal(top, m.Viewport.PriceTop);
        Assert.Equal(bottom, m.Viewport.PriceBottom);
    }

    [Fact]
    public void Keyboard_plus_and_minus_zoom_around_the_center_with_the_same_rule()
    {
        var (m, c, l, render) = Chart(bothAxes: true);
        using var _ = m;
        double span = l.Main.Top - l.Main.Bottom, mid = l.Main.ValueAt(l.Main.Rect.MidY);
        Assert.True(c.KeyDown("OemPlus", InputModifiers.None));
        Assert.Equal(8 * 1.2, m.Viewport.BarWidth, 9);
        Assert.Equal(span / 1.2, m.Viewport.PriceTop - m.Viewport.PriceBottom, 6);
        var after = render();
        Assert.Equal(mid, after.Main.ValueAt(after.Main.Rect.MidY), 6);
        Assert.True(c.KeyDown("OemMinus", InputModifiers.None));
        Assert.Equal(8, m.Viewport.BarWidth, 9);

        // without the setting, + only widens bars
        m.Settings.ZoomBothAxes = false;
        m.Viewport.AutoScale = true;
        render();
        Assert.True(c.KeyDown("Add", InputModifiers.None));
        Assert.True(m.Viewport.AutoScale);
    }

    [Fact]
    public void Following_the_live_edge_keeps_following_after_a_zoom()
    {
        var (m, c, l, _) = Chart(bothAxes: true, scrollBars: 0);
        using var __ = m;
        c.Wheel(300, l.Main.Rect.MidY, 0, 2, InputModifiers.None);
        Assert.Equal(0, m.Viewport.ScrollBars);
    }

    [Fact]
    public void Wheel_zoom_both_axes_is_off_by_default_and_survives_clone()
    {
        var settings = new ChartSettings();
        Assert.False(settings.ZoomBothAxes);
        Assert.False(settings.HideDrawings);
        Assert.False(settings.LockDrawings);
        settings.ZoomBothAxes = settings.HideDrawings = settings.LockDrawings = true;
        var copy = settings.Clone();
        Assert.True(copy.ZoomBothAxes && copy.HideDrawings && copy.LockDrawings);
        var json = System.Text.Json.JsonSerializer.Deserialize<ChartSettings>("{}")!;
        Assert.False(json.ZoomBothAxes);
    }

    // ------------------------------------------------------------------ one chart menu at a time

    private sealed class Menu(string name)
    {
        public string Name { get; } = name;
        public int Closes { get; set; }
    }

    [Fact]
    public void Opening_a_chart_menu_closes_the_previous_one()
    {
        var slot = new ExclusiveMenuSlot<Menu>();
        var a = new Menu("a");
        var b = new Menu("b");
        slot.Opening(a, m => m.Closes++);
        Assert.Same(a, slot.Current);
        slot.Opening(b, m => m.Closes++);
        Assert.Equal(1, a.Closes);
        Assert.Equal(0, b.Closes);
        Assert.Same(b, slot.Current);

        // the previous menu's late Closed event never clears the new one
        slot.Closed(a);
        Assert.Same(b, slot.Current);
        slot.Closed(b);
        Assert.Null(slot.Current);

        // reopening the same menu does not close it
        slot.Opening(b, m => m.Closes++);
        slot.Opening(b, m => m.Closes++);
        Assert.Equal(0, b.Closes);
    }
}
