using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingHub.Common;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Serializes cooperating IB loads and publications, independently of the append-recorder lease.
/// Order: process gate, snapshot file lease, raw writer lease, bar writer lease. No ownership crosses a network await.
/// Supported file/namespace synchronization orders recovery authority before publication; it is not certified power-loss durability.
/// </summary>
internal static class IbHistoryCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, LegacyAudit> LegacyAudits = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DirectoryInitialization> DirectoryInitializations = new(StringComparer.Ordinal);

    private sealed class LegacyAudit
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal bool Complete;
    }
    private sealed class DirectoryInitialization
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal bool Complete;
        internal string? PendingAncestor;
    }
    private const string StateFolder = ".ib-history";
    private const string BackupFolder = ".ib-import-backups";

    internal static Ownership Enter(string root, string symbol, CancellationToken ct = default, Func<string, bool>? syncDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol is "." or ".." || Path.GetFileName(symbol) != symbol || symbol.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Invalid IB history symbol.");
        syncDirectory ??= StorageSync.SyncDirectory;
        root = CanonicalDirectory(root);
        EnsureDirectory(root, root, syncDirectory, ct);
        AuditLegacy(root, ct);
        string state = Path.Combine(root, StateFolder, symbol);
        CheckPath(root, state);
        EnsureDirectory(root, state, syncDirectory, ct);
        string lockPath = Path.Combine(state, ".snapshot.lock");
        CheckPath(root, lockPath);
        var gate = Gates.GetOrAdd(lockPath, static _ => new SemaphoreSlim(1, 1));
        gate.Wait(ct);
        FileStream? file = null;
        bool transferred = false;
        try
        {
            while (file == null)
            {
                ct.ThrowIfCancellationRequested();
                try { file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when (IsSharingViolation(error))
                {
                    ct.WaitHandle.WaitOne(10);
                }
            }
            var ownership = new Ownership(root, symbol, state, gate, file, syncDirectory, ct);
            transferred = true;
            file = null;
            try
            {
                ownership.Recover();
                return ownership;
            }
            catch (Exception error)
            {
                ownership.Dispose();
                if (error is IOException or OperationCanceledException) throw;
                throw new IOException($"IB history recovery failed for {symbol}; history remains blocked.", error);
            }
        }
        catch
        {
            if (!transferred)
            {
                try { file?.Dispose(); }
                finally { gate.Release(); }
            }
            throw;
        }
    }

    private static void AuditLegacy(string root, CancellationToken ct)
    {
        // Upgraded cooperating writers use each symbol's pending authority. Completed archives are immutable history,
        // not a read-time index: inspect legacy state once per root, and never cache a failed/unfinished audit.
        var audit = LegacyAudits.GetOrAdd(root, static _ => new LegacyAudit());
        if (Volatile.Read(ref audit.Complete)) return;
        audit.Gate.Wait(ct);
        try
        {
            if (audit.Complete) return;
            string backups = Path.Combine(root, BackupFolder);
            CheckPath(root, backups);
            if (Directory.Exists(backups))
            {
                foreach (string directory in Directory.EnumerateDirectories(backups))
                {
                    ct.ThrowIfCancellationRequested();
                    CheckPath(root, directory);
                    string journal = Path.Combine(directory, "transaction.json");
                    CheckPath(root, journal);
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(journal); }
                    catch (FileNotFoundException) { continue; } // No journal means preparation never authorized target changes.
                    catch (DirectoryNotFoundException) { continue; }
                    using var document = JsonDocument.Parse(bytes);
                    var value = document.RootElement;
                    if (!value.TryGetProperty("State", out var state) || state.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("Unrecognized IB recovery journal.");
                    string? status = state.GetString();
                    if (value.TryGetProperty("Version", out var version))
                    {
                        if (!version.TryGetInt32(out int number) || number != 1 || status is not ("staged" or "committed" or "rolled-back"))
                            throw new InvalidDataException("Unsupported IB recovery journal.");
                        // A current-format stage cannot change targets before its complete per-symbol pending pointer.
                        // Its live/recovery authority is checked under that symbol's coordinator, never inferred here.
                        continue;
                    }
                    if (status is not ("committed" or "rolled-back"))
                        throw new IOException($"An unresolved legacy IB import requires reviewed recovery before loading history: {journal}");
                }
            }
            Volatile.Write(ref audit.Complete, true);
        }
        catch (Exception error) when (error is not IOException and not OperationCanceledException)
        {
            throw new IOException("IB legacy history audit failed; retain and repair its import journals before retrying.", error);
        }
        finally { audit.Gate.Release(); }
    }

    internal static IReadOnlyList<string> Symbols(string root)
    {
        root = PhysicalRoot(root);
        var symbols = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string folder in new[] { IbBarImport.FeedId, IbBarStore.BarFolder, StateFolder })
        {
            string path = Path.Combine(root, folder);
            CheckPath(root, path);
            if (!Directory.Exists(path)) continue;
            foreach (string directory in Directory.EnumerateDirectories(path))
            {
                CheckPath(root, directory);
                symbols.Add(Path.GetFileName(directory));
            }
        }
        return symbols.ToArray();
    }

    // Resolve existing configured-root aliases (including macOS /var -> /private/var), not storage-layout identities.
    private static string PhysicalRoot(string root)
    {
        root = Path.GetFullPath(root);
        string current = Path.GetPathRoot(root)!;
        foreach (string part in root[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        return current;
    }

    private static string CanonicalDirectory(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string ancestor = directory;
        while (!Directory.Exists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new DirectoryNotFoundException($"IB storage root has no existing ancestor: {directory}");
        string physical = PhysicalRoot(ancestor);
        return ancestor == directory ? physical : Path.Combine(physical, Path.GetRelativePath(ancestor, directory));
    }

    private static void EnsureDirectory(string root, string directory, Func<string, bool> syncDirectory, CancellationToken ct = default, bool transient = false)
    {
        directory = Path.TrimEndingDirectorySeparator(directory);
        if (transient)
        {
            // Unique publication directories have one owner; do not retain one cache entry per archive.
            string parent = Path.GetDirectoryName(directory)!;
            EnsureDirectory(root, parent, syncDirectory, ct);
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            syncDirectory(directory);
            syncDirectory(parent);
            return;
        }

        var initialization = DirectoryInitializations.GetOrAdd(directory, static _ => new DirectoryInitialization());
        if (Directory.Exists(directory) && Volatile.Read(ref initialization.Complete)) return;
        // Initialize shared managed parents first. A concurrent sibling must not mistake a parent's mkdir for its completed sync.
        if (directory != root)
            EnsureDirectory(root, Path.GetDirectoryName(directory)!, syncDirectory, ct);
        initialization.Gate.Wait(ct);
        try
        {
            if (Directory.Exists(directory) && initialization.Complete) return;
            Volatile.Write(ref initialization.Complete, false);
            if (initialization.PendingAncestor == null)
            {
                string? ancestor = Path.GetDirectoryName(directory);
                while (ancestor != null && !Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
                initialization.PendingAncestor = ancestor ?? directory;
            }
            Directory.CreateDirectory(directory);
            // Retain the original boundary on any mkdir/sync failure, even when those directories now exist.
            for (string? current = directory; current != null; current = Path.GetDirectoryName(current))
            {
                syncDirectory(current);
                if (current == initialization.PendingAncestor) break;
            }
            initialization.PendingAncestor = null;
            Volatile.Write(ref initialization.Complete, true);
        }
        finally { initialization.Gate.Release(); }
    }

    internal static void CheckPath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (relative is "." or ".." || Path.IsPathFullyQualified(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("IB recovery path escapes its storage root.");
        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"IB recovery refuses a linked managed path: {current}");
        }
    }

    private static bool IsSharingViolation(IOException error) => (error.HResult & 0xFFFF) is 11 or 35 or 32 or 33;
    private sealed record Identity(long Length, string Hash);
    private sealed record Entry(string Folder, DateOnly Day, Identity? Original, Identity? Replacement);
    private sealed record Authority(int Version, string Transaction, string Symbol, Entry[] Entries);
    private sealed record Pending(string Transaction, string AuthorityHash);
    internal sealed record Output(string Path, IReadOnlyList<Trade>? Rows);

    internal sealed class Ownership : IDisposable
    {
        internal string Root { get; }
        internal string Symbol { get; }
        private readonly string _state;
        private readonly SemaphoreSlim _gate;
        private readonly Func<string, bool> _syncDirectory;
        private readonly CancellationToken _ct;
        private FileStream? _file;
        private FileStream? _raw, _bars;
        private string PendingPath => Path.Combine(_state, "pending.json");

        internal Ownership(string root, string symbol, string state, SemaphoreSlim gate, FileStream file, Func<string, bool> syncDirectory, CancellationToken ct)
        { Root = root; Symbol = symbol; _state = state; _gate = gate; _file = file; _syncDirectory = syncDirectory; _ct = ct; }

        internal Guid ReadChangeEpoch()
        {
            string path = Path.Combine(_state, "change-epoch.json");
            CheckPath(Root, path);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return Guid.Empty; } // Existing history predates invalidation epochs.
            catch (DirectoryNotFoundException) { return Guid.Empty; }
            try
            {
                Guid epoch = JsonSerializer.Deserialize<Guid>(bytes);
                if (epoch == Guid.Empty) throw new InvalidDataException("Invalid IB history change epoch.");
                return epoch;
            }
            catch (JsonException error) { throw new InvalidDataException("Damaged IB history change epoch.", error); }
        }

        internal void Invalidate() => WriteJson(Path.Combine(_state, "change-epoch.json"), Guid.NewGuid());

        internal IDisposable Writers(bool raw, bool bars)
        {
            if (_raw != null || _bars != null) throw new InvalidOperationException("IB writer ownership cannot be nested.");
            try
            {
                if (raw)
                {
                    CheckPath(Root, Path.Combine(Root, IbBarImport.FeedId, Symbol, ".grabando.lock"));
                    EnsureDirectory(Root, Path.Combine(Root, IbBarImport.FeedId, Symbol), _syncDirectory, _ct);
                    _raw = TickFile.TryLock(Root, IbBarImport.FeedId, Symbol, failOnNonSharingError: true)
                        ?? throw new IOException("IB instrument is recording/in use; stop its live consumers before changing recorded history.");
                }
                if (bars)
                {
                    CheckPath(Root, Path.Combine(Root, IbBarStore.BarFolder, Symbol, ".grabando.lock"));
                    EnsureDirectory(Root, Path.Combine(Root, IbBarStore.BarFolder, Symbol), _syncDirectory, _ct);
                    _bars = TickFile.TryLock(Root, IbBarStore.BarFolder, Symbol, failOnNonSharingError: true)
                        ?? throw new IOException("IB bar history is in use by another writer.");
                }
                return new WriterOwnership(this);
            }
            catch { ReleaseWriters(); throw; }
        }

        internal FileStream? StartRecorder()
        {
            CheckPath(Root, Path.Combine(Root, IbBarImport.FeedId, Symbol, ".grabando.lock"));
            CheckPath(Root, Path.Combine(Root, IbBarImport.FeedId, Symbol, ".recording-active.lock"));
            EnsureDirectory(Root, Path.Combine(Root, IbBarImport.FeedId, Symbol), _syncDirectory, _ct);
            return TickFile.TryLock(Root, IbBarImport.FeedId, Symbol, failOnNonSharingError: true, recordingWriter: true);
        }

        internal IbBarStore.PublishResult Publish(Instrument instrument, IReadOnlyList<Output> outputs, CancellationToken ct, Action<int>? beforePublish)
        {
            string transaction = Guid.NewGuid().ToString("N");
            string directory = TransactionDirectory(transaction);
            EnsureDirectory(Root, directory, _syncDirectory, ct, transient: true);
            string journal = Path.Combine(directory, "transaction.json");
            if (outputs.Count == 0)
            {
                WriteJson(journal, new { Version = 1, State = "committed", Entries = Array.Empty<Entry>() });
                return new(directory, 0);
            }
            bool pending = false;
            bool committed = false;
            try
            {
                var entries = new Entry[outputs.Count];
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < outputs.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var output = outputs[i];
                    var (folder, day) = TargetIdentity(output.Path);
                    string target = Target(folder, day);
                    if (!seen.Add(target)) throw new InvalidDataException("Duplicate IB publication target.");
                    if ((folder == IbBarImport.FeedId && _raw == null) || (folder == IbBarStore.BarFolder && _bars == null))
                        throw new InvalidOperationException("IB publication requires its target writer ownership.");
                    Identity? original = Identify(target);
                    if (original != null)
                    {
                        string backup = Path.Combine(directory, i + ".original");
                        File.Copy(target, backup);
                        FlushFile(backup);
                        RequireIdentity(backup, original);
                    }
                    Identity? replacement = null;
                    if (output.Rows != null)
                    {
                        string staged = Path.Combine(directory, i + ".staged");
                        using (var writer = new TickFile.Writer(staged, instrument, includeProvenance: true))
                        {
                            foreach (var row in output.Rows) writer.Write(row);
                            writer.FlushToDisk();
                        }
                        replacement = Identify(staged)!;
                    }
                    entries[i] = new(folder, day, original, replacement);
                }
                var authority = new Authority(1, transaction, Symbol, entries);
                string authorityPath = Path.Combine(directory, "authority.json");
                WriteJson(authorityPath, authority);
                string hash = IbBarStore.Fingerprint(authorityPath);
                WriteJson(journal, new { Version = 1, State = "staged", Entries = entries });
                ct.ThrowIfCancellationRequested();
                WriteJson(PendingPath, new Pending(transaction, hash));
                pending = true;
                // Check the entire authority and every original/staged/current identity before the first rename.
                Validate(authority, directory, committed: false, prepared: true);
                Invalidate(); // Publish invalidation before any target changes, including same-length replacements.
                for (int i = 0; i < entries.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    beforePublish?.Invoke(i);
                    string target = Target(entries[i].Folder, entries[i].Day);
                    EnsureDirectory(Root, Path.GetDirectoryName(target)!, _syncDirectory, ct);
                    if (entries[i].Replacement == null) File.Delete(target);
                    else File.Move(Path.Combine(directory, i + ".staged"), target, true);
                }
                _syncDirectory(directory); // Cross-directory moves removed staged names here.
                SyncTargetDirectories(authority);
                WriteJson(Path.Combine(directory, "committed.json"), new Pending(transaction, hash));
                committed = true;
                Finish(directory, "committed");
                return new(directory, entries.Length);
            }
            catch (Exception error)
            {
                // WriteJson may have renamed the marker before its directory synchronization failed.
                if (committed || File.Exists(Path.Combine(directory, "committed.json")))
                    throw new IOException($"IB commit marker was published; synchronization/finalization is required. Journal: {directory}", error);
                if (pending)
                {
                    try { RecoverOwned(); }
                    catch (Exception recovery)
                    {
                        throw new IOException($"IB publication failed; recovery required before history can be consumed. Journal: {directory}", new AggregateException(error, recovery));
                    }
                }
                if (error is OperationCanceledException)
                    throw new OperationCanceledException($"IB import canceled; original files restored. Staging journal: {directory}", error, ct);
                throw new IOException($"IB publication failed; original files restored. Backups and journal: {directory}", error);
            }
        }

        private (string Folder, DateOnly Day) TargetIdentity(string path)
        {
            string relative = Path.GetRelativePath(Root, Path.GetFullPath(path));
            // A caller may have used a legitimate configured-root alias; resolve only its root, not managed descendants.
            if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                string full = Path.GetFullPath(path);
                string? suppliedRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(full)));
                if (suppliedRoot == null || PhysicalRoot(suppliedRoot) != Root)
                    throw new InvalidDataException("IB publication target escapes its storage root.");
                relative = Path.GetRelativePath(suppliedRoot, full);
            }
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Length != 3 || parts[1] != Symbol || parts[0] is not (IbBarImport.FeedId or IbBarStore.BarFolder) ||
                !DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(parts[2]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ||
                parts[2] != day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".vtk")
                throw new InvalidDataException("IB publication target is not an owned standard day file.");
            return (parts[0], day);
        }

        private string Target(string folder, DateOnly day)
        {
            if (folder is not (IbBarImport.FeedId or IbBarStore.BarFolder)) throw new InvalidDataException("Invalid IB recovery folder.");
            string path = TickFile.DayPath(Root, folder, Symbol, day);
            CheckPath(Root, path);
            return path;
        }

        private string TransactionDirectory(string transaction)
        {
            if (!Guid.TryParseExact(transaction, "N", out _)) throw new InvalidDataException("Invalid IB recovery transaction identity.");
            string directory = Path.Combine(Root, BackupFolder, transaction);
            CheckPath(Root, directory);
            return directory;
        }

        private (Pending Pending, Authority Authority, string Directory) ReadAuthority()
        {
            CheckPath(Root, PendingPath);
            var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllBytes(PendingPath)) ?? throw new InvalidDataException("Missing IB pending authority.");
            string directory = TransactionDirectory(pending.Transaction);
            string path = Path.Combine(directory, "authority.json");
            CheckPath(Root, path);
            if (!ValidHash(pending.AuthorityHash) || IbBarStore.Fingerprint(path) != pending.AuthorityHash)
                throw new InvalidDataException("IB recovery authority is missing or damaged.");
            var authority = JsonSerializer.Deserialize<Authority>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Missing IB recovery entries.");
            if (authority.Version != 1 || authority.Transaction != pending.Transaction || authority.Symbol != Symbol || authority.Entries == null || authority.Entries.Length == 0)
                throw new InvalidDataException("Inconsistent IB recovery authority.");
            return (pending, authority, directory);
        }

        internal void Recover()
        {
            if (!File.Exists(PendingPath)) { CheckPath(Root, PendingPath); return; }
            try
            {
                var (_, authority, _) = ReadAuthority();
                using var writers = Writers(authority.Entries.Any(e => e.Folder == IbBarImport.FeedId), authority.Entries.Any(e => e.Folder == IbBarStore.BarFolder));
                RecoverOwned();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new IOException($"IB history recovery failed for {Symbol}; history remains blocked. Retain {_state} and its import backups.", error);
            }
        }

        private void RecoverOwned()
        {
            var (pending, authority, directory) = ReadAuthority();
            string marker = Path.Combine(directory, "committed.json");
            CheckPath(Root, marker);
            bool committed = File.Exists(marker);
            if (committed && JsonSerializer.Deserialize<Pending>(File.ReadAllBytes(marker)) != pending)
                throw new InvalidDataException("IB commit marker does not match its complete authority.");
            Validate(authority, directory, committed);
            if (!committed)
            {
                Invalidate(); // Rollback attempts may invalidate too; no restored data is published under an older epoch.
                for (int i = 0; i < authority.Entries.Length; i++)
                {
                    var entry = authority.Entries[i];
                    string target = Target(entry.Folder, entry.Day);
                    if (Identify(target) == entry.Original) continue;
                    if (entry.Original == null) File.Delete(target);
                    else
                    {
                        EnsureDirectory(Root, Path.GetDirectoryName(target)!, _syncDirectory);
                        string prepared = target + ".ib-restore-" + Guid.NewGuid().ToString("N");
                        CheckPath(Root, prepared);
                        File.Copy(Path.Combine(directory, i + ".original"), prepared);
                        FlushFile(prepared);
                        RequireIdentity(prepared, entry.Original);
                        File.Move(prepared, target, true);
                    }
                }
            }
            SyncTargetDirectories(authority); // Include targets restored before a previous recovery interruption.
            Finish(directory, committed ? "committed" : "rolled-back");
        }

        private void Validate(Authority authority, string directory, bool committed, bool prepared = false)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < authority.Entries.Length; i++)
            {
                var entry = authority.Entries[i] ?? throw new InvalidDataException("Missing IB recovery entry.");
                string target = Target(entry.Folder, entry.Day);
                if (!targets.Add(target) || !ValidIdentity(entry.Original) || !ValidIdentity(entry.Replacement))
                    throw new InvalidDataException("Invalid or duplicate IB recovery entry.");
                if ((entry.Folder == IbBarImport.FeedId && _raw == null) || (entry.Folder == IbBarStore.BarFolder && _bars == null))
                    throw new IOException("IB recovery cannot change a target without its writer ownership.");
                string backup = Path.Combine(directory, i + ".original"), staged = Path.Combine(directory, i + ".staged");
                CheckPath(Root, backup);
                CheckPath(Root, staged);
                if (Directory.Exists(backup) || Directory.Exists(staged))
                    throw new InvalidDataException("IB recovery authority requires regular backup/staged files.");
                if (entry.Original != null) RequireIdentity(backup, entry.Original);
                else if (File.Exists(backup)) throw new InvalidDataException("Unexpected IB original backup.");
                if (prepared && entry.Replacement != null && !File.Exists(staged))
                    throw new InvalidDataException("Missing prepared IB output.");
                if (File.Exists(staged))
                {
                    if (entry.Replacement == null) throw new InvalidDataException("Unexpected staged IB deletion.");
                    RequireIdentity(staged, entry.Replacement);
                }
                if (Directory.Exists(target)) throw new InvalidDataException("An IB target is a directory, not an authorized day file.");
                Identity? current = Identify(target);
                if (committed ? current != entry.Replacement : current != entry.Original && current != entry.Replacement)
                    throw new InvalidDataException($"IB target has unrelated/later bytes; refusing recovery: {target}");
            }
        }

        private void SyncTargetDirectories(Authority authority)
        {
            bool raw = false, bars = false;
            foreach (var entry in authority.Entries)
            {
                raw |= entry.Folder == IbBarImport.FeedId;
                bars |= entry.Folder == IbBarStore.BarFolder;
            }
            if (raw)
            {
                string directory = Path.Combine(Root, IbBarImport.FeedId, Symbol);
                CheckPath(Root, directory);
                _syncDirectory(directory);
            }
            if (bars)
            {
                string directory = Path.Combine(Root, IbBarStore.BarFolder, Symbol);
                CheckPath(Root, directory);
                _syncDirectory(directory);
            }
        }

        private void Finish(string directory, string state)
        {
            WriteJson(Path.Combine(directory, "transaction.json"), new { Version = 1, State = state });
            CheckPath(Root, PendingPath);
            File.Delete(PendingPath);
            _syncDirectory(_state);
        }

        private void WriteJson<T>(string path, T value)
        {
            CheckPath(Root, path);
            string prepared = path + ".tmp-" + Guid.NewGuid().ToString("N");
            CheckPath(Root, prepared);
            using (var stream = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                StorageSync.Flush(stream);
            }
            File.Move(prepared, path, true);
            _syncDirectory(Path.GetDirectoryName(path)!);
        }

        private static Identity? Identify(string path)
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (stream.Length != length) throw new IOException("IB transaction file changed while validating its identity.");
            return new(length, hash);
        }
        private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
        private static bool ValidIdentity(Identity? identity) => identity == null || identity.Length >= 0 && ValidHash(identity.Hash);
        private static void RequireIdentity(string path, Identity expected)
        {
            if (Identify(path) != expected) throw new InvalidDataException($"Missing or damaged IB recovery file: {path}");
        }
        private static void FlushFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            StorageSync.Flush(stream);
        }
        private void ReleaseWriters()
        {
            var bars = _bars;
            var raw = _raw;
            _bars = null;
            _raw = null;
            try { bars?.Dispose(); }
            finally { raw?.Dispose(); }
        }
        private sealed class WriterOwnership(Ownership owner) : IDisposable
        {
            private Ownership? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseWriters();
        }
        public void Dispose()
        {
            var file = Interlocked.Exchange(ref _file, null);
            if (file == null) return;
            try { ReleaseWriters(); }
            finally
            {
                try { file.Dispose(); }
                finally { _gate.Release(); }
            }
        }
    }
}
