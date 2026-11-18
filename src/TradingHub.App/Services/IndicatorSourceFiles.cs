using System.Text;
using TradingHub.Scripting;

namespace TradingHub.App.Services;

/// <summary>
/// Handles C# sources and KVS scripts and never overwrites a collision. C# file operations never rename types or saved
/// indicator identities; a KVS script's type is <c>Kvs_</c> + its file name, so renaming a script renames its type.
/// </summary>
public sealed class IndicatorSourceFiles(string folder)
{
    public string Folder { get; } = Path.GetFullPath(folder);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public IReadOnlyDictionary<string, string> ReadAll() => Paths().ToDictionary(p => p, File.ReadAllText, StringComparer.Ordinal);

    /// <summary>C# sources only (the input of semantic source indexes and copy preflights).</summary>
    public IReadOnlyList<string> Paths() => Collect(scripts: false);

    /// <summary>Everything the editor lists and the compiler builds: C# sources and <c>.kvs</c> scripts.</summary>
    public IReadOnlyList<string> EditablePaths() => Collect(scripts: true);

    private IReadOnlyList<string> Collect(bool scripts)
    {
        if (!Directory.Exists(Folder)) return Array.Empty<string>();
        var result = new List<string>();
        Walk(Folder);
        return result.Order(StringComparer.Ordinal).ToArray();
        void Walk(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(entry) is not ("bin" or "obj")) Walk(entry);
                }
                else if (entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || scripts && KvsTranslator.IsScript(entry)) result.Add(entry);
            }
        }
    }

    public string Read(string path) => File.ReadAllText(Owned(path));

    /// <summary>
    /// Publishes the complete file in one rename (never overwriting), so the compiler's monitor cannot capture an empty or
    /// half-written source between creation and write.
    /// </summary>
    public string Create(string fileName, string text)
    {
        string name = FileName(fileName);
        Directory.CreateDirectory(Folder);
        EnsureNoCollision(Folder, name);
        EnsureNoScriptTypeCollision(name, except: null);
        string path = Path.Combine(Folder, name);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: false); // a concurrent collision fails safely
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    /// <summary>Rejects externally edited/deleted files instead of silently replacing the user's newer source.</summary>
    public void Save(string path, string expectedText, string text)
    {
        path = Owned(path);
        Unchanged(path, expectedText);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            Unchanged(path, expectedText);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string Rename(string path, string fileName, string expectedText)
    {
        path = Owned(path);
        string name = FileName(fileName, Path.GetExtension(path));
        string directory = Path.GetDirectoryName(path)!;
        string destination = Path.Combine(directory, name);
        if (string.Equals(path, destination, StringComparison.Ordinal)) return path;
        EnsureNoCollision(directory, name, except: path);
        EnsureNoScriptTypeCollision(name, except: path);
        Unchanged(path, expectedText);
        if (string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
        {
            // case only: a case-insensitive volume sees the destination as this very file, so move through a unique name
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.Move(path, temporary);
            try { File.Move(temporary, destination); }
            catch
            {
                File.Move(temporary, path);
                throw;
            }
        }
        else File.Move(path, destination); // preserves the complete source, including all type and persisted series identities
        return destination;
    }

    public void Delete(string path, string expectedText)
    {
        path = Owned(path);
        Unchanged(path, expectedText);
        File.Delete(path);
    }

    private string Owned(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Folder) + Path.DirectorySeparatorChar, PathComparison) ||
            !full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !KvsTranslator.IsScript(full))
            throw new ArgumentException("Select a C# source or KVS script inside the configured indicator directory.", nameof(path));
        string relative = Path.GetRelativePath(Folder, full);
        string current = Folder;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Editing linked source files is not supported; open the original file separately.");
        }
        return full;
    }

    /// <summary>A bare name receives <paramref name="extension"/>; .cs and .kvs names keep theirs, but a rename never switches language.</summary>
    private static string FileName(string value, string? extension = null)
    {
        string name = value.Trim();
        bool cs = name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase), kvs = KvsTranslator.IsScript(name);
        if (extension != null && (cs || kvs) && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Keep the file extension; a C# source and a KVS script cannot be converted by renaming.", nameof(value));
        if (!cs && !kvs) name += extension ?? ".cs";
        string ext = Path.GetExtension(name);
        if (name.Length <= ext.Length || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.StartsWith('.') || name.EndsWith(' ') ||
            Path.GetFileNameWithoutExtension(name).EndsWith('.'))
            throw new ArgumentException("Choose a filename without folders or reserved path characters.", nameof(value));
        return name;
    }

    /// <summary>Entries named <paramref name="name"/> in any letter case, other than <paramref name="except"/> itself.</summary>
    private static void EnsureNoCollision(string directory, string name, string? except = null)
    {
        if (Directory.EnumerateFileSystemEntries(directory).Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(p, except, StringComparison.Ordinal)))
            throw new IOException("A file or folder with that name already exists. Nothing was overwritten.");
    }

    /// <summary>Two KVS scripts compiling to the same type (x.kvs in two folders, my-ind.kvs and my_ind.kvs) would break every custom indicator.</summary>
    private void EnsureNoScriptTypeCollision(string name, string? except)
    {
        if (!KvsTranslator.IsScript(name)) return;
        string type = KvsTranslator.ClassNameFor(Path.GetFileNameWithoutExtension(name));
        if (EditablePaths().Any(p => KvsTranslator.IsScript(p) && !string.Equals(p, except, StringComparison.Ordinal) &&
                KvsTranslator.ClassNameFor(Path.GetFileNameWithoutExtension(p)) == type))
            throw new IOException("Another KVS script already compiles to that indicator type. Choose another name; nothing was changed.");
    }

    private static void Unchanged(string path, string expectedText)
    {
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path), expectedText, StringComparison.Ordinal))
            throw new IOException("This source changed on disk. Reopen it before saving, renaming or deleting; the newer file was preserved.");
    }
}
