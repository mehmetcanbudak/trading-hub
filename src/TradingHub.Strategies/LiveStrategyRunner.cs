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
    private readonly Strategy _strategy;
    private readonly InstrumentData _data;
    private readonly ITradingAccount _account;
    private readonly ChartPeriod _period;
    private readonly SessionSettings _session;
    private readonly TimeZoneInfo _tz;
    private readonly BarSeries _bars = new();
    private readonly List<StrategyOrder> _orders = new();
    private readonly List<string> _log = new();
    private readonly SynchronizationContext? _sync;
    private BarBuilder _builder = null!;
    private bool _warmup;
    private long _nextId = 1, _lastId = long.MinValue;
    private long _timeTicks, _priceTicks;
    private decimal _lastPos;
    private StrategyOrder? _sl, _tp;
    private decimal _highWater, _lowWater;
    private DateTime _lastTrailUpdate;
    private DateOnly _sessionDay;

    // posición de la estrategia en sus unidades (sin el tamaño de la cartera)
    private decimal _virtual;

    // detenida con «cerrar posición»: sigue escuchando la cuenta hasta que se ejecute el cierre
    private bool _closing;
    private bool _accountHooked;
    private int _placing;

    // arrancó con una posición en su contabilidad y espera que la cuenta la confirme
    private bool _awaitingAccount;
    private DateTime _awaitUntil;
    private bool _awaitLogged;

    // límites de riesgo
    private int _tradesSeen = -1;
    private DateOnly _totalsDay;
    private decimal _dayPnl, _peakClosed;
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
    public string Key => $"{_strategy.GetType().FullName}|{_data.Instrument.FeedId}:{_data.Instrument.Symbol}|{_account.Name}";

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

    /// <summary>Tiene órdenes vivas en la cuenta (por ejemplo una entrada límite esperando).</summary>
    public bool HasWorkingOrders => _orders.Any(o => o.IsActive);

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

    /// <summary>Al arrancar con una posición heredada que la cuenta confirma, le pone de nuevo el stop y el objetivo.</summary>
    public bool ProtectOnStart { get; set; }

    /// <summary>Pausada: no abre ni agranda posiciones; si tiene una abierta, la sigue gestionando hasta cerrarla.</summary>
    public bool Paused { get; private set; }

    /// <summary>Tocó la pérdida máxima del día: no abre más hasta la próxima sesión.</summary>
    public bool DailyLimitActive => _limitDay != default && _limitDay == _sessionDay;

    /// <summary>Se detuvo y está esperando que se ejecute el cierre de su posición.</summary>
    public bool IsClosing => _closing;

    /// <summary>Contabilidad propia de esta estrategia (llenados, posición, resultado y operaciones).</summary>
    public LiveLedger Ledger { get; private set; } = null!;

    /// <summary>Posición real de la estrategia (en la cuenta, con el tamaño aplicado).</summary>
    public decimal OwnPosition => Ledger?.Position ?? 0;

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
        Position = Ledger?.Position ?? 0,
        OpenPnL = Ledger?.OpenPnL(_data.LastPrice) ?? 0,
        Realized = Ledger?.Realized ?? 0,
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
        Ledger ??= Persist ? LiveStore.LoadLedger(Key, _data.Instrument.PointValue) : new LiveLedger(Key, _data.Instrument.PointValue);
        StopReason = null;
        _strategy.InternalInit(this);
        _builder = new BarBuilder(_data.Instrument, _period, _session, _strategy.UseFootprint);

        // calentamiento con la historia ya cargada: arma velas e indicadores sin operar
        _warmup = true;
        for (int i = 0; i < _data.Trades.Count; i++) Process(i);
        _warmup = false;
        WarmupBars = _bars.Count;
        // lo que dibujó mientras calentaba no vale: sin operar se cree siempre plana y marca rupturas que la corrida real
        // no tomó (en el gráfico, hasta acá se ven los dibujos del backtest de la historia)
        _strategy.Canvas.Clear();
        LiveFrom = _data.Trades.Count > 0 ? _data.Trades.LastTime : DateTime.UtcNow;

        _virtual = ToVirtual(Ledger.Position);
        _lastPos = Ledger.Position;
        _closing = false;
        _data.TradesAppended += OnAppended;
        HookAccount(true);
        IsRunning = true;
        StartedUtc = DateTime.UtcNow;
        Log($"Started on {TradingHub.Common.EnglishText.Of(_account.Name)} with {WarmupBars} warmup bars{(SizeMultiplier != 1 ? $" · size ×{Qty(SizeMultiplier)}" : "")}" +
            (CrossExecution ? $" · executes on {ExecInstrument.Symbol} (1 {_data.Instrument.Symbol} = {Qty(ExecRatio)} {ExecInstrument.Symbol})" : ""));
        if (Ledger.Position != 0) CheckInheritedPosition(initial: true);
        Changed?.Invoke();
        Started?.Invoke(this);
    }

    /// <summary>Detiene la estrategia; opcionalmente cierra su posición (solo la suya) y cancela sus órdenes.</summary>
    public void Stop(bool flatten)
    {
        if (!IsRunning) return;
        _data.TradesAppended -= OnAppended;
        foreach (var o in _orders.ToList()) Cancel(o);
        _sl = _tp = null;
        try
        {
            _strategy.InternalStop();
        }
        catch (Exception ex)
        {
            Log("Error in OnStop: " + ex.Message);
        }

        IsRunning = false;
        _awaitingAccount = false;
        if (flatten && Ledger.Position != 0)
        {
            // sigue escuchando la cuenta hasta que se ejecute el cierre (así su contabilidad queda plana)
            _closing = true;
            SendClose("Close on stop");
            SyncOrders(); // una simulada llena al instante
            if (_orders.Count == 0) _closing = false;
        }

        if (!_closing) HookAccount(false);
        Log(flatten ? "Stopped and position closed" : "Stopped");
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Stop(flatten: false);
        HookAccount(false);
    }

    /// <summary>Pausa o reanuda. En pausa cancela las entradas pendientes y no abre nada nuevo.</summary>
    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        Paused = paused;
        if (paused)
        {
            foreach (var o in _orders.Where(o => !o.IsProtective && Opens(o.Side)).ToList()) Cancel(o);
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
        Ledger ??= Persist ? LiveStore.LoadLedger(Key, _data.Instrument.PointValue) : new LiveLedger(Key, _data.Instrument.PointValue);
        Ledger.Apply(fill);
        if (Persist) LiveStore.AppendFill(Key, fill);
    }

    /// <summary>
    /// Empieza su contabilidad de cero (borra sus operaciones guardadas). Solo sin posición abierta. Devuelve false si
    /// tiene posición.
    /// </summary>
    public bool ResetLedger()
    {
        if (Ledger is { Position: not 0 }) return false;
        if (Persist) LiveStore.DeleteFills(Key);
        Ledger = new LiveLedger(Key, _data.Instrument.PointValue);
        _virtual = 0;
        _lastPos = 0;
        _tradesSeen = -1;
        Log("History deleted: starting fresh");
        Changed?.Invoke();
        return true;
    }

    /// <summary>Cierra la posición de la estrategia a mercado y cancela sus órdenes (sigue corriendo).</summary>
    public void ClosePosition(string why = "Manual close")
    {
        foreach (var o in _orders.ToList()) Cancel(o);
        _sl = _tp = null;
        if (Ledger == null || Ledger.Position == 0) return;
        if (!IsRunning) _closing = true;
        HookAccount(true);
        SendClose(why);
        SyncOrders(); // una simulada llena al instante
        if (!IsRunning && _orders.Count == 0)
        {
            _closing = false;
            HookAccount(false);
        }

        Changed?.Invoke();
    }

    /// <summary>Resultado del día de sesión (cerrado + abierto).</summary>
    public decimal TodayPnL
    {
        get
        {
            if (Ledger == null) return 0;
            RefreshTotals();
            return _dayPnl + Ledger.OpenPnL(_data.LastPrice);
        }
    }

    /// <summary>Drawdown actual desde el máximo de su curva (cerrado), contando lo abierto.</summary>
    public decimal CurrentDrawdown
    {
        get
        {
            if (Ledger == null) return 0;
            RefreshTotals();
            return Math.Max(0, _peakClosed - (Ledger.Realized + Ledger.OpenPnL(_data.LastPrice)));
        }
    }

    // ----------------------------------------------------------------- datos

    private void OnAppended(int start, int count)
    {
        try
        {
            for (int i = start; i < start + count && IsRunning; i++) Process(i);
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
        long id = store.GetId(i);
        if (id <= _lastId) return;
        _lastId = id;
        long t = store.GetTimeTicks(i), p = store.GetPriceTicks(i);
        _timeTicks = t;
        var closed = _builder.Roll(t, p);
        if (closed != null)
        {
            _bars.Add(closed);
            _priceTicks = _data.Instrument.ToTicks(closed.Close);
            _strategy.InternalBar(closed);
        }

        var day = _session.SessionDate(new DateTime(t, DateTimeKind.Utc));
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
        _builder.Append(t, p, store.GetVolume(i), store.GetSide(i));
        if (_warmup) return;
        Trail(store.GetPrice(i));
        if (_strategy.WantsTicks) _strategy.InternalTick(store.Get(i));
    }

    private void HookAccount(bool on)
    {
        if (on == _accountHooked) return;
        _accountHooked = on;
        if (on) _account.Changed += OnAccountChanged;
        else _account.Changed -= OnAccountChanged;
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
    /// plana, o la cerraste a mano) se cierra en la contabilidad al último precio. Con un broker externo espera unos
    /// segundos a que informe la posición antes de decidir, y mientras tanto no manda órdenes.
    /// </summary>
    private void CheckInheritedPosition(bool initial)
    {
        decimal own = Ledger.Position;
        if (own == 0)
        {
            _awaitingAccount = false;
            return;
        }

        decimal acc = _account.Position?.Volume ?? 0;
        decimal covered = Math.Sign(acc) == Math.Sign(own) ? Math.Min(Math.Abs(acc), Math.Abs(own)) : 0;
        if (covered == Math.Abs(own))
        {
            _awaitingAccount = false;
            Log($"Continuing with its open position: {(own > 0 ? "long" : "short")} {Qty(Math.Abs(own))} @ {_data.Instrument.FormatPrice(Ledger.AveragePrice)}");
            if (ProtectOnStart) ProtectInherited();
            return;
        }

        if (initial && _account is not SimBroker)
        {
            // el broker puede tardar en informar las posiciones después de conectarse
            _awaitingAccount = true;
            _awaitUntil = DateTime.UtcNow.AddSeconds(45);
            _awaitLogged = false;
            Log($"Had a position ({(own > 0 ? "long" : "short")} {Qty(Math.Abs(own))}): waiting for {TradingHub.Common.EnglishText.Of(_account.Name)} to confirm it before trading");
            return;
        }

        if (_awaitingAccount && DateTime.UtcNow < _awaitUntil) return;
        _awaitingAccount = false;
        decimal excess = Math.Abs(own) - covered;
        BookSynthetic(own > 0 ? -excess : excess, $"The account does not have {(covered > 0 ? "all of " : "")}the strategy's previous position ({(own > 0 ? "long" : "short")} {Qty(Math.Abs(own))}): " +
                                                   $"closed {Qty(excess)} in its ledger at the latest price");
        _lastPos = Ledger.Position;
        if (ProtectOnStart && Ledger.Position != 0) ProtectInherited();
    }

    /// <summary>
    /// Le vuelve a poner stop y objetivo a la posición heredada, salvo que en la cuenta ya haya órdenes de esta
    /// estrategia (por ejemplo si Trading Hub se cerró de golpe y quedaron vivas en el broker): nunca se duplican.
    /// </summary>
    private void ProtectInherited()
    {
        string prefix = StrategyName + ": ";
        bool already;
        lock (CanceledOrders)
            already = _account.ActiveOrders.Any(o => o.Comment.StartsWith(prefix, StringComparison.Ordinal) && o.Type is OrderTypes.Stop or OrderTypes.Limit && !CanceledOrders.Contains(o.Id));
        if (already)
        {
            Log("This strategy already has orders in the account: no additional stop or target will be placed (review them in Chart Trader)");
            return;
        }

        UpdateProtection();
    }

    // ----------------------------------------------------------------- órdenes

    private void SyncOrders()
    {
        if ((!IsRunning && !_closing) || _placing > 0) return;
        bool changed = false;
        foreach (var so in _orders.ToList())
        {
            if (so.Live is not Order o) continue;
            bool done = o.Status == OrderStatus.Filled || (o.State == OrderStates.Done && o.Unfilled == 0 && !o.Canceled);
            decimal filled = o.Filled;
            if (done && filled <= 0) filled = so.LiveQuantity;
            filled = Math.Min(filled, so.LiveQuantity);
            if (filled > so.Booked)
            {
                Book(so, filled - so.Booked);
                so.Booked = filled;
                changed = true;
            }

            if (done)
            {
                so.State = StrategyOrderState.Filled;
                so.FillPrice = LastPrice;
                so.FillTime = Time;
                _orders.Remove(so);
                Fills++;
                changed = true;
                Log($"Filled: {Describe(so)}");
                Executed?.Invoke(this, ExecutedText(so, filled));
                if (!so.IsProtective && IsRunning) _strategy.InternalFilled(so);
            }
            else if (o.Canceled || o.State == OrderStates.Failed)
            {
                if (o.State == OrderStates.Failed) Log($"Rejected: {Describe(so)} ({_account.LastError})");
                so.State = StrategyOrderState.Canceled;
                _orders.Remove(so);
                changed = true;
            }
        }

        decimal pos = Ledger.Position;
        if (pos != _lastPos)
        {
            _lastPos = pos;
            if (IsRunning) UpdateProtection();
            changed = true;
        }

        if (_closing && _orders.Count == 0)
        {
            _closing = false;
            HookAccount(false);
        }

        if (_awaitingAccount) CheckInheritedPosition(initial: false);
        if (changed)
        {
            CheckLimits();
            Changed?.Invoke();
        }
    }

    /// <summary>Contabiliza lo ejecutado de una orden y actualiza la posición en unidades de la estrategia.</summary>
    private void Book(StrategyOrder so, decimal quantity)
    {
        decimal before = Ledger.Position;
        var fill = new LiveFill(DateTime.UtcNow, so.Side == OrderSide.Buy, quantity, LastPrice > 0 ? LastPrice : _data.LastPrice, ExecInstrument.Symbol, ExecInstrument.PointValue);
        Ledger.Apply(fill);
        if (Persist) LiveStore.AppendFill(Key, fill);
        decimal after = Ledger.Position;
        if (after == 0) _virtual = 0;
        else if (so.IsProtective) _virtual = before != 0 && Math.Sign(before) == Math.Sign(after) ? _virtual * after / before : ToVirtual(after);
        else
        {
            decimal sign = so.Side == OrderSide.Buy ? 1 : -1;
            if (so.LiveQuantity > 0) _virtual += sign * so.Quantity * quantity / so.LiveQuantity;
            if (Math.Sign(_virtual) != Math.Sign(after)) _virtual = ToVirtual(after); // se desincronizó: se rehace
        }
    }

    /// <summary>Asiento sin orden (lo que la cuenta no tiene): se cierra al último precio.</summary>
    private void BookSynthetic(decimal signedQuantity, string why)
    {
        if (signedQuantity == 0) return;
        decimal before = Ledger.Position;
        decimal price = _data.LastPrice > 0 ? _data.LastPrice : Ledger.AveragePrice;
        var fill = new LiveFill(DateTime.UtcNow, signedQuantity > 0, Math.Abs(signedQuantity), price, Ledger.OpenSymbol ?? ExecInstrument.Symbol, Ledger.OpenPointValue > 0 ? Ledger.OpenPointValue : ExecInstrument.PointValue);
        Ledger.Apply(fill);
        if (Persist) LiveStore.AppendFill(Key, fill);
        decimal after = Ledger.Position;
        _virtual = after == 0 ? 0 : before != 0 && Math.Sign(before) == Math.Sign(after) ? _virtual * after / before : ToVirtual(after);
        Log(why);
    }

    /// <summary>Manda el cierre de la posición propia (nunca más de lo que la cuenta tiene en ese sentido).</summary>
    private void SendClose(string why)
    {
        decimal own = Ledger.Position;
        if (own == 0) return;
        decimal acc = _account.Position?.Volume ?? 0;
        decimal closable = Math.Sign(acc) == Math.Sign(own) ? Math.Min(Math.Abs(acc), Math.Abs(own)) : 0;
        decimal excess = Math.Abs(own) - closable;
        if (excess > 0)
            BookSynthetic(own > 0 ? -excess : excess, $"The account did not have {Qty(excess)} of the strategy's position: it was closed in its ledger at the latest price");
        if (closable <= 0) return;
        var so = new StrategyOrder { Id = _nextId++, Side = own > 0 ? OrderSide.Sell : OrderSide.Buy, Type = StrategyOrderType.Market, Quantity = closable, Name = why, Time = Time, IsProtective = true };
        Place(so, closable);
    }

    private void UpdateProtection()
    {
        if (_sl is { IsActive: true }) Cancel(_sl);
        if (_tp is { IsActive: true }) Cancel(_tp);
        _sl = _tp = null;
        decimal pos = Ledger.Position;
        if (pos == 0) return;
        decimal avg = Ledger.AveragePrice;
        decimal qty = Math.Abs(pos);
        var exit = pos > 0 ? OrderSide.Sell : OrderSide.Buy;
        var inst = _data.Instrument;
        _highWater = _lowWater = LastPrice;
        int stopTicks = _strategy.StopLossTicks > 0 ? _strategy.StopLossTicks : _strategy.TrailingStopTicks;
        if (stopTicks > 0)
        {
            decimal d = stopTicks * inst.TickSize;
            _sl = Protective(exit, StrategyOrderType.Stop, qty, inst.RoundPrice(pos > 0 ? avg - d : avg + d), "Stop loss");
        }

        if (_strategy.TakeProfitTicks > 0)
        {
            decimal d = _strategy.TakeProfitTicks * inst.TickSize;
            _tp = Protective(exit, StrategyOrderType.Limit, qty, inst.RoundPrice(pos > 0 ? avg + d : avg - d), "Take profit");
        }
    }

    private StrategyOrder Protective(OrderSide side, StrategyOrderType type, decimal qty, decimal price, string name)
    {
        var so = new StrategyOrder { Id = _nextId++, Side = side, Type = type, Quantity = qty, Price = price, Name = name, Time = Time, IsProtective = true };
        return Place(so, qty);
    }

    private void Trail(decimal price)
    {
        if (_strategy.TrailingStopTicks <= 0 || _sl is not { IsActive: true } || _sl.Live is not Order o) return;
        decimal pos = Ledger.Position;
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
        if (target is { } t && (DateTime.UtcNow - _lastTrailUpdate).TotalSeconds >= 1)
        {
            _lastTrailUpdate = DateTime.UtcNow;
            _sl.Price = t;
            _account.Modify(o, t);
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
            live = so.Type switch
            {
                StrategyOrderType.Market => _account.Market(dir, liveQuantity, comment),
                StrategyOrderType.Limit => _account.Limit(dir, liveQuantity, ExecInstrument.RoundPrice(so.Price), comment),
                _ => _account.Stop(dir, liveQuantity, ExecInstrument.RoundPrice(so.Price), comment),
            };
        }
        finally
        {
            _placing--;
        }

        so.Live = live;
        if (live.State == OrderStates.Failed)
        {
            so.State = StrategyOrderState.Canceled;
            Log($"Rejected: {Describe(so)} ({_account.LastError})");
            return so;
        }

        _orders.Add(so);
        Log($"Submitted: {Describe(so)}");
        Changed?.Invoke();
        return so;
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
        decimal price = _account.LastPrice > 0 ? _account.LastPrice : LastPrice > 0 ? LastPrice : _data.LastPrice;
        return $"«{StrategyName}» {(so.Side == OrderSide.Buy ? "bought" : "sold")} {Qty(qty)} {ExecInstrument.Symbol} at {ExecInstrument.FormatPrice(price)}{what} on {TradingHub.Common.EnglishText.Of(_account.Name)}{micro}";
    }

    private static string Qty(decimal q) => q.ToString("0.########", Inv);

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Inv);

    // ----------------------------------------------------------------- límites de riesgo

    private void RefreshTotals()
    {
        var trades = Ledger.Trades;
        if (trades.Count == _tradesSeen && _totalsDay == _sessionDay) return;
        _tradesSeen = trades.Count;
        _totalsDay = _sessionDay;
        decimal eq = 0, peak = 0, day = 0;
        foreach (var t in trades)
        {
            eq += t.NetPnl;
            peak = Math.Max(peak, eq);
            if (_sessionDay != default && _session.SessionDate(DateTime.SpecifyKind(t.ExitTime, DateTimeKind.Utc)) == _sessionDay) day += t.NetPnl;
        }

        _peakClosed = peak;
        _dayPnl = day;
    }

    private void CheckLimits()
    {
        if (!IsRunning || _warmup || (MaxDailyLoss <= 0 && MaxDrawdown <= 0)) return;
        if (MaxDrawdown > 0 && CurrentDrawdown >= MaxDrawdown)
        {
            decimal dd = CurrentDrawdown;
            StopReason = $"Maximum drawdown reached ({Money(-dd)}; limit {Money(-MaxDrawdown)})";
            string msg = $"{StrategyName}: {StopReason}. Stopped{(CloseOnLimit ? " and closes its position" : "")}.";
            Log(StopReason + (CloseOnLimit ? ": stops and closes the position" : ": stops (position remains open)"));
            Stop(CloseOnLimit);
            LimitHit?.Invoke(this, msg);
            return;
        }

        if (MaxDailyLoss > 0 && !DailyLimitActive)
        {
            decimal today = TodayPnL;
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
    public decimal OpenPnL => _warmup || Ledger == null || Ledger.Position == 0 ? 0 : Ledger.OpenPnL(LastPrice) * _virtual / Ledger.Position;

    public decimal RealizedPnL => _warmup || Ledger == null ? 0 : Ledger.Realized;
    public IReadOnlyList<StrategyOrder> ActiveOrders => _orders.ToList();
    public bool IsBacktest => false;

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

        if (_awaitingAccount)
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

        decimal own = Ledger.Position;
        decimal delta = Target(_virtual + sign * q) - own;
        if (delta == 0 || Math.Sign(delta) != Math.Sign(sign))
        {
            Log(SizeMultiplier <= 0 ? $"Size is zero: did not submit {name}" : $"With size ×{Qty(SizeMultiplier)} the order {name} rounds to zero");
            so.State = StrategyOrderState.Canceled;
            return so;
        }

        decimal after = own + delta;
        bool reduces = Math.Abs(after) < Math.Abs(own) && (after == 0 || Math.Sign(after) == Math.Sign(own));
        if (!reduces && Guard?.Check(this, side, Math.Abs(delta), after) is { } why)
        {
            Log($"Risk guard: {why}. Did not submit {name}");
            so.State = StrategyOrderState.Canceled;
            GuardBlocked?.Invoke(this, why);
            return so;
        }

        so.Quantity = q;
        return Place(so, Math.Abs(delta));
    }

    public void Cancel(StrategyOrder order)
    {
        if (!order.IsActive) return;
        if (order.Live is Order o)
        {
            // las que cancela una estrategia (por ejemplo al reiniciarla) no cuentan como «ya protegida» aunque el broker tarde
            lock (CanceledOrders) CanceledOrders.Add(o.Id);
            _account.Cancel(o);
        }

        order.State = StrategyOrderState.Canceled;
        _orders.Remove(order);
    }

    public void Modify(StrategyOrder order, decimal price)
    {
        if (!order.IsActive || order.Live is not Order o || price <= 0) return;
        order.Price = price;
        _account.Modify(o, price);
    }

    public void Log(string message)
    {
        if (_warmup) return;
        _log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        Changed?.Invoke();
    }
}
