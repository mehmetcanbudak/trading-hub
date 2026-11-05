using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Replay;

/// <summary>Owns every financial and temporal input of one practice run. Advance is also the consumer test seam.</summary>
public sealed class ReplaySession : IDisposable
{
    private InstrumentData? _data;
    private bool _disposed;
    private bool _initialHistoryProcessed;
    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);

    public ReplaySession(ReplaySelection selection, decimal startBalance = 10_000m, ReplayFees? fees = null, Guid? runId = null)
    {
        Selection = ReplaySelection.Selected(selection.Source, selection.StartUtc, selection.EndUtc, selection.Seed, selection.Blind);
        if (startBalance < 0) throw new ArgumentOutOfRangeException(nameof(startBalance));
        fees ??= new();
        fees.Validate();
        Fees = fees;
        RunId = runId ?? Guid.NewGuid();
        AccountId = "REPLAY-" + RunId.ToString("N");
        ClockUtc = selection.StartUtc;
        IsBlind = selection.Blind;
        Account = new ReplayAccount(this, selection.Source, startBalance, fees);
    }

    public Guid RunId { get; }
    public string AccountId { get; }
    public ReplaySelection Selection { get; }
    public ReplayFees Fees { get; }
    public ReplayAccount Account { get; }
    public DateTime ClockUtc { get; private set; }
    public bool IsBlind { get; private set; }
    public bool Ended { get; private set; }
    public string? DataWarning { get; private set; }
    public bool HasKnownAvailability { get; private set; } = true;
    public DataQuality Quality { get; private set; }
    public int ResolutionSeconds { get; private set; }
    public long CorrectionRevision { get; private set; }
    public ReplayFeed? Feed { get; private set; }
    public InstrumentData Data => _data ?? throw new InvalidOperationException("Replay data has not been bound.");
    public event Action? Changed;

    public ReplayFeed CreateFeed(string historyRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Feed != null) throw new InvalidOperationException("A replay run owns exactly one feed.");
        Feed = new ReplayFeed(historyRoot, Selection.Source, Selection.StartUtc, Selection.EndUtc, RunId) { Paused = true };
        return Feed;
    }

    public void Bind(InstrumentData data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Feed == null || !ReferenceEquals(data.Feed, Feed) || _data != null)
            throw new InvalidOperationException("Replay data must belong to this run's feed and can only be bound once.");
        _data = data;
        data.TradesAppended += OnTrades;
        data.HistoryLoaded += OnHistoryLoaded;
        data.StatusChanged += OnStatusChanged;
    }

    private void OnHistoryLoaded()
    {
        if (_initialHistoryProcessed || _data == null || Ended) return;
        _initialHistoryProcessed = true;
        OnTrades(0, _data.Trades.Count);
    }

    private void OnStatusChanged()
    {
        if (_disposed || Ended || _data == null || (_data.Status != FeedStatus.Error && _data.HistoryError == null)) return;
        HasKnownAvailability = false;
        DataWarning = "Replay history is unavailable or source availability is unknown. Practice fills require reliable source data.";
        Account.BlockAvailability();
        Notify();
    }

    private void OnTrades(int start, int count)
    {
        if (_disposed || _data == null) return;
        for (int i = start; i < start + count; i++)
        {
            var trades = _data.Trades;
            var trade = new Trade(trades.GetId(i), trades.GetTime(i), trades.GetPrice(i), trades.GetVolume(i), trades.GetSide(i), trades.GetProvenance(i));
            if (trade.Provenance.HasKnownAvailability && trade.AvailableAt < Selection.StartUtc) continue;
            Advance(trade);
        }
    }

    /// <summary>Delivers available source events; their chart locations may precede the selected start. The clock never moves backward.</summary>
    public void Advance(in Trade trade)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Ended) return;
        if (!trade.Provenance.HasKnownAvailability)
        {
            HasKnownAvailability = false;
            DataWarning = "Source availability is unknown. This event cannot mark equity or execute practice orders.";
            Account.BlockAvailability();
            Notify();
            throw new ArgumentException(DataWarning, nameof(trade));
        }
        DateTime available = trade.AvailableAt;
        if (trade.Time.Kind != DateTimeKind.Utc || available.Kind != DateTimeKind.Utc || available < Selection.StartUtc || available > Selection.EndUtc)
            throw new ArgumentException("Replay source availability must be inside the selected UTC interval.");
        if (trade.Volume < 0) throw new ArgumentException("Replay tape has an invalid volume.");
        if (available > ClockUtc) ClockUtc = available;
        Quality |= trade.Provenance.Quality;
        ResolutionSeconds = Math.Max(ResolutionSeconds, trade.Provenance.ResolutionSeconds);
        CorrectionRevision = Math.Max(CorrectionRevision, trade.Provenance.CorrectionRevision);
        _sources.Add(trade.Provenance.SourceIdentity ?? Selection.Source.FeedId);
        if (!HasKnownAvailability)
            DataWarning = "Some replay history or source availability is unknown. Excluded events cannot mark equity or execute practice orders.";
        else if ((Quality & (DataQuality.Compact | DataQuality.Synthetic)) != 0)
            DataWarning = "Aggregated or synthetic history marks price but does not supply executable practice prints.";
        else if ((Quality & DataQuality.Partial) != 0)
            DataWarning = "Recorded tape has partial coverage; practice results cannot represent missing events.";
        else if ((Quality & DataQuality.InferredSide) != 0)
            DataWarning = "Aggressor side is inferred. Practice fills use recorded price and quantity, not measured aggressor side.";
        Account.Advance(trade);
        Notify();
    }

    public void Reveal() { IsBlind = false; Notify(); }

    /// <summary>Ends at the last delivered price, explicitly closing or retaining an open marked position; no future-price lookahead.</summary>
    public ReplayResult End(bool closePosition = true)
    {
        if (!Ended)
        {
            Feed?.Stop();
            _data?.Stop(); // Discard queued but not yet presented tape; the last delivered print remains authoritative.
            if (closePosition && Account.HasObservedPrice) Account.Flatten();
            else Account.CancelAll();
            Ended = true;
            IsBlind = false;
            Notify();
        }
        return Result();
    }

    public ReplayResult Result() => new(RunId, Selection.Seed, Selection.Source.Key, Selection.StartUtc, Selection.EndUtc,
        ClockUtc, Ended, Account.Portfolio!.BeginBalance, Account.Portfolio.RealizedPnL, Account.Position!.UnrealizedPnL,
        Account.Portfolio.Commission, Account.Position.Volume, Account.Position.AveragePrice, Account.Journal.ToArray(),
        Fees, Quality, ResolutionSeconds, CorrectionRevision, _sources.Order(StringComparer.Ordinal).ToArray(), DataWarning, HasKnownAvailability);

    private void Notify()
    {
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList()) { try { handler(); } catch { } }
    }
    public void Dispose()
    {
        if (_disposed) return;
        End();
        _disposed = true;
        if (_data != null)
        {
            _data.TradesAppended -= OnTrades;
            _data.HistoryLoaded -= OnHistoryLoaded;
            _data.StatusChanged -= OnStatusChanged;
        }
        Feed?.Dispose();
    }
}
