using System.Text.Json;
using SkiaSharp;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.External;
using TradingHub.Indicators;
using TradingHub.Indicators.Native.Macro;
using TradingHub.Indicators.Native.Options;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Option formulas with hand-computed values (Black–Scholes, zero rate, t = days / 365).</summary>
public sealed class OptionsMathTests
{
    private static readonly DateTime T0 = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    private static OptionContract C(double strike, OptionRight right, double oi, double iv = 0.5, double gamma = 0, double delta = 0, double size = 1, int days = 30) =>
        new($"X-{strike}-{right}", T0.AddDays(days), strike, right, 0, 0, 0, iv, delta, gamma, 0, 0, oi, 0, size);

    [Fact]
    public void Black_scholes_matches_hand_values()
    {
        // S = K = 100, t = 30/365, σ = 0.5: d1 = 0.0716728, N(d1) = 0.5285688, Γ = φ(d1) / (S σ √t) = 0.0277594.
        var (dc, gc) = OptionsMath.Greeks(100, 100, 30 / 365.0, 0.5, OptionRight.Call);
        var (dp, gp) = OptionsMath.Greeks(100, 100, 30 / 365.0, 0.5, OptionRight.Put);
        Assert.Equal(0.5285688, dc, 6);
        Assert.Equal(-0.4714312, dp, 6);
        Assert.Equal(0.0277594, gc, 6);
        Assert.Equal(gc, gp, 12);
        Assert.Equal(0.1140798, OptionsMath.Vega(100, 100, 30 / 365.0, 0.5), 6);
        Assert.Equal(0.4714312, OptionsMath.ProbAbove(100, 100, 30 / 365.0, 0.5), 6);
        // Out of the money: S = 100, K = 110, t = 0.25, σ = 0.4.
        var (d, g) = OptionsMath.Greeks(100, 110, 0.25, 0.4, OptionRight.Call);
        Assert.Equal(0.3532537, d, 6);
        Assert.Equal(0.0185819, g, 6);
        Assert.Equal(0.2821214, OptionsMath.ProbAbove(100, 110, 0.25, 0.4), 6);
        Assert.Equal(0.9750021, OptionsMath.Cdf(1.96), 6);
        // Degenerate inputs give intrinsic delta and no gamma.
        Assert.Equal((1.0, 0.0), OptionsMath.Greeks(120, 100, 0, 0.5, OptionRight.Call));
    }

    [Fact]
    public void Gamma_exposure_uses_open_interest_multiplier_and_spot_squared()
    {
        // 0.0001 × 10 contracts × 1 × 50,000² × 1 % = $25,000 per 1 % move; puts count negative.
        var chain = new OptionsChain("BTC", T0, 50_000, new[] { C(50_000, OptionRight.Call, 10, gamma: 0.0001), C(48_000, OptionRight.Put, 10, gamma: 0.0001) });
        var gex = OptionsMath.GexByStrike(chain);
        Assert.Equal(25_000, gex[50_000], 6);
        Assert.Equal(-25_000, gex[48_000], 6);
        Assert.Equal(0, OptionsMath.TotalGex(chain), 6);
        // Equity multiplier: 0.0001 × 10 × 100 × 100² × 1 % = $10.
        var spy = new OptionsChain("SPY", T0, 100, new[] { C(100, OptionRight.Call, 10, gamma: 0.0001, size: 100) });
        Assert.Equal(10, OptionsMath.TotalGex(spy), 9);
        // Delta exposure in USD: Δ × OI × multiplier × S.
        Assert.Equal(0.5 * 10 * 100 * 100, GreekExposure.Value(GreekExposure.Greek.Delta, C(100, OptionRight.Call, 10, delta: 0.5, size: 100), spy), 9);
    }

    [Fact]
    public void Max_pain_walls_and_gamma_flip()
    {
        // Payouts: K=90 → puts (100−90)×10 + (110−90)×5 = 200; K=100 → 50; K=110 → calls (110−100)×10 = 100.
        var pain = new OptionsChain("X", T0, 100, new[] { C(100, OptionRight.Call, 10), C(100, OptionRight.Put, 10), C(110, OptionRight.Put, 5) });
        Assert.Equal(100, OptionsMath.MaxPain(pain, null));
        Assert.True(double.IsNaN(OptionsMath.MaxPain(pain with { Contracts = Array.Empty<OptionContract>() }, null)));

        // Equal OI at 90 puts and 110 calls: gammas match where |d1| matches, s = √(90·110) · e^(−σ²t/2) = 98.4818.
        var chain = new OptionsChain("X", T0, 100, new[] { C(110, OptionRight.Call, 100), C(90, OptionRight.Put, 100) });
        double flip = OptionsMath.GammaFlip(chain);
        Assert.InRange(flip, 98.2, 98.8);
        Assert.True(OptionsMath.TotalGex(chain, flip - 1) < 0 && OptionsMath.TotalGex(chain, flip + 1) > 0);
        var (call, put) = OptionsMath.Walls(chain);
        Assert.Equal(110, call);
        Assert.Equal(90, put);
        // One-sided chains have no flip and no wall on the missing side.
        var calls = chain with { Contracts = new[] { C(110, OptionRight.Call, 100) } };
        Assert.True(double.IsNaN(OptionsMath.GammaFlip(calls)));
        Assert.True(double.IsNaN(OptionsMath.Walls(calls).Put));
    }

    [Fact]
    public void Skew_atm_iv_expected_move_rank_and_realized_volatility()
    {
        var e = T0.AddDays(7);
        var chain = new OptionsChain("BTC", T0, 50_000, new[]
        {
            new OptionContract("C", e, 50_000, OptionRight.Call, 0, 0, 0, 0.62, 0.52, 0, 0, 0, 1, 0, 1),
            new OptionContract("P", e, 50_000, OptionRight.Put, 0, 0, 0, 0.58, -0.48, 0, 0, 0, 1, 0, 1),
            new OptionContract("C25", e, 55_000, OptionRight.Call, 0, 0, 0, 0.50, 0.25, 0, 0, 0, 1, 0, 1),
            new OptionContract("P25", e, 45_000, OptionRight.Put, 0, 0, 0, 0.60, -0.25, 0, 0, 0, 1, 0, 1),
        });
        Assert.Equal(0.60, OptionsMath.AtmIv(chain, e), 9);
        Assert.Equal(-10, OptionsMath.RiskReversal25(chain, e), 9);
        // 50,000 × 0.60 × √(7/365) = 4,154.5486.
        Assert.Equal(4154.5486, OptionsMath.ExpectedMove(chain, e), 3);
        Assert.Equal(e, OptionsMath.ExpiryNear(chain, 30));
        Assert.Equal(50, OptionsMath.Rank(new[] { 0.4, 0.8 }, 0.6), 9);
        Assert.Equal(75, OptionsMath.Percentile(new[] { 0.4, 0.6 }, 0.6), 9);
        Assert.True(double.IsNaN(OptionsMath.Rank(new[] { 0.4 }, 0.6)));
        // Closes 100, 101, 99, 102: sample deviation of log returns × √365 = 0.4794398.
        Assert.Equal(0.4794398, OptionsMath.RealizedVol(new double[] { 100, 101, 99, 102 }, 365), 6);
    }

    [Fact]
    public void Contract_odds_and_probability_cloud_quantiles()
    {
        var e = T0.AddDays(30);
        var call = new OptionContract("C", e, 110, OptionRight.Call, 0, 0, 5, 0.5, 0, 0, 0, 0, 1, 0, 1);
        var chain = new OptionsChain("X", T0, 100, new[] { call, new OptionContract("P", e, 100, OptionRight.Put, 0, 0, 5, 0.5, 0, 0, 0, 0, 1, 0, 1) });
        var (be, win, itm, loss) = ContractOdds.Odds(chain, call, buy: true, premium: 5, iv: 0.5);
        Assert.Equal(115, be);
        Assert.Equal(OptionsMath.ProbAbove(100, 115, 30 / 365.0, 0.5), win, 12);
        Assert.Equal(OptionsMath.ProbAbove(100, 110, 30 / 365.0, 0.5), itm, 12);
        Assert.Equal(5, loss);
        // A flat smile gives a lognormal: the median is S·e^(−σ²t/2).
        var flat = new OptionsChain("X", T0, 100, new[]
        {
            new OptionContract("C", e, 100, OptionRight.Call, 0, 0, 0, 0.5, 0, 0, 0, 0, 1, 0, 1),
            new OptionContract("P", e, 100, OptionRight.Put, 0, 0, 0, 0.5, 0, 0, 0, 0, 1, 0, 1),
        });
        var (grid, atm) = OptionsOddsCloud.Distribution(flat, e);
        Assert.Equal(0.5, atm, 9);
        Assert.Equal(100 * Math.Exp(-0.25 * 0.5 * 30 / 365.0), OptionsOddsCloud.Quantile(grid, 0.5), 1);
        Assert.True(OptionsOddsCloud.Quantile(grid, 0.16) < OptionsOddsCloud.Quantile(grid, 0.84));
    }

    [Fact]
    public void Daily_merge_fills_unknown_fields_and_known_before_respects_availability()
    {
        var day = new DateTime(2026, 11, 3);
        var provider = new[] { new OptionDaily(day, double.NaN, 0.5, double.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) };
        var recorded = new[] { new OptionDaily(day.AddHours(13), 70_000, 0.45, -2, 1.1, 0.9, 1, 1, 1, 1, -3e8, 75_000, 65_000, 69_000, 1500) };
        var merged = Assert.Single(OptionsData.MergeDaily(provider, recorded));
        Assert.Equal(day, merged.Date);
        Assert.Equal(0.5, merged.AtmIv);
        Assert.Equal(70_000, merged.Spot);
        Assert.Equal(-2, merged.Rr25);
        Assert.Equal(-3e8, merged.Gex);
        // A row for day D becomes known on day D+1.
        var rows = new[] { merged, merged with { Date = day.AddDays(1) } };
        Assert.Null(Opt_KnownBefore(rows, day));
        Assert.Equal(day, Opt_KnownBefore(rows, day.AddDays(1).AddHours(5))!.Date);
        Assert.Equal(day.AddDays(1), Opt_KnownBefore(rows, day.AddDays(2))!.Date);
    }

    private static OptionDaily? Opt_KnownBefore(IReadOnlyList<OptionDaily> rows, DateTime t) =>
        (OptionDaily?)typeof(GreekExposure).Assembly.GetType("TradingHub.Indicators.Native.Options.Opt")!.GetMethod("KnownBefore")!.Invoke(null, new object[] { rows, t });

    [Fact]
    public void Symbols_map_to_option_underlyings()
    {
        Assert.Equal("BTC", OptionsData.UnderlyingOf("BTCUSDT"));
        Assert.Equal("ETH", OptionsData.UnderlyingOf("ETH-PERP"));
        Assert.Equal("BTC", OptionsData.UnderlyingOf("BTC-USDT-SWAP"));
        Assert.Equal("SPY", OptionsData.UnderlyingOf("SPY"));
        Assert.Equal("ETH", OptionsData.UnderlyingOf("ignored", "eth"));
        Assert.Equal("will-btc-hit-100k", PolymarketClient.SlugOf("https://polymarket.com/event/will-btc-hit-100k?tid=1"));
    }

    [Fact]
    public void Flow_helpers_classify_side_and_join_sweeps()
    {
        var e = T0.AddDays(7);
        OptionTrade Tr(int ms, string sym, OptionRight r, int side, double price = 10, double size = 2) => new(T0.AddMilliseconds(ms), sym, e, 100, r, price, size, side, 0.5, 100, 1);
        var trades = new[] { Tr(0, "A", OptionRight.Call, 1), Tr(300, "A", OptionRight.Call, 1), Tr(900, "A", OptionRight.Call, 1), Tr(5000, "B", OptionRight.Put, 1), Tr(5100, "C", OptionRight.Put, -1), Tr(5200, "D", OptionRight.Call, 0) };
        Assert.Equal(1, trades[0].Bias);
        Assert.Equal(-1, trades[3].Bias);
        Assert.Equal(1, trades[4].Bias);
        Assert.Equal(0, trades[5].Bias);
        Assert.Equal(20, trades[0].Premium);
        var orders = FlowTape.Orders(trades);
        Assert.Equal(4, orders.Count);
        Assert.Equal(3, orders[0].Fills);
        Assert.Equal(60, orders[0].Premium);
        // NOPE: Σ Δ × size × multiplier ÷ underlying volume × 100.
        double delta = trades.Sum(t => t.Delta * t.Size);
        Assert.Equal(delta / 50 * 100, Nope.Value(trades, 50), 9);
        Assert.True(double.IsNaN(Nope.Value(trades, 0)));
        var (d, v) = GreekFlow.Of(new[] { Tr(0, "A", OptionRight.Call, 1) });
        Assert.Equal(2 * Tr(0, "A", OptionRight.Call, 1).Delta * 100, d, 9);
        Assert.True(v > 0);
    }

    [Fact]
    public void Open_interest_changes_and_stance_scores()
    {
        var now = new StrikeSnapshot(T0, 100, new[] { new StrikeRow(100, 15, 5, 0, 0, 0), new StrikeRow(110, 3, 0, 0, 0, 0) });
        var old = new StrikeSnapshot(T0.AddHours(-24), 100, new[] { new StrikeRow(100, 10, 8, 0, 0, 0) });
        var ch = OiChanges.Changes(now, old);
        Assert.Equal((100.0, 5.0, -3.0), ch[0]);
        Assert.Equal((110.0, 3.0, 0.0), ch[1]);
        // Up trend with cheap volatility favors buying calls and straddles (selling premium is neutral at best).
        Assert.Equal(new[] { 1, -1, 0, -1, 1 }, OptionStance.Scores(1, -1, 0));
        Assert.Equal(new[] { -1, 0, -1, 1, -1 }, OptionStance.Scores(-1, 1, 0));
    }

    [Fact]
    public void Day_type_classification_and_trailing_volatility_use_no_future_samples()
    {
        DayType.Sample S(int k, double p) => new(k, T0.AddMinutes(10 * k), p, 0, true);
        double minutesYear = 365 * 1440;
        var rising = Enumerable.Range(0, 7).Select(k => S(k, 100 + 0.1 * k)).ToList();
        // σ = P × vol × √(60 / minutes per year); choose vol so σ = 1 at P = 100.6.
        double vol = 1 / (100.6 * Math.Sqrt(60 / minutesYear));
        Assert.Equal(DayType.State.Trending, DayType.Classify(rising, 6, 6, vol, minutesYear, 1.5, 0.5, 0.6));
        var flat = Enumerable.Range(0, 7).Select(k => S(k, 100)).ToList();
        Assert.Equal(DayType.State.Pinned, DayType.Classify(flat, 6, 6, vol, minutesYear, 1.5, 0.5, 0.6));
        var wide = Enumerable.Range(0, 7).Select(k => S(k, k % 2 == 0 ? 100 : 102)).ToList();
        Assert.Equal(DayType.State.Volatile, DayType.Classify(wide, 6, 6, vol, minutesYear, 1.5, 0.5, 0.6));

        var series = Enumerable.Range(0, 40).Select(k => S(k, 100 + Math.Sin(k))).ToList();
        double before = DayType.TrailingVol(series, 30, minutesYear);
        series[30] = S(30, 500);
        Assert.Equal(before, DayType.TrailingVol(series, 30, minutesYear), 12);
        Assert.True(double.IsNaN(DayType.TrailingVol(series, 10, minutesYear)));
    }

    [Fact]
    public void Macro_math_cusum_rsi_and_alignment()
    {
        var a = CusumChangePoint.Step(0, 0, 3, 0.5, 5);
        Assert.Equal((2.5, 0.0, 0, 2.5, 0.0), a);
        var b = CusumChangePoint.Step(a.PosAfter, a.NegAfter, 3, 0.5, 5);
        Assert.Equal(0, b.Alarm);
        var c = CusumChangePoint.Step(b.PosAfter, b.NegAfter, 3, 0.5, 5);
        Assert.Equal(1, c.Alarm);
        Assert.Equal(7.5, c.Pos, 12);
        Assert.Equal(0, c.PosAfter);
        var rsi = MarketHud.Rsi(new double[] { 1, 2, 3, 4, 5 }, 3);
        Assert.True(double.IsNaN(rsi[2]));
        Assert.Equal(100, rsi[3]);
        // Prices 1, 2, 3 vs 2, 4, 6: correlation 1; z of the last of 1, 2, 3 is 1.2247.
        var x = new List<double> { 1, 2, 3 };
        var y = new List<double> { 2, 4, 6 };
        Assert.Equal(1, InvokeMacro<double>("Pearson", x, y, 2, 3), 12);
        Assert.Equal(1.2247449, InvokeMacro<double>("Z", x, 2, 3), 6);
        Assert.True(double.IsNaN(InvokeMacro<double>("Pearson", x, new List<double> { 2, double.NaN, 6 }, 2, 3)));

        var flows = new[] { new EtfFlowDay(new DateTime(2026, 11, 3), 1e8), new EtfFlowDay(new DateTime(2026, 11, 4), -2e8) };
        Assert.Equal(1e8, SpotEtfFlow.FlowFor(flows, new DateTime(2026, 11, 4), SpotEtfFlow.Placement.WhenKnown)!.NetUsd);
        Assert.Equal(-2e8, SpotEtfFlow.FlowFor(flows, new DateTime(2026, 11, 4), SpotEtfFlow.Placement.TradeDate)!.NetUsd);
        Assert.Null(SpotEtfFlow.FlowFor(flows, new DateTime(2026, 11, 3), SpotEtfFlow.Placement.WhenKnown));

        var hist = new List<(DateTime, double)> { (T0, 0.4), (T0.AddMinutes(30), 0.6) };
        Assert.Equal(0.4, OddsVsPrice.ProbabilityAt(hist, T0, T0.AddMinutes(30)), 12);
        Assert.Equal(0.6, OddsVsPrice.ProbabilityAt(hist, T0.AddMinutes(30), T0.AddMinutes(31)), 12);
        Assert.True(double.IsNaN(OddsVsPrice.ProbabilityAt(hist, T0.AddDays(5), T0.AddDays(5).AddMinutes(1))));

        var kl = new List<Kline>
        {
            new(new DateTimeOffset(T0).ToUnixTimeMilliseconds(), 10, 12, 9, 11, 1, 0, false),
            new(new DateTimeOffset(T0.AddMinutes(1)).ToUnixTimeMilliseconds(), 11, 14, 10, 13, 1, 0, false),
        };
        Assert.Equal(13, CorrelationCoefficient.Aggregate(kl, T0, T0.AddMinutes(2), PriceSource.Close));
        Assert.Equal(11.5, CorrelationCoefficient.Aggregate(kl, T0, T0.AddMinutes(2), PriceSource.Hl2));
        Assert.True(double.IsNaN(CorrelationCoefficient.Aggregate(kl, T0.AddMinutes(5), T0.AddMinutes(6), PriceSource.Close)));
    }

    private static T InvokeMacro<T>(string name, params object[] args) =>
        (T)typeof(CusumChangePoint).Assembly.GetType("TradingHub.Indicators.Native.Macro.MacroMath")!.GetMethod(name)!.Invoke(null, args)!;
}

/// <summary>Providers read their documented JSON from fake servers, send keys only as configured and keep unknowns unknown.</summary>
public sealed class ExternalDataProviderTests
{
    [Fact]
    public async Task Custom_options_api_reads_the_documented_format()
    {
        using var s = new FakeHttpServer();
        s.OnHttp = req =>
        {
            if (req.Headers.GetValueOrDefault("X-Api-Key") != "secret") return (401, "{}");
            return req.Route switch
            {
                "/api/chain" => (200, """{"time":"2026-11-05T10:00:00Z","spot":70000,"contracts":[{"symbol":"BTC-C","expiry":"2026-11-27T08:00:00Z","strike":72000,"right":"C","bid":900,"ask":950,"mark":925,"iv":0.52,"delta":0.4,"gamma":0.00003,"oi":120,"volume":30},{"symbol":"BTC-P","expiry":"2026-11-27T08:00:00Z","strike":68000,"right":"P","mark":800,"iv":0.58,"oi":200,"volume":50},{"symbol":"bad","strike":1}]}"""),
                "/api/trades" => (200, """[{"time":"2026-11-05T09:59:00Z","symbol":"BTC-C","expiry":"2026-11-27T08:00:00Z","strike":72000,"right":"C","price":930,"size":5,"side":"buy","iv":0.52,"spot":70000,"venue":"CBOE"},{"time":"2026-11-05T09:59:30Z","symbol":"SPY-C","expiry":"2026-11-27T20:00:00Z","strike":600,"right":"P","price":2.5,"size":4,"side":"sell","contractSize":100}]"""),
                "/api/daily" => (200, """{"data":[{"date":"2026-11-04","atmIv":0.5,"rr25":-2.1,"pcVolume":1.2,"gex":-1.5e8,"callWall":75000,"putWall":65000,"gammaFlip":69000}]}"""),
                _ => (404, "{}"),
            };
        };
        using var http = new HttpClient();
        var api = new JsonOptionsApi(http, s.Http + "/api/", "X-Api-Key", "secret", new[] { "btc" });
        Assert.True(api.Supports("BTC"));
        Assert.False(api.Supports("ETH"));
        var chain = await api.ChainAsync("BTC", CancellationToken.None);
        Assert.NotNull(chain);
        Assert.Equal(70000, chain!.Spot);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc), chain.Time);
        Assert.Equal(2, chain.Contracts.Count);
        Assert.Equal(OptionRight.Put, chain.Contracts[1].Right);
        Assert.Equal(1, chain.Contracts[1].ContractSize);
        // The put's gamma is computed from its IV because the provider gave none.
        Assert.True(OptionsMath.GammaOf(chain.Contracts[1], chain.Spot, chain.Time) > 0);
        var trades = await api.TradesAsync("BTC", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(2, trades.Count);
        Assert.Equal(1, trades[0].Aggressor);
        Assert.Equal(1, trades[0].Bias);
        Assert.Equal("CBOE", trades[0].Venue);
        Assert.Equal(930 * 5, trades[0].Premium);
        // Equity contract: 2.50 per share × 4 contracts × 100 shares.
        Assert.Equal(1000, trades[1].Premium, 9);
        Assert.Equal(1, trades[1].Bias);
        var day = Assert.Single(await api.DailyAsync("BTC", DateTime.UtcNow.AddDays(-10), DateTime.UtcNow, CancellationToken.None));
        Assert.Equal(-2.1, day.Rr25);
        Assert.True(double.IsNaN(day.Spot));
        Assert.Contains(s.Requests, r => r.Route == "/api/trades" && r.Query("underlying") == "BTC" && r.Query("from").EndsWith("Z"));

        var noKey = new JsonOptionsApi(http, s.Http + "/api", null, null);
        await Assert.ThrowsAsync<HttpRequestException>(() => noKey.ChainAsync("BTC", CancellationToken.None));
    }

    [Fact]
    public async Task Etf_api_reads_dollars_or_millions_and_skips_unknown_days()
    {
        using var s = new FakeHttpServer();
        s.OnHttp = req => req.Headers.GetValueOrDefault("Authorization") != "Bearer k"
            ? (401, "{}")
            : (200, """{"data":[{"date":"2026-11-03","net":-123400000,"funds":{"IBIT":-80000000}},{"date":"2026-11-04","netMillions":55.5},{"date":"2026-11-05"}]}""");
        using var http = new HttpClient();
        var api = new JsonEtfFlowsApi(http, s.Http, "Authorization", "k");
        var days = await api.DailyAsync("BTC", new DateTime(2026, 11, 1), new DateTime(2026, 11, 5), CancellationToken.None);
        Assert.Equal(2, days.Count);
        Assert.Equal(-123400000, days[0].NetUsd);
        Assert.Equal(-80000000, days[0].Funds!["IBIT"]);
        Assert.Equal(55.5e6, days[1].NetUsd, 3);
        Assert.Contains(s.Requests, r => r.Route == "/flows" && r.Query("asset") == "BTC" && r.Query("from") == "2026-11-01" && r.Query("to") == "2026-11-05");
    }

    [Fact]
    public async Task Deribit_public_api_parses_chain_trades_and_volatility_index()
    {
        using var s = new FakeHttpServer();
        long stamp = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        s.OnHttp = req => req.Route switch
        {
            "/api/v2/public/get_book_summary_by_currency" => (200, $$"""{"result":[{"instrument_name":"BTC-27NOV26-72000-C","underlying_price":70100,"estimated_delivery_price":70000,"mark_price":0.02,"bid_price":0.019,"ask_price":0.021,"mark_iv":52,"open_interest":120,"volume":30,"creation_timestamp":{{stamp}}},{"instrument_name":"BTC-27NOV26-68000-P","underlying_price":70100,"estimated_delivery_price":70000,"mark_price":0.01,"mark_iv":58,"open_interest":200,"volume":50,"creation_timestamp":{{stamp}}},{"instrument_name":"BTC-PERPETUAL","underlying_price":70000}]}"""),
            "/api/v2/public/get_last_trades_by_currency_and_time" => (200, $$$"""{"result":{"has_more":false,"trades":[{"instrument_name":"BTC-27NOV26-72000-C","timestamp":{{{stamp}}},"price":0.02,"amount":5,"direction":"buy","iv":52,"index_price":70000},{"instrument_name":"BTC-27NOV26-68000-P","timestamp":{{{stamp + 1}}},"price":0.01,"amount":2,"direction":"sell","iv":58,"index_price":70000}]}}"""),
            "/api/v2/public/get_volatility_index_data" => (200, $$$"""{"result":{"data":[[{{{stamp}}},50,52,49,51.5]]}}"""),
            _ => (200, """{"error":{"message":"unknown method"}}"""),
        };
        using var http = new HttpClient();
        var deribit = new DeribitOptions(http, new Uri(s.Http + "/api/v2/public/"));
        Assert.True(deribit.Supports("ETH"));
        Assert.False(deribit.Supports("SPY"));
        var chain = await deribit.ChainAsync("BTC", CancellationToken.None);
        Assert.NotNull(chain);
        Assert.Equal(70000, chain!.Spot);
        Assert.Equal("Deribit", chain.Source);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(stamp).UtcDateTime, chain.Time);
        Assert.Equal(2, chain.Contracts.Count);
        var c = chain.Contracts[0];
        Assert.Equal(new DateTime(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc), c.Expiry);
        Assert.Equal(0.02 * 70000, c.Mark, 9);
        Assert.Equal(0.52, c.Iv, 12);
        // Greeks use the expiry's forward (70,100).
        Assert.Equal(OptionsMath.Greeks(70100, 72000, c.DaysToExpiry(chain.Time) / 365, 0.52, OptionRight.Call).Delta, c.Delta, 12);
        var trades = await deribit.TradesAsync("BTC", chain.Time.AddMinutes(-1), chain.Time.AddMinutes(1), CancellationToken.None);
        Assert.Equal(2, trades.Count);
        Assert.Equal(0.02 * 70000 * 5, trades[0].Premium, 9);
        Assert.Equal(-1, trades[1].Aggressor);
        Assert.Equal(1, trades[1].Bias);
        var daily = Assert.Single(await deribit.DailyAsync("BTC", chain.Time.AddDays(-1), chain.Time, CancellationToken.None));
        Assert.Equal(0.515, daily.AtmIv, 12);
        Assert.True(double.IsNaN(daily.Gex));
        Assert.Null(DeribitOptions.ParseName("BTC-PERPETUAL"));
        Assert.Equal((new DateTime(2026, 12, 27, 8, 0, 0, DateTimeKind.Utc), 100000.0, OptionRight.Call), DeribitOptions.ParseName("BTC-27DEC26-100000-C"));
        Assert.Equal(1.5, DeribitOptions.ParseName("XRP_USDC-5DEC26-1d5-P")!.Value.Strike);
    }

    [Fact]
    public async Task Polymarket_client_finds_markets_by_slug_and_reads_history()
    {
        using var s = new FakeHttpServer();
        s.OnHttp = req => req.Route switch
        {
            "/markets" => (200, "[]"),
            "/events" => (200, """[{"markets":[{"question":"BTC above 100k?","outcomes":"[\"Yes\",\"No\"]","clobTokenIds":"[\"tok-yes\",\"tok-no\"]"}]}]"""),
            "/prices-history" => (200, req.Query("market") == "tok-no" ? """{"history":[{"t":1793786400,"p":0.4},{"t":1793788200,"p":0.45},{"t":1793788200,"p":0.46}]}""" : """{"history":[]}"""),
            _ => (404, "{}"),
        };
        using var http = new HttpClient();
        var client = new PolymarketClient(http, new Uri(s.Http + "/"), new Uri(s.Http + "/"));
        var info = await client.MarketAsync("btc-100k", CancellationToken.None);
        Assert.Equal("BTC above 100k?", info!.Question);
        Assert.Equal(new[] { "tok-yes", "tok-no" }, info.TokenIds);
        var hub = new PolymarketOdds(client, () => DateTimeOffset.FromUnixTimeSeconds(1793790000).UtcDateTime);
        Assert.Equal("Enter a Polymarket market (its URL or slug).", hub.Odds("", 0, DateTime.UtcNow, 30).Message);
        await hub.LoadAsync("btc-100k", 1, DateTimeOffset.FromUnixTimeSeconds(1793780000).UtcDateTime, 30, CancellationToken.None);
        var st = hub.Odds("https://polymarket.com/event/btc-100k", 1, DateTimeOffset.FromUnixTimeSeconds(1793780000).UtcDateTime, 30);
        Assert.Null(st.Message);
        Assert.Equal(2, st.History.Count);
        Assert.Equal(0.46, st.History[1].P);
    }
}

/// <summary>The shared options data: unconfigured means no requests; coverage, recording and provider changes.</summary>
public sealed class OptionsDataHubTests
{
    [Fact]
    public async Task Unconfigured_hub_reports_waiting_and_requests_nothing()
    {
        var hub = new OptionsData();
        long gen = hub.Generation;
        Assert.Equal(OptionsData.NotConfigured, hub.Unavailable("BTC"));
        Assert.Null(hub.Chain("BTC"));
        Assert.Empty(hub.TradesBetween("BTC", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow));
        Assert.Empty(hub.Daily("BTC"));
        await hub.WhenIdleAsync();
        Assert.Equal(gen, hub.Generation);
        var etf = new EtfFlows();
        Assert.Equal(EtfFlows.NotConfigured, etf.Unavailable("BTC"));
        Assert.Null(etf.Daily("BTC", DateTime.UtcNow));
    }

    [Fact]
    public async Task Trades_load_in_daily_windows_with_explicit_coverage_and_recording_writes_snapshots()
    {
        var fx = new OptionsFixture(ProfileStart.AddDays(3));
        var root = Directory.CreateTempSubdirectory("th-options-hub-").FullName;
        try
        {
            var hub = new OptionsData(root, () => fx.End) { MaxTradeDays = 2, Provider = fx };
            Assert.Null(hub.Unavailable("BTC"));
            Assert.Equal("Fixture options has no options on ETH.", hub.Unavailable("ETH"));
            await hub.LoadTradesAsync("BTC", fx.End.AddDays(-5), fx.End, CancellationToken.None);
            var cov = hub.TradeCoverage("BTC")!.Value;
            Assert.Equal(fx.End.AddDays(-2), cov.From);
            Assert.Equal(fx.End, cov.To);
            Assert.Equal(2, fx.TradeCalls);
            Assert.True(hub.TradesCovered("BTC", fx.End.AddHours(-3), fx.End.AddHours(-2)));
            Assert.False(hub.TradesCovered("BTC", fx.End.AddDays(-3), fx.End));
            var hour = hub.TradesBetween("BTC", fx.End.AddHours(-1), fx.End);
            Assert.Equal(6, hour.Count);
            Assert.All(hour, t => Assert.InRange(t.Time, fx.End.AddHours(-1), fx.End.AddTicks(-1)));

            await hub.RefreshChainAsync("BTC", CancellationToken.None);
            Assert.NotNull(hub.Chain("BTC"));
            Assert.Single(hub.Snapshots("BTC"));
            Assert.True(File.Exists(Path.Combine(root, "BTC.strikes.json")));
            Assert.True(File.Exists(Path.Combine(root, "BTC.daily.json")));
            await hub.RefreshDailyAsync("BTC", CancellationToken.None);
            var days = hub.Daily("BTC");
            Assert.Equal(fx.End.Date, days[^1].Date);
            Assert.True(double.IsFinite(days[^1].Gex));

            // A new provider clears every cache; a failing one records its error without throwing.
            hub.Provider = new FailingOptions();
            Assert.Null(hub.TradeCoverage("BTC"));
            Assert.Null(await hub.RefreshChainAsync("BTC", CancellationToken.None));
            Assert.Equal("boom", hub.ChainError("BTC"));
            hub.Provider = null;
            Assert.Equal(OptionsData.NotConfigured, hub.Unavailable("BTC"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Deribit_trade_window_that_hits_the_page_cap_reports_where_coverage_stops()
    {
        // Every page is full and says there is more: 1,000 trades one millisecond apart from the requested start.
        using var s = new FakeHttpServer();
        s.OnHttp = req =>
        {
            long start = long.Parse(req.Query("start_timestamp"));
            var trades = Enumerable.Range(0, 1000).Select(i =>
                $$"""{"trade_id":"t{{start + i}}","instrument_name":"BTC-27NOV26-72000-C","timestamp":{{start + i}},"price":0.01,"amount":1,"direction":"buy","iv":50,"index_price":70000}""");
            return (200, """{"result":{"has_more":true,"trades":[""" + string.Join(",", trades) + "]}}");
        };
        using var http = new HttpClient();
        var deribit = new DeribitOptions(http, new Uri(s.Http + "/api/v2/public/"));
        var from = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(1);
        var window = await deribit.TradesWindowAsync("BTC", from, to, CancellationToken.None);
        // 50 pages advance 999 ms each; trades at the last page's final millisecond may continue on an unread page.
        var coveredTo = from.AddMilliseconds(50 * 999);
        Assert.Equal(coveredTo, window.CoveredTo);
        Assert.Equal(50 * 999, window.Trades.Count);
        Assert.All(window.Trades, t => Assert.True(t.Time < coveredTo));
        Assert.Equal(window.Trades.Count, window.Trades.Select(t => t.Time).Distinct().Count());
        Assert.Equal(DeribitOptions.MaxTradePages, s.Requests.Count);
        Assert.Equal(DeribitOptions.MaxTradePages * 1000, deribit.TradesPerWindow);
    }

    [Fact]
    public async Task Capped_trade_windows_are_partial_gaps_until_a_later_refresh_fills_them()
    {
        var end = ProfileStart.AddDays(2);
        var capped = new CappedTrades(end);
        var hub = new OptionsData(null, () => end) { MaxTradeDays = 2, Provider = capped };
        await hub.LoadTradesAsync("BTC", end.AddDays(-2), end, CancellationToken.None);
        // Each request covers only its first hour: the rest of each window is a recorded gap, never an empty (zero) span.
        Assert.True(hub.TradesCovered("BTC", end.AddDays(-1), end.AddDays(-1).AddMinutes(30)));
        Assert.False(hub.TradesCovered("BTC", end.AddHours(-3), end.AddHours(-2)));
        var note = hub.TradeCoverageNote("BTC", end.AddDays(-2), end);
        Assert.NotNull(note);
        Assert.StartsWith("Partial: trades capped at 1000 per request;", note);
        Assert.Contains("UTC not loaded yet", note);
        Assert.Null(hub.TradeCoverageNote("BTC", end.AddDays(-1), end.AddDays(-1).AddMinutes(30)));
        // Studies name the unloaded span: the card footer of an overlay, and gaps (not zeros) in a pane.
        using (var data = OptionsMacroStudyTests.Market(2))
        {
            var stats = new OptionsStats { Service = hub };
            using var model = new TradingHub.Charting.ChartModel(data, TradingHub.Data.ChartPeriod.Parse("M30"), new TradingHub.Charting.ChartSettings { TimeZoneId = "UTC" });
            Assert.Null(model.AddIndicator(stats, applyDefaultColors: false).Error);
            var volume = new OptionsVolume { Service = hub };
            Assert.Null(model.AddIndicator(volume, applyDefaultColors: false).Error);
            model.Update();
            Assert.Contains("Partial: trades capped at 1000 per request;", stats.Provenance);
            var calls = (OptionalValueDataSeries)volume.DataSeries[0];
            Assert.False(calls.IsAvailable(model.Series.Count - 2));
        }

        // Later refreshes keep paging from where each capped window stopped until the gaps close.
        for (int i = 0; i < 60 && hub.TradeCoverageNote("BTC", end.AddDays(-2), end) != null; i++)
            await hub.LoadTradesAsync("BTC", end.AddDays(-2), end, CancellationToken.None);
        Assert.Null(hub.TradeCoverageNote("BTC", end.AddDays(-2), end));
        Assert.True(hub.TradesCovered("BTC", end.AddDays(-2), end));
        var all = hub.TradesBetween("BTC", end.AddDays(-2), end);
        Assert.Equal(capped.All.Count(t => t.Time >= end.AddDays(-2)), all.Count);
        Assert.Equal(all.Count, all.Select(t => t.Time).Distinct().Count());
    }

    /// <summary>Two loads of one underlying (a scheduled background load and a direct caller) read the store, await the
    /// provider and write the store back. Overlapping them lets the slower writer replace the faster one's progress,
    /// which left a window reported as not covered on a loaded runner. Their provider requests must not overlap.</summary>
    [Fact]
    public async Task Concurrent_trade_loads_for_one_underlying_never_overlap_their_provider_requests()
    {
        var end = ProfileStart.AddDays(2);
        var provider = new OverlapProbe();
        var hub = new OptionsData(null, () => end) { MaxTradeDays = 2, Provider = provider };
        await Task.WhenAll(
            hub.LoadTradesAsync("BTC", end.AddDays(-2), end, CancellationToken.None),
            hub.LoadTradesAsync("BTC", end.AddDays(-2), end, CancellationToken.None));
        Assert.Equal(1, provider.MaxActive);
        Assert.True(hub.TradesCovered("BTC", end.AddDays(-2), end));
    }

    private sealed class OverlapProbe : IOptionsProvider, IOptionTradeWindows
    {
        private int _active, _max;
        public int MaxActive => Volatile.Read(ref _max);

        public string Name => "Overlap";
        public int TradesPerWindow => 1000;
        public bool Supports(string underlying) => true;
        public Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct) => Task.FromResult<OptionsChain?>(null);
        public Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => Task.FromResult<IReadOnlyList<OptionDaily>>(Array.Empty<OptionDaily>());
        public async Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => (await TradesWindowAsync(underlying, from, to, ct)).Trades;

        public async Task<OptionTradeWindow> TradesWindowAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
            try { await Task.Delay(100, ct); }
            finally { Interlocked.Decrement(ref _active); }
            return new OptionTradeWindow(new List<OptionTrade>(), to);
        }
    }

    /// <summary>A provider whose every trade request stops after one hour of its window, like a page cap.</summary>
    private sealed class CappedTrades : IOptionsProvider, IOptionTradeWindows
    {
        public readonly List<OptionTrade> All = new();

        public CappedTrades(DateTime end)
        {
            for (var t = end.AddDays(-3); t < end; t = t.AddMinutes(5))
                All.Add(new OptionTrade(t, "BTC-C", end.AddDays(7), 100, OptionRight.Call, 1, 1, 1, 0.5, 100, 1));
        }

        public string Name => "Capped";
        public int TradesPerWindow => 1000;
        public bool Supports(string underlying) => true;
        public Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct) => Task.FromResult<OptionsChain?>(null);
        public Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => Task.FromResult<IReadOnlyList<OptionDaily>>(Array.Empty<OptionDaily>());
        public async Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => (await TradesWindowAsync(underlying, from, to, ct)).Trades;

        public Task<OptionTradeWindow> TradesWindowAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
        {
            var covered = from.AddHours(1) < to ? from.AddHours(1) : to;
            return Task.FromResult(new OptionTradeWindow(All.Where(t => t.Time >= from && t.Time < covered).ToList(), covered));
        }
    }

    internal static readonly DateTime ProfileStart = ProfileMergeTests.Monday;

    private sealed class FailingOptions : IOptionsProvider
    {
        public string Name => "Failing";
        public bool Supports(string underlying) => true;
        public Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct) => throw new HttpRequestException("boom");
        public Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => throw new HttpRequestException("boom");
        public Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => throw new HttpRequestException("boom");
    }
}

/// <summary>An in-memory options provider around a synthetic BTC market priced near 100.</summary>
internal sealed class OptionsFixture : IOptionsProvider
{
    private readonly List<OptionTrade> _trades = new();
    private readonly List<OptionDaily> _daily = new();

    public OptionsFixture(DateTime end)
    {
        End = end;
        var rnd = new Random(7);
        double spot = 100;
        var expiries = new[] { 1, 3, 7, 14, 30, 60, 90 }.Select(d => end.Date.AddDays(d).AddHours(8)).ToList();
        var contracts = new List<OptionContract>();
        foreach (var e in expiries)
            for (double k = 70; k <= 130; k += 2.5)
                foreach (var r in new[] { OptionRight.Call, OptionRight.Put })
                {
                    double t = (e - end).TotalDays / 365, m = Math.Log(k / spot);
                    double iv = 0.5 + 0.6 * m * m - (r == OptionRight.Put ? 0.05 * m : 0);
                    var (delta, gamma) = OptionsMath.Greeks(spot, k, t, iv, r);
                    double oi = 200 * Math.Exp(-m * m * 40) * (r == OptionRight.Put && k < spot ? 1.6 : 1);
                    contracts.Add(new OptionContract($"BTC-{e:ddMMMyy}-{k}-{(r == OptionRight.Call ? "C" : "P")}", e, k, r, 1, 1.1, 1.05, iv, delta, gamma, 0, 0, oi, oi * 0.3, 1));
                }

        Chain = new OptionsChain("BTC", end, spot, contracts, Name, "fixture spot");
        for (var t = ProfileMergeTests.Monday; t < end; t = t.AddMinutes(10))
        {
            var e = expiries[rnd.Next(expiries.Count)];
            double k = 90 + 2.5 * rnd.Next(9);
            var r = rnd.Next(2) == 0 ? OptionRight.Call : OptionRight.Put;
            _trades.Add(new OptionTrade(t, $"BTC-{e:ddMMMyy}-{k}-{(r == OptionRight.Call ? "C" : "P")}", e, k, r, 2 + rnd.NextDouble(), 1 + rnd.Next(20), rnd.Next(3) - 1, 0.55, 100, 1,
                rnd.Next(2) == 0 ? "Deribit" : "OKX"));
        }

        for (var d = end.Date.AddDays(-400); d < end.Date; d = d.AddDays(1))
            _daily.Add(new OptionDaily(d, spot, 0.45 + 0.15 * Math.Sin(d.DayOfYear / 30.0), -3 + 0.01 * d.DayOfYear, 1.1, 0.9, 1000 + d.DayOfYear, 800, 5000 + d.Day, 4000, (d.Day - 15) * 1e7,
                105, 95, 99, 3));
    }

    public DateTime End { get; }
    public OptionsChain Chain { get; }
    public int TradeCalls { get; private set; }
    public string Name => "Fixture options";
    public bool Supports(string underlying) => underlying == "BTC";
    public Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct) => Task.FromResult<OptionsChain?>(Chain);

    public Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        TradeCalls++;
        return Task.FromResult<IReadOnlyList<OptionTrade>>(_trades.Where(t => t.Time >= from && t.Time < to).ToList());
    }

    public Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OptionDaily>>(_daily.Where(d => d.Date >= from.Date && d.Date <= to).ToList());

    /// <summary>Strike snapshots every 30 minutes over the fixture, for the studies that read recorded history.</summary>
    public void WriteSnapshots(string root)
    {
        var snaps = new List<StrikeSnapshot>();
        for (var t = ProfileMergeTests.Monday; t <= End; t = t.AddMinutes(30)) snaps.Add(OptionsMath.SnapshotOf(Chain with { Time = t }));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "BTC.strikes.json"), JsonSerializer.Serialize(snaps));
    }
}

/// <summary>Every options and macro study calculates and renders, shows the waiting state without data and never repaints closed bars.</summary>
public sealed class OptionsMacroStudyTests
{
    public static IEnumerable<object[]> OptionsTypes() => typeof(OptionsOverlay).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && (typeof(OptionsOverlay).IsAssignableFrom(t) || typeof(OptionsPane).IsAssignableFrom(t)))
        .OrderBy(t => t.Name).Select(t => new object[] { t });

    public static IEnumerable<object[]> PaneTypes() => OptionsTypes().Where(o => typeof(OptionsPane).IsAssignableFrom((Type)o[0]));

    internal static InstrumentData Market(int days)
    {
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "sim", TickSize = 0.1m, VolumeStep = 1 };
        var data = new InstrumentData(instrument, new SimulatedFeed());
        long id = 0;
        for (int minute = 0; minute < days * 24 * 60; minute += 10)
        {
            decimal price = 100m + Math.Round(3m * (decimal)Math.Sin(minute / 97.0), 1);
            data.Trades.Add(new Trade(++id, ProfileMergeTests.Monday.AddMinutes(minute), price, 1 + minute % 4, minute % 30 == 0 ? TradeSide.Sell : TradeSide.Buy));
        }

        return data;
    }

    private static async Task<(OptionsData Hub, OptionsFixture Fx, string Root)> LoadedHub(int days = 10)
    {
        var fx = new OptionsFixture(ProfileMergeTests.Monday.AddDays(days));
        var root = Directory.CreateTempSubdirectory("th-options-studies-").FullName;
        fx.WriteSnapshots(root);
        var hub = new OptionsData(root, () => fx.End) { MaxTradeDays = days + 1, Provider = fx };
        await hub.LoadTradesAsync("BTC", ProfileMergeTests.Monday, fx.End, CancellationToken.None);
        await hub.RefreshChainAsync("BTC", CancellationToken.None);
        await hub.RefreshDailyAsync("BTC", CancellationToken.None);
        return (hub, fx, root);
    }

    private static Indicator Create(Type type, OptionsData? hub)
    {
        var ind = (Indicator)Activator.CreateInstance(type)!;
        switch (ind)
        {
            case OptionsOverlay o: o.Service = hub; break;
            case OptionsPane p: p.Service = hub; break;
        }

        return ind;
    }

    private static (ChartModel Model, IndicatorSlot Slot) Chart(InstrumentData data, Indicator ind, string period = "M30")
    {
        var model = new ChartModel(data, ChartPeriod.Parse(period), new ChartSettings { TimeZoneId = "UTC" });
        var slot = model.AddIndicator(ind, applyDefaultColors: false);
        Assert.Null(slot.Error);
        model.Update();
        model.Update();
        return (model, slot);
    }

    private static void Render(ChartModel model)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1400, 800));
        new ChartRenderer().Render(surface.Canvas, model, 1400, 800, new ChartRenderState());
    }

    private static string? StatusOf(Indicator ind) => ind switch
    {
        OptionsOverlay o => o.Status,
        OptionsPane p => p.Status,
        _ => null,
    };

    [Fact]
    public void Options_studies_are_registered_in_the_options_group()
    {
        var types = OptionsTypes().Select(o => (Type)o[0]).ToList();
        Assert.True(types.Count >= 42, $"{types.Count} options studies");
        foreach (var t in types.Where(t => t != typeof(DayType)))
            Assert.Equal(IndicatorCategories.Options, ((System.ComponentModel.CategoryAttribute)Attribute.GetCustomAttribute(t, typeof(System.ComponentModel.CategoryAttribute))!).Category);
        foreach (var t in new[] { typeof(CusumChangePoint), typeof(MarketHud), typeof(SpotEtfFlow), typeof(CorrelationCoefficient), typeof(OddsVsPrice) })
            Assert.Equal(IndicatorCategories.Macro, ((System.ComponentModel.CategoryAttribute)Attribute.GetCustomAttribute(t, typeof(System.ComponentModel.CategoryAttribute))!).Category);
    }

    [Theory]
    [MemberData(nameof(OptionsTypes))]
    public void Without_an_options_connection_every_study_waits(Type type)
    {
        using var data = Market(3);
        foreach (var hub in new[] { null, new OptionsData() })
        {
            var ind = Create(type, hub);
            var (model, slot) = Chart(data, ind);
            using var _ = model;
            Render(model);
            Assert.Null(slot.Error);
            if (type == typeof(DayType)) Assert.Null(StatusOf(ind));
            else Assert.Equal(OptionsData.NotConfigured, StatusOf(ind));
        }
    }

    [Theory]
    [MemberData(nameof(OptionsTypes))]
    public async Task With_data_every_study_calculates_and_renders(Type type)
    {
        var (hub, fx, root) = await LoadedHub();
        try
        {
            using var data = Market(10);
            int calls = fx.TradeCalls;
            foreach (var period in new[] { "M10", "M30", "H1" })
            {
                var ind = Create(type, hub);
                var (model, slot) = Chart(data, ind, period);
                using var _ = model;
                model.Viewport.BarWidth = 8;
                Render(model);
                Assert.Null(slot.Error);
                string? status = StatusOf(ind);
                Assert.True(status == null, $"{type.Name} {period}: {status}");
            }

            await hub.WhenIdleAsync();
            // With fresh caches, calculating and rendering requests nothing more.
            Assert.Equal(calls, fx.TradeCalls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(PaneTypes))]
    public async Task Pane_values_of_closed_bars_never_repaint(Type type)
    {
        var (hub, _, root) = await LoadedHub();
        try
        {
            using var shortData = Market(7);
            using var fullData = Market(10);
            var a = Create(type, hub);
            var b = Create(type, hub);
            var (ma, _) = Chart(shortData, a);
            var (mb, _) = Chart(fullData, b);
            using var __ = ma;
            using var ___ = mb;
            int cut = ma.Series.Count - 1;
            int compared = 0;
            foreach (var (sa, sb) in a.DataSeries.OfType<OptionalValueDataSeries>().Zip(b.DataSeries.OfType<OptionalValueDataSeries>()))
                for (int i = 0; i < cut; i++)
                {
                    // Only today's sentiment is drawn: earlier days are hidden once a later day exists.
                    if (type == typeof(OptionsSentiment)) break;
                    Assert.True(sa.IsAvailable(i) == sb.IsAvailable(i), $"{type.Name} {sa.Id} bar {i} availability");
                    if (!sa.IsAvailable(i)) continue;
                    Assert.True(Math.Abs((double)(sa[i] - sb[i])) < 1e-9, $"{type.Name} {sa.Id} bar {i}: {sa[i]} vs {sb[i]}");
                    compared++;
                }

            Assert.True(type == typeof(OptionsSentiment) || compared > 0, $"{type.Name} compared no values");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Bars_outside_loaded_trades_are_gaps_and_daily_rows_apply_from_the_next_day()
    {
        var fx = new OptionsFixture(ProfileMergeTests.Monday.AddDays(5));
        var hub = new OptionsData(null, () => fx.End) { MaxTradeDays = 2, Provider = fx };
        await hub.LoadTradesAsync("BTC", fx.End.AddDays(-2), fx.End, CancellationToken.None);
        await hub.RefreshDailyAsync("BTC", CancellationToken.None);
        using var data = Market(5);
        var volume = new OptionsVolume { Service = hub };
        var (model, _) = Chart(data, volume);
        using var __ = model;
        var calls = (OptionalValueDataSeries)volume.DataSeries[0];
        var first = model.Series.Candles[0].Time;
        Assert.False(calls.IsAvailable(0));
        int recent = model.Series.Count - 10;
        Assert.True(calls.IsAvailable(recent));
        Assert.True(model.Series.Candles[recent].Time >= fx.End.AddDays(-2));

        var history = new OptionsHistory { Service = hub };
        var (hm, _) = Chart(data, history);
        using var ___ = hm;
        var hc = (OptionalValueDataSeries)history.DataSeries[0];
        for (int i = 0; i < hm.Series.Count; i++)
        {
            var day = hm.Series.Candles[i].Time.Date;
            // The fixture's call volume for day D is 5000 + D.Day; bars on day D show day D−1.
            Assert.True(hc.IsAvailable(i));
            Assert.Equal(5000 + day.AddDays(-1).Day, (double)hc[i]);
        }

        Assert.NotEqual(first, default);
    }

    [Fact]
    public async Task Macro_studies_wait_without_connections_and_compute_with_data()
    {
        using var data = Market(10);
        var end = ProfileMergeTests.Monday.AddDays(10);

        var etfIdle = new SpotEtfFlow { Service = new EtfFlows() };
        var (m0, _) = Chart(data, etfIdle);
        using (m0) Assert.Equal(EtfFlows.NotConfigured, etfIdle.Status);

        var etf = new EtfFlows(() => end) { Provider = new FixtureEtf() };
        await etf.RefreshAsync("BTC", ProfileMergeTests.Monday.AddDays(-1), CancellationToken.None);
        var flow = new SpotEtfFlow { Service = etf };
        var (m1, s1) = Chart(data, flow, "H1");
        using (m1)
        {
            Render(m1);
            Assert.Null(flow.Status);
            var inflow = (OptionalValueDataSeries)flow.DataSeries[0];
            var outflow = (OptionalValueDataSeries)flow.DataSeries[1];
            for (int i = 0; i < m1.Series.Count; i++)
            {
                var day = m1.Series.Candles[i].Time.Date;
                var known = FixtureEtf.Days.FirstOrDefault(d => d.Date == day.AddDays(-1));
                if (known == null) Assert.False(inflow.IsAvailable(i) || outflow.IsAvailable(i), $"bar {i} on {day:yyyy-MM-dd} must be a gap");
                else Assert.Equal(known.NetUsd / 1e6, (double)(known.NetUsd >= 0 ? inflow[i] : outflow[i]), 9);
            }
        }

        var klines = new FixtureKlines(data);
        var compare = new ComparisonKlines(_ => klines, () => end);
        await compare.LoadAsync("ETHUSDT", true, data.Trades.FirstTime, 15, CancellationToken.None);
        var corr = new CorrelationCoefficient { Service = compare, Length = 10 };
        var (m2, _) = Chart(data, corr, "M30");
        using (m2)
        {
            Render(m2);
            Assert.Null(corr.Status);
            var series = (OptionalValueDataSeries)corr.DataSeries[0];
            Assert.False(series.IsAvailable(5));
            Assert.Equal(1, (double)series[m2.Series.Count - 2], 6);
        }

        var cusum = new CusumChangePoint();
        var (m3, s3) = Chart(data, cusum);
        using (m3) { Render(m3); Assert.Null(s3.Error); }
        var hud = new MarketHud();
        var (m4, s4) = Chart(data, hud);
        using (m4) { Render(m4); Assert.Null(s4.Error); }

        var odds = new OddsVsPrice { Service = new PolymarketOdds(new PolymarketClient(new HttpClient(), new Uri("http://127.0.0.1:9/"), new Uri("http://127.0.0.1:9/")), () => end) };
        var (m5, _) = Chart(data, odds);
        using (m5) { Render(m5); Assert.Equal("Enter a Polymarket market (its URL or slug).", odds.Status); }
    }

    private sealed class FixtureEtf : IEtfFlowsProvider
    {
        // Weekdays only, like US issuers; Monday 2026-10-19 through Friday 2026-10-23 and the next Monday–Tuesday.
        public static readonly EtfFlowDay[] Days = new[] { 19, 20, 21, 22, 23, 26, 27 }.Select(d => new EtfFlowDay(new DateTime(2026, 10, d), (d % 2 == 0 ? 1 : -1) * d * 1e7)).ToArray();
        public string Name => "Fixture ETF";
        public bool Supports(string asset) => asset == "BTC";
        public Task<IReadOnlyList<EtfFlowDay>> DailyAsync(string asset, DateTime from, DateTime to, CancellationToken ct) => Task.FromResult<IReadOnlyList<EtfFlowDay>>(Days);
    }

    /// <summary>The chart's own 15-minute candles doubled, so the price correlation is exactly 1.</summary>
    private sealed class FixtureKlines(InstrumentData data) : IKlineHistory
    {
        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(data.Trades.FirstTime);

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice, Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            var bars = new List<Kline>();
            for (int i = 0; i < data.Trades.Count; i++)
            {
                var t = data.Trades.GetTime(i);
                if (t < fromUtc || t >= toUtc) continue;
                long open = new DateTimeOffset(t).ToUnixTimeMilliseconds() / (minutes * 60_000L) * minutes * 60_000L;
                decimal p = data.Trades.GetPrice(i) * 2;
                if (bars.Count > 0 && bars[^1].OpenTimeMs == open)
                {
                    var b = bars[^1];
                    bars[^1] = b with { High = Math.Max(b.High, p), Low = Math.Min(b.Low, p), Close = p };
                }
                else bars.Add(new Kline(open, p, p, p, p, 1, 0, false));
            }

            onBatch(bars);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Options and ETF connections: data only, keys in the secret store, providers follow enabled profiles.</summary>
public sealed class ExternalDataConnectionTests
{
    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string Get(string account) => _values.GetValueOrDefault(account, "");
        public void Set(string account, string? secret) { if (string.IsNullOrEmpty(secret)) _values.Remove(account); else _values[account] = secret; }
    }

    [Fact]
    public async Task External_connections_configure_shared_data_and_never_act_as_markets()
    {
        string root = Directory.CreateTempSubdirectory("th-external-connections-").FullName;
        using var server = new FakeHttpServer();
        server.OnHttp = req => req.Headers.GetValueOrDefault("Authorization") != "Bearer opt-KEY" ? (401, "{}") : req.Route switch
        {
            "/v1/chain" => (200, """{"time":"2026-11-05T10:00:00Z","spot":70000,"contracts":[{"symbol":"C1","expiry":"2026-11-27T08:00:00Z","strike":72000,"right":"C","mark":925,"iv":0.52,"oi":120,"volume":30}]}"""),
            "/flows" => (200, $$"""[{"date":"{{DateTime.UtcNow.AddDays(-2):yyyy-MM-dd}}","net":-123400000}]"""),
            _ => (404, "{}"),
        };
        try
        {
            var connections = new ConnectionManager(secretStore: new MemorySecrets(), configurationPath: Path.Combine(root, "connections.json"));
            var options = new OptionsData();
            var etf = new EtfFlows();
            connections.ApplyExternalData(options, etf);
            Assert.Null(options.Provider);
            Assert.Null(etf.Provider);

            var pOpt = new ConnectionProfile { Id = "options-data", Kind = ConnectionKind.OptionsData, Name = "Options data", OptionsSource = OptionsSource.CustomJson, ExternalUrl = server.Http + "/v1", ExternalUnderlyings = "btc" };
            var pEtf = new ConnectionProfile { Id = "etf-flows", Kind = ConnectionKind.EtfFlows, Name = "ETF flows", ExternalUrl = server.Http };
            connections.Profiles.AddRange(new[] { pOpt, pEtf });
            connections.SetSecret(pOpt, "opt-KEY");
            connections.SetSecret(pEtf, "opt-KEY");
            connections.Save();
            Assert.DoesNotContain("opt-KEY", File.ReadAllText(Path.Combine(root, "connections.json")));
            Assert.False(pOpt.HasMarketData || pEtf.HasMarketData);
            Assert.True(pOpt.IsExternalData && pEtf.IsExternalData);
            Assert.NotEqual("ib", pOpt.MarketDataId);
            Assert.Throws<NotSupportedException>(() => connections.GetFeed(pOpt.Id));

            connections.ApplyExternalData(options, etf);
            Assert.IsType<JsonOptionsApi>(options.Provider);
            Assert.IsType<JsonEtfFlowsApi>(etf.Provider);
            Assert.Null(options.Unavailable("BTC"));
            Assert.NotNull(options.Unavailable("ETH"));

            var (ok, msg) = await connections.TestAsync(pOpt, CancellationToken.None);
            Assert.True(ok, msg);
            Assert.Contains("1 BTC contracts", msg);
            var (ok2, msg2) = await connections.TestAsync(pEtf, CancellationToken.None);
            Assert.True(ok2, msg2);

            // Unchanged settings keep the provider (and its caches); disabling releases it.
            var same = options.Provider;
            connections.ApplyExternalData(options, etf);
            Assert.Same(same, options.Provider);
            pOpt.Enabled = false;
            connections.ApplyExternalData(options, etf);
            Assert.Null(options.Provider);
            Assert.NotNull(etf.Provider);

            // Deribit needs no key or URL; a custom API without a URL configures nothing.
            pOpt.Enabled = true;
            pOpt.OptionsSource = OptionsSource.Deribit;
            connections.ApplyExternalData(options, etf);
            Assert.IsType<DeribitOptions>(options.Provider);
            pOpt.OptionsSource = OptionsSource.CustomJson;
            pOpt.ExternalUrl = "";
            connections.ApplyExternalData(options, etf);
            Assert.Null(options.Provider);
            Assert.Equal((false, "Enter the API base URL."), await connections.TestAsync(pOpt, CancellationToken.None));

            // Reloading keeps the persisted fields.
            connections.Save();
            var reloaded = new ConnectionManager(secretStore: new MemorySecrets(), configurationPath: Path.Combine(root, "connections.json"));
            var back = reloaded.Find("options-data")!;
            Assert.Equal(ConnectionKind.OptionsData, back.Kind);
            Assert.Equal("btc", back.ExternalUnderlyings);
            connections.DisposeExternalData();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
