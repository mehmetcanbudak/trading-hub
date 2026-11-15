using System.Globalization;
using System.Text.Json;

namespace TradingHub.Feeds.Derivatives;

/// <summary>
/// One venue's current perpetual reference prices: mark and index price, the current funding rate (percent per funding
/// period) and the next settlement. Null fields were not published by the venue.
/// </summary>
public sealed record VenueMarketStats(string VenueId, DateTime Time, decimal? MarkPrice, decimal? IndexPrice,
    decimal? FundingRatePercent, DateTime? NextFundingUtc)
{
    /// <summary>Perpetual premium over the index, in percent: (mark − index) / index × 100.</summary>
    public decimal? PremiumPercent => MarkPrice is { } mark && IndexPrice is { } index && index > 0 ? (mark - index) / index * 100 : null;
}

/// <summary>Share of accounts long and short (each 0–1) published by the venue for one period.</summary>
public readonly record struct LongShortSample(DateTime Time, decimal LongShare, decimal ShortShare)
{
    /// <summary>Long accounts per short account; null when the short share is zero.</summary>
    public decimal? Ratio => ShortShare > 0 ? LongShare / ShortShare : null;
}

public sealed record LongShortResult(IReadOnlyList<LongShortSample> Samples, OpenInterestAvailability Availability, string? Message = null);

public sealed partial class DerivativesData
{
    // ================================================================ premium and long/short

    /// <summary>
    /// Current mark/index price and funding of one perpetual: Binance <c>/fapi/v1/premiumIndex</c>, Bybit
    /// <c>/v5/market/tickers</c>. Public endpoints on the same hosts as funding and open interest.
    /// </summary>
    public async Task<VenueMarketStats> LoadMarketStatsAsync(string venue, string symbol, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        if (IsAddedVenue(venue)) return await LoadVenueMarketStatsAsync(venue, symbol, ct).ConfigureAwait(false);
        string escaped = Uri.EscapeDataString(symbol);
        if (venue == Binance)
        {
            using var doc = await GetAsync($"{BinanceOrigin}/fapi/v1/premiumIndex?symbol={escaped}", ct).ConfigureAwait(false);
            return ParseBinancePremium(doc.RootElement, _utcNow());
        }
        if (venue == Bybit)
        {
            using var doc = await GetAsync($"{BybitOrigin}/v5/market/tickers?category=linear&symbol={escaped}", ct).ConfigureAwait(false);
            return ParseBybitTicker(doc.RootElement, _utcNow())
                ?? throw new InvalidDataException($"{VenueName(venue)} published no ticker for {symbol}.");
        }
        throw new ArgumentException("Unknown derivatives venue.", nameof(venue));
    }

    /// <summary>Binance premiumIndex {markPrice, indexPrice, lastFundingRate (fraction), nextFundingTime, time}.</summary>
    public static VenueMarketStats ParseBinancePremium(JsonElement root, DateTime fetchedUtc)
    {
        DateTime time = root.TryGetProperty("time", out var t) && Long(t) is { } ms and > 0 ? FromMs(ms) : fetchedUtc;
        return new VenueMarketStats(Binance, time, Optional(root, "markPrice"), Optional(root, "indexPrice"),
            Optional(root, "lastFundingRate") * 100, root.TryGetProperty("nextFundingTime", out var next) && Long(next) is { } n and > 0 ? FromMs(n) : null);
    }

    /// <summary>Bybit tickers result.list[0] {markPrice, indexPrice, fundingRate (fraction), nextFundingTime}; null without a row.</summary>
    public static VenueMarketStats? ParseBybitTicker(JsonElement root, DateTime fetchedUtc)
    {
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var row in list.EnumerateArray())
        {
            DateTime time = root.TryGetProperty("time", out var t) && Long(t) is { } ms and > 0 ? FromMs(ms) : fetchedUtc;
            return new VenueMarketStats(Bybit, time, Optional(row, "markPrice"), Optional(row, "indexPrice"), Optional(row, "fundingRate") * 100,
                row.TryGetProperty("nextFundingTime", out var next) && Long(next) is { } n and > 0 ? FromMs(n) : null);
        }
        return null;
    }

    /// <summary>
    /// Long/short account ratio history: Binance <c>/futures/data/globalLongShortAccountRatio</c> (last 30 days only),
    /// Bybit <c>/v5/market/account-ratio</c>. One request of at most 500 periods; the period widens with the range.
    /// </summary>
    public async Task<LongShortResult> LoadLongShortAsync(string venue, string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        if (IsAddedVenue(venue)) return await LoadVenueLongShortAsync(venue, symbol, from, to, ct).ConfigureAwait(false);
        var span = to - from;
        string escaped = Uri.EscapeDataString(symbol);
        IReadOnlyList<LongShortSample> rows;
        if (venue == Binance)
        {
            string period = span <= TimeSpan.FromHours(41) ? "5m" : span <= TimeSpan.FromDays(20) ? "1h" : "1d";
            using var doc = await GetAsync($"{BinanceOrigin}/futures/data/globalLongShortAccountRatio?symbol={escaped}&period={period}&limit=500&startTime={Ms(from)}&endTime={Ms(to)}", ct).ConfigureAwait(false);
            rows = ParseBinanceLongShort(doc.RootElement).ToArray();
        }
        else if (venue == Bybit)
        {
            string period = span <= TimeSpan.FromHours(41) ? "5min" : span <= TimeSpan.FromDays(20) ? "1h" : "1d";
            using var doc = await GetAsync($"{BybitOrigin}/v5/market/account-ratio?category=linear&symbol={escaped}&period={period}&limit=500&startTime={Ms(from)}&endTime={Ms(to)}", ct).ConfigureAwait(false);
            rows = ParseBybitLongShort(doc.RootElement).ToArray();
        }
        else throw new ArgumentException("Unknown derivatives venue.", nameof(venue));
        var values = rows.Where(r => r.Time >= from && r.Time <= to).OrderBy(r => r.Time).ToArray();
        return values.Length == 0
            ? new(values, OpenInterestAvailability.Unavailable, $"{VenueName(venue)} reports no long/short ratio for {symbol} in this range.")
            : new(values, OpenInterestAvailability.Available);
    }

    /// <summary>Binance rows {longAccount, shortAccount (fractions), timestamp}.</summary>
    public static IEnumerable<LongShortSample> ParseBinanceLongShort(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in root.EnumerateArray())
            if (Long(e.GetProperty("timestamp")) is { } ms && Optional(e, "longAccount") is { } l && Optional(e, "shortAccount") is { } s)
                yield return new LongShortSample(FromMs(ms), l, s);
    }

    /// <summary>Bybit result.list rows {buyRatio, sellRatio (fractions), timestamp}, newest first.</summary>
    public static IEnumerable<LongShortSample> ParseBybitLongShort(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in list.EnumerateArray())
            if (Long(e.GetProperty("timestamp")) is { } ms && Optional(e, "buyRatio") is { } l && Optional(e, "sellRatio") is { } s)
                yield return new LongShortSample(FromMs(ms), l, s);
    }

    private static decimal? Optional(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDecimal();
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static long? Long(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetInt64(),
        JsonValueKind.String => long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null,
        _ => null,
    };
}
