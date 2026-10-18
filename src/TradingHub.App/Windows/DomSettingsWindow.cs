using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.Charting.Dom;

namespace TradingHub.App.Windows;

/// <summary>Configuración completa del DOM: columnas (cuáles, orden y ancho), tamaños, comportamiento y colores.</summary>
internal sealed class DomSettingsWindow : Window
{
    private readonly DomSettings _settings;
    private readonly Action _changed;
    private readonly StackPanel _columns = new() { Spacing = 6 };
    private readonly PropertyGrid _grid = new();

    public DomSettingsWindow(DomSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        Title = "DOM settings";
        Width = 860;
        Height = 640;
        MinWidth = 720;
        MinHeight = 480;
        Background = Ui.Bg0;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Window;

        _grid.Target = _settings;
        _grid.Changed += _changed;
        Content = Build();
        FillColumns();
    }

    private Control Build()
    {
        var left = Ui.V(12,
            Ui.H2("Columns"),
            Ui.Faint("Select visible columns, reorder them with the arrows and adjust relative widths.").Also(t => t.TextWrapping = Avalonia.Media.TextWrapping.Wrap),
            new ScrollViewer { Content = _columns, MaxHeight = 460 });

        var right = Ui.V(12, Ui.H2("Appearance and behavior"), _grid);
        _grid.Height = 470;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*"), Margin = new Thickness(20, 18) };
        grid.Children.Add(left);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var reset = Ui.Button("Reset", MaterialIconKind.Restore, () =>
        {
            var d = new DomSettings();
            _settings.Columns = d.Columns;
            foreach (var p in typeof(DomSettings).GetProperties().Where(p => p.CanWrite && p.Name != nameof(DomSettings.Columns))) p.SetValue(_settings, p.GetValue(d));
            _grid.Rebuild();
            FillColumns();
            _changed();
        });
        var close = Ui.Button("Done", MaterialIconKind.Check, Close, "primary");
        var buttons = Ui.H(10, reset, close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(20, 0, 20, 18);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(grid);
        return root;
    }

    private void FillColumns()
    {
        _columns.Children.Clear();
        var cols = _settings.Columns;
        for (int i = 0; i < cols.Count; i++)
        {
            var c = cols[i];
            int index = i;
            var check = new CheckBox { IsChecked = c.Visible, Content = Ui.EnumName(c.Column), VerticalAlignment = VerticalAlignment.Center };
            check.IsCheckedChanged += (_, _) =>
            {
                c.Visible = check.IsChecked == true;
                if (!cols.Any(x => x.Visible))
                {
                    // siempre queda al menos una columna
                    c.Visible = true;
                    check.IsChecked = true;
                    return;
                }

                _changed();
            };
            var width = new NumericUpDown { Minimum = 0.3m, Maximum = 4m, Increment = 0.1m, FormatString = "0.0", Value = (decimal)c.Width, Width = 136 };
            ToolTip.SetTip(width, "Relative width (1 = normal)");
            width.ValueChanged += (_, _) =>
            {
                c.Width = (double)(width.Value ?? 1);
                _changed();
            };
            var up = Ui.IconButton(MaterialIconKind.ChevronUp, "Move up (further left)", () => Move(index, -1), 16);
            var down = Ui.IconButton(MaterialIconKind.ChevronDown, "Move down (further right)", () => Move(index, 1), 16);
            up.IsEnabled = i > 0;
            down.IsEnabled = i < cols.Count - 1;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
            row.Children.Add(check);
            Grid.SetColumn(width, 1);
            row.Children.Add(width);
            Grid.SetColumn(up, 2);
            row.Children.Add(up);
            Grid.SetColumn(down, 3);
            row.Children.Add(down);
            _columns.Children.Add(new Border { Child = row, Padding = new Thickness(10, 6), Background = Ui.Bg1, CornerRadius = new CornerRadius(8) });
        }
    }

    private void Move(int index, int delta)
    {
        var cols = _settings.Columns;
        int j = index + delta;
        if (j < 0 || j >= cols.Count) return;
        (cols[index], cols[j]) = (cols[j], cols[index]);
        FillColumns();
        _changed();
    }
}
