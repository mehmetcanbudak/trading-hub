// Adapted from PANASV/trading-hub cdb84ea16eee7ac8d6c1b74136d2eeacedadf834 (MIT with Commons Clause).
// QuanTAlib 0.8.12 (Miha Kralj), Apache-2.0, pinned source d1a5d96d2ed912604c32f8dcaca896720d33f1b7.
using TradingHub.Data;
using Q = QuanTAlib;

namespace TradingHub.Strategies;

/// <summary>Owned single-value QuanTAlib adapter, updated only with closed bars by the strategy host.</summary>
public sealed class QtSeries : Series, IDisposable
{
    private readonly Q.AbstractBase _indicator;
    private readonly PriceSource _source;
    private bool _valid, _hot, _disposed;
    internal QtSeries(Q.AbstractBase indicator, PriceSource source)
    {
        _indicator = indicator ?? throw new ArgumentNullException(nameof(indicator));
        if (!Enum.IsDefined(source))
        {
            _indicator.Dispose();
            throw new ArgumentOutOfRangeException(nameof(source));
        }
        _source = source;
        Name = indicator.Name;
    }
    public Q.AbstractBase Indicator => _indicator;
    public int WarmupPeriod => _indicator.WarmupPeriod;
    public override bool IsReady => _valid && _hot;
    public decimal? AvailableValue => _valid ? Value : null;
    internal override void Update(BarSeries bars)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bar = bars[0];
        _valid = false;
        double value = _indicator.Update(new Q.TValue(DateTime.SpecifyKind(bar.Time, DateTimeKind.Utc), (double)Source(bar, _source)), true).Value;
        if (!double.IsFinite(value)) throw new ArithmeticException($"QuanTAlib {Name} returned a non-finite value.");
        Push(checked((decimal)value));
        _hot = _indicator.IsHot;
        _valid = true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _indicator.Dispose();
    }
}

/// <summary>Previous observed session levels; primary series value is H3. Initial history can be partial.</summary>
public sealed class CamarillaSeries : Series
{
    private readonly Func<DateTime, DateOnly> _sessionOf;
    private DateOnly? _session;
    private CamarillaSessionState _state;
    internal CamarillaSeries(Func<DateTime, DateOnly> sessionOf) { _sessionOf = sessionOf; Name = "Camarilla"; }
    public override bool IsReady => _state.IsReady;
    public CamarillaLevels? Levels => IsReady ? _state.Levels : null;
    public decimal? AvailableValue => IsReady ? Value : null;
    public decimal PP => _state.Levels.PP;
    public decimal H1 => _state.Levels.H1;
    public decimal H2 => _state.Levels.H2;
    public decimal H3 => _state.Levels.H3;
    public decimal H4 => _state.Levels.H4;
    public decimal H5 => _state.Levels.H5;
    public decimal H6 => _state.Levels.H6;
    public decimal L1 => _state.Levels.L1;
    public decimal L2 => _state.Levels.L2;
    public decimal L3 => _state.Levels.L3;
    public decimal L4 => _state.Levels.L4;
    public decimal L5 => _state.Levels.L5;
    public decimal L6 => _state.Levels.L6;
    internal override void Update(BarSeries bars)
    {
        var bar = bars[0];
        var session = _sessionOf(bar.Time);
        _state.Update(bar.High, bar.Low, bar.Close, _session.HasValue && session != _session.Value);
        _session = session;
        Push(IsReady ? H3 : 0m);
    }
}
