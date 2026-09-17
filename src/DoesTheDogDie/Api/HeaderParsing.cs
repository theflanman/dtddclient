namespace DoesTheDogDie.Api;

/// <summary>
/// Helpers for parsing standard HTTP headers relevant to rate limiting.
/// </summary>
internal static class HeaderParsing
{
    /// <summary>
    /// Computes how long to wait before retrying, based on the response's <c>Retry-After</c> header.
    /// </summary>
    /// <param name="response">The HTTP response, typically a 429.</param>
    /// <param name="now">The current time, used to resolve an HTTP-date form of the header.</param>
    /// <returns>The wait duration, or null if the header is absent.</returns>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var difference = date - now;
            return difference < TimeSpan.Zero ? TimeSpan.Zero : difference;
        }

        return null;
    }
}
