using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Layout;
using TradingHub.Strategies;

namespace TradingHub.App.Controls;

/// <summary>Editores de los parámetros de una estrategia (número, sí/no, opciones o texto), con su descripción de ayuda.</summary>
internal sealed class ParameterEditor : StackPanel
{
    private readonly List<(PropertyInfo Prop, Func<object?> Read, Control Editor)> _editors = new();
    private Type? _type;

    public ParameterEditor() => Spacing = 8;

    /// <summary>Hay algún parámetro.</summary>
    public bool HasParameters => _editors.Count > 0;

    /// <summary>Arma los editores con los valores dados (los que falten, los de la clase).</summary>
    public void Load(Type? type, IReadOnlyDictionary<string, object?> values, bool readOnly = false, IReadOnlyCollection<string>? hidden = null)
    {
        Children.Clear();
        _editors.Clear();
        _type = type;
        if (type == null)
        {
            Children.Add(new TextBlock { Text = "Strategy is not compiled: open it in Backtesting.", Classes = { "faint", "small" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            return;
        }

        var current = (Strategy)Activator.CreateInstance(type)!;
        current.SetParameters(values);
        foreach (var p in Strategy.Parameters(type))
        {
            // los que se editan aparte (por ejemplo, la condición de mercado) quedan como están
            if (hidden != null && hidden.Contains(p.Name)) continue;
            object? value = p.GetValue(current);
            Control editor;
            Func<object?> read;
            var t = p.PropertyType;
            if (t == typeof(bool))
            {
                var cb = new CheckBox { IsChecked = value is true };
                editor = cb;
                read = () => cb.IsChecked == true;
            }
            else if (t.IsEnum)
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var v in Enum.GetValues(t)) combo.Items.Add(new ComboBoxItem { Content = Controls.PropertyGrid.EnumDisplay(v), Tag = v });
                var selected = value is string str && Enum.TryParse(t, str, out var parsed) ? parsed : value;
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
                editor = combo;
                read = () => combo.SelectedItem is ComboBoxItem { Tag: { } tag } ? tag : value;
            }
            else if (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal) || t == typeof(float))
            {
                bool whole = t == typeof(int) || t == typeof(long);
                var num = new NumericUpDown
                {
                    Value = Convert.ToDecimal(value ?? 0, CultureInfo.InvariantCulture),
                    Increment = whole ? 1 : 0.1m,
                    FormatString = whole ? "0" : "0.####",
                    NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Minimum = decimal.MinValue,
                    Maximum = decimal.MaxValue,
                };
                editor = num;
                read = () => Convert.ChangeType(num.Value ?? 0, t, CultureInfo.InvariantCulture);
            }
            else
            {
                var tb = new TextBox { Text = value?.ToString() ?? "" };
                editor = tb;
                read = () => tb.Text;
            }

            editor.IsEnabled = !readOnly;
            var attr = p.GetCustomAttribute<ParameterAttribute>();
            if (!string.IsNullOrWhiteSpace(attr?.Description)) ToolTip.SetTip(editor, attr.Description);
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            g.Children.Add(new TextBlock { Text = Strategy.ParameterName(p), VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            Grid.SetColumn(editor, 1);
            g.Children.Add(editor);
            Children.Add(g);
            _editors.Add((p, read, editor));
        }

        if (_editors.Count == 0) Children.Add(new TextBlock { Text = "This strategy has no parameters.", Classes = { "faint", "small" } });
    }

    /// <summary>Los valores elegidos (los inválidos quedan como estaban).</summary>
    public Dictionary<string, object?> Read(IReadOnlyDictionary<string, object?> current)
    {
        if (_type == null) return new Dictionary<string, object?>(current);
        var s = (Strategy)Activator.CreateInstance(_type)!;
        s.SetParameters(current);
        foreach (var (prop, read, _) in _editors)
        {
            try
            {
                prop.SetValue(s, read());
            }
            catch (Exception)
            {
                // valor inválido: queda el anterior
            }
        }

        return s.GetParameters();
    }
}
