using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Perfil de volumen por sesión (o por semana) dibujado sobre las velas, con POC, VAH y VAL.
/// </summary>
[DisplayName("Session volume profile")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class SessionVolumeProfile : Indicator
{
    public enum ProfilePeriod
    {
        [Display(Name = "Session (day)")] Session,
        [Display(Name = "Week")] Week,
        [Display(Name = "All visible history")] Visible,
    }

    public enum ProfileKind
    {
        [Display(Name = "Volume")] Volume,
        [Display(Name = "Delta")] Delta,
        [Display(Name = "Bid / Ask")] BidAsk,
    }

    public enum RowSizing { TickCount, TargetCount }
    public enum ProfileLineStyle { Solid, Dash, Dot, DashDot, DashDotDot }

    private sealed class Profile
    {
        public int StartBar;
        public int EndBar;
        public int CommittedEnd = -1;
        public readonly Dictionary<decimal, (decimal Volume, decimal Bid, decimal Ask)> Committed = new();
        public SortedDictionary<decimal, (decimal Volume, decimal Bid, decimal Ask)> Levels = new();
        public StudyCoverage CommittedCoverage, Coverage;
        public decimal RowStep;
        public decimal Poc;
        public decimal Vah;
        public decimal Val;
        public decimal MaxVolume;
        public decimal Total;
    }

    private readonly List<Profile> _profiles = new();
    private ProfilePeriod _period = ProfilePeriod.Session;
    private int _valueArea = 70;
    private int _last = -1;
    private bool _allowInferred;
    private RowSizing _rowSizing; private int _ticksPerRow = 1, _rowCount = 100;
    private readonly RenderPen _pocPen = new(Color.Gold), _valueAreaPen = new(Color.SteelBlue, 1, System.Drawing.Drawing2D.DashStyle.Dash);

    public SessionVolumeProfile() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Period", GroupName = "Calculation", Order = 10)]
    public ProfilePeriod Period { get => _period; set { _period = value; RecalculateValues(); } }

    [Display(Name = "Value area (%)", GroupName = "Calculation", Order = 20)]
    [Range(10, 100)]
    public int ValueAreaPercent { get => _valueArea; set { _valueArea = Math.Clamp(value, 10, 100); RecalculateValues(); } }

    [Display(Name = "Type", GroupName = "Visualization", Order = 30)]
    public ProfileKind Kind { get; set; } = ProfileKind.Volume;

    [Display(Name = "Maximum width (% of session)", GroupName = "Visualization", Order = 40)]
    [Range(5, 100)]
    public int WidthPercent { get; set; } = 70;

    [Display(Name = "Profile color", GroupName = "Visualization", Order = 50)]
    public Color ProfileColor { get; set; } = Color.FromArgb(70, 110, 140, 190);

    [Display(Name = "Value area color", GroupName = "Visualization", Order = 60)]
    public Color ValueAreaColor { get; set; } = Color.FromArgb(110, 120, 160, 220);

    [Display(Name = "POC color", GroupName = "Visualization", Order = 70)]
    public Color PocColor { get; set; } = Color.FromArgb(255, 255, 196, 0);

    [Display(Name = "Extend POC until touched", GroupName = "Visualization", Order = 80)]
    public bool ExtendPoc { get; set; } = true;

    [Display(Name = "Show VAH / VAL", GroupName = "Visualization", Order = 90)]
    public bool ShowValueAreaLines { get; set; } = true;
    [Display(Name = "Allow explicitly inferred aggressors", GroupName = "Calculation")]
    public bool AllowInferredAggressors { get => _allowInferred; set { _allowInferred = value; RecalculateValues(); } }
    [Display(Name = "Row sizing", GroupName = "Calculation")] public RowSizing Rows { get => _rowSizing; set { _rowSizing = value; RecalculateValues(); } }
    [Display(Name = "Ticks per row", GroupName = "Calculation"), Range(1, 100000)] public int TicksPerRow { get => _ticksPerRow; set { _ticksPerRow = Math.Clamp(value, 1, 100000); RecalculateValues(); } }
    [Display(Name = "Target rows (maximum 4096)", GroupName = "Calculation"), Range(2, 4096)] public int RowCount { get => _rowCount; set { _rowCount = Math.Clamp(value, 2, 4096); RecalculateValues(); } }
    [Display(Name = "POC line width", GroupName = "Visualization"), Range(1, 10)] public int PocLineWidth { get; set; } = 1;
    [Display(Name = "POC line style", GroupName = "Visualization")] public ProfileLineStyle PocLineStyle { get; set; } = ProfileLineStyle.Solid;
    [Display(Name = "Value area line width", GroupName = "Visualization"), Range(1, 10)] public int ValueAreaLineWidth { get; set; } = 1;
    [Display(Name = "Value area line style", GroupName = "Visualization")] public ProfileLineStyle ValueAreaLineStyle { get; set; } = ProfileLineStyle.Dash;
    [Browsable(false)] public decimal LatestRowStep => _profiles.Count == 0 ? 0 : _profiles[^1].RowStep;
    [Browsable(false)] public int LatestRowCount => _profiles.Count == 0 ? 0 : _profiles[^1].Levels.Count;
    [Browsable(false)] public decimal LatestTotal => _profiles.Count == 0 ? 0 : _profiles[^1].Total;
    [Browsable(false)] public decimal LatestPoc => _profiles.Count == 0 ? 0 : _profiles[^1].Poc;
    [Browsable(false)] public decimal LatestValueAreaHigh => _profiles.Count == 0 ? 0 : _profiles[^1].Vah;
    [Browsable(false)] public decimal LatestValueAreaLow => _profiles.Count == 0 ? 0 : _profiles[^1].Val;
    [Browsable(false)] public StudyCoverage Coverage => _profiles.Count == 0 ? 0 : _profiles[^1].Coverage;
    [Browsable(false)] public string Status => FootprintStudyData.Label(Coverage);

    protected override void OnRecalculate() { _profiles.Clear(); _last = -1; }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < _last) { OnRecalculate(); Clear(); }
        int from = bar == _last ? bar : _last + 1;
        for (int b = from; b <= bar; b++) { Step(b); _last = b; }
    }
    private void Step(int bar)
    {
        bool newProfile = bar == 0 || _profiles.Count == 0 || _period switch
        {
            ProfilePeriod.Session => IsNewSession(bar),
            ProfilePeriod.Week => IsNewWeek(bar),
            _ => false,
        };

        if (newProfile && (_profiles.Count == 0 || _profiles[^1].StartBar != bar))
            _profiles.Add(new Profile { StartBar = bar, EndBar = bar, CommittedEnd = bar - 1 });

        var p = _profiles[^1];
        // las barras cerradas se acumulan una sola vez; la barra actual se suma aparte en cada tick
        while (p.CommittedEnd < bar - 1)
        {
            p.CommittedEnd++;
            p.CommittedCoverage |= AddTo(p.Committed, p.CommittedEnd);
        }
        p.EndBar = bar;

        // This draw-only profile needs snapshots at period boundaries and the current bar, not every historical bar.
        bool snapshotNeeded = bar == CurrentBar - 1 || _period switch
        {
            ProfilePeriod.Session => IsNewSession(bar + 1),
            ProfilePeriod.Week => IsNewWeek(bar + 1),
            _ => false,
        };
        if (!snapshotNeeded) return;

        var levels = new Dictionary<decimal, (decimal Volume, decimal Bid, decimal Ask)>(p.Committed);
        p.Coverage = p.CommittedCoverage | AddTo(levels, bar);
        var source = GetCandle(p.StartBar); decimal sourceStep = source.ProfileRowSize, tick = source.MarketTickSize;
        decimal min = decimal.MaxValue, max = decimal.MinValue;
        foreach (decimal price in levels.Keys) { min = Math.Min(min, price); max = Math.Max(max, price); }
        decimal requested = Rows == RowSizing.TargetCount && levels.Count > 1 ? (max - min) / (RowCount - 1) : tick * TicksPerRow;
        p.RowStep = Math.Max(1, decimal.Ceiling(requested / sourceStep)) * sourceStep;
        p.Levels = new();
        foreach (var (price, value) in levels)
        {
            decimal row = FootprintStudyData.Nearest(price, p.RowStep); p.Levels.TryGetValue(row, out var old);
            p.Levels[row] = (old.Volume + value.Volume, old.Bid + value.Bid, old.Ask + value.Ask);
        }
        Finish(p);
    }

    private StudyCoverage AddTo(Dictionary<decimal, (decimal Volume, decimal Bid, decimal Ask)> dict, int bar)
    {
        var candle = GetCandle(bar); bool side = !candle.HasInferredSide || AllowInferredAggressors;
        foreach (var lvl in candle.GetProfilePriceLevels())
        {
            dict.TryGetValue(lvl.Price, out var v);
            dict[lvl.Price] = (v.Volume + lvl.Volume, v.Bid + (side ? lvl.Bid : 0), v.Ask + (side ? lvl.Ask : 0));
        }
        var coverage = StudyMath.Coverage(candle);
        var frame = Period == ProfilePeriod.Week ? StudyTimeFrame.Week : Period == ProfilePeriod.Visible ? StudyTimeFrame.Chart : StudyTimeFrame.Day;
        if (!candle.CanCalculateCausally || !StudyMath.IsContained(candle, frame)) coverage |= StudyCoverage.MissingBars;
        if (!candle.HasObservedFootprint && !candle.HasAggregatedFootprint && candle.Volume != 0) coverage |= StudyCoverage.MissingBars;
        else if ((candle.Quality & TradingHub.Market.DataQuality.Synthetic) != 0 && candle.HasObservedFootprint) coverage |= StudyCoverage.Incomplete;
        return coverage;
    }

    private void Finish(Profile p)
    {
        p.MaxVolume = 0;
        p.Total = 0;
        foreach (var (price, v) in p.Levels)
        {
            var vol = v.Volume;
            p.Total += vol;
            if (vol > p.MaxVolume)
            {
                p.MaxVolume = vol;
                p.Poc = price;
            }
        }

        var prices = p.Levels.Keys.ToArray();
        if (prices.Length == 0) { p.Poc = p.Vah = p.Val = 0; return; }
        var volumes = new decimal[prices.Length]; for (int i = 0; i < prices.Length; i++) volumes[i] = p.Levels[prices[i]].Volume;
        var area = SparseStudyValueArea.Calculate(prices, volumes, Array.BinarySearch(prices, p.Poc), p.RowStep, p.Total * _valueArea / 100);
        p.Vah = area.High; p.Val = area.Low;
    }


    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _profiles.Count == 0) return;
        FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 2, $"Session profile: {Status}", Color.LightSkyBlue);
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var container = ChartInfo.PriceChartContainer;
        var font = FootprintStudyData.TinyFont; _pocPen.Color = PocColor; _valueAreaPen.Color = ValueAreaColor;
        _pocPen.Width = Math.Clamp(PocLineWidth, 1, 10); _pocPen.DashStyle = (System.Drawing.Drawing2D.DashStyle)PocLineStyle;
        _valueAreaPen.Width = Math.Clamp(ValueAreaLineWidth, 1, 10); _valueAreaPen.DashStyle = (System.Drawing.Drawing2D.DashStyle)ValueAreaLineStyle;

        foreach (var p in _profiles)
        {
            int rowH = (int)Math.Round(Math.Clamp(Convert.ToDouble(container.PriceRowHeight) * (double)(p.RowStep / GetCandle(p.StartBar).ProfileRowSize), 1, Math.Max(1, ChartInfo.PriceChartContainer.Region.Height)));
            if (p.EndBar < first || p.StartBar > last || p.MaxVolume <= 0) continue;
            int x0 = ChartInfo.GetXByBar(p.StartBar, true);
            int x1 = ChartInfo.GetXByBar(p.EndBar, true) + container.BarsWidth;
            int maxW = Math.Max(10, (x1 - x0) * WidthPercent / 100);

            foreach (var (price, v) in p.Levels)
            {
                decimal vol = v.Volume;
                int y = ChartInfo.GetYByPrice(price, true);
                bool inVa = price <= p.Vah && price >= p.Val;
                switch (Kind)
                {
                    case ProfileKind.Delta:
                    {
                        decimal d = v.Ask - v.Bid;
                        int w = (int)(maxW * Math.Abs(d) / p.MaxVolume);
                        context.FillRectangle(d >= 0 ? Color.FromArgb(120, 73, 209, 73) : Color.FromArgb(120, 232, 69, 72), new Rectangle(x0, y, Math.Max(1, w), Math.Max(1, rowH - 1)));
                        break;
                    }
                    case ProfileKind.BidAsk:
                    {
                        int wb = (int)(maxW * v.Bid / p.MaxVolume), wa = (int)(maxW * v.Ask / p.MaxVolume);
                        context.FillRectangle(Color.FromArgb(120, 232, 69, 72), new Rectangle(x0, y, Math.Max(1, wb), Math.Max(1, rowH - 1)));
                        context.FillRectangle(Color.FromArgb(120, 73, 209, 73), new Rectangle(x0 + wb, y, Math.Max(1, wa), Math.Max(1, rowH - 1)));
                        break;
                    }
                    default:
                    {
                        int w = (int)(maxW * vol / p.MaxVolume);
                        context.FillRectangle(price == p.Poc ? PocColor : inVa ? ValueAreaColor : ProfileColor, new Rectangle(x0, y, Math.Max(1, w), Math.Max(1, rowH - 1)));
                        break;
                    }
                }
            }

            // POC (se extiende hasta que el precio lo toca en sesiones posteriores)
            int yPoc = ChartInfo.GetYByPrice(p.Poc, false);
            int xEnd = x1;
            if (ExtendPoc)
            {
                xEnd = ChartInfo.GetXByBar(Math.Min(CurrentBar - 1, last), false);
                for (int b = p.EndBar + 1; b <= Math.Min(last, CurrentBar - 1); b++)
                {
                    var c = GetCandle(b);
                    if (c.Low <= p.Poc && c.High >= p.Poc)
                    {
                        xEnd = ChartInfo.GetXByBar(b, false);
                        break;
                    }
                }
            }

            context.DrawLine(_pocPen, x0, yPoc, Math.Max(x1, xEnd), yPoc);
            if (ShowValueAreaLines)
            {
                var pen = _valueAreaPen;
                int yVah = ChartInfo.GetYByPrice(p.Vah, true);
                int yVal = ChartInfo.GetYByPrice(p.Val, true) + rowH;
                context.DrawLine(pen, x0, yVah, x1, yVah);
                context.DrawLine(pen, x0, yVal, x1, yVal);
            }
        }
    }
}
