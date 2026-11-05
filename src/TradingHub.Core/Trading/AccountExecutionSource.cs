namespace TradingHub.Trading;

/// <summary>A source incarnation and its local revision, not a broker execution identity.</summary>
public readonly record struct ExecutionCursor(Guid Epoch, long Revision)
{
    public static ExecutionCursor Start => default;
}

/// <summary>Read-only replay. Capturing never consumes another consumer's progress.</summary>
public interface IExecutionSource
{
    Guid Epoch { get; }
    event Action? Changed;
    ExecutionSnapshot Capture(ExecutionCursor cursor);
}

/// <summary>Actual execution revisions shared by all instrument facades of an account context.</summary>
public sealed class ExecutionLog : IExecutionSource
{
    private readonly object _gate = new();
    private readonly List<ExecutionReport> _reports = new();
    private readonly ExecutionAvailability _initialAvailability;
    private Dictionary<object, ExecutionAvailability>? _producers;
    private int _synchronizing, _unavailable;
    public Guid Epoch { get; } = Guid.NewGuid();
    public event Action? Changed;

    public ExecutionLog(ExecutionAvailability initialAvailability = ExecutionAvailability.Ready) =>
        _initialAvailability = initialAvailability;

    private ExecutionAvailability AvailabilityLocked => _unavailable > 0 ? ExecutionAvailability.Unavailable
        : _synchronizing > 0 ? ExecutionAvailability.Synchronizing
        : _producers is { Count: > 0 } ? ExecutionAvailability.Ready : _initialAvailability;

    /// <summary>A lightweight producer lifetime token, never a facade/runner. Null unregisters its replay scope.</summary>
    internal bool SetProducerAvailability(object producer, ExecutionAvailability? availability, bool notify = true)
    {
        bool changed;
        lock (_gate)
        {
            var before = AvailabilityLocked;
            if (_producers != null && _producers.TryGetValue(producer, out var previous))
            {
                if (availability == previous) return false;
                AdjustAvailability(previous, -1);
                _producers.Remove(producer);
            }
            if (availability is { } current)
            {
                _producers ??= new(ReferenceEqualityComparer.Instance);
                _producers.Add(producer, current);
                AdjustAvailability(current, 1);
            }
            changed = before != AvailabilityLocked;
        }
        if (changed && notify) NotifyChanged();
        return changed;
    }

    private void AdjustAvailability(ExecutionAvailability availability, int delta)
    {
        if (availability == ExecutionAvailability.Synchronizing) _synchronizing += delta;
        else if (availability == ExecutionAvailability.Unavailable) _unavailable += delta;
    }

    public void Publish(ExecutionReport report)
    {
        lock (_gate) _reports.Add(report);
        Changed?.Invoke();
    }

    internal void PublishBatch(IReadOnlyList<ExecutionReport> reports, bool notify = true)
    {
        int count = reports.Count;
        if (count == 0) return;
        lock (_gate)
        {
            _reports.EnsureCapacity(checked(_reports.Count + count));
            for (int i = 0; i < count; i++) _reports.Add(reports[i]);
        }
        if (notify) NotifyChanged();
    }

    internal void NotifyChanged() => Changed?.Invoke();

    public ExecutionSnapshot Capture(ExecutionCursor cursor)
    {
        lock (_gate)
        {
            long revision = cursor.Epoch == Epoch ? cursor.Revision : 0;
            if (revision < 0 || revision > _reports.Count) throw new ArgumentOutOfRangeException(nameof(cursor));
            int count = _reports.Count - (int)revision;
            var availability = AvailabilityLocked;
            if (count == 0) return new(_reports.Count, Array.Empty<ExecutionReport>(), availability) { Epoch = Epoch };
            var reports = new ExecutionReport[count];
            _reports.CopyTo((int)revision, reports, 0, count);
            return new(_reports.Count, reports, availability) { Epoch = Epoch };
        }
    }
}

/// <summary>An explicit missing native producer. It makes no assertion about fills, prices or fees.</summary>
public sealed class UnavailableExecutionSource : IExecutionSource
{
    public Guid Epoch { get; } = Guid.NewGuid();
    public event Action? Changed { add { } remove { } }
    public ExecutionSnapshot Capture(ExecutionCursor cursor) => ExecutionSnapshot.Unavailable with { Epoch = Epoch };
}

/// <summary>Adapts an existing report stream without bypassing a wrapper's fact transformations.</summary>
public sealed class DelegateExecutionSource : IExecutionSource, IDisposable
{
    private readonly Func<long, ExecutionSnapshot> _capture;
    private readonly Action<Action> _unsubscribe;
    private long _notificationRevision;
    private ExecutionAvailability _availability;
    public Guid Epoch { get; } = Guid.NewGuid();
    public event Action? Changed;

    public DelegateExecutionSource(Func<long, ExecutionSnapshot> capture, Action<Action> subscribe, Action<Action> unsubscribe)
    {
        _capture = capture;
        _unsubscribe = unsubscribe;
        subscribe(OnChanged);
    }

    public ExecutionSnapshot Capture(ExecutionCursor cursor) =>
        _capture(cursor.Epoch == Epoch ? cursor.Revision : 0) with { Epoch = Epoch };

    private void OnChanged()
    {
        var snapshot = _capture(_notificationRevision);
        if (snapshot.Revision == _notificationRevision && snapshot.Availability == _availability) return;
        _notificationRevision = snapshot.Revision;
        _availability = snapshot.Availability;
        Changed?.Invoke();
    }

    public void Dispose() => _unsubscribe(OnChanged);
}
