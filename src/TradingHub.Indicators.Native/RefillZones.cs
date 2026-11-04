// Adapted from ClusterSearchRefill.cs, jjuangutierrez/trading-hub @ 8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9.
// Original contributors retain copyright. MIT with Commons Clause; see LICENSE.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Refill Zones (heuristic)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Heuristic absorption/refill zones from observed footprint or optional estimated OHLC; not proof of book replenishment or icebergs.")]
public class RefillZones : Indicator
{
    public enum ZoneDirection { Buyer, Seller, Neutral }
    public sealed class Zone
    {
        public int Bar { get; internal set; }
        public decimal Low { get; internal set; }
        public decimal High { get; internal set; }
        public decimal Volume { get; internal set; }
        public ZoneDirection Direction { get; internal set; }
        public StudyCoverage Coverage { get; internal set; }
        public int MitigatedBar { get; internal set; } = -1;
        public int ExpiredBar { get; internal set; } = -1;
        // Canonical source text only: translation and measurement stay at the live rendering boundary.
        internal string AnnotationText = "";
    }
    private readonly object _sync = new();
    private readonly List<Zone> _zones = new(), _active = new(), _changed = new();
    private readonly List<StudyLevel> _levels = new();
    private readonly Dictionary<decimal, StudyLevel> _buckets = new();
    private static readonly RenderFont AnnotationFont = new("Arial", 8);
    private readonly Rectangle[] _annotationBounds = new Rectangle[8];
    private readonly RenderPen _zonePen = new(Color.Empty);
    private int _last = -1, _lifetime;
    private decimal _minimum = 50, _group = 10;
    private bool _estimate, _absorption = true, _project = true;
    public RefillZones() : base(true)
    {
        DataSeries[0].IsHidden = true; ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true; DrawAbovePrice = false; SubscribeToDrawingEvents(DrawingLayouts.Final);
    }
    [Display(Name = "Minimum bucket volume", GroupName = "Calculation"), Range(0.000001, 1000000000)]
    public decimal MinimumVolume { get => _minimum; set { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value); _minimum = value; RecalculateValues(); } }
    [Display(Name = "Floor-price bucket size", GroupName = "Calculation"), Range(0.00000001, 100000000)]
    public decimal PriceGrouping { get => _group; set { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value); _group = value; RecalculateValues(); } }
    [Display(Name = "Absorption only (hide neutral)", GroupName = "Calculation")]
    public bool AbsorptionOnly { get => _absorption; set { _absorption = value; RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC (threshold x 0.4, maximum 200 buckets)", GroupName = "Calculation")]
    public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "Project until mitigation", GroupName = "Visualization")]
    public bool ProjectZones { get => _project; set { _project = value; RecalculateValues(); } }
    [Display(Name = "Keep mitigated zones", GroupName = "Visualization")] public bool ShowMitigated { get; set; } = true;
    [Display(Name = "Maximum lifetime (bars; 0 = unlimited)", GroupName = "Visualization"), Range(0, 1000000)]
    public int MaximumLifetime { get => _lifetime; set { ArgumentOutOfRangeException.ThrowIfNegative(value); _lifetime = value; RecalculateValues(); } }
    [Display(Name = "Buyer color", GroupName = "Visualization")] public Color BuyerColor { get; set; } = Color.FromArgb(46, 204, 113);
    [Display(Name = "Seller color", GroupName = "Visualization")] public Color SellerColor { get; set; } = Color.Crimson;
    [Display(Name = "Neutral color", GroupName = "Visualization")] public Color NeutralColor { get; set; } = Color.Gold;
    [Browsable(false)] public IReadOnlyList<Zone> Zones => _zones;
    [Browsable(false)] public StudyCoverage Coverage { get; private set; }
    [Browsable(false)] public string Status => FootprintStudyData.Label(Coverage) + "; heuristic, not book replenishment / iceberg proof";
    protected override void OnRecalculate() { lock (_sync) Reset(); }
    private void Reset() { _zones.Clear(); _active.Clear(); _changed.Clear(); _last = -1; Coverage = StudyCoverage.Missing; }
    protected override void OnCalculate(int bar, decimal value)
    {
        lock (_sync)
        {
            if (bar < _last) Reset();
            if (bar == _last)
            {
                // Restore every mitigation/expiry changed by the replaced candle, not just its newly created zones.
                for (int i = _zones.Count - 1; i >= 0 && _zones[i].Bar == bar; i--) { _active.Remove(_zones[i]); _zones.RemoveAt(i); }
                foreach (var zone in _changed) { zone.MitigatedBar = zone.ExpiredBar = -1; _active.Add(zone); }
                _last--;
            }
            for (int i = _last + 1; i <= bar; i++) Step(i);
        }
    }
    private void Step(int bar)
    {
        _changed.Clear();
        var candle = GetCandle(bar);
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var zone = _active[i];
            bool expired = _lifetime > 0 && bar > zone.Bar + _lifetime;
            bool touched = !expired && (zone.Direction switch
            {
                ZoneDirection.Buyer => candle.Low <= zone.High,
                ZoneDirection.Seller => candle.High >= zone.Low,
                _ => candle.High >= zone.Low && candle.Low <= zone.High
            });
            if (!expired && !touched) continue;
            if (expired) zone.ExpiredBar = zone.Bar + _lifetime; else zone.MitigatedBar = bar;
            _changed.Add(zone); _active.RemoveAt(i);
        }
        Coverage = FootprintStudyData.Read(candle, _estimate, _group, true, _levels);
        _buckets.Clear();
        foreach (var row in _levels)
        {
            decimal price = row.Width > 0 ? row.Price : decimal.Floor(row.Price / _group) * _group;
            _buckets.TryGetValue(price, out var aggregate);
            _buckets[price] = new(price, aggregate.Volume + row.Volume, aggregate.Bid + row.Bid, aggregate.Ask + row.Ask, row.Width > 0 ? row.Width : _group);
        }
        bool estimated = (Coverage & StudyCoverage.Estimated) != 0;
        foreach (var (_, bucket) in _buckets)
        {
            if (bucket.Volume <= 0 || bucket.Volume < _minimum * (estimated ? 0.4m : 1)) continue;
            decimal low = bucket.Price, high = low + bucket.Width, mid = low / 2 + high / 2, range = candle.High - candle.Low;
            ZoneDirection direction = ZoneDirection.Neutral;
            if (bucket.Bid > bucket.Ask && bucket.Bid >= bucket.Volume * 0.52m && low <= candle.Low + range * 0.35m && candle.Close > mid) direction = ZoneDirection.Buyer;
            else if (bucket.Ask > bucket.Bid && bucket.Ask >= bucket.Volume * 0.52m && high >= candle.High - range * 0.35m && candle.Close < mid) direction = ZoneDirection.Seller;
            if (_absorption && direction == ZoneDirection.Neutral) continue;
            var zone = new Zone { Bar = bar, Low = low, High = high, Volume = bucket.Volume, Direction = direction, Coverage = Coverage };
            zone.AnnotationText = $"{direction} {FootprintStudyData.Label(Coverage)}";
            _zones.Add(zone); if (_project) _active.Add(zone);
        }
        _last = bar;
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        lock (_sync)
        {
            var plot = ChartInfo.PriceChartContainer.Region;
            int observedVisible = 0, estimatedVisible = 0, incompleteVisible = 0;
            int first = FirstVisibleBarNumber, last = Math.Min(LastVisibleBarNumber, _last);
            foreach (var zone in _zones)
            {
                if (zone.Bar > last || zone.MitigatedBar >= 0 && !ShowMitigated) continue;
                int end = !_project ? zone.Bar : zone.MitigatedBar >= 0 ? zone.MitigatedBar : zone.ExpiredBar >= 0 ? zone.ExpiredBar : _last;
                if (_lifetime > 0) end = Math.Min(end, zone.Bar + _lifetime);
                if (end < first) continue;
                end = Math.Min(end, last);
                Color color = zone.Direction switch { ZoneDirection.Buyer => BuyerColor, ZoneDirection.Seller => SellerColor, _ => NeutralColor };
                int alpha = zone.MitigatedBar >= 0 ? 60 : 150;
                int x = ChartInfo.GetXByBar(Math.Max(first, zone.Bar)), xEnd = ChartInfo.GetXByBar(end) + 3;
                int y = ChartInfo.GetYByPrice(zone.High), bottom = ChartInfo.GetYByPrice(zone.Low);
                context.FillRectangle(Color.FromArgb(alpha / 3, color), new Rectangle(x, Math.Min(y, bottom), Math.Max(3, xEnd - x), Math.Max(3, Math.Abs(bottom - y))));
                int middle = ChartInfo.GetYByPrice(zone.Low / 2 + zone.High / 2);
                _zonePen.Color = Color.FromArgb(alpha, color); _zonePen.Width = zone.MitigatedBar >= 0 ? 1 : 2;
                context.DrawLine(_zonePen, x, middle, xEnd, middle);
                if (x <= plot.Right && xEnd >= plot.Left && Math.Max(y, bottom) >= plot.Top && Math.Min(y, bottom) <= plot.Bottom)
                {
                    if ((zone.Coverage & StudyCoverage.Observed) != 0) observedVisible++;
                    if ((zone.Coverage & StudyCoverage.Estimated) != 0) estimatedVisible++;
                    if ((zone.Coverage & StudyCoverage.Incomplete) != 0) incompleteVisible++;
                }
            }
            DrawAnnotations(context, first, last, plot);
            FootprintStudyData.Legend(context, plot, 1,
                $"Refill: {Status}; visible zones: observed {observedVisible}, estimated {estimatedVisible}, incomplete {incompleteVisible}; latest labels: at most 8",
                Color.LightGreen);
        }
    }
    private void DrawAnnotations(RenderContext context, int first, int last, Rectangle plot)
    {
        // Reserve the native six-study legend/header and the three shared footer rows.
        var viewport = Rectangle.FromLTRB(plot.Left + 6, plot.Top + Math.Min(160, plot.Height / 3),
            plot.Right - 6, plot.Bottom - 64);
        var font = RenderContext.GetFont(AnnotationFont);
        int height = (int)Math.Ceiling(font.Spacing) + 4;
        if (viewport.Width <= 8 || viewport.Height < height) return;
        int count = 0;
        for (int i = _zones.Count - 1; i >= 0 && count < _annotationBounds.Length; i--)
        {
            var zone = _zones[i];
            if (zone.Bar > last || zone.MitigatedBar >= 0 && !ShowMitigated) continue;
            int end = !_project ? zone.Bar : zone.MitigatedBar >= 0 ? zone.MitigatedBar : zone.ExpiredBar >= 0 ? zone.ExpiredBar : _last;
            if (_lifetime > 0) end = Math.Min(end, zone.Bar + _lifetime);
            if (end < first) continue;
            int middle = ChartInfo.GetYByPrice(zone.Low / 2 + zone.High / 2);
            if (middle < viewport.Top || middle > viewport.Bottom) continue;
            int width = Math.Min(viewport.Width, (int)Math.Ceiling(RenderContext.MeasureWidth(font, zone.AnnotationText)) + 8);
            int x = Math.Clamp(ChartInfo.GetXByBar(Math.Max(first, zone.Bar)) + 3, viewport.Left, viewport.Right - width);
            int y = Math.Clamp(middle - height - 2, viewport.Top, viewport.Bottom - height);
            var bounds = new Rectangle(x, y, width, height);
            var padded = Rectangle.Inflate(bounds, 3, 2);
            bool overlaps = false;
            for (int j = 0; j < count; j++)
                if (padded.IntersectsWith(_annotationBounds[j])) { overlaps = true; break; }
            if (overlaps) continue;
            _annotationBounds[count++] = bounds;
            Color color = zone.Direction switch { ZoneDirection.Buyer => BuyerColor, ZoneDirection.Seller => SellerColor, _ => NeutralColor };
            context.FillRectangle(Color.FromArgb(225, 20, 24, 32), bounds);
            context.Canvas.Save();
            context.Canvas.ClipRect(new SkiaSharp.SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
            context.DrawString(zone.AnnotationText, AnnotationFont, color, bounds.Left + 4, bounds.Top + 2);
            context.Canvas.Restore();
        }
    }
}
