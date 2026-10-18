using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TradingHub.Attributes;
using TradingHub.Attributes.Editors;
using TradingHub.Indicators;
using TradingHub.Rendering.Settings;
using DColor = System.Drawing.Color;

namespace TradingHub.App.Controls;

/// <summary>
/// Editor de propiedades genérico: arma los controles a partir de los atributos [Display], [Range],
/// [Browsable], [Tab] y [VisibleWhen] (los mismos que usan los indicadores de ATAS).
/// Los cambios se aplican en el momento.
/// </summary>
public sealed class PropertyGrid : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 2 };
    private object? _target;
    private readonly List<(PropertyInfo Prop, Control Row, VisibleWhenAttribute[] Conditions, object Owner)> _rows = new();

    /// <summary>Títulos de grupo con sus filas: un título sin filas visibles se oculta.</summary>
    private readonly List<(TextBlock Header, List<Control> Rows)> _headers = new();

    public PropertyGrid()
    {
        Content = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>Se dispara después de cada cambio.</summary>
    public event Action? Changed;

    /// <summary>Propiedades a omitir (por nombre).</summary>
    public HashSet<string> Exclude { get; } = new();

    public object? Target
    {
        get => _target;
        set
        {
            _target = value;
            Rebuild();
        }
    }

    public void Rebuild()
    {
        _root.Children.Clear();
        _rows.Clear();
        _headers.Clear();
        if (_target == null) return;

        var props = EditableProperties(_target.GetType()).Where(p => !Exclude.Contains(p.Name)).ToList();
        var groups = props
            .GroupBy(p => (Tab: p.GetCustomAttribute<TabAttribute>()?.GetTabName() ?? "", Group: GroupOf(p)))
            .OrderBy(g => g.Key.Tab == "" ? 0 : 1).ThenBy(g => g.Min(p => OrderOf(p)));

        string? lastTab = null;
        foreach (var g in groups)
        {
            if (g.Key.Tab != lastTab && g.Key.Tab != "")
            {
                _root.Children.Add(new TextBlock { Text = g.Key.Tab.ToUpperInvariant(), FontWeight = FontWeight.Bold, Foreground = Ui.Accent, Margin = new Thickness(0, 12, 0, 2) });
                lastTab = g.Key.Tab;
            }

            TextBlock? header = null;
            if (!string.IsNullOrEmpty(g.Key.Group))
            {
                header = new TextBlock { Text = g.Key.Group, FontWeight = FontWeight.SemiBold, Foreground = Ui.Text1, Margin = new Thickness(0, 10, 0, 2) };
                _root.Children.Add(header);
            }

            int first = _rows.Count;
            foreach (var p in g.OrderBy(OrderOf)) AddRow(_root, _target, p, 0);
            if (header != null) _headers.Add((header, _rows.Skip(first).Select(r => r.Row).ToList()));
        }

        if (_target is Indicator ind) AddSeriesSection(ind);
        UpdateVisibility();
    }

    // ------------------------------------------------------------------ reflexión

    public static IEnumerable<PropertyInfo> EditableProperties(Type t)
    {
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false }) continue;
            if (p.DeclaringType == typeof(Indicator)) continue;
            bool complex = IsComplex(p.PropertyType);
            if (!p.CanWrite && !complex) continue;
            if (p.GetSetMethod() == null && !complex) continue;
            yield return p;
        }
    }

    private static bool IsComplex(Type t) =>
        t == typeof(PenSettings) || t == typeof(FontSetting) || t == typeof(BrushSettings) ||
        (t.IsClass && t != typeof(string) && !t.IsArray && t.Namespace?.StartsWith("TradingHub") == true && t.GetProperties().Any(x => x.CanWrite));

    private static string GroupOf(PropertyInfo p)
    {
        var d = p.GetCustomAttribute<DisplayAttribute>();
        try { return TradingHub.Common.EnglishText.Of(d?.GetGroupName() ?? p.GetCustomAttribute<CategoryAttribute>()?.Category ?? ""); }
        catch { return TradingHub.Common.EnglishText.Of(d?.GroupName ?? ""); }
    }

    private static int OrderOf(PropertyInfo p)
    {
        var d = p.GetCustomAttribute<DisplayAttribute>();
        return d?.GetOrder() ?? 10000;
    }

    public static string NameOf(PropertyInfo p)
    {
        var d = p.GetCustomAttribute<DisplayAttribute>();
        try
        {
            var n = d?.GetName();
            if (!string.IsNullOrWhiteSpace(n)) return TradingHub.Common.EnglishText.Of(n);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(d?.Name)) return TradingHub.Common.EnglishText.Of(d!.Name!);
        }

        var dn = p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
        return string.IsNullOrWhiteSpace(dn) ? Humanize(p.Name) : TradingHub.Common.EnglishText.Of(dn);
    }

    private static string? DescriptionOf(PropertyInfo p)
    {
        var d = p.GetCustomAttribute<DisplayAttribute>();
        try { return d?.GetDescription() ?? p.GetCustomAttribute<DescriptionAttribute>()?.Description; }
        catch { return d?.Description; }
    }

    private static string Humanize(string s) => System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])([A-Z])", " $1");

    public static string EnumDisplay(object value)
    {
        var f = value.GetType().GetField(value.ToString()!);
        var d = f?.GetCustomAttribute<DisplayAttribute>();
        // también [Description] (los filtros de contexto de las estrategias lo usan)
        string fallback = f?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? value.ToString()!;
        try { return d?.GetName() ?? fallback; }
        catch { return d?.Name ?? fallback; }
    }

    // ------------------------------------------------------------------ filas

    private void AddRow(Panel parent, object owner, PropertyInfo p, int depth)
    {
        Control? editor;
        try
        {
            editor = CreateEditor(owner, p, depth);
        }
        catch (Exception ex)
        {
            editor = new TextBlock { Text = "(" + ex.Message + ")", Foreground = Brushes.OrangeRed };
        }

        if (editor == null) return;
        var label = new TextBlock { Text = NameOf(p), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(depth * 12, 0, 8, 0), FontSize = 12 };
        var tip = DescriptionOf(p);
        if (!string.IsNullOrWhiteSpace(tip))
        {
            ToolTip.SetTip(label, tip);
            ToolTip.SetTip(editor, tip);
        }

        Control row;
        if (editor is Expander)
        {
            row = editor;
        }
        else
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("0.95*,1.05*"), Margin = new Thickness(0, 1) };
            g.Children.Add(label);
            Grid.SetColumn(editor, 1);
            g.Children.Add(editor);
            row = g;
        }

        parent.Children.Add(row);
        _rows.Add((p, row, p.GetCustomAttributes<VisibleWhenAttribute>().ToArray(), owner));
    }

    private void Set(object owner, PropertyInfo p, object? value)
    {
        try
        {
            p.SetValue(owner, value);
            Changed?.Invoke();
            UpdateVisibility();
        }
        catch (Exception ex)
        {
            TradingHub.Common.Logging.LoggingExtensions.LogWarn(this, $"Could not change {p.Name}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private void UpdateVisibility()
    {
        foreach (var (p, row, conds, owner) in _rows)
        {
            if (conds.Length == 0) continue;
            bool visible = conds.All(c =>
            {
                var other = owner.GetType().GetProperty(c.PropertyName)?.GetValue(owner);
                return c.Values.Length == 0 || c.Values.Any(v => Equals(v, other) || Equals(v?.ToString(), other?.ToString()));
            });
            row.IsVisible = visible;
        }

        foreach (var (header, rows) in _headers) header.IsVisible = rows.Count == 0 || rows.Any(r => r.IsVisible);
    }

    private Control? CreateEditor(object owner, PropertyInfo p, int depth)
    {
        var t = p.PropertyType;
        var nt = Nullable.GetUnderlyingType(t);
        object? value = p.GetValue(owner);
        bool ro = !p.CanWrite || p.GetSetMethod() == null;

        if (t == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = (bool)value!, IsEnabled = !ro };
            cb.IsCheckedChanged += (_, _) => Set(owner, p, cb.IsChecked == true);
            return cb;
        }

        // colores guardados como ARGB (uint): antes que los números, si no aparecían como una caja con un número
        if ((t == typeof(uint?) || t == typeof(uint)) && p.Name.EndsWith("Color", StringComparison.Ordinal))
        {
            var current = value is uint u ? DColor.FromArgb(unchecked((int)u)) : DColor.Empty;
            return ColorEditor(current, ro, c => Set(owner, p, c.IsEmpty ? null : (uint?)unchecked((uint)c.ToArgb())), allowAuto: true);
        }

        if (IsNumeric(t) || (nt != null && IsNumeric(nt)))
        {
            var range = p.GetCustomAttribute<RangeAttribute>();
            var num = new NumericUpDown
            {
                Value = value == null ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                Increment = IsInteger(nt ?? t) ? 1 : 0.1m,
                FormatString = IsInteger(nt ?? t) ? "0" : "0.########",
                IsEnabled = !ro,
                MinWidth = 110,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            if (range != null)
            {
                try
                {
                    num.Minimum = Convert.ToDecimal(range.Minimum, CultureInfo.InvariantCulture);
                    var max = Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture);
                    num.Maximum = max >= (double)decimal.MaxValue / 2 ? decimal.MaxValue : (decimal)max;
                }
                catch
                {
                    // rango inválido: sin límites
                }
            }
            else num.Minimum = decimal.MinValue;

            num.ValueChanged += (_, e) =>
            {
                if (e.NewValue == null) return;
                try { Set(owner, p, Convert.ChangeType(e.NewValue.Value, nt ?? t, CultureInfo.InvariantCulture)); }
                catch { /* fuera de rango del tipo */ }
            };
            return num;
        }

        if (t.IsEnum)
        {
            var combo = new ComboBox { IsEnabled = !ro, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var v in Enum.GetValues(t))
            {
                var item = new ComboBoxItem { Content = EnumDisplay(v), Tag = v };
                combo.Items.Add(item);
                if (Equals(v, value)) combo.SelectedItem = item;
            }

            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: { } v }) Set(owner, p, v);
            };
            return combo;
        }

        if (t == typeof(DColor) || (nt == typeof(DColor)))
            return ColorEditor(value is DColor c ? c : DColor.Empty, ro, c => Set(owner, p, c));

        if (t == typeof(string))
        {
            var tb = new TextBox { Text = value as string ?? "", IsEnabled = !ro };
            tb.LostFocus += (_, _) => Set(owner, p, tb.Text);
            tb.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) Set(owner, p, tb.Text);
            };
            return tb;
        }

        if (t == typeof(TimeSpan) || nt == typeof(TimeSpan))
        {
            var tb = new TextBox { Text = value is TimeSpan ts ? ts.ToString(@"hh\:mm\:ss") : "", Watermark = "hh:mm:ss" };
            void Apply()
            {
                if (TimeSpan.TryParse(tb.Text, CultureInfo.InvariantCulture, out var v)) Set(owner, p, v);
            }

            tb.LostFocus += (_, _) => Apply();
            tb.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) Apply();
            };
            return tb;
        }

        if (t == typeof(DateTime) || nt == typeof(DateTime))
        {
            var tb = new TextBox { Text = value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "", Watermark = "yyyy-mm-dd hh:mm" };
            tb.LostFocus += (_, _) =>
            {
                if (DateTime.TryParse(tb.Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)) Set(owner, p, v);
            };
            return tb;
        }

        // filtros: activado + valor
        if (value != null && IsFilter(t, out var valueProp))
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var enabledProp = t.GetProperty("Enabled")!;
            var cb = new CheckBox { IsChecked = (bool)enabledProp.GetValue(value)! };
            cb.IsCheckedChanged += (_, _) =>
            {
                enabledProp.SetValue(value, cb.IsChecked == true);
                Changed?.Invoke();
            };
            panel.Children.Add(cb);
            var inner = CreateEditor(value, valueProp!, depth + 1);
            if (inner != null)
            {
                inner.MinWidth = 100;
                panel.Children.Add(inner);
            }

            return panel;
        }

        // objetos anidados (lápices, fuentes, configuraciones)
        if (value != null && IsComplex(t))
        {
            var inner = new StackPanel { Spacing = 2, Margin = new Thickness(8, 2, 0, 4) };
            foreach (var cp in EditableProperties(t).OrderBy(OrderOf)) AddRow(inner, value, cp, depth + 1);
            return new Expander
            {
                Header = NameOf(p),
                Content = inner,
                IsExpanded = p.GetCustomAttribute<IsExpandedAttribute>() != null || t == typeof(PenSettings),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(depth * 12, 2, 0, 2),
            };
        }

        return null;
    }

    private static bool IsFilter(Type t, out PropertyInfo? valueProp)
    {
        valueProp = null;
        for (var c = t; c != null; c = c.BaseType)
        {
            if (c.IsGenericType && c.GetGenericTypeDefinition() == typeof(Filter<>))
            {
                valueProp = t.GetProperty("Value");
                return valueProp != null;
            }
        }

        return false;
    }

    private static bool IsNumeric(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(decimal) || t == typeof(double) || t == typeof(float) || t == typeof(short) || t == typeof(byte) || t == typeof(uint);

    private static bool IsInteger(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint);

    private Control ColorEditor(DColor current, bool readOnly, Action<DColor> apply, bool allowAuto = false)
    {
        var swatch = new Border
        {
            Width = 26,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(current.IsEmpty ? Colors.Transparent : Color.FromArgb(current.A, current.R, current.G, current.B)),
        };
        var text = new TextBlock { Text = current.IsEmpty ? "auto" : $"#{current.A:X2}{current.R:X2}{current.G:X2}{current.B:X2}", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { swatch, text } };
        var button = new Button { Content = content, IsEnabled = !readOnly, Padding = new Thickness(6, 2) };
        var view = new ColorView
        {
            Color = current.IsEmpty ? Colors.DodgerBlue : Color.FromArgb(current.A, current.R, current.G, current.B),
            IsAlphaEnabled = true,
            Width = 320,
        };
        view.ColorChanged += (_, e) =>
        {
            var c = e.NewColor;
            var dc = DColor.FromArgb(c.A, c.R, c.G, c.B);
            swatch.Background = new SolidColorBrush(c);
            text.Text = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            apply(dc);
        };
        var flyoutContent = new StackPanel { Spacing = 8, Margin = new Thickness(12), Children = { view } };
        if (allowAuto)
        {
            var auto = new Button { Content = "Automatic (theme color)" };
            auto.Click += (_, _) =>
            {
                swatch.Background = Brushes.Transparent;
                text.Text = "auto";
                apply(DColor.Empty);
            };
            flyoutContent.Children.Add(auto);
        }

        button.Flyout = new Flyout { Content = flyoutContent };
        return button;
    }

    // ------------------------------------------------------------------ series

    private void AddSeriesSection(Indicator ind)
    {
        var visibleSeries = ind.DataSeries.Where(s => !s.IsHidden).ToList();
        if (visibleSeries.Count == 0 && ind.LineSeries.Count == 0) return;
        _root.Children.Add(new TextBlock { Text = "Series (colors and style)", FontWeight = FontWeight.SemiBold, Foreground = Ui.Text1, Margin = new Thickness(0, 12, 0, 2) });
        foreach (var s in visibleSeries)
        {
            var inner = new StackPanel { Spacing = 2, Margin = new Thickness(8, 2, 0, 4) };
            foreach (var p in EditableProperties(s.GetType()).Where(p => p.Name is "Visible" or "Color" or "Width" or "VisualType" or "LineDashStyle" or "RangeColor" or "UpCandleColor" or "DownCandleColor" or "ShowCurrentValue" or "ShowZeroValue"))
                AddRow(inner, s, p, 1);
            _root.Children.Add(new Expander { Header = s.Name, Content = inner, HorizontalAlignment = HorizontalAlignment.Stretch });
        }

        foreach (var ls in ind.LineSeries)
        {
            var inner = new StackPanel { Spacing = 2, Margin = new Thickness(8, 2, 0, 4) };
            foreach (var p in EditableProperties(ls.GetType())) AddRow(inner, ls, p, 1);
            _root.Children.Add(new Expander { Header = "Level: " + ls.Name, Content = inner, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
    }
}
