// Behavior adapted from the KVP capability reference (commit 3629e91); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Spot versus perpetual flow dominance: each bar's measured delta of the aggregate's spot markets and of its perpetual
/// markets, each as a z-score against its own preceding bars (the bar never scores itself). One bar with the sum colored by
/// whichever leads, or both bars. Unattributed prints never move to either side; a side without prints in a bar is a gap.
/// </summary>
[DisplayName("Spot versus perpetual flow dominance")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Per-bar spot and perpetual delta of an aggregate instrument, each as a z-score against its own history, to see whether spot or derivatives drive a move. Needs an aggregate connection with both spot and perpetual markets; unattributed prints are excluded.")]
public sealed class SpotPerpetualDominance : Indicator
{
    public enum DominanceUnit
    {
        [Display(Name = "Base units (contracts / coins)")] Base,
        [Display(Name = "Notional (quote currency)")] Notional,
    }

    private readonly OptionalValueDataSeries _sum = new("DOM", "Spot + perpetual (z)") { VisualType = VisualMode.Histogram, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _spot = new("SPOT", "Spot (z)") { VisualType = VisualMode.Hide, ShowZeroValue = true, Color = Color.FromArgb(200, 255, 167, 38) };
    private readonly OptionalValueDataSeries _perpetual = new("PERP", "Perpetual (z)") { VisualType = VisualMode.Hide, ShowZeroValue = true, Color = Color.FromArgb(200, 41, 182, 246) };
    private readonly MarketFlowCache _cache = new();
    private readonly List<(Stats Spot, Stats Perpetual)> _prefix = new();
    private readonly RenderFont _font = new("Arial", 8);
    private DominanceUnit _unit = DominanceUnit.Notional;
    private bool _breakdown;

    private readonly record struct Stats(int N, double Mean, double M2)
    {
        public Stats Add(double? value)
        {
            if (value is not { } v) return this;
            int n = N + 1;
            double mean = Mean + (v - Mean) / n;
            return new Stats(n, mean, M2 + (v - Mean) * (v - mean));
        }

        public double? Z(double? value) => value is { } v && N >= StandOutRule.MinimumBaseline && M2 > 0 ? (v - Mean) / Math.Sqrt(M2 / (N - 1)) : null;
    }

    public SpotPerpetualDominance() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _sum;
        DataSeries.Add(_spot);
        DataSeries.Add(_perpetual);
        LineSeries.Add(new LineSeries("Zero", "Zero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Dash });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Unit", GroupName = "Calculation", Order = 10)]
    public DominanceUnit Unit { get => _unit; set { _unit = value; RecalculateValues(); } }

    [Display(Name = "Show both bars (spot and perpetual)", GroupName = "Visualization", Order = 20)]
    public bool Breakdown { get => _breakdown; set { _breakdown = value; RecalculateValues(); } }

    [Display(Name = "Spot leads", GroupName = "Colors", Order = 30)]
    public Color SpotColor { get; set; } = Color.FromArgb(220, 255, 167, 38);

    [Display(Name = "Perpetual leads", GroupName = "Colors", Order = 31)]
    public Color PerpetualColor { get; set; } = Color.FromArgb(220, 41, 182, 246);

    [Browsable(false)]
    public string Status { get; private set; } = "";

    protected override void OnRecalculate()
    {
        var sources = InstrumentInfo?.Sources ?? Array.Empty<InstrumentSource>();
        _cache.Reset(sources);
        _prefix.Clear();
        _sum.VisualType = _breakdown ? VisualMode.Hide : VisualMode.Histogram;
        _spot.VisualType = _perpetual.VisualType = _breakdown ? VisualMode.Histogram : VisualMode.Hide;
        Status = sources.Any(s => s.IsSpot) && sources.Any(s => !s.IsSpot) ? "" : "Needs an aggregate instrument with both spot and perpetual markets.";
    }

    /// <summary>Spot and perpetual delta of a bar; a side is null when none of its markets printed.</summary>
    internal (decimal? Spot, decimal? Perpetual) Deltas(int bar)
    {
        var f = _cache.Get(this, bar, GetBarTrades);
        var sources = _cache.Sources;
        decimal spot = 0, perpetual = 0;
        bool hasSpot = false, hasPerpetual = false;
        for (int i = 0; i < sources.Count; i++)
        {
            decimal gross = f.Gross(i);
            if (gross == 0) continue;
            decimal delta = _unit == DominanceUnit.Notional ? f.Net(i) : f.BuyBase[i] - f.SellBase[i];
            if (sources[i].IsSpot) { spot += delta; hasSpot = true; }
            else { perpetual += delta; hasPerpetual = true; }
        }
        return (hasSpot ? spot : null, hasPerpetual ? perpetual : null);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var (s, p) = Deltas(bar);
        double? spot = (double?)s, perpetual = (double?)p;
        var previous = bar == 0 || bar - 1 >= _prefix.Count ? (new Stats(), new Stats()) : _prefix[bar - 1];
        double? zs = previous.Item1.Z(spot), zp = previous.Item2.Z(perpetual);
        var next = (previous.Item1.Add(spot), previous.Item2.Add(perpetual));
        while (_prefix.Count < bar) _prefix.Add(_prefix.Count > 0 ? _prefix[^1] : (new Stats(), new Stats()));
        if (_prefix.Count == bar) _prefix.Add(next); else _prefix[bar] = next;
        _spot.Set(bar, (decimal)Math.Clamp(zs ?? 0, -50, 50), zs.HasValue);
        _perpetual.Set(bar, (decimal)Math.Clamp(zp ?? 0, -50, 50), zp.HasValue);
        _spot.Colors[bar] = SpotColor;
        _perpetual.Colors[bar] = PerpetualColor;
        bool both = zs.HasValue && zp.HasValue;
        _sum.Set(bar, both ? (decimal)Math.Clamp(zs!.Value + zp!.Value, -100, 100) : 0, both);
        if (both) _sum.Colors[bar] = Math.Abs(zs!.Value) >= Math.Abs(zp!.Value) ? SpotColor : PerpetualColor;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (Container == null || Status.Length == 0) return;
        context.DrawString(Status, _font, FlowFormat.Dim, Container.Region.Left + 8, Container.Region.Top + 18);
    }
}

/// <summary>
/// A decision board that rates the market factor by factor and states which way it leans ("Buyers ahead, 4 to 1"): close
/// against an EMA, RSI, volume against its mean, measured aggressor flow, open-interest change and the long share of
/// observed liquidations, with the net score of the last 64 bars. A factor without data (no measured delta, no open
/// interest, no observed liquidations) is shown as "—" and does not count. A summary, not a recommendation.
/// </summary>
[DisplayName("Decision board")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Rates six factors (close against an EMA, RSI, volume against its mean, measured aggressor flow, open-interest change and long share of observed liquidations) and writes which side they lean to, with the recent net score. Factors without data do not count. A summary, not a recommendation.")]
public sealed class DecisionBoard : Indicator
{
    /// <summary>Per-factor reading and sign (+1 bullish, −1 bearish, 0 neutral, null = no data).</summary>
    public sealed record Board(int Bull, int Bear, string[] Readings, int?[] Signs);

    internal static readonly string[] FactorNames = { "Close against the EMA", "RSI", "Volume against its mean", "Aggressor flow", "Open-interest change", "Liquidations" };
    private readonly ValueDataSeries _ema = new("EMA", "EMA") { Color = Color.FromArgb(255, 255, 167, 38), Width = 2 };
    private readonly DerivativesContext _derivatives;
    private readonly RenderFont _font = new("Arial", 8), _bold = new("Arial", 9, FontStyle.Bold);
    private int _emaLength = 50, _rsiLength = 14, _window = 20;
    private readonly List<(double Up, double Down, double Value)> _rsi = new();

    public DecisionBoard() : base(true)
    {
        DataSeries[0] = _ema;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _derivatives = new DerivativesContext(a => DoActionInGuiThread(a), RedrawChart, openInterest: true, funding: false, liquidations: true);
    }

    [Display(Name = "EMA (bars)", GroupName = "Calculation", Order = 10)] [Range(2, 500)] public int EmaLength { get => _emaLength; set { _emaLength = Math.Clamp(value, 2, 500); RecalculateValues(); } }
    [Display(Name = "RSI (bars)", GroupName = "Calculation", Order = 11)] [Range(2, 100)] public int RsiLength { get => _rsiLength; set { _rsiLength = Math.Clamp(value, 2, 100); RecalculateValues(); } }
    [Display(Name = "Window (bars)", GroupName = "Calculation", Order = 12)] [Range(2, 500)] public int Window { get => _window; set { _window = Math.Clamp(value, 2, 500); RecalculateValues(); } }
    [Display(Name = "RSI bullish above", GroupName = "Rules", Order = 20)] [Range(50, 90)] public decimal RsiBull { get; set; } = 55;
    [Display(Name = "RSI bearish below", GroupName = "Rules", Order = 21)] [Range(10, 50)] public decimal RsiBear { get; set; } = 45;
    [Display(Name = "Volume spike (× mean)", GroupName = "Rules", Order = 22)] [Range(1, 10)] public decimal VolumeSpike { get; set; } = 1.2m;
    [Display(Name = "Flow edge (%)", GroupName = "Rules", Order = 23)] [Range(0, 50)] public decimal FlowEdge { get; set; } = 5;
    [Display(Name = "Minimum open-interest rise (%)", GroupName = "Rules", Order = 24)] [Range(0, 10)] public decimal OpenInterestFlat { get; set; } = 0.05m;
    [Display(Name = "Heavy long liquidations from (%)", GroupName = "Rules", Order = 25)] [Range(50, 100)] public decimal LiquidationHeavy { get; set; } = 65;
    [Display(Name = "Margin to lean (factors)", GroupName = "Rules", Order = 26)] [Range(1, 6)] public int LeanMargin { get; set; } = 2;
    [Display(Name = "Show the board", GroupName = "Visualization", Order = 30)] public bool ShowBoard { get; set; } = true;

    [Browsable(false)]
    public Feeds.Derivatives.DerivativesData? Service { get => _derivatives.Service; set => _derivatives.Service = value; }

    internal DerivativesContext Derivatives => _derivatives;

    protected override void OnRecalculate()
    {
        _rsi.Clear();
        if (CurrentBar > 0)
            _derivatives.Ensure(DerivativesPane.DerivedSymbol(InstrumentInfo?.Instrument ?? "", InstrumentInfo?.BaseAsset ?? "", InstrumentInfo?.QuoteAsset ?? ""), GetCandle(Math.Max(0, CurrentBar - 1000)).Time);
    }

    protected override void OnDispose() => _derivatives.Dispose();

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        decimal k = 2m / (_emaLength + 1);
        _ema[bar] = bar == 0 ? c.Close : _ema[bar - 1] + k * (c.Close - _ema[bar - 1]);
        // Wilder RSI state per bar, so recalculating the forming bar restarts from the previous bar's state.
        while (_rsi.Count <= bar) _rsi.Add(default);
        if (bar == 0) { _rsi[0] = (0, 0, 50); return; }
        var (up, down, _) = _rsi[bar - 1];
        double d = (double)(c.Close - GetCandle(bar - 1).Close), u = Math.Max(d, 0), w = Math.Max(-d, 0);
        if (bar <= _rsiLength) { up += u / _rsiLength; down += w / _rsiLength; }
        else { up = (up * (_rsiLength - 1) + u) / _rsiLength; down = (down * (_rsiLength - 1) + w) / _rsiLength; }
        _rsi[bar] = (up, down, bar < _rsiLength ? 50 : down == 0 ? 100 : 100 - 100 / (1 + up / down));
    }

    private DateTime End(int bar) => bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : GetCandle(bar).Time + (ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero);

    /// <summary>The board for one bar.</summary>
    public Board Evaluate(int bar)
    {
        var readings = new string[6];
        var signs = new int?[6];
        var c = GetCandle(bar);
        int from = Math.Max(0, bar - _window + 1);
        var ci = CultureInfo.InvariantCulture;
        decimal ema = _ema[bar];
        signs[0] = c.Close > ema ? 1 : c.Close < ema ? -1 : 0;
        readings[0] = ema != 0 ? ((c.Close - ema) / ema * 100).ToString("+0.00;-0.00", ci) + " %" : "—";
        double rsi = bar < _rsi.Count ? _rsi[bar].Value : 50;
        signs[1] = bar < _rsiLength ? null : rsi > (double)RsiBull ? 1 : rsi < (double)RsiBear ? -1 : 0;
        readings[1] = signs[1] == null ? "—" : rsi.ToString("0", ci);
        decimal average = 0;
        for (int i = from; i < bar; i++) average += GetCandle(i).Volume;
        average /= Math.Max(1, bar - from);
        decimal ratio = average > 0 ? c.Volume / average : 0;
        signs[2] = bar == from ? null : ratio >= VolumeSpike ? (c.Close >= c.Open ? 1 : -1) : 0;
        readings[2] = signs[2] == null ? "—" : ratio.ToString("0.0", ci) + "×";
        decimal deltaSum = 0, volumeSum = 0;
        bool measured = true;
        for (int i = from; i <= bar && measured; i++)
        {
            measured = StudyMath.TryDelta(GetCandle(i), false, out var delta, out _);
            deltaSum += delta;
            volumeSum += GetCandle(i).Volume;
        }
        if (measured && volumeSum > 0)
        {
            decimal flow = deltaSum / volumeSum * 100;
            signs[3] = flow > FlowEdge ? 1 : flow < -FlowEdge ? -1 : 0;
            readings[3] = flow.ToString("+0;-0;0", ci) + " %";
        }
        else readings[3] = "—";
        var t0 = GetCandle(from).Time - TimeSpan.FromTicks(1);
        var t1 = End(bar);
        var t1Known = (t1 < DateTime.UtcNow ? t1 : DateTime.UtcNow) - TimeSpan.FromTicks(1);
        if (_derivatives.OpenInterestAt(t0) is { } oa && _derivatives.OpenInterestAt(t1Known) is { } ob && oa > 0)
        {
            decimal change = (ob - oa) / oa * 100;
            signs[4] = change > OpenInterestFlat ? (c.Close > GetCandle(from).Close ? 1 : -1) : 0;
            readings[4] = change.ToString("+0.00;-0.00", ci) + " %";
        }
        else readings[4] = "—";
        if (_derivatives.Liquidations(GetCandle(from).Time, t1) is { } q && q.Long + q.Short > 0)
        {
            decimal longShare = q.Long / (q.Long + q.Short) * 100;
            signs[5] = longShare > LiquidationHeavy ? -1 : longShare < 100 - LiquidationHeavy ? 1 : 0;
            string share = longShare.ToString("0", ci);
            readings[5] = UiText.Tr($"{share} % longs");
        }
        else readings[5] = "—";
        return new Board(signs.Count(x => x > 0), signs.Count(x => x < 0), readings, signs);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (!ShowBoard || ChartInfo == null || CurrentBar < Math.Max(_emaLength, _window) + 2) return;
        var board = Evaluate(CurrentBar - 1);
        string head = board.Bull > board.Bear ? UiText.Tr($"Buyers ahead, {board.Bull} to {board.Bear}")
            : board.Bear > board.Bull ? UiText.Tr($"Sellers ahead, {board.Bear} to {board.Bull}")
            : UiText.Tr($"Signals split, {board.Bull} to {board.Bear}");
        int net = board.Bull - board.Bear;
        string lean = UiText.Tr(net >= LeanMargin ? "leans to buying" : -net >= LeanMargin ? "leans to selling" : "no lean");
        var region = ChartInfo.PriceChartContainer.Region;
        int w = 300, rowHeight = 14, x = Math.Max(region.Left + 8, region.Right - w - 8), y = region.Top + 24;
        int height = 26 + rowHeight * 7 + 40;
        context.FillRectangle(Color.FromArgb(215, 18, 20, 26), new Rectangle(x, y, w, height), 5);
        context.DrawString(head, _bold, Color.FromArgb(235, 235, 240), x + 8, y + 6);
        Color Tone(int? sign) => sign > 0 ? FlowFormat.Buy : sign < 0 ? FlowFormat.Sell : FlowFormat.Dim;
        int row = y + 26;
        context.DrawString(UiText.Tr("Net score"), _font, FlowFormat.Dim, x + 8, row);
        context.DrawString($"{net:+0;-0;0} · {lean}", _font, Tone(Math.Sign(net)), x + 170, row);
        for (int k = 0; k < 6; k++)
        {
            row += rowHeight;
            context.DrawString(UiText.Tr(FactorNames[k]), _font, FlowFormat.Dim, x + 8, row);
            context.DrawString(board.Readings[k], _font, Tone(board.Signs[k]), x + 170, row);
        }
        // Net-score sparkline of the last 64 bars.
        int count = Math.Min(64, CurrentBar), top = row + rowHeight + 6, h = 30, mid = top + h / 2;
        context.DrawLine(new RenderPen(Color.FromArgb(70, 140, 140, 150)), x + 6, mid, x + w - 6, mid);
        var points = new Point[count];
        for (int k = 0; k < count; k++)
        {
            var b = Evaluate(CurrentBar - count + k);
            points[k] = new Point(x + 6 + (w - 12) * k / Math.Max(1, count - 1), mid - (b.Bull - b.Bear) * (h / 2 - 2) / 6);
        }
        if (points.Length >= 2) context.DrawLines(new RenderPen(net >= 0 ? FlowFormat.Buy : FlowFormat.Sell, 2), points);
        if (_derivatives.Message is { Length: > 0 } message) context.DrawString(message, _font, FlowFormat.Dim, x + 8, y + height + 2);
    }
}

/// <summary>
/// Measured delta per bar (or a rolling sum) in base units or notional, with ±1, ±2 and ±3 standard-deviation bands of the
/// last N available values. Fixed color or alpha by how many deviations a bar reaches. Unmeasured delta is a gap.
/// </summary>
[DisplayName("Delta with deviation bands")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Measured delta per bar or as a rolling sum, in base units or notional, with ±1/2/3 standard-deviation bands of recent bars. Bars without measured delta stay gaps.")]
public sealed class DeltaDeviationBands : Indicator
{
    public enum DeltaMode
    {
        [Display(Name = "Delta per bar")] PerBar,
        [Display(Name = "Rolling delta")] Rolling,
    }

    public enum Coloring
    {
        [Display(Name = "Dynamic (by deviations)")] Dynamic,
        [Display(Name = "Fixed")] Fixed,
    }

    private readonly OptionalValueDataSeries _delta = new("VD", "Delta") { VisualType = VisualMode.Histogram, ShowZeroValue = true };
    private readonly OptionalValueDataSeries[] _bands = Enumerable.Range(0, 6).Select(k => new OptionalValueDataSeries("B" + k, (k < 3 ? "+" : "−") + (k % 3 + 1) + "σ") { ShowZeroValue = false, Width = 1 }).ToArray();
    private readonly List<decimal?> _raw = new();
    private DeltaMode _mode = DeltaMode.PerBar;
    private int _rolling = 50, _smooth, _lookback = 50;
    private bool _notional, _fill = true, _show = true;

    public DeltaDeviationBands() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _delta;
        foreach (var b in _bands) DataSeries.Add(b);
        LineSeries.Add(new LineSeries("Zero", "Zero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical);
    }

    [Display(Name = "Mode", GroupName = "Calculation", Order = 10)] public DeltaMode Mode { get => _mode; set { _mode = value; RecalculateValues(); } }
    [Display(Name = "Rolling delta bars", GroupName = "Calculation", Order = 11)] [Range(2, 1000)] public int RollingLength { get => _rolling; set { _rolling = Math.Clamp(value, 2, 1000); RecalculateValues(); } }
    [Display(Name = "In notional (quote currency)", GroupName = "Calculation", Order = 12)] public bool Notional { get => _notional; set { _notional = value; RecalculateValues(); } }
    [Display(Name = "Smoothing (bars; 0 = off)", GroupName = "Calculation", Order = 13)] [Range(0, 100)] public int SmoothLength { get => _smooth; set { _smooth = Math.Clamp(value, 0, 100); RecalculateValues(); } }
    [Display(Name = "Bars for the deviations", GroupName = "Calculation", Order = 14)] [Range(5, 1000)] public int DeviationLookback { get => _lookback; set { _lookback = Math.Clamp(value, 5, 1000); RecalculateValues(); } }
    [Display(Name = "Color", GroupName = "Visualization", Order = 20)] public Coloring ColorMode { get; set; } = Coloring.Fixed;
    [Display(Name = "Deviation bands", GroupName = "Visualization", Order = 21)] public bool ShowBands { get => _show; set { _show = value; RecalculateValues(); } }
    [Display(Name = "Filled bands", GroupName = "Visualization", Order = 22)] public bool FillBands { get => _fill; set { _fill = value; RecalculateValues(); } }
    [Display(Name = "Positive", GroupName = "Colors", Order = 30)] public Color PositiveColor { get; set; } = Color.FromArgb(255, 100, 255, 218);
    [Display(Name = "Negative", GroupName = "Colors", Order = 31)] public Color NegativeColor { get; set; } = Color.FromArgb(255, 244, 67, 54);

    protected override void OnRecalculate()
    {
        _raw.Clear();
        foreach (var b in _bands) b.VisualType = _show && !_fill ? VisualMode.Line : VisualMode.Hide;
    }

    /// <summary>Population standard deviation of the known values; null with fewer than two.</summary>
    public static double? Deviation(IEnumerable<decimal> values)
    {
        var list = values.Select(v => (double)v).ToList();
        if (list.Count < 2) return null;
        double mean = list.Average();
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / list.Count);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        while (_raw.Count <= bar) _raw.Add(null);
        _raw[bar] = StudyMath.TryDelta(c, false, out var delta, out _) ? delta * (_notional ? c.Close : 1) : null;
        decimal? v = _raw[bar];
        if (_mode == DeltaMode.Rolling && v.HasValue)
        {
            decimal sum = 0;
            for (int k = Math.Max(0, bar - _rolling + 1); k <= bar && v.HasValue; k++)
                if (_raw[k] is { } r) sum += r; else v = null;
            if (v.HasValue) v = sum;
        }
        if (_smooth > 1 && v.HasValue)
        {
            decimal sum = v.Value;
            int count = 1;
            for (int k = bar - 1; k >= Math.Max(0, bar - _smooth + 1); k--)
                if (_delta.IsAvailable(k)) { sum += _delta[k]; count++; }
            v = sum / count;
        }
        _delta.Set(bar, v ?? 0, v.HasValue);
        var window = new List<decimal>();
        for (int k = Math.Max(0, bar - _lookback + 1); k <= bar; k++) if (_delta.IsAvailable(k)) window.Add(_delta[k]);
        double? sd = Deviation(window);
        for (int k = 0; k < 3; k++)
        {
            _bands[k].Set(bar, sd is { } s ? (decimal)(s * (k + 1)) : 0, sd.HasValue);
            _bands[k + 3].Set(bar, sd is { } s2 ? (decimal)(-s2 * (k + 1)) : 0, sd.HasValue);
            _bands[k].Color = FlowFormat.Alpha(PositiveColor, 120 - k * 30);
            _bands[k + 3].Color = FlowFormat.Alpha(NegativeColor, 120 - k * 30);
        }
        if (v is not { } x) return;
        double z = sd is > 0 ? Math.Abs((double)x) / sd.Value : 0;
        var basec = x >= 0 ? PositiveColor : NegativeColor;
        _delta.Colors[bar] = ColorMode == Coloring.Fixed ? basec : FlowFormat.Alpha(basec, (int)(70 + 185 * Math.Clamp(z / 3, 0, 1)));
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (!_show || !_fill || ChartInfo == null || Container is not { } box) return;
        int first = Math.Max(1, FirstVisibleBarNumber), last = Math.Min(LastVisibleBarNumber, CurrentBar - 1);
        for (int k = 0; k < 3; k++)
            foreach (int sign in new[] { 1, -1 })
            {
                var top = new List<Point>();
                var bottom = new List<Point>();
                for (int i = first; i <= last; i++)
                {
                    var outer = sign > 0 ? _bands[k] : _bands[k + 3];
                    if (!outer.IsAvailable(i)) continue;
                    int x = ChartInfo.GetXByBar(i, false);
                    decimal inner = k == 0 ? 0 : sign > 0 ? _bands[k - 1][i] : _bands[k + 2][i];
                    top.Add(new Point(x, box.GetYByValue(outer[i])));
                    bottom.Add(new Point(x, box.GetYByValue(inner)));
                }
                if (top.Count < 2) continue;
                bottom.Reverse();
                context.FillPolygon(FlowFormat.Alpha(sign > 0 ? PositiveColor : NegativeColor, 22 - k * 5), top.Concat(bottom).ToArray());
            }
    }
}

/// <summary>
/// Volume concentration bubbles: closed bars with unusually high volume (deviations above the preceding bars' mean, three
/// tiers) get bubbles at the prices inside them where most traded, sized by share of the bar's POC and colored by the
/// dominant aggressor side, with the notional. Bars without a footprint get no bubble (no invented price location).
/// </summary>
[DisplayName("Volume concentration bubbles")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Marks high-volume bars (three tiers of deviations above the preceding mean) with bubbles at the prices where most traded inside them, colored by the dominant side, with a count of each tier. Needs a footprint.")]
public sealed class VolumeConcentrationBubbles : Indicator
{
    public readonly record struct Bubble(int Bar, int Tier, decimal Price, decimal Notional, decimal Share, bool Buy);

    private readonly List<Bubble> _bubbles = new();
    private readonly int[] _tiers = new int[3];
    private readonly RenderFont _font = new("Arial", 7, FontStyle.Bold);
    private int _lookback = 50, _maxPerBar = 3;
    private decimal _z1 = 1.5m, _z2 = 2.5m, _z3 = 3.5m, _share = 0.35m, _minimum = 50_000;

    public VolumeConcentrationBubbles() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("VB", "Bubbles") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Preceding bars for the mean", GroupName = "Calculation", Order = 10)] [Range(5, 1000)] public int Lookback { get => _lookback; set { _lookback = Math.Clamp(value, 5, 1000); RecalculateValues(); } }
    [Display(Name = "Tier 1 from (deviations)", GroupName = "Calculation", Order = 11)] [Range(0.5, 20)] public decimal Z1 { get => _z1; set { _z1 = value; RecalculateValues(); } }
    [Display(Name = "Tier 2 from (deviations)", GroupName = "Calculation", Order = 12)] [Range(0.5, 20)] public decimal Z2 { get => _z2; set { _z2 = value; RecalculateValues(); } }
    [Display(Name = "Tier 3 from (deviations)", GroupName = "Calculation", Order = 13)] [Range(0.5, 20)] public decimal Z3 { get => _z3; set { _z3 = value; RecalculateValues(); } }
    [Display(Name = "Price: minimum share of the POC", GroupName = "Calculation", Order = 14)] [Range(0.05, 1)] public decimal BucketShare { get => _share; set { _share = value; RecalculateValues(); } }
    [Display(Name = "Price: minimum notional", GroupName = "Calculation", Order = 15)] [Range(0, 1_000_000_000_000)] public decimal MinimumNotional { get => _minimum; set { _minimum = value; RecalculateValues(); } }
    [Display(Name = "Bubbles per bar", GroupName = "Calculation", Order = 16)] [Range(1, 20)] public int MaxPerBar { get => _maxPerBar; set { _maxPerBar = Math.Clamp(value, 1, 20); RecalculateValues(); } }
    [Display(Name = "Buys", GroupName = "Colors", Order = 30)] public Color BuyColor { get; set; } = FlowFormat.Buy;
    [Display(Name = "Sells", GroupName = "Colors", Order = 31)] public Color SellColor { get; set; } = FlowFormat.Sell;

    [Browsable(false)] public IReadOnlyList<Bubble> Bubbles => _bubbles;

    protected override void OnRecalculate()
    {
        _bubbles.Clear();
        Array.Clear(_tiers);
    }

    /// <summary>Tier (0 = none) of a volume against the preceding volumes' mean and population deviation.</summary>
    public static int Tier(decimal volume, IReadOnlyList<decimal> preceding, decimal z1, decimal z2, decimal z3)
    {
        if (preceding.Count < 2) return 0;
        double mean = preceding.Average(v => (double)v);
        double sd = Math.Sqrt(preceding.Sum(v => ((double)v - mean) * ((double)v - mean)) / preceding.Count);
        if (sd <= 0) return 0;
        double z = ((double)volume - mean) / sd;
        return z >= (double)z3 ? 3 : z >= (double)z2 ? 2 : z >= (double)z1 ? 1 : 0;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        _bubbles.RemoveAll(b => b.Bar >= bar);
        var c = GetCandle(bar);
        if (!c.IsCandleFinished || bar < _lookback || !c.CanCalculateCausally) return;
        var preceding = new List<decimal>(_lookback);
        for (int i = bar - _lookback; i < bar; i++) preceding.Add(GetCandle(i).Volume);
        int tier = Tier(c.Volume, preceding, _z1, _z2, _z3);
        if (tier == 0) return;
        _tiers[tier - 1]++;
        if (!c.HasObservedFootprint && !c.HasAggregatedFootprint) return;
        var levels = c.GetProfilePriceLevels().ToList();
        decimal poc = levels.Count > 0 ? levels.Max(l => l.Volume) : 0;
        if (poc <= 0) return;
        foreach (var level in levels.Where(l => l.Volume / poc >= _share && l.Volume * l.Price >= _minimum).OrderByDescending(l => l.Volume).ThenBy(l => l.Price).Take(_maxPerBar))
            _bubbles.Add(new Bubble(bar, tier, level.Price, level.Volume * level.Price, level.Volume / poc, level.Ask >= level.Bid));
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        foreach (var b in _bubbles)
        {
            if (b.Bar < first || b.Bar > last) continue;
            double scale = b.Tier switch { 3 => 1.6, 2 => 1.3, _ => 1.0 };
            int r = (int)((6 + (double)b.Share * 22) * scale / 2);
            int x = ChartInfo.GetXByBar(b.Bar, false), y = ChartInfo.GetYByPrice(b.Price, false);
            var color = b.Buy ? BuyColor : SellColor;
            context.FillEllipse(FlowFormat.Alpha(color, 140), new Rectangle(x - r, y - r, 2 * r, 2 * r));
            if (b.Share >= 0.99m) context.DrawString(FlowFormat.Compact(b.Notional), _font, Color.White, x + r + 2, y - 5);
        }
        var region = ChartInfo.PriceChartContainer.Region;
        FootprintStudyData.Legend(context, region, 1, UiText.Tr($"Volume tiers 1 / 2 / 3: {_tiers[0]} / {_tiers[1]} / {_tiers[2]}"), FlowFormat.Dim);
    }
}

/// <summary>
/// Footprint imbalance alerts: in each bar's observed footprint, the longest buy-side imbalance stack (marked at its top),
/// the longest sell-side stack (at its bottom) and the largest cell when it exceeds a volume. Each mark is also an
/// alertable series holding its price; the series is unavailable where there is no mark or no footprint.
/// </summary>
[DisplayName("Footprint imbalance alerts (stacks and large cell)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Reads each bar's observed footprint and marks the longest buying imbalance stack (at its top), the longest selling stack (at its bottom) and the largest cell above a volume. Each mark is a series with its price for alerts.")]
public sealed class FootprintImbalanceAlerts : Indicator
{
    public enum ImbalanceMethod
    {
        [Display(Name = "Diagonal")] Diagonal,
        [Display(Name = "Horizontal")] Horizontal,
    }

    private readonly OptionalValueDataSeries _buy = new("SBI", "Buying stack") { VisualType = VisualMode.Hide, ShowZeroValue = false };
    private readonly OptionalValueDataSeries _sell = new("SSI", "Selling stack") { VisualType = VisualMode.Hide, ShowZeroValue = false };
    private readonly OptionalValueDataSeries _big = new("BIG", "Large cell") { VisualType = VisualMode.Hide, ShowZeroValue = false };
    private int _ticksPerRow = 1, _minRun = 3;
    private ImbalanceMethod _method = ImbalanceMethod.Diagonal;
    private decimal _ratio = 3, _minimum, _bigCell;
    private bool _skipZero = true;

    public FootprintImbalanceAlerts() : base(true)
    {
        DataSeries[0] = _buy;
        DataSeries.Add(_sell);
        DataSeries.Add(_big);
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Ticks per row", GroupName = "Calculation", Order = 10)] [Range(1, 1000)] public int TicksPerRow { get => _ticksPerRow; set { _ticksPerRow = Math.Clamp(value, 1, 1000); RecalculateValues(); } }
    [Display(Name = "Method", GroupName = "Calculation", Order = 11)] public ImbalanceMethod Method { get => _method; set { _method = value; RecalculateValues(); } }
    [Display(Name = "Imbalance ratio", GroupName = "Calculation", Order = 12)] [Range(1.1, 50)] public decimal Ratio { get => _ratio; set { _ratio = Math.Max(1.1m, value); RecalculateValues(); } }
    [Display(Name = "Minimum volume (0 = none)", GroupName = "Calculation", Order = 13)] [Range(0, 1_000_000_000)] public decimal MinimumVolume { get => _minimum; set { _minimum = Math.Max(0, value); RecalculateValues(); } }
    [Display(Name = "Consecutive rows for a stack", GroupName = "Calculation", Order = 14)] [Range(2, 20)] public int MinimumRun { get => _minRun; set { _minRun = Math.Clamp(value, 2, 20); RecalculateValues(); } }
    [Display(Name = "Skip empty opposite cells", GroupName = "Calculation", Order = 15)] public bool SkipZero { get => _skipZero; set { _skipZero = value; RecalculateValues(); } }
    [Display(Name = "Large cell from (0 = off)", GroupName = "Calculation", Order = 16)] [Range(0, 1_000_000_000)] public decimal LargeCell { get => _bigCell; set { _bigCell = Math.Max(0, value); RecalculateValues(); } }
    [Display(Name = "Buying stack", GroupName = "Colors", Order = 20)] public Color BuyColor { get; set; } = FlowFormat.Buy;
    [Display(Name = "Selling stack", GroupName = "Colors", Order = 21)] public Color SellColor { get; set; } = FlowFormat.Sell;
    [Display(Name = "Large cell", GroupName = "Colors", Order = 22)] public Color BigColor { get; set; } = FlowFormat.Amber;

    /// <summary>Footprint rows grouped by <paramref name="step"/> (ascending price).</summary>
    public static List<(decimal Price, decimal Bid, decimal Ask)> Rows(IEnumerable<PriceVolumeInfo> levels, decimal step) =>
        levels.GroupBy(l => step > 0 ? Math.Floor(l.Price / step) * step : l.Price)
            .Select(g => (g.Key, g.Sum(x => x.Bid), g.Sum(x => x.Ask))).OrderBy(r => r.Key).ToList();

    /// <summary>Longest consecutive imbalance run on one side: (length, index of its top row).</summary>
    public static (int Length, int End) Longest(IReadOnlyList<(decimal Price, decimal Bid, decimal Ask)> rows, bool buy, ImbalanceMethod method, decimal ratio, decimal minimum, bool skipZero)
    {
        int best = 0, end = -1, run = 0;
        for (int k = 0; k < rows.Count; k++)
        {
            var r = rows[k];
            decimal mine = buy ? r.Ask : r.Bid;
            decimal other = method == ImbalanceMethod.Horizontal ? buy ? r.Bid : r.Ask
                : buy ? k > 0 ? rows[k - 1].Bid : 0 : k + 1 < rows.Count ? rows[k + 1].Ask : 0;
            bool imbalance = (minimum <= 0 || mine >= minimum) && (other == 0 ? !skipZero && mine > 0 : mine >= other * ratio);
            run = imbalance ? run + 1 : 0;
            if (run > best) { best = run; end = k; }
        }
        return (best, end);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        _buy.SetUnavailable(bar);
        _sell.SetUnavailable(bar);
        _big.SetUnavailable(bar);
        var c = GetCandle(bar);
        if (!c.HasObservedFootprint || c.HasInferredSide) return;
        decimal step = BookChartTools.Tick(this) * _ticksPerRow;
        var rows = Rows(c.GetAllPriceLevels(), step);
        if (rows.Count < 2) return;
        var (bl, be) = Longest(rows, true, _method, _ratio, _minimum, _skipZero);
        if (bl >= _minRun) _buy.Set(bar, rows[be].Price + step);
        var (sl, se) = Longest(rows, false, _method, _ratio, _minimum, _skipZero);
        if (sl >= _minRun) _sell.Set(bar, rows[se - sl + 1].Price);
        if (_bigCell > 0)
        {
            var big = rows.OrderByDescending(r => r.Bid + r.Ask).ThenBy(r => r.Price).First();
            if (big.Bid + big.Ask >= _bigCell) _big.Set(bar, big.Price + step / 2);
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        for (int bar = first; bar <= last; bar++)
        {
            int x = ChartInfo.GetXByBar(bar, false);
            if (_buy.IsAvailable(bar)) Triangle(x, ChartInfo.GetYByPrice(_buy[bar], false), true, BuyColor);
            if (_sell.IsAvailable(bar)) Triangle(x, ChartInfo.GetYByPrice(_sell[bar], false), false, SellColor);
            if (_big.IsAvailable(bar)) context.FillRectangle(BigColor, new Rectangle(x - 4, ChartInfo.GetYByPrice(_big[bar], false) - 4, 8, 8));
        }

        void Triangle(int x, int y, bool up, Color color) => context.FillPolygon(color, up
            ? new[] { new Point(x, y - 9), new Point(x - 5, y - 2), new Point(x + 5, y - 2) }
            : new[] { new Point(x, y + 9), new Point(x - 5, y + 2), new Point(x + 5, y + 2) });
    }
}
