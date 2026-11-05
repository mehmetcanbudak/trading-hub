using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Microsoft.CodeAnalysis.CSharp;
using TradingHub.App.Controls;
using TradingHub.App.Services;

namespace TradingHub.App.Windows;

internal enum CodeKind
{
    Indicator,
}

/// <summary>Views immutable built-ins or edits a separate user-owned C# source tree through its existing compiler.</summary>
internal sealed class CodeEditorWindow : Window
{
    private readonly AppServices _svc;
    private readonly Type? _builtIn;
    private readonly IndicatorSourceFiles _sources;
    private readonly ListBox _files = new() { Background = Brushes.Transparent };
    private readonly CodeEditor _editor = new();
    private readonly TextBlock _title = new() { Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 8, Height = 8 };
    private readonly TextBlock _state = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    private string? _path;
    private string _loadedText = "";
    private bool _selectingFile;

    public CodeEditorWindow(AppServices svc, CodeKind kind, string? open = null, Type? builtIn = null)
    {
        _svc = svc;
        _builtIn = builtIn;
        _sources = new IndicatorSourceFiles(AppPaths.UserIndicators);
        Title = builtIn == null ? "Custom indicator editor" : "Built-in indicator source (read only)";
        Width = 1180;
        Height = 780;
        MinWidth = 820;
        MinHeight = 520;
        Background = Ui.Bg0;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Window;
        Content = Build();
        _editor.SaveRequested += SaveAndCompile;
        _editor.CompileRequested += SaveAndCompile;
        _editor.Changed += () => _title.Text = Path.GetFileName(_path) + " •";
        if (builtIn != null)
        {
            var documents = IndicatorSources.ForAssembly(builtIn.Assembly).View(builtIn.FullName!);
            _title.Text = builtIn.FullName;
            _editor.Text = string.Join("\n\n", documents.Select(d => "// " + d.Attribution + "\n" + d.Text));
            _editor.IsReadOnly = true;
            _dot.Fill = Ui.Text2;
            _state.Text = "Read only. Make an editable copy to change this indicator; the original stays unchanged.";
        }
        else
        {
            if (_svc.Scripts != null) _svc.Scripts.Compiled += OnCompiled;
            FillFiles(open);
            _editor.IsReadOnly = _path == null;
            if (_svc.Scripts?.LastResult is { } last) OnCompiled(last);
        }
        Closing += (_, e) => { if (_editor.IsDirty && !Save()) e.Cancel = true; };
        Closed += (_, _) => { if (_svc.Scripts != null) _svc.Scripts.Compiled -= OnCompiled; };
    }

    private Control Build()
    {
        var add = Ui.Button("New", MaterialIconKind.Plus, () => _ = NewAsync());
        var folder = Ui.IconButton(MaterialIconKind.FolderOpenOutline, Os.IsWindows ? "Open folder in File Explorer" : "Open folder in Finder", () => Shell.OpenFolder(AppPaths.UserIndicators));
        var pine = Ui.IconButton(MaterialIconKind.Import, "Import Pine source for local review before any assistant handoff", () => _ = ImportPineAsync(), 17);
        var rename = Ui.IconButton(MaterialIconKind.RenameBox, "Rename this file without changing its C# type or saved identities", () => _ = RenameAsync(), 17);
        var delete = Ui.IconButton(MaterialIconKind.DeleteOutline, "Delete this source file", () => _ = DeleteAsync(), 17);
        var reload = Ui.IconButton(MaterialIconKind.Refresh, "Reload the selected file from disk", () => _ = ReloadAsync(), 17);
        var head = Ui.V(8, Ui.H3("Files"), Ui.H(3, pine, rename, delete, reload, folder, add));
        head.Margin = new Thickness(0, 0, 0, 10);
        _files.SelectionChanged += (_, _) =>
        {
            if (_selectingFile || _files.SelectedItem is not ListBoxItem { Tag: string path }) return;
            if (Open(path)) return;
            _selectingFile = true;
            _files.SelectedItem = _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag! == _path);
            _selectingFile = false;
        };
        var left = new DockPanel { Width = 290, Margin = new Thickness(14), IsVisible = _builtIn == null };
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        var note = new TextBlock
        {
            Text = "Saving recompiles custom C# with the current Trading Hub compiler. Renaming a file never renames its types, series IDs, saved chart references or templates. Pine imports stay local text until you explicitly send the reviewed source.",
            Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(note, Dock.Bottom);
        left.Children.Add(note);
        left.Children.Add(Ui.Card(_files, 6));
        var save = Ui.Button("Save and compile", MaterialIconKind.Hammer, SaveAndCompile, "primary");
        save.IsVisible = _builtIn == null;
        ToolTip.SetTip(save, Keys.Label("S"));
        var copy = Ui.Button("Make editable copy", MaterialIconKind.ContentCopy, () => _ = CopyBuiltInAsync(), "primary");
        copy.IsVisible = _builtIn != null;
        var bar = new DockPanel { Margin = new Thickness(16, 10) };
        var right = Ui.H(10, new Border { Child = Ui.H(7, _dot, _state), Classes = { "pill" } }, save, copy);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(_title);
        var top = new Border { Child = bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.Bg1 };
        var main = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        main.Children.Add(_editor);
        var root = new DockPanel();
        var sidebar = new Border { Child = left, IsVisible = _builtIn == null, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) };
        DockPanel.SetDock(sidebar, Dock.Left);
        root.Children.Add(sidebar);
        root.Children.Add(main);
        return root;
    }

    private void FillFiles(string? select = null)
    {
        _files.Items.Clear();
        foreach (string file in _sources.Paths())
        {
            var row = Ui.H(8, Ui.Icon(MaterialIconKind.LanguageCsharp, 16, Ui.Accent), new TextBlock { Text = Path.GetRelativePath(_sources.Folder, file), TextTrimming = TextTrimming.CharacterEllipsis });
            _files.Items.Add(new ListBoxItem { Content = row, Tag = file });
        }
        _files.SelectedItem = _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag! == (select ?? _path)) ?? _files.Items.OfType<ListBoxItem>().FirstOrDefault();
    }

    private bool Open(string path)
    {
        if (_path == path) return true;
        if (_editor.IsDirty && !Save()) return false;
        try
        {
            string text = _sources.Read(path);
            _path = path;
            _loadedText = text;
            _editor.Text = text;
            _editor.IsReadOnly = false;
            _title.Text = Path.GetFileName(path);
            if (_svc.Scripts?.LastResult is { } last) _editor.ShowDiagnostics(last.Diagnostics, path);
            return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }

    private bool Save()
    {
        if (_path == null || _builtIn != null) return true;
        try
        {
            if (_editor.IsDirty) _sources.Save(_path, _loadedText, _editor.Text);
            _loadedText = _editor.Text;
            _editor.IsDirty = false;
            _title.Text = Path.GetFileName(_path);
            return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }

    private void SaveAndCompile()
    {
        if (_builtIn != null || !Save()) return;
        _dot.Fill = Ui.Warn;
        _state.Text = "Compiling…";
        _svc.Scripts?.CompileAll();
    }

    private void OnCompiled(TradingHub.Scripting.ScriptCompileResult result)
    {
        int errors = result.Diagnostics.Count(d => d.Severity == "error");
        _dot.Fill = result.Success ? Ui.Up : Ui.Down;
        _state.Text = result.Success ? $"Compiled · {result.Elapsed.TotalMilliseconds:0} ms" : $"{errors} error(s) · last good catalog retained";
        if (_path != null) _editor.ShowDiagnostics(result.Diagnostics, _path);
    }

    private void Error(Exception error) { _dot.Fill = Ui.Down; _state.Text = error.Message; }

    private async Task NewAsync()
    {
        var name = await PromptDialog.ShowAsync(this, "New indicator", "C# class and file name", "MyIndicator", "Create");
        if (name == null) return;
        try
        {
            if (!SyntaxFacts.IsValidIdentifier(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None) throw new ArgumentException("Choose a valid C# class name, not a keyword.");
            if (_editor.IsDirty && !Save()) return;
            string path = _sources.Create(name, IndicatorTemplate(name, name));
            FillFiles(path);
            SaveAndCompile();
        }
        catch (Exception ex) { Error(ex); }
    }

    private async Task RenameAsync()
    {
        if (_path == null || _builtIn != null) return;
        string path = _path;
        var name = await PromptDialog.ShowAsync(this, "Rename indicator source file", "Filename only; C# types and saved identities stay unchanged", Path.GetFileNameWithoutExtension(path), "Rename file");
        if (name == null || path != _path || !Save()) return;
        try
        {
            string renamed = _sources.Rename(path, name, _loadedText);
            _path = null;
            FillFiles(renamed);
            _svc.Scripts?.CompileAll();
        }
        catch (Exception ex) { Error(ex); }
    }

    private async Task DeleteAsync()
    {
        if (_path == null || _builtIn != null) return;
        string path = _path;
        if (!await ConfirmDialog.ShowAsync(this, "Delete indicator source file",
                $"Deletes only {Path.GetFileName(path)}. Other sources and saved chart/template identities are not rewritten. Dependent sources may stop compiling and saved indicators may no longer reload. This cannot be undone.",
                "Delete source file", danger: true)) return;
        try
        {
            _sources.Delete(path, _loadedText);
            _editor.Text = "";
            _editor.IsReadOnly = true;
            _path = null;
            _loadedText = "";
            _title.Text = "";
            FillFiles();
            _svc.Scripts?.CompileAll();
        }
        catch (Exception ex) { Error(ex); }
    }

    private async Task ReloadAsync()
    {
        if (_path == null) return;
        if (_editor.IsDirty && !await ConfirmDialog.ShowAsync(this, "Reload source", "Discard unsaved editor changes and load the current file from disk?", "Reload", danger: true)) return;
        string path = _path;
        _editor.IsDirty = false;
        _path = null;
        if (!Open(path)) _path = path;
    }

    private async Task CopyBuiltInAsync()
    {
        if (_builtIn == null) return;
        var name = await PromptDialog.ShowAsync(this, "Make editable indicator copy", "New C# class name", _builtIn.Name + "Custom", "Copy complete source");
        if (name == null) return;
        try
        {
            _state.Text = "Checking source and its complete dependency closure…";
            var copy = await Task.Run(() => IndicatorSources.ForAssembly(_builtIn.Assembly).Fork(_builtIn.FullName!, name, _sources.Folder));
            _svc.Scripts?.CompileAll();
            _state.Text = $"Created separate editable copy · {copy.Files.Count} source file(s)";
            new CodeEditorWindow(_svc, CodeKind.Indicator, open: copy.EntryPath).Show(this);
        }
        catch (Exception ex) { Error(ex); }
    }

    private async Task ImportPineAsync()
    {
        try
        {
            if (await PineImports.PickAsync(this, _sources.Folder) is not { } source) return;
            if (_svc.ReviewPineSource is { } review) review(source);
            else _state.Text = $"Pine source saved locally at {source.Path}. Open Backtesting → Import Pine to review a handoff. Nothing was sent.";
        }
        catch (Exception ex) { Error(ex); }
    }

    private static string IndicatorTemplate(string cls, string display) => $$"""
        using System.ComponentModel;
        using System.ComponentModel.DataAnnotations;
        using TradingHub.Indicators;
        using TradingHub.Indicators.Drawing;

        [DisplayName("{{display}}")]
        [Category("My indicators")]
        public class {{cls}} : Indicator
        {
            private readonly ValueDataSeries _series = new("Series", "Value") { Color = System.Drawing.Color.Orange, Width = 2 };

            [Display(Name = "Period", GroupName = "Settings")]
            [Range(1, 1000)]
            public int Period { get; set; } = 20;

            public {{cls}}()
            {
                DataSeries[0] = _series;
            }

            protected override void OnCalculate(int bar, decimal value)
            {
                // Example: simple moving average of closing prices.
                if (bar < Period) return;
                decimal sum = 0;
                for (int i = 0; i < Period; i++) sum += GetCandle(bar - i).Close;
                _series[bar] = sum / Period;
            }
        }
        """;
}
