using DoesTheDogDie.Api;

namespace DoesTheDogDie;

/// <summary>
/// The client-facing, cache-aware surface over the DtDD API. Implementations are expected to be safe for
/// concurrent use from multiple threads and to honor <see cref="CancellationToken"/> for cancellation/timeouts.
/// </summary>
/// <remarks>
/// Every operation returns a <see cref="DtddResult{T}"/> whose <see cref="DtddResult{T}.Source"/> tells the
/// caller how the value was obtained:
/// <list type="bullet">
/// <item><description><see cref="ResultSource.Live"/> — fetched from the DtDD API during this call.</description></item>
/// <item><description><see cref="ResultSource.Cache"/> — served from the cache, within its freshness window.</description></item>
/// <item><description>
/// <see cref="ResultSource.StaleCache"/> — served from the cache past its freshness window. The value is still
/// returned (never silently dropped), but the caller should decide whether stale data is acceptable for its
/// purpose.
/// </description></item>
/// </list>
/// </remarks>
public interface IDtddClient
{
    /// <summary>The most recently observed rate-limit budget, or null if none has been observed yet.</summary>
    RateLimitStatus? CurrentBudget { get; }

    /// <summary>Searches for items.</summary>
    Task<DtddResult<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default);

    /// <summary>Fetches an item's detail, including per-topic stats.</summary>
    Task<DtddResult<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default);

    /// <summary>Fetches ratings for an item, optionally filtered by topic.</summary>
    Task<DtddResult<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default);

    /// <summary>Fetches all topics.</summary>
    Task<DtddResult<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default);

    /// <summary>Fetches all item types.</summary>
    Task<DtddResult<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default);

    /// <summary>Fetches all topic categories.</summary>
    Task<DtddResult<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default);

    /// <summary>Fetches all topic super categories.</summary>
    Task<DtddResult<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default);
}
