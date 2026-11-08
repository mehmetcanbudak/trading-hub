using System.Globalization;

namespace TradingHub.Data;

internal sealed class HistoryWriteOwnership : IDisposable
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string _directory;
    private readonly FileStream _scope;
    private readonly FileStream? _instrument;
    private readonly bool _wholeSymbol;
    private readonly Dictionary<string, FileStream> _days = new(Paths);
    private bool _disposed;

    private HistoryWriteOwnership(string directory, bool wholeSymbol, FileStream? instrument = null)
    {
        _directory = directory;
        _wholeSymbol = wholeSymbol;
        _instrument = instrument;
        Directory.CreateDirectory(directory);
        // Shared symbol scope admits independent days. An import excludes every day without nesting leases.
        _scope = Open(Path.Combine(directory, ".history-write.lock"), wholeSymbol ? FileShare.None : FileShare.ReadWrite,
            wholeSymbol ? FileAccess.ReadWrite : FileAccess.Read);
    }

    internal static HistoryWriteOwnership AcquireSymbol(string root, string feedId, string symbol)
    {
        var instrument = TickFile.TryLock(root, feedId, symbol, failOnNonSharingError: true)
            ?? throw new IOException("Another writer owns this imported instrument.");
        try { return new HistoryWriteOwnership(DirectoryFor(root, feedId, symbol), wholeSymbol: true, instrument); }
        catch { instrument.Dispose(); throw; }
    }

    internal static HistoryWriteOwnership AcquireDays(string root, string feedId, string symbol, IEnumerable<DateOnly> days)
    {
        var ownership = new HistoryWriteOwnership(DirectoryFor(root, feedId, symbol), wholeSymbol: false);
        try { ownership.ReserveDays(days); return ownership; }
        catch { ownership.Dispose(); throw; }
    }

    internal static HistoryWriteOwnership AcquirePath(string path)
    {
        string raw = RawPath(path);
        var ownership = new HistoryWriteOwnership(Path.GetDirectoryName(raw)!, wholeSymbol: false);
        try { ownership.ReservePaths([raw]); return ownership; }
        catch { ownership.Dispose(); throw; }
    }

    internal void ReserveDays(IEnumerable<DateOnly> days) => ReservePaths(days.Select(day =>
        Path.Combine(_directory, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".vtk")));

    private void ReservePaths(IEnumerable<string> paths)
    {
        var requested = paths.Select(RawPath).Distinct(Paths).Order(Paths).ToArray();
        lock (_days)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (string path in requested)
                if (!Paths.Equals(Path.GetDirectoryName(path), _directory))
                    throw new InvalidOperationException("History ownership belongs to another physical instrument.");
            if (_wholeSymbol) return;
            var acquired = new List<KeyValuePair<string, FileStream>>();
            try
            {
                foreach (string path in requested)
                    if (!_days.ContainsKey(path))
                        acquired.Add(new(path, Open(path + ".write.lock", FileShare.None, FileAccess.ReadWrite)));
            }
            catch
            {
                foreach (var day in acquired) day.Value.Dispose();
                throw;
            }
            foreach (var day in acquired) _days.Add(day.Key, day.Value);
        }
    }

    internal void RequirePath(string path)
    {
        string full = Path.GetFullPath(path);
        lock (_days)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Paths.Equals(Path.GetDirectoryName(full), _directory))
            {
                if (_wholeSymbol || _days.ContainsKey(RawPath(full))) return;
                // Staging files are private siblings of the day whose eventual publication is already owned.
                foreach (string day in _days.Keys)
                    if (full.StartsWith(day + ".", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            }
            throw new InvalidOperationException("The physical history day is not owned by this operation.");
        }
    }

    internal void ReleaseDay(DateOnly day)
    {
        string path = Path.Combine(_directory, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".vtk");
        lock (_days)
            if (_days.Remove(path, out var lease)) lease.Dispose();
    }

    private static string DirectoryFor(string root, string feedId, string symbol) =>
        Path.GetDirectoryName(Path.GetFullPath(TickFile.DayPath(root, feedId, symbol, DateOnly.MinValue)))!;

    private static string RawPath(string path)
    {
        string full = Path.GetFullPath(path);
        return full.EndsWith(".vtk.br", StringComparison.Ordinal) ? full[..^3] : full;
    }

    private static FileStream Open(string path, FileShare share, FileAccess access)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, access, share); }
        catch (IOException error) when (TickFile.IsSharingViolation(error))
        { throw new IOException("History is in use by another writer: " + path, error); }
    }

    public void Dispose()
    {
        lock (_days)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                foreach (var day in _days.Values) day.Dispose();
                _days.Clear();
            }
            finally
            {
                try { _scope.Dispose(); }
                finally { _instrument?.Dispose(); }
            }
        }
    }
}
