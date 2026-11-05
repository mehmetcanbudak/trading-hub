namespace TradingHub.Indicators.Native;

/// <summary>Logical-price-row value area with upper-row tie priority and sparse empty-run jumps.</summary>
internal static class SparseStudyValueArea
{
    internal static (decimal Low, decimal High) Calculate(ReadOnlySpan<decimal> prices, ReadOnlySpan<decimal> volumes, int poc, decimal step, decimal target)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        if (prices.Length == 0 || prices.Length != volumes.Length || poc < 0 || poc >= prices.Length) throw new ArgumentException("Invalid sparse value-area rows.");
        int lower = poc, upper = poc; decimal accumulated = volumes[poc];
        while (accumulated < target && (lower > 0 || upper < prices.Length - 1))
        {
            decimal above = upper == prices.Length - 1 ? -1 : prices[upper + 1] - prices[upper] == step ? volumes[upper + 1] : 0;
            decimal below = lower == 0 ? -1 : prices[lower] - prices[lower - 1] == step ? volumes[lower - 1] : 0;
            // A zero/zero tie repeatedly takes upper empty rows until the next occupied upper row.
            // The opposite candidate cannot change while that happens. Leap there without allocating or visiting the gap.
            if (above >= below) accumulated += volumes[++upper];
            else accumulated += volumes[--lower];
        }
        return (prices[lower], prices[upper]);
    }
}
