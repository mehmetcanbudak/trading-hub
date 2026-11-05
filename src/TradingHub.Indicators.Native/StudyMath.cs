// Stable weighted moments extracted from the adopted AnchoredFlowVwap study, originally adapted from
// jjuangutierrez/trading-hub @ 8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9. Original contributors retain copyright.
// Calendar/causal helpers are independent; KVP capability reference gouqizi2214730-source/trading-hub
// @ 50220ba91613161685f84384825d855cb4dbeb76. MIT with Commons Clause; see LICENSE and THIRD_PARTY_NOTICES.md.
using TradingHub.Market;

namespace TradingHub.Indicators.Native;

/// <summary>Stable weighted population moments; no subtraction of large squared prices.</summary>
public struct WeightedStudyMoments
{
    public decimal Weight { get; private set; }
    public decimal Mean { get; private set; }
    public decimal M2 { get; private set; }
    public void Add(decimal price, decimal weight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weight);
        if (weight == 0) return;
        Merge(new WeightedStudyMoments { Weight = weight, Mean = price });
    }
    public void Merge(WeightedStudyMoments other)
    {
        if (other.Weight == 0) return;
        if (Weight == 0) { this = other; return; }
        checked
        {
            decimal total = Weight + other.Weight, delta = other.Mean - Mean;
            decimal smallerWeight = Math.Min(Weight, other.Weight);
            decimal largerFraction = Weight <= other.Weight ? other.Weight / total : Weight / total;
            // Avoid squaring a wide spread, or rounding a tiny weight * fraction to zero first.
            decimal weightedDelta = delta * smallerWeight, fractionalDelta = delta * largerFraction;
            decimal cross = delta == 0 ? 0 : weightedDelta / smallerWeight == delta && fractionalDelta / largerFraction == delta
                ? weightedDelta * fractionalDelta : ScaledCrossMoment(Math.Abs(delta), smallerWeight, largerFraction);
            M2 += other.M2 + cross;
            Mean += MeanIncrement(delta, other.Weight, total); Weight = total;
        }
    }
    private static decimal MeanIncrement(decimal spread, decimal weight, decimal total)
    {
        if (spread == 0) return 0;
        if (weight <= 1 || Math.Abs(spread) <= decimal.MaxValue / weight)
        {
            decimal product = spread * weight;
            if (product / weight == spread) return product / total;
        }
        // Normalize the complete multiply/divide, not weight/total: a subnormal ratio can still
        // produce a representable price increment, and an overflowing product can have a small quotient.
        int spreadExponent = 0, weightExponent = 0, totalExponent = 0;
        decimal magnitude = Math.Abs(spread);
        Normalize(ref magnitude, ref spreadExponent); Normalize(ref weight, ref weightExponent); Normalize(ref total, ref totalExponent);
        decimal increment = RestoreScale(magnitude * weight / total, spreadExponent + weightExponent - totalExponent);
        return spread < 0 ? -increment : increment;
    }

    private static decimal ScaledCrossMoment(decimal spread, decimal weight, decimal fraction)
    {
        // Rare precision fallback: normalize before multiplication, then round the final scale once.
        // All mantissas stay below 1000; no allocations or wide-price intermediate squares.
        int spreadExponent = 0, weightExponent = 0;
        Normalize(ref spread, ref spreadExponent); Normalize(ref weight, ref weightExponent);
        return RestoreScale(spread * spread * weight * fraction, 2 * spreadExponent + weightExponent);
    }

    private static decimal RestoreScale(decimal mantissa, int exponent)
    {
        Normalize(ref mantissa, ref exponent);
        if (exponent < -29) return 0;
        if (exponent == -29) return mantissa > 5 ? 0.0000000000000000000000000001m : 0;
        if (exponent > 28) throw new OverflowException("Weighted moment exceeds decimal range.");
        decimal power = 1;
        for (int i = 0; i < Math.Abs(exponent); i++) power *= 10;
        return exponent < 0 ? mantissa / power : mantissa * power;
    }

    private static void Normalize(ref decimal value, ref int exponent)
    {
        while (value < 1) { value *= 10; exponent--; }
        while (value >= 10) { value /= 10; exponent++; }
    }
    public decimal Deviation
    {
        get
        {
            if (Weight == 0) return 0;
            decimal variance = M2 / Weight;
            if (variance < -0.000000000000000000000001m) throw new ArithmeticException("Negative weighted variance.");
            return (decimal)Math.Sqrt((double)Math.Max(0, variance));
        }
    }
}

public enum StudyTimeFrame { Chart, Minute, Minutes2, Minutes5, Minutes15, Minutes30, Hour, Hours4, Day, Week, Month, Quarter, Year }
public enum StudyVwapSource { MeasuredPriceLevels, BarHlc3, BarClose }

internal static class StudyMath
{
    internal static readonly StudyTimeFrame[] Horizons = { StudyTimeFrame.Day, StudyTimeFrame.Week, StudyTimeFrame.Month, StudyTimeFrame.Quarter, StudyTimeFrame.Year };
    internal static bool IsPeriodOpening(IndicatorCandle candle, StudyTimeFrame frame)
    {
        if (frame == StudyTimeFrame.Chart) return true;
        var date = candle.SessionDate;
        if (Duration(frame) > TimeSpan.Zero) return candle.Time.Ticks == PeriodKey(candle, frame);
        var first = frame switch
        {
            StudyTimeFrame.Week => date.AddDays(-((int)date.DayOfWeek + 6) % 7),
            StudyTimeFrame.Month => new DateOnly(date.Year, date.Month, 1),
            StudyTimeFrame.Quarter => new DateOnly(date.Year, 1 + (date.Month - 1) / 3 * 3, 1),
            StudyTimeFrame.Year => new DateOnly(date.Year, 1, 1), _ => date
        };
        return candle.Time == candle.SessionStartUtc(first);
    }
    internal static bool CanResolve(string? sourceCode, StudyTimeFrame target)
    {
        if (target == StudyTimeFrame.Chart) return true;
        if (!TradingHub.Data.ChartPeriod.TryParse(sourceCode ?? "", out var source) || !source.IsTimeBased) return false;
        if (source.Type == TradingHub.Data.PeriodType.Months)
            return source.Value == 1 && target is StudyTimeFrame.Month or StudyTimeFrame.Quarter or StudyTimeFrame.Year;
        if (source.Type == TradingHub.Data.PeriodType.Weeks) return source.Value == 1 && target == StudyTimeFrame.Week;
        if (source.Type == TradingHub.Data.PeriodType.Days) return source.Value == 1 && target is StudyTimeFrame.Day or StudyTimeFrame.Week or StudyTimeFrame.Month or StudyTimeFrame.Quarter or StudyTimeFrame.Year;
        var duration = source.ApproxDuration;
        if (duration <= TimeSpan.Zero) return false;
        var selected = Duration(target);
        return selected == TimeSpan.Zero ? duration <= TimeSpan.FromDays(1) :
            selected >= duration && selected.Ticks % duration.Ticks == 0;
    }
    internal static (DateTime Start, DateTime End) PeriodBounds(IndicatorCandle candle, StudyTimeFrame frame)
    {
        if (frame == StudyTimeFrame.Chart) return (candle.Time, candle.AvailableAt > candle.Time ? candle.AvailableAt : candle.Time.AddTicks(1));
        var duration = Duration(frame);
        if (duration > TimeSpan.Zero) { var start = new DateTime(PeriodKey(candle, frame), DateTimeKind.Utc); return (start, start.Add(duration)); }
        var date = candle.SessionDate;
        var first = frame switch
        {
            StudyTimeFrame.Week => date.AddDays(-((int)date.DayOfWeek + 6) % 7),
            StudyTimeFrame.Month => new DateOnly(date.Year, date.Month, 1),
            StudyTimeFrame.Quarter => new DateOnly(date.Year, 1 + (date.Month - 1) / 3 * 3, 1),
            StudyTimeFrame.Year => new DateOnly(date.Year, 1, 1), _ => date
        };
        var end = frame switch
        {
            StudyTimeFrame.Week => first.AddDays(7), StudyTimeFrame.Month => first.AddMonths(1),
            StudyTimeFrame.Quarter => first.AddMonths(3), StudyTimeFrame.Year => first.AddYears(1), _ => first.AddDays(1)
        };
        return (candle.SessionStartUtc(first), candle.SessionStartUtc(end));
    }
    internal static bool IsContained(IndicatorCandle candle, StudyTimeFrame frame)
    {
        if (frame == StudyTimeFrame.Chart) return candle.IsTimeResolutionCompatible;
        var bounds = PeriodBounds(candle, frame);
        return candle.IsTimeResolutionContained(bounds.Start, bounds.End);
    }
    internal static bool HasValue(WeightedStudyMoments moments, StudyCoverage coverage) => moments.Weight > 0 && (coverage & StudyCoverage.MissingBars) == 0;
    internal static DateTime ShiftSessionCalendar(IndicatorCandle candle, StudyTimeFrame frame, int amount)
    {
        var zone = candle.SessionTimeZone;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(candle.Time, DateTimeKind.Utc), zone);
        var session = candle.SessionDate;
        int dayOffset = DateOnly.FromDateTime(local).DayNumber - session.DayNumber;
        var shifted = frame switch
        {
            StudyTimeFrame.Day => session.AddDays(amount), StudyTimeFrame.Month => session.AddMonths(amount),
            StudyTimeFrame.Year => session.AddYears(amount), _ => throw new ArgumentOutOfRangeException(nameof(frame))
        };
        var target = DateTime.SpecifyKind(shifted.AddDays(dayOffset).ToDateTime(TimeOnly.FromTimeSpan(local.TimeOfDay)), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(target))
        {
            TimeSpan gap = NeighborOffset(zone, target, 1) - NeighborOffset(zone, target, -1);
            if (gap <= TimeSpan.Zero) throw new InvalidOperationException("Unsupported exchange calendar gap.");
            target = target.Add(gap);
        }
        if (zone.IsAmbiguousTime(target))
        {
            var before = NeighborOffset(zone, target, -1); var after = NeighborOffset(zone, target, 1); var sourceOffset = zone.GetUtcOffset(candle.Time);
            var offset = sourceOffset == before || sourceOffset == after ? sourceOffset : before < after ? before : after;
            return new DateTimeOffset(target, offset).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(target, zone);
    }
    private static TimeSpan NeighborOffset(TimeZoneInfo zone, DateTime local, int direction)
    {
        for (int hour = 1; hour <= 48; hour++)
        {
            var neighbor = local.AddHours(hour * direction);
            if (!zone.IsInvalidTime(neighbor) && !zone.IsAmbiguousTime(neighbor)) return zone.GetUtcOffset(neighbor);
        }
        throw new InvalidOperationException("Exchange calendar transition exceeds the supported two-day boundary.");
    }
    internal static long PeriodKey(IndicatorCandle c, StudyTimeFrame period)
    {
        var date = c.SessionDate;
        return period switch
        {
            StudyTimeFrame.Day => date.DayNumber,
            StudyTimeFrame.Week => date.DayNumber - ((int)date.DayOfWeek + 6) % 7,
            StudyTimeFrame.Month => date.Year * 12L + date.Month - 1,
            StudyTimeFrame.Quarter => date.Year * 4L + (date.Month - 1) / 3,
            StudyTimeFrame.Year => date.Year,
            StudyTimeFrame.Chart => c.Time.Ticks,
            _ => c.SessionStartUtc(date).Ticks + (c.Time.Ticks - c.SessionStartUtc(date).Ticks) / Duration(period).Ticks * Duration(period).Ticks
        };
    }
    internal static TimeSpan Duration(StudyTimeFrame frame) => frame switch
    {
        StudyTimeFrame.Minute => TimeSpan.FromMinutes(1), StudyTimeFrame.Minutes2 => TimeSpan.FromMinutes(2),
        StudyTimeFrame.Minutes5 => TimeSpan.FromMinutes(5), StudyTimeFrame.Minutes15 => TimeSpan.FromMinutes(15),
        StudyTimeFrame.Minutes30 => TimeSpan.FromMinutes(30), StudyTimeFrame.Hour => TimeSpan.FromHours(1),
        StudyTimeFrame.Hours4 => TimeSpan.FromHours(4), _ => TimeSpan.Zero
    };
    internal static (StudyTimeFrame A, StudyTimeFrame B, StudyTimeFrame C) BollingerMap(TimeSpan bar) => bar.TotalMinutes switch
    {
        <= 5 => (StudyTimeFrame.Minutes5, StudyTimeFrame.Minutes15, StudyTimeFrame.Hour),
        <= 15 => (StudyTimeFrame.Minutes15, StudyTimeFrame.Hour, StudyTimeFrame.Hours4),
        <= 60 => (StudyTimeFrame.Hour, StudyTimeFrame.Hours4, StudyTimeFrame.Day),
        <= 240 => (StudyTimeFrame.Hours4, StudyTimeFrame.Day, StudyTimeFrame.Week),
        _ => (StudyTimeFrame.Day, StudyTimeFrame.Week, StudyTimeFrame.Month)
    };
    internal static StudyCoverage Coverage(IndicatorCandle c) =>
        (c.HasObservedFootprint ? StudyCoverage.Observed : 0) | (c.HasAggregatedFootprint ? StudyCoverage.Compact : 0) |
        (c.HasInferredSide ? StudyCoverage.InferredSide : 0) | ((c.Quality & DataQuality.Partial) != 0 ? StudyCoverage.Partial : 0) |
        ((c.HasObservedFootprint || c.HasAggregatedFootprint) && (c.Quality & (DataQuality.Synthetic | DataQuality.Partial)) != 0 ? StudyCoverage.Incomplete : 0);
    internal static WeightedStudyMoments Vwap(IndicatorCandle c, StudyVwapSource source, bool allowEstimated, out StudyCoverage coverage)
    {
        var moments = new WeightedStudyMoments(); coverage = Coverage(c);
        if (!c.CanCalculateCausally) { coverage |= StudyCoverage.MissingBars; return moments; }
        // Known zero volume contributes zero weight, not a missing-data gap that poisons later bars.
        if (c.Volume == 0) { if (source != StudyVwapSource.MeasuredPriceLevels) coverage |= StudyCoverage.Estimated; return moments; }
        if (source == StudyVwapSource.MeasuredPriceLevels)
        {
            foreach (var row in c.GetProfilePriceLevels()) moments.Add(row.Price, row.Volume);
            if (moments.Weight > 0) return moments;
            if (!allowEstimated) { coverage |= StudyCoverage.MissingBars; return moments; }
        }
        coverage |= StudyCoverage.Estimated;
        if (c.Volume > 0) moments.Add(source == StudyVwapSource.BarClose ? c.Close : c.High / 3 + c.Low / 3 + c.Close / 3, c.Volume);
        else coverage |= StudyCoverage.MissingBars;
        return moments;
    }
    internal static bool TryDelta(IndicatorCandle c, bool allowInferred, out decimal delta, out StudyCoverage coverage)
    {
        coverage = Coverage(c); delta = 0;
        if (!c.CanCalculateCausally || (!c.HasObservedFootprint && !c.HasAggregatedFootprint) || c.HasInferredSide && !allowInferred)
        { coverage |= StudyCoverage.MissingBars; return false; }
        decimal unknown = 0;
        foreach (var row in c.GetProfilePriceLevels()) { delta += row.Ask - row.Bid; unknown += Math.Max(0, row.Volume - row.Ask - row.Bid); }
        // Unknown-side and omitted synthetic contributions are not balanced flow. Missing is not zero delta.
        if (unknown > 0 || (coverage & StudyCoverage.Incomplete) != 0) { coverage |= StudyCoverage.MissingBars; return false; }
        return true;
    }
    internal static decimal Median(Span<decimal> values)
    {
        values.Sort(); int n = values.Length;
        return n == 0 ? 0 : (n & 1) != 0 ? values[n / 2] : values[n / 2 - 1] / 2 + values[n / 2] / 2;
    }
    internal static decimal RobustZ(decimal value, Span<decimal> preceding)
    {
        decimal median = Median(preceding);
        for (int i = 0; i < preceding.Length; i++) preceding[i] = Math.Abs(preceding[i] - median);
        decimal mad = Median(preceding);
        return mad == 0 ? value > median ? decimal.MaxValue : value < median ? decimal.MinValue : 0 : (value - median) / (1.4826m * mad);
    }
}
