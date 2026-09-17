namespace DoesTheDogDie.Api;

/// <summary>
/// A thin, one-to-one client for the DtDD API v3.
/// </summary>
public interface IDtddApiClient
{
    /// <summary>Searches for items via <c>GET /items</c>.</summary>
    Task<ApiResponse<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default);

    /// <summary>Fetches an item's detail, including per-topic stats, via <c>GET /items/{itemId}</c>.</summary>
    Task<ApiResponse<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default);

    /// <summary>Fetches ratings for an item, optionally filtered by topic, via <c>GET /items/{itemId}/ratings</c>.</summary>
    Task<ApiResponse<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default);

    /// <summary>Fetches all topics via <c>GET /topics</c>.</summary>
    Task<ApiResponse<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default);

    /// <summary>Fetches all item types via <c>GET /itemtypes</c>.</summary>
    Task<ApiResponse<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default);

    /// <summary>Fetches all topic categories via <c>GET /topiccategories</c>.</summary>
    Task<ApiResponse<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default);

    /// <summary>Fetches all topic super categories via <c>GET /topicsupercategories</c>.</summary>
    Task<ApiResponse<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default);
}
