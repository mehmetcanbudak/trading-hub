using SkiaSharp;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class DepthHeatmapBoundaryTests
{
    private static readonly DateTime Start = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Adjacent_updates_share_only_the_first_pixels_peak_and_preserve_the_longer_remainder()
    {
        var frame = new DepthHeatmapFrame(
        [
            new(Start.AddMilliseconds(100), Start.AddMilliseconds(300), -1, BookSide.Bid, 100m, DepthCoverage.SnapshotOnly),
            new(Start.AddMilliseconds(300), Start.AddMilliseconds(4750), -1, BookSide.Bid, 50m,
                DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook),
        ], [], false);
        var projection = new DepthHeatmapProjection();

        projection.Project(frame, new SKRect(0, 0, 5, 1), X, Y, 1);

        Assert.False(projection.IsTruncated);
        Assert.Equal(new[] { 100m, 50m, 50m, 50m, 50m },
            Enumerable.Range(0, 5).Select(x => At(projection, x + 0.5f, 0.5f, BookSide.Bid).RestingSize));
        Assert.Equal(DepthCoverage.SnapshotOnly | DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook,
            At(projection, 0.5f, 0.5f, BookSide.Bid).Coverage);
        Assert.Equal(DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook,
            At(projection, 4.5f, 0.5f, BookSide.Bid).Coverage);
    }

    [Fact]
    public void Clipping_preserves_exact_sizes_sides_and_separate_negative_price_rows()
    {
        const decimal bidSize = 0.1234567890123456789012345678m;
        const decimal askSize = 9.876543210987654321098765432m;
        var frame = new DepthHeatmapFrame(
        [
            new(Start.AddSeconds(-2), Start.AddMilliseconds(2200), -1, BookSide.Bid, bidSize, DepthCoverage.SnapshotOnly),
            new(Start.AddMilliseconds(1200), Start.AddSeconds(7), -1, BookSide.Ask, askSize, DepthCoverage.ReceivedChanges),
            new(Start.AddSeconds(-2), Start.AddSeconds(7), -3, BookSide.Bid, 31m, DepthCoverage.PartialBook),
            new(Start.AddSeconds(7), Start.AddSeconds(8), -1, BookSide.Bid, 999m, DepthCoverage.SnapshotOnly),
            new(Start, Start.AddSeconds(4), -4, BookSide.Bid, 999m, DepthCoverage.SnapshotOnly),
        ], [], false);
        var viewport = new SKRect(0.25f, 0.25f, 4.75f, 2.75f);
        var projection = new DepthHeatmapProjection();

        projection.Project(frame, viewport, X, Y, 1);

        Assert.False(projection.IsTruncated);
        Assert.Equal(bidSize, At(projection, 1.5f, 0.5f, BookSide.Bid).RestingSize);
        Assert.Equal(askSize, At(projection, 1.5f, 0.5f, BookSide.Ask).RestingSize);
        Assert.Equal(31m, At(projection, 4.5f, 2.5f, BookSide.Bid).RestingSize);
        Assert.Equal(DepthCoverage.PartialBook, At(projection, 4.5f, 2.5f, BookSide.Bid).Coverage);
        Assert.DoesNotContain(projection.Spans, span => span.RestingSize == 999m);
        Assert.All(projection.Spans, span =>
        {
            Assert.True(span.Rect.Left >= 0.25f && span.Rect.Right <= 4.75f && span.Rect.Right > span.Rect.Left);
            Assert.True(span.Rect.Top >= 0.25f && span.Rect.Bottom <= 2.75f && span.Rect.Bottom > span.Rect.Top);
        });
    }

    [Fact]
    public void Subpixel_negative_rows_take_peaks_per_side_independent_of_input_order()
    {
        DepthHeatmapCell[] cells =
        [
            new(Start.AddMilliseconds(100), Start.AddMilliseconds(300), -1, BookSide.Bid, 10m, DepthCoverage.SnapshotOnly),
            new(Start.AddMilliseconds(300), Start.AddMilliseconds(3750), -2, BookSide.Bid, 7m, DepthCoverage.ReceivedChanges),
            new(Start, Start.AddSeconds(4), -2, BookSide.Ask, 2m, DepthCoverage.PartialBook),
        ];
        var projection = new DepthHeatmapProjection();
        foreach (var ordered in new[] { cells, cells.Reverse().ToArray() })
        {
            projection.Project(new(ordered, [], false), new SKRect(0, 0, 4, 1), X, ticks => Y(ticks) / 4, 1);

            Assert.False(projection.IsTruncated);
            Assert.Equal(new[] { 10m, 7m, 7m, 7m },
                Enumerable.Range(0, 4).Select(x => At(projection, x + 0.5f, 0.5f, BookSide.Bid).RestingSize));
            Assert.All(Enumerable.Range(0, 4), x => Assert.Equal(2m, At(projection, x + 0.5f, 0.5f, BookSide.Ask).RestingSize));
            Assert.Equal(DepthCoverage.SnapshotOnly | DepthCoverage.ReceivedChanges,
                At(projection, 0.5f, 0.5f, BookSide.Bid).Coverage);
        }
    }

    [Fact]
    public void Historical_projection_keeps_sparse_stale_and_sequence_gaps_without_extending_resting_size_through_them()
    {
        var query = new DepthHistoryQuery(Start, Start.AddSeconds(5),
        [
            Sample(1, 0, 100m, DepthCoverage.ReceivedChanges),
            Sample(2, 200, 50m, DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook, DepthGapReason.Sequence),
        ], null, DepthCoverage.ReceivedChanges, null);
        var frame = LiquidityHeatmapRenderer.BuildFrame(query, 1, TimeSpan.FromSeconds(2));
        var projection = new DepthHeatmapProjection();

        projection.Project(frame, new SKRect(0, 0, 5, 1), X, Y, 1);

        Assert.False(projection.IsTruncated);
        Assert.Equal(new[] { 100m, 50m, 50m },
            Enumerable.Range(0, 3).Select(x => At(projection, x + 0.5f, 0.5f, BookSide.Bid).RestingSize));
        Assert.DoesNotContain(projection.Spans, span => span.Rect.Right > 3);
        Assert.Contains(frame.Gaps, gap => gap.From == Start.AddMilliseconds(200) && gap.To == Start.AddMilliseconds(200) &&
            gap.Reason == DepthGapReason.Sequence);
        Assert.Contains(frame.Gaps, gap => gap.From == Start.AddMilliseconds(2200) && gap.To == Start.AddSeconds(5) &&
            gap.Reason == DepthGapReason.Missing);
    }

    [Fact]
    public void A_million_pixel_horizontal_span_uses_the_same_small_work_budget_as_a_single_pixel()
    {
        var frame = new DepthHeatmapFrame(
            [new(Start, Start.AddSeconds(1_000_000), -1, BookSide.Bid, 0.1234567890123456789012345678m, DepthCoverage.SnapshotOnly)],
            [], false);
        var projection = new DepthHeatmapProjection();

        projection.Project(frame, new SKRect(0, 0, 1_000_000, 1), X, Y, 1, maxSpans: 1, maxWork: 2);

        Assert.False(projection.IsTruncated);
        var span = Assert.Single(projection.Spans);
        Assert.Equal(new SKRect(0, 0, 1_000_000, 1), span.Rect);
        Assert.Equal(0.1234567890123456789012345678m, span.RestingSize);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(10, 2)]
    public void Output_and_input_work_limits_are_visible_and_reuse_does_not_retain_old_spans(int maxSpans, int maxWork)
    {
        var frame = new DepthHeatmapFrame(
        [
            new(Start, Start.AddSeconds(1), -1, BookSide.Bid, 10m, DepthCoverage.SnapshotOnly),
            new(Start.AddSeconds(1), Start.AddSeconds(3), -1, BookSide.Bid, 5m, DepthCoverage.SnapshotOnly),
        ], [], false);
        var projection = new DepthHeatmapProjection();
        var viewport = new SKRect(0, 0, 3, 1);

        projection.Project(frame, viewport, X, Y, 1, maxSpans, maxWork);

        Assert.True(projection.IsTruncated);
        var retained = Assert.Single(projection.Spans);
        Assert.Equal(new SKRect(0, 0, 1, 1), retained.Rect);
        Assert.Equal(10m, retained.RestingSize);

        projection.Project(new([], [], true), viewport, X, Y, 1, maxSpans, maxWork);
        Assert.True(projection.IsTruncated);
        Assert.Empty(projection.Spans);

        projection.Project(new([], [], false), viewport, X, Y, 1, maxSpans, maxWork);
        Assert.False(projection.IsTruncated);
        Assert.Empty(projection.Spans);
    }

    [Fact]
    public void Extremely_tall_price_rows_stop_at_the_work_limit_and_expose_the_incomplete_projection()
    {
        var frame = new DepthHeatmapFrame(
            [new(Start, Start.AddSeconds(1), -1, BookSide.Bid, 12.5m, DepthCoverage.PartialBook)], [], false);
        var projection = new DepthHeatmapProjection();

        projection.Project(frame, new SKRect(0, 0, 1, 1_000_000), X, ticks => Y(ticks) * 1_000_000, 1,
            maxSpans: 4, maxWork: 3);

        Assert.True(projection.IsTruncated);
        Assert.InRange(projection.Spans.Count, 1, 4);
        Assert.Equal(12.5m, At(projection, 0.5f, 0.5f, BookSide.Bid).RestingSize);
        Assert.Equal(12.5m, At(projection, 0.5f, 1.5f, BookSide.Bid).RestingSize);
        Assert.All(projection.Spans, span =>
        {
            Assert.True(span.Rect.Bottom <= 2);
            Assert.Equal(DepthCoverage.PartialBook, span.Coverage);
        });
    }

    private static DepthSample Sample(long ordinal, int milliseconds, decimal bid, DepthCoverage coverage,
        DepthGapReason gap = DepthGapReason.None) => new(Guid.Empty, ordinal, new DepthUpdate
        {
            Snapshot = new([new BookLevel(-1, bid)], [new BookLevel(1, 2m)], Start.AddMilliseconds(milliseconds)),
            ReceivedAt = Start.AddMilliseconds(milliseconds),
            IsFullRefresh = true,
            ProviderId = "fake",
            Venue = "venue-a",
            Coverage = coverage,
            GapBefore = gap,
        });

    private static float X(DateTime time) => (float)(time - Start).TotalSeconds;
    private static float Y(decimal ticks) => -(float)ticks;
    private static DepthHeatmapSpan At(DepthHeatmapProjection projection, float x, float y, BookSide side) =>
        Assert.Single(projection.Spans, span => span.Side == side && span.Rect.Left <= x && x < span.Rect.Right &&
            span.Rect.Top <= y && y < span.Rect.Bottom);
}
