using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class MarkedPortfolioCoverageTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Instrument Instrument = new() { Symbol = "PORTFOLIO-COVERAGE", FeedId = "sim", TickSize = 0.0001m, VolumeStep = 1, PointValue = 1 };
    private const decimal Capital = 1000m;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void CarriedTwoDayWindowRejectsInteriorCutsBeforeMoneyOrRiskWeights(bool cutFrom, bool riskWeights)
    {
        var run = CarriedRun(Start, 2);
        var window = Assert.Single(run.WalkForward!.Windows);
        var mark = Assert.Single(run.Trades!);
        Assert.Equal(Start.AddDays(1), window.OutSampleFrom);
        Assert.Equal(Start.AddDays(3), window.OutSampleTo);
        Assert.Equal(100m, mark.OpeningMark);
        Assert.Equal(120m, mark.ClosingMark);
        Assert.Equal(20m, mark.AttributionNet);
        Assert.Equal(10m, mark.CarryNetCredit);
        Assert.Equal(0, run.Metrics!.TotalTrades);
        Assert.Equal(20m, run.Equity![^1].Equity - run.Equity[0].Equity);
        var marked = PortfolioComponent.FromRun(run);
        var ordinary = Ordinary(cutFrom ? Start.AddDays(2) : Start.AddDays(1),
            cutFrom ? Start.AddDays(3) : Start.AddDays(2));

        Reject(new[] { marked, ordinary }, riskWeights);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void DifferentMarkedWidthsRejectMisalignedCommonEndpoints(bool cutFrom, bool riskWeights)
    {
        var wideRun = CarriedRun(Start, 3);
        var narrowRun = CarriedRun(cutFrom ? Start.AddDays(1) : Start, 2);
        Assert.Equal(30m, wideRun.Metrics!.NetProfit);
        Assert.Equal(20m, narrowRun.Metrics!.NetProfit);
        Assert.Equal(30m, Assert.Single(wideRun.Trades!).AttributionNet);
        Assert.Equal(20m, Assert.Single(narrowRun.Trades!).AttributionNet);
        var wide = PortfolioComponent.FromRun(wideRun);
        var narrow = PortfolioComponent.FromRun(narrowRun);

        Reject(new[] { wide, narrow }, riskWeights);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void EmptyContributionWindowStillRejectsInteriorCommonEndpoints(bool cutFrom, bool riskWeights)
    {
        var run = FlatPrefixRun();
        var wf = run.WalkForward!;
        Assert.Equal(2, wf.Windows.Count);
        Assert.Equal(0m, wf.Windows[0].OutSample!.NetProfit);
        Assert.Equal(0, wf.Windows[0].OutSample!.AttributionSamples);
        Assert.DoesNotContain(run.Trades!, t => t.WindowFrom == Start.AddDays(1));
        Assert.Equal(5m, Assert.Single(run.Trades!).AttributionNet);
        var marked = PortfolioComponent.FromRun(run);
        var ordinary = Ordinary(cutFrom ? Start.AddDays(2) : Start.AddDays(1),
            cutFrom ? Start.AddDays(5) : Start.AddDays(2));

        Reject(new[] { marked, ordinary }, riskWeights);
    }

    [Fact]
    public void SharedSeamsExcludeThePrecedingMarkAndIncludeTheEndingMarkExactlyOnce()
    {
        var run = CarriedRun(Start, 2, windows: 3);
        var source = run.Trades!.ToArray();
        var marked = PortfolioComponent.FromRun(run, 2.5m);
        var ordinary = Ordinary(Start.AddDays(3), Start.AddDays(5), profit: 20m, weight: 3m);

        var result = PortfolioAnalyzer.Analyze(new[] { marked, ordinary }, Capital);

        Assert.Equal(Start.AddDays(3), result.From);
        Assert.Equal(Start.AddDays(5), result.To);
        Assert.Equal(new[] { DateOnly.FromDateTime(Start.AddDays(3)), DateOnly.FromDateTime(Start.AddDays(4)) }, result.Days);
        Assert.Equal(new[] { 0m, 50m }, result.DailyPnl[0]);
        Assert.Equal(new[] { 60m, 0m }, result.DailyPnl[1]);
        Assert.Equal(50m, result.ComponentMetrics[0].NetProfit);
        Assert.Equal(60m, result.ComponentMetrics[1].NetProfit);
        Assert.Equal(110m, result.Metrics.NetProfit);
        Assert.Equal(110m, result.Trades.Sum(t => t.NetPnl));
        Assert.Equal(110m, result.DailyPnl.Sum(p => p.Sum()));
        Assert.Equal(1110m, result.Equity[^1].Equity);
        var mark = Assert.Single(result.Trades, t => !t.HasCompleteTradeEvidence);
        Assert.Equal(Start.AddDays(3), mark.WindowFrom);
        Assert.Equal(Start.AddDays(5), mark.WindowTo);
        Assert.Equal(Start.AddDays(5), mark.ExitTime);
        Assert.Equal(50m, mark.AttributionNet);
        Assert.Equal(2.5m, mark.Quantity);
        Assert.Equal(75m, mark.CarryNetCredit);
        Assert.Equal(120m, mark.OpeningMark);
        Assert.Equal(140m, mark.ClosingMark);
        Assert.Equal(90m, mark.EntryPrice);
        Assert.Equal(140m, mark.ExitPrice);
        Assert.Equal(Start.AddHours(12).AddSeconds(1), mark.ActualEntryTime);
        Assert.Equal(source, run.Trades);
        Assert.Equal(60m, run.Metrics!.NetProfit);
        Assert.Equal(new[] { 1m, 1m }, PortfolioAnalyzer.InverseVolatilityWeights(new[] { marked, ordinary }, Capital));
        Assert.Equal(2.5m, marked.Weight);
        Assert.Equal(3m, ordinary.Weight);
    }

    [Fact]
    public void DifferentMarkedWidthsAcceptWholeCommonCoverage()
    {
        var twoDay = PortfolioComponent.FromRun(CarriedRun(Start, 2, windows: 3), 0.5m);
        var threeDay = PortfolioComponent.FromRun(CarriedRun(Start, 3, windows: 2), 0.5m);

        var result = PortfolioAnalyzer.Analyze(new[] { twoDay, threeDay }, Capital);

        Assert.Equal(Start.AddDays(1), result.From);
        Assert.Equal(Start.AddDays(7), result.To);
        Assert.Equal(6, result.Days.Count);
        Assert.Equal(30m, result.ComponentMetrics[0].NetProfit);
        Assert.Equal(30m, result.ComponentMetrics[1].NetProfit);
        Assert.Equal(60m, result.Metrics.NetProfit);
        Assert.Equal(60m, result.Trades.Sum(t => t.NetPnl));
        Assert.Equal(60m, result.DailyPnl.Sum(p => p.Sum()));
        Assert.Equal(1060m, result.Equity[^1].Equity);
        Assert.Equal(0, result.Metrics.TotalTrades);
        Assert.Equal(5, result.Metrics.AttributionSamples);
    }

    [Fact]
    public void EmptyContributionWindowAcceptsItsWholeAlignedInterval()
    {
        var run = FlatPrefixRun();
        var marked = PortfolioComponent.FromRun(run);
        var ordinary = Ordinary(Start.AddDays(1), Start.AddDays(3));

        var result = PortfolioAnalyzer.Analyze(new[] { marked, ordinary }, Capital);

        Assert.Equal(Start.AddDays(1), result.From);
        Assert.Equal(Start.AddDays(3), result.To);
        Assert.Equal(new[] { 0m, 0m }, result.DailyPnl[0]);
        Assert.DoesNotContain(result.Trades, t => !t.HasCompleteTradeEvidence);
        Assert.Equal(0m, result.ComponentMetrics[0].NetProfit);
        Assert.Equal(0m, result.Metrics.NetProfit);
        Assert.Equal(Capital, result.Equity[^1].Equity);
        Assert.Equal(5m, run.Metrics!.NetProfit);
        Assert.Equal(5m, Assert.Single(run.Trades!).AttributionNet);
    }

    [Fact]
    public void WholeWindowWeightsActualClosingFeesAndCarryCreditsExactlyOnce()
    {
        var orders = new Dictionary<long, decimal> { [1] = 2, [4] = 2, [6] = -1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
            Tick(4, Start.AddDays(1).AddHours(10), 120), Tick(5, Start.AddDays(1).AddHours(10).AddSeconds(1), 120),
            Tick(6, Start.AddDays(1).AddHours(11), 130), Tick(7, Start.AddDays(1).AddHours(11).AddSeconds(1), 130),
            Tick(8, Start.AddDays(2).AddSeconds(-1), 125),
        };
        var run = MarkedRun(Settings(Start, 2, fee: 1m), 1, orders, rows);
        Assert.Equal(42m, run.Metrics!.NetProfit);
        var source = run.Trades!.ToArray();

        var result = PortfolioAnalyzer.Analyze(new[] { PortfolioComponent.FromRun(run, 2.5m) }, Capital);

        Assert.Equal(105m, result.Metrics.NetProfit);
        Assert.Equal(105m, result.ComponentMetrics[0].NetProfit);
        Assert.Equal(7.5m, result.Metrics.Commission);
        Assert.Equal(105m, result.Trades.Sum(t => t.NetPnl));
        Assert.Equal(105m, result.DailyPnl[0].Sum());
        Assert.Equal(1105m, result.Equity[^1].Equity);
        var close = Assert.Single(result.Trades, t => t.IsActualClosingFill);
        Assert.Equal(33.75m, close.AttributionNet);
        Assert.Equal(3.75m, close.Commission);
        Assert.Equal(11.25m, close.CarryNetCredit);
        Assert.Equal(1.25m, close.CarryCommissionCredit);
        Assert.Equal(2.5m, close.Quantity);
        var mark = Assert.Single(result.Trades, t => !t.IsActualClosingFill);
        Assert.Equal(71.25m, mark.AttributionNet);
        Assert.Equal(3.75m, mark.Commission);
        Assert.Equal(33.75m, mark.CarryNetCredit);
        Assert.Equal(3.75m, mark.CarryCommissionCredit);
        Assert.Equal(7.5m, mark.Quantity);
        Assert.Equal(110m, mark.OpeningMark);
        Assert.Equal(125m, mark.ClosingMark);
        Assert.Equal(source, run.Trades);
        Assert.Equal(42m, run.Metrics.NetProfit);
    }

    [Fact]
    public void OrdinaryPortfolioKeepsWeightsInclusiveCalendarAndUnscaledExcursions()
    {
        var ordinary = Ordinary(Start, Start.AddDays(1), profit: 7m, weight: 2m);
        var source = Assert.Single(ordinary.Trades);

        var result = PortfolioAnalyzer.Analyze(new[] { ordinary }, Capital);

        Assert.Null(ordinary.ResearchCoverage);
        Assert.Equal(2, result.Days.Count);
        Assert.Equal(14m, result.Metrics.NetProfit);
        Assert.Equal(14m, result.ComponentMetrics[0].NetProfit);
        Assert.Equal(1014m, result.Equity[^1].Equity);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(2m, trade.Quantity);
        Assert.Equal(source.Mae, trade.Mae);
        Assert.Equal(source.Mfe, trade.Mfe);
        Assert.Equal(7m, source.NetPnl);
    }

    [Fact]
    public void OrdinaryDisjointPeriodsKeepTheirUnionFallback()
    {
        var first = Ordinary(Start, Start.AddDays(1), profit: 7m, weight: 2m);
        var second = Ordinary(Start.AddDays(2), Start.AddDays(3), profit: -3m);

        var result = PortfolioAnalyzer.Analyze(new[] { first, second }, Capital);

        Assert.True(result.NoOverlap);
        Assert.Equal(Start, result.From);
        Assert.Equal(Start.AddDays(3), result.To);
        Assert.Equal(4, result.Days.Count);
        Assert.Equal(11m, result.Metrics.NetProfit);
        Assert.Equal(1011m, result.Equity[^1].Equity);
    }

    private static void Reject(PortfolioComponent[] components, bool riskWeights) =>
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (riskWeights) PortfolioAnalyzer.InverseVolatilityWeights(components, Capital);
            else PortfolioAnalyzer.Analyze(components, Capital);
        });

    private static StrategyRun CarriedRun(DateTime from, int outSampleDays, int windows = 1)
    {
        int days = 1 + outSampleDays * windows;
        var rows = new List<Trade>
        {
            Tick(1, from.AddHours(10), 100), Tick(2, from.AddHours(10).AddSeconds(1), 100),
            Tick(3, from.AddHours(11), 100), Tick(4, from.AddHours(11).AddSeconds(1), 100),
            Tick(5, from.AddHours(12), 90), Tick(6, from.AddHours(12).AddSeconds(1), 90),
            Tick(7, from.AddDays(1).AddSeconds(-1), 100),
        };
        for (int day = 1; day < days; day++)
            rows.Add(Tick(rows.Count + 1, from.AddDays(day + 1).AddSeconds(-1), 100m + 10m * day));
        return MarkedRun(Settings(from, days), outSampleDays,
            new Dictionary<long, decimal> { [1] = 1, [3] = -1, [5] = 1 }, rows);
    }

    private static StrategyRun FlatPrefixRun()
    {
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddHours(11), 100), Tick(4, Start.AddHours(11).AddSeconds(1), 100),
            Tick(5, Start.AddDays(1).AddSeconds(-1), 100),
            Tick(6, Start.AddDays(2).AddSeconds(-1), 100), Tick(7, Start.AddDays(3).AddSeconds(-1), 100),
            Tick(8, Start.AddDays(3).AddHours(10), 100), Tick(9, Start.AddDays(3).AddHours(10).AddSeconds(1), 100),
            Tick(10, Start.AddDays(3).AddHours(11), 105), Tick(11, Start.AddDays(3).AddHours(11).AddSeconds(1), 105),
            Tick(12, Start.AddDays(4).AddSeconds(-1), 105), Tick(13, Start.AddDays(5).AddSeconds(-1), 105),
        };
        return MarkedRun(Settings(Start, 5), 2,
            new Dictionary<long, decimal> { [1] = 1, [3] = -1, [8] = 1, [10] = -1 }, rows);
    }

    private static StrategyRun MarkedRun(BacktestSettings settings, int outSampleDays,
        IReadOnlyDictionary<long, decimal> orders, IEnumerable<Trade> rows)
    {
        var data = TickData.FromTrades(Instrument, rows.ToArray());
        var wf = Optimizer.WalkForward(() => new ScheduledStrategy(orders), new(), Array.Empty<ParameterRange>(), data,
            settings, FitnessMetric.NetProfit, 1, outSampleDays, false, 0, null, CancellationToken.None,
            search: SearchMode.Grid, maxParallel: 1);
        Assert.True(wf.IsComplete);
        Assert.All(wf.Windows, w =>
        {
            Assert.True(w.InSampleAvailable);
            Assert.True(w.OutSampleAvailable);
            Assert.NotNull(w.InSample);
            Assert.NotNull(w.OutSample);
        });
        return new StrategyRun
        {
            Id = $"marked-{settings.FromUtc:yyyyMMdd}-{outSampleDays}", StrategyId = "coverage-strategy", StrategyName = "Coverage strategy",
            Kind = RunKind.WalkForward, CodeHash = new string('c', 64), Settings = settings,
            Metrics = wf.OutSampleMetrics, Trades = wf.OutSampleTrades, Equity = wf.OutSampleEquity,
            WalkForward = wf, InputEvidence = RunInputEvidence.Capture(data),
        };
    }

    private static PortfolioComponent Ordinary(DateTime from, DateTime to, decimal profit = 0m, decimal weight = 1m)
    {
        var rows = new[]
        {
            Tick(1, from.AddHours(10), 100), Tick(2, from.AddHours(10).AddSeconds(1), 100),
            Tick(3, from.AddHours(11), 100m + profit), Tick(4, from.AddHours(11).AddSeconds(1), 100m + profit),
        };
        var settings = Settings(from, (int)(to - from).TotalDays);
        var data = TickData.FromTrades(Instrument, rows);
        var result = new BacktestEngine(new ScheduledStrategy(new Dictionary<long, decimal> { [1] = 1, [3] = -1 }), data, settings).Run();
        Assert.True(result.Ok, result.Error);
        Assert.Equal(profit, result.Metrics.NetProfit);
        Assert.Equal(TradeAttribution.Execution, Assert.Single(result.Trades).Attribution);
        return PortfolioComponent.FromRun(new StrategyRun
        {
            Id = $"ordinary-{from:yyyyMMdd}", Kind = RunKind.Backtest, Settings = settings,
            Metrics = result.Metrics, Trades = result.Trades, Equity = result.Equity, InputEvidence = RunInputEvidence.Capture(result, data),
        }, weight);
    }

    private static BacktestSettings Settings(DateTime from, int days, decimal fee = 0m) => new()
    {
        FeedId = "sim", Symbol = Instrument.Symbol, FromUtc = from, ToUtc = from.AddDays(days), Period = "M1",
        InitialCapital = Capital, CommissionPerUnit = fee, CommissionPercent = 0m, SlippageTicks = 0,
    };

    private static Trade Tick(long id, DateTime time, decimal price) => new(id, time, price, 1m, TradeSide.Buy);

    private sealed class ScheduledStrategy(IReadOnlyDictionary<long, decimal> orders) : Strategy
    {
        protected override void OnTick(Trade trade)
        {
            if (!orders.TryGetValue(trade.Id, out decimal quantity)) return;
            if (quantity > 0) Buy(quantity);
            else Sell(-quantity);
        }
    }
}
