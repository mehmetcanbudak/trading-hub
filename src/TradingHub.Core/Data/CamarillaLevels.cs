namespace TradingHub.Data;

/// <summary>Previous observed session Camarilla levels. These constants intentionally differ from QuanTAlib Pivotcam.</summary>
public readonly record struct CamarillaLevels(decimal PP, decimal H1, decimal H2, decimal H3, decimal H4,
    decimal H5, decimal H6, decimal L1, decimal L2, decimal L3, decimal L4, decimal L5, decimal L6)
{
    public static CamarillaLevels Calculate(decimal high, decimal low, decimal close)
    {
        if (high < low) throw new ArgumentException("Session high cannot be below session low.");
        decimal range = (high - low) * 1.1m;
        decimal h1 = close + range / 12m, h2 = close + range / 6m;
        decimal h3 = close + range / 4m, h4 = close + range / 2m;
        decimal h5 = low == 0 ? h4 : high / low * close;
        decimal h6 = h5 + 1.168m * (h5 - h4);
        return new((high + low + close) / 3m, h1, h2, h3, h4, h5, h6,
            close - range / 12m, close - range / 6m, close - range / 4m, close - range / 2m,
            2m * close - h5, 2m * close - h6);
    }
}

/// <summary>Streaming session accumulator; skips missing sessions rather than inventing OHLC.
/// The first observed session may be partial; its levels become available only at the next observed rollover.</summary>
public struct CamarillaSessionState
{
    private bool _hasSession;
    private decimal _high, _low, _close;
    public bool IsReady { get; private set; }
    public CamarillaLevels Levels { get; private set; }

    public void Update(decimal high, decimal low, decimal close, bool newSession)
    {
        if (high < low) throw new ArgumentException("Candle high cannot be below candle low.");
        if (!_hasSession || newSession)
        {
            if (_hasSession)
            {
                Levels = CamarillaLevels.Calculate(_high, _low, _close);
                IsReady = true;
            }
            _high = high;
            _low = low;
            _hasSession = true;
        }
        else
        {
            _high = Math.Max(_high, high);
            _low = Math.Min(_low, low);
        }
        _close = close;
    }
}

public static class CamarillaSessions
{
    public static SessionSettings Create(string timeZoneId, double sessionStartHour)
    {
        if (!double.IsFinite(sessionStartHour) || sessionStartHour < 0 || sessionStartHour >= 24)
            throw new ArgumentOutOfRangeException(nameof(sessionStartHour), "Session start must be in [0, 24) hours.");
        // An invalid zone is a configuration error, never an implicit UTC session.
        return new SessionSettings { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), Start = TimeSpan.FromHours(sessionStartHour) };
    }
}
