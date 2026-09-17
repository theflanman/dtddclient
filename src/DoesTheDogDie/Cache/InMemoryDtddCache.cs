using DoesTheDogDie.Api;

namespace DoesTheDogDie.Cache;

/// <summary>
/// A process-local, non-persistent <see cref="IDtddCache"/> backed by in-memory dictionaries guarded by a
/// single lock. Suitable as the first simple cache implementation, or for tests. Cached list values are
/// returned as the same instance that was stored; callers must not mutate them.
/// </summary>
public sealed class InMemoryDtddCache : IDtddCache
{
    private readonly Lock _lock = new();
    private readonly CachePolicy _policy;
    private readonly TimeProvider _time;

    private readonly Dictionary<int, Slot<ItemDetail>> _items = [];
    private readonly Dictionary<(int ItemId, int? TopicId), Slot<IReadOnlyList<Rating>>> _ratings = [];
    private readonly Dictionary<LookupKey, Slot<int?>> _lookups = [];

    private Slot<IReadOnlyList<Topic>>? _topics;
    private Slot<IReadOnlyList<ItemType>>? _itemTypes;
    private Slot<IReadOnlyList<TopicCategory>>? _topicCategories;
    private Slot<IReadOnlyList<TopicSuperCategory>>? _topicSuperCategories;

    public InMemoryDtddCache(CachePolicy? policy = null, TimeProvider? timeProvider = null)
    {
        _policy = policy ?? CachePolicy.Default;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task<CacheEntry<ItemDetail>?> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(Read(_items, itemId, _policy.ItemMaxAge));
        }
    }

    public Task PutItemAsync(ItemDetail item, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _items[item.Id] = new Slot<ItemDetail>(item, fetchedAt);
        }

        return Task.CompletedTask;
    }

    public Task<CacheEntry<IReadOnlyList<Rating>>?> GetRatingsAsync(int itemId, int? topicId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(Read(_ratings, (itemId, topicId), _policy.RatingsMaxAge));
        }
    }

    public Task PutRatingsAsync(int itemId, int? topicId, IReadOnlyList<Rating> ratings, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _ratings[(itemId, topicId)] = new Slot<IReadOnlyList<Rating>>(ratings, fetchedAt);
        }

        return Task.CompletedTask;
    }

    public Task<CacheEntry<IReadOnlyList<Topic>>?> GetTopicsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(ReadSlot(_topics, _policy.TaxonomyMaxAge));
        }
    }

    public Task PutTopicsAsync(IReadOnlyList<Topic> topics, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _topics = new Slot<IReadOnlyList<Topic>>(topics, fetchedAt);
        }

        return Task.CompletedTask;
    }

    public Task<CacheEntry<IReadOnlyList<ItemType>>?> GetItemTypesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(ReadSlot(_itemTypes, _policy.TaxonomyMaxAge));
        }
    }

    public Task PutItemTypesAsync(IReadOnlyList<ItemType> itemTypes, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _itemTypes = new Slot<IReadOnlyList<ItemType>>(itemTypes, fetchedAt);
        }

        return Task.CompletedTask;
    }

    public Task<CacheEntry<IReadOnlyList<TopicCategory>>?> GetTopicCategoriesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(ReadSlot(_topicCategories, _policy.TaxonomyMaxAge));
        }
    }

    public Task PutTopicCategoriesAsync(IReadOnlyList<TopicCategory> topicCategories, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _topicCategories = new Slot<IReadOnlyList<TopicCategory>>(topicCategories, fetchedAt);
        }

        return Task.CompletedTask;
    }

    public Task<CacheEntry<IReadOnlyList<TopicSuperCategory>>?> GetTopicSuperCategoriesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(ReadSlot(_topicSuperCategories, _policy.TaxonomyMaxAge));
        }
    }

    public Task PutTopicSuperCategoriesAsync(IReadOnlyList<TopicSuperCategory> topicSuperCategories, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _topicSuperCategories = new Slot<IReadOnlyList<TopicSuperCategory>>(topicSuperCategories, fetchedAt);
        }

        return Task.CompletedTask;
    }

    public Task<CacheEntry<int?>?> GetLookupAsync(LookupKey key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_lookups.TryGetValue(key, out var slot))
            {
                return Task.FromResult<CacheEntry<int?>?>(null);
            }

            var maxAge = slot.Value is null ? _policy.NegativeLookupMaxAge : _policy.ItemMaxAge;
            var isStale = _time.GetUtcNow() - slot.FetchedAt > maxAge;
            return Task.FromResult<CacheEntry<int?>?>(new CacheEntry<int?>(slot.Value, slot.FetchedAt, isStale));
        }
    }

    public Task PutLookupAsync(LookupKey key, int? itemId, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _lookups[key] = new Slot<int?>(itemId, fetchedAt);
        }

        return Task.CompletedTask;
    }

    private CacheEntry<T>? Read<TKey, T>(Dictionary<TKey, Slot<T>> dictionary, TKey key, TimeSpan maxAge)
        where TKey : notnull
    {
        return dictionary.TryGetValue(key, out var slot) ? ToEntry(slot, maxAge) : null;
    }

    private CacheEntry<T>? ReadSlot<T>(Slot<T>? slot, TimeSpan maxAge) => slot is null ? null : ToEntry<T>(slot.Value, maxAge);

    private CacheEntry<T> ToEntry<T>(Slot<T> slot, TimeSpan maxAge)
    {
        var isStale = _time.GetUtcNow() - slot.FetchedAt > maxAge;
        return new CacheEntry<T>(slot.Value, slot.FetchedAt, isStale);
    }

    private readonly record struct Slot<T>(T Value, DateTimeOffset FetchedAt);
}
