using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingHub.Feeds.CTrader;

/// <summary>Conexión a la Open API de cTrader (IC Markets, Pepperstone, FxPro, Fusion… con la misma API).</summary>
public sealed class CTraderSettings
{
    /// <summary>Cuenta real (live.ctraderapi.com); si no, demo.</summary>
    public bool Live { get; set; }

    /// <summary>Aplicación registrada en openapi.ctrader.com (gratis).</summary>
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Token de acceso del usuario (OAuth o el «Playground» de la aplicación).</summary>
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    /// <summary>Número de cuenta de cTrader (el login que se ve en cTrader); 0 = la primera del entorno.</summary>
    public long TraderLogin { get; set; }

    /// <summary>Servidor alternativo (pruebas): ws://127.0.0.1:puerto</summary>
    public string? Url { get; set; }
}

/// <summary>Un símbolo de la cuenta de cTrader.</summary>
public sealed record CTraderSymbol(long Id, string Name, string Description, int Digits, decimal LotSize, decimal StepVolume, decimal MinVolume);

/// <summary>
/// Una sola conexión JSON (puerto 5036) con la Open API de cTrader para todos los gráficos y descargas: autentica la
/// aplicación y la cuenta, mantiene el latido cada 10 s, correlaciona pedidos y respuestas por clientMsgId y reparte
/// los precios en vivo (ProtoOASpotEvent) a cada suscripción. Se reconecta sola y vuelve a suscribir lo que estaba.
/// </summary>
public sealed class CTraderSession : IDisposable
{
    // tipos de mensaje (ProtoOAPayloadType / ProtoPayloadType)
    public const int ErrorRes = 50, Heartbeat = 51;
    public const int AppAuthReq = 2100, AppAuthRes = 2101, AccountAuthReq = 2102, AccountAuthRes = 2103;
    public const int SymbolsListReq = 2114, SymbolsListRes = 2115, SymbolByIdReq = 2116, SymbolByIdRes = 2117;
    public const int SubscribeSpotsReq = 2127, SubscribeSpotsRes = 2128, UnsubscribeSpotsReq = 2129, UnsubscribeSpotsRes = 2130, SpotEvent = 2131;
    public const int GetTrendbarsReq = 2137, GetTrendbarsRes = 2138, OaErrorRes = 2142, GetTickDataReq = 2145, GetTickDataRes = 2146;
    public const int TokenInvalidatedEvent = 2147, ClientDisconnectEvent = 2148, AccountsByTokenReq = 2149, AccountsByTokenRes = 2150;
    public const int RefreshTokenReq = 2173, RefreshTokenRes = 2174;

    private readonly Func<CTraderSettings> _settings;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<long, int> _spots = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _loopCts;
    private long _msgId;
    private DateTime _lastHistory = DateTime.MinValue;
    private readonly CancellationTokenSource _life = new();

    public CTraderSession(Func<CTraderSettings> settings) => _settings = settings;

    public long AccountId { get; private set; }
    public string AccountTitle { get; private set; } = "";
    public bool IsConnected => _ws?.State == WebSocketState.Open && AccountId != 0;
    public IReadOnlyList<CTraderSymbol> Symbols { get; private set; } = Array.Empty<CTraderSymbol>();

    /// <summary>Token renovado por la Open API (para guardarlo en el Llavero).</summary>
    public event Action<string, string>? TokensRefreshed;

    /// <summary>Precio en vivo: símbolo, bid y ask (en 1/100000), hora (ms Unix).</summary>
    public event Action<long, long?, long?, long?>? Spot;

    /// <summary>Se cortó o se volvió a conectar (para el estado de los gráficos).</summary>
    public event Action<bool, string?>? ConnectionChanged;

    private string Url
    {
        get
        {
            var s = _settings();
            return s.Url ?? (s.Live ? "wss://live.ctraderapi.com:5036" : "wss://demo.ctraderapi.com:5036");
        }
    }

    // ===================================================================== conexión

    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var s = _settings();
        if (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrWhiteSpace(s.ClientSecret))
            throw new InvalidOperationException("cTrader application Client ID and Secret are missing (register at openapi.ctrader.com).");
        if (string.IsNullOrWhiteSpace(s.AccessToken))
            throw new InvalidOperationException("cTrader access token is missing: click Connect with cTrader or paste your application's Playground token.");
        Drop("reconnecting");
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(Url), connectCts.Token).ConfigureAwait(false);
        }

        _ws = ws;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var loop = _loopCts.Token;
        _ = Task.Run(() => ReceiveLoopAsync(ws, loop));
        _ = Task.Run(() => HeartbeatLoopAsync(ws, loop));

        await RawRequestAsync(AppAuthReq, new JsonObject { ["clientId"] = s.ClientId.Trim(), ["clientSecret"] = s.ClientSecret.Trim() }, ct).ConfigureAwait(false);
        var accounts = await RawRequestAsync(AccountsByTokenReq, new JsonObject { ["accessToken"] = s.AccessToken.Trim() }, ct).ConfigureAwait(false);
        var list = accounts.TryGetProperty("ctidTraderAccount", out var arr) ? arr.EnumerateArray().ToList() : new List<JsonElement>();
        var match = list.Where(a => (a.TryGetProperty("isLive", out var il) && il.ValueKind == JsonValueKind.True) == s.Live).ToList();
        if (match.Count == 0)
            throw new InvalidOperationException(list.Count == 0
                ? "The token has no authorized cTrader accounts."
                : $"The token has {list.Count} account(s), but none in {(s.Live ? "real" : "demo")}: change the connection environment.");
        var chosen = s.TraderLogin > 0 ? match.FirstOrDefault(a => Long(a, "traderLogin") == s.TraderLogin) : match[0];
        if (chosen.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Account not found: {s.TraderLogin} in the token. Available accounts {(s.Live ? "real" : "demo")}: {string.Join(", ", match.Select(a => Long(a, "traderLogin")))}.");
        AccountId = Long(chosen, "ctidTraderAccountId");
        AccountTitle = $"{(chosen.TryGetProperty("brokerTitleShort", out var bt) ? bt.GetString() : "cTrader")} {Long(chosen, "traderLogin")}";
        await RawRequestAsync(AccountAuthReq, new JsonObject { ["ctidTraderAccountId"] = AccountId, ["accessToken"] = s.AccessToken.Trim() }, ct).ConfigureAwait(false);
        await LoadSymbolsAsync(ct).ConfigureAwait(false);

        // lo que estaba suscripto antes del corte
        var ids = _spots.Keys.ToList();
        if (ids.Count > 0) await SendSpotsAsync(SubscribeSpotsReq, ids, ct).ConfigureAwait(false);
        ConnectionChanged?.Invoke(true, null);
    }

    private async Task LoadSymbolsAsync(CancellationToken ct)
    {
        var res = await RequestAsync(SymbolsListReq, new JsonObject { ["ctidTraderAccountId"] = AccountId }, ct).ConfigureAwait(false);
        var light = res.TryGetProperty("symbol", out var arr) ? arr.EnumerateArray().Where(x => !x.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False).ToList() : new();
        var names = light.ToDictionary(x => Long(x, "symbolId"), x => (Name: Str(x, "symbolName"), Desc: Str(x, "description")));
        var symbols = new List<CTraderSymbol>();
        // detalles (dígitos, tamaño de lote, paso de volumen) de a 200
        foreach (var chunk in names.Keys.Chunk(200))
        {
            var ids = new JsonArray(chunk.Select(id => (JsonNode)id).ToArray());
            var det = await RequestAsync(SymbolByIdReq, new JsonObject { ["ctidTraderAccountId"] = AccountId, ["symbolId"] = ids }, ct).ConfigureAwait(false);
            if (!det.TryGetProperty("symbol", out var syms)) continue;
            foreach (var x in syms.EnumerateArray())
            {
                long id = Long(x, "symbolId");
                if (!names.TryGetValue(id, out var n)) continue;
                symbols.Add(new CTraderSymbol(id, n.Name, n.Desc, (int)Long(x, "digits", 5), Long(x, "lotSize", 100) / 100m,
                    Math.Max(0.01m, Long(x, "stepVolume", 100) / 100m), Long(x, "minVolume", 100) / 100m));
            }
        }

        Symbols = symbols;
    }

    private void Drop(string why)
    {
        bool was = _ws != null;
        try
        {
            _loopCts?.Cancel();
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
            // ya cerrado
        }

        _ws = null;
        AccountId = 0;
        foreach (var p in _pending)
            if (_pending.TryRemove(p.Key, out var tcs))
                tcs.TrySetException(new IOException("cTrader connection lost (" + why + ")"));
        if (was) ConnectionChanged?.Invoke(false, why);
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                await SendAsync(ws, new JsonObject { ["payloadType"] = Heartbeat, ["payload"] = new JsonObject() }, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // el corte lo detecta la lectura
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        string why = "connection lost";
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
                    why = "cTrader closed the connection";
                    break;
                }

                Dispatch(ms.ToArray());
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

        if (ReferenceEquals(ws, _ws)) Drop(why);
    }

    private void Dispatch(byte[] data)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(data);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        int type = (int)Long(root, "payloadType");
        var payload = root.TryGetProperty("payload", out var p) ? p : default;
        string id = root.TryGetProperty("clientMsgId", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() ?? "" : "";
        switch (type)
        {
            case Heartbeat:
                return;
            case SpotEvent:
                long symbol = Long(payload, "symbolId");
                long? bid = Has(payload, "bid") ? Long(payload, "bid") : null;
                long? ask = Has(payload, "ask") ? Long(payload, "ask") : null;
                long? ts = Has(payload, "timestamp") ? Long(payload, "timestamp") : null;
                Spot?.Invoke(symbol, bid, ask, ts);
                return;
            case TokenInvalidatedEvent:
                Drop("cTrader invalidated the token: authorize the account again");
                return;
            case ClientDisconnectEvent:
                Drop("cTrader ended the session: " + Str(payload, "reason"));
                return;
        }

        if (id.Length > 0 && _pending.TryRemove(id, out var tcs))
        {
            if (type is ErrorRes or OaErrorRes)
                tcs.TrySetException(new CTraderException(Str(payload, "errorCode"), Str(payload, "description")));
            else tcs.TrySetResult(payload.ValueKind == JsonValueKind.Undefined ? root : payload);
        }
    }

    private async Task SendAsync(ClientWebSocket ws, JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Pedido sin exigir la cuenta autenticada (para la autenticación misma).</summary>
    private async Task<JsonElement> RawRequestAsync(int type, JsonObject payload, CancellationToken ct)
    {
        var ws = _ws ?? throw new IOException("No cTrader connection");
        string id = "v" + Interlocked.Increment(ref _msgId).ToString(CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(ws, new JsonObject { ["clientMsgId"] = id, ["payloadType"] = type, ["payload"] = payload }, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var reg = timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("cTrader timed out");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Pedido con la cuenta ya autenticada (conecta si hace falta).</summary>
    public async Task<JsonElement> RequestAsync(int type, JsonObject payload, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RawRequestAsync(type, payload, ct).ConfigureAwait(false);
    }

    /// <summary>Pedido de historia: de a uno y con una pausa entre pedidos (la Open API limita los de historia).</summary>
    public async Task<JsonElement> HistoryRequestAsync(int type, JsonObject payload, CancellationToken ct)
    {
        await _historyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromMilliseconds(260) - (DateTime.UtcNow - _lastHistory);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await RequestAsync(type, payload, ct).ConfigureAwait(false);
                }
                catch (CTraderException ex) when (ex.Code is "REQUEST_FREQUENCY_EXCEEDED" or "BLOCKED_PAYLOAD_TYPE" && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct).ConfigureAwait(false);
                }
                finally
                {
                    _lastHistory = DateTime.UtcNow;
                }
            }
        }
        finally
        {
            _historyGate.Release();
        }
    }

    // ===================================================================== precios en vivo

    public async Task SubscribeAsync(long symbolId, CancellationToken ct)
    {
        int n = _spots.AddOrUpdate(symbolId, 1, (_, v) => v + 1);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (n == 1) await SendSpotsAsync(SubscribeSpotsReq, new[] { symbolId }, ct).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(long symbolId)
    {
        int n = _spots.AddOrUpdate(symbolId, 0, (_, v) => Math.Max(0, v - 1));
        if (n > 0) return;
        _spots.TryRemove(symbolId, out _);
        if (!IsConnected) return;
        try
        {
            await SendSpotsAsync(UnsubscribeSpotsReq, new[] { symbolId }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // ya no importa
        }
    }

    private async Task SendSpotsAsync(int type, IEnumerable<long> ids, CancellationToken ct)
    {
        var payload = new JsonObject { ["ctidTraderAccountId"] = AccountId, ["symbolId"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()) };
        try
        {
            await RawRequestAsync(type, payload, ct).ConfigureAwait(false);
        }
        catch (CTraderException ex) when (ex.Code is "ALREADY_SUBSCRIBED" or "NOT_SUBSCRIBED")
        {
        }
    }

    /// <summary>Renueva el token con el refresh token (vale 30 días); avisa para guardarlo.</summary>
    public async Task<bool> RefreshTokenAsync(CancellationToken ct)
    {
        var s = _settings();
        if (string.IsNullOrWhiteSpace(s.RefreshToken) || _ws == null) return false;
        var res = await RawRequestAsync(RefreshTokenReq, new JsonObject { ["refreshToken"] = s.RefreshToken }, ct).ConfigureAwait(false);
        string access = Str(res, "accessToken"), refresh = Str(res, "refreshToken");
        if (access.Length == 0) return false;
        TokensRefreshed?.Invoke(access, refresh.Length > 0 ? refresh : s.RefreshToken);
        return true;
    }

    // ===================================================================== ayudas JSON

    internal static bool Has(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    internal static long Long(JsonElement e, string key, long def = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : def,
            JsonValueKind.True => 1,
            _ => def,
        };
    }

    internal static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    public void Dispose()
    {
        _life.Cancel();
        Drop("closed");
    }
}

/// <summary>Error de la Open API de cTrader (código y descripción).</summary>
public sealed class CTraderException(string code, string description) : Exception(string.IsNullOrEmpty(description) ? $"cTrader: {code}" : $"cTrader: {description} ({code})")
{
    public string Code { get; } = code;
}
