using System.Globalization;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Venues;

/// <summary>
/// Coinbase Exchange public spot data: level2_batch book and matches. A match reports the resting (maker) order's side,
/// so the aggressor is the opposite side.
/// </summary>
public sealed class CoinbaseFeed(PublicVenueTransport? transport = null) : PublicVenueFeed(transport)
{
    public const string SpotId = "coinbase";
    public override string Id => SpotId;
    public override string DisplayName => "Coinbase";
    public override string Venue => "Coinbase";
    public override bool IsSpot => true;
    public override string PreferredQuote => "USD";
    protected override Uri DefaultRestOrigin => new("https://api.exchange.coinbase.com");

    protected override async Task<IReadOnlyList<VenueInstrument>> ListAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/products", ct).ConfigureAwait(false);
        return ParseInstruments(doc.RootElement, Id, DisplayName);
    }

    public static IReadOnlyList<VenueInstrument> ParseInstruments(JsonElement root, string feedId, string exchange)
    {
        var result = new List<VenueInstrument>();
        foreach (var s in root.EnumerateArray())
        {
            if (S(s, "status") != "online" || s.TryGetProperty("trading_disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True) continue;
            string quote = S(s, "quote_currency"), baseAsset = S(s, "base_currency"), id = S(s, "id");
            decimal tick = D(s, "quote_increment"), step = D(s, "base_increment");
            if (!IsUsdQuote(quote) || tick <= 0 || step <= 0) continue;
            result.Add(new VenueInstrument(new Instrument
            {
                Symbol = id, FeedId = feedId, Exchange = exchange,
                TickSize = Norm(tick), VolumeStep = Norm(step),
                BaseAsset = baseAsset, QuoteAsset = quote,
                Description = $"{baseAsset}/{quote} spot",
            }, new VenueSpec(id)));
        }
        return result;
    }

    protected override Uri SocketEndpoint(Instrument instrument, VenueSpec spec) => new("wss://ws-feed.exchange.coinbase.com");

    protected override IEnumerable<string> SubscribeFrames(Instrument instrument, VenueSpec spec)
    {
        yield return $"{{\"type\":\"subscribe\",\"product_ids\":[\"{spec.Native}\"],\"channels\":[\"level2_batch\",\"matches\",\"heartbeat\"]}}";
    }

    protected override void OnMessage(string text, VenueSession s)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        switch (S(m, "type"))
        {
            case "snapshot":
                s.Clear();
                s.Levels(BookSide.Bid, m.GetProperty("bids"));
                s.Levels(BookSide.Ask, m.GetProperty("asks"));
                break;
            case "l2update":
                foreach (var c in m.GetProperty("changes").EnumerateArray())
                    s.Level(c[0].GetString() == "buy" ? BookSide.Bid : BookSide.Ask, D(c[1]), D(c[2]));
                break;
            case "match":
                // The reported side is the maker's; the aggressor took the other side.
                s.Trade(DateTime.Parse(S(m, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    D(m, "price"), D(m, "size"), S(m, "side") == "sell", S(m, "trade_id"));
                break;
            case "error":
                throw new InvalidDataException("Coinbase: " + S(m, "message") + " " + S(m, "reason"));
        }
    }

    protected override async Task<IReadOnlyList<Kline>> FetchKlinesAsync(Instrument instrument, VenueSpec spec, long fromMs, long toMs, int minutes, CancellationToken ct)
    {
        var bars = new List<Kline>();
        long step = minutes * 60_000L, window = 300 * step;
        for (long start = fromMs; start < toMs; start += window)
        {
            ct.ThrowIfCancellationRequested();
            long end = Math.Min(toMs, start + window) - 1;
            string from = Ms(start).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string to = Ms(end).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            using var doc = await GetJsonAsync($"/products/{Uri.EscapeDataString(spec.Native)}/candles?granularity={minutes * 60}&start={from}&end={to}", ct).ConfigureAwait(false);
            bars.AddRange(ParseCandles(doc.RootElement));
        }
        return bars;
    }

    /// <summary>Rows [time (s), low, high, open, close, volume], newest first.</summary>
    public static IReadOnlyList<Kline> ParseCandles(JsonElement root)
    {
        var result = new List<Kline>();
        foreach (var r in root.EnumerateArray())
            result.Add(new Kline((long)D(r[0]) * 1000, D(r[3]), D(r[2]), D(r[1]), D(r[4]), D(r[5]), 0, false));
        return result;
    }
}
