using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Scripting;
using TradingHub.Strategies;

internal static class GenerationContinuationScene
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private sealed record CheckResult(string Description, bool Passed, string Observed);
    private sealed class CompilationHold
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        var checks = new List<CheckResult>();
        var pending = new List<Task>();
        var holds = new ConcurrentQueue<CompilationHold>();
        var ownedHolds = new List<CompilationHold>();
        var services = App.Services;
        var originalCatalog = services.Strategies;
        bool originalSmartContracts = services.Settings.SmartContracts;
        var originalChart = window.Trading.ActiveTab;
        string nonce = Guid.NewGuid().ToString("N");
        string className = "GenerationContinuation" + nonce;
        string otherClass = "GenerationSelection" + nonce;
        string sourcePath = Path.Combine(AppPaths.Strategies, className + ".cs");
        string otherPath = Path.Combine(AppPaths.Strategies, otherClass + ".cs");
        string profileId = "generation-continuation-" + nonce;
        string chartFeed = "generation-chart-" + nonce;
        var chartInstrument = new Instrument { FeedId = chartFeed, Symbol = "GEN", TickSize = 1, VolumeStep = 1, PointValue = 1 };
        var mini = new Instrument { FeedId = HistorySources.Databento, Symbol = "NQ", TickSize = 0.25m, VolumeStep = 1, PointValue = 20 };
        var micro = new Instrument { FeedId = HistorySources.Databento, Symbol = "MNQ", TickSize = 0.25m, VolumeStep = 1, PointValue = 2 };
        var instruments = new[] { chartInstrument, mini, micro };
        var profile = new ConnectionProfile { Id = profileId, Kind = ConnectionKind.Simulator, Name = "Generation continuation fixture", HistoryHours = 1 };
        var feed = new KvpSceneFeed(chartFeed, instruments) { Now = DateTime.UtcNow };
        StrategiesView? view = null;
        Window? researchWindow = null;
        StrategyCatalog? catalog = null;
        ChartTab? chart = null;
        string? fixtureError = null;
        string? runnerKey = null;
        int statusPublications = 0;

        void Check(bool passed, string description, string observed)
        {
            checks.Add(new(description, passed, observed));
            Console.WriteLine($"{(passed ? "OK" : "FAIL")} generation continuation: {description}. {observed}");
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Generation fixture precondition failed. " + message);
        }
        void Until(Func<bool> condition)
        {
            var watchdog = Stopwatch.StartNew();
            while (!condition())
            {
                if (watchdog.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("An owned generation consumer did not reach its expected state.");
                pump(0.01);
            }
        }
        void Await(Task task)
        {
            pending.Add(task);
            Until(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }
        Task<bool> CompileView()
        {
            var task = (Task<bool>)typeof(StrategiesView).GetMethod("CompileAsync", Private)!.Invoke(view, new object?[] { false })!;
            pending.Add(task);
            return task;
        }
        CompilationHold HoldNextCompilation()
        {
            var hold = new CompilationHold();
            holds.Enqueue(hold);
            ownedHolds.Add(hold);
            return hold;
        }
        Task<StrategyCompilation> CompileCatalog(StrategyFile file)
        {
            var task = Task.Run(() => catalog!.Compile(file, force: true));
            pending.Add(task);
            return task;
        }
        bool FormShows(string marker) => Field<StackPanel>(view!, "_paramsPanel").GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Text == "Marker " + marker);
        string ViewName() => Field<TextBlock>(view!, "_strategyName").Text ?? "";
        CodeEditor Editor() => Field<CodeEditor>(view!, "_editor");
        void SetSource(string marker)
        {
            string text = Source(className, marker);
            File.WriteAllText(sourcePath, text);
            Editor().Text = text;
        }
        void ExpectChart(string marker, int input, string description)
        {
            var strategy = chart!.Strategies[0];
            string actual = strategy.Runner?.StrategyInstance.ToString() ?? "no runner";
            string history = strategy.HistoryCanvas?.Drawings().SingleOrDefault(drawing => drawing.Id == "generation")?.Text ?? "no historical initialization";
            Check(strategy.Runner is { IsRunning: true } && actual == marker + ":" + input && history == marker + ":" + input,
                description, $"Runner {actual}, history {history}");
        }
        void ExpectPanel(string marker, int input, string description)
        {
            var runner = runnerKey == null ? null : services.Live.Running(runnerKey);
            string actual = runner?.StrategyInstance.ToString() ?? "no runner";
            Check(runner is { IsRunning: true } && actual == marker + ":" + input, description, actual);
        }
        void RejectChangedEditor(StrategyFile file, bool dirty)
        {
            SetSource("B");
            Await(CompileView());
            Require(ViewName() == "Generation B" && FormShows("B"), "The existing view must display B before the held A request.");
            SetSource("A");
            var firstHold = HoldNextCompilation();
            var secondHold = HoldNextCompilation();
            var first = CompileView();
            Until(() => firstHold.Entered.Task.IsCompleted);
            var second = CompileCatalog(file);
            Until(() => secondHold.Entered.Task.IsCompleted);
            firstHold.Release.TrySetResult();
            Await(first);
            if (dirty) Field<AvaloniaEdit.TextEditor>(Editor(), "_editor").Text = Source(className, "B");
            else Editor().Text = Source(className, "B");
            Require(Editor().IsDirty == dirty, "The editor must reach the requested dirty state through its normal text control.");
            secondHold.Release.TrySetResult();
            Await(second);
            pump(0.05);
            Check(ViewName() == "Generation B" && FormShows("B") && !FormShows("A") && Editor().Text == Source(className, "B") && Editor().IsDirty == dirty,
                dirty ? "Eventual A publication cannot replace a dirty B editor" : "Eventual A publication cannot replace changed clean B editor text",
                $"View {ViewName()}, dirty {Editor().IsDirty}");
            Editor().Text = Source(className, "B");
        }

        try
        {
            foreach (string variable in new[] { "TRADINGHUB_DATA_DIR", "TRADINGHUB_STRATEGIES_DIR", "TRADINGHUB_INDICATORS_DIR", "TRADINGHUB_BACKTESTS_DIR", "TRADINGHUB_LIVE_DIR", "TRADINGHUB_KEYCHAIN_FILE" })
                Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)), "An isolated store is required for " + variable);
            Require(!File.Exists(sourcePath) && !File.Exists(otherPath), "Fixture source identities must start absent.");
            Require(instruments.All(instrument => services.Hub.Items.All(data => data.Instrument.Key != instrument.Key)), "The fake instrument identities must not reuse existing market-data consumers.");
            services.Settings.SmartContracts = false;
            File.WriteAllText(sourcePath, Source(className, "A"));
            File.WriteAllText(otherPath, Source(otherClass, "C"));
            var compiler = new ScriptCompiler();
            catalog = new StrategyCatalog((sources, name) =>
            {
                CompilationHold? held = null;
                if (sources.Single().Path == sourcePath && holds.TryDequeue(out var hold))
                {
                    held = hold;
                    hold.Entered.TrySetResult();
                    if (!hold.Release.Task.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("A held real compilation was not released.");
                }
                try { return compiler.CompileSources(sources, name); }
                finally { held?.Returned.TrySetResult(); }
            });
            catalog.Files.Clear();
            typeof(AppServices).GetField("_strategies", Private)!.SetValue(services, catalog);
            view = new StrategiesView(services, _ => Interlocked.Increment(ref statusPublications));
            researchWindow = new Window { Content = view, Width = 1200, Height = 850, Title = "Generation continuation research fixture" };
            researchWindow.Show(window);
            var firstHold = HoldNextCompilation();
            var publicationHold = HoldNextCompilation();
            var selecting = view.OpenFileAsync(sourcePath);
            pending.Add(selecting);
            Until(() => firstHold.Entered.Task.IsCompleted);
            var file = catalog.Files.Single(candidate => candidate.Path == sourcePath);
            var publishing = CompileCatalog(file);
            Until(() => publicationHold.Entered.Task.IsCompleted);
            firstHold.Release.TrySetResult();
            Await(selecting);
            Check(!FormShows("A") && !publishing.IsCompleted, "A superseded cold research request waits for its current publication", Field<TextBlock>(view, "_compileText").Text ?? "");
            publicationHold.Release.TrySetResult();
            Await(publishing);
            Until(() => ViewName() == "Generation A" && FormShows("A"));
            Check(Field<TextBlock>(view, "_compileText").Text != "Compiling…", "The eventual same-source publication loads the real selected view and parameter form", ViewName());
            var generationA = file.Generation ?? throw new InvalidOperationException("The real compiler did not publish generation A.");
            using (var frame = researchWindow.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "generation_publication.png"));

            var cachedHold = HoldNextCompilation();
            var forced = CompileView();
            Until(() => cachedHold.Entered.Task.IsCompleted);
            var cached = catalog.Compile(file);
            cachedHold.Release.TrySetResult();
            Await(forced);
            Check(forced.Result && cached.Success && ViewName() == "Generation A" && FormShows("A"), "A current cached publication also completes a superseded forced view compilation", ViewName());

            feed.Trades.AddRange(Enumerable.Range(0, 5).Select(index => new Trade(index + 1, feed.Now.AddMinutes(index - 6), 100m + index, 1m, TradeSide.Buy)));
            KvpSceneFeed.Register(services, profile, feed);
            var opening = window.Trading.NewTabAsync(profileId, chartInstrument.Symbol, ChartPeriod.M1);
            Await(opening);
            chart = opening.Result;
            Await(window.Trading.AddStrategyAsync(chart, file, new Dictionary<string, object?> { ["Input"] = 11 }, openSettings: false, generation: generationA));
            ExpectChart("A", 11, "The chart starts actual historical and continuing A executions");
            SetSource("B");
            var compilationB = catalog.Compile(file, force: true);
            var generationB = compilationB.Generation ?? throw new InvalidOperationException("The real compiler did not publish generation B.");
            var cs = chart.Strategies[0];
            var originalRunner = cs.Runner;
            Await(window.Trading.RunStrategyAsync(chart, cs, historyOnly: true));
            ExpectChart("A", 11, "History-only recalculation keeps the captured A factory after B publication");
            Check(ReferenceEquals(originalRunner, cs.Runner), "Historical recalculation does not replace the active runner", cs.Runner?.StrategyInstance.ToString() ?? "");
            window.Trading.RestartStrategies(chart, instrumentChanged: false);
            Until(() => !cs.Busy && cs.Runner != null && !ReferenceEquals(originalRunner, cs.Runner));
            ExpectChart("A", 11, "Automatic chart restart keeps A after B publication");
            cs.Parameters["Input"] = 13;
            Await(window.Trading.RunStrategyAsync(chart, cs, keepProtection: true));
            ExpectChart("A", 13, "A same-chart parameter restart changes parameters without changing generation");
            Await(window.Trading.RunStrategyAsync(chart, cs, generation: generationB));
            ExpectChart("B", 13, "An explicit chart generation override executes B");
            Await(window.Trading.AddStrategyAsync(chart, file, new Dictionary<string, object?> { ["Input"] = 19 }, openSettings: false));
            Check(chart.Strategies[1].Runner?.StrategyInstance.ToString() == "B:19", "A newly launched chart strategy can select the latest B publication", chart.Strategies[1].Runner?.StrategyInstance.ToString() ?? "");

            var launch = services.Live.LaunchAsync(new LiveLaunch
            {
                File = file, Generation = generationA, ProfileId = profileId, Instrument = mini, AccountId = "sim", Period = ChartPeriod.M1,
                Parameters = new() { ["Input"] = 23 },
            });
            Await(launch);
            Require(launch.Result.Runner != null, "The actual panel launch must start. " + launch.Result.Message);
            runnerKey = launch.Result.Runner!.Key;
            ExpectPanel("A", 23, "The panel executes the supplied A factory even with B in the catalog");
            var entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Await(services.Live.RestartWithParametersAsync(entry, new Dictionary<string, object?> { ["Input"] = 29 }));
            ExpectPanel("A", 29, "A same-process panel parameter restart keeps its launched A generation");
            services.Settings.SmartContracts = true;
            services.Live.ApplySize(runnerKey, 0.5m);
            Until(() =>
            {
                services.Live.Tick();
                return services.Live.Running(runnerKey) is { ExecInstrument.Symbol: "MNQ" };
            });
            ExpectPanel("A", 29, "Automatic mini-to-micro reconstruction keeps the launched A factory");
            var microRunner = services.Live.Running(runnerKey)!;
            Check(microRunner.CrossExecution && microRunner.ExecRatio == 10 && microRunner.SizeMultiplier == 0.5m,
                "The automatic continuation really changes execution contract", $"{microRunner.ExecInstrument.Symbol}, ratio {microRunner.ExecRatio}, size {microRunner.SizeMultiplier}");
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Await(services.Live.StopAsync(entry, close: false));
            var stoppedPanel = entry.Runner;
            var unrelatedChartStrategy = chart.Strategies[1];
            unrelatedChartStrategy.Live = true;
            Await(window.Trading.RunStrategyAsync(chart, unrelatedChartStrategy));
            Require(stoppedPanel != null && !services.Runners.Contains(stoppedPanel), "The actual unrelated chart start must prune the stopped panel runner.");
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Require(entry.Runner == null, "The stopped panel must be reconstructed from its saved entry rather than a retained runner.");
            Await(services.Live.ResumeAsync(entry));
            ExpectPanel("A", 29, "A fresh saved panel entry keeps A after an unrelated chart prunes its stopped runner");
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Await(services.Live.StopAsync(entry, close: false));
            Await(window.Trading.RunStrategyAsync(chart, unrelatedChartStrategy));
            File.Delete(sourcePath);
            catalog.Refresh();
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Await(services.Live.ResumeAsync(entry));
            ExpectPanel("A", 29, "Same-process resume keeps A even after the source disappears from the catalog");
            File.WriteAllText(sourcePath, Source(className, "B"));
            catalog.Refresh();
            file = catalog.Files.Single(candidate => candidate.Path == sourcePath);
            catalog.Compile(file);
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Await(services.Live.StopAsync(entry, close: false));
            var restored = services.Live.LaunchFromInfoAsync(entry.Info);
            Await(restored);
            Require(restored.Result.Spec != null, "Saved-run reconstruction must resolve the current real compilation. " + restored.Result.Error);
            var restoredLaunch = services.Live.LaunchAsync(restored.Result.Spec!);
            Await(restoredLaunch);
            Require(restoredLaunch.Result.Runner != null, "The explicit process-restore policy must launch. " + restoredLaunch.Result.Message);
            ExpectPanel("B", 29, "A restore without an in-process generation explicitly uses current B");
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Await(services.Live.StopAsync(entry, close: false));
            Await(window.Trading.RunStrategyAsync(chart, unrelatedChartStrategy));
            SetSource("A");
            catalog.Compile(file, force: true);
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Await(services.Live.ResumeAsync(entry));
            Require(services.Live.Running(runnerKey)?.StrategyInstance.ToString() == "B:29",
                "The saved panel must retain B before testing a failed metadata removal.");
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Await(services.Live.StopAsync(entry, close: false));
            Await(window.Trading.RunStrategyAsync(chart, unrelatedChartStrategy));
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Require(entry.Runner == null, "The failed-removal consumer must not retain a stopped runner.");
            string runnerIndex = Path.Combine(LiveStore.Root, "estrategias.json");
            byte[] savedIndex = File.ReadAllBytes(runnerIndex);
            var indexAttributes = File.GetAttributes(runnerIndex);
            try
            {
                File.SetAttributes(runnerIndex, indexAttributes | FileAttributes.ReadOnly);
                bool writeRefused = false;
                try { using var writable = new FileStream(runnerIndex, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writeRefused = true; }
                Require(writeRefused, "The owned readable runner index must actually refuse writes.");
                var deletion = services.Live.DeleteAsync(entry, history: false);
                Await(deletion);
                Check(deletion.Result != null, "A failed saved-panel removal reports its persistence error", deletion.Result ?? "Reported success");
                Check(savedIndex.AsSpan().SequenceEqual(File.ReadAllBytes(runnerIndex)),
                    "A refused panel removal leaves the original saved metadata intact", runnerIndex);
            }
            finally { File.SetAttributes(runnerIndex, indexAttributes); }
            entry = services.Live.Entries().Single(candidate => candidate.Key == runnerKey);
            Await(services.Live.ResumeAsync(entry));
            ExpectPanel("B", 29, "A surviving saved panel retains B after its actual metadata deletion fails");
            SetSource("B");
            catalog.Compile(file, force: true);
            Await(view.OpenFileAsync(sourcePath));

            RejectChangedEditor(file, dirty: true);
            RejectChangedEditor(file, dirty: false);
            SetSource("A");
            var selectionHold = HoldNextCompilation();
            var oldSelection = CompileView();
            Until(() => selectionHold.Entered.Task.IsCompleted);
            Await(view.OpenFileAsync(otherPath));
            selectionHold.Release.TrySetResult();
            Await(oldSelection);
            pump(0.05);
            Check(ViewName() == "Generation C" && FormShows("C") && !FormShows("A"), "A completed old request cannot replace a different selected strategy", ViewName());
            Await(view.OpenFileAsync(sourcePath));
            SetSource("B");
            Await(CompileView());
            SetSource("A");
            var requestHold = HoldNextCompilation();
            var oldRequest = CompileView();
            Until(() => requestHold.Entered.Task.IsCompleted);
            SetSource("B");
            Await(CompileView());
            requestHold.Release.TrySetResult();
            Await(oldRequest);
            Check(ViewName() == "Generation B" && FormShows("B"), "A completed old request cannot replace a newer same-view compilation", ViewName());
            File.Delete(sourcePath);
            var missing = CompileView();
            Await(missing);
            var diagnostics = Field<ListBox>(Editor(), "_errors").Items.OfType<ListBoxItem>().Select(item => item.Tag).OfType<ScriptDiagnostic>().ToArray();
            Check(!missing.Result && diagnostics.Any(diagnostic => diagnostic.Id == "TH02" && diagnostic.Severity == "error") &&
                Field<TextBlock>(view, "_compileText").Text != "Compiling…", "A disappeared clean source produces ordinary visible failed-compilation diagnostics", string.Join("; ", diagnostics.Select(diagnostic => diagnostic.ToString())));
            SetSource("B");
            var recovered = CompileView();
            Await(recovered);
            Check(recovered.Result && ViewName() == "Generation B" && FormShows("B"), "The same view recovers through real compilation after the source returns", ViewName());

            var selectedGeneration = Field<CompiledStrategyGeneration>(view, "_generation");
            string alternateClass = className + "Alternate";
            string selectedStatePath = Path.Combine(AppPaths.Backtests, selectedGeneration.Id, "ui.json");
            string alternateStatePath = Path.Combine(AppPaths.Backtests, alternateClass, "ui.json");
            typeof(StrategiesView).GetMethod("SaveState", Private)!.Invoke(view, null);
            Require(File.Exists(selectedStatePath) && !File.Exists(alternateStatePath), "The actual selected generation has saved state and the alternate identity is unused.");
            Directory.CreateDirectory(Path.GetDirectoryName(alternateStatePath)!);
            byte[] alternateState = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(selectedStatePath) + "\n ");
            File.WriteAllBytes(alternateStatePath, alternateState);
            string alternateSource = Source(alternateClass, "X");
            File.WriteAllText(sourcePath, alternateSource);
            Editor().Text = alternateSource;
            var identityHold = HoldNextCompilation();
            var identityCompilation = CompileView();
            Until(() => identityHold.Entered.Task.IsCompleted);
            Field<AvaloniaEdit.TextEditor>(Editor(), "_editor").Text = Source(className, "B");
            Require(Editor().IsDirty, "A real dirty editor must reject the different-identity publication.");
            identityHold.Release.TrySetResult();
            Await(identityCompilation);
            Require(Field<CompiledStrategyGeneration>(view, "_generation").Id == selectedGeneration.Id && file.Generation?.Id == alternateClass,
                "The view must retain its old executable generation while the catalog publishes the different class identity.");
            var state = Field<object>(view, "_state");
            var parameters = (Dictionary<string, object?>)state.GetType().GetProperty("Parameters")!.GetValue(state)!;
            parameters["Input"] = 37;
            typeof(StrategiesView).GetMethod("SaveState", Private)!.Invoke(view, null);
            using (var savedState = JsonDocument.Parse(File.ReadAllText(selectedStatePath)))
                Check(savedState.RootElement.GetProperty("Parameters").GetProperty("Input").GetInt32() == 37,
                    "State persistence follows the selected executable generation rather than a rejected catalog identity", selectedStatePath);
            Check(File.ReadAllBytes(alternateStatePath).AsSpan().SequenceEqual(alternateState),
                "A rejected different-identity compilation cannot overwrite another strategy's saved state", alternateStatePath);
            var savedRun = new StrategyRun
            {
                Id = "generation-identity-" + nonce, StrategyId = selectedGeneration.Id, StrategyName = selectedGeneration.DisplayName,
                Settings = new BacktestSettings { FeedId = chartFeed, Symbol = chartInstrument.Symbol, Period = ChartPeriod.M1.ToString(),
                    FromUtc = feed.Now.Date.AddDays(-1), ToUtc = feed.Now.Date },
                Parameters = new() { ["Input"] = 37 }, Metrics = new PerformanceMetrics { NetProfit = 17.25m, TotalTrades = 1 },
            };
            StrategyStore.Save(savedRun, selectedGeneration.Source.Text);
            Field<Ui.Segmented<string>>(view, "_tabs").Choose("history");
            pump(0.05);
            var historyRows = Field<ListBox>(view, "_historyList").Items.OfType<StrategyRunSummary>().ToArray();
            Check(historyRows.Any(summary => summary.Id == savedRun.Id && summary.CodeHash == savedRun.CodeHash),
                "Actual History lists the retained generation's saved run after rejecting a different class identity",
                $"Expected {savedRun.Id}, visible {string.Join(", ", historyRows.Select(summary => summary.Id))}");
            using (var frame = researchWindow.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "generation_selected_identity_history.png"));
            SetSource("B");
            Await(CompileView());

            SetSource("T");
            var terminalHold = HoldNextCompilation();
            var terminalCompilation = CompileView();
            Until(() => terminalHold.Entered.Task.IsCompleted);
            var shutdown = services.ShutdownAsync();
            pending.Add(shutdown);
            var terminalReceipt = shutdown.ContinueWith(_ =>
                (Returned: terminalHold.Returned.Task.IsCompleted, Publications: Volatile.Read(ref statusPublications)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            pending.Add(terminalReceipt);
            var releaseDeadline = Stopwatch.StartNew();
            try
            {
                while (!terminalReceipt.IsCompleted && releaseDeadline.Elapsed < TimeSpan.FromSeconds(2))
                    pump(0.01);
            }
            finally { terminalHold.Release.TrySetResult(); }
            Await(terminalCompilation);
            Await(shutdown);
            Await(terminalReceipt);
            pump(0.05);
            Check(terminalReceipt.Result.Returned, "True service shutdown retains a pending ordinary strategy compiler until its actual return",
                $"Compiler returned at terminal {terminalReceipt.Result.Returned}");
            Check(terminalReceipt.Result.Publications == Volatile.Read(ref statusPublications),
                "No ordinary strategy compilation publishes a final view callback after terminal completion",
                $"At terminal {terminalReceipt.Result.Publications}, final {Volatile.Read(ref statusPublications)}");
        }
        catch (Exception error)
        {
            fixtureError = error.ToString();
            Check(false, "The fixture reaches each actual consumer without an incidental failure", fixtureError);
        }
        finally
        {
            foreach (var hold in ownedHolds) hold.Release.TrySetResult();
            try
            {
                Until(() => pending.All(task => task.IsCompleted));
                foreach (var task in pending.Where(task => task.IsFaulted)) Check(false, "Owned finite consumer completed without a fault", task.Exception!.ToString());
                if (chart != null)
                {
                    foreach (var strategy in chart.Strategies.ToArray()) window.Trading.RemoveStrategy(chart, strategy);
                    window.Trading.CloseChart(chart);
                }
                if (runnerKey != null)
                {
                    foreach (var runner in services.Runners.Where(runner => runner.Key == runnerKey).ToArray()) runner.Stop(flatten: false);
                    if (services.Live.Entries().FirstOrDefault(entry => entry.Key == runnerKey) is { } entry)
                    {
                        var deletion = services.Live.DeleteAsync(entry, history: true);
                        Await(deletion);
                        Check(deletion.Result == null, "The owned panel history is removed through its ordinary consumer", deletion.Result ?? "Removed");
                    }
                }
                if (originalChart != null) window.Trading.Activate(originalChart);
                researchWindow?.Close();
                InstrumentPicker.ClearCache(profileId);
                services.Connections.Profiles.Remove(profile);
                Field<Dictionary<string, TradingHub.Feeds.IMarketDataFeed>>(services.Connections, "_feeds").Remove(profileId);
                feed.Dispose();
                File.Delete(sourcePath);
                File.Delete(otherPath);
                foreach (string strategyId in new[] { className, otherClass, className + "Alternate" })
                {
                    string directory = Path.Combine(AppPaths.Backtests, strategyId);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception error) { Check(false, "Owned generation fixture cleanup", error.ToString()); }
            services.Settings.SmartContracts = originalSmartContracts;
            typeof(AppServices).GetField("_strategies", Private)!.SetValue(services, originalCatalog);
            originalCatalog.Refresh();
            File.WriteAllText(Path.Combine(outDir, "generation-continuation-evidence.json"), JsonSerializer.Serialize(new
            {
                scene = "generation-continuation", fixtureError, checks,
                authority = "Real compiled A/B/C source, actual chart history and live runners, panel parameter/contract/resume reconstruction and selected StrategiesView publication/diagnostic consumers. All stores and providers are isolated fixtures. No real orders or model requests.",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return checks.Count(check => !check.Passed);
    }

    private static string Source(string className, string marker) => $$"""
        using TradingHub.Strategies;
        [Strategy("Generation {{marker}}")]
        public sealed class {{className}} : Strategy
        {
            [Parameter("Marker {{marker}}")]
            public int Input { get; set; } = {{(marker == "A" ? 1 : marker == "B" ? 2 : 3)}};
            private string _initialized = "not initialized";
            protected override void OnInit()
            {
                _initialized = "{{marker}}:" + Input;
                DrawText("generation", Time, 1, _initialized);
            }
            public override string ToString() => _initialized;
        }
        """;
}
