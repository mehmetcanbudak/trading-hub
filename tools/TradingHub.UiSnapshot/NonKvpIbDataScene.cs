using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Charting;
using TradingHub.Market;
using TradingHub.Tests;

internal static class NonKvpIbDataScene
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0;
        void Check(bool condition, string description) { Console.WriteLine($"{(condition ? "OK" : "FAIL")} IB: {description}"); if (!condition) failures++; }
        void Wait(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < until) pump(0.02);
            if (!task.IsCompleted) throw new TimeoutException("IB fixture operation timed out.");
            task.GetAwaiter().GetResult();
        }
        void Shot(Window target, string name)
        {
            pump(0.2);
            using var frame = target.CaptureRenderedFrame();
            Check(frame != null, name + " renders"); frame?.Save(Path.Combine(outDir, name + ".png"));
        }
        Window? Dialog(string title, Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < until)
            {
                pump(0.02);
                if (window.OwnedWindows.FirstOrDefault(w => w.Title == title) is { } dialog) return dialog;
            }
            return null;
        }
        Check(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } && Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE") is { Length: > 0 }, "isolated stores and fake secrets are configured");
        if (failures != 0) return failures;
        var gateway = new FakeIbGateway();
        var profile = new ConnectionProfile { Id = "nonkvp-ib-fixture", Kind = ConnectionKind.InteractiveBrokers, Name = "IB DATA ONLY fixture", IbOffline = true, IbPort = gateway.Port, IbClientId = 47 };
        var connections = App.Services.Connections;
        connections.Profiles.Add(profile); connections.Save();
        try
        {
            window.Navigate("connections"); pump(0.3);
            var connectionView = window.Page("connections")!;
            Call(connectionView, "Select", profile); pump(0.3);
            Check(!profile.SupportsTrading && profile.HasMarketData && profile.MarketDataId == "ib", "market data enabled without IB trading capability or changed storage identity");
            var offline = connections.TestAsync(profile, default); Wait(offline);
            Check(offline.Result.Ok && offline.Result.Message.Contains("offline"), "offline test does not contact Gateway");
            Check(connectionView.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("DATA ONLY") == true), "Connections displays actual data-only scope and entitlement limitations");
            Shot(window, "nonkvp_ib_offline_connections");
            var oldFeed = connections.GetFeed(profile.Id);
            profile.IbOffline = false; connections.Invalidate(profile); connections.Save();
            Call(connectionView, "Select", profile);
            var online = connections.TestAsync(profile, default); Wait(online);
            Check(online.Result.Ok && online.Result.Message.Contains("START_API"), "online Connections test performs real framed handshake against fake Gateway");
            var accepted = gateway.NextSessionAsync(); Wait(accepted);
            Check(accepted.Result.ClientId == 47, "configured unique client ID reaches START_API");
            Check(ReferenceEquals(oldFeed, connections.GetFeed(profile.Id)), "the retained Hub feed stays stable while transport and offline mode are reconfigured");
            Shot(window, "nonkvp_ib_online_fake_connections");
            accepted.Result.Close();
            profile.IbOffline = true; connections.Invalidate(profile); connections.Save();

            string folder = Path.Combine(outDir, "ib-csv-fixture"); Directory.CreateDirectory(folder);
            var time = new DateTime(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc);
            long unix = new DateTimeOffset(time).ToUnixTimeSeconds();
            File.WriteAllText(Path.Combine(folder, "MNQ1m_2026-10-30.csv"), $"time,open,high,low,close,volume,contract\n{unix},100,101,99,100,12,MNQZ6\n{unix + 60},101,102,100,101,8,MNQZ6\n");
            var instrument = IbBarImport.InstrumentFor("MNQ");
            var day = DateOnly.FromDateTime(time);
            HistoricalData.WriteDay(AppPaths.Ticks, instrument, day, new[] { new Trade(1000, time.AddSeconds(20), 100, 3, TradeSide.Buy) }, true);
            string originalPath = TickFile.DayPath(AppPaths.Ticks, "ib", "MNQ", day); byte[] original = File.ReadAllBytes(originalPath);
            window.Navigate("data"); pump(0.3);
            var data = window.Page("data")!;
            Field<TextBox>(data, "_ibFolder").Text = folder;
            Field<TextBox>(data, "_ibSymbol").Text = "MNQ";
            Field<CheckBox>(data, "_ibReplace").IsChecked = true;
            var canceled = (Task)Call(data, "ImportIbAsync")!;
            var cancelDialog = Dialog("Replace IB recorded minutes", canceled);
            Check(cancelDialog != null, "actual Data import validates then opens conflict preview");
            if (cancelDialog != null)
            {
                Shot(cancelDialog, "nonkvp_ib_conflict_preview_cancel");
                cancelDialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            }
            Wait(canceled);
            Check(original.SequenceEqual(File.ReadAllBytes(originalPath)), "Cancel-default Enter preserves recorded bytes");
            var replace = (Task)Call(data, "ImportIbAsync")!;
            var replaceDialog = Dialog("Replace IB recorded minutes", replace);
            Check(replaceDialog != null, "explicit replacement opens its own Cancel-default confirmation");
            if (replaceDialog != null)
            {
                var confirm = replaceDialog.GetVisualDescendants().OfType<Button>().First(b => b.Content is string text && text == "Replace with backups");
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Wait(replace);
            string outcome = Field<TextBlock>(data, "_ibOutcome").Text ?? "";
            Check(outcome.Contains("Backups and journal"), "real import consumer reports preserved backup location: " + outcome);
            Check(Directory.EnumerateFiles(Path.Combine(AppPaths.Ticks, ".ib-import-backups"), "*.original", SearchOption.AllDirectories).Any(p => File.ReadAllBytes(p).SequenceEqual(original)), "replacement backup preserves the exact original tick file");
            Shot(window, "nonkvp_ib_import_outcome");
            var read = HistoricalData.Read(AppPaths.Ticks, "ib", "MNQ", time, time.AddMinutes(2)).ToList();
            Check(read.Count == 8 && read.All(t => t.Id < 0) && read.SequenceEqual(read.OrderBy(t => t.Time)), "two UTC days compose chronologically without positive synthetic IDs");
            var feed = (IbFeed)connections.GetFeed(profile.Id);
            Check(feed.GetSessionSettings(instrument).SessionStartUtc(new(2026, 12, 1)).Hour == 23 && feed.GetSessionSettings(instrument).SessionStartUtc(new(2026, 7, 1)).Hour == 22, "offline chart session uses New York 18:00 with DST");
            using var chartData = new InstrumentData(instrument, feed);
            Check(App.Services.AccountsFor(chartData).Count() == 1, "IB data exposes only the existing simulated account, never IB or cross-provider real order routing");
            foreach (var trade in read) chartData.Trades.Add(trade);
            using (var chart = new ChartModel(chartData, ChartPeriod.D1, new ChartSettings()))
            {
                chart.Update();
                Check(chart.Series.Count == 1 && chart.Series[0].Time == new DateTime(2026, 10, 30, 22, 0, 0, DateTimeKind.Utc),
                    "actual daily ChartModel combines UTC-midnight bars into the NY18 session rather than UTC days");
                var chartWindow = new Window { Title = "IB offline synthetic history — NY18 session", Width = 1000, Height = 650,
                    Content = new TradingHub.App.Controls.ChartView { Model = chart } };
                chartWindow.Show(window); pump(0.3);
                Shot(chartWindow, "nonkvp_ib_offline_chart_session");
                chartWindow.Close();
            }
            window.Navigate("strategies"); pump(0.5);
            var strategies = window.Page("strategies")!;
            Call(strategies, "RefreshDataSources");
            var sources = Field<ComboBox>(strategies, "_dataSource");
            sources.SelectedItem = sources.Items.OfType<ComboBoxItem>().First(i => i.Tag is SymbolData { FeedId: "ib", Symbol: "MNQ" });
            Call(strategies, "OnDataSourceChanged"); pump(0.2);
            string warning = Field<TextBlock>(strategies, "_dataInfo").Text ?? "";
            Check(warning.Contains("synthetic") && warning.Contains("18:00") && MarketInfo.Costs("ib", "MNQ").TimeZoneId == "America/New_York", "actual offline backtest source displays synthetic warning and session metadata");
            Shot(window, "nonkvp_ib_offline_backtest");
            failures += NonKvpIbCorrectionsScene.Run(window, pump, outDir, profile, instrument, gateway);
        }
        catch (Exception error) { Check(false, error.GetBaseException().ToString()); }
        finally
        {
            connections.Profiles.Remove(profile); connections.Save();
            var dispose = gateway.DisposeAsync().AsTask(); Wait(dispose);
        }
        return failures;
    }
}
