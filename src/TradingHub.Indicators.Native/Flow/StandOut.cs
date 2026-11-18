// Behavior adapted from the KVP capability reference (commit 8cec920); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Print absorption heuristic: a lot of market-order notional on one side, at one price, inside a short window (10 ms by
/// default). Each of those prints met resting liquidity at that price, so the passive side took it all. The mark records
/// what traded (a circle at the price, in the absorbing side's color); it says nothing about what price did next and is
/// not proof of an iceberg. Only individually observed prints with a known side are used: compact packets cannot be
/// split into milliseconds. The threshold is fixed notional or automatic: mean + k deviations of the window sums in the
/// 100 bars before each bar, so a bar's marks never depend on which bars were drawn first.
/// </summary>
[DisplayName("Print absorption (heuristic)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Marks where resting liquidity absorbed a burst of one-sided market orders at one price within a few milliseconds. A heuristic from observed prints only; optional live notifications through the Alert Center.")]
public sealed class PrintAbsorption : Indicator
{
    /// <summary>An absorption: time, price, notional and whether the aggressors were buying (the ask absorbed).</summary>
    public readonly record struct Mark(DateTime Time, decimal Price, decimal Notional, bool AggressorBuy, int Fills);

    // Automatic threshold: the group window sums of the bars before a bar (cached per closed bar).
    private const int AutomaticBars = 100;
    private readonly Dictionary<int, (DateTime Time, long Revision, decimal Trades, List<Mark> Marks)> _cache = new();
    private readonly Dictionary<int, (DateTime Time, long Revision, decimal Trades, double Count, double Sum, double Squares)> _sums = new();
    private readonly List<BarTrade> _trades = new();
    private readonly LiveAlertGate _gate = new();
    private readonly RenderFont _font = new("Arial", 7);
    private DateTime _lastNotified = DateTime.MinValue;

    public PrintAbsorption() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("ABS", "Absorption") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Window (ms)", GroupName = "Calculation", Order = 10)]
    [Range(1, 5000)]
    public int WindowMilliseconds { get; set; } = 10;

    [Display(Name = "Threshold (notional; 0 = automatic)", GroupName = "Calculation", Order = 20)]
    [Range(0, 1_000_000_000_000)]
    public decimal ThresholdNotional { get; set; } = 250_000;

    [Display(Name = "Automatic: k deviations", GroupName = "Calculation", Order = 21)]
    [Range(0.5, 20)]
    public decimal K { get; set; } = 4;

    [Display(Name = "Color when the bid absorbed (market sells)", GroupName = "Colors", Order = 30)]
    public Color BidAbsorbColor { get; set; } = Color.FromArgb(255, 38, 194, 129);

    [Display(Name = "Color when the ask absorbed (market buys)", GroupName = "Colors", Order = 31)]
    public Color AskAbsorbColor { get; set; } = Color.FromArgb(255, 240, 82, 90);

    [Display(Name = "Notify on a new live absorption", GroupName = "Alerts", Order = 40)]
    public bool Notify { get; set; }

    [Browsable(false)]
    public Func<DateTime> Clock { get => _gate.Clock; set => _gate.Clock = value; }

    protected override void OnRecalculate()
    {
        _cache.Clear();
        _sums.Clear();
    }

    /// <summary>
    /// Absorptions in time-ordered prints: per price and side, the notional inside a sliding window; a group (prints closer
    /// than the window to each other) counts when its best window reaches <paramref name="threshold"/>. Prints without an
    /// observed side are ignored. Every group's best window sum is appended to <paramref name="sums"/> when supplied.
    /// </summary>
    public static List<Mark> Find(IReadOnlyList<BarTrade> trades, int windowMilliseconds, decimal threshold, List<decimal>? sums = null, decimal pointValue = 1)
    {
        var result = new List<Mark>();
        var window = TimeSpan.FromMilliseconds(Math.Max(1, windowMilliseconds));
        var groups = new Dictionary<(decimal Price, bool Buy), Group>();
        foreach (var t in trades)
        {
            if (t.Side is not (TradeSide.Buy or TradeSide.Sell) || !t.Provenance.IsObservedTrade || t.Provenance.IsSideInferred) continue;
            var key = (t.Price, t.Side == TradeSide.Buy);
            if (groups.TryGetValue(key, out var g) && t.Time - g.Last > window)
            {
                Close(key, g);
                groups.Remove(key);
                g = null;
            }
            if (g == null) groups[key] = g = new Group();
            decimal notional = t.Price * t.Volume * pointValue;
            g.Window.Enqueue((t.Time, notional));
            g.Sum += notional;
            while (g.Window.Count > 0 && t.Time - g.Window.Peek().Time > window) g.Sum -= g.Window.Dequeue().Notional;
            g.Fills++;
            g.Last = t.Time;
            if (g.Sum > g.Best) { g.Best = g.Sum; g.BestAt = t.Time; }
        }
        foreach (var (key, g) in groups) Close(key, g);
        return result.OrderBy(m => m.Time).ToList();

        void Close((decimal Price, bool Buy) key, Group g)
        {
            sums?.Add(g.Best);
            if (g.Best >= threshold) result.Add(new Mark(g.BestAt, key.Price, g.Best, key.Buy, g.Fills));
        }
    }

    private sealed class Group
    {
        public readonly Queue<(DateTime Time, decimal Notional)> Window = new();
        public decimal Sum, Best;
        public int Fills;
        public DateTime Last, BestAt;
    }

    /// <summary>Bar <paramref name="bar"/>'s threshold: fixed, or mean + k deviations of the window sums of the preceding bars.</summary>
    private decimal Threshold(int bar)
    {
        if (ThresholdNotional > 0) return ThresholdNotional;
        double count = 0, sum = 0, squares = 0;
        for (int b = Math.Max(0, bar - AutomaticBars); b < bar; b++)
        {
            var s = SumsOf(b);
            count += s.Count;
            sum += s.Sum;
            squares += s.Squares;
        }
        if (count < 50) return 250_000;
        double mean = sum / count;
        return (decimal)(mean + (double)K * Math.Sqrt(Math.Max(0, squares / count - mean * mean)));
    }

    /// <summary>Count, sum and sum of squares of a bar's group window sums; closed bars are cached.</summary>
    private (double Count, double Sum, double Squares) SumsOf(int bar)
    {
        var c = GetCandle(bar);
        if (_sums.TryGetValue(bar, out var hit) && hit.Time == c.Time && hit.Revision == c.Revision && hit.Trades == c.Ticks) return (hit.Count, hit.Sum, hit.Squares);
        var sums = new List<decimal>();
        Find(SortedTrades(bar), WindowMilliseconds, decimal.MaxValue, sums, FlowFormat.PointValue(this));
        return Remember(bar, c, sums);
    }

    private (double Count, double Sum, double Squares) Remember(int bar, IndicatorCandle c, List<decimal> sums)
    {
        double sum = 0, squares = 0;
        foreach (var s in sums)
        {
            double v = (double)s;
            sum += v;
            squares += v * v;
        }
        if (c.IsCandleFinished) _sums[bar] = (c.Time, c.Revision, c.Ticks, sums.Count, sum, squares);
        return (sums.Count, sum, squares);
    }

    private List<BarTrade> SortedTrades(int bar)
    {
        GetBarTrades(bar, _trades);
        _trades.Sort((a, b) => a.Time.CompareTo(b.Time));
        return _trades;
    }

    /// <summary>The bar's marks; closed bars are cached.</summary>
    internal List<Mark> MarksOf(int bar)
    {
        var c = GetCandle(bar);
        if (_cache.TryGetValue(bar, out var hit) && hit.Time == c.Time && hit.Revision == c.Revision && hit.Trades == c.Ticks) return hit.Marks;
        decimal threshold = Threshold(bar);
        var sums = new List<decimal>();
        var marks = Find(SortedTrades(bar), WindowMilliseconds, threshold, sums, FlowFormat.PointValue(this));
        Remember(bar, c, sums);
        _cache[bar] = (c.Time, c.Revision, c.Ticks, marks);
        return marks;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (!Notify || bar != CurrentBar - 1) return;
        foreach (var m in MarksOf(bar))
        {
            if (m.Time <= _lastNotified || !_gate.Allow(m.Time)) continue;
            _lastNotified = m.Time;
            string notional = FlowFormat.Compact(m.Notional), price = m.Price.ToString(CultureInfo.InvariantCulture);
            AddAlert("alert2", m.AggressorBuy ? UiText.Tr($"Absorption heuristic: {notional} at {price} (the ask absorbed)") : UiText.Tr($"Absorption heuristic: {notional} at {price} (the bid absorbed)"));
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var pc = ChartInfo.PriceChartContainer;
        int first = Math.Max(0, pc.FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, pc.LastVisibleBarNumber);
        var all = new List<Mark>();
        for (int b = first; b <= last; b++) all.AddRange(MarksOf(b));
        FootprintStudyData.Legend(context, pc.Region, 2, "Print absorption: a heuristic from observed prints, not proof of resting icebergs", Color.Gold);
        if (all.Count == 0) return;
        decimal max = all.Max(m => m.Notional);
        foreach (var m in all)
        {
            int x = BookChartTools.XOf(this, m.Time), y = ChartInfo.GetYByPrice(m.Price, false);
            double t = Math.Sqrt((double)(m.Notional / max));
            int r = (int)(4 + 10 * t);
            var color = m.AggressorBuy ? AskAbsorbColor : BidAbsorbColor;
            var rect = new Rectangle(x - r, y - r, 2 * r, 2 * r);
            context.DrawEllipse(new RenderPen(color, 1.6f), rect);
            context.FillEllipse(FlowFormat.Alpha(color, 50), rect);
            if (r >= 9) context.DrawString(FlowFormat.Compact(m.Notional), _font, Color.FromArgb(230, 240, 240, 245), x + r + 2, y - 5);
        }
    }
}

/// <summary>
/// Rejected aggression ("trapped traders" heuristic): in a closed bar whose upper (lower) wick holds more net aggressive
/// buying (selling) than any equally tall stretch of the rest of the bar, and which then closed at least one ATR beyond
/// those aggressors' estimated average price, the wick is framed for N bars or until price closes back beyond that average.
/// Describes flow that price rejected; it does not know who holds what and is not a proven signal. Needs an observed
/// footprint; bars without one are skipped.
/// </summary>
[DisplayName("Rejected aggression (trapped traders heuristic)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Frames the wick of a closed bar whose heavy aggressive buying or selling price then rejected by at least one ATR, with the aggressors' estimated average price. A heuristic from the observed footprint.")]
public sealed class TrappedTraders : Indicator
{
    public readonly record struct Trap(int Bar, DateTime Time, decimal Low, decimal High, decimal AveragePrice, decimal Net, bool Buyers);

    private readonly List<Trap> _traps = new();
    private readonly HashSet<int> _decided = new();
    // Traps already notified; kept across recalculations so a re-decided trap never alerts twice.
    private readonly HashSet<(DateTime Time, bool Buyers)> _notified = new();
    private readonly LiveAlertGate _gate = new();
    private readonly RenderFont _font = new("Arial", 7);
    private int _atr = 14, _keep = 12;
    private decimal _multiple = 1;

    public TrappedTraders() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("TRAP", "Trapped") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "ATR (bars)", GroupName = "Calculation", Order = 10)]
    [Range(2, 200)]
    public int AtrPeriod { get => _atr; set { _atr = Math.Clamp(value, 2, 200); RecalculateValues(); } }

    [Display(Name = "ATR multiples the close must reject", GroupName = "Calculation", Order = 20)]
    [Range(0.1, 20)]
    public decimal AtrMultiple { get => _multiple; set { _multiple = Math.Max(0.1m, value); RecalculateValues(); } }

    [Display(Name = "Keep for (bars)", GroupName = "Visualization", Order = 30)]
    [Range(1, 200)]
    public int KeepBars { get => _keep; set { _keep = Math.Clamp(value, 1, 200); RecalculateValues(); } }

    [Display(Name = "Color", GroupName = "Visualization", Order = 40)]
    public Color Color { get; set; } = Color.FromArgb(255, 242, 181, 58);

    [Display(Name = "Notify when a new one is marked live", GroupName = "Alerts", Order = 50)]
    public bool Notify { get; set; }

    [Browsable(false)]
    public Func<DateTime> Clock { get => _gate.Clock; set => _gate.Clock = value; }

    [Browsable(false)]
    public IReadOnlyList<Trap> Traps => _traps;

    protected override void OnRecalculate()
    {
        _traps.Clear();
        _decided.Clear();
    }

    private decimal Atr(int bar)
    {
        int n = Math.Min(_atr, bar);
        if (n <= 0) return 0;
        decimal sum = 0;
        for (int i = bar - n; i < bar; i++)
        {
            var c = GetCandle(i);
            var previous = i > 0 ? GetCandle(i - 1).Close : c.Open;
            sum += Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - previous), Math.Abs(c.Low - previous)));
        }
        return sum / n;
    }

    /// <summary>
    /// The upper (buyers) or lower (sellers) wick of a footprint between <paramref name="from"/> and <paramref name="to"/>: net
    /// aggression in the wick, the aggressors' volume-weighted average price there, and the best net of any equally tall
    /// stretch outside the wick.
    /// </summary>
    public static (decimal WickNet, decimal Average, decimal OtherBest) Wick(IReadOnlyList<PriceVolumeInfo> levels, decimal from, decimal to, bool buyers)
    {
        decimal wick = 0, volume = 0, weighted = 0;
        int inside = 0;
        var outside = new List<decimal>();
        foreach (var l in levels.OrderBy(l => l.Price))
        {
            decimal net = buyers ? l.Ask - l.Bid : l.Bid - l.Ask;
            if (l.Price >= from && l.Price <= to)
            {
                wick += net;
                decimal v = buyers ? l.Ask : l.Bid;
                volume += v;
                weighted += v * l.Price;
                inside++;
            }
            else outside.Add(net);
        }
        int h = Math.Max(1, inside);
        decimal? best = null;
        for (int i = 0; i + h <= outside.Count; i++)
        {
            decimal s = 0;
            for (int j = i; j < i + h; j++) s += outside[j];
            best = best is { } b ? Math.Max(b, s) : s;
        }
        return (wick, volume > 0 ? weighted / volume : (from + to) / 2, best ?? 0);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        int decide = GetCandle(bar).IsCandleFinished ? bar : bar - 1;
        if (decide < _atr || !_decided.Add(decide)) return;
        var c = GetCandle(decide);
        if (!c.HasObservedFootprint || c.HasInferredSide) return;
        var levels = c.GetAllPriceLevelsList();
        if (levels.Count < 3) return;
        decimal atr = Atr(decide) * _multiple;
        if (atr <= 0) return;
        decimal tick = BookChartTools.Tick(this);
        decimal top = Math.Max(c.Open, c.Close), bottom = Math.Min(c.Open, c.Close);
        foreach (bool buyers in new[] { true, false })
        {
            decimal lo = buyers ? top + tick : c.Low, hi = buyers ? c.High : bottom - tick;
            if (hi < lo) continue;
            var (net, average, other) = Wick(levels, lo, hi, buyers);
            if (net <= 0 || net <= other) continue;
            if (!(buyers ? c.Close <= average - atr : c.Close >= average + atr)) continue;
            _traps.Add(new Trap(decide, c.Time, lo, hi, average, net, buyers));
            var end = decide + 1 < CurrentBar ? GetCandle(decide + 1).Time : c.LastTime;
            if (Notify && !_notified.Contains((c.Time, buyers)) && _gate.Allow(end))
            {
                _notified.Add((c.Time, buyers));
                string amount = FlowFormat.Compact(net);
                AddAlert("alert2", buyers ? UiText.Tr($"Trapped buyers heuristic: wick with {amount} net rejected") : UiText.Tr($"Trapped sellers heuristic: wick with {amount} net rejected"));
            }
        }
    }

    /// <summary>Last bar of a trap: when price closes back beyond the average, or after <see cref="KeepBars"/>.</summary>
    private (int End, bool Released) Life(Trap t)
    {
        int end = Math.Min(CurrentBar - 1, t.Bar + _keep);
        for (int b = t.Bar + 1; b <= end; b++)
            if (t.Buyers ? GetCandle(b).Close > t.AveragePrice : GetCandle(b).Close < t.AveragePrice) return (b, true);
        return (end, false);
    }

    /// <summary>Each recent trap's average price, alertable while it lives.</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        foreach (var t in _traps.Skip(Math.Max(0, _traps.Count - 50)))
        {
            if (t.Bar >= CurrentBar) continue;
            var (end, released) = Life(t);
            yield return new IndicatorLevel($"trap-{(t.Buyers ? "buyers" : "sellers")}:{t.Time.Ticks}",
                t.Buyers ? "Trapped buyers average (heuristic)" : "Trapped sellers average (heuristic)", t.AveragePrice, t.Bar, released ? end : t.Bar + _keep);
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var pc = ChartInfo.PriceChartContainer;
        int bw = Math.Max(2, pc.BarsWidth);
        foreach (var t in _traps)
        {
            if (t.Bar > pc.LastVisibleBarNumber || t.Bar + _keep < pc.FirstVisibleBarNumber) continue;
            int end = Life(t).End;
            int x0 = ChartInfo.GetXByBar(t.Bar), x1 = ChartInfo.GetXByBar(end) + bw;
            int y0 = ChartInfo.GetYByPrice(t.High, false), y1 = ChartInfo.GetYByPrice(t.Low, false);
            context.DrawRectangle(new RenderPen(Color, 1.5f), new Rectangle(x0 - 1, Math.Min(y0, y1) - 1, bw + 2, Math.Abs(y1 - y0) + 2));
            int ya = ChartInfo.GetYByPrice(t.AveragePrice, false);
            context.DrawLine(new RenderPen(FlowFormat.Alpha(Color, 160), 1, System.Drawing.Drawing2D.DashStyle.Dot), x0, ya, x1, ya);
            context.DrawString(t.Buyers ? "▲✕" : "▼✕", _font, Color, x0, t.Buyers ? Math.Min(y0, y1) - 11 : Math.Max(y0, y1) + 2);
        }
    }
}

/// <summary>
/// Highlights and notifications: a bar stands out when its volume (or absolute measured delta) exceeds the mean plus k
/// deviations of the preceding bars; it is ringed with a faint column. Optional live notifications: a bar that stood out
/// at its close, an aggressor-flow burst (a ten-second notional delta far above the last half hour), a large resting wall
/// that appears or is pulled before price reaches it, and the book balance (within 1 %) flipping. Notifications go to the
/// Alert Center, never for history or replay, and at most four per ten seconds.
/// </summary>
[DisplayName("Highlights and notifications (volume, delta, walls, book balance)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Marks bars with unusual volume or measured delta (mean + k deviations of preceding bars) and can notify on live flow bursts, book walls that appear or are pulled, and book-balance flips, through the Alert Center.")]
public sealed class HighlightsAndNotifications : Indicator
{
    private readonly LiveAlertGate _gate = new();
    private readonly HashSet<DateTime> _notifiedBars = new();
    private readonly Dictionary<long, decimal> _walls = new();
    private readonly List<decimal> _flowHistory = new();
    private readonly RenderFont _font = new("Arial", 7);
    private DateTime _bookStart = DateTime.MinValue, _flowSlot = DateTime.MinValue;
    // Wall price bin of this book session, fixed from its first mid so a wall keeps its key while the mid moves.
    private decimal _bin;
    private decimal _slotDelta;
    private bool _slotNotified;
    private int _balance;

    public HighlightsAndNotifications() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("HVOL", "Highlighted") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "k (deviations above the mean)", GroupName = "Rule", Order = 10)]
    [Range(0.5, 20)]
    public decimal K { get; set; } = 2;

    [Display(Name = "Reference bars", GroupName = "Rule", Order = 11)]
    [Range(12, 1000)]
    public int Baseline { get; set; } = 72;

    [Display(Name = "Highlight volume", GroupName = "Highlights", Order = 20)]
    public bool HighlightVolume { get; set; } = true;

    [Display(Name = "Highlight measured delta", GroupName = "Highlights", Order = 21)]
    public bool HighlightDelta { get; set; } = true;

    [Display(Name = "Notify when a highlighted bar closes", GroupName = "Notifications", Order = 30)]
    public bool NotifyBar { get; set; }

    [Display(Name = "Notify on a flow burst (10 s)", GroupName = "Notifications", Order = 31)]
    public bool NotifyBurst { get; set; }

    [Display(Name = "Burst: deviations above the last 30 minutes", GroupName = "Notifications", Order = 32)]
    [Range(1, 20)]
    public decimal BurstSigmas { get; set; } = 4;

    [Display(Name = "Burst: minimum notional", GroupName = "Notifications", Order = 33)]
    [Range(0, 1_000_000_000_000)]
    public decimal BurstMinimum { get; set; } = 250_000;

    [Display(Name = "Notify when walls appear or are pulled", GroupName = "Notifications", Order = 34)]
    public bool NotifyWalls { get; set; }

    [Display(Name = "Wall from (notional, within 1 %)", GroupName = "Notifications", Order = 35)]
    [Range(0, 1_000_000_000_000)]
    public decimal WallNotional { get; set; } = 5_000_000;

    [Display(Name = "Notify when the book balance (1 %) flips", GroupName = "Notifications", Order = 36)]
    public bool NotifyBalance { get; set; }

    [Display(Name = "Color", GroupName = "Visualization", Order = 40)]
    public Color Color { get; set; } = Color.FromArgb(255, 242, 181, 58);

    [Browsable(false)]
    public Func<DateTime> Clock { get => _gate.Clock; set => _gate.Clock = value; }

    protected override void OnRecalculate()
    {
        _walls.Clear();
        _flowHistory.Clear();
        _flowSlot = DateTime.MinValue;
        _slotDelta = 0;
        _balance = 0;
        _bookStart = DateTime.MinValue;
        _bin = 0;
    }

    private double? VolumeOf(int bar) => GetCandle(bar).CanCalculateCausally ? (double)GetCandle(bar).Volume : null;

    private double? DeltaOf(int bar) => StudyMath.TryDelta(GetCandle(bar), false, out var delta, out _) ? Math.Abs((double)delta) : null;

    /// <summary>Whether a bar stands out, and why (English source text with its deviation count).</summary>
    public bool Stands(int bar, out string why)
    {
        why = "";
        double k = (double)K;
        if (HighlightVolume && VolumeOf(bar) is { } v && StandOutRule.Threshold(VolumeOf, bar, Baseline, k) is { } tv && v > tv)
        {
            string sigmas = (StandOutRule.Sigmas(VolumeOf, bar, Baseline) ?? 0).ToString("0.0", CultureInfo.InvariantCulture);
            why = UiText.Tr($"volume {sigmas} σ");
            return true;
        }
        if (HighlightDelta && DeltaOf(bar) is { } d && StandOutRule.Threshold(DeltaOf, bar, Baseline, k) is { } td && d > td)
        {
            string sigmas = (StandOutRule.Sigmas(DeltaOf, bar, Baseline) ?? 0).ToString("0.0", CultureInfo.InvariantCulture);
            why = UiText.Tr($"delta {sigmas} σ");
            return true;
        }
        return false;
    }

    private void Notify(DateTime eventUtc, string message)
    {
        if (_gate.Allow(eventUtc)) AddAlert("alert2", message);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (!NotifyBar || bar != CurrentBar - 1) return;
        int closed = GetCandle(bar).IsCandleFinished ? bar : bar - 1;
        if (closed < 0) return;
        var c = GetCandle(closed);
        var end = closed + 1 < CurrentBar ? GetCandle(closed + 1).Time : c.LastTime;
        if (_notifiedBars.Contains(c.Time) || !Stands(closed, out var why)) return;
        _notifiedBars.Add(c.Time);
        Notify(end, UiText.Tr($"Highlighted bar: {why}"));
    }

    protected override void OnNewTrades(IEnumerable<MarketDataArg> trades)
    {
        if (!NotifyBurst) return;
        foreach (var trade in trades)
        {
            if (!trade.IsObservedTrade || trade.Direction is not (TradeDirection.Buy or TradeDirection.Sell)) continue;
            var slot = new DateTime(trade.Time.Ticks / (TimeSpan.TicksPerSecond * 10) * TimeSpan.TicksPerSecond * 10, DateTimeKind.Utc);
            if (slot != _flowSlot)
            {
                if (_flowSlot != DateTime.MinValue)
                {
                    _flowHistory.Add(_slotDelta);
                    if (_flowHistory.Count > 180) _flowHistory.RemoveAt(0);
                }
                _flowSlot = slot;
                _slotDelta = 0;
                _slotNotified = false;
            }
            _slotDelta += trade.Volume * trade.Price * FlowFormat.PointValue(this) * (trade.Direction == TradeDirection.Buy ? 1 : -1);
            if (_slotNotified || _flowHistory.Count < 30) continue;
            decimal mean = _flowHistory.Average();
            double deviation = Math.Sqrt(_flowHistory.Sum(x => (double)((x - mean) * (x - mean))) / _flowHistory.Count);
            if (deviation > 0 && Math.Abs((double)(_slotDelta - mean)) > (double)BurstSigmas * deviation && Math.Abs(_slotDelta) >= BurstMinimum)
            {
                _slotNotified = true;
                Notify(trade.Time, UiText.Tr($"Flow burst: {FlowFormat.Signed(_slotDelta)} in 10 s"));
            }
        }
    }

    protected override void OnDepthSnapshot(DepthUpdate update)
    {
        if (!NotifyWalls && !NotifyBalance || !update.IsUsable) return;
        var book = update.Snapshot;
        var now = update.ReceivedAt;
        if (_bookStart == DateTime.MinValue) _bookStart = now;
        if (now - _bookStart < TimeSpan.FromSeconds(10)) return; // the first seconds of a book are not changes
        decimal tick = BookChartTools.Tick(this);
        decimal mid = (book.BestBidTicks + book.BestAskTicks) * tick / 2;
        if (mid <= 0) return;
        if (NotifyWalls)
        {
            if (_bin <= 0) _bin = tick * Math.Max(1, Math.Ceiling(mid * 0.0002m / tick));
            decimal bin = _bin;
            var sizes = new Dictionary<long, decimal>();
            foreach (var level in book.Bids.Concat(book.Asks))
            {
                decimal price = level.PriceTicks * tick;
                if (Math.Abs(price - mid) > mid * 0.01m) continue;
                long key = (long)Math.Floor(price / bin);
                sizes[key] = sizes.GetValueOrDefault(key) + level.Size * price * FlowFormat.PointValue(this);
            }
            foreach (var (key, notional) in sizes)
                if (notional >= WallNotional && _walls.TryAdd(key, notional))
                {
                    string at = (key * bin).ToString("0.##", CultureInfo.InvariantCulture);
                    Notify(now, UiText.Tr($"Wall of {FlowFormat.Compact(notional)} at {at}"));
                }
            foreach (var key in _walls.Keys.ToList())
            {
                if (sizes.TryGetValue(key, out var n) && n >= WallNotional / 2) continue;
                decimal price = key * bin;
                _walls.Remove(key);
                string pulledAt = price.ToString("0.##", CultureInfo.InvariantCulture);
                if (Math.Abs(price - mid) > bin * 2) Notify(now, UiText.Tr($"Wall pulled at {pulledAt} before price reached it"));
            }
        }
        if (NotifyBalance)
        {
            var band = DepthBands.WithinPercent(book, 1);
            if (!band.Available) return;
            decimal imbalance = band.Bid + band.Ask > 0 ? (band.Bid - band.Ask) / (band.Bid + band.Ask) : 0;
            int side = imbalance > 0.15m ? 1 : imbalance < -0.15m ? -1 : _balance; // hysteresis
            if (side != _balance && _balance != 0)
                Notify(now, UiText.Tr(side > 0 ? "Book balance flipped: more bids within 1 %" : "Book balance flipped: more asks within 1 %"));
            _balance = side;
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= StandOutRule.MinimumBaseline) return;
        var pc = ChartInfo.PriceChartContainer;
        var region = pc.Region;
        int first = Math.Max(0, pc.FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, pc.LastVisibleBarNumber);
        int bw = Math.Max(2, pc.BarsWidth);
        for (int b = first; b <= last; b++)
        {
            if (!Stands(b, out var why)) continue;
            var c = GetCandle(b);
            int x = ChartInfo.GetXByBar(b);
            context.FillRectangle(FlowFormat.Alpha(Color, 18), new Rectangle(x, region.Top, bw, region.Height));
            int y0 = ChartInfo.GetYByPrice(c.High, false), y1 = ChartInfo.GetYByPrice(c.Low, false);
            context.DrawRectangle(new RenderPen(FlowFormat.Alpha(Color, 220), 1.5f), new Rectangle(x - 2, Math.Min(y0, y1) - 2, bw + 4, Math.Abs(y1 - y0) + 4));
            if (bw >= 18) context.DrawString(why, _font, Color, x, Math.Min(y0, y1) - 12);
        }
    }
}
