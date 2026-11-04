using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Tests;

internal static class NonKvpIbCorrectionsScene
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);
    private sealed class SessionProbe : Strategy
    {
        public List<Bar> Seen { get; } = new();
        protected override void OnInit() => UseFootprint = true;
        protected override void OnBar(Bar bar) => Seen.Add(bar);
    }
    internal static int Run(MainWindow window, Action<double> pump, string outDir, ConnectionProfile profile, Instrument instrument, FakeIbGateway gateway)
    {
        int failures = 0;
        void Check(bool ok, string description) { Console.WriteLine($"{(ok ? "OK" : "FAIL")} IB correction: {description}"); if (!ok) failures++; }
        void Until(Func<bool> predicate, double seconds = 15)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!predicate() && DateTime.UtcNow < until) pump(0.02);
            if (!predicate()) throw new TimeoutException("IB correction fixture timed out.");
        }
        void Wait(Task task, double seconds = 15) { Until(() => task.IsCompleted, seconds); task.GetAwaiter().GetResult(); }
        void Shot(Window target, string name) { pump(0.2); using var frame = target.CaptureRenderedFrame(); Check(frame != null, name + " renders"); frame?.Save(Path.Combine(outDir, name + ".png")); }
        var view = window.Page("strategies")!;
        var state = Field<object>(view, "_state");
        var settingsProperty = state.GetType().GetProperty("Settings")!;
        var previous = (BacktestSettings?)settingsProperty.GetValue(state);
        try
        {
            settingsProperty.SetValue(state, null);
            Call(view, "ApplySettingsToForm", (object?)null);
            var sources = Field<ComboBox>(view, "_dataSource");
            var ib = sources.Items.OfType<ComboBoxItem>().First(item => item.Tag is SymbolData { FeedId: "ib", Symbol: "MNQ" });
            sources.SelectedItem = ib; Call(view, "OnDataSourceChanged");
            Field<TextBox>(view, "_period").Text = "D1";
            BacktestSettings Read() => (BacktestSettings)Call(view, "ReadSettingsFromForm", false)!;
            var fresh = Read();
            Check(fresh.TimeZoneId == "America/New_York" && fresh.SessionStart == TimeSpan.FromHours(18), "fresh IB form produces actual NY18 BacktestSettings");
            var start = new DateTime(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc);
            var ticks = TickData.Load(AppPaths.Ticks, instrument, start, start.AddMinutes(2), null, default);
            var probe = new SessionProbe();
            var result = new BacktestEngine(probe, ticks, fresh).Run();
            Check(result.Error == null && probe.Seen.Count == 1 && probe.Seen[0].Time == new DateTime(2026, 10, 30, 22, 0, 0, DateTimeKind.Utc),
                "actual engine consumes form settings and combines UTC-midnight history into one NY18 daily bar");
            Check(probe.Seen.All(bar => bar.IsSynthetic && !bar.HasObservedFootprint && !bar.HasObservedPoc && bar.PocPrice == 0), "actual strategy receives synthetic OHLCV without invented observed footprint or POC");
            Shot(window, "nonkvp_ib_actual_backtest_settings");

            var dataset = (SymbolData)ib.Tag!;
            var other = new ComboBoxItem { Content = "Fixture non-IB source", Tag = new SymbolData("sim", "SESSION-OTHER", dataset.Days) };
            sources.Items.Add(other); sources.SelectedItem = other; Call(view, "OnDataSourceChanged");
            Check(Read().TimeZoneId == null && Read().SessionStart == TimeSpan.Zero, "switch away resets only automatically generated IB session defaults");
            sources.SelectedItem = ib; Call(view, "OnDataSourceChanged");
            var zone = Field<ComboBox>(view, "_timeZone");
            zone.SelectedItem = zone.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag! == "America/Chicago");
            Field<NumericUpDown>(view, "_sessionStart").Value = 7;
            sources.SelectedItem = other; Call(view, "OnDataSourceChanged");
            sources.SelectedItem = ib; Call(view, "OnDataSourceChanged");
            Check(Read().TimeZoneId == "America/Chicago" && Read().SessionStart == TimeSpan.FromHours(7), "explicit unsaved user session survives source changes");
            var savedUtc = fresh.Clone(); savedUtc.TimeZoneId = null; savedUtc.SessionStart = TimeSpan.Zero;
            settingsProperty.SetValue(state, savedUtc); Call(view, "ApplySettingsToForm", savedUtc); Call(view, "OnDataSourceChanged");
            var restored = Read();
            Check(restored.TimeZoneId == null && restored.SessionStart == TimeSpan.Zero, "saved UTC/0 is an explicit override, not mistaken for missing defaults");
            var utcProbe = new SessionProbe(); var utcResult = new BacktestEngine(utcProbe, ticks, restored).Run();
            Check(utcResult.Error == null && utcProbe.Seen.Count == 2, "engine actually honors saved UTC override with two daily bars");
            Shot(window, "nonkvp_ib_saved_session_override");
        }
        finally { settingsProperty.SetValue(state, previous); Call(view, "ApplySettingsToForm", previous); }

        // Exercise the real Code-page action: CompileAsync -> LoadState -> RunBacktestAsync.
        // Each first run has no ui.json, so reading settings directly would miss the regression.
        string nonce = Guid.NewGuid().ToString("N");
        string localFeed = "nonkvp-session-" + nonce;
        var localInstrument = new Instrument { FeedId = localFeed, Symbol = instrument.Symbol, TickSize = instrument.TickSize,
            VolumeStep = instrument.VolumeStep, PointValue = instrument.PointValue, Exchange = "TEST" };
        var fixtureTime = new DateTime(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc);
        for (int i = 0; i < 2; i++)
        {
            var time = fixtureTime.AddMinutes(i);
            string path = TickFile.DayPath(AppPaths.Ticks, localFeed, instrument.Symbol, DateOnly.FromDateTime(time));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new TickFile.Writer(path, localInstrument);
            writer.Write(new Trade(i + 1, time, 100 + i, 1, TradeSide.Buy));
        }
        string ProbeFile(string name)
        {
            string path = Path.Combine(AppPaths.Strategies, name + ".cs");
            File.WriteAllText(path, $$"""
                using System;
                using TradingHub.Strategies;
                [Strategy("Session compile consumer fixture")]
                public sealed class {{name}} : Strategy
                {
                    public static DateTime LastBarTime;
                    protected override void OnInit() => UseFootprint = true;
                    protected override void OnBar(Bar bar) => LastBarTime = bar.Time;
                }
                """);
            return path;
        }
        void ChooseSource(string feedId)
        {
            var sources = Field<ComboBox>(view, "_dataSource");
            sources.SelectedItem = sources.Items.OfType<ComboBoxItem>().First(item => item.Tag is SymbolData data && data.FeedId == feedId && data.Symbol == instrument.Symbol);
            Call(view, "OnDataSourceChanged");
            Field<TextBox>(view, "_period").Text = "D1";
        }
        void Session(string? zoneId, int hour)
        {
            var zone = Field<ComboBox>(view, "_timeZone");
            zone.SelectedItem = zone.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag! == (zoneId ?? ""));
            Field<NumericUpDown>(view, "_sessionStart").Value = hour;
        }
        void CompileAndTest(string feedId, string label, string? zoneId, int hour, int bars, DateTime lastBarTime)
        {
            var before = Field<BacktestResult?>(view, "_lastResult");
            Call(view, "ShowTab", "code");
            var test = view.GetVisualDescendants().OfType<Button>().First(button =>
                button.Content is string text && text == "Compile and test" ||
                button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Compile and test"));
            test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !ReferenceEquals(Field<BacktestResult?>(view, "_lastResult"), before), 60);
            var result = Field<BacktestResult>(view, "_lastResult");
            Check(result.Ok && result.Ticks > 0 && result.Bars == bars, label + ": actual Compile and test completes a daily-bar backtest");
            Check(result.Settings.FeedId == feedId && result.Settings.TimeZoneId == zoneId && result.Settings.SessionStart == TimeSpan.FromHours(hour),
                label + ": actual engine receives the intended session after compilation refresh");
            var current = Field<StrategyFile>(view, "_current");
            Check((DateTime)current.Type!.GetField("LastBarTime", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)! == lastBarTime,
                label + ": compiled OnBar observes the intended session boundary");
            Check(StrategyStore.List(current.Id).Select(run => StrategyStore.Load(current.Id, run.Id)).Any(run =>
                    run != null && run.Bars == bars && run.Settings.FeedId == feedId && run.Settings.TimeZoneId == zoneId && run.Settings.SessionStart == TimeSpan.FromHours(hour)),
                label + ": actual saved run retains the engine-consumed session");
        }
        foreach (var (feedId, label) in new[] { (IbBarImport.FeedId, "IB"), (localFeed, "non-IB") })
        {
            string path = ProbeFile("NonKvpSession" + (feedId == IbBarImport.FeedId ? "Ib" : "Other") + nonce);
            Wait((Task)Call(view, "OpenFileAsync", path)!, 60);
            var current = Field<StrategyFile>(view, "_current");
            if (current.Type == null) throw new InvalidDataException("Session consumer compilation failed: " +
                string.Join("\n", current.LastCompile!.Diagnostics.Select(diagnostic => diagnostic.ToString())));
            ChooseSource(feedId);
            string statePath = (string)view.GetType().GetProperty("StatePath", Private)!.GetValue(view)!;
            Check(!File.Exists(statePath), label + ": first Compile and test starts without saved backtest settings");
            Session("America/Chicago", 7);
            CompileAndTest(feedId, label + " unsaved Chicago07", "America/Chicago", 7, 1,
                new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc));
            Shot(window, "nonkvp_ib_compile_session_" + (feedId == IbBarImport.FeedId ? "ib" : "other"));
            Session(null, 0); Call(view, "SaveState");
            CompileAndTest(feedId, label + " saved UTC00", null, 0, 2,
                new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc));
        }
        Session("America/Chicago", 7);
        string freshPath = ProbeFile("NonKvpSessionFresh" + nonce);
        Wait((Task)Call(view, "OpenFileAsync", freshPath)!, 60);
        ChooseSource(localFeed);
        var freshSettings = (BacktestSettings)Call(view, "ReadSettingsFromForm", false)!;
        Check(freshSettings.TimeZoneId == null && freshSettings.SessionStart == TimeSpan.Zero, "fresh strategy selection does not inherit another strategy's unsaved Chicago07");
        ChooseSource(IbBarImport.FeedId);
        freshSettings = (BacktestSettings)Call(view, "ReadSettingsFromForm", false)!;
        Check(freshSettings.TimeZoneId == "America/New_York" && freshSettings.SessionStart == TimeSpan.FromHours(18), "fresh strategy selection still receives IB automatic NY18 defaults");

        var connections = App.Services.Connections;
        var feed = (IbFeed)connections.GetFeed(profile.Id);
        feed.TickRequestInterval = TimeSpan.Zero;
        profile.IbOffline = false; connections.Invalidate(profile); connections.Save();
        using var hub = new MarketDataHub { RecordingRoot = AppPaths.Ticks, RecordLive = true };
        var retained = hub.GetOrStart(feed, instrument, TimeSpan.FromHours(1.5));
        var accepted = gateway.NextSessionAsync(); Wait(accepted);
        var ready = feed.ClientAsync(default); Wait(ready); var client = ready.Result;
        using (var initialLease = TickFile.TryLock(AppPaths.Ticks, "ib", instrument.Symbol))
            Check(initialLease == null, "retained online chart owns its actual recorder lease before deletion");
        window.Navigate("connections"); pump(0.3);
        var connectionsView = window.Page("connections")!; Call(connectionsView, "Select", profile); pump(0.2);
        var remove = connectionsView.GetVisualDescendants().OfType<Button>().First(button => button.Content is string text && text == "Remove" || button.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Remove"));
        Window OpenRemove()
        {
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => window.OwnedWindows.Any(dialog => dialog.Title == "Remove connection" && dialog.IsVisible));
            return window.OwnedWindows.First(dialog => dialog.Title == "Remove connection" && dialog.IsVisible);
        }
        var cancel = OpenRemove();
        cancel.GetVisualDescendants().OfType<Button>().First(button => button.Content is string text && text == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(0.1);
        Check(connections.Profiles.Contains(profile) && feed.IsLive && client.IsConnected, "Cancel keeps the configured online profile and transport intact");
        var confirm = OpenRemove(); Shot(confirm, "nonkvp_ib_confirmed_remove");
        confirm.GetVisualDescendants().OfType<Button>().First(button => button.Content is string text && text == "Remove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => { hub.PumpAll(); return !connections.Profiles.Contains(profile) && !retained.IsLoadingHistory; });
        Check(!feed.IsLive && !client.IsConnected, "actual confirmed Remove path retires the retained IB scope and closes its Gateway socket");
        Until(() => { hub.PumpAll(); using var lease = TickFile.TryLock(AppPaths.Ticks, "ib", instrument.Symbol); return lease != null; });
        Check(ReferenceEquals(retained.Feed, feed), "retained Hub data is rebound offline without disposing its cached feed");
        var offline = feed.ClientAsync(default); Until(() => offline.IsCompleted);
        Check(offline.IsFaulted && offline.Exception!.GetBaseException() is InvalidOperationException, "deleted retained scope cannot reconnect to Gateway");
        Shot(window, "nonkvp_ib_removed_retained_scope");
        return failures;
    }
}
