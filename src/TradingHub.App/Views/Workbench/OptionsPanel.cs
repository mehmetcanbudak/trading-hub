using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using SkiaSharp;
using TradingHub.App.Services.Workbench;
using TradingHub.App.Windows;
using TradingHub.Feeds.External;

namespace TradingHub.App.Views.Workbench;

/// <summary>
/// Options dashboard from the configured options-data connection: GEX by strike, total GEX versus price with the gamma
/// flip, call/put walls, max pain, expected move, IV term structure and smile, a per-expiry table and large option
/// trades. Without an options-data connection it shows how to connect one and makes no requests.
/// </summary>
internal sealed class OptionsPanel : WorkbenchPanel
{
    private readonly Border _connect;
    private readonly TextBlock _connectText;
    private readonly StackPanel _body;
    private readonly WrapPanel _kpis = new() { Name = "OptionsKpis", Orientation = Orientation.Horizontal };
    private readonly OptionsChartsCanvas _charts = new() { Name = "OptionsCharts", Height = 420 };
    private readonly Grid _expiries = new() { Name = "OptionsExpiries" };
    private readonly StackPanel _trades = new() { Name = "OptionsTrades", Spacing = 2 };
    private readonly TextBlock _status = new() { Name = "OptionsStatus", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private string _key = "";
    private DateTime _lastCheck;

    public OptionsPanel(WorkbenchContext context) : base(context)
    {
        var s = context.Settings;
        _connect = Banner("OptionsConnect", out _connectText);
        var open = Ui.Button("Open Connections", MaterialIconKind.LanConnect, () => Navigate("connections"));
        open.Name = "OptionsOpenConnections";
        var connectBox = Ui.V(8, _connect, open);
        var expiry = new Ui.Segmented<int>(new (int, string, string?)[] { (0, "All expiries", null), (7, "Within 7 days", null), (30, "Within 30 days", null) }) { Name = "OptionsExpiryFilter" };
        expiry.Select(s.OptionsMaxDays ?? 0);
        expiry.SelectionChanged += d => { s.OptionsMaxDays = d == 0 ? null : d; context.Save(); _key = ""; Update(); };
        var range = new Ui.Segmented<double>(new (double, string, string?)[] { (10, "±10%", null), (20, "±20%", null), (35, "±35%", null) }) { Name = "OptionsRange" };
        range.Select(s.OptionsRangePercent is 10 or 20 or 35 ? s.OptionsRangePercent : 20);
        range.SelectionChanged += r => { s.OptionsRangePercent = r; context.Save(); _key = ""; Update(); };
        var large = Number("OptionsLargePremium", s.OptionsLargeTradePremium, 0, 1_000_000_000, 50_000, "0", v => { s.OptionsLargeTradePremium = v; context.Save(); _key = ""; Update(); });
        for (int c = 0; c < 10; c++) _expiries.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _body = Ui.V(10, Ui.H(12, expiry, range, Ui.Field("Large trades from (premium)", large)), _kpis, _charts,
            Ui.H2("By expiry"), Note("Max pain = the settlement price where option holders' total intrinsic value is lowest. Expected move = spot × ATM IV × √time (one standard deviation)."),
            new ScrollViewer { Content = _expiries, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            Ui.H2("Large option trades (24 hours)"), _trades,
            Note("GEX is an estimate from public open interest under the customers-buy-puts / sell-calls convention (USD per 1% move), not dealer books."));
        var root = new DockPanel();
        DockPanel.SetDock(connectBox, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        connectBox.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(connectBox);
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { Content = _body });
        Content = root;
    }

    public override string Key => "options";
    public override string Title => "Options";
    public override MaterialIconKind Icon => MaterialIconKind.Sigma;
    internal OptionsSummary? Summary { get; private set; }

    private void Navigate(string page)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow main) main.Navigate(page);
        else if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow owner }) owner.Navigate(page);
    }

    protected override void OnSelectionChanged()
    {
        _key = "";
        Update();
    }

    protected override void OnActivated()
    {
        _key = "";
        Update();
    }

    public override void Tick(DateTime now)
    {
        if (now - _lastCheck >= TimeSpan.FromSeconds(1)) Update();
    }

    private void Update()
    {
        _lastCheck = Context.Clock();
        var hub = Context.Options();
        string underlying = Selection?.Underlying ?? "";
        string? unavailable = hub == null ? OptionsData.NotConfigured : Selection == null ? "Choose an instrument" : hub.Unavailable(underlying);
        bool notConfigured = hub?.Provider == null;
        _connect.IsVisible = unavailable != null;
        _connectText.Text = notConfigured
            ? "Connect options data: add an Options data connection (Deribit public data or your own JSON API) in Connections. Until then this panel makes no requests."
            : unavailable ?? "";
        if (_connect.Parent is Control box) box.IsVisible = unavailable != null;
        _body.IsVisible = unavailable == null;
        if (unavailable != null || hub == null || !IsActive)
        {
            Summary = null;
            _status.Text = "";
            return;
        }
        var chain = hub.Chain(underlying);
        var now = Context.Clock();
        var trades = hub.TradesBetween(underlying, now.AddHours(-24), now);
        string key = $"{underlying}|{hub.Generation}|{chain?.Time.Ticks}";
        if (key == _key) return;
        _key = key;
        if (chain == null)
        {
            Summary = null;
            _status.Text = hub.ChainError(underlying) is { } error ? "Options data: " + error : $"Loading the {underlying} options chain…";
            _kpis.Children.Clear();
            _charts.Set(null);
            return;
        }
        var s = Context.Settings;
        var summary = OptionsDashboard.Build(chain, s.OptionsRangePercent, s.OptionsMaxDays);
        Summary = summary;
        Kpis(summary);
        _charts.Set(summary);
        Expiries(summary);
        Trades(trades, (double)s.OptionsLargeTradePremium, hub.TradeCoverageNote(underlying, now.AddHours(-24), now));
        _status.Text = $"{summary.Source} · {summary.Underlying} · {summary.Contracts} contracts · snapshot {Ui.Ago(summary.Time)}"
            + (summary.Contracts == 0 ? " · no contracts in the selected expiries" : "");
    }

    private void Kpis(OptionsSummary s)
    {
        _kpis.Children.Clear();
        void Kpi(string label, string value, IBrush? color = null)
        {
            var v = new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeight.SemiBold };
            if (color != null) v.Foreground = color;
            _kpis.Children.Add(new Border { Child = Ui.V(2, Ui.Text(label, "muted", "small"), v), Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 8, 8) }.Classes("card"));
        }
        Kpi("Spot", WorkbenchFormat.Price(s.Spot));
        Kpi("Total GEX", WorkbenchFormat.Compact(s.TotalGex), s.TotalGex >= 0 ? Ui.Up : Ui.Down);
        Kpi("Gamma flip", s.GammaFlip is { } f ? WorkbenchFormat.Price(f) : "none within ±20%");
        Kpi("Call wall", WorkbenchFormat.Price(s.CallWall), Ui.Up);
        Kpi("Put wall", WorkbenchFormat.Price(s.PutWall), Ui.Down);
        Kpi("Max pain (nearest expiry)", WorkbenchFormat.Price(s.MaxPain));
        Kpi("Expected move (nearest expiry)", s.ExpectedMove is { } m ? "±" + WorkbenchFormat.Price(m) : WorkbenchFormat.Missing);
    }

    private void Expiries(OptionsSummary s)
    {
        _expiries.Children.Clear();
        _expiries.RowDefinitions.Clear();
        string[] headers = { "Expiry", "Days", "Call OI", "Put OI", "Put/call OI", "Max pain", "ATM IV", "25Δ risk reversal", "25Δ butterfly", "Expected move" };
        _expiries.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < headers.Length; c++) Cell(headers[c], 0, c, true);
        int r = 1;
        foreach (var e in s.Expiries.Take(16))
        {
            _expiries.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell(e.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r, 0);
            Cell(e.DaysLeft.ToString("0.#", CultureInfo.InvariantCulture), r, 1);
            Cell(WorkbenchFormat.Compact(e.CallOi), r, 2);
            Cell(WorkbenchFormat.Compact(e.PutOi), r, 3);
            Cell(e.PutCallOi is { } pcr ? pcr.ToString("0.00", CultureInfo.InvariantCulture) : WorkbenchFormat.Missing, r, 4);
            Cell(WorkbenchFormat.Price(e.MaxPain), r, 5);
            Cell(e.AtmIv is { } iv ? iv.ToString("0.0", CultureInfo.InvariantCulture) + "%" : WorkbenchFormat.Missing, r, 6);
            Cell(e.RiskReversal25 is { } rr ? rr.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) : WorkbenchFormat.Missing, r, 7);
            Cell(e.Butterfly25 is { } fly ? fly.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) : WorkbenchFormat.Missing, r, 8);
            Cell(e.ExpectedMove is { } em ? "±" + WorkbenchFormat.Price(em) : WorkbenchFormat.Missing, r, 9);
            r++;
        }
    }

    private void Cell(string text, int row, int column, bool header = false)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(column == 0 ? 2 : 16, 3, 2, 3), HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        if (header) { t.Classes.Add("muted"); t.FontWeight = FontWeight.SemiBold; }
        Grid.SetRow(t, row);
        Grid.SetColumn(t, column);
        _expiries.Children.Add(t);
    }

    private void Trades(IReadOnlyList<OptionTrade> trades, double threshold, string? coverage)
    {
        _trades.Children.Clear();
        var large = trades.Where(t => t.Premium >= threshold).OrderByDescending(t => t.Time).Take(30).ToArray();
        if (coverage != null) _trades.Children.Add(Note(coverage));
        if (large.Length == 0)
        {
            _trades.Children.Add(Note("No option trades above the threshold in the loaded window."));
            return;
        }
        foreach (var t in large)
        {
            string side = t.Aggressor > 0 ? "bought" : t.Aggressor < 0 ? "sold" : "traded";
            string right = t.Right == OptionRight.Call ? "call" : "put";
            var line = new TextBlock
            {
                Text = $"{t.Time.ToLocalTime():MM-dd HH:mm} · {t.Venue} · {side} {t.Size:0.##} × {t.Strike.ToString("0.##", CultureInfo.InvariantCulture)} {right} {t.Expiry:yyyy-MM-dd} · premium {WorkbenchFormat.Compact(t.Premium)}",
                Foreground = t.Bias > 0 ? Ui.Up : t.Bias < 0 ? Ui.Down : Ui.Text1, FontSize = 12,
            };
            _trades.Children.Add(line);
        }
    }
}

/// <summary>GEX by strike, total GEX versus price, IV smile and term structure.</summary>
internal sealed class OptionsChartsCanvas : WorkbenchCanvas
{
    private OptionsSummary? _summary;

    public void Set(OptionsSummary? summary)
    {
        _summary = summary;
        InvalidateVisual();
    }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        LoadColors();
        canvas.Clear(Bg);
        if (_summary is not { } s)
        {
            Label(canvas, "Waiting for the options chain", 10, 22, Text);
            return;
        }
        float half = width / 2 - 6;
        var gex = new SKRect(6, 6, half, height - 6);
        var curve = new SKRect(half + 12, 6, width - 6, height / 3 - 4);
        var smile = new SKRect(half + 12, height / 3 + 4, width - 6, height * 2 / 3 - 4);
        var term = new SKRect(half + 12, height * 2 / 3 + 4, width - 6, height - 6);
        using var frame = new SKPaint { Color = Grid, Style = SKPaintStyle.Stroke, IsAntialias = true };
        foreach (var r in new[] { gex, curve, smile, term }) canvas.DrawRoundRect(r, 5, 5, frame);

        // ---- GEX by strike (horizontal bars around zero, strikes top-down)
        Label(canvas, "Gamma exposure by strike (USD per 1%)", gex.Left + 8, gex.Top + 15, Text, 10.5f);
        var rows = s.Strikes;
        if (rows.Count > 0)
        {
            var plot = new SKRect(gex.Left + 70, gex.Top + 24, gex.Right - 8, gex.Bottom - 8);
            double max = Math.Max(1, rows.Max(r => Math.Abs(r.Gex)));
            float rowH = plot.Height / rows.Count;
            float zero = plot.MidX;
            using var bar = new SKPaint();
            using var axis = new SKPaint { Color = Grid };
            canvas.DrawLine(zero, plot.Top, zero, plot.Bottom, axis);
            double lo = rows[0].Strike, hi = rows[^1].Strike;
            float YStrike(double k) => plot.Bottom - (float)((k - lo) / Math.Max(1e-9, hi - lo)) * plot.Height;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                float y = YStrike(r.Strike) - rowH / 2, w = (float)(Math.Abs(r.Gex) / max) * (plot.Width / 2);
                bar.Color = (r.Gex >= 0 ? Up : Down).WithAlpha(190);
                canvas.DrawRect(r.Gex >= 0 ? zero : zero - w, y + 0.5f, w, Math.Max(1, rowH - 1), bar);
                if (rows.Count <= 40 || i % (rows.Count / 30 + 1) == 0)
                    Label(canvas, WorkbenchFormat.Price(r.Strike), plot.Left - 6, y + rowH / 2 + 4, Text, 9.5f, right: true);
            }
            int labels = 0;
            void Level(double? price, SKColor color, string name)
            {
                if (price is not { } p || p < lo || p > hi) return;
                using var line = new SKPaint { Color = color, StrokeWidth = 1.2f, PathEffect = SKPathEffect.CreateDash(new[] { 5f, 3f }, 0), IsAntialias = true };
                canvas.DrawLine(plot.Left, YStrike(p), plot.Right, YStrike(p), line);
                // staggered so levels at the same strike stay readable
                Label(canvas, name, plot.Right - 2 - 62 * labels++, YStrike(p) - 3, color, 9.5f, true, right: true);
            }
            Level(s.Spot, Strong, "spot");
            Level(s.GammaFlip, Warn, "flip");
            Level(s.CallWall, Up, "call wall");
            Level(s.PutWall, Down, "put wall");
            Level(s.MaxPain, Accent, "max pain");
        }
        else Label(canvas, "No strikes in range", gex.Left + 8, gex.MidY, Text, 10.5f);

        // ---- total GEX versus price
        Line(canvas, curve, "Total GEX versus price", s.GexCurve.Select(p => (p.Spot, p.Gex)).ToArray(), s.Spot, Accent, v => WorkbenchFormat.Compact(v), true);
        Line(canvas, smile, s.SmileExpiry is { } se ? $"IV smile · {se:yyyy-MM-dd}" : "IV smile", s.Smile.Select(p => (p.Strike, p.Iv * 100)).ToArray(), s.Spot, Warn, v => v.ToString("0.0", CultureInfo.InvariantCulture) + "%", false);
        Line(canvas, term, "IV term structure (ATM, by days to expiry)", s.Term.Select(p => (p.Days, p.AtmIv * 100)).ToArray(), null, Up, v => v.ToString("0.0", CultureInfo.InvariantCulture) + "%", false);
    }

    private void Line(SKCanvas canvas, SKRect rect, string title, IReadOnlyList<(double X, double Y)> points, double? marker, SKColor color, Func<double, string> format, bool zero)
    {
        Label(canvas, title, rect.Left + 8, rect.Top + 15, Text, 10.5f);
        if (points.Count < 2)
        {
            Label(canvas, "Not enough data", rect.Left + 8, rect.MidY + 6, Text, 10.5f);
            return;
        }
        var plot = new SKRect(rect.Left + 8, rect.Top + 22, rect.Right - 60, rect.Bottom - 6);
        double x0 = points.Min(p => p.X), x1 = points.Max(p => p.X), y0 = points.Min(p => p.Y), y1 = points.Max(p => p.Y);
        if (zero) { y0 = Math.Min(y0, 0); y1 = Math.Max(y1, 0); }
        if (x1 - x0 < 1e-12) x1 = x0 + 1;
        if (y1 - y0 < 1e-12) { y1 += 1; y0 -= 1; }
        float X(double x) => plot.Left + (float)((x - x0) / (x1 - x0)) * plot.Width;
        float Y(double y) => plot.Bottom - (float)((y - y0) / (y1 - y0)) * plot.Height;
        using var grid = new SKPaint { Color = Grid };
        if (zero) canvas.DrawLine(plot.Left, Y(0), plot.Right, Y(0), grid);
        if (marker is { } m && m >= x0 && m <= x1)
        {
            using var mk = new SKPaint { Color = Strong.WithAlpha(120), PathEffect = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0) };
            canvas.DrawLine(X(m), plot.Top, X(m), plot.Bottom, mk);
        }
        using var line = new SKPaint { Color = color, StrokeWidth = 1.6f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var path = new SKPath();
        for (int i = 0; i < points.Count; i++)
            if (i == 0) path.MoveTo(X(points[i].X), Y(points[i].Y)); else path.LineTo(X(points[i].X), Y(points[i].Y));
        canvas.DrawPath(path, line);
        Label(canvas, format(y1), rect.Right - 4, plot.Top + 9, Text, 9.5f, right: true);
        Label(canvas, format(y0), rect.Right - 4, plot.Bottom, Text, 9.5f, right: true);
    }
}
