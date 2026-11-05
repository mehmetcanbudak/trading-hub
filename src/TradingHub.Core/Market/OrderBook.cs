namespace TradingHub.Market;

public enum BookSide : byte
{
    Bid = 0,
    Ask = 1,
}

/// <summary>A resting book level: exact price ticks and decimal size.</summary>
public readonly record struct BookLevel(long PriceTicks, decimal Size);

public enum BookChangeOrigin : byte { Received, DerivedSnapshotDifference, BookRepair }

/// <summary>An absolute-size change; zero removes the level. Origin distinguishes feed input from local repair.</summary>
public readonly record struct BookChange(BookSide Side, long PriceTicks, decimal Size, BookChangeOrigin Origin = BookChangeOrigin.Received);

[Flags]
public enum DepthCoverage
{
    None = 0,
    SnapshotOnly = 1,
    ReceivedChanges = 2,
    PartialBook = 4,
    SequenceGap = 8,
    Disconnected = 16,
    Truncated = 32,
    Missing = 64,
    OutOfOrder = 128,
    /// <summary>Validated levels on one known side; Missing still marks the unknown opposing side.</summary>
    KnownSide = 256,
}

public enum DepthGapReason : byte
{
    None,
    Disconnected,
    Sequence,
    FeedChanged,
    Overflow,
    StorageFailure,
    Missing,
}

/// <summary>
/// A feed snapshot. Bids descend and asks ascend. A snapshot describes only the
/// levels supplied by its provider, never an event-complete or market-by-order book.
/// </summary>
public sealed class DepthSnapshot
{
    public static readonly DepthSnapshot Empty = new(Array.Empty<BookLevel>(), Array.Empty<BookLevel>(), DateTime.MinValue);

    public BookLevel[] Bids { get; }
    public BookLevel[] Asks { get; }
    public DateTime Time { get; }

    public DepthSnapshot(BookLevel[] bids, BookLevel[] asks, DateTime time)
    {
        Bids = bids;
        Asks = asks;
        Time = time;
    }

    public bool HasBid => Bids.Length > 0;
    public bool HasAsk => Asks.Length > 0;
    public long BestBidTicks => Bids.Length > 0 ? Bids[0].PriceTicks : long.MinValue;
    public long BestAskTicks => Asks.Length > 0 ? Asks[0].PriceTicks : long.MaxValue;
    public decimal BestBidSize => Bids.Length > 0 ? Bids[0].Size : 0;
    public decimal BestAskSize => Asks.Length > 0 ? Asks[0].Size : 0;

    /// <summary>Tamaño en un precio exacto (búsqueda binaria).</summary>
    public decimal GetSize(BookSide side, long priceTicks)
    {
        var arr = side == BookSide.Bid ? Bids : Asks;
        int lo = 0, hi = arr.Length - 1;
        bool descending = side == BookSide.Bid;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            long p = arr[mid].PriceTicks;
            if (p == priceTicks) return arr[mid].Size;
            bool goRight = descending ? p > priceTicks : p < priceTicks;
            if (goRight) lo = mid + 1; else hi = mid - 1;
        }
        return 0;
    }

    /// <summary>Suma de tamaños en el rango [fromTicks, toTicks] (inclusive) de un lado.</summary>
    public decimal SumRange(BookSide side, long fromTicks, long toTicks)
    {
        if (fromTicks > toTicks) (fromTicks, toTicks) = (toTicks, fromTicks);
        decimal sum = 0;
        var arr = side == BookSide.Bid ? Bids : Asks;
        foreach (var l in arr)
        {
            if (l.PriceTicks >= fromTicks && l.PriceTicks <= toTicks) sum += l.Size;
        }
        return sum;
    }
}

/// <summary>A provider update: its resulting snapshot and the received absolute-size changes.</summary>
public sealed class DepthUpdate
{
    public required DepthSnapshot Snapshot { get; init; }
    public IReadOnlyList<BookChange> Changes { get; init; } = Array.Empty<BookChange>();
    public bool IsFullRefresh { get; init; }
    public string? ProviderId { get; init; }
    public string? Venue { get; init; }
    /// <summary>UTC arrival/availability time; the receiver supplies it when absent.</summary>
    public DateTime ReceivedAt { get; init; }
    public long? Sequence { get; init; }
    public long? FirstSequence { get; init; }
    public long? PreviousSequence { get; init; }
    public DepthCoverage Coverage { get; init; } = DepthCoverage.SnapshotOnly;
    public DepthGapReason GapBefore { get; init; }
    public string? Message { get; init; }
    /// <summary>Known levels may be displayed without treating a missing opposing side as zero.</summary>
    public bool HasAvailableLevels => (Snapshot.HasBid || Snapshot.HasAsk) &&
        (Coverage & (DepthCoverage.Disconnected | DepthCoverage.OutOfOrder)) == 0 &&
        (!Coverage.HasFlag(DepthCoverage.Missing) || Coverage.HasFlag(DepthCoverage.KnownSide) && Snapshot.HasBid != Snapshot.HasAsk) &&
        (!Coverage.HasFlag(DepthCoverage.SequenceGap) || IsFullRefresh || GapBefore is DepthGapReason.Overflow or DepthGapReason.StorageFailure);
    public bool IsUsable => Snapshot.HasBid && Snapshot.HasAsk && HasAvailableLevels;
}

/// <summary>
/// Libro mutable que mantienen los feeds (un solo hilo). Genera <see cref="DepthSnapshot"/>.
/// </summary>
public sealed class OrderBookBuilder
{
    private readonly SortedDictionary<long, decimal> _bids = new(Comparer<long>.Create((a, b) => b.CompareTo(a)));
    private readonly SortedDictionary<long, decimal> _asks = new();
    private readonly List<BookChange> _pending = new();
    private bool _repaired;

    public int BidCount => _bids.Count;
    public int AskCount => _asks.Count;

    public void Clear()
    {
        _bids.Clear();
        _asks.Clear();
        _pending.Clear();
        _repaired = false;
    }

    public void Set(BookSide side, long priceTicks, decimal size, BookChangeOrigin origin = BookChangeOrigin.Received)
    {
        var dict = side == BookSide.Bid ? _bids : _asks;
        if (size <= 0) dict.Remove(priceTicks);
        else dict[priceTicks] = size;
        // Preserve repeated/no-op received absolute updates as well as effective changes.
        _pending.Add(new BookChange(side, priceTicks, Math.Max(0, size), origin));
        if (origin == BookChangeOrigin.BookRepair) _repaired = true;
    }

    /// <summary>Elimina niveles cruzados (puede pasar con feeds que mandan bid/ask por separado).</summary>
    public void Uncross(BookSide aggressorSideUpdated)
    {
        if (_bids.Count == 0 || _asks.Count == 0) return;
        long bestBid = _bids.Keys.First();
        long bestAsk = _asks.Keys.First();
        if (bestBid < bestAsk) return;
        if (aggressorSideUpdated == BookSide.Bid)
        {
            foreach (var k in _asks.Keys.TakeWhile(k => k <= bestBid).ToList()) Set(BookSide.Ask, k, 0, BookChangeOrigin.BookRepair);
        }
        else
        {
            foreach (var k in _bids.Keys.TakeWhile(k => k >= bestAsk).ToList()) Set(BookSide.Bid, k, 0, BookChangeOrigin.BookRepair);
        }
    }

    public DepthUpdate BuildUpdate(DateTime time, int maxLevelsPerSide = 1000, bool fullRefresh = false,
        string? providerId = null, string? venue = null, long? sequence = null,
        long? firstSequence = null, long? previousSequence = null,
        DepthCoverage additionalCoverage = DepthCoverage.None, DepthGapReason gapBefore = DepthGapReason.None,
        string? message = null, DateTime receivedAt = default)
    {
        if (maxLevelsPerSide < 1) throw new ArgumentOutOfRangeException(nameof(maxLevelsPerSide));
        var bids = new BookLevel[Math.Min(maxLevelsPerSide, _bids.Count)];
        int i = 0;
        foreach (var kv in _bids)
        {
            if (i >= bids.Length) break;
            bids[i++] = new BookLevel(kv.Key, kv.Value);
        }

        var asks = new BookLevel[Math.Min(maxLevelsPerSide, _asks.Count)];
        i = 0;
        foreach (var kv in _asks)
        {
            if (i >= asks.Length) break;
            asks[i++] = new BookLevel(kv.Key, kv.Value);
        }

        var changes = _pending.ToArray();
        _pending.Clear();
        var coverage = DepthCoverage.ReceivedChanges | (additionalCoverage & ~DepthCoverage.KnownSide);
        bool repaired = _repaired;
        _repaired = false;
        if (repaired) coverage |= DepthCoverage.PartialBook;
        if (_bids.Count > maxLevelsPerSide || _asks.Count > maxLevelsPerSide) coverage |= DepthCoverage.PartialBook | DepthCoverage.Truncated;
        if (bids.Length == 0 || asks.Length == 0)
        {
            if ((bids.Length > 0 || asks.Length > 0) && !coverage.HasFlag(DepthCoverage.Missing))
                coverage |= DepthCoverage.KnownSide;
            coverage |= DepthCoverage.PartialBook | DepthCoverage.Missing;
        }
        return new DepthUpdate
        {
            Snapshot = new DepthSnapshot(bids, asks, time), Changes = changes, IsFullRefresh = fullRefresh,
            ProviderId = providerId, Venue = venue, Sequence = sequence, FirstSequence = firstSequence,
            PreviousSequence = previousSequence, Coverage = coverage, GapBefore = gapBefore, ReceivedAt = receivedAt,
            Message = repaired ? "Crossed levels were removed locally; BookRepair changes are not received exchange events. " + message : message,
        };
    }
}
