using System.Diagnostics;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using TradingHub.Strategies;
using Q = QuanTAlib;

namespace TradingHub.Tests;

public class QuanTAlibTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Instrument Instrument = new() { Symbol = "QUANT-TEST", FeedId = "test", TickSize = 1, VolumeStep = 1, PointValue = 1 };
    public static IEnumerable<object[]> Types =>
    [
        [QtMaType.Sma, typeof(Q.Sma)], [QtMaType.Ema, typeof(Q.Ema)], [QtMaType.Wma, typeof(Q.Wma)],
        [QtMaType.Hma, typeof(Q.Hma)], [QtMaType.Alma, typeof(Q.Alma)], [QtMaType.Jma, typeof(Q.Jma)],
        [QtMaType.Kama, typeof(Q.Kama)], [QtMaType.T3, typeof(Q.T3)], [QtMaType.Zlema, typeof(Q.Zlema)],
        [QtMaType.Dema, typeof(Q.Dema)], [QtMaType.Tema, typeof(Q.Tema)], [QtMaType.Vidya, typeof(Q.Vidya)],
        [QtMaType.Ssf2, typeof(Q.Ssf2)],
    ];
    private static ChartModel Model(int count = 450, int minutes = 1)
    {
        var data = new InstrumentData(Instrument, new SimulatedFeed());
        for (int i = 0; i < count; i++)
        {
            data.Trades.Add(new Trade(i * 2 + 1, Start.AddMinutes(i * minutes), 100 + i % 19, 2, TradeSide.Buy));
            data.Trades.Add(new Trade(i * 2 + 2, Start.AddMinutes(i * minutes).AddSeconds(20), 97 + i % 17, 1, TradeSide.Sell));
        }
        return new ChartModel(data, ChartPeriod.M1, new ChartSettings());
    }
    private static OptionalValueDataSeries Line(Indicator indicator, int index = 0) => Assert.IsType<OptionalValueDataSeries>(indicator.DataSeries[index]);

    [Theory, MemberData(nameof(Types))]
    public void Every_type_matches_pinned_streaming_outputs_readiness_and_replacements(QtMaType type, Type libraryType)
    {
        using var model = Model();
        var study = new QtMovingAverage { Type = type, Period = 8 };
        Assert.Null(model.AddIndicator(study, false).Error);
        // The expected library class is specified independently above, never obtained from the production factory.
        var constructor = libraryType.GetConstructors().Single(c =>
        {
            var parameters = c.GetParameters();
            return parameters.Length > 0 && parameters[0].ParameterType == typeof(int) && parameters.Skip(1).All(p => p.IsOptional);
        });
        using var reference = (Q.AbstractBase)constructor.Invoke(constructor.GetParameters()
            .Select((p, i) => i == 0 ? (object)8 : p.DefaultValue).ToArray());
        var output = Line(study);
        for (int i = 0; i < model.Series.Count; i++)
        {
            var candle = model.Series[i];
            double result = reference.Update(new Q.TValue(candle.Time, (double)model.Series.TicksToPrice(candle.CloseTicks)), true).Value;
            Assert.Equal(reference.IsHot, output.IsAvailable(i));
            if (reference.IsHot) Assert.Equal((decimal)result, output[i]);
        }
        int last = model.Series.Count - 1;
        model.Series[last].CloseTicks += 3;
        study.HostCalculate(last);
        var replacement = reference.Update(new Q.TValue(model.Series[last].Time, (double)model.Series.TicksToPrice(model.Series[last].CloseTicks)), false);
        Assert.Equal(reference.IsHot, output.IsAvailable(last));
        if (reference.IsHot) Assert.Equal((decimal)replacement.Value, output[last]);
        static decimal?[] Snapshot(OptionalValueDataSeries series) => Enumerable.Range(0, series.Count)
            .Select(i => series.IsAvailable(i) ? (decimal?)series[i] : null).ToArray();
        var expected = Snapshot(output);
        study.HostCalculate(7); // rewind plus complete replay
        Assert.Equal(expected, Snapshot(output));
        study.HostRecalculate();
        Assert.Equal(expected, Snapshot(output));
        // Append several real candles without calculating their intermediate indicator bars.
        for (int i = 0; i < 10; i++)
            model.Data.Trades.Add(new Trade(1000 + i, Start.AddMinutes(450 + i), 120 + i, 1, TradeSide.Buy));
        // Keep the existing indicator state while the actual host rebuilds its candle wrappers.
        var slot = model.Indicators.Single(s => s.Indicator == study);
        model.Indicators.Remove(slot);
        model.Rebuild();
        model.Indicators.Add(slot);
        study.HostCalculate(model.Series.Count - 1);
        var fresh = new QtMovingAverage { Type = type, Period = 8 };
        Assert.Null(model.AddIndicator(fresh, false).Error);
        Assert.Equal(Snapshot(Line(fresh)), Snapshot(output));
    }


    [Fact]
    public void Chart_forming_replacements_and_strategy_closed_bar_consumers_have_distinct_SMA_lifetimes()
    {
        Trade[] trades =
        [
            new(1, Start, 10, 2, TradeSide.Buy),
            new(2, Start.AddSeconds(20), 14, 3, TradeSide.Sell),
            new(3, Start.AddMinutes(1), 20, 7, TradeSide.Buy),
            new(4, Start.AddMinutes(1).AddSeconds(20), 24, 11, TradeSide.Sell),
        ];
        var data = new InstrumentData(Instrument, new SimulatedFeed());
        foreach (var trade in trades) data.Trades.Add(trade);
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var close = new QtMovingAverage { Type = QtMaType.Sma, Period = 2 };
        var volume = new QtMovingAverage { Type = QtMaType.Sma, Period = 2, Source = DataSeriesType.Volume };
        Assert.Null(model.AddIndicator(close, false).Error);
        Assert.Null(model.AddIndicator(volume, false).Error);
        Assert.False(Line(close).IsAvailable(0));
        Assert.False(Line(volume).IsAvailable(0));
        Assert.Equal(19m, Line(close)[1]);
        Assert.Equal(11.5m, Line(volume)[1]);
        model.Series[1].CloseTicks = 28;
        model.Series[1].Volume = 22;
        close.HostCalculate(1);
        volume.HostCalculate(1);
        Assert.Equal(21m, Line(close)[1]);
        Assert.Equal(13.5m, Line(volume)[1]);

        var strategy = new ClosedSourceProbe();
        var result = new BacktestEngine(strategy, TickData.FromTrades(Instrument, trades), Settings()).Run();
        Assert.Null(result.Error);
        Assert.Equal(new[] { 0, 0, 1, 1 }, strategy.CountAtTick);
        Assert.Equal(new[] { false, true }, strategy.ReadyAtClose);
        Assert.Equal(2, strategy.Close!.Count);
        Assert.Equal(19m, strategy.Close.Value);
        Assert.Equal(11.5m, strategy.Volume!.Value);
    }

    private sealed class ClosedSourceProbe : Strategy
    {
        public QtSeries? Close, Volume;
        public List<int> CountAtTick { get; } = new();
        public List<bool> ReadyAtClose { get; } = new();
        protected override void OnInit()
        {
            Close = Qt(new Q.Sma(2));
            Volume = Qt(new Q.Sma(2), PriceSource.Volume);
        }
        protected override void OnTick(Trade trade) => CountAtTick.Add(Close!.Count);
        protected override void OnBar(Bar bar) => ReadyAtClose.Add(Close!.IsReady && Volume!.IsReady);
    }

    [Fact]
    public async Task Cold_process_compiles_Q_alias_and_runs_the_actual_strategy_backtest()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-quant-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = root,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "QuantColdFixture", "TradingHub.QuantColdFixture.dll"));
            foreach (string store in new[] { "DATA", "STRATEGIES", "INDICATORS", "BACKTESTS", "LIVE" })
                start.Environment["TRADINGHUB_" + store + "_DIR"] = Path.Combine(root, store.ToLowerInvariant());
            start.Environment["TRADINGHUB_KEYCHAIN_FILE"] = Path.Combine(root, "fake-keychain.json");
            start.Environment["TRADINGHUB_SLEEP_GUARD"] = "off";
            string temp = Path.Combine(root, "tmp");
            Directory.CreateDirectory(temp);
            start.Environment["TMPDIR"] = temp;
            using var child = Process.Start(start)!;
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
            try
            {
                // Deadlock watchdog only; exit status and consumer assertions supply the evidence.
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
                Assert.True(child.ExitCode == 0, await stdout + Environment.NewLine + await stderr);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Library_readiness_is_not_bypassed_by_period_and_ribbon_warms_independently()
    {
        using var model = Model(25);
        var jma = new QtMovingAverage { Type = QtMaType.Jma, Period = 3 };
        var ribbon = new QtMaRibbon { FastType = QtMaType.Sma, FastPeriod = 2, MediumType = QtMaType.Jma, MediumPeriod = 3, SlowType = QtMaType.Wma, SlowPeriod = 40 };
        Assert.Null(model.AddIndicator(jma, false).Error);
        Assert.Null(model.AddIndicator(ribbon, false).Error);
        Assert.False(jma.IsReady);
        Assert.False(Line(jma).IsAvailable(24));
        Assert.True(Line(ribbon, 0).IsAvailable(24));
        Assert.False(Line(ribbon, 1).IsAvailable(24));
        Assert.False(Line(ribbon, 2).IsAvailable(24));
        jma.ShowWarmup = true;
        jma.HostRecalculate();
        Assert.True(Line(jma).IsAvailable(24));
        Assert.False(jma.IsReady);
    }

    [Fact]
    public void Minimum_periods_remain_library_truth_and_invalid_settings_do_not_mutate()
    {
        using var study = new QtMovingAverage { Type = QtMaType.Hma, Period = 2 };
        Assert.ThrowsAny<ArgumentException>(() => study.Period = 1);
        Assert.Equal(2, study.Period);
        using var model = Model(1);
        var ssf = new QtMovingAverage { Type = QtMaType.Ssf2, Period = 1, ShowWarmup = true };
        Assert.Null(model.AddIndicator(ssf, false).Error);
        Assert.True(Line(ssf).IsAvailable(0));
        Assert.ThrowsAny<ArgumentException>(() => QtFactory.Create(QtMaType.Hma, 1));
    }

    [Fact]
    public void Optional_series_distinguishes_real_zero_from_missing_and_clear_resets_availability()
    {
        var series = new OptionalValueDataSeries("Test", "Test");
        series.Set(0, 0);
        series.Set(1, -12);
        series.SetUnavailable(2);
        Assert.True(series.IsAvailable(0));
        Assert.True(series.IsAvailable(1));
        Assert.False(series.IsAvailable(2));
        series.Clear();
        Assert.False(series.IsAvailable(0));
    }

    [Fact]
    public void Camarilla_shared_formulas_cover_flat_negative_zero_low_and_extension_constants()
    {
        var l = CamarillaLevels.Calculate(110, 100, 105);
        Assert.Equal(105m, l.PP);
        Assert.Equal(107.75m, l.H3);
        Assert.Equal(110.5m, l.H4);
        Assert.Equal(115.5m, l.H5);
        Assert.Equal(121.34m, l.H6);
        Assert.Equal(88.66m, l.L6);
        var flat = CamarillaLevels.Calculate(-5, -5, -5);
        Assert.Equal(-5, flat.H6);
        Assert.Equal(-5, flat.L6);
        var zero = CamarillaLevels.Calculate(10, 0, 4);
        Assert.Equal(zero.H4, zero.H5);
        var negative = CamarillaLevels.Calculate(-10, -20, -15);
        Assert.Equal(-12.25m, negative.H3);
        Assert.Throws<ArgumentException>(() => CamarillaLevels.Calculate(1, 2, 1));
    }

    [Fact]
    public void Camarilla_has_no_first_session_levels_and_rollover_replacement_restores_prebar_state()
    {
        using var model = Model(3, 24 * 60);
        var study = new QtCamarilla();
        Assert.Null(model.AddIndicator(study, false).Error);
        var line = Line(study);
        Assert.False(line.IsAvailable(0));
        Assert.True(line.IsAvailable(1));
        Assert.True(line.IsThisPointOfEndLine(0));
        Assert.True(line.IsThisPointOfEndLine(1));
        var expected = CamarillaLevels.Calculate(100, 97, 97);
        Assert.Equal(expected.H3, line[1]);
        int last = model.Series.Count - 1;
        decimal value = line[last];
        model.Series[last].HighTicks += 100;
        study.HostCalculate(last);
        Assert.Equal(value, line[last]); // current-session high cannot contaminate frozen previous-session levels
        study.HostCalculate(last);
        Assert.Equal(value, line[last]);
        study.HostRecalculate();
        Assert.Equal(value, line[last]);
    }

    [Fact]
    public void Camarilla_chart_and_strategy_use_identical_previous_observed_sessions()
    {
        using var model = Model(5, 24 * 60);
        var study = new QtCamarilla();
        Assert.Null(model.AddIndicator(study, false).Error);
        var strategy = new CamarillaProbe();
        var trades = new List<Trade>();
        for (int i = 0; i < 5; i++)
        {
            trades.Add(new Trade(i * 2 + 1, Start.AddDays(i), 100 + i % 19, 2, TradeSide.Buy));
            trades.Add(new Trade(i * 2 + 2, Start.AddDays(i).AddSeconds(20), 97 + i % 17, 1, TradeSide.Sell));
        }
        var result = new BacktestEngine(strategy, TickData.FromTrades(Instrument, trades), Settings()).Run();
        Assert.Null(result.Error);
        Assert.Equal(5, strategy.Values.Count);
        Assert.False(strategy.Values[0].HasValue);
        for (int i = 1; i < 5; i++) Assert.Equal(Line(study)[i], strategy.Values[i]);
    }

    [Theory]
    [InlineData(CamarillaSessionMode.Cme)]
    [InlineData(CamarillaSessionMode.Custom)]
    public void Chart_session_boundaries_observe_DST_fractional_start_and_undo_rollover_replacements(CamarillaSessionMode mode)
    {
        DateTime boundary = mode == CamarillaSessionMode.Cme
            ? new DateTime(2026, 3, 7, 23, 0, 0, DateTimeKind.Utc) : Start.AddHours(9.5);
        DateTime next = mode == CamarillaSessionMode.Cme ? boundary.AddHours(23) : boundary.AddDays(1);
        DateTime[] times = [boundary.AddMinutes(-1), boundary, next.AddMinutes(-1), next];
        decimal[] prices = [100, 110, 90, 120];
        var data = new InstrumentData(Instrument, new SimulatedFeed());
        for (int i = 0; i < times.Length; i++)
            data.Trades.Add(new Trade(i + 1, times[i], prices[i], 1, TradeSide.Buy));
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var study = new QtCamarilla { TimeZoneId = "UTC", SessionStartHour = 9.5, SessionMode = mode };
        Assert.Null(model.AddIndicator(study, false).Error);
        var line = Line(study);
        Assert.False(line.IsAvailable(0));
        Assert.Equal(100m, line[1]);
        Assert.Equal(100m, line[2]);
        Assert.Equal(95.5m, line[3]);
        Assert.True(line.IsThisPointOfEndLine(0));
        Assert.False(line.IsThisPointOfEndLine(1));
        Assert.True(line.IsThisPointOfEndLine(2));
        model.Series[3].Time = times[2].AddSeconds(30);
        study.HostCalculate(3);
        Assert.Equal(100m, line[3]);
        Assert.False(line.IsThisPointOfEndLine(2));
        model.Series[3].Time = times[3];
        study.HostCalculate(3);
        Assert.Equal(95.5m, line[3]);
        Assert.True(line.IsThisPointOfEndLine(2));
    }

    [Fact]
    public void Session_identity_handles_CME_DST_fractional_start_gaps_and_year_boundaries()
    {
        var cme = CamarillaSessions.Create("America/New_York", 18);
        Assert.Equal(new DateOnly(2026, 3, 6), cme.SessionDate(new DateTime(2026, 3, 7, 22, 59, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 3, 7), cme.SessionDate(new DateTime(2026, 3, 7, 23, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 3, 8), cme.SessionDate(new DateTime(2026, 3, 8, 22, 0, 0, DateTimeKind.Utc)));
        var fractional = CamarillaSessions.Create("UTC", 9.5);
        Assert.Equal(new DateOnly(2025, 12, 31), fractional.SessionDate(Start.AddHours(9).AddMinutes(29)));
        Assert.Equal(new DateOnly(2026, 1, 1), fractional.SessionDate(Start.AddHours(9).AddMinutes(30)));
        var state = new CamarillaSessionState();
        state.Update(110, 90, 100, false);
        Assert.False(state.IsReady);
        state.Update(150, 120, 130, true); // next observed session may be days later
        Assert.Equal(CamarillaLevels.Calculate(110, 90, 100), state.Levels);
        using var study = new QtCamarilla();
        Assert.Throws<TimeZoneNotFoundException>(() => study.TimeZoneId = "Not/A_Zone");
        Assert.Throws<ArgumentOutOfRangeException>(() => study.SessionStartHour = double.NaN);
    }

    [Fact]
    public void Strategy_warmup_reports_actual_JMA_IsHot_instead_of_the_requested_period()
    {
        var result = StrategyWarmup.Measure(typeof(JmaProbe));
        Assert.Null(result.Error);
        Assert.True(result.Complete);
        Assert.True(result.Bars > 3);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    public void Invalid_numeric_outputs_are_visible_errors_not_ready_zero(double output)
    {
        NumericProbe.Output = output;
        var result = StrategyWarmup.Measure(typeof(NumericProbe));
        Assert.False(result.Complete);
        Assert.NotNull(result.Error);
        Assert.Contains(output == double.MaxValue ? "OverflowException" : "ArithmeticException", result.Error);
        Assert.True(NumericProbe.Last!.Disposed);
        using var model = Model(1);
        var chartStudy = new NumericChartProbe(output);
        Assert.NotNull(model.AddIndicator(chartStudy, false).Error);
        Assert.False(Line(chartStudy).IsAvailable(0));
    }


    private sealed class NumericChartProbe(double output) : QtIndicator
    {
        private readonly OptionalValueDataSeries _line = new("Numeric", "Numeric");
        private readonly InvalidIndicator _indicator = new(output);
        public NumericChartProbe() : this(0) { }
        protected override void ResetState() { DataSeries[0] = _line; _indicator.Reset(); }
        protected override void Step(int bar, decimal value, bool isNew) => Put(_line, _indicator, bar, Input(bar, value), isNew, false);
        protected override void OnDispose() => _indicator.Dispose();
    }

    [Fact]
    public void Owned_resources_are_released_after_OnStop_and_on_failure_cancellation_and_warmup()
    {
        var ticks = TickData.FromTrades(Instrument,
            Enumerable.Range(0, 3).Select(i => new Trade(i + 1, Start.AddMinutes(i), 100, 1, TradeSide.Buy)).ToList());
        var success = new DisposalProbe();
        Assert.Null(new BacktestEngine(success, ticks, Settings()).Run().Error);
        Assert.True(success.StopSawLiveResource);
        Assert.Equal(1, success.StopCalls);
        Assert.True(success.Resource!.Disposed);
        Assert.Equal(1, success.Resource.DisposeCalls);
        Assert.Equal(3, success.Owned!.Count);
        Assert.True(success.Owned.IsReady);
        Assert.Equal(42m, success.Owned.Value);
        Assert.False(success.Unowned.Disposed);
        var failure = new DisposalProbe { ThrowOnBar = true };
        Assert.NotNull(new BacktestEngine(failure, ticks, Settings()).Run().Error);
        Assert.Equal(0, failure.StopCalls); // preserve existing failure callback behavior
        Assert.True(failure.Resource!.Disposed);
        Assert.Equal(1, failure.Resource.DisposeCalls);
        Assert.Equal(1, failure.Owned!.Count);
        Assert.Equal(42m, failure.Owned.Value);
        var cancelled = new DisposalProbe();
        Assert.Throws<OperationCanceledException>(() => new BacktestEngine(cancelled, ticks, Settings()).Run(new CancellationToken(true)));
        Assert.True(cancelled.Resource!.Disposed);
        Assert.Equal(1, cancelled.Resource.DisposeCalls);
        var warmup = StrategyWarmup.Measure(typeof(DisposalProbe));
        Assert.Null(warmup.Error);
        Assert.True(DisposalProbe.Last!.Resource!.Disposed);
        Assert.Equal(1, DisposalProbe.Last.Resource.DisposeCalls);
        var initFailure = new DisposalProbe { ThrowOnInit = true };
        Assert.NotNull(new BacktestEngine(initFailure, ticks, Settings()).Run().Error);
        Assert.True(initFailure.Resource!.Disposed);
        Assert.Equal(1, initFailure.Resource.DisposeCalls);
    }

    public sealed class DisposalProbe : Strategy
    {
        public static DisposalProbe? Last;
        public InvalidIndicator? Resource;
        public QtSeries? Owned;
        public UnownedSeries Unowned = new();
        public bool ThrowOnInit, ThrowOnBar, StopSawLiveResource;
        public int StopCalls;
        protected override void OnInit()
        {
            Last = this;
            Owned = Qt(Resource = new InvalidIndicator(42));
            Add(Unowned);
            if (ThrowOnInit) throw new InvalidOperationException("Fixture initialization failure");
        }
        protected override void OnBar(Bar bar)
        {
            if (ThrowOnBar) throw new InvalidOperationException("Fixture bar failure");
        }
        protected override void OnStop() { StopCalls++; StopSawLiveResource = !Resource!.Disposed; }
    }

    public sealed class UnownedSeries : Series, IDisposable
    {
        public bool Disposed;
        public override bool IsReady => true;
        public void Dispose() => Disposed = true;
    }

    public sealed class JmaProbe : Strategy
    {
        protected override void OnInit() => Qt(new Q.Jma(3));
    }
    private sealed class CamarillaProbe : Strategy
    {
        private CamarillaSeries _series = null!;
        public List<decimal?> Values { get; } = new();
        protected override void OnInit() => _series = Camarilla();
        protected override void OnBar(Bar bar) => Values.Add(_series.AvailableValue);
    }
    public sealed class NumericProbe : Strategy
    {
        public static double Output;
        public static InvalidIndicator? Last;
        protected override void OnInit() => Qt(Last = new InvalidIndicator(Output));
    }
    public sealed class InvalidIndicator(double output) : Q.AbstractBase
    {
        public bool Disposed;
        public int DisposeCalls;
        public override bool IsHot => true;
        public override Q.TValue Update(Q.TValue input, bool isNew = true) => new(input.Time, output);
        public override Q.TSeries Update(Q.TSeries source) => throw new NotSupportedException();
        public override void Prime(ReadOnlySpan<double> source, TimeSpan? step = null) => throw new NotSupportedException();
        public override void Reset() { }
        protected override void Dispose(bool disposing) { Disposed = true; DisposeCalls++; base.Dispose(disposing); }
    }
    private static BacktestSettings Settings() => new()
    {
        FeedId = "test", Symbol = Instrument.Symbol, Period = "M1", FromUtc = Start, ToUtc = Start.AddDays(10),
        InitialCapital = 1000, CommissionPercent = 0, SlippageTicks = 0,
    };
}
