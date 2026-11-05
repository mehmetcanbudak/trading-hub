using System.Globalization;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingHub.Common;
using TradingHub.Trading;
using TradingHub.Data;

[assembly: InternalsVisibleTo("TradingHub.Tests")]
[assembly: InternalsVisibleTo("TradingHub.LedgerCrashFixture")]

namespace TradingHub.Strategies;

public enum LiveFillKind { LegacyEstimate, QuantityObservation, Execution }
public enum LiveFinancialQuality { Authoritative, Estimated, Pending }

/// <summary>Append-only ledger event. Old six-field rows remain estimates. Quantity observations carry no price
/// or fee claim; execution rows carry actual facts and their scoped identity. Unknown fees remain unknown.</summary>
public sealed record LiveFill(DateTime Time, bool Buy, decimal Quantity, decimal Price, string? Symbol = null, decimal PointValue = 0)
{
    public LiveFillKind Kind { get; init; } = LiveFillKind.LegacyEstimate;
    public string? OrderId { get; init; }
    public decimal CumulativeQuantity { get; init; }
    public ExecutionIdentity? ExecutionId { get; init; }
    public ExecutionFee[]? Fees { get; init; }
    public bool FeesComplete { get; init; }
    public string? Currency { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContextId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OwnerGeneration { get; init; }

    public bool Equals(LiveFill? other) =>
        ReferenceEquals(this, other) || other is not null &&
        Time == other.Time && Buy == other.Buy && Quantity == other.Quantity && Price == other.Price &&
        Symbol == other.Symbol && PointValue == other.PointValue && Kind == other.Kind && OrderId == other.OrderId &&
        CumulativeQuantity == other.CumulativeQuantity && ExecutionId == other.ExecutionId &&
        FeesComplete == other.FeesComplete && Currency == other.Currency && ContextId == other.ContextId &&
        OwnerId == other.OwnerId && OwnerGeneration == other.OwnerGeneration &&
        (Fees ?? Array.Empty<ExecutionFee>()).AsSpan().SequenceEqual(other.Fees ?? Array.Empty<ExecutionFee>());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Time);
        hash.Add(Buy);
        hash.Add(Quantity);
        hash.Add(Price);
        hash.Add(Symbol);
        hash.Add(PointValue);
        hash.Add(Kind);
        hash.Add(OrderId);
        hash.Add(CumulativeQuantity);
        hash.Add(ExecutionId);
        hash.Add(FeesComplete);
        hash.Add(Currency);
        hash.Add(ContextId);
        hash.Add(OwnerId);
        hash.Add(OwnerGeneration);
        foreach (var fee in Fees ?? Array.Empty<ExecutionFee>()) hash.Add(fee);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Contabilidad propia de una estrategia en vivo: sus llenados, su posición (aunque otras estrategias
/// operen el mismo instrumento en la misma cuenta), resultado realizado, operaciones cerradas y curva.
/// </summary>
public sealed class LiveLedger
{
    private DateTime _openTime;
    private int _number;
    private decimal _financialPosition, _financialAverage, _openCommission;
    private decimal _financialPointValue;
    private decimal _lastKnownOpenPnL;
    private DateTime _lastFinancialTime;
    private string? _financialSymbol;
    private int _pendingOrders, _estimatedRows;
    private bool _openFeesPending;
    private readonly List<DateTime> _pendingFeeTimes = new();
    private readonly List<LiveFill> _priced = new();
    private readonly List<(DateTime Time, decimal Net)> _realizedChanges = new();
    private readonly Dictionary<ExecutionIdentity, int> _executionIndex = new();
    private readonly Dictionary<string, OrderAccounting> _orders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _contextPositions = new(StringComparer.Ordinal);
    private sealed class OrderAccounting
    {
        public decimal Observed, Executed;
        public decimal ObservationCorrection;
    }

    public LiveLedger(string key, decimal pointValue)
    {
        Key = key;
        PointValue = pointValue <= 0 ? 1 : pointValue;
    }

    public string Key { get; }

    /// <summary>Valor del punto del instrumento de la estrategia (el de los llenados que no traen el suyo).</summary>
    public decimal PointValue { get; }
    /// <summary>Effective priced executions/legacy estimates, not the quantity-only journal events.</summary>
    public List<LiveFill> Fills => _priced;

    /// <summary>Posición en unidades del contrato en el que está abierta (<see cref="OpenSymbol"/>).</summary>
    public decimal Position { get; private set; }

    /// <summary>Effective exposure owned by this account context. Unambiguous context-free legacy
    /// history stays with its sole context; another scoped context's units are never borrowed.</summary>
    public decimal PositionInContext(string contextId)
    {
        if (!_contextPositions.TryGetValue(contextId, out var position))
            return _contextPositions.Count == 0 ? Position : 0;
        return _contextPositions.Count == 1 ? Position : position;
    }
    public decimal AveragePrice { get; private set; }
    public string? OpenContextId { get; private set; }
    public bool OpenContextConflict { get; private set; }
    /// <summary>Best-available numeric subtotal/legacy estimate, retained for existing strategy code.
    /// NetRealized and FinancialQuality distinguish complete, pending and estimated economics.</summary>
    public decimal Realized { get; private set; }
    public decimal GrossRealized { get; private set; }
    public long Revision { get; private set; }
    public bool HasPendingPrices => _pendingOrders > 0;
    public bool HasPendingFinancials => OpenContextConflict || HasPendingPrices || _pendingFeeTimes.Count > 0;
    public LiveFinancialQuality FinancialQuality => HasPendingFinancials ? LiveFinancialQuality.Pending
        : _estimatedRows > 0 ? LiveFinancialQuality.Estimated : LiveFinancialQuality.Authoritative;
    /// <summary>Complete net, or null while ownership context, quantity, price, fee or currency is unresolved.
    /// Estimated legacy results remain distinguishable through FinancialQuality.</summary>
    public decimal? NetRealized => HasPendingFinancials ? null : Realized;
    public decimal? AuthoritativeNet => FinancialQuality == LiveFinancialQuality.Authoritative ? Realized : null;
    public decimal? AuthoritativeGross => !OpenContextConflict && !HasPendingPrices && _estimatedRows == 0 ? GrossRealized : null;
    public List<ClosedTrade> Trades { get; } = new();
    public IReadOnlyList<(DateTime Time, decimal Net)> RealizedChanges => _realizedChanges;

    /// <summary>Completeness required by a session-local loss limit. Old closed fee uncertainty remains in global
    /// quality, but does not affect a later flat session. Uncertainty in the open position episode carries forward.
    /// Callers cache this query by ledger revision and configured session date, not on every market update.</summary>
    public bool HasPendingFinancialsForSession(SessionSettings session, DateOnly day)
    {
        if (OpenContextConflict || HasPendingPrices || _openFeesPending) return true;
        if (day == default) return HasPendingFinancials;
        foreach (var time in _pendingFeeTimes)
            if (session.SessionDate(time) == day) return true;
        return false;
    }

    /// <summary>Contrato de la posición abierta (null = el de la estrategia, o plana).</summary>
    public string? OpenSymbol { get; private set; }

    /// <summary>Valor del punto de la posición abierta.</summary>
    public decimal OpenPointValue { get; private set; }

    /// <summary>
    /// Unreadable lines skipped when it was loaded (for example one cut off by a crash). The fills after them are kept and
    /// the file is never rewritten.
    /// </summary>
    public int SkippedLines { get; internal set; }

    public decimal? KnownOpenPnL(decimal last) => OpenContextConflict ? null : Position == 0 ? 0
        : last <= 0 || HasPendingPrices ? null : (last - AveragePrice) * Position * (OpenPointValue > 0 ? OpenPointValue : PointValue);

    /// <summary>Best-available/last-known numeric estimate for the existing decimal strategy interface.</summary>
    public decimal OpenPnL(decimal last)
    {
        if (KnownOpenPnL(last) is { } known) _lastKnownOpenPnL = known;
        return _lastKnownOpenPnL;
    }

    public void Apply(LiveFill f)
    {
        int index = -1;
        if (f.Kind == LiveFillKind.Execution)
        {
            if (f.ExecutionId == null || f.OrderId == null || f.Price <= 0)
                throw new IOException("An execution ledger event has no execution identity, order ownership or actual price.");
            if (_executionIndex.TryGetValue(f.ExecutionId, out int found))
            {
                index = found;
                if (_priced[index].Equals(f)) return;
                if (_priced[index].OrderId != f.OrderId)
                    throw new IOException("An execution identity cannot move to another ledger order owner.");
            }
        }
        if (f.Kind != LiveFillKind.LegacyEstimate && string.IsNullOrEmpty(f.OrderId))
            throw new IOException("A quantity or execution ledger event has no order ownership.");

        decimal delta = f.Quantity;
        if (f.Kind != LiveFillKind.LegacyEstimate)
        {
            if (!_orders.TryGetValue(f.OrderId!, out var order)) _orders.Add(f.OrderId!, order = new OrderAccounting());
            decimal previous = Math.Max(order.Observed, order.Executed);
            bool wasPending = order.Observed > order.Executed;
            if (f.Kind == LiveFillKind.QuantityObservation)
            {
                // Broker cumulative counters can retain superseded execution units. Carry the
                // correction into every later observation, including genuine new raw-counter advances.
                order.Observed = Math.Max(order.Observed, Math.Max(0, f.CumulativeQuantity + order.ObservationCorrection));
            }
            else
            {
                decimal oldQuantity = index >= 0 ? _priced[index].Quantity : 0;
                decimal correction = f.Quantity - oldQuantity;
                order.Executed += f.Quantity - oldQuantity;
                if (index >= 0 && f.Quantity != oldQuantity)
                {
                    order.ObservationCorrection += correction;
                    // A correction replaces only this execution's units. Preserve the remainder of the
                    // cumulative observation: those units still exist even while their prices are unknown.
                    order.Observed = Math.Max(0, order.Observed + correction);
                }
            }
            delta = Math.Max(order.Observed, order.Executed) - previous;
            _pendingOrders += (order.Observed > order.Executed ? 1 : 0) - (wasPending ? 1 : 0);
        }

        Revision++;
        decimal before = Position;
        Position += f.Buy ? delta : -delta;
        if (Position == 0)
        {
            OpenSymbol = null; OpenPointValue = 0;
            OpenContextId = null; OpenContextConflict = false;
        }
        else if (before == 0 || Math.Sign(before) != Math.Sign(Position))
        {
            OpenSymbol = f.Symbol;
            OpenPointValue = f.PointValue > 0 ? f.PointValue : PointValue;
        }
        UpdateOpenContext(f.ContextId ?? f.ExecutionId?.Scope.ContextId, f.Buy ? delta : -delta);
        if (f.Kind != LiveFillKind.QuantityObservation)
        {
            bool rebuild = index >= 0 || f.Time < _lastFinancialTime;
            if (index >= 0) _priced[index] = f;
            else
            {
                if (f.ExecutionId != null) _executionIndex.Add(f.ExecutionId, _priced.Count);
                _priced.Add(f);
            }
            if (rebuild) RebuildFinancials();
            else ApplyFinancial(f);
        }
        AveragePrice = Position == 0 ? 0 : HasPendingPrices ? AveragePrice : _financialAverage;
        if (Position != 0 && !HasPendingPrices)
        {
            OpenSymbol = _financialSymbol;
            OpenPointValue = _financialPointValue;
        }
    }

    private void UpdateOpenContext(string? context, decimal delta)
    {
        if (context != null && delta != 0)
        {
            _contextPositions.TryGetValue(context, out decimal previous);
            decimal current = previous + delta;
            if (current == 0) _contextPositions.Remove(context);
            else _contextPositions[context] = current;
        }
        // Only contexts with effective exposure can conflict. Closed-context corrections may temporarily
        // reopen that context; correcting its other leg must also be able to clear the conflict.
        OpenContextId = null;
        OpenContextConflict = _contextPositions.Count > 1;
        if (Position != 0 && _contextPositions.Count == 1)
            foreach (var position in _contextPositions)
            {
                OpenContextId = position.Key;
                break;
            }
    }


    private void RebuildFinancials()
    {
        _financialPosition = _financialAverage = _financialPointValue = _openCommission = Realized = GrossRealized = 0;
        _number = _estimatedRows = 0;
        _openFeesPending = false;
        _pendingFeeTimes.Clear();
        _lastFinancialTime = default;
        _financialSymbol = null;
        Trades.Clear();
        _realizedChanges.Clear();
        foreach (var fill in _priced.OrderBy(f => f.Time)) ApplyFinancial(fill);
    }

    private void ApplyFinancial(LiveFill f)
    {
        if (f.Time > _lastFinancialTime) _lastFinancialTime = f.Time;
        bool estimated = f.Kind == LiveFillKind.LegacyEstimate;
        if (estimated) _estimatedRows++;
        bool feesKnown = estimated || f.FeesComplete;
        decimal commission = 0;
        if (!estimated && f.Fees != null)
            foreach (var fee in f.Fees)
            {
                if (fee.Amount != 0 && (f.Currency == null || fee.Currency != f.Currency)) feesKnown = false;
                else commission += fee.Amount;
            }
        if (!feesKnown) _pendingFeeTimes.Add(f.Time);
        decimal pv = f.PointValue > 0 ? f.PointValue : PointValue;
        decimal signed = f.Buy ? f.Quantity : -f.Quantity, before = _financialPosition;
        Realized -= commission;
        if (commission != 0) _realizedChanges.Add((f.Time, -commission));
        if (f.Quantity == 0)
        {
            // A busted execution can retain a real fee or unresolved fee state, but has no units to
            // average, close or allocate commission across. Keep the fee fact without a phantom trade.
            if (before != 0) _openFeesPending |= !feesKnown;
            return;
        }
        if (before == 0 || Math.Sign(before) == Math.Sign(signed))
        {
            if (before == 0) { _openTime = f.Time; _financialPointValue = pv; _financialSymbol = f.Symbol; }
            _openFeesPending = before == 0 ? !feesKnown : _openFeesPending || !feesKnown;
            _financialAverage = (_financialAverage * Math.Abs(before) + f.Price * f.Quantity) / (Math.Abs(before) + f.Quantity);
            _financialPosition += signed;
            _openCommission += commission;
            return;
        }
        decimal closing = Math.Min(Math.Abs(before), f.Quantity);
        decimal pnl = (f.Price - _financialAverage) * closing * Math.Sign(before) * _financialPointValue;
        decimal entryCommission = _openCommission * closing / Math.Abs(before);
        decimal exitCommission = commission * closing / f.Quantity;
        _openCommission -= entryCommission;
        Realized += pnl;
        if (pnl != 0) _realizedChanges.Add((f.Time, pnl));
        GrossRealized += pnl;
        Trades.Add(new ClosedTrade(++_number, before > 0, _openTime, _financialAverage, f.Time, f.Price, closing,
            pnl, entryCommission + exitCommission, 0, 0, "", "", 0));
        _financialPosition += signed;
        if (_financialPosition == 0)
        {
            _financialAverage = _openCommission = 0;
            _openFeesPending = false;
        }
        else if (Math.Sign(_financialPosition) != Math.Sign(before))
        {
            _financialAverage = f.Price;
            _financialPointValue = pv;
            _financialSymbol = f.Symbol;
            _openTime = f.Time;
            _openCommission = commission - exitCommission;
            _openFeesPending = !feesKnown;
        }
        else _openFeesPending |= !feesKnown;
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
    public decimal? CompleteNet { get; set; }
    public decimal? AvailableOpenPnL { get; set; }
    public LiveFinancialQuality FinancialQuality { get; set; } = LiveFinancialQuality.Estimated;
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

    /// <summary>
    /// Broker identities of its stop loss and take profit (<see cref="LiveStrategyRunner.ProtectionOrderIds"/>): the working
    /// ones while it runs, or the ones left working at the broker when it stopped (for example by a keep-protection stop). A
    /// later start can adopt them (<see cref="LiveStrategyRunner.AdoptProtectionIds"/>) instead of placing new ones. Optional:
    /// older saved files do not have it, and it is not written while there is none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ProtectionOrderIds { get; set; }
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
    public LiveFinancialQuality FinancialQuality { get; set; }
    public decimal? CompleteNet { get; set; }
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

    // Only fills not yet acknowledged are retained here; history is streamed, never copied on append.
    // The sidecar, not this cache, owns the authoritative append offset across process exit.
    private sealed class PendingLedger(Guid generation, bool newHistory = false)
    {
        public Guid Generation { get; set; } = generation;
        public bool NewHistory { get; set; } = newHistory;
        public readonly Queue<LiveFill> Fills = new();
        public AppendIntent? Intent;
    }

    private sealed record AppendIntent(int Version, Guid Id, Guid Generation, long Start, byte[] Bytes, string Hash);

    private static readonly Dictionary<string, PendingLedger> Pending = new();

    // StreamReader may read ahead, so recovery must bound the bytes it can see, not just count parsed lines.
    // The caller owns the underlying file; this view does not copy ledger history.
    private sealed class LedgerPrefixStream(Stream file, long length) : Stream
    {
        private long _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get => Length - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = file.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string Clean(string key) => string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '|' ? '_' : c));
    private static string LedgerPath(string key) => LedgerPath(Root, key);
    private static string LedgerPath(string root, string key) => Path.Combine(root, "llenados", Clean(key) + ".jsonl");

    /// <summary>Archivo de llenados de una estrategia (para saber si cambió).</summary>
    public static string LedgerFile(string key) => LedgerPath(key);

    /// <summary>Captures ownership of the current history generation, using an explicitly bound storage root.</summary>
    public static Guid CaptureLedgerGeneration(string root, string key)
    {
        lock (Gate)
        {
            string path = LedgerPath(root, key);
            using var lease = AcquireLedger(path);
            CompleteDeletion(path);
            return EnsureGeneration(path, out _, static directory => StorageSync.SyncDirectory(directory));
        }
    }

    public enum ExecutionProjectionStatus { Applied, Unchanged, Retired, Error }
    public readonly record struct ExecutionProjectionResult(ExecutionProjectionStatus Status, string? Error = null);

    /// <summary>Appends one scoped execution upsert through the existing WAL. The caller's durable exact-ID
    /// checkpoint can supply the previous value; no lifetime history scan is required. Deleted generations retire receipts.</summary>
    public static ExecutionProjectionResult ProjectExecution(string root, string key, Guid expectedGeneration,
        LiveFill execution, LiveFill? knownProjection = null) =>
        AppendOwnedEventCore(root, key, expectedGeneration, execution, knownProjection, executionOnly: true);

    /// <summary>Generation-bound live quantity/estimate persistence. Retired owners cannot recreate deleted history.</summary>
    public static ExecutionProjectionResult AppendOwnedEvent(string root, string key, Guid expectedGeneration, LiveFill ledgerEvent) =>
        AppendOwnedEventCore(root, key, expectedGeneration, ledgerEvent, null, executionOnly: false);

    private static ExecutionProjectionResult AppendOwnedEventCore(string root, string key, Guid expectedGeneration,
        LiveFill execution, LiveFill? knownProjection, bool executionOnly)
    {
        lock (Gate)
        {
            string path = LedgerPath(root, key);
            bool handedToWal = false;
            try
            {
                using var lease = AcquireLedger(path);
                if (File.Exists(path + ".delete") || expectedGeneration == Guid.Empty || ReadGeneration(path) != expectedGeneration)
                    return new(ExecutionProjectionStatus.Retired);
                if (string.IsNullOrEmpty(execution.ContextId) || string.IsNullOrEmpty(execution.OwnerId) ||
                    execution.OwnerGeneration != expectedGeneration)
                    return new(ExecutionProjectionStatus.Error, "Ledger event requires a durable scoped owner and matching history generation.");
                if (executionOnly && (execution.Kind != LiveFillKind.Execution || execution.ExecutionId?.Scope.ContextId == null ||
                    execution.ContextId != execution.ExecutionId.Scope.ContextId || string.IsNullOrEmpty(execution.OrderId)))
                    return new(ExecutionProjectionStatus.Error, "Execution projection requires matching scoped identity and durable logical order.");
                bool unchanged = execution.Equals(knownProjection);
                handedToWal = true;
                string? error = SavePendingLeased(path, unchanged ? null : execution, OpenLedger,
                    static directory => StorageSync.SyncDirectory(directory));
                return error == null ? new(unchanged ? ExecutionProjectionStatus.Unchanged : ExecutionProjectionStatus.Applied)
                    : new(ExecutionProjectionStatus.Error, error);
            }
            catch (Exception ex)
            {
                if (!handedToWal && !execution.Equals(knownProjection))
                {
                    if (!Pending.TryGetValue(path, out var waiting))
                        Pending[path] = waiting = new PendingLedger(expectedGeneration);
                    if (waiting.Generation == expectedGeneration || waiting.Generation == Guid.Empty)
                    {
                        waiting.Generation = expectedGeneration;
                        waiting.Fills.Enqueue(execution);
                    }
                }
                return new(ExecutionProjectionStatus.Error, $"Could not project ledger '{path}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Deletes this history and pending ledger events. A durable deletion marker prevents an interrupted deletion from
    /// resurrecting a journaled event. I/O failures are reported to the caller; a later operation completes the deletion.
    /// </summary>
    public static void DeleteFills(string key)
    {
        lock (Gate)
        {
            string path = LedgerPath(key);
            using var lease = AcquireLedger(path);
            using (var marker = new FileStream(path + ".delete", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                StorageSync.Flush(marker);
            StorageSync.SyncDirectory(Path.GetDirectoryName(path)!);
            Pending.Remove(path);
            CompleteDeletion(path);
        }
    }
    private static string RunnersPath => Path.Combine(Root, "estrategias.json");
    private static string ProposalsPath => Path.Combine(Root, "propuestas.json");
    private static string PortfoliosPath => Path.Combine(Root, "portafolios.json");
    private static string? _lastRunnersJson;
    private static string? _lastRunnersPath;

    /// <summary>
    /// Appends a ledger event; identical legacy fills remain distinct. Returns null only after the durable intent,
    /// main-file write, disk flush, close and commit cleanup succeed. Failures retain ordered pending events in this process;
    /// a published intent also survives process exit. Recovery replays its byte range; execution identities govern projection.
    /// This is a single-filesystem process-crash guarantee, not power-loss durability or exactly-once broker-event delivery.
    /// </summary>
    public static string? AppendFill(string key, LiveFill f) => AppendFill(key, f, OpenLedger);

    /// <summary>
    /// Durably saves existing pending ledger events and a recovered append intent, without creating an execution.
    /// Uses the same transaction and error contract as <see cref="AppendFill(string, LiveFill)"/>; an empty history is a no-op.
    /// </summary>
    public static string? SavePendingLedgerEvents(string key) => SavePending(key, null, OpenLedger);

    // A per-call internal I/O adapter exercises the real public operation without reflection or global fault switches.
    internal static string? AppendFill(string key, LiveFill f, Func<string, Stream> openLedger, Action<string>? syncDirectory = null) =>
        SavePending(key, f, openLedger, syncDirectory);

    private static string? SavePending(string key, LiveFill? f, Func<string, Stream> openLedger, Action<string>? syncDirectory = null)
    {
        syncDirectory ??= static directory => StorageSync.SyncDirectory(directory);
        lock (Gate)
        {
            string path = LedgerPath(key);
            try
            {
                using var lease = AcquireLedger(path);
                return SavePendingLeased(path, f, openLedger, syncDirectory);
            }
            catch (Exception ex)
            {
                if (f != null)
                {
                    if (!Pending.TryGetValue(path, out var waiting)) Pending[path] = waiting = new PendingLedger(Guid.Empty);
                    waiting.Fills.Enqueue(f);
                }
                return $"Could not persist ledger '{path}': {ex.Message}";
            }
        }
    }

    private static string? SavePendingLeased(string path, LiveFill? f, Func<string, Stream> openLedger, Action<string> syncDirectory)
    {
            PendingLedger? pending = null;
            try
            {
                CompleteDeletion(path);
                if (f == null)
                {
                    Guid existingGeneration = ReadGeneration(path);
                    var waiting = ExistingPending(path, existingGeneration);
                    var recovered = ReadIntent(path, existingGeneration);
                    if ((waiting?.Fills.Count ?? 0) == 0 && recovered == null)
                    {
                        using var history = OpenHistory(path, waiting?.NewHistory == true);
                        ValidateHistory(history, null, existingGeneration, waiting?.NewHistory == true);
                        Pending.Remove(path);
                        return null;
                    }
                }
                Guid generation = EnsureGeneration(path, out bool newHistory, syncDirectory);
                pending = PendingFor(path, generation, newHistory);
                if (f != null) pending.Fills.Enqueue(f);
                var intent = ReadIntent(path, generation);
                ReconcileCommitted(path, pending, intent);
                using (var history = OpenHistory(path, pending.NewHistory))
                    ValidateHistory(history, intent, generation, pending.NewHistory);
                while (intent != null || pending.Fills.Count > 0)
                {
                    using (var file = openLedger(path))
                    {
                        pending.NewHistory = false;
                        if (intent == null)
                        {
                            intent = CreateIntent(file, generation, pending.Fills.Peek());
                            PublishIntent(path, intent, openLedger, syncDirectory, pending);
                        }

                        ValidateAppendRange(file, intent);
                        try
                        {
                            if (file.Length > intent.Start) file.SetLength(intent.Start);
                            file.Position = intent.Start;
                            file.Write(intent.Bytes);
                            FlushLedger(file);
                        }
                        catch
                        {
                            try { file.SetLength(intent.Start); FlushLedger(file); }
                            catch { /* The journal still owns the entire uncommitted tail. */ }
                            throw;
                        }
                    }
                    // Make a newly created history name reachable before removing its recovery authority.
                    syncDirectory(Path.GetDirectoryName(path)!);

                    // A reader takes the same cross-process lease and cannot observe commit cleanup in progress.
                    File.Delete(path + ".wal");
                    syncDirectory(Path.GetDirectoryName(path)!);
                    if (pending.Intent?.Id == intent.Id)
                    {
                        pending.Fills.Dequeue();
                        pending.Intent = null;
                    }
                    intent = null;
                }

                Pending.Remove(path);
                return null;
            }
            catch (Exception ex)
            {
                // Even failure to acquire a lease must retain this observed fill locally for the next append.
                if (pending == null && f != null)
                {
                    if (!Pending.TryGetValue(path, out pending))
                        Pending[path] = pending = new PendingLedger(Guid.Empty);
                    pending.Fills.Enqueue(f);
                }
                return $"Could not persist ledger '{path}': {ex.Message}";
            }
        }

    /// <summary>Uncommitted ledger events, including a durable intent left by an exited process.</summary>
    public static int PendingLedgerEvents(string key) => PendingLedgerEvents(Root, key);

    public static int PendingLedgerEvents(string root, string key)
    {
        lock (Gate)
        {
            string path = LedgerPath(root, key);
            using var lease = AcquireLedger(path);
            CompleteDeletion(path);
            Guid generation = ReadGeneration(path);
            var pending = ExistingPending(path, generation);
            var intent = ReadIntent(path, generation);
            if (pending != null) ReconcileCommitted(path, pending, intent);
            using var file = OpenHistory(path, pending?.NewHistory == true);
            ValidateHistory(file, intent, generation, pending?.NewHistory == true);
            return (pending?.Fills.Count ?? 0) + (intent != null && pending?.Intent?.Id != intent.Id ? 1 : 0);
        }
    }

    /// <summary>
    /// Streams committed JSONL and recovers the journaled event once, followed by local pending events. Malformed
    /// committed lines remain untouched and are counted. Missing history is empty; inaccessible history, a damaged journal,
    /// or a truncated/mismatched authoritative range throws an actionable I/O error instead of claiming a flat position.
    /// </summary>
    public static LiveLedger LoadLedger(string key, decimal pointValue) => LoadLedger(Root, key, pointValue);

    /// <summary>Recovers a ledger from an already bound account storage root without changing global storage.</summary>
    public static LiveLedger LoadLedger(string root, string key, decimal pointValue)
    {
        lock (Gate)
        {
            string path = LedgerPath(root, key);
            try
            {
                using var lease = AcquireLedger(path);
                CompleteDeletion(path);
                Guid generation = ReadGeneration(path);
                var pending = ExistingPending(path, generation);
                var intent = ReadIntent(path, generation);
                if (pending != null) ReconcileCommitted(path, pending, intent);
                var ledger = new LiveLedger(key, pointValue);
                using var file = OpenHistory(path, pending?.NewHistory == true);
                ValidateHistory(file, intent, generation, pending?.NewHistory == true);
                if (file != null)
                {
                    using var reader = new StreamReader(intent != null ? new LedgerPrefixStream(file, intent.Start) : file);
                    while (reader.ReadLine() is { } line)
                    {
                        if (line.Length == 0) continue;
                        LiveFill? fill;
                        try { fill = JsonSerializer.Deserialize<LiveFill>(line); }
                        catch (JsonException) { fill = null; }
                        if (fill != null) ledger.Apply(fill);
                        else ledger.SkippedLines++;
                    }
                }

                if (intent != null) ledger.Apply(IntentFill(intent));
                if (pending != null)
                {
                    bool skipIntent = intent != null && pending.Intent?.Id == intent.Id;
                    foreach (var fill in pending.Fills)
                    {
                        if (skipIntent) { skipIntent = false; continue; }
                        ledger.Apply(fill);
                    }
                }
                return ledger;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new IOException($"Cannot recover ledger '{path}'. Keep the strategy stopped and reconcile its history before restarting: {ex.Message}", ex);
            }
        }
    }

    private static Stream OpenLedger(string path) =>
        new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);

    private sealed class LedgerLease(Mutex mutex, FileStream file) : IDisposable
    {
        public void Dispose()
        {
            try { file.Dispose(); }
            finally { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }

    private static LedgerLease AcquireLedger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string identity = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        string name = "TradingHub.LiveLedger." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var mutex = new Mutex(initiallyOwned: false, name);
        bool owned = false;
        try
        {
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { /* Ownership was acquired; recover the exited writer's journal. */ }
            owned = true;
            // Never delete this file: replacing its inode would admit independent locks through path aliases.
            var file = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new LedgerLease(mutex, file);
        }
        catch
        {
            if (owned) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    private static void FlushLedger(Stream file) => StorageSync.Flush(file);

    private static FileStream? OpenHistory(string path, bool allowUnwrittenDirectory = false)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException) when (allowUnwrittenDirectory && Directory.Exists(path)) { return null; }
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void CompleteDeletion(string path)
    {
        if (!Exists(path + ".delete")) return;
        Pending.Remove(path);
        // A directory is not a ledger; leave it in place rather than deleting unrelated contents.
        if (!Directory.Exists(path)) File.Delete(path);
        File.Delete(path + ".wal");
        File.Delete(path + ".wal.tmp");
        File.Delete(path + ".id");
        File.Delete(path + ".id.tmp");
        StorageSync.SyncDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path + ".delete");
        StorageSync.SyncDirectory(Path.GetDirectoryName(path)!);
    }

    private static Guid ReadGeneration(string path)
    {
        try
        {
            if (Guid.TryParseExact(File.ReadAllText(path + ".id"), "N", out var generation) && generation != Guid.Empty) return generation;
            throw new IOException("The ledger generation is damaged.");
        }
        catch (FileNotFoundException) { return Guid.Empty; }
        catch (DirectoryNotFoundException) { return Guid.Empty; }
    }

    private static Guid EnsureGeneration(string path, out bool created, Action<string> syncDirectory)
    {
        var generation = ReadGeneration(path);
        created = generation == Guid.Empty;
        if (generation != Guid.Empty) return generation;
        generation = Guid.NewGuid();
        // An existing generation must never describe a legitimately absent history, even if namespace sync fails.
        if (!Exists(path))
        {
            using var history = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            StorageSync.Flush(history);
        }
        using (var file = new FileStream(path + ".id.tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(Encoding.ASCII.GetBytes(generation.ToString("N")));
            StorageSync.Flush(file);
        }
        File.Move(path + ".id.tmp", path + ".id");
        syncDirectory(Path.GetDirectoryName(path)!);
        syncDirectory(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!);
        return generation;
    }

    private static PendingLedger? ExistingPending(string path, Guid generation)
    {
        if (!Pending.TryGetValue(path, out var pending)) return null;
        if (pending.Generation == generation || pending.Generation == Guid.Empty) return pending;
        Pending.Remove(path); // Another process explicitly deleted this generation, including its pending events.
        return null;
    }

    private static PendingLedger PendingFor(string path, Guid generation, bool newHistory)
    {
        var pending = ExistingPending(path, generation);
        if (pending != null)
        {
            if (pending.Generation == Guid.Empty) pending.NewHistory = newHistory;
            pending.Generation = generation;
            return pending;
        }
        return Pending[path] = new PendingLedger(generation, newHistory);
    }

    private static AppendIntent CreateIntent(Stream file, Guid generation, LiveFill fill)
    {
        long start = file.Length;
        bool separator = false;
        if (start > 0)
        {
            file.Position = start - 1;
            separator = file.ReadByte() != '\n';
        }
        byte[] bytes = Encoding.UTF8.GetBytes((separator ? "\n" : "") + JsonSerializer.Serialize(fill) + "\n");
        var intent = new AppendIntent(1, Guid.NewGuid(), generation, start, bytes, "");
        return intent with { Hash = IntentHash(intent) };
    }

    private static string IntentHash(AppendIntent intent)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[44];
        BinaryPrimitives.WriteInt32LittleEndian(header, intent.Version);
        intent.Id.TryWriteBytes(header[4..20]);
        intent.Generation.TryWriteBytes(header[20..36]);
        BinaryPrimitives.WriteInt64LittleEndian(header[36..], intent.Start);
        hash.AppendData(header);
        hash.AppendData(intent.Bytes);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void PublishIntent(string path, AppendIntent intent, Func<string, Stream> openLedger, Action<string> syncDirectory, PendingLedger pending)
    {
        using (var journal = openLedger(path + ".wal.tmp"))
        {
            journal.SetLength(0);
            JsonSerializer.Serialize(journal, intent);
            FlushLedger(journal);
        }
        // Same-directory publication: a process exit can leave an ignored temporary, never half a published intent.
        File.Move(path + ".wal.tmp", path + ".wal");
        // Publication owns this queue head even when the following namespace confirmation fails.
        pending.Intent = intent;
        syncDirectory(Path.GetDirectoryName(path)!);
    }

    private static AppendIntent? ReadIntent(string path, Guid generation)
    {
        using var file = OpenHistory(path + ".wal");
        if (file == null) return null;
        AppendIntent? intent;
        try { intent = JsonSerializer.Deserialize<AppendIntent>(file); }
        catch (JsonException ex) { throw new IOException($"The durable append intent '{path}.wal' is unreadable.", ex); }
        if (intent == null || intent.Version != 1 || intent.Id == Guid.Empty || generation == Guid.Empty ||
            intent.Generation != generation || intent.Start < 0 || intent.Bytes == null || intent.Bytes.Length == 0 ||
            intent.Start > long.MaxValue - intent.Bytes.Length || intent.Hash != IntentHash(intent))
            throw new IOException("The durable append intent is damaged or belongs to another ledger generation.");
        _ = IntentFill(intent);
        return intent;
    }

    private static LiveFill IntentFill(AppendIntent intent) =>
        JsonSerializer.Deserialize<LiveFill>(Encoding.UTF8.GetString(intent.Bytes).Trim('\n'))
        ?? throw new IOException("The durable append intent has no fill.");

    private static void ValidateHistory(Stream? file, AppendIntent? intent, Guid generation, bool newHistory)
    {
        if (intent != null)
        {
            ValidateLength(file?.Length ?? 0, intent);
            if (file != null) { ValidateAppendRange(file, intent); file.Position = 0; }
        }
        else if (file == null && generation != Guid.Empty && !newHistory)
            throw new IOException("The ledger history is missing from an existing generation.");
    }

    private static void ValidateLength(long length, AppendIntent intent)
    {
        if (length < intent.Start) throw new IOException("The ledger is shorter than its authoritative committed prefix.");
        if (length > intent.Start + intent.Bytes.Length) throw new IOException("The ledger has unexpected data beyond its durable append range.");
    }

    private static void ValidateAppendRange(Stream file, AppendIntent intent)
    {
        ValidateLength(file.Length, intent);
        file.Position = intent.Start;
        Span<byte> buffer = stackalloc byte[4096];
        int offset = 0;
        long remaining = file.Length - intent.Start;
        while (remaining > 0)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
            file.ReadExactly(buffer[..count]);
            if (!buffer[..count].SequenceEqual(intent.Bytes.AsSpan(offset, count)))
                throw new IOException("The ledger tail does not match its durable append intent.");
            offset += count;
            remaining -= count;
        }
    }

    private static void ReconcileCommitted(string path, PendingLedger pending, AppendIntent? current)
    {
        if (pending.Intent is not { } own || current?.Id == own.Id) return;
        // Another process may have completed our intent while we were not holding the lease. Attribute it by generation
        // and assigned byte offset, not by fill equality: identical legitimate fills must remain distinct.
        using var file = OpenHistory(path) ?? throw new IOException("The ledger disappeared before its pending intent was reconciled.");
        if (file.Length < own.Start + own.Bytes.Length) throw new IOException("A pending intent lost its authoritative history.");
        file.Position = own.Start;
        Span<byte> buffer = stackalloc byte[4096];
        int offset = 0;
        while (offset < own.Bytes.Length)
        {
            int count = Math.Min(buffer.Length, own.Bytes.Length - offset);
            file.ReadExactly(buffer[..count]);
            if (!buffer[..count].SequenceEqual(own.Bytes.AsSpan(offset, count)))
                throw new IOException("The ledger no longer matches its pending intent's assigned byte range.");
            offset += count;
        }
        StorageSync.SyncDirectory(Path.GetDirectoryName(path)!);
        pending.Fills.Dequeue();
        pending.Intent = null;
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
            if (deleteFills) DeleteFills(key);
            try
            {
                var all = File.Exists(RunnersPath) ? JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new() : new();
                all.RemoveAll(r => r.Key == key);
                Directory.CreateDirectory(Root);
                File.WriteAllText(RunnersPath, JsonSerializer.Serialize(all, Json));
                _lastRunnersJson = null;
            }
            catch
            {
                // se reintenta
            }
        }
    }

    /// <summary>
    /// Records <see cref="LiveRunnerInfo.ProtectionOrderIds"/> in the saved state of that strategy without changing anything
    /// else (whether it was running included); a strategy not saved yet is added. A saved file that cannot be read is left
    /// untouched. Returns the error, or null.
    /// </summary>
    public static string? SaveProtectionOrderIds(LiveRunnerInfo info)
    {
        lock (Gate)
        {
            try
            {
                var all = File.Exists(RunnersPath) ? JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new() : new();
                if (all.FirstOrDefault(r => r.Key == info.Key) is { } saved) saved.ProtectionOrderIds = info.ProtectionOrderIds;
                else all.Add(info);
                Directory.CreateDirectory(Root);
                File.WriteAllText(RunnersPath, JsonSerializer.Serialize(all, Json));
                _lastRunnersJson = null;
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
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
            CompleteNet = ledger.NetRealized + info.AvailableOpenPnL,
            FinancialQuality = ledger.FinancialQuality,
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
        foreach (var s in stats.Where(s => s.Info.Running && s.FinancialQuality != LiveFinancialQuality.Pending))
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
