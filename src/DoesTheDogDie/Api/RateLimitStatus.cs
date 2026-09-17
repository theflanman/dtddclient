using System.Globalization;
using System.Net.Http.Headers;

namespace DoesTheDogDie.Api;

/// <summary>
/// A snapshot of the DtDD API rate-limit headers observed on a response.
/// </summary>
/// <param name="MinuteLimit">Value of <c>X-RateLimit-Limit-Minute</c>, or null if missing/non-numeric.</param>
/// <param name="MinuteRemaining">Value of <c>X-RateLimit-Remaining-Minute</c>, or null if missing/non-numeric.</param>
/// <param name="MonthLimit">Value of <c>X-RateLimit-Limit-Month</c>, or null if missing/non-numeric.</param>
/// <param name="MonthRemaining">Value of <c>X-RateLimit-Remaining-Month</c>, or null if missing/non-numeric.</param>
/// <param name="ObservedAt">The time this snapshot was taken.</param>
public sealed record RateLimitStatus(
    int? MinuteLimit,
    int? MinuteRemaining,
    int? MonthLimit,
    int? MonthRemaining,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// Parses the DtDD rate-limit headers from an HTTP response's headers.
    /// </summary>
    public static RateLimitStatus FromHeaders(HttpHeaders headers, DateTimeOffset observedAt)
    {
        return new RateLimitStatus(
            ParseInt(headers, "X-RateLimit-Limit-Minute"),
            ParseInt(headers, "X-RateLimit-Remaining-Minute"),
            ParseInt(headers, "X-RateLimit-Limit-Month"),
            ParseInt(headers, "X-RateLimit-Remaining-Month"),
            observedAt);
    }

    private static int? ParseInt(HttpHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault();
        if (value is null || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        return parsed;
    }
}
