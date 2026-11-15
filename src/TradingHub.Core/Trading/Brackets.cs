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
    // Dissolving: the venue cancelled a linked leg nobody here asked to cancel; held until its cause is confirmed.
    private enum ExitPhase { Waiting, Working, Replacing, CancelPending, Settled, Dissolving }

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
        // The cancelled linked leg whose cause is being confirmed, and when it was seen cancelled.
        public OwnedExit? Dissolved;
        public DateTime DissolvedAt;
    }

    private sealed class OwnedExit
    {
        public required Order Handle;
        public required Order Latest;
        public required ExitIntent Intent;
        public decimal Booked;
        // Sent as one venue-linked stop/target group; Partner is the other leg of that group.
        public bool Linked;
        public OwnedExit? Partner;
        // This app asked the venue to remove this leg (not a resize): a venue cancelling its partner is then a dissolved link.
        public bool RemovalRequested;
        // The partner's cumulative execution while this leg was working and already sized to the remaining position:
        // only executions beyond it can explain a later venue cancellation of this leg.
        public decimal PartnerBaseline;
        // The reported |position| when that baseline was taken: a replacement waits until the reported position has
        // fallen by the partner's later executions, so a stale report can never size it.
        public decimal VolumeAtBaseline;
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
    // The venue refused or dissolved this position's linked pair: app-managed protection until the next position.
    private bool _linkFallback;
    private string? _protectionNote;

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

    /// <summary>Time source for the bounded wait on a dissolved exchange-side link (replaceable for deterministic checks).</summary>
    public Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

    /// <summary>How long a venue-cancelled linked leg waits for its cause (the partner's fill and position report) before
    /// the position is reported as having a protection gap.</summary>
    public TimeSpan LinkDissolutionWait { get; set; } = TimeSpan.FromSeconds(30);

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

    /// <summary>
    /// How the working stop and targets are held. Exchange-side only when every working leg was sent linked and the venue
    /// reports its group; an unacknowledged or partial link is reported as app-managed.
    /// </summary>
    public ProtectionMode Mode
    {
        get
        {
            lock (_gate)
            {
                var working = Working(BracketRole.StopLoss).Concat(Working(BracketRole.Target)).ToList();
                if (working.Count == 0) return ProtectionMode.None;
                return Account is INativeProtection native && working.Count == 2 &&
                    working.All(w => w.Intent.Current is { Linked: true, Partner: not null } && native.ProtectionGroupOf(w.Order) != null)
                    ? ProtectionMode.ExchangeSide : ProtectionMode.AppManaged;
            }
        }
    }

    /// <summary>Why opted-in exchange-side protection is not in use for this position (venue refusal, dissolved link or an
    /// unrepresentable layout); null when nothing fell back.</summary>
    public string? ProtectionNote
    {
        get
        {
            lock (_gate) return _protectionNote ?? (Account as INativeProtection)?.NativeProtectionFault;
        }
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

        OrderActions.Reprice(Account, order, price);
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
        OrderActions.Manual(Account, OrderTypes.Market, ExitDirection, qty, comment: "Partial");
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
            _protectionNote = null;
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
            _linkFallback = false;
            _protectionNote = null;
            var source = filledEntry ?? _pending.FirstOrDefault(p =>
                p.Side == Math.Sign(v) && Live(p.Entry).Status == OrderStatus.PartlyFilled);
            // Protection the venue already holds for this position is adopted, never placed a second time.
            if (AdoptNativeProtection()) { }
            else if (source != null && (source.StopTicks != null || source.Targets.Count > 0))
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
            if (none && AdoptNativeProtection()) { }
            else if (none && filledEntry != null && (filledEntry.StopTicks != null || filledEntry.Targets.Count > 0))
            {
                string message = PlaceBrackets(filledEntry.Side, _entryPrice, filledEntry.StopTicks, filledEntry.Targets);
                Message?.Invoke(message);
            }
            else GrowTargets(ExitQuantity);
        }

        if (v != 0)
        {
            AdoptNativeProtection();
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
            decimal filled = Executed(order);
            decimal delta = Math.Max(0, filled - owned.Booked);
            if (delta > 0)
            {
                owned.Booked = filled;
                var intent = owned.Intent;
                decimal remaining = Math.Max(0, intent.Quantity - delta);
                // A triggered stop's executing child is sized from the position. A position report that led this
                // execution already shrank the stop's intent; counting the execution again must not release the order closing the position.
                // Plain stops keep the full decrement: a broker without native reduce-only must cancel an oversized remainder.
                if (ReferenceEquals(intent.Current, owned) && !Terminal(order) && ExecutingStopChild(intent, order))
                    remaining = Math.Max(remaining, Math.Min(intent.Quantity, order.Unfilled));
                intent.Quantity = remaining;
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
            if (!Terminal(order))
            {
                if (owned.Partner is { } seen && order.Unfilled <= ExitQuantity)
                {
                    owned.PartnerBaseline = Executed(Current(seen));
                    owned.VolumeAtBaseline = Math.Abs(Volume);
                }
                continue;
            }
            bool replacement = intent.Phase == ExitPhase.CancelPending && intent.Quantity > 0;
            bool refused = !replacement && LinkRefused(intent, owned, order);
            bool dissolving = !replacement && !refused && LinkCancelledByVenue(intent, owned, order);
            intent.Current = null;
            intent.CancelIdentity = null;
            intent.Phase = replacement || refused ? ExitPhase.Waiting : dissolving ? ExitPhase.Dissolving : ExitPhase.Settled;
            if (!replacement && !refused && !dissolving) intent.Quantity = 0;
            if (refused)
            {
                _linkFallback = true;
                _protectionNote = "The venue refused exchange-side protection: app-managed stop and target are used for this position.";
                Message?.Invoke(_protectionNote);
            }
            if (dissolving)
            {
                intent.Dissolved = owned;
                intent.DissolvedAt = Clock();
            }
        }
        ResolveDissolutions();
    }

    /// <summary>
    /// Decides each venue-cancelled linked leg on the evidence available now, and again on later syncs: venue events
    /// (cancellation, the partner's fill, the position) arrive separately and in any order. The leg is replaced only for a
    /// confirmed cause and only once the reported position agrees with the booked fills, sized from that position.
    /// </summary>
    private void ResolveDissolutions()
    {
        foreach (var intent in _exits.Where(e => e.Phase == ExitPhase.Dissolving).ToList())
        {
            var leg = intent.Dissolved!;
            var partner = leg.Partner;
            var other = partner != null ? Current(partner) : null;
            bool stopping = intent.Role == BracketRole.StopLoss;
            if (Volume == 0 || ExitQuantity == 0 || intent.Generation != _generation || intent.Side != Math.Sign(Volume) ||
                partner == null || other == null)
            {
                Settle(intent, gap: null); // the position closed: nothing left to protect
                continue;
            }
            decimal partnerExecuted = Executed(other) - leg.PartnerBaseline;
            bool partnerDone = Terminal(other) && Executed(other) <= partner.Booked;
            bool cause;
            if (partner.RemovalRequested || CancelRequested(partner))
                // This app removed the partner (brackets, DOM, order list): await its outcome, then this leg is needed.
                cause = partnerDone && (other.Status == OrderStatus.Canceled || partnerExecuted > 0);
            else if (partnerExecuted > 0) cause = true; // the partner executed (even completely) after this leg was sized
            else if (Terminal(other)) { Settle(intent, gap: null); continue; } // both cancelled, nobody here asked: respected
            else cause = false;

            // Confirmed once the fills are booked and the reported position has fallen by them; sized from that position.
            bool confirmed = NotReady == null && Executed(other) <= partner.Booked &&
                Math.Abs(Volume) <= leg.VolumeAtBaseline - Math.Max(0, partnerExecuted);
            if (cause && confirmed)
            {
                intent.Dissolved = null;
                intent.Phase = ExitPhase.Waiting;
                intent.Quantity = ExitQuantity;
                _linkFallback = true;
                _protectionNote = "The venue cancelled one linked leg while the position remains open: app-managed protection replaces it.";
                Message?.Invoke(_protectionNote);
                continue;
            }
            if (Clock() - intent.DissolvedAt > LinkDissolutionWait)
            {
                Settle(intent, stopping
                    ? "Protection gap: the venue cancelled the linked stop without a confirmed cause; no stop is working for this position."
                    : "Protection gap: the venue cancelled the linked target without a confirmed cause; no target is working for this position.");
                Message?.Invoke(_protectionNote!);
                continue;
            }
            _protectionNote = stopping
                ? "Protection gap: the venue cancelled the linked stop; waiting for its fills and position to confirm the remaining position."
                : "Protection gap: the venue cancelled the linked target; waiting for its fills and position to confirm the remaining position.";
        }
    }

    private void Settle(ExitIntent intent, string? gap)
    {
        intent.Dissolved = null;
        intent.Phase = ExitPhase.Settled;
        intent.Quantity = 0;
        if (gap != null) _protectionNote = gap;
        else if (_protectionNote?.StartsWith("Protection gap:", StringComparison.Ordinal) == true &&
            !_exits.Any(e => e.Phase == ExitPhase.Dissolving)) _protectionNote = null;
    }

    private bool CancelRequested(OwnedExit owned) => Account is INativeProtection native &&
        (native.CancelRequested(owned.Handle) || native.CancelRequested(owned.Latest));

    private void QueueExit(BracketRole role, int side, decimal quantity, decimal price) =>
        _exits.Add(new ExitIntent
        {
            Role = role, Side = side, Generation = _generation, Price = price,
            Quantity = quantity, Phase = ExitPhase.Waiting,
        });

    // An explicit change (a new stop price, breakeven, a resize) ends a dissolved link's wait: the leg is placed at once as
    // app-managed protection.
    private void ChangeExit(ExitIntent intent, decimal price, decimal quantity)
    {
        intent.Price = price;
        intent.Quantity = quantity;
        intent.FailedTotal = null;
        if (intent.Phase != ExitPhase.Dissolving) return;
        intent.Dissolved = null;
        intent.Phase = ExitPhase.Waiting;
        _linkFallback = true;
        if (_protectionNote?.StartsWith("Protection gap:", StringComparison.Ordinal) == true &&
            !_exits.Any(e => e.Phase == ExitPhase.Dissolving)) _protectionNote = null;
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
            if (Account is ITrackedOrderReplacement) OrderActions.Replace(Account, order, price, order.Unfilled);
            else OrderActions.Reprice(Account, order, price);
            _syncAgain = true;
        }
    }

    private static string Identity(Order order) => order.ExchangeOrderId.Length > 0
        ? order.ExchangeOrderId : $"local:{order.Id}";

    private bool PendingChange(ExitIntent intent) =>
        intent.Phase is ExitPhase.CancelPending or ExitPhase.Replacing ||
        (intent.Current is { } owned && Account is ITrackedOrderReplacement tracked && tracked.IsReplacementPending(owned.Handle));

    private bool ExecutingStopChild(ExitIntent exit, Order order) =>
        exit.Role == BracketRole.StopLoss && exit.Quantity > 0 && exit.Generation == _generation &&
        exit.Side == Math.Sign(Volume) && order.Direction == ExitDirection &&
        Account.OrderContext.FindExecutingProtection(order, Account.OrderContext.ManualOwner) != null;

    private static decimal Executed(Order order) => order.Status == OrderStatus.Filled
        ? Math.Max(Math.Max(0, order.Filled), order.QuantityToFill)
        : Math.Max(0, order.Filled);

    private void ReconcileExits(bool allowPlacement)
    {
        // Optional tracked replacements preserve native amendments and broker-owned cancel/replace sequencing.
        // Settle target changes first, retaining a working stop until those capacity changes are confirmed.
        foreach (var intent in _exits.OrderBy(e => e.Role == BracketRole.Target ? 0 : 1).ToList())
        {
            if (intent.Current is not { } owned) continue;
            var order = Current(owned);
            if (Terminal(order)) continue;
            // A broker thread may apply an execution after ObserveExits booked this order. Comparing the intent with it
            // would read that fill as a resize request (an executing child would be enlarged or cancelled); book it first.
            if (Executed(order) > owned.Booked)
            {
                _syncAgain = true;
                continue;
            }
            if (intent.Phase == ExitPhase.Replacing && intent.Quantity > 0) continue;
            // A triggered stop's executing child is reducing a same-side position. A position report can lead its fills, so
            // a reduction would be computed from a stale fill count and cancel or shrink the order closing the position.
            // Its venue order is reduce-only; a closed or flipped position, or an explicit cancel, ends the exemption.
            bool executing = ExecutingStopChild(intent, order);
            bool change = (intent.Quantity != order.Unfilled && !(executing && intent.Quantity < order.Unfilled)) ||
                (intent.Price != PriceOf(order) && !executing);
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
                    decimal price = order.Type == OrderTypes.Market ? 0 : intent.Price;
                    if (intent.FailedTotal == total && intent.FailedPrice == price) continue;
                    intent.RequestedTotal = total;
                    intent.RequestedPrice = price;
                    intent.Phase = ExitPhase.Replacing;
                    OrderActions.Replace(Account, order, price, intent.Quantity);
                    _syncAgain = true;
                    continue;
                }
                intent.Phase = ExitPhase.CancelPending;
            }
            string identity = Identity(order);
            if (intent.CancelIdentity == identity) continue;
            intent.CancelIdentity = identity;
            if (intent.Quantity == 0) owned.RemovalRequested = true;
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
        var waiting = _exits.Where(e => e.Phase == ExitPhase.Waiting && e.Quantity > 0).ToList();
        // An opted-in venue links exactly one stop and one target for the whole position; anything else stays app-managed.
        using var link = waiting.Count > 0 && LinkablePair(waiting) is { } native ? native.LinkProtection(Account.OrderContext.ManualOwner) : null;
        OwnedExit? first = null;
        foreach (var intent in waiting)
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
            var order = OrderActions.Manual(Account, intent.Role == BracketRole.StopLoss ? OrderTypes.Stop : OrderTypes.Limit, intent.Side > 0 ? OrderDirections.Sell : OrderDirections.Buy, quantity, intent.Price, intent.Role == BracketRole.StopLoss ? "SL" : "TP", reduceOnly: true);
            var owned = new OwnedExit { Handle = order, Latest = order, Intent = intent, Linked = link != null, VolumeAtBaseline = Math.Abs(Volume) };
            if (link != null)
            {
                if (first == null) first = owned;
                else (first.Partner, owned.Partner) = (owned, first);
            }
            _owned.Add(owned);
            intent.Current = owned;
            intent.Phase = ExitPhase.Working;
            _syncAgain = true;
        }
        if (first is { Partner: null }) first.Linked = false;
    }

    /// <summary>
    /// The venue to link the waiting exits through, when this connection offers linked protection and they are exactly one
    /// stop and one target of the current position, each for its whole quantity, with nothing else working.
    /// </summary>
    private INativeProtection? LinkablePair(List<ExitIntent> waiting)
    {
        if (Account is not INativeProtection native || (native.NativeProtection & NativeProtectionFeatures.LinkedStopAndTarget) == 0)
            return null;
        if (_linkFallback) return null;
        var current = _exits.Where(e => e.Generation == _generation && (e.Quantity > 0 || e.Current != null)).ToList();
        var stop = waiting.Where(e => e.Role == BracketRole.StopLoss).ToList();
        var target = waiting.Where(e => e.Role == BracketRole.Target).ToList();
        decimal whole = ExitQuantity;
        bool pair = current.Count == 2 && current.All(e => e.Current == null) && stop.Count == 1 && target.Count == 1 &&
            stop[0].Generation == _generation && target[0].Generation == _generation &&
            stop[0].Side == Math.Sign(Volume) && target[0].Side == stop[0].Side &&
            stop[0].Quantity == whole && target[0].Quantity == whole && !_exits.Any(PendingChange);
        if (pair)
        {
            _protectionNote = null;
            return native;
        }
        _protectionNote = "App-managed protection: an exchange-side pair needs exactly one stop and one target for the whole position.";
        return null;
    }

    private bool StillProtecting(ExitIntent intent, OwnedExit owned) =>
        owned.Linked && intent.Phase != ExitPhase.CancelPending && intent.Quantity > 0 &&
        intent.Generation == _generation && intent.Side == Math.Sign(_lastVolume) && ExitQuantity > 0;

    // A refused linked submission never worked, so nothing executed: it is sent again at once as app-managed protection.
    private bool LinkRefused(ExitIntent intent, OwnedExit owned, Order order) =>
        order.Status == OrderStatus.Rejected && StillProtecting(intent, owned);

    // A linked leg the venue cancelled although this app (including Cancel all and Close position) never asked it to:
    // its cause is confirmed before anything is sent (ResolveDissolutions).
    private bool LinkCancelledByVenue(ExitIntent intent, OwnedExit owned, Order order) =>
        order.Status == OrderStatus.Canceled && StillProtecting(intent, owned) && !CancelRequested(owned);

    /// <summary>
    /// Adopts a venue-linked stop/target pair this account's manual protection placed earlier and that is still working
    /// (after a restart or a reopened chart), so protection the venue already holds is never duplicated.
    /// </summary>
    private bool AdoptNativeProtection()
    {
        if (Account is not INativeProtection native || Volume == 0) return false;
        if (_exits.Any(e => e.Generation == _generation && (e.Quantity > 0 || e.Current != null))) return false;
        var exit = ExitDirection;
        string owner = Account.OrderContext.ManualOwner.Id;
        var groups = Account.ActiveOrders
            .Where(o => !Terminal(o) && o.Direction == exit && o.OwnerId == owner && !_owned.Any(x => Same(x.Handle, o) || Same(x.Latest, o)))
            .Select(o => (Order: o, Group: native.ProtectionGroupOf(o)))
            .Where(x => x.Group != null)
            .GroupBy(x => x.Group!, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var legs = group.Select(x => x.Order).ToList();
            var stop = legs.FirstOrDefault(o => o.Type == OrderTypes.Stop);
            var target = legs.FirstOrDefault(o => o.Type == OrderTypes.Limit);
            if (legs.Count != 2 || stop == null || target == null) continue;
            var stopLeg = AdoptLeg(stop, BracketRole.StopLoss);
            var targetLeg = AdoptLeg(target, BracketRole.Target);
            (stopLeg.Partner, targetLeg.Partner) = (targetLeg, stopLeg);
            (stopLeg.PartnerBaseline, targetLeg.PartnerBaseline) = (Executed(target), Executed(stop));
            stopLeg.VolumeAtBaseline = targetLeg.VolumeAtBaseline = Math.Abs(Volume);
            _protectionNote = null;
            Message?.Invoke($"Exchange-side stop {Inst.FormatPrice(PriceOf(stop))} and target {Inst.FormatPrice(PriceOf(target))} adopted from the venue.");
            return true;
        }
        return false;
    }

    private OwnedExit AdoptLeg(Order order, BracketRole role)
    {
        var intent = new ExitIntent
        {
            Role = role, Side = Math.Sign(Volume), Generation = _generation, Price = PriceOf(order),
            Quantity = order.Unfilled, Phase = ExitPhase.Working,
        };
        var owned = new OwnedExit { Handle = order, Latest = order, Intent = intent, Booked = Executed(order), Linked = true };
        _owned.Add(owned);
        _exits.Add(intent);
        intent.Current = owned;
        return owned;
    }

    private static string Fmt(decimal q) => q.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatMoney(decimal v) => v.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture);

    private void DetachWhenSettled()
    {
        if (!_disposeRequested || _busy || _entryCancels.Count > 0 || _entryMoves.Count > 0) return;
        foreach (var intent in _exits)
        {
            if (PendingChange(intent) || intent.Phase == ExitPhase.Dissolving || (intent.Phase == ExitPhase.Waiting && intent.Quantity > 0)) return;
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
