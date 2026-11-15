using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using SkiaSharp;
using TradingHub.App.Services.Workbench;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;

namespace TradingHub.App.Views.Workbench;

/// <summary>
/// Derivatives dashboard for the selected coin across the public venues Trading Hub reads (Binance USD-M and Bybit
/// linear): funding (per period and normalized to 8 hours), open interest and its change, perpetual premium over the
/// index, long/short account ratio and observed liquidations, as a table plus small charts. Missing data is a dash or a
/// hatched gap, never a zero.
/// </summary>
internal sealed class DerivativesPanel : WorkbenchPanel
{
    private static readonly TimeSpan Reload = TimeSpan.FromMinutes(1);
    private static readonly string[] Columns =
    {
        "Venue", "Funding", "Funding 8h", "Next funding", "Open interest", "OI (USD)", "OI 1h", "OI 24h", "Premium", "Long/short", "Long liqs 24h", "Short liqs 24h",
    };
    private readonly Grid _table = new() { Name = "DerivativesTable" };
    private readonly DerivativesChartsCanvas _charts = new() { Name = "DerivativesCharts" };
    private readonly TextBlock _status = new() { Name = "DerivativesStatus", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, DerivativesVenueInputs> _inputs = new();
    private IDisposable? _watch;
    private string? _watchSymbol;
    private DateTime _loadedAt = DateTime.MinValue, _lastTable;
    private int _generation;
    private bool _loading;

    public DerivativesPanel(WorkbenchContext context) : base(context)
    {
        var s = context.Settings;
        var range = new Ui.Segmented<int>(new (int, string, string?)[] { (24, "24 hours", null), (72, "3 days", null), (168, "7 days", null) }) { Name = "DerivativesRange" };
        range.Select(s.DerivativesHours is 24 or 72 or 168 ? s.DerivativesHours : 24);
        range.SelectionChanged += h => { s.DerivativesHours = h; context.Save(); _loadedAt = DateTime.MinValue; Load(); };
        var reload = Ui.Button("Reload", MaterialIconKind.Refresh, () => { _loadedAt = DateTime.MinValue; Load(); });
        reload.Name = "DerivativesReload";
        for (int c = 0; c < Columns.Length; c++) _table.ColumnDefinitions.Add(new ColumnDefinition(c == 0 ? new GridLength(130) : GridLength.Auto));
        var top = Ui.V(8, Ui.H(12, range, reload),
            Note("Public Binance USD-M and Bybit linear data for the coin's USDT perpetual. Funding is shown per settlement and normalized to 8 hours; premium = (mark − index) / index; long/short is the venues' account ratio; liquidations come from the live streams only while observed (Binance publishes a one-per-second sample)."),
            new ScrollViewer { Content = _table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        top.Margin = new Thickness(0, 0, 0, 10);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(_charts);
        Content = root;
        RenderTable(Array.Empty<DerivativesVenueRow>());
    }

    public override string Key => "derivatives";
    public override string Title => "Derivatives";
    public override MaterialIconKind Icon => MaterialIconKind.ChartTimelineVariant;
    internal IReadOnlyDictionary<string, DerivativesVenueInputs> Inputs => _inputs;

    protected override void OnSelectionChanged()
    {
        _generation++;
        _inputs.Clear();
        _loading = false;
        _loadedAt = DateTime.MinValue;
        Release();
        if (IsActive) OnActivated();
        Refresh();
    }

    protected override void OnActivated()
    {
        if (Selection is { IsCryptoUsd: true } sel && Context.Derivatives() is { } service && _watchSymbol != sel.DerivativesSymbol)
        {
            Release();
            try
            {
                _watch = service.WatchLiquidations(sel.DerivativesSymbol);
                _watchSymbol = sel.DerivativesSymbol;
            }
            catch (ObjectDisposedException) { }
        }
        Load();
    }

    protected override void OnDeactivated() => Release();

    protected override void OnDispose() => Release();

    private void Release()
    {
        _watch?.Dispose();
        _watch = null;
        _watchSymbol = null;
    }

    public override void Tick(DateTime now)
    {
        if (now - _loadedAt >= Reload) Load();
        if (now - _lastTable >= TimeSpan.FromSeconds(2)) Refresh();
    }

    private void Load()
    {
        if (_loading || !IsActive || Selection is not { } sel) return;
        _loadedAt = Context.Clock();
        if (!sel.IsCryptoUsd || Context.Derivatives() is not { } service)
        {
            _status.Text = !sel.IsCryptoUsd ? "The derivatives dashboard needs a USD-quoted crypto coin (its USDT perpetual is read on each venue)." : "Derivatives data is unavailable in this session.";
            Refresh();
            return;
        }
        _loading = true;
        int generation = _generation;
        string symbol = sel.DerivativesSymbol;
        var now = _loadedAt;
        var from = now.AddHours(-Math.Clamp(Context.Settings.DerivativesHours, 1, 24 * 30));
        _status.Text = $"Loading {symbol} from Binance and Bybit…";
        _ = Task.Run(async () =>
        {
            var results = new List<DerivativesVenueInputs>();
            foreach (var venue in DerivativesData.Venues)
            {
                var errors = new List<string>();
                async Task<T?> Try<T>(string what, Func<Task<T>> load) where T : class
                {
                    try { return await load().ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not ObjectDisposedException)
                    {
                        errors.Add($"{WorkbenchFormat.Venue(venue)} {what}: {ex.Message}");
                        return null;
                    }
                    catch (ObjectDisposedException) { return null; }
                }
                var funding = await Try("funding", () => service.LoadFundingAsync(venue, symbol, from.AddHours(-8), now, CancellationToken.None));
                var oi = await Try("open interest", () => service.LoadOpenInterestAsync(venue, symbol, from.AddHours(-1), now, CancellationToken.None));
                var stats = await Try("premium", () => service.LoadMarketStatsAsync(venue, symbol, CancellationToken.None));
                var ls = await Try("long/short", () => service.LoadLongShortAsync(venue, symbol, from, now, CancellationToken.None));
                results.Add(new DerivativesVenueInputs(venue, funding, oi, stats, ls, null, errors));
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _generation) return;
                _loading = false;
                foreach (var r in results) _inputs[r.Venue] = r;
                Refresh();
            });
        });
    }

    /// <summary>Rebuilds rows (with the latest observed liquidations) and the charts from the loaded inputs.</summary>
    private void Refresh()
    {
        _lastTable = Context.Clock();
        var now = _lastTable;
        if (Selection is not { } sel || _inputs.Count == 0)
        {
            RenderTable(Array.Empty<DerivativesVenueRow>());
            _charts.Set(Array.Empty<DerivativesChart>());
            return;
        }
        var hours = Math.Clamp(Context.Settings.DerivativesHours, 1, 24 * 30);
        var from = now.AddHours(-hours);
        var liquidations = Context.Derivatives()?.Liquidations(sel.DerivativesSymbol, from < now.AddHours(-24) ? from : now.AddHours(-24), now);
        var rows = new List<DerivativesVenueRow>();
        var charts = new List<DerivativesChart>();
        var step = TimeSpan.FromHours(hours) / 96;
        foreach (var venue in DerivativesData.Venues)
        {
            if (!_inputs.TryGetValue(venue, out var input)) continue;
            input = input with { Liquidations = liquidations };
            var row = DerivativesDashboard.BuildRow(input, now);
            rows.Add(row);
            string name = WorkbenchFormat.Venue(venue);
            double? interval = row.FundingIntervalHours;
            var funding = (input.Funding?.Samples ?? Array.Empty<FundingSample>())
                .Select(f => new SeriesPoint(f.Time, (double)(interval is { } h ? DerivativesDashboard.NormalizeTo8Hours(f.RatePercent, h) : f.RatePercent))).ToArray();
            charts.Add(new($"{name} · funding (8h %)", DerivativesDashboard.Resample(funding, from, now, step, TimeSpan.FromHours(Math.Max(9, (interval ?? 8) + 1))), true, v => v.ToString("0.####", CultureInfo.InvariantCulture) + "%"));
            var oi = (input.OpenInterest?.Samples ?? Array.Empty<OpenInterestSample>()).Select(o => new SeriesPoint(o.Time, (double)o.Quantity)).ToArray();
            charts.Add(new($"{name} · open interest", DerivativesDashboard.Resample(oi, from, now, step, TimeSpan.FromHours(2)), false, v => WorkbenchFormat.Compact(v)));
            var ls = (input.LongShort?.Samples ?? Array.Empty<LongShortSample>()).Where(x => x.Ratio != null).Select(x => new SeriesPoint(x.Time, (double)x.Ratio!.Value)).ToArray();
            charts.Add(new($"{name} · long/short ratio", DerivativesDashboard.Resample(ls, from, now, step, TimeSpan.FromHours(2)), false, v => v.ToString("0.00", CultureInfo.InvariantCulture)));
            var liqStep = TimeSpan.FromHours(Math.Max(1, hours / 48));
            var longs = liquidations == null ? Array.Empty<double?>() : DerivativesDashboard.LiquidationBuckets(liquidations.Events, liquidations.Windows, venue, from, now, liqStep, true);
            var shorts = liquidations == null ? Array.Empty<double?>() : DerivativesDashboard.LiquidationBuckets(liquidations.Events, liquidations.Windows, venue, from, now, liqStep, false);
            charts.Add(new($"{name} · liquidations (longs − / shorts +)", longs.Zip(shorts, (l, s) => l is { } a && s is { } b ? b - a : (double?)null).ToArray(), true, v => WorkbenchFormat.Compact(v)));
        }
        RenderTable(rows);
        _charts.Set(charts);
        var notes = rows.SelectMany(r => r.Notes).Distinct().ToArray();
        if (!_loading) _status.Text = notes.Length == 0 ? $"{sel.DerivativesSymbol} · updated {_loadedAt.ToLocalTime():HH:mm:ss}" : string.Join(" · ", notes);
    }

    private void RenderTable(IReadOnlyList<DerivativesVenueRow> rows)
    {
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < Columns.Length; c++) Cell(Columns[c], 0, c, header: true);
        int r = 1;
        foreach (var row in rows)
        {
            _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell(WorkbenchFormat.Venue(row.Venue), r, 0, bold: true);
            Cell(WorkbenchFormat.Percent(row.FundingPercent, 4) + (row.FundingIntervalHours is { } h ? $" / {h:0.##}h" : ""), r, 1, color: Sign(row.FundingPercent));
            Cell(WorkbenchFormat.Percent(row.Funding8hPercent, 4), r, 2, color: Sign(row.Funding8hPercent));
            Cell(row.NextFunding is { } next ? next.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : WorkbenchFormat.Missing, r, 3);
            Cell(WorkbenchFormat.Compact(row.OpenInterest), r, 4);
            Cell(WorkbenchFormat.Compact(row.OpenInterestUsd), r, 5);
            Cell(WorkbenchFormat.Percent(row.OpenInterestChange1hPercent), r, 6, color: Sign(row.OpenInterestChange1hPercent));
            Cell(WorkbenchFormat.Percent(row.OpenInterestChange24hPercent), r, 7, color: Sign(row.OpenInterestChange24hPercent));
            Cell(WorkbenchFormat.Percent(row.PremiumPercent, 4), r, 8, color: Sign(row.PremiumPercent));
            Cell(row.LongShortRatio is { } ls ? ls.ToString("0.00", CultureInfo.InvariantCulture) : WorkbenchFormat.Missing, r, 9);
            string coverage = row.Liquidations24hCoverage == LiquidationCoverage.Partial ? " (partial)" : "";
            Cell(row.LongLiquidations24h is { } l ? WorkbenchFormat.Compact(l) + coverage : "not observed", r, 10, color: row.LongLiquidations24h != null ? Ui.Down : Ui.Text2);
            Cell(row.ShortLiquidations24h is { } s ? WorkbenchFormat.Compact(s) + coverage : "not observed", r, 11, color: row.ShortLiquidations24h != null ? Ui.Up : Ui.Text2);
            r++;
        }
        if (rows.Count == 0)
        {
            _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var empty = new TextBlock { Text = _loading ? "Loading…" : "No derivatives data yet", Classes = { "muted" }, Margin = new Thickness(4, 6) };
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, Columns.Length);
            _table.Children.Add(empty);
        }
    }

    private static IBrush? Sign(decimal? value) => value is > 0 ? Ui.Up : value is < 0 ? Ui.Down : null;

    private void Cell(string text, int row, int column, bool header = false, bool bold = false, IBrush? color = null)
    {
        var t = new TextBlock
        {
            Text = text, Margin = new Thickness(column == 0 ? 4 : 14, 4, 4, 4), HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            FontWeight = header || bold ? FontWeight.SemiBold : FontWeight.Normal,
        };
        if (header) t.Classes.Add("muted");
        if (color != null) t.Foreground = color;
        Grid.SetRow(t, row);
        Grid.SetColumn(t, column);
        _table.Children.Add(t);
    }
}

internal sealed record DerivativesChart(string Title, IReadOnlyList<double?> Values, bool ZeroLine, Func<double, string> Format);

/// <summary>Grid of small gap-aware charts, four per venue.</summary>
internal sealed class DerivativesChartsCanvas : WorkbenchCanvas
{
    private IReadOnlyList<DerivativesChart> _charts = Array.Empty<DerivativesChart>();

    public IReadOnlyList<DerivativesChart> Charts => _charts;

    public void Set(IReadOnlyList<DerivativesChart> charts)
    {
        _charts = charts;
        InvalidateVisual();
    }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        LoadColors();
        canvas.Clear(Bg);
        if (_charts.Count == 0)
        {
            Label(canvas, "Charts appear once the venues answer", 10, 22, Text);
            return;
        }
        int columns = 4, rows = (int)Math.Ceiling(_charts.Count / (double)columns);
        float cw = (width - 8) / columns, ch = (height - 8) / Math.Max(1, rows);
        for (int i = 0; i < _charts.Count; i++)
        {
            var c = _charts[i];
            var rect = new SKRect(4 + (i % columns) * cw + 3, 4 + (i / columns) * ch + 3, 4 + (i % columns + 1) * cw - 3, 4 + (i / columns + 1) * ch - 3);
            Sparkline(canvas, rect, c.Title, c.Values, i % columns == 3 ? Warn : Accent, c.Format, c.ZeroLine);
        }
    }
}
