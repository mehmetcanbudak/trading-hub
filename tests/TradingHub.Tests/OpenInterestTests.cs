using System.Net;
using System.Text;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Indicators;
using TradingHub.Market;
using OiStudy = TradingHub.Indicators.Native.OpenInterest;

namespace TradingHub.Tests;

public class OpenInterestTests
{
    private static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static Instrument Instrument(string feed = "oi-test") => new() { FeedId = feed, Symbol = "TESTUSDT", TickSize = 1, VolumeStep = 1 };
    private static Trade Tick(long id, int minute, decimal volume = 2) => new(id, Start.AddMinutes(minute), 100 + minute, volume, TradeSide.Buy);

    [Fact]
    public async Task Binance_uses_server_time_origin_history_path_pagination_clipping_and_retention()
    {
        using var server = new FakeHttpServer();
        server.OnHttp = request =>
        {
            if (request.Route == "/fapi/v1/openInterest")
                return (200, $"{{\"time\":{new DateTimeOffset(Start).ToUnixTimeMilliseconds()},\"openInterest\":\"0\"}}");
            Assert.Equal("/futures/data/openInterestHist", request.Route);
            Assert.Equal("5m", request.Query("period"));
            Assert.Equal("500", request.Query("limit"));
            long from = long.Parse(request.Query("startTime"));
            long to = long.Parse(request.Query("endTime"));
            return (200, $"[{{\"timestamp\":{from},\"sumOpenInterest\":\"12\"}},{{\"timestamp\":{from},\"sumOpenInterest\":\"13\"}},{{\"timestamp\":{to + 1},\"sumOpenInterest\":\"999\"}}]");
        };
        using var feed = new BinanceFeed(true, restOrigin: new Uri(server.Http), utcNow: () => Start.AddDays(31));
        var instrument = Instrument(BinanceFeed.FuturesId);
        var current = await feed.GetOpenInterestAsync(instrument, CancellationToken.None);
        Assert.Equal(Start, current.Time);
        Assert.Equal(0m, current.Quantity);
        var result = await feed.LoadOpenInterestAsync(instrument, Start, Start.AddDays(5), CancellationToken.None);
        Assert.Equal(OpenInterestAvailability.Partial, result.Availability);
        Assert.All(result.Samples, sample => Assert.InRange(sample.Time, Start.AddDays(1), Start.AddDays(5).AddTicks(-1)));
        Assert.Equal(result.Samples.Count, result.Samples.Select(s => s.Time).Distinct().Count());
        Assert.Equal(13m, result.Samples[0].Quantity);
        Assert.True(server.Requests.Count(r => r.Route == "/futures/data/openInterestHist") >= 3);
        var expired = await feed.LoadOpenInterestAsync(instrument, Start.AddDays(-60), Start, CancellationToken.None);
        Assert.Equal(OpenInterestAvailability.Unavailable, expired.Availability);
        using var spot = new BinanceFeed(false, restOrigin: new Uri(server.Http));
        Assert.False(spot.SupportsOpenInterest(instrument));
        Assert.False(feed.SupportsOpenInterest(Instrument(BinanceFeed.SpotId)));
        Assert.Equal(OpenInterestAvailability.Unsupported, (await spot.LoadOpenInterestAsync(instrument, Start, Start.AddHours(1), CancellationToken.None)).Availability);
    }

    [Fact]
    public async Task Held_HTTP_is_cancelled_without_emitting_a_sample()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new HeldHandler(entered, cancelled);
        using var feed = new BinanceFeed(true, handler);
        var observer = new Observer();
        using var subscription = feed.SubscribeOpenInterest(Instrument(BinanceFeed.FuturesId), observer);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        subscription.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(observer.Samples);
    }

    [Fact]
    public async Task Shared_charts_receive_OI_only_after_Pump_without_market_field_changes_and_rebuild_is_stable()
    {
        using var feed = new ControlledFeed { Now = Start.AddMinutes(2).AddSeconds(20) };
        using var data = new InstrumentData(Instrument(), feed);
        data.Start(Start);
        await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        feed.History.SetResult(new[] { Tick(1, 0), Tick(2, 1), Tick(3, 2) });
        await PumpUntil(data, () => !data.IsLoadingHistory);
        using var minute = new ChartModel(data, ChartPeriod.M1);
        using var five = new ChartModel(data, ChartPeriod.M5);
        var study = new OiStudy();
        var slot = minute.AddIndicator(study, applyDefaultColors: false);
        var total = Assert.IsType<OptionalValueDataSeries>(study.DataSeries[0]);
        int changed = 0, trades = 0, depth = 0;
        data.OpenInterestChanged += () => changed++;
        data.TradesAppended += (_, _) => trades++;
        data.DepthUpdated += _ => depth++;
        decimal price = data.LastPrice, volume = minute.Series.Candles.Sum(c => c.Volume);
        var freshness = data.LastDataAt;
        feed.OiObserver!.OnOpenInterest(new(Start.AddSeconds(20), 0));
        feed.OiObserver.OnOpenInterest(new(Start.AddMinutes(1).AddSeconds(10), 10));
        feed.OiObserver.OnOpenInterest(new(Start.AddMinutes(1).AddSeconds(40), 8));
        feed.OiObserver.OnOpenInterest(new(Start.AddMinutes(2).AddSeconds(10), 6));
        Assert.Equal(0, changed);
        Assert.False(total.IsAvailable(0));
        data.Pump();
        Assert.True(changed > 0);
        Assert.Equal(0, trades);
        Assert.Equal(0, depth);
        Assert.Equal(price, data.LastPrice);
        Assert.Equal(freshness, data.LastDataAt);
        Assert.Equal(volume, minute.Series.Candles.Sum(c => c.Volume));
        Assert.True(total.IsAvailable(0));
        Assert.Equal(0m, total[0]);
        Assert.Equal(8m, minute.Series[1].OI);
        Assert.Equal(8m, minute.Series[1].MinOI);
        Assert.Equal(10m, minute.Series[1].MaxOI);
        Assert.Equal(-2m, minute.Series[2].OpenInterestChange);
        Assert.Equal(6m, five.Series[0].OI);
        Assert.Null(slot.Error);
        var snapshot = minute.Series.Candles.Select(c => (c.OI, c.MinOI, c.MaxOI, c.OpenInterestChange)).ToArray();
        minute.Rebuild();
        Assert.Equal(snapshot, minute.Series.Candles.Select(c => (c.OI, c.MinOI, c.MaxOI, c.OpenInterestChange)).ToArray());
        feed.OiObserver.OnOpenInterest(new(Start.AddMinutes(2).AddSeconds(10), 5));
        data.Pump();
        Assert.Equal(-3m, minute.Series[2].OpenInterestChange);
        minute.Rebuild();
        Assert.Equal(-3m, minute.Series[2].OpenInterestChange);
    }

    [Fact]
    public void Mapping_has_no_future_backfill_uses_actual_non_time_timestamps_and_expires()
    {
        var instrument = Instrument();
        var trades = new TradeStore(instrument);
        for (int i = 0; i < 4; i++) trades.Add(Tick(i + 1, i));
        var store = new OpenInterestStore();
        store.Add(new(Start.AddMinutes(1).AddSeconds(30), 0));
        store.Add(new(Start.AddMinutes(2).AddSeconds(40), 9));
        var time = new CandleSeries(instrument, ChartPeriod.M1);
        time.Rebuild(trades);
        store.Apply(time, Start.AddMinutes(3));
        Assert.False(time[0].HasOpenInterest);
        Assert.True(time[1].HasOpenInterest);
        Assert.Equal(0m, time[1].OI);
        Assert.False(time[1].HasOpenInterestChange);
        Assert.Equal(9m, time[2].OpenInterestChange);
        Assert.False(time[3].HasOpenInterestRange);
        var tick = new CandleSeries(instrument, new ChartPeriod(PeriodType.Tick, 1));
        tick.Rebuild(trades);
        store.Apply(tick, Start.AddMinutes(3));
        Assert.False(tick[1].HasOpenInterest); // 12:01 actual trade, not a nominal time-bar close.
        Assert.Equal(0m, tick[2].OI);
        Assert.Equal(9m, tick[3].OI);
        Assert.False(store.TryAsOf(Start.AddHours(1), out _));
        store.Apply(time, Start.AddMinutes(2));
        Assert.Equal(0m, time[2].OI); // future 12:02:40 sample is excluded by data clock.
    }

    [Fact]
    public async Task Stop_restart_dispose_reject_late_generic_history_live_OI_and_publications()
    {
        using var feed = new ControlledFeed();
        using var data = new InstrumentData(Instrument(), feed);
        data.Start(Start);
        await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await feed.OiHistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldSink = feed.Sink!;
        var oldOi = feed.OiObserver!;
        var oldHistory = feed.History;
        var oldOiHistory = feed.OiHistory;
        data.Stop();
        feed.Reset();
        data.Start(Start.AddMinutes(1));
        await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldSink.OnTrades(data.Instrument, new[] { Tick(999, 2) });
        oldSink.OnStatus(data.Instrument, FeedStatus.Error, "stale");
        oldOi.OnOpenInterest(new(Start.AddMinutes(2), 999));
        oldHistory.SetResult(new[] { Tick(998, 2) });
        oldOiHistory.SetResult(new(new[] { new OpenInterestSample(Start, 999) }, Start, Start, OpenInterestAvailability.Available));
        feed.History.SetResult(new[] { Tick(1, 1) });
        feed.OiHistory.SetResult(new(Array.Empty<OpenInterestSample>(), null, null, OpenInterestAvailability.Unavailable));
        await PumpUntil(data, () => !data.IsLoadingHistory);
        Assert.Equal(1, data.Trades.Count);
        Assert.Equal(1, data.Trades.GetId(0));
        Assert.Empty(data.OpenInterest.Samples);
        Assert.NotEqual("stale", data.StatusMessage);
        int events = 0;
        data.OpenInterestChanged += () => events++;
        data.HistoryLoaded += () => events++;
        data.StatusChanged += () => events++;
        feed.OiObserver!.OnOpenInterest(new(Start.AddMinutes(2), 20));
        data.Dispose();
        feed.Sink!.OnTrades(data.Instrument, new[] { Tick(77, 3) });
        data.Pump();
        Assert.Equal(0, events);
        Assert.Equal(1, data.Trades.Count);
    }

    [Fact]
    public async Task Bar_history_merges_chronologically_real_minutes_win_and_synthetic_never_enters_recorder()
    {
        string root = Path.Combine(Path.GetTempPath(), "oi-bar-seam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var instrument = Instrument("ib");
            string path = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start));
            using (var writer = new TickFile.Writer(path, instrument)) writer.Write(Tick(10, 1));
            using var feed = new BarFeed();
            using (var data = new InstrumentData(instrument, feed) { RecordingRoot = root })
            {
                data.Start(Start);
                await PumpUntil(data, () => !data.IsLoadingHistory);
                Assert.Equal(new long[] { -90, 10, -2 }, Enumerable.Range(0, data.Trades.Count).Select(data.Trades.GetId));
                Assert.Equal(Start.AddMinutes(1), data.RealTicksFrom);
                Assert.False(feed.TickHistoryCalled);
                using var model = new ChartModel(data, ChartPeriod.M1);
                Assert.True(model.Series[0].IsSynthetic);
                Assert.True(model.Series[0].Levels.IsEmpty);
                Assert.False(new IndicatorCandle(model.Series[0], model.Series).HasObservedFootprint);
            }
            Assert.Equal(new long[] { 10 }, TickFile.Read(path).Select(t => t.Id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Hub_scopes_separate_profiles_and_same_scope_cannot_silently_rebind()
    {
        using var hub = new MarketDataHub();
        using var first = new BarFeed { DataScopeKey = "profile-a" };
        using var second = new BarFeed { DataScopeKey = "profile-b" };
        using var conflict = new BarFeed { DataScopeKey = "profile-a" };
        var instrument = Instrument("ib");
        var data = hub.GetOrStart(first, instrument, TimeSpan.FromMinutes(3));
        Assert.Same(data, hub.GetOrStart(first, instrument, TimeSpan.FromMinutes(3)));
        Assert.NotSame(data, hub.GetOrStart(second, instrument, TimeSpan.FromMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => hub.GetOrStart(conflict, instrument, TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public async Task Late_cancelled_history_cannot_write_into_the_next_generations_recorder_or_publish_progress()
    {
        string root = Path.Combine(Path.GetTempPath(), "oi-generation-recorder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var feed = new ControlledFeed();
            using var data = new InstrumentData(Instrument(), feed) { RecordingRoot = root };
            data.Start(Start);
            await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldHistory = feed.History;
            data.Stop();
            feed.Reset();
            data.Start(Start);
            await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            oldHistory.SetResult(new[] { Tick(999, 2) });
            feed.History.SetResult(new[] { Tick(1, 1) });
            await PumpUntil(data, () => !data.IsLoadingHistory);
            Assert.Equal(Start.AddMinutes(1), data.RealTicksFrom);
            Assert.Equal(1d, data.HistoryProgress);
            data.Stop(); // Joins the owned recorder and flushes its real ticks.
            var path = TickFile.DayPath(root, data.Instrument.FeedId, data.Instrument.Symbol, DateOnly.FromDateTime(Start));
            Assert.Equal(new long[] { 1 }, TickFile.Read(path).Select(t => t.Id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Provider_timeout_and_invalid_observations_publish_visible_errors_on_Pump()
    {
        using var feed = new ControlledFeed();
        using var data = new InstrumentData(Instrument(), feed);
        data.Start(Start);
        await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await feed.OiHistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        feed.History.SetResult(new[] { Tick(1, 0) });
        feed.OiHistory.SetException(new TaskCanceledException("Fixture HTTP timeout without lifecycle cancellation."));
        await PumpUntil(data, () => data.OpenInterest.Availability == OpenInterestAvailability.Error);
        Assert.Contains("timeout", data.OpenInterest.Message);
        int events = 0;
        data.OpenInterestChanged += () => events++;
        feed.OiObserver!.OnOpenInterest(new(DateTime.SpecifyKind(Start, DateTimeKind.Unspecified), -1));
        Assert.Equal(0, events);
        data.Pump();
        Assert.Equal(1, events);
        Assert.Empty(data.OpenInterest.Samples);
        Assert.Equal(OpenInterestAvailability.Error, data.OpenInterest.Availability);
    }

    [Fact]
    public void Mixed_candle_exposes_observed_rows_without_claiming_complete_footprint()
    {
        var series = new CandleSeries(Instrument(), ChartPeriod.M1);
        var candle = new TradingHub.Data.Candle { Time = Start };
        candle.AddTrade(100, 100, 2, TradeSide.None, Start.Ticks, 0, synthetic: true);
        candle.AddTrade(101, 101, 3, TradeSide.Buy, Start.AddSeconds(1).Ticks, 1);
        var sdk = new IndicatorCandle(candle, series);
        Assert.True(candle.HasSyntheticTrades);
        Assert.False(candle.IsSynthetic);
        Assert.True(sdk.HasObservedFootprint);
        Assert.False(sdk.IsFootprintComplete);
        Assert.Single(sdk.GetAllPriceLevels());
        Assert.Equal(5m, sdk.Volume);
    }

    [Fact]
    public async Task Cancelled_history_extension_cannot_replace_new_range_or_resume_stopped_events()
    {
        using var feed = new ControlledFeed();
        using var data = new InstrumentData(Instrument(), feed);
        data.Start(Start);
        await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await feed.OiHistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        feed.History.SetResult(new[] { Tick(1, 0) });
        feed.OiHistory.SetResult(new(Array.Empty<OpenInterestSample>(), null, null, OpenInterestAvailability.Unavailable));
        await PumpUntil(data, () => !data.IsLoadingHistory);
        feed.Reset();
        Assert.True(data.ExtendHistory(Start.AddHours(-1)));
        await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldExtension = feed.History;
        data.Stop();
        feed.Reset();
        data.Start(Start.AddMinutes(1));
        await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldExtension.SetResult(new[] { Tick(99, -1) });
        feed.History.SetResult(new[] { Tick(2, 1) });
        await PumpUntil(data, () => !data.IsLoadingHistory);
        Assert.Equal(Start.AddMinutes(1), data.HistoryFrom);
        Assert.False(data.IsExtending);
        Assert.Equal(2L, data.Trades.GetId(0));
        Assert.Equal(1, data.Trades.Count);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(32768, true)]
    [InlineData(128, false)]
    [InlineData(32768, false)]
    public async Task Growing_history_updates_preserve_historical_candles_and_do_not_rewind_unrelated_studies(int bars, bool supported)
    {
        using var feed = new ControlledFeed
        {
            Now = Start.AddMinutes(bars - 1).AddSeconds(30),
            OpenInterestSupported = supported
        };
        using var data = new InstrumentData(Instrument(), feed);
        data.Start(Start);
        await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (supported)
        {
            await feed.OiHistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var samples = new[]
            {
                new OpenInterestSample(Start.AddMinutes(bars - 2).AddSeconds(10), 8),
                new OpenInterestSample(Start.AddMinutes(bars - 1).AddSeconds(10), 10)
            };
            feed.OiHistory.SetResult(new(samples, samples[0].Time, samples[^1].Time, OpenInterestAvailability.Available));
        }
        feed.History.SetResult(Enumerable.Range(0, bars).Select(i => Tick(i + 1, i)).ToArray());
        await PumpUntil(data, () => !data.IsLoadingHistory && (!supported || data.OpenInterest.Availability == OpenInterestAvailability.Available));
        using var model = new ChartModel(data, ChartPeriod.M1);
        var study = new OiStudy();
        var probe = new RewindSensitiveStudy();
        model.AddIndicator(study, applyDefaultColors: false);
        model.AddIndicator(probe, applyDefaultColors: false);
        probe.Calculated.Clear();
        model.Series[0].OpenInterestStatus = "Historical mapping must remain untouched.";

        if (supported)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            feed.OiObserver!.OnOpenInterest(new(Start.AddMinutes(bars - 1).AddSeconds(10), 11));
            data.Pump();
            Console.WriteLine($"OI latest-only mapping: bars={bars}, elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(started)}, allocated={GC.GetAllocatedBytesForCurrentThread() - allocated}");
            Assert.Equal(new[] { bars - 1 }, probe.Calculated);
            Assert.Equal(0, probe.Rewinds);
            Assert.Equal(11m, model.Series.Last!.OI);
            Assert.Equal(3m, model.Series.Last.OpenInterestChange);
            Assert.Equal("Historical mapping must remain untouched.", model.Series[0].OpenInterestStatus);
            probe.Calculated.Clear();
        }

        long tradeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        feed.Sink!.OnTrades(data.Instrument, new[] { new Trade(bars + 1, feed.Now.AddSeconds(-5), 200, 1, TradeSide.Buy) });
        data.Pump();
        Console.WriteLine($"Trade-tail OI mapping: bars={bars}, supported={supported}, elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(tradeStarted)}");
        Assert.Equal(new[] { bars - 1 }, probe.Calculated);
        Assert.Equal(0, probe.Rewinds);
        Assert.Equal("Historical mapping must remain untouched.", model.Series[0].OpenInterestStatus);

        if (supported && bars == 128)
        {
            // A genuinely historical observation correction still remaps/recalculates the affected suffix.
            probe.Calculated.Clear();
            feed.OiObserver!.OnOpenInterest(new(Start.AddSeconds(30), 0));
            data.Pump();
            Assert.Equal(0, probe.Calculated[0]);
            Assert.Equal(1, probe.Rewinds);
            Assert.True(model.Series[0].HasOpenInterest);
            Assert.Equal(0m, model.Series[0].OI);
            Assert.NotEqual("Historical mapping must remain untouched.", model.Series[0].OpenInterestStatus);
        }
    }

    [Fact]
    public async Task Scoped_refresh_reloads_only_matching_imports_and_renews_live_recording_without_rebinding()
    {
        string root = Path.Combine(Path.GetTempPath(), "ib-refresh-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var feed = new BarFeed { IsLive = false };
            using var hub = new MarketDataHub { RecordingRoot = root };
            var instrument = Instrument("ib");
            var data = hub.GetOrStart(feed, instrument, TimeSpan.FromMinutes(3));
            string State()
            {
                using var lease = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol);
                string path = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start));
                return $"{feed.RequestState}; recorderLeaseAvailable={lease != null}; " +
                    $"recordedBytes={(File.Exists(path) ? new FileInfo(path).Length : 0)}; recordedTickSize={TickFile.ReadTickSize(path)}";
            }
            await PumpUntil(data, () => !data.IsLoadingHistory, State);
            Assert.Null(data.HistoryError);
            DateTime loadedFrom = data.HistoryFrom;
            Assert.Equal(1, feed.HistoryObserverCount);
            feed.BarHistory = new[] { new Trade(-5, Start, 222, 1, TradeSide.None) };
            feed.Invalidate("OTHER");
            data.Pump();
            Assert.Equal(100m, data.Trades.GetPrice(0));
            feed.Invalidate(instrument.Symbol);
            Assert.False(data.IsLoadingHistory); // A background invalidation cannot restart the consumer directly.
            data.Pump();
            await PumpUntil(data, () => !data.IsLoadingHistory, State);
            Assert.Null(data.HistoryError);
            Assert.Equal(222m, data.Trades.GetPrice(0));
            Assert.Equal(loadedFrom, data.HistoryFrom);
            Assert.Same(data, hub.GetOrStart(feed, instrument, TimeSpan.FromMinutes(3)));

            feed.IsLive = true;
            feed.Invalidate(null);
            data.Pump();
            await PumpUntil(data, () => !data.IsLoadingHistory, State);
            Assert.Null(data.HistoryError);
            using (var unavailableLease = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol))
                Assert.Null(unavailableLease); // The restarted online generation owns the recorder.
            var oldSink = feed.Sink!;
            oldSink.OnTrades(instrument, new[] { new Trade(100, Start.AddMinutes(2).AddSeconds(50), 230, 2, TradeSide.Buy) });
            data.Pump();

            feed.IsLive = false;
            feed.Invalidate(null);
            data.Pump();
            await PumpUntil(data, () => !data.IsLoadingHistory, State);
            Assert.Null(data.HistoryError);
            Assert.Equal(loadedFrom, data.HistoryFrom);
            Assert.Equal(1, feed.ActiveSubscriptions);
            using (var releasedLease = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol))
                Assert.NotNull(releasedLease);
            oldSink.OnTrades(instrument, new[] { new Trade(999, Start.AddMinutes(2).AddSeconds(55), 999, 2, TradeSide.Buy) });
            data.Pump();
            Assert.Contains(Enumerable.Range(0, data.Trades.Count), i => data.Trades.GetId(i) == 100);
            Assert.DoesNotContain(Enumerable.Range(0, data.Trades.Count), i => data.Trades.GetId(i) == 999);
            var path = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start));
            Assert.Equal(new long[] { 100 }, TickFile.Read(path).Select(t => t.Id));
            data.Dispose();
            Assert.Equal(0, feed.HistoryObserverCount);
            Assert.Equal(0, feed.ActiveSubscriptions);
            feed.Invalidate(null);
            data.Pump();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Busy_writer_is_explicitly_read_only_and_fresh_generation_clears_warning_after_release()
    {
        string root = Path.Combine(Path.GetTempPath(), "recorder-busy-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var instrument = Instrument("ib");
            using var feed = new BarFeed();
            using var data = new InstrumentData(instrument, feed) { RecordingRoot = root };
            using var holder = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol);
            Assert.NotNull(holder);
            data.Start(Start);
            await PumpUntil(data, () => !data.IsLoadingHistory);
            using var model = new ChartModel(data, ChartPeriod.M1);
            Assert.True(model.Series.Count > 0);
            Assert.Contains("new ticks are not being saved", data.RecordingWarning ?? "");
            holder.Dispose();
            data.Start(Start);
            await PumpUntil(data, () => !data.IsLoadingHistory);
            Assert.Null(data.RecordingWarning);
            using var ownedLease = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol);
            Assert.Null(ownedLease);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Pump_wait_consumes_ready_UI_publication_before_reporting_an_expired_wait()
    {
        using var feed = new ControlledFeed();
        using var data = new InstrumentData(Instrument(), feed);
        data.Start(Start);
        await feed.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await feed.OiHistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        feed.History.SetResult(new[] { Tick(1, 0) });
        feed.OiHistory.SetResult(new(Array.Empty<OpenInterestSample>(), null, null, OpenInterestAvailability.Unavailable));
        await PumpUntil(data, () => !data.IsLoadingHistory && data.OpenInterest.Availability == OpenInterestAvailability.Unavailable);
        feed.OiObserver!.OnOpenInterest(new(Start.AddSeconds(10), 3));
        Assert.Empty(data.OpenInterest.Samples);
        using var expired = new CancellationTokenSource();
        expired.Cancel();
        await PumpUntil(data, () => data.OpenInterest.Samples.Count == 1, cancellationToken: expired.Token);
        Assert.Equal(3m, data.OpenInterest.Samples[0].Quantity);
        Assert.Equal(1, data.Trades.Count);
    }

    internal static async Task PumpUntil(InstrumentData data, Func<bool> done, Func<string>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            data.Pump();
            if (done()) return;
            try { await Task.Delay(5, timeout.Token); }
            catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
            {
                // A delayed continuation must still consume a completion already queued for the owning UI.
                data.Pump();
                if (done()) return;
                throw new TimeoutException(
                    $"Instrument data did not reach the requested state: instrument={data.Instrument.Key}; " +
                    $"loading={data.IsLoadingHistory}; extending={data.IsExtending}; historyFrom={data.HistoryFrom:O}; " +
                    $"progress={data.HistoryProgress}; historyError={data.HistoryError ?? "<none>"}; trades={data.Trades.Count}; " +
                    $"recordingWarning={data.RecordingWarning ?? "<none>"}; {diagnostics?.Invoke() ?? "no additional fixture state"}", ex);
            }
        }
    }

    private sealed class RewindSensitiveStudy : Indicator
    {
        private int _last = -1;
        public int Rewinds { get; private set; }
        public List<int> Calculated { get; } = new();
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar < _last) Rewinds++;
            _last = bar;
            Calculated.Add(bar);
        }
    }

    private sealed class HeldHandler(TaskCompletionSource entered, TaskCompletionSource cancelled) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8) };
        }
    }
    private sealed class Observer : IOpenInterestObserver
    {
        public List<OpenInterestSample> Samples { get; } = new();
        public void OnOpenInterest(OpenInterestSample sample) => Samples.Add(sample);
        public void OnOpenInterestStatus(OpenInterestAvailability availability, string? message) { }
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class ControlledFeed : IMarketDataFeed, IOpenInterestProvider
    {
        public string Id => "oi-test";
        public string DisplayName => "Controlled OI";
        public bool SupportsHistory => true;
        public bool IsLive => true;
        public DateTime Now { get; set; } = Start.AddMinutes(4);
        public bool OpenInterestSupported { get; init; } = true;
        public IMarketDataSink? Sink;
        public IOpenInterestObserver? OiObserver;
        private readonly object _requests = new();
        private readonly List<TaskCompletionSource<IReadOnlyList<Trade>>> _tradeRequests = new();
        private readonly List<TaskCompletionSource<OpenInterestHistoryResult>> _oiRequests = new();
        public TaskCompletionSource HistoryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OiHistoryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<Trade>> History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OpenInterestHistoryResult> OiHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { HistoryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); OiHistoryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); History = new(TaskCreationOptions.RunContinuationsAsynchronously); OiHistory = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { Instrument() });
        public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            var pending = History;
            lock (_requests) _tradeRequests.Add(pending);
            HistoryEntered.TrySetResult();
            var batch = await pending.Task; // Deliberately ignores cancellation: generation is the consumer's responsibility.
            progress?.Report(0.9);
            onBatch(batch);
        }
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) { Sink = sink; return new Lease(); }
        public bool SupportsOpenInterest(Instrument instrument) => OpenInterestSupported && instrument.FeedId == Id;
        public TimeSpan OpenInterestRetention => TimeSpan.FromDays(30);
        public TimeSpan OpenInterestMaxAge => TimeSpan.FromMinutes(10);
        public Task<OpenInterestHistoryResult> LoadOpenInterestAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
        {
            var pending = OiHistory;
            lock (_requests) _oiRequests.Add(pending);
            OiHistoryEntered.TrySetResult();
            return pending.Task;
        }
        public IDisposable SubscribeOpenInterest(Instrument instrument, IOpenInterestObserver observer) { OiObserver = observer; return new Lease(); }
        public void Dispose()
        {
            lock (_requests)
            {
                foreach (var pending in _tradeRequests) pending.TrySetCanceled();
                foreach (var pending in _oiRequests) pending.TrySetCanceled();
            }
            History.TrySetCanceled();
            OiHistory.TrySetCanceled();
        }
    }
    private sealed class BarFeed : IMarketDataFeed, IBarHistoryFeed, IDataScopeFeed, IHistoryRefreshFeed
    {
        public string Id => "ib";
        public string DisplayName => "Bar fixture";
        public bool SupportsHistory => true;
        public bool IsLive { get; set; } = true;
        public DateTime Now => Start.AddMinutes(3);
        public string DataScopeKey { get; init; } = "bar-fixture";
        public bool TickHistoryCalled;
        private int _subscriptionGeneration, _activeSubscriptions, _historyRequests, _lastRequestedGeneration;
        public int ActiveSubscriptions => Volatile.Read(ref _activeSubscriptions);
        public string RequestState => $"live={IsLive}; subscriptionGeneration={Volatile.Read(ref _subscriptionGeneration)}; " +
            $"activeSubscriptions={ActiveSubscriptions}; historyRequests={Volatile.Read(ref _historyRequests)}; " +
            $"lastHistoryRequestGeneration={Volatile.Read(ref _lastRequestedGeneration)}; historyObservers={HistoryObserverCount}";
        public IMarketDataSink? Sink;
        public IReadOnlyList<Trade> BarHistory { get; set; } = new[] { Tick(-2, 2), Tick(-90, 0), Tick(-50, 1) };
        public event Action<string?>? HistoryInvalidated;
        public int HistoryObserverCount => HistoryInvalidated?.GetInvocationList().Length ?? 0;
        public void Invalidate(string? symbol) => HistoryInvalidated?.Invoke(symbol);
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { Instrument("ib") });
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) { TickHistoryCalled = true; throw new InvalidOperationException("Not tick history."); }
        public Task<IReadOnlyList<Trade>> LoadBarHistoryAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _lastRequestedGeneration, Volatile.Read(ref _subscriptionGeneration));
            Interlocked.Increment(ref _historyRequests);
            return Task.FromResult(BarHistory);
        }
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            Sink = sink;
            Interlocked.Increment(ref _subscriptionGeneration);
            Interlocked.Increment(ref _activeSubscriptions);
            return new SubscriptionLease(this);
        }
        public void Dispose() { }
        private sealed class SubscriptionLease(BarFeed owner) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._activeSubscriptions);
            }
        }
    }
}
