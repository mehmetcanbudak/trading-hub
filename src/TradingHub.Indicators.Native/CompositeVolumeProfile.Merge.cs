// Head-to-tail profile merging, row sizing and profile views. Behaviour reference: gouqizi2214730-source/trading-hub
// @ 0d647bdbb1a2229de8c416438d5251b95e1358fe (MIT with Commons Clause); implemented on CompositeVolumeProfile's engine.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native;

public partial class CompositeVolumeProfile
{
    public enum ComposeRule
    {
        [Display(Name = "Next POC inside the current value area")] PocInsideValueArea,
        [Display(Name = "Neighbours overlap by at least the minimum percent")] Overlap,
    }

    public enum OverlapBasis
    {
        [Display(Name = "Value area (VAL to VAH)")] ValueArea,
        [Display(Name = "Full range (low to high)")] Range,
    }

    public enum RowSizing
    {
        [Display(Name = "Price grouping")] PriceGrouping,
        [Display(Name = "Ticks per row")] TicksPerRow,
    }

    public enum ProfileView
    {
        [Display(Name = "Volume")] Volume,
        [Display(Name = "Delta")] Delta,
        [Display(Name = "Bid / Ask")] BidAsk,
    }

    public enum MergeKind { None, Automatic, Manual }

    public sealed partial class Profile
    {
        /// <summary>Number of source periods joined head-to-tail into this profile.</summary>
        public int Parts { get; internal set; } = 1;
        public MergeKind Merge { get; internal set; }
        public decimal LowPrice { get; internal set; }
        public decimal HighPrice { get; internal set; }
        internal int ManualGroup = -1;
    }

    private ComposeRule _rule;
    private OverlapBasis _basis;
    private int _overlapPercent = 50, _ticksPerRow = 1;
    private RowSizing _rowSizing;
    private List<(DateTime From, DateTime To)> _mergeRanges = new();
    private (DateTime From, DateTime To)? _mergeHead;

    [Display(Name = "Row sizing", GroupName = "Calculation")]
    public RowSizing RowMode { get => _rowSizing; set { _rowSizing = value; RecalculateValues(); } }

    [Display(Name = "Ticks per row", GroupName = "Calculation"), Range(1, 100000)]
    public int TicksPerRow { get => _ticksPerRow; set { _ticksPerRow = Math.Clamp(value, 1, 100000); RecalculateValues(); } }

    [Display(Name = "Automatic merge rule", GroupName = "Merging")]
    public ComposeRule MergeRule { get => _rule; set { _rule = value; RecalculateValues(); } }

    [Display(Name = "Minimum overlap to merge (%)", GroupName = "Merging"), Range(1, 100)]
    public int OverlapPercent { get => _overlapPercent; set { _overlapPercent = Math.Clamp(value, 1, 100); RecalculateValues(); } }

    [Display(Name = "Overlap measured on", GroupName = "Merging")]
    public OverlapBasis MergeBasis { get => _basis; set { _basis = value; RecalculateValues(); } }

    /// <summary>
    /// Manual head-to-tail merges as UTC period starts, "yyyy-MM-ddTHH:mm:ssZ..yyyy-MM-ddTHH:mm:ssZ" separated by ";".
    /// Every source period starting inside a range becomes one profile. Ranges are timeframe-independent.
    /// </summary>
    [Display(Name = "Manual merges (UTC period start..end; separated by ;)", GroupName = "Merging")]
    public string MergedPeriods
    {
        get => string.Join(";", _mergeRanges.Select(r => Format(r.From) + ".." + Format(r.To)));
        set { _mergeRanges = ParseRanges(value); RecalculateValues(); }
    }

    [Display(Name = "Label merged profiles", GroupName = "Merging")]
    public bool LabelMerged { get; set; } = true;

    [Display(Name = "Profile view", GroupName = "Visualization")]
    public ProfileView View { get; set; }

    [Display(Name = "Buyer color (delta / bid-ask)", GroupName = "Visualization")]
    public Color BuyColor { get; set; } = Color.FromArgb(120, 73, 209, 73);

    [Display(Name = "Seller color (delta / bid-ask)", GroupName = "Visualization")]
    public Color SellColor { get; set; } = Color.FromArgb(120, 232, 69, 72);

    private static string Format(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static List<(DateTime From, DateTime To)> ParseRanges(string? text)
    {
        var parsed = new List<(DateTime From, DateTime To)>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split("..", StringSplitOptions.TrimEntries);
            if (ends.Length != 2 || !TryParseUtc(ends[0], out var from) || !TryParseUtc(ends[1], out var to))
                throw new FormatException($"Invalid merge range \"{part}\"; expected yyyy-MM-ddTHH:mm:ssZ..yyyy-MM-ddTHH:mm:ssZ.");
            parsed.Add(from <= to ? (from, to) : (to, from));
        }
        return Normalize(parsed);
    }

    private static bool TryParseUtc(string text, out DateTime value) => DateTime.TryParse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);

    /// <summary>Sorted, with touching/overlapping ranges joined: a range never splits another merged profile.</summary>
    private static List<(DateTime From, DateTime To)> Normalize(IEnumerable<(DateTime From, DateTime To)> ranges)
    {
        var result = new List<(DateTime From, DateTime To)>();
        foreach (var range in ranges.OrderBy(r => r.From).ThenBy(r => r.To))
        {
            if (result.Count > 0 && range.From <= result[^1].To) result[^1] = (result[^1].From, range.To > result[^1].To ? range.To : result[^1].To);
            else result.Add(range);
        }
        return result;
    }

    private DateTime PeriodStart(int bar) => _period == ProfilePeriod.AllHistory ? GetCandle(0).Time : StudyMath.PeriodBounds(GetCandle(bar), ProfileFrame).Start;

    private int ManualGroupOf(Profile raw)
    {
        if (_mergeRanges.Count == 0 || _period == ProfilePeriod.AllHistory) return -1;
        var start = PeriodStart(raw.StartBar);
        for (int i = 0; i < _mergeRanges.Count; i++)
            if (start >= _mergeRanges[i].From && start <= _mergeRanges[i].To) return i;
        return -1;
    }

    private decimal EffectiveGroupFor(IndicatorCandle candle)
    {
        decimal requested = _rowSizing == RowSizing.TicksPerRow ? candle.MarketTickSize * _ticksPerRow : _group;
        return Math.Max(1, decimal.Ceiling(requested / candle.ProfileRowSize)) * candle.ProfileRowSize;
    }

    /// <summary>Part of the smaller of the two ranges that both profiles share (0 to 1).</summary>
    internal static decimal OverlapFraction(Profile a, Profile b, OverlapBasis basis)
    {
        bool va = basis == OverlapBasis.ValueArea;
        decimal aLow = va ? a.ValueAreaLow : a.LowPrice, aHigh = va ? a.ValueAreaHigh : a.HighPrice;
        decimal bLow = va ? b.ValueAreaLow : b.LowPrice, bHigh = va ? b.ValueAreaHigh : b.HighPrice;
        decimal shared = Math.Min(aHigh, bHigh) - Math.Max(aLow, bLow);
        if (shared < 0) return 0;
        decimal smaller = Math.Min(aHigh - aLow, bHigh - bLow);
        // A single-row range touching the other counts as fully inside it.
        return smaller <= 0 ? 1 : shared / smaller;
    }

    private bool ShouldCompose(Profile current, Profile raw) => _rule == ComposeRule.Overlap
        ? OverlapFraction(current, raw, _basis) * 100 >= _overlapPercent
        : raw.Poc >= current.ValueAreaLow && raw.Poc <= current.ValueAreaHigh;

    /// <summary>Head-to-tail join decision. Manual ranges win; automatic composition never crosses a manual block.</summary>
    private MergeKind JoinKind(Profile? current, Profile raw, int group)
    {
        if (current == null) return MergeKind.None;
        if (group >= 0) return current.ManualGroup == group ? MergeKind.Manual : MergeKind.None;
        if (current.ManualGroup >= 0 || !_compose || current.Total == 0 || raw.Total == 0) return MergeKind.None;
        return ShouldCompose(current, raw) ? MergeKind.Automatic : MergeKind.None;
    }

    private static void SetPriceRange(Profile profile, decimal[] sortedPrices)
    {
        profile.LowPrice = sortedPrices.Length == 0 ? 0 : sortedPrices[0];
        profile.HighPrice = sortedPrices.Length == 0 ? 0 : sortedPrices[^1];
    }

    private static void CopyMerge(Profile source, Profile destination)
    {
        destination.Parts = source.Parts; destination.Merge = source.Merge; destination.ManualGroup = source.ManualGroup;
        destination.LowPrice = source.LowPrice; destination.HighPrice = source.HighPrice;
    }

    private void AddMergeRange(DateTime from, DateTime to)
    {
        _mergeRanges = Normalize(_mergeRanges.Append(from <= to ? (from, to) : (to, from)));
        RaiseMergedPeriodsChanged();
    }

    private void RaiseMergedPeriodsChanged()
    {
        RaisePropertyChanged(nameof(MergedPeriods));
        RecalculateValues();
    }

    private (DateTime From, DateTime To) SpanOf(Profile profile)
    {
        // Start of the first and of the last source period inside this (possibly merged) profile.
        var from = PeriodStart(profile.StartBar);
        var to = from;
        foreach (var raw in _raw)
            if (raw.StartBar >= profile.StartBar && raw.StartBar <= profile.EndBar) to = PeriodStart(raw.StartBar);
        return (from, to);
    }

    public override IReadOnlyList<IndicatorMenuItem> GetChartMenuItems(int x, int y)
    {
        if (ChartInfo == null || !CalculateDisplayProfiles || _period == ProfilePeriod.AllHistory) return Array.Empty<IndicatorMenuItem>();
        var items = new List<IndicatorMenuItem>();
        lock (_sync)
        {
            int bar = ChartInfo.PriceChartContainer.GetBarByX(x);
            Profile? target = null;
            for (int i = Math.Max(0, _display.Count - _count); i < _display.Count; i++)
                if (_display[i].StartBar <= bar && bar <= _display[i].EndBar) target = _display[i];
            if (target != null)
            {
                var span = SpanOf(target);
                if (_mergeHead is { } head)
                {
                    items.Add(new("Profiles: merge from the marked start to this profile", () => { _mergeHead = null; AddMergeRange(Min(head.From, span.From), Max(head.To, span.To)); }));
                    items.Add(new("Profiles: cancel the marked merge start", () => _mergeHead = null));
                }
                else items.Add(new("Profiles: mark this profile as the merge start", () => _mergeHead = span));
                if (target.Merge == MergeKind.Manual)
                    items.Add(new("Profiles: split this merged profile", () =>
                    {
                        _mergeRanges = _mergeRanges.Where(r => r.To < span.From || r.From > span.To).ToList();
                        RaiseMergedPeriodsChanged();
                    }));
            }
            items.Add(_compose
                ? new IndicatorMenuItem("Profiles: stop merging neighbours automatically", () => ComposeProfiles = false, true)
                : new IndicatorMenuItem("Profiles: merge neighbours automatically", () => ComposeProfiles = true));
            if (_mergeRanges.Count > 0)
                items.Add(new("Profiles: remove all manual merges", () => { _mergeRanges = new(); RaiseMergedPeriodsChanged(); }));
        }
        return items;
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    /// <summary>Draws one row in the selected view. Delta and bid/ask only use observed or measured sides.</summary>
    private void RenderRow(RenderContext context, Profile profile, decimal price, decimal volume, int x, int top, int height, int maxWidth)
    {
        if (View == ProfileView.Volume || profile.MaximumVolume == 0)
        {
            int width = Math.Max(1, profile.DisplayMaximum == 0 ? 0 : (int)(DisplayVolume(profile, volume) / profile.DisplayMaximum * maxWidth));
            context.FillRectangle(price >= profile.ValueAreaLow && price <= profile.ValueAreaHigh ? ValueAreaColor : ProfileColor, new Rectangle(x, top, width, height));
            return;
        }
        profile.Sides.TryGetValue(price, out var side);
        if (View == ProfileView.Delta)
        {
            decimal delta = side.Ask - side.Bid;
            context.FillRectangle(delta >= 0 ? BuyColor : SellColor, new Rectangle(x, top, Math.Max(1, (int)(Math.Abs(delta) / profile.MaximumVolume * maxWidth)), height));
            return;
        }
        int bid = (int)(side.Bid / profile.MaximumVolume * maxWidth), ask = (int)(side.Ask / profile.MaximumVolume * maxWidth);
        context.FillRectangle(SellColor, new Rectangle(x, top, Math.Max(1, bid), height));
        context.FillRectangle(BuyColor, new Rectangle(x + bid, top, Math.Max(1, ask), height));
    }

    private void RenderMergeLabel(RenderContext context, Profile profile, int x, int y)
    {
        if (!LabelMerged || profile.Parts < 2) return;
        // DrawString translates the whole label through the runtime template catalog.
        string label = profile.Merge == MergeKind.Manual ? $"Merged · {profile.Parts} periods" : $"Merged automatically · {profile.Parts} periods";
        context.DrawString(label, FootprintStudyData.SmallFont, PocColor, x + 3, y);
    }
}
