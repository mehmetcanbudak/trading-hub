// Protocol interpretation adapted from kvp61/trading-hub at 50220ba91613161685f84384825d855cb4dbeb76,
// originally ralphtradesz/trading-hub (MIT with Commons Clause). No broker or trading interface is implemented.
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Networking;

namespace TradingHub.Feeds.Longbridge;

public enum LongbridgeDataKind { Quote, Candles, RecentTape, Depth }
public sealed record LongbridgeCoverage(string Symbol, LongbridgeDataKind Kind, bool Complete, DateTime? FirstUtc, DateTime? LastUtc, string Message);
public sealed record LongbridgeTapeResult(IReadOnlyList<Trade> Trades, LongbridgeCoverage Coverage, bool HasProviderTradeIds = false);

/// <summary>
/// Longbridge quote data only. Recent tape is bounded to the provider's latest 1000 prints, not historical full tape.
/// Candle history is separately synthetic OHLC provenance. Quote pushes never manufacture trade events. Depth is
/// limited received L2 positions, not a full-depth/event-complete/MBO book; no undocumented contiguous sequence rule.
/// </summary>
public sealed class LongbridgeFeed : IMarketDataFeed, IKlineHistory, IBarHistoryFeed, IRecentTradeHistoryFeed, ISessionAwareFeed, ISessionSettingsFeed, IDataScopeFeed, IAsyncDisposable
{
    public const string FeedId = "longbridge";
    private readonly LongbridgeSession _session;
    private readonly ILongbridgeTransport _transport;
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LongbridgeOwnedTasks _owned;
    private readonly object _stopGate = new();
    private Task? _stop;
    private long _tradeId;
    private long _transportEpoch;
    private bool _disposed;
    private sealed class State
    {
        public required string Symbol;
        public required LongbridgeInstrumentMetadata Metadata;
        public Instrument? Instrument;
        public readonly object Sync = new();
        public readonly object Publication = new();
        public readonly List<Live> Listeners = new();
        public readonly Dictionary<int, LbMessages.Level> Asks = new();
        public readonly Dictionary<int, LbMessages.Level> Bids = new();
        public long? DepthSequence;
        public long? TapeSequence;
        public DepthSnapshot LastSnapshot = DepthSnapshot.Empty;
        public DepthGapReason Gap;
        public long RequestRevision;
        public long ReadyRevision = -1;
        public long ReadyEpoch = -1;
        public long ActiveEpoch = -1;
        public long QuarantinedDepthRevision;
        public bool AcceptDepth;
        public bool SubscriptionMayBeActive;
        public readonly SemaphoreSlim SubscriptionGate = new(1, 1);
    }
    private sealed class Live : IDisposable
    {
        private readonly LongbridgeFeed _feed;
        public readonly State State;
        public readonly Instrument Instrument;
        public readonly IMarketDataSink Sink;
        private readonly object _gate = new();
        private bool _active = true;
        private bool _depthReady;
        public Live(LongbridgeFeed feed, State state, Instrument instrument, IMarketDataSink sink) { _feed = feed; State = state; Instrument = instrument; Sink = sink; }
        public void Deliver(Action<IMarketDataSink, Instrument> action) { lock (_gate) { if (_active) action(Sink, Instrument); } }
        public void ReportStatus(FeedStatus status, string? message)
        {
            lock (_gate)
            {
                if (!_active) return;
                if (status != FeedStatus.Connected) _depthReady = false;
                Sink.OnStatus(Instrument, status, message);
            }
        }
        public void DeliverDepth(DepthUpdate update)
        {
            lock (_gate)
            {
                if (!_active) return;
                bool unavailable = (update.Coverage & (DepthCoverage.Missing | DepthCoverage.Disconnected | DepthCoverage.OutOfOrder)) != 0;
                if (update.IsFullRefresh && !unavailable) _depthReady = true;
                else if (unavailable || update.GapBefore != DepthGapReason.None) _depthReady = false;
                if (_depthReady || unavailable || update.GapBefore != DepthGapReason.None) Sink.OnDepth(Instrument, update);
            }
        }
        public void Dispose() { lock (_gate) { if (!_active) return; _active = false; } _feed.Remove(this); }
    }

    public LongbridgeFeed(LongbridgeSettings settings, LongbridgeSession session, ILongbridgeTransport? transport = null,
        Func<ClientWebSocket>? socketFactory = null, string? feedId = null, ProxyHandlerFactory? network = null)
    {
        _session = session;
        _owned = new(nameof(LongbridgeFeed), _lifetime);
        Id = string.IsNullOrWhiteSpace(feedId) ? FeedId : feedId;
        foreach (string configured in settings.Symbols)
        {
            string symbol = configured.Trim().ToUpperInvariant();
            if (symbol.Length == 0 || symbol.Any(char.IsWhiteSpace) || !symbol.Contains('.'))
                throw new InvalidOperationException("Longbridge symbols must be explicitly configured provider security codes.");
            var metadata = settings.Instruments.FirstOrDefault(pair => pair.Key.Equals(symbol, StringComparison.OrdinalIgnoreCase)).Value;
            if (metadata == null) throw new InvalidOperationException($"Longbridge has no configured tick/session metadata for {symbol}.");
            _states.TryAdd(symbol, new State { Symbol = symbol, Metadata = metadata.Copy() });
        }
        string identity = settings.HttpEndpoint.AbsoluteUri + "|" + settings.QuoteEndpoint.AbsoluteUri + "|" + settings.ClientId + "|" + Id + "|" +
            JsonSerializer.Serialize(_states.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { pair.Key, pair.Value.Metadata }));
        string configurationIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        DataScopeKey = $"{Id}:{configurationIdentity}:{Guid.NewGuid():N}";
        CacheTag = $"longbridge-data-v2-{configurationIdentity}";
        _transport = transport ?? new LongbridgeSocket(session, settings, socketFactory: socketFactory, network: network);
        _transport.Push += OnPush;
        _transport.Status += OnStatus;
    }

    public string Id { get; }
    public string DisplayName => "Longbridge (data only)";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public bool RecentTradesOnly => true;
    public DateTime Now => _session.UtcNow.UtcDateTime;
    public string DataScopeKey { get; }
    public string CacheTag { get; }
    public event Action<LongbridgeQuote>? QuoteReceived;
    public event Action<LongbridgeCoverage>? CoverageChanged;

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(GetInstrumentsCoreAsync(ct));
    }
    private async Task<IReadOnlyList<Instrument>> GetInstrumentsCoreAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var output = new List<Instrument>(_states.Count);
        foreach (var chunk in _states.Values.Chunk(200))
        {
            byte[] bytes = await RequestAsync((byte)LongbridgeCommand.StaticInfo, LbMessages.Symbols(chunk.Select(s => s.Symbol)), ct).ConfigureAwait(false);
            var information = LbMessages.StaticInfo(bytes).ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);
            foreach (var state in chunk)
            {
                if (!information.TryGetValue(state.Symbol, out var info)) throw new InvalidOperationException($"Longbridge returned no static information for configured symbol {state.Symbol}.");
                decimal lot = state.Metadata.VolumeStep ?? info.LotSize ?? 0;
                if (state.Metadata.VolumeStep.HasValue && info.LotSize is > 0 && state.Metadata.VolumeStep.Value != info.LotSize.Value)
                    throw new InvalidOperationException($"Configured Longbridge board-lot step for {state.Symbol} conflicts with provider static information.");
                if (lot <= 0) throw new InvalidOperationException($"Longbridge lot size for {state.Symbol} is unknown; configure it explicitly.");
                string exchange = Known(state.Metadata.Exchange, info.Exchange, "exchange", state.Symbol);
                string currency = Known(state.Metadata.Currency, info.Currency, "currency", state.Symbol);
                var instrument = new Instrument
                {
                    Symbol = state.Symbol, FeedId = Id, Exchange = exchange, TickSize = state.Metadata.TickSize,
                    VolumeStep = lot, PointValue = 1, QuoteAsset = currency, Description = info.Name.Length > 0 ? info.Name : state.Symbol,
                };
                lock (state.Sync) state.Instrument = instrument;
                output.Add(instrument);
            }
        }
        return output;
    }
    private static string Known(string configured, string observed, string field, string symbol)
    {
        if (configured.Length > 0 && observed.Length > 0 && !configured.Equals(observed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Configured Longbridge {field} for {symbol} conflicts with provider static information.");
        string value = observed.Length > 0 ? observed : configured;
        if (value.Length == 0) throw new InvalidOperationException($"Longbridge {field} for {symbol} is unknown; configure it explicitly.");
        return value;
    }
    private State Require(Instrument instrument)
    {
        ThrowIfDisposed();
        if (instrument.FeedId != Id || !_states.TryGetValue(instrument.Symbol, out var state) || instrument.TickSize != state.Metadata.TickSize)
            throw new ArgumentException("Instrument does not belong to this configured Longbridge data scope.", nameof(instrument));
        lock (state.Sync)
        {
            var expected = state.Instrument ?? throw new InvalidOperationException("Load Longbridge static metadata before using a configured instrument.");
            if (instrument.Symbol != expected.Symbol || instrument.VolumeStep != expected.VolumeStep ||
                instrument.Exchange != expected.Exchange || instrument.QuoteAsset != expected.QuoteAsset || instrument.PointValue != expected.PointValue)
                throw new ArgumentException("Instrument metadata does not match this Longbridge data scope.", nameof(instrument));
        }
        return state;
    }
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session.IsRetired) throw new InvalidOperationException("Longbridge data context is retired; select the current connection.");
    }
    private async Task<byte[]> RequestAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var result = await _transport.RequestAsync(command, body, request.Token).ConfigureAwait(false);
        request.Token.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return result;
    }
    public SessionSettings GetSessionSettings(Instrument instrument) => Require(instrument).Metadata.ToSessionSettings();
    public LongbridgeSessionDay GetSessions(Instrument instrument, DateOnly date) => Require(instrument).Metadata.GetSessions(date);

    public Task<LongbridgeQuote> GetQuoteAsync(Instrument instrument, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(GetQuoteCoreAsync(instrument, ct));
    }
    private async Task<LongbridgeQuote> GetQuoteCoreAsync(Instrument instrument, CancellationToken ct)
    {
        _ = Require(instrument);
        var quotes = LbMessages.Quotes(await RequestAsync((byte)LongbridgeCommand.Quote, LbMessages.Symbols(new[] { instrument.Symbol }), ct).ConfigureAwait(false));
        var quote = quotes.SingleOrDefault(q => q.Symbol.Equals(instrument.Symbol, StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException("Longbridge did not return the requested quote.");
        CoverageChanged?.Invoke(new(instrument.Symbol, LongbridgeDataKind.Quote, false, quote.Timestamp, quote.Timestamp,
            "Quote snapshot only; absent fields are unknown and cumulative volume is not tape."));
        return quote;
    }

    public Task<LongbridgeTapeResult> GetRecentTradesAsync(Instrument instrument, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(GetRecentTradesCoreAsync(instrument, ct));
    }
    private async Task<LongbridgeTapeResult> GetRecentTradesCoreAsync(Instrument instrument, CancellationToken ct)
    {
        _ = Require(instrument);
        var tape = LbMessages.Tape(await RequestAsync((byte)LongbridgeCommand.Trades, LbMessages.Trades(instrument.Symbol, 1000), ct).ConfigureAwait(false), push: false);
        SameSymbol(instrument.Symbol, tape.Symbol);
        var ordered = tape.Trades.Select((trade, index) => (trade, index)).OrderBy(item => item.trade.Timestamp).ThenBy(item => item.index);
        var trades = ordered.Select(item => ConvertTrade(instrument, item.trade, "recent-tape")).ToArray();
        var coverage = new LongbridgeCoverage(instrument.Symbol, LongbridgeDataKind.RecentTape, false,
            trades.Length == 0 ? null : trades[0].Time, trades.Length == 0 ? null : trades[^1].Time,
            "Only the latest up to 1000 provider prints are available; older history, stable provider trade IDs and aggressor side are not supplied. Local observation identities preserve prints, but overlap between requests and REST/live streams cannot be resolved exactly.");
        CoverageChanged?.Invoke(coverage);
        return new(trades, coverage);
    }
    private Trade ConvertTrade(Instrument instrument, LbMessages.TapeTrade trade, string channel)
    {
        ExactPrice(instrument, trade.Price);
        // Longbridge direction is price movement (down/up), not an exchange-observed aggressor side.
        var side = trade.Direction switch { 1 => TradeSide.Sell, 2 => TradeSide.Buy, _ => TradeSide.None };
        // Numeric IDs are local cursors. An independent observation identity survives feed recreation without
        // collapsing identical prints; it does not claim that repeated REST/live observations are distinct native events.
        return new(Interlocked.Increment(ref _tradeId), LbMessages.Time(trade.Timestamp), trade.Price, trade.Volume, side,
            new(DataQuality.Raw | DataQuality.Partial | DataQuality.InferredSide, 0, $"{Id}:{instrument.Symbol}:{channel}",
                SourceEventId: $"observation:{Guid.NewGuid():N}"));
    }
    private static void ExactPrice(Instrument instrument, decimal price)
    {
        if (price % instrument.TickSize != 0) throw new InvalidDataException("Observed Longbridge price is not representable by the configured price grid; no price is rounded or guessed.");
    }
    private static void SameSymbol(string requested, string actual)
    {
        if (!requested.Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Longbridge response contains a different security code.");
    }
    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(LoadTradesCoreAsync(instrument, fromUtc, toUtc, fromId, onBatch, progress, ct));
    }
    private async Task LoadTradesCoreAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (fromId.HasValue) throw new NotSupportedException("Longbridge recent tape supplies no stable provider trade IDs; ID-based resume is unsupported.");
        _ = Require(instrument);
        if (toUtc <= fromUtc) return;
        var result = await GetRecentTradesAsync(instrument, ct).ConfigureAwait(false);
        var selected = result.Trades.Where(t => t.Time >= fromUtc && t.Time < toUtc).ToArray();
        if (selected.Length > 0) _ = onBatch(selected);
        progress?.Report(1);
    }
    public Task<DateTime?> LastTradeTimeAsync(Instrument instrument, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(LastTradeTimeCoreAsync(instrument, ct));
    }
    private async Task<DateTime?> LastTradeTimeCoreAsync(Instrument instrument, CancellationToken ct)
    {
        var tape = await GetRecentTradesAsync(instrument, ct).ConfigureAwait(false);
        return tape.Trades.Count == 0 ? null : tape.Trades[^1].Time;
    }
    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        _ = Require(instrument); ct.ThrowIfCancellationRequested(); return Task.FromResult<DateTime?>(null);
    }
    public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(LoadKlinesCoreAsync(instrument, fromUtc, toUtc, minutes, endPrice, onBatch, progress, ct));
    }
    private async Task LoadKlinesCoreAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var state = Require(instrument);
        if (minutes is not (1 or 5 or 15 or 60)) throw new NotSupportedException("Longbridge history supports explicit 1, 5, 15 or 60 minute requests; periods are never silently remapped.");
        if (toUtc <= fromUtc) return;
        // Overlap cursors support both inclusive and exclusive provider offsets without skipping the boundary bar.
        var cursor = fromUtc.AddMinutes(-minutes);
        long lastSeen = -1;
        DateTime? first = null, last = null;
        for (int page = 0; page < 5000; page++)
        {
            ct.ThrowIfCancellationRequested();
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(cursor, DateTimeKind.Utc), state.Metadata.TimeZone);
            if (state.Metadata.TimeZone.IsAmbiguousTime(local))
                throw new NotSupportedException("Longbridge's local-time history cursor cannot identify an ambiguous DST instant; request an unambiguous exchange-local boundary.");
            byte[] request = LbMessages.History(instrument.Symbol, minutes, local.ToString("yyyyMMdd", CultureInfo.InvariantCulture), local.ToString("HHmm", CultureInfo.InvariantCulture), state.Metadata.TradeSession);
            var response = LbMessages.Candles(await RequestAsync((byte)LongbridgeCommand.HistoryCandles, request, ct).ConfigureAwait(false));
            SameSymbol(instrument.Symbol, response.Symbol);
            if (response.Bars.Count == 0) break;
            var sorted = response.Bars.OrderBy(c => c.Timestamp).ToArray();
            long newest = sorted[^1].Timestamp;
            if (newest <= lastSeen)
            {
                if (response.Bars.Count < 1000) break;
                throw new InvalidDataException("Longbridge candle pagination did not advance; history remains incomplete.");
            }
            var batch = new List<Kline>(sorted.Length);
            long previous = lastSeen;
            foreach (var candle in sorted)
            {
                if (candle.Timestamp <= previous) continue;
                previous = candle.Timestamp;
                DateTime open = LbMessages.Time(candle.Timestamp);
                if (open < fromUtc || open >= toUtc || open.AddMinutes(minutes) > toUtc || open.AddMinutes(minutes) > Now) continue;
                ExactPrice(instrument, candle.Open); ExactPrice(instrument, candle.High); ExactPrice(instrument, candle.Low); ExactPrice(instrument, candle.Close);
                batch.Add(new(candle.Timestamp * 1000, candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, 0, false,
                    new(DataQuality.Synthetic | DataQuality.Partial, minutes * 60, $"{Id}:{instrument.Symbol}:candles")));
                first ??= open; last = open;
            }
            if (batch.Count > 0 && !onBatch(batch)) return;
            lastSeen = newest;
            cursor = LbMessages.Time(newest);
            progress?.Report(Math.Clamp((cursor - fromUtc).TotalSeconds / (toUtc - fromUtc).TotalSeconds, 0, 1));
            if (cursor.AddMinutes(minutes) >= toUtc || response.Bars.Count < 1000) break;
            if (page == 4999) throw new InvalidDataException("Longbridge candle pagination reached its bounded request limit; reduce the requested range.");
        }
        CoverageChanged?.Invoke(new(instrument.Symbol, LongbridgeDataKind.Candles, false, first, last,
            "Unadjusted completed OHLC/volume candles only; provider history/entitlement gaps and missing buy volume are not inferred as full tape."));
        progress?.Report(1);
    }
    public Task<IReadOnlyList<Trade>> LoadBarHistoryAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(LoadBarHistoryCoreAsync(instrument, from, to, ct));
    }
    private async Task<IReadOnlyList<Trade>> LoadBarHistoryCoreAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
    {
        var klines = new List<Kline>();
        await LoadKlinesAsync(instrument, from, to, 1, 0, batch => { klines.AddRange(batch); return true; }, null, ct).ConfigureAwait(false);
        var result = new List<Trade>();
        KlineHistory.Synthesize(klines, instrument, 1, new[] { (from, to) }, result);
        return result;
    }

    public Task<DepthUpdate> GetDepthAsync(Instrument instrument, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(GetDepthCoreAsync(instrument, ct));
    }
    private async Task<DepthUpdate> GetDepthCoreAsync(Instrument instrument, CancellationToken ct)
    {
        _ = Require(instrument);
        var depth = await QueryDepthAsync(instrument, ct).ConfigureAwait(false);
        var asks = new Dictionary<int, LbMessages.Level>(); var bids = new Dictionary<int, LbMessages.Level>();
        Apply(asks, depth.Asks); Apply(bids, depth.Bids);
        return Baseline(instrument, asks, bids, DepthGapReason.None);
    }
    private async Task<(List<LbMessages.Level> Asks, List<LbMessages.Level> Bids)> QueryDepthAsync(Instrument instrument, CancellationToken ct)
    {
        var depth = LbMessages.Depth(await RequestAsync((byte)LongbridgeCommand.Depth, LbMessages.Symbol(instrument.Symbol), ct).ConfigureAwait(false), push: false);
        SameSymbol(instrument.Symbol, depth.Symbol);
        return (depth.Asks, depth.Bids);
    }
    private DepthUpdate Baseline(Instrument instrument, Dictionary<int, LbMessages.Level> asks,
        Dictionary<int, LbMessages.Level> bids, DepthGapReason gap)
    {
        DateTime received = Now;
        return new()
        {
            Snapshot = Snapshot(instrument, asks, bids, received), IsFullRefresh = true, ProviderId = Id, Venue = instrument.Exchange,
            ReceivedAt = received, Coverage = DepthCoverage.SnapshotOnly | DepthCoverage.PartialBook, GapBefore = gap,
            Message = "Provider-limited L2 query snapshot; receive timestamp only. No snapshot sequence, event-complete query-to-subscription boundary, full-depth or MBO coverage is supplied.",
        };
    }
    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        using var admission = _owned.Enter();
        var state = Require(instrument);
        var live = new Live(this, state, instrument, sink);
        lock (state.Publication)
        {
            lock (state.Sync)
            {
                state.Listeners.Add(live); state.Instrument = instrument;
                state.RequestRevision++; state.AcceptDepth = false;
                if (state.Gap == DepthGapReason.None) state.Gap = DepthGapReason.Missing;
            }
            live.ReportStatus(FeedStatus.Connecting, "Acquiring a Longbridge depth baseline before live position updates.");
        }
        _ = ChangeSubscriptionAsync(state, subscribe: true);
        return live;
    }
    private void Remove(Live live)
    {
        bool last;
        lock (live.State.Publication)
        {
            lock (live.State.Sync)
            {
                live.State.Listeners.Remove(live); last = live.State.Listeners.Count == 0;
                if (last) { live.State.RequestRevision++; live.State.AcceptDepth = false; live.State.ReadyRevision = -1; }
            }
        }
        if (last && !_disposed) _ = ChangeSubscriptionAsync(live.State, subscribe: false);
    }
    private bool IsCurrentSubscription(State state, long revision, long epoch) =>
        !_disposed && !_session.IsRetired && state.Listeners.Count > 0 &&
        state.RequestRevision == revision && Volatile.Read(ref _transportEpoch) == epoch;

    private Task ChangeSubscriptionAsync(State state, bool subscribe)
    {
        if (!_owned.TryEnter(out var admission)) return Task.CompletedTask;
        using (admission) return _owned.Track(ChangeSubscriptionCoreAsync(state, subscribe));
    }
    private async Task ChangeSubscriptionCoreAsync(State state, bool subscribe)
    {
        bool entered = false;
        long revision = -1;
        try
        {
            await state.SubscriptionGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            entered = true;
            bool active;
            long epoch;
            Instrument instrument;
            lock (state.Publication)
            {
                lock (state.Sync)
                {
                    if ((state.Listeners.Count > 0) != subscribe) return;
                    revision = state.RequestRevision;
                    epoch = Volatile.Read(ref _transportEpoch);
                    if (subscribe && state.AcceptDepth && state.SubscriptionMayBeActive &&
                        state.ReadyRevision == revision && state.ReadyEpoch == epoch) return;
                    instrument = state.Instrument ?? throw new InvalidOperationException("Longbridge instrument metadata is unavailable.");
                    active = state.SubscriptionMayBeActive;
                    state.AcceptDepth = false;
                    state.ReadyRevision = -1;
                    if (subscribe && state.Gap == DepthGapReason.None) state.Gap = DepthGapReason.Missing;
                }
                if (subscribe) ReportStatus(state, FeedStatus.Connecting, "Synchronizing a provider depth snapshot before Longbridge live data.");
            }
            if (active)
            {
                await RequestAsync((byte)LongbridgeCommand.Unsubscribe, LbMessages.Subscribe(new[] { state.Symbol }, firstPush: false), _lifetime.Token).ConfigureAwait(false);
                lock (state.Sync)
                {
                    if (state.RequestRevision != revision || Volatile.Read(ref _transportEpoch) != epoch) return;
                    state.SubscriptionMayBeActive = false;
                }
            }
            if (!subscribe) return;

            // QueryDepth has no sequence to align with pushes. Quiesce the subscription, then replace any
            // snapshot that raced a late position packet with a newer query; never replay old positions over it.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long quarantined;
                lock (state.Sync)
                {
                    if (!IsCurrentSubscription(state, revision, epoch)) return;
                    quarantined = state.QuarantinedDepthRevision;
                }
                var depth = await QueryDepthAsync(instrument, _lifetime.Token).ConfigureAwait(false);
                lock (state.Publication)
                {
                    DepthUpdate baseline;
                    lock (state.Sync)
                    {
                        if (!IsCurrentSubscription(state, revision, epoch)) return;
                        if (quarantined != state.QuarantinedDepthRevision) continue;
                        state.Asks.Clear(); state.Bids.Clear();
                        Apply(state.Asks, depth.Asks); Apply(state.Bids, depth.Bids);
                        baseline = Baseline(instrument, state.Asks, state.Bids, state.Gap);
                        state.LastSnapshot = baseline.Snapshot;
                        state.DepthSequence = null;
                    }
                    DeliverDepth(state, baseline);
                    lock (state.Sync)
                    {
                        if (!IsCurrentSubscription(state, revision, epoch)) return;
                        if (quarantined != state.QuarantinedDepthRevision) continue;
                        state.ActiveEpoch = epoch;
                        state.AcceptDepth = true;
                        state.SubscriptionMayBeActive = true;
                        state.Gap = DepthGapReason.None;
                    }
                }
                await RequestAsync((byte)LongbridgeCommand.Subscribe, LbMessages.Subscribe(new[] { state.Symbol }, firstPush: true), _lifetime.Token).ConfigureAwait(false);
                lock (state.Publication)
                {
                    lock (state.Sync)
                    {
                        if (!IsCurrentSubscription(state, revision, epoch)) return;
                        state.ReadyRevision = revision; state.ReadyEpoch = epoch;
                    }
                    ReportStatus(state, FeedStatus.Connected, "Longbridge data connected with a provider-limited L2 baseline; tape and depth coverage remain partial.");
                }
                return;
            }
            throw new InvalidDataException("Longbridge depth snapshots repeatedly raced unaligned position updates; current depth remains unavailable.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (state.Publication)
            {
                lock (state.Sync)
                {
                    if (_disposed || _session.IsRetired || state.RequestRevision != revision) return;
                    state.AcceptDepth = false; state.ReadyRevision = -1; state.Gap = DepthGapReason.Missing;
                }
                ReportStatus(state, FeedStatus.Error, error.Message);
            }
        }
        finally { if (entered) state.SubscriptionGate.Release(); }
    }
    private static void Deliver(State state, Action<IMarketDataSink, Instrument> action)
    {
        lock (state.Publication)
        {
            Live[] listeners; lock (state.Sync) listeners = state.Listeners.ToArray();
            foreach (var live in listeners) live.Deliver(action);
        }
    }
    private static void DeliverDepth(State state, DepthUpdate update)
    {
        lock (state.Publication)
        {
            Live[] listeners; lock (state.Sync) listeners = state.Listeners.ToArray();
            foreach (var live in listeners) live.DeliverDepth(update);
        }
    }
    private static void ReportStatus(State state, FeedStatus status, string? message)
    {
        lock (state.Publication)
        {
            Live[] listeners; lock (state.Sync) listeners = state.Listeners.ToArray();
            foreach (var live in listeners) live.ReportStatus(status, message);
        }
    }
    private void OnStatus(FeedStatus status, string? message)
    {
        if (!_owned.TryEnter(out var admission)) return;
        using var callback = admission;
        if (_disposed) return;
        Interlocked.Increment(ref _transportEpoch);
        foreach (var state in _states.Values)
        {
            bool synchronize;
            lock (state.Publication)
            {
                DepthUpdate? gap = null;
                lock (state.Sync)
                {
                    state.RequestRevision++; state.AcceptDepth = false; state.ReadyRevision = -1;
                    if (status != FeedStatus.Error) state.SubscriptionMayBeActive = false;
                    state.Asks.Clear(); state.Bids.Clear(); state.DepthSequence = null; state.TapeSequence = null;
                    if (status != FeedStatus.Connected)
                    {
                        var reason = status is FeedStatus.Error or FeedStatus.Connecting ? DepthGapReason.Missing : DepthGapReason.Disconnected;
                        var coverage = reason == DepthGapReason.Missing ? DepthCoverage.Missing : DepthCoverage.Disconnected;
                        state.Gap = reason;
                        if (state.Instrument != null && status != FeedStatus.Connecting) gap = new()
                        {
                            Snapshot = state.LastSnapshot, ProviderId = Id, Venue = state.Instrument.Exchange, ReceivedAt = Now,
                            Coverage = DepthCoverage.PartialBook | coverage | DepthCoverage.SequenceGap,
                            GapBefore = reason, Message = "Longbridge depth interrupted; an actual provider snapshot is required before more position updates.",
                        };
                    }
                    else if (state.Gap == DepthGapReason.None) state.Gap = DepthGapReason.Missing;
                    synchronize = state.Listeners.Count > 0 && (status is FeedStatus.Connected or FeedStatus.Error);
                }
                if (gap != null) DeliverDepth(state, gap);
                ReportStatus(state, status, message);
            }
            if (synchronize) _ = ChangeSubscriptionAsync(state, subscribe: true);
        }
    }
    private void OnPush(byte command, ReadOnlyMemory<byte> bytes)
    {
        if (!_owned.TryEnter(out var admission)) return;
        using var callback = admission;
        if (_disposed || _session.IsRetired) return;
        try
        {
            switch ((LongbridgeCommand)command)
            {
                case LongbridgeCommand.PushQuote:
                    var quote = LbMessages.Quote(bytes.Span, push: true);
                    if (_states.ContainsKey(quote.Symbol)) QuoteReceived?.Invoke(quote);
                    break;
                case LongbridgeCommand.PushTrades:
                    var tape = LbMessages.Tape(bytes.Span, push: true);
                    if (!_states.TryGetValue(tape.Symbol, out var state) || state.Instrument == null) return;
                    Trade[] trades;
                    lock (state.Sync)
                    {
                        if (tape.Sequence.HasValue && state.TapeSequence.HasValue && tape.Sequence <= state.TapeSequence) return;
                        state.TapeSequence = tape.Sequence;
                        trades = tape.Trades.OrderBy(t => t.Timestamp).Select(t => ConvertTrade(state.Instrument, t, "live-tape")).ToArray();
                    }
                    if (trades.Length > 0) Deliver(state, (sink, instrument) => sink.OnTrades(instrument, trades));
                    break;
                case LongbridgeCommand.PushDepth:
                    OnDepth(bytes.Span);
                    break;
            }
        }
        catch (Exception error)
        {
            OnStatus(FeedStatus.Error, "Longbridge data decode failed; continuity is interrupted: " + error.Message);
        }
    }
    private void OnDepth(ReadOnlySpan<byte> bytes)
    {
        var depth = LbMessages.Depth(bytes, push: true);
        if (depth.Asks.Count == 0 && depth.Bids.Count == 0) return;
        if (!_states.TryGetValue(depth.Symbol, out var state) || state.Instrument == null) return;
        bool synchronize = false;
        lock (state.Publication)
        {
            DepthUpdate update;
            lock (state.Sync)
            {
                if (state.Listeners.Count == 0) return;
                if (!state.AcceptDepth || state.ActiveEpoch != Volatile.Read(ref _transportEpoch))
                {
                    state.QuarantinedDepthRevision++;
                    if (state.Gap == DepthGapReason.None) state.Gap = DepthGapReason.Missing;
                    return;
                }
                var instrument = state.Instrument;
                long? previous = state.DepthSequence;
                if (depth.Sequence.HasValue && previous.HasValue && depth.Sequence <= previous)
                {
                    if (depth.Sequence == previous) return;
                    update = new()
                    {
                        Snapshot = state.LastSnapshot, ProviderId = Id, Venue = instrument.Exchange, ReceivedAt = Now, Sequence = depth.Sequence, PreviousSequence = previous,
                        Coverage = DepthCoverage.PartialBook | DepthCoverage.SequenceGap | DepthCoverage.OutOfOrder, GapBefore = DepthGapReason.Sequence,
                        Message = "Out-of-order Longbridge depth was ignored; a fresh provider baseline is required.",
                    };
                    state.Gap = DepthGapReason.Sequence; state.AcceptDepth = false;
                    state.ReadyRevision = -1; state.RequestRevision++; synchronize = true;
                }
                else
                {
                    var before = state.LastSnapshot;
                    Apply(state.Asks, depth.Asks); Apply(state.Bids, depth.Bids);
                    DateTime received = Now;
                    var snapshot = Snapshot(instrument, state.Asks, state.Bids, received);
                    var changes = Changes(before, snapshot);
                    state.LastSnapshot = snapshot; state.DepthSequence = depth.Sequence;
                    update = new()
                    {
                        Snapshot = snapshot, Changes = changes, ProviderId = Id, Venue = instrument.Exchange, ReceivedAt = received,
                        Sequence = depth.Sequence, PreviousSequence = previous, Coverage = DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook,
                        GapBefore = DepthGapReason.None, Message = "Provider-baselined received L2 positions only; receive timestamp only. Price-level changes are derived from position updates; sequence contiguity, unreceived positions and MBO completeness are not supplied.",
                    };
                }
            }
            DeliverDepth(state, update);
        }
        if (synchronize) _ = ChangeSubscriptionAsync(state, subscribe: true);
    }
    private static void Apply(Dictionary<int, LbMessages.Level> positions, IEnumerable<LbMessages.Level> levels)
    {
        foreach (var level in levels)
        {
            if (level.Volume == 0 || level.Price is null or 0) positions.Remove(level.Position);
            else positions[level.Position] = level;
        }
    }
    private static DepthSnapshot Snapshot(Instrument instrument, Dictionary<int, LbMessages.Level> asks, Dictionary<int, LbMessages.Level> bids, DateTime time)
    {
        BookLevel[] Levels(IEnumerable<LbMessages.Level> values, bool descending)
        {
            var result = values.Select(level => { ExactPrice(instrument, level.Price!.Value); return new BookLevel(instrument.ToTicks(level.Price.Value), level.Volume); });
            return descending ? result.OrderByDescending(l => l.PriceTicks).ToArray() : result.OrderBy(l => l.PriceTicks).ToArray();
        }
        return new(Levels(bids.Values, true), Levels(asks.Values, false), time);
    }
    private static IReadOnlyList<BookChange> Changes(DepthSnapshot before, DepthSnapshot after)
    {
        var result = new List<BookChange>();
        void Side(BookSide side, BookLevel[] oldLevels, BookLevel[] newLevels)
        {
            var old = oldLevels.ToDictionary(l => l.PriceTicks, l => l.Size);
            var current = newLevels.ToDictionary(l => l.PriceTicks, l => l.Size);
            foreach (var level in old) if (!current.ContainsKey(level.Key)) result.Add(new(side, level.Key, 0, BookChangeOrigin.DerivedSnapshotDifference));
            foreach (var level in current) if (!old.TryGetValue(level.Key, out var size) || size != level.Value) result.Add(new(side, level.Key, level.Value, BookChangeOrigin.DerivedSnapshotDifference));
        }
        Side(BookSide.Bid, before.Bids, after.Bids); Side(BookSide.Ask, before.Asks, after.Asks);
        return result;
    }
    public void Dispose() => _ = BeginStop();
    public ValueTask DisposeAsync() => new(BeginStop());
    private Task BeginStop()
    {
        lock (_stopGate)
        {
            if (_stop != null) return _stop;
            Volatile.Write(ref _disposed, true);
            var drained = _owned.CloseAsync();
            _stop = Task.Run(() => CompleteStopAsync(drained));
            return _stop;
        }
    }
    private async Task CompleteStopAsync(Task drained)
    {
        try
        {
            try { _transport.Push -= OnPush; _transport.Status -= OnStatus; }
            finally
            {
                if (_transport is IAsyncDisposable asynchronous)
                {
                    Task transport;
                    try { transport = asynchronous.DisposeAsync().AsTask(); }
                    catch (Exception error) { transport = Task.FromException(error); }
                    await Task.WhenAll(drained, transport).ConfigureAwait(false);
                }
                else
                {
                    await drained.ConfigureAwait(false);
                    _transport.Dispose();
                }
            }
        }
        finally
        {
            foreach (var state in _states.Values)
            {
                Live[] listeners; lock (state.Sync) listeners = state.Listeners.ToArray();
                foreach (var live in listeners) live.Dispose();
                state.SubscriptionGate.Dispose();
            }
            _lifetime.Dispose();
        }
    }
}
