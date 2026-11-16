namespace TradingHub.Indicators.Native;

public partial class RefillZones
{
    /// <summary>Most recent ended (mitigated or expired) zones offered for alerts; every live zone is offered.</summary>
    private const int AlertEndedZones = 20;

    /// <summary>
    /// The drawn midline of each zone: live while it projects unmitigated, up to its mitigation or expiry once ended (mitigated
    /// zones only while they are kept), or just its own bar when zones are not projected.
    /// </summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var levels = new StudyLevelList(CurrentBar);
        if (CurrentBar == 0) return levels;
        lock (_sync)
        {
            int ended = 0;
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                var zone = _zones[i];
                if (zone.Bar > _last || zone.MitigatedBar >= 0 && !ShowMitigated) continue;
                bool live = _project && zone.MitigatedBar < 0 && zone.ExpiredBar < 0;
                if (!live && ++ended > AlertEndedZones) continue;
                int end = !_project ? zone.Bar : zone.MitigatedBar >= 0 ? zone.MitigatedBar : zone.ExpiredBar >= 0 ? zone.ExpiredBar : int.MaxValue;
                if (_lifetime > 0) end = Math.Min(end, zone.Bar + _lifetime);
                // zones created on one bar are told apart by their order on that bar
                int index = 0;
                for (int j = i - 1; j >= 0 && _zones[j].Bar == zone.Bar; j--) index++;
                var source = GetCandle(zone.Bar);
                string time = StudyAlertNames.Minute(source.Time);
                string name = zone.Direction switch { ZoneDirection.Buyer => $"Buyer refill zone {time}", ZoneDirection.Seller => $"Seller refill zone {time}", _ => $"Neutral refill zone {time}" };
                levels.Add(string.Concat("refill:", StudyAlertNames.Key(source), ":", index.ToString(System.Globalization.CultureInfo.InvariantCulture)), name, zone.Low / 2 + zone.High / 2, zone.Bar, end);
            }
        }
        return levels;
    }
}
