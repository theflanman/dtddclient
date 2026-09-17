using DoesTheDogDie.Api;
using DoesTheDogDie.Cache;

namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// An <see cref="IDtddCache"/> decorator that throws <see cref="OperationCanceledException"/> from every
/// <c>Put*</c> method if the token it is given is already cancelled, and otherwise delegates to
/// <paramref name="inner"/> unchanged. Used to prove that a cache write triggered by a response that has
/// already arrived is not itself cancellable by the caller's token (I1): if it were, this double would throw
/// and the test would fail.
/// </summary>
internal sealed class ThrowingCtCache(IDtddCache inner) : IDtddCache
{
    public Task<CacheEntry<ItemDetail>?> GetItemAsync(int itemId, CancellationToken ct = default) =>
        inner.GetItemAsync(itemId, ct);

    public Task PutItemAsync(ItemDetail item, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return inner.PutItemAsync(item, fetchedAt, ct);
    }

    public Task<CacheEntry<IReadOnlyList<Rating>>?> GetRatingsAsync(int itemId, int? topicId, CancellationToken ct = default) =>
        inner.GetRatingsAsync(itemId, topicId, ct);

    public Task PutRatingsAsync(int itemId, int? topicId, IReadOnlyList<Rating> ratings, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return inner.PutRatingsAsync(itemId, topicId, ratings, fetchedAt, ct);
    }

    public Task<CacheEntry<IReadOnlyList<Topic>>?> GetTopicsAsync(CancellationToken ct = default) =>
        inner.GetTopicsAsync(ct);

    public Task PutTopicsAsync(IReadOnlyList<Topic> topics, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return inner.PutTopicsAsync(topics, fetchedAt, ct);
    }

    public Task<CacheEntry<IReadOnlyList<ItemType>>?> GetItemTypesAsync(CancellationToken ct = default) =>
        inner.GetItemTypesAsync(ct);

    public Task PutItemTypesAsync(IReadOnlyList<ItemType> itemTypes, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return inner.PutItemTypesAsync(itemTypes, fetchedAt, ct);
    }

    public Task<CacheEntry<IReadOnlyList<TopicCategory>>?> GetTopicCategoriesAsync(CancellationToken ct = default) =>
        inner.GetTopicCategoriesAsync(ct);

    public Task PutTopicCategoriesAsync(IReadOnlyList<TopicCategory> topicCategories, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return inner.PutTopicCategoriesAsync(topicCategories, fetchedAt, ct);
    }

    public Task<CacheEntry<IReadOnlyList<TopicSuperCategory>>?> GetTopicSuperCategoriesAsync(CancellationToken ct = default) =>
        inner.GetTopicSuperCategoriesAsync(ct);

    public Task PutTopicSuperCategoriesAsync(IReadOnlyList<TopicSuperCategory> topicSuperCategories, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return inner.PutTopicSuperCategoriesAsync(topicSuperCategories, fetchedAt, ct);
    }

    public Task<CacheEntry<int?>?> GetLookupAsync(LookupKey key, CancellationToken ct = default) =>
        inner.GetLookupAsync(key, ct);

    public Task PutLookupAsync(LookupKey key, int? itemId, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return inner.PutLookupAsync(key, itemId, fetchedAt, ct);
    }
}
