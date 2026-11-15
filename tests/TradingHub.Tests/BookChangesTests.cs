using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Book changes over recorded snapshots: added / pulled / filled attribution, sampling honesty, large-level heuristics, bands.</summary>
public sealed class BookChangesTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Recording = Guid.NewGuid();
    private static long _ordinal;

    private static DepthSample S(double seconds, (long Price, decimal Size)[] bids, (long Price, decimal Size)[] asks,
        Guid? recording = null, string provider = "fake", DepthGapReason gap = DepthGapReason.None, DepthCoverage coverage = DepthCoverage.ReceivedChanges) =>
        new(recording ?? Recording, Interlocked.Increment(ref _ordinal), new DepthUpdate
        {
            Snapshot = new DepthSnapshot(bids.Select(b => new BookLevel(b.Price, b.Size)).ToArray(), asks.Select(a => new BookLevel(a.Price, a.Size)).ToArray(), T0.AddSeconds(seconds)),
            ProviderId = provider, Venue = "venue", ReceivedAt = T0.AddSeconds(seconds), IsFullRefresh = gap != DepthGapReason.None,
            Coverage = coverage, GapBefore = gap,
        });

    private static BookTrade Sell(double seconds, long price, decimal volume) => new(T0.AddSeconds(seconds), price, volume, TradeSide.Sell);
    private static BookTrade Buy(double seconds, long price, decimal volume) => new(T0.AddSeconds(seconds), price, volume, TradeSide.Buy);

    [Fact]
    public void Removed_size_is_split_into_filled_by_matching_trades_and_pulled_and_unseen_levels_are_not_added()
    {
        var a = S(0, [(100, 5), (99, 3)], [(101, 4)]);
        var b = S(3, [(100, 2), (99, 6)], [(101, 4), (102, 10)]);
        var result = BookChanges.Compute([b], a, new TimedBookTrades([Sell(1, 100, 1)], TimeSpan.Zero));
        Assert.Equal(1, result.Comparisons);
        Assert.Equal(1m, result.Levels[100].BidFilled);
        Assert.Equal(2m, result.Levels[100].BidPulled);
        Assert.Equal(3m, result.Levels[99].BidAdded);
        // 102 was outside the previous snapshot's visible asks: appearing there is not evidence of an addition
        Assert.False(result.Levels.ContainsKey(102));
        Assert.Equal(BookTradeAttributionKind.Timed, result.Attribution);
        Assert.Equal(TimeSpan.FromSeconds(3), result.LongestComparedInterval);

        var grouped = BookChanges.Compute([b], a, null, new BookChangeOptions { GroupTicks = 10 });
        Assert.Equal(3m, grouped.Levels[90].BidAdded);
        // without trade data nothing is called filled, and the result says so
        Assert.Equal(3m, grouped.Levels[100].BidPulled);
        Assert.Equal(0m, grouped.Levels[100].BidFilled);
        Assert.Equal(BookTradeAttributionKind.None, grouped.Attribution);
        Assert.Contains("No trade data", grouped.Message);
    }

    [Fact]
    public void Each_trade_explains_removed_size_once_and_only_on_the_side_it_consumed_within_its_interval()
    {
        var samples = new[]
        {
            S(0, [(100, 5)], [(101, 5)]),
            S(2, [(100, 3)], [(101, 3)]),
            S(4, [(100, 1)], [(101, 1)]),
        };
        // a 2-lot sell between the first two snapshots; a buy at the bid price never consumes bids
        var trades = new TimedBookTrades([Sell(1, 100, 2), Buy(1, 100, 5), Buy(9, 101, 5)], TimeSpan.Zero);
        var result = BookChanges.Compute(samples, null, trades);
        Assert.Equal(2, result.Comparisons);
        Assert.Equal(2m, result.Levels[100].BidFilled);
        Assert.Equal(2m, result.Levels[100].BidPulled);
        // the buy at 101 happened after the last snapshot: outside every interval
        Assert.Equal(0m, result.Levels[101].AskFilled);
        Assert.Equal(4m, result.Levels[101].AskPulled);
        Assert.Equal(-4m, result.Levels[100].Net);

        // clock skew widens the matching interval (trade and book streams use different clocks)
        var skewed = BookChanges.Compute(samples, null, new TimedBookTrades([Buy(4.5, 101, 5)], TimeSpan.FromSeconds(1)));
        Assert.Equal(2m, skewed.Levels[101].AskFilled);
        Assert.Equal(2m, skewed.Levels[101].AskPulled);
    }

    [Fact]
    public void Pooled_bar_volume_is_consumed_across_transitions_without_double_counting()
    {
        var pooled = new PooledBookTrades();
        pooled.Add(100, sellAggressor: 3, buyAggressor: 0);
        var samples = new[] { S(0, [(100, 5)], [(101, 1)]), S(1, [(100, 3)], [(101, 1)]), S(2, [(100, 0.5m)], [(101, 1)]) };
        var result = BookChanges.Compute(samples, null, pooled);
        Assert.Equal(3m, result.Levels[100].BidFilled);
        Assert.Equal(1.5m, result.Levels[100].BidPulled);
        Assert.Equal(BookTradeAttributionKind.Pooled, result.Attribution);
    }

    [Fact]
    public void Gaps_stale_intervals_source_changes_and_unusable_books_are_skipped_and_counted_not_compared()
    {
        var other = Guid.NewGuid();
        var samples = new[]
        {
            S(0, [(100, 5)], [(101, 5)]),
            S(30, [(100, 1)], [(101, 5)]),                                     // stale: 30 s > 10 s max age
            S(31, [(100, 9)], [(101, 5)], gap: DepthGapReason.Sequence),       // provider gap before this book
            S(32, [(100, 4)], [(101, 5)], recording: other),                   // recording restarted
            S(33, [(100, 7)], [(101, 5)], recording: other, provider: "other"), // provider changed
            S(34, [(100, 2)], [], recording: other, provider: "other", coverage: DepthCoverage.PartialBook | DepthCoverage.Missing), // one-sided
            S(35, [(100, 3)], [(101, 5)], recording: other, provider: "other"),
            S(36, [(100, 6)], [(101, 5)], recording: other, provider: "other"),
        };
        var result = BookChanges.Compute(samples, null, null);
        Assert.Equal(1, result.Comparisons);
        Assert.Equal(6, result.SkippedTransitions);
        Assert.Equal(3m, result.Levels[100].BidAdded);
        Assert.Equal(0m, result.Levels[100].BidPulled);
        Assert.Contains("skipped", result.Message);

        var none = BookChanges.Compute([samples[0], samples[1]], null, null);
        Assert.False(none.IsAvailable);
        Assert.True(none.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.False(BookChanges.Compute([], null, null).IsAvailable);

        // partial provider books are compared only when allowed
        var partialA = S(40, [(100, 5)], [(101, 5)], coverage: DepthCoverage.PartialBook);
        var partialB = S(41, [(100, 2)], [(101, 5)], coverage: DepthCoverage.PartialBook);
        Assert.Equal(1, BookChanges.Compute([partialB], partialA, null).Comparisons);
        Assert.Equal(0, BookChanges.Compute([partialB], partialA, null, new BookChangeOptions { AllowPartialBook = false }).Comparisons);
    }

    [Fact]
    public void Large_levels_classify_quick_pulls_chasing_trading_and_never_judge_unobserved_fates()
    {
        var samples = new[]
        {
            S(0, [(100, 50), (99, 1)], [(101, 1), (105, 60)]),
            S(3, [(100, 50), (99, 1)], [(101, 1), (105, 60)]),
            // the large bid reappears one tick higher as the bid moves up (chasing); the large ask vanishes untraded (pull)
            S(6, [(101, 52), (100, 1), (99, 1)], [(102, 1), (105, 1), (107, 1)]),
        };
        var tracks = BookChanges.TrackLarge(samples, new LargeOrderOptions { MinSize = 20 }, new TimedBookTrades([], TimeSpan.Zero));
        var chase = tracks.Single(t => t.Side == BookSide.Bid && t.PriceTicks == 100);
        Assert.Equal(LargeOrderOutcome.Moved, chase.Outcome);
        Assert.Equal(101, chase.MovedToTicks);
        var pull = tracks.Single(t => t.Side == BookSide.Ask && t.PriceTicks == 105);
        Assert.Equal(LargeOrderOutcome.Pulled, pull.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(6), pull.Lifetime);
        Assert.True(pull.IsQuickPull(TimeSpan.FromSeconds(60)));
        Assert.False(pull.IsQuickPull(TimeSpan.FromSeconds(5)));
        Assert.Equal(LargeOrderOutcome.Open, tracks.Single(t => t.PriceTicks == 101).Outcome);

        // traded at its price: not a pull
        var traded = BookChanges.TrackLarge(samples, new LargeOrderOptions { MinSize = 20 }, new TimedBookTrades([Buy(5, 105, 40)], TimeSpan.Zero));
        Assert.Equal(LargeOrderOutcome.Traded, traded.Single(t => t.Side == BookSide.Ask && t.PriceTicks == 105).Outcome);
        // no trade source: removed, never called a pull
        var unknown = BookChanges.TrackLarge(samples, new LargeOrderOptions { MinSize = 20 }, null);
        Assert.Equal(LargeOrderOutcome.Removed, unknown.Single(t => t.Side == BookSide.Ask && t.PriceTicks == 105).Outcome);
        Assert.DoesNotContain(unknown, t => t.Outcome is LargeOrderOutcome.Pulled or LargeOrderOutcome.Moved);
    }

    [Fact]
    public void Large_levels_across_gaps_or_out_of_view_are_unobserved_and_unmoved_best_prices_never_imply_chasing()
    {
        var empty = new TimedBookTrades([], TimeSpan.Zero);
        var gap = new[]
        {
            S(0, [(100, 50)], [(101, 1)]),
            S(1, [(99, 1)], [(101, 1)], gap: DepthGapReason.Disconnected),
        };
        var acrossGap = BookChanges.TrackLarge(gap, new LargeOrderOptions { MinSize = 20 }, empty);
        Assert.Equal(LargeOrderOutcome.Unobserved, Assert.Single(acrossGap).Outcome);

        var stale = new[] { S(0, [(100, 50)], [(101, 1)]), S(60, [(99, 1)], [(101, 1)]) };
        Assert.Equal(LargeOrderOutcome.Unobserved, Assert.Single(BookChanges.TrackLarge(stale, new LargeOrderOptions { MinSize = 20 }, empty)).Outcome);

        // the price ran away: the level left the received book depth, so its fate is unknown
        var away = new[] { S(0, [(100, 1), (90, 50)], [(101, 1)]), S(1, [(110, 1), (105, 1)], [(111, 1)]) };
        Assert.Equal(LargeOrderOutcome.Unobserved, Assert.Single(BookChanges.TrackLarge(away, new LargeOrderOptions { MinSize = 20 }, empty)).Outcome);

        // a similar level nearby while the best bid did not move is a pull, not chasing
        var still = new[] { S(0, [(100, 1), (98, 50), (97, 1)], [(101, 1)]), S(1, [(100, 1), (99, 50), (97, 1)], [(101, 1)]) };
        var tracks = BookChanges.TrackLarge(still, new LargeOrderOptions { MinSize = 20 }, empty);
        Assert.Equal(LargeOrderOutcome.Pulled, tracks.Single(t => t.PriceTicks == 98).Outcome);
        Assert.Empty(BookChanges.TrackLarge(still, new LargeOrderOptions { MinSize = 0 }, empty));
    }

    [Fact]
    public void Percent_bands_are_mid_relative_and_feed_depth_statistics()
    {
        // mid 1000 ticks: 1 % = 10 ticks each side
        var book = new DepthSnapshot([new(999, 1), new(990, 2), new(989, 4)], [new(1001, 3), new(1010, 5), new(1011, 7)], T0);
        Assert.Equal((3m, 8m, true), DepthBands.WithinPercent(book, 1));
        Assert.False(DepthBands.WithinPercent(new DepthSnapshot([new(999, 1)], [], T0), 1).Available);

        var sample = S(0, [(999, 1), (990, 2), (989, 4)], [(1001, 3), (1010, 5), (1011, 7)]);
        var query = new DepthHistoryQuery(T0, T0.AddSeconds(5), [sample], null, DepthCoverage.None, null);
        var measured = DepthStatistics.Measure(query, 0, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10), withinPercent: 1);
        Assert.Equal(3m, measured.Bid);
        Assert.Equal(8m, measured.Ask);
        // without a percentage the existing tick distance applies unchanged
        var ticks = DepthStatistics.Measure(query, 0, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10));
        Assert.Equal(1m, ticks.Bid);
        Assert.Equal(3m, ticks.Ask);
        Assert.True(OrderbookDelta.BandExceedsBook(query, 25));
        Assert.False(OrderbookDelta.BandExceedsBook(query, 0.5m));
    }

    [Fact]
    public void Book_indicators_compute_from_recorded_depth_and_label_missing_recording()
    {
        var instrument = new Instrument { Symbol = "BOOK", FeedId = "fake", Exchange = "venue", TickSize = 1m, VolumeStep = 1m };
        using var data = new InstrumentData(instrument, new SimulatedFeed());
        // observed prints (tick rows): aggressive sells at 100 in both bars
        data.Trades.Add(new Trade(1, T0, 100, 1, TradeSide.Sell));
        data.Trades.Add(new Trade(2, T0.AddSeconds(71), 100, 40, TradeSide.Sell));
        data.Trades.Add(new Trade(3, T0.AddMinutes(2), 100, 1, TradeSide.Buy));
        using var model = new ChartModel(data, ChartPeriod.M1);
        var changes = new OrderBookChanges { Window = OrderBookChanges.ChangeWindow.Day1 };
        var large = new LargeBookOrders { MinSize = 20 };
        var percent = new OrderbookDelta { Range = OrderbookDelta.DepthRange.Percent1 };
        Assert.Null(model.AddIndicator(changes, applyDefaultColors: false).Error);
        Assert.Null(model.AddIndicator(large, applyDefaultColors: false).Error);
        Assert.Null(model.AddIndicator(percent, applyDefaultColors: false).Error);
        Assert.False(changes.ComputeWindow().IsAvailable);
        Assert.Contains("Recording must be explicitly enabled", changes.Status);

        var records = new[]
        {
            S(10, [(100, 50), (99, 3)], [(101, 4), (102, 2)]),
            S(12, [(100, 46), (99, 5)], [(101, 4), (102, 2)]),
            S(70, [(100, 46), (99, 1)], [(101, 4), (102, 2)]),
            S(72, [(99, 1), (98, 1)], [(100, 3), (101, 4)]),
        };
        data.DepthHistory.Apply(new(T0, T0.AddMinutes(3), records, null));
        model.Update();
        var window = changes.ComputeWindow();
        Assert.True(window.IsAvailable);
        // bar 0: 10 s → 12 s; bar 1: 12 s → 70 s is stale (skipped), 70 s → 72 s compared
        Assert.Equal(2, window.Comparisons);
        Assert.Equal(1, window.SkippedTransitions);
        Assert.Equal(2m, window.Levels[99].BidAdded);
        Assert.Equal(41m, window.Levels[100].BidFilled);
        Assert.Equal(9m, window.Levels[100].BidPulled);
        Assert.Contains("filled/pulled split is a heuristic", changes.Status);
        Assert.Contains("1 skipped", changes.Status);

        large.Update();
        var bid100 = large.Tracks.Where(t => t.PriceTicks == 100 && t.Side == BookSide.Bid).ToList();
        Assert.Equal(2, bid100.Count);
        Assert.Equal(LargeOrderOutcome.Unobserved, bid100[0].Outcome); // the stale interval hides its fate
        Assert.Equal(LargeOrderOutcome.Traded, bid100[1].Outcome);     // 40 sold into it within its lifetime
        Assert.Contains("heuristics, not intent", large.Status);
    }
}
