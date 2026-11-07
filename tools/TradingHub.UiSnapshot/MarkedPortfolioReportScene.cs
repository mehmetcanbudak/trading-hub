using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Lab;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Market;
using TradingHub.Strategies;

internal static class MarkedPortfolioReportScene
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private const decimal Capital = 1000m;
    private const decimal Weight = 1.25m;
    private sealed record Observation(string Id, bool Passed, string Description, string Actual);
    private sealed record ReportBundle(string Label, string SavedPath, string MarkdownPath, string HtmlPath,
        string Markdown, string Html, string[] Images, int ReportedCharts);

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        var observations = new List<Observation>();
        var reports = new List<ReportBundle>();
        var runs = new List<StrategyRun>();
        string? fixtureError = null;
        string nonce = Guid.NewGuid().ToString("N");

        void Check(string id, bool passed, string description, string actual)
        {
            observations.Add(new(id, passed, description, actual));
            Console.WriteLine($"{(passed ? "OK" : "FAIL")} marked portfolio report: {description}. {actual}");
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Marked portfolio report fixture precondition failed. " + message);
        }
        JsonObject Call(string name, JsonObject arguments)
        {
            var request = new JsonObject { ["name"] = name, ["arguments"] = arguments };
            var task = LabServer.CallAsync(JsonNode.Parse(request.ToJsonString()));
            var watchdog = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (watchdog.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("The actual saved-report consumer did not return.");
                pump(0.01);
            }
            var result = task.GetAwaiter().GetResult();
            Require(!result["isError"]!.GetValue<bool>(), result.ToJsonString());
            return JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        }
        CompiledStrategyGeneration Compile(string prefix)
        {
            string className = prefix + nonce;
            string path = Path.Combine(AppPaths.Strategies, className + ".cs");
            Require(!File.Exists(path), "Every generated strategy source must start absent.");
            File.WriteAllText(path, $$"""
                using TradingHub.Market;
                using TradingHub.Strategies;
                [Strategy("{{prefix}}")]
                public sealed class {{className}} : Strategy
                {
                    protected override void OnTick(Trade trade)
                    {
                        if (trade.Id == 1 || trade.Id == 4) Buy(2);
                        else if (trade.Id == 6) Sell(1);
                    }
                }
                """);
            var catalog = App.Services.Strategies;
            catalog.Refresh();
            var compiled = catalog.Compile(catalog.Files.Single(file => file.Path == path));
            return compiled.Generation ?? throw new InvalidOperationException(string.Join("; ", compiled.Diagnostics.Select(diagnostic => diagnostic.Message)));
        }
        StrategyRun Save(CompiledStrategyGeneration generation, RunKind kind, BacktestSettings settings,
            PerformanceMetrics metrics, List<ClosedTrade> trades, List<EquityPoint> equity, RunInputEvidence input,
            WalkForwardResult? walkForward = null)
        {
            var run = new StrategyRun
            {
                Id = kind + "-" + runs.Count.ToString(CultureInfo.InvariantCulture) + "-" + nonce,
                StrategyId = generation.Id, StrategyName = generation.DisplayName, Kind = kind,
                CreatedUtc = Start.AddMinutes(runs.Count), Settings = settings, Metrics = metrics,
                Trades = trades, Equity = equity, WalkForward = walkForward, InputEvidence = input,
            };
            StrategyStore.Save(run, generation.Source.Text);
            var reopened = StrategyStore.Load(generation.Id, run.Id) ?? throw new IOException("The actual saved component could not be reopened.");
            Require(reopened.CodeHash == generation.SourceDigest && StrategyStore.LoadCode(reopened) == generation.Source.Text,
                "Each saved run must retain the exact source that was executed.");
            runs.Add(reopened);
            return reopened;
        }
        JsonArray Components(CompiledStrategyGeneration generation, StrategyRun run, decimal weight) => new()
        {
            new JsonObject { ["file"] = generation.FileName, ["run_id"] = run.Id, ["weight"] = weight },
        };
        ReportBundle Export(string label, JsonObject arguments)
        {
            arguments["title"] = label;
            arguments["summary"] = "Saved-component financial consumer fixture.";
            arguments["analysis"] = "Only deterministic local input and saved run IDs are used.";
            arguments["capital"] = Capital;
            var result = Call("save_report", arguments);
            string savedPath = result["saved"]!.GetValue<string>();
            var saved = ReportStore.List().Single(report => report.Path == savedPath);
            string htmlPath = ReportExport.Export(saved);
            string assets = Path.ChangeExtension(savedPath, null) + "_archivos";
            string[] images = Directory.Exists(assets)
                ? Directory.EnumerateFiles(assets).Select(path => Path.GetFileName(path)).OrderBy(name => name, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            string directory = Path.Combine(outDir, label);
            Require(!Directory.Exists(directory), "Each exported evidence bundle must start absent.");
            Directory.CreateDirectory(directory);
            string markdownCopy = Path.Combine(directory, "report.md");
            string htmlCopy = Path.Combine(directory, "report.html");
            File.Copy(savedPath, markdownCopy);
            File.Copy(htmlPath, htmlCopy);
            if (Directory.Exists(assets))
            {
                string copiedAssets = Path.Combine(directory, Path.GetFileName(assets));
                Directory.CreateDirectory(copiedAssets);
                foreach (string name in images) File.Copy(Path.Combine(assets, name), Path.Combine(copiedAssets, name));
            }
            var bundle = new ReportBundle(label, savedPath, markdownCopy, htmlCopy, File.ReadAllText(savedPath),
                File.ReadAllText(htmlPath), images, result["charts"]!.GetValue<int>());
            reports.Add(bundle);
            Check(label + "-saved-bundle", bundle.ReportedCharts == images.Length &&
                Regex.Matches(bundle.Html, "<figure><img ").Count == images.Length && !bundle.Markdown.Contains("{{assets}}", StringComparison.Ordinal),
                label + " persists the real reported image bundle and embeds it through the production HTML exporter",
                $"Reported {bundle.ReportedCharts}, saved {images.Length}, exported {Regex.Matches(bundle.Html, "<figure><img ").Count}");
            return bundle;
        }
        void CheckEquity(ReportBundle report, bool separatePortfolio)
        {
            int capitalReferences = Regex.Matches(report.Markdown, @"!\[[^\]]*\]\([^\r\n)]*/capital\.png\)").Count;
            int portfolioReferences = Regex.Matches(report.Markdown, @"!\[[^\]]*\]\([^\r\n)]*/portafolio\.png\)").Count;
            int capitalHtml = Regex.Matches(report.Html, "<img alt=\"Equity curve and drawdown\"").Count;
            int portfolioHtml = Regex.Matches(report.Html, "<img alt=\"Portfolio equity curve\"").Count;
            Check(report.Label + "-measured-equity", report.Images.Count(name => name == "capital.png") == 1 && capitalReferences == 1 && capitalHtml == 1 &&
                report.Images.Count(name => name == "portafolio.png") == (separatePortfolio ? 1 : 0) &&
                portfolioReferences == (separatePortfolio ? 1 : 0) && portfolioHtml == (separatePortfolio ? 1 : 0),
                report.Label + " renders each available equity/drawdown curve exactly once without requiring independent-trade evidence",
                $"Images [{string.Join(", ", report.Images)}], capital references/HTML {capitalReferences}/{capitalHtml}, portfolio references/HTML {portfolioReferences}/{portfolioHtml}");
        }
        void CheckAttribution(ReportBundle report)
        {
            var amounts = Rows(Section(report.Markdown, "### Contribution amounts"));
            decimal[][] expected =
            {
                new[] { 1.25m, 18.75m, 0.24375m, 18.50625m, 18.50625m, 6.16875m, 0.08125m },
                new[] { 3.75m, 37.5m, 0.24375m, 37.25625m, 37.25625m, 18.50625m, 0.24375m },
                new[] { 3.75m, -37.5m, 0m, -37.5m, -37.5m, 55.7625m, 0.4875m },
            };
            bool exact = amounts.Count == 3 && amounts.Select((row, index) => row.Length == 11 &&
                row[1] == (index == 0 ? "WindowClosingFill" : "WindowBoundaryValuation") && row[2] == (index == 0 ? "Yes" : "No") &&
                row.Skip(4).Select(value => decimal.Parse(value, CultureInfo.InvariantCulture)).SequenceEqual(expected[index])).All(value => value);
            Check(report.Label + "-exact-contributions", exact,
                report.Label + " retains exact weighted money, actual closing fills, final valuations and carried credits",
                string.Join("; ", amounts.Select(row => string.Join(" | ", row))));
            var boundaries = Rows(Section(report.Markdown, "### Contribution boundaries"));
            bool metadata = boundaries.Count == 3 && boundaries.Select((row, index) => row.Length == 10 &&
                DateTime.Parse(row[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) == Start.AddHours(10).AddSeconds(1) &&
                DateTime.Parse(row[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) == Start.AddDays(index == 2 ? 2 : 1) &&
                DateTime.Parse(row[5], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) == Start.AddDays(index == 2 ? 3 : 2) &&
                decimal.Parse(row[6], CultureInfo.InvariantCulture) == 110m &&
                decimal.Parse(row[8], CultureInfo.InvariantCulture) == (index == 2 ? 125m : 110m) &&
                decimal.Parse(row[9], CultureInfo.InvariantCulture) == (index == 2 ? 115m : 125m)).All(value => value);
            Check(report.Label + "-contribution-boundaries", metadata,
                report.Label + " retains original entry, source-window boundaries and unscaled prices/marks",
                string.Join("; ", boundaries.Select(row => string.Join(" | ", row))));
        }

        try
        {
            foreach (string variable in new[] { "TRADINGHUB_DATA_DIR", "TRADINGHUB_STRATEGIES_DIR", "TRADINGHUB_INDICATORS_DIR",
                "TRADINGHUB_BACKTESTS_DIR", "TRADINGHUB_LIVE_DIR", "TRADINGHUB_KEYCHAIN_FILE" })
                Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)), "An isolated store is required for " + variable);
            Require(Environment.GetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD") == "off" && Lang.Current == Lang.English,
                "This canonical English report fixture requires the sleep guard off.");
            var markedGeneration = Compile("MarkedReportComponent");
            var ordinaryGeneration = Compile("OrdinaryReportComponent");
            Require(Path.GetFullPath(StrategyStore.Root) == Path.GetFullPath(AppPaths.Backtests), "Saved runs must use the isolated backtest store.");
            var instrument = new Instrument { Symbol = "MARKED-REPORT", FeedId = "test", TickSize = 0.01m, VolumeStep = 1m, PointValue = 1m };
            var provenance = new TradeProvenance(DataQuality.Raw, SourceIdentity: "marked-report-fixture", CorrectionRevision: 7);
            Trade Tick(long id, DateTime time, decimal price) => new(id, time, price, 1m, TradeSide.Buy, provenance);
            var data = TickData.FromTrades(instrument, new[]
            {
                Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
                Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
                Tick(4, Start.AddDays(1).AddHours(10), 120), Tick(5, Start.AddDays(1).AddHours(10).AddSeconds(1), 120),
                Tick(6, Start.AddDays(1).AddHours(11), 130), Tick(7, Start.AddDays(1).AddHours(11).AddSeconds(1), 130),
                Tick(8, Start.AddDays(2).AddSeconds(-1), 125),
                Tick(9, Start.AddDays(2).AddHours(12), 120), Tick(10, Start.AddDays(3).AddSeconds(-1), 115),
            });
            var settings = new BacktestSettings
            {
                FeedId = instrument.FeedId, Symbol = instrument.Symbol, FromUtc = Start, ToUtc = Start.AddDays(3),
                Period = "M1", InitialCapital = Capital, CommissionPerUnit = 0.13m, CommissionPercent = 0m, SlippageTicks = 0,
            };
            WalkForwardResult WalkForward(CompiledStrategyGeneration generation, int minTrades) => Optimizer.WalkForward(generation.Create, new(), Array.Empty<ParameterRange>(),
                data, settings.Clone(), FitnessMetric.NetProfit, 1, 1, false, minTrades, null, CancellationToken.None,
                search: SearchMode.Grid, maxParallel: 1);
            var measured = WalkForward(markedGeneration, 0);
            Require(measured.IsComplete && measured.Windows.Count == 2 && measured.Windows.All(value => value.InSampleAvailable && value.OutSampleAvailable) &&
                measured.OutSampleMetrics.NetProfit == 14.61m && measured.OutSampleTrades.Count == 3 &&
                measured.OutSampleTrades.Sum(value => value.NetPnl) == 14.61m && measured.OutSampleEquity[^1].Equity == 1014.61m,
                "The real warm WFO producer must measure 44.61 then -30 without fabricating independent closing fills.");
            var marked = Save(markedGeneration, RunKind.WalkForward, settings.Clone(), measured.OutSampleMetrics,
                measured.OutSampleTrades, measured.OutSampleEquity, RunInputEvidence.Capture(data), measured);
            var ordinaryResult = new BacktestEngine(ordinaryGeneration.Create(), data, settings.Clone()).Run();
            Require(ordinaryResult.Ok && ordinaryResult.Metrics.NetProfit == 33.96m && ordinaryResult.Trades.All(value => value.HasCompleteTradeEvidence),
                "The ordinary control must execute its own actual fills and fees. " + ordinaryResult.Error);
            var ordinary = Save(ordinaryGeneration, RunKind.Backtest, settings.Clone(), ordinaryResult.Metrics,
                ordinaryResult.Trades, ordinaryResult.Equity, RunInputEvidence.Capture(ordinaryResult, data));
            var portfolio = PortfolioAnalyzer.Analyze(new[] { PortfolioComponent.FromRun(marked, Weight) }, Capital);
            Check("marked-measured-input", portfolio.From == Start.AddDays(1) && portfolio.To == Start.AddDays(3) &&
                portfolio.Equity.Select(value => value.Equity).SequenceEqual(new[] { 1000m, 1055.7625m, 1018.2625m }) &&
                portfolio.Equity[^1].Drawdown == 37.5m && portfolio.Metrics.NetProfit == 18.2625m && portfolio.Metrics.TotalTrades == 1 &&
                portfolio.Metrics.AttributionSamples == 3 && portfolio.Trades.All(value => !value.HasCompleteTradeEvidence),
                "The aligned saved marked component supplies measured equity and drawdown without independent completed trades",
                JsonSerializer.Serialize(new { portfolio.From, portfolio.To, portfolio.Equity, portfolio.Metrics.NetProfit, portfolio.Metrics.TotalTrades, portfolio.Metrics.AttributionSamples }));

            var markedOnly = Export("marked-portfolio-only", new JsonObject { ["portfolio"] = Components(markedGeneration, marked, Weight) });
            CheckEquity(markedOnly, separatePortfolio: false);
            CheckAttribution(markedOnly);
            string markedSummary = ExecutiveSummary(markedOnly.Markdown);
            Check("marked-independent-analytics-unavailable", Cell(markedSummary, "Actual closing fills") == "1" && Cell(markedSummary, "Attribution samples") == "3" &&
                new[] { "SQN", "SQN (system quality)", "Average / median / maximum duration", "Average MAE (adverse excursion)", "Average MFE (favorable excursion)" }
                    .All(label => Cell(markedOnly.Markdown, label) == null),
                "Measured marked equity does not resurrect independent-trade confidence, duration or excursion tables", markedSummary);
            var coverage = Rows(Section(markedOnly.Markdown, "| Component | Available from"));
            string componentEvidence = Section(markedOnly.Markdown, "### Component input evidence");
            Check("marked-saved-metadata", Cell(componentEvidence, "Run") == marked.Id && Cell(componentEvidence, "Code version") == marked.CodeHash &&
                Cell(componentEvidence, "Run kind") == "WalkForward" && Cell(componentEvidence, "Input quality") == "Raw" &&
                Cell(componentEvidence, "Resolution (seconds)") == "0" && Cell(componentEvidence, "Source identity") == "marked-report-fixture" &&
                Cell(componentEvidence, "Correction revision") == "7" && Cell(componentEvidence, "Known availability") == "Yes" &&
                Cell(componentEvidence, "Data warning") == "None" && Cell(componentEvidence, "Content identity") == "Unknown" &&
                coverage is [{ Length: 8 }] && coverage[0][3] == "2 of 2" && coverage[0][4] == "Complete" && coverage[0][7] == "Unknown",
                "The report retains saved source/input facts, complete OOS coverage and unknown content/CPCV identities", componentEvidence);

            var ordinaryOnly = Export("ordinary-portfolio-only", new JsonObject { ["portfolio"] = Components(ordinaryGeneration, ordinary, 1m) });
            CheckEquity(ordinaryOnly, separatePortfolio: false);
            Check("ordinary-analytics-control", Cell(ordinaryOnly.Markdown, "Average / median / maximum duration") != null &&
                Cell(ordinaryOnly.Markdown, "Average MAE (adverse excursion)") != null && Cell(ExecutiveSummary(ordinaryOnly.Markdown), "SQN") != null,
                "Ordinary portfolio-only reports retain their independent-trade diagnostics", ordinaryOnly.HtmlPath);
            var mixed = Export("ordinary-main-marked-portfolio", new JsonObject
            {
                ["file"] = ordinaryGeneration.FileName, ["backtest_run_id"] = ordinary.Id,
                ["portfolio"] = Components(markedGeneration, marked, Weight),
            });
            CheckEquity(mixed, separatePortfolio: true);
            CheckAttribution(mixed);
            Check("mixed-main-analytics-control", Cell(mixed.Markdown, "Average / median / maximum duration") != null &&
                Cell(ExecutiveSummary(mixed.Markdown), "SQN") != null,
                "A separate ordinary main retains its own trade analytics beside the marked portfolio", mixed.HtmlPath);

            var unavailable = WalkForward(ordinaryGeneration, 100);
            Require(!unavailable.IsComplete && unavailable.Windows.Count == 2 && unavailable.Windows.All(value =>
                !value.InSampleAvailable && value.InSample == null && !value.OutSampleAvailable && value.OutSample == null) &&
                unavailable.OutSampleTrades.Count == 0 && unavailable.OutSampleEquity.Count == 0,
                "The separate composite fixture must come from a real WFO with no eligible completed OOS window.");
            var noOos = Save(ordinaryGeneration, RunKind.WalkForward, settings.Clone(), unavailable.OutSampleMetrics,
                unavailable.OutSampleTrades, unavailable.OutSampleEquity, RunInputEvidence.Capture(data), unavailable);
            var composite = Export("ordinary-main-no-oos-wfo", new JsonObject
            {
                ["file"] = ordinaryGeneration.FileName, ["backtest_run_id"] = ordinary.Id, ["walk_forward_run_id"] = noOos.Id,
            });
            string compositeSummary = ExecutiveSummary(composite.Markdown);
            Check("composite-no-oos-summary-availability", Cell(compositeSummary, "Walk-forward efficiency") == "Unavailable" &&
                Cell(compositeSummary, "Profitable windows (WF)") == "Unavailable",
                "The pre-existing composite summary keeps no-complete-OOS efficiency and profitable-window share unavailable",
                $"Efficiency {Cell(compositeSummary, "Walk-forward efficiency")}, profitable windows {Cell(compositeSummary, "Profitable windows (WF)")}");
            Check("composite-no-oos-dedicated-control", Cell(composite.Markdown, "Coverage") == "Unavailable" &&
                Cell(composite.Markdown, "Completed windows") == "0 of 2" && Cell(composite.Markdown, "Out-of-sample net PnL") == "Unavailable" &&
                Cell(composite.Markdown, "Average / median / maximum duration") != null,
                "The separate no-OOS report retains the existing unavailable WFO section and available ordinary-main diagnostics", composite.HtmlPath);

            window.Navigate("strategies");
            var view = (StrategiesView)window.Page("strategies")!;
            view.OnShown();
            view.OpenReport(markedOnly.SavedPath);
            pump(0.1);
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(outDir, "marked-portfolio-report-app.png"));
        }
        catch (Exception error)
        {
            fixtureError = error.ToString();
            Check("fixture", false, "The real saved-report fixture reaches every consumer without an incidental setup failure", fixtureError);
        }
        finally
        {
            File.WriteAllText(Path.Combine(outDir, "marked-portfolio-report-evidence.json"), JsonSerializer.Serialize(new
            {
                scene = "marked-portfolio-report", language = Lang.Current, fixtureError,
                failures = observations.Count(value => !value.Passed), observations,
                reports = reports.Select(report => new { report.Label, report.SavedPath, report.MarkdownPath, report.HtmlPath, report.Images, report.ReportedCharts }),
                runs = runs.Select(run => new { run.Id, run.StrategyId, run.Kind, run.CodeHash, run.InputEvidence, run.Settings,
                    run.Metrics, run.Trades, run.Equity, run.WalkForward }),
                authority = "Actual compiled fixture strategies, BacktestEngine/Optimizer execution, reopened StrategyStore runs, explicit run IDs through LabServer.save_report, real ReportStore image bundles and production ReportExport HTML. Parent owns browser observation. Fake local stores only. No provider, model, executable-portfolio or independent-trade-confidence claim.",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return observations.Count(value => !value.Passed);
    }

    private static string Section(string markdown, string start)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int first = Array.FindIndex(lines, line => line.StartsWith(start, StringComparison.Ordinal));
        if (first < 0) return "";
        int end = Array.FindIndex(lines, first + 1, line => line.StartsWith('#'));
        return string.Join("\n", lines[first..(end < 0 ? lines.Length : end)]);
    }

    private static string ExecutiveSummary(string markdown)
    {
        var sections = Regex.Split(markdown, @"(?m)^## \d+\. ");
        return sections.FirstOrDefault(section => section.StartsWith("Executive summary\n", StringComparison.Ordinal)) ?? "";
    }

    private static List<string[]> Rows(string markdown) => markdown.Replace("\r\n", "\n").Split('\n')
        .SkipWhile(line => !line.StartsWith('|')).TakeWhile(line => line.StartsWith('|')).Skip(2)
        .Select(line => line.Trim('|').Split('|').Select(value => value.Trim()).ToArray()).ToList();

    private static string? Cell(string markdown, string label)
    {
        foreach (string line in markdown.Replace("\r\n", "\n").Split('\n').Where(line => line.StartsWith('|')))
        {
            var cells = line.Trim('|').Split('|').Select(value => value.Trim()).ToArray();
            for (int index = 0; index + 1 < cells.Length; index += 2)
                if (cells[index] == label) return cells[index + 1];
        }
        return null;
    }
}
