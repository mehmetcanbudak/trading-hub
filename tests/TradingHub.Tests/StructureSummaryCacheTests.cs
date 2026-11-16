using TradingHub.App.Services.Workbench;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Tests that read or fill <see cref="CandleSeriesCache.Shared"/> and must not run beside charts of other tests.</summary>
[CollectionDefinition("SharedCandleCache", DisableParallelization = true)]
public sealed class SharedCandleCacheCollection;

/// <summary>The Workbench structure summary builds throwaway charts that must leave the open charts' shared candle cache alone.</summary>
[Collection("SharedCandleCache")]
public sealed class StructureSummaryCacheTests
{
    private static readonly DateTime Start = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private static (CandleSeries Series, TradeStore Store) Chart(int index)
    {
        var instrument = new Instrument { Symbol = $"CHART{index}USDT", FeedId = "binance-futures", Exchange = "Binance", TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = $"CHART{index}", QuoteAsset = "USDT" };
        var store = new TradeStore(instrument);
        for (int i = 0; i < 2_000; i++)
            store.Add(new Trade(i + 1, Start.AddSeconds(i * 20), 100 + (i * 37 % 120) * 0.1m, 0.001m * (1 + i % 5), i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        var series = new CandleSeries(instrument, ChartPeriod.M1, 1);
        series.Rebuild(store);
        return (series, store);
    }

    private static List<Kline> Candles(int count, int minutes)
    {
        var list = new List<Kline>(count);
        decimal close = 100;
        for (int i = 0; i < count; i++)
        {
            decimal open = close;
            close = 100 + 20 * (decimal)Math.Sin(i / 7.0);
            list.Add(new Kline(new DateTimeOffset(Start.AddMinutes(minutes * i)).ToUnixTimeMilliseconds(), open, Math.Max(open, close) + 1, Math.Min(open, close) - 1, close, 40, 0, false));
        }
        return list;
    }

    [Fact]
    public void A_summary_refresh_neither_admits_to_nor_evicts_from_the_shared_cache()
    {
        var cache = CandleSeriesCache.Shared;
        cache.Clear();
        try
        {
            var charts = Enumerable.Range(0, 8).Select(Chart).ToArray();
            foreach (var (series, store) in charts) cache.Store(series, store, "binance-futures");
            Assert.Equal(8, cache.EntryCount);
            long retained = cache.RetainedBytes;

            var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
            var now = Start.AddDays(5);
            foreach (var timeframe in Enum.GetValues<StructureTimeframe>())
            {
                int minutes = StructureSummary.SourceMinutes(timeframe);
                var result = StructureSummary.Build(instrument, Candles(600, minutes), timeframe, 0, now);
                Assert.True(result.ClosedBars > 0);
            }

            Assert.Equal(8, cache.EntryCount);
            Assert.Equal(retained, cache.RetainedBytes);
            Assert.All(charts, chart => Assert.True(cache.Contains(chart.Series, chart.Store, "binance-futures")));
        }
        finally
        {
            cache.Clear();
        }
    }

    [Fact]
    public void A_private_chart_model_neither_uses_the_shared_cache_nor_schedules_neighbour_prebuilds()
    {
        var cache = CandleSeriesCache.Shared;
        cache.Clear();
        try
        {
            var instrument = new Instrument { Symbol = "PRIVATE-" + Guid.NewGuid().ToString("N"), FeedId = "sim", TickSize = 0.25m, VolumeStep = 1 };
            using var data = new InstrumentData(instrument, new SimulatedFeed());
            for (int i = 0; i < 5_000; i++) data.Trades.Add(new Trade(i + 1, Start.AddSeconds(i * 13), 100 + i % 40 * 0.25m, 1, TradeSide.Buy));

            using var model = new ChartModel(data, ChartPeriod.M15, new ChartSettings { TimeZoneId = "UTC" }, sharedSeriesCache: false);
            Assert.Null(model.NeighbourBuilds);
            model.SetPeriod(ChartPeriod.M5);
            model.Rebuild();
            Assert.True(model.Series.Count > 0);
            Assert.Equal(0, cache.EntryCount);

            // The same history in a shared chart is cached and prebuilds its neighbours.
            using var shared = new ChartModel(data, ChartPeriod.M15, new ChartSettings { TimeZoneId = "UTC" });
            Assert.Equal(1, cache.EntryCount);
            Assert.NotNull(shared.NeighbourBuilds);
        }
        finally
        {
            cache.Clear();
        }
    }
}
