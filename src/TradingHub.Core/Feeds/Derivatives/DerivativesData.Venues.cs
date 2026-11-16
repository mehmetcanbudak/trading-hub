using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingHub.Feeds.Venues;

namespace TradingHub.Feeds.Derivatives;

/// <summary>
/// The current funding of one perpetual on one venue, for cross-venue boards: the rate per settlement in percent, the
/// settlement interval and the next settlement when the venue publishes it.
/// </summary>
/// <param name="Symbol">The requested derivatives symbol (BTCUSDT), not the venue's own name.</param>
public sealed record FundingSnapshot(string VenueId, string Symbol, decimal RatePercent, double IntervalHours, DateTime? NextFundingUtc)
{
    /// <summary>The rate on the common 8-hour basis.</summary>
    public decimal Rate8hPercent => IntervalHours > 0 ? RatePercent * 8 / (decimal)IntervalHours : RatePercent;
}

/// <summary>
/// Which public derivatives data a venue publishes and Trading Hub reads. A false entry is not published by the venue
/// (or, for liquidations, not streamed by Trading Hub) and is shown as such, never as zero.
/// </summary>
public sealed record DerivativesVenueCoverage(bool FundingHistory, bool OpenInterestHistory, bool LongShortRatio, bool LiquidationStream);

/// <summary>
/// A perpetual's base name split into the coin and the number of coins per unit: 1000PEPE → (PEPE, 1000), 1MBABYDOGE →
/// (BABYDOGE, 1 000 000), Hyperliquid's kPEPE → (PEPE, 1000), BTC and 1INCH → themselves with 1.
/// </summary>
public readonly record struct ContractUnit(string Coin, decimal Multiplier)
{
    public static ContractUnit Of(string baseName)
    {
        if (baseName.Length > 1 && baseName[0] == 'k' && char.IsUpper(baseName[1])) return new(baseName[1..], 1000);
        if (baseName.Length > 2 && baseName.StartsWith("1M", StringComparison.Ordinal) && char.IsLetter(baseName[2])) return new(baseName[2..], 1_000_000);
        int zeros = 0;
        if (baseName.StartsWith('1'))
            while (1 + zeros < baseName.Length && baseName[1 + zeros] == '0') zeros++;
        if (zeros < 3 || 1 + zeros >= baseName.Length || !char.IsLetter(baseName[1 + zeros])) return new(baseName, 1);
        decimal multiplier = 1;
        for (int i = 0; i < zeros; i++) multiplier *= 10;
        return new(baseName[(1 + zeros)..], multiplier);
    }
}

/// <summary>
/// A venue's contract for a requested derivatives symbol. Venue quantities × <see cref="Scale"/> are in the requested
/// symbol's unit and venue prices ÷ <see cref="Scale"/> are per that unit (Scale 1 when both units match).
/// </summary>
public sealed record VenueContract(string Native, decimal Scale);

public sealed partial class DerivativesData
{
    /// <summary>OKX USDT-margined perpetual swaps (BTC-USDT-SWAP); the same id as the OKX swap feed.</summary>
    public const string Okx = OkxFeed.SwapId;

    /// <summary>Bitget USDT-M perpetual futures (BTCUSDT); the same id as the Bitget futures feed.</summary>
    public const string Bitget = BitgetFeed.FuturesId;

    /// <summary>Hyperliquid USD-settled perpetuals (coin name, BTC); the same id as the Hyperliquid feed.</summary>
    public const string Hyperliquid = HyperliquidFeed.PerpId;

    /// <summary>
    /// Every perpetual venue with public funding, open interest and premium data. <see cref="Venues"/> remains the
    /// venues with liquidation streams and open-interest history shared by the flow studies.
    /// </summary>
    public static readonly IReadOnlyList<string> PerpetualVenues = new[] { Binance, Bybit, Okx, Bitget, Hyperliquid };

    private static readonly (string Name, TimeSpan Length)[] OkxPeriods =
    {
        ("5m", TimeSpan.FromMinutes(5)), ("15m", TimeSpan.FromMinutes(15)), ("30m", TimeSpan.FromMinutes(30)), ("1H", TimeSpan.FromHours(1)),
        ("2H", TimeSpan.FromHours(2)), ("4H", TimeSpan.FromHours(4)), ("6H", TimeSpan.FromHours(6)), ("12H", TimeSpan.FromHours(12)), ("1D", TimeSpan.FromDays(1)),
    };

    private static readonly (string Name, TimeSpan Length)[] BitgetPeriods =
    {
        ("5m", TimeSpan.FromMinutes(5)), ("15m", TimeSpan.FromMinutes(15)), ("30m", TimeSpan.FromMinutes(30)), ("1h", TimeSpan.FromHours(1)),
        ("2h", TimeSpan.FromHours(2)), ("4h", TimeSpan.FromHours(4)), ("6h", TimeSpan.FromHours(6)), ("12h", TimeSpan.FromHours(12)), ("1d", TimeSpan.FromDays(1)),
    };

    /// <summary>What each venue publishes publicly; liquidation streams are read from Binance and Bybit only.</summary>
    public static DerivativesVenueCoverage Coverage(string venue) => venue switch
    {
        Binance or Bybit => new(true, true, true, true),
        Okx => new(true, true, true, false),
        Bitget => new(true, false, true, false),
        Hyperliquid => new(true, false, false, false),
        _ => throw new ArgumentException("Unknown derivatives venue.", nameof(venue)),
    };

    private static bool IsAddedVenue(string venue) => venue is Okx or Bitget or Hyperliquid;

    private string OkxOrigin => (_transport.OkxRest ?? new Uri("https://www.okx.com")).GetLeftPart(UriPartial.Authority);
    private string BitgetOrigin => (_transport.BitgetRest ?? new Uri("https://api.bitget.com")).GetLeftPart(UriPartial.Authority);
    private string HyperliquidOrigin => (_transport.HyperliquidRest ?? new Uri("https://api.hyperliquid.xyz")).GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// The OKX swap for a USDT-perpetual derivatives symbol (BTCUSDT → BTC-USDT-SWAP). OKX lists coins without unit
    /// prefixes (1000PEPE and 1MBABYDOGE are PEPE and BABYDOGE) and publishes open interest in the coin itself.
    /// </summary>
    public static string OkxNativeSymbol(string symbol) => ContractUnit.Of(RequestedBase(symbol)).Coin + "-USDT-SWAP";

    /// <summary>The base of a requested derivatives symbol: 1000PEPEUSDT → 1000PEPE.</summary>
    private static string RequestedBase(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        return symbol.EndsWith("USDT", StringComparison.Ordinal) && symbol.Length > 4 ? symbol[..^4] : symbol;
    }

    /// <summary>
    /// The listed contract for a requested symbol: the one quoted in the same unit, otherwise the only listing of the same
    /// coin in another unit (PEPEUSDT for 1000PEPEUSDT, 1000BONKUSDT or kBONK for BONKUSDT). Null when the venue does not
    /// list the coin, or lists it only in several other units (which one is meant is unknown).
    /// </summary>
    /// <param name="listed">The venue's listed contracts: its own symbol and the base name it quotes (1000BONK, kPEPE).</param>
    public static VenueContract? ResolveContract(IEnumerable<(string Native, string Base)> listed, string symbol)
    {
        var wanted = ContractUnit.Of(RequestedBase(symbol));
        VenueContract? other = null;
        int others = 0;
        foreach (var (native, baseName) in listed)
        {
            var unit = ContractUnit.Of(baseName);
            if (!string.Equals(unit.Coin, wanted.Coin, StringComparison.OrdinalIgnoreCase)) continue;
            var contract = new VenueContract(native, unit.Multiplier / wanted.Multiplier);
            if (unit.Multiplier == wanted.Multiplier) return contract;
            other = contract;
            others++;
        }
        return others == 1 ? other : null;
    }

    private static VenueContract OkxContract(string symbol) => new(OkxNativeSymbol(symbol), 1 / ContractUnit.Of(RequestedBase(symbol)).Multiplier);

    /// <summary>OKX from its naming rule; Bitget and Hyperliquid from their published instrument lists (cached for an hour).</summary>
    private async Task<VenueContract> ContractAsync(string venue, string symbol, CancellationToken ct) => venue switch
    {
        Okx => OkxContract(symbol),
        Bitget => ResolveContract(await ListedAsync(_bitgetContracts, LoadBitgetContractsAsync, ct).ConfigureAwait(false), symbol),
        _ => ResolveContract(await ListedAsync(_hyperliquidUniverse, LoadHyperliquidUniverseAsync, ct).ConfigureAwait(false), symbol),
    } ?? throw NotListed(venue, symbol);

    private static InvalidDataException NotListed(string venue, string symbol) => new($"{VenueName(venue)} lists no perpetual for {symbol}.");

    /// <summary>The note shown when a venue's contract is quoted in another unit than the requested symbol; null when the unit matches.</summary>
    private static string? UnitNote(string venue, string symbol, VenueContract contract) => contract.Scale == 1 ? null
        : $"{VenueName(venue)} lists {symbol.ToUpperInvariant()} as {contract.Native}; its open interest and prices are converted to {RequestedBase(symbol)}.";

    // ================================================================ instrument lists

    private static readonly TimeSpan ListLifetime = TimeSpan.FromHours(1);

    /// <summary>One venue's listed contracts, loaded at most once per <see cref="ListLifetime"/> by one caller at a time.</summary>
    private sealed class ListedContracts
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IReadOnlyList<(string Native, string Base)>? Items;
        public DateTime LoadedAt;
    }

    private readonly ListedContracts _bitgetContracts = new(), _hyperliquidUniverse = new();

    private async Task<IReadOnlyList<(string Native, string Base)>> ListedAsync(ListedContracts list,
        Func<CancellationToken, Task<IReadOnlyList<(string Native, string Base)>>> load, CancellationToken ct)
    {
        await list.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _utcNow();
            if (list.Items == null || now - list.LoadedAt >= ListLifetime || now < list.LoadedAt)
            {
                list.Items = await load(ct).ConfigureAwait(false);
                list.LoadedAt = now;
            }
            return list.Items;
        }
        finally { list.Gate.Release(); }
    }

    private async Task<IReadOnlyList<(string Native, string Base)>> LoadBitgetContractsAsync(CancellationToken ct)
    {
        using var doc = await GetVenueJsonAsync(Bitget, $"{BitgetOrigin}/api/v2/mix/market/contracts?productType=USDT-FUTURES", null, ct).ConfigureAwait(false);
        return ParseBitgetContracts(doc.RootElement);
    }

    private async Task<IReadOnlyList<(string Native, string Base)>> LoadHyperliquidUniverseAsync(CancellationToken ct)
    {
        using var doc = await HyperliquidInfoAsync(new { type = "metaAndAssetCtxs" }, ct).ConfigureAwait(false);
        return ParseHyperliquidUniverse(doc.RootElement);
    }

    /// <summary>
    /// Bitget contracts data rows {symbol, baseCoin, quoteCoin, symbolType, symbolStatus}: its USDT perpetuals by symbol and
    /// base coin. The base carries the unit (1000BONK); sizeMultiplier is the order step, not a unit. Delisted ("off") rows are skipped.
    /// </summary>
    public static IReadOnlyList<(string Native, string Base)> ParseBitgetContracts(JsonElement root)
    {
        var result = new List<(string, string)>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in data.EnumerateArray())
            if (Text(e, "symbol") is { Length: > 0 } native && Text(e, "baseCoin") is { Length: > 0 } baseCoin && Text(e, "quoteCoin") == "USDT"
                && Text(e, "symbolType") is null or "perpetual" && Text(e, "symbolStatus") != "off")
                result.Add((native, baseCoin));
        return result;
    }

    /// <summary>Hyperliquid metaAndAssetCtxs universe names (BTC, kPEPE), each its own native symbol and base; delisted coins are skipped.</summary>
    public static IReadOnlyList<(string Native, string Base)> ParseHyperliquidUniverse(JsonElement root)
    {
        var result = new List<(string, string)>();
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1 || !root[0].TryGetProperty("universe", out var universe)
            || universe.ValueKind != JsonValueKind.Array) return result;
        foreach (var u in universe.EnumerateArray())
            if (Text(u, "name") is { Length: > 0 } name && !(u.TryGetProperty("isDelisted", out var d) && d.ValueKind == JsonValueKind.True))
                result.Add((name, name));
        return result;
    }

    /// <summary>The shortest venue period whose <paramref name="rows"/> periods cover the span.</summary>
    private static (string Name, TimeSpan Length) PeriodFor((string Name, TimeSpan Length)[] periods, TimeSpan span, int rows) =>
        periods.FirstOrDefault(p => p.Length * rows >= span) is { Name: not null } match ? match : periods[^1];

    // ================================================================ transport

    /// <summary>
    /// GET (or POST with <paramref name="body"/>) one OKX, Bitget or Hyperliquid endpoint. OKX answers code "0" and
    /// Bitget code "00000" on success; any other code is the venue's error message.
    /// </summary>
    private async Task<JsonDocument> GetVenueJsonAsync(string venue, string url, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode}: {VenueError(text) ?? (text.Length > 300 ? text[..300] : text)}", null, response.StatusCode);
        var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) && venue is Okx or Bitget
            && (code.ValueKind == JsonValueKind.String ? code.GetString() : code.ToString()) is { } value && value != (venue == Okx ? "0" : "00000"))
        {
            string message = VenueError(text) ?? value;
            doc.Dispose();
            throw new InvalidOperationException(message);
        }
        return doc;
    }

    private static string? VenueError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String
                && m.GetString() is { Length: > 0 } message ? message : null;
        }
        catch (JsonException) { return null; }
    }

    private CancellationTokenSource Linked(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
    }

    private Task<JsonDocument> HyperliquidInfoAsync(object request, CancellationToken ct) =>
        GetVenueJsonAsync(Hyperliquid, $"{HyperliquidOrigin}/info", JsonSerializer.Serialize(request), ct);

    // ================================================================ funding

    private async Task<FundingHistoryResult> LoadVenueFundingAsync(string venue, string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        using var linked = Linked(ct);
        ct = linked.Token;
        var samples = new SortedDictionary<DateTime, FundingSample>();
        string native = (await ContractAsync(venue, symbol, ct).ConfigureAwait(false)).Native;
        if (venue == Okx)
        {
            // Newest first; "after" pages to settlements strictly older than the cursor. OKX keeps about three months.
            string? after = null;
            for (int page = 0; page < 20; page++)
            {
                using var doc = await GetVenueJsonAsync(venue, $"{OkxOrigin}/api/v5/public/funding-rate-history?instId={Uri.EscapeDataString(native)}&limit=400"
                    + (after == null ? "" : $"&after={after}"), null, ct).ConfigureAwait(false);
                var rows = ParseOkxFunding(doc.RootElement).ToList();
                foreach (var row in rows) samples[row.Time] = row;
                if (rows.Count == 0) break;
                var oldest = rows.Min(r => r.Time);
                if (oldest <= from) break;
                after = Ms(oldest).ToString(CultureInfo.InvariantCulture);
            }
        }
        else if (venue == Bitget)
        {
            for (int page = 1; page <= 20; page++)
            {
                using var doc = await GetVenueJsonAsync(venue, $"{BitgetOrigin}/api/v2/mix/market/history-fund-rate?symbol={Uri.EscapeDataString(native)}&productType=USDT-FUTURES&pageSize=100&pageNo={page}",
                    null, ct).ConfigureAwait(false);
                var rows = ParseBitgetFunding(doc.RootElement).ToList();
                foreach (var row in rows) samples[row.Time] = row;
                if (rows.Count < 100 || rows.Min(r => r.Time) <= from) break;
            }
        }
        else
        {
            // Oldest first, at most 500 hourly settlements per answer.
            long start = Ms(from), end = Ms(to);
            for (int page = 0; page < 50 && start < end; page++)
            {
                using var doc = await HyperliquidInfoAsync(new { type = "fundingHistory", coin = native, startTime = start, endTime = end }, ct).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{VenueName(venue)} published no ticker for {symbol}.");
                var rows = ParseHyperliquidFunding(doc.RootElement).ToList();
                foreach (var row in rows) samples[row.Time] = row;
                if (rows.Count < 500) break;
                start = Ms(rows.Max(r => r.Time)) + 1;
            }
        }
        var values = samples.Values.Where(s => s.Time >= from && s.Time <= to).ToArray();
        return values.Length == 0
            ? new(values, OpenInterestAvailability.Unavailable, $"{VenueName(venue)} reports no funding for {symbol} in this range.")
            : new(values, OpenInterestAvailability.Available);
    }

    /// <summary>OKX funding-rate-history data rows {fundingRate, realizedRate, fundingTime} (fractions), newest first.</summary>
    public static IEnumerable<FundingSample> ParseOkxFunding(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in data.EnumerateArray())
            if (e.TryGetProperty("fundingTime", out var t) && Long(t) is { } ms && (Optional(e, "realizedRate") ?? Optional(e, "fundingRate")) is { } rate)
                yield return new FundingSample(FromMs(ms), rate * 100);
    }

    /// <summary>Bitget history-fund-rate data rows {fundingRate (fraction), fundingTime}, newest first.</summary>
    public static IEnumerable<FundingSample> ParseBitgetFunding(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in data.EnumerateArray())
            if (e.TryGetProperty("fundingTime", out var t) && Long(t) is { } ms && Optional(e, "fundingRate") is { } rate)
                yield return new FundingSample(FromMs(ms), rate * 100);
    }

    /// <summary>Hyperliquid fundingHistory rows {coin, fundingRate (hourly fraction), premium, time}, oldest first.</summary>
    public static IEnumerable<FundingSample> ParseHyperliquidFunding(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in root.EnumerateArray())
            if (e.TryGetProperty("time", out var t) && Long(t) is { } ms && Optional(e, "fundingRate") is { } rate)
                yield return new FundingSample(FromMs(ms), rate * 100);
    }

    // ================================================================ open interest

    private async Task<OpenInterestHistoryResult> LoadVenueOpenInterestAsync(string venue, string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        using var linked = Linked(ct);
        ct = linked.Token;
        VenueContract? contract = venue == Hyperliquid ? null : await ContractAsync(venue, symbol, ct).ConfigureAwait(false);
        string native = contract?.Native ?? "";
        var samples = new SortedDictionary<DateTime, OpenInterestSample>();
        TimeSpan period;
        if (venue == Okx)
        {
            var (name, length) = PeriodFor(OkxPeriods, to - from, 100);
            period = length;
            string escaped = Uri.EscapeDataString(native);
            using (var history = await GetVenueJsonAsync(venue, $"{OkxOrigin}/api/v5/rubik/stat/contracts/open-interest-history?instId={escaped}&period={name}&begin={Ms(from)}&end={Ms(to)}&limit=100",
                       null, ct).ConfigureAwait(false))
                foreach (var s in ParseOkxOpenInterestHistory(history.RootElement)) samples[s.Time] = s;
            using var current = await GetVenueJsonAsync(venue, $"{OkxOrigin}/api/v5/public/open-interest?instType=SWAP&instId={escaped}", null, ct).ConfigureAwait(false);
            if (ParseOkxOpenInterest(current.RootElement) is { } now) samples[now.Time] = now;
        }
        else if (venue == Bitget)
        {
            period = TimeSpan.Zero;
            using var doc = await GetVenueJsonAsync(venue, $"{BitgetOrigin}/api/v2/mix/market/open-interest?symbol={Uri.EscapeDataString(native)}&productType=USDT-FUTURES", null, ct).ConfigureAwait(false);
            if (ParseBitgetOpenInterest(doc.RootElement) is { } now) samples[now.Time] = now;
        }
        else
        {
            period = TimeSpan.Zero;
            using var doc = await HyperliquidInfoAsync(new { type = "metaAndAssetCtxs" }, ct).ConfigureAwait(false);
            // The listing comes from the same answer: an unlisted coin has no open interest rather than an error.
            contract = ResolveContract(ParseHyperliquidUniverse(doc.RootElement), symbol);
            if (contract != null && ParseHyperliquidContext(doc.RootElement, contract.Native) is { } context && Optional(context, "openInterest") is { } oi && oi >= 0)
            {
                var fetched = _utcNow();
                samples[fetched] = new OpenInterestSample(fetched, oi);
            }
        }
        // Quantities in the requested symbol's unit: 1000PEPE requested from a venue quoting PEPE divides by 1000.
        decimal scale = contract?.Scale ?? 1;
        var values = samples.Values.Where(s => s.Time >= from && s.Time <= to.AddMinutes(5)).Select(s => s with { Quantity = s.Quantity * scale }).ToArray();
        if (values.Length == 0)
            return new(values, null, null, OpenInterestAvailability.Unavailable, $"{VenueName(venue)} reports no open interest for {symbol} in this range.");
        // Bitget and Hyperliquid publish only the current value: one sample is partial coverage of any range.
        bool complete = period > TimeSpan.Zero && values[0].Time <= from + period * 2;
        return new(values, values[0].Time, values[^1].Time, complete ? OpenInterestAvailability.Available : OpenInterestAvailability.Partial,
            contract == null ? null : UnitNote(venue, symbol, contract));
    }

    /// <summary>OKX rubik open-interest-history rows [ts, oi (contracts), oiCcy (base), oiUsd], newest first; the base-asset column is used.</summary>
    public static IEnumerable<OpenInterestSample> ParseOkxOpenInterestHistory(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in data.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 3 && Long(row[0]) is { } ms && Number(row[2]) is { } oi && oi >= 0)
                yield return new OpenInterestSample(FromMs(ms), oi);
    }

    /// <summary>OKX public open-interest data[0] {oiCcy (base), ts}; null without a row.</summary>
    public static OpenInterestSample? ParseOkxOpenInterest(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var row in data.EnumerateArray())
            return row.TryGetProperty("ts", out var t) && Long(t) is { } ms && Optional(row, "oiCcy") is { } oi && oi >= 0 ? new OpenInterestSample(FromMs(ms), oi) : null;
        return null;
    }

    /// <summary>Bitget open-interest data {openInterestList: [{symbol, size (base)}], ts}; null without a row.</summary>
    public static OpenInterestSample? ParseBitgetOpenInterest(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("openInterestList", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var row in list.EnumerateArray())
            return data.TryGetProperty("ts", out var t) && Long(t) is { } ms && Optional(row, "size") is { } size && size >= 0 ? new OpenInterestSample(FromMs(ms), size) : null;
        return null;
    }

    /// <summary>The asset context of one coin in Hyperliquid metaAndAssetCtxs [{universe:[{name}]}, [ctx…]] (same order); null when absent.</summary>
    public static JsonElement? ParseHyperliquidContext(JsonElement root, string coin)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 || !root[0].TryGetProperty("universe", out var universe)
            || universe.ValueKind != JsonValueKind.Array || root[1].ValueKind != JsonValueKind.Array) return null;
        int index = 0;
        foreach (var u in universe.EnumerateArray())
        {
            int position = index++;
            if (u.TryGetProperty("name", out var name) && name.GetString() == coin)
                return position < root[1].GetArrayLength() && !(u.TryGetProperty("isDelisted", out var d) && d.ValueKind == JsonValueKind.True) ? root[1][position] : null;
        }
        return null;
    }

    // ================================================================ premium

    private async Task<VenueMarketStats> LoadVenueMarketStatsAsync(string venue, string symbol, CancellationToken ct)
    {
        using var linked = Linked(ct);
        ct = linked.Token;
        VenueContract? contract = venue == Hyperliquid ? null : await ContractAsync(venue, symbol, ct).ConfigureAwait(false);
        string native = contract?.Native ?? "";
        string escaped = Uri.EscapeDataString(native);
        VenueMarketStats? stats;
        if (venue == Okx)
        {
            using var mark = await GetVenueJsonAsync(venue, $"{OkxOrigin}/api/v5/public/mark-price?instType=SWAP&instId={escaped}", null, ct).ConfigureAwait(false);
            // The swap's index is its spot pair: BTC-USDT-SWAP → BTC-USDT.
            using var index = await GetVenueJsonAsync(venue, $"{OkxOrigin}/api/v5/market/index-tickers?instId={Uri.EscapeDataString(native[..^"-SWAP".Length])}", null, ct).ConfigureAwait(false);
            using var funding = await GetVenueJsonAsync(venue, $"{OkxOrigin}/api/v5/public/funding-rate?instId={escaped}", null, ct).ConfigureAwait(false);
            stats = ParseOkxMarketStats(mark.RootElement, index.RootElement, funding.RootElement, _utcNow());
        }
        else if (venue == Bitget)
        {
            using var ticker = await GetVenueJsonAsync(venue, $"{BitgetOrigin}/api/v2/mix/market/ticker?symbol={escaped}&productType=USDT-FUTURES", null, ct).ConfigureAwait(false);
            using var funding = await GetVenueJsonAsync(venue, $"{BitgetOrigin}/api/v2/mix/market/current-fund-rate?symbol={escaped}&productType=USDT-FUTURES", null, ct).ConfigureAwait(false);
            stats = ParseBitgetMarketStats(ticker.RootElement, funding.RootElement, _utcNow());
        }
        else
        {
            using var doc = await HyperliquidInfoAsync(new { type = "metaAndAssetCtxs" }, ct).ConfigureAwait(false);
            contract = ResolveContract(ParseHyperliquidUniverse(doc.RootElement), symbol);
            stats = contract == null ? null : ParseHyperliquidMarketStats(doc.RootElement, contract.Native, _utcNow());
        }
        if (stats == null || contract == null) throw new InvalidDataException($"{VenueName(venue)} published no ticker for {symbol}.");
        // Prices per the requested symbol's unit, so they match its open interest; the premium is a ratio and unchanged.
        return contract.Scale == 1 ? stats : stats with { MarkPrice = stats.MarkPrice / contract.Scale, IndexPrice = stats.IndexPrice / contract.Scale };
    }

    /// <summary>
    /// OKX mark-price data[0] {markPx, ts}, index-tickers data[0] {idxPx} and funding-rate data[0] {fundingRate (fraction),
    /// fundingTime (the upcoming settlement)}; null when none of them has a row.
    /// </summary>
    public static VenueMarketStats? ParseOkxMarketStats(JsonElement mark, JsonElement index, JsonElement funding, DateTime fetchedUtc)
    {
        static JsonElement? First(JsonElement root) =>
            root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? data[0] : null;
        JsonElement? m = First(mark), i = First(index), f = First(funding);
        if (m == null && i == null && f == null) return null;
        DateTime time = m is { } mr && mr.TryGetProperty("ts", out var t) && Long(t) is { } ms and > 0 ? FromMs(ms) : fetchedUtc;
        return new VenueMarketStats(Okx, time, m is { } a ? Optional(a, "markPx") : null, i is { } b ? Optional(b, "idxPx") : null,
            f is { } c ? Optional(c, "fundingRate") * 100 : null,
            f is { } d && d.TryGetProperty("fundingTime", out var next) && Long(next) is { } n and > 0 ? FromMs(n) : null);
    }

    /// <summary>Bitget ticker data[0] {markPrice, indexPrice, fundingRate (fraction), ts} and current-fund-rate data[0] {nextUpdate}; null without a ticker.</summary>
    public static VenueMarketStats? ParseBitgetMarketStats(JsonElement ticker, JsonElement funding, DateTime fetchedUtc)
    {
        if (!ticker.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) return null;
        var row = data[0];
        DateTime time = row.TryGetProperty("ts", out var t) && Long(t) is { } ms and > 0 ? FromMs(ms) : fetchedUtc;
        DateTime? next = funding.TryGetProperty("data", out var fd) && fd.ValueKind == JsonValueKind.Array && fd.GetArrayLength() > 0
            && fd[0].TryGetProperty("nextUpdate", out var nu) && Long(nu) is { } n and > 0 ? FromMs(n) : null;
        return new VenueMarketStats(Bitget, time, Optional(row, "markPrice"), Optional(row, "indexPrice"), Optional(row, "fundingRate") * 100, next);
    }

    /// <summary>
    /// Hyperliquid metaAndAssetCtxs context {markPx, oraclePx, funding (hourly fraction)}; the oracle price is the index.
    /// Funding settles every hour on the hour, so the next settlement is the next full hour. Null when the coin is not listed.
    /// </summary>
    public static VenueMarketStats? ParseHyperliquidMarketStats(JsonElement root, string coin, DateTime fetchedUtc)
    {
        if (ParseHyperliquidContext(root, coin) is not { } context) return null;
        var hour = new DateTime(fetchedUtc.Ticks - fetchedUtc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc).AddHours(1);
        return new VenueMarketStats(Hyperliquid, fetchedUtc, Optional(context, "markPx"), Optional(context, "oraclePx"), Optional(context, "funding") * 100, hour);
    }

    // ================================================================ long/short

    private async Task<LongShortResult> LoadVenueLongShortAsync(string venue, string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        if (venue == Hyperliquid)
            return new(Array.Empty<LongShortSample>(), OpenInterestAvailability.Unsupported, $"{VenueName(venue)} does not publish a long/short account ratio.");
        using var linked = Linked(ct);
        ct = linked.Token;
        string escaped = Uri.EscapeDataString((await ContractAsync(venue, symbol, ct).ConfigureAwait(false)).Native);
        IReadOnlyList<LongShortSample> rows;
        if (venue == Okx)
        {
            string period = PeriodFor(OkxPeriods, to - from, 100).Name;
            using var doc = await GetVenueJsonAsync(venue, $"{OkxOrigin}/api/v5/rubik/stat/contracts/long-short-account-ratio-contract?instId={escaped}&period={period}&begin={Ms(from)}&end={Ms(to)}&limit=100",
                null, ct).ConfigureAwait(false);
            rows = ParseOkxLongShort(doc.RootElement).ToArray();
        }
        else
        {
            // The venue answers its latest 30 periods; there is no time-range parameter.
            string period = PeriodFor(BitgetPeriods, to - from, 30).Name;
            using var doc = await GetVenueJsonAsync(venue, $"{BitgetOrigin}/api/v2/mix/market/account-long-short?symbol={escaped}&productType=USDT-FUTURES&period={period}", null, ct).ConfigureAwait(false);
            rows = ParseBitgetLongShort(doc.RootElement).ToArray();
        }
        var values = rows.Where(r => r.Time >= from && r.Time <= to).OrderBy(r => r.Time).ToArray();
        return values.Length == 0
            ? new(values, OpenInterestAvailability.Unavailable, $"{VenueName(venue)} reports no long/short ratio for {symbol} in this range.")
            : new(values, OpenInterestAvailability.Available);
    }

    /// <summary>
    /// OKX long-short-account-ratio-contract rows [ts, ratio], newest first. OKX publishes the ratio only; the shares
    /// are ratio / (1 + ratio) and 1 / (1 + ratio), so <see cref="LongShortSample.Ratio"/> reproduces it.
    /// </summary>
    public static IEnumerable<LongShortSample> ParseOkxLongShort(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in data.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 2 && Long(row[0]) is { } ms && Number(row[1]) is { } ratio && ratio > 0)
                yield return new LongShortSample(FromMs(ms), ratio / (1 + ratio), 1 / (1 + ratio));
    }

    /// <summary>Bitget account-long-short data rows {longAccountRatio, shortAccountRatio (fractions), ts}, oldest first.</summary>
    public static IEnumerable<LongShortSample> ParseBitgetLongShort(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in data.EnumerateArray())
            if (e.TryGetProperty("ts", out var t) && Long(t) is { } ms && Optional(e, "longAccountRatio") is { } l && Optional(e, "shortAccountRatio") is { } s)
                yield return new LongShortSample(FromMs(ms), l, s);
    }

    // ================================================================ funding board

    /// <summary>
    /// Current funding of many symbols on one venue in one or two bulk requests: Binance <c>/fapi/v1/premiumIndex</c> with
    /// <c>/fapi/v1/fundingInfo</c> (symbols absent there settle every 8 hours), Bybit <c>/v5/market/tickers</c>, OKX
    /// <c>/api/v5/public/funding-rate?instId=ANY</c>, Bitget <c>/api/v2/mix/market/current-fund-rate</c> and Hyperliquid
    /// <c>metaAndAssetCtxs</c> (hourly). Keyed by the requested derivatives symbol; symbols the venue does not list are absent.
    /// Bitget and Hyperliquid symbols are resolved through their listings, so a coin quoted in another unit (PEPEUSDT or
    /// kPEPE for 1000PEPEUSDT) is found; funding is a rate and does not depend on the unit.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FundingSnapshot>> LoadFundingBoardAsync(string venue, IReadOnlyCollection<string> symbols, CancellationToken ct)
    {
        using var linked = Linked(ct);
        ct = linked.Token;
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        void Want(IReadOnlyList<(string Native, string Base)> listed)
        {
            foreach (var s in symbols)
                if (ResolveContract(listed, s) is { } contract) wanted.TryAdd(contract.Native, s.ToUpperInvariant());
        }
        if (venue is Binance or Bybit)
            foreach (var s in symbols) wanted.TryAdd(s.ToUpperInvariant(), s.ToUpperInvariant());
        else if (venue == Okx)
            foreach (var s in symbols) wanted.TryAdd(OkxNativeSymbol(s), s.ToUpperInvariant());
        else if (venue == Bitget)
            Want(await ListedAsync(_bitgetContracts, LoadBitgetContractsAsync, ct).ConfigureAwait(false));
        switch (venue)
        {
            case Binance:
            {
                using var premium = await GetAsync($"{BinanceOrigin}/fapi/v1/premiumIndex", ct).ConfigureAwait(false);
                using var info = await GetAsync($"{BinanceOrigin}/fapi/v1/fundingInfo", ct).ConfigureAwait(false);
                return ParseBinanceFundingBoard(premium.RootElement, info.RootElement, wanted);
            }
            case Bybit:
            {
                using var tickers = await GetAsync($"{BybitOrigin}/v5/market/tickers?category=linear", ct).ConfigureAwait(false);
                return ParseBybitFundingBoard(tickers.RootElement, wanted);
            }
            case Okx:
            {
                using var doc = await GetVenueJsonAsync(venue, $"{OkxOrigin}/api/v5/public/funding-rate?instId=ANY", null, ct).ConfigureAwait(false);
                return ParseOkxFundingBoard(doc.RootElement, wanted);
            }
            case Bitget:
            {
                using var doc = await GetVenueJsonAsync(venue, $"{BitgetOrigin}/api/v2/mix/market/current-fund-rate?productType=USDT-FUTURES", null, ct).ConfigureAwait(false);
                return ParseBitgetFundingBoard(doc.RootElement, wanted);
            }
            case Hyperliquid:
            {
                using var doc = await HyperliquidInfoAsync(new { type = "metaAndAssetCtxs" }, ct).ConfigureAwait(false);
                Want(ParseHyperliquidUniverse(doc.RootElement));
                return ParseHyperliquidFundingBoard(doc.RootElement, wanted);
            }
            default:
                throw new ArgumentException("Unknown derivatives venue.", nameof(venue));
        }
    }

    /// <summary>Binance premiumIndex rows {symbol, lastFundingRate, nextFundingTime}; fundingInfo rows {symbol, fundingIntervalHours} list only adjusted intervals.</summary>
    public static IReadOnlyDictionary<string, FundingSnapshot> ParseBinanceFundingBoard(JsonElement premium, JsonElement info, IReadOnlyDictionary<string, string> wanted)
    {
        var hours = new Dictionary<string, double>(StringComparer.Ordinal);
        if (info.ValueKind == JsonValueKind.Array)
            foreach (var e in info.EnumerateArray())
                if (Text(e, "symbol") is { } s && Optional(e, "fundingIntervalHours") is { } h and > 0) hours[s] = (double)h;
        var result = new Dictionary<string, FundingSnapshot>(StringComparer.Ordinal);
        if (premium.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in premium.EnumerateArray())
            if (Text(e, "symbol") is { } native && wanted.TryGetValue(native, out var symbol) && Optional(e, "lastFundingRate") is { } rate)
                result[symbol] = new FundingSnapshot(Binance, symbol, rate * 100, hours.GetValueOrDefault(native, 8), NextTime(e, "nextFundingTime"));
        return result;
    }

    /// <summary>Bybit tickers result.list rows {symbol, fundingRate, fundingIntervalHour, nextFundingTime}; rows without a rate (dated futures) are skipped.</summary>
    public static IReadOnlyDictionary<string, FundingSnapshot> ParseBybitFundingBoard(JsonElement root, IReadOnlyDictionary<string, string> wanted)
    {
        var result = new Dictionary<string, FundingSnapshot>(StringComparer.Ordinal);
        if (!root.TryGetProperty("result", out var r) || !r.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in list.EnumerateArray())
            if (Text(e, "symbol") is { } native && wanted.TryGetValue(native, out var symbol) && Optional(e, "fundingRate") is { } rate
                && Optional(e, "fundingIntervalHour") is { } h and > 0)
                result[symbol] = new FundingSnapshot(Bybit, symbol, rate * 100, (double)h, NextTime(e, "nextFundingTime"));
        return result;
    }

    /// <summary>OKX funding-rate data rows {instId, fundingRate, fundingTime (upcoming), nextFundingTime}; the interval is their difference.</summary>
    public static IReadOnlyDictionary<string, FundingSnapshot> ParseOkxFundingBoard(JsonElement root, IReadOnlyDictionary<string, string> wanted)
    {
        var result = new Dictionary<string, FundingSnapshot>(StringComparer.Ordinal);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in data.EnumerateArray())
        {
            if (Text(e, "instId") is not { } native || !wanted.TryGetValue(native, out var symbol) || Optional(e, "fundingRate") is not { } rate) continue;
            DateTime? next = NextTime(e, "fundingTime"), after = NextTime(e, "nextFundingTime");
            // Without both settlement times the interval is unknown, and so is the 8-hour rate.
            if (next is not { } a || after is not { } b || b <= a) continue;
            result[symbol] = new FundingSnapshot(Okx, symbol, rate * 100, (b - a).TotalHours, next);
        }
        return result;
    }

    /// <summary>Bitget current-fund-rate data rows {symbol, fundingRate, fundingRateInterval (hours), nextUpdate}.</summary>
    public static IReadOnlyDictionary<string, FundingSnapshot> ParseBitgetFundingBoard(JsonElement root, IReadOnlyDictionary<string, string> wanted)
    {
        var result = new Dictionary<string, FundingSnapshot>(StringComparer.Ordinal);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in data.EnumerateArray())
            if (Text(e, "symbol") is { } native && wanted.TryGetValue(native, out var symbol) && Optional(e, "fundingRate") is { } rate
                && Optional(e, "fundingRateInterval") is { } h and > 0)
                result[symbol] = new FundingSnapshot(Bitget, symbol, rate * 100, (double)h, NextTime(e, "nextUpdate"));
        return result;
    }

    /// <summary>Hyperliquid metaAndAssetCtxs: each listed coin's hourly funding.</summary>
    public static IReadOnlyDictionary<string, FundingSnapshot> ParseHyperliquidFundingBoard(JsonElement root, IReadOnlyDictionary<string, string> wanted)
    {
        var result = new Dictionary<string, FundingSnapshot>(StringComparer.Ordinal);
        foreach (var (native, symbol) in wanted)
            if (ParseHyperliquidContext(root, native) is { } context && Optional(context, "funding") is { } rate)
                result[symbol] = new FundingSnapshot(Hyperliquid, symbol, rate * 100, 1, null);
        return result;
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? NextTime(JsonElement e, string name) => e.TryGetProperty(name, out var v) && Long(v) is { } ms and > 0 ? FromMs(ms) : null;

    private static decimal? Number(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.String => decimal.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        _ => null,
    };
}
