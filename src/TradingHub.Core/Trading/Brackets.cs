using TradingHub.Market;

namespace TradingHub.Trading;

/// <summary>Un objetivo de la plantilla: a cuántos ticks de la entrada y qué parte de la posición cierra.</summary>
public sealed class BracketTarget
{
    public int Ticks { get; set; } = 40;
    public decimal Percent { get; set; } = 100;
}

/// <summary>Stop loss y objetivos (parciales) automáticos, en ticks desde la entrada.</summary>
public sealed class BracketTemplate
{
    /// <summary>Si se ponen solos al abrir una posición o al enviar una orden de entrada.</summary>
    public bool Enabled { get; set; }

    public int StopTicks { get; set; } = 20;
    public List<BracketTarget> Targets { get; set; } = new() { new BracketTarget { Ticks = 40, Percent = 100 } };

    /// <summary>Al llenarse el primer objetivo, el stop pasa al precio de entrada.</summary>
    public bool BreakevenAfterFirstTarget { get; set; } = true;

    /// <summary>Ticks a favor sobre la entrada al mover a breakeven (cubre comisiones).</summary>
    public int BreakevenOffsetTicks { get; set; }

    public BracketTemplate Clone() => new()
    {
        Enabled = Enabled,
        StopTicks = StopTicks,
        BreakevenAfterFirstTarget = BreakevenAfterFirstTarget,
        BreakevenOffsetTicks = BreakevenOffsetTicks,
        Targets = Targets.Select(t => new BracketTarget { Ticks = t.Ticks, Percent = t.Percent }).ToList(),
    };
}

public enum BracketRole
{
    None,
    StopLoss,
    Target,
}

/// <summary>Stop y objetivos adjuntos a una orden de entrada pendiente: se envían cuando se llena.</summary>
public sealed class PendingBracket
{
    public required Order Entry { get; set; }

    /// <summary>Distancia del stop a la entrada, en ticks (null = sin stop).</summary>
    public int? StopTicks { get; set; }

    /// <summary>Objetivos: distancia a favor en ticks y porcentaje de la posición.</summary>
    public List<BracketTarget> Targets { get; } = new();

    public decimal EntryPrice => Entry.Type == OrderTypes.Stop ? Entry.TriggerPrice : Entry.Price;

    /// <summary>+1 si la entrada compra (posición larga), −1 si vende.</summary>
    public int Side => Entry.Direction == OrderDirections.Buy ? 1 : -1;
}

/// <summary>
/// Maneja el stop loss y los objetivos de la posición de una cuenta: los crea al arrastrarlos desde la
/// posición o desde una orden pendiente, los mueve, ajusta sus cantidades cuando se cierra una parte
/// (parciales), mueve el stop a breakeven después del primer objetivo y cancela lo que queda cuando la
/// posición se cierra (OCO). Funciona igual con la cuenta simulada y con brokers remotos.
/// </summary>
public sealed class BracketManager : IDisposable
{
    private readonly object _gate = new();
    private enum ExitPhase { Waiting, Working, Replacing, CancelPending, Settled }

    private sealed class ExitIntent
    {
        public required BracketRole Role;
        public required int Side;
        public required int Generation;
        public decimal Price;
        public decimal Quantity;
        public OwnedExit? Current;
        public ExitPhase Phase;
        public string? CancelIdentity;
        public decimal RequestedTotal;
        public decimal RequestedPrice;
        public decimal? FailedTotal;
        public decimal FailedPrice;
    }

    private sealed class OwnedExit
    {
        public required Order Handle;
        public required Order Latest;
        public required ExitIntent Intent;
        public decimal Booked;
    }

    private readonly List<ExitIntent> _exits = new();
    // Keep completed handles observable too: a late execution update must still resize current protection.
    private readonly List<OwnedExit> _owned = new();
    private readonly List<PendingBracket> _pending = new();
    private readonly Dictionary<PendingBracket, string?> _entryCancels = new();
    private readonly Dictionary<PendingBracket, decimal> _settledEntries = new();
    private readonly Dictionary<PendingBracket, decimal> _entryMoves = new();
    private decimal _lastVolume;
    private decimal _entryPrice;
    private decimal _exitCapacity;
    private int _targetHits;
    private int _generation;
    private bool _positionKnown;
    private bool _busy;
    private bool _syncAgain;
    private bool _targetFillObserved;
    private bool _disposeRequested;

    public BracketManager(ITradingAccount account)
    {
        Account = account;
        _positionKnown = account.PositionReady && account.OrdersReady;
        _lastVolume = _positionKnown ? account.Position.Volume : 0;
        _exitCapacity = Math.Abs(_lastVolume);
        _entryPrice = account.Position.AveragePrice;
        account.Changed += OnAccountChanged;
    }

    public ITradingAccount Account { get; }

    /// <summary>Plantilla del instrumento (stop, objetivos parciales, breakeven).</summary>
    public BracketTemplate Template { get; set; } = new();

    /// <summary>Cambió algo (para redibujar). Puede llegar desde otro hilo.</summary>
    public event Action? Changed;

    /// <summary>Avisos para la barra de estado (OCO, breakeven…). Puede llegar desde otro hilo.</summary>
    public event Action<string>? Message;

    private Instrument Inst => Account.Instrument;
    private decimal Volume => Account.Position.Volume;
    // Executions and position reports arrive independently. A newer execution bounds an older position snapshot.
    private decimal ExitQuantity => Math.Min(Math.Abs(Volume), _exitCapacity);
    private OrderDirections ExitDirection => Volume > 0 ? OrderDirections.Sell : OrderDirections.Buy;

    // ===================================================================== consultas

    private static bool Same(Order a, Order b) =>
        ReferenceEquals(a, b) || (a.ExchangeOrderId.Length > 0 && a.ExchangeOrderId == b.ExchangeOrderId);

    public static decimal PriceOf(Order o) => o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price;

    private static bool Terminal(Order order) =>
        order.Status is OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Rejected ||
        order.State is OrderStates.Done or OrderStates.Failed;

    // Absence, even from a complete listing, is not a terminal placement/cancel acknowledgment.
    private Order Live(Order tracked)
    {
        if (Terminal(tracked)) return tracked;
        return Account.ActiveOrders.FirstOrDefault(a => Same(a, tracked)) ?? tracked;
    }

    private Order Current(OwnedExit owned)
    {
        if (Terminal(owned.Handle)) return owned.Handle;
        if (Terminal(owned.Latest)) return owned.Latest;
        return owned.Latest = Account.ActiveOrders.FirstOrDefault(a => Same(a, owned.Handle) || Same(a, owned.Latest)) ?? owned.Latest;
    }

    private IEnumerable<ExitIntent> Desired(BracketRole role) =>
        _exits.Where(e => e.Role == role && e.Quantity > 0 && e.Generation == _generation);

    private IEnumerable<(ExitIntent Intent, Order Order)> Working(BracketRole role) =>
        _exits.Where(e => e.Role == role && e.Current != null)
            .Select(e => (Intent: e, Order: Current(e.Current!))).Where(e => !Terminal(e.Order));

    private string? NotReady => Account.PositionReady && Account.OrdersReady
        ? null : "Account position and working orders are still synchronizing.";

    public BracketRole RoleOf(Order o)
    {
        lock (_gate)
        {
            var owned = _owned.FirstOrDefault(e => Same(e.Handle, o) || Same(e.Latest, o));
            if (owned != null) return owned.Intent.Role;
            return BracketRole.None;
        }
    }

    /// <summary>Número del objetivo (1 = el más cercano a la entrada), o 0 si no es un objetivo.</summary>
    public int TargetNumber(Order o)
    {
        lock (_gate)
        {
            var live = Working(BracketRole.Target).Select(x => x.Order).OrderBy(x => Math.Abs(PriceOf(x) - _entryPrice)).ToList();
            int i = live.FindIndex(x => Same(x, o));
            return i + 1;
        }
    }

    public Order? StopLoss
    {
        get
        {
            lock (_gate) return Working(BracketRole.StopLoss).Select(x => x.Order).FirstOrDefault();
        }
    }

    public IReadOnlyList<Order> Targets
    {
        get
        {
            lock (_gate) return Working(BracketRole.Target).Select(x => x.Order).ToList();
        }
    }

    /// <summary>Precio de entrada de la posición abierta.</summary>
    public decimal EntryPrice => Volume != 0 ? Account.Position.AveragePrice : _entryPrice;

    public IReadOnlyList<PendingBracket> Pending
    {
        get
        {
            lock (_gate) return _pending.ToList();
        }
    }

    public PendingBracket? PendingFor(Order entry)
    {
        lock (_gate) return _pending.FirstOrDefault(p => Same(p.Entry, entry));
    }

    /// <summary>Resultado en dinero si la posición (o la cantidad dada) se cerrara en ese precio.</summary>
    public decimal PnLAt(decimal price, decimal? quantity = null, decimal? entry = null, int? side = null)
    {
        decimal e = entry ?? EntryPrice;
        int s = side ?? Math.Sign(Volume);
        decimal q = quantity ?? Math.Abs(Volume);
        return (price - e) * q * s * Inst.PointValue;
    }

    // ===================================================================== acciones

    /// <summary>Corre una acción con la cuenta bloqueada y después sincroniza (OCO, parciales).</summary>
    private T Run<T>(Func<T> op)
    {
        T result;
        lock (_gate)
        {
            bool outer = !_busy;
            _busy = true;
            try
            {
                result = op();
            }
            finally
            {
                if (outer) _busy = false;
            }

            if (outer) SyncLoop();
        }

        Changed?.Invoke();
        return result;
    }

    private decimal RoundQty(decimal q)
    {
        decimal step = Inst.VolumeStep > 0 ? Inst.VolumeStep : 1;
        return Math.Floor(q / step) * step;
    }

    /// <summary>Error si el precio está del lado equivocado (para un largo: stop abajo, objetivo arriba).</summary>
    private string? Validate(BracketRole role, decimal price, int side, decimal reference)
    {
        if (reference <= 0) return null;
        bool below = price < reference;
        bool above = price > reference;
        return role switch
        {
            BracketRole.StopLoss when side > 0 && !below => "A long-position stop must be below the current price.",
            BracketRole.StopLoss when side < 0 && !above => "A short-position stop must be above the current price.",
            BracketRole.Target when side > 0 && !above => "A long-position target must be above the current price.",
            BracketRole.Target when side < 0 && !below => "A short-position target must be below the current price.",
            _ => null,
        };
    }

    /// <summary>Arrastre desde la línea de la posición: hacia la ganancia crea un objetivo, hacia la pérdida el stop.</summary>
    public string DropFromPosition(decimal price, BracketRole role = BracketRole.None)
    {
        if (NotReady is { } pending) return pending;
        decimal v = Volume;
        if (v == 0) return "No open position.";
        if (role == BracketRole.None)
        {
            decimal last = Account.LastPrice > 0 ? Account.LastPrice : EntryPrice;
            if (price == last) return "Drop above or below the current price.";
            role = (price > last) == (v > 0) ? BracketRole.Target : BracketRole.StopLoss;
        }

        return role == BracketRole.StopLoss ? SetStopLoss(price) : AddTarget(price);
    }

    public string SetStopLoss(decimal price) => Run(() =>
    {
        if (NotReady is { } pending) return pending;
        decimal v = Volume;
        if (v == 0 || ExitQuantity == 0) return "No open position.";
        price = Inst.RoundPrice(price);
        if (Validate(BracketRole.StopLoss, price, Math.Sign(v), Account.LastPrice) is { } err) return err;
        var stop = Desired(BracketRole.StopLoss).FirstOrDefault();
        if (stop != null) ChangeExit(stop, price, ExitQuantity);
        else QueueExit(BracketRole.StopLoss, Math.Sign(v), ExitQuantity, price);

        return $"Stop loss at {Inst.FormatPrice(price)} ({FormatMoney(PnLAt(price))})";
    });

    /// <summary>
    /// Agrega un objetivo. Sin cantidad: cubre lo que falta de la posición; si los objetivos ya la cubren
    /// toda, divide el más grande a la mitad (parcial).
    /// </summary>
    public string AddTarget(decimal price, decimal? quantity = null) => Run(() =>
    {
        if (NotReady is { } pending) return pending;
        decimal v = Volume;
        if (v == 0 || ExitQuantity == 0) return "No open position.";
        price = Inst.RoundPrice(price);
        if (Validate(BracketRole.Target, price, Math.Sign(v), Account.LastPrice) is { } err) return err;
        decimal abs = ExitQuantity;
        var live = Desired(BracketRole.Target).ToList();
        decimal covered = live.Sum(t => t.Quantity);
        decimal qty;
        if (quantity is { } q)
        {
            qty = RoundQty(Math.Min(q, abs));
            ReduceTargets(abs - qty, null);
        }
        else if (covered < abs)
        {
            qty = abs - covered;
        }
        else
        {
            var biggest = live.OrderByDescending(t => t.Quantity).First();
            decimal half = RoundQty(biggest.Quantity / 2);
            if (half <= 0) return "Quantity cannot be split further: move the existing target.";
            ChangeExit(biggest, biggest.Price, biggest.Quantity - half);
            qty = half;
        }

        if (qty <= 0) return "Target quantity became zero.";
        QueueExit(BracketRole.Target, Math.Sign(v), qty, price);
        return $"Target for {Fmt(qty)} on {Inst.FormatPrice(price)} ({FormatMoney(PnLAt(price, qty))})";
    });

    /// <summary>Mueve una orden: stop, objetivo, entrada con brackets o cualquier otra.</summary>
    public string Move(Order order, decimal price) => Run(() =>
    {
        price = Inst.RoundPrice(price);
        var intent = _exits.FirstOrDefault(e => e.Current is { } owned && Same(owned.Handle, order));
        if (intent != null)
        {
            if (NotReady is { } pending) return pending;
            if (intent.Quantity <= 0) return "Order cancellation is still awaiting confirmation.";
            if (Validate(intent.Role, price, Math.Sign(Volume), Account.LastPrice) is { } err) return err;
            ChangeExit(intent, price, intent.Quantity);
            return $"{(intent.Role == BracketRole.StopLoss ? "Stop" : "Target")} moved to {Inst.FormatPrice(price)}";
        }

        var pb = _pending.FirstOrDefault(p => Same(p.Entry, order));
        if (pb != null)
        {
            if (NotReady is { } pending) return pending;
            if (_entryCancels.ContainsKey(pb)) return "Order cancellation is still awaiting confirmation.";
            _entryMoves[pb] = price;
            return $"Order move requested to {Inst.FormatPrice(price)} (stop and targets follow)";
        }

        Account.Modify(order, price);
        return $"Order moved to {Inst.FormatPrice(price)}";
    });

    /// <summary>Cambia la cantidad de un objetivo a una fracción de la posición (los demás se achican si hace falta).</summary>
    public string SetTargetFraction(Order target, decimal fraction) => Run(() =>
    {
        if (NotReady is { } pending) return pending;
        decimal abs = ExitQuantity;
        var tracked = Desired(BracketRole.Target).FirstOrDefault(t => t.Current is { } owned && Same(owned.Handle, target));
        if (abs == 0 || tracked == null) return "Not an active target.";
        decimal qty = RoundQty(abs * fraction);
        if (qty <= 0) return "This fraction rounds to zero for the position quantity.";
        ChangeExit(tracked, tracked.Price, qty);
        ReduceTargets(abs, tracked);
        return $"Target now closes {Fmt(qty)}";
    });

    public string CancelBracket(Order order) => Run(() =>
    {
        var intent = _exits.FirstOrDefault(e => e.Current is { } owned && Same(owned.Handle, order));
        if (intent != null)
        {
            intent.Quantity = 0;
            intent.CancelIdentity = null;
            return "Order cancellation requested";
        }

        var pb = _pending.FirstOrDefault(p => Same(p.Entry, order));
        if (pb != null)
        {
            _entryMoves.Remove(pb);
            _entryCancels[pb] = null;
        }
        else Account.Cancel(order);
        return "Order cancellation requested";
    });

    /// <summary>Lleva el stop al precio de entrada (más el margen de la plantilla).</summary>
    public string MoveStopToBreakeven() => Run(MoveStopToBreakevenCore);

    private string MoveStopToBreakevenCore()
    {
        if (NotReady is { } pending) return pending;
        decimal v = Volume;
        if (v == 0 || ExitQuantity == 0) return "No open position.";
        decimal be = Inst.RoundPrice(EntryPrice + Math.Sign(v) * Template.BreakevenOffsetTicks * Inst.TickSize);
        if (Validate(BracketRole.StopLoss, be, Math.Sign(v), Account.LastPrice) != null)
            return "Not enough profit yet to move the stop to breakeven.";
        var stop = Desired(BracketRole.StopLoss).FirstOrDefault();
        if (stop != null) ChangeExit(stop, be, ExitQuantity);
        else QueueExit(BracketRole.StopLoss, Math.Sign(v), ExitQuantity, be);
        return $"Stop at breakeven ({Inst.FormatPrice(be)})";
    }

    /// <summary>Cierra a mercado una parte de la posición (0.5 = la mitad).</summary>
    public string ClosePartial(decimal fraction) => Run(() =>
    {
        if (NotReady is { } pending) return pending;
        decimal abs = ExitQuantity;
        if (abs == 0) return "No open position.";
        decimal qty = fraction >= 1 ? abs : RoundQty(abs * fraction);
        if (qty <= 0) qty = Math.Min(abs, Inst.VolumeStep > 0 ? Inst.VolumeStep : 1);
        Account.Market(ExitDirection, qty, "Partial");
        return $"Closed {Fmt(qty)} of {Fmt(abs)} at market";
    });

    /// <summary>Pone el stop y los objetivos de la plantilla a la posición abierta.</summary>
    public string ApplyTemplate() => Run(ApplyTemplateCore);

    private string ApplyTemplateCore()
    {
        if (NotReady is { } pending) return pending;
        decimal v = Volume;
        if (v == 0) return "No open position.";
        CancelManaged();
        return PlaceBrackets(Math.Sign(v), EntryPrice, Template.StopTicks, Template.Targets);
    }

    private string PlaceBrackets(int side, decimal entry, int? stopTicks, IReadOnlyList<BracketTarget> targets)
    {
        decimal abs = ExitQuantity;
        decimal last = Account.LastPrice;
        var skipped = new List<string>();
        int placed = 0;

        if (stopTicks is > 0)
        {
            decimal sp = Inst.RoundPrice(entry - side * stopTicks.Value * Inst.TickSize);
            if (Validate(BracketRole.StopLoss, sp, side, last) == null)
            {
                QueueExit(BracketRole.StopLoss, side, abs, sp);
                placed++;
            }
            else skipped.Add("stop");
        }

        var valid = targets.Where(t => t.Ticks > 0 && t.Percent > 0).OrderBy(t => t.Ticks).ToList();
        decimal remaining = abs;
        decimal totalPct = valid.Sum(t => t.Percent);
        for (int i = 0; i < valid.Count && remaining > 0; i++)
        {
            var t = valid[i];
            bool lastOne = i == valid.Count - 1;
            // el último objetivo se lleva el resto si los porcentajes suman 100
            decimal qty = lastOne && totalPct >= 100 ? remaining : RoundQty(abs * t.Percent / 100m);
            qty = Math.Min(qty, remaining);
            if (qty <= 0) continue;
            decimal tp = Inst.RoundPrice(entry + side * t.Ticks * Inst.TickSize);
            if (Validate(BracketRole.Target, tp, side, last) != null)
            {
                skipped.Add($"target {i + 1}");
                continue;
            }

            QueueExit(BracketRole.Target, side, qty, tp);
            remaining -= qty;
            placed++;
        }

        string msg = placed > 0 ? $"Stop and targets placed ({placed} orders)" : "No stop or target placed";
        if (skipped.Count > 0) msg += $" · price already passed: {string.Join(", ", skipped)}";
        return msg;
    }

    // ===================================================================== entradas pendientes

    /// <summary>Adjunta la plantilla a una orden de entrada: el stop y los objetivos se envían al llenarse.</summary>
    public PendingBracket AttachTemplate(Order entry) => Run(() =>
    {
        var pb = _pending.FirstOrDefault(p => Same(p.Entry, entry));
        if (pb == null)
        {
            pb = new PendingBracket { Entry = entry };
            _pending.Add(pb);
        }

        pb.StopTicks = Template.StopTicks > 0 ? Template.StopTicks : null;
        pb.Targets.Clear();
        pb.Targets.AddRange(Template.Targets.Where(t => t.Ticks > 0 && t.Percent > 0).Select(t => new BracketTarget { Ticks = t.Ticks, Percent = t.Percent }));
        return pb;
    });

    /// <summary>
    /// Arrastre desde una orden de entrada pendiente: del lado de la pérdida define el stop, del lado de
    /// la ganancia agrega un objetivo (si ya hay, divide el más grande).
    /// </summary>
    public string AttachToEntry(Order entry, decimal price, BracketRole role = BracketRole.None) => Run(() =>
    {
        price = Inst.RoundPrice(price);
        var pb = _pending.FirstOrDefault(p => Same(p.Entry, entry));
        if (pb == null)
        {
            pb = new PendingBracket { Entry = entry };
            _pending.Add(pb);
        }

        int ticks = (int)Math.Round((price - pb.EntryPrice) / Inst.TickSize) * pb.Side;
        if (ticks == 0) return "Drop above or below the order.";
        if (role == BracketRole.None) role = ticks > 0 ? BracketRole.Target : BracketRole.StopLoss;
        if (role == BracketRole.StopLoss)
        {
            if (ticks >= 0) return "Stop must be on the order's loss side.";
            pb.StopTicks = -ticks;
            return $"Stop attached at {-ticks} ticks (submitted when the entry fills)";
        }

        if (ticks <= 0) return "Target must be on the order's profit side.";
        if (pb.Targets.Count == 0) pb.Targets.Add(new BracketTarget { Ticks = ticks, Percent = 100 });
        else
        {
            var big = pb.Targets.OrderByDescending(t => t.Percent).First();
            decimal half = Math.Round(big.Percent / 2, 1);
            big.Percent -= half;
            pb.Targets.Add(new BracketTarget { Ticks = ticks, Percent = half });
        }

        return $"Target attached at {ticks} ticks (submitted when the entry fills)";
    });

    /// <summary>Mueve el stop (index −1) o un objetivo adjunto a una entrada pendiente.</summary>
    public string MovePending(PendingBracket pb, int index, decimal price) => Run(() =>
    {
        int ticks = (int)Math.Round((Inst.RoundPrice(price) - pb.EntryPrice) / Inst.TickSize) * pb.Side;
        if (index < 0)
        {
            if (ticks >= 0) return "Stop must be on the order's loss side.";
            pb.StopTicks = -ticks;
            return $"Stop attached at {-ticks} ticks";
        }

        if (index >= pb.Targets.Count) return "";
        if (ticks <= 0) return "Target must be on the order's profit side.";
        pb.Targets[index].Ticks = ticks;
        return $"Target attached at {ticks} ticks";
    });

    public void RemovePending(PendingBracket pb, int index) => Run(() =>
    {
        if (index < 0) pb.StopTicks = null;
        else if (index < pb.Targets.Count)
        {
            var removed = pb.Targets[index];
            pb.Targets.RemoveAt(index);
            if (pb.Targets.Count > 0) pb.Targets.OrderByDescending(t => t.Percent).First().Percent += removed.Percent;
        }

        return 0;
    });

    // ===================================================================== sincronización

    private void OnAccountChanged()
    {
        lock (_gate)
        {
            if (_busy)
            {
                _syncAgain = true;
                return;
            }
            SyncLoop();
        }

        Changed?.Invoke();
    }

    private void SyncLoop()
    {
        _busy = true;
        try
        {
            do
            {
                _syncAgain = false;
                Sync();
            }
            while (_syncAgain);
        }
        catch (Exception ex)
        {
            Message?.Invoke("Stop/target error: " + ex.Message);
        }
        finally
        {
            _busy = false;
            DetachWhenSettled();
        }
    }

    private void Sync()
    {
        ObserveExits();
        CancelPendingEntries();
        if (NotReady != null)
        {
            ReconcileExits(allowPlacement: false);
            return;
        }
        MovePendingEntries();

        decimal v = Volume;
        decimal before = _positionKnown ? _lastVolume : 0;
        _positionKnown = true;
        PendingBracket? filledEntry = null;
        foreach (var (pb, booked) in _settledEntries.ToList())
        {
            var entry = Live(pb.Entry);
            if (entry.Filled <= booked || v == 0 || Math.Sign(v) != pb.Side) continue;
            filledEntry = pb;
            _settledEntries[pb] = entry.Filled;
        }
        foreach (var pb in _pending.ToList())
        {
            var entry = Live(pb.Entry);
            bool filled = entry.Filled > 0 || entry.Status == OrderStatus.Filled;
            if (filled && v != 0 && Math.Sign(v) == pb.Side) filledEntry = pb;
            // A fill may precede its position update. Keep the attachment until it can protect that position.
            if (Terminal(entry) && (!filled || (v != 0 && Math.Sign(v) == pb.Side)))
            {
                _pending.Remove(pb);
                _entryCancels.Remove(pb);
                _settledEntries[pb] = Math.Max(0, entry.Filled);
            }
        }

        if (before != 0 && v == 0)
        {
            _exitCapacity = 0;
            if (CancelManaged() > 0) Message?.Invoke("Position closed: remaining stop and target cancellation requested.");
            _targetHits = 0;
            _targetFillObserved = false;
        }
        else if (v != 0 && (before == 0 || Math.Sign(v) != Math.Sign(before)))
        {
            CancelManaged();
            _generation++;
            _exitCapacity = Math.Abs(v);
            _entryPrice = Account.Position.AveragePrice;
            _targetHits = 0;
            _targetFillObserved = false;
            var source = filledEntry ?? _pending.FirstOrDefault(p =>
                p.Side == Math.Sign(v) && Live(p.Entry).Status == OrderStatus.PartlyFilled);
            if (source != null && (source.StopTicks != null || source.Targets.Count > 0))
            {
                string message = PlaceBrackets(source.Side, _entryPrice, source.StopTicks, source.Targets);
                Message?.Invoke(message);
            }
            else if (Template.Enabled)
            {
                string message = PlaceBrackets(Math.Sign(v), _entryPrice, Template.StopTicks, Template.Targets);
                Message?.Invoke(message);
            }
        }
        else if (v != 0 && Math.Abs(v) > Math.Abs(before))
        {
            // Only an observed increase can enlarge the execution bound; preserve any fill not yet reflected in PnL.
            decimal unreported = Math.Max(0, Math.Abs(before) - _exitCapacity);
            _exitCapacity = Math.Max(0, Math.Abs(v) - unreported);
            _entryPrice = Account.Position.AveragePrice;
            bool none = !Desired(BracketRole.StopLoss).Any() && !Desired(BracketRole.Target).Any();
            if (none && filledEntry != null && (filledEntry.StopTicks != null || filledEntry.Targets.Count > 0))
            {
                string message = PlaceBrackets(filledEntry.Side, _entryPrice, filledEntry.StopTicks, filledEntry.Targets);
                Message?.Invoke(message);
            }
            else GrowTargets(ExitQuantity);
        }

        if (v != 0)
        {
            ResizeToPosition();
            if (_targetFillObserved)
            {
                _targetFillObserved = false;
                bool first = _targetHits++ == 0;
                if (first && Template.BreakevenAfterFirstTarget && Desired(BracketRole.StopLoss).Any())
                {
                    string message = MoveStopToBreakevenCore();
                    Message?.Invoke($"Target filled · {message}");
                }
                else Message?.Invoke("Target filled: stop adjusted to the remaining position.");
            }
        }

        _lastVolume = v;
        ReconcileExits(allowPlacement: true);
    }

    private void ObserveExits()
    {
        foreach (var owned in _owned)
        {
            var order = Current(owned);
            decimal filled = Math.Max(0, order.Filled);
            if (order.Status == OrderStatus.Filled) filled = Math.Max(filled, order.QuantityToFill);
            decimal delta = Math.Max(0, filled - owned.Booked);
            if (delta > 0)
            {
                owned.Booked = filled;
                var intent = owned.Intent;
                intent.Quantity = Math.Max(0, intent.Quantity - delta);
                if (intent.Side == Math.Sign(_lastVolume))
                    _exitCapacity = Math.Max(0, _exitCapacity - delta);
                if (intent.Role == BracketRole.Target && intent.Generation == _generation)
                    _targetFillObserved = true;
            }
        }

        foreach (var intent in _exits)
        {
            if (intent.Current is not { } owned) continue;
            var order = Current(owned);
            if (Account is ITrackedOrderReplacement tracked && tracked.IsReplacementPending(owned.Handle)) continue;
            if (intent.Phase == ExitPhase.Replacing && !Terminal(order))
            {
                intent.Phase = ExitPhase.Working;
                if (order.QuantityToFill != intent.RequestedTotal || PriceOf(order) != intent.RequestedPrice)
                {
                    intent.FailedTotal = intent.RequestedTotal;
                    intent.FailedPrice = intent.RequestedPrice;
                    Message?.Invoke("Stop/target amendment was not confirmed: existing working protection remains owned.");
                }
            }
            if (!Terminal(order)) continue;
            bool replacement = intent.Phase == ExitPhase.CancelPending && intent.Quantity > 0;
            intent.Current = null;
            intent.CancelIdentity = null;
            intent.Phase = replacement ? ExitPhase.Waiting : ExitPhase.Settled;
            if (!replacement) intent.Quantity = 0;
        }
    }

    private void QueueExit(BracketRole role, int side, decimal quantity, decimal price) =>
        _exits.Add(new ExitIntent
        {
            Role = role, Side = side, Generation = _generation, Price = price,
            Quantity = quantity, Phase = ExitPhase.Waiting,
        });

    private static void ChangeExit(ExitIntent intent, decimal price, decimal quantity)
    {
        intent.Price = price;
        intent.Quantity = quantity;
        intent.FailedTotal = null;
    }

    private void ResizeToPosition()
    {
        decimal abs = ExitQuantity;
        foreach (var stop in Desired(BracketRole.StopLoss)) stop.Quantity = abs;
        ReduceTargets(abs, null);
    }

    private void ReduceTargets(decimal max, ExitIntent? keep)
    {
        var targets = Desired(BracketRole.Target).OrderByDescending(t => Math.Abs(t.Price - _entryPrice)).ToList();
        decimal total = targets.Sum(t => t.Quantity);
        foreach (var target in targets)
        {
            if (total <= max) break;
            if (ReferenceEquals(target, keep)) continue;
            decimal reduction = Math.Min(target.Quantity, total - max);
            target.Quantity -= reduction;
            total -= reduction;
        }
    }

    private void GrowTargets(decimal abs)
    {
        var targets = Desired(BracketRole.Target).ToList();
        decimal total = targets.Sum(t => t.Quantity);
        if (targets.Count == 0 || total >= abs) return;
        targets.OrderByDescending(t => Math.Abs(t.Price - _entryPrice)).First().Quantity += abs - total;
    }

    private int CancelManaged()
    {
        int count = 0;
        foreach (var intent in _exits)
        {
            if (intent.Quantity > 0) count++;
            intent.Quantity = 0;
        }
        return count;
    }

    private void CancelPendingEntries()
    {
        foreach (var (pb, identity) in _entryCancels.ToList())
        {
            var order = Live(pb.Entry);
            if (Terminal(order)) continue;
            string current = Identity(order);
            if (identity == current) continue;
            _entryCancels[pb] = current;
            try { Account.Cancel(order); }
            catch
            {
                _entryCancels[pb] = null;
                throw;
            }
            _syncAgain = true;
        }
    }

    private void MovePendingEntries()
    {
        foreach (var (pb, price) in _entryMoves.ToList())
        {
            var order = Live(pb.Entry);
            if (Account is ITrackedOrderReplacement tracked && tracked.IsReplacementPending(order)) continue;
            _entryMoves.Remove(pb);
            if (Terminal(order)) continue;
            if (Account is ITrackedOrderReplacement) Account.Replace(order, price, order.Unfilled);
            else Account.Modify(order, price);
            _syncAgain = true;
        }
    }

    private static string Identity(Order order) => order.ExchangeOrderId.Length > 0
        ? order.ExchangeOrderId : $"local:{order.Id}";

    private bool PendingChange(ExitIntent intent) =>
        intent.Phase is ExitPhase.CancelPending or ExitPhase.Replacing ||
        (intent.Current is { } owned && Account is ITrackedOrderReplacement tracked && tracked.IsReplacementPending(owned.Handle));

    private void ReconcileExits(bool allowPlacement)
    {
        // Optional tracked replacements preserve native amendments and broker-owned cancel/replace sequencing.
        // Settle target changes first, retaining a working stop until those capacity changes are confirmed.
        foreach (var intent in _exits.OrderBy(e => e.Role == BracketRole.Target ? 0 : 1).ToList())
        {
            if (intent.Current is not { } owned) continue;
            var order = Current(owned);
            if (Terminal(order)) continue;
            if (intent.Phase == ExitPhase.Replacing && intent.Quantity > 0) continue;
            bool change = intent.Quantity != order.Unfilled || intent.Price != PriceOf(order);
            if (intent.Phase != ExitPhase.CancelPending && !change) continue;
            if (intent.Phase != ExitPhase.CancelPending)
            {
                if (!allowPlacement && intent.Quantity > 0) continue;
                if (intent.Role == BracketRole.StopLoss && intent.Quantity > 0 &&
                    _exits.Any(e => e.Role == BracketRole.Target && PendingChange(e)))
                    continue;
                if (Account is ITrackedOrderReplacement tracked && intent.Quantity > 0)
                {
                    if (tracked.IsReplacementPending(owned.Handle)) continue;
                    decimal total = order.Filled + intent.Quantity;
                    if (intent.FailedTotal == total && intent.FailedPrice == intent.Price) continue;
                    intent.RequestedTotal = total;
                    intent.RequestedPrice = intent.Price;
                    intent.Phase = ExitPhase.Replacing;
                    Account.Replace(order, intent.Price, intent.Quantity);
                    _syncAgain = true;
                    continue;
                }
                intent.Phase = ExitPhase.CancelPending;
            }
            string identity = Identity(order);
            if (intent.CancelIdentity == identity) continue;
            intent.CancelIdentity = identity;
            try { Account.Cancel(order); }
            catch
            {
                intent.CancelIdentity = null;
                throw;
            }
            _syncAgain = true;
        }

        if (!allowPlacement || Volume == 0) return;
        // A prior position's unresolved exits must never be mistaken for a new position's protection.
        if (_exits.Any(e => e.Generation != _generation && e.Current != null)) return;
        foreach (var intent in _exits.Where(e => e.Phase == ExitPhase.Waiting && e.Quantity > 0).ToList())
        {
            if (intent.Generation != _generation || intent.Side != Math.Sign(Volume)) continue;
            if (_exits.Any(e => e.Role == intent.Role && PendingChange(e))) continue;
            decimal quantity = Math.Min(intent.Quantity, ExitQuantity);
            if (intent.Role == BracketRole.Target)
            {
                decimal working = Working(BracketRole.Target).Sum(e => e.Order.Unfilled);
                quantity = Math.Min(quantity, Math.Max(0, ExitQuantity - working));
            }
            if (quantity <= 0) continue;
            var order = Account.Exit(intent.Role == BracketRole.StopLoss ? OrderTypes.Stop : OrderTypes.Limit,
                intent.Side > 0 ? OrderDirections.Sell : OrderDirections.Buy, quantity, intent.Price,
                intent.Role == BracketRole.StopLoss ? "SL" : "TP");
            var owned = new OwnedExit { Handle = order, Latest = order, Intent = intent };
            _owned.Add(owned);
            intent.Current = owned;
            intent.Phase = ExitPhase.Working;
            _syncAgain = true;
        }
    }

    private static string Fmt(decimal q) => q.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatMoney(decimal v) => v.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture);

    private void DetachWhenSettled()
    {
        if (!_disposeRequested || _busy || _entryCancels.Count > 0 || _entryMoves.Count > 0) return;
        foreach (var intent in _exits)
        {
            if (PendingChange(intent) || (intent.Phase == ExitPhase.Waiting && intent.Quantity > 0)) return;
            if (Account is not ITrackedOrderReplacement && intent.Current is { } owned &&
                !Terminal(Current(owned)) && owned.Handle.ExchangeOrderId.Length == 0) return;
        }
        Account.Changed -= OnAccountChanged;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposeRequested = true;
            DetachWhenSettled();
        }
    }
}
