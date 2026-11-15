using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;

namespace TradingHub.App.Services.Workbench;

public readonly record struct SeriesPoint(DateTime Time, double Value);

/// <summary>Inputs one venue returned; a null part failed or was not loaded and stays unknown.</summary>
public sealed record DerivativesVenueInputs(string Venue, FundingHistoryResult? Funding, OpenInterestHistoryResult? OpenInterest,
    VenueMarketStats? Stats, LongShortResult? LongShort, LiquidationSnapshot? Liquidations, IReadOnlyList<string> Errors);

/// <summary>
/// One dashboard row. Every value is null when the venue did not publish it or the request failed: the dashboard shows
/// a dash, never a zero. Liquidation totals are null when the stream was not observed during the window.
/// </summary>
public sealed record DerivativesVenueRow(string Venue, decimal? FundingPercent, double? FundingIntervalHours, decimal? Funding8hPercent,
    DateTime? NextFunding, decimal? OpenInterest, decimal? OpenInterestUsd, decimal? OpenInterestChange1hPercent,
    decimal? OpenInterestChange24hPercent, decimal? PremiumPercent, decimal? LongShortRatio, decimal? LongLiquidations24h,
    decimal? ShortLiquidations24h, LiquidationCoverage Liquidations24hCoverage, IReadOnlyList<string> Notes);

/// <summary>Pure aggregation for the derivatives dashboard: gap-aware resampling and per-venue rows.</summary>
public static class DerivativesDashboard
{
    /// <summary>
    /// Samples a step series on a regular grid: each slot takes the latest point at or before it, but only if that point
    /// is at most <paramref name="maxGap"/> old; otherwise the slot is null (a gap, drawn as a break, never as zero).
    /// </summary>
    public static double?[] Resample(IReadOnlyList<SeriesPoint> points, DateTime from, DateTime to, TimeSpan step, TimeSpan maxGap)
    {
        if (step <= TimeSpan.Zero || to <= from) return Array.Empty<double?>();
        int count = (int)Math.Min(10_000, Math.Ceiling((to - from) / step));
        var result = new double?[count];
        var sorted = points.Where(p => double.IsFinite(p.Value)).OrderBy(p => p.Time).ToArray();
        int k = -1;
        for (int i = 0; i < count; i++)
        {
            var slot = from + step * (i + 1);
            if (slot > to) slot = to;
            while (k + 1 < sorted.Length && sorted[k + 1].Time <= slot) k++;
            result[i] = k >= 0 && slot - sorted[k].Time <= maxGap ? sorted[k].Value : null;
        }
        return result;
    }

    /// <summary>
    /// Liquidation notional per bucket from observed events; buckets the venue's stream did not observe at all are null.
    /// <paramref name="venue"/> null sums every venue whose stream observed the bucket.
    /// </summary>
    public static double?[] LiquidationBuckets(IReadOnlyList<LiquidationEvent> events, IReadOnlyList<ObservedWindow> windows, string? venue,
        DateTime from, DateTime to, TimeSpan step, bool longs)
    {
        if (step <= TimeSpan.Zero || to <= from) return Array.Empty<double?>();
        int count = (int)Math.Min(10_000, Math.Ceiling((to - from) / step));
        var result = new double?[count];
        var venues = venue != null ? new[] { venue } : windows.Select(w => w.VenueId).Concat(events.Select(e => e.VenueId)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        for (int i = 0; i < count; i++)
        {
            var start = from + step * i;
            var end = start + step < to ? start + step : to;
            bool observed = venues.Any(v => TradeTape.CoverageOf(windows.Where(w => string.Equals(w.VenueId, v, StringComparison.OrdinalIgnoreCase)), start, end) != LiquidationCoverage.NotObserved);
            if (!observed) continue;
            result[i] = (double)events.Where(e => e.Time >= start && e.Time < end && e.LongLiquidated == longs &&
                (venue == null || string.Equals(e.VenueId, venue, StringComparison.OrdinalIgnoreCase))).Sum(e => e.Notional);
        }
        return result;
    }

    /// <summary>Percent change between the latest point and the point nearest <paramref name="ago"/> earlier (within tolerance); null when either is missing.</summary>
    public static decimal? Change(IReadOnlyList<(DateTime Time, decimal Value)> series, TimeSpan ago, TimeSpan tolerance)
    {
        if (series.Count < 2) return null;
        var last = series[^1];
        var target = last.Time - ago;
        (DateTime Time, decimal Value)? best = null;
        foreach (var p in series)
            if (Math.Abs((p.Time - target).Ticks) <= tolerance.Ticks && (best == null || Math.Abs((p.Time - target).Ticks) < Math.Abs((best.Value.Time - target).Ticks)))
                best = p;
        if (best is not { } before || before.Value == 0 || before.Time >= last.Time) return null;
        return (last.Value - before.Value) / before.Value * 100;
    }

    /// <summary>The usual spacing of the last funding settlements in hours (the median of up to five gaps); null with fewer than two.</summary>
    public static double? FundingIntervalHours(IReadOnlyList<FundingSample> samples)
    {
        if (samples.Count < 2) return null;
        var gaps = new List<double>();
        for (int i = Math.Max(1, samples.Count - 5); i < samples.Count; i++)
        {
            double hours = (samples[i].Time - samples[i - 1].Time).TotalHours;
            if (hours > 0) gaps.Add(Math.Round(hours * 4) / 4);
        }
        if (gaps.Count == 0) return null;
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    /// <summary>A funding rate per period converted to the common 8-hour basis.</summary>
    public static decimal NormalizeTo8Hours(decimal ratePercent, double intervalHours) =>
        intervalHours > 0 ? ratePercent * 8 / (decimal)intervalHours : ratePercent;

    /// <summary>Builds the row of one venue at <paramref name="now"/>; each missing input stays null.</summary>
    public static DerivativesVenueRow BuildRow(DerivativesVenueInputs input, DateTime now)
    {
        var notes = new List<string>(input.Errors);
        var funding = input.Funding is { Availability: OpenInterestAvailability.Available or OpenInterestAvailability.Partial } f ? f.Samples : Array.Empty<FundingSample>();
        if (input.Funding?.Message is { } fm) notes.Add(fm);
        double? interval = FundingIntervalHours(funding);
        decimal? rate = input.Stats?.FundingRatePercent ?? (funding.Count > 0 ? funding[^1].RatePercent : null);
        decimal? rate8 = rate is { } r && interval is { } h ? NormalizeTo8Hours(r, h) : null;

        var oi = input.OpenInterest is { Availability: OpenInterestAvailability.Available or OpenInterestAvailability.Partial } o
            ? o.Samples.OrderBy(s => s.Time).Select(s => (s.Time, s.Quantity)).ToArray() : Array.Empty<(DateTime, decimal)>();
        if (input.OpenInterest?.Message is { } om) notes.Add(om);
        decimal? openInterest = oi.Length > 0 && now - oi[^1].Item1 <= TimeSpan.FromHours(2) ? oi[^1].Item2 : null;
        decimal? price = input.Stats?.MarkPrice ?? input.Stats?.IndexPrice;
        decimal? usd = openInterest is { } q && price is { } p ? q * p : null;

        var ls = input.LongShort is { Availability: OpenInterestAvailability.Available } l && l.Samples.Count > 0 ? l.Samples[^1] : (LongShortSample?)null;
        if (input.LongShort?.Message is { } lm) notes.Add(lm);

        decimal? longs = null, shorts = null;
        var coverage = LiquidationCoverage.NotObserved;
        if (input.Liquidations is { } liq)
        {
            var windows = liq.Windows.Where(w => string.Equals(w.VenueId, input.Venue, StringComparison.OrdinalIgnoreCase)).ToArray();
            coverage = TradeTape.CoverageOf(windows, now.AddHours(-24), now);
            if (coverage != LiquidationCoverage.NotObserved)
            {
                var mine = liq.Events.Where(e => string.Equals(e.VenueId, input.Venue, StringComparison.OrdinalIgnoreCase) && e.Time >= now.AddHours(-24)).ToArray();
                longs = mine.Where(e => e.LongLiquidated).Sum(e => e.Notional);
                shorts = mine.Where(e => !e.LongLiquidated).Sum(e => e.Notional);
            }
            if (liq.Message is { } msg) notes.Add(msg);
        }
        return new DerivativesVenueRow(input.Venue, rate, interval, rate8, input.Stats?.NextFundingUtc, openInterest, usd,
            Change(oi, TimeSpan.FromHours(1), TimeSpan.FromMinutes(20)), Change(oi, TimeSpan.FromHours(24), TimeSpan.FromHours(2)),
            input.Stats?.PremiumPercent, ls?.Ratio, longs, shorts, coverage, notes.Distinct().ToArray());
    }
}
