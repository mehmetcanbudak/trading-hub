using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using TradingHub.App.Services;
using TradingHub.Scripting;

namespace TradingHub.UiSnapshot;

// A dedicated real dispatcher loop: no App/MainWindow, providers, personal stores or CLI.
internal static class UserScriptsScene
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-script-monitor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (variable, child) in new[]
        {
            ("TRADINGHUB_DATA_DIR", "data"), ("TRADINGHUB_STRATEGIES_DIR", "strategies"),
            ("TRADINGHUB_INDICATORS_DIR", "indicators"), ("TRADINGHUB_BACKTESTS_DIR", "backtests"),
            ("TRADINGHUB_LIVE_DIR", "live"), ("TRADINGHUB_KEYCHAIN_FILE", "secrets.json")
        }) Environment.SetEnvironmentVariable(variable, Path.Combine(root, child));
        Environment.SetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD", "off");
        Console.WriteLine("Fixture storage: " + root);
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        using var finished = new CancellationTokenSource();
        int exit = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await DisposedMonitorCannotPublish();
                Console.WriteLine("PASS DisposedMonitorCannotPublish");
                await ExactContentHotReloadAndRecovery();
                Console.WriteLine("PASS ExactContentHotReloadAndRecovery");
                await InitialReconciliationAndReentrantDisposal();
                Console.WriteLine("PASS InitialReconciliationAndReentrantDisposal");
                await PendingDebounceCannotPublish();
                Console.WriteLine("PASS PendingDebounceCannotPublish");
                await DisposalStopsRemainingSubscribers();
                Console.WriteLine("PASS DisposalStopsRemainingSubscribers");
                CapturedSourcesDoNotReopenLivePaths();
                Console.WriteLine("PASS CapturedSourcesDoNotReopenLivePaths");
                await CapturedBytesPreserveBomDecoding();
                Console.WriteLine("PASS CapturedBytesPreserveBomDecoding");
                await ActiveCompilationCannotPublishAfterOffThreadDisposal();
                Console.WriteLine("PASS ActiveCompilationCannotPublishAfterOffThreadDisposal");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { finished.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(finished.Token);
        // Retain isolated files for the parent to capture with its receipt.
        return exit;
    }

    private static async Task DisposedMonitorCannotPublish()
    {
        var catalog = new IndicatorCatalog();
        using var scripts = new UserScripts(catalog);
        var initial = scripts.CompileAll();
        Require(initial?.Success == true, "Initial real script compilation must succeed.");
        int compiled = 0, changed = 0;
        scripts.Compiled += _ => compiled++;
        catalog.Changed += () => changed++;
        string file = Path.Combine(scripts.Folder, "DeltaPercentageExample.cs");
        File.AppendAllText(file, "\n// queued before disposal\n");
        // Keep the UI busy while the actual monitor queues a change. Do not pump its callback yet.
        Thread.Sleep(1000);
        scripts.Dispose();
        await Task.Delay(1800);
        Require(compiled == 0 && changed == 0 && ReferenceEquals(initial, scripts.LastResult),
            $"Disposed monitor published work: Compiled={compiled}, catalog changes={changed}.");
    }

    private static string Source(string type, string name) => $$"""
        using System.ComponentModel;
        using TradingHub.Indicators;
        [DisplayName("{{name}}")]
        public class {{type}} : Indicator
        {
            protected override void OnCalculate(int bar, decimal value) { }
        }
        """;

    private static async Task Until(Func<bool> condition, string message)
    {
        // A deadlock/failure watchdog, not an implementation timeout or a substitute for assertions.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Require(deadline.Elapsed < TimeSpan.FromSeconds(30), message);
            await Task.Delay(25);
        }
    }

    private static async Task ExactContentHotReloadAndRecovery()
    {
        string folder = AppPaths.UserIndicators;
        string file = Path.Combine(folder, "DeltaPercentageExample.cs");
        File.WriteAllText(file, Source("MonitorIndicator", "Version A"));
        var catalog = new IndicatorCatalog();
        using var scripts = new UserScripts(catalog);
        int compilations = 0;
        scripts.Compiled += _ =>
        {
            Require(Dispatcher.UIThread.CheckAccess(), "Compiled must run on the UI dispatcher.");
            compilations++;
        };
        catalog.Changed += () => Require(Dispatcher.UIThread.CheckAccess(), "Catalog changes must run on the UI dispatcher.");
        Require(scripts.CompileAll()?.Success == true, "Initial source must compile.");
        await Task.Delay(1300);
        Require(compilations == 1, "Unchanged initial sources must not compile twice.");

        DateTime timestamp = File.GetLastWriteTimeUtc(file);
        long length = new FileInfo(file).Length;
        File.WriteAllText(file, Source("MonitorIndicator", "Version B"));
        File.SetLastWriteTimeUtc(file, timestamp);
        Require(new FileInfo(file).Length == length, "Same-size fixture must really preserve size.");
        await Until(() => catalog.Find("MonitorIndicator")?.Name == "Version B", "Same-size/same-mtime edit was missed.");
        Require(compilations == 2, "One same-metadata edit must produce one compilation.");

        string replacement = file + ".replacement";
        File.WriteAllText(replacement, Source("MonitorIndicator", "Version C"));
        File.SetLastWriteTimeUtc(replacement, timestamp);
        File.Move(replacement, file, overwrite: true);
        await Until(() => catalog.Find("MonitorIndicator")?.Name == "Version C", "Atomic same-metadata replacement was missed.");

        int before = compilations;
        File.WriteAllText(file, Source("MonitorIndicator", "Version D"));
        File.WriteAllText(file, Source("MonitorIndicator", "Version E"));
        File.WriteAllText(file, Source("MonitorIndicator", "Version F"));
        await Until(() => catalog.Find("MonitorIndicator")?.Name == "Version F", "Burst edits did not publish their final content.");
        await Task.Delay(1000);
        Require(compilations == before + 1, "A save burst must coalesce into one compilation.");

        var lastGood = catalog.Find("MonitorIndicator");
        File.WriteAllText(file, "not valid C#");
        await Until(() => scripts.LastResult?.Success == false, "Syntax failure was not reported.");
        Require(ReferenceEquals(lastGood, catalog.Find("MonitorIndicator")), "Syntax failure erased the last-good catalog.");
        File.WriteAllText(file, Source("MonitorIndicator", "Recovered"));
        await Until(() => catalog.Find("MonitorIndicator")?.Name == "Recovered", "Syntax recovery was missed.");

        string nested = Path.Combine(folder, "nested", "deeper");
        Directory.CreateDirectory(nested);
        string member = Path.Combine(nested, "Member.cs");
        File.WriteAllText(member, Source("RecursiveIndicator", "Recursive"));
        await Until(() => catalog.Find("RecursiveIndicator") != null, "Recursive creation was missed.");
        var prior = scripts.LastResult;
        string renamed = Path.Combine(nested, "Renamed.cs");
        File.Move(member, renamed);
        await Until(() => !ReferenceEquals(prior, scripts.LastResult), "Recursive rename was missed.");
        File.Delete(renamed);
        await Until(() => catalog.Find("RecursiveIndicator") == null, "Recursive deletion was missed.");

        before = compilations;
        foreach (string excluded in new[] { "bin", "obj" })
        {
            string directory = Path.Combine(nested, excluded);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Ignored.cs"), "not valid C#");
        }
        await Task.Delay(1300);
        Require(compilations == before, "Excluded build outputs triggered compilation.");

        prior = scripts.LastResult;
        lastGood = catalog.Find("MonitorIndicator");
        using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Require(ReferenceEquals(prior, scripts.CompileAll()), "Transient read failure must return the previous result.");
            Require(ReferenceEquals(lastGood, catalog.Find("MonitorIndicator")), "Transient read failure erased the catalog.");
            await Task.Delay(750);
        }
        await Until(() => !ReferenceEquals(prior, scripts.LastResult), "Read retry required another filesystem edit.");

        prior = scripts.LastResult;
        lastGood = catalog.Find("MonitorIndicator");
        string unavailable = folder + ".unavailable";
        Directory.Move(folder, unavailable);
        try
        {
            Require(ReferenceEquals(prior, scripts.CompileAll()), "Missing root must not be treated as an empty source set.");
            await Task.Delay(900);
            Require(ReferenceEquals(lastGood, catalog.Find("MonitorIndicator")), "Root loss erased the last-good catalog.");
        }
        finally { Directory.Move(unavailable, folder); }
        File.WriteAllText(file, Source("MonitorIndicator", "Root recovery"));
        await Until(() => catalog.Find("MonitorIndicator")?.Name == "Root recovery", "Recreated root did not resume hot reload.");

        prior = scripts.LastResult;
        File.Move(file, file + ".saved");
        await Until(() => !catalog.Items.Any(item => item.Source == UserScripts.Source), "Empty source set did not remove its catalog.");
        Require(scripts.CompileAll() == null && ReferenceEquals(prior, scripts.LastResult),
            "No-source result/null behavior changed.");
    }

    private static async Task InitialReconciliationAndReentrantDisposal()
    {
        string file = Path.Combine(AppPaths.UserIndicators, "Initial.cs");
        File.WriteAllText(file, Source("InitialIndicator", "Initial A"));
        var catalog = new IndicatorCatalog();
        using var scripts = new UserScripts(catalog);
        bool edited = false;
        catalog.Changed += () =>
        {
            if (edited) return;
            edited = true;
            File.WriteAllText(file, Source("InitialIndicator", "Initial B"));
        };
        scripts.CompileAll();
        await Until(() => catalog.Find("InitialIndicator")?.Name == "Initial B", "An edit during initial catalog publication was lost.");
        int changes = 0, compiled = 0;
        catalog.Changed += () => { changes++; scripts.Dispose(); };
        scripts.Compiled += _ => compiled++;
        scripts.CompileAll();
        await Task.Delay(1200);
        Require(changes == 1 && compiled == 0, "Reentrant disposal must reject the remainder of active publication.");
        scripts.CompileAll();
        Require(changes == 1 && compiled == 0, "Explicit CompileAll after disposal published work.");
        File.Move(file, file + ".saved");
    }

    private static async Task PendingDebounceCannotPublish()
    {
        string file = Path.Combine(AppPaths.UserIndicators, "Pending.cs");
        File.WriteAllText(file, Source("PendingIndicator", "Pending A"));
        var catalog = new IndicatorCatalog();
        using var scripts = new UserScripts(catalog);
        var initial = scripts.CompileAll();
        Require(initial?.Success == true, "Pending fixture must compile initially.");
        int compiled = 0, changes = 0;
        scripts.Compiled += _ => compiled++;
        catalog.Changed += () => changes++;
        File.WriteAllText(file, Source("PendingIndicator", "Pending B"));
        await Task.Delay(350); // real monitor can observe; its 600 ms debounce has not elapsed
        scripts.Dispose();
        await Task.Delay(1200);
        Require(compiled == 0 && changes == 0 && ReferenceEquals(initial, scripts.LastResult),
            "Disposal failed to stop a pending debounce.");
    }

    private static async Task DisposalStopsRemainingSubscribers()
    {
        string file = Path.Combine(AppPaths.UserIndicators, "Events.cs");
        File.WriteAllText(file, Source("EventsIndicator", "Events"));
        using var scripts = new UserScripts(new IndicatorCatalog());
        int first = 0, late = 0;
        scripts.Compiled += _ => { first++; scripts.Dispose(); };
        scripts.Compiled += _ => late++;
        Require(scripts.CompileAll()?.Success == true, "Event fixture must compile.");
        scripts.Dispose(); // repeated disposal must still observe the completed worker drain
        await Task.Delay(1100);
        Require(first == 1 && late == 0, "Disposal within Compiled must stop remaining subscribers.");
    }

    private static void CapturedSourcesDoNotReopenLivePaths()
    {
        Directory.CreateDirectory(AppPaths.Root);
        string file = Path.Combine(AppPaths.Root, "Captured.cs");
        string textA = Source("CapturedIndicator", "Captured A");
        File.WriteAllText(file, textA);
        var captured = new ScriptSource(file, File.ReadAllText(file));
        var compiler = new ScriptCompiler();

        // A -> B -> A across a real compilation. The live path must not choose compiler inputs.
        File.WriteAllText(file, Source("CapturedIndicator", "Captured B"));
        var result = compiler.CompileSources([captured], "CapturedAba");
        File.WriteAllText(file, textA);
        RequireCapturedA(result, "A/B/A live-path mutation selected uncaptured content.");

        string missing = file + ".temporarily-missing";
        File.Move(file, missing);
        try
        {
            result = compiler.CompileSources([captured], "CapturedMissingPath");
            RequireCapturedA(result, "A missing live path silently omitted a captured indicator.");
        }
        finally { File.Move(missing, file); }

        var broken = new ScriptSource(file, "public class BrokenCapture\n{\n    missing_type Value;\n}");
        var diagnostics = compiler.CompileSources([broken], "CapturedDiagnostic");
        Require(!diagnostics.Success && diagnostics.Diagnostics.Any(d =>
                d.Id == "CS0246" && d.File == file && d.Line == 3 && d.Column == 5),
            "Captured diagnostics must preserve their original source path/line/column, not read the valid live replacement.");

        static void RequireCapturedA(ScriptCompileResult compiled, string message)
        {
            Require(compiled.Success && compiled.Assembly != null, message);
            var catalog = new IndicatorCatalog();
            catalog.Scan(compiled.Assembly!, UserScripts.Source);
            Require(catalog.Find("CapturedIndicator")?.Name == "Captured A", message);
        }
    }

    private static async Task CapturedBytesPreserveBomDecoding()
    {
        string file = Path.Combine(AppPaths.UserIndicators, "Bom.cs");
        File.WriteAllText(file, Source("BomIndicator", "UTF-16 Ω"), System.Text.Encoding.Unicode);
        var catalog = new IndicatorCatalog();
        using var scripts = new UserScripts(catalog);
        int compiled = 0;
        scripts.Compiled += _ => compiled++;
        Require(scripts.CompileAll()?.Success == true && catalog.Find("BomIndicator")?.Name == "UTF-16 Ω",
            "Captured bytes must preserve File.ReadAllText's BOM-aware decoding.");
        await Task.Delay(1100);
        Require(compiled == 1, "Unchanged BOM-encoded content recompiled.");
        scripts.Dispose();
        File.Move(file, file + ".saved");
    }

    private static async Task ActiveCompilationCannotPublishAfterOffThreadDisposal()
    {
        string file = Path.Combine(AppPaths.UserIndicators, "Active.cs");
        File.WriteAllText(file, Source("ActiveIndicator", "Active A"));
        var catalog = new IndicatorCatalog();
        using var scripts = new UserScripts(catalog);
        var initial = scripts.CompileAll();
        Require(initial?.Success == true, "Active-disposal fixture must compile initially.");
        var priorCatalog = catalog.Items.ToArray();
        int compiled = 0, changes = 0;
        scripts.Compiled += _ => compiled++;
        catalog.Changed += () => changes++;
        File.WriteAllText(file, Source("ActiveIndicator", "Active B"));
        bool disposedInsideCompiler = false;
        AssemblyLoadEventHandler loaded = (_, args) =>
        {
            if (!args.LoadedAssembly.IsCollectible ||
                args.LoadedAssembly.GetName().Name?.StartsWith("TradingHubUserIndicators_", StringComparison.Ordinal) != true) return;
            Require(Dispatcher.UIThread.CheckAccess(), "Real compiler assembly must load on the UI dispatcher.");
            // This public runtime event occurs inside the actual compiler, before it can return a result.
            // A failure watchdog only: no UI work is pumped to complete the off-thread disposal.
            Task.Run(scripts.Dispose).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            disposedInsideCompiler = true;
        };
        AppDomain.CurrentDomain.AssemblyLoad += loaded;
        try
        {
            Require(ReferenceEquals(initial, scripts.CompileAll()), "Active disposed compilation returned a new published result.");
        }
        finally { AppDomain.CurrentDomain.AssemblyLoad -= loaded; }
        await Task.Delay(1100);
        Require(disposedInsideCompiler && compiled == 0 && changes == 0 &&
                ReferenceEquals(initial, scripts.LastResult) && catalog.Items.SequenceEqual(priorCatalog),
            "An active compilation published after off-thread disposal or disposal depended on UI progress.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
