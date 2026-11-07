using System.Text.Json.Serialization;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>Cómo se llenan las órdenes límite en la simulación.</summary>
public enum LimitFillMode
{
    /// <summary>Solo si el precio opera del otro lado del límite (conservador).</summary>
    TradeThrough,

    /// <summary>Apenas el precio toca el límite (optimista).</summary>
    Touch,
}

/// <summary>Configuración de una corrida.</summary>
public sealed class BacktestSettings
{
    public string FeedId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public string Period { get; set; } = "M5";
    public decimal InitialCapital { get; set; } = 10_000m;

    /// <summary>Comisión fija por unidad/contrato y por lado.</summary>
    public decimal CommissionPerUnit { get; set; }

    /// <summary>Comisión porcentual sobre el nominal, por lado (ej. 0.04 = 0,04 %).</summary>
    public decimal CommissionPercent { get; set; } = 0.04m;

    /// <summary>Deslizamiento en ticks de las órdenes a mercado y stops.</summary>
    public int SlippageTicks { get; set; } = 1;

    public LimitFillMode LimitFill { get; set; } = LimitFillMode.TradeThrough;

    /// <summary>Zona horaria de la sesión (null = UTC).</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Hora de inicio de la sesión en esa zona.</summary>
    public TimeSpan SessionStart { get; set; }

    /// <summary>Cerrar la posición abierta al final del backtest.</summary>
    public bool CloseAtEnd { get; set; } = true;

    public BacktestSettings Clone() => (BacktestSettings)MemberwiseClone();
}

/// <summary>Una operación cerrada (entrada + salida).</summary>
public sealed record ClosedTrade(
    int Number,
    bool IsLong,
    DateTime EntryTime,
    decimal EntryPrice,
    DateTime ExitTime,
    decimal ExitPrice,
    decimal Quantity,
    decimal Pnl,
    decimal Commission,
    decimal Mae,
    decimal Mfe,
    string EntryName,
    string ExitName,
    int Bars)
{
    public decimal NetPnl => AttributionNet ?? Pnl - Commission;
    public TradeAttribution Attribution { get; init; }
    public decimal? AttributionNet { get; init; }
    public DateTime? ActualEntryTime { get; init; }
    public DateTime? WindowFrom { get; init; }
    public DateTime? WindowTo { get; init; }
    public decimal? OpeningMark { get; init; }
    public decimal? ClosingMark { get; init; }
    public decimal CarryNetCredit { get; init; }
    public decimal CarryCommissionCredit { get; init; }
    [JsonIgnore] public bool IsActualClosingFill => Attribution != TradeAttribution.WindowBoundaryValuation;
    [JsonIgnore] public bool HasCompleteTradeEvidence => Attribution == TradeAttribution.Execution;

    /// <summary>
    /// Cómo venía el mercado al entrar (últimas 24 horas, con las velas que veía la estrategia): eficiencia de la tendencia
    /// de los cierres por hora (0 = rango puro, 1 = tendencia limpia; una caminata al azar da ~0,2). null si no hay datos.
    /// </summary>
    public double? EntryTrend { get; init; }

    /// <summary>Volatilidad al entrar: desvío de los retornos por hora de las últimas 24 horas, en %.</summary>
    public double? EntryVolatility { get; init; }

    /// <summary>Percentil de esa volatilidad entre la del último mes (0 = la más baja, 100 = la más alta).</summary>
    public double? EntryVolatilityRank { get; init; }

    /// <summary>Hacia dónde se había movido el mercado en las 24 horas previas a la entrada (1 subía, -1 bajaba).</summary>
    public int? EntryMarketDirection { get; init; }
}

/// <summary>Una posición abierta (lado, cantidad, precio promedio y desde cuándo).</summary>
public sealed record OpenPosition(bool IsLong, decimal Quantity, decimal AveragePrice, DateTime EntryTime);

/// <summary>Un punto de la curva de capital.</summary>
public readonly record struct EquityPoint(DateTime Time, decimal Equity, decimal Drawdown)
{
    public DateTime? AvailableAt { get; init; }
}

/// <summary>Métricas de rendimiento.</summary>
public sealed class PerformanceMetrics
{
    public decimal NetProfit { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal GrossLoss { get; set; }
    public decimal Commission { get; set; }
    public decimal ProfitFactor { get; set; }
    /// <summary>Actual closing fills, including partial closes and reversal closing legs, never boundary marks.</summary>
    public int TotalTrades { get; set; }
    public int AttributionSamples { get; set; }
    public PerformanceAttributionUnit AttributionUnit { get; set; }
    public int Winners { get; set; }
    public int Losers { get; set; }
    public decimal WinRate { get; set; }
    public decimal AverageTrade { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    public decimal PayoffRatio { get; set; }
    public decimal LargestWin { get; set; }
    public decimal LargestLoss { get; set; }
    public int MaxConsecutiveWins { get; set; }
    public int MaxConsecutiveLosses { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal MaxDrawdownPercent { get; set; }
    public decimal RecoveryFactor { get; set; }
    public double SharpeRatio { get; set; }
    public double SortinoRatio { get; set; }
    public decimal ReturnPercent { get; set; }
    public decimal AverageBarsInTrade { get; set; }
    public decimal LongNetProfit { get; set; }
    public decimal ShortNetProfit { get; set; }
    public decimal ExposurePercent { get; set; }
    public decimal Expectancy { get; set; }

    /// <summary>Valor de la métrica elegida para optimizar.</summary>
    public double Fitness(FitnessMetric m) => m switch
    {
        FitnessMetric.ProfitFactor => AttributionSamples == 0 && TotalTrades == 0 ? 0 : (double)Math.Min(ProfitFactor, 100),
        FitnessMetric.Sharpe => SharpeRatio,
        FitnessMetric.Sortino => SortinoRatio,
        FitnessMetric.RecoveryFactor => (double)RecoveryFactor,
        FitnessMetric.NetProfitOverDrawdown => MaxDrawdown > 0 ? (double)(NetProfit / MaxDrawdown) : (double)NetProfit,
        FitnessMetric.WinRate => (double)WinRate,
        FitnessMetric.Expectancy => (double)Expectancy,
        _ => (double)NetProfit,
    };

    public static PerformanceMetrics Compute(IReadOnlyList<ClosedTrade> trades, IReadOnlyList<EquityPoint> equity, decimal capital, bool crypto, long barsInMarket, long totalBars)
    {
        var m = new PerformanceMetrics
        {
            TotalTrades = trades.Count(t => t.IsActualClosingFill),
            AttributionSamples = trades.Count,
            AttributionUnit = trades.Any(t => t.Attribution != TradeAttribution.Execution)
                ? PerformanceAttributionUnit.WindowClosingFillAndBoundaryValuation : PerformanceAttributionUnit.ClosingFill,
        };
        int run = 0, maxW = 0, maxL = 0;
        decimal sumBars = 0;
        foreach (var t in trades)
        {
            decimal p = t.NetPnl;
            m.NetProfit += p;
            m.Commission += t.Commission;
            if (t.IsLong) m.LongNetProfit += p;
            else m.ShortNetProfit += p;
            if (t.IsActualClosingFill) sumBars += t.Bars;
            if (p > 0)
            {
                m.Winners++;
                m.GrossProfit += p;
                m.LargestWin = Math.Max(m.LargestWin, p);
                run = run > 0 ? run + 1 : 1;
                maxW = Math.Max(maxW, run);
            }
            else
            {
                m.Losers++;
                m.GrossLoss += -p;
                m.LargestLoss = Math.Min(m.LargestLoss, p);
                run = run < 0 ? run - 1 : -1;
                maxL = Math.Max(maxL, -run);
            }
        }

        m.MaxConsecutiveWins = maxW;
        m.MaxConsecutiveLosses = maxL;
        m.ProfitFactor = m.GrossLoss > 0 ? m.GrossProfit / m.GrossLoss : m.GrossProfit > 0 ? 999 : 0;
        m.WinRate = trades.Count > 0 ? 100m * m.Winners / trades.Count : 0;
        m.AverageTrade = trades.Count > 0 ? m.NetProfit / trades.Count : 0;
        m.AverageWin = m.Winners > 0 ? m.GrossProfit / m.Winners : 0;
        m.AverageLoss = m.Losers > 0 ? -m.GrossLoss / m.Losers : 0;
        m.PayoffRatio = m.AverageLoss != 0 ? m.AverageWin / -m.AverageLoss : 0;
        m.Expectancy = trades.Count > 0 ? m.WinRate / 100 * m.AverageWin + (1 - m.WinRate / 100) * m.AverageLoss : 0;
        m.AverageBarsInTrade = m.TotalTrades > 0 ? sumBars / m.TotalTrades : 0;
        m.ReturnPercent = capital > 0 ? 100 * m.NetProfit / capital : 0;
        m.ExposurePercent = totalBars > 0 ? 100m * barsInMarket / totalBars : 0;

        // drawdown sobre la curva de capital
        decimal peak = capital, maxDd = 0, maxDdPct = 0;
        foreach (var e in equity)
        {
            peak = Math.Max(peak, e.Equity);
            decimal dd = peak - e.Equity;
            if (dd > maxDd) maxDd = dd;
            if (peak > 0 && dd / peak > maxDdPct) maxDdPct = dd / peak;
        }

        m.MaxDrawdown = maxDd;
        m.MaxDrawdownPercent = 100 * maxDdPct;
        m.RecoveryFactor = maxDd > 0 ? m.NetProfit / maxDd : m.NetProfit > 0 ? 999 : 0;

        // Sharpe y Sortino con retornos diarios. Por debajo del capital inicial el retorno se mide sobre el capital: con
        // la cuenta en negativo (o casi en cero) dividir por el saldo da vuelta el signo (ganar parecía perder) o explota
        var daily = new List<double>();
        if (equity.Count > 1)
        {
            DateOnly day = DateOnly.FromDateTime(equity[0].AvailableAt ?? equity[0].Time);
            decimal start = capital, prev = start, last = equity[0].Equity;
            double Return() => m.AttributionUnit == PerformanceAttributionUnit.ClosingFill
                ? Math.Max((double)prev, (double)start) is var basis && basis > 0 ? ((double)last - (double)prev) / basis : 0
                : Math.Max(prev, start) is var exactBasis && exactBasis > 0 ? (double)((last - prev) / exactBasis) : 0;
            foreach (var e in equity)
            {
                var d = DateOnly.FromDateTime(e.AvailableAt ?? e.Time);
                if (d != day)
                {
                    daily.Add(Return());
                    prev = last;
                    day = d;
                }

                last = e.Equity;
            }

            daily.Add(Return());
        }

        if (daily.Count > 1)
        {
            double mean = daily.Average();
            double sd = Math.Sqrt(daily.Sum(r => (r - mean) * (r - mean)) / (daily.Count - 1));
            double downside = Math.Sqrt(daily.Where(r => r < 0).Sum(r => r * r) / daily.Count);
            double ann = Math.Sqrt(crypto ? 365 : 252);
            m.SharpeRatio = sd > 0 ? mean / sd * ann : 0;
            m.SortinoRatio = downside > 0 ? mean / downside * ann : 0;
        }

        return m;
    }
}

public enum FitnessMetric
{
    NetProfit,
    ProfitFactor,
    Sharpe,
    Sortino,
    RecoveryFactor,
    NetProfitOverDrawdown,
    WinRate,
    Expectancy,
}

/// <summary>Resultado de un backtest.</summary>
public sealed class BacktestResult
{
    public string Strategy { get; set; } = "";
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public BacktestSettings Settings { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public List<ClosedTrade> Trades { get; set; } = new();
    public List<EquityPoint> Equity { get; set; } = new();
    public List<string> Log { get; set; } = new();
    public long Ticks { get; set; }
    public int Bars { get; set; }
    public double Seconds { get; set; }
    public string? Error { get; set; }
    public DataQuality DataQuality { get; set; }
    public int ResolutionSeconds { get; set; }
    public string? SourceIdentity { get; set; }
    public long CorrectionRevision { get; set; }
    public bool HasKnownAvailability { get; set; }
    public string? DataWarning { get; set; }

    /// <summary>Posición que quedó abierta al final (solo sin «cerrar al final»; la usa el gráfico para seguirla en vivo).</summary>
    [JsonIgnore] public OpenPosition? OpenPosition { get; set; }

    [JsonIgnore] public bool Ok => Error == null;

    /// <summary>Reduce la curva de capital a como mucho N puntos (para guardar y dibujar).</summary>
    public static List<EquityPoint> Downsample(IReadOnlyList<EquityPoint> points, int max)
    {
        if (points.Count <= max) return points.ToList();
        var res = new List<EquityPoint>(max + 2);
        double step = points.Count / (double)max;
        for (int i = 0; i < max; i++)
        {
            // conserva el peor punto de cada tramo para no esconder drawdowns
            int a = (int)(i * step), b = Math.Min(points.Count, (int)((i + 1) * step));
            var worst = points[a];
            for (int k = a; k < b; k++)
                if (points[k].Drawdown > worst.Drawdown) worst = points[k];
            res.Add(worst);
        }

        res.Add(points[^1]);
        return res;
    }
}
