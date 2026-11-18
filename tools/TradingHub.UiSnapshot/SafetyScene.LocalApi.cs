using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;

internal static partial class SafetyScene
{
    /// <summary>
    /// Quit with the opt-in local market-data API on: it answers only loopback/same-host requests while the app runs,
    /// exposes no credentials, and the owned shutdown drain (the one the terminal quit awaits) closes the listener and
    /// its live stream, after which nothing can restart it in this process.
    /// </summary>
    private static void LocalApiQuit(AppServices svc, Action<double> pump, Action<bool, string> check, string connection, string symbol)
    {
        bool enabled = svc.Settings.LocalApiEnabled;
        int previousPort = svc.Settings.LocalApiPort;
        T Await<T>(Task<T> task, double seconds = 15)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted && DateTime.UtcNow < until) pump(0.02);
            return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException("The local API request did not complete.");
        }
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        try
        {
            check(!enabled && svc.LocalApi == null, "the local market-data API is off by default");
            LocalApiSettings(svc, pump, check);
            svc.Settings.LocalApiEnabled = true;
            svc.Settings.LocalApiPort = port;
            svc.ApplyLocalApi();
            check(svc.LocalApi is { IsRunning: true, Endpoint: { } endpoint } && endpoint.Address.Equals(IPAddress.Loopback),
                $"turning it on listens on 127.0.0.1 only ({svc.LocalApi?.Endpoint})");
            check(Await(RawAsync(port, "GET", "/api/v1/info", $"127.0.0.1:{port}")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "a same-host request is answered");
            check(Await(RawAsync(port, "GET", "/api/v1/info", "attacker.example")).Contains(" 421 ", StringComparison.Ordinal), "a rebinding Host header is refused");
            check(Await(RawAsync(port, "GET", "/api/v1/info", $"localhost:{port}", "https://attacker.example")).Contains(" 403 ", StringComparison.Ordinal), "a cross-origin browser request is refused");
            check(Await(RawAsync(port, "POST", "/api/v1/info", $"127.0.0.1:{port}")).Contains(" 405 ", StringComparison.Ordinal), "the API is read-only");
            string connections = Await(RawAsync(port, "GET", "/api/v1/connections", $"127.0.0.1:{port}"));
            var secrets = svc.Connections.Profiles.Select(p => p.ApiKey).Where(k => k.Length >= 6).ToArray();
            check(connections.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && secrets.All(s => !connections.Contains(s, StringComparison.Ordinal))
                  && !connections.Contains("apiKey", StringComparison.OrdinalIgnoreCase), "connections expose no keys or credentials");
            string notOpen = Await(RawAsync(port, "GET", "/api/v1/klines?conn=binance-futures&symbol=NOTOPENUSDT&tf=1m", $"127.0.0.1:{port}"));
            check(notOpen.Contains(" 404 ", StringComparison.Ordinal) && notOpen.Contains("\"not_open\"", StringComparison.Ordinal),
                "candles of an instrument that is not open are refused: the API opens no feed and requests no history");
            string candles = Await(RawAsync(port, "GET", $"/api/v1/klines?conn={Uri.EscapeDataString(connection)}&symbol={Uri.EscapeDataString(symbol)}&tf=1m", $"127.0.0.1:{port}"));
            check(candles.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "candles of an open instrument come from its loaded trades");

            using var stream = new ClientWebSocket();
            var connect = stream.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/v1/stream?conn={Uri.EscapeDataString(connection)}&symbol={Uri.EscapeDataString(symbol)}"), CancellationToken.None);
            Await(connect.ContinueWith(t => t.IsCompletedSuccessfully, TaskScheduler.Default));
            check(stream.State == WebSocketState.Open, "a live stream of an open instrument is connected before quitting");
            var receive = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                try
                {
                    while (true)
                    {
                        var r = await stream.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) return true;
                    }
                }
                catch (WebSocketException) { return true; }
            });

            var drain = svc.ShutdownLocalApiAsync();
            Await(drain.ContinueWith(t => t.IsCompletedSuccessfully, TaskScheduler.Default), 20);
            check(drain.IsCompletedSuccessfully, "the owned shutdown drain of the API completes");
            check(Await(receive, 10), "the live stream is closed by the drain");
            check(Await(RefusedAsync(port)), "after the drain the port no longer accepts connections");
            svc.ApplyLocalApi();
            check(svc.LocalApi == null && svc.LocalApiStatus == "Off", "after shutdown the API cannot be restarted by settings");
        }
        finally
        {
            svc.Settings.LocalApiEnabled = enabled;
            svc.Settings.LocalApiPort = previousPort;
        }
    }

    /// <summary>
    /// Settings → Local market-data API through its real controls: on, off and on again; a port change; a port another
    /// listener holds (then freed); and origin validation listing every invalid entry. Leaves the API off.
    /// </summary>
    private static void LocalApiSettings(AppServices svc, Action<double> pump, Action<bool, string> check)
    {
        var saved = (Enabled: svc.Settings.LocalApiEnabled, Port: svc.Settings.LocalApiPort, Origins: svc.Settings.LocalApiAllowedOrigins.ToList());
        var host = new Window { Width = 1000, Height = 800, Content = new SettingsView(svc, _ => { }) };
        host.Show();
        pump(0.3);
        T Find<T>(string name) where T : Control => host.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        bool Await(Task<bool> task)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < until) pump(0.02);
            return task.IsCompletedSuccessfully && task.Result;
        }
        bool Refused(int port) => Await(RefusedAsync(port));
        bool Accepts(int port) => Await(RefusedAsync(port).ContinueWith(t => t.IsCompletedSuccessfully && !t.Result, TaskScheduler.Default));
        static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
        string Status() => Find<TextBlock>("LocalApiStatus").Text ?? "";
        string Listening(int port) => Lang.T($"Listening on http://127.0.0.1:{port}/api/v1");
        bool Running(int port) => svc.LocalApi is { IsRunning: true, Endpoint: { } endpoint } && endpoint.Port == port && Status() == Listening(port) && Accepts(port);
        try
        {
            var enabled = Find<ToggleSwitch>("LocalApiEnabled");
            var portBox = Find<NumericUpDown>("LocalApiPort");
            var origins = Find<TextBox>("LocalApiAllowedOrigins");
            var invalid = Find<TextBlock>("LocalApiInvalidOrigins");
            int first = FreePort();
            portBox.Value = first;
            check(svc.Settings.LocalApiPort == first && svc.LocalApi == null && Status() == Lang.T("Off") && Refused(first),
                "Settings: changing the port while the API is off saves it and listens on nothing");
            enabled.IsChecked = true;
            check(Running(first) && Find<TextBlock>("LocalApiAddress").Text == Lang.T($"Base URL: http://127.0.0.1:{first}/api/v1"),
                "Settings: switching the API on listens on the loopback port and says so: " + Status());
            enabled.IsChecked = false;
            check(svc.LocalApi == null && Status() == Lang.T("Off") && Refused(first), "Settings: switching it off closes the listener");
            enabled.IsChecked = true;
            check(Running(first), "Settings: switching it on again listens again");

            int second = FreePort();
            portBox.Value = second;
            check(Running(second) && Refused(first), "Settings: a port change restarts on the new port and the old port refuses connections");

            var holder = new TcpListener(IPAddress.Loopback, 0);
            holder.Start();
            int held = ((IPEndPoint)holder.LocalEndpoint).Port;
            try
            {
                portBox.Value = held;
                check(svc.LocalApi == null && Status() == Lang.T($"Port {held} is already in use.") && Refused(second),
                    "Settings: a port another program listens on is reported and nothing runs: " + Status());
            }
            finally { holder.Stop(); }
            enabled.IsChecked = false;
            enabled.IsChecked = true;
            check(Running(held), "Settings: once that port is free, switching off and on again starts the API on it");

            origins.Text = "http://localhost:3000/\nnot-an-origin\nftp://example.com";
            origins.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            check(invalid.IsVisible && invalid.Text == Lang.T("Ignored invalid origins: not-an-origin, ftp://example.com")
                  && svc.Settings.LocalApiAllowedOrigins.SequenceEqual(new[] { "http://localhost:3000" }) && Running(held),
                "Settings: invalid origins are listed and ignored while the valid one is kept: " + invalid.Text);
            origins.Text = "http://localhost:3000";
            origins.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            check(!invalid.IsVisible, "Settings: the invalid-origin note disappears once every origin is valid");
            enabled.IsChecked = false;
            check(svc.LocalApi == null && Refused(held), "Settings: the API is left off");
        }
        finally
        {
            host.Close();
            svc.Settings.LocalApiEnabled = saved.Enabled;
            svc.Settings.LocalApiPort = saved.Port;
            svc.Settings.LocalApiAllowedOrigins = saved.Origins;
            svc.Settings.Save();
            svc.ApplyLocalApi();
        }
    }

    private static async Task<string> RawAsync(int port, string method, string path, string host, string? origin = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var net = client.GetStream();
        string request = $"{method} {path} HTTP/1.1\r\nHost: {host}\r\n" + (origin != null ? $"Origin: {origin}\r\n" : "") + "Connection: close\r\n\r\n";
        await net.WriteAsync(Encoding.ASCII.GetBytes(request)).ConfigureAwait(false);
        using var reader = new StreamReader(net, Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task<bool> RefusedAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
            return false;
        }
        catch (SocketException) { return true; }
    }
}
