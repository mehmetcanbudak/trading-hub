// Behavior adapted from the KVP capability reference (commits a9c40ae, 3629e91, b163ae8); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Data;
using TradingHub.Feeds.Derivatives;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Aggressor flow per exchange in its own pane: an "all markets" row on top (spot and perpetual separately), then one row
/// per exchange ranked by traded notional in the chosen window. Each row has its own scale and shows the cumulative net
/// aggressor notional (market buys − market sells). Lamps show which side leads over the last 1, 5, 15 and 60 minutes and
/// per trade-size band. Color says which market (spot blue, perpetual amber), never buy or sell. Prints whose market is
/// unknown and bars without measured prints are not attributed to any exchange; such gaps break the lines.
/// </summary>
[DisplayName("Flow by exchange (ranked, spot / perpetual)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Cumulative net aggressor notional of each exchange of an aggregate instrument, ranked by traded notional, spot and perpetual separately, with an all-markets row and 1/5/15/60-minute and trade-size lamps. Unattributed prints and candle-only history stay out of the exchange rows.")]
public sealed class FlowByExchange : Indicator
{
    public enum RankWindow
    {
        [Display(Name = "Visible bars")] Visible,
        [Display(Name = "15 minutes")] Minutes15,
        [Display(Name = "1 hour")] Hour1,
        [Display(Name = "24 hours")] Hours24,
    }

    public enum RowHeights
    {
        [Display(Name = "Golden ratio (each row 1/φ of the one above)")] Golden,
        [Display(Name = "By share of traded notional")] Share,
        [Display(Name = "Equal")] Equal,
    }

    public enum MarketFilter
    {
        [Display(Name = "Spot and perpetual")] Both,
        [Display(Name = "Spot only")] Spot,
        [Display(Name = "Perpetual only")] Perpetual,
    }

    /// <summary>One exchange in the ranking: its market indexes and window totals.</summary>
    internal sealed class ExchangeRow
    {
        public string Name = "";
        public readonly List<int> Spot = new(), Perpetual = new();
        public decimal Gross, NetSpot, NetPerpetual;
        public decimal Net => NetSpot + NetPerpetual;
    }

    private readonly MarketFlowCache _cache = new();
    // Net notional per market of the bars before the drawn range ("accumulate from the first bar"), kept between frames.
    private decimal[] _before = Array.Empty<decimal>();
    private int _beforeBars;
    private readonly RenderFont _font = new("Arial", 8), _bold = new("Arial", 8, FontStyle.Bold);

    public FlowByExchange() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = new ValueDataSeries("FLOW", "Flow") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Rank by traded notional over", GroupName = "Calculation", Order = 10)]
    public RankWindow Ranking { get; set; } = RankWindow.Hour1;

    [Display(Name = "Markets", GroupName = "Calculation", Order = 15)]
    public MarketFilter Markets { get; set; } = MarketFilter.Both;

    [Display(Name = "Warn when spot and perpetual disagree from (notional)", GroupName = "Calculation", Order = 40)]
    [Range(0, 1_000_000_000_000)]
    public decimal DivergenceNotional { get; set; } = 2_000_000;

    [Display(Name = "Exchange rows (maximum)", GroupName = "Visualization", Order = 20)]
    [Range(1, 16)]
    public int MaxRows { get; set; } = 8;

    [Display(Name = "Row heights", GroupName = "Visualization", Order = 25)]
    public RowHeights Heights { get; set; } = RowHeights.Golden;

    [Display(Name = "Flow and trade-size lamps on top", GroupName = "Visualization", Order = 30)]
    public bool ShowLamps { get; set; } = true;

    [Display(Name = "Accumulate from the first bar (otherwise from the left edge)", GroupName = "Visualization", Order = 35)]
    public bool SinceStart { get; set; }

    [Display(Name = "Label width (px)", GroupName = "Visualization", Order = 45)]
    [Range(80, 400)]
    public int LabelWidth { get; set; } = 170;

    /// <summary>Exchanges ranked in the last render, for tests and the legend.</summary>
    [Browsable(false)]
    public IReadOnlyList<string> RankedExchanges { get; private set; } = Array.Empty<string>();

    protected override void OnRecalculate()
    {
        _cache.Reset(InstrumentInfo?.Sources ?? Array.Empty<InstrumentSource>(), FlowFormat.PointValue(this));
        _before = Array.Empty<decimal>();
        _beforeBars = 0;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    private bool Includes(bool spot) => Markets switch { MarketFilter.Spot => spot, MarketFilter.Perpetual => !spot, _ => true };

    internal MarketFlowBar Flow(int bar) => _cache.Get(this, bar, GetBarTrades);

    /// <summary>Exchanges of <paramref name="sources"/> with their totals over <paramref name="bars"/>, ranked by gross notional (zero-notional exchanges dropped).</summary>
    internal static List<ExchangeRow> Rank(IReadOnlyList<InstrumentSource> sources, IEnumerable<MarketFlowBar> bars, MarketFilter filter)
    {
        var rows = new Dictionary<string, ExchangeRow>(StringComparer.Ordinal);
        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            if (!rows.TryGetValue(s.Venue, out var row)) rows[s.Venue] = row = new ExchangeRow { Name = s.Venue };
            if (s.IsSpot ? filter != MarketFilter.Perpetual : filter != MarketFilter.Spot) (s.IsSpot ? row.Spot : row.Perpetual).Add(i);
        }
        foreach (var f in bars)
            foreach (var row in rows.Values)
            {
                foreach (int i in row.Spot) { row.Gross += f.Gross(i); row.NetSpot += f.Net(i); }
                foreach (int i in row.Perpetual) { row.Gross += f.Gross(i); row.NetPerpetual += f.Net(i); }
            }
        return rows.Values.Where(r => r.Gross > 0).OrderByDescending(r => r.Gross).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    private int RankStart(int first)
    {
        if (Ranking == RankWindow.Visible) return first;
        var span = Ranking switch { RankWindow.Minutes15 => TimeSpan.FromMinutes(15), RankWindow.Hour1 => TimeSpan.FromHours(1), _ => TimeSpan.FromHours(24) };
        var end = GetCandle(CurrentBar - 1).Time + (ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero);
        int b = CurrentBar - 1;
        while (b > 0 && GetCandle(b - 1).Time >= end - span) b--;
        return b;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container == null || CurrentBar <= 0) return;
        var r = Container.Region;
        var pc = ChartInfo.PriceChartContainer;
        int first = Math.Max(0, pc.FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, pc.LastVisibleBarNumber);
        if (last < first) return;
        var sources = _cache.Sources;
        int y = r.Top + 15;
        context.FillRectangle(Color.FromArgb(60, 0, 0, 0), r);
        if (sources.Count <= 1)
        {
            context.DrawString("Single-market instrument: use an aggregate connection for one row per exchange.", _font, FlowFormat.Dim, r.Left + 8, y);
            return;
        }

        int rankFrom = RankStart(first);
        _cache.Retain(rankFrom, CurrentBar - 1, first, last);
        var before = SinceStart ? NetBefore(first) : null;
        var window = Enumerable.Range(rankFrom, CurrentBar - rankFrom).Select(Flow).ToList();
        var ranked = Rank(sources, window, Markets);
        decimal total = ranked.Sum(x => x.Gross);
        decimal unattributed = window.Sum(f => f.UnattributedBuy + f.UnattributedSell);
        var shown = ranked.Take(Math.Clamp(MaxRows, 1, 16)).ToList();
        RankedExchanges = ranked.Select(x => x.Name).ToList();
        int hidden = ranked.Count - shown.Count;

        if (ShowLamps) y = DrawLamps(context, r, y) + 2;
        int available = r.Bottom - y - (hidden > 0 ? 12 : 0);
        if (available < 30) return;
        int allHeight = Math.Max(28, (int)(available * (shown.Count > 0 ? 0.26 : 1)));
        var heights = RowHeightsFor(shown, available - allHeight, total);
        string windowText = Ranking switch { RankWindow.Visible => UiText.Tr("Visible"), RankWindow.Minutes15 => "15 min", RankWindow.Hour1 => "1 h", _ => "24 h" };

        var allSpot = Enumerable.Range(0, sources.Count).Where(i => sources[i].IsSpot && Includes(true)).ToList();
        var allPerpetual = Enumerable.Range(0, sources.Count).Where(i => !sources[i].IsSpot && Includes(false)).ToList();
        decimal ns = ranked.Sum(x => x.NetSpot), np = ranked.Sum(x => x.NetPerpetual);
        string note = "";
        if (DivergenceNotional > 0 && Math.Abs(ns) >= DivergenceNotional && Math.Abs(np) >= DivergenceNotional && Math.Sign(ns) != Math.Sign(np))
            note = UiText.Tr(ns > 0 ? "Spot buying while perpetuals sell: they disagree" : "Spot selling while perpetuals buy: they disagree");
        else if (unattributed > 0)
            note = UiText.Tr($"Unattributed notional (unknown market): {FlowFormat.Compact(unattributed)}");
        DrawRow(context, r, y, allHeight, first, last, allSpot, allPerpetual, UiText.Tr("ALL MARKETS"), ns + np, 1, windowText, note, true, before);
        y += allHeight;
        for (int k = 0; k < shown.Count; k++)
        {
            if (heights[k] < 10) break;
            var row = shown[k];
            DrawRow(context, r, y, heights[k], first, last, row.Spot, row.Perpetual, $"#{k + 1} {row.Name.ToUpperInvariant()}", row.Net, total > 0 ? row.Gross / total : 0, windowText, "", false, before);
            y += heights[k];
        }
        if (hidden > 0) context.DrawString(UiText.Tr($"+{hidden} more exchanges (raise \"Exchange rows\")"), _font, FlowFormat.Dim, r.Left + 6, r.Bottom - 12);
    }

    private List<int> RowHeightsFor(List<ExchangeRow> rows, int available, decimal total)
    {
        var result = new List<int>();
        if (rows.Count == 0 || available <= 0) return result;
        var weights = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            weights[i] = Heights switch
            {
                RowHeights.Equal => 1,
                RowHeights.Share => total > 0 ? Math.Max(0.04, (double)(rows[i].Gross / total)) : 1,
                _ => Math.Pow(0.618, i),
            };
        double sum = weights.Sum();
        const int minimum = 16;
        foreach (var w in weights) result.Add(Math.Max(minimum, (int)(available * w / sum)));
        int over = result.Sum() - available;
        for (int i = result.Count - 1; i >= 0 && over > 0; i--)
        {
            int cut = Math.Min(over, Math.Max(0, result[i] - minimum));
            result[i] -= cut;
            over -= cut;
        }
        return result;
    }

    /// <summary>
    /// Net notional per market of bars [0, <paramref name="first"/>): closed bars are added (or, scrolling left, removed)
    /// once and remembered, so accumulating from the first bar never re-walks the history on each frame.
    /// </summary>
    private decimal[] NetBefore(int first)
    {
        int markets = _cache.Sources.Count;
        if (_before.Length != markets)
        {
            _before = new decimal[markets];
            _beforeBars = 0;
        }
        while (_beforeBars > first)
        {
            var f = _cache.Compute(--_beforeBars, GetBarTrades);
            for (int i = 0; i < markets; i++) _before[i] -= f.Net(i);
        }
        while (_beforeBars < first && GetCandle(_beforeBars).IsCandleFinished)
        {
            var f = _cache.Compute(_beforeBars++, GetBarTrades);
            for (int i = 0; i < markets; i++) _before[i] += f.Net(i);
        }
        var sums = (decimal[])_before.Clone();
        for (int b = _beforeBars; b < first; b++)
        {
            var f = Flow(b);
            for (int i = 0; i < markets; i++) sums[i] += f.Net(i);
        }
        return sums;
    }

    private void DrawRow(RenderContext ctx, Rectangle r, int top, int h, int first, int last, List<int> spot, List<int> perpetual,
        string name, decimal net, decimal share, string windowText, string note, bool aggregate, decimal[]? before)
    {
        var info = ChartInfo!;
        ctx.DrawLine(new RenderPen(Color.FromArgb(70, 140, 150, 170)), r.Left, top, r.Right, top);
        int n = last - first + 1;
        var lanes = new List<(Color Color, decimal?[] Values)>();
        foreach (var (indexes, color) in new[] { (spot, FlowFormat.Spot), (perpetual, FlowFormat.Perpetual) })
        {
            if (indexes.Count == 0) continue;
            var values = new decimal?[n];
            decimal acc = 0;
            if (before != null)
                foreach (int i in indexes) acc += before[i];
            bool any = false;
            for (int b = first; b <= last; b++)
            {
                var f = Flow(b);
                if (!f.HasFlow) continue; // gap: no measured prints for this bar
                foreach (int i in indexes)
                {
                    acc += f.Net(i);
                    any |= f.Gross(i) > 0;
                }
                values[b - first] = acc;
            }
            if (any) lanes.Add((color, values));
        }
        decimal lo = 0, hi = 0;
        foreach (var (_, values) in lanes)
            foreach (var v in values)
                if (v is { } x) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
        if (hi - lo == 0) hi = lo + 1;
        int y0 = top + 3, y1 = top + h - 3;
        int Y(decimal v) => (int)Math.Round(y1 - (double)((v - lo) / (hi - lo)) * (y1 - y0));
        int zero = Y(0);
        ctx.DrawLine(new RenderPen(Color.FromArgb(60, 160, 160, 170), 1, System.Drawing.Drawing2D.DashStyle.Dash), r.Left, zero, r.Right, zero);
        int half = Math.Max(0, info.PriceChartContainer.BarsWidth / 2);
        var segment = new List<Point>();
        foreach (var (color, values) in lanes)
        {
            var pen = new RenderPen(color, aggregate ? 1.6f : 1.2f);
            segment.Clear();
            for (int k = 0; k <= n; k++)
            {
                if (k < n && values[k] is { } v) { segment.Add(new Point(info.GetXByBar(first + k) + half, Y(v))); continue; }
                if (segment.Count >= 2) ctx.DrawLines(pen, segment.ToArray());
                segment.Clear();
            }
        }

        int width = Math.Clamp(LabelWidth, 80, 400);
        ctx.FillRectangle(FlowFormat.Back, new Rectangle(r.Left, top + 1, width, Math.Min(h - 2, aggregate ? 40 : 28)));
        ctx.DrawString(name, _bold, Color.FromArgb(235, 235, 240), r.Left + 5, top + 2);
        if (h >= 22)
        {
            string line = $"{FlowFormat.Signed(net)} · {windowText}" + (aggregate ? "" : $" · {share * 100:0}%");
            ctx.DrawString(line, _font, net >= 0 ? FlowFormat.Buy : FlowFormat.Sell, r.Left + 5, top + 13);
        }
        if (h >= 32)
        {
            int lx = r.Left + 5;
            if (spot.Count > 0)
            {
                ctx.FillEllipse(FlowFormat.Spot, new Rectangle(lx, top + 27, 6, 6));
                ctx.DrawString("Spot", _font, FlowFormat.Dim, lx + 8, top + 24);
                lx += 40;
            }
            if (perpetual.Count > 0)
            {
                ctx.FillEllipse(FlowFormat.Perpetual, new Rectangle(lx, top + 27, 6, 6));
                ctx.DrawString("Perpetual", _font, FlowFormat.Dim, lx + 8, top + 24);
            }
        }
        if (note.Length > 0) ctx.DrawString(note, _bold, FlowFormat.Amber, r.Left + width + 8, top + 3);
    }

    /// <summary>Buy and sell notional over the last 1/5/15/60 minutes and per size band (last hour) for the lamps.</summary>
    internal (decimal[] Buy, decimal[] Sell, decimal[] SizeBuy, decimal[] SizeSell) LampTotals(DateTime now)
    {
        int[] minutes = { 1, 5, 15, 60 };
        decimal[] buy = new decimal[4], sell = new decimal[4], sizeBuy = new decimal[4], sizeSell = new decimal[4];
        var since = now - TimeSpan.FromMinutes(60);
        var trades = new List<BarTrade>();
        var sources = _cache.Sources;
        for (int b = CurrentBar - 1; b >= 0; b--)
        {
            var end = b + 1 < CurrentBar ? GetCandle(b + 1).Time : now;
            if (end <= since) break;
            GetBarTrades(b, trades);
            foreach (var t in trades)
            {
                if (t.Time < since || t.Time > now || t.Side is not (TradeSide.Buy or TradeSide.Sell) || !t.Provenance.IsMeasured || t.Provenance.IsSideInferred) continue;
                int index = -1;
                for (int i = 0; i < sources.Count; i++) if (sources[i].Identity == t.Provenance.SourceIdentity) { index = i; break; }
                if (index >= 0 && !Includes(sources[index].IsSpot)) continue;
                decimal notional = t.Price * t.Volume * FlowFormat.PointValue(this);
                double age = (now - t.Time).TotalMinutes;
                for (int k = 0; k < 4; k++)
                    if (age <= minutes[k]) { if (t.Side == TradeSide.Buy) buy[k] += notional; else sell[k] += notional; }
                if (!t.Provenance.IsObservedTrade) continue;
                int band = notional < 25_000 ? 0 : notional < 100_000 ? 1 : notional < 500_000 ? 2 : 3;
                if (t.Side == TradeSide.Buy) sizeBuy[band] += notional; else sizeSell[band] += notional;
            }
        }
        return (buy, sell, sizeBuy, sizeSell);
    }

    private int DrawLamps(RenderContext ctx, Rectangle r, int y)
    {
        var lastEnd = GetCandle(CurrentBar - 1).Time + (ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero);
        var now = lastEnd < DateTime.UtcNow ? lastEnd : DateTime.UtcNow;
        var (buy, sell, sizeBuy, sizeSell) = LampTotals(now);
        int x = r.Left + 6;
        ctx.DrawString(UiText.Tr("Flow"), _bold, FlowFormat.Dim, x, y);
        int gx = x + 52;
        foreach (var (k, label) in new[] { (0, "1m"), (1, "5m"), (2, "15m"), (3, "60m") }) gx = LampGroup(ctx, gx, y, label, buy[k], sell[k]) + 10;
        y += 13;
        ctx.DrawString(UiText.Tr("Size"), _bold, FlowFormat.Dim, x, y);
        gx = x + 52;
        string[] bands = { "<25K", "25-100K", "100-500K", ">500K" };
        decimal all = sizeBuy.Sum() + sizeSell.Sum();
        for (int k = 0; k < 4; k++)
            gx = LampGroup(ctx, gx, y, $"{bands[k]} {(all > 0 ? (sizeBuy[k] + sizeSell[k]) / all * 100 : 0):0}%", sizeBuy[k], sizeSell[k]) + 10;
        return y + 13;
    }

    private int LampGroup(RenderContext ctx, int x, int y, string label, decimal buy, decimal sell)
    {
        ctx.DrawString(label, _font, FlowFormat.Dim, x, y);
        x += ctx.MeasureString(label, _font).Width + 4;
        decimal total = buy + sell;
        int lit = total > 0 ? (int)Math.Round(buy / total * 10) : 0;
        bool buyAhead = buy >= sell;
        for (int i = 0; i < 10; i++)
        {
            bool isBuy = i < lit;
            var color = total <= 0 ? Color.FromArgb(90, 120, 120, 130) : isBuy ? FlowFormat.Buy : FlowFormat.Sell;
            if (total > 0 && isBuy != buyAhead) color = FlowFormat.Alpha(color, 90);
            ctx.FillEllipse(color, new Rectangle(x + i * 7, y + 3, 5, 5));
        }
        x += 70;
        string pct = total > 0 ? $"{buy / total * 100:0}%" : "–";
        ctx.DrawString(pct, _font, total <= 0 ? FlowFormat.Dim : buyAhead ? FlowFormat.Buy : FlowFormat.Sell, x + 2, y);
        return x + ctx.MeasureString(pct, _font).Width + 2;
    }
}

/// <summary>
/// A statistics strip under the chart with one column per bar: volume, measured delta, CVD, buy/sell volume, footprint
/// maxima, POC, diagonal imbalances and stacks, trade counts, average trade, retail and whale delta/CVD by trade-size band,
/// range, relative volume, VWAP distance, observed liquidations, open-interest change, funding and resting book net.
/// Cells are shaded by magnitude (log scale between the 2nd and 99th visible percentiles). A cell without data is left
/// empty: unmeasured delta, bars without individual prints, unobserved derivatives and unrecorded depth are gaps, not zero.
/// </summary>
[DisplayName("Bar statistics strip")]
[Category(IndicatorCategories.OrderFlow)]
[Description("A table under the chart with one column per bar: volume, delta, CVD, buys/sells, footprint imbalances, trade counts, retail and whale flow by trade size, range, relative volume, VWAP distance and optional derivatives and book rows. Missing data stays an empty cell.")]
public sealed class BarStatsStrip : Indicator
{
    public enum CellStyle
    {
        [Display(Name = "Cells shaded by magnitude")] Filled,
        [Display(Name = "Colored text only")] Text,
    }

    public enum StatUnit
    {
        [Display(Name = "Base units (contracts / coins)")] Base,
        [Display(Name = "Notional (quote currency)")] Notional,
    }

    public enum CvdAnchor
    {
        [Display(Name = "All visible bars")] Visible,
        [Display(Name = "Each session")] Session,
    }

    /// <summary>One strip row: a short name, a per-bar value (null = no data), formatting and whether its sign matters.</summary>
    internal sealed record StatRow(string Name, Func<int, decimal?> Value, Func<decimal, string> Format, bool Signed);

    private readonly MarketFlowCache _cache = new();
    private readonly DerivativesContext _derivatives;
    private readonly RenderFont _font = new("Arial", 7), _label = new("Arial", 7.5f, FontStyle.Bold);

    public BarStatsStrip() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = new ValueDataSeries("STATS", "Statistics") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _derivatives = new DerivativesContext(a => DoActionInGuiThread(a), RedrawChart, openInterest: true, funding: true, liquidations: true);
    }

    [Display(Name = "Volume", GroupName = "Rows: volume", Order = 1)] public bool Volume { get; set; } = true;
    [Display(Name = "Delta", GroupName = "Rows: volume", Order = 2)] public bool Delta { get; set; } = true;
    [Display(Name = "CVD", GroupName = "Rows: volume", Order = 3)] public bool Cvd { get; set; } = true;
    [Display(Name = "Delta %", GroupName = "Rows: volume", Order = 4)] public bool DeltaPercent { get; set; }
    [Display(Name = "Buy volume", GroupName = "Rows: volume", Order = 5)] public bool BuyVolume { get; set; }
    [Display(Name = "Sell volume", GroupName = "Rows: volume", Order = 6)] public bool SellVolume { get; set; }
    [Display(Name = "Buy %", GroupName = "Rows: volume", Order = 7)] public bool BuyPercent { get; set; }
    [Display(Name = "Volume change %", GroupName = "Rows: volume", Order = 8)] public bool VolumeChange { get; set; }
    [Display(Name = "Relative volume", GroupName = "Rows: volume", Order = 9)] public bool RelativeVolume { get; set; }
    [Display(Name = "Most bought at one price", GroupName = "Rows: footprint", Order = 10)] public bool MaxBuy { get; set; }
    [Display(Name = "Most sold at one price", GroupName = "Rows: footprint", Order = 11)] public bool MaxSell { get; set; }
    [Display(Name = "POC", GroupName = "Rows: footprint", Order = 12)] public bool Poc { get; set; }
    [Display(Name = "Imbalances (count)", GroupName = "Rows: footprint", Order = 13)] public bool Imbalances { get; set; }
    [Display(Name = "Stacked imbalances", GroupName = "Rows: footprint", Order = 14)] public bool Stacked { get; set; }
    [Display(Name = "Trades", GroupName = "Rows: trades", Order = 20)] public bool Trades { get; set; }
    [Display(Name = "Buy trades", GroupName = "Rows: trades", Order = 21)] public bool Buys { get; set; }
    [Display(Name = "Sell trades", GroupName = "Rows: trades", Order = 22)] public bool Sells { get; set; }
    [Display(Name = "Average trade", GroupName = "Rows: trades", Order = 23)] public bool AverageTrade { get; set; }
    [Display(Name = "Retail delta", GroupName = "Rows: trades", Order = 24)] public bool DeltaRetail { get; set; }
    [Display(Name = "Whale delta", GroupName = "Rows: trades", Order = 25)] public bool DeltaWhales { get; set; }
    [Display(Name = "Retail CVD", GroupName = "Rows: trades", Order = 26)] public bool CvdRetail { get; set; }
    [Display(Name = "Whale CVD", GroupName = "Rows: trades", Order = 27)] public bool CvdWhales { get; set; }
    [Display(Name = "Range (high − low)", GroupName = "Rows: market", Order = 30)] public bool Range { get; set; }
    [Display(Name = "Range %", GroupName = "Rows: market", Order = 31)] public bool RangePercent { get; set; }
    [Display(Name = "Distance to session VWAP %", GroupName = "Rows: market", Order = 32)] public bool VwapDistance { get; set; }
    [Display(Name = "Resting book net (0.1 % – 2.5 %)", GroupName = "Rows: market", Order = 33)] public bool BookNet { get; set; }
    [Display(Name = "Liquidations", GroupName = "Rows: derivatives", Order = 34)] public bool Liquidations { get; set; }
    [Display(Name = "Liquidations shorts − longs", GroupName = "Rows: derivatives", Order = 35)] public bool LiquidationSplit { get; set; }
    [Display(Name = "Open interest change", GroupName = "Rows: derivatives", Order = 36)] public bool OpenInterestChange { get; set; }
    [Display(Name = "Funding", GroupName = "Rows: derivatives", Order = 37)] public bool Funding { get; set; }

    [Display(Name = "Unit (volume, delta, CVD)", GroupName = "Options", Order = 39)] public StatUnit Units { get; set; } = StatUnit.Base;
    [Display(Name = "CVD from", GroupName = "Options", Order = 40)] public CvdAnchor CvdFrom { get; set; } = CvdAnchor.Visible;
    [Display(Name = "Relative volume: bars", GroupName = "Options", Order = 41)] [Range(2, 500)] public int RelativeVolumeLength { get; set; } = 20;
    [Display(Name = "Cell style", GroupName = "Options", Order = 42)] public CellStyle Style { get; set; } = CellStyle.Filled;
    [Display(Name = "Imbalance: times the opposite side", GroupName = "Options", Order = 43)] [Range(1, 100)] public decimal ImbalanceRatio { get; set; } = 3;
    [Display(Name = "Imbalance: minimum volume", GroupName = "Options", Order = 44)] [Range(0, 1_000_000_000)] public decimal ImbalanceMinimum { get; set; }
    [Display(Name = "Stack: consecutive rows", GroupName = "Options", Order = 45)] [Range(2, 10)] public int StackRows { get; set; } = 3;
    [Display(Name = "Retail: trades up to (notional)", GroupName = "Options", Order = 46)] [Range(0, 1_000_000_000_000)] public decimal RetailMaximum { get; set; } = 100_000;
    [Display(Name = "Whales: trades from (notional)", GroupName = "Options", Order = 47)] [Range(0, 1_000_000_000_000)] public decimal WhaleMinimum { get; set; } = 500_000;

    internal DerivativesContext Derivatives => _derivatives;

    protected override void OnRecalculate() => _cache.Reset(InstrumentInfo?.Sources ?? Array.Empty<InstrumentSource>(), FlowFormat.PointValue(this));

    protected override void OnDispose() => _derivatives.Dispose();

    protected override void OnDepthHistoryChanged() => RedrawChart();

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    /// <summary>Diagonal imbalances (buys against sells one row lower; sells against buys one row higher) and stacks.</summary>
    public static (int Imbalances, int Stacks) CountImbalances(IReadOnlyList<PriceVolumeInfo> levels, decimal ratio, decimal minimum, int stackRows)
    {
        var rows = levels.OrderBy(l => l.Price).ToList();
        int count = 0, stacks = 0, runBuy = 0, runSell = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            bool buy = i > 0 && rows[i].Ask > 0 && rows[i].Ask >= minimum && rows[i].Ask >= ratio * rows[i - 1].Bid && rows[i - 1].Bid > 0;
            bool sell = i + 1 < rows.Count && rows[i].Bid > 0 && rows[i].Bid >= minimum && rows[i].Bid >= ratio * rows[i + 1].Ask && rows[i + 1].Ask > 0;
            if (buy) count++;
            if (sell) count++;
            runBuy = buy ? runBuy + 1 : 0;
            runSell = sell ? runSell + 1 : 0;
            if (runBuy == stackRows) stacks++;
            if (runSell == stackRows) stacks++;
        }
        return (count, stacks);
    }

    private decimal? MeasuredDelta(int bar) => StudyMath.TryDelta(GetCandle(bar), false, out var delta, out _) ? delta : null;

    private decimal Unit(int bar) => Units == StatUnit.Notional ? GetCandle(bar).Close * FlowFormat.PointValue(this) : 1;

    private bool HasFootprint(int bar) => GetCandle(bar).HasObservedFootprint || GetCandle(bar).HasAggregatedFootprint;

    /// <summary>The enabled rows for bars <paramref name="first"/>..<paramref name="last"/>, in strip order.</summary>
    internal List<StatRow> BuildRows(int first, int last)
    {
        var rows = new List<StatRow>();
        var cvd = new Dictionary<int, (decimal? All, decimal? Retail, decimal? Whales)>();
        decimal? all = 0, retail = 0, whales = 0;
        for (int b = first; b <= last; b++)
        {
            if (CvdFrom == CvdAnchor.Session && b > first && IsNewSession(b)) { all = 0; retail = 0; whales = 0; }
            // An unmeasured delta breaks the CVD until the next reset: a gap, never an assumed zero.
            all = all is { } a && MeasuredDelta(b) is { } d ? a + d * Unit(b) : null;
            // Likewise a bar without individual prints breaks the retail and whale CVDs.
            var f = Flow(b);
            if (f.HasIndividualPrints && retail is { } rs && whales is { } ws)
            {
                var (rd, wd) = f.SizeDelta(RetailMaximum, WhaleMinimum);
                retail = rs + rd;
                whales = ws + wd;
            }
            else retail = whales = null;
            cvd[b] = (all, retail, whales);
        }
        static string Percent(decimal v) => v.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        static string Plain(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static string Count(decimal v) => v.ToString("0", CultureInfo.InvariantCulture);
        if (Volume) rows.Add(new("vol", b => GetCandle(b).Volume * Unit(b), FlowFormat.Compact, false));
        if (Delta) rows.Add(new("delta", b => MeasuredDelta(b) * Unit(b), FlowFormat.Compact, true));
        if (Cvd) rows.Add(new("cvd", b => cvd[b].All, FlowFormat.Compact, true));
        if (DeltaPercent) rows.Add(new("delta %", b => GetCandle(b).Volume > 0 && MeasuredDelta(b) is { } d ? d / GetCandle(b).Volume * 100 : null, Percent, true));
        if (BuyVolume) rows.Add(new("buy vol", b => HasFootprint(b) ? GetCandle(b).Ask * Unit(b) : null, FlowFormat.Compact, false));
        if (SellVolume) rows.Add(new("sell vol", b => HasFootprint(b) ? GetCandle(b).Bid * Unit(b) : null, FlowFormat.Compact, false));
        if (BuyPercent) rows.Add(new("buy %", b => HasFootprint(b) && GetCandle(b).Volume > 0 ? GetCandle(b).Ask / GetCandle(b).Volume * 100 : null, Percent, false));
        if (VolumeChange) rows.Add(new("vol chg", b => b > 0 && GetCandle(b - 1).Volume > 0 ? (GetCandle(b).Volume - GetCandle(b - 1).Volume) / GetCandle(b - 1).Volume * 100 : null, Percent, true));
        if (RelativeVolume)
            rows.Add(new("rvol", b =>
            {
                decimal sum = 0;
                int n = 0;
                for (int k = Math.Max(0, b - RelativeVolumeLength); k < b; k++, n++) sum += GetCandle(k).Volume;
                return n > 0 && sum > 0 ? GetCandle(b).Volume / (sum / n) : null;
            }, v => v.ToString("0.0", CultureInfo.InvariantCulture) + "x", false));
        if (MaxBuy) rows.Add(new("max buy", b => HasFootprint(b) ? GetCandle(b).GetAllPriceLevels().Select(l => (decimal?)l.Ask).DefaultIfEmpty(null).Max() : null, FlowFormat.Compact, false));
        if (MaxSell) rows.Add(new("max sell", b => HasFootprint(b) ? GetCandle(b).GetAllPriceLevels().Select(l => (decimal?)l.Bid).DefaultIfEmpty(null).Max() : null, FlowFormat.Compact, false));
        if (Poc) rows.Add(new("poc", b => GetCandle(b).GetAllPriceLevels().OrderByDescending(l => l.Volume).ThenBy(l => l.Price).Select(l => (decimal?)l.Price).FirstOrDefault(), Plain, false));
        if (Imbalances) rows.Add(new("imb #", b => GetCandle(b).HasObservedFootprint ? CountImbalances(GetCandle(b).GetAllPriceLevelsList(), ImbalanceRatio, ImbalanceMinimum, StackRows).Imbalances : null, Count, false));
        if (Stacked) rows.Add(new("stacked", b => GetCandle(b).HasObservedFootprint ? CountImbalances(GetCandle(b).GetAllPriceLevelsList(), ImbalanceRatio, ImbalanceMinimum, StackRows).Stacks : null, Count, false));
        if (Trades) rows.Add(new("trades", b => Flow(b) is { HasIndividualPrints: true } f ? f.Trades : null, FlowFormat.Compact, false));
        if (Buys) rows.Add(new("buys", b => Flow(b) is { HasIndividualPrints: true } f ? f.Buys : null, FlowFormat.Compact, false));
        if (Sells) rows.Add(new("sells", b => Flow(b) is { HasIndividualPrints: true } f ? f.Sells : null, FlowFormat.Compact, false));
        if (AverageTrade) rows.Add(new("avg trade", b => Flow(b) is { HasIndividualPrints: true } f ? (f.BuyVolume + f.SellVolume) / f.Trades : null, FlowFormat.Compact, false));
        if (DeltaRetail) rows.Add(new("Δ retail", b => Flow(b) is { HasIndividualPrints: true } f ? f.SizeDelta(RetailMaximum, WhaleMinimum).Retail : null, FlowFormat.Compact, true));
        if (DeltaWhales) rows.Add(new("Δ whales", b => Flow(b) is { HasIndividualPrints: true } f ? f.SizeDelta(RetailMaximum, WhaleMinimum).Whales : null, FlowFormat.Compact, true));
        if (CvdRetail) rows.Add(new("cvd retail", b => cvd[b].Retail, FlowFormat.Compact, true));
        if (CvdWhales) rows.Add(new("cvd whales", b => cvd[b].Whales, FlowFormat.Compact, true));
        if (Range) rows.Add(new("range", b => GetCandle(b).High - GetCandle(b).Low, Plain, false));
        if (RangePercent) rows.Add(new("range %", b => GetCandle(b).Low > 0 ? (GetCandle(b).High - GetCandle(b).Low) / GetCandle(b).Low * 100 : null, Percent, false));
        if (VwapDistance)
        {
            var vwap = new Dictionary<int, decimal>();
            decimal pv = 0, vv = 0;
            int start = first;
            while (start > 0 && !IsNewSession(start)) start--;
            for (int b = start; b <= last; b++)
            {
                if (b > start && IsNewSession(b)) pv = vv = 0;
                var c = GetCandle(b);
                pv += (c.High + c.Low + c.Close) / 3 * c.Volume;
                vv += c.Volume;
                if (vv > 0) vwap[b] = pv / vv;
            }
            rows.Add(new("vwap dist", b => vwap.TryGetValue(b, out var w) && w > 0 ? (GetCandle(b).Close - w) / w * 100 : null, Percent, true));
        }
        if (BookNet)
            rows.Add(new("book net", b =>
            {
                var samples = GetDepthSamples(b);
                for (int i = samples.Count - 1; i >= 0; i--)
                {
                    if (!samples[i].IsUsable) continue;
                    var near = DepthBands.WithinPercent(samples[i].Snapshot, 0.1m);
                    var far = DepthBands.WithinPercent(samples[i].Snapshot, 2.5m);
                    return far.Available ? (far.Bid - near.Bid) - (far.Ask - near.Ask) : null;
                }
                return null;
            }, FlowFormat.Compact, true));
        bool derivatives = Liquidations || LiquidationSplit || OpenInterestChange || Funding;
        if (derivatives && last >= first)
            _derivatives.Ensure(DerivativesPane.DerivedSymbol(InstrumentInfo?.Instrument ?? "", InstrumentInfo?.BaseAsset ?? "", InstrumentInfo?.QuoteAsset ?? ""), GetCandle(first).Time.AddDays(-1));
        DateTime T0(int b) => GetCandle(b).Time;
        DateTime T1(int b) => b + 1 < CurrentBar ? GetCandle(b + 1).Time : DateTime.UtcNow;
        decimal LiquidationUnit(int b) => Units == StatUnit.Notional ? GetCandle(b).Close : 1;
        if (Liquidations) rows.Add(new("liq", b => _derivatives.Liquidations(T0(b), T1(b)) is { } q ? (q.Long + q.Short) * LiquidationUnit(b) : null, FlowFormat.Compact, false));
        if (LiquidationSplit) rows.Add(new("liq S−L", b => _derivatives.Liquidations(T0(b), T1(b)) is { } q ? (q.Short - q.Long) * LiquidationUnit(b) : null, FlowFormat.Compact, true));
        if (OpenInterestChange) rows.Add(new("oi chg", b => _derivatives.OpenInterestAt(T1(b) - TimeSpan.FromTicks(1)) - _derivatives.OpenInterestAt(T0(b) - TimeSpan.FromTicks(1)), FlowFormat.Compact, true));
        if (Funding) rows.Add(new("funding", b => _derivatives.FundingAt(T0(b)), v => v.ToString("0.0000", CultureInfo.InvariantCulture) + "%", true));
        return rows;
    }

    private MarketFlowBar Flow(int bar) => _cache.Get(this, bar, GetBarTrades);

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container == null || CurrentBar <= 0) return;
        var r = Container.Region;
        var pc = ChartInfo.PriceChartContainer;
        int first = Math.Max(0, pc.FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, pc.LastVisibleBarNumber);
        if (last < first) return;
        _cache.Retain(first, last);
        var rows = BuildRows(first, last);
        if (rows.Count == 0) return;
        int rh = Math.Max(10, Math.Min(18, (r.Height - 17) / rows.Count));
        int bw = Math.Max(1, pc.BarsWidth);
        bool text = bw >= 26;
        var values = new decimal?[last - first + 1];
        var sorted = new List<double>();
        for (int k = 0; k < rows.Count; k++)
        {
            var row = rows[k];
            int y = r.Top + 15 + k * rh;
            if (y + rh > r.Bottom) break;
            sorted.Clear();
            for (int b = first; b <= last; b++)
            {
                values[b - first] = row.Value(b);
                if (values[b - first] is { } v) sorted.Add(Math.Abs((double)v));
            }
            sorted.Sort();
            double p2 = sorted.Count > 0 ? sorted[(int)(sorted.Count * 0.02)] : 0, p99 = sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))] : 1;
            double lp = Math.Log10(p2 + 1), hp = Math.Log10(p99 + 1);
            for (int b = first; b <= last; b++)
            {
                if (values[b - first] is not { } v) continue;
                int x = ChartInfo.GetXByBar(b);
                double t = hp - lp > 1e-9 ? Math.Clamp((Math.Log10(Math.Abs((double)v) + 1) - lp) / (hp - lp), 0, 1) : 0.5;
                var basec = row.Signed ? v >= 0 ? FlowFormat.Buy : FlowFormat.Sell : Color.FromArgb(255, 110, 150, 220);
                if (Style == CellStyle.Filled)
                {
                    context.FillRectangle(FlowFormat.Alpha(basec, (int)(25 + 160 * t)), new Rectangle(x, y, Math.Max(1, bw - 1), rh - 1));
                    if (text) context.DrawString(row.Format(v), _font, Color.FromArgb(235, 240, 240, 245), x + 2, y + 1);
                }
                else if (text) context.DrawString(row.Format(v), _font, FlowFormat.Alpha(basec, (int)(120 + 135 * t)), x + 2, y + 1);
            }
            var size = context.MeasureString(row.Name, _label);
            context.FillRectangle(FlowFormat.Back, new Rectangle(r.Left, y, size.Width + 8, rh - 1));
            context.DrawString(row.Name, _label, FlowFormat.Dim, r.Left + 4, y + 1);
        }
        if (_derivatives.Message is { Length: > 0 } message && (Liquidations || LiquidationSplit || OpenInterestChange || Funding))
            context.DrawString(message, _font, FlowFormat.Dim, r.Left + 8, r.Bottom - 12);
    }
}

/// <summary>
/// New longs / new shorts (NL/NS) estimate from Binance + Bybit open-interest change and each bar's aggressor side. By
/// delta: rising OI with net buying = new longs, with net selling = new shorts; falling OI with net selling = longs
/// closing, with net buying = shorts covering. By price: the classic price/OI quadrants averaged over N bars, with a box
/// of how the last N bars split. Solid bars opened, faint bars closed; the line is the running sum. An estimate, not a
/// signal: bars without both OI cutoffs or a measured delta are gaps.
/// </summary>
[DisplayName("NL/NS: new longs and shorts (open interest + delta, estimate)")]
[Category(IndicatorCategories.Derivatives)]
[Description("Estimates new longs, new shorts and closes from the open-interest change (Binance + Bybit perpetuals) and each bar's measured delta or price direction. An estimate: bars without open interest or measured delta stay gaps.")]
public sealed class PositioningNlNs : Indicator
{
    public enum ClassifyBy
    {
        [Display(Name = "Aggressor delta (who traded at market)")] Delta,
        [Display(Name = "Price direction (price / OI quadrants)")] Price,
    }

    /// <summary>Price/OI quadrant of a bar.</summary>
    public enum Regime { None, NewLongs, NewShorts, ShortsCovering, LongsClosing }

    private static readonly Color Long = Color.FromArgb(230, 38, 194, 129), Short = Color.FromArgb(230, 240, 82, 90);
    private readonly OptionalValueDataSeries _bars = new("NLNS", "New longs / shorts") { VisualType = VisualMode.Histogram, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _line = new("NET", "Net positioning") { Color = Color.FromArgb(255, 230, 230, 235), Width = 1, ShowZeroValue = true };
    private readonly DerivativesContext _derivatives;
    private readonly Dictionary<int, Regime> _regimes = new();
    private readonly RenderFont _font = new("Arial", 8);
    private ClassifyBy _by = ClassifyBy.Delta;
    private int _smoothing = 1;
    private bool _resetDaily, _showLine = true;
    private string _symbolOverride = "";

    public PositioningNlNs() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _bars;
        DataSeries.Add(_line);
        LineSeries.Add(new LineSeries("Zero", "Zero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _derivatives = new DerivativesContext(a => DoActionInGuiThread(a), RecalculateValues, openInterest: true, funding: false, liquidations: false);
    }

    [Display(Name = "Classify by", GroupName = "Calculation", Order = 15)]
    public ClassifyBy Classify { get => _by; set { _by = value; RecalculateValues(); } }

    [Display(Name = "Smoothing (bars; price mode)", GroupName = "Calculation", Order = 16)]
    [Range(1, 50)]
    public int Smoothing { get => _smoothing; set { _smoothing = Math.Clamp(value, 1, 50); RecalculateValues(); } }

    [Display(Name = "Reset the sum each session", GroupName = "Calculation", Order = 20)]
    public bool ResetDaily { get => _resetDaily; set { _resetDaily = value; RecalculateValues(); } }

    [Display(Name = "Perpetual symbol (blank = derived from the chart)", GroupName = "Calculation", Order = 30)]
    public string SymbolOverride { get => _symbolOverride; set { _symbolOverride = value ?? ""; RecalculateValues(); } }

    [Display(Name = "Show the net positioning line", GroupName = "Visualization", Order = 10)]
    public bool ShowLine { get => _showLine; set { _showLine = value; RecalculateValues(); } }

    [Display(Name = "Bars in the regime box", GroupName = "Visualization", Order = 11)]
    [Range(2, 500)]
    public int RegimeWindow { get; set; } = 24;

    [Browsable(false)]
    public DerivativesData? Service { get => _derivatives.Service; set => _derivatives.Service = value; }

    internal DerivativesContext Derivatives => _derivatives;

    /// <summary>Delta classification: signed OI change (positive = long side) and whether positions opened. Null when undecidable.</summary>
    public static (decimal Signed, bool Opened)? ClassifyByDelta(decimal openInterestChange, decimal delta) =>
        openInterestChange == 0 || delta == 0 ? null : (Math.Sign(delta) * Math.Abs(openInterestChange), openInterestChange > 0);

    /// <summary>Price/OI quadrant: OI up with price up = new longs; OI up, price down = new shorts; OI down, price up = shorts covering; OI down, price down = longs closing.</summary>
    public static Regime ClassifyByPrice(decimal openInterestChange, decimal priceChange) =>
        openInterestChange >= 0 ? priceChange >= 0 ? Regime.NewLongs : Regime.NewShorts : priceChange >= 0 ? Regime.ShortsCovering : Regime.LongsClosing;

    private string Symbol => _symbolOverride.Trim().Length > 0 ? _symbolOverride.Trim().ToUpperInvariant()
        : DerivativesPane.DerivedSymbol(InstrumentInfo?.Instrument ?? "", InstrumentInfo?.BaseAsset ?? "", InstrumentInfo?.QuoteAsset ?? "");

    private DateTime End(int bar) => bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : GetCandle(bar).Time + (ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromHours(1));

    private decimal? OiBefore(DateTime time) => _derivatives.OpenInterestAt(time - TimeSpan.FromTicks(1));

    protected override void OnRecalculate()
    {
        _regimes.Clear();
        _line.VisualType = _showLine ? VisualMode.Line : VisualMode.Hide;
    }

    protected override void OnDispose() => _derivatives.Dispose();

    protected override void OnCalculate(int bar, decimal value)
    {
        // Loads, then refreshes open interest as live time advances (a no-op while fresh).
        if (bar == CurrentBar - 1) _derivatives.Ensure(Symbol, GetCandle(0).Time);
        var c = GetCandle(bar);
        decimal running = bar == 0 || _resetDaily && IsNewSession(bar) ? 0 : _line[bar - 1];
        var end = End(bar);
        decimal? now = OiBefore(end < DateTime.UtcNow ? end : DateTime.UtcNow), before = OiBefore(c.Time);
        decimal? signed = null;
        Color color = Color.Empty;
        if (now is { } a && before is { } b)
        {
            decimal change = a - b;
            if (_by == ClassifyBy.Price)
            {
                int from = Math.Max(0, bar - _smoothing + 1);
                decimal? start = OiBefore(GetCandle(from).Time);
                decimal price = c.Close - (from > 0 ? GetCandle(from - 1).Close : GetCandle(from).Open);
                if (start is { } s)
                {
                    var regime = ClassifyByPrice((a - s) / (bar - from + 1), price);
                    _regimes[bar] = regime;
                    signed = change;
                    color = regime switch { Regime.NewLongs => Long, Regime.NewShorts => Short, Regime.ShortsCovering => FlowFormat.Alpha(Long, 110), _ => FlowFormat.Alpha(Short, 110) };
                }
            }
            else if (StudyMath.TryDelta(c, false, out var delta, out _))
            {
                if (ClassifyByDelta(change, delta) is { } k)
                {
                    signed = k.Signed;
                    color = (k.Opened, delta > 0) switch { (true, true) => Long, (true, false) => Short, (false, false) => FlowFormat.Alpha(Short, 90), _ => FlowFormat.Alpha(Long, 90) };
                }
                else signed = 0;
            }
        }
        _bars.Set(bar, signed ?? 0, signed.HasValue);
        if (signed is { } v)
        {
            _bars.Colors[bar] = color;
            running += v;
        }
        _line.Set(bar, running, signed.HasValue || bar > 0 && _line.IsAvailable(bar - 1));
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (Container == null) return;
        var r = Container.Region;
        context.DrawString("Solid: opened · faint: closed (estimate)", _font, FlowFormat.Dim, r.Left + 8, r.Top + 16);
        if (_derivatives.Message is { Length: > 0 } message) context.DrawString(message, _font, FlowFormat.Dim, r.Left + 8, r.Top + 29);
        if (_by != ClassifyBy.Price || CurrentBar < 2) return;
        var counts = new int[5];
        int n = 0;
        for (int b = Math.Max(0, CurrentBar - RegimeWindow); b < CurrentBar; b++)
            if (_regimes.TryGetValue(b, out var g)) { counts[(int)g]++; n++; }
        if (n == 0) return;
        string[] names = { "", "New longs", "New shorts", "Shorts covering", "Longs closing" };
        Color[] colors = { Color.Empty, Long, Short, FlowFormat.Alpha(Long, 150), FlowFormat.Alpha(Short, 150) };
        int w = 170, x = r.Right - w - 8, y = r.Top + 16;
        context.FillRectangle(Color.FromArgb(215, 18, 20, 26), new Rectangle(x, y, w, 4 * 13 + 8), 4);
        for (int k = 1; k <= 4; k++)
        {
            context.FillRectangle(colors[k], new Rectangle(x + 6, y + 6 + (k - 1) * 13, 8, 8));
            context.DrawString($"{UiText.Tr(names[k])} {100.0 * counts[k] / n:0} %", _font, FlowFormat.Dim, x + 18, y + 3 + (k - 1) * 13);
        }
    }
}
