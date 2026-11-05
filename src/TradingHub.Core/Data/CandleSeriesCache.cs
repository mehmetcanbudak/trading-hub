using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Bounded LRU of immutable, resumable aggregation snapshots. Trade stores and destination series
/// remain owned by their calling chart thread; the cache never retains their mutable objects.
/// </summary>
public sealed class CandleSeriesCache
{
    public static CandleSeriesCache Shared { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<CacheKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly long _maxBytes;
    private readonly int _maxEntries;
    private long _retainedBytes;

    public CandleSeriesCache(long maxBytes = 128L * 1024 * 1024, int maxEntries = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _maxBytes = maxBytes;
        _maxEntries = maxEntries;
    }

    public int EntryCount { get { lock (_sync) return _entries.Count; } }

    /// <summary>Conservative retained-size estimate, including populated price rows and cache keys.</summary>
    public long RetainedBytes { get { lock (_sync) return _retainedBytes; } }

    public bool TryRestore(CandleSeries series, TradeStore store, string providerScope)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(providerScope);
        var key = CacheKey.Create(series, store, providerScope);
        CandleSeries.AggregationSnapshot snapshot;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var node) || !node.Value.Snapshot.CanRestore(series, store)) return false;
            _lru.Remove(node);
            _lru.AddLast(node);
            snapshot = node.Value.Snapshot;
        }

        // Selection pins an immutable snapshot even if another chart evicts it. No bar/row cloning
        // happens under the cache lock, and every restore creates destination-owned mutable rows.
        snapshot.Restore(series);
        return true;
    }

    public void Store(CandleSeries series, TradeStore store, string providerScope)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(providerScope);
        if (!series.CanCaptureAggregation(store)) return;
        var key = CacheKey.Create(series, store, providerScope);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var current) && current.Value.Snapshot.IsCurrent(series, store))
            {
                _lru.Remove(current);
                _lru.AddLast(current);
                return;
            }
        }

        // Admission counts populated rows before copying any candle or footprint. Sparse price
        // distances never contribute a dense allocation or a min-to-max traversal here.
        var snapshot = series.CaptureAggregationSnapshot(store, _maxBytes, key.EstimatedBytes());
        if (snapshot == null) return;
        lock (_sync)
        {
            if (_entries.Remove(key, out var previous))
            {
                _lru.Remove(previous);
                _retainedBytes -= previous.Value.Snapshot.EstimatedBytes;
            }

            while (_entries.Count >= _maxEntries || _retainedBytes > _maxBytes - snapshot.EstimatedBytes)
            {
                var oldest = _lru.First!;
                _entries.Remove(oldest.Value.Key);
                _retainedBytes -= oldest.Value.Snapshot.EstimatedBytes;
                _lru.RemoveFirst();
            }

            var node = _lru.AddLast(new Entry(key, snapshot));
            _entries.Add(key, node);
            _retainedBytes += snapshot.EstimatedBytes;
        }
    }

    private sealed record Entry(CacheKey Key, CandleSeries.AggregationSnapshot Snapshot);

    private readonly record struct InstrumentKey(string Symbol, string FeedId, string Exchange,
        decimal TickSize, decimal VolumeStep, decimal PointValue, string BaseAsset, string QuoteAsset, string Description)
    {
        internal static InstrumentKey Create(Instrument instrument) => new(instrument.Symbol, instrument.FeedId,
            instrument.Exchange, instrument.TickSize, instrument.VolumeStep, instrument.PointValue,
            instrument.BaseAsset, instrument.QuoteAsset, instrument.Description);

        internal long EstimatedBytes() => 256 + StringBytes(Symbol) + StringBytes(FeedId) + StringBytes(Exchange) +
            StringBytes(BaseAsset) + StringBytes(QuoteAsset) + StringBytes(Description);
    }

    private readonly record struct CacheKey(string ProviderScope, InstrumentKey Instrument, InstrumentKey StoreInstrument,
        ChartPeriod Period, int PriceStep, TimeSpan SessionStart, TimeZoneInfo TimeZone, string ZoneDisplayName,
        string ZoneStandardName, string ZoneDaylightName, Guid StoreIdentity, long StoreCorrectionRevision)
    {
        internal static CacheKey Create(CandleSeries series, TradeStore store, string providerScope) => new(providerScope,
            InstrumentKey.Create(series.Instrument), InstrumentKey.Create(store.Instrument), series.Period, series.PriceStep,
            series.Session.Start, series.Session.TimeZone, series.Session.TimeZone.DisplayName,
            series.Session.TimeZone.StandardName, series.Session.TimeZone.DaylightName, store.Identity, store.CorrectionRevision);

        internal long EstimatedBytes() => 1024 + StringBytes(ProviderScope) + Instrument.EstimatedBytes() + StoreInstrument.EstimatedBytes() +
            StringBytes(TimeZone.Id) + StringBytes(TimeZone.DisplayName) + StringBytes(TimeZone.StandardName) + StringBytes(TimeZone.DaylightName) +
            256L * TimeZone.GetAdjustmentRules().LongLength;
    }

    private static long StringBytes(string? value) => value == null ? 0 : 32 + 2L * value.Length;
}
