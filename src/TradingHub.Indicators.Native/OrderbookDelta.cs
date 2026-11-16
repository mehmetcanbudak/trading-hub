using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native;

// Adapted from KVP Trading Hub 50220ba91613161685f84384825d855cb4dbeb76.
// Exact received L2 snapshots replace its sampled floats and stale-value carry-forward.
[DisplayName("Orderbook Delta (resting liquidity)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Recorded resting L2 bid/ask size, not traded volume. Missing or stale books remain gaps. Sample and duration averages have different, explicit semantics; partial provider books are labelled.")]
public sealed class OrderbookDelta : Indicator
{
    public enum BookView
    {
        [Display(Name = "Book delta (bid minus ask)")] Delta,
        [Display(Name = "Imbalance (%)")] Imbalance,
        [Display(Name = "Bid and ask separately")] BidAsk,
        [Display(Name = "Book delta change from previous bar")] Change,
    }

    /// <summary>How far the book is summed: ticks from each side's best price, or a mid-relative percentage band.</summary>
    public enum DepthRange
    {
        [Display(Name = "Ticks from each side's best price")] Ticks,
        [Display(Name = "0 – 0.5 % of mid")] Percent0_5,
        [Display(Name = "0 – 1 % of mid")] Percent1,
        [Display(Name = "0 – 2.5 % of mid")] Percent2_5,
        [Display(Name = "0 – 5 % of mid")] Percent5,
        [Display(Name = "0 – 10 % of mid")] Percent10,
        [Display(Name = "0 – 25 % of mid")] Percent25,
    }

    private readonly OptionalValueDataSeries _main = new("Delta", "Book delta") { Width = 2, VisualType = VisualMode.Histogram };
    private readonly OptionalValueDataSeries _bid = new("Bid", "Resting bid size") { Width = 2, Color = Color.FromArgb(60, 200, 120) };
    private readonly OptionalValueDataSeries _ask = new("Ask", "Resting ask size") { Width = 2, Color = Color.FromArgb(240, 80, 80) };
    private DepthMeasurement?[] _measurements = [];
    private BookView _view;
    private DepthAggregation _aggregation = DepthAggregation.SampleAverage;
    private int _within = 20, _maxAge = 10;
    private DepthRange _range;
    private bool _allowPartial = true;

    public OrderbookDelta() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _main; DataSeries.Add(_bid); DataSeries.Add(_ask);
        LineSeries.Add(new LineSeries("Zero", "Zero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150) });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "View", GroupName = "Display", Order = 10)]
    public BookView View { get => _view; set { _view = value; RecalculateValues(); } }

    [Display(Name = "Book range", GroupName = "Calculation", Order = 15)]
    [Description("Percentage bands are measured from the mid price; when the provider book is shallower than the band, only the recorded levels are summed and the status says so.")]
    public DepthRange Range { get => _range; set { _range = value; RecalculateValues(); } }

    /// <summary>Band percentage for <see cref="Range"/>; zero means tick distance.</summary>
    public static decimal PercentOf(DepthRange range) => range switch
    {
        DepthRange.Percent0_5 => 0.5m,
        DepthRange.Percent1 => 1m,
        DepthRange.Percent2_5 => 2.5m,
        DepthRange.Percent5 => 5m,
        DepthRange.Percent10 => 10m,
        DepthRange.Percent25 => 25m,
        _ => 0m,
    };

    [Display(Name = "Ticks from each side's best price (inclusive)", GroupName = "Calculation", Order = 20)]
    [Range(0, 10_000)]
    public int WithinTicks { get => _within; set { _within = Math.Clamp(value, 0, 10_000); RecalculateValues(); } }

    [Display(Name = "Aggregation", GroupName = "Calculation", Order = 30)]
    [Description("SampleAverage weights received snapshots equally. DurationAverage weights only covered time until the next update, gap or stale timeout. Last needs a fresh terminal snapshot. Maximum takes each side's independent maximum.")]
    public DepthAggregation Aggregation { get => _aggregation; set { _aggregation = value; RecalculateValues(); } }

    [Display(Name = "Stale timeout (seconds)", GroupName = "Calculation", Order = 31)]
    [Range(1, 300)]
    public int MaxAgeSeconds { get => _maxAge; set { _maxAge = Math.Clamp(value, 1, 300); RecalculateValues(); } }

    [Display(Name = "Allow labelled partial provider books", GroupName = "Calculation", Order = 32)]
    public bool AllowPartialBook { get => _allowPartial; set { _allowPartial = value; RecalculateValues(); } }

    [Display(Name = "Positive color", GroupName = "Display", Order = 40)]
    public Color PositiveColor { get; set; } = Color.FromArgb(60, 200, 120);
    [Display(Name = "Negative color", GroupName = "Display", Order = 41)]
    public Color NegativeColor { get; set; } = Color.FromArgb(240, 80, 80);

    [Browsable(false)] public bool IsAvailable { get; private set; }
    [Browsable(false)] public StudyCoverage Coverage { get; private set; }
    [Browsable(false)] public string Status { get; private set; } = "No recorded depth. Recording must be explicitly enabled; display does not enable it.";
    public DepthMeasurement? GetMeasurement(int bar) => bar >= 0 && bar < _measurements.Length ? _measurements[bar] : null;

    protected override void OnRecalculate()
    {
        Array.Clear(_measurements);
        _main.VisualType = _view == BookView.BidAsk ? VisualMode.Hide : _view == BookView.Imbalance ? VisualMode.Line : VisualMode.Histogram;
        _main.Name = _view == BookView.Imbalance ? "Book imbalance %" : _view == BookView.Change ? "Book delta change" : "Book delta";
        _bid.VisualType = _ask.VisualType = _view == BookView.BidAsk ? VisualMode.Line : VisualMode.Hide;
        IsAvailable = false; Coverage = StudyCoverage.Missing;
    }

    protected override void OnDepthSnapshot(DepthUpdate update) => RecalculateValues();
    protected override void OnDepthHistoryChanged() => RecalculateValues();

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar >= _measurements.Length) Array.Resize(ref _measurements, Math.Max(bar + 1, Math.Max(16, _measurements.Length * 2)));
        var query = GetDepthHistory(bar);
        decimal percent = PercentOf(_range);
        var measurement = DepthStatistics.Measure(query, _within, _aggregation, TimeSpan.FromSeconds(_maxAge), _allowPartial, percent);
        _measurements[bar] = measurement;
        bool available = measurement.IsAvailable;
        decimal result = _view == BookView.Imbalance ? measurement.Imbalance : measurement.Delta;
        string? reason = measurement.Message;
        if (_view == BookView.Change && available)
        {
            if (bar == 0) { available = false; reason = "Book change requires an available preceding bar from the same source."; }
            else
            {
                var previousQuery = GetDepthHistory(bar - 1);
                var previous = DepthStatistics.Measure(previousQuery, _within, _aggregation, TimeSpan.FromSeconds(_maxAge), _allowPartial, percent);
                var currentSource = LastSource(query);
                var previousSource = LastSource(previousQuery);
                if (!previous.IsAvailable || currentSource == null || currentSource != previousSource ||
                    query.Samples.Any(s => s.GapBefore != DepthGapReason.None || !s.IsUsable) ||
                    ((measurement.Coverage | previous.Coverage) & (DepthCoverage.SequenceGap | DepthCoverage.Missing)) != 0 ||
                    previousQuery.Samples.Any(s => s.GapBefore != DepthGapReason.None || !s.IsUsable))
                {
                    available = false;
                    reason = "Book change is unavailable across missing/stale intervals, provider changes or sequence gaps.";
                }
                else result = measurement.Delta - previous.Delta;
            }
        }
        _main.Set(bar, result, available);
        _bid.Set(bar, measurement.Bid, measurement.IsAvailable);
        _ask.Set(bar, measurement.Ask, measurement.IsAvailable);
        _main.Colors[bar] = result >= 0 ? PositiveColor : NegativeColor;
        if (bar == CurrentBar - 1)
        {
            IsAvailable = available;
            Coverage = available ? StudyCoverage.Observed : StudyCoverage.Missing;
            if ((measurement.Coverage & (DepthCoverage.PartialBook | DepthCoverage.Truncated | DepthCoverage.SequenceGap | DepthCoverage.Missing)) != 0)
                Coverage |= StudyCoverage.Incomplete;
            string range = percent > 0 ? $"0–{percent:0.##}% of mid" : $"{_within} ticks";
            Status = $"Resting L2 · {range} · {_aggregation} · {measurement.Samples} snapshots · {measurement.CoveredDuration.TotalSeconds:0.###} covered seconds";
            if ((measurement.Coverage & (DepthCoverage.PartialBook | DepthCoverage.Truncated)) != 0) Status += " · partial recorded depth";
            if (percent > 0 && BandExceedsBook(query, percent)) Status += " · band is deeper than the recorded levels; only recorded levels are summed";
            if (reason != null) Status += " · " + reason;
            var source = LastSource(query);
            if (source != null) Status += " · " + source;
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var region = Container?.Region ?? Rectangle.Empty;
        FootprintStudyData.Legend(context, region, 0, Status, IsAvailable ? Color.LightGray : Color.Gold);
    }

    private static string? LastSource(DepthHistoryQuery query)
    {
        var sample = query.Samples.LastOrDefault(s => s.IsUsable) ?? query.Preceding;
        return sample?.IsUsable == true ? sample.ProviderId + "/" + sample.Venue : null;
    }

    /// <summary>True when the latest usable book does not reach the band edge on either side (a shallow provider book).</summary>
    public static bool BandExceedsBook(DepthHistoryQuery query, decimal percent)
    {
        var sample = query.Samples.LastOrDefault(s => s.IsUsable) ?? query.Preceding;
        if (sample?.IsUsable != true) return false;
        var book = sample.Snapshot;
        decimal mid = ((decimal)book.BestBidTicks + book.BestAskTicks) / 2, distance = Math.Abs(mid) * percent / 100;
        return book.Bids[^1].PriceTicks > mid - distance || book.Asks[^1].PriceTicks < mid + distance;
    }
}
