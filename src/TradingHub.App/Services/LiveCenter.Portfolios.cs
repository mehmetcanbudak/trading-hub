using TradingHub.Strategies;

namespace TradingHub.App.Services;

internal sealed partial class LiveCenter
{
    /// <summary>
    /// Los portafolios: el automático (todas las que corren en vivo sin un portafolio tuyo) y los que armaste. El
    /// automático se crea solo la primera vez que hay estrategias corriendo sin portafolio y guarda su configuración.
    /// </summary>
    public List<LivePortfolioView> Portfolios(List<LiveEntry>? entries = null)
    {
        entries ??= Entries();
        var defs = LiveStore.LoadPortfolios();
        var res = new List<LivePortfolioView>();
        var assigned = new HashSet<string>(defs.Where(d => !d.Auto).SelectMany(d => d.Members));
        var free = entries.Where(e => (e.Running || e.WaitingResume) && !assigned.Contains(e.Key)).ToList();
        var auto = defs.FirstOrDefault(d => d.Auto);
        if (free.Count > 0)
        {
            if (auto == null)
            {
                auto = new LivePortfolioDef { Id = LivePortfolioDef.AutoId, Auto = true, Name = "Automatic · all live strategies" };
                defs.Insert(0, auto);
                LiveStore.SavePortfolios(defs);
            }

            auto.Members = free.Select(e => e.Key).ToList();
            res.Add(new LivePortfolioView { Def = auto, Members = free });
        }

        foreach (var d in defs.Where(d => !d.Auto))
            res.Add(new LivePortfolioView { Def = d, Members = d.Members.Select(k => entries.FirstOrDefault(e => e.Key == k)).OfType<LiveEntry>().ToList() });
        return res;
    }

    /// <summary>Guarda (crea o actualiza) un portafolio. Una estrategia está en un solo portafolio tuyo a la vez.</summary>
    public void SavePortfolio(LivePortfolioDef def)
    {
        var defs = LiveStore.LoadPortfolios();
        if (!def.Auto)
            foreach (var other in defs.Where(d => d.Id != def.Id && !d.Auto))
                other.Members.RemoveAll(def.Members.Contains);
        int i = defs.FindIndex(d => d.Id == def.Id);
        if (i >= 0) defs[i] = def;
        else defs.Add(def);
        LiveStore.SavePortfolios(defs);
        if (def.Auto) return;
        foreach (var key in def.Members) SetPortfolio(key, def.Id);
    }

    /// <summary>Saca un portafolio (sus estrategias siguen corriendo y pasan al automático).</summary>
    public void DeletePortfolio(string id)
    {
        var defs = LiveStore.LoadPortfolios();
        var def = defs.FirstOrDefault(d => d.Id == id);
        if (def == null || def.Auto) return;
        defs.Remove(def);
        LiveStore.SavePortfolios(defs);
        foreach (var key in def.Members) SetPortfolio(key, null);
    }

    /// <summary>Asigna una estrategia a un portafolio (null = ninguno: queda en el automático).</summary>
    public void Assign(string key, string? portfolioId)
    {
        var defs = LiveStore.LoadPortfolios();
        foreach (var d in defs.Where(d => !d.Auto)) d.Members.Remove(key);
        var target = defs.FirstOrDefault(d => d.Id == portfolioId && !d.Auto);
        target?.Members.Add(key);
        LiveStore.SavePortfolios(defs);
        SetPortfolio(key, target?.Id);
    }

    private void SetPortfolio(string key, string? id)
    {
        if (Running(key) is { } r) r.PortfolioId = id;
        LiveStore.UpdateRunner(key, i => i.PortfolioId = id);
        _svc.SaveLiveState();
    }

    /// <summary>Arranca todas las detenidas del portafolio (las bloqueadas no: las maneja quien las lanzó).</summary>
    public async Task<List<string>> StartAllAsync(LivePortfolioView p)
    {
        var errors = new List<string>();
        foreach (var m in p.Members.Where(m => !m.Running && !m.Locked))
        {
            var err = await ResumeAsync(m);
            if (err != null) errors.Add($"{m.Info.Strategy} · {m.Info.Symbol}: {err}");
        }

        return errors;
    }

    /// <summary>Detiene todas las del portafolio (opcionalmente cerrando sus posiciones).</summary>
    public async Task StopAllAsync(LivePortfolioView p, bool close)
    {
        foreach (var m in p.Members.Where(m => m.Running && !m.Locked)) await StopAsync(m, close);
    }

    /// <summary>
    /// Números del portafolio con las operaciones cerradas de cada estrategia: resultado diario de cada una, correlación
    /// entre ellas, curva conjunta y métricas (de cada una y del total), desde que arrancó la primera.
    /// </summary>
    public static PortfolioResult Analyze(LivePortfolioView p)
    {
        if (p.Members.Count == 0) return new PortfolioResult();
        var now = DateTime.UtcNow;
        var from = p.Members.Select(m => m.Ledger.Fills.Count > 0 ? m.Ledger.Fills[0].Time : now).Min();
        if (from >= now) from = now.AddDays(-1);
        var comps = p.Members.Select(m => new PortfolioComponent
        {
            StrategyId = m.Info.StrategyId,
            StrategyName = m.Info.Strategy,
            Label = $"{m.Info.Strategy} · {m.Info.Symbol}",
            Weight = 1,
            Trades = m.Ledger.Trades.ToList(),
            Settings = new BacktestSettings { FeedId = m.Info.FeedId, Symbol = m.Info.Symbol, Period = m.Info.Period, FromUtc = from, ToUtc = now },
        }).ToList();
        decimal capital = p.Def.Capital > 0 ? p.Def.Capital : 25_000m;
        var res = PortfolioAnalyzer.Analyze(comps, capital);

        // curva operación por operación: el drawdown dentro de un día con muchas operaciones también cuenta (la diaria lo esconde)
        bool crypto = comps.All(c => c.Settings.FeedId.StartsWith("binance", StringComparison.Ordinal) || c.Settings.FeedId.StartsWith("bybit", StringComparison.Ordinal) || c.Settings.FeedId == "sim");
        var all = comps.SelectMany(c => c.Trades).OrderBy(t => t.ExitTime).ToList();
        var sharpe = res.Metrics.SharpeRatio;
        var sortino = res.Metrics.SortinoRatio;
        res.Trades = all;
        res.Equity = TradeCurve(all, from, capital);
        res.Metrics = PerformanceMetrics.Compute(all, res.Equity, capital, crypto, 0, 0);
        res.Metrics.SharpeRatio = sharpe;
        res.Metrics.SortinoRatio = sortino;
        res.ComponentMetrics = comps.Select(c =>
        {
            var trades = c.Trades.OrderBy(t => t.ExitTime).ToList();
            return PerformanceMetrics.Compute(trades, TradeCurve(trades, from, capital), capital, crypto, 0, 0);
        }).ToList();
        res.SumOfComponentDrawdowns = res.ComponentMetrics.Sum(m => m.MaxDrawdown);
        return res;
    }

    /// <summary>Curva de capital al cierre de cada operación.</summary>
    private static List<EquityPoint> TradeCurve(IReadOnlyList<ClosedTrade> trades, DateTime from, decimal capital)
    {
        var curve = new List<EquityPoint> { new(trades.Count > 0 && trades[0].ExitTime < from ? trades[0].ExitTime : from, capital, 0) };
        decimal eq = capital, peak = capital;
        foreach (var t in trades)
        {
            eq += t.NetPnl;
            peak = Math.Max(peak, eq);
            curve.Add(new EquityPoint(t.ExitTime, eq, peak - eq));
        }

        return curve;
    }
}
