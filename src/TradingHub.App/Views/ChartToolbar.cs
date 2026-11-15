using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Data;

namespace TradingHub.App.Views;

/// <summary>
/// Barra de un gráfico: instrumento, historia, período, estilo, columnas del footprint y ticks por fila.
/// La usan la ventana principal (sobre el gráfico activo) y cada ventana de gráfico separada.
/// </summary>
internal sealed class ChartToolbar : UserControl
{
    private readonly TradingView _host;
    private readonly bool _floating;
    // períodos: los favoritos en fila y un selector con todos (cada uno con su estrella)
    private readonly Border _periodsHost = new() { VerticalAlignment = VerticalAlignment.Center };
    private Ui.Segmented<string>? _periods;
    private string _favSig = "";
    private readonly Button _periodMenu = new() { MinWidth = 72, Padding = new Thickness(9, 4) };
    private readonly TextBlock _periodMenuText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Flyout _periodFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };

    // tipo de gráfico: un selector al lado de Indicadores
    private readonly ChoiceButton<ChartStyle> _styleBox;
    private readonly ComboBox _layoutBox = new() { MinWidth = 190, MaxWidth = 240 };
    private readonly ChoiceButton<int> _stepBox;
    private readonly ChoiceButton<double> _historyBox;
    private ChartTab? _chart;
    private bool _syncing;
    private readonly LinkButton _link;
    private readonly TextBlock _clock = new() { FontFamily = Ui.MonoFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _clockHost = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _practiceLabel = new() { Text = "Practice", FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };

    /// <summary>Todos los períodos del selector, por grupo.</summary>
    internal static readonly (string Title, string[] Codes)[] PeriodGroups =
    {
        ("Seconds", new[] { "S5", "S10", "S15", "S30" }),
        ("Minutes", new[] { "M1", "M2", "M3", "M5", "M10", "M15", "M30" }),
        ("Hours", new[] { "H1", "H2", "H4", "H6", "H12" }),
        ("Days", new[] { "Daily", "Weekly", "Monthly" }),
        ("Ticks", new[] { "Tick100", "Tick250", "Tick500", "Tick1000" }),
        ("Volume", new[] { "Volume100", "Volume500", "Volume1000" }),
        ("Range and other", new[] { "Range10", "Range20", "Delta50", "Delta100", "Renko10" }),
    };

    private static string PeriodLabel(string code) => ChartPeriod.TryParse(code, out var p) ? p.DisplayName : code;

    /// <summary>Opciones especiales del botón de historia.</summary>
    internal const double AllHistory = -1, CustomHistory = -2;

    internal static readonly (double Hours, string Label)[] HistoryOptions =
    {
        (0.5, "30 min"), (1, "1 h"), (1.5, "1,5 h"), (3, "3 h"), (6, "6 h"), (12, "12 h"), (24, "1 day"), (72, "3 days"), (168, "7 days"),
        (720, "30 days"), (2160, "90 days"), (4380, "6 months"), (8760, "1 year"), (17520, "2 years"), (43800, "5 years"),
        (AllHistory, "All history"), (CustomHistory, "From a date…"),
    };

    public ChartToolbar(TradingView host, bool floating, params Control[] rightControls)
    {
        _host = host;
        _floating = floating;
        _link = new LinkButton(host, () => _chart);
        Picker = new InstrumentPicker(host.Services);
        Picker.Picked += async (profileId, inst, newTab) =>
        {
            var c = _chart;
            if (newTab || c == null) await _host.NewTabAsync(profileId, inst.Symbol, c?.Model?.Period ?? ChartPeriod.M5, c?.Model?.Settings.Clone());
            else await _host.OpenInTabAsync(c, profileId, inst.Symbol, c.Model?.Period ?? ChartPeriod.M5, c.Model?.Settings.Clone());
        };
        _periodMenu.Content = Ui.H(6, _periodMenuText, Ui.Icon(MaterialIconKind.ChevronDown, 14, Ui.Text2));
        _periodMenu.Flyout = _periodFlyout;
        ToolTip.SetTip(_periodMenu, "All periods (seconds, minutes, hours, days, ticks, volume, range…). Right-click to pin or unpin a period on the toolbar.");
        _periodFlyout.Opening += (_, _) => _periodFlyout.Content = BuildPeriodMenu();
        RebuildFavorites();
        _styleBox = new ChoiceButton<ChartStyle>(MaterialIconKind.ChartBoxOutline, new[]
        {
            (ChartStyle.Candles, "Candles"),
            (ChartStyle.Bars, "Bars"),
            (ChartStyle.Line, "Line"),
            (ChartStyle.HeikinAshi, "Heikin Ashi"),
            (ChartStyle.Footprint, "Footprint"),
        }, "Chart style: candles, bars, line, Heikin Ashi or footprint");
        _styleBox.Changed += SetStyle;
        _stepBox = new ChoiceButton<int>(MaterialIconKind.FormatLineSpacing,
            new[] { 0, 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000 }.Select(n => (n, n == 0 ? "Auto" : $"{n} tick{(n > 1 ? "s" : "")}")),
            "Footprint ticks per row (Auto adapts to zoom)");
        _historyBox = new ChoiceButton<double>(MaterialIconKind.History,
            HistoryOptions.Select(h => (h.Hours, h.Label)),
            "History to load, up to all available history. Recent data uses ticks with footprint; older data uses exchange 1-minute bars. History downloaded in Data uses ticks.",
            h => h == AllHistory ? "All" : HistoryOptions.First(x => x.Hours == h).Label);
        Content = Build(rightControls);
        Hook();
        RefreshLayoutBox();
    }

    public InstrumentPicker Picker { get; }

    /// <summary>Gráfico sobre el que actúa la barra.</summary>
    public ChartTab? Chart
    {
        get => _chart;
        set
        {
            _chart = value;
            Sync();
        }
    }

    // indicators: the button shows how many the chart has, next to the favorites star
    private readonly TextBlock _indicatorCount = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _indicatorBadge = new() { Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(8), MinWidth = 18, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Width below which labels collapse to icons, and below which pinned periods fold into the period menu.</summary>
    internal const double CompactWidth = 1700, NarrowWidth = 1380;

    /// <summary>The thin separator between groups.</summary>
    private static Control Sep() => new Border { Width = 1, Height = 22, Margin = new Thickness(2, 0), Background = Ui.Line, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Group(params Control[] items)
    {
        var g = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in items)
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(c);
        }

        return g;
    }

    private static Button LabeledButton(MaterialIconKind icon, string text, string tip, out TextBlock label, params Control[] extra)
    {
        label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        var content = Ui.H(6, Ui.Icon(icon, 17), label);
        foreach (var c in extra) content.Children.Add(c);
        var b = new Button { Content = content, Padding = new Thickness(8, 4) };
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    private Control Build(Control[] rightControls)
    {
        ToolTip.SetTip(_layoutBox, "Footprint bar composition");
        _clockHost.Child = _clock;

        // analysis next to the chart style: indicators (with count), favorites, navigation, practice
        _indicatorBadge.Child = _indicatorCount;
        _indicatorBadge.Background = Ui.Bg3;
        var indicators = LabeledButton(MaterialIconKind.FunctionVariant, "Indicators", "Chart indicators and library (the number: how many this chart has)", out var indicatorsText, _indicatorBadge);
        indicators.Click += (_, _) => _host.OpenIndicators(_chart);
        Button? favorites = null;
        favorites = Ui.IconButton(MaterialIconKind.StarOutline, "Favorite indicators and chart templates (in Indicators, right-click → Add to favorites)", () => _host.ShowFavoritesMenu(_chart, favorites!));
        Avalonia.Automation.AutomationProperties.SetName(favorites, "Favorite indicators and chart templates");
        Button? navigation = null;
        navigation = LabeledButton(MaterialIconKind.Magnify, "View", "Chart navigation and clock", out var navigationText);
        navigation.Click += (_, _) => _host.ShowChartNavigation(_chart, navigation!);
        Avalonia.Automation.AutomationProperties.SetName(navigation, "Chart navigation and clock");
        var practice = LabeledButton(MaterialIconKind.Play, "Practice replay…", "Practice replay", out var practiceText);
        practice.Click += (_, _) => _host.OpenPracticeDialog();

        // tighter buttons so the bar fits one row
        foreach (var b in new Button[] { _styleBox, _stepBox, _historyBox }) b.Padding = new Thickness(8, 4);
        _layoutBox.Padding = new Thickness(8, 4, 4, 4);

        var screenshot = Ui.IconButton(MaterialIconKind.CameraOutline, "Chart screenshot (PNG in Pictures)", () => _host.SaveChartImage(_chart));
        var fullScreen = Ui.IconButton(MaterialIconKind.Fullscreen, "Full screen", () => _host.ToggleFullScreen(_chart));
        var linkHost = new Border { Child = _link, VerticalAlignment = VerticalAlignment.Center };

        // narrow windows collapse labels and secondary buttons and fold the pinned periods into the period menu (the
        // bar stays one row; each chart header keeps its own link button)
        SizeChanged += (_, e) => Compact(e.NewSize.Width);
        void Compact(double width)
        {
            bool wide = width >= CompactWidth, roomy = width >= NarrowWidth;
            indicatorsText.IsVisible = navigationText.IsVisible = practiceText.IsVisible = wide;
            _periodsHost.IsVisible = _clockHost.IsVisible = roomy;
            linkHost.IsVisible = screenshot.IsVisible = fullScreen.IsVisible = roomy;
            _layoutBox.MinWidth = wide ? 150 : 120;
            _layoutBox.MaxWidth = wide ? 190 : 130;
        }

        // wraps a whole group only if even the compact bar cannot fit (never cuts buttons)
        var left = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in new Control[]
                 {
                     Group(linkHost, Picker, _practiceLabel), Sep(),
                     Group(_periodsHost, _periodMenu, _historyBox), Sep(),
                     Group(_styleBox, _layoutBox, _stepBox), Sep(),
                     Group(indicators, favorites, navigation, _clockHost, practice),
                 })
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(c);
        }

        // right: chart actions (strategies, settings, screenshot, full screen), then the window controls
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Button? strategies = null;
        strategies = Ui.IconButton(MaterialIconKind.RobotOutline, "Chart strategies: historical and live entry/exit marks, visual-only or executing on an account", () => _host.ShowStrategyMenu(_chart, strategies!));
        right.Children.Add(strategies);
        right.Children.Add(Ui.IconButton(MaterialIconKind.PaletteOutline, "Chart settings: background, candle and axis colors…", () => _host.OpenChartSettings(_chart)));
        right.Children.Add(screenshot);
        right.Children.Add(fullScreen);
        foreach (var c in rightControls) right.Children.Add(c);

        var bar = new DockPanel { Margin = new Thickness(10, 6), LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(left);
        return new Border { Child = bar }.Classes("toolbar");
    }

    /// <summary>How many indicators the chart has (on the button).</summary>
    private void UpdateIndicatorCount()
    {
        int n = _chart?.Model?.Indicators.Count ?? 0;
        string text = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_indicatorCount.Text == text) return;
        _indicatorCount.Text = text;
        _indicatorCount.Foreground = n > 0 ? Ui.Accent : Ui.Text2;
    }

    private void Hook()
    {
        _layoutBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || _layoutBox.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is string s && s == "edit")
            {
                _host.OpenFootprintEditor(_chart);
                Sync();
                return;
            }

            if (_chart?.Model == null || item.Tag is not FootprintLayout layout) return;
            _chart.Model.Settings.Layout = layout.Clone();
            if (_chart.Model.Settings.Style != ChartStyle.Footprint) SetStyle(ChartStyle.Footprint);
            _chart.Model.Invalidate();
        };

        _stepBox.Changed += n =>
        {
            if (_syncing || _chart?.Model == null) return;
            if (n != _chart.Model.Settings.PriceStep) _chart.Model.SetPriceStep(n);
        };

        _historyBox.Changed += hours =>
        {
            if (_syncing || _chart?.Model == null) return;
            if (hours == CustomHistory)
            {
                ShowCustomHistory();
                return;
            }

            _ = _host.LoadHistoryAsync(_chart, hours);
        };
    }

    /// <summary>Elegir desde qué fecha cargar.</summary>
    private void ShowCustomHistory()
    {
        var chart = _chart;
        if (chart?.Model == null) return;
        var picker = new CalendarDatePicker
        {
            SelectedDate = DateTime.Today.AddMonths(-3),
            DisplayDateEnd = DateTime.Today,
            Width = 200,
        };
        var load = Ui.Button("Load", MaterialIconKind.Download, () => { }, "primary");
        var panel = Ui.V(10, Ui.H3("Load history from"), picker, Ui.Faint("Retrieved automatically from the exchange (older history uses 1-minute bars)."), load);
        panel.Margin = new Thickness(14);
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        load.Click += (_, _) =>
        {
            if (picker.SelectedDate is not { } d) return;
            flyout.Hide();
            double hours = Math.Max(1, (DateTime.UtcNow - DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)).TotalHours);
            _ = _host.LoadHistoryAsync(chart, hours);
        };
        flyout.ShowAt(_historyBox);
        Sync();
    }

    /// <summary>Texto corto para un rango de horas (24 → «1 día», 2000 → «83 días»).</summary>
    internal static string SpanText(double hours) =>
        hours == AllHistory ? "all history"
        : HistoryOptions.FirstOrDefault(h => Math.Abs(h.Hours - hours) < 0.01).Label is { } l ? l
        : hours < 48 ? $"{hours:0.#} h" : $"{hours / 24:0} days";

    /// <summary>Los favoritos en fila (se rehace solo si cambiaron).</summary>
    private void RebuildFavorites()
    {
        var favorites = _host.Services.Settings.FavoritePeriods.Where(c => ChartPeriod.TryParse(c, out _)).ToList();
        string sig = string.Join(",", favorites);
        if (sig == _favSig && _periods != null) return;
        _favSig = sig;
        // en el orden de duración (segundos, minutos, horas, días; después ticks, volumen…)
        var ordered = PeriodGroups.SelectMany(g => g.Codes).Where(favorites.Contains).Concat(favorites.Where(c => !PeriodGroups.Any(g => g.Codes.Contains(c)))).ToList();
        _periods = new Ui.Segmented<string>(ordered.Select(c => (c, PeriodLabel(c), (string?)null)));
        _periods.SelectionChanged += code => SetPeriod(ChartPeriod.Parse(code));
        _periodsHost.Child = ordered.Count > 0 ? _periods : null;
    }

    private void ToggleFavorite(string code)
    {
        var s = _host.Services.Settings;
        if (!s.FavoritePeriods.Remove(code)) s.FavoritePeriods.Add(code);
        s.Save();
        Sync();
    }

    /// <summary>El selector de períodos: todos por grupo, con su estrella para la barra, y uno a medida.</summary>
    private Control BuildPeriodMenu()
    {
        var favorites = _host.Services.Settings.FavoritePeriods;
        var current = _chart?.Model?.Period;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*"), RowSpacing = 10 };
        int row = 0;
        foreach (var (title, codes) in PeriodGroups)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = title, Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row);
            grid.Children.Add(label);
            var wrap = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            foreach (var code in codes)
            {
                bool fav = favorites.Contains(code);
                bool isCurrent = current != null && ChartPeriod.TryParse(code, out var cp) && cp == current;
                var pick = new Button { Content = PeriodLabel(code), Padding = new Thickness(8, 3), MinHeight = 26, MinWidth = 46, FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center };
                if (isCurrent) pick.Classes.Add("primary");
                else if (fav)
                {
                    // los que están en la barra, con el color del acento
                    pick.Foreground = Ui.Accent;
                    pick.FontWeight = Avalonia.Media.FontWeight.SemiBold;
                }

                ToolTip.SetTip(pick, fav ? "Pinned · right-click to unpin" : "Right-click to pin to toolbar");
                var c = code;
                pick.Click += (_, _) =>
                {
                    _periodFlyout.Hide();
                    SetPeriod(ChartPeriod.Parse(c));
                };
                // clic derecho: lo pone o lo saca de la barra
                pick.AddHandler(PointerReleasedEvent, (_, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Right) return;
                    e.Handled = true;
                    ToggleFavorite(c);
                    _periodFlyout.Content = BuildPeriodMenu();
                }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                wrap.Children.Add(pick);
            }

            Grid.SetRow(wrap, row);
            Grid.SetColumn(wrap, 1);
            grid.Children.Add(wrap);
            row++;
        }

        var custom = new TextBox { Watermark = "Custom: M3, H2, Tick500, Range20…", Width = 240 };
        custom.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (ChartPeriod.TryParse(custom.Text, out var p))
            {
                _periodFlyout.Hide();
                SetPeriod(p);
            }
            else _host.SetStatus("Invalid period. Examples: M3, H2, Tick500, Volume100, Range20");
        };

        var body = Ui.V(12, Ui.H3("Period"), grid, new Border { Height = 1, Background = Ui.Line }, Ui.H(10, custom, Ui.Faint("Press Enter to use")),
            new TextBlock { Text = "Right-click a period to pin or unpin it. Pinned periods are highlighted.", Classes = { "faint", "small" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Margin = new Thickness(16, 14, 16, 14);
        body.Width = 470;
        return body;
    }

    public void RefreshLayoutBox()
    {
        _syncing = true;
        _layoutBox.Items.Clear();
        foreach (var p in FootprintLayout.Presets) _layoutBox.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p });
        foreach (var p in FootprintLayout.LoadUserTemplates()) _layoutBox.Items.Add(new ComboBoxItem { Content = "★ " + p.DisplayName, Tag = p });
        _layoutBox.Items.Add(new ComboBoxItem { Content = "Edit columns…", Tag = "edit" });
        _syncing = false;
        Sync();
    }

    internal void SetStyle(ChartStyle s)
    {
        if (_chart?.Model == null) return;
        var m = _chart.Model;
        m.Settings.Style = s;
        if (s == ChartStyle.Footprint && m.Viewport.BarWidth < 40) m.Viewport.BarWidth = 110;
        if (s != ChartStyle.Footprint && m.Viewport.BarWidth > 30) m.Viewport.BarWidth = 8;
        m.Rebuild();
        Sync();
    }

    internal void SetPeriod(ChartPeriod p)
    {
        if (_chart?.Model == null) return;
        _chart.Model.SetPeriod(p);
        Sync();
        _host.OnPeriodChanged(_chart);
    }

    internal void RefreshLink() => _link.Refresh();

    public void Sync()
    {
        var c = _chart;
        if (c?.Model == null) return;
        _syncing = true;
        var m = c.Model;
        Picker.IsVisible = !m.Settings.BlindReplay;
        _practiceLabel.IsVisible = m.Settings.BlindReplay;
        _historyBox.IsVisible = !m.Settings.BlindReplay;
        _link.IsVisible = !m.Settings.BlindReplay;
        Picker.SetCurrent(c.FeedId, m.Instrument);
        RebuildFavorites();
        var favorites = _host.Services.Settings.FavoritePeriods;
        string? favCode = favorites.FirstOrDefault(code => ChartPeriod.TryParse(code, out var fp) && fp == m.Period);
        _periods?.Select(favCode);
        _periodMenuText.Text = m.Period.DisplayName;
        _periodMenuText.Foreground = favCode == null ? Ui.Accent : Ui.Text1; // si no está en la barra, el selector lo muestra marcado
        _styleBox.Select(m.Settings.Style);
        _layoutBox.IsVisible = m.Settings.Style == ChartStyle.Footprint;
        _stepBox.IsVisible = m.Settings.Style == ChartStyle.Footprint;
        _layoutBox.SelectedItem = _layoutBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is FootprintLayout l && l.Name == m.Settings.Layout.Name);
        _stepBox.Select(m.Settings.PriceStep);
        double hours = c.HistoryHours != 0 ? c.HistoryHours : _host.Services.HistoryFor(c.FeedId).TotalHours;
        if (hours == AllHistory) _historyBox.Select(AllHistory);
        else
        {
            var near = HistoryOptions.Where(h => h.Hours > 0).OrderBy(h => Math.Abs(h.Hours - hours)).First();
            _historyBox.Select(near.Hours);
            // rango a medida (desde una fecha): muestra los días
            if (Math.Abs(near.Hours - hours) > hours * 0.05) _historyBox.ShowText(SpanText(hours));
        }
        UpdateIndicatorCount();
        _syncing = false;
        _link.Refresh();
        _host.UpdateTitles(c);
    }

    /// <summary>Precio y variación desde el inicio de la sesión en el selector de instrumento.</summary>
    public void UpdateQuote()
    {
        var model = _chart?.Model;
        if (model == null) return;
        UpdateIndicatorCount();
        var data = model.Data;
        decimal change = 0;
        if (model.Series.Count > 0)
        {
            int i = model.Series.Count - 1;
            while (i > 0 && !model.Series.Session.IsNewSession(model.Series[i - 1].Time, model.Series[i].Time)) i--;
            decimal open = model.Series.TicksToPrice(model.Series[i].OpenTicks);
            if (open > 0) change = (data.LastPrice - open) / open * 100m;
        }

        Picker.SetQuote(data.LastPrice, change, data.Instrument);
        _clock.IsVisible = model.Settings.ShowClock;
        if (_clock.IsVisible)
        {
            DateTime utc = data.Feed is TradingHub.Feeds.ReplayFeed replay ? replay.Clock : DateTime.UtcNow;
            _clock.Text = model.Settings.BlindReplay ? model.Settings.ReplayElapsed(utc) :
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), model.Settings.TimeZone).ToString("HH:mm:ss", TradingHub.Common.UiCulture.Current);
            ToolTip.SetTip(_clock, model.Settings.BlindReplay ? "Practice elapsed time" : model.Settings.TimeZone.Id + (data.Feed is TradingHub.Feeds.ReplayFeed ? " · replay clock" : " · local system clock"));
        }
    }
}
