using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.App.Services.Workbench;

public enum StructureTimeframe { Minutes5 = 5, Minutes15 = 15, Hour1 = 60, Hours4 = 240 }

public enum StructureItemKind { Bos, Choch, Sweep, Cisd, FairValueGap, OrderBlock, Breaker, Liquidity }

public enum StructureLevelKind { DayOpen, PreviousDayHigh, PreviousDayLow, WeekOpen, PreviousWeekHigh, PreviousWeekLow, SessionVwap }

/// <summary>
/// One confirmed structure item. <see cref="FormedAt"/> is the open of the bar its level or zone comes from (where the chart
/// study draws it); <see cref="ConfirmedAt"/> is the close of the bar that confirmed it, before which it was not known.
/// <see cref="DistancePercent"/> is from the reference price to the nearest edge (0 when the price is inside).
/// </summary>
public sealed record StructureItem(StructureItemKind Kind, int Direction, decimal Low, decimal High, DateTime FormedAt, DateTime ConfirmedAt, decimal DistancePercent);

/// <summary>A calendar level; null price when the study could not publish it (partial or missing period), never zero.</summary>
public sealed record StructureLevel(StructureLevelKind Kind, decimal? Price, DateTime? ConfirmedAt, decimal? DistancePercent);

/// <summary>
/// A session range from closed bars. <see cref="ConfirmedAt"/> is the close that made a completed session final; null while
/// it is forming (its range then covers closed bars through <see cref="Through"/>).
/// </summary>
public sealed record StructureSession(WorldSession Session, decimal Low, decimal High, DateTime From, DateTime Through, DateTime? ConfirmedAt);

/// <summary>A live PO3 heuristic on higher-period candles (<see cref="Period"/> is a period code such as 1h).</summary>
public sealed record StructurePo3(string Period, int Direction, bool TwoSided, decimal RangeLow, decimal RangeHigh, decimal Extreme,
    DateTime FormedAt, DateTime ConfirmedAt, decimal? CisdLevel, DateTime? CisdConfirmedAt);

/// <summary>
/// Read-only summary of the existing structure studies over closed candles: trend (null when the study published none,
/// 0 before the first confirmed break), the last break/sweep/CISD, the nearest live zones on each side of the price,
/// PO3, calendar levels and session ranges.
/// </summary>
public sealed record StructureSummaryResult(StructureTimeframe Timeframe, decimal Price, int ClosedBars, DateTime? LastClose, int? Trend,
    StructureItem? LastBreak, StructureItem? LastSweep, StructureItem? LastCisd,
    StructureItem? GapAbove, StructureItem? GapBelow, StructureItem? BlockAbove, StructureItem? BlockBelow, StructureItem? LiquidityAbove, StructureItem? LiquidityBelow,
    StructurePo3? Po3, IReadOnlyList<StructureLevel> Levels, IReadOnlyList<StructureSession> Sessions);

/// <summary>
/// Runs the chart's own studies (<see cref="MarketStructure"/>, <see cref="HtfStructure"/>, <see cref="KeyLevels"/>,
/// <see cref="SessionMap"/> and <see cref="SessionVwap"/>) over candles rebuilt from a connection's candle history, exactly
/// as a chart of that timeframe would, and keeps only what closed bars confirmed. No new detection rules live here.
/// </summary>
public static class StructureSummary
{
    /// <summary>Candle history granularity loaded for a timeframe (4-hour bars are built from 1-hour candles).</summary>
    public static int SourceMinutes(StructureTimeframe timeframe) => timeframe == StructureTimeframe.Hours4 ? 60 : (int)timeframe;

    /// <summary>History loaded for a timeframe: always enough for a complete previous week.</summary>
    public static TimeSpan History(StructureTimeframe timeframe) => timeframe switch
    {
        StructureTimeframe.Minutes5 or StructureTimeframe.Minutes15 => TimeSpan.FromDays(14),
        StructureTimeframe.Hour1 => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(90),
    };

    public static string Code(StructureTimeframe timeframe) => timeframe switch
    {
        StructureTimeframe.Minutes5 => "5m", StructureTimeframe.Minutes15 => "15m", StructureTimeframe.Hour1 => "1h", _ => "4h",
    };

    private static ChartPeriod Period(StructureTimeframe timeframe) => timeframe switch
    {
        StructureTimeframe.Minutes5 => ChartPeriod.M5, StructureTimeframe.Minutes15 => ChartPeriod.M15, StructureTimeframe.Hour1 => ChartPeriod.H1, _ => ChartPeriod.H4,
    };

    /// <summary>The PO3 candle period (first) and the second higher period of <see cref="HtfStructure"/> for a timeframe.</summary>
    private static (StudyTimeFrame First, StudyTimeFrame Second, string Code) Higher(StructureTimeframe timeframe) => timeframe switch
    {
        StructureTimeframe.Minutes5 => (StudyTimeFrame.Hour, StudyTimeFrame.Hours4, "1h"),
        StructureTimeframe.Minutes15 => (StudyTimeFrame.Hours4, StudyTimeFrame.Day, "4h"),
        StructureTimeframe.Hour1 => (StudyTimeFrame.Day, StudyTimeFrame.Week, "1D"),
        _ => (StudyTimeFrame.Week, StudyTimeFrame.Month, "1W"),
    };

    /// <summary>Code of the higher period whose candles form the PO3 heuristic for a timeframe.</summary>
    public static string Po3Period(StructureTimeframe timeframe) => Higher(timeframe).Code;

    /// <summary>
    /// Builds the summary from candles of <see cref="SourceMinutes"/> minutes in chronological order. The last candle is
    /// treated as forming. <paramref name="price"/> ≤ 0 uses the last candle's close. <paramref name="session"/> sets the
    /// trading-day boundary (default 00:00 UTC).
    /// </summary>
    public static StructureSummaryResult Build(Instrument instrument, IReadOnlyList<Kline> candles, StructureTimeframe timeframe, decimal price, DateTime now,
        SessionSettings? session = null)
    {
        int minutes = SourceMinutes(timeframe);
        if (price <= 0 && candles.Count > 0) price = candles[^1].Close;
        var trades = new List<Trade>(candles.Count * 4);
        if (candles.Count > 0)
        {
            var from = DateTime.UnixEpoch.AddMilliseconds(candles[0].OpenTimeMs);
            var to = DateTime.UnixEpoch.AddMilliseconds(candles[^1].OpenTimeMs).AddMinutes(minutes);
            KlineHistory.Synthesize(candles, instrument, minutes, new[] { (from, to) }, trades);
        }
        using var data = new InstrumentData(instrument, new OfflineFeed(session ?? SessionSettings.CryptoUtc, now));
        foreach (var trade in trades) data.Trades.Add(trade);

        var period = Period(timeframe);
        // Private models: a summary must not admit to or evict from the open charts' shared candle cache, nor prebuild neighbours.
        using var model = new ChartModel(data, period, sharedSeriesCache: false);
        var structure = Add(model, new MarketStructure());
        var higher = Higher(timeframe);
        var htf = Add(model, new HtfStructure { UseMappedPeriods = false, FirstPeriod = higher.First, SecondPeriod = higher.Second });
        // Calendar levels, sessions and VWAP read hourly bars at most, so a 4-hour summary uses the 1-hour candles for them.
        using var hourly = timeframe == StructureTimeframe.Hours4 ? new ChartModel(data, ChartPeriod.H1, sharedSeriesCache: false) : null;
        var context = hourly ?? model;
        var keys = Add(context, new KeyLevels());
        var sessions = Add(context, new SessionMap());
        var vwap = Add(context, new SessionVwap());

        var bars = new Bars(model, period.ApproxDuration);
        var contextBars = hourly == null ? bars : new Bars(hourly, ChartPeriod.H1.ApproxDuration);
        int last = bars.LastClosed;
        if (last < 0)
            return new StructureSummaryResult(timeframe, price, 0, null, null, null, null, null, null, null, null, null, null, null, null,
                Array.Empty<StructureLevel>(), Array.Empty<StructureSession>());

        var trend = Series(structure, "Structure.Trend");
        int? trendValue = trend.IsAvailable(last) ? (int)trend[last] : null;
        var events = structure.Events.Where(e => e.AvailableBar <= last).ToArray();
        StructureItem? Event(StructureEventKind kind, StructureEventKind? other = null)
        {
            for (int i = events.Length - 1; i >= 0; i--)
            {
                var e = events[i];
                if (e.Kind != kind && e.Kind != other) continue;
                var itemKind = e.Kind switch
                {
                    StructureEventKind.Bos => StructureItemKind.Bos, StructureEventKind.Choch => StructureItemKind.Choch,
                    StructureEventKind.Sweep => StructureItemKind.Sweep, _ => StructureItemKind.Cisd,
                };
                return Item(itemKind, e.Direction, e.Price, e.Price, bars.Open(e.RelatedBar >= 0 ? e.RelatedBar : e.SourceBar), bars.Close(e.AvailableBar), price);
            }
            return null;
        }

        var zones = structure.Zones.Where(z => z.EndBar < 0 && !z.CoverageGap && z.AvailableBar <= last)
            .Select(z => Item(z.Kind switch
            {
                StructureZoneKind.FairValueGap => StructureItemKind.FairValueGap, StructureZoneKind.OrderBlock => StructureItemKind.OrderBlock,
                StructureZoneKind.Breaker => StructureItemKind.Breaker, _ => StructureItemKind.Liquidity,
            }, z.Direction, z.Low, z.High, bars.Open(z.SourceBar), bars.Close(z.AvailableBar), price)).ToArray();
        StructureItem? Nearest(bool above, params StructureItemKind[] kinds) => zones
            .Where(z => kinds.Contains(z.Kind) && (above ? z.Low > price || z.High >= price && z.Low + z.High >= 2 * price : z.High < price || z.Low <= price && z.Low + z.High < 2 * price))
            .OrderBy(z => Math.Abs(z.DistancePercent)).ThenByDescending(z => z.ConfirmedAt).FirstOrDefault();

        StructurePo3? po3 = null;
        for (int i = htf.Patterns.Count - 1; i >= 0 && po3 == null; i--)
        {
            var p = htf.Patterns[i];
            if (p.AvailableBar > last || !p.IsActiveAt(last)) continue;
            bool cisd = p.ProjectionAvailableBar >= 0 && p.ProjectionAvailableBar <= last;
            po3 = new StructurePo3(higher.Code, p.Direction, p.TwoSided, p.RangeLow, p.RangeHigh, p.Extreme, bars.Open(p.C1StartBar), bars.Close(p.AvailableBar),
                cisd ? p.CisdLevel : null, cisd ? bars.Close(p.ProjectionAvailableBar) : null);
        }

        int ctxLast = contextBars.LastClosed;
        var levels = new List<StructureLevel>
        {
            Level(StructureLevelKind.PreviousDayHigh, Series(keys, "KeyLevels.Day.PriorHigh"), contextBars, ctxLast, price),
            Level(StructureLevelKind.PreviousDayLow, Series(keys, "KeyLevels.Day.PriorLow"), contextBars, ctxLast, price),
            Level(StructureLevelKind.DayOpen, Series(keys, "KeyLevels.Day.Open"), contextBars, ctxLast, price),
            Level(StructureLevelKind.PreviousWeekHigh, Series(keys, "KeyLevels.Week.PriorHigh"), contextBars, ctxLast, price),
            Level(StructureLevelKind.PreviousWeekLow, Series(keys, "KeyLevels.Week.PriorLow"), contextBars, ctxLast, price),
            Level(StructureLevelKind.WeekOpen, Series(keys, "KeyLevels.Week.Open"), contextBars, ctxLast, price),
        };
        var vwapSeries = Series(vwap, "VWAP");
        levels.Add(ctxLast >= 0 && vwapSeries.IsAvailable(ctxLast)
            ? new StructureLevel(StructureLevelKind.SessionVwap, vwapSeries[ctxLast], contextBars.Close(ctxLast), Distance(vwapSeries[ctxLast], vwapSeries[ctxLast], price))
            : new StructureLevel(StructureLevelKind.SessionVwap, null, null, null));

        var ranges = new List<StructureSession>();
        foreach (var world in new[] { WorldSession.Asia, WorldSession.London, WorldSession.NewYork })
        {
            string name = world switch { WorldSession.Asia => "Asia", WorldSession.London => "London", _ => "New York" };
            var done = sessions.Scene.Boxes.LastOrDefault(b => b.Text == name && b.To >= 0 && b.Available <= ctxLast);
            if (done != null)
                ranges.Add(new StructureSession(world, done.Bottom, done.Top, contextBars.Open(done.From), contextBars.Close(done.To), contextBars.Close(done.Available)));
            foreach (var forming in sessions.Forming.Where(f => f.Session == world && f.Last <= ctxLast))
                ranges.Add(new StructureSession(world, forming.Low, forming.High, contextBars.Open(forming.From), contextBars.Close(forming.Last), null));
        }

        return new StructureSummaryResult(timeframe, price, last + 1, bars.Close(last), trendValue,
            Event(StructureEventKind.Bos, StructureEventKind.Choch), Event(StructureEventKind.Sweep), Event(StructureEventKind.Cisd),
            Nearest(true, StructureItemKind.FairValueGap), Nearest(false, StructureItemKind.FairValueGap),
            Nearest(true, StructureItemKind.OrderBlock, StructureItemKind.Breaker), Nearest(false, StructureItemKind.OrderBlock, StructureItemKind.Breaker),
            Nearest(true, StructureItemKind.Liquidity), Nearest(false, StructureItemKind.Liquidity), po3, levels, ranges);
    }

    /// <summary>Signed percent from <paramref name="price"/> to the nearest edge of [low, high]; 0 inside.</summary>
    public static decimal Distance(decimal low, decimal high, decimal price) =>
        price <= 0 ? 0 : (price < low ? low - price : price > high ? high - price : 0) / price * 100;

    private static StructureItem Item(StructureItemKind kind, int direction, decimal low, decimal high, DateTime formed, DateTime confirmed, decimal price) =>
        new(kind, direction, low, high, formed, confirmed, Distance(low, high, price));

    /// <summary>The value published at <paramref name="bar"/>, confirmed at the close of the first bar of its unchanged run.</summary>
    private static StructureLevel Level(StructureLevelKind kind, OptionalValueDataSeries series, Bars bars, int bar, decimal price)
    {
        if (bar < 0 || !series.IsAvailable(bar)) return new StructureLevel(kind, null, null, null);
        decimal value = series[bar];
        int first = bar;
        while (first > 0 && series.IsAvailable(first - 1) && series[first - 1] == value) first--;
        return new StructureLevel(kind, value, bars.Close(first), Distance(value, value, price));
    }

    private static T Add<T>(ChartModel model, T indicator) where T : Indicator
    {
        var slot = model.AddIndicator(indicator, applyDefaultColors: false);
        if (slot.Error != null) throw new InvalidOperationException(slot.Error);
        return indicator;
    }

    private static OptionalValueDataSeries Series(Indicator indicator, string id) =>
        indicator.DataSeries.OfType<OptionalValueDataSeries>().First(s => s.Id == id);

    /// <summary>Bar times of a chart model; the last bar counts as closed only once the series closed it.</summary>
    private sealed class Bars(ChartModel model, TimeSpan duration)
    {
        public int LastClosed { get; } = model.Series.Count == 0 ? -1 : model.Series[model.Series.Count - 1].IsClosed ? model.Series.Count - 1 : model.Series.Count - 2;
        public DateTime Open(int bar) => model.Series[bar].Time;
        public DateTime Close(int bar) => model.Series[bar].Time + duration;
    }

    /// <summary>A feed that only supplies the trading-day boundary and the clock; it never connects anywhere.</summary>
    private sealed class OfflineFeed(SessionSettings session, DateTime now) : IMarketDataFeed, ISessionSettingsFeed
    {
        public string Id => "workbench-structure";
        public string DisplayName => "Structure summary";
        public bool SupportsHistory => false;
        public bool IsLive => false;
        public DateTime Now => now;
        public SessionSettings GetSessionSettings(Instrument instrument) => session;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(Array.Empty<Instrument>());
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => new Nothing();
        public void Dispose() { }

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
