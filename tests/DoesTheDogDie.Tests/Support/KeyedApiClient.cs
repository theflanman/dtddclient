using DoesTheDogDie.Api;

namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// Forwards to a <see cref="FakeApiClient"/> while identifying a budget, the way <see cref="DtddApiClient"/>
/// does for its API key. The fake itself stays unidentified, so tests can hold both kinds.
/// </summary>
internal sealed class KeyedApiClient(FakeApiClient inner, string budgetId) : IDtddApiClient, IBudgetIdentity
{
    public string BudgetId => budgetId;

    public Task<ApiResponse<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default) => inner.SearchItemsAsync(search, ct);

    public Task<ApiResponse<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default) => inner.GetItemAsync(itemId, ct);

    public Task<ApiResponse<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default) => inner.GetRatingsAsync(itemId, topicId, ct);

    public Task<ApiResponse<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default) => inner.GetTopicsAsync(ct);

    public Task<ApiResponse<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default) => inner.GetItemTypesAsync(ct);

    public Task<ApiResponse<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default) => inner.GetTopicCategoriesAsync(ct);

    public Task<ApiResponse<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default) => inner.GetTopicSuperCategoriesAsync(ct);
}
