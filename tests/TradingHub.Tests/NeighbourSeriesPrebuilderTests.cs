using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Background aggregation of neighbouring timeframes: identity, invalidation, admission and cancellation.</summary>
public sealed class NeighbourSeriesPrebuilderTests
{
    private static readonly DateTime Start = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private static TradeStore Store(int trades, out Instrument instrument)
    {
        instrument = new Instrument { Symbol = "PREBUILD-" + Guid.NewGuid().ToString("N"), FeedId = "sim", TickSize = 0.25m, VolumeStep = 1 };
        var store = new TradeStore(instrument);
        for (int i = 0; i < trades; i++)
            store.Add(new Trade(i + 1, Start.AddSeconds(i * 7), 100 + (i * 37 % 200) * 0.25m, 1 + i % 3, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        return store;
    }

    private static CandleSeries Expected(TradeStore store, Instrument instrument, ChartPeriod period)
    {
        int step = CandleSeries.BaseStep(0, CandleSeries.SuggestPriceStep(store, instrument, period, 150));
        var series = new CandleSeries(instrument, period, step);
        series.Rebuild(store);
        return series;
    }

    private static string Shape(CandleSeries series) => string.Join(";", series.Candles.Select(c =>
        $"{c.Time:O}|{c.OpenTicks}|{c.HighTicks}|{c.LowTicks}|{c.CloseTicks}|{c.Volume}|{c.Bid}|{c.Ask}|{c.Levels.StoredRowCount}"));

    [Fact]
    public void Neighbours_are_the_adjacent_ladder_periods_only()
    {
        Assert.Equal(new[] { ChartPeriod.H1, ChartPeriod.M5 }, NeighbourSeriesPrebuilder.NeighboursOf(ChartPeriod.M15));
        Assert.Equal(new[] { ChartPeriod.M5 }, NeighbourSeriesPrebuilder.NeighboursOf(ChartPeriod.M1));
        Assert.Equal(new[] { ChartPeriod.H4 }, NeighbourSeriesPrebuilder.NeighboursOf(ChartPeriod.D1));
        Assert.Empty(NeighbourSeriesPrebuilder.NeighboursOf(ChartPeriod.M30));
    }

    [Fact]
    public async Task Background_build_restores_exactly_what_a_foreground_rebuild_produces()
    {
        var store = Store(20_000, out var instrument);
        var cache = new CandleSeriesCache();
        using var builder = new NeighbourSeriesPrebuilder(cache) { MinimumTrades = 0 };
        builder.Request(store, NeighbourSeriesPrebuilder.NeighboursOf(ChartPeriod.M15), 0, 150, null, "scope", store.LastTime);
        await builder.Completion;
        Assert.Equal(2, builder.AdmittedCount);
        foreach (var period in new[] { ChartPeriod.H1, ChartPeriod.M5 })
        {
            var expected = Expected(store, instrument, period);
            var restored = new CandleSeries(instrument, period, expected.PriceStep);
            Assert.True(cache.TryRestore(restored, store, "scope"));
            Assert.False(restored.HasPendingTrades(store));
            Assert.Equal(Shape(expected), Shape(restored));
            Assert.False(cache.TryRestore(new CandleSeries(instrument, period, expected.PriceStep), store, "other-provider"));
        }
    }

    [Fact]
    public async Task Appends_after_capture_are_caught_up_but_a_correction_makes_the_prebuilt_entry_unreachable()
    {
        var store = Store(10_000, out var instrument);
        var cache = new CandleSeriesCache();
        using var builder = new NeighbourSeriesPrebuilder(cache) { MinimumTrades = 0 };
        builder.Request(store, new[] { ChartPeriod.H1 }, 0, 150, null, "scope", store.LastTime);
        await builder.Completion;
        int step = CandleSeries.BaseStep(0, CandleSeries.SuggestPriceStep(store, instrument, ChartPeriod.H1, 150));

        store.Add(new Trade(20_000, store.LastTime.AddSeconds(1), 101, 2, TradeSide.Buy));
        var caughtUp = new CandleSeries(instrument, ChartPeriod.H1, step);
        Assert.True(cache.TryRestore(caughtUp, store, "scope"));
        Assert.True(caughtUp.HasPendingTrades(store));
        caughtUp.Append(store);
        var direct = new CandleSeries(instrument, ChartPeriod.H1, step);
        direct.Rebuild(store);
        Assert.Equal(Shape(direct), Shape(caughtUp));

        var first = store.Get(0);
        store.Update(0, new Trade(first.Id, first.Time, first.Price, first.Volume + 1, first.Side, first.Provenance));
        Assert.False(cache.TryRestore(new CandleSeries(instrument, ChartPeriod.H1, step), store, "scope"));
        Assert.False(cache.Contains(new CandleSeries(instrument, ChartPeriod.H1, step), store, "scope"));
    }

    [Fact]
    public void Speculative_admission_never_evicts_a_used_entry()
    {
        var store = Store(2_000, out var instrument);
        var cache = new CandleSeriesCache(maxEntries: 1);
        var used = new CandleSeries(instrument, ChartPeriod.M5);
        used.Rebuild(store);
        cache.Store(used, store, "scope");
        var view = TradeStoreView.Capture(store);
        var speculative = new CandleSeries(instrument, ChartPeriod.H1);
        Assert.True(speculative.BuildThrough(store, view, CancellationToken.None));
        Assert.False(cache.AdmitPrebuilt(speculative, store, view, "scope"));
        Assert.True(cache.Contains(used, store, "scope"));
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public void A_cancelled_build_stops_before_reading_rows()
    {
        var store = Store(2_000, out var instrument);
        var series = new CandleSeries(instrument, ChartPeriod.H1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => series.BuildThrough(store, TradeStoreView.Capture(store), cancelled.Token));
        Assert.Equal(0, series.ProcessedTrades);
        Assert.Equal(0, series.Count);
    }

    [Fact]
    public void A_correction_during_capture_drops_the_build()
    {
        var store = Store(2_000, out var instrument);
        var view = TradeStoreView.Capture(store);
        var first = store.Get(0);
        store.Update(0, new Trade(first.Id, first.Time, first.Price, first.Volume + 1, first.Side, first.Provenance));
        var series = new CandleSeries(instrument, ChartPeriod.H1);
        Assert.False(series.BuildThrough(store, view, CancellationToken.None));
        Assert.False(new CandleSeriesCache().AdmitPrebuilt(series, store, view, "scope"));
    }

    [Fact]
    public void Small_histories_are_not_built_in_the_background()
    {
        var store = Store(1_000, out _);
        using var builder = new NeighbourSeriesPrebuilder(new CandleSeriesCache());
        builder.Request(store, new[] { ChartPeriod.H1 }, 0, 150, null, "scope", store.LastTime);
        Assert.True(builder.Completion.IsCompleted);
        Assert.Equal(0, builder.AdmittedCount);
    }

    [Fact]
    public async Task Closing_the_chart_cancels_and_joins_its_background_builds()
    {
        var instrument = new Instrument { Symbol = "PREBUILD-CHART-" + Guid.NewGuid().ToString("N"), FeedId = "sim", TickSize = 0.25m, VolumeStep = 1 };
        using var data = new InstrumentData(instrument, new SimulatedFeed());
        for (int i = 0; i < 5_000; i++) data.Trades.Add(new Trade(i + 1, Start.AddSeconds(i * 13), 100 + i % 40 * 0.25m, 1, TradeSide.Buy));
        var model = new ChartModel(data, ChartPeriod.M15, new ChartSettings { TimeZoneId = "UTC" });
        var builds = model.NeighbourBuilds;
        builds.MinimumTrades = 0;
        model.SetPeriod(ChartPeriod.M5);
        var running = builds.Completion;
        model.Dispose();
        Assert.True(running.IsCompleted);
        await running;
        int admitted = builds.AdmittedCount, cancelled = builds.CancelledCount;
        // Requests after close are ignored instead of starting work on a disposed chart.
        builds.Request(data.Trades, new[] { ChartPeriod.H1 }, 0, 150, null, "scope", data.Trades.LastTime);
        Assert.True(builds.Completion.IsCompleted);
        Assert.Equal((admitted, cancelled), (builds.AdmittedCount, builds.CancelledCount));
    }

    [Fact]
    public async Task Rebuilding_on_the_owner_thread_does_not_wait_for_background_builds()
    {
        var instrument = new Instrument { Symbol = "PREBUILD-UI-" + Guid.NewGuid().ToString("N"), FeedId = "sim", TickSize = 0.25m, VolumeStep = 1 };
        using var data = new InstrumentData(instrument, new SimulatedFeed());
        for (int i = 0; i < 5_000; i++) data.Trades.Add(new Trade(i + 1, Start.AddSeconds(i * 13), 100 + i % 40 * 0.25m, 1, TradeSide.Buy));
        using var model = new ChartModel(data, ChartPeriod.M15, new ChartSettings { TimeZoneId = "UTC" });
        model.NeighbourBuilds.MinimumTrades = 0;
        using var gate = new ManualResetEventSlim();
        // Occupy the worker's predecessor so a request cannot finish before Rebuild returns.
        var blocker = Task.Run(gate.Wait);
        var field = typeof(NeighbourSeriesPrebuilder).GetField("_running", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(model.NeighbourBuilds, blocker);
        model.Rebuild();
        Assert.False(model.NeighbourBuilds.Completion.IsCompleted);
        Assert.True(model.Series.Count > 0);
        gate.Set();
        await model.NeighbourBuilds.Completion;
    }
}
