// Behavior adapted from the KVP capability reference (commit 7fbf237); see THIRD_PARTY_NOTICES.md.
namespace TradingHub.Feeds.External;

public enum OptionRight
{
    Call,
    Put,
}

/// <summary>
/// One listed option. Units: <see cref="Iv"/> is a fraction (0.55 = 55 %); <see cref="Bid"/>, <see cref="Ask"/> and
/// <see cref="Mark"/> are USD per unit of the underlying; Black–Scholes <see cref="Delta"/> and <see cref="Gamma"/> are per
/// unit of the underlying (0 = not supplied, computed from the IV); <see cref="Vega"/> is USD per volatility point per
/// unit; <see cref="OpenInterest"/> and <see cref="Volume"/> are contracts; <see cref="ContractSize"/> is the contract
/// multiplier in units of the underlying (1 for Deribit, 100 for US equity options).
/// </summary>
public sealed record OptionContract(
    string Symbol, DateTime Expiry, double Strike, OptionRight Right,
    double Bid, double Ask, double Mark, double Iv,
    double Delta, double Gamma, double Vega, double Theta,
    double OpenInterest, double Volume, double ContractSize)
{
    /// <summary>Days to expiry, at least one hour.</summary>
    public double DaysToExpiry(DateTime now) => Math.Max(1.0 / 24, (Expiry - now).TotalDays);
}

/// <summary>
/// One option-chain snapshot. <see cref="Time"/> is the provider's snapshot time (UTC); <see cref="Spot"/> is the
/// reference price named by <see cref="SpotReference"/> at that time; <see cref="Source"/> names the provider.
/// </summary>
public sealed record OptionsChain(string Underlying, DateTime Time, double Spot, IReadOnlyList<OptionContract> Contracts,
    string Source = "", string SpotReference = "");

/// <summary>
/// One option execution. <see cref="Price"/> is USD per unit of the underlying, <see cref="Size"/> is contracts and
/// <see cref="Aggressor"/> is +1 (bought at the ask), −1 (sold at the bid) or 0 (unknown side).
/// </summary>
public sealed record OptionTrade(DateTime Time, string Symbol, DateTime Expiry, double Strike, OptionRight Right, double Price, double Size,
    int Aggressor, double Iv, double Spot, double ContractSize, string Venue = "")
{
    /// <summary>+1 for bought calls or sold puts, −1 for bought puts or sold calls, 0 when the side is unknown.</summary>
    public int Bias => Aggressor * (Right == OptionRight.Call ? 1 : -1);

    /// <summary>Black–Scholes delta with the trade's IV (60 % when the provider gave none) at the trade's spot.</summary>
    public double Delta => OptionsMath.Greeks(Spot, Strike, Math.Max(1.0 / 365, (Expiry - Time).TotalDays / 365), Iv > 0 ? Iv : 0.6, Right).Delta;

    /// <summary>Premium paid in USD: price × contracts × contract multiplier.</summary>
    public double Premium => Price * Size * ContractSize;
}

/// <summary>
/// One UTC day of an underlying's option statistics. Values are known only after the day ends (a provider's daily
/// close or the last recorded snapshot of that day), so studies use row D from day D+1. NaN or 0 means unknown.
/// IV and the 25-delta risk reversal (<see cref="Rr25"/>, volatility points) follow the units of
/// <see cref="OptionContract"/>; <see cref="Gex"/> is USD per 1 % move.
/// </summary>
public sealed record OptionDaily(
    DateTime Date, double Spot, double AtmIv, double Rr25, double PutCallVolume, double PutCallOi,
    double CallOi, double PutOi, double CallVolume, double PutVolume,
    double Gex, double CallWall, double PutWall, double GammaFlip, double ExpectedMove);

/// <summary>One strike in a recorded snapshot: open interest, dealer gamma (USD per 1 %) and volume.</summary>
public sealed record StrikeRow(double Strike, double CallOi, double PutOi, double Gex, double CallVolume, double PutVolume);

/// <summary>Per-strike state of the chain at one recorded instant.</summary>
public sealed record StrikeSnapshot(DateTime Time, double Spot, IReadOnlyList<StrikeRow> Rows);

/// <summary>An options data source configured on a Connections profile.</summary>
public interface IOptionsProvider
{
    string Name { get; }

    /// <summary>Whether the source lists options on the underlying (BTC, ETH, SPY…).</summary>
    bool Supports(string underlying);

    Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct);

    Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct);

    /// <summary>Daily history; may be empty, in which case recorded daily snapshots fill it.</summary>
    Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// Trades of one request window. Every trade in [from, <see cref="CoveredTo"/>) is included; when
/// <see cref="CoveredTo"/> is earlier than the requested end, a page limit stopped the request there and the rest of the
/// window is unknown, not empty.
/// </summary>
public sealed record OptionTradeWindow(IReadOnlyList<OptionTrade> Trades, DateTime CoveredTo);

/// <summary>A provider whose trade requests can stop early at a page limit and say where coverage ends.</summary>
public interface IOptionTradeWindows
{
    /// <summary>Most trades one request window returns before it stops.</summary>
    int TradesPerWindow { get; }

    Task<OptionTradeWindow> TradesWindowAsync(string underlying, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// A provider whose own daily rows (<see cref="IOptionsProvider.DailyAsync"/>) carry a published implied-volatility index
/// in <see cref="OptionDaily.AtmIv"/> (Deribit's DVOL), not an ATM IV measured from the chain.
/// </summary>
public interface IVolatilityIndexSource
{
    /// <summary>The index's published name (for example DVOL).</summary>
    string VolatilityIndexName { get; }
}

/// <summary>
/// Daily closes of a provider's published volatility index in percent points, oldest first. <see cref="Points"/> is null
/// until the provider's daily history has been fetched; <see cref="Error"/> is the last failed request.
/// </summary>
public sealed record VolatilityIndexSeries(string Name, IReadOnlyList<(DateTime Day, double Value)>? Points, string? Error);
