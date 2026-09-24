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

    /// <summary>
    /// Compares two <see cref="ItemDetail"/> values field by field rather than via record equality.
    /// <see cref="ItemDetail"/>'s (and <see cref="Item"/>'s) compiler-synthesized <c>Equals</c> compares
    /// <see cref="Item.Genres"/>/<see cref="ItemDetail.TopicItemStats"/> by reference, which a persistent
    /// cache implementation can never satisfy since it always materializes a fresh list. xUnit's
    /// <see cref="Assert.Equal{T}(T, T)"/> compares both lists element-wise and in order here, because their
    /// static type at the call site is <see cref="IReadOnlyList{T}"/> rather than a type with its own
    /// <c>Equals</c> override; <see cref="TopicItemStat"/> is a sealed record with no collection members, so
    /// its synthesized equality is trustworthy per element.
    /// </summary>
    /// <remarks>
    /// Covers every member of <see cref="Item"/> and <see cref="ItemDetail"/>. A relational cache maps each
    /// one to its own column, so a member missing from this list is a member whose loss no test would catch
    /// - extend it whenever the model grows.
    /// </remarks>
    protected static void AssertItemEquivalent(ItemDetail expected, ItemDetail actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Genres, actual.Genres);
        Assert.Equal(expected.ReleaseYear, actual.ReleaseYear);
        Assert.Equal(expected.ItemTypeId, actual.ItemTypeId);
        Assert.Equal(expected.ItemTypeName, actual.ItemTypeName);
        Assert.Equal(expected.TmdbId, actual.TmdbId);
        Assert.Equal(expected.ImdbId, actual.ImdbId);
        Assert.Equal(expected.BackgroundImage, actual.BackgroundImage);
        Assert.Equal(expected.PosterImage, actual.PosterImage);
        Assert.Equal(expected.Overview, actual.Overview);
        Assert.Equal(expected.TopicItemStats, actual.TopicItemStats);
    }

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

    // Fixtures below follow two rules, because a relational cache maps every member to its own column:
    //
    //  - Each list pairs a fully populated record with a sparse one whose nullable members are all null.
    //    The full one catches a dropped or mis-mapped column; the sparse one catches NULL read back as 0/"".
    //  - Each list is deliberately OUT of key order. A query that reads child rows by primary key instead of
    //    by insertion ordinal returns them sorted by id, and only an unsorted fixture can tell the difference.

    protected static ItemDetail MakeFullItem() => new()
    {
        Id = 10752,
        Name = "Old Yeller",
        Genres = ["Western", "Drama", "Family"],
        ReleaseYear = 1957,
        ItemTypeId = 15,
        ItemTypeName = "Movie",
        TmdbId = 15081,
        ImdbId = "tt0050798",
        BackgroundImage = "https://example.test/old-yeller/bg.jpg",
        PosterImage = "https://example.test/old-yeller/poster.jpg",
        Overview = "A boy and his dog on the Texas frontier.",
        TopicItemStats =
        [
            new TopicItemStat { TopicItemId = 900, YesSum = 120, NoSum = 3, NumComments = 14, TopicId = 153, TopicName = "a dog dies", ItemId = 10752 },
            new TopicItemStat { TopicItemId = 700, YesSum = 2, NoSum = 40, NumComments = 1, TopicId = 12, TopicName = "a cat dies", ItemId = 10752 },
            new TopicItemStat { TopicItemId = 800, YesSum = 9, NoSum = 9, NumComments = 0, TopicId = 77, TopicName = "someone is sick", ItemId = 10752 },
        ],
    };

    protected static ItemDetail MakeSparseItem() => new()
    {
        Id = 20001,
        Name = "Untitled",
        ItemTypeId = 14,
        ItemTypeName = "Book",
    };

    protected static Rating MakeFullRating(int id) => new()
    {
        Id = id,
        Yes = 7,
        No = 2,
        VoteSum = 5,
        TriggerDescription = "The dog is shot offscreen.",
        IsRampant = true,
        Index1 = 2,
        Index2 = 11,
        Position1 = 1,
        Position2 = 42,
        Position3 = 17,
        SafePosition1 = 1,
        SafePosition2 = 45,
        SafePosition3 = 3,
        CueDescription = "After the barn scene.",
        ItemId = 10752,
        TopicId = 153,
        IsSceneAlert = true,
    };

    protected static Rating MakeSparseRating(int id) => new()
    {
        Id = id,
        Yes = 0,
        No = 1,
        VoteSum = -1,
        IsRampant = false,
        Index1 = -1,
        Index2 = -1,
        ItemId = 10752,
        TopicId = 12,
        IsSceneAlert = false,
    };

    protected static readonly Topic[] AllTopics =
    [
        new Topic
        {
            Id = 153,
            Name = "a dog dies",
            NotName = "no dogs die",
            Keywords = "dog, death, pet",
            Description = "A dog dies on screen or off.",
            DoesName = "does the dog die",
            ListName = "dogs dying",
            MinimalName = "dog dies",
            TopicCategoryId = 4,
            AltTopicCategoryId = 9,
        },
        new Topic { Id = 12, Name = "a cat dies", TopicCategoryId = 4 },
    ];

    protected static readonly ItemType[] AllItemTypes =
    [
        new ItemType
        {
            Id = 16,
            Name = "TV",
            Slug = "tv",
            Verb = "watch",
            PastTenseVerb = "watched",
            Index1Label = "Season",
            Index2Label = "Episode",
            Position1Label = "Hours",
            Position2Label = "Minutes",
            Position3Label = "Seconds",
        },
        new ItemType { Id = 14, Name = "Book", Slug = "books", Verb = "read", PastTenseVerb = "read" },
    ];

    protected static readonly TopicCategory[] AllTopicCategories =
    [
        new TopicCategory { Id = 9, Name = "Animals", TopicSuperCategoryId = 2 },
        new TopicCategory { Id = 4, Name = "Death", TopicSuperCategoryId = 1 },
    ];

    protected static readonly TopicSuperCategory[] AllTopicSuperCategories =
    [
        new TopicSuperCategory { Id = 2, Name = "Content Warnings", ShortName = "Content" },
        new TopicSuperCategory { Id = 1, Name = "Sensory", ShortName = "Sense" },
    ];

    [Fact]
    public async Task Item_RoundTrip()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        var item = MakeItem();

        await cache.PutItemAsync(item, time.GetUtcNow());
        var entry = await cache.GetItemAsync(item.Id);

        Assert.NotNull(entry);
        AssertItemEquivalent(item, entry!.Value);
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
        AssertItemEquivalent(item, staleEntry.Value);
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

    [Fact]
    public async Task Item_RoundTrip_IsExact()
    {
        // Breaks: a column dropped or mis-mapped (full item); NULL read back as 0/"" (sparse item);
        // genres or stats read back by key order instead of stored order (both lists are unsorted).
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        var full = MakeFullItem();
        var sparse = MakeSparseItem();

        await cache.PutItemAsync(full, time.GetUtcNow());
        await cache.PutItemAsync(sparse, time.GetUtcNow());
        var fullEntry = await cache.GetItemAsync(full.Id);
        var sparseEntry = await cache.GetItemAsync(sparse.Id);

        Assert.NotNull(fullEntry);
        Assert.NotNull(sparseEntry);
        AssertItemEquivalent(full, fullEntry!.Value);
        AssertItemEquivalent(sparse, sparseEntry!.Value);
    }

    [Fact]
    public async Task Ratings_RoundTrip_IsExact()
    {
        // Breaks: a rating column dropped or mis-mapped; NULL read back as 0/""; rows read back by id order.
        // Ids 900 then 100 are deliberately descending.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        Rating[] ratings = [MakeFullRating(900), MakeSparseRating(100)];

        await cache.PutRatingsAsync(10752, null, ratings, time.GetUtcNow());
        var entry = await cache.GetRatingsAsync(10752, null);

        Assert.NotNull(entry);
        Assert.Equal(ratings, entry!.Value);
    }

    [Fact]
    public async Task Taxonomy_RoundTrip_IsExact()
    {
        // Breaks: any taxonomy column dropped or mis-mapped; NULL read back as 0/""; rows read by id order.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);

        await cache.PutTopicsAsync(AllTopics, time.GetUtcNow());
        await cache.PutItemTypesAsync(AllItemTypes, time.GetUtcNow());
        await cache.PutTopicCategoriesAsync(AllTopicCategories, time.GetUtcNow());
        await cache.PutTopicSuperCategoriesAsync(AllTopicSuperCategories, time.GetUtcNow());

        Assert.Equal(AllTopics, (await cache.GetTopicsAsync())?.Value);
        Assert.Equal(AllItemTypes, (await cache.GetItemTypesAsync())?.Value);
        Assert.Equal(AllTopicCategories, (await cache.GetTopicCategoriesAsync())?.Value);
        Assert.Equal(AllTopicSuperCategories, (await cache.GetTopicSuperCategoriesAsync())?.Value);
    }

    [Fact]
    public async Task Ratings_EmptyList_IsCachedNotMissing()
    {
        // Break: an empty list stored as zero rows with nothing marking that the fetch happened reads back as
        // null - a miss - so the caching client re-fetches it on every call, spending budget forever.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);

        await cache.PutRatingsAsync(10752, 153, [], time.GetUtcNow());
        var entry = await cache.GetRatingsAsync(10752, 153);

        Assert.NotNull(entry);
        Assert.Empty(entry!.Value);
        Assert.Equal(time.GetUtcNow(), entry.FetchedAt);
    }

    [Fact]
    public async Task Taxonomy_EmptyList_IsCachedNotMissing()
    {
        // Break: as Ratings_EmptyList_IsCachedNotMissing, per taxonomy table.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);

        await cache.PutTopicsAsync([], time.GetUtcNow());
        await cache.PutItemTypesAsync([], time.GetUtcNow());
        await cache.PutTopicCategoriesAsync([], time.GetUtcNow());
        await cache.PutTopicSuperCategoriesAsync([], time.GetUtcNow());

        var topics = await cache.GetTopicsAsync();
        var itemTypes = await cache.GetItemTypesAsync();
        var categories = await cache.GetTopicCategoriesAsync();
        var superCategories = await cache.GetTopicSuperCategoriesAsync();

        Assert.NotNull(topics);
        Assert.NotNull(itemTypes);
        Assert.NotNull(categories);
        Assert.NotNull(superCategories);
        Assert.Empty(topics!.Value);
        Assert.Empty(itemTypes!.Value);
        Assert.Empty(categories!.Value);
        Assert.Empty(superCategories!.Value);
    }

    [Fact]
    public async Task Item_PutFewerChildren_ReplacesThem()
    {
        // Break: re-putting an item upserts its child rows without deleting the ones no longer present, so
        // stats and genres from the earlier fetch leak into the later one.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        var before = MakeFullItem();
        var after = before with
        {
            Genres = ["Drama"],
            TopicItemStats = [before.TopicItemStats[1]],
        };

        await cache.PutItemAsync(before, time.GetUtcNow());
        await cache.PutItemAsync(after, time.GetUtcNow());
        var entry = await cache.GetItemAsync(before.Id);

        Assert.NotNull(entry);
        AssertItemEquivalent(after, entry!.Value);
    }

    [Fact]
    public async Task Ratings_PutShorterList_ReplacesRows()
    {
        // Break: re-putting a ratings set upserts rows without deleting the ones no longer present.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        Rating[] replacement = [MakeSparseRating(500)];

        await cache.PutRatingsAsync(10752, null, [MakeFullRating(900), MakeSparseRating(100)], time.GetUtcNow());
        await cache.PutRatingsAsync(10752, null, replacement, time.GetUtcNow());
        var entry = await cache.GetRatingsAsync(10752, null);

        Assert.NotNull(entry);
        Assert.Equal(replacement, entry!.Value);
    }

    [Fact]
    public async Task Taxonomy_PutShorterList_ReplacesRows()
    {
        // Break: re-putting a taxonomy set upserts rows without deleting the ones no longer present.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        Topic[] replacement = [AllTopics[1]];

        await cache.PutTopicsAsync(AllTopics, time.GetUtcNow());
        await cache.PutTopicsAsync(replacement, time.GetUtcNow());

        Assert.Equal(replacement, (await cache.GetTopicsAsync())?.Value);
    }

    [Fact]
    public async Task Ratings_SameRatingIdInTwoSets_BothRetained()
    {
        // Break: keying rating rows by rating id alone. The all-topics fetch and a per-topic fetch can return
        // the same rating, so the second put would overwrite or collide with the first set's row.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = CreateCache(Policy, time);
        Rating[] allTopics = [MakeFullRating(900), MakeSparseRating(100)];
        Rating[] topic153 = [MakeFullRating(900)];

        await cache.PutRatingsAsync(10752, null, allTopics, time.GetUtcNow());
        await cache.PutRatingsAsync(10752, 153, topic153, time.GetUtcNow());

        Assert.Equal(allTopics, (await cache.GetRatingsAsync(10752, null))?.Value);
        Assert.Equal(topic153, (await cache.GetRatingsAsync(10752, 153))?.Value);
    }
}
