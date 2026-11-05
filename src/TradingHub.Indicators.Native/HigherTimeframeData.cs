// Independent OHLC aggregation. Calendar periods use exchange-session dates, never fixed 30-day months.
namespace TradingHub.Indicators.Native;

public readonly record struct HigherTimeframeCandle(long Key, int StartBar, int EndBar, int AvailableBar, DateOnly SessionDate,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, StudyCoverage Coverage, bool Partial, DateTime AvailableAt = default, int ResolutionSeconds = 0, bool SourceCompatible = true);

internal sealed class HigherTimeframeData
{
    private readonly List<HigherTimeframeCandle> _closed = new();
    private readonly IReadOnlyList<HigherTimeframeCandle> _closedView;
    private readonly int _retention;
    private HigherTimeframeCandle _live, _before; private bool _hasLive, _hadBefore; private int _last = -1, _beforeCount;
    internal HigherTimeframeData(StudyTimeFrame frame, int retention = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, 1);
        Frame = frame; _retention = retention; _closedView = _closed.AsReadOnly();
    }
    internal StudyTimeFrame Frame { get; }
    internal IReadOnlyList<HigherTimeframeCandle> Closed => _closedView;
    internal HigherTimeframeCandle Current => _live;
    internal bool HasCurrent => _hasLive;
    internal void Clear() { _closed.Clear(); _hasLive = _hadBefore = false; _last = -1; }
    internal void Update(int bar, IndicatorCandle c)
    {
        if (bar < _last) throw new InvalidOperationException("Higher-timeframe correction must replay its owner.");
        if (bar == _last)
        { _live = _before; _hasLive = _hadBefore; if (_closed.Count > _beforeCount) _closed.RemoveRange(_beforeCount, _closed.Count - _beforeCount); }
        else
        {
            if (_closed.Count > _retention) _closed.RemoveRange(0, _closed.Count - _retention);
            _before = _live; _hadBefore = _hasLive; _beforeCount = _closed.Count;
        }
        long key = StudyMath.PeriodKey(c, Frame);
        bool partial = !StudyMath.IsContained(c, Frame) || (c.Quality & TradingHub.Market.DataQuality.Partial) != 0;
        var coverage = StudyMath.Coverage(c) | ((c.Quality & TradingHub.Market.DataQuality.Synthetic) != 0 ? StudyCoverage.Estimated : 0);
        if (!_hasLive || _live.Key != key)
        {
            if (_hasLive)
            {
                bool late = _live.AvailableAt > c.Time;
                _closed.Add(_live with { AvailableBar = bar, Partial = _live.Partial || late,
                    SourceCompatible = _live.SourceCompatible && !late, Coverage = _live.Coverage | (_live.Partial || late ? StudyCoverage.Partial : 0) });
            }
            bool firstPartial = !StudyMath.IsPeriodOpening(c, Frame) || partial;
            _live = new(key, bar, bar, -1, c.SessionDate, c.Open, c.High, c.Low, c.Close, c.Volume,
                coverage | (firstPartial ? StudyCoverage.Partial : 0), firstPartial, c.AvailableAt, c.ResolutionSeconds, !partial);
            _hasLive = true;
        }
        else _live = _live with
        {
            EndBar = bar, High = Math.Max(_live.High, c.High), Low = Math.Min(_live.Low, c.Low), Close = c.Close, Volume = _live.Volume + c.Volume,
            Coverage = _live.Coverage | coverage | (partial ? StudyCoverage.Partial : 0), Partial = _live.Partial || partial, SourceCompatible = _live.SourceCompatible && !partial,
            AvailableAt = _live.AvailableAt > c.AvailableAt ? _live.AvailableAt : c.AvailableAt, ResolutionSeconds = Math.Max(_live.ResolutionSeconds, c.ResolutionSeconds)
        };
        _last = bar;
    }
}
