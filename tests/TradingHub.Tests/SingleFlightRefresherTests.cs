using TradingHub.App.Services.Workbench;

namespace TradingHub.Tests;

/// <summary>Background refresh scheduling for Workbench panels, driven step by step through manual run and post queues.</summary>
public sealed class SingleFlightRefresherTests
{
    private sealed class Manual
    {
        public readonly Queue<Action> Background = new();
        public readonly Queue<Action> Owner = new();
        public SingleFlightRefresher<int> Refresher { get; }
        public Manual() => Refresher = new SingleFlightRefresher<int>(Background.Enqueue, Owner.Enqueue);

        /// <summary>Runs the next background computation and delivers its completion to the owner.</summary>
        public void Step()
        {
            Background.Dequeue()();
            Owner.Dequeue()();
        }
    }

    [Fact]
    public void Requests_during_a_running_compute_are_coalesced_and_never_overlap()
    {
        var m = new Manual();
        var applied = new List<int>();
        int running = 0, maxRunning = 0;
        Func<CancellationToken, int> Compute(int value) => _ =>
        {
            maxRunning = Math.Max(maxRunning, ++running);
            running--;
            return value;
        };

        m.Refresher.Request(Compute(1), applied.Add);
        Assert.True(m.Refresher.IsRunning);
        m.Refresher.Request(Compute(2), applied.Add);
        m.Refresher.Request(Compute(3), applied.Add);
        // Only the first computation was started; the second and third wait as one coalesced request.
        Assert.Single(m.Background);
        Assert.True(m.Refresher.HasPending);

        m.Step();
        Assert.Equal(new[] { 1 }, applied);
        // Finishing the first starts only the latest waiting request.
        Assert.Single(m.Background);
        Assert.False(m.Refresher.HasPending);
        m.Step();
        Assert.Equal(new[] { 1, 3 }, applied);
        Assert.False(m.Refresher.IsRunning);
        Assert.Empty(m.Background);
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public void Cancel_discards_the_running_result_drops_the_waiting_request_and_waits_before_starting_again()
    {
        var m = new Manual();
        var applied = new List<int>();
        CancellationToken observed = default;
        m.Refresher.Request(ct => { observed = ct; return 1; }, applied.Add);
        m.Refresher.Request(_ => 2, applied.Add);

        m.Refresher.Cancel();
        Assert.False(m.Refresher.HasPending);
        // A request after cancelling waits for the cancelled computation instead of overlapping it.
        m.Refresher.Request(_ => 4, applied.Add);
        Assert.Single(m.Background);

        m.Step();
        Assert.True(observed.IsCancellationRequested);
        Assert.Empty(applied);
        m.Step();
        Assert.Equal(new[] { 4 }, applied);
    }

    [Fact]
    public void A_compute_that_observes_cancellation_reports_nothing_and_errors_reach_the_owner()
    {
        var m = new Manual();
        var applied = new List<int>();
        var errors = new List<Exception>();
        m.Refresher.Request(ct => { m.Refresher.Cancel(); ct.ThrowIfCancellationRequested(); return 1; }, applied.Add, errors.Add);
        // The compute runs in the background; cancellation is requested from the owner before it delivers.
        m.Background.Dequeue()();
        m.Owner.Dequeue()();
        Assert.Empty(applied);
        Assert.Empty(errors);

        m.Refresher.Request(_ => throw new InvalidDataException("rows changed"), applied.Add, errors.Add);
        m.Step();
        Assert.Equal("rows changed", Assert.Single(errors).Message);
        Assert.Empty(applied);
        Assert.False(m.Refresher.IsRunning);
    }

    [Fact]
    public void Dispose_cancels_and_ignores_later_requests()
    {
        var m = new Manual();
        var applied = new List<int>();
        m.Refresher.Request(_ => 1, applied.Add);
        m.Refresher.Request(_ => 2, applied.Add);
        m.Refresher.Dispose();
        m.Step();
        Assert.Empty(applied);
        Assert.Empty(m.Background); // The waiting request was dropped, not started.
        m.Refresher.Request(_ => 3, applied.Add);
        Assert.Empty(m.Background);
        Assert.False(m.Refresher.IsRunning);
    }
}
