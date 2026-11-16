namespace TradingHub.Indicators.Native;

public partial class MarketStructure
{
    /// <summary>Most recent swings per side offered for alerts.</summary>
    private const int AlertSwings = 10;

    /// <summary>
    /// The edges of each drawn zone (order block, breaker, FVG, equal-high/low liquidity), live until it ends; and, with swings
    /// shown, the latest swing highs and lows from the pivot until the BOS/CHoCH that breaks them.
    /// </summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var levels = new StudyLevelList(CurrentBar);
        if (CurrentBar == 0) return levels;
        int last = CurrentBar - 1;
        foreach (var zone in _zones)
        {
            bool enabled = zone.Kind switch { StructureZoneKind.OrderBlock => ShowOrderBlocks, StructureZoneKind.Breaker => ShowBreakers, StructureZoneKind.FairValueGap => ShowFvg, _ => ShowLiquidity };
            bool ended = zone.EndAvailableBar >= 0 && zone.EndAvailableBar <= last;
            if (!enabled || ended && !ShowEndedZones || zone.AvailableBar > last) continue;
            var source = GetCandle(zone.SourceBar);
            string id = string.Concat(zone.Kind.ToString(), ":", zone.Direction.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", StudyAlertNames.Key(source));
            string time = StudyAlertNames.Minute(source.Time);
            var (highName, lowName) = zone.Kind switch
            {
                StructureZoneKind.OrderBlock => ($"Order block high {time}", $"Order block low {time}"),
                StructureZoneKind.Breaker => ($"Breaker high {time}", $"Breaker low {time}"),
                StructureZoneKind.FairValueGap => ($"FVG high {time}", $"FVG low {time}"),
                _ => ($"Liquidity high {time}", $"Liquidity low {time}"),
            };
            int from = RetrospectiveDrawing ? zone.SourceBar : zone.AvailableBar, to = ended ? zone.EndBar : int.MaxValue;
            levels.Add(id + ".high", highName, zone.High, from, to);
            levels.Add(id + ".low", lowName, zone.Low, from, to);
        }
        if (!ShowSwings) return levels;
        int highs = 0, lows = 0;
        for (int i = _events.Count - 1; i >= 0 && (highs < AlertSwings || lows < AlertSwings); i--)
        {
            var swing = _events[i];
            bool high = swing.Kind == StructureEventKind.SwingHigh;
            if (!high && swing.Kind != StructureEventKind.SwingLow || swing.AvailableBar > last || (high ? highs++ : lows++) >= AlertSwings) continue;
            // a swing high is broken upwards (direction +1), a swing low downwards, by the first BOS/CHoCH that names its pivot
            int broken = int.MaxValue;
            for (int j = i + 1; j < _events.Count; j++)
            {
                var item = _events[j];
                if (item.Kind is StructureEventKind.Bos or StructureEventKind.Choch && item.RelatedBar == swing.SourceBar && item.Direction == (high ? 1 : -1) && item.AvailableBar <= last)
                { broken = item.SourceBar; break; }
            }
            var source = GetCandle(swing.SourceBar);
            string swingTime = StudyAlertNames.Minute(source.Time);
            levels.Add(string.Concat(high ? "swing.high:" : "swing.low:", StudyAlertNames.Key(source)), high ? $"Swing high {swingTime}" : $"Swing low {swingTime}",
                swing.Price, RetrospectiveDrawing ? swing.SourceBar : swing.AvailableBar, broken);
        }
        return levels;
    }
}
