using System.Net;
using System.Text.Json;
using TradingHub.Networking;

namespace TradingHub.App.Services;

public sealed partial class AppSettings
{
    public NetworkProxyPolicy NetworkProxy { get; set; } = new();
    public double RawHistoryHours { get; set; } = 168;
    public int MaxDiskTrades { get; set; } = 2_000_000;
    public bool UseCompactHistory { get; set; }
    public int CompactHistoryMinutes { get; set; } = 5;
    public int MaxCompactTrades { get; set; } = 500_000;
    public KlineMaintenanceSettings CandleMaintenance { get; set; } = new();
    public bool OfflineCatalogOnly { get; set; }
}

/// <summary>Proxy credentials are stored only through the existing private secret store, never AppSettings.</summary>
public sealed class AppNetworkProxy(AppSettings settings, ISecretStore secrets, Action? persist = null)
{
    private const string CredentialKey = "network-proxy:credentials";
    private sealed record Credentials(string Endpoint, string Username, string Password);
    public NetworkProxyPolicy Policy => settings.NetworkProxy ?? new();

    public ProxyHandlerFactory CreateFactory()
    {
        var policy = Policy;
        NetworkCredential? credential = null;
        if (policy.Mode == NetworkProxyMode.Custom)
        {
            var encoded = secrets.Get(CredentialKey);
            if (encoded.Length > 0)
            {
                var saved = JsonSerializer.Deserialize<Credentials>(encoded);
                if (saved?.Endpoint == policy.Address) credential = new(saved.Username, saved.Password);
            }
        }
        return new(policy, credential);
    }
    public void Save(NetworkProxyPolicy policy, string? username, string? password, bool replaceCredentials)
    {
        if (policy.Mode != NetworkProxyMode.Custom) policy = policy with { Address = "" };
        _ = new ProxyHandlerFactory(policy); // Reject credentials in an address before writing anything.
        if (replaceCredentials)
            SetCredentials(string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password) ? null :
                JsonSerializer.Serialize(new Credentials(policy.Address, username ?? "", password ?? "")));
        settings.NetworkProxy = policy with { };
        if (persist != null) persist();
        else settings.Save();
    }
    public void ClearCredentials() => SetCredentials(null);
    private void SetCredentials(string? value)
    {
        secrets.Set(CredentialKey, value);
        if (secrets.Get(CredentialKey) != (value ?? ""))
            throw new IOException("The private credential store did not confirm the proxy credential change.");
    }
}
