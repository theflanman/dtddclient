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
    private Task? _lastRefresh;

    /// <summary>Raised when a background refresh throws; the exception is otherwise swallowed.</summary>
    public event Action<Exception>? RefreshFailed;

    /// <summary>
    /// A test seam only: the most recently started (or coalesced-into) background refresh task. It exists so
    /// tests can deterministically await a refresh instead of polling; production callers have no need of it
    /// (a background refresh is meant to be fire-and-forget). Only meaningful from the thread that itself just
    /// triggered a stale read — a concurrent caller on another thread can observe an unrelated, older, or
    /// already-completed refresh here, or race the field being overwritten by yet another stale read. Backed
    /// by a volatile read/write (rather than a plain auto-property) so a value set on one thread is visible
    /// promptly to a test awaiting it from another.
    /// </summary>
    internal Task? LastRefresh
    {
        get => Volatile.Read(ref _lastRefresh);
        private set => Volatile.Write(ref _lastRefresh, value);
    }

    public CachedDtddClient(IDtddClient inner, IDtddCache cache, CachePolicy? policy = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);

        _inner = inner;
        _cache = cache;
        _policy = policy ?? CachePolicy.Default;

        // Accepted for constructor symmetry with InMemoryDtddCache/SqliteDtddCache (and so a future
        // caller-visible clock read has an obvious place to live), but intentionally unused today: staleness
        // is entirely owned by the IDtddCache implementation (CacheEntry.IsStale), and every DtddResult this
        // class returns reuses either the cache entry's FetchedAt or the inner client's own result.FetchedAt.
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

        // A positive imdb/tmdb/name→id mapping is treated as permanent: DtDD ids don't get reassigned to a
        // different item, so lookupEntry.IsStale is intentionally never consulted here. Only the resolved
        // item's own cache entry (below) has a meaningful freshness window.
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

            // Deliberately returned unchanged, Source and all: this was a genuine cache miss, so the inner
            // client's own Source (Live) is exactly right and needs no relabeling.
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
        // Lazy<Task>'s factory delegate itself must stay cheap and side-effect-free until forced: with
        // ExecutionAndPublication, GetOrAdd can hand back a Lazy<Task> it just constructed but that a
        // different thread "wins" and discards, so the factory passed to `new Lazy<Task>` runs under a lock
        // exactly once per key regardless — but only once something forces .Value. Task.Run defers that
        // forcing (and thus starting RunRefreshAsync) onto the thread pool rather than running it — and
        // returning its Task synchronously — inline on whichever caller's thread happened to win the race and
        // trigger the factory, keeping this method itself fire-and-forget rather than sometimes blocking the
        // caller on the first leg of the refresh.
        Lazy<Task> lazy = null!;
        lazy = _inFlightRefreshes.GetOrAdd(
            key,
            _ =>
            {
                Lazy<Task> l = null!;
                l = new Lazy<Task>(() => Task.Run(() => RunRefreshAsync(key, l, fetchInner, put)), LazyThreadSafetyMode.ExecutionAndPublication);
                return l;
            });
        LastRefresh = lazy.Value;
    }

    /// <summary>
    /// Executes a single background refresh: fetches from the inner client with <see cref="CancellationToken.None"/>
    /// (the caller's token, which triggered the stale read, must not cancel a refresh benefiting future callers),
    /// stores the result, and never faults — any exception, from the inner fetch/put *or* from a
    /// <see cref="RefreshFailed"/> subscriber, is swallowed rather than allowed to fault this task.
    /// </summary>
    private async Task RunRefreshAsync<T>(
        string key,
        Lazy<Task> self,
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
            // A subscriber that itself throws must not escape here: that would fault this Task.Run task,
            // which would in turn fault LastRefresh — breaking the "background refresh never faults"
            // contract for a failure that isn't even this class's own.
            try
            {
                RefreshFailed?.Invoke(ex);
            }
            catch
            {
                // Deliberately swallowed; see above.
            }
        }
        finally
        {
            // Remove only the exact (key, lazy) pair this refresh registered itself under, so a coalesced
            // refresh can never remove a *different*, newer in-flight entry that has since replaced it for
            // the same key (e.g. after this one already finished and a fresh stale read started another).
            _inFlightRefreshes.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, self));
        }
    }
}
