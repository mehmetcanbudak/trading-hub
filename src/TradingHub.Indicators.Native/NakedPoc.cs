// Independent causal projection layer over CompositeVolumeProfile. KVP capability reference:
// gouqizi2214730-source/trading-hub @ 50220ba91613161685f84384825d855cb4dbeb76 (MIT with Commons Clause).
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[DisplayName("Multi-horizon naked POCs")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Session/week/month/year completed-profile POCs until a later closed bar touches them. Strategy levels appear only after the source period completes; touch painting and confirmation availability are separate. Retention is bounded.")]
public class NakedPoc : Indicator
{
    public readonly record struct Level(StudyTimeFrame Period, decimal Price, int SourceStartBar, int SourceEndBar, int AvailableBar, int TouchBar, int TouchAvailableBar, StudyCoverage Coverage, bool CanPublish = true)
    {
        public bool IsUntouchedAt(int bar) => bar >= AvailableBar && (TouchAvailableBar < 0 || bar < TouchAvailableBar);
    }
    private readonly CompositeVolumeProfile[] _profiles = new CompositeVolumeProfile[4];
    private readonly List<Level> _levels = new(); private readonly List<(int Index, Level Value)> _undo = new();
    private readonly IReadOnlyList<Level> _levelView;
    private readonly RenderPen _pocPen = new(Color.Gold);
    private readonly OptionalValueDataSeries[] _above = new OptionalValueDataSeries[4], _below = new OptionalValueDataSeries[4];
    private static readonly StudyTimeFrame[] Periods = { StudyTimeFrame.Day, StudyTimeFrame.Week, StudyTimeFrame.Month, StudyTimeFrame.Year };
    private static readonly Color[] Colors = { Color.Gold, Color.Orange, Color.LimeGreen, Color.IndianRed };
    private int _last = -1, _lastClosed = -1, _savedClosed = -1, _added, _keep = 200; private decimal _group = 1;
    private bool _daily = true, _weekly = true, _monthly = true, _yearly = true, _estimate;
    public NakedPoc() : base(true)
    {
        _levelView = _levels.AsReadOnly();
        var periods = new[] { CompositeVolumeProfile.ProfilePeriod.Session, CompositeVolumeProfile.ProfilePeriod.Week, CompositeVolumeProfile.ProfilePeriod.Month, CompositeVolumeProfile.ProfilePeriod.Year };
        for (int i = 0; i < 4; i++)
        {
            _profiles[i] = new() { Period = periods[i], ComposeProfiles = false, PriceGrouping = 1, ShowUntestedPocs = false, ShowLabels = false, CalculateDisplayProfiles = false }; Add(_profiles[i]);
            _above[i] = new($"NakedPoc.{Periods[i]}.Above", $"{Periods[i]} nearest above") { Color = Colors[i], ShowZeroValue = true, VisualType = VisualMode.Hide };
            _below[i] = new($"NakedPoc.{Periods[i]}.Below", $"{Periods[i]} nearest below") { Color = Colors[i], ShowZeroValue = true, VisualType = VisualMode.Hide };
            if (i == 0) DataSeries[0] = _above[i]; else DataSeries.Add(_above[i]); DataSeries.Add(_below[i]);
        }
        EnableCustomDrawing = true; SubscribeToDrawingEvents(DrawingLayouts.Final);
    }
    [Display(Name = "Session POCs", GroupName = "Periods")] public bool Daily { get => _daily; set { _daily = value; RecalculateValues(); } }
    [Display(Name = "Weekly POCs", GroupName = "Periods")] public bool Weekly { get => _weekly; set { _weekly = value; RecalculateValues(); } }
    [Display(Name = "Monthly POCs", GroupName = "Periods")] public bool Monthly { get => _monthly; set { _monthly = value; RecalculateValues(); } }
    [Display(Name = "Yearly POCs", GroupName = "Periods")] public bool Yearly { get => _yearly; set { _yearly = value; RecalculateValues(); } }
    [Display(Name = "Price grouping", GroupName = "Calculation"), Range(0.00000001, 100000000)] public decimal PriceGrouping { get => _group; set { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value); _group = value; foreach (var p in _profiles) p.PriceGrouping = value; RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC", GroupName = "Calculation")] public bool AllowEstimated { get => _estimate; set { _estimate = value; foreach (var p in _profiles) p.AllowEstimated = value; RecalculateValues(); } }
    [Display(Name = "Maximum retained POCs (all horizons)", GroupName = "Visualization"), Range(4, 1000)] public int MaximumLevels { get => _keep; set { _keep = Math.Clamp(value, 4, 1000); RecalculateValues(); } }
    [Display(Name = "Show touched POCs", GroupName = "Visualization")] public bool ShowTouched { get; set; }
    [Browsable(false)] public IReadOnlyList<Level> Levels => _levelView;
    private bool Enabled(int k) => k switch { 0 => Daily, 1 => Weekly, 2 => Monthly, _ => Yearly };
    protected override void OnRecalculate() { _levels.Clear(); _undo.Clear(); _last = _lastClosed = _savedClosed = -1; _added = 0; }
    protected override void OnDispose() { foreach (var profile in _profiles) profile.Dispose(); base.OnDispose(); }
    protected override void OnCalculate(int bar, decimal value)
    {
        int target = bar;
        if (bar < _last) { OnRecalculate(); foreach (var p in _profiles) p.ResetForProjectionOwner(); Clear(); }
        if (bar == _last)
        {
            if (_added > 0) _levels.RemoveRange(_levels.Count - _added, _added);
            for (int i = _undo.Count - 1; i >= 0; i--) _levels[_undo[i].Index] = _undo[i].Value;
            _lastClosed = _savedClosed; _last--;
        }
        for (int b = _last + 1; b <= target; b++)
        {
            _undo.Clear(); _added = 0; _savedClosed = _lastClosed;
            if (_levels.Count > MaximumLevels) _levels.RemoveRange(0, _levels.Count - MaximumLevels);
            var c = GetCandle(b); int closed = c.IsCandleFinished ? b : b - 1;
            for (int k = 0; k < 4; k++)
            {
                _profiles[k].Calculate(b, value);
                if (b == 0 || StudyMath.PeriodKey(c, Periods[k]) == StudyMath.PeriodKey(GetCandle(b - 1), Periods[k])) continue;
                var raw = _profiles[k].RawProfiles;
                if (raw.Count >= 2 && raw[^2].Total > 0) { var p = raw[^2]; _levels.Add(new(Periods[k], p.Poc, p.StartBar, p.EndBar, b, -1, -1, p.Coverage, p.IsStrategyAvailable)); _added++; }
            }
            for (int i = 0; i < _levels.Count; i++)
            {
                var level = _levels[i]; if (level.TouchBar >= 0) continue;
                for (int t = Math.Max(level.SourceEndBar + 1, _lastClosed + 1); t <= closed; t++)
                {
                    var touching = GetCandle(t);
                    if (!touching.CanCalculateCausally || (touching.Quality & TradingHub.Market.DataQuality.Partial) != 0)
                    {
                        if ((level.Coverage & StudyCoverage.MissingBars) == 0)
                        {
                            if (i < _levels.Count - _added) _undo.Add((i, level));
                            level = level with { CanPublish = false, Coverage = level.Coverage | StudyCoverage.MissingBars }; _levels[i] = level;
                        }
                        continue;
                    }
                    if (touching.Low > level.Price || touching.High < level.Price) continue;
                    if (i < _levels.Count - _added) _undo.Add((i, level));
                    _levels[i] = level with { TouchBar = t, TouchAvailableBar = b }; break;
                }
            }
            for (int k = 0; k < 4; k++)
            {
                decimal? above = null, below = null;
                foreach (var level in _levels)
                {
                    if (!Enabled(k) || !level.CanPublish || level.Period != Periods[k] || level.TouchBar >= 0) continue;
                    if (level.Price >= c.Close && (above == null || level.Price < above)) above = level.Price;
                    if (level.Price <= c.Close && (below == null || level.Price > below)) below = level.Price;
                }
                _above[k].Set(b, above ?? 0, c.CanCalculateCausally && above != null); _below[k].Set(b, below ?? 0, c.CanCalculateCausally && below != null);
            }
            _lastClosed = closed; _last = b;
        }
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        foreach (var level in _levels)
        {
            bool touched = level.TouchAvailableBar >= 0 && level.TouchAvailableBar <= LastVisibleBarNumber;
            int k = Array.IndexOf(Periods, level.Period); if (!Enabled(k) || touched && !ShowTouched || level.AvailableBar > LastVisibleBarNumber) continue;
            int start = Math.Max(FirstVisibleBarNumber, level.AvailableBar), end = !touched ? LastVisibleBarNumber : Math.Min(LastVisibleBarNumber, level.TouchBar);
            if (end < start) continue;
            int y = ChartInfo.GetYByPrice(level.Price), x = ChartInfo.GetXByBar(start);
            _pocPen.Color = Color.FromArgb(!touched ? 230 : 80, Colors[k]);
            context.DrawLine(_pocPen, x, y, ChartInfo.GetXByBar(end), y);
            context.DrawString($"{level.Period} POC {level.Price} ({FootprintStudyData.Label(level.Coverage)}){(level.CanPublish ? "" : "; drawing only: incomplete source/touch coverage")}", FootprintStudyData.SmallFont, Colors[k], x + 2, y - 12);
        }
    }
}
