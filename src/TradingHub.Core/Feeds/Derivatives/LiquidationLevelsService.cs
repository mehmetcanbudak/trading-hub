// Behavior adapted from the KVP capability reference (commit df8844a); see THIRD_PARTY_NOTICES.md.
using System.Text.Json;

namespace TradingHub.Feeds.Derivatives;

/// <summary>One price bin of a third-party liquidation-level snapshot: estimated long and short notional and the largest single position on each side.</summary>
public readonly record struct LiquidationLevelBin(decimal Price, decimal LongNotional, decimal ShortNotional, decimal LargestLong, decimal LargestShort);

/// <summary>A published snapshot of estimated liquidation prices for open positions, as received (never recomputed locally).</summary>
public sealed record LiquidationLevelsSnapshot(DateTime AsOfUtc, decimal Mark, decimal Step, int Positions, string Attribution,
    IReadOnlyList<LiquidationLevelBin> Bins, DateTime FetchedUtc);

public sealed record LiquidationLevelsResult(LiquidationLevelsSnapshot? Snapshot, string? Message);

/// <summary>
/// Opt-in, off-by-default client for a third-party service that publishes estimated Hyperliquid BTC liquidation levels
/// (position snapshots by HyperTracker, binned by the service). The data is a third-party estimate: Trading Hub neither
/// verifies nor recomputes it, sends no account data or credentials, and fetches only while the user has enabled it.
/// <see cref="Shared"/> is null whenever the setting is off, so studies cannot reach the network by accident.
/// </summary>
public sealed class LiquidationLevelsService : IDisposable
{
    public static readonly Uri DefaultEndpoint = new("https://levels.hiverust.dev/v1/btc/liquidations.json");
    public const string ProvenanceLabel = "Third-party estimate";
    public static readonly TimeSpan MinimumRefresh = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromHours(3);

    /// <summary>The application's instance while the user has enabled third-party liquidation levels; otherwise null.</summary>
    public static LiquidationLevelsService? Shared { get; set; }

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LiquidationLevelsResult _last = new(null, null);
    private DateTime _lastAttempt = DateTime.MinValue;
    private int _disposed;

    public LiquidationLevelsService(HttpClient http, bool ownsHttp, Uri? endpoint = null, Func<DateTime>? utcNow = null)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        Endpoint = endpoint ?? DefaultEndpoint;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public Uri Endpoint { get; }

    /// <summary>Only BTC is published by the service.</summary>
    public static bool Supports(string baseAsset, string symbol) =>
        baseAsset.Trim().Equals("BTC", StringComparison.OrdinalIgnoreCase) ||
        baseAsset.Trim().Length == 0 && symbol.Trim().StartsWith("BTC", StringComparison.OrdinalIgnoreCase);

    /// <summary>The latest result without network access.</summary>
    public LiquidationLevelsResult Latest => Volatile.Read(ref _last);

    /// <summary>Fetches a new snapshot unless one was attempted within <see cref="MinimumRefresh"/>; failures keep the previous snapshot and report why.</summary>
    public async Task<LiquidationLevelsResult> RefreshAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _utcNow();
            if (now - _lastAttempt < MinimumRefresh) return _last;
            _lastAttempt = now;
            LiquidationLevelsResult result;
            try
            {
                using var response = await _http.GetAsync(Endpoint, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    result = new(_last.Snapshot, $"The liquidation-level service answered HTTP {(int)response.StatusCode}.");
                else
                {
                    string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    result = Parse(json, now) is { } snapshot ? new(snapshot, null) : new(_last.Snapshot, "The liquidation-level service returned an unsupported format.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException && !ct.IsCancellationRequested)
            {
                result = new(_last.Snapshot, $"The liquidation-level service is unreachable: {ex.Message}");
            }
            Volatile.Write(ref _last, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Format version 2: <c>{"v":2,"step":10,"mark":…,"positions":…,"attribution":"…","snapshot":{"asOf":ms},"bins":[[k, long isolated,
    /// long cross, short isolated, short cross, long wallets, short wallets, largest long, largest short], …]}</c>; bin price = k × step.
    /// Returns null for another version or a non-positive step.
    /// </summary>
    public static LiquidationLevelsSnapshot? Parse(string json, DateTime fetchedUtc)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 2) return null;
        if (!root.TryGetProperty("snapshot", out var snapshot) || !snapshot.TryGetProperty("asOf", out var asOf)) return null;
        decimal step = root.GetProperty("step").GetDecimal();
        if (step <= 0) return null;
        var bins = new List<LiquidationLevelBin>();
        foreach (var b in root.GetProperty("bins").EnumerateArray())
        {
            if (b.ValueKind != JsonValueKind.Array || b.GetArrayLength() < 9) continue;
            bins.Add(new LiquidationLevelBin(b[0].GetDecimal() * step, b[1].GetDecimal() + b[2].GetDecimal(), b[3].GetDecimal() + b[4].GetDecimal(), b[7].GetDecimal(), b[8].GetDecimal()));
        }
        string attribution = root.TryGetProperty("attribution", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
        int positions = root.TryGetProperty("positions", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        return new LiquidationLevelsSnapshot(DateTimeOffset.FromUnixTimeMilliseconds(asOf.GetInt64()).UtcDateTime, root.GetProperty("mark").GetDecimal(), step,
            positions, attribution, bins.OrderBy(x => x.Price).ToArray(), fetchedUtc);
    }

    /// <summary>
    /// What remains after the price traded through: a long bin is gone once the low since the snapshot reached its top edge,
    /// a short bin once the high since the snapshot reached its price.
    /// </summary>
    public static IReadOnlyList<LiquidationLevelBin> Remaining(LiquidationLevelsSnapshot snapshot, decimal lowSince, decimal highSince) =>
        snapshot.Bins.Select(b => b with
        {
            LongNotional = b.Price + snapshot.Step > lowSince ? 0 : b.LongNotional,
            ShortNotional = b.Price < highSince ? 0 : b.ShortNotional,
        }).Where(b => b.LongNotional > 0 || b.ShortNotional > 0).ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsHttp) _http.Dispose();
        _gate.Dispose();
    }
}
