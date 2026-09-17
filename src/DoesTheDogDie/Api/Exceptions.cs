using System.Net;

namespace DoesTheDogDie.Api;

/// <summary>
/// Base exception for a non-success response from the DtDD API.
/// </summary>
public class DtddApiException : Exception
{
    public DtddApiException(HttpStatusCode statusCode, string? errorCode, string message, RateLimitStatus? rateLimit)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        RateLimit = rateLimit;
    }

    /// <summary>The HTTP status code of the response.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The DtDD error code from the response body, if any (e.g. <c>invalid_api_key</c>).</summary>
    public string? ErrorCode { get; }

    /// <summary>The rate-limit snapshot observed on the response, if available.</summary>
    public RateLimitStatus? RateLimit { get; }
}

/// <summary>
/// Thrown for a 401 response (<c>missing_api_key</c> or <c>invalid_api_key</c>).
/// </summary>
public sealed class DtddAuthenticationException : DtddApiException
{
    public DtddAuthenticationException(HttpStatusCode statusCode, string? errorCode, string message, RateLimitStatus? rateLimit)
        : base(statusCode, errorCode, message, rateLimit)
    {
    }
}

/// <summary>
/// Thrown for a 403 <c>upgrade_required</c> response.
/// </summary>
public sealed class DtddUpgradeRequiredException : DtddApiException
{
    public DtddUpgradeRequiredException(HttpStatusCode statusCode, string? errorCode, string message, RateLimitStatus? rateLimit)
        : base(statusCode, errorCode, message, rateLimit)
    {
    }
}

/// <summary>
/// Thrown for a 404 <c>not_found</c> response.
/// </summary>
public sealed class DtddNotFoundException : DtddApiException
{
    public DtddNotFoundException(HttpStatusCode statusCode, string? errorCode, string message, RateLimitStatus? rateLimit)
        : base(statusCode, errorCode, message, rateLimit)
    {
    }
}

/// <summary>
/// Base exception for a 429 response, carrying how long to wait before retrying.
/// </summary>
public abstract class DtddRateLimitException : DtddApiException
{
    protected DtddRateLimitException(
        HttpStatusCode statusCode, string? errorCode, string message, RateLimitStatus? rateLimit, TimeSpan? retryAfter)
        : base(statusCode, errorCode, message, rateLimit)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long to wait before retrying, parsed from the <c>Retry-After</c> header.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Thrown for a 429 <c>rate_limit_exceeded</c> response (per-minute limit; a short wait).
/// </summary>
public sealed class DtddMinuteRateLimitException : DtddRateLimitException
{
    public DtddMinuteRateLimitException(
        HttpStatusCode statusCode, string? errorCode, string message, RateLimitStatus? rateLimit, TimeSpan? retryAfter)
        : base(statusCode, errorCode, message, rateLimit, retryAfter)
    {
    }
}

/// <summary>
/// Thrown for a 429 <c>monthly_limit_exceeded</c> response (the queue is dead until the month rolls over).
/// </summary>
public sealed class DtddMonthlyRateLimitException : DtddRateLimitException
{
    public DtddMonthlyRateLimitException(
        HttpStatusCode statusCode, string? errorCode, string message, RateLimitStatus? rateLimit, TimeSpan? retryAfter)
        : base(statusCode, errorCode, message, rateLimit, retryAfter)
    {
    }
}
