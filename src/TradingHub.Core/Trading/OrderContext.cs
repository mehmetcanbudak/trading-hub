using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingHub.Common;

namespace TradingHub.Trading;

/// <summary>Local routing namespace; native account evidence is separate and never a credential fingerprint.</summary>
public sealed record AccountContextDescriptor(string Id, string Provider, string Product, string Environment,
    string LocalNamespace, string Generation, string? VerifiedNativeAccount = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? OwnershipNamespace = null);
public sealed record NativeOrderIdentity(string Kind, string Id);
public sealed record NativeWorkingOrder(NativeOrderIdentity Parent, NativeOrderIdentity Child, OrderTypes Type,
    string NativeInstrument, string NativeExchange);
public sealed record NativeOrderTerminal(NativeOrderIdentity Native, decimal CumulativeQuantity);
public sealed record NativeOrderAmendment(NativeOrderIdentity Native, string ClientOrderId, decimal TotalQuantity, decimal Price);

internal readonly record struct NativeAmendmentDispatch(int TotalQuantity, decimal PredecessorFilled)
{
    internal decimal RemainingQuantity => TotalQuantity - PredecessorFilled;
}

public enum OrderOwnerKind { Manual, Strategy }
public sealed record OrderOwner(string Id, string? LedgerKey = null, Guid LedgerGeneration = default, OrderOwnerKind Kind = OrderOwnerKind.Manual);
public sealed record OrderRequest(string Instrument, OrderDirections Direction, OrderTypes Type, decimal Quantity,
    decimal Price = 0, bool ReduceOnly = false, string Comment = "", decimal StrategyQuantity = 0, bool Protective = false,
    decimal? MaximumOwnedQuantity = null);

/// <summary>Identity prepared before dispatch. Native route resolution and dispatch uncertainty must also commit
/// through StartDispatch before a transport can send. Only OrderContext can issue these tickets.</summary>
public sealed class PreparedOrderIntent
{
    private int _attempted;
    internal bool Attempted => Volatile.Read(ref _attempted) != 0;
    internal bool TryAttempt() => Interlocked.Exchange(ref _attempted, 1) == 0;
    // Only for a send-slot refusal proven to precede any durable dispatch record or transport send.
    internal void ReleaseAttempt() => Volatile.Write(ref _attempted, 0);
    internal PreparedOrderIntent(OrderContext context, OwnedOrderIntent data, string? error = null)
    { Context = context; Data = data; Error = error; }
    internal OrderContext Context { get; }
    internal OwnedOrderIntent Data { get; }
    internal Order? BoundOrder { get; set; }
    public string Id => Data.Id;
    public string LogicalOrderId => Data.LogicalOrderId;
    public string ClientOrderId => Data.ClientOrderId;
    public OrderOwner Owner => Data.Owner;
    public OrderRequest Request => Data.Request;
    public string? Error { get; }
    public bool IsReplacement => Data.PredecessorId != null;
    public bool IsPriceOnlyReplacement => Data.PriceOnly;
    public string? PredecessorId => Data.PredecessorId;
    /// <summary>Raw logical predecessor fill, including any adoption prefix; null means legacy recovery needs native evidence.</summary>
    public decimal? PredecessorFilled => IsReplacement ? Context.Find(Id)?.PredecessorFilled ?? Data.PredecessorFilled : 0;
}

/// <summary>Persisted attribution, including an immutable pre-adoption execution prefix.</summary>
public sealed record OwnedOrderIntent(string Id, string LogicalOrderId, string ClientOrderId, OrderOwner Owner,
    OrderRequest Request, string ContextId, string Generation, string? PredecessorId = null,
    decimal QuantityBaseline = 0, ExecutionIdentity[]? PriorExecutions = null, bool BaselineKnown = true,
    bool Adopted = false, bool EphemeralPreparation = false, bool DispatchUncertain = false,
    string? NativeInstrument = null, string? NativeExchange = null, NativeOrderIdentity[]? NativeOrders = null,
    string? Rejection = null, bool Emergency = false, decimal MaximumReduction = 0, bool PriceOnly = false,
    decimal VirtualPerUnit = 0, decimal? DispatchQuantity = null, decimal? DispatchPrice = null, decimal QuantityProjected = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    NativeWorkingOrder? WorkingOrder = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool EmergencyQuantityFolded = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    decimal EmergencyQuantityAccounted = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    decimal? PredecessorFilled = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    NativeOrderTerminal? TerminalOrder = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ExecutionIdentity[]? OwnedPriorExecutions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ExecutionIdentity[]? ImportedPriorExecutions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NativePositionClose? PositionClose = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NativeOrderAmendment? AcceptedNativeAmendment = null);
public sealed record ExecutionReceipt(string Id, ExecutionCursor Cursor, ExecutionReport Report)
{
    public static ExecutionReceipt Create(ExecutionCursor cursor, ExecutionReport report) =>
        new(OrderContext.Hash(cursor.Epoch.ToString("N") + "\n" + cursor.Revision + "\n" +
            JsonSerializer.Serialize(report.Identity)), cursor, report);
}
public sealed record ExecutionProjectionCheckpoint(OrderOwner Owner, string LogicalOrderId, ExecutionCursor Cursor, ExecutionReport Report);
public sealed record OwnedOrderQuantity(OwnedOrderIntent Intent, DateTime Time, bool Buy, decimal CumulativeQuantity,
    string Symbol, decimal PointValue, PhysicalOrderAccounting? AccountingOrder = null);

/// <summary>Account-lifetime ownership. Disk lookup is by exact scoped ID, not by timestamps, comments or price.
/// Ephemeral mode is exclusively for a simulator with no external transport; native accounts require an explicit root.</summary>
public sealed partial class OrderContext
{
    private static readonly OrderTypes[] EmergencyTypes = { OrderTypes.Market, OrderTypes.Stop, OrderTypes.Limit };
    private readonly object _gate = new();
    private readonly Dictionary<string, OwnedOrderIntent> _recent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _ephemeralNative = new(StringComparer.Ordinal);
    private readonly Dictionary<OrderOwner, List<PreparedOrderIntent>> _emergency = new();
    private readonly Dictionary<(OrderOwner Owner, string Instrument, string NativeInstrument, string NativeExchange), EmergencyExposure> _emergencyExposure = new();
    private readonly Dictionary<OrderOwner, Dictionary<string, OwnedOrderQuantity>> _unprojectedQuantities = new();
    private IReadOnlyList<OwnedOrderQuantity>? _pendingQuantities;
    private long _dispatchCount;
    private string? _projectionError;
    private string? _lastError;
    private bool _storageFault, _identityFault;
    private bool _configurationFrozen;
    private readonly Dictionary<string, string> _bindingFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _releasedCloseDenials = new(StringComparer.Ordinal);
    private OrderOwnershipJournal? _journal;
    private Func<OrderRequest, string?>? _permission;
    private Func<OrderRequest, decimal, string?>? _nativePermission;
    public AccountContextDescriptor Descriptor { get; private set; }
    public IExecutionSource Executions { get; }
    public string? StorageRoot { get; private set; }
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                if (_projectionError != null) return _projectionError;
                foreach (var error in _bindingFailures.Values) return error;
                return _lastError;
            }
        }
        private set => _lastError = value;
    }
    public bool HasStorageFault { get { lock (_gate) return _storageFault || _identityFault || _projectionError != null || _bindingFailures.Count > 0; } }
    /// <summary>Exact in-process captured quantities awaiting durable projection; never queries a provider or order history.</summary>
    public bool HasUnprojectedQuantity(OrderOwner owner)
    {
        lock (_gate) return _unprojectedQuantities.ContainsKey(owner);
    }
    /// <summary>Actual outstanding in-process captures, including observations made before subscription.
    /// The cached snapshot changes only when outstanding captures change; it is not crash-durable evidence.</summary>
    public IReadOnlyList<OwnedOrderQuantity> PendingQuantities()
    {
        lock (_gate)
        {
            if (_pendingQuantities != null) return _pendingQuantities;
            if (_unprojectedQuantities.Count == 0) return _pendingQuantities = Array.Empty<OwnedOrderQuantity>();
            var captures = new List<OwnedOrderQuantity>();
            foreach (var pending in _unprojectedQuantities.Values) captures.AddRange(pending.Values);
            return _pendingQuantities = captures.AsReadOnly();
        }
    }
    public event Action? OwnershipChanged;
    public event Action<OwnedOrderQuantity>? QuantityChanged;
    public OrderOwner ManualOwner => new("manual:" + (Descriptor.OwnershipNamespace ?? Descriptor.Id));

    public OrderContext(AccountContextDescriptor descriptor, string? storageRoot = null, IExecutionSource? executions = null)
    {
        Descriptor = descriptor;
        Executions = executions ?? (descriptor.Provider switch
        {
            "simulator" => new ExecutionLog(),
            "Binance" or "Tradovate" => new ExecutionLog(ExecutionAvailability.Synchronizing),
            _ => new UnavailableExecutionSource(),
        });
        if (storageRoot != null) AttachStorage(storageRoot);
    }

    public static OrderContext EphemeralSimulation(string account) => new(new(Guid.NewGuid().ToString("N"),
        "simulator", "paper", "local simulation", account, Guid.NewGuid().ToString("N"), account));

    /// <summary>The shared simulator's durable context for this process, under <paramref name="root"/>.</summary>
    public static OrderContext ProcessSimulation(string account, string root) => new(EphemeralSimulation(account).Descriptor, root);

    public void Configure(AccountContextDescriptor descriptor, string? root, Func<OrderRequest, string?>? permission = null,
        Func<OrderRequest, decimal, string?>? nativePermission = null)
    {
        lock (_gate)
        {
            if (_recent.Count != 0 || _dispatchCount != 0)
                throw new InvalidOperationException("A used order context cannot be moved to another account or generation.");
            string? full = root == null ? null : Path.GetFullPath(root);
            if (_configurationFrozen)
            {
                if (Descriptor != descriptor || StorageRoot != full)
                    throw new InvalidOperationException("A configured account's identity, generation and ownership root cannot change.");
                _permission = permission;
                _nativePermission = nativePermission;
                return;
            }
            // Validate and durably bind the candidate before exposing any of its routing authority.
            var journal = full == null ? null : new OrderOwnershipJournal(full, descriptor);
            Descriptor = descriptor;
            _journal = journal;
            StorageRoot = full;
            _permission = permission;
            _nativePermission = nativePermission;
            _configurationFrozen = true;
        }
    }

    public void AttachStorage(string root)
    {
        lock (_gate)
        {
            string full = Path.GetFullPath(root);
            if (_configurationFrozen)
            {
                if (_journal == null || StorageRoot != full)
                    throw new InvalidOperationException("An account's captured ownership root, including a read-only binding, cannot change.");
                return;
            }
            var journal = new OrderOwnershipJournal(full, Descriptor);
            // Earlier ephemeral simulator orders stay explicitly ephemeral, never retroactively durable-before-send.
            foreach (var intent in _recent.Values) journal.SaveIntent(intent);
            foreach (var binding in _ephemeralNative)
                foreach (string id in binding.Value)
                    if (_recent.TryGetValue(id, out var intent))
                        foreach (var native in intent.NativeOrders ?? Array.Empty<NativeOrderIdentity>()) journal.Bind(native, intent);
            _journal = journal;
            StorageRoot = full;
            _configurationFrozen = true;
        }
    }

    public OrderOwner LedgerOwner(string key, Guid generation) => new(
        Hash(Descriptor.Id + "\n" + key + "\n" + generation.ToString("N")), key, generation, OrderOwnerKind.Strategy);

    public PreparedOrderIntent Prepare(OrderOwner owner, OrderRequest request, Order? predecessor = null, string? clientOrderId = null)
    {
        lock (_gate)
        {
            string id = Guid.NewGuid().ToString("N");
            var previous = predecessor == null ? null : Find(predecessor.IntentId);
            var data = new OwnedOrderIntent(id, predecessor?.LogicalOrderId ?? id, clientOrderId ?? "th-" + Guid.NewGuid().ToString("N"),
                owner, request, Descriptor.Id, Descriptor.Generation, predecessor?.IntentId,
                previous?.QuantityBaseline ?? 0, previous?.PriorExecutions, previous?.BaselineKnown ?? true,
                EphemeralPreparation: _journal == null,
                VirtualPerUnit: previous?.VirtualPerUnit ?? (request.Quantity > 0 ? request.StrategyQuantity / request.Quantity : 0),
                WorkingOrder: previous?.WorkingOrder, OwnedPriorExecutions: previous?.OwnedPriorExecutions,
                ImportedPriorExecutions: previous?.ImportedPriorExecutions);
            string? error = null;
            try
            {
                if (string.IsNullOrEmpty(owner.Id) || request.Quantity <= 0 || string.IsNullOrEmpty(request.Instrument))
                    throw new InvalidOperationException("An order needs an owner, instrument and positive quantity.");
                if (Descriptor.Provider != "simulator" && _journal == null)
                    throw new InvalidOperationException("Native order ownership has not been configured with an explicit storage root.");
                if (predecessor != null && (predecessor.ContextId != Descriptor.Id || predecessor.OwnerId != owner.Id ||
                    string.IsNullOrEmpty(predecessor.LogicalOrderId) || string.IsNullOrEmpty(predecessor.IntentId)))
                    throw new InvalidOperationException("The replacement does not belong to this exact account and owner.");
                if (predecessor != null && (previous == null || previous.Owner != owner ||
                    previous.ContextId != Descriptor.Id || previous.Generation != Descriptor.Generation ||
                    previous.LogicalOrderId != predecessor.LogicalOrderId || previous.Request.Instrument != request.Instrument ||
                    previous.Request.Direction != request.Direction ||
                    (previous.WorkingOrder is { } working && predecessor.NativeIdentity == working.Child
                        ? working.Type : previous.Request.Type) != request.Type ||
                    previous.Request.ReduceOnly != request.ReduceOnly || predecessor.Direction != request.Direction ||
                    predecessor.Type != request.Type || predecessor.ReduceOnly != request.ReduceOnly))
                    throw new InvalidOperationException("A replacement cannot change the predecessor's routed instrument, side, type or reduction mode.");
                Save(data);
                LastError = null;
                _storageFault = false;
            }
            catch (Exception ex)
            {
                error = LastError = "Could not prepare owned order: " + ex.Message;
                _storageFault = ex is IOException or UnauthorizedAccessException;
                if ((request.ReduceOnly || request.Protective) && predecessor == null && _emergency.TryGetValue(owner, out var exits))
                {
                    for (int i = exits.Count - 1; i >= 0; i--)
                    {
                        var reserved = exits[i];
                        if (reserved.Request.Instrument != request.Instrument || reserved.Request.Direction != request.Direction ||
                            reserved.Request.Type != request.Type || reserved.Data.MaximumReduction < request.Quantity || reserved.Attempted) continue;
                        exits.RemoveAt(i);
                        return new(this, reserved.Data with { Request = request with { ReduceOnly = true } });
                    }
                    error += "; no matching unused bounded emergency identity remains";
                }
            }
            return new(this, data, error);
        }
    }

    public PreparedOrderIntent PrepareReplacement(Order order, decimal price, decimal quantity)
    {
        var previous = FindBoundOrder(order);
        if (previous != null) RestoreBinding(order, previous);
        else if (order.ContextId == Descriptor.Id && order.NativeIdentity != null)
            previous = Adopt(order, ManualOwner, order.Filled, Array.Empty<ExecutionIdentity>(), order.Filled == 0,
                protective: order.ReduceOnly);
        var owner = previous?.Owner ?? new OrderOwner(order.OwnerId ?? "");
        return Prepare(owner, new(previous?.Request.Instrument ?? order.Security.SecurityId, order.Direction, order.Type, quantity, price, order.ReduceOnly,
            order.Comment, previous?.Request.StrategyQuantity ?? 0, previous?.Request.Protective ?? false,
            previous?.Request.MaximumOwnedQuantity), order);
    }

    public PreparedOrderIntent PreparePriceUpdate(Order order, decimal price)
    {
        var ticket = PrepareReplacement(order, price, order.Unfilled);
        if (ticket.Error != null) return ticket;
        lock (_gate)
        {
            var data = ticket.Data with { PriceOnly = true };
            try { Save(data); return new(this, data); }
            catch (Exception ex) { return new(this, data, LastError = "Could not prepare price-only amendment: " + ex.Message); }
        }
    }

    /// <summary>Non-consuming authorization between a definitive predecessor cancel and child dispatch.</summary>
    public string? CheckDispatch(PreparedOrderIntent intent, decimal? actualQuantity = null, decimal? reconciledPosition = null)
    {
        lock (_gate)
        {
            if (intent.Error != null) return intent.Error;
            if (!ReferenceEquals(intent.Context, this) || intent.Data.ContextId != Descriptor.Id || intent.Data.Generation != Descriptor.Generation)
                return "The prepared routing context no longer matches.";
            if (!intent.Data.Emergency && Find(intent.Id)?.Rejection is { } rejected) return "This operation was definitively rejected before reuse: " + rejected;
            if (Descriptor.Provider != "simulator" && _journal == null) return "Native ownership storage is not configured.";
            if (!intent.Request.ReduceOnly && (_projectionError != null || _bindingFailures.Count > 0))
                return _projectionError ?? "Native ownership persistence is unresolved; new exposure is disabled.";
            decimal quantity = actualQuantity ?? intent.Request.Quantity;
            if (quantity <= 0) return "The routed quantity must be positive.";
            if (intent.Data.Emergency && (!intent.Request.ReduceOnly || quantity > intent.Data.MaximumReduction))
                return "The emergency reduction exceeds its durable quantity/side authority.";
            var request = quantity == intent.Request.Quantity ? intent.Request : intent.Request with { Quantity = quantity };
            return _nativePermission != null && reconciledPosition is { } position
                ? _nativePermission(request, position) : _permission?.Invoke(request);
        }
    }

    public string? Validate(PreparedOrderIntent intent, Order? predecessor = null)
    {
        if (intent.Error != null) return intent.Error;
        if (!ReferenceEquals(intent.Context, this) || intent.Data.ContextId != Descriptor.Id || intent.Data.Generation != Descriptor.Generation)
            return "The prepared order belongs to another account context or routing generation.";
        if (Find(intent.Id)?.Rejection is { } rejected) return "This operation was definitively rejected before reuse: " + rejected;
        if (Descriptor.Provider != "simulator" && (_journal == null || string.IsNullOrEmpty(Descriptor.Id)))
            return "Native order ownership is not configured.";
        if (predecessor == null)
            return intent.IsReplacement ? "A replacement ticket cannot submit a new independent order." : null;
        return !intent.IsReplacement || predecessor.ContextId != Descriptor.Id || predecessor.OwnerId != intent.Owner.Id ||
            predecessor.LogicalOrderId != intent.LogicalOrderId || predecessor.IntentId != intent.PredecessorId
            ? "The replacement ticket does not match the exact predecessor." : null;
    }

    public string? BindOrder(Order order, PreparedOrderIntent intent)
    {
        string? error = intent.IsReplacement ? Validate(intent, order) : Validate(intent);
        if (error != null) return error;
        order.ContextId = Descriptor.Id;
        order.OwnerId = intent.Owner.Id;
        order.IntentId = intent.Id;
        order.LogicalOrderId = intent.LogicalOrderId;
        order.ClientOrderId = intent.ClientOrderId;
        intent.BoundOrder = order;
        return null;
    }
    /// <summary>Restore an accepted predecessor after its immediate prepared successor sent no child.
    /// This changes only the public binding; it grants no dispatch authority and rewrites no journal identity.</summary>
    public string? RestoreAcceptedBinding(Order order, Order predecessor)
    {
        lock (_gate)
        {
            try
            {
                var successor = Find(order.IntentId);
                var accepted = Find(predecessor.IntentId);
                if (successor == null || accepted == null || !ExactBinding(order, successor) ||
                    !ExactBinding(predecessor, accepted) || successor.PredecessorId != accepted.Id ||
                    successor.Owner != accepted.Owner || successor.LogicalOrderId != accepted.LogicalOrderId ||
                    (successor.DispatchUncertain && successor.Rejection == null) || successor.NativeOrders is { Length: > 0 } ||
                    (!accepted.DispatchUncertain && !accepted.Adopted) || accepted.Rejection != null ||
                    predecessor.NativeIdentity is not { } native || accepted.NativeOrders?.Contains(native) != true ||
                    order.NativeIdentity != native || order.ExchangeOrderId != predecessor.ExchangeOrderId ||
                    predecessor.ClientOrderId != accepted.ClientOrderId || order.ClientOrderId != successor.ClientOrderId ||
                    accepted.Request.Instrument != predecessor.Security.SecurityId ||
                    successor.Request.Instrument != accepted.Request.Instrument ||
                    order.Security.SecurityId != accepted.Request.Instrument ||
                    accepted.Request.Direction != predecessor.Direction || order.Direction != predecessor.Direction ||
                    successor.Request.Direction != predecessor.Direction ||
                    accepted.Request.ReduceOnly != predecessor.ReduceOnly || order.ReduceOnly != predecessor.ReduceOnly ||
                    successor.Request.ReduceOnly != predecessor.ReduceOnly || order.Type != predecessor.Type ||
                    successor.Request.Type != predecessor.Type ||
                    (accepted.WorkingOrder is { } working && working.Child == native
                        ? working.Type : accepted.Request.Type) != predecessor.Type)
                    return "Restoring an accepted binding requires its exact unchanged native predecessor and an immediate successor proven not to own a native child.";
                var ids = _journal?.FindNative(native) ??
                    (_ephemeralNative.TryGetValue(NativeKey(native), out var known) ? known : null);
                var claim = ids is { Count: > 0 } ? Find(ids[^1]) : null;
                if (claim == null || claim.ContextId != Descriptor.Id || claim.Generation != Descriptor.Generation ||
                    claim.Owner != accepted.Owner || claim.LogicalOrderId != accepted.LogicalOrderId ||
                    claim.NativeOrders?.Contains(native) != true)
                    return "The accepted predecessor no longer has the exact current native ownership claim.";
                RestoreBinding(order, accepted);
                order.ClientOrderId = accepted.ClientOrderId;
                return null;
            }
            catch (Exception ex)
            {
                _storageFault = ex is IOException or UnauthorizedAccessException;
                return LastError = "Could not restore the accepted predecessor binding: " + ex.Message;
            }
        }
    }

    public string? StartDispatch(PreparedOrderIntent intent, string nativeInstrument, string nativeExchange = "",
        decimal? actualQuantity = null, decimal? actualPrice = null, decimal? reconciledPosition = null) =>
        StartDispatchCore(intent, nativeInstrument, nativeExchange, actualQuantity, actualPrice, reconciledPosition, null, null);

    internal string? StartDispatch(PreparedOrderIntent intent, string nativeInstrument, string nativeExchange,
        decimal actualQuantity, decimal actualPrice, decimal reconciledPosition,
        Func<OwnedOrderIntent, string?> currentCloseAdmission)
    {
        ArgumentNullException.ThrowIfNull(currentCloseAdmission);
        return StartDispatchCore(intent, nativeInstrument, nativeExchange, actualQuantity, actualPrice, reconciledPosition, currentCloseAdmission, null);
    }

    internal string? StartNativeAmendmentDispatch(PreparedOrderIntent intent, string nativeInstrument, string nativeExchange,
        in NativeAmendmentDispatch dispatch, decimal actualPrice, decimal reconciledPosition) =>
        StartDispatchCore(intent, nativeInstrument, nativeExchange, dispatch.RemainingQuantity, actualPrice,
            reconciledPosition, null, dispatch.PredecessorFilled);

    private string? StartDispatchCore(PreparedOrderIntent intent, string nativeInstrument, string nativeExchange,
        decimal? actualQuantity, decimal? actualPrice, decimal? reconciledPosition,
        Func<OwnedOrderIntent, string?>? currentCloseAdmission, decimal? capturedPredecessorFilled)
    {
        lock (_gate)
        {
            if (_nativePermission != null && reconciledPosition == null)
                return "Native order admission requires the current reconciled account position.";
            if (CheckDispatch(intent, actualQuantity, reconciledPosition) is { } denied) return denied;
            if (string.IsNullOrEmpty(nativeInstrument)) return "The native routed instrument is not resolved.";
            if (intent.Data.Emergency && (intent.Data.NativeInstrument != nativeInstrument || intent.Data.NativeExchange != nativeExchange))
                return "The emergency reduction does not match its durable native routed instrument.";
            if (!intent.TryAttempt()) return "This identity was already attempted; reconcile its outcome instead of resending.";
            _dispatchCount++;
            try
            {
                var saved = intent.Data.Emergency ? null : Find(intent.Id);
                if (saved?.DispatchUncertain == true)
                    return "The durable identity already has an uncertain dispatch; it cannot be resent.";
                if (!intent.Data.Emergency)
                {
                    EnsureEmergencyPlan(intent, nativeInstrument, nativeExchange, actualQuantity ?? intent.Request.Quantity);
                    var dispatch = intent.Data with { DispatchUncertain = true, NativeInstrument = nativeInstrument, NativeExchange = nativeExchange,
                        DispatchQuantity = actualQuantity ?? intent.Request.Quantity, DispatchPrice = actualPrice ?? intent.Request.Price,
                        PositionClose = saved?.PositionClose ?? intent.Data.PositionClose,
                        PredecessorFilled = intent.IsReplacement ? capturedPredecessorFilled ?? intent.BoundOrder?.Filled : null };
                    if (dispatch.PositionClose != null && _journal != null)
                        Remember(_journal.StartPositionCloseDispatch(dispatch, currentCloseAdmission));
                    else Save(dispatch);
                }
                TrackEmergencyExposure(intent, nativeInstrument, nativeExchange, actualQuantity ?? intent.Request.Quantity);
                if (_releasedCloseDenials.Remove(intent.Id, out var releasedDenial) && _lastError == releasedDenial) _lastError = null;
                return null;
            }
            catch (PositionCloseAdmissionDeniedException ex)
            {
                // The current-close check refused before the dispatch record was written, so nothing was sent.
                intent.ReleaseAttempt();
                return _releasedCloseDenials[intent.Id] = LastError = "Could not commit dispatch ownership: " + ex.Message;
            }
            catch (PositionCloseAdmissionUnresolvedException ex)
            {
                if (currentCloseAdmission != null) throw;
                return LastError = "Could not commit dispatch ownership: " + ex.Message;
            }
            catch (Exception ex)
            {
                bool storageFault = ex is IOException or UnauthorizedAccessException;
                if (storageFault) _storageFault = true;
                LastError = "Could not commit dispatch ownership: " + ex.Message;
                if (storageFault && currentCloseAdmission != null) throw;
                return LastError;
            }
        }
    }

    /// <summary>Commit the final canceled predecessor quantity before its replacement is sent, or an evidenced legacy baseline.
    /// This is raw logical fill, not the adoption-adjusted quantity projected to a strategy.</summary>
    public string? RecordPredecessorFilled(PreparedOrderIntent intent, decimal predecessorFilled)
    {
        lock (_gate)
        {
            try
            {
                var data = Find(intent.Id);
                if (!ReferenceEquals(intent.Context, this) || !intent.Attempted || !intent.IsReplacement ||
                    data == null || data.ContextId != Descriptor.Id || data.Generation != Descriptor.Generation ||
                    !data.DispatchUncertain || data.Emergency || data.Rejection != null ||
                    intent.BoundOrder == null || !ExactBinding(intent.BoundOrder, data) ||
                    predecessorFilled < data.QuantityBaseline || predecessorFilled < (data.PredecessorFilled ?? 0) ||
                    (data.PredecessorFilled is { } known && known != predecessorFilled &&
                        data.NativeOrders is { Length: > 0 }))
                    return "A predecessor fill baseline requires the exact attempted replacement and unchanged native lineage.";
                if (data.PredecessorFilled != predecessorFilled) Save(data with { PredecessorFilled = predecessorFilled });
                return null;
            }
            catch (Exception ex)
            {
                _storageFault = ex is IOException or UnauthorizedAccessException;
                return LastError = "Could not persist replacement predecessor quantity: " + ex.Message;
            }
        }
    }

    private void EnsureEmergencyPlan(PreparedOrderIntent opening, string instrument, string exchange, decimal actualQuantity)
    {
        var request = opening.Request;
        decimal maximum = RequiredEmergencyCapacity(opening, instrument, exchange, actualQuantity);
        var direction = request.ReduceOnly || request.Protective ? request.Direction
            : request.Direction == OrderDirections.Buy ? OrderDirections.Sell : OrderDirections.Buy;
        if (!_emergency.TryGetValue(opening.Owner, out var exits)) _emergency[opening.Owner] = exits = new();
        foreach (var type in EmergencyTypes)
        {
            int replace = -1;
            bool ready = false;
            for (int i = 0; i < exits.Count; i++)
            {
                var exit = exits[i];
                if (exit.Request.Type != type || exit.Request.Instrument != request.Instrument ||
                    exit.Request.Direction != direction || exit.Data.NativeInstrument != instrument ||
                    exit.Data.NativeExchange != exchange) continue;
                replace = i;
                ready = !exit.Attempted && exit.Data.MaximumReduction >= maximum;
                break;
            }
            if (ready) continue;
            string id = Guid.NewGuid().ToString("N");
            // This is a bounded future reduction goal, not a claim that its future exact quantity or stop/limit price
            // is already known. Its native route and uncertain one-shot delivery authority precede opening risk.
            var data = new OwnedOrderIntent(id, id, "th-" + Guid.NewGuid().ToString("N"), opening.Owner,
                new(request.Instrument, direction, type, maximum, ReduceOnly: true, Comment: "Reserved emergency reduction",
                    Protective: true, MaximumOwnedQuantity: maximum), Descriptor.Id, Descriptor.Generation,
                EphemeralPreparation: _journal == null, DispatchUncertain: true, NativeInstrument: instrument, NativeExchange: exchange,
                Emergency: true, MaximumReduction: maximum);
            Save(data);
            if (replace >= 0) exits[replace] = new(this, data);
            else exits.Add(new(this, data));
        }
    }

    private sealed class EmergencyExposure
    {
        public decimal BaselineQuantity, SettledQuantity;
        public readonly Dictionary<string, PendingExposure> Pending = new(StringComparer.Ordinal);
    }

    private sealed class PendingExposure(string logicalOrderId, OrderDirections direction, bool opening,
        decimal maximumQuantity, Order? order)
    {
        public readonly string LogicalOrderId = logicalOrderId;
        public readonly OrderDirections Direction = direction;
        public readonly bool Opening = opening;
        public readonly decimal FilledBaseline = order?.Filled ?? 0;
        public decimal MaximumQuantity = maximumQuantity;
        public Order? Order = order;
        public decimal Filled => Math.Max(0, (Order?.Filled ?? 0) - FilledBaseline);
        public decimal Remaining => Order is { State: not OrderStates.Active } ? 0
            : Math.Max(0, MaximumQuantity - (Order?.Filled ?? 0));
    }

    private EmergencyExposure ExposureFor(PreparedOrderIntent intent, string instrument, string exchange)
    {
        var key = (intent.Owner, intent.Request.Instrument, instrument, exchange);
        if (!_emergencyExposure.TryGetValue(key, out var exposure))
            _emergencyExposure[key] = exposure = new();
        return exposure;
    }

    private decimal RequiredEmergencyCapacity(PreparedOrderIntent intent, string instrument, string exchange, decimal quantity)
    {
        var request = intent.Request;
        bool opening = !request.ReduceOnly && !request.Protective;
        decimal sign = request.Direction == OrderDirections.Buy ? 1 : -1;
        if (!opening) sign = -sign;
        var exposure = ExposureFor(intent, instrument, exchange);
        decimal filled = 0, outstanding = 0, replacing = 0;
        // Only active/unknown logical orders are retained. Terminal fills fold into one signed quantity;
        // neither completed order history nor the bounded ownership cache is scanned on admission.
        foreach (var pair in exposure.Pending)
        {
            var pending = pair.Value;
            decimal signedFill = (pending.Direction == OrderDirections.Buy ? 1 : -1) * pending.Filled;
            if (pending.Order is { State: not OrderStates.Active })
            {
                var logical = Find(pending.LogicalOrderId);
                if (logical == null) throw new InvalidOperationException("Terminal exposure lost its durable logical identity.");
                decimal cumulative = pending.Order.Filled - logical.QuantityBaseline;
                Save(logical with { EmergencyQuantityFolded = true, EmergencyQuantityAccounted = cumulative,
                    NativeInstrument = instrument, NativeExchange = exchange });
                exposure.SettledQuantity += signedFill;
                exposure.Pending.Remove(pair.Key);
                continue;
            }
            filled += signedFill;
            if (!pending.Opening || (pending.Direction == OrderDirections.Buy ? 1 : -1) != sign) continue;
            if (pending.LogicalOrderId == intent.LogicalOrderId) replacing = pending.Remaining;
            else outstanding += pending.Remaining;
        }
        decimal known = exposure.BaselineQuantity + exposure.SettledQuantity + filled;
        if (request.MaximumOwnedQuantity is { } maximumOwned)
        {
            decimal observedBefore = Math.Max(0, maximumOwned - (opening ? quantity : 0));
            // A native fill may precede the position snapshot. Never discard that known exposure merely
            // because the caller still observes flat; also retain inherited/external owned-position bounds.
            if (observedBefore > 0 && observedBefore > known * sign)
            {
                exposure.BaselineQuantity += sign * (observedBefore - known * sign);
                known = sign * observedBefore;
            }
        }
        decimal potential = Math.Max(0, known * sign) + outstanding + (opening ? Math.Max(quantity, replacing) : 0);
        return Math.Max(potential, Math.Max(quantity, request.MaximumOwnedQuantity ?? quantity));
    }

    private void TrackEmergencyExposure(PreparedOrderIntent intent, string instrument, string exchange, decimal quantity)
    {
        var exposure = ExposureFor(intent, instrument, exchange);
        decimal maximum = (intent.BoundOrder?.Filled ?? 0) + quantity;
        if (exposure.Pending.TryGetValue(intent.LogicalOrderId, out var pending))
        {
            // An amendment does not create independent opening risk; retain the larger predecessor
            // bound until its handle becomes terminal, since a command receipt is not native acceptance.
            pending.MaximumQuantity = Math.Max(pending.MaximumQuantity, maximum);
            pending.Order = intent.BoundOrder;
            return;
        }
        exposure.Pending.Add(intent.LogicalOrderId, new(intent.LogicalOrderId, intent.Request.Direction,
            !intent.Request.ReduceOnly && !intent.Request.Protective, maximum, intent.BoundOrder));
    }

    public string? BindNative(PreparedOrderIntent intent, NativeOrderIdentity native, bool notify = true)
    {
        lock (_gate)
        {
            try
            {
                var data = Find(intent.Id) ?? intent.Data;
                if (!ReferenceEquals(intent.Context, this) || data.ContextId != Descriptor.Id ||
                    data.Generation != Descriptor.Generation || string.IsNullOrEmpty(native.Kind) || string.IsNullOrEmpty(native.Id))
                    throw new InvalidOperationException("A native binding needs the exact account context and native identity.");
                if (!(data.NativeOrders ?? Array.Empty<NativeOrderIdentity>()).Contains(native))
                {
                    data = data with { NativeOrders = (data.NativeOrders ?? Array.Empty<NativeOrderIdentity>()).Append(native).ToArray() };
                    data = Save(data);
                }
                if (_journal != null) _journal.Bind(native, data);
                else
                {
                    string key = NativeKey(native);
                    if (!_ephemeralNative.TryGetValue(key, out var ids)) _ephemeralNative[key] = ids = new();
                    if (!ids.Contains(data.Id)) ids.Add(data.Id);
                }
                if (_bindingFailures.Remove(intent.Id, out var previousError) && _lastError == previousError) _lastError = null;
            }
            catch (Exception ex)
            {
                string error = LastError = "Native order accepted or unknown; could not persist binding: " + ex.Message;
                _bindingFailures[intent.Id] = error;
                return error;
            }
        }
        if (notify) NotifyOwnershipChanged();
        return null;
    }

    public void NotifyOwnershipChanged()
    {
        // A projection may query a broker. Never run its callback while holding an ownership/provider gate.
        try { OwnershipChanged?.Invoke(); }
        catch (Exception ex) { ReportProjectionFault("Native binding is durable; its projection notification failed: " + ex.Message); }
    }
    /// <summary>Bind a broker-evidenced native identity without granting another dispatch attempt.</summary>
    public string? BindNative(Order order, NativeOrderIdentity native, bool notify = true)
    {
        PreparedOrderIntent ticket;
        lock (_gate)
        {
            var data = FindBoundOrder(order);
            if (data == null || !ExactBinding(order, data))
                return "A native binding requires the exact durable order owner and lineage.";
            ticket = new(this, data) { BoundOrder = order };
            ticket.TryAttempt();
        }
        return BindNative(ticket, native, notify);
    }

    /// <summary>Startup-only discovery of attempted submissions and acknowledged unresolved conditional parents.
    /// Consumers retain the returned exact identities; never rescan the journal on prices or ordinary polls.</summary>
    public IReadOnlyList<OwnedOrderIntent> RecoverableNativeSubmissions(string instrument, OrderTypes type)
    {
        lock (_gate) return _journal?.RecoverableNativeSubmissions(instrument, type) ?? Array.Empty<OwnedOrderIntent>();
    }

    internal static bool CanRecoverNativeIdentity(OwnedOrderIntent? intent, NativeOrderIdentity native) =>
        intent?.NativeOrders?.Contains(native) == true ||
        (intent != null && !intent.Adopted && intent.DispatchUncertain &&
            intent.Rejection == null && intent.NativeOrders is not { Length: > 0 });

    internal OwnedOrderIntent? FindNativeSubmission(string clientOrderId, NativeOrderIdentity native, bool allowClientRecovery = true)
    {
        lock (_gate)
        {
            var ids = _journal?.FindNative(native) ??
                (_ephemeralNative.TryGetValue(NativeKey(native), out var known) ? known : null);
            var claim = ids is { Count: > 0 } ? Find(ids[^1]) : null;
            if (claim == null && !allowClientRecovery) return null;
            var client = string.IsNullOrEmpty(clientOrderId) ? null : _journal?.FindClient(clientOrderId);
            if (claim == null && !CanRecoverNativeIdentity(client, native)) return null;
            if (Descriptor.Provider == "Rithmic" && claim?.AcceptedNativeAmendment?.Native == native) return claim;
            return claim != null && client != null && client.Owner == claim.Owner &&
                client.LogicalOrderId == claim.LogicalOrderId && client.NativeOrders?.Contains(native) == true
                ? client : claim ?? client;
        }
    }

    /// <summary>Recover an accepted identity from its exact durable native claim or uncertain client dispatch; never resend it.
    /// Claim-only recovery lets a provider validate an existing adoption before considering original wire-type rules.</summary>
    public PreparedOrderIntent? RecoverNativeOrder(Order order, string clientOrderId, NativeOrderIdentity native,
        string instrument, string nativeInstrument, string nativeExchange = "", bool notify = true, bool allowClientRecovery = true,
        NativeOrderAmendment? nativeAmendmentEvidence = null)
    {
        PreparedOrderIntent ticket;
        lock (_gate)
        {
            var data = FindNativeSubmission(clientOrderId, native, allowClientRecovery);
            if (data == null) return null;
            if (Descriptor.Provider == "Rithmic" && data.Request.Instrument != instrument &&
                data.NativeInstrument == nativeInstrument && data.NativeExchange == nativeExchange) return null;
            if (Descriptor.Provider == "Rithmic" && _journal != null)
            {
                var evidence = nativeAmendmentEvidence ?? new NativeOrderAmendment(native, clientOrderId,
                    order.QuantityToFill, order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price);
                if (evidence.Native == native && evidence.ClientOrderId == clientOrderId)
                {
                    data = _journal.ResolveNativeAmendment(data, evidence, allowNewAcceptance: nativeAmendmentEvidence != null);
                    Remember(data);
                }
            }
            bool workingChild = data.WorkingOrder is { } working && working.Child == native &&
                working.NativeInstrument == nativeInstrument && working.NativeExchange == nativeExchange;
            bool nativeReductionModeKnown = Descriptor.Provider != "Rithmic" && (Descriptor.Provider != "Binance" || Descriptor.Product != "spot");
            string expectedClient = Descriptor.Provider == "Rithmic" && data.AcceptedNativeAmendment is { } amendment && amendment.Native == native
                ? amendment.ClientOrderId : data.ClientOrderId;
            if (Descriptor.Provider == "Rithmic" && data.DispatchQuantity is { } dispatched &&
                order.QuantityToFill != dispatched + (data.PredecessorFilled ?? 0))
                throw new InvalidOperationException("Rithmic replay quantity disagrees with the original dispatched quantity.");
            if (data.ContextId != Descriptor.Id || data.Generation != Descriptor.Generation || data.EphemeralPreparation ||
                (!data.Adopted && !data.DispatchUncertain) || data.Rejection != null || data.Emergency ||
                data.Request.Instrument != instrument ||
                (data.NativeInstrument != nativeInstrument && !(data.Adopted && data.NativeInstrument == null)) ||
                (data.NativeExchange != nativeExchange && !(data.Adopted && data.NativeExchange == null)) ||
                data.Request.Direction != order.Direction ||
                (workingChild ? data.WorkingOrder!.Type : data.Request.Type) != order.Type ||
                (nativeReductionModeKnown && data.Request.ReduceOnly != order.ReduceOnly) ||
                (!workingChild && expectedClient != clientOrderId) ||
                (data.NativeOrders is { Length: > 0 } && !data.NativeOrders.Contains(native)) ||
                (order.NativeIdentity != null && order.NativeIdentity != native) ||
                (order.ContextId != null && order.ContextId != Descriptor.Id) ||
                (!string.IsNullOrEmpty(order.ClientOrderId) && order.ClientOrderId != clientOrderId && order.ClientOrderId != data.ClientOrderId) ||
                (!string.IsNullOrEmpty(order.OwnerId) && (order.OwnerId != data.Owner.Id ||
                    order.LogicalOrderId != data.LogicalOrderId || order.IntentId != data.Id)))
                throw new InvalidOperationException("Native client recovery disagrees with its frozen durable dispatch.");
            ticket = new(this, data) { BoundOrder = order };
            ticket.TryAttempt();
            if (BindNative(ticket, native, notify: false) is { } error)
                throw new IOException(error);
            RestoreBinding(order, data);
            order.ReduceOnly = data.Request.ReduceOnly;
            order.NativeIdentity = native;
        }
        if (notify) NotifyOwnershipChanged();
        return ticket;
    }

    internal string? RecordAcceptedNativeAmendment(PreparedOrderIntent intent, NativeOrderIdentity native,
        string clientOrderId, decimal totalQuantity, decimal price)
    {
        lock (_gate)
        {
            try
            {
                if (!ReferenceEquals(intent.Context, this) || Descriptor.Provider != "Rithmic" || _journal == null)
                    throw new InvalidOperationException("An accepted native amendment requires its exact durable routing context.");
                var accepted = _journal.AcceptNativeAmendment(intent.Id, new(native, clientOrderId, totalQuantity, price));
                Remember(accepted);
                _bindingFailures.Remove(intent.Id);
                return null;
            }
            catch (Exception ex)
            {
                string error = LastError = "Could not persist accepted native amendment: " + ex.Message;
                _bindingFailures[intent.Id] = error;
                return error;
            }
        }
    }

    /// <summary>Persist the broker's conditional-parent/working-child evidence separately from its immutable request.</summary>
    public string? RecordNativeWorkingType(Order order, NativeOrderIdentity parent, NativeOrderIdentity child,
        OrderTypes workingType, string instrument, string nativeInstrument, string nativeExchange = "", bool notify = true)
    {
        lock (_gate)
        {
            try
            {
                var data = FindBoundOrder(order);
                if (data == null || !ExactBinding(order, data))
                    return "A working-type transition requires the exact durable owner and lineage.";
                if (data.Request.Type != OrderTypes.Stop || workingType is not (OrderTypes.Limit or OrderTypes.Market) ||
                    data.Request.Instrument != instrument || data.Request.Direction != order.Direction ||
                    data.Request.ReduceOnly != order.ReduceOnly || string.IsNullOrEmpty(nativeInstrument) ||
                    string.IsNullOrEmpty(parent.Kind) || string.IsNullOrEmpty(parent.Id) ||
                    string.IsNullOrEmpty(child.Kind) || string.IsNullOrEmpty(child.Id) ||
                    !(data.NativeOrders ?? Array.Empty<NativeOrderIdentity>()).Contains(parent) ||
                    (data.NativeInstrument != null && data.NativeInstrument != nativeInstrument) ||
                    (data.NativeExchange != null && data.NativeExchange != nativeExchange) ||
                    (order.NativeIdentity != parent && order.NativeIdentity != child) ||
                    (data.WorkingOrder is { } prior && data.NativeOrders?.Contains(prior.Parent) == true &&
                        (prior.Parent != parent || prior.Child != child || prior.Type != workingType ||
                            prior.NativeInstrument != nativeInstrument || prior.NativeExchange != nativeExchange)))
                    return "The native working-type evidence does not match this conditional parent and route.";
                var evidence = new NativeWorkingOrder(parent, child, workingType, nativeInstrument, nativeExchange);
                Save(data with { WorkingOrder = evidence, NativeInstrument = nativeInstrument, NativeExchange = nativeExchange });
                var ticket = new PreparedOrderIntent(this, data);
                ticket.TryAttempt();
                if (BindNative(ticket, child, notify: false) is { } error) return error;
            }
            catch (Exception ex)
            {
                string error = LastError = "Could not persist broker working-type evidence: " + ex.Message;
                if (!string.IsNullOrEmpty(order.IntentId)) _bindingFailures[order.IntentId] = error;
                return error;
            }
        }
        if (notify) NotifyOwnershipChanged();
        return null;
    }

    /// <summary>Record definitive terminal evidence for the current exact native handle, never a negative lookup
    /// or a canceled predecessor still being replaced. A triggered parent must first resolve its working child.</summary>
    public string? RecordNativeTerminal(Order order)
    {
        lock (_gate)
        {
            try
            {
                var data = FindBoundOrder(order);
                if (data == null || !ExactBinding(order, data) || order.NativeIdentity is not { } native ||
                    data.NativeOrders?.Contains(native) != true || data.Request.Instrument != order.Security.SecurityId ||
                    data.Request.Direction != order.Direction || data.Request.ReduceOnly != order.ReduceOnly ||
                    (data.WorkingOrder is { } working && (working.Parent == native || working.Child == native)
                        ? working.Child != native || working.Type != order.Type : data.Request.Type != order.Type) ||
                    order.State is not (OrderStates.Done or OrderStates.Failed) ||
                    order.Status is not (OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Rejected))
                    return "Terminal evidence requires the exact bound terminal native order and lineage.";
                var logical = Find(data.LogicalOrderId) ?? data;
                decimal cumulative = order.Filled - logical.QuantityBaseline;
                if (cumulative < 0) return "Terminal native quantity precedes its immutable ownership baseline.";
                var terminal = new NativeOrderTerminal(native, cumulative);
                if (data.TerminalOrder != terminal) Save(data with { TerminalOrder = terminal });
                return null;
            }
            catch (Exception ex)
            {
                _storageFault = ex is IOException or UnauthorizedAccessException;
                return LastError = "Could not persist terminal native evidence: " + ex.Message;
            }
        }
    }

    private bool ExactBinding(Order order, OwnedOrderIntent data) =>
        order.ContextId == Descriptor.Id && data.ContextId == Descriptor.Id && data.Generation == Descriptor.Generation &&
        order.OwnerId == data.Owner.Id && order.IntentId == data.Id && order.LogicalOrderId == data.LogicalOrderId;

    /// <summary>Returns the exact current owned executing protection, retaining historical conditional evidence
    /// separately from an explicitly dispatched MARKET successor. This grants no dispatch permission.</summary>
    public OwnedOrderIntent? FindExecutingProtection(Order order, OrderOwner expectedOwner)
    {
        lock (_gate)
        {
            if (order.Type != OrderTypes.Market || order.State != OrderStates.Active || order.Canceled ||
                !order.ReduceOnly || order.Unfilled <= 0 || order.NativeIdentity is not { } native) return null;
            var data = FindBoundOrder(order);
            if (data == null || !ExactBinding(order, data) || data.Owner != expectedOwner ||
                data.Request.Instrument != order.Security.SecurityId || data.Request.Direction != order.Direction ||
                !data.Request.ReduceOnly || data.WorkingOrder is not { Type: OrderTypes.Market } working ||
                working.NativeInstrument != data.NativeInstrument || working.NativeExchange != data.NativeExchange ||
                data.NativeOrders?.Contains(native) != true || data.TerminalOrder?.Native == native) return null;
            var ids = _journal?.FindNative(native) ??
                (_ephemeralNative.TryGetValue(NativeKey(native), out var known) ? known : null);
            var current = ids is { Count: > 0 } ? Find(ids[^1]) : null;
            if (current == null || current.ContextId != data.ContextId || current.Generation != data.Generation ||
                current.Owner != data.Owner || current.LogicalOrderId != data.LogicalOrderId ||
                !HasNativeOperationLineage(data, current, native)) return null;
            if (data.Request.Type == OrderTypes.Stop)
                return working.Child == native && data.NativeOrders.Contains(working.Parent) ? data : null;
            if (data.Request.Type != OrderTypes.Market || data.PredecessorId == null) return null;

            var predecessor = Find(data.PredecessorId);
            return predecessor != null && predecessor.ContextId == data.ContextId && predecessor.Generation == data.Generation &&
                predecessor.Owner == data.Owner && predecessor.LogicalOrderId == data.LogicalOrderId &&
                predecessor.Request.Type is OrderTypes.Stop or OrderTypes.Market && predecessor.Request.ReduceOnly &&
                predecessor.Request.Instrument == data.Request.Instrument && predecessor.Request.Direction == data.Request.Direction &&
                predecessor.NativeInstrument == data.NativeInstrument && predecessor.NativeExchange == data.NativeExchange &&
                predecessor.WorkingOrder == working && predecessor.TerminalOrder is { } terminal &&
                predecessor.NativeOrders?.Contains(terminal.Native) == true ? data : null;
        }
    }

    private bool HasNativeOperationLineage(OwnedOrderIntent operation, OwnedOrderIntent indexed, NativeOrderIdentity native)
    {
        // The bounded native index retains the first operation of an owner, not every in-place amendment.
        // Prove the current operation through immutable prepared predecessor links, never owner/name similarity.
        while (operation.Id != indexed.Id)
        {
            if ((!operation.DispatchUncertain && !operation.Adopted) || operation.NativeOrders?.Contains(native) != true ||
                operation.PredecessorId == null || Find(operation.PredecessorId) is not { } predecessor ||
                predecessor.Id == operation.Id || predecessor.ContextId != operation.ContextId ||
                predecessor.Generation != operation.Generation || predecessor.Owner != operation.Owner ||
                predecessor.LogicalOrderId != operation.LogicalOrderId ||
                predecessor.Request.Instrument != operation.Request.Instrument ||
                predecessor.Request.Direction != operation.Request.Direction ||
                predecessor.Request.ReduceOnly != operation.Request.ReduceOnly ||
                predecessor.NativeOrders?.Contains(native) != true ||
                predecessor.TerminalOrder?.Native == native ||
                (predecessor.NativeInstrument != null && predecessor.NativeInstrument != operation.NativeInstrument) ||
                (predecessor.NativeExchange != null && predecessor.NativeExchange != operation.NativeExchange) ||
                (!predecessor.Adopted && (predecessor.NativeInstrument == null || predecessor.NativeExchange == null)))
                return false;
            // A completed/rejected amendment is not terminal evidence for its still-working native order.
            operation = predecessor;
        }
        return operation.NativeOrders?.Contains(native) == true && operation.TerminalOrder?.Native != native;
    }


    public void RecordRejected(PreparedOrderIntent intent, string reason)
    {
        lock (_gate)
        {
            try { Save((Find(intent.Id) ?? intent.Data) with { Rejection = reason }); }
            catch (Exception ex)
            {
                _storageFault = ex is IOException or UnauthorizedAccessException;
                LastError = "Could not persist definite order rejection: " + ex.Message;
            }
        }
    }

    public OwnedOrderIntent Adopt(Order order, OrderOwner owner, decimal baseline, ExecutionIdentity[] prior, bool baselineKnown,
        bool protective = false, ExecutionIdentity[]? ownedPrior = null, ExecutionIdentity[]? importedPrior = null)
    {
        lock (_gate)
        {
            if (order.ContextId != null && order.ContextId != Descriptor.Id)
                throw new InvalidOperationException("Cannot adopt an order from another account context.");
            var previous = FindBoundOrder(order);
            if (previous != null && previous.Owner == owner)
            {
                RestoreBinding(order, previous);
                return previous;
            }
            if (previous?.Owner.Kind == OrderOwnerKind.Strategy)
                throw new InvalidOperationException("An order already owned by another strategy ledger cannot be adopted.");
            if (ownedPrior is { Length: > 0 } && (!baselineKnown || ownedPrior.Any(identity => !prior.Contains(identity))))
                throw new InvalidOperationException("Known owned financial corrections must be part of the exact confirmed native adoption prefix.");
            if (importedPrior is { Length: > 0 } && importedPrior.Any(identity => ownedPrior?.Contains(identity) != true))
                throw new InvalidOperationException("Imported financial corrections require exact already-owned financial prefix evidence.");
            string id = Guid.NewGuid().ToString("N");
            var data = new OwnedOrderIntent(id, id, order.ClientOrderId ?? "", owner,
                new(order.Security.SecurityId, order.Direction, order.Type, order.Unfilled,
                    order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price, order.ReduceOnly, order.Comment,
                    Protective: protective || order.ReduceOnly),
                Descriptor.Id, Descriptor.Generation, QuantityBaseline: baseline, PriorExecutions: prior,
                BaselineKnown: baselineKnown, Adopted: true, EphemeralPreparation: _journal == null,
                NativeOrders: order.NativeIdentity == null ? null : new[] { order.NativeIdentity },
                WorkingOrder: previous?.WorkingOrder is { } working && working.Child == order.NativeIdentity ? working : null,
                OwnedPriorExecutions: ownedPrior, ImportedPriorExecutions: importedPrior);
            Save(data);
            if (order.NativeIdentity != null)
            {
                if (_journal != null) _journal.Bind(order.NativeIdentity, data);
                else
                {
                    string key = NativeKey(order.NativeIdentity);
                    if (!_ephemeralNative.TryGetValue(key, out var ids)) _ephemeralNative[key] = ids = new();
                    ids.Add(data.Id);
                }
            }
            order.ContextId = Descriptor.Id; order.OwnerId = owner.Id; order.IntentId = id; order.LogicalOrderId = id;
            return data;
        }
    }

    private OwnedOrderIntent? FindBoundOrder(Order order)
    {
        lock (_gate)
        {
            if (order.ContextId != Descriptor.Id) return null;
            var found = Find(order.IntentId);
            if (found != null || order.NativeIdentity == null) return found;
            var ids = _journal?.FindNative(order.NativeIdentity) ??
                (_ephemeralNative.TryGetValue(NativeKey(order.NativeIdentity), out var known) ? known : null);
            return ids is { Count: > 0 } ? Find(ids[^1]) : null;
        }
    }

    private static void RestoreBinding(Order order, OwnedOrderIntent intent)
    {
        order.ContextId = intent.ContextId; order.OwnerId = intent.Owner.Id;
        order.IntentId = intent.Id; order.LogicalOrderId = intent.LogicalOrderId;
        if (string.IsNullOrEmpty(order.ClientOrderId)) order.ClientOrderId = intent.ClientOrderId;
    }


    /// <summary>A cohesive native cumulative-quantity observation, never an execution-price or fee claim.
    /// Terminal handle changes resolve through durable lineage; no runner/order-history scan is needed.</summary>
    public void ObserveQuantity(Order order, string symbol, decimal pointValue, decimal? physicalCumulativeQuantity = null)
    {
        if (CaptureQuantityObservation(order, symbol, pointValue, physicalCumulativeQuantity) is { } observation) NotifyQuantityChanged(observation);
    }

    /// <summary>Capture under a producer's disposal fence; publish the returned observation outside that fence.</summary>
    public OwnedOrderQuantity? CaptureQuantityObservation(Order order, string symbol, decimal pointValue, decimal? physicalCumulativeQuantity = null)
    {
        // Emergency accounting must observe late terminal quantities even when no strategy projector is subscribed.
        try
        {
            lock (_gate)
            {
                var bound = FindBoundOrder(order);
                if (bound == null) return null;
                var logical = Find(bound.LogicalOrderId) ?? bound;
                decimal cumulative = order.Filled - logical.QuantityBaseline;
                if (physicalCumulativeQuantity is { } raw &&
                    (order.NativeIdentity == null || bound.NativeOrders?.Contains(order.NativeIdentity) != true ||
                        raw < 0 || raw != order.Filled))
                    throw new InvalidOperationException("Physical accounting requires the exact bound native order and its independent cumulative quantity.");
                if (logical.EmergencyQuantityFolded && cumulative > logical.EmergencyQuantityAccounted &&
                    logical.NativeInstrument is { } nativeInstrument && logical.NativeExchange is { } nativeExchange &&
                    _emergencyExposure.TryGetValue((logical.Owner, logical.Request.Instrument, nativeInstrument, nativeExchange), out var exposure) &&
                    !exposure.Pending.ContainsKey(logical.LogicalOrderId))
                {
                    decimal delta = cumulative - logical.EmergencyQuantityAccounted;
                    logical = logical with { EmergencyQuantityAccounted = cumulative };
                    Save(logical);
                    exposure.SettledQuantity += logical.Request.Direction == OrderDirections.Buy ? delta : -delta;
                }
                if (cumulative <= logical.QuantityProjected || cumulative <= 0) return null;
                if (!_unprojectedQuantities.TryGetValue(logical.Owner, out var pending))
                    _unprojectedQuantities[logical.Owner] = pending = new(StringComparer.Ordinal);
                if (pending.TryGetValue(logical.LogicalOrderId, out var captured) && cumulative <= captured.CumulativeQuantity)
                    return null;
                var observation = new OwnedOrderQuantity(logical, DateTime.UtcNow,
                    logical.Request.Direction == OrderDirections.Buy, cumulative, symbol, pointValue,
                    physicalCumulativeQuantity is { } nativeQuantity
                        ? new PhysicalOrderAccounting(order.NativeIdentity!, logical.QuantityBaseline, nativeQuantity) : null);
                pending[logical.LogicalOrderId] = observation;
                _pendingQuantities = null;
                return observation;
            }
        }
        catch (Exception ex)
        {
            ReportProjectionFault("Native quantity remains known at the account; owned projection is unreconciled: " + ex.Message);
            return null;
        }
    }

    public void NotifyQuantityChanged(OwnedOrderQuantity observation)
    {
        try { QuantityChanged?.Invoke(observation); }
        catch (Exception ex)
        {
            ReportProjectionFault("Native quantity remains known at the account; owned projection is unreconciled: " + ex.Message);
        }
    }

    public void AcknowledgeQuantity(OwnedOrderQuantity observation)
    {
        lock (_gate)
        {
            var current = Find(observation.Intent.LogicalOrderId);
            if (current == null || current.ContextId != Descriptor.Id || current.Generation != Descriptor.Generation ||
                observation.Intent.ContextId != current.ContextId || observation.Intent.Generation != current.Generation ||
                observation.Intent.Owner != current.Owner || observation.Intent.LogicalOrderId != current.LogicalOrderId)
                throw new InvalidOperationException("Quantity acknowledgement requires the exact captured logical owner and generation.");
            if (current.QuantityProjected < observation.CumulativeQuantity)
            {
                current = current with { QuantityProjected = observation.CumulativeQuantity };
                Save(current);
            }
            // The durable acknowledgement covers only captures at or below its committed cumulative quantity.
            // A newer capture can race an older publication without losing this owner-generation fence.
            if (_unprojectedQuantities.TryGetValue(current.Owner, out var pending) &&
                pending.TryGetValue(current.LogicalOrderId, out var captured) && captured.CumulativeQuantity <= current.QuantityProjected)
            {
                pending.Remove(current.LogicalOrderId);
                if (pending.Count == 0) _unprojectedQuantities.Remove(current.Owner);
                _pendingQuantities = null;
            }
        }
    }
    public OwnedOrderIntent? Resolve(ExecutionReport report)
    {
        lock (_gate)
        {
            if (report.ContextId != Descriptor.Id) return null;
            // An adopted native claim supersedes its former owner, including late corrections carrying the
            // former intent ID. Never fall back to an earlier owner when the current prefix is excluded.
            var claim = FindReportClaim(report);
            return claim == null ? null : Eligible(claim, report);
        }
    }
    /// <summary>Exact durable/native lineage matching for a one-time adoption replay; never a process-local numeric guess.</summary>
    public bool MatchesExecution(Order order, ExecutionReport report)
    {
        lock (_gate)
        {
            if (order.ContextId != Descriptor.Id || !MatchesReportContext(report)) return false;
            var bound = FindBoundOrder(order);
            if (bound != null && !ExactBinding(order, bound)) return false;
            var claim = FindReportClaim(report);
            if (claim != null)
                return bound != null && claim.ContextId == Descriptor.Id && claim.Generation == Descriptor.Generation &&
                    claim.LogicalOrderId == bound.LogicalOrderId && claim.Request.Instrument == bound.Request.Instrument;
            if (bound != null && report.Identity.Scope.Instrument != bound.Request.Instrument &&
                report.Identity.Scope.Instrument != bound.NativeInstrument) return false;
            if (report.NativeOrder != null)
                return report.NativeOrder == order.NativeIdentity ||
                    (bound?.NativeOrders?.Contains(report.NativeOrder) == true);
            return !string.IsNullOrEmpty(report.ClientOrderId) &&
                report.ClientOrderId == (bound?.ClientOrderId ?? order.ClientOrderId);
        }
    }

    /// <summary>An explicitly captured pre-adoption execution remains excluded when a later correction replays.</summary>
    public bool IsAdoptedExecutionPrefix(ExecutionReport report)
    {
        lock (_gate)
        {
            if (!MatchesReportContext(report)) return false;
            var claim = FindReportClaim(report);
            if (claim == null || claim.ContextId != Descriptor.Id || claim.Generation != Descriptor.Generation) return false;
            var logical = Find(claim.LogicalOrderId) ?? claim;
            return logical.Adopted && logical.PriorExecutions?.Contains(report.Identity) == true;
        }
    }

    private bool MatchesReportContext(ExecutionReport report) =>
        report.ContextId == Descriptor.Id && (report.Identity.Scope.ContextId == null || report.Identity.Scope.ContextId == Descriptor.Id) &&
        (Descriptor.Provider == "simulator" || (report.Identity.Scope.Provider == Descriptor.Provider &&
            report.Identity.Scope.Environment == Descriptor.Environment &&
            (Descriptor.Provider == "Bybit"
                ? Descriptor.VerifiedNativeAccount != null && report.Identity.Scope.Account == Descriptor.Id &&
                    report.Identity.Scope.Product == Descriptor.Product && report.Identity.Scope.Epoch.Length == 0
                : Descriptor.Provider == "Binance" ? report.Identity.Scope.Epoch == Descriptor.Product + ":" + Descriptor.Generation :
                    report.Identity.Scope.Epoch == Descriptor.Generation)));

    private OwnedOrderIntent? FindReportClaim(ExecutionReport report)
    {
        if (report.NativeOrder != null)
        {
            var ids = _journal?.FindNative(report.NativeOrder) ??
                (_ephemeralNative.TryGetValue(NativeKey(report.NativeOrder), out var known) ? known : null);
            if (ids is { Count: > 0 } && Find(ids[^1]) is { } native)
            {
                if (!string.IsNullOrEmpty(report.IntentId) && Find(report.IntentId) is { } operation &&
                    operation.Owner == native.Owner && operation.LogicalOrderId == native.LogicalOrderId)
                    return operation;
                return native;
            }
        }
        if (!string.IsNullOrEmpty(report.IntentId) && Find(report.IntentId) is { } direct) return direct;
        return !string.IsNullOrEmpty(report.ClientOrderId) ? _journal?.FindClient(report.ClientOrderId) : null;
    }


    private OwnedOrderIntent? Eligible(OwnedOrderIntent data, ExecutionReport report)
    {
        if (data.ContextId != Descriptor.Id || data.Generation != Descriptor.Generation || !data.BaselineKnown) return null;
        if (data.PriorExecutions?.Contains(report.Identity) == true &&
            data.OwnedPriorExecutions?.Contains(report.Identity) != true) return null;
        if (report.AccountingOrder is { } physical &&
            (!MatchesReportContext(report) || physical.Order != report.NativeOrder || physical.CumulativeQuantity != null ||
                data.NativeOrders?.Contains(physical.Order) != true ||
                physical.QuantityBaseline != (Find(data.LogicalOrderId) ?? data).QuantityBaseline)) return null;
        return data;
    }

    public OwnedOrderIntent? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate)
        {
            if (_recent.TryGetValue(id, out var found)) return found;
            var data = _journal?.ReadIntent(id);
            if (data != null) Remember(data);
            return data;
        }
    }

    private OwnedOrderIntent Save(OwnedOrderIntent data)
    {
        data = _journal?.SaveIntent(data) ?? data;
        Remember(data);
        return data;
    }
    private void Remember(OwnedOrderIntent data)
    {
        // Without a journal this is the lifetime authority, not a disposable lookup cache.
        if (_journal != null && _recent.Count >= 256 && !_recent.ContainsKey(data.Id)) _recent.Clear();
        _recent[data.Id] = data;
    }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string NativeKey(NativeOrderIdentity native) => Hash(native.Kind + "\n" + native.Id);

    public ExecutionReceipt? FindReceipt(string id) { lock (_gate) return _journal?.FindReceipt(id); }
    public void SaveReceipt(ExecutionReceipt receipt)
    {
        lock (_gate)
        {
            if (_journal == null) throw new IOException("Execution projection requires an explicit durable ownership root.");
            _journal.SaveReceipt(receipt);
        }
    }
    public IReadOnlyList<ExecutionReceipt> PendingReceipts() { lock (_gate) return _journal?.PendingReceipts() ?? Array.Empty<ExecutionReceipt>(); }
    public void Acknowledge(ExecutionReceipt receipt) { lock (_gate) _journal!.Acknowledge(receipt); }
    public ExecutionProjectionCheckpoint? Projected(ExecutionIdentity identity)
    { lock (_gate) return _journal?.Projected(identity); }
    public string? VerifyNativeAccount(string nativeAccount)
    {
        lock (_gate)
        {
            try { _journal?.VerifyNativeAccount(nativeAccount); _identityFault = false; return null; }
            catch (Exception ex)
            {
                _identityFault = true;
                return LastError = "Native account identity reconciliation failed: " + ex.Message;
            }
        }
    }
    public void CommitProjection(ExecutionReceipt receipt, OwnedOrderIntent intent)
    {
        lock (_gate)
        {
            _journal!.CommitProjection(new(intent.Owner, intent.LogicalOrderId, receipt.Cursor, receipt.Report));
            _journal.Acknowledge(receipt);
        }
    }
    public void ReportProjectionFault(string error) => _projectionError = error;
    public void ClearProjectionFault() => _projectionError = null;
}

/// <summary>Small, scoped durable records and exact-ID indexes. Atomic replacement retains the previous complete
/// record until publication. Supported flush/sync failures propagate; no filesystem lease crosses a broker await.</summary>
internal sealed partial class OrderOwnershipJournal
{
    private readonly string _root;
    private readonly AccountContextDescriptor _descriptor;
    private string? _verifiedNativeAccount;
    private sealed record Envelope<T>(int Version, T Value, string Digest);
    private sealed record NativeBinding(NativeOrderIdentity Native, string[] Intents);

    public OrderOwnershipJournal(string root, AccountContextDescriptor descriptor)
    {
        _descriptor = descriptor;
        _root = Path.Combine(root, "execution-state", OrderContext.Hash(descriptor.OwnershipNamespace ?? descriptor.Id));
        EnsureDirectory(_root);
        using var lease = Acquire();
        string path = Path.Combine(_root, "context.json");
        var existing = Read<AccountContextDescriptor>(path);
        if (existing != null && (existing with { VerifiedNativeAccount = null }) != (descriptor with { VerifiedNativeAccount = null }))
            throw new IOException("The ownership context descriptor does not match its persisted namespace.");
        _verifiedNativeAccount = Read<string>(Path.Combine(_root, "native-account.json")) ?? existing?.VerifiedNativeAccount;
        if (descriptor.VerifiedNativeAccount != null) VerifyNativeAccountLeased(descriptor.VerifiedNativeAccount);
        if (existing == null) Write(path, descriptor);
    }

    public OwnedOrderIntent SaveIntent(OwnedOrderIntent intent)
    {
        using var lease = Acquire();
        if (intent.PositionClose != null)
        {
            var current = ReadIntent(intent.Id);
            if (current?.PositionClose == null) throw new IOException("The bounded close identity is unavailable.");
            var nativeOrders = current.NativeOrders == null ? intent.NativeOrders :
                intent.NativeOrders == null || current.NativeOrders.AsSpan().SequenceEqual(intent.NativeOrders) ? current.NativeOrders :
                current.NativeOrders.Union(intent.NativeOrders).ToArray();
            var terminal = intent.TerminalOrder;
            if (current.TerminalOrder is { } knownTerminal && (terminal == null ||
                terminal.Native == knownTerminal.Native && terminal.CumulativeQuantity < knownTerminal.CumulativeQuantity))
                terminal = knownTerminal;
            intent = intent with
            {
                PositionClose = MergePositionCloseFacts(current.PositionClose, current.PositionClose, current.DispatchUncertain),
                DispatchUncertain = current.DispatchUncertain, NativeInstrument = current.NativeInstrument,
                NativeExchange = current.NativeExchange, DispatchQuantity = current.DispatchQuantity, DispatchPrice = current.DispatchPrice,
                NativeOrders = nativeOrders, TerminalOrder = terminal, Rejection = intent.Rejection ?? current.Rejection,
            };
        }
        SaveIntentLeased(intent);
        return intent;
    }

    private void SaveIntentLeased(OwnedOrderIntent intent)
    {
        if (intent.ContextId != _descriptor.Id || intent.Generation != _descriptor.Generation)
            throw new IOException("An intent cannot cross account context generations.");
        Write(IntentPath(intent.Id), intent);
        if (intent.ClientOrderId.Length > 0)
        {
            string path = Path.Combine(_root, "client", OrderContext.Hash(intent.ClientOrderId) + ".json");
            var existing = Read<string>(path);
            // Adoption retains the real predecessor client identity; the native claim chain handles its transfer.
            if (existing == null) Write(path, intent.Id);
            else if (existing != intent.Id && !intent.Adopted && intent.PredecessorId == null)
                throw new IOException("A client identity already belongs to another intent.");
        }
    }

    public OwnedOrderIntent? ReadIntent(string id)
    {
        var data = Read<OwnedOrderIntent>(IntentPath(id));
        if (data != null && (data.Id != id || data.ContextId != _descriptor.Id || data.Generation != _descriptor.Generation))
            throw new IOException("Corrupt or incorrectly scoped intent ownership.");
        return data;
    }
    public OwnedOrderIntent? FindClient(string client)
    {
        string? id = Read<string>(Path.Combine(_root, "client", OrderContext.Hash(client) + ".json"));
        var data = id == null ? null : ReadIntent(id);
        if (data != null && data.ClientOrderId != client) throw new IOException("Client index identity mismatch.");
        return data;
    }
    public IReadOnlyList<OwnedOrderIntent> RecoverableNativeSubmissions(string instrument, OrderTypes type)
    {
        using var lease = Acquire();
        string directory = Path.Combine(_root, "intent");
        if (!Directory.Exists(directory)) return Array.Empty<OwnedOrderIntent>();
        var submissions = new List<OwnedOrderIntent>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var data = Read<OwnedOrderIntent>(path) ?? throw new IOException("An ownership intent disappeared during startup recovery.");
            if (data.ContextId != _descriptor.Id || data.Generation != _descriptor.Generation || IntentPath(data.Id) != path)
                throw new IOException("Corrupt or incorrectly scoped startup ownership intent.");
            bool acknowledgedConditional = data.Request.Type == OrderTypes.Stop && data.NativeOrders is { Length: > 0 };
            bool coveredTerminal = data.TerminalOrder is { } terminal &&
                data.NativeOrders?.Contains(terminal.Native) == true &&
                (data.WorkingOrder == null || data.WorkingOrder.Parent != terminal.Native || data.WorkingOrder.Child == terminal.Native) &&
                terminal.CumulativeQuantity <= (ReadIntent(data.LogicalOrderId) ?? data).QuantityProjected;
            if (acknowledgedConditional && !HasCurrentConditionalClaim(data)) continue;
            if (data.Request.Instrument == instrument && data.Request.Type == type &&
                !data.EphemeralPreparation && !data.Emergency && data.Rejection == null && !coveredTerminal &&
                (data.Adopted ? acknowledgedConditional :
                    data.DispatchUncertain && (data.NativeOrders is not { Length: > 0 } || acknowledgedConditional) &&
                    !string.IsNullOrEmpty(data.ClientOrderId) && !string.IsNullOrEmpty(data.NativeInstrument)))
                submissions.Add(data);
        }
        return submissions;
    }
    private bool HasCurrentConditionalClaim(OwnedOrderIntent data)
    {
        // Once triggered, ownership of the actual working child takes precedence over its historical parent.
        var working = data.WorkingOrder;
        int count = working == null ? data.NativeOrders!.Length : 1;
        for (int i = 0; i < count; i++)
        {
            var native = working?.Child ?? data.NativeOrders![i];
            var claims = FindNative(native);
            // The exact intent can outlive an interrupted first index write; native recovery repairs that gap.
            if (claims is not { Count: > 0 }) return true;
            var current = ReadIntent(claims[^1]);
            // The native index can retain an earlier operation of the same logical owner.
            if (current != null && current.Owner == data.Owner && current.LogicalOrderId == data.LogicalOrderId) return true;
        }
        return false;
    }
    public void Bind(NativeOrderIdentity native, OwnedOrderIntent intent)
    {
        using var lease = Acquire();
        string path = Path.Combine(_root, "native", OrderContext.NativeKey(native) + ".json");
        var binding = Read<NativeBinding>(path);
        if (binding != null && binding.Native != native) throw new IOException("Native index identity mismatch.");
        var ids = binding?.Intents ?? Array.Empty<string>();
        if (ids.Contains(intent.Id)) return;
        if (ids.Length > 0 && !intent.Adopted)
        {
            var previous = ReadIntent(ids[^1]);
            if (previous == null || previous.Owner != intent.Owner || previous.LogicalOrderId != intent.LogicalOrderId)
                throw new IOException("A native identity is already bound to another logical owner.");
            return; // Same logical owner: operation lineage lives in each intent, not an ever-growing native lookup array.
        }
        Write(path, new NativeBinding(native, ids.Append(intent.Id).ToArray()));
    }

    public OwnedOrderIntent AcceptNativeAmendment(string id, NativeOrderAmendment amendment)
    {
        using var lease = Acquire();
        return AcceptNativeAmendmentLeased(id, amendment);
    }

    private OwnedOrderIntent AcceptNativeAmendmentLeased(string id, NativeOrderAmendment amendment)
    {
        var data = ReadIntent(id) ?? throw new IOException("The accepted amendment identity is unavailable.");
        string path = Path.Combine(_root, "native", OrderContext.NativeKey(amendment.Native) + ".json");
        var binding = Read<NativeBinding>(path);
        if (binding == null || binding.Native != amendment.Native || binding.Intents.Length == 0)
            throw new IOException("The accepted amendment has no exact native predecessor claim.");
        var current = ReadIntent(binding.Intents[^1]) ?? throw new IOException("The native predecessor identity is unavailable.");
        if (!data.DispatchUncertain || data.PredecessorId == null || data.PredecessorFilled == null || data.Rejection != null || data.Emergency ||
            data.Owner != current.Owner || data.LogicalOrderId != current.LogicalOrderId ||
            (data.NativeInstrument != current.NativeInstrument && !(current.Adopted && current.NativeInstrument == null &&
                current.NativeOrders?.Contains(amendment.Native) == true)) ||
            (data.NativeExchange != current.NativeExchange && !(current.Adopted && current.NativeExchange == null &&
                current.NativeOrders?.Contains(amendment.Native) == true)) ||
            data.Request.Instrument != current.Request.Instrument || data.Request.Direction != current.Request.Direction ||
            data.Request.Type != current.Request.Type || data.Request.ReduceOnly != current.Request.ReduceOnly ||
            data.DispatchQuantity + (data.PredecessorFilled ?? 0) != amendment.TotalQuantity ||
            data.DispatchPrice != amendment.Price ||
            amendment.ClientOrderId != (current.AcceptedNativeAmendment?.ClientOrderId ?? current.ClientOrderId))
            throw new IOException("The accepted native amendment disagrees with its dispatched lineage.");
        if (current.Id != data.Id)
        {
            var ancestor = data;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (ancestor.Id != current.Id)
            {
                if (!seen.Add(ancestor.Id) || ancestor.PredecessorId == null ||
                    ReadIntent(ancestor.PredecessorId) is not { } predecessor ||
                    predecessor.Owner != data.Owner || predecessor.LogicalOrderId != data.LogicalOrderId)
                    throw new IOException("The accepted amendment is not a successor of the current native claim.");
                ancestor = predecessor;
            }
        }
        if (data.AcceptedNativeAmendment != null && data.AcceptedNativeAmendment != amendment)
            throw new IOException("The accepted amendment evidence cannot change.");
        if (current.Id == data.Id && data.AcceptedNativeAmendment == amendment && data.NativeOrders?.Contains(amendment.Native) == true)
            return data;
        data = data with { AcceptedNativeAmendment = amendment,
            NativeOrders = data.NativeOrders?.Contains(amendment.Native) == true ? data.NativeOrders :
                (data.NativeOrders ?? Array.Empty<NativeOrderIdentity>()).Append(amendment.Native).ToArray() };
        SaveIntentLeased(data);
        if (current.Id != data.Id)
        {
            var claims = (string[])binding.Intents.Clone();
            claims[^1] = data.Id;
            Write(path, new NativeBinding(amendment.Native, claims));
        }
        return data;
    }

    public OwnedOrderIntent ResolveNativeAmendment(OwnedOrderIntent claim, NativeOrderAmendment evidence, bool allowNewAcceptance)
    {
        using var lease = Acquire();
        List<OwnedOrderIntent>? persisted = null, attempted = null;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(_root, "intent"), "*.json"))
        {
            var data = Read<OwnedOrderIntent>(path) ?? throw new IOException("An amendment identity disappeared during recovery.");
            if (data.ContextId != _descriptor.Id || data.Generation != _descriptor.Generation || IntentPath(data.Id) != path)
                throw new IOException("Corrupt or incorrectly scoped amendment ownership.");
            if (data.PredecessorId == null || data.PredecessorFilled == null || !data.DispatchUncertain || data.Rejection != null || data.Emergency ||
                data.Owner != claim.Owner || data.LogicalOrderId != claim.LogicalOrderId ||
                (data.NativeInstrument != claim.NativeInstrument && !(claim.Adopted && claim.NativeInstrument == null &&
                    claim.NativeOrders?.Contains(evidence.Native) == true)) ||
                (data.NativeExchange != claim.NativeExchange && !(claim.Adopted && claim.NativeExchange == null &&
                    claim.NativeOrders?.Contains(evidence.Native) == true)) ||
                data.Request.Instrument != claim.Request.Instrument || data.Request.Direction != claim.Request.Direction ||
                data.Request.Type != claim.Request.Type || data.Request.ReduceOnly != claim.Request.ReduceOnly ||
                data.NativeOrders is { Length: > 0 } && !data.NativeOrders.Contains(evidence.Native) ||
                evidence.ClientOrderId != (claim.AcceptedNativeAmendment?.ClientOrderId ?? claim.ClientOrderId)) continue;
            if (data.AcceptedNativeAmendment is { } accepted)
            {
                if (accepted.Native != evidence.Native || accepted.ClientOrderId != evidence.ClientOrderId ||
                    data.NativeOrders?.Contains(evidence.Native) != true ||
                    data.DispatchQuantity + data.PredecessorFilled != accepted.TotalQuantity || data.DispatchPrice != accepted.Price) continue;
                (persisted ??= new()).Add(data);
            }
            else if (allowNewAcceptance && data.DispatchQuantity + data.PredecessorFilled == evidence.TotalQuantity &&
                data.DispatchPrice == evidence.Price)
                (attempted ??= new()).Add(data);
        }
        var durable = LatestNativeSuccessor(persisted, claim);
        if (durable?.AcceptedNativeAmendment is { } acceptance)
            claim = AcceptNativeAmendmentLeased(durable.Id, acceptance);
        if (!allowNewAcceptance || claim.DispatchQuantity + (claim.PredecessorFilled ?? 0) == evidence.TotalQuantity &&
            claim.DispatchPrice == evidence.Price) return claim;
        var selected = LatestNativeSuccessor(attempted, claim);
        return selected == null ? claim : AcceptNativeAmendmentLeased(selected.Id, evidence);
    }

    private OwnedOrderIntent? LatestNativeSuccessor(IReadOnlyList<OwnedOrderIntent>? candidates, OwnedOrderIntent ancestor)
    {
        if (candidates == null) return null;
        OwnedOrderIntent? selected = null;
        foreach (var candidate in candidates)
        {
            if (!IsNativeSuccessor(candidate, ancestor)) continue;
            bool superseded = false;
            foreach (var other in candidates)
                if (other.Id != candidate.Id && IsNativeSuccessor(other, candidate)) { superseded = true; break; }
            if (superseded) continue;
            if (selected != null) throw new IOException("Rithmic amendment replay has competing dispatched lineages.");
            selected = candidate;
        }
        return selected;
    }

    private bool IsNativeSuccessor(OwnedOrderIntent data, OwnedOrderIntent ancestor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (data.Id != ancestor.Id)
        {
            if (!seen.Add(data.Id) || data.PredecessorId == null || ReadIntent(data.PredecessorId) is not { } previous ||
                previous.Owner != ancestor.Owner || previous.LogicalOrderId != ancestor.LogicalOrderId) return false;
            data = previous;
        }
        return true;
    }
    public IReadOnlyList<string>? FindNative(NativeOrderIdentity native)
    {
        var binding = Read<NativeBinding>(Path.Combine(_root, "native", OrderContext.NativeKey(native) + ".json"));
        if (binding != null && binding.Native != native) throw new IOException("Native index identity mismatch.");
        return binding?.Intents;
    }
    public ExecutionReceipt? FindReceipt(string id) => Read<ExecutionReceipt>(ReceiptPath(id));
    public void SaveReceipt(ExecutionReceipt receipt)
    {
        using var lease = Acquire();
        string path = ReceiptPath(receipt.Id);
        var previous = Read<ExecutionReceipt>(path);
        if (previous == null) Write(path, receipt);
        else if (previous.Cursor != receipt.Cursor || previous.Report.Identity != receipt.Report.Identity)
            throw new IOException("Execution receipt identity collision.");
    }
    public IReadOnlyList<ExecutionReceipt> PendingReceipts()
    {
        string directory = Path.Combine(_root, "pending");
        if (!Directory.Exists(directory)) return Array.Empty<ExecutionReceipt>();
        var receipts = new List<ExecutionReceipt>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            receipts.Add(Read<ExecutionReceipt>(path) ?? throw new IOException("A pending execution receipt disappeared."));
        receipts.Sort((a, b) => a.Cursor.Epoch == b.Cursor.Epoch ? a.Cursor.Revision.CompareTo(b.Cursor.Revision) : a.Report.Time.CompareTo(b.Report.Time));
        return receipts;
    }
    public void Acknowledge(ExecutionReceipt receipt)
    {
        using var lease = Acquire();
        string path = ReceiptPath(receipt.Id);
        File.Delete(path);
        StorageSync.SyncDirectory(Path.GetDirectoryName(path)!);
    }
    public ExecutionProjectionCheckpoint? Projected(ExecutionIdentity identity)
    {
        var checkpoint = Read<ExecutionProjectionCheckpoint>(ProjectionPath(identity));
        if (checkpoint != null && checkpoint.Report.Identity != identity)
            throw new IOException("Projection index execution identity mismatch.");
        return checkpoint;
    }
    public void CommitProjection(ExecutionProjectionCheckpoint checkpoint)
    {
        using var lease = Acquire();
        Write(ProjectionPath(checkpoint.Report.Identity), checkpoint);
    }
    public void VerifyNativeAccount(string nativeAccount)
    {
        if (_verifiedNativeAccount == nativeAccount) return;
        using var lease = Acquire();
        VerifyNativeAccountLeased(nativeAccount);
    }
    private void VerifyNativeAccountLeased(string nativeAccount)
    {
        string? persisted = Read<string>(Path.Combine(_root, "native-account.json")) ?? _verifiedNativeAccount;
        if (persisted != null && persisted != nativeAccount)
            throw new IOException("The reported native account differs from this local ownership namespace.");
        if (persisted == null) Write(Path.Combine(_root, "native-account.json"), nativeAccount);
        _verifiedNativeAccount = nativeAccount;
    }

    private JournalLease Acquire()
    {
        var mutex = new Mutex(false, "TradingHub.OrderOwnership." + OrderContext.Hash(Path.GetFullPath(_root)));
        bool owned = false;
        try
        {
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
            owned = true;
            return new(mutex, new FileStream(Path.Combine(_root, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch
        {
            if (owned) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }
    private sealed class JournalLease(Mutex mutex, FileStream file) : IDisposable
    {
        public void Dispose()
        {
            try { file.Dispose(); }
            finally { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
    private static void EnsureDirectory(string directory)
    {
        var missing = new Stack<string>();
        string? parent = directory;
        while (parent != null && !Directory.Exists(parent))
        {
            missing.Push(parent);
            parent = Path.GetDirectoryName(parent);
        }
        while (missing.Count > 0)
        {
            string path = missing.Pop();
            Directory.CreateDirectory(path);
            if (Path.GetDirectoryName(path) is { } existingParent) StorageSync.SyncDirectory(existingParent);
        }
    }
    private string ProjectionPath(ExecutionIdentity identity) => Path.Combine(_root, "projected",
        OrderContext.Hash(JsonSerializer.Serialize(identity)) + ".json");
    private string IntentPath(string id) => Path.Combine(_root, "intent", OrderContext.Hash(id) + ".json");
    private string ReceiptPath(string id) => Path.Combine(_root, "pending", id + ".json");
    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var envelope = JsonSerializer.Deserialize<Envelope<T>>(file) ?? throw new IOException("Missing ownership record envelope.");
        if (envelope.Version != 1 || envelope.Digest != OrderContext.Hash(JsonSerializer.Serialize(envelope.Value)))
            throw new IOException("Ownership record checksum or version mismatch.");
        return envelope.Value;
    }
    private static void Write<T>(string path, T value)
    {
        string directory = Path.GetDirectoryName(path)!;
        EnsureDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new Envelope<T>(1, value, OrderContext.Hash(JsonSerializer.Serialize(value))));
                StorageSync.Flush(file);
            }
            File.Move(temporary, path, overwrite: true);
            StorageSync.SyncDirectory(directory);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Explicit manual/strategy preparation; there is no unowned transport entry point.</summary>
public static class OrderActions
{
    public static Order Place(ITradingAccount account, OrderOwner owner, OrderTypes type, OrderDirections direction,
        decimal quantity, decimal price = 0, string comment = "", bool reduceOnly = false, decimal strategyQuantity = 0,
        bool protective = false, decimal? maximumOwnedQuantity = null) =>
        account.Submit(account.OrderContext.Prepare(owner, new(account.Instrument.Key, direction, type, quantity,
            account.Instrument.RoundPrice(price), reduceOnly, comment, strategyQuantity, protective, maximumOwnedQuantity)));
    public static Order Manual(ITradingAccount account, OrderTypes type, OrderDirections direction, decimal quantity,
        decimal price = 0, string comment = "", bool reduceOnly = false)
    {
        decimal before = account.Position.Volume;
        decimal after = before + (direction == OrderDirections.Buy ? quantity : -quantity);
        bool reducing = !RealOrderPermission.IncreasesExposure(before, direction == OrderDirections.Buy ? quantity : -quantity);
        return Place(account, account.OrderContext.ManualOwner, type, direction, quantity, price, comment, reduceOnly,
            protective: reducing, maximumOwnedQuantity: Math.Max(Math.Abs(before), Math.Abs(after)));
    }
    public static Order Replace(ITradingAccount account, Order order, decimal price, decimal quantity) =>
        account.Replace(order, account.OrderContext.PrepareReplacement(order, price, quantity));
    public static Order Reprice(ITradingAccount account, Order order, decimal price) =>
        account.Replace(order, account.OrderContext.PreparePriceUpdate(order, price));
    public static void Flatten(ITradingAccount account)
    {
        if (account is ICoordinatedPositionClose coordinated)
        {
            coordinated.Flatten();
            return;
        }
        account.CancelAll();
        decimal position = account.Position.Volume;
        if (position != 0) Manual(account, OrderTypes.Market, position > 0 ? OrderDirections.Sell : OrderDirections.Buy,
            Math.Abs(position), comment: "Close", reduceOnly: true);
    }
    public static void Reverse(ITradingAccount account)
    {
        if (account is ICoordinatedPositionOperations coordinated)
        {
            coordinated.Reverse();
            return;
        }
        decimal position = account.Position.Volume;
        if (position != 0) Manual(account, OrderTypes.Market, position > 0 ? OrderDirections.Sell : OrderDirections.Buy,
            Math.Abs(position) * 2, comment: "Reverse");
    }
}
