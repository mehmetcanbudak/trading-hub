using System.Collections.Concurrent;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class IbDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ib-data-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc);
    public IbDataTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private IbFeed Feed(FakeIbGateway gateway, string profile = "fixture") => new(new("127.0.0.1", gateway.Port, 17, false, profile), _root) { RequestTimeout = TimeSpan.FromSeconds(2), TickRequestInterval = TimeSpan.Zero };
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, stop.Token);
    }
    [Fact]
    public async Task FragmentedHandshakeStartsApiWithConfiguredClientAndRejectsUnsupportedVersion()
    {
        await using (var gateway = new FakeIbGateway())
        using (var feed = Feed(gateway))
        {
            var client = await feed.ClientAsync(default);
            var session = await gateway.NextSessionAsync();
            Assert.Equal(17, session.ClientId);
            Assert.Equal(187, client.ServerVersion);
            Assert.Throws<NotSupportedException>(() => client.Send(3, "place-order"));
            Assert.Throws<NotSupportedException>(() => client.Send(5, "account-updates"));
        }
        await using var unsupported = new FakeIbGateway { ServerVersion = 186 };
        using var invalid = Feed(unsupported);
        await Assert.ThrowsAsync<NotSupportedException>(() => invalid.ClientAsync(default));
    }
    [Fact]
    public async Task ClientIdDenialIsNotReportedAsMarketDataReadiness()
    {
        await using var gateway = new FakeIbGateway { StartupError = 326 };
        using var feed = Feed(gateway);
        var error = await Assert.ThrowsAsync<IbException>(() => feed.ClientAsync(default));
        Assert.Equal(326, error.Code);
        Assert.Contains("already in use", error.Message);
    }
    [Fact]
    public async Task ConcurrentHistoryCancellationSendsWireCancelAndSuppressesLateReply()
    {
        await using var gateway = new FakeIbGateway();
        using var feed = Feed(gateway);
        var instrument = IbBarImport.InstrumentFor("MNQ");
        using var cancel = new CancellationTokenSource();
        var canceled = feed.BarsAsync(IbFeed.ContractOn(instrument, Start), Start.AddMinutes(1), "60 S", "1 min", cancel.Token);
        var session = await gateway.NextSessionAsync();
        var first = await session.NextAsync("20");
        var kept = feed.BarsAsync(IbFeed.ContractOn(instrument, Start), Start.AddMinutes(1), "60 S", "1 min", default);
        var second = await session.NextAsync("20");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        var wireCancel = await session.NextAsync("25");
        Assert.Equal(first[1], wireCancel[2]);
        await session.SendCoalescedAsync(
            new[] { "17", first[1], "from", "to", "1", "1793404740", "100", "101", "99", "100", "10", "100", "2" },
            new[] { "17", second[1], "from", "to", "1", "1793404740", "200", "201", "199", "200", "20", "200", "2" });
        Assert.Equal(200m, Assert.Single(await kept).Open);
    }
    [Fact]
    public async Task TimeoutCancelsAndHistoryEntitlementFailureLeavesCompleteCacheUntouched()
    {
        await using var gateway = new FakeIbGateway();
        using var feed = Feed(gateway);
        feed.RequestTimeout = TimeSpan.FromMilliseconds(150);
        var instrument = IbBarImport.InstrumentFor("MNQ");
        var timed = feed.BarsAsync(IbFeed.ContractOn(instrument, Start), Start, "60 S", "1 min", default);
        var session = await gateway.NextSessionAsync();
        var request = await session.NextAsync("20");
        await Assert.ThrowsAsync<TimeoutException>(() => timed);
        Assert.Equal(request[1], (await session.NextAsync("25"))[2]);
        var bars = new[] { new Kline(new DateTimeOffset(Start).ToUnixTimeMilliseconds(), 100, 101, 99, 100, 10, 0, false) };
        IbBarStore.MergeBars(_root, instrument, bars, default);
        string path = TickFile.DayPath(_root, IbBarStore.BarFolder, "MNQ", DateOnly.FromDateTime(Start));
        byte[] original = File.ReadAllBytes(path);
        feed.RequestTimeout = TimeSpan.FromSeconds(2);
        var denied = feed.LoadBarHistoryAsync(instrument, Start, Start.AddMinutes(1), default);
        var next = await session.NextAsync("20");
        await session.SendAsync(false, "4", "2", next[1], "354", "Fixture historical entitlement denied");
        await Assert.ThrowsAsync<IbException>(() => denied);
        Assert.Equal(original, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task LiveDepthDenialPublishesFreshBboAndReconnectRestoresExactlyOneSubscriptionSet()
    {
        await using var gateway = new FakeIbGateway();
        using var feed = Feed(gateway);
        var instrument = IbBarImport.InstrumentFor("MNQ");
        var sink = new Sink();
        using var subscription = feed.Subscribe(instrument, sink);
        var first = await gateway.NextSessionAsync();
        var trade = await first.NextAsync("97");
        var quote = await first.NextAsync("97");
        var depth = await first.NextAsync("10");
        Assert.Equal("AllLast", trade[^3]); Assert.Equal("BidAsk", quote[^3]);
        await first.SendAsync(false, "99", quote[1], "3", "1793404740", "99", "100", "4", "5", "0");
        await first.SendAsync(false, "12", "1", depth[2], "0", "0", "1", "98", "50");
        await first.SendCoalescedAsync(
            new[] { "13", "1", depth[2], "0", "FIXTURE", "0", "0", "101", "9", "0" },
            new[] { "13", "1", depth[2], "0", "FIXTURE", "1", "0", "102", "7", "0" },
            new[] { "13", "1", depth[2], "0", "FIXTURE", "2", "0", "102", "0", "0" });
        await UntilAsync(() => sink.Depth.Count >= 6);
        Assert.Empty(sink.Depth.Last().Snapshot.Asks);
        Assert.Equal(instrument.ToTicks(98), Assert.Single(sink.Depth.Last().Snapshot.Bids).PriceTicks);
        await first.SendCoalescedAsync(
            new[] { "4", "2", depth[2], "317", "Fixture depth reset" },
            new[] { "12", "1", depth[2], "0", "0", "1", "97", "25" });
        await UntilAsync(() => sink.Depth.Count >= 8);
        Assert.Equal(instrument.ToTicks(97), Assert.Single(sink.Depth.Last().Snapshot.Bids).PriceTicks);
        await first.SendAsync(false, "4", "2", depth[2], "2152", "Fixture depth denied");
        await UntilAsync(() => sink.Messages.Any(m => m.Contains("fresh best bid/ask")));
        var fallback = sink.Depth.Last();
        Assert.True(fallback.IsFullRefresh);
        Assert.Equal(instrument.ToTicks(99), Assert.Single(fallback.Snapshot.Bids).PriceTicks);
        Assert.Equal(4, fallback.Snapshot.BestBidSize);
        Assert.Equal(instrument.ToTicks(100), Assert.Single(fallback.Snapshot.Asks).PriceTicks);
        Assert.Equal(5, fallback.Snapshot.BestAskSize);
        int depthCount = sink.Depth.Count;
        await first.SendAsync(false, "99", quote[1], "3", "1793404740", "0", "100", "0", "5", "0");
        await UntilAsync(() => sink.Depth.Count > depthCount);
        Assert.Empty(sink.Depth.Last().Snapshot.Bids);
        await first.SendAsync(false, "99", trade[1], "2", "1793404740", "100", "2", "0", "CME", "");
        await UntilAsync(() => sink.Trades.Count == 1);
        Assert.Equal(TradeSide.Buy, sink.Trades.Single().Side);
        first.Close();
        var second = await gateway.NextSessionAsync();
        var retrade = await second.NextAsync("97");
        var requote = await second.NextAsync("97");
        await second.NextAsync("10");
        Assert.NotEqual(trade[1], retrade[1]);
        Assert.NotEqual(quote[1], requote[1]);
        await second.SendAsync(false, "99", retrade[1], "2", "1793404800", "99", "1", "0", "CME", "");
        await UntilAsync(() => sink.Trades.Count == 2);
        Assert.Equal(TradeSide.None, sink.Trades.Last().Side);
        subscription.Dispose();
        Assert.Equal(retrade[1], (await second.NextAsync("98"))[1]);
        var barrier = feed.BarsAsync(IbFeed.ContractOn(instrument, Start), Start, "60 S", "1 min", default);
        var barrierRequest = await second.NextAsync("20");
        Assert.Equal(2, second.Received.Count(request => request[0] == "97"));
        Assert.Single(second.Received, request => request[0] == "10");
        await second.SendCoalescedAsync(
            new[] { "99", retrade[1], "2", "1793404801", "999", "1", "0", "CME", "" },
            new[] { "17", barrierRequest[1], "from", "to", "0" });
        Assert.Empty(await barrier);
        Assert.Equal(2, sink.Trades.Count);
    }
    [Fact]
    public async Task OfflineFeedUsesStoredBarsWithoutGatewayAndSessionsAreDstAware()
    {
        var instrument = IbBarImport.InstrumentFor("MNQ");
        IbBarStore.MergeBars(_root, instrument, new[] { new Kline(new DateTimeOffset(Start).ToUnixTimeMilliseconds(), 100, 101, 99, 100, 12, 0, false) }, default);
        using var feed = new IbFeed(new("unreachable.invalid", 1, 17, true), _root);
        var rows = await feed.LoadBarHistoryAsync(instrument, Start, Start.AddMinutes(1), default);
        Assert.Equal(4, rows.Count); Assert.All(rows, t => Assert.True(t.Id < 0));
        Assert.Equal(new DateTime(2026, 7, 1, 22, 0, 0, DateTimeKind.Utc), IbFeed.Session.SessionStartUtc(new(2026, 7, 1)));
        Assert.Equal(new DateTime(2026, 12, 1, 23, 0, 0, DateTimeKind.Utc), IbFeed.Session.SessionStartUtc(new(2026, 12, 1)));
        Assert.Equal((2026, 12), FuturesRolls.ContractMonth(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc)));
        var sink = new Sink(); using var subscription = feed.Subscribe(instrument, sink);
        await UntilAsync(() => sink.Messages.Any(m => m.Contains("offline")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => feed.ClientAsync(default));
    }
    [Fact]
    public async Task IndependentProfileConnectionsKeepRoutesAndDisconnectionsIsolated()
    {
        await using var gateway = new FakeIbGateway();
        using var first = Feed(gateway, "profile-one");
        using var second = new IbFeed(new("127.0.0.1", gateway.Port, 18, false, "profile-two"), _root);
        var instrument = IbBarImport.InstrumentFor("MES");
        var one = first.BarsAsync(IbFeed.ContractOn(instrument, Start), Start, "60 S", "1 min", default);
        var sessionOne = await gateway.NextSessionAsync();
        var two = second.BarsAsync(IbFeed.ContractOn(instrument, Start), Start, "60 S", "1 min", default);
        var sessionTwo = await gateway.NextSessionAsync();
        var requestOne = await sessionOne.NextAsync("20");
        var requestTwo = await sessionTwo.NextAsync("20");
        Assert.NotEqual(first.DataScopeKey, second.DataScopeKey);
        Assert.Equal(17, sessionOne.ClientId); Assert.Equal(18, sessionTwo.ClientId);
        sessionOne.Close();
        await Assert.ThrowsAsync<IOException>(() => one);
        await sessionTwo.SendAsync(true, "17", requestTwo[1], "from", "to", "1", "1793404740", "100", "101", "99", "100", "10", "100", "2");
        Assert.Equal(10, Assert.Single(await two).Volume);
        Assert.NotEqual(requestOne[1], requestTwo[1]);
    }
    [Fact]
    public async Task BatchConsumerCanStopBeforeNextChunkAndContractFieldsUseCalendarMonth()
    {
        await using var gateway = new FakeIbGateway();
        using var feed = Feed(gateway);
        var instrument = IbBarImport.InstrumentFor("MNQ");
        int batches = 0;
        var loading = feed.LoadKlinesAsync(instrument, Start, Start.AddDays(3), 1, 0, bars => { batches++; return false; }, null, default);
        var session = await gateway.NextSessionAsync();
        var request = await session.NextAsync("20");
        Assert.Equal("MNQ", request[3]); Assert.Equal("FUT", request[4]); Assert.Equal("202612", request[5]);
        Assert.Equal("USD", request[11]); Assert.Equal("0", request[18]); Assert.Equal("TRADES", request[19]); Assert.Equal("2", request[20]);
        await session.SendAsync(true, "17", request[1], "from", "to", "1", new DateTimeOffset(Start).ToUnixTimeSeconds().ToString(), "100", "101", "99", "100", "10", "100", "2");
        await loading;
        Assert.Equal(1, batches);
        await session.NextAsync("25");
        Assert.False(session.Requests.Reader.TryRead(out var unexpected) && unexpected[0] == "20");
    }
    [Fact]
    public async Task DepthAndQuoteFallbackCannotHideATradeEntitlementDenial()
    {
        await using var gateway = new FakeIbGateway();
        using var feed = Feed(gateway);
        var instrument = IbBarImport.InstrumentFor("MNQ");
        var sink = new Sink();
        using var subscription = feed.Subscribe(instrument, sink);
        var session = await gateway.NextSessionAsync();
        var trades = await session.NextAsync("97");
        var quotes = await session.NextAsync("97");
        var depth = await session.NextAsync("10");
        await session.SendCoalescedAsync(
            new[] { "4", "2", trades[1], "354", "Fixture trade entitlement denied" },
            new[] { "4", "2", depth[2], "2152", "Fixture depth entitlement denied" },
            new[] { "4", "2", quotes[1], "354", "Fixture quotes entitlement denied" });
        await UntilAsync(() => sink.Messages.Any(m => m.Contains("tick rule")));
        Assert.Equal(FeedStatus.Error, sink.Statuses.Last());
        Assert.Contains("Fixture trade entitlement denied", sink.Messages.Last());
    }
    [Fact]
    public async Task RetainedIbHubRestartsModeRecorderAndImportedHistoryWithoutChangingProfileIdentity()
    {
        await using var gateway = new FakeIbGateway();
        var instrument = IbBarImport.InstrumentFor("MNQ");
        var day = DateOnly.FromDateTime(Start);
        HistoricalData.WriteDay(_root, instrument, day, new[] { new Trade(1000, Start.AddSeconds(20), 100, 3, TradeSide.Buy) }, true);
        string recorded = TickFile.DayPath(_root, "ib", "MNQ", day);
        byte[] original = File.ReadAllBytes(recorded);
        IbBarStore.MergeBars(_root, instrument, new[] { new Kline(new DateTimeOffset(Start.AddMinutes(1)).ToUnixTimeMilliseconds(), 100, 101, 99, 100, 12, 0, false) }, default);
        using var feed = new IbFeed(new("127.0.0.1", gateway.Port, 17, true, "retained"), _root) { TickRequestInterval = TimeSpan.Zero };
        using var hub = new MarketDataHub { RecordingRoot = _root, RecordLive = true };
        var data = hub.GetOrStart(feed, instrument, TimeSpan.FromHours(1.5));
        await UntilAsync(() => { hub.PumpAll(); return !data.IsLoadingHistory; });
        Assert.Contains(Enumerable.Range(0, data.Trades.Count), index => data.Trades.GetId(index) == 1000);
        string scope = feed.DataScopeKey;
        feed.Reconfigure(new("127.0.0.1", gateway.Port, 22, false, "retained"));
        hub.PumpAll();
        var session = await gateway.NextSessionAsync();
        await session.NextAsync("97");
        await session.NextAsync("97");
        await session.NextAsync("10");
        Assert.Equal(22, session.ClientId);
        Assert.Equal(2, session.Received.Count(request => request[0] == "97"));
        Assert.Same(data, hub.GetOrStart(feed, instrument, TimeSpan.FromHours(1.5)));
        Assert.Null(TickFile.TryLock(_root, "ib", "MNQ"));
        var pending = feed.LoadBarHistoryAsync(instrument, Start, Start.AddMinutes(2), default);
        await session.NextAsync("20");
        feed.Reconfigure(new("127.0.0.1", gateway.Port, 22, true, "retained"));
        hub.PumpAll();
        await Assert.ThrowsAsync<IOException>(() => pending);
        await UntilAsync(() => { hub.PumpAll(); return !data.IsLoadingHistory; });
        await UntilAsync(() => { using var released = TickFile.TryLock(_root, "ib", "MNQ"); return released != null; });
        using (var lease = TickFile.TryLock(_root, "ib", "MNQ")) Assert.NotNull(lease);
        Assert.Equal(scope, feed.DataScopeKey);
        Assert.Equal(original, File.ReadAllBytes(recorded));

        string folder = Path.Combine(_root, "csv"); Directory.CreateDirectory(folder);
        long unix = new DateTimeOffset(Start.AddMinutes(2)).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(folder, "MNQ.csv"), $"time,open,high,low,close,volume\n{unix},100,101,99,100,8\n");
        var imported = IbBarImport.Commit(IbBarImport.PreviewFolder(folder, _root), _root);
        Assert.Equal(1, imported.FilesWritten);
        hub.PumpAll();
        await UntilAsync(() => { hub.PumpAll(); return !data.IsLoadingHistory && data.Trades.LastTime >= Start.AddMinutes(2); });
        Assert.Contains(Enumerable.Range(0, data.Trades.Count), index => data.Trades.GetId(index) == 1000);
        Assert.Equal(original, File.ReadAllBytes(recorded));
    }
    private sealed class Sink : IMarketDataSink
    {
        public ConcurrentQueue<Trade> Trades { get; } = new();
        public ConcurrentQueue<DepthUpdate> Depth { get; } = new();
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentQueue<FeedStatus> Statuses { get; } = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { foreach (var trade in trades) Trades.Enqueue(trade); }
        public void OnDepth(Instrument instrument, DepthUpdate update) => Depth.Enqueue(update);
        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            Statuses.Enqueue(status);
            Messages.Enqueue(message ?? status.ToString());
        }
    }
}
