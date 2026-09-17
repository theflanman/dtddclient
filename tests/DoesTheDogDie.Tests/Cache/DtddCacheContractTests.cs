using DoesTheDogDie.Api;
using DoesTheDogDie.Cache;
using Microsoft.Extensions.Time.Testing;

namespace DoesTheDogDie.Tests.Cache;

/// <summary>
/// A contract test suite that any <see cref="IDtddCache"/> implementation must satisfy. Concrete test
/// classes provide a fresh cache instance per test via <see cref="CreateCache"/>.
/// </summary>
public abstract class DtddCacheContractTests
{
    private static readonly CachePolicy Policy = new()
    {
        TaxonomyMaxAge = TimeSpan.FromMinutes(10),
        ItemMaxAge = TimeSpan.FromMinutes(30),
        RatingsMaxAge = TimeSpan.FromMinutes(30),
        NegativeLookupMaxAge = TimeSpan.FromMinutes(5),
    };

    protected abstract IDtddCache CreateCache(CachePolicy policy, TimeProvider time);

    private static ItemDetail MakeItem(int id = 1) => new()
    {
        Id = id,
        Name = "Halloween",
        ItemTypeId = 15,
        ItemTypeName = "Movie",
    };

    private static Rating MakeRating(int id, int itemId, int topicId) => new()
    {
        Id = id,
        Yes = 1,
        No = 0,
        VoteSum = 1,
        IsRampant = false,
        Index1 = -1,
        Index2 = -1,
        ItemId = itemId,
        TopicId = topicId,
        IsSceneAlert = false,
    };

    [Fact]
    public async Task Item_RoundTrip()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        var item = MakeItem();

        await cache.PutItemAsync(item, time.GetUtcNow());
        var entry = await cache.GetItemAsync(item.Id);

        Assert.NotNull(entry);
        Assert.Equal(item, entry!.Value);
        Assert.False(entry.IsStale);
    }

    [Fact]
    public async Task Item_Missing_IsNull()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);

        var entry = await cache.GetItemAsync(999);

        Assert.Null(entry);
    }

    [Fact]
    public async Task Item_StaleAfterMaxAge()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var time = new FakeTimeProvider(start);
        var cache = CreateCache(Policy, time);
        var item = MakeItem();

        await cache.PutItemAsync(item, time.GetUtcNow());

        time.SetUtcNow(start + Policy.ItemMaxAge - TimeSpan.FromSeconds(1));
        var freshEntry = await cache.GetItemAsync(item.Id);
        Assert.NotNull(freshEntry);
        Assert.False(freshEntry!.IsStale);

        time.SetUtcNow(start + Policy.ItemMaxAge + TimeSpan.FromSeconds(1));
        var staleEntry = await cache.GetItemAsync(item.Id);
        Assert.NotNull(staleEntry);
        Assert.True(staleEntry!.IsStale);
        Assert.Equal(item, staleEntry.Value);
    }

    [Fact]
    public async Task Ratings_KeyedByTopic()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        var allTopics = new[] { MakeRating(1, 1, 5) };
        var topic153 = new[] { MakeRating(2, 1, 153) };

        await cache.PutRatingsAsync(1, null, allTopics, time.GetUtcNow());
        await cache.PutRatingsAsync(1, 153, topic153, time.GetUtcNow());

        var allEntry = await cache.GetRatingsAsync(1, null);
        var topicEntry = await cache.GetRatingsAsync(1, 153);

        Assert.NotNull(allEntry);
        Assert.NotNull(topicEntry);
        Assert.Equal(allTopics, allEntry!.Value);
        Assert.Equal(topic153, topicEntry!.Value);
    }

    [Fact]
    public async Task Taxonomy_RoundTrip_AllFour()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);

        var topics = new[] { new Topic { Id = 1, Name = "A dog dies", TopicCategoryId = 1 } };
        var itemTypes = new[] { new ItemType { Id = 15, Name = "Movie", Slug = "movie", Verb = "watch", PastTenseVerb = "watched" } };
        var categories = new[] { new TopicCategory { Id = 1, Name = "Animals", TopicSuperCategoryId = 1 } };
        var superCategories = new[] { new TopicSuperCategory { Id = 1, Name = "Content", ShortName = "Content" } };

        await cache.PutTopicsAsync(topics, time.GetUtcNow());
        await cache.PutItemTypesAsync(itemTypes, time.GetUtcNow());
        await cache.PutTopicCategoriesAsync(categories, time.GetUtcNow());
        await cache.PutTopicSuperCategoriesAsync(superCategories, time.GetUtcNow());

        Assert.Equal(topics, (await cache.GetTopicsAsync())!.Value);
        Assert.Equal(itemTypes, (await cache.GetItemTypesAsync())!.Value);
        Assert.Equal(categories, (await cache.GetTopicCategoriesAsync())!.Value);
        Assert.Equal(superCategories, (await cache.GetTopicSuperCategoriesAsync())!.Value);
    }

    [Fact]
    public async Task Lookup_Negative_RoundTrip()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        var key = LookupKey.Imdb("tt9999999");

        await cache.PutLookupAsync(key, null, time.GetUtcNow());
        var entry = await cache.GetLookupAsync(key);

        Assert.NotNull(entry);
        Assert.Null(entry!.Value);
    }

    [Fact]
    public async Task Lookup_Missing_IsNull()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);

        var entry = await cache.GetLookupAsync(LookupKey.Imdb("tt0000000"));

        Assert.Null(entry);
    }

    [Fact]
    public async Task Lookup_StaleUsesNegativeAge()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var time = new FakeTimeProvider(start);
        var cache = CreateCache(Policy, time);
        var negativeKey = LookupKey.Imdb("tt9999999");
        var positiveKey = LookupKey.Imdb("tt0050798");

        await cache.PutLookupAsync(negativeKey, null, time.GetUtcNow());
        await cache.PutLookupAsync(positiveKey, 1, time.GetUtcNow());

        time.SetUtcNow(start + Policy.NegativeLookupMaxAge + TimeSpan.FromSeconds(1));

        var negativeEntry = await cache.GetLookupAsync(negativeKey);
        var positiveEntry = await cache.GetLookupAsync(positiveKey);

        Assert.NotNull(negativeEntry);
        Assert.True(negativeEntry!.IsStale);

        Assert.NotNull(positiveEntry);
        Assert.False(positiveEntry!.IsStale);
    }

    [Fact]
    public async Task Put_ReplacesExisting()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        var original = MakeItem() with { Name = "Original" };
        var updated = MakeItem() with { Name = "Updated" };

        await cache.PutItemAsync(original, time.GetUtcNow());
        await cache.PutItemAsync(updated, time.GetUtcNow());

        var entry = await cache.GetItemAsync(original.Id);

        Assert.NotNull(entry);
        Assert.Equal("Updated", entry!.Value.Name);
    }
}
