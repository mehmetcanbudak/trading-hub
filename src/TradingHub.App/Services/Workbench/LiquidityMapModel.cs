using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Indicators.Native;

namespace TradingHub.App.Services.Workbench;

/// <summary>Reference liquidation prices of a long and a short opened at the current price with one leverage (no maintenance margin).</summary>
public readonly record struct LeverageBand(int Leverage, decimal LongPrice, decimal ShortPrice);

/// <summary>
/// Everything the liquidity map shows, each part labeled by its provenance: the opt-in third-party snapshot, the local
/// estimate from open interest and measured delta, and the leverage reference grid. All three are estimates.
/// </summary>
public sealed record LiquidityMapView(decimal Price, EstimatedLiquidationMap.Map? Estimate, string? EstimateNote,
    IReadOnlyList<LiquidationLevelBin>? ThirdParty, LiquidationLevelsSnapshot? ThirdPartySnapshot, string? ThirdPartyNote,
    IReadOnlyList<LeverageBand> Bands);

public static class LiquidityMapModel
{
    public static readonly int[] ReferenceLeverages = { 5, 10, 25, 50, 100 };
    private static readonly TimeSpan OpenInterestMaxAge = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<LeverageBand> Bands(decimal price) =>
        price <= 0 ? Array.Empty<LeverageBand>()
        : ReferenceLeverages.Select(l => { var (lo, hi) = LeverageLiquidationBands.Prices(price, l); return new LeverageBand(l, lo, hi); }).ToArray();

    /// <summary>
    /// Model inputs per closed candle: the summed open-interest increase across <paramref name="openInterest"/> venues
    /// (null unless every venue has a sample within ten minutes of both edges) and the measured delta (null without
    /// buy volume). Mirrors the chart's "Liquidation map (estimate from open interest)" study.
    /// </summary>
    public static IReadOnlyList<EstimatedLiquidationMap.Entry> Entries(IReadOnlyList<Kline> candles, TimeSpan period,
        IReadOnlyList<IReadOnlyList<(DateTime Time, decimal Value)>> openInterest, DateTime now)
    {
        decimal? At(DateTime t)
        {
            if (openInterest.Count == 0) return null;
            decimal sum = 0;
            foreach (var venue in openInterest)
            {
                if (DerivativesPane.At(venue, t, OpenInterestMaxAge) is not { } v) return null;
                sum += v;
            }
            return sum;
        }
        var result = new List<EstimatedLiquidationMap.Entry>(candles.Count);
        foreach (var k in candles)
        {
            var open = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime;
            var close = open + period < now ? open + period : now;
            decimal? opened = At(close - TimeSpan.FromTicks(1)) - At(open - TimeSpan.FromTicks(1));
            decimal? delta = k.HasBuyVolume ? 2 * k.BuyVolume - k.Volume : null;
            result.Add(new EstimatedLiquidationMap.Entry(k.High, k.Low, k.Close, k.Volume, delta, opened));
        }
        return result;
    }

    /// <summary>Third-party bins still standing after the price traded since the snapshot, grouped to at least <paramref name="group"/>.</summary>
    public static IReadOnlyList<LiquidationLevelBin> Standing(LiquidationLevelsSnapshot snapshot, IReadOnlyList<Kline> candles, decimal group)
    {
        decimal low = decimal.MaxValue, high = decimal.MinValue;
        long asOf = new DateTimeOffset(DateTime.SpecifyKind(snapshot.AsOfUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        foreach (var k in candles)
        {
            if (k.OpenTimeMs < asOf - 15 * 60_000) continue;
            low = Math.Min(low, k.Low);
            high = Math.Max(high, k.High);
        }
        var remaining = LiquidationLevelsService.Remaining(snapshot, low == decimal.MaxValue ? snapshot.Mark : low, high == decimal.MinValue ? snapshot.Mark : high);
        decimal step = Math.Max(snapshot.Step, group);
        return remaining.GroupBy(b => Math.Floor(b.Price / step) * step)
            .Select(g => new LiquidationLevelBin(g.Key, g.Sum(x => x.LongNotional), g.Sum(x => x.ShortNotional), g.Max(x => x.LargestLong), g.Max(x => x.LargestShort)))
            .OrderBy(b => b.Price).ToArray();
    }
}
