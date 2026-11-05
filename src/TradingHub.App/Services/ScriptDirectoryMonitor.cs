using System.Text;
using TradingHub.Scripting;

namespace TradingHub.App.Services;

// Reconciled from reviewed startup fix 85c43954ebd4cf6acccda850cc86f356c2bdf9f5.
internal readonly struct CapturedScriptSource(string path, byte[] bytes)
{
    // The monitor owns these bytes and never modifies them after publication.
    internal ScriptSource Decode()
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new(path, reader.ReadToEnd());
    }
}

internal readonly record struct ScriptDirectorySnapshot(long Version, IReadOnlyList<CapturedScriptSource> Sources)
{
    internal ScriptSource[] DecodeSources()
    {
        var sources = new ScriptSource[Sources.Count];
        for (int i = 0; i < sources.Length; i++) sources[i] = Sources[i].Decode();
        return sources;
    }
}

// Capture reconciles explicit compilations; Version rejects stale queued notifications and compiler results.
internal interface IScriptDirectoryMonitor : IDisposable
{
    long Version { get; }
    ScriptDirectorySnapshot Capture();
    Task StopAsync();
}

internal static class ScriptDirectoryFiles
{
    internal static IEnumerable<string> Enumerate(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Where(IsIncluded);

    internal static bool IsIncluded(string file)
    {
        string path = file.Replace(Path.DirectorySeparatorChar, '/');
        return !path.Contains("/bin/") && !path.Contains("/obj/");
    }

    internal static CapturedScriptSource[] Capture(string folder)
    {
        var files = Enumerate(folder).OrderBy(file => file).ToArray();
        var sources = new CapturedScriptSource[files.Length];
        for (int i = 0; i < files.Length; i++) sources[i] = new(files[i], File.ReadAllBytes(files[i]));
        return sources;
    }
}

internal sealed class NativeScriptDirectoryMonitor : IScriptDirectoryMonitor
{
    private readonly string _folder;
    private readonly FileSystemWatcher _watcher;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;
    private int _stopping;

    internal NativeScriptDirectoryMonitor(string folder, Action changed)
    {
        _folder = folder;
        _watcher = new FileSystemWatcher(folder, "*.cs") { IncludeSubdirectories = true };
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Deleted += Changed;
        _watcher.Renamed += Changed;
        try { _watcher.EnableRaisingEvents = true; }
        catch { _watcher.Dispose(); throw; }

        void Changed(object sender, FileSystemEventArgs args)
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            if (!ScriptDirectoryFiles.IsIncluded(args.FullPath) &&
                (args is not RenamedEventArgs renamed || !ScriptDirectoryFiles.IsIncluded(renamed.OldFullPath))) return;
            Interlocked.Increment(ref _version);
            changed();
        }
    }

    public long Version => Volatile.Read(ref _version);
    public ScriptDirectorySnapshot Capture()
    {
        long version = Version;
        return new(version, ScriptDirectoryFiles.Capture(_folder));
    }
    public Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0) _ = CompleteStopAsync();
        return _drained.Task;
    }
    private async Task CompleteStopAsync()
    {
        try { await Task.Run(_watcher.Dispose).ConfigureAwait(false); _drained.TrySetResult(); }
        catch (Exception error) { _drained.TrySetException(error); }
    }
    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}

internal sealed class ContentScriptDirectoryMonitor : IScriptDirectoryMonitor
{
    private readonly string _folder;
    private readonly Action _changed;
    private readonly Action<Exception> _unavailable;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private readonly Task _worker;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, byte[]>> _updates = new();
    private readonly List<string> _removed = new();
    private byte[] _buffer = new byte[16 * 1024];
    private string[] _files = [];
    private CapturedScriptSource[] _sources = [];
    private long _version;
    private bool _initialized;
    private int _stopping;

    internal ContentScriptDirectoryMonitor(string folder, Action changed, Action<Exception> unavailable)
    {
        _folder = folder;
        _changed = changed;
        _unavailable = unavailable;
        _token = _stop.Token;
        _worker = Task.Run(MonitorAsync);
    }

    public long Version => Volatile.Read(ref _version);

    public ScriptDirectorySnapshot Capture()
    {
        lock (_gate)
        {
            _token.ThrowIfCancellationRequested();
            _seen.Clear();
            _updates.Clear();
            _removed.Clear();
            bool membershipChanged = false;
            // Commit only a complete readable scan. A missing/unreadable root is not an empty source set.
            foreach (string file in ScriptDirectoryFiles.Enumerate(_folder))
            {
                _token.ThrowIfCancellationRequested();
                _seen.Add(file);
                int length = Read(file);
                bool exists = _contents.TryGetValue(file, out var previous);
                if (!exists) membershipChanged = true;
                if (!exists || !previous.AsSpan().SequenceEqual(_buffer.AsSpan(0, length)))
                    _updates.Add(new(file, _buffer.AsSpan(0, length).ToArray()));
            }
            foreach (string file in _contents.Keys)
                if (!_seen.Contains(file)) _removed.Add(file);
            membershipChanged |= _removed.Count != 0;
            if (!_initialized || _updates.Count != 0 || _removed.Count != 0)
            {
                foreach (var update in _updates) _contents[update.Key] = update.Value;
                foreach (string file in _removed) _contents.Remove(file);
                if (membershipChanged) _files = _contents.Keys.OrderBy(file => file).ToArray();
                var sources = new CapturedScriptSource[_files.Length];
                for (int i = 0; i < sources.Length; i++) sources[i] = new(_files[i], _contents[_files[i]]);
                _sources = sources;
                _initialized = true;
                _version++;
            }
            return new(_version, _sources);
        }
    }

    private int Read(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        int length = 0;
        while (true)
        {
            _token.ThrowIfCancellationRequested();
            if (length == _buffer.Length)
            {
                int next = stream.ReadByte();
                if (next < 0) break;
                Array.Resize(ref _buffer, checked(_buffer.Length * 2));
                _buffer[length++] = (byte)next;
            }
            int count = stream.Read(_buffer.AsSpan(length));
            if (count == 0) break;
            length += count;
        }
        return length;
    }

    private async Task MonitorAsync()
    {
        // .NET's macOS watcher activation calls global POSIX sync. Do not activate it, even off-thread.
        // One awaited loop and Capture's gate prevent overlapping polls or initial/manual-scan races.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        bool unavailable = false;
        try
        {
            while (await timer.WaitForNextTickAsync(_token).ConfigureAwait(false))
            {
                try
                {
                    long before = Version;
                    var snapshot = Capture();
                    bool recovered = unavailable;
                    unavailable = false;
                    if (snapshot.Version != before || recovered) _changed();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (!unavailable) _unavailable(error);
                    unavailable = true;
                }
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
    }

    public Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return _drained.Task;
        Exception? failure = null;
        try { _stop.Cancel(); }
        catch (Exception error) { failure = error; }
        _ = CompleteStopAsync(failure);
        return _drained.Task;
    }
    private async Task CompleteStopAsync(Exception? failure)
    {
        // Neither cancellation nor the drain holds the scan gate, and this worker never awaits UI work.
        try { await _worker.ConfigureAwait(false); }
        catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
        try { _stop.Dispose(); }
        catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
        if (failure == null) _drained.TrySetResult();
        else _drained.TrySetException(failure);
    }
    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
