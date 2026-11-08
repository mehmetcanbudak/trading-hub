using System.Globalization;
using System.Text.Json;
using TradingHub.App.Lab;
using TradingHub.App.Views;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

[Collection("StrategyAssistant")]
public sealed class RunInputEvidenceConsumerTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Directory.CreateTempSubdirectory("tradinghub-input-evidence-").FullName;
    private readonly string _previousRoot = StrategyStore.Root;
    private readonly CultureInfo _previousCulture = UiCulture.Current;
    private readonly Instrument _instrument = new() { FeedId = "evidence-fixture", Symbol = "EXACT", TickSize = 0.25m, VolumeStep = 1, PointValue = 1 };

    public RunInputEvidenceConsumerTests()
    {
        StrategyStore.Root = Path.Combine(_root, "runs");
        UiCulture.Current = UiCulture.For("en");
    }

    public void Dispose()
    {
        StrategyStore.Root = _previousRoot;
        UiCulture.Current = _previousCulture;
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(RunKind.Backtest)]
    [InlineData(RunKind.Optimization)]
    [InlineData(RunKind.WalkForward)]
    public void CapturedInputEvidenceSurvivesSaveLoadHistoryMcpAndReport(RunKind kind)
    {
        var provenance = new TradeProvenance(DataQuality.Compact | DataQuality.Partial, 60, "fixture:captured-source", 17, 11);
        HistoricalData.WriteDay(_root, _instrument, DateOnly.FromDateTime(Start), new[]
        {
            new Trade(1, Start, 100m, 11m, TradeSide.Buy, provenance),
            new Trade(2, Start.AddMinutes(1), 101m, 11m, TradeSide.Sell, provenance),
        });
        var settings = Settings();
        var data = TickData.Load(_root, _instrument, settings.FromUtc, settings.ToUtc, null, CancellationToken.None);
        var result = new BacktestEngine(new NoOrders(), data, settings).Run();
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.DataWarning);
        var evidence = kind == RunKind.Backtest ? RunInputEvidence.Capture(result, data) : RunInputEvidence.Capture(data);
        var run = Run(kind, settings, evidence);
        StrategyStore.Save(run);

        var reopened = Assert.IsType<StrategyRun>(StrategyStore.Load(run.StrategyId, run.Id));
        var summary = Assert.Single(StrategyStore.List(run.StrategyId));
        Assert.Equal(evidence, reopened.InputEvidence);
        Assert.Equal(evidence, summary.InputEvidence);
        Assert.Equal(DataQuality.Compact | DataQuality.Partial, reopened.InputEvidence!.DataQuality);
        Assert.Equal(60, reopened.InputEvidence.ResolutionSeconds);
        Assert.Equal("fixture:captured-source", reopened.InputEvidence.SourceIdentity);
        Assert.Equal(17, reopened.InputEvidence.CorrectionRevision);
        Assert.True(reopened.InputEvidence.HasKnownAvailability);
        Assert.Equal(result.DataWarning, reopened.InputEvidence.DataWarning);
        Assert.False(string.IsNullOrWhiteSpace(reopened.InputEvidence.ContentIdentity));
        var json = LabServer.InputEvidenceJson(reopened.InputEvidence);
        Assert.True(json["known"]!.GetValue<bool>());
        Assert.Equal("Compact, Partial", json["data_quality"]!.GetValue<string>());
        Assert.Equal(60, json["resolution_seconds"]!.GetValue<int>());
        Assert.Equal(17, json["correction_revision"]!.GetValue<long>());
        Assert.True(json["has_known_availability"]!.GetValue<bool>());
        Assert.Equal("fixture:captured-source", json["source_identity"]!.GetValue<string>());
        Assert.Equal(result.DataWarning, json["data_warning"]!.GetValue<string>());
        Assert.Equal(data.ContentIdentity, json["content_identity"]!.GetValue<string>());
        string display = StrategiesView.InputEvidenceText(reopened.InputEvidence);
        string report = Report(reopened);
        foreach (string value in new[] { "fixture:captured-source", "Compact, Partial", "17", "60", result.DataWarning!, data.ContentIdentity! })
        {
            Assert.Contains(value, display);
            Assert.Contains(value, report);
        }
        Assert.Contains("| Known availability | Yes |", report);
        if (kind == RunKind.WalkForward)
            Assert.Equal(data.ContentIdentity, LabServer.WalkForwardJson(reopened.WalkForward!, reopened.Id, reopened.InputEvidence)["input_evidence"]!["content_identity"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(RunKind.Backtest)]
    [InlineData(RunKind.Optimization)]
    [InlineData(RunKind.WalkForward)]
    public void LegacyAbsenceRemainsUnknownInEveryConsumer(RunKind kind)
    {
        var run = Run(kind, Settings(), null);
        string directory = Path.Combine(StrategyStore.Root, run.StrategyId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, run.Id + ".json"), JsonSerializer.Serialize(run));

        var reopened = Assert.IsType<StrategyRun>(StrategyStore.Load(run.StrategyId, run.Id));
        Assert.Null(reopened.InputEvidence);
        Assert.Null(Assert.Single(StrategyStore.List(run.StrategyId)).InputEvidence);
        var json = LabServer.InputEvidenceJson(reopened.InputEvidence);
        Assert.False(json["known"]!.GetValue<bool>());
        foreach (string key in new[] { "data_quality", "resolution_seconds", "source_identity", "correction_revision", "has_known_availability", "data_warning", "content_identity" })
            Assert.Null(json[key]);
        Assert.Equal("Input evidence is unknown for this legacy run.", StrategiesView.InputEvidenceText(reopened.InputEvidence));
        string report = Report(reopened);
        Assert.Contains("Unknown legacy input evidence", report);
        foreach (string label in new[] { "Input quality", "Resolution (seconds)", "Source identity", "Correction revision", "Known availability", "Data warning", "Content identity" })
            Assert.Contains($"| {label} | Unknown |", report);
    }

    [Fact]
    public void ExplicitUnknownSourceTimingIsNotConfusedWithMissingLegacyEvidence()
    {
        var evidence = new RunInputEvidence(DataQuality.Raw | DataQuality.Partial, 0, "fixture:unknown-timing", 3, false,
            "Source timing was not declared.", "captured-content-digest");
        var run = Run(RunKind.Backtest, Settings(), evidence);
        StrategyStore.Save(run);
        var reopened = StrategyStore.Load(run.StrategyId, run.Id)!;

        var json = LabServer.InputEvidenceJson(reopened.InputEvidence);
        Assert.True(json["known"]!.GetValue<bool>());
        Assert.False(json["has_known_availability"]!.GetValue<bool>());
        Assert.Equal(0, json["resolution_seconds"]!.GetValue<int>());
        Assert.Contains("| Known availability | No |", Report(reopened));
        Assert.Contains("Source timing was not declared.", Report(reopened));
    }

    [Fact]
    public void PortfolioReportsKeepComponentInputAndSourceEvidenceWithoutRetainingAResearchResult()
    {
        var evidence = new RunInputEvidence(DataQuality.Compact | DataQuality.Partial, 60, "fixture:portfolio-source", 19, true,
            "Compact events are not an individual-trade tape.", "portfolio-input-digest");
        var run = Run(RunKind.Backtest, Settings(), evidence);
        run.Trades = new()
        {
            new ClosedTrade(1, true, Start, 100m, Start.AddMinutes(2), 101m, 1m, 1m, 0m, 0m, 1m, "Entry", "Exit", 2),
        };
        run.Equity = new() { new(Start, 10_000m, 0m), new(Start.AddMinutes(2), 10_001m, 0m) };
        run.Metrics = PerformanceMetrics.Compute(run.Trades, run.Equity, 10_000m, false, 2, 2);
        StrategyStore.Save(run, "sealed class CapturedPortfolioSource { }");
        var reopened = StrategyStore.Load(run.StrategyId, run.Id)!;
        var component = PortfolioComponent.FromRun(reopened);
        var portfolio = PortfolioAnalyzer.Analyze(new[] { component }, 10_000m);

        var json = LabServer.PortfolioJson(portfolio)["components"]![0]!;
        Assert.Equal(reopened.CodeHash, json["code_hash"]!.GetValue<string>());
        Assert.Equal("Backtest", json["kind"]!.GetValue<string>());
        Assert.Equal("fixture:portfolio-source", json["input_evidence"]!["source_identity"]!.GetValue<string>());
        Assert.Equal("portfolio-input-digest", json["input_evidence"]!["content_identity"]!.GetValue<string>());
        Assert.Equal(19, json["input_evidence"]!["correction_revision"]!.GetValue<long>());
        string report = ReportBuilder.Build(new ReportInput { Title = "Portfolio input evidence", Portfolio = portfolio }).Markdown;
        Assert.Contains(reopened.CodeHash, report);
        Assert.Contains("fixture:portfolio-source", report);
        Assert.Contains("portfolio-input-digest", report);
        Assert.Contains("Compact events are not an individual-trade tape.", report);
    }

    private BacktestSettings Settings() => new()
    {
        FeedId = _instrument.FeedId, Symbol = _instrument.Symbol, Period = "M1", FromUtc = Start, ToUtc = Start.AddMinutes(3),
        InitialCapital = 10_000m, CommissionPercent = 0m, SlippageTicks = 0,
    };

    private static StrategyRun Run(RunKind kind, BacktestSettings settings, RunInputEvidence? evidence) => new()
    {
        StrategyId = "InputEvidenceConsumer", StrategyName = "Input evidence consumer", Kind = kind, Settings = settings,
        InputEvidence = evidence,
        Optimization = kind == RunKind.Optimization ? new OptimizationResult() : null,
        WalkForward = kind == RunKind.WalkForward ? new WalkForwardResult() : null,
    };

    private static string Report(StrategyRun run)
    {
        var input = new ReportInput { Title = "Captured input evidence" };
        switch (run.Kind)
        {
            case RunKind.Backtest: input.Backtest = run; break;
            case RunKind.Optimization: input.Optimization = run; break;
            case RunKind.WalkForward: input.WalkForward = run; break;
        }
        return ReportBuilder.Build(input).Markdown;
    }

    private sealed class NoOrders : Strategy { }
}
