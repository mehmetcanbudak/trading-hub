using System.Collections.ObjectModel;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Charting.Drawings;

// Capability inspired by KVP 50220ba91613161685f84384825d855cb4dbeb76.
// CPU implementation deliberately avoids its dense tick arrays, float totals and shared clone state.
public sealed class TradeDrawingCalc
{
    public const int MaximumProfileRows = 4096;
    public const int MaximumVwapPoints = 16384;

    public sealed record Coverage(DataQuality Quality, int ResolutionSeconds, string SourceIdentity, int Records)
    {
        public string Description { get; } = Describe(Quality, ResolutionSeconds);

        private static string Describe(DataQuality quality, int resolutionSeconds)
        {
            var parts = new List<string>(5);
            if ((quality & DataQuality.Compact) != 0) parts.Add($"compact aggregates ({resolutionSeconds}s)");
            if ((quality & DataQuality.Synthetic) != 0) parts.Add("synthetic candle estimate");
            if ((quality & DataQuality.Raw) != 0) parts.Add("raw trades");
            if ((quality & DataQuality.InferredSide) != 0) parts.Add("inferred aggressor side");
            if ((quality & DataQuality.Partial) != 0) parts.Add("partial coverage");
            return parts.Count == 0 ? "No trade coverage" : string.Join(" · ", parts);
        }
    }

    public readonly record struct VwapPoint(int Bar, decimal Vwap, double Deviation, decimal Volume);
    public sealed record VwapResult(IReadOnlyList<VwapPoint> Points, Coverage Coverage, bool LineDecimated);
    public readonly record struct ProfileRow(decimal Price, decimal Bid, decimal Ask, decimal Unclassified)
    {
        public decimal Volume => Bid + Ask + Unclassified;
        public decimal Delta => Ask - Bid;
    }
    public sealed record ProfileResult(IReadOnlyList<ProfileRow> Rows, decimal RowSize, decimal Low, decimal High,
        decimal Poc, decimal Vah, decimal Val, decimal Total, decimal MaxVolume, Coverage Coverage, DateTime From, DateTime To);

    private readonly record struct DataKey(Guid Store, long Revision, long Correction, Guid Series, long SeriesRevision, long Generation);
    private readonly record struct ProfileKey(DataKey Data, DateTime From, DateTime To, int Rows, int Area);
    private readonly record struct VwapKey(DataKey Data, DateTime Anchor);
    private ProfileKey? _profileKey;
    private ProfileResult? _profile;
    private VwapKey? _vwapKey;
    private VwapResult? _vwap;
    private SortedDictionary<decimal, ProfileRow>? _profileBins;
    private CoverageBuilder? _profileCoverage;
    private int _profileFirst, _profileEnd, _profileStoreCount;
    private long _profileLow, _profileHigh;
    private readonly List<VwapPoint> _vwapPoints = new();
    private ReadOnlyCollection<VwapPoint>? _vwapView;
    private CoverageBuilder? _vwapCoverage;
    private int _vwapProcessed, _vwapLastBar, _vwapFirstBar, _vwapStride, _vwapStoreCount;
    private decimal _vwapWeight, _vwapMean, _vwapM2, _vwapPriceVolume;
    private decimal _vwapConsumedInTrade, _vwapPreviousEndOffset, _vwapBoundaryOffset;
    private int _vwapPreviousEndIndex = -1, _vwapBoundaryIndex = -1;

    private static bool IsAppend(DataKey previous, DataKey current, int previousCount, TradeStore store) =>
        previous.Store == current.Store && previous.Series == current.Series && previous.Correction == current.Correction &&
        current.Revision > previous.Revision && store.Count > previousCount &&
        current.Revision - previous.Revision == store.Count - previousCount;

    private static DataKey Key(TradeStore store, CandleSeries series, long generation) =>
        new(store.Identity, store.Revision, store.CorrectionRevision, series.Identity, series.Revision, generation);

    /// <summary>Inclusive UTC endpoints. Memory is bounded by requested rows, never by the price-tick span.</summary>
    public ProfileResult? FixedRangeProfile(TradeStore store, CandleSeries series, DateTime fromUtc, DateTime toUtc,
        int rows = 120, int valueAreaPercent = 70, long generation = 0)
    {
        if (toUtc < fromUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        rows = Math.Clamp(rows, 1, MaximumProfileRows);
        valueAreaPercent = Math.Clamp(valueAreaPercent, 1, 100);
        var key = new ProfileKey(Key(store, series, generation), fromUtc, toUtc, rows, valueAreaPercent);
        if (_profileKey == key) return _profile;
        var previous = _profileKey;
        _profileKey = null;
        var instrument = store.Instrument;
        if (instrument.Key != series.Instrument.Key || instrument.TickSize != series.Instrument.TickSize)
            throw new ArgumentException("Drawing source and candle series must use the same instrument.", nameof(series));
        int first = store.LowerBound(fromUtc), end = UpperBound(store, toUtc);
        bool append = previous is { } oldKey && oldKey.From == fromUtc && oldKey.To == toUtc && oldKey.Rows == rows &&
            oldKey.Area == valueAreaPercent && _profile != null && _profileBins != null &&
            first == _profileFirst && end >= _profileEnd && IsAppend(oldKey.Data, key.Data, _profileStoreCount, store);
        if (append && end == _profileEnd)
        {
            _profileKey = key;
            _profileStoreCount = store.Count;
            return _profile;
        }
        long low = append ? _profileLow : long.MaxValue, high = append ? _profileHigh : long.MinValue;
        int scanFrom = append ? _profileEnd : first;
        for (int i = scanFrom; i < end; i++)
        {
            if (store.GetVolume(i) <= 0) continue;
            long price = store.GetPriceTicks(i);
            low = Math.Min(low, price);
            high = Math.Max(high, price);
        }
        if (high < low)
        {
            _profile = null;
            _profileKey = key;
            return null;
        }

        // Decimal differences also cover the complete signed Int64 tick domain without overflow.
        decimal ticksPerRow = Math.Max(1m, decimal.Ceiling(((decimal)high - low + 1m) / rows));
        decimal rowSize = ticksPerRow * instrument.TickSize;
        bool reuseBins = append && low == _profileLow && high == _profileHigh;
        var bins = reuseBins ? _profileBins! : new SortedDictionary<decimal, ProfileRow>();
        var coverage = reuseBins ? _profileCoverage! : new CoverageBuilder(instrument.Key);
        for (int i = reuseBins ? _profileEnd : first; i < end; i++)
        {
            decimal volume = store.GetVolume(i);
            if (volume <= 0) continue;
            decimal row = low + decimal.Floor(((decimal)store.GetPriceTicks(i) - low) / ticksPerRow) * ticksPerRow;
            decimal price = row * instrument.TickSize;
            bins.TryGetValue(price, out var old);
            var side = store.GetSide(i);
            bins[price] = new ProfileRow(price, old.Bid + (side == TradeSide.Sell ? volume : 0),
                old.Ask + (side == TradeSide.Buy ? volume : 0), old.Unclassified + (side == TradeSide.None ? volume : 0));
            coverage.Add(store.GetProvenance(i));
        }
        var resultRows = bins.Values.ToArray();
        decimal total = 0, max = -1;
        int poc = 0;
        for (int i = 0; i < resultRows.Length; i++)
        {
            decimal volume = resultRows[i].Volume;
            total += volume;
            if (volume > max) { max = volume; poc = i; }
        }
        decimal lowPrice = low * instrument.TickSize;
        int logicalRows = (int)decimal.Floor(((decimal)high - low) / ticksPerRow) + 1;
        int pocRow = (int)((resultRows[poc].Price - lowPrice) / rowSize);
        int down = pocRow, up = pocRow;
        decimal VolumeAt(int row) => bins.TryGetValue(lowPrice + row * rowSize, out var value) ? value.Volume : 0;
        decimal accumulated = max, target = total * valueAreaPercent / 100m;
        while (accumulated < target && (down > 0 || up + 1 < logicalRows))
        {
            decimal above = up + 1 < logicalRows ? VolumeAt(up + 1) : -1;
            decimal below = down > 0 ? VolumeAt(down - 1) : -1;
            if (above >= below) accumulated += VolumeAt(++up);
            else accumulated += VolumeAt(--down);
        }
        _profile = new ProfileResult(Array.AsReadOnly(resultRows), rowSize, lowPrice, high * instrument.TickSize,
            resultRows[poc].Price, Math.Min(high * instrument.TickSize, lowPrice + (up + 1) * rowSize - instrument.TickSize),
            lowPrice + down * rowSize, total, max, coverage.Build(), fromUtc, toUtc);
        _profileBins = bins;
        _profileCoverage = coverage;
        _profileFirst = first;
        _profileEnd = end;
        _profileStoreCount = store.Count;
        _profileLow = low;
        _profileHigh = high;
        _profileKey = key;
        return _profile;
    }

    /// <summary>
    /// Volume-weighted online moments from the exact anchor. The bounded point view is UI-thread confined
    /// and remains valid until the next calculation; every chart/drawing runtime owns its calculator.
    /// </summary>
    public VwapResult AnchoredVwap(TradeStore store, CandleSeries series, DateTime anchorUtc, long generation = 0)
    {
        var key = new VwapKey(Key(store, series, generation), anchorUtc);
        if (_vwapKey == key && _vwap != null) return _vwap;
        var previous = _vwapKey;
        _vwapKey = null;
        if (store.Instrument.Key != series.Instrument.Key || store.Instrument.TickSize != series.Instrument.TickSize)
            throw new ArgumentException("Drawing source and candle series must use the same instrument.", nameof(series));
        int firstTrade = store.LowerBound(anchorUtc);
        int firstBar = FirstBarForTrade(series, firstTrade);
        int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(0, series.Count - firstBar) / (double)MaximumVwapPoints));
        bool append = previous is { } oldKey && oldKey.Anchor == anchorUtc && _vwapCoverage != null &&
            firstBar == _vwapFirstBar && stride == _vwapStride && series.Count > _vwapLastBar &&
            IsAppend(oldKey.Data, key.Data, _vwapStoreCount, store);
        if (!append)
        {
            _vwapPoints.Clear();
            _vwapCoverage = new CoverageBuilder(store.Instrument.Key);
            _vwapProcessed = firstTrade;
            _vwapLastBar = firstBar;
            _vwapWeight = _vwapMean = _vwapM2 = _vwapPriceVolume = 0;
            _vwapFirstBar = firstBar;
            _vwapStride = stride;
            _vwapConsumedInTrade = _vwapPreviousEndOffset = _vwapBoundaryOffset = 0;
            _vwapPreviousEndIndex = _vwapBoundaryIndex = -1;
        }
        else if (_vwapPoints.Count > 0 && (_vwapPoints[^1].Bar - firstBar + 1) % stride != 0)
        {
            // A provisional last-bar sample is replaced when the next bar appears, not accumulated twice.
            _vwapPoints.RemoveAt(_vwapPoints.Count - 1);
        }
        if (_vwapProcessed < firstTrade) { _vwapProcessed = firstTrade; _vwapConsumedInTrade = 0; }
        for (int bar = _vwapLastBar; bar < series.Count; bar++)
        {
            if (bar > _vwapLastBar)
            {
                _vwapPreviousEndIndex = _vwapBoundaryIndex;
                _vwapPreviousEndOffset = _vwapBoundaryOffset;
            }
            var candle = series[bar];
            int end = Math.Min(store.Count - 1, candle.LastTradeIndex);
            decimal endOffset = candle.LastTradeVolume + (_vwapPreviousEndIndex == end ? _vwapPreviousEndOffset : 0);
            while (_vwapProcessed <= end)
            {
                decimal fullVolume = store.GetVolume(_vwapProcessed);
                decimal allowed = _vwapProcessed == end ? Math.Min(fullVolume, endOffset) : fullVolume;
                decimal volume = allowed - _vwapConsumedInTrade;
                if (volume > 0)
                {
                    if (_vwapConsumedInTrade == 0) _vwapCoverage!.Add(store.GetProvenance(_vwapProcessed));
                    decimal price = store.GetPrice(_vwapProcessed), sum = _vwapWeight + volume;
                    decimal difference = price - _vwapMean;
                    _vwapPriceVolume += price * volume;
                    _vwapMean = _vwapPriceVolume / sum;
                    _vwapM2 += volume * difference * (price - _vwapMean);
                    _vwapWeight = sum;
                    _vwapConsumedInTrade = allowed;
                }
                if (_vwapConsumedInTrade < fullVolume) break;
                _vwapProcessed++;
                _vwapConsumedInTrade = 0;
            }
            _vwapBoundaryIndex = end;
            _vwapBoundaryOffset = endOffset;
            if (_vwapWeight > 0 && ((bar - firstBar + 1) % stride == 0 || bar == series.Count - 1))
            {
                var point = new VwapPoint(bar, _vwapMean, Math.Sqrt((double)Math.Max(0, _vwapM2 / _vwapWeight)), _vwapWeight);
                if (_vwapPoints.Count > 0 && _vwapPoints[^1].Bar == bar) _vwapPoints[^1] = point;
                else _vwapPoints.Add(point);
            }
        }
        _vwapLastBar = Math.Max(firstBar, series.Count - 1);
        _vwapStoreCount = store.Count;
        _vwapView ??= new ReadOnlyCollection<VwapPoint>(_vwapPoints);
        _vwap = new VwapResult(_vwapView, _vwapCoverage!.Build(), stride > 1);
        _vwapKey = key;
        return _vwap;
    }

    private static int UpperBound(TradeStore store, DateTime time)
    {
        int low = 0, high = store.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (store.GetTimeTicks(mid) <= time.Ticks) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private static int FirstBarForTrade(CandleSeries series, int trade)
    {
        int low = 0, high = series.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (series[mid].LastTradeIndex < trade) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private sealed class CoverageBuilder(string fallbackSource)
    {
        private DataQuality _quality;
        private int _resolution, _records;
        private string? _source;
        private bool _multiple;
        public void Add(TradeProvenance provenance)
        {
            _quality |= provenance.Quality;
            _resolution = Math.Max(_resolution, provenance.ResolutionSeconds);
            _records++;
            string source = provenance.SourceIdentity ?? fallbackSource;
            if (_source == null) _source = source;
            else if (_source != source) _multiple = true;
        }
        public Coverage Build() => new(_quality, _resolution, _multiple ? "Multiple sources" : _source ?? fallbackSource, _records);
    }
}
