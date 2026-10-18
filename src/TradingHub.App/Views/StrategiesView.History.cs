using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Windows;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

internal sealed partial class StrategiesView
{
    private readonly ListBox _historyList = new() { Background = Brushes.Transparent };
    private readonly Ui.Segmented<string> _historyFilter = new(new[]
    {
        ("all", "All", (string?)null),
        ("bt", "Backtests", null),
        ("opt", "Optimizations", null),
        ("wfo", "Walk-forward", null),
        ("star", "★", (string?)"Favorites"),
    });
    private readonly StackPanel _historyDetail = new() { Spacing = 14 };
    private readonly EquityChart _historyChart = new() { Height = 240, EmptyText = "No equity curve" };
    private string _historyKind = "all";

    private Control BuildHistoryPage()
    {
        _historyFilter.Select("all");
        _historyFilter.SelectionChanged += k =>
        {
            _historyKind = k;
            RefreshHistory();
        };
        _historyList.ItemTemplate = new FuncDataTemplate<StrategyRunSummary>((s, _) =>
        {
            if (s == null) return new TextBlock();
            var icon = s.Kind switch
            {
                RunKind.Optimization => MaterialIconKind.TuneVertical,
                RunKind.WalkForward => MaterialIconKind.ChartTimelineVariantShimmer,
                _ => MaterialIconKind.ChartLine,
            };
            var kind = s.Kind switch { RunKind.Optimization => $"Optimization · {s.Combinations:N0} comb.", RunKind.WalkForward => $"Walk-forward · efficiency {s.Efficiency * 100:0}%", _ => "Backtest" };
            var title = Ui.H(6, new TextBlock { Text = $"{s.Symbol} · {s.Period}", FontWeight = FontWeight.SemiBold }, new TextBlock { Text = s.Starred ? "★" : "", Foreground = Ui.Accent });
            var text = Ui.V(2,
                title,
                new TextBlock { Text = $"{kind} · {s.FromUtc:dd/MM} → {s.ToUtc.AddDays(-1):dd/MM}", Classes = { "muted", "small" } },
                new TextBlock { Text = s.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Es) + (string.IsNullOrWhiteSpace(s.Notes) ? "" : " · " + s.Notes), Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis });
            var result = Ui.V(2,
                new TextBlock { Text = Money(s.NetProfit), FontWeight = FontWeight.SemiBold, Foreground = s.NetProfit >= 0 ? Ui.Up : Ui.Down, HorizontalAlignment = HorizontalAlignment.Right },
                new TextBlock { Text = $"PF {s.ProfitFactor:0.00} · {s.Trades} op.", Classes = { "faint", "small" }, HorizontalAlignment = HorizontalAlignment.Right });
            var row = new DockPanel { Margin = new Thickness(2, 4) };
            var ic = Ui.Icon(icon, 18, Ui.Accent);
            ic.Margin = new Thickness(0, 2, 12, 0);
            ic.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(ic, Dock.Left);
            DockPanel.SetDock(result, Dock.Right);
            row.Children.Add(ic);
            row.Children.Add(result);
            row.Children.Add(text);
            return row;
        });
        _historyList.SelectionChanged += (_, _) => ShowHistoryDetail(_historyList.SelectedItem as StrategyRunSummary);

        var left = new DockPanel { Width = 430 };
        var head = Ui.V(10, Ui.H2("Saved runs"), _historyFilter);
        head.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        left.Children.Add(Ui.Card(_historyList, 8));

        var right = new ScrollViewer { Content = _historyDetail, Margin = new Thickness(18, 0, 0, 0) };
        var root = new DockPanel { Margin = new Thickness(20, 16) };
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(right);
        ShowHistoryDetail(null);
        return root;
    }

    private void RefreshHistory()
    {
        if (_current == null)
        {
            _historyList.ItemsSource = null;
            return;
        }

        var list = StrategyStore.List(_current.Id).Where(s => _historyKind switch
        {
            "bt" => s.Kind == RunKind.Backtest,
            "opt" => s.Kind == RunKind.Optimization,
            "wfo" => s.Kind == RunKind.WalkForward,
            "star" => s.Starred,
            _ => true,
        }).ToList();
        _historyList.ItemsSource = list;
        if (list.Count == 0) ShowHistoryDetail(null);
    }

    /// <summary>Arma el informe profesional de una corrida (sin análisis escrito) y lo abre.</summary>
    private void MakeReport(StrategyRun run)
    {
        if (_current == null) return;
        try
        {
            var input = new Lab.ReportInput
            {
                Title = $"{_current.DisplayName} · {run.Settings.Symbol} {run.Settings.Period}",
                StrategyFile = _current.FileName,
                StrategyName = _current.DisplayName,
                Description = _current.Type != null ? Strategy.Description(_current.Type) : null,
                Analysis = string.IsNullOrWhiteSpace(run.Notes) ? "" : run.Notes,
            };
            switch (run.Kind)
            {
                case RunKind.Optimization: input.Optimization = run; break;
                case RunKind.WalkForward: input.WalkForward = run; break;
                default: input.Backtest = run; break;
            }

            var (markdown, images) = Lab.ReportBuilder.Build(input);
            var saved = Lab.ReportStore.Save(input.Title, markdown, _current.FileName, "", images);
            RefreshReports();
            OpenReport(saved.Path);
            _status($"Report ready: {System.IO.Path.GetFileName(saved.Path)}");
        }
        catch (Exception ex)
        {
            _status("Could not generate report: " + ex.Message);
            CrashLog.Write(ex);
        }
    }

    private void ShowHistoryDetail(StrategyRunSummary? summary)
    {
        _historyDetail.Children.Clear();
        if (summary == null || _current == null)
        {
            var empty = Ui.V(10,
                Ui.Icon(MaterialIconKind.History, 40, Ui.Text2),
                new TextBlock { Text = "Every backtest, optimization and walk-forward is saved here", Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "with its parameters, metrics and exact source code.", Classes = { "faint", "small" }, HorizontalAlignment = HorizontalAlignment.Center });
            ((StackPanel)empty).Children[0].HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 80, 0, 0);
            _historyDetail.Children.Add(empty);
            return;
        }

        var run = StrategyStore.Load(_current.Id, summary.Id);
        if (run == null)
        {
            _historyDetail.Children.Add(new TextBlock { Text = "Could not read run (was the file deleted?).", Classes = { "muted" } });
            return;
        }

        // encabezado + acciones
        var star = Ui.IconButton(run.Starred ? MaterialIconKind.Star : MaterialIconKind.StarOutline, "Mark as favorite", () =>
        {
            StrategyStore.Annotate(_current.Id, run.Id, null, !run.Starred);
            RefreshHistory();
        });
        var use = Ui.Button("Use parameters", MaterialIconKind.ArrowRightBoldBoxOutline, () => ApplyParameters(run.Parameters));
        var code = Ui.Button("View code", MaterialIconKind.CodeBraces, () =>
        {
            var src = StrategyStore.LoadCode(_current.Id, run.CodeHash);
            if (src == null || TopLevel.GetTopLevel(this) is not Window w) return;
            new CodeViewerWindow($"{run.StrategyName} · version {run.CodeHash}", src).Show(w);
        });
        code.IsEnabled = !string.IsNullOrEmpty(run.CodeHash);
        var delete = Ui.IconButton(MaterialIconKind.DeleteOutline, "Delete run", async () =>
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            if (!await ConfirmDialog.ShowAsync(w, "Delete run", "This run will be deleted from history.", "Delete", danger: true)) return;
            StrategyStore.Delete(_current.Id, run.Id);
            RefreshHistory();
        });
        string kind = run.Kind switch { RunKind.Optimization => "Optimization", RunKind.WalkForward => "Walk-forward", _ => "Backtest" };
        var title = Ui.V(3,
            new TextBlock { Text = $"{kind} · {run.Settings.Symbol} · {run.Settings.Period}", Classes = { "h2" } },
            new TextBlock { Text = $"{run.Settings.FromUtc:dd/MM/yyyy} → {run.Settings.ToUtc.AddDays(-1):dd/MM/yyyy} · {run.Ticks:N0} ticks · {run.Seconds:0.#} s · {run.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}", Classes = { "muted", "small" } });
        var header = new DockPanel();
        var report = Ui.Button("Report", MaterialIconKind.FileChartOutline, () => MakeReport(run));
        ToolTip.SetTip(report, "Detailed report for this run with all metrics, tables and charts (opens in Reports)");
        var actions = Ui.H(6, star, delete, code, report, use);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(title);
        _historyDetail.Children.Add(header);

        // notas
        var notes = new TextBox { Text = run.Notes, Watermark = "Notes (changes, conclusions…)", AcceptsReturn = false };
        notes.LostFocus += (_, _) =>
        {
            if (notes.Text == run.Notes) return;
            StrategyStore.Annotate(_current.Id, run.Id, notes.Text ?? "", null);
            run.Notes = notes.Text ?? "";
        };
        _historyDetail.Children.Add(notes);

        if (run.Metrics is { } m)
        {
            var kpis = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
            kpis.Children.Add(Kpi(run.Kind == RunKind.WalkForward ? "Out of sample" : "Net PnL", Money(m.NetProfit), $"{m.ReturnPercent:0.##}% of capital", m.NetProfit >= 0 ? Ui.Up : Ui.Down));
            kpis.Children.Add(Kpi("Profit factor", m.ProfitFactor >= 999 ? "∞" : m.ProfitFactor.ToString("0.00", Es), $"{m.TotalTrades} trades", null));
            kpis.Children.Add(Kpi("Max drawdown", Money(-m.MaxDrawdown), $"{m.MaxDrawdownPercent:0.##}%", Ui.Down));
            kpis.Children.Add(Kpi("Sharpe", m.SharpeRatio.ToString("0.00", Es), $"{m.WinRate:0.#}% winners", null));
            if (run.WalkForward is { } wf) kpis.Children.Add(Kpi("WF efficiency", $"{wf.WalkForwardEfficiency * 100:0}%", $"{wf.ProfitableWindowsPercent:0}% profitable windows", wf.WalkForwardEfficiency >= 0.5 ? Ui.Up : Ui.Warn));
            _historyDetail.Children.Add(kpis);
        }

        if (run.Equity is { Count: > 1 } eq)
        {
            _historyChart.SetData(eq, run.Settings.InitialCapital, marks: run.WalkForward?.Windows.Select(w => (w.OutSampleFrom, $"V{w.Number}")).ToList());
            _historyDetail.Children.Add(Ui.Card(_historyChart, 6));
        }

        // parámetros
        var pars = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        foreach (var (k, v) in run.Parameters)
            pars.Children.Add(new Border { Child = new TextBlock { Text = $"{ParamName(k)} = {FormatValue(v)}", FontSize = 12 }, Classes = { "pill" }, Padding = new Thickness(10, 4) });
        _historyDetail.Children.Add(Ui.Card(Ui.V(10, Ui.H3(run.Kind == RunKind.Optimization ? "Best parameters" : "Parameters"), pars)));

        if (run.Optimization is { } opt)
        {
            var top = opt.Rows.Take(15).Select((r, i) => $"{i + 1,2}. {Money(r.Metrics.NetProfit),12}  PF {r.Metrics.ProfitFactor,5:0.00}  DD {Money(-r.Metrics.MaxDrawdown),11}  {string.Join(", ", r.Parameters.Select(kv => $"{ParamName(kv.Key)}={FormatValue(kv.Value)}"))}");
            _historyDetail.Children.Add(Ui.Card(Ui.V(8, Ui.H3($"Top 15 of {opt.Combinations:N0} combinations"), new TextBlock { Text = string.Join('\n', top), Classes = { "mono", "small" } })));
        }

        var s = run.Settings;
        _historyDetail.Children.Add(new TextBlock
        {
            Text = $"Capital {s.InitialCapital:N0} · commission {s.CommissionPercent:0.###}% + {s.CommissionPerUnit:0.##}/unit · slippage {s.SlippageTicks} tick(s) · limit fills: {(s.LimitFill == LimitFillMode.Touch ? "on touch" : "on trade-through")} · code {run.CodeHash}",
            Classes = { "faint", "small" },
            TextWrapping = TextWrapping.Wrap,
        });
    }
}

/// <summary>
/// Muestra código de solo lectura (versiones guardadas en el historial, estrategias de otra carpeta). Con un archivo,
/// arriba van copiar, mostrarlo en el Finder y abrirlo en Backtesting para editarlo.
/// </summary>
internal sealed class CodeViewerWindow : Window
{
    public CodeViewerWindow(string title, string code, string? path = null, Action? openInBacktesting = null)
    {
        Title = title;
        Width = 880;
        Height = 700;
        MinWidth = 520;
        MinHeight = 360;
        Background = Ui.Bg0;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var editor = new CodeEditor { Text = code, IsReadOnly = true };
        if (path == null && openInBacktesting == null)
        {
            Content = editor;
            return;
        }

        var done = new TextBlock { Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(done);
        actions.Children.Add(Ui.Button("Copy", MaterialIconKind.ContentCopy, () =>
        {
            if (GetTopLevel(this)?.Clipboard is { } cb) _ = cb.SetTextAsync(code);
            done.Text = "Copied";
        }, "ghost"));
        if (path != null) actions.Children.Add(Ui.Button("Show in Finder", MaterialIconKind.FolderSearchOutline, () => Shell.Reveal(path), "ghost"));
        if (openInBacktesting != null) actions.Children.Add(Ui.Button("Open in Backtesting", MaterialIconKind.FlaskOutline, openInBacktesting));
        var name = new TextBlock
        {
            Text = path != null ? $"{Path.GetFileName(path)} · {code.Split('\n').Length} lines · read-only" : "read-only",
            Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var head = new DockPanel { Margin = new Thickness(16, 10) };
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(name);
        var bar = new Border { Child = head, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(editor);
        Content = root;
    }
}
