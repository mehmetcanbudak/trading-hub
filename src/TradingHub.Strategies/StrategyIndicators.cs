namespace TradingHub.Strategies;

/// <summary>Dato de la vela que usa un indicador.</summary>
public enum PriceSource
{
    Close,
    Open,
    High,
    Low,
    Typical,
    Median,
    Volume,
    Delta,
}

/// <summary>
/// Serie de valores por vela. El índice cuenta hacia atrás: <c>serie[0]</c> es el valor de la
/// última vela cerrada, <c>serie[1]</c> el anterior, etc.
/// </summary>
public class Series
{
    protected readonly List<decimal> Values = new();

    public string Name { get; protected init; } = "";

    /// <summary>Valor de hace <paramref name="ago"/> velas (0 si todavía no hay datos).</summary>
    public decimal this[int ago] => ago >= 0 && ago < Values.Count ? Values[Values.Count - 1 - ago] : 0m;

    public decimal Value => this[0];
    public int Count => Values.Count;

    /// <summary>Si ya tiene velas suficientes para que el valor sea válido.</summary>
    public virtual bool IsReady => Values.Count > 0;

    public static implicit operator decimal(Series s) => s.Value;

    internal virtual void Update(BarSeries bars)
    {
    }

    protected void Push(decimal v) => Values.Add(v);

    protected static decimal Source(Bar b, PriceSource s) => s switch
    {
        PriceSource.Open => b.Open,
        PriceSource.High => b.High,
        PriceSource.Low => b.Low,
        PriceSource.Typical => b.Typical,
        PriceSource.Median => (b.High + b.Low) / 2,
        PriceSource.Volume => b.Volume,
        PriceSource.Delta => b.Delta,
        _ => b.Close,
    };

    public override string ToString() => $"{Name} = {Value}";
}

/// <summary>Serie calculada con una función propia (para combinar indicadores).</summary>
public sealed class CustomSeries : Series
{
    private readonly Func<BarSeries, decimal> _calc;

    internal CustomSeries(string name, Func<BarSeries, decimal> calc)
    {
        Name = name;
        _calc = calc;
    }

    internal override void Update(BarSeries bars) => Push(_calc(bars));
}

/// <summary>Media móvil simple.</summary>
public sealed class Sma : Series
{
    private readonly int _period;
    private readonly PriceSource _source;
    private readonly Queue<decimal> _window = new();
    private decimal _sum;

    internal Sma(int period, PriceSource source)
    {
        _period = Math.Max(1, period);
        _source = source;
        Name = $"SMA({_period})";
    }

    public override bool IsReady => _window.Count >= _period;

    internal override void Update(BarSeries bars)
    {
        decimal v = Source(bars[0], _source);
        _window.Enqueue(v);
        _sum += v;
        if (_window.Count > _period) _sum -= _window.Dequeue();
        Push(_sum / _window.Count);
    }
}

/// <summary>Media móvil exponencial.</summary>
public sealed class Ema : Series
{
    private readonly int _period;
    private readonly PriceSource _source;
    private readonly decimal _k;
    private int _n;
    private decimal _sum;

    internal Ema(int period, PriceSource source)
    {
        _period = Math.Max(1, period);
        _source = source;
        _k = 2m / (_period + 1);
        Name = $"EMA({_period})";
    }

    public override bool IsReady => _n >= _period;

    internal override void Update(BarSeries bars) => Push(Next(Source(bars[0], _source)));

    internal decimal Next(decimal v)
    {
        _n++;
        if (_n <= _period)
        {
            // arranca con la media simple de las primeras velas
            _sum += v;
            return _sum / _n;
        }

        return Values[^1] + _k * (v - Values[^1]);
    }

    internal void PushValue(decimal v) => Push(v);
}

/// <summary>RSI de Wilder (0–100).</summary>
public sealed class Rsi : Series
{
    private readonly int _period;
    private readonly PriceSource _source;
    private decimal _avgGain, _avgLoss, _prev;
    private int _n;

    internal Rsi(int period, PriceSource source)
    {
        _period = Math.Max(1, period);
        _source = source;
        Name = $"RSI({_period})";
    }

    public override bool IsReady => _n > _period;

    internal override void Update(BarSeries bars)
    {
        decimal v = Source(bars[0], _source);
        if (_n == 0)
        {
            _prev = v;
            _n++;
            Push(50);
            return;
        }

        decimal ch = v - _prev;
        _prev = v;
        decimal gain = Math.Max(ch, 0), loss = Math.Max(-ch, 0);
        if (_n <= _period)
        {
            _avgGain += gain / _period;
            _avgLoss += loss / _period;
        }
        else
        {
            _avgGain = (_avgGain * (_period - 1) + gain) / _period;
            _avgLoss = (_avgLoss * (_period - 1) + loss) / _period;
        }

        _n++;
        Push(_avgLoss == 0 ? (_avgGain == 0 ? 50 : 100) : 100 - 100 / (1 + _avgGain / _avgLoss));
    }
}

/// <summary>Rango verdadero promedio (volatilidad, en precio).</summary>
public sealed class Atr : Series
{
    private readonly int _period;
    private decimal _atr, _prevClose;
    private int _n;

    internal Atr(int period)
    {
        _period = Math.Max(1, period);
        Name = $"ATR({_period})";
    }

    public override bool IsReady => _n >= _period;

    internal override void Update(BarSeries bars)
    {
        var b = bars[0];
        decimal tr = _n == 0 ? b.High - b.Low : Math.Max(b.High - b.Low, Math.Max(Math.Abs(b.High - _prevClose), Math.Abs(b.Low - _prevClose)));
        _prevClose = b.Close;
        _n++;
        _atr = _n <= _period ? (_atr * (_n - 1) + tr) / _n : (_atr * (_period - 1) + tr) / _period;
        Push(_atr);
    }
}

/// <summary>Máximo de las últimas N velas.</summary>
public sealed class Highest : Series
{
    private readonly int _period;
    private readonly PriceSource _source;

    internal Highest(int period, PriceSource source)
    {
        _period = Math.Max(1, period);
        _source = source;
        Name = $"Highest({_period})";
    }

    public override bool IsReady => Values.Count >= _period;

    internal override void Update(BarSeries bars)
    {
        decimal m = decimal.MinValue;
        for (int i = 0; i < _period && i < bars.Count; i++) m = Math.Max(m, Source(bars[i], _source));
        Push(m);
    }
}

/// <summary>Mínimo de las últimas N velas.</summary>
public sealed class Lowest : Series
{
    private readonly int _period;
    private readonly PriceSource _source;

    internal Lowest(int period, PriceSource source)
    {
        _period = Math.Max(1, period);
        _source = source;
        Name = $"Lowest({_period})";
    }

    public override bool IsReady => Values.Count >= _period;

    internal override void Update(BarSeries bars)
    {
        decimal m = decimal.MaxValue;
        for (int i = 0; i < _period && i < bars.Count; i++) m = Math.Min(m, Source(bars[i], _source));
        Push(m);
    }
}

/// <summary>Desvío estándar de las últimas N velas.</summary>
public sealed class StdDev : Series
{
    private readonly int _period;
    private readonly PriceSource _source;
    private readonly Queue<decimal> _window = new();

    internal StdDev(int period, PriceSource source)
    {
        _period = Math.Max(2, period);
        _source = source;
        Name = $"StdDev({_period})";
    }

    public override bool IsReady => _window.Count >= _period;

    internal override void Update(BarSeries bars)
    {
        _window.Enqueue(Source(bars[0], _source));
        if (_window.Count > _period) _window.Dequeue();
        decimal mean = _window.Average();
        double var = _window.Sum(x => (double)((x - mean) * (x - mean))) / _window.Count;
        Push((decimal)Math.Sqrt(var));
    }
}

/// <summary>Bandas de Bollinger.</summary>
public sealed class Bollinger : Series
{
    private readonly Sma _mid;
    private readonly StdDev _sd;
    private readonly decimal _k;
    private readonly List<decimal> _upper = new(), _lower = new();

    internal Bollinger(int period, decimal k, PriceSource source)
    {
        _mid = new Sma(period, source);
        _sd = new StdDev(period, source);
        _k = k;
        Name = $"Bollinger({period},{k})";
    }

    public override bool IsReady => _mid.IsReady;
    public decimal Middle => Value;
    public decimal Upper => _upper.Count > 0 ? _upper[^1] : 0;
    public decimal Lower => _lower.Count > 0 ? _lower[^1] : 0;
    public decimal UpperAgo(int ago) => ago < _upper.Count ? _upper[_upper.Count - 1 - ago] : 0;
    public decimal LowerAgo(int ago) => ago < _lower.Count ? _lower[_lower.Count - 1 - ago] : 0;

    internal override void Update(BarSeries bars)
    {
        _mid.Update(bars);
        _sd.Update(bars);
        Push(_mid.Value);
        _upper.Add(_mid.Value + _k * _sd.Value);
        _lower.Add(_mid.Value - _k * _sd.Value);
    }
}

/// <summary>MACD: diferencia de dos EMAs, su señal y el histograma.</summary>
public sealed class Macd : Series
{
    private readonly Ema _fast, _slow, _signal;
    private readonly List<decimal> _signalValues = new();

    internal Macd(int fast, int slow, int signal)
    {
        _fast = new Ema(fast, PriceSource.Close);
        _slow = new Ema(slow, PriceSource.Close);
        _signal = new Ema(signal, PriceSource.Close);
        Name = $"MACD({fast},{slow},{signal})";
    }

    public override bool IsReady => _slow.IsReady && _signal.IsReady;
    public decimal Signal => _signalValues.Count > 0 ? _signalValues[^1] : 0;
    public decimal Histogram => Value - Signal;

    internal override void Update(BarSeries bars)
    {
        _fast.Update(bars);
        _slow.Update(bars);
        decimal macd = _fast.Value - _slow.Value;
        Push(macd);
        decimal sig = _signal.Next(macd);
        _signal.PushValue(sig);
        _signalValues.Add(sig);
    }
}

/// <summary>VWAP de la sesión (se reinicia al cambiar de día de sesión).</summary>
public sealed class Vwap : Series
{
    private readonly Func<DateTime, DateOnly> _session;
    private DateOnly _day;
    private decimal _pv, _v;

    internal Vwap(Func<DateTime, DateOnly> session)
    {
        _session = session;
        Name = "VWAP";
    }

    internal override void Update(BarSeries bars)
    {
        var b = bars[0];
        var day = _session(b.Time);
        if (day != _day)
        {
            _day = day;
            _pv = _v = 0;
        }

        _pv += b.Typical * b.Volume;
        _v += b.Volume;
        Push(_v > 0 ? _pv / _v : b.Close);
    }
}

/// <summary>Delta acumulado (opcionalmente reiniciado en cada sesión).</summary>
public sealed class CumulativeDelta : Series
{
    private readonly Func<DateTime, DateOnly>? _session;
    private DateOnly _day;
    private decimal _sum;

    internal CumulativeDelta(Func<DateTime, DateOnly>? session)
    {
        _session = session;
        Name = "Cumulative delta";
    }

    internal override void Update(BarSeries bars)
    {
        var b = bars[0];
        if (_session != null)
        {
            var day = _session(b.Time);
            if (day != _day)
            {
                _day = day;
                _sum = 0;
            }
        }

        _sum += b.Delta;
        Push(_sum);
    }
}
