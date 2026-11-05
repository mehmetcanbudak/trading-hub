using SkiaSharp;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Charting.Drawings;

public sealed partial class ChartDrawing
{
    public decimal AccountSize { get; set; }
    public decimal RiskPercent { get; set; } = 1;
    public string AccountCurrency { get; set; } = "";
    /// <summary>Explicit linear P&L currency confirmation when instrument metadata has no quote currency.</summary>
    public string PnlCurrency { get; set; } = "";
    public decimal RoundTripFeePerUnit { get; set; }
    public double Band1 { get; set; } = 1;
    public double Band2 { get; set; } = 2;
    public int ProfileRows { get; set; } = 120;
    public int ValueAreaPercent { get; set; } = 70;
    public bool ProfileDelta { get; set; }
    public int ProfileWidthPercent { get; set; } = 35;
    public bool ProfileWholeSessions { get; set; }


    public sealed record PositionSize(bool IsValid, decimal Quantity, decimal Risk, decimal Reward, decimal Budget, string Currency, string Limitation);

    /// <summary>Visual-only linear point-value sizing. Never reads an account balance or creates an order.</summary>
    public PositionSize PositionSizing(Instrument instrument)
    {
        const string exclusions = "Linear estimate; excludes FX, slippage, funding and taxes. No orders.";
        PositionSize Invalid(string reason) => new(false, 0, 0, 0, 0, "", reason);
        if (!IsPosition || Points.Count < 3) return Invalid("Entry, stop and target are required");
        if (instrument.PointValue <= 0 || instrument.VolumeStep <= 0) return Invalid("Point value and quantity increment are required");
        if (RoundTripFeePerUnit < 0) return Invalid("Round-trip fees cannot be negative");
        bool isLong = Kind == DrawingKind.LongPosition;
        double entry = Points[0].Price, target = Points[1].Price, stop = Points[2].Price;
        if (!double.IsFinite(entry) || !double.IsFinite(target) || !double.IsFinite(stop) ||
            (isLong ? stop >= entry || target <= entry : stop <= entry || target >= entry))
            return Invalid("Stop and target must be on opposite, direction-correct sides of entry");
        try
        {
            decimal lossPerUnit = Math.Abs((decimal)entry - (decimal)stop) * instrument.PointValue + RoundTripFeePerUnit;
            decimal gainPerUnit = Math.Abs((decimal)target - (decimal)entry) * instrument.PointValue - RoundTripFeePerUnit;
            string currency = ((string.IsNullOrWhiteSpace(instrument.QuoteAsset) ? PnlCurrency : instrument.QuoteAsset) ?? "").Trim().ToUpperInvariant();
            bool riskSized = AccountSize > 0;
            if (riskSized && (RiskPercent <= 0 || RiskPercent > 100)) return Invalid("Risk must be greater than 0% and at most 100%");
            if (riskSized && (currency.Length == 0 || !string.Equals((AccountCurrency ?? "").Trim(), currency, StringComparison.OrdinalIgnoreCase)))
                return Invalid("Account and linear P&L currencies must match; FX conversion is not available");
            decimal budget = riskSized ? AccountSize * RiskPercent / 100m : 0;
            decimal requested = riskSized ? budget / lossPerUnit : Math.Max(0, Quantity);
            decimal quantity = decimal.Floor(requested / instrument.VolumeStep) * instrument.VolumeStep;
            string note = RoundTripFeePerUnit == 0 ? "Fees excluded. " + exclusions : "Includes entered round-trip fees only. " + exclusions;
            if (currency.Length == 0) { currency = "P&L units"; note = "Currency unverified. " + note; }
            if (quantity == 0) note = "Below one quantity increment. " + note;
            return new(true, quantity, quantity * lossPerUnit, quantity * gainPerUnit, budget, currency, note);
        }
        catch (OverflowException) { return Invalid("Sizing exceeds decimal precision/range; reduce the inputs"); }
    }


    private void RenderTradeDrawing(SKCanvas canvas, ChartLayout layout, CandleSeries series, TradeStore trades, ChartTheme theme,
        int decimals, SKFont font, SKPaint stroke, long generation, bool concealSource, ChartDrawingRuntime runtime)
    {
        runtime.Layout = layout;
        runtime.ProfileGeometry = null;
        runtime.VwapGeometry.Clear();
        runtime.VwapAnchor = null;
        try
        {
            if (Kind == DrawingKind.AnchoredVwap) RenderVwap(canvas, layout, series, trades, theme, decimals, font, stroke, generation, runtime);
            else RenderProfile(canvas, layout, series, trades, theme, decimals, font, stroke, generation, concealSource, runtime);
        }
        catch (OverflowException)
        {
            runtime.VwapGeometry.Clear();
            runtime.VwapAnchor = null;
            DrawBox(canvas, font, layout.Main.Rect.MidX, layout.Main.Rect.Top + 40, theme.Text, "Drawing exceeds decimal precision/range; adjust its range");
        }
    }

    private void RenderVwap(SKCanvas canvas, ChartLayout layout, CandleSeries series, TradeStore trades, ChartTheme theme,
        int decimals, SKFont font, SKPaint stroke, long generation, ChartDrawingRuntime runtime)
    {
        var result = runtime.CalculateVwap(this, trades, series, generation);
        runtime.UpdateVwapGeometry(this, layout, series, result.Points);
        if (result.Points.Count == 0)
        {
            DrawBox(canvas, font, XOf(layout, series, Points[0].Time), layout.Main.Y(Points[0].Price), theme.Text, "Anchored VWAP · no trade coverage");
            return;
        }
        using var path = new SKPath();
        void Line(double deviation)
        {
            path.Reset();
            bool started = false;
            for (int i = 0; i < runtime.VwapGeometry.Count; i++)
            {
                var point = runtime.VwapGeometry[i];
                float x = point.Center.X, y = point.Center.Y + point.DeviationPixels * (float)deviation;
                if (!started) { path.MoveTo(x, y); started = true; }
                else path.LineTo(x, y);
            }
            canvas.DrawPath(path, stroke);
        }
        Line(0);
        stroke.Color = Color.WithAlpha(140);
        if (double.IsFinite(Band1) && Band1 > 0) { Line(Band1); Line(-Band1); }
        if (double.IsFinite(Band2) && Band2 > 0) { Line(Band2); Line(-Band2); }
        stroke.Color = Color;
        var last = result.Points[^1];
        string caption = $"Anchored VWAP {Format((double)last.Vwap, decimals)} · {result.Coverage.Description}";
        if (result.LineDecimated) caption += " · line sampled, totals complete";
        using var text = new SKPaint { Color = Color, IsAntialias = true };
        canvas.DrawUiText(caption, layout.Main.Rect.Left + 8, layout.Main.Rect.Bottom - 8, font, text);
    }

    private void RenderProfile(SKCanvas canvas, ChartLayout layout, CandleSeries series, TradeStore trades, ChartTheme theme,
        int decimals, SKFont font, SKPaint stroke, long generation, bool concealSource, ChartDrawingRuntime runtime)
    {
        var result = runtime.CalculateProfile(this, trades, series, generation);
        float start = XOf(layout, series, Points[0].Time), end = XOf(layout, series, Points[1].Time);
        float left = Math.Max(layout.Main.Rect.Left, Math.Min(start, end)), right = Math.Min(layout.Main.Rect.Right, Math.Max(start, end));
        stroke.Color = Color.WithAlpha(150);
        canvas.DrawLine(start, layout.Main.Rect.Top, start, layout.Main.Rect.Bottom, stroke);
        canvas.DrawLine(end, layout.Main.Rect.Top, end, layout.Main.Rect.Bottom, stroke);
        if (result == null)
        {
            DrawBox(canvas, font, (left + right) / 2, layout.Main.Rect.Top + 40, theme.Text, "Fixed-range profile · no trade coverage");
            return;
        }
        if (right <= left) return;
        decimal maximum = result.MaxVolume;
        if (ProfileDelta)
        {
            maximum = 0;
            foreach (var row in result.Rows) maximum = Math.Max(maximum, Math.Abs(row.Delta));
        }
        float width = Math.Max(1, (right - left) * Math.Clamp(ProfileWidthPercent, 5, 100) / 100f);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
        foreach (var row in result.Rows)
        {
            float top = layout.Main.Y(row.Price + result.RowSize), bottom = layout.Main.Y(row.Price);
            decimal value = ProfileDelta ? Math.Abs(row.Delta) : row.Volume;
            float size = maximum > 0 ? (float)(value / maximum) * width : 0;
            fill.Color = ProfileDelta ? (row.Delta >= 0 ? theme.UpCandle : theme.DownCandle).WithAlpha(155) : Color.WithAlpha(row.Price == result.Poc ? (byte)220 : (byte)110);
            canvas.DrawRect(new SKRect(left, top, left + size, Math.Max(top + 1, bottom)), fill);
        }
        runtime.ProfileGeometry = new SKRect(left, layout.Main.Y(result.High + series.Instrument.TickSize), left + width, layout.Main.Y(result.Low));
        stroke.Color = Color;
        foreach (decimal price in new[] { result.Poc, result.Vah, result.Val })
            canvas.DrawLine(left, layout.Main.Y(price), right, layout.Main.Y(price), stroke);
        if (ShowPriceLabel)
            DrawBox(canvas, font, (left + right) / 2, layout.Main.Rect.Top + 32, Color,
                $"{(ProfileDelta ? "Delta" : "Volume")} profile · POC {Format((double)result.Poc, decimals)} · VA {Math.Clamp(ValueAreaPercent, 1, 100)}%",
                $"Volume {result.Total:0.########} · {result.Coverage.Description}",
                $"{(concealSource ? "Practice source" : result.Coverage.SourceIdentity)} · inclusive endpoints");
        if (!ShowPriceLabel)
        {
            using var text = new SKPaint { Color = Color, IsAntialias = true };
            canvas.DrawUiText(result.Coverage.Description, layout.Main.Rect.Left + 8, layout.Main.Rect.Bottom - 8, font, text);
        }
    }
}
