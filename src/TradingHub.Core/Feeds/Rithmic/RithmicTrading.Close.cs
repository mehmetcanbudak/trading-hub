using System.Text.Json;
using TradingHub.Trading;

namespace TradingHub.Feeds.Rithmic;

public sealed class RithmicCloseConfirmation
{
    internal RithmicCloseConfirmation(RithmicTradingSession session, OrderContext context, string instrument,
        string account, NativePositionClose state, string environment)
    { Session = session; Context = context; Instrument = instrument; Account = account; State = state; Environment = environment; }
    public RithmicTradingSession Session { get; }
    public OrderContext Context { get; }
    public string Instrument { get; }
    public string Account { get; }
    public string Symbol => State.Symbol;
    public string Exchange => State.Exchange;
    public decimal ConfirmedNet => State.ConfirmedNet;
    internal NativePositionClose State { get; }
    internal string Environment { get; }
}

public sealed partial class RithmicTradingSession
{
    private static readonly object NativeAdmissionGate = new();
    private readonly record struct NativeContract(string Environment, string Account, string Symbol, string Exchange);
    private static readonly Dictionary<NativeContract, CloseOperation> NativeCloses = new();
    private readonly Dictionary<string, CloseOperation> _ownedCloses = new(StringComparer.Ordinal);
    private readonly HashSet<OrderContext> _closeContexts = new(ReferenceEqualityComparer.Instance);
    private readonly record struct PositionInvalidations(long Flat, long Long, long Short);
    private enum CurrentCloseReadiness { Unknown, NativeTerminal, SessionNonworking, Working }
    private const string CurrentCloseUnresolved =
        "Rithmic current order evidence is unavailable; the original close remains unresolved and was not sent.";
    private readonly Dictionary<string, PositionInvalidations> _positionInvalidations = new(StringComparer.Ordinal);
    private Guid _connectionEpoch = Guid.NewGuid();
    private long _factRevision;
    private bool _closeSessionStopping;
    private bool _closeSessionDisposed;
    private readonly string _closeSessionNamespace = Guid.NewGuid().ToString("N");
    private readonly Dictionary<(string Fcm, string Ib, string Account), string> _nativeAccountIdentities = new();
    private string? _closeEnvironment;

    private sealed class CloseOperation(RithmicTradingSession session, OrderContext context, PreparedOrderIntent intent)
    {
        public RithmicTradingSession Session { get; set; } = session;
        public OrderContext Context { get; } = context;
        public PreparedOrderIntent Intent { get; } = intent;
        public TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Fault;
        public bool Running;
        public Order? Order;
        // Set when the send slot refuses only because a pending send this close waited on is now working.
        public bool CancellationPending;
        public readonly HashSet<string> AwaitedPendingTags = new(StringComparer.Ordinal);
    }

    private string EnvironmentIdentity
    {
        get
        {
            if (_closeEnvironment != null) return _closeEnvironment;
            var uri = new UriBuilder(_settings.Uri) { UserName = "", Password = "", Query = "", Fragment = "" };
            return _closeEnvironment = uri.Uri.GetLeftPart(UriPartial.Path) + "|" + _settings.System;
        }
    }
    private string NativeAccountOf(string account)
    {
        var a = Accounts.FirstOrDefault(item => item.AccountId == account);
        if (a == null || a.FcmId.Length == 0 || a.IbId.Length == 0 || a.AccountId.Length == 0) return "";
        lock (_orderGate)
        {
            var key = (a.FcmId, a.IbId, a.AccountId);
            if (_nativeAccountIdentities.TryGetValue(key, out string? identity)) return identity;
            return _nativeAccountIdentities[key] = JsonSerializer.Serialize(new[] { a.FcmId, a.IbId, a.AccountId });
        }
    }
    private NativeContract ContractKey(NativePositionClose close) => new(EnvironmentIdentity,
        close.NativeAccount.Length > 0 ? close.NativeAccount : "session:" + _closeSessionNamespace + "|" + close.Account, close.Symbol, close.Exchange);
    private NativeContract ContractKey(string account, string symbol, string exchange) => new(EnvironmentIdentity,
        NativeAccountOf(account) is { Length: > 0 } native ? native : "session:" + _closeSessionNamespace + "|" + account, symbol, exchange);

    public RithmicCloseConfirmation CapturePositionClose(OrderContext context, string instrument,
        string account, string symbol, string exchange, decimal confirmedNet)
    {
        lock (NativeAdmissionGate)
        {
            RegisterCloseContext(context, account);
            lock (_orderGate)
            {
                var baselines = SharedNativeOrders(ContractKey(account, symbol, exchange), _closeSessionNamespace)
                    .Where(o => o.BasketId.Length > 0 && (!o.Done || o.Quantity <= 0 || o.Filled < o.Quantity))
                    .Select(o => new PositionCloseCancellation(o.BasketId, o.IsBuy,
                        o.CumulativeQuantityKnown ? o.Filled : null, o.CumulativeQuantityKnown ? o.Filled : null, o.Done)).ToArray();
                var state = new NativePositionClose(account, NativeAccountOf(account), symbol, exchange, confirmedNet,
                    _connectionEpoch, _factRevision, PositionClosePhase.Reconciling, baselines, ObservationScope: _closeSessionNamespace);
                return new(this, context, instrument, account, state, _nativeObservationEnvironment);
            }
        }
    }

    internal void RegisterCloseContext(OrderContext context, string account)
    {
        lock (NativeAdmissionGate)
        {
            if (_closeSessionStopping) throw new RithmicRejectedException("The original Rithmic close session is unavailable.");
            if (_closeContexts.Contains(context)) return;
            foreach (var intent in context.PositionCloses())
            {
                var state = context.PositionCloseState(intent);
                if (state.Account != account) continue;
                if (!_ownedCloses.TryGetValue(intent.Id, out var operation))
                    _ownedCloses[intent.Id] = operation = new CloseOperation(this, context, intent);
                var key = ContractKey(state);
                if (!NativeCloses.TryGetValue(key, out var previous) || previous.Session._closeSessionDisposed ||
                    !IsReserved(previous)) NativeCloses[key] = operation;
            }
            foreach (var previous in context.PreviousPositionCloses())
            {
                var close = previous.Intent.PositionClose!;
                if (close.Account != account) continue;
                if (_ownedCloses.ContainsKey(previous.Intent.Id)) continue;
                var oldContext = new OrderContext(previous.Descriptor, context.StorageRoot);
                var intent = oldContext.PositionCloses().Single(ticket => ticket.Id == previous.Intent.Id);
                var key = new NativeContract(previous.Descriptor.Environment, close.NativeAccount, close.Symbol, close.Exchange);
                if (NativeCloses.TryGetValue(key, out var live) && !live.Session._closeSessionDisposed && IsReserved(live)) continue;
                var unavailable = new CloseOperation(this, oldContext, intent)
                {
                    Fault = "The original Rithmic close session is unavailable after configuration change; its original identity remains unresolved and will not be resent.",
                };
                _ownedCloses[intent.Id] = unavailable;
                NativeCloses[key] = unavailable;
            }
            foreach (var operation in _ownedCloses.Values)
                if (operation.Fault == null) RefreshClose(operation);
            _closeContexts.Add(context);
        }
    }

    public bool HasUnresolvedPositionClose
    {
        get { lock (NativeAdmissionGate) return _ownedCloses.Values.Any(IsReserved); }
    }

    public string? PositionCloseRecoveryStatus
    {
        get { lock (NativeAdmissionGate) return _ownedCloses.Values.FirstOrDefault(operation => operation.Fault != null)?.Fault; }
    }

    private static bool IsReserved(CloseOperation operation)
    {
        if (operation.Fault != null) return true;
        var close = operation.Context.PositionCloseState(operation.Intent);
        return close.KnownReductions != 0 || close.LateFillUnresolved || close.Phase is not
            (PositionClosePhase.Completed or PositionClosePhase.NotSent or PositionClosePhase.Rejected);
    }

    public Task<string?> ClosePositionAsync(RithmicCloseConfirmation confirmation)
    {
        CloseOperation operation;
        lock (NativeAdmissionGate)
        {
            if (!ReferenceEquals(confirmation.Session, this)) return Task.FromResult<string?>("The original Rithmic close session is unavailable.");
            if (_closeSessionStopping) return Task.FromResult<string?>("The original Rithmic close session is unavailable; its outcome remains unresolved.");
            if (confirmation.Environment != EnvironmentIdentity || confirmation.Environment != _nativeObservationEnvironment)
                return Task.FromResult<string?>("The captured Rithmic session environment changed; no close or protection cancellation was sent.");
            if (!NativeFuturesQuantity.TryConvert(Math.Abs(confirmation.ConfirmedNet), out _))
                return Task.FromResult<string?>(NativeFuturesQuantity.Error);
            if (confirmation.State.NativeAccount.Length == 0)
                return Task.FromResult<string?>("The captured Rithmic native account is unavailable; no close or protection cancellation was sent.");
            var key = ContractKey(confirmation.State);
            NativeCloses.TryGetValue(key, out var competing);
            if (competing != null)
            {
                try { competing.Session.RefreshClose(competing); }
                catch (Exception error) { competing.Fault = "Rithmic close ownership is unresolved: " + error.Message; }
            }
            if (competing != null && IsReserved(competing))
            {
                if (competing.Fault != null) return Task.FromResult<string?>(competing.Fault);
                if (!ReferenceEquals(competing.Context, confirmation.Context))
                    return Task.FromResult<string?>("A bounded Rithmic close already reserves this native account and contract in another ownership context.");
                operation = competing;
            }
            else
            {
                var ticket = confirmation.Context.BeginPositionClose(confirmation.Instrument, confirmation.State);
                if (ticket.Error != null) return Task.FromResult<string?>(ticket.Error);
                if (!_ownedCloses.TryGetValue(ticket.Id, out operation!))
                    _ownedCloses[ticket.Id] = operation = new(this, confirmation.Context, ticket);
                NativeCloses[key] = operation;
            }
            if (operation.Result.Task.IsCompleted) return Task.FromResult(CloseOutcome(operation));
            if (operation.Running) return operation.Result.Task;
            operation.Running = true;
        }
        _ = operation.Session.RunCloseAsync(operation);
        return operation.Result.Task;
    }

    private async Task RunCloseAsync(CloseOperation operation)
    {
        var context = operation.Context;
        var intent = operation.Intent;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        bool committedHere = false;
        try
        {
            bool alreadyDispatched = (context.Find(intent.Id) ?? intent.Data).DispatchUncertain;
            var close = context.PositionCloseState(intent);
            if (alreadyDispatched)
            {
                lock (NativeAdmissionGate) RefreshClose(operation);
                operation.Result.TrySetResult(CloseOutcome(operation));
                return;
            }
            if (close.Phase is PositionClosePhase.Completed or PositionClosePhase.NotSent or PositionClosePhase.Rejected)
            {
                operation.Result.TrySetResult(close.Outcome);
                return;
            }
            var cancellations = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    string[] cancel;
                    bool ready;
                    lock (NativeAdmissionGate)
                    {
                        RefreshClose(operation);
                        close = context.PositionCloseState(intent);
                        if (operation.Fault != null) throw new IOException(operation.Fault);
                        ready = OrdersReadyFor(close.Account) && PositionReadyFor(close.Account, close.Symbol, close.Exchange);
                        if (ready)
                        {
                            string observedAccount = NativeAccountOf(close.Account);
                            if (observedAccount != close.NativeAccount)
                                throw new RithmicRejectedException("The captured Rithmic native account is unavailable or changed; no close was sent.");
                            if (context.VerifyNativeAccount(observedAccount) is { } denied) throw new IOException(denied);
                        }
                        List<string>? pending = null;
                        if (ready)
                        {
                            var orders = SharedNativeOrders(ContractKey(close), close.ObservationScope);
                            bool complete = true;
                            foreach (var target in close.Cancellations)
                            {
                                var readiness = CurrentCloseReadinessFor(target.Basket, target.Buy, orders);
                                if (readiness == CurrentCloseReadiness.Unknown)
                                    throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
                                if (readiness != CurrentCloseReadiness.Working) continue;
                                complete = false;
                                if (!cancellations.Contains(target.Basket)) (pending ??= new()).Add(target.Basket);
                            }
                            foreach (var order in orders)
                                if (order.Unresolved && order.UserTag.Length > 0 && order.UserTag != intent.ClientOrderId)
                                    operation.AwaitedPendingTags.Add(order.UserTag);
                            if (complete && !orders.Any(order => order.UserTag != intent.ClientOrderId && order.Unresolved)) break;
                        }
                        cancel = pending?.ToArray() ?? Array.Empty<string>();
                        if (ready && close.Phase != PositionClosePhase.Cancelling)
                            context.UpdatePositionClose(intent, close with { Phase = PositionClosePhase.Cancelling });
                    }
                    foreach (string basket in cancel)
                    {
                        cancellations.Add(basket);
                        RithmicTradingSession cancellationSession;
                        lock (NativeAdmissionGate) cancellationSession = CancellationSession(close, basket);
                        try { await cancellationSession.CancelAsync(close.Account, basket, timeout.Token).ConfigureAwait(false); }
                        catch (RithmicRejectedException) { /* Current native observations, not a command response, resolve cancellation. */ }
                    }
                    await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                }
                string nativeAccount = NativeAccountOf(close.Account);
                if (nativeAccount.Length == 0 || nativeAccount != close.NativeAccount)
                    throw new RithmicRejectedException("The captured Rithmic native account is unavailable or changed; no close was sent.");
                if (context.VerifyNativeAccount(nativeAccount) is { } accountError) throw new IOException(accountError);
                int ceiling;
                if (!NativeFuturesQuantity.TryConvert(Math.Abs(close.ConfirmedNet), out ceiling))
                    throw new RithmicRejectedException(NativeFuturesQuantity.Error);
                operation.CancellationPending = false;
                try
                {
                    await PlaceOwnedAsync(close.Account, close.Symbol, close.Exchange, close.ConfirmedNet < 0, 2, ceiling, 0, 0,
                        intent.ClientOrderId, timeout.Token, () =>
                        {
                            RefreshClose(operation);
                            var current = context.PositionCloseState(intent);
                            if (operation.Fault != null) throw new PositionCloseAdmissionUnresolvedException(operation.Fault);
                            if (NativeAccountOf(current.Account) != current.NativeAccount)
                                throw new RithmicRejectedException("The captured Rithmic native account changed at the send slot; no close was sent.");
                            if (ReducingQuantityUnknown(current))
                                throw new PositionCloseAdmissionUnresolvedException(
                                    "Rithmic reducing-order cumulative quantity is unavailable; position inclusion remains unresolved.");
                            decimal available = CloseCapacity(current);
                            if (current.Invalidated || available <= 0)
                            {
                                string outcome = current.Invalidated ? "The confirmed Rithmic position became flat or changed side; no close was sent." :
                                    "Known reducing fills exhausted the confirmed Rithmic close ceiling; remaining exposure is unresolved.";
                                throw new RithmicRejectedException(outcome);
                            }
                            if (!NativeFuturesQuantity.TryConvert(available, out int quantity)) throw new RithmicRejectedException(NativeFuturesQuantity.Error);
                            var handle = CloseHandle(operation, quantity);
                            handle.QuantityToFill = handle.Unfilled = quantity;
                            if (context.StartDispatch(intent, current.Symbol, current.Exchange, quantity, 0,
                                PositionSnapshotFor(current.Account, current.Symbol, current.Exchange).Net,
                                latest => CheckCurrentCloseAdmission(operation, latest)) is { } denied)
                                throw new RithmicRejectedException(denied);
                            committedHere = true;
                            context.UpdatePositionClose(intent, current with { Phase = PositionClosePhase.Dispatching });
                            return quantity;
                        }).ConfigureAwait(false);
                    break;
                }
                // An equivalent session's order can become working between the last cancellation pass and the
                // send slot. Nothing was committed or sent, so cancel it within the same bounded close.
                catch (RithmicRejectedException) when (!committedHere && operation.CancellationPending) { }
            }
            lock (NativeAdmissionGate)
            {
                RefreshClose(operation);
                close = context.PositionCloseState(intent);
                if (close.Phase == PositionClosePhase.Dispatching)
                    context.UpdatePositionClose(intent, close with { Phase = PositionClosePhase.Working });
            }
            operation.Result.TrySetResult(null);
        }
        catch (Exception ex)
        {
            lock (NativeAdmissionGate)
            {
                try
                {
                    RefreshClose(operation);
                    bool definite = ex is RithmicRejectedException;
                    string outcome = committedHere && !definite ? "Rithmic close outcome is unknown; the original identity remains reserved and will not be resent. " + ex.Message : ex.Message;
                    context.FailPositionClose(intent, new PositionCloseFailure(committedHere, definite, outcome,
                        _factRevision, _closeSessionNamespace), latest => CheckCurrentCloseAdmission(operation, latest));
                }
                catch (Exception persistence) { operation.Fault = "Rithmic close ownership is unresolved: " + persistence.Message; }
                operation.Result.TrySetResult(operation.Fault ?? CloseOutcome(operation));
            }
        }
    }

    private string? CloseOutcome(CloseOperation operation)
    {
        var close = operation.Context.PositionCloseState(operation.Intent);
        if (operation.Fault != null || close.Outcome != null) return operation.Fault ?? close.Outcome;
        if (close.Phase == PositionClosePhase.Completed)
        {
            if (close.KnownReductions > 0)
                return "The Rithmic close reached reported flat; known cancellation-time fills remain reserved because their position inclusion is unresolved.";
            return operation.Session.PositionReadyFor(close.Account, close.Symbol, close.Exchange) &&
                operation.Session.PositionSnapshotFor(close.Account, close.Symbol, close.Exchange).Net == 0
                ? $"{close.Symbol} is already flat: nothing needed closing."
                : "The original Rithmic close is complete; confirm the newly reported position before closing again.";
        }
        return "Rithmic close remains tracked; check its original order and position. No second submission was sent.";
    }

    private static Order CloseHandle(CloseOperation operation, decimal quantity)
    {
        if (operation.Order != null) return operation.Order;
        var intent = operation.Intent;
        var handle = new Order { Security = new Security { Code = operation.Context.PositionCloseState(intent).Symbol, SecurityId = intent.Request.Instrument },
            Direction = intent.Request.Direction, Type = OrderTypes.Market, QuantityToFill = quantity, Unfilled = quantity,
            ReduceOnly = true, Comment = "Close", State = OrderStates.Active, Status = OrderStatus.Placed };
        if (operation.Context.BindOrder(handle, intent) is { } error) throw new IOException(error);
        return operation.Order = handle;
    }

    private static CurrentCloseReadiness CurrentCloseReadinessFor(string basket, bool buy,
        IReadOnlyList<NativeOrderSnapshot> orders)
    {
        foreach (var order in orders)
        {
            if (order.BasketId != basket) continue;
            if (order.IsBuy != buy || order.Unresolved) return CurrentCloseReadiness.Unknown;
            if (order.NativeTerminal) return CurrentCloseReadiness.NativeTerminal;
            return order.Done ? CurrentCloseReadiness.SessionNonworking : CurrentCloseReadiness.Working;
        }
        return CurrentCloseReadiness.Unknown;
    }

    private string? CheckCurrentCloseAdmission(CloseOperation operation, OwnedOrderIntent latest)
    {
        var close = latest.PositionClose!;
        if (_closeSessionStopping || _closeSessionDisposed || !ReferenceEquals(operation.Session, this) ||
            _nativeObservationEnvironment != EnvironmentIdentity || NativeAccountOf(close.Account) != close.NativeAccount ||
            !OrdersReadyFor(close.Account) || !PositionReadyFor(close.Account, close.Symbol, close.Exchange))
            throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
        string? nativeBasket = null;
        foreach (var native in latest.NativeOrders ?? Array.Empty<NativeOrderIdentity>())
        {
            if (native.Kind != "rithmic:basket") continue;
            if (native.Id.Length == 0 || nativeBasket != null && nativeBasket != native.Id)
                throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
            nativeBasket = native.Id;
        }
        var orders = SharedNativeOrders(ContractKey(close), close.ObservationScope);
        string? denied = null;
        bool awaitedWorking = false, unexpectedWorking = false;
        foreach (var target in close.Cancellations)
        {
            var readiness = CurrentCloseReadinessFor(target.Basket, target.Buy, orders);
            if (readiness == CurrentCloseReadiness.Unknown)
                throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
            if (readiness == CurrentCloseReadiness.Working)
            {
                string tag = orders.FirstOrDefault(order => order.BasketId == target.Basket).UserTag ?? "";
                if (tag.Length > 0 && operation.AwaitedPendingTags.Contains(tag)) awaitedWorking = true;
                else unexpectedWorking = true;
                denied = "Rithmic replay or cancellation is unresolved at the send slot; no close was sent.";
            }
        }
        // Only a pending send this close already waited on may be cancelled without a new confirmation.
        operation.CancellationPending = awaitedWorking && !unexpectedWorking;
        foreach (var target in close.Surveillance ?? Array.Empty<PositionCloseObservation>())
            if (CurrentCloseReadinessFor(target.Basket, target.Buy, orders) is
                CurrentCloseReadiness.Unknown or CurrentCloseReadiness.Working)
                throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
        bool discover = close.Phase is PositionClosePhase.Reconciling or PositionClosePhase.Cancelling or
            PositionClosePhase.Dispatching or PositionClosePhase.Working or PositionClosePhase.Unresolved;
        foreach (var order in orders)
        {
            if (order.UserTag == latest.ClientOrderId || order.BasketId.Length > 0 && order.BasketId == nativeBasket) continue;
            if (order.Unresolved) throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
            if (order.BasketId.Length == 0) continue;
            bool covered = false;
            decimal? observed = null;
            foreach (var target in close.Cancellations)
                if (target.Basket == order.BasketId)
                {
                    if (target.Buy != order.IsBuy) throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
                    observed = target.Observed;
                    covered = true;
                    break;
                }
            if (!covered)
                foreach (var target in close.Surveillance ?? Array.Empty<PositionCloseObservation>())
                    if (target.Basket == order.BasketId)
                    {
                        if (target.Buy != order.IsBuy) throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
                        observed = target.Observed;
                        covered = true;
                        break;
                    }
            if (!covered)
            {
                if (!discover || order.Done && order.Quantity > 0 && order.Filled >= order.Quantity &&
                    close.ObservationScope == order.ObservationScope && order.FillRevision <= close.ConfirmationRevision) continue;
                throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
            }
            if (order.CumulativeQuantityKnown && (observed == null || observed.Value < order.Filled))
                throw new PositionCloseAdmissionUnresolvedException(CurrentCloseUnresolved);
        }
        return denied;
    }

    // Subtract known post-confirmation reductions even if PnL might already include them. No cross-plant inclusion is assumed.
    private static bool ReducingQuantityUnknown(NativePositionClose close)
    {
        foreach (var target in close.Cancellations)
            if (target.Buy == (close.ConfirmedNet < 0) && target.Observed == null) return true;
        foreach (var target in close.Surveillance ?? Array.Empty<PositionCloseObservation>())
            if (target.Buy == (close.ConfirmedNet < 0) && target.Observed == null) return true;
        return false;
    }

    private decimal CloseCapacity(NativePositionClose close)
    {
        lock (_orderGate)
        {
            if (ReducingQuantityUnknown(close)) return 0;
            decimal net = _positions.TryGetValue(Key(close.Account, close.Symbol, close.Exchange), out var p) ? p.Net : 0;
            if (net == 0 || Math.Sign(net) != Math.Sign(close.ConfirmedNet)) return 0;
            return Math.Max(0, Math.Min(Math.Abs(close.ConfirmedNet), Math.Abs(net)) - Math.Max(0, close.KnownReductions));
        }
    }

    private void RefreshClose(CloseOperation operation)
    {
        if (_closeSessionDisposed || operation.Session._closeSessionDisposed || operation.Fault != null) return;
        var context = operation.Context;
        var intent = operation.Intent;
        var close = context.PositionCloseState(intent);
        string nativeAccount = NativeAccountOf(close.Account);
        if (nativeAccount.Length > 0 && nativeAccount != close.NativeAccount)
            throw new IOException("The original Rithmic close account no longer matches this transport.");
        var saved = context.Find(intent.Id) ?? intent.Data;
        string? nativeBasket = null;
        foreach (var native in saved.NativeOrders ?? Array.Empty<NativeOrderIdentity>())
        {
            if (native.Kind != "rithmic:basket") continue;
            if (nativeBasket != null && nativeBasket != native.Id)
                throw new IOException("Rithmic close has conflicting native basket identities; its outcome remains unresolved.");
            nativeBasket = native.Id;
        }
        List<PositionCloseCancellation>? changed = null;
        List<PositionCloseObservation>? watched = null;
        RithmicOrderState? own = null;
        bool invalidated = close.Invalidated;
        bool discover = close.Phase is PositionClosePhase.Reconciling or PositionClosePhase.Cancelling or
            PositionClosePhase.Dispatching or PositionClosePhase.Working or PositionClosePhase.Unresolved;
        long revision;
        decimal? net;
        lock (_orderGate)
        {
            revision = _factRevision;
            net = PositionReadyFor(close.Account, close.Symbol, close.Exchange) &&
                _positions.TryGetValue(Key(close.Account, close.Symbol, close.Exchange), out var p) ? p.Net : null;
            if (!saved.DispatchUncertain)
            {
                var invalidations = _positionInvalidations.GetValueOrDefault(Key(close.Account, close.Symbol, close.Exchange));
                long after = close.ObservationScope == _closeSessionNamespace ? close.ConfirmationRevision : 0;
                if (Math.Max(invalidations.Flat, close.ConfirmedNet > 0 ? invalidations.Short : invalidations.Long) > after ||
                    net is { } position && (position == 0 || Math.Sign(position) != Math.Sign(close.ConfirmedNet))) invalidated = true;
            }
            foreach (var order in SharedNativeOrders(ContractKey(close), close.ObservationScope))
            {
                if (order.BasketId.Length == 0) continue;
                if (order.UserTag == intent.ClientOrderId || order.BasketId == nativeBasket)
                {
                    _orders.TryGetValue(order.BasketId, out own);
                    continue;
                }
                var targets = changed ?? (IReadOnlyList<PositionCloseCancellation>)close.Cancellations;
                int index = -1;
                for (int i = 0; i < targets.Count; i++) if (targets[i].Basket == order.BasketId) { index = i; break; }
                var previous = index >= 0 ? targets[index] : null;
                var observations = watched ?? (IReadOnlyList<PositionCloseObservation>)(close.Surveillance ?? Array.Empty<PositionCloseObservation>());
                int watchIndex = -1;
                for (int i = 0; i < observations.Count; i++) if (observations[i].Basket == order.BasketId) { watchIndex = i; break; }
                var priorObservation = watchIndex >= 0 ? observations[watchIndex] : null;
                if (previous == null && priorObservation == null && (!discover ||
                    order.Done && order.Quantity > 0 && order.Filled >= order.Quantity &&
                    close.ObservationScope == order.ObservationScope && order.FillRevision <= close.ConfirmationRevision)) continue;
                decimal? observed = order.CumulativeQuantityKnown
                    ? Math.Max(previous?.Observed ?? priorObservation?.Observed ?? 0, order.Filled)
                    : previous?.Observed ?? priorObservation?.Observed;
                if (previous != null || priorObservation == null && !saved.DispatchUncertain)
                {
                    var next = previous == null ? new PositionCloseCancellation(order.BasketId, order.IsBuy, null, observed, order.Done) :
                        previous with { Observed = observed, Terminal = previous.Terminal || order.Done };
                    if (previous == next) continue;
                    changed ??= close.Cancellations.ToList();
                    if (index >= 0) changed[index] = next; else changed.Add(next);
                }
                else
                {
                    var next = priorObservation == null ? new PositionCloseObservation(order.BasketId, order.IsBuy, null, observed, order.Done) :
                        priorObservation with { Observed = observed, Terminal = priorObservation.Terminal || order.Done };
                    if (priorObservation == next) continue;
                    watched ??= (close.Surveillance ?? Array.Empty<PositionCloseObservation>()).ToList();
                    if (watchIndex >= 0) watched[watchIndex] = next; else watched.Add(next);
                }
            }
            if (own != null && close.Phase == PositionClosePhase.Rejected && saved.Rejection != null)
            {
                if (own.Filled > 0) throw new IOException("Rithmic reported execution after definitive rejection; the original close requires reconciliation.");
                own = null;
            }
            if (own != null)
            {
                var native = new NativeOrderIdentity("rithmic:basket", own.BasketId);
                if (!saved.DispatchUncertain || saved.DispatchQuantity is not { } dispatchedQuantity ||
                    own.IsBuy != (intent.Request.Direction == OrderDirections.Buy) || own.PriceType != 2 ||
                    own.Quantity > 0 && own.Quantity != dispatchedQuantity || own.Filled > dispatchedQuantity)
                    throw new IOException("Rithmic close replay disagrees with its original dispatched side, type or quantity.");
                if (saved.NativeOrders?.Contains(native) != true && context.BindNative(intent, native, notify: false) is { } error)
                    throw new IOException(error);
                var handle = CloseHandle(operation, dispatchedQuantity);
                handle.NativeIdentity = native;
                handle.ExchangeOrderId = own.BasketId;
                RithmicBroker.Apply(own, handle);
                if (own.Done && own.ReplayAbsent)
                    close = close with { Phase = PositionClosePhase.Unresolved,
                        Outcome = "Working replay absence does not resolve the tracked Rithmic market close; its original identity remains reserved." };
                else if (own.Done && (!own.CumulativeQuantityKnown ||
                    !own.Cancelled && !own.Rejected && own.Filled < (saved.DispatchQuantity ?? intent.Request.Quantity)))
                    close = close with { Phase = PositionClosePhase.Unresolved,
                        Outcome = "The terminal Rithmic close has no complete observed cumulative quantity; its original identity remains reserved." };
                else if (own.Done)
                {
                    if (context.RecordNativeTerminal(handle) is { } terminalError) throw new IOException(terminalError);
                    if (own.Rejected) close = close with { Phase = PositionClosePhase.Rejected, Outcome = "Rithmic rejected the tracked close: " + own.Reason };
                    else if (close.LateFillUnresolved || saved.PositionClose!.Phase == PositionClosePhase.Completed &&
                        saved.TerminalOrder is { } terminal && own.Filled > terminal.CumulativeQuantity)
                        close = close with { Phase = PositionClosePhase.Unresolved, LateFillUnresolved = true,
                            Outcome = "A late fill changed the completed Rithmic close; its remaining position inclusion is unresolved." };
                    else if (net == 0 || close.Phase == PositionClosePhase.Completed)
                        close = close with { Phase = PositionClosePhase.Completed, Outcome = null,
                            SettledRevision = close.Phase == PositionClosePhase.Completed ? close.SettledRevision : revision,
                            SettledObservationScope = close.Phase == PositionClosePhase.Completed ? close.SettledObservationScope : _closeSessionNamespace };
                    else close = close with { Phase = PositionClosePhase.Unresolved,
                        Outcome = "Rithmic close is terminal but position/fill inclusion remains unresolved; no new close identity was created." };
                }
                else close = close with { Phase = PositionClosePhase.Working };
            }
        }
        decimal reductions = close.KnownReductions;
        var cancellationFacts = changed?.ToArray() ?? close.Cancellations;
        var observationFacts = watched?.ToArray() ?? close.Surveillance;
        decimal observedReductions = 0;
        foreach (var target in cancellationFacts)
            if (target.Buy == (close.ConfirmedNet < 0) && target.Observed is { } reportedQuantity)
                observedReductions += Math.Max(0, reportedQuantity - (target.Baseline ?? 0));
        foreach (var target in observationFacts ?? Array.Empty<PositionCloseObservation>())
            if (target.Buy == (close.ConfirmedNet < 0) && target.Observed is { } reportedQuantity)
                observedReductions += Math.Max(0, reportedQuantity - (target.Baseline ?? 0));
        reductions = Math.Max(reductions, observedReductions);
        if (saved.PositionClose!.Phase == PositionClosePhase.Completed && reductions > saved.PositionClose.KnownReductions)
            close = close with { Phase = PositionClosePhase.Unresolved, LateFillUnresolved = true,
                Outcome = "A late reducing fill changed the completed Rithmic close; its remaining position inclusion is unresolved." };
        context.UpdatePositionClose(intent, close with { Cancellations = cancellationFacts, Surveillance = observationFacts,
            KnownReductions = reductions, Invalidated = invalidated });
    }

    private void ObservePositionCloses(string? changedBasket = null, NativeContract? changedContract = null)
    {
        lock (NativeAdmissionGate)
        {
            if (_closeSessionDisposed) return;
            foreach (var (contract, operation) in NativeCloses)
            {
                if (operation.Fault != null || operation.Session._closeSessionDisposed ||
                    changedContract is { } changed && changed != contract) continue;
                try
                {
                    operation.Context.PositionCloseState(operation.Intent);
                    var data = operation.Context.Find(operation.Intent.Id) ?? operation.Intent.Data;
                    if (data.PositionClose is { Phase: PositionClosePhase.Completed or PositionClosePhase.NotSent or PositionClosePhase.Rejected } settled)
                    {
                        bool relevant = false;
                        if (changedBasket != null)
                        {
                            foreach (var target in settled.Cancellations)
                                if (target.Basket == changedBasket) { relevant = true; break; }
                            foreach (var target in settled.Surveillance ?? Array.Empty<PositionCloseObservation>())
                                if (target.Basket == changedBasket) { relevant = true; break; }
                            foreach (var native in data.NativeOrders ?? Array.Empty<NativeOrderIdentity>())
                                if (native.Kind == "rithmic:basket" && native.Id == changedBasket) { relevant = true; break; }
                        }
                        if (!relevant) continue;
                    }
                    operation.Session.RefreshClose(operation);
                }
                catch (Exception ex) { operation.Fault = "Rithmic close ownership is unresolved: " + ex.Message; }
            }
        }
    }

    private static (decimal Reductions, bool Unknown) StoppedCloseReductionBudget(NativePositionClose close,
        OwnedOrderIntent owned, string closeTag, IReadOnlyList<NativeOrderSnapshot> orders)
    {
        bool MissingCumulative(string basket)
        {
            for (int i = 0; i < orders.Count; i++)
                if (orders[i].BasketId == basket && orders[i].CumulativeQuantityKnown) return false;
            return true;
        }
        bool buy = close.ConfirmedNet < 0;
        foreach (var target in close.Cancellations)
            if (target.Buy == buy && target.Observed == null && MissingCumulative(target.Basket)) return (close.KnownReductions, true);
        foreach (var target in close.Surveillance ?? Array.Empty<PositionCloseObservation>())
            if (target.Buy == buy && target.Observed == null && MissingCumulative(target.Basket)) return (close.KnownReductions, true);
        decimal reductions = close.KnownReductions;
        bool discover = close.Phase is PositionClosePhase.Reconciling or PositionClosePhase.Cancelling or
            PositionClosePhase.Dispatching or PositionClosePhase.Working or PositionClosePhase.Unresolved;
        for (int i = 0; i < orders.Count; i++)
        {
            var order = orders[i];
            if (order.BasketId.Length == 0 || order.IsBuy != buy || order.UserTag == closeTag) continue;
            bool own = false, known = false;
            foreach (var native in owned.NativeOrders ?? Array.Empty<NativeOrderIdentity>())
                if (native.Kind == "rithmic:basket" && native.Id == order.BasketId) { own = true; break; }
            if (own) continue;
            decimal observed = 0;
            foreach (var target in close.Cancellations)
                if (target.Basket == order.BasketId) { known = true; observed = Math.Max(observed, target.Observed ?? target.Baseline ?? 0); }
            foreach (var target in close.Surveillance ?? Array.Empty<PositionCloseObservation>())
                if (target.Basket == order.BasketId) { known = true; observed = Math.Max(observed, target.Observed ?? target.Baseline ?? 0); }
            if (!known && (!discover || order.Done && order.Quantity > 0 && order.Filled >= order.Quantity &&
                close.ObservationScope == order.ObservationScope && order.FillRevision <= close.ConfirmationRevision)) continue;
            if (!order.CumulativeQuantityKnown && !known) return (reductions, true);
            if (order.CumulativeQuantityKnown) reductions += Math.Max(0, order.Filled - observed);
        }
        return (reductions, false);
    }

    internal string? CheckNativeAdmission(OrderContext context, PreparedOrderIntent intent, string account,
        string symbol, string exchange, decimal quantity, string? predecessorBasket = null)
    {
        lock (NativeAdmissionGate)
        {
            RegisterCloseContext(context, account);
            if (!OrdersReadyFor(account) || !PositionReadyFor(account, symbol, exchange)) return "Rithmic replay is incomplete at native admission.";
            if (NativeAccountOf(account).Length == 0 && _ownedCloses.Values.Any(pendingOperation =>
                ReferenceEquals(pendingOperation.Context, context) && IsReserved(pendingOperation) &&
                pendingOperation.Context.PositionCloseState(pendingOperation.Intent) is { } pending && pending.Account == account &&
                pending.Symbol == symbol && pending.Exchange == exchange))
                return "Native Rithmic account evidence is unavailable for a reserved close; no competing dispatch was sent.";
            if (!NativeCloses.TryGetValue(ContractKey(account, symbol, exchange), out var operation) || !IsReserved(operation)) return null;
            operation.Session.RefreshClose(operation);
            var close = operation.Context.PositionCloseState(operation.Intent);
            if (predecessorBasket != null && close.Cancellations.Any(target => target.Basket == predecessorBasket))
                return "Cancellation for the bounded Rithmic close superseded this amendment.";
            if (intent.IsPriceOnlyReplacement && predecessorBasket != null)
            {
                foreach (var order in SharedNativeOrders(ContractKey(account, symbol, exchange), close.ObservationScope))
                    if (order.BasketId == predecessorBasket)
                        return order.Conflicting || order.QuantityDisagrees || order.AmendmentPending
                            ? "Rithmic amendment state differs across equivalent sessions; wait for matching working replay before repricing." : null;
                return "Rithmic replay is incomplete at native admission.";
            }
            // Other exits can use only capacity not reserved by the bounded close and its known fills.
            decimal net = PositionSnapshotFor(account, symbol, exchange).Net;
            decimal signed = intent.Request.Direction == OrderDirections.Buy ? quantity : -quantity;
            if (RealOrderPermission.IncreasesExposure(net, signed)) return "A bounded Rithmic close reserves this native contract; new exposure is disabled.";
            bool sameSide = net != 0 && Math.Sign(net) == Math.Sign(close.ConfirmedNet);
            var owned = operation.Context.Find(operation.Intent.Id) ?? operation.Intent.Data;
            var orders = SharedNativeOrders(ContractKey(account, symbol, exchange), close.ObservationScope);
            var budget = operation.Session._closeSessionDisposed
                ? StoppedCloseReductionBudget(close, owned, operation.Intent.ClientOrderId, orders)
                : (Reductions: close.KnownReductions, Unknown: ReducingQuantityUnknown(close));
            decimal outstanding = 0;
            for (int i = 0; i < orders.Count; i++)
            {
                var order = orders[i];
                if (order.Conflicting)
                    return "Rithmic reducing-order cumulative quantity is unavailable; competing reducing capacity remains unresolved.";
                if ((!order.Done || order.Unresolved) && order.IsBuy == (net < 0) &&
                    order.UserTag != operation.Intent.ClientOrderId && order.BasketId != predecessorBasket)
                    outstanding += order.Unfilled;
            }
            if (sameSide && budget.Unknown)
                return "Rithmic reducing-order cumulative quantity is unavailable; competing reducing capacity remains unresolved.";
            decimal reserved = !sameSide ? 0 : owned.DispatchUncertain
                ? owned.DispatchQuantity ?? Math.Abs(close.ConfirmedNet) : operation.Session.CloseCapacity(close);
            decimal reductions = sameSide ? budget.Reductions : 0;
            return quantity <= Math.Max(0, Math.Abs(net) - reductions - reserved - outstanding) ? null :
                "The bounded Rithmic close already reserves the available reducing capacity.";
        }
    }

    private void CheckRawCloseReservation(string account, string symbol, string exchange, bool ownedAdmission)
    {
        if (_closeSessionStopping) throw new RithmicRejectedException("The original Rithmic close session is unavailable.");
        if (!ownedAdmission && NativeCloses.TryGetValue(ContractKey(account, symbol, exchange), out var close) && IsReserved(close))
            throw new RithmicRejectedException("An owned bounded close reserves this Rithmic contract; uncoordinated native dispatch is disabled.");
    }

    private void StopPositionCloseAdmission()
    {
        lock (NativeAdmissionGate) _closeSessionStopping = true;
    }

    public async Task StopAsync()
    {
        Dispose();
        Task[] work;
        lock (NativeAdmissionGate)
            work = _ownedCloses.Values.Where(operation => operation.Running).Select(operation => (Task)operation.Result.Task)
                .Append(_loop ?? Task.CompletedTask).ToArray();
        try { await Task.WhenAll(work).ConfigureAwait(false); }
        finally
        {
            lock (NativeAdmissionGate)
            {
                _closeSessionDisposed = true;
                RetireNativeAccountSessions();
            }
        }
    }
}
