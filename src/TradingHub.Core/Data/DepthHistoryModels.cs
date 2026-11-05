using System.ComponentModel.DataAnnotations;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Opt-in limits apply independently to each provider/instrument recording.</summary>
public sealed record DepthRecordingOptions
{
    public bool Enabled { get; init; }
    public int MaxMemorySamples { get; init; } = 10_000;
    public long MaxMemoryBytes { get; init; } = 8L * 1024 * 1024;
    public long MaxHistoryBytes { get; init; } = 4L * 1024 * 1024;
    public long MaxSelectedHistoryBytes { get; init; } = 8L * 1024 * 1024;
    public long MaxQueuedBytes { get; init; } = 2L * 1024 * 1024;
    public long MaxSampleBytes { get; init; } = 256L * 1024;
    public TimeSpan MemoryRetention { get; init; } = TimeSpan.FromHours(6);
    public int RetentionDays { get; init; } = 14;
    public long MaxDiskBytes { get; init; } = 256L * 1024 * 1024;
    public long MaxSegmentBytes { get; init; } = 8L * 1024 * 1024;
    public int QueueCapacity { get; init; } = 512;
    public int MaxLevelsPerSide { get; init; } = 1000;
    public int MaxChangesPerUpdate { get; init; } = 10_000;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromSeconds(10);

    internal DepthRecordingOptions Checked()
    {
        long live = Math.Clamp(MaxMemoryBytes, 1024, 32L * 1024 * 1024);
        long history = Math.Clamp(MaxHistoryBytes, 1024, 16L * 1024 * 1024);
        long selected = Math.Clamp(MaxSelectedHistoryBytes, 1024, 16L * 1024 * 1024);
        long queued = Math.Clamp(MaxQueuedBytes, 5120, 8L * 1024 * 1024);
        long single = Math.Clamp(MaxSampleBytes, 1024, Math.Min(512L * 1024, Math.Min(Math.Min(live, history), Math.Min(selected, queued / 5))));
        return this with
        {
            MaxMemorySamples = Math.Clamp(MaxMemorySamples, 1, 100_000),
            MaxMemoryBytes = live, MaxHistoryBytes = history, MaxSelectedHistoryBytes = selected,
            MaxQueuedBytes = queued, MaxSampleBytes = single,
            MemoryRetention = TimeSpan.FromTicks(Math.Clamp(MemoryRetention.Ticks, TimeSpan.TicksPerSecond, TimeSpan.TicksPerDay * 365)),
            RetentionDays = Math.Clamp(RetentionDays, 1, 365),
            MaxDiskBytes = Math.Clamp(MaxDiskBytes, 1024, 4096L * 1024 * 1024),
            MaxSegmentBytes = Math.Clamp(MaxSegmentBytes, 512, Math.Max(512, Math.Min(MaxDiskBytes, 64L * 1024 * 1024))),
            QueueCapacity = Math.Clamp(QueueCapacity, 1, 4096),
            MaxLevelsPerSide = Math.Clamp(MaxLevelsPerSide, 1, 10_000),
            MaxChangesPerUpdate = Math.Clamp(MaxChangesPerUpdate, 1, 100_000),
            MaxAge = TimeSpan.FromTicks(Math.Clamp(MaxAge.Ticks, TimeSpan.TicksPerSecond, TimeSpan.TicksPerMinute * 5)),
        };
    }
}

public enum DepthRecordingState { Disabled, Recording, Stopped, Error }

public sealed record DepthHistoryStatus(DepthRecordingState RecordingState, string? Message,
    DateTime? LastReceivedAt, DateTime? LastPersistedAt, long DroppedRecords,
    DepthCoverage Coverage, bool IsStale);

/// <summary>
/// One received L2 update, retaining exact decimal sizes, absolute changes and source metadata.
/// Time is arrival/availability time; SourceTime is the provider timestamp, if supplied.
/// A recording-local ordinal preserves receive order, including equal timestamps and gap records.
/// </summary>
public sealed record DepthSample(Guid RecordingId, long Ordinal, DepthUpdate Update)
{
    public DateTime Time => Update.ReceivedAt;
    public DateTime SourceTime => Update.Snapshot.Time;
    public DepthSnapshot Snapshot => Update.Snapshot;
    public string ProviderId => Update.ProviderId ?? "";
    public string Venue => Update.Venue ?? "";
    public DepthCoverage Coverage => Update.Coverage;
    public DepthGapReason GapBefore => Update.GapBefore;
    public bool IsUsable => Update.IsUsable;

    /// <summary>Conservative accounting, not a runtime struct-layout assertion; includes object/array/reference overhead.</summary>
    public long EstimatedMemoryBytes => EstimateMemoryBytes(Update);
    public static long EstimateMemoryBytes(DepthUpdate update) =>
        512L + 32L * (update.Snapshot.Bids.Length + (long)update.Snapshot.Asks.Length) +
        48L * update.Changes.Count + 2L * ((update.ProviderId?.Length ?? 0) + (long)(update.Venue?.Length ?? 0) + (update.Message?.Length ?? 0));

    /// <summary>Inclusive distance from each side's own best price. Negative prices are valid.</summary>
    public (decimal Bid, decimal Ask) Within(int withinTicks)
    {
        if (!IsUsable) return (0, 0);
        int distance = Math.Clamp(withinTicks, 0, 100_000);
        decimal bid = 0, ask = 0;
        foreach (var level in Snapshot.Bids)
            if ((decimal)Snapshot.BestBidTicks - level.PriceTicks <= distance) bid += level.Size;
        foreach (var level in Snapshot.Asks)
            if ((decimal)level.PriceTicks - Snapshot.BestAskTicks <= distance) ask += level.Size;
        return (bid, ask);
    }
}

/// <summary>Samples lie in [From, To); Preceding is only a possible duration-average carry-in.</summary>
public sealed record DepthHistoryQuery(DateTime From, DateTime To, IReadOnlyList<DepthSample> Samples,
    DepthSample? Preceding, DepthCoverage Coverage, string? Message, bool IsTruncated = false);

/// <summary>A pure disk read. Apply only after the owning InstrumentData generation is checked.</summary>
public sealed record DepthHistoryLoad(DateTime From, DateTime To, IReadOnlyList<DepthSample> Samples,
    string? Message, bool IsTruncated = false);

public enum DepthAggregation
{
    [Display(Name = "Sample average (equal weight per snapshot)")] SampleAverage,
    [Display(Name = "Duration average (covered time only)")] DurationAverage,
    [Display(Name = "Last fresh snapshot")] Last,
    [Display(Name = "Separate-side maximum")] Maximum,
}

public sealed record DepthMeasurement(bool IsAvailable, decimal Bid, decimal Ask, int Samples,
    TimeSpan CoveredDuration, DepthCoverage Coverage, string? Message)
{
    public decimal Delta => Bid - Ask;
    public decimal Imbalance
    {
        get
        {
            if (Bid <= decimal.MaxValue - Ask) return Bid + Ask == 0 ? 0 : (Bid - Ask) / (Bid + Ask) * 100;
            decimal scale = Math.Max(Bid, Ask);
            decimal bid = Bid / scale, ask = Ask / scale;
            return (bid - ask) / (bid + ask) * 100;
        }
    }
}

/// <summary>
/// Resting-size statistics, not executions. SampleAverage gives each received usable snapshot
/// one vote. DurationAverage weights each known book until the next update, gap or MaxAge,
/// divides by covered duration only, and explicitly reports uncovered duration. Last requires
/// a book fresh at the interval end. Maximum is the separate maximum of each side.
/// </summary>
public static class DepthStatistics
{
    public static DepthMeasurement Measure(DepthHistoryQuery query, int withinTicks,
        DepthAggregation aggregation, TimeSpan maxAge, bool allowPartial = false)
    {
        if (query.To <= query.From) return Missing("The depth interval is empty.");
        maxAge = maxAge <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : maxAge;
        var coverage = query.Coverage;
        decimal bid = 0, ask = 0;
        bool bidIsMean = false, askIsMean = false;
        long coveredTicks = 0;
        int count = 0;
        string? provider = null, venue = null;
        bool multipleSources = false;
        DepthSample? active = query.Preceding;
        DateTime cursor = query.From;
        foreach (var sample in query.Samples)
        {
            AccumulateUntil(sample.Time);
            active = sample;
            cursor = sample.Time < query.From ? query.From : sample.Time;
            coverage |= sample.Coverage;
            if (!Usable(sample)) continue;
            ObserveSource(sample);
            if (aggregation is DepthAggregation.Maximum or DepthAggregation.SampleAverage)
            {
                var size = sample.Within(withinTicks);
                if (aggregation == DepthAggregation.Maximum)
                {
                    bid = Math.Max(bid, size.Bid);
                    ask = Math.Max(ask, size.Ask);
                }
                else
                {
                    bid = AccumulateSample(bid, size.Bid, count + 1, ref bidIsMean);
                    ask = AccumulateSample(ask, size.Ask, count + 1, ref askIsMean);
                }
            }
            count++;
        }
        AccumulateUntil(query.To);
        if (coveredTicks < (query.To - query.From).Ticks) coverage |= DepthCoverage.Missing;
        if (multipleSources && aggregation != DepthAggregation.Last)
            return Missing("More than one provider/venue occurs in this interval; no consolidated depth average is invented.", coverage | DepthCoverage.SequenceGap);
        if (aggregation == DepthAggregation.Last)
        {
            if (active == null || !Usable(active) || query.To - active.Time > maxAge)
                return new(false, 0, 0, count, TimeSpan.FromTicks(coveredTicks), coverage | DepthCoverage.Missing, "No fresh book at the interval end.");
            (bid, ask) = active.Within(withinTicks);
            coverage |= active.Coverage;
        }
        else if (aggregation == DepthAggregation.DurationAverage)
        {
            if (coveredTicks == 0) return Missing("No covered depth duration.", coverage);
        }
        else
        {
            if (count == 0) return Missing("No usable recorded snapshots in this interval.", coverage);
            if (aggregation == DepthAggregation.SampleAverage)
            {
                if (!bidIsMean) bid /= count;
                if (!askIsMean) ask /= count;
            }
        }
        string? message = coverage.HasFlag(DepthCoverage.Missing) ? "Partial interval: gaps or stale books are excluded, not carried forward."
            : (coverage & (DepthCoverage.PartialBook | DepthCoverage.Truncated)) != 0 ? "Partial provider book; sizes cover only the recorded levels." : query.Message;
        return new(true, bid, ask, count, TimeSpan.FromTicks(coveredTicks), coverage, message);

        bool Usable(DepthSample sample) => sample.IsUsable &&
            (allowPartial || (sample.Coverage & (DepthCoverage.PartialBook | DepthCoverage.Truncated)) == 0);

        void ObserveSource(DepthSample sample)
        {
            if (provider != null && (provider != sample.ProviderId || venue != sample.Venue)) multipleSources = true;
            provider = sample.ProviderId; venue = sample.Venue;
        }

        void AccumulateUntil(DateTime end)
        {
            if (end > query.To) end = query.To;
            if (active == null || !Usable(active) || end <= cursor) return;
            var expires = active.Time.Ticks > DateTime.MaxValue.Ticks - maxAge.Ticks ? DateTime.MaxValue : active.Time + maxAge;
            var through = end < expires ? end : expires;
            if (through <= cursor) return;
            long ticks = (through - cursor).Ticks;
            if (aggregation == DepthAggregation.DurationAverage)
            {
                coverage |= active.Coverage;
                ObserveSource(active);
                var size = active.Within(withinTicks);
                if (coveredTicks == 0)
                    (bid, ask) = size;
                else
                {
                    long totalTicks = coveredTicks + ticks;
                    bid = DurationMean(bid, size.Bid, ticks, totalTicks);
                    ask = DurationMean(ask, size.Ask, ticks, totalTicks);
                }
            }
            coveredTicks += ticks;
            cursor = end;
        }

        static decimal AccumulateSample(decimal total, decimal size, int count, ref bool isMean)
        {
            // Keep the exact sum while it fits, including very small decimal contributions.
            // Only switch to a bounded mean if the sum would overflow a representable average.
            if (!isMean && size <= decimal.MaxValue - total) return total + size;
            if (!isMean) { total /= count - 1; isMean = true; }
            return DurationMean(total, size, 1, count);
        }

        static decimal DurationMean(decimal mean, decimal size, long ticks, long totalTicks)
        {
            // Keep a mean rather than a size-tick total. Divide first only for large
            // changes, so representable tiny decimal contributions do not underflow.
            decimal change = size - mean;
            decimal adjustment = Math.Abs(change) < decimal.MaxValue / ticks
                ? change * ticks / totalTicks
                : change / totalTicks * ticks;
            return mean + adjustment;
        }
    }

    private static DepthMeasurement Missing(string message, DepthCoverage coverage = DepthCoverage.Missing) =>
        new(false, 0, 0, 0, TimeSpan.Zero, coverage | DepthCoverage.Missing, message);
}
