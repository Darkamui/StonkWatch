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

    // ---------- SessionDate ----------

    [Fact]
    public void SessionDate_returns_the_eastern_calendar_date()
    {
        Assert.Equal(new DateOnly(2026, 7, 31), MarketCalendar.SessionDate(Edt(2026, 7, 31, 12)));
    }

    [Fact]
    public void SessionDate_uses_eastern_not_utc_for_late_evening_after_hours_trading()
    {
        // 21:00 EDT on 31 July is 01:00 UTC on 1 August. A UTC date would report the wrong
        // session for after-hours trading, which is exactly when this matters.
        var lateEvening = Edt(2026, 7, 31, 21);

        Assert.Equal(new DateOnly(2026, 7, 31), MarketCalendar.SessionDate(lateEvening));
    }

    [Fact]
    public void SessionDate_tracks_eastern_time_through_the_winter()
    {
        Assert.Equal(new DateOnly(2027, 1, 11), MarketCalendar.SessionDate(Est(2027, 1, 11, 9, 30)));
    }

    [Fact]
    public void SessionDate_is_dst_aware_not_pinned_to_a_fixed_utc_offset()
    {
        // 04:30 UTC on 1 August is 00:30 EDT (UTC-4) the same calendar day. A DST-blind
        // implementation pinned to a fixed -05:00 would answer 31 July instead — the two
        // winter-only tests above can't tell the difference because EDT and EST agree on the
        // date for the instants they use.
        var instant = new DateTimeOffset(2026, 8, 1, 4, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 8, 1), MarketCalendar.SessionDate(instant));
    }

    // ---------- Phases ----------

    [Theory]
    [InlineData(3, 59, MarketPhase.Closed)]      // the overnight gap
    [InlineData(4, 0, MarketPhase.PreMarket)]    // the ECNs start matching
    [InlineData(9, 29, MarketPhase.PreMarket)]
    [InlineData(9, 30, MarketPhase.Regular)]
    [InlineData(15, 59, MarketPhase.Regular)]
    [InlineData(16, 0, MarketPhase.AfterHours)]  // the bell belongs to the session starting
    [InlineData(19, 59, MarketPhase.AfterHours)]
    [InlineData(20, 0, MarketPhase.Closed)]
    public void Each_boundary_belongs_to_the_session_that_is_starting(
        int hour, int minute, MarketPhase expected)
    {
        // Friday 31 July 2026. Every bound is inclusive at the bottom and exclusive at the
        // top, so no instant of the day falls in two phases or in none.
        Assert.Equal(expected, MarketCalendar.Phase(Edt(2026, 7, 31, hour, minute)));
    }

    [Fact]
    public void A_weekend_has_no_extended_session()
    {
        // Saturday 1 August 2026, 18:00 — inside the after-hours window on a weekday. The
        // ECNs do not run a session on a day the exchange never opened, and a worker that
        // believed otherwise would poll all weekend.
        Assert.Equal(MarketPhase.Closed, MarketCalendar.Phase(Edt(2026, 8, 1, 18)));
    }

    [Fact]
    public void A_holiday_has_no_extended_session()
    {
        // Independence Day observed, Friday 3 July 2026, 06:00 — pre-market hours.
        Assert.Equal(MarketPhase.Closed, MarketCalendar.Phase(Edt(2026, 7, 3, 6)));
    }

    [Fact]
    public void Extended_hours_are_not_open_hours()
    {
        // IsOpen decides which of Questrade's two price fields is authoritative and whether
        // an alert level counts as crossed. Neither should change because someone printed a
        // hundred shares at 04:15, so IsOpen must stay narrower than "not closed".
        var preMarket = Edt(2026, 7, 31, 5);
        Assert.Equal(MarketPhase.PreMarket, MarketCalendar.Phase(preMarket));
        Assert.False(MarketCalendar.IsOpen(preMarket));

        var afterHours = Edt(2026, 7, 31, 18);
        Assert.Equal(MarketPhase.AfterHours, MarketCalendar.Phase(afterHours));
        Assert.False(MarketCalendar.IsOpen(afterHours));
    }

    [Fact]
    public void Winter_phases_follow_eastern_standard_time()
    {
        // Tuesday 12 January 2027, 09:00 EST. The bounds are wall-clock Eastern, so a UTC
        // offset baked in anywhere would shift every boundary by an hour for four months
        // of the year.
        Assert.Equal(MarketPhase.PreMarket, MarketCalendar.Phase(Est(2027, 1, 12, 9)));
        Assert.Equal(MarketPhase.Regular, MarketCalendar.Phase(Est(2027, 1, 12, 10)));
    }

    // ---------- Display session ----------

    [Theory]
    [InlineData(3, 0, 7, 30)]    // overnight, before pre-market even opens
    [InlineData(5, 0, 7, 30)]    // pre-market
    [InlineData(9, 29, 7, 30)]   // one minute before the bell
    [InlineData(9, 30, 7, 31)]   // the bell itself rolls it forward
    [InlineData(12, 0, 7, 31)]
    [InlineData(18, 0, 7, 31)]   // after-hours still belongs to the session that just ran
    [InlineData(23, 0, 7, 31)]   // and so does the rest of the calendar day
    public void The_displayed_session_rolls_over_at_the_opening_bell(
        int hour, int minute, int expectedMonth, int expectedDay)
    {
        // Friday 31 July 2026; the previous trading day is Thursday the 30th.
        Assert.Equal(
            new DateOnly(2026, expectedMonth, expectedDay),
            MarketCalendar.DisplaySession(Edt(2026, 7, 31, hour, minute)));
    }

    [Fact]
    public void A_weekend_shows_fridays_session()
    {
        // Saturday 1 and Sunday 2 August 2026. Nothing has traded since Friday, so Friday is
        // what the row shows — including on Sunday evening, which is a fresh calendar day.
        Assert.Equal(new DateOnly(2026, 7, 31), MarketCalendar.DisplaySession(Edt(2026, 8, 1, 12)));
        Assert.Equal(new DateOnly(2026, 7, 31), MarketCalendar.DisplaySession(Edt(2026, 8, 2, 22)));
    }

    [Fact]
    public void A_holiday_weekend_skips_back_over_every_closed_day()
    {
        // Tuesday 7 July 2026, 06:00 — pre-market. Monday the 6th traded, so it is the session
        // on screen. But at 06:00 on Monday itself the last session was Thursday the 2nd:
        // Friday the 3rd is Independence Day observed, then the weekend.
        Assert.Equal(new DateOnly(2026, 7, 6), MarketCalendar.DisplaySession(Edt(2026, 7, 7, 6)));
        Assert.Equal(new DateOnly(2026, 7, 2), MarketCalendar.DisplaySession(Edt(2026, 7, 6, 6)));
    }

    [Fact]
    public void The_displayed_session_differs_from_the_calendar_date_before_the_bell()
    {
        // The whole point of having both. SessionDate answers "which day is it" and rolls at
        // midnight; DisplaySession answers "which session is on screen" and rolls at 09:30.
        // Keying a change-percentage baseline on the first flattens every row to 0.00% for
        // the whole of pre-market.
        var preMarket = Edt(2026, 7, 31, 6);
        Assert.Equal(new DateOnly(2026, 7, 31), MarketCalendar.SessionDate(preMarket));
        Assert.Equal(new DateOnly(2026, 7, 30), MarketCalendar.DisplaySession(preMarket));
    }

    [Theory]
    [InlineData(2026, 7, 31, -4)]  // summer: EDT
    [InlineData(2027, 1, 12, -5)]  // winter: EST
    public void SessionStart_is_midnight_eastern_in_that_dates_own_offset(
        int year, int month, int day, int offsetHours)
    {
        // Questrade's candle endpoint takes the offset literally, so a UTC-normalised bound
        // would ask for the wrong days for half the year.
        var start = MarketCalendar.SessionStart(new DateOnly(year, month, day));

        Assert.Equal(TimeSpan.FromHours(offsetHours), start.Offset);
        Assert.Equal(new DateTime(year, month, day, 0, 0, 0), start.DateTime);
    }
}
