using TradingHub.Market;

namespace TradingHub.Data;

public sealed partial class CandleSeries
{
    internal bool CanCaptureAggregation(TradeStore store) => _processed == store.Count &&
        _storeIdentity == store.Identity && _storeCorrectionRevision == store.CorrectionRevision;

    internal AggregationSnapshot? CaptureAggregationSnapshot(TradeStore store, long maxBytes, long keyBytes)
    {
        if (!CanCaptureAggregation(store) || keyBytes > maxBytes - 256) return null;
        long estimatedBytes = keyBytes + 256;
        foreach (var candle in _candles)
        {
            // Count populated rows, never the potentially unbounded distance between prices.
            // The estimate covers scalar snapshots, array headers/alignment and retained source text.
            long rows = (long)candle.Levels.StoredRowCount + candle.AggregatedLevels.StoredRowCount;
            long candleBytes = 640 + 2L * (candle.SourceIdentity?.Length ?? 0) + 96 * rows;
            if (candleBytes > maxBytes - estimatedBytes) return null;
            estimatedBytes += candleBytes;
        }

        return new AggregationSnapshot(this, store.Revision, estimatedBytes);
    }

    /// <summary>Only immutable values and private row arrays cross the cache boundary.</summary>
    internal sealed class AggregationSnapshot
    {
        private readonly CandleSnapshot[] _candles;
        private readonly int _processed;
        private readonly Guid _storeIdentity;
        private readonly long _storeRevision;
        private readonly long _storeCorrectionRevision;
        private readonly long _revision;
        private readonly long _correctionRevision;
        private readonly string _sourceIdentity;
        private readonly long _bucketEnd;
        private readonly decimal _volumeInCandle;
        private readonly long _renkoBase;
        private readonly int _renkoDir;
        private readonly DateTime _availableThrough;

        internal long EstimatedBytes { get; }

        internal AggregationSnapshot(CandleSeries series, long storeRevision, long estimatedBytes)
        {
            _processed = series._processed;
            _storeIdentity = series._storeIdentity;
            _storeRevision = storeRevision;
            _storeCorrectionRevision = series._storeCorrectionRevision;
            _revision = series.Revision;
            _correctionRevision = series.CorrectionRevision;
            _sourceIdentity = series.SourceIdentity;
            _bucketEnd = series._bucketEnd;
            _volumeInCandle = series._volumeInCandle;
            _renkoBase = series._renkoBase;
            _renkoDir = series._renkoDir;
            _availableThrough = series._availableThrough;
            EstimatedBytes = estimatedBytes;
            _candles = new CandleSnapshot[series._candles.Count];
            for (int i = 0; i < _candles.Length; i++) _candles[i] = new CandleSnapshot(series._candles[i]);
        }

        internal bool CanRestore(CandleSeries series, TradeStore store) => _storeIdentity == store.Identity &&
            _storeCorrectionRevision == store.CorrectionRevision && _storeRevision <= store.Revision &&
            _processed <= store.Count && _sourceIdentity == series.SourceIdentity;

        internal bool IsCurrent(CandleSeries series, TradeStore store) => CanRestore(series, store) &&
            _processed == series._processed && _storeRevision == store.Revision && _revision == series.Revision &&
            _correctionRevision == series.CorrectionRevision && _availableThrough == series._availableThrough;

        internal void Restore(CandleSeries series)
        {
            series._candles.Clear();
            series._candles.EnsureCapacity(_candles.Length);
            foreach (var candle in _candles) series._candles.Add(candle.Restore());
            series._processed = _processed;
            series._storeIdentity = _storeIdentity;
            series._storeCorrectionRevision = _storeCorrectionRevision;
            series.Revision = _revision;
            series.CorrectionRevision = _correctionRevision;
            series._bucketEnd = _bucketEnd;
            series._volumeInCandle = _volumeInCandle;
            series._renkoBase = _renkoBase;
            series._renkoDir = _renkoDir;
            series._availableThrough = _availableThrough;
            // Identity stays chart-owned; SourceIdentity is fixed by the matching instrument key.
        }
    }

    private readonly record struct LevelSnapshot(long Row, LevelData Data);

    private readonly struct CandleSnapshot
    {
        private readonly DateTime _time, _lastTime, _earliestSourceTime, _availableAt;
        private readonly long _openTicks, _highTicks, _lowTicks, _closeTicks, _pocRow;
        private readonly long _revision, _correctionRevision, _aggregatedTrades;
        private readonly decimal _volume, _bid, _ask, _between, _maxDelta, _minDelta;
        private readonly decimal _pocVolume, _maxTradeVolume, _lastTradeVolume;
        private readonly int _trades, _firstTradeIndex, _lastTradeIndex, _resolutionSeconds, _observedTrades;
        private readonly bool _isClosed, _hasRealTrades, _hasSyntheticTrades, _hasCompactTrades;
        private readonly bool _hasKnownAvailability, _hasIncompatibleResolution;
        private readonly DataQuality _quality;
        private readonly string? _sourceIdentity;
        private readonly LevelSnapshot[] _levels, _aggregatedLevels;

        internal CandleSnapshot(Candle candle)
        {
            _time = candle.Time;
            _lastTime = candle.LastTime;
            _earliestSourceTime = candle.EarliestSourceTime;
            _availableAt = candle.AvailableAt;
            _openTicks = candle.OpenTicks;
            _highTicks = candle.HighTicks;
            _lowTicks = candle.LowTicks;
            _closeTicks = candle.CloseTicks;
            _pocRow = candle.PocRow;
            _revision = candle.Revision;
            _correctionRevision = candle.CorrectionRevision;
            _aggregatedTrades = candle.AggregatedTrades;
            _volume = candle.Volume;
            _bid = candle.Bid;
            _ask = candle.Ask;
            _between = candle.Between;
            _maxDelta = candle.MaxDelta;
            _minDelta = candle.MinDelta;
            _pocVolume = candle.PocVolume;
            _maxTradeVolume = candle.MaxTradeVolume;
            _lastTradeVolume = candle.LastTradeVolume;
            _trades = candle.Trades;
            _firstTradeIndex = candle.FirstTradeIndex;
            _lastTradeIndex = candle.LastTradeIndex;
            _resolutionSeconds = candle.ResolutionSeconds;
            _observedTrades = candle.ObservedTrades;
            _isClosed = candle.IsClosed;
            _hasRealTrades = candle.HasRealTrades;
            _hasSyntheticTrades = candle.HasSyntheticTrades;
            _hasCompactTrades = candle.HasCompactTrades;
            _hasKnownAvailability = candle.HasKnownAvailability;
            _hasIncompatibleResolution = candle.HasIncompatibleResolution;
            _quality = candle.Quality;
            _sourceIdentity = candle.SourceIdentity;
            _levels = CopyLevels(candle.Levels);
            _aggregatedLevels = CopyLevels(candle.AggregatedLevels);
        }

        internal Candle Restore()
        {
            // OI is deliberately absent: it is a chart decoration from the latest OI store,
            // not part of trade aggregation, and ChartModel reapplies it after a restore.
            var candle = new Candle
            {
                Time = _time,
                LastTime = _lastTime,
                EarliestSourceTime = _earliestSourceTime,
                AvailableAt = _availableAt,
                OpenTicks = _openTicks,
                HighTicks = _highTicks,
                LowTicks = _lowTicks,
                CloseTicks = _closeTicks,
                PocRow = _pocRow,
                Revision = _revision,
                CorrectionRevision = _correctionRevision,
                AggregatedTrades = _aggregatedTrades,
                Volume = _volume,
                Bid = _bid,
                Ask = _ask,
                Between = _between,
                MaxDelta = _maxDelta,
                MinDelta = _minDelta,
                PocVolume = _pocVolume,
                MaxTradeVolume = _maxTradeVolume,
                LastTradeVolume = _lastTradeVolume,
                Trades = _trades,
                FirstTradeIndex = _firstTradeIndex,
                LastTradeIndex = _lastTradeIndex,
                ResolutionSeconds = _resolutionSeconds,
                ObservedTrades = _observedTrades,
                IsClosed = _isClosed,
                HasRealTrades = _hasRealTrades,
                HasSyntheticTrades = _hasSyntheticTrades,
                HasCompactTrades = _hasCompactTrades,
                HasKnownAvailability = _hasKnownAvailability,
                HasIncompatibleResolution = _hasIncompatibleResolution,
                Quality = _quality,
                SourceIdentity = _sourceIdentity
            };
            RestoreLevels(_levels, candle.Levels);
            RestoreLevels(_aggregatedLevels, candle.AggregatedLevels);
            return candle;
        }

        private static LevelSnapshot[] CopyLevels(PriceLevels levels)
        {
            int count = levels.StoredRowCount;
            if (count == 0) return Array.Empty<LevelSnapshot>();
            var copy = new LevelSnapshot[count];
            int index = 0;
            foreach (var (row, data) in levels.EnumerateStored()) copy[index++] = new LevelSnapshot(row, data);
            return copy;
        }

        private static void RestoreLevels(LevelSnapshot[] source, PriceLevels destination)
        {
            foreach (var level in source) destination.GetOrAdd(level.Row) = level.Data;
        }
    }
}
