using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Feeds.Bybit;

/// <summary>
/// One immutable credential context. Owns order identities, exact reconciliation and actual executions across broker views.
/// REST snapshots are polling snapshots, not an atomic stream or an exchange-native OCO guarantee.
/// </summary>
public sealed class BybitTradingSession : IDisposable, IAsyncDisposable
{
    private static long _nextExecutionRevision;
    private static readonly AccountExecutionSnapshot UnknownExecutions = new(Array.Empty<AccountExecution>(), false);
    private static readonly AccountExecutionSnapshot EmptyExecutions = new(Array.Empty<AccountExecution>(), true);
    private readonly BybitAccountClient _client;
    private readonly HttpClient? _ownedHttp;
    private readonly string? _statePath;
    private readonly string? _executionRoot;
    private readonly Func<string, decimal, decimal, bool, string?>? _sendGuard;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Tracked> _byLink = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tracked> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<Order, Tracked> _byOrder = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, BybitPosition> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BybitExecution> _executions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Symbol, string OrderId), OrderExecutions> _executionTotals = new();
    private readonly Dictionary<string, string> _settlements = new(StringComparer.Ordinal);
    private IReadOnlyList<BybitClosedPnl> _closedPnl = Array.Empty<BybitClosedPnl>();
    private IReadOnlyList<BybitExecution>? _executionSnapshot;
    private readonly HashSet<string> _publishedExecutions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BybitExecution> _reportedExecutions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Instrument> _instruments = new(StringComparer.Ordinal);
    private readonly ExecutionLog _executionSource = new(ExecutionAvailability.Synchronizing);
    private readonly HashSet<string> _reversing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlattenIntent> _flattening = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _operations = new();
    private FileStream? _lease;
    private Task? _poll;
    private Task? _disposeTask;
    private bool _ownershipVerified;
    private bool _validated;
    private bool _identityMismatch;
    private string? _persistentAccountIdentity;
    private bool _retired;
    private bool _disposed;
    private bool? _shutdownRecoveryRequired;
    private long _mutationVersion;
    private long _executionRevision = Interlocked.Increment(ref _nextExecutionRevision);
    private string? _storageError;
    private DateTime _executionWatermark;
    private int _terminalReviewCursor;

    private sealed class Tracked
    {
        public required Order Order;
        public required string Symbol;
        public required string LinkId;
        public bool Own;
        public bool SubmissionUnconfirmed;
        public bool Sending;
        public bool CancelRequested;
        public bool CancelSent;
        public bool OperationBusy;
        public Amendment? Amendment;
        public Amendment? QueuedAmendment;
        public Func<string?>? AmendmentViewGuard;
        public decimal Executed;
        public decimal ExecutionQuantity;
        public decimal AverageFill;
        public long UpdatedTimeMs;
        public DateTime LastSeen;
        public DateTime? ExecutionScanCursor;
    }
    private sealed class OrderExecutions
    {
        public decimal Quantity;
        public decimal Value;
        public decimal ClosedQuantity;
        public readonly Dictionary<string, BybitExecution> ById = new(StringComparer.Ordinal);
        public IReadOnlyList<AccountExecution>? Facts;
        public AccountExecutionSnapshot? CompleteSnapshot;
        public AccountExecutionSnapshot? PartialSnapshot;
    }
    /// <param name="Conditional">The field the requested move changes, fixed when requested: triggerPrice (true) or price (false). Null only in state saved before this was recorded.</param>
    private sealed record Amendment(decimal Price, decimal Total, bool Sent = false, string? OwnershipIntentId = null, bool? Conditional = null);
    private sealed record FlattenIntent(Instrument Instrument, string CloseLinkId);
    private sealed record SavedOrder(string Symbol, string LinkId, bool Own, string OrderId, OrderDirections Direction, OrderTypes Type,
        decimal Quantity, decimal Executed, decimal Price, decimal Trigger, bool ReduceOnly, OrderStates State, OrderStatus Status,
        DateTime Time, string Comment, bool SubmissionUnconfirmed, bool CancelRequested, bool CancelSent, Amendment? Amendment,
        Amendment? QueuedAmendment, long UpdatedTimeMs, decimal AverageFill, DateTime LastSeen, DateTime? ExecutionScanCursor = null,
        bool NativeQuantityOnly = false);
    private sealed record SavedState(string ContextIdentity, List<SavedOrder> Orders, List<BybitExecution> Executions, DateTime ExecutionWatermark,
        string AccountIdentity, bool RecoveryRequired = true, List<FlattenIntent>? FlattenIntents = null);
    private sealed record Envelope(string Payload, string Sha256);

    /// <param name="statePath">Provider-owned durable native state. Orders require this path or an explicit execution root; a missing path never grants ephemeral native dispatch.</param>
    /// <param name="sendGuard">Rechecked at dispatch, including queued amendments; native reductions and cancels remain possible.</param>
    public BybitTradingSession(BybitAccountSettings settings, string? statePath = null, HttpClient? httpClient = null,
        Func<string, decimal, decimal, bool, string?>? sendGuard = null, TimeProvider? clock = null, bool ownsHttpClient = false,
        string? executionRoot = null)
    {
        Settings = settings with { };
        ContextIdentity = settings.ContextIdentity;
        _clock = clock ?? TimeProvider.System;
        _client = new BybitAccountClient(Settings, httpClient, _clock);
        _ownedHttp = ownsHttpClient ? httpClient : null;
        _statePath = statePath;
        _sendGuard = sendGuard;
        ExecutionCoverageStart = _clock.GetUtcNow().UtcDateTime.AddDays(-7).AddSeconds(1);
        _executionWatermark = ExecutionCoverageStart;
        string? root = executionRoot ?? (statePath == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statePath))!, "Execution"));
        _executionRoot = root == null ? null : Path.GetFullPath(root);
        // Neither a credential fingerprint nor a saved UID grants provisional native ownership.
        OrderContext = new(new("unverified:bybit:" + ContextIdentity, "Bybit", "linear-uta", Settings.EnvironmentName,
            ContextIdentity, ContextIdentity, OwnershipNamespace: "bybit:" + ContextIdentity), executions: _executionSource);
        Load();
    }

    public BybitAccountSettings Settings { get; }
    public bool IsReal => Settings.IsReal;
    public string ContextIdentity { get; }
    public OrderContext OrderContext { get; }
    public IExecutionSource ExecutionSource => _executionSource;
    internal void RegisterInstrument(Instrument instrument)
    {
        lock (_gate)
        {
            bool changed = !_instruments.TryGetValue(instrument.Symbol, out var prior) ||
                prior.Key != instrument.Key || prior.PointValue != instrument.PointValue;
            _instruments[instrument.Symbol] = instrument;
            if (changed)
                foreach (var history in _executionTotals)
                    if (history.Key.Symbol == instrument.Symbol)
                        history.Value.CompleteSnapshot = history.Value.PartialSnapshot = null;
        }
        PublishExecutionFacts();
    }

    private void PublishExecutionFacts()
    {
        var reports = new List<ExecutionReport>();
        var quantities = new List<OwnedOrderQuantity>();
        ExecutionAvailability availability;
        lock (_gate)
        {
            if (!_validated) return;
            foreach (var tracked in _byLink.Values)
            {
                if (!_instruments.TryGetValue(tracked.Symbol, out var instrument) || tracked.Order.ExchangeOrderId.Length == 0) continue;
                var order = tracked.Order;
                order.Security.SecurityId = instrument.Key;
                var native = new NativeOrderIdentity("bybit:order", order.ExchangeOrderId);
                if (order.ContextId != null && order.ContextId != OrderContext.Descriptor.Id)
                    throw new InvalidDataException("A verified Bybit native handle belongs to another account context.");
                order.ContextId = OrderContext.Descriptor.Id;
                order.NativeIdentity = native;
                order.ClientOrderId = tracked.LinkId;
                if (order.IntentId == null)
                {
                    // Recover against the type the durable claim froze: a stop claim without working evidence for this
                    // native order was dispatched as a stop, even if Bybit has since triggered it (with or without a kept
                    // triggerPrice); one with that evidence recovers as its working type. The working type is recorded below.
                    var working = order.Type;
                    if (OrderContext.FindNativeSubmission(tracked.LinkId, native) is { Request.Type: OrderTypes.Stop } claim &&
                        !(claim.WorkingOrder is { } evidence && evidence.Child == native &&
                            evidence.NativeInstrument == instrument.Symbol && evidence.NativeExchange == instrument.Exchange))
                        order.Type = OrderTypes.Stop;
                    try { OrderContext.RecoverNativeOrder(order, tracked.LinkId, native, instrument.Key, instrument.Symbol, instrument.Exchange, notify: false); }
                    finally { order.Type = working; }
                }
                if (order.IntentId == null) continue;
                if (OrderContext.BindNative(order, native, notify: false) is { } bindingError) { LastError = bindingError; continue; }
                if (OrderContext.Find(order.IntentId)?.Request.Type == OrderTypes.Stop && order.Type == OrderTypes.Market &&
                    OrderContext.RecordNativeWorkingType(order, native, native, OrderTypes.Market, instrument.Key, instrument.Symbol, instrument.Exchange, notify: false) is { } workingError)
                {
                    LastError = workingError;
                    OrderContext.ReportProjectionFault(workingError);
                }
                if (OrderContext.CaptureQuantityObservation(order, instrument.Symbol, instrument.PointValue,
                    tracked.SubmissionUnconfirmed ? null : tracked.Executed) is { } quantity) quantities.Add(quantity);
            }
            foreach (var pair in _executions)
            {
                var fill = pair.Value;
                if (!_instruments.TryGetValue(fill.Symbol, out var instrument) ||
                    (_reportedExecutions.TryGetValue(pair.Key, out var prior) && prior == fill)) continue;
                _byId.TryGetValue(fill.OrderId, out var tracked);
                if (tracked?.Order.IntentId == null) continue;
                if (OrderContext.Find(tracked.Order.IntentId) is not { } ownership) continue;
                reports.Add(ExecutionFact(fill, tracked, instrument, ownership));
                _reportedExecutions[pair.Key] = fill;
            }
            availability = ExecutionsReady && OrdersReady ? ExecutionAvailability.Ready : ExecutionAvailability.Synchronizing;
            _executionSource.PublishBatch(reports, notify: false);
            _executionSource.SetProducerAvailability(this, availability, notify: false);
        }
        // Provider and ownership callbacks may reenter the account: never dispatch them under _gate.
        foreach (var quantity in quantities) OrderContext.NotifyQuantityChanged(quantity);
        OrderContext.NotifyOwnershipChanged();
        _executionSource.NotifyChanged();
    }

    private ExecutionReport ExecutionFact(BybitExecution fill, Tracked tracked, Instrument instrument, OwnedOrderIntent? ownership)
    {
        var native = tracked.Order.NativeIdentity is { } known && known.Id == fill.OrderId
            ? known : new NativeOrderIdentity("bybit:order", fill.OrderId);
        var fees = fill.Fee.HasValue ? new[] { new ExecutionFee(fill.Fee.Value, fill.FeeCurrency) } : Array.Empty<ExecutionFee>();
        return new(new(new ExecutionScope("Bybit", _persistentAccountIdentity!, Settings.EnvironmentName, instrument.Key)
            { ContextId = OrderContext.Descriptor.Id, Product = OrderContext.Descriptor.Product },
            "bybit:" + AccountIdentity + ":" + fill.Symbol + ":" + fill.OrderId + ":" + fill.Id),
            tracked.Order.Id, fill.Symbol, fill.Time, fill.Buy, fill.Quantity, fill.Price, instrument.PointValue, "USDT",
            fees, fill.Fee.HasValue)
        {
            ContextId = OrderContext.Descriptor.Id, IntentId = ownership?.Id,
            ClientOrderId = tracked.LinkId, NativeOrder = native,
            AccountingOrder = ownership == null ? null
                : new(native, (OrderContext.Find(ownership.LogicalOrderId) ?? ownership).QuantityBaseline),
        };
    }
    public string AccountIdentity { get; private set; } = "";
    /// <summary>Verified provider/environment/origin/UID scope; a saved UID alone never authorizes persistent account state.</summary>
    public string? PersistentAccountIdentity { get { lock (_gate) return _validated ? _persistentAccountIdentity : null; } }
    public bool PositionReady { get; private set; }
    public bool OrdersReady { get; private set; }
    public bool ExecutionsReady { get; private set; }
    public long ExecutionRevision => Volatile.Read(ref _executionRevision);
    public bool ClosedPnlReady { get; private set; }
    public IReadOnlyList<BybitClosedPnl> ClosedPnl { get { lock (_gate) return _closedPnl; } }
    public bool SnapshotReady => PositionReady && OrdersReady && ExecutionsReady;
    public bool IsRetired { get { lock (_gate) return _retired; } }
    public string? LastError { get; private set; }
    public string StatusText => LastError ?? (SnapshotReady ? $"Bybit UTA ({Settings.EnvironmentName}), one-way; polling snapshots" : "Bybit account state is unknown; reconciling");
    public BybitWallet? Wallet { get; private set; }
    public DateTime ExecutionCoverageStart { get; }
    public event Action? Changed;
    public event Action<BybitExecution>? FillReceived;
    public IReadOnlyList<BybitPosition> Positions { get { lock (_gate) return _positions.Values.ToArray(); } }
    public IReadOnlyList<Order> Orders { get { lock (_gate) return _byLink.Values.Select(t => t.Order).ToArray(); } }
    public IReadOnlyList<BybitExecution> Executions => GetExecutionState().Executions;
    public (IReadOnlyList<BybitExecution> Executions, long Revision) GetExecutionState()
    {
        lock (_gate)
        {
            if (_executionSnapshot == null)
            {
                var facts = _executions.Values.ToArray();
                Array.Sort(facts, static (left, right) =>
                {
                    int comparison = left.Time.CompareTo(right.Time);
                    if (comparison == 0) comparison = StringComparer.Ordinal.Compare(left.Symbol, right.Symbol);
                    if (comparison == 0) comparison = StringComparer.Ordinal.Compare(left.OrderId, right.OrderId);
                    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Id, right.Id);
                });
                _executionSnapshot = Array.AsReadOnly(facts);
            }
            return (_executionSnapshot, _executionRevision);
        }
    }
    internal bool HasUnreportedDailyClose(IReadOnlyDictionary<(string Symbol, string OrderId), decimal>? reported, TimeZoneInfo timeZone, DateTime localDate)
    {
        lock (_gate)
        {
            foreach (var fill in _executions.Values)
                if (fill.ClosedQuantity != 0 &&
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fill.Time, DateTimeKind.Utc), timeZone).Date == localDate &&
                    (reported == null || !reported.TryGetValue((fill.Symbol, fill.OrderId), out decimal reportedQuantity) ||
                        _executionTotals[(fill.Symbol, fill.OrderId)].ClosedQuantity > reportedQuantity)) return true;
            return false;
        }
    }
    public bool HasOpenPositionsOrOrders
    {
        get
        {
            lock (_gate) return _refreshGate.CurrentCount == 0 || _operations.Count > 0 || _flattening.Count > 0 || !SnapshotReady ||
                _positions.Values.Any(p => p.Size != 0) ||
                _byLink.Values.Any(t => t.Order.State == OrderStates.Active || t.Sending || t.OperationBusy) ||
                _executions.Keys.Any(key => !_publishedExecutions.Contains(key));
        }
    }
    public BybitPosition? PositionFor(string symbol) { lock (_gate) return _positions.GetValueOrDefault(symbol); }
    public IReadOnlyList<Order> OrdersFor(string symbol) { lock (_gate) return _byLink.Values.Where(t => t.Symbol == symbol).Select(t => t.Order).ToArray(); }
    public decimal AverageFillFor(Order order) { lock (_gate) return Find(order)?.AverageFill ?? 0; }
    public bool IsReplacementPending(Order order) { lock (_gate) return Find(order) is { } t && (t.Amendment != null || t.QueuedAmendment != null); }
    public bool IsSubmissionUnknown(Order order) { lock (_gate) return Find(order)?.SubmissionUnconfirmed ?? false; }
    public string? StableOrderIdentityOf(Order order)
    {
        lock (_gate)
        {
            var tracked = Find(order);
            return tracked != null && !tracked.LinkId.StartsWith("external:", StringComparison.Ordinal) && AccountIdentity.Length > 0
                ? "bybit:" + AccountIdentity + ":" + tracked.LinkId : null;
        }
    }
    /// <summary>Immutable native observations for this exact tracked handle; cumulative quantity alone is not an execution.</summary>
    public AccountExecutionSnapshot GetExecutions(Order order)
    {
        lock (_gate)
        {
            if (Find(order) is not { } tracked) return UnknownExecutions;
            decimal expected = Math.Max(tracked.Executed, order.Filled);
            // A tracked intent rejected definitively before any exchange identity/fill exists has a known empty outcome.
            // Ambiguous submissions and partial fills must still reconcile native execution history.
            if (tracked.Own && order.State == OrderStates.Failed && order.Status == OrderStatus.Rejected &&
                !tracked.SubmissionUnconfirmed && !tracked.Sending && order.ExchangeOrderId.Length == 0 &&
                expected == 0 && tracked.ExecutionQuantity == 0) return EmptyExecutions;
            if (AccountIdentity.Length == 0) return UnknownExecutions;
            bool complete = _validated && !tracked.SubmissionUnconfirmed && !tracked.Sending &&
                order.ExchangeOrderId.Length > 0;
            if (!_executionTotals.TryGetValue((tracked.Symbol, order.ExchangeOrderId), out var history))
                return complete && expected == 0 ? EmptyExecutions : UnknownExecutions;
            complete &= history.Quantity == expected;
            if (complete && history.CompleteSnapshot != null) return history.CompleteSnapshot;
            if (!complete && history.PartialSnapshot != null) return history.PartialSnapshot;
            if (history.Facts == null)
            {
                var facts = new AccountExecution[history.ById.Count];
                int index = 0;
                foreach (var fill in history.ById.Values)
                    facts[index++] = new AccountExecution("bybit:" + AccountIdentity + ":" + fill.Symbol + ":" + fill.OrderId + ":" + fill.Id,
                        fill.Quantity, fill.Price, fill.Time, fill.Fee, fill.FeeCurrency, fill.HasQuoteFee ? fill.Fee : null);
                Array.Sort(facts, static (left, right) =>
                {
                    int time = left.TimeUtc.CompareTo(right.TimeUtc);
                    return time != 0 ? time : StringComparer.Ordinal.Compare(left.Id, right.Id);
                });
                history.Facts = Array.AsReadOnly(facts);
            }
            IReadOnlyList<ExecutionReport>? executionFacts = null;
            if (_validated && _instruments.TryGetValue(tracked.Symbol, out var instrument))
            {
                var typed = new ExecutionReport[history.ById.Count];
                int index = 0;
                foreach (var fill in history.ById.Values) typed[index++] = ExecutionFact(fill, tracked, instrument, ownership: null);
                executionFacts = Array.AsReadOnly(typed);
            }
            var snapshot = new AccountExecutionSnapshot(history.Facts, complete) { ExecutionFacts = executionFacts };
            if (complete) history.CompleteSnapshot = snapshot;
            else history.PartialSnapshot = snapshot;
            return snapshot;
        }
    }
    public async Task<Instrument> GetInstrumentAsync(string symbol, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _refreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await _client.GetInstrumentAsync(symbol, linked.Token).ConfigureAwait(false); }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Persist the close identity before canceling protection; readiness gaps never discard the flatten intent.</summary>
    public void Flatten(Instrument instrument)
    {
        lock (_gate)
        {
            if (_disposed) { LastError = "Bybit account is closing; flatten was not queued."; return; }
            if (!_flattening.ContainsKey(instrument.Symbol))
            {
                _flattening.Add(instrument.Symbol, new FlattenIntent(instrument, "th-" + Guid.NewGuid().ToString("N")));
                if (!Save()) { _flattening.Remove(instrument.Symbol); return; }
            }
        }
        DispatchDeferred(); Wake(); Changed?.Invoke();
    }

    public void Reverse(Instrument instrument, decimal lastPrice, Func<string?>? viewGuard = null)
    {
        lock (_gate)
        {
            if (!PositionReady || !OrdersReady) { LastError = "Bybit position or orders are unknown; reverse was not sent."; return; }
            if (!_reversing.Add(instrument.Symbol)) return;
            RunOperation(async () =>
            {
                try
                {
                    CancelAll(instrument.Symbol);
                    await WaitForAsync(() => PositionReady && OrdersReady && !OrdersFor(instrument.Symbol).Any(o => o.State == OrderStates.Active), _cts.Token).ConfigureAwait(false);
                    decimal net;
                    lock (_gate) net = _positions.GetValueOrDefault(instrument.Symbol)?.Size ?? 0;
                    if (net != 0) Place(instrument, lastPrice, net > 0 ? OrderDirections.Sell : OrderDirections.Buy,
                        OrderTypes.Market, Math.Abs(net) * 2, 0, "Reverse", viewGuard: viewGuard);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
                finally { lock (_gate) _reversing.Remove(instrument.Symbol); Changed?.Invoke(); }
            });
        }
    }
    private async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (true)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged() { if (condition()) ready.TrySetResult(); }
            Changed += OnChanged;
            try { if (condition()) return; await ready.Task.WaitAsync(ct).ConfigureAwait(false); }
            finally { Changed -= OnChanged; }
            if (condition()) return;
        }
    }
    private static string ExecutionKey(BybitExecution fill) => fill.Symbol + "|" + fill.OrderId + "|" + fill.Id;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _poll ??= Task.Run(PollAsync);
        }
    }
    public void Retire()
    {
        lock (_gate) _retired = true; // Existing context keeps reconciling and protecting its own exposure.
        Changed?.Invoke();
    }
    /// <summary>Reuse this exact immutable context after it is explicitly selected again; retired broker views stay retired.</summary>
    public void Reactivate()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _retired = false; }
        Changed?.Invoke();
    }
    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }
    private async Task PollAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await RefreshAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                lock (_gate) { PositionReady = OrdersReady = ExecutionsReady = false; LastError = ex.Message; }
                Changed?.Invoke();
            }
            try { await _wake.WaitAsync(Settings.PollInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One complete reconciliation pass; serial with all other refreshes. Fakes may drive it without starting polling.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        ct = linked.Token;
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
                if (_identityMismatch) throw new InvalidDataException("Bybit authenticated UID changed for a saved credential context.");
            if (!_validated) await _client.ValidateAccountAsync(ct).ConfigureAwait(false);
            string identity = await _client.GetAccountIdentityAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (AccountIdentity.Length > 0 && AccountIdentity != identity)
                {
                    _identityMismatch = true;
                    _validated = false;
                    LastError = "Bybit authenticated UID changed for a saved credential context.";
                    throw new InvalidDataException(LastError);
                }
                AccountIdentity = identity;
                _persistentAccountIdentity ??= "bybit:" + identity + ":" + new Uri(Settings.RestBase).GetLeftPart(UriPartial.Authority);
                if (!_ownershipVerified)
                {
                    OrderContext.Configure(OrderContext.Descriptor with
                    {
                        Id = _persistentAccountIdentity,
                        VerifiedNativeAccount = identity,
                    }, _executionRoot);
                    _ownershipVerified = true;
                }
                _validated = true;
            }
            long version;
            lock (_gate) version = _mutationVersion;
            var open = await _client.GetOpenOrdersAsync(ct).ConfigureAwait(false);
            string? unresolved = null;
            string? lateReviewError = null;
            List<Tracked> missing;
            lock (_gate)
            {
                var seen = new HashSet<Tracked>(ReferenceEqualityComparer.Instance);
                foreach (var report in open)
                {
                    var tracked = Bind(report);
                    Apply(tracked, report);
                    seen.Add(tracked);
                }
                missing = _byLink.Values.Where(t => !seen.Contains(t) && !t.Sending &&
                    (t.Order.State == OrderStates.Active || t.SubmissionUnconfirmed || t.Amendment != null || t.CancelRequested)).ToList();
                // Revisit one recently terminal owned identity per pass: late executions can arrive outside discovery overlap.
                var terminal = _byLink.Values.Where(t => t.Own && t.Order.State is OrderStates.Done or OrderStates.Failed &&
                    t.Order.ExchangeOrderId.Length > 0 && t.LastSeen >= _clock.GetUtcNow().UtcDateTime.AddDays(-7)).ToArray();
                if (terminal.Length > 0)
                {
                    _terminalReviewCursor %= terminal.Length;
                    missing.Add(terminal[_terminalReviewCursor]);
                    _terminalReviewCursor = (_terminalReviewCursor + 1) % terminal.Length;
                }
            }
            foreach (var tracked in missing)
            {
                BybitOpenOrder? report;
                try { report = await _client.GetOrderAsync(tracked.Symbol, tracked.Order.ExchangeOrderId, tracked.LinkId, ct).ConfigureAwait(false); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (tracked.Order.State == OrderStates.Active) unresolved = ex.Message; else lateReviewError = ex.Message;
                    continue;
                }
                lock (_gate)
                {
                    if (report != null) Apply(tracked, report);
                    else if (tracked.Order.State == OrderStates.Active) unresolved = "Bybit order outcome is unknown; exact client/order lookup has not resolved it. No cancellation was inferred.";
                    else lateReviewError = "Bybit terminal-order late-fill history is unavailable; retained executions are not complete financial coverage.";
                }
            }

            DateTime now = _clock.GetUtcNow().UtcDateTime;
            DateTime from;
            lock (_gate) from = _executionWatermark.AddMinutes(-5);
            if (from < now.AddDays(-7).AddSeconds(1)) from = now.AddDays(-7).AddSeconds(1);
            var fills = await _client.GetExecutionsAsync(from, now, ct).ConfigureAwait(false);
            List<Tracked> executionGaps;
            lock (_gate)
            {
                foreach (var fill in fills) MergeExecution(fill);
                executionGaps = _byLink.Values.Where(tracked => tracked.Executed >
                    (_executionTotals.GetValueOrDefault((tracked.Symbol, tracked.Order.ExchangeOrderId))?.Quantity ?? 0)).ToList();
            }
            string? executionError = lateReviewError;
            foreach (var tracked in executionGaps)
            {
                DateTime scanFrom = tracked.ExecutionScanCursor ?? tracked.Order.Time;
                if (scanFrom > now) scanFrom = now.AddMinutes(-5);
                DateTime scanTo = scanFrom < now.AddDays(-7) ? scanFrom.AddDays(7).AddMilliseconds(-1) : now;
                try
                {
                    var exact = await _client.GetExecutionsAsync(scanFrom, scanTo, ct, tracked.Order.ExchangeOrderId, tracked.LinkId).ConfigureAwait(false);
                    lock (_gate)
                    {
                        foreach (var fill in exact) MergeExecution(fill);
                        // One bounded seven-day interval per gap/pass; old orders are not silently restricted to the rolling discovery window.
                        tracked.ExecutionScanCursor = scanTo < now ? scanTo.AddMilliseconds(1) : now.AddMinutes(-5);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { executionError = "Bybit exact-order execution coverage is incomplete: " + ex.Message; }
            }
            var positions = await _client.GetPositionsAsync(ct).ConfigureAwait(false);
            var wallet = await _client.GetWalletAsync(ct).ConfigureAwait(false);
            IReadOnlyList<BybitClosedPnl>? closed = null;
            string? closedError = null;
            try
            {
                foreach (var position in positions) _settlements[position.Symbol] = "USDT";
                foreach (var fill in fills) _settlements[fill.Symbol] = "USDT";
                var reported = await _client.GetClosedPnlAsync(now.AddDays(-7).AddSeconds(1), now, ct).ConfigureAwait(false);
                var usdt = new List<BybitClosedPnl>(reported.Count);
                foreach (var record in reported)
                {
                    if (!_settlements.TryGetValue(record.Symbol, out string? settlement))
                        _settlements[record.Symbol] = settlement = await _client.GetSettlementAsync(record.Symbol, ct).ConfigureAwait(false);
                    if (settlement == "USDT") usdt.Add(record);
                }
                closed = usdt;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { closedError = "Bybit reported closed PnL is unavailable: " + ex.Message; }
            if (closed != null)
            {
                foreach (var record in closed)
                {
                    lock (_gate)
                        if (record.ClosedQuantity <= (_executionTotals.GetValueOrDefault((record.Symbol, record.OrderId))?.ClosedQuantity ?? 0)) continue;
                    try
                    {
                        var exact = await _client.GetExecutionsAsync(now.AddDays(-7).AddSeconds(1), now, ct, record.OrderId).ConfigureAwait(false);
                        lock (_gate) foreach (var fill in exact) MergeExecution(fill);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { executionError = "Bybit closed-order execution coverage is incomplete: " + ex.Message; }
                }
            }
            List<BybitExecution> publish;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                bool executionGap = false;
                if (closed != null)
                    foreach (var record in closed)
                        if (record.ClosedQuantity > (_executionTotals.GetValueOrDefault((record.Symbol, record.OrderId))?.ClosedQuantity ?? 0)) executionGap = true;
                publish = _executions.Where(pair => !_publishedExecutions.Contains(pair.Key)).Select(pair => pair.Value)
                    .OrderBy(fill => fill.Time).ThenBy(fill => fill.OrderId, StringComparer.Ordinal).ThenBy(fill => fill.Id, StringComparer.Ordinal).ToList();
                foreach (var tracked in _byLink.Values)
                {
                    var total = _executionTotals.GetValueOrDefault((tracked.Symbol, tracked.Order.ExchangeOrderId));
                    decimal quantity = total?.Quantity ?? 0;
                    if (tracked.Executed > quantity) executionGap = true;
                    tracked.ExecutionQuantity = quantity;
                    // Financial history covers prices and fees only. Native cumulative/state come exclusively from order reports.
                    if (total is { Quantity: > 0 } && total.Quantity >= tracked.Executed) tracked.AverageFill = total.Value / total.Quantity;
                }
                _positions.Clear();
                foreach (var position in positions) _positions[position.Symbol] = position;
                Wallet = wallet;
                PositionReady = version == _mutationVersion;
                ExecutionsReady = !executionGap && executionError == null;
                ClosedPnlReady = closed != null;
                if (closed != null) _closedPnl = closed;
                _executionWatermark = now;
                OrdersReady = unresolved == null && !_byLink.Values.Any(t => t.SubmissionUnconfirmed || t.Sending || t.CancelRequested || t.Amendment != null);
                LastError = _storageError ?? unresolved ?? executionError ??
                    (executionGap ? "Bybit reported fills exceed available actual execution identities; financial results remain unknown while exact-order history reconciles." : closedError);
                if (!PositionReady) LastError ??= "Bybit position snapshot overlapped an order request; refreshing before new exposure.";
                Save();
            }
            PublishExecutionFacts();
            foreach (var fill in publish)
            {
                var receiver = FillReceived;
                if (receiver == null) continue;
                try
                {
                    receiver(fill);
                    lock (_gate) _publishedExecutions.Add(ExecutionKey(fill));
                }
                catch (Exception ex)
                {
                    lock (_gate) { ExecutionsReady = false; LastError = "Bybit execution delivery is pending after a local history error: " + ex.Message; }
                }
            }
            DispatchDeferred();
            Changed?.Invoke();
        }
        catch
        {
            lock (_gate) { PositionReady = OrdersReady = ExecutionsReady = ClosedPnlReady = false; Save(); }
            _executionSource.SetProducerAvailability(this, ExecutionAvailability.Unavailable);
            throw;
        }
        finally { _refreshGate.Release(); }
    }

    private void MergeExecution(BybitExecution fill)
    {
        string key = ExecutionKey(fill);
        _executions.TryGetValue(key, out var prior);
        if (prior != null)
        {
            if (prior.Quantity != fill.Quantity || prior.Price != fill.Price || prior.Buy != fill.Buy || prior.Time != fill.Time)
                throw new InvalidDataException("Bybit changed an execution's quantity, price, direction or timestamp under the same identity; financial reconciliation is required.");
            if (prior.ClosedQuantity.HasValue && fill.ClosedQuantity.HasValue && prior.ClosedQuantity != fill.ClosedQuantity)
                throw new InvalidDataException("Bybit changed an execution's closing quantity under the same identity; financial reconciliation is required.");
            bool differentCurrency = fill.FeeCurrency.Length > 0 && prior.FeeCurrency.Length > 0 && fill.FeeCurrency != prior.FeeCurrency;
            if ((!fill.Fee.HasValue && prior.Fee.HasValue && !differentCurrency) || (fill.FeeCurrency.Length == 0 && prior.FeeCurrency.Length > 0) ||
                (!fill.ClosedQuantity.HasValue && prior.ClosedQuantity.HasValue))
                fill = fill with { Fee = fill.Fee ?? (differentCurrency ? null : prior.Fee),
                    FeeCurrency = fill.FeeCurrency.Length == 0 ? prior.FeeCurrency : fill.FeeCurrency, ClosedQuantity = fill.ClosedQuantity ?? prior.ClosedQuantity };
        }
        if (prior == null || prior.Fee != fill.Fee || prior.FeeCurrency != fill.FeeCurrency || prior.ClosedQuantity != fill.ClosedQuantity)
        {
            var identity = (fill.Symbol, fill.OrderId);
            if (!_executionTotals.TryGetValue(identity, out var total))
                _executionTotals.Add(identity, total = new OrderExecutions());
            if (prior == null)
            {
                total.Quantity += fill.Quantity;
                total.Value += fill.Quantity * fill.Price;
            }
            total.ClosedQuantity += (fill.ClosedQuantity ?? 0) - (prior?.ClosedQuantity ?? 0);
            total.ById[fill.Id] = fill;
            if (prior == null || prior.Fee != fill.Fee || prior.FeeCurrency != fill.FeeCurrency)
            {
                total.Facts = null;
                total.CompleteSnapshot = total.PartialSnapshot = null;
            }
            _executions[key] = fill;
            _publishedExecutions.Remove(key);
            _executionRevision = Interlocked.Increment(ref _nextExecutionRevision);
            _executionSnapshot = null;
        }
        Tracked? owner = null;
        if (fill.LinkId.Length > 0) _byLink.TryGetValue(fill.LinkId, out owner);
        if (owner == null) _byId.TryGetValue(fill.OrderId, out owner);
        if (owner != null && owner.Symbol == fill.Symbol)
        {
            if (owner.Order.ExchangeOrderId.Length > 0 && owner.Order.ExchangeOrderId != fill.OrderId)
                throw new InvalidDataException("Bybit execution conflicts with the owned client identity.");
            owner.Order.ExchangeOrderId = fill.OrderId;
            _byId[fill.OrderId] = owner;
        }
    }

    private Tracked Bind(BybitOpenOrder report)
    {
        if (_byId.TryGetValue(report.OrderId, out var tracked)) return tracked;
        if (report.LinkId.Length > 0 && _byLink.TryGetValue(report.LinkId, out tracked))
        {
            if (tracked.Symbol != report.Symbol ||
                (tracked.Order.ExchangeOrderId.Length > 0 && tracked.Order.ExchangeOrderId != report.OrderId))
                throw new InvalidDataException("Bybit returned multiple exchange identities for one client order; the previous tracked identity was retained.");
            tracked.Order.ExchangeOrderId = report.OrderId;
            _byId[report.OrderId] = tracked;
            return tracked;
        }
        string link = report.LinkId.Length > 0 ? report.LinkId : "external:" + report.OrderId;
        tracked = new Tracked { Order = new Order { Comment = "Bybit external/recovered order" }, Symbol = report.Symbol, LinkId = link,
            LastSeen = _clock.GetUtcNow().UtcDateTime };
        _byLink[link] = tracked;
        _byOrder.Add(tracked.Order, tracked);
        _byId[report.OrderId] = tracked;
        return tracked;
    }
    private void Apply(Tracked tracked, BybitOpenOrder report)
    {
        var order = tracked.Order;
        if (tracked.Symbol != report.Symbol || (order.ExchangeOrderId.Length > 0 && order.ExchangeOrderId != report.OrderId))
            throw new InvalidDataException("Bybit order report does not match its tracked identity.");
        bool stale = report.UpdatedTimeMs > 0 && report.UpdatedTimeMs < tracked.UpdatedTimeMs;
        bool terminal = order.State is OrderStates.Done or OrderStates.Failed;
        bool newReport = !terminal || report.UpdatedTimeMs > tracked.UpdatedTimeMs || report.Executed > tracked.Executed;
        tracked.Executed = Math.Max(tracked.Executed, report.Executed);
        order.QuantityToFill = Math.Max(order.QuantityToFill, tracked.Executed);
        order.Unfilled = Math.Max(0, order.QuantityToFill - tracked.Executed);
        if (stale || (terminal && !report.Terminal)) return;
        order.ExchangeOrderId = report.OrderId;
        _byId[report.OrderId] = tracked;
        tracked.SubmissionUnconfirmed = false;
        tracked.UpdatedTimeMs = Math.Max(tracked.UpdatedTimeMs, report.UpdatedTimeMs);
        if (newReport) tracked.LastSeen = _clock.GetUtcNow().UtcDateTime;
        order.Security.Code = report.Symbol;
        order.Direction = report.Buy ? OrderDirections.Buy : OrderDirections.Sell;
        // A triggered conditional keeps its order id and triggerPrice and works as its orderType. Only Untriggered and
        // Deactivated (canceled before the trigger) reports, or an unexecuted cancel/reject of a waiting stop, remain a stop.
        bool waiting = report.Conditional && (report.Status is "Untriggered" or "Deactivated" ||
            (report.Terminal && report.Executed == 0 && order.Type == OrderTypes.Stop));
        order.Type = waiting ? OrderTypes.Stop : report.IsLimit ? OrderTypes.Limit : OrderTypes.Market;
        order.Price = report.Price;
        order.TriggerPrice = report.Conditional ? report.TriggerPrice : report.Price;
        order.QuantityToFill = Math.Max(report.Quantity, tracked.Executed);
        order.Unfilled = Math.Max(0, order.QuantityToFill - tracked.Executed);
        order.ReduceOnly = report.ReduceOnly;
        if (report.CreatedTimeMs > 0) order.Time = DateTimeOffset.FromUnixTimeMilliseconds(report.CreatedTimeMs).UtcDateTime;
        if (report.AvgPrice > 0) tracked.AverageFill = report.AvgPrice;
        if (report.Terminal)
        {
            order.State = report.Status == "Rejected" ? OrderStates.Failed : OrderStates.Done;
            order.Status = report.Status switch { "Filled" => OrderStatus.Filled, "Rejected" => OrderStatus.Rejected, _ => OrderStatus.Canceled };
            if (report.Status == "Filled") order.Unfilled = 0;
            tracked.CancelRequested = tracked.CancelSent = false;
            tracked.Amendment = tracked.QueuedAmendment = null;
        }
        else
        {
            order.State = OrderStates.Active;
            order.Status = tracked.Executed > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed;
            if (tracked.Amendment is { Sent: true } amendment)
            {
                bool triggerAmendment = amendment.Conditional ?? order.Type == OrderTypes.Stop;
                // Only a matching authoritative working report confirms a native amendment. A trigger-price amendment
                // overtaken by the trigger has no trigger left to confirm: this report already states what Bybit works.
                if ((triggerAmendment && order.Type != OrderTypes.Stop) ||
                    (report.Quantity == amendment.Total && (triggerAmendment ? report.TriggerPrice : report.Price) == amendment.Price))
                    tracked.Amendment = null;
            }
        }
    }

    private Tracked? Find(Order order) => _byOrder.GetValueOrDefault(order);
    private string? Check(string symbol, decimal signedQuantity, bool reduceOnly)
    {
        if (_disposed) return "Bybit account is closing; the order was not sent.";
        if (!_validated || AccountIdentity.Length == 0) return "Bybit authenticated account identity is unknown; wait for account validation before submitting an order.";
        if (reduceOnly) return null;
        if (_retired) return "Bybit credentials were replaced; this context may only cancel or send native reducing exits.";
        if (_storageError != null) return _storageError;
        if (_flattening.ContainsKey(symbol)) return "A Bybit flatten is pending; new exposure is blocked until its tracked close reconciles.";
        if (!PositionReady || !OrdersReady) return "Bybit position or working orders are unknown; new exposure is blocked until reconciliation completes.";
        decimal position = _positions.GetValueOrDefault(symbol)?.Size ?? 0;
        return _sendGuard?.Invoke(symbol, position, signedQuantity, false);
    }
    private string? AtTransport(string symbol, decimal signedQuantity, bool reduceOnly, Func<string?>? viewGuard)
    {
        lock (_gate)
        {
            if (_disposed) return "Bybit account is closing; no mutation was sent.";
            if (!_validated) return "Bybit authenticated account identity is unknown; wait for account validation before submitting an order.";
            if (reduceOnly) return null;
            if (_retired) return "Bybit credential context was retired before transport; no new exposure was sent.";
            if (_storageError != null) return _storageError;
            if (_flattening.ContainsKey(symbol)) return "A Bybit flatten is pending; new exposure is blocked until its tracked close reconciles.";
            if (viewGuard?.Invoke() is { } viewError) return viewError;
            return _sendGuard?.Invoke(symbol, _positions.GetValueOrDefault(symbol)?.Size ?? 0, signedQuantity, false);
        }
    }
    private static Order Rejected(Instrument instrument, OrderDirections direction, OrderTypes type, decimal quantity, decimal price, string comment, bool reduceOnly) =>
        new() { Security = new Security { Code = instrument.Symbol, SecurityId = instrument.Key }, Direction = direction, Type = type,
            QuantityToFill = quantity, Unfilled = quantity, Price = price, TriggerPrice = price, Comment = comment, ReduceOnly = reduceOnly,
            State = OrderStates.Failed, Status = OrderStatus.Rejected };

    public Order Place(Instrument instrument, decimal lastPrice, OrderDirections direction, OrderTypes type, decimal quantity, decimal price, string comment = "", bool reduceOnly = false, Func<string?>? viewGuard = null) =>
        PlaceCore(instrument, lastPrice, direction, type, quantity, price, comment, reduceOnly, viewGuard, null);

    internal Order Submit(Instrument instrument, decimal lastPrice, PreparedOrderIntent intent, Func<string?>? viewGuard)
    {
        var request = intent.Request;
        return PlaceCore(instrument, lastPrice, request.Direction, request.Type, request.Quantity, request.Price,
            request.Comment, request.ReduceOnly, viewGuard, null, intent);
    }

    private Order PlaceCore(Instrument instrument, decimal lastPrice, OrderDirections direction, OrderTypes type, decimal quantity, decimal price,
        string comment, bool reduceOnly, Func<string?>? viewGuard, string? reservedLinkId, PreparedOrderIntent? prepared = null)
    {
        decimal level = type == OrderTypes.Market ? 0 : instrument.RoundPrice(price);
        var order = Rejected(instrument, direction, type, quantity, level, comment, reduceOnly);
        order.Time = _clock.GetUtcNow().UtcDateTime;
        prepared ??= OrderContext.Prepare(OrderContext.ManualOwner, new(instrument.Key, direction, type, quantity, level, reduceOnly, comment),
            clientOrderId: reservedLinkId);
        string? ownershipError = prepared.Request.Instrument != instrument.Key ? "The prepared Bybit order routes to another instrument."
            : OrderContext.BindOrder(order, prepared);
        if (ownershipError != null) { LastError = ownershipError; return order; }
        Tracked tracked;
        lock (_gate)
        {
            _instruments[instrument.Symbol] = instrument;
            string? error = quantity <= 0 || (instrument.VolumeStep > 0 && quantity % instrument.VolumeStep != 0)
                ? "Bybit quantity must be positive and match the instrument quantity step."
                : type is not (OrderTypes.Market or OrderTypes.Limit or OrderTypes.Stop) ? "Unsupported Bybit order type."
                : type != OrderTypes.Market && level <= 0 ? "Bybit order price must be positive."
                : type == OrderTypes.Stop && (lastPrice <= 0 || lastPrice == level) ? "A current venue last price is required for the Bybit stop trigger direction."
                : Check(instrument.Symbol, direction == OrderDirections.Buy ? quantity : -quantity, reduceOnly);
            if (error != null)
            {
                if (!prepared.Attempted) OrderContext.RecordRejected(prepared, error);
                LastError = error;
                return order;
            }
            error = OrderContext.StartDispatch(prepared, instrument.Symbol, instrument.Exchange);
            if (error != null) { LastError = error; return order; }
            string linkId = prepared.ClientOrderId;
            order.State = OrderStates.Active;
            order.Status = OrderStatus.None;
            tracked = new Tracked { Order = order, Symbol = instrument.Symbol, LinkId = linkId, Own = true, SubmissionUnconfirmed = true,
                Sending = true, LastSeen = _clock.GetUtcNow().UtcDateTime };
            _byLink.Add(linkId, tracked);
            _byOrder.Add(order, tracked);
            if (!Save() && (!reduceOnly || reservedLinkId != null))
            {
                order.State = OrderStates.Failed; order.Status = OrderStatus.Rejected; tracked.SubmissionUnconfirmed = tracked.Sending = false;
                OrderContext.RecordRejected(prepared, _storageError ?? "The native state could not be committed before transport.");
                return order;
            }
            OrdersReady = false;
            RunOperation(async () =>
            {
                try
                {
                    lock (_gate)
                    {
                        string? permission = OrderContext.CheckDispatch(prepared) ??
                            AtTransport(instrument.Symbol, direction == OrderDirections.Buy ? quantity : -quantity, reduceOnly, viewGuard);
                        if (permission != null) throw new BybitApiException(-1, permission);
                        _mutationVersion++;
                        PositionReady = false;
                    }
                    string id = await _client.PlaceOrderAsync(instrument.Symbol, direction, type, quantity, level, reduceOnly, linkId, lastPrice, _cts.Token,
                        () => OrderContext.CheckDispatch(prepared) ?? AtTransport(instrument.Symbol, direction == OrderDirections.Buy ? quantity : -quantity, reduceOnly, viewGuard)).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (order.ExchangeOrderId.Length > 0 && order.ExchangeOrderId != id) throw new InvalidDataException("Bybit acknowledged a different tracked order.");
                        order.ExchangeOrderId = id;
                        order.NativeIdentity = new("bybit:order", id);
                        if (OrderContext.BindNative(order, order.NativeIdentity, notify: false) is { } bindingError) LastError = bindingError;
                        _byId[id] = tracked;
                        // Keep submission unconfirmed until an authoritative order report arrives.
                    }
                }
                catch (BybitApiException ex)
                {
                    lock (_gate)
                    {
                        if (order.ExchangeOrderId.Length == 0 && order.Status == OrderStatus.None)
                        {
                            order.State = OrderStates.Failed; order.Status = OrderStatus.Rejected; tracked.SubmissionUnconfirmed = false;
                            tracked.CancelRequested = tracked.CancelSent = false;
                            tracked.Amendment = tracked.QueuedAmendment = null;
                            tracked.AmendmentViewGuard = null;
                            OrderContext.RecordRejected(prepared, ex.Message);
                        }
                        LastError = ex.Message;
                    }
                }
                catch (Exception ex) { lock (_gate) LastError = "Bybit placement outcome remains tracked and unknown: " + ex.Message; }
                finally
                {
                    lock (_gate) { tracked.Sending = false; Save(); }
                    OrderContext.NotifyOwnershipChanged();
                    DispatchDeferred(); Changed?.Invoke(); Wake();
                }
            });
        }
        if (reservedLinkId == null) Changed?.Invoke();
        return order;
    }

    public void Cancel(Order order)
    {
        lock (_gate)
        {
            if (Find(order) is not { } tracked || order.State != OrderStates.Active) return;
            tracked.CancelRequested = true;
            tracked.QueuedAmendment = null;
            if (!tracked.OperationBusy) tracked.CancelSent = false; // An explicit user retry is allowed; polling never blindly retries a mutation.
            Save();
        }
        DispatchDeferred(); Wake(); Changed?.Invoke();
    }
    public Order Replace(Order order, decimal newPrice, decimal newQuantity, Instrument instrument, Func<string?>? viewGuard = null) =>
        Replace(order, OrderContext.PrepareReplacement(order, newPrice, newQuantity), instrument, viewGuard);

    internal Order Replace(Order order, PreparedOrderIntent intent, Instrument instrument, Func<string?>? viewGuard = null)
    {
        decimal newPrice = intent.Request.Price;
        decimal newQuantity = intent.IsPriceOnlyReplacement ? order.Unfilled : intent.Request.Quantity;
        string? ownershipError = OrderContext.Validate(intent, order);
        if (intent.Request.Instrument != instrument.Key) ownershipError = "The replacement routes to another Bybit instrument.";
        if (ownershipError != null) { LastError = ownershipError; return order; }
        lock (_gate)
        {
            if (Find(order) is not { } tracked || order.State != OrderStates.Active) return order;
            if (newQuantity <= 0 || (instrument.VolumeStep > 0 && newQuantity % instrument.VolumeStep != 0))
            { LastError = "Bybit replacement quantity must be positive and match the quantity step."; return order; }
            decimal price = instrument.RoundPrice(newPrice);
            if (price <= 0 || order.Type == OrderTypes.Market) { LastError = "Bybit market orders cannot be amended; a positive working price is required."; return order; }
            ownershipError = OrderContext.BindOrder(order, intent) ?? OrderContext.StartDispatch(intent, instrument.Symbol, instrument.Exchange);
            if (ownershipError == null && order.NativeIdentity != null)
                ownershipError = OrderContext.BindNative(order, order.NativeIdentity, notify: false);
            if (ownershipError != null) { LastError = ownershipError; return order; }
            // The field is the one the requester moved: a waiting stop's trigger or a working price, fixed now.
            tracked.QueuedAmendment = new(price, order.Filled + newQuantity, OwnershipIntentId: intent.Id, Conditional: order.Type == OrderTypes.Stop);
            tracked.AmendmentViewGuard = viewGuard;
            Save();
        }
        OrderContext.NotifyOwnershipChanged();
        DispatchDeferred(); Wake(); Changed?.Invoke();
        return order;
    }
    public void CancelAll(string symbol)
    {
        foreach (var order in OrdersFor(symbol).Where(o => o.State == OrderStates.Active)) Cancel(order);
    }

    private void DispatchDeferred()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_flattening.Count > 0)
            {
                bool changed = false;
                foreach (var tracked in _byLink.Values)
                {
                    if (tracked.Order.State != OrderStates.Active || tracked.CancelRequested ||
                        !_flattening.TryGetValue(tracked.Symbol, out var flatten) || tracked.LinkId == flatten.CloseLinkId) continue;
                    tracked.CancelRequested = true;
                    tracked.QueuedAmendment = null;
                    if (!tracked.OperationBusy) tracked.CancelSent = false;
                    changed = true;
                }
                if (changed) Save();
            }
            foreach (var tracked in _byLink.Values)
            {
                if (tracked.Sending || tracked.OperationBusy || tracked.Order.State != OrderStates.Active || tracked.Order.ExchangeOrderId.Length == 0 || tracked.SubmissionUnconfirmed) continue;
                if (tracked.CancelRequested && !tracked.CancelSent)
                {
                    tracked.CancelSent = true; tracked.OperationBusy = true;
                    Save();
                    RunOperation(async () =>
                    {
                        try
                        {
                            lock (_gate) { _mutationVersion++; PositionReady = false; }
                            await _client.CancelOrderAsync(tracked.Symbol, tracked.Order.ExchangeOrderId, tracked.LinkId, _cts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) { lock (_gate) LastError = "Bybit cancel is not confirmed: " + ex.Message; }
                        finally
                        {
                            lock (_gate) { tracked.OperationBusy = false; Save(); }
                            DispatchDeferred(); Changed?.Invoke(); Wake();
                        }
                    });
                    continue;
                }
                if (tracked.CancelRequested || tracked.Amendment != null || tracked.QueuedAmendment == null) continue;
                var requested = tracked.QueuedAmendment;
                tracked.QueuedAmendment = null;
                var amendmentGuard = tracked.AmendmentViewGuard;
                var ownership = OrderContext.Find(requested.OwnershipIntentId);
                var ticket = ownership == null ? null : new PreparedOrderIntent(OrderContext, ownership) { BoundOrder = tracked.Order };
                bool conditional = requested.Conditional ?? tracked.Order.Type == OrderTypes.Stop;
                if (conditional != (tracked.Order.Type == OrderTypes.Stop))
                {
                    // The order triggered (or otherwise changed type) since this move was requested: sending it now would
                    // move a different field, e.g. a resting stop-limit price to the intended trigger. Drop it unsent.
                    LastError = "Bybit amendment ownership is unavailable; request the amendment again after reconciliation.";
                    Save(); continue;
                }
                if (requested.Total <= tracked.Executed)
                {
                    tracked.CancelRequested = true; tracked.CancelSent = false; Wake(); continue;
                }
                decimal signed = (tracked.Order.Direction == OrderDirections.Buy ? 1 : -1) * Math.Max(0, requested.Total - tracked.Executed);
                string? denied = ticket == null ? "Bybit amendment ownership is unavailable; request the amendment again after reconciliation."
                    : OrderContext.CheckDispatch(ticket, Math.Abs(signed)) ?? Check(tracked.Symbol, signed, tracked.Order.ReduceOnly);
                if (denied != null) { LastError = denied; Save(); continue; }
                tracked.Amendment = requested with { Sent = true, Conditional = conditional };
                tracked.OperationBusy = true;
                if (!Save() && !tracked.Order.ReduceOnly) { tracked.Amendment = null; tracked.OperationBusy = false; continue; }
                RunOperation(async () =>
                {
                    try
                    {
                        lock (_gate)
                        {
                            string? permission = OrderContext.CheckDispatch(ticket!, Math.Abs(signed)) ??
                                AtTransport(tracked.Symbol, signed, tracked.Order.ReduceOnly, amendmentGuard);
                            if (permission != null) throw new BybitApiException(-1, permission);
                            _mutationVersion++; PositionReady = false;
                        }
                        await _client.AmendOrderAsync(tracked.Symbol, tracked.Order.ExchangeOrderId, requested.Total, requested.Price, conditional, _cts.Token,
                            () => OrderContext.CheckDispatch(ticket!, Math.Max(0, requested.Total - tracked.Executed)) ??
                                AtTransport(tracked.Symbol, (tracked.Order.Direction == OrderDirections.Buy ? 1 : -1) *
                                Math.Max(0, requested.Total - tracked.Executed), tracked.Order.ReduceOnly, amendmentGuard)).ConfigureAwait(false);
                    }
                    catch (BybitApiException ex) { lock (_gate) { tracked.Amendment = null; LastError = ex.Message; } }
                    catch (Exception ex) { lock (_gate) LastError = "Bybit amendment outcome is unknown; retaining its stable handle: " + ex.Message; }
                    finally
                    {
                        lock (_gate) { tracked.OperationBusy = false; Save(); }
                        Changed?.Invoke(); Wake();
                    }
                });
            }
            DispatchFlattening();
        }
    }
    private void DispatchFlattening()
    {
        if (!PositionReady || !OrdersReady || _flattening.Count == 0) return;
        List<string>? complete = null;
        foreach (var intent in _flattening.Values)
        {
            string symbol = intent.Instrument.Symbol;
            if (_byLink.Values.Any(tracked => tracked.Symbol == symbol && tracked.LinkId != intent.CloseLinkId &&
                (tracked.Order.State == OrderStates.Active || tracked.Sending || tracked.OperationBusy || tracked.SubmissionUnconfirmed))) continue;
            decimal position = _positions.GetValueOrDefault(symbol)?.Size ?? 0;
            if (_byLink.TryGetValue(intent.CloseLinkId, out var close))
            {
                if (close.Order.State == OrderStates.Active || close.Sending || close.OperationBusy || close.SubmissionUnconfirmed) continue;
                if (position == 0) (complete ??= new()).Add(symbol);
                else LastError = close.Order.Status == OrderStatus.Rejected
                    ? "Bybit flatten close was rejected; exposure remains tracked and requires review."
                    : "Bybit flatten close has finished but the position is not confirmed flat; exposure remains tracked and requires review.";
            }
            else if (position == 0) (complete ??= new()).Add(symbol);
            else
            {
                PlaceCore(intent.Instrument, 0, position > 0 ? OrderDirections.Sell : OrderDirections.Buy,
                    OrderTypes.Market, Math.Abs(position), 0, "Close", true, null, intent.CloseLinkId);
                break; // A fresh account snapshot is required before another symbol can advance.
            }
        }
        if (complete == null) return;
        foreach (string symbol in complete) _flattening.Remove(symbol);
        Save();
    }
    private void RunOperation(Func<Task> action)
    {
        var task = Task.Run(action);
        _operations.Add(task);
        _ = task.ContinueWith(done => { lock (_gate) _operations.Remove(done); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static SavedState ReadState(string path)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty Bybit state.");
        string checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload)));
        if (checksum != envelope.Sha256) throw new InvalidDataException("Bybit state checksum mismatch.");
        return JsonSerializer.Deserialize<SavedState>(envelope.Payload) ?? throw new InvalidDataException("Empty Bybit state payload.");
    }
    public static bool RequiresRecovery(string statePath) => ReadState(statePath).RecoveryRequired;

    private void Load()
    {
        if (_statePath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_statePath))!);
            _lease = new FileStream(_statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!File.Exists(_statePath)) return;
            var state = ReadState(_statePath);
            if (state.ContextIdentity != ContextIdentity) throw new InvalidDataException("Bybit state belongs to another credential/environment context.");
            AccountIdentity = state.AccountIdentity ?? "";
            foreach (var saved in state.Orders)
            {
                // Old KVP counters could include financial-history quantities. Only a current native report can establish their order state.
                bool replayNative = !saved.NativeQuantityOnly && saved.OrderId.Length > 0;
                decimal nativeExecuted = saved.NativeQuantityOnly ? saved.Executed : 0;
                var order = new Order { Security = new Security { Code = saved.Symbol }, ExchangeOrderId = saved.OrderId, Direction = saved.Direction,
                    Type = saved.Type, QuantityToFill = saved.Quantity, Unfilled = Math.Max(0, saved.Quantity - nativeExecuted), Price = saved.Price,
                    TriggerPrice = saved.Trigger, ReduceOnly = saved.ReduceOnly, State = replayNative ? OrderStates.Active : saved.State,
                    Status = replayNative ? OrderStatus.None : saved.Status, Time = saved.Time, Comment = saved.Comment };
                var tracked = new Tracked { Order = order, Symbol = saved.Symbol, LinkId = saved.LinkId, Own = saved.Own,
                    SubmissionUnconfirmed = replayNative || saved.SubmissionUnconfirmed || saved.State == OrderStates.Active, CancelRequested = saved.CancelRequested,
                    CancelSent = saved.CancelSent, Amendment = saved.Amendment, QueuedAmendment = saved.QueuedAmendment,
                    Executed = nativeExecuted, AverageFill = saved.AverageFill, UpdatedTimeMs = replayNative ? 0 : saved.UpdatedTimeMs, LastSeen = saved.LastSeen,
                    ExecutionScanCursor = saved.ExecutionScanCursor };
                _byLink.Add(saved.LinkId, tracked);
                _byOrder.Add(order, tracked);
                if (saved.OrderId.Length > 0) _byId.Add(saved.OrderId, tracked);
            }
            if (state.FlattenIntents != null)
                foreach (var intent in state.FlattenIntents) _flattening.Add(intent.Instrument.Symbol, intent);
            foreach (var execution in state.Executions) MergeExecution(execution);
            _executionWatermark = state.ExecutionWatermark;
        }
        catch (Exception ex)
        {
            _storageError = LastError = "Bybit durable order state is unavailable; new exposure is blocked: " + ex.Message;
        }
    }
    private bool Save()
    {
        if (_statePath == null) return true;
        if (_lease == null || _storageError != null) return false;
        string temporary = _statePath + ".pending";
        try
        {
            var orders = _byLink.Values.Select(t => new SavedOrder(t.Symbol, t.LinkId, t.Own, t.Order.ExchangeOrderId, t.Order.Direction,
                t.Order.Type, t.Order.QuantityToFill, t.Executed, t.Order.Price, t.Order.TriggerPrice, t.Order.ReduceOnly, t.Order.State,
                t.Order.Status, t.Order.Time, t.Order.Comment, t.SubmissionUnconfirmed, t.CancelRequested, t.CancelSent,
                t.Amendment, t.QueuedAmendment, t.UpdatedTimeMs, t.AverageFill, t.LastSeen, t.ExecutionScanCursor, NativeQuantityOnly: true)).ToList();
            string payload = JsonSerializer.Serialize(new SavedState(ContextIdentity, orders, _executions.Values.ToList(), _executionWatermark,
                AccountIdentity, _shutdownRecoveryRequired ?? (!SnapshotReady || _flattening.Count > 0 || _positions.Values.Any(p => p.Size != 0) ||
                    _byLink.Values.Any(t => t.Order.State == OrderStates.Active || t.Sending || t.OperationBusy) ||
                    _executions.Keys.Any(key => !_publishedExecutions.Contains(key))), _flattening.Values.ToList()));
            string data = JsonSerializer.Serialize(new Envelope(payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))));
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(data);
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, _statePath, true);
            return true;
        }
        catch (Exception ex)
        {
            OrdersReady = false;
            _storageError = LastError = "Bybit durable order state could not be saved; new exposure is blocked: " + ex.Message;
            return false;
        }
    }

    /// <summary>Atomically stop only a confirmed idle context. Its state lease is published/released before a new identical context can open.</summary>
    public bool TryBeginIdleShutdown(out Task completion)
    {
        lock (_gate)
        {
            if (HasOpenPositionsOrOrders || _disposed) { completion = Task.CompletedTask; return false; }
            _shutdownRecoveryRequired = false;
            if (!Save()) { _shutdownRecoveryRequired = null; completion = Task.CompletedTask; return false; }
            _disposed = true;
            _cts.Cancel();
            _lease?.Dispose();
            _lease = null;
            completion = _disposeTask ??= DisposeCoreAsync();
            return true;
        }
    }

    public void Dispose() { lock (_gate) _disposeTask ??= DisposeCoreAsync(); }
    public ValueTask DisposeAsync() { lock (_gate) return new ValueTask(_disposeTask ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        Task? poll;
        Task[] operations;
        lock (_gate)
        {
            _shutdownRecoveryRequired ??= HasOpenPositionsOrOrders;
            _disposed = true;
            _cts.Cancel();
            PositionReady = OrdersReady = ExecutionsReady = false;
            poll = _poll;
            operations = _operations.ToArray();
        }
        try
        {
            if (poll != null) await poll.ConfigureAwait(false);
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _refreshGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (_gate) { Save(); _lease?.Dispose(); _lease = null; }
                _client.Dispose(); _ownedHttp?.Dispose();
            }
            finally { _refreshGate.Release(); }
        }
    }
}
