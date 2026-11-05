using TradingHub.App.Services;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class KlineKeeperTests
{
    private static readonly DateTime Now = new(2024, 3, 15, 12, 0, 0, DateTimeKind.Utc);
    private static Instrument Instrument(string symbol) => new() { FeedId = "fixture", Symbol = symbol, Exchange = "Fixture candles", TickSize = 0.25m };
    private static TrackedCandleInstrument Tracked(string symbol) => new("fixture-profile", "fixture-provider", Instrument(symbol));

    [Fact]
    public async Task DisabledMaintenanceDoesNotDiscoverFetchWriteOrTrackOpenedInstruments()
    {
        int requests = 0, saves = 0;
        await using var keeper = new KlineKeeper(new KlineMaintenanceSettings(), (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(1);
        }, _ => saves++, () => Now);
        keeper.Start();
        keeper.Track("fixture-profile", "fixture-provider", Instrument("OFF"));
        await keeper.RunNowAsync();
        Assert.Equal(0, requests);
        Assert.Equal(0, saves);
        Assert.Empty(keeper.Settings.Instruments);
        Assert.False(keeper.Status.IsRunning);
    }

    [Fact]
    public async Task DisableCancelsAndDrainsAHeldWriterAndRepeatedRunsNeverOverlap()
    {
        using var fixture = new OperationsStorageFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0, active = 0, maximumActive = 0;
        await using var keeper = new KlineKeeper(new KlineMaintenanceSettings { Enabled = true, Instruments = new() { Tracked("HELD") } },
            async (_, _, _, _, ct) =>
            {
                Interlocked.Increment(ref requests);
                maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
                using var registration = ct.Register(() => cancelled.TrySetResult());
                started.TrySetResult();
                try
                {
                    // A real owned fixture write deliberately holds cancellation to prove disable drains it.
                    await release.Task;
                    await File.AppendAllTextAsync(fixture.PathFor("completed-cache-month.fixture"), "completed before drain\n");
                    ct.ThrowIfCancellationRequested();
                    return 1;
                }
                finally { Interlocked.Decrement(ref active); }
            }, utcNow: () => Now);
        var first = keeper.RunNowAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await keeper.RunNowAsync();
        Assert.Equal(1, requests);
        var disabled = keeper.Settings with { Enabled = false };
        var disabling = keeper.ConfigureAsync(disabled);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disabling.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(first, disabling).WaitAsync(TimeSpan.FromSeconds(5));
        string committed = File.ReadAllText(fixture.PathFor("completed-cache-month.fixture"));
        Assert.Equal("completed before drain\n", committed);
        Assert.Equal(1, maximumActive);
        Assert.Equal(0, active);
        await keeper.RunNowAsync();
        keeper.Track("fixture-profile", "fixture-provider", Instrument("LATER"));
        Assert.Equal(1, requests);
        Assert.Equal(committed, File.ReadAllText(fixture.PathFor("completed-cache-month.fixture")));
    }

    [Fact]
    public async Task DisposalWaitsForAnActiveMonthlyWriterInsteadOfLeavingDetachedCacheWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int finished = 0;
        var keeper = new KlineKeeper(new KlineMaintenanceSettings { Enabled = true, Instruments = new() { Tracked("QUIT") } },
            async (_, _, _, _, ct) =>
            {
                using var registration = ct.Register(() => cancelled.TrySetResult());
                started.TrySetResult();
                await release.Task;
                Interlocked.Increment(ref finished);
                ct.ThrowIfCancellationRequested();
                return 0;
            }, utcNow: () => Now);
        var running = keeper.RunNowAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = keeper.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disposing.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(running, disposing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, finished);
        Assert.False(keeper.Status.IsRunning);
        await keeper.DisposeAsync();
    }

    [Fact]
    public async Task BatchesAreBoundedAndUseRealCalendarMonthsIncludingLeapFebruary()
    {
        var calls = new List<(string Symbol, DateTime From, DateTime To, int Minutes)>();
        await using var keeper = new KlineKeeper(new KlineMaintenanceSettings
        {
            Enabled = true,
            Instruments = Enumerable.Range(0, 20).Select(i => Tracked("BOUNDED-" + i)).ToList(),
        }, (item, from, to, minutes, _) =>
        {
            calls.Add((item.Instrument.Symbol, from, to, minutes));
            return Task.FromResult(1);
        }, utcNow: () => Now);
        await keeper.RunNowAsync();
        Assert.Equal(KlineKeeper.MaxMonthlyLoadsPerCycle, calls.Count);
        Assert.Contains(calls, call => call.From == new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) &&
            call.To == new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc) && (call.To - call.From).TotalDays == 29);
        Assert.All(calls, call =>
        {
            Assert.Equal(DateTimeKind.Utc, call.From.Kind);
            Assert.Equal(1, call.From.Day);
            Assert.True(call.To <= Now);
            Assert.InRange((call.To - call.From).TotalDays, 0, 31);
            Assert.Contains(call.Minutes, new[] { 1, 5, 15, 60 });
        });
        var firstSymbols = calls.Select(c => c.Symbol).ToHashSet();
        calls.Clear();
        await keeper.RunNowAsync();
        Assert.Contains(calls, call => !firstSymbols.Contains(call.Symbol));
    }

    [Fact]
    public async Task ALaterSuccessfulResolutionDoesNotHideAnEarlierProviderFailure()
    {
        int requests = 0;
        await using var keeper = new KlineKeeper(new KlineMaintenanceSettings
        {
            Enabled = true, Instruments = new() { Tracked("PARTIAL") },
        }, (_, _, _, _, _) =>
        {
            if (++requests == 1) throw new IOException("fixture missing minute candles");
            return Task.FromResult(12);
        }, utcNow: () => Now);
        await keeper.RunNowAsync();
        Assert.True(requests > 1);
        var failure = Assert.Single(keeper.Status.Failures);
        Assert.Equal(Tracked("PARTIAL").Key, failure.InstrumentKey);
        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), failure.MonthUtc);
        Assert.Equal(1, failure.Minutes);
        await keeper.RunNowAsync();
        Assert.Empty(keeper.Status.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOlderSlotRemainsVisibleUntilItsOwnSuccessfulRetryBeforeLaterCatchUp(bool monthChanges)
    {
        var failedMonth = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = Now;
        int cycle = 1;
        bool failedOnce = false;
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<(int Cycle, DateTime From, DateTime To, int Minutes)>();
        await using var keeper = new KlineKeeper(new KlineMaintenanceSettings
        {
            Enabled = true, Instruments = new() { Tracked("OLDER-GAP") },
        }, async (_, from, to, minutes, ct) =>
        {
            calls.Add((cycle, from, to, minutes));
            if (from == failedMonth && minutes == 1)
            {
                if (!failedOnce)
                {
                    failedOnce = true;
                    throw new IOException("fixture older-month interruption");
                }
                retryStarted.TrySetResult();
                await allowRetry.Task.WaitAsync(ct);
            }
            return 12;
        }, utcNow: () => now);
        await keeper.RunNowAsync();
        var failure = Assert.Single(keeper.Status.Failures);
        Assert.Equal(Tracked("OLDER-GAP").Key, failure.InstrumentKey);
        Assert.Equal(failedMonth, failure.MonthUtc);
        Assert.Equal(1, failure.Minutes);
        Assert.Contains(calls, c => c.Cycle == 1 && c.From == new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc) && c.To == Now);

        cycle = 2;
        if (monthChanges) now = new DateTime(2024, 4, 2, 12, 0, 0, DateTimeKind.Utc);
        var second = keeper.RunNowAsync();
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(second.IsCompleted);
        var pending = Assert.Single(keeper.Status.Failures);
        Assert.Equal(failedMonth, pending.MonthUtc);
        Assert.Equal(1, pending.Minutes);
        Assert.DoesNotContain(calls, c => c.Cycle == 2 && c.From < failedMonth);
        allowRetry.TrySetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(keeper.Status.Failures);
        Assert.Equal(2, calls.Count(c => c.From == failedMonth && c.Minutes == 1));
        var currentMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Contains(calls, c => c.Cycle == 2 && c.From == currentMonth && c.To == now);
        var catchUpMonth = monthChanges ? new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc) : failedMonth;
        int catchUpMinutes = monthChanges ? 1 : 5;
        Assert.Contains(calls, c => c.Cycle == 2 && c.From == catchUpMonth && c.Minutes == catchUpMinutes);
    }

    [Fact]
    public async Task ExplicitSelectionCanTrackWhileAutomaticOpenedInstrumentTrackingIsOff()
    {
        await using var keeper = new KlineKeeper(new KlineMaintenanceSettings { Enabled = true, AutoTrackOpened = false, MaxInstruments = 2 },
            (_, _, _, _, _) => Task.FromResult(0), utcNow: () => Now);
        keeper.Track("fixture-profile", "fixture-provider", Instrument("OPENED"));
        Assert.Empty(keeper.Settings.Instruments);
        keeper.Track("fixture-profile", "fixture-provider", Instrument("SELECTED"), explicitlySelected: true);
        keeper.Track("fixture-profile", "fixture-provider", Instrument("SELECTED"), explicitlySelected: true);
        Assert.Equal("SELECTED", Assert.Single(keeper.Settings.Instruments).Instrument.Symbol);
        keeper.Track("fixture-profile", "fixture-provider", Instrument("SECOND"), explicitlySelected: true);
        keeper.Track("fixture-profile", "fixture-provider", Instrument("THIRD"), explicitlySelected: true);
        Assert.Equal(2, keeper.Settings.Instruments.Count);
    }
}
