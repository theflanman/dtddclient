namespace DoesTheDogDie;

/// <summary>
/// Which class of work a request belongs to, for <see cref="ThrottledDtddClient.ForPriority"/>. Interactive work
/// is served before any queued background work, and each class stops at its own monthly floor
/// (<see cref="ThrottleOptions.MonthlyReserve"/>, <see cref="ThrottleOptions.BackgroundReserve"/>).
/// </summary>
public enum RequestPriority
{
    /// <summary>
    /// Someone is waiting on the answer. Served first; fails fast when the month's budget is exhausted. The
    /// default, so an unset value never demotes a user-facing request.
    /// </summary>
    Interactive = 0,

    /// <summary>
    /// Speculative work such as keeping the cache warm. Runs only when no interactive work is queued, stops
    /// at a higher floor, and is held rather than failed until the month resets when budget runs out.
    /// </summary>
    Background = 1,
}
