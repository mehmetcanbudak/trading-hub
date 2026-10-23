using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.App.Views;

/// <summary>
/// Chart Trader de cada gráfico (como en ATAS): cuenta, cross trading (mirar un instrumento y ejecutar en
/// otro, p. ej. el mini y el micro), cantidad, botones de mercado/ask/bid, cancelar, revertir, breakeven,
/// cerrar, resultado y stop/objetivo automáticos. Cada gráfico opera solo lo suyo.
/// </summary>
internal sealed class ChartTraderPanel : UserControl
{
    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private readonly ComboBox _account = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _cross = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4) };
    private readonly NumericUpDown _qty = new() { Minimum = 0, Maximum = 1_000_000, FormatString = "0.########", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly WrapPanel _presets = new() { ItemSpacing = 4, LineSpacing = 4 };
    private readonly TextBlock _pnl = new() { FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _position = new() { Classes = { "muted", "small" }, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    // alto fijo (un renglón; el texto entero en el tooltip): que cambie con el precio nunca corre los botones de abajo
    private readonly TextBlock _target = new() { FontSize = 11, Height = 16, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Accent };
    private readonly TextBlock _buyAsk = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85 };
    private readonly TextBlock _sellAsk = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85 };
    private readonly TextBlock _buyBid = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85 };
    private readonly TextBlock _sellBid = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85 };
    private readonly ToggleSwitch _brackets = new() { OnContent = "", OffContent = "", MinWidth = 0, Padding = new Thickness(0) };
    private readonly NumericUpDown _sl = new() { Minimum = 0, Maximum = 100000, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _targets = new() { Spacing = 6 };
    private readonly StackPanel _bracketBox = new() { Spacing = 8 };
    private readonly CheckBox _be = new() { Content = "Breakeven at TP1", FontSize = 11.5 };
    private readonly TextBlock _chartLabel = new() { Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly bool _docked;
    private string _templateSig = "";
    private readonly Border _expanded = new();
    private readonly Border _collapsed = new();
    private readonly TextBlock _realWarning = new() { Text = "REAL ACCOUNT", Foreground = Brushes.OrangeRed, FontWeight = FontWeight.Bold, FontSize = 11, IsVisible = false };
    private readonly TextBlock _accountHint = new() { FontSize = 11, Height = 16, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Warn, IsVisible = false };
    private string _settingsKey = "";
    private bool _syncing;

    /// <param name="docked">En el panel de la derecha (junto al DOM): siempre abierto y del ancho del panel.</param>
    public ChartTraderPanel(TradingView host, ChartTab chart, bool docked = false)
    {
        _host = host;
        _chart = chart;
        _docked = docked;
        _expanded.Child = Build();
        if (!docked)
        {
            _expanded.Width = 214;
            _expanded.Background = Ui.Bg1;
            _expanded.BorderBrush = Ui.Line;
            _expanded.BorderThickness = new Thickness(1, 0, 0, 0);
        }

        var open = new Button { Content = Ui.Icon(MaterialIconKind.ChevronLeft, 14), Padding = new Thickness(0), MinHeight = 0, Width = 14, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(open, "Show Chart Trader for this chart");
        open.Click += (_, _) => _host.SetTraderVisible(_chart, true);
        _collapsed.Child = open;
        _collapsed.Background = Ui.Bg1;
        _collapsed.BorderBrush = Ui.Line;
        _collapsed.BorderThickness = new Thickness(1, 0, 0, 0);
        Content = new Panel { Children = { _expanded, _collapsed } };
        ApplyVisible();
    }

    /// <summary>Gráfico al que opera este panel.</summary>
    public ChartTab Chart => _chart;

    public void ApplyVisible()
    {
        _expanded.IsVisible = _docked || _chart.TraderVisible;
        _collapsed.IsVisible = !_docked && !_chart.TraderVisible;
    }

    private Control Build()
    {
        var close = Ui.IconButton(MaterialIconKind.ChevronRight, "Hide Chart Trader for this chart", () => _host.SetTraderVisible(_chart, false), 15);
        var head = new DockPanel { Margin = new Thickness(10, 8, 4, 4) };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = "CHART TRADER", Classes = { "h3" }, VerticalAlignment = VerticalAlignment.Center });

        _account.SelectionChanged += (_, _) =>
        {
            if (_syncing || _account.SelectedItem is not ComboBoxItem { Tag: AccountOption a } || a.Id == _chart.AccountId) return;
            // después de que el selector termine de cerrarse (cambiarlo adentro de su propio evento lo dejaba trabado)
            Dispatcher.UIThread.Post(() => _host.SetChartAccount(_chart, a.Id));
        };
        _cross.Click += (_, _) => ShowCrossPicker();
        ToolTip.SetTip(_cross, "Cross trading: view one chart but route orders to a different instrument (such as ES to MES)");
        _qty.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _chart.Quantity = Math.Max(0, _qty.Value ?? 0);
            _host.OnChartQuantityChanged(_chart);
        };

        Button Trade(string text, TextBlock? sub, string cls, string tip, Action onClick)
        {
            var b = new Button { MinHeight = sub != null ? 40 : 30, Padding = new Thickness(2, 3), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (cls.Length > 0) b.Classes.Add(cls);
            b.Content = sub != null
                ? Ui.V(0, new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center }, sub)
                : new TextBlock { Text = text, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => onClick();
            return b;
        }

        decimal Q() => _chart.Quantity > 0 ? _chart.Quantity : 0;
        var grid = Pairs(
            (Trade("Buy MKT", null, "buy", "Market buy", () => _ = _host.TradeAsync(_chart, OrderTypes.Market, OrderDirections.Buy, 0, Q())),
             Trade("Sell MKT", null, "sell", "Market sell", () => _ = _host.TradeAsync(_chart, OrderTypes.Market, OrderDirections.Sell, 0, Q()))),
            (Trade("Buy Ask", _buyAsk, "buy-soft", "Limit buy at ask (subject to liquidity)", () => AtBook(OrderDirections.Buy, ask: true)),
             Trade("Sell Ask", _sellAsk, "sell-soft", "Limit sell at ask (joins ask queue)", () => AtBook(OrderDirections.Sell, ask: true))),
            (Trade("Buy Bid", _buyBid, "buy-soft", "Limit buy at bid (joins bid queue)", () => AtBook(OrderDirections.Buy, ask: false)),
             Trade("Sell Bid", _sellBid, "sell-soft", "Limit sell at bid (subject to liquidity)", () => AtBook(OrderDirections.Sell, ask: false))),
            (Trade("Cancel buys", null, "", "Cancel buy orders", () => CancelSide(OrderDirections.Buy)),
             Trade("Cancel sells", null, "", "Cancel sell orders", () => CancelSide(OrderDirections.Sell))),
            (Trade("Cancel all", null, "", "Cancel all orders", () => _ = _host.AccountActionAsync(_chart, a => a.CancelAll(), "Orders canceled")),
             Trade("Reverse", null, "", "Reverse position", () => _ = _host.ReverseAsync(_chart))));
        var be = Trade("Breakeven", null, "", "Moves stop to entry price", () => _ = _host.BracketActionAsync(_chart, b => b.MoveStopToBreakeven(), "Move stop to breakeven"));
        // parciales: cierran una parte a mercado; el stop y los objetivos se ajustan solos
        var partials = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,4,*,4,*") };
        partials.Children.Add(new TextBlock { Text = "Close", Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        int col = 1;
        foreach (var pct in new[] { 25, 50, 75 })
        {
            decimal f = pct / 100m;
            var b = Trade($"{pct}%", null, "", $"Closes {pct}% of the position at market (stop and targets adjust)", () => _ = _host.BracketActionAsync(_chart, br => br.ClosePartial(f), $"Close {pct}% of position"));
            b.MinHeight = 26;
            Grid.SetColumn(b, col);
            partials.Children.Add(b);
            col += 2;
        }
        var flat = Trade("Close position", null, "primary", "Closes position at market and cancels orders", () => _ = _host.AccountActionAsync(_chart, a => a.Flatten(), "Position closed"));

        // stop y objetivo automáticos (plantilla del instrumento donde se ejecuta)
        _brackets.IsCheckedChanged += (_, _) => EditTemplate(t => t.Enabled = _brackets.IsChecked == true);
        _sl.ValueChanged += (_, _) => EditTemplate(t => t.StopTicks = (int)(_sl.Value ?? 0));
        _be.IsCheckedChanged += (_, _) => EditTemplate(t => t.BreakevenAfterFirstTarget = _be.IsChecked == true);
        var slHead = new DockPanel();
        DockPanel.SetDock(_brackets, Dock.Right);
        slHead.Children.Add(_brackets);
        slHead.Children.Add(new TextBlock { Text = "Automatic SL / TP", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        ToolTip.SetTip(slHead, "Automatically add stop and target as OCO (ticks from entry)");
        Control TicksRow(string label, IBrush color, NumericUpDown box)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,Auto") };
            g.Children.Add(new TextBlock { Text = label, Foreground = color, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(box, 1);
            g.Children.Add(box);
            var unit = new TextBlock { Text = "ticks", Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(unit, 2);
            g.Children.Add(unit);
            return g;
        }

        var slRow = TicksRow("SL", Ui.Down, _sl);
        var addTarget = new Button { Content = "+ target", Padding = new Thickness(6, 2), MinHeight = 24, FontSize = 11 };
        ToolTip.SetTip(addTarget, "Up to 3 partial targets; each closes a percentage of position");
        addTarget.Click += (_, _) => EditTemplate(t =>
        {
            if (t.Targets.Count >= 3) return;
            if (t.Targets.Count == 0) t.Targets.Add(new BracketTarget { Ticks = Math.Max(1, t.StopTicks * 2), Percent = 100 });
            else t.Targets.Add(new BracketTarget { Ticks = Math.Max(1, t.Targets.Max(x => x.Ticks) * 2) });
            Rebalance(t);
        }, reload: true);
        var apply = new Button { Content = "Apply to position", Padding = new Thickness(6, 2), MinHeight = 24, FontSize = 11 };
        ToolTip.SetTip(apply, "Replace open-position stop and targets with these");
        apply.Click += (_, _) => _ = _host.BracketActionAsync(_chart, b => b.ApplyTemplate(), "Attach stop and targets to position");
        _bracketBox.Children.Add(slHead);
        _bracketBox.Children.Add(slRow);
        _bracketBox.Children.Add(_targets);
        _bracketBox.Children.Add(Ui.H(6, addTarget, apply));
        _bracketBox.Children.Add(_be);

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(10, 0, 10, 12) };
        if (_docked) body.Children.Add(_chartLabel);
        body.Children.Add(Label("Account"));
        body.Children.Add(_account);
        body.Children.Add(_realWarning);
        body.Children.Add(_accountHint);
        body.Children.Add(Label("Cross trading"));
        body.Children.Add(_cross);
        body.Children.Add(_target);
        body.Children.Add(Label("Quantity"));
        body.Children.Add(_qty);
        body.Children.Add(_presets);
        body.Children.Add(grid);
        body.Children.Add(be);
        body.Children.Add(partials);
        body.Children.Add(flat);
        body.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 2) });
        body.Children.Add(_pnl);
        body.Children.Add(_position);
        body.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 2) });
        body.Children.Add(_bracketBox);

        var root = new DockPanel();
        if (!_docked)
        {
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);
        }

        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return root;
    }

    private static TextBlock Label(string text) => new() { Text = text, Classes = { "muted", "small" }, Margin = new Thickness(0, 2, 0, -4) };

    /// <summary>Botones de a dos columnas (como en ATAS).</summary>
    private static Grid Pairs(params (Control Left, Control Right)[] rows)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*"), RowSpacing = 6 };
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(rows[i].Left, i);
            Grid.SetRow(rows[i].Right, i);
            Grid.SetColumn(rows[i].Right, 2);
            g.Children.Add(rows[i].Left);
            g.Children.Add(rows[i].Right);
        }

        return g;
    }

    private void AtBook(OrderDirections dir, bool ask)
    {
        var data = _host.TradeData(_chart);
        if (data == null) return;
        decimal price = ask ? data.BestAsk : data.BestBid;
        if (price <= 0) price = data.LastPrice;
        if (price <= 0)
        {
            _host.SetStatus("No order-book price is available for this order yet");
            return;
        }

        _ = _host.TradeAsync(_chart, OrderTypes.Limit, dir, price, _chart.Quantity);
    }

    private void CancelSide(OrderDirections dir) =>
        _ = _host.AccountActionAsync(_chart, a =>
        {
            foreach (var o in a.ActiveOrders.Where(o => o.Direction == dir).ToList()) a.Cancel(o);
        }, dir == OrderDirections.Buy ? "Buy orders canceled" : "Sell orders canceled");

    private BracketTemplate? Template() => _host.TradeData(_chart) is { } data ? _host.Services.Settings.BracketTemplateFor(data.Instrument.Key) : null;

    private void EditTemplate(Action<BracketTemplate> edit, bool reload = false)
    {
        if (_syncing || Template() is not { } t) return;
        edit(t);
        _host.Services.Settings.Save();
        _templateSig = Sig(t);
        if (reload) LoadTemplate(t);
    }

    private static string Sig(BracketTemplate t) =>
        $"{t.Enabled}|{t.StopTicks}|{t.BreakevenAfterFirstTarget}|{string.Join(",", t.Targets.Select(x => x.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + x.Percent.ToString(CultureInfo.InvariantCulture)))}";

    /// <summary>Reparte el 100% entre los objetivos (el último se lleva el resto).</summary>
    private static void Rebalance(BracketTemplate t)
    {
        if (t.Targets.Count == 0) return;
        decimal each = Math.Floor(100m / t.Targets.Count);
        for (int i = 0; i < t.Targets.Count; i++) t.Targets[i].Percent = i == t.Targets.Count - 1 ? 100 - each * (t.Targets.Count - 1) : each;
    }

    /// <summary>Muestra la plantilla de stop y objetivos (la misma en el panel del gráfico y en el de la derecha).</summary>
    private void LoadTemplate(BracketTemplate t)
    {
        _syncing = true;
        _brackets.IsChecked = t.Enabled;
        _sl.Value = t.StopTicks;
        _be.IsChecked = t.BreakevenAfterFirstTarget;
        _targets.Children.Clear();
        int n = Math.Max(1, t.Targets.Count);
        for (int i = 0; i < n; i++)
        {
            int index = i;
            var target = i < t.Targets.Count ? t.Targets[i] : null;
            var ticks = new NumericUpDown { Minimum = 0, Maximum = 100000, Increment = 1, FormatString = "0", Value = target?.Ticks ?? 0, HorizontalAlignment = HorizontalAlignment.Stretch, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
            ticks.ValueChanged += (_, _) => EditTemplate(tt =>
            {
                int v = (int)(ticks.Value ?? 0);
                if (index < tt.Targets.Count)
                {
                    if (v <= 0 && tt.Targets.Count == 1) tt.Targets.Clear(); // 0 = sin objetivo
                    else tt.Targets[index].Ticks = Math.Max(1, v);
                }
                else if (v > 0) tt.Targets.Add(new BracketTarget { Ticks = v, Percent = 100 });
            });
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(n > 1 ? "30,*,4,46,2,20" : "30,*,Auto") };
            g.Children.Add(new TextBlock { Text = n > 1 ? $"TP{i + 1}" : "TP", Foreground = Ui.Up, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(ticks, 1);
            g.Children.Add(ticks);
            if (n > 1)
            {
                var pct = new NumericUpDown { Minimum = 1, Maximum = 100, Increment = 5, FormatString = "0", Value = target?.Percent ?? 100, ShowButtonSpinner = false, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
                ToolTip.SetTip(pct, "Percentage of the position closed by this target");
                pct.ValueChanged += (_, _) => EditTemplate(tt =>
                {
                    if (index < tt.Targets.Count) tt.Targets[index].Percent = pct.Value ?? 100;
                });
                Grid.SetColumn(pct, 3);
                g.Children.Add(pct);
                var remove = new Button { Content = "×", Padding = new Thickness(0), MinHeight = 0, Width = 20, Height = 24, FontSize = 13, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
                ToolTip.SetTip(remove, "Remove this target");
                remove.Click += (_, _) => EditTemplate(tt =>
                {
                    if (index < tt.Targets.Count) tt.Targets.RemoveAt(index);
                    Rebalance(tt);
                }, reload: true);
                Grid.SetColumn(remove, 5);
                g.Children.Add(remove);
            }
            else
            {
                var unit = new TextBlock { Text = "ticks", Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                Grid.SetColumn(unit, 2);
                g.Children.Add(unit);
            }

            _targets.Children.Add(g);
        }

        _templateSig = Sig(t);
        _syncing = false;
    }

    // ------------------------------------------------------------------ cross trading

    private async void ShowCrossPicker()
    {
        var list = new ListBox { MaxHeight = 320, Width = 260 };
        var search = new TextBox { Watermark = "Search instruments (MES, MNQ, BTCUSDC…)" };
        var flyout = new Flyout { Placement = PlacementMode.LeftEdgeAlignedTop };
        var none = new ListBoxItem { Content = $"None · execute on {_chart.Symbol}", Tag = "" };
        IReadOnlyList<Instrument> instruments = Array.Empty<Instrument>();
        try
        {
            instruments = await InstrumentPicker.InstrumentsAsync(_host.Services, _chart.FeedId);
        }
        catch (Exception ex)
        {
            _host.SetStatus("Could not retrieve instruments: " + ex.Message);
        }

        void Fill()
        {
            var q = (search.Text ?? "").Trim();
            list.Items.Clear();
            list.Items.Add(none);
            // primero los "hermanos" del instrumento (mismo subyacente: ES ↔ MES, NQ ↔ MNQ, BTC…)
            var root = Root(_chart.Symbol);
            foreach (var i in instruments
                         .Where(i => !i.Symbol.Equals(_chart.Symbol, StringComparison.OrdinalIgnoreCase))
                         .Where(i => q.Length == 0 || i.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(i => Root(i.Symbol) == root ? 0 : 1)
                         .Take(200))
            {
                list.Items.Add(new ListBoxItem
                {
                    Tag = i.Symbol,
                    Content = Ui.V(0, new TextBlock { Text = i.Symbol, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = i.Description, Classes = { "faint", "small" } }),
                });
            }
        }

        search.TextChanged += (_, _) => Fill();
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: string symbol }) return;
            flyout.Hide();
            await _host.SetCrossTradingAsync(_chart, symbol.Length == 0 ? null : symbol);
        };
        Fill();
        flyout.Content = Ui.V(8, Ui.H3("Execute on"), search, list, Ui.Faint("Orders, stops and targets route to the selected instrument using their original numerical prices (for example mini/micro contracts).")).Also(v =>
        {
            v.Margin = new Thickness(12);
            v.Width = 280;
        });
        flyout.ShowAt(_cross);
        search.Focus();
    }

    /// <summary>Subyacente aproximado para ordenar sugerencias (MESZ6 → ES, MNQ → NQ, BTCUSDC → BTC).</summary>
    private static string Root(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        if (s.StartsWith('M') && s.Length > 2 && char.IsLetter(s[1]) && char.IsLetter(s[2])) s = s[1..];
        foreach (var quote in new[] { "USDT", "USDC", "BUSD", "USD" })
            if (s.EndsWith(quote, StringComparison.Ordinal) && s.Length > quote.Length) return s[..^quote.Length];
        return s.Length > 2 ? s[..2] : s;
    }

    // ------------------------------------------------------------------ refresco

    /// <summary>Vuelve a armar cuentas, cantidades sugeridas y plantilla (cambió el instrumento o la cuenta).</summary>
    public void Rebind()
    {
        _settingsKey = "";
        Refresh();
    }

    public void SyncQuantity()
    {
        _syncing = true;
        _qty.Value = _chart.Quantity;
        _syncing = false;
    }

    /// <summary>Se llama unas veces por segundo: posición, resultado y precios del libro.</summary>
    public void Refresh()
    {
        var model = _chart.Model;
        if (model == null || !_expanded.IsVisible) return;
        var data = model.Data;
        var tradeData = _host.TradeData(_chart) ?? data;
        var inst = tradeData.Instrument;

        // cuentas: la lista se rehace solo si cambió y la elegida se marca sin tocar las demás
        var accounts = _host.AccountOptions(_chart, data);
        _syncing = true;
        Ui.SyncAccounts(_account, accounts, _chart.AccountId);
        _syncing = false;
        ToolTip.SetTip(_account, (_account.SelectedItem as ComboBoxItem)?.Content as string); // el nombre entero (el panel es angosto)
        _realWarning.IsVisible = accounts.FirstOrDefault(a => a.Id == _chart.AccountId)?.IsReal == true;

        // cantidades y stop/objetivo del instrumento (solo si cambió el instrumento o la cuenta)
        string key = inst.Key + "#" + _chart.AccountId;
        if (key != _settingsKey)
        {
            _settingsKey = key;
            _syncing = true;
            decimal step = inst.VolumeStep > 0 ? inst.VolumeStep : 1;
            decimal[] presets = step >= 1 ? new[] { 1m, 2m, 5m, 10m } : new[] { step, step * 10, step * 100, step * 1000 };
            _presets.Children.Clear();
            foreach (var v in presets)
            {
                var b = new Button { Content = v.ToString("0.####", CultureInfo.InvariantCulture), Padding = new Thickness(7, 2), MinHeight = 22, FontSize = 11 };
                b.Click += (_, _) => _qty.Value = v;
                _presets.Children.Add(b);
            }

            _qty.Increment = step;
            if (_chart.Quantity <= 0) _chart.Quantity = presets[0];
            _qty.Value = _chart.Quantity;
            _syncing = false;
            LoadTemplate(_host.Services.Settings.BracketTemplateFor(inst.Key));
        }
        else if (!_bracketBox.IsKeyboardFocusWithin && Sig(_host.Services.Settings.BracketTemplateFor(inst.Key)) != _templateSig)
        {
            // la cambió el otro panel (el del gráfico o el de la derecha)
            LoadTemplate(_host.Services.Settings.BracketTemplateFor(inst.Key));
        }

        if (_docked) _chartLabel.Text = $"Trades on active chart: {_chart.Symbol} · {model.Period.DisplayName}";

        // cuentas de Rithmic: si todavía no aparecen, se dice por qué (conectando, rechazo…). En los gráficos de Rithmic el
        // renglón queda reservado aunque esté vacío: que el aviso aparezca o se vaya nunca corre los botones de abajo
        var rithmic = _host.Services.RithmicStatusFor(data);
        _accountHint.Text = rithmic ?? "";
        ToolTip.SetTip(_accountHint, rithmic);
        _accountHint.IsVisible = _host.Services.Connections.Enabled.Any(x => x.IsRithmic && x.MarketDataId == data.Instrument.FeedId);

        _cross.Content = _chart.CrossSymbol is { } cs
            ? Ui.H(6, Ui.Icon(MaterialIconKind.SwapHorizontal, 14, Ui.Accent), new TextBlock { Text = cs, FontWeight = FontWeight.SemiBold })
            : Ui.H(6, Ui.Icon(MaterialIconKind.SwapHorizontal, 14, Ui.Text2), new TextBlock { Text = "None", Classes = { "muted" } });
        if (_chart.CrossSymbol != null)
        {
            decimal a = data.LastPrice, b = tradeData.LastPrice;
            bool far = a > 0 && b > 0 && Math.Abs(a - b) / a > 0.001m;
            _target.Text = $"Viewing {_chart.Symbol}, executes on {_chart.CrossSymbol}" + (far ? $" · warning: prices differ ({inst.FormatPrice(b)})" : "");
            ToolTip.SetTip(_target, _target.Text);
            _target.Foreground = far ? Ui.Warn : Ui.Accent;
            _target.IsVisible = true;
        }
        else
        {
            _target.IsVisible = false;
        }

        string Px(decimal p) => p > 0 ? inst.FormatPrice(p) : "—";
        _buyAsk.Text = Px(tradeData.BestAsk);
        _sellAsk.Text = Px(tradeData.BestAsk);
        _buyBid.Text = Px(tradeData.BestBid);
        _sellBid.Text = Px(tradeData.BestBid);

        var acc = _host.AccountOf(_chart);
        var pos = acc?.Position;
        if (pos == null || pos.IsFlat)
        {
            decimal realized = Math.Round(pos?.RealizedPnL ?? 0, 2);
            _pnl.Text = realized != 0 ? $"{realized:+0.00;-0.00}" : "No position";
            _pnl.Foreground = realized > 0 ? Ui.Up : realized < 0 ? Ui.Down : Ui.Text1;
            _pnl.FontSize = realized != 0 ? 18 : 13;
            int orders = acc?.ActiveOrders.Count() ?? 0;
            _position.Text = (realized != 0 ? "Flat · today's PnL" : "Flat") + (orders > 0 ? $" · {orders} order(s)" : "");
        }
        else
        {
            _pnl.Text = $"{pos.UnrealizedPnL:+0.00;-0.00;0.00}";
            _pnl.Foreground = pos.UnrealizedPnL >= 0 ? Ui.Up : Ui.Down;
            _pnl.FontSize = 18;
            int orders = acc!.ActiveOrders.Count();
            _position.Text = $"{(pos.Volume > 0 ? "LONG" : "SHORT")} {Math.Abs(pos.Volume).ToString("0.########", CultureInfo.InvariantCulture)} @ {inst.FormatPrice(pos.AveragePrice)}" + (orders > 0 ? $" · {orders} order(s)" : "");
        }
    }
}
