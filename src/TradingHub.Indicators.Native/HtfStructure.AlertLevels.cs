using System.Globalization;

namespace TradingHub.Indicators.Native;

public partial class HtfStructure
{
    /// <summary>
    /// The edges of every drawn active box: higher-period FVGs, PO3 ranges with their T-Spot C3/C4 zones, CISD range-multiple
    /// projections and breakers, each live from its availability until the study stops drawing it.
    /// </summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var levels = new StudyLevelList(CurrentBar);
        if (CurrentBar == 0) return levels;
        int last = CurrentBar - 1;
        if (ShowFvg)
            foreach (var gap in _gaps)
            {
                if (!gap.IsActiveAt(last)) continue;
                var source = GetCandle(gap.SourceBar);
                string id = string.Concat("fvg:", gap.Period.ToString(), ":", gap.Direction.ToString(CultureInfo.InvariantCulture), ":", StudyAlertNames.Key(source));
                string time = StudyAlertNames.Minute(source.Time);
                levels.Add(id + ".high", $"HTF FVG high {time}", gap.High, gap.AvailableBar);
                levels.Add(id + ".low", $"HTF FVG low {time}", gap.Low, gap.AvailableBar);
            }
        foreach (var pattern in _patterns)
        {
            if (!pattern.IsActiveAt(last)) continue;
            var source = GetCandle(pattern.C1StartBar);
            string id = string.Concat("po3:", pattern.Direction.ToString(CultureInfo.InvariantCulture), ":", StudyAlertNames.Key(source));
            string time = StudyAlertNames.Minute(source.Time);
            if (ShowPo3)
            {
                levels.Add(id + ".high", $"PO3 range high {time}", pattern.RangeHigh, pattern.AvailableBar);
                levels.Add(id + ".low", $"PO3 range low {time}", pattern.RangeLow, pattern.AvailableBar);
                levels.Add(id + ".c3.high", $"T-Spot C3 high {time}", pattern.TSpot3High, pattern.AvailableBar);
                levels.Add(id + ".c3.low", $"T-Spot C3 low {time}", pattern.TSpot3Low, pattern.AvailableBar);
                if (pattern.TSpot4AvailableBar >= 0 && pattern.TSpot4AvailableBar <= last && pattern.TSpot4High is { } c4High && pattern.TSpot4Low is { } c4Low)
                {
                    levels.Add(id + ".c4.high", $"T-Spot C4 high {time}", c4High, pattern.TSpot4AvailableBar);
                    levels.Add(id + ".c4.low", $"T-Spot C4 low {time}", c4Low, pattern.TSpot4AvailableBar);
                }
            }
            if (ShowProjections && pattern.CisdBar >= 0 && pattern.ProjectionAvailableBar <= last)
                foreach (decimal multiple in ProjectionMultiples)
                {
                    string r = multiple.ToString(CultureInfo.InvariantCulture);
                    levels.Add(id + ".cisd." + r, $"CISD projection {r}R {time}", pattern.CisdLevel + multiple * (pattern.CisdLevel - pattern.Extreme), pattern.ProjectionAvailableBar);
                }
        }
        if (ShowBreakers)
            foreach (var breaker in _lower.Zones)
            {
                if (breaker.Kind != StructureZoneKind.Breaker || !breaker.IsActiveAt(last)) continue;
                var source = GetCandle(breaker.SourceBar);
                string id = string.Concat("breaker:", breaker.Direction.ToString(CultureInfo.InvariantCulture), ":", StudyAlertNames.Key(source));
                string time = StudyAlertNames.Minute(source.Time);
                levels.Add(id + ".high", $"Breaker high {time}", breaker.High, breaker.AvailableBar);
                levels.Add(id + ".low", $"Breaker low {time}", breaker.Low, breaker.AvailableBar);
            }
        return levels;
    }
}
