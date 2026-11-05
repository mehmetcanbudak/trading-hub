using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingHub.App.Services;

/// <summary>Notification-outbox encryption only. No general credential vault or key material is written to ordinary state.</summary>
internal static class NotificationPayload
{
    private const int Schema = 1, MaximumBytes = 16 * 1024;
    internal static string OpaqueId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    internal static string ConfigurationOf(NotificationDestination destination) => OpaqueId(JsonSerializer.Serialize(new {
        kind = (int)destination.Kind, target = destination.Target, kinds = destination.Kinds.OrderBy(k => k).Select(k => (int)k).ToArray() }));
    internal static bool ConsentsTo(NotificationCredential credential, NotificationDestination destination) =>
        string.Equals(credential.Configuration, ConfigurationOf(destination), StringComparison.Ordinal);
    internal static bool HasKey(NotificationCredential credential)
    {
        try { return credential.EncryptionKey.Length == 32; }
        catch { return false; }
    }
    internal static AlertEvent Metadata(AlertEvent alert)
    {
        string id = alert.OutboxEventId ?? OpaqueId(alert.Id);
        return new(id, alert.TimeUtc, alert.Kind, "", "Alert center", "Private content is not stored in ordinary history.", false, id);
    }
    private static byte[] AssociatedData(string eventId, string destinationId, string revision, string deliveryId) =>
        Encoding.UTF8.GetBytes($"TradingHub.NotificationPayload/{Schema}\0{eventId}\0{destinationId}\0{revision}\0{deliveryId}");
    internal static NotificationCiphertext Seal(AlertEvent alert, string destinationId, string revision, string deliveryId, NotificationCredential credential)
    {
        string eventId = alert.OutboxEventId ?? OpaqueId(alert.Id);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(alert with { OutboxEventId = eventId });
        try
        {
            if (plaintext.Length > MaximumBytes) throw new InvalidOperationException("Notification payload exceeds its protected size limit.");
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(credential.EncryptionKey, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(eventId, destinationId, revision, deliveryId));
            return new(Schema, eventId, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    internal static bool IsBounded(NotificationCiphertext? envelope) => envelope is
        { Schema: Schema, EventId.Length: 64, Nonce.Length: 16, Tag.Length: 24, Ciphertext.Length: > 0 and <= ((MaximumBytes + 2) / 3 * 4) };
    internal static AlertEvent Open(NotificationDelivery delivery, NotificationCredential credential)
    {
        var envelope = delivery.EncryptedAlert;
        if (!IsBounded(envelope)) throw new CryptographicException("Protected notification content is unavailable.");
        byte[] nonce = Convert.FromBase64String(envelope!.Nonce), tag = Convert.FromBase64String(envelope.Tag), ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        if (nonce.Length != 12 || tag.Length != 16 || ciphertext.Length > MaximumBytes)
            throw new CryptographicException("Protected notification content is invalid.");
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(credential.EncryptionKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(envelope.EventId, delivery.DestinationId, delivery.Revision, delivery.Id));
            var alert = JsonSerializer.Deserialize<AlertEvent>(plaintext) ?? throw new CryptographicException("Protected notification content is invalid.");
            if (!alert.AllowExternal || alert.OutboxEventId != envelope.EventId || alert.Id.Length is < 1 or > 128 ||
                alert.Instrument.Length > 128 || alert.Source.Length > 128 || alert.Message.Length > 2048 || !Enum.IsDefined(alert.Kind))
                throw new CryptographicException("Protected notification content is invalid.");
            return alert;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
