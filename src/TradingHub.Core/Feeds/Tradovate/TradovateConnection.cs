using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Market;

namespace TradingHub.Feeds.Tradovate;

/// <summary>Conexión de Tradovate (API oficial): usuario y contraseña de Tradovate más la API key (CID y secret).</summary>
public sealed class TradovateSettings
{
    /// <summary>Cuenta real (live.tradovateapi.com); si no, demo.</summary>
    public bool Live { get; set; }
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>API key de Tradovate (Settings → API Access): CID y secret.</summary>
    public string Cid { get; set; } = "";
    public string Sec { get; set; } = "";
    public string AppId { get; set; } = "TradingHub";
    public string AppVersion { get; set; } = "1.0";

    /// <summary>Id fijo de este equipo (Tradovate limita la cantidad de dispositivos).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Productos para el buscador (contrato del mes de cada uno).</summary>
    public List<string> Products { get; set; } = new() { "ES", "NQ", "MES", "MNQ", "YM", "MYM", "RTY", "M2K", "CL", "MCL", "GC", "MGC" };

    /// <summary>Servidores alternativos (pruebas).</summary>
    public string? RestUrl { get; set; }
    public string? UserSocketUrl { get; set; }
    public string? MdSocketUrl { get; set; }
}

/// <summary>Error de la API de Tradovate.</summary>
public sealed class TradovateException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Una conexión por perfil a Tradovate: un solo token (cada login abre una sesión y Tradovate deja dos), renovado antes
/// de que venza; REST para órdenes y consultas; y dos WebSocket compartidos: datos de mercado (md) y la cuenta (user).
/// </summary>
public sealed class TradovateConnection : IDisposable
{
    private readonly Func<TradovateSettings> _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (long Id, string Name)> _contracts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, string> _contractNames = new();
    private string? _access, _md;
    private DateTime _expires;
    private TradovateSocket? _mdSocket, _userSocket;

    public TradovateConnection(Func<TradovateSettings> settings) => _settings = settings;

    public TradovateSettings Settings => _settings();
    public long UserId { get; private set; }
    public string UserName { get; private set; } = "";
    public bool HasLive { get; private set; }
    public bool IsDemo => !Settings.Live;

    public string RestBase => (Settings.RestUrl ?? (Settings.Live ? "https://live.tradovateapi.com/v1" : "https://demo.tradovateapi.com/v1")).TrimEnd('/');
    private string UserSocketUrl => Settings.UserSocketUrl ?? (Settings.Live ? "wss://live.tradovateapi.com/v1/websocket" : "wss://demo.tradovateapi.com/v1/websocket");
    private string MdSocketUrl => Settings.MdSocketUrl ?? "wss://md.tradovateapi.com/v1/websocket";

    public TradovateSocket Md => _mdSocket ??= new TradovateSocket(MdSocketUrl, ct => MdTokenAsync(ct), "Tradovate data connection");
    public TradovateSocket User => _userSocket ??= new TradovateSocket(UserSocketUrl, ct => AccessTokenAsync(ct), "Tradovate account connection");

    // ===================================================================== token

    public async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct).ConfigureAwait(false);
        return _access!;
    }

    public async Task<string> MdTokenAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct).ConfigureAwait(false);
        return _md ?? _access!;
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_access != null && DateTime.UtcNow < _expires.AddMinutes(-10)) return;
        await _authGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_access != null && DateTime.UtcNow < _expires.AddMinutes(-10)) return;
            // renovar no abre otra sesión; pedir uno nuevo sí (y Tradovate corta la más vieja si hay más de dos)
            if (_access != null && DateTime.UtcNow < _expires)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, RestBase + "/auth/renewaccesstoken");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
                    using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode && ReadToken(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), renew: true)) return;
                }
                catch (HttpRequestException)
                {
                    // se pide uno nuevo
                }
            }

            await LoginAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        var s = Settings;
        if (string.IsNullOrWhiteSpace(s.User) || string.IsNullOrEmpty(s.Password))
            throw new TradovateException("Tradovate username and password are missing.");
        var body = new JsonObject
        {
            ["name"] = s.User.Trim(),
            ["password"] = s.Password,
            ["appId"] = string.IsNullOrWhiteSpace(s.AppId) ? "TradingHub" : s.AppId,
            ["appVersion"] = string.IsNullOrWhiteSpace(s.AppVersion) ? "1.0" : s.AppVersion,
            ["deviceId"] = string.IsNullOrWhiteSpace(s.DeviceId) ? Guid.NewGuid().ToString() : s.DeviceId,
        };
        if (!string.IsNullOrWhiteSpace(s.Cid)) body["cid"] = s.Cid.Trim();
        if (!string.IsNullOrWhiteSpace(s.Sec)) body["sec"] = s.Sec.Trim();
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(RestBase + "/auth/accesstokenrequest", content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new TradovateException($"Tradovate returned {(int)resp.StatusCode} during sign-in: {Short(text)}", (int)resp.StatusCode);
        if (!ReadToken(text, renew: false))
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("p-ticket", out _))
                throw new TradovateException("Tradovate requires a wait or captcha after too many attempts. Sign in on the Tradovate website once, then retry in a few minutes.");
            string err = root.TryGetProperty("errorText", out var e) ? e.GetString() ?? "" : "";
            throw new TradovateException(err.Length > 0 ? "Tradovate: " + err : "Tradovate returned no token (check username, password, CID and secret).");
        }
    }

    private bool ReadToken(string json, bool renew)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("accessToken", out var at) || at.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(at.GetString())) return false;
        _access = at.GetString();
        if (root.TryGetProperty("mdAccessToken", out var md) && md.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(md.GetString())) _md = md.GetString();
        else if (!renew) _md = null;
        _expires = root.TryGetProperty("expirationTime", out var exp) && DateTime.TryParse(exp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var e)
            ? e
            : DateTime.UtcNow.AddMinutes(80);
        if (root.TryGetProperty("userId", out var uid) && uid.TryGetInt64(out var u)) UserId = u;
        if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) UserName = n.GetString() ?? "";
        if (root.TryGetProperty("hasLive", out var hl)) HasLive = hl.ValueKind == JsonValueKind.True;
        return true;
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s;

    // ===================================================================== REST

    public async Task<JsonElement> GetAsync(string path, CancellationToken ct) => await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);

    public async Task<JsonElement> PostAsync(string path, JsonObject body, CancellationToken ct) => await SendAsync(HttpMethod.Post, path, body, ct).ConfigureAwait(false);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var token = await AccessTokenAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(method, RestBase + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                _access = null; // vencido o cortado: uno nuevo
                continue;
            }

            if ((int)resp.StatusCode == 429 && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 + attempt * 3), ct).ConfigureAwait(false);
                continue;
            }

            if (!resp.IsSuccessStatusCode) throw new TradovateException($"Tradovate returned {(int)resp.StatusCode}: {Short(text)}", (int)resp.StatusCode);
            using var doc = JsonDocument.Parse(text.Length == 0 ? "null" : text);
            return doc.RootElement.Clone();
        }
    }

    // ===================================================================== contratos

    /// <summary>Contrato del mes de un producto (ES → ESZ6) con su id, en una fecha.</summary>
    public async Task<(long Id, string Name)> ContractAsync(string root, DateTime utc, CancellationToken ct)
    {
        root = root.Trim().ToUpperInvariant();
        string? name = FuturesRolls.Supports(root) ? FuturesRolls.On(root, utc).Symbol : null;
        if (name == null)
        {
            // otros productos: el primero que sugiere Tradovate (el más cercano a vencer que siga activo)
            if (_contracts.TryGetValue("front|" + root, out var cached)) return cached;
            var list = await GetAsync($"/contract/suggest?t={Uri.EscapeDataString(root)}&l=10", ct).ConfigureAwait(false);
            var first = list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().FirstOrDefault(c => c.TryGetProperty("name", out var n) && (n.GetString() ?? "").StartsWith(root, StringComparison.OrdinalIgnoreCase) && (n.GetString()!.Length <= root.Length + 2))
                : default;
            if (first.ValueKind != JsonValueKind.Object) throw new TradovateException($"Tradovate has no contracts for {root}.");
            var r = (first.GetProperty("id").GetInt64(), first.GetProperty("name").GetString()!);
            _contracts["front|" + root] = r;
            _contractNames[r.Item1] = r.Item2;
            return r;
        }

        return await ContractByNameAsync(name, ct).ConfigureAwait(false);
    }

    public async Task<(long Id, string Name)> ContractByNameAsync(string name, CancellationToken ct)
    {
        if (_contracts.TryGetValue(name, out var c)) return c;
        var e = await GetAsync($"/contract/find?name={Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id)) throw new TradovateException($"Tradovate could not find contract {name}.");
        var r = (id.GetInt64(), e.GetProperty("name").GetString() ?? name);
        _contracts[name] = r;
        _contractNames[r.Item1] = r.Item2;
        return r;
    }

    /// <summary>Nombre de un contrato por su id (ESZ6).</summary>
    public async Task<string> ContractNameAsync(long id, CancellationToken ct)
    {
        if (_contractNames.TryGetValue(id, out var n)) return n;
        var e = await GetAsync($"/contract/item?id={id}", ct).ConfigureAwait(false);
        n = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var nm) ? nm.GetString() ?? id.ToString(CultureInfo.InvariantCulture) : id.ToString(CultureInfo.InvariantCulture);
        _contractNames[id] = n;
        return n;
    }

    public string? KnownContractName(long id) => _contractNames.TryGetValue(id, out var n) ? n : null;

    public void RememberContract(long id, string name)
    {
        _contractNames[id] = name;
        _contracts.TryAdd(name, (id, name));
    }

    public void Dispose()
    {
        _mdSocket?.Dispose();
        _userSocket?.Dispose();
        _http.Dispose();
    }
}

/// <summary>
/// WebSocket de Tradovate: marcos «o» (abierto: se autoriza), «h» (latido del servidor), «a[…]» (mensajes) y «c» (cierre).
/// Los pedidos van como «endpoint\nid\n\ncuerpo» y la respuesta trae {s, i, d}; los eventos traen {e, d}. Hay que
/// mandar «[]» cada 2,5 s o el servidor corta a los 15 s sin nada.
/// </summary>
public sealed class TradovateSocket : IDisposable
{
    private readonly string _url;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly string _name;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _life = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _loop;
    private TaskCompletionSource<bool>? _authorized;
    private int _id = 1;

    public TradovateSocket(string url, Func<CancellationToken, Task<string>> token, string name)
    {
        _url = url;
        _token = token;
        _name = name;
    }

    public bool IsConnected => _ws?.State == WebSocketState.Open && _authorized?.Task.IsCompletedSuccessfully == true;

    /// <summary>Evento del servidor (e: md, chart, props, clock, shutdown…).</summary>
    public event Action<string, JsonElement>? Event;

    /// <summary>Se conectó (true) o se cortó (false) con el motivo.</summary>
    public event Action<bool, string?>? StateChanged;

    public async Task EnsureAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            Drop("reconnecting", notify: false);
            var token = await _token(ct).ConfigureAwait(false);
            var ws = new ClientWebSocket();
            using (var c = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                c.CancelAfter(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(new Uri(_url), c.Token).ConfigureAwait(false);
            }

            _ws = ws;
            _authorized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loop = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            var loop = _loop.Token;
            _ = Task.Run(() => ReceiveAsync(ws, token, loop));
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(15));
            await using (wait.Token.Register(() => _authorized.TrySetException(new TimeoutException($"The {_name} timed out."))))
                await _authorized.Task.ConfigureAwait(false);
            _ = Task.Run(() => HeartbeatAsync(ws, loop));
            StateChanged?.Invoke(true, null);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task HeartbeatAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(2500, ct).ConfigureAwait(false);
                await SendRawAsync(ws, "[]", ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // el corte lo detecta la lectura
        }
    }

    private async Task SendRawAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveAsync(ClientWebSocket ws, string token, CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        string why = "connection lost";
        bool opened = false;
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    why = "Tradovate closed the connection";
                    break;
                }

                var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                if (text.Length == 0) continue;
                switch (text[0])
                {
                    case 'o':
                        if (!opened)
                        {
                            opened = true;
                            await SendRawAsync(ws, $"authorize\n0\n\n{token}", ct).ConfigureAwait(false);
                        }

                        break;
                    case 'h':
                        break;
                    case 'c':
                        why = "Tradovate ended the session" + (text.Length > 1 ? ": " + text[1..] : "");
                        goto done;
                    case 'a':
                        HandleArray(text[1..]);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            why = ex.Message;
        }

        done:
        if (ReferenceEquals(ws, _ws)) Drop(why, notify: true);
    }

    private void HandleArray(string json)
    {
        JsonElement arr;
        try
        {
            using var doc = JsonDocument.Parse(json);
            arr = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (arr.ValueKind != JsonValueKind.Array) return;
        foreach (var m in arr.EnumerateArray())
        {
            if (m.TryGetProperty("e", out var e) && e.ValueKind == JsonValueKind.String)
            {
                string kind = e.GetString() ?? "";
                var d = m.TryGetProperty("d", out var dd) ? dd : default;
                if (kind == "shutdown")
                {
                    string reason = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("reasonCode", out var rc) ? rc.GetString() ?? "" : "";
                    Drop(reason switch
                    {
                        "ConnectionQuotaReached" => "Tradovate disconnected due to its connection limit (is another session using the same username?).",
                        "Maintenance" => "Tradovate is under maintenance",
                        _ => "Tradovate ended session " + reason,
                    }, notify: true);
                    return;
                }

                Event?.Invoke(kind, d);
                continue;
            }

            if (!m.TryGetProperty("i", out var iEl) || !iEl.TryGetInt32(out int i)) continue;
            int status = m.TryGetProperty("s", out var s) && s.TryGetInt32(out var st) ? st : 0;
            var data = m.TryGetProperty("d", out var dv) ? dv : default;
            if (i == 0 && _authorized is { } auth && !auth.Task.IsCompleted)
            {
                if (status == 200) auth.TrySetResult(true);
                else auth.TrySetException(new TradovateException($"Tradovate rejected authorization for {_name}: {Text(data)}", status));
                continue;
            }

            if (_pending.TryRemove(i, out var tcs))
            {
                if (status == 200) tcs.TrySetResult(data);
                else tcs.TrySetException(new TradovateException($"Tradovate returned {status}: {Text(data)}", status));
            }
        }
    }

    private static string Text(JsonElement d) => d.ValueKind switch
    {
        // a veces el texto viene entre comillas («"Access is denied"»)
        JsonValueKind.String => (d.GetString() ?? "").Trim('"'),
        JsonValueKind.Object when d.TryGetProperty("errorText", out var e) => e.GetString() ?? "",
        JsonValueKind.Undefined => "no details",
        _ => d.ToString(),
    };

    /// <summary>Pedido por el socket (conecta si hace falta); devuelve «d».</summary>
    public async Task<JsonElement> RequestAsync(string endpoint, JsonNode? body, CancellationToken ct, string query = "")
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        var ws = _ws ?? throw new IOException($"No connection to {_name}");
        int id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendRawAsync(ws, $"{endpoint}\n{id}\n{query}\n{body?.ToJsonString() ?? ""}", ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var reg = timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The {_name} timed out ({endpoint}).");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void Drop(string why, bool notify)
    {
        bool was = _ws != null;
        try
        {
            _loop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _ws?.Abort();
            _ws?.Dispose();
        }
        catch
        {
            // cerrado
        }

        _ws = null;
        _authorized?.TrySetException(new IOException(why));
        foreach (var p in _pending)
            if (_pending.TryRemove(p.Key, out var tcs))
                tcs.TrySetException(new IOException($"Disconnected: {_name} ({why})"));
        if (was && notify) StateChanged?.Invoke(false, why);
    }

    public void Dispose()
    {
        _life.Cancel();
        Drop("closed", notify: false);
    }
}
