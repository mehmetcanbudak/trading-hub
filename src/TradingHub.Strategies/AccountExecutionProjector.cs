using System.Runtime.CompilerServices;
using TradingHub.Trading;

namespace TradingHub.Strategies;

public sealed record OwnedExecutionUpdate(OwnedOrderIntent Intent, ExecutionReport Report, LiveFill Fill);
public sealed record OwnedQuantityUpdate(OwnedOrderQuantity Observation, LiveFill Fill);

/// <summary>One replay consumer per account source, independent of runner lifetime. One active runner and any
/// stopped runners still settling orders may subscribe to an owner; terminal attribution uses exact durable identity.</summary>
public sealed class AccountExecutionProjector
{
    private static readonly ConditionalWeakTable<IExecutionSource, AccountExecutionProjector> Projectors = new();
    private readonly IExecutionSource _source;
    private static readonly object ProjectorsGate = new();
    // Exact generation membership is weak: a registry entry must not keep an account or runner alive.
    // This gate orders durable writes and their queued facts, never their callbacks.
    private static readonly object LedgersGate = new();
    private static readonly Dictionary<(string Root, string Key, Guid Generation), WeakReference<ProjectionSubscription>[]> Ledgers = new();
    private readonly OrderContext _context;
    private readonly string? _root;
    private readonly object _slotsGate = new();
    private readonly Dictionary<string, ProjectionSubscription[]> _slots = new(StringComparer.Ordinal);
    private readonly List<ProjectionSubscription> _notifications = new();
    private readonly Queue<WeakReference<ProjectionSubscription>[]> _foreignNotifications = new();
    private int _foreignDrainScheduled;
    private int _availability = (int)ExecutionAvailability.Unavailable;
    public ExecutionAvailability Availability => (ExecutionAvailability)Volatile.Read(ref _availability);
    private ExecutionCursor _cursor;
    private int _processing, _requested;

    private AccountExecutionProjector(IExecutionSource source, OrderContext context)
    {
        _source = source; _context = context; _root = context.StorageRoot;
        source.Changed += Synchronize;
        context.OwnershipChanged += Synchronize;
        context.QuantityChanged += OnQuantity;
    }

    public static AccountExecutionProjector For(IExecutionSource source, OrderContext context)
    {
        lock (ProjectorsGate)
        {
            if (!Projectors.TryGetValue(source, out var projector))
            {
                projector = new(source, context);
                Projectors.Add(source, projector);
            }
            if (!ReferenceEquals(projector._context, context) || projector._root != context.StorageRoot)
                throw new InvalidOperationException("An execution source cannot move between ownership contexts or storage roots.");
            return projector;
        }
    }

    public ProjectionSubscription Subscribe(OrderOwner owner, Action changed)
    {
        ProjectionSubscription slot;
        lock (LedgersGate)
        {
            lock (_slotsGate)
            {
                var current = _slots.TryGetValue(owner.Id, out var slots) ? slots : Array.Empty<ProjectionSubscription>();
                if (current.Any(static subscription => subscription.Active)) throw new InvalidOperationException("This live ledger owner already has an active runner.");
                slot = new ProjectionSubscription(this, owner, changed);
                var next = new ProjectionSubscription[current.Length + 1];
                current.CopyTo(next, 0);
                next[^1] = slot;
                _slots[owner.Id] = next;
            }
            RegisterLedger(slot);
        }
        return slot;
    }

    // Published arrays never change: callbacks may dispose a pending observer or subscribe its successor.
    // Snapshot lookup is bounded by the owner's live/unresolved runners, without allocating per execution.
    private ProjectionSubscription[] Subscriptions(string ownerId)
    {
        lock (_slotsGate)
            return _slots.TryGetValue(ownerId, out var slots) ? slots : Array.Empty<ProjectionSubscription>();
    }

    private void RegisterLedger(ProjectionSubscription slot)
    {
        if (_root == null || slot._owner.LedgerKey == null) return;
        lock (LedgersGate)
        {
            var key = (_root, slot._owner.LedgerKey, slot._owner.LedgerGeneration);
            var current = Ledgers.TryGetValue(key, out var slots) ? slots : Array.Empty<WeakReference<ProjectionSubscription>>();
            Ledgers[key] = current.Where(static weak => weak.TryGetTarget(out var target) && !target.Disposed)
                .Append(new WeakReference<ProjectionSubscription>(slot)).ToArray();
        }
    }

    private void UnregisterLedger(ProjectionSubscription slot)
    {
        if (_root == null || slot._owner.LedgerKey == null) return;
        lock (LedgersGate)
        {
            var key = (_root, slot._owner.LedgerKey, slot._owner.LedgerGeneration);
            if (!Ledgers.TryGetValue(key, out var current)) return;
            var next = current.Where(weak => weak.TryGetTarget(out var target) &&
                !ReferenceEquals(target, slot) && !target.Disposed).ToArray();
            if (next.Length == 0) Ledgers.Remove(key);
            else Ledgers[key] = next;
        }
    }

    internal LiveStore.ExecutionProjectionResult CommitOwnedEvent(OrderOwner owner, LiveFill fill,
        bool executionOnly = false, LiveFill? knownProjection = null)
    {
        if (_root == null || owner.LedgerKey == null)
            throw new InvalidOperationException("A durable fact requires its frozen ledger root and owner.");
        lock (LedgersGate)
        {
            var result = executionOnly
                ? LiveStore.ProjectExecution(_root, owner.LedgerKey, owner.LedgerGeneration, fill, knownProjection)
                : LiveStore.AppendOwnedEvent(_root, owner.LedgerKey, owner.LedgerGeneration, fill);
            if (result.Status == LiveStore.ExecutionProjectionStatus.Applied &&
                Ledgers.TryGetValue((_root, owner.LedgerKey, owner.LedgerGeneration), out var slots))
            {
                bool queued = false;
                foreach (var weak in slots)
                    if (weak.TryGetTarget(out var slot) && !ReferenceEquals(slot._projector, this))
                        queued |= slot.EnqueueCommitted(fill);
                if (queued) _foreignNotifications.Enqueue(slots);
            }
            return result;
        }
    }

    internal void NotifyCommittedFacts()
    {
        while (true)
        {
            WeakReference<ProjectionSubscription>[] slots;
            lock (LedgersGate)
            {
                if (_foreignNotifications.Count == 0) return;
                slots = _foreignNotifications.Dequeue();
            }
            foreach (var weak in slots)
                if (weak.TryGetTarget(out var slot) && !ReferenceEquals(slot._projector, this) && !slot.Disposed)
                    slot._projector.Synchronize();
        }
    }

    private void ScheduleForeignNotifications()
    {
        lock (LedgersGate)
            if (_foreignNotifications.Count == 0) return;
        if (Interlocked.CompareExchange(ref _foreignDrainScheduled, 1, 0) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(static weak =>
        {
            if (!weak.TryGetTarget(out var projector)) return;
            try { projector.NotifyCommittedFacts(); }
            finally
            {
                Volatile.Write(ref projector._foreignDrainScheduled, 0);
                projector.ScheduleForeignNotifications();
            }
        }, new WeakReference<AccountExecutionProjector>(this), preferLocal: false);
    }

    // Retirement and source projection must not pass one another. Retained stopped observers are
    // outstanding authority for this generation even when its successor is locally flat.
    internal bool TryResetOwner(OrderOwner owner, ProjectionSubscription? current, Action reset)
    {
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return false;
        try
        {
            var snapshot = _source.Capture(_cursor);
            if (snapshot.Changes.Count != 0) return false;
            lock (LedgersGate)
            {
                if (_context.HasStorageFault || _context.HasUnprojectedQuantity(owner)) return false;
                if (_root != null && owner.LedgerKey != null &&
                    Ledgers.TryGetValue((_root, owner.LedgerKey, owner.LedgerGeneration), out var observers))
                    foreach (var weak in observers)
                        if (weak.TryGetTarget(out var observer) && !observer.Disposed &&
                            (!ReferenceEquals(observer, current) || observer.HasPendingUpdates)) return false;
                lock (_slotsGate)
                {
                    if (_slots.TryGetValue(owner.Id, out var slots))
                        foreach (var slot in slots)
                            if (!ReferenceEquals(slot, current) || slot.HasPendingUpdates) return false;
                    reset();
                }
            }
            return true;
        }
        finally
        {
            Volatile.Write(ref _processing, 0);
            Synchronize();
        }
    }

    private void NotifyPending()
    {
        lock (_slotsGate)
            foreach (var slots in _slots.Values)
                foreach (var slot in slots)
                    if (slot.TakeNotification()) _notifications.Add(slot);
        try
        {
            foreach (var slot in _notifications)
            {
                try { slot.Notify(); }
                catch (Exception ex) { _context.ReportProjectionFault("An execution owner could not reconcile its captured updates: " + ex.Message); }
            }
        }
        finally { _notifications.Clear(); }
    }

    public void Synchronize()
    {
        Interlocked.Exchange(ref _requested, 1);
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return;
        try
        {
            // Retry each genuine outstanding capture once per synchronization attempt. A failed writer
            // must not turn reentrant source notifications into an unbounded quantity-write loop.
            bool quantitiesHealthy = true;
            foreach (var pending in _context.PendingQuantities()) quantitiesHealthy &= ProjectQuantity(pending);
            do
            {
                Interlocked.Exchange(ref _requested, 0);
                bool healthy = quantitiesHealthy;
                if (_root != null)
                    foreach (var pending in _context.PendingReceipts()) healthy &= Project(pending.Report, pending.Cursor, pending);
                var snapshot = _source.Capture(_cursor);
                var availability = (ExecutionAvailability)Interlocked.Exchange(ref _availability, (int)snapshot.Availability);
                if (availability != snapshot.Availability)
                    lock (_slotsGate)
                        foreach (var slots in _slots.Values)
                            foreach (var slot in slots) slot.RequestNotification();
                long start = _cursor.Epoch == snapshot.Epoch ? _cursor.Revision : 0;
                if (snapshot.Changes.Count == 0)
                {
                    if (healthy && _context.PendingQuantities().Count == 0) _context.ClearProjectionFault();
                    NotifyPending();
                    NotifyCommittedFacts();
                    continue;
                }
                if (snapshot.Revision != start + snapshot.Changes.Count)
                    throw new IOException("The execution source has an incomplete revision range; replay reconciliation is required.");
                // Each revision is an execution transition: a downward amendment must see its earlier quantity
                // even when both facts arrive in the first replay batch. Distinct identities also remain distinct.
                bool received = true;
                long revision = start;
                foreach (var report in snapshot.Changes)
                {
                    received &= Receive(report, new(snapshot.Epoch, ++revision), out bool projected);
                    healthy &= projected;
                }
                // Source progress is acknowledged by durable receipt, not by downstream attribution/ledger success.
                // Unprojected receipts remain independently retryable without replaying lifetime source history.
                if (received) _cursor = new(snapshot.Epoch, snapshot.Revision);
                if (healthy && received && _context.PendingQuantities().Count == 0) _context.ClearProjectionFault();
                NotifyPending();
                NotifyCommittedFacts();
            }
            while (Volatile.Read(ref _requested) != 0);
        }
        catch (Exception ex) { _context.ReportProjectionFault("Execution projection remains unreconciled: " + ex.Message); }
        finally
        {
            NotifyPending();
            NotifyCommittedFacts();
            Volatile.Write(ref _processing, 0);
            if (Volatile.Read(ref _requested) != 0) Synchronize();
        }
    }

    private bool Receive(ExecutionReport report, ExecutionCursor cursor, out bool projected)
    {
        ExecutionReceipt? receipt = null;
        bool received = true;
        if (_root != null)
        {
            try { receipt = _context.SaveReceipt(cursor, report); }
            catch (Exception ex)
            {
                received = false;
                _context.ReportProjectionFault("Could not persist actual execution receipt: " + ex.Message);
            }
        }
        // Even a receipt-storage failure must deliver observed facts to the live owner. The held cursor retries
        // durable receipt/projection; quantity protection must not wait for storage or fee finality.
        projected = Project(report, cursor, receipt);
        return received;
    }

    private bool Project(ExecutionReport report, ExecutionCursor cursor, ExecutionReceipt? receipt)
    {
        if (report.ContextId != _context.Descriptor.Id || report.Identity.Scope.ContextId != _context.Descriptor.Id ||
            report.Identity.Scope.Provider != _context.Descriptor.Provider)
            throw new IOException("Execution account context does not match its canonical scoped identity.");
        var intent = _context.Resolve(report);
        if (intent == null)
        {
            if (_context.IsAdoptedExecutionPrefix(report))
            {
                if (receipt != null) _context.Acknowledge(receipt);
                return true;
            }
            _context.ReportProjectionFault("Execution ownership or the adopted execution prefix is not yet proven.");
            return false;
        }
        if (report.Identity.Scope.Instrument != intent.Request.Instrument && report.Identity.Scope.Instrument != intent.NativeInstrument)
            throw new IOException("Execution instrument does not match the prepared native route.");
        var fill = ToFill(intent, report);
        bool committed = true;
        if (intent.Owner.LedgerKey != null)
        {
            if (_root == null) throw new IOException("A persisted ledger owner has no bound ownership root.");
            var previous = _context.Projected(report.Identity);
            // Source revisions are comparable only within one incarnation. A delayed receipt from that same
            // epoch may be retired, but must not undo a newer committed fact or replace its durable checkpoint.
            if (previous != null && previous.Owner == intent.Owner && previous.LogicalOrderId == intent.LogicalOrderId &&
                previous.Cursor.Epoch == cursor.Epoch && previous.Cursor.Revision > cursor.Revision)
            {
                if (receipt != null) _context.Acknowledge(receipt);
                return true;
            }
            LiveFill? known = previous != null && previous.Owner == intent.Owner && previous.LogicalOrderId == intent.LogicalOrderId
                ? ToFill(intent, previous.Report) : null;
            var result = CommitOwnedEvent(intent.Owner, fill, executionOnly: true, knownProjection: known);
            if (result.Status == LiveStore.ExecutionProjectionStatus.Retired)
            {
                foreach (var slot in Subscriptions(intent.Owner.Id)) slot.MarkRetired();
                if (receipt != null) _context.Acknowledge(receipt);
                return true;
            }
            if (result.Status == LiveStore.ExecutionProjectionStatus.Error)
            {
                committed = false;
                _context.ReportProjectionFault(result.Error!);
            }
            else if (receipt != null)
            {
                try { _context.CommitProjection(receipt, intent); }
                catch (Exception ex)
                {
                    committed = false;
                    _context.ReportProjectionFault("Execution committed or pending; projection acknowledgement failed: " + ex.Message);
                }
            }
        }
        else if (receipt != null) _context.Acknowledge(receipt);
        var update = new OwnedExecutionUpdate(intent, report, fill);
        foreach (var slot in Subscriptions(intent.Owner.Id)) slot.Enqueue(update);
        return committed;
    }

    private void OnQuantity(OwnedOrderQuantity observation)
    {
        ProjectQuantity(observation);
        ScheduleForeignNotifications();
    }

    private bool ProjectQuantity(OwnedOrderQuantity observation)
    {
        var intent = observation.Intent;
        var fill = new LiveFill(observation.Time, observation.Buy, 0, 0, observation.Symbol, observation.PointValue)
        {
            Kind = LiveFillKind.QuantityObservation, OrderId = intent.LogicalOrderId,
            CumulativeQuantity = observation.CumulativeQuantity, ContextId = intent.ContextId,
            OwnerId = intent.Owner.Id, OwnerGeneration = intent.Owner.LedgerGeneration,
        };
        bool committed = true;
        bool retired = false;
        if (intent.Owner.LedgerKey != null)
        {
            if (_root == null) throw new IOException("An owned quantity observation has no frozen persistence root.");
            var result = CommitOwnedEvent(intent.Owner, fill);
            committed = result.Status != LiveStore.ExecutionProjectionStatus.Error;
            retired = result.Status == LiveStore.ExecutionProjectionStatus.Retired;
            if (!committed) _context.ReportProjectionFault(result.Error!);
        }
        // Admit the local queue before acknowledging the Core capture: reset must always see
        // either the exact outstanding capture or its not-yet-consumed local update.
        var update = new OwnedQuantityUpdate(observation, fill);
        foreach (var slot in Subscriptions(intent.Owner.Id))
        {
            if (retired) slot.MarkRetired();
            slot.EnqueueQuantity(update);
        }
        if (committed)
        {
            try { _context.AcknowledgeQuantity(observation); }
            catch (Exception ex)
            {
                committed = false;
                _context.ReportProjectionFault("Quantity committed; its durable acknowledgement is pending: " + ex.Message);
            }
        }
        return committed;
    }

    private static LiveFill ToFill(OwnedOrderIntent intent, ExecutionReport report) =>
        new(report.Time, report.Buy, report.Quantity, report.Price, report.Symbol, report.PointValue)
        {
            Kind = LiveFillKind.Execution, OrderId = intent.LogicalOrderId, ExecutionId = report.Identity,
            Fees = report.Fees, FeesComplete = report.FeesComplete, Currency = report.AccountingCurrency,
            ContextId = report.ContextId, OwnerId = intent.Owner.Id, OwnerGeneration = intent.Owner.LedgerGeneration,
        };

    public sealed class ProjectionSubscription : IDisposable
    {
        internal readonly AccountExecutionProjector _projector;
        internal readonly OrderOwner _owner;
        private readonly Action _changed;
        private readonly object _gate = new();
        private readonly List<OwnedExecutionUpdate> _pending = new();
        private readonly Dictionary<string, OwnedQuantityUpdate> _quantities = new(StringComparer.Ordinal);
        private readonly List<LiveFill> _committed = new();
        private bool _disposed;
        private bool _retired;
        private bool _notificationPending;
        internal bool HasPendingUpdates
        {
            get { lock (_gate) return _pending.Count != 0 || _quantities.Count != 0 || _committed.Count != 0; }
        }
        internal bool TakeNotification()
        {
            lock (_gate)
            {
                if (_disposed || !_notificationPending) return false;
                _notificationPending = false;
                return true;
            }
        }
        internal void Notify()
        {
            lock (_gate) { if (_disposed) return; }
            _changed();
        }
        public bool Retired { get { lock (_gate) return _retired; } }
        internal bool Disposed { get { lock (_gate) return _disposed; } }
        internal void RequestNotification() { lock (_gate) { if (!_disposed) _notificationPending = true; } }
        internal bool Active { get; private set; } = true;
        internal void RetainPendingOrders()
        {
            lock (_projector._slotsGate) Active = false;
        }
        internal void MarkRetired() { lock (_gate) { _retired = true; _notificationPending = true; } }
        internal ProjectionSubscription(AccountExecutionProjector projector, OrderOwner owner, Action changed)
        { _projector = projector; _owner = owner; _changed = changed; }
        internal void Enqueue(OwnedExecutionUpdate update)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _pending.Add(update);
                _notificationPending = true;
            }
        }
        internal void EnqueueQuantity(OwnedQuantityUpdate update)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (!_quantities.TryGetValue(update.Observation.Intent.LogicalOrderId, out var previous) ||
                    previous.Observation.CumulativeQuantity < update.Observation.CumulativeQuantity)
                    _quantities[update.Observation.Intent.LogicalOrderId] = update;
                _notificationPending = true;
            }
        }
        internal bool EnqueueCommitted(LiveFill fill)
        {
            lock (_gate)
            {
                if (_disposed) return false;
                _committed.Add(fill);
                _notificationPending = true;
                return true;
            }
        }
        internal IReadOnlyList<LiveFill> DrainCommitted()
        {
            lock (_gate)
            {
                if (_committed.Count == 0) return Array.Empty<LiveFill>();
                // Keep admission fenced until the runner has actually applied this prefix.
                return _committed.ToArray();
            }
        }
        internal void AcknowledgeCommitted(int count)
        {
            lock (_gate)
                if (!_disposed && count > 0) _committed.RemoveRange(0, count);
        }
        public IReadOnlyList<OwnedQuantityUpdate> DrainQuantities()
        {
            lock (_gate)
            {
                if (_quantities.Count == 0) return Array.Empty<OwnedQuantityUpdate>();
                var updates = _quantities.Values.ToArray();
                _quantities.Clear();
                return updates;
            }
        }
        public IReadOnlyList<OwnedExecutionUpdate> Drain()
        {
            lock (_gate)
            {
                if (_pending.Count == 0) return Array.Empty<OwnedExecutionUpdate>();
                var updates = _pending.ToArray();
                _pending.Clear();
                return updates;
            }
        }
        public void Dispose()
        {
            lock (_projector._slotsGate)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _pending.Clear();
                    _quantities.Clear();
                    _committed.Clear();
                }
                if (!_projector._slots.TryGetValue(_owner.Id, out var current)) return;
                int index = Array.IndexOf(current, this);
                if (index < 0) return;
                if (current.Length == 1) _projector._slots.Remove(_owner.Id);
                else
                {
                    var next = new ProjectionSubscription[current.Length - 1];
                    Array.Copy(current, 0, next, 0, index);
                    Array.Copy(current, index + 1, next, index, next.Length - index);
                    _projector._slots[_owner.Id] = next;
                }
            }
            _projector.UnregisterLedger(this);
        }
    }
}
