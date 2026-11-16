namespace TradingHub.Indicators.Native;

public partial class SessionBackgrounds
{
    /// <summary>Most recent sessions per window offered for alerts; older ranges are history that never trades again.</summary>
    private const int AlertSessions = 30;

    /// <summary>
    /// The drawn high, low and opening lines of each enabled window's recent sessions, from the session's first to its last
    /// bar; the session still containing the last bar stays live.
    /// </summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var levels = new StudyLevelList(CurrentBar);
        int bars = Math.Min(CurrentBar, _ranges[0].Count);
        if (bars == 0 || !ShowRanges) return levels;
        for (int k = 0; k < 3; k++)
        {
            if (!(k == 0 ? FirstEnabled : k == 1 ? SecondEnabled : ThirdEnabled)) continue;
            int found = 0;
            for (int b = bars - 1; b >= 0 && found < AlertSessions;)
            {
                if (_ranges[k][b] is not { } range) { b--; continue; }
                // b is the last bar of this session run: its range is final
                var source = GetCandle(range.SourceStartBar);
                string key = StudyAlertNames.Key(source), date = StudyAlertNames.Day(range.Date);
                int window = k + 1;
                string id = string.Concat("w", window.ToString(System.Globalization.CultureInfo.InvariantCulture), ".");
                int to = b == CurrentBar - 1 ? int.MaxValue : b;
                levels.Add(id + "high:" + key, $"Window {window} high {date}", range.High, range.SourceStartBar, to);
                levels.Add(id + "low:" + key, $"Window {window} low {date}", range.Low, range.SourceStartBar, to);
                levels.Add(id + "open:" + key, $"Window {window} open {date}", range.Open, range.SourceStartBar, to);
                found++;
                b = range.SourceStartBar - 1;
            }
        }
        return levels;
    }
}
