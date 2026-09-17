using DoesTheDogDie.Api;

namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// An in-memory <see cref="IDtddApiClient"/> for exercising queue/cache/throttle logic without hitting the
/// network. Records a short description of each call in <see cref="Calls"/>, always succeeds with minimal
/// fixed values (unless <see cref="BeforeRespond"/> says otherwise), and stamps every response with
/// <see cref="NextRateLimit"/>.
/// </summary>
internal sealed class FakeApiClient : IDtddApiClient
{
    /// <summary>Short descriptions of each call made, in order (e.g. "GetItem:10752", "Search:?q=old%20yeller").</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The rate-limit snapshot attached to every successful response.</summary>
    public RateLimitStatus NextRateLimit { get; set; } = new(30, 30, 5000, 5000, DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Optional hook invoked (and awaited) before each response is produced. If it returns a non-null
    /// exception, that exception is thrown instead of a normal response. Useful for injecting
    /// <see cref="DtddMinuteRateLimitException"/>/<see cref="DtddMonthlyRateLimitException"/> on demand, or for
    /// awaiting a signal to test call ordering.
    /// </summary>
    public Func<string, CancellationToken, Task<Exception?>>? BeforeRespond { get; set; }

    public async Task<ApiResponse<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default)
    {
        var call = $"Search:{search.ToQueryString()}";
        await RecordAndMaybeThrowAsync(call, ct).ConfigureAwait(false);
        return Respond<IReadOnlyList<Item>>([]);
    }

    public async Task<ApiResponse<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        var call = $"GetItem:{itemId}";
        await RecordAndMaybeThrowAsync(call, ct).ConfigureAwait(false);
        return Respond(new ItemDetail
        {
            Id = itemId,
            Name = "Item " + itemId,
            ItemTypeId = 15,
            ItemTypeName = "Movie",
        });
    }

    public async Task<ApiResponse<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default)
    {
        var call = topicId is { } id ? $"Ratings:{itemId}:{id}" : $"Ratings:{itemId}";
        await RecordAndMaybeThrowAsync(call, ct).ConfigureAwait(false);
        return Respond<IReadOnlyList<Rating>>([]);
    }

    public async Task<ApiResponse<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync("Topics", ct).ConfigureAwait(false);
        return Respond<IReadOnlyList<Topic>>([]);
    }

    public async Task<ApiResponse<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync("ItemTypes", ct).ConfigureAwait(false);
        return Respond<IReadOnlyList<ItemType>>([]);
    }

    public async Task<ApiResponse<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync("TopicCategories", ct).ConfigureAwait(false);
        return Respond<IReadOnlyList<TopicCategory>>([]);
    }

    public async Task<ApiResponse<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default)
    {
        await RecordAndMaybeThrowAsync("TopicSuperCategories", ct).ConfigureAwait(false);
        return Respond<IReadOnlyList<TopicSuperCategory>>([]);
    }

    private async Task RecordAndMaybeThrowAsync(string call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(call);
        if (BeforeRespond is { } hook)
        {
            var exception = await hook(call, ct).ConfigureAwait(false);
            if (exception is not null)
            {
                throw exception;
            }
        }
    }

    private ApiResponse<T> Respond<T>(T value) => new(value, NextRateLimit);
}
