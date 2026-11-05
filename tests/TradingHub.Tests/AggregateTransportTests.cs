using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class AggregateTransportTests
{
    private static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static long Ms(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();
    private const string BinanceInstruments = """
        {"symbols":[{"symbol":"BTCUSDT","status":"TRADING","contractType":"PERPETUAL","baseAsset":"BTC","quoteAsset":"USDT","filters":[{"filterType":"PRICE_FILTER","tickSize":"0.1"},{"filterType":"LOT_SIZE","stepSize":"0.001"}]}]}
        """;
    private const string BybitInstruments = """
        {"result":{"list":[{"symbol":"BTCUSDT","status":"Trading","contractType":"LinearPerpetual","baseCoin":"BTC","quoteCoin":"USDT","priceFilter":{"tickSize":"0.25"},"lotSizeFilter":{"qtyStep":"0.001"},"launchTime":"1609459200000"}],"nextPageCursor":""}}
        """;

    [Fact]
    public async Task Actual_constituent_parsers_keep_multi_fill_packets_compact_recent_tape_partial_and_injected_HTTP_caller_owned()
    {
        using var handler = new FixtureHttp(request => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/exchangeInfo" => Json(BinanceInstruments),
            "/fapi/v1/aggTrades" => Json($$"""
                [{"a":10,"p":"100","q":"12","T":{{Ms(Start.AddSeconds(1))}},"m":false,"f":100,"l":102},
                 {"a":11,"p":"102","q":"2","T":{{Ms(Start.AddSeconds(3))}},"m":true,"f":103,"l":103}]
                """),
            "/v5/market/instruments-info" => Json(BybitInstruments),
            "/v5/market/recent-trade" => Json($$$"""
                {"result":{"list":[{"time":"{{{Ms(Start.AddSeconds(2))}}}","price":"101","size":"3","side":"Sell","execId":"91f2f7bc-65de-4b78-80cf-000000000011"}]}}
                """),
            "/v5/market/tickers" => Json("{\"result\":{\"list\":[]}}"),
            _ => throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri),
        });
        using var http = new HttpClient(handler);
        var binance = new BinanceFeed(true, httpClient: http, restOrigin: new Uri("https://binance.fixture"));
        var bybit = new BybitFeed(http, restOrigin: new Uri("https://bybit.fixture"));
        using var feed = new AggregateFeed([binance, bybit], ownsSources: true);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var trades = new List<Trade>();

        await feed.LoadTradesAsync(instrument, Start, Start.AddMinutes(1), null, batch => { trades.AddRange(batch); return true; }, null, CancellationToken.None);

        Assert.Equal(new[] { 100m, 101m, 102m }, trades.Select(t => t.Price));
        Assert.Equal(DataQuality.Compact | DataQuality.Partial, trades[0].Provenance.Quality);
        Assert.Equal(3, trades[0].Provenance.ObservedTradeCount);
        Assert.Equal(BinanceFeed.FuturesId, trades[0].Provenance.SourceIdentity);
        Assert.Equal("10", trades[0].Provenance.SourceEventId);
        Assert.True(trades[0].Id > 0); // An exchange aggregate is recordable, unlike a synthetic display candle.
        Assert.True(trades[1].IsObservedTrade);
        Assert.Equal(BybitFeed.LinearId, trades[1].Provenance.SourceIdentity);
        Assert.Equal("91f2f7bc-65de-4b78-80cf-000000000011", trades[1].Provenance.SourceEventId);
        Assert.Equal("11", trades[2].Provenance.SourceEventId);
        Assert.True(trades[1].Provenance.IsPartial);
        var store = new TradeStore(instrument);
        foreach (var trade in trades) store.Add(trade);
        var candles = new CandleSeries(instrument, ChartPeriod.M1);
        candles.Rebuild(store);
        Assert.Equal(17m, candles[0].Volume);
        Assert.Equal(3m, candles[0].MaxTradeVolume);
        Assert.Equal(2, candles[0].ObservedTrades);
        Assert.Equal(3, candles[0].AggregatedTrades);
        var coverage = feed.GetCoverage(instrument, AggregateOperation.Trades);
        Assert.Equal(Start, coverage.RequestedFromUtc);
        Assert.Equal(Start.AddMinutes(1), coverage.RequestedToUtc);
        Assert.Contains(coverage.Constituents, status => status.FeedId == BybitFeed.LinearId && status.IsPartial && status.Message!.Contains("recent tape"));
        Assert.All(handler.Requests, uri => Assert.Contains(uri.Host, new[] { "binance.fixture", "bybit.fixture" }));

        feed.Dispose();
        Assert.Equal("{\"result\":{\"list\":[]}}", await http.GetStringAsync("https://bybit.fixture/v5/market/tickers"));
    }

    [Fact]
    public async Task Monthly_archives_use_the_injected_HTTP_client_instead_of_a_hidden_default_transport()
    {
        var month = new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        using var content = new MemoryStream();
        using (var zip = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("BTCUSDT-1m-2025-09.csv").Open());
            writer.Write($"{Ms(month)},100,110,90,105,10,{Ms(month.AddMinutes(1)) - 1},1000,3,6,600,0\n");
        }
        byte[] archive = content.ToArray();
        using var handler = new FixtureHttp(request =>
        {
            Assert.Equal("data.binance.vision", request.RequestUri!.Host);
            Assert.EndsWith("BTCUSDT-1m-2025-09.zip", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        });
        using var http = new HttpClient(handler);
        using var feed = new BinanceFeed(true, httpClient: http);
        var instrument = new Instrument { FeedId = BinanceFeed.FuturesId, Symbol = "BTCUSDT", TickSize = 0.1m };
        var candles = new List<Kline>();

        await feed.LoadKlinesAsync(instrument, month, month.AddMonths(1), 1, 0,
            batch => { candles.AddRange(batch); return true; }, null, CancellationToken.None);

        var candle = Assert.Single(candles);
        Assert.Equal((100m, 110m, 90m, 105m, 10m, 6m), (candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, candle.BuyVolume));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Bybit_fake_socket_uses_configured_endpoint_identity_and_preserves_actual_venue_book_metadata()
    {
        using var handler = new FixtureHttp(_ => Json(BybitInstruments));
        using var http = new HttpClient(handler);
        var socket = new FixtureSocket([
            $$"""{"topic":"publicTrade.BTCUSDT","data":[{"T":{{Ms(Start)}},"S":"Buy","p":"100.25","v":"2"}]}""",
            """{"topic":"orderbook.200.BTCUSDT","type":"snapshot","data":{"u":42,"seq":100,"b":[["100","7"]],"a":[["100.25","8"]]}}""",
        ]);
        Uri? connected = null;
        var endpoint = new Uri("wss://socket.fixture/testnet");
        using var feed = new BybitFeed(http, new Uri("https://bybit.fixture"), endpoint,
            connectWebSocket: (uri, _) => { connected = uri; return Task.FromResult<WebSocket>(socket); }, feedId: BybitFeed.TestnetId);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new SocketObserver();
        using var subscription = feed.Subscribe(instrument, sink);

        var trade = await sink.Trade.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var depth = await sink.Depth.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await socket.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        subscription.Dispose();
        await socket.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(endpoint, connected);
        Assert.Equal(BybitFeed.TestnetId, instrument.FeedId);
        Assert.Equal(100.25m, trade.Price);
        Assert.Equal(2m, trade.Volume);
        Assert.True(trade.IsObservedTrade);
        Assert.StartsWith("observation:", trade.Provenance.SourceEventId);
        Assert.True(trade.Provenance.IsPartial);
        Assert.Equal(BybitFeed.TestnetId, depth.ProviderId);
        Assert.Equal("Bybit", depth.Venue);
        Assert.Equal(42, depth.Sequence);
        Assert.Equal(400, depth.Snapshot.BestBidTicks);
        Assert.Equal(7m, depth.Snapshot.BestBidSize);
        Assert.Equal(401, depth.Snapshot.BestAskTicks);
        Assert.True((depth.Coverage & DepthCoverage.PartialBook) != 0);
        Assert.NotEqual(default, depth.ReceivedAt);
    }

    [Theory]
    [InlineData(BybitFeed.LinearId, "api.bybit.com", "stream.bybit.com")]
    [InlineData(BybitFeed.TestnetId, "api-testnet.bybit.com", "stream-testnet.bybit.com")]
    public async Task Bybit_environment_identity_selects_matching_default_endpoints_without_using_real_networks(string feedId, string restHost, string socketHost)
    {
        using var handler = new FixtureHttp(request =>
        {
            Assert.Equal(restHost, request.RequestUri!.Host);
            return Json(BybitInstruments);
        });
        using var http = new HttpClient(handler);
        var socket = new FixtureSocket([]);
        Uri? connected = null;
        using var feed = new BybitFeed(http, feedId: feedId,
            connectWebSocket: (uri, _) => { connected = uri; return Task.FromResult<WebSocket>(socket); });
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        using var subscription = feed.Subscribe(instrument, new SocketObserver());
        await socket.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        subscription.Dispose();
        await socket.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(feedId, instrument.FeedId);
        Assert.Equal(socketHost, connected!.Host);
        Assert.Equal("/v5/public/linear", connected.AbsolutePath);
    }

    [Fact]
    public async Task Explicit_primary_HTTP_ownership_releases_injected_clients_for_both_constituent_types()
    {
        using var bybitHandler = new FixtureHttp(_ => Json(BybitInstruments));
        using var bybitHttp = new HttpClient(bybitHandler);
        var bybit = new BybitFeed(bybitHttp, ownsHttpClient: true);
        bybit.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bybitHttp.GetAsync("https://unused.fixture"));

        using var binanceHandler = new FixtureHttp(_ => Json(BinanceInstruments));
        using var binanceHttp = new HttpClient(binanceHandler);
        var binance = new BinanceFeed(true, httpClient: binanceHttp, ownsHttpClient: true);
        binance.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => binanceHttp.GetAsync("https://unused.fixture"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_socket_policy_failures_are_reported_before_any_default_endpoint_can_connect(bool binance)
    {
        using var handler = new FixtureHttp(request => throw new InvalidOperationException("No HTTP request is allowed: " + request.RequestUri));
        using var http = new HttpClient(handler);
        Action<ClientWebSocketOptions> policy = _ => throw new InvalidOperationException("fixture proxy policy failure");
        using IMarketDataFeed feed = binance
            ? new BinanceFeed(true, httpClient: http, configureSocket: policy)
            : new BybitFeed(http, configureSocket: policy);
        var instrument = new Instrument { FeedId = feed.Id, Symbol = "BTCUSDT", TickSize = 0.1m };
        var sink = new SocketObserver();
        using var subscription = feed.Subscribe(instrument, sink);

        string failure = await sink.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        subscription.Dispose();

        Assert.Contains("fixture proxy policy failure", failure);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bybit_same_time_overlap_preserves_native_keys_or_explicitly_uncertain_unique_observations(bool nativeIds)
    {
        const string firstKey = "A1F2F7BC-65DE-4B78-80CF-000000000001";
        const string secondKey = "A1F2F7BC-65DE-4B78-80CF-000000000002";
        Dictionary<string, object> Tape(bool stream, string key)
        {
            var trade = stream
                ? new Dictionary<string, object> { ["T"] = Ms(Start), ["S"] = "Buy", ["p"] = "100.25", ["v"] = "2" }
                : new Dictionary<string, object>
                {
                    ["time"] = Ms(Start).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["side"] = "Buy", ["price"] = "100.25", ["size"] = "2",
                };
            if (nativeIds)
            {
                trade[stream ? "i" : "execId"] = key;
                if (stream) trade["execId"] = ""; // An empty alternative must not hide a valid native stream key.
            }
            return trade;
        }
        string historyBody = JsonSerializer.Serialize(new { result = new { list = new[] { Tape(false, firstKey), Tape(false, secondKey) } } });
        string liveFrame = JsonSerializer.Serialize(new { topic = "publicTrade.BTCUSDT", data = new[] { Tape(true, firstKey), Tape(true, secondKey) } });
        using var handler = new FixtureHttp(request => request.RequestUri!.AbsolutePath switch
        {
            "/v5/market/instruments-info" => Json(BybitInstruments),
            "/v5/market/recent-trade" => Json(historyBody),
            _ => throw new InvalidOperationException("Unexpected identity fixture request: " + request.RequestUri),
        });
        using var http = new HttpClient(handler);
        var socket = new FixtureSocket([liveFrame]);
        using var feed = new BybitFeed(http, new Uri("https://bybit.fixture"), new Uri("wss://socket.fixture"),
            connectWebSocket: (_, _) => Task.FromResult<WebSocket>(socket));
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var firstHistory = new List<Trade>();
        var overlappingHistory = new List<Trade>();
        await feed.LoadTradesAsync(instrument, Start, Start.AddMinutes(1), 99,
            batch => { firstHistory.AddRange(batch); return true; }, null, CancellationToken.None);
        await feed.LoadTradesAsync(instrument, Start, Start.AddMinutes(1), 99,
            batch => { overlappingHistory.AddRange(batch); return true; }, null, CancellationToken.None);
        using var restarted = new BybitFeed(http, new Uri("https://bybit.fixture"));
        var restartedHistory = new List<Trade>();
        await restarted.LoadTradesAsync(instrument, Start, Start.AddMinutes(1), 99,
            batch => { restartedHistory.AddRange(batch); return true; }, null, CancellationToken.None);
        var sink = new SocketObserver();
        using var subscription = feed.Subscribe(instrument, sink);
        await socket.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        subscription.Dispose();
        await socket.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, firstHistory.Count);
        Assert.Equal(2, overlappingHistory.Count);
        Assert.Equal(2, restartedHistory.Count);
        Assert.Equal(2, sink.ReceivedTrades.Count);
        var observations = firstHistory.Concat(overlappingHistory).Concat(restartedHistory).Concat(sink.ReceivedTrades).ToArray();
        Assert.All(observations, trade =>
        {
            Assert.Equal((Start, 100.25m, 2m, TradeSide.Buy), (trade.Time, trade.Price, trade.Volume, trade.Side));
            Assert.Equal(BybitFeed.LinearId, trade.Provenance.SourceIdentity);
            Assert.True(trade.IsObservedTrade);
        });
        if (nativeIds)
        {
            Assert.Equal(new[] { firstKey, secondKey }, firstHistory.Select(trade => trade.Provenance.SourceEventId).Order(StringComparer.Ordinal));
            Assert.Equal(new[] { firstKey, secondKey }, sink.ReceivedTrades.Select(trade => trade.Provenance.SourceEventId).Order(StringComparer.Ordinal));
            Assert.All(observations, trade => Assert.False(trade.Provenance.IsPartial));
            Assert.Equal(2, observations.Select(trade => TradeIdentity.From(trade)).Distinct().Count());
        }
        else
        {
            Assert.All(observations, trade =>
            {
                Assert.StartsWith("observation:", trade.Provenance.SourceEventId);
                Assert.True(Guid.TryParseExact(trade.Provenance.SourceEventId!["observation:".Length..], "N", out _));
                Assert.True(trade.Provenance.IsPartial);
            });
            Assert.Equal(8, observations.Select(trade => TradeIdentity.From(trade)).Distinct().Count());
        }
    }

    [Fact]
    public async Task Binance_real_depth_loop_publishes_a_book_disconnect_while_tape_is_already_degraded_and_recovers_from_a_real_REST_snapshot()
    {
        int snapshots = 0, tapeConnections = 0, bookConnections = 0;
        using var tape = new FixtureSocket([]);
        using var firstBook = new FixtureSocket([]);
        using var secondBook = new FixtureSocket([]);
        var firstBookConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBookConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FixtureHttp(request =>
        {
            Assert.Equal("/fapi/v1/depth", request.RequestUri!.AbsolutePath);
            int anchor = Interlocked.Increment(ref snapshots) == 1 ? 10 : 20;
            return Json(JsonSerializer.Serialize(new
            {
                lastUpdateId = anchor,
                bids = new[] { new[] { "100", "7" } },
                asks = new[] { new[] { "101", "8" } },
            }));
        });
        using var http = new HttpClient(handler);
        using var feed = new BinanceFeed(true, restOrigin: new Uri("https://binance.fixture"), httpClient: http,
            connectWebSocket: (uri, _) =>
            {
                if (uri.Query.Contains("aggTrade", StringComparison.Ordinal))
                    return Interlocked.Increment(ref tapeConnections) == 1
                        ? Task.FromResult<WebSocket>(tape)
                        : Task.FromException<WebSocket>(new IOException("The fake tape remains unavailable."));
                if (!uri.Query.Contains("depth", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected fake Binance socket: " + uri);
                if (Interlocked.Increment(ref bookConnections) == 1)
                {
                    firstBookConnected.TrySetResult();
                    return Task.FromResult<WebSocket>(firstBook);
                }
                secondBookConnected.TrySetResult();
                return Task.FromResult<WebSocket>(secondBook);
            });
        var instrument = new Instrument { FeedId = BinanceFeed.FuturesId, Symbol = "BTCUSDT", TickSize = 1 };
        var observer = new BinanceDepthObserver();
        using var subscription = feed.Subscribe(instrument, observer);
        var initialTask = observer.NextDepthAsync(update => update.IsFullRefresh && update.IsUsable);
        await firstBookConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = await EstablishBook(firstBook, 10, initialTask);
        Assert.Equal((100L, 101L), (initial.Snapshot.BestBidTicks, initial.Snapshot.BestAskTicks));
        Assert.InRange(initial.Sequence.GetValueOrDefault(-1), 11L, 42L);

        tape.CloseServer();
        await observer.Reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(observer.Updates, update => Assert.True(update.IsUsable));
        firstBook.CloseServer();
        var gap = await observer.NextDepthAsync(update => update.Coverage.HasFlag(DepthCoverage.Disconnected | DepthCoverage.Missing));
        Assert.False(gap.IsFullRefresh);
        Assert.False(gap.IsUsable);
        Assert.Equal(DepthGapReason.Disconnected, gap.GapBefore);
        Assert.True(gap.Coverage.HasFlag(DepthCoverage.SequenceGap | DepthCoverage.PartialBook));
        Assert.Equal(BinanceFeed.FuturesId, gap.ProviderId);
        Assert.Equal(BinanceFeed.FuturesId, gap.Venue);
        Assert.Empty(gap.Snapshot.Bids);
        Assert.Empty(gap.Snapshot.Asks);
        Assert.Null(gap.Sequence);
        Assert.Null(gap.FirstSequence);
        Assert.Null(gap.PreviousSequence);
        Assert.NotEqual(default, gap.ReceivedAt);

        var restoredTask = observer.NextDepthAsync(update => update.IsFullRefresh && update.IsUsable);
        await secondBookConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restored = await EstablishBook(secondBook, 20, restoredTask);
        Assert.Equal((100L, 101L), (restored.Snapshot.BestBidTicks, restored.Snapshot.BestAskTicks));
        Assert.InRange(restored.Sequence.GetValueOrDefault(-1), 21L, 52L);
        Assert.True(restored.Coverage.HasFlag(DepthCoverage.PartialBook));
        Assert.False(restored.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.False(restored.Coverage.HasFlag(DepthCoverage.Disconnected));
        Assert.Equal(FeedStatus.Reconnecting, observer.Status);
        Assert.Equal(2, Volatile.Read(ref snapshots));
        subscription.Dispose();

        static async Task<DepthUpdate> EstablishBook(FixtureSocket socket, long anchor, Task<DepthUpdate> ready)
        {
            // Valid venue packets continue while the asynchronous REST snapshot is being acquired.
            for (long offset = 0; offset < 32 && !ready.IsCompleted; offset++)
            {
                long previous = anchor + offset;
                socket.SendServer(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["stream"] = "btcusdt@depth@100ms",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["e"] = "depthUpdate", ["U"] = offset == 0 ? anchor : previous + 1,
                        ["u"] = previous + 1, ["pu"] = previous, ["b"] = Array.Empty<string[]>(), ["a"] = Array.Empty<string[]>(),
                    },
                }));
                await Task.WhenAny(ready, Task.Delay(1));
            }
            return await ready.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SocketObserver : IMarketDataSink
    {
        public TaskCompletionSource<Trade> Trade { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DepthUpdate> Depth { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<Trade> ReceivedTrades { get; } = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            foreach (var trade in trades) ReceivedTrades.Enqueue(trade);
            if (trades.Count > 0) Trade.TrySetResult(trades[0]);
        }
        public void OnDepth(Instrument instrument, DepthUpdate update) => Depth.TrySetResult(update);
        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            if (message?.Contains("fixture proxy policy failure", StringComparison.Ordinal) == true) Failure.TrySetResult(message);
        }
    }

    private sealed class BinanceDepthObserver : IMarketDataSink
    {
        private readonly Channel<DepthUpdate> _updates = Channel.CreateUnbounded<DepthUpdate>();
        private int _status;
        public ConcurrentQueue<DepthUpdate> Updates { get; } = new();
        public TaskCompletionSource Reconnecting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FeedStatus Status => (FeedStatus)Volatile.Read(ref _status);
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { }
        public void OnDepth(Instrument instrument, DepthUpdate update) { Updates.Enqueue(update); _updates.Writer.TryWrite(update); }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            Volatile.Write(ref _status, (int)status);
            if (status == FeedStatus.Reconnecting) Reconnecting.TrySetResult();
        }
        public async Task<DepthUpdate> NextDepthAsync(Func<DepthUpdate, bool> accepts)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var update in _updates.Reader.ReadAllAsync(deadline.Token))
                if (accepts(update)) return update;
            throw new InvalidOperationException("The fake Binance depth stream ended without its expected publication.");
        }
    }

    private sealed class FixtureSocket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> _frames = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        public FixtureSocket(IEnumerable<string> frames) { foreach (var frame in frames) SendServer(frame); }
        public void SendServer(string frame) => _frames.Writer.TryWrite((Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text));
        public void CloseServer() => _frames.Writer.TryWrite((Array.Empty<byte>(), WebSocketMessageType.Close));
        private WebSocketState _state = WebSocketState.Open;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { _state = WebSocketState.Closed; _frames.Writer.TryComplete(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_frames.Reader.TryRead(out var frame))
            {
                Waiting.TrySetResult();
                try { frame = await _frames.Reader.ReadAsync(cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            if (frame.Type == WebSocketMessageType.Close) _state = WebSocketState.CloseReceived;
            frame.Bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(frame.Bytes.Length, frame.Type, true);
        }
    }
}
