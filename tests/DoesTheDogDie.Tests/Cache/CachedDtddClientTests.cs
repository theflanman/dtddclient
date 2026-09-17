using DoesTheDogDie.Api;
using DoesTheDogDie.Cache;
using DoesTheDogDie.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DoesTheDogDie.Tests.Cache;

public class CachedDtddClientTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly CachePolicy ShortPolicy = new()
    {
        TaxonomyMaxAge = TimeSpan.FromMinutes(10),
        ItemMaxAge = TimeSpan.FromMinutes(10),
        RatingsMaxAge = TimeSpan.FromMinutes(10),
        NegativeLookupMaxAge = TimeSpan.FromMinutes(10),
    };

    private static (FakeDtddClient Inner, InMemoryDtddCache Cache, FakeTimeProvider Time, CachedDtddClient Client) CreateSut(
        bool refreshStaleInBackground = true)
    {
        var time = new FakeTimeProvider(StartTime);
        var inner = new FakeDtddClient { Now = StartTime };
        var policy = new CachePolicy
        {
            TaxonomyMaxAge = ShortPolicy.TaxonomyMaxAge,
            ItemMaxAge = ShortPolicy.ItemMaxAge,
            RatingsMaxAge = ShortPolicy.RatingsMaxAge,
            NegativeLookupMaxAge = ShortPolicy.NegativeLookupMaxAge,
            RefreshStaleInBackground = refreshStaleInBackground,
        };
        var cache = new InMemoryDtddCache(policy, time);
        var client = new CachedDtddClient(inner, cache, policy, time);
        return (inner, cache, time, client);
    }

    [Fact]
    public async Task Miss_CallsInnerAndStores()
    {
        var (inner, cache, _, client) = CreateSut();

        var result = await client.GetItemAsync(10752);

        Assert.Equal(ResultSource.Live, result.Source);
        Assert.Equal(["GetItem:10752"], inner.Calls);

        var cached = await cache.GetItemAsync(10752);
        Assert.NotNull(cached);
        Assert.Equal(10752, cached!.Value.Id);
    }

    [Fact]
    public async Task FreshHit_ReturnsCacheWithoutInner()
    {
        var (inner, _, _, client) = CreateSut();

        await client.GetItemAsync(10752);
        var result = await client.GetItemAsync(10752);

        Assert.Equal(ResultSource.Cache, result.Source);
        Assert.Equal(["GetItem:10752"], inner.Calls);
    }

    [Fact]
    public async Task StaleHit_ReturnsStaleCacheAndRefreshes()
    {
        var (inner, _, time, client) = CreateSut();

        await client.GetItemAsync(10752);
        time.Advance(TimeSpan.FromMinutes(11));
        inner.Now = time.GetUtcNow();

        // Gate the inner call so the background refresh cannot complete (and mutate inner.Calls from a pool
        // thread) until this test says so — otherwise the assertion right after the stale read races the
        // fire-and-forget refresh.
        var gate = new TaskCompletionSource();
        inner.BeforeRespond = async _ =>
        {
            await gate.Task;
            return null;
        };

        var stale = await client.GetItemAsync(10752);

        Assert.Equal(ResultSource.StaleCache, stale.Source);
        Assert.Equal(["GetItem:10752"], inner.Calls);
        Assert.NotNull(client.LastRefresh);

        gate.SetResult();
        await client.LastRefresh!;

        Assert.Equal(["GetItem:10752", "GetItem:10752"], inner.Calls);

        var freshAgain = await client.GetItemAsync(10752);
        Assert.Equal(ResultSource.Cache, freshAgain.Source);
        Assert.Equal(["GetItem:10752", "GetItem:10752"], inner.Calls);
    }

    [Fact]
    public async Task StaleHit_NoBackgroundRefresh_WhenDisabled()
    {
        var (inner, _, time, client) = CreateSut(refreshStaleInBackground: false);

        await client.GetItemAsync(10752);
        time.Advance(TimeSpan.FromMinutes(11));

        var stale = await client.GetItemAsync(10752);

        Assert.Equal(ResultSource.StaleCache, stale.Source);
        Assert.Null(client.LastRefresh);
        Assert.Equal(["GetItem:10752"], inner.Calls);
    }

    [Fact]
    public async Task RefreshFailure_RaisesEvent()
    {
        var (inner, _, time, client) = CreateSut();

        await client.GetItemAsync(10752);
        time.Advance(TimeSpan.FromMinutes(11));

        var thrown = new InvalidOperationException("boom");
        inner.BeforeRespond = _ => Task.FromResult<Exception?>(thrown);

        Exception? observed = null;
        client.RefreshFailed += ex => observed = ex;

        await client.GetItemAsync(10752);
        Assert.NotNull(client.LastRefresh);
        await client.LastRefresh!;

        Assert.Same(thrown, observed);
    }

    [Fact]
    public async Task RefreshFailure_ThrowingSubscriber_DoesNotFaultLastRefresh()
    {
        var (inner, _, time, client) = CreateSut();

        await client.GetItemAsync(10752);
        time.Advance(TimeSpan.FromMinutes(11));

        inner.BeforeRespond = _ => Task.FromResult<Exception?>(new InvalidOperationException("boom"));
        client.RefreshFailed += _ => throw new InvalidOperationException("subscriber blew up");

        await client.GetItemAsync(10752);
        Assert.NotNull(client.LastRefresh);

        // Must not throw: a throwing RefreshFailed subscriber must not escape and fault the refresh task.
        await client.LastRefresh!;

        Assert.Equal(TaskStatus.RanToCompletion, client.LastRefresh!.Status);
    }

    [Fact]
    public async Task StaleHit_CoalescesConcurrentRefreshes()
    {
        var (inner, _, time, client) = CreateSut();

        await client.GetItemAsync(10752);
        time.Advance(TimeSpan.FromMinutes(11));

        var gate = new TaskCompletionSource();
        inner.BeforeRespond = async _ =>
        {
            await gate.Task;
            return null;
        };

        var first = await client.GetItemAsync(10752);
        var refresh1 = client.LastRefresh;
        var second = await client.GetItemAsync(10752);
        var refresh2 = client.LastRefresh;

        Assert.Equal(ResultSource.StaleCache, first.Source);
        Assert.Equal(ResultSource.StaleCache, second.Source);

        gate.SetResult();
        await refresh1!;
        if (refresh2 is not null)
        {
            await refresh2;
        }

        Assert.Equal(["GetItem:10752", "GetItem:10752"], inner.Calls);
    }

    [Fact]
    public async Task Search_Query_PassesThrough()
    {
        var (inner, _, _, client) = CreateSut();
        var search = ItemSearch.ByQuery("old yeller");

        // A free-text query has no exact key to cache by; nothing about it should ever be written to the cache.
        Assert.Null(LookupKey.FromSearch(search));

        var first = await client.SearchItemsAsync(search);
        var second = await client.SearchItemsAsync(search);

        Assert.Equal(ResultSource.Live, first.Source);
        Assert.Equal(ResultSource.Live, second.Source);
        Assert.Equal(2, inner.Calls.Count);
    }

    [Fact]
    public async Task Search_Imdb_NegativeHit_ReturnsEmptyFromCache()
    {
        var (inner, _, _, client) = CreateSut();
        inner.SearchResult = [];
        var search = ItemSearch.ByImdbId("tt000");

        var first = await client.SearchItemsAsync(search);
        Assert.Empty(first.Value);
        Assert.Equal(ResultSource.Live, first.Source);
        Assert.Single(inner.Calls);

        var second = await client.SearchItemsAsync(search);
        Assert.Empty(second.Value);
        Assert.Equal(ResultSource.Cache, second.Source);
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task Search_Imdb_PositiveHit_ServesFromItemCache()
    {
        var (inner, _, _, client) = CreateSut();
        var search = ItemSearch.ByImdbId("tt123");
        inner.SearchResult = [new Item { Id = 10752, Name = "Old Yeller", ItemTypeId = 15, ItemTypeName = "Movie" }];

        await client.SearchItemsAsync(search);
        await client.GetItemAsync(10752);
        inner.ClearCalls();

        var result = await client.SearchItemsAsync(search);

        Assert.Empty(inner.Calls);
        Assert.Equal(ResultSource.Cache, result.Source);
        Assert.Single(result.Value);
        Assert.Equal(10752, result.Value[0].Id);
    }

    [Fact]
    public async Task Search_Imdb_Miss_StoresLookup()
    {
        var (inner, cache, _, client) = CreateSut();
        var search = ItemSearch.ByImdbId("tt123");

        var searchResult = await client.SearchItemsAsync(search);
        Assert.Equal(ResultSource.Live, searchResult.Source);
        Assert.Equal(["Search:?imdb=tt123"], inner.Calls);

        var lookupEntry = await cache.GetLookupAsync(LookupKey.Imdb("tt123"));
        Assert.NotNull(lookupEntry);
        Assert.Equal(10752, lookupEntry!.Value);

        var itemResult = await client.GetItemAsync(10752);
        Assert.Equal(ResultSource.Live, itemResult.Source);
        Assert.Equal(["Search:?imdb=tt123", "GetItem:10752"], inner.Calls);

        var itemEntry = await cache.GetItemAsync(10752);
        Assert.NotNull(itemEntry);
    }

    [Fact]
    public async Task Ratings_CachedPerTopic()
    {
        var (inner, _, _, client) = CreateSut();

        await client.GetRatingsAsync(10752, topicId: 1);
        await client.GetRatingsAsync(10752, topicId: 2);
        var cachedAgain = await client.GetRatingsAsync(10752, topicId: 1);

        Assert.Equal(["Ratings:10752:1", "Ratings:10752:2"], inner.Calls);
        Assert.Equal(ResultSource.Cache, cachedAgain.Source);
    }

    [Fact]
    public async Task Taxonomy_Cached()
    {
        var (inner, _, _, client) = CreateSut();

        var first = await client.GetTopicsAsync();
        var second = await client.GetTopicsAsync();

        Assert.Equal(ResultSource.Live, first.Source);
        Assert.Equal(ResultSource.Cache, second.Source);
        Assert.Equal(["Topics"], inner.Calls);
    }

    [Fact]
    public void CurrentBudget_Delegates()
    {
        var (inner, _, _, client) = CreateSut();
        var budget = new RateLimitStatus(30, 25, 5000, 4900, StartTime);
        inner.CurrentBudget = budget;

        Assert.Equal(budget, client.CurrentBudget);
    }

    [Fact]
    public async Task Miss_StoresEvenIfCallerCancelsAfterResponse()
    {
        // I1 regression test: once the inner call has returned a response, the cache write must not be
        // cancellable by the caller's own token -- otherwise a caller that cancels right after receiving its
        // result (a common pattern: "I have what I need, tear down") silently discards a response that cost
        // real monthly budget to fetch. ThrowingCtCache throws if it is ever handed a cancelled token, so this
        // test fails loudly (rather than just "the cache is empty") if the bug regresses.
        //
        // Uses SearchItemsAsync's direct item-fetch branch (lookup already resolved to an id, but the item
        // itself not yet cached) rather than GetItemAsync: that branch fetches+stores the item outright, so
        // this test is not entangled with the coalesced-miss cancellation semantics of ReadThroughAsync/I2
        // (where WaitAsync(ct) legitimately surfaces the caller's own cancellation).
        var time = new FakeTimeProvider(StartTime);
        var inner = new FakeDtddClient { Now = StartTime };
        var cache = new ThrowingCtCache(new InMemoryDtddCache(ShortPolicy, time));
        var client = new CachedDtddClient(inner, cache, ShortPolicy, time);

        var search = ItemSearch.ByImdbId("tt123");
        inner.SearchResult = [new Item { Id = 10752, Name = "Old Yeller", ItemTypeId = 15, ItemTypeName = "Movie" }];

        // Seed a positive lookup without caching the item itself.
        await client.SearchItemsAsync(search);
        inner.ClearCalls();

        using var cts = new CancellationTokenSource();
        inner.BeforeRespond = _ =>
        {
            cts.Cancel();
            return Task.FromResult<Exception?>(null);
        };

        var result = await client.SearchItemsAsync(search, cts.Token);

        Assert.Equal(ResultSource.Live, result.Source);
        Assert.Single(result.Value);
        Assert.Equal(10752, result.Value[0].Id);

        var itemEntry = await cache.GetItemAsync(10752);
        Assert.NotNull(itemEntry);
    }

    [Fact]
    public async Task Miss_CoalescesConcurrentFetches()
    {
        // I2 regression test: two concurrent misses for the same key must coalesce into a single inner call,
        // the same way concurrent stale-refreshes already do.
        var (inner, _, _, client) = CreateSut();

        var gate = new TaskCompletionSource();
        inner.BeforeRespond = async _ =>
        {
            await gate.Task;
            return null;
        };

        var first = client.GetItemAsync(10752);
        var second = client.GetItemAsync(10752);

        gate.SetResult();

        var firstResult = await first;
        var secondResult = await second;

        Assert.Equal(ResultSource.Live, firstResult.Source);
        Assert.Equal(ResultSource.Live, secondResult.Source);
        Assert.Equal(["GetItem:10752"], inner.Calls);
    }

    [Fact]
    public async Task DisposeAsync_AwaitsInFlightRefresh()
    {
        // I3 regression test: disposal must not race an in-flight background refresh -- awaiting DisposeAsync
        // must guarantee the refresh (and thus its cache write) has actually finished, not merely been started.
        var (inner, cache, time, client) = CreateSut();

        await client.GetItemAsync(10752);
        time.Advance(TimeSpan.FromMinutes(11));
        inner.Now = time.GetUtcNow();

        var gate = new TaskCompletionSource();
        inner.BeforeRespond = async _ =>
        {
            await gate.Task;
            return null;
        };

        var stale = await client.GetItemAsync(10752);
        Assert.Equal(ResultSource.StaleCache, stale.Source);
        Assert.NotNull(client.LastRefresh);

        var disposeTask = client.DisposeAsync().AsTask();

        // DisposeAsync must actually be waiting on the in-flight refresh, not racing ahead of it.
        Assert.False(disposeTask.IsCompleted);

        gate.SetResult();
        await disposeTask;

        var entry = await cache.GetItemAsync(10752);
        Assert.NotNull(entry);
        Assert.False(entry!.IsStale);

        // GetItemAsync throws synchronously (before returning a Task) once disposed, so this is a plain
        // try/catch rather than Assert.Throws(Async) -- see the same pattern in ThrottledDtddClientTests.
        ObjectDisposedException? thrown = null;
        try
        {
            _ = client.GetItemAsync(10752);
        }
        catch (ObjectDisposedException ex)
        {
            thrown = ex;
        }

        Assert.NotNull(thrown);
    }

    [Fact]
    public async Task Search_Imdb_NegativeStale_RefreshesLookup()
    {
        var (inner, cache, time, client) = CreateSut();
        inner.SearchResult = [];
        var search = ItemSearch.ByImdbId("tt000");

        await client.SearchItemsAsync(search);
        time.Advance(TimeSpan.FromMinutes(11));
        inner.Now = time.GetUtcNow();

        var gate = new TaskCompletionSource();
        inner.BeforeRespond = async _ =>
        {
            await gate.Task;
            return null;
        };

        var stale = await client.SearchItemsAsync(search);

        Assert.Equal(ResultSource.StaleCache, stale.Source);
        Assert.Empty(stale.Value);
        Assert.Single(inner.Calls);
        Assert.NotNull(client.LastRefresh);

        gate.SetResult();
        await client.LastRefresh!;

        Assert.Equal(2, inner.Calls.Count);

        var lookupEntry = await cache.GetLookupAsync(LookupKey.Imdb("tt000"));
        Assert.NotNull(lookupEntry);
        Assert.Null(lookupEntry!.Value);
        Assert.False(lookupEntry.IsStale);
        Assert.Equal(time.GetUtcNow(), lookupEntry.FetchedAt);
    }

    [Fact]
    public async Task Search_Imdb_PositiveStaleItem_RefreshesItem()
    {
        var (inner, cache, time, client) = CreateSut();
        var search = ItemSearch.ByImdbId("tt123");
        inner.SearchResult = [new Item { Id = 10752, Name = "Old Yeller", ItemTypeId = 15, ItemTypeName = "Movie" }];

        await client.SearchItemsAsync(search);
        await client.GetItemAsync(10752);
        inner.ClearCalls();

        time.Advance(TimeSpan.FromMinutes(11));
        inner.Now = time.GetUtcNow();

        var gate = new TaskCompletionSource();
        inner.BeforeRespond = async _ =>
        {
            await gate.Task;
            return null;
        };

        var stale = await client.SearchItemsAsync(search);

        Assert.Equal(ResultSource.StaleCache, stale.Source);
        Assert.Single(stale.Value);
        Assert.Equal(10752, stale.Value[0].Id);
        Assert.NotNull(client.LastRefresh);

        gate.SetResult();
        await client.LastRefresh!;

        // The refresh re-fetches the item detail: the imdb->id lookup itself is treated as permanent and is
        // never re-queried.
        Assert.Equal(["GetItem:10752"], inner.Calls);

        var itemEntry = await cache.GetItemAsync(10752);
        Assert.NotNull(itemEntry);
        Assert.False(itemEntry!.IsStale);
    }
}
