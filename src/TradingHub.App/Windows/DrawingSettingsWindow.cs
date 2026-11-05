using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Charting.Drawings;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Windows;

/// <summary>
/// Panel completo de un dibujo (doble clic sobre él): color, grosor, estilo de línea, relleno, texto,
/// extensiones, niveles de Fibonacci, precios, cantidad de la posición y visibilidad. Los cambios se ven al
/// instante en todos los gráficos del instrumento.
/// </summary>
public sealed class DrawingSettingsWindow : Window
{
    private static readonly uint[] Palette =
    {
        0xFF1E90FF, 0xFF3D8BFD, 0xFF22D3EE, 0xFF26C281, 0xFF49D149, 0xFFF2B53A, 0xFFF97316,
        0xFFF0525A, 0xFFE84548, 0xFFEC4899, 0xFFB478FF, 0xFFFFFFFF, 0xFF9CA3AF, 0xFF1F2937,
    };

    private readonly ChartDrawing _d;
    private readonly Instrument _instrument;
    private readonly ChartPeriod _period;
    private readonly Action _changed;
    private readonly AppServices _svc;
    private readonly bool _concealTimestamps;

    /// <param name="changed">Se llama en cada cambio (redibujar y guardar).</param>
    /// <param name="delete">Borra el dibujo de su lista.</param>
    /// <param name="duplicate">Agrega una copia.</param>
    public DrawingSettingsWindow(AppServices svc, ChartDrawing drawing, Instrument instrument, ChartPeriod period, Action changed, Action delete, Action duplicate, bool concealTimestamps = false)
    {
        _svc = svc;
        _d = drawing;
        _instrument = instrument;
        _period = period;
        _changed = changed;
        _concealTimestamps = concealTimestamps;
        Title = ChartDrawing.KindName(drawing.Kind);
        Width = 430;
        Height = 640;
        MinWidth = 380;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(20, 18, 20, 18) };
        body.Children.Add(Ui.H2(ChartDrawing.KindName(drawing.Kind)));

        if (!drawing.IsPosition && drawing.Kind != DrawingKind.Ruler) body.Children.Add(StyleSection());
        if (drawing.Kind == DrawingKind.Rectangle) body.Children.Add(FillSection());
        if (drawing.Kind is not (DrawingKind.Ruler or DrawingKind.FibRetracement) && !drawing.IsPosition) body.Children.Add(TextSection());
        if (drawing.Kind is DrawingKind.TrendLine or DrawingKind.Rectangle or DrawingKind.HorizontalRay or DrawingKind.FibRetracement) body.Children.Add(ExtendSection());
        if (drawing.Kind == DrawingKind.FibRetracement) body.Children.Add(LevelsSection());
        if (drawing.IsPosition) body.Children.Add(PositionSection());
        else if (drawing.Kind == DrawingKind.AnchoredVwap) body.Children.Add(AnchoredVwapSection());
        else if (drawing.Kind == DrawingKind.FixedRangeProfile) body.Children.Add(ProfileSection());
        else if (drawing.Kind != DrawingKind.VerticalLine) body.Children.Add(PricesSection());
        body.Children.Add(VisibilitySection());

        var makeDefault = Ui.Button("Use as default", MaterialIconKind.StarOutline, () =>
        {
            var template = new ChartDrawing { Kind = _d.Kind };
            template.CopyStyleFrom(_d);
            _svc.Settings.DrawingDefaults[_d.Kind.ToString()] = template;
            _svc.Settings.Save();
            Title = ChartDrawing.KindName(_d.Kind) + " · style saved as default";
        });
        ToolTip.SetTip(makeDefault, "New drawings of this type use this style");
        var dup = Ui.Button("Duplicate", MaterialIconKind.ContentCopy, () =>
        {
            duplicate();
            Close();
        });
        var del = Ui.Button("Remove", MaterialIconKind.DeleteOutline, () =>
        {
            delete();
            Close();
        }, "danger");
        var close = Ui.Button("Done", MaterialIconKind.Check, Close, "primary");
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(20, 10, 20, 16) };
        foreach (var b in new Control[] { makeDefault, dup, del, close }) actions.Children.Add(b);

        var root = new DockPanel();
        var actionsBorder = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(actionsBorder, Dock.Bottom);
        root.Children.Add(actionsBorder);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private void Changed() => _changed();

    // ------------------------------------------------------------------ secciones

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 10 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private static Control Labeled(string label, Control control)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
        g.Children.Add(t);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private Control StyleSection()
    {
        var width = new Slider { Minimum = 0.5, Maximum = 6, Value = _d.Width, TickFrequency = 0.5, IsSnapToTickEnabled = true, MinWidth = 160 };
        var widthText = new TextBlock { Text = $"{_d.Width:0.#} px", VerticalAlignment = VerticalAlignment.Center, Width = 44, Classes = { "muted" } };
        width.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _d.Width = (float)width.Value;
            widthText.Text = $"{_d.Width:0.#} px";
            Changed();
        };
        var style = new Ui.Segmented<LineStyleKind>(new[]
        {
            (LineStyleKind.Solid, "Solid", (string?)null),
            (LineStyleKind.Dashed, "Dashed", (string?)null),
            (LineStyleKind.Dotted, "Points", (string?)null),
        });
        style.Select(_d.EffectiveLineStyle);
        style.SelectionChanged += v =>
        {
            _d.LineStyle = v;
            _d.Dashed = false;
            Changed();
        };
        return Section("Line",
            Labeled("Color", ColorPicker(() => _d.ColorArgb, c => _d.ColorArgb = c, allowAuto: false)),
            Labeled("Width", Ui.H(8, width, widthText)),
            Labeled("Style", style));
    }

    private Control FillSection()
    {
        uint Current() => _d.FillArgb != 0 ? _d.FillArgb : (_d.ColorArgb & 0x00FFFFFF) | (40u << 24);
        var opacity = new Slider { Minimum = 0, Maximum = 255, Value = Current() >> 24, MinWidth = 160 };
        opacity.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _d.FillArgb = (Current() & 0x00FFFFFF) | ((uint)opacity.Value << 24);
            Changed();
        };
        return Section("Fill",
            Labeled("Color", ColorPicker(() => _d.FillArgb, c => _d.FillArgb = c == 0 ? 0 : (c & 0x00FFFFFF) | ((uint)opacity.Value << 24), allowAuto: true)),
            Labeled("Opacity", opacity));
    }

    private Control TextSection()
    {
        var text = new TextBox { Text = _d.Text == "alerta" ? "" : _d.Text, Watermark = _d.Kind == DrawingKind.Text ? "Text" : "Label (optional)", AcceptsReturn = false };
        text.TextChanged += (_, _) =>
        {
            _d.Text = text.Text ?? "";
            if (_d.Text == "alerta") _d.Text = "alerta ";
            Changed();
        };
        var size = new NumericUpDown { Minimum = 8, Maximum = 40, Increment = 1, Value = (decimal)_d.FontSize, FormatString = "0", Width = 110 };
        size.ValueChanged += (_, _) =>
        {
            _d.FontSize = (float)(size.Value ?? 13);
            Changed();
        };
        var bold = new CheckBox { Content = "Bold", IsChecked = _d.Bold, Margin = new Thickness(12, 0, 0, 0) };
        bold.IsCheckedChanged += (_, _) =>
        {
            _d.Bold = bold.IsChecked == true;
            Changed();
        };
        return Section("Text",
            text,
            Labeled("Size", Ui.H(4, size, bold)),
            Labeled("Color", ColorPicker(() => _d.TextArgb, c => _d.TextArgb = c, allowAuto: true)));
    }

    private Control ExtendSection()
    {
        var left = new CheckBox { Content = "Left", IsChecked = _d.ExtendLeft };
        var right = new CheckBox { Content = "Right", IsChecked = _d.ExtendRight, Margin = new Thickness(16, 0, 0, 0) };
        left.IsCheckedChanged += (_, _) =>
        {
            _d.ExtendLeft = left.IsChecked == true;
            Changed();
        };
        right.IsCheckedChanged += (_, _) =>
        {
            _d.ExtendRight = right.IsChecked == true;
            Changed();
        };
        var rows = new List<Control> { Ui.H(0, left, right) };
        if (_d.Kind is DrawingKind.HorizontalRay or DrawingKind.FibRetracement or DrawingKind.HorizontalLine)
        {
            var price = new CheckBox { Content = "Show price", IsChecked = _d.ShowPriceLabel };
            price.IsCheckedChanged += (_, _) =>
            {
                _d.ShowPriceLabel = price.IsChecked == true;
                Changed();
            };
            rows.Add(price);
        }

        return Section("Extend", rows.ToArray());
    }

    private Control LevelsSection()
    {
        var list = new StackPanel { Spacing = 6 };
        void Rebuild()
        {
            list.Children.Clear();
            var levels = _d.Levels ??= _d.EffectiveLevels.Select(l => l.Clone()).ToList();
            foreach (var lvl in levels.OrderBy(l => l.Value).ToList())
            {
                var on = new CheckBox { IsChecked = lvl.Visible, VerticalAlignment = VerticalAlignment.Center };
                on.IsCheckedChanged += (_, _) =>
                {
                    lvl.Visible = on.IsChecked == true;
                    Changed();
                };
                var value = new NumericUpDown { Value = (decimal)lvl.Value, Increment = 0.001m, FormatString = "0.###", Width = 150, Minimum = -10, Maximum = 10, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
                value.ValueChanged += (_, _) =>
                {
                    lvl.Value = (double)(value.Value ?? 0);
                    Changed();
                };
                var color = SwatchButton(() => lvl.ColorArgb != 0 ? lvl.ColorArgb : _d.ColorArgb, c =>
                {
                    lvl.ColorArgb = c;
                    Changed();
                });
                var remove = Ui.IconButton(MaterialIconKind.Close, "Remove level", () =>
                {
                    levels.Remove(lvl);
                    Changed();
                    Rebuild();
                }, 15);
                list.Children.Add(Ui.H(8, on, value, color, remove));
            }
        }

        Rebuild();
        var add = Ui.Button("Add level", MaterialIconKind.Plus, () =>
        {
            (_d.Levels ??= _d.EffectiveLevels.Select(l => l.Clone()).ToList()).Add(new FibLevel { Value = 2.618 });
            Changed();
            Rebuild();
        });
        var reset = Ui.Button("Reset", MaterialIconKind.Restore, () =>
        {
            _d.Levels = null;
            Changed();
            Rebuild();
        });
        var price = new CheckBox { Content = "Show each level's price", IsChecked = _d.ShowPriceLabel };
        price.IsCheckedChanged += (_, _) =>
        {
            _d.ShowPriceLabel = price.IsChecked == true;
            Changed();
        };
        return Section("Levels", list, Ui.H(8, add, reset), price);
    }

    private NumericUpDown PriceBox(double value, Action<double> set)
    {
        decimal tick = _instrument.TickSize > 0 ? _instrument.TickSize : 0.01m;
        var box = new NumericUpDown
        {
            Value = _instrument.RoundPrice((decimal)value),
            Increment = tick,
            FormatString = "F" + _instrument.PriceDecimals.ToString(CultureInfo.InvariantCulture),
            MinWidth = 160,
            NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
        };
        box.ValueChanged += (_, _) =>
        {
            if (box.Value is not { } v) return;
            set((double)v);
            Changed();
        };
        return box;
    }

    private Control PositionSection()
    {
        var sized = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        NumericUpDown? qty = null;
        void Refresh()
        {
            var result = _d.PositionSizing(_instrument);
            sized.Text = result.IsValid
                ? $"Effective quantity {result.Quantity:0.########} · risk {result.Risk:0.00} · reward {result.Reward:0.00} {result.Currency}\n{result.Limitation}"
                : result.Limitation;
            if (qty != null) qty.IsEnabled = _d.AccountSize <= 0;
        }
        var entry = PriceBox(_d.Points[0].Price, v => { _d.Points[0] = new ChartPoint(_d.Points[0].Time, v); Refresh(); });
        var target = PriceBox(_d.Points[1].Price, v => { _d.Points[1] = new ChartPoint(_d.Points[1].Time, v); Refresh(); });
        var stop = PriceBox(_d.Points[2].Price, v => { _d.Points[2] = new ChartPoint(_d.Points[2].Time, v); Refresh(); });
        NumericUpDown Number(string name, decimal value, decimal minimum, decimal maximum, decimal increment, Action<decimal> set)
        {
            var box = new NumericUpDown { Value = Math.Clamp(value, minimum, maximum), Minimum = minimum, Maximum = maximum, Increment = increment, FormatString = "0.########", MinWidth = 160, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
            Avalonia.Automation.AutomationProperties.SetName(box, name);
            box.ValueChanged += (_, _) => { set(box.Value ?? 0); Refresh(); Changed(); };
            return box;
        }
        qty = Number("Drawing quantity", _d.Quantity, 0, decimal.MaxValue, Math.Max(_instrument.VolumeStep, 0.00000001m), v => _d.Quantity = v);
        var account = Number("Reference account size", _d.AccountSize, 0, decimal.MaxValue, 100, v => _d.AccountSize = v);
        var risk = Number("Risk percent", _d.RiskPercent, 0, 100, 0.25m, v => _d.RiskPercent = v);
        var fees = Number("Round-trip fee per quantity", _d.RoundTripFeePerUnit, 0, decimal.MaxValue, 0.01m, v => _d.RoundTripFeePerUnit = v);
        var accountCurrency = new TextBox { Text = _d.AccountCurrency, Watermark = "Account currency, e.g. USD", MinWidth = 160 };
        Avalonia.Automation.AutomationProperties.SetName(accountCurrency, "Reference account currency");
        accountCurrency.TextChanged += (_, _) => { _d.AccountCurrency = accountCurrency.Text?.Trim().ToUpperInvariant() ?? ""; Refresh(); Changed(); };
        var pnlCurrency = new TextBox { Text = string.IsNullOrWhiteSpace(_instrument.QuoteAsset) ? _d.PnlCurrency : _instrument.QuoteAsset, IsReadOnly = !string.IsNullOrWhiteSpace(_instrument.QuoteAsset), Watermark = "Confirm linear P&L currency", MinWidth = 160 };
        Avalonia.Automation.AutomationProperties.SetName(pnlCurrency, "Linear P&L currency");
        pnlCurrency.TextChanged += (_, _) => { _d.PnlCurrency = pnlCurrency.Text?.Trim().ToUpperInvariant() ?? ""; Refresh(); Changed(); };
        Refresh();
        return Section("Visual risk / reward",
            Labeled("Entry", entry), Labeled("Target", target), Labeled("Stop", stop), Labeled("Quantity", qty),
            Labeled("Reference account size", account), Labeled("Risk (%)", risk), Labeled("Account currency", accountCurrency),
            Labeled("Linear P&L currency", pnlCurrency), Labeled("Round-trip fee / unit", fees), sized,
            Note($"Point value {_instrument.PointValue:0.########} · quantity increment {_instrument.VolumeStep:0.########}. A positive reference account size enables risk sizing; quantity is always rounded down."),
            Note("Amounts are a linear point-value estimate, not inverse/quanto contract or FX conversion. Confirm the P&L currency. Fee input is round-trip per quantity; zero excludes fees. Slippage, funding and taxes are excluded. This drawing never places orders or changes the selected account."));
    }

    private Control TimestampRow(string label, int point)
    {
        if (_concealTimestamps) return Note(label + " · practice timeline (reveal to inspect timestamps)");
        var box = new TextBox { Text = _d.Points[point].Time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture), MinWidth = 220 };
        Avalonia.Automation.AutomationProperties.SetName(box, label + " UTC");
        var error = new TextBlock { Classes = { "faint", "small" } };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            if (DateTime.TryParse(box.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            {
                _d.Points[point] = new ChartPoint(time, _d.Points[point].Price);
                error.Text = "";
                Changed();
            }
            else error.Text = "Enter a valid UTC timestamp.";
            e.Handled = true;
        };
        return Ui.V(4, Labeled(label + " (UTC)", box), error);
    }

    private Control AnalysisNumber(string label, decimal value, decimal min, decimal max, Action<decimal> set)
    {
        var box = new NumericUpDown { Value = Math.Clamp(value, min, max), Minimum = min, Maximum = max, Increment = 1, FormatString = "0.##", MinWidth = 150, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        box.ValueChanged += (_, _) => { set(box.Value ?? min); Changed(); };
        return Labeled(label, box);
    }

    private Control AnchoredVwapSection() => Section("Anchored VWAP",
        TimestampRow("Anchor", 0),
        AnalysisNumber("Deviation band 1 (σ)", double.IsFinite(_d.Band1) ? (decimal)Math.Clamp(_d.Band1, 0, 10) : 1, 0, 10, v => _d.Band1 = (double)v),
        AnalysisNumber("Deviation band 2 (σ)", double.IsFinite(_d.Band2) ? (decimal)Math.Clamp(_d.Band2, 0, 10) : 2, 0, 10, v => _d.Band2 = (double)v),
        Note("Volume-weighted mean and deviation use every available record from the exact anchor. Compact aggregates remain labelled aggregates; synthetic candle estimates and inferred aggressor side remain visible. A zero band hides it. No orders."));

    private Control ProfileSection()
    {
        var delta = new CheckBox { Content = "Show signed bid/ask delta", IsChecked = _d.ProfileDelta };
        delta.IsCheckedChanged += (_, _) => { _d.ProfileDelta = delta.IsChecked == true; Changed(); };
        var sessions = new CheckBox { Content = "Expand endpoints to whole exchange sessions", IsChecked = _d.ProfileWholeSessions };
        sessions.IsCheckedChanged += (_, _) => { _d.ProfileWholeSessions = sessions.IsChecked == true; Changed(); };
        return Section("Fixed-range profile", TimestampRow("From", 0), TimestampRow("Through", 1),
            AnalysisNumber("Maximum rows", _d.ProfileRows, 1, TradeDrawingCalc.MaximumProfileRows, v => _d.ProfileRows = (int)v),
            AnalysisNumber("Value area (%)", _d.ValueAreaPercent, 1, 100, v => _d.ValueAreaPercent = (int)v),
            AnalysisNumber("Profile width (%)", _d.ProfileWidthPercent, 5, 100, v => _d.ProfileWidthPercent = (int)v),
            delta, sessions, Note("Both endpoints are inclusive. Sparse, row-aware CPU calculation preserves decimal volume, including measured compact price/side aggregates. Unknown aggressor volume is included in volume, not delta. Coverage, synthetic estimates and inferred-side limitations are displayed on the chart."));
    }

    private Control PricesSection()
    {
        var rows = new List<Control>();
        int n = _d.Kind is DrawingKind.HorizontalLine or DrawingKind.HorizontalRay or DrawingKind.Text ? 1 : _d.Points.Count;
        for (int i = 0; i < n && i < _d.Points.Count; i++)
        {
            int k = i;
            rows.Add(Labeled(n == 1 ? "Price" : $"Price {k + 1}", PriceBox(_d.Points[k].Price, v => _d.Points[k] = new ChartPoint(_d.Points[k].Time, v))));
        }

        return Section("Coordinates", rows.ToArray());
    }

    private Control VisibilitySection()
    {
        var hidden = new CheckBox { Content = "Hide", IsChecked = _d.Hidden };
        hidden.IsCheckedChanged += (_, _) =>
        {
            _d.Hidden = hidden.IsChecked == true;
            Changed();
        };
        var only = new CheckBox { Content = $"Show only on {_period.DisplayName}", IsChecked = _d.OnlyPeriod != null, };
        if (_d.OnlyPeriod != null && _d.OnlyPeriod != _period.Code) only.Content = $"Show only on {_d.OnlyPeriod} (uncheck to show on all)";
        ToolTip.SetTip(only, "Unchecked: show on all charts for this instrument (all periods)");
        only.IsCheckedChanged += (_, _) =>
        {
            _d.OnlyPeriod = only.IsChecked == true ? _period.Code : null;
            Changed();
        };
        var locked = new CheckBox { Content = "Lock (cannot be moved)", IsChecked = _d.Locked };
        locked.IsCheckedChanged += (_, _) =>
        {
            _d.Locked = locked.IsChecked == true;
            Changed();
        };
        var rows = new List<Control> { hidden, only, locked };
        if (_d.Kind is DrawingKind.HorizontalLine or DrawingKind.HorizontalRay)
        {
            var alert = new CheckBox { Content = "Alert when price touches", IsChecked = _d.IsAlert };
            alert.IsCheckedChanged += (_, _) =>
            {
                _d.Alert = alert.IsChecked == true;
                if (!_d.Alert && _d.Text == "alerta") _d.Text = "";
                Changed();
            };
            rows.Add(alert);
        }

        rows.Add(Note("Drawings appear on all charts for the same instrument and synchronize when moved or edited."));
        return Section("Visibility", rows.ToArray());
    }

    // ------------------------------------------------------------------ colores

    private Control ColorPicker(Func<uint> get, Action<uint> set, bool allowAuto)
    {
        var wrap = new WrapPanel { ItemSpacing = 5, LineSpacing = 5 };
        var hex = new TextBox { Width = 96, Watermark = "#RRGGBB", FontSize = 12 };
        var swatches = new List<(uint Color, Border Ring)>();

        void Refresh()
        {
            uint cur = get();
            foreach (var (c, ring) in swatches) ring.BorderBrush = (c | 0xFF000000) == (cur | 0xFF000000) && cur != 0 ? Ui.Accent : Brushes.Transparent;
            hex.Text = cur == 0 ? "" : $"#{cur & 0xFFFFFF:X6}";
        }

        if (allowAuto)
        {
            var auto = new Button { Content = "Auto", Padding = new Thickness(6, 1), MinHeight = 22, FontSize = 11 };
            ToolTip.SetTip(auto, "Same as line");
            auto.Click += (_, _) =>
            {
                set(0);
                Refresh();
                Changed();
            };
            wrap.Children.Add(auto);
        }

        foreach (var c in Palette)
        {
            var color = c;
            var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(color)) };
            var ring = new Border { Child = swatch, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Padding = new Thickness(1) };
            var b = new Button { Content = ring, Padding = new Thickness(0), MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            b.Click += (_, _) =>
            {
                set(color);
                Refresh();
                Changed();
            };
            swatches.Add((color, ring));
            wrap.Children.Add(b);
        }

        hex.LostFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyHex();
        };

        void ApplyHex()
        {
            var t = (hex.Text ?? "").Trim().TrimStart('#');
            if (t.Length == 6 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                set(0xFF000000 | rgb);
                Changed();
            }

            Refresh();
        }

        wrap.Children.Add(hex);
        Refresh();
        return wrap;
    }

    /// <summary>Botoncito de color que abre la paleta (para los niveles de Fibonacci).</summary>
    private Control SwatchButton(Func<uint> get, Action<uint> set)
    {
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(get())) };
        var panel = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, MaxWidth = 170, Margin = new Thickness(8) };
        var flyout = new Flyout { Content = panel };
        foreach (var c in Palette)
        {
            var color = c;
            var b = new Button { Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(color)) }, Padding = new Thickness(1), MinHeight = 0 };
            b.Click += (_, _) =>
            {
                set(color);
                swatch.Background = new SolidColorBrush(Color.FromUInt32(color));
                flyout.Hide();
            };
            panel.Children.Add(b);
        }

        return new Button { Content = swatch, Padding = new Thickness(2), MinHeight = 0, Flyout = flyout, VerticalAlignment = VerticalAlignment.Center };
    }
}
