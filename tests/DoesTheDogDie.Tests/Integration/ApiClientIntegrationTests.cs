using DoesTheDogDie.Api;
using DoesTheDogDie.Tests.Support;

namespace DoesTheDogDie.Tests.Integration;

/// <summary>
/// Exercises <see cref="DtddApiClient"/> against the live DtDD API. Skipped unless an API key is
/// available (see <see cref="IntegrationFactAttribute"/>). Each test spends one real request
/// against the free-tier monthly budget, so no test here should loop, retry, or add more calls.
/// </summary>
public class ApiClientIntegrationTests : IClassFixture<ApiClientFixture>
{
    private readonly ApiClientFixture _fixture;

    public ApiClientIntegrationTests(ApiClientFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationFact]
    public async Task Search_OldYeller_Returns10752()
    {
        var response = await _fixture.Client!.SearchItemsAsync(ItemSearch.ByQuery("old yeller"));

        Assert.Contains(response.Value, item => item.Id == 10752);
        Assert.NotNull(response.RateLimit.MonthLimit);
    }

    [IntegrationFact]
    public async Task GetItem_10752_HasTopic153()
    {
        var response = await _fixture.Client!.GetItemAsync(10752);

        Assert.Contains(response.Value.TopicItemStats, stat => stat.TopicId == 153);
        Assert.NotNull(response.RateLimit.MonthLimit);
    }

    [IntegrationFact]
    public async Task Topics_NonEmpty()
    {
        var response = await _fixture.Client!.GetTopicsAsync();

        Assert.NotEmpty(response.Value);
        Assert.NotNull(response.RateLimit.MonthLimit);
    }
}
