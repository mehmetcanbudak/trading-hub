using System.Globalization;
using TradingHub.Feeds.Rithmic;
using TradingHub.Feeds.Bybit;
using TradingHub.Trading;

namespace TradingHub.App.Services;

public enum AccountKind
{
    Sim,
    Rithmic,
    Tradovate,
    Bybit,
    Replay,
}

/// <summary>Una cuenta para la sección Cuenta.</summary>
/// <param name="CanTrade">New orders allowed (always on simulated, practice and demo accounts); cancel and close never need it.</param>
public sealed record AccountEntry(string Id, string Name, AccountKind Kind, bool IsReal, bool CanTrade, ConnectionProfile? Profile = null, string RithmicAccount = "", string BybitContextIdentity = "");

/// <summary>Posición de un contrato (las que quedaron planas hoy también figuran).</summary>
public sealed record PositionLine(string Key, string Symbol, decimal Net, decimal AvgPrice, decimal OpenPnl, decimal ClosedPnl, object? Ref = null, bool ClosedPnlKnown = true);

/// <summary>Una orden de hoy, activa o terminada.</summary>
public sealed record OrderLine(string Key, DateTime Time, string Symbol, bool Buy, string Type, decimal Quantity, decimal Price, decimal Filled, decimal AvgFill, string Status, bool Active, object? Ref = null);

/// <summary>Foto de una cuenta: saldo, resultado, posiciones, órdenes, ejecuciones y operaciones cerradas.</summary>
public sealed class AccountSnapshot
{
    public required AccountEntry Account { get; init; }
    public string Status { get; set; } = "";
    public bool Connected { get; set; } = true;
    public bool FinancialsKnown { get; set; } = true;
    public bool CostsComplete { get; set; } = true;
    public bool OpenPnlKnown { get; set; } = true;
    public bool RealizedPnlKnown { get; set; } = true;
    public bool AnalysisKnown { get; set; } = true;
    public bool PositionsKnown { get; set; } = true;
    public bool OrdersKnown { get; set; } = true;
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
    public long ExecutionRevision { get; set; }
    public RoundTripResult Analysis { get; set; } = RoundTrips.Analyze(Array.Empty<AccountFill>());
}

/// <summary>
/// Junta lo de cada cuenta para la sección Cuenta y la barra de arriba. La simulada se arma con lo que guarda
/// Trading Hub (ejecuciones en disco) y las de Rithmic con lo que manda Rithmic (saldo, posiciones, órdenes).
/// </summary>
public sealed class AccountCenter
{
    private readonly AppServices _svc;
    private readonly Dictionary<string, (int Count, long ExecutionRevision, RoundTripResult Result)> _cache = new();

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

        foreach (var p in _svc.Connections.Enabled.Where(p => p.Kind == ConnectionKind.Bybit))
            _svc.EnsureBybitAccountRegistered(p);
        foreach (var (profile, session) in _svc.BybitContexts)
        {
            bool current = _svc.IsCurrentBybitContext(profile, session);
            string id = current ? profile.Id : "bybit-retired|" + profile.Id + "|" + session.ContextIdentity;
            string name = $"{profile.Name} ({session.Settings.EnvironmentName})" + (current ? "" : " — previous credentials; reduce/cancel only");
            list.Add(new AccountEntry(id, name, AccountKind.Bybit, session.IsReal, current && profile.Enabled && (!session.IsReal || profile.AllowRealOrders),
                profile, BybitContextIdentity: session.ContextIdentity));
        }
        foreach (var issue in _svc.BybitRecoveryIssues)
        {
            var profile = _svc.Connections.Profiles.FirstOrDefault(p => p.Id == issue.ProfileId) ??
                new ConnectionProfile { Id = issue.ProfileId, Name = "Retired Bybit " + issue.ProfileId, Kind = ConnectionKind.Bybit, Enabled = false };
            list.Add(new AccountEntry("bybit-unresolved|" + issue.ProfileId + "|" + issue.ContextIdentity, profile.Name + " — recovery unavailable",
                AccountKind.Bybit, true, false, profile, BybitContextIdentity: issue.ContextIdentity));
        }
        list.AddRange(_svc.ReplaySessions.Entries);

        return list;
    }

    /// <summary>Reuses immutable fill analysis until its count or the producer's execution-content revision changes.</summary>
    public RoundTripResult Analysis(string accountId, IReadOnlyList<AccountFill> fills, long executionRevision = 0)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(accountId, out var c) && c.Count == fills.Count && c.ExecutionRevision == executionRevision) return c.Result;
            var r = RoundTrips.Analyze(fills);
            _cache[accountId] = (fills.Count, executionRevision, r);
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
        if (a.Kind == AccountKind.Replay) return _svc.ReplaySessions.Snapshot(a.Id);
        var s = new AccountSnapshot { Account = a };
        BybitTradingSession? bybit = null;
        if (a.Kind == AccountKind.Bybit)
        {
            bybit = a.BybitContextIdentity.Length > 0 ? _svc.FindBybitContext(a.Profile!.Id, a.BybitContextIdentity) : _svc.BybitSessionFor(a.Profile!);
            if (bybit == null)
            {
                s.Connected = s.FinancialsKnown = s.CostsComplete = s.OpenPnlKnown = s.RealizedPnlKnown = s.AnalysisKnown = s.PositionsKnown = s.OrdersKnown = false;
                s.Status = _svc.BybitRecoveryIssues.FirstOrDefault(i => i.ProfileId == a.Profile!.Id && i.ContextIdentity == a.BybitContextIdentity)?.Message ??
                    "The selected Bybit credential context is unavailable; exposure is unknown.";
                return s;
            }
            if (bybit.PersistentAccountIdentity == null)
            {
                s.Connected = s.FinancialsKnown = s.CostsComplete = s.OpenPnlKnown = s.RealizedPnlKnown =
                    s.AnalysisKnown = s.PositionsKnown = s.OrdersKnown = false;
                s.Status = bybit.LastError ?? "Bybit account identity is not verified; persistent account history is deferred.";
                return s;
            }
        }
        var executionState = bybit?.GetExecutionState() ??
            (Executions: (IReadOnlyList<BybitExecution>)Array.Empty<BybitExecution>(), Revision: 0L);
        s.ExecutionRevision = executionState.Revision;
        string journalId = bybit != null ? AppServices.BybitJournalId(a.Profile!.Id, bybit) : a.Id;
        var fills = _svc.Journal.FillsOf(journalId);
        if (bybit != null)
        {
            var actual = fills.ToDictionary(fill => fill.Id, StringComparer.Ordinal);
            foreach (var fill in executionState.Executions)
            {
                string id = "bybit-" + fill.Symbol + "-" + fill.OrderId + "-" + fill.Id;
                actual[id] = new AccountFill(id, fill.Time, fill.Symbol, fill.Buy, fill.Quantity, fill.Price, 1,
                    fill.HasQuoteFee ? fill.Fee!.Value : 0, fill.OrderId);
            }
            s.Fills = actual.Values.OrderBy(fill => fill.Time).ThenBy(fill => fill.Id, StringComparer.Ordinal).ToArray();
            s.Analysis = Analysis(journalId, s.Fills, executionState.Revision);
        }
        else { s.Fills = fills; s.Analysis = Analysis(journalId, fills); }
        s.TradesToday = s.Analysis.Trades.Count(t => IsToday(t.CloseTime));
        decimal realizedToday = s.Analysis.Realized.Where(x => IsToday(x.Time)).Sum(x => x.Net);
        if (a.Kind == AccountKind.Sim) FillSim(s, realizedToday);
        else if (a.Kind == AccountKind.Tradovate) FillTradovate(s, realizedToday);
        else if (a.Kind == AccountKind.Bybit) FillBybit(s, bybit!, executionState.Executions);
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

    private void FillBybit(AccountSnapshot snapshot, BybitTradingSession session, IReadOnlyList<BybitExecution> executions)
    {
        snapshot.Connected = session.SnapshotReady;
        snapshot.PositionsKnown = session.PositionReady;
        snapshot.OrdersKnown = session.OrdersReady;
        var metrics = BybitAccountMetrics.From(session, TimeZone, DateTime.UtcNow);
        snapshot.OpenPnlKnown = metrics.OpenPnl.HasValue;
        snapshot.RealizedPnlKnown = metrics.ReportedRealizedToday.HasValue;
        snapshot.AnalysisKnown = metrics.HasCompleteOpeningBasis;
        snapshot.FinancialsKnown = snapshot.OpenPnlKnown && snapshot.RealizedPnlKnown && session.ExecutionsReady;
        var costs = executions.ToDictionary(fill => "bybit-" + fill.Symbol + "-" + fill.OrderId + "-" + fill.Id, StringComparer.Ordinal);
        snapshot.CostsComplete = session.ExecutionsReady && snapshot.Fills.All(fill => costs.TryGetValue(fill.Id, out var execution) && execution.HasQuoteFee);
        snapshot.Status = session.StatusText + $"; executions available since {session.ExecutionCoverageStart:yyyy-MM-dd HH:mm} UTC";
        snapshot.Status += ". USDT-contract PnL is provider-reported; wallet equity is the entire UTA account in USD. " + metrics.Warning;
        if (!snapshot.CostsComplete)
            snapshot.Status += ". Some execution fees or their currencies are unavailable/unconverted; displayed net excludes those costs.";
        foreach (var position in session.Positions)
            snapshot.Positions.Add(new PositionLine(position.Symbol, position.Symbol, position.Size, position.AvgPrice, position.UnrealizedPnl, 0, (session, position), ClosedPnlKnown: false));
        foreach (var order in session.Orders)
        {
            string status = session.IsSubmissionUnknown(order) ? "Submission unknown"
                : session.IsReplacementPending(order) ? "Amendment awaiting confirmation"
                : order.State == OrderStates.Active ? order.Filled > 0 ? "Partial" : "Active"
                : order.Status == OrderStatus.Filled ? "Filled"
                : order.Status == OrderStatus.Rejected ? "Rejected" : order.Filled > 0 ? "Canceled (partial)" : "Canceled";
            snapshot.Orders.Add(new OrderLine(order.ExchangeOrderId.Length > 0 ? order.ExchangeOrderId : "bybit-pending-" + order.Id,
                order.Time, order.Security.Code, order.Direction == OrderDirections.Buy, TypeName(order.Type), order.QuantityToFill,
                order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price, order.Filled, session.AverageFillFor(order), status,
                order.State == OrderStates.Active, (session, order)));
        }
        snapshot.Balance = session.Wallet?.Equity;
        snapshot.Available = session.Wallet?.Available;
        snapshot.MarginBalance = session.Wallet?.WalletBalance;
        snapshot.OpenPnl = metrics.OpenPnl ?? 0;
        snapshot.RealizedToday = metrics.ReportedRealizedToday ?? 0;
        snapshot.DayPnl = snapshot.RealizedToday + snapshot.OpenPnl;
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
        if (o.Ref is (BybitTradingSession bybit, Order bybitOrder))
        {
            bybit.Cancel(bybitOrder);
            return;
        }
        if (a.Kind == AccountKind.Replay && o.Ref is (ITradingAccount replay, Order replayOrder))
        {
            replay.Cancel(replayOrder);
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
    /// <returns>null when the broker accepts the request; otherwise a pending/failure explanation for the status bar.</returns>
    public async Task<string?> CloseAsync(AccountEntry a, PositionLine p)
    {
        if (p.Net == 0) return NothingToClose(p.Symbol, 0);
        if (p.Ref is SimBroker b)
        {
            b.Flatten();
            return null;
        }
        if (a.Kind == AccountKind.Replay && p.Ref is ITradingAccount replay)
        {
            replay.Flatten();
            return null;
        }
        if (a.Kind == AccountKind.Bybit && p.Ref is (BybitTradingSession bybit, BybitPosition position))
        {
            await bybit.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            if (!bybit.PositionReady) return "Bybit position is unknown; no close quantity was guessed.";
            decimal net = bybit.PositionFor(position.Symbol)?.Size ?? 0;
            if (net == 0) return NothingToClose(position.Symbol, 0);
            if (Math.Sign(net) != Math.Sign(p.Net)) return "Bybit position changed side; check the new position before closing.";
            var instrument = await bybit.GetInstrumentAsync(position.Symbol, CancellationToken.None).ConfigureAwait(false);
            bybit.Flatten(instrument);
            return bybit.LastError ?? "Bybit close requested; check position and order status for confirmation.";
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
