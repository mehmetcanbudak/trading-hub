using System.Collections.Concurrent;
using System.Globalization;

namespace TradingHub.Feeds.Rithmic;

/// <summary>Campos de las plantas de órdenes y de PnL (definiciones oficiales de R|Protocol).</summary>
internal static class RpO
{
    // plantillas
    public const int RequestLoginInfo = 300, ResponseLoginInfo = 301;
    public const int RequestAccountList = 302, ResponseAccountList = 303;
    public const int RequestSubscribeOrders = 308, ResponseSubscribeOrders = 309;
    public const int RequestTradeRoutes = 310, ResponseTradeRoutes = 311;
    public const int RequestNewOrder = 312, ResponseNewOrder = 313;
    public const int RequestModifyOrder = 314, ResponseModifyOrder = 315;
    public const int RequestCancelOrder = 316, ResponseCancelOrder = 317;
    public const int RequestShowOrders = 320, ResponseShowOrders = 321;
    public const int RequestCancelAll = 346, ResponseCancelAll = 347;
    public const int RithmicOrderNotification = 351, ExchangeOrderNotification = 352;
    public const int RequestPnlUpdates = 400, ResponsePnlUpdates = 401;
    public const int RequestPnlSnapshot = 402, ResponsePnlSnapshot = 403;
    public const int InstrumentPnl = 450, AccountPnl = 451;
    public const int Reject = 75, ForcedLogout = 77;

    // campos
    public const int RqHandlerRpCode = 132764;
    public const int FcmId = 154013, IbId = 154014, UserType = 154036;
    public const int AccountId = 154008, AccountName = 154002, AccountCurrency = 154383;
    public const int TradeRoute = 112016, RouteStatus = 131407, IsDefault = 154689, SubscribeForUpdates = 154352;
    public const int UserTag = 154119, WindowName = 154629;
    public const int Quantity = 112004, Price = 110306, TriggerPrice = 149247;
    public const int TransactionType = 112003, Duration = 112005, PriceType = 112008, ManualOrAuto = 154710;
    public const int BasketId = 110300, NotifyType = 153625, IsSnapshot = 110121, Status = 110303, CompletionReason = 149273;
    public const int AvgFillPrice = 110322, TotalFillSize = 154111, TotalUnfilledSize = 154112;
    public const int FillPrice = 110307, FillSize = 110308, Text = 120008, ReportText = 120028;
    public const int Request = 100000;
    public const int NetQuantity = 156967, AvgOpenFillPrice = 154434, OpenPositionPnl = 156961, ClosedPositionPnl = 156963;
    public const int DayOpenPnl = 157954, DayClosedPnl = 157955, DayPnl = 157956;
    public const int AccountBalance = 156970, CashOnHand = 156971, MarginBalance = 156977, MinAccountBalance = 156968;
    public const int AvailableBuyingPower = 157015, UsedBuyingPower = 157014, ExcessBuyMargin = 156991;
    public const int Ssboe = 150100, Usecs = 150101;
}

/// <summary>Saldo y resultado de una cuenta (planta de PnL).</summary>
public sealed class RithmicAccountSummary
{
    public decimal? Balance { get; set; }
    public decimal? CashOnHand { get; set; }
    public decimal? MarginBalance { get; set; }
    public decimal? MinAccountBalance { get; set; }
    public decimal? AvailableBuyingPower { get; set; }
    public decimal? UsedBuyingPower { get; set; }
    public decimal? OpenPnl { get; set; }
    public decimal? ClosedPnl { get; set; }
    public decimal? DayPnl { get; set; }
    public DateTime Updated { get; set; }
}

/// <summary>Una ejecución (parcial o total) de una orden.</summary>
public sealed record RithmicFill(string Id, string Account, string Symbol, string Exchange, bool IsBuy, decimal Quantity, decimal Price, DateTime Time, string BasketId);

/// <summary>Una cuenta de Rithmic.</summary>
public sealed record RithmicAccountInfo(string FcmId, string IbId, string AccountId, string Name, string Currency);

/// <summary>Una orden en Rithmic, como la cuentan sus notificaciones.</summary>
public sealed class RithmicOrderState
{
    public string BasketId { get; set; } = "";
    public string UserTag { get; set; } = "";
    public string Account { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public bool IsBuy { get; set; }

    /// <summary>1 límite, 2 mercado, 3 stop límite, 4 stop mercado.</summary>
    public int PriceType { get; set; }

    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Trigger { get; set; }
    public decimal Filled { get; set; }
    public decimal Unfilled { get; set; }
    public decimal AvgFillPrice { get; set; }
    public bool Done { get; set; }
    public bool Rejected { get; set; }
    public bool Cancelled { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>Cuándo se vio la orden por primera vez (UTC).</summary>
    public DateTime Time { get; set; } = DateTime.UtcNow;
}

/// <summary>Posición de un instrumento en una cuenta (planta de PnL).</summary>
public sealed class RithmicPosition
{
    public decimal Net { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal OpenPnl { get; set; }
    public decimal ClosedPnl { get; set; }
}

/// <summary>
/// Sesión de trading de Rithmic para un usuario: planta de órdenes (cuentas, rutas, órdenes y ejecuciones) y planta
/// de PnL (posiciones y resultado). Se conecta en segundo plano y se reconecta sola.
/// </summary>
public sealed class RithmicTradingSession : IDisposable
{
    private RithmicSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _connCts;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProtoMessage>> _pending = new();
    private readonly ConcurrentDictionary<string, RithmicOrderState> _orders = new();
    private readonly ConcurrentDictionary<string, RithmicPosition> _positions = new();
    private readonly ConcurrentDictionary<string, decimal> _balances = new();
    private readonly ConcurrentDictionary<string, RithmicAccountSummary> _summaries = new();
    private readonly ConcurrentDictionary<string, RithmicFill> _fills = new();
    private readonly List<(string Exchange, string Route, bool Default, bool Up)> _routes = new();
    private RithmicClient? _orderClient;
    private string _fcm = "", _ib = "";
    private int _userType = 3;
    private long _seq;
    private Task? _loop;

    public RithmicTradingSession(RithmicSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Sistema de la sesión (para saber si es de práctica).</summary>
    public string System => _settings.System;

    /// <summary>Paper Trading y Test son de práctica; el resto (Rithmic 01, prop firms…) se trata como real.</summary>
    public bool IsPractice => _settings.System is "Rithmic Paper Trading" or "Rithmic Test";

    public IReadOnlyList<RithmicAccountInfo> Accounts { get; private set; } = Array.Empty<RithmicAccountInfo>();
    public bool IsConnected { get; private set; }
    public string StatusText { get; private set; } = "Connecting…";

    /// <summary>Cambió algo (cuentas, órdenes, posiciones). Llega desde hilos de red.</summary>
    public event Action? Changed;

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Cambió la configuración: la misma sesión vuelve a entrar con los datos nuevos (las cuentas abiertas siguen andando).</summary>
    public void Reconfigure(RithmicSettings settings)
    {
        _settings = settings;
        try
        {
            _connCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Marca como vista la excepción de una tarea que ya no se espera (si no, el recolector la reporta como error).</summary>
    private static void Observe(Task t) => t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    private static string Key(string account, string symbol, string exchange) => $"{account}|{symbol}|{exchange}";

    public RithmicPosition? PositionFor(string account, string symbol, string exchange) =>
        _positions.TryGetValue(Key(account, symbol, exchange), out var p) ? p : null;

    public decimal? BalanceFor(string account) => _balances.TryGetValue(account, out var b) ? b : null;

    public IReadOnlyList<RithmicOrderState> OrdersFor(string account, string symbol, string exchange) =>
        _orders.Values.Where(o => o.Account == account && o.Symbol == symbol && o.Exchange == exchange).ToList();

    public RithmicOrderState? OrderByTag(string userTag) => _orders.Values.FirstOrDefault(o => o.UserTag == userTag);

    /// <summary>Todas las órdenes de la cuenta que se vieron en esta sesión (las de hoy, activas y terminadas).</summary>
    public IReadOnlyList<RithmicOrderState> OrdersOf(string account) =>
        _orders.Values.Where(o => o.Account == account).OrderBy(o => o.Time).ToList();

    /// <summary>Posiciones de la cuenta por contrato (incluye las que ya quedaron planas hoy).</summary>
    public IReadOnlyList<(string Symbol, string Exchange, RithmicPosition Position)> PositionsOf(string account) =>
        _positions.Where(kv => kv.Key.StartsWith(account + "|", StringComparison.Ordinal))
            .Select(kv => kv.Key.Split('|') is [_, var sym, var ex] ? (sym, ex, kv.Value) : ("", "", kv.Value))
            .OrderBy(p => p.Item1).ToList();

    public RithmicAccountSummary? SummaryFor(string account) => _summaries.TryGetValue(account, out var s) ? s : null;

    public IReadOnlyList<RithmicFill> FillsOf(string account) =>
        _fills.Values.Where(f => f.Account == account).OrderBy(f => f.Time).ToList();

    /// <summary>Llegó una ejecución nueva (también las de hoy que Rithmic repite al conectarse). Desde hilos de red.</summary>
    public event Action<RithmicFill>? FillReceived;

    // ------------------------------------------------------------------ conexión

    private async Task RunAsync()
    {
        int failures = 0;
        var root = _cts.Token;
        while (!root.IsCancellationRequested)
        {
            RithmicClient? orders = null, pnl = null;
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(root);
            _connCts = conn;
            var ct = conn.Token;
            try
            {
                StatusText = "Connecting to order plant…";
                orders = new RithmicClient();
                await orders.ConnectAsync(_settings.Uri, ct, _settings.TimeoutMs).ConfigureAwait(false);
                var login = await orders.LoginAsync(_settings.User, _settings.Password, _settings.System, _settings.AppName, _settings.AppVersion, 2, ct).ConfigureAwait(false);
                if (!login.Ok) throw new RithmicRejectedException(login.Message);

                var info = await AskAsync(orders, new ProtoWriter().Int(Rp.TemplateId, RpO.RequestLoginInfo), RpO.ResponseLoginInfo, ct).ConfigureAwait(false);
                _fcm = info.String(RpO.FcmId) ?? "";
                _ib = info.String(RpO.IbId) ?? "";
                _userType = (int)info.Int(RpO.UserType, 3);

                var accounts = new List<RithmicAccountInfo>();
                foreach (var row in await CollectAsync(orders, Ids(new ProtoWriter().Int(Rp.TemplateId, RpO.RequestAccountList)).Int(RpO.UserType, _userType), RpO.ResponseAccountList, ct).ConfigureAwait(false))
                {
                    var id = row.String(RpO.AccountId);
                    if (string.IsNullOrEmpty(id)) continue;
                    accounts.Add(new RithmicAccountInfo(row.String(RpO.FcmId) ?? _fcm, row.String(RpO.IbId) ?? _ib, id, row.String(RpO.AccountName) ?? id, row.String(RpO.AccountCurrency) ?? "USD"));
                }

                var routeRows = await CollectAsync(orders, new ProtoWriter().Int(Rp.TemplateId, RpO.RequestTradeRoutes).Bool(RpO.SubscribeForUpdates, true), RpO.ResponseTradeRoutes, ct).ConfigureAwait(false);
                lock (_routes)
                {
                    _routes.Clear();
                    foreach (var row in routeRows)
                    {
                        var exch = row.String(Rp.Exchange);
                        var route = row.String(RpO.TradeRoute);
                        if (exch == null || route == null) continue;
                        _routes.Add((exch, route, row.Int(RpO.IsDefault) != 0, !string.Equals(row.String(RpO.RouteStatus), "down", StringComparison.OrdinalIgnoreCase)));
                    }
                }

                Accounts = accounts;
                // órdenes en vivo de cada cuenta + las que ya estaban abiertas
                foreach (var a in accounts)
                {
                    await orders.SendAsync(Account(new ProtoWriter().Int(Rp.TemplateId, RpO.RequestSubscribeOrders).String(Rp.UserMsg, "sub"), a), ct).ConfigureAwait(false);
                    await orders.SendAsync(Account(new ProtoWriter().Int(Rp.TemplateId, RpO.RequestShowOrders).String(Rp.UserMsg, "show"), a), ct).ConfigureAwait(false);
                }

                // posiciones y resultado
                StatusText = "Connecting to PnL plant…";
                pnl = new RithmicClient();
                await pnl.ConnectAsync(_settings.Uri, ct, _settings.TimeoutMs).ConfigureAwait(false);
                var pl = await pnl.LoginAsync(_settings.User, _settings.Password, _settings.System, _settings.AppName, _settings.AppVersion, 4, ct).ConfigureAwait(false);
                if (!pl.Ok) throw new RithmicRejectedException(pl.Message);
                foreach (var a in accounts)
                {
                    await pnl.SendAsync(Account(new ProtoWriter().Int(Rp.TemplateId, RpO.RequestPnlUpdates).String(Rp.UserMsg, "pnl").Int(RpO.Request, 1), a), ct).ConfigureAwait(false);
                    await pnl.SendAsync(Account(new ProtoWriter().Int(Rp.TemplateId, RpO.RequestPnlSnapshot).String(Rp.UserMsg, "snap"), a), ct).ConfigureAwait(false);
                }

                _orderClient = orders;
                IsConnected = true;
                failures = 0;
                StatusText = accounts.Count == 1 ? $"Account {accounts[0].AccountId} connected" : $"{accounts.Count} accounts connected";
                Changed?.Invoke();

                // escucha las dos plantas hasta que alguna se corte
                var a1 = ReadLoopAsync(orders, ct);
                var a2 = ReadLoopAsync(pnl, ct);
                Observe(a1);
                Observe(a2);
                await Task.WhenAny(a1, a2).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                throw new IOException("Rithmic connection lost");
            }
            catch (OperationCanceledException) when (root.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // configuración nueva: entra otra vez al toque
                IsConnected = false;
                failures = 0;
                StatusText = "Connecting with updated settings…";
                Changed?.Invoke();
            }
            catch (RithmicRejectedException ex)
            {
                IsConnected = false;
                StatusText = ex.Message;
                Changed?.Invoke();
                failures = Math.Max(failures, 4); // credenciales o permisos: reintenta despacio
            }
            catch (Exception ex)
            {
                IsConnected = false;
                failures++;
                StatusText = $"Reconnecting Rithmic orders · attempt {failures} ({ex.Message})";
                Changed?.Invoke();
            }
            finally
            {
                _orderClient = null;
                foreach (var p in _pending.Values) p.TrySetException(new IOException("Rithmic connection lost"));
                _pending.Clear();
                if (orders != null) await orders.DisposeAsync().ConfigureAwait(false);
                if (pnl != null) await pnl.DisposeAsync().ConfigureAwait(false);
            }

            if (root.IsCancellationRequested) break;
            await Task.Delay(LiveSubscription.Backoff(failures), root).ContinueWith(_ => { }).ConfigureAwait(false);
        }
    }

    private sealed class RithmicRejectedException : Exception
    {
        public RithmicRejectedException(string message) : base(message)
        {
        }
    }

    private ProtoWriter Ids(ProtoWriter w) => w.String(RpO.FcmId, _fcm).String(RpO.IbId, _ib);

    private static ProtoWriter Account(ProtoWriter w, RithmicAccountInfo a) =>
        w.String(RpO.FcmId, a.FcmId).String(RpO.IbId, a.IbId).String(RpO.AccountId, a.AccountId);

    /// <summary>Pedido con una sola respuesta (antes de que arranquen los lectores).</summary>
    private static async Task<ProtoMessage> AskAsync(RithmicClient c, ProtoWriter w, int responseTemplate, CancellationToken ct)
    {
        await c.SendAsync(w.String(Rp.UserMsg, "q"), ct).ConfigureAwait(false);
        for (int i = 0; i < 200; i++)
        {
            var m = await c.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new IOException("Rithmic closed the connection");
            if (m.Int(Rp.TemplateId) == responseTemplate) return m;
        }

        throw new IOException($"Rithmic did not respond ({responseTemplate})");
    }

    /// <summary>Pedido con varias filas: junta las filas hasta la que trae el código final.</summary>
    private static async Task<List<ProtoMessage>> CollectAsync(RithmicClient c, ProtoWriter w, int responseTemplate, CancellationToken ct)
    {
        await c.SendAsync(w.String(Rp.UserMsg, "c"), ct).ConfigureAwait(false);
        var rows = new List<ProtoMessage>();
        for (int i = 0; i < 5000; i++)
        {
            var m = await c.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new IOException("Rithmic closed the connection");
            if (m.Int(Rp.TemplateId) != responseTemplate) continue;
            if (m.Strings(RpO.RqHandlerRpCode).Any()) rows.Add(m); // fila de datos
            var rp = m.Strings(Rp.RpCode).ToList();
            if (rp.Count > 0)
            {
                if (rp[0] != "0" && rp[0] != "7") throw new RithmicRejectedException("Rithmic: " + string.Join(" ", rp.Skip(1).DefaultIfEmpty(rp[0])));
                return rows;
            }
        }

        return rows;
    }

    private async Task ReadLoopAsync(RithmicClient c, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var m = await c.ReceiveAsync(ct).ConfigureAwait(false);
            if (m == null) return;
            try
            {
                Handle(m);
            }
            catch (Exception)
            {
                // un mensaje raro no corta la sesión
            }
        }
    }

    private void Handle(ProtoMessage m)
    {
        int tpl = (int)m.Int(Rp.TemplateId);
        switch (tpl)
        {
            case RpO.RithmicOrderNotification:
            case RpO.ExchangeOrderNotification:
                OnOrder(m, tpl == RpO.ExchangeOrderNotification);
                return;
            case RpO.InstrumentPnl:
                OnPosition(m);
                return;
            case RpO.AccountPnl:
                if (m.String(RpO.AccountId) is { } acc)
                {
                    var sum = _summaries.GetOrAdd(acc, _ => new RithmicAccountSummary());
                    sum.Balance = m.Number(RpO.AccountBalance) ?? sum.Balance;
                    sum.CashOnHand = m.Number(RpO.CashOnHand) ?? sum.CashOnHand;
                    sum.MarginBalance = m.Number(RpO.MarginBalance) ?? sum.MarginBalance;
                    sum.MinAccountBalance = m.Number(RpO.MinAccountBalance) ?? sum.MinAccountBalance;
                    sum.AvailableBuyingPower = m.Number(RpO.AvailableBuyingPower) ?? sum.AvailableBuyingPower;
                    sum.UsedBuyingPower = m.Number(RpO.UsedBuyingPower) ?? sum.UsedBuyingPower;
                    sum.OpenPnl = m.Number(RpO.OpenPositionPnl) ?? m.Number(RpO.DayOpenPnl) ?? sum.OpenPnl;
                    sum.ClosedPnl = m.Number(RpO.DayClosedPnl) ?? m.Number(RpO.ClosedPositionPnl) ?? sum.ClosedPnl;
                    sum.DayPnl = m.Number(RpO.DayPnl) ?? sum.DayPnl;
                    sum.Updated = DateTime.UtcNow;
                    if (sum.Balance is { } bal) _balances[acc] = bal;
                }

                Changed?.Invoke();
                return;
            case RpO.ForcedLogout:
                StatusText = "Rithmic ended the order session (another login with the same username)";
                Changed?.Invoke();
                return;
        }

        // respuesta a un pedido nuestro
        foreach (var tag in m.Strings(Rp.UserMsg))
        {
            if (_pending.TryRemove(tag, out var tcs))
            {
                tcs.TrySetResult(m);
                return;
            }
        }
    }

    private void OnPosition(ProtoMessage m)
    {
        var acc = m.String(RpO.AccountId);
        var sym = m.String(Rp.Symbol);
        var exch = m.String(Rp.Exchange);
        if (acc == null || sym == null || exch == null) return;
        var p = _positions.GetOrAdd(Key(acc, sym, exch), _ => new RithmicPosition());
        if (m.Number(RpO.NetQuantity) is { } net) p.Net = net;
        if (m.Number(RpO.AvgOpenFillPrice) is { } avg) p.AvgPrice = avg;
        if ((m.Number(RpO.OpenPositionPnl) ?? m.Number(RpO.DayOpenPnl)) is { } open) p.OpenPnl = open;
        if ((m.Number(RpO.DayClosedPnl) ?? m.Number(RpO.ClosedPositionPnl)) is { } closed) p.ClosedPnl = closed;
        if (p.Net == 0) p.OpenPnl = 0;
        Changed?.Invoke();
    }

    private void OnOrder(ProtoMessage m, bool exchange)
    {
        var basket = m.String(RpO.BasketId);
        if (string.IsNullOrEmpty(basket)) return;
        long ssboe = m.Int(RpO.Ssboe);
        var when = ssboe > 0 ? DateTimeOffset.FromUnixTimeSeconds(ssboe).UtcDateTime.AddTicks(m.Int(RpO.Usecs) * 10) : DateTime.UtcNow;
        var o = _orders.GetOrAdd(basket, b => new RithmicOrderState { BasketId = b, Time = when });
        if (m.String(RpO.UserTag) is { Length: > 0 } tag) o.UserTag = tag;
        if (m.String(RpO.AccountId) is { Length: > 0 } acc) o.Account = acc;
        if (m.String(Rp.Symbol) is { Length: > 0 } sym) o.Symbol = sym;
        if (m.String(Rp.Exchange) is { Length: > 0 } ex) o.Exchange = ex;
        if (m.Has(RpO.TransactionType)) o.IsBuy = m.Int(RpO.TransactionType) == 1;
        if (m.Has(RpO.PriceType)) o.PriceType = (int)m.Int(RpO.PriceType);
        if (m.Has(RpO.Quantity)) o.Quantity = m.Int(RpO.Quantity);
        if (m.Has(RpO.Price)) o.Price = (decimal)m.Double(RpO.Price);
        if (m.Has(RpO.TriggerPrice)) o.Trigger = (decimal)m.Double(RpO.TriggerPrice);
        if (m.Has(RpO.TotalFillSize)) o.Filled = m.Int(RpO.TotalFillSize);
        if (m.Has(RpO.TotalUnfilledSize)) o.Unfilled = m.Int(RpO.TotalUnfilledSize);
        else if (o.Quantity > 0) o.Unfilled = Math.Max(0, o.Quantity - o.Filled);
        if (m.Has(RpO.AvgFillPrice)) o.AvgFillPrice = (decimal)m.Double(RpO.AvgFillPrice);
        int notify = (int)m.Int(RpO.NotifyType);
        string text = m.String(RpO.Text) ?? m.String(RpO.ReportText) ?? m.String(RpO.CompletionReason) ?? "";
        if (exchange)
        {
            switch (notify)
            {
                case 3: // cancelada
                    o.Done = true;
                    o.Cancelled = true;
                    break;
                case 5: // ejecución
                {
                    if (!m.Has(RpO.TotalFillSize))
                    {
                        // sin totales: se suma
                        o.Filled += m.Int(RpO.FillSize);
                        o.Unfilled = Math.Max(0, o.Quantity - o.Filled);
                    }

                    decimal size = m.Number(RpO.FillSize) ?? 0, price = m.Number(RpO.FillPrice) ?? o.AvgFillPrice;
                    if (size > 0 && price > 0 && o.Account.Length > 0)
                    {
                        // el total ejecutado después de esta ejecución la identifica (Rithmic la repite al reconectar)
                        var fill = new RithmicFill($"{basket}|{o.Filled.ToString(CultureInfo.InvariantCulture)}", o.Account, o.Symbol, o.Exchange, o.IsBuy, size, price, when, basket);
                        if (_fills.TryAdd(fill.Id, fill)) FillReceived?.Invoke(fill);
                    }

                    break;
                }
                case 6: // rechazada por el exchange
                    o.Done = true;
                    o.Rejected = true;
                    o.Reason = text;
                    break;
            }

            if (o.Quantity > 0 && o.Filled >= o.Quantity) o.Done = true;
        }
        else
        {
            if (notify == 15) // completa: llenada, cancelada o rechazada
            {
                o.Done = true;
                string why = (m.String(RpO.CompletionReason) ?? "").ToLowerInvariant();
                if (why.Contains("reject")) { o.Rejected = true; o.Reason = text; }
                else if (why.Contains("cancel")) o.Cancelled = true;
            }
            else if (notify is 16 or 17)
            {
                o.Reason = text.Length > 0 ? text : notify == 16 ? "could not modify" : "could not cancel";
            }
        }

        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ pedidos

    private async Task<ProtoMessage> RequestAsync(ProtoWriter w, CancellationToken ct)
    {
        var client = _orderClient ?? throw new InvalidOperationException("Rithmic is not connected yet (" + StatusText + ")");
        string tag = "v" + Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<ProtoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[tag] = tcs;
        await client.SendAsync(w.String(Rp.UserMsg, tag), ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using (timeout.Token.Register(() => tcs.TrySetException(new TimeoutException("Rithmic timed out"))))
        {
            var r = await tcs.Task.ConfigureAwait(false);
            var rp = r.Strings(Rp.RpCode).ToList();
            if (rp.Count > 0 && rp[0] != "0") throw new InvalidOperationException("Rithmic rejected: " + string.Join(" — ", rp.Skip(1).DefaultIfEmpty(rp[0])));
            return r;
        }
    }

    private RithmicAccountInfo AccountOf(string accountId) =>
        Accounts.FirstOrDefault(a => a.AccountId == accountId) ?? throw new InvalidOperationException($"Account {accountId} is not in this Rithmic session");

    private string RouteFor(string exchange)
    {
        lock (_routes)
        {
            var r = _routes.Where(x => x.Exchange == exchange && x.Up).OrderByDescending(x => x.Default).FirstOrDefault();
            if (r.Route == null) r = _routes.FirstOrDefault(x => x.Exchange == exchange);
            return r.Route ?? throw new InvalidOperationException($"Rithmic has no order route for {exchange}");
        }
    }

    /// <summary>Manda una orden. Devuelve el basket id (el número de la orden en Rithmic).</summary>
    /// <param name="priceType">1 límite, 2 mercado, 4 stop mercado.</param>
    public async Task<string> PlaceAsync(string accountId, string symbol, string exchange, bool buy, int priceType, int quantity, decimal price, decimal trigger, string userTag, CancellationToken ct)
    {
        var a = AccountOf(accountId);
        var w = Account(new ProtoWriter().Int(Rp.TemplateId, RpO.RequestNewOrder), a)
            .String(RpO.UserTag, userTag).String(RpO.WindowName, "TradingHub")
            .String(Rp.Symbol, symbol).String(Rp.Exchange, exchange)
            .Int(RpO.Quantity, quantity).Int(RpO.TransactionType, buy ? 1 : 2).Int(RpO.Duration, 1).Int(RpO.PriceType, priceType)
            .String(RpO.TradeRoute, RouteFor(exchange)).Int(RpO.ManualOrAuto, 1);
        if (priceType is 1 or 3) w.Double(RpO.Price, (double)price);
        if (priceType is 3 or 4) w.Double(RpO.TriggerPrice, (double)trigger);
        // la orden aparece enseguida (las notificaciones completan el resto)
        _orders.TryAdd("tag:" + userTag, new RithmicOrderState { UserTag = userTag, Account = accountId, Symbol = symbol, Exchange = exchange, IsBuy = buy, PriceType = priceType, Quantity = quantity, Unfilled = quantity, Price = price, Trigger = trigger });
        try
        {
            var r = await RequestAsync(w, ct).ConfigureAwait(false);
            var basket = r.String(RpO.BasketId) ?? "";
            _orders.TryRemove("tag:" + userTag, out _);
            if (basket.Length > 0)
            {
                var o = _orders.GetOrAdd(basket, b => new RithmicOrderState { BasketId = b, UserTag = userTag, Account = accountId, Symbol = symbol, Exchange = exchange, IsBuy = buy, PriceType = priceType, Quantity = quantity, Unfilled = quantity, Price = price, Trigger = trigger });
                if (o.UserTag.Length == 0) o.UserTag = userTag;
            }

            Changed?.Invoke();
            return basket;
        }
        catch
        {
            _orders.TryRemove("tag:" + userTag, out _);
            throw;
        }
    }

    public async Task CancelAsync(string accountId, string basketId, CancellationToken ct)
    {
        var a = AccountOf(accountId);
        await RequestAsync(Account(new ProtoWriter().Int(Rp.TemplateId, RpO.RequestCancelOrder), a)
            .String(RpO.WindowName, "TradingHub").String(RpO.BasketId, basketId).Int(RpO.ManualOrAuto, 1), ct).ConfigureAwait(false);
    }

    public async Task ModifyAsync(string accountId, RithmicOrderState o, int quantity, decimal priceOrTrigger, CancellationToken ct)
    {
        var a = AccountOf(accountId);
        var w = Account(new ProtoWriter().Int(Rp.TemplateId, RpO.RequestModifyOrder), a)
            .String(RpO.WindowName, "TradingHub").String(RpO.BasketId, o.BasketId)
            .String(Rp.Symbol, o.Symbol).String(Rp.Exchange, o.Exchange)
            .Int(RpO.Quantity, quantity).Int(RpO.PriceType, o.PriceType).Int(RpO.ManualOrAuto, 1);
        if (o.PriceType is 1 or 3) w.Double(RpO.Price, (double)(o.PriceType == 1 ? priceOrTrigger : o.Price));
        if (o.PriceType is 3 or 4) w.Double(RpO.TriggerPrice, (double)priceOrTrigger);
        await RequestAsync(w, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}
