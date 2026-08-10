using StonkWatch.Web.Services.Monitoring;

namespace StonkWatch.Web.Tests;

public class MarketCalendarTests
{
    /// <summary>A UTC instant for a given wall-clock time in New York, EDT (UTC-4, summer).</summary>
    private static DateTimeOffset Edt(int year, int month, int day, int hour, int minute = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(-4)).ToUniversalTime();

    /// <summary>Same, for EST (UTC-5, winter).</summary>
    private static DateTimeOffset Est(int year, int month, int day, int hour, int minute = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(-5)).ToUniversalTime();

    // ---------- Session window ----------

    [Theory]
    [InlineData(9, 29, false)]  // one minute before the open
    [InlineData(9, 30, true)]   // the open itself
    [InlineData(12, 0, true)]
    [InlineData(15, 59, true)]
    [InlineData(16, 0, false)]  // the close is exclusive
    [InlineData(16, 1, false)]
    public void The_regular_session_runs_from_0930_to_1600_eastern(int hour, int minute, bool expected)
    {
        // Friday 31 July 2026.
        Assert.Equal(expected, MarketCalendar.IsOpen(Edt(2026, 7, 31, hour, minute)));
    }

    [Fact]
    public void Overnight_is_closed()
    {
        Assert.False(MarketCalendar.IsOpen(Edt(2026, 7, 31, 3)));
    }

    // ---------- Weekends ----------

    [Fact]
    public void Saturday_is_closed_even_during_session_hours()
    {
        Assert.False(MarketCalendar.IsOpen(Edt(2026, 8, 1, 12)));
    }

    [Fact]
    public void Sunday_is_closed_even_during_session_hours()
    {
        Assert.False(MarketCalendar.IsOpen(Edt(2026, 8, 2, 12)));
    }

    [Fact]
    public void A_weekend_gives_a_weekend_reason()
    {
        Assert.Contains("weekend", MarketCalendar.DescribeClosed(Edt(2026, 8, 1, 12)));
    }

    // ---------- Holidays ----------

    [Theory]
    [InlineData(2026, 1, 1)]    // New Year's Day
    [InlineData(2026, 5, 25)]   // Memorial Day
    [InlineData(2026, 7, 3)]    // Independence Day observed
    [InlineData(2026, 11, 26)]  // Thanksgiving
    [InlineData(2026, 12, 25)]  // Christmas
    public void Market_holidays_are_closed(int year, int month, int day)
    {
        Assert.False(MarketCalendar.IsOpen(Edt(year, month, day, 12)));
    }

    [Fact]
    public void A_holiday_gives_a_holiday_reason()
    {
        Assert.Contains("holiday", MarketCalendar.DescribeClosed(Edt(2026, 12, 25, 12)));
    }

    [Fact]
    public void The_day_after_a_holiday_is_open_again()
    {
        // Friday 27 November 2026 — the day after Thanksgiving is a (half) trading day.
        Assert.True(MarketCalendar.IsOpen(Edt(2026, 11, 27, 12)));
    }

    // ---------- Daylight saving ----------

    [Fact]
    public void The_session_tracks_eastern_time_through_the_winter()
    {
        // 14:30 UTC is 09:30 EST in winter but 10:30 EDT in summer, so a UTC-fixed window
        // would get this wrong. Monday 11 January 2027.
        Assert.True(MarketCalendar.IsOpen(Est(2027, 1, 11, 9, 30)));
        Assert.False(MarketCalendar.IsOpen(Est(2027, 1, 11, 9, 29)));
    }

    [Fact]
    public void The_session_tracks_eastern_time_through_the_summer()
    {
        Assert.True(MarketCalendar.IsOpen(Edt(2026, 7, 31, 9, 30)));
        Assert.False(MarketCalendar.IsOpen(Edt(2026, 7, 31, 9, 29)));
    }

    [Fact]
    public void The_same_utc_instant_differs_across_the_dst_boundary()
    {
        // 13:45 UTC: 09:45 EDT (open) in summer, 08:45 EST (shut) in winter.
        Assert.True(MarketCalendar.IsOpen(new DateTimeOffset(2026, 7, 31, 13, 45, 0, TimeSpan.Zero)));
        Assert.False(MarketCalendar.IsOpen(new DateTimeOffset(2027, 1, 11, 13, 45, 0, TimeSpan.Zero)));
    }

    // ---------- Input handling ----------

    [Fact]
    public void A_non_utc_instant_is_converted_rather_than_taken_at_face_value()
    {
        // 18:00 in Paris (UTC+2) is 12:00 in New York — open.
        var paris = new DateTimeOffset(2026, 7, 31, 18, 0, 0, TimeSpan.FromHours(2));

        Assert.True(MarketCalendar.IsOpen(paris));
    }

    [Fact]
    public void Outside_hours_on_a_weekday_gives_the_session_window_reason()
    {
        var reason = MarketCalendar.DescribeClosed(Edt(2026, 7, 31, 18));

        Assert.Contains("09:30", reason);
        Assert.DoesNotContain("weekend", reason);
        Assert.DoesNotContain("holiday", reason);
    }
}
