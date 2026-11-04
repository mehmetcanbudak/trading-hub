using System.Collections.Concurrent;
using System.Diagnostics;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class IbConsumerCorrectionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ib-consumers-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldLiveRoot = LiveStore.Root;
    private static readonly DateTime Start = new(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc);
    private readonly Instrument _instrument = IbBarImport.InstrumentFor("MNQ");
    public IbConsumerCorrectionsTests() { Directory.CreateDirectory(_root); LiveStore.Root = Path.Combine(_root, "live"); }
    public void Dispose() { LiveStore.Root = _oldLiveRoot; Directory.Delete(_root, true); }
    private BacktestSettings Settings() => new() { FeedId = "ib", Symbol = "MNQ", Period = "M1", FromUtc = Start, ToUtc = Start.AddMinutes(4), InitialCapital = 1000, TimeZoneId = "America/New_York", SessionStart = TimeSpan.FromHours(18) };
    private List<Trade> ImportMixed()
    {
        HistoricalData.WriteDay(_root, _instrument, DateOnly.FromDateTime(Start), new[] { new Trade(1000, Start.AddSeconds(20), 100, 3, TradeSide.Buy) }, true);
        string folder = Path.Combine(_root, "csv"); Directory.CreateDirectory(folder);
        long unix = new DateTimeOffset(Start).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(folder, "MNQ.csv"), $"time,open,high,low,close,volume\n{unix},100,101,99,100,12\n{unix + 60},100,101,99,100,8\n{unix + 120},100,101,99,100,4\n");
        IbBarImport.Commit(IbBarImport.PreviewFolder(folder, _root), _root);
        return HistoricalData.Read(_root, "ib", "MNQ", Start, Start.AddMinutes(4)).ToList();
    }
    private sealed class Probe : Strategy
    {
        public List<Bar> Seen { get; } = new();
        public List<Trade> CapturedTicks { get; } = new();
        protected override void OnInit() => UseFootprint = true;
        protected override void OnBar(Bar bar) => Seen.Add(bar);
        protected override void OnTick(Trade trade) => CapturedTicks.Add(trade);
    }
    private void AssertMixed(TickData ticks)
    {
        var strategy = new Probe();
        var result = new BacktestEngine(strategy, ticks, Settings()).Run();
        Assert.Null(result.Error);
        Assert.Equal(3, strategy.Seen.Count);
        Assert.True(strategy.Seen[0].HasRealTrades);
        Assert.True(strategy.Seen[0].HasObservedPoc);
        Assert.Equal(3m, strategy.Seen[0].Levels[100].Buy);
        foreach (var bar in strategy.Seen.Skip(1))
        {
            Assert.True(bar.IsSynthetic); Assert.True(bar.HasSyntheticTrades);
            Assert.Empty(bar.Levels); Assert.False(bar.HasObservedPoc); Assert.Equal(0m, bar.PocPrice);
        }
        Assert.Equal(8m, strategy.Seen[1].Volume); Assert.Equal(4m, strategy.Seen[2].Volume);
        Assert.Equal(8, strategy.CapturedTicks.Count(t => t.Id < 0));
        Assert.All(strategy.CapturedTicks, tick => Assert.True(tick.Side is TradeSide.None or TradeSide.Buy or TradeSide.Sell));
    }
    [Fact]
    public void ImportedMixedHistoryRetainsProvenanceThroughAllTickDataViewsAndActualStrategy()
    {
        var rows = ImportMixed();
        var store = new TradeStore(_instrument); foreach (var row in rows) store.Add(row);
        foreach (var ticks in new[] {
            TickData.Load(_root, _instrument, Start, Start.AddMinutes(4), null, default),
            TickData.Load(_root, _instrument, Start, Start.AddMinutes(4), null, default, compress: true),
            TickData.FromTrades(_instrument, rows), TickData.FromStore(store),
            TickData.FromTrades(_instrument, rows).Compress().Slice(Start, Start.AddMinutes(4)) }) AssertMixed(ticks);
        Assert.Equal(rows.Count * 25L, TickData.FromTrades(_instrument, rows).MemoryBytes);
    }
    [Fact]
    public void CompressionNeverMergesObservedAndSyntheticTicksAtTheSamePriceSideAndSecond()
    {
        var ticks = TickData.FromTrades(_instrument, new[] { new Trade(1, Start, 100, 2, TradeSide.Buy), new Trade(-1, Start, 100, 5, TradeSide.Buy) }).Compress();
        Assert.Equal(2, ticks.Count); Assert.False(ticks.IsSynthetic(0)); Assert.True(ticks.IsSynthetic(1));
        var strategy = new Probe(); var result = new BacktestEngine(strategy, ticks, Settings()).Run(); Assert.Null(result.Error);
        var bar = Assert.Single(strategy.Seen);
        Assert.True(bar.HasRealTrades && bar.HasSyntheticTrades); Assert.Equal(7m, bar.Volume);
        Assert.Equal(2m, bar.Levels[100].Buy); Assert.True(bar.HasObservedPoc);
        Assert.Equal(TradeSide.Buy, ticks.Side(1)); Assert.True(ticks.Get(1).Id < 0);
    }
    [Fact]
    public void ActualLiveWarmupProcessesSyntheticRowsAfterEarlierPositiveRealId()
    {
        var rows = ImportMixed();
        using var data = new InstrumentData(_instrument, new SimulatedFeed());
        foreach (var row in rows) data.Trades.Add(row);
        var strategy = new Probe();
        using var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());
        using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
        runner.Start();
        Assert.Equal(2, runner.WarmupBars);
        Assert.Equal(2, strategy.Seen.Count);
        Assert.True(strategy.Seen[1].IsSynthetic); Assert.Empty(strategy.Seen[1].Levels); Assert.Equal(0m, strategy.Seen[1].PocPrice);
        Assert.True(strategy.FormingBar!.IsSynthetic); Assert.Empty(strategy.FormingBar.Levels);
        Assert.Equal(4m, strategy.FormingBar.Volume);
        int index = data.Trades.Count;
        data.Trades.Add(new Trade(-999, Start.AddMinutes(3), 100, 1, TradeSide.Buy));
        var appended = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((Action<int, int>?)appended.GetValue(data))?.Invoke(index, 1);
        Assert.True(Assert.Single(strategy.CapturedTicks).Id < 0);
        Assert.True(strategy.FormingBar!.IsSynthetic);
        Assert.Empty(strategy.FormingBar.Levels);
    }
    [Fact]
    public async Task ActualReplayRetainsSyntheticSignsIntoTickEventsAndStrategyBars()
    {
        ImportMixed();
        using var replay = new ReplayFeed(_root, _instrument, Start, Start.AddMinutes(4)) { Speed = 1_000_000 };
        var sink = new Sink(); using var subscription = replay.Subscribe(replay.Instrument, sink);
        await Until(() => replay.IsFinished);
        AssertMixed(TickData.FromTrades(_instrument, sink.Trades.ToArray()));
    }
    [Fact]
    public void ActualWriterUnflushedHeaderAndCommittedRowsAreReadableButImportLeaseDoesNotAuthorizeTruncation()
    {
        var day = DateOnly.FromDateTime(Start); string path = TickFile.DayPath(_root, "ib", "MNQ", day);
        byte[] full;
        using (var owner = TickFile.TryLock(_root, "ib", "MNQ", recordingWriter: true))
        {
            Assert.NotNull(owner);
            using (var writer = new TickFile.Writer(path, _instrument))
            {
                Assert.Empty(HistorySources.ReadDay(_root, "ib", "MNQ", day));
                Assert.Equal(0, TickData.Load(_root, _instrument, Start, Start.AddMinutes(1), null, default).Count);
                writer.Write(new Trade(1000, Start, 100, 3, TradeSide.Buy)); writer.Flush();
                Assert.Single(HistorySources.ReadDay(_root, "ib", "MNQ", day));
                writer.Write(new Trade(1001, Start.AddSeconds(1), 100, 4, TradeSide.Sell)); writer.Flush();
            }
            full = File.ReadAllBytes(path); File.WriteAllBytes(path, full[..^1]);
            Assert.Single(HistorySources.ReadDay(_root, "ib", "MNQ", day));
            Assert.Equal(1, TickData.Load(_root, _instrument, Start, Start.AddMinutes(1), null, default).Count);
        }
        Assert.Throws<InvalidDataException>(() => HistorySources.ReadDay(_root, "ib", "MNQ", day));
        using (var importLease = TickFile.TryLock(_root, "ib", "MNQ"))
        {
            Assert.NotNull(importLease);
            Assert.Throws<InvalidDataException>(() => HistorySources.ReadDay(_root, "ib", "MNQ", day));
        }
        File.WriteAllBytes(path, full);
        Assert.Equal(2, HistorySources.ReadDay(_root, "ib", "MNQ", day).Count);
    }
    [Fact]
    public async Task RetainedHubHistoryReadsGenuineWriterPrefixesAndPublishesStrictCorruptionAfterWriterRelease()
    {
        var time = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(-2);
        string path = TickFile.DayPath(_root, "ib", "MNQ", DateOnly.FromDateTime(time));
        var settings = new IbSettings("127.0.0.1", 1, 300, Offline: true, ProfileId: "reader-lifecycle");
        using var feed = new IbFeed(settings, _root);
        using var hub = new MarketDataHub { RecordingRoot = _root, RecordLive = false };
        InstrumentData data;
        DateTime retainedFrom;
        async Task Refresh()
        {
            settings = settings with { ClientId = settings.ClientId + 1 };
            feed.Reconfigure(settings);
            await Until(() => { hub.PumpAll(); return !data.IsLoadingHistory; });
            Assert.Same(data, hub.GetOrStart(feed, _instrument, TimeSpan.FromMinutes(5)));
            Assert.Equal(retainedFrom, data.HistoryFrom);
        }
        byte[] full;
        using (var owner = TickFile.TryLock(_root, "ib", "MNQ", recordingWriter: true))
        {
            Assert.NotNull(owner);
            using var writer = new TickFile.Writer(path, _instrument);
            Assert.Equal(0, new FileInfo(path).Length); // Genuine BufferedStream header not flushed yet.
            data = hub.GetOrStart(feed, _instrument, TimeSpan.FromMinutes(5));
            retainedFrom = data.HistoryFrom;
            await Until(() => { hub.PumpAll(); return !data.IsLoadingHistory; });
            Assert.Null(data.HistoryError); Assert.Equal(0, data.Trades.Count);
            writer.Write(new Trade(1000, time, 100, 3, TradeSide.Buy)); writer.Flush();
            await Refresh();
            Assert.Null(data.HistoryError); Assert.Equal(1, data.Trades.Count); Assert.Equal(1000, data.Trades.GetId(0));
            writer.Write(new Trade(1001, time.AddSeconds(1), 100, 4, TradeSide.Sell)); writer.Flush();
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var snapshot = new MemoryStream()) { input.CopyTo(snapshot); full = snapshot.ToArray(); }
            // Model an OS-visible partial append through the actual owner's stream, not a second writer.
            var stream = (FileStream)typeof(TickFile.Writer).GetField("_fs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(writer)!;
            stream.SetLength(full.Length - 1); stream.Flush();
            await Refresh();
            Assert.Null(data.HistoryError); Assert.Equal(1, data.Trades.Count); Assert.Equal(1000, data.Trades.GetId(0));
        } // Writer closes before marker and primary ownership are released.
        await Refresh();
        Assert.False(string.IsNullOrWhiteSpace(data.HistoryError));
        using (var importing = TickFile.TryLock(_root, "ib", "MNQ"))
        {
            Assert.NotNull(importing);
            await Refresh();
            Assert.False(string.IsNullOrWhiteSpace(data.HistoryError)); // Import ownership cannot authorize leniency.
        }
        File.WriteAllBytes(path, full);
        await Refresh();
        Assert.Null(data.HistoryError); Assert.Equal(2, data.Trades.Count);
    }
    [IbUnixFact]
    public async Task CrossProcessWriterMarkerAllowsOnlyItsCommittedPrefixAndQuiescentTailStillFails()
    {
        string path = TickFile.DayPath(_root, "ib", "MNQ", DateOnly.FromDateTime(Start));
        using (var writer = new TickFile.Writer(path, _instrument))
        { writer.Write(new Trade(1, Start, 100, 3, TradeSide.Buy)); writer.Write(new Trade(2, Start.AddSeconds(1), 100, 4, TradeSide.Sell)); }
        byte[] full = File.ReadAllBytes(path);
        const string script = "import sys,os,fcntl,time,base64\np,body,ready,release=sys.argv[1:]\nd=os.path.dirname(p)\nl=open(os.path.join(d,'.grabando.lock'),'a+b');fcntl.flock(l,fcntl.LOCK_EX)\nm=open(os.path.join(d,'.recording-active.lock'),'a+b');fcntl.flock(m,fcntl.LOCK_EX)\nb=base64.b64decode(body)\nf=open(p,'wb');f.write(b[:-1]);f.flush()\nopen(ready,'w').close()\nend=time.monotonic()+20\nwhile not os.path.exists(release) and time.monotonic()<end: time.sleep(.01)\nf.write(b[-1:]);f.flush();f.close();m.close();l.close()\n";
        string ready = Path.Combine(_root, "ready"), release = Path.Combine(_root, "release");
        var start = new ProcessStartInfo("python3") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string arg in new[] { "-c", script, path, Convert.ToBase64String(full), ready, release }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        try
        {
            await Until(() => File.Exists(ready) || child.HasExited);
            Assert.False(child.HasExited, child.HasExited ? await child.StandardError.ReadToEndAsync() : "");
            Assert.Single(HistorySources.ReadDay(_root, "ib", "MNQ", DateOnly.FromDateTime(Start)));
            Assert.Equal(1, TickData.Load(_root, _instrument, Start, Start.AddMinutes(1), null, default).Count);
        }
        finally { File.WriteAllText(release, "release"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
        Assert.Equal(0, child.ExitCode);
        Assert.Equal(2, HistorySources.ReadDay(_root, "ib", "MNQ", DateOnly.FromDateTime(Start)).Count);
        File.WriteAllBytes(path, full[..^1]);
        Assert.Throws<InvalidDataException>(() => HistorySources.ReadDay(_root, "ib", "MNQ", DateOnly.FromDateTime(Start)));
    }
    [Fact]
    public async Task QuoteDenialClearsPublishedFallbackAndLateQuotesCannotRepopulateInstrumentDepth()
    {
        await QuoteDenialCase(depthDenied: true);
        await QuoteDenialCase(depthDenied: false);
    }
    private async Task QuoteDenialCase(bool depthDenied)
    {
        await using var gateway = new FakeIbGateway();
        using var feed = new IbFeed(new("127.0.0.1", gateway.Port, 41), _root) { TickRequestInterval = TimeSpan.Zero };
        using var data = new InstrumentData(_instrument, feed);
        data.Start(Start);
        var session = await gateway.NextSessionAsync();
        var trade = await session.NextAsync("97"); var quote = await session.NextAsync("97"); var depth = await session.NextAsync("10");
        await session.SendAsync(false, "99", quote[1], "3", "1793404740", "99", "100", "4", "5", "0");
        await Until(() => { data.Pump(); return data.Depth.HasBid; });
        if (depthDenied) await session.SendAsync(false, "4", "2", depth[2], "2152", "depth denied");
        else await session.SendAsync(false, "12", "1", depth[2], "0", "0", "1", "98", "50");
        await session.SendAsync(false, "4", "2", quote[1], "354", "quotes denied");
        await Until(() => { data.Pump(); return data.StatusMessage?.Contains("quotes unavailable") == true; });
        if (depthDenied) Assert.False(data.Depth.HasBid || data.Depth.HasAsk);
        else Assert.Equal(_instrument.ToTicks(98), data.Depth.BestBidTicks);
        var barrier = feed.BarsAsync(IbFeed.ContractOn(_instrument, Start), Start, "60 S", "1 min", default);
        string[] history;
        do { history = await session.NextAsync("20"); } while (history[17] != "60 S");
        await session.SendCoalescedAsync(new[] { "99", quote[1], "3", "1793404741", "500", "501", "5", "5", "0" }, new[] { "17", history[1], "from", "to", "0" });
        await barrier; data.Pump();
        if (depthDenied) Assert.False(data.Depth.HasBid || data.Depth.HasAsk);
        else Assert.Equal(_instrument.ToTicks(98), data.Depth.BestBidTicks);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, stop.Token);
    }
    private sealed class Sink : IMarketDataSink
    {
        public ConcurrentQueue<Trade> Trades { get; } = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { foreach (var trade in trades) Trades.Enqueue(trade); }
        public void OnDepth(Instrument instrument, DepthUpdate update) { }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }
}
internal sealed class IbUnixFactAttribute : FactAttribute
{
    public IbUnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "POSIX cross-process writer fixture; Windows shared codec tests remain active."; }
}
