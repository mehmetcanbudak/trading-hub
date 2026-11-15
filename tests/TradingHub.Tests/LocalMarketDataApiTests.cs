using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.App.Services;
using TradingHub.App.Services.LocalApi;
using TradingHub.Feeds;

namespace TradingHub.Tests;

public sealed class LocalMarketDataApiTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    // ===================================================================== binding and happy paths

    [Fact]
    public async Task BindsOnlyTheIPv4LoopbackAddressOnTheRequestedPort()
    {
        var api = Start(new FakeSource());
        try
        {
            Assert.True(api.IsRunning);
            Assert.NotNull(api.Endpoint);
            Assert.Equal(IPAddress.Loopback, api.Endpoint!.Address);
            Assert.NotEqual(0, api.Endpoint.Port);
        }
        finally { await api.StopAsync(); }
        Assert.False(api.IsRunning);
    }

    [Fact]
    public async Task InfoReportsReadOnlyIdentityWithSafeHeaders()
    {
        var api = Start(new FakeSource());
        try
        {
            int port = api.Endpoint!.Port;
            var response = await SendAsync(port, Request("GET", "/api/v1/info", Host(port)));
            Assert.Equal(200, response.Status);
            Assert.Equal("application/json; charset=utf-8", response.Headers["Content-Type"]);
            Assert.Equal("no-store", response.Headers["Cache-Control"]);
            Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
            Assert.Equal("close", response.Headers["Connection"]);
            Assert.False(response.Headers.ContainsKey("Access-Control-Allow-Origin"));
            using var json = JsonDocument.Parse(response.Body);
            Assert.Equal("Trading Hub", json.RootElement.GetProperty("app").GetString());
            Assert.Equal("1.2.3", json.RootElement.GetProperty("version").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("apiVersion").GetInt32());
            Assert.True(json.RootElement.GetProperty("readOnly").GetBoolean());
            Assert.Equal(port, json.RootElement.GetProperty("port").GetInt32());
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public async Task ConnectionsOpenTradesDepthKlinesAndLiquidityServeTheSourceData()
    {
        var source = new FakeSource();
        var api = Start(source);
        try
        {
            int port = api.Endpoint!.Port;
            string host = Host(port);

            var connections = await SendAsync(port, Request("GET", "/api/v1/connections", host));
            Assert.Equal(200, connections.Status);
            using (var json = JsonDocument.Parse(connections.Body))
            {
                var first = Assert.Single(json.RootElement.EnumerateArray());
                Assert.Equal("bf", first.GetProperty("id").GetString());
                Assert.Equal("binance-futures", first.GetProperty("marketData").GetString());
            }

            var open = await SendAsync(port, Request("GET", "/api/v1/open", host));
            Assert.Equal(200, open.Status);
            using (var json = JsonDocument.Parse(open.Body))
            {
                var item = Assert.Single(json.RootElement.EnumerateArray());
                Assert.Equal("binance-futures", item.GetProperty("connection").GetString());
                Assert.Equal("BTCUSDT", item.GetProperty("symbol").GetString());
                Assert.Equal(0.1m, item.GetProperty("tickSize").GetDecimal());
                Assert.Equal(101.5m, item.GetProperty("lastPrice").GetDecimal());
                Assert.Equal(2, item.GetProperty("trades").GetInt32());
                Assert.True(item.GetProperty("hasDepth").GetBoolean());
            }

            var trades = await SendAsync(port, Request("GET", "/api/v1/trades?conn=binance-futures&symbol=BTCUSDT&limit=999999", host));
            Assert.Equal(200, trades.Status);
            Assert.Contains("trades:binance-futures:BTCUSDT:5000", source.Calls);
            using (var json = JsonDocument.Parse(trades.Body))
            {
                var rows = json.RootElement.EnumerateArray().ToList();
                Assert.Equal(2, rows.Count);
                Assert.Equal(1_000, rows[0][0].GetInt64());
                Assert.Equal(101m, rows[0][1].GetDecimal());
                Assert.Equal("b", rows[0][3].GetString());
                Assert.Equal(JsonValueKind.Null, rows[0][4].ValueKind);
                Assert.Equal("s", rows[1][3].GetString());
                Assert.Equal("okx", rows[1][4].GetString());
            }

            var notOpen = await SendAsync(port, Request("GET", "/api/v1/trades?conn=binance-futures&symbol=ETHUSDT", host));
            Assert.Equal(404, notOpen.Status);
            Assert.Equal("not_open", ErrorCode(notOpen));

            var depth = await SendAsync(port, Request("GET", "/api/v1/depth?conn=binance-futures&symbol=BTCUSDT&levels=1", host));
            Assert.Equal(200, depth.Status);
            using (var json = JsonDocument.Parse(depth.Body))
            {
                Assert.Equal(5_000, json.RootElement.GetProperty("time").GetInt64());
                var bid = Assert.Single(json.RootElement.GetProperty("bids").EnumerateArray());
                Assert.Equal(101.4m, bid[0].GetDecimal());
                Assert.Equal(3m, bid[1].GetDecimal());
                Assert.Single(json.RootElement.GetProperty("asks").EnumerateArray());
            }

            var klines = await SendAsync(port, Request("GET", "/api/v1/klines?conn=bf&symbol=BTCUSDT&tf=1h&from=0&to=36000000&limit=2", host));
            Assert.Equal(200, klines.Status);
            var request = Assert.Single(source.KlineRequests);
            Assert.Equal(60, request.Minutes);
            Assert.Equal("bf", request.Connection);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(36_000_000).UtcDateTime, request.ToUtc);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(36_000_000 - 2 * 3_600_000).UtcDateTime, request.FromUtc);
            using (var json = JsonDocument.Parse(klines.Body))
            {
                var bars = json.RootElement.EnumerateArray().ToList();
                Assert.Equal(2, bars.Count); // the source returned three; limit keeps the newest two
                Assert.Equal(3_600_000, bars[0][0].GetInt64());
                Assert.Equal(7, bars[0].GetArrayLength());
                Assert.Equal(JsonValueKind.Null, bars[1][6].ValueKind);
            }

            var badTimeframe = await SendAsync(port, Request("GET", "/api/v1/klines?conn=bf&symbol=BTCUSDT&tf=7m", host));
            Assert.Equal(400, badTimeframe.Status);
            Assert.Equal("bad_timeframe", ErrorCode(badTimeframe));

            var unknown = await SendAsync(port, Request("GET", "/api/v1/klines?conn=bf&symbol=NOPE&tf=1m", host));
            Assert.Equal(404, unknown.Status);
            Assert.Equal("unknown_symbol", ErrorCode(unknown));

            var liquidity = await SendAsync(port, Request("GET", "/api/v1/liquidity?conn=binance-futures&symbol=BTCUSDT&minutes=9999", host));
            Assert.Equal(200, liquidity.Status);
            Assert.Contains("liquidity:120", source.Calls);
            using (var json = JsonDocument.Parse(liquidity.Body))
            {
                Assert.False(json.RootElement.GetProperty("recording").GetBoolean());
                Assert.Equal(0, json.RootElement.GetProperty("samples").GetArrayLength());
            }

            var missing = await SendAsync(port, Request("GET", "/api/v1/depth?symbol=BTCUSDT", host));
            Assert.Equal(400, missing.Status);
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public async Task ConnectionsProjectionNeverContainsSecretsAccountsOrEndpoints()
    {
        var profiles = new List<ConnectionProfile>
        {
            new()
            {
                Id = "rith", Name = "Futures", Kind = ConnectionKind.Rithmic, ApiKey = "SECRET-KEY", RithmicUser = "RITHMIC-USER-SECRET",
                RithmicUri = "wss://private-gateway.example:443", TradingNamespace = "NAMESPACE-SECRET", DeviceId = "DEVICE-SECRET",
                AllowRealOrders = true, MaxOrderQuantity = 7,
            },
            new() { Id = "tv", Name = "Tradovate", Kind = ConnectionKind.Tradovate, TradovateUser = "TRADOVATE-USER-SECRET", TradovateCid = "CID-SECRET" },
            new() { Id = "oa", Name = "Oanda", Kind = ConnectionKind.Oanda, ApiKey = "SECRET-KEY", OandaAccount = "OANDA-ACCOUNT-SECRET" },
            new() { Id = "ct", Name = "cTrader", Kind = ConnectionKind.CTrader, CTraderClientId = "CTRADER-CLIENT-SECRET", CTraderLogin = 987654321 },
            new() { Id = "ib", Name = "IB", Kind = ConnectionKind.InteractiveBrokers, IbHost = "10.11.12.13" },
            new() { Id = "db", Name = "Databento", Kind = ConnectionKind.Databento, ApiKey = "SECRET-KEY" },
        };
        var source = new FakeSource { Connections = AppLocalMarketDataSource.ProjectConnections(profiles) };
        var api = Start(source);
        try
        {
            int port = api.Endpoint!.Port;
            var response = await SendAsync(port, Request("GET", "/api/v1/connections", Host(port)));
            Assert.Equal(200, response.Status);
            foreach (var secret in new[] { "SECRET-KEY", "RITHMIC-USER-SECRET", "private-gateway", "NAMESPACE-SECRET", "DEVICE-SECRET",
                         "TRADOVATE-USER-SECRET", "CID-SECRET", "OANDA-ACCOUNT-SECRET", "CTRADER-CLIENT-SECRET", "987654321", "10.11.12.13" })
                Assert.DoesNotContain(secret, response.Body, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(response.Body);
            var items = json.RootElement.EnumerateArray().ToList();
            Assert.Equal(5, items.Count); // Databento has no chart market data
            foreach (var item in items)
                Assert.Equal(new[] { "id", "name", "kind", "enabled", "marketData" }, item.EnumerateObject().Select(p => p.Name).ToArray());
        }
        finally { await api.StopAsync(); }
    }

    // ===================================================================== DNS rebinding, CSRF and CORS

    [Fact]
    public async Task HostHeaderIsRequiredAndMustBeLoopbackWithTheBoundPort()
    {
        var source = new FakeSource();
        var api = Start(source);
        try
        {
            int port = api.Endpoint!.Port;
            var missing = await SendAsync(port, Request("GET", "/api/v1/info", null));
            Assert.Equal(400, missing.Status);
            Assert.Equal("bad_host", ErrorCode(missing));
            foreach (var host in new[] { $"evil.example:{port}", $"127.0.0.1:{port + 1}", "127.0.0.1", $"127.0.0.2:{port}", $"[::1]:{port}" })
            {
                var response = await SendAsync(port, Request("GET", "/api/v1/info", host));
                Assert.Equal(421, response.Status);
                Assert.Equal("bad_host", ErrorCode(response));
            }
            Assert.Empty(source.Calls);
            Assert.Equal(200, (await SendAsync(port, Request("GET", "/api/v1/info", $"LOCALHOST:{port}"))).Status);
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public async Task OriginsOutsideTheAllowListAreRefusedAndAllowedOriginsAreEchoed()
    {
        var source = new FakeSource();
        var api = Start(source, new LocalMarketDataApiOptions { AllowedOrigins = ["http://LOCALHOST:3000/", "not an origin"] });
        try
        {
            int port = api.Endpoint!.Port;
            string host = Host(port);
            foreach (var origin in new[] { "http://evil.example", "http://localhost:3001", "https://localhost:3000", "null" })
            {
                var refused = await SendAsync(port, Request("GET", "/api/v1/info", host, "Origin: " + origin));
                Assert.Equal(403, refused.Status);
                Assert.Equal("origin_not_allowed", ErrorCode(refused));
                Assert.False(refused.Headers.ContainsKey("Access-Control-Allow-Origin"));
            }
            var crossSite = await SendAsync(port, Request("GET", "/api/v1/info", host, "Sec-Fetch-Site: cross-site"));
            Assert.Equal(403, crossSite.Status);
            Assert.Empty(source.Calls);

            var allowed = await SendAsync(port, Request("GET", "/api/v1/info", host, "Origin: http://localhost:3000", "Sec-Fetch-Site: cross-site"));
            Assert.Equal(200, allowed.Status);
            Assert.Equal("http://localhost:3000", allowed.Headers["Access-Control-Allow-Origin"]);
            Assert.Equal("Origin", allowed.Headers["Vary"]);
            Assert.False(allowed.Headers.ContainsKey("Access-Control-Allow-Credentials"));

            var upgrade = await SendAsync(port, Request("GET", "/api/v1/stream?conn=binance-futures&symbol=BTCUSDT", host,
                "Origin: http://evil.example", "Upgrade: websocket", "Connection: Upgrade", "Sec-WebSocket-Version: 13",
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ=="));
            Assert.Equal(403, upgrade.Status);
            var upgradeHost = await SendAsync(port, Request("GET", "/api/v1/stream?conn=binance-futures&symbol=BTCUSDT", "evil.example:" + port,
                "Upgrade: websocket", "Connection: Upgrade", "Sec-WebSocket-Version: 13", "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ=="));
            Assert.Equal(421, upgradeHost.Status);
            Assert.DoesNotContain(source.Calls, call => call.StartsWith("stream", StringComparison.Ordinal));
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public async Task PreflightIsAnsweredOnlyForAllowedOrigins()
    {
        var api = Start(new FakeSource(), new LocalMarketDataApiOptions { AllowedOrigins = ["http://localhost:3000"] });
        try
        {
            int port = api.Endpoint!.Port;
            string host = Host(port);
            var allowed = await SendAsync(port, Request("OPTIONS", "/api/v1/trades", host, "Origin: http://localhost:3000", "Access-Control-Request-Method: GET"));
            Assert.Equal(204, allowed.Status);
            Assert.Equal("http://localhost:3000", allowed.Headers["Access-Control-Allow-Origin"]);
            Assert.Equal("GET", allowed.Headers["Access-Control-Allow-Methods"]);
            Assert.Equal("", allowed.Body);
            Assert.Equal(403, (await SendAsync(port, Request("OPTIONS", "/api/v1/trades", host, "Origin: http://evil.example"))).Status);
            Assert.Equal(403, (await SendAsync(port, Request("OPTIONS", "/api/v1/trades", host))).Status);
        }
        finally { await api.StopAsync(); }
    }

    // ===================================================================== methods, routes and bounds

    [Fact]
    public async Task MutatingMethodsAreRejectedWithoutTouchingTheSource()
    {
        var source = new FakeSource();
        var api = Start(source);
        try
        {
            int port = api.Endpoint!.Port;
            foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH", "HEAD" })
            {
                var response = await SendAsync(port, Request(method, "/api/v1/trades?conn=binance-futures&symbol=BTCUSDT", Host(port)));
                Assert.Equal(405, response.Status);
                Assert.Equal("GET", response.Headers["Allow"]);
            }
            Assert.Empty(source.Calls);
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public async Task UnknownPathsAreNotFound()
    {
        var source = new FakeSource();
        var api = Start(source);
        try
        {
            int port = api.Endpoint!.Port;
            foreach (var path in new[] { "/", "/api/v1", "/api/v1/INFO", "/api/v2/info", "/api/v1/orders" })
            {
                var response = await SendAsync(port, Request("GET", path, Host(port)));
                Assert.Equal(404, response.Status);
                Assert.Equal("not_found", ErrorCode(response));
            }
            Assert.Empty(source.Calls);
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public async Task OversizedRequestLineHeadersAndBodiesAreRejected()
    {
        var source = new FakeSource();
        var api = Start(source);
        try
        {
            int port = api.Endpoint!.Port;
            string host = Host(port);
            var longLine = await SendAsync(port, Request("GET", "/api/v1/info?pad=" + new string('a', 2100), host));
            Assert.Equal(414, longLine.Status);

            var bigHeaders = await SendAsync(port, Request("GET", "/api/v1/info", host, "X-Pad: " + new string('b', 9000)));
            Assert.Equal(431, bigHeaders.Status);

            var manyHeaders = await SendAsync(port, Request("GET", "/api/v1/info", host, Enumerable.Range(0, 70).Select(i => $"X-H{i}: v").ToArray()));
            Assert.Equal(431, manyHeaders.Status);

            var body = await SendAsync(port, $"GET /api/v1/info HTTP/1.1\r\nHost: {host}\r\nContent-Length: 3\r\n\r\nabc");
            Assert.Equal(413, body.Status);
            var chunked = await SendAsync(port, $"GET /api/v1/info HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
            Assert.Equal(413, chunked.Status);
            Assert.Empty(source.Calls);
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public async Task SilentClientsTimeOutWaitingForHeaders()
    {
        var api = Start(new FakeSource(), new LocalMarketDataApiOptions { HeaderTimeout = TimeSpan.FromMilliseconds(50) });
        try
        {
            var response = await SendAsync(api.Endpoint!.Port, "");
            Assert.Equal(408, response.Status);
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public async Task RequestsBeyondTheTokenBucketAreRateLimitedUntilTheClockAdvances()
    {
        var time = new ManualTime();
        var api = Start(new FakeSource(), new LocalMarketDataApiOptions { RequestsPerSecond = 1, RequestBurst = 2, Time = time });
        try
        {
            int port = api.Endpoint!.Port;
            string request = Request("GET", "/api/v1/info", Host(port));
            Assert.Equal(200, (await SendAsync(port, request)).Status);
            Assert.Equal(200, (await SendAsync(port, request)).Status);
            var limited = await SendAsync(port, request);
            Assert.Equal(429, limited.Status);
            Assert.Equal("1", limited.Headers["Retry-After"]);
            Assert.Equal("rate_limited", ErrorCode(limited));
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(200, (await SendAsync(port, request)).Status);
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public async Task ConnectionsBeyondTheCapAreRefusedImmediately()
    {
        var api = Start(new FakeSource(), new LocalMarketDataApiOptions { MaxConnections = 2, HeaderTimeout = TimeSpan.FromMinutes(5) });
        using var idle1 = new TcpClient();
        using var idle2 = new TcpClient();
        try
        {
            int port = api.Endpoint!.Port;
            await idle1.ConnectAsync(IPAddress.Loopback, port);
            await idle2.ConnectAsync(IPAddress.Loopback, port);
            var refused = await SendAsync(port, Request("GET", "/api/v1/info", Host(port)));
            Assert.Equal(503, refused.Status);
            Assert.Equal("busy", ErrorCode(refused));
        }
        finally { await api.StopAsync().WaitAsync(Deadline); }
    }

    // ===================================================================== lifecycle

    [Fact]
    public async Task StopWaitsForInFlightHandlersRefusesNewConnectionsAndIsIdempotent()
    {
        var source = new FakeSource { InfoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var api = Start(source);
        int port = api.Endpoint!.Port;
        var inFlight = SendAsync(port, Request("GET", "/api/v1/info", Host(port)));
        await source.InfoEntered.Task.WaitAsync(Deadline);

        var stop = api.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.False(api.IsRunning);
        Assert.Same(stop, api.StopAsync());
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var late = new TcpClient();
            await late.ConnectAsync(IPAddress.Loopback, port);
        });
        Assert.False(stop.IsCompleted);

        source.InfoGate.SetResult();
        await stop.WaitAsync(Deadline);
        try { await inFlight.WaitAsync(Deadline); }
        catch (Exception error) when (error is IOException or SocketException or FormatException)
        {
            // the cancelled response may be cut off; only the drain is under test
        }
        Assert.Same(stop, api.StopAsync());
        Assert.Throws<ObjectDisposedException>(api.Start);
    }

    [Fact]
    public async Task StreamDeliversTradesAndClosesCleanlyOnStop()
    {
        var source = new FakeSource();
        var api = Start(source, new LocalMarketDataApiOptions { StreamInterval = TimeSpan.FromMilliseconds(10) });
        int port = api.Endpoint!.Port;
        using var timeout = new CancellationTokenSource(Deadline);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/v1/stream?conn=binance-futures&symbol=BTCUSDT"), timeout.Token);
        await source.Subscribed.Task.WaitAsync(Deadline);
        source.OnTrades!(new ApiTradeBatch([new ApiTrade(1_000, 101m, 2m, "b", null), new ApiTrade(2_000, 101.5m, 1m, "s", null)], 0));

        JsonDocument? trades = null;
        while (trades == null)
        {
            var (type, text) = await ReceiveAsync(client, timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, type);
            var message = JsonDocument.Parse(text);
            if (message.RootElement.GetProperty("type").GetString() == "trades") trades = message;
            else message.Dispose();
        }
        using (trades)
        {
            Assert.Equal("BTCUSDT", trades.RootElement.GetProperty("symbol").GetString());
            var data = trades.RootElement.GetProperty("data").EnumerateArray().ToList();
            Assert.Equal(2, data.Count);
            Assert.Equal(101m, data[0][1].GetDecimal());
            Assert.Equal("s", data[1][3].GetString());
        }

        var stop = api.StopAsync();
        while (true)
        {
            var (type, _) = await ReceiveAsync(client, timeout.Token);
            if (type == WebSocketMessageType.Close) break;
        }
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        await stop.WaitAsync(Deadline);
        await source.Unsubscribed.Task.WaitAsync(Deadline);
    }

    [Fact]
    public async Task StreamForAnInstrumentThatIsNotOpenIsRefusedBeforeTheUpgrade()
    {
        var api = Start(new FakeSource());
        try
        {
            int port = api.Endpoint!.Port;
            var response = await SendAsync(port, Request("GET", "/api/v1/stream?conn=binance-futures&symbol=ETHUSDT", Host(port),
                "Upgrade: websocket", "Connection: Upgrade", "Sec-WebSocket-Version: 13", "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ=="));
            Assert.Equal(404, response.Status);
            Assert.Equal("not_open", ErrorCode(response));
        }
        finally { await api.StopAsync(); }
    }

    [Fact]
    public void CandlesAggregateIntoUtcAlignedBucketsAndKeepUnknownBuyVolumeNull()
    {
        var bars = new List<Kline>
        {
            new(0, 1, 2, 0.5m, 1.5m, 10, 4, true),
            new(60_000, 1.5m, 3, 1, 2.5m, 5, 1, true),
            new(300_000, 2.5m, 2.6m, 2.4m, 2.5m, 1, 0, false),
        };
        var result = ApiKlineAggregation.Aggregate(bars, 5);
        Assert.Equal(2, result.Count);
        Assert.Equal(new ApiKline(0, 1, 3, 0.5m, 2.5m, 15, 5), result[0]);
        Assert.Equal(new ApiKline(300_000, 2.5m, 2.6m, 2.4m, 2.5m, 1, null), result[1]);
        Assert.Equal(60, ApiKlineAggregation.BaseMinutes(240));
        Assert.Equal(60, ApiKlineAggregation.BaseMinutes(1440));
        Assert.Equal(15, ApiKlineAggregation.BaseMinutes(15));
    }

    [Fact]
    public void OriginsNormalizeToSchemeHostAndPortOnly()
    {
        var invalid = new List<string>();
        var valid = LocalMarketDataApiOptions.NormalizeOrigins(
            ["http://LocalHost:3000/", "https://tools.example", "http://localhost:3000", "ftp://x.example", "http://a.example/path", "http://u:p@a.example", "localhost:3000", "http://a.example?q"],
            invalid);
        Assert.Equal(new[] { "http://localhost:3000", "https://tools.example" }, valid);
        Assert.Equal(5, invalid.Count);
    }

    // ===================================================================== helpers

    private static LocalMarketDataApi Start(FakeSource source, LocalMarketDataApiOptions? options = null)
    {
        options ??= new LocalMarketDataApiOptions();
        var api = new LocalMarketDataApi(source, new LocalMarketDataApiOptions
        {
            Port = 0,
            AllowedOrigins = options.AllowedOrigins,
            MaxConnections = options.MaxConnections,
            MaxStreams = options.MaxStreams,
            RequestsPerSecond = options.RequestsPerSecond,
            RequestBurst = options.RequestBurst,
            HeaderTimeout = options.HeaderTimeout,
            StreamInterval = options.StreamInterval,
            Time = options.Time,
        });
        api.Start();
        return api;
    }

    private static string Host(int port) => $"127.0.0.1:{port}";

    private static string Request(string method, string target, string? host, params string[] headers)
    {
        var text = new StringBuilder();
        text.Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
        if (host != null) text.Append("Host: ").Append(host).Append("\r\n");
        foreach (var header in headers) text.Append(header).Append("\r\n");
        return text.Append("\r\n").ToString();
    }

    private sealed record RawResponse(int Status, Dictionary<string, string> Headers, string Body);

    private static async Task<RawResponse> SendAsync(int port, string request)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = client.GetStream();
        if (request.Length > 0) await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, timeout.Token);
        string text = Encoding.UTF8.GetString(buffer.ToArray());
        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (split < 0) throw new FormatException("Incomplete HTTP response: " + text);
        var lines = text[..split].Split("\r\n");
        int status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        return new RawResponse(status, headers, text[(split + 4)..]);
    }

    private static string? ErrorCode(RawResponse response)
    {
        using var json = JsonDocument.Parse(response.Body);
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task<(WebSocketMessageType Type, string Text)> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return (result.MessageType, Encoding.UTF8.GetString(message.ToArray()));
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeSource : ILocalMarketDataSource
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<ApiKlineRequest> KlineRequests { get; } = new();
        public TaskCompletionSource? InfoGate { get; init; }
        public TaskCompletionSource InfoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Unsubscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ApiConnection> Connections { get; init; } = [new ApiConnection("bf", "Binance", "BinanceFutures", true, "binance-futures")];
        public Action<ApiTradeBatch>? OnTrades { get; private set; }

        private static bool IsOpen(string connection, string symbol) =>
            connection is "binance-futures" or "bf" && symbol.Equals("BTCUSDT", StringComparison.OrdinalIgnoreCase);

        public async Task<ApiResult<ApiInfo>> GetInfoAsync(CancellationToken ct)
        {
            Calls.Enqueue("info");
            InfoEntered.TrySetResult();
            // Deliberately ignores cancellation: shutdown must still wait for the actual return.
            if (InfoGate != null) await InfoGate.Task;
            return ApiResult<ApiInfo>.Ok(new ApiInfo("Trading Hub", "1.2.3"));
        }

        public Task<ApiResult<IReadOnlyList<ApiConnection>>> GetConnectionsAsync(CancellationToken ct)
        {
            Calls.Enqueue("connections");
            return Task.FromResult(ApiResult<IReadOnlyList<ApiConnection>>.Ok(Connections));
        }

        public Task<ApiResult<IReadOnlyList<ApiOpenInstrument>>> GetOpenInstrumentsAsync(CancellationToken ct)
        {
            Calls.Enqueue("open");
            IReadOnlyList<ApiOpenInstrument> open = [new ApiOpenInstrument("binance-futures", "BTCUSDT", 0.1m, 101.5m, 2, true)];
            return Task.FromResult(ApiResult<IReadOnlyList<ApiOpenInstrument>>.Ok(open));
        }

        public Task<ApiResult<IReadOnlyList<ApiKline>>> GetKlinesAsync(ApiKlineRequest request, CancellationToken ct)
        {
            Calls.Enqueue("klines");
            if (!request.Symbol.Equals("BTCUSDT", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(ApiResult<IReadOnlyList<ApiKline>>.NotFound("unknown_symbol", "Unknown symbol."));
            KlineRequests.Enqueue(request);
            IReadOnlyList<ApiKline> bars =
            [
                new ApiKline(0, 1, 2, 0.5m, 1.5m, 10, 4),
                new ApiKline(3_600_000, 1.5m, 2, 1, 1.8m, 11, 5),
                new ApiKline(7_200_000, 1.8m, 2.2m, 1.7m, 2, 12, null),
            ];
            return Task.FromResult(ApiResult<IReadOnlyList<ApiKline>>.Ok(bars));
        }

        public Task<ApiResult<IReadOnlyList<ApiTrade>>> GetTradesAsync(string connection, string symbol, int limit, CancellationToken ct)
        {
            Calls.Enqueue($"trades:{connection}:{symbol}:{limit}");
            if (!IsOpen(connection, symbol)) return Task.FromResult(ApiResult<IReadOnlyList<ApiTrade>>.NotOpen("Not open."));
            IReadOnlyList<ApiTrade> trades = [new ApiTrade(1_000, 101m, 2m, "b", null), new ApiTrade(2_000, 101.5m, 1m, "s", "okx")];
            return Task.FromResult(ApiResult<IReadOnlyList<ApiTrade>>.Ok(trades));
        }

        public Task<ApiResult<ApiBook>> GetDepthAsync(string connection, string symbol, int levels, CancellationToken ct)
        {
            Calls.Enqueue($"depth:{levels}");
            if (!IsOpen(connection, symbol)) return Task.FromResult(ApiResult<ApiBook>.NotOpen("Not open."));
            return Task.FromResult(ApiResult<ApiBook>.Ok(new ApiBook(5_000, [new(101.4m, 3m), new(101.3m, 4m)], [new(101.5m, 1m), new(101.6m, 2m)])));
        }

        public Task<ApiResult<ApiLiquidity>> GetLiquidityAsync(string connection, string symbol, TimeSpan window, int levels, int maxSamples, CancellationToken ct)
        {
            Calls.Enqueue($"liquidity:{(int)window.TotalMinutes}");
            if (!IsOpen(connection, symbol)) return Task.FromResult(ApiResult<ApiLiquidity>.NotOpen("Not open."));
            return Task.FromResult(ApiResult<ApiLiquidity>.Ok(new ApiLiquidity(false, 0.1m, [], false)));
        }

        public Task<ApiResult<ApiStreamSubscription>> SubscribeStreamAsync(string connection, string symbol,
            Action<ApiTradeBatch> onTrades, Action<Func<int, ApiBook>> onBook, CancellationToken ct)
        {
            Calls.Enqueue("stream");
            if (!IsOpen(connection, symbol)) return Task.FromResult(ApiResult<ApiStreamSubscription>.NotOpen("Not open."));
            OnTrades = onTrades;
            onBook(levels => new ApiBook(5_000, [new(101.4m, 3m)], [new(101.5m, 1m)]));
            Subscribed.TrySetResult();
            return Task.FromResult(ApiResult<ApiStreamSubscription>.Ok(new ApiStreamSubscription("BTCUSDT", new Subscription(this))));
        }

        private sealed class Subscription(FakeSource owner) : IDisposable
        {
            public void Dispose() => owner.Unsubscribed.TrySetResult();
        }
    }
}
