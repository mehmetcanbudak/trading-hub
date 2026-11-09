using System.Globalization;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Strategies;

/// <summary>
/// Limitador de riesgo duro (no lo decide ninguna IA): revisa cada orden que abre o agranda una posición antes de
/// mandarla. Las que achican la posición (salidas, stops, cierres) siempre pasan.
/// </summary>
public interface ILiveOrderGuard
{
    /// <summary>null = se manda; si no, el motivo por el que se bloquea.</summary>
    string? Check(LiveStrategyRunner runner, OrderSide side, decimal quantity, decimal positionAfter);
}

/// <summary>
/// Ejecuta una estrategia sobre datos en vivo y manda sus órdenes a una cuenta (simulada o real).
/// Antes de empezar "calienta" los indicadores con la historia cargada, sin operar. Los stops y
/// objetivos automáticos se colocan como órdenes reales en la cuenta (OCO emulado).
/// <para>
/// La estrategia ve su posición en sus propias unidades (sin el tamaño de la cartera): cada orden manda la diferencia
/// entre la posición real que corresponde con el tamaño actual y la que tiene, así una salida cierra justo lo abierto
/// aunque el tamaño haya cambiado en el medio. Lleva su propia contabilidad (aunque otras estrategias o vos operen el
/// mismo instrumento en la misma cuenta) y respeta la pausa y los límites de riesgo (pérdida del día y drawdown).
/// </para>
/// Todo corre en el hilo de UI (el de <see cref="InstrumentData.Pump"/>).
/// </summary>
public sealed class LiveStrategyRunner : IStrategyHost, IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HashSet<long> CanceledOrders = new();

    // identifies orders without an exchange ID only within this process (a later process numbers its orders again)
    private static readonly string LocalSession = Guid.NewGuid().ToString("N")[..8];
    private readonly Strategy _strategy;
    private readonly InstrumentData _data;
    private readonly ITradingAccount _account;
    private readonly ChartPeriod _period;
    private readonly SessionSettings _session;
    private readonly TimeZoneInfo _tz;
    private readonly BarSeries _bars = new();
    private readonly List<StrategyOrder> _orders = new();
    // Only active/unresolved handles stay here. Durable lineage owns terminal execution corrections.
    private readonly Dictionary<long, OwnedExecutionOrder> _executionOrders = new();
    private readonly Dictionary<ExecutionIdentity, ExecutionReport> _executionReports = new();
    private AccountExecutionProjector? _projector;
    private AccountExecutionProjector.ProjectionSubscription? _projectionSubscription;
    private OrderOwner _owner = null!;
    private sealed class OwnedExecutionOrder(StrategyOrder order, string ledgerId, decimal quantityBaseline = 0)
    {
        public StrategyOrder Order { get; } = order;
        public string LedgerId { get; } = ledgerId;
        // Immutable for this handle, including native amendments/replacements retaining cumulative Filled.
        public decimal QuantityBaseline { get; } = quantityBaseline;
        public Dictionary<ExecutionIdentity, decimal>? PriorExecutions;
        public bool ExecutionBaselineKnown = quantityBaseline == 0;
        public decimal Quantity, Value;
        public List<ExecutionIdentity>? Reports;
    }
    private bool OwnershipRiskBlocked => _ownershipMismatch || _historyRetired || _projectionSubscription?.Retired == true
        || _account.OrderContext.HasStorageFault || (Ledger != null && ForeignOpenContext);
    private bool ConfiguredValuationBlocked => (MaxDailyLoss > 0 && AvailableTodayPnL == null)
        || (MaxDrawdown > 0 && AvailableCurrentDrawdown == null);
    private bool FinancialRiskBlocked => OwnershipRiskBlocked
        || (Ledger != null && Ledger.HasPendingPrices) || ConfiguredValuationBlocked
        || NativeExitAwaitingAcknowledgment
        || _projectionSubscription?.HasPendingUpdates == true
        || (_owner != null && _account.OrderContext.HasUnprojectedQuantity(_owner));
    // Ready may precede this runner consuming already-published execution facts. Pending prices hold
    // admitted orders; source failure, ownership faults and configured risk still require a pause.
    // The source is read now: the projector's cached availability can lag it while a pass is in flight.
    private bool FinancialPauseRequired => FinancialRiskBlocked &&
        (OwnershipRiskBlocked || ConfiguredValuationBlocked || _projector?.SourceAvailability == ExecutionAvailability.Unavailable);
    private readonly List<string> _log = new();
    private readonly SynchronizationContext? _sync;
    private BarBuilder _builder = null!;
    private bool _warmup;
    private bool _hasUnknownAvailability;
    private bool _hasUnsupportedSource;
    private bool _bootstrapping;
    private long _nextId = 1;
    private readonly HashSet<TradeIdentity> _seenTradeEvents = new();
    private long _timeTicks, _priceTicks;
    private decimal _lastPos;
    private string? _frozenAccountIdentity, _frozenKey;
    private bool _restoredLocal;
    private decimal MarkPrice => _account is IObservedMarketPrice ? _account.LastPrice : _data.LastPrice;
    private bool MarkPriceKnown => _account is IObservedMarketPrice observed && observed.HasKnownPrice;

    public LiveRunnerLocalState CaptureLocalState()
    {
        if (Persist || Ledger == null) throw new InvalidOperationException("Only initialized nonpersistent runners have run-local state.");
        return new(_data, _account, Ledger, _owner, _executionOrders.Values.Select(item => item.Order).ToArray());
    }
    public void RestoreLocalState(LiveRunnerLocalState state)
    {
        if (Persist || IsRunning || !ReferenceEquals(state.Data, _data) || !ReferenceEquals(state.Account, _account))
            throw new InvalidOperationException("Run-local strategy state belongs only to its exact nonpersistent data and account.");
        Ledger = state.Ledger;
        _owner = state.Owner;
        _restoredLocal = true;
        foreach (var order in state.Orders)
            if (order.Live is Order live && live.LogicalOrderId is { } logicalId)
                _executionOrders[live.Id] = new(order, logicalId);
    }
    private StrategyOrder? _sl, _tp;

    // identities of the stop and target left working at the broker when it stopped (see Stop with keepProtection)
    private readonly List<string> _keptProtection = new();

    // the ids it was asked to adopt (taken at start), and whether it still waits to decide on them (the account has not
    // confirmed the inherited position yet): meanwhile they stay its protection ids, so a save or a stop does not lose them
    private List<string> _adoptIds = new();
    private bool _adoptionPending;
    private decimal _highWater, _lowWater;
    private DateTime _lastTrailUpdate;
    private DateOnly _sessionDay;

    // posición de la estrategia en sus unidades (sin el tamaño de la cartera)
    private decimal _virtual;

    // detenida con «cerrar posición»: sigue escuchando la cuenta hasta que se ejecute el cierre
    private bool _closing;
    private bool _accountHooked;
    private int _syncing, _syncAgain;
    private readonly HashSet<Order> _cancelPending = new();
    private readonly List<string> _cancelIds = new();
    private string? _closeReason;
    private StrategyOrder? _submittedClose;
    private bool _protectionRefresh, _refreshTarget;
    private (int Side, decimal Price)? _refreshStopBound;
    private (int Side, decimal Stop, decimal Target)? _localProtectionPrices;
    private (StrategyOrder Order, decimal Price, string? IntentId)? _provisionalStop;

    // Observed user exits are not adopted or booked as strategy orders. Once an actual reduction happens,
    // cap their remaining size to the account's remaining exposure and reconcile our own protection.
    private readonly Dictionary<Order, decimal> _observedResize = new();
    private readonly Dictionary<Order, decimal> _observedLimits = new();
    private decimal _exitCapacity, _lastExitCapacity;
    private int _exitSign;
    private int _placing, _strategyCallbacks;

    // arrancó con una posición en su contabilidad y espera que la cuenta la confirme
    private bool _awaitingAccount;
    private bool _awaitLogged;
    private bool _ownershipMismatch;
    private bool _historyRetired;
    private bool ForeignOpenContext => Ledger.OpenContextConflict || (Ledger.Position != 0 &&
        Ledger.OpenContextId != null && Ledger.OpenContextId != _account.OrderContext.Descriptor.Id);
    private decimal LocalPosition => Ledger?.PositionInContext(_account.OrderContext.Descriptor.Id) ?? 0;

    // límites de riesgo
    private long _tradesSeen = -1;
    private DateOnly _totalsDay;
    private decimal _dayPnl, _peakClosed;
    private bool _dayFinancialsPending;
    private DateOnly _limitDay;

    public LiveStrategyRunner(Strategy strategy, InstrumentData data, ITradingAccount account, ChartPeriod period, string? timeZoneId = null, TimeSpan sessionStart = default)
    {
        _strategy = strategy;
        _data = data;
        _account = account;
        _period = period;
        _tz = timeZoneId != null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var z) ? z : TimeZoneInfo.Utc;
        _session = new SessionSettings { TimeZone = _tz, Start = sessionStart };
        _sync = SynchronizationContext.Current;
    }

    public string StrategyName => Strategy.DisplayName(_strategy.GetType());

    /// <summary>Identificador estable en la cartera: estrategia, instrumento y cuenta.</summary>
    public string Key
    {
        get
        {
            if (_account is not IAccountExecutionSource)
                return $"{_strategy.GetType().FullName}|{_data.Instrument.FeedId}:{_data.Instrument.Symbol}|{_account.Name}";
            if (_frozenKey == null && !TryPrepareIdentity(out var reason)) throw new InvalidOperationException(reason);
            return _frozenKey!;
        }
    }
    public bool TryPrepareIdentity(out string? reason)
    {
        reason = null;
        if (_account is not IAccountExecutionSource source) return true;
        string? identity = source.PersistentAccountIdentity;
        if (string.IsNullOrWhiteSpace(identity))
        {
            reason = "Waiting for the provider to verify the native account identity before loading or starting this strategy.";
            return false;
        }
        if (_frozenAccountIdentity != null && !string.Equals(_frozenAccountIdentity, identity, StringComparison.Ordinal))
        {
            reason = "The verified account identity changed. This runner remains bound to its original account; review it before creating another run.";
            return false;
        }
        _frozenAccountIdentity ??= identity;
        _frozenKey ??= $"{_strategy.GetType().FullName}|{_data.Instrument.FeedId}:{_data.Instrument.Symbol}|{identity}";
        return true;
    }

    /// <summary>Multiplicador del tamaño (1 = como dice la estrategia). Se aplica en la próxima orden.</summary>
    public decimal SizeMultiplier { get; set; } = 1;

    /// <summary>
    /// Contrato en el que se ejecuta: el mismo de la estrategia, o su micro cuando el tamaño pide fracciones del mini
    /// (la estrategia sigue mirando el NQ y las órdenes van al MNQ). La cuenta que recibe el runner es la de este contrato.
    /// </summary>
    public Instrument ExecInstrument
    {
        get => _exec ?? _data.Instrument;
        set => _exec = value;
    }

    private Instrument? _exec;

    /// <summary>Unidades del contrato de ejecución por cada unidad de la estrategia (1 NQ = 10 MNQ).</summary>
    public decimal ExecRatio { get; set; } = 1;

    /// <summary>Has tracked working orders or saved-ID cancellations still awaiting broker confirmation.</summary>
    public bool HasWorkingOrders => _orders.Any(o => o.IsActive) || _cancelIds.Count > 0 || _cancelPending.Any(o => o.State == OrderStates.Active);

    /// <summary>Ejecuta en otro contrato que el que mira.</summary>
    public bool CrossExecution => !string.Equals(ExecInstrument.Symbol, _data.Instrument.Symbol, StringComparison.OrdinalIgnoreCase);

    /// <summary>De dónde se lanzó: panel (En vivo), chart (un gráfico) u otro origen.</summary>
    public string Source { get; set; } = "panel";

    /// <summary>Cuenta y conexión con las que opera (para volver a lanzarla igual).</summary>
    public string AccountId { get; set; } = "";
    public string ProfileId { get; set; } = "";

    /// <summary>Límites de riesgo: pérdida máxima del día y drawdown máximo (en dinero; 0 = sin límite).</summary>
    public decimal MaxDailyLoss { get; set; }
    public decimal MaxDrawdown { get; set; }

    /// <summary>Al tocar un límite, además de frenar, cierra la posición.</summary>
    public bool CloseOnLimit { get; set; } = true;

    /// <summary>Portafolio en vivo al que pertenece (null = ninguno).</summary>
    public string? PortfolioId { get; set; }

    /// <summary>Por qué se detuvo o se frenó (límite de riesgo, error…).</summary>
    public string? StopReason { get; set; }

    /// <summary>Guarda sus ejecuciones en la cartera (las visuales de un gráfico no).</summary>
    public bool Persist { get; set; } = true;

    /// <summary>
    /// On start with an inherited position the account confirms, protects it again, stop first: it adopts the stop loss and
    /// take profit kept at the broker (<see cref="AdoptProtectionIds"/>) and places the ones it lacks. While another working
    /// stop could be its stop it places neither; while only another working limit could be its take profit it places just the
    /// stop loss.
    /// </summary>
    public bool ProtectOnStart { get; set; }

    /// <summary>
    /// Broker identities of a previous run's stop loss and take profit to adopt instead of placing new ones, usually the
    /// saved <see cref="LiveRunnerInfo.ProtectionOrderIds"/>. Set before <see cref="Start"/>; with <see cref="ProtectOnStart"/>,
    /// once the account confirms the inherited position, a working order that matches by an account's durable client identity,
    /// <see cref="Order.ExchangeOrderId"/>, or its id in this process and would close that position becomes its stop or target: later protection refreshes
    /// cancel and replace it, and trailing continues from its price.
    /// </summary>
    public IReadOnlyCollection<string> AdoptProtectionIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Broker identities of its stop loss and take profit. While it runs: the working ones or, until the account confirms the
    /// inherited position and order replay, the ones it waits to adopt. Once stopped: only the ones explicitly
    /// left working with <see cref="Stop(bool, bool)"/>. Pending cancellations are not intentionally kept protection;
    /// they remain visible through <see cref="HasWorkingOrders"/>. Each identity is the exchange ID, or an ID valid only in this process.
    /// </summary>
    public IReadOnlyList<string> ProtectionOrderIds => !IsRunning ? _keptProtection : _adoptionPending ? _adoptIds : WorkingProtectionIds();

    /// <summary>Pausada: no abre ni agranda posiciones; si tiene una abierta, la sigue gestionando hasta cerrarla.</summary>
    public bool Paused { get; private set; }

    /// <summary>Tocó la pérdida máxima del día: no abre más hasta la próxima sesión.</summary>
    public bool DailyLimitActive => _limitDay != default && _limitDay == _sessionDay;

    /// <summary>A close was requested but remains unresolved, including unknown snapshots or an unfilled/rejected close.</summary>
    public bool IsClosing => _closing;

    /// <summary>Contabilidad propia de esta estrategia (llenados, posición, resultado y operaciones).</summary>
    public LiveLedger Ledger { get; private set; } = null!;

    /// <summary>Owned quantity in this bound account context, expressed in account units.</summary>
    public decimal OwnPosition => LocalPosition;

    /// <summary>Posición en las unidades de la estrategia (sin el tamaño).</summary>
    public decimal VirtualPosition => _virtual;

    /// <summary>Tocó un límite de riesgo (mensaje para avisarte).</summary>
    public event Action<LiveStrategyRunner, string>? LimitHit;

    /// <summary>Limitador de riesgo duro (por ejemplo uno compartido por varias estrategias).</summary>
    public ILiveOrderGuard? Guard { get; set; }

    /// <summary>El limitador bloqueó una orden (con el motivo).</summary>
    public event Action<LiveStrategyRunner, string>? GuardBlocked;

    /// <summary>Se ejecutó una orden (texto para avisarte: qué, cuántos contratos y de cuál).</summary>
    public event Action<LiveStrategyRunner, string>? Executed;

    /// <summary>
    /// Cuántas unidades del contrato de ejecución manda una orden de <paramref name="strategyQuantity"/> de la estrategia
    /// (con el tamaño, el micro si lo hay y el mínimo de 1). 0 si el tamaño está en cero.
    /// </summary>
    public decimal UnitsFor(decimal strategyQuantity) => Math.Abs(Target(Math.Abs(strategyQuantity)));

    /// <summary>Foto del estado para la cartera y el laboratorio.</summary>
    public LiveRunnerInfo Info() => new()
    {
        Key = Key,
        Strategy = StrategyName,
        StrategyId = _strategy.GetType().FullName ?? "",
        FeedId = _data.Instrument.FeedId,
        Symbol = _data.Instrument.Symbol,
        Period = _period.Code,
        Account = _account.Name,
        Running = IsRunning,
        StartedUtc = StartedUtc,
        Size = SizeMultiplier,
        PointValue = _data.Instrument.PointValue,
        ExecSymbol = CrossExecution ? ExecInstrument.Symbol : null,
        Position = OwnPosition,
        OpenPnL = Ledger?.OpenPnL(MarkPrice, MarkPriceKnown) ?? 0,
        Realized = Ledger?.Realized ?? 0,
        CompleteNet = Ledger?.NetRealized,
        AvailableOpenPnL = Ledger?.KnownOpenPnL(MarkPrice, MarkPriceKnown),
        FinancialQuality = Ledger?.FinancialQuality ?? LiveFinancialQuality.Pending,
        Trades = Ledger?.Trades.Count ?? 0,
        Source = Source,
        AccountId = AccountId,
        ProfileId = ProfileId,
        Parameters = _strategy.GetParameters(),
        TimeZoneId = _tz == TimeZoneInfo.Utc ? null : _tz.Id,
        SessionStartHours = _session.Start.TotalHours,
        MaxDailyLoss = MaxDailyLoss,
        MaxDrawdown = MaxDrawdown,
        CloseOnLimit = CloseOnLimit,
        PortfolioId = PortfolioId,
        StopReason = StopReason,
        Paused = Paused,
        IsReal = _account.IsReal,
        ProtectionOrderIds = ProtectionOrderIds is { Count: > 0 } ids ? ids.ToList() : null,
    };

    public Strategy StrategyInstance => _strategy;
    public InstrumentData Data => _data;
    public ITradingAccount Account => _account;
    public bool IsRunning { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public int Fills { get; private set; }
    public int WarmupBars { get; private set; }
    public IReadOnlyList<string> LogLines => _log;

    /// <summary>Desde cuándo corre de verdad (lo anterior fue calentamiento con la historia, sin operar).</summary>
    public DateTime LiveFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>Algo cambió (para refrescar la UI).</summary>
    public event Action? Changed;

    /// <summary>
    /// Cambia en qué condiciones de mercado abre operaciones, sin reiniciar: vale desde la próxima orden. Las salidas
    /// siempre pasan, así que lo que tenga abierto lo sigue gestionando.
    /// </summary>
    public void SetContextFilter(RegimeFilter filter, int minRank, int maxRank)
    {
        if (_sync != null && SynchronizationContext.Current != _sync)
        {
            _sync.Post(_ => SetContextFilter(filter, minRank, maxRank), null);
            return;
        }

        _strategy.RegimeFilter = filter;
        _strategy.MinVolatilityRank = Math.Clamp(minRank, 0, 100);
        _strategy.MaxVolatilityRank = Math.Clamp(Math.Max(maxRank, minRank), 0, 100);
        string what = Robustness.DescribeFilter(filter, _strategy.MinVolatilityRank, _strategy.MaxVolatilityRank);
        Log($"Market condition: {(what.Length > 0 ? "trades in " + what : "trades in any condition")} · now {_strategy.Context.Describe()}");
        Changed?.Invoke();
    }

    /// <summary>Arrancó una estrategia en vivo (la app le pasa la historia por hora del mercado para su contexto).</summary>
    public static event Action<LiveStrategyRunner>? Started;

    /// <summary>Días de historia por hora con que arrancó su contexto de mercado (0 = solo el calentamiento).</summary>
    public int ContextDays { get; private set; }

    /// <summary>
    /// Arma el contexto del mercado (tendencia de 24 horas y percentil de volatilidad del último mes) con la historia por
    /// hora que le pasa la app y después con sus propias velas: el calentamiento de un gráfico suele traer pocas horas y sin
    /// esto el filtro de contexto arrancaría sin datos (y dejaría entrar en cualquier contexto). Se puede llamar ya corriendo.
    /// </summary>
    public void SeedContext(IReadOnlyList<(DateTime Time, decimal Close)> hourly)
    {
        if (_sync != null && SynchronizationContext.Current != _sync)
        {
            _sync.Post(_ => SeedContext(hourly), null);
            return;
        }

        if (hourly.Count == 0) return;
        bool coarse = _period.IsTimeBased && _period.ApproxDuration >= TimeSpan.FromHours(1);
        var tracker = new MarketContextTracker(coarse);
        // solo lo anterior a la primera vela propia (desde ahí siguen sus velas, como siempre)
        var firstHour = _bars.Count > 0 ? new DateTime(_bars.At(0).Time.Ticks - _bars.At(0).Time.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc) : DateTime.MaxValue;
        var before = hourly.Where(h => h.Time < firstHour).OrderBy(h => h.Time).ToList();
        if (before.Count == 0) return;
        tracker.AddHistory(before, _period.IsTimeBased ? _period.ApproxDuration : TimeSpan.FromHours(1));
        foreach (var b in _bars.All) tracker.Add(b.Time, b.Close);
        _strategy.Context = tracker;
        ContextDays = (int)Math.Round((before[^1].Time - before[0].Time).TotalDays);
        Log($"Market context with {ContextDays} days of hourly history: {tracker.Describe()}");
        Changed?.Invoke();
    }

    public void Start()
    {
        if (IsRunning) return;
        if (_closing || HasWorkingOrders) throw new InvalidOperationException("The previous close or order cancellation is still unresolved.");
        if (!TryPrepareIdentity(out var identityReason)) throw new InvalidOperationException(identityReason);
        _bootstrapping = true;
        try { StartCore(); }
        finally
        {
            _bootstrapping = false;
            if (!IsRunning) HookAccount(false);
        }
    }

    private string PersistenceRoot()
    {
        var context = _account.OrderContext;
        if (context.StorageRoot is { } root) return root;
        if (context.Descriptor.Provider != "simulator")
            throw new InvalidOperationException("Native strategy persistence requires the account's captured explicit ownership root.");
        context.AttachStorage(LiveStore.Root);
        return context.StorageRoot!;
    }

    private void StartCore()
    {
        if (Persist) PersistenceRoot();
        _owner = Persist
            ? _account.OrderContext.LedgerOwner(Key, LiveStore.CaptureLedgerGeneration(_account.OrderContext.StorageRoot!, Key))
            : _restoredLocal ? _owner : new OrderOwner(Guid.NewGuid().ToString("N"), Kind: OrderOwnerKind.Strategy);
        _projector = AccountExecutionProjector.For(_account.Executions, _account.OrderContext);
        _projector.Synchronize();
        // Subscribe before loading the durable snapshot. Facts committed during load or warmup stay
        // queued; applying an overlap is idempotent, whereas subscribing after warmup loses that interval.
        HookAccount(true);
        Ledger = Persist ? LiveStore.LoadLedger(_account.OrderContext.StorageRoot!, Key, _data.Instrument.PointValue)
            : Ledger ?? new LiveLedger(Key, _data.Instrument.PointValue);
        if (_restoredLocal) _projector.ReplayLocalOwner(_owner,
            _executionOrders.Values.Select(owned => owned.Order.Live).OfType<Order>().ToArray(),
            ExecInstrument.Symbol, ExecInstrument.PointValue);
        _historyRetired = false;
        if (!_restoredLocal) _executionOrders.Clear();
        _executionReports.Clear();
        _localProtectionPrices = null;
        if (Persist && LiveStore.PendingLedgerEvents(_account.OrderContext.StorageRoot!, Key) > 0)
        {
            SetPaused(true);
            Log("Recovered pending ledger events: new entries remain paused until the events are saved; existing exposure remains managed.");
        }
        StopReason = null;
        _strategy.InternalInit(this);
        _builder = new BarBuilder(_data.Instrument, _period, _session, _strategy.UseFootprint);

        // calentamiento con la historia ya cargada: arma velas e indicadores sin operar
        _warmup = true;
        for (int i = 0; i < _data.Trades.Count; i++) Process(i);
        _warmup = false;
        if (_hasUnknownAvailability) Log("Events with unknown source availability were excluded from causal strategy processing.");
        if (_hasUnsupportedSource)
        {
            SetPaused(true);
            Log("Source interval or chart location cannot be consumed causally by this strategy period. New entries are paused; existing account protection remains managed.");
        }
        WarmupBars = _bars.Count;
        // lo que dibujó mientras calentaba no vale: sin operar se cree siempre plana y marca rupturas que la corrida real
        // no tomó (en el gráfico, hasta acá se ven los dibujos del backtest de la historia)
        _strategy.Canvas.Clear();
        LiveFrom = _timeTicks > 0 ? Time : DateTime.UtcNow;

        SyncQuantities();
        SyncExecutions(out _);
        _ownershipMismatch = ForeignOpenContext;
        _virtual = ToVirtual(LocalPosition);
        _lastPos = LocalPosition;
        _closing = false;
        _closeReason = null;
        _submittedClose = null;
        _observedLimits.Clear();
        _observedResize.Clear();
        _keptProtection.Clear(); // running again: its protection is what it tracks from now on
        _adoptIds = AdoptProtectionIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        _adoptionPending = _adoptIds.Count > 0 && LocalPosition != 0;
        _data.TradesAppended += OnAppended;
        HookAccount(true);
        IsRunning = true;
        StartedUtc = DateTime.UtcNow;
        _bootstrapping = false;
        Log($"Started on {TradingHub.Common.EnglishText.Of(_account.Name)} with {WarmupBars} warmup bars{(SizeMultiplier != 1 ? $" · size ×{Qty(SizeMultiplier)}" : "")}" +
            (CrossExecution ? $" · executes on {ExecInstrument.Symbol} (1 {_data.Instrument.Symbol} = {Qty(ExecRatio)} {ExecInstrument.Symbol})" : ""));
        CancelRestoredFlatProtection();
        if (Ledger.SkippedLines > 0)
            Log($"Its ledger has {Ledger.SkippedLines} unreadable line{(Ledger.SkippedLines == 1 ? "" : "s")}: skipped, and the fills after them were read (the file was not changed)");
        SyncOrders();
        if (LocalPosition != 0 || ForeignOpenContext) CheckInheritedPosition(initial: true);
        else ReportOrphanedProtection();
        SyncOrders();
        Changed?.Invoke();
        Started?.Invoke(this);
    }

    /// <summary>Detiene la estrategia; opcionalmente cierra su posición (solo la suya) y cancela sus órdenes.</summary>
    public void Stop(bool flatten) => Stop(flatten, keepProtection: false);

    /// <summary>
    /// Stops the strategy. With <paramref name="flatten"/> it closes its own position and cancels all its orders. Otherwise it
    /// cancels its orders, except that with <paramref name="keepProtection"/> its stop loss and take profit stay working at the
    /// broker: their identities stay in <see cref="ProtectionOrderIds"/> (and, with <see cref="Persist"/>, in its saved state)
    /// so a later start can adopt them (<see cref="AdoptProtectionIds"/>). It no longer tracks them: a fill while it is stopped
    /// is settled with the account at the next start. Closing the position never keeps them.
    /// </summary>
    public void Stop(bool flatten, bool keepProtection)
    {
        if (!IsRunning) return;
        _data.TradesAppended -= OnAppended;
        _keptProtection.Clear();
        if (_adoptionPending && keepProtection && !flatten) _keptProtection.AddRange(_adoptIds);
        if (!keepProtection || flatten) _cancelIds.AddRange(_adoptIds);
        _adoptionPending = false;
        var kept = new List<StrategyOrder>(2);
        if (keepProtection && !flatten)
            foreach (var so in new[] { _sl, _tp })
                if (so is { IsActive: true, Live: Order live })
                {
                    kept.Add(so);
                    _keptProtection.Add(IdentityOf(live));
                }

        _placing++;
        try
        {
            CancelSavedProtection();
            foreach (var o in _orders.ToList())
                if (!kept.Contains(o)) Cancel(o);
            _orders.RemoveAll(kept.Contains);
            _sl = _tp = null;
            _protectionRefresh = false;
            _refreshStopBound = null;
        }
        finally { _placing--; }
        _strategyCallbacks++;
        try
        {
            _strategy.InternalStop();
        }
        catch (Exception ex)
        {
            Log("Error in OnStop: " + ex.Message);
        }
        finally { _strategyCallbacks--; }

        IsRunning = false;
        // A successor may run this ledger while this stopped runner still watches a cancel-pending handle.
        // Keep those observations, but never transfer unresolved close management to another active runner.
        if (!flatten && !_closing && HasWorkingOrders) _projectionSubscription?.RetainPendingOrders();
        if (flatten && LocalPosition != 0)
        {
            _closing = true;
            _submittedClose = null;
            _closeReason = "Close on stop";
        }
        SyncOrders();
        if (!_closing && !HasWorkingOrders) HookAccount(false);
        if (kept.Count > 0)
            Log($"Stopped: {(kept.Count > 1 ? "its stop loss and take profit stay" : $"its {kept[0].Name.ToLowerInvariant()} stays")} working at the broker ({string.Join(", ", kept.Select(so => DescribeLive((Order)so.Live!)))})");
        else Log(_closing ? "Stopped: position close is unresolved; waiting for the account and order confirmations" : flatten && LocalPosition == 0 ? "Stopped and position closed" : "Stopped");
        if (_keptProtection.Count > kept.Count)
            Log("It stopped before adopting the orders its previous run kept at the broker: they stay working (review them in Chart Trader)");
        if (_cancelIds.Count > 0 || _cancelPending.Any(o => o.State == OrderStates.Active))
            Log("Order cancellation is unresolved: waiting for broker confirmation (review working orders in Chart Trader)");
        if (keepProtection && !flatten && Persist && _keptProtection.Count > 0 && LiveStore.SaveProtectionOrderIds(_account.OrderContext.StorageRoot!, Info()) is { } error)
            Log($"Could not record the orders it kept at the broker in its saved state ({error.TrimEnd('.')}): review them in Chart Trader");
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Stop(flatten: false);
        // A caller releasing the runner must not discard late fills or an unresolved close/cancel.
        // SyncOrders detaches itself once those broker acknowledgments actually settle.
        if (!_closing && !HasWorkingOrders) HookAccount(false);
    }

    /// <summary>Pausa o reanuda. En pausa cancela las entradas pendientes y no abre nada nuevo.</summary>
    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        if (!paused) _projector?.Synchronize();
        if (!paused && _ownershipMismatch && Persist)
        {
            Ledger = LiveStore.LoadLedger(_account.OrderContext.StorageRoot!, Key, _data.Instrument.PointValue);
            _ownershipMismatch = ForeignOpenContext;
            if (!_ownershipMismatch)
            {
                _virtual = ToVirtual(LocalPosition);
                _lastPos = LocalPosition;
                _awaitingAccount = LocalPosition != 0;
                StopReason = null;
            }
        }
        if (!paused && FinancialRiskBlocked)
        {
            Log("Cannot resume: execution prices or the financial facts required by configured risk limits are pending.");
            return;
        }
        if (!paused && Persist)
        {
            try
            {
                if (LiveStore.SavePendingLedgerEvents(_account.OrderContext.StorageRoot!, Key) is { } error)
                {
                    Log($"Cannot resume: pending ledger events could not be saved ({error}). New entries remain paused.");
                    return;
                }
                if (LiveStore.PendingLedgerEvents(_account.OrderContext.StorageRoot!, Key) > 0)
                {
                    Log("Cannot resume: ledger events are still pending. Restore ledger storage and save the pending events before opening new exposure.");
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"Cannot resume: ledger recovery failed ({ex.Message}). Keep new entries paused and reconcile its history.");
                return;
            }
        }
        Paused = paused;
        if (paused)
        {
            foreach (var order in _orders.Where(o => !o.IsProtective && o.IsActive).ToList())
            {
                if (order.Live is Order live && live.State != OrderStates.Active) continue;
                decimal remaining = order.Live is Order active ? active.Unfilled : Math.Max(0, order.LiveQuantity - order.Booked);
                decimal position = LocalPosition;
                bool opens = position == 0 || Math.Sign(position) == (order.Side == OrderSide.Buy ? 1 : -1) || remaining > Math.Abs(position);
                if (opens) Cancel(order);
            }
            Log("Paused: no new entries (an existing position remains managed until closed)");
        }
        else Log("Resumed: trading again");

        Changed?.Invoke();
    }

    /// <summary>
    /// Antes de arrancar: le da una posición ya abierta en su contabilidad (por ejemplo la que dejó el backtest de la
    /// historia del gráfico en modo visual), así sigue gestionándola en vivo y la salida queda unida a esa entrada.
    /// La cuenta tiene que tener esa posición (si no, al arrancar se cierra en la contabilidad).
    /// </summary>
    public void SeedPosition(LiveFill fill)
    {
        if (IsRunning) throw new InvalidOperationException("Only before starting.");
        if (!TryPrepareIdentity(out var identityReason)) throw new InvalidOperationException(identityReason);
        Ledger ??= Persist ? LiveStore.LoadLedger(PersistenceRoot(), Key, _data.Instrument.PointValue) : new LiveLedger(Key, _data.Instrument.PointValue);
        Ledger.Apply(fill);
        SaveFill(fill);
    }

    /// <summary>
    /// Deletes flat, settled history and starts a fresh owner generation. Open exposure and unresolved orders
    /// must settle first so an old order cannot book quantity under the new history owner.
    /// </summary>
    public bool ResetLedger()
    {
        _projector?.Synchronize();
        if (Ledger is { Position: not 0 } or { OpenContextConflict: true } || HasWorkingOrders || HasRecoveredWorkingOrders() || _closing) return false;
        if (_projector != null && _owner != null)
        {
            if (!_projector.TryResetOwner(_owner, _projectionSubscription, _data.Instrument.PointValue, ResetLedgerCore)) return false;
        }
        else
        {
            if (Persist)
            {
                _historyRetired = true;
                LiveStore.DeleteFills(PersistenceRoot(), Key);
            }
            ResetLedgerCore();
        }
        Log("History deleted: starting fresh");
        Changed?.Invoke();
        return true;
    }

    private bool HasRecoveredWorkingOrders()
    {
        if (_owner == null) return false;
        // The recovered broker handle need not have a local StrategyOrder. Require its exact durable
        // context/owner/generation and logical lineage; comments and process-local IDs are not authority.
        if (!_account.OrdersReady) return true;
        var context = _account.OrderContext;
        foreach (var order in _account.ActiveOrders)
        {
            if (order.State != OrderStates.Active || order.ContextId != context.Descriptor.Id || order.OwnerId != _owner.Id) continue;
            var intent = context.Find(order.IntentId);
            if (intent != null && intent.Owner == _owner && intent.ContextId == context.Descriptor.Id &&
                intent.Generation == context.Descriptor.Generation && intent.LogicalOrderId == order.LogicalOrderId)
                return true;
        }
        return false;
    }

    private void ResetLedgerCore()
    {
        if (Persist)
        {
            // If deletion or generation creation fails, the old owner must not open new exposure.
            _historyRetired = true;
            if (_owner != null)
                _owner = _account.OrderContext.LedgerOwner(Key,
                    LiveStore.CaptureLedgerGeneration(_account.OrderContext.StorageRoot!, Key));
        }
        else if (_owner != null)
            _owner = new OrderOwner(Guid.NewGuid().ToString("N"), Kind: OrderOwnerKind.Strategy);
        _projectionSubscription?.Dispose();
        _projectionSubscription = null;
        Ledger = new LiveLedger(Key, _data.Instrument.PointValue);
        _executionOrders.Clear();
        _executionReports.Clear();
        _ownershipMismatch = _historyRetired = false;
        _virtual = 0;
        _lastPos = 0;
        _tradesSeen = -1;
        if (_accountHooked)
        {
            var owner = _owner ?? throw new InvalidOperationException("An active account subscription requires a strategy owner.");
            var projector = _projector ?? throw new InvalidOperationException("An active account subscription requires an execution projector.");
            _projectionSubscription = projector.Subscribe(owner, OnAccountChanged);
            projector.Synchronize();
        }
    }

    /// <summary>Cierra la posición de la estrategia a mercado y cancela sus órdenes (sigue corriendo).</summary>
    public void ClosePosition(string why = "Manual close")
    {
        _cancelIds.AddRange(_adoptIds);
        _adoptionPending = false;
        _placing++;
        try
        {
            CancelSavedProtection();
            foreach (var o in _orders.ToList()) Cancel(o);
            _sl = _tp = null;
            _protectionRefresh = false;
            _refreshStopBound = null;
        }
        finally { _placing--; }
        if (Ledger == null || LocalPosition == 0) return;
        _closing = true;
        _submittedClose = null;
        _closeReason = why;
        HookAccount(true);
        SyncOrders();

        Changed?.Invoke();
    }

    /// <summary>Resultado del día de sesión (cerrado + abierto).</summary>
    public decimal TodayPnL
    {
        get
        {
            if (Ledger == null) return 0;
            RefreshTotals();
            return _dayPnl + Ledger.OpenPnL(MarkPrice, MarkPriceKnown);
        }
    }

    /// <summary>Drawdown actual desde el máximo de su curva (cerrado), contando lo abierto.</summary>
    public decimal CurrentDrawdown
    {
        get
        {
            if (Ledger == null) return 0;
            RefreshTotals();
            return Math.Max(0, _peakClosed - (Ledger.Realized + Ledger.OpenPnL(MarkPrice, MarkPriceKnown)));
        }
    }

    /// <summary>Current configured session's available net, including known open valuation; prior closed fee
    /// uncertainty does not masquerade as current-session uncertainty. Global quality remains unchanged.</summary>
    public decimal? AvailableTodayPnL
    {
        get
        {
            if (Ledger == null) return null;
            RefreshTotals();
            return !_dayFinancialsPending && Ledger.KnownOpenPnL(MarkPrice, MarkPriceKnown) is { } open ? _dayPnl + open : null;
        }
    }

    public decimal? AvailableCurrentDrawdown
    {
        get
        {
            if (Ledger == null || Ledger.HasPendingFinancials) return null;
            RefreshTotals();
            return Ledger.KnownOpenPnL(MarkPrice, MarkPriceKnown) is { } open ? Math.Max(0, _peakClosed - (Ledger.Realized + open)) : null;
        }
    }

    // ----------------------------------------------------------------- datos

    private void OnAppended(int start, int count)
    {
        try
        {
            _strategyCallbacks++;
            try
            {
                for (int i = start; i < start + count && IsRunning; i++) Process(i);
            }
            finally { _strategyCallbacks--; }
            if (!IsRunning) return;
            SyncOrders();
            CheckLimits();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            StopReason = "Error: " + ex.Message;
            Stop(flatten: false);
        }
    }

    private void Process(int i)
    {
        var store = _data.Trades;
        // Numeric IDs are source/day scoped, not globally ordered. Opaque native IDs retain their exact identity.
        var trade = store.Get(i);
        if (!trade.Provenance.HasKnownAvailability)
        {
            if (!_hasUnknownAvailability && !_warmup)
                Log("Events with unknown source availability are excluded from causal strategy processing.");
            _hasUnknownAvailability = true;
            return;
        }
        if (!_seenTradeEvents.Add(TradeIdentity.From(trade))) return;
        long t = trade.Time.Ticks, p = store.GetPriceTicks(i);
        _timeTicks = Math.Max(_timeTicks, trade.AvailableAt.Ticks);
        if (!_builder.IsResolutionCompatible(t, trade.Provenance) ||
            (_period.IsTimeBased && _builder.Current is { } forming && t < forming.Time.Ticks))
        {
            if (!_hasUnsupportedSource && !_warmup)
                Log("Source interval or chart location cannot be consumed causally by this strategy period. New entries are paused; existing account protection remains managed.");
            _hasUnsupportedSource = true;
            if (!_warmup) SetPaused(true);
            return;
        }
        var closed = _builder.Roll(t, p);
        if (closed != null)
        {
            _bars.Add(closed);
            _priceTicks = _data.Instrument.ToTicks(closed.Close);
            _strategy.InternalBar(closed);
        }

        var day = _session.SessionDate(Time);
        if (day != _sessionDay)
        {
            bool first = _sessionDay == default;
            _sessionDay = day;
            if (!first) _strategy.InternalSession(day);
            if (!_warmup && _limitDay != default && day != _limitDay)
            {
                _limitDay = default;
                StopReason = null;
                Log("New session: trading resumes after the daily loss limit");
            }
        }

        _priceTicks = p;
        _builder.Append(t, p, trade.Volume, trade.Side, trade.Provenance);
        if (_warmup) return;
        Trail(trade.Price);
        if (_strategy.WantsTicks) _strategy.InternalTick(trade);
    }

    private void HookAccount(bool on)
    {
        if (on == _accountHooked) return;
        _accountHooked = on;
        if (on)
        {
            _account.Changed += OnAccountChanged;
            _projectionSubscription = _projector!.Subscribe(_owner, OnAccountChanged);
            _projector.Synchronize();
        }
        else
        {
            _account.Changed -= OnAccountChanged;
            _projectionSubscription?.Dispose();
            _projectionSubscription = null;
        }
    }

    private void OnAccountChanged()
    {
        if (_sync != null && SynchronizationContext.Current != _sync) _sync.Post(_ => SyncOrders(), null);
        else SyncOrders();
    }

    // ----------------------------------------------------------------- posición heredada

    /// <summary>
    /// Arrancó con una posición en su contabilidad (Trading Hub se cerró con la posición abierta, o se reinició con otros
    /// parámetros). Si la cuenta la tiene, la sigue gestionando; lo que la cuenta no tiene (una simulada nueva arranca
    /// plana, o la cerraste a mano) se cierra en la contabilidad al último precio. Nunca confunde una foto
    /// todavía incompleta con una posición plana ni con la ausencia de órdenes guardadas.
    /// </summary>
    private void CheckInheritedPosition(bool initial)
    {
        if (_ownershipMismatch)
        {
            _awaitingAccount = true;
            StopReason = "The open ledger position belongs to another or conflicting account context; reconcile its original account before resuming.";
            SetPaused(true);
            if (initial) Log(StopReason);
            return;
        }
        decimal own = LocalPosition;
        if (own == 0)
        {
            _awaitingAccount = false;
            _adoptionPending = false;
            return;
        }

        bool positionReady = _account.PositionReady;
        decimal acc = positionReady ? _account.Position?.Volume ?? 0 : 0;
        decimal covered = Math.Sign(acc) == Math.Sign(own) ? Math.Min(Math.Abs(acc), Math.Abs(own)) : 0;
        // Aggregate native PnL/netting cannot prove a closing execution for this strategy's owned quantity.
        if (!positionReady || !_account.OrdersReady ||
            (_account.OrderContext.Descriptor.Provider != "simulator" && covered < Math.Abs(own)))
        {
            _awaitingAccount = true;
            if (initial)
            {
                _awaitLogged = false;
                Log($"Had a position ({(own > 0 ? "long" : "short")} {Qty(Math.Abs(own))}): waiting for {TradingHub.Common.EnglishText.Of(_account.Name)} to confirm its position and working orders before trading");
            }
            return;
        }

        _awaitingAccount = false;
        _adoptionPending = false;
        if (covered < Math.Abs(own))
        {
            decimal excess = Math.Abs(own) - covered;
            BookSynthetic(own > 0 ? -excess : excess, $"The account does not have {(covered > 0 ? "all of " : "")}the strategy's previous position ({(own > 0 ? "long" : "short")} {Qty(Math.Abs(own))}): closed {Qty(excess)} in its ledger at the latest price");
            _lastPos = LocalPosition;
        }
        else
            Log($"Continuing with its open position: {(own > 0 ? "long" : "short")} {Qty(Math.Abs(own))} @ {_data.Instrument.FormatPrice(Ledger.AveragePrice)}");
        if (LocalPosition == 0) ReportOrphanedProtection();
        else if (ProtectOnStart && IsRunning && !_closing) ProtectInherited();
    }

    /// <summary>
    /// Protects the inherited position again, stop first. It adopts the stop and target the previous run kept at the broker
    /// (<see cref="AdoptProtectionIds"/>) and places the ones it still lacks, unless another working order could already be
    /// them: a stop or limit that would close the position, or one of this strategy (left by a crash, placed by hand, or
    /// restored by the broker after a restart with its own comment: "Rithmic", "Tradovate" or none). Such a stop means it
    /// places and replaces nothing; such limits alone still let it place its stop loss, but no take profit. It logs them for
    /// review instead: never duplicated.
    /// </summary>
    private void ProtectInherited()
    {
        bool resize = AdoptKeptProtection();
        string prefix = StrategyName + ": ";
        var exit = LocalPosition > 0 ? OrderDirections.Sell : OrderDirections.Buy;
        var existing = _account.ActiveOrders.Where(o => o.State == OrderStates.Active && (o.Type is OrderTypes.Stop or OrderTypes.StopLimit or OrderTypes.Limit)
            && (o.Direction == exit || o.Comment.StartsWith(prefix, StringComparison.Ordinal))
            && !ReferenceEquals(o, _sl?.Live) && !ReferenceEquals(o, _tp?.Live)).ToList();
        if (existing.Any(o => o.Type is OrderTypes.Stop or OrderTypes.StopLimit))
        {
            if (existing.Count == 1)
                Log($"An order that could be its stop or target is already working in the account ({DescribeLive(existing[0])}): no additional stop or target will be placed (review it in Chart Trader)");
            else
                Log($"{existing.Count} orders that could be its stop or target are already working in the account ({string.Join(", ", existing.Select(DescribeLive))}): no additional stop or target will be placed (review them in Chart Trader)");
            if (resize)
                Log($"The adopted order size does not match the position ({Qty(Math.Abs(LocalPosition))} {ExecInstrument.Symbol}): it is not replaced while another stop is working (review it in Chart Trader)");
            return;
        }

        // only limits can be left: its stop loss is always placed, its take profit only when none of them could be it
        if (existing.Count == 1)
            Log($"A closing limit order is already working in the account ({DescribeLive(existing[0])}): no additional take profit will be placed (review it in Chart Trader)");
        else if (existing.Count > 1)
            Log($"{existing.Count} closing limit orders are already working in the account ({string.Join(", ", existing.Select(DescribeLive))}): no additional take profit will be placed (review them in Chart Trader)");
        _exitSign = Math.Sign(LocalPosition);
        _exitCapacity = _lastExitCapacity = Math.Abs(_account.Position.Volume);
        foreach (var order in existing.Where(o => o.Type == OrderTypes.Limit && o.Direction == exit))
            _observedLimits.TryAdd(order, order.Filled);
        bool target = existing.Count == 0;
        if (resize)
        {
            Log($"The adopted order size does not match the position ({Qty(Math.Abs(LocalPosition))} {ExecInstrument.Symbol}): replaced");
            UpdateProtection(target);
        }
        else PlaceProtection(stop: _sl == null, target: target && _tp == null);
    }

    /// <summary>
    /// Adopts as its stop and target the working orders named in <see cref="AdoptProtectionIds"/> that would close the
    /// inherited position. True if the size of one it adopted does not match the position, so it has to be replaced.
    /// </summary>
    private bool AdoptKeptProtection()
    {
        var exit = LocalPosition > 0 ? OrderDirections.Sell : OrderDirections.Buy;
        var kept = KeptOrders().Where(o => o.Direction == exit).ToList();
        var stop = kept.FirstOrDefault(o => o.Type is OrderTypes.Stop or OrderTypes.StopLimit);
        var target = kept.FirstOrDefault(o => o.Type == OrderTypes.Limit);
        if (stop == null && target == null)
        {
            if (_adoptIds.Count > 0) Log("None of the orders kept at the broker is working for this position: nothing to adopt");
            return false;
        }

        _highWater = _lowWater = LastPrice;
        if (stop != null) _sl = Adopt(stop, StrategyOrderType.Stop, "Stop loss");
        if (target != null) _tp = Adopt(target, StrategyOrderType.Limit, "Take profit");
        Log($"Adopted {(stop != null && target != null ? "the stop loss and take profit" : stop != null ? "the stop loss" : "the take profit")} kept at the broker: " +
            string.Join(", ", new[] { stop, target }.OfType<Order>().Select(DescribeLive)));
        decimal size = Math.Abs(LocalPosition);
        return (stop != null && stop.Unfilled != size) || (target != null && target.Unfilled != size);
    }

    /// <summary>Tracks a working order kept at the broker as its stop or target (what it filled before is settled with the account).</summary>
    private StrategyOrder Adopt(Order live, StrategyOrderType type, string name)
    {
        var so = new StrategyOrder
        {
            Id = _nextId++,
            Side = live.Direction == OrderDirections.Buy ? OrderSide.Buy : OrderSide.Sell,
            Type = type,
            Quantity = live.Unfilled,
            Price = type == StrategyOrderType.Stop && live.TriggerPrice > 0 ? live.TriggerPrice : live.Price,
            Name = name,
            Time = Time,
            IsProtective = true,
            Live = live,
            LiveQuantity = live.QuantityToFill,
            Booked = live.Filled,
            ExecutionBaseline = live.Filled,
        };
        SyncExecutions(out _); // Historical unowned reports are not new fills of this adopted exit.
        var prior = new Dictionary<ExecutionIdentity, decimal>();
        List<ExecutionIdentity>? ownedPrior = null;
        List<ExecutionIdentity>? importedPrior = null;
        bool baselineKnown = so.Booked == 0;
        if (so.Booked > 0)
        {
            // Adoption is a reconciliation path, not an ordinary append. Capture the prior identities once so
            // a later price/fee correction of one cannot become a new execution of the inherited position.
            var captured = (_account as IAccountExecutionSource)?.GetExecutions(live);
            var replay = captured?.ExecutionFacts is { } facts
                ? new ExecutionSnapshot(facts.Count, facts, captured.CompleteCoverage ? ExecutionAvailability.Ready : ExecutionAvailability.Synchronizing)
                : _account.Executions.Capture(ExecutionCursor.Start);
            foreach (var report in replay.Changes)
            {
                if (!_account.OrderContext.MatchesExecution(live, report)) continue;
                prior[report.Identity] = report.Quantity;
                var known = Ledger.Execution(report.Identity);
                if (known?.AccountingOrder is { } physical && physical.Order == report.NativeOrder &&
                    known.ContextId == _account.OrderContext.Descriptor.Id && known.OwnerId == _owner.Id &&
                    known.OwnerGeneration == _owner.LedgerGeneration && known.Symbol == report.Symbol &&
                    known.Buy == report.Buy && known.PointValue == report.PointValue)
                    (ownedPrior ??= new()).Add(report.Identity);
                else if (Ledger.ImportedExecution(report.Identity, report.Symbol, report.Buy, report.PointValue) != null)
                {
                    (ownedPrior ??= new()).Add(report.Identity);
                    (importedPrior ??= new()).Add(report.Identity);
                }
            }
            decimal priorQuantity = 0;
            foreach (decimal quantity in prior.Values) priorQuantity += quantity;
            baselineKnown = replay.Availability == ExecutionAvailability.Ready && priorQuantity == so.Booked;
            if (!baselineKnown)
                Log("The adopted order's prior execution baseline is unconfirmed: quantity protection continues, but financial attribution remains pending.");
        }
        var claim = _account.OrderContext.Adopt(live, _owner, so.Booked, prior.Keys.ToArray(), baselineKnown, protective: true,
            ownedPrior: baselineKnown ? ownedPrior?.ToArray() : null, importedPrior: baselineKnown ? importedPrior?.ToArray() : null);
        var owned = new OwnedExecutionOrder(so, claim.LogicalOrderId, claim.QuantityBaseline)
        {
            ExecutionBaselineKnown = claim.BaselineKnown,
            PriorExecutions = claim.PriorExecutions?.ToDictionary(id => id, _ => 0m),
        };
        foreach (var fill in Ledger.Fills)
        {
            if (fill.Kind != LiveFillKind.Execution || fill.OrderId != claim.LogicalOrderId || fill.ExecutionId == null) continue;
            var report = new ExecutionReport(fill.ExecutionId, live.Id, fill.Symbol ?? ExecInstrument.Symbol, fill.Time,
                fill.Buy, fill.Quantity, fill.Price, fill.PointValue, fill.Currency, fill.Fees ?? Array.Empty<ExecutionFee>(), fill.FeesComplete);
            _executionReports[report.Identity] = report;
            (owned.Reports ??= new()).Add(report.Identity);
            owned.Quantity += report.Quantity;
            owned.Value += report.Quantity * report.Price;
        }
        _executionOrders.TryAdd(live.Id, owned);
        _orders.Add(so);
        return so;
    }

    /// <summary>Working orders in the account named in <see cref="AdoptProtectionIds"/> (not being canceled by a strategy).</summary>
    private List<Order> KeptOrders()
    {
        if (_adoptIds.Count == 0) return new List<Order>();
        var ids = new HashSet<string>(_adoptIds, StringComparer.Ordinal);
        lock (CanceledOrders)
            return _account.ActiveOrders.Where(o => o.State == OrderStates.Active && MatchesIdentity(ids, o) && !CanceledOrders.Contains(o.Id)).ToList();
    }

    /// <summary>The inherited position is not in the account: orders kept at the broker for it are reported, not adopted.</summary>
    private void ReportOrphanedProtection()
    {
        var kept = KeptOrders();
        if (kept.Count > 0)
            Log($"Orders kept at the broker for its position are still working ({string.Join(", ", kept.Select(DescribeLive))}), but the account no longer has the position: review them in Chart Trader");
    }

    /// <summary>Durable provider client identity when available, otherwise the exchange ID or the legacy process-local fallback.</summary>
    private string IdentityOf(Order o) => StableIdentityOf(o) ?? (o.ExchangeOrderId.Length > 0 ? o.ExchangeOrderId : LocalIdentityOf(o));

    private string? StableIdentityOf(Order order) => _account is IStableOrderIdentity provider && provider.StableOrderIdentityOf(order) is { Length: > 0 } identity
        ? "client:" + identity : null;

    private bool MatchesIdentity(HashSet<string> identities, Order order) => identities.Contains(order.ExchangeOrderId) ||
        identities.Contains(LocalIdentityOf(order)) || (StableIdentityOf(order) is { } stable && identities.Contains(stable));

    private static string LocalIdentityOf(Order o) => $"local:{LocalSession}:{o.Id.ToString(Inv)}";

    private IReadOnlyList<string> WorkingProtectionIds()
    {
        var ids = new List<string>(2);
        if (_sl is { IsActive: true, Live: Order sl }) ids.Add(IdentityOf(sl));
        if (_tp is { IsActive: true, Live: Order tp }) ids.Add(IdentityOf(tp));
        return ids;
    }

    /// <summary>A broker order for the log: «sell stop 1 MNQ @ 20,150.25 · Rithmic · ID 123».</summary>
    private string DescribeLive(Order o)
    {
        string type = o.Type switch { OrderTypes.Limit => "limit", OrderTypes.StopLimit => "stop limit", OrderTypes.Market => "market", _ => "stop" };
        decimal price = (o.Type is OrderTypes.Stop or OrderTypes.StopLimit) && o.TriggerPrice > 0 ? o.TriggerPrice : o.Price;
        return $"{(o.Direction == OrderDirections.Buy ? "buy" : "sell")} {type} {Qty(o.Unfilled)} {ExecInstrument.Symbol}" +
               (o.Type == OrderTypes.Market ? "" : " @ " + ExecInstrument.FormatPrice(price)) +
               (o.Comment.Length > 0 ? " · " + o.Comment : "") + (o.ExchangeOrderId.Length > 0 ? " · ID " + o.ExchangeOrderId : "");
    }

    // ----------------------------------------------------------------- órdenes

    private void SyncOrders()
    {
        Interlocked.Exchange(ref _syncAgain, 1);
        if (_bootstrapping || _placing > 0 || _strategyCallbacks > 0) return;
        if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0) return;
        try
        {
            do
            {
                Interlocked.Exchange(ref _syncAgain, 0);
                SyncOrdersCore();
            }
            while (Volatile.Read(ref _syncAgain) != 0);
        }
        finally
        {
            Volatile.Write(ref _syncing, 0);
            if (Volatile.Read(ref _syncAgain) != 0) SyncOrders();
        }
    }

    private void SyncOrdersCore()
    {
        if (!IsRunning && !_closing && !HasWorkingOrders)
        {
            // A saved-ID cancellation can settle without a tracked StrategyOrder to enter the loop below.
            // Drain its final facts, then release the stopped observer rather than retaining this runner.
            if (_projectionSubscription?.Active == false)
            {
                SyncQuantities();
                SyncExecutions(out _);
                SyncCommittedLedger();
                HookAccount(false);
            }
            return;
        }
        decimal previousAverage = Ledger.AveragePrice;
        bool previousPricesPending = Ledger.HasPendingPrices;
        if (_projectionSubscription?.Retired == true)
        {
            _historyRetired = true;
            StopReason = "This ledger generation was explicitly retired; original exposure remains managed without recreating its history.";
            SetPaused(true);
        }
        CancelSavedProtection();
        bool changed = SyncCommittedLedger();
        if (changed)
        {
            // Shared durable facts may be ahead of this provider's native snapshot.
            // Its own captured quantities and execution reports drive protection reconciliation.
            _lastPos = LocalPosition;
            previousAverage = Ledger.AveragePrice;
            previousPricesPending = Ledger.HasPendingPrices;
        }
        bool capturedQuantityChanged = SyncQuantities();
        changed |= capturedQuantityChanged;
        bool executionObserved = false;
        foreach (var so in _orders.ToList())
        {
            if (so.Live is not Order o) continue;
            bool done = o.Status == OrderStatus.Filled || (o.State == OrderStates.Done && o.Unfilled == 0 && !o.Canceled);
            decimal filled = o.Filled;
            if (done && filled <= 0) filled = so.LiveQuantity;
            filled = Math.Min(filled, so.LiveQuantity);
            if (filled > so.Booked)
            {
                // A native handle can change before its immutable owned-quantity capture is published.
                // Wait for that capture instead of inventing an unscoped second accounting observation.
                if (_account.OrderContext.Descriptor.Provider != "simulator") continue;
                decimal newlyFilled = filled - so.Booked;
                Book(so, newlyFilled);
                so.Booked = filled;
                changed = true;
            }

            changed |= SyncExecutions(out bool ownExecutionObserved);
            executionObserved |= ownExecutionObserved;
            if (done)
            {
                so.State = StrategyOrderState.Filled;
                // Execution reports, not the last market print, set FillPrice/FillTime.
                _orders.Remove(so);
                Fills++;
                changed = true;
                Log($"Filled: {Describe(so)}");
                Executed?.Invoke(this, ExecutedText(so, filled));
                if (!so.IsProtective && IsRunning) _strategy.InternalFilled(so);
                ForgetTerminalExecutionOrder(o.Id);
            }
            else if (o.Canceled || o.State == OrderStates.Failed)
            {
                if (o.State == OrderStates.Failed) Log($"Rejected: {Describe(so)} ({_account.LastError})");
                so.State = StrategyOrderState.Canceled;
                _orders.Remove(so);
                changed = true;
                ForgetTerminalExecutionOrder(o.Id);
            }
        }
        changed |= SyncExecutions(out bool finalExecutionObserved);
        executionObserved |= finalExecutionObserved;
        if (FinancialPauseRequired && IsRunning && !Paused)
        {
            Log("Execution facts needed for position pricing or configured risk limits are pending: new exposure is paused; reductions and protection remain managed.");
            SetPaused(true);
        }

        _cancelPending.RemoveWhere(o => o.State != OrderStates.Active);
        if (_observedLimits.Count > 0) changed |= CoordinateObservedExits();

        decimal pos = LocalPosition;
        bool quantityChanged = pos != _lastPos;
        bool basisChanged = pos != 0 && (Ledger.AveragePrice != previousAverage || Ledger.HasPendingPrices != previousPricesPending);
        if (quantityChanged || basisChanged || capturedQuantityChanged || executionObserved)
        {
            _lastPos = pos;
            if (IsRunning && !_closing)
                UpdateProtection(target: _observedLimits.Count == 0);
            changed = true;
        }
        CancelRestoredFlatProtection();

        if (_awaitingAccount && IsRunning && !_closing) CheckInheritedPosition(initial: false);
        if (_protectionRefresh && !_closing && !_cancelPending.Any(o => o.State == OrderStates.Active))
        {
            _protectionRefresh = false;
            PlaceProtection(stop: _sl is not { IsActive: true },
                target: _refreshTarget && _tp is not { IsActive: true } && _observedLimits.Count == 0);
        }
        if (_closeReason != null && _account.PositionReady && _account.OrdersReady &&
            _cancelIds.Count == 0 && !_cancelPending.Any(o => o.State == OrderStates.Active) &&
            !_orders.Any(o => o.IsActive && o.Type == StrategyOrderType.Market))
        {
            if (_submittedClose is { State: StrategyOrderState.Filled, Live: Order { State: OrderStates.Done, Status: OrderStatus.Filled } terminal } &&
                terminal.Filled >= terminal.QuantityToFill &&
                (_owner == null || !_account.OrderContext.HasUnprojectedQuantity(_owner)))
                _submittedClose = null;
            if (LocalPosition != 0 && _submittedClose == null)
                _submittedClose = SendClose(_closeReason);
            if (LocalPosition == 0)
            {
                _closeReason = null;
                _submittedClose = null;
                _closing = false;
                _awaitingAccount = false;
            }
        }
        if (!IsRunning && !_closing && !HasWorkingOrders) HookAccount(false);
        if (changed)
        {
            CheckLimits();
            Changed?.Invoke();
        }
    }

    private void CancelSavedProtection()
    {
        if (_cancelIds.Count == 0) return;
        var ids = new HashSet<string>(_cancelIds, StringComparer.Ordinal);
        foreach (var live in _account.ActiveOrders.Where(o => MatchesIdentity(ids, o)).ToList())
        {
            if (!_orders.Any(o => ReferenceEquals(o.Live, live)))
                Adopt(live, live.Type is OrderTypes.Stop or OrderTypes.StopLimit ? StrategyOrderType.Stop : StrategyOrderType.Limit, "Pending protection cancellation");
            Cancel(_orders.First(o => ReferenceEquals(o.Live, live)));
        }
        if (_account.OrdersReady) _cancelIds.Clear();
    }

    private bool CoordinateObservedExits()
    {
        if (ForeignOpenContext) return false;
        bool reduced = false;
        foreach (var (order, booked) in _observedLimits.ToList())
        {
            decimal filled = Math.Max(booked, order.Filled);
            if (filled > booked)
            {
                _exitCapacity = Math.Max(0, _exitCapacity - (filled - booked));
                _observedLimits[order] = filled;
                reduced = true;
            }
        }
        // Order fills and PnL updates can arrive on independent broker read loops. Reconcile ownership from
        // the observed execution, not from a possibly earlier/later position message (and never book it twice).
        if (reduced && Math.Sign(LocalPosition) == _exitSign && Math.Abs(LocalPosition) > _exitCapacity)
        {
            decimal reduction = Math.Abs(LocalPosition) - _exitCapacity;
            BookSynthetic(-_exitSign * reduction, $"The account's observed closing orders reduced its position by {Qty(reduction)}: reconciled its ledger and protection");
            reduced = true;
        }
        decimal actual = _account.PositionReady
            ? Math.Sign(_account.Position.Volume) == _exitSign ? Math.Abs(_account.Position.Volume) : 0
            : _exitCapacity;
        decimal capacity = Math.Min(actual, _exitCapacity);
        bool adjust = reduced || _exitCapacity < _lastExitCapacity || _observedResize.Count > 0;
        _lastExitCapacity = _exitCapacity;
        if (!adjust) return reduced;
        foreach (var order in _observedLimits.Keys.ToList())
        {
            if (_observedResize.TryGetValue(order, out decimal requested) && order.Unfilled <= requested)
                _observedResize.Remove(order);
            if (order.State != OrderStates.Active)
            {
                _observedResize.Remove(order);
                continue;
            }
            if (_cancelPending.Contains(order)) continue;
            if (capacity == 0)
            {
                _cancelPending.Add(order);
                _account.Cancel(order);
            }
            else if (order.Unfilled > capacity && !_observedResize.ContainsKey(order))
            {
                _observedResize[order] = capacity;
                var replacement = OrderActions.Replace(_account, order, order.Price, capacity);
                if (!ReferenceEquals(replacement, order))
                {
                    _observedLimits.Remove(order);
                    _observedLimits[replacement] = replacement.Filled;
                    _observedResize.Remove(order);
                    _observedResize[replacement] = capacity;
                }
            }
        }
        return reduced;
    }
    private void CancelRestoredFlatProtection()
    {
        if (!_restoredLocal || LocalPosition != 0 || Ledger.HasPendingPrices) return;
        _placing++;
        try
        {
            foreach (var owned in _executionOrders.Values)
                if (owned.Order.IsProtective && owned.Order.Live is Order live && live.State == OrderStates.Active) _account.Cancel(live);
        }
        finally { _placing--; }
    }


    /// <summary>Contabiliza lo ejecutado de una orden y actualiza la posición en unidades de la estrategia.</summary>
    private void Book(StrategyOrder so, decimal quantity, bool persist = true, LiveFill? observation = null)
    {
        if (observation == null)
        {
            var owned = _executionOrders[((Order)so.Live!).Id];
            observation = new LiveFill(_account is IObservedMarketPrice ? Time : DateTime.UtcNow, so.Side == OrderSide.Buy, quantity, 0,
                ExecInstrument.Symbol, ExecInstrument.PointValue)
            {
                Kind = LiveFillKind.QuantityObservation, OrderId = owned.LedgerId,
                CumulativeQuantity = so.Booked + quantity - owned.QuantityBaseline,
                ContextId = _account.OrderContext.Descriptor.Id,
                OwnerId = _owner.Id, OwnerGeneration = _owner.LedgerGeneration,
            };
        }
        ApplyLedgerFill(observation, allocationOrder: so);
        if (persist) SaveFill(observation);
    }

    /// <summary>Updates strategy allocation and observed-exit capacity once at the canonical position change,
    /// independently of native order counters and whichever projection path arrives first.</summary>
    private bool ApplyLedgerFill(LiveFill fill, StrategyOrder? allocationOrder = null, OwnedOrderIntent? allocationIntent = null)
    {
        // An amendment carries remaining native quantity, not a new strategy-allocation fraction.
        if (allocationOrder == null && _owner != null && fill.ContextId == _account.OrderContext.Descriptor.Id &&
            fill.OwnerId == _owner.Id && fill.OwnerGeneration == _owner.LedgerGeneration &&
            !string.IsNullOrEmpty(fill.OrderId))
        {
            foreach (var owned in _executionOrders.Values)
            {
                if (owned.LedgerId != fill.OrderId) continue;
                allocationOrder = owned.Order;
                break;
            }
            if (allocationOrder == null)
            {
                var intent = _account.OrderContext.Find(fill.OrderId);
                if (intent != null && intent.LogicalOrderId == fill.OrderId && intent.Owner == _owner &&
                    intent.ContextId == _account.OrderContext.Descriptor.Id &&
                    intent.Generation == _account.OrderContext.Descriptor.Generation)
                {
                    allocationIntent = intent;
                }
            }
        }
        bool? protective = allocationOrder?.IsProtective ?? allocationIntent?.Request.Protective;
        decimal? virtualQuantity = allocationOrder?.Quantity ?? allocationIntent?.Request.StrategyQuantity;
        decimal nativeQuantity = allocationOrder?.LiveQuantity ?? allocationIntent?.Request.Quantity ?? 0;
        if (allocationOrder == null && allocationIntent != null &&
            allocationIntent.Request.StrategyQuantity <= 0 && allocationIntent.VirtualPerUnit > 0)
        {
            virtualQuantity = allocationIntent.VirtualPerUnit;
            nativeQuantity = 1;
        }
        long revision = Ledger.Revision;
        decimal before = LocalPosition;
        Ledger.Apply(fill);
        decimal after = LocalPosition;
        if (after != before)
        {
            AdjustObservedExitCapacity(before);
            if (after == 0) _virtual = 0;
            else if (protective != false || virtualQuantity == null || nativeQuantity <= 0)
                _virtual = before != 0 && Math.Sign(before) == Math.Sign(after)
                    ? _virtual * after / before : ToVirtual(after);
            else
            {
                // Preserve the submitted fraction: dividing first can round whole strategy units.
                _virtual += (after - before) * virtualQuantity.Value / nativeQuantity;
                if (_virtual == 0 || Math.Sign(_virtual) != Math.Sign(after)) _virtual = ToVirtual(after);
            }
        }
        return Ledger.Revision != revision;
    }

    private void AdjustObservedExitCapacity(decimal previousPosition)
    {
        if (_observedLimits.Count > 0 && LocalPosition != previousPosition)
            _exitCapacity = Math.Max(0, _exitCapacity + _exitSign * (LocalPosition - previousPosition));
    }

    private bool SyncQuantities()
    {
        bool changed = false;
        foreach (var update in _projectionSubscription?.DrainQuantities() ?? Array.Empty<OwnedQuantityUpdate>())
        {
            var observation = update.Observation;
            var owned = _executionOrders.Values.FirstOrDefault(order => order.LedgerId == observation.Intent.LogicalOrderId);
            if (owned != null)
            {
                decimal nativeQuantity = owned.QuantityBaseline + observation.CumulativeQuantity;
                if (nativeQuantity > owned.Order.Booked)
                {
                    Book(owned.Order, nativeQuantity - owned.Order.Booked, persist: false, observation: update.Fill);
                    owned.Order.Booked = nativeQuantity;
                    changed = true;
                }
            }
            else
            {
                changed |= ApplyLedgerFill(update.Fill, allocationIntent: observation.Intent);
            }
        }
        return changed;
    }

    private bool SyncCommittedLedger()
    {
        bool changed = false;
        var subscription = _projectionSubscription;
        var updates = subscription?.DrainCommitted() ?? Array.Empty<LiveFill>();
        foreach (var fill in updates)
        {
            // A durable financial fact updates this book's allocation, never native order counters
            // or ownership. The same canonical position delta cannot be applied again by a later path.
            if (!ApplyLedgerFill(fill)) continue;
            _ownershipMismatch = ForeignOpenContext;
            changed = true;
        }
        subscription?.AcknowledgeCommitted(updates.Count);
        return changed;
    }

    private bool SyncExecutions(out bool observed)
    {
        bool changed = false;
        observed = false;
        foreach (var update in _projectionSubscription?.Drain() ?? Array.Empty<OwnedExecutionUpdate>())
        {
            var report = update.Report;
            _executionOrders.TryGetValue(report.LocalOrderId, out var owned);
            if (owned?.LedgerId != update.Intent.LogicalOrderId)
                owned = _executionOrders.Values.FirstOrDefault(order => order.LedgerId == update.Intent.LogicalOrderId);
            if (owned != null && !owned.ExecutionBaselineKnown) continue;
            observed = true;
            // Corrections of already-owned pre-adoption executions update money, never this successor's native fill counter.
            if (owned != null && owned.PriorExecutions?.ContainsKey(report.Identity) != true)
            {
                _executionReports.TryGetValue(report.Identity, out var previous);
                if (previous != null) { owned.Quantity -= previous.Quantity; owned.Value -= previous.Quantity * previous.Price; }
                else (owned.Reports ??= new()).Add(report.Identity);
                _executionReports[report.Identity] = report;
                owned.Quantity += report.Quantity;
                owned.Value += report.Quantity * report.Price;
                owned.Order.FillPrice = owned.Quantity > 0 ? owned.Value / owned.Quantity : 0;
                owned.Order.FillTime = report.Time;
            }
            changed |= ApplyLedgerFill(update.Fill, allocationOrder: owned?.Order, allocationIntent: update.Intent);
        }
        return changed;
    }

    private void ForgetTerminalExecutionOrder(long localId)
    {
        if (!_executionOrders.Remove(localId, out var owned)) return;
        if (owned.Reports != null)
            foreach (var identity in owned.Reports) _executionReports.Remove(identity);
    }

    /// <summary>Asiento sin orden (lo que la cuenta no tiene): se cierra al último precio.</summary>
    private void BookSynthetic(decimal signedQuantity, string why)
    {
        if (signedQuantity == 0 || ForeignOpenContext) return;
        decimal before = LocalPosition;
        decimal price = MarkPriceKnown ? MarkPrice : _data.LastPrice > 0 ? _data.LastPrice : Ledger.AveragePrice;
        var fill = new LiveFill(_account is IObservedMarketPrice ? Time : DateTime.UtcNow, signedQuantity > 0, Math.Abs(signedQuantity), price,
            Ledger.OpenSymbol ?? ExecInstrument.Symbol, Ledger.OpenPointValue > 0 ? Ledger.OpenPointValue : ExecInstrument.PointValue)
        { ContextId = _account.OrderContext.Descriptor.Id };
        Ledger.Apply(fill);
        SaveFill(fill);
        decimal after = LocalPosition;
        _virtual = after == 0 ? 0 : before != 0 && Math.Sign(before) == Math.Sign(after) ? _virtual * after / before : ToVirtual(after);
        Log(why);
    }

    /// <summary>
    /// Saves every observed fill even when advisory pending-state inspection fails. A persistence failure pauses new
    /// exposure without stopping owned protection, closing, cancellation acknowledgments or late-fill accounting.
    /// </summary>
    private void SaveFill(LiveFill fill)
    {
        if (!Persist) return;
        string root = PersistenceRoot();
        if (_owner != null && fill.OwnerId == null)
            fill = fill with { ContextId = fill.ContextId ?? _account.OrderContext.Descriptor.Id,
                OwnerId = _owner.Id, OwnerGeneration = _owner.LedgerGeneration };
        int waiting = 0;
        try { waiting = LiveStore.PendingLedgerEvents(root, Key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // AppendFill must still receive this observed event; it retains it and reports the actual storage error.
        }
        string? error;
        if (_owner != null)
        {
            var result = _projector != null
                ? _projector.CommitOwnedEvent(_owner, fill)
                : LiveStore.AppendOwnedEvent(root, Key, _owner.LedgerGeneration, fill);
            if (result.Status == LiveStore.ExecutionProjectionStatus.Retired)
            {
                _historyRetired = true;
                StopReason = "This ledger generation was explicitly retired; old observations cannot recreate its history.";
                SetPaused(true);
                Log(StopReason);
                return;
            }
            error = result.Error;
        }
        else error = LiveStore.AppendFill(root, Key, fill);
        if (error != null)
        {
            Log($"Could not save a ledger event ({error.TrimEnd('.')}): it stays pending and is saved before the next event; new entries are paused");
            try { SetPaused(true); }
            catch (Exception ex)
            {
                // Paused is already set before cancellation. Do not lose this event if a broker cancel fails.
                Log($"New entries remain paused; entry cancellation or its notification failed ({ex.Message}). Reconcile the working orders.");
            }
        }
        else if (waiting > 0)
            Log($"Saved {waiting} pending ledger event{(waiting == 1 ? "" : "s")} to its ledger; resume manually when ready");
        _projector?.NotifyCommittedFacts();
    }

    /// <summary>Manda el cierre de la posición propia (nunca más de lo que la cuenta tiene en ese sentido).</summary>
    private StrategyOrder? SendClose(string why)
    {
        decimal own = LocalPosition;
        if (own == 0) return null;
        if (!_account.PositionReady || !_account.OrdersReady) return null;
        decimal acc = _account.Position?.Volume ?? 0;
        decimal closable = Math.Sign(acc) == Math.Sign(own) ? Math.Min(Math.Abs(acc), Math.Abs(own)) : 0;
        decimal excess = Math.Abs(own) - closable;
        if (excess > 0 && ForeignOpenContext) return null;
        if (excess > 0 && _account.OrderContext.Descriptor.Provider == "simulator")
            BookSynthetic(own > 0 ? -excess : excess, $"The account did not have {Qty(excess)} of the strategy's position: it was closed in its ledger at the latest price");
        if (closable <= 0) return null;
        var so = new StrategyOrder { Id = _nextId++, Side = own > 0 ? OrderSide.Sell : OrderSide.Buy, Type = StrategyOrderType.Market, Quantity = closable, Name = why, Time = Time, IsProtective = true };
        Place(so, closable);
        Interlocked.Exchange(ref _syncAgain, 1); // synchronous fills can arrive while Place suppresses callbacks
        return so;
    }

    /// <summary>Refreshes only changed owned protection legs through cancellation acknowledgment.
    /// Matching native remaining quantities and prices survive partial fills and price finalization.</summary>
    private void UpdateProtection(bool target = true)
    {
        decimal pos = LocalPosition;
        bool awaitingNativeTerminal = pos == 0 && _sl is { Live: Order executing } currentStop &&
            !_cancelPending.Contains(executing) && FindCurrentExecutingStop(currentStop) is { } executingIntent &&
            LatestKnownQuantity(currentStop, executing, executingIntent) >= executing.QuantityToFill;
        int side = Math.Sign(pos);
        if (_refreshStopBound is { } pendingBound && pendingBound.Side != side) _refreshStopBound = null;
        if (side != 0 && _sl is { IsActive: true } previousStop &&
            previousStop.Side == (side > 0 ? OrderSide.Sell : OrderSide.Buy))
        {
            var live = previousStop.Live as Order;
            decimal nativePrice = live?.TriggerPrice ?? 0;
            bool untouchedProvisional = _provisionalStop is { } provisional &&
                ReferenceEquals(provisional.Order, previousStop) && previousStop.Price == provisional.Price &&
                live is { Type: OrderTypes.Stop or OrderTypes.StopLimit } && live.IntentId == provisional.IntentId &&
                (nativePrice <= 0 || nativePrice == ExecInstrument.RoundPrice(provisional.Price));
            if (!untouchedProvisional)
            {
                _provisionalStop = null;
                decimal bound = previousStop.Price;
                if (nativePrice > 0)
                    bound = bound > 0 ? (side > 0 ? Math.Max(bound, nativePrice) : Math.Min(bound, nativePrice)) : nativePrice;
                if (bound > 0)
                {
                    if (_refreshStopBound is { } retained)
                        bound = side > 0 ? Math.Max(bound, retained.Price) : Math.Min(bound, retained.Price);
                    _refreshStopBound = (side, bound);
                }
            }
        }
        (decimal Stop, decimal Target) prices = awaitingNativeTerminal ? default : ProtectionPrices(pos);
        bool keepStop = awaitingNativeTerminal ||
            (pos != 0 && prices.Stop > 0 && MatchesProtection(_sl, pos, prices.Stop, stop: true));
        bool keepTarget = pos != 0 && target && prices.Target > 0 && MatchesProtection(_tp, pos, prices.Target, stop: false);
        _protectionRefresh = true;
        _refreshTarget = target;
        _placing++;
        try
        {
            if (!keepStop)
            {
                if (_sl is { IsActive: true }) Cancel(_sl);
                _sl = null;
            }
            if (!keepTarget)
            {
                if (_tp is { IsActive: true }) Cancel(_tp);
                _tp = null;
            }
        }
        finally { _placing--; }
        if (!_cancelPending.Any(o => o.State == OrderStates.Active))
        {
            _protectionRefresh = false;
            PlaceProtection(stop: !keepStop, target: target && !keepTarget && _observedLimits.Count == 0);
        }
    }

    private bool NativeExitAwaitingAcknowledgment
    {
        get
        {
            if (_owner == null || Ledger == null || LocalPosition != 0) return false;
            foreach (var order in _orders)
                if (FindCurrentExecutingStop(order) != null) return true;
            return false;
        }
    }

    private OwnedOrderIntent? FindCurrentExecutingStop(StrategyOrder? order) =>
        order is { IsActive: true, IsProtective: true, Type: StrategyOrderType.Stop, Live: Order live } &&
        live.Type == OrderTypes.Market &&
        live.Direction == (order.Side == OrderSide.Sell ? OrderDirections.Sell : OrderDirections.Buy)
            ? _account.OrderContext.FindExecutingProtection(live, _owner) : null;

    private decimal LatestKnownQuantity(StrategyOrder order, Order live, OwnedOrderIntent intent)
    {
        if (live.NativeIdentity is { } native)
        {
            var instrument = ExecInstrument;
            if (Ledger.KnownPhysicalQuantity(intent, native, instrument.Symbol, instrument.PointValue) is { } known)
                return known;
        }
        return _executionOrders.TryGetValue(live.Id, out var owned) && ReferenceEquals(owned.Order, order) && owned.ExecutionBaselineKnown
            ? owned.QuantityBaseline + owned.Quantity : live.Filled;
    }

    private bool MatchesProtection(StrategyOrder? strategyOrder, decimal position, decimal price, bool stop)
    {
        if (strategyOrder is not { IsActive: true, Live: Order live } ||
            live.State != OrderStates.Active || live.Canceled || _cancelPending.Contains(live)) return false;
        if (live.Direction != (position > 0 ? OrderDirections.Sell : OrderDirections.Buy)) return false;
        if (stop && live.Type == OrderTypes.Market)
        {
            var intent = FindCurrentExecutingStop(strategyOrder);
            if (intent == null) return false;
            // Revisions replace scoped effective units, never the broker handle's cumulative counter.
            decimal remaining = live.Unfilled - Math.Max(0, LatestKnownQuantity(strategyOrder, live, intent) - live.Filled);
            return remaining == Math.Abs(position);
        }
        if (live.Unfilled != Math.Abs(position)) return false;
        if (stop ? live.Type is not (OrderTypes.Stop or OrderTypes.StopLimit) : live.Type != OrderTypes.Limit) return false;
        decimal nativePrice = stop && live.TriggerPrice > 0 ? live.TriggerPrice : live.Price;
        return nativePrice == ExecInstrument.RoundPrice(price);
    }

    private (decimal Stop, decimal Target) ProtectionPrices(decimal pos)
    {
        if (pos == 0) { _localProtectionPrices = null; return default; }
        int side = Math.Sign(pos);
        if (ForeignOpenContext || Ledger.AveragePrice <= 0)
        {
            // Neither another context nor a blended account average can price this owned lot.
            // Keep acknowledged/local provisional protection until its own execution basis is available.
            var retained = _localProtectionPrices is { } local && local.Side == side ? local : default;
            decimal retainedStop = AcknowledgedProtectionPrice(_sl, pos, stop: true);
            decimal retainedTarget = AcknowledgedProtectionPrice(_tp, pos, stop: false);
            if (retainedStop <= 0) retainedStop = retained.Stop;
            if (retainedTarget <= 0) retainedTarget = retained.Target;
            if (ForeignOpenContext || retainedStop > 0 || retainedTarget > 0)
            {
                _localProtectionPrices = (side, retainedStop, retainedTarget);
                return (retainedStop, retainedTarget);
            }
        }
        decimal avg = Ledger.AveragePrice;
        if (avg <= 0)
            avg = _account.LastPrice > 0 ? _account.LastPrice : _data.LastPrice;
        if (avg <= 0) return default;
        var inst = _data.Instrument;
        int stopTicks = _strategy.StopLossTicks > 0 ? _strategy.StopLossTicks : _strategy.TrailingStopTicks;
        decimal stop = stopTicks > 0 ? inst.RoundPrice(pos > 0 ? avg - stopTicks * inst.TickSize : avg + stopTicks * inst.TickSize) : 0;
        if (stop > 0 && _refreshStopBound is { } bound && bound.Side == Math.Sign(pos))
            stop = pos > 0 ? Math.Max(stop, bound.Price) : Math.Min(stop, bound.Price);
        decimal target = _strategy.TakeProfitTicks > 0
            ? inst.RoundPrice(pos > 0 ? avg + _strategy.TakeProfitTicks * inst.TickSize : avg - _strategy.TakeProfitTicks * inst.TickSize) : 0;
        _localProtectionPrices = (side, stop, target);
        return (stop, target);
    }

    private static decimal AcknowledgedProtectionPrice(StrategyOrder? strategyOrder, decimal position, bool stop)
    {
        if (strategyOrder?.Live is not Order live || live.State != OrderStates.Active || live.Canceled ||
            live.Direction != (position > 0 ? OrderDirections.Sell : OrderDirections.Buy)) return 0;
        if (stop ? live.Type is not (OrderTypes.Stop or OrderTypes.StopLimit) : live.Type != OrderTypes.Limit) return 0;
        return stop && live.TriggerPrice > 0 ? live.TriggerPrice : live.Price;
    }

    /// <summary>Places required protection only when the account can admit its full reduce-only quantity.
    /// A fill may precede the native position snapshot; keep the existing refresh pending until it reconciles.</summary>
    private void PlaceProtection(bool stop, bool target)
    {
        decimal pos = LocalPosition;
        if (pos == 0) { _refreshStopBound = null; _provisionalStop = null; return; }
        decimal qty = Math.Abs(pos);
        int stopTicks = _strategy.StopLossTicks > 0 ? _strategy.StopLossTicks : _strategy.TrailingStopTicks;
        if ((!stop || stopTicks <= 0) && (!target || _strategy.TakeProfitTicks <= 0)) return;
        var position = _account.Position;
        decimal accountPosition = position?.Volume ?? 0;
        if (position == null || !_account.PositionReady || !_account.OrdersReady || Math.Sign(accountPosition) != Math.Sign(pos) ||
            Math.Abs(accountPosition) < qty)
        {
            _protectionRefresh = true;
            _refreshTarget = target;
            return;
        }
        var prices = ProtectionPrices(pos);
        _placing++;
        try
        {
            var exit = pos > 0 ? OrderSide.Sell : OrderSide.Buy;
            if (stop && prices.Stop > 0)
            {
                if (_refreshStopBound is { } bound && bound.Side == Math.Sign(pos))
                {
                    _highWater = Math.Max(_highWater, LastPrice);
                    _lowWater = Math.Min(_lowWater, LastPrice);
                }
                else _highWater = _lowWater = LastPrice;
                bool provisional = Ledger.AveragePrice <= 0 && _refreshStopBound == null;
                _sl = Protective(exit, StrategyOrderType.Stop, qty, prices.Stop, "Stop loss");
                _provisionalStop = provisional ? (_sl, prices.Stop, (_sl.Live as Order)?.IntentId) : null;
            }

            if (target && prices.Target > 0)
                _tp = Protective(exit, StrategyOrderType.Limit, qty, prices.Target, "Take profit");
            _refreshStopBound = null;
        }
        finally { _placing--; }
    }

    private StrategyOrder Protective(OrderSide side, StrategyOrderType type, decimal qty, decimal price, string name)
    {
        var so = new StrategyOrder { Id = _nextId++, Side = side, Type = type, Quantity = qty, Price = price, Name = name, Time = Time, IsProtective = true };
        return Place(so, qty);
    }

    private void Trail(decimal price)
    {
        if (ForeignOpenContext) return; // Do not re-anchor local trailing protection from conflicted financial prices.
        if (_strategy.TrailingStopTicks <= 0 || _sl is not { IsActive: true } || _sl.Live is not Order o ||
            o.Type == OrderTypes.Market) return;
        decimal pos = LocalPosition;
        decimal dist = _strategy.TrailingStopTicks * _data.Instrument.TickSize;
        decimal? target = null;
        if (pos > 0 && price > _highWater)
        {
            _highWater = price;
            decimal stop = ExecInstrument.RoundPrice(price - dist);
            if (stop > _sl.Price) target = stop;
        }
        else if (pos < 0 && price < _lowWater)
        {
            _lowWater = price;
            decimal stop = ExecInstrument.RoundPrice(price + dist);
            if (stop < _sl.Price) target = stop;
        }

        // no más de una modificación por segundo (los exchanges limitan pedidos)
        DateTime observedNow = _account is IObservedMarketPrice ? Time : DateTime.UtcNow;
        if (target is { } t && (observedNow - _lastTrailUpdate).TotalSeconds >= 1)
        {
            _lastTrailUpdate = observedNow;
            _sl.Price = t;
            OrderActions.Reprice(_account, o, t);
        }
    }

    /// <summary>Manda a la cuenta una orden con su cantidad real.</summary>
    private StrategyOrder Place(StrategyOrder so, decimal liveQuantity)
    {
        so.LiveQuantity = liveQuantity;
        var dir = so.Side == OrderSide.Buy ? OrderDirections.Buy : OrderDirections.Sell;
        string comment = $"{StrategyName}: {so.Name}";
        Order live;
        _placing++; // una simulada llena (y avisa) antes de devolver la orden: se contabiliza después
        try
        {
            var type = so.Type == StrategyOrderType.Market ? OrderTypes.Market
                : so.Type == StrategyOrderType.Stop ? OrderTypes.Stop : OrderTypes.Limit;
            bool reducing = so.IsProtective || !RealOrderPermission.IncreasesExposure(LocalPosition,
                dir == OrderDirections.Buy ? liveQuantity : -liveQuantity);
            live = OrderActions.Place(_account, _owner, type, dir, liveQuantity, ExecInstrument.RoundPrice(so.Price),
                comment, reducing, so.Quantity, so.IsProtective,
                Math.Max(Math.Abs(LocalPosition), Math.Abs(LocalPosition + (dir == OrderDirections.Buy ? liveQuantity : -liveQuantity))));

            so.Live = live;
            if (live.LogicalOrderId != null)
                _executionOrders.TryAdd(live.Id, new OwnedExecutionOrder(so, live.LogicalOrderId));
            if (live.State == OrderStates.Failed)
            {
                so.State = StrategyOrderState.Canceled;
                Log($"Rejected: {Describe(so)} ({_account.LastError})");
                return so;
            }
            Interlocked.Exchange(ref _syncAgain, 1);

            _orders.Add(so);
            Log($"Submitted: {Describe(so)}");
            Changed?.Invoke();
            return so;
        }
        finally
        {
            _placing--;
        }

    }

    /// <summary>
    /// Real en la cuenta para una posición de la estrategia (con el tamaño, redondeado hacia abajo al paso del
    /// instrumento). Si la estrategia quiere estar en el mercado nunca queda en cero por el redondeo: va al menos una
    /// unidad (por ejemplo, 1 contrato × 0,5 en un futuro es 1 contrato; antes daba 0 y no se operaba).
    /// </summary>
    private decimal Target(decimal virtualPosition)
    {
        if (virtualPosition == 0 || SizeMultiplier <= 0) return 0;
        decimal step = ExecInstrument.VolumeStep > 0 ? ExecInstrument.VolumeStep : 1;
        decimal units = Math.Floor(Math.Abs(virtualPosition) * SizeMultiplier * ExecRatio / step + 0.0000001m);
        return Math.Sign(virtualPosition) * Math.Max(1, units) * step;
    }

    /// <summary>El tamaño no se puede aplicar tal cual (queda por debajo de una unidad y se manda el mínimo).</summary>
    public bool SizeBelowMinimum(decimal strategyQuantity)
    {
        decimal step = ExecInstrument.VolumeStep > 0 ? ExecInstrument.VolumeStep : 1;
        return SizeMultiplier > 0 && strategyQuantity > 0 && strategyQuantity * SizeMultiplier * ExecRatio < step;
    }

    private decimal ToVirtual(decimal own) => SizeMultiplier > 0 && ExecRatio > 0 ? Math.Round(own / (SizeMultiplier * ExecRatio), 8) : own;

    /// <summary>¿Una orden de este lado abre o agranda la posición de la estrategia?</summary>
    private bool Opens(OrderSide side) => _virtual == 0 || Math.Sign(_virtual) == (side == OrderSide.Buy ? 1 : -1);

    private string Describe(StrategyOrder so)
    {
        string type = so.Type switch { StrategyOrderType.Market => "at market", StrategyOrderType.Limit => "limit", _ => "stop" };
        string price = so.Type == StrategyOrderType.Market ? "" : " @ " + _data.Instrument.FormatPrice(so.Price);
        string scale = !so.IsProtective && (so.LiveQuantity != so.Quantity || CrossExecution)
            ? $" (strategy {Qty(so.Quantity)} {_data.Instrument.Symbol} × size {Qty(SizeMultiplier)}{(CrossExecution ? $" = {Qty(so.LiveQuantity)} {ExecInstrument.Symbol}, micro" : "")})"
            : "";
        return $"{so.Name}: {(so.Side == OrderSide.Buy ? "buy" : "sell")} {type} {Qty(so.LiveQuantity)} {ExecInstrument.Symbol}{price}{scale}";
    }

    /// <summary>El aviso de una orden ejecutada: «Cruce de medias compró 5 MNQ a 20.150,25 (micro: equivale a 0,5 NQ)».</summary>
    private string ExecutedText(StrategyOrder so, decimal filled)
    {
        decimal qty = filled > 0 ? filled : so.LiveQuantity;
        string what = so.IsProtective ? $" ({so.Name.ToLowerInvariant()})" : "";
        string micro = CrossExecution && ExecRatio > 0 ? $" · micro: equivalent to {Qty(qty / ExecRatio)} {_data.Instrument.Symbol}" : "";
        string price = so.FillPrice > 0 ? $"at {ExecInstrument.FormatPrice(so.FillPrice)}" : "at an execution price still pending";
        return $"«{StrategyName}» {(so.Side == OrderSide.Buy ? "bought" : "sold")} {Qty(qty)} {ExecInstrument.Symbol} {price}{what} on {TradingHub.Common.EnglishText.Of(_account.Name)}{micro}";
    }

    private static string Qty(decimal q) => q.ToString("0.########", Inv);

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Inv);

    // ----------------------------------------------------------------- límites de riesgo

    private void RefreshTotals()
    {
        var changes = Ledger.RealizedChanges;
        if (Ledger.Revision == _tradesSeen && _totalsDay == _sessionDay) return;
        _tradesSeen = Ledger.Revision;
        _totalsDay = _sessionDay;
        decimal eq = 0, peak = 0, day = 0;
        foreach (var change in changes)
        {
            eq += change.Net;
            peak = Math.Max(peak, eq);
            if (_sessionDay != default && _session.SessionDate(DateTime.SpecifyKind(change.Time, DateTimeKind.Utc)) == _sessionDay) day += change.Net;
        }

        _peakClosed = peak;
        _dayPnl = day;
        _dayFinancialsPending = Ledger.HasPendingFinancialsForSession(_session, _sessionDay);
    }

    private void CheckLimits()
    {
        if (!IsRunning || _warmup || (MaxDailyLoss <= 0 && MaxDrawdown <= 0)) return;
        if (MaxDrawdown > 0 && AvailableCurrentDrawdown is { } dd && dd >= MaxDrawdown)
        {
            StopReason = $"Maximum drawdown reached ({Money(-dd)}; limit {Money(-MaxDrawdown)})";
            string msg = $"{StrategyName}: {StopReason}. Stopped{(CloseOnLimit ? " and closes its position" : "")}.";
            Log(StopReason + (CloseOnLimit ? ": stops and closes the position" : ": stops (position remains open)"));
            Stop(CloseOnLimit);
            LimitHit?.Invoke(this, msg);
            return;
        }

        if (MaxDailyLoss > 0 && !DailyLimitActive && AvailableTodayPnL is { } today)
        {
            if (today > -MaxDailyLoss) return;
            _limitDay = _sessionDay;
            StopReason = $"Maximum daily loss reached ({Money(today)}; limit {Money(-MaxDailyLoss)})";
            Log(StopReason + ": no new entries until the next session" + (CloseOnLimit ? " and closes the position" : ""));
            foreach (var o in _orders.Where(o => !o.IsProtective).ToList()) Cancel(o);
            if (CloseOnLimit)
            {
                foreach (var o in _orders.ToList()) Cancel(o);
                _sl = _tp = null;
                SendClose("Daily limit");
            }

            LimitHit?.Invoke(this, $"{StrategyName}: {StopReason}. No new entries until the next session{(CloseOnLimit ? " and closed its position" : "")}.");
            Changed?.Invoke();
        }
    }

    // ----------------------------------------------------------------- IStrategyHost

    public Instrument Instrument => _data.Instrument;
    public ChartPeriod Period => _period;
    public BarSeries Bars => _bars;
    public Bar? FormingBar => _builder?.Current;
    public DateTime Time => new(_timeTicks, DateTimeKind.Utc);
    public decimal LastPrice => _data.Instrument.FromTicks(_priceTicks);
    public TimeZoneInfo TimeZone => _tz;
    public SessionSettings Session => _session;

    /// <summary>La posición que ve la estrategia: en sus unidades, sin el tamaño de la cartera.</summary>
    public decimal Position => _warmup ? 0 : _virtual;

    public decimal AveragePrice => _warmup || Ledger == null ? 0 : Ledger.AveragePrice;

    /// <summary>Resultado abierto en las unidades de la estrategia.</summary>
    public decimal OpenPnL => _warmup || Ledger == null || Ledger.Position == 0 ? 0 : Ledger.OpenPnL(MarkPriceKnown ? MarkPrice : LastPrice, MarkPriceKnown) * _virtual / Ledger.Position;

    /// <summary>Best-available numeric result for existing strategy code; inspect FinancialQuality before using it
    /// as confirmed economics. Missing execution facts never become fabricated market-price fills.</summary>
    public decimal RealizedPnL => _warmup || Ledger == null ? 0 : Ledger.Realized;
    public LiveFinancialQuality FinancialQuality => Ledger?.FinancialQuality ?? LiveFinancialQuality.Pending;
    public IReadOnlyList<StrategyOrder> ActiveOrders => _orders.ToList();
    public bool IsBacktest => false;
    public bool AlertsEnabled => IsRunning && !_warmup;

    /// <summary>
    /// Orden de la estrategia (cantidad en sus unidades). Se manda la diferencia entre lo que corresponde en la cuenta con
    /// el tamaño actual y lo que tiene; en pausa o con el límite del día tocado solo se aceptan las que achican la posición.
    /// </summary>
    public StrategyOrder Submit(OrderSide side, StrategyOrderType type, decimal quantity, decimal price, string name)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be greater than zero.");
        var so = new StrategyOrder { Id = _nextId++, Side = side, Type = type, Quantity = quantity, Price = price, Name = name, Time = Time };
        if (_warmup)
        {
            so.State = StrategyOrderState.Canceled;
            return so;
        }

        if (_awaitingAccount || _closing)
        {
            if (!_awaitLogged) Log($"Waiting for {TradingHub.Common.EnglishText.Of(_account.Name)} to confirm the inherited position: did not submit {name}");
            _awaitLogged = true;
            so.State = StrategyOrderState.Canceled;
            return so;
        }

        decimal sign = side == OrderSide.Buy ? 1 : -1;
        decimal q = quantity;
        if (Paused || DailyLimitActive)
        {
            // solo se achica la posición (una vuelta se recorta a cerrar)
            if (Opens(side))
            {
                Log($"{(Paused ? "Paused" : "Daily limit reached")}: did not submit {name}");
                so.State = StrategyOrderState.Canceled;
                return so;
            }

            q = Math.Min(q, Math.Abs(_virtual));
        }

        decimal own = LocalPosition;
        decimal delta = Target(_virtual + sign * q) - own;
        if (delta == 0 || Math.Sign(delta) != Math.Sign(sign))
        {
            Log(SizeMultiplier <= 0 ? $"Size is zero: did not submit {name}" : $"With size ×{Qty(SizeMultiplier)} the order {name} rounds to zero");
            so.State = StrategyOrderState.Canceled;
            return so;
        }

        decimal after = own + delta;
        bool reduces = Math.Abs(after) < Math.Abs(own) && (after == 0 || Math.Sign(after) == Math.Sign(own));
        if (!reduces && FinancialRiskBlocked)
        {
            Log($"Execution facts required for configured risk are pending. Did not submit {name}; reductions and cancels remain allowed.");
            so.State = StrategyOrderState.Canceled;
            return so;
        }
        if (!reduces && Guard?.Check(this, side, Math.Abs(delta), after) is { } why)
        {
            Log($"Risk guard: {why}. Did not submit {name}");
            so.State = StrategyOrderState.Canceled;
            GuardBlocked?.Invoke(this, why);
            return so;
        }

        so.Quantity = q;
        Place(so, Math.Abs(delta));
        // Public callers have no following market-data boundary. Reconcile only after registration;
        // strategy callbacks defer to their existing outer boundary, and SyncOrders remains single-flight.
        SyncOrders();
        return so;
    }

    public void Cancel(StrategyOrder order)
    {
        if (!order.IsActive) return;
        if (order.Live is Order o)
        {
            // A pending cancel must not be re-adopted, but the still-active broker order remains a duplicate-exit blocker.
            lock (CanceledOrders) CanceledOrders.Add(o.Id);
            if (_cancelPending.Add(o)) _account.Cancel(o);
        }

        if (order.Live is not Order)
        {
            order.State = StrategyOrderState.Canceled;
            _orders.Remove(order);
        }
    }

    public void Modify(StrategyOrder order, decimal price)
    {
        if (!order.IsActive || order.Live is not Order o || price <= 0) return;
        order.Price = price;
        OrderActions.Reprice(_account, o, price);
    }

    public void Log(string message)
    {
        if (_warmup) return;
        _log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        Changed?.Invoke();
    }
}
