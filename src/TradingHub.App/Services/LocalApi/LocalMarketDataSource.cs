using Avalonia.Threading;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Services.LocalApi;

/// <summary>Application identity reported by <c>/api/v1/info</c>.</summary>
public sealed record ApiInfo(string App, string Version);

/// <summary>A market-data connection without credentials, accounts, endpoints or order permissions.</summary>
public sealed record ApiConnection(string Id, string Name, string Kind, bool Enabled, string MarketData);

/// <summary>An instrument currently open in the market-data hub.</summary>
public sealed record ApiOpenInstrument(string Connection, string Symbol, decimal TickSize, decimal? LastPrice, int Trades, bool HasDepth);

/// <summary>One candle; BuyVolume is null when the aggressor side of some of its trades is unknown.</summary>
public sealed record ApiKline(long OpenTimeMs, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal? BuyVolume);

/// <summary>One trade; Side is "b", "s" or null when the aggressor is unknown. Venue is only set for multi-venue feeds.</summary>
public sealed record ApiTrade(long TimeMs, decimal Price, decimal Size, string? Side, string? Venue);

public readonly record struct ApiBookLevel(decimal Price, decimal Size);

/// <summary>Order book; TimeMs is null when the provider supplied no book time.</summary>
public sealed record ApiBook(long? TimeMs, IReadOnlyList<ApiBookLevel> Bids, IReadOnlyList<ApiBookLevel> Asks);

/// <summary>One recorded depth sample (receive time), as stored by opt-in depth recording.</summary>
public sealed record ApiLiquiditySample(long TimeMs, bool Usable, bool GapBefore, IReadOnlyList<ApiBookLevel> Bids, IReadOnlyList<ApiBookLevel> Asks);

/// <summary>Recorded depth samples; Recording is false (and Samples empty) when depth recording is off.</summary>
public sealed record ApiLiquidity(bool Recording, decimal TickSize, IReadOnlyList<ApiLiquiditySample> Samples, bool Truncated);

/// <summary>Candle request; Minutes is one of 1, 5, 15, 60, 240 or 1440.</summary>
public sealed record ApiKlineRequest(string Connection, string Symbol, int Minutes, DateTime FromUtc, DateTime ToUtc);

/// <summary>New trades for a stream; Skipped counts trades the source did not convert because the batch exceeded its bound.</summary>
public sealed record ApiTradeBatch(IReadOnlyList<ApiTrade> Trades, int Skipped);

/// <summary>An active stream subscription; disposing it detaches every handler.</summary>
public sealed record ApiStreamSubscription(string Symbol, IDisposable Subscription);

public enum ApiResultStatus { Ok, NotFound, NotOpen, Unavailable }

/// <summary>Result of a source query, distinguishing unknown instruments from instruments that are not open.</summary>
public readonly record struct ApiResult<T>(ApiResultStatus Status, T? Value, string Code, string Message)
{
    public static ApiResult<T> Ok(T value) => new(ApiResultStatus.Ok, value, "", "");
    public static ApiResult<T> NotFound(string code, string message) => new(ApiResultStatus.NotFound, default, code, message);
    public static ApiResult<T> NotOpen(string message) => new(ApiResultStatus.NotOpen, default, "not_open", message);
    public static ApiResult<T> Unavailable(string message) => new(ApiResultStatus.Unavailable, default, "unavailable", message);
}

/// <summary>
/// Read-only market data served by <see cref="LocalMarketDataApi"/>. Implementations serve only data Trading Hub already
/// holds: they never create feeds, read credentials, open subscriptions, request provider history or catalogs, or mutate state.
/// </summary>
public interface ILocalMarketDataSource
{
    Task<ApiResult<ApiInfo>> GetInfoAsync(CancellationToken ct);
    Task<ApiResult<IReadOnlyList<ApiConnection>>> GetConnectionsAsync(CancellationToken ct);
    Task<ApiResult<IReadOnlyList<ApiOpenInstrument>>> GetOpenInstrumentsAsync(CancellationToken ct);
    Task<ApiResult<IReadOnlyList<ApiKline>>> GetKlinesAsync(ApiKlineRequest request, CancellationToken ct);
    Task<ApiResult<IReadOnlyList<ApiTrade>>> GetTradesAsync(string connection, string symbol, int limit, CancellationToken ct);
    Task<ApiResult<ApiBook>> GetDepthAsync(string connection, string symbol, int levels, CancellationToken ct);
    Task<ApiResult<ApiLiquidity>> GetLiquidityAsync(string connection, string symbol, TimeSpan window, int levels, int maxSamples, CancellationToken ct);

    /// <summary>
    /// Attaches to an already open instrument. Callbacks may run on any thread; onBook receives a factory that builds
    /// the book with at most the requested number of levels per side.
    /// </summary>
    Task<ApiResult<ApiStreamSubscription>> SubscribeStreamAsync(string connection, string symbol,
        Action<ApiTradeBatch> onTrades, Action<Func<int, ApiBook>> onBook, CancellationToken ct);
}

/// <summary>Candles built from an open instrument's loaded trades, in UTC-aligned bars.</summary>
internal static class ApiKlineAggregation
{
    private const int Checkpoint = 1 << 15;

    /// <summary>
    /// Aggregates rows [<paramref name="first"/>, <paramref name="end"/>) captured by <paramref name="view"/> (safe from a
    /// background thread). A row whose source window does not fit inside one bar (compact or candle-derived history coarser
    /// than the bar) is left out, so such bars are absent rather than invented. BuyVolume is null when any row of the bar
    /// has no aggressor side. Returns null when the store was corrected or replaced while it was read.
    /// </summary>
    public static List<ApiKline>? FromTrades(TradeStore store, TradeStoreView view, int first, int end, int minutes, CancellationToken ct)
    {
        long step = minutes * TimeSpan.TicksPerMinute;
        var result = new List<ApiKline>();
        long open = long.MinValue;
        decimal o = 0, h = 0, l = 0, c = 0, volume = 0, buy = 0;
        bool hasBuy = true;
        void Flush()
        {
            if (open != long.MinValue)
                result.Add(new ApiKline((open - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond, o, h, l, c, volume, hasBuy ? buy : null));
        }
        end = Math.Min(end, view.Count);
        try
        {
            for (int i = first; i < end; i++)
            {
                if (((i - first) & (Checkpoint - 1)) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!view.IsCurrent(store)) return null;
                }
                long ticks = store.GetTimeTicks(i);
                long bucket = ticks - ticks % step;
                var provenance = store.GetProvenance(i);
                if (provenance.ResolutionSeconds > 0)
                {
                    var (from, to) = provenance.SourceWindow(new DateTime(ticks, DateTimeKind.Utc));
                    if (from.Ticks < bucket || to.Ticks > bucket + step) continue;
                }
                decimal price = store.GetPrice(i), size = store.GetVolume(i);
                if (bucket != open)
                {
                    Flush();
                    open = bucket;
                    o = h = l = price;
                    volume = 0;
                    buy = 0;
                    hasBuy = true;
                }
                if (price > h) h = price;
                if (price < l) l = price;
                c = price;
                volume += size;
                switch (store.GetSide(i))
                {
                    case TradeSide.Buy: buy += size; break;
                    case TradeSide.Sell: break;
                    default: hasBuy = false; break;
                }
            }
        }
        catch (Exception) when (!view.IsCurrent(store))
        {
            // a correction or replacement moved the rows while they were read
            return null;
        }
        Flush();
        return view.IsCurrent(store) ? result : null;
    }
}

/// <summary>
/// The application's market data for the local API, strictly read-only over what Trading Hub already holds: every route
/// serves instruments that are already open in the market-data hub, and candles are built from their loaded trades.
/// Nothing here creates a feed, reads a stored secret, downloads a catalog, requests provider history or calls
/// <see cref="AppServices.Open"/>. Hub and connection reads run on the thread that owns them (the UI thread); larger
/// projections run off it on captured append-only or immutable data. Work is refused once Trading Hub is shutting down.
/// </summary>
internal sealed class AppLocalMarketDataSource(Func<bool> shuttingDown, ConnectionManager connections,
    Func<IEnumerable<InstrumentData>> openInstruments, Action<Action> post) : ILocalMarketDataSource
{
    private const string ShuttingDown = "Trading Hub is shutting down.";
    private const string HistoryChanged = "The instrument's history changed while it was read; try again.";
    private const string NotOpenMessage = "That instrument is not open in Trading Hub. Open it in a chart first; the API never opens market subscriptions or requests history.";
    private const int MaxStreamConversion = 20_000;

    public AppLocalMarketDataSource(AppServices svc) : this(() => svc.IsShuttingDown, svc.Connections, () => svc.Hub.Items, PostToUi) { }

    private static void PostToUi(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    /// <summary>Only display identity and the market-data id; never keys, users, accounts, endpoints, namespaces or order permissions.</summary>
    internal static IReadOnlyList<ApiConnection> ProjectConnections(IEnumerable<ConnectionProfile> profiles) =>
        profiles.Where(p => p.HasMarketData)
            .Select(p => new ApiConnection(p.Id, p.Name, p.Kind.ToString(), p.Enabled, p.MarketDataId))
            .ToList();

    public Task<ApiResult<ApiInfo>> GetInfoAsync(CancellationToken ct) =>
        Task.FromResult(shuttingDown()
            ? ApiResult<ApiInfo>.Unavailable(ShuttingDown)
            : ApiResult<ApiInfo>.Ok(new ApiInfo(TradingHub.Common.Product.Name, typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "")));

    public Task<ApiResult<IReadOnlyList<ApiConnection>>> GetConnectionsAsync(CancellationToken ct) =>
        OnUi(() => shuttingDown()
            ? ApiResult<IReadOnlyList<ApiConnection>>.Unavailable(ShuttingDown)
            : ApiResult<IReadOnlyList<ApiConnection>>.Ok(ProjectConnections(connections.Profiles)), ct);

    public Task<ApiResult<IReadOnlyList<ApiOpenInstrument>>> GetOpenInstrumentsAsync(CancellationToken ct) =>
        OnUi(() =>
        {
            if (shuttingDown()) return ApiResult<IReadOnlyList<ApiOpenInstrument>>.Unavailable(ShuttingDown);
            IReadOnlyList<ApiOpenInstrument> list = openInstruments().Where(d => d.Feed is not ReplayFeed)
                .Select(d => new ApiOpenInstrument(d.Instrument.FeedId, d.Instrument.Symbol, d.Instrument.TickSize,
                    d.Trades.Count > 0 ? d.LastPrice : null, d.Trades.Count, d.Depth.HasBid || d.Depth.HasAsk))
                .ToList();
            return ApiResult<IReadOnlyList<ApiOpenInstrument>>.Ok(list);
        }, ct);

    /// <summary>Rows of an open instrument's trade store captured on the UI thread for a background read.</summary>
    private sealed record TradeRows(InstrumentData Data, TradeStoreView View, int First, int End);

    public async Task<ApiResult<IReadOnlyList<ApiKline>>> GetKlinesAsync(ApiKlineRequest request, CancellationToken ct)
    {
        long step = request.Minutes * TimeSpan.TicksPerMinute;
        var from = new DateTime(request.FromUtc.Ticks - request.FromUtc.Ticks % step, DateTimeKind.Utc);
        var rows = await OnUi(() =>
        {
            if (shuttingDown()) return ApiResult<TradeRows>.Unavailable(ShuttingDown);
            if (FindOpen(request.Connection, request.Symbol) is not { } data) return ApiResult<TradeRows>.NotOpen(NotOpenMessage);
            var store = data.Trades;
            return ApiResult<TradeRows>.Ok(new TradeRows(data, TradeStoreView.Capture(store), store.LowerBound(from), store.LowerBound(request.ToUtc)));
        }, ct).ConfigureAwait(false);
        if (rows.Value is not { } range) return Failed<TradeRows, IReadOnlyList<ApiKline>>(rows);
        var bars = ApiKlineAggregation.FromTrades(range.Data.Trades, range.View, range.First, range.End, request.Minutes, ct);
        return bars == null ? ApiResult<IReadOnlyList<ApiKline>>.Unavailable(HistoryChanged) : ApiResult<IReadOnlyList<ApiKline>>.Ok(bars);
    }

    public async Task<ApiResult<IReadOnlyList<ApiTrade>>> GetTradesAsync(string connection, string symbol, int limit, CancellationToken ct)
    {
        var rows = await OnUi(() =>
        {
            if (shuttingDown()) return ApiResult<TradeRows>.Unavailable(ShuttingDown);
            if (FindOpen(connection, symbol) is not { } data) return ApiResult<TradeRows>.NotOpen(NotOpenMessage);
            var view = TradeStoreView.Capture(data.Trades);
            return ApiResult<TradeRows>.Ok(new TradeRows(data, view, Math.Max(0, view.Count - limit), view.Count));
        }, ct).ConfigureAwait(false);
        if (rows.Value is not { } range) return Failed<TradeRows, IReadOnlyList<ApiTrade>>(rows);
        var store = range.Data.Trades;
        try
        {
            var trades = Trades(range.Data, range.First, range.End);
            if (range.View.IsCurrent(store)) return ApiResult<IReadOnlyList<ApiTrade>>.Ok(trades);
        }
        catch (Exception) when (!range.View.IsCurrent(store))
        {
            // a correction or replacement moved the rows while they were read
        }
        return ApiResult<IReadOnlyList<ApiTrade>>.Unavailable(HistoryChanged);
    }

    public Task<ApiResult<ApiBook>> GetDepthAsync(string connection, string symbol, int levels, CancellationToken ct) =>
        OnUi(() =>
        {
            if (shuttingDown()) return ApiResult<ApiBook>.Unavailable(ShuttingDown);
            if (FindOpen(connection, symbol) is not { } data) return ApiResult<ApiBook>.NotOpen(NotOpenMessage);
            return ApiResult<ApiBook>.Ok(Book(data.Instrument, data.Depth, levels));
        }, ct);

    private sealed record DepthSource(InstrumentData Data, bool Recording);

    public async Task<ApiResult<ApiLiquidity>> GetLiquidityAsync(string connection, string symbol, TimeSpan window, int levels, int maxSamples, CancellationToken ct)
    {
        // Only the instrument is resolved on the UI thread; the recorder's query is locked and its snapshots are immutable.
        var target = await OnUi(() =>
        {
            if (shuttingDown()) return ApiResult<DepthSource>.Unavailable(ShuttingDown);
            if (FindOpen(connection, symbol) is not { } data) return ApiResult<DepthSource>.NotOpen(NotOpenMessage);
            return ApiResult<DepthSource>.Ok(new DepthSource(data, data.DepthRecording.Enabled));
        }, ct).ConfigureAwait(false);
        if (target.Value is not { } source) return Failed<DepthSource, ApiLiquidity>(target);
        var instrument = source.Data.Instrument;
        if (!source.Recording) return ApiResult<ApiLiquidity>.Ok(new ApiLiquidity(false, instrument.TickSize, Array.Empty<ApiLiquiditySample>(), false));
        var now = DateTime.UtcNow;
        var query = source.Data.DepthHistory.Query(now - window, now.AddSeconds(1));
        var samples = query.Samples;
        int start = Math.Max(0, samples.Count - maxSamples);
        var list = new List<ApiLiquiditySample>(samples.Count - start);
        for (int i = start; i < samples.Count; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            var sample = samples[i];
            var book = Book(instrument, sample.Snapshot, levels);
            list.Add(new ApiLiquiditySample(ToMs(sample.Time), sample.IsUsable, sample.GapBefore != DepthGapReason.None, book.Bids, book.Asks));
        }
        return ApiResult<ApiLiquidity>.Ok(new ApiLiquidity(true, instrument.TickSize, list, start > 0 || query.IsTruncated));
    }

    public async Task<ApiResult<ApiStreamSubscription>> SubscribeStreamAsync(string connection, string symbol,
        Action<ApiTradeBatch> onTrades, Action<Func<int, ApiBook>> onBook, CancellationToken ct)
    {
        var subscribe = OnUiCore(() =>
        {
            if (shuttingDown()) return ApiResult<ApiStreamSubscription>.Unavailable(ShuttingDown);
            if (FindOpen(connection, symbol) is not { } data) return ApiResult<ApiStreamSubscription>.NotOpen(NotOpenMessage);
            var instrument = data.Instrument;
            void OnTrades(int start, int count)
            {
                int end = Math.Min(start + count, data.Trades.Count);
                int from = Math.Max(start, end - MaxStreamConversion);
                if (end <= from) return;
                onTrades(new ApiTradeBatch(Trades(data, from, end), from - start));
            }
            void OnDepth(DepthUpdate update)
            {
                // The vetted book: InstrumentData empties it for disconnected, out-of-order or unrefreshed updates before raising the event.
                var snapshot = data.Depth;
                onBook(levels => Book(instrument, snapshot, levels));
            }
            data.TradesAppended += OnTrades;
            data.DepthUpdated += OnDepth;
            var current = data.Depth;
            if (current.HasBid || current.HasAsk) onBook(levels => Book(instrument, current, levels));
            return ApiResult<ApiStreamSubscription>.Ok(new ApiStreamSubscription(instrument.Symbol, new UiSubscription(post, () =>
            {
                data.TradesAppended -= OnTrades;
                data.DepthUpdated -= OnDepth;
            })));
        });
        try
        {
            return await subscribe.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The UI thread may still attach the handlers after the caller gave up: detach them once it does.
            _ = subscribe.ContinueWith(t => t.Result.Value?.Subscription.Dispose(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            throw;
        }
    }

    private static ApiResult<TOut> Failed<TIn, TOut>(ApiResult<TIn> result) => new(result.Status, default, result.Code, result.Message);

    private InstrumentData? FindOpen(string connection, string symbol)
    {
        string feedId = connections.Find(connection)?.MarketDataId ?? connection;
        return openInstruments().FirstOrDefault(d => d.Feed is not ReplayFeed && d.Instrument.FeedId == feedId &&
            d.Instrument.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ApiTrade> Trades(InstrumentData data, int from, int to)
    {
        var store = data.Trades;
        bool venues = data.Feed is AggregateFeed;
        var list = new List<ApiTrade>(Math.Max(0, to - from));
        for (int i = from; i < to; i++)
        {
            string? side = store.GetSide(i) switch { TradeSide.Buy => "b", TradeSide.Sell => "s", _ => null };
            string? venue = venues ? store.GetProvenance(i).SourceIdentity : null;
            list.Add(new ApiTrade((store.GetTimeTicks(i) - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond,
                store.GetPrice(i), store.GetVolume(i), side, venue));
        }
        return list;
    }

    private static ApiBook Book(Instrument instrument, DepthSnapshot snapshot, int levels)
    {
        static ApiBookLevel[] Side(Instrument instrument, BookLevel[] side, int levels)
        {
            var result = new ApiBookLevel[Math.Min(levels, side.Length)];
            for (int i = 0; i < result.Length; i++) result[i] = new ApiBookLevel(instrument.FromTicks(side[i].PriceTicks), side[i].Size);
            return result;
        }
        long? time = snapshot.Time == DateTime.MinValue ? null : ToMs(snapshot.Time);
        return new ApiBook(time, Side(instrument, snapshot.Bids, levels), Side(instrument, snapshot.Asks, levels));
    }

    private static long ToMs(DateTime time) => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private Task<T> OnUi<T>(Func<T> read, CancellationToken ct) => OnUiCore(read).WaitAsync(ct);

    /// <summary>Runs on the UI thread; the returned task completes even if the caller stops waiting.</summary>
    private Task<T> OnUiCore<T>(Func<T> read)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        post(() =>
        {
            try { completion.TrySetResult(read()); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    /// <summary>Detaches on the UI thread exactly once.</summary>
    private sealed class UiSubscription(Action<Action> post, Action detach) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            post(detach);
        }
    }
}
