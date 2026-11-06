using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using SkiaSharp;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Market;
using OiStudy = TradingHub.Indicators.Native.OpenInterest;

internal static class NonKvpOpenInterestScene
{
    internal static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0;
        void Check(bool condition, string label) { Console.WriteLine($"{(condition ? "OK" : "FAIL")} OI: {label}"); if (!condition) failures++; }
        var originalFocus = window.FocusManager?.GetFocusedElement();
        var originalPointerTarget = ((IInputRoot)window).PointerOverElement as Control;
        window.Navigate("trading");
        var tab = window.Trading.ActiveTab;
        if (tab?.Model == null)
        {
            Check(false, "production trading host has an active chart");
            return failures;
        }
        using var feed = new FixtureFeed();
        using var data = new InstrumentData(feed.Instrument, feed);
        data.Start(feed.Start);
        for (int i = 0; i < 100 && data.IsLoadingHistory; i++) { data.Pump(); pump(0.02); }
        Check(!data.IsLoadingHistory && data.Trades.Count == 20, "actual history consumer loaded controlled trades");
        using var minute = new ChartModel(data, ChartPeriod.M1, new ChartSettings { ShowLegendTitle = true });
        using var five = new ChartModel(data, ChartPeriod.M5);
        var study = new OiStudy();
        var second = new OiStudy();
        minute.AddIndicator(study, applyDefaultColors: false);
        five.AddIndicator(second, applyDefaultColors: false);
        var originalModel = tab.Model;
        var originalViewModel = tab.View.Model;
        var originalMarksKey = tab.MarksKey;
        double originalWidth = window.Width, originalHeight = window.Height;
        var originalPosition = window.Position;
        var originalWindowState = window.WindowState;
        var chartRow = tab.Page!.Host.RowDefinitions[Avalonia.Controls.Grid.GetRow(tab.Frame)];
        var originalRowHeight = chartRow.Height;
        var chartColumn = tab.Page.Host.ColumnDefinitions[Grid.GetColumn(tab.Frame)];
        var originalColumnWidth = chartColumn.Width;
        var originalToolTip = ToolTip.GetTip(tab.View);
        bool originalToolTipOpen = ToolTip.GetIsOpen(tab.View);
        bool originalHeaderVisible = tab.Header.IsVisible;
        bool originalTraderVisible = tab.TraderVisible;
        var oiPanel = minute.SubPanels[0];
        double originalPanelRatio = oiPanel.HeightRatio;
        IndicatorSlot? shortVolumeSlot = null;
        TextBlock? keyboardDetails = null;
        ChartRenderState RenderState() => (ChartRenderState)typeof(TradingHub.App.Controls.ChartView)
            .GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(tab.View)!;

        void RestoreShortViewport()
        {
            if (shortVolumeSlot != null)
            {
                minute.RemoveIndicator(shortVolumeSlot);
                shortVolumeSlot = null;
            }
            oiPanel.HeightRatio = originalPanelRatio;
            chartRow.Height = originalRowHeight;
            chartColumn.Width = originalColumnWidth;
            tab.Header.IsVisible = originalHeaderVisible;
            tab.TraderVisible = originalTraderVisible;
            tab.Trader?.ApplyVisible();
        }

        void RestoreGeometry()
        {
            window.WindowState = Avalonia.Controls.WindowState.Normal;
            window.Width = originalWidth;
            window.Height = originalHeight;
            window.Position = originalPosition;
            window.WindowState = originalWindowState;
        }

        SKColor[] Capture(string name, SKColor[]? previous = null)
        {
            // Drive the same MainWindow -> TradingView.Tick -> dirty ChartView render path as production.
            // Dirtiness is only a settlement fence; acceptance compares actual rendered chart pixels.
            pump(0.05);
            for (int i = 0; i < 50 && minute.IsDirty; i++) pump(0.02);
            using var frame = window.CaptureRenderedFrame();
            Check(frame != null, $"{name}: production host frame rendered");
            if (frame == null) return Array.Empty<SKColor>();
            string path = Path.Combine(outDir, name + ".png");
            frame.Save(path);
            using var bitmap = SKBitmap.Decode(path);
            var position = tab.View.TranslatePoint(new Point(0, 0), window);
            if (bitmap == null || position == null || tab.View.Bounds.Width <= 0 || tab.View.Bounds.Height <= 0)
            {
                Check(false, $"{name}: visible production chart capture is available");
                return Array.Empty<SKColor>();
            }
            double scaleX = bitmap.Width / window.Bounds.Width, scaleY = bitmap.Height / window.Bounds.Height;
            var rectangle = new SKRectI(
                Math.Clamp((int)Math.Floor(position.Value.X * scaleX), 0, bitmap.Width - 1),
                Math.Clamp((int)Math.Floor(position.Value.Y * scaleY), 0, bitmap.Height - 1),
                Math.Clamp((int)Math.Ceiling((position.Value.X + tab.View.Bounds.Width) * scaleX), 1, bitmap.Width),
                Math.Clamp((int)Math.Ceiling((position.Value.Y + tab.View.Bounds.Height) * scaleY), 1, bitmap.Height));
            using var chartFrame = new SKBitmap();
            if (rectangle.Width <= 0 || rectangle.Height <= 0 || !bitmap.ExtractSubset(chartFrame, rectangle))
            {
                Check(false, $"{name}: actual production chart pixels can be captured");
                return Array.Empty<SKColor>();
            }
            var pixels = chartFrame.Pixels;
            if (previous != null) Check(previous.Length > 0 && !pixels.SequenceEqual(previous),
                $"{name}: actual chart frame changes, independently of other window controls");
            return pixels;
        }
        try
        {
            tab.Model = minute;
            tab.View.Model = minute;
            for (int i = 0; i < 100 && data.OpenInterest.Availability == OpenInterestAvailability.Loading; i++) { data.Pump(); pump(0.02); }
            Check(minute.Series[0].HasOpenInterest == false, "no future sample backfilled before first observation");
            Check(minute.Series[10].HasOpenInterest && minute.Series[10].OI == 0, "observed zero is available");
            Check(study.DataSeries[0] is OptionalValueDataSeries total && total.IsAvailable(10), "study publishes valid zero explicitly");
            var historyFrame = Capture("nonkvp-oi-history");
            decimal price = data.LastPrice;
            decimal volume = minute.Series.Candles.Sum(c => c.Volume);
            var freshness = data.LastDataAt;
            feed.Observer!.OnOpenInterest(new(feed.Now.AddSeconds(-10), 21));
            Check(minute.Series.Last!.OI != 21, "callback does not mutate chart before Pump");
            data.Pump();
            Check(minute.Series.Last!.OI == 21 && five.Series.Last!.OI == 21, "OI-only Pump recalculates both chart periods");
            var liveFrame = Capture("nonkvp-oi-live", historyFrame);
            feed.Observer.OnOpenInterest(new(feed.Now.AddSeconds(-10), 18));
            data.Pump();
            Check(minute.Series.Last!.OI == 18, "latest timestamp replacement is idempotent");
            minute.SetPeriod(ChartPeriod.M5);
            Check(minute.Series.Last!.OI == five.Series.Last!.OI, "period rebuild shares observations without lookahead");
            Check(data.LastPrice == price && data.LastDataAt == freshness && minute.Series.Candles.Sum(c => c.Volume) == volume, "OI does not change price, volume or trade freshness");
            var periodFrame = Capture("nonkvp-oi-live-period", liveFrame);
            feed.Observer.OnOpenInterestStatus(OpenInterestAvailability.Unavailable, "Fixture: open-interest coverage unavailable.");
            feed.Now = feed.Now.AddMinutes(11);
            feed.Sink!.OnTrades(feed.Instrument, new[] { new Trade(21, feed.Now, 101, 2, TradeSide.Buy) });
            data.Pump();
            Check(!minute.Series.Last!.HasOpenInterest && study.Status.Contains("unavailable", StringComparison.OrdinalIgnoreCase), "expired coverage and provider status are visible, not zero");
            var unavailableFrame = Capture("nonkvp-oi-unavailable", periodFrame);
            data.RecordingRoot = Path.Combine(outDir, "oi-recorder-fixture");
            SKColor[] busyFrame, narrowBusyFrame, shortBusyFrame, extremeBusyFrame;
            using (var writerLease = TickFile.TryLock(data.RecordingRoot, feed.Instrument.FeedId, feed.Instrument.Symbol))
            {
                Check(writerLease != null, "fixture owns an actual competing history-writer lease");
                feed.IsLive = true;
                data.Start(feed.Start);
                for (int i = 0; i < 100 && data.IsLoadingHistory; i++) { data.Pump(); pump(0.02); }
                Check(!data.IsLoadingHistory && minute.Series.Count > 0 && data.RecordingWarning != null,
                    "writer-busy generation remains readable and exposes the recording warning");
                busyFrame = Capture("nonkvp-oi-recorder-busy", unavailableFrame);
                var wideBadge = RenderState().CompactRecordingWarningBounds;
                Check(wideBadge is { } badge && minute.LastLayout is { } wideLayout &&
                    wideLayout.Plot.Contains(badge) && !badge.Contains(wideLayout.Main.Rect.MidX, wideLayout.Main.Rect.MidY),
                    "recording status stays inside the chart without obscuring the price-pane center");
                var badgeTarget = wideBadge is { } wideBounds
                    ? tab.View.TranslatePoint(new Point(wideBounds.MidX, wideBounds.MidY), window) : null;
                Check(badgeTarget != null, "actual corner status has a pointer target");
                if (badgeTarget is { } badgePoint)
                {
                    window.FocusManager?.ClearFocus();
                    window.MouseMove(new Point(window.Bounds.Width - 8, window.Bounds.Height - 8));
                    tab.View.Controller!.SelectRectangleZoom();
                    double widthBefore = minute.Viewport.BarWidth, scrollBefore = minute.Viewport.ScrollBars;
                    window.MouseMove(badgePoint);
                    window.MouseDown(badgePoint, MouseButton.Left);
                    window.MouseUp(badgePoint, MouseButton.Left);
                    pump(0.2);
                    Check(!tab.View.Controller.IsDragging && RenderState().ZoomRectangle == null &&
                        minute.Viewport.BarWidth == widthBefore && minute.Viewport.ScrollBars == scrollBefore,
                        "status click is consumed without starting or completing a chart gesture");
                    Check(ToolTip.GetIsOpen(tab.View) && ToolTip.GetTip(tab.View) is TextBlock { IsEffectivelyVisible: true },
                        "explicit status click opens visible diagnostic details");
                    using (var detailFrame = TopLevel.GetTopLevel((Control)ToolTip.GetTip(tab.View)!)?.CaptureRenderedFrame())
                        detailFrame?.Save(Path.Combine(outDir, "nonkvp-oi-recorder-detail-wide.png"));
                    tab.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = tab.View });
                    pump(0.1);
                    Check(!ToolTip.GetIsOpen(tab.View), "Escape dismisses details while the pointer remains on the status");
                    tab.View.Controller.ActiveTool = null;
                    if (minute.LastLayout is { } clickLayout &&
                        tab.View.TranslatePoint(new Point(clickLayout.Main.Rect.MidX, clickLayout.Main.Rect.MidY), window) is { } chartPoint)
                    {
                        window.MouseMove(chartPoint);
                        window.MouseDown(chartPoint, MouseButton.Left);
                        window.MouseUp(chartPoint, MouseButton.Left);
                        pump(0.1);
                        Check(!ToolTip.GetIsOpen(tab.View) && tab.View.IsFocused && !tab.View.Controller.IsDragging,
                            "ordinary chart pointer focus does not reopen a large diagnostic popup");
                    }
                    window.MouseMove(new Point(window.Bounds.Width - 8, window.Bounds.Height - 8));
                    window.FocusManager?.ClearFocus();
                    window.MouseMove(badgePoint);
                    pump(0.1);
                    Check(!tab.View.IsFocused && ToolTip.GetIsOpen(tab.View),
                        "hover can disclose a warning without transferring keyboard focus to the chart");
                    window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = window });
                    pump(0.1);
                    Check(!ToolTip.GetIsOpen(tab.View), "host Escape dismisses a hovered warning on an unfocused chart");
                    window.MouseMove(new Point(window.Bounds.Width - 8, window.Bounds.Height - 8));
                    tab.View.Focus(NavigationMethod.Tab);
                    pump(0.1);
                    tab.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = tab.View });
                    pump(0.1);
                    window.MouseMove(badgePoint);
                    pump(0.1);
                    Check(ToolTip.GetIsOpen(tab.View),
                        "a fresh direct badge hover reopens details after keyboard dismissal outside the chart");
                    if (minute.LastLayout is { } focusLayout &&
                        tab.View.TranslatePoint(new Point(focusLayout.Main.Rect.MidX, focusLayout.Main.Rect.MidY), window) is { } resetPoint)
                        window.MouseMove(resetPoint);
                    window.MouseMove(badgePoint);
                    var focusedTrade = new TradeFocus(feed.Start, 100, feed.Start.AddMinutes(1), 101, true, 1, 1, "Fixture selection");
                    minute.FocusTrade = focusedTrade;
                    minute.Invalidate();
                    pump(0.1);
                    tab.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = tab.View });
                    pump(0.1);
                    Check(!ToolTip.GetIsOpen(tab.View) && ReferenceEquals(minute.FocusTrade, focusedTrade),
                        "warning dismissal takes precedence over clearing an independently selected trade");
                    minute.FocusTrade = null;
                    minute.Invalidate();
                }
                double originalPlotWidth = tab.View.Bounds.Width;
                window.WindowState = Avalonia.Controls.WindowState.Normal;
                window.Width = Math.Max(window.MinWidth, Math.Min(1100, window.Bounds.Width - 100));
                window.Height = Math.Max(window.MinHeight, Math.Min(760, originalHeight));
                narrowBusyFrame = Capture("nonkvp-oi-recorder-busy-narrow");
                Check(window.WindowState == Avalonia.Controls.WindowState.Normal && window.Bounds.Width <= 1100 &&
                    tab.View.Bounds.Width > 0 && tab.View.Bounds.Width <= originalPlotWidth,
                    "writer warning renders in a supported normal narrow production window and chart viewport");
                using var competingLease = TickFile.TryLock(data.RecordingRoot, feed.Instrument.FeedId, feed.Instrument.Symbol);
                Check(competingLease == null && data.RecordingWarning != null,
                    "actual competing writer remains owned throughout the narrow warning capture");
                decimal shortPrice = data.LastPrice, shortVolume = minute.Series.Candles.Sum(c => c.Volume);
                var shortFreshness = data.LastDataAt;
                // This is the production splitter's supported 120px row, including its actual 26px chart header.
                chartRow.Height = new Avalonia.Controls.GridLength(120);
                tab.Header.IsVisible = true;
                shortVolumeSlot = minute.AddIndicator(new TradingHub.Indicators.Native.VolumeBars(), applyDefaultColors: false);
                oiPanel.HeightRatio = 0.4;
                shortVolumeSlot.Panel!.HeightRatio = 0.25;
                shortBusyFrame = Capture("nonkvp-oi-recorder-busy-short");
                Check(tab.Frame.Bounds.Height <= 122 && tab.View.Bounds.Height > 0 && tab.View.Bounds.Height <= 100,
                    "actual minimum-height production chart row includes its native header and a short chart viewport");
                Check(minute.LastLayout is { } shortLayout && shortLayout.Panels.Count == 3 &&
                    shortLayout.Main.Rect.Height <= shortLayout.Plot.Height * 0.36,
                    "supported indicator panels produce the minimum-row small price-pane stress case");
                Check(data.RecordingWarning != null && data.LastPrice == shortPrice && data.LastDataAt == shortFreshness &&
                    minute.Series.Candles.Sum(c => c.Volume) == shortVolume,
                    "short warning capture keeps the competing writer and leaves market values unchanged");
                tab.TraderVisible = false;
                tab.Trader?.ApplyVisible();
                chartColumn.Width = new GridLength(160);
                extremeBusyFrame = Capture("nonkvp-oi-recorder-busy-extreme");
                var extremeViewBounds = tab.View.Bounds;
                var viewInFrame = tab.View.TranslatePoint(new Point(0, 0), tab.Frame);
                Check(tab.Frame.Bounds.Width <= 162 && tab.Frame.Bounds.Height <= 122 &&
                    double.IsFinite(extremeViewBounds.Width) && extremeViewBounds.Width > 0 && extremeViewBounds.Width <= 160 &&
                    double.IsFinite(extremeViewBounds.Height) && extremeViewBounds.Height > 0 &&
                    viewInFrame is { } origin && double.IsFinite(origin.X) && double.IsFinite(origin.Y) &&
                    origin.X >= -0.5 && origin.Y >= -0.5 &&
                    origin.X + extremeViewBounds.Width <= tab.Frame.Bounds.Width + 0.5 &&
                    origin.Y + extremeViewBounds.Height <= tab.Frame.Bounds.Height + 0.5,
                    "minimum host cell contains a finite positive chart viewport with Chart Trader collapsed");
                Check(minute.LastLayout is { } visibleLayout &&
                    float.IsFinite(visibleLayout.Plot.Left) && float.IsFinite(visibleLayout.Plot.Top) &&
                    float.IsFinite(visibleLayout.Plot.Right) && float.IsFinite(visibleLayout.Plot.Bottom) &&
                    visibleLayout.Plot.Width > 0 && visibleLayout.Plot.Height > 0 &&
                    visibleLayout.Plot.Left >= -0.5 && visibleLayout.Plot.Top >= -0.5 &&
                    visibleLayout.Plot.Right <= extremeViewBounds.Width + 0.5 &&
                    visibleLayout.Plot.Bottom <= extremeViewBounds.Height + 0.5,
                    "actual extreme plot is finite, positive, and inside the frame-visible chart viewport");
                var disclosureStages = new List<object>();
                void RecordDisclosureStage(string stage)
                {
                    var renderState = RenderState();
                    var tip = ToolTip.GetTip(tab.View) as TextBlock;
                    var root = tip == null ? null : TopLevel.GetTopLevel(tip);
                    disclosureStages.Add(new
                    {
                        Stage = stage,
                        Open = ToolTip.GetIsOpen(tab.View),
                        tab.View.IsFocused,
                        tab.View.IsPointerOver,
                        Pointer = new[] { renderState.MouseX, renderState.MouseY },
                        Badge = renderState.CompactRecordingWarningBounds?.ToString(),
                        TipPresent = tip != null,
                        TipVisible = tip?.IsEffectivelyVisible,
                        TipBounds = tip?.Bounds.ToString(),
                        VisualAncestors = tip?.GetVisualAncestors().Select(v => v.GetType().Name).Take(10).ToArray(),
                        Root = root?.GetType().Name,
                        RootIsMainWindow = ReferenceEquals(root, window),
                        RootVisible = root?.IsVisible,
                        RootBounds = root?.Bounds.ToString(),
                    });
                }
                void DisclosurePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
                {
                    if (args.Property == ToolTip.IsOpenProperty || args.Property == ToolTip.TipProperty)
                        RecordDisclosureStage(args.Property.Name);
                }
                tab.View.PropertyChanged += DisclosurePropertyChanged;
                RecordDisclosureStage("before-hover");
                var hover = RenderState().CompactRecordingWarningBounds is { } disclosureBounds
                    ? tab.View.TranslatePoint(new Point(disclosureBounds.MidX, disclosureBounds.MidY), window)
                    : null;
                Check(hover != null, "extreme chart has a real pointer target for warning disclosure");
                try
                {
                    if (hover is { } target)
                    {
                        window.MouseMove(target);
                        RecordDisclosureStage("after-move");
                        pump(1);
                        RecordDisclosureStage("after-pump");
                    }
                    var details = ToolTip.GetTip(tab.View) as TextBlock;
                    var popup = details == null ? null : TopLevel.GetTopLevel(details);
                    var detailOrigin = details != null && popup != null ? details.TranslatePoint(new Point(0, 0), popup) : null;
                    Check(ToolTip.GetIsOpen(tab.View) && details is { IsEffectivelyVisible: true } &&
                        popup is { IsVisible: true } &&
                        details.GetVisualAncestors().OfType<ToolTip>().Any(t => t.IsEffectivelyVisible) &&
                        double.IsFinite(details.Bounds.Width) && details.Bounds.Width > 0 &&
                        double.IsFinite(details.Bounds.Height) && details.Bounds.Height > 0 &&
                        detailOrigin is { } detailPosition &&
                        double.IsFinite(detailPosition.X) && double.IsFinite(detailPosition.Y) &&
                        detailPosition.X >= -0.5 && detailPosition.Y >= -0.5 &&
                        detailPosition.X + details.Bounds.Width <= popup.Bounds.Width + 0.5 &&
                        detailPosition.Y + details.Bounds.Height <= popup.Bounds.Height + 0.5,
                        "actual chart hover opens an attached visible in-bounds full-detail tooltip");
                    using (var popupFrame = popup?.CaptureRenderedFrame())
                    {
                        Check(popupFrame != null, "actual tooltip host produces a rendered full-detail frame");
                        popupFrame?.Save(Path.Combine(outDir, "nonkvp-oi-recorder-detail-extreme.png"));
                    }
                    using (var hostFrame = window.CaptureRenderedFrame())
                        hostFrame?.Save(Path.Combine(outDir, "nonkvp-oi-recorder-detail-host.png"));
                    window.MouseMove(new Point(window.Bounds.Width - 8, window.Bounds.Height - 8));
                    pump(0.1);
                    ToolTip.SetIsOpen(tab.View, false);
                    window.FocusManager?.ClearFocus();
                    Check(!ToolTip.GetIsOpen(tab.View), "leaving hover closes disclosure before keyboard-only focus");
                    bool focusAccepted = tab.View.Focus(NavigationMethod.Tab);
                    pump(0.2);
                    keyboardDetails = ToolTip.GetTip(tab.View) as TextBlock;
                    var keyboardHost = keyboardDetails == null ? null : TopLevel.GetTopLevel(keyboardDetails);
                    var keyboardOrigin = keyboardDetails != null && keyboardHost != null
                        ? keyboardDetails.TranslatePoint(new Point(0, 0), keyboardHost) : null;
                    Check(focusAccepted && tab.View.IsFocused && ToolTip.GetIsOpen(tab.View) &&
                        keyboardDetails is { IsEffectivelyVisible: true } && keyboardHost is { IsVisible: true } &&
                        keyboardDetails.GetVisualAncestors().OfType<ToolTip>().Any(t => t.IsEffectivelyVisible) &&
                        double.IsFinite(keyboardDetails.Bounds.Width) && keyboardDetails.Bounds.Width > 0 &&
                        double.IsFinite(keyboardDetails.Bounds.Height) && keyboardDetails.Bounds.Height > 0 &&
                        keyboardOrigin is { } keyboardPosition &&
                        double.IsFinite(keyboardPosition.X) && double.IsFinite(keyboardPosition.Y) &&
                        keyboardPosition.X >= -0.5 && keyboardPosition.Y >= -0.5 &&
                        keyboardPosition.X + keyboardDetails.Bounds.Width <= keyboardHost.Bounds.Width + 0.5 &&
                        keyboardPosition.Y + keyboardDetails.Bounds.Height <= keyboardHost.Bounds.Height + 0.5,
                        "keyboard navigation focus opens attached visible in-bounds recording details without an order click");
                    using (var keyboardFrame = keyboardHost?.CaptureRenderedFrame())
                    {
                        Check(keyboardFrame != null, "actual keyboard tooltip host produces a rendered full-detail frame");
                        keyboardFrame?.Save(Path.Combine(outDir, "nonkvp-oi-recorder-keyboard-extreme.png"));
                    }
                    RecordDisclosureStage("after-keyboard-focus");
                }
                finally
                {
                    RecordDisclosureStage("cleanup");
                    tab.View.PropertyChanged -= DisclosurePropertyChanged;
                    File.WriteAllText(Path.Combine(outDir, "nonkvp-oi-tooltip-diagnostic.json"),
                        System.Text.Json.JsonSerializer.Serialize(disclosureStages, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
            }
            data.Start(feed.Start);
            for (int i = 0; i < 100 && data.IsLoadingHistory; i++) { data.Pump(); pump(0.02); }
            Check(!data.IsLoadingHistory && data.RecordingWarning == null, "fresh generation acquires recording after writer release");
            Capture("nonkvp-oi-recorder-ready-extreme", extremeBusyFrame);
            Check(tab.View.IsFocused && !ToolTip.GetIsOpen(tab.View) && ToolTip.GetTip(tab.View) == null &&
                keyboardDetails is { Text: null },
                "recording recovery clears full disclosure and stale content while keyboard focus remains on chart");
            chartColumn.Width = originalColumnWidth;
            tab.TraderVisible = originalTraderVisible;
            tab.Trader?.ApplyVisible();
            Capture("nonkvp-oi-recorder-ready-short", shortBusyFrame);
            RestoreShortViewport();
            Capture("nonkvp-oi-recorder-ready-narrow", narrowBusyFrame);
            RestoreGeometry();
            Capture("nonkvp-oi-recorder-ready", busyFrame);
            data.Stop();
            feed.Observer.OnOpenInterest(new(feed.Now, 999));
            data.Pump();
            Check(data.OpenInterest.Samples.Count == 0, "shutdown rejects late observations");
            using (var floatingLease = TickFile.TryLock(data.RecordingRoot, feed.Instrument.FeedId, feed.Instrument.Symbol))
            {
                if (floatingLease == null) throw new InvalidOperationException("Controlled floating warning could not own the history-writer lease.");
                data.Start(feed.Start);
                for (int i = 0; i < 100 && data.IsLoadingHistory; i++) { data.Pump(); pump(0.02); }
                var detached = window.Trading.Detach(tab) ?? throw new InvalidOperationException("Controlled chart could not detach.");
                try
                {
                    pump(0.2);
                    Check(ReferenceEquals(TopLevel.GetTopLevel(tab.View), detached) && detached.IsVisible && tab.View.IsEffectivelyVisible,
                        "the actual detached chart owns a visible warning consumer");
                    var floatingTarget = RenderState().CompactRecordingWarningBounds is { } floatingBounds
                        ? tab.View.TranslatePoint(new Point(floatingBounds.MidX, floatingBounds.MidY), detached) : null;
                    if (floatingTarget is not { } floatingPoint) throw new InvalidOperationException("Detached warning has no pointer target.");
                    detached.MouseMove(new Point(detached.Bounds.Width - 8, detached.Bounds.Height - 8));
                    detached.FocusManager?.ClearFocus();
                    detached.MouseMove(floatingPoint);
                    pump(0.1);
                    Check(!tab.View.IsFocused && ToolTip.GetIsOpen(tab.View),
                        "detached chart hover reveals details without stealing keyboard focus");
                    detached.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = detached });
                    pump(0.1);
                    Check(!ToolTip.GetIsOpen(tab.View), "detached host Escape dismisses the unfocused hovered warning");
                    detached.MouseMove(new Point(detached.Bounds.Width - 8, detached.Bounds.Height - 8));
                    detached.MouseMove(floatingPoint);
                    pump(0.1);
                    Check(ToolTip.GetIsOpen(tab.View), "detached warning can be disclosed again in a new hover session");
                    var floatingTrade = new TradeFocus(feed.Start, 100, feed.Start.AddMinutes(1), 101, true, 1, 1, "Fixture detached selection");
                    minute.FocusTrade = floatingTrade;
                    minute.Invalidate();
                    pump(0.1);
                    using (var detachedFrame = detached.CaptureRenderedFrame())
                        detachedFrame?.Save(Path.Combine(outDir, "nonkvp-oi-recorder-detached.png"));
                    tab.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = tab.View });
                    pump(0.1);
                    Check(!ToolTip.GetIsOpen(tab.View) && ReferenceEquals(minute.FocusTrade, floatingTrade),
                        "detached warning dismissal precedes selected-trade clearing");
                    tab.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = tab.View });
                    pump(0.1);
                    Check(minute.FocusTrade == null, "the next detached Escape retains the existing selected-trade shortcut");
                }
                finally
                {
                    minute.FocusTrade = null;
                    window.Trading.DockBack(detached);
                    data.Stop();
                    pump(0.1);
                }
            }
        }
        finally
        {
            ToolTip.SetIsOpen(tab.View, false);
            RestoreShortViewport();
            tab.Model = originalModel;
            tab.View.Model = originalViewModel;
            tab.MarksKey = originalMarksKey;
            RestoreGeometry();
            pump(0.1);
            var originalPointerPosition = originalPointerTarget?.TranslatePoint(
                new Point(originalPointerTarget.Bounds.Width / 2, originalPointerTarget.Bounds.Height / 2), window);
            window.MouseMove(originalPointerPosition ?? new Point(-1, -1));
            if (originalFocus != null) originalFocus.Focus();
            else window.FocusManager?.ClearFocus();
            ToolTip.SetTip(tab.View, originalToolTip);
            ToolTip.SetIsOpen(tab.View, originalToolTipOpen);
        }
        return failures;
    }

    private sealed class FixtureFeed : IMarketDataFeed, IOpenInterestProvider
    {
        public DateTime Start { get; } = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Now { get; set; } = new(2026, 10, 15, 12, 19, 45, DateTimeKind.Utc);
        public Instrument Instrument { get; } = new() { FeedId = "oi-fixture", Symbol = "OI-FIXTURE", TickSize = 1, VolumeStep = 1 };
        public IMarketDataSink? Sink;
        public string Id => "oi-fixture";
        public string DisplayName => "Controlled open interest";
        public bool SupportsHistory => true;
        public bool IsLive { get; set; }
        public IOpenInterestObserver? Observer;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { Instrument });
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            onBatch(Enumerable.Range(0, 20).Select(i => new Trade(i + 1, Start.AddMinutes(i), 100 + i % 6, 2, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell)).ToArray());
            return Task.CompletedTask;
        }
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) { Sink = sink; return new Lease(); }
        public bool SupportsOpenInterest(Instrument instrument) => instrument.FeedId == Id;
        public TimeSpan OpenInterestRetention => TimeSpan.FromDays(30);
        public TimeSpan OpenInterestMaxAge => TimeSpan.FromMinutes(10);
        public Task<OpenInterestHistoryResult> LoadOpenInterestAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
        {
            IReadOnlyList<OpenInterestSample> samples = new[] { new OpenInterestSample(Start.AddMinutes(5), 10), new(Start.AddMinutes(10), 0), new(Start.AddMinutes(15), 12) };
            return Task.FromResult(new OpenInterestHistoryResult(samples, samples[0].Time, samples[^1].Time, OpenInterestAvailability.Partial, "Fixture: five-minute observations, incomplete earlier coverage."));
        }
        public IDisposable SubscribeOpenInterest(Instrument instrument, IOpenInterestObserver observer) { Observer = observer; return new Lease(); }
        public void Dispose() { }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
