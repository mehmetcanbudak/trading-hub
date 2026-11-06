using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class ChartDataWarningTests
{
    [Fact]
    public async Task Limited_history_retains_a_corner_disclosure_without_obscuring_the_price_pane_center()
    {
        using var feed = new LimitedHistoryFeed();
        using var data = new InstrumentData(feed.Instrument, feed) { RawTradeLimit = 8 };
        data.Start(feed.Start);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (data.IsLoadingHistory && DateTime.UtcNow < deadline)
        {
            data.Pump();
            await Task.Delay(5);
        }
        data.Pump();
        Assert.False(data.IsLoadingHistory);
        Assert.NotNull(data.HistoryWarning);
        using var model = new ChartModel(data, ChartPeriod.M1);
        using var surface = SKSurface.Create(new SKImageInfo(1000, 600));
        var state = new ChartRenderState();
        var layout = new ChartRenderer().Render(surface.Canvas, model, 1000, 600, state);
        var badge = Assert.IsType<SKRect>(state.CompactRecordingWarningBounds);

        Assert.True(layout.Plot.Contains(badge));
        Assert.False(badge.Contains(layout.Main.Rect.MidX, layout.Main.Rect.MidY));
        Assert.True(badge.Width < layout.Main.Rect.Width / 3);
        Assert.True(badge.Height < layout.Main.Rect.Height / 3);
        Assert.Equal(16m, model.Series.Candles.Sum(candle => candle.Volume));
        Assert.Equal(107m, data.LastPrice);
    }

    private sealed class LimitedHistoryFeed : IMarketDataFeed
    {
        public DateTime Start { get; } = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Now => Start.AddMinutes(20);
        public Instrument Instrument { get; } = new() { FeedId = "warning-fixture", Symbol = "LIMITED", TickSize = 1, VolumeStep = 1 };
        public string Id => "warning-fixture";
        public string DisplayName => "Isolated limited history";
        public bool SupportsHistory => true;
        public bool IsLive => false;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>([Instrument]);
        public Task LoadTradesAsync(Instrument instrument, DateTime from, DateTime to, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            onBatch(Enumerable.Range(0, 20).Select(index => new Trade(index + 1, Start.AddMinutes(index), 100 + index, 2, TradeSide.Buy)).ToArray());
            return Task.CompletedTask;
        }
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => new Lease();
        public void Dispose() { }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
