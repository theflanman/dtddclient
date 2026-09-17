namespace DoesTheDogDie.Api;

/// <summary>
/// Configuration for <see cref="DtddApiClient"/>.
/// </summary>
public sealed class DtddApiOptions
{
    /// <summary>The DtDD API key, sent as the <c>X-API-KEY</c> header on every request.</summary>
    public required string ApiKey { get; init; }

    /// <summary>The base address of the DtDD API. Normalized to end with a trailing slash.</summary>
    public Uri BaseAddress { get; init; } = new("https://www.doesthedogdie.com/api/v3/");
}
