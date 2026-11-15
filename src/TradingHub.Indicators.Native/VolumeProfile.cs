using System.ComponentModel;

namespace TradingHub.Indicators.Native;

/// <summary>
/// The single library entry for period volume profiles: session, week, month, quarter, year or all loaded history, sized
/// by ticks per row or price grouping, drawn as volume, delta or bid/ask, and merged head-to-tail (manually from the chart's
/// right-click menu or automatically by POC or overlap). Uses the <see cref="CompositeVolumeProfile"/> engine; the older
/// "Session volume profile" and "Composite Volume Profile" entries stay available for saved charts.
/// </summary>
[DisplayName("Volume profile")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("One volume profile for session, week, month, quarter, year or all history: ticks per row or price grouping, volume/delta/bid-ask view, untested POCs, and head-to-tail merging from the chart's right-click menu or by overlap. Observed and measured rows only unless estimates are enabled.")]
public class VolumeProfile : CompositeVolumeProfile
{
    public VolumeProfile()
    {
        ComposeProfiles = false;
        RowMode = RowSizing.TicksPerRow;
        TicksPerRow = 1;
    }
}
