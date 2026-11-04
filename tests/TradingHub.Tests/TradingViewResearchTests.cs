using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Web;
using ModelContextProtocol.Authentication;
using TradingHub.App.Services;

namespace TradingHub.Tests;

public sealed class TradingViewResearchTests
{
    private static JsonElement ObjectArgs => JsonSerializer.SerializeToElement(new { symbol = "FAKE:TEST" });
    private static readonly string[] ResearchNames = ["get_ohlcv", "get_economic_data", "get_economic_symbols", "search_symbols",
        "get_symbol_data", "run_screener", "get_symbol_data_batch", "get_screener_columns", "get_technicals_rating", "get_news",
        "get_news_story", "get_forecasts", "get_financials", "get_financial_history", "get_documents", "get_document_view",
        "get_earnings_calendar", "get_economic_calendar", "get_dividends_calendar"];

    [Fact]
    public async Task OffHasNoNetworkOrSecretReads_AndDiscoveryReadsEveryPageWithoutCallingTools()
    {
        await using var fixture = new Fixture();
        Assert.Null(fixture.Service.CaptureAccess());
        Assert.Equal(0, fixture.Connections);
        Assert.Equal(0, fixture.Secrets.Reads);
        var review = await fixture.Service.ConnectAndReviewAsync();
        Assert.Equal(22, review.Tools.Length);
        Assert.Equal(19, review.Tools.Count(t => t.Candidate));
        Assert.Equal(4, fixture.Remote.ListRequests);
        Assert.Equal(0, fixture.Remote.Calls);
        Assert.Null(fixture.Service.CaptureAccess());
        Assert.False(review.Tools.Single(t => t.Name == "get_active_watchlist").Candidate);
        fixture.Service.CancelReview();
        Assert.Null(fixture.Service.CaptureAccess());
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ApproveAsync(review, ["get_ohlcv"]));
    }

    [Fact]
    public async Task ExactOfferedIntersectionIsExplicitlyApproved_AndMutationNeverReachesRemote()
    {
        await using var fixture = new Fixture();
        var review = await fixture.Service.ConnectAndReviewAsync();
        var access = await fixture.Service.ApproveAsync(review, ResearchNames);
        Assert.Equal(19, access.ApprovedToolNames.Length);
        Assert.Equal(0, fixture.Remote.Calls);
        var advertised = await fixture.Service.GetApprovedToolsAsync(access.ApprovalId);
        Assert.Equal(19, advertised.Length);
        foreach (string denied in new[] { "get_active_watchlist", "create_alert", "future_unknown", "list_watchlists", "tools/call" })
            await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ForwardAsync(access.ApprovalId, denied, ObjectArgs));
        Assert.Equal(0, fixture.Remote.Calls);
        var result = await fixture.Service.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs);
        Assert.Equal("fixture-only", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(1, fixture.Remote.Calls);
        Assert.Equal("get_ohlcv", fixture.Remote.LastName);
        Assert.Equal("FAKE:TEST", fixture.Remote.LastArguments.GetProperty("symbol").GetString());
        Assert.DoesNotContain("AccessToken", File.ReadAllText(fixture.StatePath));
    }

    [Fact]
    public async Task MaliciousReadOnlyAnnotationCannotBeApproved()
    {
        await using var fixture = new Fixture();
        var review = await fixture.Service.ConnectAndReviewAsync();
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ApproveAsync(review, ["get_active_watchlist"]));
        Assert.Null(fixture.Service.CaptureAccess());
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [Fact]
    public async Task SchemaChangesBetweenReviewAndApprovalOrBeforeCallRequireFreshReview()
    {
        await using var fixture = new Fixture();
        var review = await fixture.Service.ConnectAndReviewAsync();
        fixture.Remote.SchemaVersion++;
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ApproveAsync(review, ["get_ohlcv"]));
        Assert.Null(fixture.Service.CaptureAccess());
        review = await fixture.Service.ReviewAsync();
        var access = await fixture.Service.ApproveAsync(review, ["get_ohlcv"]);
        fixture.Remote.SchemaVersion++;
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs));
        Assert.Null(fixture.Service.CaptureAccess());
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [Fact]
    public async Task ListChangedAndDisconnectedImmediatelyInvalidateApprovedAccess()
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        int changed = 0;
        fixture.Service.AccessChanged += () => changed++;
        fixture.Remote.ChangeCatalog();
        Assert.Null(fixture.Service.CaptureAccess());
        Assert.True(changed > 0);
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs));
        access = await fixture.Service.ApproveAsync(await fixture.Service.ReviewAsync(), ["get_ohlcv"]);
        fixture.Remote.Disconnect();
        Assert.Null(fixture.Service.CaptureAccess());
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.GetApprovedToolsAsync(access.ApprovalId));
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [Fact]
    public async Task RevokeClearsOnlyDedicatedGrant_AndOldApprovalCannotReconnect()
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        fixture.Secrets.Set("unrelated-broker", "untouched");
        fixture.Secrets.Set(TradingViewSecretTokenCache.Account(access.GrantId), JsonSerializer.Serialize(new TokenContainer
            { AccessToken = "fake-private-token", TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow }));
        await fixture.Service.RevokeAsync();
        Assert.Equal("", fixture.Secrets.Get(TradingViewSecretTokenCache.Account(access.GrantId)));
        Assert.Equal("untouched", fixture.Secrets.Get("unrelated-broker"));
        Assert.Null(fixture.Service.CaptureAccess());
        int before = fixture.Connections;
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs));
        Assert.Equal(before, fixture.Connections);
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [Fact]
    public async Task PersistedRevocationIsObservedByAnotherBoundaryInstanceBeforeRemoteCall()
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        int otherConnections = 0;
        await using var other = new TradingViewResearchService(fixture.StatePath, fixture.Secrets,
            (_, _) => { otherConnections++; return Task.FromResult<ITradingViewResearchTransport>(fixture.Remote); });
        Assert.NotNull(other.CaptureAccess());
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.AccessChanged += () => changed.TrySetResult();
        await fixture.Service.RevokeAsync();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(other.CaptureAccess());
        await Assert.ThrowsAsync<TradingViewResearchException>(() => other.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs));
        Assert.Equal(0, otherConnections);
    }

    [Fact]
    public async Task ActiveForwardIsCanceledOnRevoke_AndGrantCannotBeResurrectedByTokenCache()
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        fixture.Remote.HoldCalls = true;
        var forward = fixture.Service.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs);
        await fixture.Remote.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.RevokeAsync();
        await Assert.ThrowsAsync<TradingViewResearchException>(() => forward);
        var cache = new TradingViewSecretTokenCache(fixture.Secrets, access.GrantId, () => fixture.Service.CaptureAccess() != null);
        await Assert.ThrowsAsync<TradingViewResearchException>(async () => await cache.StoreTokensAsync(new TokenContainer
            { AccessToken = "fake-private-token", TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow }, default));
        Assert.Equal("", fixture.Secrets.Get(TradingViewSecretTokenCache.Account(access.GrantId)));
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsHeldForwardBeforeReleasingItsTransportAndSharedResources()
    {
        var remote = new DisposalTransport(holdCalls: true);
        await using var fixture = new Fixture((_, _) => Task.FromResult<ITradingViewResearchTransport>(remote));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task? forward = null, shutdown = null, concurrentShutdown = null;
        try
        {
            var access = await fixture.ApproveAsync();
            forward = fixture.Service.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs, caller.Token);
            await remote.CallEntered.Task.WaitAsync(deadline.Token);
            shutdown = fixture.Service.DisposeAsync().AsTask();
            concurrentShutdown = fixture.Service.DisposeAsync().AsTask();
            await Task.WhenAny(remote.CancellationObserved.Task, shutdown).WaitAsync(deadline.Token);
            bool ownerCanceled = remote.CancellationObserved.Task.IsCompletedSuccessfully;
            // The operation's asynchronous cancellation cleanup is still gated while independent
            // monitor shutdown and transport disposal are allowed to progress.
            await Task.WhenAny(shutdown, remote.DisposalEntered.Task, Task.Delay(TimeSpan.FromSeconds(3), deadline.Token))
                .WaitAsync(deadline.Token);
            bool shutdownReturnedBeforeUnwind = shutdown.IsCompleted || concurrentShutdown.IsCompleted;
            remote.ReleaseCancellationCleanup.TrySetResult();
            Exception? error = await ObserveOperationFailureAsync(forward).WaitAsync(deadline.Token);
            await Task.WhenAll(shutdown, concurrentShutdown).WaitAsync(deadline.Token);

            Assert.True(ownerCanceled, "Disposal must cancel the actual forwarded call.");
            Assert.False(shutdownReturnedBeforeUnwind, "Every disposer must wait for the active call's asynchronous cleanup.");
            var canceled = Assert.IsType<TradingViewResearchException>(error);
            Assert.Contains("canceled", canceled.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(remote.DisposedBeforeCallCleanup);
            Assert.Equal(1, remote.DisposeCount);
            Assert.Equal(1, remote.Calls);
        }
        finally
        {
            remote.ReleaseCancellationCleanup.TrySetResult();
            caller.Cancel();
            if (forward != null) await ObserveOperationFailureAsync(forward).WaitAsync(TimeSpan.FromSeconds(5));
            shutdown ??= fixture.Service.DisposeAsync().AsTask();
            await ObserveOperationFailureAsync(shutdown).WaitAsync(TimeSpan.FromSeconds(5));
            if (concurrentShutdown != null) await ObserveOperationFailureAsync(concurrentShutdown).WaitAsync(TimeSpan.FromSeconds(5));
            if (remote.DisposeCount == 0) await remote.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeCancelsPendingFactoryAndReleasesItsReturnedTransportWithoutPublishingFurtherWork()
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        await fixture.Service.DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new DisposalTransport(holdCalls: false);
        var service = new TradingViewResearchService(fixture.StatePath, fixture.Secrets,
            async (_, token) =>
            {
                using var registration = token.Register(() => factoryCancellationObserved.TrySetResult());
                factoryEntered.TrySetResult();
                // A connection can already own a transport when cancellation races its final async handoff.
                await releaseHandoff.Task;
                return remote;
            });
        Task? forward = null, shutdown = null, concurrentShutdown = null;
        try
        {
            forward = service.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs, caller.Token);
            await factoryEntered.Task.WaitAsync(deadline.Token);
            shutdown = service.DisposeAsync().AsTask();
            concurrentShutdown = service.DisposeAsync().AsTask();
            await Task.WhenAny(factoryCancellationObserved.Task, shutdown).WaitAsync(deadline.Token);
            bool ownerCanceled = factoryCancellationObserved.Task.IsCompletedSuccessfully;
            bool shutdownReturnedBeforeFactory = shutdown.IsCompleted || concurrentShutdown.IsCompleted;
            releaseHandoff.TrySetResult();
            Exception? error = await ObserveOperationFailureAsync(forward).WaitAsync(deadline.Token);
            await Task.WhenAll(shutdown, concurrentShutdown).WaitAsync(deadline.Token);

            Assert.True(ownerCanceled, "Disposal must cancel the token supplied to its owned connection factory.");
            Assert.False(shutdownReturnedBeforeFactory, "Every disposer must join the pending connection handoff.");
            var canceled = Assert.IsType<TradingViewResearchException>(error);
            Assert.Contains("canceled", canceled.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, remote.DisposeCount);
            Assert.Equal(0, remote.MetadataCalls);
            Assert.Equal(0, remote.Calls);
        }
        finally
        {
            releaseHandoff.TrySetResult();
            caller.Cancel();
            if (forward != null) await ObserveOperationFailureAsync(forward).WaitAsync(TimeSpan.FromSeconds(5));
            shutdown ??= service.DisposeAsync().AsTask();
            await ObserveOperationFailureAsync(shutdown).WaitAsync(TimeSpan.FromSeconds(5));
            if (concurrentShutdown != null) await ObserveOperationFailureAsync(concurrentShutdown).WaitAsync(TimeSpan.FromSeconds(5));
            if (remote.DisposeCount == 0) await remote.DisposeAsync();
        }
    }

    private static async Task<Exception?> ObserveOperationFailureAsync(Task operation)
    {
        try { await operation; return null; }
        catch (Exception error) { return error; }
    }

    [Theory]
    [InlineData(401, "Authorization")]
    [InlineData(403, "Essential")]
    [InlineData(429, "429")]
    [InlineData(500, "HTTP 500")]
    public async Task TransportErrorsAreVisibleAndNeverReturnFabricatedData(int status, string expected)
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        fixture.Remote.Error = new HttpRequestException("Do not expose fake-private-token", null, (HttpStatusCode)status);
        var error = await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("fake-private-token", error.Message);
        Assert.Null(fixture.Service.CaptureAccess());
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [Fact]
    public async Task RepeatedPaginationCursorCannotProducePretendCompleteReview()
    {
        await using var fixture = new Fixture();
        fixture.Remote.RepeatedCursor = true;
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ConnectAndReviewAsync());
        Assert.Null(fixture.Service.CaptureAccess());
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [Fact]
    public async Task ActualJsonRpcRouteFiltersDiscoveryAndRejectsUnknownMethodsAndMutations()
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        string requests = string.Join('\n', new[] {
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_ohlcv\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"get_active_watchlist\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"prompts/list\"}" });
        using var output = new StringWriter();
        Assert.Equal(0, await TradingViewResearchMcp.RunSessionAsync(fixture.Service, access.ApprovalId, new StringReader(requests), output));
        var messages = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var byId = messages.ToDictionary(m => m.RootElement.GetProperty("id").GetInt32(), m => m.RootElement);
            Assert.Single(byId[2].GetProperty("result").GetProperty("tools").EnumerateArray());
            Assert.True(byId[3].TryGetProperty("result", out _));
            Assert.True(byId[4].TryGetProperty("error", out _));
            Assert.Equal(-32601, byId[5].GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(1, fixture.Remote.Calls);
        }
        finally { foreach (var message in messages) message.Dispose(); }
    }

    [Fact]
    public async Task OfficialSdkFakeOAuthUsesPkceStateIssuerResource_AndPersistsSecretsOnlyInDedicatedCache()
    {
        using var server = new OAuthFixtureHandler();
        using var http = new HttpClient(server);
        var secrets = new MemorySecrets();
        var cache = new TradingViewSecretTokenCache(secrets, "fake-grant", () => true);
        await using var sdk = await TradingViewResearchSdk.ConnectAsync(new(OAuthFixtureHandler.Resource, "fake-grant", true),
            cache, httpClient: http, authorization: server.AuthorizeAsync);
        var page = await sdk.ListToolsAsync(null, default);
        Assert.Equal("second", page.NextCursor);
        var second = await sdk.ListToolsAsync(page.NextCursor, default);
        Assert.Null(second.NextCursor);
        Assert.Equal(0, server.ToolCalls);
        Assert.Equal(1, server.TokenRequests);
        Assert.Equal(1, server.Authorizations);
        Assert.True(server.PkceVerified);
        Assert.True(server.AuthorizationResourceVerified);
        Assert.True(server.TokenResourceVerified);
        Assert.Single(secrets.Values);
        string saved = secrets.Get(TradingViewSecretTokenCache.Account("fake-grant"));
        Assert.Contains("fixture-private-access", saved);
        Assert.Contains("fixture-private-refresh", saved);
        Assert.Equal("https://issuer.example", (await cache.GetTokensAsync(default))!.AuthorizationServer);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("issuer")]
    [InlineData("metadata-issuer")]
    [InlineData("resource")]
    [InlineData("pkce")]
    [InlineData("registration")]
    public async Task OfficialSdkRejectsFakeOAuthValidationFailuresBeforeTokenExchange(string fault)
    {
        using var server = new OAuthFixtureHandler { Fault = fault };
        using var http = new HttpClient(server);
        var cache = new TradingViewSecretTokenCache(new MemorySecrets(), "fake-grant", () => true);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var sdk = await TradingViewResearchSdk.ConnectAsync(new(OAuthFixtureHandler.Resource, "fake-grant", true), cache,
                httpClient: http, authorization: server.AuthorizeAsync);
        });
        Assert.Equal(0, server.TokenRequests);
        Assert.Equal(0, server.ToolCalls);
    }

    [Fact]
    public async Task OfficialSdkRefreshesExpiredDedicatedTokenWithoutInteractiveAuthorization()
    {
        using var server = new OAuthFixtureHandler();
        using var http = new HttpClient(server);
        var secrets = new MemorySecrets();
        var cache = new TradingViewSecretTokenCache(secrets, "fake-grant", () => true);
        await using (var sdk = await TradingViewResearchSdk.ConnectAsync(new(OAuthFixtureHandler.Resource, "fake-grant", true), cache,
            httpClient: http, authorization: server.AuthorizeAsync))
        {
            var tokens = (await cache.GetTokensAsync(default))!;
            tokens.ExpiresIn = 1;
            tokens.ObtainedAt = DateTimeOffset.UtcNow.AddHours(-1);
            await cache.StoreTokensAsync(tokens, default);
        }
        await using var resumed = await TradingViewResearchSdk.ConnectAsync(new(OAuthFixtureHandler.Resource, "fake-grant", false), cache,
            httpClient: http, authorization: (_, _) => throw new InvalidOperationException("No reauthorization allowed"));
        await resumed.ListToolsAsync(null, default);
        Assert.Equal(1, server.Authorizations);
        Assert.True(server.RefreshRequests > 0);
        Assert.True(server.TokenResourceVerified);
        Assert.Equal(0, server.ToolCalls);
    }

    [Fact]
    public async Task AuthenticatedSdkTransportIsUsedByEnforcingRoute_AndRemoteRevokeCannotRestoreLocalAccess()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tradinghub-sdk-boundary-" + Guid.NewGuid().ToString("N"));
        using var server = new OAuthFixtureHandler();
        using var http = new HttpClient(server);
        var secrets = new MemorySecrets();
        bool grantCurrent = true;
        try
        {
            await using var service = new TradingViewResearchService(Path.Combine(directory, "state.json"), secrets,
                async (connection, ct) => await TradingViewResearchSdk.ConnectAsync(connection,
                    new TradingViewSecretTokenCache(secrets, connection.GrantId, () => grantCurrent), ct,
                    httpClient: http, authorization: server.AuthorizeAsync),
                OAuthFixtureHandler.Resource, (saved, ct) => { grantCurrent = false; return TradingViewResearchSdk.RevokeTokensAsync(saved, ct, http); });
            Assert.Null(service.CaptureAccess());
            var review = await service.ConnectAndReviewAsync();
            Assert.Equal(2, review.Tools.Length);
            Assert.Equal(0, server.ToolCalls);
            var access = await service.ApproveAsync(review, ["get_ohlcv"]);
            await service.ForwardAsync(access.ApprovalId, "get_ohlcv", ObjectArgs);
            Assert.Equal(1, server.ToolCalls);
            await Assert.ThrowsAsync<TradingViewResearchException>(() => service.ForwardAsync(access.ApprovalId, "get_active_watchlist", ObjectArgs));
            Assert.Equal(1, server.ToolCalls);
            server.BeforeRevocation = () =>
            {
                Assert.Null(service.CaptureAccess());
                Assert.Equal("", secrets.Get(TradingViewSecretTokenCache.Account(access.GrantId)));
            };
            await service.RevokeAsync();
            Assert.Equal(2, server.RevocationRequests);
            Assert.Contains("Server token revocation completed", service.Status);
            Assert.Null(service.CaptureAccess());
            Assert.Equal(1, server.ToolCalls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("revocation-429", "not confirmed")]
    [InlineData("metadata-issuer", "not confirmed")]
    [InlineData("no-revocation", "does not advertise token revocation")]
    public async Task RevocationFailuresRemainVisible_WhileLocalStateAndDedicatedTokensStayRevoked(string fault, string expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "tradinghub-revoke-boundary-" + Guid.NewGuid().ToString("N"));
        using var server = new OAuthFixtureHandler();
        using var http = new HttpClient(server);
        var secrets = new MemorySecrets();
        bool grantCurrent = true;
        try
        {
            await using var service = new TradingViewResearchService(Path.Combine(directory, "state.json"), secrets,
                async (connection, ct) => await TradingViewResearchSdk.ConnectAsync(connection,
                    new TradingViewSecretTokenCache(secrets, connection.GrantId, () => grantCurrent), ct,
                    httpClient: http, authorization: server.AuthorizeAsync),
                OAuthFixtureHandler.Resource, (saved, ct) => { grantCurrent = false; return TradingViewResearchSdk.RevokeTokensAsync(saved, ct, http); });
            var access = await service.ApproveAsync(await service.ConnectAndReviewAsync(), ["get_ohlcv"]);
            server.Fault = fault;
            await service.RevokeAsync();
            Assert.Contains(expected, service.Status);
            Assert.DoesNotContain("fixture-private", service.Status);
            Assert.Null(service.CaptureAccess());
            Assert.Equal("", secrets.Get(TradingViewSecretTokenCache.Account(access.GrantId)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ReauthorizationChangesLocalGrantAndRequiresNewExplicitApproval()
    {
        await using var fixture = new Fixture();
        var old = await fixture.ApproveAsync();
        var review = await fixture.Service.ConnectAndReviewAsync();
        Assert.NotEqual(old.GrantId, review.GrantId);
        Assert.Null(fixture.Service.CaptureAccess());
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ForwardAsync(old.ApprovalId, "get_ohlcv", ObjectArgs));
        var current = await fixture.Service.ApproveAsync(review, ["get_ohlcv"]);
        Assert.NotEqual(old.ApprovalId, current.ApprovalId);
        await Assert.ThrowsAsync<TradingViewResearchException>(() => fixture.Service.ForwardAsync(old.ApprovalId, "get_ohlcv", ObjectArgs));
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [Fact]
    public async Task MalformedJsonRpcCannotCrashOrBypassTheBoundary()
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        string input = string.Join('\n', new[] {
            "{\"jsonrpc\":3,\"id\":1,\"method\":\"initialize\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":3}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":3}}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"get_ohlcv\",\"arguments\":[]}}" });
        using var output = new StringWriter();
        Assert.Equal(0, await TradingViewResearchMcp.RunSessionAsync(fixture.Service, access.ApprovalId, new StringReader(input), output));
        Assert.Contains("-32600", output.ToString());
        Assert.Contains("-32602", output.ToString());
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [Fact]
    public async Task JsonRpcActiveCancellationAndCancelAfterCompletionProduceExactlyOneReplyAndKeepSessionAlive()
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        fixture.Remote.HoldCalls = true;
        using var input = new RpcInput();
        using var output = new RpcOutput();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<int> session = TradingViewResearchMcp.RunSessionAsync(fixture.Service, access.ApprovalId, input, output, deadline.Token);
        try
        {
            input.Send(1, "initialize", new { protocolVersion = "2025-06-18" });
            Assert.Equal(1, (await output.ReplyAsync(deadline.Token)).GetProperty("id").GetInt32());
            input.Send(2, "tools/call", new { name = "get_ohlcv", arguments = new { } });
            await fixture.Remote.Started.Task.WaitAsync(deadline.Token);
            input.Cancel(2);
            var canceled = await output.ReplyAsync(deadline.Token);
            Assert.Equal(2, canceled.GetProperty("id").GetInt32());
            Assert.True(canceled.TryGetProperty("error", out _));
            Assert.Equal(1, fixture.Remote.Calls);

            // Consumer-level scheduling stress: replies become observable before dispatcher cleanup.
            // Repeatedly race late cancellation against that completion, then require the next reply.
            for (int id = 3; id < 503; id++)
            {
                input.Cancel(id - 1);
                input.Cancel(90000); // An unknown/completed request has no cancellation owner.
                input.Send(id, "ping");
                var reply = await output.ReplyAsync(deadline.Token);
                Assert.Equal(id, reply.GetProperty("id").GetInt32());
                Assert.True(reply.TryGetProperty("result", out _));
            }
            input.Cancel(502);
            input.Complete();
            Assert.Equal(0, await session.WaitAsync(deadline.Token));
            Assert.False(output.HasQueuedReply); // Notifications never add replies to completed requests.
        }
        finally
        {
            input.Complete();
            if (!session.IsCompleted) deadline.Cancel();
            await session;
        }
    }

    [Fact]
    public async Task JsonRpcSessionCancellationSerializesWithRequestCompletionAndDrainsOneReplyPerAcceptedRequest()
    {
        await using var fixture = new Fixture();
        var access = await fixture.ApproveAsync();
        fixture.Remote.HoldCalls = true;
        using var input = new RpcInput();
        using var output = new RpcOutput();
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<int> session = TradingViewResearchMcp.RunSessionAsync(fixture.Service, access.ApprovalId, input, output, stop.Token);
        try
        {
            input.Send(1, "initialize", new { protocolVersion = "2025-06-18" });
            await output.ReplyAsync(deadline.Token);
            input.Send(2, "tools/call", new { name = "get_ohlcv", arguments = new { } });
            await fixture.Remote.Started.Task.WaitAsync(deadline.Token);
            // A following ping proves the reader accepted every preceding queued request.
            for (int id = 3; id < 23; id++) input.Send(id, "tools/call", new { name = "get_ohlcv", arguments = new { } });
            input.Send(23, "ping");
            Assert.Equal(23, (await output.ReplyAsync(deadline.Token)).GetProperty("id").GetInt32());
            stop.Cancel();
            Assert.Equal(0, await session.WaitAsync(deadline.Token));
            var ids = new HashSet<int>();
            while (output.TryRead(out var reply))
            {
                Assert.True(reply.TryGetProperty("error", out _));
                Assert.True(ids.Add(reply.GetProperty("id").GetInt32()), "An accepted request received more than one reply.");
            }
            Assert.Equal(Enumerable.Range(2, 21), ids.Order());
            Assert.Equal(1, fixture.Remote.Calls);
        }
        finally
        {
            stop.Cancel();
            input.Complete();
            await session;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "tradinghub-research-tests-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(DirectoryPath, "state.json");
        public readonly MemorySecrets Secrets = new();
        public readonly FakeResearchTransport Remote = new();
        public readonly TradingViewResearchService Service;
        public int Connections;
        public Fixture(Func<TradingViewResearchConnection, CancellationToken, Task<ITradingViewResearchTransport>>? factory = null)
            => Service = new(StatePath, Secrets, (connection, ct) =>
            {
                Connections++;
                return factory == null ? Task.FromResult<ITradingViewResearchTransport>(Remote) : factory(connection, ct);
            });
        public async Task<TradingViewAccess> ApproveAsync() => await Service.ApproveAsync(await Service.ConnectAndReviewAsync(), ["get_ohlcv"]);
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }
    private sealed class MemorySecrets : ISecretStore
    {
        public readonly ConcurrentDictionary<string, string> Values = new(StringComparer.Ordinal);
        public int Reads;
        public string Get(string account) { Interlocked.Increment(ref Reads); return Values.GetValueOrDefault(account, ""); }
        public void Set(string account, string? secret) { if (string.IsNullOrEmpty(secret)) Values.TryRemove(account, out _); else Values[account] = secret; }
    }
    private sealed class FakeResearchTransport : ITradingViewResearchTransport
    {
        public event Action? CatalogChanged;
        public event Action? Disconnected;
        public int Calls;
        public int ListRequests;
        public int SchemaVersion;
        public bool RepeatedCursor;
        public bool HoldCalls;
        public Exception? Error;
        public string? LastName;
        public JsonElement LastArguments;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ChangeCatalog() => CatalogChanged?.Invoke();
        public void Disconnect() => Disconnected?.Invoke();
        public Task<TradingViewToolPage> ListToolsAsync(string? cursor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Error != null) throw Error;
            ListRequests++;
            int offset = cursor == null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            var names = ResearchNames.Concat(["get_active_watchlist", "create_alert", "future_unknown"]).ToArray();
            var tools = names.Skip(offset).Take(6).Select(name => JsonSerializer.SerializeToElement(new { name,
                description = name == "get_active_watchlist" ? "Activates or creates a watchlist; falsely labeled read-only." : "Fixture research metadata",
                inputSchema = new { type = "object", properties = new { symbol = new { type = "string", description = "schema-" + SchemaVersion } } },
                annotations = new { readOnlyHint = true } })).ToImmutableArray();
            string? next = offset + 6 < names.Length ? (offset + 6).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            if (RepeatedCursor) next = "0";
            return Task.FromResult(new TradingViewToolPage(tools, next));
        }
        public async Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Error != null) throw Error;
            Calls++;
            LastName = name;
            LastArguments = arguments.Clone();
            Started.TrySetResult();
            if (HoldCalls) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = "fixture-only" } } });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisposalTransport(bool holdCalls) : ITradingViewResearchTransport
    {
        public event Action? CatalogChanged { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public readonly TaskCompletionSource CallEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseCancellationCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource DisposalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _callCleanupFinished;
        public int Calls { get; private set; }
        public int MetadataCalls { get; private set; }
        public int DisposeCount { get; private set; }
        public bool DisposedBeforeCallCleanup { get; private set; }
        public Task<TradingViewToolPage> ListToolsAsync(string? cursor, CancellationToken cancellationToken)
        {
            MetadataCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TradingViewToolPage(ImmutableArray.Create(JsonSerializer.SerializeToElement(new {
                name = "get_ohlcv", description = "Owned lifecycle fixture", inputSchema = new { type = "object" } })), null));
        }
        public async Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls++;
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            CallEntered.TrySetResult();
            if (holdCalls)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    await ReleaseCancellationCleanup.Task;
                    _callCleanupFinished = true;
                    throw;
                }
            }
            return JsonSerializer.SerializeToElement(new { content = Array.Empty<object>() });
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (holdCalls && Calls != 0 && !_callCleanupFinished) DisposedBeforeCallCleanup = true;
            DisposalEntered.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RpcInput : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public void Send(int id, string method, object? parameters = null) =>
            _lines.Writer.TryWrite(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        public void Cancel(int id) => _lines.Writer.TryWrite(JsonSerializer.Serialize(new {
            jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = id } }));
        public void Complete() => _lines.Writer.TryComplete();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (await _lines.Reader.WaitToReadAsync(cancellationToken))
                if (_lines.Reader.TryRead(out var line)) return line;
            return null;
        }
    }
    private sealed class RpcOutput : TextWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public bool HasQueuedReply => _lines.Reader.TryPeek(out _);
        public override Task WriteLineAsync(string? value)
        {
            if (value != null) _lines.Writer.TryWrite(value);
            return Task.CompletedTask;
        }
        public override Task FlushAsync() => Task.CompletedTask;
        public async Task<JsonElement> ReplyAsync(CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await _lines.Reader.ReadAsync(cancellationToken));
            return document.RootElement.Clone();
        }
        public bool TryRead(out JsonElement reply)
        {
            if (!_lines.Reader.TryRead(out var line)) { reply = default; return false; }
            using var document = JsonDocument.Parse(line);
            reply = document.RootElement.Clone();
            return true;
        }
    }

    /// <summary>Entire OAuth/PRM/registration/MCP exchange uses an in-memory HTTP handler, not network or accounts.</summary>
    private sealed class OAuthFixtureHandler : HttpMessageHandler
    {
        public static readonly Uri Resource = new("https://resource.example/mcp");
        private const string Issuer = "https://issuer.example";
        public string? Fault;
        public int Authorizations;
        public int TokenRequests;
        public int RefreshRequests;
        public int ToolCalls;
        public int RevocationRequests;
        public Action? BeforeRevocation;
        public bool PkceVerified;
        public bool AuthorizationResourceVerified;
        public bool TokenResourceVerified;
        private string? _challenge;
        public Task<AuthorizationResult?> AuthorizeAsync(AuthorizationCallbackContext context, CancellationToken cancellationToken)
        {
            Authorizations++;
            var query = HttpUtility.ParseQueryString(context.AuthorizationUri.Query);
            _challenge = query["code_challenge"];
            AuthorizationResourceVerified = query["resource"] == Resource.AbsoluteUri;
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.False(string.IsNullOrEmpty(query["state"]));
            Assert.False(string.IsNullOrEmpty(_challenge));
            return Task.FromResult<AuthorizationResult?>(new() { Code = "fixture-code", State = Fault == "state" ? "wrong-state" : query["state"],
                Iss = Fault == "issuer" ? "https://evil.example" : Issuer });
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            HttpResponseMessage Reply(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
                { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
            if (uri.Host == "resource.example" && uri.AbsolutePath.StartsWith("/.well-known/", StringComparison.Ordinal))
                return Reply(new { resource = Fault == "resource" ? "https://other.example/mcp" : Resource.AbsoluteUri,
                    authorization_servers = new[] { Issuer }, scopes_supported = new[] { "research" } });
            if (uri.Host == "issuer.example" && uri.AbsolutePath.Contains(".well-known", StringComparison.Ordinal))
            {
                var metadata = new Dictionary<string, object> {
                    ["issuer"] = Fault == "metadata-issuer" ? "https://evil.example" : Issuer,
                    ["authorization_endpoint"] = Issuer + "/authorize", ["token_endpoint"] = Issuer + "/token",
                    ["code_challenge_methods_supported"] = Fault == "pkce" ? new[] { "plain" } : new[] { "S256" },
                    ["response_types_supported"] = new[] { "code" }, ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
                    ["token_endpoint_auth_methods_supported"] = new[] { "none" }, ["authorization_response_iss_parameter_supported"] = true };
                if (Fault != "registration") metadata["registration_endpoint"] = Issuer + "/register";
                if (Fault != "no-revocation") metadata["revocation_endpoint"] = Issuer + "/revoke";
                return Reply(metadata);
            }
            if (uri.AbsoluteUri == Issuer + "/register")
                return Reply(new { client_id = "fixture-client", token_endpoint_auth_method = "none" }, HttpStatusCode.Created);
            if (uri.AbsoluteUri == Issuer + "/revoke")
            {
                BeforeRevocation?.Invoke();
                RevocationRequests++;
                Assert.DoesNotContain("fixture-private", uri.AbsoluteUri);
                var values = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Contains(values["token"], new[] { "fixture-private-access", "fixture-private-refresh" });
                Assert.Equal("fixture-client", values["client_id"]);
                return Reply(new { }, Fault == "revocation-429" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            }
            if (uri.AbsoluteUri == Issuer + "/token")
            {
                TokenRequests++;
                var values = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                TokenResourceVerified = values["resource"] == Resource.AbsoluteUri;
                Assert.True(TokenResourceVerified);
                if (values["grant_type"] == "refresh_token")
                {
                    RefreshRequests++;
                    Assert.Equal("fixture-private-refresh", values["refresh_token"]);
                }
                else
                {
                    Assert.Equal("fixture-code", values["code"]);
                    string verifier = values["code_verifier"]!;
                    string calculated = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    PkceVerified = calculated == _challenge;
                    Assert.True(PkceVerified);
                }
                return Reply(new { access_token = "fixture-private-access", refresh_token = "fixture-private-refresh", token_type = "Bearer", expires_in = 3600 });
            }
            if (uri == Resource)
            {
                if (request.Headers.Authorization?.Parameter != "fixture-private-access")
                {
                    var denied = Reply(new { }, HttpStatusCode.Unauthorized);
                    denied.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", "resource_metadata=\"https://resource.example/.well-known/oauth-protected-resource\", scope=\"research\""));
                    return denied;
                }
                if (request.Method == HttpMethod.Get) return Reply(new { }, HttpStatusCode.MethodNotAllowed);
                if (request.Method == HttpMethod.Delete) return Reply(new { });
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id)) return Reply(new { }, HttpStatusCode.Accepted);
                string method = root.GetProperty("method").GetString()!;
                object result;
                if (method == "initialize") result = new { protocolVersion = "2025-06-18", capabilities = new { tools = new { listChanged = true } },
                    serverInfo = new { name = "Fake authenticated research", version = "1" } };
                else if (method == "tools/list")
                {
                    bool second = root.TryGetProperty("params", out var p) && p.TryGetProperty("cursor", out var cursor) && cursor.GetString() == "second";
                    result = new { tools = new[] { new { name = second ? "get_active_watchlist" : "get_ohlcv", description = "Fake metadata only",
                        inputSchema = new { type = "object" }, annotations = new { readOnlyHint = true } } }, nextCursor = second ? null : "second" };
                }
                else if (method == "tools/call") { ToolCalls++; result = new { content = Array.Empty<object>() }; }
                else throw new InvalidOperationException("Unexpected fake MCP method: " + method);
                return Reply(new { jsonrpc = "2.0", id = id.Clone(), result });
            }
            throw new InvalidOperationException("Unexpected fake HTTP endpoint; real network is unavailable in this handler.");
        }
    }
}
