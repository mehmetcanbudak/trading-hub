using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

/// <summary>
/// Backtest según el mercado: cómo le fue en rango, mixto o tendencia (alcista o bajista) y con volatilidad baja, normal
/// o alta, y volver a correrlo solo en las condiciones que elijas. La corrida nueva es un backtest de verdad con el filtro
/// de contexto: en cada vela la estrategia decide con el contexto medido hasta ese momento (no se sacan operaciones de la
/// corrida anterior), igual que en vivo.
/// </summary>
internal sealed partial class StrategiesView
{
    private readonly StackPanel _marketBox = new() { Spacing = 14, Margin = new Thickness(0, 0, 10, 10) };
    private readonly MarketConditionPicker _btConditions = new();
    private Ui.Segmented<string>? _resultTabs;
    private BacktestResult? _baseline;

    private static (RegimeFilter Filter, int Min, int Max) FilterOf(BacktestResult r)
    {
        if (r.Parameters == null) return (RegimeFilter.All, 0, 100);
        var probe = new ProbeStrategy();
        probe.SetParameters(r.Parameters);
        return (probe.RegimeFilter, probe.MinVolatilityRank, probe.MaxVolatilityRank);
    }

    /// <summary>Solo para leer los parámetros comunes de una corrida.</summary>
    private sealed class ProbeStrategy : Strategy;

    private static bool Filtered((RegimeFilter Filter, int Min, int Max) f) => f is not (RegimeFilter.All, 0, 100);

    private void RenderMarket(BacktestResult r)
    {
        _marketBox.Children.Clear();
        // el selector es siempre el mismo: se saca de la tarjeta anterior antes de ponerlo en la nueva
        if (_btConditions.Parent is Panel previous) previous.Children.Remove(_btConditions);
        var current = FilterOf(r);
        if (!Filtered(current)) _baseline = r;
        else if (_baseline != null && !SameRun(_baseline, r)) _baseline = null;

        _marketBox.Children.Add(Ui.H3("Performance by market condition"));
        _marketBox.Children.Add(new TextBlock
        {
            Text = "Each trade records the market context at entry, measured using closed bars: trend over the last 24 hours " +
                   "(range, mixed, uptrend or downtrend) and volatility relative to the last month.",
            Classes = { "faint", "small" },
            TextWrapping = TextWrapping.Wrap,
        });
        // con filtro, primero la comparación contra la corrida en todas las condiciones
        if (Filtered(current) && _baseline != null && !ReferenceEquals(_baseline, r))
            _marketBox.Children.Add(Compare(_baseline, r, current));
        _marketBox.Children.Add(Ui.Card(RegimeGrid(r.Trades), 10));

        // volver a correrlo solo en las condiciones elegidas
        _btConditions.Load(current.Filter, current.Min, current.Max);
        var run = Ui.Button("Rerun only in selected conditions", MaterialIconKind.Play, () =>
        {
            if (!_btConditions.IsValid)
            {
                _status("Choose at least one market condition and volatility category.");
                return;
            }

            var (filter, min, max) = _btConditions.Value;
            ApplyParameters(new Dictionary<string, object?>
            {
                [nameof(Strategy.RegimeFilter)] = filter, [nameof(Strategy.MinVolatilityRank)] = min, [nameof(Strategy.MaxVolatilityRank)] = max,
            });
            _resultTabs?.Choose("market");
        }, "primary");
        var reset = Ui.Button("Run in all conditions", MaterialIconKind.Restore, () =>
        {
            ApplyParameters(new Dictionary<string, object?>
            {
                [nameof(Strategy.RegimeFilter)] = RegimeFilter.All, [nameof(Strategy.MinVolatilityRank)] = 0, [nameof(Strategy.MaxVolatilityRank)] = 100,
            });
            _resultTabs?.Choose("market");
        }, "ghost");
        reset.IsVisible = Filtered(current);
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { run, reset } };
        var note = new TextBlock
        {
            Text = "This is a new backtest using the context filter, not a subset of the prior results: each bar uses only the context available at that " +
                   "time and opens entries only in selected conditions (exits always pass), matching live execution. Selecting conditions after inspecting " +
                   "these results fits the same data; validate with walk-forward or a separate period to establish out-of-sample evidence.",
            Classes = { "faint", "small" },
            TextWrapping = TextWrapping.Wrap,
        };
        var box = Ui.V(10, Ui.H3("Test selected conditions"), _btConditions, actions, note);
        _marketBox.Children.Add(Ui.Card(box, 14));
    }

    /// <summary>La misma estrategia, mercado, período, fechas y parámetros: solo cambia el filtro de contexto.</summary>
    private static bool SameRun(BacktestResult a, BacktestResult b)
    {
        if (a.Strategy != b.Strategy || a.Settings.FeedId != b.Settings.FeedId || a.Settings.Symbol != b.Settings.Symbol ||
            a.Settings.Period != b.Settings.Period || a.Settings.FromUtc != b.Settings.FromUtc || a.Settings.ToUtc != b.Settings.ToUtc) return false;
        var context = new[] { nameof(Strategy.RegimeFilter), nameof(Strategy.MinVolatilityRank), nameof(Strategy.MaxVolatilityRank) };
        foreach (var key in a.Parameters.Keys.Union(b.Parameters.Keys).Except(context))
            if (!Equals(a.Parameters.GetValueOrDefault(key)?.ToString(), b.Parameters.GetValueOrDefault(key)?.ToString())) return false;
        return true;
    }

    /// <summary>Todas las condiciones contra solo las elegidas, en una línea cada una.</summary>
    private Control Compare(BacktestResult all, BacktestResult only, (RegimeFilter Filter, int Min, int Max) f)
    {
        string Line(BacktestResult x) => $"{Money(x.Metrics.NetProfit)} on {x.Metrics.TotalTrades} op · PF {(x.Metrics.ProfitFactor >= 999 ? "∞" : x.Metrics.ProfitFactor.ToString("0.00", Es))} · " +
                                         $"drawdown {Money(-x.Metrics.MaxDrawdown)}";
        string what = Robustness.DescribeFilter(f.Filter, f.Min, f.Max);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 4, RowDefinitions = new RowDefinitions("Auto,Auto") };
        void Row(int row, string label, string text, IBrush color)
        {
            var l = new TextBlock { Text = label, Classes = { "muted" } };
            var t = new TextBlock { Text = text, Foreground = color, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, row);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, 1);
            grid.Children.Add(l);
            grid.Children.Add(t);
        }

        Row(0, "In all conditions", Line(all), all.Metrics.NetProfit >= 0 ? Ui.Up : Ui.Down);
        Row(1, $"With filter ({what})", Line(only), only.Metrics.NetProfit >= 0 ? Ui.Up : Ui.Down);
        return new Border { Child = grid, Background = Ui.Bg2, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8) };
    }

    /// <summary>La tabla: operaciones, neto, PF, ganadoras y promedio en cada condición y franja de volatilidad.</summary>
    private Control RegimeGrid(IReadOnlyList<ClosedTrade> trades)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        if (known.Count == 0)
            return new TextBlock { Text = trades.Count == 0 ? "No trades." : "This older run did not record per-trade context. Rerun the backtest.", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var rows = new List<(string Name, List<ClosedTrade> Trades, bool Head)>();
        bool Trend(ClosedTrade t) => t.EntryTrend >= MarketContextTracker.TrendLimit;
        rows.Add(("Range", known.Where(t => t.EntryTrend <= MarketContextTracker.RangeLimit).ToList(), false));
        rows.Add(("Mixed", known.Where(t => t.EntryTrend > MarketContextTracker.RangeLimit && !Trend(t)).ToList(), false));
        rows.Add(("Uptrend", known.Where(t => Trend(t) && t.EntryMarketDirection > 0).ToList(), false));
        rows.Add(("Downtrend", known.Where(t => Trend(t) && t.EntryMarketDirection < 0).ToList(), false));
        var undirected = known.Where(t => Trend(t) && t.EntryMarketDirection is null or 0).ToList();
        if (undirected.Count > 0) rows.Add(("Trend (direction not recorded)", undirected, false));
        if (known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8)
        {
            rows.Add(("Low volatility", known.Where(t => t.EntryVolatilityRank <= MarketContextTracker.LowVolatility).ToList(), true));
            rows.Add(("Normal volatility", known.Where(t => t.EntryVolatilityRank > MarketContextTracker.LowVolatility && t.EntryVolatilityRank < MarketContextTracker.HighVolatility).ToList(), false));
            rows.Add(("High volatility", known.Where(t => t.EntryVolatilityRank >= MarketContextTracker.HighVolatility).ToList(), false));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,0.8*,1.2*,0.8*,1*,1.1*"), RowSpacing = 2 };
        int r = 0;
        void Cell(string text, int col, IBrush? color = null, bool head = false, bool faint = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = head ? 11 : 12.5,
                Foreground = faint ? Ui.Text2 : color ?? (head ? Ui.Text2 : Ui.Text0),
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Margin = new Thickness(6, 5),
            };
            if (head) tb.Classes.Add("h3");
            Grid.SetRow(tb, r);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] titles = { "Condition at entry", "Ops", "Net", "PF", "Winners", "Average" };
        for (int i = 0; i < titles.Length; i++) Cell(titles[i], i, head: true);
        foreach (var (name, list, head) in rows)
        {
            if (head)
            {
                r++;
                grid.RowDefinitions.Add(new RowDefinition(new GridLength(8)));
            }

            r++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            bool few = list.Count < Math.Max(5, known.Count / 20);
            decimal net = list.Sum(t => t.NetPnl);
            decimal win = list.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), loss = -list.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
            string pf = list.Count == 0 ? "—" : loss > 0 ? (win / loss).ToString("0.00", Es) : win > 0 ? "∞" : "—";
            Cell(few && list.Count > 0 ? $"{name} (small sample)" : name, 0, faint: list.Count == 0 || few);
            Cell(list.Count.ToString(CultureInfo.InvariantCulture), 1, faint: list.Count == 0 || few);
            Cell(list.Count == 0 ? "—" : Money(net), 2, list.Count == 0 || few ? null : net >= 0 ? Ui.Up : Ui.Down, faint: list.Count == 0 || few);
            Cell(pf, 3, faint: list.Count == 0 || few);
            Cell(list.Count == 0 ? "—" : $"{100.0 * list.Count(t => t.NetPnl > 0) / list.Count:0}%", 4, faint: list.Count == 0 || few);
            Cell(list.Count == 0 ? "—" : Money(net / list.Count), 5, faint: list.Count == 0 || few);
        }

        int unknown = trades.Count - known.Count;
        var v = Ui.V(6, grid);
        if (unknown > 0)
            v.Children.Add(new TextBlock { Text = $"{unknown} trade(s) without context (early in the run, before sufficient history was available).", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap });
        return v;
    }
}
