namespace TradingHub.Indicators.Native;

public partial class VwapSuite
{
    /// <summary>Only the prior/closing part draws levels by hand; a part that is off is neither calculated nor drawn.</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        if (!_prior) return Array.Empty<IndicatorLevel>();
        return PriorClosing.GetAlertLevels().Select(level => level with { Id = "prior." + level.Id });
    }
}
