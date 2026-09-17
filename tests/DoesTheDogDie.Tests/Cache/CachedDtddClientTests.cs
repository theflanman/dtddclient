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

        var stale = await client.GetItemAsync(10752);

        Assert.Equal(ResultSource.StaleCache, stale.Source);
        Assert.Equal(["GetItem:10752"], inner.Calls);
        Assert.NotNull(client.LastRefresh);

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

        await client.SearchItemsAsync(search);
        await client.SearchItemsAsync(search);

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
        inner.Calls.Clear();

        var result = await client.SearchItemsAsync(search);

        Assert.Empty(inner.Calls);
        Assert.Equal(ResultSource.Cache, result.Source);
        Assert.Single(result.Value);
        Assert.Equal(10752, result.Value[0].Id);
    }

    [Fact]
    public async Task Search_Imdb_Miss_StoresLookupAndItem()
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
        inner.CurrentBudget = new RateLimitStatus(30, 25, 5000, 4900, StartTime);

        Assert.Equal(inner.CurrentBudget, client.CurrentBudget);
    }
}
