using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
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
using TradingHub.Scripting;

/// <summary>KVS scripts in the real editor/compiler, the grouped library and tracked editable copies of built-ins.</summary>
internal static class AuthoringScriptsScene
{
    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        int checks = 0, failures = 0;
        void Check(bool condition, string text) { checks++; Console.WriteLine($"{(condition ? "OK" : "FAIL")} authoring scripts: {text}"); if (!condition) failures++; }
        void Until(Func<bool> condition, string description)
        {
            var until = DateTime.UtcNow.AddSeconds(45);
            while (!condition() && DateTime.UtcNow < until) pump(0.02);
            Check(condition(), description);
        }
        T Await<T>(Task<T> task) { Until(() => task.IsCompleted, "asynchronous library operation completes"); return task.GetAwaiter().GetResult(); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Button Action(Window window, string text) => window.GetVisualDescendants().OfType<Button>().Single(button => EnglishScene.Labels(button).Contains(Lang.T(text)));
        Button TooltipAction(Window window, string text) => window.GetVisualDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), Lang.T(text)));
        static IEnumerable<MenuItem> Menu(Control control) => ((IEnumerable<Control>)control.ContextMenu!.ItemsSource!).OfType<MenuItem>();
        static void Choose(IEnumerable<MenuItem> items, string header) =>
            items.Single(m => Equals(m.Header, Lang.T(header))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        void Prompt(Window window, string text, string ok)
        {
            Until(() => window.OwnedWindows.OfType<PromptDialog>().Any(), "prompt opens");
            var prompt = window.OwnedWindows.OfType<PromptDialog>().Single();
            prompt.GetVisualDescendants().OfType<TextBox>().Single().Text = text;
            Click(Action(prompt, ok));
            pump(0.1);
        }
        void Save(Window window, string name) { using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(outDir, name)); }

        var svc = App.Services;
        var scripts = svc.Scripts!;

        // ---- KVS script: created, compiled, diagnosed in the real editor
        string scriptType = KvsTranslator.FullTypeNameFor("SceneBands");
        string scriptPath = Path.Combine(AppPaths.UserIndicators, "SceneBands" + KvsTranslator.Extension);
        var editor = new CodeEditorWindow(svc, CodeKind.Indicator);
        editor.Show(owner);
        pump(0.15);
        Click(TooltipAction(editor, "New KVS script (simple language: param / input / output / onBar)"));
        Prompt(editor, "SceneBands", "Create");
        Until(() => File.Exists(scriptPath) && svc.Catalog.Items.Any(i => i.Type.FullName == scriptType), "a new KVS script compiles through UserScripts into the library");
        var code = editor.GetVisualDescendants().OfType<CodeEditor>().Single();
        string good = code.Text;
        Check(good.Contains("name \"SceneBands\"", StringComparison.Ordinal) && !code.IsReadOnly, "the editor opens the English KVS template");
        Save(editor, "authoring_kvs_editor.png");

        string broken = good.Replace("basis = ema(src, length)", "basis = emma(src, length)", StringComparison.Ordinal);
        var lines = broken.Split('\n');
        int errorLine = Array.FindIndex(lines, l => l.Contains("emma(", StringComparison.Ordinal)) + 1;
        int errorColumn = lines[errorLine - 1].IndexOf("emma", StringComparison.Ordinal) + 1;
        code.Text = broken;
        code.IsDirty = true;
        Click(Action(editor, "Save and compile"));
        Until(() => scripts.LastResult?.Success == false, "the real compiler rejects an unknown KVS function");
        var error = scripts.LastResult!.Diagnostics.FirstOrDefault(d => d.Severity == "error");
        Check(error != null && error.File == scriptPath && error.Line == errorLine && error.Column == errorColumn && error.Message.Contains("emma", StringComparison.Ordinal),
            "the KVS diagnostic points at the script line and column");
        Check(svc.Catalog.Items.Any(i => i.Type.FullName == scriptType), "the last good script stays in the library after an error");
        Save(editor, "authoring_kvs_error.png");
        code.Text = good;
        code.IsDirty = true;
        Click(Action(editor, "Save and compile"));
        Until(() => scripts.LastResult?.Success == true, "the corrected script compiles again");
        editor.Close();

        // ---- grouped library
        var instrument = new Instrument { FeedId = "authoring-scripts", Symbol = "SCRIPT", TickSize = 1 };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed);
        for (int i = 0; i < 40; i++)
            data.Trades.Add(new Trade(i + 1, new DateTime(2026, 11, 4, 12, i, 0, DateTimeKind.Utc), 100 + i % 7, 1 + i % 3, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var library = new IndicatorsWindow(svc, model);
        library.Show(owner);
        pump(0.2);
        var available = (ListBox)typeof(IndicatorsWindow).GetField("_available", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(library)!;
        IEnumerable<ListBoxItem> Rows() => available.Items.OfType<ListBoxItem>();
        ListBoxItem Header(string id) => Rows().Single(r => r.Tag is IndicatorLibraryGroup g && g.Id == id);
        ListBoxItem Row(string type) => Rows().Single(r => r.Tag is IndicatorInfo i && i.Type.FullName == type);
        var groups = Rows().Select(r => r.Tag).OfType<IndicatorLibraryGroup>().Select(g => g.Id).ToList();
        var known = groups.Where(id => IndicatorCategories.LibraryOrder.Contains(id)).ToList();
        Check(groups.FirstOrDefault() == IndicatorLibrary.OwnGroup && known.Count >= 8 &&
              known.SequenceEqual(known.OrderBy(id => IndicatorCategories.LibraryOrder.ToList().IndexOf(id))),
            "custom indicators come first and built-ins follow in the default group order");
        Check(Rows().SkipWhile(r => !(r.Tag is IndicatorLibraryGroup { Id: IndicatorLibrary.OwnGroup })).Skip(1).TakeWhile(r => r.Tag is IndicatorInfo)
            .Any(r => ((IndicatorInfo)r.Tag!).Type.FullName == scriptType), "the KVS script is listed under My indicators");

        Choose(Menu(Header(IndicatorCategories.Vwap)), "New group…");
        Prompt(library, "Scene favorites", "Create");
        var favorites = svc.Settings.IndicatorLibrary.Groups.FirstOrDefault(g => g.Name == "Scene favorites");
        groups = Rows().Select(r => r.Tag).OfType<IndicatorLibraryGroup>().Select(g => g.Id).ToList();
        Check(favorites != null && groups.IndexOf(favorites.Id) == groups.IndexOf(IndicatorCategories.Vwap) + 1, "a new custom group is created below the chosen group");
        var move = Menu(Row(scriptType)).Single(m => Equals(m.Header, Lang.T("Move to group")));
        ((IEnumerable<Control>)move.ItemsSource!).OfType<MenuItem>().Single(m => Equals(m.Header, "Scene favorites")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        pump(0.1);
        Check(favorites != null && Rows().SkipWhile(r => r != Header(favorites.Id)).Skip(1).FirstOrDefault()?.Tag is IndicatorInfo { } moved && moved.Type.FullName == scriptType,
            "an indicator moves into the custom group");
        Choose(Menu(Header(IndicatorCategories.Vwap)), "Rename group…");
        Prompt(library, "Scene VWAPs", "Rename");
        Check(Header(IndicatorCategories.Vwap).Tag is IndicatorLibraryGroup { Name: "Scene VWAPs" }, "a default group takes a custom name");
        var vwapItems = Rows().SkipWhile(r => r != Header(IndicatorCategories.Vwap)).Skip(1).TakeWhile(r => r.Tag is IndicatorInfo).Select(r => (IndicatorInfo)r.Tag!).ToList();
        Choose(Menu(Row(vwapItems[1].Type.FullName!)), "Move up");
        var reordered = Rows().SkipWhile(r => r != Header(IndicatorCategories.Vwap)).Skip(1).TakeWhile(r => r.Tag is IndicatorInfo).Select(r => (IndicatorInfo)r.Tag!).ToList();
        Check(reordered.Count == vwapItems.Count && reordered[0] == vwapItems[1] && reordered[1] == vwapItems[0], "an indicator moves up within its group");
        var stored = AppSettings.Load().IndicatorLibrary;
        Check(stored.Groups.Any(g => g.Name == "Scene favorites" && g.Items.Contains(scriptType)) && stored.Groups.Any(g => g.Id == IndicatorCategories.Vwap && g.Name == "Scene VWAPs"),
            "the arrangement is persisted additively in settings");
        Save(library, "authoring_library_groups.png");

        // ---- display alias and remove-from-library, with the study on a live chart
        string average = typeof(MovingAverage).FullName!;
        Choose(Menu(Row(average)), "Add to chart");
        var slot = model.Indicators.Single(s => s.Indicator is MovingAverage);
        Choose(Menu(Row(average)), "Rename…");
        Prompt(library, "Scene average", "Rename");
        string RowText(string type) => ((DockPanel)Row(type).Content!).Children.OfType<TextBlock>().Last().Text ?? "";
        Check(RowText(average) == "Scene average" && slot.Indicator.Name == "Scene average" && slot.Indicator.GetType() == typeof(MovingAverage),
            "a rename relabels the library row and the indicator already on the chart without changing its type");
        Check(AppSettings.Load().IndicatorLibrary.Aliases.GetValueOrDefault(average) == "Scene average" &&
              !Workspace.SaveIndicator(slot.Indicator, out _).ToString().Contains("Scene average", StringComparison.Ordinal),
            "the alias is persisted in settings, never in the saved indicator");
        Save(library, "authoring_library_alias.png");
        Choose(Menu(Row(average)), "Remove from library…");
        Until(() => library.OwnedWindows.OfType<ConfirmDialog>().Any(), "remove-from-library asks first");
        Click(Action(library.OwnedWindows.OfType<ConfirmDialog>().Single(), "Remove from library"));
        pump(0.1);
        model.Update();
        Check(Rows().All(r => !(r.Tag is IndicatorInfo { } hiddenInfo && hiddenInfo.Type == typeof(MovingAverage))) && slot.Error == null && model.Indicators.Contains(slot),
            "a removed study leaves the library but stays on the chart");
        Check(svc.Catalog.Create(average) is MovingAverage, "saved charts can still create the removed study");
        var showHidden = Menu(Header(IndicatorCategories.Vwap)).Single(m => Equals(m.Header, Lang.T("Show hidden indicators (1)")));
        ((IEnumerable<Control>)showHidden.ItemsSource!).OfType<MenuItem>().Single(m => Equals(m.Header, Lang.T("Restore «Scene average»")))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        pump(0.1);
        Check(Rows().Any(r => r.Tag is IndicatorInfo { } shown && shown.Type == typeof(MovingAverage)) && svc.Settings.IndicatorLibrary.Hidden.Count == 0,
            "Show hidden indicators restores the study to the library");
        Choose(Menu(Row(average)), Lang.T("Restore the default name («Moving average»)"));
        Check(slot.Indicator.Name == Lang.T("Moving average") || slot.Indicator.Name == "Moving average", "restoring the default name relabels the chart");
        model.RemoveIndicator(slot);

        // ---- editable copy of a built-in, then an "updated original" with user edits
        Choose(Menu(Row(typeof(MovingAverage).FullName!)), "Edit a copy…");
        Until(() => svc.SourceCopies.Any(s => s.OriginalType == typeof(MovingAverage).FullName) && library.OwnedWindows.OfType<CodeEditorWindow>().Any(),
            "Edit a copy publishes a tracked copy and opens it");
        foreach (var opened in library.OwnedWindows.OfType<CodeEditorWindow>().ToList()) opened.Close();
        var copy = svc.SourceCopies.Single(s => s.OriginalType == typeof(MovingAverage).FullName);
        Until(() => svc.Catalog.Items.Any(i => i.Type.FullName == copy.CopyType), "the copy compiles as its own type while the original stays listed");
        Check(svc.Catalog.Items.Any(i => i.Type == typeof(MovingAverage)), "the built-in original remains available");
        string entry = Path.Combine(copy.Directory, copy.Manifest.EntryFile);
        File.AppendAllText(entry, "// scene edit\n");
        var manifest = IndicatorSourceCopies.ReadManifest(copy.Directory)!;
        manifest.Originals = manifest.Originals.ToDictionary(p => p.Key, _ => "previous-version", StringComparer.Ordinal);
        IndicatorSourceCopies.WriteManifest(copy.Directory, manifest);
        var reviewed = Await(svc.ReviewSourceCopiesAsync());
        Check(reviewed.Single(s => s.CopyType == copy.CopyType).State == SourceCopyState.OriginalUpdated && File.ReadAllText(entry).EndsWith("// scene edit\n", StringComparison.Ordinal),
            "an edited copy of an updated original is flagged and left unchanged");
        pump(0.1);
        Check(Row(copy.CopyType).Content is DockPanel badgeRow && badgeRow.Children.OfType<Border>().Any(b => b.Child is TextBlock { Text: var badge } && badge == Lang.T("original updated")),
            "the library shows the original-updated badge");
        Save(library, "authoring_library_original_updated.png");
        Choose(Menu(Row(copy.CopyType)), "Keep my edits and stop reminding me");
        Until(() => svc.SourceCopyFor(copy.CopyType)?.State == SourceCopyState.Current, "keeping edits clears the notice");
        Check(File.ReadAllText(entry).EndsWith("// scene edit\n", StringComparison.Ordinal), "keeping edits never rewrites the copy");

        // ---- cleanup of the isolated fixture
        library.Close();
        Directory.Delete(copy.Directory, recursive: true);
        File.Delete(scriptPath);
        IndicatorLibrary.Reset(svc.Settings.IndicatorLibrary);
        svc.Settings.Save();
        scripts.CompileAll();
        Await(svc.ReviewSourceCopiesAsync());
        File.WriteAllText(Path.Combine(outDir, "authoring-scripts-results.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures }));
        return failures;
    }
}
