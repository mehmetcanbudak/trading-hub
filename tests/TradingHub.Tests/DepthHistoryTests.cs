using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class DepthHistoryTests
{
    private static readonly DateTime Start = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
    private static Instrument Instrument() => new() { Symbol = "DEPTH-FIXTURE", FeedId = "fake", Exchange = "venue-a", TickSize = 0.25m, VolumeStep = 0.01m };
    private static DepthUpdate Update(DateTime at, decimal bid, decimal ask, long sequence = 1, string venue = "venue-a", bool full = true,
        BookChangeOrigin origin = BookChangeOrigin.Received) => new()
    {
        Snapshot = new DepthSnapshot([new BookLevel(-5, bid), new BookLevel(-8, 0.1234567890123456789012345678m)], [new BookLevel(-3, ask)], at.AddMilliseconds(-7)),
        Changes = [new BookChange(BookSide.Bid, -5, bid, origin), new BookChange(BookSide.Ask, -2, 0)],
        ProviderId = "fake", Venue = venue, ReceivedAt = at, Sequence = sequence,
        IsFullRefresh = full, Coverage = DepthCoverage.ReceivedChanges,
    };

    [Fact]
    public async Task Disposal_uses_the_owned_logical_clock_not_current_UTC_for_replay_retention()
    {
        using var fixture = new DepthFixture();
        using (var recorder = new DepthHistory(Instrument()))
        {
            recorder.Start(fixture.Root, new() { Enabled = true, RetentionDays = 1 }, Start);
            recorder.Receive(Update(Start, 10, 2), Start);
            recorder.Refresh(Start.AddSeconds(2));
        }
        using var reader = new DepthHistory(Instrument());
        reader.Start(fixture.Root, new(), Start.AddMonths(2));
        var loaded = await reader.LoadAsync(Start, Start.AddMinutes(1));
        Assert.Equal(10m, Assert.Single(loaded.Samples, s => s.IsUsable).Snapshot.BestBidSize);
        Assert.Equal(Start.AddSeconds(2), Assert.Single(loaded.Samples, s => s.GapBefore == DepthGapReason.Disconnected).Time);
    }

    [Fact]
    public async Task Received_changes_and_exact_negative_price_snapshots_round_trip_for_selected_past_day()
    {
        using var fixture = new DepthFixture();
        using (var history = new DepthHistory(Instrument()))
        {
            history.Start(fixture.Root, new() { Enabled = true, MemoryRetention = TimeSpan.FromSeconds(1) }, Start);
            history.Receive(Update(Start, 12.345678901234567890123456789m, 4, origin: BookChangeOrigin.DerivedSnapshotDifference), Start);
            history.Receive(Update(Start.AddSeconds(2), 17, 3, 2, full: false), Start.AddSeconds(2));
            history.Stop(Start.AddSeconds(3));
            Assert.Equal(DepthRecordingState.Stopped, history.Status.RecordingState);
            Assert.Equal(Start.AddSeconds(3), history.Status.LastPersistedAt);
        }
        using var loaded = new DepthHistory(Instrument());
        loaded.Start(fixture.Root, new() { Enabled = false }, Start.AddMonths(2));
        loaded.Apply(await loaded.LoadAsync(Start.Date, Start.Date.AddDays(1)));
        loaded.Refresh(Start.AddMonths(2));
        var samples = loaded.Query(Start, Start.AddSeconds(3)).Samples;
        Assert.Equal(2, samples.Count);
        Assert.Equal(new long[] { 1, 2 }, samples.Select(s => s.Ordinal));
        Assert.Equal(new long?[] { 1, 2 }, samples.Select(s => s.Update.Sequence));
        Assert.Equal(12.345678901234567890123456789m, samples[0].Snapshot.BestBidSize);
        Assert.Equal(0.1234567890123456789012345678m, samples[0].Snapshot.Bids[1].Size);
        Assert.Equal(-5, samples[0].Snapshot.BestBidTicks);
        Assert.Equal(Start.AddMilliseconds(-7), samples[0].SourceTime);
        Assert.Equal("fake", samples[0].ProviderId);
        Assert.Equal("venue-a", samples[0].Venue);
        Assert.Equal(new BookChange(BookSide.Ask, -2, 0), samples[0].Update.Changes[1]);
        Assert.Equal(BookChangeOrigin.DerivedSnapshotDifference, samples[0].Update.Changes[0].Origin);
        Assert.True(samples[0].Update.IsFullRefresh);
        Assert.False(samples[1].Update.IsFullRefresh);
        Assert.Equal(DepthRecordingState.Disabled, loaded.Status.RecordingState);
        var stopped = Assert.Single(loaded.Query(Start.AddSeconds(3), Start.AddMinutes(1)).Samples);
        Assert.Equal(DepthGapReason.Disconnected, stopped.GapBefore);
        Assert.False(stopped.IsUsable);
    }

    [Fact]
    public void Sample_and_duration_averages_have_independent_worked_values_and_stale_terminal_books_are_missing()
    {
        var id = Guid.NewGuid();
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(4),
            [new(id, 1, Update(Start, 10, 2)), new(id, 2, Update(Start.AddSeconds(1), 30, 6, 2))], null, DepthCoverage.ReceivedChanges, null);
        var samples = DepthStatistics.Measure(query, 0, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10));
        var duration = DepthStatistics.Measure(query, 0, DepthAggregation.DurationAverage, TimeSpan.FromSeconds(10));
        var last = DepthStatistics.Measure(query, 0, DepthAggregation.Last, TimeSpan.FromSeconds(10));
        Assert.Equal((20m, 4m, 16m), (samples.Bid, samples.Ask, samples.Delta));
        Assert.Equal((25m, 5m, 20m), (duration.Bid, duration.Ask, duration.Delta));
        Assert.Equal((30m, 6m), (last.Bid, last.Ask));
        Assert.Equal(TimeSpan.FromSeconds(4), duration.CoveredDuration);
        Assert.Equal(2, duration.Samples);
        Assert.InRange(duration.Imbalance, 66.666666m, 66.666667m);
        var stale = DepthStatistics.Measure(query, 0, DepthAggregation.Last, TimeSpan.FromSeconds(2));
        Assert.False(stale.IsAvailable);
        Assert.True(stale.Coverage.HasFlag(DepthCoverage.Missing));
        var missing = DepthStatistics.Measure(query with { From = Start.AddMinutes(1), To = Start.AddMinutes(2), Samples = [] },
            0, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10));
        Assert.False(missing.IsAvailable);
    }

    [Fact]
    public void Heatmap_is_resting_size_not_sum_of_updates_and_groups_negative_prices_downward()
    {
        var id = Guid.NewGuid();
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(4),
            [new(id, 1, Update(Start, 10, 2)), new(id, 2, Update(Start.AddSeconds(1), 10, 2, 2))], null, DepthCoverage.ReceivedChanges, null);
        var frame = LiquidityHeatmapRenderer.BuildFrame(query, 2, TimeSpan.FromSeconds(2));
        var bestBid = frame.Cells.Where(c => c.Side == BookSide.Bid && c.PriceTicks == -6).ToArray();
        Assert.Equal(2, bestBid.Length);
        Assert.All(bestBid, cell => Assert.Equal(10m, cell.RestingSize));
        Assert.Equal(Start.AddSeconds(3), bestBid[1].To);
        Assert.Contains(frame.Gaps, gap => gap.From == Start.AddSeconds(3) && gap.To == Start.AddSeconds(4));
        Assert.DoesNotContain(frame.Cells, cell => cell.PriceTicks >= 0);
        var clipped = LiquidityHeatmapRenderer.BuildFrame(query, 2, TimeSpan.FromSeconds(2), minimumTicks: -5, maximumTicks: -5);
        Assert.All(clipped.Cells, cell => { Assert.Equal(-6m, cell.PriceTicks); Assert.Equal(10m, cell.RestingSize); });
    }

    [Fact]
    public void Disconnect_sequence_loss_and_missing_sides_never_become_fresh_zero_depth()
    {
        using var fixture = new DepthFixture();
        using var history = new DepthHistory(Instrument());
        history.Start(fixture.Root, new() { Enabled = true }, Start);
        history.Receive(Update(Start, 10, 2), Start);
        Assert.NotNull(history.Current);
        var skipped = history.Receive(Update(Start.AddSeconds(1), 20, 3, 3, full: false), Start.AddSeconds(1));
        Assert.Equal(DepthGapReason.Sequence, skipped.GapBefore);
        Assert.Null(history.Current);
        history.Receive(Update(Start.AddSeconds(2), 20, 3, 4), Start.AddSeconds(2));
        Assert.NotNull(history.Current);
        history.MarkGap(Start.AddSeconds(3), DepthGapReason.Disconnected, "fake disconnect");
        Assert.Null(history.Current);
        history.Receive(Update(Start.AddSeconds(4), 20, 3, 5, full: false), Start.AddSeconds(4));
        Assert.Null(history.Current);
        history.Receive(Update(Start.AddSeconds(5), 20, 3, 6), Start.AddSeconds(5));
        history.Refresh(Start.AddSeconds(16));
        Assert.Null(history.Current);
        Assert.True(history.Status.IsStale);
        var absent = history.Receive(new DepthUpdate
        {
            Snapshot = new DepthSnapshot([new BookLevel(-5, 10)], [], Start.AddSeconds(17)),
            IsFullRefresh = true, ReceivedAt = Start.AddSeconds(17),
        }, Start.AddSeconds(17));
        Assert.True(absent.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.False(absent.IsUsable);
        Assert.False(DepthStatistics.Measure(history.Query(Start.AddSeconds(17), Start.AddSeconds(18)), 0,
            DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10), allowPartial: true).IsAvailable);
    }

    [Fact]
    public void Source_changes_are_visible_and_cannot_form_a_fictitious_consolidated_average()
    {
        using var fixture = new DepthFixture();
        using var history = new DepthHistory(Instrument());
        history.Start(fixture.Root, new() { Enabled = true }, Start);
        history.Receive(Update(Start, 10, 2), Start);
        var changed = history.Receive(Update(Start.AddSeconds(1), 100, 5, 1, "venue-b"), Start.AddSeconds(1));
        Assert.Equal(DepthGapReason.FeedChanged, changed.GapBefore);
        Assert.Equal("venue-b", history.Current!.Venue);
        var query = history.Query(Start, Start.AddSeconds(2));
        var average = DepthStatistics.Measure(query, 0, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10));
        Assert.False(average.IsAvailable);
        Assert.Contains("consolidated", average.Message);
        var last = DepthStatistics.Measure(query, 0, DepthAggregation.Last, TimeSpan.FromSeconds(10));
        Assert.Equal((100m, 5m), (last.Bid, last.Ask));
    }

    [Fact]
    public async Task Disabled_recording_creates_no_files_and_retention_does_not_touch_raw_source_data()
    {
        using var fixture = new DepthFixture();
        using (var disabled = new DepthHistory(Instrument()))
        {
            disabled.Start(fixture.Root, new(), Start);
            disabled.Receive(Update(Start, 10, 2), Start);
            disabled.Stop(Start.AddSeconds(1));
            Assert.Empty(disabled.Query(Start, Start.AddSeconds(2)).Samples);
            Assert.False(Directory.Exists(fixture.Root));
        }
        Directory.CreateDirectory(fixture.Root);
        var source = Path.Combine(fixture.Root, "raw-source.bin");
        byte[] original = [11, 22, 33, 44];
        File.WriteAllBytes(source, original);
        using var history = new DepthHistory(Instrument());
        history.Start(fixture.Root, new() { Enabled = true, RetentionDays = 1, MaxMemorySamples = 2, MaxDiskBytes = 4096, MaxSegmentBytes = 1024 }, Start);
        history.Receive(Update(Start, 10, 2), Start);
        history.Receive(Update(Start.AddDays(1), 20, 3, 2), Start.AddDays(1));
        history.Receive(Update(Start.AddDays(1).AddSeconds(1), 30, 4, 3), Start.AddDays(1).AddSeconds(1));
        history.Stop(Start.AddDays(1).AddSeconds(2));
        Assert.True(history.Query(Start, Start.AddDays(2)).Samples.Count <= 2);
        Assert.Empty((await history.LoadAsync(Start.Date, Start.Date.AddDays(1))).Samples);
        Assert.NotEmpty((await history.LoadAsync(Start.AddDays(1).Date, Start.Date.AddDays(2))).Samples);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.InRange(Directory.EnumerateFiles(Path.Combine(fixture.Root, "Depth"), "*.depth", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length), 1, 4096);
    }

    [Fact]
    public async Task Write_failure_is_visible_while_live_book_remains_available_and_shutdown_drains_owned_writer()
    {
        using var fixture = new DepthFixture();
        Directory.CreateDirectory(fixture.Root);
        string blocked = Path.Combine(fixture.Root, "not-a-directory");
        File.WriteAllText(blocked, "preserve");
        using var history = new DepthHistory(Instrument());
        history.Start(blocked, new() { Enabled = true }, Start);
        history.Receive(Update(Start, 10, 2), Start);
        await Until(() => history.Status.RecordingState == DepthRecordingState.Error);
        Assert.NotNull(history.Current);
        Assert.Contains("failed", history.Status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(history.Status.DroppedRecords > 0);
        history.Stop(Start.AddSeconds(1));
        Assert.Equal(DepthRecordingState.Error, history.Status.RecordingState);
        Assert.Equal("preserve", File.ReadAllText(blocked));
        Assert.Empty((await history.LoadAsync(Start, Start.AddMinutes(1))).Samples);
    }

    [Fact]
    public void Limits_preserve_source_arrays_and_explicitly_label_partial_levels_and_changes()
    {
        using var fixture = new DepthFixture();
        using var history = new DepthHistory(Instrument());
        history.Start(fixture.Root, new() { Enabled = true, MaxLevelsPerSide = 1, MaxChangesPerUpdate = 1 }, Start);
        var source = Update(Start, 10, 2);
        var recorded = history.Receive(source, Start);
        Assert.Equal(2, source.Snapshot.Bids.Length);
        Assert.Equal(2, source.Changes.Count);
        Assert.Single(recorded.Snapshot.Bids);
        Assert.Single(recorded.Changes);
        Assert.True(recorded.Coverage.HasFlag(DepthCoverage.Truncated));
        Assert.True(recorded.Coverage.HasFlag(DepthCoverage.PartialBook));
        var query = history.Query(Start, Start.AddSeconds(1));
        Assert.False(DepthStatistics.Measure(query, 20, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10)).IsAvailable);
        Assert.True(DepthStatistics.Measure(query, 20, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10), allowPartial: true).IsAvailable);
    }

    [Fact]
    public void Study_modes_preserve_true_zero_missing_and_repeated_forming_bar_replacement()
    {
        using var fixture = new DepthFixture();
        using var feed = new DepthFeed { Now = Start.AddMinutes(3) };
        using var data = new InstrumentData(Instrument(), feed);
        for (int i = 0; i < 3; i++) data.Trades.Add(new Trade(i + 1, Start.AddMinutes(i), -1.25m, 1, TradeSide.None));
        data.DepthHistory.Start(fixture.Root, new(), feed.Now);
        var id = Guid.NewGuid();
        DepthSample[] records =
        [
            new(id, 1, Update(Start, 10, 2)),
            new(id, 2, Update(Start.AddSeconds(30), 30, 6, 2)),
            new(id, 3, Update(Start.AddMinutes(1), 50, 10, 3)),
        ];
        data.DepthHistory.Apply(new(Start, Start.AddMinutes(3), records, null));
        using var model = new ChartModel(data, ChartPeriod.M1);
        var study = new OrderbookDelta { WithinTicks = 0, MaxAgeSeconds = 120 };
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
        var main = Assert.IsType<OptionalValueDataSeries>(study.DataSeries[0]);
        Assert.Equal(16m, main[0]);
        Assert.Equal(40m, main[1]);
        Assert.False(main.IsAvailable(2));
        Assert.False(study.IsAvailable);
        study.View = OrderbookDelta.BookView.BidAsk; model.Update();
        Assert.Equal(20m, ((ValueDataSeries)study.DataSeries[1])[0]);
        Assert.Equal(4m, ((ValueDataSeries)study.DataSeries[2])[0]);
        study.View = OrderbookDelta.BookView.Imbalance; model.Update();
        Assert.InRange(main[0], 66.666666m, 66.666667m);
        study.View = OrderbookDelta.BookView.Change; model.Update();
        Assert.False(main.IsAvailable(0));
        Assert.True(main.IsAvailable(1));
        Assert.Equal(24m, main[1]);
        study.View = OrderbookDelta.BookView.Delta; model.Update();
        records[0] = new(id, 1, Update(Start, 12, 2));
        data.DepthHistory.Apply(new(Start, Start.AddMinutes(3), records, null));
        study.HostCalculate(0);
        Assert.Equal(17m, main[0]);
        study.HostCalculate(0);
        Assert.Equal(17m, main[0]);
        records[0] = new(id, 1, Update(Start, 2, 2));
        data.DepthHistory.Apply(new(Start, Start.AddMinutes(3), [records[0]], null));
        study.HostCalculate(0);
        Assert.True(main.IsAvailable(0));
        Assert.Equal(0m, main[0]);
        Assert.False(main.IsAvailable(1));
    }

    [Fact]
    public async Task Pump_preserves_received_order_and_callbacks_and_disconnect_clears_legacy_current_depth()
    {
        using var fixture = new DepthFixture();
        using var feed = new DepthFeed { Now = Start.AddSeconds(10) };
        using var data = new InstrumentData(Instrument(), feed) { RecordingRoot = fixture.Root, RecordLive = false, DepthRecording = new() { Enabled = true } };
        data.Start(Start);
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        using var model = new ChartModel(data, ChartPeriod.M1);
        var capture = new DepthCapture();
        Assert.Null(model.AddIndicator(capture, applyDefaultColors: false).Error);
        var sink = feed.Sink!;
        sink.OnDepth(data.Instrument, Update(feed.Now, 10, 2));
        sink.OnDepth(data.Instrument, Update(feed.Now, 11, 3, 2, full: false));
        sink.OnDepth(data.Instrument, Update(feed.Now, 12, 4, 3));
        Assert.Empty(capture.Updates);
        data.Pump();
        Assert.Equal(new long?[] { 1, 2, 3 }, capture.Updates.Select(u => u.Sequence));
        Assert.Equal(new decimal[] { 10, 11, 12 }, capture.Updates.Select(u => u.Snapshot.BestBidSize));
        Assert.Equal(12m, model.MarketDepthInfo.Current!.Snapshot.BestBidSize);
        Assert.Contains(model.MarketDepthInfo.GetMarketDepthSnapshot(), arg => arg.IsBid && arg.Price == -1.25m && arg.Volume == 12);
        sink.OnStatus(data.Instrument, FeedStatus.Disconnected, "fake disconnect");
        data.Pump();
        Assert.Null(model.MarketDepthInfo.Current);
        Assert.Empty(model.MarketDepthInfo.GetMarketDepthSnapshot());
        Assert.False(capture.BestQuotes[^1].IsAvailable);
        feed.Now = feed.Now.AddSeconds(1);
        sink.OnStatus(data.Instrument, FeedStatus.Reconnecting, "fake reconnect");
        sink.OnStatus(data.Instrument, FeedStatus.Connected, null);
        sink.OnDepth(data.Instrument, Update(feed.Now, 15, 5, 4));
        data.Pump();
        Assert.Equal(15m, model.MarketDepthInfo.Current!.Snapshot.BestBidSize);
        data.Stop();
        Assert.Equal(0, feed.Subscriptions);
        sink.OnDepth(data.Instrument, Update(feed.Now.AddSeconds(1), 999, 1, 5));
        data.Pump();
        Assert.DoesNotContain(capture.Updates, update => update.Snapshot.BestBidSize == 999);
    }

    [Fact]
    public async Task Shared_charts_load_independent_selected_past_ranges_instead_of_only_recent_UTC_memory()
    {
        using var fixture = new DepthFixture();
        using (var writer = new DepthHistory(Instrument()))
        {
            writer.Start(fixture.Root, new() { Enabled = true }, Start);
            writer.Receive(Update(Start, 10, 2), Start);
            writer.Receive(Update(Start.AddDays(1), 100, 20, 2), Start.AddDays(1));
            writer.Stop(Start.AddDays(1).AddSeconds(1));
        }
        using var feed = new DepthFeed { Now = Start.AddMonths(2) };
        using var data = new InstrumentData(Instrument(), feed) { RecordingRoot = fixture.Root };
        data.DepthHistory.Start(fixture.Root, new(), feed.Now);
        data.Trades.Add(new Trade(1, Start, -1.25m, 1, TradeSide.None));
        data.Trades.Add(new Trade(2, Start.AddDays(1), -1.25m, 1, TradeSide.None));
        using var first = new ChartModel(data, ChartPeriod.M1);
        using var second = new ChartModel(data, ChartPeriod.M1);
        first.EnsureDepthHistory(Start, Start.AddMinutes(1));
        second.EnsureDepthHistory(Start.AddDays(1), Start.AddDays(1).AddMinutes(1));
        await Until(() =>
        {
            first.Update(); second.Update();
            return !first.IsLoadingDepthHistory && !second.IsLoadingDepthHistory;
        });
        Assert.Equal(10m, Assert.Single(first.GetDepthHistory(Start, Start.AddSeconds(1)).Samples).Snapshot.BestBidSize);
        Assert.Equal(100m, Assert.Single(second.GetDepthHistory(Start.AddDays(1), Start.AddDays(1).AddSeconds(1)).Samples).Snapshot.BestBidSize);
        Assert.Empty(first.GetDepthHistory(Start.AddDays(1), Start.AddDays(1).AddSeconds(1)).Samples);
        Assert.Empty(second.GetDepthHistory(Start, Start.AddSeconds(1)).Samples);
        Assert.Empty(data.DepthHistory.Query(Start, Start.AddDays(2)).Samples);
        // Replacing a pending request and disposing cancels/drains the owned disk reader.
        first.EnsureDepthHistory(Start.AddYears(-1), Start);
        first.Dispose();
        first.Update();
        Assert.Empty(data.DepthHistory.Query(Start, Start.AddDays(2)).Samples);
    }

    [Fact]
    public void Book_builder_preserves_repeated_received_changes_and_labels_local_uncross_repairs()
    {
        var book = new OrderBookBuilder();
        book.Set(BookSide.Bid, -5, 10);
        book.Set(BookSide.Ask, -3, 2);
        book.BuildUpdate(Start, fullRefresh: true);
        book.Set(BookSide.Bid, -5, 10);
        book.Set(BookSide.Bid, -5, 10);
        book.Set(BookSide.Ask, -2, 0);
        var repeated = book.BuildUpdate(Start.AddSeconds(1));
        Assert.Equal(3, repeated.Changes.Count);
        Assert.All(repeated.Changes, change => Assert.Equal(BookChangeOrigin.Received, change.Origin));
        book.Set(BookSide.Bid, -2, 5);
        book.Uncross(BookSide.Bid);
        var repaired = book.BuildUpdate(Start.AddSeconds(2));
        Assert.Contains(repaired.Changes, change => change.Side == BookSide.Ask && change.PriceTicks == -3 && change.Size == 0 && change.Origin == BookChangeOrigin.BookRepair);
        Assert.True(repaired.Coverage.HasFlag(DepthCoverage.PartialBook));
    }

    [Fact]
    public void Chart_initial_aggregation_is_bounded_and_same_count_corrections_advance_calculation_identity()
    {
        using var feed = new DepthFeed { Now = Start.AddMinutes(1) };
        using var data = new InstrumentData(Instrument(), feed);
        int count = CandleSeries.InitialBatchSize + 5;
        for (int i = 0; i < count; i++) data.Trades.Add(new Trade(i + 1, Start.AddTicks(i), -1.25m, 1, TradeSide.None));
        using var model = new ChartModel(data, ChartPeriod.M1);
        Assert.True(model.IsAggregating);
        Assert.Equal(CandleSeries.InitialBatchSize, model.Series.ProcessedTrades);
        Assert.Equal(CandleSeries.InitialBatchSize, model.Series[0].Volume);
        model.Update();
        Assert.False(model.IsAggregating);
        Assert.Equal(count, model.Series[0].Volume);
        long generation = model.DataGeneration;
        data.Trades.Clear();
        for (int i = 0; i < count; i++) data.Trades.Add(new Trade(i + 1, Start.AddTicks(i), -2.50m, 1, TradeSide.None));
        model.Update(); model.Update();
        Assert.True(model.DataGeneration > generation);
        Assert.Equal(-2.50m, model.GetCandle(0).Close);
        Assert.Equal(count, model.Series[0].Volume);
    }

    [Fact]
    public async Task An_owned_writer_lease_prevents_competing_pruning_and_explicit_reconfiguration_recovers_after_release()
    {
        using var fixture = new DepthFixture();
        using var first = new DepthHistory(Instrument());
        using var second = new DepthHistory(Instrument());
        first.Start(fixture.Root, new() { Enabled = true }, Start);
        first.Receive(Update(Start, 10, 2), Start);
        await Until(() => first.Status.LastPersistedAt == Start);
        second.Start(fixture.Root, new() { Enabled = true }, Start);
        await Until(() => second.Status.RecordingState == DepthRecordingState.Error);
        second.Receive(Update(Start.AddSeconds(1), 999, 2), Start.AddSeconds(1));
        Assert.NotNull(second.Current);
        first.Stop(Start.AddSeconds(2));
        second.Configure(fixture.Root, new() { Enabled = true }, Start.AddSeconds(3));
        second.Receive(Update(Start.AddSeconds(3), 30, 6, 2, full: false), Start.AddSeconds(3));
        second.Stop(Start.AddSeconds(4));
        Assert.Equal(DepthRecordingState.Stopped, second.Status.RecordingState);
        using var reader = new DepthHistory(Instrument());
        reader.Start(fixture.Root, new(), Start);
        reader.Apply(await reader.LoadAsync(Start, Start.AddMinutes(1)));
        Assert.Equal(new decimal[] { 10, 30 }, reader.Query(Start, Start.AddMinutes(1)).Samples.Where(s => s.IsUsable).Select(s => s.Snapshot.BestBidSize));
    }

    [Fact]
    public async Task Corrupt_tail_preserves_complete_prefix_and_selected_range_limit_is_explicitly_incomplete()
    {
        using var fixture = new DepthFixture();
        using var history = new DepthHistory(Instrument());
        history.Start(fixture.Root, new() { Enabled = true, MaxMemorySamples = 2 }, Start);
        for (int i = 0; i < 5; i++) history.Receive(Update(Start.AddSeconds(i), 10 + i, 2, i + 1), Start.AddSeconds(i));
        history.Stop(Start.AddSeconds(5));
        var limited = await history.LoadAsync(Start, Start.AddMinutes(1));
        Assert.True(limited.IsTruncated);
        Assert.Equal(2, limited.Samples.Count);
        history.Apply(limited);
        Assert.True(history.Query(Start, Start.AddMinutes(1)).Coverage.HasFlag(DepthCoverage.Truncated));
        string segment = Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "Depth"), "*.depth", SearchOption.AllDirectories));
        using (var file = new FileStream(segment, FileMode.Append, FileAccess.Write)) file.Write(new byte[] { 1, 2, 3 });
        using var complete = new DepthHistory(Instrument());
        complete.Start(fixture.Root, new(), Start);
        var loaded = await complete.LoadAsync(Start, Start.AddMinutes(1));
        Assert.NotNull(loaded.Message);
        complete.Apply(loaded);
        var query = complete.Query(Start, Start.AddMinutes(1));
        Assert.Equal(new decimal[] { 10, 11, 12, 13, 14 }, query.Samples.Where(s => s.IsUsable).Select(s => s.Snapshot.BestBidSize));
        Assert.True(query.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.Contains("end", loaded.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recording_preferences_do_not_invalidate_a_continuous_provider_book_or_restart_the_subscription()
    {
        using var fixture = new DepthFixture();
        using var feed = new DepthFeed { Now = Start.AddSeconds(1) };
        using var data = new InstrumentData(Instrument(), feed) { RecordingRoot = fixture.Root, RecordLive = false };
        data.Start(Start);
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        feed.Sink!.OnDepth(data.Instrument, Update(feed.Now, 10, 2));
        data.Pump();
        Assert.NotNull(data.DepthHistory.Current);
        data.ConfigureDepthRecording(new() { Enabled = true });
        Assert.Equal(1, feed.Subscriptions);
        Assert.NotNull(data.DepthHistory.Current);
        feed.Now = feed.Now.AddSeconds(1);
        feed.Sink.OnDepth(data.Instrument, Update(feed.Now, 20, 4, 2, full: false));
        data.Pump();
        Assert.Equal(20m, data.DepthHistory.Current!.Snapshot.BestBidSize);
        data.ConfigureDepthRecording(new());
        Assert.NotNull(data.DepthHistory.Current);
        Assert.Equal(DepthRecordingState.Disabled, data.DepthHistory.Status.RecordingState);
        Assert.Equal(1, feed.Subscriptions);
    }

    [Fact]
    public async Task SDK_trade_callbacks_preserve_positive_ID_compact_quality_and_cumulative_trades_remain_raw_only()
    {
        using var feed = new DepthFeed { Now = Start.AddMinutes(1) };
        using var data = new InstrumentData(Instrument(), feed) { RecordLive = false };
        data.Start(Start);
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        using var model = new ChartModel(data, ChartPeriod.M1);
        var live = new TradeCapture();
        Assert.Null(model.AddIndicator(live, applyDefaultColors: false).Error);
        feed.Sink!.OnTrades(data.Instrument,
        [
            new Trade(1, Start.AddSeconds(1), -1.25m, 1, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(2), -1.25m, 100, TradeSide.Buy, new(DataQuality.Compact, 0, "fake-aggregate", 0, 12)),
            new Trade(-1, Start.AddSeconds(3), -1.25m, 99, TradeSide.Buy),
        ]);
        data.Pump();
        Assert.Equal(3, live.Trades.Count);
        Assert.True(live.Trades[0].IsObservedTrade);
        Assert.False(live.Trades[1].IsObservedTrade);
        Assert.Equal(DataQuality.Compact, live.Trades[1].Quality);
        Assert.Equal(12, live.Trades[1].ObservedTradeCount);
        Assert.Equal("fake-aggregate", live.Trades[1].SourceIdentity);
        Assert.False(live.Trades[2].IsObservedTrade);
        Assert.Equal(1m, Assert.Single(live.Cumulative).Volume);
        var historical = new TradeCapture();
        Assert.Null(model.AddIndicator(historical, applyDefaultColors: false).Error);
        Assert.Equal(1m, Assert.Single(historical.Historical).Volume);
        Assert.All(historical.Historical.SelectMany(t => t.Ticks), trade => Assert.True(trade.IsObservedTrade));
    }

    [Fact]
    public async Task Cumulative_history_waits_for_consumed_rows_then_shares_the_live_tail_and_refreshes_after_correction()
    {
        using var fixture = new DepthFixture();
        using var feed = new DepthFeed { Now = Start.AddMinutes(3) };
        var instrument = new Instrument { Symbol = "CUMULATIVE-AGG-FIXTURE", FeedId = "fake", Exchange = "venue-a", TickSize = 0.25m };
        using var data = new InstrumentData(instrument, feed) { RecordingRoot = fixture.Root, RecordLive = false };
        data.Start(Start);
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; });
        int batch = CandleSeries.InitialBatchSize;
        var source = new Trade[2 * batch + 1];
        for (int i = 0; i < source.Length; i++)
        {
            int group = i / batch;
            // The second 100k rows share the first forming candle; its nominal end is not a consumed-row bound.
            var at = group == 0 ? Start : group == 1 ? Start.AddSeconds(30) : Start.AddMinutes(1);
            source[i] = new Trade(i + 1, at, -1.25m + group * 0.25m, 1, TradeSide.Buy);
        }
        feed.Sink!.OnTrades(instrument, source);
        data.Pump();
        Assert.Equal(source.Length, data.Trades.Count);
        using var model = new ChartModel(data, ChartPeriod.M1);
        var big = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 1 };
        var bigSlot = model.AddIndicator(big, applyDefaultColors: false);
        var capture = new CumulativeBoundaryCapture();
        var captureSlot = model.AddIndicator(capture, applyDefaultColors: false);
        Assert.True(model.IsAggregating);
        Assert.Equal(batch, model.Series.ProcessedTrades);
        Assert.Equal(0, big.BubbleCount);
        Assert.Empty(capture.ResponseBars);

        // A real live print arrives beyond the consumed prefix while history is pending.
        var tailTime = Start.AddMinutes(1);
        feed.Sink.OnTrades(instrument, [new Trade(source.Length + 1, tailTime.AddTicks(1), -0.75m, 2, TradeSide.Buy)]);
        data.Pump();
        Assert.True(model.IsAggregating);
        Assert.Equal(2 * batch, model.Series.ProcessedTrades);
        Assert.Equal(1, model.Series.Count);
        Assert.Equal(0, big.BubbleCount);
        Assert.Empty(capture.ResponseBars);

        model.Update();
        Assert.False(model.IsAggregating);
        Assert.Equal(source.Length + 1, model.Series.ProcessedTrades);
        Assert.Equal(new[] { 2 }, capture.ResponseBars);
        Assert.Equal(new[] { tailTime }, capture.ResponseLastBars);
        Assert.Equal(new[] { Start, Start.AddSeconds(30), tailTime }, capture.Historical.Select(trade => trade.Time));
        Assert.Equal(new decimal[] { batch, batch, 3 }, capture.Historical.Select(trade => trade.Volume));
        Assert.Equal(3, big.BubbleCount);
        var open = capture.Historical[^1];
        Assert.Equal(new long[] { source.Length, source.Length + 1 }, open.Ticks.Select(tick => tick.Id));

        feed.Sink.OnTrades(instrument, [new Trade(source.Length + 2, tailTime.AddTicks(2), -0.75m, 4, TradeSide.Buy)]);
        data.Pump();
        Assert.Same(open, Assert.Single(capture.Updates));
        Assert.Equal(7m, open.Volume);
        Assert.Equal(3, open.Ticks.Count);
        Assert.Equal(3, big.BubbleCount);
        Assert.Single(capture.ResponseBars);

        // Same-count source correction rebuilds the view and replaces, rather than appends, history.
        feed.Sink.OnTrades(instrument, [new Trade(source.Length + 2, tailTime.AddTicks(2), -0.75m, 9, TradeSide.Buy)]);
        data.Pump();
        Assert.True(model.IsAggregating);
        Assert.Equal(0, big.BubbleCount);
        Assert.Single(capture.ResponseBars);
        model.Update();
        Assert.True(model.IsAggregating);
        Assert.Single(capture.ResponseBars);
        model.Update();
        Assert.False(model.IsAggregating);
        Assert.Equal(new[] { 2, 2 }, capture.ResponseBars);
        Assert.Equal(3, big.BubbleCount);
        Assert.Equal(3, capture.Historical.Count);
        Assert.Equal(12m, capture.Historical[^1].Volume);
        Assert.NotSame(open, capture.Historical[^1]);
        Assert.All(capture.Historical.SelectMany(trade => trade.Ticks), tick => Assert.True(tick.IsObservedTrade));
        Assert.Null(bigSlot.Error);
        Assert.Null(captureSlot.Error);
    }

    private sealed class CumulativeBoundaryCapture : Indicator
    {
        private bool _requested;
        public List<int> ResponseBars { get; } = [];
        public List<DateTime> ResponseLastBars { get; } = [];
        public List<CumulativeTrade> Historical { get; } = [];
        public List<CumulativeTrade> Updates { get; } = [];
        protected override void OnRecalculate() => _requested = false;
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar != 0 || _requested) return;
            _requested = true;
            RequestForCumulativeTrades(new(GetCandle(0).Time));
        }
        protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> trades)
        {
            ResponseBars.Add(CurrentBar);
            ResponseLastBars.Add(GetCandle(CurrentBar - 1).Time);
            Historical.Clear(); Historical.AddRange(trades);
        }
        protected override void OnUpdateCumulativeTrade(CumulativeTrade trade) => Updates.Add(trade);
    }

    private sealed class TradeCapture : Indicator
    {
        public List<MarketDataArg> Trades { get; } = new();
        public List<CumulativeTrade> Cumulative { get; } = new();
        public List<CumulativeTrade> Historical { get; } = new();
        protected override void OnInitialize() => RequestForCumulativeTrades(new(Start));
        protected override void OnCalculate(int bar, decimal value) { }
        protected override void OnNewTrades(IEnumerable<MarketDataArg> trades) => Trades.AddRange(trades);
        protected override void OnCumulativeTrade(CumulativeTrade trade) => Cumulative.Add(trade);
        protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> trades) => Historical.AddRange(trades);
    }

    private sealed class DepthCapture : Indicator
    {
        public List<DepthUpdate> Updates { get; } = new();
        public List<MarketDataArg> BestQuotes { get; } = new();
        protected override void OnCalculate(int bar, decimal value) { }
        protected override void OnDepthSnapshot(DepthUpdate update) => Updates.Add(update);
        protected override void OnBestBidAskChanged(MarketDataArg depth) => BestQuotes.Add(depth);
    }

    private sealed class DepthFeed : IMarketDataFeed
    {
        public string Id => "fake";
        public string DisplayName => "Fake depth fixture";
        public bool SupportsHistory => false;
        public bool IsLive => false;
        public DateTime Now { get; set; } = Start;
        public IMarketDataSink? Sink { get; private set; }
        public int Subscriptions { get; private set; }
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>([Instrument()]);
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            Sink = sink; Subscriptions++;
            sink.OnStatus(instrument, FeedStatus.Connecting, "Fake connecting");
            sink.OnStatus(instrument, FeedStatus.Connected, null);
            return new EndSubscription(() => Subscriptions--);
        }
        public void Dispose() { }
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
            Assert.True(DateTime.UtcNow < until, "The fake depth operation did not settle.");
            await Task.Delay(1);
        }
    }

    private sealed class DepthFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tradinghub-depth-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
