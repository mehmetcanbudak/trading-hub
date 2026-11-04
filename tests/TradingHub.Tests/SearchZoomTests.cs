using System.Reflection;
using SkiaSharp;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Market;

namespace TradingHub.Tests;

public class SearchZoomTests
{
    private static ConnectionProfile Profile() => new() { Id = Guid.NewGuid().ToString("N"), Kind = ConnectionKind.Simulator, Name = "Fixture" };

    [Fact]
    public async Task Invalidating_during_a_request_rejects_the_old_result_and_does_not_repopulate_cache()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = Profile();
        var feed = new SearchZoomFeed { Response = _ => pending.Task };
        var services = SearchZoomFixture.Services((profile, feed));
        var old = InstrumentPicker.InstrumentsAsync(services, profile.Id);
        InstrumentPicker.ClearCache(profile.Id);
        pending.SetResult(new[] { SearchZoomFixture.Instrument("OLD") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => old);
        feed.Response = _ => Task.FromResult<IReadOnlyList<Instrument>>(new[] { SearchZoomFixture.Instrument("NEW") });
        Assert.Equal("NEW", Assert.Single(await InstrumentPicker.InstrumentsAsync(services, profile.Id)).Symbol);
        Assert.Equal(2, feed.Requests);
        InstrumentPicker.ClearCache(profile.Id);
    }

    [Fact]
    public async Task Cancellation_abandons_even_an_uncooperative_source_without_caching_its_late_result()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = Profile();
        var feed = new SearchZoomFeed { Response = _ => pending.Task };
        var services = SearchZoomFixture.Services((profile, feed));
        using var cancel = new CancellationTokenSource();
        var method = typeof(InstrumentPicker).GetMethod("CachedInstrumentsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var request = (Task<IReadOnlyList<Instrument>>)method.Invoke(null, new object[] { services, profile.Id, cancel.Token, TimeSpan.FromSeconds(5) })!;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(feed.LastToken.IsCancellationRequested);
        pending.SetResult(new[] { SearchZoomFixture.Instrument("STALE") });
        feed.Response = _ => Task.FromResult<IReadOnlyList<Instrument>>(new[] { SearchZoomFixture.Instrument("FRESH") });
        Assert.Equal("FRESH", Assert.Single(await InstrumentPicker.InstrumentsAsync(services, profile.Id)).Symbol);
        InstrumentPicker.ClearCache(profile.Id);
    }

    private sealed class UncooperativeStatsSource(Task<IReadOnlyDictionary<string, TickerStats>> response) : ITickerStatsProvider
    {
        public CancellationToken LastToken { get; private set; }
        public int Requests { get; private set; }
        public Task<IReadOnlyDictionary<string, TickerStats>> Response { get; set; } = response;

        public Task<IReadOnlyDictionary<string, TickerStats>> GetTickerStatsAsync(CancellationToken token)
        {
            LastToken = token;
            Requests++;
            return Response;
        }
    }

    [Fact]
    public async Task Statistics_cancellation_cancels_transport_before_abandoning_and_rejects_late_cache_writes()
    {
        var pending = new TaskCompletionSource<IReadOnlyDictionary<string, TickerStats>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UncooperativeStatsSource(pending.Task);
        string profileId = Profile().Id;
        using var cancel = new CancellationTokenSource();
        var method = typeof(InstrumentPicker).GetMethod("CachedStatsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var request = (Task<IReadOnlyDictionary<string, TickerStats>>)method.Invoke(null, new object[] { profileId, source, cancel.Token })!;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(source.LastToken.IsCancellationRequested);
        pending.SetResult(new Dictionary<string, TickerStats> { ["STALE"] = new("STALE", 1, 0, 1, 1, 1) });
        source.Response = Task.FromResult<IReadOnlyDictionary<string, TickerStats>>(
            new Dictionary<string, TickerStats> { ["FRESH"] = new("FRESH", 2, 0, 1, 2, 2) });
        var fresh = await (Task<IReadOnlyDictionary<string, TickerStats>>)method.Invoke(null, new object[] { profileId, source, CancellationToken.None })!;
        Assert.Equal("FRESH", Assert.Single(fresh).Key);
        Assert.Equal(2, source.Requests);
        InstrumentPicker.ClearCache(profileId);
    }

    [Fact]
    public async Task Timed_out_sources_report_failure_and_cancel_the_transport()
    {
        var profile = Profile();
        var pending = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new SearchZoomFeed { Response = _ => pending.Task };
        var services = SearchZoomFixture.Services((profile, feed));
        var method = typeof(InstrumentPicker).GetMethod("CachedInstrumentsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var request = (Task<IReadOnlyList<Instrument>>)method.Invoke(null, new object[] { services, profile.Id, CancellationToken.None, TimeSpan.FromMilliseconds(20) })!;
        await Assert.ThrowsAsync<TimeoutException>(() => request);
        Assert.True(feed.LastToken.IsCancellationRequested);
        pending.SetResult(Array.Empty<Instrument>());
        InstrumentPicker.ClearCache(profile.Id);
    }

    [Fact]
    public async Task Concurrent_profile_loads_share_a_safe_cache_and_targeted_invalidation_preserves_other_profiles()
    {
        var sources = Enumerable.Range(0, 24).Select(i => (Profile: Profile(), Feed: new SearchZoomFeed(SearchZoomFixture.Instrument("SYMBOL-" + i)))).ToArray();
        var services = SearchZoomFixture.Services(sources);
        var lists = await Task.WhenAll(sources.Select(source => Task.Run(() => InstrumentPicker.InstrumentsAsync(services, source.Profile.Id))));
        for (int i = 0; i < sources.Length; i++) Assert.Equal("SYMBOL-" + i, Assert.Single(lists[i]).Symbol);
        InstrumentPicker.ClearCache(sources[0].Profile.Id);
        await InstrumentPicker.InstrumentsAsync(services, sources[0].Profile.Id);
        await InstrumentPicker.InstrumentsAsync(services, sources[1].Profile.Id);
        Assert.Equal(2, sources[0].Feed.Requests);
        Assert.Equal(1, sources[1].Feed.Requests);
        foreach (var source in sources) InstrumentPicker.ClearCache(source.Profile.Id);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.5)]
    [InlineData(8)]
    public void Dense_layout_keeps_coordinate_round_trips_margin_and_focal_zoom(double width)
    {
        using var model = SearchZoomFixture.Model(4000);
        model.Viewport.BarWidth = width;
        model.Viewport.ScrollBars = 1000;
        model.Viewport.RightMarginPx = 70;
        var renderer = new ChartRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        var layout = renderer.Render(surface.Canvas, model, 400, 300);
        Assert.InRange(layout.RightMarginBars * width, 70, 70 + width);
        int bar = (layout.FirstVisible + layout.LastVisible) / 2;
        Assert.InRange(Math.Abs(layout.BarAtExact(layout.CenterX(bar)) - bar), 0, 0.001);
        Assert.Equal(bar, layout.BarAt(layout.CenterX(bar)));
        float x = layout.Plot.MidX;
        double before = layout.BarAtExact(x);
        var controller = new ChartController(model, new ChartRenderState());
        controller.Wheel(x, 100, 0, 2, InputModifiers.None);
        var after = renderer.Render(surface.Canvas, model, 400, 300);
        Assert.InRange(Math.Abs(after.BarAtExact(x) - before), 0, 0.001);
        controller.Wheel(x, 100, 10, 0, InputModifiers.Shift);
        renderer.Render(surface.Canvas, model, 400, 300);
        Assert.True(double.IsFinite(model.Viewport.ScrollBars));
        model.Viewport.ScrollBars = 0;
        renderer.Render(surface.Canvas, model, 400, 300);
        controller.Wheel(x, 100, 0, 1, InputModifiers.None);
        Assert.Equal(0, model.Viewport.ScrollBars);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-2)]
    public void Invalid_persisted_widths_and_scroll_are_bounded_at_layout(double width)
    {
        using var model = SearchZoomFixture.Model(5);
        model.Viewport.BarWidth = width;
        model.Viewport.ScrollBars = double.NaN;
        model.Viewport.RightMarginPx = double.PositiveInfinity;
        var layout = ChartRenderer.ComputeLayout(model, 400, 300);
        Assert.InRange(layout.BarWidth, 0.1, 400);
        Assert.True(double.IsFinite(layout.ScrollBars));
        Assert.True(float.IsFinite(layout.CenterX(0)));
    }

    [Theory]
    [InlineData(ChartStyle.Candles)]
    [InlineData(ChartStyle.Bars)]
    public void Pixel_buckets_preserve_early_extrema_aggregate_direction_and_hidden_bar_semantics(ChartStyle style)
    {
        using var model = SearchZoomFixture.Model(20, i => i == 0 ? (150, 180, 20, 100) : (100, 120, 80, 110));
        model.Viewport.BarWidth = 0.1;
        model.Viewport.AutoScale = false;
        model.Viewport.PriceTop = 200;
        model.Viewport.PriceBottom = 0;
        var layout = ChartRenderer.ComputeLayout(model, 200, 220);
        int x = (int)MathF.Floor(layout.CenterX(0));
        using var bitmap = new SKBitmap(200, 220);
        using var canvas = new SKCanvas(bitmap);
        bool Ink(decimal value) => Enumerable.Range(-1, 3).Any(offset => bitmap.GetPixel(x, (int)layout.Main.Y(value) + offset).Alpha != 0);
        canvas.Clear(SKColors.Transparent);
        SearchZoomFixture.RenderCandles(canvas, model, layout, style);
        Assert.True(Ink(179));
        Assert.True(Ink(21));
        Assert.Equal(model.Theme.DownCandle, bitmap.GetPixel(x, (int)layout.Main.Y(150m)));
        canvas.Clear(SKColors.Transparent);
        SearchZoomFixture.RenderCandles(canvas, model, layout, style, new() { [0] = SKColors.Transparent });
        Assert.False(Ink(179));
        Assert.False(Ink(21));
        Assert.True(Ink(119));
        canvas.Clear(SKColors.Transparent);
        int lastInBucket = Enumerable.Range(0, model.Series.Count).Last(i => (int)MathF.Floor(layout.CenterX(i)) == x);
        SearchZoomFixture.RenderCandles(canvas, model, layout, style, new() { [lastInBucket] = SKColors.Magenta });
        Assert.Equal(SKColors.Magenta, bitmap.GetPixel(x, (int)layout.Main.Y(150m)));
    }

    [Fact]
    public void Dense_line_preserves_close_extrema_instead_of_sampling_only_the_last_close()
    {
        using var model = SearchZoomFixture.Model(20, i => i == 0 ? (100, 180, 100, 180) : i == 1 ? (100, 100, 20, 20) : (100, 100, 100, 100));
        model.Viewport.BarWidth = 0.1;
        model.Viewport.AutoScale = false;
        model.Viewport.PriceTop = 200;
        model.Viewport.PriceBottom = 0;
        var layout = ChartRenderer.ComputeLayout(model, 200, 220);
        using var bitmap = new SKBitmap(200, 220);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        SearchZoomFixture.RenderCandles(canvas, model, layout, ChartStyle.Line);
        int x = (int)MathF.Floor(layout.CenterX(0));
        Assert.NotEqual((byte)0, bitmap.GetPixel(x, (int)layout.Main.Y(179m)).Alpha);
        Assert.NotEqual((byte)0, bitmap.GetPixel(x, (int)layout.Main.Y(21m)).Alpha);
    }

    private sealed class NonzeroBaselineHistogram : Indicator
    {
        public NonzeroBaselineHistogram()
        {
            Panel = IndicatorDataProvider.NewPanel;
            DataSeries.Clear();
            DataSeries.Add(Values);
        }

        public ValueDataSeries Values { get; } = new("Histogram", "Histogram")
        {
            VisualType = VisualMode.Histogram,
            ZeroValue = 100,
            ShowZeroValue = false,
            ShowCurrentValue = false,
            Color = System.Drawing.Color.Magenta,
        };

        protected override void OnCalculate(int bar, decimal value) => Values[bar] = bar == 0 ? 0 : 100;
    }

    [Fact]
    public void Legacy_histogram_scales_and_renders_actual_zero_but_hides_its_nonzero_baseline()
    {
        using var model = SearchZoomFixture.Model(4);
        model.Viewport.BarWidth = 20;
        model.Viewport.RightMarginPx = 0;
        model.Settings.ShowGrid = false;
        model.Settings.ShowIndicatorNames = false;
        var histogram = new NonzeroBaselineHistogram();
        var slot = model.AddIndicator(histogram, applyDefaultColors: false);
        Assert.Null(slot.Error);
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        var layout = new ChartRenderer().Render(canvas, model, 400, 300);
        var panel = Assert.Single(layout.Panels, p => !p.IsPrice);
        // The actual zero must participate in autoscaling, not be clipped off-screen as a gap.
        Assert.True(panel.Bottom < 0 && panel.Top > 100);
        int y = (int)panel.Y(50m);
        int zeroX = (int)MathF.Round(layout.CenterX(0));
        int baselineX = (int)MathF.Round(layout.CenterX(1));
        Assert.Equal(SKColors.Magenta, bitmap.GetPixel(zeroX, y));
        Assert.NotEqual(SKColors.Magenta, bitmap.GetPixel(baselineX, y));
    }
}
