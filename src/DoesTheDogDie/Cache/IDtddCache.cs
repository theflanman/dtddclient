using DoesTheDogDie.Api;

namespace DoesTheDogDie.Cache;

/// <summary>
/// A persistent cache for DtDD API data, sitting between the client-facing API and the work queue. A
/// <see langword="null"/> return from any getter means the value has never been cached; a non-null
/// <see cref="CacheEntry{T}"/> is always returned even if stale, with <see cref="CacheEntry{T}.IsStale"/>
/// computed by the implementation at read time from its <see cref="CachePolicy"/> and <see cref="TimeProvider"/>.
/// Implementations must be safe to call concurrently from multiple threads.
/// </summary>
public interface IDtddCache
{
    /// <summary>Gets the cached item detail for <paramref name="itemId"/>, or <see langword="null"/> if never cached.</summary>
    Task<CacheEntry<ItemDetail>?> GetItemAsync(int itemId, CancellationToken ct = default);

    /// <summary>Caches <paramref name="item"/>, replacing any existing entry for the same item id.</summary>
    Task PutItemAsync(ItemDetail item, DateTimeOffset fetchedAt, CancellationToken ct = default);

    /// <summary>
    /// Gets the cached ratings for <paramref name="itemId"/>, optionally scoped to <paramref name="topicId"/>
    /// (a <see langword="null"/> topic id is its own cache entry, distinct from any specific topic), or
    /// <see langword="null"/> if never cached.
    /// </summary>
    Task<CacheEntry<IReadOnlyList<Rating>>?> GetRatingsAsync(int itemId, int? topicId, CancellationToken ct = default);

    /// <summary>Caches <paramref name="ratings"/> for <paramref name="itemId"/>/<paramref name="topicId"/>.</summary>
    Task PutRatingsAsync(int itemId, int? topicId, IReadOnlyList<Rating> ratings, DateTimeOffset fetchedAt, CancellationToken ct = default);

    /// <summary>Gets the cached topic list, or <see langword="null"/> if never cached.</summary>
    Task<CacheEntry<IReadOnlyList<Topic>>?> GetTopicsAsync(CancellationToken ct = default);

    /// <summary>Caches the full topic list.</summary>
    Task PutTopicsAsync(IReadOnlyList<Topic> topics, DateTimeOffset fetchedAt, CancellationToken ct = default);

    /// <summary>Gets the cached item type list, or <see langword="null"/> if never cached.</summary>
    Task<CacheEntry<IReadOnlyList<ItemType>>?> GetItemTypesAsync(CancellationToken ct = default);

    /// <summary>Caches the full item type list.</summary>
    Task PutItemTypesAsync(IReadOnlyList<ItemType> itemTypes, DateTimeOffset fetchedAt, CancellationToken ct = default);

    /// <summary>Gets the cached topic category list, or <see langword="null"/> if never cached.</summary>
    Task<CacheEntry<IReadOnlyList<TopicCategory>>?> GetTopicCategoriesAsync(CancellationToken ct = default);

    /// <summary>Caches the full topic category list.</summary>
    Task PutTopicCategoriesAsync(IReadOnlyList<TopicCategory> topicCategories, DateTimeOffset fetchedAt, CancellationToken ct = default);

    /// <summary>Gets the cached topic super category list, or <see langword="null"/> if never cached.</summary>
    Task<CacheEntry<IReadOnlyList<TopicSuperCategory>>?> GetTopicSuperCategoriesAsync(CancellationToken ct = default);

    /// <summary>Caches the full topic super category list.</summary>
    Task PutTopicSuperCategoriesAsync(IReadOnlyList<TopicSuperCategory> topicSuperCategories, DateTimeOffset fetchedAt, CancellationToken ct = default);

    /// <summary>
    /// Gets the cached item id resolution for <paramref name="key"/>, or <see langword="null"/> if never
    /// cached. When the returned entry's <see cref="CacheEntry{T}.Value"/> is <see langword="null"/>, that is
    /// itself a cached result: the item does not exist on DtDD (a negative lookup), and its staleness is
    /// computed using <see cref="CachePolicy.NegativeLookupMaxAge"/> rather than <see cref="CachePolicy.ItemMaxAge"/>.
    /// </summary>
    Task<CacheEntry<int?>?> GetLookupAsync(LookupKey key, CancellationToken ct = default);

    /// <summary>
    /// Caches the item id resolution for <paramref name="key"/>. Pass <see langword="null"/> for
    /// <paramref name="itemId"/> to cache a negative result (no such item on DtDD).
    /// </summary>
    Task PutLookupAsync(LookupKey key, int? itemId, DateTimeOffset fetchedAt, CancellationToken ct = default);
}
