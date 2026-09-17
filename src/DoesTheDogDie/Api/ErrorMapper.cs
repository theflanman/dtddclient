using System.Net;

namespace DoesTheDogDie.Api;

/// <summary>
/// Maps a non-success DtDD API response into a typed <see cref="DtddApiException"/>.
/// </summary>
internal static class ErrorMapper
{
    public static DtddApiException Map(HttpStatusCode status, ApiError? error, RateLimitStatus? rateLimit, TimeSpan? retryAfter)
    {
        var message = error?.Message ?? $"HTTP {(int)status}";
        var code = error?.Error;

        return code switch
        {
            "missing_api_key" or "invalid_api_key" => new DtddAuthenticationException(status, code, message, rateLimit),
            "upgrade_required" => new DtddUpgradeRequiredException(status, code, message, rateLimit),
            "not_found" => new DtddNotFoundException(status, code, message, rateLimit),
            "rate_limit_exceeded" => new DtddMinuteRateLimitException(status, code, message, rateLimit, retryAfter),
            "monthly_limit_exceeded" => new DtddMonthlyRateLimitException(status, code, message, rateLimit, retryAfter),
            _ => status switch
            {
                HttpStatusCode.Unauthorized => new DtddAuthenticationException(status, code, message, rateLimit),
                HttpStatusCode.NotFound => new DtddNotFoundException(status, code, message, rateLimit),
                _ => new DtddApiException(status, code, message, rateLimit),
            },
        };
    }
}
