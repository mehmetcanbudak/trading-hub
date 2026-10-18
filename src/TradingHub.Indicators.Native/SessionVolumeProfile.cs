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

    private sealed class Profile
    {
        public int StartBar;
        public int EndBar;
        public int CommittedEnd = -1;
        public readonly Dictionary<decimal, (decimal Bid, decimal Ask)> Committed = new();
        public SortedDictionary<decimal, (decimal Bid, decimal Ask)> Levels = new();
        public decimal Poc;
        public decimal Vah;
        public decimal Val;
        public decimal MaxVolume;
        public decimal Total;
    }

    private readonly List<Profile> _profiles = new();
    private ProfilePeriod _period = ProfilePeriod.Session;
    private int _valueArea = 70;

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

    protected override void OnRecalculate() => _profiles.Clear();

    protected override void OnCalculate(int bar, decimal value)
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
            AddTo(p.Committed, p.CommittedEnd);
        }

        var levels = new Dictionary<decimal, (decimal Bid, decimal Ask)>(p.Committed);
        AddTo(levels, bar);
        p.Levels = new SortedDictionary<decimal, (decimal Bid, decimal Ask)>(levels);
        p.EndBar = bar;
        Finish(p);
    }

    private void AddTo(Dictionary<decimal, (decimal Bid, decimal Ask)> dict, int bar)
    {
        foreach (var lvl in GetCandle(bar).GetAllPriceLevels())
        {
            dict.TryGetValue(lvl.Price, out var v);
            dict[lvl.Price] = (v.Bid + lvl.Bid, v.Ask + lvl.Ask);
        }
    }

    private void Finish(Profile p)
    {
        p.MaxVolume = 0;
        p.Total = 0;
        foreach (var (price, v) in p.Levels)
        {
            var vol = v.Bid + v.Ask;
            p.Total += vol;
            if (vol > p.MaxVolume)
            {
                p.MaxVolume = vol;
                p.Poc = price;
            }
        }

        // área de valor expandiendo desde el POC
        var prices = p.Levels.Keys.ToList();
        if (prices.Count == 0) return;
        int pocIdx = prices.IndexOf(p.Poc);
        int up = pocIdx, dn = pocIdx;
        decimal acc = Vol(p, prices[pocIdx]);
        decimal target = p.Total * _valueArea / 100m;
        while (acc < target && (up < prices.Count - 1 || dn > 0))
        {
            decimal vu = up < prices.Count - 1 ? Vol(p, prices[up + 1]) : -1;
            decimal vd = dn > 0 ? Vol(p, prices[dn - 1]) : -1;
            if (vu >= vd) acc += Vol(p, prices[++up]);
            else acc += Vol(p, prices[--dn]);
        }

        p.Vah = prices[up];
        p.Val = prices[dn];
    }

    private static decimal Vol(Profile p, decimal price) => p.Levels.TryGetValue(price, out var v) ? v.Bid + v.Ask : 0;

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _profiles.Count == 0) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var container = ChartInfo.PriceChartContainer;
        int rowH = Math.Max(1, (int)Math.Round(container.PriceRowHeight));
        var font = new RenderFont("Arial", 7);

        foreach (var p in _profiles)
        {
            if (p.EndBar < first || p.StartBar > last || p.MaxVolume <= 0) continue;
            int x0 = ChartInfo.GetXByBar(p.StartBar, true);
            int x1 = ChartInfo.GetXByBar(p.EndBar, true) + container.BarsWidth;
            int maxW = Math.Max(10, (x1 - x0) * WidthPercent / 100);

            foreach (var (price, v) in p.Levels)
            {
                decimal vol = v.Bid + v.Ask;
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
                for (int b = p.EndBar + 1; b < CurrentBar; b++)
                {
                    var c = GetCandle(b);
                    if (c.Low <= p.Poc && c.High >= p.Poc)
                    {
                        xEnd = ChartInfo.GetXByBar(b, false);
                        break;
                    }
                }
            }

            context.DrawLine(new RenderPen(PocColor, 1), x0, yPoc, Math.Max(x1, xEnd), yPoc);
            if (ShowValueAreaLines)
            {
                var pen = new RenderPen(ValueAreaColor, 1, System.Drawing.Drawing2D.DashStyle.Dash);
                int yVah = ChartInfo.GetYByPrice(p.Vah, true);
                int yVal = ChartInfo.GetYByPrice(p.Val, true) + rowH;
                context.DrawLine(pen, x0, yVah, x1, yVah);
                context.DrawLine(pen, x0, yVal, x1, yVal);
            }
        }
    }
}
