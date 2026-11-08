using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Lab;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

internal static class ResearchAccountingScene
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private sealed record Observation(string Description, bool Passed, string Actual);

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        var observations = new List<Observation>();
        var runs = new List<StrategyRun>();
        var pending = new List<Task>();
        string nonce = Guid.NewGuid().ToString("N");
        string className = "ResearchAccounting" + nonce;
        string feed = "research-accounting-" + nonce;
        string sourcePath = Path.Combine(AppPaths.Strategies, className + ".cs");
        string feedPath = Path.Combine(AppPaths.Ticks, feed);
        string historyPath = Path.Combine(AppPaths.Backtests, className);
        var first = DateTime.UtcNow.Date.AddDays(-5);
        StrategiesView? view = null;
        string? previousSource = null;
        string? fixtureError = null;

        void Check(bool passed, string description, string actual)
        {
            observations.Add(new(description, passed, actual));
            Console.WriteLine($"{(passed ? "OK" : "FAIL")} research accounting: {description}. {actual}");
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Research accounting fixture precondition failed. " + message);
        }
        void Await(Task task)
        {
            pending.Add(task);
            var watchdog = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (watchdog.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("The real research consumer did not return.");
                pump(0.01);
            }
            task.GetAwaiter().GetResult();
        }
        StrategyRun Execute(string label, int failureDay)
        {
            var state = Field<object>(view!, "_state");
            var fixedInput = ((List<ParameterRange>)state.GetType().GetProperty("Ranges")!.GetValue(state)!).Single(range => range.Name == "FailureDay");
            fixedInput.Enabled = false;
            fixedInput.Min = fixedInput.Max = failureDay;
            typeof(StrategiesView).GetMethod("SaveState", Private)!.Invoke(view, null);
            var previous = StrategyStore.List(className).Select(summary => summary.Id).ToHashSet(StringComparer.Ordinal);
            Field<Ui.Segmented<string>>(view!, "_tabs").Choose("wfo");
            Await((Task)typeof(StrategiesView).GetMethod("RunWalkForwardAsync", Private)!.Invoke(view, null)!);
            var added = StrategyStore.List(className).Where(summary => !previous.Contains(summary.Id)).ToArray();
            Require(added.Length == 1, label + " must save one real walk-forward result. " + Field<TextBlock>(view!, "_wfoPreview").Text);
            var run = StrategyStore.Load(className, added[0].Id) ?? throw new IOException("The real saved research result could not be reopened.");
            Require(run.WalkForward != null && run.InputEvidence != null, label + " must retain its measured windows and input evidence.");
            Require(run.WalkForward!.Windows.Where(value => value.OutSampleAvailable).All(value =>
                JsonSerializer.SerializeToElement(value.BestParameters["FailureDay"]).GetInt32() == failureDay),
                label + " must use the selected fixed numeric range in every measured OOS window.");
            runs.Add(run);
            pump(0.1);
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "research_accounting_" + label + ".png"));
            var report = ReportBuilder.Build(new ReportInput { Title = "Causal research " + label, WalkForward = run });
            File.WriteAllText(Path.Combine(outDir, "research_accounting_" + label + ".md"), report.Markdown);
            Check(report.Markdown.Contains(run.CodeHash, StringComparison.Ordinal) &&
                report.Markdown.Contains(run.InputEvidence!.ContentIdentity!, StringComparison.Ordinal),
                label + " report retains the exact compiled source and captured input identities", run.CodeHash);
            return run;
        }
        void CheckMcpReadback()
        {
            string mcpRoot = Path.Combine(outDir, "mcp-readback");
            Require(!Directory.Exists(mcpRoot), "The real MCP consumer must have fresh independent stores.");
            Directory.CreateDirectory(mcpRoot);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add(typeof(App).Assembly.Location);
            start.ArgumentList.Add("--mcp");
            foreach (var (variable, directory) in new[]
            {
                ("TRADINGHUB_DATA_DIR", "data"), ("TRADINGHUB_STRATEGIES_DIR", "strategies"),
                ("TRADINGHUB_INDICATORS_DIR", "indicators"), ("TRADINGHUB_BACKTESTS_DIR", "backtests"),
                ("TRADINGHUB_LIVE_DIR", "live"), ("TMPDIR", "tmp"),
            })
            {
                string path = Path.Combine(mcpRoot, directory);
                Directory.CreateDirectory(path);
                start.Environment[variable] = path;
            }
            start.Environment["TRADINGHUB_KEYCHAIN_FILE"] = Path.Combine(mcpRoot, "fake-keychain.json");
            start.Environment["TRADINGHUB_SLEEP_GUARD"] = "off";
            start.Environment["TRADINGHUB_CLAUDE_CLI"] = start.Environment["TRADINGHUB_CODEX_CLI"] = "/usr/bin/false";
            File.Copy(sourcePath, Path.Combine(mcpRoot, "strategies", Path.GetFileName(sourcePath)));
            foreach (string original in Directory.EnumerateFiles(historyPath, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(mcpRoot, "backtests", className, Path.GetRelativePath(historyPath, original));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(original, copy);
            }
            using var process = Process.Start(start) ?? throw new IOException("The actual laboratory MCP process did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                process.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } }));
                process.StandardInput.WriteLine(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = 2, method = "tools/call",
                    @params = new { name = "list_runs", arguments = new { file = className, limit = 20 } },
                }));
                process.StandardInput.Close();
                Await(process.WaitForExitAsync());
                Await(Task.WhenAll(stdout, stderr));
                File.WriteAllText(Path.Combine(outDir, "research-accounting-mcp-stdout.jsonl"), stdout.Result);
                File.WriteAllText(Path.Combine(outDir, "research-accounting-mcp-stderr.txt"), stderr.Result);
                Require(process.ExitCode == 0, "The real MCP process must finish its requests and streams. " + stderr.Result);
                JsonElement response = default;
                foreach (string line in stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var message = JsonDocument.Parse(line);
                    if (message.RootElement.GetProperty("id").GetInt32() == 2) response = message.RootElement.Clone();
                }
                Require(response.ValueKind == JsonValueKind.Object && !response.TryGetProperty("error", out _) &&
                    !response.GetProperty("result").GetProperty("isError").GetBoolean(), "The actual tools/call list_runs request must succeed. " + stdout.Result);
                using var content = JsonDocument.Parse(response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
                var listed = content.RootElement.GetProperty("runs").EnumerateArray().ToDictionary(row => row.GetProperty("run_id").GetString()!);
                foreach (var run in runs)
                {
                    var row = listed[run.Id];
                    var actual = row.GetProperty("input_evidence");
                    var expected = run.InputEvidence!;
                    Check(row.GetProperty("code_hash").GetString() == run.CodeHash && actual.GetProperty("known").GetBoolean() &&
                        actual.GetProperty("data_quality").GetString() == expected.DataQuality.ToString() &&
                        actual.GetProperty("resolution_seconds").GetInt32() == expected.ResolutionSeconds &&
                        actual.GetProperty("source_identity").GetString() == expected.SourceIdentity &&
                        actual.GetProperty("correction_revision").GetInt64() == expected.CorrectionRevision &&
                        actual.GetProperty("has_known_availability").GetBoolean() == expected.HasKnownAvailability &&
                        actual.GetProperty("data_warning").GetString() == expected.DataWarning &&
                        actual.GetProperty("content_identity").GetString() == expected.ContentIdentity,
                        "Actual MCP standard IO preserves every saved input fact and compiled source identity", run.Id);
                }
                Check(listed[runs[0].Id].GetProperty("net_profit").GetDecimal() == 9.48m &&
                    listed[runs[0].Id].GetProperty("completed_windows").GetInt32() == 2 &&
                    listed[runs[1].Id].GetProperty("net_profit").GetDecimal() == 4.74m &&
                    !listed[runs[1].Id].GetProperty("is_complete").GetBoolean() &&
                    listed[runs[1].Id].GetProperty("completed_windows").GetInt32() == 1 &&
                    listed[runs[2].Id].GetProperty("completed_windows").GetInt32() == 0 &&
                    new[] { "net_profit", "profit_factor", "max_drawdown", "trades", "win_rate", "sharpe", "efficiency" }
                        .All(key => listed[runs[2].Id].GetProperty(key).ValueKind == JsonValueKind.Null),
                    "Actual MCP output distinguishes complete, measured-prefix and unavailable money", stdout.Result);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                Await(process.WaitForExitAsync());
                Await(Task.WhenAll(stdout, stderr));
            }
        }

        try
        {
            foreach (string variable in new[] { "TRADINGHUB_DATA_DIR", "TRADINGHUB_STRATEGIES_DIR", "TRADINGHUB_INDICATORS_DIR", "TRADINGHUB_BACKTESTS_DIR", "TRADINGHUB_LIVE_DIR", "TRADINGHUB_KEYCHAIN_FILE" })
                Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)), "An isolated store is required for " + variable);
            Require(!File.Exists(sourcePath) && !Directory.Exists(feedPath) && !Directory.Exists(historyPath), "Every fixture identity must start absent.");
            var instrument = new Instrument { FeedId = feed, Symbol = "CAUSAL", Exchange = "TEST", TickSize = 0.01m, VolumeStep = 1, PointValue = 1 };
            for (int day = 0; day < 4; day++)
            {
                var date = first.AddDays(day);
                decimal entry = 100m + day * 10m;
                var provenance = new TradeProvenance(DataQuality.Raw, SourceIdentity: feed + ":CAUSAL");
                HistoricalData.WriteDay(AppPaths.Ticks, instrument, DateOnly.FromDateTime(date), Enumerable.Range(0, 4).Select(index =>
                    new Trade(day * 4L + index + 1, date.AddHours(12).AddMinutes(index), index < 2 ? entry : entry + 5m, 1m, TradeSide.Buy, provenance)).ToArray());
            }
            File.WriteAllText(sourcePath, $$"""
                using System;
                using TradingHub.Market;
                using TradingHub.Strategies;
                [Strategy("Causal accounting consumer")]
                public sealed class {{className}} : Strategy
                {
                    [Parameter("Direction"), Optimize(-1, 1, 2)] public int Direction { get; set; } = 1;
                    [Parameter("Failure day")] public int FailureDay { get; set; }
                    protected override void OnTick(Trade trade)
                    {
                        var first = new DateTime({{first.Year}}, {{first.Month}}, {{first.Day}}, 0, 0, 0, DateTimeKind.Utc);
                        if (FailureDay > 0 && (trade.Time.Date - first).Days + 1 == FailureDay)
                            throw new InvalidOperationException("Deliberate research fixture failure.");
                        if (trade.Time.Minute == 0 && IsFlat)
                        {
                            if (Direction > 0) Buy(1);
                            else Sell(1);
                        }
                        else if (trade.Time.Minute == 2 && !IsFlat) ClosePosition();
                    }
                }
                """);
            window.Navigate("strategies");
            view = (StrategiesView)window.Page("strategies")!;
            previousSource = Field<StrategyFile?>(view, "_current")?.Path;
            view.OnShown();
            Await(view.OpenFileAsync(sourcePath));
            var file = App.Services.Strategies.Files.Single(candidate => candidate.Path == sourcePath);
            Require(file.LastCompile?.Success == true && file.Id == className, "The actual editor must compile the isolated strategy.");
            Field<ComboBox>(view, "_dataSource").SelectedItem = Field<ComboBox>(view, "_dataSource").Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag is SymbolData data && data.FeedId == feed && data.Symbol == instrument.Symbol);
            Field<CalendarDatePicker>(view, "_from").SelectedDate = first;
            Field<CalendarDatePicker>(view, "_to").SelectedDate = first.AddDays(3);
            Field<TextBox>(view, "_period").Text = "M1";
            Field<NumericUpDown>(view, "_capital").Value = 10_000m;
            Field<NumericUpDown>(view, "_commPct").Value = 0;
            Field<NumericUpDown>(view, "_commUnit").Value = 0.13m;
            Field<NumericUpDown>(view, "_slippage").Value = 0;
            Field<ComboBox>(view, "_timeZone").SelectedIndex = 0;
            Field<NumericUpDown>(view, "_sessionStart").Value = 0;
            Field<NumericUpDown>(view, "_minTrades").Value = 1;
            Field<NumericUpDown>(view, "_isDays").Value = 2;
            Field<NumericUpDown>(view, "_osDays").Value = 1;
            var complete = Execute("complete", 0);
            var full = complete.WalkForward!;
            Check(full.IsComplete && full.Windows.Count == 2 && full.Windows.All(value => value.InSampleAvailable && value.OutSampleAvailable) &&
                full.Windows.All(value => JsonSerializer.SerializeToElement(value.BestParameters["Direction"]).GetInt32() == 1),
                "Actual walk-forward selects the profitable prefix configuration for both measured windows", $"Complete {full.IsComplete}, windows {full.Windows.Count}");
            Check(full.OutSampleMetrics.NetProfit == 9.48m && full.OutSampleMetrics.TotalTrades == 2 &&
                full.OutSampleTrades.Sum(trade => trade.NetPnl) == 9.48m && full.OutSampleEquity[^1].Equity - 10_000m == 9.48m,
                "Exact fees, marked contributions and stitched equity conserve the actual UI research net", $"Net {full.OutSampleMetrics.NetProfit}, fills {full.OutSampleMetrics.TotalTrades}");
            Check(complete.InputEvidence is { HasKnownAvailability: true, DataQuality: DataQuality.Raw, ContentIdentity: not null } &&
                complete.CodeHash == StrategyStore.HashCode(File.ReadAllText(sourcePath)),
                "Reopened walk-forward retains actual input availability, fidelity and exact compiled source", complete.InputEvidence!.ContentIdentity!);
            var mc = Optimizer.MonteCarlo(full.OutSampleTrades, complete.Settings.InitialCapital);
            Check(!mc.Available && mc.DrawdownP95 == null && mc.RuinProbabilityPercent == null,
                "Marked contributions do not become independent-trade Monte Carlo evidence", mc.UnavailableReason ?? "Missing unavailability reason");

            var partial = Execute("partial", 4);
            var prefix = partial.WalkForward!;
            Check(!prefix.IsComplete && prefix.Windows.Count == 2 && prefix.Windows[0].OutSampleAvailable && !prefix.Windows[1].OutSampleAvailable &&
                prefix.Windows[1].OutSample == null && prefix.Windows[1].Efficiency == null && prefix.OutSampleMetrics.NetProfit == 4.74m &&
                prefix.OutSampleTrades.Sum(trade => trade.NetPnl) == 4.74m && prefix.OutSampleEquity[^1].Equity - 10_000m == 4.74m,
                "Actual UI retains the completed OOS prefix without inventing unavailable suffix returns", $"Net {prefix.OutSampleMetrics.NetProfit}, complete {prefix.IsComplete}");
            var unavailable = Execute("unavailable", 2);
            Check(unavailable.WalkForward!.Windows.All(value => !value.OutSampleAvailable && value.OutSample == null) &&
                unavailable.WalkForward.OutSampleTrades.Count == 0 && unavailable.WalkForward.OutSampleEquity.Count == 0,
                "No completed OOS window stays unavailable rather than a completed zero-money result", $"Complete {unavailable.WalkForward.IsComplete}, windows {unavailable.WalkForward.Windows.Count}");
            CheckMcpReadback();
            Field<Ui.Segmented<string>>(view, "_tabs").Choose("history");
            Field<ListBox>(view, "_historyList").SelectedItem = Field<ListBox>(view, "_historyList").Items.OfType<StrategyRunSummary>().Single(summary => summary.Id == partial.Id);
            pump(0.1);
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "research_accounting_saved_partial.png"));
        }
        catch (Exception error)
        {
            fixtureError = error.ToString();
            Check(false, "The actual research consumer reaches each owned scenario without an incidental failure", fixtureError);
        }
        finally
        {
            if (view != null) Field<CancellationTokenSource?>(view, "_cts")?.Cancel();
            while (pending.Any(task => !task.IsCompleted)) pump(0.01);
            if (view != null && previousSource != null && File.Exists(previousSource))
            {
                try { Await(view.OpenFileAsync(previousSource)); }
                catch (Exception error) { Check(false, "The previous isolated strategy is restored", error.Message); }
            }
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (Directory.Exists(feedPath)) Directory.Delete(feedPath, recursive: true);
            if (Directory.Exists(historyPath)) Directory.Delete(historyPath, recursive: true);
            App.Services.Strategies.Refresh();
            File.WriteAllText(Path.Combine(outDir, "research-accounting-evidence.json"), JsonSerializer.Serialize(new
            {
                language = Lang.Current, fixtureError, observations,
                runs = runs.Select(run => new { run.Id, run.CodeHash, run.InputEvidence, run.Parameters, run.Settings,
                    run.WalkForward!.IsComplete, run.WalkForward.AccountingPolicy, run.WalkForward.AttributionPolicy,
                    net = run.WalkForward.OutSampleMetrics.NetProfit, actualClosingFills = run.WalkForward.OutSampleMetrics.TotalTrades,
                    contributions = run.WalkForward.OutSampleTrades, windows = run.WalkForward.Windows }),
                authority = "Actual StrategiesView compilation and walk-forward execution, real history input, reopened StrategyStore results and actual report builder. Fake local data only. No executable-portfolio, independent-trade confidence, model or real-provider claim.",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return observations.Count(observation => !observation.Passed);
    }
}
