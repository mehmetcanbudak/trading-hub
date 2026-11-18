using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>
/// The periodic live-state save (about every 3 s, on the UI thread) durably replaces the runners file only when
/// something other than the open P&amp;L marks changed: a runner holding a position, whose marks move with every price,
/// no longer costs a drive flush every period. The marks alone refresh the saved estimate at a slower interval.
/// </summary>
[Collection("LiveStore")]
public sealed class LiveStoreReviewRunnerSaveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tradinghub-runner-saves-").FullName;
    private readonly string _previousRoot = LiveStore.Root;
    private readonly TimeSpan _previousRefresh = LiveStore.RunnerMarkRefresh;

    public LiveStoreReviewRunnerSaveTests() => LiveStore.Root = _root;

    public void Dispose()
    {
        LiveStore.Root = _previousRoot;
        LiveStore.RunnerMarkRefresh = _previousRefresh;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string RunnersFile => Path.Combine(_root, "estrategias.json");

    [Fact]
    public void Open_pnl_marks_alone_do_not_rewrite_the_runners_file_but_position_and_realized_changes_do()
    {
        LiveStore.RunnerMarkRefresh = TimeSpan.FromHours(1);
        var recorder = new DurabilityRecorder();
        using (recorder.Install())
        {
            LiveStore.SaveRunners(new[] { Runner(position: 1, realized: 0, open: 12.5m) });
            Assert.Equal(1, Writes(recorder));

            // The next periods: only the marks moved with the price (also to an unknown price).
            LiveStore.SaveRunners(new[] { Runner(position: 1, realized: 0, open: 40m) });
            LiveStore.SaveRunners(new[] { Runner(position: 1, realized: 0, open: -7.25m, priceKnown: false) });
            Assert.Equal(1, Writes(recorder));
            Assert.Equal(12.5m, Saved().OpenPnL);

            LiveStore.SaveRunners(new[] { Runner(position: 2, realized: 0, open: -7.25m) });
            Assert.Equal(2, Writes(recorder));
            Assert.Equal(2m, Saved().Position);
            Assert.Equal(-7.25m, Saved().OpenPnL);

            LiveStore.SaveRunners(new[] { Runner(position: 2, realized: 31m, open: -7.25m) });
            Assert.Equal(3, Writes(recorder));
            Assert.Equal(31m, Saved().Realized);
        }
        recorder.AssertAtomicReplace(RunnersFile);
    }

    [Fact]
    public void Open_pnl_marks_alone_refresh_the_saved_estimate_after_the_refresh_interval()
    {
        LiveStore.RunnerMarkRefresh = TimeSpan.FromHours(1);
        var recorder = new DurabilityRecorder();
        using (recorder.Install())
        {
            LiveStore.SaveRunners(new[] { Runner(position: 1, realized: 0, open: 12.5m) });
            LiveStore.SaveRunners(new[] { Runner(position: 1, realized: 0, open: 40m) });
            Assert.Equal(1, Writes(recorder));

            // Zero stands for the interval having passed since the last write: the latest marks are written once.
            LiveStore.RunnerMarkRefresh = TimeSpan.Zero;
            LiveStore.SaveRunners(new[] { Runner(position: 1, realized: 0, open: 40m) });
            Assert.Equal(2, Writes(recorder));
            Assert.Equal(40m, Saved().OpenPnL);
            Assert.Equal(40m, Saved().AvailableOpenPnL);
            LiveStore.SaveRunners(new[] { Runner(position: 1, realized: 0, open: 40m) });
            Assert.Equal(2, Writes(recorder)); // unchanged marks are not rewritten
        }
    }

    private LiveRunnerInfo Saved() => Assert.Single(LiveStore.LoadRunners());

    private int Writes(DurabilityRecorder recorder) => recorder.Steps.Count(step =>
        step.StartsWith("rename:", StringComparison.Ordinal) && step.EndsWith("->" + DurabilityRecorder.Full(RunnersFile), StringComparison.Ordinal));

    private static LiveRunnerInfo Runner(decimal position, decimal realized, decimal open, bool priceKnown = true) => new()
    {
        Key = "review-runner|sim:ES|1m",
        Strategy = "Review strategy",
        FeedId = "sim",
        Symbol = "ES",
        Period = "1m",
        Account = "SIM",
        Running = true,
        StartedUtc = new DateTime(2026, 11, 18, 14, 30, 0, DateTimeKind.Utc),
        PointValue = 50,
        Position = position,
        Realized = realized,
        OpenPnL = open,
        AvailableOpenPnL = priceKnown ? open : null,
    };
}
