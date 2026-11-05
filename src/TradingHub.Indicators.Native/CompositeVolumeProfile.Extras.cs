// Independent marginal-profile capabilities based on the KVP capability reference
// gouqizi2214730-source/trading-hub @ 50220ba91613161685f84384825d855cb4dbeb76, MIT with Commons Clause.
// Reuses CompositeVolumeProfile; no second profile engine or dense tick-span allocation.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

public partial class CompositeVolumeProfile
{
    public enum VolumeTransform { Raw, Logarithmic, MedianCap }
    public sealed partial class Profile
    {
        internal readonly Dictionary<decimal, (decimal Bid, decimal Ask)> Sides = new();
        internal int CompactBars, InferredBars, PartialBars;
        internal readonly SortedDictionary<int, int> Resolutions = new();
        public int ResolutionSeconds { get; internal set; }
        internal int NonCausalBars;
        public bool IsStrategyAvailable => Total > 0 && NonCausalBars == 0 && MissingBars == 0 && IncompleteBars == 0 && PartialBars == 0 && !IsPartialPeriod;
        public bool IsPartialPeriod { get; internal set; }
        public decimal BuyerPoc { get; internal set; }
        public decimal SellerPoc { get; internal set; }
        public bool HasBuyerPoc { get; internal set; }
        public bool HasSellerPoc { get; internal set; }
        public decimal OuterValueAreaHigh { get; internal set; }
        public decimal OuterValueAreaLow { get; internal set; }
        /// <summary>Outer-area width / inner-area width; null for a zero-width inner area. Not a distribution-fit test.</summary>
        public decimal? TailRatio { get; internal set; }
        internal decimal MedianVolume, DisplayMaximum;
    }
    private sealed record ExtraResult(decimal Poc, decimal ValueAreaHigh, decimal ValueAreaLow, decimal MaximumVolume, decimal Total,
        decimal BuyerPoc, decimal SellerPoc, bool HasBuyerPoc, bool HasSellerPoc, decimal OuterHigh, decimal OuterLow, decimal? TailRatio, decimal MedianVolume);
    private readonly Dictionary<decimal, (decimal Bid, decimal Ask)> _latestSides = new();
    private CompletedStudyResultCache _resultCache = new(); private string _cacheDirectory = "";
    private decimal _outer = 90, _cap = 10; private VolumeTransform _transform; private bool _inferred;
    private int _latestResolution;
    private bool _latestCausal;
    private static readonly RenderPen BuyerPocPen = new(Color.LimeGreen, 2), SellerPocPen = new(Color.IndianRed, 2);
    private readonly RenderPen _outerPen = new(Color.DarkOrange);
    [Display(Name = "Outer value area (%)", GroupName = "Calculation"), Range(1, 100)]
    public decimal OuterValueAreaPercent { get => _outer; set { _outer = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Display-only volume transform", GroupName = "Visualization")]
    public VolumeTransform DisplayVolumeTransform { get => _transform; set { _transform = value; RecalculateValues(); } }
    [Display(Name = "Display cap (times row median)", GroupName = "Visualization"), Range(1, 100)]
    public decimal DisplayCapMultiple { get => _cap; set { _cap = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Buyer / seller POC", GroupName = "POC")] public bool ShowBuyerSellerPoc { get; set; }
    [Display(Name = "Outer area and tail-width ratio", GroupName = "Visualization")] public bool ShowOuterValueArea { get; set; }
    [Display(Name = "Allow explicitly inferred aggressors for side POC", GroupName = "Calculation")]
    public bool AllowInferredAggressors { get => _inferred; set { _inferred = value; RecalculateValues(); } }
    [Display(Name = "Opt-in completed cache directory (empty = bounded memory only)", GroupName = "Cache")]
    public string CompletedCacheDirectory
    {
        get => _cacheDirectory;
        set { var next = value?.Trim() ?? ""; if (_cacheDirectory == next) return; _resultCache.Dispose(); _cacheDirectory = next; _resultCache = new(next); RecalculateValues(); }
    }
    [Browsable(false)] public string? CacheError => _resultCache.LastError;
    internal void ResetForProjectionOwner() { Reset(); Clear(); }
    private StudyTimeFrame ProfileFrame => _period switch
    {
        ProfilePeriod.Week => StudyTimeFrame.Week, ProfilePeriod.Month => StudyTimeFrame.Month,
        ProfilePeriod.Quarter => StudyTimeFrame.Quarter, ProfilePeriod.Year => StudyTimeFrame.Year, _ => StudyTimeFrame.Day
    };
    private bool IsCalendarPeriodStart(IndicatorCandle candle) => StudyMath.IsPeriodOpening(candle, ProfileFrame);
    private static void CountExtraCoverage(Profile p, StudyCoverage coverage, int change)
    {
        if ((coverage & StudyCoverage.Compact) != 0) p.CompactBars += change;
        if ((coverage & StudyCoverage.InferredSide) != 0) p.InferredBars += change;
        if ((coverage & StudyCoverage.Partial) != 0) p.PartialBars += change;
    }
    private static void CountResolution(Profile p, int resolution, int change)
    {
        p.Resolutions.TryGetValue(resolution, out var count);
        if (count + change == 0) p.Resolutions.Remove(resolution); else p.Resolutions[resolution] = count + change;
        p.ResolutionSeconds = 0; foreach (var pair in p.Resolutions) p.ResolutionSeconds = pair.Key;
    }
    private void AddSideRows(Profile profile, IndicatorCandle candle)
    {
        _latestSides.Clear();
        // Estimated OHLC never acquires invented buyer/seller POCs. Raw inferred side requires explicit opt-in.
        if ((!candle.HasObservedFootprint && !candle.HasAggregatedFootprint) || candle.HasInferredSide && !AllowInferredAggressors) return;
        foreach (var row in _levels)
        {
            if (row.Bid == 0 && row.Ask == 0) continue;
            decimal price = FootprintStudyData.Nearest(row.Price, _effectiveGroup);
            profile.Sides.TryGetValue(price, out var old); profile.Sides[price] = (old.Bid + row.Bid, old.Ask + row.Ask);
            _latestSides.TryGetValue(price, out var latest); _latestSides[price] = (latest.Bid + row.Bid, latest.Ask + row.Ask);
        }
    }
    private void UndoSideRows(Profile p)
    {
        foreach (var (price, side) in _latestSides)
        { var old = p.Sides[price]; var next = (Bid: old.Bid - side.Bid, Ask: old.Ask - side.Ask); if (next == (0m, 0m)) p.Sides.Remove(price); else p.Sides[price] = next; }
    }
    private static void CopyExtras(Profile source, Profile destination)
    {
        destination.CompactBars = source.CompactBars; destination.InferredBars = source.InferredBars; destination.PartialBars = source.PartialBars;
        destination.IsPartialPeriod = source.IsPartialPeriod;
        destination.NonCausalBars = source.NonCausalBars;
        destination.ResolutionSeconds = source.ResolutionSeconds; foreach (var pair in source.Resolutions) destination.Resolutions.Add(pair.Key, pair.Value);
        destination.BuyerPoc = source.BuyerPoc; destination.SellerPoc = source.SellerPoc; destination.HasBuyerPoc = source.HasBuyerPoc; destination.HasSellerPoc = source.HasSellerPoc;
        destination.OuterValueAreaHigh = source.OuterValueAreaHigh; destination.OuterValueAreaLow = source.OuterValueAreaLow; destination.TailRatio = source.TailRatio;
        destination.MedianVolume = source.MedianVolume; destination.DisplayMaximum = source.DisplayMaximum;
        foreach (var row in source.Sides) destination.Sides.Add(row.Key, row.Value);
    }
    private static void MergeExtras(Profile source, Profile destination)
    {
        destination.CompactBars += source.CompactBars; destination.InferredBars += source.InferredBars; destination.PartialBars += source.PartialBars;
        destination.IsPartialPeriod |= source.IsPartialPeriod;
        destination.NonCausalBars += source.NonCausalBars;
        foreach (var pair in source.Resolutions) CountResolution(destination, pair.Key, pair.Value);
        foreach (var (price, side) in source.Sides) { destination.Sides.TryGetValue(price, out var old); destination.Sides[price] = (old.Bid + side.Bid, old.Ask + side.Ask); }
    }
    private CompletedStudyKey CacheKey(Profile profile, decimal[] prices)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<int> bits = stackalloc int[4];
        foreach (decimal price in prices)
        {
            decimal.GetBits(price, bits); hash.AppendData(MemoryMarshal.AsBytes(bits));
            decimal.GetBits(profile.Volumes[price], bits); hash.AppendData(MemoryMarshal.AsBytes(bits));
            profile.Sides.TryGetValue(price, out var side);
            decimal.GetBits(side.Bid, bits); hash.AppendData(MemoryMarshal.AsBytes(bits)); decimal.GetBits(side.Ask, bits); hash.AppendData(MemoryMarshal.AsBytes(bits));
        }
        var first = GetCandle(profile.StartBar); var last = GetCandle(profile.EndBar);
        var sources = new HashSet<string>(StringComparer.Ordinal); long revision = 0, correction = 0; int resolution = 0;
        Span<long> metadata = stackalloc long[7];
        for (int b = profile.StartBar; b <= profile.EndBar; b++)
        {
            var candle = GetCandle(b); revision = Math.Max(revision, candle.Revision); correction = Math.Max(correction, candle.CorrectionRevision); resolution = Math.Max(resolution, candle.ResolutionSeconds);
            sources.Add(candle.SourceIdentity ?? "");
            metadata[0] = candle.Time.Ticks; metadata[1] = candle.Revision; metadata[2] = candle.CorrectionRevision; metadata[3] = (long)candle.Quality; metadata[4] = candle.ResolutionSeconds;
            metadata[5] = candle.AvailableAt.Ticks; metadata[6] = (candle.HasKnownAvailability ? 1 : 0) | (candle.IsTimeResolutionCompatible ? 2 : 0);
            hash.AppendData(MemoryMarshal.AsBytes(metadata));
        }
        string config = FormattableString.Invariant($"profile-logical-rows-v5;period={_period};source={ChartInfo?.TimeFrame};group={_group};effective={_effectiveGroup};va={_va};outer={_outer};estimate={_estimate};inferred={_inferred};session={first.SessionStartUtc(first.SessionDate):O};zone={first.SessionTimeZone.Id};tick={InstrumentInfo?.TickSize}");
        return new(first.SeriesSourceIdentity, InstrumentInfo?.Instrument ?? "", config, JsonSerializer.Serialize(sources.Order(StringComparer.Ordinal)),
            revision, correction, profile.Coverage, resolution, first.Time.Ticks, last.LastTime.Ticks, Convert.ToHexString(hash.GetHashAndReset()));
    }
    private bool TryRestoreCompleted(Profile profile, CompletedStudyKey key)
    {
        if (!_resultCache.TryRead(key, out var payload)) return false;
        ExtraResult? result;
        try { result = JsonSerializer.Deserialize<ExtraResult>(payload); } catch (JsonException) { return false; }
        if (result == null || result.Total != profile.Total || result.MedianVolume <= 0 || result.TailRatio < 0 ||
            !profile.Volumes.TryGetValue(result.Poc, out var peak) || peak != result.MaximumVolume ||
            result.OuterLow > result.ValueAreaLow || result.OuterHigh < result.ValueAreaHigh ||
            result.ValueAreaLow > result.ValueAreaHigh || !profile.Volumes.ContainsKey(result.ValueAreaLow) || !profile.Volumes.ContainsKey(result.ValueAreaHigh)) return false;
        profile.Poc = result.Poc; profile.MaximumVolume = result.MaximumVolume; profile.ValueAreaHigh = result.ValueAreaHigh; profile.ValueAreaLow = result.ValueAreaLow;
        ApplyExtras(profile, result); return true;
    }
    private void CalculateExtras(Profile profile, decimal[] prices, decimal[] volumes, bool completed, CompletedStudyKey key)
    {
        decimal bidMax = 0, askMax = 0, bidPoc = 0, askPoc = 0;
        foreach (decimal price in prices)
        {
            profile.Sides.TryGetValue(price, out var side);
            if (side.Bid > bidMax) { bidMax = side.Bid; bidPoc = price; }
            if (side.Ask > askMax) { askMax = side.Ask; askPoc = price; }
        }
        int poc = Array.BinarySearch(prices, profile.Poc);
        var outer = SparseStudyValueArea.Calculate(prices, volumes, poc, _effectiveGroup, profile.Total * Math.Max(_outer, _va) / 100);
        decimal median = StudyMath.Median(volumes), width = profile.ValueAreaHigh - profile.ValueAreaLow;
        var result = new ExtraResult(profile.Poc, profile.ValueAreaHigh, profile.ValueAreaLow, profile.MaximumVolume, profile.Total,
            askPoc, bidPoc, askMax > 0, bidMax > 0, outer.High, outer.Low, width == 0 ? null : (outer.High - outer.Low) / width, median);
        if (completed) _resultCache.Store(key, JsonSerializer.SerializeToUtf8Bytes(result));
        ApplyExtras(profile, result);
    }
    private void ApplyExtras(Profile profile, ExtraResult result)
    {
        profile.BuyerPoc = result.BuyerPoc; profile.SellerPoc = result.SellerPoc; profile.HasBuyerPoc = result.HasBuyerPoc; profile.HasSellerPoc = result.HasSellerPoc;
        profile.OuterValueAreaHigh = result.OuterHigh; profile.OuterValueAreaLow = result.OuterLow; profile.TailRatio = result.TailRatio; profile.MedianVolume = result.MedianVolume;
        profile.DisplayMaximum = DisplayVolume(profile, profile.MaximumVolume);
    }
    private decimal DisplayVolume(Profile profile, decimal volume) => DisplayVolumeTransform switch
    {
        VolumeTransform.Logarithmic => (decimal)Math.Log(1 + (double)volume),
        VolumeTransform.MedianCap => profile.MedianVolume >= volume / DisplayCapMultiple ? volume : profile.MedianVolume * DisplayCapMultiple, _ => volume
    };
    private void RenderExtras(RenderContext context, Profile p, int x, int end)
    {
        if (ShowBuyerSellerPoc)
        {
            if (p.HasBuyerPoc) { int y = ChartInfo.GetYByPrice(p.BuyerPoc); context.DrawLine(BuyerPocPen, x, y, end, y); }
            if (p.HasSellerPoc) { int y = ChartInfo.GetYByPrice(p.SellerPoc); context.DrawLine(SellerPocPen, x, y, end, y); }
        }
        if (ShowOuterValueArea)
        {
            int top = ChartInfo.GetYByPrice(p.OuterValueAreaHigh), bottom = ChartInfo.GetYByPrice(p.OuterValueAreaLow);
            context.DrawRectangle(_outerPen, new Rectangle(x, Math.Min(top, bottom), Math.Max(1, end - x), Math.Max(1, Math.Abs(bottom - top))));
            context.DrawString(p.TailRatio is { } ratio ? $"Tail width ratio {ratio:0.##}" : "Tail width ratio unavailable (zero inner width)", FootprintStudyData.SmallFont, PocColor, x + 2, Math.Min(top, bottom));
        }
    }
    protected override void OnDispose() { _resultCache.Dispose(); base.OnDispose(); }
}
