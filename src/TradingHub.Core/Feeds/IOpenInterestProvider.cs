using TradingHub.Market;

namespace TradingHub.Feeds;

/// <summary>Quantity observed at the server's UTC timestamp, not at local receipt time.</summary>
public readonly record struct OpenInterestSample(DateTime Time, decimal Quantity);
public enum OpenInterestAvailability { Unsupported, Loading, Available, Partial, Unavailable, Error }
public sealed record OpenInterestHistoryResult(
    IReadOnlyList<OpenInterestSample> Samples,
    DateTime? CoverageFrom, DateTime? CoverageTo,
    OpenInterestAvailability Availability, string? Message = null);

public interface IOpenInterestObserver
{
    void OnOpenInterest(OpenInterestSample sample);
    void OnOpenInterestStatus(OpenInterestAvailability availability, string? message);
}

/// <summary>Optional capability; no change to ordinary trade/depth sinks.</summary>
public interface IOpenInterestProvider
{
    bool SupportsOpenInterest(Instrument instrument);
    TimeSpan OpenInterestRetention { get; }
    TimeSpan OpenInterestMaxAge { get; }
    Task<OpenInterestHistoryResult> LoadOpenInterestAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct);
    IDisposable SubscribeOpenInterest(Instrument instrument, IOpenInterestObserver observer);
}
