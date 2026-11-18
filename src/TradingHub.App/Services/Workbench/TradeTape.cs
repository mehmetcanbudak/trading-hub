using TradingHub.Feeds.Derivatives;
using TradingHub.Market;

namespace TradingHub.App.Services.Workbench;

/// <summary>One print as received: venue is the constituent feed id of an aggregate, or the connection's feed.</summary>
public readonly record struct TapeTrade(DateTime Time, string Venue, decimal Price, decimal Size, TradeSide Side, bool Partial = false, bool SideInferred = false);

public enum TapeEmphasis { Normal, Large, Huge }

/// <summary>A tape line: one or more same-venue, same-side prints in the same millisecond, or one liquidation.</summary>
public sealed record TapeRow(DateTime Time, string Venue, TradeSide Side, decimal Price, decimal Size, decimal Notional, int Prints,
    TapeEmphasis Emphasis, bool IsLiquidation, bool Partial, bool SideInferred);

/// <summary>
/// Thresholds are in quote notional (price × size × point value). <see cref="MinNotional"/> hides smaller tape lines;
/// <see cref="LargeNotional"/> and <see cref="HugeNotional"/> highlight; volume bars always count every print.
/// </summary>
public sealed record TapeSettings(decimal MinNotional = 0, decimal LargeNotional = 100_000, decimal HugeNotional = 1_000_000,
    decimal MinLiquidationNotional = 0, bool AggregateSameMillisecond = true, decimal PointValue = 1, int MaxRows = 400,
    int BucketSeconds = 10, int MaxBuckets = 180);

public enum LiquidationCoverage { NotObserved, Partial, Observed }

/// <summary>One venue's volume in one time bucket. Liquidations are null-meaning unknown when <see cref="Liquidations"/> is NotObserved.</summary>
public sealed class TapeVenueBucket
{
    public decimal Buy { get; internal set; }
    public decimal Sell { get; internal set; }
    public decimal LongLiquidations { get; internal set; }
    public decimal ShortLiquidations { get; internal set; }
    public bool TradesPartial { get; internal set; }
    public LiquidationCoverage Liquidations { get; internal set; }
}

public sealed class TapeBucket(DateTime start, TimeSpan length)
{
    public DateTime Start { get; } = start;
    public DateTime End => Start + Length;
    public TimeSpan Length { get; } = length;
    public Dictionary<string, TapeVenueBucket> Venues { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Price of the latest print in the bucket (any venue); null without prints.</summary>
    public decimal? Last { get; internal set; }
    private DateTime _lastTime;
    internal void Print(DateTime time, decimal price)
    {
        if (Last != null && time < _lastTime) return;
        Last = price;
        _lastTime = time;
    }
    internal TapeVenueBucket Venue(string id) => Venues.TryGetValue(id, out var v) ? v : Venues[id] = new TapeVenueBucket();
}

public sealed class TapeVenueTotals
{
    public decimal Buy { get; internal set; }
    public decimal Sell { get; internal set; }
    public int Prints { get; internal set; }
    public decimal LongLiquidations { get; internal set; }
    public decimal ShortLiquidations { get; internal set; }
    public DateTime LastTime { get; internal set; }
}

/// <summary>
/// Aggregated multi-venue trades tape. Keeps the newest lines above the display threshold, per-venue buy/sell notional
/// and liquidation notional per time bucket, and per-venue totals. Buckets before the tape started observing are not
/// represented (gaps, never zeros); liquidation values count only where a venue's liquidation stream was observed.
/// </summary>
public sealed class TradeTape
{
    private readonly LinkedList<TapeRow> _rows = new();
    private readonly List<TapeBucket> _buckets = new();
    private readonly Dictionary<string, TapeVenueTotals> _totals = new(StringComparer.OrdinalIgnoreCase);
    private TapeRow? _open;
    private long _openMs = long.MinValue;

    public TradeTape(TapeSettings settings) => Settings = settings;

    public TapeSettings Settings { get; private set; }

    /// <summary>Newest first.</summary>
    public IEnumerable<TapeRow> Rows => _rows;

    public int RowCount => _rows.Count;

    /// <summary>Oldest first, at most <see cref="TapeSettings.MaxBuckets"/>.</summary>
    public IReadOnlyList<TapeBucket> Buckets => _buckets;

    public IReadOnlyDictionary<string, TapeVenueTotals> Totals => _totals;

    /// <summary>Start of observation; buckets earlier than this are never created.</summary>
    public DateTime? ObservedFrom { get; private set; }

    /// <summary>Changes thresholds; existing lines keep their emphasis, bucket volumes are unaffected.</summary>
    public void Configure(TapeSettings settings)
    {
        bool rebucket = settings.BucketSeconds != Settings.BucketSeconds;
        Settings = settings;
        if (rebucket) _buckets.Clear();
        while (_rows.Count > Math.Max(1, settings.MaxRows)) _rows.RemoveLast();
    }

    public void Clear()
    {
        _rows.Clear();
        _buckets.Clear();
        _totals.Clear();
        _open = null;
        _openMs = long.MinValue;
        ObservedFrom = null;
    }

    public void Observe(DateTime from)
    {
        if (ObservedFrom == null || from < ObservedFrom) ObservedFrom = from;
    }

    public TapeEmphasis Emphasis(decimal notional) =>
        Settings.HugeNotional > 0 && notional >= Settings.HugeNotional ? TapeEmphasis.Huge
        : Settings.LargeNotional > 0 && notional >= Settings.LargeNotional ? TapeEmphasis.Large : TapeEmphasis.Normal;

    /// <summary>
    /// Adds prints in time order. Returns the lines that became visible with Large or Huge emphasis (for markers and
    /// optional sounds). A same-millisecond line stays open until a print from another venue, side or millisecond arrives,
    /// so its emphasis reflects the merged notional.
    /// </summary>
    public IReadOnlyList<TapeRow> AddTrades(IEnumerable<TapeTrade> trades)
    {
        var emphasized = new List<TapeRow>();
        foreach (var t in trades)
        {
            if (t.Price <= 0 || t.Size <= 0) continue;
            Observe(t.Time);
            decimal notional = t.Price * t.Size * Settings.PointValue;
            var bucket = BucketAt(t.Time);
            if (bucket != null)
            {
                bucket.Print(t.Time, t.Price);
                var v = bucket.Venue(t.Venue);
                if (t.Side == TradeSide.Sell) v.Sell += notional; else v.Buy += notional;
                v.TradesPartial |= t.Partial;
            }
            var total = Total(t.Venue);
            if (t.Side == TradeSide.Sell) total.Sell += notional; else total.Buy += notional;
            total.Prints++;
            if (t.Time > total.LastTime) total.LastTime = t.Time;

            long ms = t.Time.Ticks / TimeSpan.TicksPerMillisecond;
            if (Settings.AggregateSameMillisecond && _open is { } open && ms == _openMs && open.Venue == t.Venue && open.Side == t.Side)
            {
                decimal size = open.Size + t.Size;
                decimal merged = open.Notional + notional;
                _open = open with
                {
                    Size = size, Notional = merged, Prints = open.Prints + 1, Price = merged / size / Settings.PointValue,
                    Partial = open.Partial || t.Partial, SideInferred = open.SideInferred || t.SideInferred, Emphasis = Emphasis(merged),
                };
                continue;
            }
            Close(emphasized);
            _open = new TapeRow(t.Time, t.Venue, t.Side, t.Price, t.Size, notional, 1, Emphasis(notional), false, t.Partial, t.SideInferred);
            _openMs = ms;
            if (!Settings.AggregateSameMillisecond) Close(emphasized);
        }
        return emphasized;
    }

    /// <summary>Publishes the line still open for same-millisecond merging (call before rendering).</summary>
    public IReadOnlyList<TapeRow> Flush()
    {
        var emphasized = new List<TapeRow>();
        Close(emphasized);
        return emphasized;
    }

    private void Close(List<TapeRow> emphasized)
    {
        if (_open is not { } row) return;
        _open = null;
        _openMs = long.MinValue;
        if (row.Notional < Settings.MinNotional) return;
        Push(row);
        if (row.Emphasis != TapeEmphasis.Normal) emphasized.Add(row);
    }

    /// <summary>
    /// Adds observed liquidations (a long liquidated is a forced sell). Returns those at or above the liquidation
    /// threshold, which also appear as tape lines.
    /// </summary>
    public IReadOnlyList<TapeRow> AddLiquidations(IEnumerable<LiquidationEvent> liquidations)
    {
        var shown = new List<TapeRow>();
        foreach (var l in liquidations.OrderBy(l => l.Time))
        {
            decimal notional = l.Price * l.Quantity * Settings.PointValue;
            var bucket = BucketAt(l.Time);
            if (bucket != null)
            {
                var v = bucket.Venue(l.VenueId);
                if (l.LongLiquidated) v.LongLiquidations += notional; else v.ShortLiquidations += notional;
                if (v.Liquidations == LiquidationCoverage.NotObserved) v.Liquidations = LiquidationCoverage.Partial;
            }
            var total = Total(l.VenueId);
            if (l.LongLiquidated) total.LongLiquidations += notional; else total.ShortLiquidations += notional;
            if (notional < Settings.MinLiquidationNotional) continue;
            var row = new TapeRow(l.Time, l.VenueId, l.LongLiquidated ? TradeSide.Sell : TradeSide.Buy, l.Price, l.Quantity, notional, 1,
                Emphasis(notional), true, false, false);
            Close(new List<TapeRow>());
            Push(row);
            shown.Add(row);
        }
        return shown;
    }

    /// <summary>
    /// Marks each bucket's liquidation coverage per venue from the streams' observed windows: Observed when a window
    /// covers the whole bucket (up to <paramref name="now"/>), Partial when one overlaps it, otherwise NotObserved.
    /// </summary>
    public void ApplyLiquidationWindows(IReadOnlyList<ObservedWindow> windows, IEnumerable<string> venues, DateTime now)
    {
        foreach (var bucket in _buckets)
            foreach (var venue in venues)
            {
                var end = bucket.End < now ? bucket.End : now;
                var coverage = CoverageOf(windows.Where(w => string.Equals(w.VenueId, venue, StringComparison.OrdinalIgnoreCase)), bucket.Start, end);
                if (coverage == LiquidationCoverage.NotObserved && !bucket.Venues.ContainsKey(venue)) continue;
                var v = bucket.Venue(venue);
                // An event already counted proves at least partial observation.
                v.Liquidations = coverage == LiquidationCoverage.NotObserved && (v.LongLiquidations > 0 || v.ShortLiquidations > 0) ? LiquidationCoverage.Partial : coverage;
            }
    }

    /// <summary>Whether the windows cover [from, to) completely, partly or not at all.</summary>
    public static LiquidationCoverage CoverageOf(IEnumerable<ObservedWindow> windows, DateTime from, DateTime to)
    {
        if (to <= from) return LiquidationCoverage.NotObserved;
        var cursor = from;
        bool any = false;
        foreach (var w in windows.Where(w => w.To > from && w.From < to).OrderBy(w => w.From))
        {
            any = true;
            if (w.From > cursor) return LiquidationCoverage.Partial;
            if (w.To > cursor) cursor = w.To;
            if (cursor >= to) return LiquidationCoverage.Observed;
        }
        return any ? LiquidationCoverage.Partial : LiquidationCoverage.NotObserved;
    }

    /// <summary>
    /// A bucket's liquidation coverage across <paramref name="venues"/> (the liquidation streams): a venue without an entry
    /// was not observed. Observed when every stream was; NotObserved when none was; otherwise Partial, so one stream that
    /// never connected is never drawn as an observed zero.
    /// </summary>
    public static LiquidationCoverage BucketCoverage(TapeBucket bucket, IEnumerable<string> venues)
    {
        bool any = false, all = true;
        foreach (var venue in venues)
        {
            var state = bucket.Venues.TryGetValue(venue, out var v) ? v.Liquidations : LiquidationCoverage.NotObserved;
            any |= state != LiquidationCoverage.NotObserved;
            all &= state == LiquidationCoverage.Observed;
        }
        return !any ? LiquidationCoverage.NotObserved : all ? LiquidationCoverage.Observed : LiquidationCoverage.Partial;
    }

    /// <summary>Flags the bucket at <paramref name="time"/> as partial for a venue whose feed is not connected.</summary>
    public void MarkPartial(DateTime time, string venue)
    {
        if (BucketAt(time) is { } bucket) bucket.Venue(venue).TradesPartial = true;
    }

    private void Push(TapeRow row)
    {
        _rows.AddFirst(row);
        while (_rows.Count > Math.Max(1, Settings.MaxRows)) _rows.RemoveLast();
    }

    private TapeVenueTotals Total(string venue) => _totals.TryGetValue(venue, out var t) ? t : _totals[venue] = new TapeVenueTotals();

    private TapeBucket? BucketAt(DateTime time)
    {
        var length = TimeSpan.FromSeconds(Math.Max(1, Settings.BucketSeconds));
        var start = new DateTime(time.Ticks - time.Ticks % length.Ticks, DateTimeKind.Utc);
        if (ObservedFrom is { } from && start + length <= from) return null;
        if (_buckets.Count > 0)
        {
            var last = _buckets[^1];
            if (start == last.Start) return last;
            if (start < last.Start)
            {
                int index = _buckets.FindIndex(b => b.Start == start);
                if (index >= 0) return _buckets[index];
                if (start < _buckets[0].Start && _buckets.Count >= Settings.MaxBuckets) return null;
                var inserted = new TapeBucket(start, length);
                int at = _buckets.FindIndex(b => b.Start > start);
                _buckets.Insert(at < 0 ? _buckets.Count : at, inserted);
                Trim();
                return _buckets.Contains(inserted) ? inserted : null;
            }
        }
        var bucket = new TapeBucket(start, length);
        _buckets.Add(bucket);
        Trim();
        return bucket;
    }

    private void Trim()
    {
        int max = Math.Max(1, Settings.MaxBuckets);
        if (_buckets.Count > max) _buckets.RemoveRange(0, _buckets.Count - max);
    }
}
