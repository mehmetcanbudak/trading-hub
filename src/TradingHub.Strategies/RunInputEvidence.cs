using TradingHub.Market;
using Quality = TradingHub.Market.DataQuality;

namespace TradingHub.Strategies;

/// <summary>Evidence captured from the input used by a run. A missing value on a saved run means unknown legacy evidence.</summary>
public sealed record RunInputEvidence(
    DataQuality DataQuality,
    int ResolutionSeconds,
    string? SourceIdentity,
    long CorrectionRevision,
    bool HasKnownAvailability,
    string? DataWarning,
    string? ContentIdentity)
{
    public static RunInputEvidence Capture(BacktestResult result, TickData data) => new(
        result.DataQuality, result.ResolutionSeconds, result.SourceIdentity, result.CorrectionRevision,
        result.HasKnownAvailability, result.DataWarning, data.ContentIdentity);

    public static RunInputEvidence Capture(TickData data)
    {
        string? warning = !data.HasKnownAvailability
            ? "Source availability is unknown. Reimport declared source timing before running a causal backtest."
            : (data.Quality & (Quality.Synthetic | Quality.Compact)) != 0
                ? "This run contains compact or synthetic events, not individual observed trades. Input-event counts and intrabar paths may be estimates. Orders cannot fill from interval facts predating their submission; same-window anchors cannot execute a newly learned close signal."
                : (data.Quality & Quality.Partial) != 0 || data.ResolutionSeconds > 0
                    ? "This run contains partial coverage or interval-resolved timing. Event ordering and coverage must not be interpreted as a complete individual-trade tape."
                    : null;
        return new(data.Quality, data.ResolutionSeconds, data.SourceIdentity, data.CorrectionRevision,
            data.HasKnownAvailability, warning, data.ContentIdentity);
    }
}
