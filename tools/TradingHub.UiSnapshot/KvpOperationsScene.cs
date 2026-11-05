using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

internal static class KvpOperationsScene
{
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        int checks = 0, failures = 0;
        object? maintenanceEvidence = null;
        void Check(bool value, string description)
        {
            checks++;
            Console.WriteLine($"{(value ? "OK" : "FAIL")} KVP operations: {description}");
            if (!value) failures++;
        }
        void Await(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!task.IsCompleted && DateTime.UtcNow < until) pump(0.02);
            Check(task.IsCompleted, "bounded actual control operation completes");
            if (task.IsCompleted) task.GetAwaiter().GetResult();
        }
        void Until(Func<bool> condition, string description)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!condition() && DateTime.UtcNow < until) pump(.02);
            Check(condition(), description);
            if (!condition()) throw new TimeoutException(description);
        }
        static T Named<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.Navigate("settings"); pump(0.2);
        var settings = window.Page("settings")!;
        var compact = Named<ToggleSwitch>(settings, "UseCompactHistory");
        bool oldCompact = App.Services.Settings.UseCompactHistory;
        compact.BringIntoView(); compact.IsChecked = !oldCompact; pump(0.05);
        Check(App.Services.Settings.UseCompactHistory == !oldCompact && App.Services.Hub.UseCompactHistory == !oldCompact,
            "real compact toggle changes persisted preference and history consumer together");
        compact.IsChecked = oldCompact;
        var minutes = Named<ComboBox>(settings, "CompactHistoryMinutes");
        int oldMinutes = App.Services.Settings.CompactHistoryMinutes;
        minutes.SelectedItem = 60; pump(0.05);
        Check(App.Services.Hub.CompactMinutes == 60, "actual compact selector chooses 60-minute measured history");
        minutes.SelectedItem = oldMinutes;
        Check(Named<ToggleSwitch>(settings, "RecordDepth").IsChecked == false, "depth recording remains opt-in, not enabled by displaying settings");
        Check(Named<ToggleSwitch>(settings, "CandleMaintenanceEnabled").IsChecked == false, "fresh background maintenance remains off");
        using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_settings_history.png"));

        var services = App.Services;
        var keeper = services.KlineKeeper;
        var originalMaintenance = keeper.Settings;
        var maintenanceProfile = new ConnectionProfile
        {
            Id = "keeper-ui-" + Guid.NewGuid().ToString("N"), Name = "Isolated candle maintenance", Kind = ConnectionKind.Bybit, Enabled = true,
        };
        var maintenanceInstrument = new Instrument
        {
            FeedId = maintenanceProfile.MarketDataId, Symbol = "KVPKEEPER" + Guid.NewGuid().ToString("N")[..8],
            TickSize = 1, VolumeStep = 1, PointValue = 1, Exchange = "Isolated",
        };
        var maintenanceFeed = new KvpCandleSceneFeed(maintenanceInstrument.FeedId, maintenanceInstrument);
        var month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int offset = 0; offset <= 12; offset++)
            maintenanceFeed.Candles.Add(new Kline(new DateTimeOffset(month.AddMonths(-offset)).ToUnixTimeMilliseconds(), 100, 102, 99, 101, 10, 5, true));
        KvpSceneFeed.Register(services, maintenanceProfile, maintenanceFeed);
        var enabledMaintenance = Named<ToggleSwitch>(settings, "CandleMaintenanceEnabled");
        var autoTrack = Named<ToggleSwitch>(settings, "CandleMaintenanceAutoTrack");
        var runMaintenance = Named<Button>(settings, "CandleMaintenanceRun");
        var maintenanceItems = Named<ListBox>(settings, "CandleMaintenanceInstruments");
        string cacheParent = Path.Combine(KlineHistory.CacheRoot ?? throw new InvalidOperationException("The real app must configure its candle cache."),
            maintenanceInstrument.FeedId);
        string cacheBlocker = Path.Combine(cacheParent, maintenanceInstrument.Symbol);
        void RefreshMaintenance() => settings.GetType().GetMethod("RefreshCandleMaintenance",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(settings, null);
        try
        {
            Await(keeper.ConfigureAsync(originalMaintenance with { Enabled = false, AutoTrackOpened = false, Instruments = new() }));
            autoTrack.IsChecked = false;
            Directory.CreateDirectory(cacheParent);
            File.WriteAllText(cacheBlocker, "Owned fixture blocks this instrument cache directory, never another source.");
            enabledMaintenance.IsChecked = true;
            Until(() => keeper.Settings.Enabled, "actual maintenance opt-in admits only the configured fake provider");
            keeper.Track(maintenanceProfile.Id, InstrumentCatalog.IdentityFor(maintenanceProfile), maintenanceInstrument, explicitlySelected: true);
            RefreshMaintenance();
            Click(runMaintenance);
            Until(() => maintenanceFeed.CandleRequests.Count > 0 && !keeper.Status.IsRunning,
                "actual Update now consumer completes the bounded cache-persistence failure cycle");
            var failed = keeper.Status;
            Check(failed.Failures.Any(f => f.InstrumentKey == maintenanceProfile.Id + "|" + maintenanceInstrument.Symbol && f.MonthUtc == month),
                "valid fetched candles with a failed cache write remain an explicit failed monthly maintenance slot");
            Check(maintenanceFeed.CandleRequests.All(request => request.From >= month),
                "failed current-month persistence does not advance the catch-up cursor into older slots");
            RefreshMaintenance();
            maintenanceItems.BringIntoView(); pump(.15);
            Check(maintenanceItems.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                    text.Text?.Contains(maintenanceInstrument.Symbol, StringComparison.Ordinal) == true &&
                    text.Text.Contains(month.ToString("yyyy-MM"), StringComparison.Ordinal)),
                "actual maintenance instrument row renders the source and failed calendar month");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_keeper_cache_failed.png"));
            int failedRequests = maintenanceFeed.CandleRequests.Count;
            File.Delete(cacheBlocker);
            Click(runMaintenance);
            Until(() => maintenanceFeed.CandleRequests.Count > failedRequests && !keeper.Status.IsRunning,
                "actual Update now retries the same source after the owned cache blocker is removed");
            var retryRequests = maintenanceFeed.CandleRequests.Skip(failedRequests).ToArray();
            Check(!keeper.Status.Failures.Any(f => f.InstrumentKey == maintenanceProfile.Id + "|" + maintenanceInstrument.Symbol),
                "successful persisted retry clears only the repaired source's failed slots");
            Check(retryRequests.FirstOrDefault(request => request.From < month) is { Minutes: 1 } older && older.From == month.AddMonths(-1),
                "repaired source resumes at the first uncommitted older 1-minute slot, not a silently advanced cursor");
            Check(Directory.Exists(cacheBlocker) && Directory.EnumerateFiles(cacheBlocker, "*.bin").Any(),
                "recovery creates actual bounded monthly cache files rather than claiming fetched-only success");
            RefreshMaintenance(); pump(.1);
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_keeper_cache_recovered.png"));
            maintenanceEvidence = new
            {
                profile = maintenanceProfile.Id, source = maintenanceInstrument.FeedId, symbol = maintenanceInstrument.Symbol,
                failed.Failures, retryRequests = retryRequests.Select(request => new { request.From, request.To, request.Minutes }),
                recoveredFailures = keeper.Status.Failures,
            };
        }
        finally
        {
            Await(keeper.CancelAsync());
            Await(keeper.ConfigureAsync(originalMaintenance));
            enabledMaintenance.IsChecked = originalMaintenance.Enabled;
            autoTrack.IsChecked = originalMaintenance.AutoTrackOpened;
            services.Connections.Profiles.Remove(maintenanceProfile);
            if (File.Exists(cacheBlocker)) File.Delete(cacheBlocker);
        }

        foreach (string fault in new[] { "none", "corrupt", "permission", "cancel" })
        {
            string root = Path.Combine(AppPaths.Root, "storage-ui-" + fault + "-" + Guid.NewGuid().ToString("N"));
            string original = Path.Combine(root, "original"), scripts = Path.Combine(root, "scripts");
            Directory.CreateDirectory(Path.Combine(original, "Ticks"));
            Directory.CreateDirectory(Path.Combine(scripts, "Estrategias"));
            byte[] raw = [4, 2, 9, 0, 7];
            File.WriteAllBytes(Path.Combine(original, "Ticks", "raw.fixture"), raw);
            File.WriteAllText(Path.Combine(original, "settings.fixture"), "saved settings");
            File.WriteAllText(Path.Combine(scripts, "Estrategias", "SavedStrategy.cs"), "// preserved identity\n");
            using var cancellation = new CancellationTokenSource();
            async Task Copy(string source, string target, CancellationToken token)
            {
                if (fault == "permission") throw new UnauthorizedAccessException("Fixture denied copy permission");
                await using var input = File.OpenRead(source);
                await using var output = File.Create(target);
                await input.CopyToAsync(output, token);
                if (fault == "corrupt") { output.Position = 0; output.WriteByte(255); }
                if (fault == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            }
            var store = new StorageLocationStore(original, scripts, _ => null, Copy);
            var preferences = new StoragePreferences(store);
            Task<StorageRelocationResult>? apply = null;
            var panel = new StorageLocationPanel(preferences, () => apply = store.ApplyPendingAsync(cancellation.Token));
            var fixture = new Window { Title = "Storage relocation — isolated real controls", Width = 1040, Height = 800, Content = panel };
            fixture.Show(window); pump(0.1);
            string targetApp = Path.Combine(root, "new-app"), targetMarket = Path.Combine(root, "new-market"), targetScripts = Path.Combine(root, "new-scripts");
            Named<TextBox>(panel, "StorageAppRoot").Text = targetApp;
            Named<TextBox>(panel, "StorageMarketRoot").Text = targetMarket;
            Named<TextBox>(panel, "StorageScriptsRoot").Text = targetScripts;
            Click(Named<Button>(panel, "StoragePreview")); Await(panel.CurrentOperation);
            Check(Named<Button>(panel, "StorageStage").IsEnabled, fault + ": actual preview hashes the full selected roots");
            Check(store.Resolve().AppRoot == original && !Directory.Exists(targetApp), fault + ": preview never switches or copies live data");
            Click(Named<Button>(panel, "StorageStage")); Await(panel.CurrentOperation);
            Check(store.HasPending && store.Resolve().AppRoot == original, fault + ": approval stages only, original remains authoritative");
            Click(Named<Button>(panel, "StorageRestart"));
            Check(apply != null, fault + ": explicit restart invokes the injected drained-startup adapter");
            if (apply != null)
            {
                Await(apply); panel.RefreshStatus(); pump(0.1);
                var result = apply.GetAwaiter().GetResult();
                Check(result.Success == (fault == "none"), fault + ": copy result is truthful");
                Check(result.Cancelled == (fault == "cancel"), fault + ": cancellation is distinct from success and failure");
                Check(store.Resolve().AppRoot == (fault == "none" ? targetApp : original), fault + ": one pointer changes only after successful verification");
                if (fault == "none")
                {
                    Check(File.ReadAllBytes(Path.Combine(targetMarket, "Ticks", "raw.fixture")).SequenceEqual(raw), "successful copy preserves exact raw bytes in the market root");
                    Check(File.ReadAllText(Path.Combine(targetScripts, "Estrategias", "SavedStrategy.cs")) == "// preserved identity\n", "successful copy preserves strategy filename and content");
                }
            }
            Check(File.ReadAllBytes(Path.Combine(original, "Ticks", "raw.fixture")).SequenceEqual(raw), fault + ": original raw history remains byte-identical");
            Check(File.ReadAllText(Path.Combine(original, "settings.fixture")) == "saved settings", fault + ": original application state survives");
            using (var frame = fixture.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_storage_" + fault + ".png"));
            fixture.Close();
        }
        File.WriteAllText(Path.Combine(outDir, "kvp-operations-results.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures, maintenanceEvidence }));
        return failures;
    }
}
