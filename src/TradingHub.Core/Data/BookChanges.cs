// Behaviour adapted from gouqizi2214730-source/trading-hub f041ff6 (MIT with Commons Clause; see THIRD_PARTY_NOTICES.md),
// rebuilt on recorded received DepthSamples with explicit gap/staleness/source checks and consumable trade attribution.
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Resting-size changes at one price between compared received snapshots. Added and removed sizes are net
/// differences between consecutive snapshots, not individual orders: an order added and removed between two
/// snapshots is never seen. Removed size is split into Filled (explained by aggressor trades at that price) and
/// Pulled (the unexplained rest); that split is a heuristic attribution, not order-level evidence.
/// </summary>
public sealed class BookLevelChange
{
    public decimal BidAdded { get; set; }
    public decimal BidPulled { get; set; }
    public decimal BidFilled { get; set; }
    public decimal AskAdded { get; set; }
    public decimal AskPulled { get; set; }
    public decimal AskFilled { get; set; }

    public decimal Added => BidAdded + AskAdded;
    public decimal Pulled => BidPulled + AskPulled;
    public decimal Filled => BidFilled + AskFilled;

    /// <summary>Net resting-size change: added minus everything removed.</summary>
    public decimal Net => Added - Pulled - Filled;

    public void Add(BookLevelChange other)
    {
        BidAdded += other.BidAdded; BidPulled += other.BidPulled; BidFilled += other.BidFilled;
        AskAdded += other.AskAdded; AskPulled += other.AskPulled; AskFilled += other.AskFilled;
    }
}

/// <summary>An aggressor trade used for fill attribution. Sell aggressors consume bids; buy aggressors consume asks.</summary>
public readonly record struct BookTrade(DateTime Time, long PriceTicks, decimal Volume, TradeSide Side);

public enum BookTradeAttributionKind
{
    /// <summary>No trade information: removed size cannot be split; it is reported as pulled and labelled.</summary>
    None,
    /// <summary>Individual timestamped trades, matched to the snapshot interval in which they occurred (± clock skew).</summary>
    Timed,
    /// <summary>Per-price totals for a whole interval (for example one bar's observed footprint), without timing.</summary>
    Pooled,
}

/// <summary>
/// Aggressor volume available to explain removed resting size. Every unit of volume explains at most one removal,
/// so repeated snapshots of the same level never double count one trade.
/// </summary>
public abstract class BookTradeAttribution
{
    public abstract BookTradeAttributionKind Kind { get; }

    /// <summary>Consumes up to <paramref name="max"/> of aggressor volume that hit <paramref name="side"/> at the price.</summary>
    public abstract decimal Take(BookSide side, long priceTicks, DateTime from, DateTime to, decimal max);

    /// <summary>Total aggressor volume against <paramref name="side"/> at the price in [from, to], without consuming it.</summary>
    public abstract decimal Peek(BookSide side, long priceTicks, DateTime from, DateTime to);

    /// <summary>False when trade data for [from, to] is missing, so trading cannot be told from pulling there.</summary>
    public virtual bool Covers(DateTime from, DateTime to) => true;

    protected static BookSide ConsumedSide(TradeSide aggressor) => aggressor == TradeSide.Sell ? BookSide.Bid : BookSide.Ask;
}

/// <summary>Timestamped trades. Trade and book streams use different clocks, so <c>skew</c> widens each interval.</summary>
public sealed class TimedBookTrades : BookTradeAttribution
{
    private readonly Dictionary<(BookSide, long), List<(DateTime Time, decimal Left)>> _trades = new();
    private readonly TimeSpan _skew;

    public TimedBookTrades(IEnumerable<BookTrade> trades, TimeSpan skew)
    {
        _skew = skew < TimeSpan.Zero ? TimeSpan.Zero : skew;
        foreach (var trade in trades)
        {
            if (trade.Volume <= 0 || trade.Side == TradeSide.None) continue;
            var key = (ConsumedSide(trade.Side), trade.PriceTicks);
            if (!_trades.TryGetValue(key, out var list)) _trades[key] = list = new();
            list.Add((trade.Time, trade.Volume));
        }
        foreach (var list in _trades.Values) list.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    public override BookTradeAttributionKind Kind => BookTradeAttributionKind.Timed;

    public override decimal Take(BookSide side, long priceTicks, DateTime from, DateTime to, decimal max)
    {
        if (max <= 0 || !_trades.TryGetValue((side, priceTicks), out var list)) return 0;
        DateTime lo = from - _skew, hi = to + _skew;
        decimal taken = 0;
        for (int i = 0; i < list.Count && taken < max; i++)
        {
            var (time, left) = list[i];
            if (time > hi) break;
            if (time <= lo || left <= 0) continue;
            decimal use = Math.Min(left, max - taken);
            list[i] = (time, left - use);
            taken += use;
        }
        return taken;
    }

    public override decimal Peek(BookSide side, long priceTicks, DateTime from, DateTime to)
    {
        if (!_trades.TryGetValue((side, priceTicks), out var list)) return 0;
        DateTime lo = from - _skew, hi = to + _skew;
        decimal sum = 0;
        foreach (var (time, left) in list)
            if (time > lo && time <= hi) sum += left;
        return sum;
    }
}

/// <summary>Per-price totals without timing (e.g. a bar's observed footprint); consumed across the whole interval.</summary>
public sealed class PooledBookTrades : BookTradeAttribution
{
    private readonly Dictionary<(BookSide, long), decimal> _left = new();

    public override BookTradeAttributionKind Kind => BookTradeAttributionKind.Pooled;

    /// <param name="sellAggressor">Volume traded at the bid (consumes bids).</param>
    /// <param name="buyAggressor">Volume traded at the ask (consumes asks).</param>
    public void Add(long priceTicks, decimal sellAggressor, decimal buyAggressor)
    {
        if (sellAggressor > 0) _left[(BookSide.Bid, priceTicks)] = _left.GetValueOrDefault((BookSide.Bid, priceTicks)) + sellAggressor;
        if (buyAggressor > 0) _left[(BookSide.Ask, priceTicks)] = _left.GetValueOrDefault((BookSide.Ask, priceTicks)) + buyAggressor;
    }

    public override decimal Take(BookSide side, long priceTicks, DateTime from, DateTime to, decimal max)
    {
        if (max <= 0 || !_left.TryGetValue((side, priceTicks), out var left) || left <= 0) return 0;
        decimal use = Math.Min(left, max);
        _left[(side, priceTicks)] = left - use;
        return use;
    }

    public override decimal Peek(BookSide side, long priceTicks, DateTime from, DateTime to) =>
        _left.GetValueOrDefault((side, priceTicks));
}

public sealed record BookChangeOptions
{
    /// <summary>Price ticks per output row (1 = exact price).</summary>
    public long GroupTicks { get; init; } = 1;

    /// <summary>Consecutive snapshots further apart are not compared: the book between them was not observed.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Compare provider-partial or truncated books (within the levels both snapshots show).</summary>
    public bool AllowPartialBook { get; init; } = true;
}

/// <summary>Book changes over an interval plus the sampling facts needed to read them honestly.</summary>
public sealed record BookChangesResult(
    IReadOnlyDictionary<long, BookLevelChange> Levels,
    int Samples,
    int Comparisons,
    int SkippedTransitions,
    TimeSpan ComparedDuration,
    TimeSpan LongestComparedInterval,
    DepthCoverage Coverage,
    BookTradeAttributionKind Attribution,
    string? Message)
{
    public static readonly BookChangesResult Empty = new(new Dictionary<long, BookLevelChange>(), 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero,
        DepthCoverage.Missing, BookTradeAttributionKind.None, "No recorded depth snapshots in this interval.");

    public bool IsAvailable => Comparisons > 0;
}

/// <summary>What happened to a tracked large resting level once it disappeared.</summary>
public enum LargeOrderOutcome
{
    /// <summary>Still visible in the last compared snapshot.</summary>
    Open,
    /// <summary>Mostly explained by aggressor trades at its price.</summary>
    Traded,
    /// <summary>Removed without matching trades (a quick pull is the spoofing heuristic, never proof).</summary>
    Pulled,
    /// <summary>Removed while a similar new level appeared nearby in the direction the price moved (chasing heuristic).</summary>
    Moved,
    /// <summary>Removed, but no trade information exists to tell trading from pulling.</summary>
    Removed,
    /// <summary>Its fate was not observed: a gap, stale interval, source change or the price left the visible book.</summary>
    Unobserved,
}

/// <summary>A large resting level (one price and side, not one order) from first to last observation.</summary>
public sealed class LargeOrderTrack
{
    public BookSide Side { get; init; }
    public long PriceTicks { get; init; }
    public decimal MaxSize { get; set; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; set; }
    public DateTime? EndedAt { get; set; }
    public LargeOrderOutcome Outcome { get; set; } = LargeOrderOutcome.Open;
    public long? MovedToTicks { get; set; }
    public decimal TradedAtPrice { get; set; }

    public TimeSpan Lifetime => (EndedAt ?? LastSeen) - FirstSeen;

    /// <summary>Spoofing heuristic: pulled without matching trades within <paramref name="within"/>. Never proof of intent.</summary>
    public bool IsQuickPull(TimeSpan within) => Outcome == LargeOrderOutcome.Pulled && Lifetime <= within;
}

public sealed record LargeOrderOptions
{
    public decimal MinSize { get; init; }
    public int MaxMoveTicks { get; init; } = 50;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromSeconds(10);
    public bool AllowPartialBook { get; init; } = true;

    /// <summary>Share of the level's size that trades must explain for <see cref="LargeOrderOutcome.Traded"/>.</summary>
    public decimal TradedFraction { get; init; } = 0.5m;

    /// <summary>Relative size tolerance for a moved (chasing) twin.</summary>
    public decimal TwinTolerance { get; init; } = 0.3m;
}

/// <summary>
/// Order-book change analytics over recorded received L2 snapshots. Only consecutive snapshots from one recording,
/// provider and venue, without a gap and no further apart than MaxAge, are compared; everything else is counted
/// as skipped. Results describe snapshot differences, never market-by-order events.
/// </summary>
public static class BookChanges
{
    public static BookChangesResult Compute(IReadOnlyList<DepthSample> samples, DepthSample? preceding,
        BookTradeAttribution? trades, BookChangeOptions? options = null)
    {
        options ??= new BookChangeOptions();
        long group = Math.Max(1, options.GroupTicks);
        var levels = new Dictionary<long, BookLevelChange>();
        int usable = 0, comparisons = 0, skipped = 0;
        long compared = 0, longest = 0;
        var coverage = DepthCoverage.None;
        DepthSample? previous = preceding;
        foreach (var current in samples)
        {
            coverage |= current.Coverage;
            if (current.IsUsable) usable++;
            if (previous != null)
            {
                if (Comparable(previous, current, options.MaxAge, options.AllowPartialBook))
                {
                    comparisons++;
                    long ticks = (current.Time - previous.Time).Ticks;
                    compared += ticks;
                    longest = Math.Max(longest, ticks);
                    CompareSide(previous, current, BookSide.Bid, trades, levels, group);
                    CompareSide(previous, current, BookSide.Ask, trades, levels, group);
                }
                else skipped++;
            }
            previous = current;
        }
        if (samples.Count == 0) return BookChangesResult.Empty;
        if (comparisons == 0) coverage |= DepthCoverage.Missing;
        string? message = comparisons == 0 ? "No comparable consecutive snapshots (gaps, stale intervals or source changes)."
            : skipped > 0 ? "Some transitions were skipped (gaps, stale intervals or source changes); changes across them are unknown."
            : trades == null ? "No trade data: removed size includes fills."
            : null;
        return new(levels, usable, comparisons, skipped, TimeSpan.FromTicks(compared), TimeSpan.FromTicks(longest), coverage,
            trades?.Kind ?? BookTradeAttributionKind.None, message);
    }

    /// <summary>True when the interval between two received snapshots was observed by one uninterrupted source.</summary>
    public static bool Comparable(DepthSample a, DepthSample b, TimeSpan maxAge, bool allowPartial)
    {
        const DepthCoverage broken = DepthCoverage.SequenceGap | DepthCoverage.Missing | DepthCoverage.OutOfOrder | DepthCoverage.Disconnected;
        if (!a.IsUsable || !b.IsUsable || a.RecordingId != b.RecordingId) return false;
        if (a.ProviderId != b.ProviderId || a.Venue != b.Venue || b.GapBefore != DepthGapReason.None) return false;
        if ((b.Coverage & broken) != 0) return false;
        if (b.Time < a.Time || b.Time - a.Time > maxAge) return false;
        return allowPartial || ((a.Coverage | b.Coverage) & (DepthCoverage.PartialBook | DepthCoverage.Truncated)) == 0;
    }

    /// <summary>Inclusive price range visible on one side in both snapshots; outside it a level can vanish just by leaving view.</summary>
    public static (long Lo, long Hi) Covered(DepthSnapshot a, DepthSnapshot b, BookSide side)
    {
        if (side == BookSide.Bid)
            return (Math.Max(a.Bids[^1].PriceTicks, b.Bids[^1].PriceTicks), Math.Max(a.BestBidTicks, b.BestBidTicks));
        return (Math.Min(a.BestAskTicks, b.BestAskTicks), Math.Min(a.Asks[^1].PriceTicks, b.Asks[^1].PriceTicks));
    }

    private static void CompareSide(DepthSample a, DepthSample b, BookSide side, BookTradeAttribution? trades,
        Dictionary<long, BookLevelChange> levels, long group)
    {
        var (lo, hi) = Covered(a.Snapshot, b.Snapshot, side);
        if (hi < lo) return;
        var before = side == BookSide.Bid ? a.Snapshot.Bids : a.Snapshot.Asks;
        var after = side == BookSide.Bid ? b.Snapshot.Bids : b.Snapshot.Asks;
        foreach (var level in before)
        {
            if (level.PriceTicks < lo || level.PriceTicks > hi) continue;
            decimal now = b.Snapshot.GetSize(side, level.PriceTicks);
            if (now >= level.Size) continue;
            decimal removed = level.Size - now;
            decimal filled = trades?.Take(side, level.PriceTicks, a.Time, b.Time, removed) ?? 0;
            var change = At(levels, level.PriceTicks, group);
            if (side == BookSide.Bid) { change.BidFilled += filled; change.BidPulled += removed - filled; }
            else { change.AskFilled += filled; change.AskPulled += removed - filled; }
        }
        foreach (var level in after)
        {
            if (level.PriceTicks < lo || level.PriceTicks > hi) continue;
            decimal was = a.Snapshot.GetSize(side, level.PriceTicks);
            if (level.Size <= was) continue;
            var change = At(levels, level.PriceTicks, group);
            if (side == BookSide.Bid) change.BidAdded += level.Size - was;
            else change.AskAdded += level.Size - was;
        }
    }

    private static BookLevelChange At(Dictionary<long, BookLevelChange> levels, long priceTicks, long group)
    {
        long key = CandleSeries.FloorDiv(priceTicks, group) * group;
        if (!levels.TryGetValue(key, out var change)) levels[key] = change = new BookLevelChange();
        return change;
    }

    /// <summary>
    /// Follows resting levels of at least MinSize across comparable snapshots. A level that vanishes is classified as
    /// traded, pulled, moved (a similar new level appeared nearby in the direction the best price moved) or removed when
    /// no trade source exists. Tracks crossing a gap, stale interval or source change, or leaving the visible book, are
    /// closed as unobserved instead of judged.
    /// </summary>
    public static List<LargeOrderTrack> TrackLarge(IReadOnlyList<DepthSample> samples, LargeOrderOptions options, BookTradeAttribution? trades)
    {
        var all = new List<LargeOrderTrack>();
        if (options.MinSize <= 0) return all;
        var live = new Dictionary<(BookSide, long), LargeOrderTrack>();
        DepthSample? previous = null;
        foreach (var sample in samples)
        {
            if (!sample.IsUsable)
            {
                CloseUnobserved(live);
                previous = null;
                continue;
            }
            if (previous != null && !Comparable(previous, sample, options.MaxAge, options.AllowPartialBook))
            {
                CloseUnobserved(live);
                previous = null;
            }

            var seen = new HashSet<(BookSide, long)>();
            var born = new List<LargeOrderTrack>();
            foreach (var side in new[] { BookSide.Bid, BookSide.Ask })
            {
                foreach (var level in side == BookSide.Bid ? sample.Snapshot.Bids : sample.Snapshot.Asks)
                {
                    if (level.Size < options.MinSize) continue;
                    var key = (side, level.PriceTicks);
                    seen.Add(key);
                    if (live.TryGetValue(key, out var track))
                    {
                        track.LastSeen = sample.Time;
                        track.MaxSize = Math.Max(track.MaxSize, level.Size);
                        continue;
                    }
                    track = new LargeOrderTrack { Side = side, PriceTicks = level.PriceTicks, MaxSize = level.Size, FirstSeen = sample.Time, LastSeen = sample.Time };
                    live[key] = track;
                    all.Add(track);
                    if (previous != null) born.Add(track);
                }
            }

            if (previous != null)
            {
                var twinsUsed = new HashSet<LargeOrderTrack>();
                foreach (var (key, track) in live.ToList())
                {
                    if (seen.Contains(key)) continue;
                    live.Remove(key);
                    track.EndedAt = sample.Time;
                    var (lo, hi) = Covered(previous.Snapshot, sample.Snapshot, track.Side);
                    if (track.PriceTicks < lo || track.PriceTicks > hi)
                    {
                        track.Outcome = LargeOrderOutcome.Unobserved;
                        continue;
                    }
                    if (trades == null || !trades.Covers(track.FirstSeen, sample.Time))
                    {
                        track.Outcome = LargeOrderOutcome.Removed;
                        continue;
                    }
                    track.TradedAtPrice = trades.Peek(track.Side, track.PriceTicks, track.FirstSeen, sample.Time);
                    if (track.TradedAtPrice >= track.MaxSize * options.TradedFraction)
                    {
                        track.Outcome = LargeOrderOutcome.Traded;
                        continue;
                    }
                    long bestMove = track.Side == BookSide.Bid
                        ? sample.Snapshot.BestBidTicks - previous.Snapshot.BestBidTicks
                        : sample.Snapshot.BestAskTicks - previous.Snapshot.BestAskTicks;
                    var twin = bestMove == 0 ? null : born.FirstOrDefault(b => b.Side == track.Side && !twinsUsed.Contains(b)
                        && b.PriceTicks != track.PriceTicks && Math.Abs(b.PriceTicks - track.PriceTicks) <= options.MaxMoveTicks
                        && Math.Sign(b.PriceTicks - track.PriceTicks) == Math.Sign(bestMove)
                        && b.MaxSize >= track.MaxSize * (1 - options.TwinTolerance) && b.MaxSize <= track.MaxSize * (1 + options.TwinTolerance));
                    if (twin != null)
                    {
                        twinsUsed.Add(twin);
                        track.Outcome = LargeOrderOutcome.Moved;
                        track.MovedToTicks = twin.PriceTicks;
                    }
                    else track.Outcome = LargeOrderOutcome.Pulled;
                }
            }
            previous = sample;
        }
        return all;

        static void CloseUnobserved(Dictionary<(BookSide, long), LargeOrderTrack> live)
        {
            foreach (var track in live.Values)
            {
                track.EndedAt = track.LastSeen;
                track.Outcome = LargeOrderOutcome.Unobserved;
            }
            live.Clear();
        }
    }
}

/// <summary>Resting size within a percentage of the mid price ("0 – X %" depth bands).</summary>
public static class DepthBands
{
    /// <summary>
    /// Inclusive band around the mid: bids at or above mid × (1 − p), asks at or below mid × (1 + p). Prices are
    /// in ticks, so the band is relative to the absolute mid. Requires a two-sided book; otherwise (0, 0, false).
    /// </summary>
    public static (decimal Bid, decimal Ask, bool Available) WithinPercent(DepthSnapshot book, decimal percent)
    {
        if (!book.HasBid || !book.HasAsk || percent <= 0) return (0, 0, false);
        decimal mid = ((decimal)book.BestBidTicks + book.BestAskTicks) / 2;
        decimal distance = Math.Abs(mid) * percent / 100;
        decimal bid = 0, ask = 0;
        foreach (var level in book.Bids)
        {
            if (level.PriceTicks < mid - distance) break;
            bid += level.Size;
        }
        foreach (var level in book.Asks)
        {
            if (level.PriceTicks > mid + distance) break;
            ask += level.Size;
        }
        return (bid, ask, true);
    }
}
