// Behavior adapted from the KVP capability reference (commits 7fbf237, 644b8ca); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds.External;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native.Options;

[DisplayName("Day type")]
[Category(IndicatorCategories.Sessions)]
[Description("What kind of session this is and how long such states usually last. Every 10 minutes it classifies the last hour of prices as Pinned (narrow range), Trending (moving one way), Volatile (wide range) or Unsettled. Ranges are measured against the move options expect (1σ from the IV known at that time; without an options connection, from realized volatility of the preceding day of samples). Each state is known when its 10-minute sample closes; the last one is still forming. The card counts how often the state persisted 30 minutes to 2 hours later and how far price moved; a box ahead of price shows that range. With options data it also marks the gamma flip and walls.")]
public class DayType : OptionsOverlay
{
    public enum State
    {
        [Display(Name = "Unsettled")] Unsettled,
        [Display(Name = "Pinned")] Pinned,
        [Display(Name = "Trending")] Trending,
        [Display(Name = "Volatile")] Volatile,
    }

    /// <summary>One 10-minute sample: its last bar, close time, close price, trading day and whether it has closed.</summary>
    internal readonly record struct Sample(int Bar, DateTime End, double Price, int Day, bool Closed);

    private readonly List<(int Bar, State State)> _ribbon = new();

    public DayType()
    {
        CardPosition = CardCorner.BottomRight;
    }

    [Display(Name = "Window (10-minute samples)", GroupName = "Calculation", Order = 10)]
    [Range(2, 60)]
    public int Window { get; set; } = 6;

    [Display(Name = "Volatile from (× 1σ)", GroupName = "Calculation", Order = 11)]
    [Range(0.2, 10)]
    public decimal VolatileRatio { get; set; } = 1.5m;

    [Display(Name = "Pinned below (× 1σ)", GroupName = "Calculation", Order = 12)]
    [Range(0.05, 5)]
    public decimal PinnedRatio { get; set; } = 0.6m;

    [Display(Name = "Trend efficiency", GroupName = "Calculation", Order = 13)]
    [Range(0.1, 1)]
    public decimal TrendEfficiency { get; set; } = 0.6m;

    [Display(Name = "Minimum cases", GroupName = "Calculation", Order = 14)]
    [Range(1, 1000)]
    public int MinOutcomes { get; set; } = 20;

    [Display(Name = "Options levels", GroupName = "Drawing", Order = 20)]
    public bool ShowLevels { get; set; } = true;

    [Display(Name = "State ribbon", GroupName = "Drawing", Order = 21)]
    public bool ShowRibbon { get; set; } = true;

    protected override bool RequiresOptions => false;

    protected override TimeSpan ChainAge => TimeSpan.FromMinutes(10);

    /// <summary>
    /// Classifies one window ending at sample <paramref name="k"/>: Volatile when range ≥ volatile × σ; Trending when
    /// efficiency (|net move| ÷ path) ≥ the threshold and range ≥ pinned × σ; Pinned when range &lt; pinned × σ.
    /// σ = price × annual vol × √(window minutes ÷ minutes per year).
    /// </summary>
    internal static State Classify(IReadOnlyList<Sample> samples, int k, int window, double annualVol, double minutesPerYear,
        double volatileRatio, double pinnedRatio, double trendEfficiency)
    {
        double hi = double.MinValue, lo = double.MaxValue, path = 0;
        for (int j = k - window; j <= k; j++)
        {
            hi = Math.Max(hi, samples[j].Price);
            lo = Math.Min(lo, samples[j].Price);
            if (j > k - window) path += Math.Abs(samples[j].Price - samples[j - 1].Price);
        }

        double sigma = samples[k].Price * annualVol * Math.Sqrt(window * 10 / minutesPerYear);
        double range = hi - lo, eff = path > 0 ? Math.Abs(samples[k].Price - samples[k - window].Price) / path : 0;
        return range >= volatileRatio * sigma ? State.Volatile
            : eff >= trendEfficiency && range >= pinnedRatio * sigma ? State.Trending
            : range < pinnedRatio * sigma ? State.Pinned
            : State.Unsettled;
    }

    /// <summary>
    /// Annualized realized volatility of the 10-minute log returns in the 144 samples before <paramref name="k"/>
    /// (about one day; excludes sample k so the state never uses its own move); NaN with fewer than 20 returns.
    /// </summary>
    internal static double TrailingVol(IReadOnlyList<Sample> samples, int k, double minutesPerYear)
    {
        double sum = 0;
        int n = 0;
        for (int j = Math.Max(1, k - 144); j < k; j++)
        {
            if (samples[j].Price <= 0 || samples[j - 1].Price <= 0) continue;
            double r = Math.Log(samples[j].Price / samples[j - 1].Price);
            sum += r * r;
            n++;
        }

        return n >= 20 ? Math.Sqrt(sum / n * (minutesPerYear / 10)) : double.NaN;
    }

    internal override void BuildOptions(OptionsScene s, OptionsData? data)
    {
        _ribbon.Clear();
        int n = CurrentBar;
        if (n < 10) return;
        var chain = data?.Chain(Underlying, ChainAge);
        var daily = data?.Daily(Underlying) ?? Array.Empty<OptionDaily>();
        string sym = (InstrumentInfo?.Instrument ?? "").ToUpperInvariant();
        bool crypto = Opt.Crypto(Underlying) || sym.EndsWith("USDT", StringComparison.Ordinal) || sym.EndsWith("USDC", StringComparison.Ordinal);
        double minutesYear = crypto ? 365 * 1440 : 252 * 390;

        // 10-minute samples closed by the chart's bars; each is known at its last bar's close.
        var samples = new List<Sample>();
        long bucket = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            long b = TimeOf(i).Ticks / TimeSpan.FromMinutes(10).Ticks;
            if (b != bucket)
            {
                if (samples.Count > 0) samples[^1] = samples[^1] with { Closed = true };
                samples.Add(new Sample(i, EndOf(i), Close(i), DayOf(i), false));
                bucket = b;
            }
            else samples[^1] = new Sample(i, EndOf(i), Close(i), DayOf(i), false);
        }

        double liveIv = chain != null ? OptionsMath.AtmIv(chain, OptionsMath.ExpiryNear(chain, 1)) : double.NaN;
        var states = new State?[samples.Count];
        int w = Window;
        bool usedOptions = false;
        for (int k = w; k < samples.Count; k++)
        {
            if (samples[k - w].Day != samples[k].Day) continue;
            // The IV known when this sample closes: the live snapshot once it exists, else the previous day's row.
            double iv = chain != null && samples[k].End >= chain.Time && chain.Time.Date == samples[k].End.Date && double.IsFinite(liveIv) ? liveIv
                : Opt.KnownBefore(daily, samples[k].End) is { } row && row.Date.Date >= samples[k].End.Date.AddDays(-4) ? row.AtmIv : double.NaN;
            if (double.IsFinite(iv) && iv > 0) usedOptions = true;
            else iv = TrailingVol(samples, k, minutesYear);
            if (!double.IsFinite(iv) || iv <= 0) continue;
            states[k] = Classify(samples, k, w, iv, minutesYear, (double)VolatileRatio, (double)PinnedRatio, (double)TrendEfficiency);
            _ribbon.Add((samples[k].Bar, states[k]!.Value));
        }

        int last = samples.Count - 1;
        if (states[last] is not { } cur)
        {
            Status = "Not enough samples yet: needs a full window today and about 20 earlier samples.";
            return;
        }

        // How often the same state held 30, 60, 90 and 120 minutes later (counted on closed samples, not modelled).
        var horizons = new[] { 3, 6, 9, 12 };
        var odds = new double[4];
        var moves = new double[4];
        var counts = new int[4];
        for (int h = 0; h < 4; h++)
        {
            int held = 0, cnt = 0;
            double mv = 0;
            for (int k = w; k + horizons[h] < last; k++)
            {
                if (states[k] != cur || states[k + horizons[h]] == null || samples[k + horizons[h]].Day != samples[k].Day) continue;
                cnt++;
                if (states[k + horizons[h]] == cur) held++;
                mv += Math.Abs(samples[k + horizons[h]].Price / samples[k].Price - 1) * 100;
            }

            counts[h] = cnt;
            odds[h] = cnt >= MinOutcomes ? 100.0 * held / cnt : double.NaN;
            moves[h] = cnt >= MinOutcomes ? mv / cnt : double.NaN;
        }

        int longest = Enumerable.Range(0, 4).Where(h => double.IsFinite(moves[h])).DefaultIfEmpty(-1).Max();
        if (longest >= 0)
        {
            double px = Close(n - 1), mv = moves[longest] / 100 * px;
            int ahead = Math.Max(4, (int)(horizons[longest] * 10 / Math.Max(1, BarLength.TotalMinutes)));
            s.Boxes.Add(new OptionsScene.Box(n - 1, n - 1 + ahead, px + mv, px - mv, Color.FromArgb(35, 86, 160, 255), Color.FromArgb(120, 86, 160, 255), "typical range"));
        }

        if (ShowLevels && chain != null)
        {
            var (cw, pw) = OptionsMath.Walls(chain);
            int from = DayStart(n - 1);
            s.Line(from, -1, OptionsMath.GammaFlip(chain), Opt.Flip, 1, LineDashStyle.Dash, "Gamma flip");
            s.Line(from, -1, cw, Opt.Call, 1, LineDashStyle.Dash, "Call wall");
            s.Line(from, -1, pw, Opt.Put, 1, LineDashStyle.Dash, "Put wall");
        }

        double strong = odds[1];
        string name = cur switch { State.Pinned => "Pinned", State.Trending => "Trending", State.Volatile => "Volatile", _ => "Unsettled" };
        s.Title = $"Day type: {UiText.Tr(name)}";
        s.Row("Firmness (1 h)", !double.IsFinite(strong) ? "few cases" : strong >= 75 ? "firm" : strong >= 50 ? "medium" : "weak", strong >= 75 ? Opt.Call : default);
        string[] hn = { "30 min", "1 h", "1 h 30", "2 h" };
        for (int h = 0; h < 4; h++)
            s.Row($"Holds after {hn[h]}", double.IsFinite(odds[h]) ? Opt.Pct(odds[h], "0") + " · ±" + Opt.Pct(moves[h], "0.00") : $"— ({counts[h]})");
        s.Row("Volatility", usedOptions ? "options IV" : "realized");
        s.Footer = samples[last].Closed ? "States known at each 10-minute close" : "States known at each 10-minute close; the last is forming";
    }

    protected override void RenderBefore(RenderContext ctx, Rectangle region)
    {
        if (!ShowRibbon || _ribbon.Count == 0) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        for (int k = 0; k < _ribbon.Count; k++)
        {
            var (bar, st) = _ribbon[k];
            if (bar < first || bar > last) continue;
            int next = k + 1 < _ribbon.Count ? _ribbon[k + 1].Bar : bar + 1;
            var col = st switch
            {
                State.Volatile => Color.FromArgb(200, 239, 83, 80),
                State.Trending => Color.FromArgb(200, 41, 98, 255),
                State.Pinned => Color.FromArgb(200, 230, 232, 238),
                _ => Color.FromArgb(110, 120, 123, 134),
            };
            int x0 = X(bar), x1 = X(Math.Min(next, bar + 50));
            ctx.FillRectangle(col, new Rectangle(x0, region.Bottom - 6, Math.Max(1, x1 - x0), 5));
        }
    }
}
