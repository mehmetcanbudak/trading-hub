// "dom-book" scene: the DOM's optional book-reading features (auto tick, notional unit, size/total/changes/recent
// columns, distance marks, stats bar, hover mirror), the DOM trading lock and the tape notional filter. Default
// settings keep the existing look; every change is restored at the end. Temporary data and fake providers only.
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Windows;
using TradingHub.Charting.Dom;

internal static class DomBookScene
{
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "OK " : "FAIL ") + what);
            if (!ok) failures++;
        }
        void Shot(string name)
        {
            pump(0.4);
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(outDir, name + ".png"));
        }

        var svc = App.Services;
        window.Navigate("trading");
        var tv = window.Trading;
        foreach (var key in new[] { "ticket", "dom", "tape" }) tv.SetPanel(key, true);
        pump(2);
        var dom = tv.GetVisualDescendants().OfType<DomView>().FirstOrDefault(d => d.IsEffectivelyVisible);
        var tape = tv.GetVisualDescendants().OfType<TapeView>().FirstOrDefault(t => t.IsEffectivelyVisible);
        Check(dom?.Dom != null && tape?.Tape != null, "the DOM and the tape are bound to the active chart");
        if (dom?.Dom == null || tape?.Tape == null) return 1;
        var renderer = (DomRenderer)typeof(DomView).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dom)!;
        var original = svc.Settings.Dom.Clone();

        Check(!original.AutoStep && !original.ShowStats && !original.HoverMirror && original.Unit == DomUnit.Quantity && original.Marks().Length == 0 &&
              original.Columns.Where(c => c.Column is DomColumn.Size or DomColumn.Total or DomColumn.Changes or DomColumn.Recent).All(c => !c.Visible),
            "book-reading features are off by default (existing DOM look)");
        Shot("dom_default");
        float defaultHeader = renderer.HeaderHeight;

        var settings = svc.Settings.Dom;
        settings.AutoStep = true;
        settings.ShowStats = true;
        settings.HoverMirror = true;
        settings.AutoLargeSize = true;
        settings.DistanceMarks = "0.5, 1";
        settings.Unit = DomUnit.Notional;
        foreach (var column in settings.Columns)
            column.Visible = column.Column is DomColumn.Changes or DomColumn.Size or DomColumn.Total or DomColumn.Price or DomColumn.Recent or DomColumn.Volume;
        dom.ApplySettings(settings);
        var middle = dom.TranslatePoint(new Point(dom.Bounds.Width / 2, dom.Bounds.Height * 0.3), window) ?? default;
        window.MouseMove(middle);
        Shot("dom_book_reading");
        Console.WriteLine($"DOM step {dom.Dom.Step} · header {defaultHeader:0} → {renderer.HeaderHeight:0} · changes: {dom.Dom.RecentChangesStatus ?? "available"}");
        Check(renderer.HeaderHeight > defaultHeader, "the stats bar adds a header row");
        Check(dom.Dom.RecentChanges != null && (dom.Dom.RecentChanges.IsAvailable || dom.Dom.RecentChangesStatus != null),
            "the changes column either shows recorded changes or says why it cannot");

        // the DOM trading lock in the DOM header
        var lockButton = tv.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "DomTradingLock" && b.IsEffectivelyVisible);
        Check(lockButton != null && svc.Settings.DomTrading && dom.TradingEnabled, "the DOM header has the trading lock, on by default");
        lockButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(0.3);
        Check(!svc.Settings.DomTrading && !dom.TradingEnabled, "the lock makes the DOM read-only");
        Shot("dom_trading_off");
        lockButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(0.3);
        Check(svc.Settings.DomTrading && dom.TradingEnabled, "the lock turns DOM trading back on");

        // tape: notional unit and minimum-notional filter
        tape.Tape.Unit = DomUnit.Notional;
        tape.Tape.MinNotional = 1000;
        tape.InvalidateVisual();
        Shot("tape_notional_filter");

        // restore the existing look
        svc.Settings.Dom = original;
        dom.ApplySettings(original);
        tape.Tape.Unit = DomUnit.Quantity;
        tape.Tape.MinNotional = svc.Settings.TapeMinNotional;
        svc.Settings.Save();
        pump(0.3);
        Console.WriteLine(failures == 0 ? "dom-book: all checks passed" : $"dom-book: {failures} failures");
        return failures;
    }
}
