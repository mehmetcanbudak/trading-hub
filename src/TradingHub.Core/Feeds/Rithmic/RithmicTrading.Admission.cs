using TradingHub.Trading;

namespace TradingHub.Feeds.Rithmic;

public sealed partial class RithmicTradingSession
{
    private readonly record struct NativeAccountKey(string Environment, string Account);
    private static readonly Dictionary<NativeAccountKey, HashSet<RithmicTradingSession>> NativeSessions = new();
    private readonly Dictionary<(NativeContract Contract, string Basket), NativeOrderSnapshot> _retiredNativeOrders = new();
    private string _nativeObservationEnvironment = "";

    private readonly record struct NativeOrderSnapshot(RithmicTradingSession Session, string Account, string BasketId,
        string UserTag, bool IsBuy, int PriceType, decimal Quantity, decimal Filled, decimal Unfilled,
        bool CumulativeQuantityKnown, bool Done, bool ReplayAbsent, bool LocalSubmission, long FillRevision,
        string ObservationScope, bool AmendmentPending, bool NativeTerminal, bool Conflicting = false, bool QuantityDisagrees = false)
    {
        public bool Unresolved => Conflicting || !Done && BasketId.Length == 0 ||
            ReplayAbsent && LocalSubmission && PriceType == 2;
    }

    private void RegisterNativeAccountSessions()
    {
        lock (NativeAdmissionGate)
            foreach (var account in Accounts)
            {
                string identity = NativeAccountOf(account.AccountId);
                if (identity.Length == 0) continue;
                var key = new NativeAccountKey(_nativeObservationEnvironment, identity);
                if (!NativeSessions.TryGetValue(key, out var sessions)) NativeSessions[key] = sessions = new(ReferenceEqualityComparer.Instance);
                sessions.Add(this);
            }
    }

    private void StampNativeOrder(RithmicOrderState order, bool submitted = false)
    {
        if (order.NativeObservationEnvironment.Length == 0) order.NativeObservationEnvironment = _nativeObservationEnvironment;
        if (order.NativeAccountIdentity.Length == 0) order.NativeAccountIdentity = NativeAccountOf(order.Account);
        order.LocalSubmission |= submitted;
    }

    private NativeOrderSnapshot SnapshotNativeOrder(RithmicOrderState order)
    {
        decimal quantity = Math.Max(order.Quantity, order.PendingModifyQuantity ?? 0);
        decimal unfilled = Math.Max(order.Unfilled, Math.Max(0, (order.PendingModifyQuantity ?? 0) - order.Filled));
        return new(this, order.Account, order.BasketId, order.UserTag, order.IsBuy, order.PriceType,
            quantity, order.Filled, unfilled, order.CumulativeQuantityKnown, order.Done, order.ReplayAbsent,
            order.LocalSubmission, order.FillRevision, _closeSessionNamespace, order.PendingModifyQuantity != null, order.NativeTerminal);
    }

    private IReadOnlyList<NativeOrderSnapshot> SharedNativeOrders(NativeContract contract, string? preferredScope = null)
    {
        var key = new NativeAccountKey(contract.Environment, contract.Account);
        var baskets = new Dictionary<string, NativeOrderSnapshot>(StringComparer.Ordinal);
        List<NativeOrderSnapshot>? pending = null;
        if (!NativeSessions.TryGetValue(key, out var sessions)) return Array.Empty<NativeOrderSnapshot>();
        void Add(NativeOrderSnapshot next)
        {
            if (next.BasketId.Length == 0) { (pending ??= new()).Add(next); return; }
            if (baskets.TryGetValue(next.BasketId, out var previous)) next = MergeNativeOrder(previous, next, preferredScope);
            baskets[next.BasketId] = next;
        }
        foreach (var session in sessions)
            lock (session._orderGate)
            {
                foreach (var order in session._orders.Values)
                {
                    if (order.NativeObservationEnvironment != contract.Environment || order.NativeAccountIdentity != contract.Account ||
                        order.Symbol != contract.Symbol || order.Exchange != contract.Exchange) continue;
                    Add(session.SnapshotNativeOrder(order));
                }
                foreach (var (identity, retained) in session._retiredNativeOrders)
                    if (identity.Contract == contract) Add(retained);
            }
        if (pending == null) return baskets.Values.ToArray();
        var tags = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var order in baskets.Values)
        {
            if (order.UserTag.Length == 0) continue;
            if (tags.TryGetValue(order.UserTag, out var basket) && basket != order.BasketId) tags[order.UserTag] = null;
            else if (!tags.ContainsKey(order.UserTag)) tags.Add(order.UserTag, order.BasketId);
        }
        var combined = new List<NativeOrderSnapshot>(baskets.Values);
        foreach (var order in pending)
        {
            if (order.UserTag.Length > 0 && tags.TryGetValue(order.UserTag, out var basket) && basket != null)
            {
                var bound = baskets[basket];
                if (bound.IsBuy == order.IsBuy && bound.PriceType == order.PriceType && bound.Quantity >= order.Quantity) continue;
            }
            combined.Add(order);
        }
        return combined;
    }

    private static NativeOrderSnapshot MergeNativeOrder(NativeOrderSnapshot first, NativeOrderSnapshot next, string? preferredScope)
    {
        bool realTerminal = first.NativeTerminal || next.NativeTerminal;
        bool done = realTerminal || first.Done && next.Done;
        var evidence = next.Filled > first.Filled || next.Filled == first.Filled && next.ObservationScope == preferredScope
            ? next : first;
        var transport = !next.Session._closeSessionStopping && (first.Session._closeSessionStopping ||
            first.Done && !next.Done || first.Done == next.Done && next.LocalSubmission) ? next : first;
        return evidence with
        {
            Session = transport.Session, Account = transport.Account,
            UserTag = first.UserTag.Length > 0 ? first.UserTag : next.UserTag,
            Quantity = Math.Max(first.Quantity, next.Quantity), Unfilled = Math.Max(first.Unfilled, next.Unfilled),
            Filled = Math.Max(first.Filled, next.Filled),
            CumulativeQuantityKnown = first.CumulativeQuantityKnown || next.CumulativeQuantityKnown,
            Done = done, NativeTerminal = realTerminal,
            ReplayAbsent = done && !realTerminal && (first.ReplayAbsent || next.ReplayAbsent),
            LocalSubmission = first.LocalSubmission || next.LocalSubmission,
            AmendmentPending = first.AmendmentPending || next.AmendmentPending,
            QuantityDisagrees = first.QuantityDisagrees || next.QuantityDisagrees || first.Quantity != next.Quantity,
            Conflicting = first.Conflicting || next.Conflicting || first.IsBuy != next.IsBuy || first.PriceType != next.PriceType ||
                first.UserTag.Length > 0 && next.UserTag.Length > 0 && first.UserTag != next.UserTag,
        };
    }

    private RithmicTradingSession CancellationSession(NativePositionClose close, string basket)
    {
        var contract = ContractKey(close);
        foreach (var order in SharedNativeOrders(contract, close.ObservationScope))
            if (order.BasketId == basket && !order.Session._closeSessionStopping && order.Session.IsConnected &&
                order.Session.ContractKey(order.Account, close.Symbol, close.Exchange) == contract) return order.Session;
        return this;
    }

    private void RetainTerminalOrderFact(NativeContract contract, NativeOrderSnapshot order)
    {
        if (!NativeSessions.TryGetValue(new(contract.Environment, contract.Account), out var sessions)) return;
        foreach (var session in sessions)
        {
            if (ReferenceEquals(session, this) || session._closeSessionStopping ||
                session.ContractKey(order.Account, contract.Symbol, contract.Exchange) != contract) continue;
            var retained = order with { Session = session };
            lock (session._orderGate)
            {
                var identity = (contract, order.BasketId);
                if (session._retiredNativeOrders.TryGetValue(identity, out var previous))
                    retained = MergeNativeOrder(previous, retained, null);
                session._retiredNativeOrders[identity] = retained;
            }
        }
    }

    private void RetireNativeAccountSessions()
    {
        lock (NativeAdmissionGate)
        {
            if (_ownedCloses.Values.Any(IsReserved)) return;
            lock (_orderGate)
                if (_orders.Values.Any(order => !order.Done || order.ReplayAbsent || order.PendingModifyQuantity != null)) return;
            foreach (var order in _orders.Values)
                RetainTerminalOrderFact(new(order.NativeObservationEnvironment, order.NativeAccountIdentity, order.Symbol, order.Exchange),
                    SnapshotNativeOrder(order));
            foreach (var (identity, retained) in _retiredNativeOrders)
                RetainTerminalOrderFact(identity.Contract, retained);
            _retiredNativeOrders.Clear();
            List<NativeAccountKey>? empty = null;
            foreach (var (key, sessions) in NativeSessions)
                if (sessions.Remove(this) && sessions.Count == 0) (empty ??= new()).Add(key);
            if (empty != null)
                foreach (var key in empty) NativeSessions.Remove(key);
        }
    }
}
