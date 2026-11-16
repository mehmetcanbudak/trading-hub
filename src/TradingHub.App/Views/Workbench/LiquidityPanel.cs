using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Material.Icons;
using SkiaSharp;
using TradingHub.App.Services.Workbench;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Indicators.Native;

namespace TradingHub.App.Views.Workbench;

/// <summary>
/// Liquidity/liquidation map: a local estimate of where recently opened positions would be liquidated (open-interest
/// increases split by measured delta over a fixed leverage mix), the opt-in third-party Hyperliquid BTC levels, and a
/// leverage reference grid from the current price. All three are estimates and are labeled as such.
/// </summary>
internal sealed class LiquidityPanel : WorkbenchPanel
{
    private static readonly TimeSpan Reload = TimeSpan.FromMinutes(1);
    private readonly LiquidityCanvas _canvas = new() { Name = "LiquidityCanvas" };
    private readonly TextBlock _status = new() { Name = "LiquidityStatus", Classes = { "faint", "small" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _thirdParty = new() { Name = "LiquidityThirdPartyState", Classes = { "small" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private List<Kline> _candles = new();
    private IReadOnlyList<EstimatedLiquidationMap.Entry> _entries = Array.Empty<EstimatedLiquidationMap.Entry>();
    private string? _estimateNote;
    private DateTime _loadedAt = DateTime.MinValue;
    private int _generation;
    private bool _loading, _refreshingLevels;

    public LiquidityPanel(WorkbenchContext context) : base(context)
    {
        var s = context.Settings;
        var range = Number("LiquidityRange", s.LiquidityRangePercent, 2, 50, 1, "0", v => { s.LiquidityRangePercent = v; context.Save(); Render(); });
        var mm = Number("LiquidityMaintenance", s.LiquidityMaintenancePercent, 0, 5, 0.1m, "0.##", v => { s.LiquidityMaintenancePercent = v; context.Save(); Render(); });
        var reload = Ui.Button("Reload", MaterialIconKind.Refresh, () => { _loadedAt = DateTime.MinValue; Load(); });
        reload.Name = "LiquidityReload";
        var top = Ui.V(8, Ui.H(12, Ui.Field("Range (± %)", range), Ui.Field("Maintenance margin (%)", mm), reload), _thirdParty,
            Note("Estimates, not exchange data. The local model spreads open-interest increases (Binance + Bybit) over 5×–100× leverage tiers by each bar's measured buy/sell volume and removes levels the price already traded through. The leverage grid shows where positions opened at the current price would be liquidated, ignoring maintenance margin."));
        top.Margin = new Thickness(0, 0, 0, 8);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(_canvas);
        Content = root;
    }

    public override string Key => "liquidity";
    public override string Title => "Liquidation map";
    public override MaterialIconKind Icon => MaterialIconKind.Water;
    internal LiquidityCanvas Canvas => _canvas;

    protected override void OnSelectionChanged()
    {
        _generation++;
        _candles = new();
        _entries = Array.Empty<EstimatedLiquidationMap.Entry>();
        _estimateNote = null;
        _loadedAt = DateTime.MinValue;
        _loading = false;
        if (IsActive) Load();
        Render();
    }

    protected override void OnActivated() => Load();

    public override void Tick(DateTime now)
    {
        if (now - _loadedAt >= Reload) Load();
        RefreshLevels(now);
        Render();
    }

    /// <summary>Loads 15-minute candles (7 days) and both venues' open interest, then recomputes the local estimate.</summary>
    private void Load()
    {
        if (_loading || !IsActive || Selection is not { } sel) return;
        _loadedAt = Context.Clock();
        if (!sel.IsCryptoUsd)
        {
            _estimateNote = "Liquidation maps need a USD-quoted crypto perpetual.";
            Render();
            return;
        }
        if (sel.Data.Feed is not IKlineHistory source)
        {
            _estimateNote = "This connection has no candle history for the estimate.";
            Render();
            return;
        }
        var derivatives = Context.Derivatives();
        int generation = _generation;
        _loading = true;
        var now = _loadedAt;
        var from = now.AddDays(-7);
        string symbol = sel.DerivativesSymbol;
        _ = Task.Run(async () =>
        {
            List<Kline> candles = new();
            var oi = new List<IReadOnlyList<(DateTime, decimal)>>();
            var notes = new List<string>();
            try
            {
                candles = await KlineHistory.LoadAsync(source, sel.Instrument, from, now, 15, 0, null, CancellationToken.None, warning: notes.Add).ConfigureAwait(false);
            }
            catch (Exception ex) { notes.Add($"Candles could not be loaded: {ex.Message}"); }
            if (derivatives == null) notes.Add("Derivatives data is unavailable in this session.");
            else
                foreach (var venue in DerivativesData.Venues)
                {
                    try
                    {
                        var r = await derivatives.LoadOpenInterestAsync(venue, symbol, from, now, CancellationToken.None).ConfigureAwait(false);
                        oi.Add(r.Samples.Select(x => (x.Time, x.Quantity)).ToArray());
                        if (r.Message != null) notes.Add($"Open interest from {WorkbenchFormat.Venue(venue)}: {r.Message}");
                    }
                    catch (Exception ex) { notes.Add($"Open interest from {WorkbenchFormat.Venue(venue)}: {ex.Message}"); }
                }
            var entries = LiquidityMapModel.Entries(candles, TimeSpan.FromMinutes(15), oi.Count == DerivativesData.Venues.Count ? oi : Array.Empty<IReadOnlyList<(DateTime, decimal)>>(), now);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _generation) return;
                _loading = false;
                _candles = candles;
                _entries = entries;
                _estimateNote = notes.Count == 0 ? null : string.Join(" · ", notes.Distinct());
                Render();
            });
        });
    }

    /// <summary>Asks the opt-in service for a snapshot at most once a minute (the service also rate-limits itself).</summary>
    private void RefreshLevels(DateTime now)
    {
        if (_refreshingLevels || Selection is not { } sel || Context.Levels() is not { } service) return;
        if (!LiquidationLevelsService.Supports(sel.Instrument.BaseAsset, sel.Instrument.Symbol)) return;
        if (now - (service.Latest.Snapshot?.FetchedUtc ?? DateTime.MinValue) < LiquidationLevelsService.MinimumRefresh) return;
        _refreshingLevels = true;
        _ = Task.Run(async () =>
        {
            try { await service.RefreshAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
            Dispatcher.UIThread.Post(() => { _refreshingLevels = false; Render(); });
        });
    }

    private void Render()
    {
        if (Selection is not { } sel)
        {
            _canvas.Set(null, "Choose an instrument");
            return;
        }
        var s = Context.Settings;
        decimal price = sel.Data.LastPrice > 0 ? sel.Data.LastPrice : _candles.Count > 0 ? _candles[^1].Close : 0;
        EstimatedLiquidationMap.Map? estimate = null;
        if (price > 0 && _entries.Count > 0 && _entries.Any(e => e.OpenedInterest != null))
            estimate = EstimatedLiquidationMap.Estimate(_entries, price, s.LiquidityRangePercent, 160, s.LiquidityMaintenancePercent);
        string? estimateNote = _estimateNote ?? (estimate == null && !_loading && _entries.Count > 0 ? "Open interest is unavailable for these candles; the local estimate needs both venues." : null);
        if (_loading && estimate == null) estimateNote = "Loading candles and open interest…";

        IReadOnlyList<LiquidationLevelBin>? bins = null;
        LiquidationLevelsSnapshot? snapshot = null;
        string thirdParty;
        var service = Context.Levels();
        if (service == null) thirdParty = "Third-party levels: off. Enable Settings → Third-party data → Third-party liquidation levels to add the published Hyperliquid BTC estimate.";
        else if (!LiquidationLevelsService.Supports(sel.Instrument.BaseAsset, sel.Instrument.Symbol)) thirdParty = "Third-party levels: the service publishes BTC only.";
        else
        {
            var latest = service.Latest;
            snapshot = latest.Snapshot;
            if (snapshot == null) thirdParty = latest.Message is { } m ? $"Third-party levels: {m}" : "Third-party levels: loading…";
            else if (Context.Clock() - snapshot.AsOfUtc > LiquidationLevelsService.MaximumSnapshotAge)
            {
                thirdParty = $"Third-party levels: the snapshot is older than three hours ({Ui.Ago(snapshot.AsOfUtc)}) and is not drawn.";
                snapshot = null;
            }
            else
            {
                bins = LiquidityMapModel.Standing(snapshot, _candles, 0);
                thirdParty = $"Third-party estimate · {snapshot.Attribution} · {snapshot.Positions} positions · as of {Ui.Ago(snapshot.AsOfUtc)}";
            }
        }
        _thirdParty.Text = thirdParty;
        _thirdParty.Foreground = service == null ? Ui.Text2 : Ui.Text1;
        var view = new LiquidityMapView(price, estimate, estimateNote, bins, snapshot, thirdParty, LiquidityMapModel.Bands(price));
        _canvas.Set(view, price <= 0 ? "Waiting for a price" : null, s.LiquidityRangePercent);
        // Each piece is one complete display template joined by the " · " separator; the counts are the only holes.
        if (estimate is { } map)
        {
            string modelled = $"Local estimate: {map.Used} bars modelled, {map.Skipped} skipped (no open interest or measured delta).";
            _status.Text = estimateNote is { } n ? string.Join(" · ", modelled, n) : modelled;
        }
        else _status.Text = estimateNote ?? "";
    }
}

/// <summary>Shared price axis: local estimate bars (left), third-party bins (right), leverage grid lines across both.</summary>
internal sealed class LiquidityCanvas : WorkbenchCanvas
{
    private LiquidityMapView? _view;
    private string? _empty;
    private decimal _range = 12;

    public LiquidityMapView? View => _view;

    public void Set(LiquidityMapView? view, string? empty, decimal range = 12)
    {
        _view = view;
        _empty = empty;
        _range = range;
        InvalidateVisual();
    }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        LoadColors();
        canvas.Clear(Bg);
        if (_view is not { Price: > 0 } view)
        {
            Label(canvas, _empty ?? "Choose an instrument", 12, 24, Text);
            return;
        }
        decimal lo = view.Price * (1 - _range / 100), hi = view.Price * (1 + _range / 100);
        var plot = new SKRect(70, 26, width - 10, height - 10);
        float mid = plot.MidX;
        float Y(decimal p) => plot.Bottom - (float)((p - lo) / (hi - lo)) * plot.Height;
        using var grid = new SKPaint { Color = Grid, StrokeWidth = 1 };
        canvas.DrawLine(mid, plot.Top, mid, plot.Bottom, grid);
        Label(canvas, "Local estimate from open interest", plot.Left + 4, 16, Text, 10.5f, true);
        Label(canvas, "Third-party estimate (opt-in)", mid + 6, 16, Text, 10.5f, true);
        for (int k = 0; k <= 6; k++)
        {
            decimal p = lo + (hi - lo) * k / 6;
            canvas.DrawLine(plot.Left, Y(p), plot.Right, Y(p), grid);
            Label(canvas, WorkbenchFormat.Price(p), plot.Left - 6, Y(p) + 4, Text, 10, right: true);
        }

        using var bar = new SKPaint { IsAntialias = false };
        if (view.Estimate is { } map)
        {
            decimal max = map.Rows.Length > 0 ? map.Rows.Max() : 0;
            for (int k = 0; k < map.Rows.Length && max > 0; k++)
            {
                if (map.Rows[k] <= 0) continue;
                decimal p0 = map.Low + k * map.Step, p1 = p0 + map.Step;
                if (p1 < lo || p0 > hi) continue;
                float w = (float)(map.Rows[k] / max) * (mid - plot.Left - 8);
                bar.Color = (p0 + map.Step / 2 > view.Price ? Up : Down).WithAlpha(170);
                float y0 = Y(p1), y1 = Y(p0);
                canvas.DrawRect(mid - 4 - w, y0, w, Math.Max(1, y1 - y0), bar);
            }
        }
        else Label(canvas, view.EstimateNote ?? "No local estimate", plot.Left + 6, plot.MidY, Text, 10.5f);

        if (view.ThirdParty is { Count: > 0 } bins && view.ThirdPartySnapshot is { } snap)
        {
            decimal max = bins.Max(b => Math.Max(b.LongNotional, b.ShortNotional));
            foreach (var b in bins)
            {
                if (b.Price + snap.Step < lo || b.Price > hi || max <= 0) continue;
                float y0 = Y(b.Price + snap.Step), y1 = Y(b.Price);
                if (b.LongNotional > 0)
                {
                    bar.Color = Down.WithAlpha(170);
                    canvas.DrawRect(mid + 4, y0, (float)(b.LongNotional / max) * (plot.Right - mid - 8), Math.Max(1, y1 - y0), bar);
                }
                if (b.ShortNotional > 0)
                {
                    bar.Color = Up.WithAlpha(170);
                    canvas.DrawRect(mid + 4, y0, (float)(b.ShortNotional / max) * (plot.Right - mid - 8), Math.Max(1, y1 - y0), bar);
                }
            }
        }
        else Label(canvas, "Not available", mid + 10, plot.MidY, Text, 10.5f);

        using var dashed = new SKPaint { Color = Warn.WithAlpha(150), StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0), IsAntialias = true };
        foreach (var band in view.Bands)
            foreach (var p in new[] { band.LongPrice, band.ShortPrice })
            {
                if (p < lo || p > hi) continue;
                canvas.DrawLine(plot.Left, Y(p), plot.Right, Y(p), dashed);
                Label(canvas, $"{band.Leverage}×", plot.Right - 4, Y(p) - 3, Warn, 9.5f, right: true);
            }
        using var now = new SKPaint { Color = Strong, StrokeWidth = 1.5f };
        canvas.DrawLine(plot.Left, Y(view.Price), plot.Right, Y(view.Price), now);
        Label(canvas, WorkbenchFormat.Price(view.Price), plot.Left - 6, Y(view.Price) + 4, Strong, 10, true, right: true);
    }
}
