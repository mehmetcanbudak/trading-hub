using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Windows;

/// <summary>
/// Lanzar una estrategia en vivo: cuál, en qué instrumento y cuenta, con qué velas y parámetros (los de su backtest),
/// con qué riesgo (tamaño, pérdida máxima del día, drawdown máximo) y en qué portafolio.
/// </summary>
internal sealed class LiveLaunchWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly ComboBox _strategy = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select strategy" };
    private readonly InstrumentPicker _picker;
    private readonly ComboBox _account = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select instrument first" };
    private readonly ComboBox _period = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _session = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly ParameterEditor _params = new();
    private readonly MarketConditionPicker _conditions = new();
    private readonly NumericUpDown _size = Num(0.25m, 0.01m, 100, "0.##", 1);
    private readonly NumericUpDown _dailyLoss = Num(10, 0, 100_000_000, "0.##", 0);
    private readonly NumericUpDown _maxDd = Num(10, 0, 100_000_000, "0.##", 0);
    private readonly CheckBox _closeOnLimit = new() { Content = "When a risk limit is hit, also close its position", IsChecked = true };
    private readonly ComboBox _portfolio = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _warning = new() { Foreground = Ui.Warn, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly Button _start;
    private string? _profile;
    private Instrument? _instrument;
    private InstrumentData? _data;
    private Dictionary<string, object?> _values = new();
    private string? _timeZoneId;
    private TimeSpan _sessionStart;

    public LiveLaunchWindow(AppServices svc, Action<string> status, string? strategyId = null, IReadOnlyDictionary<string, object?>? parameters = null, string? portfolioId = null)
    {
        _svc = svc;
        _status = status;
        Title = "New live strategy";
        Width = 600;
        Height = 840;
        MinWidth = 500;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;

        var catalog = svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        foreach (var f in catalog.Files.OrderBy(f => f.DisplayName))
            _strategy.Items.Add(new ComboBoxItem { Content = f.DisplayName + (f.LastCompile?.Success == false ? " (with errors)" : ""), Tag = f });
        foreach (var p in new[] { "M1", "M2", "M3", "M5", "M10", "M15", "M30", "H1", "H4" }) _period.Items.Add(p);
        _period.SelectedItem = "M5";

        _picker = new InstrumentPicker(svc) { HorizontalAlignment = HorizontalAlignment.Stretch };
        _picker.Picked += (profileId, inst, _) =>
        {
            _profile = profileId;
            _instrument = inst;
            _picker.SetCurrent(profileId, inst);
            _data = _svc.Open(profileId, inst);
            Ui.SyncAccounts(_account, _svc.AccountsFor(_data).ToList(), "sim", Ui.Down);
            if (_account.SelectedItem == null && _account.Items.Count > 0) _account.SelectedIndex = 0;
            UpdateWarning();
        };
        _account.SelectionChanged += (_, _) => UpdateWarning();
        _strategy.SelectionChanged += (_, _) => _ = LoadStrategyAsync(null);

        _portfolio.Items.Add(new ComboBoxItem { Content = "None (use automatic portfolio)", Tag = null });
        foreach (var d in LiveStore.LoadPortfolios().Where(d => !d.Auto)) _portfolio.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        _portfolio.SelectedItem = _portfolio.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == portfolioId) ?? _portfolio.Items[0];

        _start = Ui.Button("Start live", MaterialIconKind.Play, () => _ = StartAsync(), "primary");
        var cancel = Ui.Button("Cancel", MaterialIconKind.Close, Close);

        var riskGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*") };
        void Put(Control c, int col)
        {
            Grid.SetColumn(c, col);
            riskGrid.Children.Add(c);
        }

        Put(Ui.Field("Size (×)", _size, "multiplies the size of its orders"), 0);
        Put(Ui.Field("Max daily loss", _dailyLoss, "0 = no limit"), 2);
        Put(Ui.Field("Maximum drawdown", _maxDd, "0 = no limit"), 4);

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(22, 18, 22, 18) };
        body.Children.Add(Ui.V(4, Ui.H2("New live strategy"),
            Note("Receives live market data and trades on the selected account. Warms up indicators from historical data without trading before starting. Test with simulation first.")));
        body.Children.Add(Section("What and where", Ui.Field("Strategy", _strategy), Ui.Field("Instrument", _picker), Ui.Field("Account", _account), _warning,
            Ui.Field("Candles", _period), _session));
        body.Children.Add(Section("Parameters", _params, Note("Uses the latest parameters from its backtest.")));
        body.Children.Add(Section("Market condition", _conditions,
            Note("Only opens trades in selected market conditions, using closed bars for trend and volatility. You can adjust this in Live without restarting.")));
        body.Children.Add(Section("Risk", riskGrid, _closeOnLimit,
            Note("Daily loss limit stops new entries until the next session; drawdown limit stops the strategy. Exits must close only its open size.")));
        body.Children.Add(Section("Portfolio", _portfolio));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 10, 22, 16) };
        actions.Children.Add(cancel);
        actions.Children.Add(_start);
        var bottom = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };

        if (strategyId != null)
        {
            var item = _strategy.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is StrategyFile f && f.Id == strategyId);
            if (item != null)
            {
                _strategy.SelectedItem = item;
                _ = LoadStrategyAsync(parameters);
            }
        }
        else _params.Load(null, new Dictionary<string, object?>());
    }

    private static NumericUpDown Num(decimal increment, decimal min, decimal max, string format, decimal value) => new()
    {
        Increment = increment,
        Minimum = min,
        Maximum = max,
        Value = value,
        FormatString = format,
        NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private static Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 9 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private void UpdateWarning()
    {
        bool real = (_account.SelectedItem as ComboBoxItem)?.Tag is AccountOption { IsReal: true };
        _warning.IsVisible = real;
        _warning.Text = "REAL ACCOUNT: This strategy will send real-money orders without individual confirmations.";
    }

    /// <summary>Carga parámetros, velas y sesión de su último backtest (o los de la clase).</summary>
    private async Task LoadStrategyAsync(IReadOnlyDictionary<string, object?>? overrides)
    {
        if (_strategy.SelectedItem is not ComboBoxItem { Tag: StrategyFile file }) return;
        if (file.Type == null) await Task.Run(() => _svc.Strategies.Compile(file));
        if (file.Type == null)
        {
            _params.Load(null, new Dictionary<string, object?>());
            _status($"«{file.FileName}» has errors: fix them in Backtesting.");
            return;
        }

        if (_strategy.SelectedItem is ComboBoxItem selected && ReferenceEquals(selected.Tag, file))
            selected.Content = file.DisplayName;
        var saved = LiveCenter.SavedBacktest(file, _svc.Settings.TimeZoneId);
        var values = saved.Parameters;
        _timeZoneId = saved.TimeZoneId;
        _sessionStart = saved.SessionStart;
        if (saved.Period is { } sp)
        {
            if (!_period.Items.Cast<string>().Contains(sp)) _period.Items.Add(sp);
            _period.SelectedItem = sp;
        }

        if (overrides != null)
            foreach (var (k, v) in overrides)
                if (values.ContainsKey(k)) values[k] = v;
        _values = values;
        _params.Load(file.Type, values, hidden: LiveStrategyWindow.ContextParameters);
        var probe = file.Type != null ? (Strategy)Activator.CreateInstance(file.Type)! : null;
        probe?.SetParameters(values);
        _conditions.Load(probe?.RegimeFilter ?? RegimeFilter.All, probe?.MinVolatilityRank ?? 0, probe?.MaxVolatilityRank ?? 100);
        _session.Text = $"Session: {(_timeZoneId ?? "UTC")} starting at {_sessionStart:hh\\:mm} (from the backtest; defines the day for daily loss limits).";
    }

    /// <summary>Los parámetros con la condición de mercado elegida.</summary>
    private Dictionary<string, object?> WithConditions(Dictionary<string, object?> values)
    {
        var (filter, min, max) = _conditions.Value;
        values[nameof(Strategy.RegimeFilter)] = (int)filter;
        values[nameof(Strategy.MinVolatilityRank)] = min;
        values[nameof(Strategy.MaxVolatilityRank)] = max;
        return values;
    }

    private async Task StartAsync()
    {
        if (_strategy.SelectedItem is not ComboBoxItem { Tag: StrategyFile file })
        {
            _status("Select a strategy.");
            return;
        }

        if (_profile == null || _instrument == null || _data == null)
        {
            _picker.Open();
            return;
        }

        if (_account.SelectedItem is not ComboBoxItem { Tag: AccountOption acc })
        {
            _status("Select an account.");
            return;
        }

        if (!ChartPeriod.TryParse(_period.SelectedItem as string ?? "M5", out var period))
        {
            _status("Invalid period.");
            return;
        }

        if (!_conditions.IsValid)
        {
            _status("Choose at least one market condition and volatility category.");
            return;
        }

        if (acc.IsReal && !await ConfirmDialog.ShowAsync(this, "Execute on REAL account",
                $"«{file.DisplayName}» will trade {_instrument.Symbol} on {acc.Name} with real orders and no individual confirmation. Stop it from Live.",
                "Execute with real money", danger: true))
            return;

        _start.IsEnabled = false;
        try
        {
            var (runner, message) = await _svc.Live.LaunchAsync(new LiveLaunch
            {
                File = file,
                ProfileId = _profile,
                Instrument = _instrument,
                AccountId = acc.Id,
                Period = period,
                Parameters = WithConditions(_params.Read(_values)),
                TimeZoneId = _timeZoneId,
                SessionStart = _sessionStart,
                Size = _size.Value ?? 1,
                MaxDailyLoss = _dailyLoss.Value ?? 0,
                MaxDrawdown = _maxDd.Value ?? 0,
                CloseOnLimit = _closeOnLimit.IsChecked == true,
                PortfolioId = (_portfolio.SelectedItem as ComboBoxItem)?.Tag as string,
            });
            _status(message);
            if (runner == null) return;
            if (runner.PortfolioId != null) _svc.Live.Assign(runner.Key, runner.PortfolioId);
            Close(runner.Key);
        }
        finally
        {
            _start.IsEnabled = true;
        }
    }
}
