using System.Net;
using DoesTheDogDie.Api;

namespace DoesTheDogDie.Tests.Api;

public class ErrorMapperTests
{
    [Fact]
    public void MissingApiKey_MapsToAuthenticationException()
    {
        var error = new ApiError { Error = "missing_api_key", Message = "no key" };

        var exception = ErrorMapper.Map(HttpStatusCode.Unauthorized, error, rateLimit: null, retryAfter: null);

        var authException = Assert.IsType<DtddAuthenticationException>(exception);
        Assert.Equal("missing_api_key", authException.ErrorCode);
        Assert.Equal(HttpStatusCode.Unauthorized, authException.StatusCode);
        Assert.Equal("no key", authException.Message);
    }

    [Fact]
    public void InvalidApiKey_MapsToAuthenticationException()
    {
        var error = new ApiError { Error = "invalid_api_key", Message = "bad key" };

        var exception = ErrorMapper.Map(HttpStatusCode.Unauthorized, error, rateLimit: null, retryAfter: null);

        var authException = Assert.IsType<DtddAuthenticationException>(exception);
        Assert.Equal("invalid_api_key", authException.ErrorCode);
    }

    [Fact]
    public void UpgradeRequired_MapsToUpgradeRequiredException()
    {
        var error = new ApiError { Error = "upgrade_required", Message = "pay up" };

        var exception = ErrorMapper.Map(HttpStatusCode.Forbidden, error, rateLimit: null, retryAfter: null);

        var upgradeException = Assert.IsType<DtddUpgradeRequiredException>(exception);
        Assert.Equal("upgrade_required", upgradeException.ErrorCode);
    }

    [Fact]
    public void NotFound_MapsToNotFoundException()
    {
        var error = new ApiError { Error = "not_found", Message = "gone" };

        var exception = ErrorMapper.Map(HttpStatusCode.NotFound, error, rateLimit: null, retryAfter: null);

        var notFoundException = Assert.IsType<DtddNotFoundException>(exception);
        Assert.Equal("not_found", notFoundException.ErrorCode);
    }

    [Fact]
    public void RateLimitExceeded_MapsToMinuteRateLimitException()
    {
        var error = new ApiError { Error = "rate_limit_exceeded", Message = "slow down" };
        var retryAfter = TimeSpan.FromSeconds(12);

        var exception = ErrorMapper.Map(HttpStatusCode.TooManyRequests, error, rateLimit: null, retryAfter: retryAfter);

        var minuteException = Assert.IsType<DtddMinuteRateLimitException>(exception);
        Assert.Equal("rate_limit_exceeded", minuteException.ErrorCode);
        Assert.Equal(retryAfter, minuteException.RetryAfter);
    }

    [Fact]
    public void MonthlyLimitExceeded_MapsToMonthlyRateLimitException()
    {
        var error = new ApiError { Error = "monthly_limit_exceeded", Message = "come back next month" };
        var retryAfter = TimeSpan.FromDays(3);

        var exception = ErrorMapper.Map(HttpStatusCode.TooManyRequests, error, rateLimit: null, retryAfter: retryAfter);

        var monthlyException = Assert.IsType<DtddMonthlyRateLimitException>(exception);
        Assert.Equal("monthly_limit_exceeded", monthlyException.ErrorCode);
        Assert.Equal(retryAfter, monthlyException.RetryAfter);
    }

    [Fact]
    public void UnknownCodeOn429_MapsToBaseException()
    {
        var error = new ApiError { Error = "something_else", Message = "huh" };

        var exception = ErrorMapper.Map(HttpStatusCode.TooManyRequests, error, rateLimit: null, retryAfter: null);

        Assert.IsType<DtddApiException>(exception);
    }

    [Fact]
    public void NullBody_MapsToBaseExceptionWithHttpStatusMessage()
    {
        var exception = ErrorMapper.Map(HttpStatusCode.InternalServerError, error: null, rateLimit: null, retryAfter: null);

        Assert.IsType<DtddApiException>(exception);
        Assert.Equal("HTTP 500", exception.Message);
    }

    [Fact]
    public void NullBody_401_MapsToAuthenticationException()
    {
        var exception = ErrorMapper.Map(HttpStatusCode.Unauthorized, error: null, rateLimit: null, retryAfter: null);

        Assert.IsType<DtddAuthenticationException>(exception);
        Assert.Equal("HTTP 401", exception.Message);
    }

    [Fact]
    public void NullBody_404_MapsToNotFoundException()
    {
        var exception = ErrorMapper.Map(HttpStatusCode.NotFound, error: null, rateLimit: null, retryAfter: null);

        Assert.IsType<DtddNotFoundException>(exception);
        Assert.Equal("HTTP 404", exception.Message);
    }

    [Fact]
    public void RateLimitPreserved_OnException()
    {
        var error = new ApiError { Error = "not_found", Message = "gone" };
        var rateLimit = new RateLimitStatus(30, 29, 5000, 4999, DateTimeOffset.UtcNow);

        var exception = ErrorMapper.Map(HttpStatusCode.NotFound, error, rateLimit, retryAfter: null);

        Assert.Equal(rateLimit, ((DtddApiException)exception).RateLimit);
    }
}
