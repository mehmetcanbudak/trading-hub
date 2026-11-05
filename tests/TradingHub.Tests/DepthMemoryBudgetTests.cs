using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class DepthMemoryBudgetTests
{
    private static readonly DateTime Start = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

    private static Instrument CreateInstrument() => new()
    {
        FeedId = "fake", Symbol = "DEPTH-MEMORY-FIXTURE", Exchange = "wide-book-fixture",
        TickSize = 0.25m, VolumeStep = 0.01m,
    };

    private static DepthRecordingOptions Limits(bool enabled = true) => new()
    {
        Enabled = enabled, MaxMemorySamples = 10_000,
        MaxMemoryBytes = 64 * 1024, MaxHistoryBytes = 48 * 1024,
        MaxSelectedHistoryBytes = 64 * 1024, MaxQueuedBytes = 128 * 1024,
        MaxSampleBytes = 32 * 1024, MemoryRetention = TimeSpan.FromHours(6),
        MaxLevelsPerSide = 1000, MaxChangesPerUpdate = 10_000,
    };

    [Fact]
    public async Task Live_and_loaded_histories_retain_labelled_byte_bounded_suffixes_before_the_count_cap()
    {
        using var fixture = new DepthFixture();
        var instrument = CreateInstrument();
        using var writer = new DepthHistory(instrument);
        writer.Start(fixture.Root, Limits(), Start);
        for (int i = 0; i < 12; i++)
        {
            var at = Start.AddMinutes(i);
            writer.Receive(DepthRecordingSizingFixture.WideBook(at, 200, 10, i + 1, 1.25m + i), at);
            AssertWithinBudgets(writer);
            await WaitForBooks(writer, i + 1);
            AssertWithinBudgets(writer);
        }
        writer.Stop(Start.AddMinutes(12));

        Assert.Equal(DepthRecordingState.Stopped, writer.Status.RecordingState);
        Assert.Equal(12L, writer.RecordingStatistics.BookRecords);
        Assert.Equal(0L, writer.MemoryUsage.QueuedBytes);
        Assert.True(writer.MemoryUsage.LiveWasTruncated);
        Assert.Equal(10_000, writer.Options.MaxMemorySamples);
        var live = writer.Query(Start, Start.AddMinutes(12));
        AssertByteBoundedSuffix(live.Samples, 12, writer.MemoryUsage.LiveLimit);
        AssertTruncated(live);
        AssertWithinBudgets(writer);

        using var reader = new DepthHistory(instrument);
        reader.Start(fixture.Root, Limits(enabled: false), Start.AddMonths(2));
        var limited = await reader.LoadAsync(Start, Start.AddMinutes(12));
        Assert.True(limited.IsTruncated);
        Assert.False(string.IsNullOrWhiteSpace(limited.Message));
        AssertByteBoundedSuffix(limited.Samples, 12, reader.MemoryUsage.HistoricalLimit);
        reader.Apply(limited);
        AssertByteBoundedSuffix(reader.Query(Start, Start.AddMinutes(12)).Samples, 12, reader.MemoryUsage.HistoricalLimit);
        AssertTruncated(reader.Query(Start, Start.AddMinutes(12)));
        Assert.InRange(reader.MemoryUsage.HistoricalBytes, 1L, reader.MemoryUsage.HistoricalLimit);
        AssertWithinBudgets(reader);

        // Apply must enforce its own policy even when a different reader supplied a wider load.
        using var widerReader = new DepthHistory(instrument);
        widerReader.Start(fixture.Root, Limits(enabled: false) with { MaxHistoryBytes = 256 * 1024 }, Start.AddMonths(2));
        var complete = await widerReader.LoadAsync(Start, Start.AddMinutes(12));
        Assert.False(complete.IsTruncated);
        Assert.Equal(12, complete.Samples.Count);
        reader.Apply(complete);
        var boundedApply = reader.Query(Start, Start.AddMinutes(12));
        AssertByteBoundedSuffix(boundedApply.Samples, 12, reader.MemoryUsage.HistoricalLimit);
        AssertTruncated(boundedApply);
        AssertWithinBudgets(reader);
    }

    [Fact]
    public async Task The_existing_sample_count_cap_still_applies_when_the_byte_pools_have_room()
    {
        using var fixture = new DepthFixture();
        using var history = new DepthHistory(CreateInstrument());
        history.Start(fixture.Root, Limits() with
        {
            MaxMemorySamples = 2, MaxMemoryBytes = 256 * 1024, MaxHistoryBytes = 256 * 1024,
        }, Start);
        for (int i = 0; i < 5; i++)
        {
            var at = Start.AddMinutes(i);
            history.Receive(DepthRecordingSizingFixture.WideBook(at, 200, 10, i + 1, 1.25m + i), at);
            await WaitForBooks(history, i + 1);
            AssertWithinBudgets(history);
        }

        var query = history.Query(Start, Start.AddMinutes(5));
        Assert.Equal(new long?[] { 4, 5 }, query.Samples.Select(sample => sample.Update.Sequence));
        Assert.Equal(new[] { 4.25m, 5.25m }, query.Samples.Select(sample => sample.Snapshot.BestBidSize));
        Assert.True(query.Samples.Sum(sample => sample.EstimatedMemoryBytes) < history.MemoryUsage.LiveLimit);
        AssertTruncated(query);
        history.Stop(Start.AddMinutes(5));
        Assert.Equal(0L, history.MemoryUsage.QueuedBytes);
        var loaded = await history.LoadAsync(Start, Start.AddMinutes(5));
        Assert.True(loaded.IsTruncated);
        Assert.Equal(new long?[] { 4, 5 }, loaded.Samples.Select(sample => sample.Update.Sequence));
        AssertWithinBudgets(history);
    }

    [Fact]
    public async Task Single_sample_clipping_preserves_exact_best_quotes_source_arrays_and_study_partial_policy()
    {
        using var fixture = new DepthFixture();
        var instrument = CreateInstrument();
        using var feed = new ControlledDepthFeed(instrument) { Now = Start.AddSeconds(-1) };
        using var data = new InstrumentData(instrument, feed)
        {
            RecordingRoot = fixture.Root, RecordLive = false,
            DepthRecording = Limits() with { MaxSampleBytes = 8 * 1024 },
        };
        data.Start(Start.AddMinutes(-1));
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        var source = DepthRecordingSizingFixture.WideBook(Start, 1000, 10, 1, 12.345678901234567890123456789m);
        var originalBids = source.Snapshot.Bids.ToArray();
        var originalAsks = source.Snapshot.Asks.ToArray();
        var originalChanges = source.Changes.ToArray();
        Assert.True(DepthSample.EstimateMemoryBytes(source) > data.DepthHistory.MemoryUsage.SingleSampleLimit);

        feed.Publish(source);
        feed.Publish(new Trade(1, Start, -250m, 1m, TradeSide.None));
        await WaitForBooks(data.DepthHistory, 1);
        feed.Now = Start.AddSeconds(1);
        data.Pump();
        var current = Assert.IsType<DepthSample>(data.DepthHistory.Current);
        AssertClippedSample(current, data.DepthHistory.MemoryUsage.SingleSampleLimit, originalBids, originalAsks, originalChanges);
        AssertWithinBudgets(data.DepthHistory);

        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings { ShowLiquidityHeatmap = false });
        var study = new OrderbookDelta
        {
            WithinTicks = 0, MaxAgeSeconds = 120, AllowPartialBook = true,
            Aggregation = DepthAggregation.SampleAverage,
        };
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
        await Until(() => { model.Update(); return !model.IsLoadingDepthHistory; });
        var sdkQuery = model.MarketDepthInfo.GetHistory(Start, Start.AddSeconds(1));
        var sdkBook = Assert.Single(sdkQuery.Samples, sample => sample.IsUsable);
        AssertClippedSample(sdkBook, data.DepthHistory.MemoryUsage.SingleSampleLimit, originalBids, originalAsks, originalChanges);
        var measurement = Assert.IsType<DepthMeasurement>(study.GetMeasurement(0));
        Assert.True(measurement.IsAvailable);
        Assert.Equal((12.345678901234567890123456789m, 2.50m), (measurement.Bid, measurement.Ask));
        Assert.True(measurement.Coverage.HasFlag(DepthCoverage.PartialBook));
        Assert.True(measurement.Coverage.HasFlag(DepthCoverage.Truncated));
        var values = Assert.IsType<OptionalValueDataSeries>(study.DataSeries[0]);
        Assert.True(values.IsAvailable(0));
        Assert.Equal(9.845678901234567890123456789m, values[0]);

        study.AllowPartialBook = false;
        model.Update();
        Assert.False(values.IsAvailable(0));
        Assert.False(Assert.IsType<DepthMeasurement>(study.GetMeasurement(0)).IsAvailable);
        Assert.Equal(originalBids, source.Snapshot.Bids);
        Assert.Equal(originalAsks, source.Snapshot.Asks);
        Assert.Equal(originalChanges, source.Changes.ToArray());

        data.Stop();
        Assert.Equal(0L, data.DepthHistory.MemoryUsage.QueuedBytes);
        var saved = await data.DepthHistory.LoadAsync(Start, Start.AddSeconds(1));
        AssertClippedSample(Assert.Single(saved.Samples, sample => sample.IsUsable),
            data.DepthHistory.MemoryUsage.SingleSampleLimit, originalBids, originalAsks, originalChanges);
        AssertWithinBudgets(data.DepthHistory);
    }

    [Fact]
    public async Task A_reader_with_a_lower_sample_budget_exposes_saved_wide_books_as_missing_truncated_markers()
    {
        using var fixture = new DepthFixture();
        var instrument = CreateInstrument();
        using (var writer = new DepthHistory(instrument))
        {
            writer.Start(fixture.Root, Limits(), Start);
            for (int i = 0; i < 3; i++)
            {
                var at = Start.AddMinutes(i);
                writer.Receive(DepthRecordingSizingFixture.WideBook(at, 200, 10, i + 1), at);
                await WaitForBooks(writer, i + 1);
            }
            writer.Stop(Start.AddMinutes(3));
            Assert.Equal(0L, writer.MemoryUsage.QueuedBytes);
        }

        using var reader = new DepthHistory(instrument);
        reader.Start(fixture.Root, Limits(enabled: false) with { MaxSampleBytes = 8 * 1024 }, Start.AddMonths(2));
        var load = await reader.LoadAsync(Start, Start.AddMinutes(3));
        Assert.True(load.IsTruncated);
        Assert.False(string.IsNullOrWhiteSpace(load.Message));
        Assert.Equal(new[] { Start, Start.AddMinutes(1), Start.AddMinutes(2) }, load.Samples.Select(sample => sample.Time));
        Assert.Equal(new long[] { 1, 2, 3 }, load.Samples.Select(sample => sample.Ordinal));
        Assert.All(load.Samples, sample => AssertMissingMarker(sample, reader.MemoryUsage.SingleSampleLimit));
        Assert.InRange(load.Samples.Sum(sample => sample.EstimatedMemoryBytes), 1L, reader.MemoryUsage.HistoricalLimit);
        reader.Apply(load);
        var query = reader.Query(Start, Start.AddMinutes(3));
        AssertTruncated(query);
        Assert.Equal(3, query.Samples.Count);
        Assert.All(query.Samples, sample => AssertMissingMarker(sample, reader.MemoryUsage.SingleSampleLimit));
        Assert.False(DepthStatistics.Measure(query, 0, DepthAggregation.SampleAverage, TimeSpan.FromMinutes(2), allowPartial: true).IsAvailable);
        Assert.InRange(reader.MemoryUsage.HistoricalBytes, 1L, reader.MemoryUsage.HistoricalLimit);
        AssertWithinBudgets(reader);
    }

    [Fact]
    public async Task Oversized_identity_headers_fail_visibly_without_expanding_the_encoding_pool_or_changing_source_identity()
    {
        using var fixture = new DepthFixture();
        string symbol = new('界', 1024);
        var instrument = new Instrument { FeedId = "fake", Symbol = symbol, Exchange = "wide-book-fixture", TickSize = 0.25m };
        using var history = new DepthHistory(instrument);
        history.Start(fixture.Root, Limits() with { MaxSampleBytes = 1024 }, Start);
        history.Receive(DepthRecordingSizingFixture.WideBook(Start, 1, 0, 1), Start);
        await Until(() => history.Status.RecordingState == DepthRecordingState.Error && history.MemoryUsage.QueuedBytes == 0);

        Assert.Contains("identity header", history.Status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(symbol, instrument.Symbol);
        Assert.Equal(0L, history.RecordingStatistics.BookRecords);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.depth", SearchOption.AllDirectories));
        AssertWithinBudgets(history);
    }

    [Fact]
    public async Task Bursts_remain_within_the_queue_byte_pool_and_stop_releases_all_queued_and_in_flight_bytes()
    {
        using var fixture = new DepthFixture();
        using var history = new DepthHistory(CreateInstrument());
        history.Start(fixture.Root, Limits() with { QueueCapacity = 4096 }, Start);
        for (int i = 0; i < 64; i++)
        {
            var at = Start.AddMilliseconds(i);
            history.Receive(DepthRecordingSizingFixture.WideBook(at, 200, 10, i + 1), at);
            AssertWithinBudgets(history);
        }
        history.Stop(Start.AddSeconds(1));

        Assert.Equal(DepthRecordingState.Stopped, history.Status.RecordingState);
        Assert.Equal(0L, history.MemoryUsage.QueuedBytes);
        Assert.True(history.MemoryUsage.LiveWasTruncated);
        AssertTruncated(history.Query(Start, Start.AddSeconds(1)));
        AssertWithinBudgets(history);
        if (history.Status.DroppedRecords > 0)
        {
            using var reader = new DepthHistory(CreateInstrument());
            reader.Start(fixture.Root, Limits(enabled: false) with { MaxHistoryBytes = 2 * 1024 * 1024 }, Start.AddSeconds(2));
            var persisted = await reader.LoadAsync(Start, Start.AddSeconds(2));
            var loss = persisted.Samples.FirstOrDefault(sample =>
                sample.GapBefore == DepthGapReason.Overflow && !sample.IsUsable && sample.Time < Start.AddSeconds(1));
            Assert.NotNull(loss);
            var nextBook = persisted.Samples.FirstOrDefault(sample => sample.IsUsable && sample.Time > loss.Time);
            var end = nextBook?.Time ?? Start.AddSeconds(1);
            var missing = new DepthHistoryQuery(loss.Time, end,
                persisted.Samples.Where(sample => sample.Time >= loss.Time && sample.Time < end).ToArray(),
                persisted.Samples.LastOrDefault(sample => sample.Time < loss.Time), DepthCoverage.ReceivedChanges, null);
            var measurement = DepthStatistics.Measure(missing, 0, DepthAggregation.DurationAverage, TimeSpan.FromMinutes(1));
            Assert.False(measurement.IsAvailable);
            Assert.Equal(TimeSpan.Zero, measurement.CoveredDuration);
            Assert.True(measurement.Coverage.HasFlag(DepthCoverage.Missing));
        }
    }

    [Fact]
    public async Task Disabled_recording_still_bounds_the_UI_depth_queue_and_labels_lost_callbacks_without_poisoning_the_provider_book()
    {
        using var fixture = new DepthFixture();
        var instrument = CreateInstrument();
        using var feed = new ControlledDepthFeed(instrument) { Now = Start.AddSeconds(-1) };
        using var data = new InstrumentData(instrument, feed)
        {
            RecordingRoot = fixture.Root, RecordLive = false, DepthRecording = Limits(enabled: false),
        };
        data.Start(Start.AddMinutes(-1));
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        using var chart = new ChartModel(data, ChartPeriod.M1, new ChartSettings { ShowLiquidityHeatmap = false });
        var consumer = new DepthCapture();
        Assert.Null(chart.AddIndicator(consumer, applyDefaultColors: false).Error);
        Assert.Equal(0L, data.QueuedDepthBytes);

        // Forty messages cannot hit the 2,048-update count limit, but these exact
        // hundred-level books exceed the configured 128 KiB UI byte pool together.
        for (int i = 0; i < 40; i++)
        {
            feed.Publish(DepthRecordingSizingFixture.WideBook(Start.AddMilliseconds(i), 100, 10, i + 1, 1.25m + i));
            Assert.InRange(data.QueuedDepthBytes, 0L, data.DepthQueueByteLimit);
            AssertWithinBudgets(data.DepthHistory);
        }
        Assert.Empty(consumer.Updates);
        Assert.InRange(data.QueuedDepthBytes, 1L, data.DepthQueueByteLimit);
        var providerBeforePump = Assert.IsType<DepthSample>(data.DepthHistory.Current);
        Assert.True(providerBeforePump.IsUsable);
        Assert.Equal(40.25m, providerBeforePump.Snapshot.BestBidSize);
        Assert.Equal(100, providerBeforePump.Snapshot.Bids.Length);
        Assert.Equal(100, providerBeforePump.Snapshot.Asks.Length);
        Assert.False(providerBeforePump.Coverage.HasFlag(DepthCoverage.Truncated));
        Assert.Equal(DepthGapReason.None, providerBeforePump.GapBefore);

        long beforeLowering = data.QueuedDepthBytes;
        data.ConfigureDepthRecording(Limits(enabled: false) with { MaxQueuedBytes = 64 * 1024, MaxSampleBytes = 8 * 1024 });
        Assert.True(beforeLowering > data.DepthQueueByteLimit);
        Assert.InRange(data.QueuedDepthBytes, 1L, data.DepthQueueByteLimit);
        Assert.True(data.QueuedDepthBytes < beforeLowering);
        Assert.Equal(40.25m, data.DepthHistory.Current!.Snapshot.BestBidSize);

        data.Pump();
        Assert.Equal(0L, data.QueuedDepthBytes);
        var delivered = consumer.Updates.Where(update => update.Snapshot.Bids.Length > 0).ToArray();
        Assert.InRange(delivered.Length, 1, 39);
        Assert.Equal(Enumerable.Range(41 - delivered.Length, delivered.Length).Select(sequence => (long?)sequence),
            delivered.Select(update => update.Sequence));
        Assert.Equal(DepthGapReason.Overflow, delivered[0].GapBefore);
        Assert.True(delivered[0].Coverage.HasFlag(DepthCoverage.Truncated));
        Assert.Equal(40.25m, delivered[^1].Snapshot.BestBidSize);
        var providerAfterPump = Assert.IsType<DepthSample>(chart.MarketDepthInfo.Current);
        Assert.True(providerAfterPump.IsUsable);
        Assert.Equal(100, providerAfterPump.Snapshot.Bids.Length);
        Assert.Equal(100, providerAfterPump.Snapshot.Asks.Length);
        Assert.Equal((-1000L, -999L, 40.25m, 2.50m),
            (providerAfterPump.Snapshot.BestBidTicks, providerAfterPump.Snapshot.BestAskTicks,
                providerAfterPump.Snapshot.BestBidSize, providerAfterPump.Snapshot.BestAskSize));
        Assert.False(providerAfterPump.Coverage.HasFlag(DepthCoverage.Truncated));
        Assert.Equal(DepthGapReason.None, providerAfterPump.GapBefore);
        Assert.Equal(DepthRecordingState.Disabled, data.DepthHistory.Status.RecordingState);
        Assert.Equal(0L, data.DepthHistory.RecordingStatistics.BookRecords);
        Assert.Equal(0L, data.DepthHistory.MemoryUsage.QueuedBytes);
        AssertWithinBudgets(data.DepthHistory);
    }

    [Fact]
    public async Task Lowering_the_single_sample_limit_releases_old_UI_books_and_does_not_poison_a_new_small_provider_book()
    {
        using var fixture = new DepthFixture();
        var instrument = CreateInstrument();
        using var feed = new ControlledDepthFeed(instrument) { Now = Start.AddSeconds(-1) };
        using var data = new InstrumentData(instrument, feed)
        {
            RecordingRoot = fixture.Root, RecordLive = false, DepthRecording = Limits(enabled: false),
        };
        data.Start(Start.AddMinutes(-1));
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        using var chart = new ChartModel(data, ChartPeriod.M1, new ChartSettings { ShowLiquidityHeatmap = false });
        var capture = new DepthCapture();
        Assert.Null(chart.AddIndicator(capture, applyDefaultColors: false).Error);
        for (int i = 0; i < 10; i++)
            feed.Publish(DepthRecordingSizingFixture.WideBook(Start.AddMilliseconds(i), 100, 10, i + 1));
        Assert.InRange(data.QueuedDepthBytes, 1L, data.DepthQueueByteLimit);

        data.ConfigureDepthRecording(Limits(enabled: false) with { MaxQueuedBytes = 64 * 1024, MaxSampleBytes = 1024 });
        Assert.Equal(1024L, data.DepthHistory.Options.MaxSampleBytes);
        Assert.Equal(0L, data.QueuedDepthBytes);
        Assert.Empty(capture.Updates);
        feed.Publish(DepthRecordingSizingFixture.WideBook(Start.AddSeconds(1), 1, 0, 11, 12.25m));
        Assert.InRange(data.QueuedDepthBytes, 1L, data.DepthHistory.Options.MaxSampleBytes);
        data.Pump();

        var delivered = Assert.Single(capture.Updates);
        Assert.Single(delivered.Snapshot.Bids);
        Assert.Single(delivered.Snapshot.Asks);
        Assert.InRange(DepthSample.EstimateMemoryBytes(delivered), 1L, data.DepthHistory.Options.MaxSampleBytes);
        Assert.Equal(DepthGapReason.Overflow, delivered.GapBefore);
        Assert.True(delivered.Coverage.HasFlag(DepthCoverage.Truncated));
        Assert.Equal(12.25m, delivered.Snapshot.BestBidSize);
        var current = Assert.IsType<DepthSample>(chart.MarketDepthInfo.Current);
        Assert.True(current.IsUsable);
        Assert.Equal(DepthGapReason.None, current.GapBefore);
        Assert.False(current.Coverage.HasFlag(DepthCoverage.Truncated));
        Assert.Equal((12.25m, 2.50m), (data.Depth.BestBidSize, data.Depth.BestAskSize));
        Assert.Equal(0L, data.QueuedDepthBytes);
        AssertWithinBudgets(data.DepthHistory);
    }

    [Fact]
    public async Task A_complete_narrow_chart_selection_supersedes_unrelated_broad_load_truncation_and_warning()
    {
        using var fixture = new DepthFixture();
        var instrument = CreateInstrument();
        using (var writer = new DepthHistory(instrument))
        {
            writer.Start(fixture.Root, Limits(), Start);
            for (int i = 0; i < 12; i++)
            {
                var at = Start.AddMinutes(i);
                writer.Receive(DepthRecordingSizingFixture.WideBook(at, 200, 10, i + 1, 1.25m + i), at);
                await WaitForBooks(writer, i + 1);
            }
            writer.Stop(Start.AddMinutes(12));
        }
        using var feed = new ControlledDepthFeed(instrument) { Now = Start.AddMonths(2) };
        using var data = new InstrumentData(instrument, feed) { RecordingRoot = fixture.Root, RecordLive = false };
        data.DepthHistory.Start(fixture.Root, Limits(enabled: false), feed.Now);
        var broad = await data.DepthHistory.LoadAsync(Start, Start.AddMinutes(12));
        Assert.True(broad.IsTruncated);
        Assert.False(string.IsNullOrWhiteSpace(broad.Message));
        data.DepthHistory.Apply(broad);
        using var chart = new ChartModel(data, ChartPeriod.M1, new ChartSettings { ShowLiquidityHeatmap = false });
        var from = Start.AddMinutes(2);
        var to = Start.AddMinutes(4);
        chart.EnsureDepthHistory(from, to);
        await Until(() => { chart.Update(); return !chart.IsLoadingDepthHistory; });

        foreach (var query in new[] { chart.GetDepthHistory(from, to), chart.MarketDepthInfo.GetHistory(from, to) })
        {
            Assert.False(query.IsTruncated);
            Assert.Null(query.Message);
            Assert.Equal(new long?[] { 3, 4 }, query.Samples.Select(sample => sample.Update.Sequence));
            Assert.Equal(DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook, query.Coverage);
            var measured = DepthStatistics.Measure(query, 0, DepthAggregation.DurationAverage, TimeSpan.FromMinutes(2), allowPartial: true);
            Assert.True(measured.IsAvailable);
            Assert.Equal((3.75m, 2.50m), (measured.Bid, measured.Ask));
            Assert.Equal(TimeSpan.FromMinutes(2), measured.CoveredDuration);
            Assert.False(measured.Coverage.HasFlag(DepthCoverage.Missing));
            var heatmap = LiquidityHeatmapRenderer.BuildFrame(query, 1, TimeSpan.FromMinutes(2), -1000m, -999m);
            Assert.False(heatmap.IsTruncated);
            Assert.Empty(heatmap.Gaps);
            Assert.Equal(4, heatmap.Cells.Count);
        }
        AssertTruncated(chart.GetDepthHistory(Start, Start.AddMinutes(12)));
        Assert.False(chart.GetDepthHistory(from, to).IsTruncated);
        Assert.Null(chart.GetDepthHistory(from, to).Message);
        AssertTruncated(data.DepthHistory.Query(Start, Start.AddMinutes(12)));
        AssertWithinBudgets(data.DepthHistory);
    }

    [Fact]
    public async Task A_live_book_at_the_interval_start_does_not_inherit_unused_historical_carry_faults()
    {
        using var fixture = new DepthFixture();
        var instrument = CreateInstrument();
        using (var writer = new DepthHistory(instrument))
        {
            writer.Start(fixture.Root, Limits(), Start);
            for (int i = 0; i < 12; i++)
            {
                var at = Start.AddMinutes(i);
                writer.Receive(DepthRecordingSizingFixture.WideBook(at, 200, 10, i + 1), at);
                await WaitForBooks(writer, i + 1);
            }
            writer.Stop(Start.AddMinutes(12));
        }
        var from = Start.AddDays(1);
        using var history = new DepthHistory(instrument);
        history.Start(fixture.Root, Limits(), from);
        var broad = await history.LoadAsync(Start, Start.AddMinutes(12));
        Assert.True(broad.IsTruncated);
        Assert.False(string.IsNullOrWhiteSpace(broad.Message));
        history.Apply(broad);
        using var selection = history.CreateSelection();
        Assert.True((await selection.LoadAsync(Start, Start.AddMinutes(12))).IsTruncated);
        history.Receive(DepthRecordingSizingFixture.WideBook(from, 1, 0, 100, 12.25m), from);
        history.Refresh(from.AddSeconds(1));

        foreach (var query in new[] { history.Query(from, from.AddSeconds(1)), selection.Query(from, from.AddSeconds(1)) })
        {
            Assert.NotNull(query.Preceding);
            Assert.True(query.Preceding.Time < from);
            Assert.Equal(from, Assert.Single(query.Samples).Time);
            Assert.False(query.IsTruncated);
            Assert.Null(query.Message);
            Assert.Equal(DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook, query.Coverage);
            var measured = DepthStatistics.Measure(query, 0, DepthAggregation.DurationAverage, TimeSpan.FromSeconds(10), allowPartial: true);
            Assert.True(measured.IsAvailable);
            Assert.Equal((12.25m, 2.50m), (measured.Bid, measured.Ask));
            Assert.Equal(TimeSpan.FromSeconds(1), measured.CoveredDuration);
            Assert.False(measured.Coverage.HasFlag(DepthCoverage.Missing));
        }
        history.Stop(from.AddSeconds(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_oversized_frame_cannot_publish_a_marker_from_an_unverified_timestamp_or_invalid_ordinal(bool invalidOrdinal)
    {
        using var fixture = new DepthFixture();
        var instrument = CreateInstrument();
        using (var writer = new DepthHistory(instrument))
        {
            writer.Start(fixture.Root, Limits(), Start);
            writer.Receive(DepthRecordingSizingFixture.WideBook(Start, 1, 0, 1), Start);
            await WaitForBooks(writer, 1);
            writer.Receive(DepthRecordingSizingFixture.WideBook(Start.AddSeconds(1), 200, 10, 2), Start.AddSeconds(1));
            await WaitForBooks(writer, 2);
            writer.Receive(DepthRecordingSizingFixture.WideBook(Start.AddSeconds(2), 1, 0, 3), Start.AddSeconds(2));
            writer.Stop(Start.AddSeconds(3));
        }
        string segment = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.depth", SearchOption.AllDirectories));
        using (var file = new FileStream(segment, FileMode.Open, FileAccess.ReadWrite))
        using (var fields = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            fields.ReadInt32(); fields.ReadInt32();
            Assert.Equal(instrument.FeedId, fields.ReadString());
            Assert.Equal(instrument.Symbol, fields.ReadString());
            Assert.Equal(instrument.TickSize, fields.ReadDecimal());
            int firstLength = fields.ReadInt32(); fields.ReadUInt32(); file.Position += firstLength;
            long frameAt = file.Position;
            int length = fields.ReadInt32(); fields.ReadUInt32();
            long payloadAt = file.Position;
            byte[] payload = fields.ReadBytes(length);
            Assert.True(length > 1536);
            Assert.Equal(length, payload.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(invalidOrdinal ? 16 : 24),
                invalidOrdinal ? -1L : Start.AddSeconds(5).Ticks);
            using var rewrite = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
            if (invalidOrdinal) { file.Position = frameAt + sizeof(int); rewrite.Write(FrameChecksum(payload)); }
            file.Position = payloadAt; rewrite.Write(payload); rewrite.Flush();
        }

        using var reader = new DepthHistory(instrument);
        reader.Start(fixture.Root, Limits(enabled: false) with { MaxSampleBytes = 1024 }, Start.AddSeconds(6));
        var loaded = await reader.LoadAsync(Start, Start.AddSeconds(6));
        Assert.Contains(invalidOrdinal ? "ordering" : "damaged", loaded.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(loaded.IsTruncated);
        var verified = Assert.Single(loaded.Samples);
        Assert.Equal(Start, verified.Time);
        Assert.Equal(1L, verified.Ordinal);
        Assert.True(verified.IsUsable);
        Assert.DoesNotContain(loaded.Samples, sample => sample.Time == Start.AddSeconds(5));
        reader.Apply(loaded);
        Assert.True(reader.Query(Start, Start.AddSeconds(6)).Coverage.HasFlag(DepthCoverage.Missing));
        AssertWithinBudgets(reader);
    }

    [Fact]
    public async Task Independent_chart_selections_share_one_byte_pool_evict_the_least_recent_view_and_release_on_dispose()
    {
        using var fixture = new DepthFixture();
        var instrument = CreateInstrument();
        var dayA = Start;
        var dayB = Start.AddDays(1);
        var dayC = Start.AddDays(2);
        using (var writer = new DepthHistory(instrument))
        {
            writer.Start(fixture.Root, Limits(), dayA);
            long sequence = 0;
            foreach (var (day, count, size) in new[] { (dayA, 12, 1.25m), (dayB, 2, 11.25m), (dayC, 2, 21.25m) })
            {
                for (int minute = 0; minute < count; minute++)
                {
                    var at = day.AddMinutes(minute);
                    writer.Receive(DepthRecordingSizingFixture.WideBook(at, 200, 10, ++sequence, size), at);
                    await WaitForBooks(writer, sequence);
                    AssertWithinBudgets(writer);
                }
                if (day == dayA)
                {
                    Assert.True(writer.MemoryUsage.LiveWasTruncated);
                    AssertTruncated(writer.Query(dayA, dayA.AddMinutes(12)));
                }
            }
            writer.Stop(dayC.AddMinutes(2));
            Assert.Equal(16L, writer.RecordingStatistics.BookRecords);
            Assert.Equal(0L, writer.MemoryUsage.QueuedBytes);
        }

        using var feed = new ControlledDepthFeed(instrument) { Now = Start.AddMonths(2) };
        using var data = new InstrumentData(instrument, feed) { RecordingRoot = fixture.Root, RecordLive = false };
        data.DepthHistory.Start(fixture.Root, Limits(enabled: false), feed.Now);
        using var first = new ChartModel(data, ChartPeriod.M1, new ChartSettings { ShowLiquidityHeatmap = false });
        using var second = new ChartModel(data, ChartPeriod.M1, new ChartSettings { ShowLiquidityHeatmap = false });
        using var third = new ChartModel(data, ChartPeriod.M1, new ChartSettings { ShowLiquidityHeatmap = false });
        first.EnsureDepthHistory(dayA, dayA.AddMinutes(2));
        second.EnsureDepthHistory(dayB, dayB.AddMinutes(2));
        await Until(() =>
        {
            first.Update(); second.Update();
            return !first.IsLoadingDepthHistory && !second.IsLoadingDepthHistory;
        });
        Assert.Empty(first.GetDepthHistory(dayB, dayB.AddMinutes(2)).Samples);
        Assert.Empty(second.GetDepthHistory(dayA, dayA.AddMinutes(2)).Samples);
        Assert.Empty(data.DepthHistory.Query(dayA, dayC.AddMinutes(2)).Samples);
        AssertSelection(first, dayA, 1.25m);
        AssertSelection(second, dayB, 11.25m);
        Assert.Equal(3, data.DepthHistory.MemoryUsage.SelectedViews);
        Assert.InRange(data.DepthHistory.MemoryUsage.SelectedBytes, 1L, data.DepthHistory.MemoryUsage.SelectedLimit);
        AssertWithinBudgets(data.DepthHistory);

        // The preceding actual chart query makes second more recent than first.
        third.EnsureDepthHistory(dayC, dayC.AddMinutes(2));
        await Until(() =>
        {
            first.Update(); second.Update(); third.Update();
            return !third.IsLoadingDepthHistory;
        });
        var evicted = first.GetDepthHistory(dayA, dayA.AddMinutes(2));
        Assert.Empty(evicted.Samples);
        Assert.Null(evicted.Preceding);
        AssertTruncated(evicted);
        Assert.False(string.IsNullOrWhiteSpace(evicted.Message));
        var sdkEvicted = first.MarketDepthInfo.GetHistory(dayA, dayA.AddMinutes(2));
        Assert.Empty(sdkEvicted.Samples);
        AssertTruncated(sdkEvicted);
        AssertSelection(second, dayB, 11.25m);
        AssertSelection(third, dayC, 21.25m);
        Assert.Empty(second.GetDepthHistory(dayC, dayC.AddMinutes(2)).Samples);
        Assert.Empty(third.GetDepthHistory(dayB, dayB.AddMinutes(2)).Samples);
        AssertWithinBudgets(data.DepthHistory);

        long retained = data.DepthHistory.MemoryUsage.SelectedBytes;
        first.Dispose();
        Assert.Equal(retained, data.DepthHistory.MemoryUsage.SelectedBytes);
        Assert.Equal(2, data.DepthHistory.MemoryUsage.SelectedViews);
        second.Dispose();
        Assert.InRange(data.DepthHistory.MemoryUsage.SelectedBytes, 1L, retained - 1);
        Assert.Equal(1, data.DepthHistory.MemoryUsage.SelectedViews);
        AssertSelection(third, dayC, 21.25m);
        third.Dispose();
        Assert.Equal(0L, data.DepthHistory.MemoryUsage.SelectedBytes);
        Assert.Equal(0, data.DepthHistory.MemoryUsage.SelectedViews);
        AssertWithinBudgets(data.DepthHistory);
    }

    [Fact]
    public void The_checked_sample_budget_cannot_outgrow_its_live_history_selection_or_queue_pools()
    {
        using var fixture = new DepthFixture();
        using var history = new DepthHistory(CreateInstrument());
        history.Start(fixture.Root, Limits(enabled: false) with { MaxSampleBytes = long.MaxValue }, Start);
        var usage = history.MemoryUsage;
        Assert.InRange(usage.SingleSampleLimit, 1L, usage.LiveLimit);
        Assert.InRange(usage.SingleSampleLimit, 1L, usage.HistoricalLimit);
        Assert.InRange(usage.SingleSampleLimit, 1L, usage.SelectedLimit);
        Assert.InRange(usage.SingleSampleLimit, 1L, usage.QueueLimit / 2);
        var source = DepthRecordingSizingFixture.WideBook(Start, 1000, 10, 1);
        Assert.True(DepthSample.EstimateMemoryBytes(source) > usage.SingleSampleLimit);
        history.Receive(source, Start);
        var current = Assert.IsType<DepthSample>(history.Current);
        Assert.InRange(current.EstimatedMemoryBytes, 1L, usage.SingleSampleLimit);
        Assert.True(current.Coverage.HasFlag(DepthCoverage.Truncated));
        AssertWithinBudgets(history);
    }

    private static uint FrameChecksum(ReadOnlySpan<byte> payload)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in payload)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u;
        }
        return ~crc;
    }

    private static void AssertByteBoundedSuffix(IReadOnlyList<DepthSample> samples, int submitted, long limit)
    {
        Assert.InRange(samples.Count, 1, submitted - 1);
        int first = submitted - samples.Count;
        Assert.Equal(Enumerable.Range(first, samples.Count).Select(i => (long?)(i + 1)), samples.Select(sample => sample.Update.Sequence));
        Assert.Equal(Enumerable.Range(first, samples.Count).Select(i => 1.25m + i), samples.Select(sample => sample.Snapshot.BestBidSize));
        Assert.All(samples, AssertWideBook);
        Assert.InRange(samples.Sum(sample => sample.EstimatedMemoryBytes), 1L, limit);
    }

    private static void AssertWideBook(DepthSample sample)
    {
        Assert.True(sample.IsUsable);
        Assert.Equal(200, sample.Snapshot.Bids.Length);
        Assert.Equal(200, sample.Snapshot.Asks.Length);
        Assert.Equal(10, sample.Update.Changes.Count);
        Assert.Equal(-1000L, sample.Snapshot.BestBidTicks);
        Assert.Equal(-999L, sample.Snapshot.BestAskTicks);
        Assert.Equal(2.50m, sample.Snapshot.BestAskSize);
        Assert.Equal("fake", sample.ProviderId);
        Assert.Equal("wide-book-fixture", sample.Venue);
        Assert.True(sample.Coverage.HasFlag(DepthCoverage.ReceivedChanges));
        Assert.True(sample.Coverage.HasFlag(DepthCoverage.PartialBook));
        Assert.False(sample.Coverage.HasFlag(DepthCoverage.Truncated));
        Assert.Equal(DepthGapReason.None, sample.GapBefore);
    }

    private static void AssertSelection(ChartModel chart, DateTime from, decimal bid)
    {
        var query = chart.GetDepthHistory(from, from.AddMinutes(2));
        Assert.False(query.IsTruncated);
        Assert.Equal(new[] { from, from.AddMinutes(1) }, query.Samples.Select(sample => sample.Time));
        Assert.Equal(new[] { bid, bid }, query.Samples.Select(sample => sample.Snapshot.BestBidSize));
        Assert.All(query.Samples, AssertWideBook);
        var measurement = DepthStatistics.Measure(chart.MarketDepthInfo.GetHistory(from, from.AddMinutes(2)),
            0, DepthAggregation.SampleAverage, TimeSpan.FromMinutes(2), allowPartial: true);
        Assert.True(measurement.IsAvailable);
        Assert.Equal((bid, 2.50m), (measurement.Bid, measurement.Ask));
    }

    private static void AssertClippedSample(DepthSample sample, long limit, BookLevel[] originalBids,
        BookLevel[] originalAsks, BookChange[] originalChanges)
    {
        Assert.True(sample.IsUsable);
        Assert.InRange(sample.EstimatedMemoryBytes, 1L, limit);
        Assert.InRange(sample.Snapshot.Bids.Length, 1, originalBids.Length - 1);
        Assert.InRange(sample.Snapshot.Asks.Length, 1, originalAsks.Length - 1);
        Assert.Equal(-1000L, sample.Snapshot.BestBidTicks);
        Assert.Equal(-999L, sample.Snapshot.BestAskTicks);
        Assert.Equal(12.345678901234567890123456789m, sample.Snapshot.BestBidSize);
        Assert.Equal(2.50m, sample.Snapshot.BestAskSize);
        Assert.Equal(originalBids.Take(sample.Snapshot.Bids.Length), sample.Snapshot.Bids);
        Assert.Equal(originalAsks.Take(sample.Snapshot.Asks.Length), sample.Snapshot.Asks);
        Assert.Equal(originalChanges.Take(sample.Update.Changes.Count), sample.Update.Changes);
        Assert.True(sample.Coverage.HasFlag(DepthCoverage.PartialBook));
        Assert.True(sample.Coverage.HasFlag(DepthCoverage.Truncated));
    }

    private static void AssertMissingMarker(DepthSample sample, long limit)
    {
        Assert.False(sample.IsUsable);
        Assert.Empty(sample.Snapshot.Bids);
        Assert.Empty(sample.Snapshot.Asks);
        Assert.Empty(sample.Update.Changes);
        Assert.True(sample.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.True(sample.Coverage.HasFlag(DepthCoverage.Truncated));
        Assert.False(string.IsNullOrWhiteSpace(sample.Update.Message));
        Assert.InRange(sample.EstimatedMemoryBytes, 1L, limit);
    }

    private static void AssertTruncated(DepthHistoryQuery query)
    {
        Assert.True(query.IsTruncated);
        Assert.True(query.Coverage.HasFlag(DepthCoverage.Truncated));
        Assert.True(query.Coverage.HasFlag(DepthCoverage.Missing));
    }

    private static void AssertWithinBudgets(DepthHistory history)
    {
        var usage = history.MemoryUsage;
        Assert.InRange(usage.LiveBytes, 0L, usage.LiveLimit);
        Assert.InRange(usage.HistoricalBytes, 0L, usage.HistoricalLimit);
        Assert.InRange(usage.SelectedBytes, 0L, usage.SelectedLimit);
        Assert.InRange(usage.QueuedBytes, 0L, usage.QueueLimit);
    }

    private static async Task WaitForBooks(DepthHistory history, long count)
    {
        await Until(() => history.RecordingStatistics.BookRecords >= count || history.Status.RecordingState == DepthRecordingState.Error);
        Assert.True(history.Status.RecordingState != DepthRecordingState.Error, history.Status.Message);
        Assert.Equal(count, history.RecordingStatistics.BookRecords);
    }

    private static async Task Until(Func<bool> ready)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(1, watchdog.Token);
    }

    private sealed class DepthCapture : Indicator
    {
        public List<DepthUpdate> Updates { get; } = new();
        protected override void OnCalculate(int bar, decimal value) { }
        protected override void OnDepthSnapshot(DepthUpdate update) => Updates.Add(update);
    }

    private sealed class ControlledDepthFeed(Instrument instrument) : IMarketDataFeed
    {
        private IMarketDataSink? _sink;
        public string Id => "fake";
        public string DisplayName => "Fake depth memory fixture";
        public bool SupportsHistory => false;
        public bool IsLive => false;
        public DateTime Now { get; set; } = Start;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Instrument>>([instrument]);
        public Task LoadTradesAsync(Instrument selected, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument selected, IMarketDataSink sink)
        {
            _sink = sink;
            sink.OnStatus(selected, FeedStatus.Connected, null);
            return new Subscription(() => _sink = null);
        }
        public void Publish(DepthUpdate update)
        {
            Now = update.ReceivedAt;
            (_sink ?? throw new InvalidOperationException("The fake feed is not subscribed.")).OnDepth(instrument, update);
        }
        public void Publish(Trade trade) =>
            (_sink ?? throw new InvalidOperationException("The fake feed is not subscribed.")).OnTrades(instrument, [trade]);
        public void Dispose() => _sink = null;
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }

    private sealed class DepthFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tradinghub-depth-memory-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
