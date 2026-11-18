using System.Globalization;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Venues;

/// <summary>OKX public data: linear USD-family swaps (sizes in contracts × ctVal) or spot; 400-level book chained by prevSeqId.</summary>
public sealed class OkxFeed(bool spot, PublicVenueTransport? transport = null) : PublicVenueFeed(transport)
{
    public const string SwapId = "okx-swap";
    public const string SpotId = "okx-spot";
    public override string Id => spot ? SpotId : SwapId;
    public override string DisplayName => spot ? "OKX Spot" : "OKX";
    public override string Venue => "OKX";
    public override bool IsSpot => spot;
    protected override Uri DefaultRestOrigin => new("https://www.okx.com");
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(20), "ping");
    // The books channel is the venue's 400-level depth, not an event-complete book.
    protected override DepthCoverage BookCoverage => DepthCoverage.PartialBook;

    protected override async Task<IReadOnlyList<VenueInstrument>> ListAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/api/v5/public/instruments?instType={(spot ? "SPOT" : "SWAP")}", ct).ConfigureAwait(false);
        return ParseInstruments(doc.RootElement, spot, Id, DisplayName);
    }

    /// <summary>Parses /api/v5/public/instruments; inverse swaps and non-USD quotes are skipped.</summary>
    public static IReadOnlyList<VenueInstrument> ParseInstruments(JsonElement root, bool spot, string feedId, string exchange)
    {
        var result = new List<VenueInstrument>();
        foreach (var s in root.GetProperty("data").EnumerateArray())
        {
            if (S(s, "state") != "live") continue;
            string id = S(s, "instId"), baseAsset, quote;
            decimal multiplier = 1;
            if (spot)
            {
                baseAsset = S(s, "baseCcy");
                quote = S(s, "quoteCcy");
            }
            else
            {
                if (S(s, "ctType") != "linear") continue;
                baseAsset = S(s, "ctValCcy");
                quote = S(s, "settleCcy");
                multiplier = D(s, "ctVal");
                if (multiplier <= 0) continue;
            }
            decimal tick = D(s, "tickSz"), lot = D(s, "lotSz");
            if (!IsUsdQuote(quote) || tick <= 0 || lot <= 0) continue;
            result.Add(new VenueInstrument(new Instrument
            {
                Symbol = id, FeedId = feedId, Exchange = exchange,
                TickSize = Norm(tick), VolumeStep = Norm(lot * multiplier),
                BaseAsset = baseAsset, QuoteAsset = quote,
                Description = $"{baseAsset}/{quote} {(spot ? "spot" : "perpetual")}",
            }, new VenueSpec(id, multiplier)));
        }
        return result;
    }

    protected override Uri SocketEndpoint(Instrument instrument, VenueSpec spec) => new("wss://ws.okx.com:8443/ws/v5/public");

    protected override IEnumerable<string> SubscribeFrames(Instrument instrument, VenueSpec spec)
    {
        yield return $"{{\"op\":\"subscribe\",\"args\":[{{\"channel\":\"books\",\"instId\":\"{spec.Native}\"}},{{\"channel\":\"trades\",\"instId\":\"{spec.Native}\"}}]}}";
    }

    protected override void OnMessage(string text, VenueSession s)
    {
        if (text == "pong") return;
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        if (S(m, "event") == "error") throw new InvalidDataException("OKX: " + S(m, "msg"));
        if (!m.TryGetProperty("arg", out var arg) || !m.TryGetProperty("data", out var data)) return;
        string channel = S(arg, "channel");
        if (channel == "trades")
        {
            foreach (var d in data.EnumerateArray())
                s.Trade(Ms(D(d, "ts")), D(d, "px"), D(d, "sz"), S(d, "side") == "buy", S(d, "tradeId"));
            return;
        }
        if (channel != "books") return;
        bool snapshot = S(m, "action") == "snapshot";
        foreach (var d in data.EnumerateArray())
        {
            long seq = (long)D(d, "seqId"), previous = (long)D(d, "prevSeqId", -1);
            if (snapshot) s.Clear();
            else if (s.Seq < 0) continue;
            else if (previous != s.Seq) VenueSession.Resync($"{s.Seq} → {previous}");
            s.Seq = seq;
            s.Levels(BookSide.Bid, d.GetProperty("bids"));
            s.Levels(BookSide.Ask, d.GetProperty("asks"));
        }
    }

    protected override async Task<IReadOnlyList<Kline>> FetchKlinesAsync(Instrument instrument, VenueSpec spec, long fromMs, long toMs, int minutes, CancellationToken ct)
    {
        var bars = new List<Kline>();
        long cursor = toMs;
        string bar = minutes == 60 ? "1H" : $"{minutes}m"; // OKX writes hours in upper case.
        for (int page = 0; page < 10_000 && cursor > fromMs; page++)
        {
            ct.ThrowIfCancellationRequested();
            // history-candles pages backwards: "after" returns bars strictly older than the cursor.
            using var doc = await GetJsonAsync($"/api/v5/market/history-candles?instId={Uri.EscapeDataString(spec.Native)}&bar={bar}&after={cursor}&limit=100", ct).ConfigureAwait(false);
            var page0 = ParseCandles(doc.RootElement, spot);
            if (page0.Count == 0) break;
            bars.AddRange(page0);
            long oldest = page0.Min(b => b.OpenTimeMs);
            if (oldest >= cursor) break;
            cursor = oldest;
        }
        return bars;
    }

    /// <summary>Rows [ts, o, h, l, c, vol, volCcy, …]; swap volume in base currency is volCcy, spot volume is vol.</summary>
    public static IReadOnlyList<Kline> ParseCandles(JsonElement root, bool spot)
    {
        var result = new List<Kline>();
        foreach (var r in root.GetProperty("data").EnumerateArray())
            result.Add(new Kline(long.Parse(r[0].GetString()!, CultureInfo.InvariantCulture), D(r[1]), D(r[2]), D(r[3]), D(r[4]),
                spot ? D(r[5]) : D(r[6]), 0, false));
        return result;
    }
}
