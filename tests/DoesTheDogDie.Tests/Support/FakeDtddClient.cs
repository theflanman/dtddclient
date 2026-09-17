using DoesTheDogDie.Api;

namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// An in-memory <see cref="IDtddClient"/> for exercising caching-decorator logic without a real inner client.
/// Records a short description of each call in <see cref="Calls"/>, always succeeds with configurable canned
/// values (unless <see cref="BeforeRespond"/> says otherwise), and stamps every result's <c>FetchedAt</c> with
/// <see cref="Now"/> and <see cref="ResultSource.Live"/>.
/// </summary>
internal sealed class FakeDtddClient : IDtddClient
{
    private readonly Lock _callsLock = new();
    private readonly List<string> _calls = [];

    /// <summary>
    /// Short descriptions of each call made, in order (e.g. "GetItem:10752", "Search:?imdb=tt123"). Calls can
    /// arrive from a background-refresh pool thread (<c>CachedDtddClient</c>'s fire-and-forget refreshes)
    /// concurrently with a test thread reading this property, so each read takes a lock-protected snapshot
    /// rather than exposing the backing list directly.
    /// </summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_callsLock)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>The rate-limit budget returned from <see cref="CurrentBudget"/>.</summary>
    public RateLimitStatus? CurrentBudget { get; set; }

    /// <summary>The timestamp stamped onto every returned <see cref="DtddResult{T}"/>.</summary>
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Optional hook invoked (and awaited) before each response is produced. If it returns a non-null
    /// exception, that exception is thrown instead of a normal response.
    /// </summary>
    public Func<string, Task<Exception?>>? BeforeRespond { get; set; }

    /// <summary>Factory for the <see cref="ItemDetail"/> returned by <see cref="GetItemAsync"/>, by id.</summary>
    public Func<int, ItemDetail> ItemToReturn { get; set; } = id => new ItemDetail
    {
        Id = id,
        Name = "Item " + id,
        ItemTypeId = 15,
        ItemTypeName = "Movie",
    };

    /// <summary>The items returned by <see cref="SearchItemsAsync"/>.</summary>
    public List<Item> SearchResult { get; set; } =
    [
        new Item { Id = 10752, Name = "Old Yeller", ItemTypeId = 15, ItemTypeName = "Movie" },
    ];

    /// <summary>The ratings returned by <see cref="GetRatingsAsync"/>.</summary>
    public List<Rating> RatingsResult { get; set; } = [];

    /// <summary>The topics returned by <see cref="GetTopicsAsync"/>.</summary>
    public List<Topic> TopicsResult { get; set; } =
    [
        new Topic { Id = 1, Name = "A dog dies", TopicCategoryId = 1 },
    ];

    /// <summary>The item types returned by <see cref="GetItemTypesAsync"/>.</summary>
    public List<ItemType> ItemTypesResult { get; set; } =
    [
        new ItemType { Id = 15, Name = "Movie", Slug = "movie", Verb = "watch", PastTenseVerb = "watched" },
    ];

    /// <summary>The topic categories returned by <see cref="GetTopicCategoriesAsync"/>.</summary>
    public List<TopicCategory> TopicCategoriesResult { get; set; } =
    [
        new TopicCategory { Id = 1, Name = "Animal Death", TopicSuperCategoryId = 1 },
    ];

    /// <summary>The topic super categories returned by <see cref="GetTopicSuperCategoriesAsync"/>.</summary>
    public List<TopicSuperCategory> TopicSuperCategoriesResult { get; set; } =
    [
        new TopicSuperCategory { Id = 1, Name = "Animals", ShortName = "Animals" },
    ];

    public async Task<DtddResult<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync($"Search:{search.ToQueryString()}").ConfigureAwait(false);
        return new DtddResult<IReadOnlyList<Item>>(SearchResult, ResultSource.Live, Now);
    }

    public async Task<DtddResult<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync($"GetItem:{itemId}").ConfigureAwait(false);
        return new DtddResult<ItemDetail>(ItemToReturn(itemId), ResultSource.Live, Now);
    }

    public async Task<DtddResult<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default)
    {
        var call = topicId is { } id ? $"Ratings:{itemId}:{id}" : $"Ratings:{itemId}";
        await RecordAndMaybeThrowAsync(call).ConfigureAwait(false);
        return new DtddResult<IReadOnlyList<Rating>>(RatingsResult, ResultSource.Live, Now);
    }

    public async Task<DtddResult<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync("Topics").ConfigureAwait(false);
        return new DtddResult<IReadOnlyList<Topic>>(TopicsResult, ResultSource.Live, Now);
    }

    public async Task<DtddResult<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync("ItemTypes").ConfigureAwait(false);
        return new DtddResult<IReadOnlyList<ItemType>>(ItemTypesResult, ResultSource.Live, Now);
    }

    public async Task<DtddResult<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync("TopicCategories").ConfigureAwait(false);
        return new DtddResult<IReadOnlyList<TopicCategory>>(TopicCategoriesResult, ResultSource.Live, Now);
    }

    public async Task<DtddResult<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync("TopicSuperCategories").ConfigureAwait(false);
        return new DtddResult<IReadOnlyList<TopicSuperCategory>>(TopicSuperCategoriesResult, ResultSource.Live, Now);
    }

    /// <summary>Clears <see cref="Calls"/>, e.g. to isolate assertions to calls made after some setup phase.</summary>
    public void ClearCalls()
    {
        lock (_callsLock)
        {
            _calls.Clear();
        }
    }

    private async Task RecordAndMaybeThrowAsync(string call)
    {
        lock (_callsLock)
        {
            _calls.Add(call);
        }

        if (BeforeRespond is { } hook)
        {
            var exception = await hook(call).ConfigureAwait(false);
            if (exception is not null)
            {
                throw exception;
            }
        }
    }
}
