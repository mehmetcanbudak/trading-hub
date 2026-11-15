using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Venues;

/// <summary>
/// Deribit public perpetual data: full book chained by change_id and trades. Inverse perpetuals (BTC-PERPETUAL,
/// ETH-PERPETUAL) are sized in USD and converted to base currency with the trade or level price.
/// </summary>
public sealed class DeribitFeed(PublicVenueTransport? transport = null) : PublicVenueFeed(transport)
{
    public const string PerpId = "deribit";
    public override string Id => PerpId;
    public override string DisplayName => "Deribit";
    public override string Venue => "Deribit";
    public override bool IsSpot => false;
    public override string PreferredQuote => "USD";
    protected override Uri DefaultRestOrigin => new("https://www.deribit.com");
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(30), "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"public/test\"}");
    protected override TimeSpan Stale => TimeSpan.FromSeconds(90);

    protected override async Task<IReadOnlyList<VenueInstrument>> ListAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/api/v2/public/get_instruments?currency=any&kind=future", ct).ConfigureAwait(false);
        return ParseInstruments(doc.RootElement, Id, DisplayName);
    }

    public static IReadOnlyList<VenueInstrument> ParseInstruments(JsonElement root, string feedId, string exchange)
    {
        var result = new List<VenueInstrument>();
        foreach (var s in root.GetProperty("result").EnumerateArray())
        {
            if (S(s, "settlement_period") != "perpetual" || s.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False) continue;
            string quote = S(s, "quote_currency"), baseAsset = S(s, "base_currency"), name = S(s, "instrument_name");
            decimal tick = D(s, "tick_size");
            if (!IsUsdQuote(quote) || tick <= 0) continue;
            bool inverse = S(s, "instrument_type") == "reversed";
            decimal step = inverse ? 0.00001m : D(s, "min_trade_amount", 0.001m);
            if (step <= 0) continue;
            result.Add(new VenueInstrument(new Instrument
            {
                Symbol = name, FeedId = feedId, Exchange = exchange,
                TickSize = Norm(tick), VolumeStep = Norm(step),
                BaseAsset = baseAsset, QuoteAsset = quote,
                Description = $"{baseAsset}/{quote} perpetual{(inverse ? " (inverse, USD contracts converted to " + baseAsset + ")" : "")}",
            }, new VenueSpec(name, 1, inverse)));
        }
        return result;
    }

    protected override Uri SocketEndpoint(Instrument instrument, VenueSpec spec) => new("wss://www.deribit.com/ws/api/v2");

    protected override IEnumerable<string> SubscribeFrames(Instrument instrument, VenueSpec spec)
    {
        yield return $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"public/subscribe\",\"params\":{{\"channels\":[\"book.{spec.Native}.100ms\",\"trades.{spec.Native}.100ms\"]}}}}";
    }

    protected override void OnMessage(string text, VenueSession s)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        if (m.TryGetProperty("error", out var error)) throw new InvalidDataException("Deribit: " + S(error, "message"));
        if (!m.TryGetProperty("params", out var p)) return;
        string channel = S(p, "channel");
        var data = p.GetProperty("data");
        if (channel.StartsWith("book.", StringComparison.Ordinal))
        {
            long change = (long)D(data, "change_id");
            if (S(data, "type") == "snapshot") s.Clear();
            else if (s.Seq < 0) return;
            else if ((long)D(data, "prev_change_id") != s.Seq) VenueSession.Resync($"{s.Seq} → {S(data, "prev_change_id")}");
            s.Seq = change;
            Rows(s, BookSide.Bid, data.GetProperty("bids"));
            Rows(s, BookSide.Ask, data.GetProperty("asks"));
            return;
        }
        if (channel.StartsWith("trades.", StringComparison.Ordinal))
            foreach (var d in data.EnumerateArray())
                s.Trade(Ms(D(d, "timestamp")), D(d, "price"), D(d, "amount"), S(d, "direction") == "buy", S(d, "trade_id"));
    }

    /// <summary>Rows [new | change | delete, price, amount].</summary>
    private static void Rows(VenueSession s, BookSide side, JsonElement rows)
    {
        foreach (var r in rows.EnumerateArray())
        {
            if (r.GetArrayLength() < 3) continue;
            s.Level(side, D(r[1]), r[0].GetString() == "delete" ? 0 : D(r[2]));
        }
    }

    protected override async Task<IReadOnlyList<Kline>> FetchKlinesAsync(Instrument instrument, VenueSpec spec, long fromMs, long toMs, int minutes, CancellationToken ct)
    {
        var bars = new List<Kline>();
        long window = 1000 * minutes * 60_000L;
        for (long start = fromMs; start < toMs; start += window)
        {
            ct.ThrowIfCancellationRequested();
            long end = Math.Min(toMs, start + window) - 1;
            using var doc = await GetJsonAsync($"/api/v2/public/get_tradingview_chart_data?instrument_name={Uri.EscapeDataString(spec.Native)}&start_timestamp={start}&end_timestamp={end}&resolution={minutes}", ct).ConfigureAwait(false);
            bars.AddRange(ParseCandles(doc.RootElement));
        }
        return bars;
    }

    /// <summary>Columnar result {ticks, open, high, low, close, volume}; volume is in base currency.</summary>
    public static IReadOnlyList<Kline> ParseCandles(JsonElement root)
    {
        var r = root.GetProperty("result");
        var result = new List<Kline>();
        if (S(r, "status") == "no_data") return result;
        var ticks = r.GetProperty("ticks");
        var open = r.GetProperty("open"); var high = r.GetProperty("high"); var low = r.GetProperty("low");
        var close = r.GetProperty("close"); var volume = r.GetProperty("volume");
        for (int i = 0; i < ticks.GetArrayLength(); i++)
            result.Add(new Kline((long)D(ticks[i]), D(open[i]), D(high[i]), D(low[i]), D(close[i]), D(volume[i]), 0, false));
        return result;
    }
}
