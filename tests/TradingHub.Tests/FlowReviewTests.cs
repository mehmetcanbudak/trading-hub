using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Feeds.External;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Indicators.Native.Macro;
using TradingHub.Indicators.Native.Options;
using TradingHub.Market;
using static TradingHub.Tests.VenueFixtures;

namespace TradingHub.Tests;

/// <summary>
/// Regressions from the flow, derivatives, options and macro review: live time and live ticks never leave stale or
/// duplicated study state. Fixtures only (no network); synchronization is by posted actions and observed values.
/// </summary>
public sealed class FlowReviewTests
{
    private static readonly DateTime Start = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>
    /// Binance and Bybit public endpoints publishing five-minute open interest and eight-hourly funding up to
    /// <see cref="Published"/>; every request is recorded with the start it asked for.
    /// </summary>
    private sealed class DerivativesVenues
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private long _published;
        public readonly ConcurrentQueue<(string Path, DateTime From)> Requests = new();

        /// <summary>The latest published sample time (requests are served on pool threads).</summary>
        public DateTime PublishedUntil
        {
            get => new(Interlocked.Read(ref _published), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _published, value.Ticks);
        }

        public static decimal OpenInterest(DateTime t, decimal level) => level + (decimal)(t - Start).TotalMinutes;

        private static string Rate(DateTime t) => (0.0001m * (1 + (decimal)(t - Start).TotalHours / 8)).ToString(Ci);

        private static IEnumerable<DateTime> Every(DateTime from, DateTime until, TimeSpan step)
        {
            long s = step.Ticks;
            for (var t = new DateTime((from.Ticks + s - 1) / s * s, DateTimeKind.Utc); t <= until; t += step) yield return t;
        }

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            DateTime from = FromMs(long.Parse(query["startTime"]!, Ci)), to = FromMs(long.Parse(query["endTime"]!, Ci));
            Requests.Enqueue((uri.AbsolutePath, from));
            var published = PublishedUntil;
            var until = to < published ? to : published;
            var fiveMinutes = Every(from, until, TimeSpan.FromMinutes(5)).ToList();
            var settlements = Every(from, until, TimeSpan.FromHours(8)).ToList();
            return uri.AbsolutePath switch
            {
                "/futures/data/openInterestHist" => Json("[" + string.Join(",", fiveMinutes.Select(t =>
                    "{\"symbol\":\"BTCUSDT\",\"sumOpenInterest\":\"" + OpenInterest(t, 1000).ToString(Ci) + "\",\"sumOpenInterestValue\":\"1\",\"timestamp\":" + Ms(t).ToString(Ci) + "}")) + "]"),
                "/v5/market/open-interest" => Json("{\"retCode\":0,\"result\":{\"symbol\":\"BTCUSDT\",\"list\":[" + string.Join(",", Enumerable.Reverse(fiveMinutes).Select(t =>
                    "{\"openInterest\":\"" + OpenInterest(t, 500).ToString(Ci) + "\",\"timestamp\":\"" + Ms(t).ToString(Ci) + "\"}")) + "],\"nextPageCursor\":\"\"}}"),
                "/fapi/v1/fundingRate" => Json("[" + string.Join(",", settlements.Select(t =>
                    "{\"symbol\":\"BTCUSDT\",\"fundingTime\":" + Ms(t).ToString(Ci) + ",\"fundingRate\":\"" + Rate(t) + "\"}")) + "]"),
                "/v5/market/funding/history" => Json("{\"retCode\":0,\"result\":{\"list\":[" + string.Join(",", Enumerable.Reverse(settlements).Select(t =>
                    "{\"symbol\":\"BTCUSDT\",\"fundingRate\":\"" + Rate(t) + "\",\"fundingRateTimestamp\":\"" + Ms(t).ToString(Ci) + "\"}")) + "]}}"),
                _ => throw new InvalidOperationException("Unexpected fixture request: " + uri),
            };
        }
    }

    private static DerivativesData Service(HttpClient http, Func<DateTime> now) => new(new DerivativesTransport(http,
        BinanceRest: new Uri("https://binance.fixture"), BybitRest: new Uri("https://bybit.fixture"), UtcNow: now, Delay: NoDelay));

    /// <summary>Runs chart updates (posted actions, recalculations, timers) until <paramref name="done"/> holds.</summary>
    private static async Task PumpUntil(ChartModel model, Func<bool> done)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            model.Update();
            if (done()) return;
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    // ------------------------------------------------------------------ SF-1: derivatives series follow live time

    [Fact]
    public async Task Derivatives_context_refreshes_open_interest_and_funding_from_the_latest_sample_as_live_time_advances()
    {
        var now = Start.AddHours(7).AddMinutes(55);
        var venues = new DerivativesVenues { PublishedUntil = now };
        using var handler = new Http((request, _) => venues.Respond(request));
        using var http = new HttpClient(handler);
        using var data = Service(http, () => now);
        var posted = Channel.CreateUnbounded<Action>();
        int changes = 0;
        using var context = new DerivativesContext(a => posted.Writer.TryWrite(a), () => changes++, openInterest: true, funding: true, liquidations: false)
        {
            Service = data,
            Clock = () => now,
        };

        // The posted actions stand in for the chart's GUI thread; each load and refresh posts exactly once.
        async Task Apply(int count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (int i = 0; i < count; i++) (await posted.Reader.ReadAsync(deadline.Token))();
        }

        context.Ensure("BTCUSDT", Start);
        Assert.Equal("Loading derivatives data…", context.Message);
        await Apply(2);
        Assert.Equal(2, changes);
        Assert.Null(context.Message);
        Assert.Equal(1470m + 970m, context.OpenInterestAt(now.AddTicks(-1))); // samples at 07:50
        Assert.Equal(0.01m, context.FundingAt(now));                          // settled at 00:00

        now = Start.AddHours(9).AddMinutes(5);
        venues.PublishedUntil = now;
        Assert.Null(context.OpenInterestAt(now.AddTicks(-1))); // 75 minutes old: unknown until refreshed
        Assert.Null(context.FundingAt(now));                   // 9 h 5 min old: unknown until refreshed
        int before = venues.Requests.Count;
        context.Ensure("BTCUSDT", Start);
        await Apply(2);
        Assert.Equal(4, changes);
        Assert.Equal(1540m + 1040m, context.OpenInterestAt(now.AddTicks(-1))); // samples at 09:00
        Assert.Equal(0.02m, context.FundingAt(now));                           // settled at 08:00
        Assert.Equal(1470m + 970m, context.OpenInterestAt(Start.AddMinutes(471))); // earlier samples are kept

        // One bounded request per venue and series, starting at each venue's latest sample, never the whole range.
        var refresh = venues.Requests.Skip(before).ToList();
        Assert.Equal(4, refresh.Count);
        Assert.All(refresh.Where(r => r.Path.Contains("pen", StringComparison.Ordinal)), r => Assert.Equal(Start.AddMinutes(470), r.From));
        Assert.All(refresh.Where(r => r.Path.Contains("unding", StringComparison.Ordinal)), r => Assert.Equal(Start, r.From));

        // Within the refresh interval nothing more is requested, however often the last bar recalculates.
        for (int i = 0; i < 10; i++) context.Ensure("BTCUSDT", Start);
        Assert.Equal(before + 4, venues.Requests.Count);
    }

    [Fact]
    public async Task Derivatives_pane_extends_open_interest_to_new_live_bars()
    {
        var now = Start.AddHours(2);
        var venues = new DerivativesVenues { PublishedUntil = now };
        using var handler = new Http((request, _) => venues.Respond(request));
        using var http = new HttpClient(handler);
        using var data = Service(http, () => now);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "fake", TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        using var feed = new SimulatedFeed();
        using var market = new InstrumentData(instrument, feed) { RecordLive = false };
        for (int i = 0; i < 120; i++) market.Trades.Add(new Trade(i + 1, Start.AddMinutes(i).AddSeconds(10), 100, 1, TradeSide.Buy));
        using var model = new ChartModel(market, ChartPeriod.Parse("M5"), new ChartSettings { TimeZoneId = "UTC" });
        var pane = new DerivativesPane { View = DerivativesPane.DerivativesView.OpenInterest, Service = data, Clock = () => now };
        Assert.Null(model.AddIndicator(pane, applyDefaultColors: false).Error);
        var combined = Assert.IsType<OptionalValueDataSeries>(pane.DataSeries[0]);

        await PumpUntil(model, () => model.Series.Count == 24 && combined.IsAvailable(23));
        Assert.Equal(1115m + 615m, combined[23]); // the 01:55 samples close the last bar

        // Twenty live minutes later: four new bars, which the loaded series alone would leave as gaps.
        now = Start.AddMinutes(140);
        venues.PublishedUntil = now;
        int before = venues.Requests.Count;
        for (int i = 120; i < 140; i++) market.Trades.Add(new Trade(i + 1, Start.AddMinutes(i).AddSeconds(10), 100, 1, TradeSide.Buy));
        await PumpUntil(model, () => model.Series.Count == 28 && combined.IsAvailable(27));
        Assert.Equal(1135m + 635m, combined[27]);
        Assert.True(combined.IsAvailable(24));
        Assert.All(venues.Requests.Skip(before), r => Assert.Equal(Start.AddMinutes(115), r.From));
    }

    // ------------------------------------------------------------------ SF-2 / SF-3: live ticks of a day's first bar

    [Fact]
    public void Daily_closes_are_kept_once_per_day_while_the_first_bar_of_a_day_forms()
    {
        var fx = new OptionsFixture(ProfileMergeTests.Monday.AddDays(70));
        var hub = new OptionsData(null, () => fx.End) { Provider = fx };
        using var data = OptionsMacroStudyTests.Market(70);
        using var model = new ChartModel(data, ChartPeriod.D1, new ChartSettings { TimeZoneId = "UTC" });
        var vrp = new VarianceRiskPremium { Service = hub, RealizedDays = 5 };
        var gauge = new OptionsSentimentGauge { Service = hub };
        Assert.Null(model.AddIndicator(vrp, applyDefaultColors: false).Error);
        Assert.Null(model.AddIndicator(gauge, applyDefaultColors: false).Error);
        model.Update();
        model.Update();
        int last = model.Series.Count - 1;
        var realized = Assert.IsType<OptionalValueDataSeries>(vrp.DataSeries[2]);
        var greed = Assert.IsType<OptionalValueDataSeries>(gauge.DataSeries[0]);
        Assert.True(realized.IsAvailable(last));
        Assert.True(greed.IsAvailable(last));
        decimal rv = realized[last], g = greed[last];

        // Every trade batch recalculates the forming bar; on a daily chart each bar starts a new session.
        for (int tick = 0; tick < 10; tick++)
        {
            vrp.HostCalculate(last);
            gauge.HostCalculate(last);
        }

        Assert.Equal(rv, realized[last]);
        Assert.Equal(g, greed[last]);
    }

    [Fact]
    public async Task Spot_etf_flow_counts_each_published_day_once_while_a_day_forms()
    {
        var end = ProfileMergeTests.Monday.AddDays(10);
        var etf = new EtfFlows(() => end) { Provider = new DailyEtf() };
        await etf.RefreshAsync("BTC", ProfileMergeTests.Monday.AddDays(-1), CancellationToken.None);
        using var data = OptionsMacroStudyTests.Market(10);
        using var model = new ChartModel(data, ChartPeriod.D1, new ChartSettings { TimeZoneId = "UTC" });
        var flow = new SpotEtfFlow { Service = etf };
        Assert.Null(model.AddIndicator(flow, applyDefaultColors: false).Error);
        model.Update();
        model.Update();
        Assert.Null(flow.Status);
        int last = model.Series.Count - 1;
        int days = flow.PublishedDays.Count;
        Assert.True(days >= 5, $"{days} published days");
        Assert.Equal(last, flow.PublishedDays[^1].Bar);

        for (int tick = 0; tick < 5; tick++) flow.HostCalculate(last);

        Assert.Equal(days, flow.PublishedDays.Count);
        Assert.Equal(days, flow.PublishedDays.Select(d => d.Day).Distinct().Count());
    }

    private sealed class DailyEtf : IEtfFlowsProvider
    {
        public string Name => "Fixture ETF";

        public bool Supports(string asset) => asset == "BTC";

        public Task<IReadOnlyList<EtfFlowDay>> DailyAsync(string asset, DateTime from, DateTime to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EtfFlowDay>>(Enumerable.Range(0, 10)
                .Select(d => new EtfFlowDay(ProfileMergeTests.Monday.AddDays(d), (d % 3 == 0 ? -1 : 1) * (d + 1) * 1e7)).ToArray());
    }

    // ------------------------------------------------------------------ SF-5: walls keep their key while the mid moves

    [Fact]
    public void Wall_notifications_keep_a_resting_wall_when_the_mid_moves()
    {
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "fake", TickSize = 0.1m, VolumeStep = 0.001m };
        using var feed = new SimulatedFeed();
        using var market = new InstrumentData(instrument, feed) { RecordLive = false };
        for (int i = 0; i < 3; i++) market.Trades.Add(new Trade(i + 1, Start.AddMinutes(i), 100_000m, 1, TradeSide.Buy));
        using var model = new ChartModel(market, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
        var alerts = new List<AlertInfo>();
        model.AlertRaised += alerts.Add;
        var now = Start.AddHours(1);
        var study = new HighlightsAndNotifications { NotifyWalls = true, Clock = () => now };
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
        model.Update();

        // One-lot touch on each side of the mid and a 100-lot bid at 99 500 (9.95M notional, above the 5M wall size).
        DepthUpdate Book(decimal mid)
        {
            long m = (long)(mid * 10);
            return new DepthUpdate
            {
                Snapshot = new DepthSnapshot([new BookLevel(m - 5, 1), new BookLevel(995_000, 100)], [new BookLevel(m + 5, 1)], now),
                ProviderId = "fake", Venue = "venue-a", ReceivedAt = now, Sequence = 1, IsFullRefresh = true, Coverage = DepthCoverage.ReceivedChanges,
            };
        }

        study.HostDepthSnapshot(Book(100_000m)); // the first seconds of a book are not changes
        now = now.AddSeconds(11);
        study.HostDepthSnapshot(Book(100_000m));
        var appeared = Assert.Single(alerts);
        Assert.Contains("99500", appeared.Message);

        // The mid moves by 0.01 % each way; the wall itself never moves, so nothing is pulled or appears.
        foreach (var mid in new[] { 100_010m, 99_995m, 100_030m })
        {
            now = now.AddSeconds(2);
            study.HostDepthSnapshot(Book(mid));
        }

        Assert.Single(alerts);
    }

    // ------------------------------------------------------------------ SF-16: closed strikes count as closed interest

    [Fact]
    public void Open_interest_changes_count_strikes_that_closed_since_the_old_snapshot()
    {
        var t = ProfileMergeTests.Monday;
        var now = new StrikeSnapshot(t, 100, new[] { new StrikeRow(100, 15, 5, 0, 0, 0) });
        var old = new StrikeSnapshot(t.AddHours(-24), 100, new[] { new StrikeRow(100, 10, 8, 0, 0, 0), new StrikeRow(105, 7, 2, 0, 0, 0) });
        Assert.Equal(new[] { (100.0, 5.0, -3.0), (105.0, -7.0, -2.0) }, OiChanges.Changes(now, old));
    }
}
