using System.Text.Json;
using System.Text.Json.Serialization;
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
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

/// <summary>
/// Backtesting: todo lo que es prueba. Código de la estrategia, backtest tick a tick, optimización, walk-forward,
/// historial de corridas, portafolio de pruebas y reportes. Lo que corre en tiempo real vive en la sección En vivo.
/// La estrategia se elige arriba (sin barra lateral) para dejar todo el ancho.
/// </summary>
internal sealed partial class StrategiesView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly StrategyCatalog _catalog;
    private StrategyFile? _current;
    private UiState _state = new();

    // encabezado
    private readonly TextBlock _strategyName = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private Flyout _strategyMenu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private Button _strategyPicker = null!;
    private readonly Avalonia.Controls.Shapes.Ellipse _compileDot = new() { Width = 8, Height = 8 };
    private readonly TextBlock _compileText = new() { FontSize = 12 };
    private readonly Ui.Segmented<string> _tabs;
    private readonly Panel _pages = new();
    private readonly Dictionary<string, Control> _pageControls = new();

    // código
    private readonly CodeEditor _editor = new();
    private readonly TextBlock _fileInfo = new() { Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };

    public StrategiesView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        _catalog = svc.Strategies;
        _tabs = new Ui.Segmented<string>(new[]
        {
            ("code", "Code", (string?)"Edit strategy in C#"),
            ("backtest", "Backtest", (string?)"Test against recorded tick-level history"),
            ("optimize", "Optimization", (string?)"Test combinations of parameters"),
            ("wfo", "Walk-forward", (string?)"Optimize and validate out of sample, window by window"),
            ("history", "History", (string?)"Saved runs for this strategy"),
            ("portfolio", "Portfolio", (string?)"Test portfolio: combine strategy backtests and measure correlation"),
            ("reports", "Reports", (string?)"Detailed strategy and portfolio reports"),
        });
        _tabs.SelectionChanged += ShowTab;

        _pageControls["code"] = BuildCodePage();
        _pageControls["backtest"] = BuildBacktestPage();
        _pageControls["optimize"] = BuildOptimizePage();
        _pageControls["wfo"] = BuildWalkForwardPage();
        _pageControls["history"] = BuildHistoryPage();
        _pageControls["portfolio"] = BuildPortfolioPage();
        _pageControls["reports"] = BuildReportsPage();
        foreach (var c in _pageControls.Values) _pages.Children.Add(c);

        Content = Build();
        _editor.SaveRequested += () => Save();
        _editor.CompileRequested += () => _ = CompileAsync();
        _editor.Changed += UpdateFileInfo;
        _catalog.Changed += () => Dispatcher.UIThread.Post(BuildStrategyMenu);
        BuildStrategyMenu();
        var first = _catalog.Files.FirstOrDefault();
        if (first != null) _ = SelectAsync(first);
        ShowTab("code");
        _tabs.Select("code");
    }

    public void OnShown()
    {
        _catalog.Refresh();
        RefreshDataSources();
    }

    public void OnTimer()
    {
    }

    private Control Build()
    {
        var picker = new Button { Flyout = _strategyMenu, Classes = { "ghost" }, Padding = new Thickness(10, 6) };
        _strategyPicker = picker;
        picker.Content = Ui.H(10, Ui.Icon(MaterialIconKind.RobotOutline, 20, Ui.Accent), _strategyName, Ui.Icon(MaterialIconKind.ChevronDown, 16, Ui.Text2));
        ToolTip.SetTip(picker, "Select or create strategy");
        var state = new Border { Child = Ui.H(7, _compileDot, _compileText), Classes = { "pill" } };
        var left = Ui.H(12, picker, state);
        var header = new DockPanel { Margin = new Thickness(20, 12, 20, 12) };
        DockPanel.SetDock(_tabs, Dock.Right);
        header.Children.Add(_tabs);
        header.Children.Add(left);
        var top = new Border { Child = header, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_pages);
        return root;
    }

    private void ShowTab(string id)
    {
        foreach (var (k, c) in _pageControls) c.IsVisible = k == id;
        if (id == "history") RefreshHistory();
        if (id == "reports") RefreshReports();
        if (id == "portfolio") LoadPortfolio();
        if (id == "optimize" || id == "wfo") RefreshRanges();
    }

    /// <summary>
    /// La lista de estrategias: arriba las favoritas y abajo todas, cada una con su estrella para sumarla o sacarla de
    /// favoritas (sin cerrar la lista), un buscador y las acciones de siempre.
    /// </summary>
    private void BuildStrategyMenu()
    {
        _strategyMenu = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = StrategyList() };
        if (_strategyPicker != null) _strategyPicker.Flyout = _strategyMenu;
    }

    private bool IsFavorite(StrategyFile f) => _svc.Settings.FavoriteStrategies.Contains(f.FileName, StringComparer.OrdinalIgnoreCase);

    private void ToggleFavorite(StrategyFile f)
    {
        var favs = _svc.Settings.FavoriteStrategies;
        if (favs.RemoveAll(x => x.Equals(f.FileName, StringComparison.OrdinalIgnoreCase)) == 0) favs.Add(f.FileName);
        _svc.Settings.Save();
        _status(IsFavorite(f) ? $"{f.DisplayName} added to favorites" : $"{f.DisplayName} removed from favorites");
    }

    private Control StrategyList()
    {
        var search = new TextBox { Watermark = "Search strategies…", Margin = new Thickness(0, 0, 0, 8) };
        var list = new StackPanel { Spacing = 2 };

        void Fill()
        {
            list.Children.Clear();
            string q = (search.Text ?? "").Trim();
            bool Match(StrategyFile f) => q.Length == 0 || f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || f.FileName.Contains(q, StringComparison.OrdinalIgnoreCase);
            var files = _catalog.Files.Where(Match).ToList();
            var favorites = files.Where(IsFavorite).OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

            TextBlock Section(string text)
            {
                var h = Ui.H3(text);
                h.Margin = new Thickness(6, 4, 0, 4);
                return h;
            }

            list.Children.Add(Section("Favorites"));
            if (favorites.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = q.Length > 0 ? "No favorite matches this name." : "Star a strategy to pin it here.",
                    Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 8),
                });
            foreach (var f in favorites) list.Children.Add(StrategyRow(f, Fill));

            list.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 6) });
            list.Children.Add(Section("All strategies"));
            if (files.Count == 0)
                list.Children.Add(new TextBlock { Text = q.Length > 0 ? "No strategy matches this name." : "No strategies yet.", Classes = { "faint", "small" }, Margin = new Thickness(4, 2) });
            foreach (var f in files) list.Children.Add(StrategyRow(f, Fill));
        }

        search.TextChanged += (_, _) => Fill();
        Fill();

        Button Action(string text, MaterialIconKind icon, Action run)
        {
            var b = Ui.Button(text, icon, () =>
            {
                _strategyMenu.Hide();
                run();
            }, "ghost");
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            return b;
        }

        var actions = Ui.V(2,
            new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 6) },
            Action("New strategy…", MaterialIconKind.Plus, () => _ = NewStrategyAsync()),
            Action("Open strategies folder", MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.Strategies)));
        var root = new DockPanel { Width = 440, Margin = new Thickness(12) };
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(search);
        root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 460 });
        return root;
    }

    /// <summary>Una estrategia de la lista: la estrella (favorita o no) y el resto de la fila la abre.</summary>
    private Control StrategyRow(StrategyFile f, Action refresh)
    {
        bool fav = IsFavorite(f), ok = f.LastCompile?.Success != false, current = ReferenceEquals(f, _current);
        var star = new Button
        {
            Content = Ui.Icon(fav ? MaterialIconKind.Star : MaterialIconKind.StarOutline, 18, fav ? Ui.Accent : Ui.Text2),
            Classes = { "ghost" }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(star, fav ? "Remove from favorites" : "Add to favorites");
        star.Click += (_, _) =>
        {
            ToggleFavorite(f);
            refresh();
        };

        var icon = Ui.Icon(current ? MaterialIconKind.Check : ok ? MaterialIconKind.RobotOutline : MaterialIconKind.AlertCircleOutline, 16, current ? Ui.Accent : ok ? Ui.Text1 : Ui.Down);
        var info = Ui.V(1,
            Ui.H(8, new TextBlock { Text = f.DisplayName, FontWeight = current ? FontWeight.SemiBold : FontWeight.Medium }, VerdictPill(Lab.ReportStore.VerdictOf(f.FileName))),
            new TextBlock { Text = Path.GetFileName(f.Path), Classes = { "faint", "small" } });
        var open = new Button
        {
            Content = Ui.H(10, icon, info), Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5),
        };
        open.Click += (_, _) =>
        {
            _strategyMenu.Hide();
            _ = SelectAsync(f);
        };

        var row = new DockPanel();
        DockPanel.SetDock(star, Dock.Left);
        row.Children.Add(star);
        row.Children.Add(open);
        return row;
    }

    // ===================================================================== selección y compilación

    /// <summary>Abre en el editor la estrategia de ese archivo (por ejemplo, una de otra carpeta).</summary>
    internal async Task OpenFileAsync(string path)
    {
        _catalog.Refresh();
        var full = Path.GetFullPath(path);
        var file = _catalog.Files.FirstOrDefault(f => string.Equals(Path.GetFullPath(f.Path), full, StringComparison.OrdinalIgnoreCase));
        if (file != null) await SelectAsync(file);
        else _status($"Could not find {Path.GetFileName(path)} in strategies");
    }

    private async Task SelectAsync(StrategyFile file)
    {
        if (_current != null && _editor.IsDirty) Save();
        _current = file;
        _strategyName.Text = file.DisplayName;
        _editor.Text = File.Exists(file.Path) ? await File.ReadAllTextAsync(file.Path) : "";
        _editor.ClearDiagnostics();
        UpdateFileInfo();
        await CompileAsync(silent: true);
        BuildStrategyMenu();
    }

    private void Save()
    {
        if (_current == null) return;
        File.WriteAllText(_current.Path, _editor.Text);
        _editor.IsDirty = false;
        UpdateFileInfo();
        _status($"Saved {Path.GetFileName(_current.Path)}");
    }

    private void UpdateFileInfo()
    {
        if (_current == null) return;
        _fileInfo.Text = $"{Path.GetFileName(_current.Path)}{(_editor.IsDirty ? " · unsaved" : "")}";
    }

    private async Task<bool> CompileAsync(bool silent = false)
    {
        if (_current == null) return false;
        if (_editor.IsDirty) Save();
        SetCompileState(null, "Compiling…");
        var file = _current;
        var res = await Task.Run(() => _catalog.Compile(file, force: !silent));
        if (!ReferenceEquals(file, _current)) return false;
        _editor.ShowDiagnostics(res.Diagnostics, file.Path);
        if (res.Success && file.Type != null)
        {
            _compiledSource = File.ReadAllText(file.Path);
            SetCompileState(true, $"Compiled · {res.Elapsed.TotalMilliseconds:0} ms");
            _strategyName.Text = file.DisplayName;
            LoadState();
            BuildParameterForm();
            RefreshRanges();
            if (!silent) _status($"{file.DisplayName} compiled without errors");
            return true;
        }

        int errors = res.Diagnostics.Count(d => d.Severity == "error");
        SetCompileState(false, $"{errors} error{(errors == 1 ? "" : "s")}");
        if (!silent) _status($"Strategy has {errors} error(s): review them in Code");
        return false;
    }

    private void SetCompileState(bool? ok, string text)
    {
        _compileDot.Fill = ok == null ? Ui.Warn : ok.Value ? Ui.Up : Ui.Down;
        _compileText.Text = text;
    }

    private async Task NewStrategyAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window w) return;
        var dlg = new NewStrategyDialog();
        var result = await dlg.ShowDialog<(string Name, StrategyTemplate Template)?>(w);
        if (result is not { } r) return;
        var file = _catalog.Create(r.Name, r.Template);
        await SelectAsync(file);
        _tabs.Select("code");
        ShowTab("code");
    }

    // ===================================================================== página: código

    private Control BuildCodePage()
    {
        var save = Ui.Button("Save", MaterialIconKind.ContentSaveOutline, Save);
        ToolTip.SetTip(save, "⌘S");
        var compile = Ui.Button("Compile", MaterialIconKind.Hammer, () => _ = CompileAsync(), "primary");
        ToolTip.SetTip(compile, "⌘B");
        var test = Ui.Button("Compile and test", MaterialIconKind.PlayOutline, async () =>
        {
            if (!await CompileAsync()) return;
            _tabs.Select("backtest");
            ShowTab("backtest");
            await RunBacktestAsync();
        });
        var help = new Button { Content = Ui.H(6, Ui.Icon(MaterialIconKind.BookOpenPageVariantOutline, 16), new TextBlock { Text = "Reference" }), Flyout = new Flyout { Content = ApiHelp() } };
        var delete = Ui.IconButton(MaterialIconKind.DeleteOutline, "Delete this strategy", async () =>
        {
            if (_current == null || TopLevel.GetTopLevel(this) is not Window w) return;
            if (!await ConfirmDialog.ShowAsync(w, "Delete strategy", $"Deletes file {Path.GetFileName(_current.Path)}. Backtest history is retained.", "Delete", danger: true)) return;
            _catalog.Delete(_current);
            _current = null;
            var next = _catalog.Files.FirstOrDefault();
            if (next != null) await SelectAsync(next);
        });

        var bar = new DockPanel { Margin = new Thickness(16, 10) };
        var right = Ui.H(8, delete, help, save, test, compile);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(_fileInfo);
        var top = new Border { Child = bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        var editorArea = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        editorArea.Children.Add(top);
        editorArea.Children.Add(_editor);

        // editor + Claude lado a lado: lo que Claude escribe se ve en el editor
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,400") };
        grid.Children.Add(editorArea);
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Width = 1 };
        Grid.SetColumn(split, 1);
        grid.Children.Add(split);
        var claude = BuildClaudePanel();
        Grid.SetColumn(claude, 2);
        grid.Children.Add(claude);
        _claudeGrid = grid;
        _claudeSplit = split;
        _claudePanel = claude;
        return grid;
    }

    private static Control ApiHelp()
    {
        Control Section(string title, string body) => Ui.V(4, new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = body, Classes = { "muted", "small", "mono" }, TextWrapping = TextWrapping.Wrap });
        return new ScrollViewer
        {
            MaxHeight = 520,
            Width = 520,
            Content = Ui.V(12,
                Ui.H2("Quick reference"),
                Section("Lifecycle", "OnInit() → create indicators\nOnBar(Bar bar) → on bar close\nOnTick(Trade trade) → every trade (slower)\nOnOrderFilled(order) · OnSessionStart(session) · OnStop()"),
                Section("Data", "Bars[0] = latest closed bar, Bars[1] = previous\nBar.Open/High/Low/Close/Volume · BuyVolume/SellVolume/Delta\nBar.MaxDelta/MinDelta/Trades/PocPrice\nBar.Levels requires UseFootprint = true\nPrice · Time · LocalTime · TickSize"),
                Section("Orders", "EnterLong() · EnterShort() · Buy(quantity) · Sell(quantity)\nBuyLimit(price) · SellLimit(price) · BuyStop(price) · SellStop(price)\nClosePosition() · CancelAll() · Modify(order, price)\nStopLossTicks · TakeProfitTicks · TrailingStopTicks (automatic OCO)"),
                Section("Position", "Position (>0 long, <0 short) · IsFlat · IsLong · IsShort\nAveragePrice · OpenPnL · NetProfit · ActiveOrders"),
                Section("Indicators", "Sma(n) · Ema(n) · Rsi(n) · Atr(n) · Vwap() · Bollinger(n, k)\nMacd(12,26,9) · Highest(n) · Lowest(n) · StdDev(n) · CumulativeDelta()\nCustom(\"Name\", bars => value)\nseries[0] = current, series[1] = previous · .IsReady"),
                Section("Helpers", "CrossAbove(a, b) · CrossBelow(a, b) · Ticks(n) · Round(price)\nTimeBetween(from, to) · Log(\"text\")"),
                Section("Chart drawings", "DrawBox(id, from, price1, to, price2, color, fillOpacity, text)\nDrawLine(id, t1, p1, t2, p2, color)\nDrawHorizontalLine(id, price, from, color, text)\nDrawArrowUp/DrawArrowDown(id, time, price, color, text)\nDrawMarker · DrawText · Plot(\"Name\", value, color)\nRemoveDrawing(id) · ClearDrawings()\nReusing an ID replaces the drawing. Colors: \"#RRGGBB\", \"green\", \"red\"…"),
                Section("Market context", "MarketTrend (0 range … 1 trend, 24 h) · Regime (Range/Mixed/Trend)\nMarketVolatility (% per hour, 24 h)\nVolatilityRank (percentile over the last month)\nBuilt-in filters: RegimeFilter · MinVolatilityRank · MaxVolatilityRank"),
                Section("Parameters", "[Parameter(\"Name\")] on a public editable property\n[Optimize(min, max, step)] includes it in optimization")),
        };
    }

    // ===================================================================== estado por estrategia

    private sealed class UiState
    {
        public BacktestSettings? Settings { get; set; }
        public Dictionary<string, object?> Parameters { get; set; } = new();
        public List<ParameterRange> Ranges { get; set; } = new();
        public FitnessMetric Metric { get; set; } = FitnessMetric.NetProfitOverDrawdown;
        public int MinTrades { get; set; } = 10;
        public int InSampleDays { get; set; } = 14;
        public int OutSampleDays { get; set; } = 7;
        public bool Anchored { get; set; }

        /// <summary>Cómo busca parámetros el walk-forward en cada ventana.</summary>
        public SearchMode Search { get; set; } = SearchMode.Auto;
    }

    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private string StatePath => Path.Combine(AppPaths.Backtests, string.Concat((_current?.Id ?? "x").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)), "ui.json");

    private void LoadState()
    {
        _state = new UiState();
        try
        {
            if (File.Exists(StatePath)) _state = JsonSerializer.Deserialize<UiState>(File.ReadAllText(StatePath), StateJson) ?? new UiState();
        }
        catch
        {
            _state = new UiState();
        }

        // valores tipados: instancia nueva + lo guardado
        if (_current?.Type != null)
        {
            var s = _current.Create();
            s.SetParameters(_state.Parameters);
            _state.Parameters = s.GetParameters();
            var defaults = ParameterRange.FromStrategy(_current.Type, s);
            foreach (var d in defaults)
            {
                var saved = _state.Ranges.FirstOrDefault(r => r.Name == d.Name);
                if (saved != null)
                {
                    d.Enabled = saved.Enabled;
                    d.Min = saved.Min;
                    d.Max = saved.Max;
                    d.Step = saved.Step;
                }
            }

            // el período de las velas va al final, tildado o no, con los períodos que se habían elegido
            defaults.Add(_state.Ranges.FirstOrDefault(r => r.IsPeriod) ?? ParameterRange.Periods(Array.Empty<string>(), enabled: false));
            _state.Ranges = defaults;
        }

        ApplySettingsToForm(_state.Settings);
    }

    private void SaveState()
    {
        if (_current?.Type == null) return;
        try
        {
            _state.Settings = ReadSettingsFromForm(validate: false);
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, StateJson));
        }
        catch
        {
            // no es crítico
        }
    }
}

/// <summary>Diálogo para crear una estrategia desde una plantilla.</summary>
internal sealed class NewStrategyDialog : Window
{
    public NewStrategyDialog()
    {
        Title = "New strategy";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg2;
        var name = new TextBox { Text = "My strategy" };
        var list = new ListBox { MaxHeight = 320, Background = Brushes.Transparent };
        foreach (var t in StrategyTemplates.All)
        {
            var row = Ui.V(2, new TextBlock { Text = t.Name, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = t.Description, Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
            list.Items.Add(new ListBoxItem { Content = row, Tag = t, Padding = new Thickness(10, 8) });
        }

        list.SelectedIndex = 0;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var ok = new Button { Content = "Create", IsDefault = true }.Classes("primary");
        ok.Click += (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: StrategyTemplate t } || string.IsNullOrWhiteSpace(name.Text)) return;
            Close(((string Name, StrategyTemplate Template)?)(name.Text.Trim(), t));
        };
        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "New strategy", Classes = { "h2" } },
                Ui.Field("Name", name),
                Ui.Field("Start from", list),
                Ui.H(8, cancel, ok).Also(p => p.HorizontalAlignment = HorizontalAlignment.Right),
            },
        };
    }
}
