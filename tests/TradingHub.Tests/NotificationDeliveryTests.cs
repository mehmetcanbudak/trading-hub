using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using TradingHub.App.Services;

namespace TradingHub.Tests;

public sealed class NotificationDeliveryTests
{
    private static readonly DateTime Start = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Pending_delivery_survives_restart_retries_with_stable_id_and_never_persists_or_previews_secrets()
    {
        await using var fixture = new Fixture();
        var failed = new Endpoint { Response = HttpStatusCode.ServiceUnavailable };
        using (var http = new HttpClient(failed))
        await using (var center = fixture.Open(http))
        {
            Assert.True(center.Raise(AlertKind.Price, "TEST", "Price", "Local only", "local-first"));
            await center.FlushAsync();
            Assert.Empty(failed.Requests);
            var destination = await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://fixture.invalid/alerts", "", "Bearer private-fixture-authorization", true, new[] { AlertKind.Price });
            Assert.True(center.Raise(AlertKind.Price, "TEST", "Price", "Do not leak private-fixture-authorization", "price-17"));
            await center.FlushAsync();
            await center.DeliverPendingAsync();
            var pending = Assert.Single(center.Snapshot().Deliveries);
            Assert.Equal(NotificationDeliveryState.Pending, pending.State);
            Assert.Equal(1, pending.Attempts);
            var preview = center.Preview(destination, center.Snapshot().Recent.Last());
            Assert.DoesNotContain("private-fixture-authorization", JsonSerializer.Serialize(preview));
            Assert.Contains("[redacted]", preview.Payload);
            Assert.DoesNotContain("private-fixture-authorization", File.ReadAllText(fixture.StatePath));
        }
        fixture.Now = Start.AddMinutes(1);
        var recovered = new Endpoint();
        using (var http = new HttpClient(recovered))
        await using (var center = fixture.Open(http))
        {
            await center.DeliverPendingAsync();
            var sent = Assert.Single(center.Snapshot().Deliveries);
            Assert.Equal(NotificationDeliveryState.Delivered, sent.State);
            Assert.Equal(2, sent.Attempts);
            Assert.Equal(failed.Requests.Single().DeliveryId, recovered.Requests.Single().DeliveryId);
            Assert.Equal("price-17", recovered.Requests.Single().EventId);
            Assert.Equal("Bearer private-fixture-authorization", recovered.Requests.Single().Authorization);
            Assert.DoesNotContain("private-fixture-authorization", recovered.Requests.Single().Payload);
            center.Raise(AlertKind.Price, "TEST", "Price", "Duplicate event ID after restart", "price-17");
            await center.FlushAsync();
            await center.DeliverPendingAsync();
            Assert.Single(recovered.Requests);
            Assert.Equal(2, center.Snapshot().Recent.Count);
        }
    }

    [Fact]
    public async Task Retry_is_bounded_failed_status_is_visible_and_explicit_retry_preserves_delivery_identity()
    {
        await using var fixture = new Fixture();
        var endpoint = new Endpoint { Response = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(endpoint);
        await using var center = fixture.Open(http);
        await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://fixture.invalid/alerts", "", "", true, new[] { AlertKind.Risk });
        center.Raise(AlertKind.Risk, "TEST", "Risk", "Fixture risk limit", "risk-9");
        await center.FlushAsync();
        await center.DeliverPendingAsync();
        for (int i = 1; i < 5; i++) { fixture.Now = fixture.Now.AddHours(1); await center.DeliverPendingAsync(); }
        var failed = Assert.Single(center.Snapshot().Deliveries);
        Assert.Equal(NotificationDeliveryState.Failed, failed.State);
        Assert.Equal(5, failed.Attempts);
        Assert.Equal(5, endpoint.Requests.Count);
        fixture.Now = fixture.Now.AddDays(1);
        await center.DeliverPendingAsync();
        Assert.Equal(5, endpoint.Requests.Count);
        endpoint.Response = HttpStatusCode.OK;
        Assert.True(center.Retry(failed.Id));
        await center.DeliverPendingAsync();
        var sent = Assert.Single(center.Snapshot().Deliveries);
        Assert.Equal(NotificationDeliveryState.Delivered, sent.State);
        Assert.Equal(failed.Id, sent.Id);
        Assert.All(endpoint.Requests, request => Assert.Equal(failed.Id, request.DeliveryId));
        Assert.Equal(6, endpoint.Requests.Count);
    }

    [Fact]
    public async Task Revocation_cancels_active_transport_and_pending_work_and_secret_deletion_prevents_restart_sends()
    {
        await using var fixture = new Fixture();
        var endpoint = new Endpoint { Hold = true };
        string destinationId;
        string revision;
        using (var http = new HttpClient(endpoint))
        await using (var center = fixture.Open(http))
        {
            var destination = await center.ConfigureAsync(null, NotificationDestinationKind.Telegram, "", "-123456", "12345:fixture_token", true, new[] { AlertKind.Strategy });
            destinationId = destination.Id;
            revision = destination.Revision;
            center.Raise(AlertKind.Strategy, "TEST", "Strategy", "Fixture signal", "signal-9");
            await endpoint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await center.RevokeAsync(destination.Id);
            await center.DeliverPendingAsync();
            Assert.True(endpoint.Cancelled);
            Assert.Equal(NotificationDeliveryState.Cancelled, Assert.Single(center.Snapshot().Deliveries).State);
            center.Raise(AlertKind.Strategy, "TEST", "Strategy", "Must stay local after revoke", "signal-10");
            await center.FlushAsync();
            await center.DeliverPendingAsync();
            Assert.Single(endpoint.Requests);
            Assert.Equal("", fixture.Secrets.Get(AlertCenter.SecretAccount(destinationId, revision)));
            Assert.DoesNotContain("12345:fixture_token", File.ReadAllText(fixture.StatePath));
            Assert.Contains("12345:fixture_token", endpoint.Requests.Single().Uri); // only the fake transport sees the token
        }
        fixture.Now = Start.AddDays(1);
        using var restartedHttp = new HttpClient(new Endpoint());
        await using var restarted = fixture.Open(restartedHttp);
        await restarted.DeliverPendingAsync();
        Assert.False(Assert.Single(restarted.Snapshot().Destinations).Enabled);
        Assert.Equal(NotificationDeliveryState.Cancelled, Assert.Single(restarted.Snapshot().Deliveries).State);
    }

    [Fact]
    public async Task Shutdown_cancels_and_drains_an_unconfirmed_send_then_restart_keeps_it_retryable()
    {
        await using var fixture = new Fixture();
        var held = new Endpoint { Hold = true };
        using (var http = new HttpClient(held))
        {
            var center = fixture.Open(http);
            await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://fixture.invalid/alerts", "", "", true, new[] { AlertKind.Execution });
            center.Raise(AlertKind.Execution, "TEST", "Execution", "Fixture fill", "execution-1");
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await center.DisposeAsync();
            Assert.True(held.Cancelled);
            Assert.False(center.Raise(AlertKind.Execution, "TEST", "Execution", "after shutdown"));
        }
        fixture.Now = Start.AddHours(1);
        var recovered = new Endpoint();
        using var recoveredHttp = new HttpClient(recovered);
        await using var restarted = fixture.Open(recoveredHttp);
        await restarted.DeliverPendingAsync();
        Assert.Equal(NotificationDeliveryState.Delivered, Assert.Single(restarted.Snapshot().Deliveries).State);
        Assert.Equal(held.Requests.Single().DeliveryId, recovered.Requests.Single().DeliveryId);
    }

    [Fact]
    public async Task Missing_secret_store_consent_blocks_even_an_unauthenticated_webhook_and_local_cancellation_survives_restart()
    {
        await using var fixture = new Fixture();
        string deliveryId;
        var endpoint = new Endpoint { Response = HttpStatusCode.ServiceUnavailable };
        using (var http = new HttpClient(endpoint))
        await using (var center = fixture.Open(http))
        {
            var destination = await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://fixture.invalid/alerts", "", "", true, new[] { AlertKind.System });
            center.Raise(AlertKind.System, "TEST", "System", "Fixture", "missing-consent");
            await center.FlushAsync();
            await center.DeliverPendingAsync();
            deliveryId = Assert.Single(center.Snapshot().Deliveries).Id;
            fixture.Secrets.Set(AlertCenter.SecretAccount(destination.Id, destination.Revision), null);
        }
        fixture.Now = Start.AddHours(1);
        var recovered = new Endpoint();
        using (var http = new HttpClient(recovered))
        await using (var center = fixture.Open(http))
        {
            Assert.False(await center.RefreshPrivateMaterialAsync());
            await center.DeliverPendingAsync();
            Assert.Empty(recovered.Requests);
            Assert.Equal(NotificationDeliveryState.Failed, Assert.Single(center.Snapshot().Deliveries).State);
            var blocked = Assert.Single(center.Snapshot().Deliveries);
            Assert.Equal(deliveryId, blocked.Id);
            Assert.NotNull(blocked.EncryptedAlert);
            Assert.Equal(1, blocked.Attempts);
            center.CancelDelivery(deliveryId);
            Assert.Equal(NotificationDeliveryState.Cancelled, Assert.Single(center.Snapshot().Deliveries).State);
        }
        using var finalHttp = new HttpClient(recovered);
        await using var final = fixture.Open(finalHttp);
        Assert.Equal(NotificationDeliveryState.Cancelled, Assert.Single(final.Snapshot().Deliveries).State);
    }

    [Fact]
    public async Task Storage_failure_and_throwing_subscribers_do_not_suppress_local_alerts_or_send_external_content()
    {
        await using var fixture = new Fixture();
        string blocker = Path.Combine(fixture.Root, "not-a-directory");
        File.WriteAllText(blocker, "fixture");
        var endpoint = new Endpoint();
        using var http = new HttpClient(endpoint);
        await using var center = new AlertCenter(Path.Combine(blocker, "alerts.json"), fixture.Secrets, http);
        var seen = new TaskCompletionSource<AlertEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        center.Raised += _ => throw new InvalidOperationException("Fixture toast failure");
        center.Raised += alert => seen.TrySetResult(alert);
        Assert.True(center.Raise(AlertKind.Risk, "TEST", "Risk", "Protection already acted", "local-risk"));
        Assert.Equal("local-risk", (await seen.Task.WaitAsync(TimeSpan.FromSeconds(10))).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => center.FlushAsync());
        Assert.Contains(center.Snapshot().Recent, alert => alert.Message == "Protection already acted");
        Assert.Empty(endpoint.Requests);
    }

    [Fact]
    public async Task Corrupt_state_is_preserved_and_practice_events_are_never_queued_for_external_delivery()
    {
        await using var fixture = new Fixture();
        File.WriteAllText(fixture.StatePath, "{truncated-fixture");
        var endpoint = new Endpoint();
        using (var http = new HttpClient(endpoint))
        await using (var center = fixture.Open(http))
        {
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            center.Raised += _ => seen.TrySetResult();
            center.Raise(AlertKind.Price, "TEST", "Price", "Local despite corrupt file");
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal("{truncated-fixture", File.ReadAllText(fixture.StatePath));
        File.Delete(fixture.StatePath);
        using var secondHttp = new HttpClient(endpoint);
        await using var clean = fixture.Open(secondHttp);
        await clean.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://fixture.invalid/alerts", "", "", true, Enum.GetValues<AlertKind>());
        clean.Raise(AlertKind.Execution, "Practice", "Practice", "Local-only practice", "practice-1", allowExternal: false);
        await clean.FlushAsync();
        await clean.DeliverPendingAsync();
        Assert.Empty(endpoint.Requests);
        Assert.Empty(clean.Snapshot().Deliveries);
        Assert.False(Assert.Single(clean.Snapshot().Recent).AllowExternal);
    }

    [Fact]
    public async Task A_held_external_send_does_not_block_local_alerts_and_pending_history_remains_bounded()
    {
        await using var fixture = new Fixture();
        var endpoint = new Endpoint { Hold = true };
        using var http = new HttpClient(endpoint);
        await using var center = fixture.Open(http);
        var destination = await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://fixture.invalid/alerts",
            "", "", true, new[] { AlertKind.Price });
        center.Raise(AlertKind.Price, "TEST", "Price", "Held request", "capacity-0");
        await endpoint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 1; i <= 500; i++)
            Assert.True(center.Raise(AlertKind.Price, "TEST", "Price", "Local update " + i, "capacity-" + i));
        await center.FlushAsync();
        var snapshot = center.Snapshot();
        Assert.Equal(500, snapshot.Deliveries.Count);
        Assert.Equal(300, snapshot.Recent.Count);
        Assert.Contains(snapshot.Recent, alert => alert.Id == "capacity-500");
        Assert.Single(endpoint.Requests);
        var pending = snapshot.Deliveries.Single(d => d.Alert.Id == "capacity-499");
        center.CancelDelivery(pending.Id);
        Assert.False(endpoint.Cancelled); // Cancelling a different pending message must not cancel the active request.
        Assert.Equal(NotificationDeliveryState.Cancelled, center.Snapshot().Deliveries.Single(d => d.Id == pending.Id).State);
        await center.RevokeAsync(destination.Id);
        await center.DeliverPendingAsync();
        Assert.All(center.Snapshot().Deliveries, delivery => Assert.Equal(NotificationDeliveryState.Cancelled, delivery.State));
        Assert.Single(endpoint.Requests);
    }

    [Fact]
    public async Task Telegram_requires_ok_acknowledgment_and_previews_the_exact_chat_payload_without_its_token()
    {
        await using var fixture = new Fixture();
        var endpoint = new Endpoint { Body = "{\"ok\":false,\"error_code\":403,\"description\":\"private response with 54321:fixture_secret\"}" };
        using var http = new HttpClient(endpoint);
        await using var center = fixture.Open(http);
        var destination = await center.ConfigureAsync(null, NotificationDestinationKind.Telegram, "", "-9988",
            "54321:fixture_secret", true, new[] { AlertKind.System });
        center.Raise(AlertKind.System, "TEST", "System", "Telegram acknowledgment fixture", "telegram-1");
        await center.FlushAsync();
        await center.DeliverPendingAsync();
        var failed = Assert.Single(center.Snapshot().Deliveries);
        Assert.Equal(NotificationDeliveryState.Failed, failed.State);
        Assert.Contains("403", failed.Status);
        Assert.DoesNotContain("private response", File.ReadAllText(fixture.StatePath));
        Assert.DoesNotContain("54321:fixture_secret", File.ReadAllText(fixture.StatePath));
        var preview = center.Preview(destination, center.Snapshot().Recent.Single());
        Assert.Equal(endpoint.Requests.Single().Payload, preview.Payload);
        Assert.DoesNotContain("54321:fixture_secret", JsonSerializer.Serialize(preview));
        endpoint.Body = "{\"ok\":true,\"result\":{\"message_id\":123}}";
        Assert.True(center.Retry(failed.Id));
        await center.DeliverPendingAsync();
        Assert.Equal(NotificationDeliveryState.Delivered, Assert.Single(center.Snapshot().Deliveries).State);
    }

    [Theory]
    [InlineData(NotificationDestinationKind.Webhook)]
    [InlineData(NotificationDestinationKind.Telegram)]
    public async Task Silent_secret_write_failure_never_publishes_a_new_permission_or_retires_existing_consent(NotificationDestinationKind kind)
    {
        await using var fixture = new Fixture();
        var secrets = new SilentWriteSecrets(fixture.Secrets) { DropWrites = true };
        var endpoint = new Endpoint { Response = HttpStatusCode.ServiceUnavailable, Body = "{\"ok\":true,\"result\":{\"message_id\":1}}" };
        using var http = new HttpClient(endpoint);
        await using var center = new AlertCenter(fixture.StatePath, secrets, http, () => fixture.Now);
        string url = kind == NotificationDestinationKind.Webhook ? "https://fixture.invalid/original" : "";
        string target = kind == NotificationDestinationKind.Telegram ? "-1122" : "";
        string original = kind == NotificationDestinationKind.Telegram ? "12345:original_private_token" : "Bearer original-private-authorization";
        string replacement = kind == NotificationDestinationKind.Telegram ? "54321:replacement_private_token" : "Bearer replacement-private-authorization";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            center.ConfigureAsync(null, kind, url, target, replacement, true, new[] { AlertKind.System }));
        Assert.DoesNotContain(replacement, error.Message);
        Assert.Empty(center.Snapshot().Destinations);
        center.Raise(AlertKind.System, "TEST", "Secret write fixture", "Must remain local without confirmed permission", "unconfirmed-consent");
        await center.FlushAsync();
        await center.DeliverPendingAsync();
        Assert.Empty(center.Snapshot().Deliveries);
        Assert.Empty(endpoint.Requests);

        secrets.DropWrites = false;
        var saved = await center.ConfigureAsync(null, kind, url, target, original, true, new[] { AlertKind.System });
        center.Raise(AlertKind.System, "TEST", "Secret write fixture", "Original approved pending message", "original-consent");
        await center.FlushAsync();
        await center.DeliverPendingAsync();
        var pending = Assert.Single(center.Snapshot().Deliveries);
        Assert.Equal(NotificationDeliveryState.Pending, pending.State);
        Assert.Single(endpoint.Requests);
        string credentialBefore = fixture.Secrets.Get(AlertCenter.SecretAccount(saved.Id, saved.Revision));
        string stateBefore = File.ReadAllText(fixture.StatePath);

        secrets.DropWrites = true;
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => center.ConfigureAsync(saved.Id, kind,
            kind == NotificationDestinationKind.Webhook ? "https://fixture.invalid/replacement" : "",
            kind == NotificationDestinationKind.Telegram ? "-3344" : "", replacement, true, new[] { AlertKind.System }));
        Assert.DoesNotContain(replacement, error.Message);
        var retained = Assert.Single(center.Snapshot().Destinations);
        Assert.Equal(saved.Id, retained.Id);
        Assert.Equal(saved.Revision, retained.Revision);
        Assert.True(retained.Enabled);
        Assert.Equal(saved.Endpoint, retained.Endpoint);
        Assert.Equal(saved.Target, retained.Target);
        Assert.Equal(credentialBefore, fixture.Secrets.Get(AlertCenter.SecretAccount(saved.Id, saved.Revision)));
        Assert.Equal(stateBefore, File.ReadAllText(fixture.StatePath));
        var retainedPending = Assert.Single(center.Snapshot().Deliveries);
        Assert.Equal(pending.Id, retainedPending.Id);
        Assert.Equal(saved.Revision, retainedPending.Revision);
        Assert.Equal(NotificationDeliveryState.Pending, retainedPending.State);
        Assert.Single(endpoint.Requests);

        fixture.Now = fixture.Now.AddHours(1);
        endpoint.Response = HttpStatusCode.OK;
        await center.DeliverPendingAsync();
        Assert.Equal(NotificationDeliveryState.Delivered, Assert.Single(center.Snapshot().Deliveries).State);
        Assert.Equal(2, endpoint.Requests.Count);
        Assert.All(endpoint.Requests, request =>
        {
            Assert.Equal(pending.Id, request.DeliveryId);
            if (kind == NotificationDestinationKind.Webhook)
            {
                Assert.Equal(url, request.Uri);
                Assert.Equal(original, request.Authorization);
            }
            else
            {
                Assert.Contains("/bot" + original + "/sendMessage", request.Uri);
                Assert.DoesNotContain(replacement, request.Uri);
            }
        });
        Assert.DoesNotContain(original, File.ReadAllText(fixture.StatePath));
        Assert.DoesNotContain(replacement, File.ReadAllText(fixture.StatePath));
        Assert.DoesNotContain(original, JsonSerializer.Serialize(center.Snapshot()));
        Assert.DoesNotContain(replacement, JsonSerializer.Serialize(center.Snapshot()));
    }

    [Fact]
    public async Task Capability_webhook_target_is_private_across_restart_and_only_explicitly_reviewed()
    {
        await using var fixture = new Fixture();
        const string fullTarget = "https://capability-host.fixture.invalid/services/channel/CAPABILITY_SECRET?key=QUERY_SECRET";
        const string authorization = "Bearer private-capability-auth";
        string id;
        var endpoint = new Endpoint { Response = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(endpoint);
        await using (var center = fixture.Open(http))
        {
            var destination = await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, fullTarget, "", authorization, true, new[] { AlertKind.System });
            id = destination.Id;
            Assert.False(Uri.TryCreate(destination.Endpoint, UriKind.Absolute, out _));
            Assert.Equal(fullTarget, center.ReviewPrivateTarget(id));
            var preview = center.Preview(destination with { Endpoint = fullTarget },
                new AlertEvent("preview", Start, AlertKind.System, "TEST", "Private target fixture", fullTarget));
            Assert.False(Uri.TryCreate(preview.Destination, UriKind.Absolute, out _));
            Assert.DoesNotContain("CAPABILITY_SECRET", JsonSerializer.Serialize(preview));
            Assert.DoesNotContain("QUERY_SECRET", JsonSerializer.Serialize(preview));
            Assert.DoesNotContain("capability-host", JsonSerializer.Serialize(preview));
            center.Raise(AlertKind.System, "TEST", "Private target fixture", "Pending for restart", "private-target-pending");
            await center.FlushAsync();
            await center.DeliverPendingAsync();
            Assert.Equal(fullTarget, Assert.Single(endpoint.Requests).Uri);
            Assert.Equal(NotificationDeliveryState.Pending, Assert.Single(center.Snapshot().Deliveries).State);
            foreach (string ordinary in new[] { File.ReadAllText(fixture.StatePath), JsonSerializer.Serialize(center.Snapshot()) })
            {
                Assert.DoesNotContain("CAPABILITY_SECRET", ordinary);
                Assert.DoesNotContain("QUERY_SECRET", ordinary);
                Assert.DoesNotContain("capability-host", ordinary);
                Assert.DoesNotContain(authorization, ordinary);
            }
            Assert.Contains("CAPABILITY_SECRET", fixture.Secrets.Get(AlertCenter.SecretAccount(destination.Id, destination.Revision)));
        }
        fixture.Now = fixture.Now.AddHours(1);
        endpoint.Response = HttpStatusCode.OK;
        await using (var restarted = fixture.Open(http))
        {
            Assert.True(await restarted.RefreshPrivateMaterialAsync());
            Assert.Equal(fullTarget, restarted.ReviewPrivateTarget(id));
            await restarted.DeliverPendingAsync();
            Assert.Equal(2, endpoint.Requests.Count);
            Assert.All(endpoint.Requests, request => Assert.Equal(fullTarget, request.Uri));
            Assert.Equal(NotificationDeliveryState.Delivered, Assert.Single(restarted.Snapshot().Deliveries).State);
            Assert.DoesNotContain("CAPABILITY_SECRET", File.ReadAllText(fixture.StatePath));
            Assert.DoesNotContain("QUERY_SECRET", JsonSerializer.Serialize(restarted.Snapshot()));
        }
    }

    [Theory]
    [InlineData(NotificationDestinationKind.Webhook, false)]
    [InlineData(NotificationDestinationKind.Telegram, false)]
    [InlineData(NotificationDestinationKind.Webhook, true)]
    [InlineData(NotificationDestinationKind.Telegram, true)]
    public async Task Unchecked_save_revokes_active_pending_and_future_delivery_despite_private_or_disk_failure(NotificationDestinationKind kind, bool diskFailure)
    {
        await using var fixture = new Fixture();
        var secrets = new SilentWriteSecrets(fixture.Secrets);
        var endpoint = new Endpoint { Hold = true };
        using var http = new HttpClient(endpoint);
        await using var center = new AlertCenter(fixture.StatePath, secrets, http, () => fixture.Now);
        var destination = await center.ConfigureAsync(null, kind, kind == NotificationDestinationKind.Webhook ? "https://fixture.invalid/private-path" : "",
            kind == NotificationDestinationKind.Telegram ? "-1234" : "", kind == NotificationDestinationKind.Telegram ? "12345:private_token" : "Bearer private-auth",
            true, new[] { AlertKind.System });
        center.Raise(AlertKind.System, "TEST", "Withdrawal fixture", "Active delivery", "withdraw-active");
        await center.FlushAsync();
        await endpoint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        center.Raise(AlertKind.System, "TEST", "Withdrawal fixture", "Pending delivery", "withdraw-pending");
        await center.FlushAsync();
        secrets.DropDeletes = true;
        if (diskFailure)
        {
            File.Delete(fixture.StatePath);
            Directory.CreateDirectory(fixture.StatePath);
        }
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            // Withdrawal outranks even invalid edited fields, no selected kinds and a cancelled UI request.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => center.ConfigureAsync(destination.Id, kind,
                "invalid edited target", "invalid edited chat", "\r\ninvalid edited authorization", false, Array.Empty<AlertKind>(), cancelled.Token));
            Assert.False(Assert.Single(center.Snapshot().Destinations).Enabled);
            Assert.All(center.Snapshot().Deliveries, delivery => Assert.Equal(NotificationDeliveryState.Cancelled, delivery.State));
            await center.DeliverPendingAsync();
            Assert.True(endpoint.Cancelled);
            Assert.Single(endpoint.Requests);
            var local = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            center.Raised += alert => { if (alert.Id == "withdraw-local") local.TrySetResult(); };
            center.Raise(AlertKind.System, "TEST", "Withdrawal fixture", "Local after failed durable revocation", "withdraw-local");
            await local.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(endpoint.Requests);
            Assert.Equal(2, center.Snapshot().Deliveries.Count);
        }
        finally
        {
            if (diskFailure) Directory.Delete(fixture.StatePath);
            secrets.DropDeletes = false;
            await center.RevokeAsync(destination.Id);
        }
        Assert.Empty(fixture.Secrets.Get(AlertCenter.SecretAccount(destination.Id, destination.Revision)));
        Assert.False(Assert.Single(center.Snapshot().Destinations).Enabled);
        Assert.DoesNotContain("private-auth", File.ReadAllText(fixture.StatePath));
        Assert.DoesNotContain("private_token", File.ReadAllText(fixture.StatePath));
    }

    [Fact]
    public async Task Encrypted_pending_survives_unavailable_private_store_shutdown_and_delivers_real_content_after_restart()
    {
        await using var fixture = new Fixture();
        const string target = "https://private-host.fixture.invalid/PRIVATE_PATH?key=PRIVATE_QUERY";
        const string authorization = "Bearer private-restart-authorization";
        const string body = "PRIVATE_APPROVED_BODY";
        string deliveryId, destinationId, revision, protectedBytes, privateKey;
        var firstTransport = new Endpoint { Response = HttpStatusCode.ServiceUnavailable };
        using (var http = new HttpClient(firstTransport))
        await using (var center = fixture.Open(http))
        {
            var destination = await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, target, "", authorization, true, new[] { AlertKind.System });
            destinationId = destination.Id;
            revision = destination.Revision;
            center.Raise(AlertKind.System, "PRIVATE_SYMBOL", "PRIVATE_SOURCE", body, "private-original-id");
            await center.FlushAsync();
            await center.DeliverPendingAsync();
            var delivery = Assert.Single(center.Snapshot().Deliveries);
            deliveryId = delivery.Id;
            protectedBytes = JsonSerializer.Serialize(delivery.EncryptedAlert);
            Assert.Equal(12, Convert.FromBase64String(delivery.EncryptedAlert!.Nonce).Length);
            Assert.Equal(16, Convert.FromBase64String(delivery.EncryptedAlert.Tag).Length);
            Assert.InRange(Convert.FromBase64String(delivery.EncryptedAlert.Ciphertext).Length, 1, 16 * 1024);
            using var credential = JsonDocument.Parse(fixture.Secrets.Get(AlertCenter.SecretAccount(destinationId, revision)));
            privateKey = credential.RootElement.GetProperty("DeliveryKey").GetString()!;
            Assert.Equal(32, Convert.FromBase64String(privateKey).Length);
        }
        fixture.Now = fixture.Now.AddHours(1);
        var barrier = new AsyncBarrierSecrets(fixture.Secrets);
        var blockedTransport = new Endpoint();
        using (var http = new HttpClient(blockedTransport))
        {
            await using var blocked = new AlertCenter(fixture.StatePath, barrier, http, () => fixture.Now);
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            blocked.Raise(AlertKind.Risk, "LOCAL", "Local protection", "LOCAL_NOT_YET_DURABLE", "local-during-private-read", allowExternal: false);
            await blocked.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, blocked.Snapshot().NotDurableAlerts);
            Assert.Single(blocked.Snapshot().Deliveries);
            Assert.Equal(NotificationDeliveryState.Pending, Assert.Single(blocked.Snapshot().Deliveries).State);
            Assert.Equal(protectedBytes, JsonSerializer.Serialize(blocked.Snapshot().Deliveries.Single().EncryptedAlert));
            Assert.Empty(blockedTransport.Requests);
            await blocked.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(barrier.CancelledReads > 0);
        }
        string ordinary = File.ReadAllText(fixture.StatePath);
        foreach (string value in new[] { body, target, authorization, privateKey, "PRIVATE_SYMBOL", "PRIVATE_SOURCE", "private-original-id", "LOCAL_NOT_YET_DURABLE" })
            Assert.DoesNotContain(value, ordinary);
        var recoveredTransport = new Endpoint();
        using (var http = new HttpClient(recoveredTransport))
        await using (var recovered = fixture.Open(http))
        {
            Assert.True(await recovered.RefreshPrivateMaterialAsync());
            await recovered.DeliverPendingAsync();
            var request = Assert.Single(recoveredTransport.Requests);
            Assert.Equal(deliveryId, request.DeliveryId);
            Assert.Equal("private-original-id", request.EventId);
            Assert.Equal(target, request.Uri);
            Assert.Equal(authorization, request.Authorization);
            using var payload = JsonDocument.Parse(request.Payload);
            Assert.Equal(body, payload.RootElement.GetProperty("message").GetString());
            Assert.Equal(protectedBytes, JsonSerializer.Serialize(recovered.Snapshot().Deliveries.Single().EncryptedAlert));
            Assert.Equal(NotificationDeliveryState.Delivered, recovered.Snapshot().Deliveries.Single().State);
            recovered.Raise(AlertKind.System, "PRIVATE_SYMBOL", "PRIVATE_SOURCE", "Duplicate changed body", "private-original-id");
            await recovered.FlushAsync();
            await recovered.DeliverPendingAsync();
            Assert.Single(recoveredTransport.Requests);
        }
    }

    [Theory]
    [InlineData("Tag")]
    [InlineData("Ciphertext")]
    [InlineData("Revision")]
    public async Task Actual_delivery_and_retry_reject_tampered_or_wrong_revision_ciphertext(string changed)
    {
        await using var fixture = new Fixture();
        string id, destinationId, revision, credential;
        using (var http = new HttpClient(new Endpoint { Response = HttpStatusCode.ServiceUnavailable }))
        await using (var center = fixture.Open(http))
        {
            var destination = await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://integrity.fixture.invalid/PRIVATE_CAPABILITY", "",
                "Bearer private-integrity-auth", true, new[] { AlertKind.System });
            destinationId = destination.Id;
            revision = destination.Revision;
            credential = fixture.Secrets.Get(AlertCenter.SecretAccount(destinationId, revision));
            center.Raise(AlertKind.System, "TEST", "Integrity", "PRIVATE_INTEGRITY_BODY", "integrity-event");
            await center.FlushAsync();
            await center.DeliverPendingAsync();
            id = Assert.Single(center.Snapshot().Deliveries).Id;
        }
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.StatePath))!;
        var delivery = state["Deliveries"]!.AsArray()[0]!;
        if (changed == "Revision")
        {
            string replacement = Guid.NewGuid().ToString("N");
            state["Destinations"]!.AsArray()[0]!["Revision"] = replacement;
            delivery["Revision"] = replacement;
            // The same key under a different consent is still rejected by authenticated associated data.
            fixture.Secrets.Set(AlertCenter.SecretAccount(destinationId, replacement), credential);
        }
        else
        {
            var envelope = delivery["EncryptedAlert"]!;
            byte[] bytes = Convert.FromBase64String(envelope[changed]!.GetValue<string>());
            bytes[0] ^= 1;
            envelope[changed] = Convert.ToBase64String(bytes);
        }
        File.WriteAllText(fixture.StatePath, state.ToJsonString());
        fixture.Now = fixture.Now.AddHours(1);
        var transport = new Endpoint();
        using var recoveredHttp = new HttpClient(transport);
        await using var recovered = fixture.Open(recoveredHttp);
        Assert.True(await recovered.RefreshPrivateMaterialAsync());
        await recovered.DeliverPendingAsync();
        var failed = Assert.Single(recovered.Snapshot().Deliveries);
        Assert.Equal(NotificationDeliveryState.Failed, failed.State);
        Assert.NotEmpty(failed.Status);
        Assert.Empty(transport.Requests);
        Assert.True(recovered.Retry(id));
        await recovered.DeliverPendingAsync();
        Assert.Equal(NotificationDeliveryState.Failed, recovered.Snapshot().Deliveries.Single().State);
        Assert.Empty(transport.Requests);
        Assert.DoesNotContain("PRIVATE_INTEGRITY_BODY", File.ReadAllText(fixture.StatePath));
        Assert.DoesNotContain("private-integrity-auth", File.ReadAllText(fixture.StatePath));
    }

    [Fact]
    public async Task Confirmed_permission_revisions_have_independent_keys_and_each_pending_payload_has_a_fresh_nonce()
    {
        await using var fixture = new Fixture();
        using var http = new HttpClient(new Endpoint { Response = HttpStatusCode.ServiceUnavailable });
        await using var center = fixture.Open(http);
        var first = await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://nonce.fixture.invalid/private", "", "", true, new[] { AlertKind.System });
        using var firstCredential = JsonDocument.Parse(fixture.Secrets.Get(AlertCenter.SecretAccount(first.Id, first.Revision)));
        center.Raise(AlertKind.System, "TEST", "Nonce", "Same content", "nonce-a");
        center.Raise(AlertKind.System, "TEST", "Nonce", "Same content", "nonce-b");
        await center.FlushAsync();
        var deliveries = center.Snapshot().Deliveries;
        Assert.Equal(2, deliveries.Count);
        Assert.NotEqual(deliveries[0].EncryptedAlert!.Nonce, deliveries[1].EncryptedAlert!.Nonce);
        var next = await center.ConfigureAsync(first.Id, NotificationDestinationKind.Webhook, "", "", null, true, new[] { AlertKind.System });
        using var nextCredential = JsonDocument.Parse(fixture.Secrets.Get(AlertCenter.SecretAccount(next.Id, next.Revision)));
        Assert.NotEqual(first.Revision, next.Revision);
        Assert.NotEqual(firstCredential.RootElement.GetProperty("DeliveryKey").GetString(), nextCredential.RootElement.GetProperty("DeliveryKey").GetString());
        Assert.All(center.Snapshot().Deliveries, delivery => Assert.Equal(NotificationDeliveryState.Cancelled, delivery.State));
    }

    [Fact]
    public async Task Ordinary_settings_cannot_redirect_an_existing_private_Telegram_permission_to_another_recipient()
    {
        await using var fixture = new Fixture();
        using (var http = new HttpClient(new Endpoint { Response = HttpStatusCode.ServiceUnavailable }))
        await using (var center = fixture.Open(http))
        {
            await center.ConfigureAsync(null, NotificationDestinationKind.Telegram, "", "-111", "12345:private_recipient_token", true, new[] { AlertKind.System });
            center.Raise(AlertKind.System, "TEST", "Recipient binding", "PRIVATE_RECIPIENT_BODY", "recipient-event");
            await center.FlushAsync();
            await center.DeliverPendingAsync();
        }
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.StatePath))!;
        state["Destinations"]!.AsArray()[0]!["Target"] = "-222";
        File.WriteAllText(fixture.StatePath, state.ToJsonString());
        fixture.Now = fixture.Now.AddHours(1);
        var transport = new Endpoint();
        using var recoveredHttp = new HttpClient(transport);
        await using var recovered = fixture.Open(recoveredHttp);
        Assert.False(await recovered.RefreshPrivateMaterialAsync());
        await recovered.DeliverPendingAsync();
        Assert.Empty(transport.Requests);
        Assert.NotEmpty(recovered.Snapshot().Status);
        Assert.DoesNotContain("PRIVATE_RECIPIENT_BODY", File.ReadAllText(fixture.StatePath));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Deferred_content_only_reaches_unchanged_grants_captured_before_destinations_change(bool reconfigureFirst, bool addDestination)
    {
        await using var fixture = new Fixture();
        var transport = new Endpoint();
        using var http = new HttpClient(transport);
        NotificationDestination first, unavailable;
        await using (var configured = fixture.Open(http))
        {
            first = await configured.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://first.fixture.invalid/original", "",
                "Bearer first-private-consent", true, new[] { AlertKind.System });
            unavailable = await configured.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://unavailable.fixture.invalid/original", "",
                "Bearer unavailable-private-consent", true, new[] { AlertKind.System });
        }
        var secrets = new SelectivelyUnavailableSecrets(fixture.Secrets)
            { UnavailableAccounts = new[] { AlertCenter.SecretAccount(unavailable.Id, unavailable.Revision) } };
        await using var center = new AlertCenter(fixture.StatePath, secrets, http, () => fixture.Now);
        Assert.False(await center.RefreshPrivateMaterialAsync());
        Assert.True(center.Raise(AlertKind.System, "TEST", "Deferred consent", "ORIGINAL_DEFERRED_CONTENT", "captured-before-edit"));
        await center.FlushAsync();
        Assert.Equal(1, center.Snapshot().NotDurableAlerts);
        Assert.Empty(center.Snapshot().Deliveries);
        Assert.Empty(transport.Requests);
        Assert.DoesNotContain("ORIGINAL_DEFERRED_CONTENT", File.ReadAllText(fixture.StatePath));

        if (reconfigureFirst)
            first = await center.ConfigureAsync(first.Id, NotificationDestinationKind.Webhook, "https://first.fixture.invalid/replacement", "",
                "Bearer replacement-private-consent", true, new[] { AlertKind.System });
        if (addDestination)
            await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://new.fixture.invalid/recipient", "",
                "Bearer new-private-consent", true, new[] { AlertKind.System });
        Assert.Empty(transport.Requests);

        secrets.UnavailableAccounts = Array.Empty<string>();
        Assert.True(await center.RefreshPrivateMaterialAsync());
        await center.FlushAsync();
        await center.DeliverPendingAsync();
        var originalRequests = transport.Requests.ToArray();
        Assert.Equal(reconfigureFirst ? 1 : 2, originalRequests.Length);
        Assert.Contains(originalRequests, request => request.Uri == "https://unavailable.fixture.invalid/original");
        if (!reconfigureFirst)
            Assert.Contains(originalRequests, request => request.Uri == "https://first.fixture.invalid/original");
        Assert.DoesNotContain(originalRequests, request => request.Uri == "https://first.fixture.invalid/replacement" ||
            request.Uri == "https://new.fixture.invalid/recipient");
        Assert.All(originalRequests, request =>
        {
            Assert.Equal("captured-before-edit", request.EventId);
            using var payload = JsonDocument.Parse(request.Payload);
            Assert.Equal("ORIGINAL_DEFERRED_CONTENT", payload.RootElement.GetProperty("message").GetString());
        });
        Assert.Equal("ORIGINAL_DEFERRED_CONTENT", Assert.Single(center.Snapshot().Recent).Message);
        Assert.Equal(0, center.Snapshot().NotDurableAlerts);
        Assert.All(center.Snapshot().Deliveries, delivery =>
        {
            Assert.Equal(NotificationDeliveryState.Delivered, delivery.State);
            Assert.NotNull(delivery.EncryptedAlert);
            if (delivery.DestinationId == unavailable.Id) Assert.Equal(unavailable.Revision, delivery.Revision);
        });

        Assert.True(center.Raise(AlertKind.System, "TEST", "Deferred consent", "NEW_APPROVED_CONTENT", "accepted-after-edit"));
        await center.FlushAsync();
        await center.DeliverPendingAsync();
        var futureRequests = transport.Requests.Where(request => request.EventId == "accepted-after-edit").ToArray();
        Assert.Equal(addDestination ? 3 : 2, futureRequests.Length);
        Assert.Contains(futureRequests, request => request.Uri == (reconfigureFirst
            ? "https://first.fixture.invalid/replacement" : "https://first.fixture.invalid/original"));
        if (addDestination) Assert.Contains(futureRequests, request => request.Uri == "https://new.fixture.invalid/recipient");
        Assert.All(futureRequests, request =>
        {
            using var payload = JsonDocument.Parse(request.Payload);
            Assert.Equal("NEW_APPROVED_CONTENT", payload.RootElement.GetProperty("message").GetString());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retiring_a_ready_grant_preserves_other_deferred_grants_without_forwarding_to_its_reenabled_permission(bool uncheckedSave)
    {
        await using var fixture = new Fixture();
        var transport = new Endpoint();
        using var http = new HttpClient(transport);
        NotificationDestination retired, unavailable;
        await using (var configured = fixture.Open(http))
        {
            retired = await configured.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://retired.fixture.invalid/original", "",
                "Bearer retired-private-consent", true, new[] { AlertKind.System });
            unavailable = await configured.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://remaining.fixture.invalid/original", "",
                "Bearer remaining-private-consent", true, new[] { AlertKind.System });
        }
        var secrets = new SelectivelyUnavailableSecrets(fixture.Secrets)
            { UnavailableAccounts = new[] { AlertCenter.SecretAccount(unavailable.Id, unavailable.Revision) } };
        await using var center = new AlertCenter(fixture.StatePath, secrets, http, () => fixture.Now);
        Assert.False(await center.RefreshPrivateMaterialAsync());
        Assert.True(center.Raise(AlertKind.System, "TEST", "Deferred retirement", "CONTENT_APPROVED_BEFORE_RETIREMENT", "before-retirement"));
        await center.FlushAsync();
        Assert.Equal(1, center.Snapshot().NotDurableAlerts);
        if (uncheckedSave)
            await center.ConfigureAsync(retired.Id, NotificationDestinationKind.Webhook, "", "", null, false, Array.Empty<AlertKind>());
        else
            await center.RevokeAsync(retired.Id);
        Assert.False(center.Snapshot().Destinations.Single(destination => destination.Id == retired.Id).Enabled);
        Assert.Equal(1, center.Snapshot().NotDurableAlerts);
        Assert.Empty(transport.Requests);

        var reenabled = await center.ConfigureAsync(retired.Id, NotificationDestinationKind.Webhook, "https://retired.fixture.invalid/reenabled", "",
            "Bearer reenabled-private-consent", true, new[] { AlertKind.System });
        Assert.NotEqual(retired.Revision, reenabled.Revision);
        secrets.UnavailableAccounts = Array.Empty<string>();
        Assert.True(await center.RefreshPrivateMaterialAsync());
        await center.FlushAsync();
        await center.DeliverPendingAsync();
        var original = Assert.Single(transport.Requests);
        Assert.Equal("https://remaining.fixture.invalid/original", original.Uri);
        Assert.Equal("before-retirement", original.EventId);
        using (var payload = JsonDocument.Parse(original.Payload))
            Assert.Equal("CONTENT_APPROVED_BEFORE_RETIREMENT", payload.RootElement.GetProperty("message").GetString());
        var delivered = Assert.Single(center.Snapshot().Deliveries);
        Assert.Equal(unavailable.Id, delivered.DestinationId);
        Assert.Equal(unavailable.Revision, delivered.Revision);
        Assert.Equal(NotificationDeliveryState.Delivered, delivered.State);
        Assert.Equal("CONTENT_APPROVED_BEFORE_RETIREMENT", Assert.Single(center.Snapshot().Recent).Message);
        Assert.Equal(0, center.Snapshot().NotDurableAlerts);

        Assert.True(center.Raise(AlertKind.System, "TEST", "Deferred retirement", "CONTENT_APPROVED_AFTER_REENABLE", "after-reenable"));
        await center.FlushAsync();
        await center.DeliverPendingAsync();
        var future = transport.Requests.Where(request => request.EventId == "after-reenable").ToArray();
        Assert.Equal(2, future.Length);
        Assert.Contains(future, request => request.Uri == "https://retired.fixture.invalid/reenabled");
        Assert.All(future, request =>
        {
            using var payload = JsonDocument.Parse(request.Payload);
            Assert.Equal("CONTENT_APPROVED_AFTER_REENABLE", payload.RootElement.GetProperty("message").GetString());
        });
        Assert.DoesNotContain("CONTENT_APPROVED_BEFORE_RETIREMENT", File.ReadAllText(fixture.StatePath));
    }

    [Fact]
    public async Task Deferred_content_scrubbed_when_material_becomes_known_stays_safe_after_retirement_and_preview_churn()
    {
        await using var fixture = new Fixture();
        var transport = new Endpoint();
        using var http = new HttpClient(transport);
        const string token = "retired-deferred-private-token";
        const string authorization = "Bearer " + token;
        const string target = "https://retired-deferred.fixture.invalid/hooks/PRIVATE_RETIRED_CAPABILITY";
        const string authority = "retired-deferred.fixture.invalid";
        NotificationDestination retired, unavailable;
        await using (var configured = fixture.Open(http))
        {
            retired = await configured.ConfigureAsync(null, NotificationDestinationKind.Webhook, target, "",
                authorization, true, new[] { AlertKind.System });
            unavailable = await configured.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://deferred-recipient.fixture.invalid/original", "",
                "Bearer deferred-recipient-private-consent", true, new[] { AlertKind.System });
        }
        string retiredAccount = AlertCenter.SecretAccount(retired.Id, retired.Revision);
        string unavailableAccount = AlertCenter.SecretAccount(unavailable.Id, unavailable.Revision);
        var secrets = new SelectivelyUnavailableSecrets(fixture.Secrets) { UnavailableAccounts = new[] { retiredAccount, unavailableAccount } };
        await using var center = new AlertCenter(fixture.StatePath, secrets, http, () => fixture.Now);
        Assert.False(await center.RefreshPrivateMaterialAsync());
        string originalId = "deferred:" + token;
        string body = "auth=" + authorization + "; token=" + token + "; target=" + target + "; authority=" + authority + "; visible=ORIGINAL_BODY";
        Assert.True(center.Raise(AlertKind.System, token, authority, body, originalId));
        await center.FlushAsync();
        Assert.Equal(1, center.Snapshot().NotDurableAlerts);

        secrets.UnavailableAccounts = new[] { unavailableAccount };
        Assert.False(await center.RefreshPrivateMaterialAsync());
        await center.RevokeAsync(retired.Id);
        for (int i = 0; i < 80; i++)
        {
            var preview = new NotificationDestination("unsaved-" + i, NotificationDestinationKind.Webhook,
                "https://retired-churn-" + i + ".fixture.invalid/unsaved", "", true, "preview-" + i, new[] { AlertKind.System });
            center.Preview(preview, new AlertEvent("preview-" + i, Start, AlertKind.System, "TEST", "Preview", "Unsent preview"));
        }
        Assert.Empty(transport.Requests);
        secrets.UnavailableAccounts = Array.Empty<string>();
        Assert.True(await center.RefreshPrivateMaterialAsync());
        await center.FlushAsync();
        await center.DeliverPendingAsync();

        var request = Assert.Single(transport.Requests);
        Assert.Equal("https://deferred-recipient.fixture.invalid/original", request.Uri);
        Assert.Equal("Bearer deferred-recipient-private-consent", request.Authorization);
        Assert.NotEqual(originalId, request.EventId);
        const string expected = "auth=[redacted]; token=[redacted]; target=[redacted]; authority=[redacted]; visible=ORIGINAL_BODY";
        using (var payload = JsonDocument.Parse(request.Payload))
        {
            Assert.Equal("[redacted]", payload.RootElement.GetProperty("instrument").GetString());
            Assert.Equal("[redacted]", payload.RootElement.GetProperty("source").GetString());
            Assert.Equal(expected, payload.RootElement.GetProperty("message").GetString());
            Assert.Equal(request.EventId, payload.RootElement.GetProperty("id").GetString());
        }
        Assert.Equal(expected, Assert.Single(center.Snapshot().Recent).Message);
        Assert.Equal(unavailable.Id, Assert.Single(center.Snapshot().Deliveries).DestinationId);
        foreach (string exposed in new[] { request.Payload, request.EventId, JsonSerializer.Serialize(center.Snapshot()), File.ReadAllText(fixture.StatePath) })
            foreach (string material in new[] { authorization, token, target, authority })
                Assert.DoesNotContain(material, exposed);
    }

    [Fact]
    public async Task Retiring_unavailable_material_discards_unsafe_deferred_content_instead_of_releasing_it_to_ready_grants()
    {
        await using var fixture = new Fixture();
        var transport = new Endpoint();
        using var http = new HttpClient(transport);
        NotificationDestination unavailable;
        await using (var configured = fixture.Open(http))
        {
            await configured.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://ready-grant.fixture.invalid/original", "",
                "Bearer ready-grant-private-consent", true, new[] { AlertKind.System });
            unavailable = await configured.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://missing-material.fixture.invalid/original", "",
                "Bearer missing-material-private-consent", true, new[] { AlertKind.System });
        }
        var secrets = new SelectivelyUnavailableSecrets(fixture.Secrets)
            { UnavailableAccounts = new[] { AlertCenter.SecretAccount(unavailable.Id, unavailable.Revision) } };
        await using (var center = new AlertCenter(fixture.StatePath, secrets, http, () => fixture.Now))
        {
            Assert.False(await center.RefreshPrivateMaterialAsync());
            Assert.True(center.Raise(AlertKind.System, "TEST", "Unsafe retirement", "UNREDACTED_DEFERRED_CONTENT", "unsafe-before-retirement"));
            await center.FlushAsync();
            Assert.Equal(1, center.Snapshot().NotDurableAlerts);
            await Assert.ThrowsAsync<InvalidOperationException>(() => center.RevokeAsync(unavailable.Id));
            Assert.False(center.Snapshot().Destinations.Single(destination => destination.Id == unavailable.Id).Enabled);
            Assert.Equal(0, center.Snapshot().NotDurableAlerts);
            secrets.UnavailableAccounts = Array.Empty<string>();
            await center.RevokeAsync(unavailable.Id);
            Assert.False(await center.RefreshPrivateMaterialAsync());
            await center.FlushAsync();
            await center.DeliverPendingAsync();
            Assert.Empty(center.Snapshot().Deliveries);
            Assert.Empty(transport.Requests);
        }
        Assert.DoesNotContain("UNREDACTED_DEFERRED_CONTENT", File.ReadAllText(fixture.StatePath));
        await using var restarted = fixture.Open(http);
        Assert.True(await restarted.RefreshPrivateMaterialAsync());
        await restarted.DeliverPendingAsync();
        Assert.Empty(transport.Requests);
        Assert.Empty(restarted.Snapshot().Deliveries);
        Assert.True(restarted.Raise(AlertKind.System, "TEST", "Safe restart", "APPROVED_AFTER_RESTART", "safe-after-retirement-restart"));
        await restarted.FlushAsync();
        await restarted.DeliverPendingAsync();
        var request = Assert.Single(transport.Requests);
        Assert.Equal("https://ready-grant.fixture.invalid/original", request.Uri);
        Assert.Equal("safe-after-retirement-restart", request.EventId);
        using var payload = JsonDocument.Parse(request.Payload);
        Assert.Equal("APPROVED_AFTER_RESTART", payload.RootElement.GetProperty("message").GetString());
    }

    private sealed class AsyncBarrierSecrets(ISecretStore inner) : IAsyncSecretStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancelledReads;
        public int CancelledReads => Volatile.Read(ref _cancelledReads);
        public string Get(string account) => throw new InvalidOperationException("Synchronous private access is forbidden by this fixture.");
        public void Set(string account, string? secret) => throw new InvalidOperationException("Synchronous private access is forbidden by this fixture.");
        public async Task<string> GetAsync(string account, CancellationToken cancellationToken = default)
        {
            if (inner.Get(account).Length > 0)
            {
                Entered.TrySetResult();
                try { await _release.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { Interlocked.Increment(ref _cancelledReads); throw; }
            }
            return inner.Get(account);
        }
        public Task SetAsync(string account, string? secret, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); inner.Set(account, secret); return Task.CompletedTask; }
    }

    [Fact]
    public async Task New_encrypted_content_is_not_published_as_durable_pending_until_storage_confirms_it()
    {
        await using var fixture = new Fixture();
        var transport = new Endpoint();
        using var http = new HttpClient(transport);
        await using var center = fixture.Open(http);
        await center.ConfigureAsync(null, NotificationDestinationKind.Webhook, "https://persist.fixture.invalid/private", "", "",
            true, new[] { AlertKind.System });
        File.Delete(fixture.StatePath);
        Directory.CreateDirectory(fixture.StatePath);
        try
        {
            center.Raise(AlertKind.System, "TEST", "Persistence fixture", "PRIVATE_WAITING_FOR_COMMIT", "persist-stage");
            await Assert.ThrowsAsync<InvalidOperationException>(() => center.FlushAsync());
            var delivery = Assert.Single(center.Snapshot().Deliveries);
            Assert.Equal(NotificationDeliveryState.AwaitingPersistence, delivery.State);
            Assert.Equal(1, center.Snapshot().NotDurableAlerts);
            Assert.NotNull(delivery.EncryptedAlert);
            await center.DeliverPendingAsync();
            Assert.Empty(transport.Requests);
        }
        finally { Directory.Delete(fixture.StatePath); }
        await center.FlushAsync();
        Assert.Equal(0, center.Snapshot().NotDurableAlerts);
        await center.DeliverPendingAsync();
        Assert.Equal(NotificationDeliveryState.Delivered, Assert.Single(center.Snapshot().Deliveries).State);
        Assert.Equal("persist-stage", Assert.Single(transport.Requests).EventId);
        Assert.DoesNotContain("PRIVATE_WAITING_FOR_COMMIT", File.ReadAllText(fixture.StatePath));
    }

    private sealed class SelectivelyUnavailableSecrets(ISecretStore inner) : IAsyncSecretStore
    {
        private string[] _unavailableAccounts = Array.Empty<string>();
        public string[] UnavailableAccounts
        {
            get => Volatile.Read(ref _unavailableAccounts);
            set => Volatile.Write(ref _unavailableAccounts, value);
        }
        public string Get(string account) => throw new InvalidOperationException("Synchronous private access is forbidden by this fixture.");
        public void Set(string account, string? secret) => throw new InvalidOperationException("Synchronous private access is forbidden by this fixture.");
        public Task<string> GetAsync(string account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UnavailableAccounts.Contains(account, StringComparer.Ordinal)
                ? Task.FromException<string>(new InvalidOperationException("Fixture private material is temporarily unavailable."))
                : Task.FromResult(inner.Get(account));
        }
        public Task SetAsync(string account, string? secret, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); inner.Set(account, secret); return Task.CompletedTask; }
    }

    private sealed class SilentWriteSecrets(ISecretStore inner) : ISecretStore
    {
        public bool DropWrites { get; set; }
        public bool DropDeletes { get; set; }
        public string Get(string account) => inner.Get(account);
        public void Set(string account, string? secret)
        {
            if (secret == null ? !DropDeletes : !DropWrites) inner.Set(account, secret);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tradinghub-notifications-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(Root, "notifications.json");
        public DateTime Now { get; set; } = Start;
        public PersistentSecrets Secrets { get; }
        public Fixture() { Directory.CreateDirectory(Root); Secrets = new PersistentSecrets(Path.Combine(Root, "fake-secrets.json")); }
        public AlertCenter Open(HttpClient http) => new(StatePath, new PersistentSecrets(Secrets.Path), http, () => Now);
        public ValueTask DisposeAsync() { Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
    private sealed class PersistentSecrets(string path) : ISecretStore
    {
        public string Path => path;
        private static readonly object Gate = new();
        public string Get(string account)
        {
            lock (Gate) return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!.GetValueOrDefault(account, "") : "";
        }
        public void Set(string account, string? secret)
        {
            lock (Gate)
            {
                var values = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))! : new();
                if (secret == null) values.Remove(account); else values[account] = secret;
                File.WriteAllText(path, JsonSerializer.Serialize(values));
            }
        }
    }
    private sealed record Request(string Uri, string DeliveryId, string EventId, string Authorization, string Payload);
    private sealed class Endpoint : HttpMessageHandler
    {
        public HttpStatusCode Response { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "Fixture response; must never be logged";
        public bool Hold { get; init; }
        public bool Cancelled { get; private set; }
        public ConcurrentQueue<Request> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.RequestUri!.ToString(), request.Headers.GetValues("Idempotency-Key").Single(),
                request.Headers.GetValues("X-TradingHub-Alert-Id").Single(), request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.Single() : "",
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            Entered.TrySetResult();
            if (Hold)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new HttpResponseMessage(Response) { Content = new StringContent(Body) };
        }
    }
}
