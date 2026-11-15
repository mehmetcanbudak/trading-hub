// Behavior adapted from the KVP capability reference (commits e43b23b, 239bdb8); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Market;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Cumulative delta of each market of an aggregate instrument (or grouped by exchange, or spot versus perpetual) next to
/// the aggregate CVD colored by sign. Each print is attributed by its recorded source venue. Delta that cannot be
/// attributed — candle-derived history, prints without a known constituent — is kept in its own unattributed line,
/// never assigned to a venue.
/// </summary>
[DisplayName("CVD by market")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("One cumulative delta line per market (Binance Futures, Bybit, OKX…), per exchange, or spot versus perpetual, plus the aggregate CVD colored by sign. Use with an aggregate crypto connection; candle-derived delta stays unattributed.")]
public sealed class CvdByMarket : Indicator
{
    public enum CvdReset
    {
        [Display(Name = "Never")] Never,
        [Display(Name = "Each session")] Session,
        [Display(Name = "Each week")] Week,
        [Display(Name = "Each month")] Month,
    }

    public enum CvdGrouping
    {
        [Display(Name = "Each market")] Market,
        [Display(Name = "Each exchange (spot and perpetual together)")] Exchange,
        [Display(Name = "Spot versus perpetual")] SpotVersusPerpetual,
    }

    /// <summary>Maximum number of lines; further groups share the last line.</summary>
    public const int MaximumLanes = 16;

    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 242, 181, 58), Color.FromArgb(255, 86, 180, 255), Color.FromArgb(255, 180, 120, 255), Color.FromArgb(255, 38, 194, 129),
        Color.FromArgb(255, 255, 120, 160), Color.FromArgb(255, 120, 220, 220), Color.FromArgb(255, 200, 200, 90), Color.FromArgb(255, 255, 150, 90),
        Color.FromArgb(255, 150, 160, 255), Color.FromArgb(255, 110, 200, 120), Color.FromArgb(255, 230, 110, 230), Color.FromArgb(255, 170, 140, 110),
        Color.FromArgb(255, 90, 140, 200), Color.FromArgb(255, 200, 90, 90), Color.FromArgb(255, 160, 220, 90), Color.FromArgb(255, 210, 170, 220),
    };

    private readonly ValueDataSeries _aggregate = new("AGG", "Aggregate CVD") { Color = Color.FromArgb(255, 220, 220, 220), Width = 2, VisualType = VisualMode.Histogram };
    private readonly ValueDataSeries[] _lanes = new ValueDataSeries[MaximumLanes];
    private readonly ValueDataSeries _unattributed = new("UNATTRIBUTED", "Unattributed CVD") { Color = Color.FromArgb(200, 150, 150, 150), Width = 1, VisualType = VisualMode.Hide };
    private readonly List<decimal[]> _rows = new();
    private readonly List<BarTrade> _trades = new();
    private decimal[] _barDelta = Array.Empty<decimal>();
    private readonly RenderFont _statusFont = new("Arial", 9);
    private Dictionary<string, int> _laneOf = new(StringComparer.Ordinal);
    private int _laneCount;
    private CvdReset _reset = CvdReset.Never;
    private CvdGrouping _grouping = CvdGrouping.Market;
    private bool _histogram = true, _showAggregate = true;

    public CvdByMarket() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _aggregate;
        for (int i = 0; i < MaximumLanes; i++)
        {
            _lanes[i] = new ValueDataSeries("M" + i, "Market " + (i + 1)) { Color = Palette[i], Width = 1, ShowZeroValue = true, VisualType = VisualMode.Hide };
            DataSeries.Add(_lanes[i]);
        }
        DataSeries.Add(_unattributed);
        LineSeries.Add(new LineSeries("Zero", "Zero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Reset", GroupName = "Calculation", Order = 10)]
    public CvdReset Reset { get => _reset; set { _reset = value; RecalculateValues(); } }

    [Display(Name = "One line per", GroupName = "Calculation", Order = 15)]
    public CvdGrouping Grouping { get => _grouping; set { _grouping = value; RecalculateValues(); } }

    [Display(Name = "Aggregate as sign-colored histogram (otherwise line)", GroupName = "Visualization", Order = 20)]
    public bool AggregateHistogram { get => _histogram; set { _histogram = value; RecalculateValues(); } }

    [Display(Name = "Show the aggregate", GroupName = "Visualization", Order = 25)]
    public bool ShowAggregate { get => _showAggregate; set { _showAggregate = value; RecalculateValues(); } }

    [Display(Name = "Positive color", GroupName = "Visualization", Order = 30)]
    public Color PositiveColor { get; set; } = Color.FromArgb(200, 38, 194, 129);

    [Display(Name = "Negative color", GroupName = "Visualization", Order = 31)]
    public Color NegativeColor { get; set; } = Color.FromArgb(200, 240, 82, 90);

    /// <summary>Line names in lane order, for the legend and tests.</summary>
    [Browsable(false)]
    public IReadOnlyList<string> LaneNames { get; private set; } = Array.Empty<string>();

    [Browsable(false)]
    public string Status { get; private set; } = "";

    /// <summary>Which line each source goes to and the line names, for a grouping. Sources beyond the limit share the last line.</summary>
    public static (Dictionary<string, int> LaneOf, List<string> Names) Lanes(IReadOnlyList<InstrumentSource> sources, CvdGrouping grouping)
    {
        var laneOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var source in sources)
        {
            string name = grouping switch
            {
                CvdGrouping.Exchange => source.Venue,
                CvdGrouping.SpotVersusPerpetual => source.IsSpot ? "Spot" : "Perpetual",
                _ => source.Name,
            };
            int lane = names.IndexOf(name);
            if (lane < 0)
            {
                if (names.Count < MaximumLanes)
                {
                    lane = names.Count;
                    names.Add(name);
                }
                else
                {
                    lane = MaximumLanes - 1;
                    names[lane] = "Other markets";
                }
            }
            laneOf[source.Identity] = lane;
        }
        return (laneOf, names);
    }

    protected override void OnRecalculate()
    {
        _rows.Clear();
        var sources = InstrumentInfo?.Sources ?? Array.Empty<InstrumentSource>();
        var (laneOf, names) = Lanes(sources, _grouping);
        _laneOf = laneOf;
        _laneCount = names.Count;
        LaneNames = names;
        bool multi = sources.Count > 1;
        for (int i = 0; i < MaximumLanes; i++)
        {
            _lanes[i].Name = i < names.Count ? "CVD " + names[i] : "Market " + (i + 1);
            _lanes[i].VisualType = multi && i < names.Count ? VisualMode.Line : VisualMode.Hide;
        }
        _unattributed.VisualType = VisualMode.Hide;
        _aggregate.VisualType = !_showAggregate ? VisualMode.Hide : _histogram ? VisualMode.Histogram : VisualMode.Line;
    }

    private bool NewPeriod(int bar) => _reset switch
    {
        CvdReset.Session => IsNewSession(bar),
        CvdReset.Week => IsNewWeek(bar),
        CvdReset.Month => IsNewMonth(bar),
        _ => false,
    };

    /// <summary>Per-lane delta of a bar into <see cref="_barDelta"/>; index <c>_laneCount</c> holds the unattributed remainder.</summary>
    private void BarDelta(int bar)
    {
        if (_barDelta.Length != _laneCount + 1) _barDelta = new decimal[_laneCount + 1];
        else Array.Clear(_barDelta);
        decimal attributed = 0;
        GetBarTrades(bar, _trades);
        foreach (var trade in _trades)
        {
            if (trade.Side is not (TradeSide.Buy or TradeSide.Sell)) continue;
            if (!trade.Provenance.IsMeasured || trade.Provenance.SourceIdentity is not { } identity || !_laneOf.TryGetValue(identity, out int lane)) continue;
            decimal signed = trade.Side == TradeSide.Buy ? trade.Volume : -trade.Volume;
            _barDelta[lane] += signed;
            attributed += signed;
        }
        _barDelta[_laneCount] = GetCandle(bar).Delta - attributed;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        BarDelta(bar);
        bool continues = bar > 0 && bar - 1 < _rows.Count && !NewPeriod(bar);
        // Row layout: lanes, unattributed, aggregate. The forming bar's row is rewritten in place.
        var row = bar < _rows.Count && _rows[bar].Length == _laneCount + 2 ? _rows[bar] : new decimal[_laneCount + 2];
        for (int i = 0; i <= _laneCount; i++) row[i] = (continues ? _rows[bar - 1][i] : 0) + _barDelta[i];
        row[_laneCount + 1] = (continues ? _rows[bar - 1][_laneCount + 1] : 0) + GetCandle(bar).Delta;
        while (_rows.Count < bar) _rows.Add(new decimal[_laneCount + 2]);
        if (_rows.Count == bar) _rows.Add(row);
        else _rows[bar] = row;

        for (int i = 0; i < _laneCount; i++) _lanes[i][bar] = row[i];
        _unattributed[bar] = row[_laneCount];
        decimal aggregate = row[_laneCount + 1];
        _aggregate[bar] = aggregate;
        _aggregate.Colors[bar] = aggregate >= 0 ? PositiveColor : NegativeColor;
        if (row[_laneCount] != 0 && (InstrumentInfo?.Sources.Count ?? 0) > 1) _unattributed.VisualType = VisualMode.Line;
        if (bar == CurrentBar - 1)
            Status = (InstrumentInfo?.Sources.Count ?? 0) > 1
                ? row[_laneCount] == 0 ? "" : $"Unattributed delta (candle history or unknown venue): {row[_laneCount]:0.####}"
                : "Single-market instrument: per-market lines need an aggregate connection.";
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var region = Container?.Region ?? Rectangle.Empty;
        if (Status.Length == 0 || region.Width <= 16 || region.Height < 40) return;
        context.DrawString(Status, _statusFont, Color.FromArgb(200, 180, 186, 196), new Rectangle(region.X + 8, region.Y + 20, region.Width - 16, 18));
    }
}
