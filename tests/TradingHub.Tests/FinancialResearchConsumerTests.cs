using System.Globalization;
using System.Text.Json.Nodes;
using TradingHub.App.Lab;
using TradingHub.Common;
using TradingHub.Strategies;

namespace TradingHub.Tests;

[Collection("StrategyAssistant")]
public sealed class FinancialResearchConsumerTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private readonly CultureInfo _previousCulture = UiCulture.Current;

    public FinancialResearchConsumerTests() => UiCulture.Current = UiCulture.For("en");
    public void Dispose() => UiCulture.Current = _previousCulture;

    [Fact]
    public void WalkForwardPayloadKeepsExactContributionsAndUnavailableSuffix()
    {
        var result = Partial();
        var json = JsonNode.Parse(LabServer.WalkForwardJson(result, "test-run").ToJsonString())!;

        Assert.False(json["is_complete"]!.GetValue<bool>());
        Assert.False(json["trade_evidence_available"]!.GetValue<bool>());
        Assert.NotNull(json["trade_evidence_unavailable_reason"]);
        Assert.Equal(1, json["completed_windows"]!.GetValue<int>());
        Assert.Equal("warm-mark-to-market-splice", json["accounting_policy"]!.GetValue<string>());
        Assert.Equal(12.1234567890123456789m, json["out_of_sample_metrics"]!["net_profit"]!.GetValue<decimal>());
        Assert.Equal(1, json["out_of_sample_metrics"]!["trades"]!.GetValue<int>());
        Assert.Equal(2, json["out_of_sample_metrics"]!["attribution_samples"]!.GetValue<int>());
        var missing = json["windows"]![1]!;
        Assert.False(missing["out_sample_available"]!.GetValue<bool>());
        Assert.Null(missing["out_sample_net"]);
        Assert.Null(missing["out_sample_trades"]);
        Assert.Null(missing["efficiency"]);
        Assert.Equal("The selected test range is incomplete.", missing["unavailable_reason"]!.GetValue<string>());
        var closing = json["out_of_sample_contributions"]![0]!;
        Assert.Equal("window_closing_fill", closing["attribution"]!.GetValue<string>());
        Assert.Equal(10.1234567890123456789m, closing["attribution_net"]!.GetValue<decimal>());
        Assert.Equal(0.1234567890123456789m, closing["carry_commission_credit"]!.GetValue<decimal>());
        Assert.True(closing["is_actual_closing_fill"]!.GetValue<bool>());
        Assert.False(json["out_of_sample_contributions"]![1]!["is_actual_closing_fill"]!.GetValue<bool>());
        Assert.Equal(1012.1234567890123456789m, json["windows"]![0]!["out_sample_stitched_closing_equity"]!.GetValue<decimal>());
    }

    [Fact]
    public void CpcvSummaryUsesOnlyCompleteAvailablePathsAndDisclosesCoverage()
    {
        var result = Partial();
        result.CpcvPaths = new()
        {
            new() { Number = 1, Net = 7.1234567890123456789m, Sharpe = 1, MaxDrawdown = 2m, ActualClosingFills = 3 },
            new() { Number = 2, Available = false, UnavailableReason = "No eligible training configuration." },
            new() { Number = 3, Net = -1m, Sharpe = 0, MaxDrawdown = 3m, ActualClosingFills = 2 },
        };

        var summary = LabServer.WalkForwardJson(result, "test-run")["cpcv_summary"]!;
        Assert.Equal(2, summary["available_paths"]!.GetValue<int>());
        Assert.Equal(3, summary["total_paths"]!.GetValue<int>());
        Assert.Equal(0.5, summary["profitable_share"]!.GetValue<double>());
        Assert.Equal(7.1234567890123456789m, summary["median_net"]!.GetValue<decimal>());
    }

    [Fact]
    public void NoEligibleCpcvPathHasNoZeroMedianOrProfitableShare()
    {
        var result = Partial();
        result.CpcvPaths = new() { new() { Number = 1, Available = false, UnavailableReason = "No eligible training configuration." } };
        var summary = LabServer.WalkForwardJson(result, "test-run")["cpcv_summary"]!;
        Assert.Equal(0, summary["available_paths"]!.GetValue<int>());
        Assert.Null(summary["profitable_share"]);
        Assert.Null(summary["median_net"]);
        Assert.NotNull(summary["unavailable_reason"]);
    }

    [Fact]
    public void ReportsKeepExactAttributedContributionAmounts()
    {
        string report = Report(Partial());
        Assert.Contains("10.1234567890123456789", report);
        Assert.Contains("1.1234567890123456789", report);
        Assert.Contains("0.1234567890123456789", report);
    }

    [Fact]
    public void MonteCarloUnavailabilityRemainsNullInMcpAndVisibleInReport()
    {
        var contributions = Partial().OutSampleTrades;
        var mc = Optimizer.MonteCarlo(contributions, 1000m);
        var json = LabServer.MonteCarloJson(mc);
        Assert.False(json["available"]!.GetValue<bool>());
        Assert.Null(json["drawdown_p95"]);
        Assert.Null(json["ruin_probability_percent"]);
        Assert.Equal(mc.UnavailableReason, json["unavailable_reason"]!.GetValue<string>());
        string report = ReportBuilder.Build(new ReportInput
        {
            Title = "Marked research",
            Backtest = new StrategyRun
            {
                Settings = new BacktestSettings { FromUtc = Start, ToUtc = Start.AddDays(1) },
                Trades = contributions, MonteCarlo = mc,
            },
        }).Markdown;
        Assert.Contains(mc.UnavailableReason!, report);
    }

    [Fact]
    public void EmptyCoverageDoesNotPublishZeroFinancialMetrics()
    {
        var result = Partial();
        result.Windows.RemoveAt(0);
        result.OutSampleTrades.Clear();
        result.OutSampleEquity.Clear();
        var json = LabServer.WalkForwardJson(result, "test-run");
        Assert.Null(json["out_of_sample_metrics"]);
        Assert.Null(json["walk_forward_efficiency"]);
        Assert.Null(json["profitable_windows_percent"]);
    }

    [Fact]
    public void PortfolioWeightsExactAttributedMoneyAndKeepsCoverageAndPriceMarks()
    {
        var wf = Partial();
        var component = PortfolioComponent.FromRun(new StrategyRun
        {
            Kind = RunKind.WalkForward,
            Settings = new BacktestSettings { FromUtc = Start.AddDays(-1), ToUtc = Start.AddDays(3) },
            Metrics = wf.OutSampleMetrics, Trades = wf.OutSampleTrades, WalkForward = wf,
        }, 2m);
        var result = PortfolioAnalyzer.Analyze(new[] { component }, 1000m);

        Assert.Equal(Start, result.From);
        Assert.Equal(Start.AddDays(1), result.To);
        Assert.Equal(DateOnly.FromDateTime(Start), Assert.Single(result.Days));
        Assert.Equal(24.2469135780246913578m, result.Metrics.NetProfit);
        Assert.Equal(24.2469135780246913578m, result.ComponentMetrics[0].NetProfit);
        Assert.Equal(24.2469135780246913578m, result.Equity[^1].Equity - 1000m);
        Assert.Equal(24.2469135780246913578m, result.Trades.Sum(t => t.NetPnl));
        Assert.Equal(1, result.Metrics.TotalTrades);
        Assert.Equal(2, result.Metrics.AttributionSamples);
        var closing = Assert.Single(result.Trades, t => t.IsActualClosingFill);
        Assert.Equal(20.2469135780246913578m, closing.AttributionNet);
        Assert.Equal(0.2469135780246913578m, closing.CarryCommissionCredit);
        Assert.Equal(2m, closing.Quantity);
        Assert.Equal(100m, closing.OpeningMark);
        Assert.Equal(110m, closing.ClosingMark);
        Assert.Equal(Start.AddHours(-1), closing.ActualEntryTime);
        var coverage = LabServer.PortfolioJson(result)["components"]![0]!["research_coverage"]!;
        Assert.False(coverage["is_complete"]!.GetValue<bool>());
        Assert.Equal(1, coverage["completed_windows"]!.GetValue<int>());
        Assert.Equal("warm-mark-to-market-splice", coverage["accounting_policy"]!.GetValue<string>());
        Assert.Equal(20.2469135780246913578m, LabServer.PortfolioJson(result)["attributed_contributions"]![0]!["attribution_net"]!.GetValue<decimal>());
    }

    [Fact]
    public void PortfolioRejectsInternalUnavailableGapWithoutDiscardingLaterKnownWindow()
    {
        var wf = Partial();
        wf.Windows.Add(new WalkForwardWindow
        {
            Number = 3, InSample = wf.OutSampleMetrics, OutSample = wf.OutSampleMetrics,
            OutSampleFrom = Start.AddDays(2), OutSampleTo = Start.AddDays(3),
        });
        Assert.Throws<InvalidOperationException>(() => PortfolioComponent.FromRun(new StrategyRun
        {
            WalkForward = wf, Metrics = wf.OutSampleMetrics, Trades = wf.OutSampleTrades,
        }));
    }

    [Fact]
    public void PortfolioRejectsResearchWithoutAnyAvailableOutOfSampleInterval()
    {
        var wf = Partial();
        wf.Windows.RemoveAt(0);
        Assert.Throws<InvalidOperationException>(() => PortfolioComponent.FromRun(new StrategyRun
        {
            WalkForward = wf, Metrics = wf.OutSampleMetrics, Trades = wf.OutSampleTrades,
        }));
    }

    [Fact]
    public void PortfolioRejectsDisjointResearchCoverageInsteadOfZeroFillingTheUnion()
    {
        var wf = Partial();
        var first = PortfolioComponent.FromRun(new StrategyRun { WalkForward = wf, Metrics = wf.OutSampleMetrics, Trades = wf.OutSampleTrades });
        var second = new PortfolioComponent
        {
            ResearchCoverage = first.ResearchCoverage! with
            {
                From = Start.AddDays(2), To = Start.AddDays(3),
                AvailableIntervals = new[] { (Start.AddDays(2), Start.AddDays(3)) },
            },
            Trades = wf.OutSampleTrades, Metrics = wf.OutSampleMetrics,
        };
        Assert.Throws<InvalidOperationException>(() => PortfolioAnalyzer.Analyze(new[] { first, second }, 1000m));
    }

    [Fact]
    public void BoundaryValuationsCannotSatisfyPortfolioSuggestionTradeMinimum()
    {
        var mark = Partial().OutSampleTrades[1];
        var component = new PortfolioComponent
        {
            Trades = Enumerable.Range(1, 5).Select(n => mark with { Number = n }).ToList(),
            Metrics = new PerformanceMetrics { NetProfit = 10m },
        };
        Assert.Empty(PortfolioAnalyzer.SuggestUncorrelated(new[] { component }, 1000m));
    }

    [Fact]
    public void PortfolioCommonIntervalExcludesThePrecedingBoundaryContribution()
    {
        var mark = Partial().OutSampleTrades[1];
        var source = new PortfolioComponent
        {
            ResearchCoverage = new(Start, Start.AddDays(2), true, 2, 2, "warm-mark-to-market-splice", "test-attribution", null,
                new[] { (Start, Start.AddDays(1)), (Start.AddDays(1), Start.AddDays(2)) }),
            Trades = new()
            {
                mark,
                mark with { Number = 3, WindowFrom = Start.AddDays(1), WindowTo = Start.AddDays(2), ExitTime = Start.AddDays(2), AttributionNet = 3m },
            },
        };
        var ordinary = new PortfolioComponent
        {
            Settings = new BacktestSettings { FromUtc = Start.AddDays(1), ToUtc = Start.AddDays(2) },
        };

        var result = PortfolioAnalyzer.Analyze(new[] { source, ordinary }, 1000m);

        Assert.Equal(3m, result.Metrics.NetProfit);
        Assert.Equal(3m, result.Equity[^1].Equity - 1000m);
        Assert.Equal(3m, result.DailyPnl[0].Sum());
        Assert.Equal(3, Assert.Single(result.Trades).Number);
    }

    [Fact]
    public void OrdinaryBacktestPortfolioKeepsItsExistingRangeAndMetrics()
    {
        var trade = new ClosedTrade(1, true, Start, 100m, Start.AddHours(1), 110m, 1m, 10m, 1m, -2m, 12m, "entry", "exit", 1);
        var component = PortfolioComponent.FromRun(new StrategyRun
        {
            Settings = new BacktestSettings { FromUtc = Start, ToUtc = Start.AddDays(1) },
            Trades = new() { trade },
        }, 2m);
        var result = PortfolioAnalyzer.Analyze(new[] { component }, 1000m);
        Assert.Null(component.ResearchCoverage);
        Assert.Equal(Start, result.From);
        Assert.Equal(Start.AddDays(1), result.To);
        Assert.Equal(2, result.Days.Count);
        Assert.Equal(18m, result.Metrics.NetProfit);
        Assert.Equal(18m, result.ComponentMetrics[0].NetProfit);
        Assert.Equal(2m, Assert.Single(result.Trades).Quantity);
        Assert.Equal(-2m, result.Trades[0].Mae);
        Assert.Equal(12m, result.Trades[0].Mfe);
    }

    private static string Report(WalkForwardResult result) => ReportBuilder.Build(new ReportInput
    {
        Title = "Financial research",
        WalkForward = new StrategyRun
        {
            Kind = RunKind.WalkForward, WalkForward = result,
            Settings = new BacktestSettings { FromUtc = Start.AddDays(-1), ToUtc = Start.AddDays(3), InitialCapital = 1000m },
        },
    }).Markdown;

    private static WalkForwardResult Partial()
    {
        var close = new ClosedTrade(1, true, Start, 100m, Start.AddHours(12), 110m, 1m, 10m, 0m, 0m, 0m, "entry", "exit", 1)
        {
            Attribution = TradeAttribution.WindowClosingFill, AttributionNet = 10.1234567890123456789m,
            ActualEntryTime = Start.AddHours(-1), WindowFrom = Start, WindowTo = Start.AddDays(1),
            OpeningMark = 100m, ClosingMark = 110m, CarryNetCredit = 1.1234567890123456789m,
            CarryCommissionCredit = 0.1234567890123456789m,
        };
        var mark = close with { Number = 2, Attribution = TradeAttribution.WindowBoundaryValuation, AttributionNet = 2m, ExitTime = Start.AddDays(1) };
        var metrics = new PerformanceMetrics
        {
            NetProfit = 12.1234567890123456789m, TotalTrades = 1, AttributionSamples = 2,
            AttributionUnit = PerformanceAttributionUnit.WindowClosingFillAndBoundaryValuation,
        };
        return new WalkForwardResult
        {
            AccountingPolicy = "warm-mark-to-market-splice", AttributionPolicy = "closing-fill-proportional-carry-residual-and-final-mark",
            CpcvPolicy = "retrospective-warm-marked-ranges", IsComplete = false,
            OutSampleMetrics = metrics, OutSampleTrades = new() { close, mark },
            Windows = new()
            {
                new()
                {
                    Number = 1, InSampleFrom = Start.AddDays(-1), InSampleTo = Start, OutSampleFrom = Start, OutSampleTo = Start.AddDays(1),
                    InSample = metrics, OutSample = metrics, InSampleOpeningEquity = 1000m, InSampleClosingEquity = 1012.1234567890123456789m,
                    OutSampleOpeningEquity = 2000m, OutSampleClosingEquity = 2012.1234567890123456789m,
                    OutSampleStitchedOpeningEquity = 1000m, OutSampleStitchedClosingEquity = 1012.1234567890123456789m,
                },
                new()
                {
                    Number = 2, InSampleAvailable = true, OutSampleAvailable = false, InSample = metrics, OutSample = null, Efficiency = null,
                    InSampleFrom = Start, InSampleTo = Start.AddDays(1), OutSampleFrom = Start.AddDays(1), OutSampleTo = Start.AddDays(2),
                    UnavailableReason = "The selected test range is incomplete.",
                },
            },
        };
    }
}
