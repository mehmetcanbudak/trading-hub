using System.Net;
using System.Text;
using System.Text.Json;
using TradingHub.App.Services.Workbench;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Market;
using static TradingHub.Tests.VenueFixtures;

namespace TradingHub.Tests;

/// <summary>
/// OKX, Bitget and Hyperliquid derivatives data, the cross-venue funding board and the dashboard's flow and liquidation
/// models. Payloads are trimmed copies of the venues' real public answers; every request is answered in-process.
/// </summary>
public sealed class DerivativesVenueTests
{
    /// <summary>2026-11-15 16:00 UTC, a funding settlement in the recorded payloads.</summary>
    private static readonly DateTime Settlement = new(2026, 11, 15, 16, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = Settlement.AddMinutes(-17);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static Dictionary<string, string> Query(Uri uri) => System.Web.HttpUtility.ParseQueryString(uri.Query) is var q
        ? q.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => q[k]!) : new();

    private static HttpResponseMessage Status(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static DerivativesData Service(Http handler) => new(new DerivativesTransport(new HttpClient(handler), OwnsHttp: true,
        BinanceRest: new Uri("https://binance.fixture"), BybitRest: new Uri("https://bybit.fixture"), UtcNow: () => Now, Delay: NoDelay,
        OkxRest: new Uri("https://okx.fixture"), BitgetRest: new Uri("https://bitget.fixture"), HyperliquidRest: new Uri("https://hyperliquid.fixture")));

    [Fact]
    public void Venue_ids_names_symbols_and_published_coverage_are_explicit()
    {
        Assert.Equal(new[] { "binance", "bybit", "okx-swap", "bitget-futures", "hyperliquid" }, DerivativesData.PerpetualVenues);
        Assert.Equal(new[] { DerivativesData.Binance, DerivativesData.Bybit }, DerivativesData.Venues); // Liquidation-stream venues are unchanged.
        Assert.Equal(new[] { "OKX", "Bitget", "Hyperliquid" },
            new[] { DerivativesData.Okx, DerivativesData.Bitget, DerivativesData.Hyperliquid }.Select(DerivativesData.VenueName));
        Assert.Equal("BTC-USDT-SWAP", DerivativesData.NativeSymbol(DerivativesData.Okx, "btcusdt"));
        Assert.Equal("PEPE-USDT-SWAP", DerivativesData.NativeSymbol(DerivativesData.Okx, "1000PEPEUSDT"));
        Assert.Equal("MOG-USDT-SWAP", DerivativesData.NativeSymbol(DerivativesData.Okx, "1000000MOGUSDT"));
        Assert.Equal("1INCH-USDT-SWAP", DerivativesData.NativeSymbol(DerivativesData.Okx, "1INCHUSDT"));
        Assert.Equal("BTCUSDT", DerivativesData.NativeSymbol(DerivativesData.Bitget, "BTCUSDT"));
        Assert.Equal("BTC", DerivativesData.NativeSymbol(DerivativesData.Hyperliquid, "BTCUSDT"));
        Assert.Equal("kPEPE", DerivativesData.NativeSymbol(DerivativesData.Hyperliquid, "1000PEPEUSDT"));

        Assert.Equal(new DerivativesVenueCoverage(true, true, true, false), DerivativesData.Coverage(DerivativesData.Okx));
        Assert.Equal(new DerivativesVenueCoverage(true, false, true, false), DerivativesData.Coverage(DerivativesData.Bitget));
        Assert.Equal(new DerivativesVenueCoverage(true, false, false, false), DerivativesData.Coverage(DerivativesData.Hyperliquid));
        Assert.True(DerivativesData.Coverage(DerivativesData.Binance).LiquidationStream);
    }

    // ================================================================ OKX

    [Fact]
    public async Task Okx_funding_pages_backwards_with_after_and_prefers_the_realized_rate()
    {
        var from = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        static string Row(DateTime t, string rate, string realized) =>
            $$"""{"formulaType":"withRate","fundingRate":"{{rate}}","fundingTime":"{{Ms(t)}}","instId":"BTC-USDT-SWAP","instType":"SWAP","method":"current_period","realizedRate":"{{realized}}"}""";
        using var handler = new Http((request, _) =>
        {
            Assert.Equal("/api/v5/public/funding-rate-history", request.RequestUri!.AbsolutePath);
            var q = Query(request.RequestUri);
            Assert.Equal(("BTC-USDT-SWAP", "400"), (q["instId"], q["limit"]));
            return q.TryGetValue("after", out var after)
                ? Json($$"""{"code":"0","data":[{{Row(from, "0.0000465375388426", "0.0000465375388426")}},{{Row(from.AddHours(-8), "0.00001", "0.00001")}}],"msg":""}""")
                : Json($$"""{"code":"0","data":[{{Row(from.AddHours(32), "-0.0000004970281698", "-0.0000004970281698")}},{{Row(from.AddHours(24), "-0.0000378743771102", "")}},{{Row(from.AddHours(16), "0.0001", "0.0002")}},{{Row(from.AddHours(8), "0.00003", "0.00003")}}],"msg":""}""");
        });
        using var data = Service(handler);

        var result = await data.LoadFundingAsync(DerivativesData.Okx, "BTCUSDT", from, Settlement, CancellationToken.None);

        Assert.Equal(OpenInterestAvailability.Available, result.Availability);
        Assert.Equal(new[] { from, from.AddHours(8), from.AddHours(16), from.AddHours(24), from.AddHours(32) }, result.Samples.Select(s => s.Time));
        // An empty realizedRate falls back to fundingRate; a settled row uses realizedRate.
        Assert.Equal(new[] { 0.00465375388426m, 0.003m, 0.02m, -0.00378743771102m, -0.00004970281698m }, result.Samples.Select(s => s.RatePercent));
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(Ms(from.AddHours(8)).ToString(), Query(requests[1].Uri)["after"]);
        Assert.All(requests, r => Assert.Equal("okx.fixture", r.Uri.Host));
    }

    [Fact]
    public async Task Okx_open_interest_uses_history_in_base_units_plus_the_current_value_with_a_period_fitting_100_rows()
    {
        var from = Now.AddHours(-24);
        var first = new DateTime(from.Ticks - from.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc).AddMinutes(15);
        using var handler = new Http((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v5/rubik/stat/contracts/open-interest-history" => Json($$"""{"code":"0","data":[["{{Ms(Now.AddMinutes(-3))}}","3248522.96000001304","32485.2296000001304","2102119207.416008438184"],["{{Ms(first)}}","3249557.83000001274","32495.5783000001274","2099279349.3366082302948"]],"msg":""}"""),
            "/api/v5/public/open-interest" => Json($$"""{"code":"0","data":[{"instId":"BTC-USDT-SWAP","instType":"SWAP","oi":"3248658.32000001276","oiCcy":"32486.5832000001276","oiUsd":"2103470526.95848826195964","ts":"{{Ms(Now)}}"}],"msg":""}"""),
            _ => throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri),
        });
        using var data = Service(handler);

        var result = await data.LoadOpenInterestAsync(DerivativesData.Okx, "BTCUSDT", from, Now, CancellationToken.None);

        Assert.Equal(OpenInterestAvailability.Available, result.Availability);
        Assert.Equal(new[] { 32495.5783000001274m, 32485.2296000001304m, 32486.5832000001276m }, result.Samples.Select(s => s.Quantity));
        var history = Query(handler.Requests.First().Uri);
        Assert.Equal(("BTC-USDT-SWAP", "15m", "100", Ms(from).ToString(), Ms(Now).ToString()),
            (history["instId"], history["period"], history["limit"], history["begin"], history["end"]));
        Assert.Contains(handler.Requests, r => r.Uri.AbsolutePath == "/api/v5/public/open-interest" && Query(r.Uri)["instType"] == "SWAP");

        // Longer ranges widen the period so 100 rows still cover them.
        handler.Requests.Clear();
        await data.LoadOpenInterestAsync(DerivativesData.Okx, "BTCUSDT", Now.AddHours(-72), Now, CancellationToken.None);
        Assert.Equal("1H", Query(handler.Requests.First().Uri)["period"]);
        handler.Requests.Clear();
        var week = await data.LoadOpenInterestAsync(DerivativesData.Okx, "BTCUSDT", Now.AddDays(-7), Now, CancellationToken.None);
        Assert.Equal("2H", Query(handler.Requests.First().Uri)["period"]);
        Assert.Equal(OpenInterestAvailability.Partial, week.Availability); // The fixture's history starts a day ago, not a week ago.
    }

    [Fact]
    public async Task Okx_premium_long_short_and_venue_errors_follow_the_published_shapes()
    {
        using var handler = new Http((request, _) => (request.RequestUri!.AbsolutePath, Query(request.RequestUri).GetValueOrDefault("instId")) switch
        {
            ("/api/v5/public/mark-price", "BTC-USDT-SWAP") => Json("""{"code":"0","data":[{"instId":"BTC-USDT-SWAP","instType":"SWAP","markPx":"64735.6","ts":"1794756977991"}],"msg":""}"""),
            ("/api/v5/market/index-tickers", "BTC-USDT") => Json("""{"code":"0","data":[{"instId":"BTC-USDT","idxPx":"64767.5","high24h":"66186.8","low24h":"62034.9","open24h":"65418.4","sodUtc0":"64148.9","sodUtc8":"65747.2","ts":"1794756977740"}],"msg":""}"""),
            ("/api/v5/public/funding-rate", "BTC-USDT-SWAP") => Json("""{"code":"0","data":[{"formulaType":"withRate","fundingRate":"0.0000134820001958","fundingTime":"1794758400000","instId":"BTC-USDT-SWAP","instType":"SWAP","method":"current_period","nextFundingRate":"","nextFundingTime":"1794787200000","premium":"-0.0005299466190796","ts":"1794756902843"}],"msg":""}"""),
            ("/api/v5/rubik/stat/contracts/long-short-account-ratio-contract", _) => Json($$"""{"code":"0","data":[["{{Ms(Now.AddMinutes(-13))}}","2.2528827188590502"],["{{Ms(Now.AddMinutes(-28))}}","3"]],"msg":""}"""),
            (_, "NOPE-USDT-SWAP") => Json("""{"code":"51001","data":[],"msg":"Instrument ID, Instrument ID code, or Spread ID doesn't exist."}"""),
            _ => throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri),
        });
        using var data = Service(handler);

        var stats = await data.LoadMarketStatsAsync(DerivativesData.Okx, "BTCUSDT", CancellationToken.None);
        Assert.Equal((DerivativesData.Okx, 64735.6m, 64767.5m, 0.00134820001958m, Settlement), (stats.VenueId, stats.MarkPrice, stats.IndexPrice, stats.FundingRatePercent, stats.NextFundingUtc));
        Assert.Equal((64735.6m - 64767.5m) / 64767.5m * 100, stats.PremiumPercent);

        var ls = await data.LoadLongShortAsync(DerivativesData.Okx, "BTCUSDT", Now.AddHours(-24), Now, CancellationToken.None);
        Assert.Equal(OpenInterestAvailability.Available, ls.Availability);
        Assert.Equal(new[] { 0.75m, 0.25m }, new[] { ls.Samples[0].LongShare, ls.Samples[0].ShortShare });
        Assert.True(Math.Abs(ls.Samples[1].Ratio!.Value - 2.2528827188590502m) < 1e-20m);
        var lsQuery = Query(handler.Requests.Last().Uri);
        Assert.Equal(("BTC-USDT-SWAP", "15m", "100"), (lsQuery["instId"], lsQuery["period"], lsQuery["limit"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => data.LoadMarketStatsAsync(DerivativesData.Okx, "NOPEUSDT", CancellationToken.None));
        Assert.Equal("Instrument ID, Instrument ID code, or Spread ID doesn't exist.", error.Message);
    }

    // ================================================================ Bitget

    [Fact]
    public async Task Bitget_funding_pages_by_number_and_open_interest_is_current_only()
    {
        var newest = new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);
        using var handler = new Http((request, _) =>
        {
            var q = Query(request.RequestUri!);
            Assert.Equal("USDT-FUTURES", q["productType"]);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/v2/mix/market/history-fund-rate":
                    int page = int.Parse(q["pageNo"]);
                    Assert.Equal(("BTCUSDT", "100"), (q["symbol"], q["pageSize"]));
                    var rows = Enumerable.Range((page - 1) * 100, page == 1 ? 100 : 30)
                        .Select(i => $$"""{"symbol":"BTCUSDT","fundingRate":"{{(i % 2 == 0 ? "-0.000055" : "0.000057")}}","fundingTime":"{{Ms(newest.AddHours(-8 * i))}}"}""");
                    return Json($$"""{"code":"00000","msg":"success","requestTime":1794756980349,"data":[{{string.Join(",", rows)}}]}""");
                case "/api/v2/mix/market/open-interest":
                    return Json($$$"""{"code":"00000","msg":"success","requestTime":1794756981622,"data":{"openInterestList":[{"symbol":"BTCUSDT","size":"36789.194400000104"}],"ts":"{{{Ms(Now)}}}"}}""");
                default:
                    throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri);
            }
        });
        using var data = Service(handler);

        var funding = await data.LoadFundingAsync(DerivativesData.Bitget, "BTCUSDT", newest.AddDays(-60), Settlement, CancellationToken.None);
        Assert.Equal(130, funding.Samples.Count);
        Assert.Equal((newest.AddHours(-8 * 129), newest), (funding.Samples[0].Time, funding.Samples[^1].Time));
        Assert.Equal(-0.0055m, funding.Samples[^1].RatePercent);
        Assert.Equal(new[] { "1", "2" }, handler.Requests.Select(r => Query(r.Uri)["pageNo"]));

        var oi = await data.LoadOpenInterestAsync(DerivativesData.Bitget, "BTCUSDT", Now.AddHours(-24), Now, CancellationToken.None);
        // One current value is partial coverage of the range; the 1h/24h change stays unknown.
        Assert.Equal(OpenInterestAvailability.Partial, oi.Availability);
        Assert.Equal((Now, 36789.194400000104m), (Assert.Single(oi.Samples).Time, oi.Samples[0].Quantity));
        Assert.Null(oi.Message);
    }

    [Fact]
    public async Task Bitget_premium_long_short_and_errors_follow_the_published_shapes()
    {
        using var handler = new Http((request, _) => (request.RequestUri!.AbsolutePath, Query(request.RequestUri).GetValueOrDefault("symbol")) switch
        {
            ("/api/v2/mix/market/ticker", "BTCUSDT") => Json("""{"code":"00000","msg":"success","requestTime":1794756980763,"data":[{"symbol":"BTCUSDT","lastPr":"64731.6","askPr":"64731.6","bidPr":"64731.5","ts":"1794756980764","indexPrice":"64769.833478260869565","fundingRate":"0.00001","holdingAmount":"36789.194400000104","deliveryStartTime":null,"deliveryTime":null,"deliveryStatus":"","markPrice":"64731.6"}]}"""),
            ("/api/v2/mix/market/current-fund-rate", "BTCUSDT") => Json("""{"code":"00000","msg":"success","requestTime":1794756979939,"data":[{"symbol":"BTCUSDT","fundingRate":"0.00001","fundingRateInterval":"8","nextUpdate":"1794758400000","minFundingRate":"-0.003","maxFundingRate":"0.003"}]}"""),
            ("/api/v2/mix/market/account-long-short", "BTCUSDT") => Json($$"""{"code":"00000","msg":"success","requestTime":1794756982055,"data":[{"longAccountRatio":"0.7952","shortAccountRatio":"0.2048","longShortAccountRatio":"3.8828","ts":"{{Ms(Now.AddDays(-3))}}"},{"longAccountRatio":"0.8","shortAccountRatio":"0.2","longShortAccountRatio":"4","ts":"{{Ms(Now.AddHours(-1))}}"}]}"""),
            (_, "NOPEUSDT") => Status(HttpStatusCode.BadRequest, """{"code":"40034","msg":"Parameter NOPEUSDT does not exist","requestTime":1794757002319,"data":null}"""),
            (_, "BADUSDT") => Json("""{"code":"40019","msg":"Parameter symbol cannot be empty","requestTime":1794757002319,"data":null}"""),
            _ => throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri),
        });
        using var data = Service(handler);

        var stats = await data.LoadMarketStatsAsync(DerivativesData.Bitget, "BTCUSDT", CancellationToken.None);
        Assert.Equal((DerivativesData.Bitget, 64731.6m, 64769.833478260869565m, 0.001m, Settlement), (stats.VenueId, stats.MarkPrice, stats.IndexPrice, stats.FundingRatePercent, stats.NextFundingUtc));

        var ls = await data.LoadLongShortAsync(DerivativesData.Bitget, "BTCUSDT", Now.AddHours(-24), Now, CancellationToken.None);
        Assert.Equal(4m, Assert.Single(ls.Samples).Ratio); // The three-day-old row is outside the range.
        Assert.Equal("1h", Query(handler.Requests.Last().Uri)["period"]);
        await data.LoadLongShortAsync(DerivativesData.Bitget, "BTCUSDT", Now.AddDays(-7), Now, CancellationToken.None);
        Assert.Equal("6h", Query(handler.Requests.Last().Uri)["period"]); // The venue answers 30 periods.

        var http = await Assert.ThrowsAsync<HttpRequestException>(() => data.LoadMarketStatsAsync(DerivativesData.Bitget, "NOPEUSDT", CancellationToken.None));
        Assert.Equal("400: Parameter NOPEUSDT does not exist", http.Message);
        var code = await Assert.ThrowsAsync<InvalidOperationException>(() => data.LoadFundingAsync(DerivativesData.Bitget, "BADUSDT", Now.AddDays(-1), Now, CancellationToken.None));
        Assert.Equal("Parameter symbol cannot be empty", code.Message);
    }

    // ================================================================ Hyperliquid

    private const string HyperliquidContexts = """
        [{"universe":[{"szDecimals":5,"name":"BTC","maxLeverage":40,"marginTableId":56},{"szDecimals":1,"name":"MATIC","maxLeverage":20,"marginTableId":20,"isDelisted":true},{"szDecimals":0,"name":"kPEPE","maxLeverage":10,"marginTableId":10}]},
         [{"funding":"0.0000125","openInterest":"37277.79216","prevDayPx":"65716.0","dayNtlVlm":"7459954888.4141139984","premium":"-0.0004631845","oraclePx":"64769.0","markPx":"64738.0","midPx":"64738.5","impactPxs":["64738.0","64739.0"],"dayBaseVlm":"115790.9757300001"},
          {"funding":"0.0","openInterest":"0.0","prevDayPx":"0.2","dayNtlVlm":"0.0","premium":null,"oraclePx":"0.2","markPx":"0.2","midPx":null,"impactPxs":null},
          {"funding":"-0.0000031","openInterest":"5530291604","prevDayPx":"0.0091","dayNtlVlm":"1000.0","premium":"0.0","oraclePx":"0.009","markPx":"0.0091","midPx":"0.0091","impactPxs":["0.009","0.0092"]}]]
        """;

    [Fact]
    public async Task Hyperliquid_posts_info_requests_pages_hourly_funding_and_reads_current_context()
    {
        var from = Settlement.AddDays(-30);
        using var handler = new Http((request, body) =>
        {
            Assert.Equal((HttpMethod.Post, "/info"), (request.Method, request.RequestUri!.AbsolutePath));
            using var doc = JsonDocument.Parse(body!);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() == "metaAndAssetCtxs") return Json(HyperliquidContexts);
            Assert.Equal(("fundingHistory", "BTC"), (root.GetProperty("type").GetString(), root.GetProperty("coin").GetString()));
            var start = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("startTime").GetInt64()).UtcDateTime;
            var end = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("endTime").GetInt64()).UtcDateTime;
            // The venue answers at most 500 settlements, oldest first, a few milliseconds after each hour.
            var hour = new DateTime(start.Ticks - start.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
            if (Ms(hour) + 21 < Ms(start)) hour = hour.AddHours(1);
            var rows = Enumerable.Range(0, 500).Select(i => hour.AddHours(i)).Where(t => Ms(t) + 21 <= Ms(end))
                .Select(t => $$"""{"coin":"BTC","fundingRate":"0.0000125","premium":"-0.0001469453","time":{{Ms(t) + 21}}}""");
            return Json("[" + string.Join(",", rows) + "]");
        });
        using var data = Service(handler);

        var funding = await data.LoadFundingAsync(DerivativesData.Hyperliquid, "BTCUSDT", from, Settlement, CancellationToken.None);
        Assert.Equal(720, funding.Samples.Count);
        Assert.All(funding.Samples, s => Assert.Equal(0.00125m, s.RatePercent));
        Assert.Equal(1, DerivativesDashboard.FundingIntervalHours(funding.Samples));
        var bodies = handler.Requests.Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("startTime").GetInt64()).ToArray();
        Assert.Equal(new[] { Ms(from), Ms(from.AddHours(499)) + 22 }, bodies);

        var stats = await data.LoadMarketStatsAsync(DerivativesData.Hyperliquid, "BTCUSDT", CancellationToken.None);
        // The oracle price is the index; funding settles on the next full hour.
        Assert.Equal((64738.0m, 64769.0m, 0.00125m, Settlement, Now), (stats.MarkPrice, stats.IndexPrice, stats.FundingRatePercent, stats.NextFundingUtc, stats.Time));
        var oi = await data.LoadOpenInterestAsync(DerivativesData.Hyperliquid, "1000PEPEUSDT", Now.AddHours(-24), Now, CancellationToken.None);
        Assert.Equal(OpenInterestAvailability.Partial, oi.Availability);
        Assert.Equal((Now, 5530291604m), (oi.Samples[0].Time, oi.Samples[0].Quantity));
        // A delisted or absent coin is an error, not a zero.
        await Assert.ThrowsAsync<InvalidDataException>(() => data.LoadMarketStatsAsync(DerivativesData.Hyperliquid, "MATICUSDT", CancellationToken.None));
        Assert.Equal(OpenInterestAvailability.Unavailable, (await data.LoadOpenInterestAsync(DerivativesData.Hyperliquid, "ETHUSDT", Now.AddHours(-1), Now, CancellationToken.None)).Availability);
    }

    [Fact]
    public async Task Hyperliquid_long_short_is_unsupported_without_a_request_and_unknown_coins_fail_honestly()
    {
        using var handler = new Http((_, _) => Status(HttpStatusCode.InternalServerError, "null"));
        using var data = Service(handler);

        var ls = await data.LoadLongShortAsync(DerivativesData.Hyperliquid, "BTCUSDT", Now.AddDays(-1), Now, CancellationToken.None);
        Assert.Equal(OpenInterestAvailability.Unsupported, ls.Availability);
        Assert.Empty(ls.Samples);
        Assert.Equal("Hyperliquid does not publish a long/short account ratio.", ls.Message);
        Assert.Empty(handler.Requests);
        // The venue answers an unknown coin's funding history with HTTP 500 and a null body.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => data.LoadFundingAsync(DerivativesData.Hyperliquid, "NOPEUSDT", Now.AddDays(-1), Now, CancellationToken.None));
        Assert.Equal("500: null", error.Message);
    }

    // ================================================================ funding board

    [Fact]
    public async Task Funding_board_reads_each_venue_in_bulk_with_its_settlement_interval()
    {
        using var handler = new Http((request, _) => (request.RequestUri!.Host, request.RequestUri.AbsolutePath) switch
        {
            ("binance.fixture", "/fapi/v1/premiumIndex") => Json("""[{"symbol":"BTCUSDT","markPrice":"64747.50000000","indexPrice":"64766.99217391","estimatedSettlePrice":"64692.58311993","lastFundingRate":"-0.00000248","interestRate":"0.00010000","nextFundingTime":1794758400000,"time":1794756984000},{"symbol":"GTCUSDT","markPrice":"0.1","indexPrice":"0.1","lastFundingRate":"0.0001","nextFundingTime":1794758400000,"time":1794756984000}]"""),
            ("binance.fixture", "/fapi/v1/fundingInfo") => Json("""[{"symbol":"GTCUSDT","adjustedFundingRateCap":"0.02000000","adjustedFundingRateFloor":"-0.02000000","fundingIntervalHours":4,"disclaimer":false,"updateTime":1791950460152}]"""),
            ("bybit.fixture", "/v5/market/tickers") => Json("""{"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[{"symbol":"BTCUSDT","lastPrice":"64747.30","indexPrice":"64776.95","markPrice":"64747.20","fundingRate":"-0.00001358","nextFundingTime":"1794758400000","fundingIntervalHour":"8","fundingCap":"0.00333"},{"symbol":"BTCUSDT-26DEC25","fundingRate":"","nextFundingTime":"0","fundingIntervalHour":""}]},"retExtInfo":{},"time":1794756984000}"""),
            ("okx.fixture", "/api/v5/public/funding-rate") => Json("""{"code":"0","data":[{"fundingRate":"0.0000134820001958","fundingTime":"1794758400000","instId":"BTC-USDT-SWAP","nextFundingTime":"1794787200000"},{"fundingRate":"0.00005","fundingTime":"1794758400000","instId":"GTC-USDT-SWAP","nextFundingTime":""}],"msg":""}"""),
            ("bitget.fixture", "/api/v2/mix/market/current-fund-rate") => Json("""{"code":"00000","msg":"success","requestTime":1794757061722,"data":[{"symbol":"BTCUSDT","fundingRate":"0.00001","fundingRateInterval":"8","nextUpdate":"1794758400000"},{"symbol":"PEPEUSDT","fundingRate":"-0.0001","fundingRateInterval":"4","nextUpdate":"1794758400000"}]}"""),
            ("hyperliquid.fixture", "/info") => Json(HyperliquidContexts),
            _ => throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri),
        });
        using var data = Service(handler);
        string[] symbols = { "BTCUSDT", "GTCUSDT", "1000PEPEUSDT" };

        var binance = await data.LoadFundingBoardAsync(DerivativesData.Binance, symbols, CancellationToken.None);
        Assert.Equal((-0.000248m, 8d, Settlement), (binance["BTCUSDT"].RatePercent, binance["BTCUSDT"].IntervalHours, binance["BTCUSDT"].NextFundingUtc));
        Assert.Equal((4d, 0.02m), (binance["GTCUSDT"].IntervalHours, binance["GTCUSDT"].Rate8hPercent)); // fundingInfo lists only adjusted intervals.
        Assert.False(binance.ContainsKey("1000PEPEUSDT"));
        var bybit = await data.LoadFundingBoardAsync(DerivativesData.Bybit, symbols, CancellationToken.None);
        Assert.Equal(-0.001358m, Assert.Single(bybit).Value.Rate8hPercent);
        var okx = await data.LoadFundingBoardAsync(DerivativesData.Okx, symbols, CancellationToken.None);
        Assert.Equal((8d, Settlement), (okx["BTCUSDT"].IntervalHours, okx["BTCUSDT"].NextFundingUtc));
        Assert.False(okx.ContainsKey("GTCUSDT")); // Without the following settlement the interval is unknown, so the row is left out.
        Assert.Equal("ANY", Query(handler.Requests.Single(r => r.Uri.Host == "okx.fixture").Uri)["instId"]);
        var bitget = await data.LoadFundingBoardAsync(DerivativesData.Bitget, symbols, CancellationToken.None);
        Assert.Equal(-0.02m, bitget["1000PEPEUSDT"].Rate8hPercent); // Listed as PEPEUSDT on Bitget.
        var hyperliquid = await data.LoadFundingBoardAsync(DerivativesData.Hyperliquid, symbols, CancellationToken.None);
        Assert.Equal((0.00125m, 1d, 0.01m), (hyperliquid["BTCUSDT"].RatePercent, hyperliquid["BTCUSDT"].IntervalHours, hyperliquid["BTCUSDT"].Rate8hPercent));
        Assert.Equal(-0.00248m, hyperliquid["1000PEPEUSDT"].Rate8hPercent);
        Assert.Equal(6, handler.Requests.Count); // Bulk endpoints only: two for Binance, one per other venue.
    }

    [Fact]
    public void Board_rows_keep_unknown_cells_out_of_the_annualized_spread()
    {
        Assert.Equal(new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "DOGEUSDT", "1000PEPEUSDT" },
            DerivativesDashboard.BoardSymbols(DerivativesDashboard.BoardCoins, "1000pepeusdt").Select(s => s.Symbol));
        Assert.Equal(5, DerivativesDashboard.BoardSymbols(DerivativesDashboard.BoardCoins, "ETHUSDT").Count);
        Assert.Equal("1000PEPE", DerivativesDashboard.BoardSymbols(DerivativesDashboard.BoardCoins, "1000PEPEUSDT")[^1].Coin);

        var boards = new Dictionary<string, IReadOnlyDictionary<string, FundingSnapshot>?>
        {
            [DerivativesData.Binance] = new Dictionary<string, FundingSnapshot> { ["BTCUSDT"] = new(DerivativesData.Binance, "BTCUSDT", 0.01m, 8, null) },
            [DerivativesData.Bybit] = new Dictionary<string, FundingSnapshot> { ["BTCUSDT"] = new(DerivativesData.Bybit, "BTCUSDT", 0.02m, 4, null) },
            [DerivativesData.Okx] = null,
            [DerivativesData.Bitget] = new Dictionary<string, FundingSnapshot>(),
        };
        var row = DerivativesDashboard.BuildBoardRow("BTC", "BTCUSDT", DerivativesData.PerpetualVenues, boards);
        Assert.Equal(new[] { FundingBoardCellState.Value, FundingBoardCellState.Value, FundingBoardCellState.Failed, FundingBoardCellState.NotListed, FundingBoardCellState.Pending },
            row.Cells.Select(c => c.State));
        Assert.Equal(new decimal?[] { 0.01m, 0.04m, null, null, null }, row.Cells.Select(c => c.Rate8hPercent));
        Assert.Equal((0.04m - 0.01m) * 3 * 365, row.SpreadAnnualizedPercent);
        Assert.Equal((DerivativesData.Bybit, DerivativesData.Binance), (row.HighVenue, row.LowVenue));

        var single = DerivativesDashboard.BuildBoardRow("BTC", "BTCUSDT", DerivativesData.PerpetualVenues,
            new Dictionary<string, IReadOnlyDictionary<string, FundingSnapshot>?> { [DerivativesData.Binance] = boards[DerivativesData.Binance] });
        Assert.Null(single.SpreadAnnualizedPercent);
        Assert.Null(DerivativesDashboard.AnnualizedSpread(new decimal?[] { 0.01m, null }));
        Assert.Equal(0.02m * 1095, DerivativesDashboard.AnnualizedSpread(new decimal?[] { -0.01m, null, 0.01m }));
        Assert.Equal((0.5, 1.0, 1.0), (DerivativesDashboard.Heat(0.025m), DerivativesDashboard.Heat(-0.05m), DerivativesDashboard.Heat(0.2m)));
    }

    // ================================================================ dashboard models

    [Fact]
    public void Price_and_cvd_use_measured_sides_only_and_keep_unknown_steps_as_gaps()
    {
        var t0 = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var raw = new TradeProvenance(DataQuality.Raw);
        var inferred = new TradeProvenance(DataQuality.Raw | DataQuality.InferredSide);
        var compact = new TradeProvenance(DataQuality.Compact, ResolutionSeconds: 60, ObservedTradeCount: 3);
        var trades = new[]
        {
            new FlowTrade(t0.AddSeconds(5), 100, 2, TradeSide.Buy, raw),
            new FlowTrade(t0.AddSeconds(30), 101, 1, TradeSide.Sell, raw),
            // minute 1: nothing recorded
            new FlowTrade(t0.AddMinutes(2), 102, 5, TradeSide.Buy, inferred),
            new FlowTrade(t0.AddMinutes(2).AddSeconds(1), 103, 1, TradeSide.Sell, raw),
            new FlowTrade(t0.AddMinutes(3), 104, 3, TradeSide.None, raw),
            new FlowTrade(t0.AddMinutes(4), 105, 4, TradeSide.Buy, compact),
            new FlowTrade(t0.AddMinutes(9), 999, 1, TradeSide.Buy, raw), // outside the range
        };

        var flow = DerivativesDashboard.PriceAndCvd(trades, t0, t0.AddMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Equal(new double?[] { 101, null, 103, 104, 105 }, flow.Price);
        Assert.Equal(new double?[] { 1, null, 0, null, 4 }, flow.Cvd);
        Assert.Equal(new[] { false, false, true, true, true }, flow.Flagged);
        Assert.Equal((3 + 1 + 4m, 5 + 3m, 3, t0.AddSeconds(5)), (flow.MeasuredVolume, flow.UnmeasuredVolume, flow.FlaggedSteps, flow.FirstTrade));

        var empty = DerivativesDashboard.PriceAndCvd(Array.Empty<FlowTrade>(), t0, t0.AddMinutes(3), TimeSpan.FromMinutes(1));
        Assert.All(empty.Price.Concat(empty.Cvd), v => Assert.Null(v));
        Assert.Null(empty.FirstTrade);
    }

    [Fact]
    public void Liquidation_feed_is_newest_first_above_the_minimum_and_names_observing_streams()
    {
        var t0 = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var events = new[]
        {
            new LiquidationEvent(t0, DerivativesData.Binance, 100, 50, true),
            new LiquidationEvent(t0.AddSeconds(2), DerivativesData.Bybit, 100, 1, false),
            new LiquidationEvent(t0.AddSeconds(4), DerivativesData.Bybit, 200, 100, false),
        };
        Assert.Equal(new[] { 20_000m, 5_000m }, DerivativesDashboard.LiquidationFeed(events, 5_000, 10).Select(e => e.Notional));
        Assert.Single(DerivativesDashboard.LiquidationFeed(events, 0, 1));

        var windows = new[] { new ObservedWindow(DerivativesData.Binance, t0.AddHours(-1), t0.AddSeconds(-30)), new ObservedWindow(DerivativesData.Bybit, t0.AddHours(-2), t0.AddHours(-1)) };
        Assert.Equal(new[] { (DerivativesData.Binance, true), (DerivativesData.Bybit, false) },
            DerivativesDashboard.StreamCoverage(windows, DerivativesData.Venues, t0, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Rows_show_unpublished_metrics_as_gaps_without_repeating_them_as_notes()
    {
        var now = Settlement;
        var unsupported = new LongShortResult(Array.Empty<LongShortSample>(), OpenInterestAvailability.Unsupported, "Hyperliquid does not publish a long/short account ratio.");
        var oi = new OpenInterestHistoryResult(new[] { new OpenInterestSample(now.AddMinutes(-1), 10) }, now.AddMinutes(-1), now.AddMinutes(-1), OpenInterestAvailability.Partial);
        var stats = new VenueMarketStats(DerivativesData.Hyperliquid, now, 101, 100, 0.00125m, now.AddHours(1));
        var row = DerivativesDashboard.BuildRow(new DerivativesVenueInputs(DerivativesData.Hyperliquid, null, oi, stats, unsupported, null, Array.Empty<string>()), now);
        Assert.Empty(row.Notes);
        Assert.Null(row.LongShortRatio);
        Assert.Equal((10m, 1010m), (row.OpenInterest, row.OpenInterestUsd));
        Assert.Null(row.OpenInterestChange1hPercent);
        Assert.Null(row.OpenInterestChange24hPercent);
        Assert.Equal(1m, row.PremiumPercent);

        var failed = new LongShortResult(Array.Empty<LongShortSample>(), OpenInterestAvailability.Unavailable, "Bitget reports no long/short ratio for BTCUSDT in this range.");
        Assert.Contains(failed.Message, DerivativesDashboard.BuildRow(new DerivativesVenueInputs(DerivativesData.Bitget, null, null, null, failed, null, Array.Empty<string>()), now).Notes);
    }
}
