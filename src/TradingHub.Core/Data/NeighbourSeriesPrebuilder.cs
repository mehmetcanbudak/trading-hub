using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Builds the neighbouring timeframes of a chart in the background so switching to them restores from
/// <see cref="CandleSeriesCache"/> instead of re-aggregating the whole history on the UI thread.
/// One request runs at a time per owner; a new request, <see cref="Cancel"/> or <see cref="Dispose"/> cancels the previous one.
/// Results are speculative: a corrected/replaced store, a full cache or cancellation drops them without side effects.
/// </summary>
public sealed class NeighbourSeriesPrebuilder : IDisposable
{
    /// <summary>Smaller histories aggregate fast enough on demand; background work would only add churn.</summary>
    public const int DefaultMinimumTrades = 200_000;

    private static readonly ChartPeriod[] Ladder = { ChartPeriod.M1, ChartPeriod.M5, ChartPeriod.M15, ChartPeriod.H1, ChartPeriod.H4, ChartPeriod.D1 };
    private readonly CandleSeriesCache _cache;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task _running = Task.CompletedTask;
    private bool _disposed;
    private int _admitted, _cancelled, _dropped;

    public NeighbourSeriesPrebuilder(CandleSeriesCache cache) => _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public int MinimumTrades { get; set; } = DefaultMinimumTrades;

    /// <summary>Completes when the current request has finished, been cancelled or been dropped. Never faults.</summary>
    public Task Completion { get { lock (_sync) return _running; } }

    public int AdmittedCount => Volatile.Read(ref _admitted);
    public int CancelledCount => Volatile.Read(ref _cancelled);
    public int DroppedCount => Volatile.Read(ref _dropped);
    public string? LastError { get; private set; }

    /// <summary>The fixed ladder neighbours (one shorter, one longer) of a time-based period; empty otherwise.</summary>
    public static IReadOnlyList<ChartPeriod> NeighboursOf(ChartPeriod period)
    {
        int index = Array.IndexOf(Ladder, period);
        if (index < 0) return Array.Empty<ChartPeriod>();
        var result = new List<ChartPeriod>(2);
        if (index + 1 < Ladder.Length) result.Add(Ladder[index + 1]);
        if (index > 0) result.Add(Ladder[index - 1]);
        return result;
    }

    /// <summary>
    /// Starts building <paramref name="periods"/> from the rows currently in <paramref name="store"/>. Call from the store's owner
    /// thread: the store view is captured here, and the worker only reads rows below the captured count.
    /// </summary>
    public void Request(TradeStore store, IReadOnlyList<ChartPeriod> periods, int userStep, int targetRows, SessionSettings? session,
        string providerScope, DateTime availabilityClock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentNullException.ThrowIfNull(providerScope);
        lock (_sync)
        {
            if (_disposed) return; // a late history event after the chart closed
            CancelLocked();
            if (periods.Count == 0 || store.Count < MinimumTrades) return;
            var view = TradeStoreView.Capture(store);
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            // The token is captured now: a later request or dispose disposes the source after cancelling it.
            var token = cancellation.Token;
            var previous = _running;
            var work = periods.ToArray();
            // Sequential behind the cancelled predecessor: never two aggregations of the same chart at once.
            _running = previous.ContinueWith(_ => Build(store, view, work, userStep, targetRows, session, providerScope, availabilityClock, token),
                CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }
    }

    private void Build(TradeStore store, TradeStoreView view, ChartPeriod[] periods, int userStep, int targetRows, SessionSettings? session,
        string providerScope, DateTime availabilityClock, CancellationToken cancellation)
    {
        var instrument = store.Instrument;
        foreach (var period in periods)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                int step = CandleSeries.BaseStep(userStep, CandleSeries.SuggestPriceStep(store, view, instrument, period, targetRows));
                var series = new CandleSeries(instrument, period, step, session);
                if (_cache.Contains(series, store, providerScope)) continue;
                if (!series.BuildThrough(store, view, cancellation)) { Interlocked.Increment(ref _dropped); return; }
                series.AdvanceAvailability(availabilityClock);
                if (_cache.AdmitPrebuilt(series, store, view, providerScope)) Interlocked.Increment(ref _admitted);
                else Interlocked.Increment(ref _dropped);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                return;
            }
            catch (Exception ex) when (!view.IsCurrent(store))
            {
                // The owner corrected or replaced rows while they were read: discard, the key generation is stale anyway.
                Interlocked.Increment(ref _dropped);
                LastError = ex.Message;
                return;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _dropped);
                LastError = $"Background aggregation of {instrument.Symbol} {period.Code} failed: {ex.Message}";
                return;
            }
        }
    }

    /// <summary>Cancels the running request without waiting for it.</summary>
    public void Cancel()
    {
        lock (_sync) CancelLocked();
    }

    private void CancelLocked()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    /// <summary>Cancels and waits for the worker; it observes cancellation within one bounded checkpoint of rows.</summary>
    public void Dispose()
    {
        Task running;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CancelLocked();
            running = _running;
        }
        running.GetAwaiter().GetResult();
    }
}
