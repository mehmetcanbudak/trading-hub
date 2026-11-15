// Behaviour adapted from gouqizi2214730-source/trading-hub f041ff6 (MIT with Commons Clause; see THIRD_PARTY_NOTICES.md),
// reading the existing opt-in depth recording instead of heatmap samples; pull/chase marks are labelled heuristics.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Observed per-bar footprint volume used to attribute removed resting size to trades. Bars without an observed
/// tick-resolution footprint are not covered: removals there are not split into filled and pulled.
/// </summary>
internal sealed class FootprintTrades : BookTradeAttribution
{
    private readonly List<(DateTime Start, DateTime End, Dictionary<(BookSide, long), decimal>? Left)> _bars = new();

    public override BookTradeAttributionKind Kind => BookTradeAttributionKind.Pooled;

    /// <summary>Observed footprint volume for bars [first, last]; null rows mark bars without usable footprint.</summary>
    public static FootprintTrades For(Indicator owner, int first, int last, decimal tick)
    {
        var result = new FootprintTrades();
        for (int bar = Math.Max(0, first); bar <= last && bar < owner.CurrentBar; bar++)
        {
            var candle = owner.GetCandle(bar);
            DateTime start = candle.Time, end = bar + 1 < owner.CurrentBar ? owner.GetCandle(bar + 1).Time : DateTime.MaxValue;
            Dictionary<(BookSide, long), decimal>? rows = null;
            // Only exact tick rows can be matched to book prices; compact aggregates are not individual prints.
            if (tick > 0 && candle.HasObservedFootprint && !candle.HasAggregatedFootprint && candle.ProfileRowSize == tick)
            {
                rows = new();
                foreach (var level in candle.GetAllPriceLevels())
                {
                    long price = (long)Math.Round(level.Price / tick, MidpointRounding.AwayFromZero);
                    if (level.Bid > 0) rows[(BookSide.Bid, price)] = rows.GetValueOrDefault((BookSide.Bid, price)) + level.Bid;
                    if (level.Ask > 0) rows[(BookSide.Ask, price)] = rows.GetValueOrDefault((BookSide.Ask, price)) + level.Ask;
                }
            }
            result._bars.Add((start, end, rows));
        }
        return result;
    }

    public override bool Covers(DateTime from, DateTime to)
    {
        bool any = false;
        foreach (var (start, end, rows) in _bars)
        {
            if (end <= from || start > to) continue;
            if (rows == null) return false;
            any = true;
        }
        return any;
    }

    public override decimal Take(BookSide side, long priceTicks, DateTime from, DateTime to, decimal max)
    {
        decimal taken = 0;
        foreach (var (start, end, rows) in _bars)
        {
            if (taken >= max) break;
            if (rows == null || end <= from || start > to || !rows.TryGetValue((side, priceTicks), out var left) || left <= 0) continue;
            decimal use = Math.Min(left, max - taken);
            rows[(side, priceTicks)] = left - use;
            taken += use;
        }
        return taken;
    }

    public override decimal Peek(BookSide side, long priceTicks, DateTime from, DateTime to)
    {
        decimal sum = 0;
        foreach (var (start, end, rows) in _bars)
            if (rows != null && end > from && start <= to) sum += rows.GetValueOrDefault((side, priceTicks));
        return sum;
    }
}

/// <summary>Shared placement helpers for the order-book indicators.</summary>
internal static class BookChartTools
{
    public static decimal Tick(Indicator indicator) => indicator.InstrumentInfo is { TickSize: > 0 } info ? info.TickSize : 1m;

    /// <summary>Bar containing a time (binary search; −1 before the first bar).</summary>
    public static int BarOf(Indicator indicator, DateTime time)
    {
        int lo = 0, hi = indicator.CurrentBar - 1, answer = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (indicator.GetCandle(mid).Time <= time) { answer = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return answer;
    }

    /// <summary>X of a time inside its bar, proportional to the bar period.</summary>
    public static int XOf(Indicator indicator, DateTime time)
    {
        var info = indicator.ChartInfo!;
        int bar = Math.Max(0, BarOf(indicator, time));
        int x0 = info.GetXByBar(bar, true);
        var length = info.GetTimeFrameTypeChartPeriod();
        if (length <= TimeSpan.Zero) return x0;
        double fraction = Math.Clamp((time - indicator.GetCandle(bar).Time).TotalSeconds / length.TotalSeconds, 0, 1);
        return x0 + (int)(fraction * Math.Max(1, info.PriceChartContainer.BarsWidth));
    }

    public static string Compact(decimal value)
    {
        decimal a = Math.Abs(value);
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return a >= 1_000_000 ? (value / 1_000_000).ToString("0.#", c) + "M"
            : a >= 1000 ? (value / 1000).ToString("0.#", c) + "K"
            : a >= 10 ? value.ToString("0", c) : value.ToString("0.##", c);
    }
}

/// <summary>
/// Order-book changes ladder: per price, resting size added and removed over a window, from recorded received
/// snapshots. Removed size is split into filled (matched to observed footprint volume at that price) and pulled
/// (the unexplained rest). Snapshot differences never show orders added and removed between two snapshots.
/// </summary>
[DisplayName("Order book changes (added / pulled)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Ladder of recorded L2 changes per price: size added, size removed without matching trades (pulled) and removed size matched to observed footprint volume (filled). Snapshot differences, not individual orders; gaps and stale intervals are skipped and counted. Needs explicit depth recording.")]
public sealed class OrderBookChanges : Indicator
{
    public enum ChangeWindow
    {
        [Display(Name = "Visible bars")] Visible,
        [Display(Name = "1 hour")] Hour1,
        [Display(Name = "4 hours")] Hour4,
        [Display(Name = "1 day")] Day1,
        [Display(Name = "Current session")] Session,
        [Display(Name = "Custom hours")] Custom,
    }

    public enum ChangeView
    {
        [Display(Name = "Added and removed (two columns)")] Both,
        [Display(Name = "Net (added minus removed)")] Net,
    }

    private readonly Dictionary<int, (DateTime Time, int Count, long Last, long Revision, BookChangesResult Result)> _perBar = new();
    private ChangeWindow _window = ChangeWindow.Hour1;
    private int _maxAge = 10;
    private bool _allowPartial = true;

    public OrderBookChanges() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Window", GroupName = "Calculation", Order = 10)]
    public ChangeWindow Window { get => _window; set { _window = value; RedrawChart(); } }

    [Display(Name = "Hours (custom window)", GroupName = "Calculation", Order = 20)]
    [Range(1, 720)]
    public int CustomHours { get; set; } = 24;

    [Display(Name = "Group rows by ticks (0 = automatic)", GroupName = "Calculation", Order = 30)]
    [Range(0, 1_000_000)]
    public int GroupTicks { get; set; }

    [Display(Name = "Approximate rows (automatic grouping)", GroupName = "Calculation", Order = 35)]
    [Range(10, 200)]
    public int AutoRows { get; set; } = 40;

    [Display(Name = "Stale timeout (seconds)", GroupName = "Calculation", Order = 36)]
    [Description("Consecutive snapshots further apart are not compared; the book between them was not observed.")]
    [Range(1, 300)]
    public int MaxAgeSeconds { get => _maxAge; set { _maxAge = Math.Clamp(value, 1, 300); _perBar.Clear(); RedrawChart(); } }

    [Display(Name = "Allow labelled partial provider books", GroupName = "Calculation", Order = 37)]
    public bool AllowPartialBook { get => _allowPartial; set { _allowPartial = value; _perBar.Clear(); RedrawChart(); } }

    [Display(Name = "View", GroupName = "Display", Order = 40)]
    public ChangeView View { get; set; } = ChangeView.Both;

    [Display(Name = "Show filled (removed by trades)", GroupName = "Display", Order = 45)]
    public bool ShowFilled { get; set; } = true;

    [Display(Name = "Ladder width (px)", GroupName = "Display", Order = 50)]
    [Range(60, 600)]
    public int Width { get; set; } = 180;

    [Display(Name = "Values on each row", GroupName = "Display", Order = 55)]
    public bool ShowValues { get; set; } = true;

    [Display(Name = "Added color", GroupName = "Colors", Order = 60)]
    public Color AddedColor { get; set; } = Color.FromArgb(200, 38, 194, 129);

    [Display(Name = "Pulled color", GroupName = "Colors", Order = 61)]
    public Color PulledColor { get; set; } = Color.FromArgb(200, 240, 82, 90);

    [Display(Name = "Filled color", GroupName = "Colors", Order = 62)]
    public Color FilledColor { get; set; } = Color.FromArgb(160, 242, 181, 58);

    [Display(Name = "Bid side background", GroupName = "Colors", Order = 63)]
    public Color BidBack { get; set; } = Color.FromArgb(28, 61, 139, 253);

    [Display(Name = "Ask side background", GroupName = "Colors", Order = 64)]
    public Color AskBack { get; set; } = Color.FromArgb(28, 240, 82, 90);

    [Browsable(false)] public string Status { get; private set; } = "No recorded depth. Recording must be explicitly enabled; display does not enable it.";

    /// <summary>The window's changes at exact price ticks, with sampling facts (for tests and the legend).</summary>
    [Browsable(false)] public BookChangesResult? LastWindow { get; private set; }

    protected override void OnRecalculate() => _perBar.Clear();
    protected override void OnDepthSnapshot(DepthUpdate update) => RedrawChart();
    protected override void OnDepthHistoryChanged() => RedrawChart();

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    /// <summary>Changes inside one bar (exact ticks), cached while its recorded samples and candle are unchanged.</summary>
    private BookChangesResult BarChanges(int bar, decimal tick)
    {
        var query = GetDepthHistory(bar);
        var candle = GetCandle(bar);
        long last = query.Samples.Count > 0 ? query.Samples[^1].Ordinal : -1;
        if (_perBar.TryGetValue(bar, out var hit) && hit.Time == candle.Time && hit.Count == query.Samples.Count && hit.Last == last && hit.Revision == candle.Revision)
            return hit.Result;
        var trades = FootprintTrades.For(this, bar, bar, tick);
        var options = new BookChangeOptions { MaxAge = TimeSpan.FromSeconds(_maxAge), AllowPartialBook = _allowPartial };
        var result = query.Samples.Count == 0 ? BookChangesResult.Empty
            : BookChanges.Compute(query.Samples, query.Preceding, trades.Covers(candle.Time, candle.Time) ? trades : null, options);
        _perBar[bar] = (candle.Time, query.Samples.Count, last, candle.Revision, result);
        return result;
    }

    /// <summary>Bars covered by the selected window.</summary>
    public (int From, int To) WindowBars()
    {
        int last = CurrentBar - 1;
        if (last < 0) return (0, -1);
        if (_window == ChangeWindow.Visible) return (Math.Max(0, FirstVisibleBarNumber), Math.Min(last, LastVisibleBarNumber));
        if (_window == ChangeWindow.Session)
        {
            int start = last;
            while (start > 0 && !IsNewSession(start)) start--;
            return (start, last);
        }
        var span = _window switch
        {
            ChangeWindow.Hour1 => TimeSpan.FromHours(1),
            ChangeWindow.Hour4 => TimeSpan.FromHours(4),
            ChangeWindow.Day1 => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(Math.Clamp(CustomHours, 1, 720)),
        };
        return (Math.Max(0, BookChartTools.BarOf(this, GetCandle(last).Time - span)), last);
    }

    /// <summary>Sums the window at exact ticks and records the sampling facts in <see cref="Status"/>.</summary>
    public BookChangesResult ComputeWindow()
    {
        decimal tick = BookChartTools.Tick(this);
        var (from, to) = WindowBars();
        var total = new Dictionary<long, BookLevelChange>();
        int samples = 0, comparisons = 0, skipped = 0, unattributed = 0;
        long compared = 0, longest = 0;
        var coverage = DepthCoverage.None;
        for (int bar = from; bar <= to; bar++)
        {
            var result = BarChanges(bar, tick);
            if (result.Samples == 0 && result.Comparisons == 0) continue;
            samples += result.Samples; comparisons += result.Comparisons; skipped += result.SkippedTransitions;
            compared += result.ComparedDuration.Ticks; longest = Math.Max(longest, result.LongestComparedInterval.Ticks);
            coverage |= result.Coverage;
            if (result.Comparisons > 0 && result.Attribution == BookTradeAttributionKind.None) unattributed++;
            foreach (var (price, change) in result.Levels)
            {
                if (!total.TryGetValue(price, out var sum)) total[price] = sum = new BookLevelChange();
                sum.Add(change);
            }
        }
        var window = new BookChangesResult(total, samples, comparisons, skipped, TimeSpan.FromTicks(compared), TimeSpan.FromTicks(longest), coverage,
            unattributed == 0 ? BookTradeAttributionKind.Pooled : BookTradeAttributionKind.None,
            unattributed > 0 ? "Some bars lack an observed tick footprint; their removed size is shown as pulled." : null);
        LastWindow = window;
        Status = comparisons == 0
            ? samples == 0 ? "No recorded depth in the window. Recording must be explicitly enabled; display does not enable it." : "No comparable consecutive snapshots in the window (gaps, stale intervals or source changes)."
            : $"Recorded L2 snapshot differences · {comparisons} comparisons · {skipped} skipped (gaps/stale/source changes) · longest interval {TimeSpan.FromTicks(longest).TotalSeconds:0.###} s · filled/pulled split is a heuristic"
              + (unattributed > 0 ? $" · {unattributed} bars without tick footprint: removals shown as pulled" : "")
              + ((coverage & (DepthCoverage.PartialBook | DepthCoverage.Truncated)) != 0 ? " · partial recorded depth" : "");
        return window;
    }

    private string WindowText() => _window switch
    {
        ChangeWindow.Visible => "visible bars",
        ChangeWindow.Hour1 => "1 hour",
        ChangeWindow.Hour4 => "4 hours",
        ChangeWindow.Day1 => "1 day",
        ChangeWindow.Session => "current session",
        _ => $"{CustomHours} h",
    };

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var container = ChartInfo.PriceChartContainer;
        var region = container.Region;
        decimal tick = BookChartTools.Tick(this);
        var window = ComputeWindow();
        FootprintStudyData.Legend(context, region, 1, Status, window.IsAvailable ? Color.LightGray : Color.Gold);
        if (!window.IsAvailable || window.Levels.Count == 0) return;

        var font = new RenderFont("Arial", 8);
        var dim = Color.FromArgb(200, 180, 186, 196);
        int x1 = region.Right - 4, x0 = x1 - Math.Clamp(Width, 60, 600);
        decimal high = container.High, low = container.Low;
        long group = GroupTicks > 0 ? GroupTicks : Math.Max(1, CandleSeries.NiceStep((long)Math.Ceiling((high - low) / tick / Math.Max(10, AutoRows))));
        var rows = new SortedDictionary<long, BookLevelChange>();
        foreach (var (price, change) in window.Levels)
        {
            long key = CandleSeries.FloorDiv(price, group) * group;
            if (!rows.TryGetValue(key, out var row)) rows[key] = row = new BookLevelChange();
            row.Add(change);
        }

        decimal max = 0;
        foreach (var row in rows.Values)
            max = Math.Max(max, View == ChangeView.Net ? Math.Abs(row.Net) : Math.Max(row.Added, row.Pulled + (ShowFilled ? row.Filled : 0)));
        if (max <= 0) return;

        decimal mid = GetCandle(CurrentBar - 1).Close;
        int column = (x1 - x0) / 2;
        foreach (var (key, row) in rows)
        {
            decimal lo = key * tick, hi = (key + group) * tick;
            if (hi < low || lo > high) continue;
            int yTop = ChartInfo.GetYByPrice(hi, false), yBottom = ChartInfo.GetYByPrice(lo, false);
            int h = Math.Max(1, Math.Abs(yBottom - yTop) - 1), y = Math.Min(yTop, yBottom);
            if (y < region.Top + 30) continue; // below the title box
            context.FillRectangle(lo >= mid ? AskBack : BidBack, new Rectangle(x0, y, x1 - x0, h));
            int textY = y + Math.Max(0, (h - 10) / 2);
            if (View == ChangeView.Net)
            {
                int w = (int)Math.Round(Math.Abs(row.Net) / max * (x1 - x0));
                if (w > 0) context.FillRectangle(row.Net >= 0 ? AddedColor : PulledColor, new Rectangle(x1 - w, y, w, h));
                if (ShowValues && h >= 9) context.DrawString(BookChartTools.Compact(row.Net), font, dim, x0 + 2, textY);
                continue;
            }
            int added = (int)Math.Round(row.Added / max * column);
            if (added > 0) context.FillRectangle(AddedColor, new Rectangle(x0 + column - added, y, added, h));
            int pulled = (int)Math.Round(row.Pulled / max * column);
            if (pulled > 0) context.FillRectangle(PulledColor, new Rectangle(x0 + column, y, pulled, h));
            if (ShowFilled)
            {
                int filled = (int)Math.Round(row.Filled / max * column);
                if (filled > 0) context.FillRectangle(FilledColor, new Rectangle(x0 + column + pulled, y, filled, h));
            }
            if (ShowValues && h >= 9)
            {
                context.DrawString("+" + BookChartTools.Compact(row.Added), font, dim, x0 + 2, textY);
                string removed = "−" + BookChartTools.Compact(row.Pulled + row.Filled);
                context.DrawString(removed, font, dim, x1 - context.MeasureString(removed, font).Width - 2, textY);
            }
        }
        if (View == ChangeView.Both) context.DrawLine(new RenderPen(Color.FromArgb(90, 200, 200, 200), 1), x0 + column, region.Top + 30, x0 + column, region.Bottom);

        string title = UiText.Tr($"Book changes · {WindowText()}");
        string legend = UiText.Tr(View == ChangeView.Net ? "green: more added · red: more removed" : "green added · red pulled · amber filled (heuristic)");
        var titleSize = context.MeasureString(title, font);
        var legendSize = context.MeasureString(legend, font);
        int box = Math.Max(titleSize.Width, legendSize.Width) + 8;
        context.FillRectangle(Color.FromArgb(170, 12, 14, 18), new Rectangle(x1 - box, region.Top + 2, box, 27));
        context.DrawString(title, font, dim, x1 - titleSize.Width - 4, region.Top + 4);
        context.DrawString(legend, font, dim, x1 - legendSize.Width - 4, region.Top + 16);
    }
}

/// <summary>
/// Large resting levels drawn where they rested, from recorded received snapshots. Levels pulled quickly without
/// matching trades are marked S (spoofing heuristic) and levels that reappeared nearby in the direction of the price
/// are marked C (chasing heuristic). Marks describe snapshot behaviour of a price level, never intent or one order.
/// </summary>
[DisplayName("Large book orders (pull / chase heuristics)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Large recorded resting levels: when they appeared and how long they rested. Marks quick pulls without matching trades (S, spoofing heuristic) and levels moving with the price (C, chasing heuristic); fates crossing gaps, stale intervals or leaving the visible book stay unjudged. Needs explicit depth recording.")]
public sealed class LargeBookOrders : Indicator
{
    private List<LargeOrderTrack> _tracks = new();
    private (int First, int Last, int Samples, long LastOrdinal, decimal Min) _key;
    private decimal _autoMin;

    public LargeBookOrders() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Minimum size (0 = automatic)", GroupName = "Calculation", Order = 10)]
    [Range(0, 1_000_000_000)]
    public decimal MinSize { get; set; }

    [Display(Name = "Automatic: multiple of the median level size", GroupName = "Calculation", Order = 15)]
    [Range(2, 200)]
    public int AutoMultiple { get; set; } = 8;

    [Display(Name = "Quick pull: removed within (seconds)", GroupName = "Calculation", Order = 20)]
    [Range(1, 3600)]
    public int QuickPullSeconds { get; set; } = 60;

    [Display(Name = "Chasing: maximum distance (ticks)", GroupName = "Calculation", Order = 25)]
    [Range(1, 10000)]
    public int ChaseTicks { get; set; } = 50;

    [Display(Name = "Stale timeout (seconds)", GroupName = "Calculation", Order = 26)]
    [Range(1, 300)]
    public int MaxAgeSeconds { get; set; } = 10;

    [Display(Name = "Show traded levels", GroupName = "Display", Order = 30)]
    public bool ShowTraded { get; set; } = true;

    [Display(Name = "Show unobserved fates", GroupName = "Display", Order = 31)]
    public bool ShowUnobserved { get; set; } = true;

    [Display(Name = "Line width", GroupName = "Display", Order = 35)]
    [Range(1, 8)]
    public int LineWidth { get; set; } = 2;

    [Display(Name = "Bid color", GroupName = "Colors", Order = 40)]
    public Color BidColor { get; set; } = Color.FromArgb(220, 61, 139, 253);

    [Display(Name = "Ask color", GroupName = "Colors", Order = 41)]
    public Color AskColor { get; set; } = Color.FromArgb(220, 240, 82, 90);

    [Display(Name = "Quick pull color", GroupName = "Colors", Order = 42)]
    public Color QuickPullColor { get; set; } = Color.FromArgb(255, 242, 181, 58);

    [Display(Name = "Chasing color", GroupName = "Colors", Order = 43)]
    public Color ChaseColor { get; set; } = Color.FromArgb(255, 180, 120, 255);

    [Browsable(false)] public string Status { get; private set; } = "No recorded depth. Recording must be explicitly enabled; display does not enable it.";

    /// <summary>Tracked levels for the visible bars (for tests and the legend).</summary>
    [Browsable(false)] public IReadOnlyList<LargeOrderTrack> Tracks => _tracks;

    protected override void OnRecalculate()
    {
        _tracks = new();
        _key = default;
    }

    protected override void OnDepthSnapshot(DepthUpdate update) => RedrawChart();
    protected override void OnDepthHistoryChanged() => RedrawChart();

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    /// <summary>Re-tracks the visible bars when their recorded samples or the threshold changed.</summary>
    public void Update()
    {
        if (CurrentBar <= 0) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        if (last < first) (first, last) = (0, CurrentBar - 1);
        var samples = new List<DepthSample>();
        for (int bar = first; bar <= last; bar++) samples.AddRange(GetDepthHistory(bar).Samples);
        decimal min = MinSize;
        if (min <= 0)
        {
            var latest = samples.LastOrDefault(s => s.IsUsable);
            if (latest != null)
            {
                var sizes = latest.Snapshot.Bids.Concat(latest.Snapshot.Asks).Select(l => l.Size).Where(v => v > 0).OrderBy(v => v).ToList();
                if (sizes.Count > 0) _autoMin = sizes[sizes.Count / 2] * Math.Max(2, AutoMultiple);
            }
            min = _autoMin;
        }
        var key = (first, last, samples.Count, samples.Count > 0 ? samples[^1].Ordinal : -1, min);
        if (key == _key) return;
        _key = key;
        if (min <= 0 || samples.Count == 0)
        {
            _tracks = new();
            Status = samples.Count == 0 ? "No recorded depth in the visible bars. Recording must be explicitly enabled; display does not enable it." : "No usable book to size the automatic threshold.";
            return;
        }
        var trades = FootprintTrades.For(this, first, last, BookChartTools.Tick(this));
        _tracks = BookChanges.TrackLarge(samples, new LargeOrderOptions { MinSize = min, MaxMoveTicks = ChaseTicks, MaxAge = TimeSpan.FromSeconds(Math.Clamp(MaxAgeSeconds, 1, 300)) }, trades);
        var quick = TimeSpan.FromSeconds(QuickPullSeconds);
        Status = $"Large levels ≥ {BookChartTools.Compact(min)} · {_tracks.Count} tracked · S quick pulls {_tracks.Count(t => t.IsQuickPull(quick))} · C moved {_tracks.Count(t => t.Outcome == LargeOrderOutcome.Moved)}"
            + $" · unobserved {_tracks.Count(t => t.Outcome == LargeOrderOutcome.Unobserved)} · removed without trade data {_tracks.Count(t => t.Outcome == LargeOrderOutcome.Removed)} · heuristics, not intent";
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        Update();
        var region = ChartInfo.PriceChartContainer.Region;
        FootprintStudyData.Legend(context, region, 0, Status, _tracks.Count > 0 ? Color.LightGray : Color.Gold);
        decimal tick = BookChartTools.Tick(this);
        var font = new RenderFont("Arial", 7);
        var quick = TimeSpan.FromSeconds(QuickPullSeconds);
        foreach (var track in _tracks)
        {
            if (!ShowTraded && track.Outcome == LargeOrderOutcome.Traded) continue;
            if (!ShowUnobserved && track.Outcome == LargeOrderOutcome.Unobserved) continue;
            bool pull = track.IsQuickPull(quick), chase = track.Outcome == LargeOrderOutcome.Moved;
            int x0 = BookChartTools.XOf(this, track.FirstSeen), x1 = Math.Max(x0 + 3, BookChartTools.XOf(this, track.EndedAt ?? track.LastSeen));
            if (x1 < region.Left || x0 > region.Right) continue;
            int y = ChartInfo.GetYByPrice(track.PriceTicks * tick, false);
            var color = pull ? QuickPullColor : chase ? ChaseColor : track.Side == BookSide.Bid ? BidColor : AskColor;
            var pen = track.Outcome == LargeOrderOutcome.Unobserved
                ? new RenderPen(Color.FromArgb(120, color), 1, DashStyle.Dot)
                : new RenderPen(color, Math.Clamp(LineWidth, 1, 8));
            context.DrawLine(pen, x0, y, x1, y);
            if (pull) context.DrawString("S", font, color, x1 + 2, y - 5);
            if (chase && track.MovedToTicks is { } to)
            {
                context.DrawLine(new RenderPen(color, 1, DashStyle.Dot), x1, y, x1 + 6, ChartInfo.GetYByPrice(to * tick, false));
                context.DrawString("C", font, color, x1 + 2, y - 5);
            }
        }
    }
}
