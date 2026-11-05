using TradingHub.Data;
using TradingHub.Trading;

namespace TradingHub.Strategies;

/// <summary>In-memory ownership for replacing a nonpersistent runner. Bound to the exact data and account objects;
/// never serialized into live restoration or the production ledger. Owned execution handles permit truthful gap settlement.</summary>
public sealed class LiveRunnerLocalState
{
    internal LiveRunnerLocalState(InstrumentData data, ITradingAccount account, LiveLedger ledger, StrategyOrder[] orders)
    { Data = data; Account = account; Ledger = ledger; Orders = orders; }
    internal InstrumentData Data { get; }
    internal ITradingAccount Account { get; }
    internal LiveLedger Ledger { get; }
    internal StrategyOrder[] Orders { get; }
}
