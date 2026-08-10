namespace StonkWatch.Web.Services.Monitoring;

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

    public static bool IsOpen(DateTimeOffset instant)
    {
        var eastern = TimeZoneInfo.ConvertTime(instant, Eastern);

        if (eastern.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        if (Holidays.Contains(DateOnly.FromDateTime(eastern.Date)))
        {
            return false;
        }

        var timeOfDay = TimeOnly.FromDateTime(eastern.DateTime);
        return timeOfDay >= Open && timeOfDay < Close;
    }

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
