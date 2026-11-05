// Adapted from KVP Trading Hub AggregateFeed.cs, commit 50220ba91613161685f84384825d855cb4dbeb76.
// Original Trading Hub and KVP attribution and license terms are retained in THIRD_PARTY_NOTICES.md.
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Feeds;

/// <summary>A successful tape response may still cover only a provider's recent window.</summary>
public interface IRecentTradeHistoryFeed
{
    bool RecentTradesOnly { get; }
}

public enum AggregateOperation
{
    Instruments,
    Trades,
    Candles,
    FirstAvailable,
    Tickers,
    Live,
}

public sealed record AggregateConstituentStatus(string FeedId, string DisplayName, FeedStatus Status,
    string? Message = null, bool IsPartial = false);

/// <summary>A request/session coverage snapshot, not a guarantee of event-complete exchange data.</summary>
public sealed record AggregateCoverage(string Symbol, AggregateOperation Operation,
    IReadOnlyList<AggregateConstituentStatus> Constituents, string BookFeedId,
    DateTime? RequestedFromUtc = null, DateTime? RequestedToUtc = null)
{
    public bool IsPartial => Constituents.Any(c => c.Status != FeedStatus.Connected || c.IsPartial);
    public bool IsExecutable => false;
    public bool IsBookConsolidated => false;
}

/// <summary>
/// Data-only composite tape and candles. Constituent prints retain their venue and quality;
/// candle volume is summed, extremes are combined, and open/close prefer the first venue.
/// The DOM is exclusively the first configured venue, including when that venue is unavailable.
/// No instrument or price published by this feed is an executable quote.
/// </summary>
public sealed class AggregateFeed : IMarketDataFeed, IKlineHistory, IBarHistoryFeed, ITickerStatsProvider, IDataScopeFeed
{
    public const string FeedId = "agregado";
    private const int OutputBatchSize = 8192;
    private const int MaxPendingLiveTrades = 32768;
    private readonly IMarketDataFeed[] _sources;
    private readonly bool _ownsSources;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _liveReorderWindow;
    private readonly object _gate = new();
    private readonly object _historyPublicationGate = new();
    private readonly SemaphoreSlim _listing = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Instrument>[] _catalogs;
    private readonly Dictionary<string, Part?[]> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Symbol, AggregateOperation Operation), AggregateCoverage> _coverage = new();
    private readonly Dictionary<ulong, string> _candleSourceIdentities = new();
    private readonly HashSet<Live> _live = new();
    private int _disposed;

    public AggregateFeed(IReadOnlyList<IMarketDataFeed> sources, bool ownsSources = false, string? dataScopeKey = null,
        TimeProvider? timeProvider = null, TimeSpan? liveReorderWindow = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < 1 or > 64) throw new ArgumentException("An aggregate needs between one and 64 venues.", nameof(sources));
        _sources = sources.ToArray();
        if (_sources.Any(s => s == null) || _sources.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _sources.Length)
            throw new ArgumentException("Aggregate venues must be non-null and have distinct feed identities.", nameof(sources));
        _ownsSources = ownsSources;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _liveReorderWindow = liveReorderWindow ?? TimeSpan.FromMilliseconds(250);
        if (_liveReorderWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(liveReorderWindow));
        _catalogs = _sources.Select(_ => new Dictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase)).ToArray();
        Sources = Array.AsReadOnly(_sources);
        DataScopeKey = dataScopeKey ?? "aggregate:" + string.Join("+", _sources.Select(s => s is IDataScopeFeed scoped ? scoped.DataScopeKey : s.Id));
    }

    public IReadOnlyList<IMarketDataFeed> Sources { get; }
    public string Id => FeedId;
    public string DisplayName => "Aggregate (" + string.Join(" + ", _sources.Select(s => s.DisplayName)) + ")";
    public string DataScopeKey { get; }
    public bool SupportsHistory => _sources.Any(s => s.SupportsHistory);
    public bool IsLive => _sources.Any(s => s.IsLive);
    public bool IsExecutable => false;
    public bool IsBookConsolidated => false;
    public string BookFeedId => _sources[0].Id;
    public DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;
    public string CacheTag => "aggregate-provenance-v1:" + DataScopeKey + ":" + string.Join("+", _sources.Select(s => (s as IKlineHistory)?.CacheTag ?? "no-candles"));
    public event Action<AggregateCoverage>? CoverageChanged;

    public AggregateCoverage GetCoverage(Instrument instrument, AggregateOperation operation)
    {
        lock (_gate)
        {
            if (_coverage.TryGetValue((instrument.Symbol, operation), out var result) || _coverage.TryGetValue(("*", operation), out result))
                return result;
        }
        return Snapshot(instrument.Symbol, operation, InitialStates());
    }

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        await _listing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var states = InitialStates();
            var failures = new List<Exception>();
            for (int i = 0; i < _sources.Length; i++)
            {
                try
                {
                    var listed = await _sources[i].GetInstrumentsAsync(ct).ConfigureAwait(false);
                    var catalog = new Dictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase);
                    foreach (var instrument in listed) catalog[instrument.Symbol] = instrument;
                    lock (_gate) _catalogs[i] = catalog;
                    states[i] = State(i, FeedStatus.Connected);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failures.Add(ex);
                    states[i] = State(i, FeedStatus.Error, ex.Message);
                }
            }
            ct.ThrowIfCancellationRequested();
            var result = new List<Instrument>();
            List<(string Symbol, AggregateConstituentStatus[] States)>? partialListings = null;
            lock (_gate)
            {
                var bySymbol = new Dictionary<string, Part?[]>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _sources.Length; i++)
                    foreach (var instrument in _catalogs[i].Values)
                    {
                        if (!bySymbol.TryGetValue(instrument.Symbol, out var parts)) bySymbol[instrument.Symbol] = parts = new Part?[_sources.Length];
                        var first = parts.FirstOrDefault(p => p != null)?.Instrument;
                        if (first != null && (!string.Equals(first.BaseAsset, instrument.BaseAsset, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(first.QuoteAsset, instrument.QuoteAsset, StringComparison.OrdinalIgnoreCase) || first.PointValue != instrument.PointValue))
                            continue; // Unlike units cannot be added as if they were the same instrument.
                        parts[i] = new Part(i, _sources[i], instrument);
                    }
                foreach (var previous in _parts.Keys) _coverage.Remove((previous, AggregateOperation.Instruments));
                _parts.Clear();
                foreach (var (symbol, parts) in bySymbol)
                {
                    _parts[symbol] = parts;
                    result.Add(MakeInstrument(symbol, parts));
                    if (!parts.Any(part => part == null)) continue;
                    var symbolStates = (AggregateConstituentStatus[])states.Clone();
                    for (int i = 0; i < parts.Length; i++)
                        if (parts[i] == null) symbolStates[i] = State(i, FeedStatus.Disconnected, "Instrument unavailable or incompatible at this venue.");
                    (partialListings ??= new()).Add((symbol, symbolStates));
                }
            }
            Publish("*", AggregateOperation.Instruments, states);
            if (partialListings != null)
                foreach (var partial in partialListings) Publish(partial.Symbol, AggregateOperation.Instruments, partial.States);
            if (result.Count == 0 && failures.Count == _sources.Length) throw new AggregateException("No aggregate venue could list instruments.", failures);
            result.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Symbol, b.Symbol));
            return result;
        }
        finally { _listing.Release(); }
    }

    private Instrument MakeInstrument(string symbol, Part?[] parts)
    {
        var available = parts.OfType<Part>().ToArray();
        var first = available[0].Instrument;
        decimal tick = first.TickSize, volumeStep = first.VolumeStep;
        foreach (var part in available.Skip(1))
        {
            tick = CommonStep(tick, part.Instrument.TickSize);
            volumeStep = CommonStep(volumeStep, part.Instrument.VolumeStep);
        }
        return new Instrument
        {
            Symbol = symbol,
            FeedId = FeedId,
            Exchange = string.Join("+", _sources.Select(s => s.DisplayName)),
            TickSize = tick,
            VolumeStep = volumeStep,
            PointValue = first.PointValue,
            BaseAsset = first.BaseAsset,
            QuoteAsset = first.QuoteAsset,
            Description = $"{symbol} aggregate — DATA ONLY, non-executable; DOM: {_sources[0].DisplayName} only, not consolidated",
        };
    }

    private static decimal CommonStep(decimal left, decimal right)
    {
        if (left <= 0 || right <= 0) throw new InvalidOperationException("Aggregate venue price and volume steps must be positive.");
        while (right != 0) (left, right) = (right, left % right);
        return left;
    }

    private async Task<Part?[]> PartsAsync(Instrument instrument, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!string.Equals(instrument.FeedId, FeedId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The instrument does not belong to the aggregate feed.", nameof(instrument));
        lock (_gate) if (_parts.TryGetValue(instrument.Symbol, out var known)) return known;
        await GetInstrumentsAsync(ct).ConfigureAwait(false);
        lock (_gate)
            return _parts.TryGetValue(instrument.Symbol, out var listed) ? listed
                : throw new InvalidOperationException($"{instrument.Symbol} is not available from the aggregate venues.");
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        var parts = await PartsAsync(instrument, ct).ConfigureAwait(false);
        var overall = InitialStates(parts);
        double totalTicks = Math.Max(1, (toUtc - fromUtc).Ticks);
        // A bounded hour is collected before publication so a failed producer cannot retroactively
        // turn an already-published complete batch into an undisclosed partial batch.
        for (var cursor = fromUtc; cursor < toUtc;)
        {
            var end = toUtc - cursor > TimeSpan.FromHours(1) ? cursor.AddHours(1) : toUtc;
            var states = InitialStates(parts);
            var collected = new List<SourcedTrade>();
            var failures = new List<Exception>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] is not { } part || !part.Feed.SupportsHistory) continue;
                int source = i;
                bool inputPartial = false;
                try
                {
                    await part.Feed.LoadTradesAsync(part.Instrument, cursor, end, null, batch =>
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var trade in batch)
                        {
                            if (trade.Time < cursor || trade.Time > end || trade.Time == end && end != toUtc) continue;
                            inputPartial |= trade.Provenance.IsPartial;
                            collected.Add(new SourcedTrade(trade, source, collected.Count));
                        }
                        return true;
                    }, null, ct).ConfigureAwait(false);
                    bool recentOnly = part.Feed is IRecentTradeHistoryFeed { RecentTradesOnly: true };
                    states[i] = State(i, FeedStatus.Connected,
                        recentOnly ? "Only the provider's recent tape window is available; older trade coverage is incomplete."
                            : inputPartial ? "The constituent reports partial trade coverage." : null, recentOnly || inputPartial);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failures.Add(ex);
                    states[i] = State(i, FeedStatus.Error, ex.Message);
                }
            }
            ct.ThrowIfCancellationRequested();
            Accumulate(overall, states);
            Publish(instrument.Symbol, AggregateOperation.Trades, overall, fromUtc, end);
            if (!states.Any(s => s.Status == FeedStatus.Connected) && collected.Count == 0)
                throw new AggregateException("No aggregate venue could load trade history.", failures);
            collected.Sort(static (a, b) =>
            {
                int comparison = a.Trade.Time.CompareTo(b.Trade.Time);
                if (comparison == 0) comparison = a.Source.CompareTo(b.Source);
                if (comparison == 0) comparison = a.Ordinal.CompareTo(b.Ordinal);
                return comparison;
            });
            bool partial = states.Any(s => s.Status != FeedStatus.Connected || s.IsPartial);
            var output = new List<Trade>(Math.Min(OutputBatchSize, collected.Count));
            foreach (var item in collected)
            {
                // Numeric IDs are venue-scoped, not an aggregate continuation cursor.
                // Inclusive time overlaps are reconciled by the canonical source event identity.
                output.Add(MapTrade(item.Trade, item.Source, partial));
                if (output.Count < OutputBatchSize) continue;
                if (!PublishBatch(onBatch, output, ct)) return;
                ct.ThrowIfCancellationRequested();
                output = new List<Trade>(OutputBatchSize);
            }
            if (output.Count > 0 && !PublishBatch(onBatch, output, ct)) return;
            ct.ThrowIfCancellationRequested();
            cursor = end;
            progress?.Report((cursor - fromUtc).Ticks / totalTicks);
        }
        progress?.Report(1);
    }

    private Trade MapTrade(Trade trade, int source, bool partial)
    {
        var provenance = trade.Provenance;
        string identity = provenance.SourceIdentity ?? _sources[source].Id;
        provenance = provenance with
        {
            SourceIdentity = identity,
            Quality = provenance.Quality | (partial ? DataQuality.Partial : DataQuality.None),
        };
        return new Trade(trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side, provenance);
    }

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (minutes <= 0) throw new ArgumentOutOfRangeException(nameof(minutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        var parts = await PartsAsync(instrument, ct).ConfigureAwait(false);
        var overall = InitialStates(parts);
        long step = checked(minutes * 60_000L);
        long from = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long to = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long start = from / step * step;
        double total = Math.Max(1, to - start);
        for (long cursor = start; cursor < to;)
        {
            long end = Math.Min(to, checked(cursor + 5000 * step));
            var buckets = new SortedDictionary<long, Kline?[]>();
            var states = InitialStates(parts);
            var failures = new List<Exception>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] is not { Feed: IKlineHistory history } part) continue;
                int source = i;
                bool inputPartial = false;
                try
                {
                    await history.LoadKlinesAsync(part.Instrument, FromMs(cursor), FromMs(end), minutes, endPrice, batch =>
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var candle in batch)
                        {
                            if (candle.OpenTimeMs < cursor || candle.OpenTimeMs >= end) continue;
                            inputPartial |= (candle.Provenance.Quality & DataQuality.Partial) != 0;
                            if (!buckets.TryGetValue(candle.OpenTimeMs, out var values)) buckets[candle.OpenTimeMs] = values = new Kline?[_sources.Length];
                            values[source] = candle; // A correction replaces its venue's bar; it never adds it twice.
                        }
                        return true;
                    }, null, ct).ConfigureAwait(false);
                    states[i] = State(i, FeedStatus.Connected, inputPartial ? "The constituent reports partial candle coverage." : null, inputPartial);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failures.Add(ex);
                    states[i] = State(i, FeedStatus.Error, ex.Message);
                }
            }
            ct.ThrowIfCancellationRequested();
            bool failed = states.Any(state => state.Status != FeedStatus.Connected);
            var output = new List<Kline>(buckets.Count);
            foreach (var (time, values) in buckets)
            {
                for (int i = 0; i < values.Length; i++)
                    if (!values[i].HasValue && states[i].Status == FeedStatus.Connected)
                        states[i] = states[i] with { IsPartial = true, Message = "A constituent candle is missing in the requested range." };
                output.Add(MergeCandle(time, values, step, failed));
            }
            Accumulate(overall, states);
            Publish(instrument.Symbol, AggregateOperation.Candles, overall, FromMs(start), FromMs(end));
            if (!states.Any(s => s.Status == FeedStatus.Connected) && output.Count == 0)
                throw new AggregateException("No aggregate venue could load candle history.", failures);
            if (output.Count > 0 && !PublishBatch(onBatch, output, ct)) return;
            ct.ThrowIfCancellationRequested();
            cursor = end;
            progress?.Report((cursor - start) / total);
        }
        progress?.Report(1);
    }

    private Kline MergeCandle(long time, Kline?[] values, long step, bool failed)
    {
        Kline? first = null;
        decimal high = decimal.MinValue, low = decimal.MaxValue, volume = 0, buy = 0;
        var quality = DataQuality.Synthetic | (failed ? DataQuality.Partial : DataQuality.None);
        bool hasBuyVolume = true;
        int resolution = checked((int)(step / 1000));
        long correction = 0;
        ulong mask = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } candle) { quality |= DataQuality.Partial; continue; }
            first ??= candle;
            mask |= 1UL << i;
            high = Math.Max(high, candle.High);
            low = Math.Min(low, candle.Low);
            volume += candle.Volume;
            if (candle.HasBuyVolume) buy += candle.BuyVolume;
            quality |= candle.Provenance.Quality & ~(DataQuality.Raw | DataQuality.Compact);
            if (!candle.HasBuyVolume)
            {
                hasBuyVolume = false;
                quality |= DataQuality.InferredSide;
            }
            resolution = Math.Max(resolution, candle.Provenance.ResolutionSeconds);
            correction = Math.Max(correction, candle.Provenance.CorrectionRevision);
        }
        var anchor = first!.Value;
        string identity;
        lock (_gate)
        {
            if (!_candleSourceIdentities.TryGetValue(mask, out identity!))
                _candleSourceIdentities[mask] = identity = "aggregate:" + string.Join("+", _sources.Where((_, i) => (mask & (1UL << i)) != 0).Select(s => s.Id));
        }
        return new Kline(time, anchor.Open, high, low, anchor.Close, volume, buy, hasBuyVolume,
            new TradeProvenance(quality, resolution, identity, correction, 0));
    }

    public async Task<IReadOnlyList<Trade>> LoadBarHistoryAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
    {
        var result = new List<Trade>();
        int minutes = KlineHistory.MinutesFor(to - from);
        await LoadKlinesAsync(instrument, from, to, minutes, 0, batch =>
        {
            KlineHistory.Synthesize(batch, instrument, minutes, new[] { (from, to) }, result);
            return true;
        }, null, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        var parts = await PartsAsync(instrument, ct).ConfigureAwait(false);
        var states = InitialStates(parts);
        var failures = new List<Exception>();
        DateTime? first = null;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] is not { Feed: IKlineHistory history } part) continue;
            try
            {
                var available = await history.FirstAvailableAsync(part.Instrument, ct).ConfigureAwait(false);
                states[i] = State(i, FeedStatus.Connected, available.HasValue ? null : "First available time is unknown.", !available.HasValue);
                if (available.HasValue && (!first.HasValue || available < first)) first = available;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failures.Add(ex);
                states[i] = State(i, FeedStatus.Error, ex.Message);
            }
        }
        ct.ThrowIfCancellationRequested();
        Publish(instrument.Symbol, AggregateOperation.FirstAvailable, states);
        if (!states.Any(s => s.Status == FeedStatus.Connected)) throw new AggregateException("No aggregate venue could report history availability.", failures);
        return first;
    }

    public async Task<IReadOnlyDictionary<string, TickerStats>> GetTickerStatsAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        var states = InitialStates();
        var result = new Dictionary<string, TickerStats>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<Exception>();
        for (int i = 0; i < _sources.Length; i++)
        {
            if (_sources[i] is not ITickerStatsProvider provider) continue;
            try
            {
                var stats = await provider.GetTickerStatsAsync(ct).ConfigureAwait(false);
                states[i] = State(i, FeedStatus.Connected);
                foreach (var (symbol, ticker) in stats)
                    result[symbol] = result.TryGetValue(symbol, out var current)
                        ? current with { QuoteVolume = current.QuoteVolume + ticker.QuoteVolume, High = Math.Max(current.High, ticker.High), Low = Math.Min(current.Low, ticker.Low) }
                        : ticker;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failures.Add(ex);
                states[i] = State(i, FeedStatus.Error, ex.Message);
            }
        }
        ct.ThrowIfCancellationRequested();
        Publish("*", AggregateOperation.Tickers, states);
        if (!states.Any(s => s.Status == FeedStatus.Connected)) throw new AggregateException("No aggregate venue could load ticker statistics.", failures);
        return result;
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        ThrowIfDisposed();
        var live = new Live(this, instrument, sink);
        lock (_gate)
        {
            if (_disposed != 0) { live.Dispose(); throw new ObjectDisposedException(nameof(AggregateFeed)); }
            _live.Add(live);
        }
        _ = live.StartAsync();
        return live;
    }

    private AggregateConstituentStatus[] InitialStates(Part?[]? parts = null) => _sources.Select((_, i) =>
        State(i, FeedStatus.Disconnected, parts != null && parts[i] == null ? "Instrument unavailable at this venue." : "Not yet available.")).ToArray();

    private AggregateConstituentStatus State(int source, FeedStatus status, string? message = null, bool partial = false) =>
        new(_sources[source].Id, _sources[source].DisplayName, status, message, partial);

    private AggregateCoverage Snapshot(string symbol, AggregateOperation operation, AggregateConstituentStatus[] states,
        DateTime? requestedFromUtc = null, DateTime? requestedToUtc = null) =>
        new(symbol, operation, Array.AsReadOnly((AggregateConstituentStatus[])states.Clone()), BookFeedId, requestedFromUtc, requestedToUtc);

    private AggregateCoverage Publish(string symbol, AggregateOperation operation, AggregateConstituentStatus[] states,
        DateTime? requestedFromUtc = null, DateTime? requestedToUtc = null)
    {
        var snapshot = Snapshot(symbol, operation, states, requestedFromUtc, requestedToUtc);
        lock (_gate)
        {
            if (_disposed != 0) return snapshot;
            _coverage[(symbol, operation)] = snapshot;
        }
        CoverageChanged?.Invoke(snapshot);
        return snapshot;
    }

    private static void Accumulate(AggregateConstituentStatus[] overall, AggregateConstituentStatus[] current)
    {
        for (int i = 0; i < overall.Length; i++)
        {
            if (overall[i].Status == FeedStatus.Error) continue;
            bool previousGap = overall[i].IsPartial;
            overall[i] = current[i] with { IsPartial = current[i].IsPartial || previousGap };
        }
    }

    private static DateTime FromMs(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    private sealed record Part(int Source, IMarketDataFeed Feed, Instrument Instrument);
    private readonly record struct SourcedTrade(Trade Trade, int Source, int Ordinal);

    private bool PublishBatch<T>(Func<IReadOnlyList<T>, bool> callback, IReadOnlyList<T> batch, CancellationToken ct)
    {
        // Dispose waits for an active historical callback, and cancellation prevents a late producer
        // from starting another callback. This lock is never held by live venue callbacks.
        lock (_historyPublicationGate)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return callback(batch);
        }
    }

    public void Dispose()
    {
        Live[] live;
        lock (_historyPublicationGate)
        {
            lock (_gate)
            {
                if (_disposed != 0) return;
                _disposed = 1;
                live = _live.ToArray();
                _live.Clear();
            }
        }
        List<Exception>? failures = null;
        CancelAndDispose(_lifetime, ref failures);
        foreach (var subscription in live) DisposeResource(subscription, ref failures);
        if (_ownsSources) foreach (var source in _sources) DisposeResource(source, ref failures);
        if (failures != null) throw new AggregateException("Some aggregate resources could not be disposed.", failures);
    }

    private static void DisposeResource(IDisposable resource, ref List<Exception>? failures)
    {
        try { resource.Dispose(); }
        catch (Exception ex) { (failures ??= new()).Add(ex); }
    }

    private static void CancelAndDispose(CancellationTokenSource lifetime, ref List<Exception>? failures)
    {
        try { lifetime.Cancel(); }
        catch (Exception ex) { (failures ??= new()).Add(ex); }
        DisposeResource(lifetime, ref failures);
    }

    private sealed class Live : IDisposable
    {
        private readonly AggregateFeed _owner;
        private readonly Instrument _instrument;
        private readonly IMarketDataSink _sink;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime;
        private readonly List<IDisposable> _inner = new();
        private readonly PriorityQueue<PendingTrade, (long Ticks, long Sequence)> _pending = new();
        private readonly AggregateConstituentStatus[] _states;
        private readonly ITimer _timer;
        private bool _disposed;
        private long _sequence;
        private DateTime _lastTime;
        private Part? _primary;
        private bool _bookNeedsBaseline = true;
        private bool _bookDisconnected;
        private string? _bookVenue;
        private IReconnectable? _firstReconnect;
        private bool _firstVenueStatusOutage;
        private bool _bookReconnectIssued;
        private long _bookBaselineRevision;

        public Live(AggregateFeed owner, Instrument instrument, IMarketDataSink sink)
        {
            _owner = owner;
            _instrument = instrument;
            _sink = sink;
            _states = owner.InitialStates();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner._lifetime.Token);
            var period = owner._liveReorderWindow > TimeSpan.Zero ? owner._liveReorderWindow : TimeSpan.FromMilliseconds(1);
            _timer = owner._timeProvider.CreateTimer(_ => Flush(), null, period, period);
        }

        public async Task StartAsync()
        {
            try
            {
                var parts = await _owner.PartsAsync(_instrument, _lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) return;
                    _primary = parts[0];
                }
                for (int i = 0; i < parts.Length; i++)
                {
                    lock (_gate) if (_disposed) return;
                    if (parts[i] is not { } part) { Status(i, FeedStatus.Disconnected, "Instrument unavailable at this venue."); continue; }
                    try
                    {
                        var subscription = part.Feed.Subscribe(part.Instrument, new PartSink(this, i));
                        bool late;
                        lock (_gate)
                        {
                            late = _disposed;
                            if (!late)
                            {
                                _inner.Add(subscription);
                                if (i == 0)
                                {
                                    _firstReconnect = subscription as IReconnectable;
                                    RequestBookBaselineLocked();
                                }
                            }
                        }
                        if (late) subscription.Dispose();
                    }
                    catch (Exception ex) when (!_lifetime.IsCancellationRequested) { Status(i, FeedStatus.Error, ex.Message); }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    for (int i = 0; i < _states.Length; i++) _states[i] = _owner.State(i, FeedStatus.Error, ex.Message);
                    InvalidateBook(ex.Message);
                    if (_disposed) return;
                    PublishStatus();
                }
            }
        }

        public void Trades(int source, IReadOnlyList<Trade> trades)
        {
            lock (_gate)
            {
                if (_disposed || trades.Count == 0) return;
                bool partial = _states.Any(s => s.Status != FeedStatus.Connected || s.IsPartial);
                long received = _owner._timeProvider.GetTimestamp();
                foreach (var trade in trades)
                {
                    _pending.Enqueue(new PendingTrade(trade, source, received, partial), (trade.Time.Ticks, ++_sequence));
                    if (_pending.Count <= MaxPendingLiveTrades) continue;
                    FlushLocked();
                    if (_disposed) return;
                }
                FlushLocked();
            }
        }

        private void Flush()
        {
            lock (_gate)
            {
                if (_disposed) return;
                FlushLocked();
            }
        }

        private void FlushLocked()
        {
            long now = _owner._timeProvider.GetTimestamp();
            List<Trade>? output = null;
            bool dropped = false, overflow = false;
            bool currentPartial = _states.Any(state => state.Status != FeedStatus.Connected || state.IsPartial);
            int forced = _pending.Count > MaxPendingLiveTrades ? MaxPendingLiveTrades / 4 : 0;
            while (!_disposed && _pending.TryPeek(out var next, out _))
            {
                bool full = forced > 0;
                if (full) forced--;
                if (!full && _owner._timeProvider.GetElapsedTime(next.ReceivedAt, now) < _owner._liveReorderWindow) break;
                _pending.Dequeue();
                if (next.Trade.Time < _lastTime)
                {
                    _states[next.Source] = _states[next.Source] with { IsPartial = true, Message = "A late trade exceeded the reorder window and was omitted; its exchange timestamp was not changed." };
                    dropped = true;
                    currentPartial = true;
                    continue;
                }
                if (full)
                {
                    _states[next.Source] = _states[next.Source] with { IsPartial = true, Message = "The bounded live reorder buffer filled; ordering coverage is partial." };
                    overflow = true;
                    currentPartial = true;
                }
                _lastTime = next.Trade.Time;
                bool partial = next.PartialAtArrival || currentPartial;
                (output ??= new List<Trade>(Math.Min(OutputBatchSize, _pending.Count + 1))).Add(_owner.MapTrade(next.Trade, next.Source, partial));
            }
            if (output is { Count: > 0 }) _sink.OnTrades(_instrument, output);
            if (!_disposed && (dropped || overflow)) PublishStatus();
        }

        public void Depth(int source, DepthUpdate update)
        {
            lock (_gate)
            {
                if (_disposed || source != 0 || _primary == null) return;
                _bookVenue = update.Venue ?? _primary.Feed.DisplayName;
                if (update.IsFullRefresh && update.IsUsable)
                {
                    _bookNeedsBaseline = false;
                    _bookDisconnected = false;
                    _bookReconnectIssued = false;
                    _bookBaselineRevision++;
                }
                else if (!update.IsUsable)
                {
                    _bookNeedsBaseline = true;
                    _bookDisconnected |= update.Coverage.HasFlag(DepthCoverage.Disconnected);
                }
                var coverage = update.Coverage;
                var gap = update.GapBefore;
                if (_bookNeedsBaseline)
                {
                    coverage |= DepthCoverage.Missing | DepthCoverage.SequenceGap;
                    if (_bookDisconnected) coverage |= DepthCoverage.Disconnected;
                    if (gap == DepthGapReason.None) gap = _bookDisconnected ? DepthGapReason.Disconnected : DepthGapReason.Missing;
                }
                bool convert = _primary.Instrument.TickSize != _instrument.TickSize;
                var snapshot = convert ? new DepthSnapshot(
                    RemapLevels(update.Snapshot.Bids, _primary.Instrument),
                    RemapLevels(update.Snapshot.Asks, _primary.Instrument), update.Snapshot.Time) : update.Snapshot;
                IReadOnlyList<BookChange> changes = update.Changes;
                if (convert && changes.Count > 0)
                {
                    var remapped = new BookChange[changes.Count];
                    for (int i = 0; i < remapped.Length; i++)
                    {
                        var change = changes[i];
                        remapped[i] = new BookChange(change.Side, _instrument.ToTicks(_primary.Instrument.FromTicks(change.PriceTicks)), change.Size, change.Origin);
                    }
                    changes = remapped;
                }
                _sink.OnDepth(_instrument, new DepthUpdate
                {
                    Snapshot = snapshot,
                    Changes = changes,
                    IsFullRefresh = update.IsFullRefresh && !_bookNeedsBaseline,
                    ProviderId = _primary.Feed.Id,
                    Venue = _bookVenue,
                    ReceivedAt = update.ReceivedAt == default ? _owner.Now : update.ReceivedAt,
                    Sequence = update.Sequence,
                    FirstSequence = update.FirstSequence,
                    PreviousSequence = update.PreviousSequence,
                    Coverage = coverage,
                    GapBefore = gap,
                    Message = update.Message ?? (_bookNeedsBaseline
                        ? "First venue book unavailable until a valid full refresh; other venue books are not substituted."
                        : "First venue only; not a consolidated aggregate book."),
                });
            }
        }

        private BookLevel[] RemapLevels(BookLevel[] levels, Instrument source)
        {
            if (levels.Length == 0) return Array.Empty<BookLevel>();
            var remapped = new BookLevel[levels.Length];
            for (int i = 0; i < remapped.Length; i++)
                remapped[i] = new BookLevel(_instrument.ToTicks(source.FromTicks(levels[i].PriceTicks)), levels[i].Size);
            return remapped;
        }

        private void InvalidateBook(string? message)
        {
            _bookNeedsBaseline = true;
            _bookDisconnected = true;
            _sink.OnDepth(_instrument, new DepthUpdate
            {
                Snapshot = DepthSnapshot.Empty,
                ProviderId = _owner.BookFeedId,
                Venue = _bookVenue ?? _primary?.Feed.DisplayName ?? _owner._sources[0].DisplayName,
                ReceivedAt = _owner.Now,
                IsFullRefresh = false,
                Coverage = DepthCoverage.Missing | DepthCoverage.SequenceGap | DepthCoverage.Disconnected,
                GapBefore = DepthGapReason.Disconnected,
                Message = message ?? "The first venue book is disconnected; available composite tape does not restore it.",
            });
        }

        private void RequestBookBaselineLocked()
        {
            var reconnect = _firstReconnect;
            if (!_firstVenueStatusOutage || !_bookNeedsBaseline || _bookReconnectIssued || reconnect == null) return;
            _bookReconnectIssued = true;
            long baselineRevision = _bookBaselineRevision;
            // Reconnect can synchronously call back into this sink. The provider invocation must run
            // outside our callback lock, and a baseline received before dispatch makes it obsolete.
            _ = Task.Run(() =>
            {
                lock (_gate)
                    if (_disposed || !_bookNeedsBaseline || baselineRevision != _bookBaselineRevision) return;
                try { reconnect.Reconnect(); }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        if (_disposed || !_bookNeedsBaseline || baselineRevision != _bookBaselineRevision) return;
                        _states[0] = _owner.State(0, FeedStatus.Error, "First venue book resynchronization failed: " + ex.Message);
                        PublishStatus();
                    }
                }
            });
        }

        public void Status(int source, FeedStatus status, string? message)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _states[source] = _owner.State(source, status, message);
                if (source == 0)
                {
                    if (status == FeedStatus.Connected) _firstVenueStatusOutage = false;
                    else if (!_firstVenueStatusOutage && (status is FeedStatus.Reconnecting or FeedStatus.Error or FeedStatus.Disconnected))
                    {
                        _firstVenueStatusOutage = true;
                        if (!_bookNeedsBaseline || !_bookDisconnected)
                        {
                            InvalidateBook(message);
                            if (_disposed) return;
                        }
                        RequestBookBaselineLocked();
                    }
                }
                PublishStatus();
            }
        }

        private void PublishStatus()
        {
            var coverage = _owner.Publish(_instrument.Symbol, AggregateOperation.Live, _states);
            if (_disposed) return;
            var status = _states.Any(s => s.Status == FeedStatus.Connected) ? FeedStatus.Connected
                : _states.Any(s => s.Status == FeedStatus.Error) ? FeedStatus.Error
                : _states.Any(s => s.Status == FeedStatus.Reconnecting) ? FeedStatus.Reconnecting
                : _states.Any(s => s.Status == FeedStatus.Connecting) ? FeedStatus.Connecting : FeedStatus.Disconnected;
            string prefix = coverage.IsPartial ? "Partial aggregate coverage. " : "Aggregate data. ";
            string details = string.Join("; ", _states.Where(s => s.Status != FeedStatus.Connected || s.IsPartial)
                .Select(s => $"{s.DisplayName}: {s.Status}{(s.Message == null ? "" : " — " + s.Message)}"));
            _sink.OnStatus(_instrument, status, prefix + $"Data only; non-executable. DOM: {_owner.BookFeedId} only, not consolidated. " + details);
        }

        public void Dispose()
        {
            IDisposable[] inner;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _pending.Clear();
                inner = _inner.ToArray();
                _inner.Clear();
            }
            List<Exception>? failures = null;
            CancelAndDispose(_lifetime, ref failures);
            DisposeResource(_timer, ref failures);
            foreach (var subscription in inner) DisposeResource(subscription, ref failures);
            lock (_owner._gate) _owner._live.Remove(this);
            if (failures != null) throw new AggregateException("Some aggregate subscriptions could not be disposed.", failures);
        }

        private readonly record struct PendingTrade(Trade Trade, int Source, long ReceivedAt, bool PartialAtArrival);
        private sealed class PartSink(Live live, int source) : IMarketDataSink
        {
            public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) => live.Trades(source, trades);
            public void OnDepth(Instrument instrument, DepthUpdate update) => live.Depth(source, update);
            public void OnStatus(Instrument instrument, FeedStatus status, string? message) => live.Status(source, status, message);
        }
    }
}
