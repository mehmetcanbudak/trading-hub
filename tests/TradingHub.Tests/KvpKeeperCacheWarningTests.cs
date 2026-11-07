using System.Runtime.CompilerServices;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

[Collection("KvpHistoryCache")]
public sealed class KvpKeeperCacheWarningTests
{
    [Fact]
    public async Task CachePersistenceFailureKeepsChartSourceValidButMaintenanceRetriesItsExactUncachedSlot()
    {
        using var fixture = new OperationsStorageFixture();
        string? previousRoot = KlineHistory.CacheRoot;
        string cacheRoot = fixture.PathFor("keeper-candle-cache");
        KlineHistory.CacheRoot = cacheRoot;
        var instrument = new Instrument { FeedId = "keeper-cache-warning-fixture", Symbol = "CACHE", TickSize = 1m };
        using var source = new CandleSource(instrument);
        var now = DateTime.UtcNow;
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = month.AddMonths(-1);
        try
        {
            await KlineHistory.LoadAsync(source, instrument, older, older.AddMinutes(5), 1, 0, null, CancellationToken.None);
            string cacheFile = Assert.Single(Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories));
            File.Delete(cacheFile);
            // Portable, fixture-owned publication failure: an existing directory cannot be replaced by a cache file.
            Directory.CreateDirectory(cacheFile);
            var chartWarnings = new List<string>();
            var chartBars = await KlineHistory.LoadAsync(source, instrument, older, older.AddMinutes(5), 1, 0, null,
                CancellationToken.None, warning: chartWarnings.Add);
            var chartBar = Assert.Single(chartBars);
            Assert.Equal(11m, chartBar.Close);
            Assert.Equal(3m, chartBar.Volume);
            Assert.False((chartBar.Provenance.Quality & DataQuality.Partial) != 0);
            Assert.Contains(cacheFile, Assert.Single(chartWarnings), StringComparison.Ordinal);

            var profile = new ConnectionProfile { Id = "keeper-cache-profile", Name = "Fixture candles", Kind = ConnectionKind.Simulator, Enabled = true };
            var tracked = new TrackedCandleInstrument(profile.Id, InstrumentCatalog.IdentityFor(profile), instrument);
            var settings = new AppSettings
            {
                CandleMaintenance = new KlineMaintenanceSettings { Enabled = true, Instruments = new() { tracked } },
            };
            // Reuse the established no-startup composition seam: no watcher, account, credential store or network starts.
            var connections = (ConnectionManager)RuntimeHelpers.GetUninitializedObject(typeof(ConnectionManager));
            SearchZoomFixture.Set(connections, "<Profiles>k__BackingField", new List<ConnectionProfile> { profile });
            SearchZoomFixture.Set(connections, "_feeds", new Dictionary<string, IMarketDataFeed> { [profile.Id] = source });
            var services = (AppServices)RuntimeHelpers.GetUninitializedObject(typeof(AppServices));
            SearchZoomFixture.Set(services, "<Connections>k__BackingField", connections);
            SearchZoomFixture.Set(services, "<Settings>k__BackingField", settings);
            SearchZoomFixture.Set(services, "<Work>k__BackingField", new OwnedOperationRegistry());
            await using var keeper = services.KlineKeeper;
            await keeper.RunNowAsync();
            var failure = Assert.Single(keeper.Status.Failures);
            Assert.Equal(tracked.Key, failure.InstrumentKey);
            Assert.Equal(older, failure.MonthUtc);
            Assert.Equal(1, failure.Minutes);
            Assert.Contains(cacheFile, failure.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(cacheFile));
            fixture.AssertOriginals();
            int requestsBeforeRepair = source.Requests.Count(request => request.From == older && request.Minutes == 1);

            Directory.Delete(cacheFile);
            await keeper.RunNowAsync();
            Assert.Empty(keeper.Status.Failures);
            Assert.Equal(requestsBeforeRepair + 1, source.Requests.Count(request => request.From == older && request.Minutes == 1));
            Assert.True(File.Exists(cacheFile));
            fixture.AssertOriginals();
        }
        finally { KlineHistory.CacheRoot = previousRoot; }
    }

    private sealed class CandleSource(Instrument instrument) : IMarketDataFeed, IKlineHistory
    {
        public List<(DateTime From, DateTime To, int Minutes)> Requests { get; } = new();
        public string Id => instrument.FeedId;
        public string DisplayName => "Fixture candle source";
        public DateTime Now => DateTime.UtcNow;
        public bool SupportsHistory => true;
        public bool IsLive => false;
        public string CacheTag => "keeper-persistence-fixture";
        public Task<DateTime?> FirstAvailableAsync(Instrument selected, CancellationToken ct) => Task.FromResult<DateTime?>(null);
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { instrument });
        public Task LoadKlinesAsync(Instrument selected, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add((fromUtc, toUtc, minutes));
            long step = TimeSpan.TicksPerMinute * minutes;
            long openTicks = fromUtc.Ticks - fromUtc.Ticks % step;
            if (openTicks < fromUtc.Ticks) openTicks += step;
            var rows = openTicks <= toUtc.Ticks - step
                ? new[] { new Kline(new DateTimeOffset(new DateTime(openTicks, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), 10, 12, 8, 11, 3, 2, true) }
                : Array.Empty<Kline>();
            onBatch(rows);
            return Task.CompletedTask;
        }
        public Task LoadTradesAsync(Instrument selected, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException("This fixture has no raw transport.");
        public IDisposable Subscribe(Instrument selected, IMarketDataSink sink) => throw new NotSupportedException("This fixture has no live transport.");
        public void Dispose() { }
    }
}
