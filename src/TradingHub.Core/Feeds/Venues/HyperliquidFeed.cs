using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Venues;

/// <summary>
/// Hyperliquid public perpetual data (USD-settled): the l2Book stream is a full top-20 snapshot per message at exact
/// prices, plus trades. Thousand-unit coins (kPEPE) use the 1000PEPE base asset, as Binance does.
/// </summary>
public sealed class HyperliquidFeed(PublicVenueTransport? transport = null) : PublicVenueFeed(transport)
{
    public const string PerpId = "hyperliquid";
    public override string Id => PerpId;
    public override string DisplayName => "Hyperliquid";
    public override string Venue => "Hyperliquid";
    public override bool IsSpot => false;
    public override string PreferredQuote => "USD";
    protected override Uri DefaultRestOrigin => new("https://api.hyperliquid.xyz");
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(30), "{\"method\":\"ping\"}");
    protected override DepthCoverage BookCoverage => DepthCoverage.PartialBook | DepthCoverage.Truncated;

    protected override async Task<IReadOnlyList<VenueInstrument>> ListAsync(CancellationToken ct)
    {
        using var doc = await PostJsonAsync("/info", "{\"type\":\"metaAndAssetCtxs\"}", ct).ConfigureAwait(false);
        return ParseInstruments(doc.RootElement, Id, DisplayName);
    }

    public static IReadOnlyList<VenueInstrument> ParseInstruments(JsonElement root, string feedId, string exchange)
    {
        var universe = root[0].GetProperty("universe");
        var contexts = root[1];
        var result = new List<VenueInstrument>();
        int index = 0;
        foreach (var u in universe.EnumerateArray())
        {
            int position = index++;
            if (u.TryGetProperty("isDelisted", out var delisted) && delisted.ValueKind == JsonValueKind.True) continue;
            string coin = S(u, "name");
            int sizeDecimals = (int)D(u, "szDecimals");
            decimal mark = position < contexts.GetArrayLength() ? D(contexts[position], "markPx") : 0;
            string baseAsset = coin.Length > 1 && coin[0] == 'k' && char.IsUpper(coin[1]) ? "1000" + coin[1..] : coin;
            result.Add(new VenueInstrument(new Instrument
            {
                Symbol = coin, FeedId = feedId, Exchange = exchange,
                TickSize = Norm(TickFor(mark, sizeDecimals)), VolumeStep = Norm(Pow10(-sizeDecimals)),
                BaseAsset = baseAsset, QuoteAsset = "USD",
                Description = $"{coin}/USD perpetual",
            }, new VenueSpec(coin)));
        }
        return result;
    }

    /// <summary>
    /// Hyperliquid prices have at most five significant figures and 6 − szDecimals decimals. The fixed instrument tick
    /// is one decimal finer than five figures at the current mark, so prices stay exact if the market falls tenfold.
    /// </summary>
    public static decimal TickFor(decimal price, int sizeDecimals)
    {
        decimal minimum = Pow10(-Math.Max(0, 6 - sizeDecimals));
        if (price <= 0) return minimum;
        int magnitude = (int)Math.Floor(Math.Log10((double)price));
        return Math.Max(minimum, Pow10(magnitude - 5));
    }

    protected override Uri SocketEndpoint(Instrument instrument, VenueSpec spec) => new("wss://api.hyperliquid.xyz/ws");

    protected override IEnumerable<string> SubscribeFrames(Instrument instrument, VenueSpec spec)
    {
        yield return $"{{\"method\":\"subscribe\",\"subscription\":{{\"type\":\"l2Book\",\"coin\":\"{spec.Native}\"}}}}";
        yield return $"{{\"method\":\"subscribe\",\"subscription\":{{\"type\":\"trades\",\"coin\":\"{spec.Native}\"}}}}";
    }

    protected override void OnMessage(string text, VenueSession s)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        string channel = S(m, "channel");
        if (channel == "error") throw new InvalidDataException("Hyperliquid: " + S(m, "data"));
        if (channel == "l2Book")
        {
            var data = m.GetProperty("data");
            if (S(data, "coin") != s.Spec.Native) return;
            var levels = data.GetProperty("levels");
            if (levels.GetArrayLength() < 2) return;
            s.Clear();
            foreach (var l in levels[0].EnumerateArray()) s.Level(BookSide.Bid, D(l, "px"), D(l, "sz"));
            foreach (var l in levels[1].EnumerateArray()) s.Level(BookSide.Ask, D(l, "px"), D(l, "sz"));
            return;
        }
        if (channel == "trades")
            foreach (var d in m.GetProperty("data").EnumerateArray())
            {
                if (S(d, "coin") != s.Spec.Native) continue;
                s.Trade(Ms(D(d, "time")), D(d, "px"), D(d, "sz"), S(d, "side") == "B", S(d, "tid"));
            }
    }

    /// <summary>Candles the venue keeps per interval; earlier ones are unavailable, not absent.</summary>
    public const int CandleWindow = 5000;

    protected override async Task<IReadOnlyList<Kline>> FetchKlinesAsync(Instrument instrument, VenueSpec spec, long fromMs, long toMs, int minutes, CancellationToken ct)
    {
        // The venue serves only its latest 5000 candles per interval. A range that starts before that window is read
        // from the window's start, and its bars are marked partial so caches and maintenance record the missing start
        // as unknown rather than as a period without trading.
        long step = minutes * 60_000L;
        long horizon = (Clock.GetUtcNow().ToUnixTimeMilliseconds() / step - (CandleWindow - 1)) * step;
        bool partial = fromMs < horizon;
        string interval = minutes == 60 ? "1h" : $"{minutes}m";
        var bars = new List<Kline>();
        long window = CandleWindow * step;
        for (long start = Math.Max(fromMs, horizon); start < toMs; start += window)
        {
            ct.ThrowIfCancellationRequested();
            long end = Math.Min(toMs, start + window) - 1;
            string body = $"{{\"type\":\"candleSnapshot\",\"req\":{{\"coin\":\"{spec.Native}\",\"interval\":\"{interval}\",\"startTime\":{start},\"endTime\":{end}}}}}";
            using var doc = await PostJsonAsync("/info", body, ct).ConfigureAwait(false);
            bars.AddRange(ParseCandles(doc.RootElement));
        }
        if (!partial) return bars;
        return bars.Select(b => b with { Provenance = b.Provenance with { Quality = b.Provenance.Quality | DataQuality.Partial } }).ToList();
    }

    /// <summary>Objects {t, o, h, l, c, v}; v is base-asset volume.</summary>
    public static IReadOnlyList<Kline> ParseCandles(JsonElement root)
    {
        var result = new List<Kline>();
        foreach (var c in root.EnumerateArray())
            result.Add(new Kline((long)D(c, "t"), D(c, "o"), D(c, "h"), D(c, "l"), D(c, "c"), D(c, "v"), 0, false));
        return result;
    }
}
