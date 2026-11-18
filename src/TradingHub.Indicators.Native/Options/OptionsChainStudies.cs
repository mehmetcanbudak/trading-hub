// Behavior adapted from the KVP capability reference (commits 7fbf237, 644b8ca); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds.External;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Options;

// Studies of the current chain snapshot: dealer gamma, greek exposure by strike, gamma by price, strike lanes, strike
// distribution and max pain, most-traded contracts, volatility surface, probability cloud, expected move, one
// contract's odds, stance, dashboard, volatility context, per-bar gamma map and open-interest changes. Each level is drawn
// from the bar where its snapshot became known; card footers name the source and snapshot time.

[DisplayName("Dealer positioning (estimated)")]
[Category(IndicatorCategories.Options)]
[Description("Estimates dealer gamma from the option chain every 10 minutes (assuming customers buy puts and sell calls). Positive gamma means dealer hedging dampens moves (they sell rallies and buy dips); negative gamma amplifies them. The card shows how much dealers would trade per 1 % move; lines mark the gamma flip and the call and put walls, with their history since snapshots were recorded.")]
public class DealerPositioning : OptionsOverlay
{
    protected override TimeSpan ChainAge => TimeSpan.FromMinutes(10);

    [Display(Name = "Wall history", GroupName = "Drawing", Order = 20)]
    public bool ShowHistory { get; set; } = true;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        double gex = OptionsMath.TotalGex(c), flip = OptionsMath.GammaFlip(c);
        var (cw, pw) = OptionsMath.Walls(c);
        int from = Math.Max(0, BarOf(c.Time) is >= 0 and var b ? b : LastBar);
        s.Line(from, -1, flip, Opt.Flip, 2, LineDashStyle.Dash, $"Gamma flip {Price(flip)}");
        s.Line(from, -1, cw, Opt.Call, 2, LineDashStyle.Solid, $"Call wall {Price(cw)}");
        s.Line(from, -1, pw, Opt.Put, 2, LineDashStyle.Solid, $"Put wall {Price(pw)}");
        if (ShowHistory)
        {
            var callPath = new List<(int, double)>();
            var putPath = new List<(int, double)>();
            foreach (var sn in data.Snapshots(Underlying))
            {
                int bar = BarOf(sn.Time);
                if (bar < 0) continue;
                var call = sn.Rows.Where(r => r.Strike >= sn.Spot && r.Gex > 0).OrderByDescending(r => r.Gex).FirstOrDefault();
                var put = sn.Rows.Where(r => r.Strike <= sn.Spot && r.Gex < 0).OrderBy(r => r.Gex).FirstOrDefault();
                if (call != null) callPath.Add((bar, call.Strike));
                if (put != null) putPath.Add((bar, put.Strike));
            }

            if (callPath.Count > 1) s.Paths.Add(new OptionsScene.PathLine(callPath, Opt.A(Opt.Call, 130), 1, LineDashStyle.Dot));
            if (putPath.Count > 1) s.Paths.Add(new OptionsScene.PathLine(putPath, Opt.A(Opt.Put, 130), 1, LineDashStyle.Dot));
        }

        string amount = Opt.Usd(Math.Abs(gex));
        s.Title = $"Dealers · {Underlying}";
        s.Row("Total gamma (per 1 %)", Opt.SignedUsd(gex), Opt.BiasColor(gex));
        s.Row("Regime", gex >= 0 ? "positive gamma: dampening" : "negative gamma: amplifying", gex >= 0 ? Opt.Call : Opt.Put);
        s.Row("If price rises 1 %", gex >= 0 ? $"sell ≈ {amount}" : $"buy ≈ {amount}");
        s.Row("If price falls 1 %", gex >= 0 ? $"buy ≈ {amount}" : $"sell ≈ {amount}");
        if (double.IsFinite(flip)) s.Row("Gamma flip", Price(flip) + "  (" + Opt.Pct((flip / c.Spot - 1) * 100, "+0.0;-0.0") + ")", Opt.Flip);
        s.Footer = StudyCard.Provenance(c);
    }
}

[DisplayName("Greek exposure by strike")]
[Category(IndicatorCategories.Options)]
[Description("Where dealer hedging concentrates in the current snapshot: bars per strike docked at the right of the price pane (gamma or delta signed; vega or open interest with calls right and puts left) and a card with the expiries carrying the most exposure. GEX = Γ × OI × multiplier × S² × 1 %; DEX = Δ × OI × multiplier × S.")]
public class GreekExposure : OptionsOverlay
{
    public enum Greek
    {
        [Display(Name = "Gamma (GEX)")] Gamma,
        [Display(Name = "Delta (DEX)")] Delta,
        [Display(Name = "Vega")] Vega,
        [Display(Name = "Open interest")] OpenInterest,
    }

    private List<(double Strike, double Left, double Right)> _rows = new();
    private double _step;

    [Display(Name = "Greek", GroupName = "Calculation", Order = 10)]
    public Greek Measure { get; set; } = Greek.Gamma;

    [Display(Name = "Expiry", GroupName = "Calculation", Order = 11)]
    public ExpiryPick Expiry { get; set; } = ExpiryPick.All;

    [Display(Name = "Bar width (px)", GroupName = "Drawing", Order = 20)]
    [Range(40, 600)]
    public int Width { get; set; } = 160;

    private bool Signed => Measure is Greek.Gamma or Greek.Delta;

    /// <summary>The exposure of one contract in USD (open interest: USD notional).</summary>
    internal static double Value(Greek measure, OptionContract o, OptionsChain c)
    {
        double t = o.DaysToExpiry(c.Time) / 365, s = c.Spot, n = o.OpenInterest * o.ContractSize;
        return measure switch
        {
            Greek.Gamma => OptionsMath.Gex(OptionsMath.GammaOf(o, s, c.Time), o, s),
            Greek.Delta => OptionsMath.DeltaOf(o, s, c.Time) * n * s,
            Greek.Vega => (o.Vega > 0 ? o.Vega : OptionsMath.Vega(s, o.Strike, t, o.Iv)) * n,
            _ => n * s,
        };
    }

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        var exp = Opt.Pick(c, Expiry);
        var by = new SortedDictionary<double, (double L, double R)>();
        var byExp = new Dictionary<DateTime, double>();
        foreach (var o in Opt.Of(c, exp))
        {
            double v = Value(Measure, o, c);
            var cur = by.TryGetValue(o.Strike, out var x) ? x : (0, 0);
            by[o.Strike] = Signed ? (0, cur.R + v) : o.Right == OptionRight.Call ? (cur.L, cur.R + v) : (cur.L + v, cur.R);
            byExp[o.Expiry] = (byExp.TryGetValue(o.Expiry, out var e) ? e : 0) + (Signed ? v : Math.Abs(v));
        }

        _rows = by.Where(kv => Math.Abs(kv.Key / c.Spot - 1) < 0.35).Select(kv => (kv.Key, kv.Value.L, kv.Value.R)).ToList();
        _step = Opt.StrikeStep(_rows.Select(r => r.Strike), c.Spot);
        var top = _rows.OrderByDescending(r => Math.Abs(r.Left) + Math.Abs(r.Right)).FirstOrDefault();
        if (top.Strike > 0) s.Line(Math.Max(0, LastBar - 40), -1, top.Strike, Opt.Flip, 1, LineDashStyle.Dash, $"Largest exposure {Price(top.Strike)}");
        string name = Measure switch { Greek.Gamma => "Gamma", Greek.Delta => "Delta", Greek.Vega => "Vega", _ => "Open interest" };
        s.Title = $"{UiText.Tr(name)} · {Underlying} · {Opt.ExpiryText(exp)}";
        double total = byExp.Values.Sum();
        s.Row("Total", Signed ? Opt.SignedUsd(total) : Opt.Usd(total), Opt.BiasColor(Signed ? total : 0));
        foreach (var kv in byExp.OrderByDescending(kv => Math.Abs(kv.Value)).Take(6))
            s.Row(kv.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + $" ({(kv.Key - c.Time).TotalDays:0} d)", Signed ? Opt.SignedUsd(kv.Value) : Opt.Usd(kv.Value),
                Opt.BiasColor(Signed ? kv.Value : 0));
        s.Footer = StudyCard.Provenance(c);
    }

    protected override void RenderAfter(RenderContext ctx, Rectangle region) =>
        Opt.DockBars(ctx, region, Y, _rows, Width, _step, Opt.A(Opt.Put, 190), Opt.A(Opt.Call, 190), Signed);
}

[DisplayName("Strike matrix (dealer gamma by price)")]
[Category(IndicatorCategories.Options)]
[Description("A strip docked to the price axis with total dealer gamma recomputed as if the underlying traded at each price (±15 %): green where dealers dampen, red where they push. Dashed lines at the gamma flip, the walls and the strike with the most gamma.")]
public class StrikeMatrix : OptionsOverlay
{
    private readonly List<(double Price, double Gex)> _curve = new();

    protected override TimeSpan ChainAge => TimeSpan.FromMinutes(5);

    [Display(Name = "Expiries up to (days)", GroupName = "Calculation", Order = 10)]
    [Range(1, 1000)]
    public int MaxDays { get; set; } = 400;

    [Display(Name = "Strip width (px)", GroupName = "Drawing", Order = 20)]
    [Range(40, 600)]
    public int Width { get; set; } = 130;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        _curve.Clear();
        for (int k = -30; k <= 30; k++)
        {
            double px = c.Spot * (1 + k * 0.005);
            _curve.Add((px, OptionsMath.GexByStrike(c, px, MaxDays).Values.Sum()));
        }

        double flip = OptionsMath.GammaFlip(c);
        var (cw, pw) = OptionsMath.Walls(c);
        var maxK = OptionsMath.GexByStrike(c, null, MaxDays).OrderByDescending(kv => Math.Abs(kv.Value)).FirstOrDefault();
        int from = Math.Max(0, LastBar - 80);
        s.Line(from, -1, flip, Opt.Flip, 1, LineDashStyle.Dash, "Gamma flip");
        s.Line(from, -1, cw, Opt.Call, 1, LineDashStyle.Dash, "Call wall");
        s.Line(from, -1, pw, Opt.Put, 1, LineDashStyle.Dash, "Put wall");
        if (maxK.Key > 0) s.Line(from, -1, maxK.Key, Opt.Text, 1, LineDashStyle.Dot, "Most gamma");
        s.Footer = StudyCard.Provenance(c);
    }

    protected override void RenderAfter(RenderContext ctx, Rectangle region)
    {
        if (_curve.Count < 2) return;
        double max = _curve.Max(p => Math.Abs(p.Gex));
        if (max <= 0) return;
        int edge = region.Right - 4, mid = edge - Width / 2;
        ctx.FillRectangle(Color.FromArgb(80, 14, 17, 24), new Rectangle(edge - Width - 2, region.Top, Width + 4, region.Height));
        for (int k = 0; k < _curve.Count - 1; k++)
        {
            int y0 = Y(_curve[k].Price), y1 = Y(_curve[k + 1].Price);
            double g = _curve[k].Gex;
            int w = (int)(Width / 2.0 * Math.Abs(g) / max);
            var col = Opt.A(g >= 0 ? Opt.Call : Opt.Put, (int)(60 + 150 * Math.Abs(g) / max));
            ctx.FillRectangle(col, new Rectangle(g >= 0 ? mid : mid - w, Math.Min(y0, y1), Math.Max(1, w), Math.Max(1, Math.Abs(y1 - y0))));
        }

        ctx.DrawLine(new RenderPen(Color.FromArgb(110, 170, 175, 185), 1), mid, region.Top, mid, region.Bottom);
        ctx.DrawString(UiText.Tr("dampen →"), StudyCard.Font, Opt.Call, mid + 2, region.Top + 4);
        string push = UiText.Tr("← push");
        ctx.DrawString(push, StudyCard.Font, Opt.Put, mid - ctx.MeasureString(push, StudyCard.Font).Width - 2, region.Top + 4);
    }
}

[DisplayName("Strike lanes")]
[Category(IndicatorCategories.Options)]
[Description("A horizontal lane per strike near the price whose thickness and brightness follow today's option volume at that strike (from trades, or from the chain's 24-hour volume when trades are not loaded), with each strike's gamma, the call and put walls and the gamma flip.")]
public class StrikeLanes : OptionsOverlay
{
    [Display(Name = "Strikes on each side", GroupName = "Calculation", Order = 10)]
    [Range(2, 60)]
    public int Lanes { get; set; } = 12;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        var (today, complete) = TodayTrades(data);
        var vol = new Dictionary<double, (double Call, double Put)>();
        bool fromTrades = complete && today.Count > 0;
        if (fromTrades)
            foreach (var t in today)
            {
                var v = vol.TryGetValue(t.Strike, out var x) ? x : (0, 0);
                vol[t.Strike] = t.Right == OptionRight.Call ? (v.Call + t.Size, v.Put) : (v.Call, v.Put + t.Size);
            }
        else
            foreach (var o in c.Contracts)
            {
                var v = vol.TryGetValue(o.Strike, out var x) ? x : (0, 0);
                vol[o.Strike] = o.Right == OptionRight.Call ? (v.Call + o.Volume, v.Put) : (v.Call, v.Put + o.Volume);
            }

        var strikes = c.Contracts.Select(o => o.Strike).Distinct().OrderBy(k => k).ToList();
        int at = strikes.FindIndex(k => k >= c.Spot);
        if (at < 0) at = strikes.Count - 1;
        var near = strikes.Skip(Math.Max(0, at - Lanes)).Take(2 * Lanes).ToList();
        double max = near.Select(k => vol.TryGetValue(k, out var v) ? v.Call + v.Put : 0).DefaultIfEmpty(0).Max();
        var gex = OptionsMath.GexByStrike(c);
        int start = DayStart(LastBar);
        foreach (var k in near)
        {
            var v = vol.TryGetValue(k, out var x) ? x : (0, 0);
            double tot = v.Call + v.Put, frac = max > 0 ? tot / max : 0;
            double g = gex.TryGetValue(k, out var gg) ? gg : 0;
            s.Line(start, -1, k, Opt.A(v.Call >= v.Put ? Opt.Call : Opt.Put, (int)(40 + 180 * frac)), 1 + (int)Math.Round(frac * 4), LineDashStyle.Solid,
                frac > 0.35 ? $"{Opt.Strike(k)} · {Opt.N(tot, "0")} · GEX {Opt.SignedUsd(g)}" : null);
        }

        var (cw, pw) = OptionsMath.Walls(c);
        s.Line(start, -1, OptionsMath.GammaFlip(c), Opt.Flip, 2, LineDashStyle.Dash, "Gamma flip");
        s.Line(start, -1, cw, Opt.Call, 2, LineDashStyle.Dash, "Call wall");
        s.Line(start, -1, pw, Opt.Put, 2, LineDashStyle.Dash, "Put wall");
        s.Title = $"Lanes · {Underlying}";
        s.Row(fromTrades ? "Volume today (trades)" : "Volume 24 h (chain)", Opt.N(vol.Values.Sum(v => v.Call + v.Put), "0"));
        s.Row("Calls / puts", Opt.N(vol.Values.Sum(v => v.Call), "0") + " / " + Opt.N(vol.Values.Sum(v => v.Put), "0"));
        s.Row("Total gamma", Opt.SignedUsd(gex.Values.Sum()), Opt.BiasColor(gex.Values.Sum()));
        s.Footer = StudyCard.Provenance(c);
    }
}

[DisplayName("Strike distribution and max pain")]
[Category(IndicatorCategories.Options)]
[Description("Which strikes are crowded before expiry: bars docked at the right with each strike's open interest or volume (calls right, puts left) and a line at max pain, the strike where option holders' intrinsic value at expiry is lowest.")]
public class StrikeBreakdown : OptionsOverlay
{
    private List<(double Strike, double Left, double Right)> _rows = new();
    private double _step;

    [Display(Name = "Expiry", GroupName = "Calculation", Order = 10)]
    public ExpiryPick Expiry { get; set; } = ExpiryPick.Nearest;

    [Display(Name = "Use volume (otherwise open interest)", GroupName = "Calculation", Order = 11)]
    public bool UseVolume { get; set; }

    [Display(Name = "Bar width (px)", GroupName = "Drawing", Order = 20)]
    [Range(40, 600)]
    public int Width { get; set; } = 160;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        var exp = Opt.Pick(c, Expiry);
        _rows = Opt.Of(c, exp).GroupBy(o => o.Strike).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Where(o => o.Right == OptionRight.Put).Sum(o => UseVolume ? o.Volume : o.OpenInterest), g.Where(o => o.Right == OptionRight.Call).Sum(o => UseVolume ? o.Volume : o.OpenInterest)))
            .Where(r => Math.Abs(r.Key / c.Spot - 1) < 0.35).ToList();
        _step = Opt.StrikeStep(_rows.Select(r => r.Strike), c.Spot);
        double pain = OptionsMath.MaxPain(c, exp);
        s.Line(Math.Max(0, LastBar - 60), -1, pain, Opt.Flip, 2, LineDashStyle.Dash, $"Max pain {Price(pain)}");
        double calls = _rows.Sum(r => r.Right), puts = _rows.Sum(r => r.Left);
        s.Title = $"{UiText.Tr(UseVolume ? "Volume by strike" : "Open interest by strike")} · {Opt.ExpiryText(exp)}";
        s.Row("Calls", Opt.N(calls, "0"), Opt.Call);
        s.Row("Puts", Opt.N(puts, "0"), Opt.Put);
        s.Row("Puts / calls", calls > 0 ? Opt.N(puts / calls, "0.00") : "—");
        var topC = _rows.OrderByDescending(r => r.Right).FirstOrDefault();
        var topP = _rows.OrderByDescending(r => r.Left).FirstOrDefault();
        if (topC.Strike > 0) s.Row("Most calls at", Price(topC.Strike), Opt.Call);
        if (topP.Strike > 0) s.Row("Most puts at", Price(topP.Strike), Opt.Put);
        if (double.IsFinite(pain)) s.Row("Max pain", Price(pain), Opt.Flip);
        s.Footer = StudyCard.Provenance(c);
    }

    protected override void RenderAfter(RenderContext ctx, Rectangle region) =>
        Opt.DockBars(ctx, region, Y, _rows, Width, _step, Opt.A(Opt.Put, 190), Opt.A(Opt.Call, 190), false);
}

[DisplayName("Most active option contracts")]
[Category(IndicatorCategories.Options)]
[Description("A card with the contracts carrying the most 24-hour volume or open interest in the snapshot (type, strike, expiry, IV and quantity) and a dotted line at each strike.")]
public class TopContracts : OptionsOverlay
{
    [Display(Name = "By open interest (otherwise volume)", GroupName = "Calculation", Order = 10)]
    public bool ByOpenInterest { get; set; }

    [Display(Name = "Contracts", GroupName = "Calculation", Order = 11)]
    [Range(1, 30)]
    public int Count { get; set; } = 8;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        s.Title = $"{UiText.Tr(ByOpenInterest ? "Most open interest" : "Most volume")} · {Underlying}";
        int from = Math.Max(0, LastBar - 30);
        foreach (var o in c.Contracts.OrderByDescending(o => ByOpenInterest ? o.OpenInterest : o.Volume).ThenBy(o => o.Symbol, StringComparer.Ordinal).Take(Count))
        {
            var col = o.Right == OptionRight.Call ? Opt.Call : Opt.Put;
            s.Row(Opt.Contract(o.Right, o.Strike, o.Expiry) + "  IV " + Opt.N(o.Iv * 100, "0"), Opt.N(ByOpenInterest ? o.OpenInterest : o.Volume, "0"), col);
            s.Line(from, -1, o.Strike, Opt.A(col, 150), 1, LineDashStyle.Dot, Opt.Contract(o.Right, o.Strike, o.Expiry));
        }

        s.Footer = StudyCard.Provenance(c);
    }
}

[DisplayName("Volatility surface")]
[Category(IndicatorCategories.Options)]
[Description("Implied volatility of each expiry (rows) at each distance from spot (columns, −20 % to +20 %), tinted against that expiry's at-the-money IV: richer in orange, cheaper in blue. Helps spot cheap or expensive options before trading.")]
public class VolatilitySurface : OptionsOverlay
{
    private static readonly double[] Moneyness = { -20, -10, -5, 0, 5, 10, 20 };
    private readonly List<(DateTime Expiry, double Atm, double[] Iv)> _grid = new();
    private string _footer = "";

    [Display(Name = "Expiries", GroupName = "Calculation", Order = 10)]
    [Range(2, 16)]
    public int Expiries { get; set; } = 8;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        _grid.Clear();
        foreach (var e in c.Contracts.Select(o => o.Expiry).Distinct().Where(e => e > c.Time).OrderBy(e => e).Take(Expiries))
            _grid.Add((e, OptionsMath.AtmIv(c, e), Moneyness.Select(m => OptionsMath.SmileIv(c, e, c.Spot * (1 + m / 100))).ToArray()));
        _footer = StudyCard.Provenance(c);
    }

    protected override void RenderAfter(RenderContext ctx, Rectangle region)
    {
        if (_grid.Count == 0 || !ShowCard) return;
        var font = StudyCard.Font;
        int cw = 44, rh = 16, lw = 74;
        int w = lw + cw * Moneyness.Length + 12, h = rh * (_grid.Count + 3) + 10;
        int col = (int)CardPosition % 3, row = (int)CardPosition / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        ctx.FillRectangle(Color.FromArgb(225, 18, 20, 26), new Rectangle(x, y, w, h), 5);
        ctx.DrawString(UiText.Tr($"Implied volatility · {Underlying}"), StudyCard.Bold, Opt.Text, x + 6, y + 4);
        for (int j = 0; j < Moneyness.Length; j++)
            ctx.DrawString(Moneyness[j] == 0 ? "ATM" : Moneyness[j].ToString("+0;−0", CultureInfo.InvariantCulture) + "%", font, Opt.Dim, x + lw + j * cw + 6, y + 4 + rh);
        for (int i = 0; i < _grid.Count; i++)
        {
            var g = _grid[i];
            int ry = y + 4 + rh * (i + 2);
            ctx.DrawString(g.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), font, Opt.Dim, x + 6, ry);
            for (int j = 0; j < Moneyness.Length; j++)
            {
                double iv = g.Iv[j], rel = double.IsFinite(g.Atm) && g.Atm > 0 && double.IsFinite(iv) ? iv / g.Atm - 1 : 0;
                var back = rel >= 0 ? Color.FromArgb((int)Math.Clamp(30 + rel * 900, 30, 200), 251, 140, 0) : Color.FromArgb((int)Math.Clamp(30 - rel * 900, 30, 200), 41, 98, 255);
                var cell = new Rectangle(x + lw + j * cw, ry - 2, cw - 2, rh - 2);
                ctx.FillRectangle(back, cell, 2);
                ctx.DrawString(Opt.N(iv * 100, "0.0"), font, Opt.Text, cell.Left + 6, ry);
            }
        }

        ctx.DrawString(UiText.Tr(_footer), font, Opt.Dim, x + 6, y + 4 + rh * (_grid.Count + 2));
    }
}

[DisplayName("Options probability cloud")]
[Category(IndicatorCategories.Options)]
[Description("Where option prices imply the underlying will be at expiry: the risk-neutral distribution derived from the volatility smile (N(d2) at each level), drawn as a cloud ahead of the last bar, with the 68 % and 95 % ranges and the median.")]
public class OptionsOddsCloud : OptionsOverlay
{
    private readonly List<(double Price, double Density)> _cloud = new();

    [Display(Name = "Expiry", GroupName = "Calculation", Order = 10)]
    public ExpiryPick Expiry { get; set; } = ExpiryPick.Weekly;

    [Display(Name = "Cloud width (px)", GroupName = "Drawing", Order = 20)]
    [Range(40, 600)]
    public int Width { get; set; } = 150;

    /// <summary>Price quantiles of the smile-implied distribution at <paramref name="expiry"/>; NaN without an ATM IV.</summary>
    internal static (List<(double K, double Cdf)> Grid, double Atm) Distribution(OptionsChain c, DateTime expiry)
    {
        double t = Math.Max(1.0 / 365, (expiry - c.Time).TotalDays / 365), atm = OptionsMath.AtmIv(c, expiry);
        var grid = new List<(double K, double Cdf)>();
        if (!double.IsFinite(atm) || atm <= 0) return (grid, atm);
        double sd = atm * Math.Sqrt(t);
        for (int k = -80; k <= 80; k++)
        {
            double px = c.Spot * Math.Exp(k / 20.0 * sd);
            grid.Add((px, 1 - OptionsMath.ProbAbove(c.Spot, px, t, OptionsMath.SmileIv(c, expiry, px))));
        }

        // The cumulative distribution must not decrease; smile noise can make it do so locally.
        for (int i = 1; i < grid.Count; i++)
            if (grid[i].Cdf < grid[i - 1].Cdf) grid[i] = (grid[i].K, grid[i - 1].Cdf);
        return (grid, atm);
    }

    internal static double Quantile(List<(double K, double Cdf)> grid, double p)
    {
        for (int i = 1; i < grid.Count; i++)
            if (grid[i].Cdf >= p)
            {
                double w = (p - grid[i - 1].Cdf) / Math.Max(1e-12, grid[i].Cdf - grid[i - 1].Cdf);
                return grid[i - 1].K + w * (grid[i].K - grid[i - 1].K);
            }

        return grid.Count > 0 ? grid[^1].K : double.NaN;
    }

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        var exp = Opt.Pick(c, Expiry == ExpiryPick.All ? ExpiryPick.Weekly : Expiry) ?? OptionsMath.ExpiryNear(c, 7);
        var (grid, atm) = Distribution(c, exp);
        _cloud.Clear();
        if (grid.Count < 2)
        {
            Status = "The chain has no at-the-money IV for that expiry.";
            return;
        }

        for (int i = 1; i < grid.Count; i++) _cloud.Add(((grid[i].K + grid[i - 1].K) / 2, (grid[i].Cdf - grid[i - 1].Cdf) / Math.Max(1e-9, grid[i].K - grid[i - 1].K)));
        double q025 = Quantile(grid, 0.025), q16 = Quantile(grid, 0.16), q50 = Quantile(grid, 0.5), q84 = Quantile(grid, 0.84), q975 = Quantile(grid, 0.975);
        double t = Math.Max(1.0 / 365, (exp - c.Time).TotalDays / 365);
        int from = LastBar;
        s.Line(from, -1, q50, Opt.Text, 1, LineDashStyle.Dash, $"Median {Price(q50)}");
        s.Line(from, -1, q84, Opt.Flip, 1, LineDashStyle.Solid, $"68 % {Price(q84)}");
        s.Line(from, -1, q16, Opt.Flip, 1, LineDashStyle.Solid, $"68 % {Price(q16)}");
        s.Line(from, -1, q975, Opt.A(Opt.Flip, 150), 1, LineDashStyle.Dot, $"95 % {Price(q975)}");
        s.Line(from, -1, q025, Opt.A(Opt.Flip, 150), 1, LineDashStyle.Dot, $"95 % {Price(q025)}");
        s.Title = $"At expiry {exp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        s.Row("At-the-money IV", Opt.Pct(atm * 100));
        s.Row("68 %", Price(q16) + " – " + Price(q84), Opt.Flip);
        s.Row("95 %", Price(q025) + " – " + Price(q975));
        s.Row("Probability of finishing higher", Opt.Pct(OptionsMath.ProbAbove(c.Spot, c.Spot, t, atm) * 100, "0"));
        s.Footer = StudyCard.Provenance(c);
    }

    protected override void RenderBefore(RenderContext ctx, Rectangle region)
    {
        if (_cloud.Count < 2) return;
        double max = _cloud.Max(p => p.Density);
        if (max <= 0) return;
        int x0 = Math.Min(ChartInfo.GetXByBar(LastBar, false) + 12, region.Right - Width - 4);
        for (int i = 0; i < _cloud.Count - 1; i++)
        {
            int y0 = Y(_cloud[i].Price), y1 = Y(_cloud[i + 1].Price);
            double f = _cloud[i].Density / max;
            ctx.FillRectangle(Color.FromArgb((int)(10 + 90 * f * f), 251, 191, 36), new Rectangle(x0, Math.Min(y0, y1), Math.Max(1, (int)(Width * f)), Math.Max(1, Math.Abs(y1 - y0))));
        }
    }
}

[DisplayName("Options expected move")]
[Category(IndicatorCategories.Options)]
[Description("The range the options market expects. Each day: the previous close ± one standard deviation using the IV known at that day's open (the previous day's close from daily history; today, the live snapshot when the history has no row yet), never revised afterwards. A forward cone shows ±1σ and ±2σ from the latest snapshot. Move = close × IV × √(1 / days per year).")]
public class ExpectedMove : OptionsOverlay
{
    [Display(Name = "Days back", GroupName = "Calculation", Order = 10)]
    [Range(1, 60)]
    public int Days { get; set; } = 10;

    [Display(Name = "Cone bars", GroupName = "Drawing", Order = 20)]
    [Range(0, 500)]
    public int ConeBars { get; set; } = 40;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = data.Chain(Underlying, ChainAge);
        var daily = data.Daily(Underlying);
        double year = Opt.Year(Underlying);
        var starts = new List<int>();
        for (int i = LastBar, done = 0; i > 0 && done <= Days; done++)
        {
            int st = DayStart(i);
            starts.Add(st);
            i = st - 1;
        }

        int todayStart = DayStart(LastBar);
        foreach (var st in starts)
        {
            if (st == 0) continue;
            double prevClose = Close(st - 1);
            bool today = st == todayStart;
            // The IV known at the day's open: the latest daily row before that day, if at most four days old.
            var known = Opt.KnownBefore(daily, TimeOf(st));
            double iv = known != null && known.Date.Date >= TimeOf(st).Date.AddDays(-4) ? known.AtmIv : double.NaN;
            string source = "daily close";
            if (today && c != null && (!double.IsFinite(iv) || known!.Date.Date < TimeOf(st).Date.AddDays(-1)))
            {
                iv = OptionsMath.AtmIv(c, OptionsMath.ExpiryNear(c, 1));
                source = "live snapshot";
            }

            if (!double.IsFinite(iv) || iv <= 0) continue;
            double em = prevClose * iv * Math.Sqrt(1 / year);
            int end = today ? -1 : NextDayStart(st) - 1;
            s.Line(st, end, prevClose + em, Opt.Call, 1, LineDashStyle.Solid, today ? $"+1σ {Price(prevClose + em)}" : null);
            s.Line(st, end, prevClose - em, Opt.Put, 1, LineDashStyle.Solid, today ? $"−1σ {Price(prevClose - em)}" : null);
            s.Line(st, end, prevClose, Opt.A(Opt.Muted, 160), 1, LineDashStyle.Dot);
            if (!today) continue;
            double px = Close(LastBar);
            s.Title = $"Expected move · {Underlying}";
            s.Row("Today (±1σ)", "±" + Price(em));
            s.Row("In %", "±" + Opt.Pct(em / prevClose * 100, "0.00"));
            s.Row("Range", Price(prevClose - em) + " – " + Price(prevClose + em), Opt.Flip);
            s.Row("IV used", Opt.Pct(iv * 100) + " (" + UiText.Tr(source) + ")");
            s.Row("Range used", Opt.Pct(Math.Abs(px - prevClose) / em * 100, "0"), Math.Abs(px - prevClose) > em ? Opt.Flip : default);
        }

        if (ConeBars > 0 && c != null && BarLength > TimeSpan.Zero)
        {
            double iv = OptionsMath.AtmIv(c, OptionsMath.ExpiryNear(c, Math.Max(1, ConeBars * BarLength.TotalDays)));
            if (double.IsFinite(iv) && iv > 0)
            {
                double px = Close(LastBar);
                foreach (var k in new[] { 1.0, 2.0 })
                    foreach (var sign in new[] { 1, -1 })
                    {
                        var pts = new List<(int, double)>();
                        for (int b = 0; b <= ConeBars; b += Math.Max(1, ConeBars / 30))
                            pts.Add((LastBar + b, px * Math.Exp(sign * k * iv * Math.Sqrt(b * BarLength.TotalDays / 365))));
                        s.Paths.Add(new OptionsScene.PathLine(pts, Opt.A(sign > 0 ? Opt.Call : Opt.Put, k == 1 ? 170 : 90), 1, k == 1 ? LineDashStyle.Dash : LineDashStyle.Dot));
                    }
            }

            s.Footer = StudyCard.Provenance(c);
        }

        if (s.Levels.Count == 0) Status = data.ChainError(Underlying) is { } err ? "Options: " + err : $"Loading {Underlying} implied volatility…";
    }

    private int NextDayStart(int st)
    {
        int k = st;
        while (k < CurrentBar - 1 && DayOf(k + 1) == DayOf(st)) k++;
        return k + 1;
    }
}

[DisplayName("Option contract odds")]
[Category(IndicatorCategories.Options)]
[Description("Before trading an option: the chosen contract (type, strike, expiry), its premium, break-even, risk-neutral probability of profit and of finishing in the money at expiry, and how many contracts fit the risk you set. Draws the strike and the break-even.")]
public class ContractOdds : OptionsOverlay
{
    public enum Way
    {
        [Display(Name = "Buy")] Buy,
        [Display(Name = "Sell")] Sell,
    }

    [Display(Name = "Type", GroupName = "Contract", Order = 10)]
    public OptionRight Right { get; set; } = OptionRight.Call;

    [Display(Name = "Side", GroupName = "Contract", Order = 11)]
    public Way Side { get; set; } = Way.Buy;

    [Display(Name = "Strike (0 = by distance)", GroupName = "Contract", Order = 12)]
    [Range(0, 1e9)]
    public decimal Strike { get; set; }

    [Display(Name = "Distance from price (%)", GroupName = "Contract", Order = 13)]
    [Range(-50, 50)]
    public decimal OffsetPct { get; set; } = 5;

    [Display(Name = "Expiry", GroupName = "Contract", Order = 14)]
    public ExpiryPick Expiry { get; set; } = ExpiryPick.Weekly;

    [Display(Name = "Risk per trade (USD)", GroupName = "Contract", Order = 15)]
    [Range(1, 1e9)]
    public decimal Risk { get; set; } = 500;

    /// <summary>Break-even, probability of profit, probability in the money and loss basis per contract (premium; for a sale, the loss at a 2σ adverse move).</summary>
    internal static (double BreakEven, double Win, double Itm, double LossPerContract) Odds(OptionsChain c, OptionContract o, bool buy, double premium, double iv)
    {
        double t = Math.Max(1.0 / 365, (o.Expiry - c.Time).TotalDays / 365);
        bool call = o.Right == OptionRight.Call;
        double be = call ? o.Strike + premium : o.Strike - premium;
        double pAbove = OptionsMath.ProbAbove(c.Spot, be, t, iv), pItm = OptionsMath.ProbAbove(c.Spot, o.Strike, t, iv);
        double win = call ? pAbove : 1 - pAbove;
        if (!call) pItm = 1 - pItm;
        if (!buy) win = 1 - win;
        double perContract = premium * o.ContractSize;
        double move2 = c.Spot * Math.Exp((call ? 2 : -2) * iv * Math.Sqrt(t));
        double loss = buy ? perContract : Math.Max(perContract * 0.1, (call ? Math.Max(0, move2 - o.Strike) : Math.Max(0, o.Strike - move2)) * o.ContractSize - perContract);
        return (be, win, pItm, loss);
    }

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        var exp = Opt.Pick(c, Expiry == ExpiryPick.All ? ExpiryPick.Weekly : Expiry) ?? OptionsMath.ExpiryNear(c, 7);
        double target = Strike > 0 ? (double)Strike : c.Spot * (1 + (double)OffsetPct / 100 * (Right == OptionRight.Call ? 1 : -1));
        var o = c.Contracts.Where(x => x.Expiry == exp && x.Right == Right).OrderBy(x => Math.Abs(x.Strike - target)).FirstOrDefault();
        if (o == null)
        {
            Status = "No contracts for that expiry.";
            return;
        }

        double prem = o.Mark > 0 ? o.Mark : (o.Bid + o.Ask) / 2;
        double iv = o.Iv > 0 ? o.Iv : OptionsMath.AtmIv(c, exp);
        var (be, win, itm, loss) = Odds(c, o, Side == Way.Buy, prem, iv);
        double size = loss > 0 ? Math.Floor((double)Risk / loss * 100) / 100 : 0;
        int from = Math.Max(0, LastBar - 30);
        var col = Right == OptionRight.Call ? Opt.Call : Opt.Put;
        s.Line(from, -1, o.Strike, col, 2, LineDashStyle.Solid, $"Strike {Price(o.Strike)}");
        s.Line(from, -1, be, Opt.Flip, 1, LineDashStyle.Dash, $"Break-even {Price(be)}");
        s.Title = $"{UiText.Tr(Side == Way.Buy ? "Buy" : "Sell")} {Opt.Contract(o.Right, o.Strike, o.Expiry)}";
        s.Row("Premium", Opt.Usd(prem * o.ContractSize) + "  (IV " + Opt.N(iv * 100, "0") + " %)");
        s.Row("Break-even", Price(be), Opt.Flip);
        s.Row("Probability of profit at expiry", Opt.Pct(win * 100, "0"), win >= 0.5 ? Opt.Call : Opt.Put);
        s.Row("Probability in the money", Opt.Pct(itm * 100, "0"));
        s.Row(Side == Way.Buy ? "Maximum loss per contract" : "Loss at 2σ per contract", Opt.Usd(loss), Opt.Put);
        s.Row("Contracts for the risk", Opt.N(size, "0.##"));
        s.Row("Days to expiry", Opt.N((exp - c.Time).TotalDays, "0.0"));
        s.Footer = StudyCard.Provenance(c);
    }
}

[DisplayName("Options stance")]
[Category(IndicatorCategories.Options)]
[Description("Rates five common option trades (buy call, buy put, sell put, covered call, buy straddle) as favorable, neutral or unfavorable from the price trend (EMA), how expensive volatility is (IV rank over the last year of daily history) and the 25-delta skew. A rule of thumb, not advice.")]
public class OptionStance : OptionsOverlay
{
    [Display(Name = "Trend EMA length", GroupName = "Calculation", Order = 10)]
    [Range(5, 400)]
    public int EmaLength { get; set; } = 50;

    /// <summary>Scores (−1, 0, +1) for buy call, buy put, sell put, covered call and buy straddle.</summary>
    internal static int[] Scores(int trend, int vol, double rr)
    {
        static int C(int v) => Math.Clamp(v, -1, 1);
        return new[]
        {
            C(trend - (vol > 0 ? 1 : 0) + (vol < 0 && trend > 0 ? 1 : 0)),
            C(-trend - (vol > 0 ? 1 : 0) + (vol < 0 && trend < 0 ? 1 : 0) + (double.IsFinite(rr) && rr > 2 ? 1 : 0)),
            C((trend >= 0 ? 1 : -1) + (vol > 0 ? 1 : vol < 0 ? -1 : 0) - (trend < 0 ? 1 : 0)),
            C((trend <= 0 ? 1 : -1) + (vol > 0 ? 1 : vol < 0 ? -1 : 0)),
            C(vol < 0 ? 1 : vol > 0 ? -1 : 0),
        };
    }

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        var closes = Enumerable.Range(0, CurrentBar).Select(Close).ToList();
        var ema = Opt.Ema(closes, EmaLength);
        double px = closes[LastBar], e = ema[LastBar], e0 = ema[Math.Max(0, LastBar - 10)];
        int trend = px > e && e >= e0 ? 1 : px < e && e <= e0 ? -1 : 0;
        var e30 = OptionsMath.ExpiryNear(c, 30);
        double iv = OptionsMath.AtmIv(c, e30), rr = OptionsMath.RiskReversal25(c, e30);
        double rank = OptionsMath.Rank(data.Daily(Underlying).Where(d => d.Date > c.Time.AddDays(-365) && d.Date.Date < c.Time.Date).Select(d => d.AtmIv), iv);
        int vol = !double.IsFinite(rank) ? 0 : rank >= 60 ? 1 : rank <= 30 ? -1 : 0;
        var names = new[] { "Buy call", "Buy put", "Sell put", "Covered call", "Buy straddle" };
        var scores = Scores(trend, vol, rr);
        s.Title = $"Stance · {Underlying}";
        for (int i = 0; i < names.Length; i++)
            s.Row(names[i], scores[i] > 0 ? "favorable" : scores[i] < 0 ? "unfavorable" : "neutral", scores[i] > 0 ? Opt.Call : scores[i] < 0 ? Opt.Put : Opt.Muted);
        s.Row("Trend", trend > 0 ? "up" : trend < 0 ? "down" : "sideways", trend > 0 ? Opt.Call : trend < 0 ? Opt.Put : default);
        s.Row("30-day IV · IV rank", Opt.Pct(iv * 100) + " · " + Opt.N(rank, "0"));
        s.Row("25Δ skew (call − put)", Opt.N(rr, "+0.0;-0.0"), rr >= 0 ? Opt.Call : Opt.Put);
        s.Footer = StudyCard.Provenance(c);
    }
}

[DisplayName("Options dashboard")]
[Category(IndicatorCategories.Options)]
[Description("Nine tiles summarizing the underlying's options: 30-day IV, IV rank, 25-delta skew, put/call by volume and by open interest, gamma regime, distance to the gamma flip, one-day expected move and today's net premium flow, with a reading of whether moves tend to extend or stall.")]
public class OptionsDashboard : OptionsOverlay
{
    private readonly List<(string Name, string Value, Color Color)> _tiles = new();
    private string _verdict = "", _footer = "";
    private Color _verdictColor = Opt.Muted;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        var e30 = OptionsMath.ExpiryNear(c, 30);
        double iv = OptionsMath.AtmIv(c, e30), rr = OptionsMath.RiskReversal25(c, e30), gex = OptionsMath.TotalGex(c), flip = OptionsMath.GammaFlip(c);
        double em = OptionsMath.ExpectedMove(c, OptionsMath.ExpiryNear(c, 1));
        var day = OptionsMath.DailyOf(c);
        double rank = OptionsMath.Rank(data.Daily(Underlying).Where(d => d.Date > c.Time.AddDays(-365) && d.Date.Date < c.Time.Date).Select(d => d.AtmIv), iv);
        var (today, complete) = TodayTrades(data);
        double net = complete ? today.Sum(t => t.Bias * t.Premium) : double.NaN;
        _tiles.Clear();
        _tiles.Add(("30-day IV", Opt.Pct(iv * 100), default));
        _tiles.Add(("IV rank", Opt.N(rank, "0"), rank >= 60 ? Opt.Put : rank <= 30 ? Opt.Call : default));
        _tiles.Add(("25Δ skew", Opt.N(rr, "+0.0;-0.0"), rr >= 0 ? Opt.Call : Opt.Put));
        _tiles.Add(("Puts/calls volume", Opt.N(day.PutCallVolume, "0.00"), day.PutCallVolume > 1 ? Opt.Put : Opt.Call));
        _tiles.Add(("Puts/calls OI", Opt.N(day.PutCallOi, "0.00"), day.PutCallOi > 1 ? Opt.Put : Opt.Call));
        _tiles.Add(("Gamma", gex >= 0 ? "positive" : "negative", gex >= 0 ? Opt.Call : Opt.Put));
        _tiles.Add(("To gamma flip", double.IsFinite(flip) ? Opt.Pct((flip / c.Spot - 1) * 100, "+0.0;-0.0") : "—", Opt.Flip));
        _tiles.Add(("1-day expected move", double.IsFinite(em) ? "±" + Opt.Pct(em / c.Spot * 100, "0.00") : "—", default));
        _tiles.Add(("Net flow today", Opt.SignedUsd(net), Opt.BiasColor(double.IsFinite(net) ? net : 0)));
        var ema = Opt.Ema(Enumerable.Range(0, CurrentBar).Select(Close).ToList(), 20);
        int trend = Math.Sign(Close(LastBar) - ema[LastBar]);
        if (gex < 0 && double.IsFinite(net) && Math.Sign(net) == trend && trend != 0) (_verdict, _verdictColor) = ("Moves tend to extend: negative gamma and flow agrees", trend > 0 ? Opt.Call : Opt.Put);
        else if (gex >= 0) (_verdict, _verdictColor) = ("Moves tend to stall: dealers dampen", Opt.Muted);
        else (_verdict, _verdictColor) = ("No clear signal", Opt.Muted);
        _footer = StudyCard.Provenance(c);
    }

    protected override void RenderAfter(RenderContext ctx, Rectangle region)
    {
        if (_tiles.Count == 0 || !ShowCard) return;
        var big = new RenderFont("Arial", 11, FontStyle.Bold);
        int tw = 112, th = 40, gap = 4;
        int w = 3 * tw + 4 * gap, h = 3 * th + 4 * gap + 34;
        int col = (int)CardPosition % 3, row = (int)CardPosition / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        ctx.FillRectangle(Color.FromArgb(225, 18, 20, 26), new Rectangle(x, y, w, h), 6);
        for (int k = 0; k < _tiles.Count; k++)
        {
            int cx = x + gap + k % 3 * (tw + gap), cy = y + gap + k / 3 * (th + gap);
            ctx.FillRectangle(Color.FromArgb(160, 32, 36, 46), new Rectangle(cx, cy, tw, th), 4);
            ctx.DrawString(UiText.Tr(_tiles[k].Name), StudyCard.Font, Opt.Dim, cx + 6, cy + 4);
            ctx.DrawString(UiText.Tr(_tiles[k].Value), big, _tiles[k].Color.A > 0 ? _tiles[k].Color : Opt.Text, cx + 6, cy + 18);
        }

        ctx.DrawString(UiText.Tr(_verdict), StudyCard.Bold, _verdictColor, x + 8, y + h - 30);
        ctx.DrawString(UiText.Tr(_footer), StudyCard.Font, Opt.Dim, x + 8, y + h - 16);
    }
}

[DisplayName("Volatility context (options)")]
[Category(IndicatorCategories.Options)]
[Description("A card for the session open: whether options are rich or cheap against realized volatility (30-day IV ÷ 30-day realized), whether traders fear a drop (25-delta skew) and whether the short term is stressed (7-day IV ÷ 30-day IV).")]
public class VolatilityContext : OptionsOverlay
{
    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        var e30 = OptionsMath.ExpiryNear(c, 30);
        double iv30 = OptionsMath.AtmIv(c, e30), iv7 = OptionsMath.AtmIv(c, OptionsMath.ExpiryNear(c, 7)), rv = RealizedVol(30), rr = OptionsMath.RiskReversal25(c, e30);
        double ratio = rv > 0 ? iv30 / rv : double.NaN, term = iv30 > 0 ? iv7 / iv30 : double.NaN;
        s.Title = $"Volatility context · {Underlying}";
        s.Row("30-day IV", Opt.Pct(iv30 * 100));
        s.Row("30-day realized", Opt.Pct(rv * 100));
        s.Row("Options", !double.IsFinite(ratio) ? "—" : ratio > 1.25 ? "rich" : ratio < 0.9 ? "cheap" : "fairly priced", ratio > 1.25 ? Opt.Put : ratio < 0.9 ? Opt.Call : default);
        s.Row("Downside fear", !double.IsFinite(rr) ? "—" : rr < -3 ? "high" : rr < -0.5 ? "moderate" : "low", rr < -3 ? Opt.Put : default);
        s.Row("Short term (7 d IV / 30 d IV)", Opt.N(term, "0.00") + (term > 1.05 ? "  " + UiText.Tr("stressed") : ""), term > 1.05 ? Opt.Flip : default);
        s.Footer = StudyCard.Provenance(c);
    }
}

[DisplayName("Gamma map by bar")]
[Category(IndicatorCategories.Options)]
[Description("Behind each bar, a heatmap of where dealers hedge most (each strike's gamma in the latest snapshot recorded before the bar closed): green where they dampen, red where they push. Uses the snapshots recorded every 30 minutes while an options study requests the chain; nothing is drawn where no snapshot existed.")]
public class SpotExposures : OptionsOverlay
{
    private int[] _snapOf = Array.Empty<int>();
    private List<StrikeSnapshot> _snaps = new();
    private double _max;

    public SpotExposures() => ShowCard = false;

    [Display(Name = "Intensity", GroupName = "Drawing", Order = 20)]
    [Range(10, 255)]
    public int Alpha { get; set; } = 90;

    internal override void Build(OptionsScene s, OptionsData data)
    {
        _ = data.Chain(Underlying, ChainAge);
        _snaps = data.Snapshots(Underlying).OrderBy(x => x.Time).ToList();
        _snapOf = new int[CurrentBar];
        _max = 0;
        int k = -1;
        for (int i = 0; i < CurrentBar; i++)
        {
            var end = EndOf(i);
            while (k + 1 < _snaps.Count && _snaps[k + 1].Time < end) k++;
            _snapOf[i] = k >= 0 && end - _snaps[k].Time < TimeSpan.FromHours(6) ? k : -1;
        }

        foreach (var sn in _snaps)
            foreach (var r in sn.Rows)
                if (Math.Abs(r.Strike / sn.Spot - 1) < 0.2) _max = Math.Max(_max, Math.Abs(r.Gex));
        if (_snaps.Count == 0) Status = "No chain snapshots yet: one is recorded every 30 minutes while an options study is open.";
    }

    protected override void RenderBefore(RenderContext ctx, Rectangle region)
    {
        if (_max <= 0) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(_snapOf.Length - 1, LastVisibleBarNumber);
        for (int i = first; i <= last; i++)
        {
            int k = _snapOf[i];
            if (k < 0) continue;
            var sn = _snaps[k];
            int x0 = X(i), x1 = X(i + 1);
            var rows = sn.Rows.Where(r => Math.Abs(r.Strike / sn.Spot - 1) < 0.2).ToList();
            for (int j = 0; j < rows.Count; j++)
            {
                var r = rows[j];
                double f = Math.Abs(r.Gex) / _max;
                if (f < 0.04) continue;
                double half = (j + 1 < rows.Count ? rows[j + 1].Strike - r.Strike : j > 0 ? r.Strike - rows[j - 1].Strike : sn.Spot * 0.005) * 0.4;
                int y0 = Y(r.Strike + half), y1 = Y(r.Strike - half);
                ctx.FillRectangle(Opt.A(r.Gex >= 0 ? Opt.Call : Opt.Put, (int)(Alpha * Math.Sqrt(f))), new Rectangle(x0, Math.Min(y0, y1), Math.Max(1, x1 - x0), Math.Max(1, Math.Abs(y1 - y0))));
            }
        }
    }
}

[DisplayName("Option open-interest changes")]
[Category(IndicatorCategories.Options)]
[Description("How much option open interest opened (+) or closed (−) at each strike since the recorded snapshot from a set number of hours ago (24 by default): bars docked at the right, calls right and puts left, to see where new positions build.")]
public class OiChanges : OptionsOverlay
{
    private List<(double Strike, double Call, double Put)> _rows = new();
    private double _step;

    [Display(Name = "Compare with the snapshot from (hours ago)", GroupName = "Calculation", Order = 10)]
    [Range(1, 168)]
    public int Hours { get; set; } = 24;

    [Display(Name = "Bar width (px)", GroupName = "Drawing", Order = 20)]
    [Range(40, 600)]
    public int Width { get; set; } = 160;

    /// <summary>
    /// Per strike of either snapshot, in strike order: open interest now minus then (a strike absent then counts as zero
    /// then; a strike absent now, all of its interest closed or expired, counts as zero now).
    /// </summary>
    internal static List<(double Strike, double Call, double Put)> Changes(StrikeSnapshot now, StrikeSnapshot old)
    {
        var before = old.Rows.ToDictionary(r => r.Strike);
        var after = now.Rows.ToDictionary(r => r.Strike);
        return after.Keys.Union(before.Keys).Order().Select(k =>
        {
            var (n, b) = (after.GetValueOrDefault(k), before.GetValueOrDefault(k));
            return (k, (n?.CallOi ?? 0) - (b?.CallOi ?? 0), (n?.PutOi ?? 0) - (b?.PutOi ?? 0));
        }).ToList();
    }

    internal override void Build(OptionsScene s, OptionsData data)
    {
        var c = GetChain(data);
        if (c == null) return;
        var old = data.Snapshots(Underlying).Where(x => x.Time <= c.Time.AddHours(-Hours)).OrderByDescending(x => x.Time).FirstOrDefault();
        if (old == null)
        {
            Status = $"Needs a snapshot from {Hours} h ago: one is recorded every 30 minutes while an options study is open.";
            return;
        }

        _rows = Changes(OptionsMath.SnapshotOf(c), old).Where(r => Math.Abs(r.Strike / c.Spot - 1) < 0.35).ToList();
        _step = Opt.StrikeStep(_rows.Select(r => r.Strike), c.Spot);
        s.Title = $"Open interest since {old.Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC";
        s.Row("Calls", Signed(_rows.Sum(r => r.Call)), Opt.Call);
        s.Row("Puts", Signed(_rows.Sum(r => r.Put)), Opt.Put);
        var top = _rows.OrderByDescending(r => r.Call + r.Put).FirstOrDefault();
        if (top.Strike > 0 && top.Call + top.Put > 0) s.Row("Most opened at", Price(top.Strike));
        s.Footer = StudyCard.Provenance(c);
    }

    private static string Signed(double v) => (v > 0 ? "+" : "") + Opt.N(v, "0");

    protected override void RenderAfter(RenderContext ctx, Rectangle region)
    {
        if (_rows.Count == 0) return;
        double max = _rows.Max(r => Math.Max(Math.Abs(r.Call), Math.Abs(r.Put)));
        if (max <= 0) return;
        int edge = region.Right - 4, mid = edge - Width / 2;
        int h = Math.Clamp(Math.Abs(Y(_rows[0].Strike) - Y(_rows[0].Strike + _step)) - 1, 2, 14);
        ctx.FillRectangle(Color.FromArgb(70, 14, 17, 24), new Rectangle(edge - Width - 2, region.Top, Width + 4, region.Height));
        foreach (var r in _rows)
        {
            int cy = Y(r.Strike);
            if (cy < region.Top || cy > region.Bottom) continue;
            int wc = (int)(Width / 2.0 * Math.Abs(r.Call) / max), wp = (int)(Width / 2.0 * Math.Abs(r.Put) / max);
            if (wc > 0) ctx.FillRectangle(Opt.A(Opt.Call, r.Call >= 0 ? 210 : 80), new Rectangle(mid, cy - h / 2, wc, h));
            if (wp > 0) ctx.FillRectangle(Opt.A(Opt.Put, r.Put >= 0 ? 210 : 80), new Rectangle(mid - wp, cy - h / 2, wp, h));
        }

        ctx.DrawLine(new RenderPen(Color.FromArgb(90, 160, 165, 175), 1), mid, region.Top, mid, region.Bottom);
    }
}
