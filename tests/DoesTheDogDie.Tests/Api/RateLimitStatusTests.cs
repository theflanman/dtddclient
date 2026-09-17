using System.Net;
using System.Net.Http.Headers;
using DoesTheDogDie.Api;

namespace DoesTheDogDie.Tests.Api;

public class RateLimitStatusTests
{
    [Fact]
    public void FromHeaders_ParsesAllFour()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add("X-RateLimit-Limit-Minute", "30");
        response.Headers.Add("X-RateLimit-Remaining-Minute", "29");
        response.Headers.Add("X-RateLimit-Limit-Month", "5000");
        response.Headers.Add("X-RateLimit-Remaining-Month", "4999");

        var observedAt = DateTimeOffset.UtcNow;
        var status = RateLimitStatus.FromHeaders(response.Headers, observedAt);

        Assert.Equal(30, status.MinuteLimit);
        Assert.Equal(29, status.MinuteRemaining);
        Assert.Equal(5000, status.MonthLimit);
        Assert.Equal(4999, status.MonthRemaining);
        Assert.Equal(observedAt, status.ObservedAt);
    }

    [Fact]
    public void FromHeaders_MissingHeaderIsNull()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add("X-RateLimit-Limit-Minute", "30");

        var status = RateLimitStatus.FromHeaders(response.Headers, DateTimeOffset.UtcNow);

        Assert.Equal(30, status.MinuteLimit);
        Assert.Null(status.MinuteRemaining);
        Assert.Null(status.MonthLimit);
        Assert.Null(status.MonthRemaining);
    }

    [Fact]
    public void FromHeaders_NonNumericIsNull()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add("X-RateLimit-Limit-Minute", "not-a-number");

        var status = RateLimitStatus.FromHeaders(response.Headers, DateTimeOffset.UtcNow);

        Assert.Null(status.MinuteLimit);
    }

    [Fact]
    public void RetryAfter_DeltaSeconds()
    {
        using var response = new HttpResponseMessage();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));

        var result = HeaderParsing.RetryAfter(response, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(45), result);
    }

    [Fact]
    public void RetryAfter_HttpDate()
    {
        var now = DateTimeOffset.UtcNow;
        using var response = new HttpResponseMessage();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(90));

        var result = HeaderParsing.RetryAfter(response, now);

        Assert.NotNull(result);
        Assert.True(Math.Abs((result!.Value - TimeSpan.FromSeconds(90)).TotalSeconds) < 1);
    }

    [Fact]
    public void RetryAfter_Absent_IsNull()
    {
        using var response = new HttpResponseMessage();

        var result = HeaderParsing.RetryAfter(response, DateTimeOffset.UtcNow);

        Assert.Null(result);
    }
}
