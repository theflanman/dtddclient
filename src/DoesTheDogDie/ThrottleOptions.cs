namespace DoesTheDogDie;

/// <summary>
/// Tunables for the work queue's throttling behavior against the DtDD monthly/per-minute rate limits.
/// </summary>
public sealed class ThrottleOptions
{
    /// <summary>
    /// The interactive floor: once the month's remaining requests fall to this many, interactive requests are
    /// refused until the month resets. Defaults to 50, which no current priority class ever spends - it is
    /// held back for a future class above <see cref="RequestPriority.Interactive"/>.
    /// </summary>
    public int MonthlyReserve { get; init; } = 50;

    /// <summary>
    /// The background floor: once the month's remaining requests fall to this many, background requests are
    /// held until the month resets, leaving the rest for interactive work. Defaults to 500. Must be at least
    /// <see cref="MonthlyReserve"/>, or background work could spend the interactive reserve.
    /// </summary>
    public int BackgroundReserve { get; init; } = 500;

    /// <summary>
    /// Maximum number of requests that may be queued per priority class before
    /// <see cref="DtddQueueFullException"/> is thrown. Per class, so a backlog of held background work can never
    /// lock out interactive requests.
    /// </summary>
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
