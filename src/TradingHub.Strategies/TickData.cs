using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>Immutable columnar history shared by optimization runs. Slices retain original event identities and source quality without copying.</summary>
public sealed class TickData
{
    private readonly long[] _ids, _times, _prices;
    private readonly decimal[] _volumes;
    private readonly byte[] _sides;
    private readonly ushort[] _quality;
    private readonly TradeProvenance[] _provenance;
    private readonly string?[]? _eventIds;
    private readonly long _eventIdTextBytes;
    private readonly Timeline _timeline;
    private int[]? _causalOrder;
    private bool _causalOrderReady;
    private Summary? _summary;
    private sealed record Summary(DataQuality Quality, int ResolutionSeconds, long CorrectionRevision, string SourceIdentity, bool HasKnownAvailability);

    private TickData(Instrument instrument, long[] ids, long[] times, long[] prices, decimal[] volumes, byte[] sides, ushort[] quality, TradeProvenance[] provenance, string?[]? eventIds, long eventIdTextBytes, int start, int end, bool compressed, Timeline? timeline = null, IbHistoryStamp? storageStamp = null)
    {
        Instrument = instrument; _ids = ids; _times = times; _prices = prices; _volumes = volumes; _sides = sides;
        _quality = quality; _provenance = provenance; Start = start; End = end; Compressed = compressed;
        _eventIds = eventIds; _eventIdTextBytes = eventIdTextBytes;
        _timeline = timeline ?? new Timeline(times, quality, provenance);
        StorageStamp = storageStamp;
    }

    public Instrument Instrument { get; }
    public bool Compressed { get; }
    /// <summary>Identity captured before the original disk-load range; slices and compression retain that scope's stamp.</summary>
    public IbHistoryStamp? StorageStamp { get; }
    public int Start { get; }
    public int End { get; }
    public int Count => End - Start;
    public DateTime FirstTime => Count > 0 ? new DateTime(_times[Start], DateTimeKind.Utc) : DateTime.MinValue;
    public DateTime LastTime => Count > 0 ? new DateTime(_times[End - 1], DateTimeKind.Utc) : DateTime.MinValue;
    public long MemoryBytes => (long)_times.Length * (8 + 8 + 8 + 16 + 1 + 2) + (long)(_eventIds?.Length ?? 0) * IntPtr.Size +
        _eventIdTextBytes + _timeline.MemoryBytes + (long)(_causalOrder?.Length ?? 0) * sizeof(int);
    public long Id(int index) => _ids[index];
    public long TimeTicks(int index) => _times[index];
    public long PriceTicks(int index) => _prices[index];
    public decimal Volume(int index) => _volumes[index];
    public TradeSide Side(int index) => (TradeSide)_sides[index];
    public TradeProvenance Provenance(int index)
    {
        var provenance = _provenance[_quality[index]];
        return _eventIds?[index] is { } eventId ? provenance with { SourceEventId = eventId } : provenance;
    }
    public bool IsSynthetic(int index) => (Provenance(index).Quality & DataQuality.Synthetic) != 0;
    public bool IsCompact(int index) => (Provenance(index).Quality & DataQuality.Compact) != 0;
    public bool IsObservedTrade(int index) => Provenance(index).IsObservedTrade;
    public DataQuality Quality => GetSummary().Quality;
    public int ResolutionSeconds => GetSummary().ResolutionSeconds;
    public long CorrectionRevision => GetSummary().CorrectionRevision;
    public string SourceIdentity => GetSummary().SourceIdentity;
    public bool HasKnownAvailability => GetSummary().HasKnownAvailability;

    public Trade Get(int index) => new(_ids[index], new DateTime(_times[index], DateTimeKind.Utc), Instrument.FromTicks(_prices[index]), _volumes[index], Side(index), Provenance(index));

    internal long AvailableTimeTicks(int index) => _timeline.At(index);

    internal int[]? GetCausalOrder()
    {
        if (ResolutionSeconds == 0) return null;
        if (Volatile.Read(ref _causalOrderReady)) return _causalOrder;
        lock (_timeline.Gate)
        {
            if (_causalOrderReady) return _causalOrder;
            var available = _timeline.Values;
            for (int i = Start + 1; i < End; i++)
            {
                if (available[i] >= available[i - 1]) continue;
                var order = new int[Count];
                for (int offset = 0; offset < order.Length; offset++) order[offset] = Start + offset;
                Array.Sort(order, (left, right) =>
                {
                    int compare = available[left].CompareTo(available[right]);
                    return compare != 0 ? compare : left.CompareTo(right);
                });
                _causalOrder = order;
                break;
            }
            Volatile.Write(ref _causalOrderReady, true);
            return _causalOrder;
        }
    }

    private sealed class Timeline(long[] times, ushort[] quality, TradeProvenance[] provenance)
    {
        public object Gate { get; } = new();
        private long[]? _values;
        public long MemoryBytes => (long)(Volatile.Read(ref _values)?.Length ?? 0) * sizeof(long);
        public long At(int index)
        {
            var values = Volatile.Read(ref _values);
            if (values != null) return values[index];
            return provenance[quality[index]].ResolutionSeconds <= 0 ? times[index] : Values[index];
        }
        public long[] Values
        {
            get
            {
                var values = Volatile.Read(ref _values);
                if (values != null) return values;
                lock (Gate)
                {
                    if (_values != null) return _values;
                    values = new long[times.Length];
                    for (int i = 0; i < times.Length; i++)
                        values[i] = provenance[quality[i]].AvailabilityTime(new DateTime(times[i], DateTimeKind.Utc)).Ticks;
                    Volatile.Write(ref _values, values);
                    return values;
                }
            }
        }
    }

    private Summary GetSummary()
    {
        var summary = Volatile.Read(ref _summary);
        if (summary != null) return summary;
        lock (_timeline.Gate)
        {
            if (_summary != null) return _summary;
            DataQuality quality = DataQuality.None;
            int resolution = 0; long correction = 0; string? source = null;
            bool known = true;
            for (int i = Start; i < End; i++)
            {
                var provenance = _provenance[_quality[i]];
                known &= provenance.HasKnownAvailability;
                quality |= provenance.Quality; resolution = Math.Max(resolution, provenance.ResolutionSeconds); correction = Math.Max(correction, provenance.CorrectionRevision);
                var identity = provenance.SourceIdentity;
                if (source == null) source = identity;
                else if (identity != null && source != identity) source = "mixed";
            }
            summary = new Summary(quality, resolution, correction, source ?? Instrument.Key, known);
            Volatile.Write(ref _summary, summary);
            return summary;
        }
    }

    /// <summary>Coalesces same-price/side runs within one second into measured compact events, never individual observed trades. Point runs use their last actual timestamp; interval summaries retain their source window.</summary>
    public TickData Compress()
    {
        if (Compressed) return this;
        var builder = new Builder(Instrument, Math.Min(Count, 1 << 20));
        for (int i = Start; i < End; i++) builder.Add(Get(i), compress: true);
        return builder.Finish(compressed: true, storageStamp: StorageStamp);
    }

    public TickData Slice(DateTime fromUtc, DateTime toUtc) =>
        new(Instrument, _ids, _times, _prices, _volumes, _sides, _quality, _provenance, _eventIds, _eventIdTextBytes, Lower(fromUtc.Ticks), Lower(toUtc.Ticks), Compressed, _timeline, StorageStamp);

    private int Lower(long time)
    {
        int low = Start, high = End;
        while (low < high) { int mid = (low + high) >>> 1; if (_times[mid] < time) low = mid + 1; else high = mid; }
        return low;
    }

    public static TickData Load(string root, Instrument instrument, DateTime fromUtc, DateTime toUtc, IProgress<double>? progress, CancellationToken ct, bool compress = false)
    {
        using var ibSnapshot = instrument.FeedId == IbBarImport.FeedId ? IbBarStore.OpenSnapshot(root, instrument.Symbol, ct) : null;
        IbHistoryStamp? storageStamp = ibSnapshot?.GetStamp(fromUtc, toUtc);
        var builder = new Builder(instrument, 1 << 20);
        int days = Math.Max(1, DateOnly.FromDateTime(toUtc).DayNumber - DateOnly.FromDateTime(fromUtc).DayNumber + 1), done = 0;
        for (var day = DateOnly.FromDateTime(fromUtc); day <= DateOnly.FromDateTime(toUtc); day = day.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            var rows = ibSnapshot != null ? ibSnapshot.ReadDay(day, instrument.TickSize) : HistorySources.ReadDay(root, instrument.FeedId, instrument.Symbol, day);
            foreach (var trade in rows)
            {
                if (trade.Time < fromUtc || trade.Time >= toUtc) continue;
                builder.Add(trade, compress);
            }
            if (ibSnapshot == null) progress?.Report(++done / (double)days);
        }
        var result = builder.Finish(compress, storageStamp);
        // Inline consumers may reenter history; publish progress only after releasing snapshot ownership.
        ibSnapshot?.Dispose();
        if (ibSnapshot != null) progress?.Report(1);
        return result;
    }

    public static TickData FromStore(TradeStore store)
    {
        var builder = new Builder(store.Instrument, store.Count);
        for (int i = 0; i < store.Count; i++) builder.Add(store.Get(i), compress: false);
        return builder.Finish(compressed: false);
    }

    public static TickData FromTrades(Instrument instrument, IReadOnlyList<Trade> trades)
    {
        var builder = new Builder(instrument, trades.Count);
        foreach (var trade in trades) builder.Add(trade, compress: false);
        return builder.Finish(compressed: false);
    }

    private sealed class Builder
    {
        private const int MaximumEvents = 30_000_000;
        private readonly Instrument _instrument;
        private long[] _ids, _times, _prices;
        private decimal[] _volumes;
        private byte[] _sides;
        private ushort[] _quality;
        private string?[]? _eventIds;
        private long _eventIdTextBytes;
        private readonly List<TradeProvenance> _provenance = new();
        private readonly Dictionary<TradeProvenance, ushort> _indices = new();
        private int _count;
        private TradeProvenance _lastProvenance;
        private bool _pendingProvenance;

        public Builder(Instrument instrument, int capacity)
        {
            _instrument = instrument;
            capacity = Math.Clamp(capacity, 1, MaximumEvents);
            _ids = new long[capacity]; _times = new long[capacity]; _prices = new long[capacity]; _volumes = new decimal[capacity]; _sides = new byte[capacity]; _quality = new ushort[capacity];
        }

        public void Add(in Trade trade, bool compress)
        {
            long time = trade.Time.Ticks, price = _instrument.ToTicks(trade.Price);
            if (_count > 0 && time < _times[_count - 1]) throw new InvalidDataException("Backtest history must be chronologically ordered; timestamps are never clamped into invented order.");
            var provenance = trade.Provenance;
            if (compress && (provenance.ResolutionSeconds > 0 || provenance.HasKnownAvailability && provenance.IsMeasured))
            {
                if (_count > 0 && _prices[_count - 1] == price && _sides[_count - 1] == (byte)trade.Side && _times[_count - 1] / TimeSpan.TicksPerSecond == time / TimeSpan.TicksPerSecond)
                {
                    var previous = _lastProvenance;
                    static TradeProvenance Aggregate(TradeProvenance value) => value with
                    {
                        Quality = value.IsObservedTrade ? (value.Quality & ~DataQuality.Raw) | DataQuality.Compact : value.Quality,
                        ObservedTradeCount = 0, SourceEventId = null,
                    };
                    if (Aggregate(previous) == Aggregate(provenance))
                    {
                        _volumes[_count - 1] += trade.Volume;
                        _lastProvenance = Aggregate(previous) with { ObservedTradeCount = checked(previous.ObservedTradeCount + provenance.ObservedTradeCount) };
                        // A repeated point run is fully known at its last actual point, not at an invented second-end.
                        // Keeping interval resolution unchanged also keeps coarse source-window execution fences intact.
                        if (provenance.ResolutionSeconds == 0) _times[_count - 1] = time;
                        _pendingProvenance = true;
                        if (_eventIds?[_count - 1] is { } priorEventId)
                        {
                            _eventIdTextBytes -= 32L + 2L * priorEventId.Length;
                            _eventIds[_count - 1] = null;
                        }
                        if ((previous.Quality & DataQuality.Synthetic) == 0) _ids[_count - 1] = CompactHistory.CompressedIdBase + _count - 1;
                        return;
                    }
                }
            }
            FlushProvenance();
            if (_count == MaximumEvents) throw new InvalidDataException("The requested backtest exceeds the bounded event capacity. Select a shorter range or explicitly compress the input.");
            if (_count == _times.Length)
            {
                int capacity = (int)Math.Min(MaximumEvents, _count * 2L);
                Array.Resize(ref _ids, capacity); Array.Resize(ref _times, capacity); Array.Resize(ref _prices, capacity); Array.Resize(ref _volumes, capacity); Array.Resize(ref _sides, capacity); Array.Resize(ref _quality, capacity);
                if (_eventIds != null) Array.Resize(ref _eventIds, capacity);
            }
            _ids[_count] = trade.Id;
            _times[_count] = time; _prices[_count] = price; _volumes[_count] = trade.Volume; _sides[_count] = (byte)trade.Side; _quality[_count] = Index(provenance);
            if (!string.IsNullOrEmpty(provenance.SourceEventId))
            {
                _eventIds ??= new string?[_times.Length];
                _eventIds[_count] = provenance.SourceEventId;
                _eventIdTextBytes += 32L + 2L * provenance.SourceEventId.Length;
            }
            _lastProvenance = provenance;
            _count++;
        }

        private void FlushProvenance()
        {
            if (!_pendingProvenance) return;
            _quality[_count - 1] = Index(_lastProvenance);
            _pendingProvenance = false;
        }

        private ushort Index(TradeProvenance provenance)
        {
            provenance = provenance with { SourceEventId = null };
            if (_indices.TryGetValue(provenance, out ushort index)) return index;
            if (_provenance.Count > ushort.MaxValue) throw new InvalidDataException("The range contains too many distinct provenance revisions; select a shorter range.");
            index = (ushort)_provenance.Count; _provenance.Add(provenance); _indices.Add(provenance, index); return index;
        }

        public TickData Finish(bool compressed, IbHistoryStamp? storageStamp = null)
        {
            FlushProvenance();
            Array.Resize(ref _ids, _count); Array.Resize(ref _times, _count); Array.Resize(ref _prices, _count); Array.Resize(ref _volumes, _count); Array.Resize(ref _sides, _count); Array.Resize(ref _quality, _count);
            if (_eventIds != null) Array.Resize(ref _eventIds, _count);
            return new TickData(_instrument, _ids, _times, _prices, _volumes, _sides, _quality, _provenance.ToArray(), _eventIds, _eventIdTextBytes, 0, _count, compressed, storageStamp: storageStamp);
        }
    }
}
