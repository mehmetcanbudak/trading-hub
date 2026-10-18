using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Indicators.Drawing;

namespace TradingHub.Indicators.Native;

public enum MaType
{
    [Display(Name = "Simple (SMA)")] Sma,
    [Display(Name = "Exponential (EMA)")] Ema,
    [Display(Name = "Weighted (WMA)")] Wma,
}

/// <summary>Media móvil simple, exponencial o ponderada.</summary>
[DisplayName("Moving average")]
[Category(IndicatorCategories.Trend)]
public class MovingAverage : Indicator
{
    private readonly ValueDataSeries _ma = new("MA", "Average") { Color = Color.FromArgb(255, 255, 196, 0), Width = 2 };
    private int _period = 20;
    private MaType _type = MaType.Ema;

    public MovingAverage()
    {
        DataSeries[0] = _ma;
    }

    [Display(Name = "Period", GroupName = "Calculation", Order = 10)]
    [Range(1, 10000)]
    public int Period { get => _period; set { _period = Math.Max(1, value); RecalculateValues(); } }

    [Display(Name = "Type", GroupName = "Calculation", Order = 20)]
    public MaType Type { get => _type; set { _type = value; RecalculateValues(); } }

    [Display(Name = "Source", GroupName = "Calculation", Order = 30)]
    public DataSeriesType Source { get => SourceType; set { SourceType = value; RecalculateValues(); } }

    protected override void OnCalculate(int bar, decimal value)
    {
        switch (_type)
        {
            case MaType.Sma:
                _ma[bar] = SourceDataSeries.CalcAverage(_period, bar);
                break;
            case MaType.Wma:
            {
                decimal num = 0, den = 0;
                int w = 1;
                for (int i = Math.Max(0, bar - _period + 1); i <= bar; i++, w++)
                {
                    num += (decimal)SourceDataSeries[i] * w;
                    den += w;
                }

                _ma[bar] = den > 0 ? num / den : value;
                break;
            }
            default:
            {
                decimal k = 2m / (_period + 1);
                _ma[bar] = bar == 0 ? value : value * k + _ma[bar - 1] * (1 - k);
                break;
            }
        }
    }
}

/// <summary>VWAP de sesión con bandas de desvío estándar.</summary>
/// <summary>Período en el que se reinicia el VWAP.</summary>
public enum VwapAnchor
{
    [Display(Name = "Session (day)")] Session,
    [Display(Name = "Week")] Week,
    [Display(Name = "Month")] Month,
}

[DisplayName("VWAP with bands")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("VWAP anchored to the session, week or month with up to three volume-weighted standard-deviation bands.")]
public class SessionVwap : Indicator
{
    private readonly ValueDataSeries _vwap = new("VWAP", "VWAP") { Color = Color.FromArgb(255, 255, 255, 255), Width = 2 };
    private readonly ValueDataSeries _up1 = new("Up1", "+1σ") { Color = Color.FromArgb(200, 70, 170, 255), LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _dn1 = new("Dn1", "-1σ") { Color = Color.FromArgb(200, 70, 170, 255), LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _up2 = new("Up2", "+2σ") { Color = Color.FromArgb(200, 255, 120, 60), LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _dn2 = new("Dn2", "-2σ") { Color = Color.FromArgb(200, 255, 120, 60), LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _up3 = new("Up3", "+3σ") { Color = Color.FromArgb(200, 240, 82, 90), LineDashStyle = LineDashStyle.Dot };
    private readonly ValueDataSeries _dn3 = new("Dn3", "-3σ") { Color = Color.FromArgb(200, 240, 82, 90), LineDashStyle = LineDashStyle.Dot };
    private decimal _sumPv, _sumV, _sumP2v;
    private int _sessionStart;
    private decimal _k1 = 1, _k2 = 2, _k3 = 3;
    private VwapAnchor _anchor = VwapAnchor.Session;

    public SessionVwap() : base(true)
    {
        DataSeries[0] = _vwap;
        DataSeries.Add(_up1);
        DataSeries.Add(_dn1);
        DataSeries.Add(_up2);
        DataSeries.Add(_dn2);
        DataSeries.Add(_up3);
        DataSeries.Add(_dn3);
    }

    [Display(Name = "Reset every", GroupName = "Calculation", Order = 1)]
    public VwapAnchor Anchor { get => _anchor; set { _anchor = value; RecalculateValues(); } }

    [Display(Name = "Band 1 (σ)", GroupName = "Bands", Order = 10)]
    [Range(0, 10)]
    public decimal Band1 { get => _k1; set { _k1 = value; RecalculateValues(); } }

    [Display(Name = "Band 2 (σ)", GroupName = "Bands", Order = 20)]
    [Range(0, 10)]
    public decimal Band2 { get => _k2; set { _k2 = value; RecalculateValues(); } }

    [Display(Name = "Band 3 (σ, 0 = hidden)", GroupName = "Bands", Order = 30)]
    [Range(0, 10)]
    public decimal Band3 { get => _k3; set { _k3 = value; RecalculateValues(); } }

    private bool IsNewPeriod(int bar) => _anchor switch
    {
        VwapAnchor.Week => IsNewWeek(bar),
        VwapAnchor.Month => IsNewMonth(bar),
        _ => IsNewSession(bar),
    };

    protected override void OnRecalculate()
    {
        _sumPv = _sumV = _sumP2v = 0;
        _sessionStart = 0;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0 || IsNewPeriod(bar))
        {
            if (bar != _sessionStart || bar == 0)
            {
                _sessionStart = bar;
                if (bar > 0) _vwap.SetPointOfEndLine(bar - 1);
                foreach (var s in new[] { _up1, _dn1, _up2, _dn2, _up3, _dn3 }) if (bar > 0) s.SetPointOfEndLine(bar - 1);
            }
        }

        // recalcula la sesión completa hasta esta barra (la última barra cambia con cada tick)
        if (bar == _sessionStart)
        {
            _sumPv = _sumV = _sumP2v = 0;
        }

        decimal pv = 0, v = 0, p2v = 0;
        var c = GetCandle(bar);
        foreach (var lvl in c.GetAllPriceLevels())
        {
            pv += lvl.Price * lvl.Volume;
            p2v += lvl.Price * lvl.Price * lvl.Volume;
            v += lvl.Volume;
        }

        if (v == 0)
        {
            decimal tp = (c.High + c.Low + c.Close) / 3;
            pv = tp * c.Volume;
            p2v = tp * tp * c.Volume;
            v = c.Volume;
        }

        // acumulado hasta la barra anterior + esta barra
        decimal accPv = _sumPv + pv, accV = _sumV + v, accP2v = _sumP2v + p2v;
        if (bar < CurrentBar - 1)
        {
            _sumPv = accPv;
            _sumV = accV;
            _sumP2v = accP2v;
        }

        if (accV <= 0) return;
        decimal vwap = accPv / accV;
        decimal variance = Math.Max(0, accP2v / accV - vwap * vwap);
        decimal sd = (decimal)Math.Sqrt((double)variance);
        _vwap[bar] = vwap;
        _up1[bar] = _k1 > 0 ? vwap + _k1 * sd : 0;
        _dn1[bar] = _k1 > 0 ? vwap - _k1 * sd : 0;
        _up2[bar] = _k2 > 0 ? vwap + _k2 * sd : 0;
        _dn2[bar] = _k2 > 0 ? vwap - _k2 * sd : 0;
        _up3[bar] = _k3 > 0 ? vwap + _k3 * sd : 0;
        _dn3[bar] = _k3 > 0 ? vwap - _k3 * sd : 0;
    }
}

/// <summary>Volumen por vela, coloreado según el delta.</summary>
[DisplayName("Volume")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class VolumeBars : Indicator
{
    private readonly ValueDataSeries _vol = new("Volume", "Volume") { VisualType = VisualMode.Histogram, Color = Color.FromArgb(255, 110, 120, 140) };

    public VolumeBars() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _vol;
    }

    [Display(Name = "Bullish color", GroupName = "Colors", Order = 10)]
    public Color UpColor { get; set; } = Color.FromArgb(255, 60, 170, 90);

    [Display(Name = "Bearish color", GroupName = "Colors", Order = 20)]
    public Color DownColor { get; set; } = Color.FromArgb(255, 200, 60, 60);

    [Display(Name = "Color by delta (otherwise by bar)", GroupName = "Colors", Order = 30)]
    public bool ByDelta { get; set; } = true;

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        _vol[bar] = c.Volume;
        bool up = ByDelta ? c.Delta >= 0 : c.Close >= c.Open;
        _vol.Colors[bar] = up ? UpColor : DownColor;
    }
}

/// <summary>Delta por vela (compras agresivas − ventas agresivas).</summary>
[DisplayName("Delta")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class DeltaBars : Indicator
{
    public enum DeltaView
    {
        [Display(Name = "Histogram")] Histogram,
        [Display(Name = "Candles (with delta high/low)")] Candles,
    }

    private readonly ValueDataSeries _delta = new("Delta", "Delta") { VisualType = VisualMode.Histogram };
    private readonly CandleDataSeries _candles = new("DeltaCandles", "Delta candles") { DrawCandleBorder = false };
    private DeltaView _view = DeltaView.Histogram;

    public DeltaBars() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _delta;
        DataSeries.Add(_candles);
        LineSeries.Add(new LineSeries("Zero", "Zero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
    }

    [Display(Name = "View", GroupName = "Visualization", Order = 10)]
    public DeltaView View { get => _view; set { _view = value; RecalculateValues(); } }

    [Display(Name = "Positive color", GroupName = "Visualization", Order = 20)]
    public Color PositiveColor { get; set; } = Color.FromArgb(255, 73, 209, 73);

    [Display(Name = "Negative color", GroupName = "Visualization", Order = 30)]
    public Color NegativeColor { get; set; } = Color.FromArgb(255, 232, 69, 72);

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        if (_view == DeltaView.Histogram)
        {
            _delta.VisualType = VisualMode.Histogram;
            _candles.Visible = false;
            _delta[bar] = c.Delta;
            _delta.Colors[bar] = c.Delta >= 0 ? PositiveColor : NegativeColor;
        }
        else
        {
            _delta.VisualType = VisualMode.OnlyValueOnAxis;
            _delta[bar] = c.Delta;
            _candles.Visible = true;
            _candles.UpCandleColor = PositiveColor;
            _candles.DownCandleColor = NegativeColor;
            _candles[bar] = new Candle(0, c.MaxDelta, c.MinDelta, c.Delta);
        }
    }
}

/// <summary>Delta acumulado (CVD) en velas.</summary>
[DisplayName("Cumulative delta (CVD)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class CumulativeDeltaCandles : Indicator
{
    private readonly CandleDataSeries _cvd = new("CVD", "Cumulative delta") { DrawCandleBorder = false };
    private readonly ValueDataSeries _value = new("Value", "Value") { VisualType = VisualMode.OnlyValueOnAxis, Color = Color.FromArgb(255, 200, 200, 210) };
    private bool _resetDaily;
    private decimal _base;

    public CumulativeDeltaCandles() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _value;
        DataSeries.Add(_cvd);
    }

    [Display(Name = "Reset each session", GroupName = "Calculation", Order = 10)]
    public bool ResetDaily { get => _resetDaily; set { _resetDaily = value; RecalculateValues(); } }

    protected override void OnRecalculate() => _base = 0;

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        decimal open = bar == 0 ? 0 : _cvd[bar - 1].Close;
        if (_resetDaily && IsNewSession(bar)) open = 0;
        _cvd[bar] = new Candle(open, open + c.MaxDelta, open + c.MinDelta, open + c.Delta);
        _value[bar] = open + c.Delta;
    }
}

/// <summary>RSI de Wilder.</summary>
[DisplayName("RSI")]
[Category(IndicatorCategories.Oscillators)]
public class Rsi : Indicator
{
    private readonly ValueDataSeries _rsi = new("RSI", "RSI") { Color = Color.FromArgb(255, 180, 120, 255), Width = 2 };
    private readonly ValueDataSeries _gain = new("g"), _loss = new("l");
    private int _period = 14;

    public Rsi()
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _rsi;
        LineSeries.Add(new LineSeries("70", "Overbought") { Value = 70, Color = Color.FromArgb(150, 232, 69, 72) });
        LineSeries.Add(new LineSeries("30", "Oversold") { Value = 30, Color = Color.FromArgb(150, 73, 209, 73) });
    }

    [Display(Name = "Period", GroupName = "Calculation", Order = 10)]
    [Range(2, 1000)]
    public int Period { get => _period; set { _period = Math.Max(2, value); RecalculateValues(); } }

    protected override void OnRecalculate()
    {
        _gain.Clear();
        _loss.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0)
        {
            _rsi[bar] = 50;
            return;
        }

        decimal diff = value - (decimal)SourceDataSeries[bar - 1];
        decimal g = Math.Max(diff, 0), l = Math.Max(-diff, 0);
        _gain[bar] = (_gain[bar - 1] * (_period - 1) + g) / _period;
        _loss[bar] = (_loss[bar - 1] * (_period - 1) + l) / _period;
        _rsi[bar] = _loss[bar] == 0 ? 100 : 100 - 100 / (1 + _gain[bar] / _loss[bar]);
    }
}

/// <summary>MACD con histograma.</summary>
[DisplayName("MACD")]
[Category(IndicatorCategories.Oscillators)]
public class Macd : Indicator
{
    private readonly ValueDataSeries _macd = new("MACD", "MACD") { Color = Color.FromArgb(255, 30, 144, 255), Width = 2 };
    private readonly ValueDataSeries _signal = new("Signal", "Signal") { Color = Color.FromArgb(255, 255, 140, 0) };
    private readonly ValueDataSeries _hist = new("Hist", "Histogram") { VisualType = VisualMode.Histogram, ShowCurrentValue = false };
    private readonly ValueDataSeries _fast = new("f"), _slow = new("s");
    private int _fastP = 12, _slowP = 26, _sigP = 9;

    public Macd()
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _hist;
        DataSeries.Add(_macd);
        DataSeries.Add(_signal);
    }

    [Display(Name = "Fast", GroupName = "Calculation", Order = 10)] public int Fast { get => _fastP; set { _fastP = Math.Max(1, value); RecalculateValues(); } }
    [Display(Name = "Slow", GroupName = "Calculation", Order = 20)] public int Slow { get => _slowP; set { _slowP = Math.Max(1, value); RecalculateValues(); } }
    [Display(Name = "Signal", GroupName = "Calculation", Order = 30)] public int SignalPeriod { get => _sigP; set { _sigP = Math.Max(1, value); RecalculateValues(); } }

    protected override void OnRecalculate()
    {
        _fast.Clear();
        _slow.Clear();
    }

    private static decimal Ema(ValueDataSeries s, int bar, decimal v, int p) => bar == 0 ? v : v * (2m / (p + 1)) + s[bar - 1] * (1 - 2m / (p + 1));

    protected override void OnCalculate(int bar, decimal value)
    {
        _fast[bar] = Ema(_fast, bar, value, _fastP);
        _slow[bar] = Ema(_slow, bar, value, _slowP);
        _macd[bar] = _fast[bar] - _slow[bar];
        _signal[bar] = Ema(_signal, bar, _macd[bar], _sigP);
        _hist[bar] = _macd[bar] - _signal[bar];
        _hist.Colors[bar] = _hist[bar] >= 0 ? Color.FromArgb(180, 73, 209, 73) : Color.FromArgb(180, 232, 69, 72);
    }
}

/// <summary>Bandas de Bollinger.</summary>
[DisplayName("Bollinger Bands")]
[Category(IndicatorCategories.Trend)]
public class Bollinger : Indicator
{
    private readonly ValueDataSeries _mid = new("Mid", "Average") { Color = Color.FromArgb(220, 200, 200, 210) };
    private readonly ValueDataSeries _up = new("Up", "Upper") { Color = Color.FromArgb(220, 30, 144, 255) };
    private readonly ValueDataSeries _dn = new("Dn", "Lower") { Color = Color.FromArgb(220, 30, 144, 255) };
    private readonly RangeDataSeries _band = new("Band", "Fill") { RangeColor = Color.FromArgb(22, 30, 144, 255) };
    private int _period = 20;
    private decimal _width = 2;

    public Bollinger()
    {
        DataSeries[0] = _mid;
        DataSeries.Add(_up);
        DataSeries.Add(_dn);
        DataSeries.Add(_band);
    }

    [Display(Name = "Period", GroupName = "Calculation", Order = 10)] public int Period { get => _period; set { _period = Math.Max(2, value); RecalculateValues(); } }
    [Display(Name = "Standard deviations", GroupName = "Calculation", Order = 20)] public decimal Width { get => _width; set { _width = value; RecalculateValues(); } }

    protected override void OnCalculate(int bar, decimal value)
    {
        int start = Math.Max(0, bar - _period + 1);
        int n = bar - start + 1;
        decimal mean = SourceDataSeries.CalcSum(_period, bar) / n;
        double var = 0;
        for (int i = start; i <= bar; i++)
        {
            double d = (double)((decimal)SourceDataSeries[i] - mean);
            var += d * d;
        }

        decimal sd = (decimal)Math.Sqrt(var / n);
        _mid[bar] = mean;
        _up[bar] = mean + _width * sd;
        _dn[bar] = mean - _width * sd;
        _band[bar].Upper = _up[bar];
        _band[bar].Lower = _dn[bar];
    }
}
