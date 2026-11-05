using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds.Alpaca;
using TradingHub.Networking;
using TradingHub.Tests;

internal static class KvpProxyScene
{
    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        int checks = 0, failures = 0;
        var evidence = new List<object>();
        void Check(bool condition, string description)
        {
            checks++; if (!condition) failures++;
            evidence.Add(new { condition, description });
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} KVP proxy: {description}");
        }
        void Until(Func<bool> condition, string description)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(30);
            while (!condition() && DateTime.UtcNow < until) pump(0.02);
            Check(condition(), description);
            if (!condition()) throw new TimeoutException(description);
        }
        T Await<T>(Task<T> task, string description)
        {
            Until(() => task.IsCompleted, description);
            return task.GetAwaiter().GetResult();
        }
        static T Named<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using var origin = new FakeHttpServer();
        using var firstProxy = new FakeProxyServer();
        using var secondProxy = new FakeProxyServer();
        int socketGeneration = 0;
        origin.OnHttp = request =>
        {
            if (request.Headers.GetValueOrDefault("APCA-API-KEY-ID") != "KEY" || request.Headers.GetValueOrDefault("APCA-API-SECRET-KEY") != "SECRET")
                return (403, "{\"message\":\"Fixture authentication rejected\"}");
            string time = DateTime.UtcNow.AddMinutes(-2).ToString("O", CultureInfo.InvariantCulture);
            return request.Route switch
            {
                "/v2/assets" => (200, "[{\"symbol\":\"SPY\",\"name\":\"Isolated SPY quote fixture\",\"exchange\":\"ARCA\",\"tradable\":true}]"),
                "/v2/stocks/SPY/trades" => (200, "{\"trades\":[{\"t\":\"" + time + "\",\"x\":\"P\",\"p\":580.00,\"s\":2,\"i\":1}],\"symbol\":\"SPY\",\"next_page_token\":null}"),
                "/v2/stocks/SPY/bars" => (200, "{\"bars\":[],\"symbol\":\"SPY\",\"next_page_token\":null}"),
                _ => (404, "{}"),
            };
        };
        origin.OnSocket = async (_, socket, ct) =>
        {
            int generation = Interlocked.Increment(ref socketGeneration);
            await FakeHttpServer.SendText(socket, "[{\"T\":\"success\",\"msg\":\"connected\"}]", ct);
            while (socket.State == WebSocketState.Open)
            {
                string? message = await FakeHttpServer.ReceiveText(socket, ct);
                if (message == null) return;
                using var document = JsonDocument.Parse(message);
                string? action = document.RootElement.GetProperty("action").GetString();
                if (action == "auth")
                {
                    bool valid = document.RootElement.GetProperty("key").GetString() == "KEY" && document.RootElement.GetProperty("secret").GetString() == "SECRET";
                    await FakeHttpServer.SendText(socket, valid ? "[{\"T\":\"success\",\"msg\":\"authenticated\"}]" : "[{\"T\":\"error\",\"code\":402,\"msg\":\"auth failed\"}]", ct);
                }
                else if (action == "subscribe")
                {
                    string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    await FakeHttpServer.SendText(socket, "[{\"T\":\"subscription\",\"trades\":[\"SPY\"],\"quotes\":[\"SPY\"]}]", ct);
                    await FakeHttpServer.SendText(socket, "[{\"T\":\"q\",\"S\":\"SPY\",\"bx\":\"V\",\"bp\":580.1,\"bs\":3,\"ax\":\"V\",\"ap\":580.12,\"as\":2,\"t\":\"" + now + "\"}," +
                        "{\"T\":\"t\",\"S\":\"SPY\",\"i\":" + (generation * 10 + 1) + ",\"x\":\"V\",\"p\":580.12,\"s\":100,\"t\":\"" + now + "\",\"z\":\"B\"}]", ct);
                }
            }
        };
        var services = App.Services;
        var oldPolicy = services.NetworkProxy.Policy;
        const string credentialKey = "network-proxy:credentials";
        string oldCredentials = Keychain.Get(credentialKey);
        var original = owner.Trading.ActiveTab;
        var before = owner.Trading.AllCharts.ToHashSet();
        string id = "proxy-ui-" + Guid.NewGuid().ToString("N");
        var profile = new ConnectionProfile { Id = id, Kind = ConnectionKind.Alpaca, Name = "Proxy-routed isolated Alpaca", HistoryHours = 1 };
        AlpacaFeed? feed = null;
        try
        {
            owner.Navigate("settings"); pump(0.15);
            var settings = owner.Page("settings")!;
            void Apply(NetworkProxyPolicy policy, bool credentials = false)
            {
                var mode = Named<ComboBox>(settings, "NetworkProxyMode");
                mode.BringIntoView();
                mode.SelectedItem = mode.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, policy.Mode));
                Named<TextBox>(settings, "NetworkProxyAddress").Text = policy.Address;
                Named<CheckBox>(settings, "NetworkProxyReplaceCredentials").IsChecked = credentials;
                Named<TextBox>(settings, "NetworkProxyUsername").Text = credentials ? "fixture-proxy-user" : "";
                Named<TextBox>(settings, "NetworkProxyPassword").Text = credentials ? "fixture-proxy-secret" : "";
                Click(Named<Button>(settings, "NetworkProxySave")); pump(0.1);
                Check(services.NetworkProxy.Policy.Mode == policy.Mode && services.NetworkProxy.Policy.Address == policy.Address,
                    "actual settings controls persist the selected explicit proxy policy");
            }
            Apply(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = firstProxy.Address }, credentials: true);
            string ordinarySettings = File.ReadAllText(Path.Combine(AppPaths.Root, "settings.json"));
            Check(!ordinarySettings.Contains("fixture-proxy-user", StringComparison.Ordinal) && !ordinarySettings.Contains("fixture-proxy-secret", StringComparison.Ordinal),
                "proxy authentication is absent from ordinary settings");
            using (var frame = owner.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_proxy_settings.png"));
            var captured = services.NetworkProxy.CreateFactory();
            var source = new AlpacaSettings { KeyId = "KEY", Secret = "SECRET", TradingUrl = origin.Http, DataUrl = origin.Http, StreamUrl = origin.Ws + "/v2/iex" };
            feed = new AlpacaFeed(source, network: captured);
            KvpSceneFeed.Register(services, profile, feed);
            var chart = Await(owner.Trading.NewTabAsync(id, "SPY", ChartPeriod.M1), "actual chart opens through proxy-routed provider discovery");
            owner.Navigate("trading");
            Until(() => chart.Model!.Data.Trades.Count > 0 && chart.Model.Data.Depth.BestBidSize == 3 && chart.Model.Data.LastPrice == 580.12m,
                "real HTTP history and authenticated WebSocket print/book reach actual chart consumers");
            var actualTrades = chart.Model!.Data.Trades;
            bool decodedPrint = false;
            for (int i = 0; i < actualTrades.Count; i++)
                if (actualTrades.GetPrice(i) == 580.12m && actualTrades.GetVolume(i) == 100) { decodedPrint = true; break; }
            Check(chart.Model.Series.Count > 0 && decodedPrint,
                "actual decoded fake provider print reaches chart candles rather than a factory property check");
            Check(firstProxy.Requests.Any(request => request.Target.AbsolutePath == "/v2/assets") &&
                firstProxy.Requests.Any(request => request.Target.AbsolutePath == "/v2/stocks/SPY/trades") &&
                firstProxy.Requests.Any(IsSocket), "actual discovery/history HTTP and WebSocket handshake cross the selected fake proxy");
            using (var frame = owner.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_proxy_actual_market_consumers.png"));
            owner.Navigate("settings"); pump(0.1);
            Apply(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = secondProxy.Address });
            using (var next = new AlpacaFeed(source, network: services.NetworkProxy.CreateFactory()))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                Check(Await(next.GetInstrumentsAsync(deadline.Token), "new consumer discovers instruments over new captured policy").Any(instrument => instrument.Symbol == "SPY"),
                    "a newly created real provider consumer uses the changed proxy");
            int socketHops = firstProxy.Requests.Count(IsSocket);
            chart.Model.Data.Reconnect();
            Until(() => firstProxy.Requests.Count(IsSocket) > socketHops && socketGeneration >= 2,
                "active source reconnect retains its original proxy rather than mutating its transport context");
            Check(secondProxy.Requests.Any(request => request.Target.AbsolutePath == "/v2/assets") && !secondProxy.Requests.Any(IsSocket),
                "new HTTP context and held original WebSocket context remain separate");

            using var stopped = new FakeProxyServer();
            string stoppedAddress = stopped.Address;
            stopped.Dispose();
            Apply(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = stoppedAddress });
            int originBefore;
            lock (origin.Requests) originBefore = origin.Requests.Count(request => request.Route == "/v2/assets");
            using (var denied = new AlpacaFeed(source, network: services.NetworkProxy.CreateFactory()))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                var deniedRequest = denied.GetInstrumentsAsync(deadline.Token);
                Until(() => deniedRequest.IsCompleted, "failed explicit proxy finishes the real provider request without direct fallback");
                bool failed = false;
                try { deniedRequest.GetAwaiter().GetResult(); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { failed = true; }
                Check(failed, "unavailable explicit proxy is surfaced as failure rather than a successful direct request");
            }
            lock (origin.Requests)
                Check(origin.Requests.Count(request => request.Route == "/v2/assets") == originBefore, "stopped proxy never falls back to the reachable loopback origin");
            Check(firstProxy.Errors.IsEmpty && secondProxy.Errors.IsEmpty, "finite fake proxies forwarded only literal-loopback endpoints");
        }
        finally
        {
            foreach (var chart in owner.Trading.AllCharts.Where(chart => !before.Contains(chart)).ToArray()) owner.Trading.CloseChart(chart);
            if (original != null) owner.Trading.Activate(original);
            services.Connections.Profiles.Remove(profile);
            feed?.Dispose();
            Keychain.Set(credentialKey, oldCredentials);
            services.NetworkProxy.Save(oldPolicy, null, null, replaceCredentials: false);
            File.WriteAllText(Path.Combine(outDir, "kvp-proxy-results.json"), JsonSerializer.Serialize(new
            {
                checks, failures, evidence,
                firstProxy = firstProxy.Requests.Select(request => new { request.Method, origin = request.Target.GetLeftPart(UriPartial.Authority), path = request.Target.AbsolutePath }).ToArray(),
                secondProxy = secondProxy.Requests.Select(request => new { request.Method, origin = request.Target.GetLeftPart(UriPartial.Authority), path = request.Target.AbsolutePath }).ToArray(),
            }));
        }
        return failures;
    }
    private static bool IsSocket(FakeProxyRequest request) => request.Method == "CONNECT" ||
        request.Headers.TryGetValue("Upgrade", out var upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase);
}
