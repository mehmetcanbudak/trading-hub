using System.Text;

namespace TradingHub.Common;

/// <summary>The storage steps whose order makes a state file power-loss durable. The default forwards to the OS
/// (<see cref="StorageSync"/> and <see cref="File.Move(string, string, bool)"/>); tests substitute an observer to prove
/// the order of operations or to stop at a simulated power cut.</summary>
public class DurabilityOperations
{
    internal static DurabilityOperations System { get; } = new();

    /// <summary>Hands buffered bytes to the OS and asks the drive to persist them (Darwin F_FULLFSYNC, FlushFileBuffers
    /// on Windows, fsync elsewhere). Other streams use their own flush contract.</summary>
    public virtual void Flush(Stream stream) => StorageSync.Flush(stream);

    /// <summary>Renames within one directory. Durability of the new name requires a later <see cref="SyncDirectory"/>.</summary>
    public virtual void Rename(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);

    /// <summary>Persists a directory's names on macOS/Linux; a no-op where the platform has no directory handle sync.</summary>
    public virtual void SyncDirectory(string directory) => StorageSync.SyncDirectory(directory);
}

/// <summary>
/// The one convention for safety-critical state files (order ownership, account/ledger journals, broker sessions,
/// connections, alerts, settings and workspace). Whole-file state is written to a unique same-directory temporary,
/// flushed to the drive, atomically renamed over the target and then its parent directory is synchronized. Append
/// journals flush after each committed record and synchronize the parent once a new name appears. Every failure
/// propagates; a crash leaves either the previous or the complete new file, plus an ignorable "*.tmp" leftover.
/// Successful OS calls are not a hardware/firmware guarantee.
/// </summary>
public static class DurableFile
{
    private static readonly AsyncLocal<DurabilityOperations?> Scoped = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    internal static DurabilityOperations Operations => Scoped.Value ?? DurabilityOperations.System;

    /// <summary>Routes this asynchronous flow's durable writes through <paramref name="operations"/> until disposed.
    /// Scoped to the calling flow, so concurrent tests and other threads keep the OS behavior.</summary>
    internal static IDisposable Use(DurabilityOperations operations)
    {
        var previous = Scoped.Value;
        Scoped.Value = operations;
        return new Restore(previous);
    }

    /// <summary>Atomically and durably replaces <paramref name="path"/> with the bytes <paramref name="write"/> produces.</summary>
    public static void ReplaceAtomically(string path, Action<Stream> write)
    {
        var operations = Operations;
        string target = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(target)!;
        string temporary = TemporaryFor(target);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(file);
                operations.Flush(file);
            }
            operations.Rename(temporary, target, overwrite: true);
        }
        catch
        {
            DiscardTemporary(temporary);
            throw;
        }
        operations.SyncDirectory(directory);
    }

    /// <summary>Atomically and durably replaces <paramref name="path"/> with UTF-8 text (no byte-order mark).</summary>
    public static void ReplaceAtomically(string path, string text) =>
        ReplaceAtomically(path, stream =>
        {
            byte[] bytes = Utf8.GetBytes(text);
            stream.Write(bytes);
        });

    /// <summary>Asynchronous serialization variant; cancellation is honored before the rename publishes anything.</summary>
    public static async Task ReplaceAtomicallyAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        var operations = Operations;
        string target = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(target)!;
        string temporary = TemporaryFor(target);
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await write(file, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                operations.Flush(file);
            }
            ct.ThrowIfCancellationRequested();
            operations.Rename(temporary, target, overwrite: true);
        }
        catch
        {
            DiscardTemporary(temporary);
            throw;
        }
        operations.SyncDirectory(directory);
    }

    /// <summary>Persists an appended or rewritten record of an open file. A newly created name additionally needs
    /// <see cref="SyncDirectory"/> of its parent before the record may be acknowledged.</summary>
    public static void Flush(Stream stream) => Operations.Flush(stream);

    /// <summary>Same-directory rename step for writers that own a fixed temporary name or publish without overwrite.
    /// Callers follow it with <see cref="SyncDirectory"/>.</summary>
    public static void Rename(string source, string destination, bool overwrite) => Operations.Rename(source, destination, overwrite);

    /// <summary>Creates any missing directory levels, persisting each new name in its parent. Existing levels cost one
    /// existence check and no synchronization.</summary>
    public static void EnsureDirectory(string directory)
    {
        var missing = new Stack<string>();
        string? current = Path.GetFullPath(directory);
        while (current != null && !Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current);
        }
        var operations = Operations;
        while (missing.Count > 0)
        {
            string created = missing.Pop();
            Directory.CreateDirectory(created);
            if (Path.GetDirectoryName(created) is { } parent) operations.SyncDirectory(parent);
        }
    }

    /// <summary>Persists a directory's entries (creations, renames and deletions) on macOS/Linux.</summary>
    public static void SyncDirectory(string directory) => Operations.SyncDirectory(directory);

    /// <summary>Deletes a file and persists the removal of its name.</summary>
    public static void Delete(string path)
    {
        File.Delete(path);
        Operations.SyncDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    private static string TemporaryFor(string target) => target + "." + Guid.NewGuid().ToString("N") + ".tmp";

    // The original failure remains the visible error; an unremovable leftover is an ignorable unique temporary.
    private static void DiscardTemporary(string temporary)
    {
        try { File.Delete(temporary); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Restore(DurabilityOperations? previous) : IDisposable
    {
        public void Dispose() => Scoped.Value = previous;
    }
}
