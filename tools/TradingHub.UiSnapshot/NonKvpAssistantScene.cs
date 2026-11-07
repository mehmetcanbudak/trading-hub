using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

internal static class NonKvpAssistantScene
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private sealed class SceneAbortedException(string message) : Exception(message);

    internal static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0, checks = 0;
        void Check(bool value, string message) { checks++; Console.WriteLine((value ? "OK " : "FAIL ") + message); if (!value) failures++; }
        if (OperatingSystem.IsWindows()) { Console.WriteLine("This Apple Silicon assistant scene requires a POSIX spawned CLI fixture."); return 1; }
        string fixture = Path.Combine(outDir, "assistant-fixture"); Directory.CreateDirectory(fixture);
        string cli = Path.Combine(fixture, "fake codex 'quoted'");
        File.WriteAllText(cli, "#!/bin/sh\nroot=$(dirname \"$0\")\ncase \" $* \" in *' --help '*) printf '%s\\n' '--ignore-user-config --ignore-rules --sandbox --json --effort VALUE --add-dir DIR'; exit 0;; esac\nprintf '%s\\0' \"$@\" > \"$root/args.txt\"\n/bin/cat > \"$root/stdin.txt\"\nprintf started > \"$root/started\"\nif [ -f \"$root/stop\" ]; then /bin/sleep 30; fi\nif [ -f \"$root/edit\" ]; then /bin/cp \"$root/generated.cs\" \"$TRADINGHUB_STRATEGIES_DIR/AssistantFixture.cs\"; printf 'second changed file' > \"$TRADINGHUB_STRATEGIES_DIR/AssistantFixture.txt\"; fi\n/bin/cat \"$root/reply.jsonl\"\n", new UTF8Encoding(false));
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string? oldCodex = Environment.GetEnvironmentVariable("TRADINGHUB_CODEX_CLI");
        string? oldClaude = Environment.GetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI");
        Environment.SetEnvironmentVariable("TRADINGHUB_CODEX_CLI", cli);
        Environment.SetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI", cli);
        object? view = null;
        var sceneDeadline = DateTime.UtcNow.AddMinutes(5);
        string ConsumerState() => view == null ? "view not created" :
            $"busy={Field<bool>(view, "_chatBusy")}, cliRunning={Field<IStrategyChatBridge>(view, "_claude").IsRunning}, " +
            $"compile={Field<TextBlock>(view, "_compileText").Text}, optionsEnabled={Field<ComboBox>(view, "_chatEngine").IsEnabled}";
        void Reply(params object[] events) => File.WriteAllText(Path.Combine(fixture, "reply.jsonl"), string.Join("\n", events.Select(e => e is string text ? text : JsonSerializer.Serialize(e))) + "\n");
        void Until(Func<bool> condition, string description, bool consumer = false)
        {
            // Consumer completion follows the real compiler task. A single scene watchdog is only an abort guard,
            // not a per-turn assumption about compile speed; never advance dependent stages after a failure.
            var until = consumer ? sceneDeadline : DateTime.UtcNow.AddSeconds(20);
            while (!condition() && DateTime.UtcNow < until) pump(0.01);
            if (!condition())
            {
                string failure = description + " · " + ConsumerState();
                Check(false, failure);
                throw new SceneAbortedException(failure);
            }
            Check(true, description);
        }
        void Send(string prompt)
        {
            var send = (Task)Call(view!, "SendToClaudeAsync", prompt)!;
            bool sawConsumer = false, heldUnavailable = true;
            Until(() =>
            {
                if (!Field<IStrategyChatBridge>(view!, "_claude").IsRunning && Field<bool>(view!, "_chatBusy"))
                {
                    sawConsumer = true;
                    heldUnavailable &= !Field<Button>(view!, "_sendButton").IsVisible
                        && Field<TradingHub.App.Controls.CodeEditor>(view!, "_editor").IsReadOnly
                        && !Field<ComboBox>(view!, "_chatEngine").IsEnabled;
                }
                return send.IsCompleted;
            }, "actual assistant turn completes through editor/compiler and UI restoration", consumer: true);
            send.GetAwaiter().GetResult();
            Check(!Field<bool>(view!, "_chatBusy") && Field<ComboBox>(view!, "_chatEngine").IsEnabled
                && !Field<TradingHub.App.Controls.CodeEditor>(view!, "_editor").IsReadOnly, "completed turn restores only its owning consumer");
            if (sawConsumer) Check(heldUnavailable, "Send, editor and options remain unavailable for the entire actual post-edit consumer");
        }
        void Shot(string name)
        {
            pump(0.2); using var frame = window.CaptureRenderedFrame();
            Check(frame != null, name + " renders"); frame?.Save(Path.Combine(outDir, name + ".png"));
        }
        try
        {
            window.Navigate("strategies"); pump(0.2); view = window.Page("strategies")!;
            Call(view, "ShowTab", "code");
            var engine = Field<ComboBox>(view, "_chatEngine");
            Check(Field<IStrategyChatBridge>(view, "_claude").Engine == "claude", "legacy/default strategy assistant is Claude");
            engine.SelectedItem = "codex"; pump(0.05);
            Field<TextBox>(view, "_chatModel").Text = "fixture-model";
            Field<TextBox>(view, "_chatEffort").Text = "high";
            File.WriteAllText(Path.Combine(fixture, "generated.cs"), """
                using TradingHub.Strategies;
                [Strategy("Assistant fixture")]
                public class AssistantFixture : Strategy
                {
                    public int ObservedBars { get; private set; }
                    public decimal ObservedCloseSum { get; private set; }
                    protected override void OnBar(Bar bar)
                    {
                        ObservedBars++;
                        ObservedCloseSum += bar.Close * 2m;
                    }
                }
                """);
            File.WriteAllText(Path.Combine(fixture, "edit"), "yes");
            Reply(new { type = "thread.started", thread_id = "codex-fixture" },
                new { type = "item.completed", item = new { id = "files", type = "file_change", changes = new[] { new { kind = "add", path = Path.Combine(AppPaths.Strategies, "AssistantFixture.cs") }, new { kind = "add", path = Path.Combine(AppPaths.Strategies, "AssistantFixture.txt") } } } },
                new { type = "item.completed", item = new { id = "answer", type = "agent_message", text = "Created the fixture strategy and compiled it locally." } },
                new { type = "turn.completed", usage = new { input_tokens = 60, cached_input_tokens = 20, output_tokens = 12 } });
            Send("Fixture only: edit the owned strategy file; no network or model call.");
            File.Delete(Path.Combine(fixture, "edit"));
            var current = Field<StrategyFile>(view, "_current");
            var generation = current.Generation;
            Check(current.FileName == "AssistantFixture" && current.LastCompile?.Success == true && generation != null, "fake Codex edit reaches actual editor and compiler");
            if (generation == null)
                throw new SceneAbortedException("The actual edited-strategy compilation did not succeed.");
            var compiledStrategy = generation.Create();
            var instrument = new Instrument { FeedId = "fixture", Symbol = "ASSISTANT", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
            var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            decimal[] prices = [10m, 11m, 12m, 20m, 21m, 22m, 30m, 31m, 32m];
            var ticks = TickData.FromTrades(instrument, prices.Select((price, i) => new Trade(i + 1, start.AddSeconds(i * 20), price, 1m, TradeSide.Buy)).ToList());
            var replay = new BacktestEngine(compiledStrategy, ticks, new BacktestSettings
            {
                FeedId = "fixture", Symbol = "ASSISTANT", Period = "M1", FromUtc = start, ToUtc = start.AddDays(1),
                InitialCapital = 1000m, CommissionPercent = 0, SlippageTicks = 0,
            }).Run();
            Check(replay.Error == null && replay.Trades.Count == 0
                && (int)generation.Type.GetProperty("ObservedBars")!.GetValue(compiledStrategy)! == 3
                && (decimal)generation.Type.GetProperty("ObservedCloseSum")!.GetValue(compiledStrategy)! == 132m,
                "the actual CLI-edited and compiled strategy consumes three controlled closed bars (12, 22, 32), doubles their sum and places no orders");
            var codexConversation = Field<ChatConversation>(view, "_conv");
            Check(codexConversation.Engine == "codex" && codexConversation.SessionId == "codex-fixture", "engine and exact Codex session are persisted");
            Check(codexConversation.Messages.Last(m => m.Role == "assistant") is { ReportedCostUsd: null, TokenUsage.OutputTokens: 12 }, "unknown dollar billing and reported token usage remain distinct");
            Check(EnglishScene.Labels((Control)view).Any(s => s.Contains("Cost not reported", StringComparison.Ordinal)), "actual UI honestly displays unknown billing");
            string capturedArgs = File.ReadAllText(Path.Combine(fixture, "args.txt"));
            Check(capturedArgs.Contains("fixture-model", StringComparison.Ordinal) && capturedArgs.Contains("--ignore-user-config", StringComparison.Ordinal), "actual model control reaches isolated CLI arguments");
            Shot("nonkvp_assistant_codex_compiled");
            Call(view, "DetachClaude"); pump(0.1);
            var detached = Field<Window>(view, "_claudeWindow");
            Check(detached.Title == "Codex · strategies", "detached title uses active engine without changing geometry keys");
            using (var frame = detached.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "nonkvp_assistant_detached.png"));
            Call(view, "DockClaude");

            var legacy = new ChatConversation { Title = "Legacy Claude fixture", SessionId = "claude-fixture", Messages = { new ChatMessage { Role = "user", Text = "Legacy fixture" } } };
            Call(view, "LoadConversation", legacy);
            Check(Field<IStrategyChatBridge>(view, "_claude") is { Engine: "claude", SessionId: "claude-fixture" }, "legacy history selects Claude before resuming its exact session");
            Call(view, "LoadConversation", codexConversation);
            Check(Field<IStrategyChatBridge>(view, "_claude") is { Engine: "codex", SessionId: "codex-fixture" }, "Codex history never crosses into Claude");
            Check(Field<TextBox>(view, "_chatModel").Text == "fixture-model", "per-engine custom model is restored");
            Call(view, "LoadConversation", new ChatConversation { Engine = "unknown", SessionId = "foreign" });
            Check(Field<IStrategyChatBridge>(view, "_claude").SessionId == "codex-fixture", "unknown history cannot replace active engine/session");
            Call(view, "DeleteConversation", codexConversation);
            Check(Field<IStrategyChatBridge>(view, "_claude").SessionId == null && ChatHistory.List().All(c => c.Id != codexConversation.Id), "active history deletion clears adapter session and persisted history");

            Reply(new { type = "turn.failed", error = new { message = "Invalid model fixture-model" } });
            Send("Fixture failure");
            Check(EnglishScene.Labels((Control)view).Any(s => s.Contains("Invalid model fixture-model", StringComparison.Ordinal)), "model error remains visible on the actual surface");
            Shot("nonkvp_assistant_model_error");
            File.Delete(Path.Combine(fixture, "started")); File.WriteAllText(Path.Combine(fixture, "stop"), "yes");
            var stoppingBridge = Field<IStrategyChatBridge>(view, "_claude");
            int stoppedResults = 0;
            void StoppedResult(StrategyChatResult result) { stoppedResults++; Check(result.Status == StrategyChatStatus.Cancelled && !result.NeedsLogin, "stop terminal result is cancelled, not authentication failure"); }
            stoppingBridge.Finished += StoppedResult;
            var stopped = (Task)Call(view, "SendToClaudeAsync", "Fixture stop")!;
            Until(() => File.Exists(Path.Combine(fixture, "started")), "owned fake process reaches active turn");
            engine.SelectedItem = "claude";
            Check(Field<IStrategyChatBridge>(view, "_claude").Engine == "codex", "engine switching is blocked during request");
            Field<Button>(view, "_stopButton2").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => stopped.IsCompleted, "stop settles the complete owning consumer task", consumer: true);
            stopped.GetAwaiter().GetResult();
            Check(!Field<bool>(view, "_chatBusy"), "stop restores the owning consumer's editable state");
            File.Delete(Path.Combine(fixture, "stop"));
            Check(EnglishScene.Labels((Control)view).Any(s => s.Contains("Response stopped", StringComparison.Ordinal)), "cancellation is not misclassified as login");
            pump(0.05);
            Check(stoppedResults == 1, "cancellation emits exactly one terminal event");
            stoppingBridge.Finished -= StoppedResult;

            Environment.SetEnvironmentVariable("TRADINGHUB_CODEX_CLI", Path.Combine(fixture, "missing-cli"));
            engine.SelectedItem = "claude"; engine.SelectedItem = "codex"; pump(0.05);
            Check(!Field<Button>(view, "_sendButton").IsEnabled, "missing optional Codex is visible and Send is disabled");
            Send("Missing CLI fixture");
            Check(EnglishScene.Labels((Control)view).Any(s => s.Contains("CLI was not found", StringComparison.Ordinal)), "missing CLI produces a visible terminal error");
            Shot("nonkvp_assistant_missing_cli");
            Environment.SetEnvironmentVariable("TRADINGHUB_CODEX_CLI", cli);

            // Real adapters, not a fake bridge: collect streamed events and exactly one terminal event.
            var bridge = new CodexBridge(); int finished = 0; var files = new List<string>();
            bridge.Finished += _ => finished++;
            bridge.ToolUsed += (_, file) => { if (file != null) files.Add(file); };
            Reply(new { type = "thread.started", thread_id = "standalone" }, new { type = "item.completed", item = new { id = "two", type = "file_change", changes = new[] { new { kind = "update", path = "one.cs" }, new { kind = "update", path = "two.cs" } } } }, new { type = "turn.completed" });
            var direct = bridge.SendAsync(new("fixture", fixture, "owned instructions"));
            Until(() => direct.IsCompleted && finished == 1, "spawned Codex emits exactly one terminal result");
            Check(files.SequenceEqual(new[] { "one.cs", "two.cs" }), "all changed files are emitted, not just the first");
            var claudeBridge = new ClaudeCodeBridge(); var text = new StringBuilder(); int claudeFinished = 0;
            claudeBridge.Text += value => text.Append(value); claudeBridge.Finished += _ => claudeFinished++;
            Reply("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\"}}", "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Once\"}}}", "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Once\"}]}}", "{\"type\":\"result\",\"result\":\"Once\",\"is_error\":false,\"total_cost_usd\":0}");
            var claudeTask = claudeBridge.SendAsync(new("fixture", fixture, "owned instructions"));
            Until(() => claudeTask.IsCompleted && claudeFinished == 1, "spawned Claude emits exactly one terminal result");
            Check(text.ToString() == "Once" && claudeTask.Result.CostUsd == 0, "Claude partial/full text is deduplicated and reported zero is retained");
            // A separate real view/window exercises actual visual-tree teardown, not a private cleanup-method call.
            object teardownView = Activator.CreateInstance(view.GetType(), new object[] { Field<AppServices>(view, "_svc"), (Action<string>)(_ => { }) })!;
            var teardownWindow = new Window { Content = (Control)teardownView, Width = 900, Height = 650 };
            try
            {
                teardownWindow.Show(window);
                pump(0.05);
                Call(teardownView, "ShowTab", "code");
                Field<ComboBox>(teardownView, "_chatEngine").SelectedItem = "codex";
                File.Delete(Path.Combine(fixture, "started"));
                File.WriteAllText(Path.Combine(fixture, "stop"), "yes");
                var teardownSend = (Task)Call(teardownView, "SendToClaudeAsync", "Owned teardown fixture")!;
                Until(() => File.Exists(Path.Combine(fixture, "started")), "temporary actual view starts its owned CLI");
                var survivingConversation = Field<ChatConversation>(view, "_conv");
                teardownWindow.Close();
                Until(() => teardownSend.IsCompleted, "actual visual-tree teardown settles its returned consumer task", consumer: true);
                teardownSend.GetAwaiter().GetResult();
                Check(Field<bool>(teardownView, "_chatTornDown") && !Field<bool>(teardownView, "_chatBusy")
                    && !Field<IStrategyChatBridge>(teardownView, "_claude").IsRunning,
                    "teardown cancels its owned process without an orphan consumer await");
                Check(!Field<ComboBox>(teardownView, "_chatEngine").IsEnabled
                    && ReferenceEquals(survivingConversation, Field<ChatConversation>(view, "_conv")),
                    "stale teardown completion neither re-enables the removed view nor changes another view's history");
            }
            finally { teardownWindow.Close(); File.Delete(Path.Combine(fixture, "stop")); }
        }
        catch (SceneAbortedException ex) { Console.WriteLine("ABORT dependent assistant stages: " + ex.Message); }
        catch (Exception ex) { Check(false, "Assistant consumer scene failed: " + ex.Message + " · " + ConsumerState()); }
        finally
        {
            if (view != null) Field<IStrategyChatBridge>(view, "_claude").Cancel();
            Environment.SetEnvironmentVariable("TRADINGHUB_CODEX_CLI", oldCodex);
            Environment.SetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI", oldClaude);
        }
        Console.WriteLine($"Assistant scene: {checks} checks, {failures} failures.");
        return failures == 0 ? 0 : 1;
    }
}
