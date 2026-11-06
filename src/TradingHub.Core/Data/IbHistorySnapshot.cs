using System.Buffers.Binary;
using System.Security.Cryptography;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Cache identity for a caller-scoped IB range; an epoch invalidates before mutation, not a commit/power-loss guarantee.</summary>
public readonly record struct IbHistoryStamp(Guid ChangeEpoch, string FilesFingerprint);

/// <summary>
/// One cooperating IB publication generation for an entire logical load. Dispose before awaiting or delivering callbacks.
/// Concurrent real recording retains valid-prefix semantics; this does not freeze newly appended real trades.
/// </summary>
public sealed class IbHistorySnapshot : IDisposable
{
    private IbHistoryCoordinator.Ownership? _ownership;
    private Guid? _changeEpoch;
    internal IbHistorySnapshot(IbHistoryCoordinator.Ownership ownership) => _ownership = ownership;
    private IbHistoryCoordinator.Ownership Ownership => _ownership ?? throw new ObjectDisposedException(nameof(IbHistorySnapshot));

    /// <summary>An active appender permits committed prefixes, not proof that a closed trade's entire range is saved.</summary>
    public bool HasActiveRecorder
    {
        get
        {
            var owner = Ownership;
            CheckDay(owner, DateOnly.MinValue);
            return TickFile.HasActiveRecordingWriter(owner.Root, IbBarImport.FeedId, owner.Symbol);
        }
    }

    /// <summary>Capture source bytes before reading the scoped range; same-metadata replacements and concurrent append growth invalidate later reuse.</summary>
    public IbHistoryStamp GetStamp(DateTime fromUtc, DateTime toUtc)
    {
        var owner = Ownership;
        Guid epoch = _changeEpoch ?? owner.ReadChangeEpoch();
        _changeEpoch = epoch; // Publication ownership prevents a cooperating mutation for this snapshot's lifetime.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> metadata = stackalloc byte[21];
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        int first = DateOnly.FromDateTime(fromUtc).DayNumber;
        int last = toUtc > fromUtc ? DateOnly.FromDateTime(toUtc.AddTicks(-1)).DayNumber : first - 1;
        for (int number = first; number <= last; number++)
        {
            var day = DateOnly.FromDayNumber(number);
            for (byte source = 0; source < 2; source++)
            {
                string folder = source == 0 ? IbBarImport.FeedId : IbBarStore.BarFolder;
                string path = TickFile.DayPath(owner.Root, folder, owner.Symbol, day);
                IbHistoryCoordinator.CheckPath(owner.Root, path);
                if (Directory.Exists(path)) throw new InvalidDataException("IB day metadata identifies a directory, not a data file.");
                long length, modified;
                try
                {
                    var info = new FileInfo(path);
                    length = info.Length;
                    modified = info.LastWriteTimeUtc.Ticks;
                }
                catch (FileNotFoundException) { length = -1; modified = 0; }
                catch (DirectoryNotFoundException) { length = -1; modified = 0; }
                metadata[0] = source;
                BinaryPrimitives.WriteInt32LittleEndian(metadata[1..], number);
                BinaryPrimitives.WriteInt64LittleEndian(metadata[5..], length);
                BinaryPrimitives.WriteInt64LittleEndian(metadata[13..], modified);
                hash.AppendData(metadata);
                if (length >= 0)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    SHA256.HashData(stream, digest);
                    hash.AppendData(digest);
                }
            }
        }
        return new(epoch, Convert.ToHexString(hash.GetHashAndReset()));
    }

    public List<Trade> ReadDay(DateOnly day, decimal? expectedTickSize = null)
    {
        var owner = Ownership;
        CheckDay(owner, day);
        return IbBarStore.ReadDayOwned(owner.Root, owner.Symbol, day, expectedTickSize);
    }

    internal List<Trade> ReadRecordedDay(DateOnly day, decimal? expectedTickSize = null)
    {
        var owner = Ownership;
        CheckDay(owner, day);
        return IbBarStore.ReadRecordedDayOwned(owner.Root, owner.Symbol, day, expectedTickSize);
    }

    internal List<Trade> ReadBarDay(DateOnly day, decimal? expectedTickSize = null)
    {
        var owner = Ownership;
        CheckDay(owner, day);
        return IbBarStore.ReadBarDayOwned(owner.Root, owner.Symbol, day, expectedTickSize);
    }

    public IReadOnlyList<DateOnly> AvailableDays()
    {
        var owner = Ownership;
        CheckDay(owner, DateOnly.MinValue);
        return TickFile.AvailableDays(owner.Root, IbBarImport.FeedId, owner.Symbol)
            .Concat(TickFile.AvailableDays(owner.Root, IbBarStore.BarFolder, owner.Symbol)).Distinct().Order().ToArray();
    }

    public List<string> DayFiles(DateOnly day)
    {
        var owner = Ownership;
        CheckDay(owner, day);
        var files = new List<string>(2);
        string raw = TickFile.DayPath(owner.Root, IbBarImport.FeedId, owner.Symbol, day);
        if (File.Exists(raw) && new FileInfo(raw).Length > 16) files.Add(raw);
        string bars = TickFile.DayPath(owner.Root, IbBarStore.BarFolder, owner.Symbol, day);
        if (File.Exists(bars)) files.Add(bars);
        return files;
    }

    public IReadOnlyList<DayData> Days()
    {
        var owner = Ownership;
        var result = new List<DayData>();
        foreach (var day in AvailableDays())
        {
            CheckDay(owner, day);
            string raw = TickFile.DayPath(owner.Root, IbBarImport.FeedId, owner.Symbol, day);
            string bars = TickFile.DayPath(owner.Root, IbBarStore.BarFolder, owner.Symbol, day);
            long bytes = (File.Exists(raw) ? new FileInfo(raw).Length : 0) + (File.Exists(bars) ? new FileInfo(bars).Length : 0);
            result.Add(new(day, File.Exists(raw) ? raw : bars, bytes));
        }
        return result;
    }

    public decimal? StoredTickSize()
    {
        var days = AvailableDays();
        if (days.Count == 0) return null;
        foreach (string path in DayFiles(days[^1]))
            if (TickFile.ReadTickSize(path) is { } tick) return tick;
        return null;
    }

    private static void CheckDay(IbHistoryCoordinator.Ownership owner, DateOnly day)
    {
        IbHistoryCoordinator.CheckPath(owner.Root, TickFile.DayPath(owner.Root, IbBarImport.FeedId, owner.Symbol, day));
        IbHistoryCoordinator.CheckPath(owner.Root, TickFile.DayPath(owner.Root, IbBarStore.BarFolder, owner.Symbol, day));
    }

    public void Dispose()
    {
        var owner = _ownership;
        _ownership = null;
        owner?.Dispose();
    }
}
