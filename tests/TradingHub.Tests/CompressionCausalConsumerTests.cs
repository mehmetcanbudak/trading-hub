using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class CompressionCausalConsumerTests
{
    private static readonly Instrument Instrument = new() { FeedId = "compression-fixture", Symbol = "TEST", TickSize = 1, VolumeStep = 1, PointValue = 1 };
    private static readonly DateTime Start = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RepeatedPointRunsKeepBarSignalFillPricesAndExactMeasuredVolume()
    {
        const decimal volume = 0.1234567890123456789012345678m;
        const decimal extra = 0.0000000000000000000000000001m;
        var provenance = new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide, SourceIdentity: "venue:point-runs", CorrectionRevision: 17);
        var rows = new List<Trade>();
        for (int minute = 0; minute < 3; minute++)
        {
            decimal price = 1080 + minute * 5;
            long id = 9_000_000_010 + minute * 10;
            var time = Start.AddMinutes(minute);
            rows.Add(new Trade(id, time, price, volume, TradeSide.Buy, provenance with { SourceEventId = $"native:東京/{minute}/A" }));
            rows.Add(new Trade(id + 1, time.AddMilliseconds(40), price, extra, TradeSide.Buy, provenance with { SourceEventId = $"native:東京/{minute}/B" }));
            rows.Add(new Trade(id + 2, time.AddSeconds(20), price + 1, 1, TradeSide.Sell, provenance with { SourceEventId = $"native:東京/{minute}/C" }));
        }
        var original = TickData.FromTrades(Instrument, rows);
        var packed = original.Compress();
        var originalStrategy = new BarRoundTrip();
        var packedStrategy = new BarRoundTrip();
        var originalResult = new BacktestEngine(originalStrategy, original, Settings()).Run();
        var packedResult = new BacktestEngine(packedStrategy, packed, Settings()).Run();

        Assert.True(originalResult.Ok, originalResult.Error);
        Assert.True(packedResult.Ok, packedResult.Error);
        var expected = Assert.Single(originalResult.Trades);
        var actual = Assert.Single(packedResult.Trades);
        Assert.Equal((1086m, 1089m, 3m, 1m, 2m), (expected.EntryPrice, expected.ExitPrice, expected.Pnl, expected.Commission, expected.NetPnl));
        Assert.Equal((expected.EntryPrice, expected.ExitPrice, expected.Pnl, expected.Commission, expected.NetPnl),
            (actual.EntryPrice, actual.ExitPrice, actual.Pnl, actual.Commission, actual.NetPnl));
        Assert.Equal(Start.AddMinutes(1), expected.EntryTime);
        Assert.Equal(Start.AddMinutes(2), expected.ExitTime);
        Assert.Equal(expected.EntryTime.Ticks / TimeSpan.TicksPerSecond, actual.EntryTime.Ticks / TimeSpan.TicksPerSecond);
        Assert.Equal(expected.ExitTime.Ticks / TimeSpan.TicksPerSecond, actual.ExitTime.Ticks / TimeSpan.TicksPerSecond);
        Assert.Equal(2m, packedResult.Metrics.NetProfit);
        Assert.Equal(originalStrategy.Seen.Select(PricesAndVolume), packedStrategy.Seen.Select(PricesAndVolume));
        Assert.Equal(rows.Select(t => (t.Id, t.Time, t.Volume, t.Provenance)),
            Enumerable.Range(original.Start, original.Count).Select(original.Get).Select(t => (t.Id, t.Time, t.Volume, t.Provenance)));

        Assert.Equal(6, packed.Count);
        var run = packed.Get(0);
        Assert.True(run.Id < 0);
        Assert.DoesNotContain(run.Id, rows.Select(t => t.Id));
        Assert.Equal(Start.AddMilliseconds(40), run.Time);
        Assert.Equal(run.Time, run.AvailableAt);
        Assert.True(run.HasKnownAvailability && run.Provenance.IsMeasured);
        Assert.False(run.IsObservedTrade);
        Assert.Equal(DataQuality.Compact | DataQuality.InferredSide, run.Provenance.Quality);
        Assert.Equal(0, run.Provenance.ResolutionSeconds);
        Assert.Equal(2, run.Provenance.ObservedTradeCount);
        Assert.Equal(17, run.Provenance.CorrectionRevision);
        Assert.Equal(provenance.SourceIdentity, run.Provenance.SourceIdentity);
        Assert.Null(run.Provenance.SourceEventId);
        Assert.Equal(0.1234567890123456789012345679m, run.Volume);
        Assert.Equal(rows[2].Id, packed.Get(1).Id);
        Assert.Equal(rows[2].Provenance, packed.Get(1).Provenance);

        var store = new TradeStore(Instrument);
        store.Add(run);
        var series = new CandleSeries(Instrument, ChartPeriod.M1);
        series.Rebuild(store);
        var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
        Assert.Equal(0m, candle.MaxTradeVolume);
        Assert.Empty(candle.GetAllPriceLevels());
        Assert.Equal(run.Volume, Assert.Single(candle.GetAggregatedPriceLevels()).Volume);
    }

    [Fact]
    public void UnknownPointTimingKeepsOriginalEventsInsteadOfInventingKnownCompletion()
    {
        var provenance = new TradeProvenance(DataQuality.Raw, SourceIdentity: "venue:unknown", HasKnownAvailability: false);
        var rows = new[]
        {
            new Trade(9_000_000_001, Start, 100, 2, TradeSide.Buy, provenance with { SourceEventId = "first" }),
            new Trade(9_000_000_002, Start.AddMilliseconds(40), 100, 3, TradeSide.Buy, provenance with { SourceEventId = "second" }),
        };
        var packed = TickData.FromTrades(Instrument, rows).Compress();
        Assert.Equal(rows.Select(t => (t.Id, t.Time, t.Volume, t.Provenance)),
            Enumerable.Range(packed.Start, packed.Count).Select(packed.Get).Select(t => (t.Id, t.Time, t.Volume, t.Provenance)));
        var strategy = new BarRoundTrip();
        var result = new BacktestEngine(strategy, packed, Settings()).Run();
        Assert.NotNull(result.Error);
        Assert.False(result.HasKnownAvailability);
        Assert.Empty(strategy.Seen);
        Assert.Empty(strategy.Filled);
    }

    [Theory]
    [InlineData(DataQuality.Compact)]
    [InlineData(DataQuality.Synthetic | DataQuality.InferredSide)]
    public void CompressingIntervalAnchorsDoesNotLetTheirCloseSignalFillTheirOwnWindow(DataQuality quality)
    {
        var provenance = new TradeProvenance(quality, 60, "venue:intervals", ObservedTradeCount: quality == DataQuality.Compact ? 2 : 0);
        var rows = new[]
        {
            new Trade(-101, Start, 1080, 1, TradeSide.Buy, provenance),
            new Trade(-102, Start.AddMilliseconds(40), 1080, 2, TradeSide.Buy, provenance),
            new Trade(-201, Start.AddMinutes(1), 1085, 3, TradeSide.Buy, provenance),
            new Trade(-202, Start.AddMinutes(1).AddMilliseconds(40), 1085, 5, TradeSide.Buy, provenance),
            new Trade(9_000_000_001, Start.AddMinutes(2).AddSeconds(5), 1090, 7, TradeSide.Sell),
        };
        var packed = TickData.FromTrades(Instrument, rows).Compress();
        var strategy = new BarRoundTrip(exitOnSecondBar: false);
        var result = new BacktestEngine(strategy, packed, Settings(closeAtEnd: false)).Run();

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.DataWarning);
        var entry = Assert.Single(strategy.Filled);
        Assert.Equal(Start.AddMinutes(2), entry.Time);
        Assert.Equal(Start.AddMinutes(2).AddSeconds(5), entry.FillTime);
        Assert.Equal(1091m, entry.FillPrice);
        Assert.Empty(result.Trades);
        Assert.NotNull(result.OpenPosition);
        if ((quality & DataQuality.Synthetic) != 0)
            Assert.Equal(new[] { -101L, -201L }, Enumerable.Range(packed.Start, 2).Select(packed.Id));
        else
            Assert.All(Enumerable.Range(packed.Start, 2), i => Assert.DoesNotContain(packed.Id(i), rows.Select(t => t.Id)));
        Assert.Equal(rows[^1].Id, packed.Get(packed.End - 1).Id);
        Assert.Equal(60, packed.Provenance(0).ResolutionSeconds);
        Assert.Equal(Start.AddMinutes(1), packed.Get(0).AvailableAt);
        Assert.Equal(3m, packed.Volume(0));
        Assert.Equal(8m, packed.Volume(1));
    }

    private static (decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal Buy, decimal Sell) PricesAndVolume(Bar bar) =>
        (bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.BuyVolume, bar.SellVolume);

    private static BacktestSettings Settings(bool closeAtEnd = true) => new()
    {
        FeedId = Instrument.FeedId, Symbol = Instrument.Symbol, FromUtc = Start, ToUtc = Start.AddMinutes(3), Period = "M1",
        CommissionPercent = 0, CommissionPerUnit = 0.5m, SlippageTicks = 1, CloseAtEnd = closeAtEnd,
    };

    private sealed class BarRoundTrip(bool exitOnSecondBar = true) : Strategy
    {
        public List<Bar> Seen { get; } = [];
        public List<StrategyOrder> Filled { get; } = [];
        protected override void OnInit() => UseFootprint = true;
        protected override void OnBar(Bar bar)
        {
            Seen.Add(bar);
            if (bar.Index == 0) Buy(1);
            else if (bar.Index == 1 && exitOnSecondBar) ClosePosition();
        }
        protected override void OnOrderFilled(StrategyOrder order) => Filled.Add(order);
    }
}
