using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    private readonly object _historyArchiveGate = new();
    private readonly CancellationTokenSource _historyArchiveStop = new();
    private readonly HashSet<Task> _historyArchiveTasks = new();

    /// <summary>Explicit, bounded archival of selected closed UTC days. Raw files remain authoritative and are never deleted.</summary>
    public Task<int> ArchiveRecordedDaysAsync(Instrument instrument, IReadOnlyList<DateOnly> days, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(days), "Select between one and 31 recorded UTC days per archive operation.");
        var selected = days.Distinct().Order().ToArray();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (selected.Any(day => day >= today)) throw new ArgumentException("Only closed UTC days can be archived; the current recording day is excluded.", nameof(days));
        if (instrument.FeedId == IbBarImport.FeedId) throw new NotSupportedException("IB history uses its own atomic publication catalog and cannot be archived independently.");
        string root = AppPaths.Ticks;
        lock (_historyArchiveGate)
        {
            if (_historyArchiveStop.IsCancellationRequested) throw new InvalidOperationException("History archival is closing down.");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _historyArchiveStop.Token);
            var task = Task.Run(() => TickArchive.ArchiveDays(root, instrument, selected, progress, linked.Token), linked.Token);
            _historyArchiveTasks.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_historyArchiveGate) _historyArchiveTasks.Remove(completed);
                linked.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    internal async Task ShutdownHistoryArchivesAsync()
    {
        Task[] pending;
        lock (_historyArchiveGate)
        {
            _historyArchiveStop.Cancel();
            pending = _historyArchiveTasks.ToArray();
        }
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) when (pending.All(task => task.IsCompleted)) { }
    }
}
