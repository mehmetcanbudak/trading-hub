using System.Net;
using System.Net.WebSockets;
using TradingHub.Feeds.Rithmic;

namespace TradingHub.Tests;

/// <summary>
/// Servidor de Rithmic de mentira (R|Protocol por WebSocket) para probar sin cuenta real: login, contrato del mes,
/// historia tick a tick en páginas chicas, velas de 1 minuto, cuentas, rutas, órdenes con ejecuciones, cancelaciones,
/// modificaciones y posiciones. Usa los mismos números de campo que el servidor real.
/// </summary>
internal sealed class FakeRithmicServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private WebSocket? _pnlSocket;
    private int _basket;

    public string Uri { get; }

    /// <summary>Operaciones de la historia: (segundos, microsegundos, precio, cantidad, compra).</summary>
    public List<(long Ssboe, int Us, double Price, int Size, bool Buy)> Ticks { get; } = new();

    /// <summary>Velas de 1 minuto: (marca de cierre, apertura, máximo, mínimo, cierre, volumen, volumen al ask).</summary>
    public List<(long Marker, double O, double H, double L, double C, int V, int Ask)> Bars { get; } = new();

    /// <summary>Filas por página de historia (el real corta en miles; acá pocas para probar el paginado).</summary>
    public int PageSize { get; set; } = 7;

    public long LastTradeSsboe { get; set; }
    public double MarketPrice { get; set; } = 5000;
    public int Position { get; private set; }
    public double AvgPrice { get; private set; }
    public List<string> Received { get; } = new();

    /// <summary>User tag of every new-order request, in arrival order (lock the list to read it).</summary>
    public List<string> Tags { get; } = new();

    /// <summary>Inicios de sesión por planta (1 cotizaciones, 2 órdenes, 3 historia, 4 PnL).</summary>
    public Dictionary<int, int> Logins { get; } = new();

    /// <summary>Sesiones cortadas porque entró otra con el mismo usuario en la misma planta (como hace Rithmic).</summary>
    public int Kicks { get; private set; }

    private readonly Dictionary<int, WebSocket> _sessions = new();
    private readonly Dictionary<WebSocket, HashSet<string>> _marketData = new();
    public Dictionary<string, (bool Buy, int Qty, double Price, int Type, string Tag)> Open { get; } = new();

    public FakeRithmicServer()
    {
        var rnd = new Random();
        for (int i = 0; ; i++)
        {
            int port = rnd.Next(30000, 45000);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Uri = $"ws://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException) when (i < 20)
            {
            }
        }

        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            var wsc = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
            _ = Task.Run(() => Serve(wsc.WebSocket));
        }
    }

    private static async Task Send(WebSocket ws, ProtoWriter w)
    {
        var bytes = w.ToArray();
        await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
    }

    private static ProtoWriter Msg(int template, ProtoMessage req) =>
        new ProtoWriter().Int(154467, template).String(132760, req.Strings(132760).FirstOrDefault() ?? "");

    private async Task Serve(WebSocket ws)
    {
        var buf = new byte[1 << 16];
        int infra = 0;
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, _cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                var m = ProtoMessage.Parse(ms.ToArray());
                int tpl = (int)m.Int(154467);
                lock (Received) Received.Add(tpl.ToString());
                switch (tpl)
                {
                    case 16:
                        await Send(ws, Msg(17, m).String(153628, "Rithmic Paper Trading").Int(153649, 0).String(132766, "0"));
                        break;
                    case 10:
                    {
                        infra = (int)m.Int(153621);
                        bool ok = m.String(131003) == "demo" && m.String(130004) == "demo";
                        WebSocket? kicked = null;
                        if (ok)
                        {
                            // una sola sesión por usuario en cada planta: la nueva corta la anterior
                            lock (_gate)
                            {
                                Logins[infra] = Logins.GetValueOrDefault(infra) + 1;
                                if (_sessions.TryGetValue(infra, out var old) && !ReferenceEquals(old, ws) && old.State == WebSocketState.Open)
                                {
                                    kicked = old;
                                    Kicks++;
                                }

                                _sessions[infra] = ws;
                            }
                        }

                        if (kicked != null)
                        {
                            try
                            {
                                await Send(kicked, new ProtoWriter().Int(154467, 77).String(132766, "0"));
                            }
                            catch
                            {
                            }

                            kicked.Abort();
                        }

                        if (infra == 4 && ok) lock (_gate) _pnlSocket = ws;
                        await Send(ws, ok
                            ? Msg(11, m).String(132766, "0").String(154013, "FCM").String(154014, "IB").Int(153633, 60)
                            : Msg(11, m).String(132766, "13").String(132766, "permission denied"));
                        break;
                    }
                    case 18:
                        await Send(ws, Msg(19, m).String(132766, "0"));
                        break;
                    case 12:
                        return;
                    case 113:
                        // contrato del mes: la raíz pedida + Z6 (ES → ESZ6, MES → MESZ6)
                        await Send(ws, Msg(114, m).String(132766, "0").String(157095, (m.String(110100) ?? "ES") + "Z6").String(157096, m.String(110101) ?? "CME"));
                        break;
                    case 100:
                    {
                        string sym = m.String(110100) ?? "ESZ6", exch = m.String(110101) ?? "CME";
                        bool subscribe = m.Int(100000) != 2;
                        lock (_gate)
                        {
                            if (!_marketData.TryGetValue(ws, out var set)) _marketData[ws] = set = new HashSet<string>();
                            if (subscribe) set.Add(sym);
                            else set.Remove(sym);
                        }

                        await Send(ws, Msg(101, m).String(132766, "0"));
                        if (subscribe)
                            await Send(ws, new ProtoWriter().Int(154467, 150).String(110100, sym).String(110101, exch).Int(110121, 1)
                                .Double(100006, MarketPrice).Int(100178, 1).Int(150100, LastTradeSsboe).Int(150101, 0));
                        break;
                    }
                    case 206:
                        await TickReplay(ws, m);
                        break;
                    case 202:
                        await BarReplay(ws, m);
                        break;
                    case 300:
                        await Send(ws, Msg(301, m).String(132766, "0").String(154013, "FCM").String(154014, "IB").Int(154036, 3));
                        break;
                    case 302:
                        await Send(ws, Msg(303, m).String(132764, "0").String(154013, "FCM").String(154014, "IB").String(154008, "ACC1").String(154002, "Demo").String(154383, "USD"));
                        await Send(ws, Msg(303, m).String(132766, "0"));
                        break;
                    case 310:
                        await Send(ws, Msg(311, m).String(132764, "0").String(110101, "CME").String(112016, "simulator").String(131407, "UP").Int(154689, 1));
                        await Send(ws, Msg(311, m).String(132766, "0"));
                        break;
                    case 308:
                        await Send(ws, Msg(309, m).String(132766, "0"));
                        break;
                    case 320:
                        await Send(ws, Msg(321, m).String(132766, "0"));
                        break;
                    case 400:
                        await Send(ws, Msg(401, m).String(132766, "0"));
                        break;
                    case 402:
                        await Send(ws, Msg(403, m).String(132766, "0"));
                        await SendPnl(snapshot: true);
                        break;
                    case 312:
                        await NewOrder(ws, m);
                        break;
                    case 316:
                        await Cancel(ws, m);
                        break;
                    case 314:
                        await Modify(ws, m);
                        break;
                }
            }
        }
        catch
        {
            // conexión cerrada
        }
    }

    /// <summary>Pedidos de historia tick a tick: (contrato, desde, hasta) en segundos.</summary>
    public List<(string Symbol, long Start, long End)> TickRequests { get; } = new();

    /// <summary>Si se pone, solo hay historia de ese contrato (como Rithmic, que no guarda los vencidos).</summary>
    public string? OnlySymbol { get; set; }

    private async Task TickReplay(WebSocket ws, ProtoMessage m)
    {
        long start = m.Int(153002), end = m.Int(153003);
        string symbol = m.String(110100) ?? "";
        lock (TickRequests) TickRequests.Add((symbol, start, end));
        var rows = OnlySymbol != null && symbol != OnlySymbol
            ? new List<(long Ssboe, int Us, double Price, int Size, bool Buy)>()
            : Ticks.Where(t => t.Ssboe >= start && t.Ssboe <= end).OrderBy(t => t.Ssboe).ThenBy(t => t.Us).Take(PageSize).ToList();
        foreach (var t in rows)
        {
            await Send(ws, Msg(207, m).String(132764, "0").String(110100, "ESZ6").String(110101, "CME")
                .Double(100021, t.Price).Double(100019, t.Price).Double(100012, t.Price).Double(100013, t.Price)
                .Int(119205, t.Size).Int(t.Buy ? 119214 : 119213, t.Size).Int(119202, t.Ssboe).Int(119203, t.Us));
        }

        await Send(ws, Msg(207, m).String(132766, "0"));
    }

    private async Task BarReplay(WebSocket ws, ProtoMessage m)
    {
        long start = m.Int(153002), end = m.Int(153003);
        var rows = Bars.Where(b => b.Marker >= start && b.Marker <= end).OrderBy(b => b.Marker).Take(PageSize).ToList();
        foreach (var b in rows)
        {
            await Send(ws, Msg(203, m).String(132764, "0").Int(119100, b.Marker).Double(100019, b.O).Double(100012, b.H).Double(100013, b.L).Double(100021, b.C)
                .Int(119110, b.V).Int(119118, b.Ask).Int(119117, b.V - b.Ask));
        }

        await Send(ws, Msg(203, m).String(132766, "0"));
    }

    private ProtoWriter Notify(int template, int notify, string basket, (bool Buy, int Qty, double Price, int Type, string Tag) o) =>
        new ProtoWriter().Int(154467, template).Int(153625, notify).String(110300, basket).String(154119, o.Tag)
            .String(154008, "ACC1").String(110100, "ESZ6").String(110101, "CME").Int(112003, o.Buy ? 1 : 2)
            .Int(112004, o.Qty).Double(110306, o.Price).Int(112008, o.Type);

    private async Task NewOrder(WebSocket ws, ProtoMessage m)
    {
        string basket = "B" + Interlocked.Increment(ref _basket);
        var o = (Buy: m.Int(112003) == 1, Qty: (int)m.Int(112004), Price: m.Double(110306), Type: (int)m.Int(112008), Tag: m.String(154119) ?? "");
        lock (Tags) Tags.Add(o.Tag);
        if (m.String(112016) != "simulator")
        {
            await Send(ws, Msg(313, m).String(132766, "1").String(132766, "invalid trade route"));
            return;
        }

        await Send(ws, Msg(313, m).String(132766, "0").String(110300, basket));
        await Send(ws, Notify(351, 13, basket, o).Int(154112, o.Qty).Int(154111, 0));
        if (o.Type == 2)
        {
            // mercado: se llena al toque
            double px = MarketPrice;
            await Send(ws, Notify(352, 5, basket, o).Double(110307, px).Int(110308, o.Qty).Int(154111, o.Qty).Int(154112, 0).Double(110322, px)
                .Int(150100, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Int(150101, 250000));
            await Send(ws, Notify(351, 15, basket, o).String(149273, "Fill").Int(154111, o.Qty).Int(154112, 0));
            ApplyFill(o.Buy, o.Qty, px);
            await SendPnl(snapshot: false);
        }
        else
        {
            lock (_gate) Open[basket] = o;
        }
    }

    private async Task Cancel(WebSocket ws, ProtoMessage m)
    {
        string basket = m.String(110300) ?? "";
        (bool, int, double, int, string) o;
        lock (_gate)
        {
            if (!Open.Remove(basket, out o))
            {
                _ = Send(ws, Msg(317, m).String(132766, "5").String(132766, "order not found"));
                return;
            }
        }

        await Send(ws, Msg(317, m).String(132766, "0"));
        await Send(ws, Notify(352, 3, basket, o).Int(110318, o.Item2));
        await Send(ws, Notify(351, 15, basket, o).String(149273, "Cancel"));
    }

    private async Task Modify(WebSocket ws, ProtoMessage m)
    {
        string basket = m.String(110300) ?? "";
        (bool Buy, int Qty, double Price, int Type, string Tag) o;
        lock (_gate)
        {
            if (!Open.TryGetValue(basket, out o)) return;
            o = (o.Buy, (int)m.Int(112004), m.Has(110306) ? m.Double(110306) : m.Double(149247), o.Type, o.Tag);
            Open[basket] = o;
        }

        await Send(ws, Msg(315, m).String(132766, "0"));
        await Send(ws, Notify(351, 14, basket, o).Int(154112, o.Qty));
    }

    /// <summary>Resultado cerrado del día (en dólares, ES = 50 por punto).</summary>
    public double ClosedPnl { get; private set; }

    public double StartBalance { get; set; } = 50_000;

    private void ApplyFill(bool buy, int qty, double px)
    {
        int signed = buy ? qty : -qty;
        if (Position != 0 && Math.Sign(Position) != Math.Sign(signed))
            ClosedPnl += (px - AvgPrice) * Math.Min(qty, Math.Abs(Position)) * Math.Sign(Position) * 50;
        int next = Position + signed;
        if (Position == 0 || Math.Sign(Position) == Math.Sign(signed)) AvgPrice = (AvgPrice * Math.Abs(Position) + px * qty) / Math.Abs(next == 0 ? 1 : next);
        else if (next != 0 && Math.Sign(next) != Math.Sign(Position)) AvgPrice = px;
        Position = next;
        if (Position == 0) AvgPrice = 0;
    }

    private async Task SendPnl(bool snapshot)
    {
        WebSocket? ws;
        lock (_gate) ws = _pnlSocket;
        if (ws == null) return;
        double open = (MarketPrice - AvgPrice) * Position * 50;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        await Send(ws, new ProtoWriter().Int(154467, 450).Int(110121, snapshot ? 1 : 0).String(154008, "ACC1").String(110100, "ESZ6").String(110101, "CME")
            .Int(156967, Position).Double(154434, AvgPrice).String(156961, open.ToString(inv)).Double(157955, ClosedPnl));
        // saldo de la cuenta: como el real, unos campos en texto y otros en double
        await Send(ws, new ProtoWriter().Int(154467, 451).Int(110121, snapshot ? 1 : 0).String(154008, "ACC1")
            .String(156970, (StartBalance + ClosedPnl).ToString("0.00", inv)).String(156971, (StartBalance + ClosedPnl).ToString("0.00", inv))
            .String(157015, (StartBalance + ClosedPnl - Math.Abs(Position) * 500).ToString("0.00", inv)).String(156961, open.ToString("0.00", inv))
            .Double(157955, ClosedPnl).Double(157956, ClosedPnl + open));
    }

    /// <summary>Manda una operación en vivo a las sesiones de cotizaciones suscriptas a ese contrato.</summary>
    public async Task PushTrade(string symbol, double price, int size, bool buy)
    {
        WebSocket[] targets;
        lock (_gate) targets = _marketData.Where(kv => kv.Key.State == WebSocketState.Open && kv.Value.Contains(symbol)).Select(kv => kv.Key).ToArray();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var ws in targets)
        {
            try
            {
                await Send(ws, new ProtoWriter().Int(154467, 150).String(110100, symbol).String(110101, "CME").Double(100006, price).Int(100178, size)
                    .Int(112003, buy ? 1 : 2).Int(150100, now).Int(150101, Environment.TickCount % 1_000_000));
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Reports a working order that this Trading Hub process did not send (another session's, for example) on the order
    /// plant, as Rithmic reports the account's orders. It can then be modified or canceled by basket id.
    /// </summary>
    public async Task PushOrder(string basket, string tag, bool buy, int qty, double price, int type = 1)
    {
        var o = (Buy: buy, Qty: qty, Price: price, Type: type, Tag: tag);
        WebSocket? ws;
        lock (_gate)
        {
            Open[basket] = o;
            _sessions.TryGetValue(2, out ws);
        }

        if (ws is { State: WebSocketState.Open }) await Send(ws, Notify(351, 13, basket, o).Int(154112, qty).Int(154111, 0));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }
    }
}
