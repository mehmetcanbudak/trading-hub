using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Alpaca;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.CTrader;
using TradingHub.Feeds.Oanda;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>
/// Hourly candle requests reach each provider as its native one-hour interval, unsupported resolutions are rejected
/// instead of substituted, and finer bars are never cached or served as hourly history.
/// </summary>
[Collection("KvpHistoryCache")]
public sealed class HourlyKlinesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-hourly-klines-" + Guid.NewGuid().ToString("N"));
    private readonly Instrument _instrument = new() { FeedId = "hourly-fixture", Symbol = "HOURLY", TickSize = 1m, VolumeStep = 1m };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Binance_requests_one_hour_klines_from_REST_and_the_monthly_archive()
    {
        var month = new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var restOpen = new DateTime(2025, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        using var content = new MemoryStream();
        using (var zip = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("BTCUSDT-1h-2025-09.csv").Open()))
            writer.Write($"{Ms(month)},100,110,90,105,10,{Ms(month.AddHours(1)) - 1},1000,3,6,600,0\n");
        byte[] archive = content.ToArray();
        using var handler = new FixtureHttp(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "data.binance.vision")
                return uri.AbsolutePath == "/data/futures/um/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2025-09.zip"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            Assert.Equal("/fapi/v1/klines", uri.AbsolutePath);
            return Json(uri.Query.Contains($"startTime={Ms(restOpen)}&", StringComparison.Ordinal)
                ? $"[[{Ms(restOpen)},\"100\",\"110\",\"90\",\"105\",\"10\",{Ms(restOpen.AddHours(1)) - 1},\"1000\",3,\"6\",\"600\",\"0\"]]"
                : "[]");
        });
        using var http = new HttpClient(handler);
        using var feed = new BinanceFeed(true, restOrigin: new Uri("https://binance.fixture"), httpClient: http);
        var instrument = new Instrument { FeedId = BinanceFeed.FuturesId, Symbol = "BTCUSDT", TickSize = 0.1m };

        var archived = new List<Kline>();
        await feed.LoadKlinesAsync(instrument, month, month.AddMonths(1), 60, 0, batch => { archived.AddRange(batch); return true; }, null, CancellationToken.None);
        Assert.Equal(Ms(month), Assert.Single(archived).OpenTimeMs);
        var rest = new List<Kline>();
        await feed.LoadKlinesAsync(instrument, restOpen, restOpen.AddHours(3), 60, 0, batch => { rest.AddRange(batch); return true; }, null, CancellationToken.None);
        Assert.Equal(Ms(restOpen), Assert.Single(rest).OpenTimeMs);

        var queries = handler.Requests.Where(uri => uri.Host == "binance.fixture").Select(uri => uri.Query).ToArray();
        Assert.NotEmpty(queries);
        Assert.All(queries, query => Assert.Contains("interval=1h&", query, StringComparison.Ordinal));
        int requests = handler.Requests.Count;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            feed.LoadKlinesAsync(instrument, restOpen, restOpen.AddHours(3), 7, 0, _ => true, null, CancellationToken.None));
        Assert.Equal(requests, handler.Requests.Count);
    }

    [Fact]
    public async Task Bybit_requests_its_sixty_minute_interval_and_rejects_unlisted_ones()
    {
        var open = new DateTime(2025, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        using var handler = new FixtureHttp(_ => Json($$$"""{"retCode":0,"result":{"category":"linear","symbol":"BTCUSDT","list":[["{{{Ms(open)}}}","100","110","90","105","10","1000"]]}}"""));
        using var http = new HttpClient(handler);
        using var feed = new BybitFeed(http, restOrigin: new Uri("https://bybit.fixture"));
        var instrument = new Instrument { FeedId = BybitFeed.LinearId, Symbol = "BTCUSDT", TickSize = 0.1m };
        var bars = new List<Kline>();
        await feed.LoadKlinesAsync(instrument, open, open.AddHours(3), 60, 0, batch => { bars.AddRange(batch); return true; }, null, CancellationToken.None);
        Assert.Equal(Ms(open), Assert.Single(bars).OpenTimeMs);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/v5/market/kline", request.AbsolutePath);
        Assert.Contains("interval=60&", request.Query, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            feed.LoadKlinesAsync(instrument, open, open.AddHours(3), 7, 0, _ => true, null, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Alpaca_requests_one_hour_bars_instead_of_fifteen_minute_bars()
    {
        using var server = FakeBrokers.Alpaca();
        using var feed = new AlpacaFeed(new AlpacaSettings { KeyId = "KEY", Secret = "SECRET", DataUrl = server.Http, TradingUrl = server.Http, StreamUrl = server.Ws + "/v2/iex" });
        var spy = new Instrument { Symbol = "SPY", FeedId = AlpacaFeed.FeedId, TickSize = 0.01m };
        var start = FakeBrokers.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await feed.LoadKlinesAsync(spy, start, start.AddDays(1), 60, 0, _ => true, null, CancellationToken.None);
        await feed.LoadKlinesAsync(spy, start, start.AddDays(1), 15, 0, _ => true, null, CancellationToken.None);
        FakeRequest[] bars;
        lock (server.Requests) bars = server.Requests.Where(request => request.Route == "/v2/stocks/SPY/bars").ToArray();
        Assert.Equal(new[] { "1Hour", "15Min" }, bars.Select(request => request.Query("timeframe")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            feed.LoadKlinesAsync(spy, start, start.AddDays(1), 90, 0, _ => true, null, CancellationToken.None));
        lock (server.Requests) Assert.Equal(2, server.Requests.Count(request => request.Route == "/v2/stocks/SPY/bars"));
    }

    [Fact]
    public async Task CTrader_requests_the_H1_trendbar_period_and_rejects_periods_it_does_not_define()
    {
        using var fake = new FakeCTrader();
        using var feed = new CTraderFeed(() => fake.Settings());
        var us100 = (await feed.GetInstrumentsAsync(CancellationToken.None))[0];
        var from = FakeBrokers.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await feed.LoadKlinesAsync(us100, from, from.AddDays(1), 60, 0, _ => true, null, CancellationToken.None);
        await feed.LoadKlinesAsync(us100, from, from.AddDays(1), 15, 0, _ => true, null, CancellationToken.None);
        Assert.Equal(new[] { 9, 7 }, fake.TrendbarPeriods.ToArray()); // H1, M15
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            feed.LoadKlinesAsync(us100, from, from.AddDays(1), 7, 0, _ => true, null, CancellationToken.None));
        Assert.Equal(2, fake.TrendbarPeriods.Count);
    }

    [Fact]
    public async Task Oanda_requests_H1_candles_and_declares_their_hourly_resolution()
    {
        using var server = FakeBrokers.Oanda();
        using var feed = new OandaFeed(new OandaSettings { Token = "TOKEN", RestUrl = server.Http, StreamUrl = server.Http });
        var nas = (await feed.GetInstrumentsAsync(CancellationToken.None)).Single(instrument => instrument.Symbol == "NAS100_USD");
        var day = FakeBrokers.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var bars = new List<Kline>();
        await feed.LoadKlinesAsync(nas, day, day.AddDays(1), 60, 0, batch => { bars.AddRange(batch); return true; }, null, CancellationToken.None);
        var bar = Assert.Single(bars);
        Assert.Equal(Ms(day.AddHours(13)), bar.OpenTimeMs);
        Assert.Equal(3600, bar.Provenance.ResolutionSeconds);
        FakeRequest[] candles;
        lock (server.Requests) candles = server.Requests.Where(request => request.Route.EndsWith("/candles", StringComparison.Ordinal)).ToArray();
        Assert.Equal("H1", Assert.Single(candles).Query("granularity"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            feed.LoadKlinesAsync(nas, day, day.AddDays(1), 7, 0, _ => true, null, CancellationToken.None));
    }

    [Fact]
    public async Task FinerBarsAnsweringAnHourlyRequestAreNeitherCachedNorServedAsHourly()
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = ClosedMonth();
            // An undeclared provider substituting minute bars, and an honest provider declaring quarter-hour bars.
            var minutes = new HourlySource((from, _) => Bars(from, from.AddHours(2), TimeSpan.FromMinutes(1)));
            var quarters = new HourlySource((from, _) => Bars(from, from.AddHours(2), TimeSpan.FromMinutes(15),
                new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, 900, "quarter-provider", ObservedTradeCount: 0)));
            foreach (var source in new[] { minutes, quarters })
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    KlineHistory.LoadAsync(source, _instrument, month, month.AddDays(1), 60, 0, null, CancellationToken.None));
                Assert.Contains("60-minute", error.Message, StringComparison.Ordinal);
                Assert.Empty(CacheFiles());
            }

            var hourly = new HourlySource((from, to) => Bars(from, to, TimeSpan.FromHours(1)));
            var loaded = await KlineHistory.LoadAsync(hourly, _instrument, month, month.AddDays(1), 60, 0, null, CancellationToken.None);
            Assert.Equal(24, loaded.Count);
            Assert.All(loaded, bar => Assert.Equal(3600, bar.Provenance.ResolutionSeconds));
            hourly.Unavailable = true;
            Assert.Equal(loaded, await KlineHistory.LoadAsync(hourly, _instrument, month, month.AddDays(1), 60, 0, null, CancellationToken.None));
            Assert.Single(hourly.Requests);
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Theory]
    [InlineData("hourly-control")]
    [InlineData("minute")]
    [InlineData("quarter-hour")]
    public async Task AnExistingHourlyCacheHoldingFinerBarsIsRefetchedInsteadOfServed(string stored)
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = ClosedMonth();
            var hourly = new HourlySource((from, to) => Bars(from, to, TimeSpan.FromHours(1)));
            await KlineHistory.LoadAsync(hourly, _instrument, month, month.AddDays(1), 60, 0, null, CancellationToken.None);
            string path = Assert.Single(CacheFiles());
            // Authenticated monthly caches exactly as earlier versions wrote them for these provider answers.
            var bars = stored switch
            {
                "hourly-control" => Enumerable.Range(0, 24).Select(i =>
                    (month.AddHours(i), new TradeProvenance(DataQuality.Synthetic, 3600, _instrument.Key, 0, 0, true, 0))),
                "minute" => Enumerable.Range(0, 180).Select(i =>
                    (month.AddMinutes(i), new TradeProvenance(DataQuality.Synthetic, 3600, _instrument.Key, 0, 0, true, i * 60 % 3600))),
                _ => Enumerable.Range(0, 12).Select(i =>
                    (month.AddMinutes(15 * i), new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, 900, "oanda:midpoint-candles:quote-count", 0, 0, true, 0))),
            };
            RewriteCache(path, Payload(bars.ToList(), volume: 42));
            hourly.Requests.Clear();

            var loaded = await KlineHistory.LoadAsync(hourly, _instrument, month, month.AddDays(1), 60, 0, null, CancellationToken.None);
            Assert.Equal(24, loaded.Count);
            Assert.All(loaded, bar =>
            {
                Assert.Equal(0, bar.OpenTimeMs % 3_600_000);
                Assert.Equal(3600, bar.Provenance.ResolutionSeconds);
            });
            if (stored == "hourly-control")
            {
                Assert.Empty(hourly.Requests);
                Assert.All(loaded, bar => Assert.Equal(42m, bar.Volume));
                return;
            }
            Assert.Single(hourly.Requests);
            Assert.All(loaded, bar => Assert.Equal(1m, bar.Volume));
            // The refetched month replaced the stale file and is now served from the cache.
            hourly.Unavailable = true;
            Assert.Equal(loaded, await KlineHistory.LoadAsync(hourly, _instrument, month, month.AddDays(1), 60, 0, null, CancellationToken.None));
            Assert.Single(hourly.Requests);
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public async Task ARefreshOfAClosedMonthKeepsCachedBarsTheProviderNoLongerServes()
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = ClosedMonth();
            var hourly = new HourlySource((from, to) => Bars(from, to, TimeSpan.FromHours(1)));
            await KlineHistory.LoadAsync(hourly, _instrument, month, month.AddDays(2), 60, 0, null, CancellationToken.None);
            string path = Assert.Single(CacheFiles());
            // A stale cache of the first two days (volume 42), as an earlier session stored them.
            var stored = Enumerable.Range(0, 48).Select(i =>
                (month.AddHours(i), new TradeProvenance(DataQuality.Synthetic, 3600, _instrument.Key, 0, 0, true, 0))).ToList();
            RewriteCache(path, Payload(stored, volume: 42), DateTime.UtcNow.AddDays(-3));
            // The provider has since aged out the first day: it serves only later bars.
            var aged = new HourlySource((from, to) => Bars(from < month.AddDays(1) ? month.AddDays(1) : from, to, TimeSpan.FromHours(1)));

            var loaded = await KlineHistory.LoadAsync(aged, _instrument, month, month.AddDays(2), 60, 0, null, CancellationToken.None);

            Assert.Single(aged.Requests);
            Assert.Equal(48, loaded.Count);
            Assert.All(loaded.Take(24), bar => Assert.Equal(42m, bar.Volume)); // kept from the cache
            Assert.All(loaded.Skip(24), bar => Assert.Equal(1m, bar.Volume));  // refreshed by the provider
            aged.Unavailable = true;
            Assert.Equal(loaded, await KlineHistory.LoadAsync(aged, _instrument, month, month.AddDays(2), 60, 0, null, CancellationToken.None));
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    [Fact]
    public async Task AMonthBeforeTheProvidersWindowIsNeitherFetchedNorCachedAsEmptyButAnOldCacheIsStillServed()
    {
        string? previousRoot = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = Path.Combine(_root, "candles");
        try
        {
            var month = ClosedMonth();
            var windowed = new WindowedSource(month.AddMonths(1), (from, to) => Bars(from, to, TimeSpan.FromHours(1)));
            var warnings = new List<string>();

            var none = await KlineHistory.LoadAsync(windowed, _instrument, month, month.AddDays(2), 60, 0, null, CancellationToken.None, warnings.Add);
            Assert.Empty(none);
            Assert.Empty(windowed.Requests);
            Assert.Empty(CacheFiles()); // an unavailable month is not remembered as a month without trading
            Assert.Single(warnings);
            Assert.Contains("not available from this provider", warnings[0], StringComparison.Ordinal);

            // An earlier session cached the month while the provider still served it: it stays usable after it aged out.
            var open = new HourlySource((from, to) => Bars(from, to, TimeSpan.FromHours(1)));
            await KlineHistory.LoadAsync(open, _instrument, month, month.AddDays(2), 60, 0, null, CancellationToken.None);
            string path = Assert.Single(CacheFiles());
            var stored = Enumerable.Range(0, 48).Select(i =>
                (month.AddHours(i), new TradeProvenance(DataQuality.Synthetic, 3600, _instrument.Key, 0, 0, true, 0))).ToList();
            RewriteCache(path, Payload(stored, volume: 42), DateTime.UtcNow.AddDays(-3));
            warnings.Clear();

            var kept = await KlineHistory.LoadAsync(windowed, _instrument, month, month.AddDays(2), 60, 0, null, CancellationToken.None, warnings.Add);
            Assert.Equal(48, kept.Count);
            Assert.All(kept, bar => Assert.Equal(42m, bar.Volume));
            Assert.Empty(windowed.Requests);
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    private static IEnumerable<string> CacheFiles() => Directory.Exists(KlineHistory.CacheRoot)
        ? Directory.EnumerateFiles(KlineHistory.CacheRoot, "*.bin", SearchOption.AllDirectories).ToArray()
        : Array.Empty<string>();

    private static DateTime ClosedMonth()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-2);
    }

    private static long Ms(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();

    private static List<Kline> Bars(DateTime from, DateTime to, TimeSpan step, TradeProvenance provenance = default)
    {
        var bars = new List<Kline>();
        for (var time = from; time < to; time += step) bars.Add(new Kline(Ms(time), 100, 101, 99, 100, 1, 0, false, provenance));
        return bars;
    }

    /// <summary>The monthly cache payload layout: sources, provenance metadata, then bars.</summary>
    private static byte[] Payload(IReadOnlyList<(DateTime Open, TradeProvenance Provenance)> bars, decimal volume)
    {
        var sources = bars.Select(bar => bar.Provenance.SourceIdentity!).Distinct(StringComparer.Ordinal).ToList();
        var metadata = bars.Select(bar => bar.Provenance).Distinct().ToList();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(sources.Count);
            foreach (var source in sources) writer.Write(source);
            writer.Write(metadata.Count);
            foreach (var provenance in metadata)
            {
                writer.Write((byte)provenance.Quality); writer.Write(provenance.ResolutionSeconds); writer.Write(sources.IndexOf(provenance.SourceIdentity!));
                writer.Write(provenance.CorrectionRevision); writer.Write(provenance.ObservedTradeCount);
                writer.Write(provenance.HasKnownAvailability); writer.Write(provenance.ResolutionOffsetSeconds);
            }
            writer.Write(bars.Count);
            foreach (var (open, provenance) in bars)
            {
                writer.Write(Ms(open)); writer.Write(100m); writer.Write(101m); writer.Write(99m); writer.Write(100m); writer.Write(volume); writer.Write(0m); writer.Write(false);
                writer.Write(metadata.IndexOf(provenance));
            }
        }
        return stream.ToArray();
    }

    /// <summary>Replaces a monthly cache payload, keeping its header and re-authenticating it as the cache writer does.</summary>
    private static void RewriteCache(string path, byte[] payload, DateTime? updatedAt = null)
    {
        int magic, minutes;
        decimal tick;
        long from, to, updated;
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            magic = reader.ReadInt32(); tick = reader.ReadDecimal(); minutes = reader.ReadInt32();
            from = reader.ReadInt64(); to = reader.ReadInt64(); updated = reader.ReadInt64();
        }
        if (updatedAt is { } stamp) updated = stamp.Ticks;
        var header = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(header, magic);
        int[] bits = decimal.GetBits(tick);
        for (int part = 0; part < bits.Length; part++) BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4 + part * 4), bits[part]);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), minutes);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), from);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), to);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), updated);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(payload);
        byte[] digest = hash.GetHashAndReset();
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(magic); writer.Write(tick); writer.Write(minutes); writer.Write(from); writer.Write(to); writer.Write(updated);
        writer.Write(digest); writer.Write(payload);
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

    private sealed class WindowedSource(DateTime earliest, Func<DateTime, DateTime, IReadOnlyList<Kline>> create) : IKlineHistory, IKlineWindow
    {
        public List<(DateTime From, DateTime To)> Requests { get; } = new();
        public string CacheTag => "hourly-fixture";
        public DateTime? EarliestKline(Instrument instrument, int minutes) => earliest;
        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);
        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            Requests.Add((fromUtc, toUtc));
            onBatch(create(fromUtc, toUtc));
            return Task.CompletedTask;
        }
    }

    private sealed class HourlySource(Func<DateTime, DateTime, IReadOnlyList<Kline>> create) : IKlineHistory
    {
        public List<(DateTime From, DateTime To)> Requests { get; } = new();
        public bool Unavailable { get; set; }
        public string CacheTag => "hourly-fixture";
        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);
        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(60, minutes);
            Requests.Add((fromUtc, toUtc));
            if (Unavailable) throw new IOException("Owned fixture candle source is unavailable.");
            onBatch(create(fromUtc, toUtc));
            return Task.CompletedTask;
        }
    }
}
