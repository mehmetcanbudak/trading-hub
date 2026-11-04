// Adapted concepts from jjuangutierrez/trading-hub, 8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9.
// Copyright remains with the original contributors. MIT with Commons Clause; see repository LICENSE.
using System.ComponentModel;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

[Flags]
public enum StudyCoverage { Missing = 0, Observed = 1, Estimated = 2, Incomplete = 4, MissingBars = 8 }

internal readonly record struct StudyLevel(decimal Price, decimal Volume, decimal Bid, decimal Ask, decimal Width = 0);

internal static class FootprintStudyData
{
    private static readonly RenderFont LegendFont = new("Arial", 10);
    internal static string Label(StudyCoverage coverage) => (coverage & (StudyCoverage.Observed | StudyCoverage.Estimated)) == 0 ? "Missing footprint (estimation off or zero volume)" :
        ((coverage & StudyCoverage.Observed) != 0 ? "Observed footprint" : "") +
        ((coverage & StudyCoverage.Estimated) != 0 ? ((coverage & StudyCoverage.Observed) != 0 ? " + estimated OHLC" : "Estimated OHLC / aggregate") : "") +
        ((coverage & StudyCoverage.Incomplete) != 0 ? " (incomplete observed contribution)" : "") +
        ((coverage & StudyCoverage.MissingBars) != 0 ? " + missing bars" : "");

    // A single observed row is evidence. Never augment mixed candles with invented residual order flow.
    internal static StudyCoverage Read(IndicatorCandle candle, bool estimate, decimal step, bool floor,
        List<StudyLevel> destination)
    {
        destination.Clear();
        if (candle.HasObservedFootprint)
        {
            foreach (var row in candle.GetAllPriceLevels())
            {
                if (row.Volume < 0 || row.Bid < 0 || row.Ask < 0) throw new InvalidOperationException("Negative footprint volume.");
                destination.Add(new(row.Price, row.Volume, row.Bid, row.Ask));
            }
            return StudyCoverage.Observed | (candle.IsFootprintComplete ? 0 : StudyCoverage.Incomplete);
        }
        if (!estimate || candle.Volume <= 0) return StudyCoverage.Missing;
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        decimal low = candle.Low, high = candle.High;
        if (high < low) throw new InvalidOperationException("Candle high is below low.");
        if (floor)
        {
            // Enlarge the grid by an integer factor, retaining floor buckets even below zero.
            decimal bucketCount = decimal.Floor(high / step) - decimal.Floor(low / step) + 1;
            if (bucketCount > 200) step *= decimal.Ceiling(bucketCount / 199);
            decimal start = decimal.Floor(low / step), end = decimal.Floor(high / step);
            decimal volumeLeft = candle.Volume, bidLeft = candle.Bid, askLeft = candle.Ask;
            for (decimal key = start; key <= end; key++)
            {
                decimal overlap = high == low ? 1 : Math.Max(0, Math.Min(high, (key + 1) * step) - Math.Max(low, key * step));
                if (overlap == 0) continue;
                bool last = high == low || (key + 1) * step >= high;
                decimal share = high == low ? 1 : overlap / (high - low);
                decimal volume = last ? volumeLeft : candle.Volume * share;
                decimal bid = last ? bidLeft : candle.Bid * share, ask = last ? askLeft : candle.Ask * share;
                destination.Add(new(key * step, volume, bid, ask, step));
                volumeLeft -= volume; bidLeft -= bid; askLeft -= ask;
            }
        }
        else
        {
            decimal first = Nearest(low, step), last = Nearest(high, step);
            decimal count = (last - first) / step + 1;
            // Bounded, volume-conserving OHLC sampling; each sample is mapped to the requested grid.
            int samples = (int)Math.Min(200, count);
            decimal left = candle.Volume, bidLeft = candle.Bid, askLeft = candle.Ask;
            for (int i = 0; i < samples; i++)
            {
                decimal price = samples == 1 ? first : Nearest(first + (last - first) * i / (samples - 1), step);
                decimal volume = i == samples - 1 ? left : candle.Volume / samples;
                decimal bid = i == samples - 1 ? bidLeft : candle.Bid / samples;
                decimal ask = i == samples - 1 ? askLeft : candle.Ask / samples;
                destination.Add(new(price, volume, bid, ask));
                left -= volume; bidLeft -= bid; askLeft -= ask;
            }
        }
        return StudyCoverage.Estimated;
    }

    internal static decimal Nearest(decimal price, decimal step) => decimal.Round(price / step, 0, MidpointRounding.AwayFromZero) * step;
    internal static void Legend(RenderContext context, Rectangle area, int row, string text, Color color)
    {
        if (area.Width <= 12 || area.Height < 60) return;
        // Keep fidelity separate from the chart's native indicator legend at the top.
        var bounds = new Rectangle(area.Left + 6, area.Bottom - 60 + row * 18, area.Width - 12, 18);
        context.FillRectangle(Color.FromArgb(220, 20, 24, 32), bounds);
        context.Canvas.Save();
        context.Canvas.ClipRect(new SkiaSharp.SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        context.DrawString(text, LegendFont, color, bounds.Left + 4, bounds.Top + 1);
        context.Canvas.Restore();
    }
}
