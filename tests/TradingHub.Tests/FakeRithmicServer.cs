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
    private readonly Dictionary<string, int> _filled = new();
    private readonly Dictionary<string, (bool Buy, int Qty, double Price, int Type, string Tag)> _canceledOrders = new();
    private readonly Dictionary<string, (string Symbol, string Exchange)> _orderContracts = new();

    public TaskCompletionSource? OrderReplayRelease { get; set; }
    public TaskCompletionSource? PositionSnapshotRelease { get; set; }
    public TaskCompletionSource OrderReplayRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? PositionUpdateRelease { get; set; }
    public TaskCompletionSource PositionUpdateRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool LoseNewOrderResponse { get; set; }
    public string? NewOrderRejection { get; set; }
    public TaskCompletionSource<string> UnacknowledgedOrderAccepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? ModifyResponseRelease { get; set; }
    public TaskCompletionSource ModifyResponseSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool SuppressModifyNotification { get; set; }
    public bool LoseModifyResponse { get; set; }
    public bool ApplyLostModification { get; set; } = true;
    public string? ModifyRejection { get; set; }
    public bool RejectModifyByNotification { get; set; }
    private int _modifyRequests;
    public int ModifyRequests => Volatile.Read(ref _modifyRequests);
    public TaskCompletionSource? CancelNotificationRelease { get; set; }
    public TaskCompletionSource CancelResponseSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool FillMarketBeforeLostReply { get; set; }
    public bool ReplayTerminalOrders { get; set; }
    public int? ReportedPositionOverride { get; set; }
    private readonly Dictionary<string, (bool Buy, int Qty, double Price, int Type, string Tag)> _terminalOrders = new();
    public bool AllowConcurrentTradingSessions { get; set; }
    public TaskCompletionSource? NewOrderResponseRelease { get; set; }
    public TaskCompletionSource<string> NewOrderResponseRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? MarketFillRelease { get; set; }
    private readonly Dictionary<WebSocket, int> _tradingSessions = new();
    private readonly Dictionary<WebSocket, string> _loginSystems = new();
    private readonly List<WebSocket> _orderSessions = new();
    public HashSet<string> OrderReplayExclusions { get; } = new(StringComparer.Ordinal);

    public void SeedCompletedOrder(string basket, string tag, bool buy, int quantity, double price)
    {
        lock (_gate)
        {
            _terminalOrders[basket] = (buy, quantity, price, 1, tag);
            _filled[basket] = quantity;
            _orderContracts[basket] = ("ESZ6", "CME");
            ApplyFill(buy, quantity, price);
        }
    }

    public void SeedPosition(bool buy, int quantity, double price) => ApplyFill(buy, quantity, price);

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
                // Retained native evidence belongs to one fake venue, not to a reused listener port.
                Uri = $"ws://127.0.0.1:{port}/{Guid.NewGuid():N}";
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

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> SendLocks = new();

    private static async Task Send(WebSocket ws, ProtoWriter w)
    {
        var sendLock = SendLocks.GetValue(ws, static _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = w.ToArray();
            await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
        }
        finally { sendLock.Release(); }
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
                            // One session per user, plant and system: a new login replaces the older one on the same system only.
                            string system = m.String(153628) ?? "";
                            lock (_gate)
                            {
                                Logins[infra] = Logins.GetValueOrDefault(infra) + 1;
                                if (!AllowConcurrentTradingSessions && _sessions.TryGetValue(infra, out var old) && !ReferenceEquals(old, ws) &&
                                    old.State == WebSocketState.Open && _loginSystems.GetValueOrDefault(old) == system)
                                {
                                    kicked = old;
                                    Kicks++;
                                }

                                _sessions[infra] = ws;
                                _loginSystems[ws] = system;
                                if (infra is 2 or 4) _tradingSessions[ws] = infra;
                                if (infra == 2) _orderSessions.Add(ws);
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
                        OrderReplayRequested.TrySetResult();
                        if (OrderReplayRelease != null) await OrderReplayRelease.Task.WaitAsync(_cts.Token);
                        (string Basket, (bool Buy, int Qty, double Price, int Type, string Tag) Order, int Filled)[] replay;
                        lock (_gate) replay = Open.Where(kv => !OrderReplayExclusions.Contains(kv.Key))
                            .Select(kv => (kv.Key, kv.Value, _filled.GetValueOrDefault(kv.Key))).ToArray();
                        foreach (var (basket, order, filled) in replay)
                            await Send(ws, Notify(351, 13, basket, order).Int(110121, 1).Int(154112, order.Qty - filled).Int(154111, filled));
                        if (ReplayTerminalOrders)
                        {
                            (string Basket, (bool Buy, int Qty, double Price, int Type, string Tag) Order, int Filled)[] terminal;
                            lock (_gate) terminal = _terminalOrders.Select(kv => (kv.Key, kv.Value, _filled.GetValueOrDefault(kv.Key))).ToArray();
                            foreach (var (basket, order, filled) in terminal)
                                await Send(ws, Notify(351, 15, basket, order).String(149273, "Fill").Int(154111, filled).Int(154112, 0));
                        }
                        await Send(ws, Msg(321, m).String(132766, "0"));
                        break;
                    case 400:
                        await Send(ws, Msg(401, m).String(132766, "0"));
                        break;
                    case 402:
                        if (PositionSnapshotRelease != null) await PositionSnapshotRelease.Task.WaitAsync(_cts.Token);
                        await Send(ws, Msg(403, m).String(132766, "0"));
                        await SendPnl(snapshot: true, target: ws);
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

    private ProtoWriter Notify(int template, int notify, string basket, (bool Buy, int Qty, double Price, int Type, string Tag) o,
        bool includeQuantity = true)
    {
        (string Symbol, string Exchange) contract;
        lock (_gate) contract = _orderContracts.GetValueOrDefault(basket, ("ESZ6", "CME"));
        var message = new ProtoWriter().Int(154467, template).Int(153625, notify).String(110300, basket).String(154119, o.Tag)
            .String(154008, "ACC1").String(110100, contract.Symbol).String(110101, contract.Exchange).Int(112003, o.Buy ? 1 : 2)
            .Double(110306, o.Price).Double(149247, o.Price).Int(112008, o.Type);
        if (includeQuantity) message.Int(112004, o.Qty);
        return message;
    }

    private async Task NewOrder(WebSocket ws, ProtoMessage m)
    {
        string basket = "B" + Interlocked.Increment(ref _basket);
        var o = (Buy: m.Int(112003) == 1, Qty: (int)m.Int(112004), Price: m.Has(110306) ? m.Double(110306) : m.Double(149247), Type: (int)m.Int(112008), Tag: m.String(154119) ?? "");
        lock (_gate) _orderContracts[basket] = (m.String(110100) ?? "ESZ6", m.String(110101) ?? "CME");
        lock (Tags) Tags.Add(o.Tag);
        if (NewOrderRejection is { } rejection)
        {
            await Send(ws, Msg(313, m).String(132766, "1").String(132766, rejection));
            return;
        }
        if (m.String(112016) != "simulator")
        {
            await Send(ws, Msg(313, m).String(132766, "1").String(132766, "invalid trade route"));
            return;
        }
        if (LoseNewOrderResponse)
        {
            lock (_gate)
            {
                if (FillMarketBeforeLostReply && o.Type == 2)
                {
                    ApplyFill(o.Buy, o.Qty, MarketPrice);
                    _filled[basket] = o.Qty;
                    _terminalOrders[basket] = o;
                }
                else Open[basket] = o;
            }
            UnacknowledgedOrderAccepted.TrySetResult(basket);
            await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Fixture lost order response", _cts.Token);
            return;
        }

        if (NewOrderResponseRelease is { } responseRelease && NewOrderResponseRequested.TrySetResult(basket))
        {
            lock (_gate) Open[basket] = o;
            await responseRelease.Task.WaitAsync(_cts.Token);
        }
        await Send(ws, Msg(313, m).String(132766, "0").String(110300, basket));
        await Send(ws, Notify(351, 13, basket, o).Int(154112, o.Qty).Int(154111, 0));
        if (o.Type == 2)
        {
            if (MarketFillRelease is { } fillRelease) _ = FinishMarketAsync(ws, basket, o, fillRelease.Task);
            else await FinishMarketAsync(ws, basket, o, Task.CompletedTask);
        }
        else
        {
            lock (_gate) Open[basket] = o;
        }
    }

    private async Task FinishMarketAsync(WebSocket ws, string basket,
        (bool Buy, int Qty, double Price, int Type, string Tag) order, Task release)
    {
        await release.WaitAsync(_cts.Token);
        double price = MarketPrice;
        await Send(ws, Notify(352, 5, basket, order).Double(110307, price).Int(110308, order.Qty)
            .Int(154111, order.Qty).Int(154112, 0).Double(110322, price)
            .Int(150100, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Int(150101, 250000));
        await Send(ws, Notify(351, 15, basket, order).String(149273, "Fill").Int(154111, order.Qty).Int(154112, 0));
        lock (_gate)
        {
            Open.Remove(basket);
            ApplyFill(order.Buy, order.Qty, price);
            _filled[basket] = order.Qty;
            _terminalOrders[basket] = order;
        }
        await SendPnl(snapshot: false);
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
            _canceledOrders[basket] = o;
        }

        await Send(ws, Msg(317, m).String(132766, "0"));
        CancelResponseSent.TrySetResult();
        if (CancelNotificationRelease is { } release)
            _ = FinishCancellationAsync(ws, basket, o, release.Task);
        else await FinishCancellationAsync(ws, basket, o, Task.CompletedTask);
    }

    private async Task FinishCancellationAsync(WebSocket ws, string basket,
        (bool Buy, int Qty, double Price, int Type, string Tag) order, Task release)
    {
        await release.WaitAsync(_cts.Token);
        await Send(ws, Notify(352, 3, basket, order).Int(110318, order.Qty));
        await Send(ws, Notify(351, 15, basket, order).String(149273, "Cancel"));
    }

    private async Task Modify(WebSocket ws, ProtoMessage m)
    {
        string? rejection = ModifyRejection;
        bool rejectByNotification = RejectModifyByNotification;
        string basket = m.String(110300) ?? "";
        (bool Buy, int Qty, double Price, int Type, string Tag) o;
        lock (_gate)
        {
            if (!Open.TryGetValue(basket, out o)) return;
            if (rejection == null && !rejectByNotification && (!LoseModifyResponse || ApplyLostModification))
            {
                o = (o.Buy, (int)m.Int(112004), m.Has(110306) ? m.Double(110306) : m.Double(149247), o.Type, o.Tag);
                Open[basket] = o;
            }
        }
        Interlocked.Increment(ref _modifyRequests);

        if (LoseModifyResponse)
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Fixture lost modify response", _cts.Token);
            return;
        }
        if (ModifyResponseRelease != null) await ModifyResponseRelease.Task.WaitAsync(_cts.Token);
        if (rejection != null)
            await Send(ws, Msg(315, m).String(132766, "1").String(132766, rejection));
        else
        {
            await Send(ws, Msg(315, m).String(132766, "0"));
            if (rejectByNotification)
            {
                // Rejection packets can echo the attempted amendment rather than the still-working values.
                var attempted = (o.Buy, (int)m.Int(112004), m.Has(110306) ? m.Double(110306) : m.Double(149247), o.Type, o.Tag);
                await Send(ws, Notify(351, 16, basket, attempted).String(120008, "exchange refused amendment"));
            }
            else if (!SuppressModifyNotification)
                await Send(ws, Notify(351, 14, basket, o).Int(154112, o.Qty));
        }
        ModifyResponseSent.TrySetResult();
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

    private async Task SendPnl(bool snapshot, WebSocket? target = null)
    {
        WebSocket[] targets;
        lock (_gate) targets = target != null ? new[] { target } : AllowConcurrentTradingSessions
            ? _tradingSessions.Where(pair => pair.Value == 4 && pair.Key.State == WebSocketState.Open).Select(pair => pair.Key).ToArray()
            : _pnlSocket == null ? Array.Empty<WebSocket>() : new[] { _pnlSocket };
        if (targets.Length == 0) return;
        if (!snapshot && PositionUpdateRelease is { } release)
        {
            PositionUpdateRequested.TrySetResult();
            await release.Task.WaitAsync(_cts.Token);
        }
        double open = (MarketPrice - AvgPrice) * Position * 50;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var ws in targets)
        {
            await Send(ws, new ProtoWriter().Int(154467, 450).Int(110121, snapshot ? 1 : 0).String(154008, "ACC1").String(110100, "ESZ6").String(110101, "CME")
                .Int(156967, ReportedPositionOverride ?? Position).Double(154434, AvgPrice).String(156961, open.ToString(inv)).Double(157955, ClosedPnl));
            await Send(ws, new ProtoWriter().Int(154467, 451).Int(110121, snapshot ? 1 : 0).String(154008, "ACC1")
                .String(156970, (StartBalance + ClosedPnl).ToString("0.00", inv)).String(156971, (StartBalance + ClosedPnl).ToString("0.00", inv))
                .String(157015, (StartBalance + ClosedPnl - Math.Abs(Position) * 500).ToString("0.00", inv)).String(156961, open.ToString("0.00", inv))
                .Double(157955, ClosedPnl).Double(157956, ClosedPnl + open));
        }
    }

    public Task PublishPosition() => SendPnl(snapshot: false);

    public async Task PublishPosition(string symbol, int net, string exchange = "CME")
    {
        WebSocket[] targets;
        lock (_gate) targets = AllowConcurrentTradingSessions
            ? _tradingSessions.Where(pair => pair.Value == 4 && pair.Key.State == WebSocketState.Open).Select(pair => pair.Key).ToArray()
            : new[] { _pnlSocket! };
        foreach (var ws in targets)
            await Send(ws, new ProtoWriter().Int(154467, 450).String(154008, "ACC1")
                .String(110100, symbol).String(110101, exchange).Int(156967, net));
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

    /// <summary>Sends only the supplied BBO fields to current subscriptions, including incomplete side reports.</summary>
    public async Task PushBestBidOffer(string symbol, double? bidPrice = null, int? bidSize = null,
        double? askPrice = null, int? askSize = null)
    {
        WebSocket[] targets;
        lock (_gate) targets = _marketData.Where(kv => kv.Key.State == WebSocketState.Open && kv.Value.Contains(symbol)).Select(kv => kv.Key).ToArray();
        foreach (var ws in targets)
        {
            var message = new ProtoWriter().Int(154467, 151).String(110100, symbol).String(110101, "CME");
            if (bidPrice is { } bid) message.Double(100022, bid);
            if (bidSize is { } bids) message.Int(100030, bids);
            if (askPrice is { } ask) message.Double(100025, ask);
            if (askSize is { } asks) message.Int(100031, asks);
            await Send(ws, message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports a working order that this Trading Hub process did not send (another session's, for example) on the order
    /// plant, as Rithmic reports the account's orders. It can then be modified or canceled by basket id.
    /// </summary>
    public async Task PushOrder(string basket, string tag, bool buy, int qty, double price, int type = 1,
        string symbol = "ESZ6", string exchange = "CME", bool cumulativeKnown = true, int? orderSession = null)
    {
        var o = (Buy: buy, Qty: qty, Price: price, Type: type, Tag: tag);
        WebSocket? ws;
        lock (_gate)
        {
            Open[basket] = o;
            _orderContracts[basket] = (symbol, exchange);
            if (orderSession is { } index) ws = _orderSessions[index];
            else _sessions.TryGetValue(2, out ws);
        }

        if (ws is { State: WebSocketState.Open })
        {
            var notification = Notify(351, 13, basket, o).Int(154112, qty);
            if (cumulativeKnown) notification.Int(154111, 0);
            await Send(ws, notification);
        }
    }

    /// <summary>A real late execution report for a canceled basket; never reopens or resubmits it.</summary>
    public async Task FillCanceledOrder(string basket, int quantity, double price)
    {
        (bool Buy, int Qty, double Price, int Type, string Tag) order;
        WebSocket ws;
        int filled;
        lock (_gate)
        {
            order = _canceledOrders[basket];
            filled = _filled.GetValueOrDefault(basket) + quantity;
            if (quantity <= 0 || filled > order.Qty) throw new ArgumentOutOfRangeException(nameof(quantity));
            _filled[basket] = filled;
            if (filled == order.Qty) _canceledOrders.Remove(basket);
            ws = _sessions[2];
            ApplyFill(order.Buy, quantity, price);
        }
        await Send(ws, Notify(352, 5, basket, order).Double(110307, price).Int(110308, quantity)
            .Int(154111, filled).Int(154112, order.Qty - filled));
        await SendPnl(snapshot: false);
    }

    public async Task FillOrder(string basket, int quantity, double price, bool includeQuantity = true,
        bool allOrderSessions = false)
    {
        (bool Buy, int Qty, double Price, int Type, string Tag) order;
        WebSocket[] sockets;
        int filled;
        lock (_gate)
        {
            order = Open[basket];
            filled = _filled.GetValueOrDefault(basket) + quantity;
            if (quantity <= 0 || filled > order.Qty) throw new ArgumentOutOfRangeException(nameof(quantity));
            _filled[basket] = filled;
            if (filled == order.Qty) Open.Remove(basket);
            sockets = allOrderSessions ? _tradingSessions.Where(pair => pair.Value == 2 && pair.Key.State == WebSocketState.Open)
                .Select(pair => pair.Key).ToArray() : new[] { _sessions[2] };
            ApplyFill(order.Buy, quantity, price);
        }
        foreach (var ws in sockets)
            await Send(ws, Notify(352, 5, basket, order, includeQuantity).Double(110307, price).Int(110308, quantity)
                .Int(154111, filled).Int(154112, order.Qty - filled));
        await SendPnl(snapshot: false);
    }

    public void FillWhileDisconnected(string basket, int quantity, double price)
    {
        lock (_gate)
        {
            var order = Open[basket];
            int filled = _filled.GetValueOrDefault(basket) + quantity;
            if (quantity <= 0 || filled >= order.Qty) throw new ArgumentOutOfRangeException(nameof(quantity));
            _filled[basket] = filled;
            ApplyFill(order.Buy, quantity, price);
        }
    }

    public async Task ReplayFill(string basket, int cumulative, int quantity, double price)
    {
        (bool Buy, int Qty, double Price, int Type, string Tag) order;
        WebSocket ws;
        lock (_gate)
        {
            order = Open[basket];
            ws = _sessions[2];
        }
        await Send(ws, Notify(352, 5, basket, order).Double(110307, price).Int(110308, quantity)
            .Int(154111, cumulative).Int(154112, order.Qty - cumulative));
    }

    public async Task DisconnectOrderSession(int index)
    {
        WebSocket socket;
        lock (_gate) socket = _orderSessions[index];
        await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Fixture reconnect", _cts.Token);
    }

    public async Task RemoveOrderWhileDisconnected(string basket)
    {
        WebSocket? socket;
        lock (_gate)
        {
            Open.Remove(basket);
            _sessions.TryGetValue(2, out socket);
        }
        if (socket is { State: WebSocketState.Open })
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Fixture disconnect", _cts.Token);
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
