using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public class SessionVolumeProfileTests
{
    [Fact]
    public void Replaying_a_growing_visible_profile_does_not_allocate_a_full_snapshot_for_every_bar()
    {
        var instrument = new Instrument
        {
            Symbol = "PROFILE-REPLAY",
            FeedId = "test",
            TickSize = 1,
            VolumeStep = 1,
        };
        var data = new InstrumentData(instrument, new SimulatedFeed());
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int bar = 0; bar < 1024; bar++)
            data.Trades.Add(new Trade(bar + 1, start.AddMinutes(bar), 1000 + bar, 1, TradeSide.Buy));

        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var profile = new SessionVolumeProfile { Period = SessionVolumeProfile.ProfilePeriod.Visible };
        long before = GC.GetAllocatedBytesForCurrentThread();
        var slot = model.AddIndicator(profile, applyDefaultColors: false);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(slot.Error);
        // A replay must not clone and sort every growing intermediate profile on the UI thread.
        // The budget allows substantial runtime overhead but rejects the quadratic snapshot path.
        Assert.InRange(allocated, 0L, 16L * 1024 * 1024);
    }
}
