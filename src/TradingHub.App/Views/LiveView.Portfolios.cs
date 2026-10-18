using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

/// <summary>
/// Portafolios en vivo: el automático (todas las que corren sin un portafolio tuyo) y los que armás. Para cada uno:
/// resultado conjunto y de cada estrategia, curva, correlación entre estrategias y su agente de riesgo.
/// </summary>
internal sealed partial class LiveView
{
    private readonly WrapPanel _portfolioChips = new() { ItemSpacing = 8, LineSpacing = 8 };
    private readonly StackPanel _portfolioDetail = new() { Spacing = 16 };
    private readonly EquityChart _portfolioChart = new() { Height = 230, EmptyText = "The combined equity curve appears when portfolio strategies close trades" };
    private string? _portfolioId;
    private string _portfolioKey = "";

    // agente de riesgo (controles del portafolio elegido)
    private readonly ToggleSwitch _agentOn = new() { OnContent = "Enabled", OffContent = "Disabled" };
    private readonly ComboBox _agentMode = new() { MinWidth = 280 };
    private readonly TextBox _agentInstructions = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, Watermark = "Optional agent instructions: prioritize NQ strategies; never exceed ×2 in crypto…" };
    private readonly NumericUpDown _agentDailyRisk = AgentNum(0.25m, 0.1m, 50, "0.##");
    private readonly NumericUpDown _agentMaxDd = AgentNum(0.5m, 0.5m, 90, "0.##");
    private readonly NumericUpDown _agentMinSize = AgentNum(0.05m, 0, 10, "0.##");
    private readonly NumericUpDown _agentMaxSize = AgentNum(0.25m, 0.1m, 50, "0.##");
    private readonly NumericUpDown _agentEvery = AgentNum(15, 10, 1440, "0");
    private readonly TextBlock _agentState = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _agentProposals = new() { Spacing = 10 };
    private Button _agentRun = null!;
    private ComboBox? _agentModel, _agentEffort;
    private bool _agentFilling;
    private int _agentVersion = -1;
    private Border? _agentCard;
    private Border? _chartCard;
    private readonly TextBlock _agentRealNote = new() { Text = "This portfolio includes real accounts: the agent only proposes changes, and each requires your approval.", FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private static NumericUpDown AgentNum(decimal increment, decimal min, decimal max, string format) => new()
    {
        Increment = increment,
        Minimum = min,
        Maximum = max,
        FormatString = format,
        NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private Control BuildPortfoliosPage()
    {
        var create = Ui.Button("New portfolio", MaterialIconKind.FolderPlusOutline, () => _ = EditPortfolioAsync(null));
        var bar = new DockPanel();
        DockPanel.SetDock(create, Dock.Right);
        bar.Children.Add(create);
        bar.Children.Add(_portfolioChips);

        _agentMode.Items.Add(new ComboBoxItem { Content = "Proposals: approve each change manually", Tag = "propose" });
        _agentMode.Items.Add(new ComboBoxItem { Content = "Automatic: applies changes directly (simulated accounts only)", Tag = "auto" });
        _agentOn.IsCheckedChanged += (_, _) => SaveAgent();
        _agentMode.SelectionChanged += (_, _) => SaveAgent();
        foreach (var n in new[] { _agentDailyRisk, _agentMaxDd, _agentMinSize, _agentMaxSize, _agentEvery }) n.ValueChanged += (_, _) => SaveAgent();
        _agentInstructions.LostFocus += (_, _) => SaveAgent();
        _agentRun = Ui.Button("Review now", MaterialIconKind.ShieldHalfFull, () => _ = RunAgentAsync(), "primary");

        var intro = Ui.Faint("Strategies running together. The automatic portfolio groups live strategies without a custom portfolio. Each portfolio can use a Claude risk agent to allocate risk among its strategies.");
        intro.TextWrapping = TextWrapping.Wrap;
        var root = Ui.V(16, intro, bar, _portfolioDetail);
        root.Margin = new Thickness(24, 18, 24, 28);
        return new ScrollViewer { Content = root };
    }

    private void RefreshPortfolios(bool force)
    {
        var entries = _svc.Live.Entries();
        var portfolios = _svc.Live.Portfolios(entries);
        if (_portfolioId == null || portfolios.All(p => p.Def.Id != _portfolioId)) _portfolioId = portfolios.FirstOrDefault()?.Def.Id;
        var current = portfolios.FirstOrDefault(p => p.Def.Id == _portfolioId);

        string key = string.Join("|", portfolios.Select(p => $"{p.Def.Id}:{p.Def.Name}:{p.Def.Capital}:{string.Join(",", p.Members.Select(m => $"{m.Key}{m.StateLabel}{m.Ledger.Fills.Count}{m.Runner?.SizeMultiplier ?? m.Info.Size}{Math.Round(m.OpenPnL, 0)}"))}")) +
                     $"|{_portfolioId}|{LiveStore.ProposalsStamp.Ticks}";
        bool agentChanged = _svc.Live.RiskAgent.Version != _agentVersion;
        if (!force && key == _portfolioKey && !agentChanged) return;
        _portfolioKey = key;
        _agentVersion = _svc.Live.RiskAgent.Version;

        _portfolioChips.Children.Clear();
        foreach (var p in portfolios)
        {
            var id = p.Def.Id;
            var chip = new ToggleButton
            {
                Content = Ui.H(6, Ui.Icon(p.Def.Auto ? MaterialIconKind.AutoFix : MaterialIconKind.BriefcaseOutline, 14, Ui.Text1),
                    new TextBlock { Text = $"{p.Def.Name} ({p.Members.Count})" },
                    p.Def.Agent.Enabled ? Ui.Icon(MaterialIconKind.ShieldHalfFull, 13, Ui.Accent) : new Panel()),
                IsChecked = id == _portfolioId,
            }.Classes("seg");
            chip.Click += (_, _) =>
            {
                _portfolioId = id;
                RefreshPortfolios(force: true);
            };
            _portfolioChips.Children.Add(chip);
        }

        _portfolioDetail.Children.Clear();
        if (current == null)
        {
            _portfolioDetail.Children.Add(Ui.Card(Ui.V(8,
                new TextBlock { Text = "No portfolios yet.", Classes = { "muted" } },
                Ui.Faint("An automatic portfolio appears when live strategies run. Create custom ones with New portfolio.").Also(t => t.TextWrapping = TextWrapping.Wrap)), 16));
            return;
        }

        BuildPortfolioDetail(current);
    }

    private void BuildPortfolioDetail(LivePortfolioView p)
    {
        var def = p.Def;
        var analysis = LiveCenter.Analyze(p);
        var m = analysis.Metrics;
        decimal open = p.Members.Sum(x => x.OpenPnL);

        // encabezado y acciones
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (!def.Auto) actions.Children.Add(Ui.Button("Add new strategy", MaterialIconKind.Play, () => OpenLaunch(portfolioId: def.Id)));
        if (p.Members.Any(x => !x.Running && !x.Locked)) actions.Children.Add(Ui.Button("Start all", MaterialIconKind.PlayCircleOutline, () => _ = StartAllAsync(p), "primary"));
        if (p.Members.Any(x => x.Running && !x.Locked)) actions.Children.Add(Ui.Button("Stop all", MaterialIconKind.StopCircleOutline, () => _ = StopAllAsync(p)));
        actions.Children.Add(Ui.Button("Edit", MaterialIconKind.PencilOutline, () => _ = EditPortfolioAsync(def)));
        if (!def.Auto) actions.Children.Add(Ui.Button("Delete", MaterialIconKind.DeleteOutline, () => _ = DeletePortfolioAsync(def), "danger"));
        var head = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(Ui.V(3, Ui.H2(def.Name),
            Ui.Faint($"{(def.Auto ? "Automatic: all live strategies without a custom portfolio · " : "")}{p.Members.Count} strategy/strategies · reference capital {def.Capital.ToString("#,##0", Es)}" +
                     $"{(p.HasReal ? " · includes REAL accounts" : "")}").Also(t => t.TextWrapping = TextWrapping.Wrap)));
        _portfolioDetail.Children.Add(head);

        // números del portafolio
        var kpis = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
        kpis.Children.Add(Kpi("PnL", Money(m.NetProfit + open), $"closed {Money(m.NetProfit)} · open {Money(open)}", m.NetProfit + open >= 0 ? Ui.Up : Ui.Down));
        kpis.Children.Add(Kpi("Trades", m.TotalTrades.ToString(Inv), m.TotalTrades > 0 ? $"{m.WinRate:0} % winners" : "none yet", null,
            m.TotalTrades > 0 ? () => OpenTradesOf(p) : null));
        kpis.Children.Add(Kpi("Profit factor", m.TotalTrades > 0 ? (m.ProfitFactor >= 999 ? "∞" : m.ProfitFactor.ToString("0.00", Es)) : "—", m.TotalTrades > 0 ? $"average {Money(m.AverageTrade)} per trade" : ""));
        kpis.Children.Add(Kpi("Max drawdown", (-m.MaxDrawdown).ToString("#,##0.00", Es), def.Capital > 0 ? $"{(m.MaxDrawdown / def.Capital * 100):0.#} % of capital" : ""));
        kpis.Children.Add(Kpi("Sharpe", analysis.Days.Count >= 5 ? m.SharpeRatio.ToString("0.00", Es) : "—", analysis.Days.Count >= 5 ? "annualized, daily" : "requires more than a week of data"));
        if (p.Members.Count > 1)
            kpis.Children.Add(Kpi("Average correlation", analysis.Days.Count >= 3 ? analysis.AverageCorrelation.ToString("0.00", Es) : "—",
                analysis.Days.Count >= 3 ? $"maximum {analysis.MaxCorrelation.ToString("0.00", Es)} · DD {analysis.DrawdownReductionPercent:0} % below the sum" : "requires more than a couple of days",
                analysis.AverageCorrelation > 0.6 ? Ui.Warn : null, null, "Pearson correlation of daily strategy PnL: near 1 = move together (little diversification)"));
        _portfolioDetail.Children.Add(kpis);

        _portfolioChart.SetData(analysis.Equity, def.Capital > 0 ? def.Capital : 25_000m);
        _chartCard ??= Ui.Card(_portfolioChart, 8);
        _portfolioDetail.Children.Add(_chartCard);

        // cada estrategia
        _portfolioDetail.Children.Add(Ui.H3("Strategies"));
        _portfolioDetail.Children.Add(Ui.Card(MembersTable(p, analysis), 10));

        // correlación
        if (p.Members.Count > 1)
        {
            _portfolioDetail.Children.Add(Ui.H3("Strategy correlation"));
            _portfolioDetail.Children.Add(Ui.Card(CorrelationMatrix(p, analysis), 12));
        }

        // agente de riesgo
        _portfolioDetail.Children.Add(Ui.H3("Risk agent"));
        _portfolioDetail.Children.Add(AgentCard(p));
        var mine = LiveStore.LoadProposals().Where(x => x.PortfolioId == def.Id).OrderByDescending(x => x.CreatedUtc).Take(6).ToList();
        if (mine.Count > 0)
        {
            _portfolioDetail.Children.Add(Ui.H3("Agent proposals"));
            foreach (var prop in mine) _portfolioDetail.Children.Add(ProposalCard(prop, () => RefreshPortfolios(force: true)));
        }
    }

    private Control MembersTable(LivePortfolioView p, PortfolioResult analysis)
    {
        const string cols = "2.4*,1*,0.8*,1*,0.6*,0.7*,1*,0.9*,0.9*";
        var table = new StackPanel();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), Margin = new Thickness(6, 2, 6, 8) };
        string[] titles = { "Strategy", "Status", "Size", "Net", "Ops", "PF", "Max DD", "% of total", "Avg. corr." };
        for (int i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], Classes = { "h3" }, HorizontalAlignment = i >= 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(6, 0) };
            Grid.SetColumn(t, i);
            head.Children.Add(t);
        }

        table.Children.Add(head);
        if (p.Members.Count == 0)
        {
            table.Children.Add(new TextBlock { Text = "No strategies: use Edit to add running strategies, or Add new strategy.", Classes = { "muted", "small" }, Margin = new Thickness(12, 4), TextWrapping = TextWrapping.Wrap });
            return table;
        }

        decimal total = p.Members.Sum(x => x.Ledger.Realized);
        int n = p.Members.Count;
        for (int i = 0; i < n; i++)
        {
            var e = p.Members[i];
            var cm = i < analysis.ComponentMetrics.Count ? analysis.ComponentMetrics[i] : new PerformanceMetrics();
            double corr = 0;
            if (n > 1 && analysis.Correlation.GetLength(0) == n)
            {
                for (int j = 0; j < n; j++)
                    if (j != i) corr += analysis.Correlation[i, j];
                corr /= n - 1;
            }

            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), Margin = new Thickness(6, 0) };
            decimal size = e.Runner?.SizeMultiplier ?? e.Info.Size;
            Control[] cells =
            {
                Ui.V(1, new TextBlock { Text = e.Info.Strategy, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{e.Info.Symbol} {e.Info.Period} · {TradingHub.Common.EnglishText.Of(e.Info.Account)}{(e.Locked ? $" · {e.SourceLabel} 🔒" : "")}", Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis }),
                new TextBlock { Text = e.StateLabel, FontSize = 12, Foreground = e.Running ? Ui.Up : Ui.Text2 },
                Num("×" + size.ToString("0.##", Es)),
                Num(Money(e.Net), e.Net >= 0 ? Ui.Up : Ui.Down),
                Num(e.Ledger.Trades.Count.ToString(Inv)),
                Num(cm.TotalTrades > 0 ? (cm.ProfitFactor >= 999 ? "∞" : cm.ProfitFactor.ToString("0.00", Es)) : "—"),
                Num((-cm.MaxDrawdown).ToString("#,##0.00", Es)),
                Num(total != 0 ? $"{e.Ledger.Realized / Math.Abs(total) * 100:0} %" : "—"),
                Num(n > 1 && analysis.Days.Count >= 3 ? corr.ToString("0.00", Es) : "—", corr > 0.6 ? Ui.Warn : null),
            };
            for (int c = 0; c < cells.Length; c++)
            {
                cells[c].Margin = new Thickness(6, 8);
                cells[c].VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(cells[c], c);
                g.Children.Add(cells[c]);
            }

            var row = new Border { Child = g, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            var entry = e;
            row.PointerPressed += (_, ev) =>
            {
                if (ev.GetCurrentPoint(row).Properties.IsLeftButtonPressed) OpenManage(entry);
            };
            table.Children.Add(row);
        }

        return table;
    }

    /// <summary>Matriz de correlación del resultado diario (rojo = se mueven juntas, verde = se compensan).</summary>
    private static Control CorrelationMatrix(LivePortfolioView p, PortfolioResult analysis)
    {
        int n = p.Members.Count;
        if (analysis.Correlation.GetLength(0) != n || analysis.Days.Count < 3)
            return new TextBlock { Text = "At least a couple of trading days are required to measure correlation.", Classes = { "muted", "small" } };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(220)));
        for (int j = 0; j < n; j++) g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(74)));
        for (int i = 0; i <= n; i++) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string Short(LiveEntry e) => $"{e.Info.Strategy} · {e.Info.Symbol}";
        for (int j = 0; j < n; j++)
        {
            var h = new TextBlock { Text = (j + 1).ToString(Inv), Classes = { "h3" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            ToolTip.SetTip(h, Short(p.Members[j]));
            Grid.SetColumn(h, j + 1);
            g.Children.Add(h);
        }

        for (int i = 0; i < n; i++)
        {
            var label = new TextBlock { Text = $"{i + 1}. {Short(p.Members[i])}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) };
            Grid.SetRow(label, i + 1);
            g.Children.Add(label);
            for (int j = 0; j < n; j++)
            {
                double c = analysis.Correlation[i, j];
                var color = i == j ? Color.FromArgb(40, 128, 128, 128)
                    : c >= 0 ? Color.FromArgb((byte)Math.Clamp(30 + c * 170, 30, 200), 220, 70, 70)
                    : Color.FromArgb((byte)Math.Clamp(30 - c * 170, 30, 200), 40, 180, 110);
                var cell = new Border
                {
                    Background = new SolidColorBrush(color),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(5),
                    Height = 30,
                    Child = new TextBlock { Text = i == j ? "—" : c.ToString("0.00", Es), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeight.SemiBold },
                };
                if (i != j) ToolTip.SetTip(cell, $"{Short(p.Members[i])} ↔ {Short(p.Members[j])}: {c.ToString("0.00", Es)}");
                Grid.SetRow(cell, i + 1);
                Grid.SetColumn(cell, j + 1);
                g.Children.Add(cell);
            }
        }

        var note = Ui.Faint("Pearson correlation of daily PnL. Red: win and lose on the same days (similar exposure). Green: offset one another (diversification).");
        note.TextWrapping = TextWrapping.Wrap;
        return Ui.V(10, new ScrollViewer { Content = g, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, note);
    }

    private Control AgentCard(LivePortfolioView p)
    {
        var a = p.Def.Agent;
        _agentFilling = true;
        _agentOn.IsChecked = a.Enabled;
        _agentMode.SelectedItem = _agentMode.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == a.Mode) ?? _agentMode.Items[0];
        if (!_agentInstructions.IsFocused) _agentInstructions.Text = a.Instructions;
        _agentDailyRisk.Value = a.DailyRiskPercent;
        _agentMaxDd.Value = a.MaxDrawdownPercent;
        _agentMinSize.Value = a.MinSize;
        _agentMaxSize.Value = a.MaxSize;
        _agentEvery.Value = a.EveryMinutes;
        _agentModel ??= AgentChoices.Combo(AgentChoices.Models, a.Model, _ => SaveAgent(), 160);
        _agentEffort ??= AgentChoices.Combo(AgentChoices.Efforts, a.Effort, _ => SaveAgent(), 140);
        _agentModel.SelectedItem = _agentModel.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == a.Model) ?? _agentModel.SelectedItem;
        _agentEffort.SelectedItem = _agentEffort.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == a.Effort) ?? _agentEffort.SelectedItem;
        _agentFilling = false;

        var agent = _svc.Live.RiskAgent;
        bool busy = agent.IsRunning(p.Def.Id);
        _agentRun.IsEnabled = !busy && p.Members.Any(m => !m.Locked && (m.Running || m.WaitingResume));
        var lines = new List<string>();
        if (busy) lines.Add(agent.Activity(p.Def.Id) ?? "Working…");
        if (a.LastRunUtc is { } last) lines.Add($"Last review {Ui.Ago(last)} · {LiveRiskAgent.RunsToday(a)}/{a.MaxRunsPerDay} today · US$ {a.CostTotal.ToString("0.00", Es)} estimated total");
        if (a.LastError is { Length: > 0 } err) lines.Add("⚠ " + err);
        else if (a.LastSummary.Length > 0) lines.Add(a.LastSummary);
        if (lines.Count == 0) lines.Add(a.Enabled ? "Reviews new trades at the configured interval and leaves proposals here." : "Disabled. Enable to allocate risk among strategies in this portfolio.");
        _agentState.Text = string.Join("\n", lines);
        _agentState.Foreground = a.LastError != null ? Ui.Warn : null;
        bool real = p.HasReal;
        if (_agentMode.Items[1] is ComboBoxItem autoItem) autoItem.IsEnabled = !real;
        _agentRealNote.IsVisible = real;
        _agentRealNote.Foreground = Ui.Warn;
        if (_agentCard != null) return _agentCard;

        Grid Row(params Control[] cs)
        {
            var g = new Grid();
            for (int i = 0; i < cs.Length; i++)
            {
                if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(12)));
                g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                Grid.SetColumn(cs[i], g.ColumnDefinitions.Count - 1);
                g.Children.Add(cs[i]);
            }

            return g;
        }

        var explain = Ui.Faint("Claude (Opus, extra effort) reviews each strategy's PnL, streak, drawdown versus history, volatility and correlation to allocate risk: " +
                               "position sizes and risk limits within your bounds. Real-account changes always require approval.");
        explain.TextWrapping = TextWrapping.Wrap;
        var top = new DockPanel();
        var run = Ui.H(10, _agentRun);
        DockPanel.SetDock(run, Dock.Right);
        top.Children.Add(run);
        top.Children.Add(Ui.H(12, _agentOn, _agentMode));
        var body = Ui.V(12,
            top,
            explain,
            _agentRealNote,
            Row(Ui.Field("Daily risk (% of capital)", _agentDailyRisk), Ui.Field("Portfolio max drawdown (%)", _agentMaxDd), Ui.Field("Review interval (min)", _agentEvery)),
            Row(Ui.Field("Minimum size (×)", _agentMinSize), Ui.Field("Maximum size (×)", _agentMaxSize), Ui.Field("Model and effort", Ui.H(8, _agentModel!, _agentEffort!))),
            Ui.Field("Instructions", _agentInstructions),
            _agentState);
        _agentCard = Ui.Card(body, 16);
        return _agentCard;
    }

    private void SaveAgent()
    {
        if (_agentFilling || _portfolioId == null) return;
        var defs = LiveStore.LoadPortfolios();
        var def = defs.FirstOrDefault(d => d.Id == _portfolioId);
        if (def == null) return;
        var a = def.Agent;
        bool wasOn = a.Enabled;
        a.Enabled = _agentOn.IsChecked == true;
        a.Mode = (_agentMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "propose";
        var view = _svc.Live.Portfolios().FirstOrDefault(p => p.Def.Id == def.Id);
        if (a.Mode == "auto" && view?.HasReal == true) a.Mode = "propose";
        a.Instructions = _agentInstructions.Text?.Trim() ?? "";
        a.DailyRiskPercent = _agentDailyRisk.Value ?? a.DailyRiskPercent;
        a.MaxDrawdownPercent = _agentMaxDd.Value ?? a.MaxDrawdownPercent;
        a.MinSize = _agentMinSize.Value ?? a.MinSize;
        a.MaxSize = Math.Max(a.MinSize, _agentMaxSize.Value ?? a.MaxSize);
        a.EveryMinutes = (int)(_agentEvery.Value ?? a.EveryMinutes);
        if (_agentModel?.SelectedItem is ComboBoxItem { Tag: string m }) a.Model = m;
        if (_agentEffort?.SelectedItem is ComboBoxItem { Tag: string ef }) a.Effort = ef;
        LiveStore.SavePortfolios(defs);
        if (a.Enabled && !wasOn) _status($"Risk agent enabled for «{def.Name}»: reviews new trades (or click Review now)");
    }

    private async Task RunAgentAsync()
    {
        if (_portfolioId == null) return;
        SaveAgent();
        _agentRun.IsEnabled = false;
        _status("Risk agent is reviewing the portfolio…");
        var id = _portfolioId;
        var task = _svc.Live.RiskAgent.RunAsync(id, manual: true);
        RefreshPortfolios(force: true);
        var err = await task;
        _status(err ?? "Risk agent finished: review its proposals");
        RefreshPortfolios(force: true);
    }

    private async Task EditPortfolioAsync(LivePortfolioDef? def)
    {
        var w = new LivePortfolioWindow(_svc, def);
        var id = await w.ShowDialog<string?>(_window);
        if (id == null) return;
        _portfolioId = id;
        RefreshPortfolios(force: true);
    }

    private async Task DeletePortfolioAsync(LivePortfolioDef def)
    {
        if (!await ConfirmDialog.ShowAsync(_window, "Delete portfolio", $"Deletes «{def.Name}». Its strategies keep running and move to the automatic portfolio.", "Delete", danger: true)) return;
        _svc.Live.DeletePortfolio(def.Id);
        _portfolioId = null;
        RefreshPortfolios(force: true);
    }

    private async Task StartAllAsync(LivePortfolioView p)
    {
        var pending = p.Members.Where(m => !m.Running && !m.Locked).ToList();
        if (pending.Any(m => m.Info.IsReal) &&
            !await ConfirmDialog.ShowAsync(_window, "Execute on REAL accounts", $"Some of the {pending.Count} strategies use real accounts: they will submit real-money orders without individual confirmation.", "Start all", danger: true))
            return;
        var errors = await _svc.Live.StartAllAsync(p);
        _status(errors.Count == 0 ? $"«{p.Def.Name}»: {pending.Count} strategy/strategies running" : "Some did not start: " + string.Join(" · ", errors));
        RefreshPortfolios(force: true);
    }

    private async Task StopAllAsync(LivePortfolioView p)
    {
        if (!await ConfirmDialog.ShowAsync(_window, "Stop all", $"Stops strategies in «{p.Def.Name}». Open positions remain unchanged.", "Stop all")) return;
        await _svc.Live.StopAllAsync(p, close: false);
        _status($"«{p.Def.Name}»: strategies stopped");
        RefreshPortfolios(force: true);
    }

    private void OpenTradesOf(LivePortfolioView p)
    {
        if (p.Members.Count == 1) OpenTrades(p.Members[0].Key, $"Trades · {p.Members[0].Info.Strategy}");
        else OpenTrades(null, $"Live trades · {p.Def.Name}", p.Members.Select(m => m.Key).ToList());
    }
}
