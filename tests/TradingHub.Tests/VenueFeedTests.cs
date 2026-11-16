using System.Text.Json;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.Venues;
using TradingHub.Market;
using static TradingHub.Tests.VenueFixtures;

namespace TradingHub.Tests;

/// <summary>Public venue parsers, listing metadata, symbol mapping and the live reconnect loop, all against fixtures.</summary>
public sealed class VenueFeedTests
{
    private static Instrument Inst(string symbol, decimal tick, string baseAsset = "BTC", string quote = "USDT") =>
        new() { Symbol = symbol, FeedId = "fixture", TickSize = tick, BaseAsset = baseAsset, QuoteAsset = quote };

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Okx_lists_linear_swaps_in_coins_converts_contract_trades_and_chains_the_book()
    {
        var listed = OkxFeed.ParseInstruments(Root("""
            {"data":[
              {"instId":"BTC-USDT-SWAP","state":"live","ctType":"linear","ctVal":"0.01","ctValCcy":"BTC","settleCcy":"USDT","tickSz":"0.1","lotSz":"0.01"},
              {"instId":"BTC-USD-SWAP","state":"live","ctType":"inverse","ctVal":"100","ctValCcy":"USD","settleCcy":"BTC","tickSz":"0.1","lotSz":"1"},
              {"instId":"ETH-USDT-SWAP","state":"suspend","ctType":"linear","ctVal":"0.1","ctValCcy":"ETH","settleCcy":"USDT","tickSz":"0.01","lotSz":"1"}]}
            """), spot: false, OkxFeed.SwapId, "OKX");
        var swap = Assert.Single(listed);
        Assert.Equal(("BTC", "USDT", 0.1m, 0.0001m), (swap.Instrument.BaseAsset, swap.Instrument.QuoteAsset, swap.Instrument.TickSize, swap.Instrument.VolumeStep));
        Assert.Equal(0.01m, swap.Spec.Multiplier);
        Assert.Equal("BTCUSDT", AggregateFeed.CompositeSymbol(swap.Instrument));

        using var feed = new OkxFeed(false);
        var session = feed.CreateSession(swap.Instrument, swap.Spec);
        var trade = Assert.Single(feed.Process("""{"arg":{"channel":"trades","instId":"BTC-USDT-SWAP"},"data":[{"instId":"BTC-USDT-SWAP","tradeId":"9","px":"70000.1","sz":"250","side":"sell","ts":"1730000000000"}]}""", session));
        Assert.Equal((2.5m, TradeSide.Sell, "9", OkxFeed.SwapId), (trade.Volume, trade.Side, trade.Provenance.SourceEventId, trade.Provenance.SourceIdentity));
        Assert.True(trade.IsObservedTrade);
        Assert.False(trade.Provenance.IsPartial);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1730000000000).UtcDateTime, trade.Time);

        Assert.Empty(feed.Process("""{"arg":{"channel":"books"},"action":"update","data":[{"bids":[["1","1","0","1"]],"asks":[],"seqId":3,"prevSeqId":2}]}""", session));
        Assert.Equal(-1, session.Seq); // An update before the snapshot is ignored, not applied to an unknown book.
        feed.Process("""{"arg":{"channel":"books"},"action":"snapshot","data":[{"bids":[["70000","100","0","1"]],"asks":[["70000.5","50","0","1"]],"seqId":10,"prevSeqId":-1}]}""", session);
        feed.Process("""{"arg":{"channel":"books"},"action":"update","data":[{"bids":[["70000","0","0","0"],["69999.9","10","0","1"]],"asks":[],"seqId":11,"prevSeqId":10}]}""", session);
        var book = session.Book.BuildUpdate(DateTime.UnixEpoch).Snapshot;
        Assert.Equal((swap.Instrument.ToTicks(69999.9m), 0.1m), (book.Bids[0].PriceTicks, book.Bids[0].Size));
        Assert.Equal(0.5m, book.Asks[0].Size);
        Assert.Throws<PublicVenueFeed.VenueResyncException>(() =>
            feed.Process("""{"arg":{"channel":"books"},"action":"update","data":[{"bids":[],"asks":[],"seqId":20,"prevSeqId":15}]}""", session));

        var candles = OkxFeed.ParseCandles(Root("""{"data":[["1730000060000","2","3","1","2.5","400","4","10","1"]]}"""), spot: false);
        Assert.Equal((1730000060000L, 4m), (candles[0].OpenTimeMs, candles[0].Volume));
        Assert.False(candles[0].HasBuyVolume);
    }

    [Fact]
    public void Bitget_lists_both_markets_ignores_the_trade_backlog_and_resynchronizes_on_a_sequence_gap()
    {
        var futures = Assert.Single(BitgetFeed.ParseInstruments(Root("""
            {"data":[{"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","pricePlace":"1","priceEndStep":"5","sizeMultiplier":"0.001","symbolStatus":"normal"},
                     {"symbol":"BTCEUR","baseCoin":"BTC","quoteCoin":"EUR","pricePlace":"1","priceEndStep":"1","sizeMultiplier":"0.001","symbolStatus":"normal"}]}
            """), spot: false, BitgetFeed.FuturesId, "Bitget"));
        Assert.Equal((0.5m, 0.001m), (futures.Instrument.TickSize, futures.Instrument.VolumeStep));
        var spot = Assert.Single(BitgetFeed.ParseInstruments(Root("""
            {"data":[{"symbol":"ETHUSDC","baseCoin":"ETH","quoteCoin":"USDC","pricePrecision":"2","quantityPrecision":"4","status":"online"}]}
            """), spot: true, BitgetFeed.SpotId, "Bitget Spot"));
        Assert.Equal((0.01m, 0.0001m, "ETHUSDT"), (spot.Instrument.TickSize, spot.Instrument.VolumeStep, AggregateFeed.CompositeSymbol(spot.Instrument)));

        using var feed = new BitgetFeed(false);
        var session = feed.CreateSession(futures.Instrument, futures.Spec);
        Assert.Empty(feed.Process("""{"action":"snapshot","arg":{"channel":"trade"},"data":[{"price":"1","size":"1","side":"buy","ts":"1","tradeId":"1"}]}""", session));
        var live = Assert.Single(feed.Process("""{"action":"update","arg":{"channel":"trade"},"data":[{"price":"80000","size":"0.2","side":"buy","ts":"1730000000000","tradeId":"77"}]}""", session));
        Assert.Equal((0.2m, TradeSide.Buy, "77"), (live.Volume, live.Side, live.Provenance.SourceEventId));
        feed.Process("""{"action":"snapshot","arg":{"channel":"books"},"data":[{"bids":[["80000","1"]],"asks":[["80000.5","2"]],"seq":100,"pseq":99}]}""", session);
        feed.Process("""{"action":"update","arg":{"channel":"books"},"data":[{"bids":[["80000","3"]],"asks":[],"seq":101,"pseq":100}]}""", session);
        feed.Process("""{"action":"update","arg":{"channel":"books"},"data":[{"bids":[["80000","9"]],"asks":[],"seq":101,"pseq":100}]}""", session);
        Assert.Equal(3m, session.Book.BuildUpdate(DateTime.UnixEpoch).Snapshot.Bids[0].Size);
        Assert.Throws<PublicVenueFeed.VenueResyncException>(() =>
            feed.Process("""{"action":"update","arg":{"channel":"books"},"data":[{"bids":[],"asks":[],"seq":105,"pseq":104}]}""", session));
        var candle = Assert.Single(BitgetFeed.ParseCandles(Root("""{"data":[["1730000000000","1","2","0.5","1.5","12","18"]]}""")));
        Assert.Equal((1.5m, 12m), (candle.Close, candle.Volume));
    }

    [Fact]
    public void Coinbase_inverts_the_maker_side_skips_non_usd_and_reads_candles_in_its_column_order()
    {
        var listed = CoinbaseFeed.ParseInstruments(Root("""
            [{"id":"BTC-USD","base_currency":"BTC","quote_currency":"USD","quote_increment":"0.01000000","base_increment":"0.00000001","status":"online","trading_disabled":false},
             {"id":"BTC-EUR","base_currency":"BTC","quote_currency":"EUR","quote_increment":"0.01","base_increment":"0.00000001","status":"online","trading_disabled":false},
             {"id":"OLD-USD","base_currency":"OLD","quote_currency":"USD","quote_increment":"0.01","base_increment":"1","status":"online","trading_disabled":true}]
            """), CoinbaseFeed.SpotId, "Coinbase");
        var btc = Assert.Single(listed);
        Assert.Equal((0.01m, 0.00000001m), (btc.Instrument.TickSize, btc.Instrument.VolumeStep));

        using var feed = new CoinbaseFeed();
        var session = feed.CreateSession(btc.Instrument, btc.Spec);
        var trade = Assert.Single(feed.Process("""{"type":"match","trade_id":42,"price":"80000.01","size":"0.5","side":"sell","time":"2026-11-05T01:02:03.456Z"}""", session));
        Assert.Equal((TradeSide.Buy, "42"), (trade.Side, trade.Provenance.SourceEventId));
        Assert.Equal(new DateTime(2026, 11, 5, 1, 2, 3, 456, DateTimeKind.Utc), trade.Time);
        feed.Process("""{"type":"snapshot","bids":[["80000","1.5"]],"asks":[["80000.02","2"]]}""", session);
        feed.Process("""{"type":"l2update","changes":[["buy","80000","0"],["sell","80000.03","1"]]}""", session);
        var book = session.Book.BuildUpdate(DateTime.UnixEpoch).Snapshot;
        Assert.Empty(book.Bids);
        Assert.Equal(2, book.Asks.Length);
        Assert.Throws<InvalidDataException>(() => feed.Process("""{"type":"error","message":"Failed to subscribe","reason":"denied"}""", session));
        var candle = Assert.Single(CoinbaseFeed.ParseCandles(Root("[[1730000000, 9, 12, 10, 11, 3.5]]")));
        Assert.Equal((1730000000000L, 10m, 12m, 9m, 11m, 3.5m), (candle.OpenTimeMs, candle.Open, candle.High, candle.Low, candle.Close, candle.Volume));
    }

    [Fact]
    public void Deribit_converts_inverse_usd_contracts_and_lists_linear_usdc_perpetuals_in_coins()
    {
        var listed = DeribitFeed.ParseInstruments(Root("""
            {"result":[
              {"instrument_name":"BTC-PERPETUAL","settlement_period":"perpetual","is_active":true,"base_currency":"BTC","quote_currency":"USD","tick_size":0.5,"min_trade_amount":10,"instrument_type":"reversed"},
              {"instrument_name":"BTC_USDC-PERPETUAL","settlement_period":"perpetual","is_active":true,"base_currency":"BTC","quote_currency":"USDC","tick_size":1,"min_trade_amount":0.001,"instrument_type":"linear"},
              {"instrument_name":"BTC-27DEC26","settlement_period":"month","is_active":true,"base_currency":"BTC","quote_currency":"USD","tick_size":2.5,"min_trade_amount":10,"instrument_type":"reversed"}]}
            """), DeribitFeed.PerpId, "Deribit");
        Assert.Equal(new[] { "BTC-PERPETUAL", "BTC_USDC-PERPETUAL" }, listed.Select(v => v.Instrument.Symbol));
        Assert.True(listed[0].Spec.Inverse);
        Assert.Equal(0.001m, listed[1].Instrument.VolumeStep);

        using var feed = new DeribitFeed();
        var session = feed.CreateSession(listed[0].Instrument, listed[0].Spec);
        var trade = Assert.Single(feed.Process("""{"params":{"channel":"trades.BTC-PERPETUAL.100ms","data":[{"trade_id":"ETH-1","price":80000,"amount":8000,"timestamp":1730000000000,"direction":"buy"}]}}""", session));
        Assert.Equal((0.1m, TradeSide.Buy), (trade.Volume, trade.Side));
        feed.Process("""{"params":{"channel":"book.BTC-PERPETUAL.100ms","data":{"type":"snapshot","change_id":5,"bids":[["new",80000,16000]],"asks":[["new",80000.5,8000]]}}}""", session);
        feed.Process("""{"params":{"channel":"book.BTC-PERPETUAL.100ms","data":{"type":"change","change_id":6,"prev_change_id":5,"bids":[["delete",80000,0]],"asks":[]}}}""", session);
        var book = session.Book.BuildUpdate(DateTime.UnixEpoch).Snapshot;
        Assert.Empty(book.Bids);
        Assert.Equal(8000m / 80000.5m, book.Asks[0].Size);
        Assert.Throws<PublicVenueFeed.VenueResyncException>(() =>
            feed.Process("""{"params":{"channel":"book.BTC-PERPETUAL.100ms","data":{"type":"change","change_id":9,"prev_change_id":8,"bids":[],"asks":[]}}}""", session));
        var candles = DeribitFeed.ParseCandles(Root("""{"result":{"status":"ok","ticks":[1730000000000],"open":[1],"high":[2],"low":[0.5],"close":[1.5],"volume":[7],"cost":[9]}}"""));
        Assert.Equal((7m, 1.5m), (candles[0].Volume, candles[0].Close));
        Assert.Empty(DeribitFeed.ParseCandles(Root("""{"result":{"status":"no_data","ticks":[]}}""")));
    }

    [Fact]
    public void Hyperliquid_maps_thousand_unit_coins_keeps_exact_ticks_and_reads_full_top_book_snapshots()
    {
        var listed = HyperliquidFeed.ParseInstruments(Root("""
            [{"universe":[{"name":"BTC","szDecimals":5},{"name":"kPEPE","szDecimals":0},{"name":"OLD","szDecimals":2,"isDelisted":true}]},
             [{"markPx":"80000.0"},{"markPx":"0.012"},{"markPx":"1"}]]
            """), HyperliquidFeed.PerpId, "Hyperliquid");
        Assert.Equal(new[] { "BTC", "kPEPE" }, listed.Select(v => v.Instrument.Symbol));
        Assert.Equal(("BTC", 0.1m, 0.00001m), (listed[0].Instrument.BaseAsset, listed[0].Instrument.TickSize, listed[0].Instrument.VolumeStep));
        Assert.Equal(("1000PEPE", "1000PEPEUSDT"), (listed[1].Instrument.BaseAsset, AggregateFeed.CompositeSymbol(listed[1].Instrument)));
        Assert.Equal(0.000001m, HyperliquidFeed.TickFor(0.012m, 0));
        Assert.Equal(0.01m, HyperliquidFeed.TickFor(9000m, 2));

        using var feed = new HyperliquidFeed();
        var session = feed.CreateSession(listed[0].Instrument, listed[0].Spec);
        var trade = Assert.Single(feed.Process("""{"channel":"trades","data":[{"coin":"BTC","side":"A","px":"80001","sz":"0.3","time":1730000000000,"tid":12345},{"coin":"ETH","side":"B","px":"1","sz":"1","time":1,"tid":1}]}""", session));
        Assert.Equal((TradeSide.Sell, "12345"), (trade.Side, trade.Provenance.SourceEventId));
        feed.Process("""{"channel":"l2Book","data":{"coin":"BTC","levels":[[{"px":"80000","sz":"2","n":3}],[{"px":"80010","sz":"1","n":1}]]}}""", session);
        feed.Process("""{"channel":"l2Book","data":{"coin":"BTC","levels":[[{"px":"79990","sz":"4","n":3}],[{"px":"80010","sz":"1","n":1}]]}}""", session);
        var update = session.Book.BuildUpdate(DateTime.UnixEpoch);
        Assert.Equal(listed[0].Instrument.ToTicks(79990), Assert.Single(update.Snapshot.Bids).PriceTicks);
        var candle = Assert.Single(HyperliquidFeed.ParseCandles(Root("""[{"t":1730000000000,"T":1730000059999,"s":"BTC","i":"1m","o":"1","c":"2","h":"3","l":"0.5","v":"4.5","n":10}]""")));
        Assert.Equal((2m, 4.5m), (candle.Close, candle.Volume));
    }

    [Fact]
    public void Mexc_converts_contracts_and_marks_unidentified_deals_as_partial_observations()
    {
        var listed = MexcFeed.ParseInstruments(Root("""
            {"success":true,"data":[
              {"symbol":"BTC_USDT","baseCoin":"BTC","quoteCoin":"USDT","settleCoin":"USDT","contractSize":0.0001,"priceUnit":0.1,"volUnit":1,"state":0},
              {"symbol":"BTC_USD","baseCoin":"BTC","quoteCoin":"USD","settleCoin":"BTC","contractSize":100,"priceUnit":0.5,"volUnit":1,"state":0}]}
            """), MexcFeed.PerpId, "MEXC");
        var btc = Assert.Single(listed);
        Assert.Equal((0.1m, 0.0001m), (btc.Instrument.TickSize, btc.Instrument.VolumeStep));

        using var feed = new MexcFeed();
        var session = feed.CreateSession(btc.Instrument, btc.Spec);
        var deals = feed.Process("""{"channel":"push.deal","symbol":"BTC_USDT","data":[{"p":80000.1,"v":1000,"T":1,"t":1730000000000},{"p":80000,"v":10,"T":2,"t":1730000000001}]}""", session);
        Assert.Equal(new[] { 0.1m, 0.001m }, deals.Select(d => d.Volume));
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Sell }, deals.Select(d => d.Side));
        Assert.All(deals, d => Assert.True(d.Provenance.IsPartial));
        Assert.NotEqual(deals[0].Provenance.SourceEventId, deals[1].Provenance.SourceEventId);
        Assert.Empty(feed.Process("""{"channel":"push.deal","symbol":"ETH_USDT","data":{"p":1,"v":1,"T":1,"t":1}}""", session));
        feed.Process("""{"channel":"push.depth.full","symbol":"BTC_USDT","data":{"asks":[[80001,100,1]],"bids":[[80000,2000,3]],"version":5}}""", session);
        Assert.Equal(0.2m, session.Book.BuildUpdate(DateTime.UnixEpoch).Snapshot.Bids[0].Size);
        var candle = Assert.Single(MexcFeed.ParseCandles(Root("""{"data":{"time":[1730000000],"open":[1],"close":[2],"high":[3],"low":[0.5],"vol":[5000],"amount":[1]}}"""), 0.0001m));
        Assert.Equal((1730000000000L, 0.5m), (candle.OpenTimeMs, candle.Volume));
    }

    [Fact]
    public async Task Bybit_spot_uses_the_spot_category_base_precision_and_venue_metadata()
    {
        using var handler = new Http((request, _) =>
        {
            Assert.Contains("category=spot", request.RequestUri!.Query);
            return Json("""{"result":{"list":[{"symbol":"BTCUSDT","status":"Trading","baseCoin":"BTC","quoteCoin":"USDT","priceFilter":{"tickSize":"0.01"},"lotSizeFilter":{"basePrecision":"0.000001"}}],"nextPageCursor":""}}""");
        });
        using var http = new HttpClient(handler);
        using var feed = new BybitFeed(http, new Uri("https://bybit.fixture"), feedId: BybitFeed.SpotId);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        Assert.Equal((0.01m, 0.000001m, "Bybit Spot", "BTC/USDT spot"), (instrument.TickSize, instrument.VolumeStep, feed.DisplayName, instrument.Description));
        Assert.True(feed.IsSpot);
        Assert.Equal("Bybit", feed.Venue);
        Assert.False(new BybitFeed(http).IsSpot);
    }

    [Fact]
    public async Task Candle_history_pages_backwards_and_returns_sorted_unique_bars_inside_the_range()
    {
        long start = 1729999980000; // Minute-aligned: misaligned bars are rejected, not shifted.
        using var handler = new Http((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v5/public/instruments")
                return Json("""{"data":[{"instId":"BTC-USDT-SWAP","state":"live","ctType":"linear","ctVal":"0.01","ctValCcy":"BTC","settleCcy":"USDT","tickSz":"0.1","lotSz":"0.01"}]}""");
            Assert.Equal("/api/v5/market/history-candles", path);
            long after = long.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["after"]!);
            // Newest first, with a duplicated boundary bar and one bar outside the range.
            return Json(after > start + 120_000
                ? $$"""{"data":[["{{start + 120_000}}","3","3","3","3","0","3","0","1"],["{{start + 60_000}}","2","2","2","2","0","2","0","1"]]}"""
                : after > start ? $$"""{"data":[["{{start + 60_000}}","2","2","2","2","0","2","0","1"],["{{start}}","1","1","1","1","0","1","0","1"],["{{start - 60_000}}","0","0","0","0","0","9","0","1"]]}"""
                : """{"data":[]}""");
        });
        using var http = new HttpClient(handler);
        using var feed = new OkxFeed(false, new PublicVenueTransport(http, RestOrigin: new Uri("https://okx.fixture"), Delay: NoDelay));
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var bars = new List<Kline>();
        await feed.LoadKlinesAsync(instrument, DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
            DateTimeOffset.FromUnixTimeMilliseconds(start + 180_000).UtcDateTime, 1, 0, batch => { bars.AddRange(batch); return true; }, null, CancellationToken.None);

        Assert.Equal(new[] { start, start + 60_000, start + 120_000 }, bars.Select(b => b.OpenTimeMs));
        Assert.Equal(new[] { 1m, 2m, 3m }, bars.Select(b => b.Volume));
        Assert.All(handler.Requests, r => Assert.Equal("okx.fixture", r.Uri.Host));
    }

    [Fact]
    public async Task Live_loop_streams_coins_invalidates_the_book_on_a_sequence_gap_and_reconnects_with_a_fresh_snapshot()
    {
        using var handler = new Http((_, _) => Json("""{"data":[{"instId":"BTC-USDT-SWAP","state":"live","ctType":"linear","ctVal":"0.01","ctValCcy":"BTC","settleCcy":"USDT","tickSz":"0.1","lotSz":"0.01"}]}"""));
        using var http = new HttpClient(handler);
        var sockets = new Sockets();
        var first = sockets.Add();
        var second = sockets.Add();
        using var feed = new OkxFeed(false, new PublicVenueTransport(http, RestOrigin: new Uri("https://okx.fixture"),
            ConnectWebSocket: sockets.ConnectAsync, Delay: NoDelay));
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using var subscription = feed.Subscribe(instrument, sink);
        await first.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("\"channel\":\"books\"", first.Sent.Single());
        await sink.NextAsync<(FeedStatus Status, string? Message)>(s => s.Status == FeedStatus.Connected);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        first.SendServer($$"""{"arg":{"channel":"trades"},"data":[{"tradeId":"1","px":"70000","sz":"30","side":"buy","ts":"{{now}}"}]}""");
        var trades = await sink.NextAsync<IReadOnlyList<Trade>>(_ => true);
        Assert.Equal(0.3m, Assert.Single(trades).Volume);
        first.SendServer("""{"arg":{"channel":"books"},"action":"snapshot","data":[{"bids":[["70000","100"]],"asks":[["70000.5","50"]],"seqId":10,"prevSeqId":-1}]}""");
        var snapshot = await sink.NextAsync<DepthUpdate>(_ => true);
        Assert.True(snapshot.IsFullRefresh && snapshot.IsUsable);
        Assert.Equal((OkxFeed.SwapId, "OKX", 10L), (snapshot.ProviderId, snapshot.Venue, snapshot.Sequence));
        Assert.True(snapshot.Coverage.HasFlag(DepthCoverage.PartialBook));

        first.SendServer("""{"arg":{"channel":"books"},"action":"update","data":[{"bids":[],"asks":[],"seqId":20,"prevSeqId":15}]}""");
        var gap = await sink.NextAsync<DepthUpdate>(_ => true);
        Assert.False(gap.IsUsable);
        Assert.Equal(DepthGapReason.Sequence, gap.GapBefore);
        var reconnecting = await sink.NextAsync<(FeedStatus Status, string? Message)>(s => s.Status == FeedStatus.Reconnecting);
        Assert.Contains("sequence gap", reconnecting.Message);

        await second.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sink.NextAsync<(FeedStatus Status, string? Message)>(s => s.Status == FeedStatus.Connected);
        second.SendServer("""{"arg":{"channel":"books"},"action":"snapshot","data":[{"bids":[["69999","1"]],"asks":[["70001","1"]],"seqId":30,"prevSeqId":-1}]}""");
        var restored = await sink.NextAsync<DepthUpdate>(_ => true);
        Assert.True(restored.IsFullRefresh && restored.IsUsable);
        Assert.Equal(2, sockets.Endpoints.Count);
        Assert.All(sockets.Endpoints, e => Assert.Equal("ws.okx.com", e.Host));
    }

    [Fact]
    public async Task Live_loop_never_streams_without_listed_contract_metadata()
    {
        int listings = 0;
        using var handler = new Http((_, _) =>
        {
            Interlocked.Increment(ref listings);
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("maintenance") };
        });
        using var http = new HttpClient(handler);
        var sockets = new Sockets();
        // Backoff waits until disposal, so exactly one failed listing attempt is observed.
        using var feed = new OkxFeed(false, new PublicVenueTransport(http, ConnectWebSocket: sockets.ConnectAsync,
            Delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct)));
        var sink = new Sink();
        using var subscription = feed.Subscribe(Inst("BTC-USDT-SWAP", 0.1m), sink);
        var status = await sink.NextAsync<(FeedStatus Status, string? Message)>(s => s.Status == FeedStatus.Reconnecting);
        Assert.Contains("503", status.Message);
        Assert.Empty(sockets.Endpoints);
        Assert.Equal(1, Volatile.Read(ref listings));
    }
}
