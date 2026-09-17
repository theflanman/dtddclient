using System.Collections.Concurrent;
using DoesTheDogDie.Api;

namespace DoesTheDogDie.Cache;

/// <summary>
/// A read-through caching decorator over an <see cref="IDtddClient"/>. A cache hit returns immediately; a
/// cache miss calls through to <paramref name="inner"/> (see constructor) and stores the result, coalesced
/// per key so concurrent misses only trigger one inner call. A stale entry is returned immediately (flagged
/// <see cref="ResultSource.StaleCache"/>), and — when <see cref="CachePolicy.RefreshStaleInBackground"/> is
/// set — a fire-and-forget refresh is started to repopulate the cache, likewise coalesced per key.
/// </summary>
/// <remarks>
/// <see cref="SearchItemsAsync"/>'s name/imdb/tmdb lookups are cached by their first match: once a lookup
/// resolves to an item id, that mapping is treated as permanent (DtDD ids are not reassigned to a different
/// item — see the remarks on that method), and later calls with the same search return a one-element
/// <c>Item</c> list sourced from the item's own cache entry rather than re-searching.
/// </remarks>
public sealed class CachedDtddClient : IDtddClient, IAsyncDisposable
{
    private readonly IDtddClient _inner;
    private readonly IDtddCache _cache;
    private readonly CachePolicy _policy;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlightRefreshes = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inFlightMisses = new();
    private Task? _lastRefresh;
    private int _disposedFlag;

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

    /// <param name="timeProvider">
    /// Accepted for constructor symmetry with InMemoryDtddCache/SqliteDtddCache, but currently unused: staleness
    /// is entirely owned by the IDtddCache implementation (CacheEntry.IsStale), and every DtddResult this class
    /// returns reuses either the cache entry's FetchedAt or the inner client's own result.FetchedAt.
    /// </param>
    public CachedDtddClient(IDtddClient inner, IDtddCache cache, CachePolicy? policy = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);

        _inner = inner;
        _cache = cache;
        _policy = policy ?? CachePolicy.Default;
    }

    /// <inheritdoc />
    public RateLimitStatus? CurrentBudget => _inner.CurrentBudget;

    private bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

    /// <summary>
    /// Searches for items. A free-text query is passed straight through, uncached. An exact lookup
    /// (imdb/tmdb/name) is cached by <em>first match</em>: once resolved to an item id, that mapping is treated
    /// as permanent, and this method thereafter returns a one-element list sourced from the item's own cache
    /// entry (see the class remarks) rather than re-searching.
    /// </summary>
    public async Task<DtddResult<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
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

            // CancellationToken.None: the response has already arrived (and cost real monthly budget), so the
            // caller cancelling from here on must not stop it from being persisted (I1).
            await _cache.PutLookupAsync(key, resolvedId, result.FetchedAt, CancellationToken.None).ConfigureAwait(false);
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

        // CancellationToken.None: see the comment on the miss branch above (I1).
        await _cache.PutItemAsync(itemResult.Value, itemResult.FetchedAt, CancellationToken.None).ConfigureAwait(false);
        return new DtddResult<IReadOnlyList<Item>>([itemResult.Value], ResultSource.Live, itemResult.FetchedAt);
    }

    /// <inheritdoc />
    public Task<DtddResult<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return ReadThroughAsync(
            $"item:{itemId}",
            c => _cache.GetItemAsync(itemId, c),
            c => _inner.GetItemAsync(itemId, c),
            (value, fetchedAt, c) => _cache.PutItemAsync(value, fetchedAt, c),
            ct);
    }

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return ReadThroughAsync(
            topicId is { } id ? $"ratings:{itemId}:{id}" : $"ratings:{itemId}",
            c => _cache.GetRatingsAsync(itemId, topicId, c),
            c => _inner.GetRatingsAsync(itemId, topicId, c),
            (value, fetchedAt, c) => _cache.PutRatingsAsync(itemId, topicId, value, fetchedAt, c),
            ct);
    }

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return ReadThroughAsync(
            "topics",
            c => _cache.GetTopicsAsync(c),
            c => _inner.GetTopicsAsync(c),
            (value, fetchedAt, c) => _cache.PutTopicsAsync(value, fetchedAt, c),
            ct);
    }

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return ReadThroughAsync(
            "itemTypes",
            c => _cache.GetItemTypesAsync(c),
            c => _inner.GetItemTypesAsync(c),
            (value, fetchedAt, c) => _cache.PutItemTypesAsync(value, fetchedAt, c),
            ct);
    }

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return ReadThroughAsync(
            "topicCategories",
            c => _cache.GetTopicCategoriesAsync(c),
            c => _inner.GetTopicCategoriesAsync(c),
            (value, fetchedAt, c) => _cache.PutTopicCategoriesAsync(value, fetchedAt, c),
            ct);
    }

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return ReadThroughAsync(
            "topicSuperCategories",
            c => _cache.GetTopicSuperCategoriesAsync(c),
            c => _inner.GetTopicSuperCategoriesAsync(c),
            (value, fetchedAt, c) => _cache.PutTopicSuperCategoriesAsync(value, fetchedAt, c),
            ct);
    }

    /// <summary>
    /// The generic read-through pattern shared by every getter except <see cref="SearchItemsAsync"/> (which
    /// has extra lookup-then-item indirection): cache miss calls inner and stores (coalesced per key, see
    /// <see cref="FetchMissAsync{T}"/>); a fresh hit is served from cache; a stale hit is served from cache
    /// immediately, optionally kicking off a coalesced background refresh.
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
            return await FetchMissAsync(key, fetchInner, put, ct).ConfigureAwait(false);
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
    /// Fetches a cache miss for <paramref name="key"/> and stores the result, coalescing concurrent misses for
    /// the same key into a single inner call (I2): the shared fetch runs with <see cref="CancellationToken.None"/>
    /// (it must not be torn down by whichever caller happens to have triggered it — other callers may still be
    /// waiting on it), and each caller's own await is independently cancellable via <see cref="Task.WaitAsync(CancellationToken)"/>.
    /// Unlike a background refresh, a miss is not fire-and-forget: an exception from the shared fetch propagates
    /// to every waiting caller.
    /// </summary>
    private Task<DtddResult<T>> FetchMissAsync<T>(
        string key,
        Func<CancellationToken, Task<DtddResult<T>>> fetchInner,
        Func<T, DateTimeOffset, CancellationToken, Task> put,
        CancellationToken ct)
    {
        var missKey = $"miss:{key}";

        Lazy<Task<object>> lazy = null!;
        lazy = _inFlightMisses.GetOrAdd(
            missKey,
            _ =>
            {
                Lazy<Task<object>> l = null!;
                l = new Lazy<Task<object>>(
                    () => Task.Run(() => RunMissAsync(missKey, l, fetchInner, put)),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                return l;
            });

        return AwaitMissAsync<T>(lazy.Value, ct);
    }

    private static async Task<DtddResult<T>> AwaitMissAsync<T>(Task<object> shared, CancellationToken ct)
    {
        var boxed = await shared.WaitAsync(ct).ConfigureAwait(false);
        return (DtddResult<T>)boxed;
    }

    /// <summary>
    /// Executes a single coalesced cache-miss fetch: fetches from the inner client, stores the result (both
    /// with <see cref="CancellationToken.None"/> — see <see cref="FetchMissAsync{T}"/>), and returns it boxed
    /// for <see cref="AwaitMissAsync{T}"/> to unwrap. Unlike <see cref="RunRefreshAsync{T}"/>, exceptions are
    /// deliberately NOT swallowed: they must propagate to every caller awaiting this miss.
    /// </summary>
    private async Task<object> RunMissAsync<T>(
        string key,
        Lazy<Task<object>> self,
        Func<CancellationToken, Task<DtddResult<T>>> fetchInner,
        Func<T, DateTimeOffset, CancellationToken, Task> put)
    {
        try
        {
            var result = await fetchInner(CancellationToken.None).ConfigureAwait(false);
            await put(result.Value, result.FetchedAt, CancellationToken.None).ConfigureAwait(false);

            // Deliberately returned unchanged, Source and all: this was a genuine cache miss, so the inner
            // client's own Source (Live) is exactly right and needs no relabeling.
            return result;
        }
        finally
        {
            // Remove only the exact (key, lazy) pair this fetch registered itself under, so it can never
            // remove a *different*, newer in-flight entry that has since replaced it for the same key (see the
            // identical concern in RunRefreshAsync).
            _inFlightMisses.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, self));
        }
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

    /// <summary>
    /// Marks this instance disposed (subsequent public calls throw <see cref="ObjectDisposedException"/>) and
    /// awaits every in-flight background refresh and coalesced miss fetch, so that no such task outlives this
    /// call. Exceptions from those tasks are swallowed: a refresh never faults by contract (see
    /// <see cref="RunRefreshAsync{T}"/>), and a miss's fault is already propagating to its own caller(s) via
    /// <see cref="AwaitMissAsync{T}"/> — disposal must not surface it a second time. Does NOT dispose
    /// <c>inner</c> (passed to the constructor): ownership of the inner <see cref="IDtddClient"/> stays with
    /// whoever constructed this instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            return;
        }

        var pending = _inFlightRefreshes.Values.Select(l => l.Value)
            .Concat(_inFlightMisses.Values.Select(l => (Task)l.Value))
            .ToArray();

        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Deliberately swallowed; see the XML remarks above.
            }
        }
    }
}
