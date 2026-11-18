using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using SkiaSharp;
using TradingHub.App.Services;
using TradingHub.App.Services.Workbench;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Market;
using TradingHub.Rendering;

namespace TradingHub.App.Views.Workbench;

/// <summary>
/// Aggregated trades tape: live prints of the selected instrument from every venue of its connection (an aggregate
/// connection lists each constituent), merged per venue/side/millisecond, filtered and highlighted by notional, with
/// per-venue buy/sell volume and observed liquidation bars over time. Venue coverage is shown honestly: disconnected or
/// partial venues are flagged, and liquidations count only while their stream was observed.
/// </summary>
internal sealed class TapePanel : WorkbenchPanel
{
    private const int MaxPrintsPerTick = 50_000;
    private static readonly TimeSpan AlertSpacing = TimeSpan.FromSeconds(3);
    // Liquidations are re-read over this trailing window and deduplicated, so a venue that publishes late (Binance sends
    // at most one event per second, stamped with its order time) is not lost behind the other venue's newer events.
    private static readonly TimeSpan LateLiquidations = TimeSpan.FromMinutes(2);
    // A correction that inserted prints before the read cursor rebuilds the tape, at most this often.
    private static readonly TimeSpan RebuildSpacing = TimeSpan.FromSeconds(5);
    private readonly TradeTape _tape;
    private readonly TapeListCanvas _list;
    private readonly TapeChartCanvas _chart;
    private readonly TextBlock _coverage = new() { Name = "TapeCoverage", TextWrapping = TextWrapping.Wrap, Classes = { "small" } };
    private readonly TextBlock _skippedNote = new() { Name = "TapeSkipped", TextWrapping = TextWrapping.Wrap, Classes = { "small" }, Foreground = Ui.Warn, IsVisible = false };
    private readonly TextBlock _liquidationState = new() { Name = "TapeLiquidationCoverage", TextWrapping = TextWrapping.Wrap, Classes = { "faint", "small" } };
    private readonly WrapPanel _venueFilters = new() { Name = "TapeVenues", Orientation = Orientation.Horizontal };
    private readonly Dictionary<string, ToggleButton> _venueButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<LiquidationEvent> _seenLiquidations = new();
    private IDisposable? _liquidationWatch;
    private string? _liquidationSymbol;
    private long _liquidationGeneration = -1;
    private DateTime _liquidationFrom;
    private long _cursorTicks;
    private int _seenAtCursor;
    private TradeStoreView _view;
    private DateTime _lastWindows, _lastAlert = DateTime.MinValue, _alertFrom = DateTime.MaxValue, _rebuiltAt;
    private int _skipped;

    public TapePanel(WorkbenchContext context) : base(context)
    {
        var s = context.Settings;
        _tape = new TradeTape(Settings());
        _list = new TapeListCanvas(_tape, s.TapeHiddenVenues) { Name = "TapeList" };
        _chart = new TapeChartCanvas(_tape) { Name = "TapeChart" };
        var merge = new CheckBox { Name = "TapeMerge", Content = "Merge same-millisecond prints", IsChecked = s.TapeMergeSameMillisecond };
        merge.IsCheckedChanged += (_, _) => { s.TapeMergeSameMillisecond = merge.IsChecked == true; Changed(); };
        var bucket = new ComboBox { Name = "TapeBucket", ItemsSource = new[] { 5, 10, 30, 60 }, SelectedItem = s.TapeBucketSeconds is 5 or 10 or 30 or 60 ? s.TapeBucketSeconds : 10, MinWidth = 80 };
        bucket.SelectionChanged += (_, _) =>
        {
            if (bucket.SelectedItem is not int seconds) return;
            s.TapeBucketSeconds = seconds;
            Changed();
            Rebuild();
        };
        var soundLarge = new CheckBox { Name = "TapeSoundLarge", Content = "Alert on large trades", IsChecked = s.TapeSoundLarge };
        soundLarge.IsCheckedChanged += (_, _) => { s.TapeSoundLarge = soundLarge.IsChecked == true; context.Save(); };
        var soundLiq = new CheckBox { Name = "TapeSoundLiquidations", Content = "Alert on liquidations", IsChecked = s.TapeSoundLiquidations };
        soundLiq.IsCheckedChanged += (_, _) => { s.TapeSoundLiquidations = soundLiq.IsChecked == true; context.Save(); };
        ToolTip.SetTip(soundLarge, "Raises a local alert (sound follows Settings → Sound alerts; never sent to external destinations).");
        ToolTip.SetTip(soundLiq, "Raises a local alert (sound follows Settings → Sound alerts; never sent to external destinations).");

        var thresholds = Ui.H(12,
            Ui.Field("Hide below (notional)", Number("TapeMinNotional", s.TapeMinNotional, 0, 1_000_000_000, 1000, "0", v => { s.TapeMinNotional = v; Changed(); })),
            Ui.Field("Large from", Number("TapeLargeNotional", s.TapeLargeNotional, 0, 1_000_000_000, 10_000, "0", v => { s.TapeLargeNotional = v; Changed(); })),
            Ui.Field("Huge from", Number("TapeHugeNotional", s.TapeHugeNotional, 0, 10_000_000_000, 100_000, "0", v => { s.TapeHugeNotional = v; Changed(); })),
            Ui.Field("Liquidations from", Number("TapeMinLiquidation", s.TapeMinLiquidationNotional, 0, 1_000_000_000, 1000, "0", v => { s.TapeMinLiquidationNotional = v; Changed(); })),
            Ui.Field("Bar seconds", bucket));
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Children = { merge, soundLarge, soundLiq } };
        foreach (var c in options.Children) c.Margin = new Thickness(0, 0, 16, 0);

        var listBorder = new Border { Child = _list, MinWidth = 360 };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("420,*"), RowDefinitions = new RowDefinitions("*") };
        grid.Children.Add(listBorder);
        var right = new DockPanel();
        var coverageTitle = new TextBlock { Text = "Coverage:", Classes = { "small" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(coverageTitle, Dock.Left);
        var coverageRow = new DockPanel { Children = { coverageTitle, _coverage } };
        var legend = Ui.V(4, coverageRow, _skippedNote, _liquidationState, _venueFilters);
        DockPanel.SetDock(legend, Dock.Bottom);
        right.Children.Add(legend);
        right.Children.Add(_chart);
        right.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var top = Ui.V(8, thresholds, options);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(top);
        root.Children.Add(grid);
        Content = root;
    }

    public override string Key => "tape";
    public override string Title => "Aggregated trades";
    public override MaterialIconKind Icon => MaterialIconKind.FormatListBulleted;
    internal TradeTape Tape => _tape;

    /// <summary>Whether the panel currently holds the liquidation streams (scenes check that hidden panels release them).</summary>
    internal bool WatchingLiquidations => _liquidationWatch != null;

    private TapeSettings Settings()
    {
        var s = Context.Settings;
        return new TapeSettings(s.TapeMinNotional, s.TapeLargeNotional, s.TapeHugeNotional, s.TapeMinLiquidationNotional, s.TapeMergeSameMillisecond,
            Selection?.Instrument.PointValue is { } pv and > 0 ? pv : 1, 400, Math.Clamp(s.TapeBucketSeconds, 1, 3600), 180);
    }

    private void Changed()
    {
        Context.Save();
        _tape.Configure(Settings());
        Redraw();
    }

    protected override void OnSelectionChanged() => Restart();

    protected override void OnActivated() => Restart();

    protected override void OnDeactivated() => ReleaseLiquidations();

    protected override void OnDispose() => ReleaseLiquidations();

    private void ReleaseLiquidations()
    {
        _liquidationWatch?.Dispose();
        _liquidationWatch = null;
        _liquidationSymbol = null;
    }

    /// <summary>Starts observing again (selection or visibility changed): resubscribes liquidations and rebuilds the tape.</summary>
    private void Restart()
    {
        _venueButtons.Clear();
        _venueFilters.Children.Clear();
        ReleaseLiquidations();
        if (IsActive && Selection is { IsCryptoUsd: true } sel && Context.Derivatives() is { } derivatives)
        {
            try
            {
                _liquidationWatch = derivatives.WatchLiquidations(sel.DerivativesSymbol);
                _liquidationSymbol = sel.DerivativesSymbol;
            }
            catch (ObjectDisposedException) { }
        }
        Rebuild();
    }

    /// <summary>
    /// Backfills the recent window from the shared trade store and the liquidations observed in it, and shows the coverage
    /// of the current selection at once. Alerts only consider prints and liquidations from now on.
    /// </summary>
    private void Rebuild()
    {
        _tape.Clear();
        _tape.Configure(Settings());
        _skipped = 0;
        _liquidationGeneration = -1;
        _seenLiquidations.Clear();
        _lastWindows = DateTime.MinValue;
        if (!IsActive || Selection is not { } sel)
        {
            _coverage.Text = "";
            _liquidationState.Text = "";
            _skippedNote.IsVisible = false;
            Redraw();
            return;
        }
        var now = Context.Clock();
        _rebuiltAt = now;
        _alertFrom = now;
        var store = sel.Data.Trades;
        var window = TimeSpan.FromSeconds(_tape.Settings.BucketSeconds * _tape.Settings.MaxBuckets);
        var from = now - (window < TimeSpan.FromMinutes(30) ? window : TimeSpan.FromMinutes(30));
        var historyFrom = sel.Data.HistoryFrom == DateTime.MaxValue ? (store.Count > 0 ? store.FirstTime : now) : sel.Data.HistoryFrom;
        _tape.Observe(historyFrom > from ? historyFrom : from);
        _view = TradeStoreView.Capture(store);
        _cursorTicks = from.Ticks;
        _seenAtCursor = 0;
        _liquidationFrom = from;
        Pull(now);
        UpdateCoverage(now);
        Redraw();
    }

    public override void Tick(DateTime now)
    {
        if (Selection is not { } sel) return;
        var store = sel.Data.Trades;
        // A replaced store, or a correction that may have merged prints before the read cursor (an out-of-order arrival).
        if (store.Identity != _view.Identity || (store.CorrectionRevision != _view.CorrectionRevision && now - _rebuiltAt >= RebuildSpacing))
        {
            Rebuild();
            return;
        }
        var shown = Pull(now);
        UpdateCoverage(now);
        if (shown) Redraw();
        else _chart.InvalidateVisual();
    }

    /// <summary>Reads prints after the cursor and new liquidations; returns whether anything changed.</summary>
    private bool Pull(DateTime now)
    {
        if (Selection is not { } sel) return false;
        var store = sel.Data.Trades;
        var instrument = sel.Instrument;
        int i = store.LowerBound(new DateTime(_cursorTicks, DateTimeKind.Utc));
        int skip = _seenAtCursor;
        while (skip > 0 && i < store.Count && store.GetTimeTicks(i) == _cursorTicks) { i++; skip--; }
        int available = store.Count - i;
        if (available > MaxPrintsPerTick)
        {
            _skipped += available - MaxPrintsPerTick;
            i = store.Count - MaxPrintsPerTick;
        }
        var prints = new List<TapeTrade>(Math.Max(0, store.Count - i));
        for (; i < store.Count; i++)
        {
            long ticks = store.GetTimeTicks(i);
            if (ticks == _cursorTicks) _seenAtCursor++;
            else { _cursorTicks = ticks; _seenAtCursor = 1; }
            var p = store.GetProvenance(i);
            prints.Add(new TapeTrade(new DateTime(ticks, DateTimeKind.Utc), p.SourceIdentity ?? sel.Data.Feed.Id, store.GetPrice(i), store.GetVolume(i),
                store.GetSide(i), p.IsPartial || !p.IsObservedTrade, p.IsSideInferred));
        }
        var emphasized = new List<TapeRow>(_tape.AddTrades(prints));
        emphasized.AddRange(_tape.Flush());
        bool changed = prints.Count > 0;
        var liquidations = new List<TapeRow>();
        if (_liquidationSymbol != null && Context.Derivatives() is { } derivatives)
        {
            long generation = derivatives.LiquidationGeneration(_liquidationSymbol);
            if (generation != _liquidationGeneration)
            {
                _liquidationGeneration = generation;
                var snapshot = derivatives.Liquidations(_liquidationSymbol, _liquidationFrom, now.AddSeconds(5));
                var fresh = snapshot.Events.Where(_seenLiquidations.Add).ToArray();
                var floor = now - LateLiquidations;
                if (floor > _liquidationFrom)
                {
                    _liquidationFrom = floor;
                    _seenLiquidations.RemoveWhere(e => e.Time < floor);
                }
                liquidations.AddRange(_tape.AddLiquidations(fresh));
                changed |= fresh.Length > 0;
                _lastWindows = DateTime.MinValue;
            }
            if (now - _lastWindows >= TimeSpan.FromSeconds(1) && _tape.Buckets.Count > 0)
            {
                _lastWindows = now;
                var windows = derivatives.Liquidations(_liquidationSymbol, _tape.Buckets[0].Start, now).Windows;
                _tape.ApplyLiquidationWindows(windows, DerivativesData.Venues, now);
            }
        }
        // Only what happened since the tape (re)started, and never partial or synthetic prints: backfilled history is not news.
        Alert(emphasized.Where(r => r.Time >= _alertFrom && !r.Partial).ToList(), liquidations.Where(r => r.Time >= _alertFrom).ToList(), instrument, now);
        return changed;
    }

    /// <summary>Optional local alerts (never external), at most one every three seconds.</summary>
    private void Alert(IReadOnlyList<TapeRow> large, IReadOnlyList<TapeRow> liquidations, Instrument instrument, DateTime now)
    {
        var s = Context.Settings;
        var candidates = (s.TapeSoundLarge ? large : Array.Empty<TapeRow>()).Concat(s.TapeSoundLiquidations ? liquidations : Array.Empty<TapeRow>()).ToArray();
        if (candidates.Length == 0 || now - _lastAlert < AlertSpacing) return;
        _lastAlert = now;
        var top = candidates.MaxBy(r => r.Notional)!;
        string what = top.IsLiquidation
            ? (top.Side == TradeSide.Sell ? $"Long liquidation {WorkbenchFormat.Compact(top.Notional)} on {WorkbenchFormat.Venue(top.Venue)} at {WorkbenchFormat.Price(top.Price)}"
                : $"Short liquidation {WorkbenchFormat.Compact(top.Notional)} on {WorkbenchFormat.Venue(top.Venue)} at {WorkbenchFormat.Price(top.Price)}")
            : (top.Side == TradeSide.Sell ? $"Large sell {WorkbenchFormat.Compact(top.Notional)} on {WorkbenchFormat.Venue(top.Venue)} at {WorkbenchFormat.Price(top.Price)}"
                : $"Large buy {WorkbenchFormat.Compact(top.Notional)} on {WorkbenchFormat.Venue(top.Venue)} at {WorkbenchFormat.Price(top.Price)}");
        Context.Services.Alerts.Raise(AlertKind.Indicator, instrument.Symbol, "Workbench tape", what, allowExternal: false);
    }

    private void UpdateCoverage(DateTime now)
    {
        if (Selection is not { } sel) return;
        var lines = new List<string>();
        IEnumerable<(string Id, string Name, FeedStatus Status, string? Message, bool Partial)> venues;
        if (sel.Data.Feed is AggregateFeed aggregate)
            venues = aggregate.GetCoverage(sel.Instrument, AggregateOperation.Live).Constituents.Select(c => (c.FeedId, c.DisplayName, c.Status, c.Message, c.IsPartial));
        else venues = new[] { (sel.Data.Feed.Id, sel.Data.Feed.DisplayName, sel.Data.Status, sel.Data.StatusMessage, false) };
        foreach (var v in venues)
        {
            if (v.Status != FeedStatus.Connected) _tape.MarkPartial(now, v.Id);
            // Translated per venue: in the joined line a leading "{0}: …" template hole could swallow the earlier venues.
            lines.Add(Localization.Lang.T(CoverageLine(v.Name, v.Status, v.Partial, v.Message is { Length: > 0 } text ? text : null)));
            EnsureVenueButton(v.Id, v.Name);
        }
        foreach (var venue in _tape.Totals.Keys.Where(v => !DerivativesData.Venues.Contains(v, StringComparer.OrdinalIgnoreCase)).ToArray())
            EnsureVenueButton(venue, WorkbenchFormat.Venue(venue));
        _skippedNote.IsVisible = _skipped > 0;
        _skippedNote.Text = _skipped > 0 ? $"{_skipped} prints arrived faster than the tape could list; volume bars may be incomplete." : "";
        // Each venue line is one complete template whose holes are data (venue name, provider message).
        _coverage.Text = string.Join(" · ", lines);
        _liquidationState.Text = _liquidationSymbol == null
            ? sel.IsCryptoUsd ? "Liquidations: derivatives data is unavailable in this session." : "Liquidations: available only for USD-quoted crypto perpetuals (Binance and Bybit streams)."
            : $"Liquidations ({_liquidationSymbol}): Binance and Bybit public streams since this panel started listening; hatched bars were not observed. Binance publishes at most one liquidation per second (a sample).";
    }

    /// <summary>One venue's coverage as a complete display template per state; holes carry only the venue name and the provider's message.</summary>
    internal static string CoverageLine(string venue, FeedStatus status, bool partial, string? message) => (status, message) switch
    {
        (FeedStatus.Connected, _) when !partial => $"{venue}: live",
        (FeedStatus.Connected, null) => $"{venue}: partial",
        (FeedStatus.Connected, { } m) => $"{venue}: partial ({m})",
        (FeedStatus.Connecting, null) => $"{venue}: connecting",
        (FeedStatus.Connecting, { } m) => $"{venue}: connecting ({m})",
        (FeedStatus.Reconnecting, null) => $"{venue}: reconnecting",
        (FeedStatus.Reconnecting, { } m) => $"{venue}: reconnecting ({m})",
        (FeedStatus.Error, null) => $"{venue}: error",
        (FeedStatus.Error, { } m) => $"{venue}: error ({m})",
        (_, null) => $"{venue}: missing",
        (_, { } m) => $"{venue}: missing ({m})",
    };

    private void EnsureVenueButton(string id, string name)
    {
        if (_venueButtons.ContainsKey(id)) return;
        var hidden = Context.Settings.TapeHiddenVenues;
        var button = new ToggleButton { Content = name, IsChecked = !hidden.Contains(id, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2) };
        ToolTip.SetTip(button, "Show or hide this venue's lines in the tape (volume bars always include every venue).");
        button.IsCheckedChanged += (_, _) =>
        {
            hidden.RemoveAll(v => string.Equals(v, id, StringComparison.OrdinalIgnoreCase));
            if (button.IsChecked != true) hidden.Add(id);
            Context.Save();
            Redraw();
        };
        _venueButtons[id] = button;
        _venueFilters.Children.Add(button);
    }

    private void Redraw()
    {
        _list.InvalidateVisual();
        _chart.InvalidateVisual();
    }
}

/// <summary>Newest-first tape lines with emphasis by notional; hidden venues are skipped.</summary>
internal sealed class TapeListCanvas(TradeTape tape, List<string> hidden) : WorkbenchCanvas
{
    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        LoadColors();
        canvas.Clear(Bg);
        const float row = 18;
        float y = 18;
        Label(canvas, "Time", 8, y - 4, Text, 10);
        Label(canvas, "Venue", 92, y - 4, Text, 10);
        Label(canvas, "Price", width * 0.62f, y - 4, Text, 10, right: true);
        Label(canvas, "Size", width * 0.80f, y - 4, Text, 10, right: true);
        Label(canvas, "Notional", width - 8, y - 4, Text, 10, right: true);
        y += 4;
        using var fill = new SKPaint();
        int drawn = 0;
        foreach (var r in tape.Rows)
        {
            if (y > height) break;
            if (hidden.Contains(r.Venue, StringComparer.OrdinalIgnoreCase)) continue;
            var side = r.Side == TradeSide.Sell ? Down : Up;
            var color = r.IsLiquidation ? new SKColor(0xB0, 0x7C, 0xF0) : side;
            byte alpha = r.IsLiquidation ? (byte)70 : r.Emphasis switch { TapeEmphasis.Huge => (byte)110, TapeEmphasis.Large => (byte)45, _ => (byte)0 };
            if (alpha > 0)
            {
                fill.Color = color.WithAlpha(alpha);
                canvas.DrawRect(0, y, width, row, fill);
            }
            if (r.Partial)
            {
                fill.Color = Warn;
                canvas.DrawRect(0, y, 3, row, fill);
            }
            float baseLine = y + 13;
            bool bold = r.Emphasis != TapeEmphasis.Normal || r.IsLiquidation;
            Label(canvas, r.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), 8, baseLine, Text, 10.5f);
            string venue = WorkbenchFormat.Venue(r.Venue);
            if (venue.Length > 18) venue = venue[..18];
            Label(canvas, r.IsLiquidation ? $"LIQ {venue}" : venue, 92, baseLine, r.IsLiquidation ? color : Strong, 10.5f, bold);
            Label(canvas, (r.Side == TradeSide.Sell ? "▼ " : "▲ ") + WorkbenchFormat.Price(r.Price), width * 0.62f, baseLine, color, 10.5f, bold, right: true);
            Label(canvas, (r.SideInferred ? "~" : "") + WorkbenchFormat.Compact(r.Size) + (r.Prints > 1 ? $" ×{r.Prints}" : ""), width * 0.80f, baseLine, Strong, 10.5f, right: true);
            Label(canvas, WorkbenchFormat.Compact(r.Notional), width - 8, baseLine, color, 10.5f, bold, right: true);
            y += row;
            drawn++;
        }
        if (drawn == 0) Label(canvas, "No trades above the threshold yet", 8, 44, Text, 11);
    }
}

/// <summary>
/// Price with large-trade and liquidation markers, per-venue buy/sell volume bars and liquidation bars per time bucket.
/// Slots before observation started, and liquidation buckets no stream observed, are hatched gaps.
/// </summary>
internal sealed class TapeChartCanvas(TradeTape tape) : WorkbenchCanvas
{
    private static readonly SKColor[] Palette =
    {
        new(0x4C, 0x9A, 0xFF), new(0xF5, 0xB0, 0x41), new(0x3D, 0xD6, 0xC6), new(0xE8, 0x6A, 0xF0), new(0x9C, 0xCC, 0x65),
        new(0xFF, 0x7A, 0x59), new(0x8E, 0x8C, 0xF5), new(0xBD, 0xBD, 0xBD), new(0x26, 0xC6, 0xDA), new(0xD4, 0xE1, 0x57),
    };

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        LoadColors();
        canvas.Clear(Bg);
        var buckets = tape.Buckets;
        if (buckets.Count == 0)
        {
            Label(canvas, "Waiting for trades", 10, 22, Text);
            return;
        }
        var length = buckets[0].Length;
        int slots = Math.Max(10, tape.Settings.MaxBuckets);
        var end = buckets[^1].End;
        var start = end - length * slots;
        float left = 8, right = width - 64;
        float slotWidth = (right - left) / slots;
        float X(DateTime t) => left + (float)((t - start).TotalSeconds / length.TotalSeconds) * slotWidth;
        var price = new SKRect(left, 22, right, height * 0.42f);
        var volume = new SKRect(left, price.Bottom + 18, right, height * 0.78f);
        var liq = new SKRect(left, volume.Bottom + 18, right, height - 18);
        using var grid = new SKPaint { Color = Grid, StrokeWidth = 1 };
        foreach (var r in new[] { price, volume, liq }) canvas.DrawLine(r.Left, r.Bottom, r.Right, r.Bottom, grid);
        Label(canvas, "Price · large trades · liquidations", left, 14, Text, 10);
        Label(canvas, "Buy / sell volume by venue (notional)", left, volume.Top - 4, Text, 10);
        Label(canvas, "Liquidations (longs below, shorts above)", left, liq.Top - 4, Text, 10);

        if (tape.ObservedFrom is { } observed && observed > start)
        {
            float gapRight = Math.Min(right, X(observed));
            Hatch(canvas, new SKRect(left, volume.Top, gapRight, volume.Bottom));
            Hatch(canvas, new SKRect(left, liq.Top, gapRight, liq.Bottom));
        }

        // ---- price line and markers
        var priced = buckets.Where(b => b.Last != null).ToArray();
        var markers = tape.Rows.Where(r => (r.Emphasis != TapeEmphasis.Normal || r.IsLiquidation) && r.Time >= start).ToArray();
        decimal lo = decimal.MaxValue, hi = decimal.MinValue;
        foreach (var b in priced) { lo = Math.Min(lo, b.Last!.Value); hi = Math.Max(hi, b.Last!.Value); }
        foreach (var m in markers) { lo = Math.Min(lo, m.Price); hi = Math.Max(hi, m.Price); }
        if (priced.Length > 0 && hi >= lo)
        {
            if (hi == lo) { hi += 1; lo -= 1; }
            float Y(decimal p) => price.Bottom - 6 - (float)((p - lo) / (hi - lo)) * (price.Height - 12);
            using var line = new SKPaint { Color = Strong, StrokeWidth = 1.3f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var path = new SKPath();
            for (int k = 0; k < priced.Length; k++)
            {
                float x = X(priced[k].Start) + slotWidth / 2, y = Y(priced[k].Last!.Value);
                if (k == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            canvas.DrawPath(path, line);
            Label(canvas, WorkbenchFormat.Price(hi), width - 4, price.Top + 10, Text, 10, right: true);
            Label(canvas, WorkbenchFormat.Price(lo), width - 4, price.Bottom - 2, Text, 10, right: true);
            decimal peak = markers.Length > 0 ? markers.Max(m => m.Notional) : 1;
            using var dot = new SKPaint { IsAntialias = true };
            foreach (var m in markers)
            {
                float radius = 3 + 9 * (float)Math.Sqrt((double)(m.Notional / Math.Max(peak, 1)));
                dot.Color = (m.IsLiquidation ? new SKColor(0xB0, 0x7C, 0xF0) : m.Side == TradeSide.Sell ? Down : Up).WithAlpha(170);
                float x = X(m.Time), y = Y(m.Price);
                if (m.IsLiquidation)
                {
                    using var diamond = new SKPath();
                    diamond.MoveTo(x, y - radius); diamond.LineTo(x + radius, y); diamond.LineTo(x, y + radius); diamond.LineTo(x - radius, y); diamond.Close();
                    canvas.DrawPath(diamond, dot);
                }
                else canvas.DrawCircle(x, y, radius, dot);
            }
        }

        // ---- per-venue volume (buys up, sells down) and liquidations
        var venues = buckets.SelectMany(b => b.Venues.Where(v => v.Value.Buy + v.Value.Sell > 0).Select(v => v.Key)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        decimal maxSide = 1, maxLiq = 1;
        foreach (var b in buckets)
        {
            maxSide = Math.Max(maxSide, Math.Max(b.Venues.Values.Sum(v => v.Buy), b.Venues.Values.Sum(v => v.Sell)));
            maxLiq = Math.Max(maxLiq, Math.Max(b.Venues.Values.Sum(v => v.LongLiquidations), b.Venues.Values.Sum(v => v.ShortLiquidations)));
        }
        float mid = volume.MidY, liqMid = liq.MidY;
        canvas.DrawLine(left, mid, right, mid, grid);
        canvas.DrawLine(left, liqMid, right, liqMid, grid);
        using var bar = new SKPaint { IsAntialias = false };
        using var outline = new SKPaint { Color = Warn, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        foreach (var b in buckets)
        {
            float x = X(b.Start) + 0.5f, w = Math.Max(1, slotWidth - 1);
            float up = mid, down = mid;
            for (int vi = 0; vi < venues.Length; vi++)
            {
                if (!b.Venues.TryGetValue(venues[vi], out var v)) continue;
                bar.Color = Palette[vi % Palette.Length];
                float hb = (float)(v.Buy / maxSide) * (volume.Height / 2 - 2), hs = (float)(v.Sell / maxSide) * (volume.Height / 2 - 2);
                canvas.DrawRect(x, up - hb, w, hb, bar);
                bar.Color = Palette[vi % Palette.Length].WithAlpha(150);
                canvas.DrawRect(x, down, w, hs, bar);
                up -= hb;
                down += hs;
                if (v.TradesPartial) canvas.DrawRect(x, up, w, down - up, outline);
            }
            // A stream without an entry was not observed: one silent stream makes the bar partial, never an observed zero.
            var coverage = TradeTape.BucketCoverage(b, DerivativesData.Venues);
            if (coverage == LiquidationCoverage.NotObserved)
            {
                Hatch(canvas, new SKRect(x, liq.Top, x + w, liq.Bottom));
                continue;
            }
            decimal longs = b.Venues.Values.Sum(v => v.LongLiquidations), shorts = b.Venues.Values.Sum(v => v.ShortLiquidations);
            float hl = (float)(longs / maxLiq) * (liq.Height / 2 - 2), hsh = (float)(shorts / maxLiq) * (liq.Height / 2 - 2);
            bar.Color = Down;
            canvas.DrawRect(x, liqMid, w, hl, bar);
            bar.Color = Up;
            canvas.DrawRect(x, liqMid - hsh, w, hsh, bar);
            if (coverage == LiquidationCoverage.Partial) canvas.DrawRect(x, liq.Top, w, liq.Height, outline);
        }
        Label(canvas, WorkbenchFormat.Compact(maxSide), width - 4, volume.Top + 10, Text, 10, right: true);
        Label(canvas, WorkbenchFormat.Compact(maxLiq), width - 4, liq.Top + 10, Text, 10, right: true);

        // ---- venue legend with session totals
        float lx = left, ly = height - 4;
        for (int vi = 0; vi < venues.Length; vi++)
        {
            tape.Totals.TryGetValue(venues[vi], out var total);
            string text = $"{WorkbenchFormat.Venue(venues[vi])} {WorkbenchFormat.Compact(total?.Buy)}/{WorkbenchFormat.Compact(total?.Sell)}";
            bar.Color = Palette[vi % Palette.Length];
            canvas.DrawRect(lx, ly - 8, 8, 8, bar);
            Label(canvas, text, lx + 11, ly, Text, 10);
            lx += 18 + Font(10).MeasureUiText(text);
            if (lx > right) break;
        }
    }
}
