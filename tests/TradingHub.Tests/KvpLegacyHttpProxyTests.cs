using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Alpaca;
using TradingHub.Feeds.Databento;
using TradingHub.Feeds.Oanda;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using TradingHub.Networking;

namespace TradingHub.Tests;

public sealed class KvpLegacyHttpProxyTests
{
    private static readonly DateOnly Day = FakeBrokers.Day;

    [Fact]
    public async Task Oanda_routes_instruments_candles_and_live_prices_through_its_proxy()
    {
        using var origin = FakeBrokers.Oanda();
        await using var proxy = new FakeProxyServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var feed = new OandaFeed(new OandaSettings
        {
            Token = "TOKEN", RestUrl = origin.Http, StreamUrl = origin.Http,
        }, network: Network(proxy));

        var instruments = await feed.GetInstrumentsAsync(deadline.Token);
        var nas = Assert.Single(instruments, i => i.Symbol == "NAS100_USD");
        Assert.Equal(0.1m, nas.TickSize);
        Assert.Equal(0.00001m, Assert.Single(instruments, i => i.Symbol == "EUR_USD").TickSize);
        var start = Day.ToDateTime(new TimeOnly(13, 0), DateTimeKind.Utc);
        var history = new List<Trade>();
        await feed.LoadTradesAsync(nas, start, start.AddMinutes(1), null,
            batch => { history.AddRange(batch); return true; }, null, deadline.Token);
        Assert.Equal(48, history.Count);
        Assert.Equal(start, history[0].Time);
        Assert.Equal(20000m, history[0].Price);
        Assert.All(history, trade =>
        {
            Assert.Equal(2m, trade.Volume);
            Assert.True(trade.Id > 0);
            Assert.Equal(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, trade.Provenance.Quality);
            Assert.Equal(5, trade.Provenance.ResolutionSeconds);
            Assert.Equal(0, trade.Provenance.ObservedTradeCount);
            Assert.Equal("oanda:midpoint-candles:quote-count", trade.Provenance.SourceIdentity);
            Assert.False(trade.IsObservedTrade);
        });
        Assert.True(history.Zip(history.Skip(1)).All(pair => pair.Second.Id > pair.First.Id));

        var bars = new List<Kline>();
        await feed.LoadKlinesAsync(nas, start, start.AddMinutes(1), 1, 0,
            batch => { bars.AddRange(batch); return true; }, null, deadline.Token);
        var bar = Assert.Single(bars);
        Assert.Equal(20000m, bar.Open);
        Assert.Equal(8m, bar.Volume);
        Assert.Equal(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, bar.Provenance.Quality);
        Assert.Equal(60, bar.Provenance.ResolutionSeconds);
        Assert.Equal(0, bar.Provenance.ObservedTradeCount);
        Assert.Equal("oanda:midpoint-candles:quote-count", bar.Provenance.SourceIdentity);

        var sink = new Observer();
        using (feed.Subscribe(nas, sink))
        {
            var trade = await sink.Trades.Reader.ReadAsync(deadline.Token);
            var depth = await sink.Depth.Reader.ReadAsync(deadline.Token);
            Assert.Equal(20000.5m, trade.Price);
            Assert.Equal(0m, trade.Volume);
            Assert.Equal(200000L, depth.Snapshot.BestBidTicks);
            Assert.Equal(200010L, depth.Snapshot.BestAskTicks);
            Assert.Equal(10m, depth.Snapshot.BestBidSize);
        }
        await sink.Disconnected.Task.WaitAsync(deadline.Token);

        var hops = proxy.Requests.ToArray();
        Assert.Contains(hops, request => request.Target.AbsolutePath == "/v3/accounts");
        Assert.Contains(hops, request => request.Target.AbsolutePath.EndsWith("/instruments", StringComparison.Ordinal));
        Assert.Contains(hops, request => request.Target.AbsolutePath.EndsWith("/candles", StringComparison.Ordinal));
        Assert.Contains(hops, request => request.Target.AbsolutePath.EndsWith("/pricing/stream", StringComparison.Ordinal));
        Assert.All(hops, request =>
        {
            Assert.Equal(origin.Port, request.Target.Port);
            Assert.Equal("Bearer TOKEN", request.Headers["Authorization"]);
            Assert.Equal("RFC3339", request.Headers["Accept-Datetime-Format"]);
        });
    }

    [Fact]
    public async Task Oanda_midpoint_quotes_preserve_prices_without_inventing_observed_trades_or_footprint()
    {
        using var origin = FakeBrokers.Oanda();
        await using var proxy = new FakeProxyServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var feed = new OandaFeed(new OandaSettings
        {
            Token = "TOKEN", AccountId = "101-001-1", RestUrl = origin.Http, StreamUrl = origin.Http,
        }, network: Network(proxy));
        var nas = new Instrument { Symbol = "NAS100_USD", FeedId = OandaFeed.FeedId, TickSize = 0.1m };
        var quotes = new List<Trade>();
        var sink = new Observer();
        using (feed.Subscribe(nas, sink))
        {
            for (int i = 0; i < 5; i++) quotes.Add(await sink.Trades.Reader.ReadAsync(deadline.Token));
        }
        await sink.Disconnected.Task.WaitAsync(deadline.Token);

        Assert.Equal(new[] { 20000.5m, 20001.5m, 20002.5m, 20003.5m, 20004.5m }, quotes.Select(quote => quote.Price));
        Assert.True(quotes.Zip(quotes.Skip(1)).All(pair => pair.Second.Id > pair.First.Id && pair.Second.Time > pair.First.Time));
        Assert.All(quotes, quote =>
        {
            Assert.True(quote.Id > 0);
            Assert.Equal(0m, quote.Volume);
            Assert.Equal(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, quote.Provenance.Quality);
            Assert.Equal(0, quote.Provenance.ObservedTradeCount);
            Assert.Equal("oanda:midpoint-quotes", quote.Provenance.SourceIdentity);
            Assert.False(quote.IsObservedTrade);
        });

        var store = new TradeStore(nas);
        foreach (var quote in quotes) store.Add(quote);
        var series = new CandleSeries(nas, ChartPeriod.M1);
        series.Rebuild(store);
        var candle = new IndicatorCandle(Assert.Single(series.Candles), series);
        Assert.Equal(20000.5m, candle.Open);
        Assert.Equal(20004.5m, candle.Close);
        Assert.Equal(20004.5m, candle.High);
        Assert.Equal(20000.5m, candle.Low);
        Assert.Equal(0m, candle.Volume);
        Assert.Equal(0m, candle.Delta);
        Assert.Equal(0, candle.ObservedTrades);
        Assert.Equal(0m, candle.MaxTradeVolume);
        Assert.True(candle.IsSynthetic);
        Assert.True(candle.HasInferredSide);
        Assert.False(candle.HasRawTrades);
        Assert.False(candle.HasObservedFootprint);
        Assert.False(candle.HasAggregatedFootprint);
        Assert.False(candle.IsFootprintComplete);
        Assert.Empty(candle.GetAllPriceLevels());
        Assert.Empty(candle.GetProfilePriceLevels());

        var data = new InstrumentData(nas, new SimulatedFeed());
        foreach (var quote in quotes) data.Trades.Add(quote);
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var bigTrades = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 1 };
        Assert.Null(model.AddIndicator(bigTrades, applyDefaultColors: false).Error);
        model.Update();
        Assert.Equal(0, bigTrades.BubbleCount);
    }

    [Fact]
    public async Task Alpaca_routes_asset_lists_paginated_trades_and_bars_through_its_proxy()
    {
        using var origin = FakeBrokers.Alpaca();
        await using var proxy = new FakeProxyServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var feed = new AlpacaFeed(new AlpacaSettings
        {
            KeyId = " KEY ", Secret = " SECRET ", DataUrl = origin.Http, TradingUrl = origin.Http,
        }, network: Network(proxy));

        var instruments = await feed.GetInstrumentsAsync(deadline.Token);
        var spy = Assert.Single(instruments, instrument => instrument.Symbol == "SPY");
        Assert.DoesNotContain(instruments, instrument => instrument.Symbol == "XYZ");
        Assert.Equal("ARCA", spy.Exchange);
        Assert.Equal(0.01m, spy.TickSize);
        var trades = await feed.DownloadDayAsync(spy, Day, null, deadline.Token);
        Assert.NotNull(trades);
        Assert.Equal(new[] { 580.10m, 580.11m, 580.11m, 580.05m, 580.07m }, trades.Select(trade => trade.Price));
        Assert.Equal(new[] { 100m, 50m, 20m, 10m, 5m }, trades.Select(trade => trade.Volume));
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Buy, TradeSide.Buy, TradeSide.Sell, TradeSide.Buy }, trades.Select(trade => trade.Side));
        Assert.True(trades.Zip(trades.Skip(1)).All(pair => pair.Second.Id > pair.First.Id));
        var start = Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var bars = new List<Kline>();
        await feed.LoadKlinesAsync(spy, start, start.AddDays(1), 1, 0,
            batch => { bars.AddRange(batch); return true; }, null, deadline.Token);
        Assert.Equal(2, bars.Count);
        Assert.Equal(120000m, bars[0].Volume);

        var hops = proxy.Requests.ToArray();
        Assert.Contains(hops, request => request.Target.AbsolutePath == "/v2/assets");
        var pages = hops.Where(request => request.Target.AbsolutePath == "/v2/stocks/SPY/trades").ToArray();
        Assert.Equal(2, pages.Length);
        Assert.Contains(pages, request => request.Target.Query.Contains("page_token=p2", StringComparison.Ordinal));
        Assert.Contains(hops, request => request.Target.AbsolutePath == "/v2/stocks/SPY/bars");
        Assert.All(hops, request =>
        {
            Assert.Equal(origin.Port, request.Target.Port);
            Assert.Equal("KEY", request.Headers["APCA-API-KEY-ID"]);
            Assert.Equal("SECRET", request.Headers["APCA-API-SECRET-KEY"]);
        });
    }

    [Fact]
    public async Task Alpaca_reconnects_over_its_original_proxy_while_another_consumer_uses_a_different_proxy()
    {
        using var origin = FakeBrokers.Alpaca();
        await using var streamProxy = new FakeProxyServer();
        await using var otherProxy = new FakeProxyServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var feed = new AlpacaFeed(new AlpacaSettings
        {
            KeyId = "KEY", Secret = "SECRET", StreamUrl = origin.Ws + "/v2/iex",
        }, network: Network(streamProxy));
        var spy = new Instrument { Symbol = "SPY", FeedId = AlpacaFeed.FeedId, TickSize = 0.01m };
        var sink = new Observer();
        using (var subscription = feed.Subscribe(spy, sink))
        {
            var first = await sink.Trades.Reader.ReadAsync(deadline.Token);
            var second = await sink.Trades.Reader.ReadAsync(deadline.Token);
            var firstDepth = await sink.Depth.Reader.ReadAsync(deadline.Token);
            Assert.Equal(580.12m, first.Price);
            Assert.Equal(100m, first.Volume);
            Assert.Equal(TradeSide.Sell, second.Side);
            Assert.Equal(58010L, firstDepth.Snapshot.BestBidTicks);
            Assert.Equal(58012L, firstDepth.Snapshot.BestAskTicks);

            using var other = new AlpacaFeed(new AlpacaSettings
            {
                KeyId = "KEY", Secret = "SECRET", TradingUrl = origin.Http,
            }, network: Network(otherProxy));
            Assert.Contains(await other.GetInstrumentsAsync(deadline.Token), instrument => instrument.Symbol == "SPY");

            Assert.IsAssignableFrom<IReconnectable>(subscription).Reconnect();
            var reconnected = await sink.Trades.Reader.ReadAsync(deadline.Token);
            var reconnectedDepth = await sink.Depth.Reader.ReadAsync(deadline.Token);
            Assert.Equal(580.12m, reconnected.Price);
            Assert.True(reconnected.Id > second.Id);
            Assert.Equal(3m, reconnectedDepth.Snapshot.BestBidSize);
        }
        await sink.Disconnected.Task.WaitAsync(deadline.Token);

        var streamHops = streamProxy.Requests.ToArray();
        Assert.Equal(2, streamHops.Length);
        Assert.All(streamHops, request =>
        {
            Assert.Equal(origin.Port, request.Target.Port);
            Assert.True(request.Method == "CONNECT" ||
                request.Headers.TryGetValue("Upgrade", out var upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal("/v2/assets", Assert.Single(otherProxy.Requests).Target.AbsolutePath);
    }

    [Fact]
    public async Task Alpaca_rejects_upgrade_redirects_before_contacting_or_authenticating_the_private_target()
    {
        using var privateTarget = new FakeHttpServer { OnHttp = _ => (200, "{}") };
        var redirectedHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedPaths = new ConcurrentQueue<string>();
        var redirectedMessage = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        privateTarget.OnSocket = async (_, socket, ct) =>
        {
            redirectedHandshake.TrySetResult();
            redirectedMessage.TrySetResult(await FakeHttpServer.ReceiveText(socket, ct));
        };
        using var origin = new FakeHttpServer
        {
            SocketRedirect = path =>
            {
                requestedPaths.Enqueue(path);
                return privateTarget.Http + "/private";
            },
        };
        await using var proxy = new FakeProxyServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var feed = new AlpacaFeed(new AlpacaSettings
        {
            KeyId = "KEY", Secret = "SECRET", StreamUrl = origin.Ws + "/v2/iex",
        }, network: Network(proxy));
        var spy = new Instrument { Symbol = "SPY", FeedId = AlpacaFeed.FeedId, TickSize = 0.01m };
        var sink = new Observer();
        using (feed.Subscribe(spy, sink))
        {
            var rejected = sink.Reconnecting.Reader.ReadAsync(deadline.Token).AsTask();
            var observed = await Task.WhenAny(rejected, redirectedHandshake.Task).WaitAsync(deadline.Token);
            Assert.Same(rejected, observed);
            await rejected;
        }
        await sink.Disconnected.Task.WaitAsync(deadline.Token);

        Assert.False(redirectedHandshake.Task.IsCompleted);
        Assert.False(redirectedMessage.Task.IsCompleted);
        Assert.False(sink.Trades.Reader.TryRead(out _));
        Assert.False(sink.Depth.Reader.TryRead(out _));
        var originPaths = requestedPaths.ToArray();
        Assert.NotEmpty(originPaths);
        Assert.All(originPaths, path => Assert.Equal("/v2/iex", path));
        var hops = proxy.Requests.ToArray();
        Assert.NotEmpty(hops);
        Assert.All(hops, request =>
        {
            Assert.Equal(new Uri(origin.Http).Authority, request.Target.Authority);
            if (request.Method != "CONNECT")
            {
                Assert.Equal("GET", request.Method);
                Assert.Equal("/v2/iex", request.Target.AbsolutePath);
                Assert.Equal("websocket", request.Headers["Upgrade"], ignoreCase: true);
            }
        });
        lock (privateTarget.Requests) Assert.Empty(privateTarget.Requests);
    }

    [Fact]
    public async Task Databento_routes_cost_range_and_streamed_trade_history_through_its_proxy()
    {
        using var origin = FakeBrokers.Databento();
        await using var proxy = new FakeProxyServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var client = new DatabentoClient(" db-KEY ", origin.Http + "/v0", network: Network(proxy));

        Assert.Equal(1.2575m, await client.CostAsync("NQ", Day, Day, deadline.Token));
        var (start, end) = await client.RangeAsync(deadline.Token);
        Assert.Equal(new DateTime(2010, 6, 6, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), end);
        var nq = new Instrument { Symbol = "NQ", FeedId = "rithmic", TickSize = 0.25m, PointValue = 20 };
        var trades = await client.DownloadDayAsync(nq, Day, null, deadline.Token);
        Assert.NotNull(trades);
        Assert.Equal(new[] { 20000.25m, 20000m, 19999.75m, 20000.25m }, trades.Select(trade => trade.Price));
        Assert.Equal(new[] { 2m, 1m, 3m, 1m }, trades.Select(trade => trade.Volume));
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Sell, TradeSide.Sell, TradeSide.Buy }, trades.Select(trade => trade.Side));
        Assert.Equal(Day.ToDateTime(new TimeOnly(14, 30), DateTimeKind.Utc), trades[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(1), trades[1].Time - trades[0].Time);
        Assert.True(trades.Zip(trades.Skip(1)).All(pair => pair.Second.Id > pair.First.Id));

        var hops = proxy.Requests.ToArray();
        Assert.Equal(3, hops.Length);
        Assert.Contains(hops, request => request.Target.AbsolutePath == "/v0/metadata.get_cost");
        Assert.Contains(hops, request => request.Target.AbsolutePath == "/v0/metadata.get_dataset_range");
        Assert.Contains(hops, request => request.Target.AbsolutePath == "/v0/timeseries.get_range");
        Assert.All(hops, request =>
        {
            Assert.Equal(origin.Port, request.Target.Port);
            Assert.Equal("Basic ZGItS0VZOg==", request.Headers["Authorization"]);
        });
    }

    [Theory]
    [InlineData("Oanda")]
    [InlineData("Alpaca")]
    [InlineData("DatabentoCost")]
    [InlineData("DatabentoHistory")]
    public async Task Authenticated_http_rejects_redirects_without_contacting_the_private_target(string consumer)
    {
        using var privateTarget = new FakeHttpServer { OnHttp = _ => (200, "{}") };
        using var origin = new FakeHttpServer
        {
            OnHttp = _ => (307, "{\"message\":\"provider redirect\"}"),
            ResponseHeaders = _ => new Dictionary<string, string> { ["Location"] = privateTarget.Http + "/private" },
        };
        await using var proxy = new FakeProxyServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var network = Network(proxy);
        switch (consumer)
        {
            case "Oanda":
            {
                using var feed = new OandaFeed(new OandaSettings { Token = "TOKEN", RestUrl = origin.Http }, network: network);
                await Assert.ThrowsAsync<HttpRequestException>(() => feed.AccountAsync(deadline.Token));
                break;
            }
            case "Alpaca":
            {
                using var feed = new AlpacaFeed(new AlpacaSettings { KeyId = "KEY", Secret = "SECRET", TradingUrl = origin.Http }, network: network);
                await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetInstrumentsAsync(deadline.Token));
                break;
            }
            case "DatabentoCost":
            {
                using var client = new DatabentoClient("db-KEY", origin.Http + "/v0", network: network);
                await Assert.ThrowsAsync<HttpRequestException>(() => client.CostAsync("NQ", Day, Day, deadline.Token));
                break;
            }
            case "DatabentoHistory":
            {
                using var client = new DatabentoClient("db-KEY", origin.Http + "/v0", network: network);
                var nq = new Instrument { Symbol = "NQ", FeedId = "rithmic", TickSize = 0.25m };
                await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadDayAsync(nq, Day, null, deadline.Token));
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(consumer));
        }

        var hop = Assert.Single(proxy.Requests);
        Assert.Equal(origin.Port, hop.Target.Port);
        if (consumer == "Alpaca")
        {
            Assert.Equal("KEY", hop.Headers["APCA-API-KEY-ID"]);
            Assert.Equal("SECRET", hop.Headers["APCA-API-SECRET-KEY"]);
        }
        else
        {
            Assert.Equal(consumer == "Oanda" ? "Bearer TOKEN" : "Basic ZGItS0VZOg==", hop.Headers["Authorization"]);
        }
        lock (privateTarget.Requests) Assert.Empty(privateTarget.Requests);
    }

    [Fact]
    public async Task Oanda_live_http_rejects_redirects_without_sending_prices_or_contacting_the_private_target()
    {
        using var privateTarget = new FakeHttpServer { OnHttp = _ => (200, "{}") };
        using var origin = new FakeHttpServer
        {
            OnHttp = _ => (307, "{}"),
            ResponseHeaders = _ => new Dictionary<string, string> { ["Location"] = privateTarget.Http + "/private" },
        };
        await using var proxy = new FakeProxyServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var feed = new OandaFeed(new OandaSettings
        {
            Token = "TOKEN", AccountId = "101-001-1", RestUrl = origin.Http, StreamUrl = origin.Http,
        }, network: Network(proxy));
        var nas = new Instrument { Symbol = "NAS100_USD", FeedId = OandaFeed.FeedId, TickSize = 0.1m };
        var sink = new Observer();
        using (feed.Subscribe(nas, sink))
        {
            await sink.Reconnecting.Reader.ReadAsync(deadline.Token);
            Assert.False(sink.Trades.Reader.TryRead(out _));
            Assert.False(sink.Depth.Reader.TryRead(out _));
        }
        await sink.Disconnected.Task.WaitAsync(deadline.Token);

        var hops = proxy.Requests.ToArray();
        Assert.NotEmpty(hops);
        Assert.All(hops, request =>
        {
            Assert.Equal(origin.Port, request.Target.Port);
            Assert.Equal("/v3/accounts/101-001-1/pricing/stream", request.Target.AbsolutePath);
            Assert.Equal("Bearer TOKEN", request.Headers["Authorization"]);
        });
        lock (privateTarget.Requests) Assert.Empty(privateTarget.Requests);
    }

    private static ProxyHandlerFactory Network(FakeProxyServer proxy) => new(new NetworkProxyPolicy
    {
        Mode = NetworkProxyMode.Custom,
        Address = proxy.Address,
    });

    private sealed class Observer : IMarketDataSink
    {
        public Channel<Trade> Trades { get; } = Channel.CreateUnbounded<Trade>();
        public Channel<DepthUpdate> Depth { get; } = Channel.CreateUnbounded<DepthUpdate>();
        public Channel<string?> Reconnecting { get; } = Channel.CreateUnbounded<string?>();
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            foreach (var trade in trades) Trades.Writer.TryWrite(trade);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update) => Depth.Writer.TryWrite(update);

        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            if (status == FeedStatus.Reconnecting) Reconnecting.Writer.TryWrite(message);
            if (status == FeedStatus.Disconnected) Disconnected.TrySetResult();
        }
    }
}
