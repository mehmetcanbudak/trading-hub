using TradingHub.Common.Logging;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    /// <summary>Navigation only: opens the full local source/egress preview, never sends a model request.</summary>
    public Action<PineImportSource>? ReviewPineSource { get; set; }

    public OwnedOperationRegistry PineHandoffs { get; } = new();

    /// <summary>True shutdown only. Await while the UI dispatcher is alive; tray hiding must not call this.</summary>
    public Task ShutdownAuthoringAsync() => PineHandoffs.StopAsync();

    /// <summary>Last review of editable copies of built-in indicators (library badges and actions read it).</summary>
    public IReadOnlyList<SourceCopyStatus> SourceCopies { get; private set; } = Array.Empty<SourceCopyStatus>();

    public event Action? SourceCopiesChanged;

    /// <summary>The tracked copy whose compiled type is <paramref name="copyType"/>, if any.</summary>
    public SourceCopyStatus? SourceCopyFor(string copyType) => SourceCopies.FirstOrDefault(s => s.CopyType == copyType);

    /// <summary>One review at a time: an overlapping older review could otherwise publish its list after a newer one.</summary>
    private readonly SemaphoreSlim _sourceCopyReview = new(1, 1);

    /// <summary>
    /// Re-reads every tracked copy off the UI thread; untouched copies of updated originals are regenerated. Reviews run one
    /// after another, so the last published list reflects the folder after every earlier review and copy operation.
    /// </summary>
    public async Task<IReadOnlyList<SourceCopyStatus>> ReviewSourceCopiesAsync(string? folder = null)
    {
        string root = folder ?? AppPaths.UserIndicators;
        await _sourceCopyReview.WaitAsync();
        try
        {
            var result = await Task.Run(() => IndicatorSourceCopies.Review(root));
            SourceCopies = result;
            SourceCopiesChanged?.Invoke();
            return result;
        }
        finally { _sourceCopyReview.Release(); }
    }

    /// <summary>
    /// Start-up check after an update: untouched copies follow the new original (status line); edited copies whose
    /// original changed raise one in-app notice and a library badge. Nothing the user edited is changed.
    /// </summary>
    public async Task ReviewSourceCopiesAtStartupAsync(Action<string> status)
    {
        try
        {
            var result = await ReviewSourceCopiesAsync();
            var refreshed = result.Where(s => s.State == SourceCopyState.Refreshed).Select(s => s.Manifest.ClassName).ToList();
            var updated = result.Where(s => s.State == SourceCopyState.OriginalUpdated).Select(s => s.Manifest.ClassName).ToList();
            if (refreshed.Count > 0)
            {
                // a whole-directory swap is not a source-file event for every monitor: load the regenerated copies now
                Scripts?.CompileAll();
                status($"Unchanged indicator copies now follow their updated originals: {string.Join(", ", refreshed)}");
            }
            if (updated.Count > 0)
                Alerts.Raise(AlertKind.System, "", "Indicators",
                    $"The original of your edited indicator copy was updated: {string.Join(", ", updated)}. Your edits were kept. Right-click the copy in the indicator library to keep your edits or restore the new original.",
                    allowExternal: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            this.LogWarn("Could not check editable indicator copies: " + error.Message);
        }
    }
}
