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
using TradingHub.App.Windows;
using TradingHub.Feeds.Rithmic;

namespace TradingHub.App.Views;

/// <summary>
/// Conexiones: perfiles de datos y cuentas (Rithmic, Binance, Bybit, Interactive Brokers y simulador).
/// Las contraseñas y API secrets se guardan en el Llavero de macOS, nunca en archivos.
/// </summary>
internal sealed class ConnectionsView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly ListBox _list = new();
    private readonly ContentControl _detail = new();
    private readonly Dictionary<string, (bool Ok, string Message, DateTime When)> _results = new();
    private ConnectionProfile? _selected;
    private CancellationTokenSource? _testCts;

    public ConnectionsView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        Content = Build();
        FillList();
        _svc.Connections.Changed += FillList;
    }

    public void OnShown() => FillList();

    public void OnTimer()
    {
    }

    private static MaterialIconKind IconOf(ConnectionKind k) => k switch
    {
        ConnectionKind.Simulator => MaterialIconKind.FlaskOutline,
        ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot => MaterialIconKind.Bitcoin,
        ConnectionKind.Bybit => MaterialIconKind.CurrencyUsd,
        ConnectionKind.Rithmic => MaterialIconKind.ChartTimelineVariant,
        ConnectionKind.AmpFutures => MaterialIconKind.ChartBellCurveCumulative,
        ConnectionKind.Tradovate => MaterialIconKind.ChartLineVariant,
        ConnectionKind.CTrader => MaterialIconKind.ChartAreaspline,
        ConnectionKind.Oanda => MaterialIconKind.Earth,
        ConnectionKind.Alpaca => MaterialIconKind.Finance,
        ConnectionKind.Databento => MaterialIconKind.DatabaseArrowDownOutline,
        _ => MaterialIconKind.Bank,
    };

    private Control Build()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };

        // ---- lista
        var add = new Button { Content = Ui.H(6, Ui.Icon(MaterialIconKind.Plus, 16), new TextBlock { Text = "New connection" }), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var menu = new MenuFlyout();
        foreach (var kind in Enum.GetValues<ConnectionKind>())
        {
            // qué da cada una, para elegir sin tener que saberlo
            string? hint = kind switch
            {
                ConnectionKind.Tradovate => "futures: trading accounts and data",
                ConnectionKind.CTrader => "CFD and forex data from IC Markets, Pepperstone, FxPro…",
                ConnectionKind.Oanda => "Index, gold and forex CFDs (free demo)",
                ConnectionKind.Alpaca => "Free US stocks and ETFs (SPY, QQQ)",
                ConnectionKind.Databento => "CME futures history since 2010",
                _ => null,
            };
            object itemHeader = hint == null ? Ui.EnumName(kind) : Ui.V(1, new TextBlock { Text = Ui.EnumName(kind) }, new TextBlock { Text = hint, Classes = { "faint", "small" } });
            var mi = new MenuItem { Header = itemHeader, Icon = Ui.Icon(IconOf(kind), 16) };
            mi.Click += (_, _) => AddProfile(kind);
            menu.Items.Add(mi);
        }

        add.Flyout = menu;
        var header = Ui.V(4, Ui.H1("Connections"), Ui.Muted("Market data and trading accounts"));
        header.Margin = new Thickness(0, 0, 0, 16);
        var left = new DockPanel { Margin = new Thickness(20, 20, 12, 20) };
        DockPanel.SetDock(header, Dock.Top);
        left.Children.Add(header);
        DockPanel.SetDock(add, Dock.Bottom);
        add.Margin = new Thickness(0, 12, 0, 0);
        left.Children.Add(add);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: ConnectionProfile p }) Select(p);
        };
        _list.Background = Brushes.Transparent;
        left.Children.Add(_list);
        grid.Children.Add(new Border { Child = left, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) });

        // ---- detalle
        _detail.Margin = new Thickness(28, 24, 28, 32);
        var scroll = new ScrollViewer { Content = _detail };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    private void FillList()
    {
        var keep = _selected?.Id;
        _list.Items.Clear();
        foreach (var p in _svc.Connections.Profiles)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            dot.Fill = !p.Enabled ? Ui.Text2 : _results.TryGetValue(p.Id, out var r) ? (r.Ok ? Ui.Up : Ui.Down) : Ui.Line;
            ToolTip.SetTip(dot, !p.Enabled ? "Disabled" : _results.TryGetValue(p.Id, out var r2) ? r2.Message : "Not tested");
            var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = Ui.Bg3, Child = Ui.Icon(IconOf(p.Kind), 18, p.Enabled ? Ui.Accent : Ui.Text2) };
            string sub = Describe(p);
            var text = Ui.V(1, new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, Opacity = p.Enabled ? 1 : 0.6 }, new TextBlock { Text = sub, Classes = { "muted", "small" } });
            var row = new DockPanel { Margin = new Thickness(2, 4) };
            DockPanel.SetDock(icon, Dock.Left);
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(dot, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(dot);
            row.Children.Add(text);
            _list.Items.Add(new ListBoxItem { Content = row, Tag = p });
        }

        var sel = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((ConnectionProfile)i.Tag!).Id == keep) ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault();
        _list.SelectedItem = sel;
    }

    /// <summary>Resumen de una conexión para la lista.</summary>
    private string Describe(ConnectionProfile p)
    {
        string prefix = p.Name == Ui.EnumName(p.Kind) ? "" : Ui.EnumName(p.Kind) + " · ";
        if (!p.Enabled) return prefix + "disabled";
        string detail = p.Kind switch
        {
            ConnectionKind.Simulator => "simulated market",
            ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot => string.IsNullOrWhiteSpace(p.ApiKey)
                ? "public data · no account"
                : p.Testnet ? "data + testnet account" : p.AllowRealOrders ? "data + REAL account" : "data + real account (read-only)",
            ConnectionKind.Bybit => "public market data",
            ConnectionKind.Rithmic or ConnectionKind.AmpFutures => string.IsNullOrWhiteSpace(p.RithmicUser) ? "no username configured" : $"{p.RithmicUser} · {p.RithmicSystem}",
            ConnectionKind.Tradovate => string.IsNullOrWhiteSpace(p.TradovateUser) ? "no username configured" : $"{p.TradovateUser} · {(p.Testnet ? "demo" : p.AllowRealOrders ? "REAL" : "real (read-only)")}",
            ConnectionKind.CTrader => string.IsNullOrWhiteSpace(p.CTraderClientId) ? "no app configured" : $"{(p.Testnet ? "demo" : "real")}{(p.CTraderLogin > 0 ? " · account " + p.CTraderLogin : "")}",
            ConnectionKind.Oanda => (p.Testnet ? "demo" : "real") + (string.IsNullOrWhiteSpace(p.OandaAccount) ? "" : " · " + p.OandaAccount),
            ConnectionKind.Alpaca => string.IsNullOrWhiteSpace(p.ApiKey) ? "no credentials" : $"SIP history · live {p.AlpacaLiveFeed.ToUpperInvariant()}",
            ConnectionKind.Databento => "CME futures history (pay as you go)",
            _ => $"{p.IbHost}:{p.IbPort}",
        };
        return prefix + detail;
    }

    private void AddProfile(ConnectionKind kind)
    {
        int n = _svc.Connections.Profiles.Count(p => p.Kind == kind) + 1;
        var p = new ConnectionProfile { Kind = kind, Name = Ui.EnumName(kind) + (n > 1 ? $" {n}" : ""), Enabled = kind is not (ConnectionKind.Rithmic or ConnectionKind.AmpFutures) };
        if (kind is ConnectionKind.CTrader) p.Name = n > 1 ? $"cTrader {n}" : "cTrader";
        if (kind is ConnectionKind.Alpaca) p.Name = n > 1 ? $"Alpaca {n}" : "Alpaca";
        if (kind is ConnectionKind.Databento) p.Name = "Databento";
        // el primero de cada tipo usa el id de sus datos (así los archivos de ticks quedan con un nombre claro)
        string defaultId = ConnectionProfile.DefaultId(kind);
        if (kind is ConnectionKind.Tradovate or ConnectionKind.CTrader or ConnectionKind.Oanda or ConnectionKind.Alpaca or ConnectionKind.Databento
            && _svc.Connections.Profiles.All(x => x.Id != defaultId))
            p.Id = defaultId;
        if (kind == ConnectionKind.Simulator) p.HistoryHours = 6;
        if (kind == ConnectionKind.AmpFutures)
        {
            // AMP da cuentas de Rithmic: servidor de producción (Chicago) y el sistema de la demo por defecto
            p.RithmicUri = RithmicGateways.ChicagoUri;
            p.RithmicSystem = "Rithmic Paper Trading";
        }
        _svc.Connections.Profiles.Add(p);
        _svc.Connections.Save();
        _selected = p;
        FillList();
    }

    // ===================================================================== detalle

    private void Select(ConnectionProfile p)
    {
        _selected = p;
        _detail.Content = BuildDetail(p);
    }

    private Control BuildDetail(ConnectionProfile p)
    {
        var name = new TextBox { Text = p.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, MinWidth = 170, MaxWidth = 300 };
        var enabled = new ToggleSwitch { IsChecked = p.Enabled, OnContent = "Enabled", OffContent = "Disabled" };
        var history = new ComboBox { MinWidth = 160 };
        foreach (var (h, label) in new[] { (0.5, "30 minutes"), (1.0, "1 hour"), (1.5, "1.5 hours"), (3.0, "3 hours"), (6.0, "6 hours"), (12.0, "12 hours"), (24.0, "1 day"), (72.0, "3 days"), (168.0, "7 days") })
            history.Items.Add(new ComboBoxItem { Content = label, Tag = h });
        history.SelectedItem = history.Items.OfType<ComboBoxItem>().OrderBy(i => Math.Abs((double)i.Tag! - p.HistoryHours)).First();

        var resultIcon = new ContentControl();
        var resultText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var resultBox = new Border { Child = Ui.H(10, resultIcon, resultText), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), Background = Ui.Bg2, IsVisible = false };
        void ShowResult(bool ok, string msg, bool running = false)
        {
            resultBox.IsVisible = true;
            resultIcon.Content = running ? Ui.Icon(MaterialIconKind.ProgressClock, 18, Ui.Text1) : Ui.Icon(ok ? MaterialIconKind.CheckCircle : MaterialIconKind.CloseCircle, 18, ok ? Ui.Up : Ui.Down);
            resultText.Text = msg;
        }

        if (_results.TryGetValue(p.Id, out var last)) ShowResult(last.Ok, $"{last.Message}  ({Ui.Ago(last.When)})");

        // campos según el tipo (se escriben en el perfil al guardar)
        var apply = new List<Action>();
        var sections = new StackPanel { Spacing = 16 };

        var dataSection = Ui.V(14,
            Ui.H2("Market data"),
            Ui.Field("History when opening a chart", history, "Saved or downloaded data loads immediately; missing history comes from the connection."));
        sections.Children.Add(Ui.Card(dataSection));
        apply.Add(() => p.HistoryHours = (double)((ComboBoxItem)history.SelectedItem!).Tag!);

        switch (p.Kind)
        {
            case ConnectionKind.Simulator:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Simulated order book and trades for offline practice. No setup required."));
                break;
            case ConnectionKind.BinanceFutures:
            case ConnectionKind.BinanceSpot:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Binance market data is public; charts, DOM and tape do not require an account."));
                sections.Children.Add(Ui.Card(BinanceAccount(p, apply)));
                break;
            case ConnectionKind.Bybit:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Public Bybit linear USDT perpetual data. Bybit account order routing is not yet available."));
                break;
            case ConnectionKind.Rithmic:
            case ConnectionKind.AmpFutures:
                sections.Children.Add(Ui.Card(RithmicSection(p, apply)));
                break;
            case ConnectionKind.InteractiveBrokers:
                sections.Children.Add(Ui.Card(IbSection(p, apply)));
                break;
            case ConnectionKind.Tradovate:
                sections.Children.Add(Ui.Card(TradovateSection(p, apply)));
                break;
            case ConnectionKind.CTrader:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "CFDs have no exchange trades here: each bid quote counts as one trade with volume 1. Suitable for price bars, replay and price-based backtests; footprint and delta are approximate."));
                sections.Children.Add(Ui.Card(CTraderSection(p, apply)));
                break;
            case ConnectionKind.Oanda:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "CFDs have no exchange trades here: each midpoint quote counts as one trade with volume 1. Suitable for price bars, replay and price-based backtests; footprint and delta are approximate."));
                sections.Children.Add(Ui.Card(OandaSection(p, apply)));
                break;
            case ConnectionKind.Alpaca:
                sections.Children.Add(Ui.Card(AlpacaSection(p, apply)));
                break;
            case ConnectionKind.Databento:
                dataSection.IsVisible = false;
                sections.Children.Add(Ui.Card(DatabentoSection(p, apply)));
                break;
        }

        // ---- encabezado y acciones
        var test = Ui.Button("Test connection", MaterialIconKind.LanConnect, () => { });
        var save = Ui.Button("Save", MaterialIconKind.ContentSaveOutline, () => { }, "primary");
        var delete = Ui.Button("Remove", MaterialIconKind.DeleteOutline, () => { }, "ghost");
        delete.IsVisible = _svc.Connections.Profiles.Count(x => x.Kind == p.Kind) > 1 || p.Kind is ConnectionKind.InteractiveBrokers or ConnectionKind.AmpFutures
            or ConnectionKind.Tradovate or ConnectionKind.CTrader or ConnectionKind.Oanda or ConnectionKind.Alpaca or ConnectionKind.Databento;

        void Save()
        {
            p.Name = string.IsNullOrWhiteSpace(name.Text) ? Ui.EnumName(p.Kind) : name.Text.Trim();
            p.Enabled = enabled.IsChecked == true;
            foreach (var a in apply) a();
            _svc.Connections.Save();
            _svc.Connections.Invalidate(p);
            InstrumentPicker.ClearCache(p.Id);
            _status($"Connection \"{p.Name}\" saved");
        }

        save.Click += (_, _) => Save();
        test.Click += async (_, _) =>
        {
            Save();
            _testCts?.Cancel();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            test.IsEnabled = false;
            ShowResult(true, "Testing…", running: true);
            try
            {
                var (ok, msg) = await _svc.Connections.TestAsync(p, _testCts.Token);
                _results[p.Id] = (ok, msg, DateTime.UtcNow);
                ShowResult(ok, msg);
                FillList();
            }
            finally
            {
                test.IsEnabled = true;
            }
        };
        delete.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            if (!await ConfirmDialog.ShowAsync(w, "Remove connection", $"Removes \"{p.Name}\" and its Keychain secrets. Saved market data remains unchanged.", "Remove", danger: true)) return;
            _svc.Connections.ClearSecrets(p);
            _svc.Connections.Invalidate(p);
            _svc.Connections.Profiles.Remove(p);
            _svc.Connections.Save();
            _selected = null;
            FillList();
        };

        var kindBadge = new Border { Child = Ui.H(6, Ui.Icon(IconOf(p.Kind), 14, Ui.Accent), new TextBlock { Text = Ui.EnumName(p.Kind), FontSize = 12 }), Classes = { "pill" } };
        var titleRow = new DockPanel();
        // el interruptor va con las acciones: en ventanas angostas no se superpone con «Eliminar»
        enabled.VerticalAlignment = VerticalAlignment.Center;
        enabled.Margin = new Thickness(0, 0, 8, 0);
        var actions = Ui.H(8, enabled, delete, test, save);
        DockPanel.SetDock(actions, Dock.Right);
        titleRow.Children.Add(actions);
        var left = Ui.H(12, name, kindBadge);
        left.ClipToBounds = true;
        titleRow.Children.Add(left);

        return new StackPanel { Spacing = 16, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left, Children = { titleRow, resultBox, sections } };
    }

    private static Control Note(MaterialIconKind icon, string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center };
        var d = new DockPanel();
        var i = Ui.Icon(icon, 16, Ui.Text2);
        i.Margin = new Thickness(0, 0, 8, 0);
        i.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(i, Dock.Left);
        d.Children.Add(i);
        d.Children.Add(t);
        return d;
    }

    private static Grid TwoColumns(params Control[] fields)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        int row = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            if (i % 2 == 0)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                if (i > 0) g.RowDefinitions.Add(new RowDefinition(new GridLength(14)));
                row = g.RowDefinitions.Count - 1;
            }

            Grid.SetRow(fields[i], row);
            Grid.SetColumn(fields[i], i % 2 == 0 ? 0 : 2);
            g.Children.Add(fields[i]);
        }

        return g;
    }

    private Control BinanceAccount(ConnectionProfile p, List<Action> apply)
    {
        var testnet = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Testnet (paper money)", OffContent = "Real account" };
        var key = new TextBox { Text = p.ApiKey, Watermark = "API key" };
        var secret = new TextBox { PasswordChar = '•', Watermark = string.IsNullOrEmpty(_svc.Connections.GetSecret(p)) ? "API secret" : "•••••••• (saved in Keychain)" };
        var allowReal = new CheckBox { Content = "Allow REAL orders from Trading Hub", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Confirm each real-money order", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Increment = 0.001m, FormatString = "0.#####", Watermark = "no limit" };
        var warn = Note(MaterialIconKind.AlertOutline, "REAL account orders use actual money. Trading Hub defaults to testnet; enable real orders only when fully aware of the risks.");
        void UpdateEnabled()
        {
            bool real = testnet.IsChecked != true;
            allowReal.IsEnabled = real;
            confirm.IsEnabled = real && allowReal.IsChecked == true;
            maxQty.IsEnabled = real && allowReal.IsChecked == true;
            warn.IsVisible = real;
        }

        testnet.IsCheckedChanged += (_, _) => UpdateEnabled();
        allowReal.IsCheckedChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();

        apply.Add(() =>
        {
            p.Testnet = testnet.IsChecked == true;
            p.ApiKey = (key.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            p.AllowRealOrders = !p.Testnet && allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked != false;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });

        var help = new Button { Content = "How do I create an API key?", Classes = { "ghost" }, Padding = new Thickness(0), FontSize = 12, Foreground = Ui.Focus };
        help.Click += (_, _) => Shell.Open(testnet.IsChecked == true
            ? (p.Kind == ConnectionKind.BinanceFutures ? "https://testnet.binancefuture.com" : "https://testnet.binance.vision")
            : "https://www.binance.com/en/my/settings/api-management");

        return Ui.V(14,
            Ui.H2("Trading account"),
            Note(MaterialIconKind.ShieldKeyOutline, "Use API keys with trading permission only (no withdrawals). The secret is stored in macOS Keychain."),
            testnet,
            TwoColumns(Ui.Field("API key", key), Ui.Field("API secret", secret)),
            help,
            Ui.Divider(),
            allowReal,
            confirm,
            Ui.Field("Maximum order quantity (0 = no limit)", maxQty),
            warn);
    }

    private static string RithmicCacheFile => Path.Combine(AppPaths.Root, "rithmic-servidores.json");

    private static List<RithmicServer> LoadRithmicCache()
    {
        try
        {
            if (File.Exists(RithmicCacheFile))
                return System.Text.Json.JsonSerializer.Deserialize<List<RithmicServer>>(File.ReadAllText(RithmicCacheFile)) ?? new();
        }
        catch
        {
            // caché dañada: se vuelve a pedir
        }

        return new List<RithmicServer>();
    }

    /// <summary>Rithmic como en ATAS: usuario, contraseña, un solo «Servidor» (sistema. región), cotizaciones agregadas y tiempo de espera.</summary>
    private Control RithmicSection(ConnectionProfile p, List<Action> apply)
    {
        var servers = LoadRithmicCache();
        var search = new TextBox { Watermark = "Search servers (paper, 01, Apex, Frankfurt, São Paulo…)" };
        var server = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 420, PlaceholderText = "Choose a server" };
        var info = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var refresh = Ui.IconButton(MaterialIconKind.Refresh, "Refresh full server list", () => { }, 16);
        refresh.Margin = new Thickness(6, 0, 0, 0);
        string selSystem = p.RithmicSystem, selUri = p.RithmicUri;

        // servidor a mano (por si Rithmic agrega uno nuevo)
        var customUri = new TextBox { Text = p.RithmicUri, Watermark = "wss://server:443" };
        var customSystem = new TextBox { Text = p.RithmicSystem, Watermark = "System (e.g. Rithmic 01)" };
        var customToggle = new CheckBox { Content = "Use a server not listed" };
        var customPanel = TwoColumns(Ui.Field("Address", customUri), Ui.Field("System", customSystem));
        customPanel.IsVisible = false;
        customToggle.IsCheckedChanged += (_, _) => customPanel.IsVisible = customToggle.IsChecked == true;

        void Fill()
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var list = servers.Where(sv => words.All(w => sv.Display.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            int fastest = servers.Count > 0 ? servers.Min(x => x.LatencyMs) : 0;
            server.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var sv in list)
            {
                var item = new ComboBoxItem
                {
                    Tag = sv,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new DockPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = $"{sv.LatencyMs} ms", Classes = { "faint", "small" }, Foreground = sv.LatencyMs <= fastest * 1.3 ? Ui.Up : null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }.Also(t => DockPanel.SetDock(t, Dock.Right)),
                            new TextBlock { Text = sv.Display, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                };
                server.Items.Add(item);
                if (sv.System == selSystem && sv.Uri == selUri) selected = item;
            }

            if (selected == null && words.Length == 0)
            {
                // lo guardado no está en la lista (o todavía no llegó): se muestra igual
                var cur = new RithmicServer(selSystem, RithmicGateways.NameOf(selUri), selUri, 0);
                selected = new ComboBoxItem { Tag = cur, Content = new TextBlock { Text = cur.Display } };
                server.Items.Insert(0, selected);
            }

            server.SelectedItem = selected;
        }

        var aggNote = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        void UpdateAggregated()
        {
            var known = servers.FirstOrDefault(x => x.System == selSystem && x.Uri == selUri);
            aggNote.Text = known == null ? ""
                : known.Aggregated ? "This system supports aggregated quotes."
                : "This system does not support aggregated quotes: connects without them (requesting them causes Rithmic to reject login).";
        }

        server.SelectionChanged += (_, _) =>
        {
            if (server.SelectedItem is ComboBoxItem { Tag: RithmicServer sv })
            {
                selSystem = sv.System;
                selUri = sv.Uri;
                UpdateAggregated();
            }
        };
        search.TextChanged += (_, _) => Fill();

        async Task DiscoverAsync()
        {
            info.Text = "Checking Rithmic servers…";
            refresh.IsEnabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var found = await RithmicDirectory.DiscoverAsync(cts.Token);
                if (found.Count > 0)
                {
                    servers = found;
                    try
                    {
                        Directory.CreateDirectory(AppPaths.Root);
                        File.WriteAllText(RithmicCacheFile, System.Text.Json.JsonSerializer.Serialize(found));
                    }
                    catch
                    {
                        // sin caché no pasa nada
                    }
                }

                Fill();
                UpdateAggregated();
                var best = servers.GroupBy(x => x.Gateway).Select(g => g.First()).OrderBy(x => x.LatencyMs).FirstOrDefault();
                info.Text = servers.Count == 0
                    ? "No server responded (internet unavailable?)."
                    : $"{servers.Count} combinations on {servers.Select(x => x.Uri).Distinct().Count()} servers. Fastest from here: {best?.Gateway} ({best?.LatencyMs} ms). Colo 75 only works inside the CME datacenter.";
            }
            catch (Exception ex)
            {
                info.Text = "Could not query: " + ex.Message;
            }
            finally
            {
                refresh.IsEnabled = true;
            }
        }

        refresh.Click += async (_, _) => await DiscoverAsync();
        var serverRow = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        serverRow.Children.Add(refresh);
        serverRow.Children.Add(server);

        var user = new TextBox { Text = p.RithmicUser, Watermark = "username (login)" };
        var pass = new TextBox { PasswordChar = '•', RevealPassword = false, Watermark = string.IsNullOrEmpty(_svc.Connections.GetSecret(p)) ? "password" : "•••••••• (saved in Keychain)" };
        var reveal = new ToggleButton { Content = Ui.Icon(MaterialIconKind.EyeOutline, 15), Padding = new Thickness(6, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTip.SetTip(reveal, "Show typed password");
        reveal.IsCheckedChanged += (_, _) => pass.RevealPassword = reveal.IsChecked == true;
        var passRow = new DockPanel();
        DockPanel.SetDock(reveal, Dock.Right);
        reveal.Margin = new Thickness(6, 0, 0, 0);
        passRow.Children.Add(reveal);
        passRow.Children.Add(pass);

        var aggregated = new CheckBox { Content = "Aggregated quotes", IsChecked = p.RithmicAggregatedQuotes };
        ToolTip.SetTip(aggregated, "Rithmic combines simultaneous order-book updates (fewer messages, identical prices), matching the ATAS setting.");
        var timeout = new NumericUpDown { Value = p.RithmicTimeoutMs, Minimum = 2000, Maximum = 120000, Increment = 1000, FormatString = "0", Width = 140 };
        var app = new TextBox { Text = p.RithmicAppName, Watermark = "TradingHub" };
        var products = new TextBox { Text = string.Join(", ", p.RithmicProducts), Watermark = "ES, NQ, MES, MNQ, CL…" };

        // órdenes: las cuentas de práctica operan siempre; las reales (Rithmic 01, prop firms) solo si se habilita acá
        var allowReal = new CheckBox { Content = "Allow orders on real accounts (Rithmic 01, prop firms…)", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Confirm every real order", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0", Width = 140 };

        apply.Add(() =>
        {
            if (customToggle.IsChecked == true && !string.IsNullOrWhiteSpace(customUri.Text))
            {
                p.RithmicUri = customUri.Text.Trim();
                if (!string.IsNullOrWhiteSpace(customSystem.Text)) p.RithmicSystem = customSystem.Text.Trim();
            }
            else
            {
                p.RithmicUri = selUri;
                p.RithmicSystem = selSystem;
            }

            p.RithmicUser = (user.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pass.Text)) _svc.Connections.SetSecret(p, pass.Text);
            pass.Text = "";
            p.RithmicAggregatedQuotes = aggregated.IsChecked == true;
            p.RithmicTimeoutMs = (int)(timeout.Value ?? 10000);
            p.RithmicAppName = string.IsNullOrWhiteSpace(app.Text) ? "TradingHub" : app.Text.Trim();
            p.RithmicProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
            p.AllowRealOrders = allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked == true;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });

        Fill();
        Dispatcher.UIThread.Post(() => _ = DiscoverAsync(), DispatcherPriority.Background);
        return Ui.V(14,
            Ui.H2(p.Kind == ConnectionKind.AmpFutures ? "AMP Futures account" : "Rithmic account"),
            Note(MaterialIconKind.InformationOutline, p.Kind == ConnectionKind.AmpFutures
                ? "AMP Futures uses Rithmic credentials supplied by AMP. Select Rithmic Paper Trading for demo or Rithmic 01 for real accounts on the nearest server. Trading Hub connects directly through R|Protocol; R|Trader Pro is not required."
                : "Direct R|Protocol connection, Rithmic's official API (R|Trader Pro not required). Choose the system (Paper Trading, Rithmic 01 or your prop firm) and region."),
            Ui.H3("Connection"),
            TwoColumns(Ui.Field("Username", user), Ui.Field("Password", passRow)),
            Ui.Field("Server", Ui.V(8, search, serverRow, info)),
            customToggle,
            customPanel,
            Ui.H3("Market data"),
            TwoColumns(Ui.V(4, aggregated, aggNote), Ui.Field("Connection timeout (ms)", timeout)),
            Ui.H3("Trade"),
            Note(MaterialIconKind.ShieldCheckOutline, "Rithmic accounts appear in Chart Trader when the connection is active. Paper accounts trade directly; real accounts need this authorization and request confirmation."),
            allowReal,
            TwoColumns(confirm, Ui.Field("Maximum order quantity (0 = no limit)", maxQty)),
            Ui.H3("Advanced"),
            TwoColumns(Ui.Field("Application name", app, "Some systems require an approved application name."), Ui.Field("Products available in search", products, "Search the active monthly contract for each product (e.g. ES → ESZ6).")));
    }

    /// <summary>Campo de secreto: no muestra lo guardado, solo avisa que está en el Llavero.</summary>
    private TextBox SecretBox(ConnectionProfile p, string? name, string watermark) =>
        new() { PasswordChar = '•', Watermark = string.IsNullOrEmpty(name == null ? _svc.Connections.GetSecret(p) : _svc.Connections.GetSecret(p, name)) ? watermark : "•••••••• (saved in Keychain)" };

    private static Button Link(string text, string url)
    {
        var b = new Button { Content = text, Classes = { "ghost" }, Padding = new Thickness(0), FontSize = 12, Foreground = Ui.Focus };
        b.Click += (_, _) => Shell.Open(url);
        ToolTip.SetTip(b, url);
        return b;
    }

    /// <summary>Tradovate: usuario, contraseña y API key (CID + secret); demo o real; productos.</summary>
    private Control TradovateSection(ConnectionProfile p, List<Action> apply)
    {
        var demo = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Demo (Tradovate paper account)", OffContent = "Real account" };
        var user = new TextBox { Text = p.TradovateUser, Watermark = "Tradovate username" };
        var pass = SecretBox(p, null, "password");
        var cid = new TextBox { Text = p.TradovateCid, Watermark = "CID (number)" };
        var sec = SecretBox(p, "apisecret", "API key secret");
        var products = new TextBox { Text = string.Join(", ", p.TradovateProducts), Watermark = "ES, NQ, MES, MNQ, CL…" };
        var allowReal = new CheckBox { Content = "Allow orders on real Tradovate accounts", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Confirm each real-money order", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0", Width = 140 };
        void Update()
        {
            allowReal.IsEnabled = demo.IsChecked != true;
            confirm.IsEnabled = demo.IsChecked != true && allowReal.IsChecked == true;
        }

        allowReal.IsCheckedChanged += (_, _) => Update();
        demo.IsCheckedChanged += (_, _) => Update();
        Update();
        apply.Add(() =>
        {
            p.Testnet = demo.IsChecked == true;
            p.TradovateUser = (user.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pass.Text)) _svc.Connections.SetSecret(p, pass.Text);
            pass.Text = "";
            p.TradovateCid = (cid.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sec.Text)) _svc.Connections.SetSecret(p, "apisecret", sec.Text.Trim());
            sec.Text = "";
            p.TradovateProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
            p.AllowRealOrders = !p.Testnet && allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked != false;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });
        return Ui.V(14,
            Ui.H2("Tradovate account"),
            Note(MaterialIconKind.InformationOutline, "Direct Tradovate API connection. Accounts appear in Chart Trader and Account and work with compatible CME charts, including Rithmic. Demo accounts trade directly; real accounts need authorization below."),
            Note(MaterialIconKind.AlertOutline, "Tradovate API market data requires CME API data entitlement, separate from other CME licenses. Without it, orders can still use charts from Rithmic or another compatible feed."),
            demo,
            TwoColumns(Ui.Field("Username", user), Ui.Field("Password", pass)),
            TwoColumns(Ui.Field("API key CID", cid), Ui.Field("API key secret", sec)),
            Ui.H(16, Link("Where do I find the API key? (Settings → API Access)", "https://trader.tradovate.com/"), Link("API documentation", "https://partner.tradovate.com")),
            Ui.Field("Products available in search", products, "Use the active monthly contract of each product (ES → ESZ6)."),
            Ui.Divider(),
            allowReal,
            confirm,
            Ui.Field("Maximum order quantity (0 = no limit)", maxQty),
            Note(MaterialIconKind.ShieldCheckOutline, "Tradovate allows one data session per user. Opening Tradovate Trader with the same user can disconnect Trading Hub, and vice versa."));
    }

    /// <summary>cTrader: aplicación de la Open API (Client ID + Secret) y token de la cuenta (OAuth o Playground).</summary>
    private Control CTraderSection(ConnectionProfile p, List<Action> apply)
    {
        const string redirect = "http://127.0.0.1:53682/ctrader";
        var demo = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Demo account", OffContent = "Real account" };
        var clientId = new TextBox { Text = p.CTraderClientId, Watermark = "Your app Client ID" };
        var secret = SecretBox(p, null, "Your app secret");
        var token = SecretBox(p, "token", "access token (or click Connect with cTrader)");
        var login = new NumericUpDown { Value = p.CTraderLogin, Minimum = 0, Maximum = 999999999999, FormatString = "0", Watermark = "first account", HorizontalAlignment = HorizontalAlignment.Stretch };
        var oauthStatus = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var connect = Ui.Button("Connect with cTrader", MaterialIconKind.Login, () => { }, "primary");
        connect.Click += async (_, _) =>
        {
            string id = (clientId.Text ?? "").Trim();
            string sec = !string.IsNullOrWhiteSpace(secret.Text) ? secret.Text.Trim() : _svc.Connections.GetSecret(p);
            if (id.Length == 0 || sec.Length == 0)
            {
                oauthStatus.Text = "First enter your app's Client ID and secret.";
                return;
            }

            connect.IsEnabled = false;
            oauthStatus.Text = "Browser opened: sign in with your cTrader ID and authorize Trading Hub. Awaiting authorization…";
            try
            {
                var (access, refresh) = await CTraderOAuthAsync(id, sec, redirect);
                p.CTraderClientId = id;
                _svc.Connections.SetSecret(p, sec);
                _svc.Connections.SetSecret(p, "token", access);
                if (refresh.Length > 0) _svc.Connections.SetSecret(p, "refresh", refresh);
                _svc.Connections.Save();
                _svc.Connections.Invalidate(p);
                token.Watermark = "•••••••• (saved in Keychain)";
                oauthStatus.Text = "Trading Hub authorized. Click Test connection.";
            }
            catch (Exception ex)
            {
                oauthStatus.Text = "Authorization failed: " + ex.Message;
            }
            finally
            {
                connect.IsEnabled = true;
            }
        };
        var copy = Ui.Button("Copy", MaterialIconKind.ContentCopy, () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) _ = cb.SetTextAsync(redirect);
        }, "ghost");
        apply.Add(() =>
        {
            p.Testnet = demo.IsChecked == true;
            p.CTraderClientId = (clientId.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            if (!string.IsNullOrWhiteSpace(token.Text)) _svc.Connections.SetSecret(p, "token", token.Text.Trim());
            token.Text = "";
            p.CTraderLogin = (long)(login.Value ?? 0);
        });
        return Ui.V(14,
            Ui.H2("cTrader Open API"),
            Note(MaterialIconKind.InformationOutline, "Available through a cTrader broker (IC Markets, Pepperstone, FxPro, Fusion Markets…) and a demo account: index CFDs, forex, gold and oil, with years of tick history."),
            Ui.H3("1 · Your application (one-time setup)"),
            Note(MaterialIconKind.ShieldKeyOutline, "Register an application at openapi.ctrader.com with your cTrader ID and add this redirect URL. Then copy its Client ID and Secret here."),
            Ui.H(8, new TextBlock { Text = redirect, FontFamily = new FontFamily("Menlo, monospace"), VerticalAlignment = VerticalAlignment.Center }, copy, Link("Open openapi.ctrader.com", "https://openapi.ctrader.com/apps")),
            TwoColumns(Ui.Field("Client ID", clientId), Ui.Field("Secret", secret)),
            Ui.H3("2 · Authorize your account"),
            Ui.H(10, connect, demo),
            oauthStatus,
            Ui.Field("Or paste an access token from your app's Playground", token),
            Ui.Field("Account number (cTrader login)", login, "If the token has multiple accounts, enter the one to use. 0 = first demo/real."));
    }

    /// <summary>OAuth de cTrader: abre el navegador y espera el código en la dirección local de redirección.</summary>
    private static async Task<(string Access, string Refresh)> CTraderOAuthAsync(string clientId, string secret, string redirect)
    {
        var uri = new Uri(redirect);
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{uri.Port}/");
        listener.Start();
        Shell.Open(TradingHub.Feeds.CTrader.CTraderAuth.AuthorizeUrl(clientId, redirect));
        var wait = listener.GetContextAsync();
        if (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMinutes(5))) != wait) throw new TimeoutException("authorization not received (5 minutes)");
        var ctx = await wait;
        string? code = ctx.Request.QueryString["code"];
        string? error = ctx.Request.QueryString["error"] ?? ctx.Request.QueryString["error_description"];
        var html = System.Text.Encoding.UTF8.GetBytes(code != null
            ? "<html><body style='font-family:-apple-system;padding:40px'><h2>Authorized</h2><p>Trading Hub is authorized. Close this tab and return to Trading Hub.</p></body></html>"
            : "<html><body style='font-family:-apple-system;padding:40px'><h2>Not authorized</h2><p>Return to Trading Hub and try again.</p></body></html>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(html);
        ctx.Response.Close();
        if (code == null) throw new InvalidOperationException(error ?? "cTrader did not send an authorization code");
        var (access, refresh, _) = await TradingHub.Feeds.CTrader.CTraderAuth.ExchangeAsync(clientId, secret, code, redirect, CancellationToken.None);
        return (access, refresh);
    }

    /// <summary>OANDA: token personal y cuenta de práctica (gratis) o real.</summary>
    private Control OandaSection(ConnectionProfile p, List<Action> apply)
    {
        var practice = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Practice account (free)", OffContent = "Real account" };
        var token = SecretBox(p, null, "API token (Manage API Access)");
        var account = new TextBox { Text = p.OandaAccount, Watermark = "101-001-… (blank = first account)" };
        apply.Add(() =>
        {
            p.Testnet = practice.IsChecked == true;
            if (!string.IsNullOrWhiteSpace(token.Text)) _svc.Connections.SetSecret(p, token.Text.Trim());
            token.Text = "";
            p.OandaAccount = (account.Text ?? "").Trim();
        });
        return Ui.V(14,
            Ui.H2("OANDA (API v20)"),
            Note(MaterialIconKind.InformationOutline, "Practice-account data: index CFDs (NAS100_USD, SPX500_USD, US30_USD, DE30_EUR…), gold, oil and forex, with years of 5-second bar history."),
            practice,
            TwoColumns(Ui.Field("Token", token), Ui.Field("Account", account)),
            Ui.H(16, Link("Create practice account", "https://www.oanda.com/demo-account/"), Link("Generate token (Manage API Access)", "https://www.oanda.com/demo-account/tpa/personal_token")),
            Note(MaterialIconKind.ShieldKeyOutline, "Token is stored in Keychain. Trading Hub uses it for data only (no OANDA orders)."));
    }

    /// <summary>Alpaca: claves de la cuenta de práctica (historia SIP desde 2016 gratis).</summary>
    private Control AlpacaSection(ConnectionProfile p, List<Action> apply)
    {
        var key = new TextBox { Text = p.ApiKey, Watermark = "API key ID" };
        var secret = SecretBox(p, null, "secret key");
        var live = new ComboBox { MinWidth = 260 };
        live.Items.Add(new ComboBoxItem { Content = "IEX (free, one venue)", Tag = "iex" });
        live.Items.Add(new ComboBoxItem { Content = "SIP (all venues, paid plan)", Tag = "sip" });
        live.SelectedItem = live.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == p.AlpacaLiveFeed) ?? live.Items[0];
        apply.Add(() =>
        {
            p.ApiKey = (key.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            p.AlpacaLiveFeed = (live.SelectedItem as ComboBoxItem)?.Tag as string ?? "iex";
        });
        return Ui.V(14,
            Ui.H2("Alpaca (US stocks and ETFs)"),
            Note(MaterialIconKind.InformationOutline, "Practice accounts provide US-equity trade history from 2016, excluding the latest 15 minutes, subject to provider access. SPY = S&P 500, QQQ = Nasdaq 100, IWM = Russell 2000, DIA = Dow."),
            TwoColumns(Ui.Field("API key ID", key), Ui.Field("Secret key", secret)),
            Ui.Field("Live market data", live),
            Ui.H(16, Link("Create practice account", "https://app.alpaca.markets/signup"), Link("Where are credentials stored?", "https://app.alpaca.markets/paper/dashboard/overview")),
            Note(MaterialIconKind.ShieldKeyOutline, "Trading Hub uses these keys for data only (no Alpaca orders)."));
    }

    /// <summary>Databento: API key (historia de futuros de CME desde 2010, pago por uso con créditos de regalo).</summary>
    private Control DatabentoSection(ConnectionProfile p, List<Action> apply)
    {
        var key = SecretBox(p, null, "API key (db-…)");
        apply.Add(() =>
        {
            if (!string.IsNullOrWhiteSpace(key.Text)) _svc.Connections.SetSecret(p, key.Text.Trim());
            key.Text = "";
        });
        return Ui.V(14,
            Ui.H2("Databento"),
            Note(MaterialIconKind.InformationOutline, "Historical CME futures ticks (NQ, ES, MNQ, MES, CL, GC…) from 2010, including the true aggressor side, beyond Rithmic's retained history. Billed by usage; check current signup credits with Databento."),
            Ui.Field("API key", key),
            Ui.H(16, Link("Create account (with signup credits)", "https://databento.com/signup"), Link("Prices", "https://databento.com/pricing")),
            Note(MaterialIconKind.ShieldCheckOutline, "Use Data → CME futures with Databento. Costs are estimated before confirmation. History is stored separately. Backtests support Databento only, Rithmic only or combined history (Rithmic first, Databento for gaps)."));
    }

    private Control IbSection(ConnectionProfile p, List<Action> apply)
    {
        var host = new TextBox { Text = p.IbHost };
        var port = new NumericUpDown { Value = p.IbPort, Minimum = 1, Maximum = 65535, FormatString = "0" };
        var client = new NumericUpDown { Value = p.IbClientId, Minimum = 0, Maximum = 999, FormatString = "0" };
        apply.Add(() =>
        {
            p.IbHost = string.IsNullOrWhiteSpace(host.Text) ? "127.0.0.1" : host.Text.Trim();
            p.IbPort = (int)(port.Value ?? 7497);
            p.IbClientId = (int)(client.Value ?? 17);
        });
        return Ui.V(14,
            Ui.H2("Interactive Brokers (TWS / IB Gateway)"),
            Note(MaterialIconKind.InformationOutline, "Open TWS or IB Gateway and enable API access (Configure → API → Settings). Common ports: 7497 TWS paper, 7496 TWS real, 4002 Gateway paper. Trading Hub currently only tests connectivity."),
            TwoColumns(Ui.Field("Host", host), Ui.Field("Port", port)),
            Ui.Field("Client ID", client));
    }
}
