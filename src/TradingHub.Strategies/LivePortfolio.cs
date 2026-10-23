using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TradingHub.Strategies;

/// <summary>
/// Un llenado de una estrategia en vivo. <paramref name="Symbol"/> y <paramref name="PointValue"/> dicen en qué contrato
/// se ejecutó (por ejemplo MNQ aunque la estrategia mire el NQ); los llenados viejos no los tienen y usan los del
/// instrumento de la estrategia.
/// </summary>
public sealed record LiveFill(DateTime Time, bool Buy, decimal Quantity, decimal Price, string? Symbol = null, decimal PointValue = 0);

/// <summary>
/// Contabilidad propia de una estrategia en vivo: sus llenados, su posición (aunque otras estrategias
/// operen el mismo instrumento en la misma cuenta), resultado realizado, operaciones cerradas y curva.
/// </summary>
public sealed class LiveLedger
{
    private DateTime _openTime;
    private int _number;

    public LiveLedger(string key, decimal pointValue)
    {
        Key = key;
        PointValue = pointValue <= 0 ? 1 : pointValue;
    }

    public string Key { get; }

    /// <summary>Valor del punto del instrumento de la estrategia (el de los llenados que no traen el suyo).</summary>
    public decimal PointValue { get; }
    public List<LiveFill> Fills { get; } = new();

    /// <summary>Posición en unidades del contrato en el que está abierta (<see cref="OpenSymbol"/>).</summary>
    public decimal Position { get; private set; }
    public decimal AveragePrice { get; private set; }
    public decimal Realized { get; private set; }
    public List<ClosedTrade> Trades { get; } = new();

    /// <summary>Contrato de la posición abierta (null = el de la estrategia, o plana).</summary>
    public string? OpenSymbol { get; private set; }

    /// <summary>Valor del punto de la posición abierta.</summary>
    public decimal OpenPointValue { get; private set; }

    /// <summary>
    /// Unreadable lines skipped when it was loaded (for example one cut off by a crash). The fills after them are kept and
    /// the file is never rewritten.
    /// </summary>
    public int SkippedLines { get; internal set; }

    public decimal OpenPnL(decimal last) => Position == 0 || last <= 0 ? 0 : (last - AveragePrice) * Position * (OpenPointValue > 0 ? OpenPointValue : PointValue);

    public void Apply(LiveFill f)
    {
        Fills.Add(f);
        decimal pv = f.PointValue > 0 ? f.PointValue : PointValue;
        decimal signed = f.Buy ? f.Quantity : -f.Quantity;
        decimal before = Position;
        if (before == 0 || Math.Sign(before) == Math.Sign(signed))
        {
            if (before == 0)
            {
                _openTime = f.Time;
                OpenSymbol = f.Symbol;
                OpenPointValue = pv;
            }

            decimal total = Math.Abs(before) + f.Quantity;
            AveragePrice = total > 0 ? (AveragePrice * Math.Abs(before) + f.Price * f.Quantity) / total : f.Price;
            Position = before + signed;
            return;
        }

        decimal closing = Math.Min(Math.Abs(before), f.Quantity);
        decimal pnl = (f.Price - AveragePrice) * closing * Math.Sign(before) * (OpenPointValue > 0 ? OpenPointValue : pv);
        Realized += pnl;
        Trades.Add(new ClosedTrade(++_number, before > 0, _openTime, AveragePrice, f.Time, f.Price, closing, pnl, 0, 0, 0, "", "", 0));
        Position = before + signed;
        if (Position == 0)
        {
            AveragePrice = 0;
            OpenSymbol = null;
            OpenPointValue = 0;
        }
        else if (Math.Sign(Position) != Math.Sign(before))
        {
            // dio vuelta: lo que sobra abre al precio del llenado
            AveragePrice = f.Price;
            _openTime = f.Time;
            OpenSymbol = f.Symbol;
            OpenPointValue = pv;
        }
    }

    /// <summary>Curva de capital al cierre de cada operación (empieza en 0).</summary>
    public List<EquityPoint> Curve(decimal capital)
    {
        var res = new List<EquityPoint>();
        decimal eq = capital, peak = capital;
        if (Fills.Count > 0) res.Add(new EquityPoint(Fills[0].Time, capital, 0));
        foreach (var t in Trades)
        {
            eq += t.NetPnl;
            peak = Math.Max(peak, eq);
            res.Add(new EquityPoint(t.ExitTime, eq, peak - eq));
        }

        return res;
    }
}

/// <summary>Estado de una estrategia en vivo (lo que se guarda y lee el laboratorio).</summary>
public sealed class LiveRunnerInfo
{
    public string Key { get; set; } = "";
    public string Strategy { get; set; } = "";
    public string StrategyId { get; set; } = "";
    public string FeedId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Period { get; set; } = "";
    public string Account { get; set; } = "";
    public bool Running { get; set; }
    public DateTime StartedUtc { get; set; }
    public decimal Size { get; set; } = 1;
    public decimal PointValue { get; set; } = 1;

    /// <summary>Contrato en el que ejecuta, si no es el que mira (por ejemplo MNQ para una estrategia del NQ).</summary>
    public string? ExecSymbol { get; set; }
    public decimal Position { get; set; }
    public decimal OpenPnL { get; set; }
    public decimal Realized { get; set; }
    public int Trades { get; set; }

    /// <summary>De dónde se lanzó (panel, chart u otro), cuenta y conexión, para volver a lanzarla igual.</summary>
    public string Source { get; set; } = "panel";
    public string AccountId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public string? TimeZoneId { get; set; }
    public double SessionStartHours { get; set; }

    /// <summary>Límites de riesgo (en dinero; 0 = sin límite) y si al tocarlos se cierra la posición.</summary>
    public decimal MaxDailyLoss { get; set; }
    public decimal MaxDrawdown { get; set; }
    public bool CloseOnLimit { get; set; } = true;
    public string? PortfolioId { get; set; }
    public string? StopReason { get; set; }

    /// <summary>Pausada (no abre operaciones nuevas) y si opera dinero real.</summary>
    public bool Paused { get; set; }
    public bool IsReal { get; set; }
}

/// <summary>Un portafolio en vivo: estrategias que corren juntas, con su capital y, si querés, un agente de riesgo.</summary>
public sealed class LivePortfolioDef
{
    public const string AutoId = "auto";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";

    /// <summary>El automático: todas las estrategias que corren en vivo sin un portafolio asignado.</summary>
    public bool Auto { get; set; }

    /// <summary>Estrategias del portafolio (clave estrategia|mercado|cuenta). En el automático se calcula sola.</summary>
    public List<string> Members { get; set; } = new();

    /// <summary>Capital de referencia (para porcentajes, drawdown y Sharpe).</summary>
    public decimal Capital { get; set; } = 25_000m;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public LiveRiskAgentSettings Agent { get; set; } = new();
}

/// <summary>
/// Agente de riesgo de un portafolio: Claude mira cómo viene cada estrategia (resultado, racha, drawdown, volatilidad y
/// correlación) y reparte el riesgo cambiando el tamaño de cada una. Por defecto propone y vos aprobás cada cambio;
/// en modo automático (solo cuentas que no son reales) aplica solo, siempre dentro de los topes.
/// </summary>
public sealed class LiveRiskAgentSettings
{
    public bool Enabled { get; set; }

    /// <summary>propose (cada cambio lo aprobás vos) o auto (aplica solo; nunca con cuentas reales).</summary>
    public string Mode { get; set; } = "propose";

    /// <summary>Cada cuántos minutos revisa (si hubo operaciones nuevas) y cada cuántas horas igual aunque no haya.</summary>
    public int EveryMinutes { get; set; } = 60;
    public int MinNewTrades { get; set; } = 3;
    public int ForceEveryHours { get; set; } = 8;
    public int MaxRunsPerDay { get; set; } = 8;
    public double MaxUsdPerRun { get; set; } = 2;

    /// <summary>Topes del tamaño que puede asignar a cada estrategia.</summary>
    public decimal MinSize { get; set; } = 0.25m;
    public decimal MaxSize { get; set; } = 3m;

    /// <summary>Riesgo total por día que reparte entre las estrategias (% del capital) y drawdown máximo del portafolio (%).</summary>
    public decimal DailyRiskPercent { get; set; } = 2m;
    public decimal MaxDrawdownPercent { get; set; } = 10m;

    /// <summary>Indicaciones tuyas para el agente (por ejemplo «priorizá las de NQ», «nada más de ×2»).</summary>
    public string Instructions { get; set; } = "";

    /// <summary>Modelo y esfuerzo del agente (por defecto Opus 5.5 con esfuerzo extra).</summary>
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "xhigh";

    public DateTime? LastRunUtc { get; set; }
    public int TradesAtLastRun { get; set; }
    public string LastSummary { get; set; } = "";
    public string? LastError { get; set; }
    public List<DateTime> RunsUtc { get; set; } = new();
    public double CostTotal { get; set; }
}

/// <summary>Un cambio de tamaño propuesto para una estrategia.</summary>
public sealed class SizingItem
{
    public string Key { get; set; } = "";
    public string Strategy { get; set; } = "";
    public decimal CurrentSize { get; set; }
    public decimal ProposedSize { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>Límites propuestos (null = no se tocan) y los que tenía.</summary>
    public decimal? ProposedMaxDailyLoss { get; set; }
    public decimal? ProposedMaxDrawdown { get; set; }
    public decimal CurrentMaxDailyLoss { get; set; }
    public decimal CurrentMaxDrawdown { get; set; }

    /// <summary>pending, approved o rejected.</summary>
    public string Status { get; set; } = "pending";
}

/// <summary>Propuesta de cambios de tamaño (de las reglas o de Claude). Nada se aplica sin aprobación.</summary>
public sealed class SizingProposal
{
    public string Id { get; set; } = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..4];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "reglas";
    public string Summary { get; set; } = "";
    public List<SizingItem> Items { get; set; } = new();

    /// <summary>Portafolio en vivo del que salió (las del agente de riesgo).</summary>
    public string? PortfolioId { get; set; }
    public string? PortfolioName { get; set; }
    public bool IsPending => Items.Any(i => i.Status == "pending");
}

/// <summary>Métricas en vivo de una estrategia, para la cartera.</summary>
public sealed class LiveStats
{
    public LiveRunnerInfo Info { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public decimal Net { get; set; }
    public decimal CurrentDrawdown { get; set; }
    public decimal RecentProfitFactor { get; set; }
    public decimal RecentExpectancy { get; set; }
    public int RecentTrades { get; set; }
}

/// <summary>
/// Persistencia de la cartera en vivo (en {raíz}/Live): llenados por estrategia, estado de cada estrategia y
/// propuestas de tamaño. La app escribe el estado; el laboratorio de Claude lo lee y escribe propuestas.
/// </summary>
public static class LiveStore
{
    public static string Root { get; set; } = Path.Combine(TradingHub.Common.Product.AppData, "Live");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    // fills that could not be written to their ledger yet (by ledger file), in order: they go first with the next fill
    private static readonly Dictionary<string, List<LiveFill>> Pending = new();

    private static string Clean(string key) => string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '|' ? '_' : c));
    private static string LedgerPath(string key) => Path.Combine(Root, "llenados", Clean(key) + ".jsonl");

    /// <summary>Archivo de llenados de una estrategia (para saber si cambió).</summary>
    public static string LedgerFile(string key) => LedgerPath(key);

    /// <summary>Borra el historial de ejecuciones de una estrategia (queda en la cartera, sin operaciones).</summary>
    public static void DeleteFills(string key)
    {
        lock (Gate)
        {
            Pending.Remove(LedgerPath(key)); // they belong to the deleted history
            try
            {
                if (File.Exists(LedgerPath(key))) File.Delete(LedgerPath(key));
            }
            catch
            {
                // se reintenta
            }
        }
    }
    private static string RunnersPath => Path.Combine(Root, "estrategias.json");
    private static string ProposalsPath => Path.Combine(Root, "propuestas.json");
    private static string PortfoliosPath => Path.Combine(Root, "portafolios.json");
    private static string? _lastRunnersJson;
    private static string? _lastRunnersPath;

    /// <summary>
    /// Adds a fill to the strategy's ledger. If the file cannot be written the fill is kept pending in memory, in order, and
    /// written before the next one (meanwhile <see cref="LoadLedger"/> includes it in this process). Returns the error, or
    /// null once everything pending is saved.
    /// </summary>
    public static string? AppendFill(string key, LiveFill f)
    {
        lock (Gate)
        {
            string path = LedgerPath(key);
            if (!Pending.TryGetValue(path, out var pending)) Pending[path] = pending = new List<LiveFill>();
            pending.Add(f);
            string? error = WritePending(path, pending);
            if (error == null) Pending.Remove(path);
            return error;
        }
    }

    /// <summary>Fills of a strategy that could not be written to its ledger yet.</summary>
    public static int PendingFills(string key)
    {
        lock (Gate) return Pending.TryGetValue(LedgerPath(key), out var pending) ? pending.Count : 0;
    }

    /// <summary>Appends the pending fills in order, all or none; returns the error, or null.</summary>
    private static string? WritePending(string path, List<LiveFill> pending)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
            long end = file.Length;
            var text = new StringBuilder();
            if (end > 0)
            {
                // a line cut off by an earlier failure stays on its own line (skipped when loading) instead of absorbing this one
                file.Seek(-1, SeekOrigin.End);
                if (file.ReadByte() != '\n') text.Append('\n');
            }

            foreach (var f in pending) text.Append(JsonSerializer.Serialize(f)).Append('\n');
            file.Seek(end, SeekOrigin.Begin);
            try
            {
                file.Write(Encoding.UTF8.GetBytes(text.ToString()));
            }
            catch
            {
                // nothing half-written: everything stays pending for the next attempt
                try { file.SetLength(end); } catch (IOException) { /* the next attempt starts on a new line */ }
                throw;
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Reads a strategy's ledger. A line that cannot be read (for example one cut off by a crash) is skipped and counted in
    /// <see cref="LiveLedger.SkippedLines"/>, and reading goes on; the file is never rewritten. Pending fills come last.
    /// </summary>
    public static LiveLedger LoadLedger(string key, decimal pointValue)
    {
        var l = new LiveLedger(key, pointValue);
        lock (Gate)
        {
            string path = LedgerPath(key);
            try
            {
                if (File.Exists(path))
                    foreach (var line in File.ReadLines(path))
                    {
                        if (line.Length == 0) continue;
                        LiveFill? f;
                        try
                        {
                            f = JsonSerializer.Deserialize<LiveFill>(line);
                        }
                        catch (JsonException)
                        {
                            f = null;
                        }

                        if (f != null) l.Apply(f);
                        else l.SkippedLines++;
                    }
            }
            catch
            {
                // archivo dañado: se usa lo que se pudo leer
            }

            if (Pending.TryGetValue(path, out var pending))
                foreach (var f in pending) l.Apply(f);
        }

        return l;
    }

    public static List<LiveRunnerInfo> LoadRunners()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(RunnersPath)) return JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new();
            }
            catch
            {
                // se reconstruye
            }

            return new();
        }
    }

    /// <summary>Guarda el estado (mezcla con lo guardado: las estrategias detenidas quedan con su historia).</summary>
    public static void SaveRunners(IEnumerable<LiveRunnerInfo> current)
    {
        lock (Gate)
        {
            var all = new Dictionary<string, LiveRunnerInfo>();
            try
            {
                if (File.Exists(RunnersPath))
                    foreach (var r in JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new()) all[r.Key] = r;
            }
            catch
            {
                // archivo dañado
            }

            foreach (var r in all.Values) r.Running = false;
            foreach (var r in current) all[r.Key] = r;
            try
            {
                // solo si cambió algo (se llama cada pocos segundos)
                var json = JsonSerializer.Serialize(all.Values.ToList(), Json);
                if (json == _lastRunnersJson && _lastRunnersPath == RunnersPath && File.Exists(RunnersPath)) return;
                Directory.CreateDirectory(Root);
                File.WriteAllText(RunnersPath, json);
                _lastRunnersJson = json;
                _lastRunnersPath = RunnersPath;
            }
            catch
            {
                // se reintenta en la próxima vuelta
            }
        }
    }

    public static decimal SizeFor(string key) => LoadRunners().FirstOrDefault(r => r.Key == key)?.Size ?? 1;

    /// <summary>Lo guardado de una estrategia (tamaño, límites, portafolio), o null.</summary>
    public static LiveRunnerInfo? InfoFor(string key) => LoadRunners().FirstOrDefault(r => r.Key == key);

    /// <summary>Saca una estrategia de la cartera (y, si se pide, borra su historial de ejecuciones).</summary>
    public static void RemoveRunner(string key, bool deleteFills)
    {
        lock (Gate)
        {
            if (deleteFills) Pending.Remove(LedgerPath(key)); // they belong to the deleted history
            try
            {
                var all = File.Exists(RunnersPath) ? JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new() : new();
                all.RemoveAll(r => r.Key == key);
                Directory.CreateDirectory(Root);
                File.WriteAllText(RunnersPath, JsonSerializer.Serialize(all, Json));
                _lastRunnersJson = null;
                if (deleteFills && File.Exists(LedgerPath(key))) File.Delete(LedgerPath(key));
            }
            catch
            {
                // se reintenta
            }
        }
    }

    /// <summary>Actualiza lo guardado de una estrategia que no está corriendo (tamaño, límites, portafolio).</summary>
    public static void UpdateRunner(string key, Action<LiveRunnerInfo> change)
    {
        lock (Gate)
        {
            try
            {
                var all = File.Exists(RunnersPath) ? JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new() : new();
                var info = all.FirstOrDefault(r => r.Key == key);
                if (info == null) return;
                change(info);
                File.WriteAllText(RunnersPath, JsonSerializer.Serialize(all, Json));
                _lastRunnersJson = null;
            }
            catch
            {
                // se reintenta
            }
        }
    }

    /// <summary>Portafolios en vivo guardados (el automático incluido, con su configuración).</summary>
    public static List<LivePortfolioDef> LoadPortfolios()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(PortfoliosPath)) return JsonSerializer.Deserialize<List<LivePortfolioDef>>(File.ReadAllText(PortfoliosPath)) ?? new();
            }
            catch
            {
                // dañado: se arma de nuevo
            }

            return new();
        }
    }

    public static void SavePortfolios(List<LivePortfolioDef> list)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(PortfoliosPath, JsonSerializer.Serialize(list, Json));
            }
            catch
            {
                // se reintenta en el próximo cambio
            }
        }
    }

    public static List<SizingProposal> LoadProposals()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(ProposalsPath)) return JsonSerializer.Deserialize<List<SizingProposal>>(File.ReadAllText(ProposalsPath)) ?? new();
            }
            catch
            {
                // dañado
            }

            return new();
        }
    }

    public static void SaveProposals(List<SizingProposal> list)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(ProposalsPath, JsonSerializer.Serialize(list.OrderByDescending(p => p.CreatedUtc).Take(100).ToList(), Json));
        }
    }

    public static void AddProposal(SizingProposal p)
    {
        lock (Gate)
        {
            var list = LoadProposals();
            list.Add(p);
            SaveProposals(list);
        }
    }

    public static DateTime ProposalsStamp => File.Exists(ProposalsPath) ? File.GetLastWriteTimeUtc(ProposalsPath) : DateTime.MinValue;

    /// <summary>Métricas de cada estrategia guardada (en vivo o detenida) a partir de sus llenados.</summary>
    public static List<LiveStats> Stats(decimal capital = 10_000)
    {
        var res = new List<LiveStats>();
        foreach (var info in LoadRunners())
        {
            var ledger = LoadLedger(info.Key, info.PointValue);
            res.Add(StatsOf(info, ledger, capital));
        }

        return res;
    }

    public static LiveStats StatsOf(LiveRunnerInfo info, LiveLedger ledger, decimal capital)
    {
        var curve = ledger.Curve(capital);
        var m = PerformanceMetrics.Compute(ledger.Trades, curve, capital, true, 0, 0);
        var recent = ledger.Trades.TakeLast(20).ToList();
        decimal gp = recent.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), gl = -recent.Where(t => t.NetPnl <= 0).Sum(t => t.NetPnl);
        decimal peak = capital, eq = capital;
        foreach (var t in ledger.Trades)
        {
            eq += t.NetPnl;
            peak = Math.Max(peak, eq);
        }

        return new LiveStats
        {
            Info = info,
            Metrics = m,
            Net = ledger.Realized + info.OpenPnL,
            CurrentDrawdown = peak - eq,
            RecentTrades = recent.Count,
            RecentProfitFactor = gl > 0 ? gp / gl : gp > 0 ? 999 : 0,
            RecentExpectancy = recent.Count > 0 ? recent.Average(t => t.NetPnl) : 0,
        };
    }
}

/// <summary>
/// Reglas simples de tamaño: más a las que rinden (profit factor reciente alto y sin drawdown fuerte), menos a
/// las que no, y pausa para las que superan su peor drawdown con mala racha. Solo propone: aplica el usuario.
/// </summary>
public static class LiveSizing
{
    public const int MinTrades = 10;
    public const decimal MaxSize = 3, MinSize = 0.25m;

    public static SizingProposal SuggestByRules(IReadOnlyList<LiveStats> stats)
    {
        var p = new SizingProposal { Source = "reglas" };
        foreach (var s in stats.Where(s => s.Info.Running))
        {
            decimal size = s.Info.Size;
            string name = $"{s.Info.Strategy} · {s.Info.Symbol}";
            if (s.Metrics.TotalTrades < MinTrades) continue; // poca muestra: no se toca
            decimal pf = s.RecentProfitFactor;
            decimal maxDd = s.Metrics.MaxDrawdown;
            decimal typicalLoss = Math.Abs(s.Metrics.AverageLoss);
            // "profundo": cerca del peor drawdown y de al menos tres pérdidas típicas (una racha corta es normal)
            bool deepDd = maxDd > 0 && s.CurrentDrawdown >= maxDd * 0.9m && s.CurrentDrawdown >= typicalLoss * 3 && s.CurrentDrawdown > 0;
            bool shallowDd = s.CurrentDrawdown <= Math.Max(maxDd * 0.5m, typicalLoss * 2);
            if (pf < 0.7m && deepDd && s.RecentTrades >= 15)
                p.Items.Add(new SizingItem { Key = s.Info.Key, Strategy = name, CurrentSize = size, ProposedSize = 0, Reason = $"Pause: PF over the last {s.RecentTrades} trades {pf:0.00} and at its worst drawdown ({s.CurrentDrawdown:N2})." });
            else if (pf < 0.9m || deepDd)
                p.Items.Add(new SizingItem { Key = s.Info.Key, Strategy = name, CurrentSize = size, ProposedSize = Math.Max(MinSize, Math.Round(size * 0.5m, 2)), Reason = $"Halve size: recent PF {pf:0.00}{(deepDd ? ", drawdown near its maximum" : "")}." });
            else if (pf >= 1.5m && s.RecentExpectancy > 0 && shallowDd && size < MaxSize)
                p.Items.Add(new SizingItem { Key = s.Info.Key, Strategy = name, CurrentSize = size, ProposedSize = Math.Min(MaxSize, Math.Round(size * 1.25m, 2)), Reason = $"Increase 25%: recent PF {pf:0.00}, expectancy {s.RecentExpectancy:N2} per trade and low drawdown." });
        }

        p.Summary = p.Items.Count == 0
            ? "No changes: strategies with enough trades are performing as expected (or the sample is still too small)."
            : $"{p.Items.Count} change(s) suggested by the rules (PF over the last 20 trades and drawdown).";
        return p;
    }
}

/// <summary>MAE y MFE de operaciones en vivo (en dinero, como en el backtest), con los precios entre la entrada y la salida.</summary>
public static class TradeExcursion
{
    public static (decimal Mae, decimal Mfe) Compute(ClosedTrade t, IEnumerable<decimal> prices, decimal pointValue)
    {
        decimal sign = t.IsLong ? 1 : -1, mae = 0, mfe = 0;
        foreach (var p in prices)
        {
            if (p <= 0) continue;
            decimal exc = (p - t.EntryPrice) * sign;
            if (exc > mfe) mfe = exc;
            if (exc < mae) mae = exc;
        }

        // la salida también es un precio por el que pasó
        decimal last = (t.ExitPrice - t.EntryPrice) * sign;
        mfe = Math.Max(mfe, last);
        mae = Math.Min(mae, last);
        decimal pv = pointValue <= 0 ? 1 : pointValue;
        return (mae * t.Quantity * pv, mfe * t.Quantity * pv);
    }
}
