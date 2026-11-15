// Independent closed-bar study base for the structure/session/profile library; capability reference
// gouqizi2214730-source/trading-hub @ 97a8521 (MIT with Commons Clause), not a port of its scene engine.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>One completed chart bar as the library studies see it (index = chart bar index).</summary>
public readonly record struct StudyBar(DateTime Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, DateOnly SessionDate, bool Causal)
{
    public decimal Range => High - Low;
    public decimal Body => Math.Abs(Close - Open);
    public bool Bullish => Close > Open;
    public bool Bearish => Close < Open;

    /// <summary>Exchange-session calendar key (session day, Monday week, calendar month/quarter/year of the session date).</summary>
    public long Key(StudyCalendar period) => period switch
    {
        StudyCalendar.Week => SessionDate.DayNumber - ((int)SessionDate.DayOfWeek + 6) % 7,
        StudyCalendar.Month => SessionDate.Year * 12L + SessionDate.Month - 1,
        StudyCalendar.Quarter => SessionDate.Year * 4L + (SessionDate.Month - 1) / 3,
        StudyCalendar.Year => SessionDate.Year,
        _ => SessionDate.DayNumber,
    };
}

public enum StudyCalendar
{
    [Display(Name = "Session")] Day,
    [Display(Name = "Week")] Week,
    [Display(Name = "Month")] Month,
    [Display(Name = "Quarter")] Quarter,
    [Display(Name = "Year")] Year,
}

public enum StudyMarkKind { TriangleUp, TriangleDown, Circle, Diamond, Cross, Square }

public enum StudyCorner
{
    [Display(Name = "Top left")] TopLeft,
    [Display(Name = "Top center")] TopCenter,
    [Display(Name = "Top right")] TopRight,
    [Display(Name = "Middle left")] MiddleLeft,
    [Display(Name = "Middle")] Middle,
    [Display(Name = "Middle right")] MiddleRight,
    [Display(Name = "Bottom left")] BottomLeft,
    [Display(Name = "Bottom center")] BottomCenter,
    [Display(Name = "Bottom right")] BottomRight,
}

public enum StudyTextSize
{
    [Display(Name = "Small")] Small,
    [Display(Name = "Normal")] Normal,
    [Display(Name = "Large")] Large,
}

/// <summary>
/// Base of a scene shape. <see cref="From"/> is the source bar it is drawn from; <see cref="Available"/> is the closed bar
/// at whose close it became known (never earlier than the data it uses). <see cref="To"/> = -1 extends to the right edge;
/// when a later close ends it, <see cref="EndAvailable"/> is that bar.
/// </summary>
public abstract class StudyShape
{
    public int From { get; init; }
    public int Available { get; init; }
    public int To { get; set; } = -1;
    public int EndAvailable { get; set; } = -1;
    public bool Live => To < 0;
    /// <summary>English level name for alerts; null = not alertable.</summary>
    public string? Alert { get; init; }

    /// <summary>Ends the shape at <paramref name="bar"/>, known at the close of that bar.</summary>
    public void End(int bar) { To = bar; EndAvailable = bar; }
}

public sealed class StudyBox : StudyShape
{
    public decimal Top { get; init; }
    public decimal Bottom { get; init; }
    public Color Color { get; set; }
    public int FillAlpha { get; set; } = 40;
    public string? Text { get; init; }
    public LineDashStyle Style { get; init; }
}

public sealed class StudyLine : StudyShape
{
    public decimal Price { get; init; }
    /// <summary>End price for a sloped segment; equal to <see cref="Price"/> for a horizontal level.</summary>
    public decimal EndPrice { get; init; }
    public Color Color { get; set; }
    public int Width { get; init; } = 1;
    public LineDashStyle Style { get; init; }
    public string? Label { get; init; }
}

public sealed record StudyMark(int Bar, int Available, decimal Price, StudyMarkKind Kind, Color Color, string? Text = null, bool Above = true);

public sealed record StudyTag(int Bar, int Available, decimal Price, string Text, Color Back, bool Above = true);

public sealed record StudyPath(int Available, IReadOnlyList<(int Bar, decimal Price)> Points, Color Color, int Width = 1, LineDashStyle Style = LineDashStyle.Solid);

/// <summary>What a <see cref="ClosedBarStudy"/> draws, built only from completed bars, in order.</summary>
public sealed class StudyScene
{
    public List<StudyBox> Boxes { get; } = new();
    public List<StudyLine> Lines { get; } = new();
    public List<StudyMark> Marks { get; } = new();
    public List<StudyTag> Tags { get; } = new();
    public List<StudyPath> Paths { get; } = new();
    /// <summary>Background tint of a whole bar column.</summary>
    public Dictionary<int, Color> Tints { get; } = new();
    /// <summary>Dashboard title and rows (English names; values are numbers or English words).</summary>
    public string? DashTitle { get; set; }
    public List<(string Name, string Value, Color Color)> Dash { get; } = new();

    public void Clear()
    {
        Boxes.Clear(); Lines.Clear(); Marks.Clear(); Tags.Clear(); Paths.Clear(); Tints.Clear(); Dash.Clear(); DashTitle = null;
    }

    public StudyBox Box(int from, int available, decimal top, decimal bottom, Color color, string? text = null, string? alert = null, int fillAlpha = 40, LineDashStyle style = LineDashStyle.Solid)
    {
        var box = new StudyBox { From = from, Available = available, Top = Math.Max(top, bottom), Bottom = Math.Min(top, bottom), Color = color, Text = text, Alert = alert, FillAlpha = fillAlpha, Style = style };
        Boxes.Add(box); return box;
    }

    public StudyLine Level(int from, int available, decimal price, Color color, string? label = null, string? alert = null, int width = 1, LineDashStyle style = LineDashStyle.Solid)
    {
        var line = new StudyLine { From = from, Available = available, Price = price, EndPrice = price, Color = color, Label = label, Alert = alert, Width = width, Style = style };
        Lines.Add(line); return line;
    }

    public StudyLine Segment(int from, int to, int available, decimal p0, decimal p1, Color color, int width = 1, LineDashStyle style = LineDashStyle.Solid)
    {
        var line = new StudyLine { From = from, Available = available, Price = p0, EndPrice = p1, Color = color, Width = width, Style = style, To = to, EndAvailable = available };
        Lines.Add(line); return line;
    }

    public void Mark(int bar, int available, decimal price, StudyMarkKind kind, Color color, string? text = null, bool above = true) => Marks.Add(new(bar, available, price, kind, color, text, above));

    public void Tag(int bar, int available, decimal price, string text, Color back, bool above = true) => Tags.Add(new(bar, available, price, text, back, above));

    public void Row(string name, string value, Color color = default) => Dash.Add((name, value, color));

    /// <summary>Drops the oldest ended shapes once a list is twice <paramref name="max"/>; live shapes are kept.</summary>
    internal void Retain(int max)
    {
        Trim(Boxes, max); Trim(Lines, max);
        if (Marks.Count > 2 * max) Marks.RemoveRange(0, Marks.Count - max);
        if (Tags.Count > 2 * max) Tags.RemoveRange(0, Tags.Count - max);
        if (Paths.Count > 2 * max) Paths.RemoveRange(0, Paths.Count - max);
        if (Tints.Count > 4 * max)
        {
            int keepFrom = Tints.Keys.OrderByDescending(k => k).ElementAt(2 * max - 1);
            foreach (var key in Tints.Keys.Where(k => k < keepFrom).ToList()) Tints.Remove(key);
        }
    }

    private static void Trim<T>(List<T> list, int max) where T : StudyShape
    {
        if (list.Count <= 2 * max) return;
        int excess = list.Count - max;
        list.RemoveAll(s => !s.Live && excess-- > 0);
    }
}

/// <summary>Shared colors of the library studies.</summary>
internal static class StudyPalette
{
    internal static readonly Color Bull = Color.FromArgb(255, 38, 166, 154);
    internal static readonly Color Bear = Color.FromArgb(255, 239, 83, 80);
    internal static readonly Color Neutral = Color.FromArgb(255, 41, 98, 255);
    internal static readonly Color Muted = Color.FromArgb(255, 120, 123, 134);
    internal static readonly Color Amber = Color.FromArgb(255, 251, 140, 0);
    internal static readonly Color Rose = Color.FromArgb(255, 251, 113, 133);
    internal static Color A(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);
}

/// <summary>
/// Base of the closed-bar library studies. Each completed bar is processed exactly once, in order
/// (<see cref="OnBarClosed"/>); the forming bar never creates or ends shapes, so nothing drawn is ever redrawn backwards
/// and a strategy value written at bar <c>b</c> depends only on bars up to <c>b</c>. Bars without causal coverage call
/// <see cref="OnCoverageGap"/> (default: reset running state). Shapes carry their source bar (drawing position) and their
/// confirmation bar (availability) separately.
/// </summary>
public abstract class ClosedBarStudy : Indicator
{
    private readonly List<StudyBar> _bars = new();
    private readonly StudyScene _scene = new();
    private int _lastRequested = -1, _maxShapes = 400;
    private bool _dash = true, _retrospective = true;
    private StudyCorner _corner = StudyCorner.TopRight;
    private StudyTextSize _text = StudyTextSize.Normal;

    protected ClosedBarStudy() : base(true)
    {
        EnableCustomDrawing = true; DrawAbovePrice = true; SubscribeToDrawingEvents(DrawingLayouts.Final);
        DataSeries[0].IsHidden = true;
    }

    /// <summary>Completed bars processed so far (index = chart bar).</summary>
    [Browsable(false)] public IReadOnlyList<StudyBar> Bars => _bars;
    /// <summary>Everything drawn, for inspection and tests.</summary>
    [Browsable(false)] public StudyScene Scene => _scene;
    [Browsable(false)] public virtual string Status => "Closed bars only; drawn at the source bar, known at the confirming close";

    [Display(Name = "Draw from the source bar (strategy values stay causal)", GroupName = "Visualization", Order = 890)]
    public bool RetrospectiveDrawing { get => _retrospective; set { _retrospective = value; RedrawChart(); } }
    [Display(Name = "Maximum retained objects", GroupName = "Visualization", Order = 891), Range(50, 5000)]
    public int MaximumObjects { get => _maxShapes; set { _maxShapes = Math.Clamp(value, 50, 5000); RecalculateValues(); } }
    [Display(Name = "Show the dashboard", GroupName = "Dashboard", Order = 900)]
    public bool ShowDashboard { get => _dash; set { _dash = value; RedrawChart(); } }
    [Display(Name = "Dashboard position", GroupName = "Dashboard", Order = 901)]
    public StudyCorner DashboardPosition { get => _corner; set { _corner = value; RedrawChart(); } }
    [Display(Name = "Text size", GroupName = "Dashboard", Order = 902)]
    public StudyTextSize TextSize { get => _text; set { _text = value; RedrawChart(); } }

    /// <summary>Clears the study's running state (not the scene).</summary>
    protected abstract void ResetState();
    /// <summary>Processes completed causal bar <paramref name="bar"/> (= <c>Bars.Count - 1</c>).</summary>
    protected abstract void OnBarClosed(int bar);
    /// <summary>A completed bar without causal coverage: by default the running state restarts after it.</summary>
    protected virtual void OnCoverageGap(int bar) => ResetState();

    protected sealed override void OnRecalculate()
    {
        _bars.Clear(); _scene.Clear(); _lastRequested = -1; ResetState();
    }

    protected sealed override void OnCalculate(int bar, decimal value)
    {
        bool retracted = bar < _bars.Count && bar == _lastRequested && !GetCandle(bar).IsCandleFinished;
        if (bar < _lastRequested || retracted) { OnRecalculate(); Clear(); }
        // a bar is complete when it is finished or a later bar exists; the forming bar is never processed
        while (_bars.Count <= bar && (_bars.Count < bar || GetCandle(_bars.Count).IsCandleFinished)) Process(_bars.Count);
        _lastRequested = bar;
    }

    private void Process(int b)
    {
        var c = GetCandle(b);
        _bars.Add(new StudyBar(c.Time, c.Open, c.High, c.Low, c.Close, c.Volume, c.SessionDate, c.CanCalculateCausally));
        if (_bars[b].Causal) OnBarClosed(b); else OnCoverageGap(b);
        _scene.Retain(_maxShapes);
    }

    /// <summary>The causal candle of a processed bar, for studies needing footprint rows.</summary>
    protected IndicatorCandle Candle(int bar) => GetCandle(bar);

    /// <summary>True when bars <paramref name="from"/>..<paramref name="to"/> are all causal.</summary>
    protected bool AllCausal(int from, int to)
    {
        if (from < 0) return false;
        for (int i = from; i <= to; i++) if (!_bars[i].Causal) return false;
        return true;
    }

    protected decimal Tick => InstrumentInfo?.TickSize is > 0 and var t ? t : 0.01m;

    protected string Price(decimal price) => FootprintStudyData.Nearest(price, Tick).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ alerts

    /// <summary>
    /// Live alert-named horizontal lines and live box edges. Ids combine the name, the source bar time and the (immutable)
    /// level price, so they survive recalculation and two levels started on the same bar stay distinct; identical live levels
    /// (same name, source bar and price) are one alertable level.
    /// </summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        if (_bars.Count == 0) return Array.Empty<IndicatorLevel>();
        var levels = new List<IndicatorLevel>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id, string name, decimal price, int from) { if (seen.Add(id)) levels.Add(new IndicatorLevel(id, name, price, from)); }
        foreach (var line in _scene.Lines)
            if (line.Alert != null && line.Live && line.Price == line.EndPrice) Add(Id(line.Alert, line.From, line.Price), line.Alert, line.Price, line.From);
        foreach (var box in _scene.Boxes)
        {
            if (box.Alert == null || !box.Live) continue;
            Add(Id(box.Alert + ".top", box.From, box.Top), $"{box.Alert}: top edge", box.Top, box.From);
            Add(Id(box.Alert + ".bottom", box.From, box.Bottom), $"{box.Alert}: bottom edge", box.Bottom, box.From);
        }
        return levels;
    }

    private string Id(string name, int from, decimal price) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}@{_bars[from].Time.Ticks}@{price:G29}");

    // ------------------------------------------------------------------ drawing

    private RenderFont Font(bool bold = false) => new("Arial", _text switch { StudyTextSize.Small => 8, StudyTextSize.Large => 11, _ => 9 }, bold ? FontStyle.Bold : FontStyle.Regular);

    internal static RenderPen Pen(Color color, int width, LineDashStyle style) => new(color, width, (DashStyle)(int)style);

    private int Start(StudyShape s) => _retrospective ? s.From : Math.Max(s.From, s.Available);

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || _bars.Count == 0) return;
        var region = ChartInfo.PriceChartContainer.Region;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber, lastBar = _bars.Count - 1;
        int right = region.Right, bw = Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth);
        int X(int bar, bool start = true) => ChartInfo.GetXByBar(bar, start);
        int Y(decimal price) => ChartInfo.GetYByPrice(price, false);
        bool Visible(int from, int to) => from <= last + 1 && (to < 0 || to >= first);
        var font = Font();

        foreach (var (bar, color) in _scene.Tints)
        {
            if (bar < first || bar > last || !IsTintShown(bar, color)) continue;
            int x0 = X(bar); ctx.FillRectangle(color, new Rectangle(x0, region.Top, Math.Max(1, X(bar + 1) - x0), region.Height));
        }
        foreach (var b in _scene.Boxes)
        {
            int from = Start(b);
            if (!Visible(from, b.To) || b.To >= 0 && b.To < from || !IsShown(b)) continue;
            int x0 = X(from), x1 = b.To < 0 ? right : X(b.To, false) + bw / 2; if (x1 <= x0) x1 = x0 + 2;
            int y0 = Y(b.Top), y1 = Y(b.Bottom);
            var rect = new Rectangle(x0, Math.Min(y0, y1), x1 - x0, Math.Max(1, Math.Abs(y1 - y0)));
            int alpha = b.Live ? b.FillAlpha : b.FillAlpha / 2;
            if (alpha > 0) ctx.FillRectangle(StudyPalette.A(b.Color, alpha), rect);
            ctx.DrawRectangle(Pen(StudyPalette.A(b.Color, BoxBorderAlpha(b)), 1, b.Style), rect);
            string? t = BoxText(b);
            if (string.IsNullOrEmpty(t)) continue;
            var size = ctx.MeasureString(t, font);
            if (size.Height <= rect.Height + 4) ctx.DrawString(t, font, b.Color, Math.Max(rect.Left + 3, Math.Min(rect.Right - size.Width - 3, right - size.Width - 4)), rect.Top + (rect.Height - size.Height) / 2);
        }
        foreach (var l in _scene.Lines)
        {
            int from = Start(l);
            if (!Visible(from, l.To) || l.To >= 0 && l.To < from || !IsShown(l)) continue;
            bool flat = l.Price == l.EndPrice;
            int x0 = X(from), x1 = l.To < 0 ? (flat ? right : X(lastBar, false)) : X(l.To, false), y0 = Y(l.Price), y1 = Y(l.EndPrice);
            ctx.DrawLine(Pen(l.Color, LineWidth(l), l.Style), x0, y0, Math.Max(x0 + 1, x1), y1);
            string? t = LineLabel(l);
            if (string.IsNullOrEmpty(t)) continue;
            var size = ctx.MeasureString(t, font);
            if (LabelAtRight(l)) ctx.DrawString(t, font, l.Color, Math.Min(x1 + 3, right - size.Width - 2), y1 - size.Height - 1);
            else ctx.DrawString(t, font, l.Color, Math.Max(region.Left + 2, x0 + 2), y0 - size.Height - 1);
        }
        foreach (var p in _scene.Paths)
        {
            if (p.Points.Count < 2 || p.Points[^1].Bar < first || p.Points[0].Bar > last + 1 || !IsShown(p)) continue;
            ctx.DrawLines(Pen(p.Color, p.Width, p.Style), p.Points.Select(q => new Point(X(q.Bar, false), Y(q.Price))).ToArray());
        }
        int ms = Math.Clamp(bw / 2 + 2, 4, 9);
        foreach (var m in _scene.Marks)
        {
            int bar = _retrospective ? m.Bar : Math.Max(m.Bar, m.Available);
            if (bar < first || bar > last || !IsShown(m)) continue;
            int x = X(bar, false), y = Y(m.Price) + (m.Above ? -ms - 3 : ms + 3);
            DrawMark(ctx, m.Kind, x, y, ms, m.Color);
            if (string.IsNullOrEmpty(m.Text)) continue;
            string t = UiText.Tr(m.Text); var size = ctx.MeasureString(t, font);
            ctx.DrawString(t, font, m.Color, x - size.Width / 2, m.Above ? y - ms - size.Height - 1 : y + ms + 1);
        }
        foreach (var tag in _scene.Tags)
        {
            int bar = _retrospective ? tag.Bar : Math.Max(tag.Bar, tag.Available);
            if (bar < first || bar > last || !IsShown(tag)) continue;
            string text = UiText.Tr(tag.Text); var size = ctx.MeasureString(text, font);
            int x = Math.Clamp(X(bar, false) - size.Width / 2 - 4, region.Left + 2, Math.Max(region.Left + 2, right - size.Width - 10));
            int y = tag.Above ? Y(tag.Price) - size.Height - 10 : Y(tag.Price) + 6;
            ctx.FillRectangle(StudyPalette.A(tag.Back, 210), new Rectangle(x, y, size.Width + 8, size.Height + 4), 3);
            ctx.DrawString(text, font, Color.White, x + 4, y + 2);
        }
        if (_dash && _scene.Dash.Count > 0) DrawDash(ctx, region);
        RenderExtra(ctx, region);
        FootprintStudyData.Legend(ctx, region, 2, UiText.Tr(Status), Color.LightSlateGray);
    }

    /// <summary>Additional drawing (panels, maps) after the scene.</summary>
    protected virtual void RenderExtra(RenderContext ctx, Rectangle region) { }

    /// <summary>
    /// Display-only visibility filters (for example "only the last test" or "hide filled"). They may hide a drawing on
    /// the chart but never change detection, confirmation timing, strategy series or the alert levels of still-valid levels.
    /// </summary>
    public virtual bool IsShown(StudyBox box) => true;
    /// <inheritdoc cref="IsShown(StudyBox)"/>
    public virtual bool IsShown(StudyLine line) => true;
    /// <inheritdoc cref="IsShown(StudyBox)"/>
    public virtual bool IsShown(StudyMark mark) => true;
    /// <inheritdoc cref="IsShown(StudyBox)"/>
    public virtual bool IsShown(StudyTag tag) => true;
    /// <inheritdoc cref="IsShown(StudyBox)"/>
    public virtual bool IsShown(StudyPath path) => true;
    /// <inheritdoc cref="IsShown(StudyBox)"/>
    public virtual bool IsTintShown(int bar, Color color) => true;

    /// <summary>Display-only border opacity of a box (0–255).</summary>
    protected virtual int BoxBorderAlpha(StudyBox box) => box.Live ? 150 : 70;

    /// <summary>Display-only box caption (translated); default: the box text.</summary>
    public virtual string? BoxText(StudyBox box) => box.Text is { Length: > 0 } text ? UiText.Tr(text) : null;

    /// <summary>Display-only line caption (translated); default: the label plus the price of a flat level.</summary>
    public virtual string? LineLabel(StudyLine line) =>
        string.IsNullOrEmpty(line.Label) ? null : UiText.Tr(line.Label) + (line.Price == line.EndPrice ? " " + Price(line.Price) : "");

    /// <summary>Display-only caption side: at the line's right end (default) or at its start.</summary>
    public virtual bool LabelAtRight(StudyLine line) => true;

    /// <summary>Display-only stroke width of a line.</summary>
    public virtual int LineWidth(StudyLine line) => line.Width;

    internal static void DrawMark(RenderContext ctx, StudyMarkKind kind, int x, int y, int r, Color c)
    {
        switch (kind)
        {
            case StudyMarkKind.TriangleUp: ctx.FillPolygon(c, new[] { new Point(x, y - r), new Point(x + r, y + r), new Point(x - r, y + r) }); break;
            case StudyMarkKind.TriangleDown: ctx.FillPolygon(c, new[] { new Point(x, y + r), new Point(x + r, y - r), new Point(x - r, y - r) }); break;
            case StudyMarkKind.Diamond: ctx.FillPolygon(c, new[] { new Point(x, y - r), new Point(x + r, y), new Point(x, y + r), new Point(x - r, y) }); break;
            case StudyMarkKind.Square: ctx.FillRectangle(c, new Rectangle(x - r + 1, y - r + 1, 2 * r - 2, 2 * r - 2)); break;
            case StudyMarkKind.Cross:
                ctx.DrawLine(new RenderPen(c, 2), x - r, y - r, x + r, y + r); ctx.DrawLine(new RenderPen(c, 2), x - r, y + r, x + r, y - r); break;
            default: ctx.FillEllipse(c, new Rectangle(x - r + 1, y - r + 1, 2 * r - 2, 2 * r - 2)); break;
        }
    }

    private void DrawDash(RenderContext ctx, Rectangle region)
    {
        var font = Font(); var bold = Font(true);
        var rows = _scene.Dash.Select(r => (Name: UiText.Tr(r.Name), Value: UiText.Tr(r.Value), r.Color)).ToList();
        string? title = _scene.DashTitle == null ? null : UiText.Tr(_scene.DashTitle);
        int nameW = rows.Max(r => ctx.MeasureString(r.Name, font).Width), valW = rows.Max(r => ctx.MeasureString(r.Value, bold).Width);
        int rowH = ctx.MeasureString("Ag", font).Height + 3, titleW = title == null ? 0 : ctx.MeasureString(title, bold).Width;
        int w = Math.Max(titleW, nameW + valW + 18) + 16, h = rows.Count * rowH + (title != null ? rowH + 2 : 0) + 10;
        int col = (int)_corner % 3, row = (int)_corner / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 70 };
        ctx.FillRectangle(Color.FromArgb(215, 18, 20, 26), new Rectangle(x, y, w, h), 5);
        ctx.DrawRectangle(new RenderPen(Color.FromArgb(90, 120, 130, 150), 1), new Rectangle(x, y, w, h), 5);
        int cy = y + 5;
        if (title != null) { ctx.DrawString(title, bold, Color.FromArgb(235, 235, 240), x + 8, cy); cy += rowH + 2; }
        foreach (var r in rows)
        {
            ctx.DrawString(r.Name, font, Color.FromArgb(160, 165, 175), x + 8, cy);
            var vs = ctx.MeasureString(r.Value, bold);
            ctx.DrawString(r.Value, bold, r.Color.A > 0 ? r.Color : Color.FromArgb(235, 235, 240), x + w - vs.Width - 8, cy);
            cy += rowH;
        }
    }
}
