using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;
using TradingHub.Feeds.External;

namespace TradingHub.App.Services;

/// <summary>Where an options-data connection gets its data.</summary>
public enum OptionsSource
{
    [Display(Name = "Deribit (public, BTC and ETH)")] Deribit,
    [Display(Name = "Custom JSON API (your provider, key optional)")] CustomJson,
}

public sealed partial class ConnectionProfile
{
    /// <summary>Options data connection: the source.</summary>
    public OptionsSource OptionsSource { get; set; } = OptionsSource.Deribit;

    /// <summary>Custom options or ETF flows API: base URL (the key lives in the secret store).</summary>
    public string ExternalUrl { get; set; } = "";

    /// <summary>Header that carries the key; "Authorization" sends it as a Bearer token.</summary>
    public string ExternalHeader { get; set; } = "Authorization";

    /// <summary>Underlyings or assets the custom API covers (blank = any).</summary>
    public string ExternalUnderlyings { get; set; } = "";

    /// <summary>Data for studies (options, ETF flows), not a chart market.</summary>
    [JsonIgnore] public bool IsExternalData => Kind is ConnectionKind.OptionsData or ConnectionKind.EtfFlows;

    /// <summary>The underlyings typed by the user (BTC, ETH, SPY…), upper case.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ExternalUnderlyingList =>
        ExternalUnderlyings.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim().ToUpperInvariant()).Distinct().ToList();
}

public sealed partial class ConnectionManager
{
    private readonly object _externalGate = new();
    private string _externalKey = "";
    private readonly List<HttpClient> _externalClients = new();

    /// <summary>Isolated runs point Deribit at a fake origin; production uses Deribit's public REST root.</summary>
    private static Uri? DeribitRoot => Environment.GetEnvironmentVariable("TRADINGHUB_DERIBIT_REST") is { Length: > 0 } u ? new Uri(u) : null;

    /// <summary>The options provider of a connection; null when a custom API has no URL.</summary>
    private IOptionsProvider? OptionsProviderOf(ConnectionProfile p, List<HttpClient> clients)
    {
        if (p.OptionsSource == OptionsSource.Deribit)
        {
            var http = _networkFactory().CreateHttpClient(TimeSpan.FromSeconds(25));
            clients.Add(http);
            return new DeribitOptions(http, DeribitRoot);
        }

        if (!Uri.TryCreate(p.ExternalUrl.Trim(), UriKind.Absolute, out _)) return null;
        var client = CreatePrivateHttpClient(TimeSpan.FromSeconds(30));
        clients.Add(client);
        return new JsonOptionsApi(client, p.ExternalUrl, p.ExternalHeader, GetSecret(p), p.ExternalUnderlyingList);
    }

    /// <summary>The ETF flows provider of a connection; null without a URL.</summary>
    private IEtfFlowsProvider? EtfProviderOf(ConnectionProfile p, List<HttpClient> clients)
    {
        if (!Uri.TryCreate(p.ExternalUrl.Trim(), UriKind.Absolute, out _)) return null;
        var client = CreatePrivateHttpClient(TimeSpan.FromSeconds(30));
        clients.Add(client);
        return new JsonEtfFlowsApi(client, p.ExternalUrl, p.ExternalHeader, GetSecret(p), p.ExternalUnderlyingList);
    }

    /// <summary>
    /// Points the shared options and ETF-flow data at the first enabled connection of each kind (none: the studies wait
    /// and make no request). Called at startup and whenever connections are saved; unchanged settings keep their caches.
    /// </summary>
    public void ApplyExternalData(OptionsData? options, EtfFlows? etf)
    {
        var opt = Enabled.FirstOrDefault(p => p.Kind == ConnectionKind.OptionsData);
        var flows = Enabled.FirstOrDefault(p => p.Kind == ConnectionKind.EtfFlows);
        string Sig(ConnectionProfile? p) => p == null ? "-" : string.Join("|", p.Id, p.OptionsSource, p.ExternalUrl, p.ExternalHeader, p.ExternalUnderlyings,
            string.IsNullOrEmpty(GetSecret(p)) ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(GetSecret(p)))),
            Environment.GetEnvironmentVariable("TRADINGHUB_DERIBIT_REST"));
        string key = Sig(opt) + "#" + Sig(flows) + "#" + (options?.GetHashCode() ?? 0).ToString(CultureInfo.InvariantCulture) + "#" + (etf?.GetHashCode() ?? 0).ToString(CultureInfo.InvariantCulture);
        List<HttpClient> retired;
        lock (_externalGate)
        {
            if (key == _externalKey) return;
            _externalKey = key;
            retired = _externalClients.ToList();
            _externalClients.Clear();
            if (options != null) options.Provider = opt == null ? null : OptionsProviderOf(opt, _externalClients);
            if (etf != null) etf.Provider = flows == null ? null : EtfProviderOf(flows, _externalClients);
        }

        foreach (var http in retired) http.Dispose();
    }

    /// <summary>Releases the external data clients (application shutdown).</summary>
    public void DisposeExternalData()
    {
        lock (_externalGate)
        {
            foreach (var http in _externalClients) http.Dispose();
            _externalClients.Clear();
            _externalKey = "";
        }
    }

    private async Task<(bool Ok, string Message)> TestExternalAsync(ConnectionProfile p, CancellationToken ct)
    {
        var clients = new List<HttpClient>();
        try
        {
            if (p.Kind == ConnectionKind.OptionsData)
            {
                var provider = OptionsProviderOf(p, clients);
                if (provider == null) return (false, "Enter the API base URL.");
                string under = p.OptionsSource == OptionsSource.Deribit ? "BTC" : p.ExternalUnderlyingList.FirstOrDefault() ?? "BTC";
                var chain = await provider.ChainAsync(under, ct);
                if (chain == null || chain.Contracts.Count == 0) return (false, $"{provider.Name} returned no options on {under}.");
                int expiries = chain.Contracts.Select(c => c.Expiry).Distinct().Count();
                return (true, string.Create(CultureInfo.InvariantCulture,
                    $"{provider.Name}: {chain.Contracts.Count} {under} contracts in {expiries} expiries (spot {chain.Spot:0.##}, snapshot {chain.Time:yyyy-MM-dd HH:mm} UTC). Options studies use it once saved."));
            }

            var etf = EtfProviderOf(p, clients);
            if (etf == null) return (false, "Enter the API base URL.");
            string asset = p.ExternalUnderlyingList.FirstOrDefault() ?? "BTC";
            var days = await etf.DailyAsync(asset, DateTime.UtcNow.AddDays(-14), DateTime.UtcNow, ct);
            if (days.Count == 0) return (false, $"The API returned no {asset} days in the last two weeks.");
            var last = days.OrderBy(d => d.Date).Last();
            return (true, string.Create(CultureInfo.InvariantCulture, $"{days.Count} {asset} days; the latest ({last.Date:yyyy-MM-dd}) {last.NetUsd / 1e6:+0.0;-0.0} M USD."));
        }
        finally
        {
            foreach (var http in clients) http.Dispose();
        }
    }
}
