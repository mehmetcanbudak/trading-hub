using System.Collections;
using System.Reflection;
using TradingHub.App.Services;
using TradingHub.App.Services.LocalApi;
using TradingHub.App.Services.Workbench;
using TradingHub.App.Views.Workbench;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>
/// Regressions from the Workbench and local-API review: the loopback API stays strictly read-only over data Trading Hub
/// already holds, tape liquidation coverage counts a silent stream as unobserved, and the heat map's price range is
/// maintained incrementally. No network, no sleeps, no wall clock; temporary folders are deleted.
/// </summary>
public sealed class WorkbenchReviewTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Klines_for_an_instrument_that_is_not_open_create_no_feed_and_read_no_credential()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-review-klines-" + Guid.NewGuid().ToString("N"));
        var secrets = new RecordingSecrets();
        var connections = new ConnectionManager(secretStore: secrets, configurationPath: Path.Combine(root, "connections.json"));
        try
        {
            // A credentialed broker connection that was never opened: building its feed would read its password.
            var rithmic = new ConnectionProfile { Id = "review-rithmic", Name = "Review futures", Kind = ConnectionKind.Rithmic, Enabled = true };
            connections.Profiles.Add(rithmic);
            int readsBefore = secrets.Reads, writesBefore = secrets.Writes;
            var source = new AppLocalMarketDataSource(() => false, connections, () => Array.Empty<InstrumentData>(), run => run());

            var result = await source.GetKlinesAsync(new ApiKlineRequest(rithmic.Id, "ESZ6", 1, T0, T0.AddHours(1)), CancellationToken.None);

            Assert.Equal(ApiResultStatus.NotOpen, result.Status);
            Assert.Equal("not_open", result.Code);
            Assert.Equal(readsBefore, secrets.Reads);
            Assert.Equal(writesBefore, secrets.Writes);
            Assert.Equal(0, OpenFeeds(connections));
        }
        finally
        {
            connections.DisposeAll();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Klines_of_an_open_instrument_come_from_its_loaded_trades_without_any_provider_request()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-review-open-klines-" + Guid.NewGuid().ToString("N"));
        var secrets = new RecordingSecrets();
        var connections = new ConnectionManager(secretStore: secrets, configurationPath: Path.Combine(root, "connections.json"));
        var feed = new NoProviderFeed();
        var instrument = new Instrument { Symbol = "REVIEW", FeedId = feed.Id, TickSize = 0.5m };
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        try
        {
            data.Trades.Add(new Trade(1, T0.AddSeconds(5), 100m, 1m, TradeSide.Buy));
            data.Trades.Add(new Trade(2, T0.AddSeconds(30), 101m, 2m, TradeSide.Sell));
            data.Trades.Add(new Trade(3, T0.AddSeconds(59), 99.5m, 1m, TradeSide.Buy));
            data.Trades.Add(new Trade(4, T0.AddSeconds(70), 102m, 1m, TradeSide.None));
            // Two prints of a 5-minute candle: coarser than a 1-minute bar, so never split into one.
            var candle = new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide, ResolutionSeconds: 300, ObservedTradeCount: 0);
            data.Trades.Add(new Trade(-100, T0.AddMinutes(5), 103m, 1m, TradeSide.Buy, candle));
            data.Trades.Add(new Trade(-99, T0.AddMinutes(6).AddSeconds(15), 104m, 1m, TradeSide.Sell, candle));
            int readsBefore = secrets.Reads;
            var source = new AppLocalMarketDataSource(() => false, connections, () => new[] { data }, run => run());
            long open = new DateTimeOffset(T0).ToUnixTimeMilliseconds();

            var minute = await source.GetKlinesAsync(new ApiKlineRequest(feed.Id, "review", 1, T0, T0.AddMinutes(10)), CancellationToken.None);
            Assert.Equal(ApiResultStatus.Ok, minute.Status);
            Assert.Equal(new[]
            {
                new ApiKline(open, 100m, 101m, 99.5m, 99.5m, 4m, 2m),
                new ApiKline(open + 60_000, 102m, 102m, 102m, 102m, 1m, null),
            }, minute.Value!);

            var five = await source.GetKlinesAsync(new ApiKlineRequest(feed.Id, "REVIEW", 5, T0, T0.AddMinutes(10)), CancellationToken.None);
            Assert.Equal(ApiResultStatus.Ok, five.Status);
            Assert.Equal(new[]
            {
                new ApiKline(open, 100m, 102m, 99.5m, 102m, 5m, null),
                new ApiKline(open + 300_000, 103m, 104m, 103m, 104m, 2m, 1m),
            }, five.Value!);

            Assert.Equal(0, feed.Calls);
            Assert.Equal(readsBefore, secrets.Reads);
            Assert.Equal(0, OpenFeeds(connections));
        }
        finally
        {
            connections.DisposeAll();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Candles_are_not_built_from_rows_a_correction_moved_while_they_were_read()
    {
        var instrument = new Instrument { Symbol = "REVIEW", FeedId = "review", TickSize = 0.5m };
        var store = new TradeStore(instrument);
        var trades = new[] { new Trade(1, T0, 100m, 1m, TradeSide.Buy), new Trade(2, T0.AddSeconds(1), 101m, 1m, TradeSide.Sell) };
        foreach (var trade in trades) store.Add(trade);
        var view = TradeStoreView.Capture(store);
        store.Replace(trades);

        Assert.Null(ApiKlineAggregation.FromTrades(store, view, 0, view.Count, 1, CancellationToken.None));
        Assert.NotNull(ApiKlineAggregation.FromTrades(store, TradeStoreView.Capture(store), 0, store.Count, 1, CancellationToken.None));
    }

    [Fact]
    public void A_tape_bar_one_liquidation_stream_never_observed_is_partial_not_an_observed_zero()
    {
        var tape = new TradeTape(new TapeSettings(BucketSeconds: 10));
        tape.AddTrades(new[] { new TapeTrade(T0.AddSeconds(2), "binance-futures", 100m, 1m, TradeSide.Buy) });
        var bucket = Assert.Single(tape.Buckets);
        var now = T0.AddSeconds(20);
        Assert.Equal(LiquidationCoverage.NotObserved, TradeTape.BucketCoverage(bucket, DerivativesData.Venues));

        var binance = new ObservedWindow(DerivativesData.Binance, T0.AddMinutes(-1), T0.AddMinutes(1));
        tape.ApplyLiquidationWindows(new[] { binance }, DerivativesData.Venues, now);
        Assert.Equal(LiquidationCoverage.Partial, TradeTape.BucketCoverage(bucket, DerivativesData.Venues));

        tape.ApplyLiquidationWindows(new[] { binance, binance with { VenueId = DerivativesData.Bybit } }, DerivativesData.Venues, now);
        Assert.Equal(LiquidationCoverage.Observed, TradeTape.BucketCoverage(bucket, DerivativesData.Venues));
    }

    [Fact]
    public void The_heat_map_price_range_follows_its_window_and_rescans_after_a_correction()
    {
        var instrument = new Instrument { Symbol = "REVIEW", FeedId = "review", TickSize = 0.5m };
        var store = new TradeStore(instrument);
        store.Add(new Trade(1, T0.AddSeconds(10), 100m, 1m, TradeSide.Buy));
        store.Add(new Trade(2, T0.AddSeconds(70), 105m, 1m, TradeSide.Sell));
        store.Add(new Trade(3, T0.AddSeconds(130), 95m, 1m, TradeSide.Buy));
        var range = new TradePriceRange();

        Assert.Equal((95m, 105m), range.Of(store, T0));
        store.Add(new Trade(4, T0.AddSeconds(190), 110m, 1m, TradeSide.Sell));
        Assert.Equal((95m, 110m), range.Of(store, T0));
        Assert.Equal((95m, 110m), range.Of(store, T0.AddSeconds(120)));
        Assert.Equal((110m, 110m), range.Of(store, T0.AddSeconds(180)));
        Assert.Equal((95m, 110m), range.Of(store, T0)); // a longer window scans the earlier rows again
        Assert.Null(range.Of(store, T0.AddMinutes(10)));

        store.Replace(new[] { new Trade(5, T0.AddSeconds(10), 50m, 1m, TradeSide.Buy) });
        Assert.Equal((50m, 50m), range.Of(store, T0));
    }

    /// <summary>Feeds the connection layer created (it builds them on first use and reads their credentials then).</summary>
    private static int OpenFeeds(ConnectionManager connections) =>
        ((ICollection)typeof(ConnectionManager).GetField("_feeds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connections)!).Count;

    private sealed class RecordingSecrets : ISecretStore
    {
        private int _reads, _writes;
        public int Reads => Volatile.Read(ref _reads);
        public int Writes => Volatile.Read(ref _writes);

        public string Get(string account)
        {
            Interlocked.Increment(ref _reads);
            return "";
        }

        public void Set(string account, string? secret) => Interlocked.Increment(ref _writes);
    }

    /// <summary>Counts every provider call; the local API must make none.</summary>
    private sealed class NoProviderFeed : IMarketDataFeed, IKlineHistory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Id => "review-feed";
        public string DisplayName => "Review feed";
        public bool SupportsHistory => true;
        public bool IsLive => false;

        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<Instrument>>(Array.Empty<Instrument>());
        }

        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            Interlocked.Increment(ref _calls);
            return new Released();
        }

        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<DateTime?>(null);
        }

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }

        public void Dispose() { }

        private sealed class Released : IDisposable
        {
            public void Dispose() { }
        }
    }
}
