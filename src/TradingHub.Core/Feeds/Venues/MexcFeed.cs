using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Venues;

/// <summary>
/// MEXC public USDT perpetual data: the best 20 levels per message and deals; sizes are contracts × contractSize.
/// Deals carry no trade identifier, so each reception is a distinct, partially identified observation.
/// </summary>
public sealed class MexcFeed(PublicVenueTransport? transport = null) : PublicVenueFeed(transport)
{
    public const string PerpId = "mexc";
    public override string Id => PerpId;
    public override string DisplayName => "MEXC";
    public override string Venue => "MEXC";
    public override bool IsSpot => false;
    protected override Uri DefaultRestOrigin => new("https://contract.mexc.com");
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(15), "{\"method\":\"ping\"}");
    protected override DepthCoverage BookCoverage => DepthCoverage.PartialBook | DepthCoverage.Truncated;

    protected override async Task<IReadOnlyList<VenueInstrument>> ListAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/api/v1/contract/detail", ct).ConfigureAwait(false);
        return ParseInstruments(doc.RootElement, Id, DisplayName);
    }

    public static IReadOnlyList<VenueInstrument> ParseInstruments(JsonElement root, string feedId, string exchange)
    {
        var result = new List<VenueInstrument>();
        foreach (var s in root.GetProperty("data").EnumerateArray())
        {
            if (D(s, "state") != 0) continue;
            // USDT-margined only; BTC_USD contracts are inverse and sized in USD.
            string quote = S(s, "quoteCoin"), settle = S(s, "settleCoin");
            if (quote != "USDT" || settle.Length > 0 && settle != "USDT") continue;
            decimal size = D(s, "contractSize"), tick = D(s, "priceUnit");
            if (size <= 0 || tick <= 0) continue;
            string symbol = S(s, "symbol"), baseAsset = S(s, "baseCoin");
            result.Add(new VenueInstrument(new Instrument
            {
                Symbol = symbol, FeedId = feedId, Exchange = exchange,
                TickSize = Norm(tick), VolumeStep = Norm(size * Math.Max(1, D(s, "volUnit", 1))),
                BaseAsset = baseAsset, QuoteAsset = quote,
                Description = $"{baseAsset}/{quote} perpetual",
            }, new VenueSpec(symbol, size)));
        }
        return result;
    }

    protected override Uri SocketEndpoint(Instrument instrument, VenueSpec spec) => new("wss://contract.mexc.com/edge");

    protected override IEnumerable<string> SubscribeFrames(Instrument instrument, VenueSpec spec)
    {
        yield return $"{{\"method\":\"sub.depth.full\",\"param\":{{\"symbol\":\"{spec.Native}\",\"limit\":20}}}}";
        yield return $"{{\"method\":\"sub.deal\",\"param\":{{\"symbol\":\"{spec.Native}\"}}}}";
    }

    protected override void OnMessage(string text, VenueSession s)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        string channel = S(m, "channel");
        if (channel == "rs.error") throw new InvalidDataException("MEXC: " + S(m, "data"));
        if (m.TryGetProperty("symbol", out var symbol) && symbol.GetString() != s.Spec.Native) return;
        if (channel == "push.depth.full")
        {
            var d = m.GetProperty("data");
            s.Clear();
            s.Levels(BookSide.Bid, d.GetProperty("bids"));
            s.Levels(BookSide.Ask, d.GetProperty("asks"));
            return;
        }
        if (channel != "push.deal") return;
        var data = m.GetProperty("data");
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var d in data.EnumerateArray()) Deal(s, d);
        else Deal(s, data);
    }

    // T: aggressor side (1 buy, 2 sell); v: contracts.
    private static void Deal(VenueSession s, JsonElement d) => s.Trade(Ms(D(d, "t")), D(d, "p"), D(d, "v"), D(d, "T") == 1, null);

    protected override async Task<IReadOnlyList<Kline>> FetchKlinesAsync(Instrument instrument, VenueSpec spec, long fromMs, long toMs, int minutes, CancellationToken ct)
    {
        var bars = new List<Kline>();
        long window = 2000 * minutes * 60_000L;
        for (long start = fromMs; start < toMs; start += window)
        {
            ct.ThrowIfCancellationRequested();
            long end = Math.Min(toMs, start + window) - 1;
            using var doc = await GetJsonAsync($"/api/v1/contract/kline/{Uri.EscapeDataString(spec.Native)}?interval=Min{minutes}&start={start / 1000}&end={end / 1000}", ct).ConfigureAwait(false);
            bars.AddRange(ParseCandles(doc.RootElement, spec.Multiplier));
        }
        return bars;
    }

    /// <summary>Columnar data {time (s), open, high, low, close, vol (contracts)}.</summary>
    public static IReadOnlyList<Kline> ParseCandles(JsonElement root, decimal contractSize)
    {
        var d = root.GetProperty("data");
        var time = d.GetProperty("time");
        var result = new List<Kline>();
        for (int i = 0; i < time.GetArrayLength(); i++)
            result.Add(new Kline((long)D(time[i]) * 1000, D(d.GetProperty("open")[i]), D(d.GetProperty("high")[i]), D(d.GetProperty("low")[i]),
                D(d.GetProperty("close")[i]), D(d.GetProperty("vol")[i]) * contractSize, 0, false));
        return result;
    }
}
