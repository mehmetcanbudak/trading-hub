// Scene "venues": public crypto market and aggregate connections, and a chart with CVD by market and the derivatives
// pane, all against in-process fixtures and a loopback fake server (never real exchanges or accounts).
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Controls;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Feeds.Venues;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using TradingHub.Tests;

internal static class VenuesScene
{
    private const string OkxInstruments = """
        {"data":[{"instId":"BTC-USDT-SWAP","state":"live","ctType":"linear","ctVal":"0.01","ctValCcy":"BTC","settleCcy":"USDT","tickSz":"0.1","lotSz":"0.01"},
                 {"instId":"ETH-USDT-SWAP","state":"live","ctType":"linear","ctVal":"0.1","ctValCcy":"ETH","settleCcy":"USDT","tickSz":"0.01","lotSz":"1"}]}
        """;

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "OK " : "FAIL")}  venues: {what}");
            if (!ok) failures.Add(what);
        }
        void Shot(string name)
        {
            pump(1);
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(outDir, name + ".png"));
        }
        void Await(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted && DateTime.UtcNow < until) pump(0.05);
        }

        var now = DateTime.UtcNow;
        long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
        using var server = new FakeHttpServer();
        server.OnHttp = request => request.Path.Split('?')[0] switch
        {
            "/api/v5/public/instruments" => (200, OkxInstruments),
            "/fapi/v1/fundingRate" => (200, $$"""[{"symbol":"BTCUSDT","fundingTime":{{Ms(now.AddHours(-8))}},"fundingRate":"0.0001"},{"symbol":"BTCUSDT","fundingTime":{{Ms(now.AddMinutes(-20))}},"fundingRate":"-0.00012"}]"""),
            "/v5/market/funding/history" => (200, $$$"""{"retCode":0,"result":{"list":[{"fundingRate":"0.00008","fundingRateTimestamp":"{{{Ms(now.AddMinutes(-20))}}}"},{"fundingRate":"0.00015","fundingRateTimestamp":"{{{Ms(now.AddHours(-8))}}}"}]}}"""),
            _ => (404, "{}"),
        };
        Environment.SetEnvironmentVariable("TRADINGHUB_VENUE_REST", server.Http);
        var svc = App.Services;
        var conns = svc.Connections;
        Check(!AppPaths.Root.StartsWith(TradingHub.Common.Product.DefaultAppData, StringComparison.Ordinal), "isolated data folder");

        var okx = new ConnectionProfile { Id = "okx-scene", Kind = ConnectionKind.PublicCrypto, Name = "OKX", PublicVenueId = OkxFeed.SwapId };
        var aggregate = new ConnectionProfile
        {
            Id = "aggregate-scene", Kind = ConnectionKind.AggregateCrypto, Name = "Crypto aggregate",
            AggregateSources = new() { "binance-futures", OkxFeed.SwapId, HyperliquidFeed.PerpId, CoinbaseFeed.SpotId, "binance-spot" },
            AggregateMarkets = AggregateMarkets.Both, AggregateBook = AggregateBookMode.Consolidated,
        };
        conns.Profiles.RemoveAll(p => p.Id is "okx-scene" or "aggregate-scene");
        conns.Profiles.AddRange(new[] { okx, aggregate });
        conns.Save();
        Check(okx.MarketDataId == OkxFeed.SwapId && !okx.SupportsTrading, "public market is data-only under its venue market id");
        Check(aggregate.EffectiveAggregateSources.Count == 5, "aggregate keeps the five selected markets");
        aggregate.AggregateMarkets = AggregateMarkets.Spot;
        Check(aggregate.EffectiveAggregateSources.SequenceEqual(new[] { CoinbaseFeed.SpotId, "binance-spot" }), "spot filter keeps only spot markets");
        aggregate.AggregateMarkets = AggregateMarkets.Both;

        var test = conns.TestAsync(okx, CancellationToken.None);
        Await(test);
        Check(test.IsCompletedSuccessfully && test.Result.Ok && test.Result.Message.StartsWith("2 instruments", StringComparison.Ordinal),
            $"testing the OKX connection lists the fixture instruments ({(test.IsCompletedSuccessfully ? test.Result.Message : test.Exception?.GetBaseException().Message)})");

        window.Navigate("connections");
        pump(1);
        var pages = (Dictionary<string, Control>)window.GetType().GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var view = pages["connections"];
        var select = view.GetType().GetMethod("Select", BindingFlags.NonPublic | BindingFlags.Instance)!;
        select.Invoke(view, new object[] { okx });
        Shot("venues_public_market");
        select.Invoke(view, new object[] { aggregate });
        Shot("venues_aggregate");

        // ---- chart: CVD by market and the derivatives pane on a fixture aggregate
        window.Navigate("trading");
        var tab = window.Trading.ActiveTab;
        if (tab?.Model == null)
        {
            Check(false, "the trading page has an active chart");
            return failures.Count;
        }
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(-40);
        using var first = new Fixture("binance-futures", "Binance Futures", "Binance", false, start, 3);
        using var second = new Fixture(OkxFeed.SwapId, "OKX", "OKX", false, start, 0);
        using var third = new Fixture(CoinbaseFeed.SpotId, "Coinbase", "Coinbase", true, start, -2);
        using var feed = new AggregateFeed([first, second, third], dataScopeKey: "venues-scene");
        var instrument = feed.GetInstrumentsAsync(CancellationToken.None).GetAwaiter().GetResult().Single();
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        data.Start(start);
        for (int i = 0; i < 200 && data.IsLoadingHistory; i++) { data.Pump(); pump(0.02); }
        Check(!data.IsLoadingHistory && data.Trades.Count > 0, $"fixture aggregate history loaded ({data.Trades.Count} prints)");
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
        var cvd = new CvdByMarket();
        model.AddIndicator(cvd, applyDefaultColors: false);
        using var derivatives = new DerivativesData(new DerivativesTransport(new HttpClient(), OwnsHttp: true,
            BinanceRest: new Uri(server.Http), BybitRest: new Uri(server.Http)));
        var pane = new DerivativesPane { Service = derivatives };
        model.AddIndicator(pane, applyDefaultColors: false);
        var originalModel = tab.Model;
        try
        {
            tab.Model = model;
            tab.View.Model = model;
            for (int i = 0; i < 200 && pane.Status.Length > 0; i++) { model.Update(); pump(0.02); }
            Check(cvd.LaneNames.SequenceEqual(new[] { "Binance Futures", "OKX", "Coinbase" }), $"one CVD line per market ({string.Join(", ", cvd.LaneNames)})");
            var lastBar = model.Series.Count - 1;
            decimal Lane(string id) => ((ValueDataSeries)cvd.DataSeries.Single(s => s.Id == id))[lastBar];
            Check(Lane("M0") > 0 && Lane("M2") < 0 && Lane("AGG") == Lane("M0") + Lane("M1") + Lane("M2") + Lane("UNATTRIBUTED"),
                "per-market lines reconcile with the aggregate CVD");
            var combined = (OptionalValueDataSeries)pane.DataSeries.Single(s => s.Id == "AGG");
            Check(pane.Status.Length == 0 && combined.IsAvailable(lastBar), $"funding from both venues is combined ({pane.Status})");
            Shot("venues_cvd_derivatives");
        }
        finally
        {
            tab.Model = originalModel;
            tab.View.Model = originalModel;
            conns.Profiles.RemoveAll(p => p.Id is "okx-scene" or "aggregate-scene");
            conns.Save();
            Environment.SetEnvironmentVariable("TRADINGHUB_VENUE_REST", null);
            pump(0.2);
        }

        Console.WriteLine(failures.Count == 0 ? "VENUES: all OK" : $"VENUES: {failures.Count} failure(s)");
        return failures.Count;
    }

    /// <summary>A fixture market whose prints alternate side with a fixed buy/sell bias.</summary>
    private sealed class Fixture(string id, string name, string venue, bool spot, DateTime start, int bias) : IMarketDataFeed, IKlineHistory, ICryptoVenue
    {
        public string Id => id;
        public string DisplayName => name;
        public string Venue => venue;
        public bool IsSpot => spot;
        public bool SupportsHistory => true;
        public bool IsLive => false;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[]
        {
            new Instrument { Symbol = spot ? "BTC-USD" : "BTCUSDT", FeedId = id, TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = spot ? "USD" : "USDT" },
        });

        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            var trades = new List<Trade>();
            for (int i = 0; i < 240; i++)
            {
                var time = start.AddSeconds(i * 10 + (id.Length % 7));
                if (time < fromUtc || time > toUtc) continue;
                bool buy = bias > 0 ? i % 4 != 0 : bias < 0 ? i % 4 == 0 : i % 2 == 0;
                decimal price = 70000 + (decimal)Math.Sin(i / 12.0) * 40 + (spot ? 3 : 0);
                trades.Add(new Trade(i + 1, time, Math.Round(price, 1), 0.1m + i % 3 * 0.05m, buy ? TradeSide.Buy : TradeSide.Sell,
                    new TradeProvenance(DataQuality.Raw, SourceIdentity: id, SourceEventId: $"{id}-{i}")));
            }
            onBatch(trades);
            return Task.CompletedTask;
        }

        // No provider candles: history comes from the fixture tape only.
        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(start);

        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => new Lease();
        public void Dispose() { }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
