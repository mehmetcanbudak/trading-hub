// "safety" scene part: the DOM trading switch. Real pointer gestures on the DOM of a chart routed to the fake REAL
// account: on (default) they reach the unchanged confirmed order path; off, no click, cancel, drag or DOM order button
// reaches the account or opens a confirmation. Fake accounts only.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Charting.Dom;
using TradingHub.Trading;

internal static partial class SafetyScene
{
    private static void DomTradingSwitch(MainWindow window, Action<double> pump, TradingView tv, ChartTab chart, ITradingAccount real,
        Action<bool, string> check, Action<Window, string> shot, Func<string> status)
    {
        var svc = App.Services;
        check(svc.Settings.DomTrading, "DOM trading defaults on (existing behaviour)");
        tv.SetChartAccount(chart, "safety-real");
        tv.SetPanel("dom", true);
        pump(0.6);
        var dom = tv.GetVisualDescendants().OfType<DomView>().FirstOrDefault(d => d.IsEffectivelyVisible && d.Dom?.Data == chart.Model!.Data);
        check(dom?.Dom?.Broker == real && dom.TradingEnabled, "the DOM is bound to the fake REAL account with trading enabled");
        if (dom?.Dom == null) return;
        var renderer = Field<DomRenderer>(dom, "_renderer");
        var inst = dom.Dom.Instrument;
        decimal last = dom.Dom.Data.LastPrice;

        Point? Cell(DomColumn column, Func<decimal, bool> want)
        {
            for (double y = renderer.HeaderHeight + 2; y < dom.Bounds.Height - 2; y += dom.Dom.RowHeight)
                for (double x = 2; x < dom.Bounds.Width - 2; x += 3)
                    if (renderer.HitTest(dom.Dom, (float)x, (float)y) is { } hit && hit.Column == column && want(hit.Price))
                        return new Point(x, y);
            return null;
        }

        void Press(Point at, MouseButton button)
        {
            var p = dom.TranslatePoint(at, window) ?? default;
            window.MouseDown(p, button);
            window.MouseUp(p, button);
        }

        Window? Confirmation(double seconds)
        {
            string title = Lang.T("Confirm REAL order");
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                pump(0.05);
                if (window.OwnedWindows.FirstOrDefault(w => w.Title == title && w.IsVisible) is { } found) return found;
            }
            return null;
        }

        var bidCell = Cell(DomColumn.Bid, p => p <= last - 2 * inst.TickSize);
        check(bidCell != null, $"a bid cell below the market is on screen (last {last})");
        if (bidCell is not { } bid) return;
        decimal bidPrice = renderer.HitTest(dom.Dom, (float)bid.X, (float)bid.Y)!.Value.Price;

        // ON: the existing path, including the REAL confirmation
        Press(bid, MouseButton.Left);
        var dialog = Confirmation(3);
        check(dialog != null, "on: a DOM click on a REAL account opens the unchanged «Confirm REAL order» dialog");
        if (dialog != null)
        {
            ButtonOf(dialog, "Cancel")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pump(0.3);
        }
        check(!real.ActiveOrders.Any(), "on: canceling the confirmation sends nothing");
        Press(bid, MouseButton.Left);
        dialog = Confirmation(3);
        if (dialog != null) SafetyScene.Press(dialog, Key.Enter, Keys.Command);
        pump(0.5);
        var working = real.ActiveOrders.FirstOrDefault(o => o.Type == OrderTypes.Limit && o.Direction == OrderDirections.Buy && o.Price == bidPrice);
        check(dialog != null && working != null, $"on: confirming places the DOM limit buy at {inst.FormatPrice(bidPrice)}");

        // OFF: persisted, and every DOM gesture and DOM order button is inert
        tv.SetDomTrading(false);
        pump(0.3);
        check(!svc.Settings.DomTrading && !AppSettings.Load().DomTrading && !dom.TradingEnabled, "off: the switch is persisted and applied to the DOM");
        int orders = real.ActiveOrders.Count();
        decimal position = real.Position.Volume;
        if (Cell(DomColumn.Bid, p => p < bidPrice) is { } other)
        {
            Press(other, MouseButton.Left);
            Press(other, MouseButton.Left); // a double click too
        }
        if (Cell(DomColumn.Ask, p => p > last + 2 * inst.TickSize) is { } ask) Press(ask, MouseButton.Left);
        check(Confirmation(1) == null && real.ActiveOrders.Count() == orders && real.Position.Volume == position,
            $"off: DOM clicks open no confirmation and send nothing (orders {real.ActiveOrders.Count()}, status «{status()}»)");
        check(status().Contains(Lang.T(DomOrderGestures.TradingOffMessage)) || status().Contains(DomOrderGestures.TradingOffMessage),
            "off: the status bar explains the DOM is read-only");
        if (working != null && Cell(DomColumn.BuyOrders, p => p == bidPrice) is { } own)
        {
            Press(own, MouseButton.Right);
            pump(0.3);
            check(real.ActiveOrders.Contains(working), "off: a right-click cancel on the DOM leaves the order working");
            var from = dom.TranslatePoint(own, window) ?? default;
            var to = dom.TranslatePoint(new Point(own.X, own.Y + 3 * dom.Dom.RowHeight), window) ?? default;
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to);
            window.MouseUp(to, MouseButton.Left);
            check(Confirmation(1) == null && working.Price == bidPrice, $"off: dragging the order moves nothing (price {inst.FormatPrice(working.Price)})");
        }
        var buttons = tv.GetVisualDescendants().OfType<Button>().Where(b => b.Content is TextBlock t && (t.Text == "Buy MKT" || t.Text == Lang.T("Buy MKT"))).ToList();
        check(buttons.Count > 0 && buttons.All(b => !b.IsEffectivelyEnabled), "off: the DOM order buttons are disabled");
        foreach (var button in buttons) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // even a forced click
        check(Confirmation(1) == null && real.Position.Volume == position, "off: a forced DOM order-button click sends nothing");
        check(tv.GetVisualDescendants().OfType<NumericUpDown>().Any(n => n.IsEffectivelyVisible && n.IsEffectivelyEnabled), "off: quantity entry stays usable");
        shot(window, "safety_dom_trading_off");

        // ON again: the existing cancel path works unchanged
        tv.SetDomTrading(true);
        pump(0.3);
        if (working != null && Cell(DomColumn.BuyOrders, p => p == bidPrice) is { } again)
        {
            Press(again, MouseButton.Right);
            pump(0.5);
            check(!real.ActiveOrders.Contains(working), "on again: a right-click cancel on the DOM cancels the order as before");
        }
        foreach (var o in real.ActiveOrders.ToList()) real.Cancel(o);
        check(svc.Settings.DomTrading && dom.TradingEnabled, "the switch is restored to its default");
        tv.SetChartAccount(chart, "sim");
        pump(0.2);
    }
}
