using System.Text.Json;
using TradingHub.App.Services.Workbench;
using TradingHub.Feeds;
using TradingHub.Feeds.External;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class WorkbenchStructureTests
{
    private static readonly DateTime Monday = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Instrument Market = new() { Symbol = "STRUCTURE-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 };

    private static Kline Bar(DateTime open, decimal o, decimal h, decimal l, decimal c) =>
        new(new DateTimeOffset(open).ToUnixTimeMilliseconds(), o, h, l, c, 40, 0, false);

    /// <summary>Candles that move from close to close along <paramref name="closes"/> with one tick of wick on each side.</summary>
    private static List<Kline> Path(DateTime start, TimeSpan step, decimal first, params decimal[] closes)
    {
        var list = new List<Kline>();
        decimal open = first;
        for (int i = 0; i < closes.Length; i++)
        {
            decimal close = closes[i];
            list.Add(Bar(start + step * i, open, Math.Max(open, close) + 1, Math.Min(open, close) - 1, close));
            open = close;
        }
        return list;
    }

    private static decimal[] Leg(decimal from, decimal to, int bars) =>
        Enumerable.Range(1, bars).Select(i => from + (to - from) * i / bars).ToArray();

    private static StructureSummaryResult Build(IReadOnlyList<Kline> candles, StructureTimeframe timeframe = StructureTimeframe.Minutes5) =>
        StructureSummary.Build(Market, candles, timeframe, 0, Now);

    /// <summary>Up 100→150, down to 100, up to 170 (BOS up), then down to 80 (CHoCH down).</summary>
    private static List<Kline> Swings() =>
        Path(Monday, TimeSpan.FromMinutes(5), 100, Leg(100, 150, 10).Concat(Leg(150, 100, 10)).Concat(Leg(100, 170, 14)).Concat(Leg(170, 80, 18)).Append(80).ToArray());

    [Fact]
    public void Breaks_are_confirmed_only_at_a_closed_bar_and_report_their_confirming_close()
    {
        var candles = Swings();
        // swing low of the valley (bar 19 closes at 100, low 99; bar 20's equal low comes later); the CHoCH is the first later close below it
        int valley = 19;
        Assert.Equal(99m, candles[valley].Low);
        int choch = Enumerable.Range(valley + 6, candles.Count - valley - 6).First(i => candles[i].Close < 99);

        var full = Build(candles);
        var last = Assert.IsType<StructureItem>(full.LastBreak);
        Assert.Equal(StructureItemKind.Choch, last.Kind);
        Assert.Equal(-1, last.Direction);
        Assert.Equal(99m, last.Low);
        Assert.Equal(Monday.AddMinutes(5 * valley), last.FormedAt);
        Assert.Equal(Monday.AddMinutes(5 * (choch + 1)), last.ConfirmedAt);
        Assert.Equal(-1, full.Trend);

        // the breaking candle still forming: the CHoCH is not known yet and the last break is the earlier bullish BOS
        var forming = Build(candles.Take(choch + 1).ToList());
        var earlier = Assert.IsType<StructureItem>(forming.LastBreak);
        Assert.Equal(StructureItemKind.Bos, earlier.Kind);
        Assert.Equal(1, earlier.Direction);
        Assert.True(earlier.ConfirmedAt <= Monday.AddMinutes(5 * choch));
        Assert.Equal(1, forming.Trend);
        Assert.Equal(choch, forming.ClosedBars);
        Assert.Equal(Monday.AddMinutes(5 * choch), forming.LastClose);
    }

    [Fact]
    public void Fair_value_gaps_appear_after_the_third_close_and_disappear_once_filled()
    {
        var step = TimeSpan.FromMinutes(5);
        var candles = new List<Kline>();
        for (int i = 0; i < 12; i++) candles.Add(Bar(Monday + step * i, 100, 101, 99, 100));
        candles.Add(Bar(Monday + step * 12, 101, 113, 100, 112));
        candles.Add(Bar(Monday + step * 13, 112, 115, 105, 114));
        for (int i = 14; i < 20; i++) candles.Add(Bar(Monday + step * i, 114, 115, 113, 114));

        var gapBar = Build(candles.Take(14).ToList()); // the third candle is still forming
        Assert.Null(gapBar.GapBelow);
        Assert.Null(gapBar.GapAbove);

        var open = Build(candles);
        var gap = Assert.IsType<StructureItem>(open.GapBelow);
        Assert.Equal(StructureItemKind.FairValueGap, gap.Kind);
        Assert.Equal(1, gap.Direction);
        Assert.Equal(101m, gap.Low);
        Assert.Equal(105m, gap.High);
        Assert.Equal(Monday + step * 12, gap.FormedAt);
        Assert.Equal(Monday + step * 14, gap.ConfirmedAt);
        Assert.Equal((105m - 114m) / 114m * 100, gap.DistancePercent);

        // a wick through the far edge fills it
        candles.Add(Bar(Monday + step * 20, 114, 114, 100, 103));
        for (int i = 21; i < 26; i++) candles.Add(Bar(Monday + step * i, 103, 104, 102, 103));
        var filled = Build(candles);
        Assert.False(filled.GapBelow is { Low: 101m, High: 105m });
        Assert.False(filled.GapAbove is { Low: 101m, High: 105m });
    }

    [Fact]
    public void Previous_day_levels_are_published_only_after_midnight_utc_and_unknown_levels_stay_null()
    {
        var step = TimeSpan.FromMinutes(15);
        var candles = new List<Kline>();
        for (int i = 0; i < 96; i++)
        {
            decimal top = i == 40 ? 130 : 111, bottom = i == 70 ? 90 : 109;
            candles.Add(Bar(Monday + step * i, 110, top, bottom, 110));
        }
        var tuesday = Monday.AddDays(1);
        candles.Add(Bar(tuesday, 112, 113, 111, 112));

        var beforeMidnightClose = Build(candles, StructureTimeframe.Minutes15); // Tuesday's first candle still forming
        Assert.Null(Level(beforeMidnightClose, StructureLevelKind.PreviousDayHigh).Price);
        Assert.Null(Level(beforeMidnightClose, StructureLevelKind.PreviousDayLow).Price);

        for (int i = 1; i < 6; i++) candles.Add(Bar(tuesday + step * i, 112, 113, 111, 112));
        var after = Build(candles, StructureTimeframe.Minutes15);
        var high = Level(after, StructureLevelKind.PreviousDayHigh);
        var low = Level(after, StructureLevelKind.PreviousDayLow);
        Assert.Equal(130m, high.Price);
        Assert.Equal(90m, low.Price);
        Assert.Equal(tuesday + step, high.ConfirmedAt);
        Assert.Equal(112m, Level(after, StructureLevelKind.DayOpen).Price);
        Assert.Equal((130m - 112m) / 112m * 100, high.DistancePercent);
        // no complete previous week was loaded: unknown, not zero
        var week = Level(after, StructureLevelKind.PreviousWeekHigh);
        Assert.Null(week.Price);
        Assert.Null(week.DistancePercent);
    }

    private static StructureLevel Level(StructureSummaryResult r, StructureLevelKind kind) => r.Levels.Single(l => l.Kind == kind);

    [Fact]
    public void Four_hour_summary_runs_over_hourly_history_with_sessions_and_vwap()
    {
        var walk = LibraryStudyFixture.Walk(24 * 20, 11);
        var candles = walk.Select((b, i) => Bar(Monday.AddHours(i), b.Open, b.High, b.Low, b.Close)).ToList();
        var r = Build(candles, StructureTimeframe.Hours4);
        Assert.Equal(StructureTimeframe.Hours4, r.Timeframe);
        Assert.Equal(24 * 20 / 4 - 1, r.ClosedBars);
        Assert.Equal("1W", StructureSummary.Po3Period(r.Timeframe));
        Assert.NotNull(Level(r, StructureLevelKind.SessionVwap).Price);
        Assert.NotNull(Level(r, StructureLevelKind.PreviousWeekHigh).Price);
        Assert.Contains(r.Sessions, s => s.Session == WorldSession.London && s.ConfirmedAt != null);
        Assert.All(r.Sessions.Where(s => s.ConfirmedAt is { }), s => Assert.True(s.ConfirmedAt > s.From && s.Low <= s.High));
        foreach (var item in new[] { r.LastBreak, r.LastSweep, r.LastCisd, r.GapAbove, r.GapBelow, r.BlockAbove, r.BlockBelow, r.LiquidityAbove, r.LiquidityBelow })
            if (item != null) Assert.True(item.FormedAt < item.ConfirmedAt && item.ConfirmedAt <= r.LastClose);
    }

    [Fact]
    public void Empty_history_yields_no_items_rather_than_zero_levels()
    {
        var r = Build(Array.Empty<Kline>());
        Assert.Equal(0, r.ClosedBars);
        Assert.Null(r.Trend);
        Assert.Null(r.LastClose);
        Assert.Empty(r.Levels);
    }

    // ---------------------------------------------------------------- DVOL

    [Fact]
    public void Volatility_index_window_hatches_missing_and_invalid_days_and_keeps_today_apart()
    {
        var now = new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Utc);
        var points = new List<(DateTime, double)>();
        for (int d = 1; d <= 30; d++)
        {
            if (d is 3 or 4) continue;                 // not published
            points.Add((now.Date.AddDays(-d), d == 7 ? double.NaN : d == 8 ? 0 : 50 + d));
        }
        points.Add((now.Date, 47.5));                 // today's forming value
        var w = OptionsDashboard.VolatilityIndex("DVOL", points, now);
        Assert.Equal(30, w.Closes.Count);
        Assert.Equal(now.Date.AddDays(-30), w.FirstDay);
        Assert.Equal(4, w.MissingDays);
        Assert.Null(w.Closes[30 - 3]);
        Assert.Null(w.Closes[30 - 4]);
        Assert.Null(w.Closes[30 - 7]);
        Assert.Null(w.Closes[30 - 8]);
        Assert.Equal(51, w.Closes[^1]);
        Assert.Equal(80, w.Closes[0]);
        Assert.Equal(47.5, w.Today);
        Assert.DoesNotContain(0.0, w.Closes.Where(c => c.HasValue).Select(c => c!.Value));
    }

    [Fact]
    public async Task Volatility_index_uses_only_the_provider_rows_and_reports_sources_without_one()
    {
        var now = new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Utc);
        var gate = new TaskCompletionSource<IReadOnlyList<OptionDaily>>();
        var hub = new OptionsData(null, () => now) { Provider = new IndexProvider(gate.Task) };
        Assert.Null(hub.VolatilityIndex("BTC")!.Points);             // requested, not fetched yet: unknown, not empty
        gate.SetResult(new[] { Daily(now.Date.AddDays(-2), 0.52), Daily(now.Date.AddDays(-1), 0.55), Daily(now.Date.AddDays(-3), double.NaN) });
        await hub.RefreshDailyAsync("BTC", CancellationToken.None);
        var series = hub.VolatilityIndex("BTC")!;
        Assert.Equal("DVOL", series.Name);
        Assert.Null(series.Error);
        Assert.Equal(new[] { (now.Date.AddDays(-2), 52.0), (now.Date.AddDays(-1), 55.0) }, series.Points!.Select(p => (p.Day, Math.Round(p.Value, 6))));

        var custom = new OptionsData(null, () => now) { Provider = new JsonProvider(Task.FromResult<IReadOnlyList<OptionDaily>>(new[] { Daily(now.Date.AddDays(-1), 0.4) })) };
        await custom.RefreshDailyAsync("BTC", CancellationToken.None);
        Assert.Null(custom.VolatilityIndex("BTC"));
        Assert.NotEmpty(custom.Daily("BTC"));

        var failing = new OptionsData(null, () => now) { Provider = new IndexProvider(Task.FromException<IReadOnlyList<OptionDaily>>(new InvalidOperationException("daily history unavailable"))) };
        await failing.RefreshDailyAsync("BTC", CancellationToken.None);
        var failed = failing.VolatilityIndex("BTC")!;
        Assert.Null(failed.Points);
        Assert.Equal("daily history unavailable", failed.Error);
    }

    [Fact]
    public void Deribit_declares_dvol_and_parses_daily_closes()
    {
        Assert.Equal("DVOL", new DeribitOptions(new HttpClient()).VolatilityIndexName);
        using var doc = JsonDocument.Parse("{\"data\":[[1763164800000,50,52,49,51.5],[1763251200000,51.5,53,50,52]]}");
        var rows = DeribitOptions.ParseVolatilityIndex(doc.RootElement);
        Assert.Equal(0.515, rows[0].AtmIv, 6);
        Assert.Equal(new DateTime(2025, 11, 15, 0, 0, 0, DateTimeKind.Utc), rows[0].Date);
    }

    private static OptionDaily Daily(DateTime day, double iv) =>
        new(day, double.NaN, iv, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

    /// <summary>A custom-JSON-like source: daily rows but no published volatility index.</summary>
    private class JsonProvider(Task<IReadOnlyList<OptionDaily>> rows) : IOptionsProvider
    {
        public string Name => "Fixture JSON source";
        public bool Supports(string underlying) => underlying == "BTC";
        public Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct) => Task.FromResult<OptionsChain?>(null);
        public Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => Task.FromResult<IReadOnlyList<OptionTrade>>(Array.Empty<OptionTrade>());
        public Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => rows;
    }

    private sealed class IndexProvider(Task<IReadOnlyList<OptionDaily>> rows) : JsonProvider(rows), IVolatilityIndexSource
    {
        public string VolatilityIndexName => "DVOL";
    }
}
