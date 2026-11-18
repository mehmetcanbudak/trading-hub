using TradingHub.Common;

namespace TradingHub.Tests;

/// <summary>The Windows write-through rename tolerates another process briefly holding the source or destination
/// without FILE_SHARE_DELETE (antivirus, indexer, backup). The native mover and the pause between attempts are injected,
/// so these tests count attempts and requested pauses instead of waiting.</summary>
public sealed class DurableReviewWindowsRenameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tradinghub-durable-review-rename-").FullName;

    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(_root)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    /// <summary>A scripted native mover: each attempt returns the next scripted Win32 code, then the last one forever.</summary>
    private sealed class ScriptedMove(params int[] results)
    {
        public int Attempts { get; private set; }
        public List<TimeSpan> Pauses { get; } = new();
        public int Move(string source, string destination, int flags) => results[Math.Min(Attempts++, results.Length - 1)];
        public void Pause(TimeSpan delay) => Pauses.Add(delay);
    }

    /// <summary>Routes durable replacements through the Windows branch with the scripted mover.</summary>
    private sealed class WindowsOperations(ScriptedMove move) : DurabilityOperations
    {
        public override void Rename(string source, string destination, bool overwrite) =>
            StorageSync.Rename(source, destination, overwrite, windows: true, move.Move, move.Pause);
    }

    [Theory]
    [InlineData(5)]  // ERROR_ACCESS_DENIED: the replaced file is still open for deletion elsewhere
    [InlineData(32)] // ERROR_SHARING_VIOLATION
    [InlineData(33)] // ERROR_LOCK_VIOLATION
    public void A_transient_sharing_error_is_retried_and_the_rename_then_succeeds(int transient)
    {
        File.WriteAllText(PathFor("state.json"), "previous");
        var move = new ScriptedMove(transient, 0);
        StorageSync.Rename(PathFor("state.json.tmp"), PathFor("state.json"), overwrite: true, windows: true, move.Move, move.Pause);
        Assert.Equal(2, move.Attempts);
        Assert.Equal(new[] { StorageSync.WindowsRetryDelays[0] }, move.Pauses);
    }

    [Fact]
    public void A_persistent_access_denial_throws_after_the_bound_and_discards_the_temporary()
    {
        string target = PathFor("state.json");
        File.WriteAllText(target, "previous");
        var move = new ScriptedMove(5);
        using (DurableFile.Use(new WindowsOperations(move)))
            Assert.Throws<UnauthorizedAccessException>(() => DurableFile.ReplaceAtomically(target, "next"));

        Assert.Equal(StorageSync.WindowsRetryDelays.Count + 1, move.Attempts);
        Assert.Equal(StorageSync.WindowsRetryDelays, move.Pauses);
        var total = move.Pauses.Aggregate(TimeSpan.Zero, (sum, pause) => sum + pause);
        Assert.InRange(total, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));
        for (int i = 1; i < move.Pauses.Count; i++) Assert.True(move.Pauses[i] > move.Pauses[i - 1], "The back-off must grow.");
        Assert.Equal("previous", File.ReadAllText(target));
        Assert.Equal(new[] { target }, Directory.GetFiles(_root)); // the unique temporary was discarded
    }

    [Theory]
    [InlineData(2)]   // ERROR_FILE_NOT_FOUND
    [InlineData(3)]   // ERROR_PATH_NOT_FOUND
    [InlineData(112)] // ERROR_DISK_FULL
    [InlineData(183)] // ERROR_ALREADY_EXISTS
    public void A_non_transient_error_is_not_retried(int error)
    {
        var move = new ScriptedMove(error, 0);
        var thrown = Assert.ThrowsAny<Exception>(() =>
            StorageSync.Rename(PathFor("state.json.tmp"), PathFor("state.json"), overwrite: false, windows: true, move.Move, move.Pause));
        Assert.Contains($"OS error {error}", thrown.Message);
        Assert.Equal(1, move.Attempts);
        Assert.Empty(move.Pauses);
    }

    [Fact]
    public void A_read_only_destination_is_refused_without_retry_on_both_branches()
    {
        string source = PathFor("state.json.tmp"), destination = PathFor("state.json");
        File.WriteAllText(source, "next");
        File.WriteAllText(destination, "previous");
        File.SetAttributes(destination, FileAttributes.ReadOnly);

        // Windows reports a read-only destination as access denied too; waiting cannot clear it.
        var windows = new ScriptedMove(5, 0);
        Assert.Throws<UnauthorizedAccessException>(() => StorageSync.Rename(source, destination, overwrite: true, windows: true, windows.Move, windows.Pause));
        Assert.Equal(1, windows.Attempts);
        Assert.Empty(windows.Pauses);

        // POSIX refuses before renaming and never consults the Windows mover or the retry pause.
        var posix = new ScriptedMove(0);
        Assert.Throws<UnauthorizedAccessException>(() => StorageSync.Rename(source, destination, overwrite: true, windows: false, posix.Move, posix.Pause));
        Assert.Equal(0, posix.Attempts);
        Assert.Empty(posix.Pauses);
        Assert.Equal("previous", File.ReadAllText(destination));
        Assert.Equal("next", File.ReadAllText(source));
    }
}
