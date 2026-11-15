// Closed-profile reuse across chart timeframes. Behaviour reference: gouqizi2214730-source/trading-hub
// @ 1f5bd6e22dd61ec12a4dee1ccb9d20eaad836582 (MIT with Commons Clause); our key is correction-safe instead of volume/delta only.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("TradingHub.Tests")]

namespace TradingHub.Indicators.Native;

public partial class CompositeVolumeProfile
{
    /// <summary>
    /// Process-wide, memory-only rows of closed source periods. A key binds the exact history generation
    /// (store identity + correction revision), provider, instrument, period bounds, footprint row size, grouping and coverage,
    /// so a 15-minute and a 4-hour chart of the same history share entries while any correction misses.
    /// </summary>
    internal static readonly CompletedStudyResultCache SharedClosedRows = new(null, maxEntries: 1024, maxBytes: 64L * 1024 * 1024);

    /// <summary>The cache this instance reads and publishes closed rows through (isolated caches are for tests).</summary>
    internal CompletedStudyResultCache ClosedRows { get; set; } = SharedClosedRows;

    private sealed record ClosedRowsPayload(decimal[] Prices, decimal[] Volumes, decimal[] SidePrices, decimal[] Bids, decimal[] Asks, ExtraResult Result);

    private Profile? _reuseProfile;
    private int _reuseEnd = -1;
    private CompletedStudyKey? _pendingClosedKey;
    private ExtraResult? _reusedResult;
    private int _reusedClosed;

    /// <summary>Closed source periods restored from another calculation (for example another timeframe) since the last reset.</summary>
    [Browsable(false)] public int ReusedClosedProfiles => _reusedClosed;

    private void ResetReuse() { _reuseProfile = null; _reuseEnd = -1; _pendingClosedKey = null; _reusedResult = null; _reusedClosed = 0; }

    private bool IsReusing(Profile profile, int bar) => ReferenceEquals(profile, _reuseProfile) && bar <= _reuseEnd;

    /// <summary>
    /// At the first bar of a period, decide whether its rows are timeframe-independent and either restore them or remember
    /// the key so <see cref="Finish"/> publishes them. Only scalar candle fields are read here, never footprint rows.
    /// </summary>
    private void BeginClosedReuse(Profile profile, int start)
    {
        _reuseProfile = null; _reuseEnd = -1; _pendingClosedKey = null; _reusedResult = null;
        if (start == 0 || _period == ProfilePeriod.AllHistory || start >= CurrentBar - 1) return;
        int end = start;
        while (end + 1 < CurrentBar && !Boundary(end + 1)) end++;
        if (end + 1 >= CurrentBar) return; // still developing
        var first = GetCandle(start);
        decimal volume = 0, bid = 0, ask = 0, trades = 0;
        StudyCoverage coverage = 0; int resolution = 0;
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        for (int bar = start; bar <= end; bar++)
        {
            var candle = GetCandle(bar);
            bool footprint = candle.HasObservedFootprint || candle.HasAggregatedFootprint;
            // Bars spilling outside the period, estimated rows and per-bar side exclusions depend on bar boundaries.
            if (!candle.IsCandleFinished || !candle.CanCalculateCausally || !StudyMath.IsContained(candle, ProfileFrame) ||
                (!footprint && _estimate && candle.Volume > 0) || (candle.HasInferredSide && !_inferred) ||
                candle.HistoryIdentity != first.HistoryIdentity || candle.HistoryCorrectionRevision != first.HistoryCorrectionRevision) return;
            volume += candle.Volume; bid += candle.Bid; ask += candle.Ask; trades += candle.Ticks;
            coverage |= footprint ? StudyMath.Coverage(candle) : StudyCoverage.Missing;
            resolution = Math.Max(resolution, candle.ResolutionSeconds);
            sources.Add(candle.SourceIdentity ?? "");
        }
        var bounds = StudyMath.PeriodBounds(first, ProfileFrame);
        string configuration = FormattableString.Invariant(
            $"closed-profile-rows-v1;frame={ProfileFrame};row={first.ProfileRowSize};effective={_effectiveGroup};estimate={_estimate};inferred={_inferred};va={_va};outer={_outer};zone={first.SessionTimeZone.Id}");
        var key = new CompletedStudyKey(first.SeriesSourceIdentity, InstrumentInfo?.Instrument ?? "", configuration,
            first.HistoryIdentity.ToString("N") + "|" + JsonSerializer.Serialize(sources), 0, first.HistoryCorrectionRevision, coverage, resolution,
            bounds.Start.Ticks, bounds.End.Ticks, FormattableString.Invariant($"{volume}|{bid}|{ask}|{trades}"));
        if (ClosedRows.TryRead(key, out var bytes) && Restore(profile, bytes))
        {
            _reuseProfile = profile; _reuseEnd = end; _reusedClosed++;
            return;
        }
        _reuseProfile = profile; _pendingClosedKey = key;
    }

    private bool Restore(Profile profile, byte[] bytes)
    {
        ClosedRowsPayload? payload;
        try { payload = JsonSerializer.Deserialize<ClosedRowsPayload>(bytes); }
        catch (JsonException) { return false; }
        if (payload?.Result == null || payload.Prices.Length != payload.Volumes.Length || payload.SidePrices.Length != payload.Bids.Length ||
            payload.SidePrices.Length != payload.Asks.Length || payload.Prices.Length == 0) return false;
        decimal total = 0;
        foreach (decimal value in payload.Volumes) total += value;
        if (total != payload.Result.Total) return false;
        for (int i = 0; i < payload.Prices.Length; i++) profile.Volumes[payload.Prices[i]] = payload.Volumes[i];
        for (int i = 0; i < payload.SidePrices.Length; i++) profile.Sides[payload.SidePrices[i]] = (payload.Bids[i], payload.Asks[i]);
        profile.Total = total;
        _reusedResult = payload.Result;
        return true;
    }

    /// <summary>Completes a restored period without sorting or re-deriving its rows.</summary>
    private bool TryFinishReused(Profile profile)
    {
        if (!ReferenceEquals(profile, _reuseProfile) || _reusedResult is not { } result) return false;
        profile.Poc = result.Poc; profile.MaximumVolume = result.MaximumVolume;
        profile.ValueAreaHigh = result.ValueAreaHigh; profile.ValueAreaLow = result.ValueAreaLow;
        decimal low = decimal.MaxValue, high = decimal.MinValue;
        foreach (decimal price in profile.Volumes.Keys) { low = Math.Min(low, price); high = Math.Max(high, price); }
        profile.LowPrice = low; profile.HighPrice = high;
        ApplyExtras(profile, result);
        _reuseProfile = null; _reusedResult = null;
        return true;
    }

    private void PublishClosedRows(Profile profile, ExtraResult result)
    {
        if (!ReferenceEquals(profile, _reuseProfile) || _pendingClosedKey is not { } key) return;
        _pendingClosedKey = null; _reuseProfile = null;
        var prices = new decimal[profile.Volumes.Count]; var volumes = new decimal[prices.Length]; int i = 0;
        foreach (var (price, volume) in profile.Volumes) { prices[i] = price; volumes[i++] = volume; }
        var sidePrices = new decimal[profile.Sides.Count]; var bids = new decimal[sidePrices.Length]; var asks = new decimal[sidePrices.Length]; i = 0;
        foreach (var (price, side) in profile.Sides) { sidePrices[i] = price; bids[i] = side.Bid; asks[i++] = side.Ask; }
        ClosedRows.Store(key, JsonSerializer.SerializeToUtf8Bytes(new ClosedRowsPayload(prices, volumes, sidePrices, bids, asks, result)));
    }
}
