// Adapted from FlowVwapAnclada.cs by jjuangutierrez/trading-hub at 8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9.
// Original contributors retain copyright. MIT with Commons Clause; see LICENSE.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Anchored Flow VWAP")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Observed-footprint weighted VWAP with optional estimated OHLC. Confirmed swings are retrospective, not real-time pivot predictions.")]
public class AnchoredFlowVwap : Indicator
{
    public enum AnchorMode { Manual, Swing, VolumeSpike }
    public enum SwingKind { Low, High, Both }
    public enum FlowWeight { AbsoluteDelta, Volume, AbsolutePriceNotional }
    private AnchorMode _mode = AnchorMode.Swing;
    private SwingKind _swing = SwingKind.Both;
    private FlowWeight _weight = FlowWeight.AbsoluteDelta;
    private string _manual = "";
    private static readonly string[] ManualFormats = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss'Z'", "yyyy-MM-ddTHH:mm:ss.FFFFFFF'Z'" };
    private DateTime _manualTime; private bool _manualParsed;
    private int _radius = 10, _baseline = 50, _bands = 3;
    private decimal _spike = 3, _sigma = 1;
    private bool _estimate, _allowInferred, _retrospective = true;
    private readonly object _sync = new();
    private readonly List<StudyLevel> _levels = new();
    private readonly List<Row> _causal = new(), _painted = new();
    private readonly List<Row> _paintUndo = new();
    private int _undoStart;
    private readonly OptionalValueDataSeries _vwap = new("AnchoredFlowVwap.Value", "Flow VWAP") { Color = Color.Gold, Width = 2, ShowZeroValue = true };
    private readonly OptionalValueDataSeries[] _upper = new OptionalValueDataSeries[3], _lower = new OptionalValueDataSeries[3];
    private readonly RenderPen[] _linePens = new RenderPen[7];
    private static readonly RenderPen AnchorPen = new(Color.Gold);
    private readonly record struct Row(int Anchor, int Confirmed, WeightedStudyMoments State, StudyCoverage Coverage, decimal Flow);

    public AnchoredFlowVwap() : base(true)
    {
        DataSeries[0] = _vwap;
        for (int i = 0; i < 3; i++)
        {
            _upper[i] = new($"AnchoredFlowVwap.Upper{i + 1}", $"Upper {i + 1} sigma") { Color = Color.DarkGoldenrod };
            _lower[i] = new($"AnchoredFlowVwap.Lower{i + 1}", $"Lower {i + 1} sigma") { Color = Color.DarkGoldenrod };
            DataSeries.Add(_upper[i]); DataSeries.Add(_lower[i]);
        }
        for (int i = 0; i < DataSeries.Count; i++) { var series = (ValueDataSeries)DataSeries[i]; _linePens[i] = new(series.Color, series.Width); }
        UpdateDrawingMode();
        EnableCustomDrawing = true; DrawAbovePrice = true; SubscribeToDrawingEvents(DrawingLayouts.Final);
    }
    [Display(Name = "Anchor mode", GroupName = "Anchor")]
    public AnchorMode Mode { get => _mode; set { _mode = value; RecalculateValues(); } }
    [Display(Name = "Manual UTC (yyyy-MM-dd HH:mm; empty = loaded history start)", GroupName = "Anchor")]
    public string ManualUtc { get => _manual; set { _manual = value?.Trim() ?? ""; _manualParsed = false; RecalculateValues(); } }
    [Display(Name = "Closed bars on each side", GroupName = "Anchor"), Range(1, 200)]
    public int SwingRadius { get => _radius; set { ArgumentOutOfRangeException.ThrowIfLessThan(value, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 200); _radius = value; RecalculateValues(); } }
    [Display(Name = "Swing kind", GroupName = "Anchor")]
    public SwingKind Swing { get => _swing; set { _swing = value; RecalculateValues(); } }
    [Display(Name = "Preceding volume bars", GroupName = "Anchor"), Range(1, 500)]
    public int SpikeLookback { get => _baseline; set { ArgumentOutOfRangeException.ThrowIfLessThan(value, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 500); _baseline = value; RecalculateValues(); } }
    [Display(Name = "Volume spike multiplier", GroupName = "Anchor"), Range(1, 100)]
    public decimal SpikeMultiplier { get => _spike; set { ArgumentOutOfRangeException.ThrowIfLessThan(value, 1m); _spike = value; RecalculateValues(); } }
    [Display(Name = "Weight (notional = absolute price x volume)", GroupName = "Calculation")]
    public FlowWeight Weight { get => _weight; set { _weight = value; RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC / aggregate fallback", GroupName = "Calculation")]
    public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "Allow explicitly inferred aggressors", GroupName = "Calculation")]
    public bool AllowInferredAggressors { get => _allowInferred; set { _allowInferred = value; RecalculateValues(); } }
    [Display(Name = "Sigma bands", GroupName = "Bands"), Range(0, 3)]
    public int Bands { get => _bands; set { ArgumentOutOfRangeException.ThrowIfLessThan(value, 0); ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 3); _bands = value; RecalculateValues(); } }
    [Display(Name = "Sigma multiplier", GroupName = "Bands"), Range(0.01, 20)]
    public decimal SigmaMultiplier { get => _sigma; set { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value); _sigma = value; RecalculateValues(); } }
    [Display(Name = "Fill opacity", GroupName = "Visualization"), Range(0, 80)] public int FillOpacity { get; set; } = 22;
    [Display(Name = "Flow bubbles", GroupName = "Visualization")] public bool ShowFlowBubbles { get; set; } = true;
    [Display(Name = "Maximum bubble size", GroupName = "Visualization"), Range(4, 60)] public int BubbleSize { get; set; } = 14;
    [Display(Name = "Retrospective chart lines (numeric series stay causal)", GroupName = "Visualization")]
    public bool RetrospectiveDrawing { get => _retrospective; set { _retrospective = value; UpdateDrawingMode(); RecalculateValues(); } }
    /// <summary>Drawing-only value; never a strategy series. Available only after RetrospectiveConfirmationAt closes.</summary>
    public decimal? RetrospectiveValueAt(int bar) => bar >= 0 && bar < _painted.Count && _painted[bar].State.Weight > 0 ? _painted[bar].State.Mean : null;
    public int RetrospectiveConfirmationAt(int bar) => bar >= 0 && bar < _painted.Count ? _painted[bar].Confirmed : -1;
    public int CausalAnchorAt(int bar) => bar >= 0 && bar < _causal.Count ? _causal[bar].Anchor : -1;
    private void UpdateDrawingMode()
    {
        foreach (var data in DataSeries) ((ValueDataSeries)data).VisualType = _retrospective ? VisualMode.OnlyValueOnAxis : VisualMode.Line;
    }
    [Browsable(false)] public int AnchorBar => _causal.Count == 0 ? -1 : _causal[^1].Anchor;
    [Browsable(false)] public int ConfirmationBar => _causal.Count == 0 ? -1 : _causal[^1].Confirmed;
    [Browsable(false)] public StudyCoverage Coverage => _causal.Count == 0 ? StudyCoverage.Missing : _causal[^1].Coverage;
    [Browsable(false)] public string Status => $"{FootprintStudyData.Label(Coverage)}; numeric series causal; " + (AnchorBar < 0 ? "waiting for anchor" : Mode == AnchorMode.Swing ? $"drawing pivot {AnchorBar}, confirmed at close {ConfirmationBar}" : Mode == AnchorMode.Manual && ManualUtc.Length == 0 ? "explicit loaded-history start" : $"anchor {AnchorBar}") + (!IsAvailable ? "; unavailable (zero accumulated weight)" : "");
    [Browsable(false)] public bool IsAvailable => _causal.Count > 0 && StudyMath.HasValue(_causal[^1].State, _causal[^1].Coverage);

    protected override void OnRecalculate() { lock (_sync) { _causal.Clear(); _painted.Clear(); _paintUndo.Clear(); } }
    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0) UpdateDrawingMode();
        lock (_sync)
        {
            if (bar < _causal.Count - 1) { _causal.Clear(); _painted.Clear(); _paintUndo.Clear(); Clear(); }
            if (_causal.Count == bar + 1)
            {
                // Undo the retrospective paint made by the old latest-bar confirmation.
                for (int i = 0; i < _paintUndo.Count; i++) _painted[_undoStart + i] = _paintUndo[i];
                _causal.RemoveAt(bar); _painted.RemoveAt(bar);
            }
            for (int i = _causal.Count; i <= bar; i++) Step(i);
        }
    }
    private void Step(int bar)
    {
        _paintUndo.Clear();
        Row previous = bar == 0 ? new(-1, -1, default, StudyCoverage.Missing, 0) : _causal[bar - 1];
        int anchor = Detect(bar, previous.Anchor), confirmed = anchor != previous.Anchor ? bar : previous.Confirmed;
        WeightedStudyMoments moments = anchor == previous.Anchor ? previous.State : default;
        StudyCoverage coverage = anchor == previous.Anchor ? previous.Coverage : StudyCoverage.Missing;
        _undoStart = anchor;
        if (anchor >= 0 && anchor != previous.Anchor)
            for (int i = anchor; i < bar; i++)
            {
                _paintUndo.Add(_painted[i]);
                var retro = Accumulate(i, anchor, confirmed, ref moments, ref coverage);
                _painted[i] = retro;
            }
        Row row = anchor < 0 ? new(-1, -1, default, StudyCoverage.Missing, 0) : Accumulate(bar, anchor, confirmed, ref moments, ref coverage);
        _causal.Add(row); _painted.Add(row); Write(bar, row);
    }
    private Row Accumulate(int bar, int anchor, int confirmed, ref WeightedStudyMoments moments, ref StudyCoverage coverage)
    {
        var candle = GetCandle(bar);
        if (!candle.CanCalculateCausally)
        { coverage |= StudyMath.Coverage(candle) | StudyCoverage.MissingBars; return new(anchor, confirmed, moments, coverage, 0); }
        StudyCoverage current;
        decimal flow = 0;
        if ((candle.HasObservedFootprint || candle.HasAggregatedFootprint) && (_weight != FlowWeight.AbsoluteDelta || !candle.HasInferredSide || AllowInferredAggressors))
        {
            current = FootprintStudyData.Read(candle, false, 1, false, _levels);
            foreach (var row in _levels) { Add(row.Price, row.Volume, row.Bid, row.Ask, ref moments); flow += Math.Abs(row.Ask - row.Bid); }
        }
        else if (_estimate && candle.Volume > 0 && (_weight != FlowWeight.AbsoluteDelta || !candle.HasInferredSide || AllowInferredAggressors))
        {
            current = StudyMath.Coverage(candle) | StudyCoverage.Estimated;
            Add(candle.High / 3 + candle.Low / 3 + candle.Close / 3, candle.Volume, candle.Bid, candle.Ask, ref moments);
            flow = Math.Abs(candle.Delta);
        }
        else current = StudyCoverage.MissingBars;
        coverage |= current;
        return new(anchor, confirmed, moments, coverage, flow);
    }
    private void Add(decimal price, decimal volume, decimal bid, decimal ask, ref WeightedStudyMoments moments) => moments.Add(price, _weight switch
    { FlowWeight.AbsoluteDelta => Math.Abs(ask - bid), FlowWeight.Volume => volume, _ => checked(Math.Abs(price) * volume) });
    private int Detect(int bar, int previous)
    {
        if (_mode == AnchorMode.Manual)
        {
            if (_manual.Length == 0) return 0;
            if (!_manualParsed)
            {
                if (!DateTime.TryParseExact(_manual, ManualFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _manualTime))
                    throw new FormatException("Manual anchor must be an explicit UTC date/time (yyyy-MM-dd HH:mm or ISO Z). Empty explicitly means loaded-history start.");
                _manualParsed = true;
            }
            return previous >= 0 ? previous : GetCandle(bar).Time >= _manualTime ? bar : -1;
        }
        if (_mode == AnchorMode.VolumeSpike)
        {
            if (bar < _baseline || !GetCandle(bar).CanCalculateCausally || (StudyMath.Coverage(GetCandle(bar)) & StudyCoverage.Incomplete) != 0) return previous;
            decimal sum = 0;
            for (int i = bar - _baseline; i < bar; i++)
            {
                var source = GetCandle(i);
                if (!source.CanCalculateCausally || (StudyMath.Coverage(source) & StudyCoverage.Incomplete) != 0) return previous;
                sum += source.Volume;
            }
            return sum > 0 && GetCandle(bar).Volume >= sum / _baseline * _spike ? bar : previous;
        }
        int candidate = bar - _radius;
        if (candidate < _radius) return previous;
        var pivot = GetCandle(candidate); bool low = true, high = true;
        for (int i = candidate - _radius; i <= bar; i++)
        {
            var source = GetCandle(i);
            if (!source.IsCandleFinished || !source.CanCalculateCausally || (StudyMath.Coverage(source) & StudyCoverage.Incomplete) != 0) return previous;
            if (i == candidate) continue;
            // Earliest equal-price pivot wins a plateau, independent of replay order.
            var c = GetCandle(i);
            if (c.Low < pivot.Low || i < candidate && c.Low == pivot.Low) low = false;
            if (c.High > pivot.High || i < candidate && c.High == pivot.High) high = false;
        }
        return (_swing != SwingKind.High && low || _swing != SwingKind.Low && high) ? candidate : previous;
    }
    private void Write(int bar, Row row)
    {
        bool available = StudyMath.HasValue(row.State, row.Coverage);
        _vwap.Set(bar, available ? row.State.Mean : 0, available);
        decimal deviation = available ? row.State.Deviation * _sigma : 0;
        for (int i = 0; i < 3; i++)
        {
            decimal width = deviation * (i + 1);
            bool bandAvailable = available && i < _bands;
            _upper[i].Set(bar, bandAvailable ? row.State.Mean + width : 0, bandAvailable);
            _lower[i].Set(bar, bandAvailable ? row.State.Mean - width : 0, bandAvailable);
        }
        UpdateBreak(bar - 1); UpdateBreak(bar);
    }
    private void UpdateBreak(int bar)
    {
        if (bar < 0 || bar >= _causal.Count) return;
        bool broken = bar + 1 < _causal.Count && _causal[bar].Anchor != _causal[bar + 1].Anchor;
        for (int i = 0; i < DataSeries.Count; i++)
        {
            var series = (ValueDataSeries)DataSeries[i];
            if (broken) series.SetPointOfEndLine(bar); else series.RemovePointOfEndLine(bar);
        }
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        lock (_sync)
        {
            FootprintStudyData.Legend(context, ChartInfo.PriceChartContainer.Region, 0, "Flow VWAP: " + Status, Color.Gold);
            var rendering = _retrospective ? _painted : _causal;
            int last = Math.Min(LastVisibleBarNumber, rendering.Count - 1), first = Math.Max(0, FirstVisibleBarNumber);
            for (int s = 0; s < DataSeries.Count; s++)
            {
                var series = (ValueDataSeries)DataSeries[s]; _linePens[s].Color = series.Color; _linePens[s].Width = series.Width;
                _linePens[s].DashStyle = (System.Drawing.Drawing2D.DashStyle)(int)series.LineDashStyle;
            }
            decimal maximum = 0; for (int i = first; i <= last; i++) maximum = Math.Max(maximum, rendering[i].Flow);
            for (int i = first; i <= last; i++)
            {
                var row = rendering[i]; if (row.State.Weight == 0) continue;
                int x = ChartInfo.GetXByBar(i), width = Math.Max(2, ChartInfo.PriceChartContainer.BarsWidth);
                decimal deviation = row.State.Deviation * _sigma;
                if (_retrospective)
                {
                    Row previous = i > 0 ? rendering[i - 1] : default;
                    bool connected = i > 0 && previous.State.Weight > 0 && previous.Anchor == row.Anchor;
                    int from = connected ? ChartInfo.GetXByBar(i - 1) : x - width / 2;
                    for (int s = 0; s < DataSeries.Count; s++)
                    {
                        var series = (ValueDataSeries)DataSeries[s]; if (!series.Visible || s > 0 && (s + 1) / 2 > _bands) continue;
                        decimal multiplier = s == 0 ? 0 : (s % 2 == 1 ? 1 : -1) * ((s + 1) / 2);
                        decimal currentValue = row.State.Mean + deviation * multiplier;
                        decimal priorValue = connected ? previous.State.Mean + previous.State.Deviation * _sigma * multiplier : currentValue;
                        context.DrawLine(_linePens[s], from, ChartInfo.GetYByPrice(priorValue), connected ? x : x + width / 2, ChartInfo.GetYByPrice(currentValue));
                    }
                }
                for (int band = _bands; band >= 1; band--)
                {
                    int top = ChartInfo.GetYByPrice(row.State.Mean + deviation * band), bottom = ChartInfo.GetYByPrice(row.State.Mean - deviation * band);
                    context.FillRectangle(Color.FromArgb(Math.Clamp(FillOpacity, 0, 80), 240, 180, 40), new Rectangle(x - width / 2, Math.Min(top, bottom), width, Math.Max(1, Math.Abs(bottom - top))));
                }
                if (ShowFlowBubbles && row.Flow > 0 && maximum > 0)
                {
                    int size = 3 + (int)(row.Flow / maximum * Math.Max(1, BubbleSize - 3));
                    int y = ChartInfo.GetYByPrice(row.State.Mean);
                    context.FillEllipse(Color.FromArgb(150, 255, 205, 80), new Rectangle(x - width / 2, y - size / 2, width, size));
                    if (_bands > 0)
                    {
                        var candle = GetCandle(i);
                        decimal upper = row.State.Mean + deviation * _bands, lower = row.State.Mean - deviation * _bands;
                        if (candle.High >= upper) context.FillEllipse(Color.FromArgb(150, 255, 205, 80), new Rectangle(x - width / 2, ChartInfo.GetYByPrice(upper) - size / 2, width, size));
                        if (candle.Low <= lower) context.FillEllipse(Color.FromArgb(150, 255, 205, 80), new Rectangle(x - width / 2, ChartInfo.GetYByPrice(lower) - size / 2, width, size));
                    }
                }
            }
            int marker = _retrospective ? AnchorBar : ConfirmationBar;
            if (marker >= first && marker <= last)
            {
                int x = ChartInfo.GetXByBar(marker);
                context.DrawLine(AnchorPen, x, ChartArea.Top, x, ChartArea.Bottom);
            }
        }
    }
}
