using System.Globalization;
using System.Text.Json;
using TradingHub.App.Lab;
using TradingHub.Common;
using TradingHub.Strategies;

namespace TradingHub.Tests;

[Collection("StrategyAssistant")]
public sealed class SavedOptimizationPopulationRegressionTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Directory.CreateTempSubdirectory("tradinghub-saved-population-").FullName;
    private readonly string _previousRoot = StrategyStore.Root;
    private readonly CultureInfo _previousUiCulture = UiCulture.Current;

    public SavedOptimizationPopulationRegressionTests()
    {
        StrategyStore.Root = _root;
        UiCulture.Current = UiCulture.For("en");
    }

    public void Dispose()
    {
        StrategyStore.Root = _previousRoot;
        UiCulture.Current = _previousUiCulture;
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SavedReportDoesNotPresentRetainedPrefixProfitabilityAsFullPopulation()
    {
        var run = LargePopulation();
        AssertProfitability(Report(run), 50.05m, allowUnavailable: false);

        var reopened = SaveAndReopen(run);

        AssertProfitability(Report(reopened), 50.05m, allowUnavailable: false);
    }

    [Fact]
    public void SavedReportDoesNotPresentRetainedPrefixSensitivityAsFullPopulation()
    {
        var run = LargePopulation();
        AssertModeSensitivity(Report(run), 1001, 50.05m, -99.89m, allowUnavailable: false);

        var reopened = SaveAndReopen(run);

        AssertModeSensitivity(Report(reopened), 1001, 50.05m, -99.89m, allowUnavailable: false);
    }

    [Fact]
    public void SavedOptimizationKeepsExactRecommendationOutsideRetainedPrefix()
    {
        var run = LargePopulation();
        var optimization = Assert.IsType<OptimizationResult>(run.Optimization);
        Assert.True(optimization.RecommendedIndex >= 1000, "The selected configuration must be outside the retained prefix.");
        AssertRecommendation(optimization, lookback: 1000, mode: 0, netProfit: 10m);

        var reopened = SaveAndReopen(run);

        AssertRecommendation(Assert.IsType<OptimizationResult>(reopened.Optimization), lookback: 1000, mode: 0, netProfit: 10m);
    }

    [Fact]
    public void CompleteSmallSavedPopulationStillReportsKnownStatisticsAndExactRecommendation()
    {
        var run = Run(new OptimizationResult
        {
            Metric = FitnessMetric.NetProfit,
            Combinations = 4,
            Ranges = Ranges(1),
            Rows = new()
            {
                Row(0, 0, 100m),
                Row(1, 1, 100m),
                Row(0, 1, -300m),
                Row(1, 0, -300m),
            },
            RecommendedIndex = 1,
        });

        var reopened = SaveAndReopen(run);
        string report = Report(reopened);

        AssertProfitability(report, 50m, allowUnavailable: false);
        AssertModeSensitivity(report, 2, 50m, -100m, allowUnavailable: false);
        AssertRecommendation(Assert.IsType<OptimizationResult>(reopened.Optimization), lookback: 1, mode: 1, netProfit: 100m);
    }

    [Fact]
    public void SavingBoundsOnlyTheStoredRowsAndDoesNotMutateTheLivePopulation()
    {
        var run = LargePopulation();
        var live = run.Optimization!;
        var rows = live.Rows;
        var recommendation = live.Recommended;

        var reopened = SaveAndReopen(run);

        Assert.Same(rows, live.Rows);
        Assert.Equal(2002, live.Rows.Count);
        Assert.Same(recommendation, live.Recommended);
        Assert.Null(live.Population);
        Assert.Equal(1000, reopened.Optimization!.Rows.Count);
        Assert.Equal(2002, reopened.Optimization.Population!.MeasuredCombinations);
        Assert.NotNull(reopened.Optimization.RecommendedRow);
        var json = LabServer.OptimizationPopulationJson(reopened.Optimization);
        Assert.True(json["available"]!.GetValue<bool>());
        Assert.Equal(50.04995004995004995004995005m, json["profitable_combinations_percent"]!.GetValue<decimal>());
        Assert.Equal("1000", LabServer.RecommendationJson(reopened.Optimization)!["parameters"]!["Lookback"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyCompletePopulationRemainsKnownButTruncatedPopulationAndRecommendationAreUnknown(bool truncated)
    {
        var run = LargePopulation();
        run.Optimization!.RecommendedIndex = 0;
        if (truncated) run.Optimization.Rows = run.Optimization.Rows.Take(1000).ToList();
        string directory = Path.Combine(_root, run.StrategyId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, run.Id + ".json"), JsonSerializer.Serialize(run));

        var reopened = StrategyStore.Load(run.StrategyId, run.Id)!;
        string report = Report(reopened);
        var population = LabServer.OptimizationPopulationJson(reopened.Optimization!);

        Assert.Equal(!truncated, population["available"]!.GetValue<bool>());
        if (truncated)
        {
            Assert.Null(reopened.Optimization!.Recommended);
            Assert.Null(LabServer.RecommendationJson(reopened.Optimization));
            Assert.Null(population["profitable_combinations_percent"]);
            Assert.Null(population["parameter_marginals"]);
            Assert.Contains("Complete-population sensitivity is unavailable", report);
            Assert.Contains("The exact recommended combination is unavailable.", report);
        }
        else
        {
            AssertProfitability(report, 50.05m, allowUnavailable: false);
            AssertModeSensitivity(report, 1001, 50.05m, -99.89m, allowUnavailable: false);
            AssertRecommendation(reopened.Optimization!, lookback: 0, mode: 0, netProfit: 100m);
        }
    }

    private static StrategyRun LargePopulation()
    {
        var rows = new List<OptimizationRow>();
        for (int lookback = 0; lookback <= 1000; lookback++)
        for (int mode = 0; mode <= 1; mode++)
        {
            decimal net = lookback == 1000 ? 10m : (lookback + mode) % 2 == 0 ? 100m : -300m;
            rows.Add(Row(lookback, mode, net));
        }

        rows = rows.OrderByDescending(row => row.Fitness).ThenByDescending(row => row.Metrics.NetProfit).ToList();
        return Run(new OptimizationResult
        {
            Metric = FitnessMetric.NetProfit,
            Combinations = 2002,
            Ranges = Ranges(1000),
            Rows = rows,
            RecommendedIndex = RobustSelection.Select(rows.Select(row => (row.Index!, row.Fitness)).ToList()),
        });
    }

    private static List<ParameterRange> Ranges(int maximumLookback) => new()
    {
        new() { Name = "SignalMode", Label = "Signal mode", Min = 0, Max = 1, Step = 1, IsInteger = true },
        new() { Name = "Lookback", Label = "Lookback", Min = 0, Max = maximumLookback, Step = 1, IsInteger = true },
    };

    private static OptimizationRow Row(int lookback, int mode, decimal netProfit)
    {
        var trade = new ClosedTrade(1, true, Start, 1000m, Start.AddHours(1), 1000m + netProfit,
            1m, netProfit, 0m, Math.Min(0m, netProfit), Math.Max(0m, netProfit), "Entry", "Exit", 1);
        var metrics = PerformanceMetrics.Compute(new[] { trade }, new[]
        {
            new EquityPoint(Start, 10_000m, 0m),
            new EquityPoint(trade.ExitTime, 10_000m + netProfit, Math.Max(0m, -netProfit)),
        }, 10_000m, false, 1, 1);
        return new OptimizationRow
        {
            Index = new[] { mode, lookback },
            Parameters = new() { ["Lookback"] = lookback, ["SignalMode"] = mode },
            Metrics = metrics,
            Fitness = (double)netProfit,
        };
    }

    private static StrategyRun Run(OptimizationResult optimization) => new()
    {
        StrategyId = "SavedPopulationRegression",
        StrategyName = "Saved population regression",
        Kind = RunKind.Optimization,
        Settings = new BacktestSettings
        {
            FeedId = "test", Symbol = "TEST", Period = "M1", FromUtc = Start, ToUtc = Start.AddDays(1),
            InitialCapital = 10_000m, CommissionPercent = 0m, SlippageTicks = 0,
        },
        Optimization = optimization,
    };

    private static StrategyRun SaveAndReopen(StrategyRun run)
    {
        StrategyStore.Save(run);
        var summary = Assert.Single(StrategyStore.List(run.StrategyId), summary => summary.Id == run.Id);
        return Assert.IsType<StrategyRun>(StrategyStore.Load(run.StrategyId, summary.Id));
    }

    private static string Report(StrategyRun run) => ReportBuilder.Build(new ReportInput
    {
        Title = "Saved optimization population",
        Optimization = run,
    }).Markdown;

    private static void AssertRecommendation(OptimizationResult optimization, int lookback, int mode, decimal netProfit)
    {
        var recommended = Assert.IsType<OptimizationRow>(optimization.Recommended);
        Assert.Equal(new[] { "Lookback", "SignalMode" }, recommended.Parameters.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(lookback, JsonSerializer.SerializeToElement(recommended.Parameters["Lookback"]).GetInt32());
        Assert.Equal(mode, JsonSerializer.SerializeToElement(recommended.Parameters["SignalMode"]).GetInt32());
        Assert.Equal(netProfit, recommended.Metrics.NetProfit);
    }

    private static void AssertProfitability(string report, decimal expected, bool allowUnavailable)
    {
        var row = Assert.Single(TableRows(report), row => row.Length == 2 && row[0] == "Profitable combinations");
        AssertStatistic(row[1], expected, allowUnavailable);
    }

    private static void AssertModeSensitivity(string report, int combinations, decimal profitablePercent, decimal averageNet, bool allowUnavailable)
    {
        var lines = report.Split('\n').Select(line => line.Trim()).ToArray();
        int sectionStart = Array.FindIndex(lines, line => line == "### Parameter sensitivity");
        Assert.True(sectionStart >= 0, "The report must expose parameter sensitivity or explicitly state that it is unavailable.");
        int sectionEnd = Array.FindIndex(lines, sectionStart + 1, line => line.StartsWith("## ", StringComparison.Ordinal) || line == "---");
        if (sectionEnd < 0) sectionEnd = lines.Length;
        var section = lines[(sectionStart + 1)..sectionEnd];
        int modeStart = Array.FindIndex(section, line => line == "**Signal mode**");
        if (modeStart < 0)
        {
            Assert.True(allowUnavailable && ExplicitlyUnavailable(string.Join('\n', section)),
                "The saved report must expose full-population sensitivity or explicitly state that it is unavailable.");
            return;
        }

        int modeEnd = Array.FindIndex(section, modeStart + 1, line => line.StartsWith("**", StringComparison.Ordinal) || line.StartsWith('#'));
        if (modeEnd < 0) modeEnd = section.Length;
        string modeBlock = string.Join('\n', section[(modeStart + 1)..modeEnd]);
        var rows = TableRows(modeBlock).Where(row => row[0] is "0" or "1").ToArray();
        if (rows.Length == 0)
        {
            Assert.True(allowUnavailable && ExplicitlyUnavailable(modeBlock),
                "Missing sensitivity values require an explicit unavailable result.");
            return;
        }

        Assert.Equal(new[] { "0", "1" }, rows.Select(row => row[0]).OrderBy(value => value, StringComparer.Ordinal));
        foreach (var row in rows)
        {
            Assert.Equal(5, row.Length);
            AssertStatistic(row[3], averageNet, allowUnavailable);
            AssertStatistic(row[2], profitablePercent, allowUnavailable);
            AssertStatistic(row[1], combinations, allowUnavailable);
        }
    }

    private static IEnumerable<string[]> TableRows(string markdown) => markdown.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.StartsWith('|') && line.EndsWith('|'))
        .Select(line => line[1..^1].Split('|').Select(cell => cell.Trim()).ToArray());

    private static void AssertStatistic(string cell, decimal expected, bool allowUnavailable)
    {
        if (decimal.TryParse(cell.Replace("%", "").Trim(), NumberStyles.Number, UiCulture.Current, out decimal actual))
        {
            Assert.Equal(expected, actual);
            return;
        }

        Assert.True(allowUnavailable && ExplicitlyUnavailable(cell),
            $"Expected the full-population value {expected} or explicit unavailability, but the report contains '{cell}'.");
    }

    private static bool ExplicitlyUnavailable(string text) => new[] { "unavailable", "unknown", "not available", "not retained" }
        .Any(reason => text.Contains(reason, StringComparison.OrdinalIgnoreCase));
}
