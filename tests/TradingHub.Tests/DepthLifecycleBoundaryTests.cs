using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class DepthLifecycleBoundaryTests
{
    private static readonly DateTime Start = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Clean_prefix_storage_failure_persists_its_original_loss_time_before_a_non_full_recording_restart()
    {
        using var fixture = new DepthFixture();
        var options = new DepthRecordingOptions
        {
            Enabled = true, MaxSegmentBytes = 512, MaxDiskBytes = 1024 * 1024,
            MaxSampleBytes = 64 * 1024, MaxAge = TimeSpan.FromSeconds(60),
        };
        var failedAt = Start.AddSeconds(2);
        var resumedAt = Start.AddSeconds(6);
        var stoppedAt = Start.AddSeconds(7);
        Guid firstRecording;
        using (var history = new DepthHistory(Instrument()))
        {
            history.Start(fixture.Root, options, Start);
            history.Receive(Book(Start, 10, 2, 1), Start);
            await Until(() => history.Status.LastPersistedAt == Start);
            var prefixStatistics = history.RecordingStatistics;
            Assert.Equal(1L, prefixStatistics.BookRecords);
            Assert.Equal(512L, history.Options.MaxSegmentBytes);
            Assert.Equal(64 * 1024L, history.Options.MaxSampleBytes);

            var wide = DepthRecordingSizingFixture.WideBook(failedAt, 32, 8, 2);
            var received = history.Receive(wide, failedAt);
            Assert.Equal(32, received.Snapshot.Bids.Length);
            Assert.Equal(32, received.Snapshot.Asks.Length);
            Assert.Equal(8, received.Changes.Count);
            Assert.False(received.Coverage.HasFlag(DepthCoverage.Truncated));
            await Until(() => history.Status.RecordingState == DepthRecordingState.Error);
            Assert.Contains("segment", history.Status.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Start, history.Status.LastPersistedAt);
            Assert.Equal(prefixStatistics.BookFrameBytes, history.RecordingStatistics.BookFrameBytes);
            Assert.Equal(1L, history.RecordingStatistics.BookRecords);
            Assert.NotNull(history.Current);
            Assert.True(history.Current.EstimatedMemoryBytes < history.Options.MaxSampleBytes);

            // A frame rejected before append leaves a readable prefix, not a corrupt-tail warning.
            var prefix = await history.LoadAsync(Start, resumedAt);
            Assert.Null(prefix.Message);
            Assert.False(prefix.IsTruncated);
            var first = Assert.Single(prefix.Samples);
            Assert.Equal(Start, first.Time);
            Assert.Equal((10m, 2m), (first.Snapshot.BestBidSize, first.Snapshot.BestAskSize));
            firstRecording = first.RecordingId;

            history.Configure(fixture.Root, options with { MaxSegmentBytes = 64 * 1024 }, resumedAt);
            var resumed = history.Receive(Book(resumedAt, 30, 6, 3, full: false), resumedAt);
            Assert.False(resumed.IsFullRefresh);
            Assert.True(resumed.IsUsable);
            history.Stop(stoppedAt);
            Assert.Equal(DepthRecordingState.Stopped, history.Status.RecordingState);
            Assert.Equal(stoppedAt, history.Status.LastPersistedAt);
        }

        using var reader = new DepthHistory(Instrument());
        reader.Start(fixture.Root, new() { MaxAge = TimeSpan.FromSeconds(60) }, stoppedAt);
        var loaded = await reader.LoadAsync(Start, stoppedAt.AddSeconds(1));
        Assert.Null(loaded.Message);
        Assert.False(loaded.IsTruncated);
        Assert.Equal(new[] { Start, failedAt, resumedAt, stoppedAt }, loaded.Samples.Select(sample => sample.Time));
        var gap = Assert.Single(loaded.Samples, sample => sample.GapBefore == DepthGapReason.StorageFailure);
        Assert.Equal(failedAt, gap.Time);
        Assert.False(gap.IsUsable);
        Assert.Empty(gap.Snapshot.Bids);
        Assert.Empty(gap.Snapshot.Asks);
        Assert.True(gap.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.True(gap.Coverage.HasFlag(DepthCoverage.SequenceGap));
        var recovered = Assert.Single(loaded.Samples, sample => sample.Time == resumedAt);
        Assert.NotEqual(firstRecording, recovered.RecordingId);
        Assert.Equal(recovered.RecordingId, gap.RecordingId);
        Assert.True(gap.Ordinal < recovered.Ordinal);
        Assert.True(gap.Time < recovered.Time);
        Assert.False(recovered.Update.IsFullRefresh);
        Assert.True(recovered.IsUsable);
        Assert.Equal((30m, 6m), (recovered.Snapshot.BestBidSize, recovered.Snapshot.BestAskSize));
        Assert.All(loaded.Samples, sample =>
        {
            Assert.Equal("fake", sample.ProviderId);
            Assert.Equal("wide-book-fixture", sample.Venue);
        });

        reader.Apply(loaded);
        var spanningLoss = DepthStatistics.Measure(reader.Query(Start, Start.AddSeconds(5)), 0,
            DepthAggregation.DurationAverage, TimeSpan.FromSeconds(60));
        Assert.True(spanningLoss.IsAvailable);
        Assert.Equal((10m, 2m), (spanningLoss.Bid, spanningLoss.Ask));
        Assert.Equal(1, spanningLoss.Samples);
        Assert.Equal(TimeSpan.FromSeconds(2), spanningLoss.CoveredDuration);
        Assert.True(spanningLoss.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.True(spanningLoss.Coverage.HasFlag(DepthCoverage.SequenceGap));
    }

    [Fact]
    public async Task Pending_storage_loss_survives_another_policy_restart_without_an_intervening_book()
    {
        using var fixture = new DepthFixture();
        var options = new DepthRecordingOptions
        {
            Enabled = true, MaxSegmentBytes = 512, MaxDiskBytes = 1024 * 1024,
            MaxSampleBytes = 64 * 1024, MaxAge = TimeSpan.FromSeconds(60),
        };
        var failedAt = Start.AddSeconds(2);
        using (var history = new DepthHistory(Instrument()))
        {
            history.Start(fixture.Root, options, Start);
            history.Receive(Book(Start, 10, 2, 1), Start);
            await Until(() => history.Status.LastPersistedAt == Start);
            history.Receive(DepthRecordingSizingFixture.WideBook(failedAt, 32, 8, 2), failedAt);
            await Until(() => history.Status.RecordingState == DepthRecordingState.Error);

            var recoveredPolicy = options with { MaxSegmentBytes = 64 * 1024 };
            history.Configure(fixture.Root, recoveredPolicy, Start.AddSeconds(3));
            history.Configure(fixture.Root, recoveredPolicy with { RetentionDays = 15 }, Start.AddSeconds(4));
            var resumed = history.Receive(Book(Start.AddSeconds(6), 30, 6, 3, full: false), Start.AddSeconds(6));
            Assert.True(resumed.IsUsable);
            Assert.False(resumed.IsFullRefresh);
            history.Stop(Start.AddSeconds(7));
            Assert.Equal(0L, history.MemoryUsage.QueuedBytes);
        }

        using var reader = new DepthHistory(Instrument());
        reader.Start(fixture.Root, new(), Start.AddSeconds(8));
        var loaded = await reader.LoadAsync(Start, Start.AddSeconds(8));
        Assert.Null(loaded.Message);
        Assert.False(loaded.IsTruncated);
        Assert.Equal(new[] { Start, failedAt, Start.AddSeconds(4), Start.AddSeconds(6), Start.AddSeconds(7) },
            loaded.Samples.Select(sample => sample.Time));
        var failure = Assert.Single(loaded.Samples, sample => sample.GapBefore == DepthGapReason.StorageFailure);
        Assert.Equal(failedAt, failure.Time);
        Assert.False(failure.IsUsable);
        var recovered = Assert.Single(loaded.Samples, sample => sample.Time == Start.AddSeconds(6));
        Assert.NotEqual(failure.RecordingId, recovered.RecordingId);
        Assert.False(recovered.Update.IsFullRefresh);
        reader.Apply(loaded);
        var measured = DepthStatistics.Measure(reader.Query(Start, Start.AddSeconds(5)), 0,
            DepthAggregation.DurationAverage, TimeSpan.FromSeconds(60));
        Assert.True(measured.IsAvailable);
        Assert.Equal((10m, 2m), (measured.Bid, measured.Ask));
        Assert.Equal(TimeSpan.FromSeconds(2), measured.CoveredDuration);
        Assert.True(measured.Coverage.HasFlag(DepthCoverage.Missing));
    }

    [Fact]
    public async Task A_fresh_known_side_remains_displayable_without_becoming_a_two_sided_book_or_bypassing_gap_and_stale_gates()
    {
        using var fixture = new DepthFixture();
        using var feed = new DepthFeed();
        using var data = new InstrumentData(Instrument(), feed)
        {
            RecordingRoot = fixture.Root, RecordLive = false,
            DepthRecording = new() { Enabled = true, MaxAge = TimeSpan.FromSeconds(2) },
        };
        data.Start(Start);
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        using var model = new ChartModel(data, ChartPeriod.M1);
        var capture = new BestQuoteCapture();
        Assert.Null(model.AddIndicator(capture, applyDefaultColors: false).Error);
        var sink = Assert.IsAssignableFrom<IMarketDataSink>(feed.Sink);
        var builder = new OrderBookBuilder();
        DepthUpdate OneSide(long sequence, bool full, bool invalidChange = false, DepthCoverage additionalCoverage = DepthCoverage.None)
        {
            builder.Clear();
            builder.Set(BookSide.Bid, -1000, 50);
            var update = builder.BuildUpdate(feed.Now.AddMilliseconds(-1), fullRefresh: full,
                providerId: "fake", venue: "wide-book-fixture", sequence: sequence,
                additionalCoverage: additionalCoverage, receivedAt: feed.Now);
            if (!invalidChange) return update;
            return new DepthUpdate
            {
                Snapshot = update.Snapshot, Changes = [new BookChange(BookSide.Bid, -1000, -1)],
                ProviderId = update.ProviderId, Venue = update.Venue, ReceivedAt = update.ReceivedAt,
                Sequence = update.Sequence, IsFullRefresh = update.IsFullRefresh, Coverage = update.Coverage,
            };
        }

        feed.Now = Start.AddSeconds(1);
        var builtPartial = OneSide(1, full: true);
        Assert.True(builtPartial.Coverage.HasFlag(DepthCoverage.KnownSide));
        Assert.True(builtPartial.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.True(builtPartial.HasAvailableLevels);
        sink.OnDepth(data.Instrument, builtPartial);
        data.Pump();
        Assert.Equal((-1000L, 50m), (data.Depth.BestBidTicks, data.Depth.BestBidSize));
        Assert.False(data.Depth.HasAsk);
        var partial = capture.Updates[^1];
        Assert.True(partial.HasAvailableLevels);
        Assert.True(partial.Coverage.HasFlag(DepthCoverage.KnownSide));
        Assert.True(partial.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.True(partial.Coverage.HasFlag(DepthCoverage.PartialBook));
        Assert.False(partial.IsUsable);
        Assert.Null(data.DepthHistory.Current);
        Assert.True(data.DepthHistory.HasFreshLevels);
        var query = data.DepthHistory.Query(feed.Now, feed.Now.AddSeconds(1));
        Assert.False(DepthStatistics.Measure(query, 0, DepthAggregation.SampleAverage,
            TimeSpan.FromSeconds(2), allowPartial: true).IsAvailable);
        Assert.Empty(LiquidityHeatmapRenderer.BuildFrame(query, 1, TimeSpan.FromSeconds(2)).Cells);

        int beforeStale = capture.Updates.Count;
        feed.Now = Start.AddSeconds(4);
        data.Pump();
        Assert.Same(DepthSnapshot.Empty, data.Depth);
        Assert.False(data.DepthHistory.HasFreshLevels);
        Assert.Equal(Start.AddSeconds(1), data.DepthHistory.Status.LastReceivedAt);
        Assert.Equal(beforeStale + 1, capture.Updates.Count);
        feed.Now = Start.AddSeconds(5);
        data.Pump();
        Assert.Equal(beforeStale + 1, capture.Updates.Count);

        feed.Now = Start.AddSeconds(6);
        sink.OnStatus(data.Instrument, FeedStatus.Reconnecting, "Fake known-side outage");
        data.Pump();
        feed.Now = Start.AddSeconds(7);
        sink.OnStatus(data.Instrument, FeedStatus.Connected, null);
        sink.OnDepth(data.Instrument, OneSide(2, full: false));
        data.Pump();
        Assert.Same(DepthSnapshot.Empty, data.Depth);
        Assert.False(capture.Updates[^1].HasAvailableLevels);
        Assert.False(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.KnownSide));
        Assert.True(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.SequenceGap));
        Assert.False(data.DepthHistory.HasFreshLevels);

        feed.Now = Start.AddSeconds(8);
        sink.OnDepth(data.Instrument, OneSide(3, full: true));
        data.Pump();
        Assert.Equal(50m, data.Depth.BestBidSize);
        Assert.False(data.Depth.HasAsk);
        Assert.True(data.DepthHistory.HasFreshLevels);
        Assert.Null(data.DepthHistory.Current);
        Assert.False(capture.Updates[^1].IsUsable);

        feed.Now = Start.AddSeconds(9);
        sink.OnDepth(data.Instrument, OneSide(4, full: true, invalidChange: true));
        data.Pump();
        Assert.Same(DepthSnapshot.Empty, data.Depth);
        Assert.False(capture.Updates[^1].HasAvailableLevels);
        Assert.False(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.KnownSide));
        feed.Now = Start.AddSeconds(10);
        var sourceMissing = OneSide(5, full: true, additionalCoverage: DepthCoverage.Missing);
        Assert.False(sourceMissing.Coverage.HasFlag(DepthCoverage.KnownSide));
        Assert.False(sourceMissing.HasAvailableLevels);
        sink.OnDepth(data.Instrument, sourceMissing);
        data.Pump();
        Assert.Same(DepthSnapshot.Empty, data.Depth);
        Assert.False(data.DepthHistory.HasFreshLevels);
        data.Stop();
        var saved = await data.DepthHistory.LoadAsync(Start, feed.Now.AddSeconds(1));
        var recorded = Assert.Single(saved.Samples, sample => sample.Time == Start.AddSeconds(1));
        Assert.True(recorded.Update.HasAvailableLevels);
        Assert.True(recorded.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.True(recorded.Coverage.HasFlag(DepthCoverage.KnownSide));
        Assert.False(recorded.IsUsable);
        Assert.Equal(50m, recorded.Snapshot.BestBidSize);
        Assert.Empty(recorded.Snapshot.Asks);
    }

    [Fact]
    public async Task Same_price_best_quotes_publish_unavailable_then_available_and_reconnect_requires_a_full_refresh()
    {
        using var fixture = new DepthFixture();
        using var feed = new DepthFeed();
        using var data = new InstrumentData(Instrument(), feed)
        {
            RecordingRoot = fixture.Root, RecordLive = false,
            DepthRecording = new() { MaxAge = TimeSpan.FromSeconds(60) },
        };
        data.Start(Start);
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        using var model = new ChartModel(data, ChartPeriod.M1);
        var capture = new BestQuoteCapture();
        var slot = model.AddIndicator(capture, applyDefaultColors: false);
        Assert.Null(slot.Error);
        var sink = Assert.IsAssignableFrom<IMarketDataSink>(feed.Sink);

        feed.Now = Start.AddSeconds(1);
        sink.OnDepth(data.Instrument, Book(feed.Now, 12.345678901234567890123456789m, 3.125m, 1));
        Assert.Empty(capture.Quotes);
        data.Pump();
        AssertQuotePair(capture, 0, feed.Now, true, 12.345678901234567890123456789m, 3.125m);
        Assert.NotNull(model.MarketDepthInfo.Current);

        feed.Now = Start.AddSeconds(2);
        sink.OnDepth(data.Instrument, Book(feed.Now, 77.25m, 8.5m, 2, full: false,
            coverage: DepthCoverage.ReceivedChanges | DepthCoverage.Missing | DepthCoverage.SequenceGap,
            gap: DepthGapReason.Sequence));
        data.Pump();
        Assert.Null(model.MarketDepthInfo.Current);
        Assert.Same(DepthSnapshot.Empty, data.Depth);
        Assert.Empty(model.MarketDepthInfo.GetMarketDepthSnapshot());
        AssertQuotePair(capture, 2, feed.Now, false, 0, 0);
        Assert.All(capture.Quotes.Skip(2), quote =>
        {
            Assert.True(quote.DepthCoverage.HasFlag(DepthCoverage.Missing));
            Assert.True(quote.DepthCoverage.HasFlag(DepthCoverage.SequenceGap));
        });

        feed.Now = Start.AddSeconds(3);
        sink.OnDepth(data.Instrument, Book(feed.Now, 19.875m, 5.0625m, 3));
        data.Pump();
        AssertQuotePair(capture, 4, feed.Now, true, 19.875m, 5.0625m);
        Assert.Equal(new long?[] { 1, 2, 3 }, capture.Updates.Select(update => update.Sequence));
        Assert.Equal(new[] { 12.345678901234567890123456789m, 77.25m, 19.875m },
            capture.Updates.Select(update => update.Snapshot.BestBidSize));
        Assert.Equal(new[] { 3.125m, 8.5m, 5.0625m }, capture.Updates.Select(update => update.Snapshot.BestAskSize));
        Assert.All(capture.Updates, update =>
        {
            Assert.Equal(-1000L, update.Snapshot.BestBidTicks);
            Assert.Equal(-999L, update.Snapshot.BestAskTicks);
        });

        feed.Now = Start.AddSeconds(4);
        Assert.True(feed.Now - model.MarketDepthInfo.Current!.Time < data.DepthHistory.Options.MaxAge);
        sink.OnStatus(data.Instrument, FeedStatus.Reconnecting, "Fake direct reconnect without a preceding disconnect.");
        Assert.Null(data.DepthHistory.Current);
        Assert.Null(model.MarketDepthInfo.Current);
        Assert.Equal(6, capture.Quotes.Count);
        data.Pump();
        Assert.Equal(FeedStatus.Reconnecting, data.Status);
        AssertQuotePair(capture, 6, feed.Now, false, 0, 0, bidPrice: 0, askPrice: 0);
        Assert.Same(DepthSnapshot.Empty, data.Depth);

        feed.Now = Start.AddSeconds(5);
        sink.OnStatus(data.Instrument, FeedStatus.Connected, null);
        sink.OnDepth(data.Instrument, Book(feed.Now, 23.125m, 6.25m, 4, full: false));
        Assert.Null(data.DepthHistory.Current);
        data.Pump();
        Assert.Equal(FeedStatus.Connected, data.Status);
        Assert.Null(model.MarketDepthInfo.Current);
        Assert.Empty(model.MarketDepthInfo.GetMarketDepthSnapshot());
        Assert.False(capture.Updates[^1].IsFullRefresh);
        Assert.True(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.Missing));
        Assert.True(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.SequenceGap));
        AssertQuotePair(capture, 8, feed.Now, false, 0, 0);

        feed.Now = Start.AddSeconds(6);
        sink.OnDepth(data.Instrument, Book(feed.Now, 29.875m, 7.5m, 5));
        data.Pump();
        AssertQuotePair(capture, 10, feed.Now, true, 29.875m, 7.5m);
        Assert.Equal(new[] { true, true, false, false, true, true, false, false, false, false, true, true },
            capture.Quotes.Select(quote => quote.IsAvailable));
        var current = Assert.IsType<DepthSample>(model.MarketDepthInfo.Current);
        Assert.True(current.Update.IsFullRefresh);
        Assert.Equal((29.875m, 7.5m), (current.Snapshot.BestBidSize, current.Snapshot.BestAskSize));
        Assert.Null(slot.Error);

        int updatesBeforeStale = capture.Updates.Count;
        feed.Now = Start.AddSeconds(67);
        data.Pump();
        Assert.Same(DepthSnapshot.Empty, data.Depth);
        Assert.Null(model.MarketDepthInfo.Current);
        Assert.Empty(model.MarketDepthInfo.GetMarketDepthSnapshot());
        Assert.Equal(Start.AddSeconds(6), data.DepthHistory.Status.LastReceivedAt);
        Assert.True(data.DepthHistory.Status.IsStale);
        Assert.Equal(updatesBeforeStale + 1, capture.Updates.Count);
        Assert.False(capture.Updates[^1].IsUsable);
        Assert.True(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.Missing));
        AssertQuotePair(capture, 12, feed.Now, false, 0, 0, bidPrice: 0, askPrice: 0);
        int quotesAfterStale = capture.Quotes.Count;
        feed.Now = Start.AddSeconds(68);
        data.Pump();
        Assert.Equal(updatesBeforeStale + 1, capture.Updates.Count);
        Assert.Equal(quotesAfterStale, capture.Quotes.Count);
    }

    [Theory]
    [InlineData(30, 120)]
    [InlineData(270, 300)]
    public async Task Disk_and_chart_selection_carry_in_honor_consumer_freshness_across_UTC_midnight(int ageAtStart, int maxAge)
    {
        using var fixture = new DepthFixture();
        var from = Start.Date.AddDays(1);
        var to = from.AddSeconds(30);
        var at = from.AddSeconds(-ageAtStart);
        using var writer = new DepthHistory(Instrument());
        writer.Start(fixture.Root, new() { Enabled = true }, at);
        writer.Receive(Book(at, 12.25m, 3.125m, 1), at);
        writer.Stop(from.AddSeconds(61));
        var inMemory = DepthStatistics.Measure(writer.Query(from, to), 0,
            DepthAggregation.DurationAverage, TimeSpan.FromSeconds(maxAge));

        using var reader = new DepthHistory(Instrument());
        reader.Start(fixture.Root, new(), to);
        Assert.Equal(TimeSpan.FromSeconds(10), reader.Options.MaxAge);
        var load = await reader.LoadAsync(from, to);
        Assert.Null(load.Message);
        Assert.False(load.IsTruncated);
        Assert.Equal(at, Assert.Single(load.Samples).Time);
        reader.Apply(load);
        using var selection = reader.CreateSelection();
        await selection.LoadAsync(from, to);
        foreach (var query in new[] { reader.Query(from, to), selection.Query(from, to) })
        {
            Assert.Empty(query.Samples);
            Assert.Equal(at, Assert.IsType<DepthSample>(query.Preceding).Time);
            var duration = DepthStatistics.Measure(query, 0, DepthAggregation.DurationAverage, TimeSpan.FromSeconds(maxAge));
            var last = DepthStatistics.Measure(query, 0, DepthAggregation.Last, TimeSpan.FromSeconds(maxAge));
            Assert.True(duration.IsAvailable);
            Assert.True(last.IsAvailable);
            Assert.Equal(inMemory, duration);
            Assert.Equal((12.25m, 3.125m), (last.Bid, last.Ask));
            Assert.Equal(TimeSpan.FromSeconds(30), duration.CoveredDuration);
            Assert.False(duration.Coverage.HasFlag(DepthCoverage.Missing));
            var heatmap = LiquidityHeatmapRenderer.BuildFrame(query, 1, TimeSpan.FromSeconds(maxAge));
            Assert.False(heatmap.IsTruncated);
            Assert.Empty(heatmap.Gaps);
            Assert.Equal(2, heatmap.Cells.Count);
            Assert.All(heatmap.Cells, cell => Assert.Equal((from, to), (cell.From, cell.To)));
        }
    }

    [Fact]
    public async Task Book_change_rejects_an_available_but_uncovered_previous_sample_average()
    {
        using var fixture = new DepthFixture();
        Persist(fixture.Root, Start.AddMinutes(2),
            Book(Start.AddSeconds(50), 10, 2, 1),
            Book(Start.AddMinutes(1), 30, 6, 2, full: false));
        using var feed = new DepthFeed { Now = Start.AddMinutes(2) };
        using var data = new InstrumentData(Instrument(), feed) { RecordingRoot = fixture.Root, RecordLive = false };
        data.DepthHistory.Start(fixture.Root, new() { MaxAge = TimeSpan.FromSeconds(60) }, feed.Now);
        data.Trades.Add(new Trade(1, Start, -250m, 1, TradeSide.None));
        data.Trades.Add(new Trade(2, Start.AddMinutes(1), -250m, 1, TradeSide.None));
        using var model = new ChartModel(data, ChartPeriod.M1);
        var study = new OrderbookDelta
        {
            WithinTicks = 0, MaxAgeSeconds = 60, Aggregation = DepthAggregation.SampleAverage,
            AllowPartialBook = false, View = OrderbookDelta.BookView.Delta,
        };
        var slot = model.AddIndicator(study, applyDefaultColors: false);
        Assert.Null(slot.Error);
        await Until(() => { model.Update(); return !model.IsLoadingDepthHistory; });
        Assert.Equal(2, model.Series.Count);
        var values = Assert.IsType<OptionalValueDataSeries>(study.DataSeries[0]);
        Assert.True(values.IsAvailable(0));
        Assert.Equal(8m, values[0]);
        Assert.True(values.IsAvailable(1));
        Assert.Equal(24m, values[1]);
        var previous = Assert.IsType<DepthMeasurement>(study.GetMeasurement(0));
        Assert.True(previous.IsAvailable);
        Assert.Equal((10m, 2m), (previous.Bid, previous.Ask));
        Assert.Equal(1, previous.Samples);
        Assert.Equal(TimeSpan.FromSeconds(10), previous.CoveredDuration);
        Assert.True(previous.Coverage.HasFlag(DepthCoverage.Missing));
        var current = Assert.IsType<DepthMeasurement>(study.GetMeasurement(1));
        Assert.True(current.IsAvailable);
        Assert.Equal((30m, 6m), (current.Bid, current.Ask));
        Assert.Equal(1, current.Samples);
        Assert.Equal(TimeSpan.FromMinutes(1), current.CoveredDuration);
        Assert.False(current.Coverage.HasFlag(DepthCoverage.Missing));

        // Both deltas exist, but the previous bar has fifty seconds of unknown depth.
        study.View = OrderbookDelta.BookView.Change;
        model.Update();
        Assert.False(values.IsAvailable(0));
        Assert.False(values.IsAvailable(1));
        Assert.False(study.IsAvailable);
        Assert.True(study.Coverage.HasFlag(StudyCoverage.Missing));
        Assert.Null(slot.Error);
    }

    [Fact]
    public async Task Completing_bounded_initial_aggregation_extends_recorded_depth_for_a_study_with_heatmap_off()
    {
        using var fixture = new DepthFixture();
        var later = Start.AddMinutes(5);
        Persist(fixture.Root, later.AddMinutes(1), Book(Start, 10, 2, 1), Book(later, 70, 7, 2));
        using var feed = new DepthFeed { Now = later.AddMinutes(1) };
        using var data = new InstrumentData(Instrument(), feed) { RecordingRoot = fixture.Root, RecordLive = false };
        data.DepthHistory.Start(fixture.Root, new() { MaxAge = TimeSpan.FromSeconds(60) }, feed.Now);
        int firstMinuteTrades = CandleSeries.InitialBatchSize + 5;
        for (int i = 0; i < firstMinuteTrades; i++)
            data.Trades.Add(new Trade(i + 1, Start.AddTicks(i), -250m, 1, TradeSide.Buy));
        data.Trades.Add(new Trade(firstMinuteTrades + 1, later, -250m, 1, TradeSide.Buy));
        using var model = new ChartModel(data, ChartPeriod.M1, new() { ShowLiquidityHeatmap = false });
        Assert.True(model.IsAggregating);
        Assert.Equal(CandleSeries.InitialBatchSize, model.Series.ProcessedTrades);
        Assert.Equal(Start, Assert.Single(model.Series.Candles).Time);
        Assert.False(model.Settings.ShowLiquidityHeatmap);
        var study = new OrderbookDelta
        {
            WithinTicks = 0, MaxAgeSeconds = 60, Aggregation = DepthAggregation.SampleAverage,
            AllowPartialBook = false,
        };
        var slot = model.AddIndicator(study, applyDefaultColors: false);
        Assert.Null(slot.Error);
        Assert.True(model.IsAggregating);
        Assert.True(model.IsLoadingDepthHistory);
        await Until(() =>
        {
            model.Update();
            return !model.IsAggregating && !model.IsLoadingDepthHistory;
        });

        Assert.False(model.IsAggregating);
        Assert.False(model.IsLoadingDepthHistory);
        Assert.False(model.Settings.ShowLiquidityHeatmap);
        Assert.Equal(firstMinuteTrades + 1, model.Series.ProcessedTrades);
        Assert.Equal(2, model.Series.Count);
        Assert.Equal(later, model.Series[1].Time);
        Assert.Null(model.MarketDepthInfo.Current);
        var selected = model.MarketDepthInfo.GetHistory(later, later.AddMinutes(1));
        var recorded = Assert.Single(selected.Samples);
        Assert.Equal(later, recorded.Time);
        Assert.Equal((70m, 7m), (recorded.Snapshot.BestBidSize, recorded.Snapshot.BestAskSize));
        Assert.Equal((-1000L, -999L), (recorded.Snapshot.BestBidTicks, recorded.Snapshot.BestAskTicks));
        var measurement = Assert.IsType<DepthMeasurement>(study.GetMeasurement(1));
        Assert.True(measurement.IsAvailable);
        Assert.Equal((70m, 7m), (measurement.Bid, measurement.Ask));
        Assert.Equal(1, measurement.Samples);
        Assert.Equal(TimeSpan.FromMinutes(1), measurement.CoveredDuration);
        Assert.False(measurement.Coverage.HasFlag(DepthCoverage.Missing));
        var values = Assert.IsType<OptionalValueDataSeries>(study.DataSeries[0]);
        Assert.True(values.IsAvailable(0));
        Assert.Equal(8m, values[0]);
        Assert.True(values.IsAvailable(1));
        Assert.Equal(63m, values[1]);
        Assert.Null(slot.Error);
    }

    private static Instrument Instrument() => new()
    {
        Symbol = "DEPTH-LIFECYCLE-FIXTURE", FeedId = "fake", Exchange = "wide-book-fixture",
        TickSize = 0.25m, VolumeStep = 0.01m,
    };

    private static DepthUpdate Book(DateTime at, decimal bid, decimal ask, long sequence, bool full = true,
        DepthCoverage coverage = DepthCoverage.ReceivedChanges, DepthGapReason gap = DepthGapReason.None) => new()
    {
        Snapshot = new DepthSnapshot([new BookLevel(-1000, bid)], [new BookLevel(-999, ask)], at.AddMilliseconds(-1)),
        Changes = [new BookChange(BookSide.Bid, -1000, bid), new BookChange(BookSide.Ask, -999, ask)],
        ProviderId = "fake", Venue = "wide-book-fixture", ReceivedAt = at, Sequence = sequence,
        IsFullRefresh = full, Coverage = coverage, GapBefore = gap,
    };

    private static void Persist(string root, DateTime stoppedAt, params DepthUpdate[] updates)
    {
        using var history = new DepthHistory(Instrument());
        history.Start(root, new() { Enabled = true, MaxAge = TimeSpan.FromSeconds(60) }, Start);
        foreach (var update in updates) history.Receive(update, update.ReceivedAt);
        history.Stop(stoppedAt);
        Assert.Equal(DepthRecordingState.Stopped, history.Status.RecordingState);
        Assert.Equal(stoppedAt, history.Status.LastPersistedAt);
    }

    private static void AssertQuotePair(BestQuoteCapture capture, int offset, DateTime at, bool available,
        decimal bidVolume, decimal askVolume, decimal bidPrice = -250m, decimal askPrice = -249.75m)
    {
        Assert.Equal(offset + 2, capture.Quotes.Count);
        var bid = capture.Quotes[offset];
        var ask = capture.Quotes[offset + 1];
        Assert.Equal(MarketDataType.Bid, bid.DataType);
        Assert.Equal(MarketDataType.Ask, ask.DataType);
        Assert.Equal((at, at), (bid.Time, ask.Time));
        Assert.Equal((available, available), (bid.IsAvailable, ask.IsAvailable));
        Assert.Equal((bidPrice, askPrice), (bid.Price, ask.Price));
        Assert.Equal((bidVolume, askVolume), (bid.Volume, ask.Volume));
    }

    private sealed class BestQuoteCapture : Indicator
    {
        public List<DepthUpdate> Updates { get; } = [];
        public List<MarketDataArg> Quotes { get; } = [];
        protected override void OnCalculate(int bar, decimal value) { }
        protected override void OnDepthSnapshot(DepthUpdate update) => Updates.Add(update);
        protected override void OnBestBidAskChanged(MarketDataArg quote) => Quotes.Add(quote.Clone());
    }

    private sealed class DepthFeed : IMarketDataFeed
    {
        public string Id => "fake";
        public string DisplayName => "Fake depth lifecycle fixture";
        public bool SupportsHistory => false;
        public bool IsLive => false;
        public DateTime Now { get; set; } = Start;
        public IMarketDataSink? Sink { get; private set; }
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Instrument>>([Instrument()]);
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            Sink = sink;
            sink.OnStatus(instrument, FeedStatus.Connected, null);
            return new EndSubscription(() => Sink = null);
        }
        public void Dispose() => Sink = null;
    }

    private sealed class EndSubscription(Action end) : IDisposable
    {
        private Action? _end = end;
        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }

    private static async Task Until(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            Assert.True(DateTime.UtcNow < until, "The fake depth lifecycle operation did not settle.");
            await Task.Delay(1);
        }
    }

    private sealed class DepthFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tradinghub-depth-lifecycle-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
