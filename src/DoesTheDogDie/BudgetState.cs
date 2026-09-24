using DoesTheDogDie.Api;

namespace DoesTheDogDie;

/// <summary>
/// The throttle state an <see cref="IBudgetStore"/> persists, so a restarted <see cref="ThrottledDtddClient"/>
/// neither re-probes an exhausted month nor forgets how much budget is left. Every instant is absolute.
/// </summary>
/// <param name="Budget">The last-observed rate-limit figures, or null if none were ever observed.</param>
/// <param name="MonthEndsAt">When the month <paramref name="Budget"/> describes ends; past it, the figure is stale.</param>
/// <param name="ExhaustedUntil">Until when the month is known to be exhausted, if it is.</param>
/// <param name="BackgroundHeldUntil">Until when background work is held at its floor, if it is.</param>
public sealed record BudgetState(
    RateLimitStatus? Budget,
    DateTimeOffset? MonthEndsAt,
    DateTimeOffset? ExhaustedUntil,
    DateTimeOffset? BackgroundHeldUntil);
