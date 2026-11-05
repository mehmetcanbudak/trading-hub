using System.Numerics;
using SkiaSharp;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Charting.Rendering;

public readonly record struct DepthHeatmapCell(DateTime From, DateTime To, decimal PriceTicks, BookSide Side, decimal RestingSize, DepthCoverage Coverage);
public readonly record struct DepthHeatmapGap(DateTime From, DateTime To, DepthGapReason Reason);
public sealed record DepthHeatmapFrame(IReadOnlyList<DepthHeatmapCell> Cells, IReadOnlyList<DepthHeatmapGap> Gaps, bool IsTruncated);
public readonly record struct DepthHeatmapSpan(SKRect Rect, BookSide Side, decimal RestingSize, DepthCoverage Coverage);

/// <summary>
/// Reusable, managed-only display projection. Every touched pixel retains the peak resting size
/// and combined coverage for its side. Adjacent equal pixels become horizontal spans, so long
/// intervals do not require work or storage proportional to their width.
/// </summary>
public sealed class DepthHeatmapProjection
{
    private readonly record struct RowEvent(float Y, BookSide Side, float X, float End, decimal Size,
        DepthCoverage Coverage, bool Starts);
    private readonly List<RowEvent> _events = new();
    private readonly List<DepthHeatmapSpan> _spans = new();
    private readonly PriorityQueue<(float End, decimal Size), decimal> _active = new();
    private readonly int[] _coverageCounts = new int[32];

    public IReadOnlyList<DepthHeatmapSpan> Spans => _spans;
    public bool IsTruncated { get; private set; }

    /// <summary>
    /// Replaces the previous projection. Limits bound output spans and input-cell/pixel-row visits;
    /// sorting and sweeping are bounded by those visits. IsTruncated includes source/display limits.
    /// Projectors return display coordinates for time and signed price ticks, respectively.
    /// </summary>
    public void Project(DepthHeatmapFrame frame, SKRect viewport, Func<DateTime, float> projectTime,
        Func<decimal, float> projectTicks, int priceGrouping, int maxSpans = 250_000, int maxWork = 500_000)
    {
        int grouping = Math.Clamp(priceGrouping, 1, 10_000);
        maxSpans = Math.Clamp(maxSpans, 1, 500_000);
        maxWork = Math.Clamp(maxWork, 1, 1_000_000);
        _events.Clear();
        _spans.Clear();
        _active.Clear();
        IsTruncated = frame.IsTruncated;
        int work = 0;
        bool limited = false;
        for (int i = 0; i < frame.Cells.Count; i++)
        {
            if (work >= maxWork) { IsTruncated = true; break; }
            work++;
            var cell = frame.Cells[i];
            if (cell.RestingSize <= 0) continue;
            float left = Math.Max(viewport.Left, projectTime(cell.From));
            float right = Math.Min(viewport.Right, projectTime(cell.To));
            float top = Math.Max(viewport.Top, projectTicks(cell.PriceTicks + grouping));
            float bottom = Math.Min(viewport.Bottom, projectTicks(cell.PriceTicks));
            if (!(right > left) || !(bottom > top)) continue;
            float firstX = MathF.Floor(left), throughX = MathF.Ceiling(right);
            for (float row = MathF.Floor(top), throughRow = MathF.Ceiling(bottom); row < throughRow; row++)
            {
                if (work >= maxWork) { IsTruncated = limited = true; break; }
                work++;
                _events.Add(new(row, cell.Side, firstX, throughX, cell.RestingSize, cell.Coverage, true));
                _events.Add(new(row, cell.Side, throughX, throughX, cell.RestingSize, cell.Coverage, false));
            }
            if (limited) break;
        }
        _events.Sort(static (a, b) =>
        {
            int order = ((byte)a.Side).CompareTo((byte)b.Side);
            if (order == 0) order = a.Y.CompareTo(b.Y);
            return order != 0 ? order : a.X.CompareTo(b.X);
        });
        int index = 0;
        while (index < _events.Count)
        {
            var row = _events[index];
            _active.Clear();
            Array.Clear(_coverageCounts);
            DepthCoverage coverage = DepthCoverage.None;
            while (index < _events.Count && _events[index].Y == row.Y && _events[index].Side == row.Side)
            {
                float x = _events[index].X;
                do
                {
                    var change = _events[index++];
                    if (change.Starts)
                        _active.Enqueue((change.End, change.Size), -change.Size);
                    ChangeCoverage(change.Coverage, change.Starts ? 1 : -1, _coverageCounts, ref coverage);
                }
                while (index < _events.Count && _events[index].Y == row.Y && _events[index].Side == row.Side &&
                    _events[index].X == x);
                while (_active.TryPeek(out var expired, out _) && expired.End <= x) _active.Dequeue();
                if (index >= _events.Count || _events[index].Y != row.Y || _events[index].Side != row.Side ||
                    !_active.TryPeek(out var peak, out _)) continue;
                var rect = new SKRect(Math.Max(viewport.Left, x), Math.Max(viewport.Top, row.Y),
                    Math.Min(viewport.Right, _events[index].X), Math.Min(viewport.Bottom, row.Y + 1));
                if (!(rect.Right > rect.Left) || !(rect.Bottom > rect.Top)) continue;
                if (_spans.Count > 0)
                {
                    var previous = _spans[^1];
                    if (previous.Side == row.Side && previous.RestingSize == peak.Size && previous.Coverage == coverage &&
                        previous.Rect.Top == rect.Top && previous.Rect.Bottom == rect.Bottom && previous.Rect.Right == rect.Left)
                    {
                        _spans[^1] = previous with { Rect = new(previous.Rect.Left, rect.Top, rect.Right, rect.Bottom) };
                        continue;
                    }
                }
                if (_spans.Count >= maxSpans) { IsTruncated = true; return; }
                _spans.Add(new(rect, row.Side, peak.Size, coverage));
            }
        }
    }

    private static void ChangeCoverage(DepthCoverage flags, int change, Span<int> counts, ref DepthCoverage coverage)
    {
        uint bits = (uint)flags;
        while (bits != 0)
        {
            int bit = BitOperations.TrailingZeroCount(bits);
            counts[bit] += change;
            var flag = (DepthCoverage)(1u << bit);
            if (counts[bit] > 0) coverage |= flag;
            else coverage &= ~flag;
            bits &= bits - 1;
        }
    }
}


/// <summary>
/// Resting L2 size, not executed/traded volume. The KVP 50220ba heatmap concept is retained;
/// exact sizes, sparse negative-price grouping and explicit stale/gap intervals replace its
/// sampled-float/max-brightness persistence. Snapshot sizes are never summed across time.
/// </summary>
public sealed class LiquidityHeatmapRenderer
{
    private readonly SKPaint _paint = new() { IsAntialias = false };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly DepthHeatmapProjection _projection = new();
    private readonly record struct FrameKey(DepthHistory History, long Version, DateTime From, DateTime To,
        int Grouping, int MaxAgeSeconds, decimal Minimum, decimal Maximum, int MaxCells);
    private FrameKey _frameKey;
    private DepthHeatmapFrame? _frame;
    private string _caption = "";

    /// <summary>Pure sparse consumer used by rendering and behavioral tests.</summary>
    public static DepthHeatmapFrame BuildFrame(DepthHistoryQuery query, int priceGrouping, TimeSpan maxAge,
        decimal? minimumTicks = null, decimal? maximumTicks = null, int maxCells = 250_000)
    {
        int grouping = Math.Clamp(priceGrouping, 1, 10_000);
        maxCells = Math.Clamp(maxCells, 1, 500_000);
        maxAge = maxAge <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : maxAge;
        var cells = new List<DepthHeatmapCell>();
        var gaps = new List<DepthHeatmapGap>();
        var rows = new Dictionary<(decimal Price, BookSide Side), decimal>();
        DepthSample? active = query.Preceding;
        DateTime cursor = query.From;
        bool truncated = query.IsTruncated;
        foreach (var sample in query.Samples)
        {
            Emit(active, cursor, sample.Time);
            active = sample;
            cursor = sample.Time;
            if (sample.GapBefore != DepthGapReason.None) gaps.Add(new(sample.Time, sample.Time, sample.GapBefore));
            if (truncated && cells.Count >= maxCells)
            {
                gaps.Add(new(sample.Time, query.To, DepthGapReason.Overflow));
                cursor = query.To;
                active = null;
                break;
            }
        }
        Emit(active, cursor, query.To);
        return new(cells, gaps, truncated);

        void Emit(DepthSample? sample, DateTime from, DateTime to)
        {
            if (from < query.From) from = query.From;
            if (to > query.To) to = query.To;
            if (to <= from) return;
            if (sample == null || !sample.IsUsable)
            {
                gaps.Add(new(from, to, sample?.GapBefore ?? DepthGapReason.Missing));
                return;
            }
            var expiry = sample.Time.Ticks > DateTime.MaxValue.Ticks - maxAge.Ticks ? DateTime.MaxValue : sample.Time + maxAge;
            var through = to < expiry ? to : expiry;
            if (through <= from) { gaps.Add(new(from, to, DepthGapReason.Missing)); return; }
            rows.Clear();
            Put(sample.Snapshot.Bids, BookSide.Bid);
            Put(sample.Snapshot.Asks, BookSide.Ask);
            foreach (var (key, size) in rows)
            {
                if (cells.Count >= maxCells) { truncated = true; break; }
                cells.Add(new(from, through, key.Price, key.Side, size, sample.Coverage));
            }
            if (through < to) gaps.Add(new(through, to, DepthGapReason.Missing));
        }

        void Put(BookLevel[] levels, BookSide side)
        {
            foreach (var level in levels)
            {
                if (minimumTicks is { } min && level.PriceTicks < min || maximumTicks is { } max && level.PriceTicks > max) continue;
                decimal row = decimal.Floor((decimal)level.PriceTicks / grouping) * grouping;
                var key = (row, side);
                rows.TryGetValue(key, out decimal size);
                rows[key] = size + level.Size;
            }
        }
    }

    public void Render(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout main)
    {
        if (model.Series.Count == 0 || model.Settings.HeatmapOpacity <= 0 || layout.LastVisible < layout.FirstVisible) return;
        int first = Math.Max(0, layout.FirstVisible), last = Math.Min(model.Series.Count - 1, layout.LastVisible);
        var from = model.GetDepthInterval(first).From;
        var to = model.GetDepthInterval(last).To;
        if (to <= from) return;
        int grouping = Math.Clamp(model.Settings.HeatmapPriceGrouping, 1, 10_000);
        decimal tick = model.Instrument.TickSize;
        if (tick <= 0) return;
        model.EnsureDepthHistory(from, to);
        var frameKey = new FrameKey(model.Data.DepthHistory, model.DepthDataVersion, from, to, grouping,
            Math.Clamp(model.Settings.HeatmapMaxAgeSeconds, 1, 300),
            (decimal)main.Bottom / tick - grouping, (decimal)main.Top / tick + grouping,
            Math.Clamp((int)(main.Rect.Width * Math.Min(main.Rect.Height, 500)), 1000, 250_000));
        if (_frame == null || _frameKey != frameKey)
        {
            var query = model.GetDepthHistory(from, to);
            _frame = BuildFrame(query, grouping, TimeSpan.FromSeconds(frameKey.MaxAgeSeconds), frameKey.Minimum, frameKey.Maximum, frameKey.MaxCells);
            _frameKey = frameKey;
            _caption = "Resting L2 size (peak per display pixel) · not trade flow";
            if (_frame.Gaps.Count > 0) _caption += " · missing/stale intervals shaded";
            if (_frame.IsTruncated) _caption += " · display limit reached";
            if (!string.IsNullOrWhiteSpace(query.Message)) _caption += " · " + query.Message;
        }
        var frame = _frame!;
        _projection.Project(frame, main.Rect, X, price => main.Y(price * tick), grouping);
        decimal peak = 0;
        foreach (var span in _projection.Spans) peak = Math.Max(peak, span.RestingSize);
        double scale = Math.Log(1 + (double)peak);
        byte alpha = (byte)Math.Clamp(model.Settings.HeatmapOpacity * 255 / 100, 0, 255);
        canvas.Save();
        canvas.ClipRect(main.Rect);
        foreach (var gap in frame.Gaps)
        {
            float left = Math.Max(main.Rect.Left, X(gap.From)), right = Math.Min(main.Rect.Right, X(gap.To));
            if (right < left) continue;
            _paint.Color = new SKColor(245, 170, 80, 14);
            canvas.DrawRect(left, main.Rect.Top, Math.Max(1, right - left), main.Rect.Height, _paint);
            _paint.Color = new SKColor(245, 170, 80, 100);
            canvas.DrawLine(left, main.Rect.Top, left, main.Rect.Bottom, _paint);
        }
        if (scale > 0)
            foreach (var pixel in _projection.Spans)
            {
                double intensity = Math.Log(1 + (double)pixel.RestingSize) / scale;
                byte opacity = (byte)(alpha * (0.2 + intensity * 0.8));
                _paint.Color = pixel.Side == BookSide.Bid ? new SKColor(40, (byte)(110 + intensity * 130), 170, opacity)
                    : new SKColor((byte)(110 + intensity * 140), 100, 210, opacity);
                canvas.DrawRect(pixel.Rect, _paint);
                if ((pixel.Coverage & (DepthCoverage.PartialBook | DepthCoverage.Truncated)) != 0)
                {
                    _paint.Color = new SKColor(255, 205, 90, 130);
                    canvas.DrawLine(pixel.Rect.Left, pixel.Rect.Bottom, pixel.Rect.Right, pixel.Rect.Bottom, _paint);
                }
            }
        _text.Color = model.Theme.TextDim;
        string caption = _projection.IsTruncated && !frame.IsTruncated ? _caption + " · display limit reached" : _caption;
        canvas.DrawUiText(caption, main.Rect.Left + 5, main.Rect.Bottom - 5, RenderContext.GetFont(model.Theme.FontFamily, 10, false, false), _text);
        canvas.Restore();

        float X(DateTime time)
        {
            int bar = Math.Clamp(model.Series.FindBar(time), first, last);
            var interval = model.GetDepthInterval(bar);
            double duration = (interval.To - interval.From).TotalSeconds;
            double fraction = duration > 0 ? Math.Clamp((time - interval.From).TotalSeconds / duration, 0, 1) : 0;
            return layout.LeftX(bar) + (float)(fraction * layout.BarWidth);
        }
    }
}
