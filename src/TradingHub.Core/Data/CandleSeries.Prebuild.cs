using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>History values captured on the store owner's thread before background aggregation starts.</summary>
public readonly record struct TradeStoreView(Guid Identity, long Revision, long CorrectionRevision, int Count)
{
    public static TradeStoreView Capture(TradeStore store) => new(store.Identity, store.Revision, store.CorrectionRevision, store.Count);

    /// <summary>True while no correction/replacement happened after capture. Appends keep the view valid.</summary>
    public bool IsCurrent(TradeStore store) => store.Identity == Identity && store.CorrectionRevision == CorrectionRevision && store.Count >= Count;
}

public sealed partial class CandleSeries
{
    /// <summary>The history store generation this aggregation was built from.</summary>
    public Guid StoreIdentity => _storeIdentity;

    /// <summary>Correction revision of the history store this aggregation was built from; any correction changes it.</summary>
    public long StoreCorrectionRevision => _storeCorrectionRevision;

    /// <summary>Base row step: the automatic step, or the largest divisor of a fixed user step not above it.</summary>
    public static int BaseStep(int userStep, int automaticStep)
    {
        if (userStep <= 0) return automaticStep;
        if (userStep <= automaticStep) return userStep;
        for (int divisor = automaticStep; divisor >= 1; divisor--)
            if (userStep % divisor == 0) return divisor;
        return 1;
    }

    /// <summary>
    /// Aggregates the captured rows of <paramref name="view"/> from a background thread. Only rows below the captured count
    /// are read; the build stops when cancelled or when the store is corrected/replaced after capture.
    /// </summary>
    internal bool BuildThrough(TradeStore store, TradeStoreView view, CancellationToken cancellation)
    {
        const int checkpoint = 1 << 15;
        Clear();
        _storeIdentity = view.Identity;
        _storeCorrectionRevision = view.CorrectionRevision;
        for (int start = 0; start < view.Count; start += checkpoint)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!view.IsCurrent(store)) return false;
            int end = Math.Min(view.Count, start + checkpoint);
            for (int i = start; i < end; i++) ProcessTrade(store, i);
            _processed = end;
        }
        Revision++;
        return view.IsCurrent(store);
    }

    internal AggregationSnapshot? CaptureAggregationSnapshot(TradeStoreView view, long maxBytes, long keyBytes)
    {
        if (_processed != view.Count || _storeIdentity != view.Identity || _storeCorrectionRevision != view.CorrectionRevision || keyBytes > maxBytes - 256) return null;
        long estimatedBytes = keyBytes + 256;
        foreach (var candle in _candles)
        {
            // Count populated rows, never the potentially unbounded distance between prices.
            // The estimate covers scalar snapshots, array headers/alignment and retained source text.
            long rows = (long)candle.Levels.StoredRowCount + candle.AggregatedLevels.StoredRowCount;
            long candleBytes = 640 + 2L * (candle.SourceIdentity?.Length ?? 0) + 96 * rows;
            if (candleBytes > maxBytes - estimatedBytes) return null;
            estimatedBytes += candleBytes;
        }
        return new AggregationSnapshot(this, view.Revision, estimatedBytes);
    }

    /// <summary>Bounded suggestion over the captured rows only (safe from a background reader).</summary>
    internal static int SuggestPriceStep(TradeStore store, TradeStoreView view, Instrument instrument, ChartPeriod period, int targetRows)
    {
        if (view.Count < 50) return 1;
        var probe = new CandleSeries(instrument, period, 1);
        for (int i = Math.Max(0, view.Count - 100_000); i < view.Count; i++) probe.ProcessTrade(store, i);
        return SuggestFromProbe(probe, targetRows);
    }
}
