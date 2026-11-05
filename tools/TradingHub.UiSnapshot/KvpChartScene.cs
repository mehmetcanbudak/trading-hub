using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Data;
using TradingHub.Market;

internal static class KvpChartScene
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Session(TradingView view) => typeof(TradingView).GetField("_chartEntry", Fields)!.GetValue(view);
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Fields)!.GetValue(value)!;

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int checks = 0, failures = 0;
        void Check(bool condition, string description)
        {
            checks++;
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} KVP chart: {description}");
            if (!condition) failures++;
        }
        void Until(Func<bool> condition, string description)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!condition() && DateTime.UtcNow < until) pump(0.02);
            Check(condition(), description);
        }
        static void Key(Control control, Key key) => control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = control });
        var trading = window.Trading;
        var original = trading.ActiveTab;
        string id = "kvp-chart-ui-" + Guid.NewGuid().ToString("N");
        Instrument Instrument(string symbol) => new() { FeedId = id, Symbol = symbol, Description = symbol + " isolated fixture", TickSize = 1, VolumeStep = 1, PointValue = 2 };
        var feed = new KvpSceneFeed(id, Instrument("LOCAL"), Instrument("15"), Instrument("LATE"));
        var start = feed.Now.AddHours(-3);
        for (int bar = 0; bar < 180; bar++)
        {
            decimal price = 100 + bar % 17;
            feed.Trades.Add(new Trade(bar * 3 + 1, start.AddMinutes(bar), price, 2, TradeSide.Buy));
            feed.Trades.Add(new Trade(bar * 3 + 2, start.AddMinutes(bar).AddSeconds(10), price + 2, 1, TradeSide.Sell));
            feed.Trades.Add(new Trade(bar * 3 + 3, start.AddMinutes(bar).AddSeconds(20), price - 2, 1, TradeSide.None));
        }
        var profile = new ConnectionProfile { Id = id, Name = "KVP isolated keyboard source", Kind = ConnectionKind.Simulator, HistoryHours = 6 };
        KvpSceneFeed.Register(App.Services, profile, feed);
        var opening = trading.NewTabAsync(id, "LOCAL", ChartPeriod.M1);
        Until(() => opening.IsCompleted, "actual selected fixture chart opens");
        if (!opening.IsCompletedSuccessfully) return failures + 1;
        var chart = opening.Result;
        Until(() => chart.Model is { Series.Count: > 0, LastLayout: not null }, "fake history reaches the actual chart renderer");
        var model = chart.Model!;
        string account = chart.AccountId;
        int orders = trading.AccountOf(chart)?.ActiveOrders.Count() ?? 0;
        window.Navigate("trading"); chart.View.Focus(); pump(0.1);

        object? Type(string text)
        {
            chart.View.Focus();
            chart.View.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Source = chart.View, Text = text });
            Until(() => Session(trading) != null, "committed chart text opens actual quick-entry popup");
            return Session(trading);
        }
        void CapturePopup(object session, string name)
        {
            pump(0.1);
            using var frame = TopLevel.GetTopLevel(Field<TextBox>(session, "Input"))?.CaptureRenderedFrame();
            Check(frame != null, "actual popup renders " + name);
            frame?.Save(Path.Combine(outDir, name + ".png"));
        }
        try
        {
            var ime = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent, Source = chart.View };
            chart.View.RaiseEvent(ime);
            Check(ime.Client != null, "focused chart provides actual IME client");
            ime.Client?.SetPreeditText("LO"); pump(0.05);
            Check(chart.View.IsTextComposing && Session(trading) == null && chart.Symbol == "LOCAL", "uncommitted IME text changes no symbol or period");
            ime.Client?.SetPreeditText(null);

            if (Type("15") is { } numeric)
            {
                var input = Field<TextBox>(numeric, "Input"); var results = Field<ListBox>(numeric, "Results");
                Until(() => results.ItemCount == 2, "numeric symbol and period remain separate visible choices");
                Check(results.SelectedIndex == 0 && chart.Symbol == "LOCAL", "visible exact-symbol selection has not yet changed chart");
                CapturePopup(numeric, "kvp_chart_numeric_ambiguity");
                Key(input, Avalonia.Input.Key.Enter);
                Until(() => chart.Symbol == "15", "explicit Enter opens the visibly selected numeric symbol");
                Check(chart.AccountId == account && chart.Model?.Period == ChartPeriod.M1, "numeric symbol selection preserves account and period");
            }
            if (Type("M3") is { } period)
            {
                var input = Field<TextBox>(period, "Input"); var results = Field<ListBox>(period, "Results");
                Until(() => results.ItemCount > 0, "typed period has a visible selection");
                var popupIme = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent, Source = input };
                input.RaiseEvent(popupIme); popupIme.Client?.SetPreeditText("3");
                Key(input, Avalonia.Input.Key.Enter); pump(0.1);
                Check(chart.Model?.Period == ChartPeriod.M1, "Enter during popup IME composition cannot apply a period");
                popupIme.Client?.SetPreeditText(null);
                Key(input, Avalonia.Input.Key.Enter);
                Until(() => chart.Model?.Period.Code == "M3", "committed period selection reaches actual toolbar/model");
                Check(chart.AccountId == account, "period entry retains exact trading account");
            }
            if (Type("LOC") is { } prefix)
            {
                var input = Field<TextBox>(prefix, "Input"); var results = Field<ListBox>(prefix, "Results");
                Until(() => results.ItemCount == 1, "prefix discovery completes against fake provider");
                Check(results.SelectedIndex == -1, "arbitrary prefix has no hidden selected result");
                Key(input, Avalonia.Input.Key.Enter); pump(0.1);
                Check(chart.Symbol == "15", "Enter without a visible selected row cannot navigate");
                Key(input, Avalonia.Input.Key.Down); Key(input, Avalonia.Input.Key.Enter);
                Until(() => chart.Symbol == "LOCAL", "arrow-selected visible row alone authorizes symbol navigation");
            }
            var stable = chart.Model;
            if (Type("15") is { } canceled)
            {
                Key(Field<TextBox>(canceled, "Input"), Avalonia.Input.Key.Escape); pump(0.05);
                Check(Session(trading) == null && ReferenceEquals(chart.Model, stable), "Escape closes quick entry without replacing chart");
            }
            chart.View.Focus(); chart.View.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "?", Source = chart.View }); pump(0.1);
            var help = typeof(TradingView).GetField("_chartHelp", Fields)!.GetValue(trading) as Window;
            Check(help is { IsVisible: true }, "committed question mark opens actual shortcut help");
            using (var frame = help?.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_chart_shortcuts.png"));
            help?.Close();

            InstrumentPicker.ClearCache(id);
            var pending = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
            feed.InstrumentsResponse = _ => pending.Task;
            if (Type("LATE") is { })
            {
                if (original != null) trading.Activate(original);
                pending.TrySetResult([Instrument("LATE")]); pump(0.2);
                Check(Session(trading) == null && ReferenceEquals(chart.Model, stable), "tab switch rejects late discovery without model/account mutation");
            }
            feed.InstrumentsResponse = null; InstrumentPicker.ClearCache(id);
            trading.Activate(chart); pump(0.1);
            var current = chart.Model!;
            current.Settings.TimeZoneId = "America/New_York";
            var reopened = trading.OpenChartSelectionAsync(chart, id, "LOCAL", current.Period, current.Settings.Clone(), current, window);
            Until(() => reopened.IsCompleted, "guarded selection reuses actual provider lookup");
            Check(chart.Model!.Settings.TimeZoneId == "America/New_York", "per-chart clock survives symbol reopen rather than being reset globally");

            model = chart.Model;
            trading.ExecuteHotkey("rectangle-zoom", chart); chart.View.InvalidateVisual(); pump(0.1);
            if (model.LastLayout is { } layout)
            {
                double beforeWidth = model.Viewport.BarWidth;
                Point? from = chart.View.TranslatePoint(new Point(layout.Main.Rect.Left + layout.Main.Rect.Width * 0.2, layout.Main.Rect.Top + layout.Main.Rect.Height * 0.2), window);
                Point? to = chart.View.TranslatePoint(new Point(layout.Main.Rect.Left + layout.Main.Rect.Width * 0.7, layout.Main.Rect.Top + layout.Main.Rect.Height * 0.7), window);
                if (from is { } a && to is { } b)
                {
                    window.MouseDown(a, MouseButton.Left); window.MouseMove(b); window.MouseUp(b, MouseButton.Left); pump(0.1);
                    Check(!model.Viewport.AutoScale && model.Viewport.BarWidth > beforeWidth, "actual rectangle pointer gesture changes time and price bounds");
                }
                else Check(false, "actual chart coordinates translate to its window");
                trading.ExecuteHotkey("reset-view", chart); pump(0.05);
                Check(model.Viewport.AutoScale && model.Viewport.ScrollBars == 0, "reset restores autoscale and live edge");
            }
            foreach (var kind in new[] { DrawingKind.AnchoredVwap, DrawingKind.FixedRangeProfile, DrawingKind.LongPosition })
            {
                var drawing = new ChartDrawing { Kind = kind, Points = [new(start.AddMinutes(40), 106), new(start.AddMinutes(100), 112)] };
                if (kind == DrawingKind.AnchoredVwap) drawing.Points.RemoveAt(1);
                if (kind == DrawingKind.LongPosition) drawing.Points.Add(new ChartPoint(start.AddMinutes(100), 100));
                model.Drawings.Add(drawing); model.Invalidate(); chart.View.InvalidateVisual(); pump(0.1);
                trading.EditDrawing(chart, drawing); pump(0.1);
                var dialogs = (System.Collections.IDictionary)typeof(TradingView).GetField("_drawingWindows", Fields)!.GetValue(trading)!;
                var dialog = dialogs[drawing.Id] as Window;
                Check(dialog is { IsVisible: true }, "actual drawing settings opens for " + kind);
                using (var frame = dialog?.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_drawing_settings_" + kind + ".png"));
                dialog?.Close();
            }
            Check(chart.AccountId == account && (trading.AccountOf(chart)?.ActiveOrders.Count() ?? 0) == orders, "all chart typing/navigation/drawings send zero orders and preserve account");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_chart_drawings.png"));
        }
        finally
        {
            pendingSearchCleanup();
            void pendingSearchCleanup() { trading.CancelChartQuickEntry(); if (original != null) trading.Activate(original); trading.CloseChart(chart); }
            File.WriteAllText(Path.Combine(outDir, "kvp-chart-results.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures }));
        }
        return failures;
    }
}
