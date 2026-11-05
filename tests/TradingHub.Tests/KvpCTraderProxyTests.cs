using System.Net;
using System.Net.WebSockets;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.CTrader;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using TradingHub.Networking;

namespace TradingHub.Tests;

public sealed class KvpCTraderProxyTests
{
    [Fact]
    public async Task OAuthExchangeUsesCustomProxyAndPreservesEscapedParameters()
    {
        using var origin = new FakeHttpServer();
        origin.OnHttp = _ => (200, "{\"accessToken\":\"fake-access\",\"refreshToken\":\"fake-refresh\",\"expiresIn\":3600}");
        await using var proxy = new FakeProxyServer();
        var network = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var tokens = await CTraderAuth.ExchangeAsync("client & id", "secret+/&=value", "code?+/&", "http://127.0.0.1/callback?state=a&b=c",
            timeout.Token, origin.Http + "/apps", network);

        Assert.Equal(("fake-access", "fake-refresh", 3600L), tokens);
        FakeRequest request;
        lock (origin.Requests) request = Assert.Single(origin.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/apps/token", request.Route);
        Assert.Equal("authorization_code", request.Query("grant_type"));
        Assert.Equal("client & id", request.Query("client_id"));
        Assert.Equal("secret+/&=value", request.Query("client_secret"));
        Assert.Equal("code?+/&", request.Query("code"));
        Assert.Equal("http://127.0.0.1/callback?state=a&b=c", request.Query("redirect_uri"));
        var hop = Assert.Single(proxy.Requests);
        Assert.Equal("GET", hop.Method);
        Assert.Equal(new Uri(origin.Http).Authority, hop.Target.Authority);
        Assert.Equal("/apps/token", hop.Target.AbsolutePath);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task OAuthRedirectDoesNotForwardPrivateParameters(int status)
    {
        using var destination = new FakeHttpServer();
        destination.OnHttp = _ => (200, "{\"accessToken\":\"redirected-access\",\"refreshToken\":\"redirected-refresh\",\"expiresIn\":3600}");
        using var origin = new FakeHttpServer();
        origin.OnHttp = _ => (status, "{}");
        origin.ResponseHeaders = request => new Dictionary<string, string> { ["Location"] = destination.Http + request.Path };
        await using var proxy = new FakeProxyServer();
        var network = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CTraderAuth.ExchangeAsync("private-client", "private-secret", "private-code", "http://127.0.0.1/callback",
                timeout.Token, origin.Http + "/apps", network));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        FakeRequest request;
        lock (origin.Requests) request = Assert.Single(origin.Requests);
        Assert.Equal("private-secret", request.Query("client_secret"));
        Assert.Equal("private-code", request.Query("code"));
        lock (destination.Requests) Assert.Empty(destination.Requests);
        var hop = Assert.Single(proxy.Requests);
        Assert.Equal(new Uri(origin.Http).Authority, hop.Target.Authority);
    }

    [Fact]
    public async Task FeedLoadsHistoryAndLiveBidQuotesThroughCustomProxy()
    {
        using var origin = new FakeCTrader { TickPage = 30 };
        await using var proxy = new FakeProxyServer();
        var network = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });
        using var feed = new CTraderFeed(() => origin.Settings(), "proxy-ctrader", network);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var instruments = await feed.GetInstrumentsAsync(timeout.Token);
        var instrument = Assert.Single(instruments, i => i.Symbol == "US100");
        Assert.Equal("proxy-ctrader", instrument.FeedId);
        var from = DateTimeOffset.FromUnixTimeMilliseconds(origin.DayStartMs).UtcDateTime;
        var trades = new List<Trade>();
        await feed.LoadTradesAsync(instrument, from, from.AddMinutes(2), null,
            batch => { trades.AddRange(batch); return true; }, null, timeout.Token);
        Assert.Equal(100, trades.Count);
        Assert.Equal(20000.00m, trades[0].Price);
        Assert.Equal(20000.99m, trades[^1].Price);
        var klines = new List<Kline>();
        await feed.LoadKlinesAsync(instrument, from, from.AddMinutes(3), 1, 0,
            batch => { klines.AddRange(batch); return true; }, null, timeout.Token);
        Assert.Equal(3, klines.Count);
        Assert.Equal(20000.20m, klines[0].Open);
        Assert.Equal(20000.50m, klines[0].Close);

        var sink = new QuoteSink();
        using var subscription = feed.Subscribe(instrument, sink);
        var quote = await sink.FirstTrade.Task.WaitAsync(timeout.Token);
        await sink.FirstDepth.Task.WaitAsync(timeout.Token);
        Assert.Equal(20001.00m, quote.Price);
        Assert.Equal(0m, quote.Volume);
        var hop = Assert.Single(proxy.Requests);
        Assert.Equal("127.0.0.1", hop.Target.Host);
        Assert.Equal(origin.Server.Port, hop.Target.Port);
    }

    [Fact]
    public async Task SessionReconnectAuthenticatesAgainThroughCapturedProxy()
    {
        using var origin = new FakeCTrader();
        var firstSocket = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handleSocket = origin.Server.OnSocket!;
        origin.Server.OnSocket = async (path, socket, ct) =>
        {
            firstSocket.TrySetResult(socket);
            await handleSocket(path, socket, ct);
        };
        await using var proxy = new FakeProxyServer();
        var network = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });
        using var session = new CTraderSession(() => origin.Settings(), network);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.EnsureConnectedAsync(timeout.Token);
        Assert.Equal(99, session.AccountId);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ConnectionChanged += (connected, _) =>
        {
            if (!connected) disconnected.TrySetResult();
        };

        var socket = await firstSocket.Task.WaitAsync(timeout.Token);
        await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Fixture reconnect", timeout.Token);
        await disconnected.Task.WaitAsync(timeout.Token);
        await session.EnsureConnectedAsync(timeout.Token);

        Assert.True(session.IsConnected);
        Assert.Equal(99, session.AccountId);
        Assert.Contains(session.Symbols, symbol => symbol.Name == "US100");
        Assert.Equal(2, proxy.Requests.Count);
        Assert.All(proxy.Requests, hop => Assert.Equal(origin.Server.Port, hop.Target.Port));
    }

    [Fact]
    public async Task OAuthDoesNotFallBackToDirectWhenCustomProxyStops()
    {
        using var origin = new FakeHttpServer();
        origin.OnHttp = _ => (200, "{\"accessToken\":\"direct-access\",\"refreshToken\":\"direct-refresh\",\"expiresIn\":3600}");
        await using var proxy = new FakeProxyServer();
        var network = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });
        await proxy.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CTraderAuth.ExchangeAsync("private-client", "private-secret", "private-code", "http://127.0.0.1/callback",
                timeout.Token, origin.Http + "/apps", network));

        lock (origin.Requests) Assert.Empty(origin.Requests);
    }

    [Fact]
    public async Task SessionDoesNotFallBackToDirectWhenCustomProxyStops()
    {
        using var origin = new FakeCTrader();
        var openedOrigin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handleSocket = origin.Server.OnSocket!;
        origin.Server.OnSocket = async (path, socket, ct) =>
        {
            openedOrigin.TrySetResult();
            await handleSocket(path, socket, ct);
        };
        await using var proxy = new FakeProxyServer();
        var network = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });
        using var session = new CTraderSession(() => origin.Settings(), network);
        await proxy.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<WebSocketException>(() => session.EnsureConnectedAsync(timeout.Token));

        Assert.False(openedOrigin.Task.IsCompleted);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task WebSocketRedirectDoesNotEstablishCredentialBearingSessionAtDestination()
    {
        using var destination = new FakeCTrader();
        var openedDestination = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handleSocket = destination.Server.OnSocket!;
        destination.Server.OnSocket = async (path, socket, ct) =>
        {
            openedDestination.TrySetResult();
            await handleSocket(path, socket, ct);
        };
        using var origin = new FakeHttpServer();
        origin.SocketRedirect = _ => destination.Server.Http + "/";
        var settings = destination.Settings();
        settings.Url = origin.Ws + "/";
        await using var proxy = new FakeProxyServer();
        var network = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });
        using var session = new CTraderSession(() => settings, network);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<WebSocketException>(() => session.EnsureConnectedAsync(timeout.Token));

        Assert.False(openedDestination.Task.IsCompleted);
        lock (destination.Received) Assert.Empty(destination.Received);
        Assert.False(session.IsConnected);
        var hop = Assert.Single(proxy.Requests);
        Assert.Equal(origin.Port, hop.Target.Port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalAndLiveQuotesPreservePriceWithoutObservedFootprintOrBigTrades(bool downloadWholeDay)
    {
        using var origin = new FakeCTrader();
        await using var proxy = new FakeProxyServer();
        var network = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });
        using var feed = new CTraderFeed(() => origin.Settings(), "ctrader-quote-fixture", network);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(timeout.Token), item => item.Symbol == "US100");
        var from = DateTimeOffset.FromUnixTimeMilliseconds(origin.DayStartMs).UtcDateTime;
        var history = new List<Trade>();
        if (downloadWholeDay)
        {
            var downloaded = await feed.DownloadDayAsync(instrument, DateOnly.FromDateTime(from), null, timeout.Token);
            Assert.NotNull(downloaded);
            history.AddRange(downloaded);
        }
        else
        {
            await feed.LoadTradesAsync(instrument, from, from.AddMinutes(2), null,
                batch => { history.AddRange(batch); return true; }, null, timeout.Token);
        }
        Assert.Equal(100, history.Count);
        Assert.Equal(20000.00m, history[0].Price);
        Assert.Equal(20000.99m, history[^1].Price);

        origin.DayStartMs += 120_000;
        var sink = new QuoteSink();
        using var subscription = feed.Subscribe(instrument, sink);
        var live = await sink.FirstTrade.Task.WaitAsync(timeout.Token);
        Assert.Equal(20001.00m, live.Price);
        history.Add(live);
        Assert.All(history, quote =>
        {
            Assert.True(quote.Id > 0);
            Assert.Equal(0m, quote.Volume);
            Assert.Equal(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, quote.Provenance.Quality);
            Assert.Equal(0, quote.Provenance.ObservedTradeCount);
            Assert.Equal("ctrader-quote-fixture:US100:bid-quote", quote.Provenance.SourceIdentity);
        });

        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        foreach (var quote in history) data.Trades.Add(quote);
        Assert.Equal(20001.00m, data.LastPrice);
        data.Trades.Add(new Trade(long.MaxValue, live.Time.AddMinutes(1), 20002m, 3m, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "executed-print-fixture", ObservedTradeCount: 1)));
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var bigTrades = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 0.5m };
        Assert.Null(model.AddIndicator(bigTrades, applyDefaultColors: false).Error);
        model.Update();

        Assert.Equal(4, model.BarCount);
        Assert.Equal(20000.00m, model.GetCandle(0).Open);
        Assert.Equal(20000.99m, model.GetCandle(1).Close);
        Assert.Equal(20001.00m, model.GetCandle(2).Close);
        for (int bar = 0; bar < 3; bar++)
        {
            var candle = model.GetCandle(bar);
            Assert.False(model.Series[bar].HasRealTrades);
            Assert.True(model.Series[bar].HasSyntheticTrades);
            Assert.False(candle.HasObservedFootprint);
            Assert.False(candle.HasAggregatedFootprint);
            Assert.Empty(candle.GetAllPriceLevels());
            Assert.Equal(0, candle.ObservedTrades);
            Assert.Equal(0m, candle.MaxTradeVolume);
            Assert.Equal(0m, candle.Volume);
        }
        Assert.True(model.GetCandle(3).HasObservedFootprint);
        Assert.Equal(3m, model.GetCandle(3).MaxTradeVolume);
        Assert.Equal(1, bigTrades.BubbleCount);
    }

    private sealed class QuoteSink : IMarketDataSink
    {
        public TaskCompletionSource<Trade> FirstTrade { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DepthUpdate> FirstDepth { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            if (trades.Count > 0) FirstTrade.TrySetResult(trades[0]);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update) => FirstDepth.TrySetResult(update);

        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }
}
