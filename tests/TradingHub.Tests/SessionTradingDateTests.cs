using TradingHub.Data;
using TradingHub.Indicators.Native;
using static TradingHub.Tests.LibraryStudyFixture;

namespace TradingHub.Tests;

/// <summary>Trading-date attribution of the shared exchange-session calendar (CME convention for evening starts).</summary>
public class SessionTradingDateTests
{
    private static readonly SessionSettings Cme = new() { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Start = TimeSpan.FromHours(18) };
    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Sunday_evening_session_is_mondays_trading_day_and_opens_the_new_week()
    {
        // 2026-03-08 is the US DST change: Sunday 18:00 EDT = 22:00 UTC; Friday 16:00 EST = 21:00 UTC
        var sundayOpen = Utc(2026, 3, 8, 22); var fridayClose = Utc(2026, 3, 6, 20, 59);
        Assert.Equal(new DateOnly(2026, 3, 9), Cme.SessionDate(sundayOpen));
        Assert.Equal(new DateOnly(2026, 3, 6), Cme.SessionDate(fridayClose));
        // the weekend gap before the open (no bars there) maps to Sunday, never to the new Monday session
        Assert.Equal(new DateOnly(2026, 3, 8), Cme.SessionDate(sundayOpen.AddMinutes(-1)));
        Assert.True(Cme.IsNewWeek(fridayClose, sundayOpen));
        Assert.True(Cme.IsNewSession(fridayClose, sundayOpen));
        Assert.Equal(sundayOpen, Cme.SessionStartUtc(new DateOnly(2026, 3, 9)));
        // Friday's session opened Thursday 18:00 EST (23:00 UTC)
        Assert.Equal(Utc(2026, 3, 5, 23), Cme.SessionStartUtc(new DateOnly(2026, 3, 6)));
        // a mid-week evening belongs to the next day, still in the same week
        Assert.Equal(new DateOnly(2026, 3, 11), Cme.SessionDate(Utc(2026, 3, 10, 23)));
        Assert.False(Cme.IsNewWeek(Utc(2026, 3, 10, 21), Utc(2026, 3, 10, 23)));
    }

    [Fact]
    public void Month_and_year_follow_the_trading_date()
    {
        // Tuesday 2026-03-31 18:00 EDT opens April 1st's session
        Assert.True(Cme.IsNewMonth(Utc(2026, 3, 31, 21), Utc(2026, 3, 31, 22)));
        Assert.Equal(new DateOnly(2026, 4, 1), Cme.SessionDate(Utc(2026, 3, 31, 22)));
        // Thursday 2026-12-31 18:00 EST opens 2027-01-01
        Assert.Equal(new DateOnly(2027, 1, 1), Cme.SessionDate(Utc(2026, 12, 31, 23)));
    }

    [Theory]
    [InlineData(2026, 3, 1)]   // across the US spring-forward change
    [InlineData(2026, 10, 25)] // across the US fall-back change
    public void Session_start_and_trading_date_round_trip_across_dst(int y, int m, int d)
    {
        var first = new DateOnly(y, m, d);
        for (int k = 0; k < 21; k++)
        {
            var date = first.AddDays(k); var start = Cme.SessionStartUtc(date);
            Assert.Equal(date, Cme.SessionDate(start));
            Assert.Equal(date.AddDays(-1), Cme.SessionDate(start.AddTicks(-1)));
            var local = TimeZoneInfo.ConvertTimeFromUtc(start, Cme.TimeZone);
            Assert.Equal((date.AddDays(-1), new TimeSpan(18, 0, 0)), (DateOnly.FromDateTime(local), local.TimeOfDay));
        }
        // the fall-back Sunday 2026-11-01 18:00 EST = 23:00 UTC opens Monday's week
        Assert.Equal(Utc(2026, 11, 1, 23), Cme.SessionStartUtc(new DateOnly(2026, 11, 2)));
        Assert.True(Cme.IsNewWeek(Utc(2026, 10, 30, 20), Utc(2026, 11, 1, 23)));
    }

    [Fact]
    public void Crypto_and_morning_calendars_are_unchanged()
    {
        var crypto = SessionSettings.CryptoUtc;
        Assert.Equal(0, crypto.TradingDateOffset);
        Assert.Equal(new DateOnly(2026, 3, 8), crypto.SessionDate(Utc(2026, 3, 8, 23, 59)));
        Assert.Equal(new DateOnly(2026, 3, 9), crypto.SessionDate(Utc(2026, 3, 9, 0)));
        Assert.Equal(Utc(2026, 3, 9, 0), crypto.SessionStartUtc(new DateOnly(2026, 3, 9)));
        Assert.True(crypto.IsNewWeek(Utc(2026, 3, 8, 23, 59), Utc(2026, 3, 9, 0)));
        Assert.False(crypto.IsNewWeek(Utc(2026, 3, 8, 0), Utc(2026, 3, 8, 23)));
        var cash = new SessionSettings { TimeZone = Cme.TimeZone, Start = new TimeSpan(9, 30, 0) };
        Assert.Equal(0, cash.TradingDateOffset);
        Assert.Equal(new DateOnly(2026, 3, 9), cash.SessionDate(Utc(2026, 3, 9, 13, 30)));
        Assert.Equal(Utc(2026, 3, 9, 13, 30), cash.SessionStartUtc(new DateOnly(2026, 3, 9)));
    }

    [Fact]
    public void Week_vwap_anchor_restarts_with_the_sunday_evening_session()
    {
        // hourly flat bars from Friday 2026-03-06 19:00 UTC; bar 51 = Sunday 18:00 New York opens the new trading week
        var bars = Enumerable.Range(0, 60).Select(i => new Ohlc(i < 51 ? 100 : 200, i < 51 ? 100 : 200, i < 51 ? 100 : 200, i < 51 ? 100 : 200)).ToList();
        using var model = Model(new DateTime(2026, 3, 6, 19, 0, 0, DateTimeKind.Utc), ChartPeriod.H1, Cme, bars);
        var vwap = Add(model, new MultiPeriodVwap());
        var week = Series(vwap, "MultiVwap.Week");
        Assert.Equal(100m, week[50]); Assert.Equal(200m, week[51]);
        Assert.Equal(200m, Series(vwap, "MultiVwap.Day")[51]);
    }
}
