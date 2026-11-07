using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;

internal static class OrdinaryShutdownScene
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ControlReply = "The ordinary assistant control completed.";
    private const string HeldReply = "The held ordinary assistant completed after release.";
    private sealed record CheckResult(bool Passed, string Description);
    private sealed record TerminalObservation(bool ConsumerCompleted, bool BridgeRunning, bool CliExited,
        bool StderrReleased, int FinishedCallbacks, string HistoryDigest);

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);

    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
        {
            Console.Error.WriteLine("ordinary-shutdown requires the Unix held-stderr fixture. It does not run an installed assistant on unsupported platforms.");
            return 2;
        }

        RequireIsolatedStores();
        string fixture = Path.Combine(AppPaths.Root, "ordinary-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string cli = Path.Combine(fixture, "fake ordinary codex 'quoted'");
        string releaseCli = Path.Combine(fixture, "release-cli");
        string releaseStderr = Path.Combine(fixture, "release-stderr");
        string holderDone = Path.Combine(fixture, "held-stderr-done");
        string sourcePath = Path.Combine(AppPaths.Strategies, "OrdinaryShutdownProbe" + Guid.NewGuid().ToString("N") + ".cs");
        string? oldCodex = Environment.GetEnvironmentVariable("TRADINGHUB_CODEX_CLI");
        string? oldClaude = Environment.GetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI");
        var application = Application.Current!;
        var lifetimeField = typeof(Application).GetField("_applicationLifetime", Private)!;
        object? previousLifetime = lifetimeField.GetValue(application);
        var previousQuit = App.Quit;
        var services = App.Services;
        var desktop = KvpTerminalDesktopLifetime.For(owner);
        var evidence = new List<CheckResult>();
        int failures = 0, finishedCallbacks = 0, lateCallbacks = 0, replacements = 0;
        bool terminalEntered = false;
        string? conversationId = null, historyPath = null, terminalHistory = null, finalHistory = null;
        TerminalObservation? terminal = null;
        StrategyChatResult? result = null;
        IStrategyChatBridge? bridge = null;
        Process? cliProcess = null, stderrProcess = null;
        Task? selection = null, controlConsumer = null, consumer = null, quitTask = null;

        void Check(bool condition, string description)
        {
            evidence.Add(new(condition, description));
            if (!condition) failures++;
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} ordinary shutdown: {description}");
        }
        void Require(bool condition, string description)
        {
            Check(condition, description);
            if (!condition) throw new InvalidOperationException(description);
        }
        void Until(Func<bool> condition, string description)
        {
            var watchdog = Stopwatch.StartNew();
            while (!condition() && watchdog.Elapsed < TimeSpan.FromSeconds(90)) pump(0.02);
            Require(condition(), description);
        }
        void Await(Task task, string description)
        {
            Until(() => task.IsCompleted, description);
            task.GetAwaiter().GetResult();
        }
        void Release()
        {
            File.WriteAllText(releaseCli, "Release only this scene's CLI.");
            File.WriteAllText(releaseStderr, "Release only this scene's stderr holder.");
        }
        void CleanTask(Task task, string description)
        {
            try { Await(task, description); }
            catch (Exception error) { Check(false, description + ": " + error); }
        }
        void Finished(StrategyChatResult completed)
        {
            result = completed;
            finishedCallbacks++;
            if (terminalEntered) lateCallbacks++;
        }
        string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        void Reply(string text) => File.WriteAllText(Path.Combine(fixture, "reply.jsonl"), string.Join('\n',
            JsonSerializer.Serialize(new { type = "thread.started", thread_id = "ordinary-shutdown-fixture" }),
            JsonSerializer.Serialize(new { type = "item.completed", item = new { id = "answer", type = "agent_message", text } }),
            JsonSerializer.Serialize(new { type = "turn.completed" })) + "\n");

        try
        {
            WriteCli(cli);
            Environment.SetEnvironmentVariable("TRADINGHUB_CODEX_CLI", cli);
            Environment.SetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI", cli);
            services.Settings.StrategyChatEngine = "codex";
            services.Settings.StrategyCodexModel = "";
            services.Settings.StrategyCodexEffort = "";
            File.WriteAllText(sourcePath, "using TradingHub.Strategies; [Strategy(\"Ordinary shutdown consumer\")] public class OrdinaryShutdownProbe : Strategy { }");
            services.Strategies.Refresh();
            Require(CanonicalPath(TradingHub.Strategies.StrategyStore.Root) == CanonicalPath(AppPaths.Backtests),
                "the initialized strategy catalog uses the configured isolated backtest store");
            var file = services.Strategies.Files.Single(item => item.Path == sourcePath);
            owner.Navigate("strategies");
            var view = owner.Page("strategies")!;
            selection = (Task)Call(view, "SelectAsync", file)!;
            Await(selection, "the actual strategy editor finishes selecting and compiling its isolated source");
            Require(file.LastCompile?.Success == true, "the ordinary assistant has a valid compiled strategy consumer");
            Field<ComboBox>(view, "_chatEngine").SelectedItem = "codex";
            bridge = Field<IStrategyChatBridge>(view, "_claude");

            Reply(ControlReply);
            controlConsumer = (Task)Call(view, "SendToClaudeAsync", "Complete the isolated ordinary assistant control.")!;
            Await(controlConsumer, "an unheld real fake CLI and its complete ordinary view consumer finish");
            var conversation = Field<ChatConversation>(view, "_conv");
            conversationId = conversation.Id;
            historyPath = Path.Combine(ChatHistory.Root, conversationId + ".json");
            Require(ChatHistory.List().Single(item => item.Id == conversationId).Messages.Any(message => message.Role == "assistant" && message.Text == ControlReply),
                "the unheld control is persisted by the actual ordinary chat-history consumer");
            Require(CanonicalPath(File.ReadAllText(Path.Combine(fixture, "workdir.txt")).TrimEnd('\r', '\n')) == CanonicalPath(AppPaths.Strategies),
                "the spawned fixture really uses the configured isolated strategy directory");

            Reply(HeldReply);
            File.WriteAllText(Path.Combine(fixture, "hold"), "Hold this ordinary turn and its redirected stderr.");
            bridge = Field<IStrategyChatBridge>(view, "_claude");
            bridge.Finished += Finished;
            consumer = (Task)Call(view, "SendToClaudeAsync", "Hold this isolated ordinary turn until the scene releases it.")!;
            Until(() => File.Exists(Path.Combine(fixture, "held-cli-pid")) && File.Exists(Path.Combine(fixture, "held-stderr-pid")),
                "the actual ordinary CLI is held and its independent stderr writer has been adopted");
            cliProcess = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture, "held-cli-pid")), System.Globalization.CultureInfo.InvariantCulture));
            stderrProcess = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture, "held-stderr-pid")), System.Globalization.CultureInfo.InvariantCulture));
            Require(!cliProcess.HasExited && !stderrProcess.HasExited && bridge.IsRunning && !consumer.IsCompleted,
                "the app-owned ordinary consumer is still executing a real, well-owned fixture process");
            Require(ChatHistory.List().Single(item => item.Id == conversationId).Messages.Last().Role == "user",
                "the held turn has an actual saved prompt but no completed assistant publication");
            using (var frame = owner.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "ordinary_shutdown_held.png"));

            lifetimeField.SetValue(application, desktop.Lifetime);
            var quit = App.UseQuit(desktop.Lifetime);
            quitTask = quit.QuitAsync(() =>
            {
                terminalEntered = true;
                replacements++;
                terminalHistory = File.ReadAllText(historyPath);
                terminal = new(consumer.IsCompleted, bridge.IsRunning, cliProcess.HasExited,
                    File.Exists(holderDone), finishedCallbacks, Digest(terminalHistory));
                Check(consumer.IsCompleted && !bridge.IsRunning && cliProcess.HasExited && File.Exists(holderDone) && finishedCallbacks == 1,
                    "the real replacement callback follows complete ordinary process, stream and view-consumer completion");
            });
            var rootShutdown = services.ShutdownAsync();
            Until(() => terminalEntered || cliProcess.HasExited,
                "true Quit either stops its ordinary CLI or exposes a premature terminal handoff");
            Check(!consumer.IsCompleted && bridge.IsRunning && !File.Exists(holderDone),
                "cancellation or early replacement cannot impersonate completion of the held ordinary stream consumer");
            if (!terminalEntered)
                Check(!rootShutdown.IsCompleted && !quit.IsTerminalComplete && desktop.Shutdowns == 0,
                    "true exit remains pending after child termination while its real stderr drain is held");

            File.WriteAllText(releaseCli, "The finite ordinary CLI may now complete cooperatively.");
            Until(() => cliProcess.HasExited, "the actual ordinary CLI exits after cancellation or explicit cooperative release");
            Check(!consumer.IsCompleted && bridge.IsRunning && !File.Exists(holderDone),
                "CLI exit alone does not settle the actual ordinary view while redirected stderr remains open");
            File.WriteAllText(releaseStderr, "The owned stderr writer may finish.");
            Await(consumer, "the complete ordinary view consumer settles after its process and stream release");
            Until(() => finishedCallbacks == 1 && File.Exists(holderDone),
                "the real bridge delivers one terminal callback after the independent stderr writer finishes");
            Require(result?.Status is StrategyChatStatus.Succeeded or StrategyChatStatus.Cancelled,
                "the controlled ordinary CLI completes or cancels without a real assistant, login or incidental protocol failure");
            Await(rootShutdown, "the real AppServices terminal drain finishes");
            Await(quitTask, "the actual protected Quit operation finishes");
            Require(replacements == 1 && desktop.Shutdowns == 1 && quit.IsTerminalComplete,
                "the accepted true quit invokes one replacement and one desktop exit");
            finalHistory = File.ReadAllText(historyPath);
            Check(terminalHistory == finalHistory, "no ordinary chat-history save follows the real terminal handoff");
            Check(lateCallbacks == 0, "no ordinary bridge completion callback follows the real terminal handoff");
            Check(ChatHistory.List().Single(item => item.Id == conversationId).Messages.Any(message => message.Role == "assistant" && message.Text == ControlReply),
                "true shutdown preserves the previously completed ordinary conversation");
        }
        catch (Exception error)
        {
            Check(false, "the ordinary shutdown fixture could not complete its declared consumer scenario: " + error);
        }
        finally
        {
            Release();
            try { bridge?.Cancel(); }
            catch (Exception error) { Check(false, "owned ordinary cleanup cancellation failed: " + error); }
            if (selection != null) CleanTask(selection, "the owned strategy selection is drained during cleanup");
            if (controlConsumer != null) CleanTask(controlConsumer, "the unheld ordinary control is drained during cleanup");
            if (consumer != null) CleanTask(consumer, "the actual ordinary consumer is drained during cleanup");
            CleanTask(services.ShutdownAsync(), "the real service root is drained during cleanup");
            CleanTask(Keychain.ShutdownAsync(), "the isolated private-store terminal drain finishes during cleanup");
            if (quitTask != null) CleanTask(quitTask, "the accepted true Quit finishes during cleanup");
            if (bridge != null) bridge.Finished -= Finished;
            cliProcess?.Dispose();
            stderrProcess?.Dispose();
            if ((selection?.IsCompleted ?? true) && (controlConsumer?.IsCompleted ?? true) && (consumer?.IsCompleted ?? true)) File.Delete(sourcePath);
            lifetimeField.SetValue(application, previousLifetime);
            typeof(App).GetProperty("Quit", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, previousQuit);
            Environment.SetEnvironmentVariable("TRADINGHUB_CODEX_CLI", oldCodex);
            Environment.SetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI", oldClaude);
            File.WriteAllText(Path.Combine(outDir, "ordinary-shutdown-results.json"), JsonSerializer.Serialize(new
            {
                checks = evidence.Count, failures, conversationId, fixture, terminal, replacements, lateCallbacks,
                finalHistoryDigest = finalHistory == null ? null : Digest(finalHistory), result, evidence,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures;
    }

    private static void RequireIsolatedStores()
    {
        string[] variables = ["TRADINGHUB_DATA_DIR", "TRADINGHUB_STRATEGIES_DIR", "TRADINGHUB_INDICATORS_DIR",
            "TRADINGHUB_BACKTESTS_DIR", "TRADINGHUB_LIVE_DIR", "TRADINGHUB_KEYCHAIN_FILE"];
        string[] protectedRoots = [CanonicalPath(TradingHub.Common.Product.DefaultAppData), CanonicalPath(TradingHub.Common.Product.DefaultDocuments)];
        foreach (string variable in variables)
        {
            string? configured = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("Missing isolated store " + variable + ". Actual environment value: " + (configured ?? "<unset>"));
            string path = configured;
            string resolved = CanonicalPath(path);
            if (protectedRoots.Any(root => resolved.Equals(root, StringComparison.OrdinalIgnoreCase) || resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The ordinary shutdown scene refuses a production store for " + variable + ". Configured path: " + path + ". Resolved path: " + resolved);
        }
        if (Environment.GetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD") != "off") throw new InvalidOperationException("The ordinary shutdown scene requires sleep guard off.");
        foreach (var (variable, actual) in new[]
        {
            ("TRADINGHUB_DATA_DIR", AppPaths.Root),
            ("TRADINGHUB_STRATEGIES_DIR", AppPaths.Strategies),
            ("TRADINGHUB_INDICATORS_DIR", AppPaths.UserIndicators),
            ("TRADINGHUB_BACKTESTS_DIR", AppPaths.Backtests),
            ("TRADINGHUB_LIVE_DIR", TradingHub.Strategies.LiveStore.Root),
        })
        {
            string expected = Environment.GetEnvironmentVariable(variable)!;
            if (CanonicalPath(actual) != CanonicalPath(expected))
                throw new InvalidOperationException("The ordinary shutdown fixture has a setup-root mismatch for " + variable + ". Expected: " + expected + ". Actual consumer root: " + actual);
        }
    }

    private static string CanonicalPath(string path)
    {
        path = Path.GetFullPath(path);
        string resolved = Path.GetPathRoot(path)!;
        foreach (string part in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            FileSystemInfo entry = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            if (entry.LinkTarget != null) resolved = entry.ResolveLinkTarget(true)!.FullName;
        }
        return resolved.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static void WriteCli(string path)
    {
        File.WriteAllText(path, """
            #!/bin/sh
            root=$(dirname "$0")
            case " $* " in
              *' --help '*) printf '%s\n' '--ignore-user-config --ignore-rules --sandbox --json --add-dir DIR'; exit 0;;
            esac
            printf '%s\0' "$@" > "$root/args.txt"
            /bin/pwd > "$root/workdir.txt"
            /bin/cat > "$root/stdin.txt"
            if [ -f "$root/hold" ]; then
              printf '%s' "$$" > "$root/held-cli-pid.tmp"
              /bin/mv "$root/held-cli-pid.tmp" "$root/held-cli-pid"
              /bin/sh -c '/bin/sh "$1/stderr-holder" "$1" "$$" >/dev/null &' holder "$root"
              while [ ! -f "$root/release-cli" ]; do /bin/sleep 0.02; done
            fi
            /bin/cat "$root/reply.jsonl"
            """ + "\n", new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Adoption puts only the fixture's stderr writer outside the CLI's cancellation tree.
        // Its explicit release keeps a real OS pipe open after CLI exit, as in StrategyAssistantTests.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "stderr-holder"), """
            #!/bin/sh
            root=$1
            former=$2
            while [ "$(/bin/ps -o ppid= -p "$$" | /usr/bin/tr -d ' ')" = "$former" ]; do /bin/sleep 0.02; done
            printf '%s' "$$" > "$root/held-stderr-pid.tmp"
            /bin/mv "$root/held-stderr-pid.tmp" "$root/held-stderr-pid"
            while [ ! -f "$root/release-stderr" ]; do /bin/sleep 0.02; done
            printf 'ordinary fixture stderr fully released\n' >&2
            printf done > "$root/held-stderr-done.tmp"
            /bin/mv "$root/held-stderr-done.tmp" "$root/held-stderr-done"
            """ + "\n", new UTF8Encoding(false));
    }
}
