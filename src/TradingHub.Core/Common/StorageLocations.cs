using System.Security.Cryptography;
using System.Text.Json;

namespace TradingHub.Common;

/// <summary>Opt-in locations. Null preserves the existing Trading Hub location and directory names.</summary>
public sealed record StorageLocationSelection(string? AppRoot = null, string? MarketRoot = null, string? ScriptsRoot = null);

public sealed record ResolvedStorageLocations(string AppRoot, string MarketRoot, string ScriptsRoot,
    string IndicatorsRoot, string StrategiesRoot, string BacktestsRoot, string LiveRoot, string? KeychainFile);

/// <summary>One immutable selection per process; publishing a new selection requires a protected restart.</summary>
public static class StorageLocations
{
    // A fresh profile may not have created these directories; resolve their OS-defined paths without creating them.
    public static string DefaultAppRoot => Product.Folder(Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify));
    public static string DefaultScriptsRoot => Product.Folder(Environment.GetFolderPath(
        Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify));
    public static string BootstrapRoot => Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } root ? root : DefaultAppRoot;
    private static readonly Lazy<ResolvedStorageLocations> Initial = new(() => CreateStore().Resolve());
    public static ResolvedStorageLocations Startup => Initial.Value;
    public static StorageLocationStore CreateStore(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return CreateStore(environment, () => DefaultAppRoot, () => DefaultScriptsRoot);
    }

    internal static StorageLocationStore CreateStore(Func<string, string?> environment,
        Func<string> defaultAppRoot, Func<string> defaultScriptsRoot)
    {
        // Only the bootstrap authority is needed before reading a persisted explicit scripts selection.
        return new(environment("TRADINGHUB_DATA_DIR") is { Length: > 0 } data ? data : defaultAppRoot(),
            defaultScriptsRoot, environment);
    }
}

public sealed record StorageCopyRoot(string Source, string Destination, IReadOnlyList<string> Exclusions);
public sealed record StorageFileSnapshot(int RootIndex, string RelativePath, long Length, string Sha256, bool IsDirectory = false);
public sealed record StorageRelocationPreview(StorageLocationSelection Selection, ResolvedStorageLocations Original,
    ResolvedStorageLocations Proposed, IReadOnlyList<StorageCopyRoot> Roots, IReadOnlyList<StorageFileSnapshot> Files)
{
    public long Bytes => Files.Sum(f => f.Length);
    public bool HasChanges => Roots.Count > 0;
}
public sealed record StorageRelocationProgress(string Phase, string RelativePath, int Completed, int Total);
public sealed record StorageRelocationResult(bool Success, bool Cancelled, string Message);

/// <summary>
/// Copies into owned staging trees, verifies every file and the unchanged source manifest, then atomically
/// publishes one location pointer. It never moves or deletes a source tree. Call ApplyPendingAsync only with
/// exclusive, drained ownership of the old roots, before application stores and watchers are constructed.
/// </summary>
public sealed class StorageLocationStore
{
    private const string SelectionName = "storage-locations.json";
    private const string PendingName = "storage-relocation.json";
    private const string ResultName = "storage-relocation-result.json";
    private static readonly string[] MarketDirectories = { "Ticks", "Klines", "Compact", "DepthHistory", "InstrumentCatalog" };
    private static readonly string[] ControlFiles = { SelectionName, PendingName, ResultName, ".storage-relocation.lock", ".tradinghub.instance.lock" };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    // Conservative on every volume: a case variant is never assumed to name the same authority.
    private const StringComparison PathComparison = StringComparison.Ordinal;
    private readonly string _bootstrapRoot;
    private readonly Lazy<string> _defaultScriptsRoot;
    private readonly Func<string, string?> _environment;
    private readonly Func<string, string, CancellationToken, Task> _copyFile;
    public string BootstrapRoot => _bootstrapRoot;

    public StorageLocationStore(string bootstrapRoot, string defaultScriptsRoot, Func<string, string?>? environment = null,
        Func<string, string, CancellationToken, Task>? copyFile = null)
        : this(bootstrapRoot, () => defaultScriptsRoot, environment, copyFile)
    {
    }

    internal StorageLocationStore(string bootstrapRoot, Func<string> defaultScriptsRoot, Func<string, string?>? environment = null,
        Func<string, string, CancellationToken, Task>? copyFile = null)
    {
        _bootstrapRoot = Normalize(bootstrapRoot);
        _defaultScriptsRoot = new(() => Normalize(defaultScriptsRoot()));
        _environment = environment ?? (_ => null);
        _copyFile = copyFile ?? CopyFileAsync;
    }

    public StorageLocationSelection Selection => Read<StorageLocationSelection>(SelectionName) ?? new();
    public bool HasPending => File.Exists(ControlPath(PendingName));
    public StorageRelocationResult? LastResult
    {
        get
        {
            try { return Read<StorageRelocationResult>(ResultName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { return new(false, false, "The last relocation receipt is unreadable. Reading the receipt did not change the active locations."); }
        }
    }
    public ResolvedStorageLocations Resolve()
    {
        var selection = Selection;
        var resolved = Resolve(selection);
        if (_environment("TRADINGHUB_DATA_DIR") is not { Length: > 0 })
        {
            RequireAvailable(selection.AppRoot, resolved.AppRoot);
            RequireAvailable(selection.MarketRoot, resolved.MarketRoot);
            if (_environment("TRADINGHUB_INDICATORS_DIR") is not { Length: > 0 } ||
                _environment("TRADINGHUB_STRATEGIES_DIR") is not { Length: > 0 })
                RequireAvailable(selection.ScriptsRoot, resolved.ScriptsRoot);
        }
        return resolved;
    }

    private static void RequireAvailable(string? configured, string effective)
    {
        if (string.IsNullOrWhiteSpace(configured)) return;
        if (!Directory.Exists(effective))
            throw new IOException("The explicitly selected storage location is unavailable. Restore access before starting Trading Hub; no empty replacement or stale fallback was opened: " + effective);
        using var entries = Directory.EnumerateFileSystemEntries(effective).GetEnumerator();
        _ = entries.MoveNext(); // Fail before constructing stores if the selected root cannot be read.
    }

    public ResolvedStorageLocations Resolve(StorageLocationSelection selection)
    {
        string? Env(string name) => _environment(name) is { Length: > 0 } value ? Normalize(value) : null;
        string? data = Env("TRADINGHUB_DATA_DIR");
        string app = data ?? Optional(selection.AppRoot) ?? _bootstrapRoot;
        string market = data ?? Optional(selection.MarketRoot) ?? app;
        string scripts = data ?? Optional(selection.ScriptsRoot) ?? _defaultScriptsRoot.Value;
        return new(app, market, scripts,
            Env("TRADINGHUB_INDICATORS_DIR") ?? Path.Combine(scripts, "Indicadores"),
            Env("TRADINGHUB_STRATEGIES_DIR") ?? Path.Combine(scripts, "Estrategias"),
            Env("TRADINGHUB_BACKTESTS_DIR") ?? Path.Combine(app, "Backtests"),
            Env("TRADINGHUB_LIVE_DIR") ?? Path.Combine(app, "Live"), Env("TRADINGHUB_KEYCHAIN_FILE"));
    }

    public async Task<StorageRelocationPreview> PreviewAsync(StorageLocationSelection selection, CancellationToken ct,
        IProgress<StorageRelocationProgress>? progress = null)
    {
        var old = Resolve();
        var proposed = Resolve(selection);
        selection = CanonicalSelection(selection, proposed);
        var roots = Plan(old, proposed);
        var files = new List<StorageFileSnapshot>();
        for (int r = 0; r < roots.Count; r++)
        {
            var root = roots[r];
            RejectLinks(root.Source);
            RejectLinks(root.Destination);
            if (Directory.Exists(root.Destination) || File.Exists(root.Destination))
                throw new IOException("Choose a new, nonexistent destination folder. Existing folders are never overwritten: " + root.Destination);
            if (!Directory.Exists(root.Source)) continue;
            foreach (var (path, directory) in EnumerateEntries(root.Source, root.Exclusions))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root.Source, path);
                progress?.Report(new("Previewing", relative, files.Count, 0));
                var (length, hash) = directory ? (0L, "") : await HashAsync(path, ct).ConfigureAwait(false);
                files.Add(new(r, relative, length, hash, directory));
            }
        }
        return new(selection, old, proposed, roots, files);
    }

    /// <summary>Approval stages only the choice, not a mutable live copy; startup rehashes after all writers drain.</summary>
    public async Task StageAsync(StorageRelocationPreview preview, CancellationToken ct)
    {
        if (Resolve() != preview.Original) throw new IOException("Storage locations changed. Preview again before staging.");
        if (Resolve(preview.Selection) != preview.Proposed || !SamePlan(Plan(preview.Original, preview.Proposed), preview.Roots))
            throw new IOException("Storage authority changed after preview. Preview again before staging.");
        if (!preview.HasChanges) throw new InvalidOperationException("No effective location change. Explicit environment overrides take precedence.");
        await AtomicWriteAsync(PendingName, preview.Selection, ct).ConfigureAwait(false);
    }

    public void CancelPending()
    {
        // The pending request is ours; this does not touch any data or approved root pointer.
        File.Delete(ControlPath(PendingName));
    }

    public async Task<StorageRelocationResult> ApplyPendingAsync(CancellationToken ct,
        IProgress<StorageRelocationProgress>? progress = null)
    {
        if (!HasPending) return new(true, false, "No storage relocation is pending.");
        Directory.CreateDirectory(_bootstrapRoot);
        var stages = new List<string>();
        StorageRelocationResult result;
        try
        {
            using var lease = new FileStream(ControlPath(".storage-relocation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var selection = Read<StorageLocationSelection>(PendingName) ?? throw new InvalidDataException("The pending location request is unreadable; original locations remain active.");
            var preview = await PreviewAsync(selection, ct, progress).ConfigureAwait(false);
            if (selection != preview.Selection) throw new InvalidDataException("The pending location approval is not canonical. Preview and approve the locations again.");
            if (!preview.HasChanges) throw new InvalidOperationException("Environment overrides prevent the pending location change.");
            foreach (var root in preview.Roots)
            {
                string stage = root.Destination + ".tradinghub-copy-" + Guid.NewGuid().ToString("N");
                RejectLinks(stage);
                Directory.CreateDirectory(stage);
                stages.Add(stage);
            }
            int completed = 0;
            foreach (var file in preview.Files)
            {
                ct.ThrowIfCancellationRequested();
                var root = preview.Roots[file.RootIndex];
                var source = SafeChild(root.Source, file.RelativePath);
                var target = SafeChild(stages[file.RootIndex], file.RelativePath);
                if (file.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    completed++;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                progress?.Report(new("Copying", file.RelativePath, completed, preview.Files.Count));
                await _copyFile(source, target, ct).ConfigureAwait(false);
                var copied = await HashAsync(target, ct).ConfigureAwait(false);
                if (copied.Length != file.Length || copied.Hash != file.Sha256)
                    throw new IOException("Copied-file hash verification failed: " + file.RelativePath);
                completed++;
            }
            // A late external writer or changed file invalidates the entire operation before publication.
            var current = await PreviewAsync(selection, ct, progress).ConfigureAwait(false);
            if (preview.Original != current.Original || preview.Proposed != current.Proposed || !SamePlan(preview.Roots, current.Roots))
                throw new IOException("Storage authority changed during relocation. Original locations remain authoritative.");
            if (!preview.Files.SequenceEqual(current.Files)) throw new IOException("Source files changed during relocation. Original locations remain authoritative.");
            ct.ThrowIfCancellationRequested();
            if (Read<StorageLocationSelection>(PendingName) != selection) throw new OperationCanceledException("Relocation approval was cancelled or changed.");
            for (int r = 0; r < preview.Roots.Count; r++)
            {
                RejectLinks(preview.Roots[r].Destination);
                Directory.Move(stages[r], preview.Roots[r].Destination);
            }
            // This is the only authority switch, after every copied tree has passed verification.
            await AtomicWriteAsync(SelectionName, preview.Selection, ct).ConfigureAwait(false);
            result = new(true, false, "Storage relocation verified and activated. Original data was retained.");
            try { File.Delete(ControlPath(PendingName)); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (OperationCanceledException)
        {
            result = new(false, true, "Storage relocation cancelled. Original locations remain active; original files were retained.");
            try { File.Delete(ControlPath(PendingName)); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            result = new(false, false, "Storage relocation failed. Original locations remain active; original files were retained. " + ex.Message);
        }
        finally
        {
            foreach (var stage in stages)
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // A receipt failure cannot roll back or misreport an already completed atomic authority switch.
        try { await AtomicWriteAsync(ResultName, result, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return result;
    }

    private List<StorageCopyRoot> Plan(ResolvedStorageLocations old, ResolvedStorageLocations next)
    {
        var roots = new List<StorageCopyRoot>();
        if (!Same(old.AppRoot, next.AppRoot))
        {
            var excluded = new List<string> { ".tradinghub.instance.lock" };
            if (Same(old.AppRoot, _bootstrapRoot)) excluded.AddRange(ControlFiles);
            if (Same(old.MarketRoot, old.AppRoot) && !Same(next.MarketRoot, next.AppRoot)) excluded.AddRange(MarketDirectories);
            roots.Add(new(old.AppRoot, next.AppRoot, excluded));
        }
        if (!Same(old.MarketRoot, next.MarketRoot) && !(Same(old.MarketRoot, old.AppRoot) && Same(next.MarketRoot, next.AppRoot)))
        {
            // A market root contains market data only, never credentials/account settings copied from the app root.
            if (Same(old.MarketRoot, old.AppRoot))
            {
                var excluded = Directory.Exists(old.MarketRoot)
                    ? Directory.EnumerateFileSystemEntries(old.MarketRoot).Select(Path.GetFileName)
                        .Where(name => name != null && !MarketDirectories.Contains(name, StringComparer.Ordinal)).Select(name => name!).ToArray()
                    : Array.Empty<string>();
                roots.Add(new(old.MarketRoot, next.MarketRoot, excluded));
            }
            else roots.Add(new(old.MarketRoot, next.MarketRoot, Array.Empty<string>()));
        }
        if (!Same(old.ScriptsRoot, next.ScriptsRoot)) roots.Add(new(old.ScriptsRoot, next.ScriptsRoot, Array.Empty<string>()));
        for (int i = 0; i < roots.Count; i++)
        {
            var root = roots[i];
            foreach (var source in new[] { old.AppRoot, old.MarketRoot, old.ScriptsRoot, old.IndicatorsRoot, old.StrategiesRoot })
                if (Within(root.Destination, source) || Within(source, root.Destination))
                    throw new IOException("Destination and existing storage trees must not overlap: " + root.Destination);
            for (int j = 0; j < i; j++)
                if (Within(root.Destination, roots[j].Destination) || Within(roots[j].Destination, root.Destination))
                    throw new IOException("Relocation destinations must not overlap.");
        }
        return roots;
    }

    private StorageLocationSelection CanonicalSelection(StorageLocationSelection requested, ResolvedStorageLocations resolved)
    {
        // An override is runtime authority, not a new persisted default. With DATA_DIR all three roots are overridden.
        if (_environment("TRADINGHUB_DATA_DIR") is { Length: > 0 }) return Selection;
        return new(string.IsNullOrWhiteSpace(requested.AppRoot) ? null : resolved.AppRoot,
            string.IsNullOrWhiteSpace(requested.MarketRoot) ? null : resolved.MarketRoot,
            string.IsNullOrWhiteSpace(requested.ScriptsRoot) ? null : resolved.ScriptsRoot);
    }

    private static bool SamePlan(IReadOnlyList<StorageCopyRoot> left, IReadOnlyList<StorageCopyRoot> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!Same(left[i].Source, right[i].Source) || !Same(left[i].Destination, right[i].Destination) ||
                !left[i].Exclusions.OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(right[i].Exclusions.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
                return false;
        return true;
    }

    private static IEnumerable<(string Path, bool IsDirectory)> EnumerateEntries(string root, IReadOnlyList<string> exclusions)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (exclusions.Contains(Path.GetFileName(entry), StringComparer.Ordinal)) continue;
            var attr = File.GetAttributes(entry);
            if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked files and folders require manual review before relocation: " + entry);
            bool directory = (attr & FileAttributes.Directory) != 0;
            yield return (entry, directory);
            if (directory)
                foreach (var nested in EnumerateEntries(entry, Array.Empty<string>())) yield return nested;
        }
    }

    private T? Read<T>(string name)
    {
        var path = ControlPath(name);
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
    }
    private string ControlPath(string name) => Path.Combine(_bootstrapRoot, name);
    private async Task AtomicWriteAsync<T>(string name, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(_bootstrapRoot);
        string path = ControlPath(name), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        RejectLinks(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }
    private static async Task<(long Length, string Hash)> HashAsync(string path, CancellationToken ct)
    {
        RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return (stream.Length, Convert.ToHexString(hash));
    }
    private static string SafeChild(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(path, root) || Same(path, root)) throw new IOException("Invalid relocation file path.");
        RejectLinks(path);
        return path;
    }
    private static void RejectLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked storage paths require manual review: " + current);
    }
    private static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Storage locations must be absolute paths.", nameof(path));
        // Resolve existing ancestors once so OS aliases and user-selected path aliases cannot bypass overlap checks.
        var full = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(full)!;
        foreach (var segment in full[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            if (!Directory.Exists(resolved) && !File.Exists(resolved)) continue;
            if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo info = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw new IOException("Storage alias could not be resolved: " + resolved);
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }
    private static string? Optional(string? path) => string.IsNullOrWhiteSpace(path) ? null : Normalize(path.Trim());
    private static bool Same(string a, string b) => string.Equals(a, b, PathComparison);
    private static bool Within(string path, string root) => Same(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);
}
