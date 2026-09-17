using System.Collections.Concurrent;
using DoesTheDogDie.Api;

namespace DoesTheDogDie.Cache;

/// <summary>
/// A read-through caching decorator over an <see cref="IDtddClient"/>. A cache hit returns immediately; a
/// cache miss calls through to <paramref name="inner"/> (see constructor) and stores the result. A stale
/// entry is returned immediately (flagged <see cref="ResultSource.StaleCache"/>), and — when
/// <see cref="CachePolicy.RefreshStaleInBackground"/> is set — a fire-and-forget refresh is started to
/// repopulate the cache, coalesced per key so concurrent stale reads only trigger one inner call.
/// </summary>
public sealed class CachedDtddClient : IDtddClient
{
    private readonly IDtddClient _inner;
    private readonly IDtddCache _cache;
    private readonly CachePolicy _policy;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlightRefreshes = new();

    /// <summary>Raised when a background refresh throws; the exception is otherwise swallowed.</summary>
    public event Action<Exception>? RefreshFailed;

    /// <summary>The most recently started (or coalesced-into) background refresh task, for tests to await.</summary>
    internal Task? LastRefresh { get; private set; }

    public CachedDtddClient(IDtddClient inner, IDtddCache cache, CachePolicy? policy = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);

        _inner = inner;
        _cache = cache;
        _policy = policy ?? CachePolicy.Default;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public RateLimitStatus? CurrentBudget => _inner.CurrentBudget;

    /// <inheritdoc />
    public async Task<DtddResult<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);

        var key = LookupKey.FromSearch(search);
        if (key is null)
        {
            // Free-text query: not cacheable by exact key, pass straight through.
            return await _inner.SearchItemsAsync(search, ct).ConfigureAwait(false);
        }

        var lookupEntry = await _cache.GetLookupAsync(key, ct).ConfigureAwait(false);
        if (lookupEntry is null)
        {
            var result = await _inner.SearchItemsAsync(search, ct).ConfigureAwait(false);
            var resolvedId = result.Value.Count > 0 ? result.Value[0].Id : (int?)null;
            await _cache.PutLookupAsync(key, resolvedId, result.FetchedAt, ct).ConfigureAwait(false);
            return result;
        }

        if (lookupEntry.Value is null)
        {
            // A cached negative lookup: the item does not exist on DtDD.
            if (!lookupEntry.IsStale)
            {
                return new DtddResult<IReadOnlyList<Item>>([], ResultSource.Cache, lookupEntry.FetchedAt);
            }

            if (_policy.RefreshStaleInBackground)
            {
                StartBackgroundRefresh<int?>(
                    $"lookup:{key.Kind}:{key.Key}",
                    async c =>
                    {
                        var innerResult = await _inner.SearchItemsAsync(search, c).ConfigureAwait(false);
                        var resolvedId = innerResult.Value.Count > 0 ? innerResult.Value[0].Id : (int?)null;
                        return new DtddResult<int?>(resolvedId, innerResult.Source, innerResult.FetchedAt);
                    },
                    (value, fetchedAt, c) => _cache.PutLookupAsync(key, value, fetchedAt, c));
            }

            return new DtddResult<IReadOnlyList<Item>>([], ResultSource.StaleCache, lookupEntry.FetchedAt);
        }

        var itemId = lookupEntry.Value.Value;
        var itemEntry = await _cache.GetItemAsync(itemId, ct).ConfigureAwait(false);
        if (itemEntry is not null)
        {
            if (!itemEntry.IsStale)
            {
                return new DtddResult<IReadOnlyList<Item>>([itemEntry.Value], ResultSource.Cache, itemEntry.FetchedAt);
            }

            if (_policy.RefreshStaleInBackground)
            {
                StartBackgroundRefresh(
                    $"item:{itemId}",
                    c => _inner.GetItemAsync(itemId, c),
                    (value, fetchedAt, c) => _cache.PutItemAsync(value, fetchedAt, c));
            }

            return new DtddResult<IReadOnlyList<Item>>([itemEntry.Value], ResultSource.StaleCache, itemEntry.FetchedAt);
        }

        var itemResult = await _inner.GetItemAsync(itemId, ct).ConfigureAwait(false);
        await _cache.PutItemAsync(itemResult.Value, itemResult.FetchedAt, ct).ConfigureAwait(false);
        return new DtddResult<IReadOnlyList<Item>>([itemResult.Value], ResultSource.Live, itemResult.FetchedAt);
    }

    /// <inheritdoc />
    public Task<DtddResult<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default) =>
        ReadThroughAsync(
            $"item:{itemId}",
            c => _cache.GetItemAsync(itemId, c),
            c => _inner.GetItemAsync(itemId, c),
            (value, fetchedAt, c) => _cache.PutItemAsync(value, fetchedAt, c),
            ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default) =>
        ReadThroughAsync(
            topicId is { } id ? $"ratings:{itemId}:{id}" : $"ratings:{itemId}",
            c => _cache.GetRatingsAsync(itemId, topicId, c),
            c => _inner.GetRatingsAsync(itemId, topicId, c),
            (value, fetchedAt, c) => _cache.PutRatingsAsync(itemId, topicId, value, fetchedAt, c),
            ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default) =>
        ReadThroughAsync(
            "topics",
            c => _cache.GetTopicsAsync(c),
            c => _inner.GetTopicsAsync(c),
            (value, fetchedAt, c) => _cache.PutTopicsAsync(value, fetchedAt, c),
            ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default) =>
        ReadThroughAsync(
            "itemTypes",
            c => _cache.GetItemTypesAsync(c),
            c => _inner.GetItemTypesAsync(c),
            (value, fetchedAt, c) => _cache.PutItemTypesAsync(value, fetchedAt, c),
            ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default) =>
        ReadThroughAsync(
            "topicCategories",
            c => _cache.GetTopicCategoriesAsync(c),
            c => _inner.GetTopicCategoriesAsync(c),
            (value, fetchedAt, c) => _cache.PutTopicCategoriesAsync(value, fetchedAt, c),
            ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default) =>
        ReadThroughAsync(
            "topicSuperCategories",
            c => _cache.GetTopicSuperCategoriesAsync(c),
            c => _inner.GetTopicSuperCategoriesAsync(c),
            (value, fetchedAt, c) => _cache.PutTopicSuperCategoriesAsync(value, fetchedAt, c),
            ct);

    /// <summary>
    /// The generic read-through pattern shared by every getter except <see cref="SearchItemsAsync"/> (which
    /// has extra lookup-then-item indirection): cache miss calls inner and stores; a fresh hit is served from
    /// cache; a stale hit is served from cache immediately, optionally kicking off a coalesced background
    /// refresh.
    /// </summary>
    private async Task<DtddResult<T>> ReadThroughAsync<T>(
        string key,
        Func<CancellationToken, Task<CacheEntry<T>?>> getCached,
        Func<CancellationToken, Task<DtddResult<T>>> fetchInner,
        Func<T, DateTimeOffset, CancellationToken, Task> put,
        CancellationToken ct)
    {
        var entry = await getCached(ct).ConfigureAwait(false);
        if (entry is null)
        {
            var result = await fetchInner(ct).ConfigureAwait(false);
            await put(result.Value, result.FetchedAt, ct).ConfigureAwait(false);
            return result;
        }

        if (!entry.IsStale)
        {
            return new DtddResult<T>(entry.Value, ResultSource.Cache, entry.FetchedAt);
        }

        if (_policy.RefreshStaleInBackground)
        {
            StartBackgroundRefresh(key, fetchInner, put);
        }

        return new DtddResult<T>(entry.Value, ResultSource.StaleCache, entry.FetchedAt);
    }

    /// <summary>
    /// Starts (or joins an already-running) fire-and-forget background refresh for <paramref name="key"/>,
    /// setting <see cref="LastRefresh"/> to the (possibly shared) task. <see cref="Lazy{T}"/> with
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> guarantees <paramref name="fetchInner"/> is
    /// invoked at most once per key even if this is called concurrently from multiple threads, which a plain
    /// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,System.Func{TKey,TValue})"/> would not:
    /// its factory can run more than once under contention.
    /// </summary>
    private void StartBackgroundRefresh<T>(
        string key,
        Func<CancellationToken, Task<DtddResult<T>>> fetchInner,
        Func<T, DateTimeOffset, CancellationToken, Task> put)
    {
        // Task.Run forces a real asynchronous boundary before the refresh runs. Without it, an inner client
        // whose async methods happen to complete synchronously (no genuine I/O to await, as in tests) would
        // run the whole refresh inline on this thread before this method returns — defeating "fire-and-forget"
        // and making the caller's immediate StaleCache return misleadingly appear to race with, or even follow,
        // the refresh's own completion.
        var lazy = _inFlightRefreshes.GetOrAdd(
            key,
            _ => new Lazy<Task>(() => Task.Run(() => RunRefreshAsync(key, fetchInner, put)), LazyThreadSafetyMode.ExecutionAndPublication));
        LastRefresh = lazy.Value;
    }

    /// <summary>
    /// Executes a single background refresh: fetches from the inner client with <see cref="CancellationToken.None"/>
    /// (the caller's token, which triggered the stale read, must not cancel a refresh benefiting future callers),
    /// stores the result, and never faults — any exception is reported via <see cref="RefreshFailed"/> instead.
    /// </summary>
    private async Task RunRefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<DtddResult<T>>> fetchInner,
        Func<T, DateTimeOffset, CancellationToken, Task> put)
    {
        try
        {
            var result = await fetchInner(CancellationToken.None).ConfigureAwait(false);
            await put(result.Value, result.FetchedAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RefreshFailed?.Invoke(ex);
        }
        finally
        {
            _inFlightRefreshes.TryRemove(key, out _);
        }
    }
}
