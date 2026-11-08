namespace TradingHub.Trading;

internal static class NativeFuturesQuantity
{
    internal const string Error = "Futures quantity must be a whole number of contracts between 1 and 2147483647.";

    internal static bool TryConvert(decimal quantity, out int contracts)
    {
        contracts = 0;
        if (quantity <= 0 || quantity > int.MaxValue || quantity != decimal.Truncate(quantity)) return false;
        contracts = (int)quantity;
        return true;
    }

    internal static bool TryConvertReplacement(decimal filled, decimal remaining, out int total)
    {
        total = 0;
        if (filled < 0 || filled > int.MaxValue || filled != decimal.Truncate(filled) ||
            !TryConvert(remaining, out _)) return false;
        return TryConvert(filled + remaining, out total);
    }
}
