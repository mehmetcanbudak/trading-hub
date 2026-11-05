using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Services;

public sealed record InstrumentCatalogSnapshot(string ProfileId, string ProviderIdentity, string ProviderLabel,
    DateTime CapturedUtc, IReadOnlyList<Instrument> Instruments, bool IsOffline, bool IsStale, string? Warning)
{
    public string StatusText => IsOffline
        ? $"Offline catalog · {ProviderLabel} · saved {CapturedUtc:yyyy-MM-dd HH:mm} UTC · stale metadata; verify contract details before trading."
        : Warning ?? "";
}

/// <summary>
/// Persisted, provider/profile-scoped discovery only. This module has no account-selection or order-routing
/// dependency. Offline rows require the same explicit instrument selection as online search.
/// Inspired by KVP's instrument catalog; metadata/age and cancellation semantics are redesigned.
/// </summary>
public sealed class InstrumentCatalog
{
    private const int MaxRows = 100_000;
    private const long MaxFileBytes = 32L * 1024 * 1024;
    private readonly string _root;
    private readonly Func<DateTime> _utcNow;
    private sealed record SavedCatalog(int Version, string ProfileId, string ProviderIdentity, string ProviderLabel,
        DateTime CapturedUtc, List<Instrument> Instruments);

    public InstrumentCatalog(string root, Func<DateTime>? utcNow = null)
    {
        _root = root;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public static string IdentityFor(ConnectionProfile profile) => profile.Kind + ":" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile))));

    public async Task<InstrumentCatalogSnapshot> LoadAsync(string profileId, string providerIdentity, IMarketDataFeed feed,
        CancellationToken ct, bool allowNetwork = true, TimeSpan? requestTimeout = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!allowNetwork) return Read(profileId, providerIdentity) ??
            new(profileId, providerIdentity, feed.DisplayName, _utcNow(), Array.Empty<Instrument>(), true, true, "No saved catalog for this provider configuration.");
        try
        {
            var timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(timeout);
            IReadOnlyList<Instrument> instruments;
            try { instruments = await feed.GetInstrumentsAsync(bounded.Token).WaitAsync(timeout, ct).ConfigureAwait(false); }
            finally { bounded.Cancel(); }
            ct.ThrowIfCancellationRequested();
            Validate(instruments);
            var snapshot = new InstrumentCatalogSnapshot(profileId, providerIdentity, feed.DisplayName, _utcNow(), instruments.ToArray(), false, false, null);
            try { await SaveAsync(snapshot, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                snapshot = snapshot with { Warning = "Catalog is available, but the offline copy could not be saved: " + ex.Message };
            }
            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            var saved = Read(profileId, providerIdentity);
            if (saved == null || saved.Instruments.Count == 0) throw;
            return saved with { Warning = "Live instrument discovery is unavailable. " + ex.GetType().Name };
        }
    }

    public InstrumentCatalogSnapshot? Read(string profileId, string providerIdentity)
    {
        try
        {
            var path = FileFor(profileId, providerIdentity);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes) return null;
            var saved = JsonSerializer.Deserialize<SavedCatalog>(File.ReadAllText(path));
            if (saved is not { Version: 1 } || saved.ProfileId != profileId || saved.ProviderIdentity != providerIdentity ||
                saved.CapturedUtc.Kind != DateTimeKind.Utc || saved.CapturedUtc > _utcNow().AddMinutes(5)) return null;
            Validate(saved.Instruments);
            return new(profileId, providerIdentity, saved.ProviderLabel, saved.CapturedUtc, saved.Instruments, true, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { return null; }
    }

    private async Task SaveAsync(InstrumentCatalogSnapshot snapshot, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        var path = FileFor(snapshot.ProfileId, snapshot.ProviderIdentity);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, new SavedCatalog(1, snapshot.ProfileId, snapshot.ProviderIdentity,
                    snapshot.ProviderLabel, snapshot.CapturedUtc, snapshot.Instruments.ToList()), cancellationToken: ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string FileFor(string profileId, string identity) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId + "\n" + identity))) + ".json");
    private static void Validate(IReadOnlyList<Instrument> instruments)
    {
        if (instruments.Count > MaxRows) throw new InvalidDataException("Instrument catalog exceeds the bounded row limit.");
        foreach (var i in instruments)
            if (string.IsNullOrWhiteSpace(i.Symbol) || string.IsNullOrWhiteSpace(i.FeedId) || i.TickSize <= 0 || i.VolumeStep <= 0 || i.PointValue <= 0)
                throw new InvalidDataException("Instrument catalog contains invalid contract metadata.");
    }
}
