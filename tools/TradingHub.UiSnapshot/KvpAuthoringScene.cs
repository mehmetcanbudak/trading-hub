using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

internal static class KvpAuthoringScene
{
    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        int checks = 0, failures = 0;
        void Check(bool condition, string text) { checks++; Console.WriteLine($"{(condition ? "OK" : "FAIL")} KVP authoring: {text}"); if (!condition) failures++; }
        void Until(Func<bool> condition, string description)
        {
            var until = DateTime.UtcNow.AddSeconds(45);
            while (!condition() && DateTime.UtcNow < until) pump(0.02);
            Check(condition(), description);
        }
        T Await<T>(Task<T> task) { Until(() => task.IsCompleted, "actual asynchronous authoring operation completes"); return task.GetAwaiter().GetResult(); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Button Action(Window window, string text) => window.GetVisualDescendants().OfType<Button>().Single(button => EnglishScene.Labels(button).Contains(Lang.T(text)));
        Button TooltipAction(Window window, string text) => window.GetVisualDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), Lang.T(text)));
        var builtIn = new CodeEditorWindow(App.Services, CodeKind.Indicator, builtIn: typeof(MovingAverage));
        builtIn.Show(owner); pump(0.15);
        var originals = IndicatorSources.ForAssembly(typeof(MovingAverage).Assembly).View(typeof(MovingAverage).FullName!).Select(source => source.Text).ToArray();
        Check(builtIn.GetVisualDescendants().OfType<CodeEditor>().Single().IsReadOnly, "actual built-in source viewer is read-only");
        using (var frame = builtIn.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_builtin_source.png"));
        Click(Action(builtIn, "Make editable copy")); pump(0.1);
        var copyPrompt = builtIn.OwnedWindows.OfType<PromptDialog>().Single();
        copyPrompt.GetVisualDescendants().OfType<TextBox>().Single().Text = "KvpUiAverage";
        Click(Action(copyPrompt, "Copy complete source"));
        Until(() => builtIn.OwnedWindows.OfType<CodeEditorWindow>().Any(), "complete semantic source fork opens an actual editable window");
        var custom = builtIn.OwnedWindows.OfType<CodeEditorWindow>().Single();
        var editor = custom.GetVisualDescendants().OfType<CodeEditor>().Single();
        Check(!editor.IsReadOnly && App.Services.Catalog.Items.Any(item => item.Type.Name == "KvpUiAverage"), "fork compiles through real UserScripts and appears under its separate type identity");
        string copyPath = (string)typeof(CodeEditorWindow).GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(custom)!;
        string first = File.ReadAllText(copyPath);
        editor.Text = "// UI consumer edit; source attribution below is retained.\n" + first;
        editor.IsDirty = true;
        Click(Action(custom, "Save and compile")); pump(0.1);
        Check(File.ReadAllText(copyPath).StartsWith("// UI consumer edit;", StringComparison.Ordinal) && App.Services.Scripts?.LastResult?.Success == true,
            "actual save/compile persists the edit and publishes its compilation");
        Click(TooltipAction(custom, "Rename this file without changing its C# type or saved identities")); pump(0.1);
        var rename = custom.OwnedWindows.OfType<PromptDialog>().Single();
        rename.GetVisualDescendants().OfType<TextBox>().Single().Text = "KvpUiAverageRenamed";
        Click(Action(rename, "Rename file")); pump(0.15);
        string renamedPath = Path.Combine(Path.GetDirectoryName(copyPath)!, "KvpUiAverageRenamed.cs");
        Check(!File.Exists(copyPath) && File.Exists(renamedPath) && App.Services.Catalog.Items.Any(item => item.Type.Name == "KvpUiAverage"),
            "actual filename rename preserves the independent C# type/catalog identity");
        using (var frame = custom.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_custom_source_renamed.png"));
        Click(TooltipAction(custom, "Delete this source file")); pump(0.1);
        var delete = custom.OwnedWindows.OfType<ConfirmDialog>().Single();
        delete.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = delete }); pump(0.05);
        Check(File.Exists(renamedPath), "cancel-default deletion leaves actual source intact");
        Click(TooltipAction(custom, "Delete this source file")); pump(0.1);
        delete = custom.OwnedWindows.OfType<ConfirmDialog>().Single(); Click(Action(delete, "Delete source file")); pump(0.15);
        Check(!File.Exists(renamedPath) && App.Services.Catalog.Items.All(item => item.Type.Name != "KvpUiAverage"), "explicit deletion and real recompile remove only the fork");
        Check(originals.SequenceEqual(IndicatorSources.ForAssembly(typeof(MovingAverage).Assembly).View(typeof(MovingAverage).FullName!).Select(source => source.Text)), "built-in source resources remain unchanged after fork/edit/rename/delete");
        custom.Close(); builtIn.Close();

        byte[] pineBytes = Encoding.UTF8.GetBytes("//@version=5\r\n// Author: isolated UI fixture — preserve this attribution\r\nindicator(\"Fixture\")\r\nplot(close)\r\n");
        PineImportSource firstImport;
        using (var stream = new MemoryStream(pineBytes)) firstImport = Await(PineImports.PreserveAsync(stream, "study.pine", AppPaths.UserIndicators));
        PineImportSource secondImport;
        using (var stream = new MemoryStream(pineBytes)) secondImport = Await(PineImports.PreserveAsync(stream, "study.pine", AppPaths.UserIndicators));
        Check(firstImport.Path != secondImport.Path && File.ReadAllBytes(firstImport.Path).SequenceEqual(pineBytes) && File.ReadAllBytes(secondImport.Path).SequenceEqual(pineBytes),
            "duplicate Pine imports preserve exact original bytes without overwrite");
        var preview = PineImports.Preview(firstImport, "codex", "fixture-model", "high");
        var bridge = new DraftBridge(); int factories = 0;
        Task pineReview = PineReviewWindow.ShowOwnedAsync(App.Services, owner, preview, () => { factories++; return bridge; });
        int sourcesBefore = Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories).Count();
        pump(0.1);
        var pineWindow = owner.OwnedWindows.OfType<PineReviewWindow>().Single();
        Check(factories == 0 && bridge.Calls == 0, "importing and rendering the complete egress preview start no assistant");
        using (var frame = pineWindow.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_pine_review.png"));
        Click(Action(pineWindow, "Send source to Codex")); pump(0.1);
        var confirmation = pineWindow.OwnedWindows.OfType<ConfirmDialog>().Single();
        confirmation.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = confirmation }); pump(0.1);
        Check(bridge.Calls == 0 && factories == 0, "plain Enter cancels the paid-egress confirmation and sends nothing");
        Click(Action(pineWindow, "Send source to Codex")); pump(0.1);
        confirmation = pineWindow.OwnedWindows.OfType<ConfirmDialog>().Single(); Click(Action(confirmation, "Send reviewed source"));
        Until(() => bridge.Calls == 1 && pineWindow.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text == DraftBridge.Draft), "explicit approved action displays the fake assistant draft as text");
        Check(bridge.Last is { TextOnly: true, Laboratory: false, TradingView: null } request && request.ExtraDirectories.IsEmpty && request.Model == "fixture-model" && request.Effort == "high" && request.Prompt == preview.Prompt,
            "actual Pine handoff preserves selected engine/model/effort and exactly reviewed request, without lab/research/file access");
        Check(bridge.WorkspaceWasEmpty && bridge.Last != null && !Directory.Exists(bridge.Last.WorkDirectory), "draft handoff owns an empty temporary workspace and removes it after drain");
        Check(Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories).Count() == sourcesBefore && App.Services.Catalog.Items.All(item => item.Type.Name != "UncompiledPineDraft"),
            "assistant draft creates no C# file and is never automatically compiled or executed");
        using (var frame = pineWindow.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_pine_draft.png"));
        pineWindow.Close();
        Until(() => pineReview.IsCompleted, "owned Pine review drains and unregisters after close");
        pineReview.GetAwaiter().GetResult();

        string monitorFile = Path.Combine(AppPaths.UserIndicators, "UiReloadIndicator.cs");
        const string initial = "using TradingHub.Indicators; public class UiReloadIndicator : Indicator { public UiReloadIndicator() : base(true) {} protected override void OnCalculate(int bar, decimal value) { ((ValueDataSeries)DataSeries[0])[bar] = 7m; } }";
        File.WriteAllText(monitorFile, initial); App.Services.Scripts!.CompileAll();
        decimal Output()
        {
            var type = App.Services.Catalog.Items.Single(item => item.Type.Name == "UiReloadIndicator").Type;
            var instrument = new Instrument { FeedId = "authoring-fixture", Symbol = "SOURCE", TickSize = 1 };
            using var feed = new SimulatedFeed(); using var data = new InstrumentData(instrument, feed);
            data.Trades.Add(new Trade(1, new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc), 10, 1, TradeSide.Buy));
            using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
            var indicator = (Indicator)Activator.CreateInstance(type)!; model.AddIndicator(indicator);
            return ((ValueDataSeries)indicator.DataSeries[0])[0];
        }
        Check(Output() == 7m, "actual monitored source compiles to renderer-consumable value7");
        var oldAssembly = App.Services.Scripts.LastResult!.Assembly;
        DateTime timestamp = File.GetLastWriteTimeUtc(monitorFile);
        owner.Hide(); File.WriteAllText(monitorFile, initial.Replace("= 7m;", "= 9m;", StringComparison.Ordinal)); File.SetLastWriteTimeUtc(monitorFile, timestamp);
        Until(() => !ReferenceEquals(oldAssembly, App.Services.Scripts.LastResult?.Assembly), "hidden main window retains same-size/timestamp-preserved source monitoring");
        Check(Output() == 9m, "hidden-window hot reload publishes actual value9 without native watcher startup");
        File.WriteAllText(monitorFile, "not a valid indicator");
        Until(() => App.Services.Scripts.LastResult?.Success == false, "real compiler reports invalid hot-reload source");
        Check(Output() == 9m, "failed compilation preserves the last-good actual indicator catalog");
        string replacement = monitorFile + ".atomic"; File.WriteAllText(replacement, initial.Replace("= 7m;", "= 8m;", StringComparison.Ordinal)); File.Move(replacement, monitorFile, true);
        Until(() => App.Services.Scripts.LastResult?.Success == true, "atomic source replacement recovers through owned monitor");
        Check(Output() == 8m, "atomic replacement publishes actual value8 after failure");
        owner.Show(); owner.Activate(); pump(0.1); File.Delete(monitorFile); App.Services.Scripts.CompileAll();
        File.WriteAllText(Path.Combine(outDir, "kvp-authoring-results.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures }));
        return failures;
    }

    private sealed class DraftBridge : IStrategyChatBridge
    {
        public const string Draft = "// Unvalidated UI fixture draft only\npublic class UncompiledPineDraft {}";
        public string Engine => "codex";
        public string? SessionId => null;
        public bool IsRunning => false;
        public int Calls { get; private set; }
        public bool WorkspaceWasEmpty { get; private set; }
        public StrategyChatRequest? Last { get; private set; }
        public event Action<string>? Text;
        public event Action<StrategyChatResult>? Finished;
        public event Action<string, string?>? ToolUsed { add { } remove { } }
        public event Action? ToolFinished { add { } remove { } }
        public void NewConversation() { }
        public void Resume(string? sessionId) { if (sessionId != null) throw new InvalidOperationException("Fixture has no resumable session"); }
        public void Cancel() { }
        public Task<StrategyChatResult> SendAsync(StrategyChatRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; Last = request;
            WorkspaceWasEmpty = !Directory.EnumerateFileSystemEntries(request.WorkDirectory).Any();
            var result = new StrategyChatResult(StrategyChatStatus.Succeeded, Draft, null, 0, false);
            Text?.Invoke(Draft); Finished?.Invoke(result); return Task.FromResult(result);
        }
    }
}
