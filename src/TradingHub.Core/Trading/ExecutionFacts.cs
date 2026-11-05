namespace TradingHub.Trading;

/// <summary>The issuer of an execution identity. Epoch separates local simulator runs; real providers must use
/// verified provider/account/environment/instrument identities, never a profile name or a credential.</summary>
public sealed record ExecutionScope(string Provider, string Account, string Environment, string Instrument, string Epoch = "")
{
    public string? ContextId { get; init; }
}

public sealed record ExecutionIdentity(ExecutionScope Scope, string Id);

/// <summary>A reported charge in its original currency. Different currencies cannot be added without conversion.</summary>
public sealed record ExecutionFee(decimal Amount, string? Currency);

/// <summary>An actual execution report, not an order's limit price or cumulative quantity estimate.
/// LocalOrderId binds the current process's order handle only; it is not a durable broker order identity.
/// Repeated identities update the same execution; a new identity represents a distinct execution.</summary>
public sealed record ExecutionReport(
    ExecutionIdentity Identity, long LocalOrderId, string Symbol, DateTime Time, bool Buy, decimal Quantity,
    decimal Price, decimal PointValue, string? AccountingCurrency, ExecutionFee[] Fees, bool FeesComplete)
{
    public string? ContextId { get; init; }
    public string? IntentId { get; init; }
    public string? ClientOrderId { get; init; }
    public NativeOrderIdentity? NativeOrder { get; init; }
}

public enum ExecutionAvailability { Unavailable, Synchronizing, Ready }

/// <summary>Changes after the requested revision. Ready describes report replay, not fee finality.
/// Unavailable is an explicit lack of execution reports, never evidence of no fills or zero fees.</summary>
public readonly record struct ExecutionSnapshot(long Revision, IReadOnlyList<ExecutionReport> Changes, ExecutionAvailability Availability)
{
    public Guid Epoch { get; init; }
    public static ExecutionSnapshot Unavailable => new(0, Array.Empty<ExecutionReport>(), ExecutionAvailability.Unavailable);
}
