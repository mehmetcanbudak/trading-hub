using TradingHub.Market;

namespace TradingHub.Data;

public sealed record DepthMemoryUsage(long LiveBytes, long HistoricalBytes, long SelectedBytes, long QueuedBytes,
    long LiveLimit, long HistoricalLimit, long SelectedLimit, long QueueLimit, long SingleSampleLimit,
    int SelectedViews, bool LiveWasTruncated, long WorkingReadBytes = 0, long ReadLimit = 0);

public sealed record DepthRecordingStatistics(long BookRecords, long GapRecords, long BookFrameBytes, long GapFrameBytes, long HeaderBytes);
public sealed record DepthSelectionResult(long Version, string? Message, bool IsTruncated);

/// <summary>A chart-owned selection within its instrument's shared byte budget. Dispose releases its retained books.</summary>
public sealed class DepthHistorySelection : IDisposable
{
    internal readonly DepthHistory Owner;
    internal DepthSample[] Records = [];
    internal DepthSample[] QueryRecords = [];
    internal long Bytes, Revision, Access, Request, CachedDataVersion = -1, CachedRevision = -1;
    internal DateTime From, To;
    internal bool Truncated, Disposed, CachedExclusive;
    internal string? Detail;
    internal DepthHistorySelection(DepthHistory owner) => Owner = owner;
    public long Version => Owner.SelectionVersion(this);
    public string? Message => Owner.SelectionMessage(this);
    public DepthHistoryQuery Query(DateTime from, DateTime to) => Owner.QuerySelection(this, from, to);
    public Task<DepthSelectionResult> LoadAsync(DateTime from, DateTime to, CancellationToken ct = default) => Owner.LoadSelectionAsync(this, from, to, ct);
    public void Dispose() => Owner.ReleaseSelection(this);
}

public sealed partial class DepthHistory
{
    private static readonly IComparer<DepthSample> SampleComparer = Comparer<DepthSample>.Create(Compare);
    private readonly List<DepthHistorySelection> _selections = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private long _liveBytes, _loadedBytes, _selectedBytes, _queuedBytes, _selectionAccess;
    private long _writerBufferBytes, _workingReadBytes;
    private bool _currentInLive;
    private long UnretainedCurrentBytes => _current is { } sample && !_currentInLive ? sample.EstimatedMemoryBytes : 0;
    private DateTime? _liveEvictedFrom, _liveEvictedThrough;
    private (DateTime From, DateTime To)? _loadedRange;
    private long _bookRecords, _gapRecords, _bookFrameBytes, _gapFrameBytes, _headerBytes;
    private sealed record RecordingLoss(DateTime Time, DepthGapReason Reason, string? Message);
    private RecordingLoss? _pendingLoss;
    private readonly Dictionary<(Guid Recording, long Ordinal), RecordingLoss> _acceptedLosses = new();

    public DepthRecordingOptions Options { get { lock (_gate) return _options; } }
    public DepthMemoryUsage MemoryUsage
    {
        get { lock (_gate) return new(_liveBytes + UnretainedCurrentBytes, _loadedBytes, _selectedBytes, _queuedBytes + _writerBufferBytes,
            _options.MaxMemoryBytes, _options.MaxHistoryBytes, _options.MaxSelectedHistoryBytes, _options.MaxQueuedBytes,
            _options.MaxSampleBytes, _selections.Count, _liveEvictedFrom != null,
            _workingReadBytes, Math.Max(_workingReadBytes, _options.MaxHistoryBytes + 3 * _options.MaxSampleBytes)); }
    }
    public DepthRecordingStatistics RecordingStatistics
    {
        get { lock (_gate) return new(_bookRecords, _gapRecords, _bookFrameBytes, _gapFrameBytes, _headerBytes); }
    }

    public DepthHistorySelection CreateSelection()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var selection = new DepthHistorySelection(this) { Access = ++_selectionAccess };
            _selections.Add(selection);
            return selection;
        }
    }

    internal long SelectionVersion(DepthHistorySelection selection) { lock (_gate) return selection.Revision; }
    internal string? SelectionMessage(DepthHistorySelection selection) { lock (_gate) return selection.Detail; }

    internal void ReleaseSelection(DepthHistorySelection selection)
    {
        lock (_gate)
        {
            if (selection.Disposed) return;
            ClearSelection(selection, "The recorded depth selection was closed.");
            selection.Disposed = true;
            _selections.Remove(selection);
        }
    }

    private void ClearSelection(DepthHistorySelection selection, string? message)
    {
        _selectedBytes -= selection.Bytes;
        selection.Bytes = 0; selection.Records = []; selection.QueryRecords = [];
        selection.Detail = message; selection.Truncated = message != null;
        selection.Revision++; selection.Request++;
    }

    private void InvalidateMemoryQueries()
    {
        _queryRecords = []; _queryVersion = -1;
        foreach (var view in _selections) { view.QueryRecords = []; view.CachedDataVersion = -1; }
    }

    internal async Task<DepthSelectionResult> LoadSelectionAsync(DepthHistorySelection selection, DateTime from, DateTime to, CancellationToken ct)
    {
        long request;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || selection.Disposed, selection);
            request = ++selection.Request;
        }
        await _readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? root; DepthRecordingOptions options;
            lock (_gate) { root = _root; options = _options; _workingReadBytes = options.MaxHistoryBytes + 3 * options.MaxSampleBytes; }
            var load = await Task.Run(() => Load(root, from, to, options, ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_disposed || selection.Disposed || selection.Request != request)
                    return new(selection.Revision, selection.Detail, selection.Truncated);
                options = _options;
                var retained = BoundRecords(load.Samples, options.MaxHistoryBytes, options.MaxMemorySamples + 1, out long bytes, out bool clipped);
                _selectedBytes -= selection.Bytes;
                selection.Bytes = 0; selection.Records = []; selection.QueryRecords = [];
                while (_selectedBytes + bytes > options.MaxSelectedHistoryBytes)
                {
                    var oldest = _selections.Where(s => s != selection && s.Bytes > 0).MinBy(s => s.Access);
                    if (oldest == null) break;
                    ClearSelection(oldest, "The per-instrument selected-depth memory limit evicted this view; choose a narrower recorded interval to reload it.");
                }
                // This also handles a live policy reduction without retaining an oversized view.
                if (bytes > options.MaxSelectedHistoryBytes)
                    retained = BoundRecords(retained, options.MaxSelectedHistoryBytes, options.MaxMemorySamples + 1, out bytes, out clipped);
                selection.Records = retained; selection.Bytes = bytes; _selectedBytes += bytes;
                selection.From = from; selection.To = to; selection.Access = ++_selectionAccess;
                selection.Truncated = load.IsTruncated || clipped;
                selection.Detail = clipped ? "The selected depth exceeds its byte/count memory limit; only a labelled suffix is retained. " + load.Message : load.Message;
                selection.Revision++; selection.QueryRecords = [];
                _version++;
                return new(selection.Revision, selection.Detail, selection.Truncated);
            }
        }
        finally { lock (_gate) _workingReadBytes = 0; _readGate.Release(); }
    }

    internal DepthHistoryQuery QuerySelection(DepthHistorySelection selection, DateTime from, DateTime to)
    {
        lock (_gate)
        {
            if (selection.Disposed) return new(from, to, [], null, DepthCoverage.Missing, "The depth selection is closed.", true);
            selection.Access = ++_selectionAccess;
            return QueryCore(from, to, selection);
        }
    }

    private DepthHistoryQuery QueryCore(DateTime from, DateTime to, DepthHistorySelection? selection)
    {
        if (to <= from) return new(from, to, [], null, DepthCoverage.Missing, "The depth interval is empty.");
        bool completeSelection = selection != null && !selection.Truncated && selection.Detail == null &&
            selection.From <= from && selection.To >= to;
        if (_queryVersion != _dataVersion)
        {
            _queryRecords = _loaded.Concat(_live).DistinctBy(s => (s.RecordingId, s.Ordinal))
                .OrderBy(s => s.Time).ThenBy(s => s.RecordingId).ThenBy(s => s.Ordinal).ToArray();
            _queryVersion = _dataVersion;
        }
        var records = _queryRecords;
        if (selection != null)
        {
            if (selection.CachedDataVersion != _dataVersion || selection.CachedRevision != selection.Revision ||
                selection.CachedExclusive != completeSelection)
            {
                var supplement = completeSelection ? (IEnumerable<DepthSample>)_live : records;
                selection.QueryRecords = selection.Records.Concat(supplement).DistinctBy(s => (s.RecordingId, s.Ordinal))
                    .OrderBy(s => s.Time).ThenBy(s => s.RecordingId).ThenBy(s => s.Ordinal).ToArray();
                selection.CachedDataVersion = _dataVersion; selection.CachedRevision = selection.Revision;
                selection.CachedExclusive = completeSelection;
            }
            records = selection.QueryRecords;
        }
        int first = LowerBound(records, from), end = LowerBound(records, to);
        var preceding = first > 0 ? records[first - 1] : null;
        bool usesCarryIn = preceding != null && (first == end || records[first].Time > from);
        var coverage = DepthCoverage.None;
        for (int i = first; i < end; i++) coverage |= records[i].Coverage;
        bool sharedApplies = !completeSelection && _loadedRange is { } sharedRange &&
            (sharedRange.From < to && sharedRange.To > from ||
             usesCarryIn && Array.BinarySearch(_loaded, preceding!, SampleComparer) >= 0);
        bool selectedApplies = selection != null &&
            (selection.From < to && selection.To > from ||
             usesCarryIn && Array.BinarySearch(selection.Records, preceding!, SampleComparer) >= 0);
        bool backedByDisk = completeSelection || sharedApplies && !_loadTruncated && _loadMessage == null &&
            _loadedRange is { } range && range.From <= from && range.To >= to;
        bool lostLive = !backedByDisk && _liveEvictedFrom is { } lostFrom && _liveEvictedThrough is { } lostTo && to > lostFrom && from <= lostTo;
        bool truncated = sharedApplies && _loadTruncated || selectedApplies && selection!.Truncated || lostLive;
        string? message = selectedApplies && selection!.Detail != null ? selection.Detail : sharedApplies ? _loadMessage : null;
        if (lostLive) message = "The live depth byte/count retention limit removed part of this interval; selected disk history can reload it.";
        if (first == end && preceding == null) coverage |= DepthCoverage.Missing;
        if (truncated) coverage |= DepthCoverage.Truncated | DepthCoverage.Missing;
        if (message != null) coverage |= DepthCoverage.Missing;
        return new(from, to, new ArraySegment<DepthSample>(records, first, end - first), preceding, coverage, message, truncated);
    }

    private DepthSample[] BoundRecords(IReadOnlyList<DepthSample> records, long maxBytes, int maxCount, out long bytes, out bool clipped)
    {
        var kept = new SortedSet<DepthSample>(Comparer<DepthSample>.Create(Compare));
        bytes = 0; clipped = false;
        for (int i = 0; i < records.Count; i++)
        {
            var sample = records[i];
            long size = sample.EstimatedMemoryBytes;
            if (size > _options.MaxSampleBytes || size > maxBytes) { clipped = true; continue; }
            if (kept.Add(sample)) bytes += size;
            while (kept.Count > maxCount || bytes > maxBytes)
            {
                var oldest = kept.Min!;
                bytes -= oldest.EstimatedMemoryBytes; kept.Remove(oldest); clipped = true;
            }
        }
        return kept.ToArray();
    }

    private void EnforceMemoryPolicy()
    {
        if (_current is { } current && current.EstimatedMemoryBytes > _options.MaxSampleBytes)
        { _current = null; _stale = true; _coverage |= DepthCoverage.Missing | DepthCoverage.Truncated; }
        if (_live.Any(s => s.EstimatedMemoryBytes > _options.MaxSampleBytes))
        {
            _liveEvictedFrom ??= _live.Peek().Time; _liveEvictedThrough = _live.Last().Time;
            _live.Clear(); _liveBytes = 0;
            _currentInLive = false;
        }
        TrimLive(_clock);
        var records = BoundRecords(_loaded, _options.MaxHistoryBytes, _options.MaxMemorySamples + 1, out _loadedBytes, out bool clipped);
        _loaded = records; _loadTruncated |= clipped;
        foreach (var selection in _selections)
            if (selection.Bytes > Math.Min(_options.MaxHistoryBytes, _options.MaxSelectedHistoryBytes) || selection.Records.Any(s => s.EstimatedMemoryBytes > _options.MaxSampleBytes))
                ClearSelection(selection, "The selected-depth memory policy changed; this view was released.");
        while (_selectedBytes > _options.MaxSelectedHistoryBytes)
        {
            var oldest = _selections.Where(s => s.Bytes > 0).MinBy(s => s.Access);
            if (oldest == null) break;
            ClearSelection(oldest, "The selected-depth byte limit evicted this view.");
        }
        InvalidateMemoryQueries(); _dataVersion++;
    }

    private static long EncodingBufferReservation(long maxSampleBytes)
    {
        // UTF-8 can use three bytes per UTF-16 code unit; MemoryStream rounds its capacity up.
        long capacity = 256, encodedBound = maxSampleBytes + (maxSampleBytes + 1) / 2;
        while (capacity < encodedBound) capacity *= 2;
        return capacity;
    }

    private void RememberRecordingLoss(DateTime time, DepthGapReason reason, string? message)
    {
        if (_pendingLoss == null || time < _pendingLoss.Time) _pendingLoss = new(time, reason, Limit(message));
        _pendingDiskGap = _pendingLoss.Reason;
    }

    private void EnsureRecordingGapQueued()
    {
        if (_pendingLoss is not { } loss || _state != DepthRecordingState.Recording || !_started) return;
        var marker = NewGapSample(loss.Time, loss.Reason, loss.Message);
        if (!QueueSample(marker, lossMarker: true)) return;
        _acceptedLosses[(marker.RecordingId, marker.Ordinal)] = loss;
        _pendingLoss = null; _pendingDiskGap = DepthGapReason.None;
    }

    private DepthSample NewGapSample(DateTime time, DepthGapReason reason, string? message)
    {
        string provider = _provider ?? _instrument.FeedId, venue = _venue ?? _instrument.Exchange;
        var coverage = DepthCoverage.Missing | DepthCoverage.SequenceGap;
        if (reason == DepthGapReason.Disconnected) coverage |= DepthCoverage.Disconnected;
        message = Limit(message);
        if (512L + 2L * (provider.Length + (long)venue.Length + (message?.Length ?? 0)) > _options.MaxSampleBytes - 256)
        {
            provider = "unavailable"; venue = ""; message = "Depth gap metadata exceeded its memory limit.";
            coverage |= DepthCoverage.Truncated;
        }
        return new(_recordingId, ++_ordinal, new DepthUpdate
        {
            Snapshot = DepthSnapshot.Empty, ReceivedAt = time, ProviderId = provider, Venue = venue,
            Coverage = coverage, GapBefore = reason, Message = message,
        });
    }

    private (int Bids, int Asks, int Changes) FitSample(DepthUpdate update, ref string provider, ref string venue,
        ref string? message, ref DepthCoverage coverage)
    {
        message = Limit(message);
        int bids = Math.Min(update.Snapshot.Bids.Length, _options.MaxLevelsPerSide);
        int asks = Math.Min(update.Snapshot.Asks.Length, _options.MaxLevelsPerSide);
        int changes = Math.Min(update.Changes.Count, _options.MaxChangesPerUpdate);
        long metadata = 512L + 2L * (provider.Length + (long)venue.Length + (message?.Length ?? 0));
        long target = _options.MaxSampleBytes - 256; // Room for a later queue-loss explanation.
        if (metadata + 32L * (bids + asks) + 48L * changes <= target) return (bids, asks, changes);
        message = "The depth sample exceeded the byte limit; recorded levels/changes are explicitly truncated.";
        metadata = 512L + 2L * (provider.Length + (long)venue.Length + message.Length);
        coverage |= DepthCoverage.Truncated | DepthCoverage.PartialBook;
        if (metadata > target)
        {
            provider = "unavailable"; venue = "";
            message = "The provider depth identity exceeds the sample memory limit; no book was retained.";
            coverage |= DepthCoverage.Missing;
            return (0, 0, 0);
        }
        int levels = (int)Math.Max(0, (target - metadata) / 32);
        int keepBids = Math.Min(bids, levels / 2), keepAsks = Math.Min(asks, levels - keepBids);
        keepBids = Math.Min(bids, levels - keepAsks);
        int keepChanges = (int)Math.Min(changes, Math.Max(0, (target - metadata - 32L * (keepBids + keepAsks)) / 48));
        return (keepBids, keepAsks, keepChanges);
    }
}
