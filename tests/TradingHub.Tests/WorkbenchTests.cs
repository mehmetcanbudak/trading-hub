using System.Text.Json;
using TradingHub.App.Services.Workbench;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Feeds.External;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class WorkbenchCalculatorTests
{
    [Fact]
    public void Position_size_matches_hand_computation_with_fees_and_floors_to_the_step()
    {
        // budget 10,000 × 1 % = 100; loss/unit = 5 + (100 + 95) × 0.0005 = 5.0975; 100 / 5.0975 = 19.6174…
        var r = WorkbenchCalculators.PositionSize(new PositionSizeInput(10_000, 1, RiskUnit.Percent, TradeDirection.Long, 100, 95, 110, 0.05m, 1, 0.001m));
        Assert.True(r.IsValid);
        Assert.Equal(100m, r.Budget);
        Assert.Equal(19.617m, r.Quantity);
        Assert.Equal(19.617m * 5.0975m, r.Risk);
        Assert.True(r.Risk <= r.Budget);
        Assert.Equal(19.617m * 100m, r.Notional);
        Assert.Equal(5m, r.StopDistance);
        Assert.Equal(5m, r.StopDistancePercent);
        // gain/unit = 10 − (100 + 110) × 0.0005 = 9.895; R = 9.895 / 5.0975
        Assert.Equal(19.617m * 9.895m, r.Reward);
        Assert.Equal(9.895m / 5.0975m, r.RewardRiskRatio);
        Assert.Equal(100m / (1 + 9.895m / 5.0975m), r.BreakevenWinRatePercent);
    }

    [Fact]
    public void Futures_point_value_and_whole_contract_step_size_like_the_position_drawing()
    {
        // ES: 50 per point. 50,000 × 1 % = 500; stop 10 points = 500 per contract → 1 contract.
        var input = new PositionSizeInput(50_000, 1, RiskUnit.Percent, TradeDirection.Short, 5000, 5010, 4980, 0, 50, 1);
        var r = WorkbenchCalculators.PositionSize(input);
        Assert.Equal(1m, r.Quantity);
        Assert.Equal(500m, r.Risk);
        Assert.Equal(1000m, r.Reward);
        Assert.Equal(250_000m, r.Notional);

        var drawing = new ChartDrawing
        {
            Kind = DrawingKind.ShortPosition, AccountSize = 50_000, RiskPercent = 1, AccountCurrency = "USD",
            Points = { new ChartPoint(DateTime.UnixEpoch, 5000), new ChartPoint(DateTime.UnixEpoch, 4980), new ChartPoint(DateTime.UnixEpoch, 5010) },
        };
        var instrument = new Instrument { Symbol = "ES", FeedId = "sim", TickSize = 0.25m, VolumeStep = 1, PointValue = 50, QuoteAsset = "USD" };
        var reference = drawing.PositionSizing(instrument);
        Assert.Equal(reference.Quantity, r.Quantity);
        Assert.Equal(reference.Risk, r.Risk);
        Assert.Equal(reference.Reward, r.Reward);

        // 499 of budget never rounds up to a contract that would risk 500
        var below = WorkbenchCalculators.PositionSize(input with { Balance = 49_900 });
        Assert.Equal(0m, below.Quantity);
        Assert.Contains(below.Warnings, w => w.Contains("below one quantity step"));
    }

    [Fact]
    public void Position_size_amount_mode_validation_and_direction_checks()
    {
        var amount = WorkbenchCalculators.PositionSize(new PositionSizeInput(0, 250, RiskUnit.Amount, TradeDirection.Long, 2000, 1950));
        Assert.True(amount.IsValid);
        Assert.Equal(5m, amount.Quantity);
        Assert.Null(amount.Leverage);
        Assert.False(WorkbenchCalculators.PositionSize(new PositionSizeInput(1000, 1, RiskUnit.Percent, TradeDirection.Long, 100, 101)).IsValid);
        Assert.False(WorkbenchCalculators.PositionSize(new PositionSizeInput(1000, 1, RiskUnit.Percent, TradeDirection.Short, 100, 99)).IsValid);
        Assert.False(WorkbenchCalculators.PositionSize(new PositionSizeInput(1000, 150, RiskUnit.Percent, TradeDirection.Long, 100, 99)).IsValid);
        Assert.False(WorkbenchCalculators.PositionSize(new PositionSizeInput(1000, 1, RiskUnit.Percent, TradeDirection.Long, 100, 99, FeePercentPerSide: -1)).IsValid);
        var wrongTarget = WorkbenchCalculators.PositionSize(new PositionSizeInput(1000, 1, RiskUnit.Percent, TradeDirection.Long, 100, 99, 98));
        Assert.True(wrongTarget.IsValid);
        Assert.Null(wrongTarget.Reward);
        Assert.Contains(wrongTarget.Warnings, w => w.Contains("target must be above"));
        var leveraged = WorkbenchCalculators.PositionSize(new PositionSizeInput(1000, 1, RiskUnit.Percent, TradeDirection.Long, 100, 99.98m));
        Assert.Equal(50m, leveraged.Leverage);
        Assert.Contains(leveraged.Warnings, w => w.Contains("above 20×"));
    }

    [Fact]
    public void Liquidation_price_uses_the_documented_isolated_formula()
    {
        var longs = WorkbenchCalculators.LiquidationPrice(new LiquidationInput(TradeDirection.Long, 100, 10, 0.5m));
        Assert.Equal(100m * 0.9m / 0.995m, longs.Price);
        Assert.Equal(10m, longs.InitialMarginPercent);
        Assert.Null(longs.Warning);
        var shorts = WorkbenchCalculators.LiquidationPrice(new LiquidationInput(TradeDirection.Short, 100, 10, 0.5m));
        Assert.Equal(100m * 1.1m / 1.005m, shorts.Price);
        Assert.True(shorts.DistancePercent > 0 && longs.DistancePercent < 0);
        var noMaintenance = WorkbenchCalculators.LiquidationPrice(new LiquidationInput(TradeDirection.Long, 30_000, 20, 0));
        Assert.Equal(28_500m, noMaintenance.Price);
        Assert.NotNull(WorkbenchCalculators.LiquidationPrice(new LiquidationInput(TradeDirection.Long, 100, 100, 1)).Warning);
        Assert.False(WorkbenchCalculators.LiquidationPrice(new LiquidationInput(TradeDirection.Long, 100, 0.5m, 0.5m)).IsValid);
        Assert.False(WorkbenchCalculators.LiquidationPrice(new LiquidationInput(TradeDirection.Long, 0, 10, 0.5m)).IsValid);
    }

    [Fact]
    public void Funding_cost_counts_whole_settlements_and_sign()
    {
        var r = WorkbenchCalculators.FundingCost(new FundingCostInput(10_000, 0.01m, 8, 24));
        Assert.Equal(3, r.Settlements);
        Assert.Equal(3m, r.Cost);
        Assert.Equal(3m, r.PerDay);
        Assert.Equal(10.95m, r.AnnualizedPercent);
        Assert.True(r.LongsPay);
        var partial = WorkbenchCalculators.FundingCost(new FundingCostInput(10_000, -0.02m, 8, 23.9m));
        Assert.Equal(2, partial.Settlements);
        Assert.Equal(-4m, partial.Cost);
        Assert.False(partial.LongsPay);
        Assert.False(WorkbenchCalculators.FundingCost(new FundingCostInput(10_000, 0.01m, 0, 24)).IsValid);
    }

    [Fact]
    public void Session_clock_and_next_funding_settlement()
    {
        // 14:00 UTC on 15 January = 09:00 in New York (EST)
        var now = new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc);
        var rows = WorkbenchCalculators.SessionClock(now);
        var morning = rows.Single(r => r.Session.Name == "New York morning");
        Assert.True(morning.Live);
        Assert.Equal(TimeSpan.FromHours(3), morning.Left);
        Assert.Equal(now.AddHours(-1), morning.StartUtc);
        var london = rows.Single(r => r.Session.Name == "London");
        Assert.False(london.Live);
        Assert.Equal(TimeSpan.FromHours(17), london.Until);
        Assert.True(new TradingSession("Asia", 18, 2).Contains(1));
        Assert.Equal(new DateTime(2026, 1, 15, 16, 0, 0, DateTimeKind.Utc), WorkbenchCalculators.NextEightHourFunding(now));
        Assert.Equal(new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc), WorkbenchCalculators.NextEightHourFunding(new DateTime(2026, 1, 15, 16, 0, 0, DateTimeKind.Utc)));
    }
}

public sealed class WorkbenchTapeTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_millisecond_prints_merge_per_venue_and_side()
    {
        var tape = new TradeTape(new TapeSettings(MinNotional: 0, LargeNotional: 1000, HugeNotional: 5000));
        var large = tape.AddTrades(new[]
        {
            new TapeTrade(T0, "binance-futures", 100, 4, TradeSide.Buy),
            new TapeTrade(T0, "binance-futures", 102, 6, TradeSide.Buy),
            new TapeTrade(T0, "bybit", 100, 1, TradeSide.Buy),
            new TapeTrade(T0.AddMilliseconds(1), "bybit", 100, 1, TradeSide.Sell),
        });
        // the merged binance line (1012 notional) closed when bybit arrived
        var merged = Assert.Single(large);
        Assert.Equal(2, merged.Prints);
        Assert.Equal(10m, merged.Size);
        Assert.Equal(101.2m, merged.Price);
        Assert.Equal(TapeEmphasis.Large, merged.Emphasis);
        tape.Flush();
        Assert.Equal(3, tape.RowCount);
        Assert.Equal("bybit", tape.Rows.First().Venue);
        Assert.Equal(TradeSide.Sell, tape.Rows.First().Side);
    }

    [Fact]
    public void Thresholds_hide_small_lines_but_volume_bars_count_every_print()
    {
        var tape = new TradeTape(new TapeSettings(MinNotional: 500, LargeNotional: 10_000, HugeNotional: 100_000, AggregateSameMillisecond: false, BucketSeconds: 10));
        tape.AddTrades(new[]
        {
            new TapeTrade(T0, "okx", 100, 1, TradeSide.Buy),
            new TapeTrade(T0.AddSeconds(1), "okx", 100, 10, TradeSide.Sell),
            new TapeTrade(T0.AddSeconds(11), "okx", 100, 2000, TradeSide.Buy, Partial: true),
        });
        Assert.Equal(2, tape.RowCount);
        Assert.Equal(TapeEmphasis.Huge, tape.Rows.First().Emphasis);
        Assert.Equal(2, tape.Buckets.Count);
        var first = tape.Buckets[0].Venues["okx"];
        Assert.Equal(100m, first.Buy);
        Assert.Equal(1000m, first.Sell);
        Assert.False(first.TradesPartial);
        Assert.True(tape.Buckets[1].Venues["okx"].TradesPartial);
        Assert.Equal(100m, tape.Buckets[1].Last);
        Assert.Equal(3, tape.Totals["okx"].Prints);
        Assert.Equal(200_100m, tape.Totals["okx"].Buy);
    }

    [Fact]
    public void Rows_are_bounded_and_point_value_scales_notional()
    {
        var tape = new TradeTape(new TapeSettings(MinNotional: 0, MaxRows: 3, PointValue: 50, AggregateSameMillisecond: false));
        for (int i = 0; i < 10; i++) tape.AddTrades(new[] { new TapeTrade(T0.AddSeconds(i), "cme", 5000, 1, TradeSide.Buy) });
        Assert.Equal(3, tape.RowCount);
        Assert.Equal(250_000m, tape.Rows.First().Notional);
        Assert.Equal(T0.AddSeconds(9), tape.Rows.First().Time);
    }

    [Fact]
    public void Older_prints_extend_observation_without_fabricating_buckets_in_between()
    {
        var tape = new TradeTape(new TapeSettings(BucketSeconds: 10));
        tape.Observe(T0);
        tape.AddTrades(new[] { new TapeTrade(T0.AddSeconds(-30), "okx", 100, 1, TradeSide.Buy), new TapeTrade(T0.AddSeconds(5), "okx", 100, 1, TradeSide.Buy) });
        // Observe() keeps the earliest start: an older print extends observation, it never fabricates empty buckets in between.
        Assert.Equal(T0.AddSeconds(-30), tape.ObservedFrom);
        Assert.Equal(new[] { T0.AddSeconds(-30), T0 }, tape.Buckets.Select(b => b.Start));
    }

    [Fact]
    public void Liquidations_are_lines_above_threshold_and_bars_only_where_observed()
    {
        var tape = new TradeTape(new TapeSettings(MinLiquidationNotional: 1000, BucketSeconds: 10));
        tape.Observe(T0);
        tape.AddTrades(new[] { new TapeTrade(T0, "binance-futures", 100, 1, TradeSide.Buy), new TapeTrade(T0.AddSeconds(25), "binance-futures", 100, 1, TradeSide.Buy) });
        var shown = tape.AddLiquidations(new[]
        {
            new LiquidationEvent(T0.AddSeconds(2), DerivativesData.Binance, 100, 50, true),
            new LiquidationEvent(T0.AddSeconds(3), DerivativesData.Binance, 100, 1, false),
        });
        var line = Assert.Single(shown);
        Assert.True(line.IsLiquidation);
        Assert.Equal(TradeSide.Sell, line.Side);
        var windows = new[] { new ObservedWindow(DerivativesData.Binance, T0, T0.AddSeconds(25)) };
        tape.ApplyLiquidationWindows(windows, new[] { DerivativesData.Binance, DerivativesData.Bybit }, T0.AddSeconds(30));
        Assert.Equal(LiquidationCoverage.Observed, tape.Buckets[0].Venues[DerivativesData.Binance].Liquidations);
        Assert.Equal(5000m, tape.Buckets[0].Venues[DerivativesData.Binance].LongLiquidations);
        Assert.Equal(100m, tape.Buckets[0].Venues[DerivativesData.Binance].ShortLiquidations);
        Assert.False(tape.Buckets[0].Venues.ContainsKey(DerivativesData.Bybit));
        Assert.Equal(LiquidationCoverage.Partial, tape.Buckets[1].Venues[DerivativesData.Binance].Liquidations);
        Assert.False(tape.Buckets[1].Venues.ContainsKey(DerivativesData.Bybit));
    }

    [Fact]
    public void Coverage_of_windows_is_full_partial_or_none()
    {
        var w = new[] { new ObservedWindow("x", T0, T0.AddMinutes(5)), new ObservedWindow("x", T0.AddMinutes(5), T0.AddMinutes(10)) };
        Assert.Equal(LiquidationCoverage.Observed, TradeTape.CoverageOf(w, T0.AddMinutes(1), T0.AddMinutes(9)));
        Assert.Equal(LiquidationCoverage.Partial, TradeTape.CoverageOf(w, T0.AddMinutes(-1), T0.AddMinutes(2)));
        Assert.Equal(LiquidationCoverage.NotObserved, TradeTape.CoverageOf(w, T0.AddMinutes(11), T0.AddMinutes(12)));
    }
}

public sealed class WorkbenchDashboardTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resample_leaves_gaps_as_null_never_zero()
    {
        var points = new[] { new SeriesPoint(T0, 1), new SeriesPoint(T0.AddHours(1), 2), new SeriesPoint(T0.AddHours(5), 3) };
        var values = DerivativesDashboard.Resample(points, T0, T0.AddHours(6), TimeSpan.FromHours(1), TimeSpan.FromHours(1.5));
        // each slot takes the latest value at its end: 1h→2, 2h→2 (1 h old), 3h and 4h too old, 5h→3, 6h→3
        Assert.Equal(new double?[] { 2, 2, null, null, 3, 3 }, values);
        Assert.All(DerivativesDashboard.Resample(Array.Empty<SeriesPoint>(), T0, T0.AddHours(2), TimeSpan.FromHours(1), TimeSpan.FromHours(1)), v => Assert.Null(v));
    }

    [Fact]
    public void Liquidation_buckets_are_null_where_unobserved_and_zero_where_observed_without_events()
    {
        var events = new[] { new LiquidationEvent(T0.AddMinutes(30), "binance", 100, 2, true) };
        var windows = new[] { new ObservedWindow("binance", T0, T0.AddHours(2)) };
        var longs = DerivativesDashboard.LiquidationBuckets(events, windows, "binance", T0, T0.AddHours(4), TimeSpan.FromHours(1), true);
        Assert.Equal(new double?[] { 200, 0, null, null }, longs);
        var shorts = DerivativesDashboard.LiquidationBuckets(events, windows, null, T0, T0.AddHours(4), TimeSpan.FromHours(1), false);
        Assert.Equal(new double?[] { 0, 0, null, null }, shorts);
    }

    [Fact]
    public void Change_interval_and_eight_hour_normalization()
    {
        var oi = new[] { (T0, 100m), (T0.AddHours(1), 110m), (T0.AddHours(25), 121m) };
        Assert.Equal(10m, DerivativesDashboard.Change(oi, TimeSpan.FromHours(24), TimeSpan.FromHours(2)));
        Assert.Null(DerivativesDashboard.Change(oi, TimeSpan.FromHours(1), TimeSpan.FromMinutes(20)));
        var funding = new[] { new FundingSample(T0, 0.01m), new FundingSample(T0.AddHours(4), 0.01m), new FundingSample(T0.AddHours(8), 0.02m) };
        Assert.Equal(4, DerivativesDashboard.FundingIntervalHours(funding));
        Assert.Equal(0.04m, DerivativesDashboard.NormalizeTo8Hours(0.02m, 4));
        Assert.Null(DerivativesDashboard.FundingIntervalHours(funding.Take(1).ToArray()));
    }

    [Fact]
    public void A_venue_row_keeps_every_missing_input_null()
    {
        var now = T0.AddDays(2);
        var empty = DerivativesDashboard.BuildRow(new DerivativesVenueInputs("bybit", null, null, null, null, null, new[] { "Bybit funding: timeout" }), now);
        Assert.Null(empty.FundingPercent);
        Assert.Null(empty.Funding8hPercent);
        Assert.Null(empty.OpenInterest);
        Assert.Null(empty.OpenInterestUsd);
        Assert.Null(empty.OpenInterestChange24hPercent);
        Assert.Null(empty.PremiumPercent);
        Assert.Null(empty.LongShortRatio);
        Assert.Null(empty.LongLiquidations24h);
        Assert.Equal(LiquidationCoverage.NotObserved, empty.Liquidations24hCoverage);
        Assert.Contains("Bybit funding: timeout", empty.Notes);

        var funding = new FundingHistoryResult(new[] { new FundingSample(now.AddHours(-16), 0.01m), new FundingSample(now.AddHours(-8), 0.01m), new FundingSample(now, 0.02m) }, OpenInterestAvailability.Available);
        var oi = new OpenInterestHistoryResult(new[] { new OpenInterestSample(now.AddHours(-24), 1000), new OpenInterestSample(now.AddHours(-1), 1100), new OpenInterestSample(now, 1210) },
            now.AddHours(-24), now, OpenInterestAvailability.Available);
        var stats = new VenueMarketStats("bybit", now, 101, 100, 0.02m, now.AddHours(8));
        var ls = new LongShortResult(new[] { new LongShortSample(now, 0.6m, 0.4m) }, OpenInterestAvailability.Available);
        var liq = new LiquidationSnapshot(new[] { new LiquidationEvent(now.AddHours(-1), "bybit", 100, 3, false) },
            new[] { new ObservedWindow("bybit", now.AddHours(-2), now) }, null);
        var row = DerivativesDashboard.BuildRow(new DerivativesVenueInputs("bybit", funding, oi, stats, ls, liq, Array.Empty<string>()), now);
        Assert.Equal(0.02m, row.FundingPercent);
        Assert.Equal(8, row.FundingIntervalHours);
        Assert.Equal(0.02m, row.Funding8hPercent);
        Assert.Equal(1210m, row.OpenInterest);
        Assert.Equal(1210m * 101, row.OpenInterestUsd);
        Assert.Equal(10m, row.OpenInterestChange1hPercent);
        Assert.Equal(21m, row.OpenInterestChange24hPercent);
        Assert.Equal(1m, row.PremiumPercent);
        Assert.Equal(1.5m, row.LongShortRatio);
        // 24 hours were not fully observed: partial, with the observed short liquidation counted
        Assert.Equal(LiquidationCoverage.Partial, row.Liquidations24hCoverage);
        Assert.Equal(300m, row.ShortLiquidations24h);
        Assert.Equal(0m, row.LongLiquidations24h);
    }

    [Fact]
    public void Venue_statistics_parse_premium_ticker_and_long_short()
    {
        using var binance = JsonDocument.Parse("""{"symbol":"BTCUSDT","markPrice":"101.0","indexPrice":"100.0","lastFundingRate":"0.0001","nextFundingTime":1772352000000,"time":1772330400000}""");
        var b = DerivativesData.ParseBinancePremium(binance.RootElement, T0);
        Assert.Equal(0.01m, b.FundingRatePercent);
        Assert.Equal(1m, b.PremiumPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1772352000000).UtcDateTime, b.NextFundingUtc);
        using var bybit = JsonDocument.Parse("""{"retCode":0,"result":{"list":[{"symbol":"BTCUSDT","markPrice":"99","indexPrice":"100","fundingRate":"-0.0002","nextFundingTime":"1772352000000"}]},"time":1772330400000}""");
        var y = DerivativesData.ParseBybitTicker(bybit.RootElement, T0)!;
        Assert.Equal(-0.02m, y.FundingRatePercent);
        Assert.Equal(-1m, y.PremiumPercent);
        using var empty = JsonDocument.Parse("""{"retCode":0,"result":{"list":[]}}""");
        Assert.Null(DerivativesData.ParseBybitTicker(empty.RootElement, T0));
        using var bls = JsonDocument.Parse("""[{"symbol":"BTCUSDT","longShortRatio":"1.5","longAccount":"0.6","shortAccount":"0.4","timestamp":1772330400000}]""");
        Assert.Equal(1.5m, Assert.Single(DerivativesData.ParseBinanceLongShort(bls.RootElement)).Ratio);
        using var yls = JsonDocument.Parse("""{"result":{"list":[{"symbol":"BTCUSDT","buyRatio":"0.5","sellRatio":"0.5","timestamp":"1772330400000"}]}}""");
        Assert.Equal(1m, Assert.Single(DerivativesData.ParseBybitLongShort(yls.RootElement)).Ratio);
    }
}

public sealed class WorkbenchOptionsTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    private static OptionContract C(DateTime expiry, double strike, OptionRight right, double oi, double iv) =>
        new($"X-{strike}-{right}", expiry, strike, right, 0, 0, 1, iv, 0, 0, 0, 0, oi, 0, 1);

    [Fact]
    public void Summary_reports_walls_max_pain_expiry_rows_and_respects_filters()
    {
        var near = T0.AddDays(7);
        var far = T0.AddDays(60);
        var contracts = new List<OptionContract>();
        foreach (var k in new[] { 80.0, 90, 100, 110, 120 })
        {
            contracts.Add(C(near, k, OptionRight.Call, k == 110 ? 500 : 50, 0.5));
            contracts.Add(C(near, k, OptionRight.Put, k == 90 ? 500 : 50, 0.55));
            contracts.Add(C(far, k, OptionRight.Call, 10, 0.6));
            contracts.Add(C(far, k, OptionRight.Put, 10, 0.65));
        }
        var chain = new OptionsChain("BTC", T0, 100, contracts, "Test");
        var all = OptionsDashboard.Build(chain, 20);
        Assert.Equal(110, all.CallWall);
        Assert.Equal(90, all.PutWall);
        Assert.Equal(near, all.NearestExpiry);
        Assert.Equal(2, all.Expiries.Count);
        Assert.Equal(81, all.GexCurve.Count);
        Assert.Equal(2, all.Term.Count);
        var nearRow = all.Expiries[0];
        Assert.Equal(700, nearRow.CallOi);
        Assert.Equal(700, nearRow.PutOi);
        Assert.Equal(1, nearRow.PutCallOi);
        Assert.Equal(OptionsMath.MaxPain(chain, near), nearRow.MaxPain);
        Assert.Equal(OptionsMath.ExpectedMove(chain, near), nearRow.ExpectedMove);
        var week = OptionsDashboard.Build(chain, 20, maxDays: 30);
        Assert.Single(week.Expiries);
        Assert.Equal(contracts.Count / 2, week.Contracts);
        var narrow = OptionsDashboard.Build(chain, 10);
        Assert.Equal(new[] { 90.0, 100, 110 }, narrow.Strikes.Select(s => s.Strike));
        var none = OptionsDashboard.Build(chain with { Contracts = Array.Empty<OptionContract>() }, 20);
        Assert.Null(none.GammaFlip);
        Assert.Null(none.CallWall);
        Assert.Null(none.MaxPain);
        Assert.Empty(none.GexCurve);
    }

    [Fact]
    public void Butterfly_is_wing_average_minus_atm_in_vol_points()
    {
        var expiry = T0.AddDays(30);
        var contracts = new List<OptionContract>();
        for (int k = 60; k <= 140; k += 5)
        {
            double m = Math.Log(k / 100.0), iv = 0.5 + 0.8 * m * m;
            contracts.Add(C(expiry, k, OptionRight.Call, 10, iv));
            contracts.Add(C(expiry, k, OptionRight.Put, 10, iv));
        }
        var chain = new OptionsChain("BTC", T0, 100, contracts);
        var fly = OptionsDashboard.Butterfly25(chain, expiry);
        Assert.NotNull(fly);
        Assert.True(fly > 0, "a convex smile has a positive butterfly");
        Assert.Null(OptionsDashboard.Butterfly25(chain with { Contracts = Array.Empty<OptionContract>() }, expiry));
    }
}
