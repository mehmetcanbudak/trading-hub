using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Views;
using TradingHub.Charting;

namespace TradingHub.App.Windows;

/// <summary>
/// Editor del footprint: elegís qué columnas tiene cada vela (escaleras, perfiles, celdas, vela,
/// espacios), en qué orden y con qué datos. Los cambios se ven en el gráfico en el momento.
/// </summary>
public sealed class FootprintEditorWindow : Window
{
    private readonly ChartModel _model;
    private readonly TradingView _main;
    private readonly FootprintLayout _layout;
    private readonly ListBox _columns = new();
    private readonly PropertyGrid _columnGrid = new();
    private readonly PropertyGrid _layoutGrid = new();
    private readonly TextBox _name = new() { Watermark = "My footprint" };
    private readonly ComboBox _presets = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a template…" };
    private readonly TextBlock _columnTitle = new() { Classes = { "h2" } };

    internal FootprintEditorWindow(ChartModel model, TradingView main)
    {
        _model = model;
        _main = main;
        _layout = model.Settings.Layout.Clone();
        model.Settings.Layout = _layout;
        if (model.Settings.Style != ChartStyle.Footprint) model.Settings.Style = ChartStyle.Footprint;
        Title = "Footprint editor";
        Width = 980;
        Height = 720;
        MinWidth = 820;
        MinHeight = 560;
        Background = Ui.Bg0;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _columnGrid.Changed += Apply;
        _layoutGrid.Changed += Apply;
        _layoutGrid.Exclude.Add(nameof(FootprintLayout.Columns));
        _layoutGrid.Exclude.Add(nameof(FootprintLayout.Name));
        Content = Build();
        LoadIntoUi();
    }

    private Control Build()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("340,14,*"), Margin = new Thickness(14) };

        // ---- columna izquierda: plantilla + columnas
        var left = new DockPanel();
        foreach (var p in FootprintLayout.Presets) _presets.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p });
        foreach (var p in FootprintLayout.LoadUserTemplates()) _presets.Items.Add(new ComboBoxItem { Content = "★ " + p.DisplayName, Tag = p });
        _presets.SelectionChanged += (_, _) =>
        {
            if (_presets.SelectedItem is not ComboBoxItem { Tag: FootprintLayout p }) return;
            var c = p.Clone();
            _layout.Columns = c.Columns;
            _layout.Name = c.Name;
            _layout.ShowTotals = c.ShowTotals;
            _layout.ShowPoc = c.ShowPoc;
            _layout.ShowImbalances = c.ShowImbalances;
            _layout.ImbalanceRatio = c.ImbalanceRatio;
            _layout.StrongDeltaRatio = c.StrongDeltaRatio;
            _layout.HighlightMinVolumeFraction = c.HighlightMinVolumeFraction;
            _layout.ProportionPerCandle = c.ProportionPerCandle;
            LoadIntoUi();
            Apply();
        };
        var top = Ui.V(10, Ui.Field("Start from a template", _presets));
        top.Margin = new Thickness(0, 0, 0, 14);
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);

        var listHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var moveTools = Ui.H(2,
            Ui.IconButton(MaterialIconKind.ArrowUp, "Move left", () => Move(-1), 16),
            Ui.IconButton(MaterialIconKind.ArrowDown, "Move right", () => Move(1), 16),
            Ui.IconButton(MaterialIconKind.DeleteOutline, "Remove column", RemoveSelected, 16));
        DockPanel.SetDock(moveTools, Dock.Right);
        listHeader.Children.Add(moveTools);
        listHeader.Children.Add(Ui.V(2, Ui.H3("Columns"), Ui.Faint("left to right within each bar")));
        DockPanel.SetDock(listHeader, Dock.Top);
        left.Children.Add(listHeader);

        var addPanel = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        void AddBtn(string text, MaterialIconKind icon, Func<FootprintColumn> make)
        {
            var b = Ui.Button(text, icon, () =>
            {
                int idx = _columns.SelectedIndex >= 0 ? _columns.SelectedIndex + 1 : _layout.Columns.Count;
                _layout.Columns.Insert(idx, make());
                RefreshColumns(idx);
                Apply();
            });
            b.Padding = new Thickness(9, 4);
            addPanel.Children.Add(b);
        }

        AddBtn("Ladder", MaterialIconKind.FormatListNumbered, () => new FootprintColumn { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Delta, Highlight = HighlightRule.StrongDelta });
        AddBtn("Profile", MaterialIconKind.ChartGantt, () => new FootprintColumn { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume });
        AddBtn("Cells", MaterialIconKind.ViewGridOutline, () => new FootprintColumn { Kind = FootprintColumnKind.Cells, Value = FootprintValue.BidAsk });
        AddBtn("Bar", MaterialIconKind.ChartBoxOutline, () => new FootprintColumn { Kind = FootprintColumnKind.Candle });
        AddBtn("Spacer", MaterialIconKind.ArrowExpandHorizontal, () => new FootprintColumn { Kind = FootprintColumnKind.Spacer, Width = 0.2 });

        var save = Ui.Button("Save as template", MaterialIconKind.StarOutline, () =>
        {
            _layout.Name = string.IsNullOrWhiteSpace(_name.Text) ? "My footprint" : _name.Text!.Trim();
            _layout.SaveAsTemplate();
            _main.RefreshLayoutBoxes();
            _main.SetStatus($"Template \"{_layout.Name}\" saved");
        }, "primary");
        save.HorizontalAlignment = HorizontalAlignment.Stretch;
        save.HorizontalContentAlignment = HorizontalAlignment.Center;

        var bottom = Ui.V(10, addPanel, Ui.Divider(), Ui.Field("Template name", _name), save);
        DockPanel.SetDock(bottom, Dock.Bottom);
        left.Children.Add(bottom);

        _columns.SelectionChanged += (_, _) =>
        {
            int i = _columns.SelectedIndex;
            var col = i >= 0 && i < _layout.Columns.Count ? _layout.Columns[i] : null;
            _columnGrid.Target = col;
            _columnTitle.Text = col == null ? "No column selected" : $"Column {i + 1}: {PropertyGrid.EnumDisplay(col.Kind)}";
        };
        left.Children.Add(Ui.Card(_columns, 4));
        root.Children.Add(left);

        // ---- derecha: columna seleccionada + opciones generales
        var right = new Grid { RowDefinitions = new RowDefinitions("*,14,0.8*") };
        var colCard = new DockPanel();
        _columnTitle.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(_columnTitle, Dock.Top);
        colCard.Children.Add(_columnTitle);
        colCard.Children.Add(_columnGrid);
        right.Children.Add(Ui.Card(colCard));

        var general = new DockPanel();
        var gh = Ui.V(2, Ui.H2("General options"), Ui.Faint("Totals, POC, imbalances and scaling for the entire footprint"));
        gh.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(gh, Dock.Top);
        general.Children.Add(gh);
        general.Children.Add(_layoutGrid);
        var generalCard = Ui.Card(general);
        Grid.SetRow(generalCard, 2);
        right.Children.Add(generalCard);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        return root;
    }

    private void LoadIntoUi()
    {
        _name.Text = _layout.DisplayName;
        RefreshColumns(0);
        _layoutGrid.Target = _layout;
    }

    private static MaterialIconKind IconOf(FootprintColumn c) => c.Kind switch
    {
        FootprintColumnKind.Candle => MaterialIconKind.ChartBoxOutline,
        FootprintColumnKind.Spacer => MaterialIconKind.ArrowExpandHorizontal,
        FootprintColumnKind.Ladder => MaterialIconKind.FormatListNumbered,
        FootprintColumnKind.Profile => MaterialIconKind.ChartGantt,
        _ => MaterialIconKind.ViewGridOutline,
    };

    private static (string Title, string Detail) Describe(FootprintColumn c) => c.Kind switch
    {
        FootprintColumnKind.Candle => ("Thin candle", "body and wicks"),
        FootprintColumnKind.Spacer => ("Spacer", $"width {c.Width:0.##}"),
        FootprintColumnKind.Ladder => ("Ladder", PropertyGrid.EnumDisplay(c.Value) + (c.Highlight != HighlightRule.None ? " · highlights " + PropertyGrid.EnumDisplay(c.Highlight).ToLowerInvariant() : "")),
        FootprintColumnKind.Profile => ("Profile", $"{PropertyGrid.EnumDisplay(c.Value)} · {PropertyGrid.EnumDisplay(c.Direction).ToLowerInvariant()}"),
        _ => ("Cells", $"{PropertyGrid.EnumDisplay(c.Value)} · {PropertyGrid.EnumDisplay(c.ColorScheme).ToLowerInvariant()}"),
    };

    private static Control Row(FootprintColumn c, int index)
    {
        var (title, detail) = Describe(c);
        var num = new TextBlock { Text = (index + 1).ToString(), Classes = { "faint", "small" }, Width = 16, VerticalAlignment = VerticalAlignment.Center };
        return Ui.H(10, num, Ui.Icon(IconOf(c), 18, Ui.Accent), Ui.V(1, new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = detail, Classes = { "muted", "small" } }));
    }

    private void RefreshColumns(int select)
    {
        _columns.Items.Clear();
        for (int i = 0; i < _layout.Columns.Count; i++) _columns.Items.Add(new ListBoxItem { Content = Row(_layout.Columns[i], i) });
        if (_layout.Columns.Count > 0) _columns.SelectedIndex = Math.Clamp(select, 0, _layout.Columns.Count - 1);
    }

    private void RemoveSelected()
    {
        int i = _columns.SelectedIndex;
        if (i < 0 || i >= _layout.Columns.Count) return;
        _layout.Columns.RemoveAt(i);
        RefreshColumns(Math.Min(i, _layout.Columns.Count - 1));
        Apply();
    }

    private void Move(int dir)
    {
        int i = _columns.SelectedIndex;
        int j = i + dir;
        if (i < 0 || j < 0 || j >= _layout.Columns.Count) return;
        (_layout.Columns[i], _layout.Columns[j]) = (_layout.Columns[j], _layout.Columns[i]);
        RefreshColumns(j);
        Apply();
    }

    private void Apply()
    {
        int sel = _columns.SelectedIndex;
        for (int k = 0; k < _layout.Columns.Count && k < _columns.Items.Count; k++)
            if (_columns.Items[k] is ListBoxItem li) li.Content = Row(_layout.Columns[k], k);
        _columns.SelectedIndex = sel;
        _model.Invalidate();
    }
}
