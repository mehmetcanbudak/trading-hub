using System.Globalization;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.App.Services;

public enum AccountKind
{
    Sim,
    Rithmic,
    Tradovate,
}

/// <summary>Una cuenta para la sección Cuenta.</summary>
/// <param name="CanTrade">New orders allowed (always on simulated, practice and demo accounts); cancel and close never need it.</param>
public sealed record AccountEntry(string Id, string Name, AccountKind Kind, bool IsReal, bool CanTrade, ConnectionProfile? Profile = null, string RithmicAccount = "");

/// <summary>Posición de un contrato (las que quedaron planas hoy también figuran).</summary>
public sealed record PositionLine(string Key, string Symbol, decimal Net, decimal AvgPrice, decimal OpenPnl, decimal ClosedPnl, object? Ref = null);

/// <summary>Una orden de hoy, activa o terminada.</summary>
public sealed record OrderLine(string Key, DateTime Time, string Symbol, bool Buy, string Type, decimal Quantity, decimal Price, decimal Filled, decimal AvgFill, string Status, bool Active, object? Ref = null);

/// <summary>Foto de una cuenta: saldo, resultado, posiciones, órdenes, ejecuciones y operaciones cerradas.</summary>
public sealed class AccountSnapshot
{
    public required AccountEntry Account { get; init; }
    public string Status { get; set; } = "";
    public bool Connected { get; set; } = true;
    public decimal? Balance { get; set; }
    public decimal? StartBalance { get; set; }
    public decimal OpenPnl { get; set; }
    public decimal RealizedToday { get; set; }
    public decimal DayPnl { get; set; }
    public decimal? Available { get; set; }
    public decimal? MarginBalance { get; set; }
    public int TradesToday { get; set; }
    public List<PositionLine> Positions { get; } = new();
    public List<OrderLine> Orders { get; } = new();
    public IReadOnlyList<AccountFill> Fills { get; set; } = Array.Empty<AccountFill>();
    public RoundTripResult Analysis { get; set; } = RoundTrips.Analyze(Array.Empty<AccountFill>());
}

/// <summary>
/// Junta lo de cada cuenta para la sección Cuenta y la barra de arriba. La simulada se arma con lo que guarda
/// Trading Hub (ejecuciones en disco) y las de Rithmic con lo que manda Rithmic (saldo, posiciones, órdenes).
/// </summary>
public sealed class AccountCenter
{
    private readonly AppServices _svc;
    private readonly Dictionary<string, (int Count, RoundTripResult Result)> _cache = new();

    public AccountCenter(AppServices svc)
    {
        _svc = svc;
    }

    public const string SimId = "sim";

    /// <summary>Zona horaria para mostrar horas y decidir qué es "hoy".</summary>
    public TimeZoneInfo TimeZone
    {
        get
        {
            try
            {
                return _svc.Settings.TimeZoneId is { Length: > 0 } id ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.Local;
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.Local;
            }
        }
    }

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);

    public bool IsToday(DateTime utc) => ToLocal(utc).Date == ToLocal(DateTime.UtcNow).Date;

    public IReadOnlyList<AccountEntry> Accounts()
    {
        var list = new List<AccountEntry> { new(SimId, "Simulated account", AccountKind.Sim, false, true) };
        foreach (var p in _svc.Connections.Enabled.Where(p => p.IsRithmic && !string.IsNullOrWhiteSpace(p.RithmicUser)))
        {
            var session = _svc.RithmicSessionFor(p);
            foreach (var a in session.Accounts)
            {
                bool real = !session.IsPractice;
                list.Add(new AccountEntry(AppServices.RithmicAccountId(p, a.AccountId), $"{a.AccountId} · {p.Name}{(real ? " — REAL" : " (practice)")}",
                    AccountKind.Rithmic, real, !real || p.AllowRealOrders, p, a.AccountId));
            }
        }

        foreach (var p in _svc.Connections.Enabled.Where(p => p.Kind == ConnectionKind.Tradovate && !string.IsNullOrWhiteSpace(p.TradovateUser)))
        {
            var session = _svc.TradovateSessionFor(p);
            foreach (var a in session.Accounts)
            {
                bool real = !session.IsDemo;
                list.Add(new AccountEntry(AppServices.TradovateAccountId(p, a.Id), $"{a.Name} · {p.Name}{(real ? " — REAL" : " (demo)")}",
                    AccountKind.Tradovate, real, !real || p.AllowRealOrders, p, a.Name));
            }
        }

        return list;
    }

    /// <summary>Operaciones y resultados de la cuenta (se recalcula solo cuando hay ejecuciones nuevas).</summary>
    public RoundTripResult Analysis(string accountId, IReadOnlyList<AccountFill> fills)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(accountId, out var c) && c.Count == fills.Count) return c.Result;
            var r = RoundTrips.Analyze(fills);
            _cache[accountId] = (fills.Count, r);
            return r;
        }
    }

    /// <summary>Resultado del día y saldo de la cuenta simulada (para la barra de arriba).</summary>
    public (decimal DayPnl, decimal Balance) SimSummary()
    {
        var fills = _svc.Journal.FillsOf(SimId);
        var r = Analysis(SimId, fills);
        decimal open = _svc.SimBrokers.Sum(b => b.Position?.UnrealizedPnL ?? 0);
        decimal today = r.Realized.Where(x => IsToday(x.Time)).Sum(x => x.Net);
        return (today + open, _svc.Settings.SimStartBalance + r.RealizedNet + open);
    }

    public AccountSnapshot Snapshot(AccountEntry a)
    {
        var s = new AccountSnapshot { Account = a };
        s.Fills = _svc.Journal.FillsOf(a.Id);
        s.Analysis = Analysis(a.Id, s.Fills);
        s.TradesToday = s.Analysis.Trades.Count(t => IsToday(t.CloseTime));
        decimal realizedToday = s.Analysis.Realized.Where(x => IsToday(x.Time)).Sum(x => x.Net);
        if (a.Kind == AccountKind.Sim) FillSim(s, realizedToday);
        else if (a.Kind == AccountKind.Tradovate) FillTradovate(s, realizedToday);
        else FillRithmic(s, realizedToday);
        return s;
    }

    private static string TypeName(OrderTypes t) => t switch
    {
        OrderTypes.Market => "Market",
        OrderTypes.Stop => "Stop",
        _ => "Limit",
    };

    private void FillSim(AccountSnapshot s, decimal realizedToday)
    {
        foreach (var b in _svc.SimBrokers.OrderBy(b => b.Instrument.Symbol))
        {
            var pos = b.Position!;
            if (pos.Volume != 0 || b.Orders.Count > 0 || b.MyTrades.Count > 0)
                s.Positions.Add(new PositionLine(b.Instrument.Key, b.Instrument.Symbol, pos.Volume, pos.AveragePrice, pos.UnrealizedPnL, pos.RealizedPnL, b));

            foreach (var o in b.Orders)
            {
                var fills = b.MyTrades.Where(t => ReferenceEquals(t.Order, o)).ToList();
                decimal qty = fills.Sum(t => t.Volume);
                decimal avg = qty > 0 ? fills.Sum(t => t.Price * t.Volume) / qty : 0;
                bool active = o.State == OrderStates.Active;
                string status = active
                    ? o.Filled > 0 ? "Partial" : "Active"
                    : o.Status switch
                    {
                        OrderStatus.Filled => "Filled",
                        OrderStatus.Canceled => o.Filled > 0 ? "Canceled (partial)" : "Canceled",
                        OrderStatus.Rejected => "Rejected",
                        _ => o.State == OrderStates.Failed ? "Rejected" : o.Filled > 0 ? "Filled" : "Completed",
                    };
                s.Orders.Add(new OrderLine("sim|" + o.Id, o.Time, b.Instrument.Symbol, o.Direction == OrderDirections.Buy, TypeName(o.Type),
                    o.QuantityToFill, o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price, o.Filled, avg, status, active, (b, o)));
            }
        }

        s.Orders.Sort((x, y) => x.Time.CompareTo(y.Time));
        s.OpenPnl = s.Positions.Sum(p => p.OpenPnl);
        s.StartBalance = _svc.Settings.SimStartBalance;
        s.Balance = s.StartBalance + s.Analysis.RealizedNet + s.OpenPnl;
        s.RealizedToday = realizedToday;
        s.DayPnl = realizedToday + s.OpenPnl;
        s.Status = "Trading Hub simulates fills using real market data";
    }

    private void FillRithmic(AccountSnapshot s, decimal realizedToday)
    {
        var session = _svc.RithmicSessionFor(s.Account.Profile!);
        string acc = s.Account.RithmicAccount;
        s.Connected = session.IsConnected;
        s.Status = session.IsConnected ? $"Connected to Rithmic ({session.System})" : session.StatusText;
        foreach (var (sym, ex, p) in session.PositionsOf(acc))
            s.Positions.Add(new PositionLine(sym + "|" + ex, sym, p.Net, p.AvgPrice, p.OpenPnl, p.ClosedPnl, (sym, ex)));

        foreach (var o in session.OrdersOf(acc))
        {
            string type = o.PriceType switch { 2 => "Market", 3 => "Stop limit", 4 => "Stop", _ => "Limit" };
            string status = !o.Done
                ? o.Filled > 0 ? "Partial" : "Active"
                : o.Rejected ? "Rejected" + (o.Reason.Length > 0 ? ": " + o.Reason : "")
                : o.Cancelled ? o.Filled > 0 ? "Canceled (partial)" : "Canceled"
                : "Filled";
            s.Orders.Add(new OrderLine(o.BasketId, o.Time, o.Symbol, o.IsBuy, type, o.Quantity, o.PriceType is 3 or 4 ? o.Trigger : o.Price,
                o.Filled, o.AvgFillPrice, status, !o.Done, o));
        }

        var sum = session.SummaryFor(acc);
        s.OpenPnl = sum?.OpenPnl ?? s.Positions.Sum(p => p.OpenPnl);
        s.RealizedToday = sum?.ClosedPnl ?? (s.Positions.Count > 0 ? s.Positions.Sum(p => p.ClosedPnl) : realizedToday);
        s.DayPnl = sum?.DayPnl ?? s.RealizedToday + s.OpenPnl;
        s.Balance = sum?.Balance ?? sum?.CashOnHand;
        s.Available = sum?.AvailableBuyingPower;
        s.MarginBalance = sum?.MarginBalance;
    }

    private static long TradovateIdOf(AccountEntry a) => long.TryParse(a.Id.Split('|').Last(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private void FillTradovate(AccountSnapshot s, decimal realizedToday)
    {
        var session = _svc.TradovateSessionFor(s.Account.Profile!);
        long acc = TradovateIdOf(s.Account);
        s.Connected = session.IsConnected;
        s.Status = session.IsConnected ? $"Connected to Tradovate ({(session.IsDemo ? "demo" : "real")})" : session.StatusText;
        foreach (var p in session.PositionsOf(acc))
        {
            decimal pv = PointValueOf(p.Symbol);
            decimal closed = p.Bought > 0 && p.Sold > 0 ? Math.Min(p.Bought, p.Sold) * (p.SoldValue / p.Sold - p.BoughtValue / p.Bought) * pv : 0;
            s.Positions.Add(new PositionLine("tv|" + p.ContractId, p.Symbol, p.Net, p.NetPrice, 0, closed, p));
        }

        foreach (var o in session.OrdersOf(acc))
        {
            string type = o.Type switch { "Market" => "Market", "Stop" => "Stop", "StopLimit" => "Stop limit", "TrailingStop" => "Trailing stop", _ => "Limit" };
            string status = !o.Done
                ? o.Filled > 0 ? "Partial" : "Active"
                : o.Rejected ? "Rejected" + (o.Text.Length > 0 ? ": " + o.Text : "")
                : o.Cancelled ? o.Filled > 0 ? "Canceled (partial)" : "Canceled"
                : "Filled";
            s.Orders.Add(new OrderLine("tv|" + o.Id, o.Time, o.Symbol, o.IsBuy, type, o.Quantity, o.Type is "Stop" or "StopLimit" ? o.StopPrice : o.Price,
                o.Filled, o.AvgFill, status, !o.Done, o));
        }

        var cash = session.CashOf(acc);
        s.OpenPnl = cash?.OpenPnl ?? 0;
        s.RealizedToday = cash?.RealizedPnl ?? realizedToday;
        s.DayPnl = s.RealizedToday + s.OpenPnl;
        s.Balance = cash?.NetLiq ?? cash?.Balance;
        s.MarginBalance = cash?.InitialMargin;
    }

    /// <summary>Valor del punto de un contrato de futuros por su símbolo (ESZ6 → ES → 50).</summary>
    public static decimal PointValueOf(string contract) => RithmicFeed.PointValueOfContract(contract);

    // ------------------------------------------------------------------ acciones

    /// <summary>
    /// Cancels an active order. A cancel never adds exposure, so it stays available on real accounts even with real
    /// orders disabled (the Account page confirms it first).
    /// </summary>
    public async Task CancelAsync(AccountEntry a, OrderLine o)
    {
        if (!o.Active) return;
        if (o.Ref is (SimBroker b, Order order))
        {
            b.Cancel(order);
            return;
        }

        if (a.Kind == AccountKind.Rithmic)
            await _svc.RithmicSessionFor(a.Profile!).CancelAsync(a.RithmicAccount, o.Key, CancellationToken.None).ConfigureAwait(false);
        else if (a.Kind == AccountKind.Tradovate && o.Ref is TradingHub.Feeds.Tradovate.TradovateOrderState tv)
            await _svc.TradovateSessionFor(a.Profile!).CancelAsync(tv.Id, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes a contract's position at market, canceling its orders first (like Chart Trader's Close). Closing only
    /// reduces, so it stays available on real accounts even with real orders disabled (the Account page confirms it first).
    /// </summary>
    /// <returns>null once the close is sent; otherwise why nothing was sent, for the status bar.</returns>
    public async Task<string?> CloseAsync(AccountEntry a, PositionLine p)
    {
        if (p.Net == 0) return NothingToClose(p.Symbol, 0);
        if (p.Ref is SimBroker b)
        {
            b.Flatten();
            return null;
        }

        if (a.Kind == AccountKind.Tradovate && p.Ref is TradingHub.Feeds.Tradovate.TradovatePositionState tp)
        {
            // Tradovate cancela las órdenes del contrato y cierra a mercado en un solo pedido
            await _svc.TradovateSessionFor(a.Profile!).LiquidateAsync(tp.AccountId, tp.ContractId, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        if (a.Kind == AccountKind.Rithmic && p.Ref is (string sym, string ex))
        {
            var session = _svc.RithmicSessionFor(a.Profile!);
            int canceled = 0;
            foreach (var o in session.OrdersOf(a.RithmicAccount).Where(o => !o.Done && o.Symbol == sym && o.Exchange == ex))
            {
                await session.CancelAsync(a.RithmicAccount, o.BasketId, CancellationToken.None).ConfigureAwait(false);
                canceled++;
            }

            // what is open now, after the cancels (the row can be a moment old): a close never opens the other side
            decimal net = session.PositionFor(a.RithmicAccount, sym, ex)?.Net ?? 0;
            if (net == 0) return NothingToClose(sym, canceled);
            if (Math.Sign(net) != Math.Sign(p.Net))
                return $"{sym} changed side before the close was sent (now {(net > 0 ? "long" : "short")} {Math.Abs(net):0.####}): nothing was sent; check the position and close it again.";
            int qty = (int)Math.Abs(net);
            await session.PlaceAsync(a.RithmicAccount, sym, ex, net < 0, 2, qty, 0, 0, "th-cerrar-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
        }

        return null;
    }

    private static string NothingToClose(string symbol, int canceled) =>
        $"{symbol} is already flat: nothing needed closing{(canceled > 0 ? $" ({canceled} working order{(canceled == 1 ? "" : "s")} canceled)" : "")}.";
}
