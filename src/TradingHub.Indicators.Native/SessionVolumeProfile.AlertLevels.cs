namespace TradingHub.Indicators.Native;

public partial class SessionVolumeProfile
{
    /// <summary>Most recent profiles offered for alerts.</summary>
    private const int AlertProfiles = 30;

    /// <summary>
    /// Each recent profile's POC (extended until the first later bar touching it when that is on) and, when shown, its VAH/VAL
    /// across the profile; the developing profile stays live.
    /// </summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var levels = new StudyLevelList(CurrentBar);
        if (CurrentBar == 0) return levels;
        int last = CurrentBar - 1;
        for (int i = Math.Max(0, _profiles.Count - AlertProfiles); i < _profiles.Count; i++)
        {
            var p = _profiles[i]; if (p.MaxVolume <= 0) continue;
            var source = GetCandle(p.StartBar);
            string key = StudyAlertNames.Key(source), date = StudyAlertNames.Day(source.SessionDate);
            int end = p.EndBar >= last ? int.MaxValue : p.EndBar, pocEnd = end;
            if (ExtendPoc && end != int.MaxValue)
            {
                pocEnd = int.MaxValue;
                for (int b = p.EndBar + 1; b <= last; b++)
                {
                    var c = GetCandle(b);
                    if (c.Low <= p.Poc && c.High >= p.Poc) { pocEnd = b; break; }
                }
            }
            levels.Add($"poc:{key}", $"POC {date}", p.Poc, p.StartBar, pocEnd);
            if (!ShowValueAreaLines) continue;
            levels.Add($"vah:{key}", $"VAH {date}", p.Vah, p.StartBar, end);
            levels.Add($"val:{key}", $"VAL {date}", p.Val, p.StartBar, end);
        }
        return levels;
    }
}
