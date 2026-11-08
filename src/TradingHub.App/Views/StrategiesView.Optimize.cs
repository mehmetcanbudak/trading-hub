using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.Data;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

internal sealed partial class StrategiesView
{
    private readonly StackPanel _rangesPanel = new() { Spacing = 8 };
    private readonly TextBlock _comboCount = new() { Classes = { "muted", "small" } };
    private readonly ComboBox _metric = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _minTrades = new() { Value = 10, Minimum = 0, Increment = 5, FormatString = "0" };
    private readonly ListBox _optList = new() { Background = Brushes.Transparent };
    private readonly TextBlock _optSummary = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _optimizationInputEvidence = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private Button _optButton = null!;
    private Button _wfoButton = null!;
    private OptimizationResult? _lastOpt;

    // walk-forward
    private readonly NumericUpDown _isDays = new() { Value = 14, Minimum = 1, Maximum = 365, Increment = 1, FormatString = "0" };
    private readonly NumericUpDown _osDays = new() { Value = 7, Minimum = 1, Maximum = 365, Increment = 1, FormatString = "0" };
    private readonly ToggleSwitch _anchored = new() { OnContent = "Anchored (in-sample window grows from the start)", OffContent = "Rolling (fixed-length in-sample window)" };
    private readonly ComboBox _search = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _wfoPreview = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _wfoKpis = new() { ItemSpacing = 10, LineSpacing = 10 };
    private readonly TextBlock _wfoPolicy = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _walkForwardInputEvidence = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly EquityChart _wfoChart = new() { Height = 250, EmptyText = "Run walk-forward to view out-of-sample equity" };
    private readonly ListBox _wfoList = new() { Background = Brushes.Transparent };

    private const string OptColumns = "40,*,104,72,104,58,64,64,80";
    private const string WfoColumns = "34,150,150,*,100,100,70";

    private static readonly (FitnessMetric Metric, string Label)[] Metrics =
    {
        (FitnessMetric.NetProfitOverDrawdown, "PnL / drawdown"),
        (FitnessMetric.NetProfit, "Net PnL"),
        (FitnessMetric.ProfitFactor, "Profit factor"),
        (FitnessMetric.Sharpe, "Sharpe"),
        (FitnessMetric.Sortino, "Sortino"),
        (FitnessMetric.RecoveryFactor, "Recovery factor"),
        (FitnessMetric.Expectancy, "Expectancy"),
        (FitnessMetric.WinRate, "% winners"),
    };

    private Control BuildOptimizePage()
    {
        foreach (var (m, label) in Metrics) _metric.Items.Add(new ComboBoxItem { Content = label, Tag = m });
        _metric.SelectedIndex = 0;
        _metric.SelectionChanged += (_, _) =>
        {
            if (_metric.SelectedItem is ComboBoxItem { Tag: FitnessMetric fm }) _state.Metric = fm;
            SaveState();
        };
        _minTrades.ValueChanged += (_, _) =>
        {
            _state.MinTrades = (int)(_minTrades.Value ?? 0);
            SaveState();
        };
        _optButton = Ui.Button("Optimize", MaterialIconKind.TuneVertical, () => _ = RunOptimizationAsync(), "primary");

        var config = Ui.Card(Ui.V(14,
            Ui.H(8, Ui.Icon(MaterialIconKind.TuneVertical, 17, Ui.Accent), Ui.H2("What to test")),
            new TextBlock { Text = "Select parameters and ranges, including the bar period. Uses the data and execution settings from Backtest.", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap },
            _rangesPanel,
            _comboCount,
            Ui.Divider(),
            Ui.Field("Rank by", _metric),
            Ui.Field("Minimum trades", _minTrades, "Exclude combinations with too few trades (potentially lucky results)."),
            _optButton,
            _optSummary, _optimizationInputEvidence));
        config.Margin = new Thickness(0, 0, 10, 8);
        var left = new ScrollViewer { Content = config, Width = 380 };

        // tabla de resultados
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(OptColumns), Margin = new Thickness(12, 0, 12, 6) };
        string[] titles = { "#", "Parameters", "PnL", "PF", "Drawdown", "Trades", "% win", "Sharpe", "Metric" };
        for (int i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], Classes = { "h3" }, TextAlignment = i >= 2 ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }

        _optList.ItemTemplate = new FuncDataTemplate<OptimizationRowView>((r, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(OptColumns) };
            if (r == null) return g;
            void Cell(int c, string text, IBrush? fg = null, bool right = true)
            {
                var tb = new TextBlock { Text = text, FontSize = 12, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                if (fg != null) tb.Foreground = fg;
                Grid.SetColumn(tb, c);
                g.Children.Add(tb);
            }

            var m = r.Row.Metrics;
            Cell(0, r.Rank.ToString(), Ui.Text2, false);
            Cell(1, r.ParamsText, Ui.Text0, false);
            Cell(2, Money(m.NetProfit), m.NetProfit >= 0 ? Ui.Up : Ui.Down);
            Cell(3, m.ProfitFactor >= 999 ? "∞" : m.ProfitFactor.ToString("0.00", Es));
            Cell(4, Money(-m.MaxDrawdown), Ui.Text1);
            Cell(5, m.TotalTrades.ToString());
            Cell(6, $"{m.WinRate:0}%");
            Cell(7, m.SharpeRatio.ToString("0.00", Es));
            Cell(8, double.IsNegativeInfinity(r.Row.Fitness) ? "—" : r.Row.Fitness.ToString("0.##", Es), Ui.Accent);
            ToolTip.SetTip(g, r.ParamsText);
            return g;
        });
        _optList.DoubleTapped += (_, _) => UseSelectedOptimizationRow();
        var use = Ui.Button("Use in Backtest", MaterialIconKind.ArrowRightBoldBoxOutline, UseSelectedOptimizationRow);
        ToolTip.SetTip(use, "Copy the selected row's parameters and run a backtest (also available by double-click)");
        var tableHead = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(use, Dock.Right);
        tableHead.Children.Add(use);
        tableHead.Children.Add(Ui.V(2, Ui.H2("Results"), new TextBlock { Text = "Ranked by the selected metric. Beware of overfitting: validate with walk-forward.", Classes = { "faint", "small" } }));
        var table = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(_optList);
        var right = new DockPanel { Margin = new Thickness(18, 0, 0, 0) };
        DockPanel.SetDock(tableHead, Dock.Top);
        right.Children.Add(tableHead);
        right.Children.Add(Ui.Card(table, 8));

        var root = new DockPanel { Margin = new Thickness(20, 16) };
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(right);
        return root;
    }

    private sealed record OptimizationRowView(int Rank, OptimizationRow Row, string ParamsText);

    private void RefreshRanges()
    {
        _rangesPanel.Children.Clear();
        _metric.SelectedItem = _metric.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, _state.Metric)) ?? _metric.Items[0];
        _minTrades.Value = _state.MinTrades;
        _isDays.Value = _state.InSampleDays;
        _osDays.Value = _state.OutSampleDays;
        _anchored.IsChecked = _state.Anchored;
        _search.SelectedItem = _search.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, _state.Search)) ?? (_search.Items.Count > 0 ? _search.Items[0] : null);
        var numeric = _state.Ranges.Where(r => !r.IsPeriod).ToList();
        if (numeric.Count == 0)
            _rangesPanel.Children.Add(new TextBlock { Text = "No numeric strategy parameters. Add [Parameter] and [Optimize(min, max, step)] attributes (bar periods can still be tested).", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
        else RangeRows(numeric);

        // el período de las velas, como un parámetro más
        if (_state.Ranges.FirstOrDefault(r => r.IsPeriod) is { } period) _rangesPanel.Children.Add(PeriodRow(period));
        UpdateComboCount();
    }

    /// <summary>Las filas de los parámetros numéricos: tildado, mínimo, máximo y paso.</summary>
    private void RangeRows(IReadOnlyList<ParameterRange> ranges)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,76,76,66") };
        foreach (var (text, col) in new[] { ("Parameter", 0), ("Min.", 1), ("Max.", 2), ("Step", 3) })
        {
            var t = new TextBlock { Text = text, Classes = { "h3" }, Margin = new Thickness(col == 0 ? 0 : 6, 0, 0, 0) };
            Grid.SetColumn(t, col);
            header.Children.Add(t);
        }

        _rangesPanel.Children.Add(header);
        foreach (var r in ranges)
        {
            var range = r;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,76,76,66") };
            var check = new CheckBox { Content = r.Label, IsChecked = r.Enabled, FontSize = 12.5 };
            string fmt = r.IsInteger ? "0" : "0.####";
            var min = new NumericUpDown { Value = (decimal)r.Min, FormatString = fmt, Increment = r.IsInteger ? 1 : 0.1m, ShowButtonSpinner = false, Margin = new Thickness(6, 0, 0, 0) };
            var max = new NumericUpDown { Value = (decimal)r.Max, FormatString = fmt, Increment = r.IsInteger ? 1 : 0.1m, ShowButtonSpinner = false, Margin = new Thickness(6, 0, 0, 0) };
            var step = new NumericUpDown { Value = (decimal)r.Step, FormatString = fmt, Increment = r.IsInteger ? 1 : 0.1m, ShowButtonSpinner = false, Minimum = 0, Margin = new Thickness(6, 0, 0, 0) };
            void Changed()
            {
                range.Enabled = check.IsChecked == true;
                range.Min = (double)(min.Value ?? 0);
                range.Max = (double)(max.Value ?? 0);
                range.Step = (double)(step.Value ?? 1);
                min.IsEnabled = max.IsEnabled = step.IsEnabled = range.Enabled;
                UpdateComboCount();
                SaveState();
            }

            check.IsCheckedChanged += (_, _) => Changed();
            min.ValueChanged += (_, _) => Changed();
            max.ValueChanged += (_, _) => Changed();
            step.ValueChanged += (_, _) => Changed();
            min.IsEnabled = max.IsEnabled = step.IsEnabled = r.Enabled;
            Grid.SetColumn(min, 1);
            Grid.SetColumn(max, 2);
            Grid.SetColumn(step, 3);
            row.Children.Add(check);
            row.Children.Add(min);
            row.Children.Add(max);
            row.Children.Add(step);
            _rangesPanel.Children.Add(row);
        }
    }

    /// <summary>
    /// El período de las velas como un parámetro más: se tildan los períodos a probar y cada combinación corre en cada uno
    /// (las velas se arman con los mismos ticks, que se cargan una sola vez).
    /// </summary>
    private Control PeriodRow(ParameterRange range)
    {
        var check = new CheckBox { Content = "Bar period", IsChecked = range.Enabled, FontSize = 12.5 };
        ToolTip.SetTip(check, "Test every combination at each selected period, as another parameter");
        var chips = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, Margin = new Thickness(26, 4, 0, 0) };
        var toggles = new List<(ToggleButton Button, string Code)>();
        foreach (var label in new[] { "M1", "M5", "M15", "M30", "H1", "H4", "D1" })
        {
            ChartPeriod.TryParse(label, out var cp);
            var t = new ToggleButton { Content = label, Padding = new Thickness(8, 2), MinHeight = 24, FontSize = 11.5, IsChecked = range.Choices.Contains(cp.Code) };
            toggles.Add((t, cp.Code));
            chips.Children.Add(t);
        }

        void Changed()
        {
            range.Enabled = check.IsChecked == true;
            range.SetChoices(toggles.Where(x => x.Button.IsChecked == true).Select(x => x.Code));
            chips.IsEnabled = range.Enabled;
            UpdateComboCount();
            SaveState();
        }

        check.IsCheckedChanged += (_, _) =>
        {
            // al tildarlo sin ninguno elegido, arranca con el período del backtest
            if (check.IsChecked == true && !toggles.Any(x => x.Button.IsChecked == true) && ChartPeriod.TryParse(_period.Text, out var current))
                foreach (var x in toggles.Where(x => x.Code == current.Code)) x.Button.IsChecked = true;
            Changed();
        };
        foreach (var (t, _) in toggles) t.IsCheckedChanged += (_, _) => Changed();
        chips.IsEnabled = range.Enabled;
        var hint = new TextBlock { Text = "Each combination is tested at every selected period.", Classes = { "faint", "small" }, Margin = new Thickness(26, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        return Ui.V(4, check, chips, hint);
    }

    /// <summary>El nombre de un parámetro para mostrar (el período de las velas no es de la estrategia).</summary>
    private static string ParamName(string name) => name == ParameterRange.PeriodName ? "Period" : name;

    private void UpdateComboCount()
    {
        long n = Optimizer.Combinations(_state.Ranges);
        _comboCount.Text = n > 1 ? $"{n:N0} combinations" + (n > 3000 ? " · too many: narrow ranges or increase steps" : "") : "Select at least one parameter.";
        _comboCount.Foreground = n > 3000 ? Ui.Warn : Ui.Text1;
        UpdateWfoPreview();
    }

    private Dictionary<string, object?> BaseParameters() => new(_state.Parameters);

    private Task RunOptimizationAsync() => _svc.Work.RunAsync(RunOptimizationOwnedAsync);

    private async Task RunOptimizationOwnedAsync(CancellationToken stopping)
    {
        if (_generation == null && !await CompileOwnedAsync(stopping)) return;
        stopping.ThrowIfCancellationRequested();
        var generation = _generation!;
        var basePars = BaseParameters();
        var ranges = _state.Ranges.Select(r => r.Clone()).ToList();
        var metric = _state.Metric;
        int minTrades = _state.MinTrades;
        long combos = Optimizer.Combinations(ranges);
        if (combos <= 1)
        {
            _optSummary.Text = "Select at least one parameter with a range.";
            return;
        }

        if (combos > 20_000)
        {
            _optSummary.Text = $"{combos:N0} combinations is too many. Increase steps or deselect parameters.";
            return;
        }

        var settings = ReadSettingsFromForm(validate: true);
        if (settings == null)
        {
            _optSummary.Text = _runStatus.Text;
            return;
        }

        SaveState();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _cts = cancellation;
        var ct = cancellation.Token;
        SetRunning(true);
        _optButton.IsEnabled = false;
        _optSummary.Text = "Loading data…";
        try
        {
            var data = await LoadTicksAsync(settings, ct);
            if (data == null)
            {
                _optSummary.Text = _runStatus.Text;
                SetRunning(false);
                return;
            }

            var started = DateTime.UtcNow;
            var progress = new Progress<double>(p =>
            {
                if (ct.IsCancellationRequested || _svc.Work.IsStopping) return;
                var el = DateTime.UtcNow - started;
                var eta = p > 0.02 ? TimeSpan.FromSeconds(el.TotalSeconds / p * (1 - p)) : (TimeSpan?)null;
                _optSummary.Text = $"Testing combinations… {p * 100:0}%{(eta is { } e ? $" · about {(e.TotalMinutes >= 1 ? $"{e.TotalMinutes:0} min" : $"{e.TotalSeconds:0} s")}" : "")}";
                _runProgress.IsIndeterminate = false;
                _runProgress.Value = p;
            });
            var (res, evidence) = await Task.Run(() =>
            {
                var periods = new ParameterSpace(ranges, generation.Type).Periods;
                var input = (periods.Count > 0 ? periods : new[] { settings.Period }).All(period => Optimizer.CanCompress(generation.Type, period))
                    ? data.Compress() : data;
                var result = Optimizer.Optimize(generation.Create, basePars, ranges, input, settings, metric, minTrades, progress, ct,
                    contextHistory: Services.MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc, ct));
                return (result.WithCapturedPopulation(), RunInputEvidence.Capture(input));
            }, ct);
            ct.ThrowIfCancellationRequested();
            _optimizationInputEvidence.Text = InputEvidenceText(evidence);
            ShowOptimization(res);
            var best = res.Best;
            var run = new StrategyRun
            {
                StrategyId = generation.Id,
                StrategyName = generation.DisplayName,
                Kind = RunKind.Optimization,
                Settings = settings,
                Parameters = best?.Parameters ?? new(),
                Metrics = best?.Metrics,
                Optimization = res,
                Ticks = data.Count,
                Seconds = res.Seconds,
                InputEvidence = evidence,
            };
            await Task.Run(() => StrategyStore.Save(run, generation.Source.Text), ct);
            _optSummary.Text = $"{res.Combinations:N0} combinations on {res.Seconds:0.#} s · saved to history";
            _status($"Optimization of {generation.DisplayName} finished");
        }
        catch (OperationCanceledException)
        {
            _optSummary.Text = "Canceled.";
        }
        catch (Exception ex)
        {
            _optSummary.Text = "Error: " + ex.Message;
            CrashLog.Write(ex);
        }
        finally
        {
            SetRunning(false, _runStatus.Text ?? "");
            if (ReferenceEquals(_cts, cancellation)) _cts = null;
            _optButton.IsEnabled = true;
        }
    }

    private void ShowOptimization(OptimizationResult res)
    {
        _lastOpt = res;
        var labels = res.Ranges.Where(r => r.Enabled).ToDictionary(r => r.Name, r => r.Label);
        _optList.ItemsSource = res.Rows.Take(500).Select((row, i) => new OptimizationRowView(i + 1, row,
            string.Join("  ·  ", row.Parameters.Where(kv => labels.ContainsKey(kv.Key)).Select(kv => $"{labels[kv.Key]} {FormatValue(kv.Value)}")))).ToList();
    }

    private static string FormatValue(object? v) => v switch
    {
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.####", CultureInfo.InvariantCulture),
        System.Text.Json.JsonElement je => je.ToString(),
        _ => v?.ToString() ?? "",
    };

    private void UseSelectedOptimizationRow()
    {
        if (_optList.SelectedItem is not OptimizationRowView r) return;
        ApplyParameters(r.Row.Parameters);
    }

    /// <summary>Copia parámetros al formulario de backtest y lo corre.</summary>
    private void ApplyParameters(IReadOnlyDictionary<string, object?> pars) => _ = RunViewOperationAsync(async stopping =>
    {
        stopping.ThrowIfCancellationRequested();
        if (_generation is not { } generation) return;
        var s = generation.Create();
        s.SetParameters(_state.Parameters);
        s.SetParameters(pars);
        _state.Parameters = s.GetParameters();
        // si se optimizó el período, también va al backtest
        if (pars.TryGetValue(ParameterRange.PeriodName, out var period) && period is not null) _period.Text = FormatValue(period);
        SaveState();
        BuildParameterForm();
        _tabs.Select("backtest");
        ShowTab("backtest");
        await RunBacktestOwnedAsync(stopping);
    });

    // ===================================================================== walk-forward

    private Control BuildWalkForwardPage()
    {
        _wfoButton = Ui.Button("Run walk-forward", MaterialIconKind.ChartTimelineVariantShimmer, () => _ = RunWalkForwardAsync(), "primary");
        _isDays.ValueChanged += (_, _) =>
        {
            _state.InSampleDays = (int)(_isDays.Value ?? 14);
            UpdateWfoPreview();
            SaveState();
        };
        _osDays.ValueChanged += (_, _) =>
        {
            _state.OutSampleDays = (int)(_osDays.Value ?? 7);
            UpdateWfoPreview();
            SaveState();
        };
        _anchored.IsCheckedChanged += (_, _) =>
        {
            _state.Anchored = _anchored.IsChecked == true;
            UpdateWfoPreview();
            SaveState();
        };
        foreach (var (mode, label) in new[]
                 {
                     (SearchMode.Auto, "Automatic (grid for small searches, Bayesian for large)"),
                     (SearchMode.Grid, "Full grid (all combinations)"),
                     (SearchMode.Bayesian, "Bayesian (prioritizes promising combinations)"),
                 })
            _search.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        _search.SelectedIndex = 0;
        _search.SelectionChanged += (_, _) =>
        {
            if (_search.SelectedItem is ComboBoxItem { Tag: SearchMode m })
            {
                _state.Search = m;
                SaveState();
            }
        };

        var config = Ui.Card(Ui.V(14,
            Ui.H(8, Ui.Icon(MaterialIconKind.ChartTimelineVariantShimmer, 17, Ui.Accent), Ui.H2("Walk-forward windows")),
            new TextBlock
            {
                Text = "Optimize in sample, test selected parameters on the following unseen days, advance and repeat. Uses the ranges selected in Optimization.",
                Classes = { "muted", "small" },
                TextWrapping = TextWrapping.Wrap,
            },
            Ui.Row(10, Ui.Field("In sample (days)", _isDays), Ui.Field("Out of sample (days)", _osDays)),
            _anchored,
            Ui.Field("Parameter search", _search),
            new TextBlock
            {
                Text = "Runs each combination once with warm strategy state and selects a parameter plateau at each causal cutoff. " +
                       "The mark-to-market research splice is not an executable portfolio. CPCV is retrospective validation.",
                Classes = { "faint", "small" },
                TextWrapping = TextWrapping.Wrap,
            },
            _wfoPreview,
            _wfoButton, _walkForwardInputEvidence));
        config.Margin = new Thickness(0, 0, 10, 8);
        var left = new ScrollViewer { Content = config, Width = 380 };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(WfoColumns), Margin = new Thickness(12, 0, 12, 6) };
        string[] titles = { "#", "Optimization", "Test", "Best parameters", "In sample", "Out of sample", "Efficiency" };
        for (int i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], Classes = { "h3" }, TextAlignment = i >= 4 ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }

        _wfoList.ItemTemplate = new FuncDataTemplate<WalkForwardWindow>((w, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(WfoColumns) };
            if (w == null) return g;
            void Cell(int c, string text, IBrush? fg = null, bool right = false)
            {
                var tb = new TextBlock { Text = text, FontSize = 12, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                if (fg != null) tb.Foreground = fg;
                Grid.SetColumn(tb, c);
                g.Children.Add(tb);
            }

            Cell(0, w.Number.ToString(), Ui.Text2);
            Cell(1, $"{w.InSampleFrom:dd/MM} → {w.InSampleTo:dd/MM}", Ui.Text1);
            Cell(2, $"{w.OutSampleFrom:dd/MM} → {w.OutSampleTo:dd/MM}");
            var optimized = _state.Ranges.Where(r => r.Enabled).ToDictionary(r => r.Name, r => r.Label);
            Cell(3, string.Join("  ·  ", w.BestParameters.Where(kv => optimized.Count == 0 || optimized.ContainsKey(kv.Key)).Select(kv => $"{(optimized.TryGetValue(kv.Key, out var l) ? l : kv.Key)} {FormatValue(kv.Value)}")), Ui.Text1);
            var training = w.InSampleAvailable ? w.InSample : null;
            var testing = w.OutSampleAvailable ? w.OutSample : null;
            Cell(4, training != null ? Money(training.NetProfit) : "Unavailable", training != null ? Ui.Text1 : Ui.Warn, true);
            Cell(5, testing != null ? Money(testing.NetProfit) : "Unavailable", testing == null ? Ui.Warn : testing.NetProfit >= 0 ? Ui.Up : Ui.Down, true);
            var efficiency = training != null && testing != null ? w.Efficiency : null;
            Cell(6, efficiency.HasValue ? $"{efficiency * 100:0}%" : "Unavailable", efficiency == null ? Ui.Warn : efficiency >= 0.5 ? Ui.Up : efficiency >= 0 ? Ui.Warn : Ui.Down, true);
            ToolTip.SetTip(g, w.UnavailableReason ?? $"Training source equity {w.InSampleOpeningEquity} to {w.InSampleClosingEquity}. Test source equity {w.OutSampleOpeningEquity} to {w.OutSampleClosingEquity}. Stitched equity {w.OutSampleStitchedOpeningEquity} to {w.OutSampleStitchedClosingEquity}.");
            return g;
        });
        var table = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(_wfoList);

        var results = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        results.Children.Add(_wfoKpis);
        Grid.SetRow(_wfoPolicy, 1);
        results.Children.Add(_wfoPolicy);
        var chart = Ui.Card(_wfoChart, 6);
        chart.Margin = new Thickness(0, 14);
        Grid.SetRow(chart, 2);
        results.Children.Add(chart);
        var tableCard = Ui.Card(table, 8);
        Grid.SetRow(tableCard, 3);
        results.Children.Add(tableCard);
        results.Margin = new Thickness(18, 0, 0, 0);

        var root = new DockPanel { Margin = new Thickness(20, 16) };
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(results);
        return root;
    }

    private void UpdateWfoPreview()
    {
        var s = ReadSettingsFromForm(validate: false);
        if (s == null || _wfoPreview == null) return;
        int isD = (int)(_isDays.Value ?? 14), osD = (int)(_osDays.Value ?? 7);
        double total = (s.ToUtc - s.FromUtc).TotalDays;
        int windows = total > isD ? (int)Math.Ceiling((total - isD) / osD) : 0;
        long combos = Optimizer.Combinations(_state.Ranges);
        _wfoPreview.Text = windows > 0
            ? $"{windows} window(s) in {total:0} days · {windows * combos:N0} backtests total"
            : $"Backtest range ({total:0} days) is shorter than the optimization window: download more days or shorten the windows.";
        _wfoPreview.Foreground = windows > 0 ? Ui.Text1 : Ui.Warn;
    }

    private Task RunWalkForwardAsync() => _svc.Work.RunAsync(RunWalkForwardOwnedAsync);

    private async Task RunWalkForwardOwnedAsync(CancellationToken stopping)
    {
        if (_generation == null && !await CompileOwnedAsync(stopping)) return;
        stopping.ThrowIfCancellationRequested();
        var generation = _generation!;
        var basePars = BaseParameters();
        var ranges = _state.Ranges.Select(r => r.Clone()).ToList();
        var metric = _state.Metric;
        int minTrades = _state.MinTrades, isD = _state.InSampleDays, osD = _state.OutSampleDays;
        bool anchored = _state.Anchored;
        var search = _state.Search;
        var settings = ReadSettingsFromForm(validate: true);
        if (settings == null)
        {
            _wfoPreview.Text = _runStatus.Text;
            return;
        }

        if (Optimizer.Combinations(ranges) <= 1)
        {
            _wfoPreview.Text = "First select parameter ranges in Optimization.";
            return;
        }

        SaveState();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _cts = cancellation;
        var ct = cancellation.Token;
        SetRunning(true);
        _wfoButton.IsEnabled = false;
        try
        {
            var data = await LoadTicksAsync(settings, ct);
            if (data == null)
            {
                _wfoPreview.Text = _runStatus.Text;
                return;
            }

            var progress = new Progress<double>(p =>
            {
                if (ct.IsCancellationRequested || _svc.Work.IsStopping) return;
                _wfoPreview.Text = $"Optimizing and validating windows… {p * 100:0}%";
                _runProgress.IsIndeterminate = false;
                _runProgress.Value = p;
            });
            var (res, evidence) = await Task.Run(() =>
            {
                var result = Optimizer.WalkForward(generation.Create, basePars, ranges, data, settings, metric, isD, osD, anchored, minTrades, progress, ct,
                    Services.MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc, ct), search);
                return (result, RunInputEvidence.Capture(data));
            }, ct);
            ct.ThrowIfCancellationRequested();
            _walkForwardInputEvidence.Text = InputEvidenceText(evidence);
            ShowWalkForward(res, settings.InitialCapital);
            var run = new StrategyRun
            {
                StrategyId = generation.Id,
                StrategyName = generation.DisplayName,
                Kind = RunKind.WalkForward,
                Settings = settings,
                Parameters = res.Windows.LastOrDefault(w => w.InSampleAvailable && w.InSample != null && w.OutSampleAvailable && w.OutSample != null)?.BestParameters ?? new(),
                Metrics = res.OutSampleMetrics,
                Trades = res.OutSampleTrades,
                Equity = res.OutSampleEquity,
                WalkForward = res,
                Ticks = data.Count,
                Seconds = res.Seconds,
                InputEvidence = evidence,
            };
            await Task.Run(() => StrategyStore.Save(run, generation.Source.Text), ct);
            int completed = res.Windows.Count(w => w.InSampleAvailable && w.InSample != null && w.OutSampleAvailable && w.OutSample != null);
            _wfoPreview.Text = $"{completed} of {res.Windows.Count} windows complete in {res.Seconds:0.#} s · search {TradingHub.Common.EnglishText.Of(res.Search)}: {res.Evaluated} of {res.SpaceSize} combinations, each run once" +
                               (res.Compression < 0.99 ? $" · ticks compressed to {res.Compression * 100:0}%" : "") + " · saved to history";
        }
        catch (OperationCanceledException)
        {
            _wfoPreview.Text = "Canceled.";
        }
        catch (Exception ex)
        {
            _wfoPreview.Text = "Error: " + ex.Message;
            CrashLog.Write(ex);
        }
        finally
        {
            SetRunning(false, _runStatus.Text ?? "");
            if (ReferenceEquals(_cts, cancellation)) _cts = null;
            _wfoButton.IsEnabled = true;
        }
    }

    private void ShowWalkForward(WalkForwardResult res, decimal capital)
    {
        var m = res.OutSampleMetrics;
        var completed = res.Windows.Where(w => w.InSampleAvailable && w.InSample != null && w.OutSampleAvailable && w.OutSample != null).ToList();
        bool available = completed.Count > 0;
        bool attributed = res.AccountingPolicy != null || m.AttributionUnit == PerformanceAttributionUnit.WindowClosingFillAndBoundaryValuation;
        _wfoKpis.Children.Clear();
        _wfoKpis.Children.Add(Kpi(res.IsComplete ? "Coverage" : "Incomplete coverage", $"{completed.Count} of {res.Windows.Count}",
            available ? res.IsComplete ? "All out-of-sample windows are complete." : "Only the completed out-of-sample prefix is reported."
                : "No complete out-of-sample window is available.", res.IsComplete && available ? Ui.Up : Ui.Warn));
        _wfoKpis.Children.Add(Kpi("Out of sample", available ? Money(m.NetProfit) : "Unavailable",
            attributed ? $"{m.TotalTrades} actual closing fills · {m.AttributionSamples} contributions" : $"{m.TotalTrades} trades · PF {m.ProfitFactor:0.00}",
            !available ? Ui.Warn : m.NetProfit >= 0 ? Ui.Up : Ui.Down));
        if (attributed)
        {
            _wfoKpis.Children.Add(Kpi("Profit factor per contribution", available ? m.ProfitFactor.ToString("0.00", Es) : "Unavailable", "Closing-fill contributions and final boundary valuations", null));
            _wfoKpis.Children.Add(Kpi("Win rate per contribution", available ? $"{m.WinRate:0.#}%" : "Unavailable", $"{m.Winners} of {m.AttributionSamples} contributions", null));
            _wfoKpis.Children.Add(Kpi("Expectancy per contribution", available ? Money(m.Expectancy) : "Unavailable", "Not an independent completed-trade estimate", null));
        }
        _wfoKpis.Children.Add(Kpi("WF efficiency", available ? $"{res.WalkForwardEfficiency * 100:0}%" : "Unavailable",
            "daily PnL outside / inside sample · completed windows only", !available ? Ui.Warn : res.WalkForwardEfficiency >= 0.5 ? Ui.Up : res.WalkForwardEfficiency >= 0 ? Ui.Warn : Ui.Down));
        _wfoKpis.Children.Add(Kpi("Profitable windows", available ? $"{res.ProfitableWindowsPercent:0}%" : "Unavailable",
            $"{completed.Count(w => w.OutSample!.NetProfit > 0)} of {completed.Count} completed windows", res.ProfitableWindowsPercent >= 60 && available ? Ui.Up : Ui.Warn));
        _wfoKpis.Children.Add(Kpi("Out-of-sample DD", available ? Money(-m.MaxDrawdown) : "Unavailable", available ? $"{m.MaxDrawdownPercent:0.##}%" : "No complete out-of-sample window is available.", Ui.Down));
        if (res.PboLoss is { } loss && res.Pbo is { } pbo)
            _wfoKpis.Children.Add(Kpi("Overfitting", $"{(1 - loss) * 100:0}%", $"times the best on one half is profitable on the other · PBO {pbo * 100:0}%",
                loss < ValidationGate.MaxPboLoss ? Ui.Up : loss < ValidationGate.FailPboLoss ? Ui.Warn : Ui.Down));
        var paths = res.CpcvPaths.Where(p => p.Available && p.Net.HasValue && p.Sharpe.HasValue && p.MaxDrawdown.HasValue).OrderBy(p => p.Net).ToList();
        if (paths.Count > 0)
        {
            int good = paths.Count(p => p.Net > 0);
            decimal median = paths[paths.Count / 2].Net!.Value;
            _wfoKpis.Children.Add(Kpi("Paths (CPCV)", $"{good} of {paths.Count}",
                $"profitable · median {Money(median)} · {paths.Count} of {res.CpcvPaths.Count} paths available",
                median <= 0 ? Ui.Down : (double)good / paths.Count >= ValidationGate.MinProfitablePaths ? Ui.Up : Ui.Warn));
        }
        else
            _wfoKpis.Children.Add(Kpi("Paths (CPCV)", "Unavailable", $"0 of {res.CpcvPaths.Count} paths available. No eligible retrospective CPCV path is available.", Ui.Warn));
        _wfoPolicy.Text = attributed
            ? $"Accounting policy {res.AccountingPolicy ?? "Unknown"}. Attribution policy {res.AttributionPolicy ?? "Unknown"}. " +
              "Warm mark-to-market research splice, not an executable portfolio. Positions are not transferred between configurations. Sizing does not use stitched capital, and deployment turnover is not charged. " +
              "Profit factor, win rate and expectancy use closing-fill contributions and final boundary valuations. Only actual closing fills count for eligibility. " +
              $"CPCV policy {res.CpcvPolicy ?? "Unknown"}. Retrospective validation uses only available paths for shares and medians."
            : "";
        var unavailable = res.Windows.Where(w => !w.InSampleAvailable || !w.OutSampleAvailable).Select(w => w.UnavailableReason)
            .Concat(res.CpcvPaths.Where(p => !p.Available).Select(p => p.UnavailableReason)).Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct();
        foreach (var reason in unavailable) _wfoPolicy.Text += "\n" + reason;
        _wfoChart.SetData(res.OutSampleEquity, capital, marks: completed.Select(w => (w.OutSampleFrom, $"V{w.Number}")).ToList());
        _wfoList.ItemsSource = res.Windows;
    }
}
