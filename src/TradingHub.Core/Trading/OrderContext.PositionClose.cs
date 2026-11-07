namespace TradingHub.Trading;

public enum PositionClosePhase { Reconciling, Cancelling, Dispatching, Working, Unresolved, Completed, NotSent, Rejected }
public sealed record PositionCloseCancellation(string Basket, bool Buy, decimal? Baseline, decimal? Observed, bool Terminal);
public sealed record PositionCloseObservation(string Basket, bool Buy, decimal? Baseline, decimal? Observed, bool Terminal);

internal sealed class PositionCloseAdmissionUnresolvedException(string message) : Exception(message) { }

internal readonly record struct PositionCloseFailure(bool CommittedHere, bool Definite, string Outcome,
    long SettledRevision, string SettledObservationScope);

/// <summary>Local bounded authorization, not a position/fill inclusion watermark or native reduce-only claim.</summary>
public sealed record NativePositionClose(string Account, string NativeAccount, string Symbol, string Exchange,
    decimal ConfirmedNet, Guid ConnectionEpoch, long ConfirmationRevision, PositionClosePhase Phase,
    PositionCloseCancellation[] Cancellations, decimal KnownReductions = 0, bool Invalidated = false,
    string? Outcome = null, long SettledRevision = 0, string? PreviousIntent = null, bool LateFillUnresolved = false,
    string? ObservationScope = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PositionCloseObservation[]? Surveillance = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? SettledObservationScope = null);

public sealed partial class OrderContext
{
    private Dictionary<(string Account, string Symbol, string Exchange), PreparedOrderIntent>? _positionCloses;

    public IReadOnlyList<PreparedOrderIntent> PositionCloses()
    {
        lock (_gate)
        {
            LoadPositionCloses();
            return _positionCloses!.Values.ToArray();
        }
    }

    private void LoadPositionCloses()
    {
        if (_positionCloses != null) return;
        var closes = new Dictionary<(string, string, string), PreparedOrderIntent>();
        foreach (var data in _journal?.PositionCloses() ?? Array.Empty<OwnedOrderIntent>())
        {
            var close = data.PositionClose!;
            var ticket = new PreparedOrderIntent(this, data);
            if (data.DispatchUncertain) ticket.TryAttempt();
            closes.Add((close.Account, close.Symbol, close.Exchange), ticket);
        }
        _positionCloses = closes;
    }

    public PreparedOrderIntent BeginPositionClose(string instrument, NativePositionClose confirmation)
    {
        lock (_gate)
        {
            OwnedOrderIntent? data = null;
            try
            {
                if (instrument.Length == 0 || confirmation.ConfirmedNet == 0)
                    throw new InvalidOperationException("An order needs an owner, instrument and positive quantity.");
                if (_journal == null) throw new IOException("Native order ownership has not been configured with an explicit storage root.");
                data = _journal.ClaimPositionClose(ManualOwner, instrument, confirmation);
                Remember(data);
                LastError = null;
                _storageFault = false;
                var key = (confirmation.Account, confirmation.Symbol, confirmation.Exchange);
                LoadPositionCloses();
                if (_positionCloses!.TryGetValue(key, out var existing) && existing.Id == data.Id) return existing;
                var ticket = new PreparedOrderIntent(this, data);
                if (data.DispatchUncertain) ticket.TryAttempt();
                _positionCloses![key] = ticket;
                return ticket;
            }
            catch (Exception ex)
            {
                _storageFault = ex is IOException or UnauthorizedAccessException;
                return new PreparedOrderIntent(this, data ?? CreatePositionCloseIntent(Descriptor, ManualOwner, instrument, confirmation),
                    LastError = "Could not persist bounded close ownership: " + ex.Message);
            }
        }
    }

    internal static OwnedOrderIntent CreatePositionCloseIntent(AccountContextDescriptor descriptor, OrderOwner owner,
        string instrument, NativePositionClose confirmation)
    {
        string id = Guid.NewGuid().ToString("N");
        return new OwnedOrderIntent(id, id, "th-" + Guid.NewGuid().ToString("N"), owner,
            new OrderRequest(instrument, confirmation.ConfirmedNet > 0 ? OrderDirections.Sell : OrderDirections.Buy,
                OrderTypes.Market, Math.Abs(confirmation.ConfirmedNet), ReduceOnly: true, Comment: "Close", Protective: true),
            descriptor.Id, descriptor.Generation, PositionClose: confirmation);
    }

    public NativePositionClose PositionCloseState(PreparedOrderIntent intent)
    {
        lock (_gate)
        {
            var data = _journal?.ReadIntent(intent.Id) ?? Find(intent.Id) ?? intent.Data;
            Remember(data);
            return data.PositionClose ?? throw new InvalidOperationException("The identity is not a bounded position close.");
        }
    }

    public void UpdatePositionClose(PreparedOrderIntent intent, NativePositionClose state)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(intent.Context, this))
                throw new InvalidOperationException("A bounded close cannot change its captured route or widen its authorization.");
            var data = _journal?.UpdatePositionClose(intent.Id, state) ??
                throw new IOException("The bounded close identity is unavailable.");
            Remember(data);
        }
    }

    internal void FailPositionClose(PreparedOrderIntent intent, PositionCloseFailure failure,
        Func<OwnedOrderIntent, string?> currentCloseAdmission)
    {
        ArgumentNullException.ThrowIfNull(currentCloseAdmission);
        lock (_gate)
        {
            if (!ReferenceEquals(intent.Context, this))
                throw new InvalidOperationException("A bounded close cannot change its captured route or widen its authorization.");
            try
            {
                var data = _journal?.FailPositionClose(intent.Id, failure, currentCloseAdmission) ??
                    throw new IOException("The bounded close identity is unavailable.");
                Remember(data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _storageFault = true;
                LastError = "Could not persist bounded close ownership: " + ex.Message;
                throw;
            }
        }
    }

    internal static void ValidatePositionCloseUpdate(OwnedOrderIntent data, NativePositionClose state)
    {
        var previous = data.PositionClose ?? throw new InvalidOperationException("The identity is not a bounded position close.");
        if (state.Account != previous.Account || state.NativeAccount != previous.NativeAccount ||
            state.Symbol != previous.Symbol || state.Exchange != previous.Exchange || state.ConfirmedNet != previous.ConfirmedNet ||
            state.ConnectionEpoch != previous.ConnectionEpoch || state.ConfirmationRevision != previous.ConfirmationRevision ||
            state.ObservationScope != previous.ObservationScope || state.PreviousIntent != previous.PreviousIntent ||
            data.DispatchUncertain && state.Phase is (PositionClosePhase.Reconciling or PositionClosePhase.Cancelling or PositionClosePhase.NotSent))
            throw new InvalidOperationException("A bounded close cannot change its captured route or widen its authorization.");
    }

    internal IReadOnlyList<(AccountContextDescriptor Descriptor, OwnedOrderIntent Intent)> PreviousPositionCloses()
    {
        lock (_gate) return StorageRoot == null ? Array.Empty<(AccountContextDescriptor, OwnedOrderIntent)>() :
            OrderOwnershipJournal.PreviousPositionCloses(StorageRoot, Descriptor);
    }
}

internal sealed partial class OrderOwnershipJournal
{
    public IReadOnlyList<OwnedOrderIntent> PositionCloses()
    {
        using var lease = Acquire();
        return PositionClosesLeased();
    }

    private IReadOnlyList<OwnedOrderIntent> PositionClosesLeased()
    {
        string directory = Path.Combine(_root, "intent");
        if (!Directory.Exists(directory)) return Array.Empty<OwnedOrderIntent>();
        var closes = new List<OwnedOrderIntent>();
        var superseded = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var data = Read<OwnedOrderIntent>(path) ?? throw new IOException("A close ownership record disappeared during recovery.");
            if (data.ContextId != _descriptor.Id || data.Generation != _descriptor.Generation || IntentPath(data.Id) != path)
                throw new IOException("Corrupt or incorrectly scoped close ownership.");
            if (data.PositionClose is not { } close) continue;
            closes.Add(data);
            if (close.PreviousIntent != null) superseded.Add(close.PreviousIntent);
        }
        var current = closes.Where(close => !superseded.Contains(close.Id)).ToArray();
        if (current.GroupBy(close => (close.PositionClose!.Account, close.PositionClose!.Symbol, close.PositionClose!.Exchange)).Any(group => group.Count() != 1))
            throw new IOException("Competing bounded close identities require reconciliation.");
        return current;
    }

    public OwnedOrderIntent ClaimPositionClose(OrderOwner owner, string instrument, NativePositionClose confirmation)
    {
        using var lease = Acquire();
        var current = PositionClosesLeased().SingleOrDefault(intent => intent.PositionClose is { } close &&
            close.Account == confirmation.Account && close.Symbol == confirmation.Symbol && close.Exchange == confirmation.Exchange);
        if (current?.PositionClose is { } prior)
        {
            var merged = MergePositionCloseFacts(prior, prior, current.DispatchUncertain);
            if (!SamePositionCloseState(prior, merged))
            {
                current = current with { PositionClose = merged };
                SaveIntentLeased(current);
                prior = merged;
            }
            bool staleConfirmation = confirmation.ConnectionEpoch == prior.ConnectionEpoch &&
                (confirmation.ObservationScope == null || confirmation.ObservationScope != prior.ObservationScope ||
                    prior.SettledObservationScope != null && confirmation.ObservationScope != prior.SettledObservationScope ||
                    confirmation.ConfirmationRevision <= prior.ConfirmationRevision);
            bool staleSettlement = prior.Phase == PositionClosePhase.Completed &&
                confirmation.ObservationScope != null && confirmation.ObservationScope == prior.SettledObservationScope &&
                confirmation.ConfirmationRevision <= prior.SettledRevision;
            if (prior.Phase is not (PositionClosePhase.Completed or PositionClosePhase.NotSent or PositionClosePhase.Rejected) ||
                prior.KnownReductions != 0 || staleConfirmation || staleSettlement) return current;
        }
        confirmation = MergePositionCloseFacts(confirmation, confirmation, false);
        var candidate = OrderContext.CreatePositionCloseIntent(_descriptor, owner, instrument,
            confirmation with { PreviousIntent = current?.Id });
        SaveIntentLeased(candidate);
        return candidate;
    }

    public OwnedOrderIntent UpdatePositionClose(string id, NativePositionClose state)
    {
        using var lease = Acquire();
        var data = ReadIntent(id) ?? throw new IOException("The bounded close identity is unavailable.");
        OrderContext.ValidatePositionCloseUpdate(data, state);
        var previous = data.PositionClose!;
        state = MergePositionCloseFacts(previous, state, data.DispatchUncertain);
        if (SamePositionCloseState(previous, state)) return data;
        data = data with { PositionClose = state };
        SaveIntentLeased(data);
        return data;
    }

    public OwnedOrderIntent FailPositionClose(string id, PositionCloseFailure failure,
        Func<OwnedOrderIntent, string?> currentCloseAdmission)
    {
        ArgumentNullException.ThrowIfNull(currentCloseAdmission);
        using var lease = Acquire();
        var current = ReadIntent(id) ?? throw new IOException("The bounded close identity is unavailable.");
        var previous = current.PositionClose ?? throw new IOException("The bounded close identity is unavailable.");
        var close = MergePositionCloseFacts(previous, previous, current.DispatchUncertain);
        if (!ReferenceEquals(close, previous)) current = current with { PositionClose = close };
        if (current.DispatchUncertain && !failure.CommittedHere ||
            close.Phase is PositionClosePhase.Completed or PositionClosePhase.Rejected)
        {
            if (!SamePositionCloseState(previous, close)) SaveIntentLeased(current);
            return current;
        }
        string? unresolved = null;
        try { currentCloseAdmission(current); }
        catch (PositionCloseAdmissionUnresolvedException ex) { unresolved = ex.Message; }
        bool reducingQuantityUnknown = close.Cancellations.Any(fact =>
            fact.Buy == (close.ConfirmedNet < 0) && fact.Observed == null) ||
            (close.Surveillance ?? Array.Empty<PositionCloseObservation>()).Any(fact =>
                fact.Buy == (close.ConfirmedNet < 0) && fact.Observed == null);
        var phase = failure.Definite && unresolved == null && !reducingQuantityUnknown &&
            close.KnownReductions == 0 && !close.LateFillUnresolved
                ? failure.CommittedHere ? PositionClosePhase.Rejected : PositionClosePhase.NotSent
                : PositionClosePhase.Unresolved;
        close = close with
        {
            Phase = phase, Outcome = close.LateFillUnresolved && close.Outcome != null ? close.Outcome : unresolved ?? failure.Outcome,
            SettledRevision = failure.SettledRevision, SettledObservationScope = failure.SettledObservationScope,
        };
        string? rejection = failure.CommittedHere && failure.Definite ? current.Rejection ?? failure.Outcome : current.Rejection;
        if (SamePositionCloseState(previous, close) && current.Rejection == rejection) return current;
        current = current with { PositionClose = close, Rejection = rejection };
        SaveIntentLeased(current);
        return current;
    }

    private static bool SamePositionCloseState(NativePositionClose first, NativePositionClose next) =>
        first.Phase == next.Phase && first.KnownReductions == next.KnownReductions && first.Invalidated == next.Invalidated &&
        first.Outcome == next.Outcome && first.SettledRevision == next.SettledRevision &&
        first.SettledObservationScope == next.SettledObservationScope && first.LateFillUnresolved == next.LateFillUnresolved &&
        first.Cancellations.AsSpan().SequenceEqual(next.Cancellations) &&
        (first.Surveillance ?? Array.Empty<PositionCloseObservation>()).AsSpan()
            .SequenceEqual(next.Surveillance ?? Array.Empty<PositionCloseObservation>());

    private static NativePositionClose MergePositionCloseFacts(NativePositionClose previous, NativePositionClose state, bool dispatched)
    {
        bool sameFacts = previous.Cancellations.AsSpan().SequenceEqual(state.Cancellations) &&
            (previous.Surveillance ?? Array.Empty<PositionCloseObservation>()).AsSpan()
                .SequenceEqual(state.Surveillance ?? Array.Empty<PositionCloseObservation>());
        Dictionary<string, PositionCloseCancellation>? cancellations = null;
        Dictionary<string, PositionCloseObservation>? observations = null;
        void Observe(string basket, bool buy, decimal? baseline, decimal? observed, bool terminal, bool cancellation)
        {
            if (cancellations != null && cancellations.TryGetValue(basket, out var target))
            {
                if (target.Buy != buy) throw new IOException("A bounded close order fact changed side.");
                baseline = MinimumBaseline(target.Baseline, baseline);
                observed = MaximumObservation(target.Observed, observed);
                terminal |= target.Terminal;
                if (target.Baseline != baseline || target.Observed != observed || target.Terminal != terminal)
                    cancellations[basket] = target with { Baseline = baseline, Observed = observed, Terminal = terminal };
            }
            else if (observations != null && observations.TryGetValue(basket, out var watched))
            {
                if (watched.Buy != buy) throw new IOException("A bounded close order fact changed side.");
                baseline = MinimumBaseline(watched.Baseline, baseline);
                observed = MaximumObservation(watched.Observed, observed);
                terminal |= watched.Terminal;
                if (watched.Baseline != baseline || watched.Observed != observed || watched.Terminal != terminal)
                    observations[basket] = watched with { Baseline = baseline, Observed = observed, Terminal = terminal };
            }
            else if (cancellation) (cancellations ??= new(StringComparer.Ordinal)).Add(basket, new(basket, buy, baseline, observed, terminal));
            else (observations ??= new(StringComparer.Ordinal)).Add(basket, new(basket, buy, baseline, observed, terminal));
        }
        foreach (var fact in previous.Cancellations)
            if (!(cancellations ??= new(StringComparer.Ordinal)).TryAdd(fact.Basket, fact))
                Observe(fact.Basket, fact.Buy, fact.Baseline, fact.Observed, fact.Terminal, true);
        foreach (var fact in previous.Surveillance ?? Array.Empty<PositionCloseObservation>())
            if (cancellations?.ContainsKey(fact.Basket) == true || !(observations ??= new(StringComparer.Ordinal)).TryAdd(fact.Basket, fact))
                Observe(fact.Basket, fact.Buy, fact.Baseline, fact.Observed, fact.Terminal, false);
        if (!sameFacts)
        {
            foreach (var fact in state.Cancellations) Observe(fact.Basket, fact.Buy, fact.Baseline, fact.Observed, fact.Terminal, !dispatched);
            foreach (var fact in state.Surveillance ?? Array.Empty<PositionCloseObservation>())
                Observe(fact.Basket, fact.Buy, fact.Baseline, fact.Observed, fact.Terminal, false);
        }
        decimal reductions = 0;
        if (cancellations != null)
            foreach (var fact in cancellations.Values)
                if (fact.Buy == (state.ConfirmedNet < 0) && fact.Observed is { } observed)
                    reductions += Math.Max(0, observed - (fact.Baseline ?? 0));
        if (observations != null)
            foreach (var fact in observations.Values)
                if (fact.Buy == (state.ConfirmedNet < 0) && fact.Observed is { } observed)
                    reductions += Math.Max(0, observed - (fact.Baseline ?? 0));
        var phase = state.Phase;
        string? outcome = state.Outcome;
        long settledRevision = state.SettledRevision;
        string? settledScope = state.SettledObservationScope;
        if (previous.Phase is PositionClosePhase.Completed or PositionClosePhase.Rejected &&
            phase is PositionClosePhase.Working or PositionClosePhase.Dispatching)
        {
            phase = previous.Phase;
            outcome = previous.Outcome;
            settledRevision = previous.SettledRevision;
            settledScope = previous.SettledObservationScope;
        }
        bool lateFill = previous.LateFillUnresolved || state.LateFillUnresolved;
        if (previous.Phase == PositionClosePhase.Completed && reductions > previous.KnownReductions)
        {
            lateFill = true;
            outcome = "A late reducing fill changed the completed Rithmic close; its remaining position inclusion is unresolved.";
        }
        else if (previous.LateFillUnresolved) outcome = previous.Outcome ?? outcome;
        sameFacts &= (cancellations?.Count ?? 0) == previous.Cancellations.Length &&
            (observations?.Count ?? 0) == (previous.Surveillance?.Length ?? 0);
        bool invalidated = previous.Invalidated || state.Invalidated;
        if (lateFill) phase = PositionClosePhase.Unresolved;
        if (sameFacts && state.KnownReductions == reductions && state.Invalidated == invalidated &&
            state.LateFillUnresolved == lateFill && state.Phase == phase && state.Outcome == outcome &&
            state.SettledRevision == settledRevision && state.SettledObservationScope == settledScope) return state;
        return state with
        {
            Cancellations = sameFacts ? previous.Cancellations : cancellations?.Values.ToArray() ?? Array.Empty<PositionCloseCancellation>(),
            Surveillance = sameFacts ? previous.Surveillance : observations?.Values.ToArray(),
            KnownReductions = reductions, Invalidated = invalidated,
            LateFillUnresolved = lateFill, Phase = phase, Outcome = outcome,
            SettledRevision = settledRevision, SettledObservationScope = settledScope,
        };
    }

    private static decimal? MinimumBaseline(decimal? first, decimal? next) =>
        first is { } known && next is { } baseline ? Math.Min(known, baseline) : null;

    private static decimal? MaximumObservation(decimal? first, decimal? next) =>
        first is { } known && next is { } observed ? Math.Max(known, observed) : first ?? next;

    public OwnedOrderIntent StartPositionCloseDispatch(OwnedOrderIntent dispatch,
        Func<OwnedOrderIntent, string?>? currentCloseAdmission)
    {
        using var lease = Acquire();
        var current = ReadIntent(dispatch.Id) ?? throw new IOException("The bounded close identity is unavailable.");
        if (current.DispatchUncertain)
            throw new InvalidOperationException("The durable identity already has an uncertain dispatch; it cannot be resent.");
        if (current.PositionClose == null || current.ContextId != dispatch.ContextId || current.Generation != dispatch.Generation ||
            current.Request != dispatch.Request || current.ClientOrderId != dispatch.ClientOrderId)
            throw new IOException("The bounded close identity changed before dispatch.");
        if (current.Rejection != null)
            throw new InvalidOperationException("The bounded close identity changed before dispatch.");
        var close = MergePositionCloseFacts(current.PositionClose, current.PositionClose, current.DispatchUncertain);
        if (close.Cancellations.Any(fact => fact.Buy == (close.ConfirmedNet < 0) && fact.Observed == null) ||
            (close.Surveillance ?? Array.Empty<PositionCloseObservation>()).Any(fact => fact.Buy == (close.ConfirmedNet < 0) && fact.Observed == null))
            throw new PositionCloseAdmissionUnresolvedException("The bounded close's persisted reconciliation or cancellation evidence changed before dispatch.");
        if (close.Invalidated || close.Phase is PositionClosePhase.Completed or PositionClosePhase.NotSent or PositionClosePhase.Rejected ||
            currentCloseAdmission == null && close.Cancellations.Any(fact => !fact.Terminal))
            throw new InvalidOperationException("The bounded close's persisted reconciliation or cancellation evidence changed before dispatch.");
        if (currentCloseAdmission != null)
        {
            var latest = ReferenceEquals(close, current.PositionClose) ? current : current with { PositionClose = close };
            if (currentCloseAdmission(latest) is { } denied) throw new InvalidOperationException(denied);
        }
        if (dispatch.NativeInstrument != close.Symbol || dispatch.NativeExchange != close.Exchange || !dispatch.Request.ReduceOnly ||
            dispatch.Request.Direction != (close.ConfirmedNet < 0 ? OrderDirections.Buy : OrderDirections.Sell) ||
            dispatch.DispatchQuantity is not { } quantity || quantity <= 0 ||
            quantity > Math.Max(0, Math.Abs(close.ConfirmedNet) - close.KnownReductions))
            throw new InvalidOperationException("The bounded close dispatch exceeds its persisted route or remaining ceiling.");
        dispatch = dispatch with { PositionClose = close };
        SaveIntentLeased(dispatch);
        return dispatch;
    }

    public static IReadOnlyList<(AccountContextDescriptor Descriptor, OwnedOrderIntent Intent)> PreviousPositionCloses(
        string root, AccountContextDescriptor current)
    {
        string directory = Path.Combine(root, "execution-state");
        if (!Directory.Exists(directory)) return Array.Empty<(AccountContextDescriptor, OwnedOrderIntent)>();
        var records = new List<(AccountContextDescriptor, OwnedOrderIntent)>();
        foreach (string contextDirectory in Directory.EnumerateDirectories(directory))
        {
            var descriptor = Read<AccountContextDescriptor>(Path.Combine(contextDirectory, "context.json"));
            if (descriptor == null || descriptor.Provider != "Rithmic" || descriptor.Id == current.Id ||
                descriptor.LocalNamespace != current.LocalNamespace) continue;
            var journal = new OrderOwnershipJournal(root, descriptor);
            foreach (var intent in journal.PositionCloses())
                if (intent.PositionClose is { } close && (close.KnownReductions > 0 ||
                    close.Phase is not (PositionClosePhase.Completed or PositionClosePhase.NotSent or PositionClosePhase.Rejected)))
                    records.Add((descriptor, intent));
        }
        return records;
    }
}
