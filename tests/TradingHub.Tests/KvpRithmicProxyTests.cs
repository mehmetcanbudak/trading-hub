using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Networking;

namespace TradingHub.Tests;

public sealed class KvpRithmicProxyTests
{
    private static readonly Instrument Es = new()
    {
        Symbol = "ES", FeedId = RithmicFeed.FeedId, Exchange = "CME",
        TickSize = 0.25m, VolumeStep = 1, PointValue = 50,
    };
    private const long T0 = 1_790_000_000;

    private static RithmicSettings Settings(FakeRithmicServer server) => new()
    {
        Uri = server.Uri, System = "Rithmic Paper Trading", User = "demo", Password = "demo", TimeoutMs = 5000,
    };

    private static ProxyHandlerFactory Network(FakeProxyServer proxy) =>
        new(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = proxy.Address });

    [Fact]
    public async Task MarketHistoryAndDirectoryProbeKeepTheirCapturedProxyAcrossReconnect()
    {
        using var server = new FakeRithmicServer { LastTradeSsboe = T0 + 10 };
        server.Ticks.Add((T0 + 1, 0, 5000.25, 4, true));
        await using var proxy = new FakeProxyServer();
        await using var otherProxy = new FakeProxyServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var feed = new RithmicFeed(Settings(server), network: Network(proxy));
        var sink = new Sink();
        using var subscription = feed.Subscribe(Es, sink);
        Assert.Equal(("ESZ6", "CME"), await feed.ContractAsync(Es, ct));
        await ReceiveTradeAsync(server, sink, 5001.25, ct);

        var history = new List<Trade>();
        await feed.LoadTradesAsync(Es, DateTimeOffset.FromUnixTimeSeconds(T0).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(T0 + 20).UtcDateTime, null,
            batch => { history.AddRange(batch); return true; }, null, ct);
        var historicalTrade = Assert.Single(history);
        Assert.Equal(5000.25m, historicalTrade.Price);
        Assert.Equal(4m, historicalTrade.Volume);
        Assert.Equal(TradeSide.Buy, historicalTrade.Side);
        Assert.Equal(3, proxy.Requests.Count(request => request.Method == "CONNECT"));

        Assert.Equal("Rithmic Paper Trading",
            Assert.Single(await RithmicClient.ListSystemsAsync(server.Uri, ct, network: Network(otherProxy))));
        await ReceiveTradeAsync(server, sink, 5002.25, ct);
        Assert.Equal(1, feed.TickerLogins);

        Assert.IsAssignableFrom<IReconnectable>(subscription).Reconnect();
        await WaitUntilAsync(() => feed.TickerLogins == 2, ct);
        await ReceiveTradeAsync(server, sink, 5003.25, ct);
        Assert.Equal(5, proxy.Requests.Count(request => request.Method == "CONNECT"));
        Assert.Single(otherProxy.Requests, request => request.Method == "CONNECT");
        Assert.All(proxy.Requests, request => Assert.Equal(new Uri(server.Uri).Port, request.Target.Port));
        Assert.Empty(proxy.Errors);
        Assert.Empty(otherProxy.Errors);
    }

    [Fact]
    public async Task OrderAndPnlPlantsConsumeAccountSnapshotsThroughCapturedProxyAfterReconnect()
    {
        using var server = new FakeRithmicServer();
        server.SeedPosition(true, 2, 5000.25);
        await using var proxy = new FakeProxyServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var session = new RithmicTradingSession(Settings(server), network: Network(proxy));
        session.Start();
        await WaitUntilAsync(() => session.IsConnected && session.OrdersReadyFor("ACC1") &&
            session.PositionReadyFor("ACC1", "ESZ6", "CME"), ct);
        Assert.Equal("ACC1", Assert.Single(session.Accounts).AccountId);
        Assert.Equal(2m, session.PositionFor("ACC1", "ESZ6", "CME")!.Net);
        Assert.Equal(5000.25m, session.PositionFor("ACC1", "ESZ6", "CME")!.AvgPrice);
        Assert.Equal(2, proxy.Requests.Count(request => request.Method == "CONNECT"));

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disconnected = false;
        session.Changed += () =>
        {
            if (!session.IsConnected) disconnected = true;
            else if (disconnected) reconnected.TrySetResult();
        };
        session.Reconfigure(Settings(server));
        await reconnected.Task.WaitAsync(ct);
        await WaitUntilAsync(() => session.IsConnected && session.OrdersReadyFor("ACC1") &&
            session.PositionReadyFor("ACC1", "ESZ6", "CME"), ct);
        Assert.Equal(2m, session.PositionFor("ACC1", "ESZ6", "CME")!.Net);
        Assert.Equal(4, proxy.Requests.Count(request => request.Method == "CONNECT"));
        Assert.All(proxy.Requests, request => Assert.Equal(new Uri(server.Uri).Port, request.Target.Port));
        Assert.Empty(proxy.Errors);
    }

    [Fact]
    public async Task BboRemainsMissingUntilBothFreshPricesAndSizesAndRefreshesOncePerConnection()
    {
        using var server = new FakeRithmicServer();
        await using var proxy = new FakeProxyServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var feed = new RithmicFeed(Settings(server), network: Network(proxy));
        var sink = new Sink();
        using var subscription = feed.Subscribe(Es, sink);
        await feed.ContractAsync(Es, ct);

        await server.PushBestBidOffer("ESZ6", bidPrice: 5000);
        var priceOnly = await sink.Depth.Reader.ReadAsync(ct);
        Assert.False(priceOnly.IsFullRefresh);
        Assert.False(priceOnly.IsUsable);
        Assert.Empty(priceOnly.Snapshot.Bids);
        Assert.True(priceOnly.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.Equal(DepthGapReason.Missing, priceOnly.GapBefore);

        await server.PushBestBidOffer("ESZ6", bidPrice: 5000, bidSize: 3);
        var bidOnly = await sink.Depth.Reader.ReadAsync(ct);
        Assert.False(bidOnly.IsFullRefresh);
        Assert.False(bidOnly.IsUsable);
        Assert.Equal(new BookLevel(20000, 3), Assert.Single(bidOnly.Snapshot.Bids));
        Assert.Empty(bidOnly.Snapshot.Asks);

        await server.PushBestBidOffer("ESZ6", askPrice: 5000.25);
        var askPriceOnly = await sink.Depth.Reader.ReadAsync(ct);
        Assert.False(askPriceOnly.IsFullRefresh);
        Assert.True(askPriceOnly.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.Empty(askPriceOnly.Snapshot.Asks);

        await server.PushBestBidOffer("ESZ6", askPrice: 5000.25, askSize: 5);
        var initial = await sink.Depth.Reader.ReadAsync(ct);
        Assert.True(initial.IsFullRefresh);
        Assert.True(initial.IsUsable);
        Assert.Equal(RithmicFeed.FeedId, initial.ProviderId);
        Assert.Equal(RithmicFeed.FeedId, initial.Venue);
        Assert.Equal(DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook, initial.Coverage);
        Assert.Equal(DepthGapReason.None, initial.GapBefore);
        Assert.Equal(new BookLevel(20000, 3), Assert.Single(initial.Snapshot.Bids));
        Assert.Equal(new BookLevel(20001, 5), Assert.Single(initial.Snapshot.Asks));
        Assert.Null(initial.Sequence);

        await server.PushBestBidOffer("ESZ6", bidPrice: 4999.75, bidSize: 7);
        var change = await sink.Depth.Reader.ReadAsync(ct);
        Assert.False(change.IsFullRefresh);
        Assert.True(change.IsUsable);
        Assert.Equal(DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook, change.Coverage);
        Assert.Equal(new BookLevel(19999, 7), Assert.Single(change.Snapshot.Bids));

        Assert.IsAssignableFrom<IReconnectable>(subscription).Reconnect();
        await WaitUntilAsync(() => feed.TickerLogins == 2, ct);
        await ReceiveTradeAsync(server, sink, 5002.25, ct);
        await server.PushBestBidOffer("ESZ6", askPrice: 5001, askSize: 11);
        var reconnectedAsk = await sink.Depth.Reader.ReadAsync(ct);
        Assert.False(reconnectedAsk.IsFullRefresh);
        Assert.False(reconnectedAsk.IsUsable);
        Assert.True(reconnectedAsk.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.Empty(reconnectedAsk.Snapshot.Bids);
        Assert.Equal(new BookLevel(20004, 11), Assert.Single(reconnectedAsk.Snapshot.Asks));

        await server.PushBestBidOffer("ESZ6", bidPrice: 5000.5, bidSize: 13);
        var restored = await sink.Depth.Reader.ReadAsync(ct);
        Assert.True(restored.IsFullRefresh);
        Assert.True(restored.IsUsable);
        Assert.Equal(DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook, restored.Coverage);
        Assert.Equal(new BookLevel(20002, 13), Assert.Single(restored.Snapshot.Bids));
        Assert.Equal(new BookLevel(20004, 11), Assert.Single(restored.Snapshot.Asks));
    }

    [Fact]
    public async Task RedirectedUpgradeNeverReceivesPrivateRithmicLoginCredentials()
    {
        using var destination = new FakeRithmicServer();
        using var redirect = new RedirectServer(destination.Uri);
        await using var proxy = new FakeProxyServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new RithmicClient(network: Network(proxy));
        await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            await client.ConnectAsync(redirect.Uri, timeout.Token);
            await client.LoginAsync("demo", "demo", "Rithmic Paper Trading", "TradingHub", "0.2", 2, timeout.Token);
        });
        Assert.Equal(1, redirect.Requests);
        lock (destination.Received) Assert.Empty(destination.Received);
        var hop = Assert.Single(proxy.Requests);
        Assert.Equal("CONNECT", hop.Method);
        Assert.Equal(new Uri(redirect.Uri).Port, hop.Target.Port);
        Assert.Empty(proxy.Errors);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(20, ct);
    }

    private static async Task ReceiveTradeAsync(FakeRithmicServer server, Sink sink, double price, CancellationToken ct)
    {
        while (!sink.Trades.Any(trade => trade.Price == (decimal)price))
        {
            ct.ThrowIfCancellationRequested();
            await server.PushTrade("ESZ6", price, 3, true);
            await Task.Delay(20, ct);
        }
        Assert.Contains(sink.Trades, trade => trade.Price == (decimal)price && trade.Volume == 3 && trade.Side == TradeSide.Buy);
    }

    private sealed class Sink : IMarketDataSink
    {
        public ConcurrentQueue<Trade> Trades { get; } = new();
        public Channel<DepthUpdate> Depth { get; } = Channel.CreateUnbounded<DepthUpdate>();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            foreach (var trade in trades) Trades.Enqueue(trade);
        }
        public void OnDepth(Instrument instrument, DepthUpdate update) => Depth.Writer.TryWrite(update);
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }

    private sealed class RedirectServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;
        public string Uri { get; }
        public int Requests => Volatile.Read(ref _requests);

        public RedirectServer(string destination)
        {
            for (int attempt = 0; ; attempt++)
            {
                int port = Random.Shared.Next(30000, 45000);
                _listener = new HttpListener();
                try
                {
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    Uri = $"ws://127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException)
                {
                    _listener.Close();
                    if (attempt >= 20) throw;
                }
            }
            _loop = ServeAsync(destination);
        }

        private async Task ServeAsync(string destination)
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception ex) when (_stop.IsCancellationRequested && (ex is HttpListenerException or ObjectDisposedException)) { return; }
                Interlocked.Increment(ref _requests);
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = destination;
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _loop.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
