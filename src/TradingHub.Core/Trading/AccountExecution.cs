namespace TradingHub.Trading;

/// <summary>One confirmed account-scoped native execution. Quantity is positive. Fee/currency retain native facts;
/// AccountFee is known only when expressed in the account ledger currency, including a confirmed zero. Null is unknown.</summary>
public sealed record AccountExecution(string Id, decimal Quantity, decimal Price, DateTime TimeUtc,
    decimal? Fee = null, string? FeeCurrency = null, decimal? AccountFee = null, long? Sequence = null);
// Sequence is an optional authoritative account-wide tie breaker, never an order ID or guessed opaque-ID ordering.

/// <summary>Confirmed facts for one owned order. Incomplete coverage never authorizes inventing missing execution prices or fees.</summary>
public sealed record AccountExecutionSnapshot(IReadOnlyList<AccountExecution> Executions, bool CompleteCoverage)
{
    /// <summary>Optional canonical provider facts for native adoption-prefix reconciliation, not a second publication path.
    /// Missing facts are unsupported/unverified; their execution identities must never be reconstructed from credentials.</summary>
    public IReadOnlyList<ExecutionReport>? ExecutionFacts { get; init; }
}

/// <summary>Optional confirmed-execution capability. Implementations raise ITradingAccount.Changed for late facts/corrections,
/// return account-scoped stable IDs and causally sorted immutable observations, and report gaps explicitly.
/// Consumers of this capability must not substitute market prices or wall time for unavailable executions.</summary>
public interface IAccountExecutionSource
{
    AccountExecutionSnapshot GetExecutions(Order order);
    /// <summary>Monotonic confirmed-content revision, or -1 when unsupported. Price-only updates do not advance it.</summary>
    long ExecutionRevision => -1;
    /// <summary>Verified provider/environment/authority/native-account scope. Null is unvalidated, never a shared fallback.</summary>
    string? PersistentAccountIdentity => null;
}

/// <summary>Optional distinction between known zero/negative marks, executable observations and absent market prices.</summary>
public interface IObservedMarketPrice
{
    bool HasObservedPrice { get; }
    bool HasKnownPrice => HasObservedPrice;
}
