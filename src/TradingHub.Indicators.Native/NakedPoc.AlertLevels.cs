namespace TradingHub.Indicators.Native;

public partial class NakedPoc
{
    /// <summary>Each drawn POC of an enabled horizon: live until touched, then (only when touched POCs are shown) up to the touch.</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var levels = new StudyLevelList(CurrentBar);
        if (CurrentBar == 0) return levels;
        foreach (var level in _levels)
        {
            int k = Array.IndexOf(Periods, level.Period);
            bool touched = level.TouchAvailableBar >= 0;
            if (k < 0 || !Enabled(k) || touched && !ShowTouched) continue;
            var source = GetCandle(level.SourceStartBar);
            string period = StudyAlertNames.Period(source, level.Period);
            string name = k switch
            {
                0 => $"Session naked POC {period}",
                1 => $"Weekly naked POC {period}",
                2 => $"Monthly naked POC {period}",
                _ => $"Yearly naked POC {period}",
            };
            levels.Add(string.Concat("npoc:", level.Period.ToString(), ":", StudyAlertNames.Key(source)), name,
                level.Price, level.AvailableBar, touched ? level.TouchBar : int.MaxValue);
        }
        return levels;
    }
}
