namespace TradingHub.Market;

/// <summary>What was observed, rather than the shape of the event used to display it.</summary>
[Flags]
public enum DataQuality : byte
{
    None = 0,
    Raw = 1,
    Compact = 2,
    Synthetic = 4,
    InferredSide = 8,
    Partial = 16,
}

/// <summary>Source and temporal resolution survive storage, chart aggregation and replay.</summary>
public readonly record struct TradeProvenance(
    DataQuality Quality = DataQuality.Raw,
    int ResolutionSeconds = 0,
    string? SourceIdentity = null,
    long CorrectionRevision = 0,
    int ObservedTradeCount = 1,
    bool HasKnownAvailability = true,
    int ResolutionOffsetSeconds = 0,
    string? SourceEventId = null)
{
    public bool IsObservedTrade => (Quality & DataQuality.Raw) != 0 && (Quality & (DataQuality.Compact | DataQuality.Synthetic)) == 0;
    public bool IsMeasured => (Quality & (DataQuality.Raw | DataQuality.Compact)) != 0 && (Quality & DataQuality.Synthetic) == 0;
    public bool IsSideInferred => (Quality & DataQuality.InferredSide) != 0;
    public bool IsPartial => (Quality & DataQuality.Partial) != 0;

    /// <summary>Nominal source window. Check HasKnownAvailability before using its end as strategy knowledge.</summary>
    public (DateTime From, DateTime To) SourceWindow(DateTime location)
    {
        if (ResolutionSeconds <= 0) return (location, location);
        long length = (long)ResolutionSeconds * TimeSpan.TicksPerSecond;
        long offset = (long)ResolutionOffsetSeconds * TimeSpan.TicksPerSecond;
        long start = CandleFloor(location.Ticks - offset, length) * length + offset;
        return (new DateTime(start, DateTimeKind.Utc), new DateTime(checked(start + length), DateTimeKind.Utc));
    }

    public DateTime AvailabilityTime(DateTime location) => SourceWindow(location).To;

    private static long CandleFloor(long value, long length)
    {
        long quotient = value / length;
        return value < 0 && value % length != 0 ? quotient - 1 : quotient;
    }

    internal TradeProvenance Normalize(long id)
    {
        var quality = Quality == DataQuality.None ? (id < 0 ? DataQuality.Synthetic | DataQuality.InferredSide : DataQuality.Raw) : Quality;
        // Compact display summaries also use negative IDs, but are never individual observed trades.
        if (id < 0 && (quality & DataQuality.Compact) == 0) quality = (quality & ~DataQuality.Raw) | DataQuality.Synthetic;
        if ((quality & (DataQuality.Compact | DataQuality.Synthetic)) != 0) quality &= ~DataQuality.Raw;
        if ((quality & DataQuality.Compact) != 0 && ObservedTradeCount <= 0) quality |= DataQuality.Partial;
        int resolution = Math.Max(0, ResolutionSeconds);
        int offset = resolution == 0 ? 0 : (int)(((long)ResolutionOffsetSeconds % resolution + resolution) % resolution);
        return this with { Quality = quality, ResolutionSeconds = resolution, ResolutionOffsetSeconds = offset,
            ObservedTradeCount = Quality == DataQuality.None ? (id < 0 ? 0 : 1) : Math.Max(0, ObservedTradeCount),
            HasKnownAvailability = Quality == DataQuality.None ? id >= 0 : HasKnownAvailability };
    }
}
