// Adapted from VP.cs, jjuangutierrez/trading-hub @ 8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9.
// Original contributors retain copyright. MIT with Commons Clause; see LICENSE.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Composite Volume Profile")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Additional session/week/month profiles with optional chronological composition. Observed rows include Between volume; estimated OHLC is optional. Existing Session volume profile is unchanged.")]
public partial class CompositeVolumeProfile : Indicator
{
    public enum ProfilePeriod { Session, Week, Month, Quarter, Year }
    public sealed partial class Profile
    {
        public int StartBar { get; internal set; }
        public int EndBar { get; internal set; }
        internal readonly Dictionary<decimal, decimal> Volumes = new();
        internal int ObservedBars, EstimatedBars, IncompleteBars, MissingBars;
        public IReadOnlyDictionary<decimal, decimal> Levels => Volumes;
        public decimal Total { get; internal set; }
        public decimal Poc { get; internal set; }
        public decimal ValueAreaHigh { get; internal set; }
        public decimal ValueAreaLow { get; internal set; }
        public decimal MaximumVolume { get; internal set; }
        public StudyCoverage Coverage => (ObservedBars > 0 ? StudyCoverage.Observed : 0) | (EstimatedBars > 0 ? StudyCoverage.Estimated : 0) |
            (IncompleteBars > 0 ? StudyCoverage.Incomplete : 0) | (MissingBars > 0 || NonCausalBars > 0 ? StudyCoverage.MissingBars : 0) |
            (CompactBars > 0 ? StudyCoverage.Compact : 0) | (InferredBars > 0 ? StudyCoverage.InferredSide : 0) | (PartialBars > 0 || IsPartialPeriod ? StudyCoverage.Partial : 0);
    }
    public readonly record struct PocProjection(decimal Price, int SourceEndBar, int TouchBar);
    private readonly object _sync = new();
    private readonly List<Profile> _raw = new();
    private List<Profile> _display = new();
    private List<PocProjection> _pocs = new();
    private IReadOnlyList<Profile> _displayView = Array.Empty<Profile>();
    private IReadOnlyList<PocProjection> _pocView = Array.Empty<PocProjection>();
    private readonly RenderPen _pocPen = new(Color.DarkOrange, 2), _projectionPen = new(Color.DarkOrange);
    private readonly List<Profile> _closed = new();
    private readonly Dictionary<Profile, (int Scanned, int Touch)> _touches = new();
    private int _closedRawCount;
    private readonly List<StudyLevel> _levels = new();
    private readonly Dictionary<decimal, decimal> _latest = new();
    private readonly List<(decimal Low, decimal High)> _ranges = new();
    private ProfilePeriod _period;
    private int _last = -1, _count = 10, _untested;
    private decimal _group = 20, _va = 70;
    private decimal _effectiveGroup = 20;
    private bool _estimate, _compose = true;
    internal bool CalculateDisplayProfiles { get; set; } = true;
    private StudyCoverage _latestCoverage;
    public CompositeVolumeProfile() : base(true)
    {
        _displayView = _display.AsReadOnly(); _pocView = _pocs.AsReadOnly();
        DataSeries[0].IsHidden = true; ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true; DrawAbovePrice = false; SubscribeToDrawingEvents(DrawingLayouts.Final);
    }
    [Display(Name = "Host session period", GroupName = "Calculation")]
    public ProfilePeriod Period { get => _period; set { _period = value; RecalculateValues(); } }
    [Display(Name = "Compose when next POC is inside current value area", GroupName = "Calculation")]
    public bool ComposeProfiles { get => _compose; set { _compose = value; RecalculateValues(); } }
    [Display(Name = "Nearest-price grouping (ties away from zero)", GroupName = "Calculation"), Range(0.00000001, 100000000)]
    public decimal PriceGrouping { get => _group; set { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value); _group = value; RecalculateValues(); } }
    [Display(Name = "Value area (%)", GroupName = "Calculation"), Range(1, 100)]
    public decimal ValueAreaPercent { get => _va; set { ArgumentOutOfRangeException.ThrowIfLessThan(value, 1m); ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100m); _va = value; RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC (maximum 200 samples)", GroupName = "Calculation")]
    public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "Profile count", GroupName = "Visualization"), Range(1, 50)]
    public int ProfileCount { get => _count; set { ArgumentOutOfRangeException.ThrowIfLessThan(value, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 50); _count = value; RecalculateValues(); } }
    [Display(Name = "Width (% of source span)", GroupName = "Visualization"), Range(5, 100)] public int WidthPercent { get; set; } = 30;
    [Display(Name = "Project untested POCs (60-profile lookback)", GroupName = "POC")] public bool ShowUntestedPocs { get; set; } = true;
    [Display(Name = "Untested count (0 = all within 60-profile lookback)", GroupName = "POC"), Range(0, 100)]
    public int UntestedPocCount { get => _untested; set { ArgumentOutOfRangeException.ThrowIfNegative(value); _untested = value; RecalculateValues(); } }
    [Display(Name = "Show tested POCs (latest 10 within lookback)", GroupName = "POC")] public bool ShowTestedPocs { get; set; }
    [Display(Name = "Show POC labels", GroupName = "POC")] public bool ShowLabels { get; set; } = true;
    [Display(Name = "Profile color", GroupName = "Visualization")] public Color ProfileColor { get; set; } = Color.FromArgb(70, 70, 130, 190);
    [Display(Name = "Value area color", GroupName = "Visualization")] public Color ValueAreaColor { get; set; } = Color.FromArgb(130, 30, 144, 255);
    [Display(Name = "POC color", GroupName = "Visualization")] public Color PocColor { get; set; } = Color.DarkOrange;
    [Browsable(false)] public IReadOnlyList<Profile> Profiles => _displayView;
    [Browsable(false)] public IReadOnlyList<PocProjection> PocProjections => _pocView;
    [Browsable(false)] public int RawProfileCount => _raw.Count;
    [Browsable(false)] public decimal EffectivePriceGrouping => _effectiveGroup;
    internal IReadOnlyList<Profile> RawProfiles => _raw;
    [Browsable(false)] public string Status => (_display.Count == 0 ? "Missing footprint" : FootprintStudyData.Label(_display[^1].Coverage)) + $"; effective price row {_effectiveGroup}" +
        (_display.Count > 0 && (_display[^1].Coverage & StudyCoverage.Compact) != 0 ? _display[^1].ResolutionSeconds > 0 ? $"; compact time resolution {_display[^1].ResolutionSeconds}s" : "; exchange-packet aggregation (time window unspecified)" : "");
    protected override void OnRecalculate() { lock (_sync) Reset(); }
    private void Reset() { _raw.Clear(); _display.Clear(); _pocs.Clear(); _closed.Clear(); _touches.Clear(); _closedRawCount = 0; _latest.Clear(); _latestSides.Clear(); _ranges.Clear(); _last = -1; }
    private bool Boundary(int bar) => bar > 0 && _period switch
    {
        ProfilePeriod.Session => IsNewSession(bar), ProfilePeriod.Week => IsNewWeek(bar), ProfilePeriod.Month => IsNewMonth(bar),
        ProfilePeriod.Quarter => StudyMath.PeriodKey(GetCandle(bar), StudyTimeFrame.Quarter) != StudyMath.PeriodKey(GetCandle(bar - 1), StudyTimeFrame.Quarter),
        _ => GetCandle(bar).SessionDate.Year != GetCandle(bar - 1).SessionDate.Year
    };
    protected override void OnCalculate(int bar, decimal value)
    {
        lock (_sync)
        {
            if (bar < _last) { Reset(); Clear(); }
            if (bar == _last)
            {
                var profile = _raw[^1];
                foreach (var (price, volume) in _latest)
                {
                    decimal remaining = profile.Volumes[price] - volume;
                    if (remaining == 0) profile.Volumes.Remove(price); else profile.Volumes[price] = remaining;
                    profile.Total -= volume;
                }
                CountCoverage(profile, _latestCoverage, -1);
                CountResolution(profile, _latestResolution, -1);
                if (!_latestCausal) profile.NonCausalBars--;
                UndoSideRows(profile);
                if (CalculateDisplayProfiles) _ranges.RemoveAt(bar); _last--;
                if (profile.StartBar == bar) _raw.RemoveAt(_raw.Count - 1);
            }
            for (int i = _last + 1; i <= bar; i++) Step(i);
        }
    }
    private static void CountCoverage(Profile profile, StudyCoverage coverage, int change)
    {
        if ((coverage & StudyCoverage.Observed) != 0) profile.ObservedBars += change;
        if ((coverage & StudyCoverage.Estimated) != 0) profile.EstimatedBars += change;
        if ((coverage & StudyCoverage.Incomplete) != 0) profile.IncompleteBars += change;
        if (coverage == StudyCoverage.Missing) profile.MissingBars += change;
        CountExtraCoverage(profile, coverage, change);
    }
    private void Step(int bar)
    {
        var candle = GetCandle(bar);
        if (bar == 0) _effectiveGroup = Math.Max(1, decimal.Ceiling(_group / candle.ProfileRowSize)) * candle.ProfileRowSize;
        if (_raw.Count == 0 || Boundary(bar))
        {
            _raw.Add(new Profile { StartBar = bar, IsPartialPeriod = bar == 0 && !IsCalendarPeriodStart(candle) });
            if (!CalculateDisplayProfiles && _raw.Count > 2) _raw.RemoveAt(0);
        }
        var profile = _raw[^1]; profile.EndBar = bar;
        if (CalculateDisplayProfiles) _ranges.Add((candle.Low, candle.High));
        _latest.Clear();
        _latestCoverage = FootprintStudyData.Read(candle, _estimate, _effectiveGroup, false, _levels);
        CountCoverage(profile, _latestCoverage, 1);
        _latestResolution = candle.ResolutionSeconds; CountResolution(profile, _latestResolution, 1);
        _latestCausal = candle.CanCalculateCausally && StudyMath.IsContained(candle, ProfileFrame); if (!_latestCausal) profile.NonCausalBars++;
        AddSideRows(profile, candle);
        foreach (var row in _levels)
        {
            if (row.Volume <= 0) continue;
            decimal price = FootprintStudyData.Nearest(row.Price, _effectiveGroup);
            profile.Volumes.TryGetValue(price, out var old); profile.Volumes[price] = old + row.Volume;
            _latest.TryGetValue(price, out var contribution); _latest[price] = contribution + row.Volume;
            profile.Total += row.Volume;
        }
        _last = bar;
        // No sorting/cloning the growing profile during historical replay. Closed profiles are finalized once.
        if (CalculateDisplayProfiles && bar == CurrentBar - 1 || bar < CurrentBar - 1 && Boundary(bar + 1)) Finish(profile, bar < CurrentBar - 1 && Boundary(bar + 1));
        if (CalculateDisplayProfiles && bar == CurrentBar - 1) BuildDisplay();
    }
    private void Finish(Profile profile, bool completed = false)
    {
        profile.MaximumVolume = 0; profile.Poc = profile.ValueAreaHigh = profile.ValueAreaLow = 0;
        profile.HasBuyerPoc = profile.HasSellerPoc = false; profile.BuyerPoc = profile.SellerPoc = 0;
        profile.OuterValueAreaHigh = profile.OuterValueAreaLow = profile.DisplayMaximum = profile.MedianVolume = 0; profile.TailRatio = null;
        if (profile.Volumes.Count == 0) return;
        var prices = profile.Volumes.Keys.ToArray(); Array.Sort(prices);
        CompletedStudyKey key = completed ? CacheKey(profile, prices) : default;
        if (completed && TryRestoreCompleted(profile, key)) return;
        int poc = 0; var volumes = new decimal[prices.Length];
        for (int i = 0; i < prices.Length; i++)
        {
            decimal volume = profile.Volumes[prices[i]];
            volumes[i] = volume;
            // Ascending order + strict greater-than makes lower-price POC ties stable, including zero/negative prices.
            if (volume > profile.MaximumVolume) { profile.MaximumVolume = volume; poc = i; }
        }
        profile.Poc = prices[poc];
        var area = SparseStudyValueArea.Calculate(prices, volumes, poc, _effectiveGroup, profile.Total * _va / 100);
        profile.ValueAreaLow = area.Low; profile.ValueAreaHigh = area.High;
        CalculateExtras(profile, prices, volumes, completed, key);
    }
    private static Profile Copy(Profile source)
    {
        var copy = new Profile { StartBar = source.StartBar, EndBar = source.EndBar, Total = source.Total, Poc = source.Poc,
            MaximumVolume = source.MaximumVolume, ValueAreaHigh = source.ValueAreaHigh, ValueAreaLow = source.ValueAreaLow,
            ObservedBars = source.ObservedBars, EstimatedBars = source.EstimatedBars, IncompleteBars = source.IncompleteBars, MissingBars = source.MissingBars };
        foreach (var (price, volume) in source.Volumes) copy.Volumes.Add(price, volume);
        CopyExtras(source, copy);
        return copy;
    }
    private void AppendProfile(List<Profile> destination, Profile raw, bool developing)
    {
        var current = destination.Count > 0 ? destination[^1] : null;
        if (!_compose || current == null || current.Total == 0 || raw.Total == 0 || raw.Poc < current.ValueAreaLow || raw.Poc > current.ValueAreaHigh)
        { destination.Add(Copy(raw)); return; }
        if (developing) { current = Copy(current); destination[^1] = current; }
        else _touches.Remove(current);
        foreach (var (price, volume) in raw.Volumes) { current.Volumes.TryGetValue(price, out var old); current.Volumes[price] = old + volume; }
        current.EndBar = raw.EndBar; current.Total += raw.Total;
        current.ObservedBars += raw.ObservedBars; current.EstimatedBars += raw.EstimatedBars;
        current.IncompleteBars += raw.IncompleteBars; current.MissingBars += raw.MissingBars;
        MergeExtras(raw, current);
        Finish(current);
    }
    private void BuildDisplay()
    {
        // Closed composites are committed once at boundaries. A developing replacement only overlays the last one.
        // Retain the 60 closed sources plus one developing block, never clone/sort all loaded history per new bar.
        while (_closedRawCount < _raw.Count - 1)
        {
            AppendProfile(_closed, _raw[_closedRawCount++], false);
            if (_closed.Count > 61) { _touches.Remove(_closed[0]); _closed.RemoveAt(0); }
        }
        var display = new List<Profile>(_closed);
        AppendProfile(display, _raw[^1], true);
        if (display.Count > 61) display.RemoveAt(0);
        var untested = new List<PocProjection>(); var tested = new List<PocProjection>();
        for (int i = Math.Max(0, display.Count - 61); i < display.Count - 1; i++)
        {
            var source = display[i]; if (source.Total <= 0) continue;
            var scan = _touches.TryGetValue(source, out var saved) ? saved : (Scanned: source.EndBar, Touch: -1);
            int touch = scan.Touch;
            if (touch == _last) touch = -1; // Latest replacement can undo a previous first touch.
            int from = Math.Max(source.EndBar + 1, Math.Min(scan.Scanned, _last));
            if (touch < 0)
                for (int bar = from; bar <= _last; bar++)
                    if (_ranges[bar].Low <= source.Poc && _ranges[bar].High >= source.Poc) { touch = bar; break; }
            _touches[source] = (_last, touch);
            var projection = new PocProjection(source.Poc, source.EndBar, touch);
            if (touch < 0) untested.Add(projection); else tested.Add(projection);
        }
        decimal close = GetCandle(_last).Close;
        untested.Sort((a, b) => { int c = Math.Abs(a.Price - close).CompareTo(Math.Abs(b.Price - close)); return c != 0 ? c : a.SourceEndBar.CompareTo(b.SourceEndBar); });
        tested.Sort((a, b) => { int c = b.TouchBar.CompareTo(a.TouchBar); return c != 0 ? c : a.SourceEndBar.CompareTo(b.SourceEndBar); });
        var pocs = new List<PocProjection>();
        for (int i = 0; i < untested.Count && (_untested == 0 || i < _untested); i++) pocs.Add(untested[i]);
        for (int i = 0; i < Math.Min(10, tested.Count); i++) pocs.Add(tested[i]);
        _display = display; _pocs = pocs;
        _displayView = _display.AsReadOnly(); _pocView = _pocs.AsReadOnly();
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        lock (_sync)
        {
            FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, $"Composite profile ({_period}): {Status}; POC: 60-profile lookback, up to 10 tested", Color.LightSkyBlue);
            _pocPen.Color = PocColor; _outerPen.Color = Color.FromArgb(130, PocColor);
            int first = FirstVisibleBarNumber, last = Math.Min(LastVisibleBarNumber, _last);
            for (int i = Math.Max(0, _display.Count - _count); i < _display.Count; i++)
            {
                var profile = _display[i]; if (profile.EndBar < first || profile.StartBar > last || profile.MaximumVolume == 0) continue;
                int x = ChartInfo.GetXByBar(profile.StartBar), end = ChartInfo.GetXByBar(profile.EndBar);
                int maxWidth = Math.Max(20, (end - x) * Math.Clamp(WidthPercent, 5, 100) / 100);
                foreach (var (price, volume) in profile.Volumes)
                {
                    int y = ChartInfo.GetYByPrice(price), next = ChartInfo.GetYByPrice(price + _effectiveGroup);
                    context.FillRectangle(price >= profile.ValueAreaLow && price <= profile.ValueAreaHigh ? ValueAreaColor : ProfileColor,
                        new Rectangle(x, Math.Min(y, next), Math.Max(1, profile.DisplayMaximum == 0 ? 0 : (int)(DisplayVolume(profile, volume) / profile.DisplayMaximum * maxWidth)), Math.Max(2, Math.Abs(next - y))));
                }
                int pocY = ChartInfo.GetYByPrice(profile.Poc);
                context.DrawLine(_pocPen, x, pocY, Math.Max(x + 5, end), pocY);
                if (ShowLabels) context.DrawString(FootprintStudyData.Label(profile.Coverage), FootprintStudyData.SmallFont, Color.LightSkyBlue, x + 3, pocY + 3);
                RenderExtras(context, profile, x, end);
            }
            foreach (var poc in _pocs)
            {
                bool tested = poc.TouchBar >= 0;
                if (tested ? !ShowTestedPocs : !ShowUntestedPocs) continue;
                if (poc.SourceEndBar > last || tested && poc.TouchBar < first) continue;
                int x = ChartInfo.GetXByBar(Math.Max(poc.SourceEndBar, first)), end = ChartInfo.GetXByBar(tested ? Math.Min(poc.TouchBar, last) : last), y = ChartInfo.GetYByPrice(poc.Price);
                _projectionPen.Color = Color.FromArgb(tested ? 80 : 230, PocColor); _projectionPen.Width = tested ? 1 : 2;
                context.DrawLine(_projectionPen, x, y, end, y);
                if (ShowLabels) context.DrawString($"POC {poc.Price} {(tested ? "tested" : "untested")}", FootprintStudyData.AnnotationFont, PocColor, x + 4, y - 14);
            }
        }
    }
}
