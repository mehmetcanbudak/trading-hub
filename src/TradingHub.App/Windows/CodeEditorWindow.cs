using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;

namespace TradingHub.App.Windows;

internal enum CodeKind
{
    Indicator,
}

/// <summary>
/// Editor de indicadores propios (C#, API compatible con ATAS). Al guardar se recompilan solos y
/// aparecen en la biblioteca de indicadores de cada gráfico.
/// </summary>
internal sealed class CodeEditorWindow : Window
{
    private readonly AppServices _svc;
    private readonly ListBox _files = new() { Background = Brushes.Transparent };
    private readonly CodeEditor _editor = new();
    private readonly TextBlock _title = new() { Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 8, Height = 8 };
    private readonly TextBlock _state = new() { FontSize = 12 };
    private string? _path;

    public CodeEditorWindow(AppServices svc, CodeKind kind)
    {
        _svc = svc;
        Title = "Custom indicator editor";
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
        if (_svc.Scripts != null) _svc.Scripts.Compiled += OnCompiled;
        Closed += (_, _) =>
        {
            if (_svc.Scripts != null) _svc.Scripts.Compiled -= OnCompiled;
            if (_editor.IsDirty) Save();
        };
        FillFiles();
        if (_svc.Scripts?.LastResult is { } last) OnCompiled(last);
    }

    private Control Build()
    {
        var add = Ui.Button("New", MaterialIconKind.Plus, () => _ = NewAsync());
        var folder = Ui.IconButton(MaterialIconKind.FolderOpenOutline, "Open folder in Finder", () => Shell.OpenFolder(AppPaths.UserIndicators));
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var acts = Ui.H(4, folder, add);
        DockPanel.SetDock(acts, Dock.Right);
        head.Children.Add(acts);
        head.Children.Add(Ui.H3("Files"));
        _files.SelectionChanged += (_, _) =>
        {
            if (_files.SelectedItem is ListBoxItem { Tag: string p }) Open(p);
        };
        var left = new DockPanel { Width = 250, Margin = new Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        var note = new TextBlock
        {
            Text = "Trading Hub indicator API (Indicator, OnCalculate, DataSeries, OnRender…); code written for compatible platforms is adapted automatically. Saving recompiles it and makes it available in Indicators.",
            Classes = { "faint", "small" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(note, Dock.Bottom);
        left.Children.Add(note);
        left.Children.Add(Ui.Card(_files, 6));

        var save = Ui.Button("Save and compile", MaterialIconKind.Hammer, SaveAndCompile, "primary");
        ToolTip.SetTip(save, "⌘S");
        var bar = new DockPanel { Margin = new Thickness(16, 10) };
        var right = Ui.H(10, new Border { Child = Ui.H(7, _dot, _state), Classes = { "pill" } }, save);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(_title);
        var top = new Border { Child = bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.Bg1 };
        var main = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        main.Children.Add(_editor);

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(new Border { Child = left, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) }.Also(b => DockPanel.SetDock(b, Dock.Left)));
        root.Children.Add(main);
        return root;
    }

    private void FillFiles(string? select = null)
    {
        _files.Items.Clear();
        foreach (var f in Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")).OrderBy(f => f))
        {
            var row = Ui.H(8, Ui.Icon(MaterialIconKind.LanguageCsharp, 16, Ui.Accent), new TextBlock { Text = Path.GetRelativePath(AppPaths.UserIndicators, f), TextTrimming = TextTrimming.CharacterEllipsis });
            _files.Items.Add(new ListBoxItem { Content = row, Tag = f });
        }

        _files.SelectedItem = _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag! == (select ?? _path)) ?? _files.Items.OfType<ListBoxItem>().FirstOrDefault();
    }

    private void Open(string path)
    {
        if (_path == path) return;
        if (_editor.IsDirty) Save();
        _path = path;
        _editor.Text = File.ReadAllText(path);
        _title.Text = Path.GetFileName(path);
        if (_svc.Scripts?.LastResult is { } last) _editor.ShowDiagnostics(last.Diagnostics, path);
    }

    private void Save()
    {
        if (_path == null) return;
        File.WriteAllText(_path, _editor.Text);
        _editor.IsDirty = false;
        _title.Text = Path.GetFileName(_path);
    }

    private void SaveAndCompile()
    {
        Save();
        _dot.Fill = Ui.Warn;
        _state.Text = "Compiling…";
        _svc.Scripts?.CompileAll();
    }

    private void OnCompiled(TradingHub.Scripting.ScriptCompileResult r)
    {
        int errors = r.Diagnostics.Count(d => d.Severity == "error");
        _dot.Fill = r.Success ? Ui.Up : Ui.Down;
        _state.Text = r.Success ? $"Compiled · {r.Elapsed.TotalMilliseconds:0} ms" : $"{errors} error(s)";
        if (_path != null) _editor.ShowDiagnostics(r.Diagnostics, _path);
    }

    private async Task NewAsync()
    {
        var name = await PromptDialog.ShowAsync(this, "New indicator", "Class name", "MyIndicator", "Create");
        if (name == null) return;
        string cls = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (cls.Length == 0 || char.IsDigit(cls[0])) cls = "Indicator" + cls;
        var path = Path.Combine(AppPaths.UserIndicators, cls + ".cs");
        if (!File.Exists(path)) File.WriteAllText(path, IndicatorTemplate(cls, name));
        FillFiles(path);
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
