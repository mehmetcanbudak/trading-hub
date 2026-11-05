using System.Globalization;
using System.Text;
using System.Threading.Channels;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Owned, opt-in L2 recording. Every accepted update records its received changes and a bounded
/// exact snapshot. Queue loss, provider gaps and I/O failure are explicit; no event-complete/MBO
/// claim is made. The live ring and selected historical range have independent memory limits.
/// </summary>
public sealed partial class DepthHistory : IDisposable
{
    private readonly object _gate = new(), _control = new();
    private readonly Instrument _instrument;
    private readonly Queue<DepthSample> _live = new();
    private DepthSample[] _loaded = [];
    private DepthSample[] _queryRecords = [];
    private long _dataVersion, _queryVersion = -1;
    private DepthRecordingOptions _options = new();
    private string? _root, _loadMessage;
    private bool _loadTruncated, _started, _disposed, _awaitingRefresh;
    private Guid _recordingId;
    private long _ordinal, _version, _dropped;
    private string? _provider, _venue, _message;
    private long? _sequence;
    private DateTime? _lastReceived, _lastPersisted;
    private DateTime _clock;
    private DepthCoverage _coverage = DepthCoverage.Missing;
    private DepthGapReason _pendingDiskGap;
    private DepthSample? _current;
    private bool _stale = true;
    private DepthRecordingState _state = DepthRecordingState.Disabled;
    private Channel<DepthSample>? _queue;
    private Task? _writer;

    public DepthHistory(Instrument instrument) => _instrument = instrument;
    public long Version { get { lock (_gate) return _version; } }
    public long DataVersion { get { lock (_gate) return _dataVersion; } }
    public DepthSample? Current { get { lock (_gate) return _stale ? null : _current; } }
    /// <summary>Fresh known-side display data; Current still requires a usable two-sided book.</summary>
    public bool HasFreshLevels
    {
        get { lock (_gate) return _current != null && _current.Update.HasAvailableLevels && _clock - _current.Time <= _options.MaxAge; }
    }
    public DepthHistoryStatus Status
    {
        get { lock (_gate) return new(_state, _message, _lastReceived, _lastPersisted, _dropped, _coverage, _stale); }
    }

    public void Start(string? root, DepthRecordingOptions options, DateTime now)
    {
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore(now);
            lock (_gate)
            {
                _live.Clear(); _loaded = []; _loadMessage = null; _loadTruncated = false;
                _liveBytes = _loadedBytes = 0; _loadedRange = null;
                _liveEvictedFrom = _liveEvictedThrough = null;
                foreach (var selection in _selections) ClearSelection(selection, "The depth recording context changed.");
                InvalidateMemoryQueries();
                _bookRecords = _gapRecords = _bookFrameBytes = _gapFrameBytes = _headerBytes = 0;
                _dataVersion++;
                _current = null; _lastReceived = _lastPersisted = null;
                _provider = _venue = null; _sequence = null; _awaitingRefresh = false;
                _coverage = DepthCoverage.Missing; _stale = true; _dropped = 0;
                _clock = Utc(now);
                StartCore(root, options);
            }
        }
    }

    /// <summary>Changes recording policy without restarting or replacing the provider subscription.</summary>
    public void Configure(string? root, DepthRecordingOptions options, DateTime now)
    {
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_gate) if (_root == root && _options == options.Checked() && _started && _state != DepthRecordingState.Error) return;
            StopCore(now, invalidateBook: false);
            lock (_gate) { _clock = Utc(now); StartCore(root, options); EnforceMemoryPolicy(); }
        }
    }

    private void StartCore(string? root, DepthRecordingOptions options)
    {
        _root = root; _options = options.Checked(); _recordingId = Guid.NewGuid(); _ordinal = 0;
        _pendingDiskGap = _pendingLoss?.Reason ?? DepthGapReason.None;
        _acceptedLosses.Clear(); _started = true; _message = null;
        _state = !_options.Enabled ? DepthRecordingState.Disabled : string.IsNullOrWhiteSpace(root) ? DepthRecordingState.Error : DepthRecordingState.Recording;
        if (_state == DepthRecordingState.Error) _message = "Depth recording is enabled but no recording directory is configured.";
        if (_state == DepthRecordingState.Recording)
        {
            var channel = Channel.CreateBounded<DepthSample>(new BoundedChannelOptions(_options.QueueCapacity)
            {
                SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            });
            _queue = channel;
            _writerBufferBytes = EncodingBufferReservation(_options.MaxSampleBytes);
            string directory = DepthRecordingFile.DirectoryFor(root!, _instrument);
            var policy = _options;
            _writer = Task.Run(() => WriteLoopAsync(directory, policy, channel));
        }
        _version++;
    }

    /// <summary>Called on the feed thread, before any UI coalescing. Returned metadata is authoritative.</summary>
    public DepthUpdate Receive(DepthUpdate update, DateTime receivedAt)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _clock = Utc(receivedAt);
            DateTime time = Utc(update.ReceivedAt == default ? receivedAt : update.ReceivedAt);
            string provider = string.IsNullOrWhiteSpace(update.ProviderId) ? _instrument.FeedId : update.ProviderId;
            string venue = string.IsNullOrWhiteSpace(update.Venue) ? _instrument.Exchange : update.Venue;
            var coverage = update.Coverage;
            if (coverage.HasFlag(DepthCoverage.KnownSide) && update.Snapshot.HasBid != update.Snapshot.HasAsk)
                coverage &= ~DepthCoverage.Missing;
            coverage &= ~DepthCoverage.KnownSide; // Revalidate derived one-sided availability below.
            var gap = update.GapBefore;
            string? message = update.Message;
            if (_provider != null && (_provider != provider || _venue != venue))
            {
                gap = DepthGapReason.FeedChanged;
                _sequence = null;
                _awaitingRefresh = !update.IsFullRefresh;
                message = "The depth provider or venue changed; books from different sources are not joined.";
            }
            if (_lastReceived is { } last && time < last)
            {
                coverage |= DepthCoverage.OutOfOrder | DepthCoverage.SequenceGap;
                gap = DepthGapReason.Sequence;
                message = "A depth update arrived with a regressing availability timestamp.";
                // Keep causal receive order even if a provider mislabeled its arrival time.
                time = last;
            }
            if (_sequence is { } previous && update.Sequence is { } current && !update.IsFullRefresh &&
                (current <= previous || update.PreviousSequence is { } expected && expected != previous ||
                 update.PreviousSequence == null && (update.FirstSequence ?? current) > (decimal)previous + 1))
            {
                gap = DepthGapReason.Sequence;
                coverage |= DepthCoverage.SequenceGap;
                _awaitingRefresh = true;
                message = "A depth sequence gap requires a full provider refresh.";
            }
            if (gap is DepthGapReason.Disconnected or DepthGapReason.Sequence or DepthGapReason.Overflow or DepthGapReason.Missing)
                _awaitingRefresh = true;
            if (update.IsFullRefresh) _awaitingRefresh = false;
            if (_awaitingRefresh) coverage |= DepthCoverage.SequenceGap | DepthCoverage.Missing;
            if (gap != DepthGapReason.None) coverage |= DepthCoverage.SequenceGap;
            if (update.Changes.Count > 0) coverage |= DepthCoverage.ReceivedChanges;
            else if ((coverage & DepthCoverage.ReceivedChanges) == 0) coverage |= DepthCoverage.SnapshotOnly;

            var limits = FitSample(update, ref provider, ref venue, ref message, ref coverage);
            var bids = CopyLevels(update.Snapshot.Bids, limits.Bids, true, ref coverage);
            var asks = CopyLevels(update.Snapshot.Asks, limits.Asks, false, ref coverage);
            if (bids.Length == 0 || asks.Length == 0)
            {
                if ((bids.Length > 0 || asks.Length > 0) && !coverage.HasFlag(DepthCoverage.Missing))
                    coverage |= DepthCoverage.KnownSide;
                coverage |= DepthCoverage.PartialBook | DepthCoverage.Missing;
            }
            if (bids.Length > 0 && asks.Length > 0 && bids[0].PriceTicks >= asks[0].PriceTicks)
            {
                coverage |= DepthCoverage.PartialBook | DepthCoverage.Missing;
                message = "The provider book is crossed; it is not treated as fresh resting liquidity.";
            }
            int changeCount = limits.Changes;
            var changes = changeCount == 0 ? Array.Empty<BookChange>() : new BookChange[changeCount];
            for (int i = 0; i < changeCount; i++)
            {
                changes[i] = update.Changes[i];
                if (changes[i].Size < 0 || changes[i].Side is not BookSide.Bid and not BookSide.Ask)
                {
                    coverage = (coverage | DepthCoverage.Missing) & ~DepthCoverage.KnownSide;
                    message = "The provider returned an invalid depth change.";
                }
            }
            if (changeCount != update.Changes.Count) coverage |= DepthCoverage.Truncated | DepthCoverage.PartialBook;
            var normalized = new DepthUpdate
            {
                Snapshot = new DepthSnapshot(bids, asks, Utc(update.Snapshot.Time)), Changes = changes,
                IsFullRefresh = update.IsFullRefresh, ProviderId = provider, Venue = venue, ReceivedAt = time,
                Sequence = update.Sequence, FirstSequence = update.FirstSequence, PreviousSequence = update.PreviousSequence,
                Coverage = coverage, GapBefore = gap, Message = Limit(message),
            };
            if (_options.Enabled && _started) EnsureRecordingGapQueued();
            var sample = new DepthSample(_recordingId, ++_ordinal, normalized);
            _provider = provider; _venue = venue; _sequence = update.Sequence;
            _currentInLive = false;
            _current = sample; _lastReceived = time; _coverage = coverage; _stale = !sample.IsUsable;
            if (_state != DepthRecordingState.Error) _message = Limit(message);
            if (_options.Enabled && _started) Append(sample);
            TrimLive(time);
            _version++;
            return normalized;
        }
    }

    public void MarkGap(DateTime time, DepthGapReason reason, string? message = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            MarkGapCore(Utc(time), reason, message);
        }
    }

    private DepthSample MarkGapCore(DateTime time, DepthGapReason reason, string? message)
    {
        _clock = Utc(time);
        if (_lastReceived is { } last && time < last) time = last;
        var coverage = DepthCoverage.Missing | DepthCoverage.SequenceGap;
        if (reason == DepthGapReason.Disconnected) coverage |= DepthCoverage.Disconnected;
        if (_options.Enabled && _started) EnsureRecordingGapQueued();
        var sample = NewGapSample(time, reason, message);
        _current = null; _stale = true; _coverage = coverage; _awaitingRefresh = true; _sequence = null;
        if (_state != DepthRecordingState.Error) _message = Limit(message);
        if (_options.Enabled && _started) Append(sample);
        TrimLive(time);
        _version++;
        return sample;
    }

    private void Append(DepthSample sample)
    {
        _live.Enqueue(sample);
        _liveBytes += sample.EstimatedMemoryBytes;
        if (ReferenceEquals(sample, _current)) _currentInLive = true;
        _dataVersion++;
        if (_pendingLoss != null && _state == DepthRecordingState.Recording) { _dropped++; return; }
        QueueSample(sample);
    }

    private bool QueueSample(DepthSample sample, bool lossMarker = false)
    {
        if (_queue == null || _state == DepthRecordingState.Error)
        {
            if (!lossMarker && _state == DepthRecordingState.Error) _dropped++;
            return false;
        }
        long bytes = sample.EstimatedMemoryBytes;
        // Include a conservatively rounded reusable encoding buffer and reserve one closing gap.
        if (bytes <= _options.MaxSampleBytes && _queuedBytes + bytes <= _options.MaxQueuedBytes - _writerBufferBytes - _options.MaxSampleBytes)
        {
            _queuedBytes += bytes;
            if (_queue.Writer.TryWrite(sample)) return true;
            _queuedBytes -= bytes;
        }
        if (!lossMarker)
        {
            _dropped++;
            _message = "Depth recording queue reached its byte/count limit. Dropped updates and the next recorded gap remain visible.";
            RememberRecordingLoss(sample.Time, DepthGapReason.Overflow, _message);
            _coverage |= DepthCoverage.SequenceGap;
        }
        return false;
    }

    public void Refresh(DateTime now)
    {
        lock (_gate)
        {
            _clock = Utc(now);
            bool stale = _current == null || !_current.IsUsable || Utc(now) - _current.Time > _options.MaxAge;
            if (stale != _stale) { _stale = stale; _version++; }
            TrimLive(Utc(now));
        }
    }

    private void TrimLive(DateTime now)
    {
        DateTime before = now.Ticks < _options.MemoryRetention.Ticks ? DateTime.MinValue : now - _options.MemoryRetention;
        bool removed = false;
        while (_live.Count > 0 && (_live.Count > _options.MaxMemorySamples || _liveBytes + UnretainedCurrentBytes > _options.MaxMemoryBytes || _live.Peek().Time < before))
        {
            var sample = _live.Dequeue();
            _liveBytes -= sample.EstimatedMemoryBytes;
            if (ReferenceEquals(sample, _current)) _currentInLive = false;
            _liveEvictedFrom ??= sample.Time;
            _liveEvictedThrough = sample.Time;
            _dataVersion++; _version++; removed = true;
        }
        if (removed) InvalidateMemoryQueries();
    }

    public DepthHistoryQuery Query(DateTime fromUtc, DateTime toUtc)
    {
        lock (_gate) return QueryCore(fromUtc, toUtc, null);
    }

    public async Task<DepthHistoryLoad> LoadAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await _readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? root; DepthRecordingOptions options;
            lock (_gate) { root = _root; options = _options; _workingReadBytes = options.MaxHistoryBytes + 3 * options.MaxSampleBytes; }
            return await Task.Run(() => Load(root, fromUtc, toUtc, options, ct), ct).ConfigureAwait(false);
        }
        finally { lock (_gate) _workingReadBytes = 0; _readGate.Release(); }
    }

    private DepthHistoryLoad Load(string? root, DateTime from, DateTime to, DepthRecordingOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root) || to <= from) return new(from, to, [], null);
        string directory = DepthRecordingFile.DirectoryFor(root, _instrument);
        var samples = new SortedSet<DepthSample>(Comparer<DepthSample>.Create(Compare));
        var messages = new HashSet<string>();
        bool truncated = false;
        DepthSample? preceding = null;
        long retainedBytes = 0, precedingBytes = 0;
        var lastOrdinal = new Dictionary<Guid, long>();
        try
        {
            // Historical consumers choose freshness independently of the recorder's live timeout.
            // Both the heatmap and depth study support up to five minutes of carry-in.
            const long carryInTicks = 5 * TimeSpan.TicksPerMinute;
            var lower = from.Ticks < carryInTicks ? DateTime.MinValue : from.AddTicks(-carryInTicks);
            if (!Directory.Exists(directory)) return new(from, to, [], null);
            var firstDay = DateOnly.FromDateTime(lower);
            var lastDay = DateOnly.FromDateTime(to);
            var selectedDays = new List<(DateOnly Day, string Directory)>();
            foreach (var dayDirectory in Directory.EnumerateDirectories(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (DateOnly.TryParseExact(Path.GetFileName(dayDirectory), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
                    day >= firstDay && day <= lastDay) selectedDays.Add((day, dayDirectory));
            }
            foreach (var (_, dayDirectory) in selectedDays.OrderBy(item => item.Day))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFiles(dayDirectory, "*.depth").Where(IsOwnedSegment)
                    .OrderBy(path => Path.GetFileName(path)[..32], StringComparer.Ordinal).ThenBy(SegmentNumber))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var original in DepthRecordingFile.Read(path, _instrument, options.MaxSampleBytes, ct))
                        {
                            var sample = original;
                            if (lastOrdinal.TryGetValue(sample.RecordingId, out long previous) && sample.Ordinal != previous + 1)
                                sample = sample with { Update = CopyWithGap(sample.Update, DepthGapReason.Overflow, "The persisted receive sequence has missing records.") };
                            lastOrdinal[sample.RecordingId] = sample.Ordinal;
                            if (sample.Time < from)
                            {
                                if (sample.Time >= lower && (preceding == null || sample.Time >= preceding.Time))
                                {
                                    retainedBytes -= precedingBytes;
                                    preceding = sample; precedingBytes = sample.EstimatedMemoryBytes; retainedBytes += precedingBytes;
                                }
                            }
                            else if (sample.Time < to && samples.Add(sample)) retainedBytes += sample.EstimatedMemoryBytes;
                            while (samples.Count > options.MaxMemorySamples || retainedBytes > options.MaxHistoryBytes)
                            {
                                if (samples.Count > 0)
                                {
                                    var removed = samples.Min!;
                                    retainedBytes -= removed.EstimatedMemoryBytes; samples.Remove(removed); truncated = true;
                                }
                                else { preceding = null; retainedBytes -= precedingBytes; precedingBytes = 0; truncated = true; break; }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException or FormatException)
                    {
                        messages.Add(ex.Message);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { messages.Add(ex.Message); }
        var result = new List<DepthSample>(samples.Count + 1);
        if (preceding != null) result.Add(preceding);
        result.AddRange(samples);
        result.Sort(Compare);
        bool partialRecords = result.Any(sample => sample.Coverage.HasFlag(DepthCoverage.Truncated));
        if (truncated) messages.Add("The selected depth range exceeds the memory limit; the retained suffix is explicitly incomplete.");
        else if (partialRecords) messages.Add("The selected depth includes explicitly truncated records.");
        return new(from, to, result, messages.Count == 0 ? null : string.Join(" ", messages), truncated || partialRecords);
    }

    public void Apply(DepthHistoryLoad load)
    {
        lock (_gate)
        {
            _loaded = BoundRecords(load.Samples, _options.MaxHistoryBytes, _options.MaxMemorySamples + 1, out _loadedBytes, out bool clipped);
            _loadedRange = (load.From, load.To);
            _loadMessage = load.Message; _loadTruncated = load.IsTruncated || clipped;
            InvalidateMemoryQueries();
            _dataVersion++;
            _version++;
        }
    }

    public void Stop(DateTime now)
    {
        lock (_control) StopCore(now);
    }

    private void StopCore(DateTime now, bool invalidateBook = true)
    {
        Task? writer;
        Channel<DepthSample>? queue;
        DepthSample? trailingGap = null;
        RecordingLoss? trailingLoss = null;
        lock (_gate)
        {
            if (!_started) return;
            if (_current != null || _pendingDiskGap != DepthGapReason.None)
            {
                DepthSample gap;
                if (invalidateBook)
                    gap = MarkGapCore(Utc(now), DepthGapReason.Disconnected, "Depth recording stopped; no book is carried beyond shutdown.");
                else
                {
                    var time = Utc(now);
                    if (_lastReceived is { } last && time < last) time = last;
                    if (_options.Enabled) EnsureRecordingGapQueued();
                    gap = NewGapSample(time, DepthGapReason.Missing, "The recording policy changed; live provider continuity is unchanged.");
                    if (_options.Enabled) Append(gap);
                }
                if (_pendingLoss is { } loss && _state == DepthRecordingState.Recording)
                {
                    trailingLoss = loss;
                    trailingGap = NewGapSample(loss.Time, loss.Reason, loss.Message);
                    _acceptedLosses[(trailingGap.RecordingId, trailingGap.Ordinal)] = loss;
                    _queuedBytes += trailingGap.EstimatedMemoryBytes;
                }
            }
            _started = false;
            queue = _queue; _queue = null;
            writer = _writer; _writer = null;
        }
        try
        {
            // Feed callbacks never wait for disk. Shutdown alone waits for room for the final
            // loss marker, so a full queue cannot conceal a missing tail after restart.
            if (trailingGap != null && queue != null)
            {
                queue.Writer.WriteAsync(trailingGap).AsTask().GetAwaiter().GetResult();
                lock (_gate)
                    if (ReferenceEquals(_pendingLoss, trailingLoss)) { _pendingLoss = null; _pendingDiskGap = DepthGapReason.None; }
            }
        }
        catch (ChannelClosedException)
        {
            if (trailingGap != null) lock (_gate)
            {
                _queuedBytes -= trailingGap.EstimatedMemoryBytes;
                _acceptedLosses.Remove((trailingGap.RecordingId, trailingGap.Ordinal));
            }
        }
        finally { queue?.Writer.TryComplete(); }
        writer?.GetAwaiter().GetResult();
        lock (_gate)
        {
            if (_state == DepthRecordingState.Recording) _state = DepthRecordingState.Stopped;
            _version++;
        }
    }

    private async Task WriteLoopAsync(string directory, DepthRecordingOptions options, Channel<DepthSample> channel)
    {
        var reader = channel.Reader;
        FileStream? stream = null, lease = null;
        BinaryWriter? writer = null;
        string? currentPath = null;
        DateOnly? currentDay = null;
        long diskBytes = 0;
        long part = 0;
        DepthSample? writing = null;
        try
        {
            Directory.CreateDirectory(directory);
            lease = new FileStream(Path.Combine(directory, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var buffer = new MemoryStream();
            using var payload = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
            long headerBytes = DepthRecordingFile.HeaderSize(_instrument);
            if (headerBytes > options.MaxSampleBytes)
                throw new IOException("The exact depth identity header exceeds the configured single-sample byte limit.");
            await foreach (var sample in reader.ReadAllAsync().ConfigureAwait(false))
            {
                writing = sample;
                try
                {
                buffer.SetLength(0); buffer.Position = 0;
                DepthRecordingFile.WritePayload(payload, sample); payload.Flush();
                long frameBytes = buffer.Length + 8;
                if (frameBytes + headerBytes > options.MaxDiskBytes || frameBytes + headerBytes > options.MaxSegmentBytes)
                    throw new IOException("A depth update exceeds the configured disk/segment limit. Increase the limit or reduce recorded levels; the update was not silently compressed.");
                var day = DateOnly.FromDateTime(sample.Time);
                if (writer == null || currentDay != day || stream!.Length + frameBytes > options.MaxSegmentBytes || diskBytes + frameBytes > options.MaxDiskBytes)
                {
                    writer?.Dispose(); stream?.Dispose(); writer = null; stream = null;
                    string dayDirectory = Path.Combine(directory, day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(dayDirectory);
                    currentPath = Path.Combine(dayDirectory, sample.RecordingId.ToString("N") + "-" + (++part).ToString("D6", CultureInfo.InvariantCulture) + ".depth");
                    // Scan/prune only at segment rotation, not once per received level update.
                    diskBytes = Prune(directory, sample.Time, options, frameBytes + headerBytes);
                    stream = new FileStream(currentPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                    DepthRecordingFile.WriteHeader(writer, _instrument);
                    writer.Flush();
                    stream.Flush();
                    diskBytes += stream.Length;
                    lock (_gate) _headerBytes += stream.Length;
                    currentDay = day;
                }
                if (stream!.Length + frameBytes > options.MaxSegmentBytes)
                    throw new IOException("The exact depth frame plus its identity header exceeds the configured segment limit.");
                DepthRecordingFile.WriteFrame(writer, buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
                writer.Flush(); stream!.Flush(flushToDisk: true);
                diskBytes += frameBytes;
                lock (_gate)
                {
                    _lastPersisted = sample.Time; _version++;
                    if (sample.Snapshot.Bids.Length == 0 && sample.Snapshot.Asks.Length == 0)
                    { _gapRecords++; _gapFrameBytes += frameBytes; }
                    else { _bookRecords++; _bookFrameBytes += frameBytes; }
                    _acceptedLosses.Remove((sample.RecordingId, sample.Ordinal));
                }
                }
                finally { lock (_gate) _queuedBytes -= sample.EstimatedMemoryBytes; }
                writing = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            channel.Writer.TryComplete();
            lock (_gate)
            {
                _state = DepthRecordingState.Error;
                _message = "Depth recording failed: " + ex.Message;
                _coverage |= DepthCoverage.SequenceGap;
                _pendingDiskGap = DepthGapReason.StorageFailure;
                DateTime failedAt = writing?.Time ?? _lastReceived ?? _clock;
                if (_pendingLoss != null && _pendingLoss.Time < failedAt) failedAt = _pendingLoss.Time;
                foreach (var loss in _acceptedLosses.Values) if (loss.Time < failedAt) failedAt = loss.Time;
                _pendingLoss = new(failedAt, DepthGapReason.StorageFailure, Limit(_message));
                _acceptedLosses.Clear();
                _dropped++;
                _version++;
            }
            while (reader.TryRead(out var discarded))
            { lock (_gate) { _dropped++; _queuedBytes -= discarded.EstimatedMemoryBytes; } }
        }
        finally
        {
            try { writer?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_gate) { _state = DepthRecordingState.Error; _message = "Depth recording close failed: " + ex.Message; _version++; }
            }
            finally
            {
                try { stream?.Dispose(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lock (_gate) { _state = DepthRecordingState.Error; _message = "Depth recording close failed: " + ex.Message; _version++; }
                }
                finally
                {
                    try { lease?.Dispose(); }
                    finally { lock (_gate) _writerBufferBytes = 0; }
                }
            }
        }
    }

    private static long Prune(string directory, DateTime now, DepthRecordingOptions options, long additionalBytes)
    {
        if (!Directory.Exists(directory)) return 0;
        DateOnly oldest = DateOnly.FromDateTime(now).AddDays(1 - options.RetentionDays);
        var files = new List<FileInfo>();
        foreach (var dayPath in Directory.EnumerateDirectories(directory))
        {
            if (!DateOnly.TryParseExact(Path.GetFileName(dayPath), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            foreach (var path in Directory.EnumerateFiles(dayPath, "*.depth"))
            {
                if (!IsOwnedSegment(path)) continue;
                if (day < oldest) File.Delete(path);
                else files.Add(new FileInfo(path));
            }
        }
        long total = files.Sum(f => f.Length);
        foreach (var file in files.OrderBy(f => f.Directory!.Name, StringComparer.Ordinal).ThenBy(f => f.CreationTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            if (total + additionalBytes <= options.MaxDiskBytes) break;
            total -= file.Length;
            file.Delete();
        }
        if (total + additionalBytes > options.MaxDiskBytes) throw new IOException("The configured depth disk limit cannot hold the exact recording frame.");
        return total;
    }

    private static bool IsOwnedSegment(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Length >= 39 && name[32] == '-' && Guid.TryParseExact(name[..32], "N", out _) &&
            long.TryParse(name.AsSpan(33), NumberStyles.None, CultureInfo.InvariantCulture, out long part) && part > 0;
    }

    private static long SegmentNumber(string path) =>
        long.Parse(Path.GetFileNameWithoutExtension(path).AsSpan(33), NumberStyles.None, CultureInfo.InvariantCulture);

    private BookLevel[] CopyLevels(BookLevel[] source, int count, bool bid, ref DepthCoverage coverage)
    {
        var result = count == 0 ? Array.Empty<BookLevel>() : new BookLevel[count];
        Array.Copy(source, result, count);
        if (count != source.Length) coverage |= DepthCoverage.PartialBook | DepthCoverage.Truncated;
        for (int i = 0; i < count; i++)
            if (result[i].Size <= 0 || i > 0 && (bid ? result[i - 1].PriceTicks <= result[i].PriceTicks : result[i - 1].PriceTicks >= result[i].PriceTicks))
                coverage |= DepthCoverage.Missing | DepthCoverage.PartialBook;
        return result;
    }

    private static DepthUpdate CopyWithGap(DepthUpdate u, DepthGapReason gap, string message) => new()
    {
        Snapshot = u.Snapshot, Changes = u.Changes, IsFullRefresh = u.IsFullRefresh,
        ProviderId = u.ProviderId, Venue = u.Venue, ReceivedAt = u.ReceivedAt,
        Sequence = u.Sequence, FirstSequence = u.FirstSequence, PreviousSequence = u.PreviousSequence,
        Coverage = u.Coverage | DepthCoverage.SequenceGap, GapBefore = gap, Message = message,
    };

    private static int LowerBound(DepthSample[] samples, DateTime time)
    {
        int lo = 0, hi = samples.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (samples[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int Compare(DepthSample left, DepthSample right)
    {
        int order = left.Time.CompareTo(right.Time);
        if (order != 0) return order;
        order = left.RecordingId.CompareTo(right.RecordingId);
        return order != 0 ? order : left.Ordinal.CompareTo(right.Ordinal);
    }
    private static DateTime Utc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Utc => time,
        DateTimeKind.Local => time.ToUniversalTime(),
        _ => DateTime.SpecifyKind(time, DateTimeKind.Utc),
    };
    private static string? Limit(string? message) => message is { Length: > 2048 } ? message[..2048] : message;

    public void Dispose()
    {
        lock (_control)
        {
            if (_disposed) return;
            DateTime now;
            lock (_gate) now = _clock == default ? _lastReceived ?? DateTime.UtcNow : _clock;
            StopCore(now);
            lock (_gate)
            {
                _disposed = true; _loaded = []; _live.Clear(); _liveBytes = _loadedBytes = 0;
                foreach (var selection in _selections) ClearSelection(selection, "The depth recording was disposed.");
                _selections.Clear(); InvalidateMemoryQueries();
            }
        }
    }
}
