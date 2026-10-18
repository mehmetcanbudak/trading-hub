using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

/// <summary>
/// Mercado: cómo está cada mercado (rango, mixto o tendencia alcista o bajista, y la volatilidad comparada con la del último
/// mes), medido igual que los filtros de contexto de las estrategias, y qué estrategias operan con eso: ahora y en cada
/// contexto. Son los mercados de las estrategias que corren y los que se siguen.
/// </summary>
internal sealed partial class LiveView
{
    private readonly WrapPanel _marketKpis = new() { ItemSpacing = 10, LineSpacing = 10 };
    private readonly WrapPanel _marketCards = new() { ItemSpacing = 14, LineSpacing = 14 };
    private readonly ContentControl _marketMatrix = new();
    private readonly TextBlock _marketStamp = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private Dictionary<string, MarketCondition> _conditions = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _conditionsAt;
    private bool _conditionsBusy, _conditionsLoaded;
    private string _marketKey = "";

    private Control BuildMarketPage()
    {
        var intro = Ui.Faint("Market conditions use the same measurements as strategy context filters: trend over the last 24 hours " +
                             "(range, mixed, uptrend or downtrend) and volatility relative to the last month. Each market lists the strategies " +
                             "trading those conditions; the final table shows each strategy's context settings.");
        intro.TextWrapping = TextWrapping.Wrap;
        var root = Ui.V(16, intro, _marketKpis, _marketCards, _marketMatrix, _marketStamp);
        root.Margin = new Thickness(24, 18, 24, 28);
        return new ScrollViewer { Content = root };
    }

    private void RefreshMarket(bool force)
    {
        // se recalcula cada 30 s (o al entrar); entre medio se redibuja si cambió el estado de alguna estrategia
        if (force || DateTime.UtcNow - _conditionsAt > TimeSpan.FromSeconds(30)) _ = LoadConditionsAsync();
        RenderMarket(force);
    }

    private sealed record MarketRef(string Key, string Feed, string Symbol);

    /// <summary>Clave del subyacente: el mini y el micro del mismo futuro son el mismo precio (NQ y MNQ van juntos).</summary>
    private static string MarketKey(string feed, string symbol) => MarketInfo.IsFutures(feed) ? $"{feed}:{MarketInfo.MiniOf(symbol)}" : $"{feed}:{symbol}";

    private List<LiveEntry> RunningEntries() => _svc.Live.Entries().Where(e => e is { Running: true, Runner: not null }).ToList();

    /// <summary>Los mercados de las estrategias que corren y los que se siguen (sin los CFD importados, que no se actualizan solos).</summary>
    private List<MarketRef> MarketList(List<LiveEntry> running)
    {
        var list = new List<MarketRef>();
        void Add(string feed, string symbol)
        {
            string key = MarketKey(feed, symbol);
            int i = list.FindIndex(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            // del mismo subyacente vale el que está abierto en la app (tiene los datos de hoy)
            bool open = _svc.Hub.Items.Any(d => d.Instrument.FeedId == feed && d.Instrument.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            if (i < 0) list.Add(new MarketRef(key, feed, symbol));
            else if (open && !_svc.Hub.Items.Any(d => d.Instrument.FeedId == list[i].Feed && d.Instrument.Symbol.Equals(list[i].Symbol, StringComparison.OrdinalIgnoreCase)))
                list[i] = new MarketRef(key, feed, symbol);
        }

        foreach (var e in running) Add(e.Runner!.Data.Instrument.FeedId, e.Runner.Data.Instrument.Symbol);

        return list;
    }

    private async Task LoadConditionsAsync()
    {
        if (_conditionsBusy) return;
        _conditionsBusy = true;
        try
        {
            var next = new Dictionary<string, MarketCondition>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in MarketList(RunningEntries()))
            {
                try
                {
                    if (await _svc.Markets.ConditionAsync(m.Feed, m.Symbol) is { } c) next[m.Key] = c;
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex);
                }
            }

            _conditions = next;
            _conditionsAt = DateTime.UtcNow;
            _conditionsLoaded = true;
        }
        finally
        {
            _conditionsBusy = false;
        }

        if (_tab == "market") RenderMarket(force: false);
    }

    private static bool HasFilter(Strategy s) => s.RegimeFilter != RegimeFilter.All || s.MinVolatilityRank > 0 || s.MaxVolatilityRank < 100;

    /// <summary>En qué contextos abre operaciones (texto corto).</summary>
    private static string FilterText(Strategy s)
    {
        string text = Robustness.DescribeFilter(s.RegimeFilter, s.MinVolatilityRank, s.MaxVolatilityRank);
        return text.Length == 0 ? "any context" : text;
    }

    /// <summary>Si opera con el contexto de ahora (el que ve la propia estrategia, el que decide sus órdenes).</summary>
    private static (string Text, IBrush Color, bool Trading) ContextStatus(LiveEntry e)
    {
        var r = e.Runner!;
        var s = r.StrategyInstance;
        if (r.DailyLimitActive) return ("Stopped for today (risk limit)", Ui.Warn, false);
        if (e.Paused) return ("Paused", Ui.Warn, false);
        if (!HasFilter(s)) return ("Trading: no context filter", Ui.Up, true);
        if (s.Regime == MarketRegime.Unknown) return ("Trading: market context not available yet", Ui.Text1, true);
        string now = MarketContextTracker.Label(s.Regime, s.MarketDirection).ToLowerInvariant() + (s.VolatilityRank is { } vr ? $", volatility percentile {vr:0}" : "");
        return s.ContextAllowsEntries ? ($"Trading: matching context ({now})", Ui.Up, true) : ($"Paused by context: currently {now}", Ui.Warn, false);
    }

    /// <summary>Violeta suave para el rango (la paleta es neutra con ámbar: se distingue del verde, el rojo y el gris).</summary>
    private static IBrush Calm => new SolidColorBrush(Color.Parse(AppTheme.IsLight ? "#7C5CD6" : "#A78BFA"));

    private static IBrush RegimeBrush(MarketRegime regime, int? direction) => regime switch
    {
        MarketRegime.Range => Calm,
        MarketRegime.Trend => direction < 0 ? Ui.Down : Ui.Up,
        MarketRegime.Mixed => Ui.Text2,
        _ => Ui.Bg3,
    };

    private static MaterialIconKind RegimeIcon(MarketRegime regime, int? direction) => regime switch
    {
        MarketRegime.Range => MaterialIconKind.ArrowLeftRight,
        MarketRegime.Trend => direction < 0 ? MaterialIconKind.TrendingDown : MaterialIconKind.TrendingUp,
        MarketRegime.Mixed => MaterialIconKind.Waves,
        _ => MaterialIconKind.HelpCircleOutline,
    };

    private static string FeedLabel(string feed) => feed switch
    {
        "rithmic" => "CME futures · Rithmic",
        "tradovate" => "CME futures · Tradovate",
        "binance-futures" => "Binance perpetual",
        "binance-spot" => "Binance spot",
        "bybit-linear" => "Bybit perpetual",
        "sim" => "simulator",
        _ => feed,
    };

    private void RenderMarket(bool force)
    {
        var running = RunningEntries();
        var markets = MarketList(running);
        string key = string.Join("|", markets.Select(m => m.Key)) + "#" + _conditionsAt.Ticks + "#" + string.Join("|", running.Select(e =>
        {
            var s = e.Runner!.StrategyInstance;
            return $"{e.Key}:{e.StateLabel}:{s.ContextAllowsEntries}:{s.Regime}:{s.MarketDirection}:{s.VolatilityRank:0}";
        }));
        if (!force && key == _marketKey) return;
        _marketKey = key;

        // resumen
        _marketKpis.Children.Clear();
        int trading = running.Count(e => ContextStatus(e).Trading);
        int byContext = running.Count(e => !e.Paused && !e.Runner!.DailyLimitActive && HasFilter(e.Runner.StrategyInstance) && !e.Runner.StrategyInstance.ContextAllowsEntries);
        int filtered = running.Count(e => HasFilter(e.Runner!.StrategyInstance));
        _marketKpis.Children.Add(Kpi("Trading now", trading.ToString(Inv), $"of {running.Count} live", trading > 0 ? Ui.Up : null,
            tip: "Running strategies allowed to open trades in current conditions"));
        _marketKpis.Children.Add(Kpi("Paused by context", byContext.ToString(Inv), "waiting for matching market conditions", byContext > 0 ? Ui.Warn : null,
            tip: "Their context filters do not match current conditions: no new entries (existing positions remain managed)"));
        _marketKpis.Children.Add(Kpi("With context filter", filtered.ToString(Inv), "trade only in selected market conditions", null,
            tip: "Strategies that only open trades in selected conditions, such as ranging markets or low volatility"));

        _marketCards.Children.Clear();
        if (markets.Count == 0)
        {
            _marketCards.Children.Add(Ui.Card(Ui.Faint("No live strategies yet: their markets and conditions appear here when they run."), 16).Also(b => b.Width = 740));
        }

        foreach (var m in markets)
        {
            _conditions.TryGetValue(m.Key, out var c);
            var mine = running.Where(e => MarketKey(e.Runner!.Data.Instrument.FeedId, e.Runner.Data.Instrument.Symbol).Equals(m.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            _marketCards.Children.Add(MarketCard(m, c, mine));
        }

        _marketMatrix.Content = running.Count > 0 ? ContextMatrix(running) : null;
        _marketStamp.Text = _conditionsLoaded
            ? $"Updated {_conditionsAt.ToLocalTime():HH:mm:ss} · recalculated every 30 seconds using the last month's hourly closes (saved history and today's live data)."
            : "Calculating from the last month's saved history…";
    }

    private Control MarketCard(MarketRef m, MarketCondition? c, List<LiveEntry> strategies)
    {
        var (_, symbol) = MarketInfo.Split(m.Key);
        var title = Ui.H(8,
            new TextBlock { Text = symbol, FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = FeedLabel(m.Feed) + (m.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) ? "" : $" · data from {m.Symbol}"), Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center });
        var head = new DockPanel();
        if (c != null)
        {
            var price = Ui.H(8,
                new TextBlock { Text = c.LastPrice.ToString("#,##0.##", Es), FontSize = 15, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("Menlo, SF Mono"), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = c.Change is { } ch ? $"{ch.ToString("+0.00;-0.00;0.00", Es)} % over 24 h" : "",
                    Foreground = c.Change > 0 ? Ui.Up : c.Change < 0 ? Ui.Down : Ui.Text2,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            DockPanel.SetDock(price, Dock.Right);
            head.Children.Add(price);
        }

        head.Children.Add(title);
        var body = Ui.V(14, head);
        if (c == null)
        {
            body.Children.Add(Ui.Faint(_conditionsLoaded ? "No saved history for this market yet. Open its chart or download history to populate conditions." : "Calculating…"));
        }
        else
        {
            body.Children.Add(Ui.H(28, RegimeBlock(c), VolatilityBlock(c)).Also(p => p.VerticalAlignment = VerticalAlignment.Top));
            body.Children.Add(Timeline(c));
            if (DateTime.UtcNow - c.DataUntilUtc > TimeSpan.FromMinutes(20))
                body.Children.Add(Ui.Faint($"Data ends on {c.DataUntilUtc.ToLocalTime():dd/MM HH:mm} (market closed or recording stopped): conditions reflect that time."));
        }

        body.Children.Add(StrategiesOf(strategies));
        // dos por fila en una pantalla ancha
        return Ui.Card(body, 16).Also(b => b.Width = 740);
    }

    private static Control RegimeBlock(MarketCondition c)
    {
        var color = RegimeBrush(c.Regime, c.Direction);
        var label = Ui.H(8, Ui.Icon(RegimeIcon(c.Regime, c.Direction), 22, color),
            new TextBlock { Text = c.RegimeLabel, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
        string since = c.RegimeHours >= 47 ? "more than 48 h ago" : c.RegimeHours <= 1 ? "since the last hour" : $"ago {c.RegimeHours} h";
        var detail = new TextBlock
        {
            Text = c.Trend is { } t ? $"Trend efficiency {t.ToString("0.00", Es)} · unchanged for {since}" : "Not enough data yet",
            Classes = { "faint", "small" },
        };
        var gauge = Gauge(c.Trend is { } tr ? Math.Clamp(tr, 0, 1) : (double?)null, new[] { MarketContextTracker.RangeLimit, MarketContextTracker.TrendLimit }, color, "range", "trend");
        var block = Ui.V(6, Ui.H3("Condition"), label, detail, gauge);
        ToolTip.SetTip(block, "Hourly-close efficiency over the last 24 hours: net change / total path (0 = back and forth, 1 = straight trend; random ≈ 0.2). " +
                              "Range up to 0.15, trend from 0.35; direction follows net movement.");
        block.MinWidth = 260;
        return block;
    }

    private static Control VolatilityBlock(MarketCondition c)
    {
        IBrush color = c.VolatilityRank is not { } r ? Ui.Text2 : r >= MarketContextTracker.HighVolatility ? Ui.Warn : r <= MarketContextTracker.LowVolatility ? Calm : Ui.Text0;
        var label = new TextBlock { Text = $"Volatility {c.VolatilityLabel}", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = color };
        var detail = new TextBlock
        {
            Text = c.VolatilityRank is { } rank
                ? $"Percentile {rank:0} over the last month · {c.Volatility?.ToString("0.00", Es)} % per hour"
                : "At least one day of history is required",
            Classes = { "faint", "small" },
        };
        var gauge = Gauge(c.VolatilityRank / 100, new[] { MarketContextTracker.LowVolatility / 100, MarketContextTracker.HighVolatility / 100 }, color, "low", "high");
        var block = Ui.V(6, Ui.H3("Volatility"), label, detail, gauge);
        ToolTip.SetTip(block, "Standard deviation of hourly returns over the last 24 hours versus the last month: low up to percentile 33, high from 67.");
        block.MinWidth = 260;
        return block;
    }

    /// <summary>Barra de 0 a 1 con las marcas de los umbrales y un punto en el valor.</summary>
    private static Control Gauge(double? value, double[] marks, IBrush color, string leftLabel, string rightLabel)
    {
        const double width = 240, height = 6;
        var canvas = new Canvas { Width = width, Height = 14 };
        canvas.Children.Add(new Rectangle { Width = width, Height = height, Fill = Ui.Bg3, RadiusX = 3, RadiusY = 3 }.Also(r => Canvas.SetTop(r, 4)));
        foreach (var mk in marks)
            canvas.Children.Add(new Rectangle { Width = 1.5, Height = 12, Fill = Ui.Text2 }.Also(r =>
            {
                Canvas.SetLeft(r, mk * width);
                Canvas.SetTop(r, 1);
            }));
        if (value is { } v)
            canvas.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = color, Stroke = Ui.Bg1, StrokeThickness = 2 }.Also(e =>
            {
                Canvas.SetLeft(e, Math.Clamp(v, 0, 1) * width - 6);
                Canvas.SetTop(e, 1);
            }));
        var labels = new DockPanel { Width = width };
        var right = new TextBlock { Text = rightLabel, FontSize = 10, Foreground = Ui.Text2 };
        DockPanel.SetDock(right, Dock.Right);
        labels.Children.Add(right);
        labels.Children.Add(new TextBlock { Text = leftLabel, FontSize = 10, Foreground = Ui.Text2 });
        return Ui.V(2, canvas, labels);
    }

    /// <summary>Las últimas 48 horas, una barrita por hora con el color de su condición.</summary>
    private static Control Timeline(MarketCondition c)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var h in c.Recent)
        {
            var bar = new Border
            {
                Width = 9,
                Height = 18,
                CornerRadius = new CornerRadius(2),
                Background = RegimeBrush(h.Regime, h.Direction),
                Opacity = h.Regime == MarketRegime.Unknown ? 0.5 : 0.85,
            };
            ToolTip.SetTip(bar, $"{h.Hour.ToLocalTime():ddd dd/MM HH:mm} · {MarketContextTracker.Label(h.Regime, h.Direction).ToLowerInvariant()}" +
                                (h.VolatilityRank is { } vr ? $" · volatility percentile {vr:0}" : ""));
            strip.Children.Add(bar);
        }

        var legend = Ui.H(12,
            Legend(Calm, "range"), Legend(Ui.Text2, "mixed"), Legend(Ui.Up, "uptrend"), Legend(Ui.Down, "downtrend"));
        var ends = new DockPanel();
        var now = new TextBlock { Text = "now", FontSize = 10, Foreground = Ui.Text2 };
        DockPanel.SetDock(now, Dock.Right);
        ends.Children.Add(now);
        ends.Children.Add(new TextBlock { Text = c.Recent.Count > 0 ? $"ago {c.Recent.Count} h" : "", FontSize = 10, Foreground = Ui.Text2 });
        ends.Width = c.Recent.Count * 11;
        ends.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.V(4, Ui.H3("Last 48 hours"), new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, ends, legend);
    }

    private static Control Legend(IBrush color, string text) =>
        Ui.H(4, new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = color, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, FontSize = 10.5, Foreground = Ui.Text2, VerticalAlignment = VerticalAlignment.Center });

    private Control StrategiesOf(List<LiveEntry> strategies)
    {
        var list = Ui.V(6, Ui.H3("Strategies in this market"));
        if (strategies.Count == 0)
        {
            list.Children.Add(Ui.Faint("None running now."));
            return list;
        }

        foreach (var e in strategies.OrderBy(x => x.Info.Strategy))
        {
            var (text, color, _) = ContextStatus(e);
            var s = e.Runner!.StrategyInstance;
            var dot = new Ellipse { Width = 8, Height = 8, Fill = color, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = e.Info.Strategy, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var where = new TextBlock { Text = $"{e.SourceLabel} · {e.Info.Symbol} {e.Info.Period}", Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
            var filter = new TextBlock { Text = $"Trades in: {FilterText(s)}", FontSize = 12, Foreground = Ui.Text1, VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { Text = text, FontSize = 12, Foreground = color, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var row = new WrapPanel { ItemSpacing = 14, LineSpacing = 4 };
            row.Children.Add(Ui.H(7, dot, name, where));
            row.Children.Add(filter);
            row.Children.Add(status);
            ToolTip.SetTip(row, "Exits are always allowed: a context-paused strategy continues managing an open position until it closes.");
            list.Children.Add(row);
        }

        return list;
    }

    /// <summary>Qué estrategia opera en cada condición de mercado (con la de ahora de su mercado resaltada).</summary>
    private Control ContextMatrix(List<LiveEntry> running)
    {
        var columns = new (string Title, MarketRegime Regime, int? Direction)[]
        {
            ("Range", MarketRegime.Range, 0), ("Mixed", MarketRegime.Mixed, 0), ("Uptrend", MarketRegime.Trend, 1), ("Downtrend", MarketRegime.Trend, -1),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2.4*,1*,1*,1*,1.2*,1.2*,1.6*"), RowSpacing = 2 };
        int row = 0;
        void Cell(Control c, int col)
        {
            c.Margin = new Thickness(6, 6);
            c.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            grid.Children.Add(c);
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(new TextBlock { Text = "Strategy", Classes = { "h3" } }, 0);
        for (int i = 0; i < columns.Length; i++) Cell(new TextBlock { Text = columns[i].Title, Classes = { "h3" }, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap }, i + 1);
        Cell(new TextBlock { Text = "Volatility", Classes = { "h3" } }, 5);
        Cell(new TextBlock { Text = "Now", Classes = { "h3" } }, 6);
        foreach (var e in running.OrderBy(x => x.Info.Symbol).ThenBy(x => x.Info.Strategy))
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var s = e.Runner!.StrategyInstance;
            Cell(Ui.V(1, new TextBlock { Text = e.Info.Strategy, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = $"{e.Info.Symbol} {e.Info.Period} · {e.SourceLabel}", Classes = { "faint", "small" } }), 0);
            for (int i = 0; i < columns.Length; i++)
            {
                var (_, regime, direction) = columns[i];
                bool ok = MarketContextTracker.RegimeAllows(s.RegimeFilter, regime, direction);
                bool current = s.Regime == regime && (regime != MarketRegime.Trend || Math.Sign(s.MarketDirection ?? 0) == direction);
                var mark = new Border
                {
                    Child = new TextBlock { Text = ok ? "✓" : "—", Foreground = ok ? Ui.Up : Ui.Text2, FontWeight = ok ? FontWeight.Bold : FontWeight.Normal, HorizontalAlignment = HorizontalAlignment.Center },
                    Padding = new Thickness(0, 3),
                    CornerRadius = new CornerRadius(6),
                    BorderBrush = current ? RegimeBrush(regime, direction) : Brushes.Transparent,
                    BorderThickness = new Thickness(current ? 1.5 : 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                if (current) ToolTip.SetTip(mark, "Current condition in this market");
                Cell(mark, i + 1);
            }

            string vol = s.MinVolatilityRank > 0 || s.MaxVolatilityRank < 100
                ? Robustness.DescribeFilter(RegimeFilter.All, s.MinVolatilityRank, s.MaxVolatilityRank)
                : "any";
            Cell(new TextBlock { Text = vol, FontSize = 12, Foreground = Ui.Text1, TextWrapping = TextWrapping.Wrap }, 5);
            var (text, color, trading) = ContextStatus(e);
            Cell(new TextBlock { Text = trading ? "Trades" : e.Paused || e.Runner.DailyLimitActive ? "Stopped" : "Paused", Foreground = color, FontWeight = FontWeight.SemiBold }.Also(t => ToolTip.SetTip(t, text)), 6);
        }

        return Ui.V(10, Ui.H3("Each strategy's trading context"),
            Ui.Faint("✓ = entries allowed in that condition, subject to the volatility filter. The outline marks the market's current condition."),
            Ui.Card(grid, 10));
    }
}
