using System.Drawing;
using System.Globalization;
using TradingHub.Charting.Drawings;
using TradingHub.Indicators;
using TradingHub.Common.Logging;
using TradingHub.Rendering;

namespace TradingHub.Charting;

/// <summary>What a chart alert waits for. Persisted by name; new values append.</summary>
public enum ChartAlertCondition
{
    Crossing,
    CrossingUp,
    CrossingDown,
    Touching,
    EnteringChannel,
    ExitingChannel,
    MovingUpPercent,
    MovingDownPercent,
    GreaterThan,
    LessThan,
    InsideChannel,
    OutsideChannel,
}

/// <summary>
/// When a met condition fires. Once: the first time, then the alert switches off. Once per bar: at most once in each bar,
/// as soon as it is met on the forming bar. Once per bar close: only on bars that have closed. Every time: each time the
/// condition becomes met, but never twice within <see cref="ChartAlert.EveryTimeMinimumInterval"/>.
/// </summary>
public enum ChartAlertTrigger
{
    Once,
    OncePerBar,
    OncePerBarClose,
    EveryTime,
}

/// <summary>Where an alert operand takes its value from.</summary>
public enum ChartAlertSource
{
    /// <summary>The bar price: <see cref="ChartAlertOperand.Field"/> is close, high, low or open.</summary>
    Price,

    /// <summary>A fixed number (a price, or the percent of a move condition).</summary>
    Value,

    /// <summary>A data series of a chart indicator.</summary>
    Series,

    /// <summary>A level an indicator declares through <see cref="Indicator.GetAlertLevels"/>.</summary>
    Level,

    /// <summary>A line or curve an indicator drew, captured from the chart (kept in time and price).</summary>
    DrawnLine,

    /// <summary>One line of a user drawing (a line, ray, rectangle edge, channel edge, Fibonacci level, anchored VWAP).</summary>
    Drawing,
}

/// <summary>One side of an alert condition.</summary>
public sealed class ChartAlertOperand
{
    public ChartAlertSource Source { get; set; }

    /// <summary>Price field (close, high, low, open), series id, level id or drawing part, depending on <see cref="Source"/>.</summary>
    public string? Field { get; set; }

    public decimal? Value { get; set; }

    /// <summary>Chart indicator: "full type name#n" (the n-th of that type).</summary>
    public string? Indicator { get; set; }

    /// <summary>User drawing id ("N" format).</summary>
    public string? Drawing { get; set; }

    /// <summary>Captured indicator line: its shape, refreshed each time the indicator draws it again.</summary>
    public DrawnLine? Line { get; set; }

    /// <summary>English display name; segments are separated by " · " and translated one by one.</summary>
    public string Label { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore] internal int LineGeneration = -1;

    public static ChartAlertOperand Price(string field = "close") => new() { Source = ChartAlertSource.Price, Field = field, Label = "Price" };

    public static ChartAlertOperand Fixed(decimal value) =>
        new() { Source = ChartAlertSource.Value, Value = value, Label = value.ToString(CultureInfo.InvariantCulture) };

    public ChartAlertOperand Copy() => new()
    {
        Source = Source, Field = Field, Value = Value, Indicator = Indicator, Drawing = Drawing, Label = Label, Line = Line?.Clone(),
    };

    /// <summary>Same line (ignores labels and captured shapes).</summary>
    public bool SameTarget(ChartAlertOperand other) =>
        Source == other.Source && Field == other.Field && Indicator == other.Indicator && Drawing == other.Drawing &&
        (Source != ChartAlertSource.Value || Value == other.Value);

    /// <summary>The label with each " · " segment translated.</summary>
    public string DisplayLabel() => string.Join(" · ", Label.Split(" · ").Select(UiText.Tr));
}

/// <summary>An alert on a chart: price or a value against an indicator line, a declared level, a captured line or a drawing.</summary>
public sealed class ChartAlert
{
    /// <summary>Stable identity (persisted). Each firing is delivered as "chart:{Id}:{bar ticks}", so a retry never duplicates it.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..16];

    public ChartAlertOperand Left { get; set; } = ChartAlertOperand.Price();
    public ChartAlertCondition Condition { get; set; } = ChartAlertCondition.Crossing;

    /// <summary>The line (or the upper channel edge, or the percent for a move condition).</summary>
    public ChartAlertOperand Right { get; set; } = new();

    /// <summary>Lower channel edge for the channel conditions.</summary>
    public ChartAlertOperand? Lower { get; set; }

    public ChartAlertTrigger Trigger { get; set; } = ChartAlertTrigger.OncePerBar;

    /// <summary>Move conditions: over how many bars (1 = since the previous bar).</summary>
    public int Bars { get; set; } = 1;

    /// <summary>Optional user note added to the notification.</summary>
    public string Message { get; set; } = "";

    public bool Active { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastFiredUtc { get; set; }

    /// <summary>After this instant (UTC) the alert is expired: listed as such and no longer evaluated. Null = never.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>
    /// "Every time" never fires twice within this interval, so a line flapping around the price cannot flood the external
    /// destinations; a firing suppressed by it is skipped, not delayed.
    /// </summary>
    public static readonly TimeSpan EveryTimeMinimumInterval = TimeSpan.FromMinutes(1);

    public bool IsExpired(DateTime nowUtc) => ExpiresUtc is { } expires && nowUtc >= expires;

    // evaluation state (not persisted): an alert arms on the bar forming when it is created or loaded and never fires for
    // what happened before that, so loading history, restarting or editing never replays old signals
    internal long ArmedBar = long.MinValue;
    internal long StateBar = long.MinValue;
    internal bool? LastState;
    internal long FiredBar = long.MinValue;
    internal long ClosedBar = long.MinValue;
    internal bool? ClosedState;

    public static bool IsChannel(ChartAlertCondition c) =>
        c is ChartAlertCondition.EnteringChannel or ChartAlertCondition.ExitingChannel or ChartAlertCondition.InsideChannel or ChartAlertCondition.OutsideChannel;

    public static bool IsPercent(ChartAlertCondition c) => c is ChartAlertCondition.MovingUpPercent or ChartAlertCondition.MovingDownPercent;

    /// <summary>Level-state conditions (greater/less than, inside/outside a channel): they fire on the transition into the state.</summary>
    public static bool IsState(ChartAlertCondition c) =>
        c is ChartAlertCondition.GreaterThan or ChartAlertCondition.LessThan or ChartAlertCondition.InsideChannel or ChartAlertCondition.OutsideChannel;

    public IEnumerable<ChartAlertOperand> Operands()
    {
        yield return Left;
        yield return Right;
        if (Lower != null) yield return Lower;
    }

    /// <summary>Uses that drawing (removing the drawing removes the alert).</summary>
    public bool Uses(Guid drawing)
    {
        string id = drawing.ToString("N");
        return Operands().Any(o => o.Drawing == id);
    }

    /// <summary>Forgets the evaluation state: the alert re-arms on the next evaluation (after an edit).</summary>
    public void Rearm()
    {
        ArmedBar = StateBar = FiredBar = ClosedBar = long.MinValue;
        LastState = ClosedState = null;
    }

    public static string ConditionText(ChartAlertCondition c) => c switch
    {
        ChartAlertCondition.Crossing => "Crossing",
        ChartAlertCondition.CrossingUp => "Crossing up",
        ChartAlertCondition.CrossingDown => "Crossing down",
        ChartAlertCondition.Touching => "Touching",
        ChartAlertCondition.EnteringChannel => "Entering channel",
        ChartAlertCondition.ExitingChannel => "Exiting channel",
        ChartAlertCondition.MovingUpPercent => "Moving up %",
        ChartAlertCondition.MovingDownPercent => "Moving down %",
        ChartAlertCondition.GreaterThan => "Greater than",
        ChartAlertCondition.LessThan => "Less than",
        ChartAlertCondition.InsideChannel => "Inside channel",
        _ => "Outside channel",
    };

    public static string TriggerText(ChartAlertTrigger t) => t switch
    {
        ChartAlertTrigger.Once => "Only once",
        ChartAlertTrigger.OncePerBar => "Once per bar",
        ChartAlertTrigger.OncePerBarClose => "Once per bar close",
        _ => "Every time (at most once a minute)",
    };

    /// <summary>English description; <paramref name="translate"/> renders it in the UI language.</summary>
    public string Describe(bool translate = false)
    {
        string L(ChartAlertOperand? o) => o == null ? "" : translate ? o.DisplayLabel() : o.Label;
        string T(string s) => translate ? UiText.Tr(s) : s;
        string pct = (Right.Value ?? 0).ToString("0.##", CultureInfo.InvariantCulture);
        int bars = Math.Max(1, Bars);
        return Condition switch
        {
            ChartAlertCondition.Crossing => T($"{L(Left)} crosses {L(Right)}"),
            ChartAlertCondition.CrossingUp => T($"{L(Left)} crosses up {L(Right)}"),
            ChartAlertCondition.CrossingDown => T($"{L(Left)} crosses down {L(Right)}"),
            ChartAlertCondition.Touching => T($"{L(Left)} touches {L(Right)}"),
            ChartAlertCondition.EnteringChannel => T($"{L(Left)} enters the channel {L(Lower)} – {L(Right)}"),
            ChartAlertCondition.ExitingChannel => T($"{L(Left)} exits the channel {L(Lower)} – {L(Right)}"),
            ChartAlertCondition.MovingUpPercent => T($"{L(Left)} moves up {pct}% in {bars} bars"),
            ChartAlertCondition.MovingDownPercent => T($"{L(Left)} moves down {pct}% in {bars} bars"),
            ChartAlertCondition.GreaterThan => T($"{L(Left)} greater than {L(Right)}"),
            ChartAlertCondition.LessThan => T($"{L(Left)} less than {L(Right)}"),
            ChartAlertCondition.InsideChannel => T($"{L(Left)} inside the channel {L(Lower)} – {L(Right)}"),
            _ => T($"{L(Left)} outside the channel {L(Lower)} – {L(Right)}"),
        };
    }
}

/// <summary>One firing: the alert, the bar it fired on and its stable delivery id.</summary>
public readonly record struct ChartAlertFire(ChartAlert Alert, DateTime BarTime, string EventId);

/// <summary>Evaluation and line discovery for chart alerts (UI thread, after each model update).</summary>
public static class ChartAlertEngine
{
    /// <summary>Captured lines are recognised by this prefix in their list key.</summary>
    public const string DrawnPrefix = "drawn:";

    /// <summary>Most captured lines one indicator offers in the alert dialog.</summary>
    public const int MaxDrawnPerIndicator = 80;

    // ------------------------------------------------------------------ indicator identity

    /// <summary>Key of a chart indicator: its type and which one of that type it is.</summary>
    public static string KeyOf(ChartModel model, IndicatorSlot slot)
    {
        var t = slot.Indicator.GetType().FullName!;
        int n = 0;
        foreach (var s in model.Indicators)
        {
            if (ReferenceEquals(s, slot)) break;
            if (s.Indicator.GetType().FullName == t) n++;
        }

        return t + "#" + n.ToString(CultureInfo.InvariantCulture);
    }

    public static IndicatorSlot? SlotOf(ChartModel model, string key)
    {
        int hash = key.LastIndexOf('#');
        string t = hash > 0 ? key[..hash] : key;
        int n = hash > 0 && int.TryParse(key.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : 0;
        foreach (var s in model.Indicators)
        {
            if (s.Indicator.GetType().FullName != t) continue;
            if (n-- == 0) return s;
        }

        return null;
    }

    private static List<IndicatorLevel> Levels(IndicatorSlot slot)
    {
        try
        {
            return slot.Indicator.GetAlertLevels().ToList();
        }
        catch (Exception ex)
        {
            // one failing custom indicator must not take the other alerts down
            slot.Indicator.LogWarn($"Alert levels of {slot.Indicator.Name}: {ex.Message}");
            return new List<IndicatorLevel>();
        }
    }

    private static string Name(IndicatorSlot slot) => slot.Indicator.Name;

    private static bool Alertable(IDataSeries s) => s is ValueDataSeries v && !v.IsHidden && v.VisualType != VisualMode.Hide;

    // ------------------------------------------------------------------ what can be alerted

    /// <summary>Indicator lines that can carry an alert: data series, declared levels and captured lines still alive.</summary>
    public static List<ChartAlertOperand> Lines(ChartModel model)
    {
        var res = new List<ChartAlertOperand>();
        int last = model.Series.Count - 1;
        foreach (var slot in model.Indicators)
        {
            string key = KeyOf(model, slot);
            foreach (var s in slot.Indicator.DataSeries)
                if (Alertable(s)) res.Add(new ChartAlertOperand { Source = ChartAlertSource.Series, Indicator = key, Field = s.Id, Label = $"{Name(slot)} · {s.Name}" });

            var levels = Levels(slot);
            foreach (var l in levels)
                res.Add(new ChartAlertOperand { Source = ChartAlertSource.Level, Indicator = key, Field = l.Id, Label = $"{Name(slot)} · {l.Name}" });

            if (slot.Panel != null) continue;
            // the other lines it drew that continue to the right, without repeating its declared levels (within a couple of
            // pixels: a level drawn at its row centre rather than its price)
            double perPx = model.LastLayout is { } ll ? ll.Main.Range / Math.Max(1, ll.Main.Rect.Height) : 0;
            decimal near = Math.Max(Math.Max(model.Series.RowSize, model.Instrument.TickSize), (decimal)Math.Min(perPx * 2.5, 1e12));
            var alive = levels.Where(l => l.ToBar >= last).Select(l => l.Price).ToList();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            int n = 0;
            foreach (var d in slot.DrawnLines)
            {
                if (!d.Right || alive.Any(p => Math.Abs(p - d.LastPrice) <= near)) continue;
                if (!keys.Add(d.Key(model.Series))) continue;
                res.Add(OperandOf(model, slot, key, d));
                if (++n >= MaxDrawnPerIndicator) break;
            }
        }

        return res;
    }

    private static ChartAlertOperand OperandOf(ChartModel model, IndicatorSlot slot, string key, DrawnLine d)
    {
        string name = d.Text.Length > 0 ? d.Text : "line";
        string price = model.Instrument.FormatPrice(d.LastPrice);
        string label = name.Contains(price, StringComparison.Ordinal) ? $"{Name(slot)} · {name}" : $"{Name(slot)} · {name} · {price}";
        return new ChartAlertOperand { Source = ChartAlertSource.DrawnLine, Indicator = key, Field = d.Key(model.Series), Label = label, Line = d.Snapshot(model.Series) };
    }

    /// <summary>Lines of the chart's drawings that can carry an alert.</summary>
    public static List<ChartAlertOperand> DrawingLines(ChartModel model)
    {
        var res = new List<ChartAlertOperand>();
        var count = new Dictionary<DrawingKind, int>();
        foreach (var d in model.Drawings) AddDrawingLines(res, d, count);
        return res;
    }

    /// <summary>The lines of one drawing (upper edge first in channels).</summary>
    public static List<ChartAlertOperand> DrawingLines(ChartModel model, ChartDrawing drawing)
    {
        var res = new List<ChartAlertOperand>();
        var count = new Dictionary<DrawingKind, int>();
        foreach (var d in model.Drawings)
        {
            var mine = new List<ChartAlertOperand>();
            AddDrawingLines(mine, d, count);
            if (ReferenceEquals(d, drawing)) res.AddRange(mine);
        }

        if (!model.Drawings.Contains(drawing)) AddDrawingLines(res, drawing, count);
        return res;
    }

    private static void AddDrawingLines(List<ChartAlertOperand> res, ChartDrawing d, Dictionary<DrawingKind, int> count)
    {
        var parts = d.AlertParts();
        if (parts.Count == 0) return;
        int n = count[d.Kind] = (count.TryGetValue(d.Kind, out var c) ? c : 0) + 1;
        string text = d.DisplayText;
        string name = text.Length > 0 ? text : ChartDrawing.KindName(d.Kind) + (n > 1 ? " " + n.ToString(CultureInfo.InvariantCulture) : "");
        foreach (var (part, partName) in parts)
            res.Add(new ChartAlertOperand { Source = ChartAlertSource.Drawing, Drawing = d.Id.ToString("N"), Field = part, Label = parts.Count > 1 ? $"{name} · {partName}" : name });
    }

    /// <summary>The indicator line nearest to chart pixel (x, y), within <paramref name="tolerance"/> pixels.</summary>
    public static ChartAlertOperand? LineAt(ChartModel model, float x, float y, float tolerance = 7)
    {
        var layout = model.LastLayout;
        var panel = layout?.PanelAt(y);
        if (layout == null || panel == null || model.Series.Count == 0) return null;
        int bar = Math.Clamp(layout.BarAt(x), 0, model.Series.Count - 1);
        ChartAlertOperand? best = null;
        float bestD = tolerance;
        foreach (var slot in model.Indicators)
        {
            if (!slot.Visible) continue;
            bool here = panel.IsPrice ? slot.Panel == null : ReferenceEquals(slot.Panel, panel.SubPanel);
            if (!here) continue;
            string key = KeyOf(model, slot);
            foreach (var s in slot.Indicator.DataSeries)
            {
                if (!Alertable(s)) continue;
                var v = (ValueDataSeries)s;
                decimal val = v[bar];
                if (val == 0 && !v.ShowZeroValue) continue;
                float d = Math.Abs(panel.Y(val) - y);
                if (d > bestD) continue;
                bestD = d;
                best = new ChartAlertOperand { Source = ChartAlertSource.Series, Indicator = key, Field = s.Id, Label = $"{Name(slot)} · {s.Name}" };
            }

            if (!panel.IsPrice) continue;
            // any other line it drew; at equal distance series and declared levels win
            foreach (var d in slot.DrawnLines)
            {
                if (d.Pixels is not { } px) continue;
                float dd = DrawnLineCapture.Distance(px, x, y) + 1.5f;
                if (dd > bestD) continue;
                bestD = dd;
                best = OperandOf(model, slot, key, d);
            }

            // right of the last bar too: levels continue to the right
            double exact = layout.BarAtExact(x);
            foreach (var l in Levels(slot))
            {
                if (exact < l.FromBar - 0.5 || (l.ToBar != int.MaxValue && exact > l.ToBar + 1)) continue;
                // where the indicator draws it: a level price sits at its row's visual centre (GetYByPrice), not at Y(price)
                float ly = ((IChartContainer)panel).GetYByPrice(l.Price, false);
                float d = Math.Min(Math.Abs(ly - y), Math.Abs(panel.Y(l.Price) - y));
                if (d > bestD) continue;
                bestD = d;
                best = new ChartAlertOperand { Source = ChartAlertSource.Level, Indicator = key, Field = l.Id, Label = $"{Name(slot)} · {l.Name}" };
            }
        }

        return best;
    }

    /// <summary>The declared level behind an operand, or null when it is not one or no longer exists.</summary>
    public static IndicatorLevel? LevelOf(ChartModel model, ChartAlertOperand op)
    {
        if (op.Source != ChartAlertSource.Level || op.Indicator == null || SlotOf(model, op.Indicator) is not { } slot) return null;
        foreach (var l in Levels(slot))
            if (l.Id == op.Field) return l;
        return null;
    }

    /// <summary>Where a captured line is on screen now (the latest capture when it is still drawn, else its saved shape).</summary>
    public static PointF[] PixelsOf(ChartModel model, ChartLayout layout, ChartAlertOperand op)
    {
        if (op.Line is not { } line) return Array.Empty<PointF>();
        if (op.Indicator != null && SlotOf(model, op.Indicator) is { } slot && DrawnLine.Match(model.Series, line, slot.DrawnLines) is { Pixels: { } px }) return px;
        var bars = line.BarsIn(model.Series);
        var pts = new PointF[bars.Length];
        for (int i = 0; i < bars.Length; i++) pts[i] = new PointF(layout.CenterX(bars[i]), layout.Main.Y(line.Prices[i]));
        return pts;
    }

    // ------------------------------------------------------------------ values

    private static ChartDrawing? DrawingOf(ChartModel model, string? id)
    {
        if (id == null) return null;
        foreach (var d in model.Drawings)
            if (d.Id.ToString("N") == id) return d;
        return null;
    }

    public static bool TryValue(ChartModel model, ChartAlertOperand op, int bar, out decimal value)
    {
        value = 0;
        if (op.Source == ChartAlertSource.Value)
        {
            value = op.Value ?? 0;
            return op.Value != null;
        }

        if (bar < 0 || bar >= model.Series.Count) return false;
        switch (op.Source)
        {
            case ChartAlertSource.Price:
            {
                var c = model.Series[bar];
                value = model.Instrument.FromTicks(op.Field switch
                {
                    "high" => c.HighTicks,
                    "low" => c.LowTicks,
                    "open" => c.OpenTicks,
                    _ => c.CloseTicks,
                });
                return true;
            }
            case ChartAlertSource.Drawing:
            {
                var d = DrawingOf(model, op.Drawing);
                if (d == null) return false;
                if (d.Kind == DrawingKind.AnchoredVwap) return AnchoredVwapValue(model, d, op.Field, bar, out value);
                if (d.ValueAt(model.Series, bar, op.Field ?? "line") is not { } v || !double.IsFinite(v)) return false;
                value = (decimal)Math.Clamp(v, -7.9e27, 7.9e27);
                return true;
            }
        }

        var slot = op.Indicator != null ? SlotOf(model, op.Indicator) : null;
        if (slot == null) return false; // the indicator was removed (or failed to load)
        switch (op.Source)
        {
            case ChartAlertSource.DrawnLine when op.Line is { } drawn:
            {
                var capture = slot.LineCapture;
                if (capture.Generation != op.LineGeneration)
                {
                    // the indicator drew it again: follow it (a forming level moves, a live line grows). A capture that starts
                    // off screen never replaces a complete one; when it is not drawn any more its last shape stays
                    op.LineGeneration = capture.Generation;
                    var lines = capture.Lines;
                    if (lines.Count > 0 && DrawnLine.Match(model.Series, drawn, lines) is { } now && (!now.CutLeft || drawn.CutLeft))
                        op.Line = drawn = now.Snapshot(model.Series);
                }

                if (drawn.ValueAt(model.Series, bar) is not { } dv) return false;
                value = dv;
                return true;
            }
            case ChartAlertSource.Level:
                foreach (var l in Levels(slot))
                {
                    if (l.Id != op.Field) continue;
                    if (bar < l.FromBar || bar > l.ToBar) return false;
                    value = l.Price;
                    return true;
                }

                return false; // the level is gone (for example a naked POC that was traded through)
            case ChartAlertSource.Series:
                if (slot.Indicator.DataSeries.FindById(op.Field ?? "") is not ValueDataSeries s) return false;
                value = s[bar];
                return value != 0 || s.ShowZeroValue;
            default:
                return false;
        }
    }

    private static bool AnchoredVwapValue(ChartModel model, ChartDrawing d, string? part, int bar, out decimal value)
    {
        value = 0;
        if (d.Points.Count == 0) return false;
        var result = model.AlertVwapCalculator(d).AnchoredVwap(model.Data.Trades, model.Series, d.Points[0].Time, model.DataGeneration);
        var pts = result.Points;
        // the latest sample at or before the bar (the line may be sampled on long anchors)
        int lo = 0, hi = pts.Count - 1, at = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (pts[mid].Bar <= bar) { at = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        if (at < 0) return false;
        var p = pts[at];
        double m = part switch { "u1" => d.Band1, "d1" => -d.Band1, "u2" => d.Band2, "d2" => -d.Band2, _ => 0 };
        if (!double.IsFinite(m) || !double.IsFinite(p.Deviation)) return false;
        value = p.Vwap + (decimal)(p.Deviation * m);
        return true;
    }

    // ------------------------------------------------------------------ conditions

    /// <summary>The condition at bar <paramref name="bar"/> (null = a value is missing).</summary>
    public static bool? Check(ChartModel model, ChartAlert a, int bar)
    {
        if (ChartAlert.IsPercent(a.Condition))
        {
            int back = Math.Max(1, a.Bars);
            if (!TryValue(model, a.Left, bar, out var now) || !TryValue(model, a.Left, bar - back, out var then) || then == 0) return null;
            decimal pct = a.Right.Value ?? 0;
            decimal change = (now - then) / Math.Abs(then) * 100m;
            return a.Condition == ChartAlertCondition.MovingUpPercent ? change >= pct : change <= -pct;
        }

        if (!TryValue(model, a.Left, bar, out var x0) || !TryValue(model, a.Right, bar, out var r0)) return null;
        bool has1 = TryValue(model, a.Left, bar - 1, out var x1) & TryValue(model, a.Right, bar - 1, out var r1);
        if (ChartAlert.IsChannel(a.Condition))
        {
            if (a.Lower == null || !TryValue(model, a.Lower, bar, out var l0)) return null;
            has1 &= TryValue(model, a.Lower, bar - 1, out var l1);
            if (l0 > r0) (l0, r0) = (r0, l0);
            if (l1 > r1) (l1, r1) = (r1, l1);
            bool inside0 = x0 >= l0 && x0 <= r0, inside1 = x1 >= l1 && x1 <= r1;
            return a.Condition switch
            {
                ChartAlertCondition.EnteringChannel => has1 && !inside1 && inside0,
                ChartAlertCondition.ExitingChannel => has1 && inside1 && !inside0,
                ChartAlertCondition.InsideChannel => inside0,
                _ => !inside0,
            };
        }

        return a.Condition switch
        {
            ChartAlertCondition.Crossing => has1 && ((x1 <= r1 && x0 > r0) || (x1 >= r1 && x0 < r0)),
            ChartAlertCondition.CrossingUp => has1 && x1 <= r1 && x0 > r0,
            ChartAlertCondition.CrossingDown => has1 && x1 >= r1 && x0 < r0,
            ChartAlertCondition.GreaterThan => x0 > r0,
            ChartAlertCondition.LessThan => x0 < r0,
            _ => Touches(model, a, bar, x0, r0, has1, x1, r1),
        };
    }

    /// <summary>
    /// Touching: against the price, the bar's range reaches the line (low at or below, high at or above); against another
    /// line, the two meet or cross.
    /// </summary>
    private static bool Touches(ChartModel model, ChartAlert a, int bar, decimal x0, decimal r0, bool has1, decimal x1, decimal r1)
    {
        if (a.Left.Source == ChartAlertSource.Price)
        {
            var c = model.Series[bar];
            return model.Instrument.FromTicks(c.LowTicks) <= r0 && model.Instrument.FromTicks(c.HighTicks) >= r0;
        }

        return x0 == r0 || (has1 && (x1 - r1) * (x0 - r0) <= 0);
    }

    /// <summary>Most alerts one chart keeps.</summary>
    public const int MaxAlertsPerChart = 200;

    /// <summary>
    /// Validated copies of persisted alerts: malformed entries (missing operands, unknown enum values, a channel without its
    /// lower edge, a percent without its value) are dropped; ids are made unique and the percent lookback bounded.
    /// </summary>
    public static List<ChartAlert> Sanitize(IEnumerable<ChartAlert?>? saved)
    {
        var res = new List<ChartAlert>();
        if (saved == null) return res;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in saved)
        {
            if (res.Count >= MaxAlertsPerChart) break;
            if (a?.Left == null || a.Right == null || !Enum.IsDefined(a.Condition) || !Enum.IsDefined(a.Trigger)) continue;
            if (a.Operands().Any(o => !Enum.IsDefined(o.Source) || !Valid(o))) continue;
            if (ChartAlert.IsChannel(a.Condition) && a.Lower == null) continue;
            if (ChartAlert.IsPercent(a.Condition) && (a.Right.Source != ChartAlertSource.Value || a.Right.Value is not >= 0)) continue;
            if (string.IsNullOrEmpty(a.Id) || a.Id.Length > 64 || !a.Id.All(char.IsAsciiLetterOrDigit) || !ids.Add(a.Id))
            {
                a.Id = Guid.NewGuid().ToString("N")[..16];
                ids.Add(a.Id);
            }

            a.Bars = Math.Clamp(a.Bars, 1, 1000);
            a.Message ??= "";
            a.Rearm();
            res.Add(a);
        }

        return res;
    }

    private static bool Valid(ChartAlertOperand o) => o.Source switch
    {
        ChartAlertSource.Value => o.Value != null,
        ChartAlertSource.Price => o.Field is "close" or "high" or "low" or "open",
        ChartAlertSource.Drawing => Guid.TryParseExact(o.Drawing, "N", out _) && !string.IsNullOrEmpty(o.Field),
        ChartAlertSource.DrawnLine => o.Indicator != null && o.Line is { Prices.Length: > 0 } l && l.Times.Length == l.Prices.Length,
        _ => o.Indicator != null && !string.IsNullOrEmpty(o.Field),
    };

    /// <summary>
    /// Removes alerts whose drawing (or drawing line) no longer exists, returning them. Indicator alerts are removed with
    /// their indicator (<see cref="ChartModel.RemoveIndicator"/>), never because it failed to load.
    /// </summary>
    public static List<ChartAlert> Prune(ChartModel model)
    {
        var removed = new List<ChartAlert>();
        if (model.Alerts.Count == 0) return removed;
        foreach (var a in model.Alerts)
        {
            foreach (var op in a.Operands())
            {
                if (op.Source != ChartAlertSource.Drawing) continue;
                var d = DrawingOf(model, op.Drawing);
                if (d == null || d.AlertParts().All(p => p.Part != op.Field))
                {
                    removed.Add(a);
                    break;
                }
            }
        }

        foreach (var a in removed) model.Alerts.Remove(a);
        return removed;
    }

    /// <summary>
    /// Checks the active alerts on the latest bars and returns those that fire now. Nothing is evaluated while history is
    /// still loading or being aggregated; an alert arms on the bar forming when first evaluated and only fires on a change
    /// observed after arming, so history and restarts never fire. Level-state conditions fire on the transition into the
    /// state. Once-alerts switch off when they fire; expired alerts are skipped.
    /// </summary>
    public static List<ChartAlertFire> Evaluate(ChartModel model, DateTime nowUtc)
    {
        var fired = new List<ChartAlertFire>();
        int n = model.Series.Count;
        if (n < 2 || model.Alerts.Count == 0 || model.Data.IsLoadingHistory || model.IsAggregating) return fired;
        long forming = model.Series[n - 1].Time.Ticks;
        foreach (var a in model.Alerts)
        {
            if (!a.Active || a.IsExpired(nowUtc)) continue;
            bool level = ChartAlert.IsState(a.Condition);
            bool onClose = a.Trigger == ChartAlertTrigger.OncePerBarClose;
            if (a.ArmedBar == long.MinValue)
            {
                a.ArmedBar = forming;
                // a level-state alert closing on the bar: its state when armed is the reference for the first close
                if (level && onClose) a.ClosedState = Check(model, a, n - 1);
            }

            int bar = onClose ? n - 2 : n - 1;
            long barKey = model.Series[bar].Time.Ticks;
            if (barKey < a.ArmedBar) continue; // closed before the alert existed
            bool fire;
            if (onClose)
            {
                if (a.ClosedBar == barKey) continue; // each closed bar is checked once
                var closed = Check(model, a, bar);
                if (closed == null) continue;
                a.ClosedBar = barKey;
                bool? before = a.ClosedState;
                a.ClosedState = closed;
                fire = closed == true && (!level || before == false);
            }
            else
            {
                var state = Check(model, a, bar);
                if (state == null) continue;
                bool never = a.StateBar == long.MinValue;
                bool sameBar = a.StateBar == barKey;
                // a level state carries over between bars; an event (a cross, a touch) belongs to its bar
                bool was = (level ? !never : sameBar) && a.LastState == true;
                a.StateBar = barKey;
                a.LastState = state;
                // the first look only records the state: what held before arming is history
                if (level ? never : barKey == a.ArmedBar && !sameBar) continue;
                bool entered = state == true && !was;
                fire = a.Trigger switch
                {
                    ChartAlertTrigger.EveryTime => entered && (a.LastFiredUtc is not { } last || nowUtc - last >= ChartAlert.EveryTimeMinimumInterval),
                    _ when level => entered && a.FiredBar != barKey,
                    _ => state == true && a.FiredBar != barKey && (barKey != a.ArmedBar || !was),
                };
            }

            if (!fire) continue;
            a.FiredBar = barKey;
            a.LastFiredUtc = nowUtc;
            if (a.Trigger == ChartAlertTrigger.Once) a.Active = false;
            var time = model.Series[bar].Time;
            string id = string.Concat("chart:", a.Id, ":", time.Ticks.ToString(CultureInfo.InvariantCulture));
            // every-time firings can repeat within a bar: each gets its own stable id
            if (a.Trigger == ChartAlertTrigger.EveryTime) id = string.Concat(id, ":", nowUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            fired.Add(new ChartAlertFire(a, time, id));
        }

        return fired;
    }
}
