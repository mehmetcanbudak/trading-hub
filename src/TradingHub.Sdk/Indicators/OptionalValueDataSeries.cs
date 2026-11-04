namespace TradingHub.Indicators;

/// <summary>Explicit availability for series where zero is a real value, not a missing-data sentinel.</summary>
public interface IValueDataSeriesAvailability
{
    bool IsAvailable(int bar);
}

/// <summary>Opt-in numeric gaps. Existing ValueDataSeries retain their legacy zero semantics.</summary>
public sealed class OptionalValueDataSeries(string id, string name) : ValueDataSeries(id, name), IValueDataSeriesAvailability
{
    private bool[] _available = Array.Empty<bool>();

    public bool IsAvailable(int bar) => bar >= 0 && bar < _available.Length && _available[bar];

    public void Set(int bar, decimal value, bool available = true)
    {
        if (bar < 0) throw new ArgumentOutOfRangeException(nameof(bar));
        if (bar >= _available.Length) Array.Resize(ref _available, Math.Max(bar + 1, Math.Max(16, _available.Length * 2)));
        this[bar] = value;
        _available[bar] = available;
    }

    public void SetUnavailable(int bar) => Set(bar, 0m, false);

    public override void Clear()
    {
        base.Clear();
        Array.Clear(_available);
    }
}
