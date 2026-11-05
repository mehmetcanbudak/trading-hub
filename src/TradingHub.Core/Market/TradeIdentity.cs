namespace TradingHub.Market;

/// <summary>
/// Exact provider-qualified event identity. Native opaque IDs are never replaced with a content hash.
/// Legacy numeric IDs retain their existing UTC-day scope when no native/observation identity was recorded.
/// Source strings compare ordinally; missing source and event IDs normalize only to their empty forms.
/// </summary>
public readonly record struct TradeIdentity(string SourceIdentity, string? SourceEventId, int UtcDay, long LegacyId)
{
    public static TradeIdentity From(in Trade trade)
    {
        return From(trade.Id, trade.Time, trade.Provenance);
    }

    public static TradeIdentity From(long id, DateTime location, in TradeProvenance provenance)
    {
        string source = provenance.SourceIdentity ?? string.Empty;
        string? eventId = provenance.SourceEventId;
        return !string.IsNullOrEmpty(eventId)
            ? new(source, eventId, 0, 0)
            : new(source, null, DateOnly.FromDateTime(location).DayNumber, id);
    }
}
