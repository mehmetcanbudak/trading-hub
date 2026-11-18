using System.Text.Json;
using TradingHub.Feeds;
using TradingHub.Feeds.Venues;
using TradingHub.Market;
using static TradingHub.Tests.VenueFixtures;

namespace TradingHub.Tests;

/// <summary>Public venue candle resolutions, book sequencing and live book publication, against fixtures only.</summary>
public sealed class FeedReviewVenueTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private const string OkxListing = """{"data":[{"instId":"BTC-USDT-SWAP","state":"live","ctType":"linear","ctVal":"0.01","ctValCcy":"BTC","settleCcy":"USDT","tickSz":"0.1","lotSz":"0.01"}]}""";

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Hourly_candles_load_from_every_public_venue_with_its_own_interval_name()
    {
        long t0 = Now.AddHours(-4).ToUnixTimeMilliseconds(), t1 = t0 + 3_600_000;
        var from = DateTimeOffset.FromUnixTimeMilliseconds(t0).UtcDateTime;
        string Rows(Func<long, string> row) => row(t1) + "," + row(t0);
        var cases = new (string Name, Func<PublicVenueTransport, PublicVenueFeed> Create, Func<string, string?, string> Respond, string Interval)[]
        {
            ("OKX", t => new OkxFeed(false, t), (path, _) => path == "/api/v5/market/history-candles"
                ? """{"data":[""" + Rows(t => $$"""["{{t}}","2","3","1","2.5","400","4","0","1"]""") + "]}" : OkxListing, "bar=1H"),
            ("Bitget", t => new BitgetFeed(false, t), (path, _) => path == "/api/v2/mix/market/history-candles"
                ? """{"data":[""" + Rows(t => $$"""["{{t}}","1","2","0.5","1.5","12","18"]""") + "]}"
                : """{"data":[{"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","pricePlace":"1","priceEndStep":"5","sizeMultiplier":"0.001","symbolStatus":"normal"}]}""", "granularity=1H"),
            ("Bitget Spot", t => new BitgetFeed(true, t), (path, _) => path == "/api/v2/spot/market/history-candles"
                ? """{"data":[""" + Rows(t => $$"""["{{t}}","1","2","0.5","1.5","12","18"]""") + "]}"
                : """{"data":[{"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","pricePrecision":"2","quantityPrecision":"4","status":"online"}]}""", "granularity=1h"),
            ("Coinbase", t => new CoinbaseFeed(t), (path, _) => path == "/products/BTC-USD/candles"
                ? "[" + Rows(t => $"[{t / 1000}, 9, 12, 10, 11, 3.5]") + "]"
                : """[{"id":"BTC-USD","base_currency":"BTC","quote_currency":"USD","quote_increment":"0.01","base_increment":"0.00000001","status":"online","trading_disabled":false}]""", "granularity=3600"),
            ("Deribit", t => new DeribitFeed(t), (path, _) => path == "/api/v2/public/get_tradingview_chart_data"
                ? $$$"""{"result":{"status":"ok","ticks":[{{{t0}}},{{{t1}}}],"open":[1,1],"high":[2,2],"low":[0.5,0.5],"close":[1.5,1.5],"volume":[7,7]}}"""
                : """{"result":[{"instrument_name":"BTC-PERPETUAL","settlement_period":"perpetual","is_active":true,"base_currency":"BTC","quote_currency":"USD","tick_size":0.5,"min_trade_amount":10,"instrument_type":"reversed"}]}""", "resolution=60"),
            ("Hyperliquid", t => new HyperliquidFeed(t), (_, body) => body != null && body.Contains("candleSnapshot")
                ? "[" + Rows(t => $$"""{"t":{{t}},"o":"1","c":"2","h":"3","l":"0.5","v":"4.5"}""") + "]"
                : """[{"universe":[{"name":"BTC","szDecimals":5}]},[{"markPx":"80000.0"}]]""", "\"interval\":\"1h\""),
            ("MEXC", t => new MexcFeed(t), (path, _) => path == "/api/v1/contract/kline/BTC_USDT"
                ? $$$"""{"data":{"time":[{{{t0 / 1000}}},{{{t1 / 1000}}}],"open":[1,1],"close":[2,2],"high":[3,3],"low":[0.5,0.5],"vol":[5000,5000]}}"""
                : """{"success":true,"data":[{"symbol":"BTC_USDT","baseCoin":"BTC","quoteCoin":"USDT","settleCoin":"USDT","contractSize":0.0001,"priceUnit":0.1,"volUnit":1,"state":0}]}""", "interval=Min60"),
        };

        foreach (var (name, create, respond, interval) in cases)
        {
            using var handler = new Http((request, body) => Json(respond(request.RequestUri!.AbsolutePath, body)));
            using var http = new HttpClient(handler);
            using var feed = create(new PublicVenueTransport(http, RestOrigin: new Uri("https://venue.fixture"), Delay: NoDelay, Clock: new FixedClock(Now)));
            var instrument = (await feed.GetInstrumentsAsync(CancellationToken.None))[0];
            var bars = new List<Kline>();

            await feed.LoadKlinesAsync(instrument, from, from.AddHours(2), 60, 0, batch => { bars.AddRange(batch); return true; }, null, CancellationToken.None);

            Assert.True(bars.Select(b => b.OpenTimeMs).SequenceEqual(new[] { t0, t1 }), $"{name}: {string.Join(", ", bars.Select(b => b.OpenTimeMs))}");
            Assert.True(handler.Requests.Any(r => (r.Uri.Query + r.Body).Contains(interval, StringComparison.Ordinal)), $"{name} did not request {interval}");
        }
    }

    [Fact]
    public async Task Hyperliquid_candles_before_its_5000_bar_window_are_read_from_the_window_and_marked_partial()
    {
        long step = 3_600_000;
        long horizon = (Now.ToUnixTimeMilliseconds() / step - (HyperliquidFeed.CandleWindow - 1)) * step;
        long recent = Now.AddHours(-20).ToUnixTimeMilliseconds();
        using var handler = new Http((_, body) => Json(body!.Contains("candleSnapshot")
            ? $$"""[{"t":{{horizon + step}},"o":"1","c":"2","h":"3","l":"0.5","v":"4.5"},{"t":{{recent}},"o":"1","c":"2","h":"3","l":"0.5","v":"4.5"}]"""
            : """[{"universe":[{"name":"BTC","szDecimals":5}]},[{"markPx":"80000.0"}]]"""));
        using var http = new HttpClient(handler);
        using var feed = new HyperliquidFeed(new PublicVenueTransport(http, RestOrigin: new Uri("https://hyperliquid.fixture"), Delay: NoDelay, Clock: new FixedClock(Now)));
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var bars = new List<Kline>();

        await feed.LoadKlinesAsync(instrument, Now.AddHours(-6000).UtcDateTime, Now.AddHours(-10).UtcDateTime, 60, 0, batch => { bars.AddRange(batch); return true; }, null, CancellationToken.None);

        Assert.Equal(2, bars.Count);
        Assert.All(bars, b => Assert.True((b.Provenance.Quality & DataQuality.Partial) != 0));
        Assert.Contains(handler.Requests, r => r.Body != null && r.Body.Contains($"\"startTime\":{horizon},", StringComparison.Ordinal));

        // A range inside the window is requested from its own start and is complete.
        bars.Clear();
        long from = Now.AddHours(-48).ToUnixTimeMilliseconds();
        await feed.LoadKlinesAsync(instrument, Now.AddHours(-48).UtcDateTime, Now.UtcDateTime, 60, 0, batch => { bars.AddRange(batch); return true; }, null, CancellationToken.None);
        Assert.Equal(DataQuality.None, Assert.Single(bars).Provenance.Quality & DataQuality.Partial);
        Assert.Contains(handler.Requests, r => r.Body != null && r.Body.Contains($"\"startTime\":{from},", StringComparison.Ordinal));
    }

    [Fact]
    public void Bitget_spot_book_applies_updates_without_pseq_and_resynchronizes_only_out_of_order()
    {
        var spot = Assert.Single(BitgetFeed.ParseInstruments(Root("""
            {"data":[{"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","pricePrecision":"2","quantityPrecision":"4","status":"online"}]}
            """), spot: true, BitgetFeed.SpotId, "Bitget Spot"));
        using var feed = new BitgetFeed(true);
        var session = feed.CreateSession(spot.Instrument, spot.Spec);
        const string arg = """{"instType":"SPOT","channel":"books","instId":"BTCUSDT"}""";
        feed.Process($$"""{"action":"snapshot","arg":{{arg}},"data":[{"bids":[["80000","1"]],"asks":[["80000.5","2"]],"seq":100,"ts":"1"}]}""", session);

        // Spot pushes carry only an increasing seq, so the first update must not look like a gap.
        feed.Process($$"""{"action":"update","arg":{{arg}},"data":[{"bids":[["80000","3"]],"asks":[],"seq":104,"ts":"2"}]}""", session);
        feed.Process($$"""{"action":"update","arg":{{arg}},"data":[{"bids":[["80000","9"]],"asks":[],"seq":104,"ts":"2"}]}""", session);
        Assert.Equal((104L, 3m), (session.Seq, session.Book.BuildUpdate(DateTime.UnixEpoch).Snapshot.Bids[0].Size));

        Assert.Throws<PublicVenueFeed.VenueResyncException>(() =>
            feed.Process($$"""{"action":"update","arg":{{arg}},"data":[{"bids":[],"asks":[],"seq":103,"ts":"3"}]}""", session));
    }

    [Fact]
    public async Task Live_loop_requests_a_new_book_when_a_frame_fails_after_changing_it()
    {
        using var handler = new Http((_, _) => Json(OkxListing));
        using var http = new HttpClient(handler);
        var sockets = new Sockets();
        var first = sockets.Add();
        using var feed = new OkxFeed(false, new PublicVenueTransport(http, RestOrigin: new Uri("https://okx.fixture"),
            ConnectWebSocket: sockets.ConnectAsync, Delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct)));
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using var subscription = feed.Subscribe(instrument, sink);
        await first.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.SendServer("""{"arg":{"channel":"books"},"action":"snapshot","data":[{"bids":[["70000","100"]],"asks":[["70000.5","50"]],"seqId":10,"prevSeqId":-1}]}""");
        Assert.True((await sink.NextAsync<DepthUpdate>(_ => true)).IsUsable);

        // The bid applies and the sequence advances, then an ask price fails to parse: the delta is half-applied.
        first.SendServer("""{"arg":{"channel":"books"},"action":"update","data":[{"bids":[["70000","5"]],"asks":[["bad","1"]],"seqId":11,"prevSeqId":10}]}""");

        var gap = await sink.NextAsync<DepthUpdate>(_ => true);
        Assert.False(gap.IsUsable);
        Assert.Equal(DepthGapReason.Sequence, gap.GapBefore);
        var status = await sink.NextAsync<(FeedStatus Status, string? Message)>(s => s.Status == FeedStatus.Reconnecting);
        Assert.Contains("unparseable book frame", status.Message);
    }

    [Fact]
    public async Task Live_loop_publishes_a_throttled_book_change_when_the_stream_goes_quiet()
    {
        var clock = new ManualClock(Now);
        using var handler = new Http((_, _) => Json(OkxListing));
        using var http = new HttpClient(handler);
        var sockets = new Sockets();
        var first = sockets.Add();
        using var feed = new OkxFeed(false, new PublicVenueTransport(http, RestOrigin: new Uri("https://okx.fixture"),
            ConnectWebSocket: sockets.ConnectAsync, Delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct), Clock: clock));
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using var subscription = feed.Subscribe(instrument, sink);
        await first.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.SendServer("""{"arg":{"channel":"books"},"action":"snapshot","data":[{"bids":[["70000","100"]],"asks":[["70000.5","50"]],"seqId":10,"prevSeqId":-1}]}""");
        await sink.NextAsync<DepthUpdate>(u => u.IsFullRefresh);

        // Within the 100 ms throttle and followed by silence: published by the trailing timer, not by a later frame.
        first.SendServer("""{"arg":{"channel":"books"},"action":"update","data":[{"bids":[["70000","5"]],"asks":[],"seqId":11,"prevSeqId":10}]}""");
        await clock.Armed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMilliseconds(100));

        var trailing = await sink.NextAsync<DepthUpdate>(_ => true);
        Assert.True(trailing.IsUsable);
        Assert.Equal(0.05m, trailing.Snapshot.Bids[0].Size);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Wall time and timers that move only when the test advances them.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now = start;

        /// <summary>Completes when any timer is scheduled to fire.</summary>
        public TaskCompletionSource Armed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.Due <= _now).ToArray();
                foreach (var timer in due) timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due = DateTimeOffset.MaxValue;
            private bool _disposed;

            public void Fire()
            {
                if (!_disposed) callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                bool armed = dueTime != Timeout.InfiniteTimeSpan;
                lock (clock._gate) Due = armed ? clock._now + dueTime : DateTimeOffset.MaxValue;
                if (armed) clock.Armed.TrySetResult();
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
