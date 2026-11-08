using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<(string Path, Guid Generation), WeakReference<ProjectionSubscription>[]> Ledgers = new();
    private readonly record struct CommittedFinancialFact(string Path, Guid Generation, LiveFill Fill);
    private static readonly ConcurrentQueue<CommittedFinancialFact> CommittedFinancialFacts = new();
    private static int _financialDrainScheduled, _financialDraining;
    // Counts queued and running projection work so storage owners can wait for it before removing ledgers.
    private static int _backgroundWork;
    internal static bool BackgroundWorkIdle => Volatile.Read(ref _backgroundWork) == 0;
    private readonly OrderContext _context;
    private readonly WeakReference<AccountExecutionProjector> _weakSelf;
    private volatile string? _root;
    private readonly object _slotsGate = new();
    private readonly Dictionary<string, ProjectionSubscription[]> _slots = new(StringComparer.Ordinal);
    private readonly List<ProjectionSubscription> _notifications = new();
    private readonly Queue<WeakReference<ProjectionSubscription>[]> _foreignNotifications = new();
    private int _foreignDrainScheduled, _synchronizationScheduled;
    private int _availability = (int)ExecutionAvailability.Unavailable;
    public ExecutionAvailability Availability => (ExecutionAvailability)Volatile.Read(ref _availability);
    private ExecutionCursor _cursor;
    private int _processing, _requested;

    private AccountExecutionProjector(IExecutionSource source, OrderContext context)
    {
        _source = source; _context = context; _root = context.StorageRoot;
        _weakSelf = new(this);
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
            if (!ReferenceEquals(projector._context, context))
                throw new InvalidOperationException("An execution source cannot move between ownership contexts.");
            projector.BindStorageRoot();
            return projector;
        }
    }

    private void BindStorageRoot()
    {
        string? root = _context.StorageRoot;
        if (_root == root) return;
        lock (_slotsGate)
        {
            if (_root == root) return;
            if (_root != null || root == null)
                throw new InvalidOperationException("An execution source cannot change its frozen ownership root.");
            foreach (var subscriptions in _slots.Values)
                foreach (var slot in subscriptions)
                    if (slot._owner.LedgerKey != null)
                        throw new InvalidOperationException("A durable owner cannot precede its frozen ownership root.");
            // A cached, unverified account may bind once before any durable owner or receipt exists.
            _root = root;
        }
    }

    /// <summary>Replays run-local facts for the same nonpersistent owner after its runner is replaced.
    /// No durable projection or native cumulative state is changed.</summary>
    internal void ReplayLocalOwner(OrderOwner owner, IReadOnlyList<Order> orders, string symbol, decimal pointValue)
    {
        if (owner.LedgerKey != null) throw new InvalidOperationException("Persisted owners use their durable ledger replay.");
        foreach (var order in orders)
        {
            var intent = _context.Find(order.IntentId);
            if (intent?.Owner != owner || order.OwnerId != owner.Id || order.ContextId != _context.Descriptor.Id ||
                order.LogicalOrderId != intent.LogicalOrderId) continue;
            ProjectQuantity(new(intent, order.Time, order.Direction == OrderDirections.Buy,
                Math.Max(0, order.Filled - intent.QuantityBaseline), symbol, pointValue));
        }
        foreach (var report in _source.Capture(ExecutionCursor.Start).Changes)
        {
            var intent = _context.Resolve(report);
            if (intent?.Owner != owner) continue;
            var update = new OwnedExecutionUpdate(intent, report, ToFill(intent, report));
            foreach (var slot in Subscriptions(owner.Id)) slot.Enqueue(update);
        }
    }
    public ProjectionSubscription Subscribe(OrderOwner owner, Action changed)
    {
        BindStorageRoot();
        if (owner.LedgerKey != null && _root == null)
            throw new InvalidOperationException("A durable owner requires its captured explicit ownership root.");
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
            var key = (LiveStore.LedgerFile(_root, slot._owner.LedgerKey), slot._owner.LedgerGeneration);
            var current = Ledgers.TryGetValue(key, out var slots) ? slots : Array.Empty<WeakReference<ProjectionSubscription>>();
            Ledgers[key] = current.Where(static weak => weak.TryGetTarget(out var target) && !target.Disposed)
                .Append(new WeakReference<ProjectionSubscription>(slot)).ToArray();
            // Publish before checking storage: a concurrent delete either fences this visible slot
            // or finishes first and is observed by this cold, exact-generation check.
            try
            {
                if (!LiveStore.IsLedgerGenerationCurrent(_root, slot._owner.LedgerKey, slot._owner.LedgerGeneration) &&
                    slot.MarkRetired()) ScheduleSynchronization();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                slot.MarkRetired();
                _context.ReportProjectionFault("Could not validate ledger owner registration: " + ex.Message);
                ScheduleSynchronization();
            }
        }
    }

    private void UnregisterLedger(ProjectionSubscription slot)
    {
        if (_root == null || slot._owner.LedgerKey == null) return;
        lock (LedgersGate)
        {
            var key = (LiveStore.LedgerFile(_root, slot._owner.LedgerKey), slot._owner.LedgerGeneration);
            if (!Ledgers.TryGetValue(key, out var current)) return;
            var next = current.Where(weak => weak.TryGetTarget(out var target) &&
                !ReferenceEquals(target, slot) && !target.Disposed).ToArray();
            if (next.Length == 0) Ledgers.TryRemove(key, out _);
            else Ledgers[key] = next;
        }
    }

    // Store-side retirement cannot take LedgersGate (admission already uses registry -> store).
    // This existing weak registry supports concurrent reads; only data flags and weak work are published.
    internal static void RetireLedgerObservers(string path)
    {
        path = Path.GetFullPath(path);
        foreach (var entry in Ledgers)
        {
            if (entry.Key.Path != path) continue;
            foreach (var weak in entry.Value)
                if (weak.TryGetTarget(out var slot) && slot.MarkRetired())
                    slot._projector.ScheduleSynchronization();
        }
    }

    // Store recovery may complete under a reader or quantity writer rather than this source's projection.
    // This entrypoint is data-only: StoreGate/lease callers never enter LedgersGate or invoke a subscriber.
    internal static void QueueCommittedFinancialFact(string path, Guid generation, LiveFill fill)
    {
        CommittedFinancialFacts.Enqueue(new(Path.GetFullPath(path), generation, fill));
        ScheduleFinancialNotifications();
    }

    private static void ScheduleFinancialNotifications()
    {
        if (Interlocked.CompareExchange(ref _financialDrainScheduled, 1, 0) != 0) return;
        Interlocked.Increment(ref _backgroundWork);
        ThreadPool.UnsafeQueueUserWorkItem(static _ =>
        {
            try { DrainCommittedFinancialFacts(); }
            finally
            {
                Volatile.Write(ref _financialDrainScheduled, 0);
                if (!CommittedFinancialFacts.IsEmpty && Volatile.Read(ref _financialDraining) == 0)
                    ScheduleFinancialNotifications();
                Interlocked.Decrement(ref _backgroundWork);
            }
        }, 0, preferLocal: false);
    }

    // Caller owns LedgersGate. Distribution never invokes a provider or subscription callback.
    private static bool DistributeNextFinancialFact(out WeakReference<ProjectionSubscription>[] slots)
    {
        slots = Array.Empty<WeakReference<ProjectionSubscription>>();
        if (!CommittedFinancialFacts.TryDequeue(out var fact)) return false;
        if (Ledgers.TryGetValue((fact.Path, fact.Generation), out var observers))
        {
            slots = observers;
            foreach (var weak in slots)
                if (weak.TryGetTarget(out var slot)) slot.EnqueueCommitted(fact.Fill);
        }
        return true;
    }

    internal static bool HasQueuedFinancialFactUnderResetFence(string path, Guid generation)
    {
        if (!Monitor.IsEntered(LedgersGate))
            throw new InvalidOperationException("Ledger retirement requires its ownership admission fence.");
        path = Path.GetFullPath(path);
        foreach (var fact in CommittedFinancialFacts)
            if (fact.Path == path && fact.Generation == generation) return true;
        return false;
    }

    private static void DrainCommittedFinancialFacts()
    {
        if (Monitor.IsEntered(LedgersGate) || Interlocked.CompareExchange(ref _financialDraining, 1, 0) != 0) return;
        try
        {
            while (true)
            {
                WeakReference<ProjectionSubscription>[] slots;
                lock (LedgersGate)
                    if (!DistributeNextFinancialFact(out slots)) return;
                foreach (var weak in slots)
                    if (weak.TryGetTarget(out var slot) && !slot.Disposed) slot._projector.Synchronize();
            }
        }
        finally
        {
            Volatile.Write(ref _financialDraining, 0);
            if (!CommittedFinancialFacts.IsEmpty) ScheduleFinancialNotifications();
        }
    }

    private void ScheduleSynchronization()
    {
        if (Interlocked.CompareExchange(ref _synchronizationScheduled, 1, 0) != 0) return;
        Interlocked.Increment(ref _backgroundWork);
        ThreadPool.UnsafeQueueUserWorkItem(static weak =>
        {
            try
            {
                if (!weak.TryGetTarget(out var projector)) return;
                Volatile.Write(ref projector._synchronizationScheduled, 0);
                projector.Synchronize();
            }
            finally { Interlocked.Decrement(ref _backgroundWork); }
        }, _weakSelf, preferLocal: false);
    }

    internal LiveStore.ExecutionProjectionResult CommitOwnedEvent(OrderOwner owner, LiveFill fill,
        bool executionOnly = false, string? receiptId = null)
    {
        BindStorageRoot();
        if (_root == null || owner.LedgerKey == null)
            throw new InvalidOperationException("A durable fact requires its frozen ledger root and owner.");
        lock (LedgersGate)
        {
            var result = executionOnly
                ? LiveStore.ProjectExecution(_root, owner.LedgerKey, owner.LedgerGeneration, fill,
                    receiptId ?? throw new IOException("Financial projection requires its immutable admitted receipt."))
                : LiveStore.AppendOwnedEvent(_root, owner.LedgerKey, owner.LedgerGeneration, fill);
            // Financial commits publish their actual admitted head centrally, including recovery and
            // superseded callers. Quantity/maintenance facts retain their existing source fanout.
            if (!executionOnly && result.Status == LiveStore.ExecutionProjectionStatus.Applied &&
                Ledgers.TryGetValue((LiveStore.LedgerFile(_root, owner.LedgerKey), owner.LedgerGeneration), out var slots))
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
        if (Monitor.IsEntered(LedgersGate) || Monitor.IsEntered(_slotsGate))
        {
            ScheduleSynchronization();
            return;
        }
        DrainCommittedFinancialFacts();
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
        Interlocked.Increment(ref _backgroundWork);
        ThreadPool.UnsafeQueueUserWorkItem(static weak =>
        {
            try
            {
                if (!weak.TryGetTarget(out var projector)) return;
                try { projector.NotifyCommittedFacts(); }
                finally
                {
                    Volatile.Write(ref projector._foreignDrainScheduled, 0);
                    projector.ScheduleForeignNotifications();
                }
            }
            finally { Interlocked.Decrement(ref _backgroundWork); }
        }, _weakSelf, preferLocal: false);
    }

    // Retirement and source projection must not pass one another. Retained stopped observers are
    // outstanding authority for this generation even when its successor is locally flat.
    internal bool TryResetOwner(OrderOwner owner, ProjectionSubscription? current, decimal pointValue, Action reset)
    {
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return false;
        Interlocked.Increment(ref _backgroundWork);
        try
        {
            var snapshot = _source.Capture(_cursor);
            if (snapshot.Changes.Count != 0) return false;
            lock (LedgersGate)
            {
                // Transfer committed data inside the same reset fence before checking observers.
                // A reader may publish without this gate; the store lease below closes that later gap.
                while (DistributeNextFinancialFact(out var pendingObservers))
                    foreach (var weak in pendingObservers)
                        if (weak.TryGetTarget(out var pendingObserver)) pendingObserver._projector.ScheduleSynchronization();
                if (_context.HasStorageFault || _context.HasUnprojectedQuantity(owner)) return false;
                if (_root != null && owner.LedgerKey != null &&
                    Ledgers.TryGetValue((LiveStore.LedgerFile(_root, owner.LedgerKey), owner.LedgerGeneration), out var observers))
                    foreach (var weak in observers)
                        if (weak.TryGetTarget(out var observer) && !observer.Disposed &&
                            (!ReferenceEquals(observer, current) || observer.HasPendingUpdates)) return false;
                lock (_slotsGate)
                {
                    if (_slots.TryGetValue(owner.Id, out var slots))
                        foreach (var slot in slots)
                            if (!ReferenceEquals(slot, current) || slot.HasPendingUpdates) return false;
                    if (_root != null && owner.LedgerKey != null &&
                        !LiveStore.TryDeleteFlatLedger(_root, owner.LedgerKey, owner.LedgerGeneration, pointValue))
                        return false;
                    reset();
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _context.ReportProjectionFault("Could not retire owned ledger: " + ex.Message);
            return false;
        }
        finally
        {
            Volatile.Write(ref _processing, 0);
            Synchronize();
            Interlocked.Decrement(ref _backgroundWork);
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
        if (Volatile.Read(ref _processing) != 0) return;
        if (Monitor.IsEntered(LedgersGate) || Monitor.IsEntered(_slotsGate))
        {
            ScheduleSynchronization();
            return;
        }
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return;
        Interlocked.Increment(ref _backgroundWork);
        try
        {
            DrainCommittedFinancialFacts();
            BindStorageRoot();
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
            Interlocked.Decrement(ref _backgroundWork);
        }
    }

    private bool Receive(ExecutionReport report, ExecutionCursor cursor, out bool projected)
    {
        ExecutionReceipt? receipt = null;
        bool received = true;
        if (_root != null)
        {
            receipt = ExecutionReceipt.Create(cursor, report);
            try
            {
                // A persisted retry must reuse an existing admission, never obtain a new causal rank.
                if (_context.FindReceipt(receipt.Id) == null && _context.Resolve(report) is { Owner.LedgerKey: not null } intent)
                {
                    var admitted = LiveStore.AdmitExecution(_root, intent.Owner.LedgerKey!, intent.Owner.LedgerGeneration,
                        receipt.Id, ToFill(intent, report));
                    if (admitted.Status == LiveStore.ExecutionProjectionStatus.Error)
                    {
                        received = false;
                        _context.ReportProjectionFault(admitted.Error!);
                    }
                }
                // Failed admission may have published its atomic binding before reporting a sync error.
                // Keep the source cursor held and retry that exact receipt; saving an unadmitted native
                // receipt would make the next attempt skip admission permanently.
                if (received) _context.SaveReceipt(receipt);
            }
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
            var result = CommitOwnedEvent(intent.Owner, fill, executionOnly: true,
                receiptId: receipt?.Id ?? throw new IOException("An owned financial fact has no captured receipt identity."));
            if (result.Revision > 0) fill = fill with { ProjectionRevision = result.Revision };
            if (result.Status == LiveStore.ExecutionProjectionStatus.Superseded)
            {
                if (receipt != null) _context.Acknowledge(receipt);
                return true;
            }
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
                if (result.Revision == 0) return false; // No admitted authority can authorize a local financial overwrite.
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
        BindStorageRoot();
        var intent = observation.Intent;
        var fill = new LiveFill(observation.Time, observation.Buy, 0, 0, observation.Symbol, observation.PointValue)
        {
            Kind = LiveFillKind.QuantityObservation, OrderId = intent.LogicalOrderId,
            CumulativeQuantity = observation.CumulativeQuantity, ContextId = intent.ContextId,
            AccountingOrder = observation.AccountingOrder,
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

    private static LiveFill ToFill(OwnedOrderIntent intent, ExecutionReport report)
    {
        bool imported = intent.ImportedPriorExecutions?.Contains(report.Identity) == true;
        return new(report.Time, report.Buy, report.Quantity, report.Price, report.Symbol, report.PointValue)
        {
            Kind = imported ? LiveFillKind.ImportedExecution : LiveFillKind.Execution,
            OrderId = imported ? null : intent.LogicalOrderId, ExecutionId = report.Identity,
            ImportedExecutionId = imported ? report.Identity.Id : null,
            AccountingOrder = imported ? null : report.AccountingOrder,
            Fees = report.Fees, FeesComplete = report.FeesComplete, Currency = report.AccountingCurrency,
            ExecutionSequence = report.Sequence, Estimated = report.Estimated,
            ContextId = report.ContextId, OwnerId = intent.Owner.Id, OwnerGeneration = intent.Owner.LedgerGeneration,
        };
    }

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
        internal bool MarkRetired()
        {
            lock (_gate)
            {
                if (_disposed || _retired) return false;
                _retired = true;
                _notificationPending = true;
                return true;
            }
        }
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
