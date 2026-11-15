using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using TradingHub.App.Services;

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
