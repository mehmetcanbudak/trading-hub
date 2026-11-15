using System.Globalization;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Collects the alertable levels a study draws (see <see cref="Indicator.GetAlertLevels"/>): skips non-positive prices and
/// repeated Ids, and keeps every span inside the loaded bars; a span reaching past the last bar stays open to the right
/// edge (<c>int.MaxValue</c>).
/// </summary>
internal sealed class StudyLevelList : List<IndicatorLevel>
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly int _bars;

    internal StudyLevelList(int bars) => _bars = bars;

    internal void Add(string id, string name, decimal price, int fromBar, int toBar = int.MaxValue)
    {
        if (_bars <= 0 || price <= 0 || !_ids.Add(id)) return;
        int from = Math.Clamp(fromBar, 0, _bars - 1);
        int to = toBar > _bars - 1 ? int.MaxValue : Math.Max(toBar, from);
        Add(new IndicatorLevel(id, name, price, from, to));
    }
}

/// <summary>Stable Id keys and invariant name suffixes for study alert levels.</summary>
internal static class StudyAlertNames
{
    /// <summary>Id key of a source bar: its opening time, which survives recalculation (bar indexes and prices do not).</summary>
    internal static string Key(IndicatorCandle candle) => candle.Time.Ticks.ToString(CultureInfo.InvariantCulture);

    internal static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Minute(DateTime time) => time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The period containing <paramref name="candle"/>: its first session date, or its opening time below one day.</summary>
    internal static string Period(IndicatorCandle candle, StudyTimeFrame frame)
    {
        if (frame == StudyTimeFrame.Chart) return Minute(candle.Time);
        if (StudyMath.Duration(frame) > TimeSpan.Zero) return Minute(new DateTime(StudyMath.PeriodKey(candle, frame), DateTimeKind.Utc));
        var date = candle.SessionDate;
        return Day(frame switch
        {
            StudyTimeFrame.Week => date.AddDays(-((int)date.DayOfWeek + 6) % 7),
            StudyTimeFrame.Month => new DateOnly(date.Year, date.Month, 1),
            StudyTimeFrame.Quarter => new DateOnly(date.Year, 1 + (date.Month - 1) / 3 * 3, 1),
            StudyTimeFrame.Year => new DateOnly(date.Year, 1, 1),
            _ => date
        });
    }
}
