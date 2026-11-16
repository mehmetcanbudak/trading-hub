// Behavior adapted from the KVP capability reference (commits 7fbf237, 644b8ca); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using TradingHub.Feeds.External;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Options;

// Options studies read the shared options data (Connections → Options data). Without a connection they show a waiting
// message and make no request; once one is enabled they recalculate by themselves. The chart symbol selects the
// underlying (BTCUSDT → BTC). Every card names its source and snapshot time.

/// <summary>Which expiry a study uses.</summary>
public enum ExpiryPick
{
    [Display(Name = "All expiries")] All,
    [Display(Name = "Nearest")] Nearest,
    [Display(Name = "Next")] Next,
    [Display(Name = "Weekly (≈ 7 days)")] Weekly,
    [Display(Name = "Monthly (≈ 30 days)")] Monthly,
    [Display(Name = "Quarterly (≈ 90 days)")] Quarterly,
}

/// <summary>Where a study's information card sits.</summary>
public enum CardCorner
{
    [Display(Name = "Top left")] TopLeft,
    [Display(Name = "Top center")] TopCenter,
    [Display(Name = "Top right")] TopRight,
    [Display(Name = "Middle left")] MiddleLeft,
    [Display(Name = "Center")] Center,
    [Display(Name = "Middle right")] MiddleRight,
    [Display(Name = "Bottom left")] BottomLeft,
    [Display(Name = "Bottom center")] BottomCenter,
    [Display(Name = "Bottom right")] BottomRight,
}

/// <summary>Formatting, colors and chain helpers shared by the options studies.</summary>
internal static class Opt
{
    public static readonly Color Call = Color.FromArgb(255, 38, 194, 129);
    public static readonly Color Put = Color.FromArgb(255, 240, 82, 90);
    public static readonly Color Flip = Color.FromArgb(255, 251, 191, 36);
    public static readonly Color Text = Color.FromArgb(235, 230, 232, 238);
    public static readonly Color Dim = Color.FromArgb(200, 160, 166, 178);
    public static readonly Color Muted = Color.FromArgb(255, 140, 145, 160);
    public static readonly Color Blue = Color.FromArgb(255, 86, 160, 255);
    public static readonly Color Violet = Color.FromArgb(255, 165, 120, 255);

    public static Color A(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

    /// <summary>USD with a K/M/B suffix ($1.2M); "—" when unknown.</summary>
    public static string Usd(double v)
    {
        if (!double.IsFinite(v)) return "—";
        double a = Math.Abs(v);
        string body = a >= 1e9 ? (a / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B"
            : a >= 1e6 ? (a / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M"
            : a >= 1e3 ? (a / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K"
            : a.ToString("0", CultureInfo.InvariantCulture);
        return (v < 0 ? "−$" : "$") + body;
    }

    public static string SignedUsd(double v) => double.IsFinite(v) ? (v > 0 ? "+" : "") + Usd(v) : "—";

    public static string Pct(double v, string fmt = "0.0") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) + " %" : "—";

    public static string N(double v, string fmt = "0.##") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    public static Color BiasColor(double v) => v > 0 ? Call : v < 0 ? Put : Muted;

    public static bool Crypto(string underlying) => underlying is "BTC" or "ETH" or "SOL" or "XRP" or "DOGE" or "BNB" or "AVAX" or "LTC";

    /// <summary>Trading days per year for annualizing: 365 for crypto, 252 otherwise.</summary>
    public static double Year(string underlying) => Crypto(underlying) ? 365 : 252;

    /// <summary>The selected expiry (null = all).</summary>
    public static DateTime? Pick(OptionsChain c, ExpiryPick pick)
    {
        var exps = c.Contracts.Select(o => o.Expiry).Distinct().Where(e => e > c.Time).OrderBy(e => e).ToList();
        if (exps.Count == 0 || pick == ExpiryPick.All) return null;
        return pick switch
        {
            ExpiryPick.Nearest => exps[0],
            ExpiryPick.Next => exps[Math.Min(1, exps.Count - 1)],
            ExpiryPick.Weekly => OptionsMath.ExpiryNear(c, 7),
            ExpiryPick.Monthly => OptionsMath.ExpiryNear(c, 30),
            _ => OptionsMath.ExpiryNear(c, 90),
        };
    }

    public static IEnumerable<OptionContract> Of(OptionsChain c, DateTime? expiry) => expiry == null ? c.Contracts : c.Contracts.Where(o => o.Expiry == expiry);

    public static string ExpiryText(DateTime? e) => e == null ? UiText.Tr("all expiries") : e.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"C 100k 12-27".</summary>
    public static string Contract(OptionRight r, double strike, DateTime expiry) =>
        $"{(r == OptionRight.Call ? "C" : "P")} {Strike(strike)} {expiry.ToString("MM-dd", CultureInfo.InvariantCulture)}";

    public static string Strike(double k) => k >= 10000 ? (k / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : k.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The smallest positive gap between consecutive strikes (1 % of spot without two strikes).</summary>
    public static double StrikeStep(IEnumerable<double> strikes, double spot)
    {
        var list = strikes.Distinct().OrderBy(k => k).ToList();
        return list.Count > 1 ? list.Zip(list.Skip(1), (a, b) => b - a).Where(d => d > 0).DefaultIfEmpty(spot * 0.01).Min() : spot * 0.01;
    }

    /// <summary>The row of the latest UTC day strictly before <paramref name="day"/> (the newest row known at that day's start).</summary>
    public static OptionDaily? KnownBefore(IReadOnlyList<OptionDaily> days, DateTime day)
    {
        int lo = 0, hi = days.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (days[mid].Date.Date < day.Date)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans >= 0 ? days[ans] : null;
    }

    public static DashStyle Dash(LineDashStyle s) => s switch
    {
        LineDashStyle.Dash => DashStyle.Dash,
        LineDashStyle.Dot => DashStyle.Dot,
        LineDashStyle.DashDot => DashStyle.DashDot,
        LineDashStyle.DashDotDot => DashStyle.DashDotDot,
        _ => DashStyle.Solid,
    };

    /// <summary>Exponential moving average (seeded with the first value); same length as the input.</summary>
    public static double[] Ema(IReadOnlyList<double> x, int length)
    {
        var res = new double[x.Count];
        double k = 2.0 / (length + 1);
        for (int i = 0; i < x.Count; i++) res[i] = i == 0 ? x[0] : res[i - 1] + k * (x[i] - res[i - 1]);
        return res;
    }

    /// <summary>Horizontal bars per strike docked at the right edge (left/right sides, or signed around the middle).</summary>
    public static void DockBars(RenderContext ctx, Rectangle region, Func<double, int> y, IReadOnlyList<(double Strike, double Left, double Right)> rows, int width, double step,
        Color left, Color right, bool signed)
    {
        if (rows.Count == 0) return;
        double max = rows.Max(r => Math.Max(Math.Abs(r.Left), Math.Abs(r.Right)));
        if (max <= 0) return;
        int edge = region.Right - 4, mid = edge - width / 2;
        int h = Math.Clamp(Math.Abs(y(rows[0].Strike) - y(rows[0].Strike + step)) - 1, 2, 14);
        ctx.FillRectangle(Color.FromArgb(70, 14, 17, 24), new Rectangle(edge - width - 2, region.Top, width + 4, region.Height));
        foreach (var r in rows)
        {
            int cy = y(r.Strike);
            if (cy < region.Top || cy > region.Bottom) continue;
            if (signed)
            {
                int w = (int)(width / 2.0 * Math.Abs(r.Right) / max);
                ctx.FillRectangle(r.Right >= 0 ? right : left, new Rectangle(r.Right >= 0 ? mid : mid - w, cy - h / 2, Math.Max(1, w), h));
            }
            else
            {
                int wl = (int)(width / 2.0 * Math.Abs(r.Left) / max), wr = (int)(width / 2.0 * Math.Abs(r.Right) / max);
                if (wl > 0) ctx.FillRectangle(left, new Rectangle(mid - wl, cy - h / 2, wl, h));
                if (wr > 0) ctx.FillRectangle(right, new Rectangle(mid, cy - h / 2, wr, h));
            }
        }

        ctx.DrawLine(new RenderPen(Color.FromArgb(90, 160, 165, 175), 1), mid, region.Top, mid, region.Bottom);
    }
}

/// <summary>A titled name–value card and a centered message, drawn in any pane.</summary>
internal static class StudyCard
{
    public static readonly RenderFont Font = new("Arial", 8);
    public static readonly RenderFont Bold = new("Arial", 8, FontStyle.Bold);

    public static Rectangle Draw(RenderContext ctx, Rectangle region, string? title, IReadOnlyList<(string Name, string Value, Color Color)> rows, CardCorner corner,
        string? footer = null, int top = 22)
    {
        var tr = rows.Select(r => (Name: UiText.Tr(r.Name), Value: UiText.Tr(r.Value), r.Color)).ToList();
        string? t = title == null ? null : UiText.Tr(title);
        string? f = footer == null ? null : UiText.Tr(footer);
        int nameW = tr.Count == 0 ? 0 : tr.Max(r => ctx.MeasureString(r.Name, Font).Width);
        int valW = tr.Count == 0 ? 0 : tr.Max(r => ctx.MeasureString(r.Value, Bold).Width);
        int rowH = ctx.MeasureString("Ag", Font).Height + 3;
        int w = Math.Max(Math.Max(t == null ? 0 : ctx.MeasureString(t, Bold).Width, f == null ? 0 : ctx.MeasureString(f, Font).Width), nameW + valW + 18) + 16;
        w = Math.Min(w, Math.Max(80, region.Width - 16));
        int h = tr.Count * rowH + (t != null ? rowH + 2 : 0) + (f != null ? rowH + 2 : 0) + 10;
        int col = (int)corner % 3, row = (int)corner / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + top, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        var rect = new Rectangle(x, y, w, h);
        ctx.FillRectangle(Color.FromArgb(215, 18, 20, 26), rect, 5);
        ctx.DrawRectangle(new RenderPen(Color.FromArgb(90, 120, 130, 150), 1), rect, 5);
        int cy = y + 5;
        if (t != null)
        {
            ctx.DrawString(t, Bold, Color.FromArgb(235, 235, 240), x + 8, cy);
            cy += rowH + 2;
        }

        foreach (var r in tr)
        {
            ctx.DrawString(r.Name, Font, Color.FromArgb(160, 165, 175), x + 8, cy);
            var vs = ctx.MeasureString(r.Value, Bold);
            ctx.DrawString(r.Value, Bold, r.Color.A > 0 ? r.Color : Color.FromArgb(235, 235, 240), x + w - vs.Width - 8, cy);
            cy += rowH;
        }

        if (f != null) ctx.DrawString(f, Font, Opt.Dim, x + 8, cy + 2);
        return rect;
    }

    /// <summary>A centered status message (waiting for a connection, loading…).</summary>
    public static void Message(RenderContext ctx, Rectangle region, string text)
    {
        var t = UiText.Tr(text);
        var font = new RenderFont("Arial", 9);
        var size = ctx.MeasureString(t, font);
        int x = region.Left + Math.Max(8, (region.Width - size.Width) / 2), y = region.Top + Math.Max(18, (region.Height - size.Height) / 2);
        ctx.FillRectangle(Color.FromArgb(200, 18, 20, 26), new Rectangle(x - 8, y - 4, size.Width + 16, size.Height + 8), 4);
        ctx.DrawString(t, font, Color.FromArgb(200, 190, 196, 206), x, y);
    }

    /// <summary>A 0–1 ring (dim track and colored arc).</summary>
    public static void Ring(RenderContext ctx, int cx, int cy, int r, double frac, Color color)
    {
        var rect = new Rectangle(cx - r, cy - r, 2 * r, 2 * r);
        ctx.DrawEllipse(new RenderPen(Color.FromArgb(70, 140, 145, 160), 3), rect);
        if (!double.IsFinite(frac)) return;
        ctx.DrawArc(new RenderPen(color, 3), rect, -90, (float)(360 * Math.Clamp(frac, 0, 1)));
    }

    /// <summary>"Deribit · snapshot 2026-11-05 10:00 UTC" for a chain.</summary>
    public static string Provenance(OptionsChain c) =>
        $"{c.Source} · snapshot {c.Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC";
}

/// <summary>What an overlay study draws on the price pane, rebuilt from the data on each refresh.</summary>
internal sealed class OptionsScene
{
    public readonly record struct Level(int From, int To, double Price, Color Color, int Width, LineDashStyle Dash, string? Label);

    public readonly record struct PathLine(IReadOnlyList<(int Bar, double Price)> Points, Color Color, int Width, LineDashStyle Dash);

    public readonly record struct Mark(int Bar, double Price, Color Color, int Size, string? Text);

    public readonly record struct Box(int From, int To, double Top, double Bottom, Color Fill, Color Border, string? Label);

    public readonly List<Level> Levels = new();
    public readonly List<PathLine> Paths = new();
    public readonly List<Mark> Marks = new();
    public readonly List<Box> Boxes = new();
    public readonly List<(string Name, string Value, Color Color)> Rows = new();
    public string? Title;
    public string? Footer;

    /// <summary>A horizontal level from <paramref name="from"/> to <paramref name="to"/> (−1 = right edge).</summary>
    public void Line(int from, int to, double price, Color color, int width, LineDashStyle dash, string? label = null)
    {
        if (double.IsFinite(price) && price > 0) Levels.Add(new Level(from, to, price, color, width, dash, label));
    }

    public void Row(string name, string value, Color color = default) => Rows.Add((name, value, color));
}

/// <summary>
/// Base of the options studies drawn over price: levels, paths, marks and a card. The scene is rebuilt when options
/// data arrives, when bars are added and at most every <see cref="Refresh"/> otherwise.
/// </summary>
public abstract class OptionsOverlay : Indicator
{
    private readonly Action _changed, _tick;
    private OptionsData? _subscribed;
    private (int Bars, long Generation) _built = (-1, -1);
    private DateTime _builtAt;
    private int[] _day = Array.Empty<int>();
    private CardCorner _corner = CardCorner.TopRight;

    internal OptionsScene Scene { get; private set; } = new();

    protected OptionsOverlay() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("Options", "Options") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        // Levels and cards must stay readable over candles and footprint cells.
        DrawAbovePrice = true;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _changed = () => DoActionInGuiThread(() =>
        {
            if (Data is { } d && d.Generation != _built.Generation) Rebuild();
        });
        _tick = () =>
        {
            if (DateTime.UtcNow - _builtAt >= Refresh) Rebuild();
        };
    }

    [Display(Name = "Show the card", GroupName = "Card", Order = 900)]
    public bool ShowCard { get; set; } = true;

    [Display(Name = "Card position", GroupName = "Card", Order = 901)]
    public CardCorner CardPosition { get => _corner; set => _corner = value; }

    /// <summary>The options data; defaults to the application's shared instance.</summary>
    [Browsable(false)]
    public OptionsData? Service { get; set; }

    /// <summary>Why nothing is drawn (null when the study has data).</summary>
    [Browsable(false)]
    public string? Status { get; protected set; }

    /// <summary>The card footer naming the data source and snapshot time (null before data arrives).</summary>
    [Browsable(false)]
    public string? Provenance => Scene.Footer;

    protected OptionsData? Data => Service ?? OptionsData.Shared;

    /// <summary>The chart's options underlying (BTC, ETH, SPY…).</summary>
    protected string Underlying { get; private set; } = "";

    /// <summary>False for studies that read their own list of underlyings instead of the chart's.</summary>
    protected virtual bool UsesChartUnderlying => true;

    /// <summary>How often the chain is requested by this study.</summary>
    protected virtual TimeSpan ChainAge => TimeSpan.FromSeconds(60);

    /// <summary>The longest a scene stays unrebuilt.</summary>
    protected virtual TimeSpan Refresh => TimeSpan.FromSeconds(15);

    protected int LastBar => CurrentBar - 1;

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(5), _tick);

    protected override void OnDispose()
    {
        UnsubscribeFromTimer(TimeSpan.FromSeconds(5), _tick);
        if (_subscribed != null) _subscribed.Changed -= _changed;
        _subscribed = null;
    }

    protected override void OnRecalculate() => _built = (-1, -1);

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar != CurrentBar - 1) return;
        if (_built.Bars == CurrentBar && _built.Generation == (Data?.Generation ?? -1) && DateTime.UtcNow - _builtAt < Refresh) return;
        Rebuild();
    }

    private void Rebuild()
    {
        var data = Data;
        if (!ReferenceEquals(data, _subscribed))
        {
            if (_subscribed != null) _subscribed.Changed -= _changed;
            _subscribed = data;
            if (data != null) data.Changed += _changed;
        }

        _built = (CurrentBar, data?.Generation ?? -1);
        _builtAt = DateTime.UtcNow;
        var scene = new OptionsScene();
        Underlying = OptionsData.UnderlyingOf(InstrumentInfo?.Instrument, InstrumentInfo?.BaseAsset);
        Status = data?.Provider == null ? OptionsData.NotConfigured : UsesChartUnderlying ? data.Unavailable(Underlying) : null;
        bool options = Status == null;
        if (!options && !RequiresOptions) Status = null;
        if (Status == null && CurrentBar >= 2)
        {
            _day = new int[CurrentBar];
            for (int i = 1; i < CurrentBar; i++) _day[i] = _day[i - 1] + (IsNewSession(i) ? 1 : 0);
            _usedTrades = false;
            BuildOptions(scene, options ? data : null);
            // Trade-based studies name any span a page limit left unloaded (gaps, never zero).
            if (_usedTrades && options && data!.TradeCoverageNote(Underlying, TimeOf(0), EndOf(CurrentBar - 1)) is { } note)
                scene.Footer = scene.Footer == null ? note : scene.Footer + " · " + note;
        }

        Scene = scene;
        RedrawChart();
    }

    /// <summary>False for studies that also work without options data (they then receive a null source).</summary>
    protected virtual bool RequiresOptions => true;

    /// <summary>Fills the scene with or without options data (<see cref="RequiresOptions"/> false); default: <see cref="Build"/>.</summary>
    internal virtual void BuildOptions(OptionsScene s, OptionsData? data) => Build(s, data!);

    /// <summary>Fills the scene from the options data; set <see cref="Status"/> when there is nothing to show yet.</summary>
    internal virtual void Build(OptionsScene s, OptionsData data)
    {
    }

    /// <summary>The chain, or null with <see cref="Status"/> set to loading or the provider error.</summary>
    protected OptionsChain? GetChain(OptionsData data)
    {
        var c = data.Chain(Underlying, ChainAge);
        if (c == null) Status = data.ChainError(Underlying) is { } e ? "Options: " + e : $"Loading the {Underlying} option chain…";
        return c;
    }

    protected DateTime TimeOf(int i) => GetCandle(i).Time;

    protected double Close(int i) => (double)GetCandle(i).Close;

    protected TimeSpan BarLength => ChartInfo?.GetTimeFrameTypeChartPeriod() is { } p && p > TimeSpan.Zero ? p : TimeSpan.Zero;

    /// <summary>The end of bar <paramref name="i"/> (the next bar's open; for the last bar its open plus the bar length).</summary>
    protected DateTime EndOf(int i) => i + 1 < CurrentBar ? TimeOf(i + 1) : TimeOf(i) + (BarLength > TimeSpan.Zero ? BarLength : TimeSpan.FromMinutes(1));

    /// <summary>The trading-day index of bar <paramref name="i"/> (increments at each new session).</summary>
    protected int DayOf(int i) => i >= 0 && i < _day.Length ? _day[i] : 0;

    /// <summary>The first bar of bar <paramref name="i"/>'s trading day.</summary>
    protected int DayStart(int i)
    {
        int k = Math.Clamp(i, 0, CurrentBar - 1);
        while (k > 0 && DayOf(k - 1) == DayOf(i)) k--;
        return k;
    }

    /// <summary>The bar containing <paramref name="t"/> (−1 before the first bar).</summary>
    protected int BarOf(DateTime t)
    {
        int lo = 0, hi = CurrentBar - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (TimeOf(mid) <= t)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans;
    }

    private bool _usedTrades;

    protected IReadOnlyList<OptionTrade> TradesOf(OptionsData data, int i) => TradesBetween(data, TimeOf(i), EndOf(i));

    /// <summary>The chart underlying's loaded trades in [from, to).</summary>
    protected IReadOnlyList<OptionTrade> TradesBetween(OptionsData data, DateTime from, DateTime to)
    {
        _usedTrades = true;
        return data.TradesBetween(Underlying, from, to);
    }

    /// <summary>Today's trades and whether all of today is loaded (an empty list is then a real absence).</summary>
    protected (IReadOnlyList<OptionTrade> Trades, bool Complete) TodayTrades(OptionsData data)
    {
        DateTime from = TimeOf(DayStart(LastBar)), to = EndOf(LastBar);
        var trades = TradesBetween(data, from, to);
        var now = DateTime.UtcNow;
        return (trades, data.TradesCovered(Underlying, from, to < now ? to : now.AddMinutes(-2)));
    }

    /// <summary>Annualized realized volatility of daily closes over the last <paramref name="days"/> trading days of the chart.</summary>
    protected double RealizedVol(int days)
    {
        var closes = new List<double>();
        for (int i = 0; i < CurrentBar; i++)
            if (i == CurrentBar - 1 || DayOf(i + 1) != DayOf(i)) closes.Add(Close(i));
        return OptionsMath.RealizedVol(closes.Skip(Math.Max(0, closes.Count - days - 1)).ToList(), Opt.Year(Underlying));
    }

    protected string Price(double p) => !double.IsFinite(p) || Math.Abs(p) > 1e15 ? "—"
        : ChartInfo?.GetPriceString((decimal)p) ?? p.ToString("0.##", CultureInfo.InvariantCulture);

    protected int X(int bar) => ChartInfo.GetXByBar(bar);

    protected int Y(double price) => ChartInfo.GetYByPrice((decimal)price);

    protected sealed override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        var region = ChartInfo.PriceChartContainer.Region;
        if (Status != null)
        {
            StudyCard.Message(ctx, region, Status);
            return;
        }

        var s = Scene;
        RenderBefore(ctx, region);
        var font = StudyCard.Font;
        foreach (var b in s.Boxes)
        {
            int x0 = X(b.From), x1 = X(b.To), y0 = Y(b.Top), y1 = Y(b.Bottom);
            var rect = new Rectangle(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(1, Math.Abs(x1 - x0)), Math.Max(1, Math.Abs(y1 - y0)));
            ctx.FillRectangle(b.Fill, rect);
            ctx.DrawRectangle(new RenderPen(b.Border, 1, DashStyle.Dash), rect);
            if (b.Label != null) ctx.DrawString(UiText.Tr(b.Label), font, b.Border, rect.Left + 2, rect.Top + 2);
        }

        foreach (var l in s.Levels)
        {
            int y = Y(l.Price);
            if (y < region.Top || y > region.Bottom) continue;
            int x0 = Math.Max(region.Left, X(l.From)), x1 = l.To < 0 ? region.Right : Math.Min(region.Right, X(l.To + 1));
            if (x1 <= x0) continue;
            ctx.DrawLine(new RenderPen(l.Color, l.Width, Opt.Dash(l.Dash)), x0, y, x1, y);
            if (l.Label != null)
            {
                string t = UiText.Tr(l.Label);
                int w = ctx.MeasureString(t, font).Width;
                ctx.DrawString(t, font, l.Color, Math.Max(x0, x1 - w - 70), y - 13);
            }
        }

        foreach (var p in s.Paths)
        {
            if (p.Points.Count < 2) continue;
            var pts = p.Points.Select(q => new Point(X(q.Bar), Y(q.Price))).ToArray();
            ctx.DrawLines(new RenderPen(p.Color, p.Width, Opt.Dash(p.Dash)), pts);
        }

        foreach (var m in s.Marks)
        {
            if (m.Bar < FirstVisibleBarNumber || m.Bar > LastVisibleBarNumber) continue;
            int x = ChartInfo.GetXByBar(m.Bar, false), y = Y(m.Price);
            ctx.FillEllipse(m.Color, new Rectangle(x - m.Size / 2, y - m.Size / 2, m.Size, m.Size));
            if (m.Text != null) ctx.DrawString(m.Text, font, Opt.Text, x + m.Size / 2 + 2, y - 6);
        }

        RenderAfter(ctx, region);
        if (ShowCard && (s.Rows.Count > 0 || s.Title != null))
            StudyCard.Draw(ctx, region, s.Title, s.Rows, CardPosition, s.Footer, 26);
    }

    /// <summary>Custom drawing under the levels (heatmaps, profiles).</summary>
    protected virtual void RenderBefore(RenderContext ctx, Rectangle region)
    {
    }

    /// <summary>Custom drawing over the levels (docked bars, grids).</summary>
    protected virtual void RenderAfter(RenderContext ctx, Rectangle region)
    {
    }
}

/// <summary>
/// Base of the options studies in their own pane. Bars outside the loaded trade coverage are gaps, never zero; series
/// recalculate when options data arrives.
/// </summary>
public abstract class OptionsPane : Indicator
{
    private readonly Action _changed, _tick;
    private OptionsData? _subscribed;
    private long _generation = -1;

    protected OptionsPane() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _changed = () => DoActionInGuiThread(() =>
        {
            if (Data is { } d && d.Generation != _generation) RecalculateValues();
        });
        _tick = () =>
        {
            // Asks for new trades and the chain; arrivals then recalculate through Changed.
            if (Status == null && Data is { } d && CurrentBar > 0)
            {
                d.TradesBetween(Underlying, GetCandle(CurrentBar - 1).Time, DateTime.UtcNow);
                Poll(d);
            }
        };
    }

    [Display(Name = "Show the card", GroupName = "Card", Order = 900)]
    public bool ShowCard { get; set; } = true;

    [Display(Name = "Card position", GroupName = "Card", Order = 901)]
    public CardCorner CardPosition { get; set; } = CardCorner.TopRight;

    [Browsable(false)]
    public OptionsData? Service { get; set; }

    [Browsable(false)]
    public string? Status { get; protected set; }

    protected OptionsData? Data => Service ?? OptionsData.Shared;

    protected string Underlying { get; private set; } = "";

    /// <summary>False for studies that read their own list of underlyings instead of the chart's.</summary>
    protected virtual bool UsesChartUnderlying => true;

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(30), _tick);

    protected override void OnDispose()
    {
        UnsubscribeFromTimer(TimeSpan.FromSeconds(30), _tick);
        if (_subscribed != null) _subscribed.Changed -= _changed;
        _subscribed = null;
    }

    protected sealed override void OnRecalculate()
    {
        var data = Data;
        if (!ReferenceEquals(data, _subscribed))
        {
            if (_subscribed != null) _subscribed.Changed -= _changed;
            _subscribed = data;
            if (data != null) data.Changed += _changed;
        }

        _generation = data?.Generation ?? -1;
        Underlying = OptionsData.UnderlyingOf(InstrumentInfo?.Instrument, InstrumentInfo?.BaseAsset);
        Status = data?.Provider == null ? OptionsData.NotConfigured : UsesChartUnderlying ? data.Unavailable(Underlying) : null;
        Reset();
    }

    /// <summary>Clears per-calculation state.</summary>
    protected virtual void Reset()
    {
    }

    /// <summary>Periodic requests the study needs besides recent trades (for example the chain).</summary>
    protected virtual void Poll(OptionsData data)
    {
    }

    protected sealed override void OnCalculate(int bar, decimal value)
    {
        if (Status != null || Data is not { } data)
        {
            foreach (var s in DataSeries.OfType<OptionalValueDataSeries>()) s.SetUnavailable(bar);
            return;
        }

        Calc(bar, data);
    }

    protected abstract void Calc(int bar, OptionsData data);

    protected DateTime BarEnd(int bar) => bar + 1 < CurrentBar ? GetCandle(bar + 1).Time
        : GetCandle(bar).Time + (ChartInfo?.GetTimeFrameTypeChartPeriod() is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromMinutes(1));

    private bool _usedTrades;

    /// <summary>The bar's trades, or null when the bar is outside the loaded coverage (a gap).</summary>
    protected IReadOnlyList<OptionTrade>? TradesOf(OptionsData data, int bar)
    {
        DateTime from = GetCandle(bar).Time, to = BarEnd(bar), now = DateTime.UtcNow;
        var trades = data.TradesBetween(Underlying, from, to);
        _usedTrades = true;
        return data.TradesCovered(Underlying, from, to < now ? to : now.AddMinutes(-2)) ? trades : null;
    }

    protected bool NewDay(int bar) => bar == 0 || IsNewSession(bar);

    protected sealed override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        if (Status != null)
        {
            StudyCard.Message(ctx, box.Region, Status);
            return;
        }

        Render(ctx, box);
        if (_usedTrades && CurrentBar > 0 && Data?.TradeCoverageNote(Underlying, GetCandle(0).Time, BarEnd(CurrentBar - 1)) is { } note)
            ctx.DrawString(UiText.Tr(note), StudyCard.Font, Opt.Flip, box.Region.Left + 6, box.Region.Top + 32);
    }

    protected virtual void Render(RenderContext ctx, IChartContainer box)
    {
    }

    protected void Card(RenderContext ctx, IChartContainer box, string? title, IReadOnlyList<(string, string, Color)> rows, string? footer = null)
    {
        if (ShowCard) StudyCard.Draw(ctx, box.Region, title, rows, CardPosition, footer);
    }

    /// <summary>A small note at the top left of the pane ("loading trades…").</summary>
    protected static void Note(RenderContext ctx, IChartContainer box, string text) =>
        ctx.DrawString(UiText.Tr(text), StudyCard.Font, Opt.Dim, box.Region.Left + 6, box.Region.Top + 18);

    /// <summary>The value at <paramref name="bar"/> if available.</summary>
    protected static double? At(OptionalValueDataSeries s, int bar) => s.IsAvailable(bar) ? (double)s[bar] : null;

    protected static void Set(OptionalValueDataSeries s, int bar, double v)
    {
        if (double.IsFinite(v)) s.Set(bar, (decimal)v);
        else s.SetUnavailable(bar);
    }
}
