using TradingHub.Market;

namespace TradingHub.Replay;

public sealed record ReplayCandidate(Instrument Instrument, IReadOnlyList<DateOnly> Days);

/// <summary>A reproducible practice interval. Blind mode conceals the UI, not the underlying data.</summary>
public sealed record ReplaySelection(Instrument Source, DateTime StartUtc, DateTime EndUtc, int Seed, bool Blind)
{
    public static ReplaySelection Selected(Instrument source, DateTime startUtc, DateTime endUtc, int seed = 0, bool blind = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (startUtc.Kind != DateTimeKind.Utc || endUtc.Kind != DateTimeKind.Utc || endUtc <= startUtc)
            throw new ArgumentException("Replay needs an increasing UTC interval.");
        if (source.TickSize <= 0 || source.VolumeStep <= 0 || source.PointValue <= 0)
            throw new ArgumentException("Replay needs positive tick size, quantity step and point value.");
        return new(source, startUtc, endUtc, seed, blind);
    }

    /// <summary>Only consecutive recorded-day windows participate; input ordering cannot change a seed's choice.</summary>
    public static ReplaySelection Random(IReadOnlyList<ReplayCandidate> candidates, int seed, bool blind, int lengthDays = 3)
    {
        if (lengthDays is < 1 or > 366) throw new ArgumentOutOfRangeException(nameof(lengthDays));
        var windows = new List<(Instrument Instrument, DateOnly Start)>();
        foreach (var group in candidates.GroupBy(c => c.Instrument.Key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var candidate = group.First();
            var days = group.SelectMany(c => c.Days).Distinct().Order().ToArray();
            for (int i = 0; i + lengthDays <= days.Length; i++)
                if (days[i + lengthDays - 1].DayNumber - days[i].DayNumber == lengthDays - 1)
                    windows.Add((candidate.Instrument, days[i]));
        }
        if (windows.Count == 0) throw new InvalidOperationException("No consecutive recorded days are available for this replay length.");
        // Fixed algorithm, independent of runtime Random implementation and process-global random state.
        uint state = unchecked((uint)seed) ^ 0x9E3779B9u;
        uint Next()
        {
            state += 0x9E3779B9u;
            uint value = state;
            value = (value ^ (value >> 16)) * 0x85EBCA6Bu;
            value = (value ^ (value >> 13)) * 0xC2B2AE35u;
            return value ^ (value >> 16);
        }
        var chosen = windows[(int)(Next() % (uint)windows.Count)];
        var midnight = chosen.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return Selected(chosen.Instrument, midnight.AddMinutes(Next() % 1440), midnight.AddDays(lengthDays), seed, blind);
    }
}

public sealed record ReplayFees(decimal PerUnit = 0, decimal NotionalRate = 0)
{
    internal void Validate()
    {
        if (PerUnit < 0 || NotionalRate < 0 || NotionalRate > 1) throw new ArgumentOutOfRangeException(nameof(PerUnit));
    }
}

public sealed record ReplayResult(Guid RunId, int Seed, string Instrument, DateTime StartUtc, DateTime EndUtc,
    DateTime ClockUtc, bool Ended, decimal StartBalance, decimal RealizedNet, decimal OpenPnl,
    decimal Commission, decimal Position, decimal AveragePrice, IReadOnlyList<Trading.AccountFill> Fills,
    ReplayFees Fees, DataQuality DataQuality, int ResolutionSeconds, long CorrectionRevision, IReadOnlyList<string> SourceIdentities,
    string? DataWarning, bool HasKnownAvailability)
{
    public decimal Equity => StartBalance + RealizedNet + OpenPnl;
}
