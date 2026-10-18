using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using Material.Icons;
using TextMateSharp.Grammars;
using TradingHub.Scripting;

namespace TradingHub.App.Controls;

/// <summary>Editor de C# con resaltado, números de línea y lista de errores de compilación.</summary>
public sealed class CodeEditor : UserControl
{
    private readonly TextEditor _editor;
    private readonly ListBox _errors = new() { MaxHeight = 170, Background = Brushes.Transparent };
    private readonly Border _errorsPanel;
    private readonly TextBlock _errorsTitle = new() { Classes = { "h3" } };
    private bool _loading;

    public CodeEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Menlo, SF Mono, Consolas, monospace"),
            FontSize = 13,
            Background = Ui.Bg0,
            Foreground = Ui.Text0,
            LineNumbersForeground = Ui.Text2,
            Padding = new Thickness(6, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.EnableRectangularSelection = true;
        _editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse("#0FFFFFFF"));
        _editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);
        _editor.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#553D8BFD"));
        try
        {
            var options = new RegistryOptions(ThemeName.DarkPlus);
            var tm = _editor.InstallTextMate(options);
            tm.SetGrammar(options.GetScopeByLanguageId(options.GetLanguageByExtension(".cs").Id));
        }
        catch
        {
            // sin resaltado si falta la gramática
        }

        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            IsDirty = true;
            Changed?.Invoke();
        };
        _editor.KeyDown += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.S)
            {
                SaveRequested?.Invoke();
                e.Handled = true;
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.B)
            {
                CompileRequested?.Invoke();
                e.Handled = true;
            }
        };

        _errors.SelectionChanged += (_, _) =>
        {
            if (_errors.SelectedItem is ListBoxItem { Tag: ScriptDiagnostic d }) GoTo(d.Line, d.Column);
        };
        var head = Ui.H(8, Ui.Icon(MaterialIconKind.AlertCircleOutline, 15, Ui.Down), _errorsTitle);
        head.Margin = new Thickness(12, 8, 0, 4);
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(_errors);
        _errorsPanel = new Border { Child = dock, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), IsVisible = false };

        var root = new DockPanel();
        DockPanel.SetDock(_errorsPanel, Dock.Bottom);
        root.Children.Add(_errorsPanel);
        root.Children.Add(_editor);
        Content = root;
    }

    /// <summary>Cambió el texto.</summary>
    public event Action? Changed;

    /// <summary>⌘S dentro del editor.</summary>
    public event Action? SaveRequested;

    /// <summary>⌘B dentro del editor.</summary>
    public event Action? CompileRequested;

    public bool IsDirty { get; set; }

    public string Text
    {
        get => _editor.Text;
        set
        {
            _loading = true;
            _editor.Text = value;
            _editor.CaretOffset = 0;
            _loading = false;
            IsDirty = false;
        }
    }

    public bool IsReadOnly
    {
        get => _editor.IsReadOnly;
        set => _editor.IsReadOnly = value;
    }

    public void GoTo(int line, int column)
    {
        if (line < 1 || line > _editor.Document.LineCount) return;
        var l = _editor.Document.GetLineByNumber(line);
        _editor.CaretOffset = Math.Min(l.Offset + Math.Max(0, column - 1), l.EndOffset);
        _editor.TextArea.Caret.BringCaretToView();
        _editor.Focus();
    }

    /// <summary>Muestra los errores y advertencias (solo los del archivo indicado, si se pasa).</summary>
    public void ShowDiagnostics(IEnumerable<ScriptDiagnostic> diagnostics, string? file = null)
    {
        var list = diagnostics.Where(d => file == null || string.IsNullOrEmpty(d.File) || Path.GetFullPath(d.File) == Path.GetFullPath(file) || d.File.EndsWith("GlobalUsings.g.cs")).ToList();
        _errors.Items.Clear();
        foreach (var d in list.OrderBy(d => d.Severity == "error" ? 0 : 1).ThenBy(d => d.Line))
        {
            bool error = d.Severity == "error";
            var row = Ui.H(8,
                Ui.Icon(error ? MaterialIconKind.CloseCircle : MaterialIconKind.AlertOutline, 14, error ? Ui.Down : Ui.Warn),
                new TextBlock { Text = $"Line {d.Line}", Classes = { "faint", "small" }, Width = 64 },
                new TextBlock { Text = $"{d.Message}  ({d.Id})", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
            _errors.Items.Add(new ListBoxItem { Content = row, Tag = d, Padding = new Thickness(12, 4) });
        }

        int errors = list.Count(d => d.Severity == "error");
        _errorsTitle.Text = errors > 0 ? $"{errors} error{(errors == 1 ? "" : "s")}" + (list.Count > errors ? $" · {list.Count - errors} warning(s)" : "") : $"{list.Count} warning(s)";
        _errorsPanel.IsVisible = list.Count > 0;
    }

    public void ClearDiagnostics()
    {
        _errors.Items.Clear();
        _errorsPanel.IsVisible = false;
    }
}
