using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace TradingHub.App.Controls;

/// <summary>
/// Visor simple de Markdown para los reportes: títulos, citas, listas, tablas, bloques de código,
/// separadores y negrita / cursiva / código en línea.
/// </summary>
public sealed class MarkdownView : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 8 };
    private static readonly Regex Inline = new(@"(\*\*[^*]+\*\*|`[^`]+`|\*[^*\s][^*]*\*)", RegexOptions.Compiled);
    private static readonly Regex ImageLine = new(@"^!\[([^\]]*)\]\(([^)]+)\)$", RegexOptions.Compiled);

    // colores (modo app oscuro o modo «hoja» claro, como un documento)
    private IBrush _text = Ui.Text0, _muted = Ui.Text1, _line = Ui.Line, _code = Ui.Bg2, _header = Ui.Bg3, _alt = Ui.Bg2, _quote = Ui.Bg2, _accent = Ui.Accent;

    public MarkdownView()
    {
        Content = _root;
    }

    /// <summary>Carpeta del archivo (para las imágenes con ruta relativa).</summary>
    public string? BasePath { get; set; }

    /// <summary>Estilo documento: fondo claro y texto oscuro (para leer e imprimir reportes).</summary>
    public bool Paper
    {
        set
        {
            if (value)
            {
                _text = new SolidColorBrush(Color.FromRgb(34, 38, 46));
                _muted = new SolidColorBrush(Color.FromRgb(88, 95, 110));
                _line = new SolidColorBrush(Color.FromRgb(227, 230, 236));
                _code = new SolidColorBrush(Color.FromRgb(243, 244, 246));
                _header = new SolidColorBrush(Color.FromRgb(238, 241, 246));
                _alt = new SolidColorBrush(Color.FromRgb(249, 250, 252));
                _quote = new SolidColorBrush(Color.FromRgb(255, 248, 230));
                _accent = new SolidColorBrush(Color.FromRgb(214, 150, 20));
            }
            else
            {
                _text = Ui.Text0;
                _muted = Ui.Text1;
                _line = Ui.Line;
                _code = Ui.Bg2;
                _header = Ui.Bg3;
                _alt = Ui.Bg2;
                _quote = Ui.Bg2;
                _accent = Ui.Accent;
            }
        }
    }

    public string Markdown
    {
        set => Render(value ?? "");
    }

    private void Render(string md)
    {
        _root.Children.Clear();
        var lines = md.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0) continue;

            var img = ImageLine.Match(t);
            if (img.Success)
            {
                var src = Uri.UnescapeDataString(img.Groups[2].Value.Trim('<', '>'));
                var full = Path.IsPathRooted(src) ? src : Path.Combine(BasePath ?? "", src);
                try
                {
                    if (File.Exists(full))
                    {
                        _root.Children.Add(new Image
                        {
                            Source = new Avalonia.Media.Imaging.Bitmap(full),
                            Stretch = Stretch.Uniform,
                            MaxWidth = 1000,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Margin = new Thickness(0, 6, 0, 10),
                        });
                        continue;
                    }
                }
                catch
                {
                    // imagen dañada: se muestra el texto alternativo
                }

                _root.Children.Add(Text($"[{img.Groups[1].Value}]", 12, FontWeight.Normal));
                continue;
            }

            if (t.StartsWith("```"))
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) code.Add(lines[i]);
                _root.Children.Add(new Border
                {
                    Child = new SelectableTextBlock { Text = string.Join('\n', code), FontFamily = Ui.MonoFont, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    Background = _code,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10),
                });
                continue;
            }

            if (t.StartsWith('|'))
            {
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(lines[i].Trim());
                i--;
                _root.Children.Add(Table(rows));
                continue;
            }

            if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                _root.Children.Add(new Border { Height = 1, Background = _line, Margin = new Thickness(0, 6) });
                continue;
            }

            if (t.StartsWith("#"))
            {
                int level = t.TakeWhile(c => c == '#').Count();
                var text = t[level..].Trim();
                var tb = Text(text, level switch { 1 => 22, 2 => 17, 3 => 14.5, _ => 13.5 }, level <= 2 ? FontWeight.Bold : FontWeight.SemiBold);
                tb.Margin = new Thickness(0, level == 1 ? 0 : 10, 0, 2);
                _root.Children.Add(tb);
                continue;
            }

            if (t.StartsWith('>'))
            {
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('>'); i++) quote.Add(lines[i].Trim().TrimStart('>').Trim());
                i--;
                var tb = Text(string.Join(" ", quote), 13, FontWeight.Normal);
                tb.Foreground = _muted;
                _root.Children.Add(new Border { Child = tb, BorderBrush = _accent, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 6), Background = _quote, Margin = new Thickness(0, 2) });
                continue;
            }

            var bullet = Regex.Match(t, @"^([-*+]|\d+[.)])\s+(.*)$");
            if (bullet.Success)
            {
                string mark = char.IsDigit(bullet.Groups[1].Value[0]) ? bullet.Groups[1].Value : "•";
                int indent = line.TakeWhile(c => c == ' ').Count() / 2;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(indent * 18, 0, 0, 0) };
                var m = new TextBlock { Text = mark, Width = 22, Foreground = _accent, FontSize = 13.5 };
                var body = Text(bullet.Groups[2].Value, 13.5, FontWeight.Normal);
                Grid.SetColumn(body, 1);
                row.Children.Add(m);
                row.Children.Add(body);
                _root.Children.Add(row);
                continue;
            }

            // párrafo: junta las líneas seguidas
            var para = new List<string> { t };
            while (i + 1 < lines.Length && lines[i + 1].Trim() is { Length: > 0 } next && !IsBlockStart(next)) para.Add(lines[++i].Trim());
            _root.Children.Add(Text(string.Join(" ", para), 13.5, FontWeight.Normal));
        }
    }

    private static bool IsBlockStart(string t) =>
        t.StartsWith('#') || t.StartsWith('|') || t.StartsWith('>') || t.StartsWith("```") || t.StartsWith("![") || Regex.IsMatch(t, @"^([-*+]|\d+[.)])\s+") || Regex.IsMatch(t, @"^(-{3,}|\*{3,})$");

    /// <summary>Texto con negrita, cursiva y código en línea.</summary>
    private SelectableTextBlock Text(string text, double size, FontWeight weight)
    {
        var tb = new SelectableTextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Foreground = _text };
        int pos = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > pos) tb.Inlines!.Add(new Run(text[pos..m.Index]));
            var v = m.Value;
            if (v.StartsWith("**")) tb.Inlines!.Add(new Run(v[2..^2]) { FontWeight = FontWeight.Bold });
            else if (v.StartsWith('`')) tb.Inlines!.Add(new Run(v[1..^1]) { FontFamily = Ui.MonoFont, Background = _code });
            else tb.Inlines!.Add(new Run(v[1..^1]) { FontStyle = FontStyle.Italic });
            pos = m.Index + m.Length;
        }

        if (pos < text.Length) tb.Inlines!.Add(new Run(text[pos..]));
        return tb;
    }

    private Control Table(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var data = rows.Where(r => !Regex.IsMatch(r, @"^\|?\s*:?-{2,}")).Select(Cells).ToList();
        if (data.Count == 0) return new Panel();
        int cols = data.Max(r => r.Length);
        var grid = new Grid();
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r < data.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cols; c++)
            {
                string cell = c < data[r].Length ? data[r][c] : "";
                var tb = Text(cell.Replace("**", ""), 12.5, r == 0 ? FontWeight.SemiBold : FontWeight.Normal);
                tb.TextWrapping = TextWrapping.NoWrap;
                bool numeric = Regex.IsMatch(cell, @"^[-+$%\d.,\s]+%?$");
                tb.HorizontalAlignment = numeric && r > 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                var border = new Border
                {
                    Child = tb,
                    Padding = new Thickness(10, 5),
                    Background = r == 0 ? _header : r % 2 == 0 ? _alt : Brushes.Transparent,
                    BorderBrush = _line,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return new ScrollViewer
        {
            Content = new Border { Child = grid, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = _line, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }
}
