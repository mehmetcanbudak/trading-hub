using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>Effective application locations; opt-in configuration never changes persistent identities.</summary>
public static partial class AppPaths
{
    public static string Root => TradingHub.Common.StorageLocations.Startup.AppRoot;
    public static string MarketRoot => TradingHub.Common.StorageLocations.Startup.MarketRoot;
    public static string Ticks => Path.Combine(MarketRoot, "Ticks");
    public static string WorkspaceFile => Path.Combine(Root, "workspace.json");
    public static string Drawings => Path.Combine(Root, "Drawings");
    public static string Backtests => TradingHub.Common.StorageLocations.Startup.BacktestsRoot;
    public static string Accounts => Path.Combine(Root, "Accounts");

    public static string UserIndicators => TradingHub.Common.StorageLocations.Startup.IndicatorsRoot;
    public static string Strategies => TradingHub.Common.StorageLocations.Startup.StrategiesRoot;


    /// <summary>Reportes del laboratorio (Markdown), dentro de la carpeta de estrategias.</summary>
    public static string Reports => Path.Combine(Strategies, "Reportes");
}

/// <summary>Un indicador disponible para agregar.</summary>
public sealed record IndicatorInfo(Type Type, string Name, string Category, string Source, string Description);

/// <summary>Ventana aparte de una sección del panel derecho (Chart Trader, DOM o cinta).</summary>
public sealed class PanelWindowState
{
    public bool Floating { get; set; }
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 720;
}

/// <summary>Catálogo de indicadores: los propios de Trading Hub, DLLs extra y scripts del usuario.</summary>
public sealed class IndicatorCatalog
{
    private readonly List<IndicatorInfo> _items = new();
    private readonly HashSet<Assembly> _scanned = new();

    public IReadOnlyList<IndicatorInfo> Items => _items;
    public event Action? Changed;

    public void Scan(Assembly asm, string source)
    {
        if (!_scanned.Add(asm)) return;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        foreach (var t in types)
        {
            if (t.IsAbstract || !typeof(Indicator).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) == null) continue;
            if (t.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false }) continue;
            string name = Indicator.DefaultName(t);
            string cat = t.GetCustomAttribute<CategoryAttribute>()?.Category ?? IndicatorCategories.Other;
            string desc = "";
            try { desc = t.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()?.GetDescription() ?? t.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""; }
            catch { /* recursos de localización ausentes */ }
            _items.RemoveAll(i => i.Type.FullName == t.FullName && i.Source == source);
            _items.Add(new IndicatorInfo(t, name, cat, source, desc));
        }

        _items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Changed?.Invoke();
    }

    public void RemoveSource(string source)
    {
        _items.RemoveAll(i => i.Source == source);
        _scanned.RemoveWhere(a => a.IsCollectible);
        Changed?.Invoke();
    }

    public void ScanFolder(string folder, string source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll"))
        {
            try { Scan(Assembly.LoadFrom(dll), source); }
            catch (Exception ex) { this.LogWarn($"Could not load {Path.GetFileName(dll)}: {ex.Message}"); }
        }
    }

    public IndicatorInfo? Find(string typeName) => _items.FirstOrDefault(i => i.Type.FullName == typeName) ?? _items.FirstOrDefault(i => i.Type.Name == typeName);

    public Indicator? Create(string typeName)
    {
        var info = Find(typeName);
        return info == null ? null : (Indicator?)Activator.CreateInstance(info.Type);
    }
}

/// <summary>Preferencias generales.</summary>
public sealed partial class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.Root, "settings.json");

    public string? TimeZoneId { get; set; }
    public bool ConfirmRealOrders { get; set; } = true;

    /// <summary>Estrategias favoritas (por nombre de archivo): van arriba de todo en la lista de Backtesting.</summary>
    public List<string> FavoriteStrategies { get; set; } = new();

    /// <summary>Apariencia de la plataforma: "oscuro" (negro) o "claro" (blanco).</summary>
    public string ThemeMode { get; set; } = "oscuro";

    /// <summary>Plantilla de gráfico con la que arrancan las pestañas nuevas (null = copian el gráfico activo).</summary>
    public string? DefaultChartTemplate { get; set; }

    /// <summary>Plantilla con la que arranca cada indicador nuevo (tipo del indicador → nombre de la plantilla).</summary>
    public Dictionary<string, string> DefaultIndicatorTemplates { get; set; } = new();

    /// <summary>Saldo con el que arranca la cuenta simulada (se puede cambiar en la sección Cuenta).</summary>
    public decimal SimStartBalance { get; set; } = 10_000m;
    public bool SoundAlerts { get; set; } = true;

    public bool RecordLiveData { get; set; } = true;

    /// <summary>Modo sin dormir: la Mac no se duerme mientras haya estrategias en vivo o agentes trabajando.</summary>
    public bool KeepAwake { get; set; } = true;

    /// <summary>Seguir con la tapa cerrada (pide tu contraseña de administrador) y, si se quiere, solo con el cargador.</summary>
    public bool KeepAwakeLidClosed { get; set; }
    public bool LidClosedOnlyOnPower { get; set; } = true;
    /// <summary>Idioma de la interfaz (es, en, zh-CN). null = todavía no se eligió: se toma el del sistema al arrancar.</summary>
    public string? Language { get; set; }

    /// <summary>Si ya había ajustes guardados (alguien que ya usaba la app antes de que hubiera idiomas).</summary>
    public static bool Saved => File.Exists(FilePath);

    public double WindowWidth { get; set; } = 1600;
    public double WindowHeight { get; set; } = 960;
    public List<string> Favorites { get; set; } = new() { "binance-futures:BTCUSDT", "binance-futures:ETHUSDT", "sim:SIM-BTC" };

    /// <summary>Períodos favoritos: los que se ven en la barra del gráfico (el resto, en su selector).</summary>
    public List<string> FavoritePeriods { get; set; } = new() { "M1", "M5", "M15", "H1", "H4", "Daily" };

    // panel derecho de Trading
    public bool ShowSidePanel { get; set; } = true;
    public bool ShowTicket { get; set; } = true;
    public bool ShowDom { get; set; } = true;
    public bool ShowTape { get; set; } = true;
    public double SidePanelWidth { get; set; } = 340;

    /// <summary>Ya se mostró el Chart Trader por gráfico (la primera vez se oculta el ticket general).</summary>
    public bool ChartTraderIntroduced { get; set; }

    /// <summary>Alto relativo de cada sección del panel derecho (ticket, dom, tape) cuando están juntas.</summary>
    public Dictionary<string, double> SideWeights { get; set; } = new();

    /// <summary>Chart Trader, DOM o cinta en una ventana aparte: si estaba afuera, dónde y de qué tamaño.</summary>
    public Dictionary<string, PanelWindowState> PanelWindows { get; set; } = new();

    /// <summary>Cerrar la ventana deja a Trading Hub corriendo en la barra de menú (con su panel); ⌘Q la cierra del todo.</summary>
    public bool KeepInMenuBar { get; set; } = true;

    /// <summary>
    /// Contrato inteligente: si el tamaño de una estrategia pide fracciones de un mini (1 NQ × 0,5), ejecuta en el micro con la
    /// misma exposición (5 MNQ). Con minis enteros, en el mini.
    /// </summary>
    public bool SmartContracts { get; set; } = true;

    /// <summary>Al abrir Trading Hub, las estrategias en vivo que corrían cuando se cerró vuelven a arrancar solas.</summary>
    public bool AutoResumeLive { get; set; } = true;

    /// <summary>…también las de cuentas reales (si no, esas esperan que las reanudes vos).</summary>
    public bool AutoResumeLiveReal { get; set; }

    /// <summary>Botones de ejecución debajo del DOM (mercado, límites, cancelar, cerrar…).</summary>
    public bool ShowDomButtons { get; set; } = true;

    /// <summary>DOM en una ventana aparte (versión anterior: ahora va en <see cref="PanelWindows"/>).</summary>
    public bool DomFloating { get; set; }
    public int DomWindowX { get; set; } = -1;
    public int DomWindowY { get; set; } = -1;
    public double DomWindowWidth { get; set; } = 360;
    public double DomWindowHeight { get; set; } = 720;

    public string StrategyChatEngine { get; set; } = "claude";
    public string StrategyClaudeModel { get; set; } = "claude-opus-5-5";
    public string StrategyClaudeEffort { get; set; } = "xhigh";
    public string StrategyCodexModel { get; set; } = "";
    public string StrategyCodexEffort { get; set; } = "high";

    /// <summary>Chat de Claude en una ventana aparte: posición y tamaño.</summary>
    public int ClaudeWindowX { get; set; } = -1;
    public int ClaudeWindowY { get; set; } = -1;
    public double ClaudeWindowWidth { get; set; } = 460;
    public double ClaudeWindowHeight { get; set; } = 760;

    /// <summary>Gráficos vinculados por color: qué se sincroniza dentro de cada grupo.</summary>
    public bool LinkSymbol { get; set; } = true;
    public bool LinkPeriod { get; set; } = true;
    public bool LinkCrosshair { get; set; } = true;

    /// <summary>Estilo por defecto de cada tipo de dibujo (lo guarda "usar como predeterminado").</summary>
    public Dictionary<string, TradingHub.Charting.Drawings.ChartDrawing> DrawingDefaults { get; set; } = new();

    /// <summary>Atajos de teclado: acción → atajo ("" = sin asignar; si falta, el de fábrica).</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>Configuración del DOM (columnas, tamaños, colores).</summary>
    public TradingHub.Charting.Dom.DomSettings Dom { get; set; } = new();

    /// <summary>Stop y objetivos automáticos por instrumento (clave del instrumento).</summary>
    public Dictionary<string, BracketTemplate> Brackets { get; set; } = new();

    public BracketTemplate BracketTemplateFor(string instrumentKey)
    {
        if (!Brackets.TryGetValue(instrumentKey, out var t)) Brackets[instrumentKey] = t = new BracketTemplate();
        return t;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new TradingHub.Charting.Dom.ColorJsonConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
                s.Dom ??= new TradingHub.Charting.Dom.DomSettings();
                s.Dom.Normalize();
                s.Brackets ??= new();
                s.Hotkeys ??= new();
                s.DrawingDefaults ??= new();
                return s;
            }
        }
        catch
        {
            // archivo dañado: valores por defecto
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}

/// <summary>Cuenta disponible para operar un instrumento.</summary>
public sealed record AccountOption(string Id, string Name, bool IsReal);

/// <summary>Servicios compartidos de la aplicación.</summary>
public sealed partial class AppServices
{
    private readonly Dictionary<string, SimBroker> _simBrokers = new();
    private readonly Dictionary<string, ITradingAccount> _realBrokers = new();
    private readonly Dictionary<string, OrderContext> _orderContexts = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Context, string Instrument), WeakReference<ITradingAccount>> _wireAccounts = new();
    private OrderContext? _simOrderContext;
    private readonly string _executionRoot;

    // how each real broker AccountFor built was made, for as long as anything holds the broker (see BrokerOrigin)
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ITradingAccount, BrokerOrigin> _brokerOrigins = new();

    // replaced brokers something still works through (a live strategy, a position or working orders): disposed once idle
    private readonly List<ITradingAccount> _replacedBrokers = new();

    private readonly Dictionary<string, TradingHub.Feeds.Rithmic.RithmicTradingSession> _rithmicSessions = new();
    private readonly Dictionary<string, TradingHub.Feeds.Tradovate.TradovateTradingSession> _tradovateSessions = new();
    private readonly List<TradingHub.Feeds.Tradovate.TradovateTradingSession> _retiredTradovateSessions = new();
    private sealed record RithmicContextOrigin(string Profile, string LocalNamespace, string Generation, string Environment);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TradingHub.Feeds.Rithmic.RithmicTradingSession, RithmicContextOrigin> _rithmicOrigins = new();
    private readonly List<TradingHub.Feeds.Rithmic.RithmicTradingSession> _retiredRithmicSessions = new();

    /// <summary>Cuentas de Tradovate de una conexión (sincroniza órdenes, posiciones y saldos en segundo plano).</summary>
    public TradingHub.Feeds.Tradovate.TradovateTradingSession TradovateSessionFor(ConnectionProfile p)
    {
        if (_tradovateSessions.TryGetValue(p.Id, out var s)) return s;
        Connections.EnsureTradingIdentity(p);
        var feed = (TradingHub.Feeds.Tradovate.TradovateFeed)Connections.GetFeed(p.Id);
        s = new TradingHub.Feeds.Tradovate.TradovateTradingSession(feed.Connection);
        _tradovateSessions[p.Id] = s;
        // cada ejecución queda en el historial de la cuenta (saldo, operaciones y estadísticas)
        s.FillReceived += f => Journal.Add(TradovateAccountId(p, f.AccountId),
            new AccountFill("tv-" + f.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), f.Time, f.Symbol, f.Buy, f.Quantity, f.Price,
                AccountCenter.PointValueOf(f.Symbol), 0, f.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        s.Start();
        return s;
    }

    /// <summary>Id de una cuenta de Tradovate en Trading Hub: tradovate|conexión|cuenta.</summary>
    public static string TradovateAccountId(ConnectionProfile p, long account) => $"tradovate|{p.Id}|{account}";

    /// <summary>Futuro de CME (sirve para operarlo con una cuenta de Tradovate aunque el gráfico sea de otra conexión).</summary>
    private static bool IsCmeFuture(Instrument inst) =>
        (inst.FeedId is TradingHub.Feeds.Rithmic.RithmicFeed.FeedId or TradingHub.Feeds.Tradovate.TradovateFeed.FeedId) && TradingHub.Feeds.Rithmic.RithmicFeed.SpecFor(inst.Symbol.Split(' ', ':')[0]) != null;

    /// <summary>Sesión de órdenes de Rithmic de una conexión (se conecta en segundo plano la primera vez).</summary>
    public TradingHub.Feeds.Rithmic.RithmicTradingSession RithmicSessionFor(ConnectionProfile p)
    {
        lock (_shutdownGate)
        {
            if (_rithmicSessions.TryGetValue(p.Id, out var s)) return s;
            // Do not replace an unresolved account's transport in place. A changed connection remains reduction/cancel-only
            // through its original session until it is confirmed idle; this also avoids competing Rithmic order logins.
            foreach (var retired in _retiredRithmicSessions)
                if (_rithmicOrigins.TryGetValue(retired, out var old) && old.Profile == p.Id && RithmicInUse(retired)) return retired;
            if (IsShuttingDown) throw new InvalidOperationException(RithmicAdmissionClosed);
            Connections.EnsureTradingIdentity(p);
            s = new TradingHub.Feeds.Rithmic.RithmicTradingSession(Connections.RithmicSettingsOf(p), network: NetworkProxy.CreateFactory());
            _rithmicSessions[p.Id] = s;
            _rithmicOrigins.Add(s, new(p.Id, p.TradingNamespace, p.TradingGeneration, PublicEndpoint(p.RithmicUri) + "|" + p.RithmicSystem));
            // cada ejecución queda en el historial de la cuenta (saldo, operaciones y estadísticas)
            s.FillReceived += f => Journal.Add(RithmicAccountId(p, f.Account),
                new AccountFill(f.Id, f.Time, f.Symbol, f.IsBuy, f.Quantity, f.Price, AccountCenter.PointValueOf(f.Symbol), 0, f.BasketId));
            s.Start();
            return s;
        }
    }

    /// <summary>Estado de la sesión de órdenes de Rithmic para un instrumento (null si no es de Rithmic).</summary>
    public string? RithmicStatusFor(InstrumentData data)
    {
        var p = Connections.Enabled.FirstOrDefault(x => x.IsRithmic && x.MarketDataId == data.Instrument.FeedId && !string.IsNullOrWhiteSpace(x.RithmicUser));
        if (p == null) return null;
        var s = DiscoverRithmicSession(p);
        return s == null ? RithmicAdmissionClosed : s.IsConnected ? null : "Rithmic: " + s.StatusText;
    }

    /// <summary>Id de una cuenta de Rithmic en Trading Hub: rithmic|conexión|cuenta.</summary>
    public static string RithmicAccountId(ConnectionProfile p, string account) => $"rithmic|{p.Id}|{account}";

    public AppServices() : this(null) { }

    internal AppServices(Func<Func<TradingHub.Networking.ProxyHandlerFactory>, ConnectionManager>? connectionsFactory)
    {
        Func<TradingHub.Networking.ProxyHandlerFactory> networkFactory = () => NetworkProxy.CreateFactory();
        Connections = connectionsFactory == null ? new ConnectionManager(networkFactory) : connectionsFactory(networkFactory);
        Portfolio.BeginBalance = Portfolio.Balance = Settings.SimStartBalance;
        Markets = new MarketConditions(this);
        // cada estrategia que arranca en vivo arma su contexto de mercado con el último mes (no solo con el calentamiento)
        TradingHub.Strategies.LiveStrategyRunner.Started += OnRunnerStarted;
        Connections.FeedInUse = RetainTradovateFeed;
        // Trading contexts are immutable. Retire old sessions without moving outstanding requests to new credentials.
        Connections.Reconfigured += p =>
        {
            lock (_shutdownGate)
            {
                if (_rithmicSessions.Remove(p.Id, out var session))
                {
                    if (RithmicInUse(session)) _retiredRithmicSessions.Add(session);
                    else RetireRithmicSession(session);
                }
            }
            // A changed profile gets a new Tradovate context. Existing activity keeps its original transport.
            if (_tradovateSessions.Remove(p.Id, out var tv))
            {
                if (TradovateInUse(tv)) _retiredTradovateSessions.Add(tv);
                else DisposeTradovate(tv);
            }

            ReconfigureKvpProviders(p);
            ReplaceStaleBrokers(p);
        };
        Directory.CreateDirectory(AppPaths.Root);
        Directory.CreateDirectory(AppPaths.UserIndicators);
        Directory.CreateDirectory(AppPaths.Strategies);
        Hub.RecordingRoot = AppPaths.Ticks;
        KlineHistory.CacheRoot = Path.Combine(AppPaths.MarketRoot, "Klines");
        TradingHub.Strategies.LiveStore.Root = TradingHub.Common.StorageLocations.Startup.LiveRoot;
        _executionRoot = TradingHub.Strategies.LiveStore.Root;
        InitializeOperations();
        InitializeReplayAlerts();
        InitializeKvpProviders();
        // las que corrían al cerrar Trading Hub (enseguida el guardado periódico las marca detenidas): para reanudarlas al abrir
        RunningAtLastClose = TradingHub.Strategies.LiveStore.LoadRunners().Where(i => i.Running && !LiveEntry.IsManaged(i.Source)).ToList();
        Hub.RecordLive = Settings.RecordLiveData;
        StatsProvider.SetSource(Statistics);
        Catalog.Scan(typeof(TradingHub.Indicators.Native.BigTrades).Assembly, "Trading Hub");
        // los indicadores técnicos, si vienen en esta versión (la de código abierto no los trae)
        try
        {
            Catalog.Scan(System.Reflection.Assembly.Load("TradingHub.Indicators.Technical"), "Técnicos");
        }
        catch (Exception ex)
        {
            this.LogInfo($"No additional technical indicators: {ex.Message}");
        }
        Catalog.ScanFolder(Path.Combine(AppContext.BaseDirectory, "Indicators"), "Extra");
        Catalog.ScanFolder(Path.Combine(AppPaths.Root, "Indicators"), "Extra");
    }

    public AppSettings Settings { get; } = AppSettings.Load();
    public ConnectionManager Connections { get; }
    public MarketDataHub Hub { get; } = new();

    /// <summary>Condición de cada mercado (rango o tendencia y volatilidad), la misma medida que usan los filtros de contexto.</summary>
    internal MarketConditions Markets { get; }

    private void OnRunnerStarted(TradingHub.Strategies.LiveStrategyRunner runner)
    {
        AttachRunnerAlerts(runner);
        _ = Markets.SeedAsync(runner);
    }
    public Portfolio Portfolio { get; } = new() { AccountID = "SIM-1", Name = "Cuenta simulada", BeginBalance = 10_000m, Balance = 10_000m };

    /// <summary>Ejecuciones guardadas de cada cuenta (sobreviven al cerrar Trading Hub).</summary>
    public AccountJournal Journal { get; } = new(AppPaths.Accounts);

    private AccountCenter? _accountCenter;
    public AccountCenter AccountCenter => _accountCenter ??= new AccountCenter(this);
    public TradingStatistics Statistics { get; } = new();
    public TradingStatisticsProvider StatsProvider { get; } = new();
    public IndicatorCatalog Catalog { get; } = new();
    public UserScripts? Scripts { get; set; }

    private StrategyCatalog? _strategies;

    /// <summary>Estrategias del usuario (se crea al entrar a la sección).</summary>
    public StrategyCatalog Strategies => _strategies ??= new StrategyCatalog();

    /// <summary>Estrategias corriendo en vivo.</summary>
    public List<TradingHub.Strategies.LiveStrategyRunner> Runners { get; } = new();


    /// <summary>Modo sin dormir (aserción de energía y, si lo activás, seguir con la tapa cerrada).</summary>
    internal SleepGuard Sleep { get; } = new();

    /// <summary>Por qué la Mac tiene que seguir despierta ahora (o null si no hay nada corriendo).</summary>
    internal string? AwakeReason()
    {
        int live = Runners.Count(r => r.IsRunning);
        bool agents = _live?.RiskAgentBusy ?? false;
        bool waiting = false;
        if (live == 0 && !agents && !waiting) return null;
        var parts = new List<string>();
        if (live > 0) parts.Add($"{live} live strategy/strategies");
        if (agents) parts.Add("agents working");
        return string.Join(" and ", parts);
    }

    private LiveCenter? _live;

    /// <summary>Todas las estrategias en vivo en un solo lugar (panel y gráficos), sus portafolios y el agente de riesgo.</summary>
    internal LiveCenter Live => _live ??= new LiveCenter(this);

    /// <summary>Guarda el estado de la cartera en vivo (lo lee el laboratorio de Claude). Solo escribe si cambió algo.</summary>
    /// <summary>Estrategias en vivo (del panel y de los gráficos) que corrían cuando se cerró Trading Hub.</summary>
    internal List<TradingHub.Strategies.LiveRunnerInfo> RunningAtLastClose { get; set; } = new();

    private bool _closingDown;

    public void SaveLiveState()
    {
        // al cerrar se guarda una vez, con lo que estaba corriendo: después se detienen las estrategias y el guardado
        // periódico (que sigue unos instantes) las marcaría detenidas, y al abrir no se reanudarían solas
        if (_closingDown) return;
        var infos = Runners.Where(r => r.Persist).Select(r => r.Info()).ToList();
        // Preserve startup intent while native identity validation is pending. SaveRunners marks
        // omitted records stopped; that must not silently cancel an undecided restored run.
        foreach (var pending in RunningAtLastClose)
            if (!infos.Any(info => info.Key == pending.Key)) infos.Add(pending);
        TradingHub.Strategies.LiveStore.SaveRunners(infos.GroupBy(i => i.Key).Select(g => g.OrderByDescending(i => i.Running).First()));
    }

    /// <summary>Aplica un tamaño aprobado: a la estrategia si está corriendo y a lo guardado para la próxima vez.</summary>
    public void ApplySize(string key, decimal size) => Live.ApplySize(key, size);

    public bool IsShuttingDown => _closingDown || Volatile.Read(ref _shutdownCompletion) != null;

    public TimeSpan HistoryFor(string profileId) =>
        TimeSpan.FromHours(Math.Clamp(Connections.Find(profileId)?.HistoryHours ?? 1.5, 0.1, 24 * 60));

    /// <summary>Abre (o reutiliza) los datos de un instrumento de una conexión.</summary>
    public InstrumentData Open(string profileId, Instrument instrument)
    {
        if (IsShuttingDown) throw new InvalidOperationException("Trading Hub is shutting down; new market-data consumers are disabled.");
        TrackOpenedInstrument(profileId, instrument);
        return Hub.GetOrStart(Connections.GetFeed(profileId), instrument, HistoryFor(profileId));
    }

    /// <summary>Abre un replay de datos guardados (no se graba ni se guarda en el espacio de trabajo).</summary>
    public InstrumentData OpenReplay(Instrument source, DateTime startUtc, DateTime endUtc, TimeSpan preload) =>
        OpenPracticeReplay(source, startUtc, endUtc, preload).Data;

    public SimBroker SimBrokerFor(InstrumentData data)
    {
        if (data.Feed is ReplayFeed)
            throw new InvalidOperationException("Practice replay owns its account, fills and balance; the shared simulated account cannot be used.");
        if (data.Feed is AggregateFeed)
            throw new InvalidOperationException("Composite crypto prices are data only and cannot be executed, including in the shared simulator. Use an explicitly owned practice replay.");
        if (!_simBrokers.TryGetValue(data.Instrument.Key, out var b))
        {
            _simOrderContext ??= new OrderContext(OrderContext.EphemeralSimulation(Portfolio.AccountID).Descriptor, _executionRoot);
            b = new SimBroker(data, Portfolio, Statistics, _simOrderContext);
            AccountExecutionProjector.For(b.Executions, b.OrderContext).Synchronize();
            _simBrokers[data.Instrument.Key] = b;
            AttachTradingAlerts(b);
            var inst = data.Instrument;
            b.NewMyTrade += t => Journal.Add(AccountCenter.SimId, new AccountFill(Guid.NewGuid().ToString("N"), t.Time, inst.Symbol,
                t.OrderDirection == OrderDirections.Buy, t.Volume, t.Price, inst.PointValue > 0 ? inst.PointValue : 1, t.Commission, t.Order?.Id.ToString() ?? ""));
        }

        return b;
    }

    public IEnumerable<SimBroker> SimBrokers => _simBrokers.Values;

    private readonly Dictionary<ITradingAccount, BracketManager> _brackets = new(ReferenceEqualityComparer.Instance);

    /// <summary>Stop/objetivos de una cuenta (uno por cuenta e instrumento), con la plantilla del instrumento.</summary>
    public BracketManager BracketsFor(ITradingAccount account)
    {
        if (!_brackets.TryGetValue(account, out var m))
        {
            m = new BracketManager(account);
            _brackets[account] = m;
            BracketsCreated?.Invoke(m);
        }

        m.Template = Settings.BracketTemplateFor(account.Instrument.Key);
        return m;
    }

    /// <summary>Se creó el manejador de stop/objetivos de una cuenta (para escuchar sus avisos).</summary>
    public event Action<BracketManager>? BracketsCreated;

    /// <summary>Cuentas con las que se puede operar este instrumento.</summary>
    public IEnumerable<AccountOption> AccountsFor(InstrumentData data)
    {
        if (data.Feed is ReplayFeed)
        {
            if (ReplaySessions.TryGet(data) is { } run) yield return new AccountOption(run.AccountId, run.Account.Name, false);
            yield break;
        }
        if (data.Feed is AggregateFeed) yield break;
        yield return new AccountOption("sim", "Simulated account", false);
        foreach (var p in Connections.Enabled.Where(p => p.SupportsTrading && p.MarketDataId == data.Instrument.FeedId && Connections.HasTradingCredentials(p)))
        {
            if (p.Kind == ConnectionKind.Bybit)
            {
                if (p.BybitEnvironment != BybitEnvironment.Real || p.AllowRealOrders)
                    yield return new AccountOption(p.Id, BybitName(p), p.BybitEnvironment == BybitEnvironment.Real);
            }
            else if (p.Testnet) yield return new AccountOption(p.Id, $"{p.Name} (testnet)", false);
            else if (p.AllowRealOrders) yield return new AccountOption(p.Id, $"{p.Name} — REAL", true);
        }

        // cuentas de Tradovate: operan cualquier futuro de CME, también desde un gráfico de Rithmic
        if (IsCmeFuture(data.Instrument))
            foreach (var p in Connections.Enabled.Where(p => p.Kind == ConnectionKind.Tradovate && !string.IsNullOrWhiteSpace(p.TradovateUser)))
            {
                var session = TradovateSessionFor(p);
                foreach (var a in session.Accounts)
                {
                    if (session.IsDemo) yield return new AccountOption(TradovateAccountId(p, a.Id), $"{a.Name} · Tradovate (demo)", false);
                    else if (p.AllowRealOrders) yield return new AccountOption(TradovateAccountId(p, a.Id), $"{a.Name} · Tradovate — REAL", true);
                }
            }

        // cuentas de Rithmic (aparecen cuando la sesión de órdenes termina de conectarse)
        foreach (var p in Connections.Enabled.Where(p => p.IsRithmic && p.MarketDataId == data.Instrument.FeedId && !string.IsNullOrWhiteSpace(p.RithmicUser)))
        {
            var session = DiscoverRithmicSession(p);
            if (session == null) continue;
            foreach (var a in session.Accounts)
            {
                if (session.IsPractice) yield return new AccountOption(RithmicAccountId(p, a.AccountId), $"{a.AccountId} · Rithmic (practice)", false);
                else if (p.AllowRealOrders) yield return new AccountOption(RithmicAccountId(p, a.AccountId), $"{a.AccountId} · Rithmic — REAL", true);
            }
        }
    }

    public ITradingAccount AccountFor(InstrumentData data, string accountId)
    {
        if (data.Feed is ReplayFeed) return ReplaySessions.Resolve(data, accountId);
        if (data.Feed is AggregateFeed)
            throw new InvalidOperationException("This aggregate instrument is data only. Its composite price is not an executable venue.");
        if (accountId == "sim") return SimBrokerFor(data);
        DisposeIdleReplacedBrokers();
        string key = accountId + "|" + data.Instrument.Key;
        if (_realBrokers.TryGetValue(key, out var b))
        {
            if (!_brokerOrigins.TryGetValue(b, out _)) return b;
            var current = Connections.FindForAccount(accountId);
            if (current == null || Connections.IsTradingGenerationCurrent(current, b.OrderContext.Descriptor.Generation)) return b;
            ReplaceStaleBrokers(current);
            if (_realBrokers.TryGetValue(key, out var retained)) return retained;
        }
        if (accountId.StartsWith("tradovate|", StringComparison.Ordinal))
        {
            var parts = accountId.Split('|');
            var tp = Connections.Find(parts[1]) ?? throw new KeyNotFoundException(accountId);
            if (tp.Kind != ConnectionKind.Tradovate) throw new InvalidOperationException("This saved account no longer belongs to a Tradovate connection.");
            var session = TradovateSessionFor(tp);
            long id = long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            var acc = session.Accounts.FirstOrDefault(a => a.Id == id) ?? new TradingHub.Feeds.Tradovate.TradovateAccount(id, parts[2], true);
            var context = TradingContext(tp, accountId, "Tradovate", "futures",
                (session.IsDemo ? "demo|" : "live|") + PublicEndpoint(session.Connection.RestBase),
                session.Accounts.Any(a => a.Id == id) ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
            return Cache(key, tp, new TradingHub.Feeds.Tradovate.TradovateBroker(data, session, acc,
                $"{acc.Name} · Tradovate{(session.IsDemo ? " (demo)" : " — REAL")}", context), session.Connection);
        }

        if (accountId.StartsWith("rithmic|", StringComparison.Ordinal))
        {
            var parts = accountId.Split('|');
            var rp = Connections.Find(parts[1]) ?? throw new KeyNotFoundException(accountId);
            if (!rp.IsRithmic) throw new InvalidOperationException("This saved account no longer belongs to a Rithmic connection.");
            var session = RithmicSessionFor(rp);
            var context = RithmicContextFor(rp, session, parts[2]);
            var contract = RithmicContractResolver(rp, session, data, parts[2]);
            return Cache(key, rp, new TradingHub.Feeds.Rithmic.RithmicBroker(data, session, parts[2],
                $"{parts[2]} · Rithmic{(session.IsPractice ? " (practice)" : " — REAL")}",
                contract, context), rithmicSession: session);
        }

        var p = Connections.Find(accountId) ?? throw new KeyNotFoundException(accountId);
        if (p.Kind == ConnectionKind.Bybit)
            return Cache(key, p, new BybitBroker(data, BybitSessionFor(p), BybitName(p)));
        if (p.Kind is not (ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot))
            throw new NotSupportedException("This connection provides market data only and has no order route.");
        var settings = Connections.BinanceSettingsOf(p);
        var binanceContext = TradingContext(p, accountId, "Binance", settings.Futures ? "usd-m-futures" : "spot",
            (settings.Testnet ? "testnet|" : "live|") + PublicEndpoint(settings.RestBase), null);
        return Cache(key, p, new BinanceBroker(data, settings, p.Testnet ? $"{p.Name} (testnet)" : $"{p.Name} — REAL",
            binanceContext, network: NetworkProxy.CreateFactory()));
    }

    /// <summary>
    /// How a real broker was built: from which connection, whether it traded real money then, and the connection settings
    /// that decide where its orders go (Binance: market, testnet or REAL, and API key, which also decide its REST server;
    /// Rithmic: system and user; active retired sessions retain their original routing and account context).
    /// </summary>
    private sealed class BrokerOrigin(string profileId, bool real, object? settings, TradingHub.Feeds.Tradovate.TradovateConnection? transport,
        string generation, TradingHub.Feeds.Rithmic.RithmicTradingSession? rithmicSession)
    {
        public string ProfileId { get; } = profileId;
        public bool Real { get; } = real;
        public object? Settings { get; } = settings;
        public TradingHub.Feeds.Tradovate.TradovateConnection? Transport { get; } = transport;
        public string Generation { get; } = generation;
        public TradingHub.Feeds.Rithmic.RithmicTradingSession? RithmicSession { get; } = rithmicSession;

        private volatile bool _replaced;

        /// <summary>The connection changed those settings after it was built: <see cref="AccountFor"/> no longer returns it.</summary>
        public bool Replaced { get => _replaced; set => _replaced = value; }
    }

    /// <summary>The settings a <see cref="BrokerOrigin"/> keeps, as the connection has them now (none for Tradovate, whose brokers every change rebuilds).</summary>
    private object? BrokerSettingsOf(ConnectionProfile p) => p switch
    {
        { IsRithmic: true } => (p.RithmicSystem, p.RithmicUser),
        { Kind: ConnectionKind.Tradovate } => null,
        { Kind: ConnectionKind.Bybit } => Connections.BybitContextOf(p),
        _ => (p.Kind, p.Testnet, p.ApiKey),
    };

    private ITradingAccount Cache(string key, ConnectionProfile p, ITradingAccount broker,
        TradingHub.Feeds.Tradovate.TradovateConnection? transport = null,
        TradingHub.Feeds.Rithmic.RithmicTradingSession? rithmicSession = null)
    {
        _realBrokers[key] = broker;
        _brokerOrigins.AddOrUpdate(broker, new BrokerOrigin(p.Id, broker.IsReal, BrokerSettingsOf(p), transport,
            broker.OrderContext.Descriptor.Generation, rithmicSession));
        _wireAccounts[(broker.OrderContext.Descriptor.Id, broker.Instrument.Key)] = new(broker);
        AccountExecutionProjector.For(broker.Executions, broker.OrderContext).Synchronize();
        AttachTradingAlerts(broker);
        return broker;
    }

    private static string PublicEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return "unresolved endpoint";
        var clean = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return clean.Uri.GetLeftPart(UriPartial.Path);
    }

    private OrderContext TradingContext(ConnectionProfile profile, string accountId, string provider, string product,
        string environment, string? nativeAccount, RithmicContextOrigin? retired = null)
    {
        Connections.EnsureTradingIdentity(profile);
        string local = retired?.LocalNamespace ?? profile.TradingNamespace;
        string generation = retired?.Generation ?? profile.TradingGeneration;
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\\n", local, generation, provider, product, environment, accountId))));
        if (_orderContexts.TryGetValue(id, out var existing)) return existing;
        var descriptor = new AccountContextDescriptor(id, provider, product, environment, local, generation, nativeAccount);
        var context = new OrderContext(descriptor);
        context.Configure(descriptor, _executionRoot, request =>
        {
            if (!_wireAccounts.TryGetValue((id, request.Instrument), out var weak) || !weak.TryGetTarget(out var broker))
                return "The prepared account route is no longer available.";
            decimal signed = request.Direction == OrderDirections.Buy ? request.Quantity : -request.Quantity;
            decimal position = broker.Position.Volume;
            bool increases = RealOrderPermission.IncreasesExposure(position, signed);
            if (request.ReduceOnly && increases)
                return "The requested reduction exceeds the currently reconciled account position.";
            if (increases && !Connections.IsTradingGenerationCurrent(profile, generation))
                return "The connection's trading namespace changed: new exposure is disabled; original-context reductions/cancels remain allowed.";
            return CheckRealOrderForProfile(Connections.Profiles.Contains(profile) ? profile : null, broker, position, signed);
        }, provider == "Rithmic" ? (request, position) => CheckRithmicNativePermission(profile, generation, id, request, position) : null);
        _orderContexts.Add(id, context);
        return context;
    }

    private bool RithmicInUse(TradingHub.Feeds.Rithmic.RithmicTradingSession session)
    {
        foreach (var broker in _realBrokers.Values.Concat(_replacedBrokers))
            if (_brokerOrigins.TryGetValue(broker, out var origin) && ReferenceEquals(origin.RithmicSession, session) && BrokerInUse(broker)) return true;
        return session.HasUnresolvedPositionClose || !session.IsConnected || session.Accounts.Any(account =>
            !session.OrdersReadyFor(account.AccountId) ||
            session.OrdersOf(account.AccountId).Any(order => !order.Done && order.Unfilled > 0) ||
            session.PositionsOf(account.AccountId).Any(position => position.Position.Net != 0));
    }

    /// <summary>Cached real brokers were replaced after their connection changed: what shows them switches to <see cref="AccountFor"/>.</summary>
    public event Action<IReadOnlyCollection<ITradingAccount>>? BrokersReplaced;

    /// <summary>
    /// A connection was saved: the cached brokers built from it whose mode, credentials or server it no longer has are
    /// dropped, so the next <see cref="AccountFor"/> builds ones that match, and <see cref="BrokersReplaced"/> tells the views.
    /// A dropped broker keeps working while a live strategy runs on it or it still has a position or working orders, so what
    /// it opened stays managed, but it can no longer add exposure (<see cref="CheckRealOrder"/>); otherwise it is disposed.
    /// </summary>
    private void ReplaceStaleBrokers(ConnectionProfile p)
    {
        var now = BrokerSettingsOf(p);
        var replaced = new List<ITradingAccount>();
        foreach (var (key, broker) in _realBrokers.ToList())
        {
            if (!_brokerOrigins.TryGetValue(broker, out var origin) || origin.ProfileId != p.Id
                || (origin.Transport == null && Equals(origin.Settings, now) &&
                    (p.Kind == ConnectionKind.Bybit || origin.Generation == p.TradingGeneration &&
                        Connections.IsTradingGenerationCurrent(p, origin.Generation)))) continue;
            origin.Replaced = true;
            if (broker is BybitBroker bybit) bybit.Retire();
            _realBrokers.Remove(key);
            _replacedBrokers.Add(broker);
            replaced.Add(broker);
        }

        if (replaced.Count == 0) return;
        DisposeIdleReplacedBrokers();
        BrokersReplaced?.Invoke(replaced);
    }

    private bool BrokerInUse(ITradingAccount broker)
    {
        if (!broker.PositionReady || !broker.OrdersReady || broker.Position.Volume != 0 || broker.ActiveOrders.Any()) return true;
        foreach (var runner in Runners)
            if (ReferenceEquals(runner.Account, broker) && (runner.IsRunning || runner.IsClosing || runner.HasWorkingOrders)) return true;
        return false;
    }

    private bool TradovateInUse(TradingHub.Feeds.Tradovate.TradovateTradingSession session)
    {
        if (!session.SnapshotReady || session.HasOpenPositionsOrOrders) return true;
        foreach (var broker in _realBrokers.Values)
            if (_brokerOrigins.TryGetValue(broker, out var origin) && ReferenceEquals(origin.Transport, session.Connection) && BrokerInUse(broker)) return true;
        foreach (var broker in _replacedBrokers)
            if (_brokerOrigins.TryGetValue(broker, out var origin) && ReferenceEquals(origin.Transport, session.Connection) && BrokerInUse(broker)) return true;
        return false;
    }

    private bool RetainTradovateFeed(IMarketDataFeed feed)
    {
        if (feed is not TradingHub.Feeds.Tradovate.TradovateFeed tv) return false;
        foreach (var data in Hub.Items)
            if (ReferenceEquals(data.Feed, feed)) return true;
        foreach (var session in _tradovateSessions.Values)
            if (ReferenceEquals(session.Connection, tv.Connection) && TradovateInUse(session)) return true;
        foreach (var session in _retiredTradovateSessions)
            if (ReferenceEquals(session.Connection, tv.Connection) && TradovateInUse(session)) return true;
        return false;
    }

    private static void DisposeTradovate(TradingHub.Feeds.Tradovate.TradovateTradingSession session)
    {
        session.Dispose();
        // No remaining account consumer needs this channel; a retained feed can still use market data independently.
        session.Connection.User.Dispose();
    }

    /// <summary>Unknown snapshots and outstanding requests are consumers too; only confirmed idle contexts can leave.</summary>
    private void DisposeIdleReplacedBrokers()
    {
        for (int i = _replacedBrokers.Count - 1; i >= 0; i--)
        {
            var broker = _replacedBrokers[i];
            if (BrokerInUse(broker)) continue;
            _replacedBrokers.RemoveAt(i);
            (broker as IDisposable)?.Dispose();
        }
        for (int i = _retiredTradovateSessions.Count - 1; i >= 0; i--)
        {
            var session = _retiredTradovateSessions[i];
            if (TradovateInUse(session)) continue;
            _retiredTradovateSessions.RemoveAt(i);
            DisposeTradovate(session);
        }
        if (_retiredRithmicSessions.Count > 0)
        {
            lock (_shutdownGate)
            {
                for (int i = _retiredRithmicSessions.Count - 1; i >= 0; i--)
                {
                    var session = _retiredRithmicSessions[i];
                    if (RithmicInUse(session)) continue;
                    _retiredRithmicSessions.RemoveAt(i);
                    RetireRithmicSession(session);
                }
            }
        }
        Connections.ReleaseRetiredFeeds();
        ReleaseRetiredBybitSessions();
    }

    /// <summary>
    /// Real-order permission when an order is sent, from the connection settings as they are now (cached brokers and
    /// restored strategies included; <see cref="AccountFor"/> does not check it): null if the order may go out, otherwise
    /// why not. Only an order that opens, adds to or reverses a position needs permission: on a real account its connection
    /// enabled with real orders allowed, and on a broker <see cref="AccountFor"/> built, a connection that still has the
    /// mode (REAL or not), credentials and server it was built with. Reductions and cancels never need it.
    /// </summary>
    public string? CheckRealOrder(string accountId, ITradingAccount account, decimal position, decimal signedQuantity) =>
        CheckRealOrderForProfile(Connections.FindForAccount(accountId), account, position, signedQuantity);

    private string? CheckRealOrderForProfile(ConnectionProfile? p, ITradingAccount account, decimal position, decimal signedQuantity)
    {
        bool increases = RealOrderPermission.IncreasesExposure(position, signedQuantity);
        if (IsShuttingDown && increases)
            return "Trading Hub is shutting down: new exposure is disabled; cancellation and reducing exits remain available.";
        if (p != null && _brokerOrigins.TryGetValue(account, out var origin) && increases)
        {
            // Rithmic brokers follow their session's mode; the others keep the one they were built with
            bool realNow = p.IsRithmic ? account.IsReal : p.Kind == ConnectionKind.Bybit
                ? p.BybitEnvironment == BybitEnvironment.Real : !p.Testnet;
            string Mode(bool real) => real ? "REAL" : p.IsRithmic ? "practice"
                : p.Kind == ConnectionKind.Tradovate || p.Kind == ConnectionKind.Bybit && p.BybitEnvironment == BybitEnvironment.Demo ? "demo" : "testnet";
            const string Rest = ": new orders are disabled; cancel/close allowed";
            if (origin.Real != realNow) return $"{p.Name} is now {Mode(realNow)} but this account was opened as {Mode(origin.Real)}" + Rest;
            if (origin.Replaced) return $"the connection {p.Name} changed its keys, system or login after this account was opened" + Rest;
        }
        if (increases && p is { MaxOrderQuantity: > 0 } && Math.Abs(signedQuantity) > p.MaxOrderQuantity)
            return "Order quantity exceeds this connection's configured maximum.";

        return RealOrderPermission.Check(account.IsReal, p is { } c ? (c.Name, c.Enabled, c.AllowRealOrders) : null, position, signedQuantity);
    }

    private TradingHub.Strategies.ILiveOrderGuard? _orderGuard;

    /// <summary>The guard every live strategy runs with: real-order permission for each order that adds exposure.</summary>
    internal TradingHub.Strategies.ILiveOrderGuard OrderGuard => _orderGuard ??= new RealOrderGuard(this);

    private sealed class RealOrderGuard(AppServices svc) : TradingHub.Strategies.ILiveOrderGuard
    {
        public string? Check(TradingHub.Strategies.LiveStrategyRunner runner, TradingHub.Strategies.OrderSide side, decimal quantity, decimal positionAfter)
        {
            decimal signed = side == TradingHub.Strategies.OrderSide.Buy ? quantity : -quantity;
            return svc.CheckRealOrder(runner.AccountId, runner.Account, positionAfter - signed, signed);
        }
    }

    /// <summary>
    /// The quit's stop, once: saves the live state (what was running resumes at the next start, if enabled in Settings) and
    /// stops every live strategy the chosen way. <see cref="QuitMode.KeepStops"/> leaves their stop loss and take profit
    /// working at the broker and records them for the next start to adopt; <see cref="QuitMode.CancelStops"/> cancels their
    /// orders and leaves the positions open (what ⌘Q always did); <see cref="QuitMode.ClosePositions"/> closes the
    /// positions, the only quit that liquidates. Later saves are skipped: they would mark the strategies stopped.
    /// </summary>
    internal void StopLiveForQuit(QuitMode mode)
    {
        if (_closingDown) return;
        SaveLiveState();
        _closingDown = true;
        foreach (var r in Runners.Where(r => r.IsRunning).ToList())
        {
            try
            {
                r.Stop(flatten: mode == QuitMode.ClosePositions, keepProtection: mode == QuitMode.KeepStops);
                // Record only the protection intentionally retained; unresolved cancellations remain tracked until
                // acknowledgment and the quit flow waits for them or warns before disposing the connection.
                if (r.Persist && mode != QuitMode.KeepStops) TradingHub.Strategies.LiveStore.SaveProtectionOrderIds(r.Info());
            }
            catch (Exception ex)
            {
                this.LogError($"Error stopping {r.StrategyName} to quit", ex);
            }
        }
    }

    private readonly object _shutdownGate = new();
    private TaskCompletionSource? _shutdownCompletion;

    public void Shutdown()
    {
        Task shutdown = ShutdownAsync();
        if (!shutdown.IsCompleted && Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            // Normal quits await the terminal lifetime. This idempotent native Exit fallback keeps
            // the dispatcher alive for already-owned UI/CLI drains rather than blocking their continuations.
            if (Avalonia.Threading.Dispatcher.UIThread.SupportsRunLoops)
            {
                var frame = new Avalonia.Threading.DispatcherFrame(exitWhenRequested: false);
                _ = shutdown.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => frame.Continue = false),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Avalonia.Threading.Dispatcher.UIThread.PushFrame(frame);
            }
            else
            {
                while (!shutdown.IsCompleted)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(1);
                }
            }
        }
        shutdown.GetAwaiter().GetResult();
    }

    internal Task ShutdownAsync()
    {
        TaskCompletionSource completion;
        lock (_shutdownGate)
        {
            if (_shutdownCompletion != null) return _shutdownCompletion.Task;
            completion = _shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = CompleteShutdownAsync(completion);
        return completion.Task;
    }

    private async Task CompleteShutdownAsync(TaskCompletionSource completion)
    {
        void Close(Action action)
        {
            try { action(); }
            catch (Exception error) { this.LogError("Error closing", error); }
        }
        async Task CloseOwnedAsync(Func<Task> stop, string message)
        {
            try { await stop(); }
            catch (Exception error) { this.LogError(message, error); }
        }
        try
        {
            var workDrain = CloseOwnedAsync(Work.StopAsync, "Error closing ordinary work");
            var recordingDrain = CloseOwnedAsync(Hub.StopRecordingAsync, "Error closing recorded history");
            Close(() => StopLiveForQuit(QuitMode.CancelStops));
            TradingHub.Strategies.LiveStrategyRunner.Started -= OnRunnerStarted;
            // Cancel admission first; ordinary consumers keep their dependencies through final persistence and refresh.
            var startsDrain = CloseOwnedAsync(() => _live?.ShutdownStartsAsync() ?? Task.CompletedTask, "Error closing pending strategy starts");
            var authoringDrain = CloseOwnedAsync(ShutdownAuthoringAsync, "Error closing authoring");
            var historyDrain = CloseOwnedAsync(ShutdownHistoryArchivesAsync, "Error closing history archives");
            var operationsDrain = CloseOwnedAsync(ShutdownOperationsAsync, "Error closing background history");
            await workDrain;
            var researchDrain = CloseOwnedAsync(() => TradingViewResearchService.DisposeDefaultAsync().AsTask(), "Error closing authorized research");
            var scriptDrain = CloseOwnedAsync(() => Scripts?.DisposeAsync().AsTask() ?? Task.CompletedTask, "Error closing custom indicator monitoring");
            var providerDrain = CloseOwnedAsync(ShutdownKvpProvidersAsync, "Error closing provider sessions");
            var longbridgeDrain = CloseOwnedAsync(Connections.StopLongbridgeAsync, "Error closing Longbridge authorization or data");
            await startsDrain;
            await authoringDrain;
            await researchDrain;
            await scriptDrain;
            await historyDrain;
            await operationsDrain;
            await providerDrain;
            await longbridgeDrain;
            await CloseOwnedAsync(DrainRithmicSessionsAsync, "Error closing Rithmic trading session");
            await CloseOwnedAsync(() => DisposeReplayAlertsAsync().AsTask(), "Error closing replay or alerts");
            Close(Sleep.Dispose);
            foreach (var broker in _realBrokers.Values) Close(() => (broker as IDisposable)?.Dispose());
            foreach (var broker in _replacedBrokers) Close(() => (broker as IDisposable)?.Dispose());
            foreach (var session in _tradovateSessions.Values) Close(() => DisposeTradovate(session));
            foreach (var session in _retiredTradovateSessions) Close(() => DisposeTradovate(session));
            foreach (var broker in _simBrokers.Values) Close(broker.Dispose);
            Close(Hub.Dispose);
            await recordingDrain;
            Close(Connections.DisposeAll);
        }
        finally
        {
            try
            {
                // Preserve reviewed startup fix85c439: only true exit owns monitoring, including
                // when another shutdown stage fails; tray hiding must keep hot reload alive.
                if (Scripts != null) await Scripts.DisposeAsync();
            }
            catch (Exception error) { this.LogError("Error closing custom indicator monitoring", error); }
            finally { completion.TrySetResult(); }
        }
    }
}
