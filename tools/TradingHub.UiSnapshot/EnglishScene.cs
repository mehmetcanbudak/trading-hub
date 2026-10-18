using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Lab;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting.Drawings;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Scripting;
using TradingHub.Strategies;
using TradingHub.Tests;
using TradingHub.UiSnapshot;

internal static class EnglishScene
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static Task Call(object target, string name) => (Task)target.GetType().GetMethod(name, Private)!.Invoke(target, null)!;

    internal static IEnumerable<string> Labels(Control root) => root.GetVisualDescendants().Prepend(root)
        .OfType<Control>().Where(c => c.IsEffectivelyVisible).SelectMany(c => new[]
        {
            c is TextBlock text ? text.Text : null,
            c is ContentControl content ? content.Content as string : null,
            c is TextBox box ? box.Watermark : null,
            ToolTip.GetTip(c) as string,
        }).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).Distinct();

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0, checks = 0;
        void Check(bool ok, string message)
        {
            checks++;
            Console.WriteLine((ok ? "OK " : "FAIL ") + message);
            if (!ok) failures++;
        }
        void English(Control control, string name)
        {
            var bad = Labels(control).Where(text => !Path.IsPathRooted(text) && !Uri.IsWellFormedUriString(text, UriKind.Absolute)
                && EnglishLocalizationAudit.LooksSpanish(text)).ToList();
            Check(bad.Count == 0, name + " has English visible labels/tooltips" + (bad.Count == 0 ? "" : ": " + string.Join(" | ", bad)));
        }
        void Shot(Window target, string name)
        {
            pump(0.3);
            English(target, name);
            var frame = target.CaptureRenderedFrame();
            Check(frame != null, name + " renders");
            frame?.Save(Path.Combine(outDir, name + ".png"));
        }
        void Inspect(Window? target, string name)
        {
            Check(target != null, name + " opens");
            if (target == null) return;
            if (!target.IsVisible) target.Show(window);
            Shot(target, name);
            foreach (var tabs in target.GetVisualDescendants().OfType<TabControl>().ToArray())
                for (int i = 0; i < tabs.ItemCount; i++)
                {
                    tabs.SelectedIndex = i;
                    pump(0.1);
                    English(target, name + " tab " + i);
                }
            target.Close();
        }
        void Cancel(Func<Task> start, Window owner, string title, params string[] required)
        {
            var task = start();
            pump(0.3);
            var dialog = owner.OwnedWindows.FirstOrDefault(w => w.Title == title);
            Check(dialog != null, title + " opens the actual safety dialog");
            if (dialog == null) return;
            string body = string.Join("\n", Labels(dialog));
            Check(required.All(body.Contains), title + " explains the relevant risk in English");
            Shot(dialog, "english_dialog_" + title.Replace(' ', '_'));
            var cancel = dialog.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "Cancel");
            Check(cancel != null, title + " has Cancel");
            cancel?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 100 && !task.IsCompleted; i++) pump(0.02);
            Check(task.IsCompletedSuccessfully, title + " cancels cleanly");
            if (dialog.IsVisible) dialog.Close(false);
        }

        Check(!Path.GetFullPath(AppPaths.Root).StartsWith(Path.GetFullPath(TradingHub.Common.Product.AppData), StringComparison.Ordinal), "uses isolated application data");
        Check(Environment.GetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD") == "off", "power changes are disabled");
        var svc = App.Services;
        Check(svc.Connections.Profiles.All(p => !p.AllowRealOrders), "all fixture profiles keep real orders disabled");
        var chart = window.Trading.ActiveTab!;
        var model = chart.Model!;
        var compiler = new ScriptCompiler();
        var examples = Path.Combine(AppPaths.Root, "EnglishExamples");
        Directory.CreateDirectory(examples);
        var parameterNames = new Dictionary<string, string[]>
        {
            ["MovingAverageCrossover"] = new[] { "FastPeriod", "SlowPeriod" },
            ["RangeBreakout"] = new[] { "ChannelPeriod", "StopAtrMultiple" },
            ["DeltaAbsorption"] = new[] { "VolumeLookback", "MinimumVolumeMultiple", "MinimumDeltaPercent", "MaxBarsInTrade" },
            ["VwapReversion"] = new[] { "DistanceAtrMultiple", "StartHour", "EndHour" },
            ["MyStrategy"] = new[] { "Period" },
        };
        Check(StrategyTemplates.All.Select(t => t.FileName).ToHashSet().SetEquals(parameterNames.Keys), "all template filenames use English identifiers");
        int index = 0;
        foreach (var template in StrategyTemplates.All)
        {
            string file = Path.Combine(examples, template.FileName + ".cs");
            string code = template.Code(template.FileName).Replace("__NOMBRE__", template.Name);
            File.WriteAllText(file, code);
            var result = compiler.Compile(new[] { file }, "EnglishTemplate" + index++);
            Check(result.Success, template.Name + " template compiles: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == "error")));
            var type = result.Assembly?.GetTypes().FirstOrDefault(t => typeof(Strategy).IsAssignableFrom(t));
            Check(type?.Name == template.FileName && parameterNames.TryGetValue(template.FileName, out var expected)
                && ((Strategy)Activator.CreateInstance(type)!).GetParameters().Keys.ToHashSet().IsSupersetOf(expected), template.Name + " class and parameter names are English");
            Check(!Regex.IsMatch(code, @"\b(?:Rapida|Lenta|Promedio|Canal|MultiploAtr|VolumenMinimo|DeltaMinimo|VelasMaximas|Distancia|HoraDesde|HoraHasta|Periodo|_rapida|_lenta|_maximo|_minimo|_volumen|_barEntrada|_media|volumenAlto|techo|piso|distancia|horario|banda)\b"), template.Name + " source has no legacy Spanish identifiers");
        }
        string indicatorFile = Path.Combine(AppPaths.UserIndicators, "DeltaPercentageExample.cs");
        Check(File.Exists(indicatorFile), "initial indicator filename is English");
        var indicatorResult = compiler.Compile(new[] { indicatorFile }, "EnglishIndicatorExample");
        var indicatorType = indicatorResult.Assembly?.GetTypes().FirstOrDefault(t => typeof(Indicator).IsAssignableFrom(t));
        Check(indicatorResult.Success && indicatorType?.Name == "DeltaPercentageExample" && indicatorType.GetProperty("HighlightThreshold") != null, "initial indicator class and parameter identifiers are English");
        Check(!Regex.IsMatch(UserScripts.ExampleSource, @"\b(?:EjemploDeltaPorcentual|Umbral|_serie|vela)\b"), "initial indicator source has no legacy Spanish identifiers");
        string newIndicatorCode = (string)typeof(CodeEditorWindow).GetMethod("IndicatorTemplate", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { "EnglishIndicator", "English indicator" })!;
        string newIndicatorFile = Path.Combine(examples, "EnglishIndicator.cs");
        File.WriteAllText(newIndicatorFile, newIndicatorCode);
        Check(compiler.Compile(new[] { newIndicatorFile }, "EnglishNewIndicator").Success, "New indicator editor template compiles");
        Check(!Regex.IsMatch(newIndicatorCode, @"\b(?:Periodo|_serie|suma)\b"), "New indicator editor template uses English parameter/private/local identifiers");
        string? adminScript = null;
        string guardFolder = Path.Combine(examples, "PowerGuard");
        using (var guard = new SleepGuard { FlagDirectory = guardFolder, RunAdminScript = script => { adminScript = script; return null; } })
        {
            guard.Update(true, true, true, "Fixture strategies running");
            Check(guard.Awake && guard.LidHelperRunning, "fake power helper enters the requested state without privileges");
            string prompt = Regex.Match(adminScript ?? "", "with prompt \\\"(.*?)\\\" with administrator privileges").Groups[1].Value;
            Check(prompt.Contains("needs permission", StringComparison.Ordinal) && prompt.Contains("restored", StringComparison.Ordinal) && !EnglishLocalizationAudit.LooksSpanish(prompt), "closed-lid administrator explanation is English");
            guard.Update(false, false, false, null);
            Check(!guard.Awake && !guard.LidHelperRunning, "fake power helper releases its assertion and flag");
        }
        Check(!Directory.EnumerateFiles(guardFolder).Any(), "fake power test leaves no active flags");

        if (!OperatingSystem.IsWindows())
        {
            string fakeCli = Path.Combine(examples, "fake-claude");
            File.WriteAllText(fakeCli, "#!/bin/sh\nif [ \"$1\" = \"--help\" ]; then printf '%s\\n' '--effort VALUE' '--add-dir DIR'; else /bin/cat claude-fixture.json; fi\n");
            File.SetUnixFileMode(fakeCli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string? previousCli = Environment.GetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI");
            Environment.SetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI", fakeCli);
            var bridge = new ClaudeCodeBridge();
            ClaudeResult? reply = null;
            bridge.Finished += result => reply = result;
            void FakeReply(string json)
            {
                reply = null;
                bridge.NewConversation();
                File.WriteAllText(Path.Combine(examples, "claude-fixture.json"), json);
                var task = bridge.SendAsync("Fixture only; no network or paid model.", examples, ClaudeCodeBridge.StrategySystemPrompt);
                for (int i = 0; i < 500 && (!task.IsCompleted || reply == null); i++) pump(0.01);
                Check(task.IsCompletedSuccessfully && reply != null, "fake Claude request completes through the real bridge");
                if (!task.IsCompleted) bridge.Cancel();
            }
            try
            {
                FakeReply("{\"type\":\"result\",\"session_id\":\"fixture-session\",\"result\":\"English fixture response.\",\"is_error\":false,\"total_cost_usd\":0}");
                Check(reply is { IsError: false, Text: "English fixture response.", CostUsd: 0 } && bridge.SessionId == "fixture-session", "Claude response/session parsing works without a paid call");
                FakeReply("{\"type\":\"result\",\"result\":\"Not logged in. Run /login.\",\"is_error\":true}");
                Check(reply is { IsError: true, NeedsLogin: true } && !EnglishLocalizationAudit.LooksSpanish(reply.Text), "Claude authentication failure is English and recognized");
                FakeReply("not a JSON response\n");
                Check(reply is { IsError: true } && reply.Text.Contains("exited without a response", StringComparison.Ordinal), "Claude empty-result fallback is English");
                Check(ClaudeCodeBridge.StrategySystemPrompt.Contains("English filenames, class/parameter/local identifiers", StringComparison.Ordinal)
                    && ClaudeCodeBridge.IndicatorSystemPrompt.Contains("English filenames, class/parameter/local identifiers", StringComparison.Ordinal), "new Claude strategy and indicator code is instructed to use English identifiers");
            }
            finally
            {
                bridge.Cancel();
                Environment.SetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI", previousCli);
            }
        }
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        foreach (string guide in new[] { "estrategias.md", "indicadores.md" })
        {
            var source = File.ReadAllText(Path.Combine(root, "docs/en", guide));
            foreach (Match block in Regex.Matches(source, @"```csharp\r?\n(.*?)\r?\n```", RegexOptions.Singleline))
            {
                string file = Path.Combine(examples, "Guide" + index + ".cs");
                File.WriteAllText(file, block.Groups[1].Value);
                var result = compiler.Compile(new[] { file }, "EnglishGuide" + index++);
                Check(result.Success, guide + " complete example compiles: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == "error")));
            }
        }
        string broken = Path.Combine(examples, "Broken.cs");
        File.WriteAllText(broken, "public class Broken { missing_type Value; }");
        var diagnostic = compiler.Compile(new[] { broken }, "EnglishDiagnostic");
        Check(!diagnostic.Success && diagnostic.Diagnostics.Any(d => d.Message.Contains("could not be found", StringComparison.Ordinal)), "compiler errors are English");

        Inspect(new ChartSettingsWindow(window.Trading, chart), "english_chart_settings");
        Inspect(new FootprintEditorWindow(model, window.Trading), "english_footprint_editor");
        Inspect(new IndicatorsWindow(svc, model), "english_indicators");
        Inspect(new CodeEditorWindow(svc, CodeKind.Indicator), "english_indicator_editor");
        Inspect(new DrawingSettingsWindow(svc, new ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.HorizontalLine, Points = { new(DateTime.UtcNow, (double)model.Data.LastPrice) } },
            model.Instrument, model.Period, () => { }, () => { }, () => { }), "english_drawing_settings");
        Inspect(window.Trading.ShowDomSettings(), "english_dom_settings");
        Inspect(new LivePortfolioWindow(svc, null), "english_live_portfolio");
        var entry = new LiveEntry
        {
            Key = "english-fixture", Source = "panel", Ledger = LiveStore.LoadLedger("english-fixture", 1),
            Info = new LiveRunnerInfo { Key = "english-fixture", Strategy = "Fixture strategy", Symbol = model.Instrument.Symbol, FeedId = "sim", ProfileId = "sim", AccountId = "sim", Account = "Cuenta simulada", Period = "M5" },
        };
        Inspect(new LiveStrategyWindow(svc, _ => { }, entry), "english_live_strategy");

        svc.Catalog.Scan(typeof(FixtureTechnicalIndicator).Assembly, "Técnicos");
        var library = new IndicatorsWindow(svc, model);
        library.Show(window);
        Field<TextBox>(library, "_search").Text = "Fixture technical";
        pump(0.3);
        Check(Labels(library).Contains("Fixture technical") && !Labels(library).Contains("extra"), "technical catalog source is recognized without an extra-source badge");
        library.Close();

        window.Navigate("live");
        Cancel(() => Call(window.Page("live")!, "KillAllAsync"), window, "Stop all and close", "ALL live strategies", "orders will be canceled", "positions closed at market");

        var fileToLaunch = svc.Strategies.Files.First();
        var launch = new LiveLaunchWindow(svc, _ => { }, fileToLaunch.Id);
        launch.Show(window);
        for (int i = 0; i < 200 && fileToLaunch.Type == null; i++) pump(0.05);
        pump(0.1);
        Check(Field<ComboBox>(launch, "_strategy").SelectedItem is ComboBoxItem selected && selected.Content as string == fileToLaunch.DisplayName && fileToLaunch.Type != null,
            "live-launch strategy label refreshes to compiled English metadata, not a stale filename");
        Set(launch, "_profile", "sim");
        Set(launch, "_instrument", model.Instrument);
        Set(launch, "_data", model.Data);
        Field<MarketConditionPicker>(launch, "_conditions").Load(RegimeFilter.All, 0, 100);
        var accounts = Field<ComboBox>(launch, "_account");
        var fakeReal = new ComboBoxItem { Content = "FAKE REAL ACCOUNT", Tag = new AccountOption("__fake_real", "FAKE REAL ACCOUNT", true) };
        accounts.Items.Add(fakeReal);
        accounts.SelectedItem = fakeReal;
        int runners = svc.Runners.Count;
        Shot(launch, "english_live_launch");
        Cancel(() => Call(launch, "StartAsync"), launch, "Execute on REAL account", "real orders", "no individual confirmation", "FAKE REAL ACCOUNT");
        Check(svc.Runners.Count == runners, "canceling fake-real launch starts no runner or broker order");
        launch.Close();

        using var db = FakeBrokers.Databento();
        string? previousDb = Environment.GetEnvironmentVariable("TRADINGHUB_DATABENTO_URL");
        Environment.SetEnvironmentVariable("TRADINGHUB_DATABENTO_URL", db.Http + "/v0");
        var profile = new ConnectionProfile { Id = "english-databento", Kind = ConnectionKind.Databento, Name = "Fixture Databento" };
        svc.Connections.Profiles.Add(profile);
        svc.Connections.SetSecret(profile, "db-FAKE");
        window.Navigate("data");
        pump(0.3);
        var data = window.Page("data")!;
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        while (day.DayOfWeek == DayOfWeek.Saturday) day = day.AddDays(-1);
        Field<CalendarDatePicker>(data, "_dbFrom").SelectedDate = day.ToDateTime(TimeOnly.MinValue);
        Field<CalendarDatePicker>(data, "_dbTo").SelectedDate = day.ToDateTime(TimeOnly.MinValue);
        Field<CheckBox>(data, "_dbAlsoRithmic").IsChecked = true;
        pump(0.1);
        string symbol = (string)data.GetType().GetProperty("DbRoot", Private)!.GetValue(data)!;
        Set(data, "_dbQuote", 1.26m);
        Set(data, "_dbQuoteKey", $"{symbol}|{day}|{day}|1");
        Cancel(() => Call(data, "DatabentoDownloadAsync"), window, "Download Databento history", "Databento charges", "US$ 1.26", "payment method");
        Check(db.Requests.All(r => r.Route != "/v0/timeseries.get_range"), "canceling paid download submits no data request or charge");
        Environment.SetEnvironmentVariable("TRADINGHUB_DATABENTO_URL", previousDb);
        svc.Connections.Profiles.Remove(profile);

        var instrument = new Instrument { FeedId = CsvImport.FeedId, Symbol = "TEST", TickSize = 1, VolumeStep = 1 };
        HistoricalData.WriteDay(AppPaths.Ticks, instrument, day, new[] { new Trade(1, day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), 100, 1, TradeSide.Buy) }, replace: true);
        data.GetType().GetMethod("Refresh", BindingFlags.Public | Private)!.Invoke(data, null);
        var symbols = Field<ListBox>(data, "_symbols");
        symbols.SelectedItem = symbols.Items.OfType<ListBoxItem>().First(i => i.Tag is SymbolData d && d.FeedId == instrument.FeedId);
        pump(0.1);
        var days = Field<ListBox>(data, "_days");
        days.SelectedItem = days.Items[0];
        Check(Field<TextBlock>(data, "_daysTitle").Bounds.Width >= days.Bounds.Width - 2, "data title keeps the full column width instead of being clipped by action buttons");
        string history = TickFile.DayPath(AppPaths.Ticks, instrument.FeedId, instrument.Symbol, day);
        Cancel(() => Call(data, "DeleteSelectedAsync"), window, "Delete data", "TEST", "cannot be undone");
        Check(File.Exists(history), "canceling deletion preserves the history file");

        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-10);
        var trades = Enumerable.Range(0, 6).Select(i =>
        {
            bool buy = i % 2 == 0;
            decimal pnl = i % 3 == 0 ? -20 : 50;
            return new ClosedTrade(i + 1, buy, start.AddDays(i), 100, start.AddDays(i).AddHours(1), buy ? 100 + pnl : 100 - pnl, 1, pnl, 0, -5, 50, "Entry", "Exit", 2);
        }).ToList();
        decimal balance = 10_000, peak = balance;
        var equity = new List<EquityPoint> { new(start, balance, 0) };
        foreach (var trade in trades) { balance += trade.NetPnl; peak = Math.Max(peak, balance); equity.Add(new(trade.ExitTime, balance, peak - balance)); }
        var metrics = PerformanceMetrics.Compute(trades, equity, 10_000, false, 12, 100);
        var run = new StrategyRun
        {
            StrategyId = "EnglishFixture", StrategyName = "English fixture", Kind = RunKind.Backtest, Trades = trades, Equity = equity, Metrics = metrics,
            Settings = new BacktestSettings { FeedId = "sim", Symbol = "TEST", FromUtc = start, ToUtc = start.AddDays(8), CommissionPercent = 0 },
            Parameters = new() { ["Rapida"] = 10 }, MonteCarlo = Optimizer.MonteCarlo(trades, 10_000, 100),
        };
        var opt = new OptimizationResult { Combinations = 2, Ranges = new() { new ParameterRange { Name = "Rapida", Min = 5, Max = 10, Step = 5 } }, Rows = new() { new OptimizationRow { Parameters = new() { ["Rapida"] = 10 }, Metrics = metrics }, new OptimizationRow { Parameters = new() { ["Rapida"] = 5 }, Metrics = metrics } } };
        var wf = new WalkForwardResult { Search = "bayesiana", InSampleDays = 4, OutSampleDays = 2, OutSampleTrades = trades, OutSampleEquity = equity, OutSampleMetrics = metrics, WalkForwardEfficiency = 0.75, ProfitableWindowsPercent = 100,
            Windows = new() { new WalkForwardWindow { Number = 1, InSampleFrom = start, InSampleTo = start.AddDays(4), OutSampleFrom = start.AddDays(4), OutSampleTo = start.AddDays(6), InSample = metrics, OutSample = metrics, BestParameters = run.Parameters } } };
        var reportInput = new ReportInput { Title = "English validation report", Verdict = "ganadora", StrategyName = run.StrategyName, Backtest = run,
            Optimization = new StrategyRun { Settings = run.Settings, Optimization = opt }, WalkForward = new StrategyRun { Settings = run.Settings, WalkForward = wf },
            Portfolio = PortfolioAnalyzer.Analyze(new[] { PortfolioComponent.FromRun(run), PortfolioComponent.FromRun(run) }, 10_000) };
        var report = ReportBuilder.Build(reportInput);
        string prose = Regex.Replace(report.Markdown, @"!\[[^\]]*\]\([^)]*\)", "");
        Check(!EnglishLocalizationAudit.LooksSpanish(prose), "generated backtest/optimization/walk-forward/portfolio report prose is English");
        Check(!prose.Contains("Rapida", StringComparison.Ordinal), "report parameter display names are English while stored keys stay unchanged");
        var saved = ReportStore.Save(reportInput.Title, report.Markdown, null, reportInput.Verdict, report.Images);
        var headerless = ReportStore.Save("English metadata", "Fixture body", null, "ganadora");
        Check(File.ReadAllText(headerless.Path).Contains("Verdict: PROFITABLE", StringComparison.Ordinal), "fallback report metadata displays an English verdict without changing its stored code");
        string html = File.ReadAllText(ReportExport.Export(saved));
        File.WriteAllText(Path.Combine(outDir, "english-report.html"), html);
        Check(html.Contains("lang=\"en\"", StringComparison.Ordinal), "exported report declares English");
        Check(html.Contains("data:image/png;base64", StringComparison.Ordinal), "exported report embeds its generated charts");
        foreach (string page in new[] { "trading", "settings", "connections", "data", "strategies", "accounts", "live" })
        {
            window.Navigate(page);
            Shot(window, "english_page_" + page);
        }
        Console.WriteLine($"English UI scenario: {checks} checks, {failures} failures.");
        return failures;
    }
}

[DisplayName("Fixture technical")]
[Category("Other")]
public sealed class FixtureTechnicalIndicator : Indicator
{
    protected override void OnCalculate(int bar, decimal value) => this[bar] = value;
}
