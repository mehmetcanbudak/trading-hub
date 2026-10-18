using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace TradingHub.App;

/// <summary>
/// Utilidades para armar las vistas en código con los tokens del sistema de diseño
/// (Styles/TradingHubTheme.axaml). Así no hay colores ni tamaños "a mano" desparramados.
/// </summary>
internal static class Ui
{
    private static readonly Dictionary<string, IBrush> Cache = new();

    public static IBrush Brush(string key)
    {
        if (Cache.TryGetValue(key, out var b)) return b;
        if (Application.Current != null && Application.Current.TryFindResource(key, out var r) && r is IBrush brush)
        {
            Cache[key] = brush;
            return brush;
        }

        return Brushes.Magenta;
    }

    public static Color Color(string key) =>
        Application.Current != null && Application.Current.TryFindResource(key, out var r) && r is Color c ? c : Colors.Magenta;

    public static IBrush Bg0 => Brush("Bg0Brush");
    public static IBrush Bg1 => Brush("Bg1Brush");
    public static IBrush Bg2 => Brush("Bg2Brush");
    public static IBrush Bg3 => Brush("Bg3Brush");
    public static IBrush Line => Brush("LineBrush");
    public static IBrush Text0 => Brush("Text0Brush");
    public static IBrush Text1 => Brush("Text1Brush");
    public static IBrush Text2 => Brush("Text2Brush");
    public static IBrush Accent => Brush("AccentBrush");
    public static IBrush Up => Brush("UpBrush");
    public static IBrush Down => Brush("DownBrush");
    public static IBrush Warn => Brush("WarnBrush");
    public static IBrush Focus => Brush("FocusBrush");

    /// <summary>Aplica una acción y devuelve el mismo objeto (para armar vistas en una expresión).</summary>
    public static T Also<T>(this T c, Action<T> a)
    {
        a(c);
        return c;
    }

    public static T Classes<T>(this T control, params string[] classes) where T : StyledElement
    {
        foreach (var c in classes) control.Classes.Add(c);
        return control;
    }

    public static MaterialIcon Icon(MaterialIconKind kind, double size = 18, IBrush? brush = null)
    {
        var i = new MaterialIcon { Kind = kind, Width = size, Height = size };
        if (brush != null) i.Foreground = brush;
        return i;
    }

    public static TextBlock Text(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    public static TextBlock H1(string text) => Text(text, "h1");
    public static TextBlock H2(string text) => Text(text, "h2");
    public static TextBlock H3(string text) => Text(text.ToUpperInvariant(), "h3");
    public static TextBlock Muted(string text) => Text(text, "muted");
    public static TextBlock Faint(string text) => Text(text, "faint", "small");

    /// <summary>Fila horizontal; los hijos sin alineación propia quedan centrados verticalmente.</summary>
    public static StackPanel H(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children)
        {
            if (c.VerticalAlignment == VerticalAlignment.Stretch) c.VerticalAlignment = VerticalAlignment.Center;
            p.Children.Add(c);
        }

        return p;
    }

    public static StackPanel V(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static Button IconButton(MaterialIconKind kind, string tip, Action onClick, double size = 18)
    {
        var b = new Button { Content = Icon(kind, size) }.Classes("icon");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static ToggleButton IconToggle(MaterialIconKind kind, string tip, double size = 18)
    {
        var b = new ToggleButton { Content = Icon(kind, size) }.Classes("icon");
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>Botón con ícono y texto.</summary>
    public static Button Button(string text, MaterialIconKind? kind, Action onClick, params string[] classes)
    {
        object content = kind is { } k ? H(7, Icon(k, 16), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }) : text;
        var b = new Button { Content = content };
        foreach (var c in classes) b.Classes.Add(c);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// Confirmación en un globito con aire: el texto (y un título opcional), «Cancelar» y la acción, en rojo si es
    /// destructiva. Todos los «¿seguro?» de la app usan este.
    /// </summary>
    public static Flyout ConfirmFlyout(string text, string action, Action onConfirm, bool danger = true, string? title = null)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => flyout.Hide();
        var ok = new Button { Content = action, MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add(danger ? "danger" : "primary");
        ok.Click += (_, _) =>
        {
            flyout.Hide();
            onConfirm();
        };

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(16, 14, 16, 14), Width = 300 };
        if (title != null) body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Foreground = Text1 });
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } });
        flyout.Content = body;
        return flyout;
    }

    /// <summary>Etiqueta con un punto de color (estado de conexión, etc.).</summary>
    public static Border Pill(string text, IBrush dot, out TextBlock label)
    {
        label = new TextBlock { Text = text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Text1 };
        var ellipse = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = dot, VerticalAlignment = VerticalAlignment.Center };
        return new Border { Child = H(7, ellipse, label) }.Classes("pill");
    }

    /// <summary>
    /// Llena un selector de cuentas sin romperlo: rehace la lista solo si cambió (nunca con la lista abierta)
    /// y después marca la elegida. Las cuentas reales van con su color; el resto con el del tema
    /// (un Foreground nulo dejaba el texto invisible salvo al pasar el mouse).
    /// </summary>
    public static void SyncAccounts(ComboBox box, IReadOnlyList<Services.AccountOption> accounts, string selectedId, IBrush? realBrush = null)
    {
        bool same = box.Items.Count == accounts.Count
                    && box.Items.Cast<object?>().Zip(accounts).All(p => p.First is ComboBoxItem { Tag: Services.AccountOption o } && o == p.Second);
        if (!same && !box.IsDropDownOpen)
        {
            box.Items.Clear();
            foreach (var a in accounts)
            {
                var item = new ComboBoxItem { Content = a.Name, Tag = a };
                if (a.IsReal) item.Foreground = realBrush ?? Brushes.OrangeRed;
                box.Items.Add(item);
            }
        }

        var want = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is Services.AccountOption o && o.Id == selectedId);
        if (want != null && !ReferenceEquals(box.SelectedItem, want)) box.SelectedItem = want;
    }

    /// <summary>Campo de formulario: etiqueta arriba, control abajo y ayuda opcional.</summary>
    public static Control Field(string label, Control control, string? help = null)
    {
        var v = V(5, Text(label, "muted", "small"), control);
        if (!string.IsNullOrWhiteSpace(help)) v.Children.Add(new TextBlock { Text = help, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap });
        return v;
    }

    /// <summary>Fila de controles del mismo ancho con un espacio real entre ellos.</summary>
    public static Grid Row(double gap, params Control[] children)
    {
        var g = new Grid();
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(gap)));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var c = children[i];
            c.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (c is ContentControl cc) cc.HorizontalContentAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(c, g.ColumnDefinitions.Count - 1);
            g.Children.Add(c);
        }

        return g;
    }

    public static Border Card(Control content, double padding = 16) => new Border { Child = content, Padding = new Thickness(padding) }.Classes("card");

    public static Border Divider(bool vertical = false) =>
        vertical
            ? new Border { Width = 1, Margin = new Thickness(10, 6) }.Classes("divider")
            : new Border { Height = 1, Margin = new Thickness(0, 6) }.Classes("divider");

    /// <summary>Control segmentado (un grupo de opciones excluyentes).</summary>
    public sealed class Segmented<T> : Border
    {
        private readonly List<(T Value, ToggleButton Button)> _items = new();
        private bool _updating;

        public Segmented(IEnumerable<(T Value, string Label, string? Tip)> items)
        {
            Classes.Add("segment");
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
            foreach (var (value, label, tip) in items)
            {
                var b = new ToggleButton { Content = label }.Classes("seg");
                if (tip != null) ToolTip.SetTip(b, tip);
                b.Click += (_, _) =>
                {
                    if (_updating) return;
                    Select(value);
                    SelectionChanged?.Invoke(value);
                };
                _items.Add((value, b));
                panel.Children.Add(b);
            }

            Child = panel;
        }

        public event Action<T>? SelectionChanged;

        public void Select(T? value)
        {
            _updating = true;
            foreach (var (v, b) in _items) b.IsChecked = EqualityComparer<T>.Default.Equals(v, value);
            _updating = false;
        }

        /// <summary>Como un clic: marca la opción y avisa (Select solo la marca).</summary>
        public void Choose(T value)
        {
            Select(value);
            SelectionChanged?.Invoke(value);
        }
    }

    public static string EnumName<TEnum>(TEnum value) where TEnum : struct, Enum => Controls.PropertyGrid.EnumDisplay(value);

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"ago {(int)d.TotalMinutes} min";
        if (d.TotalHours < 24) return $"ago {(int)d.TotalHours} h";
        return $"ago {(int)d.TotalDays} days";
    }
}
