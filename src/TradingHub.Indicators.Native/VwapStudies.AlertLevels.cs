namespace TradingHub.Indicators.Native;

public partial class PriorClosingVwap
{
    /// <summary>The drawn closing-level history: each kept period's closing VWAP and, with bands on, its band edges, live from availability.</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var levels = new StudyLevelList(CurrentBar);
        if (CurrentBar == 0 || !ShowClosingLevels) return levels;
        for (int i = Math.Max(0, _closed.Count - Keep); i < _closed.Count; i++)
        {
            var level = _closed[i];
            var source = GetCandle(level.SourceEndBar);
            string key = StudyAlertNames.Key(source), period = StudyAlertNames.Period(source, Period);
            levels.Add($"close:{key}", $"Closing VWAP {period}", level.Value, level.AvailableBar);
            if (Sigma <= 0) continue;
            decimal width = Sigma * level.Deviation;
            levels.Add($"close.upper:{key}", $"Closing VWAP upper band {period}", level.Value + width, level.AvailableBar);
            levels.Add($"close.lower:{key}", $"Closing VWAP lower band {period}", level.Value - width, level.AvailableBar);
        }
        return levels;
    }
}
