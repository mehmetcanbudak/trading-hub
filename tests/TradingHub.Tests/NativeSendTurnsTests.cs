using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed class NativeSendTurnsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Turns_go_in_take_order_and_wait_for_each_earlier_release()
    {
        var turns = new NativeSendTurns();
        var first = turns.Take();
        var second = turns.Take();
        var third = turns.Take();
        Assert.True(await first.WaitAsync(bounded: false, CancellationToken.None).WaitAsync(Deadline));

        var secondReady = second.WaitAsync(bounded: false, CancellationToken.None);
        var thirdReady = third.WaitAsync(bounded: true, CancellationToken.None);
        Assert.False(secondReady.IsCompleted);
        Assert.False(thirdReady.IsCompleted);
        Assert.Equal(2, turns.Waiting);

        first.Release();
        Assert.True(await secondReady.WaitAsync(Deadline));
        Assert.False(thirdReady.IsCompleted); // the second request has not been answered yet
        second.Release();
        second.Release(); // idempotent
        Assert.True(await thirdReady.WaitAsync(Deadline));
        Assert.Equal(0, turns.Waiting);
    }

    [Fact]
    public async Task A_bounded_wait_gives_up_but_a_later_turn_still_waits_for_the_unsettled_one()
    {
        var turns = new NativeSendTurns(TimeSpan.FromMilliseconds(50));
        var stuck = turns.Take();
        var close = turns.Take();
        var later = turns.Take();

        Assert.False(await close.WaitAsync(bounded: true, CancellationToken.None).WaitAsync(Deadline));
        close.Release(); // the close was sent anyway and answered
        var laterReady = later.WaitAsync(bounded: false, CancellationToken.None);
        Assert.False(laterReady.IsCompleted);
        stuck.Release();
        Assert.True(await laterReady.WaitAsync(Deadline));
    }

    [Fact]
    public async Task An_unbounded_wait_ends_with_its_cancellation()
    {
        var turns = new NativeSendTurns();
        _ = turns.Take();
        var next = turns.Take();
        using var cancellation = new CancellationTokenSource();
        var waiting = next.WaitAsync(bounded: false, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Deadline));
        Assert.Equal(0, turns.Waiting);
    }
}
