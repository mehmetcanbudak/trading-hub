using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Almacén columnar en memoria de todas las operaciones de un instrumento (historia + vivo).
/// Las velas de cualquier período se reconstruyen desde acá en milisegundos, así que cambiar
/// de 1m a 5m o de velas de tiempo a velas de rango no requiere volver a descargar nada.
/// Solo se usa desde el hilo de UI.
/// </summary>
public sealed class TradeStore
{
    private const int ChunkBits = 16;
    private const int ChunkSize = 1 << ChunkBits;
    private const int ChunkMask = ChunkSize - 1;

    private readonly List<Chunk> _chunks = new();
    private readonly Instrument _instrument;
    private readonly List<TradeProvenance> _provenance = new() { new(DataQuality.Raw) };
    private readonly Dictionary<TradeProvenance, ushort> _provenanceIds = new() { [new(DataQuality.Raw)] = 0 };
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private Dictionary<TradeIdentity, int>? _identityIndices;

    public Guid Identity { get; } = Guid.NewGuid();
    public long Revision { get; private set; }
    public long CorrectionRevision { get; private set; }

    public TradeStore(Instrument instrument)
    {
        _instrument = instrument;
    }

    public Instrument Instrument => _instrument;
    public int Count { get; private set; }
    public long LastId { get; private set; } = -1;
    public DateTime FirstTime => Count > 0 ? GetTime(0) : DateTime.MinValue;
    public DateTime LastTime => Count > 0 ? GetTime(Count - 1) : DateTime.MinValue;

    private sealed class Chunk
    {
        public readonly long[] Ids = new long[ChunkSize];
        public readonly long[] Times = new long[ChunkSize];
        public readonly long[] Prices = new long[ChunkSize];
        public readonly decimal[] Volumes = new decimal[ChunkSize];
        public readonly byte[] Sides = new byte[ChunkSize];
        public readonly ushort[] Provenance = new ushort[ChunkSize];
        public string?[]? EventIds;
        public long EventIdTextBytes;
        public void SetEventId(int offset, string? eventId)
        {
            if (string.IsNullOrEmpty(eventId)) eventId = null;
            if (EventIds == null && eventId != null) EventIds = new string?[ChunkSize];
            if (EventIds == null) return;
            string? previous = EventIds[offset];
            EventIdTextBytes += (eventId == null ? 0 : 32L + 2L * eventId.Length) - (previous == null ? 0 : 32L + 2L * previous.Length);
            EventIds[offset] = eventId;
        }
    }

    public void Add(in Trade t)
    {
        long price = _instrument.ToTicks(t.Price);
        ushort provenance = ProvenanceId(t.Provenance);
        int idx = Count;
        int c = idx >> ChunkBits;
        if (c == _chunks.Count) _chunks.Add(new Chunk());
        var ch = _chunks[c];
        int o = idx & ChunkMask;
        ch.Ids[o] = t.Id;
        ch.Times[o] = t.Time.Ticks;
        ch.Prices[o] = price;
        ch.Volumes[o] = t.Volume;
        ch.Sides[o] = (byte)t.Side;
        ch.Provenance[o] = provenance;
        ch.SetEventId(o, t.Provenance.SourceEventId);
        Revision++;
        Count++;
        if (_identityIndices != null) _identityIndices[GetIdentity(Count - 1)] = Count - 1;
        if (t.Id > LastId) LastId = t.Id;
    }

    public void Clear()
    {
        _chunks.Clear();
        Count = 0;
        LastId = -1;
        _provenance.Clear();
        _provenance.Add(new TradeProvenance(DataQuality.Raw));
        _provenanceIds.Clear();
        _provenanceIds[new TradeProvenance(DataQuality.Raw)] = 0;
        _sources.Clear();
        _identityIndices = null;
        Revision++;
        CorrectionRevision++;
    }

    public long GetId(int i) => _chunks[i >> ChunkBits].Ids[i & ChunkMask];
    public DateTime GetTime(int i) => new(_chunks[i >> ChunkBits].Times[i & ChunkMask], DateTimeKind.Utc);
    public long GetTimeTicks(int i) => _chunks[i >> ChunkBits].Times[i & ChunkMask];
    public long GetPriceTicks(int i) => _chunks[i >> ChunkBits].Prices[i & ChunkMask];
    public decimal GetPrice(int i) => _instrument.FromTicks(GetPriceTicks(i));
    public decimal GetVolume(int i) => _chunks[i >> ChunkBits].Volumes[i & ChunkMask];
    public TradeSide GetSide(int i) => (TradeSide)_chunks[i >> ChunkBits].Sides[i & ChunkMask];
    public TradeProvenance GetProvenance(int i)
    {
        var chunk = _chunks[i >> ChunkBits];
        int offset = i & ChunkMask;
        var provenance = _provenance[chunk.Provenance[offset]];
        return chunk.EventIds?[offset] is { } eventId ? provenance with { SourceEventId = eventId } : provenance;
    }

    public TradeIdentity GetIdentity(int index) => TradeIdentity.From(GetId(index), GetTime(index), GetProvenance(index));

    public int FindIdentity(TradeIdentity identity)
    {
        if (_identityIndices == null)
        {
            _identityIndices = new Dictionary<TradeIdentity, int>(Count);
            for (int index = 0; index < Count; index++) _identityIndices[GetIdentity(index)] = index;
        }
        return _identityIndices.TryGetValue(identity, out int found) ? found : -1;
    }

    private ushort ProvenanceId(TradeProvenance provenance)
    {
        provenance = provenance with { SourceEventId = null };
        if (provenance.SourceIdentity is { } source)
        {
            if (!_sources.TryGetValue(source, out var shared)) _sources.Add(source, shared = source);
            provenance = provenance with { SourceIdentity = shared };
        }
        if (_provenanceIds.TryGetValue(provenance, out var index)) return index;
        if (_provenance.Count > ushort.MaxValue)
            throw new InvalidOperationException("Too many distinct source revisions in one history store.");
        index = (ushort)_provenance.Count;
        _provenance.Add(provenance);
        _provenanceIds.Add(provenance, index);
        return index;
    }

    /// <summary>Insert a newly available event at its original chart location and invalidate index-based aggregations.</summary>
    public int InsertChronological(in Trade trade)
    {
        if (Count == 0 || trade.Time >= LastTime)
        {
            int appended = Count;
            Add(trade);
            return appended;
        }
        int index = LowerBound(trade.Time);
        while (index < Count && GetTime(index) == trade.Time && GetId(index) <= trade.Id) index++;
        Add(trade);
        var tail = _chunks[(Count - 1) >> ChunkBits];
        int tailOffset = (Count - 1) & ChunkMask;
        long id = tail.Ids[tailOffset], time = tail.Times[tailOffset], price = tail.Prices[tailOffset];
        decimal volume = tail.Volumes[tailOffset];
        byte side = tail.Sides[tailOffset];
        ushort provenance = tail.Provenance[tailOffset];
        string? eventId = tail.EventIds?[tailOffset];
        for (int position = Count - 1; position > index; position--)
        {
            var source = _chunks[(position - 1) >> ChunkBits];
            int sourceOffset = (position - 1) & ChunkMask;
            var destination = _chunks[position >> ChunkBits];
            int destinationOffset = position & ChunkMask;
            destination.Ids[destinationOffset] = source.Ids[sourceOffset];
            destination.Times[destinationOffset] = source.Times[sourceOffset];
            destination.Prices[destinationOffset] = source.Prices[sourceOffset];
            destination.Volumes[destinationOffset] = source.Volumes[sourceOffset];
            destination.Sides[destinationOffset] = source.Sides[sourceOffset];
            destination.Provenance[destinationOffset] = source.Provenance[sourceOffset];
            destination.SetEventId(destinationOffset, source.EventIds?[sourceOffset]);
        }
        var inserted = _chunks[index >> ChunkBits];
        int offset = index & ChunkMask;
        inserted.Ids[offset] = id; inserted.Times[offset] = time; inserted.Prices[offset] = price;
        inserted.Volumes[offset] = volume; inserted.Sides[offset] = side; inserted.Provenance[offset] = provenance;
        inserted.SetEventId(offset, eventId);
        CorrectionRevision++;
        if (_identityIndices != null)
            for (int position = index; position < Count; position++) _identityIndices[GetIdentity(position)] = position;
        return index;
    }

    /// <summary>Replace a corrected event without invalidating unrelated source identities.</summary>
    public void Update(int index, in Trade trade)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if ((index > 0 && trade.Time < GetTime(index - 1)) || (index + 1 < Count && trade.Time > GetTime(index + 1)))
            throw new ArgumentException("Corrections must preserve chronological order; use Replace for moved events.", nameof(trade));
        var previousIdentity = _identityIndices == null ? default : GetIdentity(index);
        long price = _instrument.ToTicks(trade.Price);
        ushort provenance = ProvenanceId(trade.Provenance);
        var chunk = _chunks[index >> ChunkBits];
        int offset = index & ChunkMask;
        long previousId = chunk.Ids[offset];
        chunk.Ids[offset] = trade.Id;
        chunk.Times[offset] = trade.Time.Ticks;
        chunk.Prices[offset] = price;
        chunk.Volumes[offset] = trade.Volume;
        chunk.Sides[offset] = (byte)trade.Side;
        chunk.Provenance[offset] = provenance;
        chunk.SetEventId(offset, trade.Provenance.SourceEventId);
        if (trade.Id >= LastId) LastId = trade.Id;
        else if (previousId == LastId)
        {
            LastId = -1;
            for (int i = 0; i < Count; i++) LastId = Math.Max(LastId, GetId(i));
        }
        Revision++;
        CorrectionRevision++;
        if (_identityIndices != null)
        {
            var identity = GetIdentity(index);
            if (identity != previousIdentity) _identityIndices.Remove(previousIdentity);
            _identityIndices[identity] = index;
        }
    }

    public void Replace(IEnumerable<Trade> trades)
    {
        Clear();
        foreach (var trade in trades) Add(trade);
    }

    public Trade Get(int i)
    {
        var ch = _chunks[i >> ChunkBits];
        int o = i & ChunkMask;
        return new Trade(ch.Ids[o], new DateTime(ch.Times[o], DateTimeKind.Utc), _instrument.FromTicks(ch.Prices[o]), ch.Volumes[o], (TradeSide)ch.Sides[o], GetProvenance(i));
    }

    /// <summary>Primer índice con tiempo &gt;= time (búsqueda binaria).</summary>
    public int LowerBound(DateTime time)
    {
        long t = time.Ticks;
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (GetTimeTicks(mid) < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Estimación de memoria usada (bytes).</summary>
    public long MemoryBytes
    {
        get
        {
            long bytes = (long)_chunks.Count * ChunkSize * (8 + 8 + 8 + 16 + 1 + 2);
            if (_identityIndices != null) bytes += 64L * _identityIndices.EnsureCapacity(0);
            foreach (var chunk in _chunks)
                if (chunk.EventIds != null) bytes += (long)ChunkSize * IntPtr.Size + chunk.EventIdTextBytes;
            return bytes;
        }
    }
}
