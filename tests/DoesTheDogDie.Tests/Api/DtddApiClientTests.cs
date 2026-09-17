using System.Net;
using DoesTheDogDie.Api;
using DoesTheDogDie.Tests.Support;

namespace DoesTheDogDie.Tests.Api;

public class DtddApiClientTests
{
    private static DtddApiClient CreateClient(StubHttpMessageHandler handler, TimeProvider? timeProvider = null)
    {
        var httpClient = new HttpClient(handler);
        var options = new DtddApiOptions { ApiKey = "ddd_test_key" };
        return new DtddApiClient(httpClient, options, timeProvider);
    }

    private static void AddRateLimitHeaders(HttpResponseMessage response)
    {
        response.Headers.Add("X-RateLimit-Limit-Minute", "30");
        response.Headers.Add("X-RateLimit-Remaining-Minute", "29");
        response.Headers.Add("X-RateLimit-Limit-Month", "5000");
        response.Headers.Add("X-RateLimit-Remaining-Month", "4999");
    }

    [Fact]
    public async Task Search_BuildsUrlAndHeader()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("search.json"), AddRateLimitHeaders));
        var client = CreateClient(handler);

        var response = await client.SearchItemsAsync(ItemSearch.ByQuery("old yeller"));

        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        Assert.Equal("/api/v3/items?q=old%20yeller", request.RequestUri!.PathAndQuery);
        Assert.Equal("ddd_test_key", Assert.Single(request.Headers.GetValues("X-API-KEY")));
        Assert.Single(response.Value);
        Assert.Equal("Old Yeller", response.Value[0].Name);
    }

    [Fact]
    public async Task GetItem_DeserializesDetail()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("item.json"), AddRateLimitHeaders));
        var client = CreateClient(handler);

        var response = await client.GetItemAsync(10752);

        Assert.Equal("/api/v3/items/10752", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(10752, response.Value.Id);
        Assert.Single(response.Value.TopicItemStats);
        Assert.Equal("a dog dies", response.Value.TopicItemStats[0].TopicName);
    }

    [Fact]
    public async Task GetRatings_WithTopic_AddsQuery()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("ratings.json"), AddRateLimitHeaders));
        var client = CreateClient(handler);

        var response = await client.GetRatingsAsync(10752, topicId: 153);

        Assert.Equal("/api/v3/items/10752/ratings?topicId=153", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Single(response.Value);
    }

    [Fact]
    public async Task GetRatings_WithoutTopic()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("ratings.json"), AddRateLimitHeaders));
        var client = CreateClient(handler);

        var response = await client.GetRatingsAsync(10752);

        Assert.Equal("/api/v3/items/10752/ratings", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Single(response.Value);
    }

    [Fact]
    public async Task Topics_Path()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("topics.json"), AddRateLimitHeaders));
        var client = CreateClient(handler);

        var response = await client.GetTopicsAsync();

        Assert.Equal("/api/v3/topics", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Single(response.Value);
    }

    [Fact]
    public async Task ItemTypes_Path()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("itemtypes.json"), AddRateLimitHeaders));
        var client = CreateClient(handler);

        var response = await client.GetItemTypesAsync();

        Assert.Equal("/api/v3/itemtypes", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(3, response.Value.Count);
    }

    [Fact]
    public async Task Categories_Path()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("topiccategories.json"), AddRateLimitHeaders));
        var client = CreateClient(handler);

        var response = await client.GetTopicCategoriesAsync();

        Assert.Equal("/api/v3/topiccategories", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(2, response.Value.Count);
    }

    [Fact]
    public async Task SuperCategories_Path()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("topicsupercategories.json"), AddRateLimitHeaders));
        var client = CreateClient(handler);

        var response = await client.GetTopicSuperCategoriesAsync();

        Assert.Equal("/api/v3/topicsupercategories", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(2, response.Value.Count);
    }

    [Fact]
    public async Task Response_IncludesRateLimit()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("topics.json"), AddRateLimitHeaders));
        var client = CreateClient(handler);

        var response = await client.GetTopicsAsync();

        Assert.Equal(30, response.RateLimit.MinuteLimit);
        Assert.Equal(29, response.RateLimit.MinuteRemaining);
        Assert.Equal(5000, response.RateLimit.MonthLimit);
        Assert.Equal(4999, response.RateLimit.MonthRemaining);
    }

    [Fact]
    public async Task Error401_ThrowsAuthentication()
    {
        var body = """{"error":"invalid_api_key","message":"bad key"}""";
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, body));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<DtddAuthenticationException>(
            () => client.GetTopicsAsync());

        Assert.Equal("invalid_api_key", exception.ErrorCode);
        Assert.Equal("bad key", exception.Message);
    }

    [Fact]
    public async Task Error429Monthly_ThrowsMonthlyWithRetryAfter()
    {
        var body = """{"error":"monthly_limit_exceeded","message":"come back next month"}""";
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(
                (HttpStatusCode)429,
                body,
                response => response.Headers.Add("Retry-After", "120")));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(
            () => client.GetTopicsAsync());

        Assert.Equal(TimeSpan.FromSeconds(120), exception.RetryAfter);
    }

    [Fact]
    public async Task Error500NonJson_ThrowsBase()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<html>oops</html>", System.Text.Encoding.UTF8, "text/html"),
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<DtddApiException>(() => client.GetTopicsAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Null(exception.ErrorCode);
        Assert.IsType<DtddApiException>(exception);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("topics.json")));
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetTopicsAsync(cts.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DoesNotMutateDefaultHeaders()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixtures.Read("topics.json"), AddRateLimitHeaders));
        var httpClient = new HttpClient(handler);
        var options = new DtddApiOptions { ApiKey = "ddd_test_key" };
        var client = new DtddApiClient(httpClient, options);

        await client.GetTopicsAsync();

        Assert.False(httpClient.DefaultRequestHeaders.Contains("X-API-KEY"));
    }
}
