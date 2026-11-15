using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;
using SkiaSharp;
using TradingHub.App.Windows;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Views.Workbench;

/// <summary>
/// Full-panel time × price heat map of resting L2 size from the opt-in depth recorder (the same frames and colors as
/// the chart's liquidity heat map), with the selected instrument's trades on top. Recording is never switched on here:
/// with "Record received depth" off only the current book is shown, as a column at the right edge.
/// </summary>
internal sealed class HeatmapPanel : WorkbenchPanel
{
    private readonly HeatmapCanvas _canvas = new() { Name = "HeatmapCanvas" };
    private readonly Border _banner;
    private readonly TextBlock _bannerText;
    private readonly TextBlock _status = new() { Name = "HeatmapStatus", Classes = { "faint", "small" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private DateTime _lastDraw;

    public HeatmapPanel(WorkbenchContext context) : base(context)
    {
        var s = context.Settings;
        _banner = Banner("HeatmapRecordingOff", out _bannerText);
        var minutes = new ComboBox { Name = "HeatmapMinutes", ItemsSource = new[] { 5, 15, 30, 60, 240 }, SelectedItem = s.HeatmapMinutes is 5 or 15 or 30 or 60 or 240 ? s.HeatmapMinutes : 15, MinWidth = 90 };
        minutes.SelectionChanged += (_, _) => { if (minutes.SelectedItem is int m) { s.HeatmapMinutes = m; context.Save(); Draw(); } };
        var grouping = new ComboBox { Name = "HeatmapGrouping", ItemsSource = new[] { 1, 2, 5, 10, 25, 50, 100 }, SelectedItem = s.HeatmapGrouping is 1 or 2 or 5 or 10 or 25 or 50 or 100 ? s.HeatmapGrouping : 1, MinWidth = 90 };
        grouping.SelectionChanged += (_, _) => { if (grouping.SelectedItem is int g) { s.HeatmapGrouping = g; context.Save(); Draw(); } };
        var trades = new CheckBox { Name = "HeatmapTrades", Content = "Show trades", IsChecked = s.HeatmapTrades };
        trades.IsCheckedChanged += (_, _) => { s.HeatmapTrades = trades.IsChecked == true; context.Save(); Draw(); };
        var settings = Ui.Button("Depth recording settings…", MaterialIconKind.CogOutline, OpenSettings);
        settings.Name = "HeatmapOpenSettings";
        var bar = Ui.H(12, Ui.Field("Minutes", minutes), Ui.Field("Group (ticks)", grouping), trades, settings);
        var top = Ui.V(8, bar, _banner, Note("Resting L2 size (peak per display pixel), not executed volume. Shaded intervals are missing or stale depth; amber underlines mark partial or truncated books."));
        top.Margin = new Thickness(0, 0, 0, 8);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(_canvas);
        Content = root;
    }

    public override string Key => "heatmap";
    public override string Title => "Depth heat map";
    public override MaterialIconKind Icon => MaterialIconKind.Fire;
    internal HeatmapCanvas Canvas => _canvas;

    private void OpenSettings()
    {
        if (TopLevel.GetTopLevel(this) is MainWindow main) main.Navigate("settings");
        else if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow owner }) owner.Navigate("settings");
    }

    protected override void OnSelectionChanged() => Draw();

    protected override void OnActivated() => Draw();

    public override void Tick(DateTime now)
    {
        if (now - _lastDraw >= TimeSpan.FromMilliseconds(500)) Draw();
    }

    private void Draw()
    {
        _lastDraw = Context.Clock();
        if (Selection is not { } sel)
        {
            _canvas.Set(null, default, default, 1, false, "Choose an instrument");
            return;
        }
        var status = sel.Data.DepthHistory.Status;
        bool recording = status.RecordingState == DepthRecordingState.Recording;
        _banner.IsVisible = !recording;
        _bannerText.Text = status.RecordingState switch
        {
            DepthRecordingState.Disabled => "Depth recording is off, so there is no depth history: only the current book is shown at the right edge. Turn on Settings → History and depth → Record received depth to build the heat map (an explicit opt-in that stores received depth on this computer).",
            DepthRecordingState.Error => status.Message is { Length: > 0 } failure
                ? $"Depth recording failed: {failure}. The heat map shows what was recorded before the failure."
                : "Depth recording failed without a reported reason. The heat map shows what was recorded before the failure.",
            _ => "Depth recording is stopped; the heat map shows depth recorded earlier.",
        };
        var to = _lastDraw;
        var from = to.AddMinutes(-Math.Clamp(Context.Settings.HeatmapMinutes, 1, 1440));
        _canvas.Set(sel, from, to, Math.Max(1, Context.Settings.HeatmapGrouping), Context.Settings.HeatmapTrades, null);
        // Every piece is one complete display template; holes carry only data (symbol, count, provider message).
        var pieces = new List<string>
        {
            sel.Instrument.Symbol,
            status.RecordingState switch
            {
                DepthRecordingState.Recording => "Depth recording on",
                DepthRecordingState.Disabled => "Depth recording off",
                DepthRecordingState.Error => "Depth recording failed",
                _ => "Depth recording stopped",
            },
            status.LastReceivedAt is { } t ? $"Last book: {Ui.Ago(t)}" : "No book received yet",
            $"Dropped records: {status.DroppedRecords}",
        };
        if (status.Message is { Length: > 0 } m) pieces.Add(m);
        _status.Text = string.Join(" · ", pieces);
    }
}

/// <summary>Draws depth frames over a time axis ending now, the live book at the right edge and trades as dots.</summary>
internal sealed class HeatmapCanvas : WorkbenchCanvas
{
    private readonly DepthHeatmapProjection _projection = new();
    private readonly SKPaint _paint = new() { IsAntialias = false };
    private WorkbenchSelection? _selection;
    private DateTime _from, _to;
    private int _grouping = 1;
    private bool _trades;
    private string? _empty;

    /// <summary>Spans drawn by the last frame (used by scenes to confirm the heat map rendered).</summary>
    public int LastSpans { get; private set; }

    public void Set(WorkbenchSelection? selection, DateTime from, DateTime to, int grouping, bool trades, string? empty)
    {
        _selection = selection;
        _from = from;
        _to = to;
        _grouping = grouping;
        _trades = trades;
        _empty = empty;
        InvalidateVisual();
    }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        LoadColors();
        canvas.Clear(Bg);
        if (_selection is not { } sel || _to <= _from)
        {
            Label(canvas, _empty ?? "Choose an instrument", 12, 24, Text);
            return;
        }
        var instrument = sel.Instrument;
        decimal tick = instrument.TickSize;
        if (tick <= 0) return;
        var rect = new SKRect(8, 8, width - 92, height - 22);
        var bookRect = new SKRect(rect.Right + 4, rect.Top, width - 8, rect.Bottom);
        var query = sel.Data.DepthHistory.Query(_from, _to);
        var current = sel.Data.Depth;
        var store = sel.Data.Trades;
        int first = store.LowerBound(_from);

        // price range: recent trades and the current book's inner levels, padded
        decimal lo = decimal.MaxValue, hi = decimal.MinValue;
        for (int i = first; i < store.Count; i++)
        {
            decimal p = store.GetPrice(i);
            if (p < lo) lo = p;
            if (p > hi) hi = p;
        }
        foreach (var level in current.Bids.Take(50).Concat(current.Asks.Take(50)))
        {
            decimal p = instrument.FromTicks(level.PriceTicks);
            if (p < lo) lo = p;
            if (p > hi) hi = p;
        }
        if (lo > hi)
        {
            Label(canvas, "No trades or depth for this instrument yet", 12, 24, Text);
            return;
        }
        decimal pad = Math.Max((hi - lo) * 0.15m, tick * 20);
        lo -= pad;
        hi += pad;
        float X(DateTime t) => rect.Left + (float)((t - _from).TotalSeconds / (_to - _from).TotalSeconds) * rect.Width;
        float Y(decimal price) => rect.Bottom - (float)((price - lo) / (hi - lo)) * rect.Height;

        var frame = LiquidityHeatmapRenderer.BuildFrame(query, _grouping, TimeSpan.FromSeconds(10), lo / tick - _grouping, hi / tick + _grouping,
            Math.Clamp((int)(rect.Width * Math.Min(rect.Height, 500)), 1000, 250_000));
        _projection.Project(frame, rect, X, ticks => Y(ticks * tick), _grouping);
        LastSpans = _projection.Spans.Count;
        canvas.Save();
        canvas.ClipRect(rect);
        LiquidityHeatmapRenderer.Paint(canvas, _paint, frame, _projection.Spans, rect, X, 230);
        canvas.Restore();

        // live book column
        decimal peak = 0;
        foreach (var level in current.Bids.Concat(current.Asks)) peak = Math.Max(peak, level.Size);
        if (peak > 0)
        {
            foreach (var (levels, color) in new[] { (current.Bids, Up), (current.Asks, Down) })
                foreach (var level in levels)
                {
                    decimal p = instrument.FromTicks(level.PriceTicks);
                    if (p < lo || p > hi) continue;
                    float w = (float)(level.Size / peak) * bookRect.Width;
                    _paint.Color = color.WithAlpha(170);
                    canvas.DrawRect(bookRect.Left, Y(p) - 1, Math.Max(1, w), 2, _paint);
                }
        }
        Label(canvas, "Book now", bookRect.Left, bookRect.Bottom + 14, Text, 10);

        // trades overlay
        if (_trades && store.Count > first)
        {
            int start = Math.Max(first, store.Count - 5000);
            decimal maxSize = 0;
            for (int i = start; i < store.Count; i++) maxSize = Math.Max(maxSize, store.GetVolume(i));
            using var dot = new SKPaint { IsAntialias = true };
            for (int i = start; i < store.Count; i++)
            {
                decimal size = store.GetVolume(i);
                float r = 1.5f + 7 * (float)Math.Sqrt((double)(size / Math.Max(maxSize, 1e-12m)));
                dot.Color = (store.GetSide(i) == TradeSide.Sell ? Down : Up).WithAlpha(150);
                canvas.DrawCircle(X(store.GetTime(i)), Y(store.GetPrice(i)), r, dot);
            }
        }

        using var axis = new SKPaint { Color = Grid, StrokeWidth = 1 };
        canvas.DrawRect(rect, new SKPaint { Color = Grid, Style = SKPaintStyle.Stroke });
        for (int k = 0; k <= 4; k++)
        {
            decimal p = lo + (hi - lo) * k / 4;
            Label(canvas, WorkbenchFormat.Price(p), width - 6, Y(p) + 4, Text, 10, right: true);
        }
        Label(canvas, _from.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), rect.Left, height - 6, Text, 10);
        Label(canvas, _to.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), rect.Right, height - 6, Text, 10, right: true);
        if (frame.Cells.Count == 0 && frame.Gaps.Count > 0)
            Label(canvas, "No recorded depth in this window", rect.Left + 10, rect.Top + 18, Warn, 11, true);
    }
}
