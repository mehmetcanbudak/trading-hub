using Avalonia.Threading;
using TradingHub.App.Controls;
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

/// <summary>One candle; BuyVolume is null when the provider does not report it.</summary>
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

/// <summary>Read-only market data served by <see cref="LocalMarketDataApi"/>. Implementations never open subscriptions or mutate state.</summary>
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

/// <summary>Candle aggregation from the cached 1/5/15/60-minute granularities into UTC-aligned larger bars.</summary>
internal static class ApiKlineAggregation
{
    public static int BaseMinutes(int minutes) => minutes % 60 == 0 ? 60 : minutes % 15 == 0 ? 15 : minutes % 5 == 0 ? 5 : 1;

    public static List<ApiKline> Aggregate(IReadOnlyList<Kline> source, int minutes)
    {
        long step = minutes * 60_000L;
        var result = new List<ApiKline>();
        long open = long.MinValue;
        decimal o = 0, h = 0, l = 0, c = 0, volume = 0, buy = 0;
        bool hasBuy = true;
        void Flush()
        {
            if (open != long.MinValue) result.Add(new ApiKline(open, o, h, l, c, volume, hasBuy ? buy : null));
        }
        foreach (var k in source.OrderBy(k => k.OpenTimeMs))
        {
            long bucket = FloorDiv(k.OpenTimeMs, step) * step;
            if (bucket != open)
            {
                Flush();
                open = bucket;
                o = k.Open;
                h = k.High;
                l = k.Low;
                volume = 0;
                buy = 0;
                hasBuy = true;
            }
            h = Math.Max(h, k.High);
            l = Math.Min(l, k.Low);
            c = k.Close;
            volume += k.Volume;
            buy += k.BuyVolume;
            hasBuy &= k.HasBuyVolume;
        }
        Flush();
        return result;
    }

    private static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }
}

/// <summary>
/// The application's market data for the local API. Every hub, instrument and connection read is marshalled to the UI
/// thread that owns them; work is refused once Trading Hub is shutting down, and <see cref="AppServices.Open"/> is never called.
/// </summary>
internal sealed class AppLocalMarketDataSource(AppServices svc) : ILocalMarketDataSource
{
    private const string ShuttingDown = "Trading Hub is shutting down.";
    private const int MaxStreamConversion = 20_000;

    /// <summary>Only display identity and the market-data id; never keys, users, accounts, endpoints, namespaces or order permissions.</summary>
    internal static IReadOnlyList<ApiConnection> ProjectConnections(IEnumerable<ConnectionProfile> profiles) =>
        profiles.Where(p => p.HasMarketData)
            .Select(p => new ApiConnection(p.Id, p.Name, p.Kind.ToString(), p.Enabled, p.MarketDataId))
            .ToList();

    public Task<ApiResult<ApiInfo>> GetInfoAsync(CancellationToken ct) =>
        Task.FromResult(svc.IsShuttingDown
            ? ApiResult<ApiInfo>.Unavailable(ShuttingDown)
            : ApiResult<ApiInfo>.Ok(new ApiInfo(TradingHub.Common.Product.Name, typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "")));

    public Task<ApiResult<IReadOnlyList<ApiConnection>>> GetConnectionsAsync(CancellationToken ct) =>
        OnUi(() => svc.IsShuttingDown
            ? ApiResult<IReadOnlyList<ApiConnection>>.Unavailable(ShuttingDown)
            : ApiResult<IReadOnlyList<ApiConnection>>.Ok(ProjectConnections(svc.Connections.Profiles)), ct);

    public Task<ApiResult<IReadOnlyList<ApiOpenInstrument>>> GetOpenInstrumentsAsync(CancellationToken ct) =>
        OnUi(() =>
        {
            if (svc.IsShuttingDown) return ApiResult<IReadOnlyList<ApiOpenInstrument>>.Unavailable(ShuttingDown);
            IReadOnlyList<ApiOpenInstrument> list = svc.Hub.Items.Where(d => d.Feed is not ReplayFeed)
                .Select(d => new ApiOpenInstrument(d.Instrument.FeedId, d.Instrument.Symbol, d.Instrument.TickSize,
                    d.Trades.Count > 0 ? d.LastPrice : null, d.Trades.Count, d.Depth.HasBid || d.Depth.HasAsk))
                .ToList();
            return ApiResult<IReadOnlyList<ApiOpenInstrument>>.Ok(list);
        }, ct);

    private sealed record KlineTarget(ApiResult<IReadOnlyList<ApiKline>> Error, IKlineHistory? Source = null, Instrument? Instrument = null);

    public async Task<ApiResult<IReadOnlyList<ApiKline>>> GetKlinesAsync(ApiKlineRequest request, CancellationToken ct)
    {
        var resolved = await OnUiAsync<KlineTarget>(async () =>
        {
            if (svc.IsShuttingDown) return new(ApiResult<IReadOnlyList<ApiKline>>.Unavailable(ShuttingDown));
            var profile = svc.Connections.Find(request.Connection);
            if (profile is not { Enabled: true, HasMarketData: true })
                return new(ApiResult<IReadOnlyList<ApiKline>>.NotFound("unknown_connection", "No enabled market-data connection has that id."));
            if (svc.Connections.GetFeed(profile.Id) is not IKlineHistory source)
                return new(ApiResult<IReadOnlyList<ApiKline>>.NotFound("no_klines", "That connection does not provide candle history."));
            var instruments = await InstrumentPicker.InstrumentsAsync(svc, profile.Id, ct);
            var instrument = instruments.FirstOrDefault(i => i.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase));
            if (instrument == null) return new(ApiResult<IReadOnlyList<ApiKline>>.NotFound("unknown_symbol", "That connection does not list that symbol."));
            return new(default, source, instrument);
        }).WaitAsync(ct).ConfigureAwait(false);
        if (resolved.Source == null || resolved.Instrument == null) return resolved.Error;

        int baseMinutes = ApiKlineAggregation.BaseMinutes(request.Minutes);
        long step = request.Minutes * 60_000L;
        long fromMs = new DateTimeOffset(DateTime.SpecifyKind(request.FromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var from = DateTimeOffset.FromUnixTimeMilliseconds(fromMs - (fromMs % step + step) % step).UtcDateTime;
        // Same cache and provider back-fill a chart uses: missing bars come from the connection's own provider.
        var bars = await KlineHistory.LoadAsync(resolved.Source, resolved.Instrument, from, request.ToUtc, baseMinutes, 0, null, ct).ConfigureAwait(false);
        IReadOnlyList<ApiKline> result = ApiKlineAggregation.Aggregate(bars, request.Minutes);
        return ApiResult<IReadOnlyList<ApiKline>>.Ok(result);
    }

    public Task<ApiResult<IReadOnlyList<ApiTrade>>> GetTradesAsync(string connection, string symbol, int limit, CancellationToken ct) =>
        OnUi(() =>
        {
            if (svc.IsShuttingDown) return ApiResult<IReadOnlyList<ApiTrade>>.Unavailable(ShuttingDown);
            if (FindOpen(connection, symbol) is not { } data) return ApiResult<IReadOnlyList<ApiTrade>>.NotOpen(NotOpenMessage);
            int count = data.Trades.Count;
            return ApiResult<IReadOnlyList<ApiTrade>>.Ok(Trades(data, Math.Max(0, count - limit), count));
        }, ct);

    public Task<ApiResult<ApiBook>> GetDepthAsync(string connection, string symbol, int levels, CancellationToken ct) =>
        OnUi(() =>
        {
            if (svc.IsShuttingDown) return ApiResult<ApiBook>.Unavailable(ShuttingDown);
            if (FindOpen(connection, symbol) is not { } data) return ApiResult<ApiBook>.NotOpen(NotOpenMessage);
            return ApiResult<ApiBook>.Ok(Book(data.Instrument, data.Depth, levels));
        }, ct);

    public Task<ApiResult<ApiLiquidity>> GetLiquidityAsync(string connection, string symbol, TimeSpan window, int levels, int maxSamples, CancellationToken ct) =>
        OnUi(() =>
        {
            if (svc.IsShuttingDown) return ApiResult<ApiLiquidity>.Unavailable(ShuttingDown);
            if (FindOpen(connection, symbol) is not { } data) return ApiResult<ApiLiquidity>.NotOpen(NotOpenMessage);
            var instrument = data.Instrument;
            if (!data.DepthRecording.Enabled) return ApiResult<ApiLiquidity>.Ok(new ApiLiquidity(false, instrument.TickSize, Array.Empty<ApiLiquiditySample>(), false));
            var now = DateTime.UtcNow;
            var query = data.DepthHistory.Query(now - window, now.AddSeconds(1));
            var samples = query.Samples;
            int start = Math.Max(0, samples.Count - maxSamples);
            var list = new List<ApiLiquiditySample>(samples.Count - start);
            for (int i = start; i < samples.Count; i++)
            {
                var sample = samples[i];
                var book = Book(instrument, sample.Snapshot, levels);
                list.Add(new ApiLiquiditySample(ToMs(sample.Time), sample.IsUsable, sample.GapBefore != DepthGapReason.None, book.Bids, book.Asks));
            }
            return ApiResult<ApiLiquidity>.Ok(new ApiLiquidity(true, instrument.TickSize, list, start > 0 || query.IsTruncated));
        }, ct);

    public async Task<ApiResult<ApiStreamSubscription>> SubscribeStreamAsync(string connection, string symbol,
        Action<ApiTradeBatch> onTrades, Action<Func<int, ApiBook>> onBook, CancellationToken ct)
    {
        var subscribe = OnUiCore(() =>
        {
            if (svc.IsShuttingDown) return ApiResult<ApiStreamSubscription>.Unavailable(ShuttingDown);
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
                var snapshot = update.Snapshot;
                onBook(levels => Book(instrument, snapshot, levels));
            }
            data.TradesAppended += OnTrades;
            data.DepthUpdated += OnDepth;
            var current = data.Depth;
            if (current.HasBid || current.HasAsk) onBook(levels => Book(instrument, current, levels));
            return ApiResult<ApiStreamSubscription>.Ok(new ApiStreamSubscription(instrument.Symbol, new UiSubscription(() =>
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

    private const string NotOpenMessage = "That instrument is not open in Trading Hub. Open it in a chart first; the API never opens market subscriptions.";

    private InstrumentData? FindOpen(string connection, string symbol)
    {
        string feedId = svc.Connections.Find(connection)?.MarketDataId ?? connection;
        return svc.Hub.Items.FirstOrDefault(d => d.Feed is not ReplayFeed && d.Instrument.FeedId == feedId &&
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

    private static Task<T> OnUi<T>(Func<T> read, CancellationToken ct) => OnUiCore(read).WaitAsync(ct);

    /// <summary>Runs on the UI thread; the returned task completes even if the caller stops waiting.</summary>
    private static Task<T> OnUiCore<T>(Func<T> read)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try { completion.TrySetResult(read()); }
            catch (Exception error) { completion.TrySetException(error); }
        }, DispatcherPriority.Background);
        return completion.Task;
    }

    private static Task<T> OnUiAsync<T>(Func<Task<T>> read)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.TrySetResult(await read()); }
            catch (OperationCanceledException canceled) { completion.TrySetCanceled(canceled.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        }, DispatcherPriority.Background);
        return completion.Task;
    }

    /// <summary>Detaches on the UI thread exactly once.</summary>
    private sealed class UiSubscription(Action detach) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Dispatcher.UIThread.CheckAccess()) detach();
            else Dispatcher.UIThread.Post(detach, DispatcherPriority.Background);
        }
    }
}
