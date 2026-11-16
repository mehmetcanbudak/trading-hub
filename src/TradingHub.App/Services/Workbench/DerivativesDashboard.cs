using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Market;

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
        // A metric the venue does not publish is shown as such in its cell, not repeated as a note.
        if (input.LongShort is { Message: { } lm, Availability: not OpenInterestAvailability.Unsupported }) notes.Add(lm);

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

    // ================================================================ cross-coin funding board

    /// <summary>The board's default coins; the selected coin is added when it is not one of them.</summary>
    public static readonly IReadOnlyList<string> BoardCoins = new[] { "BTC", "ETH", "SOL", "XRP", "DOGE" };

    /// <summary>Funding at or above this 8-hour rate (in percent) gets the strongest board color.</summary>
    public const decimal HeatScale8hPercent = 0.05m;

    /// <summary>Board rows as (coin, USDT derivatives symbol): the default coins, then the selected symbol if it is new.</summary>
    public static IReadOnlyList<(string Coin, string Symbol)> BoardSymbols(IReadOnlyList<string> coins, string? selectedSymbol)
    {
        var result = coins.Select(c => (Coin: c.ToUpperInvariant(), Symbol: c.ToUpperInvariant() + "USDT")).ToList();
        if (selectedSymbol is { Length: > 4 } s && s.ToUpperInvariant() is var upper && upper.EndsWith("USDT", StringComparison.Ordinal)
            && result.All(r => r.Symbol != upper))
            result.Add((upper[..^4], upper));
        return result;
    }

    /// <summary>
    /// One board row from each venue's bulk funding answer. A venue whose board is absent has not answered yet; a null
    /// board failed; a loaded board without the symbol does not list it. None of these is a zero rate.
    /// </summary>
    public static FundingBoardRow BuildBoardRow(string coin, string symbol, IReadOnlyList<string> venues,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, FundingSnapshot>?> boards)
    {
        var cells = new List<FundingBoardCell>(venues.Count);
        foreach (var venue in venues)
        {
            if (!boards.TryGetValue(venue, out var board)) cells.Add(new(venue, FundingBoardCellState.Pending, null, null, null));
            else if (board == null) cells.Add(new(venue, FundingBoardCellState.Failed, null, null, null));
            else if (!board.TryGetValue(symbol, out var snapshot)) cells.Add(new(venue, FundingBoardCellState.NotListed, null, null, null));
            else cells.Add(new(venue, FundingBoardCellState.Value, snapshot.Rate8hPercent, snapshot.RatePercent, snapshot.IntervalHours));
        }
        var known = cells.Where(c => c.Rate8hPercent != null).ToArray();
        if (known.Length < 2) return new FundingBoardRow(coin, symbol, cells, null, null, null);
        var high = known.MaxBy(c => c.Rate8hPercent!.Value);
        var low = known.MinBy(c => c.Rate8hPercent!.Value);
        return new FundingBoardRow(coin, symbol, cells, AnnualizedSpread(known.Select(c => c.Rate8hPercent)), high.Venue, low.Venue);
    }

    /// <summary>
    /// The widest 8-hour funding difference between venues, annualized (three settlements a day, 365 days), in percent;
    /// null with fewer than two known rates. Unknown venues are skipped, never counted as zero.
    /// </summary>
    public static decimal? AnnualizedSpread(IEnumerable<decimal?> rates8hPercent)
    {
        var known = rates8hPercent.Where(r => r != null).Select(r => r!.Value).ToArray();
        return known.Length < 2 ? null : (known.Max() - known.Min()) * 3 * 365;
    }

    /// <summary>Color strength 0–1 of a board cell: proportional to the rate up to <see cref="HeatScale8hPercent"/>.</summary>
    public static double Heat(decimal rate8hPercent) => (double)Math.Min(1m, Math.Abs(rate8hPercent) / HeatScale8hPercent);

    // ================================================================ price and CVD

    /// <summary>
    /// Price and cumulative volume delta from the instrument's own trades, one value per step. CVD sums only volume
    /// whose aggressor side was measured (buy minus sell); volume with an inferred or unknown side, or synthetic prints,
    /// is left out and counted in <see cref="PriceCvdSeries.UnmeasuredVolume"/>. A step without trades is a gap for
    /// both series, and a step with trades but no measured side is a CVD gap; the cumulative total carries across
    /// gaps. Steps that left volume out or contain compact or partial history are flagged.
    /// </summary>
    public static PriceCvdSeries PriceAndCvd(IEnumerable<FlowTrade> trades, DateTime from, DateTime to, TimeSpan step)
    {
        if (step <= TimeSpan.Zero || to <= from) return new(Array.Empty<double?>(), Array.Empty<double?>(), Array.Empty<bool>(), 0, 0, 0, null);
        int count = (int)Math.Min(10_000, Math.Ceiling((to - from) / step));
        var price = new double?[count];
        var delta = new decimal?[count];
        var flagged = new bool[count];
        decimal measured = 0, unmeasured = 0;
        DateTime? first = null;
        foreach (var t in trades)
        {
            if (t.Time < from || t.Time >= to || t.Size <= 0) continue;
            int i = Math.Min(count - 1, (int)((t.Time - from) / step));
            first ??= t.Time;
            price[i] = (double)t.Price;
            var p = t.Provenance;
            bool sideMeasured = t.Side is TradeSide.Buy or TradeSide.Sell && !p.IsSideInferred && p.IsMeasured;
            if (sideMeasured)
            {
                delta[i] = (delta[i] ?? 0) + (t.Side == TradeSide.Buy ? t.Size : -t.Size);
                measured += t.Size;
            }
            else
            {
                unmeasured += t.Size;
                flagged[i] = true;
            }
            if (p.IsPartial || (p.Quality & DataQuality.Compact) != 0) flagged[i] = true;
        }
        var cvd = new double?[count];
        decimal running = 0;
        int flaggedCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (flagged[i]) flaggedCount++;
            if (delta[i] is not { } d) continue;
            running += d;
            cvd[i] = (double)running;
        }
        return new PriceCvdSeries(price, cvd, flagged, measured, unmeasured, flaggedCount, first);
    }

    // ================================================================ liquidation feed

    /// <summary>Observed liquidations at or above <paramref name="minNotional"/> (quote currency), newest first, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<LiquidationEvent> LiquidationFeed(IReadOnlyList<LiquidationEvent> events, decimal minNotional, int max) =>
        events.Where(e => e.Notional >= minNotional).OrderByDescending(e => e.Time).Take(Math.Max(0, max)).ToArray();

    /// <summary>
    /// Whether each venue's liquidation stream is observing now: an observed window reaches within
    /// <paramref name="tolerance"/> of <paramref name="now"/>.
    /// </summary>
    public static IReadOnlyList<(string Venue, bool Observing)> StreamCoverage(IReadOnlyList<ObservedWindow> windows, IReadOnlyList<string> venues, DateTime now, TimeSpan tolerance) =>
        venues.Select(v => (v, windows.Any(w => string.Equals(w.VenueId, v, StringComparison.OrdinalIgnoreCase) && w.From <= now && w.To >= now - tolerance))).ToArray();
}

public enum FundingBoardCellState { Pending, Value, NotListed, Failed }

/// <summary>One venue's funding for one coin: the rate per settlement, its interval and the 8-hour rate; null unless <see cref="State"/> is Value.</summary>
public readonly record struct FundingBoardCell(string Venue, FundingBoardCellState State, decimal? Rate8hPercent, decimal? RatePercent, double? IntervalHours);

/// <summary>A board row: per-venue cells and the annualized spread between the highest and lowest known venue.</summary>
public sealed record FundingBoardRow(string Coin, string Symbol, IReadOnlyList<FundingBoardCell> Cells, decimal? SpreadAnnualizedPercent, string? HighVenue, string? LowVenue);

/// <summary>One trade of the selected instrument for the price and CVD panes.</summary>
public readonly record struct FlowTrade(DateTime Time, decimal Price, decimal Size, TradeSide Side, TradeProvenance Provenance);

/// <summary>Gap-aware price and CVD per step with the provenance of the volume behind them.</summary>
public sealed record PriceCvdSeries(double?[] Price, double?[] Cvd, bool[] Flagged, decimal MeasuredVolume, decimal UnmeasuredVolume,
    int FlaggedSteps, DateTime? FirstTrade);
