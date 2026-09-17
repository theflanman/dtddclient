namespace DoesTheDogDie;

/// <summary>
/// Tunables for the work queue's throttling behavior against the DtDD monthly/per-minute rate limits.
/// </summary>
public sealed class ThrottleOptions
{
    /// <summary>
    /// Number of monthly requests to hold back as a safety margin (never spent by the queue). Defaults to 0.
    /// </summary>
    public int MonthlyReserve { get; init; } = 0;

    /// <summary>Maximum number of requests that may be queued before <see cref="DtddQueueFullException"/> is thrown.</summary>
    public int MaxQueueLength { get; init; } = 1000;

    /// <summary>
    /// The per-minute request limit to assume before any rate-limit headers have been observed.
    /// </summary>
    public int DefaultMinuteLimit { get; init; } = 30;

    /// <summary>
    /// Given the current time, returns when the monthly request budget resets. Defaults to
    /// <see cref="NextUtcMonthStart"/>.
    /// </summary>
    public Func<DateTimeOffset, DateTimeOffset> MonthResetRule { get; init; } = NextUtcMonthStart;

    /// <summary>
    /// Converts <paramref name="now"/> to UTC and returns the first instant of the following month.
    /// </summary>
    public static DateTimeOffset NextUtcMonthStart(DateTimeOffset now)
    {
        var utcNow = now.ToUniversalTime();
        var nextMonth = new DateTimeOffset(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        return nextMonth;
    }
}
