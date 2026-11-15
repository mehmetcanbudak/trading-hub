using System.Text;
using System.Text.Json;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.Derivatives;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using static TradingHub.Tests.VenueFixtures;

namespace TradingHub.Tests;

/// <summary>Funding, open interest and liquidations from public derivatives endpoints, against fixtures only.</summary>
public sealed class DerivativesTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "th-derivatives-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Liquidation_messages_report_the_liquidated_position_side_and_ignore_control_frames()
    {
        var binance = Assert.Single(DerivativesData.Parse(DerivativesData.Binance, Encoding.UTF8.GetBytes(
            """{"e":"forceOrder","E":1568014460893,"o":{"s":"BTCUSDT","S":"SELL","o":"LIMIT","f":"IOC","q":"0.014","p":"9910","ap":"9911","X":"FILLED","l":"0.014","z":"0.012","T":1568014460893}}""")));
        Assert.True(binance.LongLiquidated);
        Assert.Equal((9911m, 0.012m), (binance.Price, binance.Quantity));
        var bybit = Assert.Single(DerivativesData.Parse(DerivativesData.Bybit, Encoding.UTF8.GetBytes(
            """{"topic":"allLiquidation.BTCUSDT","type":"snapshot","ts":1739502303000,"data":[{"T":1739502302929,"s":"BTCUSDT","S":"Sell","v":"0.5","p":"96000"}]}""")));
        Assert.False(bybit.LongLiquidated);
        Assert.Equal(48000m, bybit.Notional);
        Assert.Empty(DerivativesData.Parse(DerivativesData.Bybit, Encoding.UTF8.GetBytes("""{"op":"pong","success":true}""")));
        Assert.Empty(DerivativesData.Parse(DerivativesData.Binance, Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public async Task Funding_pages_each_venue_in_percent_and_reports_an_empty_range_as_unavailable()
    {
        using var handler = new Http((request, _) =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            return request.RequestUri.AbsolutePath switch
            {
                "/fapi/v1/fundingRate" => Json(query["symbol"] == "BTCUSDT"
                    ? $$"""[{"symbol":"BTCUSDT","fundingTime":{{Ms(Start)}},"fundingRate":"0.0001"},{"symbol":"BTCUSDT","fundingTime":{{Ms(Start.AddHours(8))}},"fundingRate":"-0.00025"}]"""
                    : "[]"),
                "/v5/market/funding/history" => Json($$$"""{"retCode":0,"result":{"list":[{"symbol":"BTCUSDT","fundingRate":"0.0002","fundingRateTimestamp":"{{{Ms(Start.AddHours(8))}}}"},{"symbol":"BTCUSDT","fundingRate":"0.0001","fundingRateTimestamp":"{{{Ms(Start)}}}"}]}}"""),
                _ => throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri),
            };
        });
        using var http = new HttpClient(handler);
        using var data = new DerivativesData(new DerivativesTransport(http, BinanceRest: new Uri("https://binance.fixture"), BybitRest: new Uri("https://bybit.fixture")));

        var binance = await data.LoadFundingAsync(DerivativesData.Binance, "BTCUSDT", Start, Start.AddDays(1), CancellationToken.None);
        Assert.Equal(new[] { 0.01m, -0.025m }, binance.Samples.Select(s => s.RatePercent));
        var bybit = await data.LoadFundingAsync(DerivativesData.Bybit, "BTCUSDT", Start, Start.AddDays(1), CancellationToken.None);
        Assert.Equal(new[] { Start, Start.AddHours(8) }, bybit.Samples.Select(s => s.Time));
        var none = await data.LoadFundingAsync(DerivativesData.Binance, "NONEUSDT", Start, Start.AddDays(1), CancellationToken.None);
        Assert.Equal(OpenInterestAvailability.Unavailable, none.Availability);
        Assert.All(handler.Requests, r => Assert.Contains(r.Uri.Host, new[] { "binance.fixture", "bybit.fixture" }));
    }

    [Fact]
    public async Task Open_interest_reuses_each_venue_provider_and_reports_gaps_as_partial()
    {
        var now = Start.AddHours(1);
        using var handler = new Http((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/futures/data/openInterestHist" => Json($$"""[{"symbol":"BTCUSDT","sumOpenInterest":"100","sumOpenInterestValue":"1","timestamp":{{Ms(Start)}}},{"symbol":"BTCUSDT","sumOpenInterest":"110","sumOpenInterestValue":"1","timestamp":{{Ms(Start.AddMinutes(5))}}}]"""),
            "/v5/market/open-interest" => Json($$$"""{"retCode":0,"result":{"symbol":"BTCUSDT","list":[{"openInterest":"55","timestamp":"{{{Ms(Start.AddMinutes(5))}}}"},{"openInterest":"50","timestamp":"{{{Ms(Start)}}}"}],"nextPageCursor":""}}"""),
            _ => throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri),
        });
        using var http = new HttpClient(handler);
        using var data = new DerivativesData(new DerivativesTransport(http, BinanceRest: new Uri("https://binance.fixture"), BybitRest: new Uri("https://bybit.fixture"),
            UtcNow: () => now, Delay: NoDelay));

        var binance = await data.LoadOpenInterestAsync(DerivativesData.Binance, "BTCUSDT", Start, Start.AddMinutes(10), CancellationToken.None);
        Assert.Equal(new[] { 100m, 110m }, binance.Samples.Select(s => s.Quantity));
        var bybit = await data.LoadOpenInterestAsync(DerivativesData.Bybit, "BTCUSDT", Start, Start.AddMinutes(30), CancellationToken.None);
        Assert.Equal(new[] { 50m, 55m }, bybit.Samples.Select(s => s.Quantity));
        Assert.Equal(OpenInterestAvailability.Partial, bybit.Availability); // Nothing after 00:05 for a request to 00:30.
        Assert.Contains(handler.Requests, r => r.Uri.Host == "binance.fixture" && r.Uri.AbsolutePath == "/futures/data/openInterestHist");
        Assert.Throws<InvalidDataException>(() => BybitOpenInterest.ParseRows(JsonDocument.Parse("""{"list":[{"openInterest":"-1","timestamp":"1"}]}""").RootElement).ToList());
    }

    [Fact]
    public async Task Liquidation_streams_record_events_with_observed_windows_and_reload_them_after_restart()
    {
        var clock = Start.AddHours(10);
        var binanceStream = new Socket();
        var bybitStream = new Socket();
        var endpoints = new System.Collections.Concurrent.ConcurrentQueue<Uri>();
        Task<System.Net.WebSockets.WebSocket> Connect(Uri endpoint, CancellationToken ct)
        {
            endpoints.Enqueue(endpoint);
            return Task.FromResult<System.Net.WebSockets.WebSocket>(endpoint.AbsolutePath.Contains("forceOrder") ? binanceStream : bybitStream);
        }
        var transport = new DerivativesTransport(new HttpClient(new Http((_, _) => Json("{}"))), OwnsHttp: true, ConnectWebSocket: Connect,
            UtcNow: () => clock, Delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        using (var data = new DerivativesData(transport, _root))
        {
            using (data.WatchLiquidations("btcusdt"))
            {
                await bybitStream.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains("allLiquidation.BTCUSDT", bybitStream.Sent.First());
                await WaitUntilAsync(() => data.Liquidations("BTCUSDT", Start, clock.AddHours(1)).Windows.Count == 2);
                Assert.Contains(endpoints, e => e.Host == "fstream.binance.com" && e.AbsolutePath == "/ws/btcusdt@forceOrder");
                Assert.Contains(endpoints, e => e.Host == "stream.bybit.com" && e.AbsolutePath == "/v5/public/linear");

                long generation = data.LiquidationGeneration("BTCUSDT");
                binanceStream.SendServer($$$"""{"e":"forceOrder","o":{"s":"BTCUSDT","S":"BUY","q":"2","p":"100","ap":"101","z":"2","T":{{{Ms(clock)}}}}}""");
                bybitStream.SendServer($$"""{"topic":"allLiquidation.BTCUSDT","data":[{"T":{{Ms(clock)}},"s":"BTCUSDT","S":"Buy","v":"1","p":"100"}]}""");
                await WaitUntilAsync(() => data.Liquidations("BTCUSDT", Start, clock.AddHours(1)).Events.Count == 2);
                Assert.True(data.LiquidationGeneration("BTCUSDT") > generation);
                clock = clock.AddMinutes(5);
            }
            // Releasing the last watcher closes both streams, which persist their final observed window end.
            await WaitUntilAsync(() => data.Liquidations("BTCUSDT", Start, clock.AddHours(1)).Windows is { Count: 2 } windows && windows.All(w => w.To == clock));
        }

        using var reloaded = new DerivativesData(new DerivativesTransport(UtcNow: () => clock), _root);
        var snapshot = reloaded.Liquidations("BTCUSDT", Start, clock.AddHours(1));
        Assert.Equal(new[] { (DerivativesData.Binance, false, 202m), (DerivativesData.Bybit, true, 100m) },
            snapshot.Events.OrderBy(e => e.VenueId).Select(e => (e.VenueId, e.LongLiquidated, e.Notional)));
        Assert.Equal(new[] { DerivativesData.Binance, DerivativesData.Bybit }, snapshot.Windows.Select(w => w.VenueId).Order());
        Assert.All(snapshot.Windows, w => Assert.Equal((Start.AddHours(10), Start.AddHours(10).AddMinutes(5)), (w.From, w.To)));
    }

    [Fact]
    public void Pane_symbol_and_sampling_keep_unknown_periods_as_gaps()
    {
        Assert.Equal("BTCUSDT", DerivativesPane.DerivedSymbol("BTC-USDT-SWAP", "BTC", "USDT"));
        Assert.Equal("1000PEPEUSDT", DerivativesPane.DerivedSymbol("kPEPE", "1000PEPE", "USD"));
        Assert.Equal("ETHBTC", DerivativesPane.DerivedSymbol("ETH-BTC", "ETH", "BTC"));
        var samples = new[] { (Start, 1m), (Start.AddMinutes(5), 2m) };
        Assert.Equal(2m, DerivativesPane.At(samples, Start.AddMinutes(9), TimeSpan.FromMinutes(10)));
        Assert.Null(DerivativesPane.At(samples, Start.AddMinutes(20), TimeSpan.FromMinutes(10)));
        Assert.Null(DerivativesPane.At(samples, Start.AddSeconds(-1), TimeSpan.FromMinutes(10)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
}
