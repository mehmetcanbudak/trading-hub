namespace TradingHub.Indicators.Native;

public partial class CompositeVolumeProfile
{
    /// <summary>
    /// The lines of each drawn profile (POC; value-area edges in the volume view; buyer/seller POC and outer area when shown),
    /// from its first to its last bar (the developing profile stays live), plus the projected POCs that are shown.
    /// </summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var levels = new StudyLevelList(CurrentBar);
        if (CurrentBar == 0 || !CalculateDisplayProfiles) return levels;
        lock (_sync)
        {
            RefreshDisplay();
            for (int i = Math.Max(0, _display.Count - _count); i < _display.Count; i++)
            {
                var profile = _display[i]; if (profile.MaximumVolume == 0) continue;
                var source = GetCandle(profile.StartBar);
                string key = StudyAlertNames.Key(source), date = StudyAlertNames.Day(source.SessionDate);
                int from = profile.StartBar, to = i == _display.Count - 1 ? int.MaxValue : profile.EndBar;
                levels.Add($"poc:{key}", $"POC {date}", profile.Poc, from, to);
                if (View == ProfileView.Volume)
                {
                    levels.Add($"vah:{key}", $"VAH {date}", profile.ValueAreaHigh, from, to);
                    levels.Add($"val:{key}", $"VAL {date}", profile.ValueAreaLow, from, to);
                }
                if (ShowBuyerSellerPoc)
                {
                    if (profile.HasBuyerPoc) levels.Add($"bpoc:{key}", $"Buyer POC {date}", profile.BuyerPoc, from, to);
                    if (profile.HasSellerPoc) levels.Add($"spoc:{key}", $"Seller POC {date}", profile.SellerPoc, from, to);
                }
                if (ShowOuterValueArea)
                {
                    levels.Add($"ovah:{key}", $"Outer VAH {date}", profile.OuterValueAreaHigh, from, to);
                    levels.Add($"oval:{key}", $"Outer VAL {date}", profile.OuterValueAreaLow, from, to);
                }
            }
            foreach (var poc in _pocs)
            {
                bool tested = poc.TouchBar >= 0;
                if (tested ? !ShowTestedPocs : !ShowUntestedPocs) continue;
                // the projection's source is one of the displayed profiles: name and key it by that profile's first bar
                int start = poc.SourceEndBar;
                foreach (var profile in _display)
                    if (profile.EndBar == poc.SourceEndBar) { start = profile.StartBar; break; }
                var source = GetCandle(start);
                string date = StudyAlertNames.Day(source.SessionDate);
                levels.Add($"npoc:{StudyAlertNames.Key(source)}", tested ? $"Tested POC {date}" : $"Naked POC {date}", poc.Price, poc.SourceEndBar, tested ? poc.TouchBar : int.MaxValue);
            }
        }
        return levels;
    }
}
