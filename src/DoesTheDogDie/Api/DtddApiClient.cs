using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DoesTheDogDie.Api;

/// <summary>
/// A thin, one-to-one <see cref="IDtddApiClient"/> implementation over <see cref="HttpClient"/>.
/// Does not read or set <see cref="HttpClient.BaseAddress"/> or <see cref="HttpClient.DefaultRequestHeaders"/>,
/// so a single <see cref="HttpClient"/> instance can safely be shared with unrelated callers.
/// </summary>
public sealed class DtddApiClient : IDtddApiClient, IBudgetIdentity
{
    private const string ApiKeyHeader = "X-API-KEY";

    private readonly HttpClient _httpClient;
    private readonly DtddApiOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _budgetId;

    public DtddApiClient(HttpClient httpClient, DtddApiOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("API key must not be empty.", nameof(options));
        }

        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var baseAddress = options.BaseAddress;
        if (!baseAddress.AbsoluteUri.EndsWith('/'))
        {
            baseAddress = new Uri(baseAddress.AbsoluteUri + "/");
        }

        _options = new DtddApiOptions { ApiKey = options.ApiKey, BaseAddress = baseAddress };
        _budgetId = ComputeBudgetId(baseAddress, options.ApiKey);
    }

    string IBudgetIdentity.BudgetId => _budgetId;

    public async Task<ApiResponse<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var response = await GetAsync($"items{search.ToQueryString()}", DtddJsonContext.Default.ListItem, ct)
            .ConfigureAwait(false);
        return ToReadOnly(response);
    }

    public async Task<ApiResponse<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        return await GetAsync($"items/{itemId}", DtddJsonContext.Default.ItemDetail, ct).ConfigureAwait(false);
    }

    public async Task<ApiResponse<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default)
    {
        var relative = topicId is { } id
            ? $"items/{itemId}/ratings?topicId={id}"
            : $"items/{itemId}/ratings";
        var response = await GetAsync(relative, DtddJsonContext.Default.ListRating, ct).ConfigureAwait(false);
        return ToReadOnly(response);
    }

    public async Task<ApiResponse<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default)
    {
        var response = await GetAsync("topics", DtddJsonContext.Default.ListTopic, ct).ConfigureAwait(false);
        return ToReadOnly(response);
    }

    public async Task<ApiResponse<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default)
    {
        var response = await GetAsync("itemtypes", DtddJsonContext.Default.ListItemType, ct).ConfigureAwait(false);
        return ToReadOnly(response);
    }

    public async Task<ApiResponse<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default)
    {
        var response = await GetAsync("topiccategories", DtddJsonContext.Default.ListTopicCategory, ct).ConfigureAwait(false);
        return ToReadOnly(response);
    }

    public async Task<ApiResponse<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default)
    {
        var response = await GetAsync("topicsupercategories", DtddJsonContext.Default.ListTopicSuperCategory, ct).ConfigureAwait(false);
        return ToReadOnly(response);
    }

    private static ApiResponse<IReadOnlyList<T>> ToReadOnly<T>(ApiResponse<List<T>> response) =>
        new(response.Value, response.RateLimit);

    private async Task<ApiResponse<T>> GetAsync<T>(string relativeUri, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var uri = new Uri(_options.BaseAddress, relativeUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(ApiKeyHeader, _options.ApiKey);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var rateLimit = RateLimitStatus.FromHeaders(response.Headers, now);

        if (!response.IsSuccessStatusCode)
        {
            ApiError? error = null;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    error = JsonSerializer.Deserialize(body, DtddJsonContext.Default.ApiError);
                }
                catch (JsonException)
                {
                    error = null;
                }
            }

            var retryAfter = HeaderParsing.RetryAfter(response, now);
            throw ErrorMapper.Map(response.StatusCode, error, rateLimit, retryAfter);
        }

        var value = await response.Content.ReadFromJsonAsync(typeInfo, ct).ConfigureAwait(false);
        if (value is null)
        {
            throw new DtddApiException(response.StatusCode, null, "Empty response body", rateLimit);
        }

        return new ApiResponse<T>(value, rateLimit);
    }

    /// <summary>
    /// One budget per API key per server: the same key against a mock server (as end-to-end tests point
    /// <see cref="DtddApiOptions.BaseAddress"/> at) is a different budget from the real one. Hashed so the key never
    /// reaches the budget store; a URI cannot contain a newline, so the separator is unambiguous.
    /// </summary>
    private static string ComputeBudgetId(Uri baseAddress, string apiKey) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseAddress.AbsoluteUri + "\n" + apiKey)));
}
