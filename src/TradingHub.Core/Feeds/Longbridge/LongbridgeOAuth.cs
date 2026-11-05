// Longbridge protocol/endpoints adapted from kvp61/trading-hub, commit 50220ba91613161685f84384825d855cb4dbeb76.
// Original project by ralphtradesz; MIT with Commons Clause. Auth/session ownership is independently redesigned.
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingHub.Feeds.Longbridge;

/// <summary>
/// One connection's public-client OAuth session. Only the injected secret-store delegates persist credentials.
/// Begin does not open a browser or bind a port: the application owns its listener and passes its exact callback
/// URI to Complete. A replaced authorization, credential store or retired generation cannot publish a late result.
/// The injected HttpClient remains caller-owned. Exceptions never include token/code/response payloads.
/// </summary>
public sealed class LongbridgeSession : IDisposable, IAsyncDisposable
{
    private readonly LongbridgeSettings _settings;
    private readonly HttpClient _http;
    private readonly Func<string?> _getSecret;
    private readonly Action<string?> _setSecret;
    private readonly TimeProvider _clock;
    private readonly Func<int, byte[]> _entropy;
    private readonly Uri _endpoint;
    private readonly string _region;
    private readonly string _quoteResource;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly CancellationTokenSource _retirement = new();
    private readonly LongbridgeOwnedTasks _owned;
    private readonly object _stopGate = new();
    private Task? _stop;
    private string? _observedSecret;
    private long _generation = 1;
    private long _authorizationRevision;
    private Pending? _pending;
    private sealed record Pending(string State, string Verifier, Uri RedirectUri, DateTimeOffset ExpiresAt, string ClientId,
        long Revision, long Generation, string? Secret);
    private sealed record Credential(string ClientId, string Authority, string Region, string QuoteResource,
        string AccessToken, string RefreshToken, long ExpiresAt);
    private sealed record Token(string Access, string Refresh, long ExpiresAt);

    public LongbridgeSession(LongbridgeSettings settings, HttpClient http, Func<string?> getSecret, Action<string?> setSecret,
        TimeProvider? clock = null, Func<int, byte[]>? entropy = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _getSecret = getSecret ?? throw new ArgumentNullException(nameof(getSecret));
        _setSecret = setSecret ?? throw new ArgumentNullException(nameof(setSecret));
        _clock = clock ?? TimeProvider.System;
        _entropy = entropy ?? RandomNumberGenerator.GetBytes;
        _endpoint = settings.HttpEndpoint;
        _region = settings.Region;
        _quoteResource = settings.QuoteEndpoint.AbsoluteUri;
        _owned = new(nameof(LongbridgeSession), _retirement, cancelClosedAdmission: true);
        _observedSecret = _getSecret();
    }

    public event Action<long>? CredentialsChanged;
    public long CredentialGeneration
    {
        get
        {
            if (!_owned.TryEnter(out var admission)) return Interlocked.Read(ref _generation);
            using (admission) { lock (_sync) { if (!IsRetired) ObserveLocked(); return _generation; } }
        }
    }
    public bool HasAuthorization
    {
        get
        {
            if (!_owned.TryEnter(out var admission)) return false;
            using (admission) { lock (_sync) { if (IsRetired) return false; ObserveLocked(); return !string.IsNullOrEmpty(_observedSecret); } }
        }
    }
    public bool IsRetired => _owned.IsClosed;
    public CancellationToken LifetimeToken => _owned.Token;
    public DateTimeOffset UtcNow => _clock.GetUtcNow();

    private void ObserveLocked()
    {
        if (IsRetired) throw new InvalidOperationException("Longbridge session is retired; create a session for the current connection.");
        if (_settings.Region != _region || _settings.HttpEndpoint != _endpoint || _settings.QuoteEndpoint.AbsoluteUri != _quoteResource)
            throw new InvalidOperationException("Longbridge settings changed; retire the old context before using a new client or resource.");
        string? current = _getSecret();
        if (string.Equals(current, _observedSecret, StringComparison.Ordinal)) return;
        _observedSecret = current;
        _generation++;
    }

    public Task<LongbridgeAuthorization> BeginAuthorizationAsync(Uri redirectUri, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(BeginAuthorizationCoreAsync(redirectUri, ct));
    }
    private async Task<LongbridgeAuthorization> BeginAuthorizationCoreAsync(Uri redirectUri, CancellationToken ct)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, LifetimeToken);
        ct = operation.Token;
        if (!redirectUri.IsAbsoluteUri || !redirectUri.IsLoopback || redirectUri.Scheme != "http" ||
            redirectUri.Query.Length != 0 || redirectUri.Fragment.Length != 0 || redirectUri.UserInfo.Length != 0)
            throw new ArgumentException("Longbridge OAuth requires the exact HTTP loopback callback URI owned by the application.", nameof(redirectUri));
        ct.ThrowIfCancellationRequested();
        LifetimeToken.ThrowIfCancellationRequested();
        long revision;
        lock (_sync) { ObserveLocked(); revision = ++_authorizationRevision; _pending = null; }
        string clientId = _settings.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            using var response = await _http.PostAsJsonAsync(Url("oauth2/register"), new
            {
                client_name = "Trading Hub", redirect_uris = new[] { redirectUri.AbsoluteUri },
                grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" },
                token_endpoint_auth_method = "none",
            }, ct).ConfigureAwait(false);
            using var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            clientId = String(body.RootElement, "client_id");
            if (clientId.Length == 0) throw new InvalidDataException("Longbridge registration did not return a public client ID.");
        }
        ct.ThrowIfCancellationRequested();
        LifetimeToken.ThrowIfCancellationRequested();
        string verifier = Base64Url(Entropy(32));
        string state = Base64Url(Entropy(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var expires = UtcNow.AddMinutes(5);
        lock (_sync)
        {
            ObserveLocked();
            if (revision != _authorizationRevision) throw new InvalidOperationException("Longbridge authorization was replaced.");
            _settings.ClientId = clientId;
            _pending = new(state, verifier, redirectUri, expires, clientId, revision, _generation, _observedSecret);
        }
        string query = Form(new()
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["access_type"] = "offline",
        });
        return new(new Uri(Url("oauth2/authorize") + "?" + query), redirectUri, expires, clientId);
    }

    public Task CompleteAuthorizationAsync(Uri callbackUri, CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(CompleteAuthorizationCoreAsync(callbackUri, ct));
    }
    private async Task CompleteAuthorizationCoreAsync(Uri callbackUri, CancellationToken ct)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, LifetimeToken);
        ct = operation.Token;
        ct.ThrowIfCancellationRequested();
        LifetimeToken.ThrowIfCancellationRequested();
        Pending pending;
        string code;
        lock (_sync)
        {
            ObserveLocked();
            pending = _pending ?? throw new InvalidOperationException("No current Longbridge authorization is pending.");
            if (!callbackUri.IsAbsoluteUri || callbackUri.Fragment.Length != 0 || callbackUri.UserInfo.Length != 0 ||
                callbackUri.GetLeftPart(UriPartial.Path) != pending.RedirectUri.GetLeftPart(UriPartial.Path))
                throw new InvalidOperationException("Longbridge callback does not match the application-owned redirect URI.");
            var query = ParseQuery(callbackUri);
            if (!query.TryGetValue("state", out var state) || !Equal(state, pending.State))
                throw new InvalidOperationException("Longbridge authorization state does not match the current request.");
            if (pending.ExpiresAt <= UtcNow || pending.Generation != _generation || pending.Revision != _authorizationRevision ||
                !string.Equals(pending.Secret, _observedSecret, StringComparison.Ordinal))
                throw new InvalidOperationException("Longbridge authorization expired or its credentials were replaced; begin again.");
            _pending = null;
            if (query.ContainsKey("error")) throw new InvalidOperationException("Longbridge authorization was denied or cancelled.");
            if (!query.TryGetValue("code", out code!) || string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Longbridge callback did not contain an authorization code.");
        }
        var token = await TokenRequestAsync(new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = pending.ClientId, ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri.AbsoluteUri, ["code_verifier"] = pending.Verifier,
        }, "", ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        LifetimeToken.ThrowIfCancellationRequested();
        long generation;
        lock (_sync)
        {
            ObserveLocked();
            if (pending.Generation != _generation || pending.Revision != _authorizationRevision ||
                _settings.ClientId != pending.ClientId || !string.Equals(pending.Secret, _observedSecret, StringComparison.Ordinal))
                throw new InvalidOperationException("Longbridge credentials changed before authorization completed; no tokens were saved.");
            SaveLocked(new(pending.ClientId, _endpoint.AbsoluteUri, _region, _quoteResource, token.Access, token.Refresh, token.ExpiresAt));
            generation = _generation;
        }
        CredentialsChanged?.Invoke(generation);
    }

    /// <summary>Closes admission immediately without deleting saved credentials; DisposeAsync awaits terminal retirement.</summary>
    public void Retire() => Dispose();
    public void Dispose() => _ = BeginStop();
    public ValueTask DisposeAsync() => new(BeginStop());
    private Task BeginStop()
    {
        lock (_stopGate)
        {
            if (_stop != null) return _stop;
            var drained = _owned.CloseAsync();
            var retired = Task.Run(() =>
            {
                long generation;
                lock (_sync) { _authorizationRevision++; _pending = null; generation = ++_generation; }
                CredentialsChanged?.Invoke(generation);
            });
            _stop = CompleteStopAsync(drained, retired);
            return _stop;
        }
    }
    private async Task CompleteStopAsync(Task drained, Task retired)
    {
        try { await Task.WhenAll(drained, retired).ConfigureAwait(false); }
        finally { _refresh.Dispose(); _retirement.Dispose(); }
    }

    /// <summary>Explicit disconnect/revoke-local action. Disposing a data feed does not delete stored credentials.</summary>
    public void ForgetAuthorization()
    {
        if (IsRetired) throw new InvalidOperationException("A retired Longbridge session cannot delete current credentials.");
        using var admission = _owned.Enter();
        long generation;
        lock (_sync)
        {
            if (IsRetired) throw new InvalidOperationException("A retired Longbridge session cannot delete current credentials.");
            _authorizationRevision++;
            _pending = null;
            _setSecret(null);
            _observedSecret = null;
            generation = ++_generation;
        }
        CredentialsChanged?.Invoke(generation);
    }

    public Task<string> AccessTokenAsync(CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(AccessTokenCoreAsync(ct));
    }
    private async Task<string> AccessTokenCoreAsync(CancellationToken ct)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, LifetimeToken);
        ct = operation.Token;
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Credential credential;
            string? original;
            long generation;
            lock (_sync)
            {
                ObserveLocked();
                original = _observedSecret;
                credential = ReadLocked();
                generation = _generation;
            }
            LifetimeToken.ThrowIfCancellationRequested();
            long now = UtcNow.ToUnixTimeSeconds();
            if (credential.ExpiresAt > now + 60) return credential.AccessToken;
            if (credential.RefreshToken.Length == 0)
            {
                if (credential.ExpiresAt > now) return credential.AccessToken;
                throw new InvalidOperationException("Longbridge authorization expired and has no refresh token; authorize again.");
            }
            var token = await TokenRequestAsync(new()
            {
                ["grant_type"] = "refresh_token", ["client_id"] = credential.ClientId, ["refresh_token"] = credential.RefreshToken,
            }, credential.RefreshToken, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            LifetimeToken.ThrowIfCancellationRequested();
            long changed;
            lock (_sync)
            {
                ObserveLocked();
                if (generation != _generation || _settings.ClientId != credential.ClientId || !string.Equals(original, _observedSecret, StringComparison.Ordinal))
                    throw new InvalidOperationException("Longbridge credentials were replaced while refreshing; the old result was discarded.");
                SaveLocked(credential with { AccessToken = token.Access, RefreshToken = token.Refresh, ExpiresAt = token.ExpiresAt });
                changed = _generation;
            }
            CredentialsChanged?.Invoke(changed);
            return token.Access;
        }
        finally { _refresh.Release(); }
    }

    public Task<LongbridgeSocketCredential> SocketOtpAsync(CancellationToken ct)
    {
        using var admission = _owned.Enter();
        return _owned.Track(SocketOtpCoreAsync(ct));
    }
    private async Task<LongbridgeSocketCredential> SocketOtpCoreAsync(CancellationToken ct)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, LifetimeToken);
        ct = operation.Token;
        string access = await AccessTokenAsync(ct).ConfigureAwait(false);
        long generation;
        string clientId;
        lock (_sync)
        {
            ObserveLocked();
            var credential = ReadLocked();
            if (credential.AccessToken != access) throw new InvalidOperationException("Longbridge credentials changed before requesting a quote session.");
            generation = _generation;
            clientId = credential.ClientId;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("v1/socket/token"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.TryAddWithoutValidation("X-Api-Key", clientId);
        request.Headers.TryAddWithoutValidation("X-Timestamp", UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-dc-region", access.StartsWith("us_", StringComparison.Ordinal) ? "us" : "ap");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var errorCode))
        {
            if (!errorCode.TryGetInt64(out long n)) throw new InvalidDataException("Longbridge returned an invalid quote-session status.");
            if (n != 0) throw new InvalidOperationException("Longbridge rejected the quote-session token request.");
        }
        var data = root.TryGetProperty("data", out var value) ? value : root;
        if (data.TryGetProperty("limit", out var limit) && data.TryGetProperty("online", out var online) &&
            limit.TryGetInt32(out int max) && online.TryGetInt32(out int count) && count >= max)
            throw new InvalidOperationException("Longbridge quote-session limit reached; close another authorized data session.");
        string otp = String(data, "otp");
        if (string.IsNullOrWhiteSpace(otp)) throw new InvalidDataException("Longbridge did not return a quote-session token.");
        LifetimeToken.ThrowIfCancellationRequested();
        if (CredentialGeneration != generation) throw new InvalidOperationException("Longbridge credentials changed while requesting a quote session.");
        return new(otp, generation);
    }

    private void SaveLocked(Credential credential)
    {
        string serialized = JsonSerializer.Serialize(credential);
        _setSecret(serialized);
        _observedSecret = serialized;
        _generation++;
    }

    private Credential ReadLocked()
    {
        if (string.IsNullOrWhiteSpace(_observedSecret)) throw new InvalidOperationException("Longbridge is not authorized; connect through OAuth first.");
        Credential? value;
        try { value = JsonSerializer.Deserialize<Credential>(_observedSecret); }
        catch (JsonException) { throw new InvalidDataException("Stored Longbridge credentials are invalid; authorize again."); }
        if (value == null || value.ClientId != _settings.ClientId || value.Authority != _endpoint.AbsoluteUri ||
            value.Region != _region || value.QuoteResource != _quoteResource ||
            string.IsNullOrWhiteSpace(value.AccessToken) || value.RefreshToken == null || value.ExpiresAt <= 0)
            throw new InvalidDataException("Stored Longbridge credentials do not match this public client, region and resource; authorize again.");
        return value;
    }

    private async Task<Token> TokenRequestAsync(Dictionary<string, string> fields, string keepRefresh, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("oauth2/token")) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        using var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var root = body.RootElement;
        string access = String(root, "access_token"), refresh = String(root, "refresh_token"), type = String(root, "token_type");
        if (string.IsNullOrWhiteSpace(access) || root.TryGetProperty("error", out _) ||
            (type.Length != 0 && !type.Equals("Bearer", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Longbridge returned an invalid OAuth token response.");
        if (!root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt64(out long seconds) || seconds <= 0 || seconds > 315_576_000)
            throw new InvalidDataException("Longbridge did not provide a valid token expiry; no expiry is assumed.");
        return new(access, refresh.Length == 0 ? keepRefresh : refresh, checked(UtcNow.ToUnixTimeSeconds() + seconds));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Longbridge request failed (HTTP {(int)response.StatusCode}); no response secrets are logged.", null, response.StatusCode);
        try
        {
            var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { throw new InvalidDataException("Longbridge returned malformed JSON; no response secrets are logged."); }
        throw new InvalidDataException("Longbridge returned an invalid JSON response.");
    }

    private Uri Url(string path) => new(_endpoint.AbsoluteUri.TrimEnd('/') + "/" + path);
    private byte[] Entropy(int size)
    {
        byte[] bytes = _entropy(size);
        if (bytes.Length != size) throw new InvalidOperationException("OAuth entropy source returned an invalid length.");
        return bytes;
    }
    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    private static string Form(Dictionary<string, string> fields) => string.Join("&", fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            string value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!result.TryAdd(key, value)) throw new InvalidOperationException("Longbridge callback contains duplicate parameters.");
        }
        return result;
    }
}
