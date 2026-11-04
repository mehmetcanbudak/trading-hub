using TradingHub.Feeds;

namespace TradingHub.Data;

/// <summary>UI-owned ordered observations. Missing data is never represented as an observed zero.</summary>
public sealed class OpenInterestStore
{
    private readonly List<OpenInterestSample> _samples = new();
    public IReadOnlyList<OpenInterestSample> Samples => _samples;
    public OpenInterestAvailability Availability { get; internal set; } = OpenInterestAvailability.Unsupported;
    public string? Message { get; internal set; }
    /// <summary>Envelope of retained observations, not a claim of continuous coverage.</summary>
    public DateTime? CoverageFrom => _samples.Count > 0 ? _samples[0].Time : null;
    public DateTime? CoverageTo => _samples.Count > 0 ? _samples[^1].Time : null;
    public TimeSpan MaxAge { get; internal set; } = TimeSpan.FromMinutes(10);
    /// <summary>Earliest pending observation correction, shared by all charts during one Pump notification.</summary>
    public DateTime? ChangedFrom { get; private set; }

    internal void AcceptChanges() => ChangedFrom = null;

    private void Changed(DateTime time) => ChangedFrom = ChangedFrom is { } previous && previous < time ? previous : time;

    internal void Clear()
    {
        _samples.Clear();
        Changed(DateTime.MinValue);
        Message = null;
    }

    public void Add(OpenInterestSample sample)
    {
        if (sample.Time.Kind != DateTimeKind.Utc || sample.Quantity < 0)
            throw new ArgumentException("Open interest requires a UTC server timestamp and nonnegative quantity.");
        int index = UpperBound(sample.Time);
        if (index > 0 && _samples[index - 1].Time == sample.Time)
        {
            if (_samples[index - 1] == sample) return;
            _samples[index - 1] = sample;
        }
        else _samples.Insert(index, sample);
        Changed(sample.Time);
    }

    private int UpperBound(DateTime time)
    {
        int lo = 0, hi = _samples.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_samples[mid].Time <= time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
    internal bool TrimBefore(DateTime time)
    {
        int count = UpperBound(time.AddTicks(-1));
        if (count == 0) return false;
        _samples.RemoveRange(0, count);
        Changed(DateTime.MinValue);
        return true;
    }

    public bool TryAsOf(DateTime cutoff, out OpenInterestSample sample)
    {
        int index = UpperBound(cutoff) - 1;
        if (index >= 0 && cutoff - _samples[index].Time <= MaxAge)
        {
            sample = _samples[index];
            return true;
        }
        sample = default;
        return false;
    }

    /// <summary>Maps each chart independently. Time bars use close-exclusive cutoffs; non-time bars use actual last trade.</summary>
    public int Apply(CandleSeries series, DateTime now, int fromBar = 0)
    {
        if (Availability == OpenInterestAvailability.Unsupported && _samples.Count == 0)
        {
            if (series.Last is { } last) last.OpenInterestStatus = "Open interest is not supported by this instrument/provider.";
            return -1;
        }
        bool timeBar = series.Period.IsTimeBased;
        int firstChanged = -1;
        for (int i = Math.Max(0, fromBar); i < series.Count; i++)
        {
            var candle = series[i];
            var old = (candle.HasOpenInterest, candle.OI, candle.MaxOI, candle.MinOI,
                candle.HasOpenInterestRange, candle.OpenInterestTime, candle.HasOpenInterestChange, candle.OpenInterestChange);
            DateTime end = timeBar
                ? new DateTime(series.TimeBucket(candle.Time.Ticks).End - 1, DateTimeKind.Utc)
                : candle.LastTime;
            if (end > now) end = now;
            OpenInterestSample latest = default;
            candle.HasOpenInterest = end >= candle.Time && TryAsOf(end, out latest);
            candle.OI = candle.MaxOI = candle.MinOI = 0;
            candle.OpenInterestTime = null;
            candle.OpenInterestStatus = Message ?? (Availability switch
            {
                OpenInterestAvailability.Unsupported => "Open interest is not supported by this instrument/provider.",
                OpenInterestAvailability.Loading => "Loading open-interest observations…",
                OpenInterestAvailability.Error => "Open-interest provider error.",
                OpenInterestAvailability.Partial => "Partial open-interest coverage.",
                OpenInterestAvailability.Available when candle.HasOpenInterest => "Server-observed open interest.",
                _ => "No open-interest observation at this candle cutoff."
            });
            candle.HasOpenInterestRange = false;
            candle.HasOpenInterestChange = false;
            candle.OpenInterestChange = 0;
            if (candle.HasOpenInterest)
            {
                candle.OI = latest.Quantity;
                candle.OpenInterestTime = latest.Time;
            }
            int start = UpperBound(candle.Time.AddTicks(-1));
            for (int j = start; j < _samples.Count && _samples[j].Time <= end; j++)
            {
                decimal value = _samples[j].Quantity;
                if (!candle.HasOpenInterestRange) candle.MinOI = candle.MaxOI = value;
                else { candle.MinOI = Math.Min(candle.MinOI, value); candle.MaxOI = Math.Max(candle.MaxOI, value); }
                candle.HasOpenInterestRange = true;
            }
            if (i > 0 && candle.HasOpenInterest && series[i - 1].HasOpenInterest)
            {
                candle.HasOpenInterestChange = true;
                candle.OpenInterestChange = candle.OI - series[i - 1].OI;
            }
            var current = (candle.HasOpenInterest, candle.OI, candle.MaxOI, candle.MinOI,
                candle.HasOpenInterestRange, candle.OpenInterestTime, candle.HasOpenInterestChange, candle.OpenInterestChange);
            if (old != current && firstChanged < 0) firstChanged = i;
        }
        return firstChanged;
    }
}
