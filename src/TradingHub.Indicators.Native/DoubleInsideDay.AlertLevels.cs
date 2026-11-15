namespace TradingHub.Indicators.Native;

public partial class DoubleInsideDay
{
    /// <summary>The breakout edges of each drawn pattern box: live while active, up to its end bar once ended (when ended patterns are shown).</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var levels = new StudyLevelList(CurrentBar);
        if (CurrentBar == 0) return levels;
        int last = CurrentBar - 1;
        foreach (var pattern in _patterns)
        {
            bool ended = pattern.StateAt(last) != 0;
            if (ended && !ShowEnded || pattern.AvailableBar > last) continue;
            var mother = GetCandle(pattern.SourceBar);
            string key = StudyAlertNames.Key(mother), date = StudyAlertNames.Day(mother.SessionDate);
            int to = ended ? pattern.EndBar : int.MaxValue;
            levels.Add($"did.high:{key}", $"Double inside day high {date}", pattern.High, pattern.AvailableBar, to);
            levels.Add($"did.low:{key}", $"Double inside day low {date}", pattern.Low, pattern.AvailableBar, to);
        }
        return levels;
    }
}
