using SkiaSharp;
using TradingHub.Indicators;

namespace TradingHub.Charting.Rendering;

/// <summary>Velas, barras OHLC y línea.</summary>
internal sealed class CandleRenderer
{
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeJoin = SKStrokeJoin.Round };

    public void Render(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout main, ChartStyle style, Dictionary<int, SKColor>? paintBars)
    {
        var series = model.Series;
        var t = model.Theme;
        if (series.Count == 0) return;
        if (layout.BarWidth < 1)
        {
            RenderDense(canvas, model, layout, main, style, paintBars);
            return;
        }

        if (style == ChartStyle.Line)
        {
            using var path = new SKPath();
            bool first = true;
            for (int i = Math.Max(0, layout.FirstVisible); i <= Math.Min(layout.LastVisible, series.Count - 1); i++)
            {
                var c = series[i];
                float x = layout.CenterX(i), y = main.Y(series.TicksToPrice(c.CloseTicks));
                if (first) { path.MoveTo(x, y); first = false; }
                else path.LineTo(x, y);
            }

            _line.Color = t.DrawingDefault;
            canvas.DrawPath(path, _line);
            return;
        }

        float slot = (float)layout.BarWidth;
        float bodyW = Math.Max(1, slot - layout.BarSpacing);
        bool ha = style == ChartStyle.HeikinAshi;
        decimal haOpen = 0, haClose = 0;
        if (ha)
        {
            // Heikin Ashi: la apertura es recursiva; arrancar 300 velas antes alcanza para converger
            int start = Math.Max(0, layout.FirstVisible - 300);
            for (int i = start; i < Math.Min(layout.FirstVisible, series.Count); i++) NextHa(series, i, start, ref haOpen, ref haClose, out _, out _);
        }

        // la serie se puede achicar entre que se calculó el layout y este dibujo (se rearmó con otro período): nunca fuera de rango
        int last = Math.Min(layout.LastVisible, series.Count - 1);
        for (int i = Math.Max(0, layout.FirstVisible); i <= last; i++)
        {
            var c = series[i];
            decimal o = series.TicksToPrice(c.OpenTicks), h = series.TicksToPrice(c.HighTicks), l = series.TicksToPrice(c.LowTicks), cl = series.TicksToPrice(c.CloseTicks);
            if (ha)
            {
                NextHa(series, i, Math.Max(0, layout.FirstVisible - 300), ref haOpen, ref haClose, out var haHigh, out var haLow);
                o = haOpen;
                cl = haClose;
                h = haHigh;
                l = haLow;
            }

            if (c.Trades == 0) continue;
            if (paintBars != null && paintBars.TryGetValue(i, out var pcHidden) && pcHidden.Alpha == 0) continue;
            float cx = layout.CenterX(i);
            float yH = main.Y(h);
            float yL = main.Y(l);
            float yO = main.Y(o);
            float yC = main.Y(cl);
            bool up = cl >= o;
            var col = up ? t.UpCandle : t.DownCandle;
            if (paintBars != null && paintBars.TryGetValue(i, out var pc)) col = pc;

            float xLine = MathF.Floor(cx) + 0.5f;
            if (style == ChartStyle.Bars)
            {
                _stroke.Color = col;
                _stroke.StrokeWidth = bodyW >= 6 ? 2 : 1;
                canvas.DrawLine(xLine, yH, xLine, yL, _stroke);
                float tick = Math.Max(2, bodyW / 2);
                canvas.DrawLine(xLine - tick, yO, xLine, yO, _stroke);
                canvas.DrawLine(xLine, yC, xLine + tick, yC, _stroke);
                continue;
            }

            _stroke.Color = col;
            _stroke.StrokeWidth = 1;
            canvas.DrawLine(xLine, yH, xLine, yL, _stroke);
            float top = Math.Min(yO, yC), bot = Math.Max(yO, yC);
            if (bot - top < 1) bot = top + 1;
            float left = MathF.Round(cx - bodyW / 2), right = left + MathF.Max(1, MathF.Round(bodyW));
            _fill.Color = col;
            canvas.DrawRect(new SKRect(left, top, right, bot), _fill);
        }
    }

    // Streaming aggregation: one drawable OHLC envelope per occupied pixel, no candle snapshot.
    // Hidden/empty bars contribute nothing. The first visible open, last visible close and
    // every visible high/low survive. A bucket takes its last visible bar's explicit paint;
    // otherwise its aggregate direction chooses the color. Analytical candles are unchanged.
    private void RenderDense(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout main, ChartStyle style, Dictionary<int, SKColor>? paintBars)
    {
        var series = model.Series;
        bool ha = style == ChartStyle.HeikinAshi, line = style == ChartStyle.Line;
        int first = Math.Max(0, layout.FirstVisible), last = Math.Min(layout.LastVisible, series.Count - 1);
        int start = Math.Max(0, first - 300);
        decimal haOpen = 0, haClose = 0;
        if (ha)
            for (int i = start; i < first; i++) NextHa(series, i, start, ref haOpen, ref haClose, out _, out _);
        bool occupied = false, previous = false;
        int pixel = 0;
        decimal open = 0, high = 0, low = 0, close = 0, firstClose = 0, minClose = 0, maxClose = 0;
        float previousX = 0, previousY = 0;
        SKColor? paint = null;
        void Flush()
        {
            if (!occupied) return;
            float x = pixel + 0.5f;
            if (line)
            {
                _stroke.Color = model.Theme.DrawingDefault;
                _stroke.StrokeWidth = 1;
                if (previous) canvas.DrawLine(previousX, previousY, x, main.Y(firstClose), _stroke);
                canvas.DrawLine(x, main.Y(maxClose), x, main.Y(minClose), _stroke);
                previousX = x;
                previousY = main.Y(close);
                previous = true;
                return;
            }
            _stroke.Color = paint ?? (close >= open ? model.Theme.UpCandle : model.Theme.DownCandle);
            _stroke.StrokeWidth = 1;
            canvas.DrawLine(x, main.Y(high), x, main.Y(low), _stroke);
            if (style == ChartStyle.Bars)
            {
                canvas.DrawLine(x - 0.5f, main.Y(open), x, main.Y(open), _stroke);
                canvas.DrawLine(x, main.Y(close), x + 0.5f, main.Y(close), _stroke);
            }
            else
            {
                _fill.Color = _stroke.Color;
                float top = Math.Min(main.Y(open), main.Y(close)), bottom = Math.Max(main.Y(open), main.Y(close));
                canvas.DrawRect(pixel, top, 1, Math.Max(1, bottom - top), _fill);
            }
        }
        for (int i = first; i <= last; i++)
        {
            var c = series[i];
            decimal o = series.TicksToPrice(c.OpenTicks), h = series.TicksToPrice(c.HighTicks),
                l = series.TicksToPrice(c.LowTicks), cl = series.TicksToPrice(c.CloseTicks);
            if (ha)
            {
                NextHa(series, i, start, ref haOpen, ref haClose, out h, out l);
                o = haOpen;
                cl = haClose;
            }
            SKColor? requested = paintBars != null && paintBars.TryGetValue(i, out var color) ? color : null;
            if (c.Trades == 0 || requested is { Alpha: 0 }) continue;
            int nextPixel = (int)MathF.Floor(layout.CenterX(i));
            if (!occupied || nextPixel != pixel)
            {
                Flush();
                pixel = nextPixel;
                open = o;
                high = h;
                low = l;
                firstClose = minClose = maxClose = cl;
                occupied = true;
            }
            else
            {
                high = Math.Max(high, h);
                low = Math.Min(low, l);
                minClose = Math.Min(minClose, cl);
                maxClose = Math.Max(maxClose, cl);
            }
            close = cl;
            paint = requested;
        }
        Flush();
    }

    /// <summary>Avanza el cálculo de Heikin Ashi una vela.</summary>
    private static void NextHa(TradingHub.Data.CandleSeries series, int i, int start, ref decimal haOpen, ref decimal haClose, out decimal haHigh, out decimal haLow)
    {
        var c = series[i];
        decimal o = series.TicksToPrice(c.OpenTicks), h = series.TicksToPrice(c.HighTicks), l = series.TicksToPrice(c.LowTicks), cl = series.TicksToPrice(c.CloseTicks);
        decimal close = (o + h + l + cl) / 4;
        decimal open = i == start ? (o + cl) / 2 : (haOpen + haClose) / 2;
        haOpen = open;
        haClose = close;
        haHigh = Math.Max(h, Math.Max(open, close));
        haLow = Math.Min(l, Math.Min(open, close));
    }
}
