using System.Globalization;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using static TradingHub.Tests.VenueFixtures;

namespace TradingHub.Tests;

/// <summary>Liquidation observation windows, the bounded liquidation store and retention-limited histories, against fixtures.</summary>
public sealed class FeedReviewDerivativesTests : IDisposable
{
    private static readonly DateTime Clock = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private const string BtcListing = """{"symbols":[{"symbol":"BTCUSDT","status":"TRADING","contractType":"PERPETUAL","baseAsset":"BTC","quoteAsset":"USDT","filters":[]}]}""";
    private const string BybitAccepted = """{"success":true,"ret_msg":"subscribe","conn_id":"fixture","op":"subscribe"}""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "th-feed-review-derivatives-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // The liquidation recorder finishes its last write after DerivativesData is disposed; on Windows its file stays
        // open for a moment, so the cleanup retries instead of failing a test that already passed.
        for (int attempt = 0; Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 50) { Thread.Sleep(100); }
        }
    }

    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private static DerivativesTransport Transport(string binanceListing, Socket binance, Socket bybit, List<Uri>? endpoints = null) => new(
        new HttpClient(new Http((request, _) => Json(request.RequestUri!.AbsolutePath == "/fapi/v1/exchangeInfo" ? binanceListing : "[]"))), OwnsHttp: true,
        ConnectWebSocket: (endpoint, _) =>
        {
            if (endpoints != null) lock (endpoints) endpoints.Add(endpoint);
            return Task.FromResult<System.Net.WebSockets.WebSocket>(endpoint.AbsolutePath.Contains("forceOrder") ? binance : bybit);
        },
        UtcNow: () => Clock, Delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

    [Fact]
    public async Task A_refused_bybit_subscription_opens_no_observed_window()
    {
        var binance = new Socket();
        var bybit = new Socket();
        using var data = new DerivativesData(Transport(BtcListing, binance, bybit));
        using var watch = data.WatchLiquidations("BTCUSDT");
        await bybit.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        bybit.SendServer("""{"success":false,"ret_msg":"Invalid symbol :[allLiquidation.BTCUSDT]","conn_id":"fixture","op":"subscribe"}""");

        await WaitUntilAsync(() => data.Liquidations("BTCUSDT", Clock.AddHours(-1), Clock.AddHours(1)) is { Message: { } message } snapshot
            && message.Contains("Invalid symbol") && snapshot.Windows.Any(w => w.VenueId == DerivativesData.Binance));
        Assert.DoesNotContain(data.Liquidations("BTCUSDT", Clock.AddHours(-1), Clock.AddHours(1)).Windows, w => w.VenueId == DerivativesData.Bybit);
    }

    [Fact]
    public async Task A_symbol_binance_does_not_list_opens_no_binance_window_or_stream()
    {
        var binance = new Socket();
        var bybit = new Socket();
        var endpoints = new List<Uri>();
        const string ethOnly = """{"symbols":[{"symbol":"ETHUSDT","status":"TRADING","contractType":"PERPETUAL","baseAsset":"ETH","quoteAsset":"USDT","filters":[]}]}""";
        using var data = new DerivativesData(Transport(ethOnly, binance, bybit, endpoints));
        using var watch = data.WatchLiquidations("BTCUSDT");
        await bybit.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bybit.SendServer(BybitAccepted);

        await WaitUntilAsync(() => data.Liquidations("BTCUSDT", Clock.AddHours(-1), Clock.AddHours(1)) is { Message: { } message } snapshot
            && message.Contains("does not list BTCUSDT") && snapshot.Windows.Any(w => w.VenueId == DerivativesData.Bybit));
        Assert.DoesNotContain(data.Liquidations("BTCUSDT", Clock.AddHours(-1), Clock.AddHours(1)).Windows, w => w.VenueId == DerivativesData.Binance);
        lock (endpoints) Assert.DoesNotContain(endpoints, e => e.AbsolutePath.Contains("forceOrder"));
    }

    [Fact]
    public async Task The_liquidation_store_keeps_its_retention_and_time_order_across_recorded_and_live_events()
    {
        string folder = Path.Combine(_root, "Liquidations", "BTCUSDT");
        Directory.CreateDirectory(folder);
        string E(DateTime t, string venue) => string.Join(",", "E", t.Ticks.ToString(CultureInfo.InvariantCulture), venue, "100", "1", "L") + "\n";
        var old = Clock.AddDays(-7).Date;
        // Recorded in arrival order, which is not time order; the oldest event is beyond the seven-day retention.
        File.WriteAllText(Path.Combine(folder, old.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv"),
            E(old.AddHours(13), DerivativesData.Bybit) + E(old.AddHours(1), DerivativesData.Binance));
        File.WriteAllText(Path.Combine(folder, Clock.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv"),
            E(Clock.AddHours(-1), DerivativesData.Binance) + E(Clock.AddHours(-2), DerivativesData.Bybit));
        var binance = new Socket();
        var bybit = new Socket();
        using var data = new DerivativesData(Transport(BtcListing, binance, bybit), _root);
        using var watch = data.WatchLiquidations("BTCUSDT");
        await bybit.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bybit.SendServer(BybitAccepted);
        await WaitUntilAsync(() => data.Liquidations("BTCUSDT", Clock.AddHours(-1), Clock.AddHours(1)).Windows.Count == 2);

        // A later print reaches us before an earlier one from the other venue.
        binance.SendServer($$$"""{"e":"forceOrder","o":{"s":"BTCUSDT","S":"SELL","q":"1","p":"100","ap":"100","z":"1","T":{{{Ms(Clock.AddSeconds(-30))}}}}}""");
        await WaitUntilAsync(() => data.Liquidations("BTCUSDT", Clock.AddDays(-30), Clock.AddHours(1)).Events.Count == 4);
        bybit.SendServer($$"""{"topic":"allLiquidation.BTCUSDT","data":[{"T":{{Ms(Clock.AddSeconds(-60))}},"s":"BTCUSDT","S":"Buy","v":"1","p":"100"}]}""");
        await WaitUntilAsync(() => data.Liquidations("BTCUSDT", Clock.AddDays(-30), Clock.AddHours(1)).Events.Count == 5);

        var events = data.Liquidations("BTCUSDT", Clock.AddDays(-30), Clock.AddHours(1)).Events;
        Assert.Equal(new[] { old.AddHours(13), Clock.AddHours(-2), Clock.AddHours(-1), Clock.AddSeconds(-60), Clock.AddSeconds(-30) }, events.Select(e => e.Time));
        // A range read returns exactly the events inside it.
        Assert.Equal(new[] { Clock.AddHours(-1), Clock.AddSeconds(-60) }, data.Liquidations("BTCUSDT", Clock.AddHours(-1), Clock.AddSeconds(-30)).Events.Select(e => e.Time));
    }

    [Fact]
    public async Task Funding_and_long_short_history_that_starts_inside_the_range_is_partial()
    {
        var from = Clock.AddDays(-60);
        var first = Clock.AddDays(-30);
        using var handler = new Http((request, _) => Json(request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/fundingRate" => "[" + string.Join(",", Enumerable.Range(0, 3).Select(i =>
                $$"""{"symbol":"BTCUSDT","fundingTime":{{Ms(first.AddHours(8 * i))}},"fundingRate":"0.0001"}""")) + "]",
            "/futures/data/globalLongShortAccountRatio" => "[" + string.Join(",", Enumerable.Range(0, 3).Select(i =>
                $$"""{"symbol":"BTCUSDT","longAccount":"0.6","shortAccount":"0.4","timestamp":{{Ms(first.AddDays(i))}}}""")) + "]",
            _ => "[]",
        }));
        using var http = new HttpClient(handler);
        using var data = new DerivativesData(new DerivativesTransport(http, BinanceRest: new Uri("https://binance.fixture"), UtcNow: () => Clock));

        var funding = await data.LoadFundingAsync(DerivativesData.Binance, "BTCUSDT", from, Clock, CancellationToken.None);
        Assert.Equal(OpenInterestAvailability.Partial, funding.Availability);
        Assert.Contains("only from", funding.Message);
        var ratio = await data.LoadLongShortAsync(DerivativesData.Binance, "BTCUSDT", from, Clock, CancellationToken.None);
        Assert.Equal(OpenInterestAvailability.Partial, ratio.Availability);

        // History that reaches the start of the range is complete.
        var covered = await data.LoadFundingAsync(DerivativesData.Binance, "BTCUSDT", first.AddHours(-4), Clock, CancellationToken.None);
        Assert.Equal(OpenInterestAvailability.Available, covered.Availability);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
}
