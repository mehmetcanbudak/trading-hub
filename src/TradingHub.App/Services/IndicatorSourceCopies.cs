using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services;

/// <summary>Complete deterministic text of an editable copy and the manifest describing where it came from.</summary>
public sealed record GeneratedSourceCopy(IReadOnlyDictionary<string, string> Sources, string EntryFile, string TypeName, SourceCopyManifest Manifest);

/// <summary>
/// Stored next to an editable copy (<see cref="IndicatorSourceCopies.ManifestFile"/>). Records the exact original documents
/// the copy reflects and the hashes of the files as generated, so later versions can tell untouched copies from edited ones.
/// </summary>
public sealed class SourceCopyManifest
{
    public int Version { get; set; } = 1;
    public string OriginalType { get; set; } = "";
    public string? OriginalAssembly { get; set; }
    public string ClassName { get; set; } = "";
    /// <summary>The copy's own namespace; kept on regeneration so saved charts keep the same copy type.</summary>
    public string NamespacePrefix { get; set; } = "";
    public string CopyType { get; set; } = "";
    public string EntryFile { get; set; } = "";
    /// <summary>Embedded document id → hash of the original source this copy reflects (or the user chose to keep edits against).</summary>
    public Dictionary<string, string> Originals { get; set; } = new(StringComparer.Ordinal);
    /// <summary>File name → hash of the text exactly as generated.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}

public enum SourceCopyState
{
    /// <summary>The original has not changed since the copy was made (or since the user chose to keep their edits).</summary>
    Current,
    /// <summary>The copy was never edited and has been regenerated from the updated original.</summary>
    Refreshed,
    /// <summary>The copy was edited and its original changed: the user decides; nothing was overwritten.</summary>
    OriginalUpdated,
    /// <summary>The original indicator no longer ships with its source in this version; the copy is left as it is.</summary>
    OriginalUnavailable,
}

public sealed record SourceCopyStatus(string Directory, SourceCopyManifest Manifest, SourceCopyState State, string? Detail = null)
{
    public string OriginalType => Manifest.OriginalType;
    public string CopyType => Manifest.CopyType;
}

/// <summary>
/// Version tracking for editable copies of built-in indicators. On start-up untouched copies follow the new original
/// under their unchanged type identity; edited copies whose original changed are only flagged. A user edit is never
/// overwritten automatically: replacing an edited copy is an explicit action that first moves the whole previous copy
/// to a backup folder outside the compiled indicator folder.
/// </summary>
public static class IndicatorSourceCopies
{
    public const string ManifestFile = ".source-copy.json";
    public const string BackupFolderName = "Indicator copy backups";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));

    public static SourceCopyManifest? ReadManifest(string directory)
    {
        string path = Path.Combine(directory, ManifestFile);
        if (!File.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<SourceCopyManifest>(File.ReadAllText(path), Json);
        return manifest is { OriginalType.Length: > 0, ClassName.Length: > 0, NamespacePrefix.Length: > 0 } ? manifest : null;
    }

    internal static void WriteManifest(string directory, SourceCopyManifest manifest)
    {
        string path = Path.Combine(directory, ManifestFile), temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Json), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Built-in source of a loaded indicator assembly.</summary>
    public static IndicatorSources? ResolveLoaded(SourceCopyManifest manifest)
    {
        if (string.IsNullOrEmpty(manifest.OriginalAssembly)) return null;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == manifest.OriginalAssembly);
        try { assembly ??= Assembly.Load(manifest.OriginalAssembly); }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException) { return null; }
        return IndicatorSources.ForAssembly(assembly);
    }

    /// <summary>The copy's C# files are exactly as generated (no edits, additions or deletions).</summary>
    public static bool IsUntouched(string directory, SourceCopyManifest manifest)
    {
        var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length != manifest.Files.Count) return false;
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(directory, file);
            if (!manifest.Files.TryGetValue(relative, out var hash) || Hash(File.ReadAllText(file)) != hash) return false;
        }

        return true;
    }

    /// <summary>
    /// Checks every tracked copy under <paramref name="folder"/>. Untouched copies of changed originals are regenerated
    /// (only after the complete new copy compiles with the user's other sources); edited ones are reported.
    /// </summary>
    public static IReadOnlyList<SourceCopyStatus> Review(string folder, Func<SourceCopyManifest, IndicatorSources?>? resolve = null)
    {
        resolve ??= ResolveLoaded;
        string root = Path.GetFullPath(folder);
        var result = new List<SourceCopyStatus>();
        if (!Directory.Exists(root)) return result;
        foreach (string manifestPath in Directory.EnumerateFiles(root, ManifestFile, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string directory = Path.GetDirectoryName(manifestPath)!;
            try
            {
                var manifest = ReadManifest(directory);
                if (manifest != null) result.Add(ReviewOne(root, directory, manifest, resolve));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                typeof(IndicatorSourceCopies).LogWarn($"Could not check the editable copy in {directory}: {error.Message}");
            }
        }

        return result;
    }

    private static SourceCopyStatus ReviewOne(string root, string directory, SourceCopyManifest manifest, Func<SourceCopyManifest, IndicatorSources?> resolve)
    {
        var sources = resolve(manifest);
        if (sources == null) return new(directory, manifest, SourceCopyState.OriginalUnavailable);
        var current = sources.CurrentHashes(manifest.Originals.Keys);
        if (current != null && current.Count == manifest.Originals.Count && current.All(p => manifest.Originals.GetValueOrDefault(p.Key) == p.Value))
            return new(directory, manifest, SourceCopyState.Current);

        GeneratedSourceCopy generated;
        try { generated = sources.Generate(manifest.OriginalType, manifest.ClassName, manifest.NamespacePrefix); }
        catch (NotSupportedException) { return new(directory, manifest, SourceCopyState.OriginalUnavailable); }

        bool untouched = IsUntouched(directory, manifest);
        if (untouched && SameFiles(generated.Manifest.Files, manifest.Files))
        {
            // the original changed in ways that do not alter the copy (for example an unrelated declaration)
            WriteManifest(directory, generated.Manifest);
            return new(directory, generated.Manifest, SourceCopyState.Current);
        }

        if (!untouched) return new(directory, manifest, SourceCopyState.OriginalUpdated);
        try
        {
            return Replace(root, directory, generated, manifest, requireUntouched: true)
                ? new(directory, generated.Manifest, SourceCopyState.Refreshed)
                : new(directory, manifest, SourceCopyState.OriginalUpdated);
        }
        catch (NotSupportedException error)
        {
            return new(directory, manifest, SourceCopyState.OriginalUpdated, error.Message);
        }
    }

    private static bool SameFiles(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    /// <summary>
    /// Explicit user choice for an edited copy: regenerate it from the current original under the same type identity.
    /// The complete previous copy directory is moved to <see cref="BackupFolderName"/> beside the indicator folder.
    /// Returns the backup directory.
    /// </summary>
    public static string RestoreOriginal(string folder, string directory, Func<SourceCopyManifest, IndicatorSources?>? resolve = null)
    {
        resolve ??= ResolveLoaded;
        string root = Path.GetFullPath(folder);
        var manifest = ReadManifest(directory) ?? throw new InvalidOperationException("This folder is not a tracked editable copy.");
        var sources = resolve(manifest) ?? throw new NotSupportedException("The original source is not available in this version. Nothing was changed.");
        var generated = sources.Generate(manifest.OriginalType, manifest.ClassName, manifest.NamespacePrefix);
        string backups = Path.Combine(Path.GetDirectoryName(root)!, BackupFolderName);
        Directory.CreateDirectory(backups);
        string backup = Path.Combine(backups, $"{Path.GetFileName(directory)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        Replace(root, directory, generated, manifest, requireUntouched: false, backup);
        return backup;
    }

    /// <summary>Explicit user choice: keep the edited copy as it is and stop flagging it for the current original.</summary>
    public static void KeepEdits(string directory, Func<SourceCopyManifest, IndicatorSources?>? resolve = null)
    {
        resolve ??= ResolveLoaded;
        var manifest = ReadManifest(directory) ?? throw new InvalidOperationException("This folder is not a tracked editable copy.");
        var sources = resolve(manifest) ?? throw new NotSupportedException("The original source is not available in this version.");
        var generated = sources.Generate(manifest.OriginalType, manifest.ClassName, manifest.NamespacePrefix);
        manifest.Originals = new Dictionary<string, string>(generated.Manifest.Originals, StringComparer.Ordinal);
        WriteManifest(directory, manifest); // file hashes stay those of the earlier generation: the copy remains "edited"
    }

    /// <summary>Atomically swaps the copy directory. Without a backup the previous directory must still be untouched and is discarded.</summary>
    private static bool Replace(string root, string directory, GeneratedSourceCopy generated, SourceCopyManifest previous, bool requireUntouched, string? backup = null)
    {
        IndicatorSources.Preflight(root, generated.Sources, excludeDirectory: directory);
        string stage = IndicatorSources.Stage(root, generated, previous: null);
        string parked = backup ?? Path.Combine(Path.GetDirectoryName(root)!, ".indicator-copy-old-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (requireUntouched && !IsUntouched(directory, previous)) return false; // edited meanwhile: leave it
            Directory.Move(directory, parked);
            try { Directory.Move(stage, directory); }
            catch
            {
                Directory.Move(parked, directory);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }

        if (backup == null) Directory.Delete(parked, recursive: true);
        return true;
    }
}
