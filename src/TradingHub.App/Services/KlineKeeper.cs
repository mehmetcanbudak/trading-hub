using System.Threading.Channels;
using TradingHub.Market;

namespace TradingHub.App.Services;

public sealed record TrackedCandleInstrument(string ProfileId, string ProviderIdentity, Instrument Instrument)
{
    public string Key => ProfileId + "|" + Instrument.Symbol;
}
public sealed record KlineMaintenanceSettings
{
    public bool Enabled { get; set; }
    public bool AutoTrackOpened { get; set; } = true;
    public int LookbackYears { get; set; } = 1;
    public int IntervalMinutes { get; set; } = 30;
    public int MaxInstruments { get; set; } = 30;
    public List<TrackedCandleInstrument> Instruments { get; set; } = new();
}
/// <param name="CoverageOnly">The provider answered with incomplete or invalid candles; its transport and the cache worked.</param>
public sealed record KlineMaintenanceFailure(string InstrumentKey, DateTime MonthUtc, int Minutes, string Message, bool CoverageOnly = false);
public sealed record KlineKeeperStatus(bool IsRunning, string Message, DateTime? LastRunUtc,
    IReadOnlyDictionary<string, string> Instruments)
{
    public IReadOnlyList<KlineMaintenanceFailure> Failures { get; init; } = Array.Empty<KlineMaintenanceFailure>();
    /// <summary>The latest run request (Update now or a background wake); increases by one per request.</summary>
    public long RunRequest { get; init; }
    /// <summary>Every request up to this one was served by a cycle that started after it and completed.</summary>
    public long CompletedRunRequest { get; init; }
    /// <summary>Cycles started so far: while one runs, its own number.</summary>
    public long Cycle { get; init; }
}

/// <summary>
/// Explicitly opted-in, bounded candle maintenance. Foreground and maintenance share KlineHistory's monthly
/// cache gates and incremental publication. Only one monthly result is retained at a time. Disabling and
/// disposal cancel and drain the owned run before transports or stores can be disposed.
/// Inspired by KVP's keeper; the lifecycle, bounds and opt-in semantics are redesigned.
/// </summary>
public sealed class KlineKeeper : IAsyncDisposable
{
    public const int MaxTrackedInstruments = 60;
    public const int MaxMonthlyLoadsPerCycle = 24;
    /// <summary>Bound of one maintenance cycle; remaining work continues in later cycles.</summary>
    internal TimeSpan CycleLimit { get; init; } = TimeSpan.FromMinutes(5);
    private static readonly int[] Resolutions = { 1, 5, 15, 60 };
    private readonly object _sync = new();
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Func<TrackedCandleInstrument, DateTime, DateTime, int, CancellationToken, Task<int>> _update;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<KlineMaintenanceSettings>? _save;
    private readonly Dictionary<string, int> _cursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _itemStatus = new(StringComparer.Ordinal);
    // At most one unresolved slot per instrument/resolution; retry it before requesting another slot.
    private readonly Dictionary<(string InstrumentKey, int Minutes), KlineMaintenanceFailure> _failures = new();
    private KlineMaintenanceSettings _settings;
    private CancellationTokenSource? _active;
    private Task? _loop;
    private Task? _disposeTask;
    private bool _disposed, _running;
    private long _requested, _completedThrough, _cancelledThrough, _cycles;
    // Completes when the running cycle has drained; null while no cycle runs.
    private TaskCompletionSource? _cycle;
    private int _instrumentCursor;
    private string _message = "Candle maintenance is disabled.";
    private DateTime? _lastRun;
    public event Action? Changed;

    public KlineKeeper(KlineMaintenanceSettings settings,
        Func<TrackedCandleInstrument, DateTime, DateTime, int, CancellationToken, Task<int>> update,
        Action<KlineMaintenanceSettings>? save = null, Func<DateTime>? utcNow = null)
    {
        _settings = Normalize(settings);
        _update = update;
        _save = save;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        if (_settings.Enabled) _message = "Candle maintenance is waiting.";
    }
    public KlineMaintenanceSettings Settings { get { lock (_sync) return Normalize(_settings); } }
    public KlineKeeperStatus Status
    {
        get
        {
            lock (_sync) return new(_running, _message, _lastRun, new Dictionary<string, string>(_itemStatus))
            {
                Failures = _failures.Values.ToArray(), RunRequest = _requested, CompletedRunRequest = _completedThrough, Cycle = _cycles,
            };
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(LoopAsync);
        }
    }
    public async Task ConfigureAsync(KlineMaintenanceSettings settings)
    {
        Exception? persistenceError = null;
        Task drain;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _settings = Normalize(settings);
            if (!_settings.Enabled) _cancelledThrough = _requested;
            drain = CancelActiveCycle();
            // Persist on the configuring caller before the first await, with no late save after disposal.
            try { _save?.Invoke(Normalize(_settings)); }
            catch (Exception ex) { persistenceError = ex; }
        }
        // Also drains a provider that takes time to honor cancellation; no detached write survives disable. Only the
        // cycle this change cancelled is awaited: a request queued behind it runs a fresh cycle with the new settings.
        await drain.ConfigureAwait(false);
        lock (_sync)
        {
            var keys = _settings.Instruments.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _cursors.Keys.Where(key => !keys.Contains(key)).ToArray()) _cursors.Remove(key);
            foreach (var key in _itemStatus.Keys.Where(key => !keys.Contains(key)).ToArray()) _itemStatus.Remove(key);
            foreach (var key in _failures.Keys.Where(key => !keys.Contains(key.InstrumentKey)).ToArray()) _failures.Remove(key);
        }
        if (Settings.Enabled) _wake.Writer.TryWrite(true);
        else lock (_sync) _message = "Candle maintenance is disabled; all owned work has drained.";
        Notify();
        if (persistenceError != null) throw new IOException("Candle maintenance changed, but saving its preference failed.", persistenceError);
    }
    public void Track(string profileId, string providerIdentity, Instrument instrument, bool explicitlySelected = false)
    {
        lock (_sync)
        {
            if (_disposed || !_settings.Enabled || (!explicitlySelected && !_settings.AutoTrackOpened)) return;
            var tracked = new TrackedCandleInstrument(profileId, providerIdentity, instrument);
            int index = _settings.Instruments.FindIndex(i => i.Key == tracked.Key);
            if (index >= 0)
            {
                var previous = _settings.Instruments[index];
                if (previous.ProviderIdentity == providerIdentity && previous.Instrument.TickSize == instrument.TickSize &&
                    previous.Instrument.VolumeStep == instrument.VolumeStep && previous.Instrument.PointValue == instrument.PointValue &&
                    previous.Instrument.FeedId == instrument.FeedId && previous.Instrument.Exchange == instrument.Exchange &&
                    previous.Instrument.Description == instrument.Description) return;
                _settings.Instruments[index] = tracked;
            }
            else
            {
                if (_settings.Instruments.Count >= _settings.MaxInstruments) return;
                _settings.Instruments.Add(tracked);
            }
            var changed = Normalize(_settings);
            _save?.Invoke(changed);
        }
        _wake.Writer.TryWrite(true);
        Notify();
    }

    /// <summary>
    /// Serves this request with a whole cycle that starts after it. While another cycle runs, the request waits for it
    /// and then runs (or is satisfied by any cycle that started after it), so Update now is never silently dropped.
    /// Cycles never overlap. Cancel and drain and disabling also drop requests made before them.
    /// </summary>
    public async Task RunNowAsync(CancellationToken ct = default)
    {
        long ticket;
        lock (_sync) ticket = ++_requested;
        await _run.WaitAsync(ct).ConfigureAwait(false);
        CancellationTokenSource? active = null;
        TaskCompletionSource? cycle = null;
        long served;
        int visited = 0, tracked = 0;
        try
        {
            KlineMaintenanceSettings settings;
            lock (_sync)
            {
                if (_disposed || !_settings.Enabled || ticket <= _completedThrough || ticket <= _cancelledThrough) return;
                settings = Normalize(_settings);
                served = _requested;
                _cycles++;
                _cycle = cycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                active = _active = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                active.CancelAfter(CycleLimit);
                _running = true;
                _message = "Updating saved candle history.";
            }
            Notify();
            var now = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc);
            var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            int loads = 0;
            tracked = settings.Instruments.Count;
            for (int offset = 0; offset < settings.Instruments.Count && loads < MaxMonthlyLoadsPerCycle; offset++)
            {
                var item = settings.Instruments[(_instrumentCursor + offset) % settings.Instruments.Count];
                int cursor;
                lock (_sync) cursor = _cursors.GetValueOrDefault(item.Key);
                int months = settings.LookbackYears * 12;
                visited++;
                foreach (int minutes in Resolutions)
                {
                    if (loads >= MaxMonthlyLoadsPerCycle) break;
                    KlineMaintenanceFailure? pending;
                    lock (_sync) _failures.TryGetValue((item.Key, minutes), out pending);
                    if (pending != null && pending.MonthUtc != thisMonth)
                    {
                        // Keep the exact failed calendar slot even when a new month changes the relative plan.
                        var error = await UpdateAsync(item, pending.MonthUtc, pending.MonthUtc.AddMonths(1), minutes, active.Token).ConfigureAwait(false);
                        loads++;
                        if (error != null) continue;
                        int plannedOffset = 1 + cursor / Resolutions.Length % months;
                        if (pending.MonthUtc == thisMonth.AddMonths(-plannedOffset) && minutes == Resolutions[cursor % Resolutions.Length])
                        {
                            cursor = (cursor + 1) % (months * Resolutions.Length);
                            lock (_sync) _cursors[item.Key] = cursor;
                        }
                        if (loads >= MaxMonthlyLoadsPerCycle) break;
                    }
                    await UpdateAsync(item, thisMonth, now, minutes, active.Token).ConfigureAwait(false);
                    loads++;
                }
                for (int part = 0; part < 2 && loads < MaxMonthlyLoadsPerCycle; part++)
                {
                    int monthOffset = 1 + cursor / Resolutions.Length % months;
                    int minutes = Resolutions[cursor % Resolutions.Length];
                    // A failed slot at this resolution must be retried in its own slot before later catch-up; that includes a
                    // current month whose cache cannot be written, since more months would only go to the failing store. Only
                    // a current month the provider covers partially (its retained window) does not block older months.
                    bool unresolved;
                    lock (_sync) unresolved = _failures.TryGetValue((item.Key, minutes), out var failed) && !(failed.MonthUtc == thisMonth && failed.CoverageOnly);
                    if (unresolved) break;
                    var month = thisMonth.AddMonths(-monthOffset);
                    var error = await UpdateAsync(item, month, month.AddMonths(1), minutes, active.Token).ConfigureAwait(false);
                    loads++;
                    if (error != null) break;
                    cursor = (cursor + 1) % (months * Resolutions.Length);
                    lock (_sync) _cursors[item.Key] = cursor;
                }
            }
            lock (_sync)
            {
                _lastRun = now;
                _message = settings.Instruments.Count == 0 ? "No opened or selected instruments are tracked." : "Candle maintenance cycle completed; older history catches up in bounded monthly batches.";
                _completedThrough = Math.Max(_completedThrough, served);
            }
        }
        catch (OperationCanceledException) when (active?.IsCancellationRequested == true || ct.IsCancellationRequested)
        { lock (_sync) _message = "Candle maintenance cancelled; completed cache months remain available."; }
        finally
        {
            // Also after a cycle cut by its time limit or cancelled: the next cycle starts after every instrument this one
            // reached, so one slow instrument cannot starve the others.
            if (tracked > 0) _instrumentCursor = (_instrumentCursor + visited) % tracked;
            lock (_sync)
            {
                _running = false;
                if (ReferenceEquals(_active, active)) _active = null;
                if (ReferenceEquals(_cycle, cycle)) _cycle = null;
            }
            active?.Dispose();
            _run.Release();
            cycle?.TrySetResult();
            Notify();
        }
    }
    public async Task CancelAsync()
    {
        Task drain;
        lock (_sync)
        {
            _cancelledThrough = _requested;
            drain = CancelActiveCycle();
        }
        await drain.ConfigureAwait(false);
    }
    /// <summary>Cancels the running cycle (under <see cref="_sync"/>) and returns when it has drained.</summary>
    private Task CancelActiveCycle()
    {
        _active?.Cancel();
        return _cycle?.Task ?? Task.CompletedTask;
    }
    private async Task<string?> UpdateAsync(TrackedCandleInstrument item, DateTime from, DateTime to, int minutes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(UpdateDeadline(from, to, minutes));
        string? error = null;
        string detail;
        try
        {
            int count = await _update(item, from, to, minutes, bounded.Token).ConfigureAwait(false);
            detail = $"{minutes}m · {from:yyyy-MM} · {count:N0} candles · {item.Instrument.Exchange}";
            lock (_sync)
                if (_failures.TryGetValue((item.Key, minutes), out var pending) && pending.MonthUtc == from)
                    _failures.Remove((item.Key, minutes));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            error = ex.Message;
            detail = "Candle maintenance unavailable: " + error;
            // InvalidDataException: the provider's data was incomplete or invalid while transport and cache worked.
            lock (_sync) _failures[(item.Key, minutes)] = new(item.Key, from, minutes, error, ex is InvalidDataException);
        }
        lock (_sync)
        {
            var failures = _failures.Values.Where(f => f.InstrumentKey == item.Key)
                .OrderBy(f => f.MonthUtc).ThenBy(f => f.Minutes)
                .Select(f => $"{f.MonthUtc:yyyy-MM} {f.Minutes}m: {f.Message}").ToArray();
            _itemStatus[item.Key] = failures.Length == 0 ? detail : "Partial candle maintenance: " + string.Join(" · ", failures);
        }
        Notify();
        return error;
    }

    /// <summary>
    /// The bound of one monthly update: 45 s plus time for the slowest public candle pagination (about 100 bars per
    /// paced request, as OKX serves them), so a closed month of 1-minute bars (447 pages) can complete.
    /// </summary>
    internal static TimeSpan UpdateDeadline(DateTime from, DateTime to, int minutes) =>
        TimeSpan.FromSeconds(45) + TimeSpan.FromMilliseconds(Math.Max(0, (to - from).TotalMinutes / minutes) * 4);

    private async Task LoopAsync()
    {
        bool first = true;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                delay.CancelAfter(first ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(Settings.IntervalMinutes));
                first = false;
                try { await _wake.Reader.ReadAsync(delay.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { }
                await RunNowAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask != null) return new(_disposeTask);
            _disposed = true;
            _lifetime.Cancel();
            _active?.Cancel();
            return new(_disposeTask = DrainAsync());
        }
    }
    private async Task DrainAsync()
    {
        var loop = _loop;
        if (loop != null) await loop.ConfigureAwait(false);
        await _run.WaitAsync().ConfigureAwait(false);
        _run.Release();
        _lifetime.Dispose();
    }
    private void Notify()
    {
        lock (_sync)
            if (!_disposed) Changed?.Invoke();
    }
    private static KlineMaintenanceSettings Normalize(KlineMaintenanceSettings value) => value with
    {
        LookbackYears = Math.Clamp(value.LookbackYears, 1, 10),
        IntervalMinutes = Math.Clamp(value.IntervalMinutes, 5, 1440),
        MaxInstruments = Math.Clamp(value.MaxInstruments, 1, MaxTrackedInstruments),
        Instruments = (value.Instruments ?? new()).Where(i => i != null && !string.IsNullOrWhiteSpace(i.ProfileId) && i.Instrument != null)
            .DistinctBy(i => i.Key).Take(Math.Clamp(value.MaxInstruments, 1, MaxTrackedInstruments)).ToList(),
    };
}
