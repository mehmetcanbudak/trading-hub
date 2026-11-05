using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using TradingHub.Feeds.Tradovate;
using TradingHub.Networking;

namespace TradingHub.Tests;

public sealed class KvpTradovateProxyTests
{
    [Fact]
    public async Task SignInPrivateRequestsAndBothSocketsUseTheConfiguredProxy()
    {
        using var proxy = new FakeProxyServer();
        using var fake = new FakeTradovate();
        using var marketOrigin = new FakeHttpServer { OnSocket = fake.Server.OnSocket };
        var settings = fake.Settings();
        settings.MdSocketUrl = marketOrigin.Ws + "/md/v1/websocket";
        using var feed = new TradovateFeed(() => settings, Network(proxy));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (ok, _) = await feed.TestAsync(timeout.Token);
        await feed.Connection.Md.RequestAsync("md/subscribeQuote", new JsonObject { ["symbol"] = fake.Contract }, timeout.Token);
        var snapshot = await feed.Connection.User.RequestAsync("user/syncrequest", new JsonObject(), timeout.Token);

        Assert.True(ok);
        Assert.Equal("DEMO101", snapshot.GetProperty("accounts")[0].GetProperty("name").GetString());
        Assert.Equal("MD1", fake.MdToken);
        Assert.Equal("AT1", fake.UserToken);
        Assert.Equal(1, fake.Logins);
        FakeRequest signIn;
        FakeRequest accountList;
        lock (fake.Server.Requests)
        {
            signIn = Assert.Single(fake.Server.Requests, r => r.Route == "/v1/auth/accesstokenrequest");
            accountList = Assert.Single(fake.Server.Requests, r => r.Route == "/v1/account/list");
        }
        using var body = JsonDocument.Parse(signIn.Body);
        Assert.Equal("pw", body.RootElement.GetProperty("password").GetString());
        Assert.Equal("SEC", body.RootElement.GetProperty("sec").GetString());
        Assert.Equal("Bearer AT1", accountList.Headers["Authorization"]);
        Assert.Contains(proxy.Requests, r => r.Method == "POST" && r.Target.AbsolutePath == "/v1/auth/accesstokenrequest");
        Assert.Contains(proxy.Requests, r => r.Method == "GET" && r.Target.AbsolutePath == "/v1/account/list");
        Assert.Contains(proxy.Requests, r => IsSocketHop(r, marketOrigin.Port));
        Assert.Contains(proxy.Requests, r => IsSocketHop(r, fake.Server.Port));
    }

    [Fact]
    public async Task ExistingContextsRemainUsableAndReconnectThroughTheirCapturedProxyAfterAReplacementIsCreated()
    {
        using var originalProxy = new FakeProxyServer();
        using var replacementProxy = new FakeProxyServer();
        using var originalOrigin = new FakeTradovate();
        using var replacementOrigin = new FakeTradovate();
        var opened = Channel.CreateUnbounded<WebSocket>();
        var handleSocket = originalOrigin.Server.OnSocket!;
        originalOrigin.Server.OnSocket = (path, socket, ct) =>
        {
            opened.Writer.TryWrite(socket);
            return handleSocket(path, socket, ct);
        };
        var originalSettings = originalOrigin.Settings();
        var replacementSettings = replacementOrigin.Settings();
        var selectedNetwork = Network(originalProxy);
        using var original = new TradovateFeed(() => originalSettings, selectedNetwork);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Assert.True((await original.TestAsync(timeout.Token)).Ok);
        await original.Connection.Md.RequestAsync("md/subscribeQuote", new JsonObject { ["symbol"] = originalOrigin.Contract }, timeout.Token);
        await original.Connection.User.RequestAsync("user/syncrequest", new JsonObject(), timeout.Token);
        var oldMarketSocket = await opened.Reader.ReadAsync(timeout.Token);
        var oldUserSocket = await opened.Reader.ReadAsync(timeout.Token);

        selectedNetwork = Network(replacementProxy);
        using var replacement = new TradovateFeed(() => replacementSettings, selectedNetwork);
        Assert.True((await replacement.TestAsync(timeout.Token)).Ok);
        await replacement.Connection.Md.RequestAsync("md/subscribeQuote", new JsonObject { ["symbol"] = replacementOrigin.Contract }, timeout.Token);
        var replacementSnapshot = await replacement.Connection.User.RequestAsync("user/syncrequest", new JsonObject(), timeout.Token);
        Assert.Equal("DEMO101", replacementSnapshot.GetProperty("accounts")[0].GetProperty("name").GetString());

        var stillActive = await original.Connection.GetAsync("/account/list", timeout.Token);
        Assert.Equal("DEMO101", stillActive[0].GetProperty("name").GetString());
        await original.Connection.Md.RequestAsync("md/subscribeQuote", new JsonObject { ["symbol"] = originalOrigin.Contract }, timeout.Token);
        var originalSnapshot = await original.Connection.User.RequestAsync("user/syncrequest", new JsonObject(), timeout.Token);
        Assert.Equal("DEMO101", originalSnapshot.GetProperty("accounts")[0].GetProperty("name").GetString());

        var marketDisconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var userDisconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        original.Connection.Md.StateChanged += (up, _) => { if (!up) marketDisconnected.TrySetResult(true); };
        original.Connection.User.StateChanged += (up, _) => { if (!up) userDisconnected.TrySetResult(true); };
        await oldMarketSocket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Fixture reconnect", timeout.Token);
        await oldUserSocket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Fixture reconnect", timeout.Token);
        await Task.WhenAll(marketDisconnected.Task, userDisconnected.Task).WaitAsync(timeout.Token);

        await original.Connection.Md.RequestAsync("md/subscribeQuote", new JsonObject { ["symbol"] = originalOrigin.Contract }, timeout.Token);
        var reconnected = await original.Connection.User.RequestAsync("user/syncrequest", new JsonObject(), timeout.Token);
        Assert.Equal("DEMO101", reconnected.GetProperty("accounts")[0].GetProperty("name").GetString());
        Assert.Equal(1, originalOrigin.Logins);
        Assert.Equal("MD1", originalOrigin.MdToken);
        Assert.Equal("AT1", originalOrigin.UserToken);
        Assert.True(originalProxy.Requests.Count(r => IsSocketHop(r, originalOrigin.Server.Port)) >= 4);
        Assert.All(originalProxy.Requests, r => Assert.Equal(originalOrigin.Server.Port, r.Target.Port));
        Assert.All(replacementProxy.Requests, r => Assert.Equal(replacementOrigin.Server.Port, r.Target.Port));
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task AuthenticationRedirectDoesNotForwardSignInSecrets(int status)
    {
        using var proxy = new FakeProxyServer();
        using var fake = new FakeTradovate();
        using var redirectTarget = new FakeHttpServer
        {
            OnHttp = _ => (200, "{\"accessToken\":\"redirected-token\"}"),
        };
        var respond = fake.Server.OnHttp!;
        fake.Server.OnHttp = request => request.Route == "/v1/auth/accesstokenrequest"
            ? (status, "{\"errorText\":\"Fixture redirect\"}")
            : respond(request);
        fake.Server.ResponseHeaders = _ => new Dictionary<string, string>
        {
            ["Location"] = redirectTarget.Http + "/stolen-sign-in",
        };
        var settings = fake.Settings();
        using var feed = new TradovateFeed(() => settings, Network(proxy));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsAsync<TradovateException>(() => feed.Connection.AccessTokenAsync(timeout.Token));

        Assert.Equal(status, error.Status);
        lock (redirectTarget.Requests) Assert.Empty(redirectTarget.Requests);
        Assert.Contains(proxy.Requests, r => r.Method == "POST" && r.Target.AbsolutePath == "/v1/auth/accesstokenrequest");
        Assert.DoesNotContain(proxy.Requests, r => r.Target.Port == redirectTarget.Port);
        FakeRequest signIn;
        lock (fake.Server.Requests) signIn = Assert.Single(fake.Server.Requests);
        using var body = JsonDocument.Parse(signIn.Body);
        Assert.Equal("pw", body.RootElement.GetProperty("password").GetString());
        Assert.Equal("SEC", body.RootElement.GetProperty("sec").GetString());
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task PrivateRequestRedirectDoesNotVisitAnotherOrigin(int status)
    {
        using var proxy = new FakeProxyServer();
        using var fake = new FakeTradovate();
        using var redirectTarget = new FakeHttpServer { OnHttp = _ => (200, "[]") };
        var respond = fake.Server.OnHttp!;
        fake.Server.OnHttp = request => request.Route == "/v1/account/list"
            ? (status, "{\"errorText\":\"Fixture redirect\"}")
            : respond(request);
        fake.Server.ResponseHeaders = _ => new Dictionary<string, string>
        {
            ["Location"] = redirectTarget.Http + "/stolen-account-request",
        };
        var settings = fake.Settings();
        using var feed = new TradovateFeed(() => settings, Network(proxy));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsAsync<TradovateException>(() => feed.Connection.GetAsync("/account/list", timeout.Token));

        Assert.Equal(status, error.Status);
        lock (redirectTarget.Requests) Assert.Empty(redirectTarget.Requests);
        Assert.DoesNotContain(proxy.Requests, r => r.Target.Port == redirectTarget.Port);
        FakeRequest privateRequest;
        lock (fake.Server.Requests)
            privateRequest = Assert.Single(fake.Server.Requests, r => r.Route == "/v1/account/list");
        Assert.Equal("Bearer AT1", privateRequest.Headers["Authorization"]);
        Assert.Contains(proxy.Requests, r => r.Method == "GET" && r.Target.AbsolutePath == "/v1/account/list");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SocketRedirectDoesNotForwardAuthorizationOrPrivateRequests(bool marketData)
    {
        using var proxy = new FakeProxyServer();
        using var origin = new FakeTradovate();
        using var redirectTarget = new FakeTradovate();
        int redirectedSockets = 0;
        var handleTargetSocket = redirectTarget.Server.OnSocket!;
        redirectTarget.Server.OnSocket = (path, socket, ct) =>
        {
            Interlocked.Increment(ref redirectedSockets);
            return handleTargetSocket(path, socket, ct);
        };
        origin.Server.SocketRedirect = path => redirectTarget.Server.Ws + path;
        var settings = origin.Settings();
        using var feed = new TradovateFeed(() => settings, Network(proxy));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = marketData ? feed.Connection.Md : feed.Connection.User;
        string endpoint = marketData ? "md/subscribeQuote" : "user/syncrequest";

        await Assert.ThrowsAsync<WebSocketException>(() => transport.RequestAsync(endpoint, new JsonObject(), timeout.Token));

        Assert.Equal(1, origin.Logins);
        Assert.Equal(0, Volatile.Read(ref redirectedSockets));
        Assert.Null(redirectTarget.MdToken);
        Assert.Null(redirectTarget.UserToken);
        Assert.DoesNotContain(proxy.Requests, r => r.Target.Port == redirectTarget.Server.Port);
        Assert.Contains(proxy.Requests, r => IsSocketHop(r, origin.Server.Port));
    }

    private static ProxyHandlerFactory Network(FakeProxyServer proxy) =>
        new(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });

    private static bool IsSocketHop(FakeProxyRequest request, int port) => request.Target.Port == port &&
        (request.Method == "CONNECT" || request.Headers.TryGetValue("Upgrade", out var upgrade) &&
            upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase));
}
