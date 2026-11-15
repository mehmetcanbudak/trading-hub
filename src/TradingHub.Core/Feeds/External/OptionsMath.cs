// Behavior adapted from the KVP capability reference (commit 7fbf237); see THIRD_PARTY_NOTICES.md.
namespace TradingHub.Feeds.External;

/// <summary>
/// Option formulas used by the options studies. Black–Scholes with a zero rate and no dividends: time <c>t</c> in years
/// (calendar days / 365), volatility as a fraction. Exposures follow the common dealer convention that customers buy puts
/// and sell calls, so dealers are long call gamma and short put gamma:
/// <list type="bullet">
/// <item>GEX per contract = Γ × open interest × multiplier × S² × 1 % (USD of underlying dealers trade per 1 % move); calls +, puts −.</item>
/// <item>DEX per contract = Δ × open interest × multiplier × S (USD delta notional).</item>
/// <item>Vega exposure = vega per volatility point × open interest × multiplier.</item>
/// <item>Expected move to expiry = S × σ_ATM × √t (one standard deviation).</item>
/// </list>
/// S is the chain's spot reference at the snapshot time. These are estimates from public positioning, not dealer books.
/// </summary>
public static class OptionsMath
{
    private static double Pdf(double x) => Math.Exp(-x * x / 2) / Math.Sqrt(2 * Math.PI);

    /// <summary>Standard normal CDF (Abramowitz–Stegun 26.2.17, absolute error below 7.5e-8).</summary>
    public static double Cdf(double x)
    {
        double t = 1 / (1 + 0.2316419 * Math.Abs(x));
        double d = 0.3989422804014327 * Math.Exp(-x * x / 2);
        double p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        return x > 0 ? 1 - p : p;
    }

    private static double D1(double spot, double strike, double t, double iv) => (Math.Log(spot / strike) + iv * iv / 2 * t) / (iv * Math.Sqrt(t));

    /// <summary>Black–Scholes delta and gamma per unit of the underlying; intrinsic delta and zero gamma when inputs are degenerate.</summary>
    public static (double Delta, double Gamma) Greeks(double spot, double strike, double t, double iv, OptionRight right)
    {
        if (spot <= 0 || strike <= 0 || t <= 0 || iv <= 0) return (right == OptionRight.Call ? (spot > strike ? 1 : 0) : (spot < strike ? -1 : 0), 0);
        double d1 = D1(spot, strike, t, iv);
        double delta = right == OptionRight.Call ? Cdf(d1) : Cdf(d1) - 1;
        return (delta, Pdf(d1) / (spot * iv * Math.Sqrt(t)));
    }

    /// <summary>Black–Scholes vega in USD per volatility point (1 %) per unit of the underlying.</summary>
    public static double Vega(double spot, double strike, double t, double iv)
    {
        if (spot <= 0 || strike <= 0 || t <= 0 || iv <= 0) return 0;
        return spot * Pdf(D1(spot, strike, t, iv)) * Math.Sqrt(t) / 100;
    }

    /// <summary>Risk-neutral probability that the price ends above <paramref name="level"/> at expiry: N(d2).</summary>
    public static double ProbAbove(double spot, double level, double t, double iv)
    {
        if (spot <= 0 || level <= 0 || t <= 0 || iv <= 0) return spot > level ? 1 : 0;
        double d2 = (Math.Log(spot / level) - iv * iv / 2 * t) / (iv * Math.Sqrt(t));
        return Cdf(d2);
    }

    /// <summary>Smile IV at a strike: linear between out-of-the-money strikes of that expiry, flat beyond the ends.</summary>
    public static double SmileIv(OptionsChain c, DateTime expiry, double strike)
    {
        var pts = c.Contracts.Where(o => o.Expiry == expiry && o.Iv > 0 && (o.Right == OptionRight.Call ? o.Strike >= c.Spot : o.Strike <= c.Spot))
            .GroupBy(o => o.Strike).Select(g => (K: g.Key, Iv: g.Average(o => o.Iv))).OrderBy(x => x.K).ToList();
        if (pts.Count == 0) return AtmIv(c, expiry);
        if (strike <= pts[0].K) return pts[0].Iv;
        if (strike >= pts[^1].K) return pts[^1].Iv;
        for (int i = 1; i < pts.Count; i++)
            if (strike <= pts[i].K)
            {
                double w = (strike - pts[i - 1].K) / Math.Max(1e-9, pts[i].K - pts[i - 1].K);
                return pts[i - 1].Iv + w * (pts[i].Iv - pts[i - 1].Iv);
            }

        return pts[^1].Iv;
    }

    /// <summary>
    /// Max pain: the listed strike where option holders' total intrinsic value at expiry is lowest,
    /// Σ OI × multiplier × max(0, K − strike) for calls and max(0, strike − K) for puts. NaN without contracts.
    /// </summary>
    public static double MaxPain(OptionsChain c, DateTime? expiry)
    {
        var set = c.Contracts.Where(o => expiry == null || o.Expiry == expiry).ToList();
        double best = double.NaN, bestPay = double.MaxValue;
        foreach (var k in set.Select(o => o.Strike).Distinct().OrderBy(k => k))
        {
            double pay = 0;
            foreach (var o in set) pay += o.OpenInterest * o.ContractSize * (o.Right == OptionRight.Call ? Math.Max(0, k - o.Strike) : Math.Max(0, o.Strike - k));
            if (pay < bestPay)
            {
                bestPay = pay;
                best = k;
            }
        }

        return best;
    }

    /// <summary>The contract's gamma: the provider's when supplied, otherwise Black–Scholes from its IV at <paramref name="spot"/>.</summary>
    public static double GammaOf(OptionContract c, double spot, DateTime now) =>
        c.Gamma > 0 ? c.Gamma : Greeks(spot, c.Strike, c.DaysToExpiry(now) / 365, c.Iv, c.Right).Gamma;

    /// <summary>The contract's delta: the provider's when supplied, otherwise Black–Scholes from its IV.</summary>
    public static double DeltaOf(OptionContract c, double spot, DateTime now) =>
        c.Delta != 0 ? c.Delta : Greeks(spot, c.Strike, c.DaysToExpiry(now) / 365, c.Iv, c.Right).Delta;

    /// <summary>Dealer gamma exposure of one contract in USD per 1 % move (calls +, puts −).</summary>
    public static double Gex(double gamma, OptionContract o, double spot) =>
        gamma * o.OpenInterest * o.ContractSize * spot * spot * 0.01 * (o.Right == OptionRight.Call ? 1 : -1);

    /// <summary>
    /// Dealer gamma exposure by strike in USD per 1 % move. With <paramref name="atSpot"/>, every contract's gamma is
    /// recomputed from its IV as if the underlying were there (provider gammas belong to the snapshot spot).
    /// </summary>
    public static SortedDictionary<double, double> GexByStrike(OptionsChain c, double? atSpot = null, int maxDays = 400)
    {
        double s = atSpot ?? c.Spot;
        var res = new SortedDictionary<double, double>();
        foreach (var o in c.Contracts)
        {
            if (o.DaysToExpiry(c.Time) > maxDays) continue;
            double g = atSpot == null ? GammaOf(o, s, c.Time) : Greeks(s, o.Strike, o.DaysToExpiry(c.Time) / 365, o.Iv, o.Right).Gamma;
            res[o.Strike] = (res.TryGetValue(o.Strike, out var x) ? x : 0) + Gex(g, o, s);
        }

        return res;
    }

    public static double TotalGex(OptionsChain c, double? atSpot = null) => GexByStrike(c, atSpot).Values.Sum();

    /// <summary>
    /// The spot where total dealer gamma changes sign, scanning ±20 % around the snapshot spot in 0.5 % steps and
    /// interpolating linearly inside the crossing step. NaN when it does not change sign in that range.
    /// </summary>
    public static double GammaFlip(OptionsChain c)
    {
        double prev = double.NaN, prevS = double.NaN;
        for (int k = -40; k <= 40; k++)
        {
            double s = c.Spot * (1 + k * 0.005), g = TotalGex(c, s);
            if (!double.IsNaN(prev) && g != 0 && prev != 0 && Math.Sign(g) != Math.Sign(prev))
                return prevS + (s - prevS) * Math.Abs(prev) / (Math.Abs(prev) + Math.Abs(g));
            prev = g;
            prevS = s;
        }

        return double.NaN;
    }

    /// <summary>Call wall: the strike at or above spot with the largest positive GEX. Put wall: at or below spot with the most negative GEX.</summary>
    public static (double Call, double Put) Walls(OptionsChain c)
    {
        var g = GexByStrike(c);
        double call = g.Where(kv => kv.Key >= c.Spot && kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).DefaultIfEmpty(double.NaN).First();
        double put = g.Where(kv => kv.Key <= c.Spot && kv.Value < 0).OrderBy(kv => kv.Value).Select(kv => kv.Key).DefaultIfEmpty(double.NaN).First();
        return (call, put);
    }

    /// <summary>The expiry closest to <paramref name="days"/> days among those at least 12 hours away; the snapshot time when none.</summary>
    public static DateTime ExpiryNear(OptionsChain c, double days) =>
        c.Contracts.Select(o => o.Expiry).Distinct().Where(e => (e - c.Time).TotalDays >= 0.5).OrderBy(e => Math.Abs((e - c.Time).TotalDays - days)).ThenBy(e => e)
            .DefaultIfEmpty(c.Time).First();

    /// <summary>At-the-money IV of an expiry: the mean IV of the two quoted contracts whose strikes are nearest to spot.</summary>
    public static double AtmIv(OptionsChain c, DateTime expiry)
    {
        var at = c.Contracts.Where(o => o.Expiry == expiry && o.Iv > 0).OrderBy(o => Math.Abs(o.Strike - c.Spot)).ThenBy(o => o.Right).Take(2).ToList();
        return at.Count == 0 ? double.NaN : at.Average(o => o.Iv);
    }

    /// <summary>25-delta risk reversal: IV of the call nearest +0.25 delta minus the put nearest −0.25 delta, in volatility points.</summary>
    public static double RiskReversal25(OptionsChain c, DateTime expiry)
    {
        double t = Math.Max(1.0 / 365, (expiry - c.Time).TotalDays / 365);
        OptionContract? Near(OptionRight r, double target) => c.Contracts.Where(o => o.Expiry == expiry && o.Right == r && o.Iv > 0)
            .OrderBy(o => Math.Abs((o.Delta != 0 ? o.Delta : Greeks(c.Spot, o.Strike, t, o.Iv, r).Delta) - target)).FirstOrDefault();
        var call = Near(OptionRight.Call, 0.25);
        var put = Near(OptionRight.Put, -0.25);
        return call == null || put == null ? double.NaN : (call.Iv - put.Iv) * 100;
    }

    /// <summary>One-standard-deviation move to <paramref name="expiry"/> in price: S × σ_ATM × √t.</summary>
    public static double ExpectedMove(OptionsChain c, DateTime expiry)
    {
        double iv = AtmIv(c, expiry);
        return double.IsNaN(iv) ? double.NaN : c.Spot * iv * Math.Sqrt(Math.Max(1.0 / 365, (expiry - c.Time).TotalDays / 365));
    }

    /// <summary>The daily summary of one snapshot (dated by the snapshot's UTC day).</summary>
    public static OptionDaily DailyOf(OptionsChain c)
    {
        var e30 = ExpiryNear(c, 30);
        var e1 = ExpiryNear(c, 1);
        double callOi = 0, putOi = 0, callVol = 0, putVol = 0;
        foreach (var o in c.Contracts)
        {
            if (o.Right == OptionRight.Call) { callOi += o.OpenInterest; callVol += o.Volume; }
            else { putOi += o.OpenInterest; putVol += o.Volume; }
        }

        var (cw, pw) = Walls(c);
        return new OptionDaily(c.Time.Date, c.Spot, AtmIv(c, e30), RiskReversal25(c, e30), callVol > 0 ? putVol / callVol : double.NaN, callOi > 0 ? putOi / callOi : double.NaN,
            callOi, putOi, callVol, putVol, TotalGex(c), cw, pw, GammaFlip(c), ExpectedMove(c, e1));
    }

    /// <summary>Per-strike open interest, gamma exposure and volume of a snapshot.</summary>
    public static StrikeSnapshot SnapshotOf(OptionsChain c)
    {
        var gex = GexByStrike(c);
        var rows = c.Contracts.GroupBy(o => o.Strike).OrderBy(g => g.Key).Select(g => new StrikeRow(g.Key,
            g.Where(o => o.Right == OptionRight.Call).Sum(o => o.OpenInterest), g.Where(o => o.Right == OptionRight.Put).Sum(o => o.OpenInterest),
            gex.TryGetValue(g.Key, out var x) ? x : 0,
            g.Where(o => o.Right == OptionRight.Call).Sum(o => o.Volume), g.Where(o => o.Right == OptionRight.Put).Sum(o => o.Volume))).ToList();
        return new StrikeSnapshot(c.Time, c.Spot, rows);
    }

    /// <summary>Where <paramref name="v"/> ranks among <paramref name="values"/>, 0–100 (ties count half). NaN with fewer than two values.</summary>
    public static double Percentile(IEnumerable<double> values, double v)
    {
        var list = values.Where(double.IsFinite).ToList();
        if (list.Count < 2 || !double.IsFinite(v)) return double.NaN;
        return 100.0 * list.Count(x => x < v) / list.Count + 50.0 * list.Count(x => x == v) / list.Count;
    }

    /// <summary>IV rank: (v − min) / (max − min) × 100 over <paramref name="values"/>, clamped to 0–100; NaN with fewer than two values.</summary>
    public static double Rank(IEnumerable<double> values, double v)
    {
        var list = values.Where(double.IsFinite).ToList();
        if (list.Count < 2 || !double.IsFinite(v)) return double.NaN;
        double lo = list.Min(), hi = list.Max();
        return hi > lo ? Math.Clamp((v - lo) / (hi - lo) * 100, 0, 100) : 50;
    }

    /// <summary>Annualized close-to-close realized volatility (sample standard deviation of log returns × √periods per year).</summary>
    public static double RealizedVol(IReadOnlyList<double> closes, double periodsPerYear)
    {
        if (closes.Count < 3) return double.NaN;
        var r = new List<double>(closes.Count - 1);
        for (int i = 1; i < closes.Count; i++)
            if (closes[i] > 0 && closes[i - 1] > 0) r.Add(Math.Log(closes[i] / closes[i - 1]));
        if (r.Count < 2) return double.NaN;
        double m = r.Average(), v = r.Sum(x => (x - m) * (x - m)) / (r.Count - 1);
        return Math.Sqrt(v * periodsPerYear);
    }
}
