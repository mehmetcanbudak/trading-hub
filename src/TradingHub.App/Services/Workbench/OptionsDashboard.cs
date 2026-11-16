using TradingHub.Feeds.External;

namespace TradingHub.App.Services.Workbench;

public sealed record OptionsStrikeRow(double Strike, double Gex, double CallOi, double PutOi);

public sealed record OptionsExpiryRow(DateTime Expiry, double DaysLeft, double CallOi, double PutOi, double? PutCallOi, double? MaxPain,
    double? AtmIv, double? RiskReversal25, double? Butterfly25, double? ExpectedMove);

/// <summary>
/// The options dashboard of one chain snapshot. GEX follows <see cref="OptionsMath"/>: USD of the underlying dealers
/// trade per 1 % move under the customers-sell-calls/buy-puts convention, an estimate from public positioning. Values
/// that cannot be computed are null (no sign change, no quotes), never zero.
/// </summary>
public sealed record OptionsSummary(string Underlying, string Source, DateTime Time, double Spot, int Contracts, double TotalGex,
    double? GammaFlip, double? CallWall, double? PutWall, double? MaxPain, double? ExpectedMove, DateTime? NearestExpiry,
    IReadOnlyList<OptionsStrikeRow> Strikes, IReadOnlyList<(double Spot, double Gex)> GexCurve, IReadOnlyList<(DateTime Expiry, double Days, double AtmIv)> Term,
    IReadOnlyList<(double Strike, double Iv)> Smile, DateTime? SmileExpiry, IReadOnlyList<OptionsExpiryRow> Expiries);

/// <summary>
/// A published volatility index over completed UTC days, oldest first. A day without a published close is null (a gap,
/// never zero); <see cref="Today"/> is the forming day's latest value, kept apart from the completed closes.
/// </summary>
public sealed record VolatilityIndexWindow(string Name, DateTime FirstDay, IReadOnlyList<double?> Closes, int MissingDays, double? Today);

public static class OptionsDashboard
{
    private static double? Finite(double v) => double.IsFinite(v) ? v : null;

    /// <summary>
    /// The last <paramref name="days"/> completed UTC days of a volatility index (percent points) ending the day before
    /// <paramref name="now"/>. Non-finite or non-positive values count as missing.
    /// </summary>
    public static VolatilityIndexWindow VolatilityIndex(string name, IEnumerable<(DateTime Day, double Value)> points, DateTime now, int days = 30)
    {
        var today = now.Date;
        var first = today.AddDays(-days);
        var byDay = new Dictionary<DateTime, double>();
        foreach (var (day, value) in points)
            if (double.IsFinite(value) && value > 0) byDay[day.Date] = value;
        var closes = new double?[days];
        for (int i = 0; i < days; i++) closes[i] = byDay.TryGetValue(first.AddDays(i), out var v) ? v : null;
        return new VolatilityIndexWindow(name, first, closes, closes.Count(c => c == null), byDay.TryGetValue(today, out var t) ? t : null);
    }

    /// <summary>
    /// Summarizes a chain. <paramref name="maxDays"/> keeps expiries within that many days (null = all);
    /// <paramref name="expiry"/> selects one expiry instead. Strikes are kept within ±<paramref name="rangePercent"/> of spot.
    /// </summary>
    public static OptionsSummary Build(OptionsChain chain, double rangePercent, int? maxDays = null, DateTime? expiry = null)
    {
        var contracts = chain.Contracts.Where(o => (expiry == null || o.Expiry == expiry) &&
            (maxDays == null || (o.Expiry - chain.Time).TotalDays <= maxDays.Value) && o.Expiry > chain.Time).ToList();
        var c = chain with { Contracts = contracts };
        var gex = contracts.Count > 0 ? OptionsMath.GexByStrike(c) : new SortedDictionary<double, double>();
        double low = chain.Spot * (1 - rangePercent / 100), high = chain.Spot * (1 + rangePercent / 100);
        var strikes = contracts.GroupBy(o => o.Strike).Where(g => g.Key >= low && g.Key <= high).OrderBy(g => g.Key)
            .Select(g => new OptionsStrikeRow(g.Key, gex.TryGetValue(g.Key, out var x) ? x : 0,
                g.Where(o => o.Right == OptionRight.Call).Sum(o => o.OpenInterest), g.Where(o => o.Right == OptionRight.Put).Sum(o => o.OpenInterest))).ToArray();
        var curve = new List<(double Spot, double Gex)>();
        if (contracts.Count > 0)
            for (int k = -40; k <= 40; k++)
            {
                double s = chain.Spot * (1 + k * 0.005);
                curve.Add((s, OptionsMath.TotalGex(c, s)));
            }
        var (callWall, putWall) = contracts.Count > 0 ? OptionsMath.Walls(c) : (double.NaN, double.NaN);
        var expiries = contracts.Select(o => o.Expiry).Distinct().OrderBy(e => e).ToArray();
        var rows = expiries.Select(e => Row(c, e)).ToArray();
        DateTime? nearest = expiries.FirstOrDefault(e => (e - chain.Time).TotalHours >= 12) is var n && n != default ? n : expiries.Length > 0 ? expiries[0] : null;
        var term = expiries.Select(e => (e, (e - chain.Time).TotalDays, OptionsMath.AtmIv(c, e))).Where(t => double.IsFinite(t.Item3)).ToArray();
        DateTime? smileExpiry = expiry ?? (contracts.Count > 0 ? OptionsMath.ExpiryNear(c, 30) : null);
        var smile = smileExpiry is { } se
            ? contracts.Where(o => o.Expiry == se && o.Iv > 0 && (o.Right == OptionRight.Call ? o.Strike >= chain.Spot : o.Strike <= chain.Spot) && o.Strike >= low && o.Strike <= high)
                .GroupBy(o => o.Strike).OrderBy(g => g.Key).Select(g => (g.Key, g.Average(o => o.Iv))).ToArray()
            : Array.Empty<(double, double)>();
        return new OptionsSummary(chain.Underlying, chain.Source, chain.Time, chain.Spot, contracts.Count, gex.Values.Sum(),
            contracts.Count > 0 ? Finite(OptionsMath.GammaFlip(c)) : null, Finite(callWall), Finite(putWall),
            contracts.Count > 0 ? Finite(OptionsMath.MaxPain(c, nearest)) : null,
            nearest is { } ne ? Finite(OptionsMath.ExpectedMove(c, ne)) : null, nearest, strikes, curve, term, smile, smileExpiry, rows);
    }

    public static OptionsExpiryRow Row(OptionsChain c, DateTime expiry)
    {
        var set = c.Contracts.Where(o => o.Expiry == expiry).ToArray();
        double calls = set.Where(o => o.Right == OptionRight.Call).Sum(o => o.OpenInterest);
        double puts = set.Where(o => o.Right == OptionRight.Put).Sum(o => o.OpenInterest);
        return new OptionsExpiryRow(expiry, (expiry - c.Time).TotalDays, calls, puts, calls > 0 ? puts / calls : null,
            Finite(OptionsMath.MaxPain(c, expiry)), Finite(OptionsMath.AtmIv(c, expiry)) is { } iv ? iv * 100 : null,
            Finite(OptionsMath.RiskReversal25(c, expiry)), Butterfly25(c, expiry), Finite(OptionsMath.ExpectedMove(c, expiry)));
    }

    /// <summary>25-delta butterfly in volatility points: mean IV of the ±25-delta wings minus ATM IV.</summary>
    public static double? Butterfly25(OptionsChain c, DateTime expiry)
    {
        double t = Math.Max(1.0 / 365, (expiry - c.Time).TotalDays / 365);
        OptionContract? Near(OptionRight r, double target) => c.Contracts.Where(o => o.Expiry == expiry && o.Right == r && o.Iv > 0)
            .OrderBy(o => Math.Abs((o.Delta != 0 ? o.Delta : OptionsMath.Greeks(c.Spot, o.Strike, t, o.Iv, r).Delta) - target)).FirstOrDefault();
        var call = Near(OptionRight.Call, 0.25);
        var put = Near(OptionRight.Put, -0.25);
        double atm = OptionsMath.AtmIv(c, expiry);
        if (call == null || put == null || !double.IsFinite(atm)) return null;
        return ((call.Iv + put.Iv) / 2 - atm) * 100;
    }
}
