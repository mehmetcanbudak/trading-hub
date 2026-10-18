using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Charting;

namespace TradingHub.App.Windows;

/// <summary>
/// Configuración de un gráfico: colores del fondo, las velas, los ejes, el precio, el footprint y las órdenes, y qué
/// se muestra. Cada gráfico tiene la suya (los gráficos nuevos arrancan con la del gráfico activo).
/// </summary>
internal sealed class ChartSettingsWindow : Window
{
    private static readonly uint[] Palette =
    {
        0xFF000000, 0xFF01000C, 0xFF06152A, 0xFF0B1220, 0xFF131722, 0xFF1E222D, 0xFF2A2E39, 0xFF434651, 0xFF787B86, 0xFFB2B5BE, 0xFFFFFFFF,
        0xFF49D149, 0xFF26A69A, 0xFF089981, 0xFF22D3EE, 0xFF1E90FF, 0xFF2962FF, 0xFFB478FF, 0xFFEC4899, 0xFFE84548, 0xFFF23645, 0xFFF97316, 0xFFF2B53A,
    };

    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private readonly ChartModel _model;
    private readonly List<Action> _refreshers = new();

    public ChartSettingsWindow(TradingView host, ChartTab chart)
    {
        _host = host;
        _chart = chart;
        _model = chart.Model!;
        Title = $"Chart settings · {chart.Symbol} {_model.Period.DisplayName}";
        Width = 470;
        Height = 700;
        MinWidth = 420;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;

        // secciones en pestañas (barra de arriba), como la configuración de ATAS
        var tabs = new TabControl { Margin = new Thickness(14, 4, 14, 0) };
        void Tab(string title, params Control[] rows)
        {
            var v = new StackPanel { Spacing = 10, Margin = new Thickness(6, 14, 6, 18) };
            foreach (var r in rows) v.Children.Add(r);
            tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = v } });
        }

        Tab("Background",
            ColorRow("Background (top)", nameof(ChartTheme.BackgroundTop)),
            ColorRow("Background (bottom)", nameof(ChartTheme.BackgroundBottom)),
            SmallButton("Solid background (bottom matches top)", () => Set(nameof(ChartTheme.BackgroundBottom), Get(nameof(ChartTheme.BackgroundTop)))),
            ColorRow("Grid", nameof(ChartTheme.Grid)),
            Toggle("Show grid", () => _model.Settings.ShowGrid, v => _model.Settings.ShowGrid = v),
            ColorRow("Panel separator", nameof(ChartTheme.PanelSeparator)));
        Tab("Candles",
            ColorRow("Bullish", nameof(ChartTheme.UpCandle)),
            ColorRow("Bearish", nameof(ChartTheme.DownCandle)),
            ColorRow("Bullish border", nameof(ChartTheme.UpCandleBorder)),
            ColorRow("Bearish border", nameof(ChartTheme.DownCandleBorder)),
            ColorRow("Wick", nameof(ChartTheme.Wick)));
        Tab("Axes",
            ColorRow("Axis background", nameof(ChartTheme.AxisBackground)),
            ColorRow("Axis text", nameof(ChartTheme.AxisText)),
            ColorRow("Axis lines", nameof(ChartTheme.AxisLine)),
            ColorRow("Text", nameof(ChartTheme.Text)),
            ColorRow("Secondary text", nameof(ChartTheme.TextDim)),
            Toggle("Bar data above (O, H, L, C…)", () => _model.Settings.ShowInfoPanel, v => _model.Settings.ShowInfoPanel = v));
        Tab("Price",
            ColorRow("Last price (up)", nameof(ChartTheme.LastPriceUp)),
            ColorRow("Last price (down)", nameof(ChartTheme.LastPriceDown)),
            ColorRow("Last price line", nameof(ChartTheme.LastPriceLine)),
            Toggle("Show last price line", () => _model.Settings.ShowLastPriceLine, v => _model.Settings.ShowLastPriceLine = v),
            ColorRow("Crosshair", nameof(ChartTheme.Crosshair)),
            ColorRow("Crosshair label", nameof(ChartTheme.CrosshairLabelBack)),
            ColorRow("Session start", nameof(ChartTheme.NewSession)),
            Toggle("Mark each session start", () => _model.Settings.ShowSessionLines, v => _model.Settings.ShowSessionLines = v));
        Tab("Footprint",
            ColorRow("Bid (sellers)", nameof(ChartTheme.Bid)),
            ColorRow("Ask (compradores)", nameof(ChartTheme.Ask)),
            ColorRow("Cells", nameof(ChartTheme.ClusterBase)),
            ColorRow("Cell border", nameof(ChartTheme.CellBorder)),
            ColorRow("Cell text", nameof(ChartTheme.ClusterText)),
            ColorRow("POC text in cells", nameof(ChartTheme.ClusterTextStrong)),
            ColorRow("Ladder text", nameof(ChartTheme.LadderText)),
            ColorRow("Ladder: positive highlight", nameof(ChartTheme.LadderPositive)),
            ColorRow("Ladder: negative highlight", nameof(ChartTheme.LadderNegative)),
            ColorRow("POC", nameof(ChartTheme.Poc)),
            ColorRow("Value area", nameof(ChartTheme.ValueArea)),
            ColorRow("Buy imbalance", nameof(ChartTheme.ImbalanceAsk)),
            ColorRow("Sell imbalance", nameof(ChartTheme.ImbalanceBid)),
            ColorRow("Positive delta", nameof(ChartTheme.DeltaPositiveText)),
            ColorRow("Negative delta", nameof(ChartTheme.DeltaNegativeText)),
            ColorRow("Volume profile", nameof(ChartTheme.ProfileBar)),
            Note("These colors apply to the entire chart. Each footprint column (delta, volume, bid/ask ladder, cells or profile) can also have its own colors, including text, in the footprint editor."));

        // grosor y trazo de la línea entrada → salida (las operaciones propias y las de las estrategias)
        var width = new Slider { Minimum = 0.5, Maximum = 6, Value = _model.Settings.TradeLineWidth, TickFrequency = 0.5, IsSnapToTickEnabled = true, Width = 180 };
        var widthText = new TextBlock { Text = $"{_model.Settings.TradeLineWidth:0.#} px", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
        width.ValueChanged += (_, _) =>
        {
            _model.Settings.TradeLineWidth = width.Value;
            widthText.Text = $"{width.Value:0.#} px";
            _model.Invalidate();
            Workspace.Save(_host);
        };
        var style = new ComboBox { Width = 180 };
        foreach (var (value, label) in new[] { (TradeLineStyle.Dashed, "Rayas"), (TradeLineStyle.Solid, "Continuous"), (TradeLineStyle.Dotted, "Points") })
            style.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        style.SelectedItem = style.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (TradeLineStyle)i.Tag! == _model.Settings.TradeLineStyle);
        style.SelectionChanged += (_, _) =>
        {
            if (style.SelectedItem is not ComboBoxItem { Tag: TradeLineStyle v }) return;
            _model.Settings.TradeLineStyle = v;
            _model.Invalidate();
            Workspace.Save(_host);
        };
        _refreshers.Add(() =>
        {
            width.Value = _model.Settings.TradeLineWidth;
            style.SelectedItem = style.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (TradeLineStyle)i.Tag! == _model.Settings.TradeLineStyle);
        });
        Control Row(string label, Control control)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        Tab("Trades",
            Ui.H3("Entry-to-exit lines"),
            ColorRow("Winning trade", nameof(ChartTheme.TradeWin)),
            ColorRow("Losing trade", nameof(ChartTheme.TradeLoss)),
            Row("Width", Ui.H(10, width, widthText)),
            Row("Line style", style),
            Toggle("Mark my trades (fills and entry-to-exit lines)", () => _model.Settings.ShowTradeMarks, v => _model.Settings.ShowTradeMarks = v),
            Note("Chart strategy lines use the same colors, width and line style."),
            Ui.H3("Orders and position"),
            ColorRow("Buy order", nameof(ChartTheme.BuyOrder)),
            ColorRow("Sell order", nameof(ChartTheme.SellOrder)),
            ColorRow("Long position / buys", nameof(ChartTheme.PositionLong)),
            ColorRow("Short position / sells", nameof(ChartTheme.PositionShort)));

        var head = Ui.V(4, Ui.H2("Chart settings"), Note($"{chart.Symbol} · {_model.Period.DisplayName}. Changes appear immediately and are saved for this chart."));
        head.Margin = new Thickness(20, 16, 20, 4);
        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        body.Children.Add(tabs);

        var reset = Ui.Button("Reset all", MaterialIconKind.Restore, () =>
        {
            _model.Settings.Colors = null;
            Changed();
        });
        ToolTip.SetTip(reset, "Restore this chart's default colors");
        var toAll = Ui.Button("Apply to all charts", MaterialIconKind.ContentCopy, () =>
        {
            int n = _host.ApplyChartLook(_chart);
            Title = $"Chart settings · applied to {n} chart{(n == 1 ? "" : "s")}";
        });
        ToolTip.SetTip(toAll, "Copy these colors and visibility settings to all open charts");
        var close = Ui.Button("Done", MaterialIconKind.Check, Close, "primary");
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(20, 10, 20, 16) };
        foreach (var b in new Control[] { reset, toAll, close }) actions.Children.Add(b);

        var root = new DockPanel();
        var actionsBorder = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(actionsBorder, Dock.Bottom);
        root.Children.Add(actionsBorder);
        root.Children.Add(body);
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private static Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 9 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private static Button SmallButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2), MinHeight = 24, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    private uint Get(string property) =>
        _model.Settings.Colors != null && _model.Settings.Colors.TryGetValue(property, out var c) ? c : ChartTheme.DefaultOf(property);

    private void Set(string property, uint? argb)
    {
        var colors = _model.Settings.Colors ??= new Dictionary<string, uint>();
        if (argb is not { } v || v == ChartTheme.DefaultOf(property)) colors.Remove(property);
        else colors[property] = v;
        if (colors.Count == 0) _model.Settings.Colors = null;
        Changed();
    }

    private void Changed()
    {
        _model.ApplyColors();
        Workspace.Save(_host);
        foreach (var r in _refreshers) r();
    }

    private Control Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = get() };
        cb.IsCheckedChanged += (_, _) =>
        {
            set(cb.IsChecked == true);
            _model.Invalidate();
            Workspace.Save(_host);
        };
        _refreshers.Add(() => cb.IsChecked = get());
        return cb;
    }

    /// <summary>Fila de un color: muestra el actual y al tocarlo abre la paleta, el código y "restablecer".</summary>
    private Control ColorRow(string label, string property)
    {
        var swatch = new Border { Width = 34, Height = 18, CornerRadius = new CornerRadius(4), BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
        var code = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Text2, FontFamily = new FontFamily("Menlo") };
        var changed = new TextBlock { Text = "•", Foreground = Ui.Accent, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold };
        ToolTip.SetTip(changed, "Customized for this chart");
        var hex = new TextBox { Width = 110, Watermark = "#RRGGBB or #RRGGBBAA", FontSize = 12 };
        var opacity = new Slider { Minimum = 0, Maximum = 255, Width = 150, TickFrequency = 5 };
        var opacityText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Text2, Width = 38 };
        var palette = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, MaxWidth = 290 };
        var flyout = new Flyout();
        bool syncing = false;

        void Refresh()
        {
            uint c = Get(property);
            swatch.Background = new SolidColorBrush(Color.FromUInt32(c));
            code.Text = $"#{c & 0xFFFFFF:X6}" + ((c >> 24) != 0xFF ? $" · {(c >> 24) * 100 / 255}%" : "");
            changed.IsVisible = _model.Settings.Colors?.ContainsKey(property) == true;
            syncing = true;
            hex.Text = (c >> 24) == 0xFF ? $"#{c & 0xFFFFFF:X6}" : $"#{c & 0xFFFFFF:X6}{c >> 24:X2}";
            opacity.Value = c >> 24;
            opacityText.Text = $"{(c >> 24) * 100 / 255}%";
            syncing = false;
        }

        foreach (var p in Palette)
        {
            uint color = p;
            var b = new Button { Content = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(color)), BorderBrush = Ui.Line, BorderThickness = new Thickness(1) }, Padding = new Thickness(1), MinHeight = 0 };
            b.Click += (_, _) =>
            {
                // conserva la transparencia que tenía (la cuadrícula, por ejemplo, es casi transparente)
                uint alpha = Get(property) & 0xFF000000;
                Set(property, (color & 0x00FFFFFF) | (alpha == 0 ? 0xFF000000 : alpha));
            };
            palette.Children.Add(b);
        }

        void ApplyHex()
        {
            var t = (hex.Text ?? "").Trim().TrimStart('#');
            if (t.Length is 6 or 8 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                Set(property, t.Length == 6 ? 0xFF000000 | v : (v >> 8) | ((v & 0xFF) << 24));
            else Refresh();
        }

        hex.LostFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyHex();
        };
        opacity.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            uint c = Get(property);
            Set(property, (c & 0x00FFFFFF) | ((uint)Math.Round(opacity.Value) << 24));
        };
        var reset = SmallButton("Reset", () => Set(property, null));
        flyout.Content = Ui.V(10, Ui.H3(label), palette, Ui.H(8, new TextBlock { Text = "Code", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, hex),
            Ui.H(8, new TextBlock { Text = "Opacity", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, opacity, opacityText), reset).Also(v => v.Margin = new Thickness(12));

        var button = new Button { Content = Ui.H(8, swatch, code), Padding = new Thickness(6, 3), MinHeight = 26, Flyout = flyout, HorizontalAlignment = HorizontalAlignment.Left };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*,Auto") };
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
        Grid.SetColumn(button, 1);
        g.Children.Add(button);
        Grid.SetColumn(changed, 2);
        g.Children.Add(changed);
        _refreshers.Add(Refresh);
        Refresh();
        return g;
    }
}
