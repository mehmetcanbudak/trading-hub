using System.Text;

namespace TradingHub.App.Services;

/// <summary>File operations never rename C# types or saved indicator identities, and never overwrite a collision.</summary>
public sealed class IndicatorSourceFiles(string folder)
{
    public string Folder { get; } = Path.GetFullPath(folder);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public IReadOnlyDictionary<string, string> ReadAll() => Paths().ToDictionary(p => p, File.ReadAllText, StringComparer.Ordinal);

    public IReadOnlyList<string> Paths()
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
                else if (entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) result.Add(entry);
            }
        }
    }

    public string Read(string path) => File.ReadAllText(Owned(path));

    public string Create(string fileName, string text)
    {
        string name = FileName(fileName);
        Directory.CreateDirectory(Folder);
        EnsureNoCollision(Folder, name);
        string path = Path.Combine(Folder, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
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
        string name = FileName(fileName);
        string directory = Path.GetDirectoryName(path)!;
        string destination = Path.Combine(directory, name);
        if (string.Equals(path, destination, StringComparison.Ordinal)) return path;
        EnsureNoCollision(directory, name);
        Unchanged(path, expectedText);
        File.Move(path, destination); // preserves the complete source, including all type and persisted series identities
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
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Folder) + Path.DirectorySeparatorChar, PathComparison) || !full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a C# source file inside the configured indicator directory.", nameof(path));
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

    private static string FileName(string value)
    {
        string name = value.Trim();
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) name += ".cs";
        if (name.Length <= 3 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.StartsWith('.') || name.EndsWith(' ') ||
            Path.GetFileNameWithoutExtension(name).EndsWith('.'))
            throw new ArgumentException("Choose a filename without folders or reserved path characters.", nameof(value));
        return name;
    }

    private static void EnsureNoCollision(string directory, string name)
    {
        if (Directory.EnumerateFileSystemEntries(directory).Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A file or folder with that name already exists. Nothing was overwritten.");
    }
    private static void Unchanged(string path, string expectedText)
    {
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path), expectedText, StringComparison.Ordinal))
            throw new IOException("This source changed on disk. Reopen it before saving, renaming or deleting; the newer file was preserved.");
    }
}
