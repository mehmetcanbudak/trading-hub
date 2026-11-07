using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class WindowAccountingTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Instrument Instrument = new() { Symbol = "ACCOUNTING", FeedId = "test", TickSize = 0.0001m, VolumeStep = 1, PointValue = 1 };

    private sealed class ScheduledStrategy(IReadOnlyDictionary<long, decimal> orders) : Strategy
    {
        [Parameter] public int Variant { get; set; }
        public long? FailAt { get; init; }
        public long? TerminalBarAt { get; init; }
        public bool FailOnStop { get; init; }
        public Action? Initialized { get; init; }
        public Action? Stopped { get; init; }
        public Action? TerminalOrder { get; init; }
        private long _lastId;

        protected override void OnInit() => Initialized?.Invoke();

        protected override void OnTick(Trade trade)
        {
            _lastId = trade.Id;
            if (trade.Id == FailAt) throw new InvalidOperationException("Scheduled input failure");
            if (!orders.TryGetValue(trade.Id, out decimal quantity)) return;
            if (quantity > 0) Buy(quantity);
            else Sell(-quantity);
        }

        protected override void OnBar(Bar bar)
        {
            if (_lastId != TerminalBarAt) return;
            Buy(50);
            TerminalOrder?.Invoke();
        }

        protected override void OnStop()
        {
            Stopped?.Invoke();
            if (FailOnStop) throw new InvalidOperationException("Scheduled terminal failure");
        }
    }

    private static Trade Tick(long id, DateTime time, decimal price, TradeProvenance provenance = default) =>
        new(id, time, price, 1, TradeSide.Buy, provenance);

    private static BacktestSettings Settings(int days = 2, decimal fee = 0, decimal capital = 1000) => new()
    {
        FeedId = "test", Symbol = Instrument.Symbol, FromUtc = Start, ToUtc = Start.AddDays(days),
        Period = "M1", InitialCapital = capital, CommissionPerUnit = fee, CommissionPercent = 0, SlippageTicks = 0,
    };

    private static WalkForwardResult Run(Func<Strategy> factory, IEnumerable<Trade> rows, BacktestSettings? settings = null,
        int minTrades = 0, int outSampleDays = 1, IReadOnlyList<ParameterRange>? ranges = null) =>
        Optimizer.WalkForward(factory, new(), ranges ?? Array.Empty<ParameterRange>(), TickData.FromTrades(Instrument, rows.ToArray()),
            settings ?? Settings(), FitnessMetric.NetProfit, 1, outSampleDays, false, minTrades, null, CancellationToken.None,
            search: SearchMode.Grid, maxParallel: 1);

    private static void Reconciles(WalkForwardResult result, decimal expected)
    {
        Assert.Equal(expected, result.OutSampleMetrics.NetProfit);
        Assert.Equal(expected, result.OutSampleTrades.Sum(t => t.NetPnl));
        Assert.Equal(expected, result.Windows.Where(w => w.OutSampleAvailable).Sum(w => w.OutSample!.NetProfit));
        Assert.Equal(expected, result.OutSampleEquity[^1].Equity - result.OutSampleEquity[0].Equity);
        Assert.Equal(result.OutSampleMetrics.NetProfit, result.OutSampleMetrics.LongNetProfit + result.OutSampleMetrics.ShortNetProfit);
    }

    [Fact]
    public void PartialCloseAllocatesCarriedCreditsAndChargesOnlyActualWindowFees()
    {
        var orders = new Dictionary<long, decimal> { [1] = 2, [4] = 2, [6] = -1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
            Tick(4, Start.AddDays(1).AddHours(10), 120), Tick(5, Start.AddDays(1).AddHours(10).AddSeconds(1), 120),
            Tick(6, Start.AddDays(1).AddHours(11), 130), Tick(7, Start.AddDays(1).AddHours(11).AddSeconds(1), 130),
            Tick(8, Start.AddDays(2).AddSeconds(-1), 125),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows, Settings(fee: 1));
        var window = Assert.Single(result.Windows);
        Assert.Equal(1018m, window.OutSampleOpeningEquity);
        Assert.Equal(1060m, window.OutSampleClosingEquity);
        Assert.Equal(1000m, window.OutSampleStitchedOpeningEquity);
        Assert.Equal(1042m, window.OutSampleStitchedClosingEquity);
        Assert.Equal(3m, window.OutSample!.Commission);
        Assert.Equal(1, window.OutSample!.TotalTrades);
        Assert.Equal(2, window.OutSample!.AttributionSamples);
        Assert.Equal(PerformanceAttributionUnit.WindowClosingFillAndBoundaryValuation, window.OutSample!.AttributionUnit);
        var close = Assert.Single(result.OutSampleTrades, t => t.Attribution == TradeAttribution.WindowClosingFill);
        var mark = Assert.Single(result.OutSampleTrades, t => t.Attribution == TradeAttribution.WindowBoundaryValuation);
        Assert.Equal(13.5m, close.NetPnl);
        Assert.Equal(1.5m, close.Commission);
        Assert.Equal(4.5m, close.CarryNetCredit);
        Assert.Equal(28.5m, mark.NetPnl);
        Assert.Equal(1.5m, mark.Commission);
        Assert.False(mark.IsActualClosingFill);
        Reconciles(result, 42m);

        var ordinary = new BacktestEngine(new ScheduledStrategy(orders), TickData.FromTrades(Instrument, rows), Settings(fee: 1)).Run();
        Assert.True(ordinary.Ok, ordinary.Error);
        Assert.Equal(57m, ordinary.Metrics.NetProfit);
        Assert.Equal(8m, ordinary.Metrics.Commission);
        Assert.Equal(2, ordinary.Metrics.TotalTrades);
        Assert.All(ordinary.Trades, t => Assert.Equal(TradeAttribution.Execution, t.Attribution));
    }

    [Fact]
    public void ReversalSeparatesClosingAndOpeningFeesWithoutInventingAnExitForTheMark()
    {
        var orders = new Dictionary<long, decimal> { [1] = 2, [4] = -3 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
            Tick(4, Start.AddDays(1).AddHours(10), 120), Tick(5, Start.AddDays(1).AddHours(10).AddSeconds(1), 120),
            Tick(6, Start.AddDays(2).AddSeconds(-1), 115),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows, Settings(fee: 1));
        Assert.Equal(1, result.OutSampleMetrics.TotalTrades);
        Assert.Equal(3m, result.OutSampleMetrics.Commission);
        Assert.Equal(18m, result.OutSampleMetrics.LongNetProfit);
        Assert.Equal(4m, result.OutSampleMetrics.ShortNetProfit);
        Assert.Equal(new[] { 2m, 1m }, result.OutSampleTrades.Select(t => t.Commission));
        Reconciles(result, 22m);
    }

    [Fact]
    public void FinalClosingFillConsumesExactFeeAndCarryDivisionResiduals()
    {
        var orders = new Dictionary<long, decimal> { [1] = 1, [4] = 2, [6] = -1, [8] = -2 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
            Tick(4, Start.AddDays(1).AddHours(10), 120), Tick(5, Start.AddDays(1).AddHours(10).AddSeconds(1), 120),
            Tick(6, Start.AddDays(1).AddHours(11), 130), Tick(7, Start.AddDays(1).AddHours(11).AddSeconds(1), 130),
            Tick(8, Start.AddDays(1).AddHours(12), 125), Tick(9, Start.AddDays(1).AddHours(12).AddSeconds(1), 125),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows, Settings(fee: 0.1m));
        Assert.Equal(2, result.OutSampleMetrics.TotalTrades);
        Assert.Equal(0.5m, result.OutSampleMetrics.Commission);
        Assert.Equal(0.5m, result.OutSampleTrades.Sum(t => t.Commission));
        Assert.Equal(9.9m, result.OutSampleTrades.Sum(t => t.CarryNetCredit));
        Assert.Equal(0.1m, result.OutSampleTrades.Sum(t => t.CarryCommissionCredit));
        Assert.All(result.OutSampleTrades, t => Assert.Equal(TradeAttribution.WindowClosingFill, t.Attribution));
        Reconciles(result, 29.5m);
    }

    [Fact]
    public void CarriedEntryFeeIsNotChargedAgainWhenTheActualExitOccurs()
    {
        var orders = new Dictionary<long, decimal> { [1] = 1, [4] = -1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
            Tick(4, Start.AddDays(1).AddHours(10), 115), Tick(5, Start.AddDays(1).AddHours(10).AddSeconds(1), 115),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows, Settings(fee: 1));
        Assert.Equal(1m, result.OutSampleMetrics.Commission);
        Assert.Equal(1, result.OutSampleMetrics.TotalTrades);
        Reconciles(result, 4m);
    }

    [Fact]
    public void FutureScaleInBasisCannotChangeTheEarlierMarkedTrainingProfit()
    {
        var orders = new Dictionary<long, decimal> { [1] = 1, [4] = 1, [6] = -2 };
        WalkForwardWindow Earlier(decimal laterEntry)
        {
            var rows = new[]
            {
                Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
                Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
                Tick(4, Start.AddDays(1).AddHours(10), laterEntry), Tick(5, Start.AddDays(1).AddHours(10).AddSeconds(1), laterEntry),
                Tick(6, Start.AddDays(1).AddHours(11), 210), Tick(7, Start.AddDays(1).AddHours(11).AddSeconds(1), 210),
            };
            return Assert.Single(Run(() => new ScheduledStrategy(orders), rows).Windows);
        }
        Assert.Equal(10m, Earlier(200).InSample!.NetProfit);
        Assert.Equal(10m, Earlier(400).InSample!.NetProfit);
    }

    [Fact]
    public void CutoffExcludesEqualAvailabilityAndLaterKnownBackdatedFacts()
    {
        var orders = new Dictionary<long, decimal> { [1] = 1 };
        WalkForwardWindow Earlier(decimal futurePrice)
        {
            var delayed = new TradeProvenance(DataQuality.Compact, 60, "delayed", ObservedTradeCount: 1, ResolutionOffsetSeconds: 30);
            var rows = new[]
            {
                Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
                Tick(5, Start.AddDays(1).AddSeconds(-30), futurePrice, delayed),
                Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
                Tick(4, Start.AddDays(1), futurePrice),
            };
            var settings = Settings();
            settings.Period = "D1";
            settings.SessionStart = TimeSpan.FromHours(18);
            return Assert.Single(Run(() => new ScheduledStrategy(orders), rows, settings).Windows);
        }
        var rising = Earlier(500);
        var falling = Earlier(50);
        Assert.True(rising.InSampleAvailable && falling.InSampleAvailable);
        Assert.Equal(10m, rising.InSample!.NetProfit);
        Assert.Equal(rising.InSample!.NetProfit, falling.InSample!.NetProfit);
        Assert.True(rising.OutSampleAvailable);
        Assert.True(falling.OutSampleAvailable);
    }

    [Fact]
    public void ExhaustionFreezesPendingCutoffsBeforeFlushForcedCloseAndFailingStop()
    {
        int initializations = 0, stops = 0, terminalOrders = 0;
        var orders = new Dictionary<long, decimal> { [1] = 1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110), Tick(4, Start.AddDays(1).AddHours(20), 115),
        };
        var result = Run(() => new ScheduledStrategy(orders)
        {
            Initialized = () => initializations++, Stopped = () => stops++, TerminalOrder = () => terminalOrders++,
            TerminalBarAt = 4, FailOnStop = true,
        }, rows, Settings(fee: 1));
        Assert.True(result.IsComplete);
        Assert.Equal(1, initializations);
        Assert.Equal(1, stops);
        Assert.Equal(1, terminalOrders);
        Assert.Equal(0, result.OutSampleMetrics.TotalTrades);
        Assert.Equal(0m, result.OutSampleMetrics.Commission);
        Assert.Equal(9m, Assert.Single(result.Windows).InSample!.NetProfit);
        Reconciles(result, 5m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FutureMalformedTimingCannotEraseCompletedWindows(bool unknownAvailability)
    {
        var orders = new Dictionary<long, decimal> { [1] = 1 };
        var malformed = unknownAvailability
            ? new TradeProvenance(DataQuality.Raw, HasKnownAvailability: false)
            : new TradeProvenance(DataQuality.Compact, 60, "misaligned", ObservedTradeCount: 1, ResolutionOffsetSeconds: 30);
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110), Tick(4, Start.AddDays(2).AddSeconds(-1), 120),
            Tick(5, Start.AddDays(2).AddHours(1), 500, malformed),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows, Settings(days: 4));
        Assert.False(result.IsComplete);
        Assert.True(result.Windows[0].InSampleAvailable);
        Assert.True(result.Windows[0].OutSampleAvailable);
        Assert.Equal(10m, result.Windows[0].InSample!.NetProfit);
        Assert.Equal(10m, result.Windows[0].OutSample!.NetProfit);
        Assert.False(result.Windows[1].OutSampleAvailable);
        Reconciles(result, 10m);
    }

    [Fact]
    public void LaterCallbackFailurePreservesCompletedPrefixesAndStopsExactlyOnce()
    {
        int stops = 0;
        var orders = new Dictionary<long, decimal> { [1] = 1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110), Tick(4, Start.AddDays(2).AddSeconds(-1), 120),
            Tick(5, Start.AddDays(2).AddHours(1), 500),
        };
        var result = Run(() => new ScheduledStrategy(orders) { FailAt = 5, Stopped = () => stops++ }, rows, Settings(days: 4));
        Assert.Equal(1, stops);
        Assert.False(result.IsComplete);
        Assert.Equal(10m, result.Windows[0].InSample!.NetProfit);
        Assert.Equal(10m, result.Windows[0].OutSample!.NetProfit);
        Assert.False(result.Windows[1].OutSampleAvailable);
        Reconciles(result, 10m);
    }

    [Fact]
    public void OriginalIntrahourDecimalPeaksAndRetracementsDetermineWindowRisk()
    {
        var orders = new Dictionary<long, decimal> { [1] = 1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddHours(10).AddMinutes(1), 100.0001m),
            Tick(4, Start.AddHours(10).AddMinutes(2), 200.0002m),
            Tick(5, Start.AddHours(10).AddMinutes(3), 150.0001m),
            Tick(6, Start.AddHours(10).AddMinutes(4), 150.0001m),
        };
        var window = Assert.Single(Run(() => new ScheduledStrategy(orders), rows, Settings(capital: 10_000)).Windows);
        Assert.Equal(50.0001m, window.InSample!.MaxDrawdown);
        Assert.Equal(50.0001m, window.InSample!.NetProfit);
        Assert.Equal(1d, window.InSample!.Fitness(FitnessMetric.NetProfitOverDrawdown));
    }

    [Fact]
    public void DailyMoneyRetainsSmallRecoveryReturnsBelowOpeningCapital()
    {
        var orders = new Dictionary<long, decimal> { [1] = 1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 100),
            Tick(4, Start.AddDays(2).AddSeconds(-1), 99.9999m),
            Tick(5, Start.AddDays(3).AddSeconds(-1), 100.0001m),
            Tick(6, Start.AddDays(4).AddSeconds(-1), 100.0002m),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows, Settings(days: 4, capital: 10_000_000_000_000_000m), outSampleDays: 3);
        Assert.Equal(0.0001m, result.OutSampleMetrics.MaxDrawdown);
        Assert.InRange(result.OutSampleMetrics.SharpeRatio, 6.9282032302, 6.9282032304);
        Reconciles(result, 0.0002m);
    }

    [Fact]
    public void BoundaryValuationsCannotSatisfyMinimumTradeEligibility()
    {
        var orders = new Dictionary<long, decimal> { [1] = 1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110), Tick(4, Start.AddDays(2).AddSeconds(-1), 120),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows, minTrades: 1);
        Assert.False(result.IsComplete);
        var unavailable = Assert.Single(result.Windows);
        Assert.False(unavailable.InSampleAvailable);
        Assert.False(unavailable.OutSampleAvailable);
        Assert.Empty(unavailable.BestParameters);
        Assert.Null(unavailable.InSample);
        Assert.Null(unavailable.OutSample);
        Assert.Null(unavailable.Efficiency);
        Assert.Empty(result.OutSampleTrades);
        Assert.Empty(result.OutSampleEquity);
    }

    [Fact]
    public void CpcvUsesExactIntradayPathsInsteadOfRoundedDailyEndpoints()
    {
        var day = new DailyPerformance(new(10.0001m, 100.0002m, 0, 90.0001m), 1);
        var paths = Overfitting.Cpcv(new[] { new[] { 0 } }, new[] { Enumerable.Repeat(day, 20).ToArray() }, 1, 1000m, false, groups: 4);
        Assert.Equal(3, paths.Count);
        Assert.All(paths, path =>
        {
            Assert.True(path.Available, path.UnavailableReason);
            Assert.Equal(200.002m, path.Net);
            Assert.Equal(90.0001m, path.MaxDrawdown);
            Assert.Equal(20, path.ActualClosingFills);
        });
    }

    [Fact]
    public void CpcvIncludesBelowOpeningRecoveryPathsInExactDrawdown()
    {
        var cycle = new[]
        {
            new DailyPerformance(new(-100m, 0, -100m, 100m), 1),
            new DailyPerformance(new(90m, 90m, 0, 0), 1),
            new DailyPerformance(new(-50m, 0, -50m, 50m), 1),
            new DailyPerformance(new(60m, 60m, 0, 0), 1),
        };
        var daily = Enumerable.Range(0, 20).Select(i => cycle[i % 4]).ToArray();
        var paths = Overfitting.Cpcv(new[] { new[] { 0 } }, new[] { daily }, 1, 1000m, false, groups: 4);
        Assert.All(paths, path => { Assert.True(path.Available); Assert.Equal(0m, path.Net); Assert.Equal(100m, path.MaxDrawdown); });
    }

    [Fact]
    public void CpcvDoesNotSubstituteConfigurationZeroWhenNoCandidateIsEligible()
    {
        var day = new DailyPerformance(new(10m, 10m, 0, 0), 0);
        var paths = Overfitting.Cpcv(new[] { new[] { 0 } }, new[] { Enumerable.Repeat(day, 20).ToArray() }, 1, 1000m, false, groups: 4);
        Assert.NotEmpty(paths);
        Assert.All(paths, path =>
        {
            Assert.False(path.Available);
            Assert.NotNull(path.UnavailableReason);
            Assert.Equal(0, path.Days);
            Assert.Null(path.Net);
            Assert.Null(path.Sharpe);
            Assert.Null(path.MaxDrawdown);
            Assert.Null(path.ActualClosingFills);
        });
    }

    [Fact]
    public void EmbargoAndDailyExitCountsUseActualHoldingTimesNotBoundaryFragments()
    {
        var orders = new Dictionary<long, decimal> { [1] = 1, [4] = -1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
            Tick(4, Start.AddDays(3).AddHours(10).AddMinutes(1), 120),
            Tick(5, Start.AddDays(3).AddHours(10).AddMinutes(1).AddSeconds(1), 120),
            Tick(6, Start.AddDays(20).AddSeconds(-1), 120),
        };
        var ranges = new[] { new ParameterRange { Name = nameof(ScheduledStrategy.Variant), Min = 0, Max = 3, Step = 1, IsInteger = true } };
        var result = Run(() => new ScheduledStrategy(orders), rows, Settings(days: 20), ranges: ranges);
        Assert.Equal(4, result.EmbargoDays);
        Assert.Equal(1, result.OutSampleMetrics.TotalTrades);
        Assert.All(result.CpcvPaths.Where(p => p.Available), path => Assert.Equal(1, path.ActualClosingFills));
        Assert.Contains(result.CpcvPaths, path => path.Available && path.ActualClosingFills == 1);
    }

    [Fact]
    public void MarkedContributionsDoNotBecomeIndependentConfidenceOrMonteCarloSamples()
    {
        var orders = new Dictionary<long, decimal> { [1] = 1, [4] = -1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
            Tick(4, Start.AddDays(1).AddHours(10), 115), Tick(5, Start.AddDays(1).AddHours(10).AddSeconds(1), 115),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows);
        Assert.Equal(1, result.OutSampleMetrics.TotalTrades);
        Assert.Equal(0, Robustness.TradeSharpe(result.OutSampleTrades).Count);
        Assert.Equal(0, Robustness.EdgeDecay(result.OutSampleTrades).Count);
        Assert.Empty(Robustness.Regimes(result.OutSampleTrades));
        var monteCarlo = Optimizer.MonteCarlo(result.OutSampleTrades, 1000m);
        Assert.False(monteCarlo.Available);
        Assert.Equal(0, monteCarlo.Runs);
        Assert.Null(monteCarlo.NetProfit);
        Assert.Null(monteCarlo.DrawdownP95);
        Assert.Null(monteCarlo.RuinProbabilityPercent);
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = result, MinTrades = 1 });
        Assert.False(gate.TradeEvidenceAvailable);
        Assert.NotNull(gate.TradeEvidenceUnavailableReason);
        Assert.Null(gate.TradeSharpe);
        Assert.Null(gate.Deflated);
        Assert.Null(gate.DecayEarly);
        Assert.Null(gate.DecayLate);
        Assert.Null(gate.DecayT);
        Assert.Contains(gate.Checks, check => check.Name == "Deflated Sharpe" && check.Status == GateStatus.Warn);
        Assert.False(gate.NeedsMoreSample);
    }

    [Fact]
    public void DelayedFactsRetainEqualAvailabilityOrderAndNextEventFillRules()
    {
        var delayed = new TradeProvenance(DataQuality.Compact, 60, "delayed", ObservedTradeCount: 1, ResolutionOffsetSeconds: 30);
        var orders = new Dictionary<long, decimal> { [1] = 1, [4] = -1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(4, Start.AddDays(1).AddSeconds(-30), 120, delayed),
            Tick(5, Start.AddDays(1).AddSeconds(-15), 130, delayed),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
            Tick(6, Start.AddDays(1).AddSeconds(30), 140),
            Tick(7, Start.AddDays(1).AddSeconds(31), 150),
        };
        var settings = Settings();
        settings.Period = "D1";
        settings.SessionStart = TimeSpan.FromHours(18);
        var result = Run(() => new ScheduledStrategy(orders), rows, settings);
        Assert.Equal(10m, Assert.Single(result.Windows).InSample!.NetProfit);
        var close = Assert.Single(result.OutSampleTrades);
        Assert.Equal(TradeAttribution.WindowClosingFill, close.Attribution);
        Assert.Equal(Start.AddDays(1).AddSeconds(30), close.ExitTime);
        Assert.Equal(140m, close.ExitPrice);
        Reconciles(result, 30m);
    }

    [Fact]
    public void BoundaryMarkIncludesAnEntryFeePaidAfterTheLastBarValuation()
    {
        var orders = new Dictionary<long, decimal> { [2] = 1 };
        var rows = new[]
        {
            Tick(1, Start.AddDays(1).AddSeconds(-1), 100),
            Tick(2, Start.AddDays(1).AddHours(10), 100),
            Tick(3, Start.AddDays(1).AddHours(10).AddSeconds(1), 100),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows, Settings(fee: 1));
        Assert.Equal(0, result.OutSampleMetrics.TotalTrades);
        Assert.Equal(1, result.OutSampleMetrics.AttributionSamples);
        Assert.Equal(1m, result.OutSampleMetrics.Commission);
        Reconciles(result, -1m);
    }

    [Fact]
    public void InvalidFarFutureSourceWindowCannotPrevalidateAwayEarlierAccounting()
    {
        var overflow = new TradeProvenance(DataQuality.Compact, 60, "overflow", ObservedTradeCount: 1);
        var orders = new Dictionary<long, decimal> { [1] = 1 };
        var rows = new[]
        {
            Tick(1, Start.AddHours(10), 100), Tick(2, Start.AddHours(10).AddSeconds(1), 100),
            Tick(3, Start.AddDays(1).AddSeconds(-1), 110),
            Tick(4, Start.AddDays(2).AddSeconds(-1), 120),
            Tick(5, DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), 900, overflow),
        };
        var result = Run(() => new ScheduledStrategy(orders), rows, Settings(days: 4));
        Assert.True(result.IsComplete);
        Assert.Equal(10m, result.Windows[0].InSample!.NetProfit);
        Assert.Equal(10m, result.Windows[0].OutSample!.NetProfit);
        Reconciles(result, 10m);
    }

}
