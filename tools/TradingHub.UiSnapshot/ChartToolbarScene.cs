// Scene "chart-toolbar": the one-row chart toolbar, the drawing bar, the selected-drawing bar, gesture drawing with its
// chip, the new drawing kinds, favorites and one chart context menu at a time. Fake time drives the gesture hold.
using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;

internal static class ChartToolbarScene
{
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int failures = 0;
        string size = $"{window.Width:0}x{window.Height:0}";
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")}  {what}");
            if (!ok) failures++;
        }

        void Shot(string name)
        {
            pump(0.4);
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(outDir, $"chart-toolbar_{size}_{name}.png"));
        }

        window.Navigate("trading");
        pump(1.2);
        var tv = window.Trading;
        var tab = tv.ActiveTab!;
        var model = tab.Model!;
        for (int i = 0; i < 100 && model.Series.Count < 40; i++) pump(0.1);
        model.Settings.Style = ChartStyle.Candles;
        model.Viewport.BarWidth = 9;
        model.Rebuild();
        tv.Toolbar.Sync();
        pump(0.6);

        // ---- one-row toolbar (also at narrow widths)
        var wrap = tv.Toolbar.GetVisualDescendants().OfType<WrapPanel>().First();
        var visible = wrap.Children.Where(c => c.IsVisible).ToList();
        double tallest = visible.Max(c => c.Bounds.Height);
        Check(wrap.Bounds.Height <= tallest + 1, $"the toolbar groups fit one row at {size} (wrap height={wrap.Bounds.Height:0}, tallest group={tallest:0}, bar={tv.Toolbar.Bounds.Height:0})");
        Console.WriteLine($"      toolbar {tv.Toolbar.Bounds.Width:0} px · groups " + string.Join(" + ", visible.Select(v => $"{v.Bounds.Width:0}")) +
            $" · right {((Control)wrap.Parent!).GetVisualChildren().OfType<Control>().Where(v => !ReferenceEquals(v, wrap)).Sum(v => v.Bounds.Width):0}");
        Check(tab.Tools != null && tab.DrawingBar != null && tab.InkChip != null, "each chart has its drawing bar, selected-drawing bar and gesture chip");
        Shot("general");

        // ---- selecting a drawing shows the floating bar
        var last = model.Series[^1];
        double px = (double)model.Series.TicksToPrice(last.CloseTicks);
        var line = new ChartDrawing { Kind = DrawingKind.HorizontalLine, Points = { new ChartPoint(last.Time, px) }, Selected = true, ColorArgb = 0xFFF2B53A, Width = 2 };
        model.Drawings.Add(line);
        model.Invalidate();
        pump(0.5);
        Check(tab.DrawingBar!.IsVisible, "a selected drawing shows the floating drawing bar");
        Shot("selected-drawing");

        model.Settings.LockDrawings = true;
        tv.SyncDrawingTools(tab);
        model.Invalidate();
        Shot("locked");
        model.Settings.LockDrawings = false;
        model.Settings.HideDrawings = true;
        foreach (var d in model.Drawings) d.Selected = false;
        model.Invalidate();
        pump(0.5);
        Check(!tab.DrawingBar.IsVisible, "hidden drawings hide the floating bar");
        model.Settings.HideDrawings = false;

        App.Services.Settings.DrawingMagnet = MagnetMode.Strong;
        tab.View.Magnet = MagnetMode.Strong;
        tv.SyncDrawingTools(tab);
        Check(tab.View.Controller?.Magnet == MagnetMode.Strong, "the magnet reaches the chart controller");
        App.Services.Settings.DrawingMagnet = MagnetMode.Off;
        tab.View.Magnet = MagnetMode.Off;
        tv.SyncDrawingTools(tab);

        // ---- favorite indicators: the star adds them with one click
        var info = App.Services.Catalog.Items.First();
        App.Services.Settings.FavoriteIndicators.Clear();
        App.Services.Settings.FavoriteIndicators.Add(info.Type.FullName!);
        Check(tv.FavoriteIndicators().Count == 1, "favorites resolve from the catalog");
        int before = model.Indicators.Count;
        tv.AddIndicator(tab, info);
        Check(model.Indicators.Count == before + 1, "adding a favorite puts it on the chart");
        model.RemoveIndicator(model.Indicators[^1]);
        App.Services.Settings.FavoriteIndicators.Clear();
        model.Drawings.Remove(line);
        model.Invalidate();
        pump(0.4);

        // ---- the new drawing kinds
        var l = model.LastLayout!;
        var main = l.Main.Rect;
        ChartPoint P(float fx, float fy) => new(ChartDrawing.TimeAt(l, model.Series, main.Left + main.Width * fx), l.Main.ValueAt(main.Top + main.Height * fy));
        var shapes = new List<ChartDrawing>
        {
            new() { Kind = DrawingKind.Ellipse, Points = { P(0.10f, 0.15f), P(0.28f, 0.40f) }, ColorArgb = 0xFF38BDF8 },
            new() { Kind = DrawingKind.Arrow, Points = { P(0.33f, 0.45f), P(0.50f, 0.20f) }, ColorArgb = 0xFFF2B53A, Width = 2 },
            new() { Kind = DrawingKind.Path, Points = { P(0.55f, 0.60f), P(0.62f, 0.35f), P(0.70f, 0.55f), P(0.78f, 0.30f) }, ColorArgb = 0xFFB478FF, Width = 2 },
            new() { Kind = DrawingKind.CrossLine, Points = { P(0.85f, 0.70f) }, ColorArgb = 0xFF26C281 },
        };
        var brush = new ChartDrawing { Kind = DrawingKind.Brush, ColorArgb = 0xFFF472B6, Width = 2 };
        var hl = new ChartDrawing { Kind = DrawingKind.Highlighter, ColorArgb = 0xFFF2B53A, Width = 2 };
        for (int i = 0; i <= 40; i++)
        {
            brush.Points.Add(P(0.10f + i * 0.006f, 0.70f + (float)Math.Sin(i / 4.0) * 0.06f));
            hl.Points.Add(P(0.40f + i * 0.007f, 0.82f + (float)Math.Sin(i / 9.0) * 0.02f));
        }

        shapes.Add(brush);
        shapes.Add(hl);
        model.Drawings.AddRange(shapes);
        model.Invalidate();
        Shot("new-drawings");
        Check(shapes.All(d => d.HitTest(l, model.Series, null, d.ScreenPoints(l, model.Series)[0].X, d.ScreenPoints(l, model.Series)[0].Y, out _)), "the new drawings are hit at their first point");
        foreach (var d in shapes) model.Drawings.Remove(d);
        model.Invalidate();

        // ---- gesture drawing: hold (fake clock), ring, stroke and the chip to switch the type
        var c = tab.View.Controller!;
        long now = 0;
        c.Clock = () => now;
        bool wasInk = c.InkGestures;
        c.InkGestures = true;
        float y0 = main.Top + main.Height * 0.35f, x0 = main.Left + main.Width * 0.25f;
        c.MouseDown(x0, y0, PointerButton.Left, 1, InputModifiers.None);
        now += InkRecognizer.HoldMs / 2;
        c.InkTick();
        Shot("gesture-ring");
        now += InkRecognizer.HoldMs;
        Check(c.InkTick() && c.IsInking, "holding still past the hold time starts the gesture");
        float step = main.Width * 0.015f, rise = step * 0.42f;
        for (int i = 1; i <= 30; i++) c.MouseMove(x0 + i * step, y0 - i * rise, InputModifiers.None);
        model.Invalidate();
        Shot("gesture-tracing");
        c.MouseUp(x0 + 30 * step, y0 - 30 * rise, PointerButton.Left, InputModifiers.None);
        pump(0.4);
        var made = model.Drawings.LastOrDefault();
        Check(made?.Kind == DrawingKind.TrendLine, "a diagonal stroke becomes a trend line");
        Check(tab.InkChip!.IsVisible, "releasing shows the gesture chip");
        Shot("gesture-done");

        float sx = x0 + 15 * step, sy = y0 - 15 * rise;
        c.MouseDown(sx, sy - 30, PointerButton.Left, 1, InputModifiers.None);
        now += InkRecognizer.HoldMs + 60;
        c.InkTick();
        for (int k = 0; k < 8; k++)
            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f;
                float xx = k % 2 == 0 ? sx - 50 + 100 * t : sx + 50 - 100 * t;
                c.MouseMove(xx, sy - 30 + k * 8 + t * 2, InputModifiers.None);
            }

        model.Invalidate();
        Shot("gesture-scribble");
        c.MouseUp(sx, sy + 34, PointerButton.Left, InputModifiers.None);
        pump(0.4);
        Check(made != null && !model.Drawings.Contains(made), "a scribble erases the line it crosses");
        Shot("gesture-erased");
        tab.InkChip.Hide();
        c.InkGestures = wasInk;
        c.Clock = () => Environment.TickCount64;

        // ---- favorite drawing tools together on the bar, and the gesture guide
        App.Services.Settings.FavoriteDrawingTools = new List<string> { "Brush", "Ellipse", "FibRetracement" };
        tv.SyncDrawingTools(tab);
        pump(0.3);
        Shot("favorite-tools");
        App.Services.Settings.FavoriteDrawingTools = new List<string>();
        tv.SyncDrawingTools(tab);
        DrawingToolStrip.ShowGuide(tab.Tools!);
        pump(0.5);
        Shot("gesture-guide");
        foreach (var popup in window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>()) popup.IsOpen = false;
        pump(0.3);

        // ---- one chart context menu at a time
        var showMenu = typeof(TradingView).GetMethod("ShowChartMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        showMenu.Invoke(tv, new object[] { tab, new Avalonia.Point(main.MidX, main.MidY) });
        pump(0.3);
        var first = tv.OpenChartContextMenu;
        showMenu.Invoke(tv, new object[] { tab, new Avalonia.Point(main.MidX - 80, main.MidY + 40) });
        pump(0.3);
        var second = tv.OpenChartContextMenu;
        Check(first != null && second != null && !ReferenceEquals(first, second), "each right-click opens a fresh chart menu");
        Check(first?.IsOpen == false && second?.IsOpen == true, "opening a second chart menu closes the first");
        Shot("one-menu");
        second?.Close();
        pump(0.3);
        Check(tv.OpenChartContextMenu == null, "closing the menu clears the slot");

        Console.WriteLine(failures == 0 ? "chart-toolbar: all checks passed" : $"chart-toolbar: {failures} failure(s)");
        return failures;
    }
}
