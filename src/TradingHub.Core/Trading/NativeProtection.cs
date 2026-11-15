namespace TradingHub.Trading;

/// <summary>What a connection's venue-native linked protection guarantees. <see cref="None"/> keeps the client-coordinated path.</summary>
[Flags]
public enum NativeProtectionFeatures
{
    None = 0,
    /// <summary>One stop and one target of equal quantity are sent as a single venue-linked group; when one fills the venue
    /// cancels the other without the application.</summary>
    LinkedStopAndTarget = 1,
    /// <summary>Each leg remains its own working order and is amended natively (price and quantity).</summary>
    NativeAmendment = 2,
}

/// <summary>
/// Optional opt-in venue-native protection. Each leg is still an ordinary owned order with its own prepared intent and
/// durable client identity, so ownership, pre-send journaling, unknown-submission reconciliation, amendment and
/// cancellation keep their existing rules. Only the transport changes: inside <see cref="LinkProtection"/> a stop and a
/// target that form a valid pair are sent as one venue-linked group. Anything else (switched off, a fault, one leg, an
/// unequal pair) is sent exactly as before, so a position is never left without the app-managed fallback.
/// </summary>
public interface INativeProtection
{
    /// <summary>Linked protection available now: <see cref="NativeProtectionFeatures.None"/> when the connection's
    /// opt-in is off, the venue documents no linked contract, or a venue refusal has switched it off for this session.</summary>
    NativeProtectionFeatures NativeProtection { get; }

    /// <summary>Why exchange-side protection is not in use although the connection asked for it (venue refusal or an
    /// unrepresentable pair); null when nothing fell back.</summary>
    string? NativeProtectionFault { get; }

    /// <summary>Collects the protective stop and target submitted on this thread until disposal, then sends them as one
    /// linked group when both are valid exits of equal quantity, or individually otherwise.</summary>
    IDisposable LinkProtection();

    /// <summary>The venue's identity of the linked group an order belongs to, or null when the venue reports none.</summary>
    string? ProtectionGroupOf(Order order);
}

/// <summary>Display state of a position's protection.</summary>
public enum ProtectionMode
{
    None,
    /// <summary>Stop and target are linked by the venue: one filling cancels the other even if the app is closed.</summary>
    ExchangeSide,
    /// <summary>The application coordinates stop/target cancellation and resizing while it runs.</summary>
    AppManaged,
}
