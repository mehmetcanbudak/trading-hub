using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Tests;

internal static class NonKvpSearchZoomScene
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    internal static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0;
        var receipt = new List<string>();
        void Check(bool condition, string text)
        {
            string line = (condition ? "OK " : "FAIL ") + text;
            Console.WriteLine(line);
            receipt.Add(line);
            if (!condition) failures++;
        }
        void Until(Func<bool> condition)
        {
            for (int i = 0; i < 500 && !condition(); i++) pump(0.01);
            Check(condition(), "bounded fixture operation settles");
        }
        var realTab = window.Trading.ActiveTab;
        var realModel = realTab?.Model;
        string? realAccount = realTab?.AccountId;
        var current = new ConnectionProfile { Id = "scene-search-local", Name = "Local fixture", Kind = ConnectionKind.Simulator };
        var alternative = new ConnectionProfile { Id = "scene-search-alt", Name = "Alternative fixture", Kind = ConnectionKind.Simulator };
        var disabled = new ConnectionProfile { Id = "scene-search-disabled", Name = "Disabled fixture", Kind = ConnectionKind.Simulator, Enabled = false };
        var dataless = new ConnectionProfile { Id = "scene-search-dataless", Name = "Data-less fixture", Kind = ConnectionKind.Databento };
        var unavailable = new ConnectionProfile { Id = "scene-search-error", Name = "Unavailable fixture", Kind = ConnectionKind.Simulator };
        var localFeed = new SearchZoomFeed(SearchZoomFixture.Instrument("LOCAL"));
        var alternativeFeed = new SearchZoomFeed(SearchZoomFixture.Instrument("NQ-FIXTURE"));
        var disabledFeed = new SearchZoomFeed(SearchZoomFixture.Instrument("NQ-DISABLED"));
        var datalessFeed = new SearchZoomFeed(SearchZoomFixture.Instrument("NQ-DATALESS"));
        var unavailableFeed = new SearchZoomFeed { Response = _ => Task.FromException<IReadOnlyList<Instrument>>(new IOException("fixture unavailable")) };
        var services = SearchZoomFixture.Services((current, localFeed), (alternative, alternativeFeed), (disabled, disabledFeed), (dataless, datalessFeed), (unavailable, unavailableFeed));
        foreach (var p in services.Connections.Profiles) InstrumentPicker.ClearCache(p.Id);
        var picker = new InstrumentPicker(services);
        picker.SetCurrent(current.Id, SearchZoomFixture.Instrument("LOCAL"));
        using var initial = SearchZoomFixture.Model(20);
        using var dense = SearchZoomFixture.Model(50_000);
        ChartModel? selectedModel = null;
        var chart = new ChartView { Model = initial };
        var bar = new StackPanel { Children = { picker } };
        DockPanel.SetDock(bar, Dock.Top);
        var panel = new DockPanel { Children = { bar, chart } };
        var fixture = new Window { Title = "Search and bounded zoom · fake data only", Width = 1280, Height = 800, Content = panel };
        int picks = 0;
        string? pickedProfile = null;
        picker.Picked += (profile, instrument, _) =>
        {
            picks++;
            pickedProfile = profile;
            var data = new InstrumentData(instrument, alternativeFeed);
            data.Trades.Add(new Trade(1, SearchZoomFixture.Start, 100, 1, TradeSide.Buy));
            selectedModel = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
            chart.Model = selectedModel;
        };
        var search = Field<TextBox>(picker, "_search");
        var elsewhere = Field<StackPanel>(picker, "_elsewhere");
        var flyout = Field<Flyout>(picker, "_flyout");
        var list = Field<ListBox>(picker, "_list");
        Button? Suggestion() => elsewhere.Children.OfType<Button>().FirstOrDefault();
        void OpenLocal()
        {
            picker.SetCurrent(current.Id, SearchZoomFixture.Instrument("LOCAL"));
            picker.Open();
            Until(() => !Field<bool>(picker, "_loading") && list.ItemCount == 1);
        }
        void CapturePopup(string name)
        {
            pump(0.05);
            var popup = TopLevel.GetTopLevel(elsewhere);
            using var image = popup?.CaptureRenderedFrame();
            Check(image != null, "actual instrument popup renders");
            image?.Save(Path.Combine(outDir, name + ".png"));
        }
        try
        {
            fixture.Show(window);
            pump(0.1);
            OpenLocal();
            int requests = alternativeFeed.Requests + unavailableFeed.Requests;
            search.Text = "";
            pump(0.25);
            search.Text = "LOCAL";
            pump(0.25);
            Check(alternativeFeed.Requests + unavailableFeed.Requests == requests, "blank and successful current searches make no cross-profile requests");
            var favorites = Field<StackPanel>(picker, "_connTabs").Children.OfType<ToggleButton>().First();
            favorites.IsChecked = true;
            favorites.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            search.Text = "NQ";
            pump(0.3);
            Check(alternativeFeed.Requests + unavailableFeed.Requests == requests, "favorites search does not scan other profiles");
            favorites.IsChecked = false;
            favorites.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => Suggestion() != null);
            Check(disabledFeed.Requests == 0 && datalessFeed.Requests == 0, "disabled and data-less profiles are excluded");
            Check(elsewhere.Children.OfType<TextBlock>().Any(t => t.Text?.Contains("fixture unavailable", StringComparison.Ordinal) == true), "failed scan remains visibly incomplete, not exhaustive no-match");
            Check(picks == 0 && ReferenceEquals(chart.Model, initial) && ReferenceEquals(window.Trading.ActiveTab?.Model, realModel) && window.Trading.ActiveTab?.AccountId == realAccount, "suggestions do not pick, change chart, account or trading context");
            CapturePopup("nonkvp_search_suggestions");
            Suggestion()!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !Field<bool>(picker, "_loading") && list.ItemCount == 1);
            Check(search.Text == "NQ" && Field<string>(picker, "_profileId") == alternative.Id && picks == 0 && ReferenceEquals(chart.Model, initial), "suggestion switches only browsing profile and preserves query without auto-pick");
            CapturePopup("nonkvp_search_switched");
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = search, Key = Key.Enter });
            Check(picks == 1 && pickedProfile == alternative.Id && chart.Model?.Instrument.Symbol == "NQ-FIXTURE", "explicit Enter alone invokes the real Picked consumer and changes fixture chart");
            unavailable.Enabled = false;

            OpenLocal();
            InstrumentPicker.ClearCache(alternative.Id);
            var pending = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
            alternativeFeed.Response = _ => pending.Task;
            int started = alternativeFeed.Requests;
            search.Text = "NQ";
            Until(() => alternativeFeed.Requests > started);
            search.Text = "LOCAL";
            pending.SetResult(new[] { SearchZoomFixture.Instrument("NQ-STALE") });
            pump(0.3);
            Check(!elsewhere.IsVisible && list.ItemCount == 1 && picks == 1, "late result from an older query never replaces current matches or selects anything");

            InstrumentPicker.ClearCache(alternative.Id);
            pending = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
            alternativeFeed.Response = _ => pending.Task;
            started = alternativeFeed.Requests;
            search.Text = "NQ";
            Until(() => alternativeFeed.Requests > started);
            var alternativeTab = Field<StackPanel>(picker, "_connTabs").Children.OfType<ToggleButton>().Single(b => Equals(b.Tag, alternative.Id));
            alternativeTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pending.SetResult(new[] { SearchZoomFixture.Instrument("NQ-PROFILE") });
            Until(() => !Field<bool>(picker, "_loading") && list.ItemCount == 1);
            Check(!elsewhere.IsVisible && Field<string>(picker, "_profileId") == alternative.Id && picks == 1, "profile switch cancels an older suggestion scan without stale buttons or auto-pick");
            flyout.Hide();

            OpenLocal();
            InstrumentPicker.ClearCache(alternative.Id);
            pending = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
            alternativeFeed.Response = _ => pending.Task;
            started = alternativeFeed.Requests;
            search.Text = "NQ";
            Until(() => alternativeFeed.Requests > started);
            InstrumentPicker.ClearCache(alternative.Id);
            pending.SetResult(new[] { SearchZoomFixture.Instrument("NQ-INVALIDATED") });
            Until(() => elsewhere.IsVisible);
            Check(Suggestion() == null && elsewhere.Children.OfType<TextBlock>().Any(t => t.Text?.Contains("changed", StringComparison.OrdinalIgnoreCase) == true), "cache invalidation during scan rejects old instruments and displays the unavailable source");
            flyout.Hide();

            OpenLocal();
            InstrumentPicker.ClearCache(alternative.Id);
            pending = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
            alternativeFeed.Response = _ => pending.Task;
            started = alternativeFeed.Requests;
            search.Text = "NQ";
            Until(() => alternativeFeed.Requests > started);
            flyout.Hide();
            pending.SetResult(new[] { SearchZoomFixture.Instrument("NQ-CLOSED") });
            pump(0.3);
            Check(!elsewhere.IsVisible && !Field<bool>(picker, "_open") && picks == 1, "closed popup suppresses late results");

            alternativeFeed.Response = _ => Task.FromResult<IReadOnlyList<Instrument>>(Array.Empty<Instrument>());
            OpenLocal();
            InstrumentPicker.ClearCache(alternative.Id);
            search.Text = "NO-MATCH";
            Until(() => elsewhere.IsVisible);
            Check(Suggestion() == null && elsewhere.Children.OfType<TextBlock>().All(t => t.Text?.Contains("fixture unavailable", StringComparison.Ordinal) != true), "completed empty search is distinct from source failure");
            flyout.Hide();

            OpenLocal();
            InstrumentPicker.ClearCache(alternative.Id);
            pending = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
            alternativeFeed.Response = _ => pending.Task;
            started = alternativeFeed.Requests;
            search.Text = "NQ";
            Until(() => alternativeFeed.Requests > started);
            bar.Children.Remove(picker);
            pending.SetResult(new[] { SearchZoomFixture.Instrument("NQ-DETACHED") });
            pump(0.3);
            Check(!elsewhere.IsVisible && !Field<bool>(picker, "_open"), "detaching control cancels popup and late UI publication");

            chart.Model = dense;
            Until(() =>
            {
                dense.Update(); // Standalone view owns the bounded update normally driven by TradingView.Tick.
                return dense.Series.Count >= 50_000;
            });
            Check(dense.Series.Count >= 50_000, "actual chart contains at least 50,000 deterministic candles with unsampled spikes");
            foreach (double width in new[] { 0.1, 0.5, 1.5, 8.0 })
            {
                dense.Viewport.BarWidth = width;
                dense.Viewport.ScrollBars = 1000;
                var watch = Stopwatch.StartNew();
                chart.InvalidateVisual();
                pump(0.05);
                using var frame = fixture.CaptureRenderedFrame();
                frame?.Save(Path.Combine(outDir, $"nonkvp_zoom_{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png"));
                var layout = dense.LastLayout;
                Check(frame != null && layout != null, "actual ChartView renders width " + width);
                if (layout == null) continue;
                int middle = (layout.FirstVisible + layout.LastVisible) / 2;
                Check(layout.BarAt(layout.CenterX(middle)) == middle, "dense crosshair hit testing round-trips width " + width);
                float focalX = layout.Plot.MidX;
                double focalBar = layout.BarAtExact(focalX);
                chart.Controller!.Wheel(focalX, layout.Main.Rect.MidY, 0, 1, InputModifiers.None);
                chart.InvalidateVisual();
                pump(0.02);
                Check(Math.Abs(dense.LastLayout!.BarAtExact(focalX) - focalBar) < 0.01, "wheel keeps focal bar while scrolled at width " + width);
                chart.Controller.Wheel(focalX, layout.Main.Rect.MidY, 4, 0, InputModifiers.Shift);
                chart.InvalidateVisual();
                pump(0.02);
                Check(double.IsFinite(dense.Viewport.ScrollBars), "scroll remains finite at width " + width);
                watch.Stop();
                receipt.Add($"OBSERVED width={width.ToString(System.Globalization.CultureInfo.InvariantCulture)} candles={dense.Series.Count} render_capture_interaction_ms={watch.Elapsed.TotalMilliseconds:F2}");
            }
            foreach (var style in new[] { ChartStyle.HeikinAshi, ChartStyle.Line, ChartStyle.Bars, ChartStyle.Footprint })
            {
                dense.Settings.Style = style;
                dense.Viewport.BarWidth = 0.1;
                chart.InvalidateVisual();
                pump(0.03);
                using var frame = fixture.CaptureRenderedFrame();
                frame?.Save(Path.Combine(outDir, "nonkvp_zoom_dense_" + style + ".png"));
                Check(frame != null, "dense actual chart preserves style " + style);
            }
            Check(ReferenceEquals(window.Trading.ActiveTab, realTab) && ReferenceEquals(window.Trading.ActiveTab?.Model, realModel) && window.Trading.ActiveTab?.AccountId == realAccount, "entire fake scene preserves main workspace and trading account");
        }
        finally
        {
            flyout.Hide();
            chart.Model = null;
            selectedModel?.Dispose();
            fixture.Close();
            foreach (var p in services.Connections.Profiles) InstrumentPicker.ClearCache(p.Id);
            File.WriteAllLines(Path.Combine(outDir, "nonkvp-search-zoom-results.txt"), receipt);
        }
        return failures;
    }
}
