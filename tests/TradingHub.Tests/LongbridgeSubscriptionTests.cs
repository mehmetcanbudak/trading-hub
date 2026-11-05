using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Longbridge;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class LongbridgeSubscriptionTests
{
    [Fact]
    public async Task SubscribeAndReconnectDeliverAuthoritativeBaselinesBeforeApplyingPositionUpdates()
    {
        var settings = LongbridgeFeedTests.Settings();
        var transport = new LongbridgeFeedTests.Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        using var feed = new LongbridgeFeed(settings, LongbridgeFeedTests.Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        using var sink = new HistorySink(instrument, () => feed.Now);
        var commands = new ConcurrentQueue<byte>();
        var firstQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int queryCount = 0, subscribeCount = 0;
        transport.Override = (command, _, _) =>
        {
            commands.Enqueue(command);
            if (command == (byte)LongbridgeCommand.Depth)
            {
                int request = Interlocked.Increment(ref queryCount);
                if (request == 1) { firstQuery.TrySetResult(); return firstReply.Task; }
                return Task.FromResult(DepthSnapshot(request == 2 ? "203.00" : "211.00", request == 2 ? 30 : 60));
            }
            if (command == (byte)LongbridgeCommand.Subscribe)
            {
                int subscription = Interlocked.Increment(ref subscribeCount);
                transport.Emit(LongbridgeCommand.PushDepth, LongbridgeFeedTests.DepthPush(subscription == 1 ? 101 : 201,
                    (1, subscription == 1 ? "203.50" : "211.50", subscription == 1 ? 40 : 70)));
                (subscription == 1 ? firstSubscribed : reSubscribed).TrySetResult();
            }
            return Task.FromResult(Array.Empty<byte>());
        };
        using var lease = feed.Subscribe(instrument, sink);
        await firstQuery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(sink.History.Current);
        transport.Emit(LongbridgeCommand.PushDepth, LongbridgeFeedTests.DepthPush(100, (1, "199.00", 99)));
        Assert.Empty(sink.Updates);
        firstReply.SetResult(DepthSnapshot("202.00", 20));
        var baseline = await sink.Baselines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await firstSubscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(baseline.IsFullRefresh);
        Assert.Equal(DepthCoverage.SnapshotOnly | DepthCoverage.PartialBook, baseline.Coverage);
        Assert.Null(baseline.Sequence);
        Assert.Equal(new BookLevel(20300, 30), Assert.Single(baseline.Snapshot.Asks));
        Assert.Equal(new BookLevel(20000, 10), Assert.Single(baseline.Snapshot.Bids));
        Assert.Equal(new byte[] { 14, 14, 6 }, commands.Take(3));
        var firstDelivery = sink.Updates.First();
        Assert.True(firstDelivery.IsFullRefresh);
        Assert.DoesNotContain(sink.Updates, update => update.Snapshot.Asks.Any(level => level.PriceTicks == 19900));
        Assert.Equal(new BookLevel(20350, 40), Assert.Single(sink.History.Current!.Update.Snapshot.Asks));
        Assert.All(sink.Updates.Where(update => update.Changes.Count > 0), update => Assert.False(update.IsFullRefresh));

        transport.Disconnect();
        Assert.Null(sink.History.Current);
        transport.Emit(LongbridgeCommand.PushDepth, LongbridgeFeedTests.DepthPush(102, (1, "1.00", 999)));
        Assert.Null(sink.History.Current);
        transport.Connect();
        var reconnectBaseline = await sink.Baselines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await reSubscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reconnectBaseline.IsFullRefresh);
        Assert.Equal(DepthGapReason.Disconnected, reconnectBaseline.GapBefore);
        Assert.Equal(new BookLevel(21100, 60), Assert.Single(reconnectBaseline.Snapshot.Asks));
        Assert.False(reconnectBaseline.Coverage.HasFlag(DepthCoverage.Missing));
        Assert.False(reconnectBaseline.Coverage.HasFlag(DepthCoverage.Disconnected));
        Assert.Equal(new BookLevel(21150, 70), Assert.Single(sink.History.Current!.Update.Snapshot.Asks));
        Assert.Equal(3, Volatile.Read(ref queryCount));
        Assert.Equal(2, Volatile.Read(ref subscribeCount));
    }

    [Fact]
    public async Task ASecondConsumerReceivesItsOwnProviderBaselineBeforeLaterDeltas()
    {
        var settings = LongbridgeFeedTests.Settings();
        var transport = new LongbridgeFeedTests.Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        using var feed = new LongbridgeFeed(settings, LongbridgeFeedTests.Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        using var first = new HistorySink(instrument, () => feed.Now);
        using var second = new HistorySink(instrument, () => feed.Now);
        var commands = new ConcurrentQueue<byte>();
        int queries = 0;
        transport.Override = (command, _, _) =>
        {
            commands.Enqueue(command);
            return Task.FromResult(command == (byte)LongbridgeCommand.Depth
                ? DepthSnapshot(Interlocked.Increment(ref queries) == 1 ? "202.00" : "204.00", 20)
                : Array.Empty<byte>());
        };
        using var firstLease = feed.Subscribe(instrument, first);
        await first.Baselines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var secondLease = feed.Subscribe(instrument, second);
        var fresh = await second.Baselines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new BookLevel(20400, 20), Assert.Single(fresh.Snapshot.Asks));
        Assert.True(second.Updates.First().IsFullRefresh);
        Assert.Equal(new byte[] { 14, 6, 7, 14, 6 }, commands);
        transport.Emit(LongbridgeCommand.PushDepth, LongbridgeFeedTests.DepthPush(50, (1, "204.50", 25)));
        Assert.Equal(new BookLevel(20450, 25), Assert.Single(first.History.Current!.Update.Snapshot.Asks));
        Assert.Equal(new BookLevel(20450, 25), Assert.Single(second.History.Current!.Update.Snapshot.Asks));
    }

    [Fact]
    public async Task RealSocketReconnectRequestsDepthBeforeRestoringAnySubscription()
    {
        var settings = LongbridgeFeedTests.Settings();
        string? secret = null;
        var handler = new LongbridgeOAuthTests.HttpFixture
        {
            Reply = request => Task.FromResult(LongbridgeOAuthTests.Json(request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"fixture\",\"refresh_token\":\"fixture-refresh\",\"expires_in\":3600}"
                : "{\"data\":{\"otp\":\"fixture-quote-otp\"}}")),
        };
        using var http = new HttpClient(handler);
        var session = new LongbridgeSession(settings, http, () => secret, value => secret = value, new LongbridgeOAuthTests.Clock());
        var auth = await session.BeginAuthorizationAsync(new Uri("http://localhost:60355/callback"), default);
        await session.CompleteAuthorizationAsync(LongbridgeOAuthTests.Callback(LongbridgeOAuthTests.Query(auth.AuthorizationUri)["state"]), default);
        var requests = new ConcurrentQueue<(int Connection, byte Command)>();
        var firstSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LongbridgeProtocolTests.SocketFixture Connection(int number) => new()
        {
            ResponseBody = packet =>
            {
                requests.Enqueue((number, packet.Command));
                switch ((LongbridgeCommand)packet.Command)
                {
                    case LongbridgeCommand.Auth: return Array.Empty<byte>();
                    case LongbridgeCommand.Heartbeat: return packet.Body;
                    case LongbridgeCommand.StaticInfo:
                        return LongbridgeFeedTests.Message(w => w.Bytes(1, LongbridgeFeedTests.Message(info =>
                        { info.Text(1, "AAPL.US"); info.Text(3, "Apple Inc."); info.Text(6, "NASDAQ"); info.Text(7, "USD"); info.Number(8, 100); })));
                    case LongbridgeCommand.Depth: return DepthSnapshot(number == 1 ? "202.00" : "211.00", 20);
                    case LongbridgeCommand.Subscribe:
                        (number == 1 ? firstSubscribed : secondSubscribed).TrySetResult();
                        return Array.Empty<byte>();
                    case LongbridgeCommand.Unsubscribe: return Array.Empty<byte>();
                    default: throw new InvalidOperationException("Unexpected quote protocol command.");
                }
            },
        };
        var initial = Connection(1);
        var reconnected = Connection(2);
        var connections = new ConcurrentQueue<LongbridgeProtocolTests.SocketFixture>(new[] { initial, reconnected });
        await using var socket = new LongbridgeSocket(session, settings, () =>
            connections.TryDequeue(out var next) ? next : throw new InvalidOperationException("Unexpected extra reconnect."))
        { MinimumRequestInterval = TimeSpan.Zero, RequestTimeout = TimeSpan.FromSeconds(5) };
        using var feed = new LongbridgeFeed(settings, session, socket);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        using var sink = new HistorySink(instrument, () => feed.Now);
        using var lease = feed.Subscribe(instrument, sink);
        var baseline = await sink.Baselines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await firstSubscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(baseline.IsFullRefresh);
        Assert.NotNull(sink.History.Current);
        initial.Incoming.Writer.TryWrite(new byte[] { 3, 0, 0, 0, 0 });
        var refreshed = await sink.Baselines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await secondSubscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refreshed.IsFullRefresh);
        Assert.Equal(new BookLevel(21100, 20), Assert.Single(refreshed.Snapshot.Asks));
        Assert.NotNull(sink.History.Current);
        Assert.Equal(new byte[] { 14, 6 }, requests.Where(request => request.Connection == 2 &&
            request.Command != (byte)LongbridgeCommand.Auth && request.Command != (byte)LongbridgeCommand.Heartbeat).Select(request => request.Command));
    }

    [Fact]
    public async Task AFullProviderSnapshotWithMissingBidsDoesNotMakeDepthUsable()
    {
        var settings = LongbridgeFeedTests.Settings();
        var transport = new LongbridgeFeedTests.Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        await using var session = LongbridgeFeedTests.Session(settings, http);
        await using var feed = new LongbridgeFeed(settings, session, transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        using var sink = new HistorySink(instrument, () => feed.Now);
        using var lease = feed.Subscribe(instrument, sink);
        var baseline = await sink.Baselines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(baseline.IsFullRefresh);
        Assert.Equal(new BookLevel(20200, 20), Assert.Single(baseline.Snapshot.Asks));
        Assert.Empty(baseline.Snapshot.Bids);
        Assert.Null(sink.History.Current);
    }

    private static byte[] DepthSnapshot(string price, long volume) => LongbridgeFeedTests.Message(w =>
    {
        w.Text(1, "AAPL.US");
        w.Bytes(2, LongbridgeFeedTests.Message(level => { level.Number(1, 1); level.Text(2, price); level.Number(3, volume); }));
        w.Bytes(3, LongbridgeFeedTests.Message(level => { level.Number(1, 1); level.Text(2, "200.00"); level.Number(3, 10); }));
    });

    private sealed class HistorySink : IMarketDataSink, IDisposable
    {
        private readonly Func<DateTime> _now;
        public DepthHistory History { get; }
        public ConcurrentQueue<DepthUpdate> Updates { get; } = new();
        public Channel<DepthUpdate> Baselines { get; } = Channel.CreateUnbounded<DepthUpdate>();
        public HistorySink(Instrument instrument, Func<DateTime> now)
        {
            _now = now;
            History = new(instrument);
            History.Start(null, new DepthRecordingOptions { Enabled = false }, now());
        }
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { }
        public void OnDepth(Instrument instrument, DepthUpdate update)
        {
            History.Receive(update, _now());
            Updates.Enqueue(update);
            if (update.IsFullRefresh) Baselines.Writer.TryWrite(update);
        }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            if (status is FeedStatus.Connecting or FeedStatus.Error or FeedStatus.Disconnected)
                History.MarkGap(_now(), DepthGapReason.Disconnected, message);
        }
        public void Dispose() => History.Dispose();
    }
}
