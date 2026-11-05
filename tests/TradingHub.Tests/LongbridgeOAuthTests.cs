using System.Net;
using System.Text;
using System.Text.Json;
using TradingHub.Feeds.Longbridge;

namespace TradingHub.Tests;

public sealed class LongbridgeOAuthTests
{
    [Fact]
    public async Task AuthorizationUsesS256AndRejectsWrongStateBeforeSendingCode()
    {
        string? secret = null;
        var handler = new HttpFixture();
        using var http = new HttpClient(handler);
        var clock = new Clock();
        var settings = new LongbridgeSettings { ClientId = "public-client", BaseUrl = "https://auth.invalid" };
        int entropyCall = 0;
        var session = new LongbridgeSession(settings, http, () => secret, value => secret = value, clock,
            length => ++entropyCall == 1 ? Convert.FromBase64String("dBjftJeZ4CVP+mB92K27uhbUJU1p1r/wW1gFWFOEjXk=") :
                Enumerable.Range(0, length).Select(i => (byte)i).ToArray());
        var attempt = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        var query = Query(attempt.AuthorizationUri);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", query["code_challenge"]); // RFC 7636 Appendix B.
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("http://localhost:60355/callback", query["redirect_uri"]);
        Assert.NotEqual(query["state"], query["code_challenge"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAuthorizationAsync(
            new Uri("http://localhost:60355/callback?code=secret-code&state=wrong"), default));
        Assert.Empty(handler.Requests);
        Assert.Null(secret);
        handler.Reply = _ => Task.FromResult(Json("{\"access_token\":\"access-one\",\"refresh_token\":\"refresh-one\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"));
        await session.CompleteAuthorizationAsync(Callback(query["state"]), default);
        var form = Query(new Uri("https://form.invalid/?" + handler.Requests.Single().Body));
        Assert.Equal("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", form["code_verifier"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("access-one", await session.AccessTokenAsync(default));
        Assert.Contains("refresh-one", secret!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAuthorizationAsync(Callback(query["state"]), default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CallbackOriginDuplicateStateAndExpiredAuthorizationCannotExchangeTokens()
    {
        string? secret = null;
        var handler = new HttpFixture();
        using var http = new HttpClient(handler);
        var clock = new Clock();
        var session = new LongbridgeSession(new() { ClientId = "client", BaseUrl = "https://auth.invalid" }, http,
            () => secret, value => secret = value, clock);
        var attempt = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        string state = Query(attempt.AuthorizationUri)["state"];
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAuthorizationAsync(
            new Uri($"http://localhost:60356/callback?state={state}&code=code"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAuthorizationAsync(
            new Uri($"http://localhost:60355/callback?state={state}&state={state}&code=code"), default));
        clock.Utc = clock.Utc.AddMinutes(6);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAuthorizationAsync(Callback(state), default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReplacedAuthorizationAndForgottenCredentialsCannotBeResurrectedByLateResponses()
    {
        string? secret = null;
        var handler = new HttpFixture();
        using var http = new HttpClient(handler);
        var session = new LongbridgeSession(new() { ClientId = "client", BaseUrl = "https://auth.invalid" }, http,
            () => secret, value => secret = value, new Clock());
        var first = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        var second = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAuthorizationAsync(Callback(Query(first.AuthorizationUri)["state"]), default));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Reply = _ => { entered.TrySetResult(); return response.Task; };
        var completing = session.CompleteAuthorizationAsync(Callback(Query(second.AuthorizationUri)["state"]), default);
        await entered.Task;
        session.ForgetAuthorization();
        response.SetResult(Json("{\"access_token\":\"late-access\",\"refresh_token\":\"late-refresh\",\"expires_in\":3600}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => completing);
        Assert.Null(secret);
    }

    [Fact]
    public async Task RefreshIsSerializedPreservesOmittedRefreshTokenAndDoesNotOverwriteReplacement()
    {
        string? secret = null;
        var handler = new HttpFixture();
        using var http = new HttpClient(handler);
        var clock = new Clock();
        var session = new LongbridgeSession(new() { ClientId = "client", BaseUrl = "https://auth.invalid" }, http,
            () => secret, value => secret = value, clock);
        handler.Reply = _ => Task.FromResult(Json("{\"access_token\":\"old\",\"refresh_token\":\"rotate-me\",\"expires_in\":120}"));
        var begin = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(Callback(Query(begin.AuthorizationUri)["state"]), default);
        clock.Utc = clock.Utc.AddSeconds(90);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Reply = _ => { entered.TrySetResult(); return response.Task; };
        var one = session.AccessTokenAsync(default);
        await entered.Task;
        var two = session.AccessTokenAsync(default);
        response.SetResult(Json("{\"access_token\":\"renewed\",\"expires_in\":120}"));
        Assert.Equal(new[] { "renewed", "renewed" }, await Task.WhenAll(one, two));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("rotate-me", secret!);
        clock.Utc = clock.Utc.AddSeconds(90);
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = session.AccessTokenAsync(default);
        await entered.Task;
        secret = null;
        response.SetResult(Json("{\"access_token\":\"stale\",\"expires_in\":3600}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale);
        Assert.Null(secret);
    }

    [Fact]
    public async Task MissingExpiryAndSecretStoreFailureNeverPublishUsableCredentialsOrSecretErrors()
    {
        var handler = new HttpFixture { Reply = _ => Task.FromResult(Json("{\"access_token\":\"do-not-log\",\"refresh_token\":\"private\"}")) };
        using var http = new HttpClient(handler);
        string? secret = null;
        var session = new LongbridgeSession(new() { ClientId = "client", BaseUrl = "https://auth.invalid" }, http,
            () => secret, _ => throw new IOException("store unavailable"), new Clock());
        var begin = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.CompleteAuthorizationAsync(Callback(Query(begin.AuthorizationUri)["state"]), default));
        Assert.DoesNotContain("do-not-log", error.Message);
        handler.Reply = _ => Task.FromResult(Json("{\"access_token\":\"do-not-log\",\"expires_in\":3600}"));
        begin = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await Assert.ThrowsAsync<IOException>(() => session.CompleteAuthorizationAsync(Callback(Query(begin.AuthorizationUri)["state"]), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AccessTokenAsync(default));
    }

    [Fact]
    public async Task PublicClientRegistrationPersistsOnlyPublicConfiguration()
    {
        string? secret = null;
        var handler = new HttpFixture { Reply = _ => Task.FromResult(Json("{\"client_id\":\"registered-public-client\"}")) };
        using var http = new HttpClient(handler);
        var settings = new LongbridgeSettings { BaseUrl = "https://auth.invalid" };
        var session = new LongbridgeSession(settings, http, () => secret, value => secret = value, new Clock());
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        Assert.Equal("registered-public-client", settings.ClientId);
        Assert.Equal("registered-public-client", auth.ClientId);
        using var registration = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.Equal("none", registration.RootElement.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("http://localhost:60355/callback", registration.RootElement.GetProperty("redirect_uris")[0].GetString());
        Assert.Null(secret);
        handler.Reply = _ => Task.FromResult(Json("{\"access_token\":\"private-access-value\",\"refresh_token\":\"private-refresh-value\",\"expires_in\":3600}"));
        await session.CompleteAuthorizationAsync(Callback(Query(auth.AuthorizationUri)["state"]), default);
        string serialized = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("private-access-value", serialized);
        Assert.DoesNotContain("private-refresh-value", serialized);
        Assert.Contains("private-refresh-value", secret!);
    }

    [Fact]
    public async Task CancellationAndRetirementDiscardHeldTokenResponsesWithoutClearingCurrentSecret()
    {
        string? secret = null;
        var handler = new HttpFixture();
        using var http = new HttpClient(handler);
        var clock = new Clock();
        var session = new LongbridgeSession(new() { ClientId = "client", BaseUrl = "https://auth.invalid" }, http,
            () => secret, value => secret = value, clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Reply = _ => { entered.TrySetResult(); return response.Task; };
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        using var cancellation = new CancellationTokenSource();
        var complete = session.CompleteAuthorizationAsync(Callback(Query(auth.AuthorizationUri)["state"]), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        response.SetResult(Json("{\"access_token\":\"cancelled\",\"refresh_token\":\"cancelled-refresh\",\"expires_in\":120}"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => complete);
        Assert.Null(secret);
        handler.Reply = _ => Task.FromResult(Json("{\"access_token\":\"accepted\",\"refresh_token\":\"accepted-refresh\",\"expires_in\":120}"));
        auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(Callback(Query(auth.AuthorizationUri)["state"]), default);
        string saved = secret!;
        clock.Utc = clock.Utc.AddSeconds(90);
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Reply = _ => { entered.TrySetResult(); return response.Task; };
        var refresh = session.AccessTokenAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Retire();
        response.SetResult(Json("{\"access_token\":\"retired-late\",\"expires_in\":3600}"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Equal(saved, secret);
        Assert.False(session.HasAuthorization);
    }

    [Fact]
    public async Task TokensCannotCrossClientRegionOrQuoteResourceAndInPlaceContextEditsAreRejected()
    {
        string? secret = null;
        var handler = new HttpFixture { Reply = _ => Task.FromResult(Json("{\"access_token\":\"bound-access\",\"refresh_token\":\"bound-refresh\",\"expires_in\":3600}")) };
        using var http = new HttpClient(handler);
        var settings = new LongbridgeSettings { ClientId = "client-one", BaseUrl = "https://auth.invalid", QuoteWsUrl = "wss://quote.invalid/v2" };
        var original = new LongbridgeSession(settings, http, () => secret, value => secret = value, new Clock());
        var auth = await original.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await original.CompleteAuthorizationAsync(Callback(Query(auth.AuthorizationUri)["state"]), default);
        foreach (var changed in new[]
        {
            new LongbridgeSettings { ClientId = "client-two", BaseUrl = "https://auth.invalid", QuoteWsUrl = "wss://quote.invalid/v2" },
            new LongbridgeSettings { ClientId = "client-one", Region = "cn", BaseUrl = "https://auth.invalid", QuoteWsUrl = "wss://quote.invalid/v2" },
            new LongbridgeSettings { ClientId = "client-one", BaseUrl = "https://other-auth.invalid", QuoteWsUrl = "wss://quote.invalid/v2" },
            new LongbridgeSettings { ClientId = "client-one", BaseUrl = "https://auth.invalid", QuoteWsUrl = "wss://other-quote.invalid/v2" },
        })
        {
            var session = new LongbridgeSession(changed, http, () => secret, _ => throw new InvalidOperationException("Unexpected secret write."), new Clock());
            await Assert.ThrowsAsync<InvalidDataException>(() => session.AccessTokenAsync(default));
        }
        settings.Region = "cn";
        await Assert.ThrowsAsync<InvalidOperationException>(() => original.AccessTokenAsync(default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NewAuthorizationSupersedesAnAlreadyExchangingCode()
    {
        string? secret = null;
        var handler = new HttpFixture();
        using var http = new HttpClient(handler);
        var session = new LongbridgeSession(new() { ClientId = "client", BaseUrl = "https://auth.invalid" }, http,
            () => secret, value => secret = value, new Clock());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Reply = _ => { entered.TrySetResult(); return response.Task; };
        var old = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        var completing = session.CompleteAuthorizationAsync(Callback(Query(old.AuthorizationUri)["state"]), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        response.SetResult(Json("{\"access_token\":\"superseded\",\"expires_in\":3600}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => completing);
        Assert.Null(secret);
        handler.Reply = _ => Task.FromResult(Json("{\"access_token\":\"current\",\"refresh_token\":\"current-refresh\",\"expires_in\":3600}"));
        await session.CompleteAuthorizationAsync(Callback(Query(current.AuthorizationUri)["state"]), default);
        Assert.Equal("current", await session.AccessTokenAsync(default));
        Assert.DoesNotContain("superseded", secret!);
    }

    [Fact]
    public async Task RefreshFailureKeepsSavedCredentialsAndSuccessfulRotationPersistsNewRefreshToken()
    {
        string? secret = null;
        var handler = new HttpFixture { Reply = _ => Task.FromResult(Json("{\"access_token\":\"old\",\"refresh_token\":\"old-refresh\",\"expires_in\":120}")) };
        using var http = new HttpClient(handler);
        var clock = new Clock();
        var session = new LongbridgeSession(new() { ClientId = "client", BaseUrl = "https://auth.invalid" }, http,
            () => secret, value => secret = value, clock);
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(Callback(Query(auth.AuthorizationUri)["state"]), default);
        string before = secret!;
        clock.Utc = clock.Utc.AddSeconds(90);
        handler.Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error_description\":\"old-refresh private value must not be logged\"}"),
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => session.AccessTokenAsync(default));
        Assert.DoesNotContain("old-refresh", error.Message);
        Assert.Equal(before, secret);
        handler.Reply = _ => Task.FromResult(Json("{\"access_token\":\"renewed\",\"refresh_token\":\"rotated-refresh\",\"expires_in\":3600}"));
        Assert.Equal("renewed", await session.AccessTokenAsync(default));
        Assert.Contains("rotated-refresh", secret!);
        Assert.DoesNotContain("old-refresh", secret!);
    }

    internal static Uri Callback(string state) => new($"http://localhost:60355/callback?state={Uri.EscapeDataString(state)}&code=secret-code");
    internal static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));
    internal static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    internal sealed class HttpFixture : HttpMessageHandler
    {
        public List<(Uri Uri, string Body, string? Bearer)> Requests { get; } = new();
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Reply { get; set; } = _ => throw new InvalidOperationException("Unexpected HTTP request.");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.Parameter));
            return await Reply(request);
        }
    }
}
