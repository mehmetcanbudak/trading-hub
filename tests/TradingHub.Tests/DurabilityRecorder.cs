using TradingHub.Common;

namespace TradingHub.Tests;

/// <summary>Observes the real durable storage steps of one test flow (through <see cref="DurableFile.Use"/>), injects a
/// failure before a chosen step, and models a power cut: renames not yet followed by a directory synchronization are
/// undone, restoring the previous target bytes and leaving the complete temporary beside it.</summary>
internal sealed class DurabilityRecorder : DurabilityOperations
{
    private readonly object _gate = new();
    private readonly List<string> _steps = new();
    private readonly List<(string Directory, string Source, string Destination, byte[]? Previous)> _unsynced = new();

    /// <summary>Throws a simulated I/O failure instead of performing a matching step ("flush:", "rename:", "sync:").</summary>
    public Predicate<string>? FailWhen { get; set; }

    public IReadOnlyList<string> Steps { get { lock (_gate) return _steps.ToArray(); } }

    public IDisposable Install() => DurableFile.Use(this);

    public override void Flush(Stream stream)
    {
        string step = "flush:" + ((stream as FileStream)?.Name is { } name ? Full(name) : "<stream>");
        Check(step);
        base.Flush(stream);
        Record(step);
    }

    public override void Rename(string source, string destination, bool overwrite)
    {
        string from = Full(source), to = Full(destination);
        string step = $"rename:{from}->{to}";
        Check(step);
        byte[]? previous = File.Exists(to) ? File.ReadAllBytes(to) : null;
        base.Rename(source, destination, overwrite);
        lock (_gate)
        {
            _steps.Add(step);
            _unsynced.Add((Path.GetDirectoryName(to)!, from, to, previous));
        }
    }

    public override void SyncDirectory(string directory)
    {
        string full = Full(directory);
        string step = "sync:" + full;
        Check(step);
        base.SyncDirectory(directory);
        lock (_gate)
        {
            _steps.Add(step);
            _unsynced.RemoveAll(rename => rename.Directory == full);
        }
    }

    /// <summary>Undoes every rename whose directory was not synchronized afterwards, newest first.</summary>
    public void LoseUnsyncedRenames()
    {
        lock (_gate)
        {
            for (int i = _unsynced.Count - 1; i >= 0; i--)
            {
                var (_, source, destination, previous) = _unsynced[i];
                File.Move(destination, source);
                if (previous != null) File.WriteAllBytes(destination, previous);
            }
            _unsynced.Clear();
        }
    }

    /// <summary>Asserts flush(temporary) → rename(temporary → target) → sync(target directory) for the latest
    /// replacement of <paramref name="target"/>, with a unique same-directory temporary.</summary>
    public void AssertAtomicReplace(string target)
    {
        string full = Full(target);
        var steps = Steps;
        int rename = LastIndex(steps, step => step.StartsWith("rename:" + full + ".", StringComparison.Ordinal) &&
            step.EndsWith(".tmp->" + full, StringComparison.Ordinal));
        Assert.True(rename >= 0, $"No temporary rename onto {full}. Steps:\n{string.Join('\n', steps)}");
        string temporary = steps[rename]["rename:".Length..steps[rename].IndexOf("->", StringComparison.Ordinal)];
        Assert.Equal(Path.GetDirectoryName(full), Path.GetDirectoryName(temporary));
        AssertOrder(steps, "flush:" + temporary, steps[rename], "sync:" + Path.GetDirectoryName(full));
    }

    /// <summary>Asserts that every rename of a "*.tmp" source was preceded by that source's flush and followed by a
    /// synchronization of the target directory, and that at least one such publication happened.</summary>
    public void AssertEveryTemporaryRenameIsDurable()
    {
        var steps = Steps;
        int published = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            if (!steps[i].StartsWith("rename:", StringComparison.Ordinal)) continue;
            int arrow = steps[i].IndexOf("->", StringComparison.Ordinal);
            string source = steps[i]["rename:".Length..arrow], destination = steps[i][(arrow + 2)..];
            if (!source.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            published++;
            int flush = -1, sync = -1;
            for (int j = i - 1; j >= 0 && flush < 0; j--) if (steps[j] == "flush:" + source) flush = j;
            for (int j = i + 1; j < steps.Count && sync < 0; j++) if (steps[j] == "sync:" + Path.GetDirectoryName(destination)) sync = j;
            Assert.True(flush >= 0, $"{steps[i]} was not preceded by its flush. Steps:\n{string.Join('\n', steps)}");
            Assert.True(sync >= 0, $"{steps[i]} was not followed by a directory sync. Steps:\n{string.Join('\n', steps)}");
        }
        Assert.True(published > 0, "No temporary publication was observed.");
    }

    /// <summary>Asserts that the given steps occur in order (each after the previous occurrence).</summary>
    public void AssertOrder(params string[] expected) => AssertOrder(Steps, expected);

    private static void AssertOrder(IReadOnlyList<string> steps, params string[] expected)
    {
        int position = -1;
        foreach (string step in expected)
        {
            int found = -1;
            for (int i = position + 1; i < steps.Count; i++)
                if (steps[i] == step) { found = i; break; }
            Assert.True(found > position, $"Expected '{step}' after step {position}. Steps:\n{string.Join('\n', steps)}");
            position = found;
        }
    }

    private static int LastIndex(IReadOnlyList<string> steps, Func<string, bool> match)
    {
        for (int i = steps.Count - 1; i >= 0; i--) if (match(steps[i])) return i;
        return -1;
    }

    private void Check(string step)
    {
        if (FailWhen?.Invoke(step) == true) throw new IOException("Simulated storage failure before " + step);
    }

    private void Record(string step)
    {
        lock (_gate) _steps.Add(step);
    }

    public static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
