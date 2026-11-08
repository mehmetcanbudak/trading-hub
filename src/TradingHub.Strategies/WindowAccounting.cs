namespace TradingHub.Strategies;

public enum TradeAttribution
{
    Execution,
    WindowClosingFill,
    WindowBoundaryValuation,
}

public enum PerformanceAttributionUnit
{
    ClosingFill,
    WindowClosingFillAndBoundaryValuation,
}

public readonly record struct EquityPathSummary(decimal Net, decimal High, decimal Low, decimal MaxDrawdown)
{
    public EquityPathSummary Append(EquityPathSummary next) => new(
        Net + next.Net,
        Math.Max(High, Net + next.High),
        Math.Min(Low, Net + next.Low),
        Math.Max(Math.Max(MaxDrawdown, next.MaxDrawdown), High - Net - next.Low));

    internal static EquityPathSummary From(IReadOnlyList<EquityPoint> equity, decimal opening)
    {
        decimal net = 0, high = 0, low = 0, drawdown = 0;
        foreach (var point in equity)
        {
            net = point.Equity - opening;
            high = Math.Max(high, net);
            low = Math.Min(low, net);
            drawdown = Math.Max(drawdown, high - net);
        }
        return new(net, high, low, drawdown);
    }
}

public sealed record DailyPerformance(EquityPathSummary Path, int ActualClosingFills, bool Available = true);

internal sealed record AccountingEntry(DateTime Time, string Name, int Bar,
    double? Trend, double? Volatility, double? VolatilityRank, int? Direction);

internal readonly record struct AccountingState(decimal Quantity, decimal Average, decimal Realized,
    decimal OpenCommission, decimal TotalCommission, decimal OpenPnl, decimal Mark,
    decimal Equity, AccountingEntry? Entry, int Bars);

internal sealed record AccountingFill(DateTime AvailableAt, OrderSide Side, decimal BeforeQuantity, decimal Quantity,
    decimal Price, decimal Commission, decimal ClosingQuantity, decimal RealizedChange,
    decimal EntryCommission, decimal ExitCommission, decimal FlatEquity,
    decimal TotalCommission, ClosedTrade? ClosingTrade);

internal readonly record struct AccountingCutoff(DateTime Time, AccountingState State, int EquityCount, int FillCount);

internal sealed record WindowStatistics(PerformanceMetrics Metrics, decimal OpeningEquity, decimal ClosingEquity);

internal sealed class WindowPerformance
{
    public required PerformanceMetrics Metrics { get; init; }
    public required IReadOnlyList<ClosedTrade> Contributions { get; init; }
    public required IReadOnlyList<EquityPoint> Equity { get; init; }
    public required decimal OpeningEquity { get; init; }
    public required decimal ClosingEquity { get; init; }
}

internal sealed class AccountingTrace
{
    private readonly DateTime[] _requested;
    private readonly Dictionary<long, AccountingCutoff> _cutoffs = new();
    private readonly List<EquityPoint> _equity;
    private int _next;

    public AccountingTrace(IReadOnlyList<DateTime> cutoffs, List<EquityPoint> equity)
    {
        _requested = cutoffs.Distinct().OrderBy(t => t).ToArray();
        if (_requested.Length == 0) throw new ArgumentException("At least one accounting cutoff is required.", nameof(cutoffs));
        _equity = equity;
    }

    public List<AccountingFill> Fills { get; } = new();
    public bool Accepting => _next < _requested.Length;
    public long NextCutoffTicks => Accepting ? _requested[_next].Ticks : long.MinValue;
    public string? Error { get; set; }

    public void CaptureThrough(long availableTicks, AccountingState state)
    {
        while (_next < _requested.Length && _requested[_next].Ticks <= availableTicks)
        {
            var time = _requested[_next++];
            _equity.Add(new(time, state.Equity, 0) { AvailableAt = time.AddTicks(-1) });
            _cutoffs.Add(time.Ticks, new(time, state, _equity.Count, Fills.Count));
        }
    }

    public WindowPerformance? Measure(DateTime from, DateTime to, decimal capital, bool crypto)
    {
        if (!TryRange(from, to, out var opening, out var closing)) return null;
        var contributions = Attribute(opening, closing);
        var equity = new WindowEquity(_equity, opening, closing, capital);
        var metrics = PerformanceMetrics.Compute(contributions, equity, capital, crypto, 0, 0);
        metrics.AttributionUnit = PerformanceAttributionUnit.WindowClosingFillAndBoundaryValuation;
        return new WindowPerformance
        {
            Metrics = metrics,
            Contributions = contributions.AsReadOnly(),
            Equity = equity,
            OpeningEquity = opening.State.Equity,
            ClosingEquity = closing.State.Equity,
        };
    }

    public DailyPerformance Day(DateTime from, DateTime to, decimal capital)
    {
        if (!TryRange(from, to, out var opening, out var closing)) return new(default, 0, false);
        int exits = 0;
        for (int i = opening.FillCount; i < closing.FillCount; i++)
            if (Fills[i].ClosingQuantity > 0) exits++;
        var equity = new WindowEquity(_equity, opening, closing, capital);
        return new(EquityPathSummary.From(equity, capital), exits);
    }

    private bool TryRange(DateTime from, DateTime to, out AccountingCutoff opening, out AccountingCutoff closing)
    {
        bool hasOpening = _cutoffs.TryGetValue(from.Ticks, out opening);
        bool hasClosing = _cutoffs.TryGetValue(to.Ticks, out closing);
        return hasOpening && hasClosing && from < to;
    }

    private List<ClosedTrade> Attribute(AccountingCutoff opening, AccountingCutoff closing)
    {
        var rows = new List<ClosedTrade>();
        decimal carryNet = opening.State.OpenPnl - opening.State.OpenCommission;
        decimal carryCommission = opening.State.OpenCommission;
        decimal settledNet = 0, settledCommission = 0;
        for (int i = opening.FillCount; i < closing.FillCount; i++)
        {
            var fill = Fills[i];
            if (fill.ClosingTrade is not { } trade) continue;
            bool settles = fill.ClosingQuantity == Math.Abs(fill.BeforeQuantity);
            decimal credit = settles ? carryNet : carryNet * fill.ClosingQuantity / Math.Abs(fill.BeforeQuantity);
            decimal feeCredit = settles ? carryCommission : carryCommission * fill.ClosingQuantity / Math.Abs(fill.BeforeQuantity);
            carryNet -= credit;
            carryCommission -= feeCredit;

            // A flat settlement consumes the exact remaining carry and fee balances, including decimal allocation residuals.
            decimal net = settles ? fill.FlatEquity - opening.State.Equity - settledNet : fill.RealizedChange - credit;
            decimal openingFee = fill.Commission - fill.ExitCommission;
            decimal commission = settles
                ? fill.TotalCommission - opening.State.TotalCommission - settledCommission - openingFee
                : fill.EntryCommission - feeCredit + fill.ExitCommission;
            rows.Add(trade with
            {
                Number = rows.Count + 1,
                EntryTime = trade.EntryTime < opening.Time ? opening.Time : trade.EntryTime,
                Pnl = net + commission,
                Commission = commission,
                Attribution = TradeAttribution.WindowClosingFill,
                AttributionNet = net,
                ActualEntryTime = trade.EntryTime,
                WindowFrom = opening.Time,
                WindowTo = closing.Time,
                OpeningMark = opening.State.Mark,
                ClosingMark = closing.State.Mark,
                CarryNetCredit = credit,
                CarryCommissionCredit = feeCredit,
                Mae = 0,
                Mfe = 0,
            });
            settledNet += net;
            settledCommission += commission;
        }

        if (closing.State.Quantity != 0)
        {
            var state = closing.State;
            var entry = state.Entry!;
            decimal net = state.Equity - opening.State.Equity - settledNet;
            decimal commission = state.TotalCommission - opening.State.TotalCommission - settledCommission;
            rows.Add(new ClosedTrade(rows.Count + 1, state.Quantity > 0,
                entry.Time < opening.Time ? opening.Time : entry.Time, state.Average,
                closing.Time, state.Mark, Math.Abs(state.Quantity), net + commission, commission,
                0, 0, entry.Name, "Window boundary valuation", state.Bars - entry.Bar)
            {
                Attribution = TradeAttribution.WindowBoundaryValuation,
                AttributionNet = net,
                ActualEntryTime = entry.Time,
                WindowFrom = opening.Time,
                WindowTo = closing.Time,
                OpeningMark = opening.State.Mark,
                ClosingMark = state.Mark,
                CarryNetCredit = carryNet,
                CarryCommissionCredit = carryCommission,
                EntryTrend = entry.Trend,
                EntryVolatility = entry.Volatility,
                EntryVolatilityRank = entry.VolatilityRank,
                EntryMarketDirection = entry.Direction,
            });
        }
        return rows;
    }

    private sealed class WindowEquity(List<EquityPoint> source, AccountingCutoff opening,
        AccountingCutoff closing, decimal capital) : IReadOnlyList<EquityPoint>
    {
        public int Count => closing.EquityCount - opening.EquityCount + 1;

        public EquityPoint this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                if (index == 0) return new(opening.Time, capital, 0) { AvailableAt = opening.Time };
                var point = source[opening.EquityCount + index - 1];
                return point with { Equity = capital + (point.Equity - opening.State.Equity), Drawdown = 0 };
            }
        }

        public IEnumerator<EquityPoint> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal readonly record struct AccountingInput(int Index, long AvailableTicks, Exception? TimingError = null);

internal static class AccountingInputs
{
    public static IEnumerable<AccountingInput> Enumerate(TickData data, CancellationToken ct)
    {
        PriorityQueue<AccountingInput, (long Available, int Index)>? pending = null;
        for (int i = data.Start; i < data.End; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            long location = data.TimeTicks(i);
            while (pending != null && pending.TryPeek(out var ready, out _) && ready.AvailableTicks <= location)
                yield return pending.Dequeue();

            var provenance = data.Provenance(i);
            long available = location;
            Exception? error = null;
            try
            {
                if (provenance.HasKnownAvailability)
                    available = provenance.AvailabilityTime(new DateTime(location, DateTimeKind.Utc)).Ticks;
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException)
            {
                error = ex;
            }
            var input = new AccountingInput(i, available, error);
            // Event location is a lower bound. A malformed future window cannot invalidate an already completed prefix.
            if (available <= location) yield return input;
            else
            {
                pending ??= new();
                pending.Enqueue(input, (available, i));
            }
        }
        while (pending is { Count: > 0 }) yield return pending.Dequeue();
    }
}
