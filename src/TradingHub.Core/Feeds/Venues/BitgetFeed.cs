using System.Globalization;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Venues;

/// <summary>
/// Bitget public data: USDT futures or spot; full books; sizes already in base currency. The futures book is chained by
/// seq/pseq; the spot book carries only an increasing seq (no pseq, and the checksum was removed in May 2026), so lost
/// spot updates cannot be detected and the spot book is disclosed as not event-complete.
/// </summary>
public sealed class BitgetFeed(bool spot, PublicVenueTransport? transport = null) : PublicVenueFeed(transport)
{
    public const string FuturesId = "bitget-futures";
    public const string SpotId = "bitget-spot";
    public override string Id => spot ? SpotId : FuturesId;
    public override string DisplayName => spot ? "Bitget Spot" : "Bitget";
    public override string Venue => "Bitget";
    public override bool IsSpot => spot;
    private string InstType => spot ? "SPOT" : "USDT-FUTURES";
    protected override Uri DefaultRestOrigin => new("https://api.bitget.com");
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(25), "ping");
    // https://www.bitget.com/docs/classic/websocket/spot/public/Depth-Channel: spot books push seq without pseq.
    protected override DepthCoverage BookCoverage => spot ? DepthCoverage.PartialBook : DepthCoverage.None;

    protected override async Task<IReadOnlyList<VenueInstrument>> ListAsync(CancellationToken ct)
    {
        string path = spot ? "/api/v2/spot/public/symbols" : "/api/v2/mix/market/contracts?productType=USDT-FUTURES";
        using var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
        return ParseInstruments(doc.RootElement, spot, Id, DisplayName);
    }

    public static IReadOnlyList<VenueInstrument> ParseInstruments(JsonElement root, bool spot, string feedId, string exchange)
    {
        var result = new List<VenueInstrument>();
        foreach (var s in root.GetProperty("data").EnumerateArray())
        {
            string status = S(s, spot ? "status" : "symbolStatus");
            if (status is not ("online" or "normal")) continue;
            string quote = S(s, "quoteCoin"), baseAsset = S(s, "baseCoin"), symbol = S(s, "symbol");
            if (!IsUsdQuote(quote)) continue;
            decimal tick = spot
                ? Pow10(-(int)D(s, "pricePrecision"))
                : Math.Max(1, D(s, "priceEndStep", 1)) * Pow10(-(int)D(s, "pricePlace"));
            decimal step = spot ? Pow10(-(int)D(s, "quantityPrecision")) : D(s, "sizeMultiplier");
            if (tick <= 0 || step <= 0) continue;
            result.Add(new VenueInstrument(new Instrument
            {
                Symbol = symbol, FeedId = feedId, Exchange = exchange,
                TickSize = Norm(tick), VolumeStep = Norm(step),
                BaseAsset = baseAsset, QuoteAsset = quote,
                Description = $"{baseAsset}/{quote} {(spot ? "spot" : "perpetual")}",
            }, new VenueSpec(symbol)));
        }
        return result;
    }

    protected override Uri SocketEndpoint(Instrument instrument, VenueSpec spec) => new("wss://ws.bitget.com/v2/ws/public");

    protected override IEnumerable<string> SubscribeFrames(Instrument instrument, VenueSpec spec)
    {
        yield return $"{{\"op\":\"subscribe\",\"args\":[{{\"instType\":\"{InstType}\",\"channel\":\"books\",\"instId\":\"{spec.Native}\"}},{{\"instType\":\"{InstType}\",\"channel\":\"trade\",\"instId\":\"{spec.Native}\"}}]}}";
    }

    protected override void OnMessage(string text, VenueSession s)
    {
        if (text == "pong") return;
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        if (S(m, "event") == "error") throw new InvalidDataException("Bitget: " + S(m, "msg"));
        if (!m.TryGetProperty("arg", out var arg) || !m.TryGetProperty("data", out var data)) return;
        string channel = S(arg, "channel");
        bool snapshot = S(m, "action") == "snapshot";
        if (channel == "trade")
        {
            if (snapshot) return; // The latest prints from before this connection.
            foreach (var d in data.EnumerateArray())
                s.Trade(Ms(D(d, "ts")), D(d, "price"), D(d, "size"), S(d, "side") == "buy", S(d, "tradeId"));
            return;
        }
        if (channel != "books") return;
        foreach (var d in data.EnumerateArray())
        {
            // Futures updates name the previous push (pseq); spot updates carry only an increasing seq.
            long seq = (long)D(d, "seq"), previous = (long)D(d, "pseq");
            if (snapshot) s.Clear();
            else if (s.Seq <= 0) continue;
            else if (seq == s.Seq) continue; // A replayed update already applied.
            else if (previous != 0)
            {
                if (seq < s.Seq) continue; // A replayed update already applied.
                if (previous != s.Seq) VenueSession.Resync($"{s.Seq} → {previous}");
            }
            else if (seq < s.Seq) VenueSession.Resync($"out of order: {seq} after {s.Seq}");
            s.Seq = seq;
            s.Levels(BookSide.Bid, d.GetProperty("bids"));
            s.Levels(BookSide.Ask, d.GetProperty("asks"));
        }
    }

    protected override async Task<IReadOnlyList<Kline>> FetchKlinesAsync(Instrument instrument, VenueSpec spec, long fromMs, long toMs, int minutes, CancellationToken ct)
    {
        var bars = new List<Kline>();
        long cursor = toMs;
        // Spot: 1min, 5min, 15min, 1h; futures: 1m, 5m, 15m, 1H.
        string granularity = spot ? (minutes == 60 ? "1h" : $"{minutes}min") : (minutes == 60 ? "1H" : $"{minutes}m");
        for (int page = 0; page < 10_000 && cursor > fromMs; page++)
        {
            ct.ThrowIfCancellationRequested();
            string path = spot
                ? $"/api/v2/spot/market/history-candles?symbol={Uri.EscapeDataString(spec.Native)}&granularity={granularity}&endTime={cursor}&limit=200"
                : $"/api/v2/mix/market/history-candles?symbol={Uri.EscapeDataString(spec.Native)}&productType=USDT-FUTURES&granularity={granularity}&endTime={cursor}&limit=200";
            using var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
            var rows = ParseCandles(doc.RootElement);
            var older = rows.Where(b => b.OpenTimeMs < cursor).ToList();
            if (older.Count == 0) break;
            bars.AddRange(older);
            cursor = older.Min(b => b.OpenTimeMs);
        }
        return bars;
    }

    /// <summary>Rows [ts, o, h, l, c, baseVolume, …].</summary>
    public static IReadOnlyList<Kline> ParseCandles(JsonElement root)
    {
        var result = new List<Kline>();
        foreach (var r in root.GetProperty("data").EnumerateArray())
            result.Add(new Kline(long.Parse(r[0].GetString()!, CultureInfo.InvariantCulture), D(r[1]), D(r[2]), D(r[3]), D(r[4]), D(r[5]), 0, false));
        return result;
    }
}
