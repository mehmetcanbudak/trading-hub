using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Market;
using TradingHub.Networking;
using TradingHub.Tests;
using TradingHub.Trading;

internal static class KvpChartAccountsScene
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Fields)!.GetValue(value)!;
    private static void Key(Control control, Key key) => control.RaiseEvent(new KeyEventArgs
        { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = control });

    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        int checks = 0, failures = 0;
        var evidence = new List<object>();
        void Check(bool condition, string description)
        {
            checks++;
            if (!condition) failures++;
            evidence.Add(new { condition, description });
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} KVP chart accounts: {description}");
        }
        void Until(Func<bool> condition, string description)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(30);
            while (!condition() && DateTime.UtcNow < until) pump(0.02);
            Check(condition(), description);
        }
        T Await<T>(Task<T> task, string description)
        {
            Until(() => task.IsCompleted, description);
            return task.GetAwaiter().GetResult();
        }
        void Capture(string name)
        {
            pump(0.15);
            using var image = owner.CaptureRenderedFrame();
            Check(image != null, "actual account availability UI renders " + name);
            image?.Save(Path.Combine(outDir, name + ".png"));
        }

        var services = App.Services;
        var trading = owner.Trading;
        var original = trading.ActiveTab;
        var before = trading.AllCharts.ToHashSet();
        byte[]? workspaceBefore = File.Exists(AppPaths.WorkspaceFile) ? File.ReadAllBytes(AppPaths.WorkspaceFile) : null;
        string suffix = Guid.NewGuid().ToString("N");
        var firstProfile = new ConnectionProfile { Id = "chart-account-" + suffix, Name = "Isolated executable fixture", Kind = ConnectionKind.BinanceFutures, ApiKey = "fixture-chart-key", Testnet = true, HistoryHours = 2 };
        var otherProfile = new ConnectionProfile { Id = "chart-other-" + suffix, Name = "Other isolated data provider", Kind = ConnectionKind.Oanda, HistoryHours = 2 };
        var aggregateProfile = new ConnectionProfile { Id = "chart-composite-" + suffix, Name = "Isolated composite data only", Kind = ConnectionKind.AggregateCrypto, HistoryHours = 2 };
        var firstInstrument = new Instrument { FeedId = BinanceFeed.FuturesId, Symbol = "LOCAL", Description = "Isolated first venue", TickSize = 1, VolumeStep = 1, PointValue = 1 };
        var otherInstrument = new Instrument { FeedId = "oanda", Symbol = "LOCAL", Description = "Isolated second provider", TickSize = 1, VolumeStep = 1, PointValue = 1 };
        var firstFeed = new KvpSceneFeed(firstProfile.Id, firstInstrument) { Now = DateTime.UtcNow };
        var otherFeed = new KvpSceneFeed(otherProfile.Id, otherInstrument) { Now = firstFeed.Now };
        for (int i = 0; i < 90; i++)
        {
            var trade = new Trade(i + 1, firstFeed.Now.AddMinutes(i - 90), 100 + i % 5, 1, TradeSide.Buy);
            firstFeed.Trades.Add(trade);
            otherFeed.Trades.Add(trade);
        }
        var aggregate = new AggregateFeed([firstFeed, otherFeed], dataScopeKey: aggregateProfile.Id);
        KvpSceneFeed.Register(services, firstProfile, firstFeed);
        KvpSceneFeed.Register(services, otherProfile, otherFeed);
        KvpSceneFeed.Register(services, aggregateProfile, aggregate);
        using var server = new FakeHttpServer();
        server.OnHttp = request => request.Path.Split('?')[0] switch
        {
            "/fapi/v1/time" => (200, "{\"serverTime\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}"),
            "/fapi/v1/openOrders" or "/fapi/v1/openAlgoOrders" or "/fapi/v2/positionRisk" => (200, "[]"),
            _ => (400, "{\"code\":-1,\"msg\":\"Unexpected fixture request\"}"),
        };
        BinanceBroker? broker = null;
        string? brokerKey = null;
        var cache = Field<Dictionary<string, ITradingAccount>>(services, "_realBrokers");
        try
        {
            var chart = Await(trading.NewTabAsync(firstProfile.Id, "LOCAL", ChartPeriod.M1), "actual chart opens on first fake provider");
            Until(() => chart.Model is { Series.Count: > 0 }, "actual raw history reaches first chart");
            broker = new BinanceBroker(chart.Model!.Data,
                new BinanceAccountSettings { ApiKey = firstProfile.ApiKey, ApiSecret = "fixture-chart-secret", Testnet = true, BaseUrl = server.Http },
                "Isolated testnet account", network: new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Direct }));
            brokerKey = firstProfile.Id + "|" + firstInstrument.Key;
            cache.Add(brokerKey, broker);
            Until(() => broker.PositionReady && broker.OrdersReady, "actual Binance account consumes confirmed fake snapshots");
            owner.Navigate("trading");
            trading.SetPanel("side", true);
            trading.SetPanel("ticket", true);
            pump(0.2);

            void ChooseAccount(string id)
            {
                var selector = owner.GetVisualDescendants().OfType<ComboBox>().First(box => box.IsEffectivelyVisible &&
                    box.Items.OfType<ComboBoxItem>().Any(item => item.Tag is AccountOption option && option.Id == id));
                selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(item => item.Tag is AccountOption option && option.Id == id);
                pump(0.15);
            }
            void NavigateByKeyboard(string profileId)
            {
                trading.Activate(chart);
                chart.View.Focus();
                chart.View.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Source = chart.View, Text = "LOCAL" });
                Until(() => typeof(TradingView).GetField("_chartEntry", Fields)!.GetValue(trading) != null, "committed text opens actual connection-aware quick entry");
                var session = typeof(TradingView).GetField("_chartEntry", Fields)!.GetValue(trading)!;
                var connection = Field<ComboBox>(session, "Connection");
                connection.SelectedItem = connection.Items.OfType<ComboBoxItem>().Single(item => Equals(item.DataContext, profileId));
                var results = Field<ListBox>(session, "Results");
                Until(() => results.ItemCount > 0 && results.SelectedIndex >= 0, "new source has a visibly selected exact result");
                Key(Field<TextBox>(session, "Input"), Avalonia.Input.Key.Enter);
                Until(() => chart.FeedId == profileId && chart.Model != null, "explicit selected row opens requested fake source");
            }
            void AssertUnavailable(string description)
            {
                Check(chart.AccountId == firstProfile.Id && chart.AccountSelectionUnavailable, description + ": original account identity remains selected and unavailable");
                Check(trading.AccountOf(chart) == null && chart.Model!.Account == null && chart.Model.Trading == null && chart.Model.Brackets == null,
                    description + ": no cached, cross or simulated route can be inferred");
                var order = trading.TradeAsync(chart, OrderTypes.Market, OrderDirections.Buy, 0, 1);
                Check(Await(order, "unavailable account action finishes without a route") == null, description + ": normal trading consumer sends no order");
                trading.ExecuteHotkey("buy-market", chart);
                pump(0.05);
                Check(broker.Position.Volume == 0 && !broker.ActiveOrders.Any(), description + ": existing order hotkey does not route or create exposure");
            }

            ChooseAccount(firstProfile.Id);
            Check(chart.AccountId == firstProfile.Id && ReferenceEquals(trading.AccountOf(chart), broker), "actual visible account picker selects the distinct confirmed fake account");
            NavigateByKeyboard(otherProfile.Id);
            AssertUnavailable("different individual provider");
            Capture("kvp_chart_other_provider_account_unavailable");
            NavigateByKeyboard(aggregateProfile.Id);
            AssertUnavailable("composite data-only provider");
            Check(!services.AccountsFor(chart.Model!.Data).Any(), "composite has no executable account, including ordinary simulation");
            Capture("kvp_chart_composite_account_unavailable");

            Workspace.Save(trading);
            var priorRestore = trading.AllCharts.ToHashSet();
            Check(Await(Workspace.RestoreAsync(trading), "actual saved workspace restores through normal consumer"), "saved unavailable-account workspace is restorable");
            var restored = trading.AllCharts.Except(priorRestore).Single(tab => tab.FeedId == aggregateProfile.Id);
            Check(restored.AccountId == firstProfile.Id && restored.AccountSelectionUnavailable && trading.AccountOf(restored) == null && restored.Model?.Trading == null,
                "actual workspace round trip retains original unavailable identity without fallback routing");
            foreach (var added in trading.AllCharts.Except(priorRestore).ToArray()) trading.CloseChart(added);
            trading.Activate(chart);
            NavigateByKeyboard(otherProfile.Id);
            AssertUnavailable("returning to incompatible individual data");
            ChooseAccount("sim");
            Check(chart.AccountId == "sim" && !chart.AccountSelectionUnavailable && trading.AccountOf(chart) is { IsReal: false },
                "only an explicit compatible account choice restores an individual data chart route");
            Capture("kvp_chart_explicit_account_recovery");
            lock (server.Requests)
                Check(server.Requests.All(request => request.Method == "GET"), "all actual fake broker traffic is snapshot-only; navigation sends no order HTTP");
        }
        finally
        {
            trading.CancelChartQuickEntry();
            foreach (var added in trading.AllCharts.Where(tab => !before.Contains(tab)).ToArray()) trading.CloseChart(added);
            if (original != null) trading.Activate(original);
            if (brokerKey != null) cache.Remove(brokerKey);
            broker?.Dispose();
            services.Connections.Profiles.Remove(firstProfile);
            services.Connections.Profiles.Remove(otherProfile);
            services.Connections.Profiles.Remove(aggregateProfile);
            if (workspaceBefore == null) File.Delete(AppPaths.WorkspaceFile);
            else File.WriteAllBytes(AppPaths.WorkspaceFile, workspaceBefore);
            File.WriteAllText(Path.Combine(outDir, "kvp-chart-account-results.json"), JsonSerializer.Serialize(new { checks, failures, evidence }));
        }
        return failures;
    }
}
