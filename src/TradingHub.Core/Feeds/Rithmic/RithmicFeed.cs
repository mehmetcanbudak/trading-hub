using System.Net.WebSockets;
using TradingHub.Market;

namespace TradingHub.Feeds.Rithmic;

/// <summary>
/// Números de campo y plantillas de R|Protocol (Rithmic). Verificados contra el servidor:
/// template_id, user_msg, rp_code y system_name. El resto sigue la especificación pública de la API.
/// </summary>
internal static class Rp
{
    public const int TemplateId = 154467;
    public const int TemplateVersion = 153634;
    public const int UserMsg = 132760;
    public const int RpCode = 132766;
    public const int SystemName = 153628;

    // login
    public const int User = 131003;
    public const int Password = 130004;
    public const int AppName = 130002;
    public const int AppVersion = 131803;
    public const int InfraType = 153621;
    public const int HeartbeatInterval = 153633;
    public const int UniqueUserId = 153428;
    public const int FcmId = 154013;
    public const int IbId = 154014;
    public const int AggregatedQuotes = 153644;
    public const int HasAggregatedQuotes = 153649;

    // datos de mercado
    public const int Symbol = 110100;
    public const int Exchange = 110101;
    public const int Request = 100000;
    public const int UpdateBits = 154211;
    public const int TradePrice = 100006;
    public const int TradeSize = 100178;
    public const int Aggressor = 112003;
    public const int Ssboe = 150100;
    public const int Usecs = 150101;
    public const int IsSnapshot = 110121;
    public const int BidPrice = 100022;
    public const int BidSize = 100030;
    public const int AskPrice = 100025;
    public const int AskSize = 100031;
    public const int TradingSymbol = 157095;
    public const int Description = 110114;
    public const int NeedUpdates = 154352;

    // plantillas
    public const int RequestSystemInfo = 16;
    public const int ResponseSystemInfo = 17;
    public const int RequestLogin = 10;
    public const int ResponseLogin = 11;
    public const int RequestLogout = 12;
    public const int RequestHeartbeat = 18;
    public const int RequestMarketDataUpdate = 100;
    public const int ResponseMarketDataUpdate = 101;
    public const int RequestFrontMonth = 113;
    public const int ResponseFrontMonth = 114;
    public const int LastTrade = 150;
    public const int BestBidOffer = 151;
}

/// <summary>
/// Servidores (gateways) de R|Protocol por región, como los muestra ATAS. Cada uno ofrece sus propios
/// sistemas (Rithmic Paper Trading, Rithmic 01, prop firms…): la lista se pide a cada servidor.
/// </summary>
public static class RithmicGateways
{
    public const string TestUri = "wss://rituz00100.rithmic.com:443";
    public const string ChicagoUri = "wss://rprotocol.rithmic.com:443";

    public static readonly (string Name, string Uri)[] Known =
    {
        ("Chicago Area", ChicagoUri),
        ("Europe", "wss://rprotocol-ie.rithmic.com:443"),
        ("Frankfurt", "wss://rprotocol-de.rithmic.com:443"),
        ("Hong Kong", "wss://rprotocol-hk.rithmic.com:443"),
        ("Sydney", "wss://rprotocol-au.rithmic.com:443"),
        ("Singapore", "wss://rprotocol-sg.rithmic.com:443"),
        ("Tokyo", "wss://rprotocol-jp.rithmic.com:443"),
        ("Seoul", "wss://rprotocol-kr.rithmic.com:443"),
        ("Sao Paulo", "wss://rprotocol-br.rithmic.com:443"),
        ("Mumbai", "wss://rprotocol-in.rithmic.com:443"),
        ("Cape Town", "wss://rprotocol-za.rithmic.com:443"),
        ("NYC Area", "wss://rprotocol-nyc.rithmic.com:443"),
        ("Colo 75", "wss://rprotocol-colo75.rithmic.com:443"),
        ("Test", TestUri),
    };

    public static string NameOf(string uri) => Known.FirstOrDefault(k => k.Uri == uri).Name ?? uri;

    /// <summary>Orden de los sistemas en la lista: primero los de Rithmic, después las prop firms.</summary>
    public static int SystemRank(string system) => system switch
    {
        "Rithmic Paper Trading" => 0,
        "Rithmic 01" => 1,
        "Rithmic 04 Colo" => 2,
        "Rithmic Test" => 3,
        _ => 10,
    };
}

/// <summary>Un sistema en un servidor ("Rithmic Paper Trading. Chicago Area"), con lo que tardó en responder.</summary>
public sealed record RithmicServer(string System, string Gateway, string Uri, int LatencyMs, bool Aggregated = false)
{
    public string Display => $"{System}. {Gateway}";
}

/// <summary>Arma la lista completa sistema × servidor preguntándole a cada servidor (no hace falta usuario).</summary>
public static class RithmicDirectory
{
    public static async Task<List<RithmicServer>> DiscoverAsync(CancellationToken ct)
    {
        var tasks = RithmicGateways.Known.Select(async g =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var systems = await RithmicClient.ListSystemsInfoAsync(g.Uri, cts.Token).ConfigureAwait(false);
                int ms = (int)sw.ElapsedMilliseconds;
                return systems.Select(s => new RithmicServer(s.Name, g.Name, g.Uri, ms, s.Aggregated)).ToList();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return new List<RithmicServer>(); // servidor inalcanzable desde acá (p. ej. Colo 75)
            }
        });
        var all = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(x => x).ToList();
        var order = RithmicGateways.Known.Select((k, i) => (k.Uri, i)).ToDictionary(x => x.Uri, x => x.i);
        return all.OrderBy(s => RithmicGateways.SystemRank(s.System)).ThenBy(s => s.System, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => order.GetValueOrDefault(s.Uri, 99)).ToList();
    }
}

/// <summary>Resultado de una operación contra Rithmic, con el código del servidor.</summary>
public sealed record RithmicResult(bool Ok, string Message, IReadOnlyList<string> Codes);

/// <summary>Cliente de R|Protocol: una conexión WebSocket con mensajes protobuf.</summary>
public sealed class RithmicClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private CancellationTokenSource? _heartbeatCts;

    public string? LastError { get; private set; }

    public static async Task<IReadOnlyList<string>> ListSystemsAsync(string uri, CancellationToken ct) =>
        (await ListSystemsInfoAsync(uri, ct).ConfigureAwait(false)).Select(s => s.Name).ToList();

    /// <summary>Sistemas del servidor y si cada uno ofrece cotizaciones agregadas.</summary>
    public static async Task<IReadOnlyList<(string Name, bool Aggregated)>> ListSystemsInfoAsync(string uri, CancellationToken ct)
    {
        await using var c = new RithmicClient();
        await c.ConnectAsync(uri, ct).ConfigureAwait(false);
        await c.SendAsync(new ProtoWriter().Int(Rp.TemplateId, Rp.RequestSystemInfo).String(Rp.UserMsg, "tradinghub"), ct).ConfigureAwait(false);
        var m = await c.ReceiveAsync(ct).ConfigureAwait(false);
        if (m == null) return Array.Empty<(string, bool)>();
        var names = m.Strings(Rp.SystemName).ToList();
        var flags = m.Ints(Rp.HasAggregatedQuotes).ToList();
        return names.Select((n, i) => (n, i < flags.Count && flags[i] != 0)).ToList();
    }

    /// <summary>
    /// Si conviene pedir cotizaciones agregadas: solo si el sistema las ofrece (si se piden en uno que no,
    /// Rithmic rechaza el login con «an error occurred while parsing data»).
    /// </summary>
    public static async Task<bool?> AggregatedQuotesFor(string uri, string system, bool wanted, CancellationToken ct)
    {
        if (!wanted) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var info = await ListSystemsInfoAsync(uri, cts.Token).ConfigureAwait(false);
            return info.Any(s => s.Name == system && s.Aggregated) ? true : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // ante la duda no se pide (el login funciona igual)
        }
    }

    public async Task ConnectAsync(string uri, CancellationToken ct, int timeoutMs = 15000)
    {
        // pings propios: si el servidor no contesta, la conexión está muerta y se reconecta sola
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 2000, 120000)));
        await _ws.ConnectAsync(new Uri(uri), t.Token).ConfigureAwait(false);
    }

    public async Task SendAsync(ProtoWriter w, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(w.ToArray(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    // un solo buffer por conexión (nunca hay dos lecturas a la vez): antes cada mensaje pedía 64 KB nuevos y bajar un
    // día tick a tick del ES (más de un millón de mensajes) generaba decenas de GB de basura
    private readonly byte[] _recv = new byte[1 << 16];
    private MemoryStream? _large;

    public async Task<ProtoMessage?> ReceiveAsync(CancellationToken ct)
    {
        var r = await _ws.ReceiveAsync(new ArraySegment<byte>(_recv), ct).ConfigureAwait(false);
        if (r.MessageType == WebSocketMessageType.Close) return null;
        if (r.EndOfMessage) return ProtoMessage.Parse(_recv.AsSpan(0, r.Count));

        // mensaje más grande que el buffer: se junta
        var ms = _large ??= new MemoryStream();
        ms.SetLength(0);
        ms.Write(_recv, 0, r.Count);
        do
        {
            r = await _ws.ReceiveAsync(new ArraySegment<byte>(_recv), ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(_recv, 0, r.Count);
        } while (!r.EndOfMessage);

        return ProtoMessage.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    /// <summary>Inicia sesión en una "planta" (1 = cotizaciones, 2 = órdenes, 3 = historia, 4 = PnL).</summary>
    public async Task<RithmicResult> LoginAsync(string user, string password, string system, string appName, string appVersion, int infraType, CancellationToken ct, bool? aggregatedQuotes = null)
    {
        var req = new ProtoWriter()
            .Int(Rp.TemplateId, Rp.RequestLogin)
            .String(Rp.TemplateVersion, "3.9")
            .String(Rp.UserMsg, "tradinghub")
            .String(Rp.User, user)
            .String(Rp.Password, password)
            .String(Rp.AppName, appName)
            .String(Rp.AppVersion, appVersion)
            .String(Rp.SystemName, system)
            .Int(Rp.InfraType, infraType);
        // cotizaciones agregadas (como la opción de ATAS): menos mensajes de libro, mismo precio
        if (aggregatedQuotes is { } agg) req.Bool(Rp.AggregatedQuotes, agg);
        await SendAsync(req, ct).ConfigureAwait(false);
        for (int i = 0; i < 20; i++)
        {
            var m = await ReceiveAsync(ct).ConfigureAwait(false);
            if (m == null) return new RithmicResult(false, "Server closed the connection", Array.Empty<string>());
            if (m.Int(Rp.TemplateId) != Rp.ResponseLogin) continue;
            var codes = m.Strings(Rp.RpCode).ToList();
            bool ok = codes.Count > 0 && codes[0] == "0";
            if (ok)
            {
                double hb = m.Int(Rp.HeartbeatInterval, 60);
                StartHeartbeat(TimeSpan.FromSeconds(Math.Max(5, hb - 5)));
                return new RithmicResult(true, $"Signed in (FCM {m.String(Rp.FcmId)}, IB {m.String(Rp.IbId)})", codes);
            }

            LastError = string.Join(" ", codes);
            string reason = string.Join(" — ", codes.Skip(1).DefaultIfEmpty(string.Join(" ", codes)));
            string help = codes.FirstOrDefault() switch
            {
                "13" => $"Rithmic rejected the user on «{system}» (permission denied). Check the system supplied by your broker and the password (Rithmic Paper Trading is case-sensitive). If correct, API access (R|Protocol) may need enabling by Rithmic or your broker.",
                "11" => $"Rithmic did not accept the login request ({reason}).",
                _ => "Rithmic rejected login: " + reason,
            };
            return new RithmicResult(false, help, codes);
        }

        return new RithmicResult(false, "No login response", Array.Empty<string>());
    }

    private void StartHeartbeat(TimeSpan every)
    {
        _heartbeatCts = new CancellationTokenSource();
        var ct = _heartbeatCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    await Task.Delay(every, ct).ConfigureAwait(false);
                    await SendAsync(new ProtoWriter().Int(Rp.TemplateId, Rp.RequestHeartbeat), ct).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // conexión cerrada: el que la usa se entera al leer
            }
        }, CancellationToken.None);
    }

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public async ValueTask DisposeAsync()
    {
        _heartbeatCts?.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await SendAsync(new ProtoWriter().Int(Rp.TemplateId, Rp.RequestLogout), CancellationToken.None).ConfigureAwait(false);
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // cierre de mejor esfuerzo
        }

        _ws.Dispose();
    }
}

/// <summary>Credenciales y servidor para conectarse a Rithmic.</summary>
public sealed class RithmicSettings
{
    public string Uri { get; set; } = RithmicGateways.TestUri;
    public string System { get; set; } = "Rithmic Test";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string AppName { get; set; } = "TradingHub";
    public string AppVersion { get; set; } = "0.2";

    /// <summary>Cotizaciones agregadas (opción de Rithmic, como en ATAS).</summary>
    public bool AggregatedQuotes { get; set; } = true;

    /// <summary>Tiempo de espera para conectar, en milisegundos.</summary>
    public int TimeoutMs { get; set; } = 10000;

    /// <summary>Contratos a ofrecer en el selector ("NQ:CME", "ES:CME", "GC:COMEX"...). Se resuelven al mes activo.</summary>
    public List<string> Products { get; set; } = new() { "ES:CME", "NQ:CME", "MES:CME", "MNQ:CME", "YM:CBOT", "RTY:CME", "CL:NYMEX", "GC:COMEX", "6E:CME", "ZN:CBOT" };
}

/// <summary>
/// Datos en vivo de Rithmic (planta de cotizaciones): operaciones y mejor bid/ask.
/// La historia de ticks llega con lo que Trading Hub va grabando en disco.
/// Requiere un usuario de Rithmic; en servidores de producción, Rithmic además exige que la
/// aplicación esté aprobada (prueba de conformidad), si no rechaza el login.
/// </summary>
public sealed partial class RithmicFeed : IMarketDataFeed
{
    public const string FeedId = "rithmic";
    private RithmicSettings _settings;
    private readonly RithmicTickerPlant _ticker;
    private readonly RithmicHistoryPlant _history;

    /// <summary>Tick y valor del punto de un futuro por su raíz (ES, NQ, CL…), o null si no se conoce.</summary>
    public static (decimal Tick, decimal PointValue, string Name)? SpecFor(string root) =>
        FuturesCatalog.Find(root) is { } p ? (p.Tick, p.PointValue, p.Name) : null;

    /// <summary>Valor del punto de un contrato por su símbolo completo (ESZ6 → ES → 50); 1 si no se conoce.</summary>
    public static decimal PointValueOfContract(string contract)
    {
        string root = System.Text.RegularExpressions.Regex.Replace(contract, "[FGHJKMNQUVXZ][0-9]{1,2}$", "");
        return SpecFor(root)?.PointValue ?? SpecFor(contract)?.PointValue ?? 1;
    }

    public RithmicFeed(RithmicSettings settings)
    {
        _settings = settings;
        _ticker = new RithmicTickerPlant(() => _settings, (inst, c) => _contracts[inst.Key] = c);
        _history = new RithmicHistoryPlant(() => _settings);
    }

    /// <summary>Cambió la configuración (servidor, usuario…): las mismas sesiones vuelven a entrar con los datos nuevos.</summary>
    public void Reconfigure(RithmicSettings settings)
    {
        _settings = settings;
        _contracts.Clear();
        _ticker.Reconnect();
        _history.Reset();
    }

    /// <summary>Prueba de conexión sin abrir otra sesión (otra cortaría la que usan los gráficos).</summary>
    public Task<(bool Ok, string Message)> TestAsync(CancellationToken ct) => _ticker.WaitConnectedAsync(TimeSpan.FromSeconds(20), ct);

    public bool IsConnected => _ticker.IsConnected;

    /// <summary>Veces que entró a cada planta (las pruebas verifican que sea una sola sesión por planta).</summary>
    public int TickerLogins => _ticker.Logins;

    public int HistoryLogins => _history.Logins;

    public string Id => FeedId;
    public string DisplayName => "Rithmic";
    public bool SupportsHistory => true;
    public bool IsLive => true;

    /// <summary>Primero los contratos que elegiste en la conexión y después todos los de CME Group (al mes activo).</summary>
    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = new List<Instrument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string root, string? exchange)
        {
            if (root.Length == 0 || !seen.Add(root)) return;
            var product = FuturesCatalog.Find(root);
            string exch = exchange ?? product?.Exchange ?? "CME";
            list.Add(new Instrument
            {
                Symbol = root,
                FeedId = FeedId,
                Exchange = exch,
                TickSize = product?.Tick ?? 0.25m,
                VolumeStep = 1,
                PointValue = product?.PointValue ?? 1m,
                Description = $"{product?.Name ?? root} · {exch} (active contract)",
            });
        }

        foreach (var p in _settings.Products)
        {
            var parts = p.Split(':');
            Add(parts[0].Trim().ToUpperInvariant(), parts.Length > 1 ? parts[1].Trim().ToUpperInvariant() : null);
        }

        foreach (var product in FuturesCatalog.All) Add(product.Root, product.Exchange);
        return Task.FromResult<IReadOnlyList<Instrument>>(list);
    }


    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        _ticker.Attach(instrument, sink);
        return new TickerSubscription(_ticker, instrument, sink);
    }

    /// <summary>Un gráfico suscripto a la sesión de cotizaciones compartida.</summary>
    private sealed class TickerSubscription(RithmicTickerPlant ticker, Instrument instrument, IMarketDataSink sink) : IDisposable, IReconnectable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) ticker.Detach(instrument, sink);
        }

        public void Reconnect() => ticker.Reconnect();
    }

    public void Dispose()
    {
        _ticker.Dispose();
        _history.Dispose();
    }
}
