using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class CsvHistoryQualityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tradinghub-csv-quality-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private string Root => Path.Combine(_dir, "ticks");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void ImportedQuotesKeepPricesAndPositiveIdentitiesWithoutInventingExecutedVolumeOrFootprint()
    {
        var result = Import("quotes.csv", "QUOTE", """
            Gmt time,Ask,Bid,AskVolume,BidVolume,Volume
            01.01.2026 12:00:00.000,101.10,100.10,5000,9000,7000
            01.01.2026 12:00:00.000,101.20,100.20,6000,8000,10000
            01.01.2026 12:01:00.000,100.90,99.90,4000,11000,12000
            """);
        var instrument = Instrument("QUOTE", result.TickSize);
        var quotes = HistorySources.ReadDay(Root, CsvImport.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start));
        Assert.Equal(new[] { 100.10m, 100.20m, 99.90m }, quotes.Select(trade => trade.Price));
        long firstId = new DateTimeOffset(Start).ToUnixTimeMilliseconds() * 8;
        Assert.Equal(new[] { firstId, firstId + 1, firstId + 480_000 }, quotes.Select(trade => trade.Id));
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Buy, TradeSide.Sell }, quotes.Select(trade => trade.Side));
        Assert.Single(quotes.Select(trade => trade.Provenance.SourceIdentity).Distinct());
        Assert.All(quotes, quote =>
        {
            Assert.True(quote.Id > 0);
            Assert.Equal(0m, quote.Volume);
            Assert.Equal(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial, quote.Provenance.Quality);
            Assert.Equal(0, quote.Provenance.ObservedTradeCount);
            Assert.False(quote.IsObservedTrade);
            Assert.True(quote.HasKnownAvailability);
            Assert.Equal(quote.Time, quote.AvailableAt);
            Assert.False(string.IsNullOrWhiteSpace(quote.Provenance.SourceIdentity));
        });

        using var data = new InstrumentData(instrument, new SimulatedFeed()) { RecordLive = false };
        foreach (var quote in quotes) data.Trades.Add(quote);
        Assert.Equal(99.90m, data.LastPrice);
        data.Trades.Add(new Trade(long.MaxValue, Start.AddMinutes(2), 102m, 3m, TradeSide.Buy));
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var bigTrades = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 0.5m };
        Assert.Null(model.AddIndicator(bigTrades, applyDefaultColors: false).Error);
        model.Update();
        Assert.Equal(3, model.BarCount);
        Assert.Equal(100.10m, model.GetCandle(0).Open);
        Assert.Equal(100.20m, model.GetCandle(0).Close);
        Assert.Equal(99.90m, model.GetCandle(1).Close);
        for (int bar = 0; bar < 2; bar++)
        {
            var candle = model.GetCandle(bar);
            Assert.True(candle.IsSynthetic);
            Assert.True(candle.HasInferredSide);
            Assert.True(candle.HasKnownAvailability);
            Assert.True(candle.CanCalculateCausally);
            Assert.Equal(0m, candle.Volume);
            Assert.Equal(0, candle.ObservedTrades);
            Assert.Equal(0m, candle.MaxTradeVolume);
            Assert.False(candle.HasObservedFootprint);
            Assert.False(candle.HasAggregatedFootprint);
            Assert.Empty(candle.GetAllPriceLevels());
            Assert.Empty(candle.GetAggregatedPriceLevels());
        }
        Assert.True(model.GetCandle(2).HasObservedFootprint);
        Assert.Equal(1, bigTrades.BubbleCount);

        var ticks = TickData.Load(Root, instrument, Start, Start.AddMinutes(2), null, default);
        Assert.Equal(3, ticks.Count);
        var probe = new Probe();
        var backtest = new BacktestEngine(probe, ticks, Settings(instrument, Start, Start.AddMinutes(2))).Run();
        Assert.Null(backtest.Error);
        Assert.NotNull(backtest.DataWarning);
        Assert.Equal(new[] { 100.20m, 99.90m }, probe.Seen.Select(bar => bar.Close));
        Assert.All(probe.Seen, bar =>
        {
            Assert.True(bar.HasSyntheticTrades);
            Assert.False(bar.HasRealTrades);
            Assert.Equal(0m, bar.Volume);
            Assert.Equal(0, bar.ObservedTrades);
            Assert.Empty(bar.Levels);
            Assert.Empty(bar.AggregatedLevels);
        });
        Assert.Empty(backtest.Trades);
    }

    [Fact]
    public void MissingAndReportedZeroBarVolumeRemainZeroAndReportedPrecisionIsConserved()
    {
        var zero = Import("zero.csv", "ZERO", """
            time,open,high,low,close,volume
            2026-01-01 12:00:00,100,103,98,102,0
            2026-01-01 12:01:00,102,105,101,104,0
            """);
        var missing = Import("missing.csv", "MISSING", """
            time,open,high,low,close,volume
            2026-01-01 12:00:00,100,103,98,102,
            2026-01-01 12:01:00,102,105,101,104,
            """);
        var absent = Import("absent.csv", "ABSENT", """
            time,open,high,low,close
            2026-01-01 12:00:00,100,103,98,102
            2026-01-01 12:01:00,102,105,101,104
            """);
        Assert.Equal(0, zero.MissingVolumeRows);
        Assert.Equal(2, missing.MissingVolumeRows);
        Assert.Equal(2, absent.MissingVolumeRows);
        foreach (var symbol in new[] { "ZERO", "MISSING", "ABSENT" })
        {
            var events = HistorySources.ReadDay(Root, CsvImport.FeedId, symbol, DateOnly.FromDateTime(Start));
            Assert.Equal(8, events.Count);
            Assert.All(events, trade =>
            {
                Assert.Equal(0m, trade.Volume);
                Assert.True(trade.Id < 0);
                Assert.True(trade.Provenance.IsPartial);
                Assert.False(trade.HasKnownAvailability);
                Assert.Equal(0, trade.Provenance.ObservedTradeCount);
            });
            var series = Series(Instrument(symbol), events, ChartPeriod.M1);
            Assert.Equal(2, series.Count);
            Assert.Equal((100m, 103m, 98m, 102m), Ohlc(new IndicatorCandle(series[0], series)));
            Assert.Equal((102m, 105m, 101m, 104m), Ohlc(new IndicatorCandle(series[1], series)));
            Assert.All(series.Candles, candle =>
            {
                var sdk = new IndicatorCandle(candle, series);
                Assert.Equal(0m, sdk.Volume);
                Assert.False(sdk.CanCalculateCausally);
                Assert.False(sdk.HasObservedFootprint);
                Assert.False(sdk.HasAggregatedFootprint);
            });
        }

        const decimal reported = 0.1234567890123456789012345678m;
        Import("precise.csv", "PRECISE", """
            time,open,high,low,close,volume
            2026-01-01 12:00:00,100,103,98,102,0.1234567890123456789012345678
            2026-01-01 12:01:00,102,105,101,104,0
            """);
        var precise = HistorySources.ReadDay(Root, CsvImport.FeedId, "PRECISE", DateOnly.FromDateTime(Start));
        Assert.Equal(reported, precise.Sum(trade => trade.Volume));
        var preciseSeries = Series(Instrument("PRECISE"), precise, ChartPeriod.M1);
        Assert.Equal(reported, preciseSeries[0].Volume);
        Assert.Equal(0m, preciseSeries[1].Volume);
    }

    private static CandleSeries Series(Instrument instrument, IReadOnlyList<Trade> events, ChartPeriod period, SessionSettings? session = null)
    {
        var store = new TradeStore(instrument);
        foreach (var trade in events) store.Add(trade);
        var series = new CandleSeries(instrument, period, session: session);
        series.Rebuild(store);
        return series;
    }

    private static (decimal Open, decimal High, decimal Low, decimal Close) Ohlc(IndicatorCandle candle) =>
        (candle.Open, candle.High, candle.Low, candle.Close);

    [Fact]
    public void FirstSourceDayUsesTheSameInferredHourAsLaterDays()
    {
        var result = Import("hourly.csv", "HOUR", """
            time,open,high,low,close,volume
            2026-01-01 23:00:00,100,110,90,105,8
            2026-01-02 00:00:00,105,108,100,101,4
            2026-01-02 01:00:00,101,103,99,102,0
            """);
        var first = HistorySources.ReadDay(Root, CsvImport.FeedId, "HOUR", new DateOnly(2026, 1, 1));
        Assert.Equal(new[] { 23, 23, 23, 23 }, first.Select(trade => trade.Time.Hour));
        Assert.Equal(new[] { 0, 15, 30, 45 }, first.Select(trade => trade.Time.Minute));
        Assert.All(first, trade => Assert.Equal(3600, trade.Provenance.ResolutionSeconds));
        Assert.Equal(8m, first.Sum(trade => trade.Volume));
        var all = ReadAll("HOUR");
        Assert.Equal(12, all.Count);
        Assert.Equal(2, result.Days);
        var series = Series(Instrument("HOUR"), all, ChartPeriod.Parse("H1"));
        Assert.Equal(3, series.Count);
        Assert.Equal((100m, 110m, 90m, 105m), Ohlc(new IndicatorCandle(series[0], series)));
        Assert.Equal((105m, 108m, 100m, 101m), Ohlc(new IndicatorCandle(series[1], series)));
        Assert.Equal((101m, 103m, 99m, 102m), Ohlc(new IndicatorCandle(series[2], series)));
    }

    [Fact]
    public void WideBarsWriteEveryGeneratedPointToItsActualUtcDayAndConserveBothSourceBars()
    {
        var result = Import("wide.csv", "WIDE", """
            time,open,high,low,close,volume
            2026-01-01 12:00:00,100,120,80,110,10
            2026-01-03 12:00:00,110,130,90,115,8
            """);
        var days = HistorySources.AvailableDays(Root, CsvImport.FeedId, "WIDE");
        Assert.Equal(new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 3),
            new DateOnly(2026, 1, 4), new DateOnly(2026, 1, 5) }, days);
        Assert.Equal(5, result.Days);
        Assert.Equal(8, result.Trades);
        foreach (var day in days)
            Assert.All(HistorySources.ReadDay(Root, CsvImport.FeedId, "WIDE", day),
                trade => Assert.Equal(day, DateOnly.FromDateTime(trade.Time)));
        var all = ReadAll("WIDE");
        Assert.Equal(8, all.Count);
        Assert.Equal(18m, all.Sum(trade => trade.Volume));
        Assert.Equal(new[] {
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 4, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
        }, all.Select(trade => trade.Time));
        Assert.All(all, trade => Assert.Equal(172_800, trade.Provenance.ResolutionSeconds));
        var first = Series(Instrument("WIDE"), all.Take(4).ToArray(), ChartPeriod.Parse("Monthly"));
        var second = Series(Instrument("WIDE"), all.Skip(4).ToArray(), ChartPeriod.Parse("Monthly"));
        Assert.Equal((100m, 120m, 80m, 110m), Ohlc(new IndicatorCandle(Assert.Single(first.Candles), first)));
        Assert.Equal((110m, 130m, 90m, 115m), Ohlc(new IndicatorCandle(Assert.Single(second.Candles), second)));
        Assert.Equal(10m, first[0].Volume);
        Assert.Equal(8m, second[0].Volume);
        long firstId = KlineHistory.SyntheticIdBase + new DateTimeOffset(Start).ToUnixTimeMilliseconds() * 4;
        Assert.Equal(Enumerable.Range(0, 4).Select(index => firstId + index), all.Take(4).Select(trade => trade.Id));
    }

    [Fact]
    public void CalendarMonthPathsFollowActualMonthOpeningsRatherThanAFixedThirtyDays()
    {
        var result = Import("monthly.csv", "MONTH", """
            time,open,high,low,close,volume
            2026-01-01 00:00:00,100,110,90,105,10
            2026-02-01 00:00:00,105,120,95,115,20
            2026-03-01 00:00:00,115,125,100,120,30
            """);
        var events = ReadAll("MONTH");
        Assert.Equal(12, result.Trades);
        Assert.Equal(12, events.Count);
        Assert.Equal(60m, events.Sum(trade => trade.Volume));
        Assert.Equal(new DateTime(2026, 2, 22, 0, 0, 0, DateTimeKind.Utc), events[7].Time);
        Assert.Equal(new DateTime(2026, 3, 24, 6, 0, 0, DateTimeKind.Utc), events[11].Time);
        var series = Series(Instrument("MONTH"), events, ChartPeriod.Parse("Monthly"));
        Assert.Equal(3, series.Count);
        Assert.Equal((100m, 110m, 90m, 105m), Ohlc(new IndicatorCandle(series[0], series)));
        Assert.Equal((105m, 120m, 95m, 115m), Ohlc(new IndicatorCandle(series[1], series)));
        Assert.Equal((115m, 125m, 100m, 120m), Ohlc(new IndicatorCandle(series[2], series)));
        Assert.Equal(new[] { 10m, 20m, 30m }, series.Candles.Select(candle => candle.Volume));
        Assert.All(events.Take(4), trade => Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), trade.AvailableAt));
        Assert.All(events.Skip(4).Take(4), trade => Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), trade.AvailableAt));
        Assert.All(events.Skip(8), trade => Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), trade.AvailableAt));
        Assert.All(series.Candles, candle => Assert.False(new IndicatorCandle(candle, series).CanCalculateCausally));
    }

    [Fact]
    public void SourceMonthOpeningsWithExplicitGmtOffsetsRetainTheirActualUtcDaysAcrossDst()
    {
        Import("local-months.csv", "LOCALMONTH", """
            Local time,Open,High,Low,Close,Volume
            01.03.2026 00:00:00.000 GMT+0100,100,110,90,105,10
            01.04.2026 00:00:00.000 GMT+0200,105,120,95,115,20
            01.05.2026 00:00:00.000 GMT+0200,115,125,100,120,30
            """, TimeSpan.FromHours(-5));
        var events = ReadAll("LOCALMONTH");
        Assert.Equal(12, events.Count);
        Assert.Equal(new DateTime(2026, 2, 28, 23, 0, 0, DateTimeKind.Utc), events[0].Time);
        Assert.Equal(new DateTime(2026, 3, 8, 16, 45, 0, DateTimeKind.Utc), events[1].Time);
        Assert.Equal(new DateTime(2026, 3, 31, 22, 0, 0, DateTimeKind.Utc), events[4].Time);
        Assert.Equal(new DateTime(2026, 4, 30, 22, 0, 0, DateTimeKind.Utc), events[8].Time);
        Assert.Equal(new DateTime(2026, 5, 24, 4, 0, 0, DateTimeKind.Utc), events[11].Time);
        Assert.All(events.Take(4), trade => Assert.Equal(new DateTime(2026, 3, 31, 22, 0, 0, DateTimeKind.Utc), trade.AvailableAt));
        var session = new SessionSettings { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), Start = TimeSpan.Zero };
        var series = Series(Instrument("LOCALMONTH"), events, ChartPeriod.Parse("Monthly"), session);
        Assert.Equal(3, series.Count);
        Assert.Equal((100m, 110m, 90m, 105m), Ohlc(new IndicatorCandle(series[0], series)));
        Assert.Equal((105m, 120m, 95m, 115m), Ohlc(new IndicatorCandle(series[1], series)));
        Assert.Equal((115m, 125m, 100m, 120m), Ohlc(new IndicatorCandle(series[2], series)));
        Assert.Equal(new[] { 10m, 20m, 30m }, series.Candles.Select(candle => candle.Volume));
        foreach (var day in HistorySources.AvailableDays(Root, CsvImport.FeedId, "LOCALMONTH"))
            Assert.All(HistorySources.ReadDay(Root, CsvImport.FeedId, "LOCALMONTH", day),
                trade => Assert.Equal(day, DateOnly.FromDateTime(trade.Time)));
    }

    [Fact]
    public void AnUnconfirmedSingleBarHasNoInventedIntervalAndCannotReachStrategyCallbacks()
    {
        Import("single.csv", "SINGLE", """
            time,open,high,low,close,volume
            2026-01-01 12:00:00,100,110,90,105,6
            """);
        var events = ReadAll("SINGLE");
        Assert.Equal(4, events.Count);
        Assert.Equal(6m, events.Sum(trade => trade.Volume));
        Assert.All(events, trade =>
        {
            Assert.Equal(Start, trade.Time);
            Assert.Equal(0, trade.Provenance.ResolutionSeconds);
            Assert.False(trade.HasKnownAvailability);
            Assert.True(trade.Provenance.IsPartial);
        });
        var instrument = Instrument("SINGLE");
        var series = Series(instrument, events, ChartPeriod.M1);
        Assert.Equal((100m, 110m, 90m, 105m), Ohlc(new IndicatorCandle(Assert.Single(series.Candles), series)));
        Assert.False(new IndicatorCandle(series[0], series).CanCalculateCausally);
        var ticks = TickData.Load(Root, instrument, Start, Start.AddMinutes(1), null, default);
        var probe = new Probe();
        var result = new BacktestEngine(probe, ticks, Settings(instrument, Start, Start.AddMinutes(1))).Run();
        Assert.NotNull(result.Error);
        Assert.Empty(probe.Seen);
        Assert.Empty(result.Trades);
    }

    [Fact]
    public void ImportingSyntheticBarsDoesNotReplaceFinerExecutedHistory()
    {
        var instrument = Instrument("PRESERVE");
        var raw = new Trade(99, Start, 107m, 2m, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "executed-fixture"));
        HistoricalData.WriteDay(Root, instrument, DateOnly.FromDateTime(Start), new[] { raw });
        Import("preserve.csv", instrument.Symbol, """
            time,open,high,low,close,volume
            2026-01-01 12:00:00,100,120,90,110,400
            2026-01-01 12:01:00,110,130,100,120,100
            """);
        var events = ReadAll(instrument.Symbol);
        var retained = Assert.Single(events, trade => trade.IsObservedTrade);
        Assert.Equal(raw.Id, retained.Id);
        Assert.Equal(raw.Time, retained.Time);
        Assert.Equal(raw.Price, retained.Price);
        Assert.Equal(raw.Volume, retained.Volume);
        Assert.Equal(raw.Provenance, retained.Provenance);
        Assert.Equal(5, events.Count);
        Assert.Equal(102m, events.Sum(trade => trade.Volume));
        Assert.Equal(raw.Id, events[0].Id);
        var loaded = TickData.Load(Root, instrument, Start, Start.AddMinutes(2), null, default);
        Assert.Equal(5, loaded.Count);
        Assert.Equal(raw.Id, loaded.Get(loaded.Start).Id);
        var series = Series(instrument, events, ChartPeriod.M1);
        Assert.True(new IndicatorCandle(series[0], series).HasObservedFootprint);
        Assert.False(new IndicatorCandle(series[1], series).HasObservedFootprint);
    }

    [Theory]
    [InlineData("last", "volume")]
    [InlineData("ask", "askvolume")]
    public void LastAndAskColumnsAlsoRemainQuoteObservations(string priceColumn, string volumeColumn)
    {
        Import("observation.csv", "OBSERVATION",
            $"time,{priceColumn},{volumeColumn}\n2026-01-01 12:00:00,100.25,5000\n2026-01-01 12:00:01,100.50,9000\n");
        var events = ReadAll("OBSERVATION");
        Assert.Equal(new[] { 100.25m, 100.50m }, events.Select(trade => trade.Price));
        Assert.All(events, trade =>
        {
            Assert.Equal(0m, trade.Volume);
            Assert.False(trade.IsObservedTrade);
            Assert.True(trade.HasKnownAvailability);
            Assert.True(trade.Provenance.IsSideInferred);
        });
    }

    [Fact]
    public void LaterFinePrecisionQuotesDoNotGetRoundedByTheInitialRows()
    {
        var rows = Enumerable.Repeat("2026-01-01 12:00:00,100.12", 520).Append("2026-01-01 12:00:01,100.12345678");
        var result = Import("precision.csv", "FINE", "time,price\n" + string.Join('\n', rows));
        Assert.Equal(0.00000001m, result.TickSize);
        var events = ReadAll("FINE");
        Assert.Equal(521, events.Count);
        Assert.Equal(100.12345678m, events[^1].Price);
        var series = Series(Instrument("FINE", result.TickSize), events, ChartPeriod.M1);
        Assert.Equal(100.12345678m, new IndicatorCandle(Assert.Single(series.Candles), series).Close);
    }

    [Fact]
    public void RepeatedAndOverlappingQuoteImportsCorrectExistingObservationsWithoutDuplicatingThem()
    {
        const string initial = """
            time,bid,ask
            2026-01-01 12:00:00,100,101
            2026-01-01 12:00:01,101,102
            2026-01-01 12:01:00,102,103
            """;
        Import("quote-first.csv", "QUOTEUPDATE", initial);
        var original = ReadAll("QUOTEUPDATE");
        Import("quote-copy.csv", "QUOTEUPDATE", initial);
        Assert.Equal(3, ReadAll("QUOTEUPDATE").Count);
        Import("quote-overlap.csv", "QUOTEUPDATE", """
            time,bid,ask
            2026-01-01 12:00:01,103,104
            2026-01-01 12:01:00,104,105
            2026-01-01 12:02:00,105,106
            """);
        var corrected = ReadAll("QUOTEUPDATE");
        Assert.Equal(new[] { 100m, 103m, 104m, 105m }, corrected.Select(trade => trade.Price));
        Assert.Equal(original.Select(trade => trade.Id), corrected.Take(3).Select(trade => trade.Id));
        Assert.All(corrected, trade =>
        {
            Assert.Equal("importado:QUOTEUPDATE", trade.Provenance.SourceIdentity);
            Assert.Equal(0m, trade.Volume);
            Assert.False(trade.IsObservedTrade);
        });
        var instrument = Instrument("QUOTEUPDATE");
        var ticks = TickData.Load(Root, instrument, Start, Start.AddMinutes(3), null, default);
        Assert.Equal(4, ticks.Count);
        var probe = new Probe();
        var result = new BacktestEngine(probe, ticks, Settings(instrument, Start, Start.AddMinutes(3))).Run();
        Assert.Null(result.Error);
        Assert.Equal(new[] { 103m, 104m, 105m }, probe.Seen.Select(bar => bar.Close));
        Assert.All(probe.Seen, bar => Assert.Empty(bar.Levels));
    }

    [Fact]
    public void RepeatedAndOverlappingBarImportsReplaceOhlcAndVolumeByOriginalSourceIdentity()
    {
        const string initial = """
            time,open,high,low,close,volume
            2026-01-01 12:00:00,100,110,90,105,10
            2026-01-01 12:01:00,105,120,95,115,20
            """;
        Import("bar-first.csv", "BARUPDATE", initial);
        var original = ReadAll("BARUPDATE");
        Import("bar-copy.csv", "BARUPDATE", initial);
        Assert.Equal(8, ReadAll("BARUPDATE").Count);
        Assert.Equal(30m, ReadAll("BARUPDATE").Sum(trade => trade.Volume));
        Import("bar-overlap.csv", "BARUPDATE", """
            time,open,high,low,close,volume
            2026-01-01 12:01:00,105,130,100,120,12
            2026-01-01 12:02:00,120,135,115,125,4
            """);
        var corrected = ReadAll("BARUPDATE");
        Assert.Equal(12, corrected.Count);
        Assert.Equal(26m, corrected.Sum(trade => trade.Volume));
        Assert.Equal(original.Select(trade => trade.Id), corrected.Take(8).Select(trade => trade.Id));
        Assert.Equal(original.Select(trade => trade.Provenance.SourceEventId),
            corrected.Take(8).Select(trade => trade.Provenance.SourceEventId));
        Assert.All(corrected, trade => Assert.False(string.IsNullOrEmpty(trade.Provenance.SourceEventId)));
        var instrument = Instrument("BARUPDATE");
        var loaded = TickData.Load(Root, instrument, Start, Start.AddMinutes(3), null, default);
        Assert.Equal(12, loaded.Count);
        var series = Series(instrument, corrected, ChartPeriod.M1);
        Assert.Equal(3, series.Count);
        Assert.Equal((105m, 130m, 100m, 120m), Ohlc(new IndicatorCandle(series[1], series)));
        Assert.Equal(new[] { 10m, 12m, 4m }, series.Candles.Select(candle => candle.Volume));
    }

    [Fact]
    public void CorrectedWideIntervalsRemoveOldDayProxiesAndRefreshArchivesWithoutRemovingUnrelatedEvents()
    {
        Import("old-wide.csv", "MOVED", """
            time,open,high,low,close,volume
            2026-01-01 12:00:00,100,120,80,110,10
            2026-01-05 12:00:00,110,130,90,115,8
            """);
        var original = ReadAll("MOVED");
        var instrument = Instrument("MOVED", 0.25m);
        var mixedDay = new DateOnly(2026, 1, 7);
        var raw = new Trade(55, new DateTime(2026, 1, 7, 18, 0, 0, DateTimeKind.Utc), 200.25m, 3m, TradeSide.Buy,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "executed-fixture"));
        var unrelated = new Trade(-77, raw.Time.AddHours(1), 210.25m, 7m, TradeSide.Sell,
            new TradeProvenance(DataQuality.Synthetic | DataQuality.Partial, SourceIdentity: "other-synthetic-fixture",
                ObservedTradeCount: 0, HasKnownAvailability: false));
        // Append through the real recorder format to retain both pre-correction proxies and measured events.
        var mixedPath = TickFile.DayPath(Root, CsvImport.FeedId, instrument.Symbol, mixedDay);
        var existing = TickFile.Read(mixedPath);
        using (var writer = new TickFile.Writer(mixedPath + ".fixture", instrument, includeProvenance: true))
        {
            foreach (var trade in existing) writer.Write(trade);
            writer.Write(raw);
            writer.Write(unrelated);
        }
        File.Move(mixedPath + ".fixture", mixedPath, overwrite: true);
        var mixedArchive = TickArchive.ArchiveDay(Root, instrument, mixedDay);
        File.Delete(mixedPath);
        var emptyDay = new DateOnly(2026, 1, 8);
        var emptyPath = TickFile.DayPath(Root, CsvImport.FeedId, instrument.Symbol, emptyDay);
        var emptyArchive = TickArchive.ArchiveDay(Root, instrument, emptyDay);
        File.Delete(emptyPath);
        // A pre-provenance CSV point with the same original negative ID must be replaced too.
        var legacy = original[3];
        var legacyPath = TickFile.DayPath(Root, CsvImport.FeedId, instrument.Symbol, DateOnly.FromDateTime(legacy.Time));
        using (var writer = new TickFile.Writer(legacyPath, Instrument("MOVED"), includeProvenance: true))
            writer.Write(new Trade(legacy.Id, legacy.Time, legacy.Price, legacy.Volume, legacy.Side,
                legacy.Provenance with { SourceEventId = null }));

        Import("new-daily.csv", "MOVED", """
            time,open,high,low,close,volume
            2026-01-01 12:00:00,100,125,75,112,10
            2026-01-02 12:00:00,112,128,100,120,6
            2026-01-05 12:00:00,120,140,110,130,12
            """);
        var corrected = ReadAll("MOVED");
        Assert.Equal(14, corrected.Count);
        Assert.Equal(38m, corrected.Sum(trade => trade.Volume));
        foreach (var previous in original)
            Assert.Single(corrected, trade => trade.Id == previous.Id && trade.Provenance.SourceIdentity == previous.Provenance.SourceIdentity);
        Assert.Equal(new[] { raw.Id, unrelated.Id }, HistorySources.ReadDay(Root, CsvImport.FeedId, instrument.Symbol, mixedDay).Select(trade => trade.Id));
        var retained = Assert.Single(corrected, trade => trade.Id == raw.Id);
        Assert.Equal(raw.Price, retained.Price);
        Assert.Equal(raw.Volume, retained.Volume);
        Assert.Equal(raw.Provenance, retained.Provenance);
        Assert.Contains(corrected, trade => trade.Id == unrelated.Id && trade.Provenance == unrelated.Provenance);
        Assert.False(HistorySources.HasDay(Root, CsvImport.FeedId, instrument.Symbol, new DateOnly(2026, 1, 4)));
        Assert.False(HistorySources.HasDay(Root, CsvImport.FeedId, instrument.Symbol, emptyDay));
        Assert.False(File.Exists(emptyArchive));
        Assert.Equal(new[] { raw.Id, unrelated.Id }, TickFile.Read(mixedArchive).Select(trade => trade.Id));
        var series = Series(instrument, corrected, ChartPeriod.Parse("Monthly"));
        var sdk = new IndicatorCandle(Assert.Single(series.Candles), series);
        Assert.Equal(38m, sdk.Volume);
        Assert.Equal(3m, sdk.GetAllPriceLevels().Sum(level => level.Volume));
    }

    [Fact]
    public void CorrectedCalendarSpacingRetiresPriorMonthLocationsWithoutChangingOriginalNegativeIds()
    {
        Import("quarters.csv", "CALUPDATE", """
            time,open,high,low,close,volume
            2026-01-01 00:00:00,100,120,80,110,10
            2026-04-01 00:00:00,110,130,90,115,20
            """);
        var previous = ReadAll("CALUPDATE");
        Assert.Equal(8, previous.Count);
        Import("months.csv", "CALUPDATE", """
            time,open,high,low,close,volume
            2026-01-01 00:00:00,100,125,75,112,10
            2026-02-01 00:00:00,112,128,100,120,6
            2026-03-01 00:00:00,120,135,110,125,8
            2026-04-01 00:00:00,125,140,115,130,12
            """);
        var corrected = ReadAll("CALUPDATE");
        Assert.Equal(16, corrected.Count);
        Assert.Equal(36m, corrected.Sum(trade => trade.Volume));
        foreach (var original in previous)
            Assert.Single(corrected, trade => trade.Id == original.Id && trade.Provenance.SourceEventId == original.Provenance.SourceEventId);
        Assert.All(HistorySources.AvailableDays(Root, CsvImport.FeedId, "CALUPDATE"), day => Assert.InRange(day.Month, 1, 4));
        var series = Series(Instrument("CALUPDATE"), corrected, ChartPeriod.Parse("Monthly"));
        Assert.Equal(4, series.Count);
        Assert.Equal(new[] { 10m, 6m, 8m, 12m }, series.Candles.Select(candle => candle.Volume));
        Assert.Equal((100m, 125m, 75m, 112m), Ohlc(new IndicatorCandle(series[0], series)));
        Assert.Equal((125m, 140m, 115m, 130m), Ohlc(new IndicatorCandle(series[3], series)));
    }

    private List<Trade> ReadAll(string symbol) => HistorySources.AvailableDays(Root, CsvImport.FeedId, symbol)
        .SelectMany(day => HistorySources.ReadDay(Root, CsvImport.FeedId, symbol, day)).ToList();

    private ImportResult Import(string name, string symbol, string content, TimeSpan? offset = null, decimal? tick = null)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return CsvImport.Import(path, symbol, Root, offset ?? TimeSpan.Zero, tick, null, CancellationToken.None);
    }

    private static Instrument Instrument(string symbol, decimal tick = 1m) => new()
    {
        FeedId = CsvImport.FeedId, Symbol = symbol, Exchange = CsvImport.FeedId,
        TickSize = tick, VolumeStep = 0.001m, PointValue = 1m,
    };

    private static BacktestSettings Settings(Instrument instrument, DateTime from, DateTime to, string period = "M1") => new()
    {
        FeedId = instrument.FeedId, Symbol = instrument.Symbol, FromUtc = from, ToUtc = to,
        Period = period, CommissionPercent = 0, SlippageTicks = 0,
    };

    private sealed class Probe : Strategy
    {
        public List<Bar> Seen { get; } = new();
        protected override void OnInit() => UseFootprint = true;
        protected override void OnBar(Bar bar) => Seen.Add(bar);
    }
}
