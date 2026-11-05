using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class DepthStatisticsBoundaryTests
{
    private static readonly DateTime Start = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid RecordingId = new("5c2c4c2e-9ae7-4af0-a813-8ac50857ad54");

    [Theory]
    [InlineData(DepthAggregation.DurationAverage, DepthCoverage.PartialBook)]
    [InlineData(DepthAggregation.DurationAverage, DepthCoverage.Truncated)]
    [InlineData(DepthAggregation.DurationAverage, DepthCoverage.PartialBook | DepthCoverage.Truncated)]
    [InlineData(DepthAggregation.Last, DepthCoverage.PartialBook)]
    [InlineData(DepthAggregation.Last, DepthCoverage.Truncated)]
    [InlineData(DepthAggregation.Last, DepthCoverage.PartialBook | DepthCoverage.Truncated)]
    public void Preceding_only_sizes_retain_their_partial_coverage_when_selected(
        DepthAggregation aggregation, DepthCoverage partial)
    {
        var preceding = Sample(Start.AddMilliseconds(-500),
            12.345678901234567890123456789m, 0.1234567890123456789012345678m,
            coverage: DepthCoverage.ReceivedChanges | partial);
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(2), [], preceding, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 0, aggregation, TimeSpan.FromSeconds(10), allowPartial: true);

        Assert.True(result.IsAvailable);
        Assert.Equal((12.345678901234567890123456789m, 0.1234567890123456789012345678m), (result.Bid, result.Ask));
        Assert.Equal(DepthCoverage.ReceivedChanges | partial, result.Coverage);
        Assert.Equal(TimeSpan.FromSeconds(2), result.CoveredDuration);
        Assert.Equal(0, result.Samples);
        Assert.Contains("Partial provider book", result.Message);
        Assert.Equal(-5, preceding.Snapshot.BestBidTicks);
        Assert.Equal(-3, preceding.Snapshot.BestAskTicks);
    }

    [Theory]
    [InlineData(DepthAggregation.DurationAverage, DepthCoverage.PartialBook)]
    [InlineData(DepthAggregation.DurationAverage, DepthCoverage.Truncated)]
    [InlineData(DepthAggregation.DurationAverage, DepthCoverage.PartialBook | DepthCoverage.Truncated)]
    [InlineData(DepthAggregation.Last, DepthCoverage.PartialBook)]
    [InlineData(DepthAggregation.Last, DepthCoverage.Truncated)]
    [InlineData(DepthAggregation.Last, DepthCoverage.PartialBook | DepthCoverage.Truncated)]
    public void Strict_measurements_exclude_partial_carry_in_instead_of_reporting_observed_zero(
        DepthAggregation aggregation, DepthCoverage partial)
    {
        var preceding = Sample(Start.AddMilliseconds(-500), 10, 2, coverage: DepthCoverage.ReceivedChanges | partial);
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(2), [], preceding, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 0, aggregation, TimeSpan.FromSeconds(10), allowPartial: false);

        Assert.False(result.IsAvailable);
        Assert.True(result.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.Equal((0m, 0m), (result.Bid, result.Ask));
        Assert.Equal(TimeSpan.Zero, result.CoveredDuration);
        Assert.Equal(0, result.Samples);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 86400)]
    [InlineData(true, 86400)]
    public void One_large_observation_has_its_exact_duration_mean_without_a_size_tick_product(bool carryIn, int seconds)
    {
        var sample = Sample(carryIn ? Start.AddMilliseconds(-250) : Start,
            10_000_000_000_000_000_000_000.125m, 20_000_000_000_000_000_000_000.875m);
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(seconds), carryIn ? [] : [sample],
            carryIn ? sample : null, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 0, DepthAggregation.DurationAverage, TimeSpan.FromSeconds(seconds + 1));

        Assert.True(result.IsAvailable);
        Assert.Equal((10_000_000_000_000_000_000_000.125m, 20_000_000_000_000_000_000_000.875m), (result.Bid, result.Ask));
        Assert.Equal(TimeSpan.FromSeconds(seconds), result.CoveredDuration);
        Assert.Equal(carryIn ? 0 : 1, result.Samples);
        Assert.Equal(DepthCoverage.ReceivedChanges, result.Coverage);
        Assert.Equal(10_000_000_000_000_000_000_000.125m, sample.Snapshot.BestBidSize);
        Assert.Equal(20_000_000_000_000_000_000_000.875m, sample.Snapshot.BestAskSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_large_observations_keep_the_worked_one_to_two_duration_weights(bool reverse)
    {
        var first = Sample(Start,
            reverse ? 40_000_000_000_000_000_000_000m : 10_000_000_000_000_000_000_000m,
            reverse ? 50_000_000_000_000_000_000_000m : 20_000_000_000_000_000_000_000m);
        var second = Sample(Start.AddSeconds(1),
            reverse ? 10_000_000_000_000_000_000_000m : 40_000_000_000_000_000_000_000m,
            reverse ? 20_000_000_000_000_000_000_000m : 50_000_000_000_000_000_000_000m, 2);
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(3), [first, second], null, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 0, DepthAggregation.DurationAverage, TimeSpan.FromSeconds(10));

        Assert.True(result.IsAvailable);
        Assert.Equal(reverse ? 20_000_000_000_000_000_000_000m : 30_000_000_000_000_000_000_000m, result.Bid);
        Assert.Equal(reverse ? 30_000_000_000_000_000_000_000m : 40_000_000_000_000_000_000_000m, result.Ask);
        Assert.Equal(TimeSpan.FromSeconds(3), result.CoveredDuration);
        Assert.Equal(2, result.Samples);
        Assert.Equal(DepthCoverage.ReceivedChanges, result.Coverage);
    }

    [Fact]
    public void Duration_weighting_does_not_underflow_representable_small_decimal_contributions()
    {
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(4),
            [Sample(Start, 0, 0), Sample(Start.AddSeconds(1), 0.0000000000000000000000000001m, 0.0000000000000000000000000003m, 2)],
            null, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 0, DepthAggregation.DurationAverage, TimeSpan.FromSeconds(10));

        Assert.True(result.IsAvailable);
        Assert.Equal((0.0000000000000000000000000001m, 0.0000000000000000000000000002m), (result.Bid, result.Ask));
        Assert.Equal(TimeSpan.FromSeconds(4), result.CoveredDuration);
        Assert.Equal(2, result.Samples);
    }

    [Theory]
    [InlineData(DepthAggregation.SampleAverage)]
    [InlineData(DepthAggregation.Maximum)]
    [InlineData(DepthAggregation.Last)]
    public void Non_duration_statistics_accept_large_sizes_without_duration_weighting(DepthAggregation aggregation)
    {
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(4),
            [Sample(Start, 10_000_000_000_000_000_000_000m, 2_000_000_000_000_000_000_000m),
             Sample(Start.AddSeconds(1), 30_000_000_000_000_000_000_000m, 6_000_000_000_000_000_000_000m, 2)],
            null, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 0, aggregation, TimeSpan.FromSeconds(10));

        Assert.True(result.IsAvailable);
        Assert.Equal(aggregation == DepthAggregation.SampleAverage ? 20_000_000_000_000_000_000_000m : 30_000_000_000_000_000_000_000m, result.Bid);
        Assert.Equal(aggregation == DepthAggregation.SampleAverage ? 4_000_000_000_000_000_000_000m : 6_000_000_000_000_000_000_000m, result.Ask);
        Assert.Equal(TimeSpan.FromSeconds(4), result.CoveredDuration);
        Assert.Equal(2, result.Samples);
        Assert.Equal(DepthCoverage.ReceivedChanges, result.Coverage);
    }

    [Theory]
    [InlineData(DepthAggregation.DurationAverage)]
    [InlineData(DepthAggregation.Last)]
    public void An_already_stale_partial_carry_in_does_not_label_later_complete_sizes_as_partial(DepthAggregation aggregation)
    {
        var preceding = Sample(Start.AddSeconds(-3), 10, 2,
            coverage: DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook | DepthCoverage.Truncated);
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(2), [Sample(Start.AddSeconds(1), 30, 6, 2)],
            preceding, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 0, aggregation, TimeSpan.FromSeconds(2), allowPartial: true);

        Assert.True(result.IsAvailable);
        Assert.Equal((30m, 6m), (result.Bid, result.Ask));
        Assert.Equal(DepthCoverage.ReceivedChanges | DepthCoverage.Missing, result.Coverage);
        Assert.Equal(TimeSpan.FromSeconds(1), result.CoveredDuration);
        Assert.Equal(1, result.Samples);
    }

    [Theory]
    [InlineData(DepthAggregation.DurationAverage)]
    [InlineData(DepthAggregation.Last)]
    public void A_carry_in_replaced_at_the_interval_start_contributes_neither_sizes_nor_partial_flags(DepthAggregation aggregation)
    {
        var preceding = Sample(Start.AddMilliseconds(-500), 10, 2,
            coverage: DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook | DepthCoverage.Truncated);
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(2), [Sample(Start, 30, 6, 2)],
            preceding, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 0, aggregation, TimeSpan.FromSeconds(10), allowPartial: true);

        Assert.True(result.IsAvailable);
        Assert.Equal((30m, 6m), (result.Bid, result.Ask));
        Assert.Equal(DepthCoverage.ReceivedChanges, result.Coverage);
        Assert.Equal(TimeSpan.FromSeconds(2), result.CoveredDuration);
        Assert.Equal(1, result.Samples);
    }

    [Theory]
    [InlineData(DepthAggregation.SampleAverage)]
    [InlineData(DepthAggregation.Maximum)]
    [InlineData(DepthAggregation.Last)]
    public void Non_duration_modes_do_not_sum_an_unused_preceding_book(DepthAggregation aggregation)
    {
        var at = Start.AddMilliseconds(-500);
        var preceding = new DepthSample(RecordingId, 1, new DepthUpdate
        {
            Snapshot = new DepthSnapshot([new BookLevel(-5, decimal.MaxValue), new BookLevel(-6, 1)], [new BookLevel(-3, 2)], at),
            ReceivedAt = at, ProviderId = "fake", Venue = "venue-a", IsFullRefresh = true,
            Coverage = DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook | DepthCoverage.Truncated,
        });
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(2), [Sample(Start.AddSeconds(1), 30, 6, 2)],
            preceding, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 1, aggregation, TimeSpan.FromSeconds(10), allowPartial: true);

        Assert.True(result.IsAvailable);
        Assert.Equal((30m, 6m), (result.Bid, result.Ask));
        Assert.Equal(DepthCoverage.ReceivedChanges, result.Coverage);
        Assert.Equal(TimeSpan.FromSeconds(2), result.CoveredDuration);
        Assert.Equal(1, result.Samples);
        Assert.Equal(decimal.MaxValue, preceding.Snapshot.Bids[0].Size);
        Assert.Equal(1m, preceding.Snapshot.Bids[1].Size);
    }

    [Fact]
    public void Duration_mean_excludes_explicit_gaps_and_stale_tails_from_its_denominator()
    {
        var preceding = Sample(Start.AddMilliseconds(-500), 10, 2);
        var gapAt = Start.AddSeconds(1);
        var gap = new DepthSample(RecordingId, 2, new DepthUpdate
        {
            Snapshot = new DepthSnapshot([], [], gapAt), ReceivedAt = gapAt,
            ProviderId = "fake", Venue = "venue-a",
            Coverage = DepthCoverage.Disconnected | DepthCoverage.Missing, GapBefore = DepthGapReason.Disconnected,
        });
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(6), [gap, Sample(Start.AddSeconds(3), 40, 8, 3)],
            preceding, DepthCoverage.ReceivedChanges, null);

        var result = DepthStatistics.Measure(query, 0, DepthAggregation.DurationAverage, TimeSpan.FromSeconds(2));
        var last = DepthStatistics.Measure(query, 0, DepthAggregation.Last, TimeSpan.FromSeconds(2));

        Assert.True(result.IsAvailable);
        Assert.Equal((30m, 6m), (result.Bid, result.Ask));
        Assert.Equal(TimeSpan.FromSeconds(3), result.CoveredDuration);
        Assert.Equal(1, result.Samples);
        Assert.Equal(DepthCoverage.ReceivedChanges | DepthCoverage.Disconnected | DepthCoverage.Missing, result.Coverage);
        Assert.Contains("Partial interval", result.Message);
        Assert.False(last.IsAvailable);
        Assert.Equal(TimeSpan.FromSeconds(3), last.CoveredDuration);
        Assert.True(last.Coverage.HasFlag(DepthCoverage.Missing));
    }

    [Fact]
    public void A_duration_mean_rejects_a_carry_in_from_another_venue_while_last_selects_the_fresh_venue()
    {
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(2), [Sample(Start.AddSeconds(1), 30, 6, 2, venue: "venue-b")],
            Sample(Start.AddMilliseconds(-500), 10, 2), DepthCoverage.ReceivedChanges, null);

        var duration = DepthStatistics.Measure(query, 0, DepthAggregation.DurationAverage, TimeSpan.FromSeconds(10));
        var last = DepthStatistics.Measure(query, 0, DepthAggregation.Last, TimeSpan.FromSeconds(10));

        Assert.False(duration.IsAvailable);
        Assert.True(duration.Coverage.HasFlag(DepthCoverage.SequenceGap));
        Assert.Contains("consolidated", duration.Message);
        Assert.True(last.IsAvailable);
        Assert.Equal((30m, 6m), (last.Bid, last.Ask));
        Assert.Equal(TimeSpan.FromSeconds(2), last.CoveredDuration);
    }

    [Theory]
    [InlineData(DepthAggregation.DurationAverage)]
    [InlineData(DepthAggregation.SampleAverage)]
    [InlineData(DepthAggregation.Maximum)]
    [InlineData(DepthAggregation.Last)]
    public void An_observed_true_zero_remains_distinct_from_a_missing_book(DepthAggregation aggregation)
    {
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(1), [Sample(Start, 0, 0)], null, DepthCoverage.ReceivedChanges, null);

        var zero = DepthStatistics.Measure(query, 0, aggregation, TimeSpan.FromSeconds(10));
        var missing = DepthStatistics.Measure(query with { Samples = [] }, 0, aggregation, TimeSpan.FromSeconds(10));

        Assert.True(zero.IsAvailable);
        Assert.Equal((0m, 0m), (zero.Bid, zero.Ask));
        Assert.Equal(TimeSpan.FromSeconds(1), zero.CoveredDuration);
        Assert.Equal(1, zero.Samples);
        Assert.Equal(DepthCoverage.ReceivedChanges, zero.Coverage);
        Assert.False(missing.IsAvailable);
        Assert.Equal((0m, 0m), (missing.Bid, missing.Ask));
        Assert.Equal(TimeSpan.Zero, missing.CoveredDuration);
        Assert.Equal(0, missing.Samples);
        Assert.True(missing.Coverage.HasFlag(DepthCoverage.Missing));
    }

    [Fact]
    public void Sample_means_and_imbalance_do_not_overflow_representable_large_sizes()
    {
        const decimal size = 30_000_000_000_000_000_000_000_000_000m;
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(3),
            [Sample(Start, size, size), Sample(Start.AddSeconds(1), size, size, 2), Sample(Start.AddSeconds(2), size, size, 3)],
            null, DepthCoverage.ReceivedChanges, null);

        var mean = DepthStatistics.Measure(query, 0, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10));
        var maximum = DepthStatistics.Measure(query, 0, DepthAggregation.Maximum, TimeSpan.FromSeconds(10));
        var largeImbalance = new DepthMeasurement(true, size * 2, size, 1, TimeSpan.FromSeconds(1), DepthCoverage.ReceivedChanges, null);

        Assert.True(mean.IsAvailable);
        Assert.Equal((size, size), (mean.Bid, mean.Ask));
        Assert.Equal((size, size), (maximum.Bid, maximum.Ask));
        Assert.Equal(0m, mean.Imbalance);
        Assert.Equal((1m / 3) * 100, largeImbalance.Imbalance);
        Assert.Equal(3, mean.Samples);
    }

    [Fact]
    public void Sample_means_keep_exact_small_contributions_when_the_sum_fits()
    {
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(2),
            [Sample(Start, 0.0000000000000000000000000001m, 0.0000000000000000000000000003m), Sample(Start.AddSeconds(1), 0, 0, 2)],
            null, DepthCoverage.ReceivedChanges, null);

        var mean = DepthStatistics.Measure(query, 0, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10));

        Assert.True(mean.IsAvailable);
        Assert.Equal(0m, mean.Bid);
        Assert.Equal(0.0000000000000000000000000002m, mean.Ask);
    }

    private static DepthSample Sample(DateTime at, decimal bid, decimal ask, long ordinal = 1,
        DepthCoverage coverage = DepthCoverage.ReceivedChanges, string venue = "venue-a") => new(RecordingId, ordinal, new DepthUpdate
        {
            Snapshot = new DepthSnapshot([new BookLevel(-5, bid)], [new BookLevel(-3, ask)], at.AddMilliseconds(-7)),
            ReceivedAt = at, ProviderId = "fake", Venue = venue, Sequence = ordinal,
            IsFullRefresh = true, Coverage = coverage,
        });
}
