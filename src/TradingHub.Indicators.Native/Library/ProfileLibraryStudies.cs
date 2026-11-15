// Independent closed-bar implementations; capability reference gouqizi2214730-source/trading-hub @ 97a8521
// (MIT with Commons Clause). Not a port of its code.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>POC and value area of a <see cref="StudyRowProfile"/> (row prices on the profile grid).</summary>
internal readonly record struct StudyProfileLevels(decimal Poc, decimal ValueAreaHigh, decimal ValueAreaLow, decimal Total, decimal MaxVolume);

/// <summary>
/// Incremental volume-at-price rows keyed by a fixed price grid. Bars are added from footprint rows
/// (<see cref="FootprintStudyData.Read"/>: observed/compact rows; OHLC estimate only when asked). With a window, the oldest
/// bar's exact contribution is removed again, so a rolling profile always equals a profile rebuilt from its last bars.
/// POC ties take the lower price; the value area comes from <see cref="SparseStudyValueArea"/>.
/// </summary>
internal sealed class StudyRowProfile
{
    private readonly SortedDictionary<decimal, decimal> _rows = new();
    private readonly Queue<(decimal Price, decimal Volume)[]> _contributions = new();
    private readonly Queue<StudyCoverage> _coverages = new();
    private readonly int[] _flags = new int[8];
    private readonly List<StudyLevel> _read = new();
    private readonly Dictionary<decimal, decimal> _bar = new();
    private decimal[] _prices = new decimal[64], _volumes = new decimal[64];
    private int _count;
    private bool _dirty = true;
    private decimal _va = -1;
    private StudyProfileLevels? _levels;

    /// <summary>Row height; 0 until <see cref="Reset"/> configures the grid.</summary>
    public decimal Step { get; private set; }
    /// <summary>Bars kept (0 = every added bar).</summary>
    public int Window { get; private set; }
    public int BarCount { get; private set; }
    public decimal Total { get; private set; }

    /// <summary>Union of the per-bar coverage of the bars in the profile; a bar without rows adds <see cref="StudyCoverage.MissingBars"/>.</summary>
    public StudyCoverage Coverage
    {
        get
        {
            int value = 0;
            for (int i = 0; i < _flags.Length; i++) if (_flags[i] > 0) value |= 1 << i;
            return (StudyCoverage)value;
        }
    }

    /// <summary>Row prices (ascending) and volumes as of the last <see cref="Levels"/> call.</summary>
    public ReadOnlySpan<decimal> Prices => _prices.AsSpan(0, _count);
    public ReadOnlySpan<decimal> Volumes => _volumes.AsSpan(0, _count);

    public void Reset(decimal step, int window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        Clear(); Step = step; Window = Math.Max(0, window);
    }

    /// <summary>Removes every bar; <paramref name="grid"/> also forgets the row height.</summary>
    public void Clear(bool grid = false)
    {
        _rows.Clear(); _contributions.Clear(); _coverages.Clear(); Array.Clear(_flags);
        BarCount = 0; Total = 0; _count = 0; _dirty = true; _levels = null;
        if (grid) Step = 0;
    }

    /// <summary>The profile row height for <paramref name="ticksPerRow"/> market ticks, a whole multiple of the chart row.</summary>
    public static decimal RowStep(IndicatorCandle candle, int ticksPerRow)
    {
        decimal unit = candle.ProfileRowSize > 0 ? candle.ProfileRowSize : candle.MarketTickSize > 0 ? candle.MarketTickSize : 0.01m;
        decimal tick = candle.MarketTickSize > 0 ? candle.MarketTickSize : unit;
        return Math.Max(1, decimal.Ceiling(tick * Math.Max(1, ticksPerRow) / unit)) * unit;
    }

    /// <summary>Adds one closed candle's footprint rows; returns its coverage (Missing when it has no usable rows).</summary>
    public StudyCoverage AddBar(IndicatorCandle candle, bool estimate)
    {
        var coverage = FootprintStudyData.Read(candle, estimate, Step, false, _read);
        Add(_read, coverage);
        return coverage;
    }

    public void Add(IReadOnlyList<StudyLevel> rows, StudyCoverage coverage)
    {
        if (Step <= 0) throw new InvalidOperationException("The profile grid is not configured.");
        _bar.Clear();
        foreach (var row in rows)
        {
            if (row.Volume <= 0) continue;
            decimal price = FootprintStudyData.Nearest(row.Price, Step);
            _bar.TryGetValue(price, out var volume); _bar[price] = volume + row.Volume;
        }
        if (Window > 0)
        {
            var contribution = new (decimal Price, decimal Volume)[_bar.Count]; int i = 0;
            foreach (var (price, volume) in _bar) { contribution[i++] = (price, volume); Apply(price, volume); }
            _contributions.Enqueue(contribution); _coverages.Enqueue(coverage);
        }
        else foreach (var (price, volume) in _bar) Apply(price, volume);
        Count(coverage, 1); BarCount++; _dirty = true;
        while (Window > 0 && BarCount > Window)
        {
            foreach (var (price, volume) in _contributions.Dequeue()) Apply(price, -volume);
            Count(_coverages.Dequeue(), -1); BarCount--;
        }
    }

    private void Apply(decimal price, decimal volume)
    {
        _rows.TryGetValue(price, out var old); decimal now = old + volume;
        if (now == 0) _rows.Remove(price); else _rows[price] = now;
        Total += volume;
    }

    private void Count(StudyCoverage coverage, int change)
    {
        int bits = (int)(coverage == StudyCoverage.Missing ? StudyCoverage.MissingBars : coverage);
        for (int i = 0; i < _flags.Length; i++) if ((bits & (1 << i)) != 0) _flags[i] += change;
    }

    /// <summary>POC (lower price on ties) and value area for <paramref name="valueAreaPercent"/>; null when the profile is empty.</summary>
    public StudyProfileLevels? Levels(decimal valueAreaPercent)
    {
        if (!_dirty && _va == valueAreaPercent) return _levels;
        _dirty = false; _va = valueAreaPercent; _count = _rows.Count;
        if (_prices.Length < _count) { int size = Math.Max(_count, _prices.Length * 2); _prices = new decimal[size]; _volumes = new decimal[size]; }
        int i = 0, poc = 0; decimal max = 0;
        foreach (var (price, volume) in _rows)
        {
            _prices[i] = price; _volumes[i] = volume;
            if (volume > max) { max = volume; poc = i; }
            i++;
        }
        if (_count == 0 || Total <= 0) return _levels = null;
        var area = SparseStudyValueArea.Calculate(Prices, Volumes, poc, Step, Total * valueAreaPercent / 100);
        return _levels = new StudyProfileLevels(_prices[poc], area.High, area.Low, Total, max);
    }
}

/// <summary>Clock buckets in a time zone that follow its DST rules.</summary>
internal static class StudyClock
{
    /// <summary>UTC start of the local clock hour containing <paramref name="utc"/> (the repeated autumn hour stays two hours).</summary>
    internal static DateTime HourStartUtc(DateTime utc, TimeZoneInfo zone)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc); var offset = zone.GetUtcOffset(u); var local = u + offset;
        return DateTime.SpecifyKind(local.Date.AddHours(local.Hour) - offset, DateTimeKind.Utc);
    }

    internal static DateTime Local(DateTime utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>UTC instant of a local wall time; skipped times move forward, repeated times take the later instant.</summary>
    internal static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}

// ====================================================================== value area reversion

[DisplayName("Value area reversion")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("A volume profile per exchange session. When a close leaves the value area and, within a few bars, closes back inside it, the reversion is marked: against the previous session's value area (triangles) and against the developing session's value area (circles). The previous value area exists from the first bar after that session completed; the developing value area uses only the session's earlier closed bars. A signal is final at the close that re-enters. Heuristic mean-reversion cue, not a forecast.")]
public class ValueAreaReversion : ClosedBarStudy
{
    private struct Exit { public int Side, Bar; }

    private bool _usePrior = true, _useDeveloping, _estimate, _open, _whole, _colorBars = true;
    private int _maxBars = 10, _minBars = 5, _ticks = 1, _sessionStart, _bulls, _bears;
    private decimal _va = 70;
    private DateOnly _date;
    private StudyProfileLevels? _prior;
    private readonly StudyRowProfile _session = new();
    private readonly Exit[] _exit = new Exit[2];
    private readonly List<StudyLine> _priorLines = new();
    // presentation-only: the signal at each processed bar, so the bar painting can be switched without recalculating
    private readonly List<sbyte> _latch = new();
    private readonly PaintbarsDataSeries _paint = new("ValueAreaReversion.Paint", "Reversion bar colors") { IsHidden = true };
    private readonly OptionalValueDataSeries _priorSignal = new("ValueAreaReversion.Prior", "Reversion vs previous value area (+1 up, -1 down)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _developingSignal = new("ValueAreaReversion.Developing", "Reversion vs developing value area (+1 up, -1 down)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _priorHigh = new("ValueAreaReversion.PriorHigh", "Previous session VAH") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _priorLow = new("ValueAreaReversion.PriorLow", "Previous session VAL") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _priorPoc = new("ValueAreaReversion.PriorPoc", "Previous session POC") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _developingHigh = new("ValueAreaReversion.DevelopingHigh", "Developing VAH") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _developingLow = new("ValueAreaReversion.DevelopingLow", "Developing VAL") { VisualType = VisualMode.Hide, ShowZeroValue = true };

    public ValueAreaReversion()
    {
        DataSeries[0] = _priorSignal;
        DataSeries.Add(_developingSignal); DataSeries.Add(_priorHigh); DataSeries.Add(_priorLow); DataSeries.Add(_priorPoc);
        DataSeries.Add(_developingHigh); DataSeries.Add(_developingLow); DataSeries.Add(_paint);
    }

    [Display(Name = "Signals against the previous session", GroupName = "Signals", Order = 10)]
    public bool PreviousSessionSignals { get => _usePrior; set { _usePrior = value; RecalculateValues(); } }
    [Display(Name = "Signals against the developing session", GroupName = "Signals", Order = 11)]
    public bool DevelopingSessionSignals { get => _useDeveloping; set { _useDeveloping = value; RecalculateValues(); } }
    [Display(Name = "Maximum bars outside before re-entry", GroupName = "Signals", Order = 12), Range(1, 100)]
    public int MaximumBars { get => _maxBars; set { _maxBars = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Minimum session bars for the developing value area", GroupName = "Signals", Order = 13), Range(1, 500)]
    public int MinimumDevelopingBars { get => _minBars; set { _minBars = Math.Clamp(value, 1, 500); RecalculateValues(); } }
    [Display(Name = "Value area (%)", GroupName = "Profile", Order = 20), Range(10, 99)]
    public decimal ValueAreaPercent { get => _va; set { _va = Math.Clamp(value, 10, 99); RecalculateValues(); } }
    [Display(Name = "Ticks per row", GroupName = "Profile", Order = 21), Range(1, 10000)]
    public int TicksPerRow { get => _ticks; set { _ticks = Math.Clamp(value, 1, 10000); RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC fallback", GroupName = "Profile", Order = 22)]
    public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "Paint the reversion bars", GroupName = "Visualization", Order = 30, Description = "Display only: colors the re-entry bars; never changes detection, strategy series or alerts.")]
    public bool ColorBars { get => _colorBars; set { _colorBars = value; Repaint(); RedrawChart(); } }
    [Display(Name = "Bullish", GroupName = "Colors", Order = 40)] public Color BullColor { get; set; } = StudyPalette.Bull;
    [Display(Name = "Bearish", GroupName = "Colors", Order = 41)] public Color BearColor { get; set; } = StudyPalette.Bear;
    [Display(Name = "POC", GroupName = "Colors", Order = 42)] public Color PocColor { get; set; } = StudyPalette.Amber;

    [Browsable(false)] public StudyCoverage Coverage => _session.Coverage;
    [Browsable(false)] public override string Status => FootprintStudyData.Label(Coverage);

    protected override void ResetState()
    {
        _session.Clear(grid: true); _prior = null; _open = _whole = false; _sessionStart = 0; _bulls = _bears = 0;
        _exit[0] = _exit[1] = default;
        if (_latch.Count > Bars.Count) _latch.RemoveRange(Bars.Count, _latch.Count - Bars.Count);
        // a coverage gap ends the previous-session lines at the gap bar (on a full recalculation the scene is already empty)
        int last = Bars.Count - 1;
        foreach (var line in _priorLines) { line.To = Math.Max(line.From, last); line.EndAvailable = Math.Max(line.Available, last); }
        _priorLines.Clear();
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; var candle = Candle(bar);
        if (_session.Step == 0) _session.Reset(StudyRowProfile.RowStep(candle, _ticks), 0);
        if (!_open || b.SessionDate != _date) BeginSession(bar, b, candle);

        int priorSignal = 0, developingSignal = 0;
        if (_prior is { } prior)
        {
            _priorHigh.Set(bar, prior.ValueAreaHigh); _priorLow.Set(bar, prior.ValueAreaLow); _priorPoc.Set(bar, prior.Poc);
            if (_usePrior) priorSignal = Track(0, bar, b, prior);
        }
        else { _priorHigh.SetUnavailable(bar); _priorLow.SetUnavailable(bar); _priorPoc.SetUnavailable(bar); }

        // the developing value area of the bars closed before this one
        var developing = bar - _sessionStart >= _minBars ? _session.Levels(_va) : null;
        if (developing is { } d)
        {
            _developingHigh.Set(bar, d.ValueAreaHigh); _developingLow.Set(bar, d.ValueAreaLow);
            if (_useDeveloping) developingSignal = Track(1, bar, b, d);
        }
        else { _developingHigh.SetUnavailable(bar); _developingLow.SetUnavailable(bar); }

        _session.AddBar(candle, _estimate);
        _priorSignal.Set(bar, priorSignal, _usePrior && _prior != null);
        _developingSignal.Set(bar, developingSignal, _useDeveloping && developing != null);
        Paint(bar, developingSignal != 0 ? developingSignal : priorSignal);
        Dashboard();
    }

    private void BeginSession(int bar, StudyBar b, IndicatorCandle candle)
    {
        // a session's value area is "previous" only once the session is complete and was observed from its start
        _prior = _open && _whole ? _session.Levels(_va) : null;
        foreach (var line in _priorLines) { line.To = bar - 1; line.EndAvailable = bar; }
        _priorLines.Clear();
        if (_prior is { } p)
        {
            _priorLines.Add(Scene.Level(bar, bar, p.ValueAreaHigh, BearColor, "Previous VAH", "Previous session VAH", style: LineDashStyle.Dash));
            _priorLines.Add(Scene.Level(bar, bar, p.ValueAreaLow, BullColor, "Previous VAL", "Previous session VAL", style: LineDashStyle.Dash));
            _priorLines.Add(Scene.Level(bar, bar, p.Poc, PocColor, "Previous POC", "Previous session POC", style: LineDashStyle.Dot));
        }
        _whole = _open || b.Time <= candle.SessionStartUtc(b.SessionDate);
        _session.Clear(); _date = b.SessionDate; _open = true; _sessionStart = bar;
        _exit[0] = _exit[1] = default;
    }

    /// <summary>Exit/re-entry state machine; returns +1 (re-entry from below), -1 (from above) or 0.</summary>
    private int Track(int mode, int bar, StudyBar b, StudyProfileLevels area)
    {
        ref var exit = ref _exit[mode];
        bool above = b.Close > area.ValueAreaHigh, below = b.Close < area.ValueAreaLow;
        if (exit.Side == 0)
        {
            if (above || below) exit = new Exit { Side = above ? 1 : -1, Bar = bar };
            return 0;
        }
        if (bar - exit.Bar > _maxBars)
        {
            // too late: re-entering after the limit is quiet
            if (!above && !below) exit.Side = 0;
            return 0;
        }
        if (above || below)
        {
            if (above != (exit.Side > 0)) exit = new Exit { Side = above ? 1 : -1, Bar = bar }; // went through to the other side
            return 0;
        }
        int signal = -exit.Side; exit.Side = 0;
        bool prior = mode == 0;
        if (signal < 0) { _bears++; Scene.Mark(bar, bar, b.High, prior ? StudyMarkKind.TriangleDown : StudyMarkKind.Circle, BearColor, prior ? "VAH" : null, true); }
        else { _bulls++; Scene.Mark(bar, bar, b.Low, prior ? StudyMarkKind.TriangleUp : StudyMarkKind.Circle, BullColor, prior ? "VAL" : null, false); }
        return signal;
    }

    private void Paint(int bar, int signal)
    {
        while (_latch.Count <= bar) _latch.Add(0);
        _latch[bar] = (sbyte)signal; _paint[bar] = PaintColor(signal);
    }

    private void Repaint() { for (int i = 0; i < _latch.Count; i++) _paint[i] = PaintColor(_latch[i]); }

    private Color? PaintColor(int signal) => _colorBars && signal != 0 ? (signal > 0 ? BullColor : BearColor) : null;

    private void Dashboard()
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Value area reversion";
        Scene.Row("Bullish reversions", _bulls.ToString(CultureInfo.InvariantCulture), BullColor);
        Scene.Row("Bearish reversions", _bears.ToString(CultureInfo.InvariantCulture), BearColor);
        if (_prior is { } p) Scene.Row("Previous value area", $"{Price(p.ValueAreaLow)} – {Price(p.ValueAreaHigh)}");
    }
}

// ====================================================================== profile level alerts

public enum ProfileLevelPeriod
{
    [Display(Name = "Session")] Session,
    [Display(Name = "Week")] Week,
}

public enum ProfileLevelSource
{
    [Display(Name = "Developing period")] Developing,
    [Display(Name = "Previous period")] Previous,
}

[DisplayName("Profile level alerts (POC, VAH, VAL)")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("The POC, VAH and VAL of the exchange session or week volume profile — the developing one (through the last closed bar) or the previous completed one — as alertable levels and step series, with a mark where a close crosses a level that was in force at the previous close. The previous period's levels exist from the first bar after it completed; crosses are final at the crossing close.")]
public class ProfileLevelAlerts : ClosedBarStudy
{
    private ProfileLevelPeriod _period = ProfileLevelPeriod.Session;
    private ProfileLevelSource _source = ProfileLevelSource.Developing;
    private decimal _va = 70; private int _ticks = 1; private bool _estimate, _crosses = true, _open, _whole;
    private long _key; private int _periodStart;
    private StudyProfileLevels? _prior, _shown;
    private readonly StudyRowProfile _profile = new();
    private readonly OptionalValueDataSeries _poc = new("ProfileLevels.Poc", "POC") { Color = StudyPalette.Amber, Width = 2, VisualType = VisualMode.Steps, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _vah = new("ProfileLevels.Vah", "VAH") { Color = StudyPalette.Bear, VisualType = VisualMode.Steps, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _val = new("ProfileLevels.Val", "VAL") { Color = StudyPalette.Bull, VisualType = VisualMode.Steps, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _cross = new("ProfileLevels.Cross", "Level crosses at this close (up minus down)") { VisualType = VisualMode.Hide, ShowZeroValue = true };

    public ProfileLevelAlerts() { DataSeries[0] = _poc; DataSeries.Add(_vah); DataSeries.Add(_val); DataSeries.Add(_cross); }

    [Display(Name = "Period", GroupName = "Calculation", Order = 10)]
    public ProfileLevelPeriod Period { get => _period; set { _period = value; RecalculateValues(); } }
    [Display(Name = "Levels of", GroupName = "Calculation", Order = 11)]
    public ProfileLevelSource LevelsOf { get => _source; set { _source = value; RecalculateValues(); } }
    [Display(Name = "Value area (%)", GroupName = "Calculation", Order = 12), Range(10, 99)]
    public decimal ValueAreaPercent { get => _va; set { _va = Math.Clamp(value, 10, 99); RecalculateValues(); } }
    [Display(Name = "Ticks per row", GroupName = "Calculation", Order = 13), Range(1, 10000)]
    public int TicksPerRow { get => _ticks; set { _ticks = Math.Clamp(value, 1, 10000); RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC fallback", GroupName = "Calculation", Order = 14)]
    public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "Mark the crosses", GroupName = "Visualization", Order = 20, Description = "Display only: hides the cross marks; never changes detection, strategy series or alerts.")]
    public bool ShowCrosses { get => _crosses; set { _crosses = value; RedrawChart(); } }

    [Browsable(false)] public StudyCoverage Coverage => _profile.Coverage;
    [Browsable(false)] public override string Status => FootprintStudyData.Label(Coverage);

    protected override void ResetState() { _profile.Clear(grid: true); _prior = _shown = null; _open = _whole = false; _key = 0; _periodStart = 0; }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; var candle = Candle(bar);
        if (_profile.Step == 0) _profile.Reset(StudyRowProfile.RowStep(candle, _ticks), 0);
        bool week = _period == ProfileLevelPeriod.Week;
        long key = b.Key(week ? StudyCalendar.Week : StudyCalendar.Day);
        if (!_open || key != _key)
        {
            _prior = _open && _whole ? _profile.Levels(_va) : null;
            var start = candle.SessionStartUtc(week ? DateOnly.FromDayNumber((int)key) : b.SessionDate);
            _whole = _open || b.Time <= start;
            _profile.Clear(); _key = key; _open = true; _periodStart = bar; _shown = null;
        }
        var before = _shown; // levels in force at the previous close of this period
        _profile.AddBar(candle, _estimate);
        var now = _source == ProfileLevelSource.Developing ? _profile.Levels(_va) : _prior;
        if (now is { } v) { _poc.Set(bar, v.Poc); _vah.Set(bar, v.ValueAreaHigh); _val.Set(bar, v.ValueAreaLow); }
        else { _poc.SetUnavailable(bar); _vah.SetUnavailable(bar); _val.SetUnavailable(bar); }

        int net = 0;
        if (before is { } l && bar > 0)
        {
            decimal previous = Bars[bar - 1].Close;
            net += Cross(bar, previous, b.Close, l.Poc) + Cross(bar, previous, b.Close, l.ValueAreaHigh) + Cross(bar, previous, b.Close, l.ValueAreaLow);
        }
        _cross.Set(bar, net, before != null);
        _shown = now;
    }

    private int Cross(int bar, decimal previous, decimal close, decimal level)
    {
        int direction = previous <= level && close > level ? 1 : previous >= level && close < level ? -1 : 0;
        if (direction != 0)
            Scene.Mark(bar, bar, level, direction > 0 ? StudyMarkKind.TriangleUp : StudyMarkKind.TriangleDown, direction > 0 ? StudyPalette.Bull : StudyPalette.Bear, null, direction < 0);
        return direction;
    }

    public override bool IsShown(StudyMark mark) => _crosses;

    /// <summary>The levels in force, one stable id per level and period (the developing level moves; its id does not).</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        foreach (var level in base.GetAlertLevels()) yield return level;
        if (_shown is not { } v || _periodStart >= Bars.Count) yield break;
        long t = Bars[_periodStart].Time.Ticks; string source = _source == ProfileLevelSource.Previous ? "previous" : "developing";
        yield return new IndicatorLevel($"ProfileLevels.Poc.{source}@{t}", "Profile POC", v.Poc, _periodStart);
        yield return new IndicatorLevel($"ProfileLevels.Vah.{source}@{t}", "Profile VAH", v.ValueAreaHigh, _periodStart);
        yield return new IndicatorLevel($"ProfileLevels.Val.{source}@{t}", "Profile VAL", v.ValueAreaLow, _periodStart);
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (ChartInfo == null || _shown is not { } v) return;
        var font = FootprintStudyData.SmallFont;
        foreach (var (name, price, color) in new[] { ("POC", v.Poc, _poc.Color), ("VAH", v.ValueAreaHigh, _vah.Color), ("VAL", v.ValueAreaLow, _val.Color) })
        {
            string text = $"{name} {Price(price)}"; var size = ctx.MeasureString(text, font);
            ctx.DrawString(text, font, color, region.Right - size.Width - 6, ChartInfo.GetYByPrice(price, false) - size.Height - 1);
        }
    }
}

// ====================================================================== profile tails

[DisplayName("Profile tails (thin volume zones)")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("From the volume profile of the last N closed bars, the upper and lower tails: the outermost rows whose volume is below a percentage of the POC volume. The upper tail is a supply zone and the lower tail a demand zone, recomputed at every close (series give the zone edges). Heuristic thin-auction zones from observed footprint rows; available once the window is full.")]
public class ProfileTails : ClosedBarStudy
{
    private int _length = 200, _ticks = 1; private decimal _threshold = 10; private bool _estimate;
    private readonly StudyRowProfile _profile = new();
    private readonly OptionalValueDataSeries _supplyLow = new("ProfileTails.SupplyLow", "Supply tail low") { Color = StudyPalette.Bear, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _supplyHigh = new("ProfileTails.SupplyHigh", "Supply tail high") { Color = StudyPalette.Bear, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _demandLow = new("ProfileTails.DemandLow", "Demand tail low") { Color = StudyPalette.Bull, ShowZeroValue = true };
    private readonly OptionalValueDataSeries _demandHigh = new("ProfileTails.DemandHigh", "Demand tail high") { Color = StudyPalette.Bull, ShowZeroValue = true };

    public ProfileTails() { DataSeries[0] = _supplyLow; DataSeries.Add(_supplyHigh); DataSeries.Add(_demandLow); DataSeries.Add(_demandHigh); }

    [Display(Name = "Bars in the profile", GroupName = "Calculation", Order = 10), Range(5, 5000)]
    public int Length { get => _length; set { _length = Math.Clamp(value, 5, 5000); RecalculateValues(); } }
    [Display(Name = "Tail threshold (% of POC volume)", GroupName = "Calculation", Order = 11), Range(1, 90)]
    public decimal ThresholdPercent { get => _threshold; set { _threshold = Math.Clamp(value, 1, 90); RecalculateValues(); } }
    [Display(Name = "Ticks per row", GroupName = "Calculation", Order = 12), Range(1, 10000)]
    public int TicksPerRow { get => _ticks; set { _ticks = Math.Clamp(value, 1, 10000); RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC fallback", GroupName = "Calculation", Order = 13)]
    public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "Fill the zones", GroupName = "Visualization", Order = 20)] public bool Fill { get; set; } = true;

    [Browsable(false)] public StudyCoverage Coverage => _profile.Coverage;
    [Browsable(false)] public override string Status => FootprintStudyData.Label(Coverage);

    protected override void ResetState() => _profile.Clear(grid: true);

    protected override void OnBarClosed(int bar)
    {
        var candle = Candle(bar);
        if (_profile.Step == 0) _profile.Reset(StudyRowProfile.RowStep(candle, _ticks), _length);
        _profile.AddBar(candle, _estimate);
        _supplyLow.SetUnavailable(bar); _supplyHigh.SetUnavailable(bar); _demandLow.SetUnavailable(bar); _demandHigh.SetUnavailable(bar);
        if (_profile.BarCount < _length || _profile.Levels(100) is not { } levels) return;
        var prices = _profile.Prices; var volumes = _profile.Volumes; decimal limit = levels.MaxVolume * _threshold / 100, step = _profile.Step;
        int poc = prices.IndexOf(levels.Poc);
        // the outermost rows below the threshold (grid gaps count as empty rows)
        int top = prices.Length - 1; while (top > poc && volumes[top] < limit) top--;
        int bottom = 0; while (bottom < poc && volumes[bottom] < limit) bottom++;
        if (top < prices.Length - 1) { _supplyLow.Set(bar, prices[top] + step); _supplyHigh.Set(bar, prices[^1]); }
        if (bottom > 0) { _demandLow.Set(bar, prices[0]); _demandHigh.Set(bar, prices[bottom] - step); }
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (!Fill || ChartInfo == null) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(LastVisibleBarNumber, Bars.Count - 1);
        int width = Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth);
        for (int i = first; i <= last; i++)
        {
            int x = ChartInfo.GetXByBar(i);
            Zone(i, x, _supplyLow, _supplyHigh, StudyPalette.Bear);
            Zone(i, x, _demandLow, _demandHigh, StudyPalette.Bull);
        }

        void Zone(int i, int x, OptionalValueDataSeries low, OptionalValueDataSeries high, Color color)
        {
            if (!low.IsAvailable(i) || !high.IsAvailable(i)) return;
            int y0 = ChartInfo.GetYByPrice(high[i], false), y1 = ChartInfo.GetYByPrice(low[i], false);
            ctx.FillRectangle(StudyPalette.A(color, 28), new Rectangle(x, Math.Min(y0, y1), width + 1, Math.Max(1, Math.Abs(y1 - y0))));
        }
    }
}

// ====================================================================== rolling profile (shared by two presentations)

/// <summary>
/// A volume profile of the last N closed bars, updated incrementally (the oldest bar's rows are removed exactly), with POC,
/// VAH and VAL series available once the window is full. Subclasses only change the presentation.
/// </summary>
public abstract class RollingProfileStudy : ClosedBarStudy
{
    private int _length, _ticks = 1; private decimal _va = 70; private bool _estimate;
    private readonly StudyRowProfile _profile = new();
    private StudyProfileLevels? _levels;
    private int _windowStart, _uncertainUntil = -1;
    private readonly string _prefix;
    private readonly OptionalValueDataSeries _poc, _vah, _val;

    protected RollingProfileStudy(string prefix, int length, bool showSeries)
    {
        _prefix = prefix; _length = length;
        var visual = showSeries ? VisualMode.Line : VisualMode.Hide;
        _poc = new($"{prefix}.Poc", "POC") { Color = StudyPalette.Amber, Width = 2, VisualType = visual, ShowZeroValue = true };
        _vah = new($"{prefix}.Vah", "VAH") { Color = StudyPalette.Bear, VisualType = visual, ShowZeroValue = true };
        _val = new($"{prefix}.Val", "VAL") { Color = StudyPalette.Bull, VisualType = visual, ShowZeroValue = true };
        DataSeries[0] = _poc; DataSeries.Add(_vah); DataSeries.Add(_val);
    }

    [Display(Name = "Bars in the profile", GroupName = "Calculation", Order = 10), Range(5, 5000)]
    public int Length { get => _length; set { _length = Math.Clamp(value, 5, 5000); RecalculateValues(); } }
    [Display(Name = "Value area (%)", GroupName = "Calculation", Order = 11), Range(10, 99)]
    public decimal ValueAreaPercent { get => _va; set { _va = Math.Clamp(value, 10, 99); RecalculateValues(); } }
    [Display(Name = "Ticks per row", GroupName = "Calculation", Order = 12), Range(1, 10000)]
    public int TicksPerRow { get => _ticks; set { _ticks = Math.Clamp(value, 1, 10000); RecalculateValues(); } }
    [Display(Name = "Allow estimated OHLC fallback", GroupName = "Calculation", Order = 13)]
    public bool AllowEstimated { get => _estimate; set { _estimate = value; RecalculateValues(); } }
    [Display(Name = "POC", GroupName = "Colors", Order = 40)] public Color PocColor { get; set; } = StudyPalette.Amber;
    [Display(Name = "Value area", GroupName = "Colors", Order = 41)] public Color ValueAreaColor { get; set; } = StudyPalette.Muted;

    [Browsable(false)] public StudyCoverage Coverage => _profile.Coverage;
    [Browsable(false)] public override string Status => FootprintStudyData.Label(Coverage);
    internal StudyRowProfile Profile => _profile;
    internal StudyProfileLevels? Current => _levels;
    /// <summary>First bar of the current window.</summary>
    protected int WindowStart => _windowStart;

    protected override void ResetState() { _profile.Clear(grid: true); _levels = null; _uncertainUntil = -1; }
    // A bar without causal coverage (e.g. synthetic OHLC with unknown availability) restarts the window, unless the user
    // opted into estimates: then it is drawn as an estimate, but strategy series stay unavailable while it is in the window.
    protected override void OnCoverageGap(int bar)
    {
        if (_estimate && Candle(bar).Volume > 0) { _uncertainUntil = bar + _length - 1; OnBarClosed(bar); return; }
        _profile.Clear(); _levels = null; _poc.SetUnavailable(bar); _vah.SetUnavailable(bar); _val.SetUnavailable(bar);
    }

    protected override void OnBarClosed(int bar)
    {
        var candle = Candle(bar);
        if (_profile.Step == 0) _profile.Reset(StudyRowProfile.RowStep(candle, _ticks), _length);
        _profile.AddBar(candle, _estimate);
        _windowStart = bar - _profile.BarCount + 1;
        _levels = _profile.Levels(_va);
        if (_profile.BarCount >= _length && bar > _uncertainUntil && _levels is { } v) { _poc.Set(bar, v.Poc); _vah.Set(bar, v.ValueAreaHigh); _val.Set(bar, v.ValueAreaLow); }
        else { _poc.SetUnavailable(bar); _vah.SetUnavailable(bar); _val.SetUnavailable(bar); }
        AfterBar(bar);
    }

    /// <summary>Per-bar hook for presentation state.</summary>
    protected virtual void AfterBar(int bar) { }

    /// <summary>The full-window levels as alertable lines (stable ids: the rolling level moves, its id does not).</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        foreach (var level in base.GetAlertLevels()) yield return level;
        if (_levels is not { } v || _profile.BarCount < _length) yield break;
        yield return new IndicatorLevel($"{_prefix}.Poc", "Rolling profile POC", v.Poc, _windowStart);
        yield return new IndicatorLevel($"{_prefix}.Vah", "Rolling profile VAH", v.ValueAreaHigh, _windowStart);
        yield return new IndicatorLevel($"{_prefix}.Val", "Rolling profile VAL", v.ValueAreaLow, _windowStart);
    }

    /// <summary>Draws the current profile rows as horizontal bars growing from <paramref name="x"/> (leftwards when <paramref name="fromRight"/>).</summary>
    protected void DrawRows(RenderContext ctx, int x, int width, bool fromRight)
    {
        if (ChartInfo == null || _levels is not { } v || v.MaxVolume <= 0) return;
        var prices = _profile.Prices; var volumes = _profile.Volumes; decimal half = _profile.Step / 2;
        for (int k = 0; k < prices.Length; k++)
        {
            int y0 = ChartInfo.GetYByPrice(prices[k] + half, false), y1 = ChartInfo.GetYByPrice(prices[k] - half, false);
            int w = Math.Max(1, (int)(width * volumes[k] / v.MaxVolume));
            var color = prices[k] == v.Poc ? StudyPalette.A(PocColor, 200) : prices[k] >= v.ValueAreaLow && prices[k] <= v.ValueAreaHigh ? StudyPalette.A(ValueAreaColor, 150) : StudyPalette.A(ValueAreaColor, 60);
            ctx.FillRectangle(color, new Rectangle(fromRight ? x - w : x, Math.Min(y0, y1), w, Math.Max(1, Math.Abs(y1 - y0) - 1)));
        }
    }
}

[DisplayName("Volume profile and value area (with hour map)")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("The volume profile of the last N closed bars drawn at the price axis with the POC and value area highlighted and dotted lines across the window, plus a heat map of volume by exchange-time hour (columns) and session day (rows). Profile rows come from observed footprint; the levels are those of the last closed bar.")]
public class VolumeProfileValueArea : RollingProfileStudy
{
    private int _heatDays = 7;
    private readonly Dictionary<(DateOnly Day, int Hour), decimal> _heat = new();
    private readonly List<DateOnly> _days = new();

    public VolumeProfileValueArea() : base("VolumeProfileValueArea", 96, false) { }

    [Display(Name = "Days in the hour map (0 = off)", GroupName = "Visualization", Order = 20), Range(0, 30)]
    public int HeatDays { get => _heatDays; set { _heatDays = Math.Clamp(value, 0, 30); RedrawChart(); } }
    [Display(Name = "Profile width (px)", GroupName = "Visualization", Order = 21), Range(20, 600)]
    public int ProfileWidth { get; set; } = 140;
    [Display(Name = "Hour map", GroupName = "Colors", Order = 42)] public Color HeatColor { get; set; } = StudyPalette.Amber;

    /// <summary>Volume by session day and exchange-time hour (0–23), last 30 session days.</summary>
    internal decimal HourVolume(DateOnly day, int hour) => _heat.TryGetValue((day, hour), out var v) ? v : 0;
    internal IReadOnlyList<DateOnly> HeatMapDays => _days;

    protected override void ResetState() { base.ResetState(); _heat.Clear(); _days.Clear(); }

    protected override void AfterBar(int bar)
    {
        var b = Bars[bar]; int hour = StudyClock.Local(b.Time, Candle(bar).SessionTimeZone).Hour;
        if (_days.Count == 0 || _days[^1] != b.SessionDate)
        {
            _days.Add(b.SessionDate);
            if (_days.Count > 30)
            {
                var drop = _days[0]; _days.RemoveAt(0);
                for (int h = 0; h < 24; h++) _heat.Remove((drop, h));
            }
        }
        _heat.TryGetValue((b.SessionDate, hour), out var old); _heat[(b.SessionDate, hour)] = old + b.Volume;
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (ChartInfo == null) return;
        DrawRows(ctx, region.Right - 2, ProfileWidth, true);
        if (Current is { } v && Bars.Count > 0)
        {
            int x0 = Math.Max(region.Left, ChartInfo.GetXByBar(WindowStart)); var font = FootprintStudyData.SmallFont;
            foreach (var (name, price, color) in new[] { ("POC", v.Poc, PocColor), ("VAH", v.ValueAreaHigh, ValueAreaColor), ("VAL", v.ValueAreaLow, ValueAreaColor) })
            {
                int y = ChartInfo.GetYByPrice(price, false);
                ctx.DrawLine(Pen(color, 1, LineDashStyle.Dot), x0, y, region.Right, y);
                ctx.DrawString($"{name} {Price(price)}", font, color, x0 + 3, y - 12);
            }
        }
        DrawHeatMap(ctx, region);
    }

    private void DrawHeatMap(RenderContext ctx, Rectangle region)
    {
        int count = Math.Min(_heatDays, _days.Count);
        if (count == 0) return;
        decimal max = 0;
        for (int d = _days.Count - count; d < _days.Count; d++) for (int h = 0; h < 24; h++) max = Math.Max(max, HourVolume(_days[d], h));
        if (max <= 0) return;
        const int cell = 9, cellWidth = 11; int x0 = region.Left + 8, y0 = region.Bottom - count * cell - 70;
        var font = FootprintStudyData.TinyFont;
        for (int row = 0; row < count; row++)
        {
            var day = _days[_days.Count - count + row]; int y = y0 + row * cell;
            ctx.DrawString(day.ToString("MM-dd", CultureInfo.InvariantCulture), font, StudyPalette.Muted, x0, y - 1);
            for (int h = 0; h < 24; h++)
            {
                double share = (double)(HourVolume(day, h) / max);
                ctx.FillRectangle(StudyPalette.A(HeatColor, (int)(20 + 220 * Math.Sqrt(share))), new Rectangle(x0 + 32 + h * cellWidth, y, cellWidth - 1, cell - 1));
            }
        }
    }
}

[DisplayName("Sliding volume profile")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("The volume profile of the last N closed bars, updated at every close: its POC and value-area edges as lines (available once the window is full) and the current profile drawn right of the last bar. Profile rows come from observed footprint.")]
public class SlidingVolumeProfile : RollingProfileStudy
{
    public SlidingVolumeProfile() : base("SlidingVolumeProfile", 100, true) { }

    [Display(Name = "Draw the current profile", GroupName = "Visualization", Order = 20)] public bool ShowProfile { get; set; } = true;
    [Display(Name = "Profile width (px)", GroupName = "Visualization", Order = 21), Range(20, 600)] public int ProfileWidth { get; set; } = 90;

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (!ShowProfile || ChartInfo == null || Bars.Count == 0) return;
        DrawRows(ctx, ChartInfo.GetXByBar(Bars.Count - 1, false) + 12, ProfileWidth, false);
    }
}

// ====================================================================== hot volume hours

[DisplayName("Volume heat hours")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("Each clock hour of the exchange time zone (DST-correct) as a box over its range, tinted by its volume compared with the average of the preceding hours; hours at or above the hot multiple get a solid border and a \"2.3x\" label. An hour is final at the close of its last bar (or, when the bar length is unknown, at the first bar of the next hour).")]
public class VolumeHeatHours : ClosedBarStudy
{
    public readonly record struct HourResult(DateTime StartUtc, int From, int To, int Available, decimal High, decimal Low, decimal Volume, decimal Average, decimal Ratio, bool Hot);

    private sealed class Hour { public DateTime Start; public int From, To; public decimal High, Low, Volume; }

    private int _baseline = 24, _hoursKept = 60; private decimal _hot = 2;
    private Hour? _hour; private TimeZoneInfo? _zone;
    private readonly Queue<decimal> _history = new(); private decimal _historySum;
    private readonly List<HourResult> _results = new();
    private readonly OptionalValueDataSeries _ratio = new("VolumeHeatHours.Ratio", "Finished hour volume ratio") { VisualType = VisualMode.Hide, ShowZeroValue = true };

    public VolumeHeatHours() { DataSeries[0] = _ratio; }

    [Display(Name = "Hours in the average", GroupName = "Calculation", Order = 10), Range(1, 500)]
    public int BaselineHours { get => _baseline; set { _baseline = Math.Clamp(value, 1, 500); RecalculateValues(); } }
    [Display(Name = "Hot from (× average)", GroupName = "Calculation", Order = 11), Range(1.1, 20)]
    public decimal HotMultiple { get => _hot; set { _hot = Math.Clamp(value, 1.1m, 20); RecalculateValues(); } }
    [Display(Name = "Hours drawn", GroupName = "Visualization", Order = 15, Description = "Display only: draws only the latest finished hours; never changes detection, strategy series or alerts."), Range(5, 1000)]
    public int HoursKept { get => _hoursKept; set { _hoursKept = Math.Clamp(value, 5, 1000); RedrawChart(); } }
    [Display(Name = "Color", GroupName = "Colors", Order = 20)] public Color HeatColor { get; set; } = StudyPalette.Amber;

    /// <summary>Finished hours with a baseline, oldest first (bounded).</summary>
    [Browsable(false)] public IReadOnlyList<HourResult> Hours => _results;

    protected override void ResetState() { _hour = null; _zone = null; _history.Clear(); _historySum = 0; _results.Clear(); }

    /// <summary>Source bar of the oldest hour still drawn (display only).</summary>
    private int FirstShownBar => _results.Count > _hoursKept ? _results[^_hoursKept].From : int.MinValue;
    public override bool IsShown(StudyBox box) => box.From >= FirstShownBar;
    public override bool IsShown(StudyTag tag) => tag.Bar >= FirstShownBar;

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; _zone ??= Candle(bar).SessionTimeZone;
        var start = StudyClock.HourStartUtc(b.Time, _zone);
        bool finished = false;
        if (_hour != null && _hour.Start != start) finished |= Finish(bar); // the previous hour's end was not known at its last bar
        if (_hour == null) _hour = new Hour { Start = start, From = bar, To = bar, High = b.High, Low = b.Low, Volume = b.Volume };
        else { _hour.To = bar; _hour.High = Math.Max(_hour.High, b.High); _hour.Low = Math.Min(_hour.Low, b.Low); _hour.Volume += b.Volume; }
        var length = ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero;
        if (length > TimeSpan.Zero && b.Time + length >= start.AddHours(1)) finished |= Finish(bar);
        if (finished) _ratio.Set(bar, _results[^1].Ratio); else _ratio.SetUnavailable(bar);
    }

    /// <summary>Closes the current hour, known at <paramref name="available"/>; true when it had a baseline.</summary>
    private bool Finish(int available)
    {
        var h = _hour!; _hour = null; bool scored = false;
        if (_history.Count > 0 && _historySum > 0)
        {
            decimal average = _historySum / _history.Count, ratio = h.Volume / average; bool hot = ratio >= _hot;
            int alpha = (int)Math.Clamp(20 + 120 * Math.Min(ratio, 3) / 3, 16, 160);
            var box = Scene.Box(h.From, available, h.High, h.Low, HeatColor, fillAlpha: alpha, style: hot ? LineDashStyle.Solid : LineDashStyle.Dot);
            box.To = h.To; box.EndAvailable = available;
            if (hot) Scene.Tag(h.From, available, h.High, ratio.ToString("0.0", CultureInfo.InvariantCulture) + "x", HeatColor);
            _results.Add(new HourResult(h.Start, h.From, h.To, available, h.High, h.Low, h.Volume, average, ratio, hot));
            if (_results.Count > 2 * MaximumObjects) _results.RemoveRange(0, _results.Count - MaximumObjects);
            scored = true;
        }
        _history.Enqueue(h.Volume); _historySum += h.Volume;
        while (_history.Count > _baseline) _historySum -= _history.Dequeue();
        return scored;
    }
}

// ====================================================================== execution benchmarks

public enum ExecutionWindow
{
    [Display(Name = "1 hour")] Hour,
    [Display(Name = "4 hours")] FourHours,
    [Display(Name = "Session")] Session,
    [Display(Name = "Week")] Week,
    [Display(Name = "Month")] Month,
}

public enum ExecutionSide
{
    [Display(Name = "Buy")] Buy,
    [Display(Name = "Sell")] Sell,
}

public enum ExecutionSizing
{
    [Display(Name = "% of typical window volume")] PercentOfVolume,
    [Display(Name = "Units")] Units,
}

[DisplayName("Execution benchmarks (TWAP, VWAP, POV)")]
[Category(IndicatorCategories.Vwap)]
[Description("Simulates, in every window (hour, 4 hours, session, week, month of the exchange calendar), a parent order worked three ways: TWAP (equal parts per bar), VWAP (by the typical volume share of each bar position, learned only from earlier windows) and POV (a percentage of each bar's volume). Fills are at each bar's typical price; average prices are compared with the arrival price, the window VWAP and the window TWAP in basis points (positive = worse for the side). A window's result is available at its last close. A simulation, not an execution forecast.")]
public class ExecutionBenchmarks : ClosedBarStudy
{
    public readonly record struct AlgoFill(decimal Filled, decimal AveragePrice, decimal VsArrivalBps, decimal VsVwapBps, decimal VsTwapBps);
    public sealed record WindowResult(int From, int To, int Available, decimal Parent, decimal Arrival, decimal WindowVwap, decimal WindowTwap, AlgoFill? Twap, AlgoFill? Vwap, AlgoFill? Pov, IReadOnlyList<decimal> VwapSchedule);

    private sealed class Live
    {
        public long Key; public DateTime EndUtc; public int From, To, Length, Count; public bool Scored;
        public decimal Arrival, Parent, ShapeSum, Pv, Volume, TypicalSum;
        public decimal[] Shape = Array.Empty<decimal>();
        public readonly decimal[] Filled = new decimal[3], Cost = new decimal[3];
        public readonly List<decimal> Volumes = new();
    }

    private static readonly string[] Algorithms = { "TWAP", "VWAP", "POV" };
    private static readonly Color[] AlgoColors = { Color.FromArgb(255, 41, 182, 246), Color.FromArgb(255, 171, 71, 188), Color.FromArgb(255, 102, 187, 106) };
    private ExecutionWindow _window = ExecutionWindow.Session; private ExecutionSide _side = ExecutionSide.Buy; private ExecutionSizing _sizing = ExecutionSizing.PercentOfVolume;
    private decimal _percent = 5, _units = 10000, _pov = 10; private int _scheduleBars, _lookback = 10; private bool _shade = true, _benchmarks = true, _fills = true;
    private Live? _live; private TimeZoneInfo? _zone;
    private readonly Queue<decimal[]> _history = new();
    private readonly List<WindowResult> _results = new();
    private readonly decimal[] _sumVsVwap = new decimal[3]; private readonly int[] _scored = new int[3];
    private readonly OptionalValueDataSeries[] _vsVwap = new OptionalValueDataSeries[3], _vsArrival = new OptionalValueDataSeries[3];

    public ExecutionBenchmarks()
    {
        for (int a = 0; a < 3; a++)
        {
            string name = Algorithms[a][0] + Algorithms[a][1..].ToLowerInvariant();
            _vsVwap[a] = new($"ExecutionBenchmarks.{name}.VsVwap", $"{Algorithms[a]} vs window VWAP (bps)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
            _vsArrival[a] = new($"ExecutionBenchmarks.{name}.VsArrival", $"{Algorithms[a]} vs arrival (bps)") { VisualType = VisualMode.Hide, ShowZeroValue = true };
        }
        DataSeries[0] = _vsVwap[0];
        DataSeries.Add(_vsVwap[1]); DataSeries.Add(_vsVwap[2]);
        foreach (var series in _vsArrival) DataSeries.Add(series);
    }

    [Display(Name = "Side", GroupName = "Parent order", Order = 10)] public ExecutionSide Side { get => _side; set { _side = value; RecalculateValues(); } }
    [Display(Name = "Sizing", GroupName = "Parent order", Order = 11)] public ExecutionSizing Sizing { get => _sizing; set { _sizing = value; RecalculateValues(); } }
    [Display(Name = "Size (% of typical window volume)", GroupName = "Parent order", Order = 12), Range(0.1, 100)]
    public decimal PercentSize { get => _percent; set { _percent = Math.Clamp(value, 0.1m, 100); RecalculateValues(); } }
    [Display(Name = "Size (units)", GroupName = "Parent order", Order = 13), Range(0.0001, 1000000000000)]
    public decimal UnitSize { get => _units; set { _units = Math.Clamp(value, 0.0001m, 1000000000000m); RecalculateValues(); } }
    [Display(Name = "Window", GroupName = "Parent order", Order = 14)] public ExecutionWindow Window { get => _window; set { _window = value; RecalculateValues(); } }
    [Display(Name = "Schedule bars (0 = typical window length)", GroupName = "Parent order", Order = 15), Range(0, 10000)]
    public int ScheduleBars { get => _scheduleBars; set { _scheduleBars = Math.Clamp(value, 0, 10000); RecalculateValues(); } }
    [Display(Name = "POV participation (%)", GroupName = "Parent order", Order = 16), Range(1, 100)]
    public decimal PovRate { get => _pov; set { _pov = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Earlier windows for the volume profile", GroupName = "Parent order", Order = 17), Range(1, 100)]
    public int LookbackWindows { get => _lookback; set { _lookback = Math.Clamp(value, 1, 100); RecalculateValues(); } }
    [Display(Name = "Benchmark lines", GroupName = "Visualization", Order = 20, Description = "Display only: hides the arrival and window VWAP lines; never changes the simulation, strategy series or alerts.")]
    public bool ShowBenchmarks { get => _benchmarks; set { _benchmarks = value; RedrawChart(); } }
    [Display(Name = "Average fill prices", GroupName = "Visualization", Order = 21, Description = "Display only: hides the average fill price lines; never changes the simulation, strategy series or alerts.")]
    public bool ShowFills { get => _fills; set { _fills = value; RedrawChart(); } }
    [Display(Name = "Shade the scheduled bars", GroupName = "Visualization", Order = 22, Description = "Display only: hides the shading of the scheduled bars; never changes the simulation, strategy series or alerts.")]
    public bool ShadeScheduled { get => _shade; set { _shade = value; RedrawChart(); } }

    /// <summary>Finished windows with a schedule, oldest first (bounded).</summary>
    [Browsable(false)] public IReadOnlyList<WindowResult> Results => _results;
    [Browsable(false)] public override string Status => "Simulated fills at bar typical prices; VWAP schedule from earlier windows only";

    protected override void ResetState()
    {
        _live = null; _zone = null; _history.Clear(); _results.Clear(); Array.Clear(_sumVsVwap); Array.Clear(_scored);
    }

    protected override void OnBarClosed(int bar)
    {
        var b = Bars[bar]; var candle = Candle(bar); _zone ??= candle.SessionTimeZone;
        var (key, end) = WindowOf(b, candle);
        for (int a = 0; a < 3; a++) { _vsVwap[a].SetUnavailable(bar); _vsArrival[a].SetUnavailable(bar); }
        if (_live != null && _live.Key != key) Finish(_live, bar); // its end was not seen at its last bar
        _live ??= Begin(key, end, bar, b);
        Work(_live, bar, b);
        var length = ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero;
        if (length > TimeSpan.Zero && b.Time + length >= _live.EndUtc) Finish(_live, bar);
        Dashboard();
    }

    private (long Key, DateTime End) WindowOf(StudyBar b, IndicatorCandle candle)
    {
        var zone = _zone!;
        switch (_window)
        {
            case ExecutionWindow.Hour:
            {
                var start = StudyClock.HourStartUtc(b.Time, zone); return (start.Ticks, start.AddHours(1));
            }
            case ExecutionWindow.FourHours:
            {
                var local = StudyClock.Local(b.Time, zone); int block = local.Hour / 4; var day = DateOnly.FromDateTime(local);
                return (day.DayNumber * 6L + block, StudyClock.ToUtc(day.ToDateTime(TimeOnly.MinValue).AddHours(4 * (block + 1)), zone));
            }
            case ExecutionWindow.Week:
            {
                long key = b.Key(StudyCalendar.Week); return (key, candle.SessionStartUtc(DateOnly.FromDayNumber((int)key).AddDays(7)));
            }
            case ExecutionWindow.Month:
                return (b.Key(StudyCalendar.Month), candle.SessionStartUtc(new DateOnly(b.SessionDate.Year, b.SessionDate.Month, 1).AddMonths(1)));
            default:
                return (b.Key(StudyCalendar.Day), candle.SessionStartUtc(b.SessionDate.AddDays(1)));
        }
    }

    /// <summary>A new window: its schedule and parent size come only from earlier, finished windows.</summary>
    private Live Begin(long key, DateTime end, int bar, StudyBar b)
    {
        var w = new Live { Key = key, EndUtc = end, From = bar, To = bar, Arrival = b.Open };
        if (_history.Count == 0) return w;
        int length = _scheduleBars > 0 ? _scheduleBars : _history.Max(h => h.Length);
        if (length <= 0) return w;
        var shape = new decimal[length]; decimal total = 0;
        foreach (var volumes in _history)
            for (int k = 0; k < Math.Min(length, volumes.Length); k++) { shape[k] += volumes[k]; total += volumes[k]; }
        w.Length = length; w.Shape = shape; w.ShapeSum = total;
        w.Parent = _sizing == ExecutionSizing.Units ? _units : total / _history.Count * _percent / 100;
        w.Scored = w.Parent > 0;
        return w;
    }

    private void Work(Live w, int bar, StudyBar b)
    {
        decimal typical = (b.High + b.Low + b.Close) / 3;
        w.To = bar; w.Pv += typical * b.Volume; w.Volume += b.Volume; w.TypicalSum += typical; w.Count++; w.Volumes.Add(b.Volume);
        int k = bar - w.From;
        if (!w.Scored || k >= w.Length) return;
        Fill(w, 0, w.Parent / w.Length, typical);
        Fill(w, 1, w.ShapeSum > 0 ? w.Parent * w.Shape[k] / w.ShapeSum : 0, typical);
        Fill(w, 2, b.Volume * _pov / 100, typical);
        Scene.Tints[bar] = StudyPalette.A(StudyPalette.Neutral, 16);
    }

    private static void Fill(Live w, int algo, decimal want, decimal price)
    {
        decimal quantity = Math.Min(want, w.Parent - w.Filled[algo]);
        if (quantity <= 0) return;
        w.Filled[algo] += quantity; w.Cost[algo] += quantity * price;
    }

    private void Finish(Live w, int available)
    {
        _live = null;
        _history.Enqueue(w.Volumes.ToArray());
        while (_history.Count > _lookback) _history.Dequeue();
        if (!w.Scored) return;
        decimal vwap = w.Volume > 0 ? w.Pv / w.Volume : w.Arrival, twap = w.Count > 0 ? w.TypicalSum / w.Count : w.Arrival;
        decimal sign = _side == ExecutionSide.Buy ? 1 : -1;
        decimal Bps(decimal price, decimal benchmark) => benchmark == 0 ? 0 : (price - benchmark) / benchmark * 10000 * sign;
        var fills = new AlgoFill?[3];
        for (int a = 0; a < 3; a++)
        {
            if (w.Filled[a] <= 0) continue;
            decimal average = w.Cost[a] / w.Filled[a];
            var fill = new AlgoFill(w.Filled[a], average, Bps(average, w.Arrival), Bps(average, vwap), Bps(average, twap));
            fills[a] = fill; _sumVsVwap[a] += fill.VsVwapBps; _scored[a]++;
            _vsVwap[a].Set(available, fill.VsVwapBps); _vsArrival[a].Set(available, fill.VsArrivalBps);
        }
        var schedule = w.ShapeSum > 0 ? w.Shape.Select(x => x / w.ShapeSum).ToArray() : Array.Empty<decimal>();
        _results.Add(new WindowResult(w.From, w.To, available, w.Parent, w.Arrival, vwap, twap, fills[0], fills[1], fills[2], schedule));
        if (_results.Count > 2 * MaximumObjects) _results.RemoveRange(0, _results.Count - MaximumObjects);

        void Line(decimal price, Color color, LineDashStyle style)
        {
            var line = Scene.Level(w.From, available, price, color, style: style);
            line.To = w.To; line.EndAvailable = available;
        }
        // benchmark lines are dotted/dashed and fill lines solid; IsShown filters them by that style
        Line(w.Arrival, StudyPalette.Muted, LineDashStyle.Dot); Line(vwap, StudyPalette.Amber, LineDashStyle.Dash);
        for (int a = 0; a < 3; a++) if (fills[a] is { } f) Line(f.AveragePrice, AlgoColors[a], LineDashStyle.Solid);
    }

    public override bool IsShown(StudyLine line) => line.Style == LineDashStyle.Solid ? _fills : _benchmarks;

    public override bool IsTintShown(int bar, Color color) => _shade;

    private void Dashboard()
    {
        Scene.Dash.Clear(); Scene.DashTitle = "Execution benchmarks (bps vs window VWAP)";
        var last = _results.Count > 0 ? _results[^1] : null;
        for (int a = 0; a < 3; a++)
        {
            var fill = a == 0 ? last?.Twap : a == 1 ? last?.Vwap : last?.Pov;
            string value = fill is { } f ? $"{Bps(f.VsVwapBps)} / {Bps(_scored[a] > 0 ? _sumVsVwap[a] / _scored[a] : 0)}" : "—";
            Scene.Row($"{Algorithms[a]} (last / average)", value, fill is { VsVwapBps: > 0 } ? StudyPalette.Bear : StudyPalette.Bull);
        }
        if (_live is { Scored: true } w) Scene.Row("Current window filled (TWAP)", (w.Filled[0] / w.Parent * 100).ToString("0", CultureInfo.InvariantCulture) + "%");
        Scene.Row("Windows evaluated", _results.Count.ToString(CultureInfo.InvariantCulture));

        static string Bps(decimal value) => value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
    }
}
