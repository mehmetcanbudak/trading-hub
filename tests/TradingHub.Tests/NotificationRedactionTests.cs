using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using TradingHub.App.Services;

namespace TradingHub.Tests;

public sealed class NotificationRedactionTests
{
    private static readonly DateTime Start = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsaved_preview_churn_cannot_expose_confirmed_material_in_delivery_or_local_history(bool longAuthorization)
    {
        using var fixture = new Fixture();
        var transport = new Endpoint();
        using var http = new HttpClient(transport);
        await using var center = fixture.Open(http);
        string token = longAuthorization ? "PRIVATE_LONG_AUTH_MARKER_" + new string('x', 3000) + "_END" : "private-confirmed-bearer-token";
        string authorization = "bEaReR " + token;
        const string target = "https://active-redaction.fixture.invalid:9443/hooks/PRIVATE_ACTIVE_CAPABILITY?grant=PRIVATE_ACTIVE_QUERY";
        const string authority = "active-redaction.fixture.invalid:9443";
        string marker = longAuthorization ? "PRIVATE_LONG_AUTH_MARKER_" : token;
        var destination = await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, target, "", authorization,
            true, new[] { AlertKind.System });
        string body = "auth=" + authorization + "; token=" + token + "; endpoint=" + target + "; authority=" + authority + "; ordinary=VISIBLE_ALERT_BODY";
        const string expected = "auth=[redacted]; token=[redacted]; endpoint=[redacted]; authority=[redacted]; ordinary=VISIBLE_ALERT_BODY";
        string originalId = "material:" + authority;
        var input = new AlertEvent(originalId, Start, AlertKind.System, authorization, authority, body);
        string lastPreviewTarget = "", lastPreviewAuthority = "";

        for (int i = 0; i < 80; i++)
        {
            lastPreviewAuthority = "unsaved-" + i + ".redaction-preview.fixture.invalid";
            lastPreviewTarget = "https://" + lastPreviewAuthority + "/hooks/UNSAVED_CAPABILITY_" + i;
            var unsaved = new NotificationDestination("unsaved-" + i, NotificationDestinationKind.Webhook, lastPreviewTarget, "",
                true, "unsaved-revision-" + i, new[] { AlertKind.System });
            var preview = center.Preview(unsaved, input with { Message = body + "; preview=" + lastPreviewTarget + "; preview-authority=" + lastPreviewAuthority });
            AssertAbsent(JsonSerializer.Serialize(preview), authorization, token, target, authority, marker, lastPreviewTarget, lastPreviewAuthority);
            using var payload = JsonDocument.Parse(preview.Payload);
            Assert.Equal(expected + "; preview=[redacted]; preview-authority=[redacted]", payload.RootElement.GetProperty("message").GetString());
        }

        Assert.Empty(transport.Requests);
        Assert.Empty(center.Snapshot().Recent);
        Assert.Equal(destination.Id, Assert.Single(center.Snapshot().Destinations).Id);
        var raised = new ConcurrentQueue<AlertEvent>();
        center.Raised += raised.Enqueue;
        string finalBody = body + "; preview=" + lastPreviewTarget + "; preview-authority=" + lastPreviewAuthority;
        string finalExpected = expected + "; preview=[redacted]; preview-authority=[redacted]";
        Assert.True(center.Raise(AlertKind.System, authorization, authority, finalBody, originalId));
        await center.FlushAsync();
        await center.DeliverPendingAsync();

        var request = Assert.Single(transport.Requests);
        Assert.Equal(target, request.Uri);
        Assert.Equal(authorization, request.Authorization);
        Assert.NotEqual(originalId, request.EventId);
        Assert.Matches("^[A-F0-9]{64}$", request.EventId);
        using (var payload = JsonDocument.Parse(request.Payload))
        {
            Assert.Equal(request.EventId, payload.RootElement.GetProperty("id").GetString());
            Assert.Equal("[redacted]", payload.RootElement.GetProperty("instrument").GetString());
            Assert.Equal("[redacted]", payload.RootElement.GetProperty("source").GetString());
            Assert.Equal(finalExpected, payload.RootElement.GetProperty("message").GetString());
        }
        var snapshot = center.Snapshot();
        var recent = Assert.Single(snapshot.Recent);
        Assert.Equal(request.EventId, recent.Id);
        Assert.Equal(finalExpected, recent.Message);
        Assert.Equal(finalExpected, Assert.Single(raised).Message);
        var delivery = Assert.Single(snapshot.Deliveries);
        Assert.Equal(NotificationDeliveryState.Delivered, delivery.State);
        Assert.NotNull(delivery.EncryptedAlert);
        var savedPreview = center.Preview(destination, recent);
        Assert.Equal(request.Payload, savedPreview.Payload);
        foreach (string exposed in new[] { request.Payload, request.EventId, JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(raised),
            JsonSerializer.Serialize(savedPreview), File.ReadAllText(fixture.StatePath) })
            AssertAbsent(exposed, authorization, token, target, authority, marker, lastPreviewTarget, lastPreviewAuthority);

        Assert.True(center.Raise(AlertKind.System, authorization, authority, finalBody, originalId));
        await center.FlushAsync();
        await center.DeliverPendingAsync();
        Assert.Single(transport.Requests);
        Assert.Single(center.Snapshot().Recent);
        Assert.Single(raised);
    }

    private static void AssertAbsent(string exposed, params string[] material)
    {
        foreach (string value in material) Assert.DoesNotContain(value, exposed);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-notification-redaction-" + Guid.NewGuid().ToString("N"));
        private readonly MemorySecrets _secrets = new();
        public string StatePath => Path.Combine(_root, "notifications.json");
        public Fixture() => Directory.CreateDirectory(_root);
        public AlertCenter Open(HttpClient http) => new(StatePath, _secrets, http, () => Start);
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class MemorySecrets : IAsyncSecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        public string Get(string account) => throw new InvalidOperationException("The fixture permits asynchronous private access only.");
        public void Set(string account, string? secret) => throw new InvalidOperationException("The fixture permits asynchronous private access only.");
        public Task<string> GetAsync(string account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.GetValueOrDefault(account, ""));
        }
        public Task SetAsync(string account, string? secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(secret)) _values.TryRemove(account, out _);
            else _values[account] = secret;
            return Task.CompletedTask;
        }
    }

    private sealed record Request(string Uri, string EventId, string Authorization, string Payload);

    private sealed class Endpoint : HttpMessageHandler
    {
        public ConcurrentQueue<Request> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.RequestUri!.ToString(), request.Headers.GetValues("X-TradingHub-Alert-Id").Single(),
                request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "",
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new(HttpStatusCode.OK) { Content = new StringContent("Fixture accepted") };
        }
    }
}
