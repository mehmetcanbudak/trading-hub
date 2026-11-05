using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    private AppNetworkProxy? _networkProxy;
    private InstrumentCatalog? _instrumentCatalog;
    private KlineKeeper? _klineKeeper;
    private StoragePreferences? _storagePreferences;
    public AppNetworkProxy NetworkProxy => _networkProxy ??= new(Settings, Keychain.Store);
    public InstrumentCatalog InstrumentCatalog => _instrumentCatalog ??= new(Path.Combine(AppPaths.MarketRoot, "InstrumentCatalog"));
    public StoragePreferences StoragePreferences => _storagePreferences ??= new(StorageLocations.CreateStore());
    public KlineKeeper KlineKeeper => _klineKeeper ??= new(Settings.CandleMaintenance ?? new(), UpdateMaintainedCandlesAsync,
        options => { Settings.CandleMaintenance = options; Settings.Save(); });

    internal void InitializeOperations()
    {
        ApplyOperationsHistorySettings();
        KlineKeeper.Start();
    }
    public void ApplyOperationsHistorySettings()
    {
        Hub.TickWindow = TimeSpan.FromHours(Math.Clamp(Settings.RawHistoryHours, 1, 2160));
        Hub.MaxDiskTrades = Math.Clamp(Settings.MaxDiskTrades, 10_000, 20_000_000);
        Hub.CompactRoot = Path.Combine(AppPaths.MarketRoot, "Compact");
        Hub.CompactMinutes = Settings.CompactHistoryMinutes is 1 or 5 or 15 or 60 ? Settings.CompactHistoryMinutes : 5;
        Hub.UseCompactHistory = Settings.UseCompactHistory;
        Hub.MaxCompactTrades = Math.Clamp(Settings.MaxCompactTrades, 10_000, 2_000_000);
        Hub.DepthRecording = new DepthRecordingOptions
        {
            Enabled = Settings.RecordDepth,
            RetentionDays = Math.Clamp(Settings.DepthRetentionDays, 1, 365),
            MaxDiskBytes = Math.Clamp(Settings.DepthMaxDiskMegabytes, 1, 4096) * 1024L * 1024,
        };
        foreach (var data in Hub.Items)
        {
            data.TickWindow = Hub.TickWindow;
            data.RawTradeLimit = Hub.MaxDiskTrades;
            data.CompactRoot = Hub.CompactRoot;
            data.CompactMinutes = Hub.CompactMinutes;
            data.UseCompactHistory = Hub.UseCompactHistory;
            data.CompactTradeLimit = Hub.MaxCompactTrades;
            data.ConfigureDepthRecording(Hub.DepthRecording);
        }
    }
    internal async Task ShutdownOperationsAsync()
    {
        if (_klineKeeper != null) await _klineKeeper.DisposeAsync().ConfigureAwait(false);
    }
    public void TrackOpenedInstrument(string profileId, Instrument instrument)
    {
        var profile = Connections.Find(profileId);
        if (profile != null) KlineKeeper.Track(profileId, global::TradingHub.App.Services.InstrumentCatalog.IdentityFor(profile), instrument);
    }
    private async Task<int> UpdateMaintainedCandlesAsync(TrackedCandleInstrument item, DateTime from, DateTime to, int minutes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var profile = Connections.Find(item.ProfileId);
        if (profile is not { Enabled: true }) throw new InvalidOperationException("The selected connection is disabled or no longer exists.");
        if (global::TradingHub.App.Services.InstrumentCatalog.IdentityFor(profile) != item.ProviderIdentity)
            throw new InvalidOperationException("Provider settings changed. Open the instrument explicitly to refresh its maintenance identity.");
        var feed = Connections.GetFeed(profile.Id);
        if (feed is not IKlineHistory source) throw new NotSupportedException("This provider does not offer candle history.");
        string? maintenanceWarning = null;
        var candles = await KlineHistory.LoadAsync(source, item.Instrument, from, to, minutes, 0, null, ct,
            warning: message => maintenanceWarning ??= message).ConfigureAwait(false);
        if (maintenanceWarning != null) throw new IOException(maintenanceWarning);
        if (candles.Any(k => (k.Provenance.Quality & DataQuality.Partial) != 0))
            throw new InvalidDataException("Provider candle coverage is partial; available cached data was retained.");
        return candles.Count;
    }
}

/// <summary>The same settings workflow is usable with isolated roots and fault-injected copy adapters.</summary>
public sealed class StoragePreferences(StorageLocationStore store)
{
    private static StorageRelocationResult? _startupResult;
    public ResolvedStorageLocations Current => store.Resolve();
    public StorageLocationSelection Selection => store.Selection;
    public StorageRelocationResult? LastResult => _startupResult ?? store.LastResult;
    public bool HasPending => store.HasPending;
    public Task<StorageRelocationPreview> PreviewAsync(StorageLocationSelection selection, CancellationToken ct,
        IProgress<StorageRelocationProgress>? progress = null) => Task.Run(() => store.PreviewAsync(selection, ct, progress), ct);
    public Task StageAsync(StorageRelocationPreview preview, CancellationToken ct) => store.StageAsync(preview, ct);
    public void CancelPending() => store.CancelPending();

    /// <summary>Program calls before AppPaths/Startup are observed and before any app writer is constructed.</summary>
    public static StorageRelocationResult ApplyPendingRelocation(CancellationToken ct = default)
    {
        try
        {
            var current = StorageLocations.CreateStore();
            if (!current.HasPending) return new(true, false, "No storage relocation is pending.");
            using var lease = SingleInstance.TryAcquireDataLease(current.Resolve().AppRoot);
            if (lease == null) return _startupResult = new(false, false,
                "Storage relocation deferred: another instance may still use the original data folder. Original locations remain active.");
            return _startupResult = current.ApplyPendingAsync(ct).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return _startupResult = new(false, false, "Storage relocation could not start. Original locations remain active. " + ex.Message);
        }
    }
}
