using System.Text.Json;
using SkiaSharp;
using TradingHub.App.Views;
using TradingHub.Charting;
using TradingHub.Charting.Dom;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Indicators;
using TradingHub.Rendering.Control;

namespace TradingHub.Tests;

public sealed class ChartToolsTests
{
    private static readonly DateTime Start = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static Instrument Instrument(string feed = "drawing-fixture", decimal tick = 1, decimal step = 1, decimal point = 1) =>
        new() { Symbol = "FIXTURE", FeedId = feed, TickSize = tick, VolumeStep = step, PointValue = point, QuoteAsset = "USD" };
    private static CandleSeries Series(TradeStore store, ChartPeriod? period = null)
    {
        var result = new CandleSeries(store.Instrument, period ?? ChartPeriod.M1);
        result.Rebuild(store);
        return result;
    }

    [Fact]
    public void Sparse_extreme_negative_profile_has_bounded_rows_and_exact_decimal_volume()
    {
        var store = new TradeStore(Instrument(tick: 0.000001m));
        store.Add(new Trade(1, Start, -1_000_000_000_000m, 0.123456789123456789m, TradeSide.Sell));
        store.Add(new Trade(2, Start.AddSeconds(1), 1_000_000_000_000m, 0.987654321987654321m, TradeSide.Buy));
        store.Add(new Trade(3, Start.AddSeconds(2), 0, 0.5m, TradeSide.None));
        var result = Assert.IsType<TradeDrawingCalc.ProfileResult>(new TradeDrawingCalc().FixedRangeProfile(store, Series(store), Start, Start.AddSeconds(2), 2));
        Assert.InRange(result.Rows.Count, 1, 2);
        Assert.Equal(1.611111111111111110m, result.Total);
        Assert.Equal(0.123456789123456789m, result.Rows.Sum(r => r.Bid));
        Assert.Equal(0.987654321987654321m, result.Rows.Sum(r => r.Ask));
        Assert.Equal(0.5m, result.Rows.Sum(r => r.Unclassified));
        Assert.Equal(-1_000_000_000_000m, result.Low);
        Assert.Equal(1_000_000_000_000m, result.High);
    }

    [Fact]
    public void Range_includes_all_records_at_both_endpoints_even_when_endpoints_are_equal_or_reversed()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, -10, 2, TradeSide.Sell));
        store.Add(new Trade(2, Start.AddSeconds(1), 0, 3, TradeSide.Buy));
        store.Add(new Trade(3, Start.AddSeconds(1), 10, 1, TradeSide.None));
        store.Add(new Trade(4, Start.AddSeconds(2), 20, 100, TradeSide.Buy));
        var series = Series(store);
        var calc = new TradeDrawingCalc();
        var result = Assert.IsType<TradeDrawingCalc.ProfileResult>(calc.FixedRangeProfile(store, series, Start.AddSeconds(1), Start, 3));
        Assert.Equal(6m, result.Total);
        Assert.Equal(7m, result.RowSize);
        Assert.Equal(-3m, result.Poc);
        Assert.Equal(new[] { -2m, 3m, 0m }, result.Rows.Select(r => r.Delta));
        var oneInstant = Assert.IsType<TradeDrawingCalc.ProfileResult>(calc.FixedRangeProfile(store, series, Start.AddSeconds(1), Start.AddSeconds(1), 3));
        Assert.Equal(4m, oneInstant.Total);
        Assert.Equal(2, oneInstant.Coverage.Records);
    }

    [Fact]
    public void Same_count_same_volume_price_correction_and_different_provider_cannot_reuse_cached_distribution()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, 10, 2, TradeSide.Buy));
        store.Add(new Trade(2, Start.AddSeconds(1), 20, 1, TradeSide.Sell));
        var series = Series(store);
        var calc = new TradeDrawingCalc();
        var first = Assert.IsType<TradeDrawingCalc.ProfileResult>(calc.FixedRangeProfile(store, series, Start, Start.AddMinutes(1), 100));
        Assert.Equal(10m, first.Poc);
        store.Update(0, new Trade(1, Start, -30, 2, TradeSide.Buy));
        series.Append(store);
        var corrected = Assert.IsType<TradeDrawingCalc.ProfileResult>(calc.FixedRangeProfile(store, series, Start, Start.AddMinutes(1), 100));
        Assert.NotSame(first, corrected);
        Assert.Equal(-30m, corrected.Poc);
        Assert.Equal(first.Total, corrected.Total);
        var other = new TradeStore(Instrument(feed: "other-provider"));
        other.Add(new Trade(1, Start, 700, 2, TradeSide.Buy));
        other.Add(new Trade(2, Start.AddSeconds(1), 800, 1, TradeSide.Sell));
        var otherResult = Assert.IsType<TradeDrawingCalc.ProfileResult>(calc.FixedRangeProfile(other, Series(other), Start, Start.AddMinutes(1), 100));
        Assert.Equal(700m, otherResult.Poc);
        Assert.Throws<ArgumentException>(() => calc.FixedRangeProfile(other, series, Start, Start.AddMinutes(1)));
    }

    [Fact]
    public void Compact_synthetic_inferred_and_partial_coverage_remain_distinct_in_drawing_result()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, 0, 2, TradeSide.Buy));
        store.Add(new Trade(2, Start.AddSeconds(1), 1, 5, TradeSide.Sell, new TradeProvenance(DataQuality.Compact | DataQuality.InferredSide | DataQuality.Partial, 15, "bucket-source")));
        store.Add(new Trade(-1, Start.AddSeconds(2), 2, 3, TradeSide.None));
        var result = Assert.IsType<TradeDrawingCalc.ProfileResult>(new TradeDrawingCalc().FixedRangeProfile(store, Series(store), Start, Start.AddSeconds(2)));
        Assert.Equal(10m, result.Total);
        Assert.True(result.Coverage.Quality.HasFlag(DataQuality.Raw));
        Assert.True(result.Coverage.Quality.HasFlag(DataQuality.Compact));
        Assert.True(result.Coverage.Quality.HasFlag(DataQuality.Synthetic));
        Assert.True(result.Coverage.Quality.HasFlag(DataQuality.InferredSide));
        Assert.True(result.Coverage.Quality.HasFlag(DataQuality.Partial));
        Assert.Equal(15, result.Coverage.ResolutionSeconds);
    }

    [Fact]
    public void Anchored_weighted_mean_and_deviation_are_exactly_anchored_and_forming_updates_do_not_double_count()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, -100, 100, TradeSide.Sell));
        store.Add(new Trade(2, Start.AddSeconds(1), -10, 2, TradeSide.Sell));
        store.Add(new Trade(3, Start.AddSeconds(2), 10, 6, TradeSide.Buy));
        var series = Series(store);
        var drawing = new ChartDrawing { Kind = DrawingKind.AnchoredVwap, Points = { new ChartPoint(Start.AddSeconds(1), 0) } };
        var runtime = new ChartDrawingRuntime();
        var first = Assert.Single(runtime.CalculateVwap(drawing, store, series).Points);
        Assert.Equal(5m, first.Vwap);
        Assert.Equal(Math.Sqrt(75), first.Deviation, 10);
        Assert.Equal(8m, first.Volume);
        Assert.Equal(first, Assert.Single(runtime.CalculateVwap(drawing, store, series).Points));
        store.Add(new Trade(4, Start.AddSeconds(3), 20, 2, TradeSide.Buy));
        series.Append(store);
        var current = Assert.Single(runtime.CalculateVwap(drawing, store, series).Points);
        Assert.Equal(8m, current.Vwap);
        Assert.Equal(10m, current.Volume);
        Assert.Equal(Math.Sqrt(96), current.Deviation, 10);
        store.Update(3, new Trade(4, Start.AddSeconds(3), -20, 2, TradeSide.Buy));
        series.Append(store);
        Assert.Equal(0m, Assert.Single(runtime.CalculateVwap(drawing, store, series).Points).Vwap);
    }

    [Fact]
    public void Drawing_clones_keep_independent_points_settings_and_calculator_cache()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, 10, 3, TradeSide.Buy));
        store.Add(new Trade(2, Start.AddMinutes(1), 20, 1, TradeSide.Sell));
        var series = Series(store);
        var drawing = new ChartDrawing { Kind = DrawingKind.FixedRangeProfile, ProfileRows = 4, Points = { new ChartPoint(Start, 0), new ChartPoint(Start.AddMinutes(1), 0) } };
        var runtime = new ChartDrawingRuntime();
        var cloneRuntime = new ChartDrawingRuntime();
        var original = Assert.IsType<TradeDrawingCalc.ProfileResult>(runtime.CalculateProfile(drawing, store, series));
        var clone = drawing.Clone();
        clone.Points[0] = new ChartPoint(Start.AddMinutes(1), 999);
        clone.ProfileRows = 1;
        var cloneResult = Assert.IsType<TradeDrawingCalc.ProfileResult>(cloneRuntime.CalculateProfile(clone, store, series));
        Assert.Equal(1m, cloneResult.Total);
        Assert.Equal(20m, cloneResult.Poc);
        Assert.Equal(Start, drawing.Points[0].Time);
        Assert.Equal(4, drawing.ProfileRows);
        Assert.Equal(4m, runtime.CalculateProfile(drawing, store, series)!.Total);
        Assert.Equal(3m, runtime.CalculateProfile(drawing, store, series)!.RowSize);
        Assert.Same(original, runtime.CalculateProfile(drawing, store, series));
        var vwap = new ChartDrawing { Kind = DrawingKind.AnchoredVwap, Points = { new ChartPoint(Start, 0) } };
        var vwapResult = runtime.CalculateVwap(vwap, store, series);
        var vwapClone = vwap.Clone();
        vwapClone.Points[0] = new ChartPoint(Start.AddMinutes(1), 0);
        Assert.Equal(20m, Assert.Single(cloneRuntime.CalculateVwap(vwapClone, store, series).Points).Vwap);
        Assert.Same(vwapResult, runtime.CalculateVwap(vwap, store, series));
    }

    [Fact]
    public void Risk_sizing_uses_point_value_fee_and_floors_to_quantity_increment_for_negative_prices()
    {
        var drawing = new ChartDrawing { Kind = DrawingKind.LongPosition, AccountSize = 1000, RiskPercent = 1, AccountCurrency = "USD", RoundTripFeePerUnit = 0.5m,
            Points = { new ChartPoint(Start, -10), new ChartPoint(Start.AddMinutes(5), -4), new ChartPoint(Start.AddMinutes(5), -13) } };
        var result = drawing.PositionSizing(Instrument(step: 0.25m, point: 2));
        Assert.True(result.IsValid);
        Assert.Equal(10m, result.Budget);
        Assert.Equal(1.5m, result.Quantity);
        Assert.Equal(9.75m, result.Risk);
        Assert.Equal(17.25m, result.Reward);
        drawing.AccountCurrency = "EUR";
        Assert.False(drawing.PositionSizing(Instrument(step: 0.25m, point: 2)).IsValid);
        drawing.AccountCurrency = "USD";
        drawing.Points[2] = drawing.Points[0];
        var invalid = drawing.PositionSizing(Instrument());
        Assert.False(invalid.IsValid);
        Assert.Equal(0, invalid.Quantity);
    }

    [Fact]
    public void Insufficient_risk_never_rounds_up_and_manual_sizing_also_respects_quantity_step()
    {
        var drawing = new ChartDrawing { Kind = DrawingKind.ShortPosition, AccountSize = 100, RiskPercent = 1, AccountCurrency = "USD", Points = { new ChartPoint(Start, 100), new ChartPoint(Start.AddMinutes(1), 90), new ChartPoint(Start.AddMinutes(1), 105) } };
        Assert.Equal(0m, drawing.PositionSizing(Instrument(point: 50)).Quantity);
        drawing.AccountSize = 0;
        drawing.Quantity = 2.99m;
        var manual = drawing.PositionSizing(Instrument(point: 50));
        Assert.Equal(2m, manual.Quantity);
        Assert.Equal(500m, manual.Risk);
        Assert.Equal(1000m, manual.Reward);
    }

    [Fact]
    public void Modified_rectangle_gesture_wins_over_order_handles_and_escape_cancels_without_execution()
    {
        using var model = SearchZoomFixture.Model(4000);
        model.Viewport.BarWidth = 0.1;
        model.Viewport.ScrollBars = 1000;
        var renderer = new ChartRenderer();
        var state = new ChartRenderState();
        using var surface = SKSurface.Create(new SKImageInfo(500, 300));
        var layout = renderer.Render(surface.Canvas, model, 500, 300, state);
        float x = layout.Main.Rect.MidX, y = layout.Main.Rect.MidY;
        state.Handles.Add(new BracketHandle(new SKRect(x - 20, y - 20, x + 20, y + 20), new BracketDrag(null, BracketRole.StopLoss), true));
        var controller = new ChartController(model, state);
        int dropped = 0;
        controller.BracketDropped += (_, _) => dropped++;
        controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.Control);
        Assert.NotNull(state.ZoomRectangle);
        Assert.Null(state.BracketDrag);
        controller.MouseMove(x + 50, y + 50, InputModifiers.Control);
        Assert.True(controller.KeyDown("Escape", InputModifiers.None));
        Assert.Null(state.ZoomRectangle);
        controller.MouseUp(x + 50, y + 50, PointerButton.Left, InputModifiers.None);
        Assert.Equal(0, dropped);
        Assert.Equal(0.1, model.Viewport.BarWidth);
        controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.None);
        Assert.NotNull(state.BracketDrag);
        controller.CancelInteraction();
    }

    [Fact]
    public void Rectangle_zoom_preserves_deep_width_limits_selected_bar_center_and_negative_price_range()
    {
        using var model = SearchZoomFixture.Model(4000, _ => (-20, -10, -30, -20));
        model.Viewport.BarWidth = 0.1;
        model.Viewport.ScrollBars = 1000;
        var renderer = new ChartRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(500, 300));
        var layout = renderer.Render(surface.Canvas, model, 500, 300);
        var rectangle = new SKRect(100, 50, 200, 150);
        double expectedBar = layout.BarAtExact(150);
        double expectedTop = layout.Main.ValueAt(50), expectedBottom = layout.Main.ValueAt(150);
        var controller = new ChartController(model, new ChartRenderState());
        Assert.True(controller.ZoomToRectangle(layout, rectangle));
        Assert.InRange(model.Viewport.BarWidth, ChartViewport.MinBarWidth, 1);
        Assert.Equal(expectedTop, model.Viewport.PriceTop);
        Assert.Equal(expectedBottom, model.Viewport.PriceBottom);
        var after = renderer.Render(surface.Canvas, model, 500, 300);
        Assert.InRange(Math.Abs(after.BarAtExact(after.Main.Rect.MidX) - expectedBar), 0, 0.001);
        controller.ResetView();
        Assert.True(model.Viewport.AutoScale);
        Assert.Equal(0, model.Viewport.ScrollBars);
        Assert.InRange(model.Viewport.BarWidth, 1, ChartViewport.MaxBarWidth);
    }

    [Fact]
    public void Legacy_numeric_drawing_kinds_remain_readable()
    {
        Assert.Equal(10, (int)DrawingKind.LongPosition);
        Assert.Equal(11, (int)DrawingKind.ShortPosition);
        var drawing = JsonSerializer.Deserialize<ChartDrawing>("""{"Kind":11,"Quantity":2,"Points":[{"Time":"2026-10-01T12:00:00Z","Price":100}],"Dashed":true}""")!;
        Assert.Equal(DrawingKind.ShortPosition, drawing.Kind);
        Assert.Equal(2m, drawing.Quantity);
        Assert.Equal(LineStyleKind.Dashed, drawing.EffectiveLineStyle);
    }

    [Fact]
    public void Blind_chart_and_tape_display_elapsed_time_across_days_and_reveal_restores_absolute_time()
    {
        var settings = new ChartSettings { BlindReplay = true, BlindReplayStart = Start };
        Assert.Equal("+1d 02:03:04", settings.ReplayElapsed(Start.AddDays(1).AddHours(2).AddMinutes(3).AddSeconds(4)));
        var tape = new TapeModel(new InstrumentData(Instrument(), new SimulatedFeed())) { BlindReplay = true, BlindReplayStart = Start };
        Assert.Equal("+26:03:04", tape.DisplayTime(Start.AddDays(1).AddHours(2).AddMinutes(3).AddSeconds(4)));
        tape.BlindReplay = false;
        tape.TimeZone = TimeZoneInfo.Utc;
        Assert.Equal("14:03:04.000", tape.DisplayTime(Start.AddDays(1).AddHours(2).AddMinutes(3).AddSeconds(4)));
    }

    [Fact]
    public void Whole_session_range_uses_exchange_DST_boundaries_not_a_fixed_24_hour_window()
    {
        var store = new TradeStore(Instrument());
        var sessionStart = new DateTime(2026, 3, 7, 22, 0, 0, DateTimeKind.Utc);
        store.Add(new Trade(1, sessionStart.AddTicks(-1), 10, 100, TradeSide.Buy));
        store.Add(new Trade(2, sessionStart, 20, 2, TradeSide.Buy));
        store.Add(new Trade(3, sessionStart.AddHours(23).AddTicks(-1), 30, 3, TradeSide.Sell));
        store.Add(new Trade(4, sessionStart.AddHours(23), 40, 200, TradeSide.Buy));
        var session = new SessionSettings { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Start = TimeSpan.FromHours(17) };
        var series = new CandleSeries(store.Instrument, ChartPeriod.M1, session: session);
        series.Rebuild(store);
        var drawing = new ChartDrawing { Kind = DrawingKind.FixedRangeProfile, ProfileWholeSessions = true,
            Points = { new ChartPoint(sessionStart.AddHours(1), 0), new ChartPoint(sessionStart.AddHours(20), 0) } };
        var result = Assert.IsType<TradeDrawingCalc.ProfileResult>(new ChartDrawingRuntime().CalculateProfile(drawing, store, series));
        Assert.Equal(5m, result.Total);
        Assert.Equal(sessionStart, result.From);
        Assert.Equal(sessionStart.AddHours(23).AddTicks(-1), result.To);
    }

    [Fact]
    public void Future_anchor_does_not_accumulate_pre_anchor_appends_and_incremental_results_equal_a_fresh_calculation()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, 0, 10, TradeSide.Buy));
        var series = Series(store);
        var calc = new TradeDrawingCalc();
        Assert.Empty(calc.AnchoredVwap(store, series, Start.AddSeconds(20)).Points);
        store.Add(new Trade(2, Start.AddSeconds(10), 100, 20, TradeSide.Buy));
        series.Append(store);
        Assert.Empty(calc.AnchoredVwap(store, series, Start.AddSeconds(20)).Points);
        store.Add(new Trade(3, Start.AddSeconds(20), -10, 2, TradeSide.Sell));
        series.Append(store);
        Assert.Equal(-10m, Assert.Single(calc.AnchoredVwap(store, series, Start.AddSeconds(20)).Points).Vwap);
        store.Add(new Trade(4, Start.AddSeconds(30), 10, 6, TradeSide.Buy));
        series.Append(store);
        var incremental = calc.AnchoredVwap(store, series, Start.AddSeconds(20));
        var fresh = new TradeDrawingCalc().AnchoredVwap(store, series, Start.AddSeconds(20));
        Assert.Equal(fresh.Points, incremental.Points);
        Assert.Equal(fresh.Coverage, incremental.Coverage);
    }

    [Fact]
    public void Profile_append_row_change_and_same_size_replacement_match_a_fresh_CPU_calculation()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, -10, 2, TradeSide.Sell));
        store.Add(new Trade(2, Start.AddSeconds(1), 10, 6, TradeSide.Buy));
        var series = Series(store);
        var calc = new TradeDrawingCalc();
        calc.FixedRangeProfile(store, series, Start, Start.AddHours(1), 4);
        store.Add(new Trade(3, Start.AddSeconds(2), 0, 1, TradeSide.None));
        series.Append(store);
        var incremental = Assert.IsType<TradeDrawingCalc.ProfileResult>(calc.FixedRangeProfile(store, series, Start, Start.AddHours(1), 4));
        var fresh = Assert.IsType<TradeDrawingCalc.ProfileResult>(new TradeDrawingCalc().FixedRangeProfile(store, series, Start, Start.AddHours(1), 4));
        Assert.Equal(9m, incremental.Total);
        Assert.Equal(fresh.Rows, incremental.Rows);
        var oneRow = Assert.IsType<TradeDrawingCalc.ProfileResult>(calc.FixedRangeProfile(store, series, Start, Start.AddHours(1), 1));
        Assert.Single(oneRow.Rows);
        store.Replace(new[]
        {
            new Trade(1, Start, 100, 2, TradeSide.Sell),
            new Trade(2, Start.AddSeconds(1), 100, 6, TradeSide.Buy),
            new Trade(3, Start.AddSeconds(2), 100, 1, TradeSide.None),
        });
        series.Append(store);
        var replaced = Assert.IsType<TradeDrawingCalc.ProfileResult>(calc.FixedRangeProfile(store, series, Start, Start.AddHours(1), 1));
        Assert.Equal(100m, replaced.Poc);
        Assert.Equal(9m, replaced.Total);
        var fiveMinute = Series(store, ChartPeriod.M5);
        Assert.Equal(100m, Assert.Single(calc.AnchoredVwap(store, fiveMinute, Start).Points).Vwap);
    }

    private sealed class InteractiveStudy : Indicator
    {
        public int Clicks { get; private set; }
        public override bool ProcessMouseDown(RenderControlMouseEventArgs e) { Clicks++; return true; }
        protected override void OnCalculate(int bar, decimal value) { }
    }

    [Fact]
    public void Unmodified_indicator_handle_keeps_precedence_and_selected_drawing_tool_is_not_changed_to_zoom()
    {
        using var model = SearchZoomFixture.Model(100);
        var study = new InteractiveStudy();
        model.AddIndicator(study);
        var renderer = new ChartRenderer();
        var state = new ChartRenderState();
        using var surface = SKSurface.Create(new SKImageInfo(500, 300));
        var layout = renderer.Render(surface.Canvas, model, 500, 300, state);
        var controller = new ChartController(model, state);
        float x = layout.Main.Rect.MidX, y = layout.Main.Rect.MidY;
        controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.None);
        Assert.Equal(1, study.Clicks);
        Assert.False(controller.IsDragging);
        controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.Control);
        Assert.Equal(1, study.Clicks);
        Assert.NotNull(state.ZoomRectangle);
        controller.CancelInteraction();
        model.RemoveIndicator(model.Indicators.Single());
        controller.ActiveTool = DrawingKind.HorizontalLine;
        controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.Control);
        Assert.Null(state.ZoomRectangle);
        Assert.Equal(DrawingKind.HorizontalLine, Assert.Single(model.Drawings).Kind);
    }

    [Fact]
    public void Click_click_drawing_is_preserved_until_the_second_in_plot_click_and_empty_zoom_is_a_no_op()
    {
        using var model = SearchZoomFixture.Model(100);
        var renderer = new ChartRenderer();
        var state = new ChartRenderState();
        using var surface = SKSurface.Create(new SKImageInfo(500, 300));
        var layout = renderer.Render(surface.Canvas, model, 500, 300, state);
        var controller = new ChartController(model, state) { ActiveTool = DrawingKind.TrendLine };
        float x = layout.Main.Rect.MidX, y = layout.Main.Rect.MidY;
        controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.None);
        controller.MouseUp(x, y, PointerButton.Left, InputModifiers.None);
        Assert.True(controller.HasPending);
        Assert.False(controller.TryCompletePending(layout.PriceAxis.MidX, y, InputModifiers.None));
        Assert.True(controller.HasPending);
        Assert.True(controller.TryCompletePending(x + 40, y + 40, InputModifiers.None));
        Assert.False(controller.HasPending);
        Assert.Single(model.Drawings);
        double width = model.Viewport.BarWidth;
        Assert.False(controller.ZoomToRectangle(layout, new SKRect(x, y, x + 1, y + 1)));
        Assert.Equal(width, model.Viewport.BarWidth);
    }

    [Fact]
    public void Tick_bars_sharing_a_timestamp_keep_their_own_anchored_cumulative_values()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, 10, 1, TradeSide.Buy));
        store.Add(new Trade(2, Start, 20, 1, TradeSide.Buy));
        store.Add(new Trade(3, Start, 30, 1, TradeSide.Sell));
        var series = Series(store, new ChartPeriod(PeriodType.Tick, 1));
        var result = new TradeDrawingCalc().AnchoredVwap(store, series, Start);
        Assert.Equal(new[] { 10m, 15m, 20m }, result.Points.Select(p => p.Vwap));
        Assert.Equal(new[] { 1m, 2m, 3m }, result.Points.Select(p => p.Volume));
    }

    [Fact]
    public void Value_area_visits_empty_logical_rows_before_distant_occupied_bins_and_uses_upper_ties()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, -10, 9, TradeSide.Sell));
        store.Add(new Trade(2, Start.AddSeconds(1), 0, 10, TradeSide.Buy));
        store.Add(new Trade(3, Start.AddSeconds(2), 1, 8, TradeSide.Buy));
        var result = Assert.IsType<TradeDrawingCalc.ProfileResult>(new TradeDrawingCalc().FixedRangeProfile(store, Series(store), Start, Start.AddSeconds(2), 100, 70));
        Assert.Equal(0m, result.Poc);
        Assert.Equal(1m, result.Vah);
        Assert.Equal(-10m, result.Val);
        store.Replace(new[]
        {
            new Trade(1, Start, -1, 5, TradeSide.Sell),
            new Trade(2, Start.AddSeconds(1), 0, 10, TradeSide.Buy),
            new Trade(3, Start.AddSeconds(2), 1, 5, TradeSide.Buy),
        });
        var tie = Assert.IsType<TradeDrawingCalc.ProfileResult>(new TradeDrawingCalc().FixedRangeProfile(store, Series(store), Start, Start.AddSeconds(2), 100, 75));
        Assert.Equal(1m, tie.Vah);
        Assert.Equal(0m, tie.Val);
    }

    [Fact]
    public void Volume_bar_boundary_consumes_only_its_split_quantity_and_live_forming_updates_match_fresh_calculation()
    {
        var store = new TradeStore(Instrument());
        store.Add(new Trade(1, Start, 10, 5, TradeSide.Buy));
        store.Add(new Trade(2, Start.AddSeconds(1), 20, 10, TradeSide.Sell));
        var period = new ChartPeriod(PeriodType.Volume, 5);
        var golden = new TradeDrawingCalc().AnchoredVwap(store, Series(store, period), Start);
        Assert.Equal(new[] { 5m, 10m, 15m }, golden.Points.Select(p => p.Volume));
        Assert.Equal(10m, golden.Points[0].Vwap);
        Assert.Equal(15m, golden.Points[1].Vwap);
        Assert.InRange(Math.Abs(golden.Points[2].Vwap - 50m / 3), 0, 0.0000000000000000000000001m);

        var live = new TradeStore(Instrument());
        live.Add(new Trade(1, Start, 10, 5, TradeSide.Buy));
        var series = Series(live, period);
        var calc = new TradeDrawingCalc();
        calc.AnchoredVwap(live, series, Start);
        live.Add(new Trade(2, Start.AddSeconds(1), 20, 4, TradeSide.Sell));
        series.Append(live);
        var forming = calc.AnchoredVwap(live, series, Start);
        Assert.Equal(9m, forming.Points[^1].Volume);
        Assert.Equal(forming.Points[^1], calc.AnchoredVwap(live, series, Start).Points[^1]);
        live.Add(new Trade(3, Start.AddSeconds(2), 20, 6, TradeSide.Sell));
        series.Append(live);
        var incremental = calc.AnchoredVwap(live, series, Start);
        var fresh = new TradeDrawingCalc().AnchoredVwap(live, series, Start);
        Assert.Equal(new[] { 5m, 10m, 15m }, incremental.Points.Select(p => p.Volume));
        Assert.Equal(15m, incremental.Points[1].Vwap);
        Assert.Equal(fresh.Points, incremental.Points);
        Assert.Equal(fresh.Coverage, incremental.Coverage);
    }

    [Fact]
    public void Explicit_cursor_selection_releases_rectangle_zoom_before_an_unmodified_drag()
    {
        using var model = SearchZoomFixture.Model(200);
        var renderer = new ChartRenderer();
        var state = new ChartRenderState();
        using var surface = SKSurface.Create(new SKImageInfo(600, 400));
        var layout = renderer.Render(surface.Canvas, model, 600, 400, state);
        var controller = new ChartController(model, state);
        controller.SelectRectangleZoom();
        controller.ActiveTool = null;
        double width = model.Viewport.BarWidth, scroll = model.Viewport.ScrollBars;
        float x = layout.Main.Rect.MidX, y = layout.Main.Rect.MidY;
        controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.None);
        controller.MouseMove(x + 40, y, InputModifiers.None);
        controller.MouseUp(x + 40, y, PointerButton.Left, InputModifiers.None);
        Assert.Null(state.ZoomRectangle);
        Assert.Equal(width, model.Viewport.BarWidth);
        Assert.True(model.Viewport.ScrollBars > scroll);
        controller.SelectRectangleZoom();
        controller.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.None);
        controller.MouseMove(x + 40, y + 40, InputModifiers.None);
        controller.ActiveTool = null;
        controller.MouseUp(x + 40, y + 40, PointerButton.Left, InputModifiers.None);
        Assert.Null(state.ZoomRectangle);
        Assert.Equal(width, model.Viewport.BarWidth);
    }

    [Fact]
    public void Alternating_charts_share_parameters_but_keep_their_own_profile_and_vwap_hit_geometry_and_cache()
    {
        using var first = SearchZoomFixture.Model(300, _ => (100, 100, 100, 100));
        using var second = SearchZoomFixture.Model(300, _ => (100, 100, 100, 100));
        first.Viewport.BarWidth = 8;
        first.Viewport.ScrollBars = 120;
        first.Viewport.AutoScale = false;
        first.Viewport.PriceTop = 120;
        first.Viewport.PriceBottom = 80;
        second.Viewport.BarWidth = 20;
        second.Viewport.ScrollBars = 20;
        second.Viewport.AutoScale = false;
        second.Viewport.PriceTop = 400;
        second.Viewport.PriceBottom = 0;
        var anchor = SearchZoomFixture.Start;
        var vwap = new ChartDrawing { Kind = DrawingKind.AnchoredVwap, Points = { new ChartPoint(anchor, 100) } };
        var profile = new ChartDrawing { Kind = DrawingKind.FixedRangeProfile,
            Points = { new ChartPoint(anchor.AddMinutes(120), 100), new ChartPoint(anchor.AddMinutes(250), 100) } };
        first.Drawings.Add(vwap); first.Drawings.Add(profile);
        second.Drawings.Add(vwap); second.Drawings.Add(profile);
        var firstState = new ChartRenderState();
        var secondState = new ChartRenderState();
        var renderer = new ChartRenderer();
        using var firstSurface = SKSurface.Create(new SKImageInfo(900, 500));
        using var secondSurface = SKSurface.Create(new SKImageInfo(440, 300));
        var firstLayout = renderer.Render(firstSurface.Canvas, first, 900, 500, firstState);
        var firstVwap = firstState.FindDrawingRuntime(vwap)!;
        var firstProfile = firstState.FindDrawingRuntime(profile)!;
        var vwapResult = firstVwap.CalculateVwap(vwap, first.Data.Trades, first.Series, first.DataGeneration);
        var profileResult = firstProfile.CalculateProfile(profile, first.Data.Trades, first.Series, first.DataGeneration);
        for (int repaint = 0; repaint < 3; repaint++)
        {
            renderer.Render(secondSurface.Canvas, second, 440, 300, secondState);
            Assert.True(vwap.HitTest(firstLayout, first.Series, firstVwap, 600, firstLayout.Main.Y(100m), out _));
            Assert.True(profile.HitTest(firstLayout, first.Series, firstProfile, 300, firstLayout.Main.Y(100.5m), out _));
            Assert.Same(vwapResult, firstVwap.CalculateVwap(vwap, first.Data.Trades, first.Series, first.DataGeneration));
            Assert.Same(profileResult, firstProfile.CalculateProfile(profile, first.Data.Trades, first.Series, first.DataGeneration));
        }
        first.Drawings.Remove(vwap);
        renderer.Render(firstSurface.Canvas, first, 900, 500, firstState);
        Assert.Null(firstState.FindDrawingRuntime(vwap));
        Assert.NotNull(secondState.FindDrawingRuntime(vwap));
    }

    [Fact]
    public void Deep_decimated_vwap_keeps_the_adjacent_segment_when_no_sample_is_inside_the_zoomed_interval()
    {
        var store = new TradeStore(Instrument());
        for (int i = 0; i < 100_000; i++) store.Add(new Trade(i + 1, Start.AddMinutes(i), 100, 1, TradeSide.Buy));
        var series = Series(store);
        var layout = new ChartLayout { Bounds = new SKRect(0, 0, 400, 200), Plot = new SKRect(0, 0, 400, 180),
            Count = series.Count, BarWidth = 100, RightMarginBars = 0, ScrollBars = 99_946, FirstVisible = 51, LastVisible = 52 };
        layout.Panels.Add(new PanelLayout(layout, layout.Plot, true) { Top = 120, Bottom = 80, RowSize = 1 });
        var drawing = new ChartDrawing { Kind = DrawingKind.AnchoredVwap, Band1 = 0, Band2 = 0,
            Points = { new ChartPoint(Start, 100) } };
        var runtime = new ChartDrawingRuntime();
        using var bitmap = new SKBitmap(400, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.ClipRect(layout.Plot);
        drawing.Render(canvas, layout, series, new ChartTheme(), 0, runtime, store);
        Assert.True(drawing.HitTest(layout, series, runtime, 200, 90, out _));
        Assert.Contains(Enumerable.Range(-1, 3), offset => bitmap.GetPixel(200, 90 + offset).Alpha > 0);
    }

    [Theory]
    [InlineData("15", "M15")]
    [InlineData("1h", "H1")]
    [InlineData("M5", "M5")]
    [InlineData("d", "Daily")]
    [InlineData("700.HK", null)]
    [InlineData("0", null)]
    [InlineData("M999999999999999999", null)]
    public void Typed_periods_are_separate_from_numeric_symbol_text_and_bounded(string input, string? code)
    {
        bool parsed = TradingView.TryChartEntryPeriod(input, out var period);
        Assert.Equal(code != null, parsed);
        if (code != null) Assert.Equal(code, period.Code);
    }
}
