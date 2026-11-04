// Adapted from PANASV/trading-hub cdb84ea16eee7ac8d6c1b74136d2eeacedadf834 (MIT with Commons Clause).
// QuanTAlib 0.8.12, Miha Kralj, Apache-2.0; source d1a5d96d2ed912604c32f8dcaca896720d33f1b7.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using Q = QuanTAlib;

namespace TradingHub.Indicators.Native;

public enum QtMaType
{
    [Display(Name = "SMA")] Sma,
    [Display(Name = "EMA")] Ema,
    [Display(Name = "WMA")] Wma,
    [Display(Name = "HMA (Hull)")] Hma,
    [Display(Name = "ALMA (Arnaud Legoux)")] Alma,
    [Display(Name = "JMA (Jurik)")] Jma,
    [Display(Name = "KAMA (Kaufman)")] Kama,
    [Display(Name = "T3 (Tillson)")] T3,
    [Display(Name = "ZLEMA")] Zlema,
    [Display(Name = "DEMA")] Dema,
    [Display(Name = "TEMA")] Tema,
    [Display(Name = "VIDYA")] Vidya,
    [Display(Name = "SuperSmoother (Ehlers)")] Ssf2,
}

public static class QtFactory
{
    public static Q.AbstractBase Create(QtMaType type, int period) => type switch
    {
        QtMaType.Sma => new Q.Sma(period), QtMaType.Ema => new Q.Ema(period),
        QtMaType.Wma => new Q.Wma(period), QtMaType.Hma => new Q.Hma(period),
        QtMaType.Alma => new Q.Alma(period), QtMaType.Jma => new Q.Jma(period),
        QtMaType.Kama => new Q.Kama(period), QtMaType.T3 => new Q.T3(period),
        QtMaType.Zlema => new Q.Zlema(period), QtMaType.Dema => new Q.Dema(period),
        QtMaType.Tema => new Q.Tema(period), QtMaType.Vidya => new Q.Vidya(period),
        QtMaType.Ssf2 => new Q.Ssf2(period),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    internal static void Validate(QtMaType type, int period)
    {
        using var candidate = Create(type, period);
    }
}

/// <summary>Streaming lifecycle: latest replacement uses isNew=false; arbitrary edits replay from the beginning.</summary>
public abstract class QtIndicator : Indicator
{
    private int _last = -1;
    protected QtIndicator(bool candles = false) : base(candles) { }
    protected abstract void ResetState();
    protected abstract void Step(int bar, decimal value, bool isNew);
    protected sealed override void OnRecalculate() { _last = -1; ResetState(); }
    protected sealed override void OnCalculate(int bar, decimal value)
    {
        if (bar > _last + 1 || bar < _last)
        {
            foreach (var series in DataSeries) series.Clear();
            _last = -1;
            ResetState();
            for (int i = 0; i < bar; i++) Step(i, Convert.ToDecimal(SourceDataSeries[i]), true);
            _last = bar - 1;
        }
        Step(bar, value, bar == _last + 1);
        _last = bar;
    }
    protected Q.TValue Input(int bar, decimal value) => new(DateTime.SpecifyKind(GetCandle(bar).Time, DateTimeKind.Utc), (double)value);
    protected static void Put(OptionalValueDataSeries series, Q.AbstractBase indicator, int bar, Q.TValue input, bool isNew, bool showWarmup)
    {
        double value = indicator.Update(input, isNew).Value;
        // Failed conversions are visible indicator errors; they never become a ready zero.
        if (!double.IsFinite(value)) { series.SetUnavailable(bar); throw new ArithmeticException("QuanTAlib returned a non-finite value."); }
        decimal result;
        try { result = checked((decimal)value); }
        catch (OverflowException) { series.SetUnavailable(bar); throw; }
        series.Set(bar, result, showWarmup || indicator.IsHot);
    }
}

[DisplayName("QuanTAlib moving average")]
[Category("QuanTAlib")]
[Description("Thirteen streaming moving averages. Warmup display is optional; readiness always follows the library IsHot state.")]
public sealed class QtMovingAverage : QtIndicator
{
    private readonly OptionalValueDataSeries _average = new("MA", "Average") { Color = Color.FromArgb(255, 0, 200, 255), Width = 2 };
    private QtMaType _type = QtMaType.Ema;
    private int _period = 20;
    private bool _showWarmup;
    private Q.AbstractBase _indicator;
    public QtMovingAverage() { DataSeries[0] = _average; _indicator = QtFactory.Create(_type, _period); }
    [Display(Name = "Type", GroupName = "Calculation", Order = 10)]
    public QtMaType Type { get => _type; set { QtFactory.Validate(value, _period); _type = value; RecalculateValues(); } }
    [Display(Name = "Period", GroupName = "Calculation", Order = 20), Range(1, 10000)]
    public int Period { get => _period; set { QtFactory.Validate(_type, value); _period = value; RecalculateValues(); } }
    [Display(Name = "Source", GroupName = "Calculation", Order = 30)]
    public DataSeriesType Source { get => SourceType; set { SourceType = value; RecalculateValues(); } }
    [Display(Name = "Show warmup", GroupName = "Calculation", Order = 40)]
    public bool ShowWarmup { get => _showWarmup; set { _showWarmup = value; RecalculateValues(); } }
    [Browsable(false)] public bool IsReady => _indicator.IsHot && _average.IsAvailable(CurrentBar - 1);
    protected override void ResetState() { _indicator.Dispose(); _indicator = QtFactory.Create(_type, _period); }
    protected override void Step(int bar, decimal value, bool isNew) => Put(_average, _indicator, bar, Input(bar, value), isNew, _showWarmup);
    protected override void OnDispose() => _indicator.Dispose();
}

[DisplayName("QuanTAlib MA ribbon")]
[Category("QuanTAlib")]
[Description("Three independently configured moving averages; defaults EMA 9, EMA 60 and WMA 200. Each has independent library warmup.")]
public sealed class QtMaRibbon : QtIndicator
{
    private readonly OptionalValueDataSeries _fast = new("Fast", "Fast") { Color = Color.Gold };
    private readonly OptionalValueDataSeries _medium = new("Medium", "Medium") { Color = Color.MediumPurple, Width = 2 };
    private readonly OptionalValueDataSeries _slow = new("Slow", "Slow") { Color = Color.DodgerBlue, Width = 2 };
    private QtMaType _fastType = QtMaType.Ema, _mediumType = QtMaType.Ema, _slowType = QtMaType.Wma;
    private int _fastPeriod = 9, _mediumPeriod = 60, _slowPeriod = 200;
    private bool _showWarmup;
    private Q.AbstractBase _f, _m, _s;
    public QtMaRibbon()
    {
        DataSeries[0] = _fast; DataSeries.Add(_medium); DataSeries.Add(_slow);
        _f = QtFactory.Create(_fastType, _fastPeriod); _m = QtFactory.Create(_mediumType, _mediumPeriod); _s = QtFactory.Create(_slowType, _slowPeriod);
    }
    [Display(Name = "Fast type", GroupName = "Fast", Order = 10)]
    public QtMaType FastType { get => _fastType; set { QtFactory.Validate(value, _fastPeriod); _fastType = value; RecalculateValues(); } }
    [Display(Name = "Fast period", GroupName = "Fast", Order = 11), Range(1, 10000)]
    public int FastPeriod { get => _fastPeriod; set { QtFactory.Validate(_fastType, value); _fastPeriod = value; RecalculateValues(); } }
    [Display(Name = "Medium type", GroupName = "Medium", Order = 20)]
    public QtMaType MediumType { get => _mediumType; set { QtFactory.Validate(value, _mediumPeriod); _mediumType = value; RecalculateValues(); } }
    [Display(Name = "Medium period", GroupName = "Medium", Order = 21), Range(1, 10000)]
    public int MediumPeriod { get => _mediumPeriod; set { QtFactory.Validate(_mediumType, value); _mediumPeriod = value; RecalculateValues(); } }
    [Display(Name = "Slow type", GroupName = "Slow", Order = 30)]
    public QtMaType SlowType { get => _slowType; set { QtFactory.Validate(value, _slowPeriod); _slowType = value; RecalculateValues(); } }
    [Display(Name = "Slow period", GroupName = "Slow", Order = 31), Range(1, 10000)]
    public int SlowPeriod { get => _slowPeriod; set { QtFactory.Validate(_slowType, value); _slowPeriod = value; RecalculateValues(); } }
    [Display(Name = "Source", GroupName = "Calculation", Order = 40)]
    public DataSeriesType Source { get => SourceType; set { SourceType = value; RecalculateValues(); } }
    [Display(Name = "Show warmup", GroupName = "Calculation", Order = 41)]
    public bool ShowWarmup { get => _showWarmup; set { _showWarmup = value; RecalculateValues(); } }
    [Browsable(false)] public bool FastIsReady => _f.IsHot && _fast.IsAvailable(CurrentBar - 1);
    [Browsable(false)] public bool MediumIsReady => _m.IsHot && _medium.IsAvailable(CurrentBar - 1);
    [Browsable(false)] public bool SlowIsReady => _s.IsHot && _slow.IsAvailable(CurrentBar - 1);
    protected override void ResetState()
    {
        _f.Dispose(); _m.Dispose(); _s.Dispose();
        _f = QtFactory.Create(_fastType, _fastPeriod); _m = QtFactory.Create(_mediumType, _mediumPeriod); _s = QtFactory.Create(_slowType, _slowPeriod);
    }
    protected override void Step(int bar, decimal value, bool isNew)
    {
        var input = Input(bar, value);
        Put(_fast, _f, bar, input, isNew, _showWarmup);
        Put(_medium, _m, bar, input, isNew, _showWarmup);
        Put(_slow, _s, bar, input, isNew, _showWarmup);
    }
    protected override void OnDispose() { _f.Dispose(); _m.Dispose(); _s.Dispose(); }
}
