using System.IO.Compression;
using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Market;
using TradingHub.Networking;

namespace TradingHub.Tests;

public sealed class KvpBinanceArchiveProxyTests
{
    private static ProxyHandlerFactory Network(FakeProxyServer proxy) => new(new NetworkProxyPolicy
    {
        Mode = NetworkProxyMode.Custom, Address = proxy.Address,
    });

    [Fact]
    public async Task SignedBalancesUseTheirCapturedProxyWhileAnotherAccountTransportUsesDifferentPolicy()
    {
        using var origin = new FakeBinance(false);
        origin.SetSpotBalance("USDT", 1250m, 50m);
        await using var firstProxy = new FakeProxyServer();
        await using var secondProxy = new FakeProxyServer();
        using var first = new BinanceAccountClient(origin.Settings, Network(firstProxy));
        var balance = Assert.Single(await first.GetBalancesAsync(CancellationToken.None), b => b.Asset == "USDT");
        Assert.Equal(1300m, balance.Wallet);
        Assert.Equal(1250m, balance.Available);
        using var second = new BinanceAccountClient(origin.Settings, Network(secondProxy));
        await second.GetBalancesAsync(CancellationToken.None);
        int previousFirst = firstProxy.Requests.Count, previousSecond = secondProxy.Requests.Count;
        await first.GetBalancesAsync(CancellationToken.None);
        Assert.True(firstProxy.Requests.Count > previousFirst);
        Assert.Equal(previousSecond, secondProxy.Requests.Count);
        Assert.Contains(firstProxy.Requests, r => r.Target.AbsolutePath == "/api/v3/account" && r.Headers.GetValueOrDefault("X-MBX-APIKEY") == FakeBinance.Key);
        Assert.Contains(secondProxy.Requests, r => r.Target.AbsolutePath == "/api/v3/account");
        Assert.Empty(origin.Refused);
    }

    [Fact]
    public async Task BrokerPublishesItsConfirmedAccountSnapshotThroughTheExplicitProxy()
    {
        using var origin = new FakeBinance(false);
        origin.SetSpotBalance("BTC", 3.25m, 1.5m);
        await using var proxy = new FakeProxyServer();
        var instrument = new Instrument
        {
            FeedId = "binance-spot", Symbol = "BTCUSDT", BaseAsset = "BTC", QuoteAsset = "USDT", TickSize = 0.1m, VolumeStep = 0.001m,
        };
        using var data = new InstrumentData(instrument, new SimulatedFeed());
        using var broker = new BinanceBroker(data, origin.Settings, "Fixture private account", Network(proxy));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            if (broker.PositionReady && broker.OrdersReady) ready.TrySetResult();
        }
        broker.Changed += Observe;
        try
        {
            Observe();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(4.75m, broker.Position.Volume);
            Assert.Empty(broker.ActiveOrders);
            Assert.Contains(proxy.Requests, r => r.Target.AbsolutePath == "/api/v3/account");
            Assert.Contains(proxy.Requests, r => r.Target.AbsolutePath == "/api/v3/openOrders");
            Assert.Empty(origin.Refused);
        }
        finally { broker.Changed -= Observe; }
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task PrivateBinanceRedirectNeverForwardsApiKeyOrSignedQuery(int status)
    {
        using var destination = new FakeHttpServer { OnHttp = _ => (200, "[]") };
        using var origin = new FakeHttpServer
        {
            OnHttp = _ => (status, "{}"),
            ResponseHeaders = _ => new Dictionary<string, string> { ["Location"] = destination.Http + "/private-target" },
        };
        await using var proxy = new FakeProxyServer();
        using var client = new BinanceAccountClient(new BinanceAccountSettings
        {
            Futures = true, Testnet = true, ApiKey = "fixture-private-api-key", ApiSecret = "fixture-private-api-secret", BaseUrl = origin.Http,
        }, Network(proxy));
        var error = await Record.ExceptionAsync(() => client.GetBalancesAsync(CancellationToken.None));
        Assert.NotNull(error);
        Assert.Contains(origin.Requests, r => r.Headers.GetValueOrDefault("X-MBX-APIKEY") == "fixture-private-api-key" && r.Path.Contains("signature=", StringComparison.Ordinal));
        Assert.Empty(destination.Requests);
        Assert.DoesNotContain(proxy.Requests, r => r.Target.Port == destination.Port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicArchiveDownloadUsesProxyAndReturnsParsedObservedTradeData(bool bybit)
    {
        byte[] payload = PublicPayload(bybit);
        using var origin = new FakeHttpServer
        {
            IsStream = _ => true,
            OnStream = async (_, output, ct) => await output.WriteAsync(payload, ct),
        };
        await using var proxy = new FakeProxyServer();
        using var client = Network(proxy).CreateHttpClient(TimeSpan.FromSeconds(5));
        var instrument = new Instrument
        {
            FeedId = bybit ? "bybit-linear" : "binance-futures", Symbol = "FIXTUREUSDT", Exchange = "Fixture archive", TickSize = 0.25m,
        };
        var trades = await PublicArchives.DownloadDayAsync(instrument, new DateOnly(2024, 1, 1), null, CancellationToken.None,
            http: client, baseOrigin: new Uri(origin.Http));
        Assert.NotNull(trades);
        Assert.Equal(2, trades.Count);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), trades[0].Time);
        Assert.Equal(100.25m, trades[0].Price);
        Assert.Equal(2.5m, trades[0].Volume);
        Assert.Equal(TradeSide.Buy, trades[0].Side);
        Assert.True(trades[0].IsObservedTrade);
        Assert.Equal(100.50m, trades[1].Price);
        Assert.Equal(3m, trades[1].Volume);
        Assert.Equal(TradeSide.Sell, trades[1].Side);
        if (!bybit)
        {
            Assert.False(trades[1].IsObservedTrade);
            Assert.True((trades[1].Provenance.Quality & DataQuality.Compact) != 0);
            Assert.Equal(3, trades[1].Provenance.ObservedTradeCount);
        }
        Assert.Contains(proxy.Requests, r => r.Method == "GET" && r.Target.Port == origin.Port &&
            r.Target.AbsolutePath.EndsWith(bybit ? ".csv.gz" : ".zip", StringComparison.Ordinal));
        Assert.Single(origin.Requests);
    }

    [Fact]
    public async Task AnUnavailableExplicitArchiveProxyDoesNotFallBackToDirectOrigin()
    {
        using var origin = new FakeHttpServer { OnHttp = _ => (200, "unreachable through stopped proxy") };
        var proxy = new FakeProxyServer();
        var network = Network(proxy);
        await proxy.DisposeAsync();
        using var client = network.CreateHttpClient(TimeSpan.FromSeconds(5));
        var instrument = new Instrument { FeedId = "binance-futures", Symbol = "FIXTUREUSDT", TickSize = 0.25m };
        await Assert.ThrowsAsync<HttpRequestException>(() => PublicArchives.DownloadDayAsync(instrument, new DateOnly(2024, 1, 1), null,
            CancellationToken.None, http: client, baseOrigin: new Uri(origin.Http)));
        Assert.Empty(origin.Requests);
    }

    private static byte[] PublicPayload(bool bybit)
    {
        using var data = new MemoryStream();
        if (bybit)
        {
            using (var gzip = new GZipStream(data, CompressionMode.Compress, leaveOpen: true))
            using (var writer = new StreamWriter(gzip, Encoding.UTF8))
            {
                writer.WriteLine("timestamp,symbol,side,size,price");
                writer.WriteLine("1704067200,FIXTUREUSDT,Buy,2.5,100.25");
                writer.WriteLine("1704067260,FIXTUREUSDT,Sell,3,100.50");
            }
        }
        else
        {
            using var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true);
            using var writer = new StreamWriter(zip.CreateEntry("fixture.csv").Open(), Encoding.UTF8);
            writer.WriteLine("agg_id,price,quantity,first_id,last_id,time,buyer_is_maker");
            writer.WriteLine("101,100.25,2.5,500,500,1704067200000,false");
            writer.WriteLine("102,100.50,3,501,503,1704067260000,true");
        }
        return data.ToArray();
    }
}
