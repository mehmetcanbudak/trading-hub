// One VWAP entry selecting variants. Behaviour reference: gouqizi2214730-source/trading-hub
// @ 0d647bdbb1a2229de8c416438d5251b95e1358fe (MIT with Commons Clause); hosts our existing VWAP studies as parts.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native;

/// <summary>
/// One library entry for the VWAP family. Each part is the existing study (same calculation, provenance labels and
/// series identities), switched on separately and configured in its own group; parts that are off are not calculated.
/// The individual studies stay in the library so saved charts keep loading.
/// </summary>
[DisplayName("VWAP suite")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("One VWAP with selectable parts: anchored VWAP with up to three bands, bar-based Claire rolling/anchored VWAPs, simultaneous session/week/month/quarter/year VWAPs, prior and closing levels, rolling window and local-time session window. Each part keeps its own coverage labels and settings.")]
public class VwapSuite : Indicator
{
    private readonly (Indicator Part, Func<bool> Enabled)[] _parts;
    private readonly Dictionary<IDataSeries, bool> _originallyHidden = new();
    private bool _anchored = true, _claire, _multi, _prior, _rolling, _window;

    public VwapSuite() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        PriorClosing.DrawLegend = false;
        _parts = new (Indicator, Func<bool>)[]
        {
            (Anchored, () => _anchored), (Claire, () => _claire), (MultiPeriod, () => _multi),
            (PriorClosing, () => _prior), (Rolling, () => _rolling), (SessionWindow, () => _window),
        };
        foreach (var (part, _) in _parts)
        {
            Add(part);
            // Each part's lines are lines of this indicator: they render, keep their styles and save with it.
            foreach (var series in part.DataSeries)
            {
                if (DataSeries.Any(existing => existing.Id == series.Id))
                    throw new InvalidOperationException($"Duplicate VWAP suite series identity {series.Id}.");
                _originallyHidden[series] = series.IsHidden;
                DataSeries.Add(series);
            }
        }
        ApplyPartVisibility();
    }

    [Display(Name = "Anchored VWAP with bands", GroupName = "Parts", Order = 1)]
    public bool UseAnchored { get => _anchored; set { _anchored = value; PartChanged(); } }

    [Display(Name = "VWAP Claire (bar-based 30 / 90 / 365 days)", GroupName = "Parts", Order = 2)]
    public bool UseClaire { get => _claire; set { _claire = value; PartChanged(); } }

    [Display(Name = "Multi-period VWAP", GroupName = "Parts", Order = 3)]
    public bool UseMultiPeriod { get => _multi; set { _multi = value; PartChanged(); } }

    [Display(Name = "Prior and closing VWAP levels", GroupName = "Parts", Order = 4)]
    public bool UsePriorClosing { get => _prior; set { _prior = value; PartChanged(); } }

    [Display(Name = "Rolling VWAP", GroupName = "Parts", Order = 5)]
    public bool UseRolling { get => _rolling; set { _rolling = value; PartChanged(); } }

    [Display(Name = "Session-window VWAP", GroupName = "Parts", Order = 6)]
    public bool UseSessionWindow { get => _window; set { _window = value; PartChanged(); } }

    [Display(Name = "Anchored VWAP settings", GroupName = "Part settings", Order = 10)]
    public SessionVwap Anchored { get; } = new();

    [Display(Name = "VWAP Claire settings", GroupName = "Part settings", Order = 20)]
    public VwapClaire Claire { get; } = new();

    [Display(Name = "Multi-period VWAP settings", GroupName = "Part settings", Order = 30)]
    public MultiPeriodVwap MultiPeriod { get; } = new();

    [Display(Name = "Prior and closing VWAP settings", GroupName = "Part settings", Order = 40)]
    public PriorClosingVwap PriorClosing { get; } = new();

    [Display(Name = "Rolling VWAP settings", GroupName = "Part settings", Order = 50)]
    public RollingVwap Rolling { get; } = new();

    [Display(Name = "Session-window VWAP settings", GroupName = "Part settings", Order = 60)]
    public TimedSessionVwap SessionWindow { get; } = new();

    [Browsable(false)]
    public string Status => string.Join("; ", Segments().Select(s => s.Part + ": " + s.Status));

    private IEnumerable<(string Part, string Status)> Segments() => _parts.Where(p => p.Enabled()).Select(p => p.Part switch
    {
        SessionVwap a => ("Anchored", a.Status),
        VwapClaire c => ("Claire", c.Status),
        MultiPeriodVwap m => ("Multi-period", m.Status),
        PriorClosingVwap prior => ("Prior", prior.Status),
        RollingVwap r => ("Rolling", r.Status),
        TimedSessionVwap t => ("Session window", t.Status),
        _ => ("", ""),
    });

    private void PartChanged()
    {
        ApplyPartVisibility();
        RecalculateValues();
    }

    private void ApplyPartVisibility()
    {
        foreach (var (part, enabled) in _parts)
            foreach (var series in part.DataSeries)
                series.IsHidden = _originallyHidden[series] || !enabled();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        foreach (var (part, enabled) in _parts)
            if (enabled()) part.Calculate(bar, value);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        // Each part's own status is a catalog entry; translate segment by segment before joining.
        string status = string.Join("; ", Segments().Select(s => UiText.Tr(s.Part) + ": " + UiText.Tr(s.Status)));
        if (status.Length > 0) FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, UiText.Tr("VWAP suite") + ": " + status, Color.LightSkyBlue);
        // Only the prior/closing part draws chart geometry beyond its series.
        if (_prior) RenderChild(PriorClosing, context, layout);
    }

    protected override void OnDispose()
    {
        foreach (var (part, _) in _parts) part.Dispose();
        base.OnDispose();
    }
}
