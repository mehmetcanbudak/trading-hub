using System.ComponentModel.DataAnnotations;

namespace TradingHub.Indicators.Native;

/// <summary>Incremental Wilder ATR over completed bars (mean of available true ranges while warming up).</summary>
public struct RunningAtr
{
    private decimal _sum, _value, _prevClose; private int _count;
    public int Length { get; }
    public RunningAtr(int length) { Length = Math.Max(1, length); _sum = _value = _prevClose = 0; _count = 0; }
    public decimal Value => _value;
    public bool Ready => _count >= Length;
    public decimal Add(StudyBar bar)
    {
        decimal tr = _count == 0 ? bar.High - bar.Low : Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - _prevClose), Math.Abs(bar.Low - _prevClose)));
        _prevClose = bar.Close; _count++;
        if (_count <= Length) { _sum += tr; _value = _sum / _count; }
        else _value = (_value * (Length - 1) + tr) / Length;
        return _value;
    }
}

/// <summary>Incremental EMA seeded by the running mean of its first <see cref="Length"/> values.</summary>
public struct RunningEma
{
    private decimal _value, _sum; private int _count;
    public int Length { get; }
    public RunningEma(int length) { Length = Math.Max(1, length); _value = _sum = 0; _count = 0; }
    public decimal Value => _value;
    public bool Ready => _count >= Length;
    public decimal Add(decimal x)
    {
        _count++;
        if (_count <= Length) { _sum += x; _value = _sum / _count; }
        else _value += (x - _value) * 2m / (Length + 1);
        return _value;
    }
}

/// <summary>A swing confirmed at <see cref="Confirmed"/> for the extreme at <see cref="Bar"/>.</summary>
public readonly record struct StudySwing(int Bar, int Confirmed, decimal Price, bool High);

public static class StudySwings
{
    /// <summary>
    /// The swings confirmed at the close of <paramref name="bar"/>: the bar <paramref name="right"/> back is a high (low) when no
    /// bar <paramref name="left"/> before it is higher (lower) and every bar after it through <paramref name="bar"/> is strictly
    /// lower (higher). Requires causal bars across the window; uses no bar after <paramref name="bar"/>.
    /// </summary>
    public static (StudySwing? High, StudySwing? Low) Confirm(IReadOnlyList<StudyBar> bars, int bar, int left, int right)
    {
        left = Math.Max(1, left); right = Math.Max(1, right);
        int p = bar - right;
        if (p - left < 0 || bar >= bars.Count) return (null, null);
        bool hi = true, lo = true;
        for (int k = p - left; k <= bar && (hi || lo); k++)
        {
            if (!bars[k].Causal) return (null, null);
            if (k == p) continue;
            if (k < p ? bars[k].High > bars[p].High : bars[k].High >= bars[p].High) hi = false;
            if (k < p ? bars[k].Low < bars[p].Low : bars[k].Low <= bars[p].Low) lo = false;
        }
        return (hi ? new StudySwing(p, bar, bars[p].High, true) : null, lo ? new StudySwing(p, bar, bars[p].Low, false) : null);
    }
}

public enum WorldSession
{
    [Display(Name = "Sydney")] Sydney,
    [Display(Name = "Tokyo")] Tokyo,
    [Display(Name = "London")] London,
    [Display(Name = "New York")] NewYork,
    [Display(Name = "Asia (19:00–04:00 New York)")] Asia,
}

/// <summary>
/// A daily local-time window in its own time zone (DST followed by the zone rules). Bars belong by opening time; weekends
/// (by the window's trading date) are excluded; exchange holidays simply have no bars, so no session forms.
/// </summary>
public sealed class LocalSessionWindow
{
    public TimeZoneInfo Zone { get; }
    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public LocalSessionWindow(TimeZoneInfo zone, TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || start >= TimeSpan.FromDays(1) || end < TimeSpan.Zero || end >= TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(start));
        Zone = zone; Start = start; End = end;
    }
    public bool Overnight => End <= Start;

    /// <summary>Whether <paramref name="utc"/> opens inside the window, and the trading date the window belongs to.</summary>
    public bool Contains(DateTime utc, out DateOnly tradingDate)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
        var t = local.TimeOfDay; var date = DateOnly.FromDateTime(local);
        bool inside = Overnight ? t >= Start || t < End : t >= Start && t < End;
        // an overnight window belongs to the date it ends on (as futures sessions do)
        tradingDate = Overnight && t >= Start ? date.AddDays(1) : date;
        return inside && tradingDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    /// <summary>The UTC instant the window of <paramref name="tradingDate"/> opens (an overnight window opens the day before).</summary>
    public DateTime StartUtc(DateOnly tradingDate) => Utc(Overnight ? tradingDate.AddDays(-1) : tradingDate, Start);

    /// <summary>The UTC instant the window of <paramref name="tradingDate"/> closes (DST-correct; skipped local times move forward).</summary>
    public DateTime EndUtc(DateOnly tradingDate) => Utc(tradingDate, End);

    private DateTime Utc(DateOnly day, TimeSpan time)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.FromTimeSpan(time)), DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }

    private static readonly Dictionary<WorldSession, LocalSessionWindow> Presets = new()
    {
        [WorldSession.Sydney] = new(TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"), TimeSpan.FromHours(7), TimeSpan.FromHours(16)),
        [WorldSession.Tokyo] = new(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"), TimeSpan.FromHours(9), TimeSpan.FromHours(18)),
        [WorldSession.London] = new(TimeZoneInfo.FindSystemTimeZoneById("Europe/London"), TimeSpan.FromHours(8), TimeSpan.FromHours(17)),
        [WorldSession.NewYork] = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), TimeSpan.FromHours(8), TimeSpan.FromHours(17)),
        [WorldSession.Asia] = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), TimeSpan.FromHours(19), TimeSpan.FromHours(4)),
    };

    public static LocalSessionWindow Of(WorldSession session) => Presets[session];
}
