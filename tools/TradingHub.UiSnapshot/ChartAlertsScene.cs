// Scene "chart-alerts": chart alerts end to end on an isolated deterministic fake feed. A custom indicator line (captured,
// never declared) and a declared level are picked with real right-clicks and highlighted; a parallel channel is drawn with
// real clicks; the alert dialog creates alerts; history never fires them; live prints fire one stable-id alert per bar;
// removing the drawing removes its alert; the workspace keeps them. Repeat with TRADINGHUB_LANG=es and zh-CN.
using System.Drawing;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

internal static class ChartAlertsScene
{
    private const decimal DrawnLevel = 107, DeclaredLevel = 103;

    /// <summary>A user indicator that draws one labelled line by hand and declares one level.</summary>
    private sealed class SceneLines : Indicator
    {
        public SceneLines() : base(true)
        {
            DataSeries[0] = new ValueDataSeries("Hidden", "Hidden") { VisualType = VisualMode.Hide, ShowZeroValue = false };
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            Name = "Scene lines";
        }

        protected override void OnCalculate(int bar, decimal value) { }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo == null || CurrentBar < 20) return;
            int y = ChartInfo.GetYByPrice(DrawnLevel, false);
            int x = ChartInfo.GetXByBar(CurrentBar - 30);
            context.DrawLine(new RenderPen(Color.Orange, 2), x, y, ChartInfo.PriceChartContainer.Region.Right, y);
            context.DrawString("Hand-drawn line", new RenderFont("Arial", 8), Color.Orange, x, y - 14);
        }

        public override IEnumerable<IndicatorLevel> GetAlertLevels()
        {
            if (CurrentBar > 10) yield return new IndicatorLevel("scene:declared", "Declared level", DeclaredLevel, 10);
        }
    }

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        int checks = 0, failures = 0;
        void Check(bool ok, string what)
        {
            checks++;
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")}  Chart alerts: {what}");
            if (!ok) failures++;
        }

        bool Until(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!condition() && DateTime.UtcNow < deadline) pump(0.02);
            bool ok = condition();
            Check(ok, what);
            return ok;
        }

        void Shot(string name, Window? of = null)
        {
            pump(0.3);
            using var frame = (of ?? window).CaptureRenderedFrame();
            Check(frame != null, "rendered " + name);
            frame?.Save(Path.Combine(outDir, $"chart-alerts_{Lang.Current}_{name}.png"));
        }

        var services = App.Services;
        var trading = window.Trading;
        var original = trading.ActiveTab;
        bool oldSound = services.Settings.SoundAlerts;
        string sourceId = "chart-alerts-ui-" + Guid.NewGuid().ToString("N");
        var source = new Instrument { FeedId = sourceId, Symbol = "ALERT-FIXTURE", Description = "Isolated chart-alert fixture", TickSize = 0.5m, VolumeStep = 1, QuoteAsset = "USD" };
        var feed = new KvpSceneFeed(sourceId, source);
        var start = feed.Now.AddMinutes(-60);
        long id = 0;
        // one hour of alternating closes: crossings all through the history that alerts must never replay
        for (int m = 0; m < 60; m++) feed.Trades.Add(new Trade(++id, start.AddMinutes(m).AddSeconds(5), m % 2 == 0 ? 110 : 100, 1, TradeSide.Buy));
        KvpSceneFeed.Register(services, new ConnectionProfile { Id = sourceId, Name = "Chart alerts fixture", Kind = ConnectionKind.Simulator, HistoryHours = 3 }, feed);
        ChartTab? tab = null;
        try
        {
            services.Settings.SoundAlerts = false;
            window.Navigate("trading");
            var open = trading.NewTabAsync(sourceId, source.Symbol, ChartPeriod.M1);
            if (!Until(() => open.IsCompleted, "the chart opens on the isolated fake feed")) return failures;
            tab = open.GetAwaiter().GetResult();
            var model = tab.Model!;
            if (!Until(() => model is { Series.Count: >= 55, LastLayout: not null } && !model.Data.IsLoadingHistory && !model.IsAggregating, "the recorded hour renders")) return failures;
            model.Settings.Style = ChartStyle.Candles;
            model.Viewport.BarWidth = 14;
            model.Viewport.AutoScale = false;
            model.Viewport.PriceTop = 116;
            model.Viewport.PriceBottom = 94;
            var lines = model.AddIndicator(new SceneLines(), applyDefaultColors: false);
            var profile = new VolumeProfile();
            model.AddIndicator(profile, applyDefaultColors: false);
            model.Update();
            model.Invalidate();
            pump(0.6);
            Check(lines.Error == null, "the custom indicator draws without error");
            Check(lines.DrawnLines.Any(l => l.LastPrice == DrawnLevel && l.Text == "Hand-drawn line"), "the hand-drawn line is captured at its exact price with its label");
            Check(profile.GetAlertLevels().Any(), "the volume profile declares alertable levels");

            var showMenu = typeof(TradingView).GetMethod("ShowChartMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var layout = model.LastLayout!;
            float atX = layout.CenterX(model.Series.Count - 8);
            MenuItem? MenuItemNamed(string name) => trading.OpenChartContextMenu?.ItemsSource?.OfType<MenuItem>().FirstOrDefault(i => i.Name == name);

            // ---- right-click on the captured line: highlighted, offered for an alert
            float lineY = ((IChartContainer)layout.Main).GetYByPrice(DrawnLevel, false);
            showMenu.Invoke(trading, new object[] { tab, new Avalonia.Point(atX, lineY + 2) });
            pump(0.4);
            Check(model.HighlightedLine is { Source: ChartAlertSource.DrawnLine }, "right-click captures the drawn line");
            Check(MenuItemNamed("ChartAlertAddLine") != null, "the menu offers an alert on the picked line");
            Shot("pick-line");
            var picked = model.HighlightedLine!.Copy();
            trading.OpenChartContextMenu?.Close();
            Until(() => model.HighlightedLine == null, "closing the menu clears the highlight");

            // ---- right-click on the declared level, drawn at its row centre
            float levelY = ((IChartContainer)layout.Main).GetYByPrice(DeclaredLevel, false);
            showMenu.Invoke(trading, new object[] { tab, new Avalonia.Point(atX, levelY) });
            pump(0.4);
            Check(model.HighlightedLine is { Source: ChartAlertSource.Level, Field: "scene:declared" }, "right-click captures the declared level");
            Shot("pick-level");
            trading.OpenChartContextMenu?.Close();
            pump(0.3);

            // ---- a parallel channel drawn with real clicks
            var controller = tab.View.Controller!;
            controller.ActiveTool = DrawingKind.ParallelChannel;
            float cx0 = layout.CenterX(model.Series.Count - 40), cx1 = layout.CenterX(model.Series.Count - 10);
            controller.MouseDown(cx0, layout.Main.Y(99m), PointerButton.Left, 1, InputModifiers.None);
            controller.MouseUp(cx0, layout.Main.Y(99m), PointerButton.Left, InputModifiers.None);
            controller.TryCompletePending(cx1, layout.Main.Y(101m), InputModifiers.None);
            var channel = model.Drawings.LastOrDefault(d => d.Kind == DrawingKind.ParallelChannel);
            Check(channel is { Points.Count: 3 } && channel.ChannelOffset > 0, "two clicks draw a parallel channel with a width handle");
            if (channel == null) return failures;
            channel.ExtendRight = true;
            channel.MoveHandle(2, new ChartPoint(channel.Points[0].Time, 108)); // width from the third handle
            foreach (var d in model.Drawings) d.Selected = ReferenceEquals(d, channel);
            model.Invalidate();
            showMenu.Invoke(trading, new object[] { tab, new Avalonia.Point(cx1, layout.Main.Y(104m)) });
            pump(0.4);
            Check(MenuItemNamed("ChartAlertAddDrawing") != null, "a selected channel offers an alert");
            Shot("channel-menu");
            trading.OpenChartContextMenu?.Close();
            pump(0.3);

            // ---- the alert dialog: crossing the captured line, and entering the channel
            var edges = ChartAlertEngine.DrawingLines(model, channel);
            var dialog = new ChartAlertDialog(model, picked);
            dialog.Show(window);
            pump(0.5);
            Shot("dialog-line", dialog);
            dialog.Create();
            dialog.Close();
            var channelDialog = new ChartAlertDialog(model, edges[0], edges[^1], ChartAlertCondition.EnteringChannel);
            channelDialog.Show(window);
            pump(0.3);
            channelDialog.Create();
            pump(0.3);
            Shot("dialog-list", channelDialog);
            channelDialog.Close();
            Check(model.Alerts.Count == 2, "the dialog created both alerts");
            var lineAlert = model.Alerts.FirstOrDefault(a => a.Right.Source == ChartAlertSource.DrawnLine);
            var channelAlert = model.Alerts.FirstOrDefault(a => a.Condition == ChartAlertCondition.EnteringChannel);
            Check(lineAlert != null && channelAlert?.Lower?.Field == "lower", "the channel alert pairs the lower edge");
            if (lineAlert == null || channelAlert == null) return failures;

            int ChartAlertEvents(string alertId) => services.Alerts.Snapshot().Recent.Count(e => e.Id.StartsWith("chart:" + alertId + ":", StringComparison.Ordinal));
            pump(1.0);
            Check(ChartAlertEvents(lineAlert.Id) == 0 && ChartAlertEvents(channelAlert.Id) == 0, "an hour of past crossings fires nothing");

            // ---- live prints: a new bar at 100, then a cross above 107 inside it
            var next = start.AddMinutes(61);
            feed.PushTrades(source, new Trade(++id, next.AddSeconds(1), 100, 1, TradeSide.Buy));
            pump(0.5);
            feed.PushTrades(source, new Trade(++id, next.AddSeconds(5), 109, 1, TradeSide.Buy));
            Until(() => ChartAlertEvents(lineAlert.Id) == 1, "crossing the captured line fires once");
            feed.PushTrades(source, new Trade(++id, next.AddSeconds(9), 100, 1, TradeSide.Sell));
            pump(0.3);
            feed.PushTrades(source, new Trade(++id, next.AddSeconds(12), 109, 1, TradeSide.Buy));
            pump(0.6);
            Check(ChartAlertEvents(lineAlert.Id) == 1, "the forming bar crossing again does not fire twice");
            var fired = services.Alerts.Snapshot().Recent.First(e => e.Id.StartsWith("chart:" + lineAlert.Id + ":", StringComparison.Ordinal));
            Check(fired.Kind == AlertKind.Indicator && fired.Message.Contains("Hand-drawn line", StringComparison.Ordinal), "it is delivered as an indicator alert naming the line");
            Shot("fired");

            // ---- persistence and drawing removal
            Workspace.Save(trading);
            string saved = File.ReadAllText(AppPaths.WorkspaceFile);
            Check(saved.Contains(lineAlert.Id, StringComparison.Ordinal) && saved.Contains(channelAlert.Id, StringComparison.Ordinal), "the workspace keeps the chart's alerts");
            model.Drawings.Remove(channel);
            feed.PushTrades(source, new Trade(++id, next.AddSeconds(20), 101, 1, TradeSide.Buy));
            Until(() => !model.Alerts.Contains(channelAlert), "removing the channel removes its alert");
            Check(model.Alerts.Contains(lineAlert), "the other alert stays");
            Shot("after-removal");

            // ---- level state + every time + expiry through the dialog; an expired alert is listed and never fires
            var stateDialog = new ChartAlertDialog(model, ChartAlertOperand.Fixed(108), null, ChartAlertCondition.GreaterThan);
            stateDialog.Show(window);
            pump(0.3);
            var triggerBox = stateDialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "ChartAlertTrigger");
            triggerBox.SelectedIndex = (int)ChartAlertTrigger.EveryTime;
            var expiryBox = stateDialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "ChartAlertExpiry");
            expiryBox.SelectedIndex = 2; // 1 day
            stateDialog.Create();
            var above = model.Alerts.LastOrDefault(a => a.Condition == ChartAlertCondition.GreaterThan);
            Check(above is { Trigger: ChartAlertTrigger.EveryTime, ExpiresUtc: not null } && above.ExpiresUtc > DateTime.UtcNow.AddHours(23),
                "the dialog creates a greater-than, every-time alert expiring in a day");
            var expired = new ChartAlert { Condition = ChartAlertCondition.LessThan, Right = ChartAlertOperand.Fixed(150), ExpiresUtc = DateTime.UtcNow.AddMinutes(-1) };
            model.Alerts.Add(expired);
            stateDialog.Close();
            var listDialog = new ChartAlertDialog(model);
            listDialog.Show(window);
            pump(0.3);
            Shot("dialog-states", listDialog);
            listDialog.Close();
            if (above == null) return failures;
            feed.PushTrades(source, new Trade(++id, next.AddSeconds(70), 110, 1, TradeSide.Buy));
            Until(() => ChartAlertEvents(above.Id) == 1, "entering above 108 fires the level-state alert");
            pump(0.5);
            Check(ChartAlertEvents(expired.Id) == 0, "the expired alert never fires");
            Check(ChartAlertEvents(above.Id) == 1, "staying above does not fire again");
            return failures;
        }
        catch (Exception error)
        {
            Check(false, "the scene completes: " + error);
            return failures;
        }
        finally
        {
            foreach (var w in window.OwnedWindows.ToArray()) w.Close();
            if (tab != null) trading.CloseChart(tab);
            if (original != null) trading.Activate(original);
            services.Settings.SoundAlerts = oldSound;
            Console.WriteLine($"Chart alerts: {checks} checks, {failures} failures");
        }
    }
}
