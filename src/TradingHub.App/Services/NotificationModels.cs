namespace TradingHub.App.Services;

public enum AlertKind { Price, Indicator, Strategy, Risk, Execution, System }
public enum NotificationDestinationKind { Webhook, Telegram }
public enum NotificationDeliveryState { Pending, Delivering, Delivered, Failed, Cancelled, AwaitingPersistence }

public sealed record AlertEvent(string Id, DateTime TimeUtc, AlertKind Kind, string Instrument, string Source, string Message,
    bool AllowExternal = true, string? OutboxEventId = null);

/// <summary>Explicit external consent and nonsecret display metadata. Endpoint is a generic label only; the entire webhook URI is private credential data.</summary>
public sealed record NotificationDestination(string Id, NotificationDestinationKind Kind, string Endpoint, string Target,
    bool Enabled, string Revision, AlertKind[] Kinds);

public sealed record NotificationDelivery(string Id, string DestinationId, string Revision, AlertEvent Alert,
    NotificationDeliveryState State, int Attempts, DateTime NextAttemptUtc, string Status, NotificationCiphertext? EncryptedAlert = null)
{
    internal bool PlaintextReady { get; init; }
}

public sealed record NotificationCiphertext(int Schema, string EventId, string Nonce, string Ciphertext, string Tag);

public sealed record NotificationPreview(string Destination, string Authorization, string Payload, string DeliveryNotice);

public sealed record NotificationSnapshot(IReadOnlyList<AlertEvent> Recent, IReadOnlyList<NotificationDestination> Destinations,
    IReadOnlyList<NotificationDelivery> Deliveries, string Status, int DroppedLocalAlerts, int NotDurableAlerts = 0);

internal sealed record NotificationCredential(string Authorization, bool Enabled, string? FullWebhookUri = null, string? DeliveryKey = null, string? Configuration = null)
{
    private byte[]? _key;
    public override string ToString() => "Private notification permission [redacted]";
    internal ReadOnlySpan<byte> EncryptionKey
    {
        get
        {
            if (_key != null) return _key;
            if (DeliveryKey?.Length != 44) throw new System.Security.Cryptography.CryptographicException("Notification key is unavailable.");
            var key = Convert.FromBase64String(DeliveryKey);
            if (key.Length != 32) throw new System.Security.Cryptography.CryptographicException("Notification key is unavailable.");
            return _key = key;
        }
    }
}

internal sealed class NotificationState
{
    public int Version { get; set; } = 3;
    public List<AlertEvent> Recent { get; set; } = new();
    public List<NotificationDestination> Destinations { get; set; } = new();
    public List<NotificationDelivery> Deliveries { get; set; } = new();
    public List<string> SeenIds { get; set; } = new();
}
