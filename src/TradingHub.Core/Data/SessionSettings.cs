namespace TradingHub.Data;

/// <summary>
/// Where each trading day starts (daily candles, session VWAP, profiles, IsNewSession). Default: 00:00 UTC, as in crypto.
/// A session that starts in the evening (at or after 12:00 local, e.g. CME 18:00 New York) belongs to the NEXT calendar
/// day's trading date: the session opening Sunday 18:00 is Monday's, so it starts the new week and Monday's range.
/// </summary>
public sealed class SessionSettings
{
    public static readonly SessionSettings CryptoUtc = new();

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    public TimeSpan Start { get; init; } = TimeSpan.Zero;

    /// <summary>Days from the local date a session opens on to its trading date (1 for evening starts, else 0).</summary>
    public int TradingDateOffset => Start >= TimeSpan.FromHours(12) ? 1 : 0;

    /// <summary>Trading date (in the session's zone) an UTC instant belongs to.</summary>
    public DateOnly SessionDate(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);
        var shifted = local - Start;
        return DateOnly.FromDateTime(shifted).AddDays(TradingDateOffset);
    }

    /// <summary>UTC instant the session of a trading date starts.</summary>
    public DateTime SessionStartUtc(DateOnly date)
    {
        var local = date.AddDays(-TradingDateOffset).ToDateTime(TimeOnly.MinValue) + Start;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (TimeZone == TimeZoneInfo.Utc) return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
    }

    public bool IsNewSession(DateTime prevUtc, DateTime curUtc) => SessionDate(prevUtc) != SessionDate(curUtc);

    public bool IsNewWeek(DateTime prevUtc, DateTime curUtc)
    {
        var a = SessionDate(prevUtc);
        var b = SessionDate(curUtc);
        return WeekStart(a) != WeekStart(b);
    }

    public bool IsNewMonth(DateTime prevUtc, DateTime curUtc)
    {
        var a = SessionDate(prevUtc);
        var b = SessionDate(curUtc);
        return a.Month != b.Month || a.Year != b.Year;
    }

    public static DateOnly WeekStart(DateOnly d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // lunes = 0
        return d.AddDays(-diff);
    }
}
