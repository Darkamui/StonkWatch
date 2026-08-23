namespace StonkWatch.Web.Services.Monitoring;

/// <summary>
/// Which part of the trading day an instant falls in. <see cref="Closed"/> means no trading of
/// any kind — a weekend, a holiday, or the overnight gap — and is the only phase in which a
/// quote cannot move.
/// </summary>
public enum MarketPhase
{
    Closed,
    PreMarket,
    Regular,
    AfterHours
}

/// <summary>
/// US/Canada equity regular session. Nasdaq, NYSE and TSX all trade 09:30–16:00 America/New_York,
/// so one window covers the whole watchlist.
/// </summary>
/// <remarks>
/// The holiday list is US-only. On a Canada-only holiday (Canada Day, Civic Holiday) the job
/// still polls; TSX tickers return an unchanged last price, which crosses nothing. That costs
/// a handful of wasted requests a year and is cheaper than maintaining two calendars.
/// </remarks>
public static class MarketCalendar
{
    private static readonly TimeZoneInfo Eastern = ResolveEastern();

    private static readonly TimeOnly Open = new(9, 30);
    private static readonly TimeOnly Close = new(16, 0);

    // Extended-hours bounds. Questrade's own pre/post window, and the one TradingView draws:
    // 04:00 is when the ECNs start matching and 20:00 is when they stop.
    private static readonly TimeOnly PreMarketOpen = new(4, 0);
    private static readonly TimeOnly AfterHoursClose = new(20, 0);

    /// <summary>
    /// US market holidays when the exchange is shut for the whole day. Half-days are treated
    /// as normal sessions — polling after an early close is harmless.
    /// </summary>
    private static readonly HashSet<DateOnly> Holidays =
    [
        // 2026
        new(2026, 1, 1),   // New Year's Day
        new(2026, 1, 19),  // Martin Luther King Jr. Day
        new(2026, 2, 16),  // Washington's Birthday
        new(2026, 4, 3),   // Good Friday
        new(2026, 5, 25),  // Memorial Day
        new(2026, 6, 19),  // Juneteenth
        new(2026, 7, 3),   // Independence Day (observed)
        new(2026, 9, 7),   // Labor Day
        new(2026, 11, 26), // Thanksgiving
        new(2026, 12, 25), // Christmas
        // 2027
        new(2027, 1, 1),
        new(2027, 1, 18),
        new(2027, 2, 15),
        new(2027, 3, 26),
        new(2027, 5, 31),
        new(2027, 6, 18),  // Juneteenth (observed)
        new(2027, 7, 5),   // Independence Day (observed)
        new(2027, 9, 6),
        new(2027, 11, 25),
        new(2027, 12, 24)  // Christmas (observed)
    ];

    /// <summary>
    /// True only during the regular session. Deliberately narrower than
    /// <see cref="Phase"/> being something other than <see cref="MarketPhase.Closed"/>:
    /// callers use this to decide which of Questrade's two price fields is authoritative and
    /// whether an alert level counts as crossed, and neither should change because someone
    /// printed 200 shares at 04:15.
    /// </summary>
    public static bool IsOpen(DateTimeOffset instant) => Phase(instant) == MarketPhase.Regular;

    /// <summary>
    /// The trading phase <paramref name="instant"/> falls in. Whole-day holidays and weekends
    /// are <see cref="MarketPhase.Closed"/> outright — the ECNs do not run an extended session
    /// on a day the exchange never opened.
    /// </summary>
    /// <remarks>
    /// Half-days are treated as full ones here, as they are by <see cref="Holidays"/>: the
    /// 13:00 close on Christmas Eve reports <see cref="MarketPhase.Regular"/> until 16:00. The
    /// cost is a few wasted polls on a handful of afternoons a year, against a second calendar
    /// to maintain.
    /// </remarks>
    public static MarketPhase Phase(DateTimeOffset instant)
    {
        var eastern = TimeZoneInfo.ConvertTime(instant, Eastern);

        if (eastern.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return MarketPhase.Closed;
        }

        if (Holidays.Contains(DateOnly.FromDateTime(eastern.Date)))
        {
            return MarketPhase.Closed;
        }

        var timeOfDay = TimeOnly.FromDateTime(eastern.DateTime);

        // Each bound is exclusive at the top and inclusive at the bottom, so an instant
        // exactly on a boundary belongs to the session that is starting, never the one that
        // just ended. 16:00:00 sharp is after-hours, not the last tick of the regular day.
        if (timeOfDay < PreMarketOpen || timeOfDay >= AfterHoursClose)
        {
            return MarketPhase.Closed;
        }

        if (timeOfDay < Open)
        {
            return MarketPhase.PreMarket;
        }

        return timeOfDay < Close ? MarketPhase.Regular : MarketPhase.AfterHours;
    }

    /// <summary>
    /// The Eastern calendar date of <paramref name="instant"/>. Used to key a session's
    /// previous close: a UTC date would be wrong for anything after 19:00 ET, which is
    /// exactly when after-hours trading happens.
    /// </summary>
    public static DateOnly SessionDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Eastern).Date);

    /// <summary>Human-readable reason a tick did nothing, for the job_runs record.</summary>
    public static string DescribeClosed(DateTimeOffset instant)
    {
        var eastern = TimeZoneInfo.ConvertTime(instant, Eastern);

        if (eastern.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return "Market closed (weekend)";
        }

        if (Holidays.Contains(DateOnly.FromDateTime(eastern.Date)))
        {
            return "Market closed (holiday)";
        }

        return "Market closed (outside 09:30-16:00 ET)";
    }

    private static TimeZoneInfo ResolveEastern()
    {
        // IANA works on Linux and, since .NET 6, on Windows via ICU. The Windows id is kept
        // as a fallback for hosts running with the invariant-globalization switch.
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
