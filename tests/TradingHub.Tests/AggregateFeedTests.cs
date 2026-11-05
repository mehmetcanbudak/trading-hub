using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class AggregateFeedTests
{
    private static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Failed_constituent_keeps_chronological_venue_provenance_and_recovers_on_the_next_load()
    {
        using var binance = new Constituent("binance-futures", 0.1m);
        using var bybit = new Constituent("bybit-linear", 0.25m);
        binance.Trades.AddRange([
            new Trade(1, Start.AddSeconds(1), 100.1m, 2, TradeSide.Buy),
            new Trade(2, Start.AddSeconds(3), 100.2m, 3, TradeSide.Sell),
        ]);
        bybit.Trades.Add(new Trade(1, Start.AddSeconds(2), 100.25m, 4, TradeSide.Buy));
        using var feed = new AggregateFeed([binance, bybit]);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        bybit.TradeFailure = new HttpRequestException("fixture Bybit outage");

        var partial = await Trades(feed, instrument);

        Assert.Equal(new[] { Start.AddSeconds(1), Start.AddSeconds(3) }, partial.Select(t => t.Time));
        Assert.All(partial, trade =>
        {
            Assert.Equal("binance-futures", trade.Provenance.SourceIdentity);
            Assert.True(trade.IsObservedTrade);
            Assert.True(trade.Provenance.IsPartial);
        });
        var failed = feed.GetCoverage(instrument, AggregateOperation.Trades);
        Assert.True(failed.IsPartial);
        Assert.Contains(failed.Constituents, c => c.FeedId == "bybit-linear" && c.Status == FeedStatus.Error && c.Message == "fixture Bybit outage");

        bybit.TradeFailure = null;
        var recovered = await Trades(feed, instrument);

        Assert.Equal(new[] { 100.1m, 100.25m, 100.2m }, recovered.Select(t => t.Price));
        Assert.Equal(new[] { "binance-futures", "bybit-linear", "binance-futures" }, recovered.Select(t => t.Provenance.SourceIdentity));
        Assert.Equal(new long[] { 1, 1, 2 }, recovered.Select(trade => trade.Id));
        Assert.Equal(3, recovered.Select(trade => TradeIdentity.From(trade)).Distinct().Count());
        Assert.All(recovered, trade => Assert.False(trade.Provenance.IsPartial));
        Assert.False(feed.GetCoverage(instrument, AggregateOperation.Trades).IsPartial);
        Assert.Equal(0.05m, instrument.TickSize);
        Assert.False(feed.IsExecutable);
        Assert.Equal(AggregateFeed.FeedId, instrument.FeedId);
        Assert.Equal("binance-futures", feed.BookFeedId);
    }

    [Fact]
    public async Task Candle_corrections_replace_venue_volume_missing_bars_stay_partial_and_unknown_buy_side_is_not_measured()
    {
        using var binance = new Constituent("binance-futures");
        using var bybit = new Constituent("bybit-linear");
        long time = new DateTimeOffset(Start).ToUnixTimeMilliseconds();
        binance.Klines.AddRange([
            new Kline(time + 60_000, 105, 115, 100, 110, 8, 5, true),
            new Kline(time, 100, 109, 91, 104, 7, 4, true),
            new Kline(time, 100, 110, 90, 105, 10, 6, true),
        ]);
        bybit.Klines.Add(new Kline(time, 101, 112, 95, 106, 20, 0, false));
        using var feed = new AggregateFeed([binance, bybit]);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var candles = new List<Kline>();

        await feed.LoadKlinesAsync(instrument, Start, Start.AddMinutes(2), 1, 0, batch =>
        {
            candles.AddRange(batch);
            return true;
        }, null, CancellationToken.None);

        Assert.Equal(new[] { time, time + 60_000 }, candles.Select(c => c.OpenTimeMs));
        Assert.Equal((100m, 112m, 90m, 105m, 30m), (candles[0].Open, candles[0].High, candles[0].Low, candles[0].Close, candles[0].Volume));
        Assert.Equal(6m, candles[0].BuyVolume);
        Assert.False(candles[0].HasBuyVolume);
        Assert.Equal(DataQuality.Synthetic | DataQuality.InferredSide, candles[0].Provenance.Quality);
        Assert.Equal("aggregate:binance-futures+bybit-linear", candles[0].Provenance.SourceIdentity);
        Assert.Equal(60, candles[0].Provenance.ResolutionSeconds);
        Assert.Equal(8m, candles[1].Volume);
        Assert.Equal(DataQuality.Synthetic | DataQuality.Partial, candles[1].Provenance.Quality);
        Assert.Equal("aggregate:binance-futures", candles[1].Provenance.SourceIdentity);
        Assert.True(feed.GetCoverage(instrument, AggregateOperation.Candles).IsPartial);

        binance.CandleFailure = new HttpRequestException("primary candle outage");
        candles.Clear();
        await feed.LoadKlinesAsync(instrument, Start, Start.AddMinutes(1), 1, 0, batch => { candles.AddRange(batch); return true; }, null, CancellationToken.None);
        var fallback = Assert.Single(candles);
        Assert.Equal((101m, 112m, 95m, 106m, 20m), (fallback.Open, fallback.High, fallback.Low, fallback.Close, fallback.Volume));
        Assert.True((fallback.Provenance.Quality & DataQuality.Partial) != 0);
        Assert.Equal("binance-futures", feed.GetCoverage(instrument, AggregateOperation.Candles).BookFeedId);
        Assert.Contains(feed.GetCoverage(instrument, AggregateOperation.Candles).Constituents,
            c => c.FeedId == "binance-futures" && c.Status == FeedStatus.Error && c.Message == "primary candle outage");
    }

    [Fact]
    public async Task Compact_buckets_and_synthetic_candles_never_become_individual_large_trades_or_raw_footprint()
    {
        using var first = new Constituent("first");
        using var second = new Constituent("second");
        first.Trades.Add(new Trade(1, Start.AddSeconds(1), 100, 2, TradeSide.Buy));
        first.Trades.Add(new Trade(-2, Start.AddSeconds(2), 101, 1000, TradeSide.Buy,
            new TradeProvenance(DataQuality.Compact, 60, CorrectionRevision: 3, ObservedTradeCount: 20)));
        second.Trades.Add(new Trade(-3, Start.AddSeconds(3), 102, 10000, TradeSide.Sell,
            new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide, 60)));
        using var feed = new AggregateFeed([first, second]);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));

        var trades = await Trades(feed, instrument);
        var store = new TradeStore(instrument);
        foreach (var trade in trades) store.Add(trade);
        var series = new CandleSeries(instrument, ChartPeriod.M1);
        series.Rebuild(store);

        var candle = Assert.Single(series.Candles);
        Assert.Equal(11002m, candle.Volume);
        Assert.Equal(2m, candle.MaxTradeVolume);
        Assert.Equal(1, candle.ObservedTrades);
        Assert.Equal(20, candle.AggregatedTrades);
        Assert.True(candle.HasRealTrades);
        Assert.True(candle.HasCompactTrades);
        Assert.True(candle.HasSyntheticTrades);
        Assert.False(candle.IsFootprintComplete);
        Assert.Single(candle.Levels.Enumerate());
        Assert.Single(candle.AggregatedLevels.Enumerate());
        Assert.Equal(DataQuality.Compact, store.GetProvenance(1).Quality);
        Assert.Equal(20, store.GetProvenance(1).ObservedTradeCount);
        Assert.Equal(3, store.GetProvenance(1).CorrectionRevision);
        Assert.Equal("first", store.GetProvenance(1).SourceIdentity);
        Assert.False(trades[1].IsObservedTrade);
        Assert.False(trades[2].IsObservedTrade);
        Assert.True(trades[1].Id < 0);
        Assert.True(trades[2].Id < 0);
    }

    [Fact]
    public async Task All_failed_constituents_throw_instead_of_publishing_a_successful_empty_history_and_cancellation_is_not_swallowed()
    {
        using var first = new Constituent("first") { TradeFailure = new HttpRequestException("first unavailable") };
        using var second = new Constituent("second") { TradeFailure = new HttpRequestException("second unavailable") };
        using var feed = new AggregateFeed([first, second]);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));

        var failure = await Assert.ThrowsAsync<AggregateException>(() => Trades(feed, instrument));

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.All(feed.GetCoverage(instrument, AggregateOperation.Trades).Constituents, status => Assert.Equal(FeedStatus.Error, status.Status));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Trades(feed, instrument, cancelled.Token));
    }

    [Fact]
    public async Task A_catalog_outage_keeps_available_symbols_and_refresh_can_restore_the_constituent()
    {
        using var first = new Constituent("first") { ListingFailure = new HttpRequestException("catalog offline") };
        using var second = new Constituent("second");
        using var feed = new AggregateFeed([first, second]);

        var partial = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        Assert.True(feed.GetCoverage(partial, AggregateOperation.Instruments).IsPartial);
        Assert.Equal("first", feed.BookFeedId);

        first.ListingFailure = null;
        var recovered = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        Assert.False(feed.GetCoverage(recovered, AggregateOperation.Instruments).IsPartial);
        Assert.Equal(partial.Key, recovered.Key);
        Assert.Contains("non-executable", recovered.Description);

        second.IncludesInstrument = false;
        var oneVenue = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        Assert.True(feed.GetCoverage(oneVenue, AggregateOperation.Instruments).IsPartial);
        Assert.Contains(feed.GetCoverage(oneVenue, AggregateOperation.Instruments).Constituents,
            source => source.FeedId == "second" && source.Status == FeedStatus.Disconnected);
        second.IncludesInstrument = true;
        var listedAgain = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        Assert.False(feed.GetCoverage(listedAgain, AggregateOperation.Instruments).IsPartial);
    }

    [Fact]
    public async Task Live_tape_reorders_without_changing_exchange_times_reports_failure_recovery_and_keeps_DOM_at_the_first_venue()
    {
        var clock = new ManualClock(Start);
        using var first = new Constituent("first", 0.1m);
        using var second = new Constituent("second", 0.25m);
        using var feed = new AggregateFeed([first, second], timeProvider: clock);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Observer();
        using var subscription = feed.Subscribe(instrument, sink);
        Assert.True(first.IsSubscribed);
        Assert.True(second.IsSubscribed);
        Assert.False(feed.GetCoverage(instrument, AggregateOperation.Live).IsPartial);

        first.Sink!.OnTrades(first.Instrument, [new Trade(1, Start.AddSeconds(2), 100.1m, 2, TradeSide.Buy)]);
        second.Sink!.OnTrades(second.Instrument, [new Trade(1, Start.AddSeconds(1), 100.25m, 3, TradeSide.Sell)]);
        Assert.Empty(sink.Trades);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(new[] { Start.AddSeconds(1), Start.AddSeconds(2) }, sink.Trades.Select(t => t.Time));
        Assert.Equal(new[] { "second", "first" }, sink.Trades.Select(t => t.Provenance.SourceIdentity));
        Assert.Equal(new[] { 100.25m, 100.1m }, sink.Trades.Select(t => t.Price));

        second.Sink.OnStatus(second.Instrument, FeedStatus.Error, "fixture disconnect");
        Assert.True(feed.GetCoverage(instrument, AggregateOperation.Live).IsPartial);
        Assert.Equal(FeedStatus.Connected, sink.Statuses[^1].Status);
        first.Sink.OnTrades(first.Instrument, [new Trade(2, Start.AddSeconds(3), 101, 1, TradeSide.Buy)]);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(sink.Trades[^1].Provenance.IsPartial);
        second.Sink.OnStatus(second.Instrument, FeedStatus.Connected, null);
        Assert.False(feed.GetCoverage(instrument, AggregateOperation.Live).IsPartial);
        second.Sink.OnTrades(second.Instrument, [new Trade(2, Start.AddSeconds(4), 101.25m, 1, TradeSide.Buy)]);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.False(sink.Trades[^1].Provenance.IsPartial);

        first.Sink.OnDepth(first.Instrument, new DepthUpdate
        {
            Snapshot = new DepthSnapshot([new BookLevel(1000, 7)], [new BookLevel(1001, 8)], Start),
            Changes = [new BookChange(BookSide.Bid, 1000, 7, BookChangeOrigin.BookRepair)],
            Sequence = 42,
            IsFullRefresh = true,
            Coverage = DepthCoverage.ReceivedChanges,
        });
        second.Sink.OnDepth(second.Instrument, new DepthUpdate
        {
            Snapshot = new DepthSnapshot([new BookLevel(400, 999)], [new BookLevel(401, 999)], Start),
        });
        var depth = Assert.Single(sink.Depth);
        Assert.Equal(2000, depth.Snapshot.BestBidTicks);
        Assert.Equal(7m, depth.Snapshot.BestBidSize);
        Assert.Equal(2002, depth.Snapshot.BestAskTicks);
        Assert.Equal(2000, Assert.Single(depth.Changes).PriceTicks);
        Assert.Equal(BookChangeOrigin.BookRepair, depth.Changes[0].Origin);
        Assert.Equal("first", depth.ProviderId);
        Assert.Equal("first", depth.Venue);
        Assert.Equal(42, depth.Sequence);
        Assert.Contains("not a consolidated", depth.Message);
        first.Sink.OnStatus(first.Instrument, FeedStatus.Reconnecting, "first venue unavailable");
        second.Sink.OnDepth(second.Instrument, new DepthUpdate { Snapshot = new DepthSnapshot([new BookLevel(400, 999)], [], Start) });
        Assert.Equal(2, sink.Depth.Count);
        Assert.False(sink.Depth[^1].IsUsable);
        Assert.Equal(DepthGapReason.Disconnected, sink.Depth[^1].GapBefore);
        Assert.Equal("first", feed.GetCoverage(instrument, AggregateOperation.Live).BookFeedId);

        second.Sink.OnTrades(second.Instrument, [new Trade(3, Start.AddMilliseconds(1500), 99, 100, TradeSide.Sell)]);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(4, sink.Trades.Count);
        Assert.Contains("omitted", feed.GetCoverage(instrument, AggregateOperation.Live).Constituents[1].Message);

        subscription.Dispose();
        int statuses = sink.Statuses.Count;
        first.Sink.OnTrades(first.Instrument, [new Trade(99, Start.AddSeconds(9), 999, 999, TradeSide.Buy)]);
        first.Sink.OnDepth(first.Instrument, depth);
        first.Sink.OnStatus(first.Instrument, FeedStatus.Connected, "late");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(4, sink.Trades.Count);
        Assert.Equal(2, sink.Depth.Count);
        Assert.Equal(statuses, sink.Statuses.Count);
        Assert.False(first.IsSubscribed);
        Assert.False(second.IsSubscribed);
        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    [Fact]
    public async Task Disposal_cancels_pending_catalogs_and_disposes_a_subscription_returned_after_reentrant_shutdown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new Constituent("first")
        {
            BeforeListing = async ct =>
            {
                entered.TrySetResult();
                try { await release.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            },
        };
        using var second = new Constituent("second");
        using var feed = new AggregateFeed([first, second], timeProvider: new ManualClock(Start));
        var sink = new Observer();
        var instrument = new Instrument { FeedId = AggregateFeed.FeedId, Symbol = "BTCUSDT", TickSize = 1 };
        var pending = feed.Subscribe(instrument, sink);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.Empty(sink.Statuses);
        Assert.False(first.IsSubscribed);
        Assert.False(second.IsSubscribed);

        first.BeforeListing = null;
        await feed.GetInstrumentsAsync(CancellationToken.None);
        first.DuringSubscribe = feed.Dispose;
        using var late = feed.Subscribe(instrument, sink);
        Assert.False(first.IsSubscribed);
        Assert.False(second.IsSubscribed);
        Assert.Empty(sink.Statuses);
        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);

        using var ownedFirst = new Constituent("owned-first");
        using var ownedSecond = new Constituent("owned-second");
        var owned = new AggregateFeed([ownedFirst, ownedSecond], ownsSources: true, timeProvider: new ManualClock(Start));
        owned.Dispose();
        owned.Dispose();
        Assert.True(ownedFirst.IsDisposed);
        Assert.True(ownedSecond.IsDisposed);
    }

    [Fact]
    public async Task A_constituent_disposal_failure_does_not_leave_the_other_subscription_or_owned_feed_running()
    {
        using var first = new Constituent("first") { FailSubscriptionDisposal = true };
        using var second = new Constituent("second");
        using var feed = new AggregateFeed([first, second], timeProvider: new ManualClock(Start));
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        using var subscription = feed.Subscribe(instrument, new Observer());

        var failure = Assert.Throws<AggregateException>(subscription.Dispose);

        Assert.Contains(failure.InnerExceptions, error => error.Message == "fixture subscription disposal failure");
        Assert.False(first.IsSubscribed);
        Assert.False(second.IsSubscribed);
        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);

        using var ownedFirst = new Constituent("owned-first") { FailFeedDisposal = true };
        using var ownedSecond = new Constituent("owned-second");
        using var owned = new AggregateFeed([ownedFirst, ownedSecond], ownsSources: true, timeProvider: new ManualClock(Start));
        var ownedFailure = Assert.Throws<AggregateException>(owned.Dispose);
        Assert.Contains(ownedFailure.InnerExceptions, error => error.Message == "fixture feed disposal failure");
        Assert.True(ownedFirst.IsDisposed);
        Assert.True(ownedSecond.IsDisposed);
    }

    [Fact]
    public async Task Bar_history_keeps_partial_synthetic_source_metadata_through_the_existing_candle_consumer()
    {
        using var first = new Constituent("first");
        using var second = new Constituent("second") { CandleFailure = new HttpRequestException("no candles") };
        long time = new DateTimeOffset(Start).ToUnixTimeMilliseconds();
        first.Klines.Add(new Kline(time, 100, 110, 90, 105, 10, 6, true));
        using var feed = new AggregateFeed([first, second]);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));

        var bars = await feed.LoadBarHistoryAsync(instrument, Start, Start.AddMinutes(1), CancellationToken.None);
        var store = new TradeStore(instrument);
        foreach (var trade in bars) store.Add(trade);
        var series = new CandleSeries(instrument, ChartPeriod.M1);
        series.Rebuild(store);

        Assert.Equal(new[] { 100m, 90m, 110m, 105m }, bars.Select(t => t.Price));
        Assert.All(bars, trade =>
        {
            Assert.True(trade.Id < 0);
            Assert.False(trade.IsObservedTrade);
            Assert.True(trade.Provenance.IsPartial);
            Assert.Equal("aggregate:first", trade.Provenance.SourceIdentity);
            Assert.Equal(60, trade.Provenance.ResolutionSeconds);
        });
        var candle = Assert.Single(series.Candles);
        Assert.Equal(10m, candle.Volume);
        Assert.Equal(2m, candle.Delta);
        Assert.Equal(0m, candle.MaxTradeVolume);
        Assert.True(candle.IsSynthetic);
        Assert.True(candle.Levels.IsEmpty);
    }

    [Fact]
    public async Task Feed_disposal_cancels_held_history_and_a_late_source_cannot_publish()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new Constituent("first")
        {
            BeforeTrades = _ => { entered.TrySetResult(); return release.Task; },
        };
        first.Trades.Add(new Trade(1, Start, 100, 2, TradeSide.Buy));
        using var second = new Constituent("second");
        using var feed = new AggregateFeed([first, second]);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var published = new List<Trade>();
        var pending = feed.LoadTradesAsync(instrument, Start, Start.AddMinutes(1), null,
            batch => { published.AddRange(batch); return true; }, null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        feed.Dispose();
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(published);
        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Instrument_history_and_buffered_live_keep_same_time_constituent_events_distinct_and_dedupe_only_the_canonical_source_key(bool nativeEventKeys)
    {
        using var fixture = new DataFixture();
        var clock = new ManualClock(Start.AddMilliseconds(59_750));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var binance = new Constituent("binance-futures")
        {
            BeforeTrades = ct => { entered.TrySetResult(); return release.Task.WaitAsync(ct); },
        };
        using var bybit = new Constituent("bybit-linear") { TradeFailure = new HttpRequestException("Bybit historical tape unavailable") };
        var location = Start.AddSeconds(10);
        var historical = new Trade(77, location, 100, 2, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: binance.Id, SourceEventId: nativeEventKeys ? "same-native-key" : null));
        var historyOnly = new Trade(79, location, 120, 5, TradeSide.Sell,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: binance.Id, SourceEventId: nativeEventKeys ? "history-only" : null));
        var firstLive = new Trade(77, location, 101, 3, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: bybit.Id, SourceEventId: nativeEventKeys ? "same-native-key" : null));
        var secondLive = new Trade(nativeEventKeys ? 77 : 78, location, 101, 3, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: bybit.Id, SourceEventId: nativeEventKeys ? "bybit-second-native-key" : null));
        binance.Trades.AddRange([historical, historyOnly]);
        using var feed = new AggregateFeed([binance, bybit], timeProvider: clock);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        using var data = new InstrumentData(instrument, feed) { RecordingRoot = fixture.Root, RecordLive = false };
        data.Start(Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        bybit.Sink!.OnTrades(bybit.Instrument, [firstLive, secondLive, firstLive]);
        binance.Sink!.OnTrades(binance.Instrument, [historical]);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        data.Pump();
        Assert.True(data.IsLoadingHistory);
        Assert.Equal(0, data.Trades.Count);
        release.TrySetResult();
        await PumpUntil(data, () => !data.IsLoadingHistory);

        Assert.Null(data.HistoryError);
        Assert.Equal(4, data.Trades.Count);
        var events = Enumerable.Range(0, data.Trades.Count).Select(data.Trades.Get).ToArray();
        Assert.All(events, trade => Assert.Equal(location, trade.Time));
        Assert.Equal(nativeEventKeys ? new long[] { 77, 77, 77, 79 } : new long[] { 77, 77, 78, 79 }, events.Select(trade => trade.Id).Order());
        var binanceEvents = events.Where(trade => trade.Provenance.SourceIdentity == binance.Id).ToArray();
        Assert.Equal(2, binanceEvents.Length);
        Assert.Equal((100m, 2m), (binanceEvents.Single(trade => trade.Id == 77).Price, binanceEvents.Single(trade => trade.Id == 77).Volume));
        Assert.Equal((120m, 5m), (binanceEvents.Single(trade => trade.Id == 79).Price, binanceEvents.Single(trade => trade.Id == 79).Volume));
        var bybitEvents = events.Where(trade => trade.Provenance.SourceIdentity == bybit.Id).ToArray();
        Assert.Equal(2, bybitEvents.Length);
        if (nativeEventKeys)
            Assert.Equal(new[] { "bybit-second-native-key", "same-native-key" }, bybitEvents.Select(trade => trade.Provenance.SourceEventId).Order(StringComparer.Ordinal));
        else Assert.All(events, trade => Assert.Null(trade.Provenance.SourceEventId));
        Assert.All(bybitEvents, trade => Assert.Equal((101m, 3m), (trade.Price, trade.Volume)));
        Assert.Equal(13m, events.Sum(trade => trade.Volume));

        bybit.TradeFailure = null;
        bybit.Trades.AddRange([firstLive, secondLive, firstLive]);
        data.Start(Start);
        await PumpUntil(data, () => !data.IsLoadingHistory);
        Assert.Null(data.HistoryError);
        Assert.Equal(4, data.Trades.Count);
        Assert.Equal(13m, Enumerable.Range(0, data.Trades.Count).Sum(data.Trades.GetVolume));
        Assert.Equal(2m, Enumerable.Range(0, data.Trades.Count).Select(data.Trades.Get)
            .Single(trade => trade.Provenance.SourceIdentity == binance.Id && trade.Id == 77).Volume);
    }

    [Theory]
    [InlineData(FeedStatus.Reconnecting)]
    [InlineData(FeedStatus.Error)]
    public async Task First_venue_failure_clears_real_data_and_SDK_book_while_tape_stays_connected_until_a_real_baseline(FeedStatus failedStatus)
    {
        using var fixture = new DataFixture();
        var clock = new ManualClock(Start.AddMinutes(1));
        using var first = new Constituent("first");
        using var second = new Constituent("second");
        first.Trades.Add(new Trade(1, Start.AddSeconds(10), 100, 2, TradeSide.Buy));
        using var feed = new AggregateFeed([first, second], timeProvider: clock);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        using var data = new InstrumentData(instrument, feed)
        {
            RecordingRoot = fixture.Root, RecordLive = false,
            DepthRecording = new() { MaxAge = TimeSpan.FromSeconds(60) },
        };
        data.Start(Start);
        await PumpUntil(data, () => !data.IsLoadingHistory);
        using var chart = new ChartModel(data, ChartPeriod.M1);
        var capture = new DepthCapture();
        var slot = chart.AddIndicator(capture, applyDefaultColors: false);
        Assert.Null(slot.Error);

        first.Sink!.OnDepth(first.Instrument, VenueBook(feed.Now, 10, full: true));
        data.Pump();
        Assert.Equal((100m, 101m), (data.BestBid, data.BestAsk));
        Assert.NotNull(data.DepthHistory.Current);
        Assert.NotNull(chart.MarketDepthInfo.Current);
        Assert.All(capture.Quotes.TakeLast(2), quote => Assert.True(quote.IsAvailable));
        Assert.Equal(new[] { MarketDataType.Bid, MarketDataType.Ask }, capture.Quotes.Select(quote => quote.DataType));

        second.Sink!.OnStatus(second.Instrument, FeedStatus.Reconnecting, "The non-book venue is reconnecting.");
        Assert.NotNull(data.DepthHistory.Current);
        data.Pump();
        Assert.NotNull(chart.MarketDepthInfo.Current);
        Assert.Equal((100m, 101m), (data.BestBid, data.BestAsk));
        second.Sink.OnStatus(second.Instrument, FeedStatus.Connected, null);
        data.Pump();

        clock.Advance(TimeSpan.FromSeconds(1));
        first.Sink.OnStatus(first.Instrument, failedStatus, "first venue is unavailable");
        Assert.Null(data.DepthHistory.Current);
        Assert.Null(chart.MarketDepthInfo.Current);
        data.Pump();

        Assert.Equal(FeedStatus.Connected, data.Status);
        Assert.Equal((0m, 0m), (data.BestBid, data.BestAsk));
        Assert.False(data.Depth.HasBid);
        Assert.False(data.Depth.HasAsk);
        Assert.Empty(chart.MarketDepthInfo.GetMarketDepthSnapshot());
        var gap = capture.Updates[^1];
        Assert.False(gap.IsFullRefresh);
        Assert.Equal(DepthGapReason.Disconnected, gap.GapBefore);
        Assert.True(gap.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.True(gap.Coverage.HasFlag(DepthCoverage.SequenceGap));
        Assert.True(gap.Coverage.HasFlag(DepthCoverage.Disconnected));
        Assert.Equal(first.Id, gap.ProviderId);
        Assert.Equal("first-venue", gap.Venue);
        Assert.Equal(feed.Now, gap.ReceivedAt);
        Assert.Null(gap.Sequence);
        Assert.Null(gap.FirstSequence);
        Assert.Null(gap.PreviousSequence);
        Assert.Empty(gap.Snapshot.Bids);
        Assert.Empty(gap.Snapshot.Asks);
        Assert.All(capture.Quotes.TakeLast(2), quote => Assert.False(quote.IsAvailable));
        Assert.True(data.DepthHistory.Status.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.True(data.DepthHistory.Status.Coverage.HasFlag(DepthCoverage.SequenceGap));

        second.Sink!.OnDepth(second.Instrument, VenueBook(feed.Now, 900, full: true));
        data.Pump();
        Assert.Null(data.DepthHistory.Current);
        Assert.Empty(chart.MarketDepthInfo.GetMarketDepthSnapshot());

        first.Sink.OnStatus(first.Instrument, FeedStatus.Connected, null);
        first.Sink.OnDepth(first.Instrument, VenueBook(feed.Now, 11, full: false));
        Assert.Null(data.DepthHistory.Current);
        data.Pump();
        Assert.Equal(FeedStatus.Connected, data.Status);
        Assert.False(capture.Updates[^1].IsFullRefresh);
        Assert.True(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.Missing));
        Assert.Null(chart.MarketDepthInfo.Current);
        Assert.Empty(chart.MarketDepthInfo.GetMarketDepthSnapshot());
        Assert.All(capture.Quotes.TakeLast(2), quote => Assert.False(quote.IsAvailable));

        // A real book baseline is authoritative even while a constituent's combined tape status is degraded.
        first.Sink.OnStatus(first.Instrument, failedStatus, "The first venue's tape channel is still degraded.");
        data.Pump();
        Assert.Null(data.DepthHistory.Current);
        Assert.Equal(FeedStatus.Connected, data.Status);

        clock.Advance(TimeSpan.FromSeconds(1));
        first.Sink.OnDepth(first.Instrument, VenueBook(feed.Now, 12, full: true));
        Assert.NotNull(data.DepthHistory.Current);
        data.Pump();
        Assert.NotNull(chart.MarketDepthInfo.Current);
        Assert.Equal((100m, 101m), (data.BestBid, data.BestAsk));
        Assert.All(capture.Quotes.TakeLast(2), quote => Assert.True(quote.IsAvailable));
        Assert.True(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.SnapshotOnly | DepthCoverage.PartialBook));
        Assert.False(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.Missing));
        Assert.False(capture.Updates[^1].Coverage.HasFlag(DepthCoverage.Disconnected));
        Assert.Equal(12, capture.Updates[^1].Sequence);
        Assert.True(capture.Updates[^1].IsFullRefresh);
        Assert.Null(slot.Error);
    }

    [Fact]
    public async Task Coarse_first_venue_failure_requests_one_existing_reconnect_per_gap_and_only_genuine_books_restore_depth()
    {
        using var fixture = new DataFixture();
        var clock = new ManualClock(Start.AddMinutes(1));
        using var first = new Constituent("first");
        using var second = new Constituent("second");
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ReconnectRequested = () =>
        {
            // A provider callback on another thread must complete before Reconnect returns.
            // Calling the provider while holding AggregateFeed's callback lock would deadlock here.
            var callback = Task.Run(() => first.Sink!.OnStatus(first.Instrument, FeedStatus.Reconnecting, "Same tape outage, reconnect acknowledged."));
            if (!callback.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("The reconnect request held the aggregate callback lock.");
            if (first.ReconnectRequests == 1) firstRequest.TrySetResult();
            else secondRequest.TrySetResult();
        };
        first.Trades.Add(new Trade(1, Start.AddSeconds(10), 100, 2, TradeSide.Buy));
        using var feed = new AggregateFeed([first, second], timeProvider: clock);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        using var data = new InstrumentData(instrument, feed)
        {
            RecordingRoot = fixture.Root, RecordLive = false,
            DepthRecording = new() { MaxAge = TimeSpan.FromSeconds(60) },
        };
        data.Start(Start);
        await PumpUntil(data, () => !data.IsLoadingHistory);
        using var chart = new ChartModel(data, ChartPeriod.M1);
        first.Sink!.OnStatus(first.Instrument, FeedStatus.Connecting, "Initial connection is not a recovery request.");
        Assert.Equal(0, first.ReconnectRequests);
        first.Sink.OnStatus(first.Instrument, FeedStatus.Connected, null);
        first.Sink.OnDepth(first.Instrument, VenueBook(feed.Now, 10, full: true));
        data.Pump();
        Assert.NotNull(chart.MarketDepthInfo.Current);

        first.Sink.OnStatus(first.Instrument, FeedStatus.Reconnecting, "Tape-only transport interruption.");
        await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.ReconnectRequests);
        Assert.Null(data.DepthHistory.Current);
        data.Pump();
        Assert.Equal(FeedStatus.Connected, data.Status);
        Assert.Empty(chart.MarketDepthInfo.GetMarketDepthSnapshot());

        first.Sink.OnStatus(first.Instrument, FeedStatus.Error, "Same outage, tape still unavailable.");
        first.Sink.OnDepth(first.Instrument, VenueBook(feed.Now, 11, full: false));
        data.Pump();
        Assert.Null(chart.MarketDepthInfo.Current);
        Assert.Equal(1, first.ReconnectRequests);

        clock.Advance(TimeSpan.FromSeconds(1));
        first.Sink.OnDepth(first.Instrument, VenueBook(feed.Now, 12, full: true));
        data.Pump();
        Assert.NotNull(chart.MarketDepthInfo.Current);
        first.Sink.OnStatus(first.Instrument, FeedStatus.Reconnecting, "The same tape outage remains degraded after the real book baseline.");
        data.Pump();
        Assert.NotNull(chart.MarketDepthInfo.Current);
        Assert.Equal(1, first.ReconnectRequests);

        first.Sink.OnStatus(first.Instrument, FeedStatus.Connected, null);
        first.Sink.OnStatus(first.Instrument, FeedStatus.Error, "A later independent venue interruption.");
        await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, first.ReconnectRequests);
        Assert.Null(data.DepthHistory.Current);
        clock.Advance(TimeSpan.FromSeconds(1));
        first.Sink.OnDepth(first.Instrument, VenueBook(feed.Now, 13, full: true));
        data.Pump();
        Assert.NotNull(chart.MarketDepthInfo.Current);
        first.Sink.OnStatus(first.Instrument, FeedStatus.Error, "Still the same second outage.");
        data.Pump();
        Assert.NotNull(chart.MarketDepthInfo.Current);
        Assert.Equal(2, first.ReconnectRequests);
    }

    private static DepthUpdate VenueBook(DateTime at, long sequence, bool full) => new()
    {
        Snapshot = new DepthSnapshot([new BookLevel(100, 7)], [new BookLevel(101, 8)], at),
        IsFullRefresh = full, ProviderId = "first", Venue = "first-venue", ReceivedAt = at,
        Sequence = sequence, Coverage = DepthCoverage.SnapshotOnly | DepthCoverage.PartialBook,
    };

    private static async Task<List<Trade>> Trades(AggregateFeed feed, Instrument instrument, CancellationToken ct = default)
    {
        var result = new List<Trade>();
        await feed.LoadTradesAsync(instrument, Start, Start.AddMinutes(2), null, batch =>
        {
            result.AddRange(batch);
            return true;
        }, null, ct);
        return result;
    }

    private static async Task PumpUntil(InstrumentData data, Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            data.Pump();
            if (ready()) return;
            Assert.True(DateTime.UtcNow < until, "The aggregate fixture did not publish its data before the deadline.");
            await Task.Delay(1);
        }
    }

    private sealed class DataFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tradinghub-aggregate-boundary-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private sealed class DepthCapture : Indicator
    {
        public List<DepthUpdate> Updates { get; } = new();
        public List<MarketDataArg> Quotes { get; } = new();
        protected override void OnCalculate(int bar, decimal value) { }
        protected override void OnDepthSnapshot(DepthUpdate update) => Updates.Add(update);
        protected override void OnBestBidAskChanged(MarketDataArg quote) => Quotes.Add(quote.Clone());
    }

    private sealed class Constituent : IMarketDataFeed, IKlineHistory
    {
        public Constituent(string id, decimal tickSize = 1)
        {
            Id = id;
            Instrument = new Instrument { FeedId = id, Symbol = "BTCUSDT", TickSize = tickSize, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        }

        public string Id { get; }
        public string DisplayName => Id;
        public bool SupportsHistory => true;
        public bool IsLive => true;
        public Instrument Instrument { get; }
        public List<Trade> Trades { get; } = new();
        public List<Kline> Klines { get; } = new();
        public Exception? TradeFailure { get; set; }
        public Exception? CandleFailure { get; set; }
        public Exception? ListingFailure { get; set; }
        public bool IncludesInstrument { get; set; } = true;
        public bool FailSubscriptionDisposal { get; set; }
        public bool FailFeedDisposal { get; set; }
        public IMarketDataSink? Sink { get; private set; }
        public bool IsSubscribed { get; private set; }
        public bool IsDisposed { get; private set; }
        public Func<CancellationToken, Task>? BeforeListing { get; set; }
        public Func<CancellationToken, Task>? BeforeTrades { get; set; }
        public Action? DuringSubscribe { get; set; }
        public Action? ReconnectRequested { get; set; }
        private int _reconnectRequests;
        public int ReconnectRequests => Volatile.Read(ref _reconnectRequests);
        public DateTime? FirstAvailable { get; set; } = Start;

        public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
        {
            if (BeforeListing != null) await BeforeListing(ct);
            ct.ThrowIfCancellationRequested();
            if (ListingFailure != null) throw ListingFailure;
            return IncludesInstrument ? new[] { Instrument } : Array.Empty<Instrument>();
        }

        public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            if (BeforeTrades != null) await BeforeTrades(ct);
            ct.ThrowIfCancellationRequested();
            if (TradeFailure != null) throw TradeFailure;
            onBatch(Trades.Where(t => t.Time >= fromUtc && t.Time <= toUtc).ToArray());
        }

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (CandleFailure != null) throw CandleFailure;
            long from = new DateTimeOffset(fromUtc).ToUnixTimeMilliseconds();
            long to = new DateTimeOffset(toUtc).ToUnixTimeMilliseconds();
            onBatch(Klines.Where(k => k.OpenTimeMs >= from && k.OpenTimeMs < to).ToArray());
            return Task.CompletedTask;
        }

        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult(FirstAvailable);

        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            Sink = sink;
            IsSubscribed = true;
            DuringSubscribe?.Invoke();
            sink.OnStatus(instrument, FeedStatus.Connected, null);
            Action dispose = () =>
            {
                IsSubscribed = false;
                if (FailSubscriptionDisposal) throw new IOException("fixture subscription disposal failure");
            };
            return ReconnectRequested == null ? new Lease(dispose) : new ReconnectingLease(dispose, () =>
            {
                Interlocked.Increment(ref _reconnectRequests);
                ReconnectRequested?.Invoke();
            });
        }

        public void Dispose()
        {
            IsDisposed = true;
            if (!FailFeedDisposal) return;
            FailFeedDisposal = false;
            throw new IOException("fixture feed disposal failure");
        }
    }

    private sealed class Observer : IMarketDataSink
    {
        public List<Trade> Trades { get; } = new();
        public List<DepthUpdate> Depth { get; } = new();
        public List<(FeedStatus Status, string? Message)> Statuses { get; } = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) => Trades.AddRange(trades);
        public void OnDepth(Instrument instrument, DepthUpdate update) => Depth.Add(update);
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) => Statuses.Add((status, message));
    }

    private sealed class ManualClock(DateTime origin) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        private long _elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => new(origin.AddTicks(_elapsed));
        public override long GetTimestamp() => _elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed.Ticks;
            foreach (var timer in _timers.ToArray()) timer.Fire(_elapsed);
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            private long _due = long.MaxValue, _period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._elapsed + dueTime.Ticks;
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
            public void Fire(long elapsed)
            {
                if (_disposed || _due > elapsed) return;
                _due = _period > 0 ? elapsed + _period : long.MaxValue;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ReconnectingLease(Action dispose, Action reconnect) : IDisposable, IReconnectable
    {
        private Action? _dispose = dispose;
        public void Reconnect() { if (Volatile.Read(ref _dispose) != null) reconnect(); }
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed class Lease(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
